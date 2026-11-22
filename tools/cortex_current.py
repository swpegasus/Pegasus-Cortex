#!/usr/bin/env python3
"""Generate the bounded project-local Cortex hot-path projection.

The projection is deliberately derived from the human-readable authority files.
It never replaces them and is safe to discard and rebuild.  Generation fails
closed when a required source is missing, linked, malformed, or changes while
being hashed.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import tempfile
from typing import Any


SCHEMA = "pegasus.cortex.current/1.0"
MAXIMUM_UTF8_BYTES = 2 * 1024
MAXIMUM_TEXT_CHARACTERS = 320
DEFAULT_MAXIMUM_AGE_SECONDS = 24 * 60 * 60
AUTHORITY_FILES = (
    "START_HERE.md",
    "AGENTS.md",
    ".cortex/STATE.md",
    ".cortex/TASKS.md",
    ".cortex/RECOVERY.md",
)
AUTHORITY_SOURCE_IDS = (
    "start_here",
    "agents",
    "cortex_state",
    "cortex_tasks",
    "cortex_recovery",
)
AUTHORITY_SET_CONTRACT = "pegasus.cortex.authority-set/1.0"
HOT_FIELDS = {
    "task": "Current task",
    "gate": "Gate",
    "checkpoint": "Checkpoint",
    "nextAction": "Next action",
}
HOT_LINE = re.compile(r"^- (?P<label>[^:]+):\s*(?P<value>.+?)\s*$")
UTC_TIMESTAMP = re.compile(
    r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z"
)
PROJECT_LINK_ID = re.compile(r"plink_[0-9a-f]{64}")


class CurrentProjectionError(ValueError):
    """Raised when the projection cannot be generated safely."""


def _is_link_or_reparse(path: Path) -> bool:
    """Reject symbolic links and Windows junction/reparse boundaries."""

    details = path.lstat()
    attributes = getattr(details, "st_file_attributes", 0)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return path.is_symlink() or bool(attributes & reparse_flag)


def _strict_project_file(root: Path, relative: str, *, must_exist: bool) -> Path:
    candidate = root.joinpath(*relative.split("/")).absolute()
    try:
        candidate.relative_to(root)
    except ValueError as exception:
        raise CurrentProjectionError(f"path escaped the project root: {relative}") from exception
    if len(str(candidate)) > 240 or any(len(part.encode("utf-8")) > 240 for part in candidate.parts):
        raise CurrentProjectionError(f"path exceeds the project boundary: {relative}")
    if must_exist and not candidate.is_file():
        raise CurrentProjectionError(f"required authority file is unavailable: {relative}")
    probe = root
    for part in Path(relative).parts:
        probe = probe / part
        if probe.exists() and _is_link_or_reparse(probe):
            raise CurrentProjectionError(f"linked paths are not allowed: {relative}")
    return candidate


def _read_stable_bytes(path: Path, relative: str) -> bytes:
    if _is_link_or_reparse(path):
        raise CurrentProjectionError(f"linked paths are not allowed: {relative}")
    with path.open("rb") as stream:
        before = os.fstat(stream.fileno())
        data = stream.read()
        after = os.fstat(stream.fileno())
    path_after = os.stat(path, follow_symlinks=False)
    identity = lambda value: (
        value.st_dev,
        value.st_ino,
        value.st_size,
        value.st_mtime_ns,
        value.st_ctime_ns,
    )
    if identity(before) != identity(after) or identity(after) != identity(path_after):
        raise CurrentProjectionError(f"authority file changed during generation: {relative}")
    try:
        data.decode("utf-8", errors="strict")
    except UnicodeDecodeError as exception:
        raise CurrentProjectionError(f"authority file is not strict UTF-8: {relative}") from exception
    return data


def authority_set(root: Path) -> tuple[str, list[dict[str, Any]]]:
    """Return the Core-compatible authority hash and per-source metadata."""

    sources: list[dict[str, Any]] = []
    authority_sources: list[dict[str, str]] = []
    for relative, source_id in zip(
        AUTHORITY_FILES, AUTHORITY_SOURCE_IDS, strict=True
    ):
        path = _strict_project_file(root, relative, must_exist=True)
        data = _read_stable_bytes(path, relative)
        digest = hashlib.sha256(data).hexdigest()
        sources.append({"path": relative, "sha256": digest, "utf8Bytes": len(data)})
        authority_sources.append(
            {
                "sourceId": source_id,
                "revisionId": f"sha256-{digest}",
                "sha256": digest,
            }
        )
    return compute_authority_set_hash(authority_sources), sources


def compute_authority_set_hash(authority_sources: list[dict[str, str]]) -> str:
    """Match CortexProjectBriefService.ComputeAuthoritySetHash exactly."""

    projection = {
        "contractId": AUTHORITY_SET_CONTRACT,
        "sources": sorted(authority_sources, key=lambda item: item["sourceId"]),
    }
    canonical = json.dumps(
        projection,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()


def _clean_markdown_value(value: str, label: str) -> str:
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] == "`":
        value = value[1:-1].strip()
    if not value or len(value) > MAXIMUM_TEXT_CHARACTERS:
        raise CurrentProjectionError(f"{label} must contain 1-{MAXIMUM_TEXT_CHARACTERS} characters")
    if any(character in value for character in ("\r", "\n", "\x00")):
        raise CurrentProjectionError(f"{label} must be one public text line")
    return value


def read_hot_fields(start_here: bytes) -> dict[str, str]:
    """Read the explicit bounded fields from START_HERE's Current Recovery block."""

    text = start_here.decode("utf-8", errors="strict")
    marker = "## Current Recovery"
    start = text.find(marker)
    if start < 0:
        raise CurrentProjectionError("START_HERE.md is missing the Current Recovery section")
    block = text[start + len(marker) :]
    next_heading = re.search(r"^##\s+", block, flags=re.MULTILINE)
    if next_heading:
        block = block[: next_heading.start()]
    found: dict[str, str] = {}
    labels = {label.casefold(): field for field, label in HOT_FIELDS.items()}
    active_field: str | None = None
    active_parts: list[str] = []

    def store_active() -> None:
        """Commit one Markdown bullet after folding its indented continuations."""

        nonlocal active_field, active_parts
        if active_field is None:
            return
        if active_field in found:
            raise CurrentProjectionError(f"duplicate {HOT_FIELDS[active_field]} field")
        found[active_field] = _clean_markdown_value(
            " ".join(active_parts), HOT_FIELDS[active_field]
        )
        active_field = None
        active_parts = []

    for line in block.splitlines():
        # Only a column-zero bullet begins a new authority field. Indented
        # Markdown lines are continuations of the active field, matching the
        # supported local operator implementation.
        if line.startswith("-"):
            store_active()
            match = HOT_LINE.match(line)
            if not match:
                continue
            field = labels.get(match.group("label").strip().casefold())
            if field:
                active_field = field
                active_parts = [match.group("value").strip()]
            continue
        if active_field is not None and line and line[0].isspace() and line.strip():
            active_parts.append(line.strip())
            if len(" ".join(active_parts)) > MAXIMUM_TEXT_CHARACTERS:
                raise CurrentProjectionError(
                    f"{HOT_FIELDS[active_field]} must contain "
                    f"1-{MAXIMUM_TEXT_CHARACTERS} characters"
                )
    store_active()
    missing = [label for field, label in HOT_FIELDS.items() if field not in found]
    if missing:
        raise CurrentProjectionError("Current Recovery is missing: " + ", ".join(missing))
    return found


