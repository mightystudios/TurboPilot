using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using TurboPilot.Ai;
using TurboPilot.Customizations;
using TurboPilot.Dialogs;
using TurboPilot.Orchestration;
using TurboPilot.Sessions;

namespace TurboPilot.Tests;

internal static class SessionLaunchChecks
{
	public static async Task RunAsync(bool includeRuntime)
	{
		CheckArgumentsAndValidation();
		CheckControlCommandValidation();
		await CheckControlMonitoringAsync();
		CheckStatusWritingAndIsolation();
		CheckCapabilityMarker();
		CheckSessionToolsBundle();
		if (includeRuntime)
			await CheckRuntimeContractAsync();
		Console.WriteLine(includeRuntime
			? "PASS external session launch, control, and runtime lifecycle"
			: "PASS external session launch and control contracts");
	}

	private static void CheckArgumentsAndValidation()
	{
		using var workspace = new TestWorkspace();
		var library = workspace.CreateLibrary();
		var attachment = workspace.Write(
			"workspace\\requests\\work-order.md", "ATTACHMENT_SENTINEL");
		var statusPath = Path.Combine(
			workspace.Workspace, ".ai", "session-runs", "validation", "session-status.json");
		var controlPath = Path.Combine(
			workspace.Workspace, ".ai", "session-runs", "validation", "session-control.json");
		var request = new SessionLaunchRequest
		{
			ContractVersion = SessionLaunchRequest.CurrentContractVersion,
			LaunchId = "fixture-launch",
			SessionLabel = "Fixture automated session",
			Workspace = workspace.Workspace,
			Attachments = [attachment],
			InitialPrompt = "Execute the attached work order.",
			Mode = "WRITER.AGENT",
			SessionStatusPath = statusPath,
			SessionControlPath = controlPath,
		};
		var requestPath = WriteRequest(workspace, "valid", request, includeUnknownField: true);

		Check.True(SessionLaunchRequestLoader.GetLaunchRequestPath([]) is null,
			"No startup arguments must preserve interactive startup.");
		Check.Equal(requestPath,
			SessionLaunchRequestLoader.GetLaunchRequestPath(["--launch-request", requestPath]),
			"Recognize the only external launch argument shape");
		Check.Throws<SessionLaunchRequestException>(() =>
			SessionLaunchRequestLoader.GetLaunchRequestPath(["--launch-request"]));
		Check.Throws<SessionLaunchRequestException>(() =>
			SessionLaunchRequestLoader.GetLaunchRequestPath(["--other", requestPath]));

		var validated = SessionLaunchRequestLoader.Load(requestPath, _ => library);
		Check.Equal(Path.GetFullPath(workspace.Workspace), validated.Request.Workspace,
			"Canonicalize the workspace");
		Check.Equal(Path.GetFullPath(attachment), validated.Request.Attachments.Single(),
			"Canonicalize every attachment");
		Check.Equal("writer.agent", validated.Request.Mode,
			"Resolve a requested enabled custom mode case-insensitively");
		Check.Equal(Path.GetFullPath(controlPath), validated.Request.SessionControlPath,
			"Canonicalize the optional session control path");
		Check.True(!File.Exists(statusPath) && !Directory.Exists(Path.GetDirectoryName(statusPath)),
			"Validation must not create the status file or its parent.");

		var noAttachments = SessionLaunchRequestLoader.Load(
			WriteRequest(workspace, "no-attachments", request with
			{
				LaunchId = "no-attachments",
				Attachments = [],
				SessionStatusPath = Path.Combine(
					workspace.Workspace, ".ai", "session-runs", "no-attachments", "session-status.json"),
			}),
			_ => library);
		Check.Equal(0, noAttachments.Request.Attachments.Count,
			"A launch request may omit attachments.");
		var withoutControl = SessionLaunchRequestLoader.Load(
			WriteRequest(workspace, "no-control", request with
			{
				LaunchId = "no-control",
				SessionStatusPath = Path.Combine(
					workspace.Workspace, ".ai", "session-runs", "no-control", "session-status.json"),
				SessionControlPath = "",
			}),
			_ => library);
		Check.Equal("", withoutControl.Request.SessionControlPath,
			"A launch request may omit the opt-in session control path.");

		ExpectFailure(workspace, library, request with { ContractVersion = "2.0" }, "version");
		ExpectFailure(workspace, library, request with { LaunchId = " \t " }, "empty-launch-id");
		ExpectFailure(workspace, library, request with { Mode = "missing-agent" }, "unknown-mode");
		ExpectFailure(workspace, library, request with { Mode = "disabled-agent" }, "disabled-mode");
		ExpectFailure(workspace, library, request with { InitialPrompt = " \t " }, "empty-prompt");
		ExpectFailure(workspace, library, request with
		{
			Attachments = [workspace.Write("outside-attachment.md", "outside")],
		}, "outside-attachment");
		ExpectFailure(workspace, library, request with
		{
			Attachments = [Path.Combine(workspace.Workspace, "missing.md")],
		}, "missing-attachment");
		ExpectFailure(workspace, library, request with
		{
			Workspace = Path.Combine(workspace.Root, "missing-workspace"),
		}, "missing-workspace");
		ExpectFailure(workspace, library, request with
		{
			SessionStatusPath = Path.Combine(workspace.Workspace, "status.txt"),
		}, "status-extension");
		ExpectFailure(workspace, library, request with
		{
			SessionStatusPath = "relative-status.json",
		}, "relative-status");
		ExpectFailure(workspace, library, request with
		{
			SessionControlPath = "relative-control.json",
		}, "relative-control");
		ExpectFailure(workspace, library, request with
		{
			SessionControlPath = Path.Combine(workspace.Workspace, "control.txt"),
		}, "control-extension");
		ExpectFailure(workspace, library, request with
		{
			SessionControlPath = statusPath,
		}, "overlapping-control-status");
		ExpectFailure(workspace, library, request with
		{
			SessionControlPath = attachment,
		}, "overlapping-control-attachment");

		var overlappingPath = Path.Combine(workspace.Root, "requests", "overlapping-status.json");
		Directory.CreateDirectory(Path.GetDirectoryName(overlappingPath)!);
		File.WriteAllText(overlappingPath, JsonSerializer.Serialize(
			request with { SessionStatusPath = overlappingPath },
			SessionLaunchRequest.JsonOptions));
		Check.Throws<SessionLaunchRequestException>(() =>
			SessionLaunchRequestLoader.Load(overlappingPath, _ => library));

		var overlappingAttachment = workspace.Write(
			"workspace\\requests\\overlapping-status.json", "{}");
		ExpectFailure(workspace, library, request with
		{
			Attachments = [overlappingAttachment],
			SessionStatusPath = overlappingAttachment,
		}, "overlapping-attachment");

		var invalidJson = workspace.Write("requests\\invalid.json", "{not-json");
		Check.Throws<SessionLaunchRequestException>(() =>
			SessionLaunchRequestLoader.Load(invalidJson, _ => library));
	}

