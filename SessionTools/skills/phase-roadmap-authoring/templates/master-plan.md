---
schema: mighty-phase-plan
version: '1.0'
roadmap_id: '{{ROADMAP_ID}}'
document_type: phase-master
title: '{{ROADMAP_TITLE}}'
goal: '{{ROADMAP_GOAL}}'
workspace: '{{ABSOLUTE_WINDOWS_WORKSPACE_PATH}}'
phase_glob: 'phase-*.md'
---

# {{ROADMAP_TITLE}}

## Purpose

{{ONE_PARAGRAPH_PURPOSE}}

## Global Constraints

- {{AUTHORITATIVE_SOURCE_OR_INVARIANT}}
- {{CROSS_PHASE_CONSTRAINT}}
- {{OWNERSHIP_OR_SAFETY_RULE}}

## Phase Order

`{{FIRST_PHASE_ID}} -> {{NEXT_PHASE_ID}} -> {{FINAL_PHASE_ID}}`

## Phase Plans

| Phase | Outcome | Plan |
|---|---|---|
| {{PHASE_ID}} | {{PHASE_OUTCOME}} | [Open](phase-{{PHASE_ID}}-{{PHASE_SLUG}}.md) |

## Validation

- Validate the plan suite before generating its manifest.
- Run `{{GLOBAL_VALIDATION_COMMAND}}` after all phases complete.
