#!/usr/bin/env python3
"""Retain tested source bytes and seal deterministic Cortex verification manifests.

The tool creates a bounded deterministic source bundle, can execute an explicit
bounded command plan against a freshly extracted copy, and records only typed
verification metrics, command receipts, and file hashes. Raw test logs,
terminal streams, credentials, and other unbounded data are rejected.

The lightweight path boundary rejects existing links and reparse points and
rechecks paths immediately before publication. It cannot make path creation
handle-relative on every supported Python/Windows combination, so a hostile
same-user process that can replace an already validated directory concurrently
remains outside this tool's protection boundary. Run sealing in an access-
controlled project/evidence root.

The execution command plan is an explicit operator-authorized test plan, not a
sandbox. It constrains the working directory to the extracted tree and retains
no terminal output, but the invoked process keeps the caller's operating-system
authority and therefore must never be populated directly from untrusted model
or retrieved content.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import pathlib
import platform
import re
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import threading
import zipfile
from typing import Any, Iterable, Sequence


CONTRACT_ID = "pegasus.cortex.local-verification-manifest/1.3"
RESULTS_CONTRACT_ID = "pegasus.cortex.local-verification-results/1.3"
SOURCE_BUNDLE_CONTRACT_ID = "pegasus.cortex.tested-source-bundle/1.1"
EXECUTION_PLAN_CONTRACT_ID = "pegasus.cortex.verification-execution-plan/1.1"
EXECUTION_RECEIPT_CONTRACT_ID = "pegasus.cortex.verification-execution-receipt/1.1"
ACCEPTANCE_EVIDENCE_CONTRACT_ID = "pegasus.cortex.context-retention-metrics/1.1"
ACCEPTANCE_PROFILE_ID = "pegasus.cortex.context-retention-hardening/1.1"
CODEX_RECOVERY_AB_EVIDENCE_CONTRACT_ID = "pegasus.cortex.codex-recovery-ab-evidence/1.0"
BUNDLE_METADATA_NAME = "__PEGASUS_CORTEX_SOURCE_BUNDLE__.json"
BUNDLE_TIMESTAMP = (1980, 1, 1, 0, 0, 0)
BUNDLE_REGULAR_MODE = 0o100644
MAX_WINDOWS_PATH_UNITS = 240
MAX_SEGMENT_UTF8_BYTES = 255
MAX_ARGUMENTS = 96
MAX_ARGUMENT_LENGTH = 1024
MAX_COMMAND_LENGTH = 8192
MAX_EVIDENCE_FILES = 32
MAX_EVIDENCE_FILE_BYTES = 2 * 1024 * 1024
MAX_EVIDENCE_TOTAL_BYTES = 8 * 1024 * 1024
MAX_RESULTS_BYTES = 2 * 1024 * 1024
MAX_MANIFEST_BYTES = 16 * 1024 * 1024
MAX_JSON_DEPTH = 32
MAX_JSON_NODES = 100_000
MAX_PUBLIC_STRING_LENGTH = 16_384
MAX_SOURCE_FILES = 20_000
MAX_SOURCE_FILE_BYTES = 32 * 1024 * 1024
MAX_SOURCE_TOTAL_BYTES = 128 * 1024 * 1024
MAX_BUNDLE_BYTES = 160 * 1024 * 1024
MAX_COMMAND_OUTPUT_BYTES = 2 * 1024 * 1024
MAX_COMMAND_TIMEOUT_SECONDS = 3600
CODEX_RECOVERY_AB_PAIR_COUNT = 20
MAX_CODEX_RECOVERY_AB_TOKEN_COUNT = 1_000_000_000_000
MAX_CODEX_RECOVERY_AB_DURATION_MS = 86_400_000.0
MAX_CODEX_RECOVERY_AB_TOOL_CALLS = 10_000
MAX_CODEX_RECOVERY_AB_MCP_SAMPLES = 64
MAX_CODEX_RECOVERY_AB_FILE_READS = 64
MAX_CODEX_RECOVERY_AB_CAPTURE_SPAN_SECONDS = 7 * 24 * 60 * 60
CODEX_RECOVERY_AB_CONTROL_SOURCES = (
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
CODEX_RECOVERY_AB_AUTHORITY_SOURCES = (
    ".cortex/CURRENT.json",
    "START_HERE.md",
    "AGENTS.md",
    ".cortex/STATE.md",
    ".cortex/TASKS.md",
    ".cortex/RECOVERY.md",
)
CODEX_RECOVERY_AB_AUTHORITY_SOURCE_IDS = {
    "START_HERE.md": "start_here",
    "AGENTS.md": "agents",
    ".cortex/STATE.md": "cortex_state",
    ".cortex/TASKS.md": "cortex_tasks",
    ".cortex/RECOVERY.md": "cortex_recovery",
}
COMMAND_PARSERS = frozenset(
    {"codex-recovery-ab", "exit-only", "msbuild", "pegasus-harness", "python-unittest"}
)

REQUIRED_ACCEPTANCE_GATES = frozenset(
    {
        "authority-fallback",
        "codex-recovery-ab",
        "concurrent-clients",
        "deterministic-time",
        "excluded-coverage",
        "exact-search",
        "full-build",
        "harness-matrix",
        "offline-recovery",
        "project-brief-authority",
        "reserved-isolation",
        "retrieval-recall",
    }
)
ADMISSIBLE_ACCEPTANCE_SKIPS = frozenset(
    {
        (
            "mcp-local-tests",
            "recovery reparse probe",
            "Symbolic-link creation is unavailable.",
        ),
        (
            "mcp-local-tests",
            "export reparse probe",
            "Symbolic-link creation is unavailable.",
        ),
        (
            "persistence-file-tests",
            "file symlink creation unavailable",
            "IOException",
        ),
        (
            "linux-platform-tests",
            "native Linux filesystem cases",
            "current platform is not Linux",
        ),
        (
            "postgresql-integration-tests",
            "LIVE integration",
            "set PEGASUS_CORTEX_PG_INTEGRATION=1 and supply isolated loopback connections plus an explicit transport profile",
        ),
        (
            "cortex-current-tests",
            "test_linked_authority_is_rejected_when_supported",
            "the current token cannot create symbolic links",
        ),
        (
            "verification-manifest-tests",
            "test_source_inventory_rejects_link_escape_when_supported",
            "symbolic-link creation is unavailable",
        ),
        (
            "heartbeat-tests",
            "test_symlinked_authority_file_is_rejected_when_supported",
            "local symlink creation is unavailable",
        ),
        (
            "heartbeat-core-tests",
            "test_reparse_mutation_guard_fails_closed_when_supported",
            "local symlink creation is unavailable",
        ),
        (
            "sync-client-tests",
            "watcher rejects an intermediate reparse path",
            "the host denied test-only symbolic-link creation",
        ),
        (
            "arm64-package-contract-tests",
            "test_package_root_path_rejects_relative_and_linked_components",
            "directory symbolic-link creation is unavailable",
        ),
        (
            "arm64-package-contract-tests",
            "test_bounded_subprocess_timeout_reaps_same_group_descendants",
            "Process-group cleanup is POSIX-only.",
        ),
        (
            "arm64-package-contract-tests",
            "test_bounded_subprocess_preserves_direct_signal_as_negative",
            "POSIX signal return codes are Linux-only.",
        ),
        (
            "common-release-manifest-tests",
            "test_linked_evidence_is_rejected_when_supported",
            "symbolic-link creation is unavailable",
        ),
        (
            "common-release-manifest-tests",
            "test_hard_linked_evidence_is_rejected_when_supported",
            "hard-link creation is unavailable",
        ),
    }
)
GATE_STATUSES = frozenset({"passed", "failed", "blocked", "skipped"})

DEFAULT_SOURCE_PATHS = (
    ".cortex",
    "AGENTS.md",
    "Directory.Build.props",
    "Directory.Version.props",
    "NuGet.Config",
    "PegasusCortex.slnx",
    "START_HERE.md",
    "VERSION.json",
    "deploy",
    "docs",
    "global.json",
    "integrations",
    "licenses",
    "schemas",
    "src",
    "tests",
    "tools",
)
MANDATORY_AUTHORITY_FILES = frozenset(
    {
        ".cortex/CURRENT.json",
        ".cortex/GUIDE.md",
        ".cortex/LEARNINGS.md",
        ".cortex/README.md",
        ".cortex/RECOVERY.md",
        ".cortex/RULES.md",
        ".cortex/SESSIONS.md",
        ".cortex/STATE.md",
        ".cortex/TASKS.md",
        ".cortex/WORKLOG.md",
        ".cortex/meta.json",
        "AGENTS.md",
        "START_HERE.md",
    }
)

EXCLUDED_DIRECTORY_NAMES = {
    ".cortex",
    ".git",
    ".idea",
    ".pytest_cache",
    ".vs",
    ".vscode",
    "__pycache__",
    "artifacts",
    "bin",
    "coverage",
    "logs",
    "node_modules",
    "obj",
    "packages",
    "testresults",
}

PROHIBITED_EVIDENCE_SUFFIXES = {
    ".core",
    ".dmp",
    ".etl",
    ".har",
    ".log",
    ".out",
    ".pcap",
    ".pcapng",
    ".trace",
    ".trx",
}

PROHIBITED_SECRET_SUFFIXES = {".key", ".p12", ".pfx", ".snk"}
PROHIBITED_SECRET_NAMES = {
    ".env",
    "credentials",
    "credentials.json",
    "id_ed25519",
    "id_rsa",
    "secrets",
    "secrets.json",
}

WINDOWS_RESERVED_NAMES = {
    "CON",
    "PRN",
    "AUX",
    "NUL",
    "CLOCK$",
    "CONIN$",
    "CONOUT$",
    *(f"COM{index}" for index in range(1, 10)),
    *(f"LPT{index}" for index in range(1, 10)),
    "COM¹",
    "COM²",
    "COM³",
    "LPT¹",
    "LPT²",
    "LPT³",
}
WINDOWS_INVALID_PATH_CHARACTERS = frozenset('<>"|?*')

SAFE_ID = re.compile(r"^[a-z0-9][a-z0-9._-]{0,63}$")
SECRET_ASSIGNMENT = re.compile(
    r"(?i)(?:password|passwd|secret|token|api[-_]?key|connection[-_]?string)\s*[:=]"
)
BEARER_VALUE = re.compile(r"(?i)\bbearer\s+[a-z0-9._~+/=-]{8,}")
URI_USERINFO = re.compile(r"\b[a-z][a-z0-9+.-]*://[^/@\s]+:[^/@\s]+@", re.IGNORECASE)
SECRET_FLAGS = {
    "--api-key",
    "--connection-string",
    "--password",
    "--private-key",
    "--secret",
    "--token",
    "/password",
    "/token",
}
SECRET_JSON_KEYS = {
    "accesstoken",
    "apikey",
    "authorization",
    "bearertoken",
    "connectionstring",
    "credential",
    "credentials",
    "password",
    "passwd",
    "privatekey",
    "secret",
    "secrets",
    "sessioncookie",
    "token",
}
PROHIBITED_EVIDENCE_KEYS = {
    "chainofthought",
    "chat",
    "content",
    "privatereasoning",
    "prompt",
    "raw",
    "rawchat",
    "rawtranscript",
    "reasoning",
    "response",
    "stderr",
    "stdout",
    "terminalstream",
    "transcript",
}
EVIDENCE_CONTRACT = re.compile(r"^pegasus\.[a-z0-9.-]+/[0-9]+\.[0-9]+$")


class ManifestError(RuntimeError):
    """Raised when manifest input or referenced state fails closed."""


def canonical_json_bytes(value: Any) -> bytes:
    """Return the byte representation used for stable hashes and seals."""

    try:
        return json.dumps(
            value,
            ensure_ascii=False,
            separators=(",", ":"),
            sort_keys=True,
        ).encode("utf-8")
    except (RecursionError, UnicodeEncodeError) as exception:
        raise ManifestError("Structured JSON is too deep or contains invalid Unicode.") from exception


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _stable_file_record(root: pathlib.Path, path: pathlib.Path) -> dict[str, Any]:
    """Hash one regular file while rejecting replacement or mutation during the read."""

    if _is_reparse_point(path):
        raise ManifestError(f"A retained file is a link or reparse point: {path}")
    before = path.stat(follow_symlinks=False)
    if not stat.S_ISREG(before.st_mode):
        raise ManifestError(f"A retained path is not a regular file: {path}")
    digest = sha256_file(path)
    after = path.stat(follow_symlinks=False)
    identity_before = (
        before.st_dev,
        before.st_ino,
        before.st_size,
        before.st_mtime_ns,
    )
    identity_after = (
        after.st_dev,
        after.st_ino,
        after.st_size,
        after.st_mtime_ns,
    )
    if identity_before != identity_after or _is_reparse_point(path):
        raise ManifestError(f"A retained file changed while it was being hashed: {path}")
    return {
        "bytes": after.st_size,
        "path": path.relative_to(root).as_posix(),
        "sha256": digest,
    }


def _normalized_public_key(value: str) -> str:
    return "".join(character for character in value.casefold() if character.isalnum())


def _screen_public_text(value: str, field: str, maximum: int) -> str:
    """Validate text that will be retained in a public structured receipt."""

    if not value or len(value) > maximum:
        raise ManifestError(f"{field} must be a non-empty string of at most {maximum} characters.")
    if any(ord(character) < 32 or ord(character) == 127 for character in value):
        raise ManifestError(f"{field} may not contain control characters.")
    if (
        SECRET_ASSIGNMENT.search(value)
        or BEARER_VALUE.search(value)
        or URI_USERINFO.search(value)
    ):
        raise ManifestError(f"{field} appears to contain credential material.")
    return value


def _unique_object_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ManifestError(f"Structured JSON contains a duplicate object key: {key}")
        result[key] = value
    return result


def _parse_json_bytes(data: bytes, field: str) -> Any:
    try:
        text = data.decode("utf-8")
        return json.loads(
            text,
            object_pairs_hook=_unique_object_pairs,
            parse_constant=lambda value: (_ for _ in ()).throw(
                ManifestError(f"{field} contains a non-finite JSON number: {value}")
            ),
        )
    except UnicodeDecodeError as exception:
        raise ManifestError(f"{field} is not valid UTF-8 JSON.") from exception
    except (json.JSONDecodeError, RecursionError) as exception:
        raise ManifestError(f"{field} is not valid UTF-8 JSON.") from exception


def _screen_json_value(value: Any, field: str) -> None:
    """Bound and screen every key and string in retained structured JSON."""

    nodes = 0

    def visit(current: Any, location: str, depth: int) -> None:
        nonlocal nodes
        nodes += 1
        if nodes > MAX_JSON_NODES:
            raise ManifestError(f"{field} exceeds the structured JSON node budget.")
        if depth > MAX_JSON_DEPTH:
            raise ManifestError(f"{field} exceeds the structured JSON depth budget.")
        if current is None or isinstance(current, (bool, int)):
            return
        if isinstance(current, float):
            if current != current or current in {float("inf"), float("-inf")}:
                raise ManifestError(f"{field} contains a non-finite number.")
            return
        if isinstance(current, str):
            _screen_public_text(current, location, MAX_PUBLIC_STRING_LENGTH)
            return
        if isinstance(current, list):
            for index, item in enumerate(current):
                visit(item, f"{location}[{index}]", depth + 1)
            return
        if isinstance(current, dict):
            for key, item in current.items():
                if not isinstance(key, str):
                    raise ManifestError(f"{field} contains a non-string object key.")
                _screen_public_text(key, f"{location} key", 160)
                normalized = _normalized_public_key(key)
                if normalized in SECRET_JSON_KEYS:
                    raise ManifestError(f"{field} contains a prohibited credential field: {key}")
                if normalized in PROHIBITED_EVIDENCE_KEYS:
                    raise ManifestError(f"{field} contains a prohibited raw-content field: {key}")
                visit(item, f"{location}.{key}", depth + 1)
            return
        raise ManifestError(f"{field} contains an unsupported JSON value.")

    visit(value, field, 0)


def _read_bounded_json_payload(
    path: pathlib.Path,
    *,
    maximum_bytes: int,
    field: str,
) -> tuple[Any, bytes]:
    if _is_reparse_point(path):
        raise ManifestError(f"{field} is a link or reparse point.")
    before = path.stat(follow_symlinks=False)
    if not stat.S_ISREG(before.st_mode):
        raise ManifestError(f"{field} is not a regular file.")
    if before.st_size > maximum_bytes:
        raise ManifestError(f"{field} exceeds the {maximum_bytes}-byte budget.")
    data = path.read_bytes()
    after = path.stat(follow_symlinks=False)
    before_identity = (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns)
    after_identity = (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns)
    if len(data) != before.st_size or before_identity != after_identity or _is_reparse_point(path):
        raise ManifestError(f"{field} changed while it was being read.")
    value = _parse_json_bytes(data, field)
    _screen_json_value(value, field)
    return value, data


def _read_bounded_json(
    path: pathlib.Path,
    *,
    maximum_bytes: int,
    field: str,
) -> Any:
    value, _ = _read_bounded_json_payload(
        path,
        maximum_bytes=maximum_bytes,
        field=field,
    )
    return value


def _utf16_units(value: str) -> int:
    try:
        return len(value.encode("utf-16-le")) // 2
    except UnicodeEncodeError as exception:
        raise ManifestError("Paths must contain valid Unicode scalar values.") from exception


def _is_reparse_point(path: pathlib.Path) -> bool:
    try:
        details = path.lstat()
    except FileNotFoundError:
        return False
    attributes = getattr(details, "st_file_attributes", 0)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return path.is_symlink() or bool(attributes & reparse_flag)


def _validate_segment(segment: str) -> None:
    if not segment or segment in {".", ".."}:
        raise ManifestError("Relative paths may not contain empty, dot, or parent segments.")
    if ":" in segment:
        raise ManifestError("Relative paths may not contain alternate-data-stream separators.")
    if any(ord(character) < 32 or ord(character) == 127 for character in segment):
        raise ManifestError("Relative paths may not contain control characters.")
    if any(character in WINDOWS_INVALID_PATH_CHARACTERS for character in segment):
        raise ManifestError("A relative path contains an invalid Windows path character.")
    if segment.endswith((".", " ")):
        raise ManifestError("Relative paths may not rely on trailing-dot or trailing-space normalization.")
    try:
        encoded = segment.encode("utf-8")
    except UnicodeEncodeError as exception:
        raise ManifestError("Relative paths must contain valid Unicode scalar values.") from exception
    if len(encoded) > MAX_SEGMENT_UTF8_BYTES:
        raise ManifestError("A relative path segment exceeds the UTF-8 byte budget.")
    stem = segment.split(".", 1)[0].rstrip(" .").upper()
    if stem in WINDOWS_RESERVED_NAMES:
        raise ManifestError("A relative path contains a reserved Windows device name.")


def _normalized_relative(value: str | os.PathLike[str], *, allow_root: bool) -> str:
    text = os.fspath(value).replace("\\", "/")
    if text == "." and allow_root:
        return "."
    if (
        not text
        or text.startswith("/")
        or text.startswith("//")
        or re.match(r"^[a-zA-Z]:", text)
    ):
        raise ManifestError("Only project-relative paths are accepted.")
    parts = text.split("/")
    for segment in parts:
        _validate_segment(segment)
    return "/".join(parts)


def resolve_project_path(
    root: pathlib.Path,
    value: str | os.PathLike[str],
    *,
    allow_root: bool = False,
    must_exist: bool = True,
    require_file: bool | None = None,
) -> tuple[pathlib.Path, str]:
    """Resolve one path beneath the trusted root without following links."""

    root = root.resolve(strict=True)
    relative = _normalized_relative(value, allow_root=allow_root)
    if relative == ".":
        candidate = root
        parts: Sequence[str] = ()
    else:
        parts = tuple(relative.split("/"))
        candidate = root.joinpath(*parts)

    if _utf16_units(str(candidate)) > MAX_WINDOWS_PATH_UNITS:
        raise ManifestError("The resolved path exceeds the 240 UTF-16 unit budget.")

    current = root
    checked_parts = parts if must_exist else parts[:-1]
    for segment in checked_parts:
        current = current / segment
        if not current.exists():
            if must_exist:
                raise ManifestError(f"Referenced path does not exist: {relative}")
            break
        if _is_reparse_point(current):
            raise ManifestError(f"Referenced path crosses a link or reparse point: {relative}")

    if must_exist:
        if not candidate.exists():
            raise ManifestError(f"Referenced path does not exist: {relative}")
        resolved = candidate.resolve(strict=True)
    else:
        parent = candidate.parent
        if not parent.exists() or not parent.is_dir():
            raise ManifestError(f"Output parent does not exist: {relative}")
        resolved = parent.resolve(strict=True) / candidate.name

    try:
        resolved.relative_to(root)
    except ValueError as exception:
        raise ManifestError("Resolved path escapes the project root.") from exception

    if require_file is True and (not candidate.exists() or not candidate.is_file()):
        raise ManifestError(f"Expected a regular file: {relative}")
    if require_file is False and (not candidate.exists() or not candidate.is_dir()):
        raise ManifestError(f"Expected a directory: {relative}")
    return candidate, relative


def validate_project_relative_reference(
    root: pathlib.Path,
    value: str | os.PathLike[str],
    *,
    allow_root: bool = False,
) -> str:
    """Validate a historical project-relative reference without requiring it to remain."""

    root = root.resolve(strict=True)
    relative = _normalized_relative(value, allow_root=allow_root)
    parts: Sequence[str] = () if relative == "." else tuple(relative.split("/"))
    candidate = root.joinpath(*parts)
    if _utf16_units(str(candidate)) > MAX_WINDOWS_PATH_UNITS:
        raise ManifestError("The referenced path exceeds the 240 UTF-16 unit budget.")
    current = root
    for segment in parts:
        current = current / segment
        if not current.exists():
            break
        if _is_reparse_point(current):
            raise ManifestError(f"Referenced path crosses a link or reparse point: {relative}")
    return relative


def _path_is_sensitive(relative: str) -> bool:
    parts = pathlib.PurePosixPath(relative).parts
    lowered = [part.casefold() for part in parts]
    name = lowered[-1]
    suffix = pathlib.PurePosixPath(name).suffix
    if name in PROHIBITED_SECRET_NAMES or suffix in PROHIBITED_SECRET_SUFFIXES:
        return True
    return any(part in {".secrets", "credentials", "secrets"} for part in lowered[:-1])


def _validate_evidence_path(relative: str) -> None:
    path = pathlib.PurePosixPath(relative)
    lowered_name = path.name.casefold()
    if _path_is_sensitive(relative):
        raise ManifestError(f"Secret-bearing files are prohibited evidence: {relative}")
    if path.suffix.casefold() in PROHIBITED_EVIDENCE_SUFFIXES:
        raise ManifestError(f"Raw log or stream files are prohibited evidence: {relative}")
    if lowered_name in {"stderr", "stderr.txt", "stdout", "stdout.txt", "transcript.txt"}:
        raise ManifestError(f"Raw terminal or transcript files are prohibited evidence: {relative}")
    if path.suffix.casefold() != ".json":
        raise ManifestError(f"Evidence must use bounded structured JSON: {relative}")


def _record_evidence_file(root: pathlib.Path, path: pathlib.Path) -> dict[str, Any]:
    relative = path.relative_to(root).as_posix()
    document, data = _read_bounded_json_payload(
        path,
        maximum_bytes=MAX_EVIDENCE_FILE_BYTES,
        field=f"evidence {relative}",
    )
    if not isinstance(document, dict):
        raise ManifestError(f"Evidence must be a JSON object: {relative}")
    contract_id = document.get("contractId")
    if not isinstance(contract_id, str) or not EVIDENCE_CONTRACT.fullmatch(contract_id):
        raise ManifestError(f"Evidence has an invalid or missing contractId: {relative}")
    record = {
        "bytes": len(data),
        "path": relative,
        "sha256": sha256_bytes(data),
    }
    record["contractId"] = contract_id
    return record


def _record_file(root: pathlib.Path, path: pathlib.Path) -> dict[str, Any]:
    return _stable_file_record(root, path)


def _inventory_digest(records: Sequence[dict[str, Any]]) -> str:
    return sha256_bytes(canonical_json_bytes(list(records)))


def _excluded_directory(name: str) -> bool:
    return name.casefold() in EXCLUDED_DIRECTORY_NAMES


def collect_source_inventory(
    root: pathlib.Path,
    source_paths: Sequence[str],
    *,
    excluded_files: Iterable[pathlib.Path] = (),
) -> dict[str, Any]:
    root = root.resolve(strict=True)
    excluded = {path.resolve(strict=False) for path in excluded_files}
    discovered: dict[str, pathlib.Path] = {}
    casefolded: dict[str, str] = {}

    def add_file(path: pathlib.Path) -> None:
        if path.resolve(strict=False) in excluded:
            return
        relative = path.relative_to(root).as_posix()
        if _path_is_sensitive(relative):
            return
        if pathlib.PurePosixPath(relative).suffix.casefold() in PROHIBITED_EVIDENCE_SUFFIXES:
            return
        if _is_reparse_point(path):
            raise ManifestError(f"Source inventory contains a link or reparse point: {relative}")
        folded = relative.casefold()
        previous = casefolded.get(folded)
        if previous is not None and previous != relative:
            raise ManifestError(f"Source paths collide under Windows casing: {previous}, {relative}")
        casefolded[folded] = relative
        discovered[relative] = path

    normalized_roots: list[str] = []
    for source_value in source_paths:
        source, relative = resolve_project_path(
            root,
            source_value,
            allow_root=True,
            must_exist=True,
        )
        normalized_roots.append(relative)
        if source.is_file():
            add_file(source)
            continue

        for current_root, directory_names, file_names in os.walk(source, followlinks=False):
            current = pathlib.Path(current_root)
            safe_directories: list[str] = []
            for name in sorted(directory_names, key=lambda item: (item.casefold(), item)):
                child = current / name
                if _excluded_directory(name):
                    continue
                if _is_reparse_point(child):
                    relative_child = child.relative_to(root).as_posix()
                    raise ManifestError(
                        f"Source inventory contains a link or reparse point: {relative_child}"
                    )
                safe_directories.append(name)
            directory_names[:] = safe_directories
            for name in sorted(file_names, key=lambda item: (item.casefold(), item)):
                add_file(current / name)

    records = [_record_file(root, discovered[path]) for path in sorted(discovered)]
    inventory = {
        "fileCount": len(records),
        "files": records,
        "roots": sorted(set(normalized_roots)),
        "sha256": _inventory_digest(records),
        "totalBytes": sum(record["bytes"] for record in records),
    }
    _validate_source_inventory_bounds(inventory)
    return inventory


def _validate_source_inventory_bounds(inventory: dict[str, Any]) -> None:
    records = inventory.get("files")
    if not isinstance(records, list) or not records:
        raise ManifestError("The tested source inventory may not be empty.")
    if len(records) > MAX_SOURCE_FILES:
        raise ManifestError("The tested source inventory exceeds the file-count budget.")
    total = 0
    for record in records:
        size = record.get("bytes") if isinstance(record, dict) else None
        if isinstance(size, bool) or not isinstance(size, int) or size < 0:
            raise ManifestError("The tested source inventory contains an invalid file size.")
        if size > MAX_SOURCE_FILE_BYTES:
            raise ManifestError("A tested source file exceeds the per-file byte budget.")
        total += size
    if total > MAX_SOURCE_TOTAL_BYTES:
        raise ManifestError("The tested source inventory exceeds the aggregate byte budget.")


def _validate_public_text(value: Any, field: str, maximum: int) -> str:
    if not isinstance(value, str):
        raise ManifestError(f"{field} must be a non-empty string of at most {maximum} characters.")
    return _screen_public_text(value, field, maximum)


def _validate_command_arguments(argv: Any) -> list[str]:
    if not isinstance(argv, list) or not argv or len(argv) > MAX_ARGUMENTS:
        raise ManifestError("argv must be a non-empty bounded string array.")
    validated: list[str] = []
    for value in argv:
        text = _validate_public_text(value, "argv entry", MAX_ARGUMENT_LENGTH)
        lowered = text.casefold()
        if (
            lowered in SECRET_FLAGS
            or SECRET_ASSIGNMENT.search(text)
            or BEARER_VALUE.search(text)
            or URI_USERINFO.search(text)
        ):
            raise ManifestError("A recorded command appears to contain credential material.")
        validated.append(text)
    if sum(len(value) for value in validated) > MAX_COMMAND_LENGTH:
        raise ManifestError("The recorded command exceeds the bounded character budget.")
    return validated


def _require_exact_keys(value: dict[str, Any], allowed: set[str], field: str) -> None:
    unknown = set(value) - allowed
    if unknown:
        raise ManifestError(f"{field} contains unsupported fields: {', '.join(sorted(unknown))}")


def _nonnegative_integer(value: Any, field: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ManifestError(f"{field} must be a non-negative integer.")
    return value


def load_verification_results(root: pathlib.Path, value: str) -> tuple[dict[str, Any], dict[str, Any]]:
    path, relative = resolve_project_path(root, value, must_exist=True, require_file=True)
    if pathlib.PurePosixPath(relative).suffix.casefold() != ".json":
        raise ManifestError("The structured results input must be JSON.")
    _validate_evidence_path(relative)
    document, results_data = _read_bounded_json_payload(
        path,
        maximum_bytes=MAX_RESULTS_BYTES,
        field="structured results input",
    )
    if not isinstance(document, dict):
        raise ManifestError("The structured results input must be a JSON object.")
    _require_exact_keys(
        document,
        {
            "acceptanceProfileId",
            "additionalGates",
            "commands",
            "contractId",
            "evidencePaths",
            "executionReceiptPath",
        },
        "results",
    )
    if document.get("contractId") != RESULTS_CONTRACT_ID:
        raise ManifestError(f"Results contractId must be {RESULTS_CONTRACT_ID}.")
    acceptance_profile_id = document.get("acceptanceProfileId")
    if acceptance_profile_id != ACCEPTANCE_PROFILE_ID:
        raise ManifestError(f"acceptanceProfileId must be {ACCEPTANCE_PROFILE_ID}.")
    commands_value = document.get("commands")
    if not isinstance(commands_value, list) or not commands_value:
        raise ManifestError("Results must contain at least one command summary.")

    command_ids: set[str] = set()
    commands: list[dict[str, Any]] = []
    for index, command_value in enumerate(commands_value):
        if not isinstance(command_value, dict):
            raise ManifestError(f"commands[{index}] must be an object.")
        _require_exact_keys(
            command_value,
            {"argv", "checks", "exitCode", "id", "skipDetails", "workingDirectory"},
            f"commands[{index}]",
        )
        command_id = command_value.get("id")
        if not isinstance(command_id, str) or not SAFE_ID.fullmatch(command_id):
            raise ManifestError(f"commands[{index}].id is invalid.")
        if command_id in command_ids:
            raise ManifestError(f"Duplicate command id: {command_id}")
        command_ids.add(command_id)

        working_directory_value = command_value.get("workingDirectory")
        if not isinstance(working_directory_value, str):
            raise ManifestError(f"commands[{index}].workingDirectory must be a string.")
        working_directory = validate_project_relative_reference(
            root,
            working_directory_value,
            allow_root=True,
        )
        exit_code = command_value.get("exitCode")
        if isinstance(exit_code, bool) or not isinstance(exit_code, int):
            raise ManifestError(f"commands[{index}].exitCode must be an integer.")

        checks_value = command_value.get("checks")
        if not isinstance(checks_value, dict):
            raise ManifestError(f"commands[{index}].checks must be an object.")
        _require_exact_keys(checks_value, {"failed", "passed", "skipped"}, "checks")
        checks = {
            "failed": _nonnegative_integer(checks_value.get("failed"), "checks.failed"),
            "passed": _nonnegative_integer(checks_value.get("passed"), "checks.passed"),
            "skipped": _nonnegative_integer(checks_value.get("skipped"), "checks.skipped"),
        }

        skip_details_value = command_value.get("skipDetails", [])
        if not isinstance(skip_details_value, list):
            raise ManifestError(f"commands[{index}].skipDetails must be an array.")
        skip_details: list[dict[str, str]] = []
        for detail_index, detail_value in enumerate(skip_details_value):
            if not isinstance(detail_value, dict):
                raise ManifestError("Each skip detail must be an object.")
            _require_exact_keys(detail_value, {"name", "reason"}, "skip detail")
            skip_details.append(
                {
                    "name": _validate_public_text(
                        detail_value.get("name"),
                        f"skipDetails[{detail_index}].name",
                        160,
                    ),
                    "reason": _validate_public_text(
                        detail_value.get("reason"),
                        f"skipDetails[{detail_index}].reason",
                        320,
                    ),
                }
            )
        if len(skip_details) != checks["skipped"]:
            raise ManifestError("The skipped count must equal the number of explicit skip details.")

        commands.append(
            {
                "argv": _validate_command_arguments(command_value.get("argv")),
                "checks": checks,
                "exitCode": exit_code,
                "id": command_id,
                "skipDetails": sorted(skip_details, key=lambda item: (item["name"], item["reason"])),
                "workingDirectory": working_directory,
            }
        )

    gates_value = document.get("additionalGates")
    if not isinstance(gates_value, list):
        raise ManifestError("additionalGates must be an array.")
    gate_ids: set[str] = set()
    gates: list[dict[str, str]] = []
    for index, gate_value in enumerate(gates_value):
        if not isinstance(gate_value, dict):
            raise ManifestError(f"gates[{index}] must be an object.")
        _require_exact_keys(gate_value, {"id", "status", "summary"}, f"gates[{index}]")
        gate_id = gate_value.get("id")
        if not isinstance(gate_id, str) or not SAFE_ID.fullmatch(gate_id):
            raise ManifestError(f"additionalGates[{index}].id is invalid.")
        if gate_id in REQUIRED_ACCEPTANCE_GATES:
            raise ManifestError(f"Derived acceptance gate may not be supplied as an additional gate: {gate_id}")
        if gate_id in gate_ids:
            raise ManifestError(f"Duplicate gate id: {gate_id}")
        gate_ids.add(gate_id)
        status = gate_value.get("status")
        if not isinstance(status, str) or status not in GATE_STATUSES:
            raise ManifestError(f"additionalGates[{index}].status is invalid.")
        gates.append(
            {
                "id": gate_id,
                "status": status,
                "summary": _validate_public_text(
                    gate_value.get("summary"),
                    f"gates[{index}].summary",
                    320,
                ),
            }
        )

    evidence_paths_value = document.get("evidencePaths")
    if not isinstance(evidence_paths_value, list) or not evidence_paths_value:
        raise ManifestError("evidencePaths must be a non-empty array.")
    if len(evidence_paths_value) > MAX_EVIDENCE_FILES:
        raise ManifestError("evidencePaths exceeds the file-count budget.")
    evidence_paths: list[str] = []
    for index, evidence_value in enumerate(evidence_paths_value):
        if not isinstance(evidence_value, str):
            raise ManifestError(f"evidencePaths[{index}] must be a string.")
        _, evidence_relative = resolve_project_path(
            root,
            evidence_value,
            must_exist=True,
            require_file=True,
        )
        _validate_evidence_path(evidence_relative)
        evidence_paths.append(evidence_relative)

    execution_receipt_value = document.get("executionReceiptPath")
    if not isinstance(execution_receipt_value, str):
        raise ManifestError("executionReceiptPath must be a project-relative JSON path.")
    _, execution_receipt_path = resolve_project_path(
        root,
        execution_receipt_value,
        must_exist=True,
        require_file=True,
    )
    _validate_evidence_path(execution_receipt_path)

    sanitized = {
        "acceptanceProfileId": acceptance_profile_id,
        "additionalGates": sorted(gates, key=lambda item: item["id"]),
        "commands": sorted(commands, key=lambda item: item["id"]),
        "evidencePaths": sorted(set(evidence_paths)),
        "executionReceiptPath": execution_receipt_path,
    }
    return sanitized, {
        "bytes": len(results_data),
        "path": relative,
        "sha256": sha256_bytes(results_data),
    }


def collect_evidence_inventory(root: pathlib.Path, evidence_paths: Sequence[str]) -> dict[str, Any]:
    records: list[dict[str, Any]] = []
    seen: set[str] = set()
    seen_casefolded: dict[str, str] = {}
    for value in sorted(set(evidence_paths)):
        path, relative = resolve_project_path(root, value, must_exist=True, require_file=True)
        _validate_evidence_path(relative)
        folded = relative.casefold()
        previous = seen_casefolded.get(folded)
        if previous is not None and previous != relative:
            raise ManifestError(f"Evidence paths collide under Windows casing: {previous}, {relative}")
        seen_casefolded[folded] = relative
        if relative not in seen:
            records.append(_record_evidence_file(root.resolve(strict=True), path))
            seen.add(relative)
    records.sort(key=lambda item: item["path"])
    total_bytes = sum(record["bytes"] for record in records)
    if total_bytes > MAX_EVIDENCE_TOTAL_BYTES:
        raise ManifestError("The evidence inventory exceeds the aggregate byte budget.")
    return {
        "fileCount": len(records),
        "files": records,
        "sha256": _inventory_digest(records),
        "totalBytes": total_bytes,
    }


def _required_boolean(value: Any, field: str) -> bool:
    if not isinstance(value, bool):
        raise ManifestError(f"{field} must be a boolean.")
    return value


def _finite_number(value: Any, field: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ManifestError(f"{field} must be a finite number.")
    observed = float(value)
    if observed != observed or observed in {float("inf"), float("-inf")}:
        raise ManifestError(f"{field} must be a finite number.")
    return observed


def _typed_object(value: Any, keys: set[str], field: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ManifestError(f"{field} must be an object.")
    _require_exact_keys(value, keys, field)
    return value


def _bounded_nonnegative_integer(value: Any, field: str, maximum: int) -> int:
    observed = _nonnegative_integer(value, field)
    if observed > maximum:
        raise ManifestError(f"{field} exceeds the supported maximum of {maximum}.")
    return observed


def _bounded_nonnegative_number(value: Any, field: str, maximum: float) -> float:
    observed = _finite_number(value, field)
    if observed < 0.0 or observed > maximum:
        raise ManifestError(f"{field} must be between 0 and {maximum}.")
    return observed


def _required_sha256(value: Any, field: str) -> str:
    if not isinstance(value, str) or re.fullmatch(r"[0-9a-f]{64}", value) is None:
        raise ManifestError(f"{field} must be a lowercase SHA-256 value.")
    return value


def _sanitize_codex_recovery_ab_summary(value: Any, field: str) -> dict[str, Any]:
    """Validate the aggregate shape; its values are trusted only after receipt comparison."""

    summary = _typed_object(
        value,
        {
            "maxCortexCalls",
            "medianLatencyReductionPercent",
            "medianUncachedInputReductionPercent",
            "p95LatencyRegressionPercent",
            "stateMatches",
            "trials",
        },
        field,
    )
    trials = _bounded_nonnegative_integer(summary["trials"], f"{field}.trials", CODEX_RECOVERY_AB_PAIR_COUNT)
    if trials != CODEX_RECOVERY_AB_PAIR_COUNT:
        raise ManifestError(f"{field}.trials must equal {CODEX_RECOVERY_AB_PAIR_COUNT} paired trials.")
    state_matches = _bounded_nonnegative_integer(
        summary["stateMatches"], f"{field}.stateMatches", CODEX_RECOVERY_AB_PAIR_COUNT
    )
    return {
        "maxCortexCalls": _bounded_nonnegative_integer(
            summary["maxCortexCalls"], f"{field}.maxCortexCalls", MAX_CODEX_RECOVERY_AB_TOOL_CALLS
        ),
        "medianLatencyReductionPercent": _finite_number(
            summary["medianLatencyReductionPercent"], f"{field}.medianLatencyReductionPercent"
        ),
        "medianUncachedInputReductionPercent": _finite_number(
            summary["medianUncachedInputReductionPercent"],
            f"{field}.medianUncachedInputReductionPercent",
        ),
        "p95LatencyRegressionPercent": _finite_number(
            summary["p95LatencyRegressionPercent"], f"{field}.p95LatencyRegressionPercent"
        ),
        "stateMatches": state_matches,
        "trials": trials,
    }


def _sanitize_ab_duration_samples(value: Any, field: str) -> list[float] | None:
    if value is None:
        return None
    if not isinstance(value, list) or not value or len(value) > MAX_CODEX_RECOVERY_AB_MCP_SAMPLES:
        raise ManifestError(
            f"{field} must be null or a non-empty array of at most {MAX_CODEX_RECOVERY_AB_MCP_SAMPLES} samples."
        )
    return [
        _bounded_nonnegative_number(sample, f"{field}[{index}]", MAX_CODEX_RECOVERY_AB_DURATION_MS)
        for index, sample in enumerate(value)
    ]


def _sanitize_ab_optional_duration_metric(value: Any, field: str) -> dict[str, Any]:
    metric = _typed_object(value, {"samples", "unavailableReason"}, field)
    samples = _sanitize_ab_duration_samples(metric["samples"], f"{field}.samples")
    unavailable_reason = metric["unavailableReason"]
    if samples is None:
        if unavailable_reason not in {"not_applicable", "telemetry_not_emitted"}:
            raise ManifestError(
                f"{field}.unavailableReason must explain why duration telemetry is unavailable."
            )
    elif unavailable_reason is not None:
        raise ManifestError(f"{field}.unavailableReason must be null when samples are retained.")
    return {"samples": samples, "unavailableReason": unavailable_reason}


def _sanitize_ab_source_records(
    value: Any,
    expected_paths: Sequence[str],
    field: str,
) -> list[dict[str, str]]:
    if not isinstance(value, list) or len(value) != len(expected_paths):
        raise ManifestError(f"{field} must contain exactly {len(expected_paths)} source records.")
    records: list[dict[str, str]] = []
    for index, (record_value, expected_path) in enumerate(zip(value, expected_paths, strict=True)):
        record = _typed_object(record_value, {"path", "sha256"}, f"{field}[{index}]")
        path = record["path"]
        if not isinstance(path, str) or _normalized_relative(path, allow_root=False) != path:
            raise ManifestError(f"{field}[{index}].path must be a canonical project-relative path.")
        if path != expected_path:
            raise ManifestError(f"{field} does not match the required recovery source order.")
        records.append(
            {
                "path": path,
                "sha256": _required_sha256(record["sha256"], f"{field}[{index}].sha256"),
            }
        )
    return records


def _ab_source_set_sha256(records: Sequence[dict[str, str]]) -> str:
    return sha256_bytes(canonical_json_bytes(list(records)))


def _ab_authority_set_sha256(records: Sequence[dict[str, str]]) -> str:
    authority_sources = []
    by_path = {record["path"]: record for record in records}
    for path, source_id in CODEX_RECOVERY_AB_AUTHORITY_SOURCE_IDS.items():
        record = by_path[path]
        authority_sources.append(
            {
                "revisionId": f"sha256-{record['sha256']}",
                "sha256": record["sha256"],
                "sourceId": source_id,
            }
        )
    projection = {
        "contractId": "pegasus.cortex.authority-set/1.0",
        "sources": sorted(authority_sources, key=lambda item: item["sourceId"]),
    }
    return sha256_bytes(canonical_json_bytes(projection))


def _expected_ab_control_first_pairs(seed: str) -> set[str]:
    """Derive a reproducible balanced arm order from a non-secret recorded seed."""

    pair_ids = [f"pair-{index:02d}" for index in range(1, CODEX_RECOVERY_AB_PAIR_COUNT + 1)]
    ranked = sorted(
        pair_ids,
        key=lambda pair_id: (sha256_bytes(f"{seed}:{pair_id}".encode("utf-8")), pair_id),
    )
    return set(ranked[: CODEX_RECOVERY_AB_PAIR_COUNT // 2])


def _sanitize_codex_recovery_ab_evidence(value: Any) -> dict[str, Any]:
    """Validate and retain only bounded, redacted per-trial Codex telemetry."""

    evidence = _typed_object(
        value,
        {"contractId", "randomization", "redacted", "trials"},
        "Codex recovery A/B evidence",
    )
    if evidence["contractId"] != CODEX_RECOVERY_AB_EVIDENCE_CONTRACT_ID:
        raise ManifestError(
            f"Codex recovery A/B evidence contractId must be {CODEX_RECOVERY_AB_EVIDENCE_CONTRACT_ID}."
        )
    if evidence["redacted"] is not True:
        raise ManifestError("Codex recovery A/B evidence must declare redacted=true.")
    randomization = _typed_object(
        evidence["randomization"], {"algorithm", "seed"}, "Codex recovery A/B randomization"
    )
    if randomization["algorithm"] != "sha256-balanced-pair-order/1":
        raise ManifestError("Codex recovery A/B randomization algorithm is unsupported.")
    seed = _required_sha256(randomization["seed"], "Codex recovery A/B randomization.seed")
    expected_pair_ids = {
        f"pair-{index:02d}" for index in range(1, CODEX_RECOVERY_AB_PAIR_COUNT + 1)
    }
    expected_control_first = _expected_ab_control_first_pairs(seed)

    trials_value = evidence["trials"]
    expected_trial_count = CODEX_RECOVERY_AB_PAIR_COUNT * 2
    if not isinstance(trials_value, list) or len(trials_value) != expected_trial_count:
        raise ManifestError(
            f"Codex recovery A/B evidence must contain exactly {expected_trial_count} arm records."
        )

    sanitized_trials: list[dict[str, Any]] = []
    seen_arms: set[tuple[str, str]] = set()
    seen_run_traces: set[str] = set()
    metadata_baseline: dict[str, str] | None = None
    pair_records: dict[str, dict[str, dict[str, Any]]] = {}
    captured_times: list[dt.datetime] = []
    captured_by_pair: dict[str, dict[int, dt.datetime]] = {}
    control_source_hashes: set[str] = set()
    treatment_source_hashes: set[str] = set()
    for index, trial_value in enumerate(trials_value):
        field = f"Codex recovery A/B trials[{index}]"
        trial = _typed_object(
            trial_value,
            {
                "arm",
                "capturedAtUtc",
                "freshTask",
                "inheritedHistory",
                "metadata",
                "metrics",
                "order",
                "pairId",
                "recovery",
                "runTraceSha256",
                "validation",
            },
            field,
        )
        pair_id = trial["pairId"]
        if not isinstance(pair_id, str) or pair_id not in expected_pair_ids:
            raise ManifestError(f"{field}.pairId is outside the fixed 20-pair trial set.")
        arm = trial["arm"]
        if arm not in {"control", "treatment"}:
            raise ManifestError(f"{field}.arm must be control or treatment.")
        if (pair_id, arm) in seen_arms:
            raise ManifestError(f"Codex recovery A/B evidence duplicates {pair_id} {arm}.")
        seen_arms.add((pair_id, arm))
        order = trial["order"]
        if isinstance(order, bool) or not isinstance(order, int) or order not in {1, 2}:
            raise ManifestError(f"{field}.order must be 1 or 2.")
        if trial["freshTask"] is not True or trial["inheritedHistory"] is not False:
            raise ManifestError(
                f"{field} must represent a fresh task with no inherited parent history."
            )
        captured_at = trial["capturedAtUtc"]
        if not isinstance(captured_at, str) or normalize_captured_at(captured_at) != captured_at:
            raise ManifestError(f"{field}.capturedAtUtc must be canonical UTC ISO-8601.")
        captured_time = dt.datetime.fromisoformat(captured_at.replace("Z", "+00:00"))
        captured_times.append(captured_time)
        if order in captured_by_pair.setdefault(pair_id, {}):
            raise ManifestError(f"Codex recovery A/B {pair_id} duplicates run order {order}.")
        captured_by_pair[pair_id][order] = captured_time
        run_trace_sha256 = _required_sha256(trial["runTraceSha256"], f"{field}.runTraceSha256")
        if run_trace_sha256 in seen_run_traces:
            raise ManifestError("Codex recovery A/B runTraceSha256 values must be unique across all arms.")
        seen_run_traces.add(run_trace_sha256)

        metadata_value = _typed_object(
            trial["metadata"],
            {
                "authoritySetHash",
                "model",
                "permissionsSha256",
                "projectSnapshotSha256",
                "promptSha256",
                "reasoningLevel",
                "runtimePackageAttestationSha256",
                "testedSourceBundleSha256",
            },
            f"{field}.metadata",
        )
        metadata = {
            "authoritySetHash": _required_sha256(
                metadata_value["authoritySetHash"], f"{field}.metadata.authoritySetHash"
            ),
            "model": _validate_public_text(metadata_value["model"], f"{field}.metadata.model", 128),
            "permissionsSha256": _required_sha256(
                metadata_value["permissionsSha256"], f"{field}.metadata.permissionsSha256"
            ),
            "projectSnapshotSha256": _required_sha256(
                metadata_value["projectSnapshotSha256"], f"{field}.metadata.projectSnapshotSha256"
            ),
            "promptSha256": _required_sha256(
                metadata_value["promptSha256"], f"{field}.metadata.promptSha256"
            ),
            "reasoningLevel": _validate_public_text(
                metadata_value["reasoningLevel"], f"{field}.metadata.reasoningLevel", 64
            ),
            # The verification receipt cannot independently hash the Codex runtime
            # package, so this remains an explicit operator/collector attestation.
            "runtimePackageAttestationSha256": _required_sha256(
                metadata_value["runtimePackageAttestationSha256"],
                f"{field}.metadata.runtimePackageAttestationSha256",
            ),
            "testedSourceBundleSha256": _required_sha256(
                metadata_value["testedSourceBundleSha256"],
                f"{field}.metadata.testedSourceBundleSha256",
            ),
        }
        if metadata_baseline is None:
            metadata_baseline = metadata
        elif metadata != metadata_baseline:
            raise ManifestError("Codex recovery A/B trial metadata is inconsistent across arms or pairs.")

        recovery_value = _typed_object(
            trial["recovery"],
            {
                "authorityProbeSources",
                "authorityProbeUsed",
                "cortexCallCount",
                "exactReadCount",
                "fileReadCount",
                "fileSources",
                "fileSourceSetSha256",
                "mode",
                "projectBriefCalls",
            },
            f"{field}.recovery",
        )
        authority_probe_sources = _sanitize_ab_source_records(
            recovery_value["authorityProbeSources"],
            CODEX_RECOVERY_AB_AUTHORITY_SOURCES if arm == "treatment" else (),
            f"{field}.recovery.authorityProbeSources",
        )
        file_sources = _sanitize_ab_source_records(
            recovery_value["fileSources"],
            CODEX_RECOVERY_AB_CONTROL_SOURCES if arm == "control" else (),
            f"{field}.recovery.fileSources",
        )
        local_sources = file_sources if arm == "control" else authority_probe_sources
        derived_source_set_sha256 = _ab_source_set_sha256(local_sources)
        recovery = {
            "authorityProbeSources": authority_probe_sources,
            "authorityProbeUsed": _required_boolean(
                recovery_value["authorityProbeUsed"], f"{field}.recovery.authorityProbeUsed"
            ),
            "cortexCallCount": _bounded_nonnegative_integer(
                recovery_value["cortexCallCount"],
                f"{field}.recovery.cortexCallCount",
                MAX_CODEX_RECOVERY_AB_TOOL_CALLS,
            ),
            "exactReadCount": _bounded_nonnegative_integer(
                recovery_value["exactReadCount"], f"{field}.recovery.exactReadCount", 1
            ),
            "fileReadCount": _bounded_nonnegative_integer(
                recovery_value["fileReadCount"],
                f"{field}.recovery.fileReadCount",
                MAX_CODEX_RECOVERY_AB_FILE_READS,
            ),
            "fileSources": file_sources,
            "fileSourceSetSha256": _required_sha256(
                recovery_value["fileSourceSetSha256"], f"{field}.recovery.fileSourceSetSha256"
            ),
            "mode": recovery_value["mode"],
            "projectBriefCalls": _bounded_nonnegative_integer(
                recovery_value["projectBriefCalls"], f"{field}.recovery.projectBriefCalls", 1
            ),
        }
        if recovery["fileSourceSetSha256"] != derived_source_set_sha256:
            raise ManifestError(f"{field}.recovery.fileSourceSetSha256 does not match its source records.")
        if arm == "control":
            if recovery != {
                "authorityProbeSources": [],
                "authorityProbeUsed": False,
                "cortexCallCount": 0,
                "exactReadCount": 0,
                "fileReadCount": len(CODEX_RECOVERY_AB_CONTROL_SOURCES),
                "fileSources": file_sources,
                "fileSourceSetSha256": derived_source_set_sha256,
                "mode": "file",
                "projectBriefCalls": 0,
            }:
                raise ManifestError(f"{field}.recovery is not a file-only control observation.")
            control_source_hashes.add(recovery["fileSourceSetSha256"])
        elif (
            recovery["authorityProbeUsed"] is not True
            or recovery["mode"] != "cortex"
            or recovery["projectBriefCalls"] != 1
            or recovery["cortexCallCount"] != 1 + recovery["exactReadCount"]
            or recovery["fileReadCount"] != len(CODEX_RECOVERY_AB_AUTHORITY_SOURCES)
            or recovery["fileSources"]
        ):
            raise ManifestError(
                f"{field}.recovery must use one authority probe, one project brief, and at most one exact read."
            )
        else:
            treatment_source_hashes.add(recovery["fileSourceSetSha256"])

        metrics_value = _typed_object(
            trial["metrics"],
            {
                "mcp.call.duration_ms",
                "mcp.tools.list.duration_ms",
                "turn.e2e_duration_ms",
                "turn.token_usage",
                "turn.tool.call",
            },
            f"{field}.metrics",
        )
        token_usage_value = _typed_object(
            metrics_value["turn.token_usage"],
            {"cachedInputTokens", "outputTokens", "uncachedInputTokens"},
            f"{field}.metrics.turn.token_usage",
        )
        token_usage = {
            "cachedInputTokens": _bounded_nonnegative_integer(
                token_usage_value["cachedInputTokens"],
                f"{field}.metrics.turn.token_usage.cachedInputTokens",
                MAX_CODEX_RECOVERY_AB_TOKEN_COUNT,
            ),
            "outputTokens": _bounded_nonnegative_integer(
                token_usage_value["outputTokens"],
                f"{field}.metrics.turn.token_usage.outputTokens",
                MAX_CODEX_RECOVERY_AB_TOKEN_COUNT,
            ),
            "uncachedInputTokens": _bounded_nonnegative_integer(
                token_usage_value["uncachedInputTokens"],
                f"{field}.metrics.turn.token_usage.uncachedInputTokens",
                MAX_CODEX_RECOVERY_AB_TOKEN_COUNT,
            ),
        }
        if token_usage["uncachedInputTokens"] == 0 or token_usage["outputTokens"] == 0:
            raise ManifestError(
                f"{field}.metrics.turn.token_usage uncached input and output counts must be positive."
            )
        mcp_call_samples = _sanitize_ab_duration_samples(
            metrics_value["mcp.call.duration_ms"], f"{field}.metrics.mcp.call.duration_ms"
        )
        if arm == "control" and mcp_call_samples is not None:
            raise ManifestError(f"{field}.metrics.mcp.call.duration_ms must be null for file-only control.")
        if arm == "treatment" and mcp_call_samples is None:
            raise ManifestError(
                f"{field}.metrics.mcp.call.duration_ms must retain one sample per Cortex call."
            )
        if mcp_call_samples is not None and len(mcp_call_samples) != recovery["cortexCallCount"]:
            raise ManifestError(
                f"{field}.metrics.mcp.call.duration_ms sample count must match the Cortex call count."
            )
        e2e_duration = _bounded_nonnegative_number(
            metrics_value["turn.e2e_duration_ms"],
            f"{field}.metrics.turn.e2e_duration_ms",
            MAX_CODEX_RECOVERY_AB_DURATION_MS,
        )
        if e2e_duration == 0.0:
            raise ManifestError(f"{field}.metrics.turn.e2e_duration_ms must be positive.")
        tool_calls = _bounded_nonnegative_integer(
            metrics_value["turn.tool.call"],
            f"{field}.metrics.turn.tool.call",
            MAX_CODEX_RECOVERY_AB_TOOL_CALLS,
        )
        if tool_calls < recovery["cortexCallCount"]:
            raise ManifestError(f"{field}.metrics.turn.tool.call is lower than its Cortex call count.")
        metrics = {
            "mcp.call.duration_ms": mcp_call_samples,
            "mcp.tools.list.duration_ms": _sanitize_ab_optional_duration_metric(
                metrics_value["mcp.tools.list.duration_ms"],
                f"{field}.metrics.mcp.tools.list.duration_ms",
            ),
            "turn.e2e_duration_ms": e2e_duration,
            "turn.token_usage": token_usage,
            "turn.tool.call": tool_calls,
        }

        validation_value = _typed_object(
            trial["validation"],
            {"authoritySetHash", "checkpoint", "currentTask", "gate", "nextAction", "revision", "state"},
            f"{field}.validation",
        )
        validation = {
            name: _required_boolean(validation_value[name], f"{field}.validation.{name}")
            for name in (
                "authoritySetHash",
                "checkpoint",
                "currentTask",
                "gate",
                "nextAction",
                "revision",
                "state",
            )
        }
        sanitized = {
            "arm": arm,
            "capturedAtUtc": captured_at,
            "freshTask": True,
            "inheritedHistory": False,
            "metadata": metadata,
            "metrics": metrics,
            "order": order,
            "pairId": pair_id,
            "recovery": recovery,
            "runTraceSha256": run_trace_sha256,
            "validation": validation,
        }
        sanitized_trials.append(sanitized)
        pair_records.setdefault(pair_id, {})[arm] = sanitized

    if set(pair_records) != expected_pair_ids or any(set(records) != {"control", "treatment"} for records in pair_records.values()):
        raise ManifestError("Codex recovery A/B evidence is unpaired or incomplete.")
    for pair_id, records in pair_records.items():
        control_order = records["control"]["order"]
        treatment_order = records["treatment"]["order"]
        if {control_order, treatment_order} != {1, 2}:
            raise ManifestError(f"Codex recovery A/B {pair_id} does not contain both run orders.")
        expected_control_order = 1 if pair_id in expected_control_first else 2
        if control_order != expected_control_order:
            raise ManifestError(
                f"Codex recovery A/B {pair_id} order does not match the recorded balanced randomization."
            )
        ordered_times = captured_by_pair.get(pair_id, {})
        if set(ordered_times) != {1, 2} or ordered_times[1] >= ordered_times[2]:
            raise ManifestError(
                f"Codex recovery A/B {pair_id} capturedAtUtc values must increase with run order."
            )
    if len(seen_run_traces) != expected_trial_count:
        raise ManifestError("Codex recovery A/B evidence must contain one unique run trace per arm.")
    if len(control_source_hashes) != 1:
        raise ManifestError("Codex recovery A/B controls must use one consistent hashed file source set.")
    if len(treatment_source_hashes) != 1:
        raise ManifestError("Codex recovery A/B treatments must use one consistent authority-probe source set.")
    assert metadata_baseline is not None
    control_source_set_sha256 = next(iter(control_source_hashes))
    if metadata_baseline["projectSnapshotSha256"] != control_source_set_sha256:
        raise ManifestError(
            "Codex recovery A/B projectSnapshotSha256 must match the retained fallback source set."
        )
    first_treatment = pair_records["pair-01"]["treatment"]
    expected_authority_set_hash = _ab_authority_set_sha256(
        first_treatment["recovery"]["authorityProbeSources"]
    )
    if metadata_baseline["authoritySetHash"] != expected_authority_set_hash:
        raise ManifestError(
            "Codex recovery A/B authoritySetHash must match the retained authority-probe sources."
        )
    if (max(captured_times) - min(captured_times)).total_seconds() > MAX_CODEX_RECOVERY_AB_CAPTURE_SPAN_SECONDS:
        raise ManifestError("Codex recovery A/B capture timestamps exceed the seven-day evidence window.")

    return {
        "contractId": CODEX_RECOVERY_AB_EVIDENCE_CONTRACT_ID,
        "randomization": {"algorithm": "sha256-balanced-pair-order/1", "seed": seed},
        "redacted": True,
        "trials": sorted(sanitized_trials, key=lambda item: (item["pairId"], item["order"])),
    }


def _median(values: Sequence[float | int]) -> float:
    ordered = sorted(float(value) for value in values)
    middle = len(ordered) // 2
    if len(ordered) % 2:
        return ordered[middle]
    return (ordered[middle - 1] + ordered[middle]) / 2.0


def _nearest_rank_p95(values: Sequence[float | int]) -> float:
    ordered = sorted(float(value) for value in values)
    index = max(0, ((95 * len(ordered) + 99) // 100) - 1)
    return ordered[index]


def _rounded_percent(value: float) -> float:
    rounded = round(value, 6)
    return 0.0 if rounded == 0.0 else rounded


def _derive_codex_recovery_ab_metrics(
    value: Any,
    *,
    receipt_captured_at_utc: str | None = None,
    source_inventory: dict[str, Any] | None = None,
    tested_source_bundle_sha256: str | None = None,
) -> dict[str, Any]:
    """Recompute gate metrics from individual sealed arm observations."""

    evidence = _sanitize_codex_recovery_ab_evidence(value)
    evidence_bundle_sha256 = evidence["trials"][0]["metadata"]["testedSourceBundleSha256"]
    if (
        tested_source_bundle_sha256 is not None
        and evidence_bundle_sha256 != _required_sha256(
            tested_source_bundle_sha256, "execution receipt tested source bundle SHA-256"
        )
    ):
        raise ManifestError(
            "Codex recovery A/B evidence is not bound to the execution receipt tested source bundle."
        )
    if receipt_captured_at_utc is not None:
        if normalize_captured_at(receipt_captured_at_utc) != receipt_captured_at_utc:
            raise ManifestError("Execution receipt capturedAtUtc must be canonical for A/B binding.")
        receipt_time = dt.datetime.fromisoformat(receipt_captured_at_utc.replace("Z", "+00:00"))
        evidence_times = [
            dt.datetime.fromisoformat(trial["capturedAtUtc"].replace("Z", "+00:00"))
            for trial in evidence["trials"]
        ]
        if max(evidence_times) > receipt_time:
            raise ManifestError("Codex recovery A/B evidence contains a future trial timestamp.")
        if (receipt_time - min(evidence_times)).total_seconds() > MAX_CODEX_RECOVERY_AB_CAPTURE_SPAN_SECONDS:
            raise ManifestError("Codex recovery A/B evidence is outside the receipt capture window.")
    if source_inventory is not None:
        inventory_files = source_inventory.get("files") if isinstance(source_inventory, dict) else None
        if not isinstance(inventory_files, list):
            raise ManifestError("The tested source inventory is unavailable for A/B source binding.")
        inventory_hashes = {
            record.get("path"): record.get("sha256")
            for record in inventory_files
            if isinstance(record, dict)
            and isinstance(record.get("path"), str)
            and isinstance(record.get("sha256"), str)
        }
        first_control = next(trial for trial in evidence["trials"] if trial["arm"] == "control")
        first_treatment = next(trial for trial in evidence["trials"] if trial["arm"] == "treatment")
        for label, source_records in (
            ("control", first_control["recovery"]["fileSources"]),
            ("treatment", first_treatment["recovery"]["authorityProbeSources"]),
        ):
            if any(inventory_hashes.get(record["path"]) != record["sha256"] for record in source_records):
                raise ManifestError(
                    f"Codex recovery A/B {label} sources do not match the tested source inventory."
                )
    records = {(trial["pairId"], trial["arm"]): trial for trial in evidence["trials"]}
    controls = [records[(f"pair-{index:02d}", "control")] for index in range(1, CODEX_RECOVERY_AB_PAIR_COUNT + 1)]
    treatments = [
        records[(f"pair-{index:02d}", "treatment")] for index in range(1, CODEX_RECOVERY_AB_PAIR_COUNT + 1)
    ]
    paired_uncached_reductions: list[float] = []
    paired_latency_reductions: list[float] = []
    paired_latency_regressions: list[float] = []
    for control, treatment in zip(controls, treatments, strict=True):
        control_uncached = control["metrics"]["turn.token_usage"]["uncachedInputTokens"]
        treatment_uncached = treatment["metrics"]["turn.token_usage"]["uncachedInputTokens"]
        control_latency = control["metrics"]["turn.e2e_duration_ms"]
        treatment_latency = treatment["metrics"]["turn.e2e_duration_ms"]
        paired_uncached_reductions.append(
            (control_uncached - treatment_uncached) / control_uncached * 100.0
        )
        latency_regression = (treatment_latency - control_latency) / control_latency * 100.0
        paired_latency_regressions.append(latency_regression)
        paired_latency_reductions.append(-latency_regression)
    state_matches = sum(
        1
        for pair_index in range(1, CODEX_RECOVERY_AB_PAIR_COUNT + 1)
        if all(records[(f"pair-{pair_index:02d}", arm)]["validation"][name]
               for arm in ("control", "treatment")
               for name in (
                   "authoritySetHash",
                   "checkpoint",
                   "currentTask",
                   "gate",
                   "nextAction",
                   "revision",
                   "state",
               ))
    )
    return _sanitize_codex_recovery_ab_summary(
        {
            "maxCortexCalls": max(trial["recovery"]["cortexCallCount"] for trial in treatments),
            "medianLatencyReductionPercent": _rounded_percent(
                _median(paired_latency_reductions)
            ),
            "medianUncachedInputReductionPercent": _rounded_percent(
                _median(paired_uncached_reductions)
            ),
            "p95LatencyRegressionPercent": _rounded_percent(
                _nearest_rank_p95(paired_latency_regressions)
            ),
            "stateMatches": state_matches,
            "trials": CODEX_RECOVERY_AB_PAIR_COUNT,
        },
        "derived Codex recovery A/B metrics",
    )


def load_acceptance_metrics(root: pathlib.Path, evidence_paths: Sequence[str]) -> dict[str, Any]:
    """Load the one recognized, closed-schema acceptance metrics artifact."""

    if len(evidence_paths) != 1:
        raise ManifestError("The acceptance profile requires exactly one typed metrics artifact.")
    path, relative = resolve_project_path(root, evidence_paths[0], must_exist=True, require_file=True)
    document, _ = _read_bounded_json_payload(
        path,
        maximum_bytes=MAX_EVIDENCE_FILE_BYTES,
        field=f"acceptance metrics {relative}",
    )
    document = _typed_object(document, {"contractId", "metrics"}, "acceptance metrics")
    if document.get("contractId") != ACCEPTANCE_EVIDENCE_CONTRACT_ID:
        raise ManifestError(
            f"The acceptance profile requires evidence contract {ACCEPTANCE_EVIDENCE_CONTRACT_ID}."
        )
    metrics = _typed_object(
        document.get("metrics"),
        {
            "authorityFallback",
            "codexRecoveryAb",
            "concurrentClients",
            "deterministicTime",
            "exactSearch",
            "fullBuild",
            "harnessMatrix",
            "offlineRecovery",
            "projectBriefAuthority",
            "reservedIsolation",
            "retrievalRecall",
        },
        "acceptance metrics.metrics",
    )

    concurrent = _typed_object(
        metrics["concurrentClients"],
        {"clientCount", "starvationCount", "timeoutCount"},
        "metrics.concurrentClients",
    )
    exact = _typed_object(
        metrics["exactSearch"],
        {"passed", "regressions", "total"},
        "metrics.exactSearch",
    )
    build = _typed_object(
        metrics["fullBuild"],
        {"errors", "exitCode", "warnings"},
        "metrics.fullBuild",
    )
    harness = _typed_object(
        metrics["harnessMatrix"],
        {"failed", "passed", "skipped"},
        "metrics.harnessMatrix",
    )
    reserved = _typed_object(
        metrics["reservedIsolation"],
        {"leakageCount"},
        "metrics.reservedIsolation",
    )
    recall = _typed_object(
        metrics["retrievalRecall"],
        {"hits", "total"},
        "metrics.retrievalRecall",
    )
    ab_value = metrics["codexRecoveryAb"]
    sanitized_ab = (
        None
        if ab_value is None
        else _sanitize_codex_recovery_ab_summary(ab_value, "metrics.codexRecoveryAb")
    )

    sanitized: dict[str, Any] = {
        "authorityFallback": _required_boolean(metrics["authorityFallback"], "metrics.authorityFallback"),
        "codexRecoveryAb": sanitized_ab,
        "concurrentClients": {
            "clientCount": _nonnegative_integer(concurrent["clientCount"], "metrics.concurrentClients.clientCount"),
            "starvationCount": _nonnegative_integer(
                concurrent["starvationCount"], "metrics.concurrentClients.starvationCount"
            ),
            "timeoutCount": _nonnegative_integer(concurrent["timeoutCount"], "metrics.concurrentClients.timeoutCount"),
        },
        "deterministicTime": _required_boolean(metrics["deterministicTime"], "metrics.deterministicTime"),
        "exactSearch": {
            "passed": _nonnegative_integer(exact["passed"], "metrics.exactSearch.passed"),
            "regressions": _nonnegative_integer(exact["regressions"], "metrics.exactSearch.regressions"),
            "total": _nonnegative_integer(exact["total"], "metrics.exactSearch.total"),
        },
        "fullBuild": {
            "errors": _nonnegative_integer(build["errors"], "metrics.fullBuild.errors"),
            "exitCode": build["exitCode"],
            "warnings": _nonnegative_integer(build["warnings"], "metrics.fullBuild.warnings"),
        },
        "harnessMatrix": {
            "failed": _nonnegative_integer(harness["failed"], "metrics.harnessMatrix.failed"),
            "passed": _nonnegative_integer(harness["passed"], "metrics.harnessMatrix.passed"),
            "skipped": _nonnegative_integer(harness["skipped"], "metrics.harnessMatrix.skipped"),
        },
        "offlineRecovery": _required_boolean(metrics["offlineRecovery"], "metrics.offlineRecovery"),
        "projectBriefAuthority": _required_boolean(
            metrics["projectBriefAuthority"], "metrics.projectBriefAuthority"
        ),
        "reservedIsolation": {
            "leakageCount": _nonnegative_integer(
                reserved["leakageCount"], "metrics.reservedIsolation.leakageCount"
            )
        },
        "retrievalRecall": {
            "hits": _nonnegative_integer(recall["hits"], "metrics.retrievalRecall.hits"),
            "total": _nonnegative_integer(recall["total"], "metrics.retrievalRecall.total"),
        },
    }
    exit_code = sanitized["fullBuild"]["exitCode"]
    if isinstance(exit_code, bool) or not isinstance(exit_code, int):
        raise ManifestError("metrics.fullBuild.exitCode must be an integer.")
    return sanitized


def _run_version_command(arguments: Sequence[str]) -> list[str]:
    completed = subprocess.run(
        list(arguments),
        check=True,
        capture_output=True,
        encoding="utf-8",
        errors="strict",
        timeout=20,
    )
    return [line.strip() for line in completed.stdout.splitlines() if line.strip()]


def collect_environment(dotnet_value: str) -> dict[str, Any]:
    dotnet_path = shutil.which(dotnet_value)
    if dotnet_path is None:
        candidate = pathlib.Path(dotnet_value)
        if candidate.is_file():
            dotnet_path = str(candidate.resolve(strict=True))
        else:
            raise ManifestError("The requested dotnet executable was not found.")
    try:
        sdk_version = _run_version_command([dotnet_path, "--version"])
        sdk_lines = _run_version_command([dotnet_path, "--list-sdks"])
        runtime_lines = _run_version_command([dotnet_path, "--list-runtimes"])
    except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired, UnicodeError) as exception:
        raise ManifestError("Unable to capture bounded dotnet version information.") from exception
    if len(sdk_version) != 1:
        raise ManifestError("dotnet --version returned an unexpected shape.")

    sdks = sorted({line.split(maxsplit=1)[0] for line in sdk_lines})
    runtimes: list[dict[str, str]] = []
    for line in runtime_lines:
        parts = line.split()
        if len(parts) < 2:
            raise ManifestError("dotnet --list-runtimes returned an unexpected shape.")
        runtimes.append({"name": parts[0], "version": parts[1]})
    runtimes.sort(key=lambda item: (item["name"], item["version"]))

    executable = pathlib.Path(dotnet_path).resolve(strict=True)
    return {
        "dotnet": {
            "hostSha256": sha256_file(executable),
            "runtimeVersions": runtimes,
            "sdkVersion": sdk_version[0],
            "sdkVersions": sdks,
        },
        "operatingSystem": {
            "machine": platform.machine(),
            "release": platform.release(),
            "system": platform.system(),
        },
        "python": {
            "implementation": platform.python_implementation(),
            "version": platform.python_version(),
        },
    }


def normalize_captured_at(value: str | None) -> str:
    if value is None:
        observed = dt.datetime.now(dt.timezone.utc)
    else:
        try:
            observed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError as exception:
            raise ManifestError("captured-at must be a valid timezone-qualified ISO-8601 value.") from exception
        if observed.tzinfo is None or observed.utcoffset() is None:
            raise ManifestError("captured-at must include an explicit UTC offset.")
        observed = observed.astimezone(dt.timezone.utc)
    return observed.isoformat(timespec="microseconds").replace("+00:00", "Z")


def _source_bundle_payload(source: dict[str, Any], captured_at: str) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "archiveFormat": "zip-stored-normalized-regular-files/1.0",
        "capturedAtUtc": normalize_captured_at(captured_at),
        "contractId": SOURCE_BUNDLE_CONTRACT_ID,
        "source": source,
    }
    payload["seal"] = {
        "algorithm": "sha256",
        "canonicalization": "sorted-key-compact-json-utf8",
        "payloadSha256": sha256_bytes(canonical_json_bytes(payload)),
    }
    return payload


def _bundle_zip_info(name: str) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, date_time=BUNDLE_TIMESTAMP)
    info.compress_type = zipfile.ZIP_STORED
    info.create_system = 3
    info.external_attr = BUNDLE_REGULAR_MODE << 16
    info.extra = b""
    info.comment = b""
    return info


def _publish_temporary_no_overwrite(temporary: pathlib.Path, output: pathlib.Path) -> None:
    if output.exists() or _is_reparse_point(output):
        raise ManifestError("The output already exists; sealed evidence is never overwritten.")
    try:
        os.link(temporary, output)
    except FileExistsError as exception:
        raise ManifestError(
            "The output already exists; sealed evidence is never overwritten."
        ) from exception


def _validated_new_output(root: pathlib.Path, output: pathlib.Path) -> pathlib.Path:
    root = root.resolve(strict=True)
    try:
        relative = output.relative_to(root).as_posix()
    except ValueError as exception:
        raise ManifestError("The output must remain beneath the project root.") from exception
    candidate, _ = resolve_project_path(
        root,
        relative,
        must_exist=False,
        require_file=None,
    )
    return candidate


def create_source_bundle(
    root: pathlib.Path,
    output: pathlib.Path,
    *,
    source_paths: Sequence[str],
    captured_at: str,
) -> dict[str, Any]:
    """Retain exact source bytes in a deterministic no-overwrite ZIP bundle."""

    root = root.resolve(strict=True)
    if captured_at is None:
        raise ManifestError("captured-at is required for deterministic source bundle creation.")
    output = _validated_new_output(root, output)
    if output.suffix.casefold() != ".zip":
        raise ManifestError("The tested source bundle output must be a ZIP file.")
    excluded = (output,)
    first = collect_source_inventory(root, source_paths, excluded_files=excluded)
    second = collect_source_inventory(root, source_paths, excluded_files=excluded)
    if first != second:
        raise ManifestError("The tested source changed between stable inventory passes.")
    if any(record["path"] == BUNDLE_METADATA_NAME for record in first["files"]):
        raise ManifestError("The tested source collides with the reserved bundle metadata name.")

    payload = _source_bundle_payload(first, captured_at)
    _screen_json_value(payload, "source bundle metadata")
    metadata = formatted_manifest_bytes(payload)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{output.name}.", dir=output.parent)
    temporary = pathlib.Path(temporary_name)
    published = False
    try:
        with os.fdopen(descriptor, "w+b") as stream:
            with zipfile.ZipFile(
                stream,
                mode="w",
                compression=zipfile.ZIP_STORED,
                allowZip64=False,
                strict_timestamps=True,
            ) as archive:
                archive.comment = b""
                archive.writestr(_bundle_zip_info(BUNDLE_METADATA_NAME), metadata)
                for record in first["files"]:
                    path, relative = resolve_project_path(
                        root,
                        record["path"],
                        must_exist=True,
                        require_file=True,
                    )
                    data = path.read_bytes()
                    if len(data) != record["bytes"] or sha256_bytes(data) != record["sha256"]:
                        raise ManifestError(
                            f"The tested source changed while bundling: {relative}"
                        )
                    archive.writestr(_bundle_zip_info(relative), data)
            stream.flush()
            os.fsync(stream.fileno())
        if temporary.stat().st_size > MAX_BUNDLE_BYTES:
            raise ManifestError("The tested source bundle exceeds the byte budget.")
        _publish_temporary_no_overwrite(temporary, output)
        published = True

        third = collect_source_inventory(root, source_paths, excluded_files=excluded)
        if first != third:
            raise ManifestError("The tested source changed while the bundle was published.")
        verified = verify_source_bundle(root, output.relative_to(root).as_posix())
        if verified["source"] != first:
            raise ManifestError("The retained source bundle differs from the source inventory.")
        temporary.unlink()
        return verified
    except BaseException:
        if published:
            try:
                if output.exists() and temporary.exists() and os.path.samefile(output, temporary):
                    output.unlink()
            except OSError:
                # Preserve an unexpected replacement rather than deleting a path
                # whose identity can no longer be proven to be ours.
                pass
        temporary.unlink(missing_ok=True)
        raise


def _zip_end_is_exact(stream: Any, size: int) -> bool:
    if size < 22:
        return False
    tail_size = min(size, 65_557)
    stream.seek(size - tail_size)
    tail = stream.read(tail_size)
    marker = tail.rfind(b"PK\x05\x06")
    if marker < 0 or marker + 22 > len(tail):
        return False
    comment_length = int.from_bytes(tail[marker + 20 : marker + 22], "little")
    return marker + 22 + comment_length == len(tail)


def _reject_file_ancestor_collisions(paths: Iterable[str], field: str) -> None:
    """Reject a file path that is also an ancestor of another file on Windows."""

    folded = {path.casefold() for path in paths}
    for path in folded:
        parts = path.split("/")
        for index in range(1, len(parts)):
            if "/".join(parts[:index]) in folded:
                raise ManifestError(f"{field} contains a file/directory ancestor collision.")


def _validate_bundle_inventory(value: Any) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ManifestError("The source bundle inventory is missing.")
    _require_exact_keys(
        value,
        {"fileCount", "files", "roots", "sha256", "totalBytes"},
        "source bundle inventory",
    )
    roots = value.get("roots")
    records = value.get("files")
    if not isinstance(roots, list) or not roots or not all(isinstance(item, str) for item in roots):
        raise ManifestError("The source bundle roots are invalid.")
    if roots != sorted(set(roots)):
        raise ManifestError("The source bundle roots are not deterministically ordered.")
    folded_roots: set[str] = set()
    for root_value in roots:
        _normalized_relative(root_value, allow_root=True)
        folded = root_value.casefold()
        if folded in folded_roots:
            raise ManifestError("The source bundle roots contain a Windows path collision.")
        folded_roots.add(folded)
    if not isinstance(records, list) or not all(isinstance(item, dict) for item in records):
        raise ManifestError("The source bundle files are invalid.")
    if records != sorted(records, key=lambda item: item.get("path", "")):
        raise ManifestError("The source bundle files are not deterministically ordered.")
    casefolded: set[str] = set()
    for record in records:
        _require_exact_keys(record, {"bytes", "path", "sha256"}, "source bundle file")
        path_value = record.get("path")
        size = record.get("bytes")
        digest = record.get("sha256")
        if not isinstance(path_value, str):
            raise ManifestError("The source bundle contains an invalid file path.")
        normalized = _normalized_relative(path_value, allow_root=False)
        if normalized != path_value:
            raise ManifestError("The source bundle contains a non-canonical file path.")
        folded = path_value.casefold()
        if folded in casefolded:
            raise ManifestError("The source bundle contains a Windows path collision.")
        casefolded.add(folded)
        if isinstance(size, bool) or not isinstance(size, int) or size < 0:
            raise ManifestError("The source bundle contains an invalid file size.")
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise ManifestError("The source bundle contains an invalid file hash.")
        if not any(
            root_value == "."
            or path_value == root_value
            or path_value.startswith(f"{root_value}/")
            for root_value in roots
        ):
            raise ManifestError("A source bundle file is outside its declared source roots.")
    _reject_file_ancestor_collisions(casefolded, "The source bundle")
    expected = {
        "fileCount": len(records),
        "sha256": _inventory_digest(records),
        "totalBytes": sum(record["bytes"] for record in records),
    }
    for key, expected_value in expected.items():
        if value.get(key) != expected_value:
            raise ManifestError(f"The source bundle {key} does not match its inventory.")
    _validate_source_inventory_bounds(value)
    return value


def verify_source_bundle(root: pathlib.Path, bundle_value: str) -> dict[str, Any]:
    root = root.resolve(strict=True)
    path, relative = resolve_project_path(root, bundle_value, must_exist=True, require_file=True)
    if pathlib.PurePosixPath(relative).suffix.casefold() != ".zip":
        raise ManifestError("The tested source bundle must be a ZIP file.")
    try:
        if _is_reparse_point(path):
            raise ManifestError("The tested source bundle is a link or reparse point.")
        with path.open("rb") as bundle_stream:
            before = os.fstat(bundle_stream.fileno())
            size = before.st_size
            if size > MAX_BUNDLE_BYTES:
                raise ManifestError("The tested source bundle exceeds the byte budget.")
            if not _zip_end_is_exact(bundle_stream, size):
                raise ManifestError("The tested source bundle has a malformed or trailing ZIP payload.")
            bundle_stream.seek(0)
            with zipfile.ZipFile(bundle_stream, mode="r", allowZip64=False) as archive:
                if archive.comment:
                    raise ManifestError("The tested source bundle may not contain an archive comment.")
                infos = archive.infolist()
                if len(infos) > MAX_SOURCE_FILES + 1:
                    raise ManifestError("The tested source bundle exceeds the entry-count budget.")
                names: list[str] = []
                casefolded: set[str] = set()
                for info in infos:
                    name = info.filename
                    normalized = name if name == BUNDLE_METADATA_NAME else _normalized_relative(
                        name, allow_root=False
                    )
                    if normalized != name or info.is_dir():
                        raise ManifestError("The tested source bundle contains an unsafe entry path.")
                    folded = name.casefold()
                    if folded in casefolded:
                        raise ManifestError("The tested source bundle contains duplicate entry paths.")
                    casefolded.add(folded)
                    names.append(name)
                    if info.compress_type != zipfile.ZIP_STORED or info.compress_size != info.file_size:
                        raise ManifestError("The tested source bundle must use stored entries only.")
                    if info.file_size > MAX_SOURCE_FILE_BYTES and name != BUNDLE_METADATA_NAME:
                        raise ManifestError("A tested source bundle entry exceeds the byte budget.")
                    if info.date_time != BUNDLE_TIMESTAMP:
                        raise ManifestError("The tested source bundle contains a non-deterministic timestamp.")
                    if info.create_system != 3 or (info.external_attr >> 16) != BUNDLE_REGULAR_MODE:
                        raise ManifestError("The tested source bundle contains a non-regular entry mode.")
                    if info.extra or info.comment or (info.flag_bits & ~0x800):
                        raise ManifestError("The tested source bundle contains unsupported entry metadata.")

                _reject_file_ancestor_collisions(
                    (name for name in names if name != BUNDLE_METADATA_NAME),
                    "The tested source bundle",
                )

                if not infos or names[0] != BUNDLE_METADATA_NAME:
                    raise ManifestError("The tested source bundle metadata entry is missing or unordered.")
                source_names = names[1:]
                if source_names != sorted(source_names):
                    raise ManifestError("The tested source bundle entries are not deterministically ordered.")
                metadata_info = infos[0]
                if metadata_info.file_size > MAX_RESULTS_BYTES:
                    raise ManifestError("The tested source bundle metadata exceeds the byte budget.")
                metadata_bytes = archive.read(metadata_info)
                metadata = _parse_json_bytes(metadata_bytes, "source bundle metadata")
                _screen_json_value(metadata, "source bundle metadata")
                if not isinstance(metadata, dict):
                    raise ManifestError("The tested source bundle metadata must be an object.")
                if metadata_bytes != formatted_manifest_bytes(metadata):
                    raise ManifestError("The tested source bundle metadata is not canonically encoded.")
                _require_exact_keys(
                    metadata,
                    {"archiveFormat", "capturedAtUtc", "contractId", "seal", "source"},
                    "source bundle metadata",
                )
                if metadata.get("contractId") != SOURCE_BUNDLE_CONTRACT_ID:
                    raise ManifestError("The tested source bundle contract is unsupported.")
                if metadata.get("archiveFormat") != "zip-stored-normalized-regular-files/1.0":
                    raise ManifestError("The tested source bundle archive format is unsupported.")
                captured_at = metadata.get("capturedAtUtc")
                if not isinstance(captured_at, str) or normalize_captured_at(captured_at) != captured_at:
                    raise ManifestError("The tested source bundle timestamp is invalid or non-canonical.")
                seal = metadata.get("seal")
                if not isinstance(seal, dict) or set(seal) != {
                    "algorithm",
                    "canonicalization",
                    "payloadSha256",
                }:
                    raise ManifestError("The tested source bundle metadata seal is invalid.")
                if seal.get("algorithm") != "sha256" or seal.get(
                    "canonicalization"
                ) != "sorted-key-compact-json-utf8":
                    raise ManifestError("The tested source bundle metadata seal is unsupported.")
                payload = dict(metadata)
                del payload["seal"]
                if sha256_bytes(canonical_json_bytes(payload)) != seal.get("payloadSha256"):
                    raise ManifestError("The tested source bundle metadata seal does not match.")
                source = _validate_bundle_inventory(metadata.get("source"))
                expected_names = [record["path"] for record in source["files"]]
                if source_names != expected_names:
                    raise ManifestError("The tested source bundle entries differ from its inventory.")
                for info, record in zip(infos[1:], source["files"], strict=True):
                    digest = hashlib.sha256()
                    observed = 0
                    with archive.open(info, mode="r") as source_stream:
                        for chunk in iter(lambda: source_stream.read(1024 * 1024), b""):
                            observed += len(chunk)
                            digest.update(chunk)
                    if observed != record["bytes"] or digest.hexdigest() != record["sha256"]:
                        raise ManifestError(
                            f"The tested source bundle entry does not match its inventory: {info.filename}"
                        )
            bundle_stream.seek(0)
            bundle_digest = hashlib.sha256()
            observed_bundle_bytes = 0
            for chunk in iter(lambda: bundle_stream.read(1024 * 1024), b""):
                observed_bundle_bytes += len(chunk)
                bundle_digest.update(chunk)
            after = os.fstat(bundle_stream.fileno())
            before_identity = (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns)
            after_identity = (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns)
            if (
                observed_bundle_bytes != size
                or before_identity != after_identity
                or _is_reparse_point(path)
            ):
                raise ManifestError("The tested source bundle changed while it was being verified.")
    except ManifestError:
        raise
    except (zipfile.BadZipFile, RuntimeError, NotImplementedError) as exception:
        raise ManifestError("The tested source bundle is not a supported deterministic ZIP.") from exception

    record = {
        "bytes": observed_bundle_bytes,
        "path": relative,
        "sha256": bundle_digest.hexdigest(),
    }
    record.update(
        {
            "contractId": SOURCE_BUNDLE_CONTRACT_ID,
            "sourceInventorySha256": source["sha256"],
        }
    )
    return {"bundle": record, "source": source}


def extract_source_bundle(
    root: pathlib.Path,
    bundle_value: str,
    output: pathlib.Path,
) -> dict[str, Any]:
    """Extract a verified bundle into one new project-relative directory."""

    root = root.resolve(strict=True)
    verified = verify_source_bundle(root, bundle_value)
    output = _validated_new_output(root, output)
    if output.exists() or _is_reparse_point(output):
        raise ManifestError("The extraction output already exists and will not be overwritten.")

    # Validate every final destination before creating the output or writing a
    # member. This keeps a late path-budget failure from producing a partial tree.
    destinations: list[tuple[dict[str, Any], pathlib.Path]] = []
    for record in verified["source"]["files"]:
        relative = record["path"]
        destination = output.joinpath(*relative.split("/"))
        if _utf16_units(str(destination)) > MAX_WINDOWS_PATH_UNITS:
            raise ManifestError("An extracted source path exceeds the path budget.")
        try:
            destination.relative_to(output)
        except ValueError as exception:
            raise ManifestError("An extracted source path escapes its output root.") from exception
        destinations.append((record, destination))

    output.mkdir(parents=False, exist_ok=False)
    if _is_reparse_point(output):
        raise ManifestError("The extraction output is a link or reparse point.")
    bundle_path, _ = resolve_project_path(root, bundle_value, must_exist=True, require_file=True)
    try:
        with zipfile.ZipFile(bundle_path, mode="r", allowZip64=False) as archive:
            for record, destination in destinations:
                relative = record["path"]
                destination.parent.mkdir(parents=True, exist_ok=True)
                current = output
                for segment in pathlib.PurePosixPath(relative).parts[:-1]:
                    current = current / segment
                    if _is_reparse_point(current):
                        raise ManifestError("The extraction path crosses a link or reparse point.")
                with archive.open(relative, mode="r") as source_stream:
                    with destination.open("xb") as destination_stream:
                        digest = hashlib.sha256()
                        observed = 0
                        for chunk in iter(lambda: source_stream.read(1024 * 1024), b""):
                            observed += len(chunk)
                            digest.update(chunk)
                            destination_stream.write(chunk)
                        destination_stream.flush()
                        os.fsync(destination_stream.fileno())
                if observed != record["bytes"] or digest.hexdigest() != record["sha256"]:
                    raise ManifestError(f"An extracted source file failed verification: {relative}")
        extracted = collect_source_inventory(output, verified["source"]["roots"])
        if extracted != verified["source"]:
            raise ManifestError("The extracted source inventory differs from the retained bundle.")
        return {
            "accepted": True,
            "bundle": verified["bundle"],
            "extractedRoot": output.relative_to(root).as_posix(),
            "sourceInventorySha256": extracted["sha256"],
        }
    except BaseException:
        # Preserve a partial extraction as diagnostic evidence; never guess that
        # recursive cleanup is safe after an interrupted or raced extraction.
        raise


def load_execution_plan(root: pathlib.Path, plan_value: str) -> list[dict[str, Any]]:
    path, _ = resolve_project_path(root, plan_value, must_exist=True, require_file=True)
    document = _read_bounded_json(path, maximum_bytes=MAX_RESULTS_BYTES, field="execution plan")
    document = _typed_object(document, {"commands", "contractId"}, "execution plan")
    if document.get("contractId") != EXECUTION_PLAN_CONTRACT_ID:
        raise ManifestError(f"Execution plan contractId must be {EXECUTION_PLAN_CONTRACT_ID}.")
    commands_value = document.get("commands")
    if not isinstance(commands_value, list) or not commands_value:
        raise ManifestError("The execution plan must contain at least one command.")
    commands: list[dict[str, Any]] = []
    seen: set[str] = set()
    for index, command in enumerate(commands_value):
        command = _typed_object(
            command,
            {"argv", "id", "parser", "timeoutSeconds", "workingDirectory"},
            f"plan.commands[{index}]",
        )
        command_id = command.get("id")
        if not isinstance(command_id, str) or not SAFE_ID.fullmatch(command_id) or command_id in seen:
            raise ManifestError(f"plan.commands[{index}].id is invalid or duplicated.")
        seen.add(command_id)
        working_directory = command.get("workingDirectory")
        if not isinstance(working_directory, str):
            raise ManifestError(f"plan.commands[{index}].workingDirectory must be a string.")
        parser = command.get("parser")
        if not isinstance(parser, str) or parser not in COMMAND_PARSERS:
            raise ManifestError(f"plan.commands[{index}].parser is unsupported.")
        timeout_seconds = command.get("timeoutSeconds")
        if (
            isinstance(timeout_seconds, bool)
            or not isinstance(timeout_seconds, int)
            or timeout_seconds < 1
            or timeout_seconds > MAX_COMMAND_TIMEOUT_SECONDS
        ):
            raise ManifestError(
                f"plan.commands[{index}].timeoutSeconds must be between 1 and {MAX_COMMAND_TIMEOUT_SECONDS}."
            )
        commands.append(
            {
                "argv": _validate_command_arguments(command.get("argv")),
                "id": command_id,
                "parser": parser,
                "timeoutSeconds": timeout_seconds,
                "workingDirectory": _normalized_relative(working_directory, allow_root=True),
            }
        )
    return commands


def _parser_failure(exit_code: int) -> dict[str, Any]:
    return {
        "checks": {"failed": 1, "passed": 0, "skipped": 0},
        "observedMetrics": {},
        "parserAccepted": False,
        "skipDetails": [],
    }


def _parse_command_output(parser: str, output: str, exit_code: int) -> dict[str, Any]:
    """Reduce bounded command output to one closed, content-safe receipt shape."""

    if parser == "codex-recovery-ab":
        if exit_code != 0:
            return _parser_failure(exit_code)
        try:
            encoded = output.encode("utf-8")
            if len(encoded) > MAX_COMMAND_OUTPUT_BYTES:
                return _parser_failure(exit_code)
            evidence = _parse_json_bytes(encoded, "Codex recovery A/B command output")
            _screen_json_value(evidence, "Codex recovery A/B command output")
            sanitized = _sanitize_codex_recovery_ab_evidence(evidence)
        except (ManifestError, UnicodeError):
            return _parser_failure(exit_code)
        return {
            "checks": {"failed": 0, "passed": 1, "skipped": 0},
            "observedMetrics": {"codexRecoveryAbEvidence": sanitized},
            "parserAccepted": True,
            "skipDetails": [],
        }

    if parser == "exit-only":
        return {
            "checks": {
                "failed": 0 if exit_code == 0 else 1,
                "passed": 1 if exit_code == 0 else 0,
                "skipped": 0,
            },
            "observedMetrics": {},
            "parserAccepted": True,
            "skipDetails": [],
        }

    if parser == "msbuild":
        warnings = re.findall(r"(?m)^\s*(\d+)\s+Warning\(s\)\s*$", output)
        errors = re.findall(r"(?m)^\s*(\d+)\s+Error\(s\)\s*$", output)
        if len(warnings) != 1 or len(errors) != 1:
            return _parser_failure(exit_code)
        warning_count = int(warnings[-1])
        error_count = int(errors[-1])
        failed = error_count if error_count else (0 if exit_code == 0 else 1)
        return {
            "checks": {"failed": failed, "passed": 1 if failed == 0 else 0, "skipped": 0},
            "observedMetrics": {
                "fullBuild": {
                    "errors": error_count,
                    "exitCode": exit_code,
                    "warnings": warning_count,
                }
            },
            "parserAccepted": True,
            "skipDetails": [],
        }

    if parser == "python-unittest":
        totals = re.findall(r"(?m)^Ran\s+(\d+)\s+tests?\s+in\s+[^\r\n]+\r?$", output)
        ok_lines = re.findall(r"(?m)^OK(?:\s+\(skipped=(\d+)\))?\s*$", output)
        failed_lines = re.findall(r"(?m)^FAILED\s+\(([^\r\n]+)\)\s*$", output)
        if len(totals) != 1 or (len(ok_lines) + len(failed_lines)) != 1:
            return _parser_failure(exit_code)
        total = int(totals[0])
        skipped = 0
        failed = 0
        if ok_lines:
            skipped = int(ok_lines[0] or 0)
        else:
            fields = {
                name: int(value)
                for name, value in re.findall(r"([a-z]+)=(\d+)", failed_lines[0])
            }
            skipped = fields.get("skipped", 0)
            failed = fields.get("failures", 0) + fields.get("errors", 0)
        if skipped + failed > total:
            return _parser_failure(exit_code)
        if exit_code != 0 and failed == 0:
            failed = 1
        passed = max(0, total - skipped - failed)
        verbose_passes: list[str] = []
        verbose_skips: list[dict[str, str]] = []
        verbose_line = re.compile(
            r"^(?P<name>test_[A-Za-z0-9_]{1,159}) "
            r"\((?P<qualified>[A-Za-z0-9_.]{1,320})\) \.\.\. (?P<result>ok)$"
        )
        verbose_skip_line = re.compile(
            r"^(?P<name>test_[A-Za-z0-9_]{1,159}) "
            r"\((?P<qualified>[A-Za-z0-9_.]{1,320})\) \.\.\. skipped "
            r"(?P<quote>['\"])(?P<reason>[A-Za-z0-9][A-Za-z0-9 ._:/()'-]{0,319})(?P=quote)$"
        )
        saw_verbose = False
        unknown_verbose_line = False
        for line in (item.strip() for item in output.splitlines()):
            if not line:
                continue
            match = verbose_line.fullmatch(line)
            if match is not None:
                saw_verbose = True
                verbose_passes.append(match.group("name"))
                continue
            match = verbose_skip_line.fullmatch(line)
            if match is not None:
                saw_verbose = True
                try:
                    verbose_skips.append(
                        {
                            "name": _screen_public_text(
                                match.group("name"), "unittest skip name", 160
                            ),
                            "reason": _screen_public_text(
                                match.group("reason"), "unittest skip reason", 320
                            ),
                        }
                    )
                except ManifestError:
                    return _parser_failure(exit_code)
                continue
            if (
                re.fullmatch(r"-+", line) is not None
                or re.fullmatch(r"Ran\s+\d+\s+tests?\s+in\s+[^\r\n]+", line) is not None
                or re.fullmatch(r"OK(?:\s+\(skipped=\d+\))?", line) is not None
                or re.fullmatch(r"FAILED\s+\([^\r\n]+\)", line) is not None
            ):
                continue
            unknown_verbose_line = True
        if saw_verbose:
            if (
                unknown_verbose_line
                or
                failed != 0
                or len(verbose_passes) != passed
                or len(verbose_skips) != skipped
                or len(verbose_passes) + len(verbose_skips) != total
            ):
                return _parser_failure(exit_code)
            skip_details = verbose_skips
        else:
            skip_details = [
                {
                    "name": f"python-unittest-skip-{index + 1}",
                    "reason": "Reported by the bounded unittest summary.",
                }
                for index in range(skipped)
            ]
        return {
            "checks": {"failed": failed, "passed": passed, "skipped": skipped},
            "observedMetrics": {},
            "parserAccepted": True,
            "skipDetails": skip_details,
        }

    if parser != "pegasus-harness":
        raise ManifestError(f"Unsupported command output parser: {parser}")

    checks = {"failed": 0, "passed": 0, "skipped": 0}
    skip_details: list[dict[str, str]] = []
    metrics: dict[str, Any] = {}
    valid = True
    # Existing harnesses use commas in descriptive PASS names. Keep the
    # accepted alphabet closed and content-safe while covering that exact
    # established output shape.
    # C# and explicit positive UTC offsets are established bounded harness
    # descriptions. Admit only their two punctuation characters; arbitrary
    # shell/control syntax remains outside the closed alphabet.
    name_pattern = r"[A-Za-z0-9][A-Za-z0-9 #+,._:/-]{0,159}"
    metric_patterns: tuple[tuple[str, re.Pattern[str]], ...] = (
        ("authorityFallback", re.compile(r"^METRIC authority-fallback passed=(true|false)$")),
        (
            "concurrentClients",
            re.compile(r"^METRIC concurrent-clients clients=(\d+) starvation=(\d+) timeouts=(\d+)$"),
        ),
        ("deterministicTime", re.compile(r"^METRIC deterministic-time passed=(true|false)$")),
        (
            "exactSearch",
            re.compile(r"^METRIC exact-search passed=(\d+) total=(\d+) regressions=(\d+)$"),
        ),
        ("offlineRecovery", re.compile(r"^METRIC offline-recovery passed=(true|false)$")),
        (
            "projectBriefAuthority",
            re.compile(r"^METRIC project-brief-authority passed=(true|false)$"),
        ),
        ("reservedIsolation", re.compile(r"^METRIC reserved-isolation leakage=(\d+)$")),
        ("retrievalRecall", re.compile(r"^METRIC retrieval-recall hits=(\d+) total=(\d+)$")),
    )
    summary_patterns = (
        re.compile(r"^RESULT \d+/\d+ passed$"),
        re.compile(r"^RESULT \d+/\d+ passed, \d+ skipped$"),
        re.compile(r"^SUMMARY passed=\d+ failed=\d+$"),
        re.compile(
            r"^RESULT offline=\d+ live=(?:skipped|preflight_passed|restored_state_equivalent|passed)$"
        ),
        re.compile(
            r"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,120} checks passed \(\d+/\d+\)\.$"
        ),
        re.compile(r"^Linux offline-journal platform tests passed: \d+/\d+$"),
    )

    def parse_skip(payload: str) -> tuple[str, str] | None:
        if " :: " in payload:
            name, reason = payload.split(" :: ", 1)
        elif ": " in payload:
            name, reason = payload.split(": ", 1)
        elif payload.endswith(")") and " (" in payload:
            name, reason = payload.rsplit(" (", 1)
            reason = reason[:-1]
        elif payload.startswith("LIVE "):
            name, reason = "LIVE integration", payload[5:]
        else:
            return None
        if re.fullmatch(name_pattern, name) is None:
            return None
        return name, reason

    for line in (item.strip() for item in output.splitlines()):
        if not line:
            continue
        match = re.fullmatch(rf"PASS ({name_pattern})", line)
        if match:
            checks["passed"] += 1
            continue
        match = re.fullmatch(rf"FAIL ({name_pattern})", line)
        if match:
            checks["failed"] += 1
            continue
        if line.startswith("SKIP "):
            parsed_skip = parse_skip(line[5:])
            if parsed_skip is None:
                valid = False
                continue
            skip_name, skip_reason = parsed_skip
            try:
                skip_details.append(
                    {
                        "name": _screen_public_text(skip_name, "harness skip name", 160),
                        "reason": _screen_public_text(skip_reason, "harness skip reason", 320),
                    }
                )
                checks["skipped"] += 1
            except ManifestError:
                valid = False
            continue
        if any(pattern.fullmatch(line) is not None for pattern in summary_patterns):
            # Existing harness summaries are bounded compatibility chatter.
            # PASS/FAIL/SKIP records remain the sole source of check counts.
            continue
        metric_matched = False
        for metric_name, pattern in metric_patterns:
            metric_match = pattern.fullmatch(line)
            if metric_match is None:
                continue
            metric_matched = True
            if metric_name in metrics:
                valid = False
                break
            groups = metric_match.groups()
            if metric_name in {"authorityFallback", "deterministicTime", "offlineRecovery", "projectBriefAuthority"}:
                metrics[metric_name] = groups[0] == "true"
            elif metric_name == "concurrentClients":
                metrics[metric_name] = {
                    "clientCount": int(groups[0]),
                    "starvationCount": int(groups[1]),
                    "timeoutCount": int(groups[2]),
                }
            elif metric_name == "exactSearch":
                metrics[metric_name] = {
                    "passed": int(groups[0]),
                    "total": int(groups[1]),
                    "regressions": int(groups[2]),
                }
            elif metric_name == "reservedIsolation":
                metrics[metric_name] = {"leakageCount": int(groups[0])}
            else:
                metrics[metric_name] = {"hits": int(groups[0]), "total": int(groups[1])}
            break
        if not metric_matched:
            valid = False
    if exit_code != 0 and checks["failed"] == 0:
        checks["failed"] = 1
    if sum(checks.values()) == 0:
        valid = False
    if not valid:
        return _parser_failure(exit_code)
    return {
        "checks": checks,
        "observedMetrics": metrics,
        "parserAccepted": True,
        "skipDetails": sorted(skip_details, key=lambda item: (item["name"], item["reason"])),
    }


def _create_windows_kill_job(process: subprocess.Popen[bytes]) -> int:
    """Place one child in a kill-on-close Job Object before bounded observation."""

    import ctypes
    from ctypes import wintypes

    class JobObjectBasicLimitInformation(ctypes.Structure):
        _fields_ = [
            ("PerProcessUserTimeLimit", ctypes.c_longlong),
            ("PerJobUserTimeLimit", ctypes.c_longlong),
            ("LimitFlags", wintypes.DWORD),
            ("MinimumWorkingSetSize", ctypes.c_size_t),
            ("MaximumWorkingSetSize", ctypes.c_size_t),
            ("ActiveProcessLimit", wintypes.DWORD),
            ("Affinity", ctypes.c_size_t),
            ("PriorityClass", wintypes.DWORD),
            ("SchedulingClass", wintypes.DWORD),
        ]

    class IoCounters(ctypes.Structure):
        _fields_ = [
            ("ReadOperationCount", ctypes.c_ulonglong),
            ("WriteOperationCount", ctypes.c_ulonglong),
            ("OtherOperationCount", ctypes.c_ulonglong),
            ("ReadTransferCount", ctypes.c_ulonglong),
            ("WriteTransferCount", ctypes.c_ulonglong),
            ("OtherTransferCount", ctypes.c_ulonglong),
        ]

    class JobObjectExtendedLimitInformation(ctypes.Structure):
        _fields_ = [
            ("BasicLimitInformation", JobObjectBasicLimitInformation),
            ("IoInfo", IoCounters),
            ("ProcessMemoryLimit", ctypes.c_size_t),
            ("JobMemoryLimit", ctypes.c_size_t),
            ("PeakProcessMemoryUsed", ctypes.c_size_t),
            ("PeakJobMemoryUsed", ctypes.c_size_t),
        ]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel32.CreateJobObjectW.restype = wintypes.HANDLE
    kernel32.SetInformationJobObject.argtypes = [
        wintypes.HANDLE,
        ctypes.c_int,
        ctypes.c_void_p,
        wintypes.DWORD,
    ]
    kernel32.SetInformationJobObject.restype = wintypes.BOOL
    kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL

    handle = kernel32.CreateJobObjectW(None, None)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        information = JobObjectExtendedLimitInformation()
        information.BasicLimitInformation.LimitFlags = 0x00002000  # KILL_ON_JOB_CLOSE
        if not kernel32.SetInformationJobObject(
            handle,
            9,  # JobObjectExtendedLimitInformation
            ctypes.byref(information),
            ctypes.sizeof(information),
        ):
            raise ctypes.WinError(ctypes.get_last_error())
        if not kernel32.AssignProcessToJobObject(handle, wintypes.HANDLE(process._handle)):
            raise ctypes.WinError(ctypes.get_last_error())
        return int(handle)
    except BaseException:
        kernel32.CloseHandle(handle)
        raise


def _close_windows_job(handle: int) -> None:
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    if not kernel32.CloseHandle(wintypes.HANDLE(handle)):
        raise ctypes.WinError(ctypes.get_last_error())


def _terminate_process_tree(
    process: subprocess.Popen[bytes],
    windows_job: int | None,
) -> None:
    """Terminate and reap the complete bounded command tree."""

    if os.name == "nt":
        if windows_job is not None:
            import ctypes
            from ctypes import wintypes

            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
            kernel32.TerminateJobObject.restype = wintypes.BOOL
            if not kernel32.TerminateJobObject(wintypes.HANDLE(windows_job), 1):
                error = ctypes.get_last_error()
                if error not in {0, 5}:  # already terminal/access race
                    raise ctypes.WinError(error)
        elif process.poll() is None:
            process.kill()
    else:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired as exception:
        raise ManifestError("A bounded command process tree could not be reaped.") from exception


def _run_bounded_command(command: dict[str, Any], working_root: pathlib.Path) -> dict[str, Any]:
    # Execution plans are already closed-schema, source-bundle-owned inputs.
    # Windows cannot assign a subprocess to a Job Object before Popen returns,
    # so this boundary is for trusted verification commands rather than a
    # hostile child that deliberately races pre-assignment descendants.
    popen_options: dict[str, Any] = {}
    if os.name == "nt":
        popen_options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        popen_options["start_new_session"] = True
    process = subprocess.Popen(
        command["argv"],
        cwd=working_root,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        **popen_options,
    )
    windows_job: int | None = None
    try:
        if os.name == "nt":
            windows_job = _create_windows_kill_job(process)
    except BaseException:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=5)
        raise
    timed_out = threading.Event()
    termination_failed = threading.Event()

    def terminate_for_timeout() -> None:
        if process.poll() is None:
            timed_out.set()
            try:
                _terminate_process_tree(process, windows_job)
            except BaseException:
                # Never leak platform exception text into a sealed receipt. A
                # failed kill boundary invalidates the receipt after cleanup.
                termination_failed.set()
                if process.poll() is None:
                    process.kill()

    timer = threading.Timer(command["timeoutSeconds"], terminate_for_timeout)
    timer.daemon = True
    timer.start()
    output = bytearray()
    overflow = False
    assert process.stdout is not None
    try:
        while True:
            chunk = process.stdout.read(64 * 1024)
            if not chunk:
                break
            if len(output) + len(chunk) > MAX_COMMAND_OUTPUT_BYTES:
                overflow = True
                if process.poll() is None:
                    _terminate_process_tree(process, windows_job)
                continue
            output.extend(chunk)
        exit_code = process.wait()
    except BaseException:
        _terminate_process_tree(process, windows_job)
        raise
    finally:
        timer.cancel()
        timer.join(timeout=5)
        if timer.is_alive():
            termination_failed.set()
        process.stdout.close()
        # Closing the Windows job is itself the final kill boundary for any
        # descendant that outlived the direct child. POSIX groups are killed
        # explicitly even after the group leader exits.
        try:
            if os.name == "nt" and windows_job is not None:
                _close_windows_job(windows_job)
            elif os.name != "nt":
                _terminate_process_tree(process, None)
        except BaseException:
            termination_failed.set()
    if termination_failed.is_set():
        raise ManifestError("A bounded command process tree could not be terminated cleanly.")
    termination_reason = "timeout" if timed_out.is_set() else ("output-limit" if overflow else "completed")
    if termination_reason != "completed":
        parsed = _parser_failure(exit_code)
    else:
        try:
            text = bytes(output).decode("utf-8", errors="strict")
        except UnicodeDecodeError:
            parsed = _parser_failure(exit_code)
        else:
            parsed = _parse_command_output(command["parser"], text, exit_code)
    return {
        **parsed,
        "exitCode": exit_code,
        "terminationReason": termination_reason,
    }


def execute_source_bundle(
    root: pathlib.Path,
    bundle_value: str,
    plan_value: str,
    extracted_output: pathlib.Path,
    receipt_output: pathlib.Path,
    *,
    captured_at: str,
) -> dict[str, Any]:
    """Execute an explicit plan in a freshly extracted, hash-verified source tree."""

    if captured_at is None:
        raise ManifestError("An explicit captured-at timestamp is required for deterministic execution evidence.")
    root = root.resolve(strict=True)
    verified = verify_source_bundle(root, bundle_value)
    commands = load_execution_plan(root, plan_value)
    extraction = extract_source_bundle(root, bundle_value, extracted_output)
    extracted_root, extracted_relative = resolve_project_path(
        root,
        extraction["extractedRoot"],
        must_exist=True,
        require_file=False,
    )
    before = collect_source_inventory(extracted_root, verified["source"]["roots"])
    if before != verified["source"]:
        raise ManifestError("The extracted source differs from the bundle before execution.")

    command_receipts: list[dict[str, Any]] = []
    for command in commands:
        working_root, working_relative = resolve_project_path(
            extracted_root,
            command["workingDirectory"],
            allow_root=True,
            must_exist=True,
            require_file=False,
        )
        observed = _run_bounded_command(command, working_root)
        command_receipts.append(
            {
                "argv": command["argv"],
                "checks": observed["checks"],
                "exitCode": observed["exitCode"],
                "id": command["id"],
                "observedMetrics": observed["observedMetrics"],
                "parser": command["parser"],
                "parserAccepted": observed["parserAccepted"],
                "skipDetails": observed["skipDetails"],
                "terminationReason": observed["terminationReason"],
                "timeoutSeconds": command["timeoutSeconds"],
                "workingDirectory": working_relative,
            }
        )

    after = collect_source_inventory(extracted_root, verified["source"]["roots"])
    stable = before == after == verified["source"]
    payload: dict[str, Any] = {
        "accepted": stable
        and all(
            command["exitCode"] == 0
            and command["parserAccepted"]
            and command["terminationReason"] == "completed"
            for command in command_receipts
        ),
        "afterSourceInventorySha256": after["sha256"],
        "beforeSourceInventorySha256": before["sha256"],
        "capturedAtUtc": normalize_captured_at(captured_at),
        "commands": command_receipts,
        "contractId": EXECUTION_RECEIPT_CONTRACT_ID,
        "extractedRoot": extracted_relative,
        "sourceInventorySha256": verified["source"]["sha256"],
        "testedSourceBundle": verified["bundle"],
    }
    payload["seal"] = {
        "algorithm": "sha256",
        "canonicalization": "sorted-key-compact-json-utf8",
        "payloadSha256": sha256_bytes(canonical_json_bytes(payload)),
    }
    receipt_output = _validated_new_output(root, receipt_output)
    write_manifest(receipt_output, payload)
    return payload


def load_execution_receipt(
    root: pathlib.Path,
    receipt_value: str,
    bundle: dict[str, Any],
) -> tuple[dict[str, Any], dict[str, Any]]:
    path, relative = resolve_project_path(root, receipt_value, must_exist=True, require_file=True)
    receipt, data = _read_bounded_json_payload(
        path,
        maximum_bytes=MAX_RESULTS_BYTES,
        field="execution receipt",
    )
    receipt = _typed_object(
        receipt,
        {
            "accepted",
            "afterSourceInventorySha256",
            "beforeSourceInventorySha256",
            "capturedAtUtc",
            "commands",
            "contractId",
            "extractedRoot",
            "seal",
            "sourceInventorySha256",
            "testedSourceBundle",
        },
        "execution receipt",
    )
    if receipt.get("contractId") != EXECUTION_RECEIPT_CONTRACT_ID:
        raise ManifestError("The execution receipt contract is unsupported.")
    seal = receipt.get("seal")
    if not isinstance(seal, dict) or set(seal) != {"algorithm", "canonicalization", "payloadSha256"}:
        raise ManifestError("The execution receipt seal is invalid.")
    payload = dict(receipt)
    del payload["seal"]
    if (
        seal.get("algorithm") != "sha256"
        or seal.get("canonicalization") != "sorted-key-compact-json-utf8"
        or seal.get("payloadSha256") != sha256_bytes(canonical_json_bytes(payload))
    ):
        raise ManifestError("The execution receipt seal does not match.")
    captured_at = receipt.get("capturedAtUtc")
    if not isinstance(captured_at, str) or normalize_captured_at(captured_at) != captured_at:
        raise ManifestError("The execution receipt timestamp is invalid or non-canonical.")
    if receipt.get("testedSourceBundle") != bundle["bundle"]:
        raise ManifestError("The execution receipt is not bound to the tested source bundle.")
    source_hash = bundle["source"]["sha256"]
    if any(
        receipt.get(field) != source_hash
        for field in (
            "beforeSourceInventorySha256",
            "afterSourceInventorySha256",
            "sourceInventorySha256",
        )
    ):
        raise ManifestError("The execution receipt source inventory binding does not match.")
    extracted_value = receipt.get("extractedRoot")
    if not isinstance(extracted_value, str):
        raise ManifestError("The execution receipt extractedRoot is invalid.")
    extracted_root, _ = resolve_project_path(
        root, extracted_value, must_exist=True, require_file=False
    )
    current = collect_source_inventory(extracted_root, bundle["source"]["roots"])
    if current != bundle["source"]:
        raise ManifestError("The executed source tree no longer matches the tested source bundle.")

    commands_value = receipt.get("commands")
    if not isinstance(commands_value, list) or not commands_value:
        raise ManifestError("The execution receipt commands are missing.")
    commands: list[dict[str, Any]] = []
    seen: set[str] = set()
    for index, command in enumerate(commands_value):
        command = _typed_object(
            command,
            {
                "argv",
                "checks",
                "exitCode",
                "id",
                "observedMetrics",
                "parser",
                "parserAccepted",
                "skipDetails",
                "terminationReason",
                "timeoutSeconds",
                "workingDirectory",
            },
            f"receipt.commands[{index}]",
        )
        command_id = command.get("id")
        if not isinstance(command_id, str) or not SAFE_ID.fullmatch(command_id) or command_id in seen:
            raise ManifestError(f"receipt.commands[{index}].id is invalid or duplicated.")
        seen.add(command_id)
        exit_code = command.get("exitCode")
        if isinstance(exit_code, bool) or not isinstance(exit_code, int):
            raise ManifestError(f"receipt.commands[{index}].exitCode must be an integer.")
        parser = command.get("parser")
        if not isinstance(parser, str) or parser not in COMMAND_PARSERS:
            raise ManifestError(f"receipt.commands[{index}].parser is unsupported.")
        parser_accepted = command.get("parserAccepted")
        if not isinstance(parser_accepted, bool):
            raise ManifestError(f"receipt.commands[{index}].parserAccepted must be a boolean.")
        termination_reason = command.get("terminationReason")
        if termination_reason not in {"completed", "output-limit", "timeout"}:
            raise ManifestError(f"receipt.commands[{index}].terminationReason is invalid.")
        timeout_seconds = command.get("timeoutSeconds")
        if (
            isinstance(timeout_seconds, bool)
            or not isinstance(timeout_seconds, int)
            or timeout_seconds < 1
            or timeout_seconds > MAX_COMMAND_TIMEOUT_SECONDS
        ):
            raise ManifestError(f"receipt.commands[{index}].timeoutSeconds is invalid.")
        checks_value = _typed_object(
            command.get("checks"), {"failed", "passed", "skipped"}, f"receipt.commands[{index}].checks"
        )
        checks = {
            key: _nonnegative_integer(checks_value[key], f"receipt.commands[{index}].checks.{key}")
            for key in ("failed", "passed", "skipped")
        }
        skip_values = command.get("skipDetails")
        if not isinstance(skip_values, list):
            raise ManifestError(f"receipt.commands[{index}].skipDetails must be an array.")
        skip_details: list[dict[str, str]] = []
        for detail_index, detail in enumerate(skip_values):
            detail = _typed_object(detail, {"name", "reason"}, "receipt skip detail")
            skip_details.append(
                {
                    "name": _validate_public_text(
                        detail.get("name"), f"receipt.commands[{index}].skipDetails[{detail_index}].name", 160
                    ),
                    "reason": _validate_public_text(
                        detail.get("reason"),
                        f"receipt.commands[{index}].skipDetails[{detail_index}].reason",
                        320,
                    ),
                }
            )
        if len(skip_details) != checks["skipped"]:
            raise ManifestError("Execution receipt skipped count does not match its named skip details.")
        observed_metrics = command.get("observedMetrics")
        if not isinstance(observed_metrics, dict):
            raise ManifestError(f"receipt.commands[{index}].observedMetrics must be an object.")
        working_directory = command.get("workingDirectory")
        if not isinstance(working_directory, str):
            raise ManifestError(f"receipt.commands[{index}].workingDirectory must be a string.")
        resolve_project_path(
            extracted_root,
            working_directory,
            allow_root=True,
            must_exist=True,
            require_file=False,
        )
        commands.append(
            {
                "argv": _validate_command_arguments(command.get("argv")),
                "checks": checks,
                "exitCode": exit_code,
                "id": command_id,
                "observedMetrics": observed_metrics,
                "parser": parser,
                "parserAccepted": parser_accepted,
                "skipDetails": sorted(skip_details, key=lambda item: (item["name"], item["reason"])),
                "terminationReason": termination_reason,
                "timeoutSeconds": timeout_seconds,
                "workingDirectory": _normalized_relative(working_directory, allow_root=True),
            }
        )
    expected_accepted = all(
        command["exitCode"] == 0
        and command["parserAccepted"]
        and command["terminationReason"] == "completed"
        for command in commands
    )
    if receipt.get("accepted") is not expected_accepted:
        raise ManifestError("The execution receipt accepted flag is inconsistent.")
    sanitized = dict(receipt)
    sanitized["commands"] = commands
    return sanitized, {"bytes": len(data), "path": relative, "sha256": sha256_bytes(data)}


def derive_acceptance_metrics_from_receipt(
    receipt: dict[str, Any],
    *,
    source_inventory: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Derive every supported gate-driving metric from sealed command receipts."""

    observed: dict[str, Any] = {}
    harness = {"failed": 0, "passed": 0, "skipped": 0}
    for command in receipt["commands"]:
        if command["parser"] in {"pegasus-harness", "python-unittest"}:
            for key in harness:
                harness[key] += command["checks"][key]
        for name, value in command["observedMetrics"].items():
            if name in observed:
                raise ManifestError(f"Execution receipt contains duplicate gate metric: {name}")
            observed[name] = value
    ab_evidence = observed.pop("codexRecoveryAbEvidence", None)
    required = {
        "authorityFallback",
        "concurrentClients",
        "deterministicTime",
        "exactSearch",
        "fullBuild",
        "offlineRecovery",
        "projectBriefAuthority",
        "reservedIsolation",
        "retrievalRecall",
    }
    missing = sorted(required - set(observed))
    unknown = sorted(set(observed) - required)
    if missing or unknown:
        details = []
        if missing:
            details.append(f"missing {', '.join(missing)}")
        if unknown:
            details.append(f"unknown {', '.join(unknown)}")
        raise ManifestError(f"Execution receipt gate metrics are incomplete: {'; '.join(details)}")
    ab_metrics = None
    if ab_evidence is not None:
        tested_source_bundle = receipt.get("testedSourceBundle")
        tested_source_bundle_sha256 = (
            tested_source_bundle.get("sha256") if isinstance(tested_source_bundle, dict) else None
        )
        if not isinstance(tested_source_bundle_sha256, str):
            raise ManifestError(
                "Execution receipt must bind Codex recovery A/B evidence to its tested source bundle."
            )
        receipt_captured_at_utc = receipt.get("capturedAtUtc")
        if not isinstance(receipt_captured_at_utc, str):
            raise ManifestError(
                "Execution receipt must bind Codex recovery A/B evidence to its capture timestamp."
            )
        if source_inventory is None:
            raise ManifestError(
                "Execution receipt must bind Codex recovery A/B evidence to the tested source inventory."
            )
        ab_metrics = _derive_codex_recovery_ab_metrics(
            ab_evidence,
            receipt_captured_at_utc=receipt_captured_at_utc,
            source_inventory=source_inventory,
            tested_source_bundle_sha256=tested_source_bundle_sha256,
        )
    return {
        "authorityFallback": observed["authorityFallback"],
        "codexRecoveryAb": ab_metrics,
        "concurrentClients": observed["concurrentClients"],
        "deterministicTime": observed["deterministicTime"],
        "exactSearch": observed["exactSearch"],
        "fullBuild": observed["fullBuild"],
        "harnessMatrix": harness,
        "offlineRecovery": observed["offlineRecovery"],
        "projectBriefAuthority": observed["projectBriefAuthority"],
        "reservedIsolation": observed["reservedIsolation"],
        "retrievalRecall": observed["retrievalRecall"],
    }