	private static void CheckControlCommandValidation()
	{
		using var workspace = new TestWorkspace();
		var replacement = RequestFor(workspace, "replacement", "Start replacement work.");
		var replacementPath = WriteRequest(workspace, "replacement", replacement);
		var replace = new SessionControlCommand
		{
			ContractVersion = SessionControlCommand.CurrentContractVersion,
			CommandId = "replace-1",
			TargetProcessId = Environment.ProcessId,
			TargetLaunchId = "current",
			Action = "REPLACE",
			Disposition = "ABANDON",
			LaunchRequestPath = replacementPath,
		};
		var parsedReplace = SessionControlCommandLoader.Parse(
			JsonSerializer.Serialize(replace, SessionLaunchRequest.JsonOptions));
		Check.True(parsedReplace.Action == SessionControlActions.Replace
			&& parsedReplace.Disposition == SessionControlDispositions.Abandon
			&& parsedReplace.LaunchRequestPath == Path.GetFullPath(replacementPath),
			"Canonicalize a replacement control command.");

		var close = replace with
		{
			CommandId = "close-1",
			Action = SessionControlActions.Close,
			Disposition = SessionControlDispositions.SummarizeAndStore,
			LaunchRequestPath = null,
		};
		var parsedClose = SessionControlCommandLoader.Parse(
			JsonSerializer.Serialize(close, SessionLaunchRequest.JsonOptions));
		Check.True(parsedClose.Action == SessionControlActions.Close
			&& parsedClose.SessionDisposition == SessionDisposition.SummarizeAndStore,
			"Resolve a close command and its explicit disposition.");

		ExpectControlFailure(replace with { ContractVersion = "2.0" });
		ExpectControlFailure(replace with { CommandId = " " });
		ExpectControlFailure(replace with { TargetProcessId = 0 });
		ExpectControlFailure(replace with { TargetLaunchId = " " });
		ExpectControlFailure(replace with { Action = "unknown" });
		ExpectControlFailure(replace with { Disposition = "keep" });
		ExpectControlFailure(replace with { LaunchRequestPath = null });
		ExpectControlFailure(close with { LaunchRequestPath = replacementPath });
		Check.Throws<SessionControlCommandException>(() =>
			SessionControlCommandLoader.Parse("{not-json"));
	}

