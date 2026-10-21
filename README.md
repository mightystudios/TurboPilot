# TurboPilot

TurboPilot is a retro Windows 11 desktop client for large-language-model coding sessions. It provides streamed chat through the GitHub Copilot SDK or any OpenAI-compatible provider, including locally hosted models. It's modern development with a cozy DOS feel.

It answers the question: _"What if LLMs existed in 1992?"_

![sample](./screenshot.png)


## Requirements

- Windows
- .NET 9 SDK or later
- WebView2 Runtime for Rendered output
- An authenticated Copilot CLI profile for cloud sessions, or an OpenAI-compatible endpoint for BYOK sessions
- Python 3.10 or later when using the bundled phase-planning workflow

## Build and run

Initialize the theme submodule, then build the solution:

```powershell
git submodule update --init --recursive
dotnet build .\TurboPilot.sln
dotnet run --project .\src\TurboPilot
```

Start a session with **Session -> Begin Session**. Choose a workspace, provider, model, reasoning effort, and mode. Use **Ctrl+Enter** or **Send** to submit a prompt. Sending while a response is running interrupts that response before sending the new prompt.

TurboPilot displays streamed text in **Raw** and **Rendered** tabs. Rendered output supports Markdown, Mermaid diagrams, links, and local image previews. Questions and permission requests appear as reply cards; answer with a choice number, choice text, or the offered freeform response.

## Workspace tools

The **Tools** menu opens programs in the active workspace:

- **Open PowerShell** starts a shell with TurboPilot helper functions loaded.
- **Open Explorer** opens the workspace in File Explorer.
- **Open VSCode** opens the workspace with the `code` launcher.

## Commands

Type `/` in the prompt box to see available commands. Common commands include:

| Command | Action |
| --- | --- |
| `/help` | List commands |
| `/plan` | Show the current plan |
| `/attach` | Attach files to the next prompt |
| `/compact` | Compact the conversation context |
| `/reset` | Reset context while keeping session settings |
| `/session` | Begin or change the session |
| `/details` | Show active session settings |
| `/save` | Save the transcript |
| `/past` | Open saved sessions |

## Inline Prompt References

Type `[f` in the User Prompt area to choose from a list of pending file attachments and insert a reference like `[file:name]` to the prompt. Type `[s` to do the same using a list of enabled skills.

## Project context and instructions

When a new workspace contains a root `README.md` or `README.txt`, TurboPilot offers to attach it to an opening prompt. The prompt asks the model to understand the project and wait for instructions without changing files.

TurboPilot also appends the user-editable file below to new and resumed sessions:

```text
%LOCALAPPDATA%\TurboPilot\instructions\turbopilot.instructions.md
```

If the file does not exist, it is created from the bundled defaults. The defaults ask for Markdown responses, links, images, and Mermaid diagrams. Edit or empty the file to change those presentation instructions.

## Bundled phase planning and execution

Published builds include `SessionTools` beside `TurboPilot.exe`. TurboPilot
discovers this application-owned source before personal, additional, and
workspace customizations. It provides three stable agent modes:

- `phase-planner.agent` drafts a master roadmap and sequential phase plans.
- `phase-orchestrator.agent` manages one TurboPilot process for the roadmap.
- `phase-executor.agent` completes exactly one phase before its session is replaced.

The built-in source is not added to the user's customization-folder list and
cannot be removed there. Individual bundled items remain visible as **Built in**
and can be disabled. User customizations with the same name receive a suffix,
so the bundled phase mode names remain stable.

Child sessions receive `TURBOPILOT_SESSION_TOOLS` and
`TURBOPILOT_EXECUTABLE`, allowing agents to run the installed Python tools
without depending on a source checkout or the current workspace.

## Session management

- **Compact Context** summarizes the conversation without changing the visible transcript.
- **Reset Context** starts a fresh conversation with the same settings.
- **Past Sessions** lets you view, resume, or delete saved sessions. Use Ctrl or Shift to select multiple sessions for deletion.
- The active session cannot be deleted while it is running.
- Ending or replacing a session, resetting context, and closing the program ask how to handle the session history.
  - **Summarize and Store** generates a short summary and keeps the history in Past Sessions.
  - **Abandon** skips summarization and deletes the saved history.
