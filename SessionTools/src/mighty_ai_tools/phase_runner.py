"""Single-process orchestration for sequential TurboPilot phase sessions."""

from __future__ import annotations

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
from typing import Any, Callable, Iterator, Mapping, Sequence
from uuid import uuid4

from .phase_suite import atomic_write_json


SCHEMA_VERSION = "1.0"
SESSION_STATES = {
    "starting",
    "connecting",
    "running",
    "waiting_user",
    "idle",
    "failed",
    "closing",
    "closed",
}
CONTROL_STATES = {"received", "completed", "failed"}
PHASE_STATES = {
    "starting",
    "running",
    "completed",
    "replacing",
    "closing",
    "closed",
    "failed",
}
CONTROL_DISPOSITIONS = {"abandon", "summarize_and_store"}
REPLACEMENT_READY_STATES = {"connecting", "running", "waiting_user", "idle"}
_PROCESS_LOCK = threading.Lock()


class RunnerError(RuntimeError):
    """Raised when orchestration state or a TurboPilot contract is invalid."""


def start_orchestration(
    manifest_path: str | Path,
    executable_path: str | Path,
    mode: str = "phase-executor.agent",
    *,
    process_checker: Callable[[int], bool] | None = None,
) -> dict[str, Any]:
    manifest_file = Path(manifest_path).resolve()
    executable = Path(executable_path).resolve()
    manifest = _load_object(manifest_file, "phase manifest")
    _validate_manifest(manifest)
    _validate_capabilities(executable)
    selected_mode = _nonempty(mode, "mode")
    roadmap_id = _required_string(manifest, "roadmapId", "phase manifest")
    workspace = Path(
        _required_string(manifest, "workspace", "phase manifest")
    ).resolve()
    if not workspace.is_dir():
        raise RunnerError(f"phase workspace does not exist: {workspace}")

    root = manifest_file.parent / ".mighty-ai-runs"
    run_directory = root / _safe_name(roadmap_id)
    state_path = run_directory / "orchestration-state.json"
    checker = process_checker or _process_is_alive

    with _exclusive_lock(run_directory):
        if state_path.exists():
            state = _load_state(state_path)
            _validate_existing_run(
                state,
                manifest_file,
                executable,
                selected_mode,
                roadmap_id,
            )
            process_id = _positive_int_or_none(state.get("processId"))
            process_alive = process_id is not None and checker(process_id)
            active_result_exists = bool(state["activeResultPath"]) and Path(
                state["activeResultPath"]
            ).is_file()
            if (
                process_alive
                or state["phaseState"] == "completed"
                or (state["phaseState"] == "closed" and active_result_exists)
            ):
                return _state_response(state_path, state, reused=True)
            if state["phaseState"] == "replacing":
                raise RunnerError(
                    "replacement is pending but the recorded process is not running"
                )
            phase_id = _required_string(state, "activePhaseId", "orchestration state")
        else:
            phase = _ordered_phases(manifest)[0]
            phase_id = _required_string(phase, "phaseId", "first phase")
            run_directory.mkdir(parents=True, exist_ok=True)
            state = _new_state(
                roadmap_id=roadmap_id,
                manifest_path=manifest_file,
                executable_path=executable,
                run_directory=run_directory,
                mode=selected_mode,
                phase_id=phase_id,
            )

        control_path = Path(state["controlPath"])
        if control_path.exists():
            control_path.unlink()
        launch = _create_launch(
            manifest=manifest,
            manifest_path=manifest_file,
            state_path=state_path,
            state=state,
            phase_id=phase_id,
        )
        _activate_launch(state, launch, "starting")
        state["progress"] = _progress(
            None,
            "Launching a fresh TurboPilot executor session.",
        )
        atomic_write_json(state_path, state)
        try:
            process_id = _launch_process(
                executable,
                Path(launch["requestPath"]),
                workspace,
                state_path,
            )
        except OSError as error:
            state["phaseState"] = "failed"
            state["progress"] = _progress(None, f"TurboPilot launch failed: {error}")
            state["updatedAt"] = _utc_now()
            atomic_write_json(state_path, state)
            raise RunnerError(f"could not launch TurboPilot: {error}") from error
        state["processId"] = process_id
        state["phaseState"] = "starting"
        state["updatedAt"] = _utc_now()
        atomic_write_json(state_path, state)
        return _state_response(state_path, state, reused=False)


def update_progress(
    state_path: str | Path,
    task: str | None,
    message: str,
) -> dict[str, Any]:
    state_file = Path(state_path).resolve()
    with _exclusive_lock(state_file.parent):
        state = _load_state(state_file)
        if state["phaseState"] in {
            "completed",
            "replacing",
            "closing",
            "closed",
            "failed",
        }:
            raise RunnerError(
                f"cannot update progress while phaseState is {state['phaseState']!r}"
            )
        state["phaseState"] = "running"
        state["progress"] = _progress(task, _nonempty(message, "message"))
        state["updatedAt"] = _utc_now()
        atomic_write_json(state_file, state)
        return _state_response(state_file, state)


def complete_phase(
    state_path: str | Path,
    candidate_result_path: str | Path,
) -> Path:
    state_file = Path(state_path).resolve()
    candidate_file = Path(candidate_result_path).resolve()
    with _exclusive_lock(state_file.parent):
        state = _load_state(state_file)
        candidate = _load_object(candidate_file, "phase result candidate")
        _validate_result(candidate, require_completed_at=False)
        if candidate["roadmapId"] != state["roadmapId"]:
            raise RunnerError("result roadmapId does not match orchestration state")
        if candidate["phaseId"] != state["activePhaseId"]:
            raise RunnerError("result phaseId does not match the active phase")

        destination = Path(state["activeResultPath"]).resolve()
        if state["phaseState"] == "completed":
            existing = _load_object(destination, "stored phase result")
            comparable = dict(existing)
            comparable.pop("completedAt", None)
            supplied = dict(candidate)
            supplied.pop("completedAt", None)
            if comparable != supplied:
                raise RunnerError("the active phase already has a different result")
            return destination
        if state["phaseState"] not in {"starting", "running"}:
            raise RunnerError(
                f"cannot complete while phaseState is {state['phaseState']!r}"
            )
        result = dict(candidate)
        result["completedAt"] = _utc_now()
        _validate_result(result, require_completed_at=True)
        atomic_write_json(destination, result)
        state["phaseState"] = "completed"
        state["progress"] = _progress(None, str(result["summary"]))
        state["updatedAt"] = _utc_now()
        atomic_write_json(state_file, state)
        return destination


