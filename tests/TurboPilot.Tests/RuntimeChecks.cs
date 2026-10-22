using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using GitHub.Copilot;
using TurboPilot.Ai;
using TurboPilot.Permissions;

namespace TurboPilot.Tests;

internal static class RuntimeChecks
{
	public static async Task RunCloudAsync()
	{
		using var workspace = new TestWorkspace();
		var models = await ModelService.QueryCopilotCliAsync(workspace.Workspace);
		var model = models.FirstOrDefault(model => model.Id == "gpt-4.1")
			?? models.FirstOrDefault(model => model.Id == "gpt-5-mini")
			?? models.First();
		var contextTier = model.LargestContextTier;
		await using var chat = new ChatService(workspace.Store);
		try
		{
			await chat.StartAsync(new ChatSessionOptions
			{
				WorkspaceFolder = workspace.Workspace,
				Model = model.Id,
				ContextTier = contextTier?.Id ?? "",
				ReasoningEffort = model.DefaultReasoningEffort,
				ContextWindowTokens = contextTier?.ContextWindowTokens ?? model.ContextWindowTokens,
				ApplyInstructions = false,
				PreloadSkills = false,
			}).WaitAsync(TimeSpan.FromSeconds(45));
			await SendAndWaitAsync(chat, "Reply with exactly TURBOPILOT_CHAT_OK. Do not use any tools.");
			Check.True(chat.Transcript.Contains("TURBOPILOT_CHAT_OK"), "Receive the cloud model's reply.");
			Check.True(chat.ContextUsedTokens > 0 && chat.ContextWindowTokens > 0, "Receive cloud context usage.");
			Check.Equal(chat.Transcript, workspace.Store.ReadTranscript(chat.SessionId!), "Persist the live cloud transcript");
			Console.WriteLine($"PASS authenticated cloud chat ({model.Id}), context usage, and persistence");
		}
		finally
		{
			await chat.DisposeAsync();
			if (chat.SessionId is { } id)
			{
				await using var cleanup = new CopilotClient(new CopilotClientOptions { WorkingDirectory = workspace.Workspace });
				await cleanup.StartAsync();
				await cleanup.DeleteSessionAsync(id);
			}
		}
	}

