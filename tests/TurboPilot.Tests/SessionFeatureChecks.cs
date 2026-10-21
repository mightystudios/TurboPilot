using System.IO;
using GitHub.Copilot;
using TurboPilot.Ai;
using TurboPilot.Permissions;
using TurboPilot.Rendering;
using TurboPilot.Sessions;

namespace TurboPilot.Tests;

internal static class SessionFeatureChecks
{
	internal const string HandoffMarker = "HANDOFF_SUMMARY_SENTINEL";
	internal const string OpeningRequest = "Document README.md without changing code.";

	// Hand-off summaries are requested as one complete response in the runtime's tagged format.
	internal static LocalProvider.Reply HandoffReply(bool hold = false) => new(
		$"<analysis>Private working notes.</analysis>\n<summary>\n{HandoffMarker}: continue documenting README.md.\n</summary>", Hold: hold);

	public static async Task RunAsync()
	{
		using var workspace = new TestWorkspace();
		await using var provider = new LocalProvider();
		var options = new ChatSessionOptions
		{
			WorkspaceFolder = workspace.Workspace, Model = "test-model", UseByok = true,
			ByokEndpoint = provider.Endpoint, ContextWindowTokens = 32768,
		};
		CheckClassification(workspace, options);
		await CheckRenderedLinksAsync(workspace, provider, options);
		await CheckLiveChangesAsync(workspace, provider, options);
		await CheckHandoffAsync(workspace, provider, options);
		await CheckArchiveAsync(workspace, provider, options);
		await CheckAgenticLinksAsync(workspace, provider, options);
		await CheckPlanAsync(workspace, provider, options);
		Check.Equal(0, provider.Errors.Count, "The loopback provider must not hide failures: " + string.Join(" | ", provider.Errors));
		Console.WriteLine("PASS session features: Rendered links, live changes, and SDK hand-off restarts");
	}

	/// <summary>
	/// The plan checklist. An agent revises its plan repeatedly, so the
	/// card has to be written once and then corrected, not reprinted. In
	/// Rendered that means one card holding the latest state; Raw is a
	/// literal record of the stream and keeps the plan as first stated,
	/// which is the same asymmetry file links already have.
	/// </summary>
	private static async Task CheckPlanAsync(TestWorkspace workspace, LocalProvider provider, ChatSessionOptions options)
	{
		await using var chat = workspace.CreateChat();
		await chat.StartAsync(options);
		Check.True(chat.Progress is null, "A session with no plan reports no progress.");

		chat.ShowPlan([new("Reading the code", "in_progress"), new("Writing tests", "pending")]);
		Check.Equal("[1/2] Reading the code", chat.Progress!.StatusFragment, "Report the first step");
		Check.Equal(1, Occurrences(chat.RenderedTranscript, "kp-plan-steps"), "Write one plan card");
		Check.True(chat.Transcript.Contains("[>] Reading the code"), "Raw must carry the plan as an ASCII checklist.");

		chat.ShowPlan([new("Reading the code", "done"), new("Writing tests", "in_progress")]);
		Check.Equal("[2/2] Writing tests", chat.Progress!.StatusFragment, "Follow the agent to the next step");
		Check.Equal(1, Occurrences(chat.RenderedTranscript, "kp-plan-steps"), "Revise the card rather than repeat it");
		Check.True(chat.RenderedTranscript.Contains("kp-plan-running")
			&& !chat.RenderedTranscript.Contains(">[>] Reading the code"), "The revised card must show the current state.");

		var before = chat.RenderedTranscript;
		chat.ShowPlan([new("Reading the code", "done"), new("Writing tests", "in_progress")]);
		Check.Equal(before, chat.RenderedTranscript, "An unchanged plan must not touch the transcript");

		chat.ShowPlan([new("Reading the code", "done"), new("Writing tests", "done")]);
		Check.True(chat.Progress!.Complete && chat.Progress.StatusFragment.Length == 0,
			"A finished plan leaves the status line.");
		Console.WriteLine("PASS plan checklist written once, revised in place, and reported in the status line");

		static int Occurrences(string text, string value)
		{
			var count = 0;
			for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
				index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
				count++;
			return count;
		}
	}