- Workspace changes are shown after each turn that modifies files. Files can be reviewed, compared, or reverted where Git history is available.

Sessions are stored under:

```text
%LOCALAPPDATA%\TurboPilot\sessions
```

Transcripts, rendered transcripts, and session metadata are saved separately. Provider credentials are not copied into session metadata.

## External session launch and monitoring

Published builds include `session-launch-capabilities.json` beside `TurboPilot.exe`. Any local tool can check this file before launching TurboPilot, start a fresh configured session, submit its first prompt, monitor its health, and optionally replace or close the live session without automating the UI:

```powershell
.\TurboPilot.exe --launch-request 'D:\absolute\path\launch-request.json'
```

Launching `TurboPilot.exe` without arguments keeps the normal interactive startup. Every other argument shape is rejected with a nonzero exit code. The launch request path, workspace, session-status path, and optional session-control path must be absolute. The workspace must exist, JSON sidecar paths must end in `.json`, and each optional attachment must be an absolute existing file contained by the workspace.

The version `1.0` launch request is:

```json
{
  "contractVersion": "1.0",
  "launchId": "nightly-refactor-20261019",
  "sessionLabel": "Nightly refactor",
  "workspace": "D:\\dev\\projects\\ExampleApp",
  "attachments": [
    "D:\\dev\\projects\\ExampleApp\\automation\\work-order.md",
    "D:\\dev\\projects\\ExampleApp\\automation\\context.json"
  ],
  "initialPrompt": "Complete the attached work order and report any blockers.",
  "mode": "Autopilot",
  "sessionStatusPath": "D:\\dev\\projects\\ExampleApp\\.ai\\session-runs\\nightly-refactor-20261019\\session-status.json",
  "sessionControlPath": "D:\\dev\\projects\\ExampleApp\\.ai\\session-runs\\turbopilot-control.json"
}
```

`launchId` is a caller-defined correlation identifier copied into every status record. `attachments` and `sessionControlPath` are optional. Omitting `sessionControlPath` leaves the instance monitor-only and preserves the original launch behavior. TurboPilot rescans customizations for the requested workspace before resolving `mode`. Built-in modes are `Standard`, `Plan`, and `Autopilot`; a custom mode must name an enabled custom agent. Provider, model, reasoning effort, API credentials, instruction loading, skill preloading, and Rendered file linking come from settings already saved in TurboPilot. The initial prompt is not added to application settings. After the fresh session connects, TurboPilot sends that prompt once with all requested attachments.

TurboPilot atomically owns and updates the requested status file. Its version `1.0` schema is:

```json
{
  "contractVersion": "1.0",
  "launchId": "nightly-refactor-20261019",
  "sessionLabel": "Nightly refactor",
  "processId": 12345,
  "sessionId": "ExampleApp-20261019-...",
  "workspace": "D:\\dev\\projects\\ExampleApp",
  "state": "running",
  "startedAt": "2026-10-20T01:00:00+00:00",
  "updatedAt": "2026-10-20T01:00:15+00:00",
  "lastActivityAt": "2026-10-20T01:00:14+00:00",
  "isWorking": true,
  "hasPendingQuestion": false,
  "error": null,
  "lastControlCommandId": null,
  "lastControlCommandState": null,
  "lastControlCommandError": null
}
```

| State | Meaning |
| --- | --- |
| `starting` | The request is valid and launch setup is underway |
| `connecting` | TurboPilot is connecting a fresh SDK session |
| `running` | The model turn is active |
| `waiting_user` | The active turn is waiting for an in-chat answer |
| `idle` | The session is connected with no active turn |
| `failed` | Startup or runtime failed; `error` explains the failure |
| `closing` | Normal window/session shutdown is underway |
| `closed` | Normal shutdown completed |

While a turn remains `running`, `updatedAt` advances at least every 15 seconds. The heartbeat stops while idle or waiting for the user. The first `waiting_user` transition brings the TurboPilot window to the user's attention. Status files never contain provider credentials, permission grants, prompts, responses, or transcript content.

### Remote session control