def _gate(gate_id: str, passed: bool, summary: str, *, unavailable: bool = False) -> dict[str, str]:
    return {
        "id": gate_id,
        "status": "blocked" if unavailable else ("passed" if passed else "failed"),
        "summary": summary,
    }


def _derived_gates(metrics: dict[str, Any]) -> list[dict[str, str]]:
    concurrent = metrics["concurrentClients"]
    exact = metrics["exactSearch"]
    build = metrics["fullBuild"]
    harness = metrics["harnessMatrix"]
    recall = metrics["retrievalRecall"]
    ab = metrics["codexRecoveryAb"]
    gates = [
        _gate("authority-fallback", metrics["authorityFallback"], "Authority fallback must pass."),
        _gate(
            "concurrent-clients",
            concurrent["clientCount"] > 0
            and concurrent["starvationCount"] == 0
            and concurrent["timeoutCount"] == 0,
            "Concurrent clients require positive coverage with zero starvation and timeouts.",
        ),
        _gate("deterministic-time", metrics["deterministicTime"], "Deterministic time must pass."),
        _gate(
            "exact-search",
            exact["total"] > 0 and exact["passed"] == exact["total"] and exact["regressions"] == 0,
            "Exact search requires complete coverage and zero regressions.",
        ),
        _gate(
            "full-build",
            build["exitCode"] == 0 and build["warnings"] == 0 and build["errors"] == 0,
            "The full build requires exit zero with zero warnings and errors.",
        ),
        _gate(
            "harness-matrix",
            harness["passed"] > 0 and harness["failed"] == 0,
            "The harness matrix requires positive coverage and zero failures; named environment/platform skips remain explicit evidence.",
        ),
        _gate("offline-recovery", metrics["offlineRecovery"], "Offline recovery must pass."),
        _gate(
            "project-brief-authority",
            metrics["projectBriefAuthority"],
            "Project brief authority binding must pass.",
        ),
        _gate(
            "reserved-isolation",
            metrics["reservedIsolation"]["leakageCount"] == 0,
            "Reserved isolation requires zero leakage.",
        ),
        _gate(
            "retrieval-recall",
            recall["total"] >= 20 and recall["hits"] >= 19 and recall["hits"] <= recall["total"],
            "Retrieval Recall@3 requires at least 19 of 20 successful queries.",
        ),
    ]
    if ab is None:
        gates.append(_gate("codex-recovery-ab", False, "The paired Codex recovery A/B has not run.", unavailable=True))
    else:
        gates.append(
            _gate(
                "codex-recovery-ab",
                ab["trials"] >= 20
                and ab["stateMatches"] == ab["trials"]
                and ab["medianUncachedInputReductionPercent"] >= 70.0
                and ab["medianLatencyReductionPercent"] >= 15.0
                and ab["p95LatencyRegressionPercent"] <= 10.0
                and ab["maxCortexCalls"] <= 2,
                "Codex recovery A/B requires 20 trials, exact state, input/latency targets, bounded p95, and at most two calls.",
            )
        )
    return sorted(gates, key=lambda item: item["id"])


