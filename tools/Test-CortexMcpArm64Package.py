#!/usr/bin/env python3
"""Independent local verification for the Cortex MCP ARM64 delivery."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import stat
import subprocess
import sys
import tarfile
import tempfile
import xml.etree.ElementTree as element_tree
from typing import Any

sys.dont_write_bytecode = True


EXPECTED_OUTER_NAMES = {
    "checksums.sha256",
    "package-manifest.json",
    "install_cortex_mcp_arm64.py",
    "smoke_cortex_mcp.py",
    "run-cortex-mcp",
    "run-cortex-migrate",
}
EXPECTED_PACKAGE_TOOL_ASSETS = {
    "install_cortex_mcp_arm64.py",
    "smoke_cortex_mcp.py",
    "run-cortex-mcp",
    "run-cortex-migrate",
}

EXPECTED_MAIN_TOOLS = {
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
    "cortex_correlate_project",
    "cortex_submit_proposal",
    "cortex_list_proposals",
    "cortex_change_publication",
    "cortex_review_proposal",
    "cortex_manage_review_grant",
}
EXPECTED_AUDITOR_TOOLS = {
    "cortex_discover",
    "cortex_status",
    "cortex_list_proposals",
    "cortex_submit_audit",
}
EXPECTED_INITIALIZE_INSTRUCTION_MARKERS = (
    "cortex://default-library/guide",
    "authoritative project files",
    "mode broad",
)
EXPECTED_ACCESS_BY_SURFACE = {
    "main": {
        "network": True,
        "database": True,
        "authentication": True,
        "writes": True,
        "liveServices": True,
        "projectContinuityRuntime": True,
    },
    "auditor": {
        "network": True,
        "database": True,
        "authentication": True,
        "writes": True,
        "liveServices": True,
        "projectContinuityRuntime": False,
    },
}
EXPECTED_STORAGE_KIND = "postgresql"
EXPECTED_FEATURES_BY_SURFACE = {
    "main": {
        "defaultLibrarySearch": True,
        "projectBrief": True,
        "retainedState": True,
        "retainedStateRead": True,
        "retainedStateWrite": True,
        "collectiveSearch": True,
        "projectLinkedSearch": True,
        "proposals": True,
        "proposalSubmit": True,
        "proposalQuery": True,
        "proposalReview": True,
        "proposalAudit": False,
        "publicationChange": True,
        "provisionalPublication": True,
    },
    "auditor": {
        "defaultLibrarySearch": False,
        "projectBrief": False,
        "retainedState": False,
        "retainedStateRead": False,
        "retainedStateWrite": False,
        "collectiveSearch": False,
        "projectLinkedSearch": False,
        "proposals": True,
        "proposalSubmit": False,
        "proposalQuery": True,
        "proposalReview": False,
        "proposalAudit": True,
        "publicationChange": False,
        "provisionalPublication": False,
    },
}
EXPECTED_RESOURCE_URIS_BY_SURFACE = {
    "main": {
        "cortex://default-library/status",
        "cortex://default-library/catalog",
        "cortex://default-library/guide",
    },
    "auditor": {
        "cortex://default-library/status",
        "cortex://default-library/guide",
    },
}
EXPECTED_RESOURCE_TEMPLATE_URIS_BY_SURFACE = {
    "main": {"cortex://default-library/records/{recordId}"},
    "auditor": set(),
}
EXPECTED_SMOKE_TIMEOUT_SECONDS_BY_SURFACE = {
    "main": 60,
    "auditor": 30,
}
EXPECTED_RUNTIME_VARIABLES = (
    "PEGASUS_CORTEX_AUTHORITY_FILE",
    "PEGASUS_CORTEX_ROW_KEY_FILE",
    "PEGASUS_CORTEX_RUNTIME_CONNECTION",
    "PEGASUS_CORTEX_RECEIPT_KEY_FILE",
    "PEGASUS_CORTEX_APPLICATION_VERSION",
    "PEGASUS_CORTEX_TLS_POLICY",
    "PEGASUS_CORTEX_ROOT_CA",
    "PEGASUS_CORTEX_POSTGRESQL_SERVER_SPKI_SHA256",
)
EXPECTED_MIGRATION_VARIABLES = (
    "PEGASUS_CORTEX_RUNTIME_CONNECTION",
    "PEGASUS_CORTEX_MIGRATION_CONNECTION",
    "PEGASUS_CORTEX_APPLICATION_VERSION",
    "PEGASUS_CORTEX_TLS_POLICY",
    "PEGASUS_CORTEX_ROOT_CA",
    "PEGASUS_CORTEX_POSTGRESQL_SERVER_SPKI_SHA256",
)
EXPECTED_TRUSTED_LOOPBACK_RUNTIME_VARIABLES = EXPECTED_RUNTIME_VARIABLES[:-2]
EXPECTED_TRUSTED_LOOPBACK_MIGRATION_VARIABLES = EXPECTED_MIGRATION_VARIABLES[:-2]
PINNED_TLS_POLICY = "pinned-spki-private-ca-loopback"
TRUSTED_LOOPBACK_TLS_POLICY = "trusted-loopback-plaintext"
ACCEPTED_RUNTIME_SHA512 = (
    "3ea2ac626fdb0f26bb52f02584ae5e68ee88c275e740b34d93918ad380985d0d"
    "bfa1632c017658dfddc5fba396114b62748efc11b25897877814a4a4c5022edc"
)
EXPECTED_PINNED_CERTIFICATE_VALIDATION = {
    "encryptedTransport": True,
    "singleLoopbackHost": True,
    "privateCaChain": True,
    "certificateLifetime": True,
    "serverAuthenticationPurpose": True,
    "sanOnlyHostMatch": True,
    "wildcardHostMatch": False,
    "spkiSha256Pin": True,
    "certificateDownloads": False,
    "revocationNetworkDependency": False,
    "failClosed": True,
}
EXPECTED_TRUSTED_LOOPBACK_VALIDATION = {
    "encryptedTransport": False,
    "singleLoopbackHost": True,
    "privateCaChain": False,
    "certificateLifetime": False,
    "serverAuthenticationPurpose": False,
    "sanOnlyHostMatch": False,
    "wildcardHostMatch": False,
    "spkiSha256Pin": False,
    "certificateDownloads": False,
    "revocationNetworkDependency": False,
    "failClosed": True,
}


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def sha512(path: pathlib.Path) -> str:
    digest = hashlib.sha512()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_link_or_reparse(path: pathlib.Path) -> bool:
    """Recognize both POSIX links and Windows reparse-point aliases."""

    information = path.lstat()
    attributes = getattr(information, "st_file_attributes", 0)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return stat.S_ISLNK(information.st_mode) or bool(attributes & reparse_flag)


def require_unlinked_path(path: pathlib.Path, stop: pathlib.Path | None = None) -> None:
    """Reject a link/reparse alias in a path and its bounded ancestor chain."""

    current = pathlib.Path(os.path.abspath(path))
    boundary = pathlib.Path(os.path.abspath(stop)) if stop is not None else None
    while True:
        if is_link_or_reparse(current):
            raise RuntimeError(f"Verification path crosses a link or reparse point: {current}")
        if boundary is not None and current == boundary:
            break
        parent = current.parent
        if parent == current:
            break
        current = parent


def exact_file_identity(path: pathlib.Path) -> dict[str, int | str]:
    """Bind content and filesystem identity so a later replay detects drift."""

    digest = hashlib.sha256()
    total_bytes = 0
    require_unlinked_path(path)
    with path.open("rb") as stream:
        before = os.fstat(stream.fileno())
        if not stat.S_ISREG(before.st_mode):
            raise RuntimeError(f"Verification input is not a regular file: {path.name}")
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
            total_bytes += len(chunk)
        after = os.fstat(stream.fileno())

    stable_fields = (
        "st_dev",
        "st_ino",
        "st_size",
        "st_mtime_ns",
        "st_ctime_ns",
    )
    if (
        any(getattr(before, field) != getattr(after, field) for field in stable_fields)
        or total_bytes != after.st_size
    ):
        raise RuntimeError(f"Verification input changed while being read: {path.name}")
    return {
        "bytes": total_bytes,
        "sha256": digest.hexdigest(),
        "device": after.st_dev,
        "inode": after.st_ino,
        "modifiedNs": after.st_mtime_ns,
        "changedNs": after.st_ctime_ns,
    }


def load_json(path: pathlib.Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise RuntimeError(f"{path.name} is not a JSON object.")
    return value


def require_ordinal_path_order(records: list[dict[str, Any]], field: str) -> None:
    """Require canonical manifest path arrays to match Python ordinal order."""

    paths = [record["path"] for record in records]
    if paths != sorted(paths):
        raise RuntimeError(f"{field} must be deterministically path-sorted.")


def import_installer(path: pathlib.Path) -> Any:
    spec = importlib.util.spec_from_file_location("cortex_arm64_installer", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("Unable to load the packaged installer.")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def verify_version_authorities(source_root: pathlib.Path, manifest: dict[str, Any]) -> None:
    """Require package naming and compiled binary version inputs to agree."""

    version = load_json(source_root / "VERSION.json")
    properties = element_tree.parse(
        source_root / "Directory.Version.props"
    ).getroot()

    def property_value(name: str) -> str | None:
        element = properties.find(f".//{name}")
        return element.text if element is not None else None

    expected = {
        "PegasusReleaseVersion": version.get("releaseVersion"),
        "PegasusNuGetVersion": version.get("nuGetVersion"),
        "PegasusAssemblyVersion": version.get("assemblyVersion"),
        "PegasusFileVersion": version.get("fileVersion"),
    }
    if any(property_value(name) != value for name, value in expected.items()):
        raise RuntimeError("VERSION.json and Directory.Version.props disagree.")
    if (
        manifest.get("releaseVersion") != expected["PegasusReleaseVersion"]
        or manifest.get("packageVersion") != expected["PegasusNuGetVersion"]
    ):
        raise RuntimeError("The package manifest differs from the compiled version authority.")


def verify_legacy_rollback_projection(installer: Any, source_root: pathlib.Path) -> None:
    """Bind the successor to preserved and reproducibly derived predecessor facts."""

    fixture = load_json(
        source_root
        / "tools"
        / "tests"
        / "fixtures"
        / "cortex_arm64_predecessor_1.2026.07.27.13.20.A.json"
    )
    manifest = fixture.get("manifest")
    assets = manifest.get("assets") if isinstance(manifest, dict) else None
    asset_hashes = {
        item.get("name"): item.get("sha256")
        for item in assets
        if isinstance(item, dict)
    } if isinstance(assets, list) else {}
    if (
        fixture.get("fixtureKind")
        != "selected-field-projection-with-derived-installed-tree"
        or fixture.get("sourceOuterPackageBytes") != 36600321
        or fixture.get("sourceOuterPackageSha256")
        != "a9ae0fb528468e7eb5607f17c6ba872114034aae86d38b6b1abbb9fc06ea5cf5"
        or fixture.get("sourceManifestBytes") != 53801
        or not isinstance(manifest, dict)
        or fixture.get("sourceManifestSha256")
        != installer.LEGACY_ROLLBACK_MANIFEST_SHA256
        or fixture.get("sourceSmokeSha256")
        != asset_hashes.get("smoke_cortex_mcp.py")
        or fixture.get("sourceLauncherSha256")
        != asset_hashes.get("run-cortex-mcp")
        or fixture.get("sourceEntryAssemblySha256")
        != manifest.get("application", {}).get("entryAssemblySha256")
        or fixture.get("sourceGuideSha256") != manifest.get("guide", {}).get("sha256")
        or fixture.get("derivedInstalledFileCount")
        != installer.LEGACY_ROLLBACK_VERSION_TREE_FILE_COUNT
        or fixture.get("derivedInstalledVersionTreeSha256")
        != installer.LEGACY_ROLLBACK_VERSION_TREE_SHA256
    ):
        raise RuntimeError("The preserved predecessor projection is incomplete or stale.")
    installer.require_legacy_rollback_manifest(manifest)
    arguments = installer.legacy_rollback_smoke_arguments(
        pathlib.Path("/opt/pegasus/apps/cortex/versions")
        / installer.LEGACY_ROLLBACK_RELEASE_VERSION,
        manifest,
    )
    if [item for item in arguments if item.startswith("--")] != fixture.get(
        "sourceSmokeOptions"
    ):
        raise RuntimeError("The legacy rollback CLI differs from the preserved contract.")


def verify_legacy_predecessor_package(
    installer: Any,
    source_root: pathlib.Path,
    package_path: pathlib.Path,
) -> None:
    """Reproduce the legacy installed-tree digest from its sealed outer package."""

    fixture = load_json(
        source_root
        / "tools"
        / "tests"
        / "fixtures"
        / "cortex_arm64_predecessor_1.2026.07.27.13.20.A.json"
    )
    package = package_path.resolve(strict=True)
    if (
        not package.is_file()
        or package.stat().st_size != fixture.get("sourceOuterPackageBytes")
        or sha256(package) != fixture.get("sourceOuterPackageSha256")
    ):
        raise RuntimeError("The preserved predecessor outer package is not exact.")

    with tempfile.TemporaryDirectory(prefix="cortex-legacy-verify-") as temporary:
        scratch = pathlib.Path(temporary)
        outer = scratch / "outer"
        installer.extract_regular_archive(package, outer)
        manifest_path = outer / "package-manifest.json"
        if (
            manifest_path.stat().st_size != fixture["sourceManifestBytes"]
            or sha256(manifest_path) != fixture["sourceManifestSha256"]
        ):
            raise RuntimeError("The predecessor manifest differs from sealed evidence.")
        manifest = load_json(manifest_path)
        installer.require_legacy_rollback_manifest(manifest)
        assets = manifest.get("assets")
        expected_names = {
            "checksums.sha256",
            "package-manifest.json",
            *(
                item["name"]
                for item in assets
                if isinstance(item, dict) and isinstance(item.get("name"), str)
            ),
        }
        actual_names = {path.name for path in outer.iterdir() if path.is_file()}
        if actual_names != expected_names:
            raise RuntimeError("The predecessor outer package file set changed.")
        for asset in assets:
            asset_path = outer / asset["name"]
            if (
                asset_path.stat().st_size != asset["bytes"]
                or sha256(asset_path) != asset["sha256"]
            ):
                raise RuntimeError(
                    f"The predecessor asset changed: {asset['name']}"
                )

        version_root = scratch / installer.LEGACY_ROLLBACK_RELEASE_VERSION
        version_root.mkdir()
        app_root = version_root / "app"
        installer.extract_regular_archive(
            outer / manifest["application"]["archiveName"],
            app_root,
        )
        installer.extract_regular_archive(
            outer / manifest["runtime"]["archiveName"],
            version_root / "runtime",
        )
        actual_app = {
            path.relative_to(app_root).as_posix(): {
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
            for path in app_root.rglob("*")
            if path.is_file()
        }
        expected_app = {
            item["path"]: {"bytes": item["bytes"], "sha256": item["sha256"]}
            for item in manifest["application"]["files"]
        }
        if actual_app != expected_app:
            raise RuntimeError("The predecessor application archive changed.")
        (version_root / "tools").mkdir()
        for source_name, relative in (
            ("package-manifest.json", "package-manifest.json"),
            ("run-cortex-mcp", "run-cortex-mcp"),
            ("run-cortex-migrate", "run-cortex-migrate"),
            ("smoke_cortex_mcp.py", "tools/smoke_cortex_mcp.py"),
        ):
            destination = version_root / relative
            destination.write_bytes((outer / source_name).read_bytes())
        if (
            installer.version_tree_file_count(version_root)
            != fixture["derivedInstalledFileCount"]
            or installer.version_tree_sha256(version_root)
            != fixture["derivedInstalledVersionTreeSha256"]
        ):
            raise RuntimeError(
                "The predecessor installed-tree derivation differs from sealed evidence."
            )


def verify_smoke_tool_contract(path: pathlib.Path) -> None:
    """Keep the install-time smoke aligned with the capability-scoped main role."""
    source = path.read_text(encoding="utf-8")
    if '"mode": "broad"' not in source:
        raise RuntimeError("The packaged smoke verifier does not exercise broad search.")
    spec = importlib.util.spec_from_file_location("cortex_arm64_smoke", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("Unable to load the packaged smoke verifier.")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)

    if (
        module.EXPECTED_MAIN_TOOLS != EXPECTED_MAIN_TOOLS
        or module.EXPECTED_AUDITOR_TOOLS != EXPECTED_AUDITOR_TOOLS
        or module.EXPECTED_INITIALIZE_INSTRUCTION_MARKERS
        != EXPECTED_INITIALIZE_INSTRUCTION_MARKERS
        or module.EXPECTED_ACCESS_BY_SURFACE != EXPECTED_ACCESS_BY_SURFACE
        or module.EXPECTED_STORAGE_KIND != EXPECTED_STORAGE_KIND
        or module.EXPECTED_FEATURES_BY_SURFACE != EXPECTED_FEATURES_BY_SURFACE
        or module.EXPECTED_RESOURCE_URIS_BY_SURFACE
        != EXPECTED_RESOURCE_URIS_BY_SURFACE
        or module.EXPECTED_RESOURCE_TEMPLATE_URIS_BY_SURFACE
        != EXPECTED_RESOURCE_TEMPLATE_URIS_BY_SURFACE
    ):
        raise RuntimeError("The packaged smoke verifier has a stale MCP tool contract.")


def verify_integrated_fails_closed(dotnet: pathlib.Path, assembly: pathlib.Path) -> None:
    """Exercise the selected entry point without contacting PostgreSQL or Nexus."""

    environment = os.environ.copy()
    for name in (
        "PEGASUS_CORTEX_AUTHORITY_FILE",
        "PEGASUS_CORTEX_ROW_KEY_FILE",
        "PEGASUS_CORTEX_RUNTIME_CONNECTION",
        "PEGASUS_CORTEX_MIGRATION_CONNECTION",
        "PEGASUS_CORTEX_APPLICATION_VERSION",
    ):
        environment.pop(name, None)
    completed = subprocess.run(
        [str(dotnet.resolve(strict=True)), str(assembly.resolve(strict=True))],
        input="",
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="strict",
        timeout=20,
        check=False,
        env=environment,
    )
    if completed.returncode != 1 or completed.stdout:
        raise RuntimeError("The portable integrated entry point did not fail closed.")
    if (
        "code=cortex_hybrid_not_configured" not in completed.stderr
        or "no local fallback was started" not in completed.stderr
    ):
        raise RuntimeError("The integrated entry point omitted its fail-closed diagnostic.")


def verify_integrated_source_contract(source_root: pathlib.Path) -> None:
    """Bind the package to the provider ports that make its advertised tools real."""

    solution = element_tree.parse(source_root / "PegasusCortex.slnx").getroot()
    solution_paths = [
        (element.get("Path") or "").replace("\\", "/")
        for element in solution.iter("Project")
    ]
    integrated_path = (
        "src/PegasusCortex.Mcp.Integrated/PegasusCortex.Mcp.Integrated.csproj"
    )
    service_path = "src/PegasusCortex.Service/PegasusCortex.Service.csproj"
    if (
        solution.tag != "Solution"
        or solution_paths.count(integrated_path) != 1
        or solution_paths.count(service_path) != 1
    ):
        raise RuntimeError(
            "PegasusCortex.slnx does not identify Integrated and Service exactly once."
        )

    integrated_project = element_tree.parse(
        source_root / pathlib.PurePosixPath(integrated_path)
    ).getroot()
    service_references = [
        (element.get("Include") or "").replace("\\", "/")
        for element in integrated_project.iter("ProjectReference")
    ]
    if service_references.count(
        "../PegasusCortex.Service/PegasusCortex.Service.csproj"
    ) != 1:
        raise RuntimeError(
            "The integrated MCP project does not reference Service exactly once."
        )

    contract = (
        source_root / "src" / "PegasusCortex.Mcp" / "Knowledge" / "CortexMcpContract.cs"
    ).read_text(encoding="utf-8")
    for name in EXPECTED_MAIN_TOOLS | EXPECTED_AUDITOR_TOOLS:
        if f'"{name}"' not in contract:
            raise RuntimeError(f"The MCP source omits packaged tool {name!r}.")
    if '"cortex_sync' in contract:
        raise RuntimeError("Sync operations unexpectedly entered the MCP tool contract.")

    protocol = (
        source_root / "src" / "PegasusCortex.Mcp" / "Protocol" / "McpProtocol.cs"
    ).read_text(encoding="utf-8")
    for marker in EXPECTED_INITIALIZE_INSTRUCTION_MARKERS:
        if marker not in protocol:
            raise RuntimeError(
                f"The MCP initialization contract omits smoke marker {marker!r}."
            )

    application = (
        source_root
        / "src"
        / "PegasusCortex.Mcp"
        / "Knowledge"
        / "CortexMcpApplication.cs"
    ).read_text(encoding="utf-8")
    if '["projectContinuityRuntime"] = CanReadProjectBrief' not in application:
        raise RuntimeError(
            "The MCP discovery contract no longer reports the project-brief runtime."
        )
    for token in (
        '["storageKind"]',
        '["features"]',
        '["projectLinkedSearch"]',
        '["collectiveSearch"]',
        '["proposalSubmit"]',
        '["proposalQuery"]',
        '["proposalReview"]',
        '["proposalAudit"]',
        '["publicationChange"]',
        '["provisionalPublication"]',
    ):
        if token not in application:
            raise RuntimeError(
                f"The MCP source omits truthful capability token {token!r}."
            )

    host = (
        source_root
        / "src"
        / "PegasusCortex.Mcp.Integrated"
        / "Runtime"
        / "CortexMcpIntegratedHost.cs"
    ).read_text(encoding="utf-8")
    for token in (
        "ProjectBrief:",
        "RetainedAssertionReader:",
        "RetainedAssertionWriter:",
        "CortexMcpRuntimeCapability.RetainedRead",
        "CortexMcpRuntimeCapability.RetainedWrite",
        "PublicationChange:",
    ):
        if token not in host:
            raise RuntimeError(
                f"The integrated host is not wired for packaged capability {token!r}."
            )

    sync_program = (
        source_root / "src" / "PegasusCortex.Sync.Client" / "Program.cs"
    ).read_text(encoding="utf-8")
    if (
        'args.Length == 1 && args[0] is "sync-now" or "watch"' not in sync_program
        or "sync_transport_adapter_required" not in sync_program
        or "CortexSyncClientCommand.RunAsync" in sync_program
    ):
        raise RuntimeError("The source-bound sync client is not inert without explicit host composition.")

    migration_command = (
        source_root
        / "src"
        / "PegasusCortex.Service"
        / "Persistence"
        / "CortexPostgreSqlMigrationCommand.cs"
    ).read_text(encoding="utf-8")
    for token in (
        'ApplicationVersionVariable = "PEGASUS_CORTEX_APPLICATION_VERSION"',
        "RequireEnvironment(environment, ApplicationVersionVariable)",
        "applicationVersion,",
    ):
        if token not in migration_command:
            raise RuntimeError(
                "The migration command no longer requires the packaged application-version variable."
            )

    for launcher_name in ("run-cortex-mcp", "run-cortex-migrate"):
        launcher = (source_root / "deploy" / "arm64" / launcher_name).read_text(
            encoding="utf-8"
        )
        if "PEGASUS_ROOT=${PEGASUS_ROOT:-" in launcher or "export PEGASUS_ROOT" not in launcher:
            raise RuntimeError(
                f"The {launcher_name} launcher does not pin its containing Pegasus root."
            )
        for token in (
            'CONFIG_RELEASES_ROOT="$PEGASUS_ROOT/config/cortex/releases"',
            'CONFIG_ROOT="$CONFIG_RELEASES_ROOT/$RELEASE_VERSION"',
            '"$PEGASUS_ROOT/apps/cortex/versions/$RELEASE_VERSION"',
        ):
            if token not in launcher:
                raise RuntimeError(
                    f"The {launcher_name} launcher no longer derives release-scoped configuration."
                )
    migration_launcher = (
        source_root / "deploy" / "arm64" / "run-cortex-migrate"
    ).read_text(encoding="utf-8")
    for token in (
        "--expected-application-version",
        'PEGASUS_CORTEX_APPLICATION_VERSION:-}',
        '!= "$EXPECTED_APPLICATION_VERSION"',
    ):
        if token not in migration_launcher:
            raise RuntimeError(
                "The migration launcher no longer binds the protected application version to the sealed package."
            )


def verify_configuration_contract(
    configuration: Any,
    tls_policy: str = PINNED_TLS_POLICY,
) -> None:
    """Require the exact protected runtime and one-shot migration inputs."""

    expected_runtime_variables = (
        EXPECTED_TRUSTED_LOOPBACK_RUNTIME_VARIABLES
        if tls_policy == TRUSTED_LOOPBACK_TLS_POLICY
        else EXPECTED_RUNTIME_VARIABLES
    )
    expected_migration_variables = (
        EXPECTED_TRUSTED_LOOPBACK_MIGRATION_VARIABLES
        if tls_policy == TRUSTED_LOOPBACK_TLS_POLICY
        else EXPECTED_MIGRATION_VARIABLES
    )

    if (
        not isinstance(configuration, dict)
        or configuration.get("runtimeFilePattern")
        != "config/cortex/releases/{releaseVersion}/cortex-mcp.env"
        or configuration.get("migrationFilePattern")
        != "config/cortex/releases/{releaseVersion}/cortex-migration.env"
        or configuration.get("releaseScoped") is not True
        or configuration.get("runtimeFileMode") != "0640"
        or configuration.get("migrationFileMode") != "0600"
        or configuration.get("sharedByPrincipal") is not True
        or configuration.get("principalSelectedByOperatingSystemIdentity") is not True
        or configuration.get("secretsIncluded") is not False
        or configuration.get("requiredRuntimeVariables")
        != list(expected_runtime_variables)
        or configuration.get("requiredMigrationVariables")
        != list(expected_migration_variables)
    ):
        raise RuntimeError("The package protected-configuration contract changed.")


def normalized_tar_names(path: pathlib.Path) -> set[str]:
    names: set[str] = set()
    with tarfile.open(path, mode="r:gz") as bundle:
        for member in bundle.getmembers():
            if member.name in {".", "./"}:
                continue
            name = member.name[2:] if member.name.startswith("./") else member.name
            relative = pathlib.PurePosixPath(name)
            if (
                relative.is_absolute()
                or any(part in {"", ".", ".."} for part in relative.parts)
                or not (member.isdir() or member.isreg())
            ):
                raise RuntimeError("The outer package contains an unsafe archive member.")
            if member.isdir():
                raise RuntimeError("The flat outer package contains an unexpected directory.")
            if name in names:
                raise RuntimeError("The outer package contains a duplicate file.")
            names.add(name)
    return names


def normalized_tar_inventory(path: pathlib.Path) -> dict[str, dict[str, Any]]:
    """Hash every safe regular outer-package member without extracting it."""

    inventory: dict[str, dict[str, Any]] = {}
    with tarfile.open(path, mode="r:gz") as bundle:
        for member in bundle.getmembers():
            if member.name in {".", "./"}:
                continue
            name = member.name[2:] if member.name.startswith("./") else member.name
            relative = pathlib.PurePosixPath(name)
            if (
                relative.is_absolute()
                or any(part in {"", ".", ".."} for part in relative.parts)
                or not (member.isdir() or member.isreg())
            ):
                raise RuntimeError("The outer package contains an unsafe archive member.")
            if member.isdir():
                raise RuntimeError("The flat outer package contains an unexpected directory.")
            if name in inventory:
                raise RuntimeError("The outer package contains a duplicate file.")
            source = bundle.extractfile(member)
            if source is None:
                raise RuntimeError("The outer package member could not be opened.")
            digest = hashlib.sha256()
            observed = 0
            with source:
                for chunk in iter(lambda: source.read(1024 * 1024), b""):
                    observed += len(chunk)
                    digest.update(chunk)
            if observed != member.size:
                raise RuntimeError("The outer package member length changed during read.")
            inventory[name] = {"bytes": observed, "sha256": digest.hexdigest()}
    return inventory


def verify_delivery_root(
    delivery: pathlib.Path,
    expected_asset_names: set[str],
) -> dict[str, dict[str, Any]]:
    """Require one clean, flat delivery with no verifier-created residue."""

    expected_names = {"package-manifest.json", "checksums.sha256", *expected_asset_names}
    entries = list(delivery.iterdir())
    if {entry.name for entry in entries} != expected_names:
        raise RuntimeError("The delivery root contains entries outside the sealed allowlist.")
    inventory: dict[str, dict[str, Any]] = {}
    for entry in entries:
        information = entry.lstat()
        if is_link_or_reparse(entry) or not stat.S_ISREG(information.st_mode):
            raise RuntimeError(
                "The delivery root contains a link, reparse point, or non-regular entry."
            )
        identity = exact_file_identity(entry)
        inventory[entry.name] = {
            "bytes": identity["bytes"],
            "sha256": identity["sha256"],
        }
    return inventory


def regular_tree_inventory(root: pathlib.Path) -> dict[str, dict[str, Any]]:
    """Inventory one ordinary tree without following linked or special entries."""

    root = pathlib.Path(os.path.abspath(root))
    require_unlinked_path(root)
    root_information = root.lstat()
    if is_link_or_reparse(root) or not stat.S_ISDIR(root_information.st_mode):
        raise RuntimeError("The portable publish root is not one direct directory.")

    inventory: dict[str, dict[str, Any]] = {}
    pending = [root]
    while pending:
        directory = pending.pop()
        require_unlinked_path(directory, root)
        for entry in directory.iterdir():
            information = entry.lstat()
            if is_link_or_reparse(entry):
                raise RuntimeError(
                    "The portable publish contains a link or reparse point."
                )
            if stat.S_ISDIR(information.st_mode):
                pending.append(entry)
                continue
            if not stat.S_ISREG(information.st_mode):
                raise RuntimeError("The portable publish contains a special entry.")
            identity = exact_file_identity(entry)
            relative = entry.relative_to(root).as_posix()
            inventory[relative] = {
                "bytes": identity["bytes"],
                "sha256": identity["sha256"],
            }
    return inventory


def verify_portable_publish_unchanged(
    portable_publish: pathlib.Path,
    expected_inventory: dict[str, dict[str, Any]],
    integrated_assembly: pathlib.Path,
    integrated_identity: dict[str, int | str],
) -> None:
    """Replay the executable tree after the subprocess returns."""

    try:
        replay_identity = exact_file_identity(integrated_assembly)
        replay_inventory = regular_tree_inventory(portable_publish)
    except (OSError, RuntimeError) as error:
        raise RuntimeError("The portable publish changed during verification.") from error
    if replay_identity != integrated_identity or replay_inventory != expected_inventory:
        raise RuntimeError("The portable publish changed during verification.")


def verify_aarch64_elf(path: pathlib.Path) -> None:
    header = path.read_bytes()[:64]
    if len(header) < 20 or header[:4] != b"\x7fELF":
        raise RuntimeError("The bundled dotnet host is not ELF.")
    byte_order = "little" if header[5] == 1 else "big" if header[5] == 2 else None
    if byte_order is None:
        raise RuntimeError("The bundled dotnet host has an invalid ELF byte order.")
    machine = int.from_bytes(header[18:20], byte_order)
    if machine != 183:
        raise RuntimeError(f"The bundled dotnet host is not AArch64; e_machine={machine}.")


def verify_launcher_exports_protected_config(path: pathlib.Path) -> None:
    """Ensure sourced protected assignments reach the launched .NET process."""

    text = path.read_text(encoding="utf-8")
    export_start = text.find("set -a")
    source = text.find('. "$CONFIG_FILE"', export_start + 1)
    export_end = text.find("set +a", source + 1)
    executable = text.find("\nexec ", export_end + 1)
    if min(export_start, source, export_end, executable) < 0:
        raise RuntimeError(
            f"The launcher does not export its protected configuration: {path.name}"
        )


def verify_integrated_launcher(path: pathlib.Path) -> None:
    verify_launcher_exports_protected_config(path)
    text = path.read_text(encoding="utf-8")
    if '"$SCRIPT_DIR/app/PegasusCortex.Mcp.Integrated.dll"' not in text:
        raise RuntimeError("The runtime launcher does not select the integrated assembly.")
    if '"$@"' not in text:
        raise RuntimeError(
            "The integrated launcher drops supported one-shot operator arguments."
        )
    if "PegasusCortex.Sync.Client" in text or "--watch" in text:
        raise RuntimeError("The MCP launcher unexpectedly starts the sync watcher.")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--artifact-root", required=True, type=pathlib.Path)
    parser.add_argument(
        "--legacy-predecessor-package",
        required=True,
        type=pathlib.Path,
    )
    parser.add_argument(
        "--dotnet",
        type=pathlib.Path,
        default=pathlib.Path(r"C:\Program Files\dotnet\dotnet.exe"),
    )
    args = parser.parse_args()

    artifact_root = args.artifact_root.resolve(strict=True)
    build = load_json(artifact_root / "build-result.json")
    delivery = pathlib.Path(build["deliveryRoot"]).resolve(strict=True)
    package_archive = pathlib.Path(build["packageArchive"]).resolve(strict=True)
    manifest_path = pathlib.Path(build["packageManifest"]).resolve(strict=True)
    if (
        delivery.parent != artifact_root
        or package_archive.parent != artifact_root
        or manifest_path != delivery / "package-manifest.json"
    ):
        raise RuntimeError("Build evidence points outside the artifact root.")
    outer_package_identity = exact_file_identity(package_archive)
    if (
        outer_package_identity["bytes"] != build.get("packageArchiveBytes")
        or outer_package_identity["sha256"] != build.get("packageArchiveSha256")
    ):
        raise RuntimeError("The outer package SHA-256 changed.")
    if sha256(manifest_path) != build["packageManifestSha256"]:
        raise RuntimeError("The package manifest SHA-256 changed.")

    manifest = load_json(manifest_path)
    release_version = manifest.get("releaseVersion")
    if (
        not isinstance(release_version, str)
        or delivery.name != "delivery"
        or package_archive.name
        != f"pegasus-cortex-mcp-{release_version}-linux-arm64.tar.gz"
        or build.get("deliveryRoot") != str(delivery)
        or build.get("packageArchive") != str(package_archive)
        or build.get("packageManifest") != str(manifest_path)
    ):
        raise RuntimeError("Build evidence names do not match the sealed release identity.")
    source_root_lexical = pathlib.Path(os.path.abspath(__file__)).parent.parent
    require_unlinked_path(source_root_lexical)
    source_root = source_root_lexical.resolve(strict=True)
    source_inputs = manifest.get("sourceInputs")
    if not isinstance(source_inputs, list) or not source_inputs:
        raise RuntimeError("The package manifest has no source-input evidence.")
    source_by_path: dict[str, dict[str, Any]] = {}
    source_identities: dict[
        str,
        tuple[pathlib.Path, pathlib.Path, dict[str, int | str]],
    ] = {}
    for source in source_inputs:
        if not isinstance(source, dict):
            raise RuntimeError("The package manifest has invalid source-input evidence.")
        relative_text = source.get("path")
        relative = pathlib.PurePosixPath(relative_text) if isinstance(relative_text, str) else None
        if (
            relative is None
            or relative.is_absolute()
            or any(part in {"", ".", ".."} for part in relative.parts)
            or relative_text in source_by_path
        ):
            raise RuntimeError("The package manifest has an unsafe or duplicate source path.")
        lexical = source_root.joinpath(*relative.parts)
        require_unlinked_path(lexical, source_root)
        local = lexical.resolve(strict=True)
        if source_root not in local.parents:
            raise RuntimeError("A source-input path escapes the Pegasus Cortex root.")
        identity = exact_file_identity(local)
        if (
            identity["bytes"] != source.get("bytes")
            or identity["sha256"] != source.get("sha256")
        ):
            raise RuntimeError(f"Source-input integrity failed: {relative_text}")
        source_by_path[relative_text] = source
        source_identities[relative_text] = (lexical, local, identity)
    require_ordinal_path_order(source_inputs, "Package source-input evidence")
    required_source_inputs = {
        "Directory.Build.props",
        "Directory.Version.props",
        "PegasusCortex.slnx",
        "VERSION.json",
        "global.json",
        "NuGet.Config",
        "deploy/arm64/install_cortex_mcp_arm64.py",
        "deploy/arm64/run-cortex-mcp",
        "deploy/arm64/run-cortex-migrate",
        "deploy/arm64/smoke_cortex_mcp.py",
        "schemas/cortex-sync-gateway.v1.schema.json",
        "src/PegasusCortex.Contracts/Sync/CortexSyncContractIds.cs",
        "src/PegasusCortex.Contracts/Sync/CortexSyncContractPolicy.cs",
        "src/PegasusCortex.Contracts/Sync/CortexSyncGatewayContracts.cs",
        "src/PegasusCortex.Core/Continuity/CortexCurrentProjection.cs",
        "src/PegasusCortex.Core/Retention/CortexRetentionTransferContracts.cs",
        "src/PegasusCortex.Core/Retention/CortexRetentionTransferImport.cs",
        "src/PegasusCortex.Core/Retention/CortexRetentionTransferPolicy.cs",
        "src/PegasusCortex.Core/Sync/CortexSyncApplicationContracts.cs",
        "src/PegasusCortex.Core/Sync/CortexSyncApplicationService.cs",
        "src/PegasusCortex.Core/Sync/CortexSyncPublishedHeadRepository.cs",
        "src/PegasusCortex.Mcp.Integrated/PegasusCortex.Mcp.Integrated.csproj",
        "src/PegasusCortex.Mcp.Integrated/Recovery/CortexIntegratedCurrentRecoveryBriefVerifier.cs",
        "src/PegasusCortex.Mcp.Integrated/Runtime/CortexIntegratedCurrentRecoverySyncGateway.cs",
        "src/PegasusCortex.Mcp.Integrated/Runtime/CortexMcpIntegratedHost.cs",
        "src/PegasusCortex.Mcp.Local/PegasusCortex.Mcp.Local.csproj",
        "src/PegasusCortex.Mcp.Local/Recovery/CortexLocalCurrentSnapshotCommand.cs",
        "src/PegasusCortex.Mcp.Local/Recovery/CortexLocalProjectTransferExportCommand.cs",
        "src/PegasusCortex.OfflineJournal/PegasusCortex.OfflineJournal.csproj",
        "src/PegasusCortex.OfflineJournal/Paths/IJournalFileSystemBoundary.cs",
        "src/PegasusCortex.OfflineJournal/Paths/LexicalTestOnlyJournalFileSystemBoundary.cs",
        "src/PegasusCortex.OfflineJournal.Platform.Linux/LinuxRootedFileSystemSession.cs",
        "src/PegasusCortex.OfflineJournal.Platform.Linux/PegasusCortex.OfflineJournal.Platform.Linux.csproj",
        "src/PegasusCortex.Persistence.PostgreSql/Migrations/0006_agent_retention.sql",
        "src/PegasusCortex.Persistence.PostgreSql/Migrations/0007_author_owned_publication_change.sql",
        "src/PegasusCortex.Persistence.PostgreSql/Migrations/PostgreSqlMigrationCatalog.cs",
        "src/PegasusCortex.Persistence.PostgreSql/Sync/PostgreSqlCortexSyncRepository.cs",
        "src/PegasusCortex.Persistence.Sqlite/PegasusCortex.Persistence.Sqlite.csproj",
        "src/PegasusCortex.Service/Persistence/CortexPostgreSqlMigrationCommand.cs",
        "src/PegasusCortex.Sync.Client/PegasusCortex.Sync.Client.csproj",
        "src/PegasusCortex.Sync.Client/Orchestration/CortexSyncCoordinator.cs",
        "src/PegasusCortex.Sync.Client/Persistence/ProtectedCortexSyncCursorStore.cs",
        "src/PegasusCortex.Sync.Client/Persistence/ProtectedCortexSyncPendingAttemptStore.cs",
        "src/PegasusCortex.Sync.Client/Watching/FileSystemCortexSyncChangeSignalSource.cs",
        "schemas/cortex-canonical-timestamp-v1.vectors.json",
        "tests/PegasusCortex.AgentRetention.Tests/RetentionTransferTests.cs",
        "tests/PegasusCortex.Contracts.Tests/CanonicalTimestampGoldenTests.cs",
        "tests/PegasusCortex.Core.Tests/CurrentProjectionTests.cs",
        "tests/PegasusCortex.Core.Tests/PegasusCortex.Core.Tests.csproj",
        "tests/PegasusCortex.Core.Tests/SyncApplicationServiceTests.cs",
        "tests/PegasusCortex.Mcp.Integrated.Tests/Program.cs",
        "tests/PegasusCortex.Mcp.Local.Tests/LocalCurrentMaterializationTests.cs",
        "tests/PegasusCortex.OfflineJournal.Platform.Linux.Tests/Program.cs",
        "tests/PegasusCortex.Persistence.PostgreSql.IntegrationTests/FixtureRetentionLiveWorkflow.cs",
        "tests/PegasusCortex.Persistence.PostgreSql.IntegrationTests/FixtureSyncLiveWorkflow.cs",
        "tests/PegasusCortex.Persistence.PostgreSql.IntegrationTests/PegasusCortex.Persistence.PostgreSql.IntegrationTests.csproj",
        "tests/PegasusCortex.Sync.Client.Tests/Program.cs",
        "tools/cortex_current.py",
        "tools/New-CortexMcpArm64Package.ps1",
        "tools/Test-CortexMcpArm64Package.py",
        "tools/tests/test_canonical_timestamp_golden.py",
        "tools/tests/test_cortex_mcp_arm64_package.py",
        "tools/tests/fixtures/cortex_arm64_predecessor_1.2026.07.27.13.20.A.json",
        "tools/test_cortex_current.py",
    }
    missing_sources = required_source_inputs - set(source_by_path)
    if missing_sources:
        raise RuntimeError(
            f"The package source allowlist omits required inputs: {sorted(missing_sources)!r}"
        )
    verify_integrated_source_contract(source_root)
    verify_version_authorities(source_root, manifest)

    migration_ids = sorted(
        {
            match.group(1)
            for source in source_inputs
            if (
                match := re.search(
                    r"/Migrations/([0-9]{4})_[^/]+[.]sql$",
                    source["path"],
                )
            )
        }
    )
    if not migration_ids:
        raise RuntimeError("The package manifest binds no PostgreSQL migrations.")
    if "0007" not in migration_ids:
        raise RuntimeError("The package manifest omits required PostgreSQL migration 0007.")
    if manifest["databaseContract"]["migrationId"] != migration_ids[-1]:
        raise RuntimeError(
            "The declared database migration is not the latest hash-bound migration."
        )
    if (
        manifest.get("schemaVersion") != 3
        or manifest.get("runtimeIdentifier") != "linux-arm64"
        or manifest.get("transport") != "stdio"
        or manifest.get("network") is not True
        or manifest.get("networkScope") != "loopback-postgresql-only"
        or manifest.get("database") is not True
        or manifest.get("authentication") is not True
        or manifest.get("writes") is not True
        or manifest.get("liveServices") is not True
        or manifest.get("serviceRegistration") is not False
        or manifest.get("x64MutationAuthorized") is not False
        or manifest.get("contractScope")
        != "integrated-stdio-with-local-postgresql"
    ):
        raise RuntimeError("The package scope is not ARM64-only integrated stdio.")
    application = manifest.get("application")
    if (
        not isinstance(application, dict)
        or application.get("profile") != "integrated-postgresql"
        or application.get("entryAssembly") != "PegasusCortex.Mcp.Integrated.dll"
    ):
        raise RuntimeError("The package does not select the integrated MCP entry point.")
    application_files = application.get("files")
    application_by_path: dict[str, dict[str, Any]] = {}
    if isinstance(application_files, list):
        for item in application_files:
            path_text = item.get("path") if isinstance(item, dict) else None
            relative = (
                pathlib.PurePosixPath(path_text)
                if isinstance(path_text, str)
                else None
            )
            if (
                relative is None
                or relative.is_absolute()
                or any(part in {"", ".", ".."} for part in relative.parts)
                or path_text in application_by_path
                or not isinstance(item.get("bytes"), int)
                or item["bytes"] < 0
                or not isinstance(item.get("sha256"), str)
                or re.fullmatch(r"[0-9a-f]{64}", item["sha256"]) is None
            ):
                raise RuntimeError(
                    "The integrated application inventory has an unsafe or duplicate record."
                )
            application_by_path[path_text] = item
        require_ordinal_path_order(
            application_files,
            "The integrated application inventory",
        )
    application_paths = set(application_by_path)
    if (
        not isinstance(application_files, list)
        or application.get("fileCount") != len(application_files)
        or "PegasusCortex.Mcp.Integrated.dll" not in application_paths
        or "PegasusCortex.Mcp.Integrated.runtimeconfig.json" not in application_paths
        or "PegasusCortex.Mcp.Integrated.deps.json" not in application_paths
        or any(
            path is not None
            and (
                path.startswith("PegasusCortex.Sync.Client")
                or path.startswith("PegasusCortex.Mcp.Local")
            )
            for path in application_paths
        )
    ):
        raise RuntimeError("The integrated application inventory is incomplete.")
    named_application_hashes = {
        "PegasusCortex.Mcp.Integrated.dll": application.get(
            "entryAssemblySha256"
        ),
        "PegasusCortex.Mcp.Integrated.runtimeconfig.json": application.get(
            "runtimeConfigurationSha256"
        ),
        "admin/PegasusCortex.Service.dll": application.get(
            "administrationEntryAssemblySha256"
        ),
    }
    if any(
        path not in application_by_path
        or application_by_path[path].get("sha256") != expected_hash
        for path, expected_hash in named_application_hashes.items()
    ):
        raise RuntimeError(
            "The named application hashes differ from the sealed inventory."
        )
    sdk = manifest.get("sdk")
    global_json = source_by_path.get("global.json")
    actual_sdk = sdk.get("actualVersion") if isinstance(sdk, dict) else None
    actual_sdk_parts = tuple(map(int, actual_sdk.split("."))) if isinstance(actual_sdk, str) and re.fullmatch(r"10\.0\.4\d{2}", actual_sdk) else None
    if (
        not isinstance(sdk, dict)
        or sdk.get("version") != "10.0.400"
        or actual_sdk_parts is None
        or actual_sdk_parts < (10, 0, 400)
        or sdk.get("rollForward") != "latestPatch"
        or sdk.get("allowPrerelease") is not False
        or global_json is None
        or sdk.get("globalJsonSha256") != global_json.get("sha256")
    ):
        raise RuntimeError("The package SDK provenance does not satisfy the 10.0.400 latestPatch policy.")
    if manifest.get("publishContract") != {
        "configuration": "Release",
        "frameworkDependent": True,
        "portable": True,
        "useAppHost": False,
        "singleFile": False,
        "readyToRun": False,
        "nativeAot": False,
        "trimmed": False,
        "debugSymbols": False,
    }:
        raise RuntimeError("The package portable publish contract changed.")
    if manifest.get("companionSourceContract") != {
        "providerNeutralTransferSourceBound": True,
        "localOperatorSourceBound": True,
        "syncClientSourceBound": True,
        "syncClientApplicationPublished": False,
        "syncClientWatcherAutoStarted": False,
        "syncToolsExposedThroughMcp": False,
    }:
        raise RuntimeError("The package companion-source boundary changed.")
    runtime = manifest.get("runtime")
    if (
        manifest.get("runtimeVersion") != "10.0.10"
        or not isinstance(runtime, dict)
        or runtime.get("sha512") != ACCEPTED_RUNTIME_SHA512
    ):
        raise RuntimeError("The package runtime provenance is not accepted .NET 10.0.10.")
    if manifest.get("runtimePrincipals") != {
        "main": "codexsvc",
        "auditor": "cortexaudit",
    }:
        raise RuntimeError("The package runtime principals changed.")
    expected_surfaces = {
        "main": sorted(EXPECTED_MAIN_TOOLS),
        "auditor": sorted(EXPECTED_AUDITOR_TOOLS),
    }
    actual_surfaces = manifest.get("mcpToolSurfaces")
    if (
        not isinstance(actual_surfaces, dict)
        or {
            name: sorted(value) if isinstance(value, list) else value
            for name, value in actual_surfaces.items()
        }
        != expected_surfaces
    ):
        raise RuntimeError("The package main/auditor MCP surfaces changed.")
    database_contract = manifest.get("databaseContract")
    if not isinstance(database_contract, dict):
        raise RuntimeError("The package database contract is missing.")
    tls_policy = database_contract.get("tlsPolicy")
    configuration = manifest.get("configurationContract")
    verify_configuration_contract(configuration, tls_policy)
    certificate_validation = database_contract.get("certificateValidation")
    expected_validation = (
        EXPECTED_PINNED_CERTIFICATE_VALIDATION
        if tls_policy == PINNED_TLS_POLICY
        else EXPECTED_TRUSTED_LOOPBACK_VALIDATION
        if tls_policy == TRUSTED_LOOPBACK_TLS_POLICY
        else None
    )
    if (
        expected_validation is None
        or not isinstance(certificate_validation, dict)
        or certificate_validation != expected_validation
    ):
        raise RuntimeError("The package PostgreSQL transport contract is incomplete.")
    if database_contract.get("requiredMigrationId") != "0007":
        raise RuntimeError("The package database contract does not require migration 0007.")

    application_archive = manifest.get("application", {}).get("archiveName")
    runtime_archive = manifest.get("runtime", {}).get("archiveName")
    if (
        application_archive != f"cortex-mcp-app-{release_version}.tar.gz"
        or runtime_archive
        != f"dotnet-runtime-{manifest.get('runtimeVersion')}-linux-arm64.tar.gz"
        or build.get("releaseVersion") != release_version
        or build.get("runtimeIdentifier") != manifest.get("runtimeIdentifier")
        or build.get("portablePublishRoot")
        != str(artifact_root / "portable-publish")
        or build.get("entryAssemblySha256")
        != application.get("entryAssemblySha256")
        or build.get("guideSha256") != manifest.get("guide", {}).get("sha256")
        or build.get("x64Changed") is not False
    ):
        raise RuntimeError("The package archive identities differ from the sealed release.")
    expected_asset_names = {
        application_archive,
        runtime_archive,
        *EXPECTED_PACKAGE_TOOL_ASSETS,
    }
    actual_asset_names = {
        asset.get("name") for asset in manifest.get("assets", [])
        if isinstance(asset, dict)
    }
    if (
        None in expected_asset_names
        or len(manifest.get("assets", [])) != len(expected_asset_names)
        or actual_asset_names != expected_asset_names
    ):
        raise RuntimeError("The manifest asset surface differs from the approved six files.")
    asset_by_name = {
        asset["name"]: asset
        for asset in manifest["assets"]
        if isinstance(asset, dict) and isinstance(asset.get("name"), str)
    }
    if asset_by_name.get(runtime_archive, {}).get("sha256") != runtime.get("sha256"):
        raise RuntimeError("The runtime asset differs from its provenance hash.")
    runtime_asset_path = delivery / runtime_archive
    if sha512(runtime_asset_path) != ACCEPTED_RUNTIME_SHA512:
        raise RuntimeError("The runtime asset differs from its accepted SHA-512.")
    delivery_inventory = verify_delivery_root(delivery, expected_asset_names)
    tool_sources = {
        "install_cortex_mcp_arm64.py": "deploy/arm64/install_cortex_mcp_arm64.py",
        "smoke_cortex_mcp.py": "deploy/arm64/smoke_cortex_mcp.py",
        "run-cortex-mcp": "deploy/arm64/run-cortex-mcp",
        "run-cortex-migrate": "deploy/arm64/run-cortex-migrate",
    }
    for asset_name, source_path in tool_sources.items():
        source_evidence = source_by_path.get(source_path)
        if source_evidence is None or delivery_inventory.get(asset_name) != {
            "bytes": source_evidence.get("bytes"),
            "sha256": source_evidence.get("sha256"),
        }:
            raise RuntimeError(
                f"Packaged tool {asset_name!r} differs from its bound source input."
            )
    expected_names = set(EXPECTED_OUTER_NAMES)
    expected_names.update(expected_asset_names)
    if normalized_tar_names(package_archive) != expected_names:
        raise RuntimeError("The outer package file set differs from the manifest.")
    if normalized_tar_inventory(package_archive) != delivery_inventory:
        raise RuntimeError("The outer package bytes differ from the sealed delivery.")

    for asset in manifest["assets"]:
        path = delivery / asset["name"]
        if path.stat().st_size != asset["bytes"] or sha256(path) != asset["sha256"]:
            raise RuntimeError(f"Asset integrity failed: {asset['name']}")
    expected_checksums = [
        f"{asset['sha256']}  {asset['name']}" for asset in manifest["assets"]
    ]
    expected_checksums.append(
        f"{build['packageManifestSha256']}  package-manifest.json"
    )
    checksum_lines = (delivery / "checksums.sha256").read_text(
        encoding="ascii"
    ).splitlines()
    if checksum_lines != expected_checksums:
        raise RuntimeError("The delivery checksum index differs from sealed assets.")

    verify_integrated_launcher(delivery / "run-cortex-mcp")
    verify_launcher_exports_protected_config(delivery / "run-cortex-migrate")
    verify_smoke_tool_contract(delivery / "smoke_cortex_mcp.py")

    installer_path = delivery / "install_cortex_mcp_arm64.py"
    installer = import_installer(installer_path)
    verify_legacy_rollback_projection(installer, source_root)
    verify_legacy_predecessor_package(
        installer,
        source_root,
        args.legacy_predecessor_package,
    )
    if (
        installer.SMOKE_TIMEOUT_SECONDS_BY_SURFACE
        != EXPECTED_SMOKE_TIMEOUT_SECONDS_BY_SURFACE
    ):
        raise RuntimeError("The package smoke timeout contract is stale.")
    with tempfile.TemporaryDirectory(prefix="cortex-arm64-verify-") as temporary:
        temporary_root = pathlib.Path(temporary)
        app_root = temporary_root / "app"
        runtime_root = temporary_root / "runtime"
        installer.extract_regular_archive(
            delivery / manifest["application"]["archiveName"],
            app_root,
        )
        installer.extract_regular_archive(
            delivery / manifest["runtime"]["archiveName"],
            runtime_root,
        )
        verify_aarch64_elf(runtime_root / "dotnet")

        inventory = {
            path.relative_to(app_root).as_posix(): {
                "bytes": path.stat().st_size,
                "sha256": sha256(path),
            }
            for path in app_root.rglob("*")
            if path.is_file()
        }
        expected_inventory = {
            item["path"]: {"bytes": item["bytes"], "sha256": item["sha256"]}
            for item in manifest["application"]["files"]
        }
        if inventory != expected_inventory:
            raise RuntimeError("The safely extracted application differs from its inventory.")

    portable_publish = artifact_root / "portable-publish"
    if regular_tree_inventory(portable_publish) != expected_inventory:
        raise RuntimeError("The portable publish differs from its sealed inventory.")
    integrated_assembly = portable_publish / "PegasusCortex.Mcp.Integrated.dll"
    integrated_identity = exact_file_identity(integrated_assembly)
    if (
        integrated_identity["sha256"]
        != manifest["application"]["entryAssemblySha256"]
    ):
        raise RuntimeError("The selected integrated entry assembly changed.")
    verify_integrated_fails_closed(args.dotnet, integrated_assembly)
    verify_portable_publish_unchanged(
        portable_publish,
        expected_inventory,
        integrated_assembly,
        integrated_identity,
    )
    if verify_delivery_root(delivery, expected_asset_names) != delivery_inventory:
        raise RuntimeError("The verifier or another process changed the sealed delivery.")

    result = {
        "status": "pass",
        "releaseVersion": manifest["releaseVersion"],
        "runtimeIdentifier": manifest["runtimeIdentifier"],
        "packageArchiveSha256": build["packageArchiveSha256"],
        "packageManifestSha256": build["packageManifestSha256"],
        "entryAssemblySha256": manifest["application"]["entryAssemblySha256"],
        "guideSha256": manifest["guide"]["sha256"],
        "aarch64ElfVerified": True,
        "safeExtractionVerified": True,
        "portableIntegratedFailClosed": True,
        "mainToolCount": len(EXPECTED_MAIN_TOOLS),
        "auditorToolCount": len(EXPECTED_AUDITOR_TOOLS),
        "databaseMigrationId": manifest["databaseContract"]["migrationId"],
        "migrationCredentialRetained": manifest["databaseContract"][
            "migrationCredentialRetained"
        ],
        "x64Changed": False,
    }

    # Replay every mutable external input after all semantic reads and imports.
    # A passing result therefore cannot be published for a persistently changed
    # package or source tree.
    if exact_file_identity(package_archive) != outer_package_identity:
        raise RuntimeError("The outer package changed during verification.")
    for relative_text, (lexical, initial_path, initial_identity) in source_identities.items():
        try:
            replay_path = lexical.resolve(strict=True)
            replay_identity = exact_file_identity(replay_path)
        except (OSError, RuntimeError) as error:
            raise RuntimeError(
                f"Source input changed during verification: {relative_text}"
            ) from error
        if replay_path != initial_path or replay_identity != initial_identity:
            raise RuntimeError(
                f"Source input changed during verification: {relative_text}"
            )
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exception:
        print(f"ARM64 package verification failed: {type(exception).__name__}: {exception}", file=sys.stderr)
        raise SystemExit(1)
