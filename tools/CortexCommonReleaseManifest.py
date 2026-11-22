#!/usr/bin/env python3
"""Create and verify one deterministic x64/ARM64 Cortex release evidence manifest.

The manifest is a compact index over already-created evidence. It does not
build, package, execute, deploy, or contact a service. Every referenced input
must be a regular project-contained file, and acceptance is derived from the
sealed exact-source execution receipt plus both package verifiers.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as element_tree
from typing import Any

import CortexVerificationManifest as verification


CONTRACT_ID = "pegasus.cortex.common-release-manifest/1.0"
SDK_VERSION = "10.0.400"
SDK_ROLL_FORWARD = "latestPatch"
MAX_JSON_BYTES = 16 * 1024 * 1024
RELEASE_VERSION = re.compile(r"^1\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$")
SHA256 = re.compile(r"^[0-9a-f]{64}$")
SHA512 = re.compile(r"^[0-9a-f]{128}$")


class CommonReleaseError(verification.ManifestError):
    """Raised when common-release evidence fails closed."""


def _valid_sdk(value: Any) -> bool:
    if not isinstance(value, dict):
        return False
    actual = value.get("actualVersion")
    if not isinstance(actual, str) or re.fullmatch(r"10\.0\.4\d{2}", actual) is None:
        return False
    return (
        value.get("version") == SDK_VERSION
        and value.get("rollForward") == SDK_ROLL_FORWARD
        and value.get("allowPrerelease") is False
        and tuple(map(int, actual.split("."))) >= tuple(map(int, SDK_VERSION.split(".")))
    )


def _sha512_file(path: pathlib.Path) -> str:
    digest = hashlib.sha512()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _sha(value: Any, field: str, *, bits: int = 256) -> str:
    pattern = SHA256 if bits == 256 else SHA512
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise CommonReleaseError(f"{field} must be a lowercase SHA-{bits} value.")
    return value


def _string(value: Any, field: str, *, maximum: int = 512) -> str:
    if not isinstance(value, str):
        raise CommonReleaseError(f"{field} must be a string.")
    return verification._screen_public_text(value, field, maximum)


def _integer(value: Any, field: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise CommonReleaseError(f"{field} must be a non-negative integer.")
    return value


def _object(value: Any, field: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise CommonReleaseError(f"{field} must be an object.")
    return value


def _reject_link_count(path: pathlib.Path, field: str) -> None:
    """Reject hard-linked inputs in addition to symlinks/reparse points."""

    if path.stat(follow_symlinks=False).st_nlink != 1:
        raise CommonReleaseError(f"{field} must not be a hard-linked file.")


def _read_json(root: pathlib.Path, relative: str, field: str) -> tuple[dict[str, Any], dict[str, Any]]:
    path, normalized = verification.resolve_project_path(
        root, relative, must_exist=True, require_file=True
    )
    _reject_link_count(path, field)
    value, data = verification._read_bounded_json_payload(
        path, maximum_bytes=MAX_JSON_BYTES, field=field
    )
    _reject_link_count(path, field)
    if not isinstance(value, dict):
        raise CommonReleaseError(f"{field} must be a JSON object.")
    return value, {"bytes": len(data), "path": normalized, "sha256": verification.sha256_bytes(data)}


def _file_record(root: pathlib.Path, relative: str, field: str) -> dict[str, Any]:
    path, _ = verification.resolve_project_path(root, relative, must_exist=True, require_file=True)
    _reject_link_count(path, field)
    try:
        return verification._stable_file_record(root, path)
    except verification.ManifestError as error:
        raise CommonReleaseError(f"{field} is not stable: {error}") from error


def _version_authority(root: pathlib.Path, release_version: str) -> dict[str, Any]:
    if RELEASE_VERSION.fullmatch(release_version) is None:
        raise CommonReleaseError("releaseVersion is invalid.")
    version, version_record = _read_json(root, "VERSION.json", "VERSION.json")
    if version.get("releaseVersion") != release_version:
        raise CommonReleaseError("VERSION.json does not match releaseVersion.")
    props_path, _ = verification.resolve_project_path(
        root, "Directory.Version.props", must_exist=True, require_file=True
    )
    try:
        props = element_tree.fromstring(props_path.read_bytes())
    except element_tree.ParseError as error:
        raise CommonReleaseError("Directory.Version.props is invalid XML.") from error
    values = {child.tag: child.text for group in props.findall("PropertyGroup") for child in group}
    expected = {
        "PegasusReleaseVersion": release_version,
        "PegasusNuGetVersion": version.get("nuGetVersion"),
        "PegasusAssemblyVersion": version.get("assemblyVersion"),
        "PegasusFileVersion": version.get("fileVersion"),
    }
    if any(values.get(key) != value for key, value in expected.items()):
        raise CommonReleaseError("VERSION.json and Directory.Version.props do not identify the same release.")
    return {
        "directoryVersionProps": _file_record(root, "Directory.Version.props", "Directory.Version.props"),
        "versionJson": version_record,
    }


def _source_index(bundle: dict[str, Any]) -> dict[str, tuple[int, str]]:
    files = bundle["source"].get("files")
    if not isinstance(files, list):
        raise CommonReleaseError("The verified tested-source inventory is unavailable.")
    return {
        item["path"]: (item["bytes"], item["sha256"])
        for item in files
        if isinstance(item, dict)
        and isinstance(item.get("path"), str)
        and isinstance(item.get("bytes"), int)
        and isinstance(item.get("sha256"), str)
    }


def _validate_source_inputs(
    value: Any,
    inventory: dict[str, tuple[int, str]],
    field: str,
) -> tuple[list[dict[str, Any]], str]:
    if not isinstance(value, list) or not value:
        raise CommonReleaseError(f"{field} must contain source inputs.")
    records: list[dict[str, Any]] = []
    seen: set[str] = set()
    for index, item_value in enumerate(value):
        item = _object(item_value, f"{field}[{index}]")
        if set(item) != {"bytes", "path", "sha256"}:
            raise CommonReleaseError(f"{field}[{index}] has an unsupported shape.")
        path = _string(item.get("path"), f"{field}[{index}].path", maximum=1024)
        normalized = verification._normalized_relative(path, allow_root=False)
        size = _integer(item.get("bytes"), f"{field}[{index}].bytes")
        digest = _sha(item.get("sha256"), f"{field}[{index}].sha256")
        if normalized in seen:
            raise CommonReleaseError(f"{field} contains a duplicate path.")
        seen.add(normalized)
        if inventory.get(normalized) != (size, digest):
            raise CommonReleaseError(f"{field} is not bound to the tested source inventory: {normalized}")
        records.append({"bytes": size, "path": normalized, "sha256": digest})
    if records != sorted(records, key=lambda item: item["path"]):
        raise CommonReleaseError(f"{field} must be deterministically path-sorted.")
    ledger = "".join(f"{item['sha256']}  {item['bytes']}  {item['path']}\n" for item in records)
    return records, verification.sha256_bytes(ledger.encode("utf-8"))


def _summarize_receipt(receipt: dict[str, Any], record: dict[str, Any]) -> dict[str, Any]:
    commands = receipt["commands"]
    skips = [
        {"commandId": command["id"], "name": detail["name"], "reason": detail["reason"]}
        for command in commands
        for detail in command["skipDetails"]
    ]
    checks = {
            key: sum(command["checks"][key] for command in commands)
            for key in ("failed", "passed", "skipped")
        }
    return {
        "accepted": bool(receipt["accepted"] and checks["failed"] == 0),
        "checks": checks,
        "namedSkips": skips,
        "receiptAccepted": receipt["accepted"],
        "record": record,
        "sealPayloadSha256": _sha(receipt["seal"]["payloadSha256"], "execution receipt seal"),
    }


def _x64_evidence(
    root: pathlib.Path,
    release_version: str,
    source_inventory: dict[str, tuple[int, str]],
    build_path: str,
    manifest_path: str,
    archive_path: str,
    verifier_path: str,
) -> dict[str, Any]:
    build, build_record = _read_json(root, build_path, "x64 build result")
    manifest, manifest_record = _read_json(root, manifest_path, "x64 package manifest")
    verifier_result, verifier_record = _read_json(root, verifier_path, "x64 verifier result")
    archive_record = _file_record(root, archive_path, "x64 package archive")
    if build.get("schemaVersion") != 1 or manifest.get("schemaVersion") != 1:
        raise CommonReleaseError("The x64 package evidence schema is unsupported.")
    if any(item.get("releaseVersion") != release_version for item in (build, manifest, verifier_result)):
        raise CommonReleaseError("The x64 package evidence release does not match.")
    if any(item.get("runtimeIdentifier") != "win-x64" for item in (build, manifest, verifier_result)):
        raise CommonReleaseError("The x64 package evidence runtimeIdentifier does not match.")
    source_inputs, source_set = _validate_source_inputs(
        manifest.get("sourceInputs"), source_inventory, "x64 package sourceInputs"
    )
    if manifest.get("sourceSetSha256") != source_set or build.get("sourceSetSha256") != source_set:
        raise CommonReleaseError("The x64 source-set hash does not match its tested-source-bound ledger.")
    if not _valid_sdk(manifest.get("sdk")):
        raise CommonReleaseError("The x64 package does not bind the accepted .NET SDK policy.")
    expected_archive_name = pathlib.PurePosixPath(archive_record["path"]).name
    if (
        build.get("manifestSha256") != manifest_record["sha256"]
        or build.get("zipName") != expected_archive_name
        or build.get("zipBytes") != archive_record["bytes"]
        or build.get("zipSha256") != archive_record["sha256"]
        or build.get("smokeRequired") is not True
        or build.get("verified") is not False
        or build.get("offlineStdioSmoke") is not False
        or verifier_result.get("schemaVersion") != 1
        or verifier_result.get("verified") is not True
        or verifier_result.get("offlineStdioSmoke") is not True
        or verifier_result.get("packageSha256") != archive_record["sha256"]
        or verifier_result.get("packageBytes") != archive_record["bytes"]
        or verifier_result.get("packageName") != expected_archive_name
        or verifier_result.get("manifestSha256") != manifest_record["sha256"]
        or verifier_result.get("sourceSetSha256") != source_set
        or verifier_result.get("buildResultSha256") != build_record["sha256"]
        or verifier_result.get("mcpContract") != manifest.get("mcpContract")
    ):
        raise CommonReleaseError("The x64 build, archive, manifest, or verifier evidence does not agree.")
    return {
        "accepted": True,
        "archive": archive_record,
        "buildResult": build_record,
        "manifest": manifest_record,
        "runtimeIdentifier": "win-x64",
        "sourceInputCount": len(source_inputs),
        "sourceSetSha256": source_set,
        "verifierResult": verifier_record,
    }


def _arm_evidence(
    root: pathlib.Path,
    release_version: str,
    runtime_version: str,
    source_inventory: dict[str, tuple[int, str]],
    build_path: str,
    manifest_path: str,
    archive_path: str,
    verifier_path: str,
    runtime_archive_path: str,
) -> tuple[dict[str, Any], dict[str, Any]]:
    build, build_record = _read_json(root, build_path, "ARM64 build result")
    manifest, manifest_record = _read_json(root, manifest_path, "ARM64 package manifest")
    verifier_result, verifier_record = _read_json(root, verifier_path, "ARM64 verifier result")
    archive_record = _file_record(root, archive_path, "ARM64 package archive")
    runtime_record = _file_record(root, runtime_archive_path, "ARM64 runtime archive")
    runtime_path, _ = verification.resolve_project_path(
        root, runtime_archive_path, must_exist=True, require_file=True
    )
    runtime_record["sha512"] = _sha512_file(runtime_path)
    if build.get("schemaVersion") != 1 or manifest.get("schemaVersion") != 3:
        raise CommonReleaseError("The ARM64 package evidence schema is unsupported.")
    if any(item.get("releaseVersion") != release_version for item in (build, manifest, verifier_result)):
        raise CommonReleaseError("The ARM64 package evidence release does not match.")
    if any(item.get("runtimeIdentifier") != "linux-arm64" for item in (build, manifest, verifier_result)):
        raise CommonReleaseError("The ARM64 package evidence runtimeIdentifier does not match.")
    source_inputs, source_set = _validate_source_inputs(
        manifest.get("sourceInputs"), source_inventory, "ARM64 package sourceInputs"
    )
    runtime = _object(manifest.get("runtime"), "ARM64 package runtime")
    if (
        runtime_version != manifest.get("runtimeVersion")
        or runtime.get("archiveName") != pathlib.PurePosixPath(runtime_record["path"]).name
        or runtime.get("sha256") != runtime_record["sha256"]
        or runtime.get("sha512") != runtime_record["sha512"]
        or not _valid_sdk(manifest.get("sdk"))
    ):
        raise CommonReleaseError("The ARM64 runtime or accepted SDK evidence does not match.")
    if (
        build.get("status") != "built"
        or build.get("packageArchiveBytes") != archive_record["bytes"]
        or build.get("packageArchiveSha256") != archive_record["sha256"]
        or build.get("packageManifestSha256") != manifest_record["sha256"]
        or verifier_result.get("status") != "pass"
        or verifier_result.get("packageArchiveSha256") != archive_record["sha256"]
        or verifier_result.get("packageManifestSha256") != manifest_record["sha256"]
        or verifier_result.get("aarch64ElfVerified") is not True
        or verifier_result.get("safeExtractionVerified") is not True
        or verifier_result.get("portableIntegratedFailClosed") is not True
    ):
        raise CommonReleaseError("The ARM64 build, archive, manifest, or verifier evidence does not agree.")
    return (
        {
            "accepted": True,
            "archive": archive_record,
            "buildResult": build_record,
            "manifest": manifest_record,
            "runtimeIdentifier": "linux-arm64",
            "sourceInputCount": len(source_inputs),
            "sourceSetSha256": source_set,
            "verifierResult": verifier_record,
        },
        {"archive": runtime_record, "version": runtime_version},
    )


def build_manifest(
    root: pathlib.Path,
    *,
    release_version: str,
    tested_source_bundle: str,
    execution_receipt: str,
    x64_build_result: str,
    x64_manifest: str,
    x64_archive: str,
    x64_verifier_result: str,
    arm_build_result: str,
    arm_manifest: str,
    arm_archive: str,
    arm_verifier_result: str,
    arm_runtime_version: str,
    arm_runtime_archive: str,
) -> dict[str, Any]:
    root = root.resolve(strict=True)
    authority = _version_authority(root, release_version)
    bundle_path, _ = verification.resolve_project_path(
        root, tested_source_bundle, must_exist=True, require_file=True
    )
    receipt_path, _ = verification.resolve_project_path(
        root, execution_receipt, must_exist=True, require_file=True
    )
    _reject_link_count(bundle_path, "tested-source bundle")
    _reject_link_count(receipt_path, "execution receipt")
    bundle = verification.verify_source_bundle(root, tested_source_bundle)
    receipt, receipt_record = verification.load_execution_receipt(root, execution_receipt, bundle)
    inventory = _source_index(bundle)
    for field, record in authority.items():
        if inventory.get(record["path"]) != (record["bytes"], record["sha256"]):
            raise CommonReleaseError(
                f"{field} is not the version-authority file retained in the tested source bundle."
            )
    x64 = _x64_evidence(
        root, release_version, inventory, x64_build_result, x64_manifest,
        x64_archive, x64_verifier_result
    )
    arm, arm_runtime = _arm_evidence(
        root, release_version, arm_runtime_version, inventory, arm_build_result,
        arm_manifest, arm_archive, arm_verifier_result, arm_runtime_archive
    )
    execution = _summarize_receipt(receipt, receipt_record)
    payload: dict[str, Any] = {
        "accepted": bool(execution["accepted"] and x64["accepted"] and arm["accepted"]),
        "contractId": CONTRACT_ID,
        "execution": execution,
        "platforms": {"linuxArm64": arm, "windowsX64": x64},
        "releaseVersion": release_version,
        "sourceAuthority": authority,
        "testedSource": {
            "bundle": bundle["bundle"],
            "sourceInventorySha256": bundle["source"]["sha256"],
        },
        "toolchain": {"armRuntime": arm_runtime, "dotnetSdk": SDK_VERSION},
    }
    payload["seal"] = {
        "algorithm": "sha256",
        "canonicalization": "sorted-key-compact-json-utf8",
        "payloadSha256": verification.sha256_bytes(verification.canonical_json_bytes(payload)),
    }
    return payload


def verify_manifest(root: pathlib.Path, manifest_path: str) -> dict[str, Any]:
    resolved_root = root.resolve(strict=True)
    path, _ = verification.resolve_project_path(
        resolved_root, manifest_path, must_exist=True, require_file=True
    )
    _reject_link_count(path, "common-release manifest")
    manifest_value, manifest_bytes = verification._read_bounded_json_payload(
        path, maximum_bytes=MAX_JSON_BYTES, field="common-release manifest"
    )
    if not isinstance(manifest_value, dict):
        raise CommonReleaseError("The common-release manifest must be a JSON object.")
    manifest = manifest_value
    if manifest_bytes != verification.formatted_manifest_bytes(manifest):
        raise CommonReleaseError("The common-release manifest is not canonically encoded.")
    if set(manifest) != {
        "accepted", "contractId", "execution", "platforms", "releaseVersion",
        "seal", "sourceAuthority", "testedSource", "toolchain"
    } or manifest.get("contractId") != CONTRACT_ID:
        raise CommonReleaseError("The common-release manifest contract is unsupported.")
    seal = _object(manifest.get("seal"), "common-release manifest seal")
    payload = dict(manifest)
    del payload["seal"]
    if (
        seal.get("algorithm") != "sha256"
        or seal.get("canonicalization") != "sorted-key-compact-json-utf8"
        or seal.get("payloadSha256") != verification.sha256_bytes(verification.canonical_json_bytes(payload))
    ):
        raise CommonReleaseError("The common-release manifest seal does not match.")
    platforms = _object(manifest.get("platforms"), "platforms")
    tested = _object(manifest.get("testedSource"), "testedSource")
    execution = _object(manifest.get("execution"), "execution")
    toolchain = _object(manifest.get("toolchain"), "toolchain")
    arm_runtime = _object(toolchain.get("armRuntime"), "toolchain.armRuntime")
    rebuilt = build_manifest(
        resolved_root,
        release_version=_string(manifest.get("releaseVersion"), "releaseVersion"),
        tested_source_bundle=_object(tested.get("bundle"), "testedSource.bundle")["path"],
        execution_receipt=_object(execution.get("record"), "execution.record")["path"],
        x64_build_result=_object(platforms["windowsX64"]["buildResult"], "x64 buildResult")["path"],
        x64_manifest=_object(platforms["windowsX64"]["manifest"], "x64 manifest")["path"],
        x64_archive=_object(platforms["windowsX64"]["archive"], "x64 archive")["path"],
        x64_verifier_result=_object(platforms["windowsX64"]["verifierResult"], "x64 verifierResult")["path"],
        arm_build_result=_object(platforms["linuxArm64"]["buildResult"], "ARM64 buildResult")["path"],
        arm_manifest=_object(platforms["linuxArm64"]["manifest"], "ARM64 manifest")["path"],
        arm_archive=_object(platforms["linuxArm64"]["archive"], "ARM64 archive")["path"],
        arm_verifier_result=_object(platforms["linuxArm64"]["verifierResult"], "ARM64 verifierResult")["path"],
        arm_runtime_version=_string(arm_runtime.get("version"), "ARM runtime version"),
        arm_runtime_archive=_object(arm_runtime.get("archive"), "ARM runtime archive")["path"],
    )
    if manifest != rebuilt:
        raise CommonReleaseError("The common-release manifest no longer matches its evidence.")
    return manifest


def _add_inputs(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--root", type=pathlib.Path, required=True)
    parser.add_argument("--release-version", required=True)
    parser.add_argument("--tested-source-bundle", required=True)
    parser.add_argument("--execution-receipt", required=True)
    parser.add_argument("--x64-build-result", required=True)
    parser.add_argument("--x64-manifest", required=True)
    parser.add_argument("--x64-archive", required=True)
    parser.add_argument("--x64-verifier-result", required=True)
    parser.add_argument("--arm-build-result", required=True)
    parser.add_argument("--arm-manifest", required=True)
    parser.add_argument("--arm-archive", required=True)
    parser.add_argument("--arm-verifier-result", required=True)
    parser.add_argument("--arm-runtime-version", required=True)
    parser.add_argument("--arm-runtime-archive", required=True)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="operation", required=True)
    create = commands.add_parser("create", help="Create one new sealed manifest.")
    _add_inputs(create)
    create.add_argument("--output", required=True)
    verify = commands.add_parser("verify", help="Revalidate one existing sealed manifest.")
    verify.add_argument("--root", type=pathlib.Path, required=True)
    verify.add_argument("--manifest", required=True)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        root = args.root.resolve(strict=True)
        if args.operation == "verify":
            result = verify_manifest(root, args.manifest)
        else:
            result = build_manifest(
                root,
                release_version=args.release_version,
                tested_source_bundle=args.tested_source_bundle,
                execution_receipt=args.execution_receipt,
                x64_build_result=args.x64_build_result,
                x64_manifest=args.x64_manifest,
                x64_archive=args.x64_archive,
                x64_verifier_result=args.x64_verifier_result,
                arm_build_result=args.arm_build_result,
                arm_manifest=args.arm_manifest,
                arm_archive=args.arm_archive,
                arm_verifier_result=args.arm_verifier_result,
                arm_runtime_version=args.arm_runtime_version,
                arm_runtime_archive=args.arm_runtime_archive,
            )
            output, _ = verification.resolve_project_path(
                root, args.output, must_exist=False
            )
            verification.write_manifest(output, result)
        print(json.dumps({
            "accepted": result["accepted"],
            "manifestPayloadSha256": result["seal"]["payloadSha256"],
            "releaseVersion": result["releaseVersion"],
        }, sort_keys=True, separators=(",", ":")))
        return 0
    except (CommonReleaseError, verification.ManifestError, KeyError) as error:
        print(f"Common release manifest failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