def _excluded_coverage_gate(
    commands: Sequence[dict[str, Any]],
) -> tuple[dict[str, str], dict[str, Any]]:
    """Admit only predeclared environment/platform skips for exact commands."""

    admissible: list[dict[str, str]] = []
    unexpected: list[dict[str, str]] = []
    for command in commands:
        command_id = command["id"]
        for detail in command["skipDetails"]:
            record = {
                "commandId": command_id,
                "name": detail["name"],
                "reason": detail["reason"],
            }
            identity = (command_id, detail["name"], detail["reason"])
            (admissible if identity in ADMISSIBLE_ACCEPTANCE_SKIPS else unexpected).append(record)
    admissible.sort(key=lambda item: (item["commandId"], item["name"], item["reason"]))
    unexpected.sort(key=lambda item: (item["commandId"], item["name"], item["reason"]))
    gate = _gate(
        "excluded-coverage",
        not unexpected,
        "Every skip must match an exact command, test name, and content-safe approved platform/environment reason.",
    )
    return gate, {
        "admissible": admissible,
        "admissibleCount": len(admissible),
        "unexpected": unexpected,
        "unexpectedCount": len(unexpected),
    }


def _verification_summary(
    results: dict[str, Any],
    source: dict[str, Any],
    metrics: dict[str, Any],
    receipt: dict[str, Any],
) -> dict[str, Any]:
    commands = results["commands"]
    totals = {
        key: sum(command["checks"][key] for command in commands)
        for key in ("failed", "passed", "skipped")
    }
    receipt_by_id = {command["id"]: command for command in receipt["commands"]}
    commands_bound = len(receipt_by_id) == len(commands) and all(
        command["id"] in receipt_by_id
        and all(
            command[field] == receipt_by_id[command["id"]][field]
            for field in ("argv", "checks", "exitCode", "skipDetails", "workingDirectory")
        )
        for command in commands
    )
    zero_check_commands = sorted(
        command["id"] for command in commands if sum(command["checks"].values()) == 0
    )
    derived_gates = _derived_gates(metrics)
    excluded_coverage_gate, skip_coverage = _excluded_coverage_gate(commands)
    gates = sorted(
        derived_gates + [excluded_coverage_gate] + results["additionalGates"],
        key=lambda item: item["id"],
    )
    gate_statuses = {gate["id"]: gate["status"] for gate in gates}
    missing = sorted(REQUIRED_ACCEPTANCE_GATES - set(gate_statuses))
    required_passed = not missing and all(
        gate_statuses[gate_id] == "passed" for gate_id in REQUIRED_ACCEPTANCE_GATES
    )
    all_gates_resolved = all(gate["status"] == "passed" for gate in gates)
    missing_source_roots = sorted(set(DEFAULT_SOURCE_PATHS) - set(source["roots"]))
    source_file_paths = {record["path"] for record in source["files"]}
    missing_authority_files = sorted(MANDATORY_AUTHORITY_FILES - source_file_paths)
    accepted = (
        all(command["exitCode"] == 0 for command in commands)
        and totals["failed"] == 0
        and not zero_check_commands
        and commands_bound
        and receipt["accepted"]
        and not missing_source_roots
        and not missing_authority_files
        and required_passed
        and all_gates_resolved
    )
    return {
        "acceptanceProfileId": results["acceptanceProfileId"],
        "accepted": accepted,
        "commands": commands,
        "commandsBoundToExecutionReceipt": commands_bound,
        "gates": gates,
        "missingRequiredGates": missing,
        "missingRequiredAuthorityFiles": missing_authority_files,
        "missingRequiredSourceRoots": missing_source_roots,
        "requiredGateIds": sorted(REQUIRED_ACCEPTANCE_GATES),
        "skipCoverage": skip_coverage,
        "totals": totals,
        "zeroCheckCommandIds": zero_check_commands,
    }


