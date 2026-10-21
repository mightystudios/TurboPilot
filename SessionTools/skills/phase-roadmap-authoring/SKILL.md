---
name: phase-roadmap-authoring
description: 'Creates a concise master roadmap and self-contained sequential phase plans for large workloads. Use for phased planning, roadmap drafting, phase splitting, manifest generation, or plan repair before TurboPilot execution.'
---

# Phase Roadmap Authoring

Create a master summary plus detailed phase plans that can be executed in fresh
contexts without routine clarification.

## Workflow

1. Inspect the target repository and its instructions.
2. Copy `templates/master-plan.md` and replace every token.
3. Split the workload into sequential, outcome-oriented phases sized for about
   3-4 focused hours.
4. Copy `templates/phase-plan.md` for each phase.
5. Put all required context, constraints, scope, tasks, acceptance criteria, and
   exact validation commands in each phase.
6. Resolve ordinary implementation choices now. If authority is genuinely
   missing, mark the plan blocked instead of passing a question to the executor.
7. Validate the suite.
8. Build the manifest only after validation passes.

## Planning Rules

- Keep the master short; it is an index and source of global constraints.
- Make phase documents self-contained.
- Use one direct predecessor and successor so execution is strictly sequential.
- Give tasks and acceptance criteria unique IDs.
- Do not include startup questions or repeated execution protocol boilerplate.
- Do not implement product code while planning.
- Never hand-edit the generated manifest.

## Commands

```powershell
python "$env:TURBOPILOT_SESSION_TOOLS\skills\phase-roadmap-authoring\scripts\validate-phase-suite.py" --master "C:\work\plans\master.md"
python "$env:TURBOPILOT_SESSION_TOOLS\skills\phase-roadmap-authoring\scripts\build-phase-manifest.py" --master "C:\work\plans\master.md" --output "C:\work\plans\phase-manifest.json"
```

## Resources

- `templates/master-plan.md` is the summary roadmap scaffold.
- `templates/phase-plan.md` is the standalone execution scaffold.
