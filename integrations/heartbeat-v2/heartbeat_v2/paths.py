"""Fail-closed path checks shared by state and recovery-probe producers."""

from __future__ import annotations

import os
from pathlib import Path

from .common import ContractError


MAXIMUM_WINDOWS_PATH_UTF16 = 240
MAXIMUM_SEGMENT_UTF8 = 255
_WINDOWS_RESERVED = {
    "CON",
    "PRN",
    "AUX",
    "NUL",
    *(f"COM{number}" for number in range(1, 10)),
    *(f"LPT{number}" for number in range(1, 10)),
}


def validate_existing_root(root: Path) -> Path:
    """Resolve an approved existing root and reject link/reparse ancestry."""

    try:
        resolved = root.resolve(strict=True)
    except OSError as exc:
        raise ContractError(f"approved root cannot be resolved: {exc}") from exc
    if not resolved.is_dir():
        raise ContractError("approved root must be a directory")
    _validate_absolute_budget(resolved)
    _reject_existing_reparse_ancestry(resolved)
    return resolved


def resolve_existing_file(root: Path, relative: str) -> Path:
    """Resolve one fixed relative input without following a reparse boundary."""

    approved_root = validate_existing_root(root)
    parts = validate_relative_path(relative)
    lexical = approved_root.joinpath(*parts)
    _validate_absolute_budget(lexical)
    _reject_existing_reparse_ancestry(lexical)
    try:
        resolved = lexical.resolve(strict=True)
    except OSError as exc:
        raise ContractError(f"required local file cannot be resolved: {relative}: {exc}") from exc
    if not is_contained(approved_root, resolved) or not resolved.is_file():
        raise ContractError(f"required local file is outside the approved root: {relative}")
    return resolved


def validate_output_file(root: Path, relative: str) -> Path:
    """Preflight an output path and every currently existing ancestor."""

    approved_root = validate_existing_root(root)
    parts = validate_relative_path(relative)
    lexical = approved_root.joinpath(*parts)
    _validate_absolute_budget(lexical)

    existing = lexical
    while not existing.exists():
        if existing == approved_root:
            break
        existing = existing.parent
    _reject_existing_reparse_ancestry(existing)
    if not is_contained(approved_root, lexical):
        raise ContractError("output path escapes the approved root")
    if lexical.exists() and (not lexical.is_file() or is_reparse(lexical)):
        raise ContractError("output path is not a regular non-reparse file")
    return lexical


def validate_direct_state_file(root: Path, state_path: Path) -> tuple[Path, Path]:
    """Require an external state file to be a direct child of its private root."""

    approved_root = validate_existing_root(root)
    candidate = state_path.resolve(strict=False)
    if candidate.parent != approved_root:
        raise ContractError("external state must be a direct child of its approved root")
    if candidate.suffix.lower() != ".json":
        raise ContractError("external state must use a .json filename")
    validate_relative_path(candidate.name)
    _validate_absolute_budget(candidate)
    _validate_absolute_budget(Path(f"{candidate}.lease"))
    _validate_absolute_budget(Path(f"{candidate}.lease") / "lease.json")
    _validate_absolute_budget(Path(f"{candidate}.lease.guard"))
    if candidate.exists() and (not candidate.is_file() or is_reparse(candidate)):
        raise ContractError("external state is not a regular non-reparse file")
    return approved_root, candidate


def validate_relative_path(value: str) -> tuple[str, ...]:
    """Validate portable relative segments without normalizing unsafe spelling."""

    if not isinstance(value, str) or not value:
        raise ContractError("relative path must be a non-empty string")
    normalized = value.replace("\\", "/")
    if normalized.startswith("/") or ":" in normalized:
        raise ContractError("relative path must not be rooted or contain an alternate stream")
    parts = tuple(normalized.split("/"))
    if any(part in ("", ".", "..") for part in parts):
        raise ContractError("relative path contains an empty, current, or parent segment")
    for part in parts:
        if part != part.rstrip(" ."):
            raise ContractError("relative path contains trailing space/dot normalization")
        if len(part.encode("utf-8")) > MAXIMUM_SEGMENT_UTF8:
            raise ContractError("relative path segment exceeds the UTF-8 byte budget")
        stem = part.split(".", 1)[0].upper()
        if stem in _WINDOWS_RESERVED:
            raise ContractError("relative path contains a reserved Windows name")
        if any(ord(character) < 0x20 for character in part):
            raise ContractError("relative path contains a control character")
    return parts


def is_contained(root: Path, candidate: Path) -> bool:
    try:
        candidate.relative_to(root)
        return True
    except ValueError:
        return False


def is_reparse(path: Path) -> bool:
    if path.is_symlink():
        return True
    try:
        stat = path.lstat()
    except OSError as exc:
        raise ContractError(f"unable to inspect path safety: {path.name}: {exc}") from exc
    attributes = getattr(stat, "st_file_attributes", 0)
    reparse_flag = getattr(os, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return bool(attributes & reparse_flag)


def _reject_existing_reparse_ancestry(path: Path) -> None:
    existing = path
    if not existing.exists():
        raise ContractError("path safety inspection requires an existing ancestor")
    ancestors = list(existing.parents)
    for candidate in reversed(ancestors):
        if candidate.exists() and is_reparse(candidate):
            raise ContractError("path ancestry contains a reparse point or symlink")
    if is_reparse(existing):
        raise ContractError("path is a reparse point or symlink")


def _validate_absolute_budget(path: Path) -> None:
    # UTF-16-LE byte count divided by two counts code units, including
    # supplementary characters as two units.
    code_units = len(str(path).encode("utf-16-le")) // 2
    if code_units > MAXIMUM_WINDOWS_PATH_UTF16:
        raise ContractError(
            f"resolved local path exceeds {MAXIMUM_WINDOWS_PATH_UTF16} UTF-16 code units"
        )