def advance_phase(
    state_path: str | Path,
    timeout: float = 30.0,
    poll_seconds: float = 0.25,
    *,
    process_checker: Callable[[int], bool] | None = None,
    sleeper: Callable[[float], None] = time.sleep,
) -> dict[str, Any]:
    if timeout < 0:
        raise RunnerError("timeout must be zero or greater")
    if poll_seconds <= 0:
        raise RunnerError("poll-seconds must be greater than zero")
    state_file = Path(state_path).resolve()
    checker = process_checker or _process_is_alive
    with _exclusive_lock(state_file.parent):
        state = _load_state(state_file)
        if state["pendingReplacement"] is not None:
            process_id = _positive_int_or_none(state.get("processId"))
            if process_id is None or not checker(process_id):
                state = _recover_pending_replacement(state_file, state)
                return _state_response(state_file, state)
            _ensure_pending_control(state)
            state = _wait_for_replacement(
                state_file,
                state,
                timeout,
                poll_seconds,
                sleeper,
            )
            return _state_response(state_file, state)
        if state["phaseState"] == "closing":
            return _state_response(state_file, state, reused=True)
        if state["phaseState"] not in {"completed", "closed"}:
            raise RunnerError("the active phase must have a validated result before advance")

        result_path = Path(state["activeResultPath"]).resolve()
        result = _load_object(result_path, "active phase result")
        _validate_result(result, require_completed_at=True)
        if (
            result["roadmapId"] != state["roadmapId"]
            or result["phaseId"] != state["activePhaseId"]
        ):
            raise RunnerError("stored phase result does not match orchestration state")
        manifest_path = Path(state["manifestPath"]).resolve()
        manifest = _load_object(manifest_path, "phase manifest")
        _validate_manifest(manifest)
        if manifest["roadmapId"] != state["roadmapId"]:
            raise RunnerError("phase manifest no longer matches orchestration state")
        phase = _phase_by_id(manifest, state["activePhaseId"])
        successor = phase.get("successor")
        completed = list(state["completedPhases"])
        if state["activePhaseId"] not in completed:
            completed.append(state["activePhaseId"])
        state["completedPhases"] = completed

        process_id = _positive_int_or_none(state.get("processId"))
        process_alive = process_id is not None and checker(process_id)
        if successor is None:
            if process_alive:
                _request_close(state, disposition="abandon")
            else:
                state["phaseState"] = "closed"
                state["progress"] = _progress(None, "Roadmap complete; executor is closed.")
                state["updatedAt"] = _utc_now()
                atomic_write_json(state_file, state)
            return _state_response(state_file, state)

        successor_record = _phase_by_id(manifest, successor)
        expected_predecessors = successor_record.get("predecessors")
        if expected_predecessors != [state["activePhaseId"]]:
            raise RunnerError("successor does not directly depend on the active phase")
        launch = _create_launch(
            manifest=manifest,
            manifest_path=manifest_path,
            state_path=state_file,
            state=state,
            phase_id=successor,
        )
        if not process_alive:
            _activate_launch(state, launch, "starting")
            state["progress"] = _progress(
                None,
                "Previous process ended; launching the successor in a new process.",
            )
            atomic_write_json(state_file, state)
            control_path = Path(state["controlPath"])
            if control_path.exists():
                control_path.unlink()
            try:
                state["processId"] = _launch_process(
                    Path(state["executablePath"]),
                    Path(launch["requestPath"]),
                    Path(manifest["workspace"]).resolve(),
                    state_file,
                )
            except OSError as error:
                state["phaseState"] = "failed"
                state["progress"] = _progress(
                    None,
                    f"Successor launch failed: {error}",
                )
                state["updatedAt"] = _utc_now()
                atomic_write_json(state_file, state)
                raise RunnerError(f"could not launch successor: {error}") from error
            state["updatedAt"] = _utc_now()
            atomic_write_json(state_file, state)
            return _state_response(state_file, state)

        pending = {
            "commandId": f"replace-{uuid4().hex}",
            "targetProcessId": process_id,
            "targetLaunchId": state["activeLaunchId"],
            "fromPhaseId": state["activePhaseId"],
            "toPhaseId": successor,
            "nextLaunchId": launch["launchId"],
            "requestPath": launch["requestPath"],
            "sessionStatusPath": launch["sessionStatusPath"],
            "resultPath": launch["resultPath"],
            "issuedAt": _utc_now(),
        }
        state["pendingReplacement"] = pending
        state["phaseState"] = "replacing"
        state["progress"] = _progress(
            None,
            f"Replacing Phase {pending['fromPhaseId']} with Phase {successor}.",
        )
        state["updatedAt"] = _utc_now()
        atomic_write_json(state_file, state)
        _ensure_pending_control(state)
        state = _wait_for_replacement(
            state_file,
            state,
            timeout,
            poll_seconds,
            sleeper,
        )
        return _state_response(state_file, state)


def inspect_orchestration(
    state_path: str | Path,
    idle_seconds: float = 300.0,
    *,
    now: datetime | None = None,
    process_checker: Callable[[int], bool] | None = None,
) -> dict[str, Any]:
    if idle_seconds <= 0:
        raise RunnerError("idle-seconds must be greater than zero")
    state_file = Path(state_path).resolve()
    checker = process_checker or _process_is_alive
    with _exclusive_lock(state_file.parent):
        state = _load_state(state_file)
        if state["pendingReplacement"] is not None:
            state, changed = _reconcile_replacement(state)
            if changed:
                atomic_write_json(state_file, state)

        process_id = _positive_int_or_none(state.get("processId"))
        process_alive = process_id is not None and checker(process_id)
        session_status = _load_active_session_status(state)
        if state["phaseState"] == "closing" and (
            not process_alive
            or (session_status is not None and session_status["state"] == "closed")
        ):
            state["phaseState"] = "closed"
            state["progress"] = _progress(None, "Executor closed.")
            state["updatedAt"] = _utc_now()
            atomic_write_json(state_file, state)

        classification, message = _classify(
            state,
            session_status,
            process_alive,
            idle_seconds,
            now or datetime.now(timezone.utc),
        )
        return {
            "classification": classification,
            "message": message,
            "statePath": str(state_file),
            "roadmapId": state["roadmapId"],
            "activePhaseId": state["activePhaseId"],
            "phaseState": state["phaseState"],
            "processId": process_id,
            "processAlive": process_alive,
            "sessionState": (
                session_status.get("state") if session_status is not None else None
            ),
            "sessionStatusPath": state["activeSessionStatusPath"],
            "resultPath": state["activeResultPath"],
            "completedPhases": list(state["completedPhases"]),
            "progress": dict(state["progress"]),
        }


