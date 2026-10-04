using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using TurboPilot.Ai;
using TurboPilot.Customizations;
using TurboPilot.Dialogs;
using TurboPilot.Rendering;
using TurboPilot.Sessions;
using TurboPilot.Tools;

namespace TurboPilot.Tests;

internal static class UiChecks
{
	public static Task RunAsync(bool closingOnly = false)
	{
		var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			Exception? failure = null;
			var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
			application.Startup += async (_, _) =>
			{
				try
				{
					if (!closingOnly)
						await RunWindowChecksAsync(application);
					await RunClosingChecksAsync(application);
				}
				catch (Exception ex) { failure = ex; }
				finally { application.Shutdown(); }
			};
			application.Run();
			if (failure is null)
				finished.TrySetResult();
			else
				finished.TrySetException(failure);
		});
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return finished.Task;
	}

	private static async Task RunClosingChecksAsync(Application application)
	{
		var unhandled = new List<Exception>();
		void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
		{
			unhandled.Add(e.Exception);
			e.Handled = true;
		}
		application.DispatcherUnhandledException += OnUnhandled;
		try
		{
			for (var scenario = 0; scenario < 2; scenario++)
			{
				using var workspace = new TestWorkspace();
				await using var provider = new LocalProvider();
				TurbolandTheme.Wpf.TurbolandTheme.Apply(application, TurbolandTheme.Core.ThemeMode.Authentic);
				var window = new MainWindow(workspace.Store, workspace.CreateChat, _ => new(),
					Path.Combine(workspace.Root, "browser"));
				TurbolandTheme.Wpf.TurbolandTheme.ApplyTo(window);
				var closed = false;
				window.Closed += (_, _) => closed = true;
				var webView = Control<WebView2>(window, "webViewOutput");
				var browserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				var browserObserved = false;
				LocalProvider.Reply? slowReply = null;
				var sessionChange = Field<SemaphoreSlim>(window, "_sessionChange");
				var sessionChangeHeld = false;
				try
				{
					window.Show();
					await Check.UntilAsync(() => Field<bool>(window, "_webViewReady"), "The close fixture did not initialize.");
					webView.CoreWebView2.Environment.BrowserProcessExited += (_, _) => browserExited.TrySetResult();
					browserObserved = true;
					if (scenario == 0)
					{
						foreach (var useCloseBox in new[] { false, true })
						{
							var answered = false;
							using var answer = DialogAction<YesNoDialog>(application, dialog =>
							{
								answered = true;
								if (useCloseBox) dialog.Close();
								else Invoke(dialog, "OnNo", dialog, new RoutedEventArgs());
							});
							if (useCloseBox) window.Close();
							else Invoke(window, "OnExitClick", window, new RoutedEventArgs());
							await Check.UntilAsync(() => answered || unhandled.Count > 0, "The exit confirmation was not shown.");
							await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
							Check.True(unhandled.Count == 0, "Declining exit must not throw: " + string.Join("\r\n", unhandled));
							Check.True(!closed && window.IsVisible, "No and the confirmation close box must keep the main window open.");
							Check.True(!Field<bool>(window, "_closing"), "Declining exit must not start shutdown.");
						}
					}
					if (scenario == 1)
					{
						await InvokeTask(window, "StartChatAsync", new ChatSessionOptions
						{
							WorkspaceFolder = workspace.Workspace, Model = "test-model", UseByok = true,
							ByokEndpoint = provider.Endpoint, ContextWindowTokens = 32768,
						}, null);
						slowReply = new LocalProvider.Reply("Response interrupted by application exit.", Hold: true);
						provider.Replies.Enqueue(slowReply);
						await Field<ChatService>(window, "_chat").SendAsync("Keep this turn active.");
						await slowReply.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
						// An unfinished session change holds the exit cleanup until it completes.
						await sessionChange.WaitAsync();
						sessionChangeHeld = true;
					}

					var confirmations = 0;
					var closeReturned = false;
					var confirmationDeferred = false;
					var errorNotices = 0;
					using var confirm = DialogAction<YesNoDialog>(application, dialog =>
					{
						confirmations++;
						confirmationDeferred = closeReturned;
						Invoke(dialog, "OnYes", dialog, new RoutedEventArgs());
					});
					using var acknowledge = DialogAction<MessageDialog>(application, dialog =>
					{
						errorNotices++;
						Invoke(dialog, "OnOk", dialog, new RoutedEventArgs());
					});
					window.Close();
					closeReturned = true;
					window.Close();
					if (scenario == 1)
					{
						await Check.UntilAsync(() => Field<bool>(window, "_closing") || unhandled.Count > 0, "Asynchronous shutdown did not start.");
						Check.True(!closed && window.IsSessionActive, "Wait for the unfinished session change before ending the session and closing.");
						window.Close();
						window.Close();
						await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
						// The dialog helper answers only the first confirmation, so look for another one directly.
						Check.True(!application.Windows.OfType<YesNoDialog>().Any(), "Do not repeat confirmation during asynchronous cleanup.");
						sessionChange.Release();
						sessionChangeHeld = false;
					}
					await Check.UntilAsync(() => closed || unhandled.Count > 0, "Confirmed shutdown did not close the window.");
					await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
					Check.True(unhandled.Count == 0, "Closing must not raise an unhandled exception: " + string.Join("\r\n", unhandled));
					Check.True(closed && confirmationDeferred, "Leave the original Closing event before showing confirmation and closing again.");
					Check.Equal(1, confirmations, "Confirm exit exactly once");
					Check.True(!window.IsSessionActive, "End the session before closing.");
					Check.Equal(0, errorNotices, "Exit without error notices");
					Check.Equal(0, provider.Errors.Count, "Stop an active response without provider failures");
				}
				finally
				{
					if (sessionChangeHeld)
						sessionChange.Release();
					slowReply?.Release.TrySetResult();
					if (!closed)
					{
						foreach (Window dialog in window.OwnedWindows.Cast<Window>().ToArray())
							dialog.Close();
						await window.EndSessionAsync();
						SetField(window, "_closing", true);
						SetField(window, "_closeApproved", true);
						window.Close();
					}
					if (browserObserved)
						await browserExited.Task.WaitAsync(TimeSpan.FromSeconds(15));
				}
			}
			Console.WriteLine("PASS real window-close confirmation, synchronous and asynchronous cleanup, and repeated requests");
		}
		finally { application.DispatcherUnhandledException -= OnUnhandled; }
	}

	private static async Task CheckRenderedUserPromptStyleAsync(MainWindow window, WebView2 webView)
	{
		window.ClearOutput();
		window.AppendOutput(
			"\r\n<!--kp-user-prompt-start-->\r\n\r\n" +
			"**You:** first prompt marker\r\n\r\nsecond prompt paragraph\r\n\r\n" +
			"<!--kp-user-prompt-end-->\r\n\r\nAssistant reply marker\r\n\r\n");

		const string script = """
			(() => {
				const prompts = document.querySelectorAll('#output .kp-user-prompt');
				const first = prompts[0];
				const second = prompts[1];
				const assistant = Array.from(document.querySelectorAll('#output p'))
					.find(p => p.textContent === 'Assistant reply marker');
				return {
					count: prompts.length,
					firstText: first ? first.textContent : '',
					secondText: second ? second.textContent : '',
					color: first ? getComputedStyle(first).color : '',
					size: first ? parseFloat(getComputedStyle(first).fontSize) : 0,
					family: first ? getComputedStyle(first).fontFamily : '',
					labelColor: first && first.querySelector('strong')
						? getComputedStyle(first.querySelector('strong')).color : '',
					assistantStyled: assistant ? assistant.classList.contains('kp-user-prompt') : true,
					assistantColor: assistant ? getComputedStyle(assistant).color : ''
				};
			})()
			""";
		var matched = false;
		for (var attempt = 0; attempt < 100 && !matched; attempt++)
		{
			using var result = JsonDocument.Parse(await webView.CoreWebView2.ExecuteScriptAsync(script));
			var root = result.RootElement;
			matched = root.GetProperty("count").GetInt32() == 2
				&& root.GetProperty("firstText").GetString() == "You: first prompt marker"
				&& root.GetProperty("secondText").GetString() == "second prompt paragraph"
				&& root.GetProperty("color").GetString() == "rgb(255, 255, 0)"
				&& Math.Abs(root.GetProperty("size").GetDouble() - 16) < 0.01
				&& root.GetProperty("family").GetString()!.Contains("Px437 IBM VGA 9x16", StringComparison.Ordinal)
				&& root.GetProperty("labelColor").GetString() == "rgb(255, 255, 0)"
				&& !root.GetProperty("assistantStyled").GetBoolean()
				&& root.GetProperty("assistantColor").GetString() != "rgb(255, 255, 0)";
			if (!matched)
				await Task.Delay(100);
		}
		Check.True(matched, "Render the full user prompt in its input font and yellow without recoloring the reply.");
		window.ClearOutput();
		Console.WriteLine("PASS Rendered user prompt styling in WebView2");
	}

	private static async Task RunWindowChecksAsync(Application application)
	{
		using var workspace = new TestWorkspace();
		await using var provider = new LocalProvider();
		var library = workspace.CreateLibrary();
		TurbolandTheme.Wpf.TurbolandTheme.Apply(application, TurbolandTheme.Core.ThemeMode.Authentic);
		var window = new MainWindow(workspace.Store, workspace.CreateChat, _ => library, Path.Combine(workspace.Root, "browser"),
			workspace.ScriptsPath);
		TurbolandTheme.Wpf.TurbolandTheme.ApplyTo(window);
		var options = new ChatSessionOptions
		{
			WorkspaceFolder = workspace.Workspace,
			Model = "test-model",
			UseByok = true,
			ByokEndpoint = provider.Endpoint,
			ContextWindowTokens = 32768,
		};
		var webView = Control<WebView2>(window, "webViewOutput");
		try
		{
			Check.True(!window.IsSessionActive, "Start without an active session.");
			Check.True(!Control<Button>(window, "buttonSend").IsEnabled, "Disable Send before startup.");
			Check.True(!Control<MenuItem>(window, "menuTools").IsEnabled, "Offer no tools without a workspace to open them on.");
			Check.Equal("_Begin Session...", Control<MenuItem>(window, "menuNewSession").Header, "Offer to begin a session when none is running");
			Check.True(!Control<MenuItem>(window, "menuCompactContext").IsEnabled
				&& !Control<MenuItem>(window, "menuResetContext").IsEnabled,
				"There is no context to compact or reset without a session.");
			Check.Equal("Start or resume a session to begin.", Status(window), "Initial status");
			window.Show();
			await Check.UntilAsync(() => Field<bool>(window, "_webViewReady"), "The rendered output did not initialize.");
			var outputTabs = Control<TabControl>(window, "outputTabs");
			outputTabs.SelectedIndex = 1;
			await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			outputTabs.SelectedIndex = 0;
			await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
			await CheckPromptLayoutAsync(window);
			await CheckRenderedUserPromptStyleAsync(window, webView);

			var starting = InvokeTask(window, "StartChatAsync", options, null);
			Check.True(Status(window).StartsWith("Starting.."), "Show Starting while connecting.");
			Check.True(!Control<Button>(window, "buttonSend").IsEnabled, "Disable Send while connecting.");
			await starting.WaitAsync(TimeSpan.FromSeconds(45));
			Check.True(window.IsSessionActive && Status(window).StartsWith("Ready.."), "Enable a fully started session.");
			Check.True(Control<Button>(window, "buttonSend").IsEnabled, "Enable Send after startup.");
			Check.True(!Status(window).Contains("AiC="), "Do not show cloud credits for a local provider.");
			Check.Equal("C_hange Session...", Control<MenuItem>(window, "menuNewSession").Header, "Offer to change the session once one is running");
			Check.True(Control<MenuItem>(window, "menuCompactContext").IsEnabled
				&& Control<MenuItem>(window, "menuResetContext").IsEnabled,
				"Offer the context actions of a running session.");
			CheckTools(application, window, workspace);
			CheckSettingsCaptions(workspace);
			CheckReasoningEffortDefaults(workspace);
			CheckPastSessionsLayout(window);
			CheckPastSessionsDelete(application, window, workspace);
			CheckCustomizationDiscoveryDialog(workspace);
			CheckPromptReferences(window);
			await CheckCommandsAsync(window, workspace);
			CheckChangeActions(application, window);

			var reply = new LocalProvider.Reply("**UI streaming reply**\n\n```mermaid\ngraph TD\nA[Input] --> B[Output]\n```");
			provider.Replies.Enqueue(reply);
			SetInput(window, "UI prompt marker");
			Click(window, "buttonSend");
			await reply.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
			await Check.UntilAsync(() => Status(window).StartsWith("Ready..") && window.OutputText.Contains("**UI streaming reply**"),
				"The Send button did not complete its turn.");
			var chat = Field<ChatService>(window, "_chat");
			Check.Equal(chat.Transcript, window.OutputText, "The main window must display the service transcript");
			var raw = Control<RichTextBox>(window, "richTextBoxOutput");
			var rawText = new TextRange(raw.Document.ContentStart, raw.Document.ContentEnd).Text;
			Check.Equal(Normalize(window.OutputText), Normalize(rawText), "The Raw tab must contain the complete transcript");
			Check.Equal("", Input(window), "Clear accepted input");
			Check.True(MainWindow.IsSendShortcut(Key.Enter, ModifierKeys.Control)
				&& MainWindow.IsSendShortcut(Key.Return, ModifierKeys.Control), "Ctrl+Enter must send.");
			Check.True(!MainWindow.IsSendShortcut(Key.Enter, ModifierKeys.None)
				&& !MainWindow.IsSendShortcut(Key.Enter, ModifierKeys.Control | ModifierKeys.Shift)
				&& !MainWindow.IsSendShortcut(Key.S, ModifierKeys.Control),
				"Plain Enter writes a newline and other combinations are not Send.");

			var rendered = false;
			for (var attempt = 0; attempt < 100 && !rendered; attempt++)
			{
				using var result = JsonDocument.Parse(await webView.CoreWebView2.ExecuteScriptAsync(
					"({bold: Array.from(document.querySelectorAll('#output strong')).some(e => e.textContent === 'UI streaming reply'), diagram: !!document.querySelector('#output .mermaid-container > svg')})"));
				rendered = result.RootElement.GetProperty("bold").GetBoolean()
					&& result.RootElement.GetProperty("diagram").GetBoolean();
				if (!rendered)
					await Task.Delay(100);
			}
			Check.True(rendered, "The Rendered tab must render streamed markdown and a Mermaid diagram.");
			await CheckToolRowsAsync(webView, chat);
			await CheckTranscriptSaveAsync(window);
			CheckSessionDetailsNotice(window);
			SetInput(window, "unsent draft");
			Click(window, "buttonHistoryPrev");
			Check.Equal("UI prompt marker", Input(window), "Recall a sent prompt with the up arrow");
			Click(window, "buttonHistoryNext");
			Check.Equal("unsent draft", Input(window), "Restore the unsent draft with the down arrow");
			Console.WriteLine("PASS WPF startup, Send, compact status, prompt arrows, Raw, rendered markdown, and Mermaid");

			// Cues ride the same transitions the status line reports, so
			// the question flow below exercises all three: a prompt
			// leaves, the model stops to ask, and the turn finishes.
			Sounds.Enabled = true;
			var cues = new List<string>();
			Sounds.Played = cues;
			provider.Replies.Enqueue(new LocalProvider.Reply("", ToolName: "ask_user",
				ToolArguments: """{"question":"Pick an option","choices":["first","second"],"allowFreeform":false}"""));
			provider.Replies.Enqueue(new LocalProvider.Reply("UI answer accepted."));
			SetInput(window, "ask through the UI");
			Click(window, "buttonSend");
			await Check.UntilAsync(() => Status(window).StartsWith("Waiting.."), "The question did not put the UI into Waiting.");
			Check.True(window.OutputText.Contains("1. first") && window.OutputText.Contains("2. second"), "Show choices in the transcript.");
			var carded = false;
			for (var attempt = 0; attempt < 100 && !carded; attempt++)
			{
				using var result = JsonDocument.Parse(await webView.CoreWebView2.ExecuteScriptAsync(
					"({title: (document.querySelector('#output .kp-question .kp-card-title')||{}).textContent || ''," +
					" body: (document.querySelector('#output .kp-question p')||{}).textContent || ''," +
					" choices: document.querySelectorAll('#output .kp-question .kp-card-choices li').length})"));
				carded = result.RootElement.GetProperty("title").GetString() == "Question"
					&& result.RootElement.GetProperty("body").GetString() == "Pick an option"
					&& result.RootElement.GetProperty("choices").GetInt32() == 2;
				if (!carded)
					await Task.Delay(100);
			}
			Check.True(carded, "The Rendered tab must draw a question as a card with its choices.");
			Check.True(application.Windows.Count == 1, "Model questions must not create pop-up windows.");
			SetInput(window, "2");
			Click(window, "buttonSend");
			await Check.UntilAsync(() => Status(window).StartsWith("Ready..") && window.OutputText.Contains("UI answer accepted."),
				"The in-chat answer did not complete.");
			Check.True(cues.Contains("notify.wav"), "Sending a prompt must be audible.");
			Check.True(cues.Contains("Windows Exclamation.wav"), "A model waiting on an answer must be audible.");
			Check.True(cues.Contains("chimes.wav"), "A finished turn must be audible.");
			Sounds.Enabled = false;
			cues.Clear();
			Invoke(window, "RefreshChatState");
			Check.Equal(0, cues.Count, "Turning cues off must silence them");
			Sounds.Played = null;
			Console.WriteLine("PASS WPF chat questions, numbered answers without pop-up dialogs, and audio cues");

			var attachments = Field<List<string>>(window, "_attachments");
			attachments.Add(Path.Combine(workspace.Workspace, "missing.txt"));
			Invoke(window, "UpdateAttachmentButton");
			SetInput(window, "keep this failed prompt");
			Click(window, "buttonSend");
			await Check.UntilAsync(() => window.OutputText.Contains("The attachment no longer exists"), "The missing attachment was not reported.");
			Check.Equal("keep this failed prompt", Input(window), "Retain input when sending fails");
			Check.Equal("+1", Control<Button>(window, "buttonAttachments").Content, "Retain unsent attachments");
			attachments.Clear();
			Invoke(window, "UpdateAttachmentButton");
			chat.AddNotice("\r\n" + string.Join("\r\n",
				Enumerable.Range(0, 200).Select(index => $"saved-history-line-{index:D3}")) + "\r\n");
			await Check.UntilAsync(() => window.OutputText.Contains("saved-history-line-199"),
				"The long saved transcript did not reach the window.");
			const string renderedRestoreSentinel = "saved-rendered-after-control";
			chat.AddNotice($"binary-prefix{(char)0}{(char)2}{(char)26}{renderedRestoreSentinel}\r\n");
			var sessionId = chat.SessionId!;
			await window.EndSessionAsync();
			string refreshedStandard = workspace.Write(
				"current\\copilot-instructions.md", "REFRESHED_STANDARD_INSTRUCTION");
			library.Instructions[refreshedStandard] = new()
			{
				FilePath = refreshedStandard,
				Name = "refreshed-standard",
				IsCliStandard = true,
			};
			string newerTurboPilotInstruction = workspace.Write(
				"current\\instructions\\additional.instructions.md", "NEW_TURBOPILOT_INSTRUCTION");
			library.Instructions[newerTurboPilotInstruction] = new()
			{
				FilePath = newerTurboPilotInstruction,
				Name = "new-turbopilot",
			};
			var displayedAtEnd = window.OutputText;
			await Task.Run(() => Invoke(window, "ForActiveChat", chat, new Action(() => window.AppendOutput("STALE_EVENT_SENTINEL"))));
			await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
			Check.Equal(displayedAtEnd, window.OutputText, "Ignore callbacks from an ended session");
			Check.True(!Control<Button>(window, "buttonSend").IsEnabled, "Disable input after ending a session.");

			await ChoosePastSessionAsync(application, window, sessionId, "buttonView");
			Check.Equal(workspace.Store.ReadTranscript(sessionId), window.OutputText, "View saved history without starting a runtime");
			Check.True(!window.IsSessionActive, "Offline viewing must not create a live session.");
			await CheckTranscriptOpenedAtTopAsync(window, webView, "Viewing saved history", renderedRestoreSentinel);
			await ChoosePastSessionAsync(application, window, sessionId, "buttonResume");
			await Check.UntilAsync(() => window.IsSessionActive && Status(window).StartsWith("Ready.."),
				"The Past Sessions menu did not resume the selected session.", timeoutSeconds: 45);
			Check.Equal(sessionId, Field<ChatService>(window, "_chat").SessionId, "Resume the selected ID in the main window");
			Check.True(window.OutputText.Contains("UI streaming reply"), "Recall earlier output in both tabs.");
			var resumedInstructions = Field<ChatService>(window, "_chat").Options.Customizations.Instructions;
			Check.True(resumedInstructions.ContainsKey(refreshedStandard)
				&& !resumedInstructions.ContainsKey(newerTurboPilotInstruction),
				"Refresh standard CLI instructions on resume without replacing saved TurboPilot additions.");
			await CheckTranscriptOpenedAtTopAsync(window, webView, "Resuming saved history", renderedRestoreSentinel);
			Console.WriteLine("PASS failed-send recovery, stale-event isolation, Past Sessions viewing, and UI resume at the transcript start");

			await window.EndSessionAsync();
			var canceledStart = InvokeTask(window, "StartChatAsync", options, null);
			var ending = window.EndSessionAsync();
			await Task.WhenAll(canceledStart, ending).WaitAsync(TimeSpan.FromSeconds(45));
			Check.True(!window.IsSessionActive && Field<ChatService?>(window, "_chat") is null, "Ending during startup must not leave a late session behind.");
			Check.Equal("Start or resume a session to begin.", Status(window), "Return to the initial state after canceling startup");
			Console.WriteLine("PASS ending during startup without a ghost session");
			await CheckReadmeOfferAsync(application, window, workspace, provider, options);
			await CheckSessionWindowAsync(application, window, workspace, provider, options);
			Check.Equal(0, provider.Errors.Count, "The UI provider must not hide request failures: "
				+ string.Join(" | ", provider.Errors.Distinct()));
		}
		finally
		{
			await window.EndSessionAsync();
			var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var hasBrowser = webView.CoreWebView2 is not null;
			if (hasBrowser)
				webView.CoreWebView2!.Environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
			SetField(window, "_closing", true);
			SetField(window, "_closeApproved", true);
			window.Close();
			if (hasBrowser)
				await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
		}
	}

	/// <summary>
	/// The readme offer that opens a session on a project. The check
	/// covers the three outcomes that matter: declining sends nothing,
	/// accepting sends the file as an attachment, and restarting the same
	/// workspace does not ask again.
	/// </summary>
	private static async Task CheckReadmeOfferAsync(Application application, MainWindow window, TestWorkspace workspace,
		LocalProvider provider, ChatSessionOptions options)
	{
		var readme = workspace.Write("workspace\\README.md", "Fixture document.");
		var sent = provider.Requests.Count;

		using (DialogAction<YesNoDialog>(application, dialog => Invoke(dialog, "OnNo", dialog, new RoutedEventArgs())))
			await InvokeTask(window, "StartFromSettingsAsync", options);
		await Check.UntilAsync(() => window.IsSessionActive && Status(window).StartsWith("Ready.."), "The declined session did not settle.", timeoutSeconds: 45);
		Check.Equal(sent, provider.Requests.Count, "Declining the readme must send nothing");
		await window.EndSessionAsync();

		provider.Replies.Enqueue(new LocalProvider.Reply("Read the project readme."));
		using (DialogAction<YesNoDialog>(application, dialog => Invoke(dialog, "OnYes", dialog, new RoutedEventArgs())))
			await InvokeTask(window, "StartFromSettingsAsync", options);
		await Check.UntilAsync(() => Status(window).StartsWith("Ready..") && window.OutputText.Contains("Read the project readme."),
			"The readme prompt did not finish.", timeoutSeconds: 45);
		var request = provider.Requests.Last().GetRawText();
		Check.True(request.Contains("README.md") && request.Contains(readme.Replace("\\", "\\\\")),
			"Send the readme as an attachment, not as inlined text.");
		Check.True(!request.Contains("Fixture document."), "The attachment carries the content; the prompt must not.");

		var asked = 0;
		var answerAll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
		answerAll.Tick += (_, _) =>
		{
			if (application.Windows.OfType<YesNoDialog>().FirstOrDefault() is not { IsLoaded: true } dialog) return;
			asked++;
			Invoke(dialog, "OnNo", dialog, new RoutedEventArgs());
		};
		answerAll.Start();
		try { await InvokeTask(window, "StartFromSettingsAsync", options with { ApplyInstructions = !options.ApplyInstructions }); }
		finally { answerAll.Stop(); }
		Check.True(window.IsSessionActive, "The restarted session must be running.");
		Check.Equal(1, asked, "A restart may ask about the hand-off, but must not offer the readme again.");
		await window.EndSessionAsync();
		Console.WriteLine("PASS workspace readme offer, attachment send, and restart suppression");
	}

	private static async Task CheckSessionWindowAsync(Application application, MainWindow window, TestWorkspace workspace,
		LocalProvider provider, ChatSessionOptions options)
	{
		workspace.Write("workspace\\README.md", "Fixture document.");
		await InvokeTask(window, "StartChatAsync", options, null);
		provider.Replies.Enqueue(new LocalProvider.Reply("Results\r\n\r\nUpdated `README.md`."));
		SetInput(window, "Please summarize README.md.");
		Click(window, "buttonSend");
		await Check.UntilAsync(() => Status(window).StartsWith("Ready..") && window.RenderedText.Contains("kp-path:"),
			"The linked UI response did not finish.");
		Check.True(window.OutputText.Contains("Updated `README.md`.") && !window.OutputText.Contains("kp-path:"), "Raw must preserve the original reply.");
		var chat = Field<ChatService>(window, "_chat");
		var sessionId = chat.SessionId;

		var live = options with { Model = "test-model-two" };
		await InvokeTask(window, "StartFromSettingsAsync", live with { Mode = "Plan" });
		Check.True(ReferenceEquals(chat, Field<ChatService>(window, "_chat")) && chat.SessionId == sessionId, "Keep the session for a live change.");
		await Check.UntilAsync(() => window.OutputText.Contains("--- Changed to test-model-two | Plan ---")
			&& Control<TextBlock>(window, "sessionInfo").Text.StartsWith("test-model-two"),
			"Show the live change in the transcript and the session badge.");
		await InvokeTask(window, "StartFromSettingsAsync", live);
		Check.True(Status(window).StartsWith("Ready.."), "Return to Ready after a live change.");

		var restart = live with { ApplyInstructions = false };
		provider.Replies.Enqueue(SessionFeatureChecks.HandoffReply());
		using (var timer = DialogAction<YesNoDialog>(application, dialog => Invoke(dialog, "OnYes", dialog, new RoutedEventArgs())))
			await InvokeTask(window, "StartFromSettingsAsync", restart);
		var restarted = Field<ChatService>(window, "_chat");
		Check.True(!ReferenceEquals(chat, restarted) && restarted.Record!.BootstrapPending
			&& restarted.Record.Bootstrap!.Summary.Contains(SessionFeatureChecks.HandoffMarker), "Attach the hand-off after restart consent.");
		Check.True(!window.OutputText.Contains(SessionFeatureChecks.HandoffMarker), "Do not display hand-off context.");
		provider.Replies.Enqueue(new LocalProvider.Reply("Restarted with context."));
		SetInput(window, "Continue after restart.");
		Click(window, "buttonSend");
		await Check.UntilAsync(() => Status(window).StartsWith("Ready..") && window.RenderedText.Contains("Restarted with context."),
			"The restarted model did not continue.");
		var request = provider.Requests.Last().GetRawText();
		Check.True(request.Contains(SessionFeatureChecks.HandoffMarker) && request.Contains("Please summarize README.md."),
			"Pass the hand-off and the earlier request to the new model.");

		using (var timer = DialogAction<YesNoDialog>(application, dialog => Invoke(dialog, "OnNo", dialog, new RoutedEventArgs())))
			await InvokeTask(window, "StartFromSettingsAsync", live);
		Check.True(Field<ChatService>(window, "_chat").Record!.Bootstrap is null, "Declining the hand-off must start without it.");

		var fresh = Field<ChatService>(window, "_chat");
		await InvokeTask(window, "StartFromSettingsAsync", restart);
		Check.True(!ReferenceEquals(fresh, Field<ChatService>(window, "_chat")) && Field<ChatService>(window, "_chat").Record!.Bootstrap is null,
			"Restart a session without a conversation without offering a hand-off.");
		provider.Replies.Enqueue(new LocalProvider.Reply("Context for a canceled restart."));
		SetInput(window, "Give the next session something to carry.");
		Click(window, "buttonSend");
		await Check.UntilAsync(() => Status(window).StartsWith("Ready..") && window.OutputText.Contains("Context for a canceled restart."),
			"The context turn did not finish.");
		var held = SessionFeatureChecks.HandoffReply(hold: true);
		provider.Replies.Enqueue(held);
		try
		{
			using (var timer = DialogAction<YesNoDialog>(application, dialog => Invoke(dialog, "OnYes", dialog, new RoutedEventArgs())))
			{
				var restarting = InvokeTask(window, "StartFromSettingsAsync", live);
				await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
				await Task.WhenAll(window.EndSessionAsync(), restarting).WaitAsync(TimeSpan.FromSeconds(30));
			}
		}
		finally { held.Release.TrySetResult(); }
		Check.True(!window.IsSessionActive, "End during hand-off preparation must cancel the pending restart.");
		Console.WriteLine("PASS UI Rendered links, live session changes, hand-off restart consent, and cancellation");
	}
	/// <summary>
	/// The Tools menu. Each item opens a separate program, so the check
	/// stops at the point of launch: it aims the window at a folder that
	/// does not exist, which makes every handler report instead of
	/// starting anything, and confirms the PowerShell item still
	/// provisions the helper functions on its way there.
	/// </summary>
	/// <summary>
	/// The primary button has to name the action it is about to take.
	/// Re-accepting an untouched dialog on a running session used to
	/// restart it, so "OK" versus "Begin Session" is a behavior
	/// difference the user can see before clicking.
	/// </summary>
	/// <summary>
	/// The command completion list and one command running end to end.
	/// The list has to stay out of the layout, so what is checked is
	/// that it lives in a popup and opens and closes on its own.
	/// </summary>
	private static async Task CheckCommandsAsync(MainWindow window, TestWorkspace workspace)
	{
		var popup = Control<Popup>(window, "commandPopup");
		var list = Control<ListBox>(window, "commandList");
		var input = Control<RichTextBox>(window, "richTextBoxInput");
		var width = window.ActualWidth;

		CheckPlainTextPaste(window);

		Check.True(!popup.IsOpen, "The command list stays shut until a command is typed.");
		SetInput(window, "just a message");
		Check.True(!popup.IsOpen, "An ordinary message must not summon the command list.");

		SetInput(window, "/");
		Check.True(popup.IsOpen && list.Items.Count > 1, "A slash opens the command list.");
		Check.Equal(0, list.SelectedIndex, "Highlight the first command");
		Check.Equal(width, window.ActualWidth, "The command list must not widen the window");

		SetInput(window, "/pa");
		Check.Equal(1, list.Items.Count, "Narrow the list to what has been typed");

		Check.True((bool)Invoke(window, "HandleCommandKey", Key.Down)!, "The list claims the arrow keys.");
		Check.True((bool)Invoke(window, "HandleCommandKey", Key.Tab)!, "Tab completes the highlighted command.");
		Check.Equal("/past", Input(window), "Tab must leave the completed command in the prompt");
		Check.True(!popup.IsOpen, "Completing a command closes the list.");

		SetInput(window, "/he");
		Check.True(popup.IsOpen, "Reopen the list for a fresh command.");
		Check.True((bool)Invoke(window, "HandleCommandKey", Key.Escape)!, "Escape dismisses the list.");
		Check.True(!popup.IsOpen && Input(window) == "/he", "Escape must dismiss without editing the prompt");
		Check.True(!(bool)Invoke(window, "HandleCommandKey", Key.Down)!,
			"A dismissed list must hand its keys back to the editor.");

		var attachments = Field<List<string>>(window, "_attachments");
		var originalAttachments = attachments.ToArray();
		try
		{
			var attachment = workspace.Write("workspace\\reference.txt", "Reference fixture.");
			attachments.Clear();
			attachments.Add(attachment);
			Invoke(window, "UpdateAttachmentButton");

			SetInput(window, "Compare [f later");
			var paragraph = input.Document.Blocks.OfType<Paragraph>().Single();
			var run = paragraph.Inlines.OfType<Run>().Single();
			input.CaretPosition = run.ContentStart.GetPositionAtOffset(10)!;
			Check.True(popup.IsOpen && list.Items.Count == 1,
				"The [f trigger opens the pending attachment list.");
			Check.True((bool)Invoke(window, "HandleCommandKey", Key.Tab)!,
				"Tab accepts a file reference.");
			Check.Equal("Compare [file:reference.txt] later", Input(window),
				"A file completion replaces only its token at the caret and inserts only the filename.");

			SetInput(window, "Apply [s");
			Check.True(popup.IsOpen && list.Items.Count == 1,
				"The [s trigger offers only enabled skills loaded in the session.");
			list.UpdateLayout();
			var skillItem = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromIndex(0);
			Check.True(skillItem is not null, "The skill completion row must be realized for mouse input.");
			var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
			{
				RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
				Source = skillItem,
			};
			Invoke(window, "CommandList_PreviewMouseLeftButtonDown", list, click);
			Check.True(click.Handled, "A mouse click on a skill completion must be accepted before focus changes.");
			Check.Equal("Apply [skill:enabled-skill]", Input(window),
				"A clicked skill completion preserves the prompt and inserts its session name.");

			SetInput(window, "Read [features]");
			Check.True(!popup.IsOpen, "Ordinary Markdown link text must close reference completion.");
		}
		finally
		{
			attachments.Clear();
			attachments.AddRange(originalAttachments);
			Invoke(window, "UpdateAttachmentButton");
		}

		var chat = Field<ChatService>(window, "_chat");
		var before = chat.Transcript.Length;
		SetInput(window, "/help");
		Click(window, "buttonSend");
		await Check.UntilAsync(() => chat.Transcript.Contains("/compact"), "The help command printed nothing.");
		Check.True(chat.Transcript.Length > before, "A command answers in the transcript.");
		Check.Equal(string.Empty, Input(window), "Running a command clears the prompt");
		Check.True(Status(window).StartsWith("Ready.."),
			"A command is answered locally and never starts a turn.");

		input.Document.Blocks.Clear();
		Console.WriteLine("PASS typed commands: completion list, keys, and local answer without a turn");
	}

	/// <summary>CLIPBRD_E_CANT_OPEN: some other program has the clipboard open.</summary>
	private const int ClipboardHeldElsewhere = unchecked((int)0x800401D0);

	private static void CheckPlainTextPaste(MainWindow window)
	{
		var input = Control<RichTextBox>(window, "richTextBoxInput");
		var previousInput = Input(window);
		IDataObject? previousClipboard;
		try
		{
			previousClipboard = Clipboard.GetDataObject();
		}
		catch (System.Runtime.InteropServices.COMException error) when (error.HResult == ClipboardHeldElsewhere)
		{
			// The whole desktop shares one clipboard, so a program that keeps
			// it open fails this check for a reason the check cannot test.
			Console.WriteLine("SKIP plain-text paste: another program is holding the clipboard open");
			return;
		}
		const string plainText = "plain clipboard representation";
		try
		{
			var clipboardData = new DataObject();
			clipboardData.SetData(DataFormats.UnicodeText, plainText);
			clipboardData.SetData(DataFormats.Rtf,
				@"{\rtf1\ansi{\fonttbl{\f0 Courier New;}}{\colortbl;\red255\green0\blue255;}\f0\fs40\cf1 rich clipboard representation}");
			Clipboard.SetDataObject(clipboardData, true);

			SetInput(window, string.Empty);
			ApplicationCommands.Paste.Execute(null, input);
			Check.Equal(plainText, Input(window), "Paste the clipboard's plain-text representation");

			var pastedRuns = input.Document.Blocks.OfType<Paragraph>()
				.SelectMany(paragraph => paragraph.Inlines.OfType<Run>())
				.Where(run => run.Text.Length > 0)
				.ToArray();
			Check.True(pastedRuns.Length > 0, "Plain-text paste inserts text into the prompt.");
			foreach (var run in pastedRuns)
			{
				Check.True(Equals(input.Foreground, run.Foreground), "Paste must not import the clipboard text color.");
				Check.Equal(input.FontFamily, run.FontFamily, "Paste must not import the clipboard font.");
				Check.Equal(input.FontSize, run.FontSize, "Paste must not import the clipboard font size.");
			}
		}
		finally
		{
			if (previousClipboard is null)
				Clipboard.Clear();
			else
				Clipboard.SetDataObject(previousClipboard, true);
			SetInput(window, previousInput);
		}
	}

	/// <summary>
	/// The Changes card acting on a click. The link scheme carries a
	/// verb and a path, and the risk worth checking is that a verb
	/// nobody implemented, or a path that is not there, does nothing at
	/// all rather than something unexpected.
	/// </summary>
	private static void CheckChangeActions(Application application, MainWindow window)
	{
		var chat = Field<ChatService>(window, "_chat");
		var windows = application.Windows.Count;

		var before = chat.Transcript.Length;
		Invoke(window, "HandleChangeAction", "nonesuch|a.txt");
		Invoke(window, "HandleChangeAction", "");
		Check.Equal(before, chat.Transcript.Length, "An unknown action must do nothing at all");
		Check.Equal(windows, application.Windows.Count, "An unknown action must not open a window");

		// No turn has begun, so the session holds no earlier copy of
		// anything, and both actions have to say so rather than pretend.
		Invoke(window, "HandleChangeAction", "tool|a.txt");
		Invoke(window, "HandleChangeAction", "revert|a.txt");
		Check.True(chat.Transcript.Contains("no earlier copy of a.txt, so there is nothing to compare it with")
			&& chat.Transcript.Contains("no earlier copy of a.txt, so there is nothing to put back"),
			"Comparing or reverting before any turn must explain itself: " + chat.Transcript);

		// Once a turn has begun, the runtime workspace, which is not a
		// repository, is watched by stamp alone, so a file in it still
		// has no earlier copy, and the reason given has to be that one.
		var workspace = Field<ChatSessionOptions>(chat, "_options").WorkspaceFolder!;
		SetField(chat, "_turnAnchor", new ChangeAnchor(workspace, [], new Dictionary<string, long>()));
		try
		{
			Invoke(window, "HandleChangeAction", "tool|a.txt");
			Invoke(window, "HandleChangeAction", "revert|a.txt");
			Check.True(chat.Transcript.Contains("a.txt is not in a Git repository, so there is no earlier copy to compare it with")
				&& chat.Transcript.Contains("a.txt is not in a Git repository, so there is no earlier copy to put back"),
				"Comparing or reverting a file in no repository must explain itself: " + chat.Transcript);
		}
		finally
		{
			SetField(chat, "_turnAnchor", null!);
		}
		Check.Equal(windows, application.Windows.Count, "Refusing to compare or revert must not open a window");
		Console.WriteLine("PASS change card actions refusing what they cannot do");
	}

	/// <summary>
	/// The Past Sessions dialog has to fit what it draws. The content
	/// states its own width, and nothing stopped it claiming more than
	/// the frame, the padding and the shadow margin leave it, so the
	/// right-hand frame line was pushed out from under the button row.
	/// Its height was equally open, growing with a workspace path long
	/// enough to wrap. The details pane is also read to be copied, so it
	/// has to be selectable text rather than a label, and its scroll bar
	/// has to sit on its bottom edge rather than under its text.
	/// </summary>
	private static void CheckPastSessionsLayout(MainWindow window)
	{
		var brief = Record("s1", "Reworked the Past Sessions dialog", @"D:\dev\p", "test-model", "prompt");
		var wordy = Record(
			"TurboPilot-20260925-" + new string('a', 32),
			"Reworked the Past Sessions dialog so its buttons fit inside the frame around them",
			@"D:\dev\projects\" + new string('w', 80),
			"registry.example.com/org/" + new string('m', 80),
			new string('d', 400));

		var (briefWidth, briefHeight) = Measure(brief);
		var (wordyWidth, wordyHeight) = Measure(wordy);

		// Everything in a row and in the details pane is written by
		// somebody else, so a dialog that sizes to them has no size.
		Check.True(briefWidth == wordyWidth && briefHeight == wordyHeight,
			$"The dialog must be the same size whatever it lists: {briefWidth}x{briefHeight} against {wordyWidth}x{wordyHeight}.");
		Console.WriteLine("PASS Past Sessions dialog fits its frame and offers selectable details");

		static SessionRecord Record(string id, string summary, string workspace, string model, string description) => new()
		{
			SessionId = id,
			Summary = summary,
			Description = description,
			Options = new ChatSessionOptions
			{
				Model = model, WorkspaceFolder = workspace, Mode = "Standard",
				UseByok = true, ByokEndpoint = "http://127.0.0.1:65535/v1",
			},
		};

		(double Width, double Height) Measure(SessionRecord record)
		{
			var dialog = new PastSessionsDialog([record]) { Owner = window };
			try
			{
				TurbolandTheme.Wpf.TurbolandTheme.ApplyTo(dialog);
				dialog.Show();
				dialog.UpdateLayout();

				var details = Control<TextBox>(dialog, "textDetails");
				Check.True(details.IsReadOnly, "The details pane must be read-only.");
				Check.True(details.Focusable && details.IsHitTestVisible,
					"The details pane must take focus and the mouse, or its text cannot be selected to copy.");
				Check.True(details.Text.Contains(record.Options.WorkspaceFolder!),
					"The details pane must carry the whole workspace path, since copying it is the point.");

				// The shared field template centers its scroll viewer,
				// which is right for a one-row field and wrong for a box
				// with a stated height: the horizontal scroll bar rides
				// with the centered text instead of the bottom edge.
				var host = (FrameworkElement)details.Template.FindName("PART_ContentHost", details);
				var bottom = host.TransformToAncestor(details).Transform(new Point(0, host.ActualHeight)).Y;
				Check.True(Math.Abs(details.ActualHeight - bottom) <= 1,
					$"The details scroll bar must sit on the bottom edge: content ends at {bottom} of {details.ActualHeight}.");

				// A pane colored like the list reads as another list.
				var list = Control<ListBox>(dialog, "listSessions");
				Check.True(!Equals(ColorOf(details.Background), ColorOf(list.Background))
					&& !Equals(ColorOf(details.Foreground), ColorOf(list.Foreground)),
					"The details pane must not take the same colors as the list it sits under.");

				// The window is capped, so content wider than the cap is
				// not refused: it is squeezed, and what gives way is the
				// frame down the right-hand side of the buttons.
				var content = (FrameworkElement)dialog.Content;
				var wanted = content.ActualWidth + content.Margin.Left + content.Margin.Right;
				Check.True(content.DesiredSize.Width >= wanted,
					$"The content must fit the frame around it rather than be squeezed into it: {content.DesiredSize.Width} for {wanted}.");
				Check.True(dialog.ActualWidth < dialog.MaxWidth,
					$"A dialog sized to its cap has already lost whatever did not fit: {dialog.ActualWidth} of {dialog.MaxWidth}.");
				return (dialog.ActualWidth, dialog.ActualHeight);
			}
			finally { dialog.Close(); }
		}

		static System.Windows.Media.Color ColorOf(System.Windows.Media.Brush brush) =>
			((System.Windows.Media.SolidColorBrush)brush).Color;
	}

	/// <summary>
	/// The changes list declares the same kind of row style as Past
	/// Sessions, so it can lose the theme the same way. Driven through
	/// the git seam rather than a real repository, since the rows only
	/// have to exist to be looked at.
	/// </summary>
	private static void CheckChangesRowSelectionIsVisible(MainWindow window)
	{
		var previous = WorkspaceChanges.Runner;
		WorkspaceChanges.Runner = (_, arguments) => arguments.StartsWith("diff --name-status")
			? "M\tsrc\\one.cs\nA\tsrc\\two.cs\n"
			: "";
		var dialog = new SessionChangesDialog(new ChangeAnchor(@"D:\dev\p",
			[new RepositoryAnchor(@"D:\dev\p", "", "HEAD", new HashSet<string>())], null))
		{
			Owner = window,
		};
		try
		{
			TurbolandTheme.Wpf.TurbolandTheme.ApplyTo(dialog);
			dialog.Show();
			CheckRowSelectionIsVisible(Control<ListBox>(dialog, "listChanges"), "The session changes list");
		}
		finally
		{
			dialog.Close();
			WorkspaceChanges.Runner = previous;
		}
	}

	/// <summary>
	/// Compare and Undo follow the selected file, not the workspace. A
	/// workspace holding a checkout has both kinds of file: one inside
	/// the checkout has an earlier copy to offer, and one beside it has
	/// none.
	/// </summary>
	private static void CheckChangesActionsFollowTheFile(MainWindow window)
	{
		var previous = WorkspaceChanges.Runner;
		WorkspaceChanges.Runner = (_, arguments) => arguments.StartsWith("diff --name-status") ? "M\tone.cs\n" : "";
		// A folder that is not there, so the walk for loose files finds
		// nothing and the one stamped file reads as removed.
		var workspace = Path.Combine(Path.GetTempPath(), "TurboPilot.Tests", "absent-" + Guid.NewGuid().ToString("N"));
		var anchor = new ChangeAnchor(workspace,
			[new RepositoryAnchor(Path.Combine(workspace, "checkout"), "checkout/", "HEAD", new HashSet<string>())],
			new Dictionary<string, long> { ["notes.txt"] = 1 });
		var dialog = new SessionChangesDialog(anchor) { Owner = window };
		try
		{
			TurbolandTheme.Wpf.TurbolandTheme.ApplyTo(dialog);
			dialog.Show();
			var list = Control<ListBox>(dialog, "listChanges");
			var compare = Control<Button>(dialog, "buttonCompare");
			var revert = Control<Button>(dialog, "buttonRevert");
			Check.Equal(2, list.Items.Count, "The list must hold the file beside the checkout and the one in it");

			object RowFor(string path) => list.Items.Cast<object>()
				.First(item => item.GetType().GetProperty("Label")?.GetValue(item) is string label && label.EndsWith(" " + path));
			list.SelectedItem = RowFor("notes.txt");
			Check.True(!compare.IsEnabled && !revert.IsEnabled,
				"A file in no repository has no earlier copy to compare with or put back.");
			list.SelectedItem = RowFor("checkout/one.cs");
			Check.True(compare.IsEnabled && revert.IsEnabled,
				"A file inside a checkout can be compared with its history and put back.");
		}
		finally
		{
			dialog.Close();
			WorkspaceChanges.Runner = previous;
		}
	}

	/// <summary>
	/// A row has to look picked. An <c>ItemContainerStyle</c> declared
	/// without <c>BasedOn</c> replaces the theme's implicit style rather
	/// than extending it, which drops the row template and leaves stock
	/// WPF chrome: a selection bar nearly the color of the cyan field
	/// behind it, and a border that only appears once the window is
	/// deactivated. Nothing about that is visible in the markup, so it
	/// is asserted against the rendered row instead.
	/// </summary>
	private static void CheckRowSelectionIsVisible(ListBox list, string where)
	{
		list.UpdateLayout();
		Check.True(list.Items.Count >= 2, $"{where} needs two rows to compare");
		var selected = Row(0);
		var plain = Row(1);
		// Single-select lists refuse the collection, so the selection is
		// set the way each mode allows.
		if (list.SelectionMode == SelectionMode.Single)
			list.SelectedItem = list.Items[0];
		else
		{
			list.SelectedItems.Clear();
			list.SelectedItems.Add(list.Items[0]);
		}
		list.UpdateLayout();

		// The theme's row parts are the evidence that the theme's style
		// survived: a stock row has neither.
		var bar = selected.Template.FindName("border", selected) as Border;
		Check.True(bar is not null && selected.Template.FindName("focus", selected) is Border,
			$"{where} rows must keep the themed template; a style without BasedOn discards it.");

		var fill = Opaque(bar!.Background) ?? Opaque(list.Background)!;
		var behind = Opaque((Row(1).Template.FindName("border", plain) as Border)?.Background) ?? Opaque(list.Background)!;
		Check.True(fill != behind,
			$"{where} must not paint a picked row the same as the field behind it: {fill} against {behind}.");
		Check.True(Opaque(selected.Foreground) != Opaque(plain.Foreground),
			$"{where} must not write a picked row in the same ink as the rest: {selected.Foreground}.");

		ListBoxItem Row(int index)
		{
			var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index)!;
			row.ApplyTemplate();
			return row;
		}

		// A transparent fill is the row deferring to the list behind it,
		// which is a color the comparison still has to account for.
		static System.Windows.Media.Color? Opaque(System.Windows.Media.Brush? brush) =>
			brush is System.Windows.Media.SolidColorBrush { Color.A: > 0 } solid ? solid.Color : null;
	}

	/// <summary>
	/// so what it acts on has to be exactly what was picked. A run of
	/// rows can be picked at once, the running session is never among
	/// them, and only what was removed from disk leaves the list.
	/// </summary>
	private static void CheckPastSessionsDelete(Application application, MainWindow window, TestWorkspace workspace)
	{
		var directory = Path.Combine(workspace.Root, "delete-sessions");
		Directory.CreateDirectory(directory);
		var store = new SessionStore(directory);
		foreach (var id in new[] { "keep-me", "doomed-one", "doomed-two" })
		{
			store.Create(id, new ChatSessionOptions { WorkspaceFolder = workspace.Root }, "prompt\r\n");
			store.WriteTranscript(id, "prompt\r\n");
			store.WriteRenderedTranscript(id, "prompt\r\n");
		}

		var sessions = store.List(out _);
		Check.Equal(3, sessions.Count, "The store must list what was written to it");

		var dialog = new PastSessionsDialog(sessions, store, "keep-me") { Owner = window };
		try
		{
			TurbolandTheme.Wpf.TurbolandTheme.ApplyTo(dialog);
			dialog.Show();
			dialog.UpdateLayout();

			var list = Control<ListBox>(dialog, "listSessions");
			CheckRowSelectionIsVisible(list, "The Past Sessions list");
			CheckChangesRowSelectionIsVisible(window);
			CheckChangesActionsFollowTheFile(window);
			Check.True(list.SelectionMode != SelectionMode.Single,
				"The list must take more than one row, or a run of old sessions cannot be cleared in one pass.");

			// The running session is listed so it can be recognized, but
			// its files are open: removing them would strand the session
			// that is still writing to them.
			list.SelectedItems.Clear();
			list.SelectedItem = sessions.First(record => record.SessionId == "keep-me");
			dialog.UpdateLayout();
			Check.True(!Control<Button>(dialog, "buttonDelete").IsEnabled,
				"The running session must not be offered for deletion.");
			Check.True(!Control<Button>(dialog, "buttonResume").IsEnabled,
				"The running session must not be offered for resuming.");

			list.SelectedItems.Clear();
			foreach (var record in sessions.Where(record => record.SessionId != "keep-me"))
				list.SelectedItems.Add(record);
			dialog.UpdateLayout();
			Check.True(Control<Button>(dialog, "buttonDelete").IsEnabled,
				"Two deletable rows must enable deleting.");
			Check.True(!Control<Button>(dialog, "buttonResume").IsEnabled,
				"Resume acts on one session, so more than one row must not offer it.");

			using (DialogAction<YesNoDialog>(application, confirm => Invoke(confirm, "OnYes", confirm, new RoutedEventArgs())))
				Invoke(dialog, "OnDelete", dialog, new RoutedEventArgs());

			Check.Equal(1, list.Items.Count, "Deleted sessions must leave the list");
			Check.Equal(1, store.List(out _).Count, "Deleted sessions must leave the store");
			foreach (var extension in new[] { ".json", ".md", ".rendered.md" })
				Check.True(!File.Exists(Path.Combine(directory, "doomed-one" + extension)),
					$"Deleting must remove the '{extension}' file as well as the record.");
			Check.True(File.Exists(Path.Combine(directory, "keep-me.json")),
				"Deleting must leave what was not picked.");
			Console.WriteLine("PASS Past Sessions deletes what was picked and spares the rest");
		}
		finally { dialog.Close(); }
	}


	private static void CheckSettingsCaptions(TestWorkspace workspace)
	{
		// Constructing a Window registers it with the application, so every
		// dialog opened here has to be closed again or later checks see a
		// stray pop-up.
		var opened = new List<SettingsDialog>();
		try
		{
			Check.Equal("Begin Session", Caption(Prepare(null)), "Offer to begin when nothing is running");

			// The settings a fresh dialog describes are, by definition, the
			// settings a session started from it is running on.
			var running = (ChatSessionOptions)Invoke(Prepare(null), "BuildOptions", workspace.Workspace)!;
			var dialog = Prepare(running);
			Check.Equal(SessionChange.None, dialog.PendingChange, "An untouched dialog must not restart the session");
			Check.Equal("OK", Caption(dialog), "Say OK when accepting the dialog changes nothing");

			Control<ComboBox>(dialog, "comboMode").SelectedItem = "Plan";
			Check.Equal("Apply Changes", Caption(dialog), "Say Apply Changes for a switch the running session can take");

			Control<CheckBox>(dialog, "checkApplyInstructions").IsChecked = false;
			Invoke(dialog, "UpdateBeginCaption");
			Check.Equal("Begin Session", Caption(dialog), "Say Begin Session for a change that needs a new session");
			Console.WriteLine("PASS settings dialog primary button captions");
		}
		finally
		{
			foreach (var dialog in opened) dialog.Close();
		}

		static string Caption(SettingsDialog dialog) =>
			(string)Control<Button>(dialog, "buttonBeginSession").Content;

		// The dialog normally fills its model list from a live query; the
		// captions only need a selection to exist.
		SettingsDialog Prepare(ChatSessionOptions? running)
		{
			var dialog = new SettingsDialog(workspace.Workspace, running);
			opened.Add(dialog);
			var model = new AvailableModel { Id = "test-model", ContextWindowTokens = 32768 };
			SetField(dialog, "_models", (IReadOnlyList<AvailableModel>)[model]);
			var combo = Control<ComboBox>(dialog, "comboModel");
			combo.ItemsSource = new[] { model.DisplayLabel };
			combo.SelectedIndex = 0;
			return dialog;
		}
	}

	private static void CheckReasoningEffortDefaults(TestWorkspace workspace)
	{
		var suffix = Guid.NewGuid().ToString("N");
		var firstEfforts = new[] { "low-" + suffix, "high-" + suffix };
		var secondEfforts = new[] { "low-" + suffix, "medium-" + suffix, "maximum-" + suffix };
		var models = (IReadOnlyList<AvailableModel>)
		[
			new AvailableModel
			{
				Id = "reasoning-model-one",
				SupportsReasoningEffort = true,
				ReasoningEfforts = firstEfforts,
				DefaultReasoningEffort = firstEfforts[0],
			},
			new AvailableModel
			{
				Id = "reasoning-model-two",
				SupportsReasoningEffort = true,
				ReasoningEfforts = secondEfforts,
				DefaultReasoningEffort = secondEfforts[0],
			},
		];
		var dialog = new SettingsDialog(workspace.Workspace, null);
		try
		{
			SetField(dialog, "_models", models);
			var comboModel = Control<ComboBox>(dialog, "comboModel");
			comboModel.ItemsSource = models.Select(model => model.DisplayLabel).ToArray();
			var comboEffort = Control<ComboBox>(dialog, "comboEffort");

			comboModel.SelectedIndex = 0;
			Check.Equal(firstEfforts[^1], comboEffort.SelectedItem as string,
				"Select the highest reasoning effort for the initial model.");

			comboEffort.SelectedIndex = 0;
			comboModel.SelectedIndex = 1;
			Check.Equal(secondEfforts[^1], comboEffort.SelectedItem as string,
				"Reset to the highest reasoning effort when changing models.");

			comboEffort.SelectedIndex = 0;
			comboModel.SelectedIndex = 0;
			Check.Equal(firstEfforts[^1], comboEffort.SelectedItem as string,
				"Reset to the highest reasoning effort when returning to a model.");
			Console.WriteLine("PASS highest reasoning effort selected on model changes");
		}
		finally
		{
			dialog.Close();
		}
	}

	/// <summary>
	/// The Session menu's Session Details, written into the transcript
	/// rather than a dialog because the window is a narrow column.
	/// </summary>
	private static void CheckSessionDetailsNotice(MainWindow window)
	{
		var chat = Field<ChatService>(window, "_chat");
		var before = chat.Transcript.Length;
		Invoke(window, "ShowSessionDetails", chat);
		var written = chat.Transcript[before..];
		Check.True(written.Contains("Session Details:"), "The listing must be titled where it is written");
		Check.True(written.Contains("Presentation") && written.Contains("Permissions"),
			"The listing must cover the instructions and the grants in force");
		Console.WriteLine("PASS session details listed into the transcript");
	}

	/// <summary>
	/// Tool calls in the Rendered tab, fed through the live session's event
	/// handler. What matters is that a run of calls folds into one line that
	/// counts them, that a line the reader opened stays open while more
	/// output arrives and the document is drawn again, that a diagram
	/// already drawn is carried through those renders rather than drawn
	/// again, and that an answer handed back through the finishing tool is
	/// drawn as the markdown it is.
	/// </summary>
	private static async Task CheckToolRowsAsync(WebView2 webView, ChatService chat)
	{
		async Task<bool> TrueAsync(string script) =>
			JsonSerializer.Deserialize<bool>(await webView.CoreWebView2.ExecuteScriptAsync(script));
		void Call(string id, string name, string arguments, string outcome, bool success = true)
		{
			chat.HandleSessionEvent(GitHub.Copilot.SessionEvent.FromJson(JsonSerializer.Serialize(new
			{
				type = "tool.execution_start",
				data = new { toolCallId = id, toolName = name, arguments = JsonDocument.Parse(arguments).RootElement },
			})));
			chat.HandleSessionEvent(GitHub.Copilot.SessionEvent.FromJson(JsonSerializer.Serialize(new
			{
				type = "tool.execution_complete",
				data = success
					? (object)new { toolCallId = id, success, result = new { content = outcome } }
					: new { toolCallId = id, success, error = new { message = outcome } },
			})));
		}
		async Task<string> LastFoldAsync(string wanted)
		{
			var seen = "";
			for (var attempt = 0; attempt < 100 && seen != wanted; attempt++)
			{
				if (attempt > 0)
					await Task.Delay(100);
				seen = JsonSerializer.Deserialize<string>(await webView.CoreWebView2.ExecuteScriptAsync(
					"(function () { var folds = document.querySelectorAll('#output details.kp-tools');" +
					" var last = folds[folds.length - 1]; if (!last) return '';" +
					" var failed = last.querySelector(':scope > summary .kp-tools-failed');" +
					" return last.querySelector(':scope > summary .kp-tools-text').textContent" +
					" + '|' + (failed ? failed.textContent : '') + '|' + (last.open ? 'open' : 'shut'); })()")) ?? "";
			}
			return seen;
		}

		Check.True(await TrueAsync("(function () { var drawn = document.querySelector('#output .mermaid-container > svg');" +
				" if (drawn) drawn.setAttribute('data-ui-kept', '1'); return !!drawn; })()"),
			"The diagram drawn above must still be on screen.");
		Call("ui-tool-1", "view", """{"path":"README.md"}""", "# Readme");
		Call("ui-tool-2", "view", """{"path":"missing.md"}""", "Path does not exist", success: false);
		Check.Equal("2 calls: view x2|1 failed|shut", await LastFoldAsync("2 calls: view x2|1 failed|shut"),
			"A run of tool calls folds into one shut line that counts them");

		await webView.CoreWebView2.ExecuteScriptAsync(
			"(function () { var folds = document.querySelectorAll('#output details.kp-tools'); folds[folds.length - 1].open = true; })()");
		Call("ui-tool-3", "rg", """{"pattern":"TODO"}""", "No matches");
		Check.Equal("3 calls: view x2, rg|1 failed|open", await LastFoldAsync("3 calls: view x2, rg|1 failed|open"),
			"A line the reader opened stays open while more calls arrive");

		Call("ui-tool-4", "task_complete", """{"summary":"**UI finishing reply** after the tools."}""",
			"**UI finishing reply** after the tools.");
		var replied = false;
		for (var attempt = 0; attempt < 100 && !replied; attempt++)
		{
			replied = await TrueAsync(
				"Array.from(document.querySelectorAll('#output strong')).some(e => e.textContent === 'UI finishing reply')" +
				" && !Array.from(document.querySelectorAll('#output .kp-tool-name')).some(e => e.textContent === 'task_complete')");
			if (!replied)
				await Task.Delay(100);
		}
		Check.True(replied, "The finishing tool's answer must be drawn as markdown, not as a tool row.");
		Check.Equal("3 calls: view x2, rg|1 failed|open", await LastFoldAsync("3 calls: view x2, rg|1 failed|open"),
			"The reply ends the run and leaves the line the reader opened open");
		Check.True(await TrueAsync("!!document.querySelector('#output .mermaid-container > svg[data-ui-kept]')"),
			"A drawn diagram must be carried through later renders, not drawn again.");
		Console.WriteLine("PASS tool calls folded by run, kept open across renders, diagrams kept drawn, and the finishing answer drawn as the reply");
	}

	/// <summary>
	/// The Session menu's Save Transcript, taken through the live renderer
	/// rather than the formatter alone, so the saved page is the document
	/// that is actually on screen.
	/// </summary>
	private static async Task CheckTranscriptSaveAsync(MainWindow window)
	{
		var chat = Field<ChatService>(window, "_chat");
		var page = await (Task<string?>)Invoke(window, "BuildTranscriptFileAsync", TranscriptExport.Format.Html, chat)!;
		Check.True(page is not null, "The renderer is up, so a page must be produced");
		Check.True(page!.Contains("<strong>UI streaming reply</strong>"),
			"The saved page must carry the rendered document, not the markdown it came from");
		Check.True(page.Contains("<svg"), "A diagram is already drawn on screen and must be saved drawn");
		Check.True(page.Contains("<details class=\"kp-tools\""), "A run of tool calls is saved folded, as it is on screen");
		Check.True(page.Contains("data:font/ttf;base64,"), "The saved page must carry the face it is read in");
		Check.True(!page.Contains("<script"), "A saved transcript must not carry script");

		var markdown = await (Task<string?>)Invoke(window, "BuildTranscriptFileAsync", TranscriptExport.Format.Markdown, chat)!;
		Check.True(markdown!.Contains("**UI streaming reply**"), "Markdown must be saved as markdown");

		var text = await (Task<string?>)Invoke(window, "BuildTranscriptFileAsync", TranscriptExport.Format.Text, chat)!;
		Check.True(text!.Contains("UI streaming reply") && !text.Contains("<strong>"),
			"The text form is the Raw tab, without markup");

		Console.WriteLine("PASS saved transcript carrying the rendered document, diagram, and face");
	}

	/// <summary>
	/// Customization's Add To Prompt names the picked items in the prompt
	/// box and leaves them there to be edited.
	/// </summary>
	private static void CheckPromptReferences(MainWindow window)
	{
		var dialog = new CustomizeDialog();
		try
		{
			var library = new CustomizationLibrary();
			var skill = new CustomizationItem { FilePath = @"C:\kit\skills\pdf\SKILL.md", Name = "pdf" };
			var prompt = new CustomizationItem { FilePath = @"C:\kit\prompts\review.md", Name = "review" };
			library.Skills[skill.FilePath] = skill;
			library.Prompts[prompt.FilePath] = prompt;
			SetField(dialog, "_library", library);

			Check.Equal("[skill:pdf]", Invoke(dialog, "ReferenceFor", skill),
				"A skill uses the same marker as prompt completion");
			Check.Equal("[file:review.md]", Invoke(dialog, "ReferenceFor", prompt),
				"A customization file uses the same filename marker as an attachment");

			var restore = Input(window);
			try
			{
				SetInput(window, "");
				Invoke(window, "AddPromptReferences", (IReadOnlyList<string>)["one", "two"]);
				Check.Equal("one\r\ntwo", Input(window).TrimEnd(), "Both picks land in the prompt box");

				Invoke(window, "AddPromptReferences", (IReadOnlyList<string>)["two", "three"]);
				Check.Equal("one\r\ntwo\r\nthree", Input(window).TrimEnd(),
					"A repeated pick must not be added twice, and typed text is kept");
			}

			finally { SetInput(window, restore); }

			Console.WriteLine("PASS customization add to prompt");
		}
		finally { dialog.Close(); }
	}

	private static void CheckCustomizationDiscoveryDialog(TestWorkspace workspace)
	{
		string home = Path.Combine(workspace.Root, "dialog-copilot-home");
		string canonical = workspace.Write(
			"dialog-copilot-home\\copilot-instructions.md", "DIALOG_CANONICAL_INSTRUCTION");
		string? originalHome = Environment.GetEnvironmentVariable("COPILOT_HOME");
		string? originalDirectories = Environment.GetEnvironmentVariable("COPILOT_CUSTOM_INSTRUCTIONS_DIRS");
		try
		{
			Environment.SetEnvironmentVariable("COPILOT_HOME", home);
			Environment.SetEnvironmentVariable("COPILOT_CUSTOM_INSTRUCTIONS_DIRS", null);
			var dialog = new CustomizeDialog(
				workspaceFolder: null, folders: [], previous: new CustomizationLibrary());
			try
			{
				var library = Field<CustomizationLibrary>(dialog, "_library");
				Check.True(library.Instructions.ContainsKey(canonical),
					"The Customization dialog must scan the canonical instruction before a session starts.");
				Check.Equal("Instructions (1)",
					Control<TabItem>(dialog, "tabInstructions").Header,
					"Show the scanned canonical instruction in the tab count");
			}
			finally
			{
				dialog.Close();
			}
		}
		finally
		{
			Environment.SetEnvironmentVariable("COPILOT_HOME", originalHome);
			Environment.SetEnvironmentVariable("COPILOT_CUSTOM_INSTRUCTIONS_DIRS", originalDirectories);
		}
	}

	private static void CheckTools(Application application, MainWindow window, TestWorkspace workspace)
	{
		Check.True(Control<MenuItem>(window, "menuTools").IsEnabled, "Offer the tools of a running session.");
		var restore = window.ActiveWorkspacePath;
		SetProperty(window, "ActiveWorkspacePath", Path.Combine(workspace.Root, "removed-workspace"));
		try
		{
			foreach (var item in new[] { "menuOpenPowershell", "menuOpenExplorer", "menuOpenVsCode" })
			{
				var reported = false;
				using (DialogAction<MessageDialog>(application, dialog => { reported = true; dialog.Close(); }))
					Control<MenuItem>(window, item).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
				Check.True(reported, $"{item} must report a workspace folder that is no longer there.");
			}
		}
		finally { SetProperty(window, "ActiveWorkspacePath", restore); }
		Check.True(File.Exists(workspace.ScriptsPath), "Open Powershell must provision the helper functions.");
		Console.WriteLine("PASS Tools menu availability, workspace guard, and helper provisioning");
	}

	private static IDisposable DialogAction<T>(Application application, Action<T> action) where T : Window
	{
		var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
		timer.Tick += (_, _) =>
		{
			var dialog = application.Windows.OfType<T>().FirstOrDefault();
			if (dialog is null || !dialog.IsLoaded) return;
			timer.Stop();
			action(dialog);
		};
		timer.Start();
		return new TimerStop(timer);
	}

	private sealed class TimerStop(DispatcherTimer timer) : IDisposable
	{
		public void Dispose() => timer.Stop();
	}

	private static async Task CheckPromptLayoutAsync(MainWindow window)
	{
		var main = Control<Grid>(window, "MainGrid");
		var prompt = Control<Grid>(window, "promptInputGrid");
		var label = Control<Label>(window, "labelUserPrompt");
		var output = Control<Grid>(window, "modelOutputGrid");
		var outputLabel = Control<Label>(window, "labelModelOutput");
		var outputTabs = Control<TabControl>(window, "outputTabs");
		var input = Control<RichTextBox>(window, "richTextBoxInput");
		var body = (Grid)input.Parent;
		var history = (Border)((Grid)Control<Button>(window, "buttonHistoryPrev").Parent).Parent;
		var splitter = Control<Thumb>(window, "SplitterThumb");
		var notice = Control<TextBlock>(window, "noticeTextBlock");
		var outputRow = main.RowDefinitions[0];
		var inputRow = main.RowDefinitions[2];
		var originalSize = new Size(window.Width, window.Height);
		var originalOutput = outputRow.Height;
		var originalInput = inputRow.Height;
		var originalNotice = notice.Text;
		var originalVisibility = notice.Visibility;

		async Task LayoutAsync()
		{
			await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
		}

		void Near(double expected, double actual, string message) =>
			Check.True(Math.Abs(expected - actual) <= 1.5, $"{message}: expected {expected:0.##}, got {actual:0.##}.");

		void CheckBounds()
		{
			Check.True(outputLabel.IsVisible && outputLabel.ActualHeight > 0, "Keep the output label visible.");
			var outputLabelBottom = outputLabel.TranslatePoint(new Point(0, outputLabel.ActualHeight), output).Y;
			Near(outputLabelBottom + outputLabel.Margin.Bottom, outputTabs.TranslatePoint(new Point(), output).Y,
				"Place the tabs immediately below Model Output");
			Near(output.ActualHeight, outputTabs.TranslatePoint(new Point(0, outputTabs.ActualHeight), output).Y,
				"Keep the output tabs filling their pane");
			var labelBottom = label.TranslatePoint(new Point(0, label.ActualHeight), prompt).Y;
			var bodyTop = body.TranslatePoint(new Point(), prompt).Y;
			Check.True(label.IsVisible && label.ActualHeight > 0, "Keep the prompt label visible.");
			Check.True(bodyTop >= labelBottom - 1.5, "Place the editor below the label.");
			Near(bodyTop, history.TranslatePoint(new Point(), prompt).Y, "Shift the history gutter with the editor");
			var bodyBottom = body.TranslatePoint(new Point(0, body.ActualHeight), main).Y;
			Near(main.ActualHeight - prompt.Margin.Bottom, bodyBottom, "Anchor the prompt body to the bottom of the available area");
			var trailingRows = body.RowDefinitions.Skip(Grid.GetRow(input) + Grid.GetRowSpan(input)).Sum(row => row.ActualHeight);
			var inputBottom = input.TranslatePoint(new Point(0, input.ActualHeight), main).Y;
			Near(bodyBottom - trailingRows - input.Margin.Bottom, inputBottom, "Fill the editor's available height without a bottom gap");
			Near(body.ActualWidth - input.Margin.Left - input.Margin.Right, input.ActualWidth, "Fill the editor's available width");
			Check.True(input.ActualHeight > 0, "Keep an editable input area.");
		}

		async Task DragAsync(double change)
		{
			splitter.RaiseEvent(new DragDeltaEventArgs(0, change) { RoutedEvent = Thumb.DragDeltaEvent });
			await LayoutAsync();
			Check.True(outputRow.Height.IsStar && inputRow.Height.IsStar, "Splitter dragging must retain stretch-sized rows.");
			CheckBounds();
		}

		try
		{
			Check.Equal("User Prompt", label.Content, "Display the requested label");
			Check.Equal("Model Output", outputLabel.Content, "Display the output heading");
			Check.True(ReferenceEquals(outputTabs, outputLabel.Target), "Associate the output label with its tabs.");
			Check.True(ReferenceEquals(input, label.Target), "Associate the label with the prompt editor.");
			window.Height = 640;
			await LayoutAsync();
			CheckBounds();
			var initialHeight = input.ActualHeight;
			window.Height = 760;
			await LayoutAsync();
			CheckBounds();
			Check.True(input.ActualHeight > initialHeight, "Grow the input area when the window grows.");
			var beforeDrag = input.ActualHeight;
			await DragAsync(70);
			Near(beforeDrag - 70, input.ActualHeight, "Move the divider down by the requested distance");
			window.Height = 860;
			await LayoutAsync();
			CheckBounds();
			window.Height = 560;
			await LayoutAsync();
			CheckBounds();
			await DragAsync(-90);
			window.Width = 1000;
			window.Height = 740;
			await LayoutAsync();
			CheckBounds();
			notice.Text = "A visible notice still occupies only its assigned layout area.";
			notice.Visibility = Visibility.Visible;
			await LayoutAsync();
			CheckBounds();
			notice.Visibility = Visibility.Collapsed;
			await LayoutAsync();
			await DragAsync(10000);
			await DragAsync(-10000);
			Console.WriteLine("PASS User Prompt label and gap-free input layout across window resizing and splitter dragging");
		}
		finally
		{
			notice.Text = originalNotice;
			notice.Visibility = originalVisibility;
			outputRow.Height = originalOutput;
			inputRow.Height = originalInput;
			window.Width = originalSize.Width;
			window.Height = originalSize.Height;
			await LayoutAsync();
		}
	}

	private static async Task ChoosePastSessionAsync(Application application, MainWindow window, string sessionId, string button)
	{
		var chosen = false;
		var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
		timer.Tick += (_, _) =>
		{
			var dialog = application.Windows.OfType<PastSessionsDialog>().FirstOrDefault();
			if (dialog is null) return;
			timer.Stop();
			Control<TextBox>(dialog, "textSearch").Text = sessionId;
			Click(dialog, button);
			chosen = true;
		};
		timer.Start();
		try
		{
			Control<MenuItem>(window, "menuPastSessions").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
			await Check.UntilAsync(() => chosen, "The Past Sessions menu did not open its picker.");
		}
		finally { timer.Stop(); }
	}

	private static async Task CheckTranscriptOpenedAtTopAsync(MainWindow window, WebView2 webView, string operation,
		string requiredRenderedText)
	{
		var tabs = Control<TabControl>(window, "outputTabs");
		var raw = Control<RichTextBox>(window, "richTextBoxOutput");
		tabs.SelectedIndex = 1;
		await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
		Check.True(raw.VerticalOffset < 1, $"{operation} must open the Raw transcript at its first line.");

		tabs.SelectedIndex = 0;
		await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
		var renderedReady = false;
		for (var attempt = 0; attempt < 100 && !renderedReady; attempt++)
		{
			using var result = JsonDocument.Parse(await webView.CoreWebView2.ExecuteScriptAsync(
				$"({{offset: window.scrollY, hasText: document.getElementById('output').textContent.includes({JsonSerializer.Serialize(requiredRenderedText)})}})"));
			renderedReady = result.RootElement.GetProperty("offset").GetDouble() < 1
				&& result.RootElement.GetProperty("hasText").GetBoolean();
			if (!renderedReady)
				await Task.Delay(50);
		}
		Check.True(renderedReady,
			$"{operation} must restore the Rendered transcript and open it at its first line.");
	}

	private static T Control<T>(FrameworkElement owner, string name) where T : class =>
		owner.FindName(name) as T ?? throw new InvalidOperationException($"Missing control '{name}'.");

	private static T Field<T>(object owner, string name) =>
		(T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

	private static void SetField(object owner, string name, object value) =>
		owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

	private static void SetProperty(object owner, string name, object? value) =>
		owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
			.GetSetMethod(nonPublic: true)!.Invoke(owner, [value]);

	private static object? Invoke(object owner, string name, params object?[] arguments) =>
		owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);

	private static Task InvokeTask(object owner, string name, params object?[] arguments) =>
		(Task)Invoke(owner, name, arguments)!;

	private static void Click(FrameworkElement owner, string name) =>
		Control<Button>(owner, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

	private static string Status(MainWindow window) => Control<TextBlock>(window, "statusTextBlock").Text;
	private static string Input(MainWindow window) => (string)Invoke(window, "GetInputText")!;
	private static void SetInput(MainWindow window, string text) => Invoke(window, "SetInputText", text);
	private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');
}
