# TurboPilot Help

TurboPilot is a Windows desktop workspace for running AI coding sessions with a cloud model or an OpenAI-compatible local server. This Help window is modeless: keep it open while you use the main window, Session settings, Permissions, Customization, and Past Sessions.

## Quick start

1. Choose **Session > Begin Session**.
2. Select a workspace folder.
3. Choose **Copilot CLI** or **BYOK (OpenAI-compatible)**.
4. Choose **Query Service**, select a model, context tier, reasoning effort, and mode, then choose **Begin Session**.
5. Write a prompt in **User Prompt** and press **Ctrl+Enter** or choose **Send**.

TurboPilot offers to attach a root `README.md` or `README.txt` the first time a new workspace is opened. Accepting gives the model project context without starting other work.

## Main window

| Area | Purpose |
| --- | --- |
| **Model Output** | Read the rendered response or its exact Raw Markdown. |
| **User Prompt** | Write requests, answer model questions, and add prompt references. |
| Left output gutter | Move the four-page output window one page older or newer. |
| Left prompt gutter | Move backward and forward through prompts already sent. |
| Status bar | See session state, elapsed time, task progress, context use, and cloud AI credits when available. |
| `+N` | Manage files attached to the next prompt. |
| **Stop** | Interrupt the active model turn. |
| **Send** | Submit the prompt. Sending during a turn interrupts that turn first. |

### Session states

- **Starting..** connects the selected service and creates or resumes the session.
- **Ready..** means the session is idle and accepts a prompt.
- **Working..** means the model or one of its tools is active.
- **Waiting..** means the model asked a question and needs your next reply.
- A suffix such as `77/512K` shows used and available context in whole Ki tokens.
- `AiC=N` shows accumulated Copilot AI credits. It is not shown for BYOK sessions.

## Model Output

**Rendered** uses the built-in Markdown viewer:

- Markdown headings, lists, tables, links, and syntax-highlighted code
- Mermaid diagrams with pan and zoom
- Copy buttons on fenced code blocks
- Local file links and image previews when **Link Files in Rendered Output** is enabled
- Question, permission, plan, tool, status, and change cards
- Collapsed tool runs that can be opened for commands, paths, output, and failures

**Raw** shows the exact transcript text stored with the session.

Each top-level request starts an output page. Both tabs show the same sliding window of up to four adjacent pages. The left gutter moves that window one page at a time; the normal right scrollbar moves through the visible lines. New output follows the newest window unless you moved back to read older pages.

Ordinary web and email links open in the registered Windows application. Local file links open in their registered application, with Visual Studio Code used when no file association exists. Right-click a rendered local file link to reveal its folder.

## Writing prompts

- Press **Ctrl+Enter** while focus is in the prompt or another WPF control. When focus is inside the Rendered browser, click the prompt first or choose **Send**.
- Choose **Stop** to interrupt without sending another prompt.
- Sending while a response is active interrupts it, waits for the runtime to become idle, and then sends the new prompt.
- Blank text is allowed when at least one file is attached.
- Failed sends retain the prompt and pending attachments.
- WPF spell checking underlines misspelled words in the prompt editor.

When the model asks a question, answer with:

- The displayed choice number
- The choice text
- A freeform answer when the question allows one

Answers stay in the same model turn and do not open a modal question box.

## Attachments and prompt references

Open the Attachments dialog with the `+N` button or `/attach`.

- Add one or more files with the picker.
- Drop files from Explorer or Visual Studio onto the prompt.
- Paste an image from the clipboard to create a temporary PNG attachment.
- Image attachments appear in the resizable preview strip.
- Folders are not accepted as attachments.
- Pending attachments clear after a successful send.

Type `[f` to choose a pending attachment and insert `[file:name]`. Type `[s` to choose an enabled skill loaded in the current session and insert `[skill:name]`. Continue typing inside the marker to filter the list. Press Enter or Tab, or click a result, to complete it.

A marker is an explicit request to use that item. Missing skills and ambiguous attachment names are reported before the prompt is sent.

## Session settings

Choose **Session > Begin Session** before connecting, or **Session > Change Session** while connected.

### Model service

| Service | Use it when |
| --- | --- |
| **Copilot CLI** | You want models available through an authenticated GitHub Copilot CLI profile. |
| **BYOK** | You have an OpenAI-compatible endpoint such as Lemonade, Ollama, vLLM, llama.cpp, or Foundry Local. |

For BYOK, enter the host or IP, port, API base path, and optional key. A scheme entered with the host is honored; otherwise TurboPilot uses HTTP. Choose **Query Service** to refresh the model list after changing service details.

### Session options

- **Model** selects a model advertised by the service.
- **Context** selects an advertised context tier when the service provides choices.
- **Reasoning effort** selects an advertised effort level.
- **Mode** selects Standard, Plan, Autopilot, or an enabled custom agent.
- **Apply Instructions** loads enabled customization instructions.
- **Preload Skills** loads enabled skills into the session context.
- **Link Files in Rendered Output** resolves unique workspace paths into links and previews.
- **Play Sounds** plays cues when a prompt leaves, a question waits, and a turn finishes.

Model, context tier, reasoning effort, and Standard/Plan/Autopilot mode changes can apply without losing the conversation when the runtime supports them. Provider, endpoint, loading, or custom-agent changes restart the runtime and offer to carry a model-written hand-off summary plus recent user requests.

> **Autopilot approves session operations automatically.** Use it only with a workspace and model you trust.

## Permissions

Open **Permissions** from Session settings.

Everything inside the active workspace is in scope. Folder grants add Read, Write, or Read/Write access outside it.

