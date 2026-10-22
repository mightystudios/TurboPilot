# TurboPilot Session Tools

TurboPilot Session Tools is the built-in, dependency-free Python 3 toolkit for
dividing a large workload into sequential phases and executing each phase with
a fresh context.

The workspace has three roles:

1. **TurboPilot Phase Planner** writes one master summary, detailed phase plans,
   and a validated manifest.
2. **TurboPilot Phase Orchestrator** owns one roadmap run, reviews progress and
   evidence, and controls phase transitions.
3. **TurboPilot Phase Executor** implements exactly one attached phase and then
   stops.

The orchestrator starts one TurboPilot process. Each phase transition uses
TurboPilot's native `replace` control command, which creates a fresh SDK session
in the same process and window. It does not launch another TurboPilot instance.

## Contents

- `agents`: the planner, orchestrator, and executor custom agents.
- `skills\phase-roadmap-authoring`: planning workflow and templates.
- `skills\phase-plan-execution`: one-phase execution workflow and result template.
- `src\mighty_ai_tools`: plan validation, manifest generation, and orchestration.
- `src\TurboPilotPhaseRunner\phase-runner.py`: bundle-local runner entry point.
- `contracts`: JSON contracts for generated and runtime state.
- `tests`: source-only standard-library `unittest` coverage, excluded from builds.

## Runtime Location

TurboPilot publishes this directory beside `TurboPilot.exe` and discovers its
agents and skills as built-in customizations. The built-in source cannot be
removed from customization settings, but individual items can be disabled.

TurboPilot exports these paths to every child session:

- `TURBOPILOT_SESSION_TOOLS`: this directory.
- `TURBOPILOT_EXECUTABLE`: the running TurboPilot executable.

## Requirements

- Python 3.10 or newer.
- A TurboPilot build that advertises session launch and control contract `1.0`.
- No third-party Python packages.

TurboPilot must support:

- `--launch-request`
- JSON request, status, and control files
- `replace` and `close` control actions
- `abandon` and `summarize_and_store` dispositions.

The built-in workflow uses these customizations:

- `phase-roadmap-authoring`
- `phase-plan-execution`
- `phase-planner.agent`
- `phase-orchestrator.agent`
- `phase-executor.agent`

## Plan a Roadmap

Use the TurboPilot Phase Planner. It creates the master and phase documents
inside the target workspace, then runs:

```powershell
python "$env:TURBOPILOT_SESSION_TOOLS\skills\phase-roadmap-authoring\scripts\validate-phase-suite.py" --master "C:\work\plans\master.md"
python "$env:TURBOPILOT_SESSION_TOOLS\skills\phase-roadmap-authoring\scripts\build-phase-manifest.py" --master "C:\work\plans\master.md" --output "C:\work\plans\phase-manifest.json"
```

Phase plans contain no startup-question checklist. The planner resolves routine
decisions before execution and records exact tasks, acceptance criteria, and
validation commands.

## Run the Roadmap

Start the first phase once:

```powershell
python "$env:TURBOPILOT_SESSION_TOOLS\src\TurboPilotPhaseRunner\phase-runner.py" start --manifest "C:\work\plans\phase-manifest.json" --exe "$env:TURBOPILOT_EXECUTABLE"
```

`start` prints the stable `orchestration-state.json` path. Use that path for all
remaining commands.

Inspect the active phase:

```powershell
python "$env:TURBOPILOT_SESSION_TOOLS\src\TurboPilotPhaseRunner\phase-runner.py" status --state "C:\work\plans\.mighty-ai-runs\roadmap-id\orchestration-state.json"
```

After the executor submits a verified result, review its scope and evidence,
then replace it with the successor:

```powershell
python "$env:TURBOPILOT_SESSION_TOOLS\src\TurboPilotPhaseRunner\phase-runner.py" advance --state "C:\work\plans\.mighty-ai-runs\roadmap-id\orchestration-state.json"
```

For the final phase, `advance` closes the managed executor. To stop earlier:

```powershell
python "$env:TURBOPILOT_SESSION_TOOLS\src\TurboPilotPhaseRunner\phase-runner.py" close --state "C:\work\plans\.mighty-ai-runs\roadmap-id\orchestration-state.json"
```

When review identifies a correctable issue in a completed active phase, replace
that session without advancing the roadmap:

```powershell
python "$env:TURBOPILOT_SESSION_TOOLS\src\TurboPilotPhaseRunner\phase-runner.py" rework --state "C:\work\plans\.mighty-ai-runs\roadmap-id\orchestration-state.json" --instructions "<specific corrective findings>"
```

## Executor Commands

The executor records milestone progress in the single orchestration state:

```powershell
python "$env:TURBOPILOT_SESSION_TOOLS\src\TurboPilotPhaseRunner\phase-runner.py" progress --state "C:\path\orchestration-state.json" --task P01-T02 --message "Implemented the change; running validation."
```

It submits a candidate result after every acceptance criterion and validation
command passes:

```powershell
python "$env:TURBOPILOT_SESSION_TOOLS\src\TurboPilotPhaseRunner\phase-runner.py" complete --state "C:\path\orchestration-state.json" --result "C:\path\candidate-result.json"
```

The orchestrator alone advances the roadmap.

## Reliability Model

- One stable run directory exists per roadmap ID.
- A filesystem lock serializes runner commands.
- Repeating `start` for a live or completed phase reuses existing state and does
  not create another process.
- `advance` writes one targeted TurboPilot `replace` command and reuses the
  recorded process ID.
- `rework` archives the rejected result and writes one targeted TurboPilot
  `replace` command for a corrective session of the same phase.
- Replacement requests use a new launch ID and status path while retaining one
  control path.
- Plans are immutable during execution; runtime progress lives in
  `orchestration-state.json`.
- A phase cannot advance until the runner accepts passing acceptance and
  validation evidence.
- TurboPilot session status is read-only to this toolkit.

## Runtime Files

| File | Writer | Purpose |
|---|---|---|
| `orchestration-state.json` | Phase runner | Active phase, process, progress, and transition state |
| `launch-request.json` | Phase runner | Native TurboPilot request for one fresh phase session |
| `session-status.json` | TurboPilot | Session lifecycle, heartbeat, and control acknowledgement |
| `session-control.json` | Phase runner | Single targeted `replace` or `close` command |
| `phase-XX.json` | Phase runner | Durable acceptance and validation evidence |

## Tests

Run the complete suite from the TurboPilot repository root:

```powershell
python -m unittest discover -s SessionTools\tests -v
```

Check command help:

```powershell
python "$env:TURBOPILOT_SESSION_TOOLS\skills\phase-roadmap-authoring\scripts\validate-phase-suite.py" --help
python "$env:TURBOPILOT_SESSION_TOOLS\skills\phase-roadmap-authoring\scripts\build-phase-manifest.py" --help
python "$env:TURBOPILOT_SESSION_TOOLS\src\TurboPilotPhaseRunner\phase-runner.py" --help
```

All published files remain ASCII.