def build_manifest(
    root: pathlib.Path,
    results_path: str,
    *,
    tested_source_bundle: str,
    captured_at: str,
    environment: dict[str, Any] | None = None,
    dotnet: str = "dotnet",
) -> dict[str, Any]:
    if captured_at is None:
        raise ManifestError("An explicit captured-at timestamp is required for deterministic manifest bytes.")
    root = root.resolve(strict=True)
    if not root.is_dir():
        raise ManifestError("The project root must be a directory.")
    results, results_record = load_verification_results(root, results_path)
    evidence = collect_evidence_inventory(root, results["evidencePaths"])
    metrics = load_acceptance_metrics(root, results["evidencePaths"])
    bundle = verify_source_bundle(root, tested_source_bundle)
    execution_receipt, execution_receipt_record = load_execution_receipt(
        root, results["executionReceiptPath"], bundle
    )
    trusted_metrics = derive_acceptance_metrics_from_receipt(
        execution_receipt, source_inventory=bundle["source"]
    )
    if metrics != trusted_metrics:
        raise ManifestError("Typed acceptance metrics differ from trusted execution observations.")
    environment_value = environment if environment is not None else collect_environment(dotnet)
    _screen_json_value(environment_value, "captured environment")
    repeated_results, repeated_results_record = load_verification_results(root, results_path)
    repeated_evidence = collect_evidence_inventory(root, repeated_results["evidencePaths"])
    repeated_metrics = load_acceptance_metrics(root, repeated_results["evidencePaths"])
    repeated_receipt, repeated_receipt_record = load_execution_receipt(
        root, repeated_results["executionReceiptPath"], bundle
    )
    repeated_trusted_metrics = derive_acceptance_metrics_from_receipt(
        repeated_receipt, source_inventory=bundle["source"]
    )
    if results != repeated_results or results_record != repeated_results_record:
        raise ManifestError("The structured results changed during manifest creation.")
    if evidence != repeated_evidence:
        raise ManifestError("The structured evidence changed during manifest creation.")
    if metrics != repeated_metrics or trusted_metrics != repeated_trusted_metrics:
        raise ManifestError("The typed acceptance metrics changed during manifest creation.")
    if execution_receipt != repeated_receipt or execution_receipt_record != repeated_receipt_record:
        raise ManifestError("The execution receipt changed during manifest creation.")
    payload: dict[str, Any] = {
        "capturedAtUtc": normalize_captured_at(captured_at),
        "contractId": CONTRACT_ID,
        "environment": environment_value,
        "evidence": evidence,
        "executionReceipt": execution_receipt_record,
        "projectRootName": _screen_public_text(root.name, "project root name", 160),
        "resultsInput": results_record,
        "source": bundle["source"],
        "testedSourceBundle": bundle["bundle"],
        "verification": _verification_summary(
            results, bundle["source"], trusted_metrics, execution_receipt
        ),
    }
    _screen_json_value(payload, "verification manifest")
    payload["seal"] = {
        "algorithm": "sha256",
        "canonicalization": "sorted-key-compact-json-utf8",
        "payloadSha256": sha256_bytes(canonical_json_bytes(payload)),
    }
    return payload


