---
schema: mighty-phase-plan
version: '1.0'
roadmap_id: '{{ROADMAP_ID}}'
document_type: phase
phase_id: '{{PHASE_ID}}'
phase_number: {{PHASE_NUMBER}}
title: '{{PHASE_TITLE}}'
goal: '{{PHASE_GOAL}}'
predecessors: [{{PREDECESSOR_PHASE_IDS}}]
successor: {{SUCCESSOR_PHASE_ID_OR_NULL}}
master: '{{MASTER_FILENAME}}'
workspace: '{{ABSOLUTE_WINDOWS_WORKSPACE_PATH}}'
---

# Phase {{PHASE_ID}}: {{PHASE_TITLE}}

## Outcome

{{MEASURABLE_END_STATE}}

## Context

- {{SOURCE_PATH_OR_VERIFIED_FACT}}
- {{ARCHITECTURAL_CONSTRAINT}}
- {{DECISION_ALREADY_RESOLVED_FOR_EXECUTION}}

## Scope

### In Scope

- {{IN_SCOPE_RESULT}}

### Out of Scope

- {{EXPLICITLY_DEFERRED_WORK}}
- Work assigned to the successor phase.

## Tasks

- [ ] P{{PHASE_ID}}-T01: {{ATOMIC_TASK_WITH_FILE_OR_SYMBOL_TARGET}}
- [ ] P{{PHASE_ID}}-T02: {{ATOMIC_TASK_WITH_FILE_OR_SYMBOL_TARGET}}

## Acceptance Criteria

- P{{PHASE_ID}}-A01: {{OBSERVABLE_ACCEPTANCE_CRITERION}}
- P{{PHASE_ID}}-A02: {{OBSERVABLE_ACCEPTANCE_CRITERION}}

## Validation

- Run `{{EXACT_COMMAND}}` and record the relevant passing output.
- Run `{{EXACT_COMMAND}}` and record the relevant passing output.
