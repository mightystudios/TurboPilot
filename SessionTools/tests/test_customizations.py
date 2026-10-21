from __future__ import annotations

import json
from pathlib import Path
import sys
import unittest


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))

from mighty_ai_tools.frontmatter import parse_frontmatter


class CustomizationTests(unittest.TestCase):
    def test_only_required_skills_and_agents_are_published(self) -> None:
        self.assertEqual(
            {path.parent.name for path in (ROOT / "skills").glob("*/SKILL.md")},
            {"phase-roadmap-authoring", "phase-plan-execution"},
        )
        self.assertEqual(
            {path.name for path in (ROOT / "agents").glob("*.agent.md")},
            {
                "phase-planner.agent.md",
                "phase-orchestrator.agent.md",
                "phase-executor.agent.md",
            },
        )

    def test_skill_frontmatter_and_line_limits(self) -> None:
        for path in sorted((ROOT / "skills").glob("*/SKILL.md")):
            with self.subTest(skill=path.parent.name):
                document = parse_frontmatter(path)
                self.assertEqual(document.metadata.get("name"), path.parent.name)
                description = document.metadata.get("description")
                self.assertIsInstance(description, str)
                self.assertGreaterEqual(len(description), 10)
                self.assertLessEqual(len(description), 1024)
                self.assertLess(len(document.body.splitlines()), 500)

    def test_agents_match_three_role_workflow(self) -> None:
        expected = {
            "phase-planner.agent.md": (
                "TurboPilot Phase Planner",
                ["read", "search", "edit", "execute"],
            ),
            "phase-orchestrator.agent.md": (
                "TurboPilot Phase Orchestrator",
                ["read", "search", "execute"],
            ),
            "phase-executor.agent.md": (
                "TurboPilot Phase Executor",
                ["read", "search", "edit", "execute"],
            ),
        }
        for path in sorted((ROOT / "agents").glob("*.agent.md")):
            with self.subTest(agent=path.name):
                document = parse_frontmatter(path)
                name, tools = expected[path.name]
                self.assertEqual(document.metadata.get("name"), name)
                self.assertEqual(document.metadata.get("tools"), tools)
                self.assertLess(len(path.read_text(encoding="ascii")), 30000)

    def test_contracts_cover_only_required_artifacts(self) -> None:
        contracts = {
            path.name: json.loads(path.read_text(encoding="ascii"))
            for path in (ROOT / "contracts").glob("*.schema.json")
        }
        self.assertEqual(
            set(contracts),
            {
                "launch-request.schema.json",
                "orchestration-state.schema.json",
                "phase-manifest.schema.json",
                "phase-result.schema.json",
                "session-control.schema.json",
                "session-status.schema.json",
            },
        )
        launch_required = contracts["launch-request.schema.json"]["required"]
        self.assertIn("sessionControlPath", launch_required)
        self.assertNotIn("phaseStatusPath", launch_required)
        self.assertNotIn("resultPath", launch_required)
        control_actions = contracts["session-control.schema.json"]["properties"][
            "action"
        ]["enum"]
        self.assertEqual(control_actions, ["replace", "close"])

    def test_templates_have_no_startup_question_protocol(self) -> None:
        phase = (
            ROOT
            / "skills"
            / "phase-roadmap-authoring"
            / "templates"
            / "phase-plan.md"
        ).read_text(encoding="ascii")
        master = (
            ROOT
            / "skills"
            / "phase-roadmap-authoring"
            / "templates"
            / "master-plan.md"
        ).read_text(encoding="ascii")
        self.assertNotIn("Startup Questions", phase)
        self.assertNotIn("startup_questions", phase)
        self.assertIn("## Acceptance Criteria", phase)
        self.assertIn("## Phase Plans", master)

    def test_runtime_commands_use_bundled_environment_paths(self) -> None:
        documents = [
            ROOT / "README.md",
            ROOT / "agents" / "phase-planner.agent.md",
            ROOT / "agents" / "phase-orchestrator.agent.md",
            ROOT / "agents" / "phase-executor.agent.md",
            ROOT / "skills" / "phase-roadmap-authoring" / "SKILL.md",
            ROOT / "skills" / "phase-plan-execution" / "SKILL.md",
        ]
        combined = "\n".join(path.read_text(encoding="ascii") for path in documents)
        legacy_root = "D:" + r"\dev\MightyStudios\DevTools\AITools"
        self.assertIn("TURBOPILOT_SESSION_TOOLS", combined)
        self.assertIn("TURBOPILOT_EXECUTABLE", combined)
        self.assertNotIn(legacy_root, combined)

    def test_all_published_files_are_ascii(self) -> None:
        failures: list[str] = []
        for path in ROOT.rglob("*"):
            if not path.is_file():
                continue
            relative = path.relative_to(ROOT)
            if any(part in {".git", "__pycache__"} for part in relative.parts):
                continue
            if path.suffix in {".pyc", ".pyo"}:
                continue
            if not path.read_bytes().isascii():
                failures.append(str(relative))
        self.assertEqual(failures, [])


if __name__ == "__main__":
    unittest.main()
