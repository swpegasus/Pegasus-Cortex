"""Shared deterministic helpers for the local Heartbeat v2 pilot."""

from __future__ import annotations

import hashlib
import json
import os
import re
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


class ContractError(ValueError):
    """Raised when an input cannot satisfy a fail-closed pilot contract."""


class CancellationRequested(RuntimeError):
    """Raised at an explicit cancellation boundary."""


_UTC_SUFFIX = "Z"
_WINDOWS_PATH = re.compile(r"(?i)\b[a-z]:\\(?:[^\\\s]+\\?)+")
_EMAIL = re.compile(r"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", re.IGNORECASE)
_IPV4 = re.compile(r"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")
_LONG_IDENTIFIER = re.compile(r"\b(?:[0-9a-f]{24,}|[A-Za-z0-9_-]{48,})\b", re.IGNORECASE)
_CREDENTIAL_ASSIGNMENT = re.compile(
    r"(?i)\b(password|passwd|secret|token|api[_-]?key|private[_-]?key)"
    r"\s*[:=]\s*[^\s,;]+"
)


def canonical_json_bytes(value: Any) -> bytes:
    """Serialize a JSON-compatible value with stable ordering and no whitespace."""

    try:
        return json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
            sort_keys=True,
        ).encode("utf-8")
    except (TypeError, ValueError) as exc:
        raise ContractError(f"value is not canonical JSON: {exc}") from exc


def sha256_tag(value: bytes) -> str:
    """Return the project's explicit algorithm-tagged digest form."""

    return f"sha256:{hashlib.sha256(value).hexdigest()}"


def parse_utc(value: str, field_name: str) -> datetime:
    """Parse an ISO-8601 UTC timestamp and reject local/offset ambiguity."""

    if not isinstance(value, str) or not value.endswith(_UTC_SUFFIX):
        raise ContractError(f"{field_name} must be an ISO-8601 UTC timestamp ending in Z")
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise ContractError(f"{field_name} is not a valid timestamp") from exc
    if parsed.tzinfo is None or parsed.utcoffset() != timezone.utc.utcoffset(parsed):
        raise ContractError(f"{field_name} must use UTC")
    return parsed.astimezone(timezone.utc)


def format_utc(value: datetime) -> str:
    """Format a timezone-aware datetime with second precision."""

    if value.tzinfo is None:
        raise ContractError("UTC formatting requires a timezone-aware datetime")
    return value.astimezone(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def require_string(value: Any, field_name: str, maximum: int, *, allow_empty: bool = False) -> str:
    """Validate a bounded string without silently truncating contract fields."""

    if not isinstance(value, str):
        raise ContractError(f"{field_name} must be a string")
    if not allow_empty and not value:
        raise ContractError(f"{field_name} must not be empty")
    if len(value) > maximum:
        raise ContractError(f"{field_name} exceeds {maximum} characters")
    if any(ord(character) < 0x20 and character not in "\t\r\n" for character in value):
        raise ContractError(f"{field_name} contains unsupported control characters")
    return value


def normalize_public_text(value: Any, field_name: str, maximum: int) -> str:
    """Normalize and sanitize a bounded public summary.

    Pilot receipts never retain these summaries. The normalized value is used
    only in the bounded operator report for a changed or attention-required run.
    """

    text = require_string(value, field_name, maximum * 4)
    text = " ".join(text.split())
    text = _CREDENTIAL_ASSIGNMENT.sub(lambda match: f"{match.group(1)}=<redacted>", text)
    text = _WINDOWS_PATH.sub("<local-path>", text)
    text = _EMAIL.sub("<email>", text)
    text = _IPV4.sub("<host>", text)
    text = _LONG_IDENTIFIER.sub("<id>", text)
    if len(text) > maximum:
        text = text[: maximum - 3].rstrip() + "..."
    return text


def read_json_object(path: Path, maximum_bytes: int) -> dict[str, Any]:
    """Read one bounded UTF-8 JSON object without accepting a BOM."""

    try:
        payload = path.read_bytes()
    except OSError as exc:
        raise ContractError(f"unable to read {path.name}: {exc}") from exc
    if len(payload) > maximum_bytes:
        raise ContractError(f"{path.name} exceeds {maximum_bytes} bytes")
    if payload.startswith(b"\xef\xbb\xbf"):
        raise ContractError(f"{path.name} must be UTF-8 without a BOM")
    try:
        value = json.loads(payload.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ContractError(f"{path.name} is not valid UTF-8 JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise ContractError(f"{path.name} must contain one JSON object")
    return value


def atomic_write_json(path: Path, value: dict[str, Any], maximum_bytes: int) -> int:
    """Atomically replace one explicitly authorized local JSON state file."""

    payload = canonical_json_bytes(value) + b"\n"
    if len(payload) > maximum_bytes:
        raise ContractError(f"serialized {path.name} exceeds {maximum_bytes} bytes")

    parent = path.parent
    parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=parent)
    temporary_path = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_path, path)
    finally:
        if temporary_path.exists():
            temporary_path.unlink()
    return len(payload)
