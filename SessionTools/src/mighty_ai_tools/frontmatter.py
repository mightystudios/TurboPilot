"""Safe parser for the small frontmatter subset used by phase plans."""

from __future__ import annotations

from dataclasses import dataclass
import json
from pathlib import Path
import re
from typing import TypeAlias


Scalar: TypeAlias = str | int | float | bool | None
FrontmatterValue: TypeAlias = Scalar | list[Scalar]


class FrontmatterError(ValueError):
    """Raised when frontmatter is missing or outside the supported subset."""


@dataclass(frozen=True)
class FrontmatterDocument:
    path: Path
    metadata: dict[str, FrontmatterValue]
    body: str
    raw: str


_KEY_PATTERN = re.compile(r"^[A-Za-z_][A-Za-z0-9_-]*$")
_INTEGER_PATTERN = re.compile(r"^[+-]?(?:0|[1-9][0-9]*)$")
_FLOAT_PATTERN = re.compile(
    r"^[+-]?(?:(?:[0-9]+\.[0-9]*)|(?:[0-9]*\.[0-9]+)|(?:[0-9]+[eE][+-]?[0-9]+)|"
    r"(?:(?:[0-9]+\.[0-9]*)|(?:[0-9]*\.[0-9]+))[eE][+-]?[0-9]+)$"
)


def parse_frontmatter(path: str | Path) -> FrontmatterDocument:
    document_path = Path(path)
    text = document_path.read_text(encoding="utf-8")
    return parse_frontmatter_text(text, document_path)


def parse_frontmatter_text(
    text: str,
    path: str | Path = Path("<memory>"),
) -> FrontmatterDocument:
    document_path = Path(path)
    lines = text.splitlines()
    if not lines or lines[0].strip() != "---":
        raise FrontmatterError(f"{document_path}: frontmatter must begin on line 1 with ---")

    closing_index = next(
        (index for index, line in enumerate(lines[1:], start=1) if line.strip() == "---"),
        None,
    )
    if closing_index is None:
        raise FrontmatterError(f"{document_path}: frontmatter is missing its closing ---")

    metadata: dict[str, FrontmatterValue] = {}
    for line_number, line in enumerate(lines[1:closing_index], start=2):
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if ":" not in line:
            raise FrontmatterError(
                f"{document_path}:{line_number}: expected a key and value separated by ':'"
            )
        key_text, value_text = line.split(":", 1)
        key = key_text.strip()
        if not _KEY_PATTERN.fullmatch(key):
            raise FrontmatterError(
                f"{document_path}:{line_number}: unsupported frontmatter key {key!r}"
            )
        if key in metadata:
            raise FrontmatterError(
                f"{document_path}:{line_number}: duplicate frontmatter key {key!r}"
            )
        if not value_text.strip():
            raise FrontmatterError(
                f"{document_path}:{line_number}: use explicit null instead of an empty value"
            )
        try:
            metadata[key] = _parse_value(value_text.strip())
        except FrontmatterError as error:
            raise FrontmatterError(f"{document_path}:{line_number}: {error}") from error

    body = "\n".join(lines[closing_index + 1 :])
    if text.endswith(("\n", "\r")):
        body += "\n"
    return FrontmatterDocument(document_path, metadata, body, text)


def _parse_value(value: str) -> FrontmatterValue:
    if value.startswith("["):
        if not value.endswith("]"):
            raise FrontmatterError("inline list is missing closing ']'")
        return [_parse_scalar(item) for item in _split_inline_list(value[1:-1])]
    return _parse_scalar(value)


def _split_inline_list(content: str) -> list[str]:
    if not content.strip():
        return []

    items: list[str] = []
    current: list[str] = []
    quote: str | None = None
    escaped = False
    index = 0
    while index < len(content):
        character = content[index]
        if quote == '"':
            current.append(character)
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character == quote:
                quote = None
        elif quote == "'":
            current.append(character)
            if character == "'":
                if index + 1 < len(content) and content[index + 1] == "'":
                    current.append(content[index + 1])
                    index += 1
                else:
                    quote = None
        elif character in ("'", '"'):
            quote = character
            current.append(character)
        elif character == ",":
            item = "".join(current).strip()
            if not item:
                raise FrontmatterError("inline list contains an empty item")
            items.append(item)
            current = []
        elif character in "[]":
            raise FrontmatterError("nested inline lists are not supported")
        else:
            current.append(character)
        index += 1

    if quote is not None:
        raise FrontmatterError("inline list contains an unterminated quoted string")
    item = "".join(current).strip()
    if not item:
        raise FrontmatterError("inline list contains an empty item")
    items.append(item)
    return items


def _parse_scalar(value: str) -> Scalar:
    if not value:
        raise FrontmatterError("empty scalar is not supported")
    if value.startswith("'"):
        if len(value) < 2 or not value.endswith("'"):
            raise FrontmatterError("unterminated single-quoted string")
        return value[1:-1].replace("''", "'")
    if value.startswith('"'):
        if not value.endswith('"'):
            raise FrontmatterError("unterminated double-quoted string")
        try:
            parsed = json.loads(value)
        except json.JSONDecodeError as error:
            raise FrontmatterError(f"invalid double-quoted string: {error.msg}") from error
        if not isinstance(parsed, str):
            raise FrontmatterError("double-quoted value must be a string")
        return parsed
    if value.endswith(("'", '"')):
        raise FrontmatterError("quoted string has no matching opening quote")

    lowered = value.lower()
    if lowered in {"true", "false"}:
        return lowered == "true"
    if lowered in {"null", "~"}:
        return None
    if _INTEGER_PATTERN.fullmatch(value):
        return int(value)
    if _FLOAT_PATTERN.fullmatch(value):
        return float(value)
    return value