	private static async Task CheckControlMonitoringAsync()
	{
		using var workspace = new TestWorkspace();
		var path = Path.Combine(workspace.Workspace, "automation", "session-control.json");
		var commands = new ConcurrentQueue<SessionControlCommand>();
		var rejections = new ConcurrentQueue<SessionControlCommandException>();
		using var monitor = new SessionControlMonitor(
			path,
			command =>
			{
				commands.Enqueue(command);
				return Task.CompletedTask;
			},
			exception =>
			{
				rejections.Enqueue(exception);
				return Task.CompletedTask;
			});
		monitor.Start();

		var first = CloseCommand("monitor-1", "current");
		WriteControl(path, JsonSerializer.Serialize(first, SessionLaunchRequest.JsonOptions));
		await Check.UntilAsync(
			() => commands.Count == 1,
			"The session control monitor did not execute a valid command.");

		WriteControl(path, "{not-json");
		await Check.UntilAsync(
			() => rejections.Count == 1,
			"The session control monitor did not reject invalid JSON.");

		WriteControl(path, new string('x', (64 * 1024) + 1));
		await Check.UntilAsync(
			() => rejections.Count == 2,
			"The session control monitor did not reject an oversized command.");

		var second = CloseCommand("monitor-2", "current");
		WriteControl(path, JsonSerializer.Serialize(second, SessionLaunchRequest.JsonOptions));
		await Check.UntilAsync(
			() => commands.Count == 2,
			"The session control monitor did not continue after a rejected command.");
	}

