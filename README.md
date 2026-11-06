<p align="center">
  <img src="./src/icon.png" alt="TurboPilot floppy disk icon" width="104">
</p>

<h1 align="center">TurboPilot</h1>

<p align="center">
  <strong>Retro-styled command center for serious AI coding sessions.</strong>
</p>

<p align="center">
  Bring GitHub Copilot or your own OpenAI-compatible model into a focused Windows workspace with rendered output, durable sessions, explicit permissions, and a distinctly 1990s soul.
</p>

<p align="center">
  <a href="https://github.com/mighty-studios/TurboPilot/stargazers"><img src="https://img.shields.io/github/stars/mighty-studios/TurboPilot?style=for-the-badge&logo=github&color=yellow" alt="GitHub stars"></a>
  <img src="https://img.shields.io/badge/Platform-Windows%2011-0078D4?style=for-the-badge&logo=windows11" alt="Windows 11">
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=for-the-badge&logo=dotnet" alt=".NET 9">
  <a href="./LICENSE.md"><img src="https://img.shields.io/badge/License-Apache%202.0-blue?style=for-the-badge" alt="Apache 2.0 license"></a>
</p>

![TurboPilot showing rendered Markdown, code, a Mermaid diagram, and an in-chat question](./screenshot.png)

TurboPilot answers a simple question: **what if modern AI coding agents had a great desktop interface in 1992?**

It keeps the model beside your editor without reducing the experience to terminal scrollback. You get a readable conversation, visible tool activity, workspace-aware links, session history, and control over what the agent may do.

> **If TurboPilot makes AI-assisted development more enjoyable, star the repository.** It is the easiest way to help other developers discover the project.

## Why TurboPilot?

### Use the model you want

Connect through the bundled GitHub Copilot runtime, or bring an OpenAI-compatible endpoint. Use a cloud model when you want frontier capability, or point TurboPilot at Lemonade, Ollama, vLLM, llama.cpp, Foundry Local, or another compatible local server.

### Read the work, not just the answer

Rendered output supports Markdown, syntax-highlighted code, Mermaid diagrams, local file links, image previews, questions, plans, tool details, and change summaries. The Raw tab keeps the exact transcript one click away.

### Stay in control

Set permissions for file access, shell commands, URLs, MCP tools, hooks, and other operations. After each turn, TurboPilot checks the workspace itself and reports changed files with options to inspect, compare, or undo Git-backed changes.

### Keep useful sessions

Resume saved conversations with their original model, mode, permissions, prompts, and customization state. Compact context without changing the visible transcript, or restart with a model-written hand-off when a provider or agent change requires a fresh runtime.

### Bring your own workflow

Load prompts, custom agents, skills, instructions, and MCP servers from standard customization locations or additional folders. TurboPilot also includes a phased planner, orchestrator, and executor for work that needs fresh contexts and durable hand-offs.

## What sets it apart

| Benefit | TurboPilot approach |
| --- | --- |
| **A real desktop workspace** | Native Windows UI designed to sit beside your editor, with menus, dialogs, previews, history, and keyboard shortcuts. |
| **Cloud or local models** | GitHub Copilot CLI and OpenAI-compatible BYOK servers share one session experience. |
| **Readable agent activity** | Tool calls collapse into a compact log, failures stay visible, and the final reply remains the focus. |
| **Long-session ergonomics** | Raw and Rendered views share a four-page sliding window instead of continuously rebuilding an unbounded transcript. |
| **Human control or autonomy** | Standard, Plan, Autopilot, and custom-agent modes combine with live permission rules. |
| **Durable history** | View, resume, search, summarize, export, or delete saved sessions without replaying prompts. |
| **Deep customization** | Instructions, skills, prompts, agents, MCP servers, typed commands, and inline references are first-class features. |
| **Retro without compromise** | The [TurbolandWPF](https://github.com/mighty-studios/TurbolandWPF) theme recreates the classic text-mode IDE feel with modern WPF and WebView2 rendering. |

## Quick start

> [!IMPORTANT]
> TurboPilot is an early alpha for Windows 11. There is not yet a packaged release, so the current installation path is building from source.

### Requirements

- Windows 11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Microsoft Edge WebView2 Runtime
- One model service:
  - An authenticated GitHub Copilot CLI profile
  - An OpenAI-compatible cloud or local endpoint
- Python 3.10 or later only for the bundled phase-planning workflow

### Build and run

```powershell
git clone --recurse-submodules https://github.com/mighty-studios/TurboPilot.git
cd TurboPilot
dotnet build .\TurboPilot.sln
dotnet run --project .\src\TurboPilot
```

### Start your first session

1. Choose **Session > Begin Session**.
2. Select the workspace the model may work in.
3. Choose **Copilot CLI** or **BYOK**, then query the service.
4. Pick a model, context tier, reasoning effort, and mode.
5. Press **Ctrl+Enter** or choose **Send**.

Use **Help > Show Help** for the complete guide to permissions, attachments, customizations, commands, session history, output paging, transcript export, and troubleshooting. Help opens in its own window, so it can stay visible while you work.

## A session workspace, not a chat box

TurboPilot is built around the full coding loop:

- Attach files with a picker, drag and drop, or pasted clipboard images.
- Reference pending files with `[file:name]` and loaded skills with `[skill:name]`.
- Answer model questions directly in the transcript.
- Follow task progress in the status bar and inspect tool details on demand.
- Open workspace files, links, PowerShell, Explorer, and Visual Studio Code without leaving the session.
- Save transcripts as styled HTML, Markdown, or plain text.
- Review cumulative changes before committing.
- Resume yesterday's session instead of reconstructing its context.

## Designed for both careful and autonomous work

**Standard** sessions keep approval boundaries visible. **Plan** mode is suited to exploration and strategy. **Autopilot** allows the runtime to proceed without operation prompts in trusted workspaces. Enabled custom agents appear alongside those built-in modes.

For larger jobs, the bundled phase workflow separates planning, orchestration, and execution:

```mermaid
flowchart LR
    Plan[Phase Planner] --> Roadmap[Validated Roadmap]
    Roadmap --> Orchestrate[Phase Orchestrator]
    Orchestrate --> Execute[Fresh Phase Executor]
    Execute --> Verify[Verified Result]
    Verify --> Orchestrate
```

## Development

Run the dependency-free checks:

```powershell
dotnet run --project .\tests\TurboPilot.Tests
```

Run the SDK runtime and real WPF/WebView2 regressions:

```powershell
dotnet run --project .\tests\TurboPilot.Tests -- --runtime --ui
```

The optional `--cloud` smoke test uses the authenticated cloud service and may consume credits.

## Project status

TurboPilot is actively developed and already supports end-to-end sessions, but it remains an alpha. Expect rapid changes while packaging, onboarding, and customization UX continue to improve.

Ideas, bug reports, and pull requests are welcome. If the project is useful or simply makes you smile, **star it and share it**.

## Related projects

- [TurbolandWPF](https://github.com/mighty-studios/TurbolandWPF) provides the retro Windows theme.
- [GitHub Copilot SDK](https://github.com/github/copilot-sdk) provides the cloud session runtime.
- [License](./LICENSE.md) and [third-party notices](./NOTICE.md) cover source and bundled assets.

<p align="center">
  <a href="https://www.buymeacoffee.com/mighty_studios">
    <img src="https://cdn.buymeacoffee.com/buttons/default-yellow.png" alt="Buy Mighty Studios a coffee" height="41" width="174">
  </a>
</p>

<p align="center"><strong>Modern AI. Classic interface. Your model, your workspace, your rules.</strong></p>