- Use `*` and `?` inside a path component.
- End a path with `\...` to include all descendants.
- Operation toggles cover file reads and writes, shell commands, MCP tools, MCP sampling, memory, custom tools, URLs, and hooks.
- An unchecked operation asks before it runs.
- A workspace inherits application defaults until it takes over its own toggles.
- **Use Defaults** returns the workspace to inherited operation settings.
- **Save Options** and **Load Options** move a permission set between workspaces.

Permission changes apply to the next operation of a running session.

## Customization

Open **Customization** from Session settings.

TurboPilot scans the standard user and workspace customization locations plus any extra folders you add. Items are grouped as:

- **Prompts**
- **Agents**
- **Skills**
- **Instructions**
- **MCP Servers**

Enable only the items the session should receive. Enabled agents appear in the Mode list. **Add To Prompt** inserts a compact skill or file reference without sending it. Save and load enabled-item choices when moving a setup between workspaces.

Customization changes apply when the next session starts or resumes. They do not rewrite a session already running.

## Typed commands

Type `/` as the first non-space character to open command completion. Continue typing to filter. Use Up and Down to choose, Enter or Tab to complete, and Escape to close the list.

A command must occupy the complete one-line prompt. An unknown command is sent to the model as ordinary text.

<!-- slash-command-table -->

## Session history and context

### Compact Context

**Session > Compact Context** asks the runtime to summarize older context while leaving the visible transcript unchanged.

### Reset Context

**Session > Reset Context** starts a fresh model conversation with the same settings. You choose whether to store or abandon the old session first.

### Ending a session

When replacing a session, resetting context, viewing or resuming another session, or closing TurboPilot:

- **Summarize and Store** asks for a short archive label and keeps metadata plus Raw and Rendered transcripts.
- **Abandon** skips summarization and deletes that session's saved files.
- **Cancel** leaves the session running.

### Past Sessions

**Session > Past Sessions** searches saved sessions by summary, workspace, prompt, or ID.

- **View** opens history without connecting a model and starts at its first output window.
- **Resume** reconnects the original conversation and starts at its newest output window.
- **Delete** removes all saved files for the selected sessions. Ctrl and Shift select multiple rows.
- The currently running session cannot be resumed or deleted.

### Save Transcript

**Session > Save Transcript** chooses the format from the extension:

- `.html` saves the live Rendered document with diagrams and styling.
- `.md` saves rendered Markdown.
- `.txt` saves the Raw transcript.

## Workspace changes

After a turn changes files, TurboPilot builds a Changes card from the workspace itself rather than trusting tool reports.

- **View Diff** writes an inline diff into the transcript.
- **Compare** opens the Git diff tool configured for the repository containing the file.
- **Undo** confirms, then restores the file to its pre-turn content when Git history is available.
- **Review all** opens the cumulative Session Changes window.

Files outside a Git repository are still reported, but Compare and Undo require an earlier Git-backed copy.

## Workspace tools

The **Tools** menu and matching typed commands open:

- PowerShell in the active workspace, with the editable TurboPilot helper script loaded
- The workspace in File Explorer
- The workspace in Visual Studio Code

The helper script is created at `%LOCALAPPDATA%\TurboPilot\scripts\scripts.ps1` only when missing. Existing edits are preserved.

## Built-in phased work

Published builds include three coordinated modes:

- **phase-planner.agent** writes a master roadmap and self-contained phase plans.
- **phase-orchestrator.agent** manages one TurboPilot executor process through that roadmap.
- **phase-executor.agent** implements and verifies one selected phase, then stops.

These items appear as built-in customizations and can be disabled individually.

## Storage and privacy

TurboPilot stores application data under `%LOCALAPPDATA%\TurboPilot`:

| Path | Contents |
| --- | --- |
| `sessions\` | Session metadata plus Raw and Rendered transcripts |
| `instructions\turbopilot.instructions.md` | User-editable presentation instructions |
| `scripts\scripts.ps1` | User-editable workspace shell helpers |
| `turbopilot-settings.json` | Saved application and provider settings |
| `permissions.json` | Application and workspace permission rules |
| `customizations.json` | Customization folders and enabled-item choices |

Provider credentials are not copied into session metadata. A resumed protected endpoint uses the matching credential currently saved in Settings.

## External launch automation

Local tools can start TurboPilot with a validated JSON request:

```powershell
TurboPilot.exe --launch-request "D:\absolute\path\launch-request.json"
```

Published builds place `session-launch-capabilities.json` beside `TurboPilot.exe`. Read that file for the supported request, status, control, and external assistant message contracts before launching. Each process writes its own status file, accepts explicit replace or close commands without UI automation, and exposes a session-scoped relay host only while the matching chat is active.

## Troubleshooting

| Problem | Check |
| --- | --- |
| No Copilot models | Confirm the Copilot CLI profile is authenticated, then choose **Query Service** again. |
| BYOK query fails | Verify scheme, host, port, API path, server process, and key. Most servers expose models below `/v1`. |
| Rendered output is unavailable | Install or repair the Microsoft Edge WebView2 Runtime. Raw output remains available. |
| A tool keeps asking | Enable that operation in Permissions or add the required outside-workspace folder grant. |
| A skill or agent is missing | Rescan Customization, enable the item, and start or resume a session. |
| A local model is slow on the first turn | Allow its server to load the model. TurboPilot keeps idle BYOK sessions warm after startup. |
| A file name did not become a link | Enable file linking and use a unique workspace-relative name or path. |
| A saved session will not resume | Keep its original provider endpoint available and review the reported session error. |

## More information

- [TurboPilot on GitHub](https://github.com/mighty-studios/TurboPilot)
- [TurbolandWPF theme](https://github.com/mighty-studios/TurbolandWPF)
- Choose **Help > About** for version and license credits.