	public static async Task RunAsync()
	{
		using var workspace = new TestWorkspace();
		await using var provider = new LocalProvider();
		var options = new ChatSessionOptions
		{
			WorkspaceFolder = workspace.Workspace,
			Model = "test-model",
			UseByok = true,
			ByokEndpoint = provider.Endpoint,
			ContextWindowTokens = 32768,
			Customizations = workspace.CreateLibrary(),
		};
		string sessionId;
		string firstTranscript;
		await using (var chat = workspace.CreateChat())
		{
			var errors = new ConcurrentQueue<string>();
			chat.ErrorReceived += errors.Enqueue;
			var chunks = new ConcurrentQueue<string>();
			chat.TranscriptReceived += chunks.Enqueue;
			await chat.StartAsync(options).WaitAsync(TimeSpan.FromSeconds(45));
			Check.Equal(1, provider.WarmupRequests, "Warm the local model before declaring the session ready");
			await chat.KeepAliveAsync().WaitAsync(TimeSpan.FromSeconds(15));
			Check.Equal(2, provider.WarmupRequests, "Refresh the local model while the session is idle");
			sessionId = chat.SessionId!;
			var runtime = (CopilotSession)typeof(ChatService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chat)!;
			var skills = await runtime.Rpc.Skills.ListAsync();
			Check.True(skills.Skills.Any(skill => skill.Name == "enabled-skill" && skill.Enabled), "The selected skill must be registered in the runtime.");
			Check.True(!skills.Skills.Any(skill => skill.Name == "disabled-skill" && skill.Enabled), "The disabled sibling skill must not be discovered.");
			Console.WriteLine("PASS runtime skill isolation");

			provider.Replies.Enqueue(new LocalProvider.Reply("Hello streaming world."));
			await SendAndWaitAsync(chat, "first prompt marker");
			Check.True(chat.Transcript.Contains("Hello streaming world."), "Stream the reply.");
			Check.True(chunks.Count(chunk => chunk.Contains("Hello") || chunk.Contains("world")) >= 2, "Display incremental text, not just a final response.");
			var request = provider.Requests.Single().GetRawText();
			Check.True(request.Contains("first prompt marker"), "Send the prompt as written.");
			Check.True(request.Contains("ENABLED_INSTRUCTION_SENTINEL") && request.Contains("ENABLED_SKILL_SENTINEL"), "Preload enabled content in the actual request.");
			Check.True(request.Contains("TurboPilot application instructions") && request.Contains("kp-path:encoded-absolute-path"),
				"Send the central presentation instructions alongside enabled customizations.");
			Check.True(!request.Contains("DISABLED_INSTRUCTION_SENTINEL") && !request.Contains("DISABLED_SKILL_SENTINEL"), "Exclude disabled content from the actual request.");
			Check.True(!request.Contains("UNSELECTED_WORKSPACE_INSTRUCTION_SENTINEL"), "Disable implicit workspace instruction loading.");
			Check.True(chat.Transcript.Contains("**You:** first prompt marker"), "Preserve the user's original prompt.");
			Check.True(!chat.Transcript.Contains("kp-user-prompt")
				&& chat.RenderedTranscript.Contains("<!--kp-user-prompt-start-->")
				&& chat.RenderedTranscript.Contains("<!--kp-user-prompt-end-->"),
				"Style only the Rendered user prompt echo.");
			firstTranscript = chat.Transcript;
			Check.Equal(firstTranscript, workspace.Store.ReadTranscript(sessionId), "Persist streamed output before ending the session");
			Check.True(chat.ContextUsedTokens > 0, "Receive runtime context usage.");
			Check.True(!chat.IsWorking, "Return to idle.");
			Check.Equal(0, errors.Count, "The running session must not hide errors");
			await Check.ThrowsAsync<InvalidOperationException>(() => chat.StartAsync(options));
			Check.Equal(sessionId, chat.SessionId, "A repeated start must not dispose the active session");
			Console.WriteLine("PASS local-provider warming, idle keep-alive, streaming, customization enforcement, context usage, and disk persistence");
		}

		File.WriteAllText(workspace.ApplicationInstructionsPath, "EDITED_APPLICATION_INSTRUCTIONS_SENTINEL");
		await using (var resumed = workspace.CreateChat())
		{
			await resumed.ResumeAsync(sessionId, options).WaitAsync(TimeSpan.FromSeconds(45));
			Check.True(resumed.Transcript.StartsWith(firstTranscript, StringComparison.Ordinal), "Recall the exact prior display transcript.");
			Check.Equal("first prompt marker", resumed.Record!.Prompts.Single(), "Recall prompt history by session ID");
			provider.Replies.Enqueue(new LocalProvider.Reply("Resumed answer."));
			await SendAndWaitAsync(resumed, "second prompt marker");
			var request = provider.Requests.Last().GetRawText();
			Check.True(request.Contains("first prompt marker") && request.Contains("Hello streaming world."), "Restore the actual conversation context, not only the display.");
			Check.True(request.Contains("EDITED_APPLICATION_INSTRUCTIONS_SENTINEL"), "Read the edited central instructions into the resumed runtime.");
			Check.Equal(resumed.Transcript, workspace.Store.ReadTranscript(sessionId), "Continue the same persisted transcript");
			Console.WriteLine("PASS cold resume with restored runtime context and prompt history");

			var slow = new LocalProvider.Reply("Slow response should be interrupted.", Hold: true);
			provider.Replies.Enqueue(slow);
			await resumed.SendAsync("start a slow response");
			await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
			var interrupted = new LocalProvider.Reply("Immediate replacement.");
			provider.Replies.Enqueue(interrupted);
			try
			{
				await resumed.SendAsync("interrupt with this prompt");
				await interrupted.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
				await Check.UntilAsync(() => !resumed.IsWorking, "The replacement turn never became idle.");
				Check.True(resumed.Transcript.Contains("Immediate replacement."), "The interrupt must run before the original stream finishes.");
			}
			finally
			{
				slow.Release.TrySetResult();
			}
			Console.WriteLine("PASS immediate interruption without queueing");

			var stopped = new LocalProvider.Reply("Stop this unfinished response.", Hold: true);
			provider.Replies.Enqueue(stopped);
			await resumed.SendAsync("stop this turn");
			await stopped.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
			try { await resumed.AbortAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
			finally { stopped.Release.TrySetResult(); }
			Check.True(!resumed.IsWorking && !resumed.HasPendingQuestion, "Stop must leave the session ready.");
			Console.WriteLine("PASS stopping an active stream");

			var toolNames = provider.Requests.Last().GetProperty("tools").EnumerateArray()
				.Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ToArray();
			Check.True(toolNames.Contains("ask_user"), "The runtime must expose its chat question tool: " + string.Join(", ", toolNames));
			provider.Replies.Enqueue(new LocalProvider.Reply("", ToolName: "ask_user",
				ToolArguments: """{"question":"Choose a color","choices":["red","blue"],"allowFreeform":false}"""));
			provider.Replies.Enqueue(new LocalProvider.Reply("Choice accepted."));
			await resumed.SendAsync("ask a question");
			await Check.UntilAsync(() => resumed.HasPendingQuestion, "The user-input tool did not request an answer.");
			Check.True(resumed.Transcript.Contains("1. red") && resumed.Transcript.Contains("2. blue"), "Display numbered options in the transcript.");
			await resumed.SendAsync("2");
			await Check.UntilAsync(() => !resumed.IsWorking, "The question turn did not finish.");
			Check.True(provider.Requests.Last().GetRawText().Contains("blue"), "Return choice text to the runtime.");
			Console.WriteLine("PASS native model questions answered in chat");

			var scopeKey = PermissionService.MakeKey(workspace.Workspace);
			var scopes = PermissionService.Current.Workspaces;
			scopes.TryGetValue(scopeKey, out var originalScope);
			scopes[scopeKey] = new PermissionScope { Operations = [] };
			var toolResults = new ConcurrentQueue<ToolExecutionCompleteData>();
			using var toolCompletion = GetSession(resumed).On<ToolExecutionCompleteEvent>(evt => toolResults.Enqueue(evt.Data));
			try
			{
				Check.True(toolNames.Contains("powershell"), "The Windows shell tool must be present.");
				provider.Replies.Enqueue(new LocalProvider.Reply("", ToolName: "powershell",
					ToolArguments: """{"command":"Write-Output TurboPilotPermissionProbe","description":"Write a fixture marker"}"""));
				await resumed.SendAsync("request a shell permission");
				await Check.UntilAsync(() => resumed.HasPendingQuestion, "The permission request did not wait for the user.");
				Check.True(resumed.Transcript.Contains("Permission requested - shell"), "Render permission requests in chat.");
				await resumed.SendAsync("2");
				await Check.UntilAsync(() => !resumed.IsWorking, "The denied tool turn did not finish.");
				Check.True(toolResults.TryDequeue(out var denied) && !denied.Success,
					"Deny the actual tool request: " + JsonSerializer.Serialize(denied));

				scopes[scopeKey].Operations = ["shell"];
				provider.Replies.Enqueue(new LocalProvider.Reply("", ToolName: "powershell",
					ToolArguments: """{"command":"Write-Output TurboPilotPermissionProbe","description":"Write a fixture marker"}"""));
				provider.Replies.Enqueue(new LocalProvider.Reply("Live permission applied."));
				await SendAndWaitAsync(resumed, "use the newly approved shell operation");
				Check.True(!resumed.HasPendingQuestion, "Live permission changes must not require a restart.");
				Check.True(toolResults.TryDequeue(out var approved) && approved.Success
					&& JsonSerializer.Serialize(approved.Result).Contains("TurboPilotPermissionProbe"),
					"Execute the approved fixture command: " + JsonSerializer.Serialize(approved));

				// The transcript has to say what the tool did, not merely
				// that one ran. A denied call and an approved call both
				// leave a card, and each says how it ended.
				Check.True(resumed.Transcript.Contains("[tool] powershell  Write-Output TurboPilotPermissionProbe"),
					"Name the command a shell tool ran, not just the tool: " + resumed.Transcript);
				Check.True(resumed.Transcript.Contains("[tool result]\r\nTurboPilotPermissionProbe"),
					"Keep the complete tool result in the Raw transcript: " + resumed.Transcript);
				Check.True(resumed.RenderedTranscript.Contains("kp-tool-ok")
					&& resumed.RenderedTranscript.Contains("TurboPilotPermissionProbe"),
					"An approved tool card must carry its outcome.");
				Check.True(resumed.RenderedTranscript.Contains("kp-tool-failed"),
					"A denied tool card must be marked as failed.");
				Check.True(!resumed.RenderedTranscript.Contains("<details open"),
					"Tool detail must stay shut until the reader asks for it.");

				// What the turn did to the files is taken from the disk,
				// not from what the agent said it did.
				provider.Replies.Enqueue(new LocalProvider.Reply("", ToolName: "powershell",
					ToolArguments: """{"command":"Set-Content -Path changed.txt -Value TurboPilotChangeProbe","description":"Write a fixture file"}"""));
				provider.Replies.Enqueue(new LocalProvider.Reply("File written."));
				await SendAndWaitAsync(resumed, "write a file into the workspace");
				// The report is written after the turn goes idle, and the
				// tool line already names the file, so wait for the report.
				await Check.UntilAsync(() => resumed.Transcript.Contains("+ changed.txt"),
					"The turn did not report the file it created.");
				Check.True(resumed.Transcript.Contains("Changes (") && resumed.Transcript.Contains("+ changed.txt"),
					"A turn that changed the workspace must say so: " + resumed.Transcript);
				Check.True(resumed.RenderedTranscript.Contains("kp-change-added")
					&& resumed.RenderedTranscript.Contains("kp-act:diff"),
					"Each changed file must be clickable.");
				Console.WriteLine("PASS per-turn workspace change reporting");
			}
			finally
			{
				if (originalScope is null)
					scopes.Remove(scopeKey);
				else
					scopes[scopeKey] = originalScope;
			}
			Console.WriteLine("PASS native permission prompts, denial, and live permission changes");

			var attachment = workspace.Write("workspace\\attachment.txt", "ATTACHMENT_SENTINEL");
			provider.Replies.Enqueue(new LocalProvider.Reply("Attachment received."));
			await SendAndWaitAsync(resumed, "", [attachment]);
			Check.True(resumed.Transcript.Contains("[attached] attachment.txt"), "Name sent attachments in the record.");
			Check.True(provider.Requests.Last().GetRawText().Contains("attachment.txt"), "Carry attachments into the actual provider request.");
			Console.WriteLine("PASS attachment-only sends");

			var persistenceErrors = new List<string>();
			resumed.ErrorReceived += persistenceErrors.Add;
			var transcriptPath = Path.Combine(workspace.HistoryDirectory, sessionId + ".md");
			File.SetAttributes(transcriptPath, FileAttributes.ReadOnly);
			try { resumed.AddNotice("PERSISTENCE_FAILURE_SENTINEL"); }
			finally { File.SetAttributes(transcriptPath, FileAttributes.Normal); }
			Check.Equal(1, persistenceErrors.Count, "Report a failed transcript write");
			resumed.AddNotice("PERSISTENCE_RECOVERY_SENTINEL");
			Check.Equal(resumed.Transcript, workspace.Store.ReadTranscript(sessionId), "Recover all unsaved text after disk access returns");
			Console.WriteLine("PASS explicit persistence errors and recovery without lost text");
		}

		await using (var withoutCustomizations = workspace.CreateChat())
		{
			await withoutCustomizations.ResumeAsync(sessionId, options with { ApplyInstructions = false, PreloadSkills = false });
			var runtime = GetSession(withoutCustomizations);
			Check.True(!(await runtime.Rpc.Skills.ListAsync()).Skills.Any(skill => skill.Enabled), "Disable native skills when preloading is off.");
			provider.Replies.Enqueue(new LocalProvider.Reply("Loading switches honored."));
			await SendAndWaitAsync(withoutCustomizations, "check disabled loading");
			var request = provider.Requests.Last().GetRawText();
			Check.True(!request.Contains("ENABLED_INSTRUCTION_SENTINEL") && !request.Contains("ENABLED_SKILL_SENTINEL"), "Resume must replace earlier customization context when loading switches are off.");
			Console.WriteLine("PASS loading switches on cold resume");
		}

		await using (var agent = workspace.CreateChat())
		{
			await agent.StartAsync(options with { Mode = "writer.agent" });
			provider.Replies.Enqueue(new LocalProvider.Reply("Selected agent response."));
			await SendAndWaitAsync(agent, "use the selected agent");
			Check.True(provider.Requests.Last().GetRawText().Contains("AGENT_SENTINEL"), "Activate the selected agent's actual prompt.");
			Console.WriteLine("PASS selected custom agent activation");
		}

		var savedMetadata = File.ReadAllText(Path.Combine(workspace.HistoryDirectory, sessionId + ".json"));
		var savedTranscript = workspace.Store.ReadTranscript(sessionId);
		await using (var failedResume = workspace.CreateChat())
		{
			await Check.ThrowsAsync<InvalidOperationException>(() =>
				failedResume.ResumeAsync(sessionId, options with { Mode = "disabled-agent" }));
		}
		Check.Equal(savedMetadata, File.ReadAllText(Path.Combine(workspace.HistoryDirectory, sessionId + ".json")), "A failed resume must not replace saved settings");
		Check.Equal(savedTranscript, workspace.Store.ReadTranscript(sessionId), "A failed resume must not alter the saved transcript");
		Console.WriteLine("PASS failed resume without overwriting saved history or settings");

		await using (var planning = workspace.CreateChat())
		{
			await planning.StartAsync(options with { Mode = "Plan" });
			Check.Equal(SessionMode.Plan, await GetSession(planning).Rpc.Mode.GetAsync(), "Start in native Plan mode");
			var plan = planning.HandleExitPlanModeRequestAsync(new ExitPlanModeRequest
			{
				Summary = "Proceed?", Actions = ["interactive", "autopilot"],
			}, new());
			await planning.SendAsync("3");
			Check.True(!(await plan).Approved, "Stay in Plan when requested.");
			Check.Equal(SessionMode.Plan, await GetSession(planning).Rpc.Mode.GetAsync(), "Declining must preserve Plan mode");
			Console.WriteLine("PASS native Plan mode and chat confirmation");
		}

		await using (var autonomous = workspace.CreateChat())
		{
			await autonomous.StartAsync(options with { Mode = "Autopilot" });
			Check.Equal(SessionMode.Autopilot, await GetSession(autonomous).Rpc.Mode.GetAsync(), "Start in native Autopilot mode");
			Console.WriteLine("PASS native Autopilot mode");
		}

		var serverLibrary = options.Customizations.Clone();
		var serversFile = workspace.Write("workspace\\.github\\fixture.mcp.json", JsonSerializer.Serialize(new
		{
			mcpServers = new Dictionary<string, object>
			{
				["enabled-fixture"] = new { type = "http", url = provider.Endpoint + "/mcp", headers = new { Authorization = "fixture-token" } },
				["disabled-fixture"] = new { type = "http", url = provider.Endpoint + "/disabled-mcp" },
			},
		}));
		serverLibrary.McpServers["enabled-fixture"] = new() { FilePath = serversFile, Name = "enabled-fixture", Element = "enabled-fixture" };
		serverLibrary.McpServers["disabled-fixture"] = new() { FilePath = serversFile, Name = "disabled-fixture", Element = "disabled-fixture", Enabled = false };
		workspace.Write("workspace\\.mcp.json", JsonSerializer.Serialize(new
		{
			mcpServers = new { unselected = new { type = "http", url = provider.Endpoint + "/disabled-mcp" } },
		}));
		await using (var configured = workspace.CreateChat())
		{
			await configured.StartAsync(options with { Customizations = serverLibrary });
			provider.Replies.Enqueue(new LocalProvider.Reply("Configured server ready."));
			await SendAndWaitAsync(configured, "use the configured tool catalog");
			await Check.UntilAsync(() => provider.McpRequests.Any(request => request.Method == "tools/list"),
				"The configured server did not load its tools. " + JsonSerializer.Serialize(await GetSession(configured).Rpc.Mcp.ListAsync()));
			var servers = await GetSession(configured).Rpc.Mcp.ListAsync();
			Check.True(servers.Servers.Any(server => server.Name == "enabled-fixture" && server.Status.Value == "connected"), "Connect the enabled server.");
			Check.True(!servers.Servers.Any(server => server.Name == "disabled-fixture" && server.Status.Value == "connected"), "Do not connect the disabled server.");
			Check.Equal(0, provider.DisabledServerRequests, "Do not contact disabled endpoints");
			Check.True(provider.McpRequests.All(request => request.Authorization == "fixture-token"), "Apply configured server headers.");
			Console.WriteLine("PASS native enabled server configuration and disabled-server isolation");
		}

		await using (var canceled = workspace.CreateChat())
		{
			var starting = canceled.StartAsync(options);
			await canceled.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
			await Check.ThrowsAsync<OperationCanceledException>(() => starting);
			Console.WriteLine("PASS canceling startup and disposing the runtime");
		}
		Check.Equal(0, provider.Errors.Count, "The local provider must not hide request failures: "
			+ string.Join(" | ", provider.Errors.Distinct()));
	}

	internal static CopilotSession GetSession(ChatService chat) =>
		(CopilotSession)typeof(ChatService).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chat)!;

	internal static async Task SendAndWaitAsync(ChatService chat, string prompt, IReadOnlyList<string>? attachments = null)
	{
		var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var started = false;
		void OnStateChanged()
		{
			if (chat.IsWorking)
				started = true;
			else if (started && !chat.HasPendingQuestion)
				idle.TrySetResult();
		}
		chat.StateChanged += OnStateChanged;
		try
		{
			await chat.SendAsync(prompt, attachments);
			await idle.Task.WaitAsync(TimeSpan.FromSeconds(30));
			await chat.WhenRenderedAsync().WaitAsync(TimeSpan.FromSeconds(10));
			Check.True(!chat.Transcript.Contains("[error]"), "The runtime reported an error: " + chat.Transcript);
		}
		finally
		{
			chat.StateChanged -= OnStateChanged;
		}
	}
}