def close_orchestration(
    state_path: str | Path,
    disposition: str = "abandon",
    *,
    process_checker: Callable[[int], bool] | None = None,
) -> dict[str, Any]:
    state_file = Path(state_path).resolve()
    checker = process_checker or _process_is_alive
    with _exclusive_lock(state_file.parent):
        state = _load_state(state_file)
        if state["pendingReplacement"] is not None:
            raise RunnerError("cannot close while a session replacement is pending")
        if state["phaseState"] == "closed":
            return _state_response(state_file, state, reused=True)
        process_id = _positive_int_or_none(state.get("processId"))
        if process_id is None or not checker(process_id):
            state["phaseState"] = "closed"
            state["progress"] = _progress(None, "Executor process is not running.")
            state["updatedAt"] = _utc_now()
            atomic_write_json(state_file, state)
            return _state_response(state_file, state)
        if state["phaseState"] != "closing":
            _request_close(state, disposition)
        return _state_response(state_file, state)


def _new_state(
    *,
    roadmap_id: str,
    manifest_path: Path,
    executable_path: Path,
    run_directory: Path,
    mode: str,
    phase_id: str,
) -> dict[str, Any]:
    now = _utc_now()
    return {
        "schemaVersion": SCHEMA_VERSION,
        "roadmapId": roadmap_id,
        "manifestPath": str(manifest_path),
        "executablePath": str(executable_path),
        "runDirectory": str(run_directory),
        "controlPath": str(run_directory / "session-control.json"),
        "mode": mode,
        "processId": None,
        "activePhaseId": phase_id,
        "activeLaunchId": "",
        "activeRequestPath": "",
        "activeSessionStatusPath": "",
        "activeResultPath": "",
        "phaseState": "starting",
        "completedPhases": [],
        "progress": _progress(None, "Orchestration created."),
        "pendingReplacement": None,
        "closeCommandId": None,
        "createdAt": now,
        "updatedAt": now,
    }


def _create_launch(
    *,
    manifest: Mapping[str, Any],
    manifest_path: Path,
    state_path: Path,
    state: Mapping[str, Any],
    phase_id: str,
) -> dict[str, str]:
    phase = _phase_by_id(manifest, phase_id)
    phase_document = _resolve_manifest_path(
        manifest_path,
        _required_string(phase, "document", f"phase {phase_id}"),
    )
    workspace = Path(_required_string(manifest, "workspace", "phase manifest")).resolve()
    if not phase_document.is_file():
        raise RunnerError(f"phase document does not exist: {phase_document}")
    try:
        phase_document.relative_to(workspace)
    except ValueError as error:
        raise RunnerError(
            f"phase document must be contained by workspace: {phase_document}"
        ) from error

    launch_id = (
        f"{_safe_name(str(state['roadmapId']))[:80]}-phase-"
        f"{phase_id[-20:]}-{uuid4().hex[:12]}"
    )
    session_directory = Path(state["runDirectory"]) / "sessions" / launch_id
    session_directory.mkdir(parents=True, exist_ok=False)
    request_path = session_directory / "launch-request.json"
    session_status_path = session_directory / "session-status.json"
    result_path = Path(state["runDirectory"]) / "results" / f"phase-{phase_id}.json"
    runner_path = (
        Path(__file__).resolve().parents[1]
        / "TurboPilotPhaseRunner"
        / "phase-runner.py"
    )
    request = {
        "contractVersion": SCHEMA_VERSION,
        "launchId": launch_id,
        "sessionLabel": (
            f"{state['roadmapId']} Phase {phase_id}: {phase.get('title', '')}"
        ).strip(),
        "workspace": str(workspace),
        "attachments": [str(phase_document)],
        "initialPrompt": _initial_prompt(
            roadmap_id=str(state["roadmapId"]),
            phase_id=phase_id,
            phase_document=phase_document,
            manifest_path=manifest_path,
            state_path=state_path,
            result_path=result_path,
            runner_path=runner_path,
        ),
        "mode": state["mode"],
        "sessionStatusPath": str(session_status_path),
        "sessionControlPath": state["controlPath"],
    }
    _validate_launch_request(request)
    atomic_write_json(request_path, request)
    return {
        "phaseId": phase_id,
        "launchId": launch_id,
        "requestPath": str(request_path),
        "sessionStatusPath": str(session_status_path),
        "resultPath": str(result_path),
    }


def _activate_launch(
    state: dict[str, Any],
    launch: Mapping[str, str],
    phase_state: str,
) -> None:
    state["activePhaseId"] = launch["phaseId"]
    state["activeLaunchId"] = launch["launchId"]
    state["activeRequestPath"] = launch["requestPath"]
    state["activeSessionStatusPath"] = launch["sessionStatusPath"]
    state["activeResultPath"] = launch["resultPath"]
    state["phaseState"] = phase_state
    state["pendingReplacement"] = None
    state["closeCommandId"] = None
    state["updatedAt"] = _utc_now()


def _initial_prompt(
    *,
    roadmap_id: str,
    phase_id: str,
    phase_document: Path,
    manifest_path: Path,
    state_path: Path,
    result_path: Path,
    runner_path: Path,
) -> str:
    return (
        f"Execute Phase {phase_id} only for roadmap {roadmap_id}. The complete phase plan is "
        f"attached at {phase_document}; read it and begin immediately. Use the manifest at "
        f"{manifest_path} only for roadmap context. Do not ask setup, confirmation, or routine "
        "implementation questions. Resolve minor ambiguity from the plan and repository using "
        "best engineering judgment. Ask the user only when a material decision or unavailable "
        "authority makes safe progress impossible. Stay inside the phase scope and do not start "
        "its successor. Report milestone progress with: "
        f'python "{runner_path}" progress --state "{state_path}" --task <task-id> --message '
        '"<plain-language-progress>". Run every validation command and verify each acceptance '
        "criterion. Create a candidate result JSON with schemaVersion, roadmapId, phaseId, "
        "summary, acceptanceCriteria, validation, and filesChanged, then submit it with: "
        f'python "{runner_path}" complete --state "{state_path}" --result "<candidate-path>". '
        f"The runner writes the durable result to {result_path}. After completion, stop and "
        "wait for the orchestrator to replace this session."
    )