def formatted_manifest_bytes(manifest: dict[str, Any]) -> bytes:
    return (
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    ).encode("utf-8")


def write_manifest(path: pathlib.Path, manifest: dict[str, Any]) -> None:
    if path.exists() or _is_reparse_point(path):
        raise ManifestError("The manifest output already exists; sealed evidence is never overwritten.")
    path.parent.mkdir(parents=False, exist_ok=True)
    data = formatted_manifest_bytes(manifest)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = pathlib.Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        # A same-directory hard link publishes complete bytes atomically while
        # preserving the no-overwrite evidence boundary across racing writers.
        _publish_temporary_no_overwrite(temporary, path)
        temporary.unlink()
    except BaseException:
        temporary.unlink(missing_ok=True)
        raise


def _validate_inventory(root: pathlib.Path, value: Any, field: str) -> None:
    if not isinstance(value, dict):
        raise ManifestError(f"{field} inventory is missing.")
    records = value.get("files")
    if not isinstance(records, list):
        raise ManifestError(f"{field}.files must be an array.")
    if not all(isinstance(record, dict) for record in records):
        raise ManifestError(f"{field}.files must contain objects.")
    if records != sorted(records, key=lambda item: item.get("path", "")):
        raise ManifestError(f"{field} inventory is not deterministically ordered.")
    actual_records: list[dict[str, Any]] = []
    for record in records:
        expected_keys = (
            {"bytes", "contractId", "path", "sha256"}
            if field == "evidence"
            else {"bytes", "path", "sha256"}
        )
        if not isinstance(record, dict) or set(record) != expected_keys:
            raise ManifestError(f"{field} contains an invalid file record.")
        path_value = record.get("path")
        if not isinstance(path_value, str):
            raise ManifestError(f"{field} contains an invalid path.")
        path, relative = resolve_project_path(root, path_value, must_exist=True, require_file=True)
        if field == "evidence":
            _validate_evidence_path(relative)
            actual_records.append(_record_evidence_file(root, path))
        else:
            actual_records.append(_record_file(root, path))
    if actual_records != records:
        raise ManifestError(f"{field} file bytes or hashes changed.")
    expected = {
        "fileCount": len(records),
        "sha256": _inventory_digest(records),
        "totalBytes": sum(record["bytes"] for record in records),
    }
    for key, expected_value in expected.items():
        if value.get(key) != expected_value:
            raise ManifestError(f"{field}.{key} does not match its file inventory.")


