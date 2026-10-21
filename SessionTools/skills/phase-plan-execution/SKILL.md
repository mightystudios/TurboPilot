---
name: phase-plan-execution
description: 'Executes one attached phase plan in a managed TurboPilot session, records progress, validates evidence, submits a durable result, and stops. Use only for the active phase selected by the Phase Orchestrator.'
---

# Phase Plan Execution

Own one phase from first read through verified result. The orchestrator, not the
executor, controls sessions and phase transitions.

## Execution

1. Read the attached phase document completely.
2. Inspect the repository context named by the plan.
3. Begin work immediately without setup or confirmation questions.
4. Use best engineering judgment for ordinary ambiguity.
5. Record progress after meaningful milestones:

```powershell
python "$env:TURBOPILOT_SESSION_TOOLS\src\TurboPilotPhaseRunner\phase-runner.py" progress --state "C:\path\orchestration-state.json" --task P01-T02 --message "Implemented the focused change; running validation."
```

6. Run every listed validation command.
7. Verify every acceptance criterion with observed evidence.
8. Create candidate result JSON using `templates/phase-result.json`.
9. Submit it:

```powershell
python "$env:TURBOPILOT_SESSION_TOOLS\src\TurboPilotPhaseRunner\phase-runner.py" complete --state "C:\path\orchestration-state.json" --result "C:\path\candidate-result.json"
```

10. Stop. Do not begin the successor.

## Blocking

- Ask only when a material product decision, missing credential, unavailable
  authority, or destructive action makes safe progress impossible.
- Do not ask how to perform routine engineering work.
- Do not launch another session, broaden phase scope, or hide failed validation.

## Gotchas

- The attached phase plan is the execution contract.
- Orchestration and TurboPilot sidecars are runner-owned.
- A phase is complete only after the runner accepts all passing evidence.