	private static void CheckStatusWritingAndIsolation()
	{
		using var workspace = new TestWorkspace();
		var now = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
		var first = RequestFor(workspace, "first", "secret initial prompt");
		var writer = new SessionStatusWriter(first, () => now, processId: 12345);
		var starting = writer.Write(SessionStatusStates.Starting);
		Check.Equal(12345, starting.ProcessId, "Write the owning process id");

		now = now.AddSeconds(15);
		writer.Write(
			SessionStatusStates.Running,
			"fixture-session",
			isWorking: true,
			lastActivityAt: now.AddSeconds(-1));
		var running = ReadStatus(first.SessionStatusPath);
		Check.True(running.State == SessionStatusStates.Running
			&& running.IsWorking
			&& !running.HasPendingQuestion
			&& running.UpdatedAt > running.StartedAt
			&& running.LastActivityAt > running.StartedAt,
			"Write complete running health data.");

		now = now.AddSeconds(1);
		writer.Write(
			SessionStatusStates.Idle,
			"fixture-session",
			lastControlCommandId: "replace-1",
			lastControlCommandState: SessionControlCommandStates.Received);
		var commandReceived = ReadStatus(first.SessionStatusPath);
		Check.True(commandReceived.LastControlCommandId == "replace-1"
			&& commandReceived.LastControlCommandState == SessionControlCommandStates.Received
			&& commandReceived.LastControlCommandError is null,
			"Acknowledge receipt of a control command without changing lifecycle state.");

		now = now.AddSeconds(1);
		writer.Write(
			SessionStatusStates.Idle,
			"fixture-session",
			lastControlCommandId: "replace-1",
			lastControlCommandState: SessionControlCommandStates.Failed,
			lastControlCommandError: "Replacement rejected.");
		var commandFailed = ReadStatus(first.SessionStatusPath);
		Check.Equal("Replacement rejected.", commandFailed.LastControlCommandError,
			"Report an actionable control-command failure");
		Check.Throws<ArgumentException>(() => writer.Write(
			SessionStatusStates.Idle,
			lastControlCommandState: SessionControlCommandStates.Failed));

		var json = File.ReadAllText(first.SessionStatusPath);
		Check.True(!json.Contains(first.InitialPrompt, StringComparison.Ordinal)
			&& !json.Contains("api-key", StringComparison.OrdinalIgnoreCase)
			&& !json.Contains("model response", StringComparison.OrdinalIgnoreCase),
			"Status records must exclude prompts, credentials, and transcript content.");
		Check.True(!Directory.EnumerateFiles(
			Path.GetDirectoryName(first.SessionStatusPath)!, "*.tmp").Any(),
			"Atomic status writes must not leave temporary files.");

		now = now.AddSeconds(1);
		writer.Write(SessionStatusStates.Failed, "fixture-session", error: "Provider connection failed.");
		var failed = ReadStatus(first.SessionStatusPath);
		Check.Equal("Provider connection failed.", failed.Error, "Write an actionable failure");
		Check.Throws<ArgumentException>(() => writer.Write(SessionStatusStates.Failed));
		Check.Throws<ArgumentOutOfRangeException>(() => writer.Write("unknown"));

		var second = RequestFor(workspace, "second", "another prompt");
		var secondWriter = new SessionStatusWriter(second, processId: 54321);
		Parallel.Invoke(
			() => writer.Write(SessionStatusStates.Closed, "fixture-session"),
			() => secondWriter.Write(SessionStatusStates.WaitingUser,
				"second-session", isWorking: true, hasPendingQuestion: true));
		var firstFinal = ReadStatus(first.SessionStatusPath);
		var secondFinal = ReadStatus(second.SessionStatusPath);
		Check.True(firstFinal.LaunchId == "first"
			&& firstFinal.State == SessionStatusStates.Closed
			&& secondFinal.LaunchId == "second"
			&& secondFinal.State == SessionStatusStates.WaitingUser
			&& secondFinal.ProcessId == 54321,
			"Concurrent launches must remain isolated by status path.");
	}

