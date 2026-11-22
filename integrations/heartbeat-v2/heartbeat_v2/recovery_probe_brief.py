"""Deterministic internal recovery-probe brief generation and validation."""

from __future__ import annotations

import re
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

from .common import (
    ContractError,
    atomic_write_json,
    canonical_json_bytes,
    format_utc,
    parse_utc,
    read_json_object,
    sha256_tag,
)
from .paths import (
    is_contained,
    resolve_existing_file,
    validate_existing_root,
    validate_output_file,
)


RECOVERY_PROBE_BRIEF_RELATIVE_PATH = Path(
    ".cortex/generated/recovery-probe-brief.json"
)
MAXIMUM_RECOVERY_PROBE_BRIEF_BYTES = 8192
MAXIMUM_AUTHORITY_FILE_BYTES = 262_144
MAXIMUM_AUTHORITY_TOTAL_BYTES = 1_048_576
RECOVERY_ORDER = (
    "START_HERE.md",
    "AGENTS.md",
    ".cortex/README.md",
    ".cortex/STATE.md",
    ".cortex/WORKLOG.md",
    ".cortex/LEARNINGS.md",
    ".cortex/SESSIONS.md",
    ".cortex/TASKS.md",
    ".cortex/RECOVERY.md",
    ".cortex/GUIDE.md",
    ".cortex/RULES.md",
)
_TASK_ID = re.compile(r"`([0-9a-f]{8,64})`", re.IGNORECASE)
_CHECKPOINT_NEAR_LABEL = re.compile(
    r"(?is)(?:current accepted source checkpoint|accepted source checkpoint)"
    r".{0,180}?`(1\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-Z])`"
)
_CHECKPOINT_BEFORE_LABEL = re.compile(
    r"(?is)`(1\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-Z])`"
    r".{0,120}?(?:current accepted source checkpoint|accepted source checkpoint)"
)


@dataclass(frozen=True)
class RecoveryProbeContext:
    """One read-only recovery projection plus its exact source bindings."""

    root: Path
    task: dict[str, Any]
    gate: str
    next_action: str
    checkpoint: dict[str, str] | None
    recovery_order: tuple[str, ...]
    source_hashes: dict[str, str]
    source_payload_bytes: int


def read_recovery_context(
    project_root: Path,
    *,
    require_exact_recovery_order: bool = True,
    checkpoint_heading: str | None = None,
) -> RecoveryProbeContext:
    """Read common Cortex authority without writing or inheriting conversation state.

    Recovery-probe briefs require the exact local order. Cross-project benchmarks may
    accept a project-specific suffix, but the complete common order must remain
    the leading authority prefix.
    """

    root = validate_existing_root(project_root)
    texts, hashes = _read_authority(root)
    parsed_order = _parse_recovery_order(texts["AGENTS.md"])
    if require_exact_recovery_order:
        order_matches = parsed_order == RECOVERY_ORDER
    else:
        order_matches = parsed_order[: len(RECOVERY_ORDER)] == RECOVERY_ORDER
    if not order_matches:
        expected = "exact authority set" if require_exact_recovery_order else "common authority prefix"
        raise ContractError(f"AGENTS.md recovery order does not match the {expected}")

    if checkpoint_heading is None:
        checkpoint = _parse_checkpoint(
            texts["START_HERE.md"],
            texts[".cortex/STATE.md"],
        )
    else:
        checkpoint_section = _optional_section(
            texts[".cortex/STATE.md"],
            checkpoint_heading,
        )
        checkpoint = (
            None
            if checkpoint_section is None
            else {
                "representation": "section-sha256",
                "source": sha256_tag(checkpoint_section.encode("utf-8")),
            }
        )

    return RecoveryProbeContext(
        root=root,
        task=_parse_current_task(texts[".cortex/TASKS.md"]),
        gate=_first_paragraph(_section(texts["START_HERE.md"], "Current Working Rule")),
        next_action=_first_paragraph(_section(texts["START_HERE.md"], "Next Step")),
        checkpoint=checkpoint,
        recovery_order=parsed_order,
        source_hashes=hashes,
        source_payload_bytes=sum(len(text.encode("utf-8")) for text in texts.values()),
    )


