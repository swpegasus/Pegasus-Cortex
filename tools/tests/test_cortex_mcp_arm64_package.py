from __future__ import annotations

import importlib.util
import argparse
import contextlib
import hashlib
import io
import json
import os
import pathlib
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import unittest
from types import SimpleNamespace
from typing import Any
from unittest import mock


PROJECT_ROOT = pathlib.Path(__file__).resolve().parents[2]
PREDECESSOR_FIXTURE_PATH = (
    PROJECT_ROOT
    / "tools"
    / "tests"
    / "fixtures"
    / "cortex_arm64_predecessor_1.2026.07.27.13.20.A.json"
)
PREDECESSOR_PACKAGE_ENVIRONMENT_VARIABLE = (
    "PEGASUS_CORTEX_ARM64_PREDECESSOR_PACKAGE"
)
CANONICAL_PREDECESSOR_PACKAGE_PATH = (
    PROJECT_ROOT
    / "artifacts"
    / "validation"
    / "cortex-mcp-audit-arm64-20260727-7"
    / "pegasus-cortex-mcp-1.2026.07.27.13.20.A-linux-arm64.tar.gz"
)
ARM_EVIDENCE_BUILDER_PATH = PROJECT_ROOT / "tools" / "New-CortexArm64EvidenceBundle.ps1"
ARM_PACKAGE_BUILDER_PATH = PROJECT_ROOT / "tools" / "New-CortexMcpArm64Package.ps1"


def predecessor_package_path() -> pathlib.Path:
    """Use an externally supplied exact predecessor, then the canonical archive."""

    configured = os.environ.get(PREDECESSOR_PACKAGE_ENVIRONMENT_VARIABLE)
    return (
        pathlib.Path(configured)
        if configured
        else CANONICAL_PREDECESSOR_PACKAGE_PATH
    )


def load_module(name: str, path: pathlib.Path) -> Any:
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load test subject: {path.name}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


PACKAGE_VERIFIER = load_module(
    "cortex_arm64_package_verifier",
    PROJECT_ROOT / "tools" / "Test-CortexMcpArm64Package.py",
)
INSTALLER = load_module(
    "cortex_arm64_installer_contract",
    PROJECT_ROOT / "deploy" / "arm64" / "install_cortex_mcp_arm64.py",
)


def complete_configuration() -> dict[str, object]:
    return {
        "runtimeFilePattern": (
            "config/cortex/releases/{releaseVersion}/cortex-mcp.env"
        ),
        "migrationFilePattern": (
            "config/cortex/releases/{releaseVersion}/cortex-migration.env"
        ),
        "releaseScoped": True,
        "runtimeFileMode": "0640",
        "migrationFileMode": "0600",
        "sharedByPrincipal": True,
        "principalSelectedByOperatingSystemIdentity": True,
        "secretsIncluded": False,
        "requiredRuntimeVariables": list(PACKAGE_VERIFIER.EXPECTED_RUNTIME_VARIABLES),
        "requiredMigrationVariables": list(
            PACKAGE_VERIFIER.EXPECTED_MIGRATION_VARIABLES
        ),
    }


def integrated_application(
    entry_bytes: bytes = b"integrated-entry",
) -> dict[str, object]:
    return {
        "archiveName": "app.tar.gz",
        "profile": "integrated-postgresql",
        "entryAssembly": "PegasusCortex.Mcp.Integrated.dll",
        "entryAssemblySha256": hashlib.sha256(entry_bytes).hexdigest(),
        "runtimeConfigurationSha256": "c" * 64,
        "administrationEntryAssembly": "admin/PegasusCortex.Service.dll",
        "administrationEntryAssemblySha256": "d" * 64,
    }


def integrated_manifest(version: str, entry_bytes: bytes = b"integrated-entry") -> dict[str, object]:
    asset_names = (
        "app.tar.gz",
        "runtime.tar.gz",
        "install_cortex_mcp_arm64.py",
        "run-cortex-mcp",
        "run-cortex-migrate",
        "smoke_cortex_mcp.py",
    )
    return {
        "schemaVersion": 3,
        "releaseVersion": version,
        "runtimeIdentifier": "linux-arm64",
        "transport": "stdio",
        "network": True,
        "networkScope": "loopback-postgresql-only",
        "database": True,
        "authentication": True,
        "writes": True,
        "liveServices": True,
        "serviceRegistration": False,
        "x64MutationAuthorized": False,
        "contractScope": "integrated-stdio-with-local-postgresql",
        "application": integrated_application(entry_bytes),
        "runtime": {"archiveName": "runtime.tar.gz"},
        "configurationContract": complete_configuration(),
        "databaseContract": {
            "provider": "postgresql",
            "migrationId": "0007",
            "requiredMigrationId": "0007",
            "migrationMode": "one-shot-separated-role",
            "runtimeRoleLeastPrivilege": True,
            "migrationCredentialRetained": False,
            "tlsPolicy": "pinned-spki-private-ca-loopback",
            "certificateValidation": dict(
                INSTALLER.EXPECTED_CERTIFICATE_VALIDATION
            ),
        },
        "runtimePrincipals": {"main": "codexsvc", "auditor": "cortexaudit"},
        "mcpToolSurfaces": {
            "main": sorted(INSTALLER.EXPECTED_MAIN_TOOLS),
            "auditor": sorted(INSTALLER.EXPECTED_AUDITOR_TOOLS),
        },
        "guide": {
            "resourceUri": "cortex://default-library/guide",
            "sha256": "a" * 64,
            "version": "2026-08-03",
            "utf8Bytes": 1024,
        },
        "assets": [
            {
                "name": name,
                "bytes": len(name.encode("ascii")),
                "sha256": hashlib.sha256(name.encode("ascii")).hexdigest(),
            }
            for name in asset_names
        ],
    }


def predecessor_fixture() -> dict[str, Any]:
    value = json.loads(PREDECESSOR_FIXTURE_PATH.read_text(encoding="utf-8"))
    if not isinstance(value, dict) or not isinstance(value.get("manifest"), dict):
        raise RuntimeError("The preserved predecessor fixture is invalid.")
    return value


def write_prepared_candidate(
    root: pathlib.Path,
    version: str,
    plan_id: str = "prepare-candidate",
    selector_before: str | None = None,
) -> tuple[dict[str, pathlib.Path], str]:
    paths = INSTALLER.installation_paths(root)
    for path in paths.values():
        path.mkdir(parents=True, exist_ok=True)
    release_configuration, runtime_configuration, migration_configuration = (
        INSTALLER.release_configuration_paths(paths, version)
    )
    release_configuration.mkdir()
    runtime_configuration.write_text("RUNTIME=prepared\n", encoding="utf-8")
    runtime_configuration.chmod(0o640)
    runtime_configuration_hash = INSTALLER.sha256_file(runtime_configuration)
    version_root = paths["versions"] / version
    version_root.mkdir()
    manifest_path = version_root / "package-manifest.json"
    manifest_path.write_text(json.dumps(integrated_manifest(version)), encoding="utf-8")
    expected_hash = INSTALLER.sha256_file(manifest_path)
    tree_hash = INSTALLER.version_tree_sha256(version_root)
    selector_identity = (
        (selector_before, 1, 2, 3)
        if selector_before is not None
        else (None, None, None, None)
    )
    intent_path = INSTALLER.preparation_intent_path(
        paths["manifests"], version, plan_id
    )
    extraction_path = INSTALLER.preparation_extraction_path(
        paths["manifests"], version, plan_id
    )
    attempt_path = INSTALLER.preparation_attempt_path(
        paths["manifests"], version, plan_id
    )
    migration_path = INSTALLER.preparation_migration_path(
        paths["manifests"], version, plan_id
    )
    package_identity = (11, 22, 33)
    migration = {
        "status": "pass",
        "currentMigrationId": "0007",
        "applicationVersion": version,
        "appliedMigrationIds": ["0007"],
    }
    intent_path.write_text(
        json.dumps(
            {
                "schemaVersion": INSTALLER.PREPARED_RECEIPT_SCHEMA_VERSION,
                "operation": "prepare",
                "status": "pending",
                "releaseVersion": version,
                "planId": plan_id,
                "intendedAt": "2026-08-03T00:00:00+00:00",
                "packageManifestSha256": expected_hash,
                "packageRootIdentity": list(package_identity),
                "runtimeConfigurationSha256": runtime_configuration_hash,
                "selectorBefore": selector_before,
                "selectorBeforeIdentity": INSTALLER.selector_identity_receipt(
                    selector_identity
                ),
                "candidate": f"versions/{version}",
            }
        ),
        encoding="utf-8",
    )
    extraction_path.write_text(
        json.dumps(
            {
                "schemaVersion": INSTALLER.PREPARED_RECEIPT_SCHEMA_VERSION,
                "operation": "prepare",
                "status": "extracted",
                "releaseVersion": version,
                "planId": plan_id,
                "extractedAt": "2026-08-03T00:00:00.500000+00:00",
                "packageManifestSha256": expected_hash,
                "packageRootIdentity": list(package_identity),
                "candidate": f"versions/{version}",
                "candidateObjectIdentity": list(
                    INSTALLER.directory_object_identity(
                        version_root,
                        "The prepared test candidate",
                    )
                ),
                "versionTreeSha256": tree_hash,
                "selectorBefore": selector_before,
                "selectorBeforeIdentity": INSTALLER.selector_identity_receipt(
                    selector_identity
                ),
            }
        ),
        encoding="utf-8",
    )
    attempt_path.write_text(
        json.dumps(
            {
                "schemaVersion": INSTALLER.PREPARED_RECEIPT_SCHEMA_VERSION,
                "operation": "prepare",
                "status": "migration-pending",
                "releaseVersion": version,
                "planId": plan_id,
                "attemptedAt": "2026-08-03T00:00:01+00:00",
                "packageManifestSha256": expected_hash,
                "preparationExtraction": extraction_path.name,
                "runtimeConfigurationSha256": runtime_configuration_hash,
                "migrationConfigurationSha256": "b" * 64,
                "versionTreeSha256": tree_hash,
                "selectorBefore": selector_before,
                "selectorBeforeIdentity": INSTALLER.selector_identity_receipt(
                    selector_identity
                ),
                "selectorChanged": False,
                "candidate": f"versions/{version}",
                "migrationCredentialRetained": True,
            }
        ),
        encoding="utf-8",
    )
    migration_path.write_text(
        json.dumps(
            {
                "schemaVersion": INSTALLER.PREPARED_RECEIPT_SCHEMA_VERSION,
                "operation": "prepare",
                "status": "migration-complete",
                "releaseVersion": version,
                "planId": plan_id,
                "completedAt": "2026-08-03T00:00:02+00:00",
                "preparationAttempt": attempt_path.name,
                "packageManifestSha256": expected_hash,
                "runtimeConfigurationSha256": runtime_configuration_hash,
                "migrationConfigurationSha256": "b" * 64,
                "versionTreeSha256": tree_hash,
                "selectorBefore": selector_before,
                "selectorBeforeIdentity": INSTALLER.selector_identity_receipt(
                    selector_identity
                ),
                "selectorChanged": False,
                "candidate": f"versions/{version}",
                "migration": migration,
                "migrationCredentialRetained": True,
            }
        ),
        encoding="utf-8",
    )
    INSTALLER.prepared_receipt_path(paths["manifests"], version, plan_id).write_text(
        json.dumps(
            {
                "schemaVersion": INSTALLER.PREPARED_RECEIPT_SCHEMA_VERSION,
                "operation": "prepare",
                "status": "prepared",
                "releaseVersion": version,
                "planId": plan_id,
                "packageManifestSha256": expected_hash,
                "preparationIntent": intent_path.name,
                "preparationExtraction": extraction_path.name,
                "preparationAttempt": attempt_path.name,
                "preparationMigration": migration_path.name,
                "runtimeConfigurationSha256": runtime_configuration_hash,
                "migrationConfigurationSha256": "b" * 64,
                "configurationReleaseVersion": version,
                "runtimeConfiguration": (
                    f"config/cortex/releases/{version}/cortex-mcp.env"
                ),
                "migrationConfiguration": (
                    f"config/cortex/releases/{version}/cortex-migration.env"
                ),
                "versionTreeSha256": tree_hash,
                "candidate": f"versions/{version}",
                "migrationCredentialRetained": False,
                "migrationCredentialDeletionDurable": True,
                "migration": migration,
                "selectorChanged": False,
                "selectorBefore": selector_before,
                "selectorBeforeIdentity": INSTALLER.selector_identity_receipt(
                    selector_identity
                ),
            }
        ),
        encoding="utf-8",
    )
    migration_configuration.unlink(missing_ok=True)
    return paths, expected_hash


def write_prepare_inputs(
    workspace: pathlib.Path,
    version: str,
    plan_id: str,
) -> dict[str, Any]:
    """Create a compact six-asset package and release-scoped prepare inputs."""

    entry_bytes = b"integrated-entry"
    manifest = integrated_manifest(version, entry_bytes)
    package_root = workspace / "package"
    package_root.mkdir()
    assets: dict[str, pathlib.Path] = {}
    for metadata in manifest["assets"]:
        name = metadata["name"]
        path = package_root / name
        path.write_bytes(name.encode("ascii"))
        assets[name] = path
    manifest_path = package_root / "package-manifest.json"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    expected_manifest_hash = INSTALLER.sha256_file(manifest_path)

    pegasus_root = workspace / "pegasus"
    paths = INSTALLER.installation_paths(pegasus_root)
    release_root, runtime_configuration, migration_configuration = (
        INSTALLER.release_configuration_paths(paths, version)
    )
    release_root.mkdir(parents=True)
    runtime_configuration.write_text("RUNTIME=prepared\n", encoding="utf-8")
    runtime_configuration.chmod(0o640)
    migration_configuration.write_text("MIGRATION=one-shot\n", encoding="utf-8")
    migration_configuration.chmod(0o600)

    def extract(_: pathlib.Path, destination: pathlib.Path) -> None:
        destination.mkdir()
        if destination.name == "app":
            (destination / "PegasusCortex.Mcp.Integrated.dll").write_bytes(
                entry_bytes
            )
        else:
            (destination / "dotnet").write_bytes(b"arm64-runtime")

    return {
        "arguments": argparse.Namespace(
            operation="prepare",
            package_root=package_root,
            pegasus_root=pegasus_root,
            expected_manifest_sha256=expected_manifest_hash,
            plan_id=plan_id,
            resume_attempt_sha256=None,
        ),
        "assets": assets,
        "extract": extract,
        "manifest": manifest,
        "manifestPath": manifest_path,
        "migrationConfiguration": migration_configuration,
        "paths": paths,
        "runtimeConfiguration": runtime_configuration,
        "version": version,
    }


def write_selector_transition_receipt(
    manifests_root: pathlib.Path,
    operation: str,
    version: str,
    plan_id: str,
    previous_identity: tuple[str | None, int | None, int | None, int | None],
    selected_identity: tuple[str | None, int | None, int | None, int | None],
    manifest_hash: str,
    tree_hash: str,
) -> pathlib.Path:
    """Persist the immutable selector evidence consumed by restore recovery."""

    path = INSTALLER.selector_transition_path(
        manifests_root,
        version,
        plan_id,
        operation,
    )
    path.write_text(
        json.dumps(
            {
                "schemaVersion": INSTALLER.PREPARED_RECEIPT_SCHEMA_VERSION,
                "operation": f"{operation}-selector",
                "status": "committed",
                "releaseVersion": version,
                "planId": plan_id,
                "committedAt": "2026-08-03T00:00:00+00:00",
                "packageManifestSha256": manifest_hash,
                "versionTreeSha256": tree_hash,
                "fromIdentity": INSTALLER.selector_identity_receipt(
                    previous_identity
                ),
                "fromIdentitySha256": INSTALLER.selector_identity_sha256(
                    previous_identity
                ),
                "target": selected_identity[0],
                "currentIdentity": INSTALLER.selector_identity_receipt(
                    selected_identity
                ),
                "currentIdentitySha256": INSTALLER.selector_identity_sha256(
                    selected_identity
                ),
            }
        ),
        encoding="utf-8",
    )
    return path


@contextlib.contextmanager
def portable_deployment_environment() -> Any:
    """Isolate transaction behavior from native Linux ownership and fsync APIs."""

    with (
        mock.patch.object(
            INSTALLER,
            "require_arm64_linux",
            return_value=("aarch64", 42),
        ),
        mock.patch.object(
            INSTALLER,
            "deployment_operation_lock",
            return_value=contextlib.nullcontext(),
        ),
        mock.patch.object(INSTALLER, "validate_operation_path_budget"),
        mock.patch.object(INSTALLER, "require_installation_ancestor_policy"),
        mock.patch.object(INSTALLER, "apply_shared_directory_policy"),
        mock.patch.object(INSTALLER, "require_shared_directory_policy"),
        mock.patch.object(INSTALLER, "apply_directory_policy"),
        mock.patch.object(INSTALLER, "require_directory_policy"),
        mock.patch.object(INSTALLER, "require_protected_configuration"),
        mock.patch.object(INSTALLER, "require_protected_receipt"),
        mock.patch.object(INSTALLER, "apply_version_policy"),
        mock.patch.object(INSTALLER, "require_version_policy"),
        mock.patch.object(INSTALLER, "fsync_directory"),
        mock.patch.object(INSTALLER, "fsync_tree"),
        mock.patch.object(INSTALLER, "fsync_regular_file"),
        mock.patch.object(INSTALLER.os, "fchown", create=True),
    ):
        yield


