"""Validation and manifest generation for sequential phase plans."""

from __future__ import annotations

import argparse
from dataclasses import dataclass, field
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import sys
from typing import Any, Iterable, Sequence
from urllib.parse import unquote, urlsplit
from uuid import uuid4

from .frontmatter import (
    FrontmatterDocument,
    FrontmatterError,
    FrontmatterValue,
    parse_frontmatter_text,
)


SCHEMA_VERSION = "1.0"
MASTER_REQUIRED_FIELDS = (
    "schema",
    "version",
    "roadmap_id",
    "document_type",
    "title",
    "goal",
    "workspace",
    "phase_glob",
)
PHASE_REQUIRED_FIELDS = (
    "schema",
    "version",
    "roadmap_id",
    "document_type",
    "phase_id",
    "phase_number",
    "title",
    "goal",
    "predecessors",
    "successor",
    "master",
    "workspace",
)
MASTER_REQUIRED_SECTIONS = (
    "Purpose",
    "Global Constraints",
    "Phase Order",
    "Phase Plans",
    "Validation",
)
PHASE_REQUIRED_SECTIONS = (
    "Outcome",
    "Context",
    "Scope",
    "Tasks",
    "Acceptance Criteria",
    "Validation",
)
_PHASE_ID_PATTERN = re.compile(r"^[0-9]{2,}$")
_HEADING_PATTERN = re.compile(r"^(#{1,6})[ \t]+(.+?)[ \t]*$")
_TASK_PATTERN = re.compile(
    r"^[ \t]*-[ \t]*\[[ xX]\][ \t]+([A-Za-z][A-Za-z0-9-]*):[ \t]+\S",
    re.MULTILINE,
)
_ACCEPTANCE_PATTERN = re.compile(
    r"^[ \t]*-[ \t]+([A-Za-z][A-Za-z0-9-]*):[ \t]+\S",
    re.MULTILINE,
)
_LINK_PATTERN = re.compile(r"!?\[[^\]]*\]\(([^)\r\n]+)\)")
_REFERENCE_LINK_PATTERN = re.compile(
    r"^[ \t]*\[[^\]\r\n]+\]:[ \t]*(<[^>\r\n]+>|[^ \t\r\n]+)",
    re.MULTILINE,
)
_PLACEHOLDER_PATTERN = re.compile(
    r"\{\{[^{}\r\n]+\}\}|\b(?:TBD|FIXME|XXX|PLACEHOLDER)\b",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class Diagnostic:
    severity: str
    code: str
    message: str
    path: str
    line: int | None = None

    def to_dict(self) -> dict[str, Any]:
        value: dict[str, Any] = {
            "severity": self.severity,
            "code": self.code,
            "message": self.message,
            "path": self.path,
        }
        if self.line is not None:
            value["line"] = self.line
        return value


@dataclass(frozen=True)
class PhaseRecord:
    path: Path
    document: FrontmatterDocument
    phase_id: str | None
    phase_number: int | None


@dataclass
class SuiteValidation:
    master_path: Path
    master: FrontmatterDocument | None = None
    phases: list[PhaseRecord] = field(default_factory=list)
    diagnostics: list[Diagnostic] = field(default_factory=list)

    @property
    def errors(self) -> list[Diagnostic]:
        return [item for item in self.diagnostics if item.severity == "error"]

    @property
    def valid(self) -> bool:
        return not self.errors

    def add_error(
        self,
        code: str,
        message: str,
        path: str | Path,
        line: int | None = None,
    ) -> None:
        self.diagnostics.append(Diagnostic("error", code, message, str(path), line))

    def to_dict(self) -> dict[str, Any]:
        return {
            "valid": self.valid,
            "master": str(self.master_path),
            "phaseCount": len(self.phases),
            "errorCount": len(self.errors),
            "diagnostics": [item.to_dict() for item in self.diagnostics],
        }


class PhaseSuiteValidationError(ValueError):
    def __init__(self, validation: SuiteValidation):
        super().__init__(
            f"phase suite validation failed with {len(validation.errors)} error(s)"
        )
        self.validation = validation


def validate_suite(master_path: str | Path) -> SuiteValidation:
    master_file = Path(master_path).resolve()
    result = SuiteValidation(master_file)
    master_text = _read_text(master_file, result)
    if master_text is None:
        return result

    _validate_text_basics(master_file, master_text, result)
    master = _parse_document(master_file, master_text, result)
    if master is None:
        return result
    result.master = master
    _validate_document(
        master,
        expected_type="phase-master",
        required_fields=MASTER_REQUIRED_FIELDS,
        required_sections=MASTER_REQUIRED_SECTIONS,
        result=result,
    )

    workspace = _workspace_path(master, result)
    phase_glob = master.metadata.get("phase_glob")
    if not isinstance(phase_glob, str) or not phase_glob.strip():
        result.add_error(
            "phase-glob",
            "phase_glob must be a nonempty relative pattern.",
            master_file,
        )
        return result
    glob_path = Path(phase_glob)
    if glob_path.is_absolute() or ".." in glob_path.parts:
        result.add_error(
            "phase-glob",
            "phase_glob must stay inside the master document directory.",
            master_file,
        )
        return result

    phase_paths = sorted(
        (
            path.resolve()
            for path in master_file.parent.glob(phase_glob)
            if path.is_file() and path.resolve() != master_file
        ),
        key=lambda path: str(path).lower(),
    )
    if not phase_paths:
        result.add_error(
            "no-phases",
            f"No phase documents match {phase_glob!r}.",
            master_file,
        )
        return result

    task_locations: dict[str, Path] = {}
    acceptance_locations: dict[str, Path] = {}
    for phase_path in phase_paths:
        phase_text = _read_text(phase_path, result)
        if phase_text is None:
            continue
        _validate_text_basics(phase_path, phase_text, result)
        phase_document = _parse_document(phase_path, phase_text, result)
        if phase_document is None:
            continue
        record = PhaseRecord(
            path=phase_path,
            document=phase_document,
            phase_id=_string_value(phase_document.metadata.get("phase_id")),
            phase_number=_integer_value(phase_document.metadata.get("phase_number")),
        )
        result.phases.append(record)
        _validate_phase(
            record,
            master,
            workspace,
            task_locations,
            acceptance_locations,
            result,
        )

    _validate_phase_sequence(result)
    _validate_master_phase_links(result)
    return result


def build_manifest(
    master_path: str | Path,
    output_path: str | Path,
    generated_at: str | None = None,
) -> dict[str, Any]:
    validation = validate_suite(master_path)
    if not validation.valid:
        raise PhaseSuiteValidationError(validation)
    if validation.master is None:
        raise RuntimeError("validated suite has no master document")

    output_file = Path(output_path).resolve()
    ordered = sorted(
        validation.phases,
        key=lambda phase: phase.phase_number if phase.phase_number is not None else -1,
    )
    manifest = {
        "schemaVersion": SCHEMA_VERSION,
        "generatedAt": generated_at or _utc_now(),
        "roadmapId": validation.master.metadata["roadmap_id"],
        "masterDocument": _portable_relative_path(
            validation.master_path,
            output_file.parent,
        ),
        "workspace": validation.master.metadata["workspace"],
        "phases": [
            {
                "phaseId": phase.document.metadata["phase_id"],
                "phaseNumber": phase.document.metadata["phase_number"],
                "title": phase.document.metadata["title"],
                "goal": phase.document.metadata["goal"],
                "predecessors": phase.document.metadata["predecessors"],
                "successor": phase.document.metadata["successor"],
                "document": _portable_relative_path(phase.path, output_file.parent),
            }
            for phase in ordered
        ],
    }
    atomic_write_json(output_file, manifest)
    return manifest


def atomic_write_json(path: str | Path, value: Any) -> None:
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(f".{destination.name}.{uuid4().hex}.tmp")
    try:
        with temporary.open("w", encoding="ascii", newline="\n") as stream:
            json.dump(value, stream, indent=2, ensure_ascii=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, destination)
    finally:
        if temporary.exists():
            temporary.unlink()


def _parse_document(
    path: Path,
    text: str,
    result: SuiteValidation,
) -> FrontmatterDocument | None:
    try:
        return parse_frontmatter_text(text, path)
    except FrontmatterError as error:
        result.add_error("frontmatter", str(error), path)
        return None


def _validate_document(
    document: FrontmatterDocument,
    *,
    expected_type: str,
    required_fields: Iterable[str],
    required_sections: Iterable[str],
    result: SuiteValidation,
) -> None:
    for key in required_fields:
        if key not in document.metadata:
            result.add_error(
                "missing-frontmatter-field",
                f"Missing required frontmatter field {key!r}.",
                document.path,
            )
            continue
        value = document.metadata[key]
        if isinstance(value, str) and not value.strip():
            result.add_error(
                "empty-frontmatter-field",
                f"Frontmatter field {key!r} must not be empty.",
                document.path,
            )
    if document.metadata.get("schema") != "mighty-phase-plan":
        result.add_error(
            "schema",
            "schema must be 'mighty-phase-plan'.",
            document.path,
        )
    if document.metadata.get("version") != SCHEMA_VERSION:
        result.add_error(
            "version",
            f"version must be {SCHEMA_VERSION!r}.",
            document.path,
        )
    if document.metadata.get("document_type") != expected_type:
        result.add_error(
            "document-type",
            f"document_type must be {expected_type!r}.",
            document.path,
        )
    for name in required_sections:
        content = _section_content(document.body, name, 2)
        if content is None:
            result.add_error(
                "missing-section",
                f"Missing required section '## {name}'.",
                document.path,
            )
        elif not _has_meaningful_content(content):
            result.add_error(
                "empty-section",
                f"Section '## {name}' must not be empty.",
                document.path,
            )
    _validate_links(document, result)


def _workspace_path(
    master: FrontmatterDocument,
    result: SuiteValidation,
) -> Path | None:
    value = master.metadata.get("workspace")
    if not isinstance(value, str) or not value.strip():
        result.add_error(
            "workspace",
            "workspace must be a nonempty absolute directory path.",
            master.path,
        )
        return None
    path = Path(value)
    if not path.is_absolute():
        result.add_error(
            "workspace",
            "workspace must be an absolute directory path.",
            master.path,
        )
        return None
    resolved = path.resolve()
    if not resolved.is_dir():
        result.add_error(
            "workspace",
            f"workspace does not exist: {resolved}",
            master.path,
        )
        return None
    return resolved


def _validate_phase(
    phase: PhaseRecord,
    master: FrontmatterDocument,
    workspace: Path | None,
    task_locations: dict[str, Path],
    acceptance_locations: dict[str, Path],
    result: SuiteValidation,
) -> None:
    document = phase.document
    metadata = document.metadata
    _validate_document(
        document,
        expected_type="phase",
        required_fields=PHASE_REQUIRED_FIELDS,
        required_sections=PHASE_REQUIRED_SECTIONS,
        result=result,
    )
    if metadata.get("roadmap_id") != master.metadata.get("roadmap_id"):
        result.add_error(
            "roadmap-id-mismatch",
            "Phase roadmap_id must match the master roadmap_id.",
            phase.path,
        )
    if metadata.get("workspace") != master.metadata.get("workspace"):
        result.add_error(
            "workspace-mismatch",
            "Phase workspace must match the master workspace.",
            phase.path,
        )
    if workspace is not None:
        try:
            phase.path.relative_to(workspace)
        except ValueError:
            result.add_error(
                "phase-outside-workspace",
                "Phase documents must be contained by workspace for TurboPilot attachments.",
                phase.path,
            )

    if phase.phase_id is None or not _PHASE_ID_PATTERN.fullmatch(phase.phase_id):
        result.add_error(
            "phase-id",
            "phase_id must contain at least two digits.",
            phase.path,
        )
    if phase.phase_number is None or phase.phase_number < 0:
        result.add_error(
            "phase-number",
            "phase_number must be a nonnegative integer.",
            phase.path,
        )
    elif phase.phase_id is not None and phase.phase_id != f"{phase.phase_number:02d}":
        result.add_error(
            "phase-id-number-mismatch",
            "phase_id must be the canonical zero-padded phase_number.",
            phase.path,
        )

    predecessors = metadata.get("predecessors")
    if not isinstance(predecessors, list) or any(
        not isinstance(value, str) or not _PHASE_ID_PATTERN.fullmatch(value)
        for value in predecessors
    ):
        result.add_error(
            "predecessors",
            "predecessors must be an inline list of phase IDs.",
            phase.path,
        )
    elif len(predecessors) != len(set(predecessors)):
        result.add_error(
            "duplicate-predecessor",
            "predecessors must not contain duplicates.",
            phase.path,
        )

    successor = metadata.get("successor")
    if successor is not None and (
        not isinstance(successor, str) or not _PHASE_ID_PATTERN.fullmatch(successor)
    ):
        result.add_error(
            "successor",
            "successor must be a phase ID or null.",
            phase.path,
        )

    master_reference = metadata.get("master")
    if not isinstance(master_reference, str) or not master_reference.strip():
        result.add_error(
            "master-reference",
            "master must be a nonempty relative path.",
            phase.path,
        )
    else:
        reference_path = Path(master_reference)
        if reference_path.is_absolute() or (
            phase.path.parent / reference_path
        ).resolve() != master.path.resolve():
            result.add_error(
                "master-reference",
                "master must resolve to the validated master document.",
                phase.path,
            )

    scope = _section_content(document.body, "Scope", 2)
    if scope is not None:
        for subsection in ("In Scope", "Out of Scope"):
            content = _section_content(scope, subsection, 3)
            if content is None or not _has_meaningful_content(content):
                result.add_error(
                    "scope-subsection",
                    f"Scope must contain a nonempty '### {subsection}' subsection.",
                    phase.path,
                )

    task_text = _section_content(document.body, "Tasks", 2) or ""
    task_ids = _TASK_PATTERN.findall(task_text)
    if not task_ids:
        result.add_error(
            "missing-task-ids",
            "Tasks must contain at least one checklist item with an ID.",
            phase.path,
        )
    for task_id in task_ids:
        _register_unique_id(task_id, phase.path, "task", task_locations, result)

    acceptance_text = _section_content(document.body, "Acceptance Criteria", 2) or ""
    acceptance_ids = _ACCEPTANCE_PATTERN.findall(acceptance_text)
    if not acceptance_ids:
        result.add_error(
            "missing-acceptance-ids",
            "Acceptance Criteria must contain at least one criterion with an ID.",
            phase.path,
        )
    for acceptance_id in acceptance_ids:
        _register_unique_id(
            acceptance_id,
            phase.path,
            "acceptance criterion",
            acceptance_locations,
            result,
        )

    validation_text = _section_content(document.body, "Validation", 2) or ""
    if "`" not in validation_text:
        result.add_error(
            "validation-command",
            "Validation must include at least one exact command in backticks.",
            phase.path,
        )


def _register_unique_id(
    value: str,
    path: Path,
    label: str,
    locations: dict[str, Path],
    result: SuiteValidation,
) -> None:
    normalized = value.upper()
    existing = locations.get(normalized)
    if existing is not None:
        result.add_error(
            f"duplicate-{label.replace(' ', '-')}-id",
            f"{label.title()} ID {value!r} is already used in {existing}.",
            path,
        )
    else:
        locations[normalized] = path


def _validate_phase_sequence(result: SuiteValidation) -> None:
    id_locations: dict[str, Path] = {}
    number_locations: dict[int, Path] = {}
    for phase in result.phases:
        if phase.phase_id is not None:
            if phase.phase_id in id_locations:
                result.add_error(
                    "duplicate-phase-id",
                    f"phase_id {phase.phase_id!r} is duplicated in {id_locations[phase.phase_id]}.",
                    phase.path,
                )
            else:
                id_locations[phase.phase_id] = phase.path
        if phase.phase_number is not None:
            if phase.phase_number in number_locations:
                result.add_error(
                    "duplicate-phase-number",
                    f"phase_number {phase.phase_number} is duplicated in {number_locations[phase.phase_number]}.",
                    phase.path,
                )
            else:
                number_locations[phase.phase_number] = phase.path

    ordered = sorted(
        (
            phase
            for phase in result.phases
            if phase.phase_id is not None and phase.phase_number is not None
        ),
        key=lambda phase: phase.phase_number if phase.phase_number is not None else -1,
    )
    numbers = [phase.phase_number for phase in ordered]
    if numbers and numbers != list(range(numbers[0], numbers[0] + len(numbers))):
        result.add_error(
            "phase-number-gap",
            "Phase numbers must be contiguous.",
            result.master_path,
        )

    for index, phase in enumerate(ordered):
        expected_predecessors = [] if index == 0 else [ordered[index - 1].phase_id]
        expected_successor = (
            ordered[index + 1].phase_id if index + 1 < len(ordered) else None
        )
        if phase.document.metadata.get("predecessors") != expected_predecessors:
            result.add_error(
                "sequential-predecessor",
                f"predecessors must be {expected_predecessors!r} for a sequential roadmap.",
                phase.path,
            )
        if phase.document.metadata.get("successor") != expected_successor:
            result.add_error(
                "sequential-successor",
                f"successor must be {expected_successor!r} for a sequential roadmap.",
                phase.path,
            )


def _validate_master_phase_links(result: SuiteValidation) -> None:
    if result.master is None:
        return
    section = _section_content(result.master.body, "Phase Plans", 2)
    if section is None:
        return
    linked_paths = [
        target
        for target in _resolved_relative_links(result.master_path, section)
        if target is not None
    ]
    expected = [
        phase.path.resolve()
        for phase in sorted(
            result.phases,
            key=lambda phase: (
                phase.phase_number if phase.phase_number is not None else -1,
                str(phase.path).lower(),
            ),
        )
    ]
    phase_paths = set(expected)
    linked_phases = [path for path in linked_paths if path in phase_paths]
    if linked_phases != expected:
        result.add_error(
            "phase-plan-links",
            "Phase Plans must link every phase exactly once in ascending phase order.",
            result.master_path,
        )


def _validate_text_basics(
    path: Path,
    text: str,
    result: SuiteValidation,
) -> None:
    if not text.isascii():
        result.add_error(
            "non-ascii",
            "All plan content must contain ASCII characters only.",
            path,
        )
    match = _PLACEHOLDER_PATTERN.search(text)
    if match:
        result.add_error(
            "placeholder",
            f"Unresolved placeholder marker {match.group(0)!r}.",
            path,
            text.count("\n", 0, match.start()) + 1,
        )


def _validate_links(
    document: FrontmatterDocument,
    result: SuiteValidation,
) -> None:
    for raw_target, target in _iter_relative_links(document.path, document.raw):
        if target is not None and not target.exists():
            result.add_error(
                "broken-link",
                f"Relative link {raw_target!r} does not resolve.",
                document.path,
            )


def _iter_relative_links(
    source_path: Path,
    text: str,
) -> Iterable[tuple[str, Path | None]]:
    raw_targets = [match.group(1) for match in _LINK_PATTERN.finditer(text)]
    raw_targets.extend(
        match.group(1) for match in _REFERENCE_LINK_PATTERN.finditer(text)
    )
    for raw_value in raw_targets:
        raw = raw_value.strip()
        if raw.startswith("<") and ">" in raw:
            raw = raw[1 : raw.index(">")]
        elif re.search(r"\s+[\"']", raw):
            raw = re.split(r"\s+[\"']", raw, maxsplit=1)[0]
        target_text = unquote(raw.strip())
        if not target_text or target_text.startswith("#"):
            continue
        split = urlsplit(target_text)
        if split.scheme or split.netloc or not split.path:
            continue
        path = Path(split.path)
        if path.is_absolute():
            continue
        yield raw, (source_path.parent / path).resolve()


def _resolved_relative_links(source_path: Path, text: str) -> Iterable[Path | None]:
    for _, target in _iter_relative_links(source_path, text):
        yield target


def _section_content(body: str, name: str, level: int) -> str | None:
    lines = body.splitlines()
    start: int | None = None
    for index, line in enumerate(lines):
        match = _HEADING_PATTERN.match(line)
        if not match:
            continue
        heading_level = len(match.group(1))
        heading_name = match.group(2).strip().rstrip("#").strip()
        if start is None:
            if heading_level == level and heading_name == name:
                start = index + 1
            continue
        if heading_level <= level:
            return "\n".join(lines[start:index]).strip()
    if start is None:
        return None
    return "\n".join(lines[start:]).strip()


def _has_meaningful_content(content: str) -> bool:
    return any(
        line.strip() and line.strip() not in {"-", "*", "[]", "[ ]"}
        for line in content.splitlines()
        if not line.strip().startswith("#")
    )


def _read_text(path: Path, result: SuiteValidation) -> str | None:
    try:
        return path.read_text(encoding="utf-8")
    except FileNotFoundError:
        result.add_error("missing-file", "File does not exist.", path)
    except UnicodeDecodeError as error:
        result.add_error("encoding", f"File is not valid UTF-8: {error}.", path)
    except OSError as error:
        result.add_error("read-error", f"Could not read file: {error}.", path)
    return None


def _string_value(value: FrontmatterValue | None) -> str | None:
    return value if isinstance(value, str) else None


def _integer_value(value: FrontmatterValue | None) -> int | None:
    if isinstance(value, int) and not isinstance(value, bool):
        return value
    return None


def _portable_relative_path(path: Path, base: Path) -> str:
    return os.path.relpath(path, base)


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def _print_validation(validation: SuiteValidation, as_json: bool) -> None:
    if as_json:
        print(json.dumps(validation.to_dict(), indent=2, ensure_ascii=True))
        return
    for diagnostic in validation.diagnostics:
        location = diagnostic.path
        if diagnostic.line is not None:
            location += f":{diagnostic.line}"
        print(
            f"{diagnostic.severity.upper()} {diagnostic.code} {location}: "
            f"{diagnostic.message}"
        )
    if validation.valid:
        print(
            f"Validation passed: {validation.master_path} "
            f"({len(validation.phases)} phase(s))."
        )
    else:
        print(f"Validation failed with {len(validation.errors)} error(s).")


def validation_cli(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Validate a master roadmap and its sequential phase documents."
    )
    parser.add_argument("--master", required=True)
    parser.add_argument("--json", action="store_true")
    arguments = parser.parse_args(argv)
    validation = validate_suite(arguments.master)
    _print_validation(validation, arguments.json)
    return 0 if validation.valid else 1


def manifest_cli(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Validate a phase suite and atomically generate its manifest."
    )
    parser.add_argument("--master", required=True)
    parser.add_argument("--output", required=True)
    arguments = parser.parse_args(argv)
    try:
        manifest = build_manifest(arguments.master, arguments.output)
    except PhaseSuiteValidationError as error:
        _print_validation(error.validation, False)
        return 1
    print(
        f"Wrote {Path(arguments.output).resolve()} "
        f"with {len(manifest['phases'])} phase(s)."
    )
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    return validation_cli(argv)


if __name__ == "__main__":
    sys.exit(main())