def build_recovery_probe_brief(
    project_root: Path,
    *,
    generated_at_utc: datetime,
    max_age_seconds: int = 86_400,
) -> dict[str, Any]:
    """Build one bounded brief from the exact recovery-order authority set."""

    if not 300 <= max_age_seconds <= 604_800:
        raise ContractError("max_age_seconds must be from 300 to 604800")
    context = read_recovery_context(project_root)
    root = context.root
    generated = generated_at_utc.astimezone(timezone.utc).replace(microsecond=0)
    root_id = _root_id(root)
    context_set_id = _context_set_id(root_id, context.source_hashes)
    brief = {
        "schema": "pegasus.cortex.recovery-probe-brief/1.0",
        "project": "Pegasus Cortex",
        "rootId": root_id,
        "contextSetId": context_set_id,
        "generatedAtUtc": format_utc(generated),
        "expiresAtUtc": format_utc(generated + timedelta(seconds=max_age_seconds)),
        "maxAgeSeconds": max_age_seconds,
        "currentTask": context.task,
        "gate": _clip(context.gate, 640),
        "nextAction": _clip(context.next_action, 760),
        "checkpoint": context.checkpoint,
        "recoveryOrder": list(RECOVERY_ORDER),
        "sourceHashes": context.source_hashes,
        "authority": {
            "localFilesDecisive": True,
            "disposable": True,
            "failClosedOnStaleOrMismatch": True,
            "parentTranscriptInherited": False,
        },
    }
    if len(canonical_json_bytes(brief) + b"\n") > MAXIMUM_RECOVERY_PROBE_BRIEF_BYTES:
        raise ContractError(
            "recovery-probe brief exceeds "
            f"{MAXIMUM_RECOVERY_PROBE_BRIEF_BYTES} UTF-8 bytes"
        )
    return brief


def write_recovery_probe_brief(
    project_root: Path,
    output_path: Path,
    brief: dict[str, Any],
) -> int:
    """Write only the approved internal recovery-probe location."""

    root = validate_existing_root(project_root)
    expected = validate_output_file(
        root,
        RECOVERY_PROBE_BRIEF_RELATIVE_PATH.as_posix(),
    )
    actual = output_path.resolve(strict=False)
    if actual != expected:
        raise ContractError(
            "recovery-probe brief output must be "
            f"{RECOVERY_PROBE_BRIEF_RELATIVE_PATH.as_posix()}"
        )
    if not is_contained(root, actual):
        raise ContractError("recovery-probe brief output escapes the project root")
    byte_count = atomic_write_json(
        actual,
        brief,
        MAXIMUM_RECOVERY_PROBE_BRIEF_BYTES,
    )
    # Recheck the completed path so a path substitution never becomes accepted
    # output. Python cannot provide a portable handle-relative Windows write;
    # this is a fail-closed postcondition around the atomic same-directory move.
    completed = validate_output_file(
        root,
        RECOVERY_PROBE_BRIEF_RELATIVE_PATH.as_posix(),
    )
    if completed != actual:
        raise ContractError(
            "completed recovery-probe brief path no longer matches the approved output"
        )
    return byte_count


def validate_recovery_probe_brief(
    project_root: Path,
    brief_path: Path,
    *,
    now_utc: datetime | None = None,
) -> dict[str, Any]:
    """Recompute every source binding and fail closed on stale or altered data."""

    root = validate_existing_root(project_root)
    expected_path = validate_output_file(
        root,
        RECOVERY_PROBE_BRIEF_RELATIVE_PATH.as_posix(),
    )
    actual_path = brief_path.resolve(strict=False)
    if actual_path != expected_path or not is_contained(root, actual_path):
        raise ContractError(
            "brief path is not the approved internal recovery-probe location"
        )

    brief = read_json_object(actual_path, MAXIMUM_RECOVERY_PROBE_BRIEF_BYTES)
    generated = parse_utc(brief.get("generatedAtUtc"), "generatedAtUtc")
    expires = parse_utc(brief.get("expiresAtUtc"), "expiresAtUtc")
    maximum_age = brief.get("maxAgeSeconds")
    if not isinstance(maximum_age, int) or isinstance(maximum_age, bool):
        raise ContractError("maxAgeSeconds must be an integer")
    if expires != generated + timedelta(seconds=maximum_age):
        raise ContractError("brief expiry does not match generatedAtUtc plus maxAgeSeconds")

    now = (now_utc or datetime.now(timezone.utc)).astimezone(timezone.utc)
    if generated > now + timedelta(seconds=60):
        raise ContractError("brief generatedAtUtc is unacceptably in the future")
    if now >= expires:
        raise ContractError("recovery-probe brief is stale")

    expected = build_recovery_probe_brief(
        root,
        generated_at_utc=generated,
        max_age_seconds=maximum_age,
    )
    if canonical_json_bytes(brief) != canonical_json_bytes(expected):
        raise ContractError(
            "recovery-probe brief does not match current authoritative files"
        )
    return {
        "status": "valid",
        "contextSetId": brief["contextSetId"],
        "expiresAtUtc": brief["expiresAtUtc"],
        "bytes": len(canonical_json_bytes(brief) + b"\n"),
    }