	private static void CheckClassification(TestWorkspace workspace, ChatSessionOptions options)
	{
		Check.Equal(SessionChange.None, SessionChanges.Classify(options, options), "Unchanged settings change nothing");
		Check.Equal(SessionChange.Fresh, SessionChanges.Classify(options, options with { WorkspaceFolder = workspace.Root }), "Another workspace starts fresh");
		Check.Equal(SessionChange.Live, SessionChanges.Classify(options, options with { Model = "test-model-two" }), "Switch the model live");
		Check.Equal(SessionChange.Live, SessionChanges.Classify(options, options with { ReasoningEffort = "low" }), "Change the effort live");
		Check.Equal(SessionChange.Live, SessionChanges.Classify(options, options with { Mode = "Plan" }), "Change a built-in mode live");
		Check.Equal(SessionChange.Live, SessionChanges.Classify(options, options with { LinkFiles = false }), "Toggle file links live");
		Check.Equal(SessionChange.Restart, SessionChanges.Classify(options, options with { Mode = "Writer" }), "A custom agent needs a new session");
		Check.Equal(SessionChange.Restart, SessionChanges.Classify(options, options with { ApplyInstructions = false }), "Instruction loading needs a new session");
		Check.Equal(SessionChange.Restart, SessionChanges.Classify(options, options with { ByokEndpoint = "http://127.0.0.1:1/v1" }), "Another endpoint needs a new session");
		Check.Equal(SessionChange.Restart, SessionChanges.Classify(options, options with { Model = "test-model-two", ContextWindowTokens = 65536 }),
			"A BYOK context change needs a new session");
		var copilot = options with { UseByok = false, ByokEndpoint = "", ByokApiKey = "stale" };
		Check.Equal(SessionChange.Live, SessionChanges.Classify(copilot, copilot with { Model = "other", ContextWindowTokens = 1000, ByokApiKey = "" }),
			"Copilot sessions ignore BYOK credentials and change the context window live");
	}

	private static async Task CheckRenderedLinksAsync(TestWorkspace workspace, LocalProvider provider, ChatSessionOptions options)
	{
		var readme = workspace.Write("workspace\\README.md", "Fixture file.");
		var deep = workspace.Write("workspace\\src\\app\\Deep.cs", "Fixture source.");
		string id, rendered;
		await using (var chat = workspace.CreateChat())
		{
			await chat.StartAsync(options);
			id = chat.SessionId!;
			provider.Replies.Enqueue(new LocalProvider.Reply("See README.md and Deep.cs."));
			await RuntimeChecks.SendAndWaitAsync(chat, "Where is the code?");
			Check.True(chat.RenderedTranscript.Contains("kp-path:" + Uri.EscapeDataString(readme))
				&& chat.RenderedTranscript.Contains("kp-path:" + Uri.EscapeDataString(deep)),
				"Link files mentioned in a reply, including a bare name found in the workspace.");
			Check.True(chat.Transcript.Contains("See README.md and Deep.cs.") && !chat.Transcript.Contains("kp-path:"), "Keep Raw exactly as the model wrote it.");
			rendered = chat.RenderedTranscript;
			Check.Equal(rendered, workspace.Store.ReadRenderedTranscript(id), "Persist linked output separately");
		}
		await using (var resumed = workspace.CreateChat())
		{
			await resumed.ResumeAsync(id, options);
			Check.True(resumed.RenderedTranscript.StartsWith(rendered, StringComparison.Ordinal), "Restore linked output on resume.");
		}
		await using (var plain = workspace.CreateChat())
		{
			await plain.StartAsync(options with { LinkFiles = false });
			provider.Replies.Enqueue(new LocalProvider.Reply("See README.md."));
			await RuntimeChecks.SendAndWaitAsync(plain, "Where is it?");
			Check.True(!plain.RenderedTranscript.Contains("kp-path:"), "Leave Rendered unlinked when file links are off.");
		}
	}