When `sessionControlPath` is present, the external tool may atomically create or replace that JSON file with a version `1.0` command. Every command targets both the current `processId` and `launchId`, preventing a stale command from controlling a replacement process or session:

```json
{
  "contractVersion": "1.0",
  "commandId": "advance-to-next-work-item",
  "targetProcessId": 12345,
  "targetLaunchId": "nightly-refactor-20261019",
  "action": "replace",
  "disposition": "abandon",
  "launchRequestPath": "D:\\dev\\projects\\ExampleApp\\.ai\\session-runs\\next-work-item\\launch-request.json"
}
```

| Action | Behavior |
| --- | --- |
| `replace` | Validate the referenced launch request, end the current session, start a fresh context in the same window, and submit the replacement request's initial prompt |
| `close` | End the current session and close the TurboPilot window without interactive confirmation |

`disposition` must be `abandon` or `summarize_and_store`. `abandon` skips another model call and removes the ended session from Past Sessions. `summarize_and_store` asks the current model for its archive label before retaining the session. A `replace` command requires `launchRequestPath`; a `close` command must omit it.

The replacement request must use a new `launchId` and `sessionStatusPath` while retaining the same `sessionControlPath`. TurboPilot resolves and validates the replacement request before ending the live session. The old status file ends in `closed`; the new request owns a new status file and receives its prompt in a fresh SDK context.

`lastControlCommandState` is `received`, `completed`, or `failed`. A rejected command leaves the current session running and records an actionable `lastControlCommandError`. External tools should use a unique `commandId`, write commands atomically, and wait for the matching acknowledgement before issuing another command.

The external tool owns completion criteria, retry policy, follow-up actions, and any result files. TurboPilot does not parse prompts, attachments, or transcripts to make external workflow decisions, and `idle` means only that the chat session is idle. Each process must receive its own request and status paths. Write access to `sessionControlPath` grants control of that TurboPilot process, so place it in a caller-owned directory with appropriate filesystem permissions. Normal user-initiated window close keeps the existing confirmation and session-history choice. A valid `close` command is already explicit authorization and therefore bypasses those dialogs. Forcibly terminating the process can leave the last health record in place, so monitoring tools should also watch `processId`.


## Regression checks

Run the dependency-free checks from the repository root:

```powershell
dotnet run --project .\tests\TurboPilot.Tests
```

Additional checks:

```powershell
dotnet run --project .\tests\TurboPilot.Tests -- --runtime --ui
```

- `--runtime` exercises the SDK runtime with a temporary loopback provider.
- `--ui` drives the WPF and WebView2 controls.
- `--session-features` checks links, workspace changes, and session hand-offs.
- `--ui-close` checks exit confirmation and cleanup.
- `--cloud` performs an optional authenticated cloud smoke test and may consume credits.

## Related Projects

TurboPilot was created using the [TurbolandWPF theme](https://github.com/mighty-studios/TurbolandWPF) and the [Github Copilot SDK](https://github.com/github/copilot-sdk)

## Licensing

### Source code

Copyright 2026 Mighty Studios, LLC.  
All rights reserved.

Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License. You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.

See [LICENSE.md](LICENSE.md)

### Third-party font notice

This repository bundles the font **Px437 IBM VGA 9x16** by VileR.  
From [The Ultimate Oldschool PC Font Pack](https://int10h.org/oldschool-pc-fonts/),
used under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/).

The font is licensed separately under **CC BY-SA 4.0** and is not
covered by the Apache 2.0 license applied to this project's source code.

__Projects made using this Theme and the bundled Px437 IBM VGA 9x16 font should also credit VileR according to the CC BY-SA 4.0 terms__

## Artistic Dislaimer
This project is an independent, artistic tribute to the look of 1990s DOS text-mode IDEs. It is
not affiliated with nor endorsed by any IDE vendor who made similar looking commercial projects.

---  
  
>If you enjoy this project, please consider:

<a href="https://www.buymeacoffee.com/mighty_studios" target="_blank">
  <img src="https://cdn.buymeacoffee.com/buttons/default-yellow.png" alt="Buy Me A Coffee" height="41" width="174">
</a>

<small>(The joy I get from a free latte is incredible)</small> 

another output sample:
![sample](./sample.gif)