def _load_previous(output: Path) -> dict[str, Any] | None:
    if not output.exists():
        return None
    if _is_link_or_reparse(output):
        raise CurrentProjectionError("CURRENT.json cannot be a link or reparse point")
    try:
        previous = json.loads(output.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exception:
        raise CurrentProjectionError("existing CURRENT.json is unreadable") from exception
    if not isinstance(previous, dict) or previous.get("schema") != SCHEMA:
        raise CurrentProjectionError("existing CURRENT.json has an unsupported schema")
    return previous


def _parse_utc(value: Any, field: str) -> datetime:
    if not isinstance(value, str) or not UTC_TIMESTAMP.fullmatch(value):
        raise CurrentProjectionError(f"{field} must be an RFC 3339 UTC value")
    try:
        return datetime.fromisoformat(value.removesuffix("Z") + "+00:00")
    except ValueError as exception:
        raise CurrentProjectionError(f"{field} is not a valid UTC timestamp") from exception


def _read_project_identity(meta_path: Path) -> dict[str, str]:
    """Read the exact local routing identity required by project-brief MCP."""

    try:
        meta = json.loads(_read_stable_bytes(meta_path, ".cortex/meta.json"))
        project_name = _clean_markdown_value(str(meta["project"]["name"]), "project name")
        correlation = meta["cortexCorrelation"]
        project_id = _clean_markdown_value(str(correlation["projectId"]), "project id")
        project_link_id = str(correlation["projectLinkId"])
    except (KeyError, TypeError, json.JSONDecodeError) as exception:
        raise CurrentProjectionError(
            ".cortex/meta.json lacks bounded correlated project identity"
        ) from exception
    if not PROJECT_LINK_ID.fullmatch(project_link_id):
        raise CurrentProjectionError(".cortex/meta.json projectLinkId is invalid")
    return {
        "id": project_id,
        "name": project_name,
        "projectLinkId": project_link_id,
    }


def verify_projection(
    root: Path,
    now_utc: str,
    maximum_age_seconds: int,
) -> dict[str, Any]:
    """Validate the existing projection without modifying any file."""

    if maximum_age_seconds < 1:
        raise CurrentProjectionError("maximum age must be positive")
    root = root.resolve(strict=True)
    output = _strict_project_file(root, ".cortex/CURRENT.json", must_exist=True)
    if output.stat().st_size > MAXIMUM_UTF8_BYTES:
        raise CurrentProjectionError("CURRENT.json exceeds its 2 KiB boundary")
    current = _load_previous(output)
    if current is None:
        raise CurrentProjectionError("CURRENT.json is unavailable")
    expected_keys = {"schema", "project", "current", "authority", "freshness", "fallback"}
    if set(current) != expected_keys:
        raise CurrentProjectionError("CURRENT.json top-level fields do not match the contract")
    authority = current.get("authority")
    freshness = current.get("freshness")
    fallback = current.get("fallback")
    if not isinstance(authority, dict) or set(authority) != {
        "revision", "authoritySetHash", "sourceCount"
    }:
        raise CurrentProjectionError("CURRENT.json authority fields do not match the contract")
    if not isinstance(freshness, dict) or set(freshness) != {"generatedAtUtc"}:
        raise CurrentProjectionError("CURRENT.json freshness fields do not match the contract")
    if fallback != {
        "pointer": ".cortex/README.md#full-fallback-recovery-order",
        "when": "missing, stale, malformed, incomplete, or authority hash mismatch",
    }:
        raise CurrentProjectionError("CURRENT.json fallback fields do not match the contract")
    meta_path = _strict_project_file(root, ".cortex/meta.json", must_exist=True)
    expected_project = _read_project_identity(meta_path)
    if current.get("project") != expected_project:
        raise CurrentProjectionError("CURRENT.json project identity does not match .cortex/meta.json")
    expected_hash, sources = authority_set(root)
    if authority.get("authoritySetHash") != expected_hash:
        raise CurrentProjectionError("CURRENT.json authority hash does not match local files")
    if authority.get("sourceCount") != len(sources):
        raise CurrentProjectionError("CURRENT.json authority source count is invalid")
    if not isinstance(authority.get("revision"), int) or authority["revision"] < 1:
        raise CurrentProjectionError("CURRENT.json authority revision is invalid")
    start_bytes = _read_stable_bytes(root / "START_HERE.md", "START_HERE.md")
    if current.get("current") != read_hot_fields(start_bytes):
        raise CurrentProjectionError("CURRENT.json hot fields do not match START_HERE.md")
    generated = _parse_utc(freshness.get("generatedAtUtc"), "generatedAtUtc")
    now = _parse_utc(now_utc, "nowUtc")
    age_seconds = (now - generated).total_seconds()
    if age_seconds < 0:
        raise CurrentProjectionError("CURRENT.json generation time is in the future")
    if age_seconds > maximum_age_seconds:
        raise CurrentProjectionError("CURRENT.json is stale")
    return {
        "authoritySetHash": expected_hash,
        "revision": authority["revision"],
        "status": "valid",
        "utf8Bytes": output.stat().st_size,
    }


def build_projection(root: Path, generated_at_utc: str) -> dict[str, Any]:
    root = root.resolve(strict=True)
    if not root.is_dir():
        raise CurrentProjectionError("project root is not a directory")
    meta_path = _strict_project_file(root, ".cortex/meta.json", must_exist=True)
    project = _read_project_identity(meta_path)
    authority_hash, sources = authority_set(root)
    start_bytes = _read_stable_bytes(root / "START_HERE.md", "START_HERE.md")
    start_digest = hashlib.sha256(start_bytes).hexdigest()
    recorded_start_digest = next(
        item["sha256"] for item in sources if item["path"] == "START_HERE.md"
    )
    if start_digest != recorded_start_digest:
        raise CurrentProjectionError(
            "START_HERE.md changed after the authority set was hashed"
        )
    hot = read_hot_fields(start_bytes)
    output = _strict_project_file(root, ".cortex/CURRENT.json", must_exist=False)
    previous = _load_previous(output)
    previous_hash = None if previous is None else previous.get("authority", {}).get("authoritySetHash")
    previous_revision = 0 if previous is None else previous.get("authority", {}).get("revision", 0)
    if not isinstance(previous_revision, int) or previous_revision < 1:
        previous_revision = 0
    unchanged_inputs = (
        previous_hash == authority_hash and
        previous is not None and
        previous.get("project") == project
    )
    revision = previous_revision if unchanged_inputs else previous_revision + 1
    if revision < 1:
        revision = 1
    return {
        "schema": SCHEMA,
        "project": project,
        "current": hot,
        "authority": {
            "revision": revision,
            "authoritySetHash": authority_hash,
            "sourceCount": len(sources),
        },
        "freshness": {"generatedAtUtc": generated_at_utc},
        "fallback": {
            "pointer": ".cortex/README.md#full-fallback-recovery-order",
            "when": "missing, stale, malformed, incomplete, or authority hash mismatch",
        },
    }


def write_projection(root: Path, projection: dict[str, Any]) -> tuple[Path, int]:
    output = _strict_project_file(root, ".cortex/CURRENT.json", must_exist=False)
    data = (json.dumps(projection, ensure_ascii=False, separators=(",", ":"), sort_keys=True) + "\n").encode("utf-8")
    if len(data) > MAXIMUM_UTF8_BYTES:
        raise CurrentProjectionError(
            f"CURRENT.json would exceed {MAXIMUM_UTF8_BYTES} UTF-8 bytes ({len(data)})"
        )
    output.parent.mkdir(parents=False, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=".CURRENT.", suffix=".tmp", dir=output.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_name, output)
    finally:
        if os.path.exists(temporary_name):
            os.unlink(temporary_name)
    return output, len(data)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", required=True, type=Path)
    parser.add_argument("--verify", action="store_true")
    parser.add_argument("--generated-at-utc")
    parser.add_argument("--now-utc")
    parser.add_argument(
        "--max-age-seconds",
        type=int,
        default=DEFAULT_MAXIMUM_AGE_SECONDS,
    )
    arguments = parser.parse_args()
    root = arguments.project_root.resolve(strict=True)
    if arguments.verify:
        if arguments.generated_at_utc is not None:
            raise CurrentProjectionError(
                "--verify cannot use --generated-at-utc"
            )
        now_utc = arguments.now_utc or (
            datetime.now(timezone.utc)
            .isoformat(timespec="microseconds")
            .replace("+00:00", "Z")
        )
        print(json.dumps(
            verify_projection(root, now_utc, arguments.max_age_seconds),
            separators=(",", ":"),
            sort_keys=True,
        ))
        return 0
    if arguments.now_utc is not None or arguments.generated_at_utc is None:
        raise CurrentProjectionError(
            "generation requires --generated-at-utc and cannot use --now-utc"
        )
    _parse_utc(arguments.generated_at_utc, "generatedAtUtc")
    projection = build_projection(root, arguments.generated_at_utc)
    output, size = write_projection(root, projection)
    print(json.dumps({
        "authoritySetHash": projection["authority"]["authoritySetHash"],
        "path": str(output),
        "revision": projection["authority"]["revision"],
        "utf8Bytes": size,
    }, separators=(",", ":"), sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
