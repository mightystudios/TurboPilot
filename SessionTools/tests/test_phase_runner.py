from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src"))

from mighty_ai_tools.phase_runner import (
    RunnerError,
    advance_phase,
    close_orchestration,
    complete_phase,
    inspect_orchestration,
    start_orchestration,
    update_progress,
)


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="ascii")


def write_manifest(root: Path) -> Path:
    for phase_id in ("01", "02"):
        (root / f"phase-{phase_id}.md").write_text(
            f"# Phase {phase_id}\n",
            encoding="ascii",
        )
    manifest = {
        "schemaVersion": "1.0",
        "generatedAt": "2026-10-20T00:00:00Z",
        "roadmapId": "runner-roadmap",
        "masterDocument": "master.md",
        "workspace": str(root),
        "phases": [
            {
                "phaseId": "01",
                "phaseNumber": 1,
                "title": "Implement",
                "goal": "Implement result",
                "predecessors": [],
                "successor": "02",
                "document": "phase-01.md",
            },
            {
                "phaseId": "02",
                "phaseNumber": 2,
                "title": "Verify",
                "goal": "Verify result",
                "predecessors": ["01"],
                "successor": None,
                "document": "phase-02.md",
            },
        ],
    }
    path = root / "phase-manifest.json"
    write_json(path, manifest)
    return path


def write_capabilities(root: Path, **overrides: object) -> Path:
    marker = {
        "contractVersion": "1.0",
        "launchArgument": "--launch-request",
        "requestTransport": "json-file",
        "statusTransport": "json-file",
        "supportsAttachments": True,
        "supportsSessionControl": True,
        "controlTransport": "json-file",
        "controlContractVersion": "1.0",
        "controlActions": ["replace", "close"],
        "controlDispositions": ["abandon", "summarize_and_store"],
        "sessionStates": [
            "starting",
            "connecting",
            "running",
            "waiting_user",
            "idle",
            "failed",
            "closing",
            "closed",
        ],
    }
    marker.update(overrides)
    path = root / "session-launch-capabilities.json"
    write_json(path, marker)
    return path


def candidate_result(phase_id: str = "01") -> dict[str, object]:
    return {
        "schemaVersion": "1.0",
        "roadmapId": "runner-roadmap",
        "phaseId": phase_id,
        "summary": f"Phase {phase_id} passed.",
        "acceptanceCriteria": [
            {
                "id": f"P{phase_id}-A01",
                "passed": True,
                "evidence": "Focused behavior passed.",
            }
        ],
        "validation": [
            {
                "command": "python -m unittest",
                "passed": True,
                "evidence": "The focused suite passed.",
            }
        ],
        "filesChanged": ["src\\feature.py"],
    }


def state_from(response: dict[str, object]) -> dict[str, object]:
    return json.loads(Path(str(response["statePath"])).read_text(encoding="ascii"))


def write_session_status(
    request_path: Path,
    *,
    state: str,
    process_id: int = 4321,
    updated_at: str = "2026-10-20T01:00:00Z",
    command_id: str | None = None,
    command_state: str | None = None,
) -> None:
    request = json.loads(request_path.read_text(encoding="ascii"))
    status = {
        "contractVersion": "1.0",
        "launchId": request["launchId"],
        "sessionLabel": request["sessionLabel"],
        "processId": process_id,
        "sessionId": "test-session",
        "workspace": request["workspace"],
        "state": state,
        "startedAt": "2026-10-20T00:00:00Z",
        "updatedAt": updated_at,
        "lastActivityAt": updated_at,
        "isWorking": state == "running",
        "hasPendingQuestion": state == "waiting_user",
        "error": "test failure" if state == "failed" else None,
        "lastControlCommandId": command_id,
        "lastControlCommandState": command_state,
        "lastControlCommandError": (
            "replacement failed" if command_state == "failed" else None
        ),
    }
    write_json(Path(request["sessionStatusPath"]), status)


class PhaseRunnerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.manifest = write_manifest(self.root)
        self.executable = self.root / "TurboPilot.exe"
        self.executable.write_bytes(b"test")
        write_capabilities(self.root)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def start(self, process_id: int = 4321) -> dict[str, object]:
        with mock.patch(
            "mighty_ai_tools.phase_runner._launch_process",
            return_value=process_id,
        ):
            return start_orchestration(
                self.manifest,
                self.executable,
            )

    def complete(self, response: dict[str, object], phase_id: str = "01") -> Path:
        candidate = self.root / f"candidate-{phase_id}.json"
        write_json(candidate, candidate_result(phase_id))
        return complete_phase(response["statePath"], candidate)

    def test_start_uses_native_control_contract(self) -> None:
        response = self.start()
        state = state_from(response)
        request = json.loads(
            Path(state["activeRequestPath"]).read_text(encoding="ascii")
        )

        self.assertFalse(response["reused"])
        self.assertEqual(state["processId"], 4321)
        self.assertEqual(state["activePhaseId"], "01")
        self.assertEqual(
            set(request),
            {
                "contractVersion",
                "launchId",
                "sessionLabel",
                "workspace",
                "attachments",
                "initialPrompt",
                "mode",
                "sessionStatusPath",
                "sessionControlPath",
            },
        )
        self.assertEqual(request["sessionControlPath"], state["controlPath"])
        self.assertIn("do not ask setup", request["initialPrompt"].lower())
        self.assertIn('python "', request["initialPrompt"])
        self.assertIn('--state "', request["initialPrompt"])
        self.assertIn("After completion, stop", request["initialPrompt"])

    def test_repeated_start_reuses_live_process(self) -> None:
        first = self.start()

        with mock.patch("mighty_ai_tools.phase_runner._launch_process") as launch:
            second = start_orchestration(
                self.manifest,
                self.executable,
                process_checker=lambda process_id: True,
            )

        self.assertEqual(second["statePath"], first["statePath"])
        self.assertTrue(second["reused"])
        self.assertEqual(second["processId"], 4321)
        launch.assert_not_called()

    def test_concurrent_start_launches_exactly_one_process(self) -> None:
        def delayed_launch(*args: object, **kwargs: object) -> int:
            time.sleep(0.1)
            return 4321

        with mock.patch(
            "mighty_ai_tools.phase_runner._launch_process",
            side_effect=delayed_launch,
        ) as launch:
            with ThreadPoolExecutor(max_workers=6) as executor:
                futures = [
                    executor.submit(
                        start_orchestration,
                        self.manifest,
                        self.executable,
                        "phase-executor.agent",
                        process_checker=lambda process_id: True,
                    )
                    for _ in range(6)
                ]
                results = [future.result() for future in futures]

        self.assertEqual(launch.call_count, 1)
        self.assertEqual(len({result["statePath"] for result in results}), 1)
        self.assertEqual(
            sum(not result["reused"] for result in results),
            1,
        )

    def test_dead_incomplete_process_restarts_same_phase_once(self) -> None:
        first = self.start()

        with mock.patch(
            "mighty_ai_tools.phase_runner._launch_process",
            return_value=9876,
        ) as launch:
            recovered = start_orchestration(
                self.manifest,
                self.executable,
                process_checker=lambda process_id: False,
            )

        self.assertEqual(recovered["activePhaseId"], "01")
        self.assertEqual(recovered["processId"], 9876)
        launch.assert_called_once()
        sessions = list(
            (
                self.root
                / ".mighty-ai-runs"
                / "runner-roadmap"
                / "sessions"
            ).iterdir()
        )
        self.assertEqual(len(sessions), 2)

    def test_start_rejects_missing_session_control_capability(self) -> None:
        write_capabilities(self.root, supportsSessionControl=False)

        with self.assertRaisesRegex(RunnerError, "supportsSessionControl"):
            self.start()

    def test_progress_and_completion_use_single_state(self) -> None:
        response = self.start()

        progress = update_progress(
            response["statePath"],
            "P01-T01",
            "Implemented the focused change.",
        )
        result_path = self.complete(response)
        state = state_from(response)

        self.assertEqual(progress["phaseState"], "running")
        self.assertEqual(state["phaseState"], "completed")
        self.assertTrue(result_path.is_file())
        result = json.loads(result_path.read_text(encoding="ascii"))
        self.assertIn("completedAt", result)
        self.assertEqual(result["phaseId"], "01")

    def test_repeated_completion_is_idempotent(self) -> None:
        response = self.start()

        first = self.complete(response)
        second = self.complete(response)

        self.assertEqual(first, second)

    def test_advance_writes_one_replace_command_and_reuses_process(self) -> None:
        response = self.start()
        self.complete(response)

        with mock.patch("mighty_ai_tools.phase_runner._launch_process") as launch:
            replacing = advance_phase(
                response["statePath"],
                timeout=0,
                process_checker=lambda process_id: True,
            )
            repeated = advance_phase(
                response["statePath"],
                timeout=0,
                process_checker=lambda process_id: True,
            )

        state = state_from(response)
        control = json.loads(Path(state["controlPath"]).read_text(encoding="ascii"))
        self.assertEqual(replacing["phaseState"], "replacing")
        self.assertEqual(repeated["phaseState"], "replacing")
        self.assertEqual(control["action"], "replace")
        self.assertEqual(control["disposition"], "abandon")
        self.assertEqual(control["targetProcessId"], 4321)
        self.assertEqual(
            control["commandId"],
            state["pendingReplacement"]["commandId"],
        )
        launch.assert_not_called()
        sessions = list(
            (
                self.root
                / ".mighty-ai-runs"
                / "runner-roadmap"
                / "sessions"
            ).iterdir()
        )
        self.assertEqual(len(sessions), 2)

    def test_status_promotes_confirmed_replacement(self) -> None:
        response = self.start()
        self.complete(response)
        advance_phase(
            response["statePath"],
            timeout=0,
            process_checker=lambda process_id: True,
        )
        state = state_from(response)
        pending = state["pendingReplacement"]
        write_session_status(Path(pending["requestPath"]), state="running")

        status = inspect_orchestration(
            response["statePath"],
            now=datetime(2026, 10, 20, 1, 0, tzinfo=timezone.utc),
            process_checker=lambda process_id: True,
        )
        promoted = state_from(response)

        self.assertEqual(status["classification"], "working")
        self.assertEqual(promoted["activePhaseId"], "02")
        self.assertEqual(promoted["completedPhases"], ["01"])
        self.assertIsNone(promoted["pendingReplacement"])
        self.assertEqual(promoted["processId"], 4321)

    def test_advance_recovers_successor_after_process_dies_during_replace(self) -> None:
        response = self.start()
        self.complete(response)
        advance_phase(
            response["statePath"],
            timeout=0,
            process_checker=lambda process_id: True,
        )

        with mock.patch(
            "mighty_ai_tools.phase_runner._launch_process",
            return_value=9876,
        ) as launch:
            recovered = advance_phase(
                response["statePath"],
                timeout=0,
                process_checker=lambda process_id: False,
            )

        state = state_from(response)
        self.assertEqual(recovered["activePhaseId"], "02")
        self.assertEqual(recovered["phaseState"], "starting")
        self.assertEqual(state["processId"], 9876)
        self.assertEqual(state["completedPhases"], ["01"])
        self.assertIsNone(state["pendingReplacement"])
        launch.assert_called_once()

    def test_final_advance_closes_same_process(self) -> None:
        response = self.start()
        self.complete(response)
        advance_phase(
            response["statePath"],
            timeout=0,
            process_checker=lambda process_id: True,
        )
        state = state_from(response)
        write_session_status(
            Path(state["pendingReplacement"]["requestPath"]),
            state="running",
        )
        inspect_orchestration(
            response["statePath"],
            process_checker=lambda process_id: True,
        )
        self.complete(response, phase_id="02")

        final = advance_phase(
            response["statePath"],
            process_checker=lambda process_id: True,
        )
        state = state_from(response)
        control = json.loads(Path(state["controlPath"]).read_text(encoding="ascii"))

        self.assertEqual(final["phaseState"], "closing")
        self.assertEqual(control["action"], "close")
        self.assertEqual(control["targetProcessId"], 4321)
        self.assertEqual(state["completedPhases"], ["01", "02"])

    def test_status_distinguishes_waiting_idle_and_stale_work(self) -> None:
        response = self.start()
        state = state_from(response)
        request_path = Path(state["activeRequestPath"])

        write_session_status(request_path, state="waiting_user")
        waiting = inspect_orchestration(
            response["statePath"],
            process_checker=lambda process_id: True,
        )
        self.assertEqual(waiting["classification"], "waiting_user")

        write_session_status(request_path, state="idle")
        idle = inspect_orchestration(
            response["statePath"],
            process_checker=lambda process_id: True,
        )
        self.assertEqual(idle["classification"], "idle_incomplete")

        write_session_status(
            request_path,
            state="running",
            updated_at="2026-10-20T00:00:00Z",
        )
        stale = inspect_orchestration(
            response["statePath"],
            idle_seconds=60,
            now=datetime(2026, 10, 20, 1, 0, tzinfo=timezone.utc),
            process_checker=lambda process_id: True,
        )
        self.assertEqual(stale["classification"], "stale_working")

    def test_close_is_idempotent(self) -> None:
        response = self.start()

        first = close_orchestration(
            response["statePath"],
            process_checker=lambda process_id: True,
        )
        second = close_orchestration(
            response["statePath"],
            process_checker=lambda process_id: True,
        )

        self.assertEqual(first["phaseState"], "closing")
        self.assertEqual(second["phaseState"], "closing")
        self.assertEqual(first["activeLaunchId"], second["activeLaunchId"])

    def test_closed_incomplete_run_restarts_same_phase(self) -> None:
        response = self.start()
        close_orchestration(
            response["statePath"],
            process_checker=lambda process_id: True,
        )
        inspect_orchestration(
            response["statePath"],
            process_checker=lambda process_id: False,
        )

        with mock.patch(
            "mighty_ai_tools.phase_runner._launch_process",
            return_value=8765,
        ) as launch:
            restarted = start_orchestration(
                self.manifest,
                self.executable,
                process_checker=lambda process_id: False,
            )

        self.assertEqual(restarted["activePhaseId"], "01")
        self.assertEqual(restarted["phaseState"], "starting")
        self.assertEqual(restarted["processId"], 8765)
        launch.assert_called_once()

    def test_closed_completed_run_advances_in_new_process(self) -> None:
        response = self.start()
        self.complete(response)
        close_orchestration(
            response["statePath"],
            process_checker=lambda process_id: True,
        )
        inspect_orchestration(
            response["statePath"],
            process_checker=lambda process_id: False,
        )

        with mock.patch(
            "mighty_ai_tools.phase_runner._launch_process",
            return_value=8765,
        ) as launch:
            advanced = advance_phase(
                response["statePath"],
                process_checker=lambda process_id: False,
            )

        self.assertEqual(advanced["activePhaseId"], "02")
        self.assertEqual(advanced["phaseState"], "starting")
        self.assertEqual(advanced["processId"], 8765)
        launch.assert_called_once()


if __name__ == "__main__":
    unittest.main()