def verify_manifest(root: pathlib.Path, manifest_path: str) -> dict[str, Any]:
    root = root.resolve(strict=True)
    path, _ = resolve_project_path(root, manifest_path, must_exist=True, require_file=True)
    manifest = _read_bounded_json(
        path,
        maximum_bytes=MAX_MANIFEST_BYTES,
        field="verification manifest",
    )
    if not isinstance(manifest, dict) or manifest.get("contractId") != CONTRACT_ID:
        raise ManifestError("The verification manifest contract is unsupported.")
    _require_exact_keys(
        manifest,
        {
            "capturedAtUtc",
            "contractId",
            "environment",
            "evidence",
            "executionReceipt",
            "projectRootName",
            "resultsInput",
            "seal",
            "source",
            "testedSourceBundle",
            "verification",
        },
        "manifest",
    )
    captured_at = manifest.get("capturedAtUtc")
    if not isinstance(captured_at, str) or normalize_captured_at(captured_at) != captured_at:
        raise ManifestError("The verification manifest timestamp is invalid or non-canonical.")
    if manifest.get("projectRootName") != root.name:
        raise ManifestError("The verification manifest project root name does not match.")
    seal = manifest.get("seal")
    if not isinstance(seal, dict) or set(seal) != {"algorithm", "canonicalization", "payloadSha256"}:
        raise ManifestError("The manifest seal is invalid.")
    if seal.get("algorithm") != "sha256" or seal.get("canonicalization") != "sorted-key-compact-json-utf8":
        raise ManifestError("The manifest seal algorithm is unsupported.")
    payload = dict(manifest)
    del payload["seal"]
    if sha256_bytes(canonical_json_bytes(payload)) != seal.get("payloadSha256"):
        raise ManifestError("The manifest payload seal does not match.")

    results_record = manifest.get("resultsInput")
    if not isinstance(results_record, dict) or set(results_record) != {"bytes", "path", "sha256"}:
        raise ManifestError("The manifest resultsInput record is invalid.")
    results_path_value = results_record.get("path")
    if not isinstance(results_path_value, str):
        raise ManifestError("The manifest resultsInput path is invalid.")
    results_path, _ = resolve_project_path(
        root,
        results_path_value,
        must_exist=True,
        require_file=True,
    )
    if _record_file(root, results_path) != results_record:
        raise ManifestError("The structured results input changed.")

    results, sanitized_results_record = load_verification_results(root, results_path_value)
    if sanitized_results_record != results_record:
        raise ManifestError("The structured results input record is inconsistent.")
    verification = manifest.get("verification")
    if not isinstance(verification, dict):
        raise ManifestError("The manifest verification summary is missing.")
    evidence = manifest.get("evidence")
    _validate_inventory(root, evidence, "evidence")
    if evidence != collect_evidence_inventory(root, results["evidencePaths"]):
        raise ManifestError("The evidence inventory differs from its structured results input.")
    metrics = load_acceptance_metrics(root, results["evidencePaths"])

    tested_source_bundle = manifest.get("testedSourceBundle")
    if not isinstance(tested_source_bundle, dict) or set(tested_source_bundle) != {
        "bytes",
        "contractId",
        "path",
        "sha256",
        "sourceInventorySha256",
    }:
        raise ManifestError("The tested source bundle record is invalid.")
    bundle_path = tested_source_bundle.get("path")
    if not isinstance(bundle_path, str):
        raise ManifestError("The tested source bundle path is invalid.")
    actual_bundle = verify_source_bundle(root, bundle_path)
    if tested_source_bundle != actual_bundle["bundle"]:
        raise ManifestError("The tested source bundle record changed.")
    source = manifest.get("source")
    if source != actual_bundle["source"]:
        raise ManifestError("The source inventory differs from the retained tested source bundle.")
    execution_receipt, execution_receipt_record = load_execution_receipt(
        root, results["executionReceiptPath"], actual_bundle
    )
    if manifest.get("executionReceipt") != execution_receipt_record:
        raise ManifestError("The execution receipt record changed.")
    trusted_metrics = derive_acceptance_metrics_from_receipt(
        execution_receipt, source_inventory=actual_bundle["source"]
    )
    if metrics != trusted_metrics:
        raise ManifestError("Typed acceptance metrics differ from trusted execution observations.")
    expected_verification = _verification_summary(
        results, actual_bundle["source"], trusted_metrics, execution_receipt
    )
    if verification != expected_verification:
        raise ManifestError("The verification summary differs from its bound inputs.")
    return manifest


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Create or verify a deterministic, content-safe Cortex verification manifest."
    )
    subparsers = parser.add_subparsers(dest="operation", required=True)
    create = subparsers.add_parser("create", help="Create one new sealed manifest.")
    create.add_argument("--root", type=pathlib.Path, required=True)
    create.add_argument("--results", required=True, help="Project-relative structured results JSON.")
    create.add_argument("--output", required=True, help="New project-relative manifest path.")
    create.add_argument(
        "--captured-at",
        required=True,
        help="Required timezone-qualified ISO-8601 timestamp for deterministic manifest bytes.",
    )
    create.add_argument("--dotnet", default="dotnet", help="dotnet executable or absolute path.")
    create.add_argument(
        "--tested-source-bundle",
        required=True,
        help="Verified project-relative deterministic source bundle.",
    )

    verify = subparsers.add_parser("verify", help="Verify a manifest and every bound local file.")
    verify.add_argument("--root", type=pathlib.Path, required=True)
    verify.add_argument("--manifest", required=True, help="Project-relative manifest path.")

    execute = subparsers.add_parser(
        "execute",
        help="Extract a bundle, run an explicit command plan, and write a bound receipt.",
    )
    execute.add_argument("--root", type=pathlib.Path, required=True)
    execute.add_argument("--bundle", required=True, help="Project-relative deterministic ZIP path.")
    execute.add_argument("--plan", required=True, help="Project-relative typed execution-plan JSON.")
    execute.add_argument(
        "--extract-output",
        required=True,
        help="New project-relative extraction directory.",
    )
    execute.add_argument(
        "--receipt-output",
        required=True,
        help="New project-relative execution receipt JSON.",
    )
    execute.add_argument(
        "--captured-at",
        required=True,
        help="Required timezone-qualified ISO-8601 timestamp for deterministic execution evidence.",
    )

    bundle = subparsers.add_parser("bundle", help="Create, verify, or extract tested source bytes.")
    bundle_operations = bundle.add_subparsers(dest="bundle_operation", required=True)
    bundle_create = bundle_operations.add_parser(
        "create",
        help="Create one deterministic no-overwrite tested source bundle.",
    )
    bundle_create.add_argument("--root", type=pathlib.Path, required=True)
    bundle_create.add_argument("--output", required=True, help="New project-relative ZIP path.")
    bundle_create.add_argument(
        "--captured-at",
        required=True,
        help="Required timezone-qualified ISO-8601 timestamp for reproducible bytes.",
    )
    bundle_create.add_argument(
        "--source",
        action="append",
        help="Project-relative source root; repeat to replace the safe defaults.",
    )
    bundle_verify = bundle_operations.add_parser(
        "verify",
        help="Verify a deterministic tested source bundle.",
    )
    bundle_verify.add_argument("--root", type=pathlib.Path, required=True)
    bundle_verify.add_argument("--bundle", required=True, help="Project-relative ZIP path.")
    bundle_extract = bundle_operations.add_parser(
        "extract",
        help="Verify and extract a bundle into one new directory.",
    )
    bundle_extract.add_argument("--root", type=pathlib.Path, required=True)
    bundle_extract.add_argument("--bundle", required=True, help="Project-relative ZIP path.")
    bundle_extract.add_argument(
        "--output",
        required=True,
        help="New project-relative extraction directory.",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        root = args.root.resolve(strict=True)
        if args.operation == "execute":
            extracted_output, _ = resolve_project_path(
                root, args.extract_output, must_exist=False, require_file=None
            )
            receipt_output, receipt_relative = resolve_project_path(
                root, args.receipt_output, must_exist=False, require_file=None
            )
            receipt = execute_source_bundle(
                root,
                args.bundle,
                args.plan,
                extracted_output,
                receipt_output,
                captured_at=args.captured_at,
            )
            print(
                json.dumps(
                    {
                        "accepted": receipt["accepted"],
                        "receipt": receipt_relative,
                        "payloadSha256": receipt["seal"]["payloadSha256"],
                    },
                    separators=(",", ":"),
                    sort_keys=True,
                )
            )
            return 0 if receipt["accepted"] else 1
        if args.operation == "bundle":
            if args.bundle_operation == "create":
                output, output_relative = resolve_project_path(
                    root,
                    args.output,
                    must_exist=False,
                    require_file=None,
                )
                source_paths = tuple(args.source) if args.source else DEFAULT_SOURCE_PATHS
                verified = create_source_bundle(
                    root,
                    output,
                    source_paths=source_paths,
                    captured_at=args.captured_at,
                )
                print(
                    json.dumps(
                        {
                            "accepted": True,
                            "bundle": output_relative,
                            "sha256": verified["bundle"]["sha256"],
                            "sourceInventorySha256": verified["source"]["sha256"],
                        },
                        separators=(",", ":"),
                        sort_keys=True,
                    )
                )
                return 0
            if args.bundle_operation == "verify":
                verified = verify_source_bundle(root, args.bundle)
                print(
                    json.dumps(
                        {
                            "accepted": True,
                            "bundle": args.bundle.replace("\\", "/"),
                            "sha256": verified["bundle"]["sha256"],
                            "sourceInventorySha256": verified["source"]["sha256"],
                        },
                        separators=(",", ":"),
                        sort_keys=True,
                    )
                )
                return 0
            output, _ = resolve_project_path(
                root,
                args.output,
                must_exist=False,
                require_file=None,
            )
            extracted = extract_source_bundle(root, args.bundle, output)
            print(json.dumps(extracted, separators=(",", ":"), sort_keys=True))
            return 0

        if args.operation == "create":
            output, output_relative = resolve_project_path(
                root,
                args.output,
                must_exist=False,
                require_file=None,
            )
            manifest = build_manifest(
                root,
                args.results,
                tested_source_bundle=args.tested_source_bundle,
                captured_at=args.captured_at,
                dotnet=args.dotnet,
            )
            write_manifest(output, manifest)
            print(
                json.dumps(
                    {
                        "accepted": manifest["verification"]["accepted"],
                        "manifest": output_relative,
                        "payloadSha256": manifest["seal"]["payloadSha256"],
                    },
                    separators=(",", ":"),
                    sort_keys=True,
                )
            )
            return 0

        manifest = verify_manifest(root, args.manifest)
        print(
            json.dumps(
                {
                    "accepted": manifest["verification"]["accepted"],
                    "manifest": args.manifest.replace("\\", "/"),
                    "payloadSha256": manifest["seal"]["payloadSha256"],
                },
                separators=(",", ":"),
                sort_keys=True,
            )
        )
        return 0
    except (ManifestError, OSError, zipfile.LargeZipFile) as exception:
        print(f"ERROR {exception}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
