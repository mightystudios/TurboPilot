from __future__ import annotations

import json
from pathlib import Path
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))

from mighty_ai_tools.frontmatter import FrontmatterError, parse_frontmatter_text
from mighty_ai_tools.phase_suite import build_manifest, validate_suite


def phase_document(
    *,
    workspace: str,
    phase_id: str,
    phase_number: int,
    predecessors: list[str],
    successor: str | None,
) -> str:
    predecessor_value = "[" + ", ".join(f"'{item}'" for item in predecessors) + "]"
    successor_value = "null" if successor is None else f"'{successor}'"
    return f"""---
schema: mighty-phase-plan
version: '1.0'
roadmap_id: sample-roadmap
document_type: phase
phase_id: '{phase_id}'
phase_number: {phase_number}
title: 'Phase {phase_id}'
goal: 'Deliver phase {phase_id}'
predecessors: {predecessor_value}
successor: {successor_value}
master: 'master.md'
workspace: '{workspace}'
---

# Phase {phase_id}

## Outcome

Deliver a verified result.

## Context

- The repository source is authoritative.

## Scope

### In Scope

- Implement this phase.

### Out of Scope

- Implement later phases.

## Tasks

- [ ] P{phase_id}-T01: Implement the phase.

## Acceptance Criteria

- P{phase_id}-A01: The focused behavior works.

## Validation

- Run `python -m unittest` and record passing output.
"""


def write_valid_suite(root: Path) -> Path:
    workspace = str(root)
    master = root / "master.md"
    master.write_text(
        f"""---
schema: mighty-phase-plan
version: '1.0'
roadmap_id: sample-roadmap
document_type: phase-master
title: 'Sample Roadmap'
goal: 'Deliver a sample'
workspace: '{workspace}'
phase_glob: 'phase-*.md'
---

# Sample Roadmap

## Purpose

Deliver a verified sample.

## Global Constraints

- Preserve the public interface.

## Phase Order

`01 -> 02`

## Phase Plans

| Phase | Plan |
|---|---|
| 01 | [First](phase-01.md) |
| 02 | [Second](phase-02.md) |

## Validation

- Run `python -m unittest`.
""",
        encoding="ascii",
    )
    (root / "phase-01.md").write_text(
        phase_document(
            workspace=workspace,
            phase_id="01",
            phase_number=1,
            predecessors=[],
            successor="02",
        ),
        encoding="ascii",
    )
    (root / "phase-02.md").write_text(
        phase_document(
            workspace=workspace,
            phase_id="02",
            phase_number=2,
            predecessors=["01"],
            successor=None,
        ),
        encoding="ascii",
    )
    return master


class FrontmatterTests(unittest.TestCase):
    def test_supported_scalar_and_list_types(self) -> None:
        document = parse_frontmatter_text(
            """---
text: 'value'
integer: 7
enabled: true
missing: null
items: ['a', 2, false, null]
---
Body
"""
        )
        self.assertEqual(document.metadata["text"], "value")
        self.assertEqual(document.metadata["integer"], 7)
        self.assertIs(document.metadata["enabled"], True)
        self.assertIsNone(document.metadata["missing"])
        self.assertEqual(document.metadata["items"], ["a", 2, False, None])

    def test_nested_lists_are_rejected(self) -> None:
        with self.assertRaises(FrontmatterError):
            parse_frontmatter_text("---\nitems: [[1]]\n---\n")


class PhaseSuiteTests(unittest.TestCase):
    def test_valid_suite_builds_immutable_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            master = write_valid_suite(root)

            validation = validate_suite(master)
            output = root / "phase-manifest.json"
            manifest = build_manifest(
                master,
                output,
                generated_at="2026-10-20T00:00:00Z",
            )

            self.assertTrue(validation.valid, validation.to_dict())
            self.assertEqual(
                [phase["phaseId"] for phase in manifest["phases"]],
                ["01", "02"],
            )
            self.assertNotIn("currentPhase", manifest)
            self.assertNotIn("status", manifest["phases"][0])
            self.assertNotIn("startupQuestions", manifest["phases"][0])
            self.assertEqual(
                json.loads(output.read_text(encoding="ascii")),
                manifest,
            )

    def test_broken_relative_link_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            master = write_valid_suite(root)
            master.write_text(
                master.read_text(encoding="ascii").replace(
                    "[First](phase-01.md)",
                    "[First](missing.md)",
                ),
                encoding="ascii",
            )

            validation = validate_suite(master)

            self.assertIn("broken-link", {item.code for item in validation.errors})

    def test_nonsequential_predecessor_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            master = write_valid_suite(root)
            phase = root / "phase-02.md"
            phase.write_text(
                phase.read_text(encoding="ascii").replace(
                    "predecessors: ['01']",
                    "predecessors: []",
                ),
                encoding="ascii",
            )

            validation = validate_suite(master)

            self.assertIn(
                "sequential-predecessor",
                {item.code for item in validation.errors},
            )

    def test_phase_gap_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            master = write_valid_suite(root)
            phase = root / "phase-02.md"
            phase.write_text(
                phase.read_text(encoding="ascii")
                .replace("phase_id: '02'", "phase_id: '03'")
                .replace("phase_number: 2", "phase_number: 3"),
                encoding="ascii",
            )

            validation = validate_suite(master)

            self.assertIn("phase-number-gap", {item.code for item in validation.errors})

    def test_missing_acceptance_id_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            master = write_valid_suite(root)
            phase = root / "phase-01.md"
            phase.write_text(
                phase.read_text(encoding="ascii").replace(
                    "- P01-A01: The focused behavior works.",
                    "The focused behavior works.",
                ),
                encoding="ascii",
            )

            validation = validate_suite(master)

            self.assertIn(
                "missing-acceptance-ids",
                {item.code for item in validation.errors},
            )


if __name__ == "__main__":
    unittest.main()