	private static async Task CheckLiveChangesAsync(TestWorkspace workspace, LocalProvider provider, ChatSessionOptions options)
	{
		await using var chat = workspace.CreateChat();
		await chat.StartAsync(options);
		var id = chat.SessionId;
		provider.Replies.Enqueue(new LocalProvider.Reply("First answer."));
		await RuntimeChecks.SendAndWaitAsync(chat, "First question.");
		var changed = options with { Model = "test-model-two", Mode = "Plan", ReasoningEffort = "low" };
		Check.True(!await chat.ApplyLiveChangesAsync(changed), "Apply changes immediately while idle.");
		Check.Equal(SessionMode.Plan, await RuntimeChecks.GetSession(chat).Rpc.Mode.GetAsync(), "Switch the runtime mode");
		Check.True(!await chat.ApplyLiveChangesAsync(changed with { Mode = "Standard" }), "Return to Standard mode immediately.");
		Check.Equal(SessionMode.Interactive, await RuntimeChecks.GetSession(chat).Rpc.Mode.GetAsync(), "Return the runtime to interactive mode");
		Check.Equal(id, chat.SessionId, "Keep the session");
		provider.Replies.Enqueue(new LocalProvider.Reply("Second answer."));
		await RuntimeChecks.SendAndWaitAsync(chat, "Second question.");
		var request = provider.Requests.Last();
		Check.Equal("test-model-two", request.GetProperty("model").GetString(), "Send the next prompt to the switched model");
		Check.True(request.GetRawText().Contains("First answer."), "Keep the conversation across the switch.");
		Check.True(chat.Transcript.Contains("--- Changed to test-model-two | Plan ---"), "Record the change in the transcript.");
		var saved = workspace.Store.Load(id!).Options;
		Check.True(saved.Model == "test-model-two" && saved.ReasoningEffort == "low" && saved.Mode == "Standard", "Persist the changed settings.");
		await Check.ThrowsAsync<InvalidOperationException>(() => chat.ApplyLiveChangesAsync(changed with { Mode = "Writer" }));

		// A change requested during a turn must survive an interrupting prompt and a stop.
		string ModelFor(string prompt) => provider.Requests.Last(sent => sent.GetProperty("messages").EnumerateArray()
			.Last(message => message.GetProperty("role").GetString() == "user").GetProperty("content").ToString().Contains(prompt))
			.GetProperty("model").GetString()!;
		var slow = new LocalProvider.Reply("Slow answer.", Hold: true);
		provider.Replies.Enqueue(slow);
		await chat.SendAsync("Long question.");
		await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
		try
		{
			Check.True(await chat.ApplyLiveChangesAsync(changed with { Model = "test-model", Mode = "Standard" }), "Hold a change requested during a turn.");
			Check.Equal("test-model-two", chat.Model, "Keep showing the model that is running");
			provider.Replies.Enqueue(new LocalProvider.Reply("Interrupting answer."));
			await chat.SendAsync("Interrupting question.");
			await Check.UntilAsync(() => chat.Transcript.Contains("Interrupting answer.") && !chat.IsWorking, "The interrupting turn did not finish.");
		}
		finally { slow.Release.TrySetResult(); }
		Check.Equal("test-model", ModelFor("Interrupting question."), "Apply a held change before the interrupting prompt");
		Check.Equal("test-model", chat.Model, "Show the applied model");
		var stopped = new LocalProvider.Reply("Stopped answer.", Hold: true);
		provider.Replies.Enqueue(stopped);
		await chat.SendAsync("Another long question.");
		await stopped.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
		try
		{
			Check.True(await chat.ApplyLiveChangesAsync(changed with { Mode = "Standard" }), "Hold another change requested during a turn.");
			await chat.AbortAsync().WaitAsync(TimeSpan.FromSeconds(20));
		}
		finally { stopped.Release.TrySetResult(); }
		await Check.UntilAsync(() => chat.Model == "test-model-two", "Apply a held change once a stopped turn is idle.");
		Check.Equal("test-model-two", (await RuntimeChecks.GetSession(chat).Rpc.Model.GetCurrentAsync()).ModelId, "Switch the runtime model after the stop");

		// Returning to the running settings withdraws a change that is still waiting.
		var paused = new LocalProvider.Reply("Paused answer.", Hold: true);
		provider.Replies.Enqueue(paused);
		await chat.SendAsync("Pause question.");
		await paused.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
		var running = chat.Options;
		try
		{
			Check.True(await chat.ApplyLiveChangesAsync(running with { Model = "test-model" }), "Hold a change during the paused turn.");
			Check.Equal(SessionChange.Live, SessionChanges.Classify(chat.RequestedOptions, running), "Treat a return to the running settings as a live change");
			Check.True(!await chat.ApplyLiveChangesAsync(running) && chat.RequestedOptions.Model == running.Model, "Withdraw the waiting change.");
		}
		finally { paused.Release.TrySetResult(); }
		await Check.UntilAsync(() => !chat.IsWorking, "The paused turn did not finish.");
		await Task.Delay(250);
		Check.Equal(running.Model, (await RuntimeChecks.GetSession(chat).Rpc.Model.GetCurrentAsync()).ModelId, "Do not apply a withdrawn change");
	}