def _request_close(state: dict[str, Any], disposition: str) -> None:
    selected_disposition = _nonempty(disposition, "disposition").lower()
    if selected_disposition not in CONTROL_DISPOSITIONS:
        raise RunnerError(
            f"disposition must be one of {sorted(CONTROL_DISPOSITIONS)}"
        )
    process_id = _positive_int_or_none(state.get("processId"))
    if process_id is None:
        raise RunnerError("cannot close without a recorded processId")
    command_id = state.get("closeCommandId") or f"close-{uuid4().hex}"
    state["closeCommandId"] = command_id
    state["phaseState"] = "closing"
    state["progress"] = _progress(None, "Closing the TurboPilot executor.")
    state["updatedAt"] = _utc_now()
    state_path = Path(state["runDirectory"]) / "orchestration-state.json"
    atomic_write_json(state_path, state)
    atomic_write_json(
        state["controlPath"],
        {
            "contractVersion": SCHEMA_VERSION,
            "commandId": command_id,
            "targetProcessId": process_id,
            "targetLaunchId": state["activeLaunchId"],
            "action": "close",
            "disposition": selected_disposition,
        },
    )


def _ensure_pending_control(state: Mapping[str, Any]) -> None:
    pending = state.get("pendingReplacement")
    if not isinstance(pending, dict):
        raise RunnerError("pending replacement is missing")
    command = {
        "contractVersion": SCHEMA_VERSION,
        "commandId": pending["commandId"],
        "targetProcessId": pending["targetProcessId"],
        "targetLaunchId": pending["targetLaunchId"],
        "action": "replace",
        "disposition": "abandon",
        "launchRequestPath": pending["requestPath"],
    }
    control_path = Path(state["controlPath"])
    existing = _load_optional_object(control_path, "session control")
    if existing != command:
        atomic_write_json(control_path, command)


def _wait_for_replacement(
    state_path: Path,
    state: dict[str, Any],
    timeout: float,
    poll_seconds: float,
    sleeper: Callable[[float], None],
) -> dict[str, Any]:
    deadline = time.monotonic() + timeout
    while True:
        state, changed = _reconcile_replacement(state)
        if changed:
            atomic_write_json(state_path, state)
        if state["pendingReplacement"] is None or time.monotonic() >= deadline:
            return state
        sleeper(poll_seconds)


def _recover_pending_replacement(
    state_path: Path,
    state: dict[str, Any],
) -> dict[str, Any]:
    pending = state.get("pendingReplacement")
    if not isinstance(pending, dict):
        raise RunnerError("pending replacement is missing")
    manifest = _load_object(Path(state["manifestPath"]), "phase manifest")
    _validate_manifest(manifest)
    successor = str(pending["toPhaseId"])
    _promote_pending(state, pending)
    launch = _create_launch(
        manifest=manifest,
        manifest_path=Path(state["manifestPath"]),
        state_path=state_path,
        state=state,
        phase_id=successor,
    )
    _activate_launch(state, launch, "starting")
    state["progress"] = _progress(
        None,
        "Managed process ended during replacement; launching the successor.",
    )
    state["updatedAt"] = _utc_now()
    control_path = Path(state["controlPath"])
    if control_path.exists():
        control_path.unlink()
    atomic_write_json(state_path, state)
    try:
        state["processId"] = _launch_process(
            Path(state["executablePath"]),
            Path(state["activeRequestPath"]),
            Path(manifest["workspace"]).resolve(),
            state_path,
        )
    except OSError as error:
        state["phaseState"] = "failed"
        state["progress"] = _progress(
            None,
            f"Successor recovery launch failed: {error}",
        )
        state["updatedAt"] = _utc_now()
        atomic_write_json(state_path, state)
        raise RunnerError(f"could not recover successor launch: {error}") from error
    state["updatedAt"] = _utc_now()
    atomic_write_json(state_path, state)
    return state


def _reconcile_replacement(
    state: dict[str, Any],
) -> tuple[dict[str, Any], bool]:
    pending = state.get("pendingReplacement")
    if not isinstance(pending, dict):
        return state, False
    next_request = _load_object(Path(pending["requestPath"]), "replacement request")
    next_status = _load_optional_object(
        Path(pending["sessionStatusPath"]),
        "replacement session status",
    )
    old_status = _load_optional_object(
        Path(state["activeSessionStatusPath"]),
        "active session status",
    )
    if next_status is not None:
        _validate_session_status(next_status, next_request)
    if old_status is not None:
        old_request = _load_object(
            Path(state["activeRequestPath"]),
            "active launch request",
        )
        _validate_session_status(old_status, old_request)

    command_id = pending["commandId"]
    old_control_state = (
        old_status.get("lastControlCommandState")
        if old_status is not None
        and old_status.get("lastControlCommandId") == command_id
        else None
    )
    next_state = next_status.get("state") if next_status is not None else None
    if next_state in {"failed", "closed"} or old_control_state == "failed":
        _promote_pending(state, pending)
        state["phaseState"] = "failed"
        error = (
            next_status.get("error")
            if next_status is not None and next_status.get("error")
            else old_status.get("lastControlCommandError")
            if old_status is not None
            else None
        )
        state["progress"] = _progress(
            None,
            f"TurboPilot replacement failed: {error or 'unknown error'}",
        )
        state["updatedAt"] = _utc_now()
        return state, True
    if next_state in REPLACEMENT_READY_STATES or old_control_state == "completed":
        _promote_pending(state, pending)
        state["phaseState"] = (
            "starting" if next_state in {None, "starting", "connecting"} else "running"
        )
        state["progress"] = _progress(
            None,
            f"Phase {state['activePhaseId']} executor session started.",
        )
        state["updatedAt"] = _utc_now()
        return state, True
    return state, False


def _promote_pending(
    state: dict[str, Any],
    pending: Mapping[str, Any],
) -> None:
    completed = list(state["completedPhases"])
    if pending["fromPhaseId"] not in completed:
        completed.append(pending["fromPhaseId"])
    state["completedPhases"] = completed
    state["activePhaseId"] = pending["toPhaseId"]
    state["activeLaunchId"] = pending["nextLaunchId"]
    state["activeRequestPath"] = pending["requestPath"]
    state["activeSessionStatusPath"] = pending["sessionStatusPath"]
    state["activeResultPath"] = pending["resultPath"]
    state["pendingReplacement"] = None
    state["closeCommandId"] = None


def _load_active_session_status(
    state: Mapping[str, Any],
) -> dict[str, Any] | None:
    path = Path(state["activeSessionStatusPath"])
    status = _load_optional_object(path, "active session status")
    if status is None:
        return None
    request = _load_object(Path(state["activeRequestPath"]), "active launch request")
    _validate_session_status(status, request)
    return status