def _read_authority(root: Path) -> tuple[dict[str, str], dict[str, str]]:
    texts: dict[str, str] = {}
    hashes: dict[str, str] = {}
    total = 0
    for relative in RECOVERY_ORDER:
        path = resolve_existing_file(root, relative)
        payload = path.read_bytes()
        total += len(payload)
        if len(payload) > MAXIMUM_AUTHORITY_FILE_BYTES:
            raise ContractError(f"authority file exceeds byte limit: {relative}")
        if total > MAXIMUM_AUTHORITY_TOTAL_BYTES:
            raise ContractError("authority set exceeds aggregate byte limit")
        try:
            text = payload.decode("utf-8")
        except UnicodeDecodeError as exc:
            raise ContractError(f"authority file is not UTF-8: {relative}") from exc
        texts[relative] = text
        hashes[relative] = sha256_tag(payload)
    return texts, hashes


def _parse_recovery_order(agents_text: str) -> tuple[str, ...]:
    section = _section(agents_text, "Recovery Order")
    values: list[str] = []
    for line in section.splitlines():
        match = re.match(r"^\s*\d+\.\s+`([^`]+)`\s*$", line)
        if match:
            values.append(match.group(1).replace("\\", "/"))
    return tuple(values)


def _parse_current_task(tasks_text: str) -> dict[str, Any]:
    active = _section(tasks_text, "Active")
    lines = active.splitlines()
    captured: list[str] = []
    for line in lines:
        if re.match(r"^\s*-\s+\[\s\]\s+", line):
            if captured:
                break
            captured.append(re.sub(r"^\s*-\s+\[\s\]\s+", "", line))
            continue
        if captured:
            if re.match(r"^\s*-\s+\[[ xX]\]\s+", line) or line.startswith("## "):
                break
            if line.strip():
                captured.append(line.strip())
            elif len(captured) > 1:
                break
    if not captured:
        raise ContractError("TASKS.md has no unchecked task under Active")
    summary = " ".join(" ".join(captured).split())
    task_id_match = _TASK_ID.search(summary)
    return {
        "id": task_id_match.group(1) if task_id_match else None,
        "status": "active",
        "summary": _clip(summary, 520),
    }


def _parse_checkpoint(start_text: str, state_text: str) -> dict[str, str]:
    combined = _section(start_text, "Next Step") + "\n" + _section(state_text, "Next Step")
    match = _CHECKPOINT_NEAR_LABEL.search(combined) or _CHECKPOINT_BEFORE_LABEL.search(combined)
    if not match:
        raise ContractError("current accepted source checkpoint is not explicit")
    return {"source": match.group(1), "claim": "accepted-source"}


def _section(markdown: str, heading: str) -> str:
    pattern = re.compile(
        rf"(?ms)^##\s+{re.escape(heading)}\s*$\n(.*?)(?=^##\s+|\Z)"
    )
    match = pattern.search(markdown)
    if not match:
        raise ContractError(f"required Markdown section is missing: {heading}")
    return match.group(1).strip()


def _optional_section(markdown: str, heading: str) -> str | None:
    try:
        return _section(markdown, heading)
    except ContractError:
        return None


def _first_paragraph(section: str) -> str:
    paragraphs = re.split(r"\n\s*\n", section.strip(), maxsplit=1)
    value = " ".join(paragraphs[0].split())
    if not value:
        raise ContractError("required Markdown paragraph is empty")
    return value


def _context_set_id(root_id: str, hashes: dict[str, str]) -> str:
    digest = sha256_tag(
        canonical_json_bytes(
            {
                "rootId": root_id,
                "schema": "pegasus.cortex.recovery-probe-brief-sources/1.0",
                "sourceHashes": hashes,
            }
        )
    )[7:]
    return f"ctx_{digest[:32]}"


def _root_id(root: Path) -> str:
    # Only the digest is emitted. It prevents a brief copied from a different
    # approved root from being accepted without exposing the local path.
    normalized = str(root).casefold() if root.drive else str(root)
    return f"root_{sha256_tag(normalized.encode('utf-8'))[7:39]}"


def _clip(value: str, maximum: int) -> str:
    normalized = " ".join(value.split())
    if len(normalized) <= maximum:
        return normalized
    return normalized[: maximum - 3].rstrip() + "..."