	private static async Task CheckHandoffAsync(TestWorkspace workspace, LocalProvider provider, ChatSessionOptions options)
	{
		SummaryBootstrap bootstrap;
		await using (var source = workspace.CreateChat())
		{
			await source.StartAsync(options);
			provider.Replies.Enqueue(new LocalProvider.Reply("Planned."));
			await RuntimeChecks.SendAndWaitAsync(source, OpeningRequest);
			provider.Replies.Enqueue(new LocalProvider.Reply("", ToolName: "ask_user",
				ToolArguments: """{"question":"Which section?","choices":["intro","usage"],"allowFreeform":false}"""));
			provider.Replies.Enqueue(new LocalProvider.Reply("Usage section chosen."));
			await source.SendAsync("Step 2.");
			await Check.UntilAsync(() => source.HasPendingQuestion, "The model question did not wait.");
			await source.SendAsync("2");
			await Check.UntilAsync(() => !source.IsWorking && !source.HasPendingQuestion, "The question turn did not finish.");
			var longRequest = "Step 6. " + new string('x', 2500);
			foreach (var request in new[] { "Step 3.", "Step 4.", "Step 5.", longRequest })
			{
				provider.Replies.Enqueue(new LocalProvider.Reply("Done."));
				await RuntimeChecks.SendAndWaitAsync(source, request);
			}
			var before = provider.Requests.Count;
			provider.Replies.Enqueue(HandoffReply());
			bootstrap = await source.PrepareHandoffAsync() ?? throw new InvalidOperationException("The session needs a hand-off.");
			Check.Equal(before + 1, provider.Requests.Count, "Ask the session model once for the hand-off");
			Check.True(bootstrap.Summary.StartsWith(HandoffMarker) && !bootstrap.Summary.Contains("Private working notes"),
				"Carry the summary without the model's working analysis: " + bootstrap.Summary);
			Check.Equal(5, bootstrap.RecentRequests.Count, "Carry the opening request and the four latest");
			Check.Equal(OpeningRequest, bootstrap.RecentRequests[0], "Quote the opening request exactly");
			Check.True(!bootstrap.RecentRequests.Contains("Step 2.") && !bootstrap.RecentRequests.Contains("2"), "Skip older requests and answers to questions.");
			Check.True(bootstrap.RecentRequests[^1].Length < 2100 && bootstrap.RecentRequests[^1].EndsWith("[...]"), "Bound very long requests.");
			Check.True(!source.Transcript.Contains(HandoffMarker), "Keep the hand-off out of the conversation.");
		}
		await using (var next = workspace.CreateChat())
		{
			await next.StartAsync(options with { ApplyInstructions = false });
			var before = provider.Requests.Count;
			next.SetBootstrap(bootstrap);
			Check.Equal(before, provider.Requests.Count, "Attaching context must not trigger model work");
			Check.True(next.HasConversation && ReferenceEquals(await next.PrepareHandoffAsync(), bootstrap) && provider.Requests.Count == before,
				"Carry unsent context to a further restart without asking the model.");
			Check.Throws<InvalidOperationException>(() => next.SetBootstrap(bootstrap with { Workspace = workspace.Root }));
			provider.Replies.Enqueue(new LocalProvider.Reply("Continued with context."));
			await RuntimeChecks.SendAndWaitAsync(next, "Continue.");
			var request = provider.Requests.Last().GetRawText();
			Check.True(request.Contains(HandoffMarker) && request.Contains(OpeningRequest) && request.Contains("Current user request"),
				"Send the hand-off and the earlier requests with the first new prompt.");
			Check.True(!workspace.Store.Load(next.SessionId!).BootstrapPending && !next.Transcript.Contains(HandoffMarker),
				"Consume the hand-off without showing it.");
			provider.Replies.Enqueue(HandoffReply());
			var chained = await next.PrepareHandoffAsync() ?? throw new InvalidOperationException("The continued session needs a hand-off.");
			Check.True(chained.RecentRequests[0] == OpeningRequest && chained.RecentRequests[^1] == "Continue.",
				"Keep the original opening request across chained restarts: " + string.Join(" | ", chained.RecentRequests));
		}
		Check.Equal("Plain reply.", ChatService.HandoffSummary(" Plain reply. "), "Use an untagged reply as the summary");
	}

