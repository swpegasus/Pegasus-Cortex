#!/usr/bin/env python3
"""Retired validator for the historical DLL/profile Cortex x64 package."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import stat
import subprocess
import sys
import tempfile
import zipfile
import xml.etree.ElementTree as element_tree
from typing import Any, Iterable


RETIREMENT_CODE = "PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED"
RETIREMENT_MESSAGE = (
    f"{RETIREMENT_CODE}: The framework-dependent "
    "PegasusCortex.Mcp.Local.dll/profile package and its validator are "
    "superseded. Use deploy/x64/New-PegasusCortexV2PackageManifest.ps1 and "
    "deploy/x64/Install-PegasusCortexV2X64.ps1. No files were changed."
)

# Historical validation implementation remains below for source archaeology.
# Public invocation and the former verify_package API fail closed.


SOURCE_FILES = (
    "Directory.Build.props",
    "Directory.Version.props",
    "PegasusCortex.slnx",
    "global.json",
    "NuGet.Config",
    "VERSION.json",
    "tools/New-CortexMcpX64Package.ps1",
    "tools/Test-CortexMcpX64Package.py",
    "tools/tests/test_cortex_mcp_x64_package.py",
)
SOURCE_TREES = ("schemas", "src")
PROJECT_ENTRY = "src/PegasusCortex.Mcp.Local/PegasusCortex.Mcp.Local.csproj"
EXPECTED_TOOLS = {
    "cortex_discover",
    "cortex_status",
    "cortex_search",
    "cortex_read",
    "cortex_project_brief",
    "cortex_read_retained_record",
    "cortex_submit_task_snapshot",
    "cortex_submit_work_packet",
    "cortex_submit_work_packet_handoff_receipt",
    "cortex_submit_work_packet_acceptance_receipt",
    "cortex_submit_proposal",
    "cortex_list_proposals",
}
WRITABLE_TOOLS = {
    "cortex_submit_task_snapshot",
    "cortex_submit_work_packet",
    "cortex_submit_work_packet_handoff_receipt",
    "cortex_submit_work_packet_acceptance_receipt",
    "cortex_submit_proposal",
}
TOOL_ARGUMENTS = {
    "cortex_discover": (set(), set()),
    "cortex_status": (set(), set()),
    "cortex_search": ({"text", "taxonomy", "productId", "componentId", "limit", "mode", "projection", "includeContent", "includeStale", "includeBlockedOrUnverified", "projectLinkId"}, {"text"}),
    "cortex_read": ({"id", "projection", "includeStale", "includeBlockedOrUnverified", "projectLinkId", "revisionId"}, {"id"}),
    "cortex_project_brief": ({"projectLinkId", "knownContextSetId", "knownAuthoritySetHash", "maxUtf8Bytes"}, {"projectLinkId", "knownAuthoritySetHash"}),
    "cortex_read_retained_record": ({"projectLinkId", "projectId", "projectNamespace", "kind", "documentId", "revision", "contentHash"}, {"projectLinkId", "projectId", "projectNamespace", "kind", "documentId", "revision", "contentHash"}),
    "cortex_submit_task_snapshot": ({"projectLinkId", "projectNamespace", "assertion"}, {"projectLinkId", "projectNamespace", "assertion"}),
    "cortex_submit_work_packet": ({"projectLinkId", "projectNamespace", "assertion"}, {"projectLinkId", "projectNamespace", "assertion"}),
    "cortex_submit_work_packet_handoff_receipt": ({"projectLinkId", "projectId", "projectNamespace", "assertion"}, {"projectLinkId", "projectId", "projectNamespace", "assertion"}),
    "cortex_submit_work_packet_acceptance_receipt": ({"projectLinkId", "projectId", "projectNamespace", "assertion"}, {"projectLinkId", "projectId", "projectNamespace", "assertion"}),
    "cortex_submit_proposal": ({"projectLinkId", "idempotencyKey", "feedbackKind", "title", "summary", "content", "taxonomy", "keywords", "applicability", "evidenceReferences", "occurredAtUtc"}, {"projectLinkId", "idempotencyKey", "feedbackKind", "title", "summary", "content", "occurredAtUtc"}),
    "cortex_list_proposals": ({"projectLinkId", "afterProposalId", "limit", "includeContent"}, {"projectLinkId"}),
}
INSTRUCTION_MARKERS = (
    "cortex_project_brief once",
    "knownAuthoritySetHash",
    "authoritative project files",
    "cortex://default-library/guide",
    "mode broad",
)
RELEASE_PATTERN = re.compile(r"^1\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$")
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
RESERVED_WINDOWS_NAMES = {
    "CON", "PRN", "AUX", "NUL", *(f"COM{index}" for index in range(1, 10)), *(f"LPT{index}" for index in range(1, 10))
}
FILE_ATTRIBUTE_VOLUME_LABEL = 0x08
FILE_ATTRIBUTE_DIRECTORY = 0x10
FILE_ATTRIBUTE_REPARSE_POINT = 0x400
ALLOWED_ZIP_FLAG_BITS = 0x800  # UTF-8 entry names only.


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_object(value: bytes, name: str) -> dict[str, Any]:
    parsed = json.loads(value.decode("utf-8"))
    if not isinstance(parsed, dict):
        raise RuntimeError(f"{name} is not a JSON object.")
    return parsed


def _require_direct_regular(path: pathlib.Path, description: str) -> os.stat_result:
    try:
        file_stat = path.lstat()
    except FileNotFoundError as exception:
        raise RuntimeError(f"Required {description} is missing: {path}.") from exception
    if path.is_symlink() or _is_reparse(file_stat) or not stat.S_ISREG(file_stat.st_mode):
        raise RuntimeError(f"{description} must be one direct regular file: {path}.")
    return file_stat


def _is_reparse(file_stat: os.stat_result) -> bool:
    return bool(getattr(file_stat, "st_file_attributes", 0) & FILE_ATTRIBUTE_REPARSE_POINT)


def _require_regular_source(path: pathlib.Path, source_root: pathlib.Path) -> os.stat_result:
    try:
        file_stat = path.lstat()
    except FileNotFoundError as exception:
        raise RuntimeError(f"Required source input is missing: {path}.") from exception
    if path.is_symlink() or _is_reparse(file_stat) or not stat.S_ISREG(file_stat.st_mode):
        raise RuntimeError(f"Source input is not one direct regular file: {path}.")
    resolved_root = source_root.resolve(strict=True)
    resolved = path.resolve(strict=True)
    try:
        resolved.relative_to(resolved_root)
    except ValueError as exception:
        raise RuntimeError(f"Source input escaped the product root: {path}.") from exception
    current = path.parent
    while True:
        current_stat = current.lstat()
        if current.is_symlink() or _is_reparse(current_stat) or not stat.S_ISDIR(current_stat.st_mode):
            raise RuntimeError(f"Source input crosses a linked or non-directory ancestor: {current}.")
        if current == source_root:
            break
        if current.parent == current:
            raise RuntimeError(f"Source input has no product-root ancestor: {path}.")
        current = current.parent
    return file_stat


def _safe_tree_files(source_root: pathlib.Path, relative_root: str) -> Iterable[pathlib.Path]:
    start = source_root / relative_root
    start_stat = start.lstat()
    if start.is_symlink() or _is_reparse(start_stat) or not stat.S_ISDIR(start_stat.st_mode):
        raise RuntimeError(f"Bound source tree is unsafe: {relative_root}.")
    pending = [start]
    while pending:
        directory = pending.pop()
        with os.scandir(directory) as entries:
            for entry in entries:
                path = pathlib.Path(entry.path)
                item_stat = entry.stat(follow_symlinks=False)
                if entry.is_symlink() or _is_reparse(item_stat):
                    raise RuntimeError(f"Bound source tree contains a link or reparse point: {path}.")
                if stat.S_ISDIR(item_stat.st_mode):
                    if entry.name not in {"bin", "obj", "__pycache__"}:
                        pending.append(path)
                elif stat.S_ISREG(item_stat.st_mode) and path.suffix != ".pyc":
                    yield path
                elif not stat.S_ISREG(item_stat.st_mode):
                    raise RuntimeError(f"Bound source tree contains a non-regular entry: {path}.")


def expected_source_inputs(source_root: pathlib.Path) -> list[dict[str, Any]]:
    root_stat = source_root.lstat()
    if source_root.is_symlink() or _is_reparse(root_stat) or not stat.S_ISDIR(root_stat.st_mode):
        raise RuntimeError("Source root is not one direct directory.")
    relative_paths = set(SOURCE_FILES)
    for tree in SOURCE_TREES:
        for path in _safe_tree_files(source_root, tree):
            relative_paths.add(path.relative_to(source_root).as_posix())
    result: list[dict[str, Any]] = []
    for relative in sorted(relative_paths):
        path = source_root.joinpath(*pathlib.PurePosixPath(relative).parts)
        file_stat = _require_regular_source(path, source_root)
        result.append({"path": relative, "bytes": file_stat.st_size, "sha256": sha256_file(path)})
    return result


def source_set_sha256(inputs: list[dict[str, Any]]) -> str:
    ledger = "".join(f"{item['sha256']}  {item['bytes']}  {item['path']}\n" for item in inputs)
    return sha256_bytes(ledger.encode("utf-8"))


def expected_runtime_assets(deps: dict[str, Any], runtime_identifier: str) -> list[str]:
    """Reconstruct the exact packaged native subset from a RID-neutral deps graph."""
    targets = deps.get("targets")
    if not isinstance(targets, dict) or not targets:
        raise RuntimeError("RID-neutral dependency manifest has no targets object.")
    seen: set[str] = set()
    selected: list[str] = []
    for target in targets.values():
        if not isinstance(target, dict):
            raise RuntimeError("RID-neutral dependency target is not an object.")
        for library in target.values():
            if not isinstance(library, dict):
                raise RuntimeError("RID-neutral dependency library is not an object.")
            runtime_targets = library.get("runtimeTargets", {})
            if not isinstance(runtime_targets, dict):
                raise RuntimeError("RID-neutral runtimeTargets is not an object.")
            for relative, metadata in runtime_targets.items():
                if not isinstance(relative, str) or not isinstance(metadata, dict):
                    raise RuntimeError("RID-neutral runtime asset entry is invalid.")
                parts = pathlib.PurePosixPath(relative).parts
                rid = metadata.get("rid")
                asset_type = metadata.get("assetType")
                if (
                    "\\" in relative
                    or relative != "/".join(parts)
                    or len(parts) < 3
                    or parts[0] != "runtimes"
                    or not isinstance(rid, str)
                    or not rid
                    or parts[1] != rid
                    or not isinstance(asset_type, str)
                    or not asset_type
                    or any(
                        part in {"", ".", ".."}
                        or ":" in part
                        or part.endswith((".", " "))
                        or len(part.encode("utf-8")) > 240
                        for part in parts
                    )
                ):
                    raise RuntimeError(f"RID-neutral runtime asset is unsafe: {relative!r}.")
                folded = relative.casefold()
                if folded in seen:
                    raise RuntimeError(f"RID-neutral runtime asset is duplicated: {relative!r}.")
                seen.add(folded)
                if rid == runtime_identifier:
                    selected.append(f"app/{relative}")
    if not selected:
        raise RuntimeError(f"RID-neutral dependency graph has no {runtime_identifier!r} runtime assets.")
    return sorted(selected, key=str.casefold)


def expected_project_closure(source_root: pathlib.Path) -> list[dict[str, Any]]:
    pending = [source_root / PROJECT_ENTRY]
    seen: set[str] = set()
    while pending:
        project = pending.pop()
        _require_regular_source(project, source_root)
        relative = project.relative_to(source_root).as_posix()
        if relative.casefold() in {item.casefold() for item in seen}:
            if relative not in seen:
                raise RuntimeError("Project closure contains a case-insensitive collision.")
            continue
        seen.add(relative)
        document = element_tree.parse(project).getroot()
        for reference in document.findall(".//ProjectReference"):
            include = reference.get("Include")
            if not include:
                raise RuntimeError(f"ProjectReference has no Include in {relative}.")
            candidate = (project.parent / include.replace("\\", os.sep)).resolve(strict=True)
            try:
                candidate.relative_to(source_root.resolve(strict=True))
            except ValueError as exception:
                raise RuntimeError(f"ProjectReference escaped source root: {include}.") from exception
            pending.append(candidate)
    closure: list[dict[str, Any]] = []
    for relative in sorted(seen, key=str.casefold):
        project = source_root.joinpath(*pathlib.PurePosixPath(relative).parts)
        lock = project.parent / "packages.lock.json"
        _require_regular_source(lock, source_root)
        closure.append({
            "project": relative,
            "projectSha256": sha256_file(project),
            "lockFile": lock.relative_to(source_root).as_posix(),
            "lockSha256": sha256_file(lock),
        })
    return closure


def _utf16_units(value: str) -> int:
    return len(value.encode("utf-16-le")) // 2


def _validate_windows_parts(name: str) -> tuple[str, ...]:
    if not name or "\\" in name or name.startswith("/") or any(char < " " for char in name):
        raise RuntimeError(f"ZIP contains an unsafe name: {name!r}.")
    parts = pathlib.PurePosixPath(name.rstrip("/")).parts
    if not parts or pathlib.PurePosixPath(name).is_absolute():
        raise RuntimeError(f"ZIP contains an absolute or empty name: {name!r}.")
    raw_name = name[:-1] if name.endswith("/") else name
    if raw_name != "/".join(parts):
        raise RuntimeError(f"ZIP contains an aliased path component: {name!r}.")
    for part in parts:
        if (
            part in {"", ".", ".."}
            or ":" in part
            or part.endswith((".", " "))
            or len(part.encode("utf-8")) > 240
            or part.split(".", 1)[0].upper() in RESERVED_WINDOWS_NAMES
        ):
            raise RuntimeError(f"ZIP contains a Windows-unsafe segment: {name!r}.")
    return parts


def safe_zip_entries(
    archive: zipfile.ZipFile,
    destination_root: pathlib.Path,
) -> dict[str, zipfile.ZipInfo]:
    files: dict[str, zipfile.ZipInfo] = {}
    normalized_files: dict[str, str] = {}
    explicit_directories: set[str] = set()
    for entry in archive.infolist():
        name = entry.filename
        if name.endswith("//"):
            raise RuntimeError(f"ZIP contains an aliased directory name: {name!r}.")
        parts = _validate_windows_parts(name)
        normalized = "/".join(part.casefold() for part in parts)
        if entry.create_system not in {0, 3}:
            raise RuntimeError(f"ZIP contains an unsupported origin system: {name!r}.")
        if entry.flag_bits & ~ALLOWED_ZIP_FLAG_BITS:
            raise RuntimeError(f"ZIP contains encrypted or unsupported flags: {name!r}.")
        if entry.extra or entry.comment:
            raise RuntimeError(f"ZIP contains unsupported per-entry metadata: {name!r}.")
        if entry.compress_type not in {zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED}:
            raise RuntimeError(f"ZIP contains an unsupported compression method: {name!r}.")
        unix_mode = entry.external_attr >> 16 if entry.create_system == 3 else 0
        unix_kind = stat.S_IFMT(unix_mode)
        dos_attributes = entry.external_attr & 0xFFFF
        if dos_attributes & FILE_ATTRIBUTE_REPARSE_POINT:
            raise RuntimeError(f"ZIP entry carries a reparse attribute: {name!r}.")
        if entry.is_dir():
            if (
                entry.file_size != 0
                or unix_kind not in {0, stat.S_IFDIR}
                or dos_attributes & FILE_ATTRIBUTE_VOLUME_LABEL
            ):
                raise RuntimeError(f"ZIP directory metadata is invalid: {name!r}.")
            if normalized in explicit_directories or normalized in normalized_files:
                raise RuntimeError(f"ZIP contains a case-insensitive directory collision: {name!r}.")
            explicit_directories.add(normalized)
            continue
        if (
            unix_kind not in {0, stat.S_IFREG}
            or dos_attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_VOLUME_LABEL)
        ):
            raise RuntimeError(f"ZIP contains a symlink, device, or non-regular entry: {name!r}.")
        if normalized in normalized_files:
            raise RuntimeError(f"ZIP contains a case-insensitive or alias collision: {name!r}.")
        normalized_files[normalized] = name
        files[name] = entry
        final_path = os.path.abspath(destination_root.joinpath(*parts))
        if _utf16_units(final_path) > 240:
            raise RuntimeError(f"ZIP final extraction path exceeds 240 UTF-16 units: {name!r}.")

    file_keys = set(normalized_files)
    for key in file_keys:
        parts = key.split("/")
        for index in range(1, len(parts)):
            prefix = "/".join(parts[:index])
            if prefix in file_keys:
                raise RuntimeError("ZIP contains a file/directory prefix collision.")
    if explicit_directories & file_keys:
        raise RuntimeError("ZIP contains a file/directory identity collision.")
    for directory in explicit_directories:
        parts = directory.split("/")
        for index in range(1, len(parts)):
            if "/".join(parts[:index]) in file_keys:
                raise RuntimeError("ZIP contains a file/directory prefix collision.")
    return files


def verify_version_authorities(source_root: pathlib.Path, manifest: dict[str, Any]) -> str:
    version = json.loads((source_root / "VERSION.json").read_text(encoding="utf-8"))
    props_root = element_tree.parse(source_root / "Directory.Version.props").getroot()
    values = {child.tag: child.text or "" for group in props_root.findall("PropertyGroup") for child in group}
    release = version.get("releaseVersion")
    if (
        not isinstance(release, str)
        or not RELEASE_PATTERN.fullmatch(release)
        or manifest.get("releaseVersion") != release
        or manifest.get("packageVersion") != version.get("nuGetVersion")
        or values.get("PegasusReleaseVersion") != release
        or values.get("PegasusNuGetVersion") != version.get("nuGetVersion")
        or values.get("PegasusAssemblyVersion") != version.get("assemblyVersion")
        or values.get("PegasusFileVersion") != version.get("fileVersion")
    ):
        raise RuntimeError("VERSION.json, Directory.Version.props, and package manifest disagree.")
    return release


def verify_source_contract(source_root: pathlib.Path, manifest: dict[str, Any]) -> str:
    expected_sources = expected_source_inputs(source_root)
    if manifest.get("sourceInputs") != expected_sources:
        raise RuntimeError("Manifest sourceInputs omit, add, reorder, or alter independently reconstructed source.")
    expected_hash = source_set_sha256(expected_sources)
    if manifest.get("sourceSetSha256") != expected_hash:
        raise RuntimeError("The exact source-set ledger hash is invalid.")
    expected_closure = expected_project_closure(source_root)
    if manifest.get("dependencyLocks") != expected_closure:
        raise RuntimeError("Manifest dependencyLocks do not match the independently reconstructed project closure.")
    return expected_hash


def _tool_structured(response: dict[str, Any], identifier: int, error: bool) -> dict[str, Any]:
    if response.get("id") != identifier:
        raise RuntimeError(f"Missing MCP response {identifier}.")
    envelope = response.get("result")
    if not isinstance(envelope, dict) or envelope.get("isError", False) is not error:
        raise RuntimeError(f"MCP response {identifier} has the wrong tool envelope.")
    structured = envelope.get("structuredContent")
    if not isinstance(structured, dict):
        raise RuntimeError(f"MCP response {identifier} has no structured content.")
    return structured


def validate_tool_definitions(tools: Any) -> str:
    if not isinstance(tools, list) or {item.get("name") for item in tools if isinstance(item, dict)} != EXPECTED_TOOLS:
        raise RuntimeError("Offline stdio returned an unexpected local tool set.")
    for tool in tools:
        if not isinstance(tool, dict) or set(tool) != {"name", "title", "description", "inputSchema", "annotations"}:
            raise RuntimeError("A tool definition has an unexpected shape.")
        name = tool["name"]
        if not isinstance(tool["title"], str) or not tool["title"].strip() or not isinstance(tool["description"], str) or len(tool["description"].strip()) < 20:
            raise RuntimeError(f"Tool {name} has no bounded human guidance.")
        schema = tool["inputSchema"]
        properties, required = TOOL_ARGUMENTS[name]
        if (
            not isinstance(schema, dict)
            or schema.get("type") != "object"
            or schema.get("additionalProperties") is not False
            or set(schema.get("properties", {})) != properties
            or set(schema.get("required", [])) != required
        ):
            raise RuntimeError(f"Tool {name} input schema changed.")
        annotations = tool["annotations"]
        if annotations != {
            "title": tool["title"],
            "readOnlyHint": name not in WRITABLE_TOOLS,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        }:
            raise RuntimeError(f"Tool {name} annotations changed.")
    canonical = json.dumps(tools, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    return sha256_bytes(canonical)


def capture_local_contract(assembly: pathlib.Path, dotnet: pathlib.Path) -> dict[str, Any]:
    if os.name != "nt" or not assembly.is_file() or not dotnet.is_file():
        raise RuntimeError("Offline local contract capture requires Windows, dotnet, and an assembly.")
    with tempfile.TemporaryDirectory(prefix="cortex-x64-contract-") as temporary:
        root = pathlib.Path(temporary)
        profile = "package-smoke"
        state_root = root / profile
        project_root = root / "project"
        project_root.mkdir()
        project_link = "plink_" + "1" * 64
        provision = subprocess.run(
            [str(dotnet), str(assembly), "provision-project", "--profile", profile, "--state-root", str(state_root), "--tenant-id", "package-smoke-tenant", "--project-link-id", project_link, "--project-id", "package-smoke-project", "--project-namespace", "package-smoke-namespace", "--project-root", str(project_root), "--allow-read", "true", "--allow-write", "true"],
            stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=30, check=False,
        )
        if provision.returncode != 0:
            raise RuntimeError(f"Temporary local profile provisioning failed: {provision.stderr.strip()}")
        occurred_at = dt.datetime.now(dt.timezone.utc).isoformat(timespec="microseconds")
        unique_phrase = "x64-package-private-pending-knowledge-7f3d2a"
        requests = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "x64-package-verifier", "version": "1.0"}}},
            {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}},
            {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "cortex_discover", "arguments": {}}},
            {"jsonrpc": "2.0", "id": 4, "method": "resources/read", "params": {"uri": "cortex://default-library/guide"}},
            {"jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": {"name": "cortex_project_brief", "arguments": {"projectLinkId": project_link, "knownAuthoritySetHash": "0" * 64}}},
            {"jsonrpc": "2.0", "id": 6, "method": "tools/call", "params": {"name": "cortex_submit_proposal", "arguments": {"projectLinkId": project_link, "idempotencyKey": "package-smoke-proposal", "feedbackKind": "quality", "title": "Offline package behavior", "summary": "A safe private proposal remains pending.", "content": unique_phrase, "occurredAtUtc": occurred_at}}},
            {"jsonrpc": "2.0", "id": 7, "method": "tools/call", "params": {"name": "cortex_list_proposals", "arguments": {"projectLinkId": project_link, "limit": 3, "includeContent": True}}},
            {"jsonrpc": "2.0", "id": 8, "method": "tools/call", "params": {"name": "cortex_search", "arguments": {"text": unique_phrase, "mode": "exact", "limit": 3}}},
        ]
        result = subprocess.run(
            [str(dotnet), str(assembly), "stdio", "--profile", profile, "--state-root", str(state_root)],
            input="".join(json.dumps(item, separators=(",", ":")) + "\n" for item in requests),
            capture_output=True, text=True, timeout=30, check=False,
        )
        if result.returncode != 0:
            raise RuntimeError(f"Offline stdio contract capture failed: {result.stderr.strip()}")
        responses = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
        by_id = {item.get("id"): item for item in responses if isinstance(item, dict) and "id" in item}
        initialize = by_id.get(1, {}).get("result", {})
        instructions = initialize.get("instructions")
        if not isinstance(instructions, str) or len(instructions.encode("utf-8")) > 512 or any(marker not in instructions for marker in INSTRUCTION_MARKERS):
            raise RuntimeError("Initialize instructions exceed 512 bytes or omit the bounded workflow.")
        tools = by_id.get(2, {}).get("result", {}).get("tools")
        tool_hash = validate_tool_definitions(tools)
        discovery = _tool_structured(by_id.get(3, {}), 3, False)
        if discovery.get("storageKind") != "sqlite" or discovery.get("access", {}).get("network") is not False or discovery.get("access", {}).get("liveServices") is not False:
            raise RuntimeError("Offline discovery did not prove SQLite/no-network composition.")
        resource = by_id.get(4, {}).get("result", {}).get("contents", [])
        if not isinstance(resource, list) or len(resource) != 1 or resource[0].get("uri") != "cortex://default-library/guide":
            raise RuntimeError("Guide resource is unavailable.")
        guide_document = json.loads(resource[0].get("text", ""))
        guide_content = guide_document.get("content")
        guide_source = guide_document.get("source", {})
        if not isinstance(guide_content, str) or guide_source.get("sha256") != sha256_bytes(guide_content.encode("utf-8")) or guide_source.get("utf8Bytes") != len(guide_content.encode("utf-8")):
            raise RuntimeError("Guide resource content/hash/size is invalid.")
        brief = _tool_structured(by_id.get(5, {}), 5, True)
        if brief.get("accepted") is not False or brief.get("fallbackRequired") is not True or brief.get("fallbackReason") != "snapshot_not_available" or brief.get("currentTask") is not None:
            raise RuntimeError("Absent project state did not produce an authority-safe local fallback.")
        proposal = _tool_structured(by_id.get(6, {}), 6, False)
        if proposal.get("reviewState") != "pending" or proposal.get("searchable") is not False or proposal.get("decisionId") is not None:
            raise RuntimeError("Standalone safe proposal did not remain private and pending.")
        listed = _tool_structured(by_id.get(7, {}), 7, False)
        if listed.get("count") != 1 or listed.get("items", [{}])[0].get("content") != unique_phrase or listed.get("items", [{}])[0].get("searchable") is not False:
            raise RuntimeError("Private pending proposal was not retained exactly in its project scope.")
        searched = _tool_structured(by_id.get(8, {}), 8, False)
        if searched.get("count") != 0:
            raise RuntimeError("Private pending proposal leaked into general search.")
        return {
            "verified": True,
            "storageKind": "sqlite",
            "network": False,
            "liveServices": False,
            "instructionsUtf8Bytes": len(instructions.encode("utf-8")),
            "instructionsSha256": sha256_bytes(instructions.encode("utf-8")),
            "toolDefinitionsSha256": tool_hash,
            "guideResourceSha256": guide_source["sha256"],
            "guideVersion": guide_document.get("guideVersion"),
            "authorityFallbackVerified": True,
            "privatePendingProposalVerified": True,
        }


def validate_captured_contract(contract: Any, guide: dict[str, Any]) -> None:
    expected_keys = {
        "verified", "storageKind", "network", "liveServices",
        "instructionsUtf8Bytes", "instructionsSha256", "toolDefinitionsSha256",
        "guideResourceSha256", "guideVersion", "authorityFallbackVerified",
        "privatePendingProposalVerified",
    }
    if (
        not isinstance(contract, dict)
        or set(contract) != expected_keys
        or contract.get("verified") is not True
        or contract.get("storageKind") != "sqlite"
        or contract.get("network") is not False
        or contract.get("liveServices") is not False
        or not isinstance(contract.get("instructionsUtf8Bytes"), int)
        or not 1 <= contract["instructionsUtf8Bytes"] <= 512
        or not SHA256_PATTERN.fullmatch(str(contract.get("instructionsSha256", "")))
        or not SHA256_PATTERN.fullmatch(str(contract.get("toolDefinitionsSha256", "")))
        or contract.get("guideResourceSha256") != guide.get("sha256")
        or contract.get("guideVersion") != guide.get("version")
        or contract.get("authorityFallbackVerified") is not True
        or contract.get("privatePendingProposalVerified") is not True
    ):
        raise RuntimeError("Offline MCP contract evidence is incomplete or inconsistent.")


def verify_build_result(
    result_path: pathlib.Path,
    package_path: pathlib.Path,
    manifest: dict[str, Any],
    manifest_bytes: bytes,
    entry_count: int,
) -> str:
    if not result_path.is_file():
        raise RuntimeError("Required build-result.json is missing.")
    _require_direct_regular(result_path, "build result")
    result_bytes = result_path.read_bytes()
    result = load_object(result_bytes, "build-result.json")
    if (
        result.get("schemaVersion") != 1
        or result.get("releaseVersion") != manifest.get("releaseVersion")
        or result.get("runtimeIdentifier") != "win-x64"
        or result.get("sourceSetSha256") != manifest.get("sourceSetSha256")
        or result.get("manifestSha256") != sha256_bytes(manifest_bytes)
        or result.get("zipName") != package_path.name
        or result.get("zipBytes") != package_path.stat().st_size
        or result.get("zipSha256") != sha256_file(package_path)
        or result.get("deliveryFileCount") != entry_count
        or result.get("smokeRequired") is not True
        or result.get("verified") is not False
        or result.get("offlineStdioSmoke") is not False
    ):
        raise RuntimeError("build-result.json does not bind the exact unverified package bytes.")
    return sha256_bytes(result_bytes)


def verify_package(
    package_path: pathlib.Path,
    source_root: pathlib.Path,
    *,
    run_smoke: bool = True,
    dotnet_path: pathlib.Path | None = None,
    build_result_path: pathlib.Path | None = None,
) -> dict[str, Any]:
    if not package_path.is_file() or not source_root.is_dir():
        raise RuntimeError("Package and source root must exist.")
    _require_direct_regular(package_path, "package ZIP")
    if _utf16_units(os.path.abspath(package_path)) > 240:
        raise RuntimeError("Package ZIP path exceeds 240 UTF-16 units.")
    synthetic_root = pathlib.Path(tempfile.gettempdir()) / "cortex-x64-package-verification"
    with zipfile.ZipFile(package_path, "r") as archive:
        entries = safe_zip_entries(archive, synthetic_root)
        required = {"package-manifest.json", "checksums.sha256", "app/PegasusCortex.Mcp.Local.dll", "app/PegasusCortex.Mcp.Local.deps.json", "app/PegasusCortex.Mcp.Local.runtimeconfig.json", "content/continuity-guide.md"}
        if not required.issubset(entries):
            raise RuntimeError(f"Package is missing: {sorted(required - set(entries))}.")
        if any(name.lower().endswith((".exe", ".pdb")) for name in entries):
            raise RuntimeError("Portable package contains an app host or debug symbols.")
        for name in entries:
            if not name.startswith("app/"):
                continue
            relative = name.removeprefix("app/")
            if "/" not in relative:
                if pathlib.PurePosixPath(relative).suffix.lower() not in {".dll", ".json"}:
                    raise RuntimeError("win-x64 package contains an unexpected top-level app file.")
            elif not relative.startswith("runtimes/win-x64/"):
                raise RuntimeError("win-x64 package contains an unexpected app directory or foreign runtime asset.")
        deps = load_object(archive.read("app/PegasusCortex.Mcp.Local.deps.json"), "local deps.json")
        expected_runtime = expected_runtime_assets(deps, "win-x64")
        observed_runtime = sorted(
            (name for name in entries if name.startswith("app/runtimes/")),
            key=str.casefold,
        )
        if observed_runtime != expected_runtime:
            raise RuntimeError("win-x64 package runtime assets differ from the RID-neutral dependency graph.")
        manifest_bytes = archive.read("package-manifest.json")
        manifest = load_object(manifest_bytes, "package-manifest.json")
        if (
            manifest.get("schemaVersion") != 1
            or manifest.get("runtimeIdentifier") != "win-x64"
            or manifest.get("transport") != "stdio"
            or manifest.get("storageKind") != "sqlite"
            or manifest.get("network") is not False
            or manifest.get("liveServices") is not False
            or manifest.get("serviceRegistration") is not False
            or manifest.get("entryAssembly") != "app/PegasusCortex.Mcp.Local.dll"
            or set(manifest.get("toolSurface", [])) != EXPECTED_TOOLS
        ):
            raise RuntimeError("Package header or local MCP tool surface is invalid.")
        if manifest.get("publishContract") != {"configuration": "Release", "frameworkDependent": True, "portable": True, "useAppHost": False, "singleFile": False, "readyToRun": False, "nativeAot": False, "trimmed": False, "debugSymbols": False}:
            raise RuntimeError("Portable publish contract is invalid.")
        verify_version_authorities(source_root, manifest)
        expected_source_hash = verify_source_contract(source_root, manifest)
        global_sdk = json.loads((source_root / "global.json").read_text(encoding="utf-8")).get("sdk", {})
        sdk = manifest.get("sdk")
        locked_sdk = global_sdk.get("version")
        if (
            not isinstance(locked_sdk, str)
            or not re.fullmatch(r"\d+\.\d+\.\d+", locked_sdk)
            or global_sdk.get("rollForward") != "disable"
            or global_sdk.get("allowPrerelease") is not False
            or not isinstance(sdk, dict)
            or sdk.get("version") != locked_sdk
            or sdk.get("actualVersion") != locked_sdk
            or sdk.get("rollForward") != "disable"
            or sdk.get("allowPrerelease") is not False
            or sdk.get("globalJsonSha256") != sha256_file(source_root / "global.json")
        ):
            raise RuntimeError("Locked SDK evidence is invalid.")

        payload = manifest.get("payload")
        if not isinstance(payload, list) or not payload:
            raise RuntimeError("Payload inventory is absent.")
        payload_names: set[str] = set()
        for item in payload:
            if not isinstance(item, dict) or not SHA256_PATTERN.fullmatch(str(item.get("sha256", ""))):
                raise RuntimeError("Payload inventory item is invalid.")
            name = item.get("path")
            if not isinstance(name, str) or name in payload_names or name not in entries:
                raise RuntimeError("Payload inventory path is absent or duplicated.")
            payload_names.add(name)
            data = archive.read(name)
            if len(data) != item.get("bytes") or sha256_bytes(data) != item.get("sha256"):
                raise RuntimeError(f"Payload bytes do not match inventory: {name}.")
        if payload_names != set(entries) - {"package-manifest.json", "checksums.sha256"}:
            raise RuntimeError("Payload inventory is not complete.")
        checksum_lines = archive.read("checksums.sha256").decode("utf-8").splitlines()
        expected_checksums = {"package-manifest.json": sha256_bytes(manifest_bytes), **{item["path"]: item["sha256"] for item in payload}}
        parsed_checksums: dict[str, str] = {}
        for line in checksum_lines:
            match = re.fullmatch(r"([0-9a-f]{64})  (.+)", line)
            if not match or match.group(2) in parsed_checksums:
                raise RuntimeError("checksums.sha256 is malformed or duplicated.")
            parsed_checksums[match.group(2)] = match.group(1)
        if parsed_checksums != expected_checksums:
            raise RuntimeError("checksums.sha256 does not bind the exact package payload.")

        guide = manifest.get("guide")
        guide_bytes = archive.read("content/continuity-guide.md")
        guide_source = source_root / "src/PegasusCortex.Mcp/Knowledge/continuity-guide.md"
        if not isinstance(guide, dict) or guide.get("path") != "content/continuity-guide.md" or guide.get("resourceUri") != "cortex://default-library/guide" or guide.get("sha256") != sha256_bytes(guide_bytes) or guide_bytes != guide_source.read_bytes():
            raise RuntimeError("Bundled guide does not match its source and manifest.")
        expected_schema_paths = sorted(path.relative_to(source_root).as_posix() for path in _safe_tree_files(source_root, "schemas") if path.name.endswith(".schema.json") and path.parent == source_root / "schemas")
        expected_schemas = [{"path": f"content/schemas/{pathlib.PurePosixPath(path).name}", "sha256": sha256_file(source_root / path), "bytes": (source_root / path).stat().st_size} for path in expected_schema_paths]
        if manifest.get("schemas") != expected_schemas:
            raise RuntimeError("Manifest schema set differs from independently reconstructed schemas.")
        for item in expected_schemas:
            name = item["path"]
            if name not in entries:
                raise RuntimeError(f"Bundled schema is missing: {name}.")
            data = archive.read(name)
            json.loads(data.decode("utf-8"))
            if sha256_bytes(data) != item["sha256"] or len(data) != item["bytes"]:
                raise RuntimeError(f"Bundled schema drifted: {name}.")

        result_path = build_result_path or package_path.parent / "build-result.json"
        build_result_sha256 = verify_build_result(result_path, package_path, manifest, manifest_bytes, len(entries))
        contract = manifest.get("mcpContract")
        validate_captured_contract(contract, guide)
        if run_smoke:
            dotnet = dotnet_path or pathlib.Path(r"C:\Program Files\dotnet\dotnet.exe")
            with tempfile.TemporaryDirectory(prefix="cortex-x64-extract-") as temporary:
                extract_root = pathlib.Path(temporary)
                safe_zip_entries(archive, extract_root)
                for name, entry in entries.items():
                    destination = extract_root.joinpath(*pathlib.PurePosixPath(name).parts)
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    destination.write_bytes(archive.read(entry))
                observed_contract = capture_local_contract(extract_root / "app/PegasusCortex.Mcp.Local.dll", dotnet)
            if observed_contract != contract:
                raise RuntimeError("Packaged offline MCP behavior differs from the build-time contract capture.")
            if observed_contract.get("guideResourceSha256") != guide.get("sha256") or observed_contract.get("guideVersion") != guide.get("version"):
                raise RuntimeError("Runtime guide resource differs from bundled guide evidence.")
        manifest["_verification"] = {
            "sourceSetSha256": expected_source_hash,
            "manifestSha256": sha256_bytes(manifest_bytes),
            "buildResultSha256": build_result_sha256,
        }
        return manifest


_verify_historical_package = verify_package


def verify_package(
    package_path: pathlib.Path,
    source_root: pathlib.Path,
    *,
    run_smoke: bool,
    dotnet_path: pathlib.Path | None = None,
    build_result_path: pathlib.Path | None = None,
) -> dict[str, Any]:
    """Reject use of the superseded DLL/profile package validator."""

    del package_path, source_root, run_smoke, dotnet_path, build_result_path
    raise RuntimeError(RETIREMENT_MESSAGE)


def _historical_main() -> int:
    """Preserved CLI implementation for archaeology; not a public entry point."""

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", nargs="?", type=pathlib.Path)
    parser.add_argument("--source-root", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[1])
    parser.add_argument("--dotnet", type=pathlib.Path, default=pathlib.Path(r"C:\Program Files\dotnet\dotnet.exe"))
    parser.add_argument("--build-result", type=pathlib.Path)
    parser.add_argument("--no-smoke", action="store_true", help="Perform only static cross-host verification.")
    parser.add_argument("--capture-contract", type=pathlib.Path, help="Capture one staged DLL's disposable offline contract.")
    args = parser.parse_args()
    if args.capture_contract is not None:
        if args.package is not None or args.no_smoke or args.build_result is not None:
            parser.error("--capture-contract is an isolated mode and accepts only --dotnet.")
        print(json.dumps(capture_local_contract(args.capture_contract.resolve(), args.dotnet.resolve()), separators=(",", ":")))
        return 0
    if args.package is None:
        parser.error("package is required unless --capture-contract is used.")
    package = args.package.resolve()
    manifest = _verify_historical_package(package, args.source_root.resolve(), run_smoke=not args.no_smoke, dotnet_path=args.dotnet.resolve(), build_result_path=args.build_result.resolve() if args.build_result else None)
    verification = manifest.pop("_verification")
    print(json.dumps({
        "schemaVersion": 1,
        "verified": True,
        "offlineStdioSmoke": not args.no_smoke,
        "releaseVersion": manifest["releaseVersion"],
        "runtimeIdentifier": manifest["runtimeIdentifier"],
        "sourceSetSha256": verification["sourceSetSha256"],
        "manifestSha256": verification["manifestSha256"],
        "packageSha256": sha256_file(package),
        "packageBytes": package.stat().st_size,
        "packageName": package.name,
        "buildResultSha256": verification["buildResultSha256"],
        "mcpContract": manifest["mcpContract"],
    }, separators=(",", ":")))
    return 0


def main() -> int:
    print(RETIREMENT_MESSAGE, file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