class CortexMcpArm64PackageContractTests(unittest.TestCase):
    @staticmethod
    def inspect_source_set(
        builder: pathlib.Path,
        project_root: pathlib.Path | None = None,
    ) -> subprocess.CompletedProcess[str]:
        arguments = [
            r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            "-NoProfile",
            "-NonInteractive",
            "-File",
            str(builder),
        ]
        if project_root is not None:
            arguments.extend(("-ProjectRoot", str(project_root)))
        arguments.append("-InspectSourceSet")
        return subprocess.run(
            arguments,
            cwd=builder.parent,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="strict",
            timeout=45,
            check=False,
        )

    @staticmethod
    def create_directory_alias(alias: pathlib.Path, target: pathlib.Path) -> None:
        """Create a real directory alias, using a junction when symlinks need privilege."""

        try:
            alias.symlink_to(target, target_is_directory=True)
            return
        except OSError as symlink_error:
            if os.name != "nt":
                raise RuntimeError("directory symbolic-link creation failed") from symlink_error
            link_error = symlink_error
        completed = subprocess.run(
            ["cmd.exe", "/d", "/c", "mklink", "/J", str(alias), str(target)],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=15,
            check=False,
        )
        if completed.returncode != 0:
            raise RuntimeError(
                f"directory alias creation failed: {completed.stderr or completed.stdout}"
            ) from link_error

    @staticmethod
    def remove_directory_alias(alias: pathlib.Path) -> None:
        """Remove only the alias entry, never the directory it targets."""

        if alias.is_symlink():
            alias.unlink()
        else:
            os.rmdir(alias)

    def test_arm_builder_inspection_accepts_copied_root_and_rejects_false_root(
        self,
    ) -> None:
        canonical = self.inspect_source_set(ARM_PACKAGE_BUILDER_PATH)
        self.assertEqual(0, canonical.returncode, canonical.stderr)
        expected = json.loads(canonical.stdout)
        self.assertEqual("sha256-utf8-lines-v1", expected["algorithm"])
        self.assertIn(
            "PegasusCortex.slnx",
            {item["path"] for item in expected["sourceInputs"]},
        )
        for unsafe_root in (
            pathlib.Path("relative-project-root"),
            pathlib.Path(r"C:drive-relative-root"),
            pathlib.Path(r"\current-drive-rooted"),
        ):
            with self.subTest(unsafe_root=str(unsafe_root)):
                relative_rejection = self.inspect_source_set(
                    ARM_PACKAGE_BUILDER_PATH,
                    unsafe_root,
                )
                self.assertNotEqual(0, relative_rejection.returncode)
                self.assertIn("canonical and absolute", relative_rejection.stderr)

        with tempfile.TemporaryDirectory() as temporary:
            temporary_root = pathlib.Path(temporary)
            real_container = temporary_root / "real-container"
            copied_root = real_container / "copied"
            for source in expected["sourceInputs"]:
                relative = pathlib.PurePosixPath(source["path"])
                source_path = PROJECT_ROOT.joinpath(*relative.parts)
                destination = copied_root.joinpath(*relative.parts)
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source_path, destination)

            copied_builder = copied_root / "tools" / ARM_PACKAGE_BUILDER_PATH.name
            for builder, explicit_root in (
                (copied_builder, None),
                (ARM_PACKAGE_BUILDER_PATH, copied_root),
            ):
                completed = self.inspect_source_set(builder, explicit_root)
                self.assertEqual(0, completed.returncode, completed.stderr)
                self.assertEqual(expected, json.loads(completed.stdout))

            solution = copied_root / "PegasusCortex.slnx"
            original_solution = solution.read_bytes()
            solution.write_text(
                '<Solution><Project Path="src/Unrelated/Unrelated.csproj" /></Solution>\n',
                encoding="utf-8",
            )
            rejected = self.inspect_source_set(
                ARM_PACKAGE_BUILDER_PATH,
                copied_root,
            )
            self.assertNotEqual(0, rejected.returncode)
            self.assertIn(
                "does not identify the integrated MCP and Service projects exactly once",
                rejected.stderr,
            )
            solution.write_bytes(original_solution)

            # Reject both a directly linked project root and a project reached
            # through a linked ancestor before source bytes are inspected.
            linked_root = temporary_root / "linked-root"
            linked_container = temporary_root / "linked-container"
            self.create_directory_alias(linked_root, copied_root)
            self.create_directory_alias(linked_container, real_container)
            try:
                direct_rejection = self.inspect_source_set(
                    ARM_PACKAGE_BUILDER_PATH,
                    linked_root,
                )
                self.assertNotEqual(0, direct_rejection.returncode)
                self.assertIn("direct directory", direct_rejection.stderr)

                ancestor_rejection = self.inspect_source_set(
                    ARM_PACKAGE_BUILDER_PATH,
                    linked_container / "copied",
                )
                self.assertNotEqual(0, ancestor_rejection.returncode)
                self.assertIn("crosses a link or reparse point", ancestor_rejection.stderr)
            finally:
                self.remove_directory_alias(linked_root)
                self.remove_directory_alias(linked_container)

            # Exercise Get-SafeTreeFiles through a final ordinary directory whose
            # intermediate `codex` ancestor is a junction/symlink.
            source_parent = copied_root / "integrations" / "codex"
            outside_source_parent = temporary_root / "outside-codex"
            shutil.copytree(source_parent, outside_source_parent)
            shutil.rmtree(source_parent)
            self.create_directory_alias(source_parent, outside_source_parent)
            try:
                linked_tree_rejection = self.inspect_source_set(copied_builder)
                self.assertNotEqual(0, linked_tree_rejection.returncode)
                self.assertIn("Directory chain crosses a link", linked_tree_rejection.stderr)
            finally:
                self.remove_directory_alias(source_parent)
                shutil.copytree(outside_source_parent, source_parent)

            # The final leaf check remains separate from root/tree chain checks.
            outside = pathlib.Path(temporary) / "outside.props"
            outside.write_bytes((copied_root / "Directory.Build.props").read_bytes())
            linked_leaf = copied_root / "Directory.Build.props"
            linked_leaf.unlink()
            try:
                linked_leaf.symlink_to(outside)
            except OSError:
                shutil.copy2(PROJECT_ROOT / "Directory.Build.props", linked_leaf)
            else:
                rejected = self.inspect_source_set(copied_builder)
                self.assertNotEqual(0, rejected.returncode)
                self.assertIn("direct regular file", rejected.stderr)

    def test_arm_builder_production_source_inputs_use_python_ordinal_order(
        self,
    ) -> None:
        # Execute the real production source-input path under Windows
        # PowerShell 5.1, then compare its serialized order and ledger with
        # Python's ordinal string ordering used by the independent verifier.
        completed = self.inspect_source_set(ARM_PACKAGE_BUILDER_PATH)
        self.assertEqual(0, completed.returncode, completed.stderr)
        observed = json.loads(completed.stdout)
        source_inputs = observed["sourceInputs"]
        paths = [item["path"] for item in source_inputs]

        self.assertEqual(sorted(paths), paths)
        self.assertEqual(len(paths), len(set(paths)))
        self.assertLess(
            paths.index("Directory.Build.props"),
            paths.index("deploy/arm64/run-cortex-mcp"),
        )
        ledger = "".join(
            f'{item["sha256"]}  {item["bytes"]}  {item["path"]}\n'
            for item in source_inputs
        )
        self.assertEqual(
            hashlib.sha256(ledger.encode("utf-8")).hexdigest(),
            observed["sourceSetSha256"],
        )

    def test_arm_builder_application_inventory_uses_python_ordinal_order(
        self,
    ) -> None:
        # Get-FileEvidence supplies manifest.application.files. Exercise that
        # production function under PowerShell 5.1 with names that differ
        # under culture-aware and ordinal comparison.
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            publish_root = root / "publish"
            payloads = {
                "admin/licenses/Npgsql-10.0.3-LICENSE.txt": b"npgsql\n",
                "admin/Microsoft.Extensions.DependencyInjection.Abstractions.dll": b"admin\n",
                "alpha0.dll": b"flat\n",
                "alpha/file.dll": b"nested\n",
                "PegasusCortex.Mcp.dll": b"mcp\n",
                "PegasusCortex.Mcp.Integrated.deps.json": b"deps\n",
            }
            for relative, payload in payloads.items():
                destination = publish_root.joinpath(*pathlib.PurePosixPath(relative).parts)
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(payload)

            source = ARM_PACKAGE_BUILDER_PATH.read_text(encoding="utf-8")
            prefix = source[: source.index("$resolvedProjectRoot = Resolve-SafeProjectRoot")]
            harness = root / "application-inventory-harness.ps1"
            harness.write_text(
                prefix
                + "\n$files = @(Get-ChildItem -LiteralPath $ProjectRoot -Recurse -File -Force)\n"
                + "@(Get-FileEvidence -Root $ProjectRoot -Files $files) "
                + "| ConvertTo-Json -Depth 5 -Compress\n",
                encoding="utf-8",
            )
            completed = subprocess.run(
                [
                    r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                    "-NoProfile",
                    "-NonInteractive",
                    "-File",
                    str(harness),
                    "-ProjectRoot",
                    str(publish_root),
                ],
                cwd=root,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="strict",
                timeout=30,
                check=False,
            )
            self.assertEqual(0, completed.returncode, completed.stderr)
            observed = json.loads(completed.stdout)
            expected_paths = sorted(payloads)
            self.assertEqual(expected_paths, [item["path"] for item in observed])
            self.assertEqual(
                [
                    {
                        "path": relative,
                        "bytes": len(payloads[relative]),
                        "sha256": hashlib.sha256(payloads[relative]).hexdigest(),
                    }
                    for relative in expected_paths
                ],
                observed,
            )

    def test_arm_builder_declares_atomic_output_and_staged_runtime_contract(self) -> None:
        source = ARM_PACKAGE_BUILDER_PATH.read_text(encoding="utf-8-sig")
        for marker in (
            "InspectSourceSet",
            "Test-FullyQualifiedPath",
            "Resolve-SafeProjectRoot",
            "Assert-UnlinkedDirectoryChain",
            "Assert-SafeSourceLeaf",
            "Get-SafeTreeFiles",
            "Assert-CortexProjectRootIdentity",
            "Resolve-SafeOutputRoot",
            "Copy-AcceptedRuntimeArchive",
            "The immutable ARM64 package output already exists.",
            "The staged ARM64 runtime changed after its accepted one-time copy.",
            "[IO.Directory]::Move($stageOutput, $resolvedOutputRoot)",
            "Remove-OwnedStage",
        ):
            self.assertIn(marker, source)
        self.assertNotIn("[string]$RuntimeArchiveSha512", source)
        self.assertNotIn("[IO.Path]::IsPathFullyQualified", source)
        self.assertNotIn("Copy-Item -LiteralPath $runtimeArchive", source)
        self.assertEqual(1, source.count("[IO.Directory]::Move("))
        cleanup = source[
            source.index("function Remove-OwnedStage") : source.index("$resolvedProjectRoot")
        ]
        self.assertGreaterEqual(cleanup.count("Assert-SafeOutputParent"), 2)
        self.assertGreaterEqual(cleanup.count("Assert-UnlinkedDirectoryChain"), 2)

    def test_windows_powershell_staged_delivery_preflight_is_strictmode_safe(
        self,
    ) -> None:
        # Exercise the real flat-delivery guard and tar boundary under PS 5.1.
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            delivery = root / "delivery"
            delivery.mkdir()
            asset_names = (
                "application.tar.gz",
                "dotnet-runtime.tar.gz",
                "install_cortex_mcp_arm64.py",
                "run-cortex-mcp",
                "run-cortex-migrate",
                "smoke_cortex_mcp.py",
            )
            for name in asset_names:
                (delivery / name).write_bytes(name.encode("ascii"))

            source = ARM_PACKAGE_BUILDER_PATH.read_text(encoding="utf-8-sig")
            prefix = source[: source.index("$resolvedProjectRoot =")]
            harness = root / "delivery-preflight-harness.ps1"
            harness.write_text(
                prefix
                + "\n$manifestPath = Join-Path $ProjectRoot 'package-manifest.json'\n"
                + "Write-Utf8Json -Path $manifestPath -Value ([ordered]@{ schemaVersion = 3; status = 'fixture' })\n"
                + "[IO.File]::WriteAllText((Join-Path $ProjectRoot 'checksums.sha256'), "
                + "('0' * 64) + '  package-manifest.json' + \"`n\", [Text.ASCIIEncoding]::new())\n"
                + "$deliveryFiles = @(Get-ValidatedFlatDeliveryFiles -DeliveryRoot $ProjectRoot -ExpectedFileCount 8)\n"
                + "foreach ($deliveryFile in $deliveryFiles) { $null = Assert-GeneratedPathBudget -Path $deliveryFile.FullName }\n"
                + "& tar.exe -czf $OutputRoot -C $ProjectRoot .\n"
                + "if ($LASTEXITCODE -ne 0) { throw ('Outer package archive creation failed with exit code ' + $LASTEXITCODE + '.') }\n"
                + "$archiveItem = Get-Item -LiteralPath $OutputRoot -Force\n"
                + "if ($archiveItem.PSIsContainer -or -not ($archiveItem -is [IO.FileInfo]) -or "
                + "(($archiveItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { "
                + "throw 'The staged outer package is not one direct regular file.' }\n"
                + "[ordered]@{ fileCount = $deliveryFiles.Count; names = @($deliveryFiles | "
                + "Sort-Object Name | ForEach-Object Name); archiveBytes = $archiveItem.Length } "
                + "| ConvertTo-Json -Depth 3 -Compress\n",
                encoding="utf-8",
            )
            archive = root / "package.tar.gz"
            command = [
                r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                "-NoProfile",
                "-NonInteractive",
                "-File",
                str(harness),
                "-ProjectRoot",
                str(delivery),
                "-OutputRoot",
                str(archive),
            ]
            completed = subprocess.run(
                command,
                cwd=root,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="strict",
                timeout=30,
                check=False,
            )
            self.assertEqual(0, completed.returncode, completed.stderr)
            observed = json.loads(completed.stdout)
            expected_names = sorted((*asset_names, "checksums.sha256", "package-manifest.json"))
            self.assertEqual(8, observed["fileCount"])
            self.assertEqual(expected_names, observed["names"])
            self.assertGreater(observed["archiveBytes"], 0)
            with tarfile.open(archive, "r:gz") as package:
                members = [member for member in package.getmembers() if member.isfile()]
            self.assertEqual(expected_names, sorted(pathlib.PurePosixPath(member.name).name for member in members))

            # A directory used to reach the broken `$deliveryEntries |` clause;
            # prove the isolated guard rejects it before tar is invoked.
            (delivery / "nested").mkdir()
            rejected = subprocess.run(
                [*command[:-1], str(root / "rejected.tar.gz")],
                cwd=root,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="strict",
                timeout=30,
                check=False,
            )
            self.assertNotEqual(0, rejected.returncode)
            self.assertIn("expected flat 8-file set", rejected.stderr)
            self.assertFalse((root / "rejected.tar.gz").exists())

    def test_package_verifier_rejects_linked_or_non_regular_delivery_entries(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            delivery = root / "delivery"
            delivery.mkdir()
            expected_assets = {"asset.bin"}
            for name in ("package-manifest.json", "checksums.sha256", "asset.bin"):
                (delivery / name).write_bytes(name.encode("ascii"))

            inventory = PACKAGE_VERIFIER.verify_delivery_root(delivery, expected_assets)
            self.assertEqual(set(inventory), {
                "package-manifest.json",
                "checksums.sha256",
                "asset.bin",
            })

            asset = delivery / "asset.bin"
            asset.unlink()
            asset.mkdir()
            with self.assertRaisesRegex(RuntimeError, "non-regular entry"):
                PACKAGE_VERIFIER.verify_delivery_root(delivery, expected_assets)
            asset.rmdir()

            outside = root / "outside.bin"
            outside.write_bytes(b"outside")
            try:
                asset.symlink_to(outside)
            except OSError:
                asset.write_bytes(b"ordinary")
                original = PACKAGE_VERIFIER.is_link_or_reparse
                with mock.patch.object(
                    PACKAGE_VERIFIER,
                    "is_link_or_reparse",
                    side_effect=lambda path: path == asset or original(path),
                ):
                    with self.assertRaisesRegex(RuntimeError, "link, reparse point"):
                        PACKAGE_VERIFIER.verify_delivery_root(delivery, expected_assets)
            else:
                with self.assertRaisesRegex(RuntimeError, "link, reparse point"):
                    PACKAGE_VERIFIER.verify_delivery_root(delivery, expected_assets)

    def test_package_verifier_binds_published_entry_as_regular_unlinked_file(self) -> None:
        source = (
            PROJECT_ROOT / "tools" / "Test-CortexMcpArm64Package.py"
        ).read_text(encoding="utf-8")
        self.assertIn(
            "integrated_identity = exact_file_identity(integrated_assembly)",
            source,
        )
        self.assertIn("regular_tree_inventory(portable_publish)", source)
        self.assertIn("verify_portable_publish_unchanged(", source)
        self.assertNotIn("if sha256(integrated_assembly)", source)

    def test_package_verifier_rejects_nonordinal_canonical_manifest_arrays(
        self,
    ) -> None:
        cases = (
            (
                "Package source-input evidence",
                [
                    {"path": "deploy/arm64/run-cortex-mcp"},
                    {"path": "Directory.Build.props"},
                ],
            ),
            (
                "The integrated application inventory",
                [
                    {"path": "admin/licenses/Npgsql-10.0.3-LICENSE.txt"},
                    {
                        "path": (
                            "admin/"
                            "Microsoft.Extensions.DependencyInjection.Abstractions.dll"
                        )
                    },
                ],
            ),
        )
        for field, records in cases:
            with self.subTest(field=field):
                with self.assertRaisesRegex(
                    RuntimeError,
                    rf"{re.escape(field)} must be deterministically path-sorted",
                ):
                    PACKAGE_VERIFIER.require_ordinal_path_order(records, field)
                PACKAGE_VERIFIER.require_ordinal_path_order(
                    sorted(records, key=lambda item: item["path"]),
                    field,
                )

        source = (
            PROJECT_ROOT / "tools" / "Test-CortexMcpArm64Package.py"
        ).read_text(encoding="utf-8")
        self.assertIn(
            'require_ordinal_path_order(source_inputs, "Package source-input evidence")',
            source,
        )
        self.assertIn(
            "require_ordinal_path_order(\n"
            "            application_files,\n"
            '            "The integrated application inventory",',
            source,
        )

    def test_package_verifier_rejects_linked_portable_publish_entries(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            publish = root / "portable-publish"
            nested = publish / "admin"
            nested.mkdir(parents=True)
            (publish / "entry.dll").write_bytes(b"entry")
            (nested / "service.dll").write_bytes(b"service")
            inventory = PACKAGE_VERIFIER.regular_tree_inventory(publish)
            self.assertEqual({"entry.dll", "admin/service.dll"}, set(inventory))

            outside = root / "outside.dll"
            outside.write_bytes(b"outside")
            linked = nested / "linked.dll"
            try:
                linked.symlink_to(outside)
            except OSError:
                linked.write_bytes(b"ordinary")
                original = PACKAGE_VERIFIER.is_link_or_reparse
                with mock.patch.object(
                    PACKAGE_VERIFIER,
                    "is_link_or_reparse",
                    side_effect=lambda path: path == linked or original(path),
                ):
                    with self.assertRaisesRegex(RuntimeError, "link or reparse point"):
                        PACKAGE_VERIFIER.regular_tree_inventory(publish)
            else:
                with self.assertRaisesRegex(RuntimeError, "link or reparse point"):
                    PACKAGE_VERIFIER.regular_tree_inventory(publish)

    def test_package_verifier_replays_portable_publish_after_execution(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            publish = pathlib.Path(temporary) / "portable-publish"
            publish.mkdir()
            entry = publish / "PegasusCortex.Mcp.Integrated.dll"
            entry.write_bytes(b"entry")
            inventory = PACKAGE_VERIFIER.regular_tree_inventory(publish)
            identity = PACKAGE_VERIFIER.exact_file_identity(entry)
            PACKAGE_VERIFIER.verify_portable_publish_unchanged(
                publish,
                inventory,
                entry,
                identity,
            )

            entry.unlink()
            outside = pathlib.Path(temporary) / "outside.dll"
            outside.write_bytes(b"entry")
            try:
                entry.symlink_to(outside)
            except OSError:
                entry.write_bytes(b"entry")
            with self.assertRaisesRegex(RuntimeError, "changed during verification"):
                PACKAGE_VERIFIER.verify_portable_publish_unchanged(
                    publish,
                    inventory,
                    entry,
                    identity,
                )

    def test_arm_evidence_fixture_declares_both_transport_profiles(self) -> None:
        source = ARM_EVIDENCE_BUILDER_PATH.read_text(encoding="utf-8-sig")

        # This focused source-contract check keeps the expensive evidence builder's
        # emitted manifest truthful without running a package build in unit tests.
        expected_fragments = (
            "disposition = 'offline-default-explicit-live-opt-in-trusted-loopback-or-private-pki-validation'",
            "postgresqlFixtureTransport = [ordered]@{",
            "selectionVariable = 'PEGASUS_CORTEX_TLS_POLICY'",
            "defaultProfile = 'verify-full'",
            "id = 'verify-full'",
            "id = 'trusted-loopback'",
            "transportProfile = 'verify-full'",
            "requiredOnlyWhenSelected = $true",
            "postgresAllowedTransportProfiles = @('verify-full', 'trusted-loopback')",
            "postgresRevocationEvidenceRequiredForProfiles = @('verify-full')",
            "postgresRevocationEvidenceForbiddenForProfiles = @('trusted-loopback')",
        )
        for fragment in expected_fragments:
            self.assertIn(fragment, source)
        self.assertNotIn("postgresRevocationEvidenceRequired = $true", source)

    def test_independent_verifier_binds_version_and_legacy_projection(self) -> None:
        version = json.loads((PROJECT_ROOT / "VERSION.json").read_text(encoding="utf-8"))
        manifest = {
            "releaseVersion": version["releaseVersion"],
            "packageVersion": version["nuGetVersion"],
        }
        PACKAGE_VERIFIER.verify_version_authorities(PROJECT_ROOT, manifest)
        PACKAGE_VERIFIER.verify_legacy_rollback_projection(INSTALLER, PROJECT_ROOT)

    def test_smoke_contract_matches_integrated_runtime_surfaces(self) -> None:
        smoke_path = PROJECT_ROOT / "deploy" / "arm64" / "smoke_cortex_mcp.py"

        PACKAGE_VERIFIER.verify_smoke_tool_contract(smoke_path)
        smoke = load_module("cortex_arm64_smoke_contract", smoke_path)

        self.assertTrue(
            smoke.EXPECTED_ACCESS_BY_SURFACE["main"]["projectContinuityRuntime"]
        )
        self.assertFalse(
            smoke.EXPECTED_ACCESS_BY_SURFACE["auditor"]["projectContinuityRuntime"]
        )
        self.assertEqual(
            3,
            len(smoke.EXPECTED_RESOURCE_URIS_BY_SURFACE["main"]),
        )
        self.assertEqual(
            2,
            len(smoke.EXPECTED_RESOURCE_URIS_BY_SURFACE["auditor"]),
        )
        self.assertEqual(
            {"cortex://default-library/records/{recordId}"},
            smoke.EXPECTED_RESOURCE_TEMPLATE_URIS_BY_SURFACE["main"],
        )
        self.assertEqual(
            set(),
            smoke.EXPECTED_RESOURCE_TEMPLATE_URIS_BY_SURFACE["auditor"],
        )
        self.assertIn(
            "authoritative project files",
            smoke.EXPECTED_INITIALIZE_INSTRUCTION_MARKERS,
        )
        self.assertIn("mode broad", smoke.EXPECTED_INITIALIZE_INSTRUCTION_MARKERS)
        self.assertEqual(
            {"main": 60, "auditor": 30},
            INSTALLER.SMOKE_TIMEOUT_SECONDS_BY_SURFACE,
        )
        PACKAGE_VERIFIER.verify_integrated_source_contract(PROJECT_ROOT)

    def test_smoke_contract_rejects_stale_all_false_project_runtime(self) -> None:
        stale_access = {
            surface: dict(access)
            for surface, access in PACKAGE_VERIFIER.EXPECTED_ACCESS_BY_SURFACE.items()
        }
        stale_access["main"]["projectContinuityRuntime"] = False

        with tempfile.TemporaryDirectory() as temporary:
            stale_smoke = pathlib.Path(temporary) / "stale_smoke.py"
            stale_smoke.write_text(
                "\n".join(
                    (
                        f"EXPECTED_MAIN_TOOLS = {PACKAGE_VERIFIER.EXPECTED_MAIN_TOOLS!r}",
                        f"EXPECTED_AUDITOR_TOOLS = {PACKAGE_VERIFIER.EXPECTED_AUDITOR_TOOLS!r}",
                        "EXPECTED_INITIALIZE_INSTRUCTION_MARKERS = "
                        f"{PACKAGE_VERIFIER.EXPECTED_INITIALIZE_INSTRUCTION_MARKERS!r}",
                        f"EXPECTED_ACCESS_BY_SURFACE = {stale_access!r}",
                        "EXPECTED_STORAGE_KIND = "
                        f"{PACKAGE_VERIFIER.EXPECTED_STORAGE_KIND!r}",
                        "EXPECTED_FEATURES_BY_SURFACE = "
                        f"{PACKAGE_VERIFIER.EXPECTED_FEATURES_BY_SURFACE!r}",
                        "EXPECTED_RESOURCE_URIS_BY_SURFACE = "
                        f"{PACKAGE_VERIFIER.EXPECTED_RESOURCE_URIS_BY_SURFACE!r}",
                        "EXPECTED_RESOURCE_TEMPLATE_URIS_BY_SURFACE = "
                        f"{PACKAGE_VERIFIER.EXPECTED_RESOURCE_TEMPLATE_URIS_BY_SURFACE!r}",
                        'SMOKE_SEARCH_ARGUMENTS = {"mode": "broad"}',
                    )
                )
                + "\n",
                encoding="utf-8",
            )
            with self.assertRaisesRegex(RuntimeError, "stale MCP tool contract"):
                PACKAGE_VERIFIER.verify_smoke_tool_contract(stale_smoke)

    def test_smoke_executes_exact_main_and_auditor_surface_contracts(self) -> None:
        smoke = load_module(
            "cortex_arm64_smoke_behavior",
            PROJECT_ROOT / "deploy" / "arm64" / "smoke_cortex_mcp.py",
        )
        guide_content = "bounded Cortex continuity guide"
        guide_hash = hashlib.sha256(guide_content.encode("utf-8")).hexdigest()
        entry_hash = "a" * 64

        def response(request_id: int, result: dict[str, object]) -> dict[str, object]:
            return {"jsonrpc": "2.0", "id": request_id, "result": result}

        def structured_response(
            request_id: int,
            value: dict[str, object],
        ) -> dict[str, object]:
            return response(request_id, {"structuredContent": value})

        def first_session(surface: str) -> dict[int, dict[str, object]]:
            resources = [
                {"uri": uri}
                for uri in smoke.EXPECTED_RESOURCE_URIS_BY_SURFACE[surface]
            ]
            templates = [
                {"uriTemplate": uri}
                for uri in smoke.EXPECTED_RESOURCE_TEMPLATE_URIS_BY_SURFACE[surface]
            ]
            guide = {
                "guideVersion": "1.7.0",
                "source": {"sha256": guide_hash},
                "authority": {
                    "projectFilesRemainAuthoritative": True,
                    "grantsOperationalAuthority": False,
                    "projectContinuityRuntime": False,
                },
                "freshness": {"state": "current"},
                "content": guide_content,
            }
            values: dict[int, dict[str, object]] = {
                1: response(
                    1,
                    {
                        "protocolVersion": smoke.PROTOCOL_VERSION,
                        "instructions": (
                            "Recovery uses authoritative project files. Read "
                            f"{smoke.GUIDE_URI}. Normal questions use mode broad."
                        ),
                    },
                ),
                2: response(
                    2,
                    {
                        "tools": [
                            {"name": name}
                            for name in (
                                smoke.EXPECTED_MAIN_TOOLS
                                if surface == "main"
                                else smoke.EXPECTED_AUDITOR_TOOLS
                            )
                        ]
                    },
                ),
                3: response(3, {"resources": resources}),
                4: response(4, {"resourceTemplates": templates}),
                5: response(
                    5,
                    {
                        "contents": [
                            {
                                "uri": smoke.GUIDE_URI,
                                "mimeType": "application/json",
                                "text": json.dumps(guide),
                            }
                        ]
                    },
                ),
                6: structured_response(
                    6,
                    {
                        "resources": {"guide": smoke.GUIDE_URI},
                        "access": smoke.EXPECTED_ACCESS_BY_SURFACE[surface],
                        "storageKind": smoke.EXPECTED_STORAGE_KIND,
                        "features": smoke.EXPECTED_FEATURES_BY_SURFACE[surface],
                    },
                ),
                7: structured_response(
                    7,
                    {
                        "serverBuild": {
                            "basis": "entry-assembly-bytes",
                            "fingerprint": f"sha256:{entry_hash}",
                        },
                        "hybrid": {
                            "state": "ready",
                            "storageKind": smoke.EXPECTED_STORAGE_KIND,
                            "writes": True,
                        },
                        "storageKind": smoke.EXPECTED_STORAGE_KIND,
                        "features": smoke.EXPECTED_FEATURES_BY_SURFACE[surface],
                    },
                ),
            }
            if surface == "main":
                values[8] = structured_response(
                    8,
                    {
                        "records": [
                            {
                                "recordId": "record-1",
                                "matchedTerms": ["cortex", "pegasus"],
                            }
                        ],
                        "retrieval": {
                            "route": "general-collective",
                            "queryTermCount": 4,
                            "queriedSourceCount": 2,
                            "partialFailureCount": 0,
                        },
                        "query": {"mode": "broad"},
                    },
                )
            return values

        with tempfile.TemporaryDirectory() as temporary:
            command = pathlib.Path(temporary) / "candidate-command"
            command.write_bytes(b"candidate")
            for surface in ("main", "auditor"):
                sessions: list[dict[int, dict[str, object]]] = [first_session(surface)]
                if surface == "main":
                    sessions.append(
                        {
                            2: structured_response(
                                2,
                                {"recordId": "record-1", "content": "exact"},
                            )
                        }
                    )
                output = io.StringIO()
                with (
                    mock.patch.object(smoke, "run_session", side_effect=sessions) as run,
                    mock.patch.object(
                        sys,
                        "argv",
                        [
                            "smoke_cortex_mcp.py",
                            "--command",
                            str(command),
                            "--expected-guide-sha256",
                            guide_hash,
                            "--expected-entry-assembly-sha256",
                            entry_hash,
                            "--expected-surface",
                            surface,
                        ],
                    ),
                    contextlib.redirect_stdout(output),
                ):
                    self.assertEqual(0, smoke.main())
                summary = json.loads(output.getvalue())
                self.assertEqual(surface, summary["surface"])
                self.assertEqual(
                    len(smoke.EXPECTED_RESOURCE_URIS_BY_SURFACE[surface]),
                    summary["resourceCount"],
                )
                self.assertEqual(
                    len(smoke.EXPECTED_RESOURCE_TEMPLATE_URIS_BY_SURFACE[surface]),
                    summary["resourceTemplateCount"],
                )
                self.assertEqual(2 if surface == "main" else 1, run.call_count)

    def test_cli_exposes_prepare_activate_and_no_combined_install(self) -> None:
        with mock.patch.object(
            sys,
            "argv",
            [
                "installer",
                "activate",
                "--target-version",
                "1.2026.08.03.10.20.A",
                "--expected-manifest-sha256",
                "a" * 64,
                "--prepared-plan-id",
                "prepare-candidate",
                "--plan-id",
                "activate-candidate",
            ],
        ):
            parsed = INSTALLER.parse_args()
        self.assertEqual("activate", parsed.operation)
        self.assertEqual("prepare-candidate", parsed.prepared_plan_id)

        with mock.patch.object(
            sys,
            "argv",
            [
                "installer",
                "rollback",
                "--target-version",
                "1.2026.07.27.13.20.A",
                "--expected-manifest-sha256",
                "e" * 64,
                "--expected-current-version",
                "1.2026.08.03.11.18.A",
                "--expected-current-identity-sha256",
                "f" * 64,
                "--expected-target-tree-sha256",
                "d" * 64,
                "--expected-runtime-configuration-sha256",
                "c" * 64,
                "--plan-id",
                "rollback-predecessor",
            ],
        ):
            rollback = INSTALLER.parse_args()
        self.assertEqual("rollback", rollback.operation)
        self.assertEqual("e" * 64, rollback.expected_manifest_sha256)
        self.assertEqual("d" * 64, rollback.expected_target_tree_sha256)
        self.assertEqual(
            "c" * 64,
            rollback.expected_runtime_configuration_sha256,
        )

        with (
            mock.patch.object(sys, "argv", ["installer", "install"]),
            contextlib.redirect_stderr(io.StringIO()),
        ):
            with self.assertRaises(SystemExit):
                INSTALLER.parse_args()

    def test_package_root_rejects_files_outside_sealed_allowlist(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            package_root = pathlib.Path(temporary)
            asset_names = (
                "app.tar.gz",
                "runtime.tar.gz",
                "install_cortex_mcp_arm64.py",
                "smoke_cortex_mcp.py",
                "run-cortex-mcp",
                "run-cortex-migrate",
            )
            assets = []
            for index, name in enumerate(asset_names):
                path = package_root / name
                path.write_bytes(f"sealed-{index}".encode("utf-8"))
                assets.append(
                    {
                        "name": name,
                        "bytes": path.stat().st_size,
                        "sha256": INSTALLER.sha256_file(path),
                    }
                )
            manifest_path = package_root / "package-manifest.json"
            manifest_path.write_text("{}\n", encoding="utf-8")
            manifest = {
                "application": {"archiveName": "app.tar.gz"},
                "runtime": {"archiveName": "runtime.tar.gz"},
                "assets": assets,
            }
            (package_root / "checksums.sha256").write_text(
                "\n".join(
                    [f"{item['sha256']}  {item['name']}" for item in assets]
                    + [
                        f"{INSTALLER.sha256_file(manifest_path)}  package-manifest.json"
                    ]
                )
                + "\n",
                encoding="utf-8",
            )
            self.assertEqual(
                {name: package_root / name for name in asset_names},
                INSTALLER.require_package_assets(package_root, manifest),
            )

            (package_root / "residue.txt").write_text("unsealed", encoding="utf-8")
            with self.assertRaisesRegex(RuntimeError, "outside the sealed manifest"):
                INSTALLER.require_package_assets(package_root, manifest)
            (package_root / "residue.txt").unlink()

            checksum_path = package_root / "checksums.sha256"
            valid_checksums = checksum_path.read_text(encoding="utf-8")
            checksum_path.write_text(
                "\n".join(reversed(valid_checksums.splitlines())) + "\n",
                encoding="utf-8",
            )
            with self.assertRaisesRegex(RuntimeError, "checksum index"):
                INSTALLER.require_package_assets(package_root, manifest)
            checksum_path.write_text(valid_checksums, encoding="utf-8")

            (package_root / "unexpected-directory").mkdir()
            with self.assertRaisesRegex(RuntimeError, "directory, link, or special"):
                INSTALLER.require_package_assets(package_root, manifest)
            (package_root / "unexpected-directory").rmdir()

            checksum_path.unlink()
            checksum_path.mkdir()
            with self.assertRaisesRegex(RuntimeError, "directory, link, or special"):
                INSTALLER.require_package_assets(package_root, manifest)

    def test_package_policy_rejects_a_writable_ancestor(self) -> None:
        safe_directory = mock.Mock(
            st_mode=stat.S_IFDIR | 0o755,
            st_uid=0,
        )
        writable_directory = mock.Mock(
            st_mode=stat.S_IFDIR | 0o777,
            st_uid=0,
        )
        safe_file = mock.Mock(
            st_mode=stat.S_IFREG | 0o644,
            st_uid=0,
        )
        child = mock.Mock()
        child.lstat.return_value = safe_file
        parent = mock.Mock()
        parent.lstat.return_value = safe_directory
        anchor = mock.Mock()
        anchor.lstat.return_value = safe_directory
        package_root = mock.Mock()
        package_root.lstat.return_value = safe_directory
        package_root.parents = (parent, anchor)
        package_root.iterdir.return_value = iter((child,))
        with (
            mock.patch.object(INSTALLER.os, "name", "posix"),
            mock.patch.object(INSTALLER, "require_generated_path_budget"),
        ):
            INSTALLER.require_package_policy(package_root)
            parent.lstat.return_value = writable_directory
            with self.assertRaisesRegex(RuntimeError, "mutable"):
                INSTALLER.require_package_policy(package_root)

    def test_deployment_lock_rejects_a_concurrent_operation(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            fake_fcntl = SimpleNamespace(
                LOCK_EX=1,
                LOCK_NB=2,
                LOCK_UN=4,
                flock=mock.Mock(side_effect=(BlockingIOError(), None)),
            )
            with (
                mock.patch.object(INSTALLER, "fcntl", fake_fcntl),
                mock.patch.object(INSTALLER.os, "geteuid", return_value=0, create=True),
                mock.patch.object(INSTALLER.os, "open", return_value=9),
                mock.patch.object(
                    INSTALLER.os,
                    "fstat",
                    return_value=mock.Mock(
                        st_mode=stat.S_IFREG | 0o600,
                        st_uid=0,
                    ),
                ),
                mock.patch.object(INSTALLER.os, "close") as close,
            ):
                with self.assertRaisesRegex(RuntimeError, "already active"):
                    with INSTALLER.deployment_operation_lock(root):
                        self.fail("The concurrent operation acquired the lock.")
            close.assert_called_once_with(9)

    def test_package_root_path_rejects_relative_and_linked_components(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            target = root / "target"
            target.mkdir()
            self.assertEqual(
                target,
                INSTALLER.validate_existing_unlinked_directory(target, "Package root"),
            )
            with self.assertRaisesRegex(RuntimeError, "must be absolute"):
                INSTALLER.validate_existing_unlinked_directory(
                    pathlib.Path("relative-package"),
                    "Package root",
                )

            direct = root / "direct-link"
            ancestor = root / "ancestor-link"
            try:
                direct.symlink_to(target, target_is_directory=True)
                ancestor.symlink_to(root, target_is_directory=True)
            except OSError:
                self.skipTest("directory symbolic-link creation is unavailable")
            with self.assertRaisesRegex(RuntimeError, "symbolic link"):
                INSTALLER.validate_existing_unlinked_directory(direct, "Package root")
            with self.assertRaisesRegex(RuntimeError, "symbolic link"):
                INSTALLER.validate_existing_unlinked_directory(
                    ancestor / "target",
                    "Package root",
                )

    def test_supplied_paths_reject_noncanonical_or_nonportable_segments(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            base = pathlib.Path(temporary)
            cases = (
                (base / "safe" / ".." / "escape", "canonical absolute path"),
                (base / "state:alternate", "unsafe path segment"),
                (base / "CON.txt", "reserved path segment"),
                (base / "release.", "unsafe path segment"),
                (base / "release ", "unsafe path segment"),
            )
            for supplied, expected_error in cases:
                with self.subTest(path=str(supplied)):
                    with self.assertRaisesRegex(RuntimeError, expected_error):
                        INSTALLER.require_canonical_supplied_path(
                            str(supplied),
                            "Test path",
                        )

    def test_receipt_is_published_complete_private_and_no_replace(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            receipt = pathlib.Path(temporary) / "receipt.json"
            original_link = os.link

            def publish(source: pathlib.Path, destination: pathlib.Path, **kwargs: Any) -> None:
                self.assertFalse(pathlib.Path(destination).exists())
                self.assertEqual(
                    {"status": "prepared"},
                    json.loads(pathlib.Path(source).read_text(encoding="utf-8")),
                )
                original_link(source, destination, **kwargs)

            with (
                mock.patch.object(INSTALLER.os, "link", side_effect=publish),
                mock.patch.object(INSTALLER, "fsync_directory") as fsync_directory,
            ):
                INSTALLER.write_json_no_replace(receipt, {"status": "prepared"})
            self.assertEqual({"status": "prepared"}, json.loads(receipt.read_text()))
            if os.name == "posix":
                self.assertEqual(0o600, stat.S_IMODE(receipt.stat().st_mode))
            self.assertEqual(1, fsync_directory.call_count)
            self.assertEqual([], list(receipt.parent.glob(f".{receipt.name}.*.tmp")))

            original = receipt.read_bytes()
            with self.assertRaises(FileExistsError):
                INSTALLER.write_json_no_replace(receipt, {"status": "changed"})
            self.assertEqual(original, receipt.read_bytes())
            self.assertEqual([], list(receipt.parent.glob(f".{receipt.name}.*.tmp")))

    def test_receipt_publication_failure_leaves_no_final_or_temporary_file(self) -> None:
        for failure in ("link", "directory-fsync"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                receipt = pathlib.Path(temporary) / "receipt.json"
                if failure == "link":
                    link_patch = mock.patch.object(
                        INSTALLER.os,
                        "link",
                        side_effect=OSError("injected link failure"),
                    )
                    fsync_patch = mock.patch.object(INSTALLER, "fsync_directory")
                else:
                    link_patch = contextlib.nullcontext()
                    calls = 0

                    def fail_first_directory_fsync(_: pathlib.Path) -> None:
                        nonlocal calls
                        calls += 1
                        if calls == 1:
                            raise OSError("injected directory fsync failure")

                    fsync_patch = mock.patch.object(
                        INSTALLER,
                        "fsync_directory",
                        side_effect=fail_first_directory_fsync,
                    )
                with link_patch, fsync_patch:
                    with self.assertRaises(OSError):
                        INSTALLER.write_json_no_replace(receipt, {"status": "prepared"})
                self.assertFalse(receipt.exists())
                self.assertEqual([], list(receipt.parent.glob(f".{receipt.name}.*.tmp")))

    def test_protected_receipt_applies_ownership_and_mode_before_publication(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            receipt = pathlib.Path(temporary) / "receipt.json"
            with (
                mock.patch.object(INSTALLER.os, "fchown", create=True) as fchown,
                mock.patch.object(INSTALLER.os, "fchmod", create=True) as fchmod,
                mock.patch.object(INSTALLER, "fsync_directory"),
            ):
                INSTALLER.write_protected_json_no_replace(
                    receipt,
                    {"status": "prepared"},
                    group_id=42,
                )
            fchown.assert_called_once()
            self.assertEqual((0, 42), fchown.call_args.args[1:])
            self.assertEqual(0o640, fchmod.call_args.args[1])

    def test_atomic_selector_rejects_prepare_checkpoint_drift(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            component_root = pathlib.Path(temporary)
            target = component_root / "versions" / "1.2026.08.03.12.20.B"
            with (
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=("versions/1.2026.08.03.11.18.A", 1, 2, 3),
                ),
                mock.patch.object(INSTALLER.os, "symlink") as create_link,
            ):
                with self.assertRaisesRegex(RuntimeError, "approved checkpoint"):
                    INSTALLER.atomic_current_link(
                        component_root,
                        target,
                        "activate-successor",
                        expected_previous="versions/1.2026.07.27.13.20.A",
                    )
            create_link.assert_not_called()

    def test_atomic_selector_rejects_midflight_identity_change(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            component_root = pathlib.Path(temporary)
            target = component_root / "versions" / "1.2026.08.03.12.20.B"
            identity = ("versions/1.2026.07.27.13.20.A", 1, 2, 3)
            changed = ("versions/1.2026.07.27.13.20.A", 1, 99, 4)
            with (
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(identity, changed),
                ),
                mock.patch.object(INSTALLER.os, "symlink"),
                mock.patch.object(
                    pathlib.Path,
                    "exists",
                    side_effect=(False, True),
                ),
                mock.patch.object(pathlib.Path, "is_symlink", return_value=False),
                mock.patch.object(
                    pathlib.Path,
                    "lstat",
                    return_value=mock.Mock(
                        st_mode=stat.S_IFLNK | 0o777,
                        st_dev=5,
                        st_ino=6,
                        st_ctime_ns=7,
                    ),
                ),
                mock.patch.object(
                    INSTALLER.os,
                    "readlink",
                    return_value=os.path.relpath(target, component_root),
                ),
                mock.patch.object(INSTALLER.os, "replace") as replace,
            ):
                with self.assertRaisesRegex(RuntimeError, "during atomic selection"):
                    INSTALLER.atomic_current_link(
                        component_root,
                        target,
                        "activate-successor",
                        expected_previous=identity[0],
                    )
            replace.assert_not_called()

    def test_atomic_selector_accepts_native_rename_ctime_for_stable_object(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            component_root = pathlib.Path(temporary)
            target = component_root / "versions" / "1.2026.08.03.12.20.B"
            previous = "versions/1.2026.07.27.13.20.A"
            before = (previous, 1, 2, 3)
            relative_target = os.path.relpath(target, component_root)
            staged = (relative_target, 5, 6, 7)
            installed = (staged[0], staged[1], staged[2], 99)
            with (
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(before, before, installed, installed),
                ),
                mock.patch.object(INSTALLER.os, "symlink"),
                mock.patch.object(INSTALLER.os, "readlink", return_value=staged[0]),
                mock.patch.object(
                    pathlib.Path,
                    "exists",
                    side_effect=(False, True),
                ),
                mock.patch.object(pathlib.Path, "is_symlink", return_value=False),
                mock.patch.object(
                    pathlib.Path,
                    "lstat",
                    return_value=mock.Mock(
                        st_mode=stat.S_IFLNK | 0o777,
                        st_dev=staged[1],
                        st_ino=staged[2],
                        st_ctime_ns=staged[3],
                    ),
                ),
                mock.patch.object(INSTALLER.os, "replace") as replace,
                mock.patch.object(INSTALLER, "fsync_directory") as fsync,
            ):
                result = INSTALLER.atomic_current_link(
                    component_root,
                    target,
                    "activate-successor",
                    expected_previous=previous,
                    expected_identity=before,
                )
            self.assertEqual(previous, result)
            replace.assert_called_once_with(
                component_root / ".current-activate-successor",
                component_root / "current",
            )
            self.assertEqual(2, fsync.call_count)
            self.assertTrue(INSTALLER.same_selector_object(staged, installed))

    def test_atomic_selector_reuses_a_valid_crash_left_staging_link(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            component_root = pathlib.Path(temporary)
            version = "1.2026.08.03.12.20.B"
            target = component_root / "versions" / version
            previous = f"versions/{INSTALLER.LEGACY_ROLLBACK_RELEASE_VERSION}"
            before = (previous, 1, 2, 3)
            relative_target = os.path.relpath(target, component_root)
            staged = (relative_target, 5, 6, 7)
            installed = (staged[0], staged[1], staged[2], 8)
            with (
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(before, before, installed, installed),
                ),
                mock.patch.object(pathlib.Path, "exists", return_value=True),
                mock.patch.object(pathlib.Path, "is_symlink", return_value=True),
                mock.patch.object(
                    INSTALLER,
                    "require_selector_staging_link",
                    return_value=staged,
                ) as require_staging,
                mock.patch.object(INSTALLER.os, "symlink") as create_link,
                mock.patch.object(INSTALLER.os, "replace") as replace,
                mock.patch.object(INSTALLER, "fsync_directory") as fsync,
            ):
                self.assertEqual(
                    previous,
                    INSTALLER.atomic_current_link(
                        component_root,
                        target,
                        "activate-resume",
                        expected_previous=previous,
                        expected_identity=before,
                    ),
                )
            require_staging.assert_called_once_with(
                component_root / ".current-activate-resume",
                relative_target,
            )
            create_link.assert_not_called()
            replace.assert_called_once()
            fsync.assert_called_once_with(component_root)

    def test_selector_transition_receipts_bind_full_post_rename_identity(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        candidate = f"versions/{version}"
        expected_from = (previous, 1, 2, 3)
        selected = (candidate, 5, 6, 99)
        for operation in ("activate", "rollback"):
            with self.subTest(operation=operation), tempfile.TemporaryDirectory() as temporary:
                root = pathlib.Path(temporary)
                component_root = root / "apps" / "cortex"
                manifests_root = root / "manifests" / "cortex"
                component_root.mkdir(parents=True)
                manifests_root.mkdir(parents=True)
                target = component_root / "versions" / version
                with (
                    mock.patch.object(
                        INSTALLER,
                        "read_current_link_identity",
                        side_effect=(expected_from, selected, selected, selected),
                    ),
                    mock.patch.object(
                        INSTALLER.os.path,
                        "relpath",
                        return_value=candidate,
                    ),
                    mock.patch.object(
                        INSTALLER,
                        "atomic_current_link",
                        return_value=previous,
                    ) as select,
                    mock.patch.object(INSTALLER, "fsync_directory"),
                    mock.patch.object(INSTALLER.os, "fchown", create=True),
                ):
                    actual_identity, receipt_path = (
                        INSTALLER.reconcile_selector_transition(
                            component_root,
                            manifests_root,
                            operation,
                            version,
                            "selector-journal",
                            target,
                            expected_from,
                            "a" * 64,
                            "b" * 64,
                            42,
                        )
                    )
                self.assertEqual(selected, actual_identity)
                select.assert_called_once_with(
                    component_root,
                    target,
                    f"{operation}-selector-journal",
                    expected_previous=previous,
                    expected_identity=expected_from,
                )
                receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
                self.assertEqual(f"{operation}-selector", receipt["operation"])
                self.assertEqual(
                    INSTALLER.selector_identity_receipt(expected_from),
                    receipt["fromIdentity"],
                )
                self.assertEqual(
                    INSTALLER.selector_identity_receipt(selected),
                    receipt["currentIdentity"],
                )
                self.assertEqual(
                    INSTALLER.selector_identity_sha256(selected),
                    receipt["currentIdentitySha256"],
                )

    def test_selector_transition_rejects_current_identity_for_another_target(
        self,
    ) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = ("versions/1.2026.07.27.13.20.A", 1, 2, 3)
        expected_target = f"versions/{version}"
        wrong_identity = ("versions/1.2026.08.03.11.18.A", 5, 6, 7)
        with tempfile.TemporaryDirectory() as temporary:
            manifests = pathlib.Path(temporary) / "manifests"
            manifests.mkdir()
            receipt_path = INSTALLER.selector_transition_path(
                manifests,
                version,
                "target-binding",
                "activate",
            )
            receipt_path.write_text(
                json.dumps(
                    {
                        "schemaVersion": INSTALLER.PREPARED_RECEIPT_SCHEMA_VERSION,
                        "operation": "activate-selector",
                        "status": "committed",
                        "releaseVersion": version,
                        "planId": "target-binding",
                        "packageManifestSha256": "a" * 64,
                        "versionTreeSha256": "b" * 64,
                        "fromIdentity": INSTALLER.selector_identity_receipt(previous),
                        "fromIdentitySha256": INSTALLER.selector_identity_sha256(
                            previous
                        ),
                        "target": expected_target,
                        "currentIdentity": INSTALLER.selector_identity_receipt(
                            wrong_identity
                        ),
                        "currentIdentitySha256": INSTALLER.selector_identity_sha256(
                            wrong_identity
                        ),
                    }
                ),
                encoding="utf-8",
            )
            with mock.patch.object(INSTALLER, "require_protected_receipt"):
                with self.assertRaisesRegex(RuntimeError, "receipt is invalid"):
                    INSTALLER.require_selector_transition(
                        receipt_path,
                        42,
                        "activate",
                        version,
                        "target-binding",
                        previous,
                        expected_target,
                        "a" * 64,
                        "b" * 64,
                    )

    def test_legacy_rollback_uses_preserved_schema1_smoke_contract(self) -> None:
        fixture = predecessor_fixture()
        manifest = fixture["manifest"]
        INSTALLER.require_legacy_rollback_manifest(manifest)
        self.assertEqual(
            fixture["sourceManifestSha256"],
            INSTALLER.LEGACY_ROLLBACK_MANIFEST_SHA256,
        )

        with tempfile.TemporaryDirectory() as temporary:
            version_root = pathlib.Path(temporary) / manifest["releaseVersion"]
            (version_root / "app").mkdir(parents=True)
            entry = version_root / "app" / manifest["application"]["entryAssembly"]
            entry.write_bytes(b"fixture-entry")
            manifest_path = version_root / "package-manifest.json"
            manifest_path.write_text("{}\n", encoding="utf-8")
            summary = {
                "status": "pass",
                "hybridReady": True,
                "guideSha256": manifest["guide"]["sha256"],
                "entryAssemblyFingerprint": (
                    f"sha256:{manifest['application']['entryAssemblySha256']}"
                ),
                "networkUsed": True,
            }
            completed = mock.Mock(
                returncode=0,
                stdout=json.dumps(summary),
                stderr="",
            )

            def installed_asset(
                _: pathlib.Path,
                __: dict[str, Any],
                ___: str,
                relative_path: str,
            ) -> pathlib.Path:
                return version_root / relative_path

            def file_hash(path: pathlib.Path) -> str:
                if path == manifest_path:
                    return fixture["sourceManifestSha256"]
                if path == entry:
                    return manifest["application"]["entryAssemblySha256"]
                raise AssertionError(f"Unexpected hash input: {path}")

            with (
                mock.patch.object(
                    INSTALLER,
                    "require_installed_asset",
                    side_effect=installed_asset,
                ),
                mock.patch.object(INSTALLER, "sha256_file", side_effect=file_hash),
                mock.patch.object(
                    INSTALLER,
                    "version_tree_file_count",
                    return_value=INSTALLER.LEGACY_ROLLBACK_VERSION_TREE_FILE_COUNT,
                ),
                mock.patch.object(
                    INSTALLER,
                    "version_tree_sha256",
                    return_value=INSTALLER.LEGACY_ROLLBACK_VERSION_TREE_SHA256,
                ),
                mock.patch.object(
                    INSTALLER,
                    "pwd",
                    mock.Mock(getpwnam=mock.Mock(return_value=mock.Mock())),
                ),
                mock.patch.object(
                    INSTALLER,
                    "run_bounded_subprocess",
                    return_value=completed,
                ) as run,
            ):
                contract, result = INSTALLER.run_rollback_smoke(
                    version_root,
                    manifest_path,
                    manifest,
                    pathlib.Path("/opt/pegasus"),
                    fixture["sourceManifestSha256"],
                )

            self.assertEqual("legacy-schema-1-preserved-predecessor", contract)
            self.assertEqual(summary, result)
            command = run.call_args.args[0]
            self.assertEqual(
                [INSTALLER.TRUSTED_RUNUSER, "-u", "codexsvc", "--"],
                command[:4],
            )
            self.assertEqual(fixture["sourceSmokeOptions"], [
                item for item in command if item != "--" and item.startswith("--")
            ])
            self.assertNotIn("--expected-surface", command)
            self.assertEqual(
                INSTALLER.LEGACY_ROLLBACK_SMOKE_TIMEOUT_SECONDS,
                run.call_args.args[1],
            )

    def test_legacy_predecessor_full_tree_is_reproducible_and_tamper_evident(
        self,
    ) -> None:
        fixture = predecessor_fixture()
        package_path = predecessor_package_path()
        if not package_path.is_file():
            self.skipTest(
                "Set "
                f"{PREDECESSOR_PACKAGE_ENVIRONMENT_VARIABLE} to the exact "
                "predecessor archive when the canonical artifact is unavailable."
            )
        PACKAGE_VERIFIER.verify_legacy_predecessor_package(
            INSTALLER,
            PROJECT_ROOT,
            package_path,
        )
        with tempfile.TemporaryDirectory() as temporary:
            scratch = pathlib.Path(temporary)
            outer = scratch / "outer"
            INSTALLER.extract_regular_archive(package_path, outer)
            manifest = json.loads(
                (outer / "package-manifest.json").read_text(encoding="utf-8")
            )
            version_root = scratch / manifest["releaseVersion"]
            version_root.mkdir()
            INSTALLER.extract_regular_archive(
                outer / manifest["application"]["archiveName"],
                version_root / "app",
            )
            INSTALLER.extract_regular_archive(
                outer / manifest["runtime"]["archiveName"],
                version_root / "runtime",
            )
            (version_root / "tools").mkdir()
            for source_name, relative in (
                ("package-manifest.json", "package-manifest.json"),
                ("run-cortex-mcp", "run-cortex-mcp"),
                ("run-cortex-migrate", "run-cortex-migrate"),
                ("smoke_cortex_mcp.py", "tools/smoke_cortex_mcp.py"),
            ):
                (version_root / relative).write_bytes(
                    (outer / source_name).read_bytes()
                )

            self.assertEqual(
                fixture["derivedInstalledFileCount"],
                INSTALLER.version_tree_file_count(version_root),
            )
            self.assertEqual(
                fixture["derivedInstalledVersionTreeSha256"],
                INSTALLER.version_tree_sha256(version_root),
            )
            non_entry_dependency = next(
                path
                for path in (version_root / "app").rglob("*.dll")
                if path.name != "PegasusCortex.Mcp.dll"
            )
            runtime_file = next(
                path
                for path in (version_root / "runtime").rglob("*")
                if path.is_file() and path.name != "dotnet"
            )
            for path in (
                non_entry_dependency,
                runtime_file,
                version_root / "run-cortex-migrate",
            ):
                original = path.read_bytes()
                path.write_bytes(original + b"tamper")
                self.assertNotEqual(
                    fixture["derivedInstalledVersionTreeSha256"],
                    INSTALLER.version_tree_sha256(version_root),
                )
                path.write_bytes(original)
            extra = version_root / "unexpected.bin"
            extra.write_bytes(b"unexpected")
            self.assertEqual(
                fixture["derivedInstalledFileCount"] + 1,
                INSTALLER.version_tree_file_count(version_root),
            )
            self.assertNotEqual(
                fixture["derivedInstalledVersionTreeSha256"],
                INSTALLER.version_tree_sha256(version_root),
            )

    def test_rollback_target_rejects_tampered_smoke_and_launcher(self) -> None:
        version = "1.2026.08.03.12.20.B"
        entry_bytes = b"integrated-entry"
        smoke_bytes = b"sealed-smoke"
        launcher_bytes = b"sealed-launcher"
        with tempfile.TemporaryDirectory() as temporary:
            version_root = pathlib.Path(temporary) / version
            (version_root / "tools").mkdir(parents=True)
            (version_root / "app").mkdir()
            entry = version_root / "app" / "PegasusCortex.Mcp.Integrated.dll"
            smoke = version_root / "tools" / "smoke_cortex_mcp.py"
            launcher = version_root / "run-cortex-mcp"
            entry.write_bytes(entry_bytes)
            smoke.write_bytes(smoke_bytes)
            launcher.write_bytes(launcher_bytes)
            manifest = integrated_manifest(version, entry_bytes)
            installed_bytes = {
                "smoke_cortex_mcp.py": smoke_bytes,
                "run-cortex-mcp": launcher_bytes,
            }
            for asset in manifest["assets"]:
                if asset["name"] in installed_bytes:
                    content = installed_bytes[asset["name"]]
                    asset["bytes"] = len(content)
                    asset["sha256"] = hashlib.sha256(content).hexdigest()
            manifest_path = version_root / "package-manifest.json"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            expected_hash = INSTALLER.sha256_file(manifest_path)
            self.assertEqual(
                "integrated-schema-3",
                INSTALLER.validate_rollback_target(
                    version_root,
                    manifest_path,
                    manifest,
                    expected_hash,
                ),
            )
            for path in (smoke, launcher):
                original = path.read_bytes()
                path.write_bytes(b"tampered")
                with self.assertRaisesRegex(RuntimeError, "failed integrity"):
                    INSTALLER.validate_rollback_target(
                        version_root,
                        manifest_path,
                        manifest,
                        expected_hash,
                    )
                path.write_bytes(original)

    def test_rollback_target_rejects_unsupported_manifest_schema(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            version_root = pathlib.Path(temporary) / "1.2026.08.03.12.20.B"
            version_root.mkdir()
            manifest = {"schemaVersion": 2}
            manifest_path = version_root / "package-manifest.json"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            with self.assertRaisesRegex(RuntimeError, "unsupported package-manifest schema"):
                INSTALLER.validate_rollback_target(
                    version_root,
                    manifest_path,
                    manifest,
                    INSTALLER.sha256_file(manifest_path),
                )

    def test_builder_and_runtime_command_expose_exact_migration_contract(self) -> None:
        builder = (PROJECT_ROOT / "tools" / "New-CortexMcpArm64Package.ps1").read_text(
            encoding="utf-8"
        )
        match = re.search(
            r"\$migrationVariables\s*=\s*@\((?P<body>.*?)\n\s*\)",
            builder,
            flags=re.DOTALL,
        )
        self.assertIsNotNone(match, "The package builder omits its base migration-variable list.")
        declared = re.findall(r"'([^']+)'", match.group("body"))
        self.assertEqual(
            list(PACKAGE_VERIFIER.EXPECTED_TRUSTED_LOOPBACK_MIGRATION_VARIABLES),
            declared,
        )
        self.assertIn("PEGASUS_CORTEX_ROOT_CA", builder)
        self.assertIn("PEGASUS_CORTEX_POSTGRESQL_SERVER_SPKI_SHA256", builder)
        for token in (
            "$sourceInputsBeforeBuild",
            "$sourceInputsAfterBuild",
            "Bound source inputs changed during restore or publish.",
        ):
            self.assertIn(token, builder)
        self.assertEqual(
            PACKAGE_VERIFIER.EXPECTED_RUNTIME_VARIABLES,
            INSTALLER.REQUIRED_RUNTIME_VARIABLES,
        )
        self.assertEqual(
            PACKAGE_VERIFIER.EXPECTED_MIGRATION_VARIABLES,
            INSTALLER.REQUIRED_MIGRATION_VARIABLES,
        )
        self.assertEqual(
            PACKAGE_VERIFIER.EXPECTED_TRUSTED_LOOPBACK_RUNTIME_VARIABLES,
            INSTALLER.TRUSTED_LOOPBACK_RUNTIME_VARIABLES,
        )
        self.assertEqual(
            PACKAGE_VERIFIER.EXPECTED_TRUSTED_LOOPBACK_MIGRATION_VARIABLES,
            INSTALLER.TRUSTED_LOOPBACK_MIGRATION_VARIABLES,
        )
        PACKAGE_VERIFIER.verify_integrated_source_contract(PROJECT_ROOT)

        environment = INSTALLER.cortex_subprocess_environment(
            pathlib.Path("/opt/pegasus"),
            {
                "PATH": "/usr/bin:/bin",
                "PEGASUS_ROOT": "/wrong/root",
                "PEGASUS_CORTEX_RUNTIME_CONNECTION": "secret",
                "DOTNET_ROOT": "/wrong/dotnet",
            },
        )
        self.assertEqual(
            {
                "HOME": "/",
                "LANG": "C.UTF-8",
                "LC_ALL": "C.UTF-8",
                "PATH": "/usr/sbin:/usr/bin:/sbin:/bin",
                "PEGASUS_ROOT": str(pathlib.Path("/opt/pegasus")),
            },
            environment,
        )

    def test_bounded_subprocess_captures_small_output(self) -> None:
        completed = INSTALLER.run_bounded_subprocess(
            [
                sys.executable,
                "-c",
                "import sys; sys.stdout.write('ready'); sys.stderr.write('note')",
            ],
            5,
            dict(os.environ),
        )

        self.assertEqual(0, completed.returncode)
        self.assertEqual("ready", completed.stdout)
        self.assertEqual("note", completed.stderr)

    def test_bounded_subprocess_does_not_limit_unrelated_child_files(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output_path = pathlib.Path(temporary) / "managed-startup-cache.bin"
            requested_bytes = INSTALLER.MAXIMUM_SUBPROCESS_OUTPUT_BYTES * 2
            completed = INSTALLER.run_bounded_subprocess(
                [
                    sys.executable,
                    "-c",
                    (
                        "import pathlib,sys; "
                        "pathlib.Path(sys.argv[1]).write_bytes(b'x'*int(sys.argv[2])); "
                        "print('ready')"
                    ),
                    str(output_path),
                    str(requested_bytes),
                ],
                5,
                dict(os.environ),
            )

            self.assertEqual(0, completed.returncode)
            self.assertEqual(["ready"], completed.stdout.splitlines())
            self.assertEqual(requested_bytes, output_path.stat().st_size)

    def test_bounded_subprocess_rejects_each_oversized_stream(self) -> None:
        for descriptor in (1, 2):
            with self.subTest(descriptor=descriptor), self.assertRaisesRegex(
                RuntimeError,
                "exceeded its bounded output allowance",
            ):
                INSTALLER.run_bounded_subprocess(
                    [
                        sys.executable,
                        "-c",
                        "import os,sys; os.write(int(sys.argv[1]), b'x'*int(sys.argv[2]))",
                        str(descriptor),
                        str(INSTALLER.MAXIMUM_SUBPROCESS_OUTPUT_BYTES + 1),
                    ],
                    5,
                    dict(os.environ),
                )

    def test_bounded_subprocess_accepts_exact_stream_limit(self) -> None:
        for descriptor, attribute in ((1, "stdout"), (2, "stderr")):
            with self.subTest(descriptor=descriptor):
                completed = INSTALLER.run_bounded_subprocess(
                    [
                        sys.executable,
                        "-c",
                        "import os,sys; os.write(int(sys.argv[1]), b'x'*int(sys.argv[2]))",
                        str(descriptor),
                        str(INSTALLER.MAXIMUM_SUBPROCESS_OUTPUT_BYTES),
                    ],
                    5,
                    dict(os.environ),
                )
                self.assertEqual(0, completed.returncode)
                self.assertEqual(
                    INSTALLER.MAXIMUM_SUBPROCESS_OUTPUT_BYTES,
                    len(getattr(completed, attribute).encode("utf-8")),
                )

    def test_bounded_subprocess_stops_output_flood_before_timeout(self) -> None:
        started = time.monotonic()
        with self.assertRaisesRegex(
            RuntimeError,
            "exceeded its bounded output allowance",
        ):
            INSTALLER.run_bounded_subprocess(
                [
                    sys.executable,
                    "-c",
                    (
                        "import os,time; "
                        f"os.write(1, b'x'*{INSTALLER.MAXIMUM_SUBPROCESS_OUTPUT_BYTES + 1}); "
                        "time.sleep(5)"
                    ),
                ],
                5,
                dict(os.environ),
            )
        self.assertLess(time.monotonic() - started, 3)

    def test_bounded_subprocess_timeout_reaps_the_child(self) -> None:
        with self.assertRaises(subprocess.TimeoutExpired):
            INSTALLER.run_bounded_subprocess(
                [sys.executable, "-c", "import time; time.sleep(5)"],
                0.1,
                dict(os.environ),
            )

    @unittest.skipUnless(os.name == "posix", "Process-group cleanup is POSIX-only.")
    def test_bounded_subprocess_timeout_reaps_same_group_descendants(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            ready = pathlib.Path(temporary) / "descendant-started"
            delayed_marker = pathlib.Path(temporary) / "descendant-survived"
            child = (
                "import pathlib,subprocess,sys,time; "
                "subprocess.Popen([sys.executable,'-c',"
                "'import pathlib,sys,time; time.sleep(1.5); "
                "pathlib.Path(sys.argv[1]).write_text(\"survived\")',sys.argv[2]]); "
                "pathlib.Path(sys.argv[1]).write_text('ready'); time.sleep(5)"
            )
            with self.assertRaises(subprocess.TimeoutExpired):
                INSTALLER.run_bounded_subprocess(
                    [sys.executable, "-c", child, str(ready), str(delayed_marker)],
                    0.5,
                    dict(os.environ),
                )
            self.assertTrue(ready.is_file())
            time.sleep(1.5)
            self.assertFalse(delayed_marker.exists())

    def test_subprocess_returncode_description_does_not_conflate_signals(self) -> None:
        self.assertEqual(
            "signal 9",
            INSTALLER.subprocess_returncode_description(-9),
        )
        ambiguous_host_exit = INSTALLER.subprocess_returncode_description(137)
        self.assertIn("possible .NET CoreClrInitFailure", ambiguous_host_exit)
        self.assertIn(
            "a direct POSIX SIGKILL would be reported as -9",
            ambiguous_host_exit,
        )
        self.assertEqual(
            "exit code 4",
            INSTALLER.subprocess_returncode_description(4),
        )

    def test_bounded_subprocess_preserves_positive_host_exit_code(self) -> None:
        completed = INSTALLER.run_bounded_subprocess(
            [sys.executable, "-c", "import os; os._exit(137)"],
            5,
            dict(os.environ),
        )
        self.assertEqual(137, completed.returncode)

    @unittest.skipUnless(os.name == "posix", "POSIX signal return codes are Linux-only.")
    def test_bounded_subprocess_preserves_direct_signal_as_negative(self) -> None:
        completed = INSTALLER.run_bounded_subprocess(
            [
                sys.executable,
                "-c",
                "import os,signal; os.kill(os.getpid(), signal.SIGKILL)",
            ],
            5,
            dict(os.environ),
        )
        self.assertEqual(-9, completed.returncode)

    def test_complete_migration_variable_contract_is_accepted(self) -> None:
        configuration = complete_configuration()
        application = integrated_application()

        PACKAGE_VERIFIER.verify_configuration_contract(configuration)
        INSTALLER.require_integrated_entry_configuration(application, configuration)

    def test_trusted_loopback_contract_omits_certificate_inputs(self) -> None:
        configuration = complete_configuration()
        configuration["requiredRuntimeVariables"] = list(
            PACKAGE_VERIFIER.EXPECTED_TRUSTED_LOOPBACK_RUNTIME_VARIABLES
        )
        configuration["requiredMigrationVariables"] = list(
            PACKAGE_VERIFIER.EXPECTED_TRUSTED_LOOPBACK_MIGRATION_VARIABLES
        )
        database_contract = {
            "provider": "postgresql",
            "migrationId": "0007",
            "requiredMigrationId": "0007",
            "migrationMode": "one-shot-separated-role",
            "runtimeRoleLeastPrivilege": True,
            "migrationCredentialRetained": False,
            "tlsPolicy": INSTALLER.TRUSTED_LOOPBACK_TLS_POLICY,
            "certificateValidation": dict(
                INSTALLER.EXPECTED_TRUSTED_LOOPBACK_VALIDATION
            ),
        }

        PACKAGE_VERIFIER.verify_configuration_contract(
            configuration,
            PACKAGE_VERIFIER.TRUSTED_LOOPBACK_TLS_POLICY,
        )
        INSTALLER.require_integrated_database_contract(database_contract)
        INSTALLER.require_integrated_entry_configuration(
            integrated_application(),
            configuration,
            database_contract,
        )

    def test_application_version_omission_fails_closed(self) -> None:
        configuration = complete_configuration()
        configuration["requiredMigrationVariables"] = [
            value
            for value in configuration["requiredMigrationVariables"]
            if value != "PEGASUS_CORTEX_APPLICATION_VERSION"
        ]
        application = integrated_application()

        with self.assertRaisesRegex(
            RuntimeError,
            "protected-configuration contract changed",
        ):
            PACKAGE_VERIFIER.verify_configuration_contract(configuration)
        with self.assertRaisesRegex(
            RuntimeError,
            "integrated entry/configuration contract is invalid",
        ):
            INSTALLER.require_integrated_entry_configuration(
                application,
                configuration,
            )

    def test_runtime_contract_or_principal_sharing_drift_fails_closed(self) -> None:
        application = integrated_application()
        for mutate in (
            lambda value: value.__setitem__("sharedByPrincipal", False),
            lambda value: value.__setitem__(
                "principalSelectedByOperatingSystemIdentity", False
            ),
            lambda value: value.__setitem__("requiredRuntimeVariables", []),
        ):
            configuration = complete_configuration()
            mutate(configuration)
            with self.assertRaisesRegex(
                RuntimeError,
                "integrated entry/configuration contract is invalid",
            ):
                INSTALLER.require_integrated_entry_configuration(
                    application,
                    configuration,
                )

    def test_prepare_rejects_manifest_drift_before_lock_or_subprocess(self) -> None:
        version = "1.2026.08.03.12.20.B"
        mutations = (
            (
                "missing",
                lambda manifest: manifest.__setitem__("databaseContract", None),
            ),
            (
                "invalid-migration-id",
                lambda manifest: manifest["databaseContract"].__setitem__(
                    "migrationId", "6"
                ),
            ),
            (
                "stale-migration-id",
                lambda manifest: manifest["databaseContract"].__setitem__(
                    "migrationId", "0006"
                ),
            ),
            (
                "required-migration-drift",
                lambda manifest: manifest["databaseContract"].__setitem__(
                    "requiredMigrationId", "0006"
                ),
            ),
            (
                "tls-validation-drift",
                lambda manifest: manifest["databaseContract"][
                    "certificateValidation"
                ].__setitem__("failClosed", False),
            ),
            (
                "principal-drift",
                lambda manifest: manifest["runtimePrincipals"].__setitem__(
                    "auditor", "codexsvc"
                ),
            ),
            (
                "surface-drift",
                lambda manifest: manifest["mcpToolSurfaces"]["main"].append(
                    "cortex_unapproved"
                ),
            ),
            (
                "guide-drift",
                lambda manifest: manifest["guide"].__setitem__(
                    "resourceUri", "cortex://unapproved/guide"
                ),
            ),
        )
        for label, mutate in mutations:
            with self.subTest(case=label), tempfile.TemporaryDirectory() as temporary:
                workspace = pathlib.Path(temporary)
                package_root = workspace / "package"
                package_root.mkdir()
                manifest = integrated_manifest(version)
                mutate(manifest)
                manifest_path = package_root / "package-manifest.json"
                manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
                pegasus_root = workspace / "pegasus"
                arguments = argparse.Namespace(
                    operation="prepare",
                    package_root=package_root,
                    pegasus_root=pegasus_root,
                    expected_manifest_sha256=INSTALLER.sha256_file(manifest_path),
                    plan_id=f"manifest-{label}",
                    resume_attempt_sha256=None,
                )
                with (
                    mock.patch.object(
                        INSTALLER,
                        "require_arm64_linux",
                        return_value=("aarch64", 42),
                    ),
                    mock.patch.object(INSTALLER, "validate_operation_path_budget"),
                    mock.patch.object(
                        INSTALLER,
                        "deployment_operation_lock",
                    ) as operation_lock,
                    mock.patch.object(INSTALLER, "run_migration") as migrate,
                    mock.patch.object(
                        INSTALLER,
                        "run_bounded_subprocess",
                    ) as runuser,
                ):
                    with self.assertRaisesRegex(RuntimeError, "package"):
                        INSTALLER.prepare(arguments)
                operation_lock.assert_not_called()
                migrate.assert_not_called()
                runuser.assert_not_called()
                self.assertFalse(pegasus_root.exists())

    def test_prepare_commits_exact_candidate_without_selecting_it(self) -> None:
        version = "1.2026.08.03.10.20.A"
        entry_bytes = b"integrated-entry"
        manifest = integrated_manifest(version, entry_bytes)
        migration = {
            "status": "pass",
            "currentMigrationId": "0007",
            "applicationVersion": version,
            "appliedMigrationIds": ["0007"],
        }
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            package_root = root / "package"
            package_root.mkdir()
            manifest_path = package_root / "package-manifest.json"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            expected_manifest_hash = INSTALLER.sha256_file(manifest_path)
            assets: dict[str, pathlib.Path] = {}
            for name in (
                "app.tar.gz",
                "runtime.tar.gz",
                "install_cortex_mcp_arm64.py",
                "run-cortex-mcp",
                "run-cortex-migrate",
                "smoke_cortex_mcp.py",
            ):
                path = package_root / name
                path.write_bytes(name.encode("ascii"))
                assets[name] = path

            def extract(_: pathlib.Path, destination: pathlib.Path) -> None:
                destination.mkdir()
                if destination.name == "app":
                    (destination / "PegasusCortex.Mcp.Integrated.dll").write_bytes(
                        entry_bytes
                    )
                else:
                    (destination / "dotnet").write_bytes(b"arm64-runtime")

            arguments = argparse.Namespace(
                operation="prepare",
                package_root=package_root,
                pegasus_root=root / "pegasus",
                expected_manifest_sha256=expected_manifest_hash,
                plan_id="prepare-candidate",
                resume_attempt_sha256=None,
            )
            paths = INSTALLER.installation_paths(arguments.pegasus_root)
            release_configuration, runtime_configuration, migration_configuration = (
                INSTALLER.release_configuration_paths(paths, version)
            )
            release_configuration.mkdir(parents=True)
            runtime_configuration.write_text("RUNTIME=prepared\n", encoding="utf-8")
            runtime_configuration.chmod(0o640)
            migration_configuration.write_text(
                "MIGRATION=one-shot\n",
                encoding="utf-8",
            )
            migration_configuration.chmod(0o600)
            migration_configuration_hash = INSTALLER.sha256_file(
                migration_configuration
            )
            selector_before = "versions/1.2026.07.27.13.20.A"
            with (
                portable_deployment_environment(),
                mock.patch.object(INSTALLER, "require_arm64_linux", return_value=("aarch64", 42)),
                mock.patch.object(
                    INSTALLER,
                    "load_approved_package",
                    return_value=(package_root, manifest_path, manifest, version, assets),
                ),
                mock.patch.object(INSTALLER, "apply_directory_policy"),
                mock.patch.object(INSTALLER, "require_directory_policy"),
                mock.patch.object(
                    INSTALLER,
                    "require_protected_configuration",
                ) as require_configuration,
                mock.patch.object(INSTALLER, "extract_regular_archive", side_effect=extract),
                mock.patch.object(INSTALLER, "apply_version_policy"),
                mock.patch.object(INSTALLER, "require_version_policy"),
                mock.patch.object(INSTALLER, "run_migration", return_value=migration) as migrate,
                mock.patch.object(
                    INSTALLER,
                    "atomic_current_link",
                    side_effect=AssertionError("prepare changed the selector"),
                ) as select,
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=(selector_before, 1, 2, 3),
                ),
                mock.patch.object(INSTALLER.os, "chown", create=True),
                contextlib.redirect_stdout(io.StringIO()),
            ):
                self.assertEqual(0, INSTALLER.prepare(arguments))

            version_root = root / "pegasus" / "apps" / "cortex" / "versions" / version
            self.assertTrue(version_root.is_dir())
            select.assert_not_called()
            self.assertEqual(version_root, migrate.call_args.args[0])
            self.assertTrue(runtime_configuration.is_file())
            self.assertFalse(migration_configuration.exists())
            require_configuration.assert_has_calls(
                [
                    mock.call(runtime_configuration, 42, 0o640),
                    mock.call(migration_configuration, 42, 0o600),
                ]
            )
            receipt_path = (
                root
                / "pegasus"
                / "manifests"
                / "cortex"
                / f"{version}-prepare-candidate-prepared.json"
            )
            receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
            self.assertEqual("prepared", receipt["status"])
            self.assertEqual(selector_before, receipt["selectorBefore"])
            self.assertFalse(receipt["selectorChanged"])
            self.assertEqual(
                INSTALLER.sha256_file(runtime_configuration),
                receipt["runtimeConfigurationSha256"],
            )
            self.assertEqual(
                migration_configuration_hash,
                receipt["migrationConfigurationSha256"],
            )
            self.assertEqual(version, receipt["configurationReleaseVersion"])
            self.assertEqual(
                f"config/cortex/releases/{version}/cortex-mcp.env",
                receipt["runtimeConfiguration"],
            )
            self.assertEqual(
                f"config/cortex/releases/{version}/cortex-migration.env",
                receipt["migrationConfiguration"],
            )
            self.assertFalse(receipt["migrationCredentialRetained"])
            self.assertTrue(receipt["migrationCredentialDeletionDurable"])
            self.assertTrue(
                INSTALLER.preparation_extraction_path(
                    paths["manifests"], version, "prepare-candidate"
                ).is_file()
            )
            self.assertTrue(
                INSTALLER.preparation_attempt_path(
                    paths["manifests"], version, "prepare-candidate"
                ).is_file()
            )
            self.assertTrue(
                INSTALLER.preparation_migration_path(
                    paths["manifests"], version, "prepare-candidate"
                ).is_file()
            )
            self.assertEqual(INSTALLER.version_tree_sha256(version_root), receipt["versionTreeSha256"])

    def test_prepare_receipt_retry_requires_exact_attempt_without_replaying_migration(
        self,
    ) -> None:
        version = "1.2026.08.03.12.20.B"
        entry_bytes = b"integrated-entry"
        manifest = integrated_manifest(version, entry_bytes)
        migration = {
            "status": "pass",
            "currentMigrationId": "0007",
            "applicationVersion": version,
            "appliedMigrationIds": ["0007"],
        }
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            package_root = root / "package"
            package_root.mkdir()
            manifest_path = package_root / "package-manifest.json"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            expected_manifest_hash = INSTALLER.sha256_file(manifest_path)
            assets: dict[str, pathlib.Path] = {}
            for name in (
                "app.tar.gz",
                "runtime.tar.gz",
                "install_cortex_mcp_arm64.py",
                "run-cortex-mcp",
                "run-cortex-migrate",
                "smoke_cortex_mcp.py",
            ):
                path = package_root / name
                path.write_bytes(name.encode("ascii"))
                assets[name] = path

            def extract(_: pathlib.Path, destination: pathlib.Path) -> None:
                destination.mkdir()
                if destination.name == "app":
                    (destination / "PegasusCortex.Mcp.Integrated.dll").write_bytes(
                        entry_bytes
                    )
                else:
                    (destination / "dotnet").write_bytes(b"arm64-runtime")

            arguments = argparse.Namespace(
                operation="prepare",
                package_root=package_root,
                pegasus_root=root / "pegasus",
                expected_manifest_sha256=expected_manifest_hash,
                plan_id="prepare-failure",
                resume_attempt_sha256=None,
            )
            paths = INSTALLER.installation_paths(arguments.pegasus_root)
            release_root, runtime_configuration, migration_configuration = (
                INSTALLER.release_configuration_paths(paths, version)
            )
            release_root.mkdir(parents=True)
            runtime_configuration.write_text("RUNTIME=prepared\n", encoding="utf-8")
            migration_configuration.write_text("MIGRATION=one-shot\n", encoding="utf-8")
            real_publish = INSTALLER.write_protected_json_no_replace
            prepared_publication_failed = False

            def fail_only_prepared_receipt(
                path: pathlib.Path,
                value: dict[str, Any],
                group_id: int | None,
                final_mode: int = 0o640,
            ) -> None:
                nonlocal prepared_publication_failed
                if (
                    path.name.endswith("-prepared.json")
                    and not prepared_publication_failed
                ):
                    prepared_publication_failed = True
                    raise OSError("prepared receipt failed")
                real_publish(path, value, group_id, final_mode)

            with (
                portable_deployment_environment(),
                mock.patch.object(INSTALLER, "require_arm64_linux", return_value=("aarch64", 42)),
                mock.patch.object(
                    INSTALLER,
                    "load_approved_package",
                    return_value=(package_root, manifest_path, manifest, version, assets),
                ),
                mock.patch.object(INSTALLER, "apply_directory_policy"),
                mock.patch.object(INSTALLER, "require_directory_policy"),
                mock.patch.object(INSTALLER, "require_protected_configuration"),
                mock.patch.object(INSTALLER, "extract_regular_archive", side_effect=extract),
                mock.patch.object(INSTALLER, "apply_version_policy"),
                mock.patch.object(INSTALLER, "require_version_policy"),
                mock.patch.object(
                    INSTALLER,
                    "run_migration",
                    return_value=migration,
                ) as migrate,
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=("versions/1.2026.07.27.13.20.A", 1, 2, 3),
                ),
                mock.patch.object(
                    INSTALLER,
                    "write_protected_json_no_replace",
                    side_effect=fail_only_prepared_receipt,
                ),
            ):
                with self.assertRaisesRegex(OSError, "prepared receipt failed"):
                    INSTALLER.prepare(arguments)

                attempt_path = INSTALLER.preparation_attempt_path(
                    paths["manifests"], version, "prepare-failure"
                )
                migration_path = INSTALLER.preparation_migration_path(
                    paths["manifests"], version, "prepare-failure"
                )
                self.assertTrue(attempt_path.is_file())
                self.assertTrue(migration_path.is_file())
                with self.assertRaisesRegex(RuntimeError, "requires --resume-attempt"):
                    INSTALLER.prepare(arguments)
                arguments.resume_attempt_sha256 = INSTALLER.sha256_file(attempt_path)
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(0, INSTALLER.prepare(arguments))

            version_root = paths["versions"] / version
            self.assertTrue(version_root.is_dir())
            self.assertFalse(migration_configuration.exists())
            self.assertTrue(
                INSTALLER.prepared_receipt_path(
                    paths["manifests"], version, "prepare-failure"
                ).is_file()
            )
            self.assertFalse(
                INSTALLER.preparation_failure_path(
                    paths["manifests"], version, "prepare-failure"
                ).exists()
            )
            migrate.assert_called_once()

    def test_unsupported_platform_preflight_leaves_root_absent(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "unsupported-root"
            arguments = argparse.Namespace(operation="prepare", pegasus_root=root)
            with (
                mock.patch.object(
                    INSTALLER,
                    "require_arm64_linux",
                    side_effect=RuntimeError("unsupported platform"),
                ),
                mock.patch.object(
                    INSTALLER,
                    "deployment_operation_lock",
                ) as operation_lock,
            ):
                with self.assertRaisesRegex(RuntimeError, "unsupported platform"):
                    INSTALLER.prepare(arguments)
            self.assertFalse(root.exists())
            operation_lock.assert_not_called()

    def test_operation_path_budget_accepts_a_normal_root_without_mutation(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version="1.2026.08.03.12.20.B",
                prepared_plan_id="prepare-candidate",
                plan_id="activate-candidate",
            )
            INSTALLER.validate_operation_path_budget(arguments)
            self.assertFalse(root.exists())

    def test_overbudget_arm_preflight_leaves_root_and_lock_absent(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            base = pathlib.Path(temporary)
            segment_length = max(1, 220 - len(str(base)) - 1)
            root = base / ("r" * segment_length)
            self.assertLessEqual(
                len(str(root)),
                INSTALLER.MAXIMUM_FINAL_PATH_CHARACTERS,
            )
            arguments = argparse.Namespace(
                operation="prepare",
                pegasus_root=root,
                plan_id="p" * 64,
            )
            with (
                mock.patch.object(
                    INSTALLER,
                    "require_arm64_linux",
                    return_value=("aarch64", 42),
                ),
                mock.patch.object(
                    INSTALLER,
                    "deployment_operation_lock",
                ) as operation_lock,
            ):
                with self.assertRaisesRegex(RuntimeError, "path budget"):
                    INSTALLER.prepare(arguments)
            self.assertFalse(root.exists())
            operation_lock.assert_not_called()

    def test_non_bmp_root_uses_utf16_units_before_any_mutation(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            base = pathlib.Path(temporary)
            # Each non-BMP character occupies one Python code point but two
            # UTF-16 code units. Separate segments stay within the UTF-8 cap.
            root = base / ("\U0001f600" * 45) / ("\U0001f600" * 45) / ("\U0001f600" * 45)
            self.assertLessEqual(
                len(str(root)),
                INSTALLER.MAXIMUM_FINAL_PATH_CHARACTERS,
            )
            self.assertGreater(
                INSTALLER.utf16_code_unit_count(str(root), "test root"),
                INSTALLER.MAXIMUM_FINAL_PATH_CHARACTERS,
            )
            arguments = argparse.Namespace(
                operation="prepare",
                pegasus_root=root,
                plan_id="unicode-budget",
            )
            with (
                mock.patch.object(
                    INSTALLER,
                    "require_arm64_linux",
                    return_value=("aarch64", 42),
                ),
                mock.patch.object(
                    INSTALLER,
                    "deployment_operation_lock",
                ) as operation_lock,
            ):
                with self.assertRaisesRegex(RuntimeError, "path budget"):
                    INSTALLER.prepare(arguments)
            self.assertFalse(root.exists())
            operation_lock.assert_not_called()

    def test_enumerated_package_child_over_final_path_budget_is_rejected(self) -> None:
        version = "1.2026.08.03.12.20.B"
        manifest = integrated_manifest(version)
        with tempfile.TemporaryDirectory() as temporary:
            base = pathlib.Path(temporary)
            desired_root_units = 200
            filler_length = desired_root_units - len(str(base)) - 1
            self.assertGreater(filler_length, 0)
            package_root = base / ("p" * filler_length)
            package_root.mkdir()
            self.assertEqual(
                desired_root_units,
                INSTALLER.utf16_code_unit_count(str(package_root), "package root"),
            )

            for item in manifest["assets"]:
                (package_root / item["name"]).write_bytes(
                    item["name"].encode("ascii")
                )
            manifest_path = package_root / "package-manifest.json"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            checksum_lines = [
                f"{item['sha256']}  {item['name']}" for item in manifest["assets"]
            ]
            checksum_lines.append(
                f"{INSTALLER.sha256_file(manifest_path)}  package-manifest.json"
            )
            (package_root / "checksums.sha256").write_text(
                "\n".join(checksum_lines) + "\n",
                encoding="utf-8",
            )

            overbudget_name_length = (
                INSTALLER.MAXIMUM_FINAL_PATH_CHARACTERS
                + 1
                - desired_root_units
                - 1
            )
            overbudget_child = package_root / ("x" * overbudget_name_length)
            overbudget_child.write_bytes(b"outside-sealed-allowlist")
            self.assertEqual(
                INSTALLER.MAXIMUM_FINAL_PATH_CHARACTERS + 1,
                INSTALLER.utf16_code_unit_count(
                    str(overbudget_child),
                    "package child",
                ),
            )
            with self.assertRaisesRegex(
                RuntimeError,
                "package entry path exceeds the final path budget",
            ):
                INSTALLER.require_package_assets(package_root, manifest)

    def test_shared_installation_ancestor_rejects_non_root_write_authority(
        self,
    ) -> None:
        def directory(mode: int) -> mock.MagicMock:
            path = mock.MagicMock()
            path.is_symlink.return_value = False
            path.is_dir.return_value = True
            path.stat.return_value = mock.Mock(
                st_uid=0,
                st_mode=stat.S_IFDIR | mode,
            )
            return path

        paths = {
            "apps": directory(0o777),
            "manifests_parent": directory(0o755),
            "config_parent": directory(0o755),
        }
        with self.assertRaisesRegex(RuntimeError, "unsafe ownership or mode"):
            INSTALLER.require_installation_ancestor_policy(paths)

    def test_prepare_reenters_after_extraction_rename_before_policy(self) -> None:
        version = "1.2026.08.03.12.20.B"
        migration = {
            "status": "pass",
            "currentMigrationId": "0007",
            "applicationVersion": version,
            "appliedMigrationIds": ["0007"],
        }
        with tempfile.TemporaryDirectory() as temporary:
            fixture = write_prepare_inputs(
                pathlib.Path(temporary),
                version,
                "prepare-rename-resume",
            )
            arguments = fixture["arguments"]
            loaded = (
                arguments.package_root,
                fixture["manifestPath"],
                fixture["manifest"],
                version,
                fixture["assets"],
            )
            previous_identity = (
                "versions/1.2026.07.27.13.20.A",
                1,
                2,
                3,
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "load_approved_package",
                    return_value=loaded,
                ),
                mock.patch.object(
                    INSTALLER,
                    "extract_regular_archive",
                    side_effect=fixture["extract"],
                ),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=previous_identity,
                ),
                mock.patch.object(
                    INSTALLER,
                    "apply_version_policy",
                    side_effect=(SystemExit("crash after rename"), None),
                ) as apply_policy,
                mock.patch.object(
                    INSTALLER,
                    "run_migration",
                    return_value=migration,
                ) as migrate,
            ):
                with self.assertRaisesRegex(SystemExit, "crash after rename"):
                    INSTALLER.prepare(arguments)
                version_root = fixture["paths"]["versions"] / version
                self.assertTrue(version_root.is_dir())
                self.assertTrue(
                    INSTALLER.preparation_extraction_path(
                        fixture["paths"]["manifests"],
                        version,
                        arguments.plan_id,
                    ).is_file()
                )
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(0, INSTALLER.prepare(arguments))
            self.assertEqual(2, apply_policy.call_count)
            migrate.assert_called_once()

    def test_prepare_reauthenticates_package_root_before_migration(self) -> None:
        version = "1.2026.08.03.12.20.B"
        with tempfile.TemporaryDirectory() as temporary:
            fixture = write_prepare_inputs(
                pathlib.Path(temporary),
                version,
                "prepare-package-replay",
            )
            arguments = fixture["arguments"]
            loaded = (
                arguments.package_root,
                fixture["manifestPath"],
                fixture["manifest"],
                version,
                fixture["assets"],
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "load_approved_package",
                    return_value=loaded,
                ),
                mock.patch.object(
                    INSTALLER,
                    "package_root_identity",
                    side_effect=((1, 2, 3), (1, 2, 4)),
                ),
                mock.patch.object(
                    INSTALLER,
                    "extract_regular_archive",
                    side_effect=fixture["extract"],
                ),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=(None, None, None, None),
                ),
                mock.patch.object(INSTALLER, "run_migration") as migrate,
            ):
                with self.assertRaisesRegex(RuntimeError, "package root changed"):
                    INSTALLER.prepare(arguments)
            migrate.assert_not_called()
            self.assertFalse(
                INSTALLER.preparation_attempt_path(
                    fixture["paths"]["manifests"],
                    version,
                    arguments.plan_id,
                ).exists()
            )

    def test_prepare_rejects_copied_executable_or_manifest_tamper(self) -> None:
        version = "1.2026.08.03.12.20.B"
        for relative_path in (
            "run-cortex-mcp",
            "tools/smoke_cortex_mcp.py",
            "package-manifest.json",
        ):
            with self.subTest(relative_path=relative_path), tempfile.TemporaryDirectory() as temporary:
                fixture = write_prepare_inputs(
                    pathlib.Path(temporary),
                    version,
                    "prepare-copy-tamper",
                )
                arguments = fixture["arguments"]
                loaded = (
                    arguments.package_root,
                    fixture["manifestPath"],
                    fixture["manifest"],
                    version,
                    fixture["assets"],
                )
                identity_calls = 0

                def package_identity(_: pathlib.Path) -> tuple[int, int, int]:
                    nonlocal identity_calls
                    identity_calls += 1
                    if identity_calls == 2:
                        installed = fixture["paths"]["versions"] / version
                        (installed / relative_path).write_bytes(b"tampered")
                    return 1, 2, 3

                with (
                    portable_deployment_environment(),
                    mock.patch.object(
                        INSTALLER,
                        "load_approved_package",
                        return_value=loaded,
                    ),
                    mock.patch.object(
                        INSTALLER,
                        "package_root_identity",
                        side_effect=package_identity,
                    ),
                    mock.patch.object(
                        INSTALLER,
                        "extract_regular_archive",
                        side_effect=fixture["extract"],
                    ),
                    mock.patch.object(
                        INSTALLER,
                        "read_current_link_identity",
                        return_value=(None, None, None, None),
                    ),
                    mock.patch.object(INSTALLER, "run_migration") as migrate,
                ):
                    with self.assertRaises(RuntimeError):
                        INSTALLER.prepare(arguments)
                migrate.assert_not_called()

    def test_activate_rejects_candidate_without_exact_prepared_receipt(self) -> None:
        version = "1.2026.08.03.10.20.A"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths = INSTALLER.installation_paths(root)
            for path in paths.values():
                path.mkdir(parents=True, exist_ok=True)
            release_configuration, runtime_configuration, _ = (
                INSTALLER.release_configuration_paths(paths, version)
            )
            release_configuration.mkdir()
            runtime_configuration.write_text("RUNTIME=prepared\n", encoding="utf-8")
            version_root = paths["versions"] / version
            version_root.mkdir()
            manifest_path = version_root / "package-manifest.json"
            manifest_path.write_text(
                json.dumps(integrated_manifest(version)),
                encoding="utf-8",
            )
            expected_hash = INSTALLER.sha256_file(manifest_path)
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-candidate",
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(INSTALLER, "require_arm64_linux", return_value=("aarch64", 42)),
                mock.patch.object(INSTALLER, "require_directory_policy"),
                mock.patch.object(INSTALLER, "require_protected_configuration"),
                mock.patch.object(
                    INSTALLER,
                    "require_protected_receipt",
                    side_effect=RuntimeError(
                        "The exact prepared Cortex receipt is missing or linked."
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                ) as select,
            ):
                with self.assertRaisesRegex(RuntimeError, "prepared Cortex receipt"):
                    INSTALLER.activate(arguments)
            select.assert_not_called()

    def test_activate_rejects_candidate_changed_after_prepare(self) -> None:
        version = "1.2026.08.03.10.20.A"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(root, version)
            (paths["versions"] / version / "unexpected.bin").write_bytes(b"tampered")
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-candidate",
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(INSTALLER, "require_arm64_linux", return_value=("aarch64", 42)),
                mock.patch.object(INSTALLER, "require_directory_policy"),
                mock.patch.object(INSTALLER, "require_protected_configuration"),
                mock.patch.object(INSTALLER, "require_protected_receipt"),
                mock.patch.object(INSTALLER, "require_version_policy"),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                ) as select,
            ):
                with self.assertRaisesRegex(RuntimeError, "does not match the exact candidate"):
                    INSTALLER.activate(arguments)
            select.assert_not_called()

    def test_activate_rejects_runtime_configuration_changed_after_prepare(self) -> None:
        version = "1.2026.08.03.10.20.A"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(root, version)
            _, runtime_configuration, _ = INSTALLER.release_configuration_paths(
                paths,
                version,
            )
            runtime_configuration.write_text("RUNTIME=changed\n", encoding="utf-8")
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-candidate",
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(INSTALLER, "require_arm64_linux", return_value=("aarch64", 42)),
                mock.patch.object(INSTALLER, "require_directory_policy"),
                mock.patch.object(INSTALLER, "require_protected_configuration"),
                mock.patch.object(INSTALLER, "require_protected_receipt"),
                mock.patch.object(INSTALLER, "require_version_policy"),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                ) as select,
            ):
                with self.assertRaisesRegex(RuntimeError, "does not match the exact candidate"):
                    INSTALLER.activate(arguments)
            select.assert_not_called()

    def test_activate_rejects_selector_drift_before_smoke_or_receipt(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(
                root,
                version,
                selector_before=previous,
            )
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-candidate",
            )
            prepared_identity = (previous, 1, 2, 3)
            changed_identity = (previous, 1, 99, 4)
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=changed_identity,
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                ) as select,
                mock.patch.object(INSTALLER, "run_smoke") as smoke,
            ):
                with self.assertRaisesRegex(RuntimeError, "before activation intent"):
                    INSTALLER.activate(arguments)
            self.assertEqual((previous, 1, 2, 3), prepared_identity)
            select.assert_not_called()
            smoke.assert_not_called()
            self.assertFalse(
                INSTALLER.activation_intent_path(
                    paths["manifests"], version, "activate-candidate"
                ).exists()
            )

    def test_prepared_receipt_rejects_invalid_selector_checkpoint(self) -> None:
        version = "1.2026.08.03.12.20.B"
        invalid_values: tuple[object, ...] = (
            "missing-field",
            "/opt/pegasus/apps/cortex/versions/1.2026.07.27.13.20.A",
            "versions/../1.2026.07.27.13.20.A",
            "versions/not-a-release",
        )
        for invalid in invalid_values:
            with self.subTest(invalid=invalid), tempfile.TemporaryDirectory() as temporary:
                root = pathlib.Path(temporary) / "pegasus"
                paths, expected_hash = write_prepared_candidate(
                    root,
                    version,
                    selector_before=None,
                )
                receipt_path = INSTALLER.prepared_receipt_path(
                    paths["manifests"],
                    version,
                    "prepare-candidate",
                )
                receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
                if invalid == "missing-field":
                    receipt.pop("selectorBefore")
                else:
                    receipt["selectorBefore"] = invalid
                receipt_path.write_text(json.dumps(receipt), encoding="utf-8")
                _, runtime_configuration, migration_configuration = (
                    INSTALLER.release_configuration_paths(paths, version)
                )
                with (
                    mock.patch.object(INSTALLER, "require_protected_receipt"),
                    mock.patch.object(INSTALLER, "require_version_policy"),
                ):
                    with self.assertRaisesRegex(RuntimeError, "prepared Cortex receipt"):
                        INSTALLER.require_prepared_candidate(
                            paths["versions"] / version,
                            receipt_path,
                            version,
                            "prepare-candidate",
                            expected_hash,
                            INSTALLER.sha256_file(runtime_configuration),
                            migration_configuration,
                            42,
                        )

    def test_prepared_receipt_rejects_invalid_selector_identity(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        invalid_identities: tuple[object, ...] = (
            None,
            {"target": previous, "device": 1, "inode": 2},
            {
                "target": previous,
                "device": None,
                "inode": None,
                "ctimeNs": None,
            },
            {
                "target": "versions/not-a-release",
                "device": 1,
                "inode": 2,
                "ctimeNs": 3,
            },
        )
        for invalid in invalid_identities:
            with self.subTest(invalid=invalid), tempfile.TemporaryDirectory() as temporary:
                root = pathlib.Path(temporary) / "pegasus"
                paths, expected_hash = write_prepared_candidate(
                    root,
                    version,
                    selector_before=previous,
                )
                receipt_path = INSTALLER.prepared_receipt_path(
                    paths["manifests"], version, "prepare-candidate"
                )
                receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
                receipt["selectorBeforeIdentity"] = invalid
                receipt_path.write_text(json.dumps(receipt), encoding="utf-8")
                _, runtime_configuration, migration_configuration = (
                    INSTALLER.release_configuration_paths(paths, version)
                )
                with (
                    mock.patch.object(INSTALLER, "require_protected_receipt"),
                    mock.patch.object(INSTALLER, "require_version_policy"),
                ):
                    with self.assertRaisesRegex(RuntimeError, "selector identity"):
                        INSTALLER.require_prepared_candidate(
                            paths["versions"] / version,
                            receipt_path,
                            version,
                            "prepare-candidate",
                            expected_hash,
                            INSTALLER.sha256_file(runtime_configuration),
                            migration_configuration,
                            42,
                        )

    def test_release_configuration_files_fail_closed_on_missing_link_or_wrong_mode(
        self,
    ) -> None:
        version = "1.2026.08.03.10.20.A"
        root = pathlib.Path("/opt/pegasus")
        paths = INSTALLER.installation_paths(root)
        release_root, runtime_configuration, migration_configuration = (
            INSTALLER.release_configuration_paths(paths, version)
        )
        self.assertEqual(
            root / "config" / "cortex" / "releases" / version,
            release_root,
        )
        self.assertEqual(release_root / "cortex-mcp.env", runtime_configuration)
        self.assertEqual(
            release_root / "cortex-migration.env",
            migration_configuration,
        )

        with tempfile.TemporaryDirectory() as temporary:
            missing = pathlib.Path(temporary) / "cortex-mcp.env"
            with self.assertRaisesRegex(RuntimeError, "configuration is missing"):
                INSTALLER.require_protected_configuration(missing, 42, 0o640)

        linked = mock.MagicMock()
        linked.name = "cortex-mcp.env"
        linked.is_symlink.return_value = True
        linked.is_file.return_value = True
        with self.assertRaisesRegex(RuntimeError, "configuration is missing"):
            INSTALLER.require_protected_configuration(linked, 42, 0o640)
        linked.stat.assert_not_called()

        for file_name, required_mode, actual_mode in (
            ("cortex-mcp.env", 0o640, 0o600),
            ("cortex-migration.env", 0o600, 0o640),
        ):
            wrong_mode = mock.MagicMock()
            wrong_mode.name = file_name
            wrong_mode.is_symlink.return_value = False
            wrong_mode.is_file.return_value = True
            wrong_mode.stat.return_value = mock.Mock(
                st_uid=0,
                st_gid=42,
                st_mode=stat.S_IFREG | actual_mode,
            )
            with self.assertRaisesRegex(RuntimeError, "unsafe ownership or mode"):
                INSTALLER.require_protected_configuration(
                    wrong_mode,
                    42,
                    required_mode,
                )

            accepted = mock.MagicMock()
            accepted.name = file_name
            accepted.is_symlink.return_value = False
            accepted.is_file.return_value = True
            accepted.stat.return_value = mock.Mock(
                st_uid=0,
                st_gid=42,
                st_mode=stat.S_IFREG | required_mode,
            )
            INSTALLER.require_protected_configuration(
                accepted,
                42,
                required_mode,
            )

    def test_activate_restores_previous_selector_when_smoke_fails(self) -> None:
        version = "1.2026.08.03.10.20.A"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            previous = "versions/1.2026.07.27.13.20.A"
            paths, expected_hash = write_prepared_candidate(
                root,
                version,
                selector_before=previous,
            )
            version_root = paths["versions"] / version
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-candidate",
            )
            prepared_identity = (previous, 1, 2, 3)
            selected_identity = (f"versions/{version}", 5, 6, 7)
            restored_identity = (previous, 8, 9, 10)
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "activate"
            )
            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "activate-restore"
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(prepared_identity, restored_identity),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=(
                        (selected_identity, selector_path),
                        (restored_identity, restore_transition),
                    ),
                ) as reconcile,
                mock.patch.object(INSTALLER, "run_smoke", side_effect=RuntimeError("smoke failed")),
            ):
                with self.assertRaisesRegex(RuntimeError, "smoke failed"):
                    INSTALLER.activate(arguments)
            self.assertEqual(2, reconcile.call_count)
            restore_intent = json.loads(
                INSTALLER.selector_restore_intent_path(
                    paths["manifests"], version, arguments.plan_id, "activate"
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("activate-restore", restore_intent["operation"])
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"], version, arguments.plan_id, "activate"
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("failed-restored", failure["status"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(restored_identity),
                failure["restoredIdentity"],
            )
            self.assertFalse(
                INSTALLER.activated_receipt_path(
                    paths["manifests"], version, "activate-candidate"
                ).exists()
            )

    def test_activate_post_smoke_tree_drift_journals_restoration(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(
                root,
                version,
                selector_before=previous,
            )
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-tree-drift",
            )
            version_root = paths["versions"] / version
            expected_tree_hash = INSTALLER.version_tree_sha256(version_root)
            prepared_identity = (previous, 1, 2, 3)
            selected_identity = (f"versions/{version}", 5, 6, 7)
            restored_identity = (previous, 8, 9, 10)
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "activate"
            )
            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "activate-restore"
            )
            tree_hash_calls = 0

            def tree_hash(_: pathlib.Path) -> str:
                nonlocal tree_hash_calls
                tree_hash_calls += 1
                return expected_tree_hash if tree_hash_calls < 4 else "f" * 64

            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "version_tree_sha256",
                    side_effect=tree_hash,
                ),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(
                        prepared_identity,
                        restored_identity,
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=(
                        (selected_identity, selector_path),
                        (restored_identity, restore_transition),
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "run_smoke",
                    return_value={"main": {"status": "pass"}},
                ),
            ):
                with self.assertRaisesRegex(RuntimeError, "changed after activation smoke"):
                    INSTALLER.activate(arguments)
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"], version, arguments.plan_id, "activate"
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("failed-restored", failure["status"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(restored_identity),
                failure["restoredIdentity"],
            )

    def test_activate_post_smoke_config_drift_journals_restoration(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(
                root,
                version,
                selector_before=previous,
            )
            _, runtime_configuration, _ = INSTALLER.release_configuration_paths(
                paths,
                version,
            )
            runtime_hash = INSTALLER.sha256_file(runtime_configuration)
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-config-drift",
            )
            prepared_identity = (previous, 1, 2, 3)
            selected_identity = (f"versions/{version}", 5, 6, 7)
            restored_identity = (previous, 8, 9, 10)
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "activate"
            )
            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "activate-restore"
            )

            def drift_during_smoke(*_: Any) -> dict[str, Any]:
                runtime_configuration.write_text(
                    "RUNTIME=drifted-after-smoke\n",
                    encoding="utf-8",
                )
                return {"main": {"status": "pass"}}

            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(
                        prepared_identity,
                        selected_identity,
                        restored_identity,
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=(
                        (selected_identity, selector_path),
                        (restored_identity, restore_transition),
                    ),
                ) as reconcile,
                mock.patch.object(
                    INSTALLER,
                    "run_smoke",
                    side_effect=drift_during_smoke,
                ),
            ):
                with self.assertRaisesRegex(
                    RuntimeError,
                    "configuration, or selector changed after activation smoke",
                ):
                    INSTALLER.activate(arguments)
            self.assertEqual(2, reconcile.call_count)
            self.assertNotEqual(
                runtime_hash,
                INSTALLER.sha256_file(runtime_configuration),
            )
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"],
                    version,
                    arguments.plan_id,
                    "activate",
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("failed-restored", failure["status"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(restored_identity),
                failure["restoredIdentity"],
            )

    def test_activate_reentry_restores_before_drifted_candidate_validation(
        self,
    ) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        candidate = f"versions/{version}"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(
                root,
                version,
                selector_before=previous,
            )
            version_root = paths["versions"] / version
            expected_tree_hash = INSTALLER.version_tree_sha256(version_root)
            _, runtime_configuration, _ = INSTALLER.release_configuration_paths(
                paths,
                version,
            )
            runtime_hash = INSTALLER.sha256_file(runtime_configuration)
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-restore-reentry",
            )
            previous_identity = (previous, 1, 2, 3)
            selected_identity = (candidate, 5, 6, 7)
            restored_identity = (previous, 8, 9, 10)

            def select_then_crash_restore(*call: Any) -> Any:
                operation = call[2]
                if operation == "activate":
                    selector_path = write_selector_transition_receipt(
                        call[1],
                        operation,
                        version,
                        arguments.plan_id,
                        previous_identity,
                        selected_identity,
                        expected_hash,
                        expected_tree_hash,
                    )
                    return selected_identity, selector_path
                self.assertEqual("activate-restore", operation)
                raise SystemExit("crash after restore intent")

            def drift_during_smoke(*_: Any) -> dict[str, Any]:
                (version_root / "post-smoke-drift.bin").write_bytes(b"drift")
                runtime_configuration.write_text(
                    "RUNTIME=drifted-after-smoke\n",
                    encoding="utf-8",
                )
                return {"main": {"status": "pass"}}

            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=previous_identity,
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=select_then_crash_restore,
                ),
                mock.patch.object(
                    INSTALLER,
                    "run_smoke",
                    side_effect=drift_during_smoke,
                ) as first_smoke,
            ):
                with self.assertRaisesRegex(RuntimeError, "restoration is incomplete"):
                    INSTALLER.activate(arguments)
            first_smoke.assert_called_once()
            restore_intent = INSTALLER.selector_restore_intent_path(
                paths["manifests"],
                version,
                arguments.plan_id,
                "activate",
            )
            self.assertTrue(restore_intent.is_file())
            self.assertNotEqual(
                expected_tree_hash,
                INSTALLER.version_tree_sha256(version_root),
            )
            self.assertNotEqual(
                runtime_hash,
                INSTALLER.sha256_file(runtime_configuration),
            )

            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"],
                version,
                arguments.plan_id,
                "activate-restore",
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=restored_identity,
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    return_value=(restored_identity, restore_transition),
                ) as recover,
                mock.patch.object(INSTALLER, "run_smoke") as replay_smoke,
                mock.patch.object(
                    INSTALLER,
                    "require_prepared_candidate",
                ) as revalidate_candidate,
            ):
                with self.assertRaisesRegex(
                    RuntimeError,
                    "previously failed and was restored",
                ):
                    INSTALLER.activate(arguments)
            replay_smoke.assert_not_called()
            revalidate_candidate.assert_not_called()
            self.assertEqual(1, recover.call_count)
            self.assertEqual("activate-restore", recover.call_args.args[2])
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"],
                    version,
                    arguments.plan_id,
                    "activate",
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("failed-restored", failure["status"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(restored_identity),
                failure["restoredIdentity"],
            )

    def test_interrupted_activation_with_selector_receipt_restores_first(
        self,
    ) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        candidate = f"versions/{version}"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(
                root,
                version,
                selector_before=previous,
            )
            version_root = paths["versions"] / version
            expected_tree_hash = INSTALLER.version_tree_sha256(version_root)
            _, runtime_configuration, _ = INSTALLER.release_configuration_paths(
                paths,
                version,
            )
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-interrupted-receipt",
            )
            previous_identity = (previous, 1, 2, 3)
            selected_identity = (candidate, 5, 6, 7)
            restored_identity = (previous, 8, 9, 10)

            def publish_selector_then_crash(*call: Any) -> Any:
                write_selector_transition_receipt(
                    call[1],
                    "activate",
                    version,
                    arguments.plan_id,
                    previous_identity,
                    selected_identity,
                    expected_hash,
                    expected_tree_hash,
                )
                raise SystemExit("crash after selector receipt")

            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=previous_identity,
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=publish_selector_then_crash,
                ),
                mock.patch.object(INSTALLER, "run_smoke") as first_smoke,
            ):
                with self.assertRaisesRegex(SystemExit, "selector receipt"):
                    INSTALLER.activate(arguments)
            first_smoke.assert_not_called()
            self.assertTrue(
                INSTALLER.activation_intent_path(
                    paths["manifests"], version, arguments.plan_id
                ).is_file()
            )
            self.assertFalse(
                INSTALLER.selector_restore_intent_path(
                    paths["manifests"], version, arguments.plan_id, "activate"
                ).exists()
            )
            (version_root / "interrupted-drift.bin").write_bytes(b"drift")
            runtime_configuration.write_text(
                "RUNTIME=interrupted-drift\n",
                encoding="utf-8",
            )

            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"],
                version,
                arguments.plan_id,
                "activate-restore",
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=restored_identity,
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    return_value=(restored_identity, restore_transition),
                ) as recover,
                mock.patch.object(
                    INSTALLER,
                    "require_prepared_candidate",
                ) as revalidate_candidate,
                mock.patch.object(INSTALLER, "run_smoke") as replay_smoke,
            ):
                with self.assertRaisesRegex(
                    RuntimeError,
                    "interrupted Cortex activate was restored",
                ):
                    INSTALLER.activate(arguments)
            replay_smoke.assert_not_called()
            revalidate_candidate.assert_not_called()
            self.assertEqual("activate-restore", recover.call_args.args[2])
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"],
                    version,
                    arguments.plan_id,
                    "activate",
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("InterruptedOperation", failure["failureType"])
            self.assertEqual("failed-restored", failure["status"])

    def test_activation_reconstructs_receipt_after_durable_rename_crash(
        self,
    ) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        candidate = f"versions/{version}"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(
                root,
                version,
                selector_before=previous,
            )
            version_root = paths["versions"] / version
            _, runtime_configuration, _ = INSTALLER.release_configuration_paths(
                paths,
                version,
            )
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-rename-crash",
            )
            previous_identity = (previous, 1, 2, 3)
            selected_identity = (candidate, 5, 6, 7)
            restored_identity = (previous, 8, 9, 10)
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"],
                version,
                arguments.plan_id,
                "activate",
            )
            real_publish = INSTALLER.write_protected_json_no_replace

            def crash_before_selector_receipt(
                path: pathlib.Path,
                value: dict[str, Any],
                group_id: int | None,
                final_mode: int = 0o640,
            ) -> None:
                if path == selector_path:
                    raise SystemExit("crash before selector receipt")
                real_publish(path, value, group_id, final_mode)

            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(
                        previous_identity,
                        previous_identity,
                        selected_identity,
                        selected_identity,
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "atomic_current_link",
                    return_value=previous,
                ) as select,
                mock.patch.object(
                    INSTALLER.os.path,
                    "relpath",
                    return_value=candidate,
                ),
                mock.patch.object(
                    INSTALLER,
                    "write_protected_json_no_replace",
                    side_effect=crash_before_selector_receipt,
                ),
                mock.patch.object(INSTALLER, "run_smoke") as first_smoke,
            ):
                with self.assertRaisesRegex(SystemExit, "before selector receipt"):
                    INSTALLER.activate(arguments)
            select.assert_called_once()
            first_smoke.assert_not_called()
            self.assertFalse(selector_path.exists())
            self.assertTrue(
                INSTALLER.activation_intent_path(
                    paths["manifests"], version, arguments.plan_id
                ).is_file()
            )
            (version_root / "rename-crash-drift.bin").write_bytes(b"drift")
            runtime_configuration.write_text(
                "RUNTIME=rename-crash-drift\n",
                encoding="utf-8",
            )

            def relative_target(
                target: pathlib.Path,
                component_root: pathlib.Path,
            ) -> str:
                return pathlib.Path(target).relative_to(component_root).as_posix()

            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"],
                version,
                arguments.plan_id,
                "activate-restore",
            )
            identity_reads = (
                selected_identity,
                selected_identity,
                selected_identity,
                selected_identity,
                selected_identity,
                selected_identity,
                restored_identity,
                restored_identity,
                restored_identity,
                restored_identity,
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=identity_reads,
                ),
                mock.patch.object(
                    INSTALLER,
                    "atomic_current_link",
                    return_value=candidate,
                ) as restore,
                mock.patch.object(
                    INSTALLER.os.path,
                    "relpath",
                    side_effect=relative_target,
                ),
                mock.patch.object(
                    INSTALLER,
                    "require_prepared_candidate",
                ) as revalidate_candidate,
                mock.patch.object(INSTALLER, "run_smoke") as replay_smoke,
            ):
                with self.assertRaisesRegex(
                    RuntimeError,
                    "interrupted Cortex activate was restored",
                ):
                    INSTALLER.activate(arguments)
            replay_smoke.assert_not_called()
            revalidate_candidate.assert_not_called()
            restore.assert_called_once()
            self.assertTrue(selector_path.is_file())
            self.assertTrue(restore_transition.is_file())
            selector_receipt = json.loads(selector_path.read_text(encoding="utf-8"))
            self.assertEqual(
                INSTALLER.selector_identity_receipt(selected_identity),
                selector_receipt["currentIdentity"],
            )
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"],
                    version,
                    arguments.plan_id,
                    "activate",
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("InterruptedOperation", failure["failureType"])
            self.assertEqual("failed-restored", failure["status"])

    def test_activate_receipt_publication_failure_restores_before_commit(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        smoke = {"main": {"status": "pass"}, "auditor": {"status": "pass"}}
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(
                root,
                version,
                selector_before=previous,
            )
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-candidate",
            )
            prepared_identity = (previous, 1, 2, 3)
            selected_identity = (f"versions/{version}", 5, 6, 7)
            restored_identity = (previous, 8, 9, 10)
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "activate"
            )
            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "activate-restore"
            )
            real_publish = INSTALLER.write_protected_json_no_replace
            publication_failed = False

            def publish_or_fail(
                path: pathlib.Path,
                value: dict[str, Any],
                group_id: int | None,
                final_mode: int = 0o640,
            ) -> None:
                nonlocal publication_failed
                if path.name.endswith("-activated.json") and not publication_failed:
                    publication_failed = True
                    raise OSError("receipt publication failed")
                real_publish(path, value, group_id, final_mode)

            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(
                        prepared_identity,
                        selected_identity,
                        restored_identity,
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=(
                        (selected_identity, selector_path),
                        (restored_identity, restore_transition),
                    ),
                ) as reconcile,
                mock.patch.object(
                    INSTALLER,
                    "run_smoke",
                    return_value=smoke,
                ) as run_smoke,
                mock.patch.object(
                    INSTALLER,
                    "write_protected_json_no_replace",
                    side_effect=publish_or_fail,
                ),
            ):
                with self.assertRaisesRegex(OSError, "receipt publication failed"):
                    INSTALLER.activate(arguments)
            self.assertEqual(2, reconcile.call_count)
            self.assertEqual(1, run_smoke.call_count)
            self.assertTrue(
                INSTALLER.activation_intent_path(
                    paths["manifests"], version, "activate-candidate"
                ).is_file()
            )
            self.assertFalse(
                INSTALLER.activated_receipt_path(
                    paths["manifests"], version, "activate-candidate"
                ).exists()
            )
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"],
                    version,
                    arguments.plan_id,
                    "activate",
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("failed-restored", failure["status"])
            self.assertEqual("OSError", failure["failureType"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(restored_identity),
                failure["restoredIdentity"],
            )

    def test_activate_interruption_after_selection_restores_without_commit(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        candidate = f"versions/{version}"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(
                root,
                version,
                selector_before=previous,
            )
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-crash-reentry",
            )
            previous_identity = (previous, 1, 2, 3)
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=previous_identity,
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=SystemExit("simulated crash"),
                ),
            ):
                with self.assertRaisesRegex(SystemExit, "simulated crash"):
                    INSTALLER.activate(arguments)

            intent_path = INSTALLER.activation_intent_path(
                paths["manifests"], version, "activate-crash-reentry"
            )
            self.assertTrue(intent_path.is_file())
            candidate_identity = (candidate, 5, 6, 7)
            restored_identity = (previous, 8, 9, 10)
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"],
                version,
                arguments.plan_id,
                "activate",
            )
            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"],
                version,
                arguments.plan_id,
                "activate-restore",
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(candidate_identity, restored_identity),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=(
                        (candidate_identity, selector_path),
                        (restored_identity, restore_transition),
                    ),
                ) as recover,
                mock.patch.object(INSTALLER, "run_smoke") as replay_smoke,
            ):
                with self.assertRaisesRegex(
                    RuntimeError,
                    "interrupted Cortex activate was restored",
                ):
                    INSTALLER.activate(arguments)
            self.assertEqual(2, recover.call_count)
            replay_smoke.assert_not_called()
            self.assertFalse(
                INSTALLER.activated_receipt_path(
                    paths["manifests"], version, "activate-crash-reentry"
                ).exists()
            )
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"],
                    version,
                    arguments.plan_id,
                    "activate",
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("InterruptedOperation", failure["failureType"])
            self.assertEqual("failed-restored", failure["status"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(restored_identity),
                failure["restoredIdentity"],
            )

    def test_activate_records_previous_selector_after_exact_smoke(self) -> None:
        version = "1.2026.08.03.10.20.A"
        previous = "versions/1.2026.07.27.13.20.A"
        smoke = {"main": {"status": "pass"}, "auditor": {"status": "pass"}}
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(
                root,
                version,
                selector_before=previous,
            )
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-candidate",
            )
            prepared_identity = (previous, 1, 2, 3)
            selected_identity = (f"versions/{version}", 5, 6, 99)
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "activate"
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(
                        prepared_identity,
                        selected_identity,
                        selected_identity,
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    return_value=(selected_identity, selector_path),
                ) as select,
                mock.patch.object(INSTALLER, "run_smoke", return_value=smoke),
                mock.patch.object(
                    INSTALLER,
                    "finish_failed_selector_restore",
                ) as restore,
                contextlib.redirect_stdout(io.StringIO()),
            ):
                self.assertEqual(0, INSTALLER.activate(arguments))

            restore.assert_not_called()
            select.assert_called_once_with(
                paths["component"],
                paths["manifests"],
                "activate",
                version,
                "activate-candidate",
                paths["versions"] / version,
                prepared_identity,
                expected_hash,
                mock.ANY,
                42,
            )
            receipt = json.loads(
                INSTALLER.activated_receipt_path(
                    paths["manifests"], version, "activate-candidate"
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("activated", receipt["status"])
            self.assertEqual(previous, receipt["previousCurrent"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(selected_identity),
                receipt["currentIdentity"],
            )
            self.assertEqual(
                INSTALLER.selector_identity_sha256(selected_identity),
                receipt["currentIdentitySha256"],
            )
            self.assertEqual(selector_path.name, receipt["selectorTransition"])
            self.assertTrue(receipt["rollbackRetained"])
            self.assertEqual(smoke, receipt["smoke"])

    def test_idempotent_activation_rejects_mismatched_previous_selector(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        smoke_result = {"main": {"status": "pass"}, "auditor": {"status": "pass"}}
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(
                root,
                version,
                selector_before=previous,
            )
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-candidate",
            )
            prepared_identity = (previous, 1, 2, 3)
            selected_identity = (f"versions/{version}", 5, 6, 7)
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "activate"
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(
                        prepared_identity,
                        selected_identity,
                        selected_identity,
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    return_value=(selected_identity, selector_path),
                ),
                mock.patch.object(INSTALLER, "run_smoke", return_value=smoke_result),
                contextlib.redirect_stdout(io.StringIO()),
            ):
                self.assertEqual(0, INSTALLER.activate(arguments))

            receipt_path = INSTALLER.activated_receipt_path(
                paths["manifests"],
                version,
                "activate-candidate",
            )
            receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
            receipt["previousCurrent"] = None
            receipt_path.write_text(json.dumps(receipt), encoding="utf-8")
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=selected_identity,
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    return_value=(selected_identity, selector_path),
                ) as select,
                mock.patch.object(INSTALLER, "run_smoke") as rerun_smoke,
            ):
                with self.assertRaisesRegex(RuntimeError, "activation receipt"):
                    INSTALLER.activate(arguments)
            select.assert_called_once()
            rerun_smoke.assert_not_called()

    def test_idempotent_activation_rejects_runtime_config_drift_during_smoke(
        self,
    ) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        smoke_result = {"main": {"status": "pass"}, "auditor": {"status": "pass"}}
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths, expected_hash = write_prepared_candidate(
                root,
                version,
                selector_before=previous,
            )
            _, runtime_configuration, _ = INSTALLER.release_configuration_paths(
                paths,
                version,
            )
            runtime_hash = INSTALLER.sha256_file(runtime_configuration)
            arguments = argparse.Namespace(
                operation="activate",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                prepared_plan_id="prepare-candidate",
                plan_id="activate-idempotent-config-drift",
            )
            prepared_identity = (previous, 1, 2, 3)
            selected_identity = (f"versions/{version}", 5, 6, 7)
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"],
                version,
                arguments.plan_id,
                "activate",
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(
                        prepared_identity,
                        selected_identity,
                        selected_identity,
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    return_value=(selected_identity, selector_path),
                ),
                mock.patch.object(INSTALLER, "run_smoke", return_value=smoke_result),
                contextlib.redirect_stdout(io.StringIO()),
            ):
                self.assertEqual(0, INSTALLER.activate(arguments))

            def drift_during_replay(*_: Any) -> dict[str, Any]:
                runtime_configuration.write_text(
                    "RUNTIME=idempotent-smoke-drift\n",
                    encoding="utf-8",
                )
                return smoke_result

            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=selected_identity,
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    return_value=(selected_identity, selector_path),
                ) as select,
                mock.patch.object(
                    INSTALLER,
                    "run_smoke",
                    side_effect=drift_during_replay,
                ) as replay_smoke,
                mock.patch.object(
                    INSTALLER,
                    "finish_failed_selector_restore",
                ) as restore,
            ):
                with self.assertRaisesRegex(
                    RuntimeError,
                    "configuration, or selector changed during idempotent smoke",
                ):
                    INSTALLER.activate(arguments)
            select.assert_called_once()
            replay_smoke.assert_called_once()
            restore.assert_not_called()
            self.assertNotEqual(
                runtime_hash,
                INSTALLER.sha256_file(runtime_configuration),
            )

    def test_rollback_rejects_untrusted_manifest_before_selector_change(self) -> None:
        version = "1.2026.08.03.12.20.B"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            target = root / "apps" / "cortex" / "versions" / version
            target.mkdir(parents=True)
            (root / "manifests" / "cortex").mkdir(parents=True)
            manifest = integrated_manifest(version)
            (target / "package-manifest.json").write_text(
                json.dumps(manifest),
                encoding="utf-8",
            )
            arguments = argparse.Namespace(
                operation="rollback",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256="0" * 64,
                expected_current_version="1.2026.07.27.13.20.A",
                expected_current_identity_sha256="f" * 64,
                expected_target_tree_sha256="e" * 64,
                expected_runtime_configuration_sha256=None,
                plan_id="rollback-candidate",
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                ) as select,
            ):
                with self.assertRaisesRegex(RuntimeError, "approved SHA-256"):
                    INSTALLER.rollback(arguments)
            select.assert_not_called()

    def test_rollback_rejects_same_target_with_different_selector_identity(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous_version = "1.2026.07.27.13.20.A"
        previous = f"versions/{previous_version}"
        approved_identity = (previous, 1, 2, 3)
        changed_identity = (previous, 1, 99, 4)
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            target = root / "apps" / "cortex" / "versions" / version
            target.mkdir(parents=True)
            (root / "manifests" / "cortex").mkdir(parents=True)
            manifest = integrated_manifest(version)
            manifest_path = target / "package-manifest.json"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            expected_hash = INSTALLER.sha256_file(manifest_path)
            paths = INSTALLER.installation_paths(root)
            release_root, runtime_configuration, _ = (
                INSTALLER.release_configuration_paths(paths, version)
            )
            release_root.mkdir(parents=True)
            runtime_configuration.write_text("RUNTIME=rollback\n", encoding="utf-8")
            runtime_hash = INSTALLER.sha256_file(runtime_configuration)
            arguments = argparse.Namespace(
                operation="rollback",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                expected_current_version=previous_version,
                expected_current_identity_sha256=(
                    INSTALLER.selector_identity_sha256(approved_identity)
                ),
                expected_target_tree_sha256=(
                    INSTALLER.version_tree_sha256(target)
                ),
                expected_runtime_configuration_sha256=runtime_hash,
                plan_id="rollback-stale-checkpoint",
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "validate_rollback_target",
                    return_value="integrated-schema-3",
                ),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=changed_identity,
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                ) as select,
            ):
                with self.assertRaisesRegex(RuntimeError, "approved rollback checkpoint"):
                    INSTALLER.rollback(arguments)
            select.assert_not_called()

    def test_rollback_restores_previous_selector_when_schema_smoke_fails(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous = "versions/1.2026.07.27.13.20.A"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            target = root / "apps" / "cortex" / "versions" / version
            target.mkdir(parents=True)
            (root / "manifests" / "cortex").mkdir(parents=True)
            manifest = integrated_manifest(version)
            manifest_path = target / "package-manifest.json"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            expected_hash = INSTALLER.sha256_file(manifest_path)
            current_identity = (previous, 1, 2, 3)
            paths = INSTALLER.installation_paths(root)
            release_root, runtime_configuration, _ = (
                INSTALLER.release_configuration_paths(paths, version)
            )
            release_root.mkdir(parents=True)
            runtime_configuration.write_text("RUNTIME=rollback\n", encoding="utf-8")
            runtime_hash = INSTALLER.sha256_file(runtime_configuration)
            target_tree_hash = INSTALLER.version_tree_sha256(target)
            arguments = argparse.Namespace(
                operation="rollback",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                expected_current_version="1.2026.07.27.13.20.A",
                expected_current_identity_sha256=(
                    INSTALLER.selector_identity_sha256(current_identity)
                ),
                expected_target_tree_sha256=target_tree_hash,
                expected_runtime_configuration_sha256=runtime_hash,
                plan_id="rollback-candidate",
            )
            selected_identity = (f"versions/{version}", 5, 6, 7)
            restored_identity = (previous, 8, 9, 10)
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "rollback"
            )
            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "rollback-restore"
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(current_identity, restored_identity),
                ),
                mock.patch.object(
                    INSTALLER,
                    "validate_rollback_target",
                    return_value="integrated-schema-3",
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=(
                        (selected_identity, selector_path),
                        (restored_identity, restore_transition),
                    ),
                ) as reconcile,
                mock.patch.object(
                    INSTALLER,
                    "run_rollback_smoke",
                    side_effect=RuntimeError("rollback smoke failed"),
                ),
            ):
                with self.assertRaisesRegex(RuntimeError, "rollback smoke failed"):
                    INSTALLER.rollback(arguments)
            self.assertEqual(2, reconcile.call_count)
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"], version, arguments.plan_id, "rollback"
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("failed-restored", failure["status"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(restored_identity),
                failure["restoredIdentity"],
            )

    def test_rollback_records_a_checkpoint_bound_durable_receipt(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous_version = "1.2026.07.27.13.20.A"
        previous = f"versions/{previous_version}"
        current_identity = (previous, 1, 2, 3)
        smoke = {"main": {"status": "pass"}, "auditor": {"status": "pass"}}
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            target = root / "apps" / "cortex" / "versions" / version
            target.mkdir(parents=True)
            manifests = root / "manifests" / "cortex"
            manifests.mkdir(parents=True)
            manifest = integrated_manifest(version)
            manifest_path = target / "package-manifest.json"
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            expected_hash = INSTALLER.sha256_file(manifest_path)
            identity_hash = INSTALLER.selector_identity_sha256(current_identity)
            paths = INSTALLER.installation_paths(root)
            release_root, runtime_configuration, _ = (
                INSTALLER.release_configuration_paths(paths, version)
            )
            release_root.mkdir(parents=True)
            runtime_configuration.write_text("RUNTIME=rollback\n", encoding="utf-8")
            runtime_hash = INSTALLER.sha256_file(runtime_configuration)
            target_tree_hash = INSTALLER.version_tree_sha256(target)
            arguments = argparse.Namespace(
                operation="rollback",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                expected_current_version=previous_version,
                expected_current_identity_sha256=identity_hash,
                expected_target_tree_sha256=target_tree_hash,
                expected_runtime_configuration_sha256=runtime_hash,
                plan_id="rollback-receipt",
            )
            selected_identity = (f"versions/{version}", 5, 6, 99)
            selector_path = INSTALLER.selector_transition_path(
                manifests, version, arguments.plan_id, "rollback"
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "validate_rollback_target",
                    return_value="integrated-schema-3",
                ),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(
                        current_identity,
                        selected_identity,
                        selected_identity,
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    return_value=(selected_identity, selector_path),
                ) as reconcile,
                mock.patch.object(
                    INSTALLER,
                    "run_rollback_smoke",
                    return_value=("integrated-schema-3", smoke),
                ),
                contextlib.redirect_stdout(io.StringIO()),
            ):
                self.assertEqual(0, INSTALLER.rollback(arguments))
            receipt_path = INSTALLER.rollback_receipt_path(
                manifests, version, "rollback-receipt"
            )
            receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
            self.assertEqual("rolled-back", receipt["status"])
            self.assertEqual(identity_hash, receipt["previousCurrentIdentitySha256"])
            self.assertEqual(f"versions/{version}", receipt["current"])
            self.assertEqual(runtime_hash, receipt["runtimeConfigurationSha256"])
            self.assertEqual(target_tree_hash, receipt["targetTreeSha256"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(selected_identity),
                receipt["currentIdentity"],
            )
            self.assertEqual(selector_path.name, receipt["selectorTransition"])
            reconcile.assert_called_once()
            self.assertTrue(
                INSTALLER.rollback_intent_path(
                    manifests, version, "rollback-receipt"
                ).is_file()
            )

    def test_rollback_receipt_publication_failure_restores_before_commit(
        self,
    ) -> None:
        version = "1.2026.08.03.12.20.B"
        previous_version = "1.2026.07.27.13.20.A"
        previous = f"versions/{previous_version}"
        current_identity = (previous, 1, 2, 3)
        selected_identity = (f"versions/{version}", 5, 6, 7)
        restored_identity = (previous, 8, 9, 10)
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths = INSTALLER.installation_paths(root)
            target = paths["versions"] / version
            target.mkdir(parents=True)
            paths["manifests"].mkdir(parents=True)
            manifest_path = target / "package-manifest.json"
            manifest_path.write_text(
                json.dumps(integrated_manifest(version)),
                encoding="utf-8",
            )
            expected_hash = INSTALLER.sha256_file(manifest_path)
            release_root, runtime_configuration, _ = (
                INSTALLER.release_configuration_paths(paths, version)
            )
            release_root.mkdir(parents=True)
            runtime_configuration.write_text(
                "RUNTIME=rollback\n",
                encoding="utf-8",
            )
            runtime_hash = INSTALLER.sha256_file(runtime_configuration)
            target_tree_hash = INSTALLER.version_tree_sha256(target)
            arguments = argparse.Namespace(
                operation="rollback",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                expected_current_version=previous_version,
                expected_current_identity_sha256=(
                    INSTALLER.selector_identity_sha256(current_identity)
                ),
                expected_target_tree_sha256=target_tree_hash,
                expected_runtime_configuration_sha256=runtime_hash,
                plan_id="rollback-receipt-failure",
            )
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "rollback"
            )
            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "rollback-restore"
            )
            receipt_path = INSTALLER.rollback_receipt_path(
                paths["manifests"], version, arguments.plan_id
            )
            real_publish = INSTALLER.write_protected_json_no_replace

            def publish_or_fail(
                path: pathlib.Path,
                value: dict[str, Any],
                group_id: int | None,
                final_mode: int = 0o640,
            ) -> None:
                if path == receipt_path:
                    raise OSError("rollback receipt publication failed")
                real_publish(path, value, group_id, final_mode)

            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "validate_rollback_target",
                    return_value="integrated-schema-3",
                ),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(
                        current_identity,
                        selected_identity,
                        restored_identity,
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=(
                        (selected_identity, selector_path),
                        (restored_identity, restore_transition),
                    ),
                ) as reconcile,
                mock.patch.object(
                    INSTALLER,
                    "run_rollback_smoke",
                    return_value=(
                        "integrated-schema-3",
                        {"main": {"status": "pass"}},
                    ),
                ) as smoke,
                mock.patch.object(
                    INSTALLER,
                    "write_protected_json_no_replace",
                    side_effect=publish_or_fail,
                ),
            ):
                with self.assertRaisesRegex(
                    OSError,
                    "rollback receipt publication failed",
                ):
                    INSTALLER.rollback(arguments)
            self.assertEqual(2, reconcile.call_count)
            smoke.assert_called_once()
            self.assertFalse(receipt_path.exists())
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"],
                    version,
                    arguments.plan_id,
                    "rollback",
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("OSError", failure["failureType"])
            self.assertEqual("failed-restored", failure["status"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(restored_identity),
                failure["restoredIdentity"],
            )

    def test_rollback_rejects_unapproved_target_tree_before_selection(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous_version = "1.2026.07.27.13.20.A"
        previous = f"versions/{previous_version}"
        current_identity = (previous, 1, 2, 3)
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths = INSTALLER.installation_paths(root)
            target = paths["versions"] / version
            target.mkdir(parents=True)
            paths["manifests"].mkdir(parents=True)
            manifest_path = target / "package-manifest.json"
            manifest_path.write_text(
                json.dumps(integrated_manifest(version)),
                encoding="utf-8",
            )
            release_root, runtime_configuration, _ = (
                INSTALLER.release_configuration_paths(paths, version)
            )
            release_root.mkdir(parents=True)
            runtime_configuration.write_text("RUNTIME=rollback\n", encoding="utf-8")
            arguments = argparse.Namespace(
                operation="rollback",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=INSTALLER.sha256_file(manifest_path),
                expected_current_version=previous_version,
                expected_current_identity_sha256=(
                    INSTALLER.selector_identity_sha256(current_identity)
                ),
                expected_target_tree_sha256="0" * 64,
                expected_runtime_configuration_sha256=(
                    INSTALLER.sha256_file(runtime_configuration)
                ),
                plan_id="rollback-tree-mismatch",
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "validate_rollback_target",
                    return_value="integrated-schema-3",
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                ) as select,
            ):
                with self.assertRaisesRegex(RuntimeError, "approved checkpoint"):
                    INSTALLER.rollback(arguments)
            select.assert_not_called()

    def test_rollback_post_smoke_tree_drift_journals_restoration(self) -> None:
        version = "1.2026.08.03.12.20.B"
        previous_version = "1.2026.07.27.13.20.A"
        previous = f"versions/{previous_version}"
        current_identity = (previous, 1, 2, 3)
        selected_identity = (f"versions/{version}", 5, 6, 7)
        restored_identity = (previous, 8, 9, 10)
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths = INSTALLER.installation_paths(root)
            target = paths["versions"] / version
            target.mkdir(parents=True)
            paths["manifests"].mkdir(parents=True)
            manifest_path = target / "package-manifest.json"
            manifest_path.write_text(
                json.dumps(integrated_manifest(version)),
                encoding="utf-8",
            )
            release_root, runtime_configuration, _ = (
                INSTALLER.release_configuration_paths(paths, version)
            )
            release_root.mkdir(parents=True)
            runtime_configuration.write_text("RUNTIME=rollback\n", encoding="utf-8")
            target_tree_hash = INSTALLER.version_tree_sha256(target)
            arguments = argparse.Namespace(
                operation="rollback",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=INSTALLER.sha256_file(manifest_path),
                expected_current_version=previous_version,
                expected_current_identity_sha256=(
                    INSTALLER.selector_identity_sha256(current_identity)
                ),
                expected_target_tree_sha256=target_tree_hash,
                expected_runtime_configuration_sha256=(
                    INSTALLER.sha256_file(runtime_configuration)
                ),
                plan_id="rollback-tree-drift",
            )
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "rollback"
            )
            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "rollback-restore"
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "validate_rollback_target",
                    return_value="integrated-schema-3",
                ),
                mock.patch.object(
                    INSTALLER,
                    "version_tree_sha256",
                    side_effect=(target_tree_hash, "f" * 64),
                ),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(current_identity, restored_identity),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=(
                        (selected_identity, selector_path),
                        (restored_identity, restore_transition),
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "run_rollback_smoke",
                    return_value=("integrated-schema-3", {"status": "pass"}),
                ),
            ):
                with self.assertRaisesRegex(RuntimeError, "changed after smoke"):
                    INSTALLER.rollback(arguments)
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"], version, arguments.plan_id, "rollback"
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("failed-restored", failure["status"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(restored_identity),
                failure["restoredIdentity"],
            )

    def test_rollback_reentry_restores_before_drifted_config_validation(
        self,
    ) -> None:
        version = "1.2026.08.03.12.20.B"
        previous_version = "1.2026.07.27.13.20.A"
        previous = f"versions/{previous_version}"
        candidate = f"versions/{version}"
        current_identity = (previous, 1, 2, 3)
        selected_identity = (candidate, 5, 6, 7)
        restored_identity = (previous, 8, 9, 10)
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths = INSTALLER.installation_paths(root)
            target = paths["versions"] / version
            target.mkdir(parents=True)
            paths["manifests"].mkdir(parents=True)
            manifest_path = target / "package-manifest.json"
            manifest_path.write_text(
                json.dumps(integrated_manifest(version)),
                encoding="utf-8",
            )
            expected_hash = INSTALLER.sha256_file(manifest_path)
            release_root, runtime_configuration, _ = (
                INSTALLER.release_configuration_paths(paths, version)
            )
            release_root.mkdir(parents=True)
            runtime_configuration.write_text(
                "RUNTIME=rollback\n",
                encoding="utf-8",
            )
            runtime_hash = INSTALLER.sha256_file(runtime_configuration)
            target_tree_hash = INSTALLER.version_tree_sha256(target)
            arguments = argparse.Namespace(
                operation="rollback",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                expected_current_version=previous_version,
                expected_current_identity_sha256=(
                    INSTALLER.selector_identity_sha256(current_identity)
                ),
                expected_target_tree_sha256=target_tree_hash,
                expected_runtime_configuration_sha256=runtime_hash,
                plan_id="rollback-restore-reentry",
            )

            def select_then_crash_restore(*call: Any) -> Any:
                operation = call[2]
                if operation == "rollback":
                    selector_path = write_selector_transition_receipt(
                        call[1],
                        operation,
                        version,
                        arguments.plan_id,
                        current_identity,
                        selected_identity,
                        expected_hash,
                        target_tree_hash,
                    )
                    return selected_identity, selector_path
                self.assertEqual("rollback-restore", operation)
                raise SystemExit("crash after restore intent")

            def drift_during_smoke(*_: Any) -> tuple[str, dict[str, Any]]:
                runtime_configuration.write_text(
                    "RUNTIME=drifted-after-smoke\n",
                    encoding="utf-8",
                )
                return "integrated-schema-3", {"main": {"status": "pass"}}

            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "validate_rollback_target",
                    return_value="integrated-schema-3",
                ),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(current_identity, selected_identity),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=select_then_crash_restore,
                ),
                mock.patch.object(
                    INSTALLER,
                    "run_rollback_smoke",
                    side_effect=drift_during_smoke,
                ) as first_smoke,
            ):
                with self.assertRaisesRegex(RuntimeError, "restoration is incomplete"):
                    INSTALLER.rollback(arguments)
            first_smoke.assert_called_once()
            restore_intent = INSTALLER.selector_restore_intent_path(
                paths["manifests"],
                version,
                arguments.plan_id,
                "rollback",
            )
            self.assertTrue(restore_intent.is_file())
            self.assertNotEqual(
                runtime_hash,
                INSTALLER.sha256_file(runtime_configuration),
            )

            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"],
                version,
                arguments.plan_id,
                "rollback-restore",
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=restored_identity,
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    return_value=(restored_identity, restore_transition),
                ) as recover,
                mock.patch.object(
                    INSTALLER,
                    "validate_rollback_target",
                ) as revalidate_target,
                mock.patch.object(INSTALLER, "run_rollback_smoke") as replay_smoke,
            ):
                with self.assertRaisesRegex(
                    RuntimeError,
                    "previously failed and was restored",
                ):
                    INSTALLER.rollback(arguments)
            replay_smoke.assert_not_called()
            revalidate_target.assert_not_called()
            self.assertEqual(1, recover.call_count)
            self.assertEqual("rollback-restore", recover.call_args.args[2])
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"],
                    version,
                    arguments.plan_id,
                    "rollback",
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("failed-restored", failure["status"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(restored_identity),
                failure["restoredIdentity"],
            )

    def test_rollback_reconstructs_receipt_after_durable_rename_crash(
        self,
    ) -> None:
        version = "1.2026.08.03.12.20.B"
        previous_version = "1.2026.07.27.13.20.A"
        previous = f"versions/{previous_version}"
        candidate = f"versions/{version}"
        current_identity = (previous, 1, 2, 3)
        selected_identity = (candidate, 5, 6, 7)
        restored_identity = (previous, 8, 9, 10)
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths = INSTALLER.installation_paths(root)
            target = paths["versions"] / version
            target.mkdir(parents=True)
            paths["manifests"].mkdir(parents=True)
            manifest_path = target / "package-manifest.json"
            manifest_path.write_text(
                json.dumps(integrated_manifest(version)),
                encoding="utf-8",
            )
            expected_hash = INSTALLER.sha256_file(manifest_path)
            release_root, runtime_configuration, _ = (
                INSTALLER.release_configuration_paths(paths, version)
            )
            release_root.mkdir(parents=True)
            runtime_configuration.write_text(
                "RUNTIME=rollback\n",
                encoding="utf-8",
            )
            runtime_hash = INSTALLER.sha256_file(runtime_configuration)
            target_tree_hash = INSTALLER.version_tree_sha256(target)
            arguments = argparse.Namespace(
                operation="rollback",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=expected_hash,
                expected_current_version=previous_version,
                expected_current_identity_sha256=(
                    INSTALLER.selector_identity_sha256(current_identity)
                ),
                expected_target_tree_sha256=target_tree_hash,
                expected_runtime_configuration_sha256=runtime_hash,
                plan_id="rollback-rename-crash",
            )
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"],
                version,
                arguments.plan_id,
                "rollback",
            )
            real_publish = INSTALLER.write_protected_json_no_replace

            def crash_before_selector_receipt(
                path: pathlib.Path,
                value: dict[str, Any],
                group_id: int | None,
                final_mode: int = 0o640,
            ) -> None:
                if path == selector_path:
                    raise SystemExit("crash before selector receipt")
                real_publish(path, value, group_id, final_mode)

            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "validate_rollback_target",
                    return_value="integrated-schema-3",
                ),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(
                        current_identity,
                        current_identity,
                        selected_identity,
                        selected_identity,
                    ),
                ),
                mock.patch.object(
                    INSTALLER,
                    "atomic_current_link",
                    return_value=previous,
                ) as select,
                mock.patch.object(
                    INSTALLER.os.path,
                    "relpath",
                    return_value=candidate,
                ),
                mock.patch.object(
                    INSTALLER,
                    "write_protected_json_no_replace",
                    side_effect=crash_before_selector_receipt,
                ),
                mock.patch.object(INSTALLER, "run_rollback_smoke") as first_smoke,
            ):
                with self.assertRaisesRegex(SystemExit, "before selector receipt"):
                    INSTALLER.rollback(arguments)
            select.assert_called_once()
            first_smoke.assert_not_called()
            self.assertFalse(selector_path.exists())
            self.assertTrue(
                INSTALLER.rollback_intent_path(
                    paths["manifests"], version, arguments.plan_id
                ).is_file()
            )
            (target / "rename-crash-drift.bin").write_bytes(b"drift")
            runtime_configuration.write_text(
                "RUNTIME=rename-crash-drift\n",
                encoding="utf-8",
            )

            def relative_target(
                selected_target: pathlib.Path,
                component_root: pathlib.Path,
            ) -> str:
                return pathlib.Path(selected_target).relative_to(
                    component_root
                ).as_posix()

            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"],
                version,
                arguments.plan_id,
                "rollback-restore",
            )
            identity_reads = (
                selected_identity,
                selected_identity,
                selected_identity,
                selected_identity,
                selected_identity,
                selected_identity,
                restored_identity,
                restored_identity,
                restored_identity,
                restored_identity,
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=identity_reads,
                ),
                mock.patch.object(
                    INSTALLER,
                    "atomic_current_link",
                    return_value=candidate,
                ) as restore,
                mock.patch.object(
                    INSTALLER.os.path,
                    "relpath",
                    side_effect=relative_target,
                ),
                mock.patch.object(
                    INSTALLER,
                    "validate_rollback_target",
                ) as revalidate_target,
                mock.patch.object(INSTALLER, "run_rollback_smoke") as replay_smoke,
            ):
                with self.assertRaisesRegex(
                    RuntimeError,
                    "interrupted Cortex rollback was restored",
                ):
                    INSTALLER.rollback(arguments)
            replay_smoke.assert_not_called()
            revalidate_target.assert_not_called()
            restore.assert_called_once()
            self.assertTrue(selector_path.is_file())
            self.assertTrue(restore_transition.is_file())
            selector_receipt = json.loads(selector_path.read_text(encoding="utf-8"))
            self.assertEqual(
                INSTALLER.selector_identity_receipt(selected_identity),
                selector_receipt["currentIdentity"],
            )
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"],
                    version,
                    arguments.plan_id,
                    "rollback",
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("InterruptedOperation", failure["failureType"])
            self.assertEqual("failed-restored", failure["status"])

    def test_rollback_interruption_after_selection_restores_without_commit(
        self,
    ) -> None:
        version = "1.2026.08.03.12.20.B"
        previous_version = "1.2026.07.27.13.20.A"
        previous = f"versions/{previous_version}"
        current_identity = (previous, 1, 2, 3)
        selected_identity = (f"versions/{version}", 5, 6, 7)
        restored_identity = (previous, 8, 9, 10)
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "pegasus"
            paths = INSTALLER.installation_paths(root)
            target = paths["versions"] / version
            target.mkdir(parents=True)
            paths["manifests"].mkdir(parents=True)
            manifest_path = target / "package-manifest.json"
            manifest_path.write_text(
                json.dumps(integrated_manifest(version)),
                encoding="utf-8",
            )
            release_root, runtime_configuration, _ = (
                INSTALLER.release_configuration_paths(paths, version)
            )
            release_root.mkdir(parents=True)
            runtime_configuration.write_text("RUNTIME=rollback\n", encoding="utf-8")
            target_tree_hash = INSTALLER.version_tree_sha256(target)
            arguments = argparse.Namespace(
                operation="rollback",
                pegasus_root=root,
                target_version=version,
                expected_manifest_sha256=INSTALLER.sha256_file(manifest_path),
                expected_current_version=previous_version,
                expected_current_identity_sha256=(
                    INSTALLER.selector_identity_sha256(current_identity)
                ),
                expected_target_tree_sha256=target_tree_hash,
                expected_runtime_configuration_sha256=(
                    INSTALLER.sha256_file(runtime_configuration)
                ),
                plan_id="rollback-crash-replay",
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "validate_rollback_target",
                    return_value="integrated-schema-3",
                ),
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    return_value=current_identity,
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=SystemExit("selector crash"),
                ),
            ):
                with self.assertRaisesRegex(SystemExit, "selector crash"):
                    INSTALLER.rollback(arguments)
            intent_path = INSTALLER.rollback_intent_path(
                paths["manifests"], version, arguments.plan_id
            )
            self.assertTrue(intent_path.is_file())
            selector_path = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "rollback"
            )
            restore_transition = INSTALLER.selector_transition_path(
                paths["manifests"], version, arguments.plan_id, "rollback-restore"
            )
            with (
                portable_deployment_environment(),
                mock.patch.object(
                    INSTALLER,
                    "validate_rollback_target",
                ) as revalidate_target,
                mock.patch.object(
                    INSTALLER,
                    "read_current_link_identity",
                    side_effect=(selected_identity, restored_identity),
                ),
                mock.patch.object(
                    INSTALLER,
                    "reconcile_selector_transition",
                    side_effect=(
                        (selected_identity, selector_path),
                        (restored_identity, restore_transition),
                    ),
                ) as recover,
                mock.patch.object(
                    INSTALLER,
                    "run_rollback_smoke",
                ) as replay_smoke,
            ):
                with self.assertRaisesRegex(
                    RuntimeError,
                    "interrupted Cortex rollback was restored",
                ):
                    INSTALLER.rollback(arguments)
            revalidate_target.assert_not_called()
            replay_smoke.assert_not_called()
            self.assertEqual(2, recover.call_count)
            self.assertFalse(
                INSTALLER.rollback_receipt_path(
                    paths["manifests"], version, arguments.plan_id
                ).exists()
            )
            failure = json.loads(
                INSTALLER.operation_failure_path(
                    paths["manifests"],
                    version,
                    arguments.plan_id,
                    "rollback",
                ).read_text(encoding="utf-8")
            )
            self.assertEqual("InterruptedOperation", failure["failureType"])
            self.assertEqual("failed-restored", failure["status"])
            self.assertEqual(
                INSTALLER.selector_identity_receipt(restored_identity),
                failure["restoredIdentity"],
            )


if __name__ == "__main__":
    unittest.main()