	/// <summary>
	/// The one sentence the archive keeps. It is asked of the model as
	/// the session ends, so what has to hold is that it is stored where
	/// the list reads it, that a session with nothing to say does not
	/// ask, and that a failure to answer leaves the shutdown alone.
	/// </summary>
	private static async Task CheckArchiveAsync(TestWorkspace workspace, LocalProvider provider, ChatSessionOptions options)
	{
		string sessionId;
		await using (var quiet = workspace.CreateChat())
		{
			await quiet.StartAsync(options);
			var before = provider.Requests.Count;
			Check.True(await quiet.ArchiveAsync() is null && provider.Requests.Count == before,
				"A session that sent nothing has nothing to summarize and must not ask.");
		}

		await using (var chat = workspace.CreateChat())
		{
			await chat.StartAsync(options);
			sessionId = chat.SessionId!;
			provider.Replies.Enqueue(new LocalProvider.Reply("Documented."));
			await RuntimeChecks.SendAndWaitAsync(chat, OpeningRequest);

			var before = provider.Requests.Count;
			var transcript = chat.Transcript;
			provider.Replies.Enqueue(new LocalProvider.Reply(
				"I **documented** the readme without touching code."));
			var line = await chat.ArchiveAsync();
			Check.Equal("Documented the readme without touching code", line,
				"Keep one short plain label out of what the model wrote");
			Check.Equal(before + 1, provider.Requests.Count, "Ask the session model once");
			var request = provider.Requests.Last().GetRawText();
			Check.True(request.Contains("Quickly summarize the most recent completed work", StringComparison.Ordinal)
				&& !request.Contains("Summarize this entire session", StringComparison.Ordinal),
				"Ask for recent completed work rather than an exhaustive session review.");
			Check.True(chat.Transcript == transcript,
				"The archive turn is not part of the conversation, so it must leave the transcript alone.");
			Check.True(!workspace.Store.ReadTranscript(sessionId).Contains("one plain sentence"),
				"The archive question must not be written to the saved transcript.");
			Check.Equal(line, workspace.Store.Load(sessionId).Summary, "Store the line where the list reads it");
			Check.True(workspace.Store.Load(sessionId).DisplayLabel.EndsWith(line!, StringComparison.Ordinal),
				"The Past Sessions row must show the stored line");
			Check.True(!workspace.Store.Load(sessionId).DisplayLabel.Contains(OpeningRequest, StringComparison.Ordinal),
				"A summarized session must not still be listed by its opening prompt.");
		}

		// A session reopened and closed again without being asked
		// anything has nothing new to say.
		await using (var reopened = workspace.CreateChat())
		{
			await reopened.ResumeAsync(sessionId, options);
			var before = provider.Requests.Count;
			Check.True(await reopened.ArchiveAsync() is null && provider.Requests.Count == before,
				"An unchanged session must not spend a model call rewriting the same sentence.");
		}

		// A session already summarized is reopened and ends without an
		// answer: the previous line has to survive rather than be cleared.
		await using (var resumed = workspace.CreateChat())
		{
			await resumed.ResumeAsync(sessionId, options);
			provider.Replies.Enqueue(new LocalProvider.Reply("Continued."));
			await RuntimeChecks.SendAndWaitAsync(resumed, "Carry on.");
			provider.Replies.Enqueue(new LocalProvider.Reply("Done."));
			Check.True(await resumed.ArchiveAsync() is null, "A fragment is not a summary.");
			Check.Equal("Documented the readme without touching code", workspace.Store.Load(sessionId).Summary,
				"A session that could not summarize keeps the line it already had");
		}

		// A summary request that never reaches idle is interrupted at its
		// deadline. The previously stored line remains the useful label.
		await using (var timedOutResume = workspace.CreateChat())
		{
			await timedOutResume.ResumeAsync(sessionId, options);
			provider.Replies.Enqueue(new LocalProvider.Reply("Continued again."));
			await RuntimeChecks.SendAndWaitAsync(timedOutResume, "Carry on again.");
			var previous = workspace.Store.Load(sessionId);
			var held = new LocalProvider.Reply("This archive response never finishes.", Hold: true);
			provider.Replies.Enqueue(held);
			try
			{
				var archiving = timedOutResume.ArchiveWithinAsync(TimeSpan.FromSeconds(1));
				await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
				Check.True(await archiving.WaitAsync(TimeSpan.FromSeconds(10)) is null,
					"A timed-out archive request returns no replacement summary.");
				await held.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
			}
			finally
			{
				held.Release.TrySetResult();
			}
			var after = workspace.Store.Load(sessionId);
			Check.True(!timedOutResume.IsWorking, "A timed-out archive turn must leave the session idle.");
			Check.Equal(previous.Summary, after.Summary, "A timeout must preserve the previous summary.");
			Check.Equal(previous.SummaryRequests, after.SummaryRequests,
				"A timeout must leave the previous summary's request position unchanged.");
			Check.True(after.DisplayLabel.EndsWith(previous.Summary, StringComparison.Ordinal),
				"The previous summary remains the Past Sessions label after a timeout.");
		}

		// A first summary can time out too. Leaving Summary empty lets the
		// existing display policy identify the session by its opening request.
		await using (var timedOutFirst = workspace.CreateChat())
		{
			await timedOutFirst.StartAsync(options);
			var timedOutId = timedOutFirst.SessionId!;
			provider.Replies.Enqueue(new LocalProvider.Reply("Started."));
			await RuntimeChecks.SendAndWaitAsync(timedOutFirst, OpeningRequest);
			var held = new LocalProvider.Reply("Another archive response that never finishes.", Hold: true);
			provider.Replies.Enqueue(held);
			try
			{
				var archiving = timedOutFirst.ArchiveWithinAsync(TimeSpan.FromSeconds(1));
				await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
				Check.True(await archiving.WaitAsync(TimeSpan.FromSeconds(10)) is null,
					"A first archive timeout returns no synthetic summary.");
				await held.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
			}
			finally
			{
				held.Release.TrySetResult();
			}
			var after = workspace.Store.Load(timedOutId);
			Check.True(!timedOutFirst.IsWorking, "A first archive timeout must leave the session idle.");
			Check.Equal("", after.Summary, "Do not replace the opening request with a generic timeout label.");
			Check.Equal(0, after.SummaryRequests, "A timed-out first summary remains eligible for a later retry.");
			Check.True(after.DisplayLabel.Contains(OpeningRequest, StringComparison.Ordinal),
				"The Past Sessions row falls back to the opening request.");
		}

		// Closed in the middle of a turn. Interrupting the user's own
		// work to ask for a list row is not worth the delay it costs.
		await using (var busy = workspace.CreateChat())
		{
			await busy.ResumeAsync(sessionId, options);
			var held = new LocalProvider.Reply("Still working.", Hold: true);
			provider.Replies.Enqueue(held);
			await busy.SendAsync("Start something long.");
			await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));