def _classify(
    state: Mapping[str, Any],
    session_status: Mapping[str, Any] | None,
    process_alive: bool,
    idle_seconds: float,
    now: datetime,
) -> tuple[str, str]:
    phase_state = state["phaseState"]
    if phase_state == "completed":
        return "phase_complete", "The phase result is ready for orchestrator review."
    if phase_state == "replacing":
        return "replacing", "TurboPilot is replacing the completed phase session."
    if phase_state == "closing":
        return "closing", "TurboPilot is closing."
    if phase_state == "closed":
        return "closed", "The orchestration is closed."
    if phase_state == "failed":
        return "failed", str(state["progress"]["message"])
    if not process_alive:
        return "dead_process", "The recorded TurboPilot process is not running."
    if session_status is None:
        return "starting", "TurboPilot has not written session status yet."

    session_state = session_status["state"]
    if session_state == "failed":
        return "failed", str(session_status.get("error") or "TurboPilot failed.")
    if session_state == "closed":
        return "closed_incomplete", "TurboPilot closed before phase completion."
    if session_state == "waiting_user":
        return "waiting_user", "A material user decision is required."
    if session_status["isWorking"]:
        updated = _parse_timestamp(str(session_status["updatedAt"]))
        if (now - updated).total_seconds() > idle_seconds:
            return "stale_working", "TurboPilot stopped heartbeating while working."
        return "working", "TurboPilot is working on the active phase."
    if session_state == "idle":
        return "idle_incomplete", "TurboPilot is idle without a completed phase result."
    return session_state, f"TurboPilot session is {session_state}."


def _validate_existing_run(
    state: Mapping[str, Any],
    manifest_path: Path,
    executable_path: Path,
    mode: str,
    roadmap_id: str,
) -> None:
    if Path(state["manifestPath"]).resolve() != manifest_path:
        raise RunnerError("run directory belongs to a different manifest")
    if Path(state["executablePath"]).resolve() != executable_path:
        raise RunnerError("run directory belongs to a different TurboPilot executable")
    if state["mode"] != mode:
        raise RunnerError("run directory belongs to a different TurboPilot mode")
    if state["roadmapId"] != roadmap_id:
        raise RunnerError("run directory belongs to a different roadmap")


def _validate_capabilities(executable: Path) -> None:
    if not executable.is_file():
        raise RunnerError(f"TurboPilot executable does not exist: {executable}")
    marker_path = executable.parent / "session-launch-capabilities.json"
    marker = _load_object(marker_path, "TurboPilot capability marker")
    expected_scalars = {
        "contractVersion": SCHEMA_VERSION,
        "launchArgument": "--launch-request",
        "requestTransport": "json-file",
        "statusTransport": "json-file",
        "supportsAttachments": True,
        "supportsSessionControl": True,
        "controlTransport": "json-file",
        "controlContractVersion": SCHEMA_VERSION,
    }
    for key, expected in expected_scalars.items():
        if marker.get(key) != expected:
            raise RunnerError(
                f"TurboPilot capability {key} must be {expected!r}"
            )
    _require_capability_values(marker, "controlActions", {"replace", "close"})
    _require_capability_values(
        marker,
        "controlDispositions",
        CONTROL_DISPOSITIONS,
    )
    _require_capability_values(marker, "sessionStates", SESSION_STATES)


def _require_capability_values(
    marker: Mapping[str, Any],
    key: str,
    required: set[str],
) -> None:
    values = marker.get(key)
    if not isinstance(values, list) or any(not isinstance(value, str) for value in values):
        raise RunnerError(f"TurboPilot capability {key} must be a string array")
    missing = sorted(required - set(values))
    if missing:
        raise RunnerError(f"TurboPilot capability {key} is missing {missing}")


def _validate_manifest(manifest: Mapping[str, Any]) -> None:
    if manifest.get("schemaVersion") != SCHEMA_VERSION:
        raise RunnerError("phase manifest schemaVersion must be '1.0'")
    _required_string(manifest, "roadmapId", "phase manifest")
    _required_string(manifest, "masterDocument", "phase manifest")
    _required_string(manifest, "workspace", "phase manifest")
    phases = manifest.get("phases")
    if not isinstance(phases, list) or not phases:
        raise RunnerError("phase manifest phases must be a nonempty array")
    seen: set[str] = set()
    seen_numbers: set[int] = set()
    for phase in phases:
        if not isinstance(phase, dict):
            raise RunnerError("each phase manifest record must be an object")
        phase_id = _required_string(phase, "phaseId", "phase record")
        if not _is_phase_id(phase_id) or phase_id in seen:
            raise RunnerError(f"invalid or duplicate phaseId {phase_id!r}")
        seen.add(phase_id)
        phase_number = phase.get("phaseNumber")
        if not isinstance(phase_number, int) or isinstance(phase_number, bool):
            raise RunnerError(f"phase {phase_id!r} phaseNumber must be an integer")
        if phase_number in seen_numbers:
            raise RunnerError(f"duplicate phaseNumber {phase_number!r}")
        seen_numbers.add(phase_number)
        if phase_id != f"{phase_number:02d}":
            raise RunnerError(
                f"phase {phase_id!r} does not match phaseNumber {phase_number}"
            )
        _required_string(phase, "document", f"phase {phase_id!r}")
        predecessors = phase.get("predecessors")
        if not isinstance(predecessors, list) or any(
            not isinstance(value, str) for value in predecessors
        ):
            raise RunnerError(f"phase {phase_id!r} predecessors must be a string array")
        successor = phase.get("successor")
        if successor is not None and not isinstance(successor, str):
            raise RunnerError(f"phase {phase_id!r} successor must be a string or null")
    ordered = sorted(phases, key=lambda phase: phase["phaseNumber"])
    numbers = [phase["phaseNumber"] for phase in ordered]
    if numbers != list(range(numbers[0], numbers[0] + len(numbers))):
        raise RunnerError("phase manifest phase numbers must be contiguous")
    for index, phase in enumerate(ordered):
        expected_predecessors = [] if index == 0 else [ordered[index - 1]["phaseId"]]
        expected_successor = (
            ordered[index + 1]["phaseId"] if index + 1 < len(ordered) else None
        )
        if phase["predecessors"] != expected_predecessors:
            raise RunnerError(
                f"phase {phase['phaseId']!r} is not sequentially linked"
            )
        if phase["successor"] != expected_successor:
            raise RunnerError(
                f"phase {phase['phaseId']!r} has an invalid successor"
            )