	private static void CheckCapabilityMarker()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "session-launch-capabilities.json");
		Check.True(File.Exists(path), "Copy the session launch capability marker beside the executable.");
		using var document = JsonDocument.Parse(File.ReadAllText(path));
		var root = document.RootElement;
		Check.Equal(SessionLaunchRequest.CurrentContractVersion,
			root.GetProperty("contractVersion").GetString(),
			"Advertise the supported contract version");
		Check.Equal("--launch-request", root.GetProperty("launchArgument").GetString(),
			"Advertise the launch argument");
		Check.True(root.GetProperty("supportsAttachments").GetBoolean(),
			"Advertise support for general prompt attachments");
		Check.True(root.GetProperty("supportsSessionControl").GetBoolean()
			&& root.GetProperty("controlContractVersion").GetString()
				== SessionControlCommand.CurrentContractVersion,
			"Advertise the opt-in session control contract");
		var actions = root.GetProperty("controlActions").EnumerateArray()
			.Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
		Check.True(actions.SetEquals(new[]
		{
			SessionControlActions.Replace,
			SessionControlActions.Close,
		}), "Advertise every session control action.");
		var dispositions = root.GetProperty("controlDispositions").EnumerateArray()
			.Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
		Check.True(dispositions.SetEquals(new[]
		{
			SessionControlDispositions.Abandon,
			SessionControlDispositions.SummarizeAndStore,
		}), "Advertise every session control disposition.");
		var states = root.GetProperty("sessionStates").EnumerateArray()
			.Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
		Check.True(states.SetEquals(new[]
		{
			SessionStatusStates.Starting,
			SessionStatusStates.Connecting,
			SessionStatusStates.Running,
			SessionStatusStates.WaitingUser,
			SessionStatusStates.Idle,
			SessionStatusStates.Failed,
			SessionStatusStates.Closing,
			SessionStatusStates.Closed,
		}), "Advertise every session-health state.");
	}

	private static void CheckSessionToolsBundle()
	{
		string root = Path.Combine(AppContext.BaseDirectory, "SessionTools");
		string[] required =
		[
			"README.md",
			@"agents\phase-planner.agent.md",
			@"agents\phase-orchestrator.agent.md",
			@"agents\phase-executor.agent.md",
			@"skills\phase-roadmap-authoring\SKILL.md",
			@"skills\phase-plan-execution\SKILL.md",
			@"src\TurboPilotPhaseRunner\phase-runner.py",
			@"src\mighty_ai_tools\phase_runner.py",
			@"contracts\phase-manifest.schema.json",
		];
		Check.True(required.All(relative => File.Exists(Path.Combine(root, relative))),
			"Copy every required SessionTools runtime file beside the executable.");
		Check.True(!Directory.Exists(Path.Combine(root, "tests")),
			"Do not publish SessionTools tests.");
		Check.True(!Directory.Exists(Path.Combine(root, "__pycache__"))
			&& (!Directory.Exists(root)
				|| !Directory.EnumerateFiles(root, "*.pyc", SearchOption.AllDirectories).Any()),
			"Do not publish generated Python cache files.");

		var library = CustomizationService.Preview(null, [], new CustomizationLibrary());
		Check.True(
			library.Agents.Values.Any(item =>
				item.IsBuiltIn && item.Name == "phase-executor.agent" && item.Enabled),
			"Discover the bundled executor under its reserved mode name.");
	}

	private static async Task CheckRuntimeContractAsync()
	{
		using var workspace = new TestWorkspace();
		await using var provider = new LocalProvider();
		var library = workspace.CreateLibrary();
		var workOrder = workspace.Write(
			"workspace\\requests\\runtime-work-order.md", "ATTACHMENT_SENTINEL");
		var request = RequestFor(
			workspace,
			"runtime-launch",
			"Execute the attached runtime work order.",
			[workOrder]);
		var settings = new Settings
		{
			ModelProvider = "Byok",
			ByokEndpoint = provider.Endpoint,
			ByokApiKey = "fixture-api-key",
			SelectedModel = "test-model",
			SelectedEffort = "stale-cloud-effort",
			ApplyInstructions = true,
			PreloadSkills = true,
			LinkFiles = false,
		};
		var options = await SessionOptionsFactory.ResolveSavedAsync(
			workspace.Workspace, "Standard", library, settings);
		Check.True(options.UseByok
			&& options.Model == "test-model"
			&& options.ContextTier == ""
			&& options.ContextWindowTokens == 32768
			&& options.ReasoningEffort == ""
			&& options.ApplyInstructions
			&& options.PreloadSkills
			&& !options.LinkFiles
			&& options.ByokApiKey == "fixture-api-key",
			"Resolve the saved provider, model, context, loading toggles, and credential.");

		var attentionRequests = 0;
		using var status = new SessionStatusController(
			request, () => Interlocked.Increment(ref attentionRequests));
		status.Connecting();
		await using var chat = workspace.CreateChat();
		status.Attach(chat);
		await chat.StartAsync(options);

		var held = new LocalProvider.Reply("Held external launch response.", Hold: true);
		provider.Replies.Enqueue(held);
		await SessionLaunch.SubmitInitialPromptAsync(chat, request);
		status.Connected();
		await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
		await Check.UntilAsync(
			() => ReadStatus(request.SessionStatusPath).State == SessionStatusStates.Running,
			"The externally launched initial turn did not report running.");
		var firstHeartbeat = ReadStatus(request.SessionStatusPath);
		await Task.Delay(SessionLiveness.ProgressInterval + TimeSpan.FromSeconds(2));
		var secondHeartbeat = ReadStatus(request.SessionStatusPath);
		Check.True(secondHeartbeat.UpdatedAt > firstHeartbeat.UpdatedAt
			&& secondHeartbeat.LastActivityAt >= firstHeartbeat.LastActivityAt,
			"A held turn must advance the health heartbeat.");

		var sent = provider.Requests.Last().GetRawText();
		Check.True(sent.Contains(request.InitialPrompt, StringComparison.Ordinal)
			&& sent.Contains(workOrder.Replace("\\", "\\\\"), StringComparison.Ordinal)
			&& !sent.Contains("ATTACHMENT_SENTINEL", StringComparison.Ordinal),
			"Submit one initial prompt with the requested attachment.");
		Check.True(workspace.Store.Load(chat.SessionId!).Prompts.Contains(request.InitialPrompt),
			"Record the externally submitted prompt in normal session history.");

		held.Release.TrySetResult();
		await Check.UntilAsync(
			() => !chat.IsWorking
				&& ReadStatus(request.SessionStatusPath).State == SessionStatusStates.Idle,
			"The held external turn did not become idle.");
		var idleAt = ReadStatus(request.SessionStatusPath).UpdatedAt;
		await Task.Delay(TimeSpan.FromSeconds(1));
		Check.Equal(idleAt, ReadStatus(request.SessionStatusPath).UpdatedAt,
			"An idle session must stop heartbeat writes.");
		status.ControlReceived("runtime-control");
		var controlReceived = ReadStatus(request.SessionStatusPath);
		Check.True(controlReceived.State == SessionStatusStates.Idle
			&& controlReceived.LastControlCommandId == "runtime-control"
			&& controlReceived.LastControlCommandState == SessionControlCommandStates.Received,
			"Control acknowledgement must preserve the live session state.");
		status.ControlCompleted("runtime-control");

		var noAttachmentRequest = request with
		{
			InitialPrompt = "Continue without attachments.",
			Attachments = [],
		};
		provider.Replies.Enqueue(new LocalProvider.Reply("No-attachment reply."));
		await SessionLaunch.SubmitInitialPromptAsync(chat, noAttachmentRequest);
		await Check.UntilAsync(
			() => !chat.IsWorking
				&& ReadStatus(request.SessionStatusPath).State == SessionStatusStates.Idle,
			"The launch helper did not complete a prompt without attachments.");
		var noAttachmentMessage = provider.Requests.Last()
			.GetProperty("messages")
			.EnumerateArray()
			.Last(message => message.GetProperty("role").GetString() == "user")
			.GetRawText();
		Check.True(noAttachmentMessage.Contains(noAttachmentRequest.InitialPrompt, StringComparison.Ordinal)
			&& !noAttachmentMessage.Contains(workOrder.Replace("\\", "\\\\"), StringComparison.Ordinal),
			"The launch helper must support an initial prompt without attachments.");

		provider.Replies.Enqueue(new LocalProvider.Reply(
			"",
			ToolName: "ask_user",
			ToolArguments:
				"""{"question":"Continue the task?","choices":["Continue","Stop"],"allowFreeform":false}"""));
		provider.Replies.Enqueue(new LocalProvider.Reply("Question answered."));
		await chat.SendAsync("Ask for confirmation.");
		await Check.UntilAsync(
			() => chat.HasPendingQuestion
				&& ReadStatus(request.SessionStatusPath).State == SessionStatusStates.WaitingUser,
			"A pending model question did not report waiting_user.");
		var waiting = ReadStatus(request.SessionStatusPath);
		Check.True(waiting.IsWorking && waiting.HasPendingQuestion && attentionRequests == 1,
			"Report the pending question and request attention exactly once.");

		await chat.SendAsync("1");
		await Check.UntilAsync(
			() => !chat.IsWorking
				&& !chat.HasPendingQuestion
				&& ReadStatus(request.SessionStatusPath).State == SessionStatusStates.Idle,
			"Answering the model question did not return through running to idle.");
		Check.Equal(1, attentionRequests, "Do not request attention again after the question is answered.");

		status.Failed("Fixture runtime failure.");
		var failedJson = File.ReadAllText(request.SessionStatusPath);
		var failed = ReadStatus(request.SessionStatusPath);
		Check.True(failed.State == SessionStatusStates.Failed
			&& failed.Error == "Fixture runtime failure."
			&& !failedJson.Contains(settings.ByokApiKey, StringComparison.Ordinal)
			&& !failedJson.Contains(request.InitialPrompt, StringComparison.Ordinal)
			&& !failedJson.Contains("Question answered.", StringComparison.Ordinal),
			"Failure health must be actionable without leaking credentials or conversation content.");
		status.ControlFailed("failed-session-command", "The command was rejected.");
		var failedWithControl = ReadStatus(request.SessionStatusPath);
		Check.True(failedWithControl.State == SessionStatusStates.Failed
			&& failedWithControl.Error == "Fixture runtime failure."
			&& failedWithControl.LastControlCommandState == SessionControlCommandStates.Failed
			&& failedWithControl.LastControlCommandError == "The command was rejected.",
			"A control rejection must preserve the session's lifecycle failure.");

		status.Closing();
		await chat.DisposeAsync();
		status.Closed();
		var closed = ReadStatus(request.SessionStatusPath);
		Check.True(closed.State == SessionStatusStates.Closed
			&& closed.SessionId == chat.SessionId
			&& closed.Error is null,
			"Report closing and closed without carrying a stale error.");
		Check.Equal(0, provider.Errors.Count,
			"The loopback provider must not hide session launch failures: "
			+ string.Join(" | ", provider.Errors));
	}

	private static string WriteRequest(
		TestWorkspace workspace,
		string name,
		SessionLaunchRequest request,
		bool includeUnknownField = false)
	{
		var json = JsonSerializer.Serialize(request, SessionLaunchRequest.JsonOptions);
		if (includeUnknownField)
			json = json[..^1] + ",\"futureField\":{\"ignored\":true}}";
		return workspace.Write($"requests\\{name}.json", json);
	}

	private static void ExpectFailure(
		TestWorkspace workspace,
		CustomizationLibrary library,
		SessionLaunchRequest request,
		string name)
	{
		var path = WriteRequest(workspace, name, request);
		Check.Throws<SessionLaunchRequestException>(() =>
			SessionLaunchRequestLoader.Load(path, _ => library));
	}

	private static SessionLaunchRequest RequestFor(
		TestWorkspace workspace,
		string launchId,
		string initialPrompt,
		IReadOnlyList<string>? attachments = null) =>
		new()
		{
			ContractVersion = SessionLaunchRequest.CurrentContractVersion,
			LaunchId = launchId,
			SessionLabel = "Fixture " + launchId,
			Workspace = workspace.Workspace,
			Attachments = attachments ?? [],
			InitialPrompt = initialPrompt,
			Mode = "Standard",
			SessionStatusPath = Path.Combine(
				workspace.Workspace, ".ai", "session-runs", launchId, "session-status.json"),
			SessionControlPath = Path.Combine(
				workspace.Workspace, ".ai", "session-runs", "session-control.json"),
		};

	private static SessionControlCommand CloseCommand(string commandId, string launchId) =>
		new()
		{
			ContractVersion = SessionControlCommand.CurrentContractVersion,
			CommandId = commandId,
			TargetProcessId = Environment.ProcessId,
			TargetLaunchId = launchId,
			Action = SessionControlActions.Close,
			Disposition = SessionControlDispositions.Abandon,
		};

	private static void ExpectControlFailure(SessionControlCommand command) =>
		Check.Throws<SessionControlCommandException>(() =>
			SessionControlCommandLoader.Parse(
				JsonSerializer.Serialize(command, SessionLaunchRequest.JsonOptions)));

	private static void WriteControl(string path, string content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temporaryPath = path + ".tmp";
		File.WriteAllText(temporaryPath, content);
		File.Move(temporaryPath, path, overwrite: true);
	}

	private static SessionStatusRecord ReadStatus(string path) =>
		JsonSerializer.Deserialize<SessionStatusRecord>(
			File.ReadAllText(path), SessionLaunchRequest.JsonOptions)
		?? throw new InvalidOperationException("The session status file is empty.");
}