			var before = provider.Requests.Count;
			Check.True(await busy.ArchiveAsync() is null && provider.Requests.Count == before,
				"A session closed mid-turn must end rather than ask for a summary.");
			held.Release.TrySetResult();
		}
	}

	// Narration that accompanies a tool call is linked as well as the final answer, across a
	// permission request and a model question in the same turn.
	private static async Task CheckAgenticLinksAsync(TestWorkspace workspace, LocalProvider provider, ChatSessionOptions options)
	{
		var scopeKey = PermissionService.MakeKey(workspace.Workspace);
		var scopes = PermissionService.Current.Workspaces;
		scopes.TryGetValue(scopeKey, out var originalScope);
		scopes[scopeKey] = new PermissionScope { Operations = [] };
		try
		{
			await using var turn = workspace.CreateChat();
			await turn.StartAsync(options);
			provider.Replies.Enqueue(new LocalProvider.Reply("Checking `README.md` before answering.", ToolName: "powershell",
				ToolArguments: """{"command":"Write-Output ToolProbe","description":"Write a fixture marker"}"""));
			provider.Replies.Enqueue(new LocalProvider.Reply("", ToolName: "ask_user",
				ToolArguments: """{"question":"Which summary style?","choices":["short","long"],"allowFreeform":false}"""));
			provider.Replies.Enqueue(new LocalProvider.Reply("Results\r\n\r\nSee `README.md`."));
			await turn.SendAsync("Review README.md.");
			await Check.UntilAsync(() => turn.HasPendingQuestion && turn.Transcript.Contains("Permission requested"), "The permission request did not wait.");
			await turn.SendAsync("1");
			await Check.UntilAsync(() => turn.HasPendingQuestion && turn.Transcript.Contains("Which summary style?"), "The model question did not wait.");
			await turn.SendAsync("2");
			await Check.UntilAsync(() => !turn.IsWorking && !turn.HasPendingQuestion && turn.Transcript.Contains("See `README.md`."),
				"The agentic turn did not finish.", timeoutSeconds: 30);
			await turn.WhenRenderedAsync();
			Check.True(turn.RenderedTranscript.Split("kp-path:").Length - 1 >= 2, "Link files in narration and in the answer.");
		}
		finally
		{
			if (originalScope is null)
				scopes.Remove(scopeKey);
			else
				scopes[scopeKey] = originalScope;
		}
	}
}