def _validate_launch_request(request: Mapping[str, Any]) -> None:
    required = {
        "contractVersion",
        "launchId",
        "sessionLabel",
        "workspace",
        "attachments",
        "initialPrompt",
        "mode",
        "sessionStatusPath",
        "sessionControlPath",
    }
    if set(request) != required:
        raise RunnerError("launch request fields do not match TurboPilot contract 1.0")
    if request.get("contractVersion") != SCHEMA_VERSION:
        raise RunnerError("launch request contractVersion must be '1.0'")
    for key in required - {"contractVersion", "attachments"}:
        _required_string(request, key, "launch request")
    attachments = request.get("attachments")
    if not isinstance(attachments, list) or any(
        not isinstance(value, str) or not value.strip() for value in attachments
    ):
        raise RunnerError("launch request attachments must be a string array")


def _validate_session_status(
    status: Mapping[str, Any],
    request: Mapping[str, Any],
) -> None:
    required = {
        "contractVersion",
        "launchId",
        "sessionLabel",
        "processId",
        "sessionId",
        "workspace",
        "state",
        "startedAt",
        "updatedAt",
        "lastActivityAt",
        "isWorking",
        "hasPendingQuestion",
        "error",
    }
    control_fields = {
        "lastControlCommandId",
        "lastControlCommandState",
        "lastControlCommandError",
    }
    fields = set(status)
    if not required.issubset(fields) or fields - required - control_fields:
        raise RunnerError("session status fields do not match TurboPilot contract 1.0")
    present_control = fields & control_fields
    if present_control and present_control != control_fields:
        raise RunnerError("session status control acknowledgement is incomplete")
    if status.get("contractVersion") != SCHEMA_VERSION:
        raise RunnerError("session status contractVersion must be '1.0'")
    for key in ("launchId", "sessionLabel", "workspace"):
        if status.get(key) != request.get(key):
            raise RunnerError(f"session status {key} does not match launch request")
    if status.get("state") not in SESSION_STATES:
        raise RunnerError(f"invalid session state {status.get('state')!r}")
    if _positive_int_or_none(status.get("processId")) is None:
        raise RunnerError("session status processId must be a positive integer")
    session_id = status.get("sessionId")
    if session_id is not None and (
        not isinstance(session_id, str) or not session_id.strip()
    ):
        raise RunnerError("session status sessionId must be a nonempty string or null")
    if not isinstance(status.get("isWorking"), bool):
        raise RunnerError("session status isWorking must be a boolean")
    if not isinstance(status.get("hasPendingQuestion"), bool):
        raise RunnerError("session status hasPendingQuestion must be a boolean")
    if status.get("error") is not None and not isinstance(status.get("error"), str):
        raise RunnerError("session status error must be a string or null")
    for key in ("startedAt", "updatedAt", "lastActivityAt"):
        _parse_timestamp(_required_string(status, key, "session status"))
    if present_control:
        for key in ("lastControlCommandId", "lastControlCommandError"):
            value = status.get(key)
            if value is not None and (
                not isinstance(value, str) or not value.strip()
            ):
                raise RunnerError(
                    f"session status {key} must be a nonempty string or null"
                )
        control_state = status.get("lastControlCommandState")
        if control_state is not None and control_state not in CONTROL_STATES:
            raise RunnerError("session status control state is invalid")
        if (
            control_state == "failed"
            and not isinstance(status.get("lastControlCommandError"), str)
        ):
            raise RunnerError(
                "failed session control requires lastControlCommandError"
            )


def _validate_result(
    result: Mapping[str, Any],
    *,
    require_completed_at: bool,
) -> None:
    required = {
        "schemaVersion",
        "roadmapId",
        "phaseId",
        "summary",
        "acceptanceCriteria",
        "validation",
        "filesChanged",
    }
    allowed = required | {"completedAt"}
    if not required.issubset(result) or set(result) - allowed:
        raise RunnerError("phase result fields are invalid")
    if require_completed_at and "completedAt" not in result:
        raise RunnerError("stored phase result requires completedAt")
    if result.get("schemaVersion") != SCHEMA_VERSION:
        raise RunnerError("phase result schemaVersion must be '1.0'")
    _required_string(result, "roadmapId", "phase result")
    phase_id = _required_string(result, "phaseId", "phase result")
    if not _is_phase_id(phase_id):
        raise RunnerError("phase result phaseId must contain at least two digits")
    _required_string(result, "summary", "phase result")
    if "completedAt" in result:
        _parse_timestamp(_required_string(result, "completedAt", "phase result"))

    acceptance = result.get("acceptanceCriteria")
    if not isinstance(acceptance, list) or not acceptance:
        raise RunnerError("acceptanceCriteria must be a nonempty array")
    for item in acceptance:
        if not isinstance(item, dict) or set(item) != {"id", "passed", "evidence"}:
            raise RunnerError("acceptance criterion fields are invalid")
        _required_string(item, "id", "acceptance criterion")
        _required_string(item, "evidence", "acceptance criterion")
        if item.get("passed") is not True:
            raise RunnerError("every acceptance criterion must pass")

    validation = result.get("validation")
    if not isinstance(validation, list) or not validation:
        raise RunnerError("validation must be a nonempty array")
    for item in validation:
        if not isinstance(item, dict) or set(item) != {
            "command",
            "passed",
            "evidence",
        }:
            raise RunnerError("validation record fields are invalid")
        _required_string(item, "command", "validation record")
        _required_string(item, "evidence", "validation record")
        if item.get("passed") is not True:
            raise RunnerError("every validation record must pass")

    files_changed = result.get("filesChanged")
    if not isinstance(files_changed, list) or any(
        not isinstance(value, str) or not value.strip() for value in files_changed
    ):
        raise RunnerError("filesChanged must be a string array")
    if len(files_changed) != len(set(files_changed)):
        raise RunnerError("filesChanged must not contain duplicates")


def _load_state(path: Path) -> dict[str, Any]:
    state = _load_object(path, "orchestration state")
    required = {
        "schemaVersion",
        "roadmapId",
        "manifestPath",
        "executablePath",
        "runDirectory",
        "controlPath",
        "mode",
        "processId",
        "activePhaseId",
        "activeLaunchId",
        "activeRequestPath",
        "activeSessionStatusPath",
        "activeResultPath",
        "phaseState",
        "completedPhases",
        "progress",
        "pendingReplacement",
        "closeCommandId",
        "createdAt",
        "updatedAt",
    }
    if set(state) != required:
        raise RunnerError("orchestration state fields are invalid")
    if state.get("schemaVersion") != SCHEMA_VERSION:
        raise RunnerError("orchestration state schemaVersion must be '1.0'")
    for key in (
        "roadmapId",
        "manifestPath",
        "executablePath",
        "runDirectory",
        "controlPath",
        "mode",
        "activePhaseId",
        "activeLaunchId",
        "activeRequestPath",
        "activeSessionStatusPath",
        "activeResultPath",
    ):
        _required_string(state, key, "orchestration state")
    if not _is_phase_id(state["activePhaseId"]):
        raise RunnerError("orchestration state activePhaseId is invalid")
    process_id = state.get("processId")
    if process_id is not None and _positive_int_or_none(process_id) is None:
        raise RunnerError("orchestration state processId must be positive or null")
    if state["phaseState"] not in PHASE_STATES:
        raise RunnerError(f"invalid phaseState {state['phaseState']!r}")
    completed = state.get("completedPhases")
    if not isinstance(completed, list) or any(
        not isinstance(value, str) or not _is_phase_id(value) for value in completed
    ):
        raise RunnerError("completedPhases must be a string array")
    if len(completed) != len(set(completed)):
        raise RunnerError("completedPhases must not contain duplicates")
    progress = state.get("progress")
    if not isinstance(progress, dict) or set(progress) != {
        "task",
        "message",
        "updatedAt",
    }:
        raise RunnerError("progress fields are invalid")
    _required_string(progress, "message", "progress")
    if progress["task"] is not None and not isinstance(progress["task"], str):
        raise RunnerError("progress task must be a string or null")
    _validate_pending_replacement(state.get("pendingReplacement"))
    close_command_id = state.get("closeCommandId")
    if close_command_id is not None and (
        not isinstance(close_command_id, str) or not close_command_id.strip()
    ):
        raise RunnerError("closeCommandId must be a nonempty string or null")
    _parse_timestamp(_required_string(progress, "updatedAt", "progress"))
    _parse_timestamp(_required_string(state, "createdAt", "orchestration state"))
    _parse_timestamp(_required_string(state, "updatedAt", "orchestration state"))
    return state


def _validate_pending_replacement(value: Any) -> None:
    if value is None:
        return
    required = {
        "commandId",
        "targetProcessId",
        "targetLaunchId",
        "fromPhaseId",
        "toPhaseId",
        "nextLaunchId",
        "requestPath",
        "sessionStatusPath",
        "resultPath",
        "issuedAt",
    }
    if not isinstance(value, dict) or set(value) != required:
        raise RunnerError("pendingReplacement fields are invalid")
    for key in required - {"targetProcessId"}:
        _required_string(value, key, "pendingReplacement")
    if _positive_int_or_none(value["targetProcessId"]) is None:
        raise RunnerError("pendingReplacement targetProcessId must be positive")
    if not _is_phase_id(value["fromPhaseId"]) or not _is_phase_id(value["toPhaseId"]):
        raise RunnerError("pendingReplacement phase IDs are invalid")
    _parse_timestamp(value["issuedAt"])


def _phase_by_id(
    manifest: Mapping[str, Any],
    phase_id: str,
) -> dict[str, Any]:
    phase = next(
        (
            item
            for item in manifest["phases"]
            if isinstance(item, dict) and item.get("phaseId") == phase_id
        ),
        None,
    )
    if phase is None:
        raise RunnerError(f"phase {phase_id!r} is absent from the manifest")
    return phase


def _ordered_phases(manifest: Mapping[str, Any]) -> list[dict[str, Any]]:
    return sorted(manifest["phases"], key=lambda phase: phase["phaseNumber"])


def _resolve_manifest_path(manifest_path: Path, value: str) -> Path:
    candidate = Path(value)
    if candidate.is_absolute():
        return candidate.resolve()
    return (manifest_path.parent / candidate).resolve()


def _launch_process(
    executable: Path,
    request_path: Path,
    workspace: Path,
    state_path: Path,
) -> int:
    environment = os.environ.copy()
    environment["MIGHTY_PHASE_STATE_PATH"] = str(state_path)
    process = subprocess.Popen(
        [str(executable), "--launch-request", str(request_path)],
        cwd=str(workspace),
        env=environment,
    )
    return process.pid


@contextmanager
def _exclusive_lock(
    directory: Path,
    timeout: float = 10.0,
) -> Iterator[None]:
    with _PROCESS_LOCK:
        with _exclusive_file_lock(directory, timeout):
            yield


@contextmanager
def _exclusive_file_lock(
    directory: Path,
    timeout: float,
) -> Iterator[None]:
    directory.mkdir(parents=True, exist_ok=True)
    lock_path = directory / ".runner.lock"
    token = uuid4().hex
    deadline = time.monotonic() + timeout
    while True:
        try:
            descriptor = os.open(
                lock_path,
                os.O_CREAT | os.O_EXCL | os.O_WRONLY,
            )
        except FileExistsError:
            owner = _load_lock_owner(lock_path)
            owner_pid = (
                _positive_int_or_none(owner.get("processId"))
                if owner is not None
                else None
            )
            if owner_pid is not None and not _process_is_alive(owner_pid):
                try:
                    lock_path.unlink()
                except FileNotFoundError:
                    pass
                continue
            if owner_pid is None:
                try:
                    lock_age = time.time() - lock_path.stat().st_mtime
                except FileNotFoundError:
                    continue
                if lock_age >= timeout:
                    try:
                        lock_path.unlink()
                    except FileNotFoundError:
                        pass
                    continue
            if time.monotonic() >= deadline:
                raise RunnerError(
                    f"another phase-runner command owns {lock_path}"
                )
            time.sleep(0.05)
            continue
        try:
            payload = json.dumps(
                {
                    "processId": os.getpid(),
                    "token": token,
                    "createdAt": _utc_now(),
                },
                ensure_ascii=True,
            ).encode("ascii")
            os.write(descriptor, payload)
        finally:
            os.close(descriptor)
        break
    try:
        yield
    finally:
        owner = _load_lock_owner(lock_path)
        if owner is not None and owner.get("token") == token:
            try:
                lock_path.unlink()
            except FileNotFoundError:
                pass


def _load_lock_owner(path: Path) -> dict[str, Any] | None:
    try:
        with path.open("r", encoding="ascii") as stream:
            value = json.load(stream)
    except FileNotFoundError:
        return None
    except (UnicodeDecodeError, json.JSONDecodeError, OSError):
        return {}
    return value if isinstance(value, dict) else {}


def _progress(task: str | None, message: str) -> dict[str, Any]:
    return {
        "task": task or None,
        "message": message,
        "updatedAt": _utc_now(),
    }


def _state_response(
    state_path: Path,
    state: Mapping[str, Any],
    *,
    reused: bool = False,
) -> dict[str, Any]:
    return {
        "statePath": str(state_path),
        "reused": reused,
        "roadmapId": state["roadmapId"],
        "activePhaseId": state["activePhaseId"],
        "phaseState": state["phaseState"],
        "processId": state["processId"],
        "activeLaunchId": state["activeLaunchId"],
        "sessionStatusPath": state["activeSessionStatusPath"],
        "resultPath": state["activeResultPath"],
        "completedPhases": list(state["completedPhases"]),
        "progress": dict(state["progress"]),
    }


def _required_string(
    value: Mapping[str, Any],
    key: str,
    context: str,
) -> str:
    item = value.get(key)
    if not isinstance(item, str) or not item.strip():
        raise RunnerError(f"{context} field {key!r} must be a nonempty string")
    return item


def _nonempty(value: str, label: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise RunnerError(f"{label} must be a nonempty string")
    return value.strip()


def _load_object(path: Path, description: str) -> dict[str, Any]:
    try:
        with path.open("r", encoding="utf-8") as stream:
            value = json.load(stream)
    except FileNotFoundError as error:
        raise RunnerError(f"{description} does not exist: {path}") from error
    except UnicodeDecodeError as error:
        raise RunnerError(f"{description} is not valid UTF-8: {path}") from error
    except json.JSONDecodeError as error:
        raise RunnerError(f"{description} is invalid JSON at {path}: {error}") from error
    except OSError as error:
        raise RunnerError(f"could not read {description} at {path}: {error}") from error
    if not isinstance(value, dict):
        raise RunnerError(f"{description} must contain a JSON object: {path}")
    return value


def _load_optional_object(
    path: Path,
    description: str,
) -> dict[str, Any] | None:
    if not path.exists():
        return None
    return _load_object(path, description)


def _positive_int_or_none(value: Any) -> int | None:
    if isinstance(value, int) and not isinstance(value, bool) and value > 0:
        return value
    return None


def _safe_name(value: str) -> str:
    converted = "".join(
        character if character.isascii() and character.isalnum() else "-"
        for character in value
    )
    return "-".join(part for part in converted.split("-") if part) or "roadmap"


def _is_phase_id(value: str) -> bool:
    return len(value) >= 2 and value.isascii() and value.isdigit()


def _parse_timestamp(value: str) -> datetime:
    normalized = value[:-1] + "+00:00" if value.endswith("Z") else value
    try:
        parsed = datetime.fromisoformat(normalized)
    except ValueError as error:
        raise RunnerError(f"invalid ISO 8601 timestamp {value!r}") from error
    if parsed.tzinfo is None:
        raise RunnerError(f"timestamp must include a timezone: {value!r}")
    return parsed.astimezone(timezone.utc)


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def _process_is_alive(process_id: int) -> bool:
    if process_id <= 0:
        return False
    if os.name == "nt":
        return _windows_process_is_alive(process_id)
    try:
        os.kill(process_id, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return False
    return True


def _windows_process_is_alive(process_id: int) -> bool:
    import ctypes
    from ctypes import wintypes

    process_query_limited_information = 0x1000
    still_active = 259
    access_denied = 5
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    open_process = kernel32.OpenProcess
    open_process.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    open_process.restype = wintypes.HANDLE
    get_exit_code = kernel32.GetExitCodeProcess
    get_exit_code.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    get_exit_code.restype = wintypes.BOOL
    close_handle = kernel32.CloseHandle
    close_handle.argtypes = [wintypes.HANDLE]
    close_handle.restype = wintypes.BOOL

    handle = open_process(process_query_limited_information, False, process_id)
    if not handle:
        return ctypes.get_last_error() == access_denied
    try:
        exit_code = wintypes.DWORD()
        if not get_exit_code(handle, ctypes.byref(exit_code)):
            return True
        return exit_code.value == still_active
    finally:
        close_handle(handle)


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run one TurboPilot executor process across sequential phase sessions."
    )
    commands = parser.add_subparsers(dest="command", required=True)

    start = commands.add_parser(
        "start",
        help="Idempotently start the first phase executor session.",
    )
    start.add_argument("--manifest", required=True)
    start.add_argument("--exe", required=True)
    start.add_argument("--mode", default="phase-executor.agent")

    progress = commands.add_parser(
        "progress",
        help="Record executor milestone progress in orchestration state.",
    )
    progress.add_argument("--state", required=True)
    progress.add_argument("--task")
    progress.add_argument("--message", required=True)

    complete = commands.add_parser(
        "complete",
        help="Validate and store the active phase result.",
    )
    complete.add_argument("--state", required=True)
    complete.add_argument("--result", required=True)

    status = commands.add_parser(
        "status",
        help="Inspect the active TurboPilot session and phase state.",
    )
    status.add_argument("--state", required=True)
    status.add_argument("--idle-seconds", type=float, default=300.0)

    advance = commands.add_parser(
        "advance",
        help="Replace the completed phase with its successor in the same process.",
    )
    advance.add_argument("--state", required=True)
    advance.add_argument("--timeout", type=float, default=30.0)
    advance.add_argument("--poll-seconds", type=float, default=0.25)

    close = commands.add_parser(
        "close",
        help="Close the managed TurboPilot executor process.",
    )
    close.add_argument("--state", required=True)
    close.add_argument(
        "--disposition",
        choices=sorted(CONTROL_DISPOSITIONS),
        default="abandon",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = _build_parser()
    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "start":
            value = start_orchestration(
                arguments.manifest,
                arguments.exe,
                arguments.mode,
            )
        elif arguments.command == "progress":
            value = update_progress(
                arguments.state,
                arguments.task,
                arguments.message,
            )
        elif arguments.command == "complete":
            path = complete_phase(arguments.state, arguments.result)
            value = {"resultPath": str(path)}
        elif arguments.command == "status":
            value = inspect_orchestration(
                arguments.state,
                arguments.idle_seconds,
            )
        elif arguments.command == "advance":
            value = advance_phase(
                arguments.state,
                arguments.timeout,
                arguments.poll_seconds,
            )
        elif arguments.command == "close":
            value = close_orchestration(
                arguments.state,
                arguments.disposition,
            )
        else:
            parser.error(f"unknown command {arguments.command!r}")
            return 2
    except RunnerError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1
    print(json.dumps(value, indent=2, ensure_ascii=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
