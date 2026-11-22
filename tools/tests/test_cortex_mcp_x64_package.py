from __future__ import annotations

import hashlib
import importlib.util
import json
import pathlib
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
import warnings
import zipfile
from unittest import mock


ROOT = pathlib.Path(__file__).resolve().parents[2]
TOOL_PATH = ROOT / "tools/Test-CortexMcpX64Package.py"
BUILDER_PATH = ROOT / "tools/New-CortexMcpX64Package.ps1"
SPEC = importlib.util.spec_from_file_location("cortex_x64_verifier", TOOL_PATH)
assert SPEC is not None and SPEC.loader is not None
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


class FakeArchive:
    def __init__(self, *entries: zipfile.ZipInfo) -> None:
        self._entries = list(entries)

    def infolist(self) -> list[zipfile.ZipInfo]:
        return self._entries


def zip_entry(name: str, *, external_attr: int = 0, create_system: int = 0, encrypted: bool = False) -> zipfile.ZipInfo:
    entry = zipfile.ZipInfo(name)
    entry.external_attr = external_attr
    entry.create_system = create_system
    entry.flag_bits = 1 if encrypted else 0
    entry.compress_type = zipfile.ZIP_STORED
    return entry


class X64PackageFixture:
    RELEASE = "1.2099.12.31.23.59.A"
    PACKAGE = "1.20991231.2359-a"

    def __init__(self, root: pathlib.Path) -> None:
        self.source = root / "source"
        self.source.mkdir()
        for relative in VERIFIER.SOURCE_FILES:
            path = self.source.joinpath(*pathlib.PurePosixPath(relative).parts)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f"fixture:{relative}\n", encoding="utf-8")
        (self.source / "src/PegasusCortex.Mcp/Knowledge").mkdir(parents=True)
        (self.source / "schemas").mkdir(exist_ok=True)
        (self.source / "VERSION.json").write_text(json.dumps({
            "releaseVersion": self.RELEASE,
            "nuGetVersion": self.PACKAGE,
            "assemblyVersion": "1.0.0.0",
            "fileVersion": "1.2099.1231.2359",
        }), encoding="utf-8")
        (self.source / "Directory.Version.props").write_text(
            "<Project><PropertyGroup>"
            f"<PegasusReleaseVersion>{self.RELEASE}</PegasusReleaseVersion>"
            f"<PegasusNuGetVersion>{self.PACKAGE}</PegasusNuGetVersion>"
            "<PegasusAssemblyVersion>1.0.0.0</PegasusAssemblyVersion>"
            "<PegasusFileVersion>1.2099.1231.2359</PegasusFileVersion>"
            "</PropertyGroup></Project>", encoding="utf-8")
        (self.source / "global.json").write_text(json.dumps({
            "sdk": {"version": "99.8.7", "rollForward": "disable", "allowPrerelease": False}
        }), encoding="utf-8")
        self.guide = b"# Offline guide\n"
        (self.source / "src/PegasusCortex.Mcp/Knowledge/continuity-guide.md").write_bytes(self.guide)
        self.schema = b'{"type":"object"}\n'
        (self.source / "schemas/example.schema.json").write_bytes(self.schema)
        self.project = self.source.joinpath(*pathlib.PurePosixPath(VERIFIER.PROJECT_ENTRY).parts)
        self.project.parent.mkdir(parents=True, exist_ok=True)
        self.project.write_text("<Project />\n", encoding="utf-8")
        self.lock = self.project.parent / "packages.lock.json"
        self.lock.write_text("{}\n", encoding="utf-8")
        self.local_deps = (json.dumps({
            "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0", "signature": ""},
            "targets": {
                ".NETCoreApp,Version=v10.0": {
                    "SQLitePCLRaw.lib.e_sqlite3/2.1.11": {
                        "runtimeTargets": {
                            "runtimes/linux-x64/native/libe_sqlite3.so": {
                                "rid": "linux-x64", "assetType": "native",
                            },
                            "runtimes/win-x64/native/e_sqlite3.dll": {
                                "rid": "win-x64", "assetType": "native",
                            },
                        }
                    }
                }
            },
        }, separators=(",", ":")) + "\n").encode("utf-8")
        self.runtime_asset = b"sqlite-native"
        self.package = root / "package.zip"
        self.build_result = root / "build-result.json"

    def write_package(
        self,
        *,
        tamper_payload: bool = False,
        duplicate: bool = False,
        omit_source: bool = False,
        omit_schema: bool = False,
        omit_lock: bool = False,
        tamper_build_result: bool = False,
        omit_runtime: bool = False,
        foreign_runtime: bool = False,
        nested_managed: bool = False,
    ) -> None:
        payload = {
            "app/PegasusCortex.Mcp.Local.dll": b"assembly",
            "app/PegasusCortex.Mcp.Local.deps.json": self.local_deps,
            "app/PegasusCortex.Mcp.Local.runtimeconfig.json": b"{}\n",
            "app/runtimes/win-x64/native/e_sqlite3.dll": self.runtime_asset,
            "content/continuity-guide.md": self.guide,
            "content/schemas/example.schema.json": self.schema,
        }
        if omit_runtime:
            del payload["app/runtimes/win-x64/native/e_sqlite3.dll"]
        if foreign_runtime:
            payload["app/runtimes/linux-x64/native/libe_sqlite3.so"] = b"foreign"
        if nested_managed:
            payload["app/unexpected/helper.dll"] = b"nested"
        sources = VERIFIER.expected_source_inputs(self.source)
        if omit_source:
            sources = sources[1:]
        schemas = [{
            "path": "content/schemas/example.schema.json",
            "sha256": digest(self.schema),
            "bytes": len(self.schema),
        }]
        if omit_schema:
            schemas = []
        closure = VERIFIER.expected_project_closure(self.source)
        if omit_lock:
            closure = []
        inventory = [{"path": name, "bytes": len(value), "sha256": digest(value)} for name, value in sorted(payload.items())]
        mcp_contract = {
            "verified": True,
            "storageKind": "sqlite",
            "network": False,
            "liveServices": False,
            "instructionsUtf8Bytes": 400,
            "instructionsSha256": "1" * 64,
            "toolDefinitionsSha256": "2" * 64,
            "guideResourceSha256": digest(self.guide),
            "guideVersion": "1.0.0",
            "authorityFallbackVerified": True,
            "privatePendingProposalVerified": True,
        }
        manifest = {
            "schemaVersion": 1,
            "releaseVersion": self.RELEASE,
            "packageVersion": self.PACKAGE,
            "runtimeIdentifier": "win-x64",
            "transport": "stdio",
            "storageKind": "sqlite",
            "network": False,
            "liveServices": False,
            "serviceRegistration": False,
            "entryAssembly": "app/PegasusCortex.Mcp.Local.dll",
            "toolSurface": sorted(VERIFIER.EXPECTED_TOOLS),
            "mcpContract": mcp_contract,
            "publishContract": {
                "configuration": "Release", "frameworkDependent": True, "portable": True,
                "useAppHost": False, "singleFile": False, "readyToRun": False,
                "nativeAot": False, "trimmed": False, "debugSymbols": False,
            },
            "sdk": {
                "version": "99.8.7", "actualVersion": "99.8.7",
                "rollForward": "disable", "allowPrerelease": False,
                "globalJsonSha256": VERIFIER.sha256_file(self.source / "global.json"),
            },
            "dependencyLocks": closure,
            "guide": {
                "path": "content/continuity-guide.md", "version": "1.0.0",
                "sha256": digest(self.guide), "resourceUri": "cortex://default-library/guide",
            },
            "schemas": schemas,
            "sourceSetSha256": VERIFIER.source_set_sha256(sources),
            "sourceInputs": sources,
            "payload": inventory,
        }
        manifest_bytes = (json.dumps(manifest, separators=(",", ":")) + "\n").encode("utf-8")
        checksums = [f"{digest(manifest_bytes)}  package-manifest.json"]
        checksums.extend(f"{item['sha256']}  {item['path']}" for item in inventory)
        with zipfile.ZipFile(self.package, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("package-manifest.json", manifest_bytes)
            archive.writestr("checksums.sha256", "\n".join(checksums) + "\n")
            for name, value in payload.items():
                archive.writestr(name, b"tampered" if tamper_payload and name.endswith(".dll") else value)
            if duplicate:
                with warnings.catch_warnings():
                    warnings.simplefilter("ignore", UserWarning)
                    archive.writestr("app/PegasusCortex.Mcp.Local.dll", b"duplicate")
        result = {
            "schemaVersion": 1,
            "releaseVersion": self.RELEASE,
            "runtimeIdentifier": "win-x64",
            "sourceSetSha256": manifest["sourceSetSha256"],
            "manifestSha256": digest(manifest_bytes),
            "zipName": self.package.name,
            "zipBytes": self.package.stat().st_size + (1 if tamper_build_result else 0),
            "zipSha256": VERIFIER.sha256_file(self.package),
            "deliveryFileCount": len(payload) + 2,
            "smokeRequired": True,
            "verified": False,
            "offlineStdioSmoke": False,
        }
        self.build_result.write_text(json.dumps(result) + "\n", encoding="utf-8")


class CortexMcpX64RouteRetirementTests(unittest.TestCase):
    def test_builder_has_clean_ast_and_fails_before_output(self) -> None:
        source = BUILDER_PATH.read_text(encoding="utf-8")
        self.assertLess(
            source.index("PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED"),
            source.index("Set-StrictMode"),
        )
        builder_literal = str(BUILDER_PATH).replace("'", "''")
        parser = (
            "$tokens = $null; $errors = $null; "
            "[System.Management.Automation.Language.Parser]::ParseFile("
            f"'{builder_literal}', [ref]$tokens, [ref]$errors) | Out-Null; "
            "if ($errors.Count -ne 0) { $errors | ForEach-Object { "
            "[Console]::Error.WriteLine($_.Message) }; exit 1 }"
        )
        parsed = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", parser],
            cwd=ROOT, capture_output=True, text=True, timeout=30, check=False,
        )
        self.assertEqual(0, parsed.returncode, parsed.stderr)

        with tempfile.TemporaryDirectory() as temporary:
            output = pathlib.Path(temporary) / "must-not-exist"
            completed = subprocess.run(
                [
                    "powershell.exe", "-NoProfile", "-NonInteractive",
                    "-ExecutionPolicy", "Bypass", "-File", str(BUILDER_PATH),
                    "-ProjectRoot", str(ROOT), "-OutputRoot", str(output),
                ],
                cwd=ROOT, capture_output=True, text=True, timeout=30, check=False,
            )
            diagnostic = completed.stdout + completed.stderr
            self.assertNotEqual(0, completed.returncode)
            self.assertIn(VERIFIER.RETIREMENT_CODE, diagnostic)
            self.assertIn("New-PegasusCortexV2PackageManifest.ps1", diagnostic)
            self.assertIn("Install-PegasusCortexV2X64.ps1", diagnostic)
            self.assertFalse(output.exists())

    def test_validator_cli_and_api_fail_closed_without_reading_a_package(self) -> None:
        completed = subprocess.run(
            [sys.executable, str(TOOL_PATH), "does-not-exist.zip"],
            cwd=ROOT, capture_output=True, text=True, timeout=30, check=False,
        )
        diagnostic = completed.stdout + completed.stderr
        self.assertEqual(2, completed.returncode)
        self.assertIn(VERIFIER.RETIREMENT_CODE, diagnostic)
        self.assertIn("New-PegasusCortexV2PackageManifest.ps1", diagnostic)
        self.assertIn("Install-PegasusCortexV2X64.ps1", diagnostic)
        with self.assertRaisesRegex(RuntimeError, VERIFIER.RETIREMENT_CODE):
            VERIFIER.verify_package(
                pathlib.Path("does-not-exist.zip"),
                pathlib.Path("does-not-exist"),
                run_smoke=False,
            )


class HistoricalCortexMcpX64PackageAssertions:
    """Preserved, non-discovered assertions for the superseded package format."""

    @staticmethod
    def copy_inspection_root(destination: pathlib.Path) -> None:
        for relative in VERIFIER.SOURCE_FILES:
            source = ROOT.joinpath(*pathlib.PurePosixPath(relative).parts)
            target = destination.joinpath(*pathlib.PurePosixPath(relative).parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
        for relative in VERIFIER.SOURCE_TREES:
            shutil.copytree(
                ROOT / relative,
                destination / relative,
                ignore=shutil.ignore_patterns("bin", "obj", "__pycache__", "*.pyc"),
            )

    def test_builder_declares_immutable_portable_locked_contract(self) -> None:
        source = BUILDER_PATH.read_text(encoding="utf-8")
        for marker in (
            "The immutable x64 package output already exists.", "--locked-mode",
            "--self-contained false", "-p:UseAppHost=false", "-p:PublishReadyToRun=false",
            "-p:PublishAot=false", "-p:PublishTrimmed=false", "-p:DebugSymbols=false",
            "Get-BoundSourceInputs", "Bound source inputs changed during restore or publish.",
            "New-DeterministicZip", "build-result.json", "--capture-contract", "InspectSourceSet",
            "Copy-RidFilteredX64Publish", "Get-DeclaredRuntimeAssets", "$rawPublish", "RID-neutral",
            "Assert-UnlinkedDirectoryChain", "Assert-SafeOutputParent", "Remove-OwnedX64Stage",
            "Copy-X64PackageContent", "Assert-InventoryUnchanged",
        ):
            self.assertIn(marker, source)
        self.assertNotIn("--runtime win-x64", source)
        self.assertNotIn("-r win-x64", source)
        guide_assignment = "$guideVersion = [string]$guideVersionMatch.Groups[1].Value"
        guide_comparison = "[string]$mcpContract.guideVersion -cne $guideVersion"
        self.assertIn(guide_assignment, source)
        self.assertIn(guide_comparison, source)
        self.assertLess(source.index(guide_assignment), source.index(guide_comparison))
        self.assertNotIn(".cortex", source)
        self.assertNotIn("1.2026.", source)

    def test_windows_powershell_join_path_arity_is_ps51_compatible(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            harness = pathlib.Path(temporary) / "join-path-ast.ps1"
            harness.write_text(
                "$tokens = $null; $errors = $null\n"
                "$ast = [System.Management.Automation.Language.Parser]::ParseFile(\n"
                "    $args[0], [ref]$tokens, [ref]$errors)\n"
                "if ($errors.Count -ne 0) { throw ($errors | ForEach-Object Message) }\n"
                "$commands = @($ast.FindAll({ param($node)\n"
                "    $node -is [System.Management.Automation.Language.CommandAst] -and\n"
                "    $node.GetCommandName() -eq 'Join-Path'\n"
                "}, $true))\n"
                "$bad = @($commands | Where-Object { $_.CommandElements.Count -gt 3 })\n"
                "if ($bad.Count -ne 0) { throw ('PS5.1-incompatible Join-Path: ' + $bad[0].Extent.Text) }\n"
                "$commands.Count\n",
                encoding="utf-8",
            )
            completed = subprocess.run(
                ["powershell.exe", "-NoProfile", "-File", str(harness), str(BUILDER_PATH)],
                cwd=ROOT,
                capture_output=True,
                text=True,
                timeout=30,
                check=True,
            )
            self.assertGreater(int(completed.stdout.strip()), 0)

    def test_windows_powershell_schema_manifest_uses_python_ordinal_order(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            source_root = root / "source"
            schema_root = source_root / "schemas"
            destination = root / "content"
            schema_root.mkdir(parents=True)
            destination.mkdir()
            (source_root / "guide.md").write_bytes(b"guide\n")
            schema_payloads = {
                "agent-work-packet.v1.schema.json": b'{"title":"packet"}\n',
                "agent-work-packet-acceptance-receipt.v1.schema.json": b'{"title":"acceptance"}\n',
                "agent-work-packet-handoff-receipt.v1.schema.json": b'{"title":"handoff"}\n',
            }
            for name, payload in schema_payloads.items():
                (schema_root / name).write_bytes(payload)

            source = BUILDER_PATH.read_text(encoding="utf-8")
            prefix = source[:source.index("$root = Resolve-SafeProjectRoot")]
            harness = root / "content-copy-harness.ps1"
            harness.write_text(
                prefix
                + "\n$schemas = @(Get-X64PackageSchemas -SourceRoot $ProjectRoot)\n"
                + "$null = Copy-X64PackageContent -SourceRoot $ProjectRoot "
                + "-GuidePath (Join-Path $ProjectRoot 'guide.md') -Schemas $schemas -DestinationRoot $OutputRoot\n"
                + "[ordered]@{ schemas = @($schemas | ForEach-Object { [ordered]@{ "
                + "path = \"content/schemas/$($_.Name)\"; sha256 = Get-Sha256 -Path $_.FullName; "
                + "bytes = $_.Length } }); inventory = @(Get-Inventory -Root $OutputRoot) } "
                + "| ConvertTo-Json -Depth 5 -Compress\n",
                encoding="utf-8",
            )
            completed = subprocess.run(
                [
                    "powershell.exe", "-NoProfile", "-File", str(harness),
                    "-ProjectRoot", str(source_root), "-OutputRoot", str(destination),
                ],
                cwd=root,
                capture_output=True,
                text=True,
                timeout=30,
                check=True,
            )
            observed = json.loads(completed.stdout)
            verifier_names = sorted(
                path.name
                for path in VERIFIER._safe_tree_files(source_root, "schemas")
                if path.name.endswith(".schema.json") and path.parent == schema_root
            )
            self.assertEqual(
                [
                    {
                        "path": f"content/schemas/{name}",
                        "sha256": digest(schema_payloads[name]),
                        "bytes": len(schema_payloads[name]),
                    }
                    for name in verifier_names
                ],
                observed["schemas"],
            )
            self.assertEqual(
                ["continuity-guide.md", *[f"schemas/{name}" for name in verifier_names]],
                [item["path"] for item in observed["inventory"]],
            )

    def test_windows_powershell_owned_stage_cleanup_is_exact_and_recursive_safe(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "product"
            parent = root / "artifacts"
            stage = parent / (".x64-package-stage-" + "a" * 32)
            (stage / "nested").mkdir(parents=True)
            (stage / "nested/evidence.txt").write_text("diagnostic\n", encoding="utf-8")

            source = BUILDER_PATH.read_text(encoding="utf-8")
            prefix = source[:source.index("$root = Resolve-SafeProjectRoot")]
            harness = pathlib.Path(temporary) / "cleanup-harness.ps1"
            harness.write_text(
                prefix
                + "\n$stage = Join-Path $OutputRoot ('.x64-package-stage-' + ('a' * 32))\n"
                + "Remove-OwnedX64Stage -Stage $stage -Parent $OutputRoot -Root $ProjectRoot\n"
                + "Test-Path -LiteralPath $stage\n",
                encoding="utf-8",
            )
            completed = subprocess.run(
                [
                    "powershell.exe", "-NoProfile", "-File", str(harness),
                    "-ProjectRoot", str(root), "-OutputRoot", str(parent),
                ],
                cwd=pathlib.Path(temporary),
                capture_output=True,
                text=True,
                timeout=30,
                check=True,
            )
            self.assertEqual("False", completed.stdout.strip())

            unowned = parent / (".x64-package-stage-" + "b" * 32 + "-extra")
            unowned.mkdir()
            rejection = pathlib.Path(temporary) / "cleanup-rejection.ps1"
            rejection.write_text(
                prefix
                + "\n$stage = Join-Path $OutputRoot ('.x64-package-stage-' + ('b' * 32) + '-extra')\n"
                + "Remove-OwnedX64Stage -Stage $stage -Parent $OutputRoot -Root $ProjectRoot\n",
                encoding="utf-8",
            )
            rejected = subprocess.run(
                [
                    "powershell.exe", "-NoProfile", "-File", str(rejection),
                    "-ProjectRoot", str(root), "-OutputRoot", str(parent),
                ],
                cwd=pathlib.Path(temporary),
                capture_output=True,
                text=True,
                timeout=30,
            )
            self.assertNotEqual(0, rejected.returncode)
            self.assertIn("Refusing to clean an unowned x64 package stage", rejected.stderr)
            self.assertTrue(unowned.is_dir())

    def test_windows_powershell_output_root_revalidates_created_parent(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary) / "product"
            root.mkdir()
            output = root / "artifacts/validation/release"

            source = BUILDER_PATH.read_text(encoding="utf-8")
            prefix = source[:source.index("$root = Resolve-SafeProjectRoot")]
            harness = pathlib.Path(temporary) / "output-root-harness.ps1"
            harness.write_text(
                prefix
                + "\n$resolved = Resolve-SafeOutputRoot -Root $ProjectRoot -RequestedOutput $OutputRoot\n"
                + "[ordered]@{ resolved = $resolved; parentExists = Test-Path -LiteralPath (Split-Path -Parent $resolved); "
                + "outputExists = Test-Path -LiteralPath $resolved } | ConvertTo-Json -Compress\n",
                encoding="utf-8",
            )
            completed = subprocess.run(
                [
                    "powershell.exe", "-NoProfile", "-File", str(harness),
                    "-ProjectRoot", str(root), "-OutputRoot", str(output),
                ],
                cwd=pathlib.Path(temporary),
                capture_output=True,
                text=True,
                timeout=30,
                check=True,
            )
            observed = json.loads(completed.stdout)
            self.assertEqual(output, pathlib.Path(observed["resolved"]))
            self.assertTrue(observed["parentExists"])
            self.assertFalse(observed["outputExists"])

            unsafe_output = root / "artifacts/../escape"
            rejected = subprocess.run(
                [
                    "powershell.exe", "-NoProfile", "-File", str(harness),
                    "-ProjectRoot", str(root), "-OutputRoot", str(unsafe_output),
                ],
                cwd=pathlib.Path(temporary),
                capture_output=True,
                text=True,
                timeout=30,
            )
            self.assertNotEqual(0, rejected.returncode)
            self.assertIn("unsafe or nonportable path segment", rejected.stderr)

    def test_windows_powershell_tree_evidence_rejects_post_capture_drift(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            tree = root / "tree"
            tree.mkdir()
            (tree / "payload.dll").write_bytes(b"before")

            source = BUILDER_PATH.read_text(encoding="utf-8")
            prefix = source[:source.index("$root = Resolve-SafeProjectRoot")]
            harness = root / "evidence-harness.ps1"
            harness.write_text(
                prefix
                + "\n$before = @(Get-Inventory -Root $ProjectRoot)\n"
                + "[IO.File]::WriteAllText((Join-Path $ProjectRoot 'payload.dll'), 'after')\n"
                + "$after = @(Get-Inventory -Root $ProjectRoot)\n"
                + "Assert-InventoryUnchanged -Before $before -After $after -Operation 'test probe'\n",
                encoding="utf-8",
            )
            rejected = subprocess.run(
                ["powershell.exe", "-NoProfile", "-File", str(harness), "-ProjectRoot", str(tree)],
                cwd=root,
                capture_output=True,
                text=True,
                timeout=30,
            )
            self.assertNotEqual(0, rejected.returncode)
            self.assertIn("changed during test probe", rejected.stderr)

    def test_builder_and_verifier_use_exact_same_source_ledger(self) -> None:
        completed = subprocess.run(
            ["powershell.exe", "-NoProfile", "-File", str(BUILDER_PATH), "-InspectSourceSet"],
            cwd=ROOT, capture_output=True, text=True, timeout=30, check=True,
        )
        observed = json.loads(completed.stdout)
        expected = VERIFIER.expected_source_inputs(ROOT)
        self.assertEqual(expected, observed["sourceInputs"])
        self.assertEqual(VERIFIER.source_set_sha256(expected), observed["sourceSetSha256"])

    def test_inspect_source_set_accepts_extracted_root_and_rejects_false_root(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            extracted = pathlib.Path(temporary) / "extracted"
            self.copy_inspection_root(extracted)
            copied_builder = extracted / "tools/New-CortexMcpX64Package.ps1"
            expected = VERIFIER.expected_source_inputs(extracted)

            # Script-location discovery and an explicit safe -ProjectRoot must
            # reconstruct the same path-independent ledger from extracted bytes.
            for arguments in (
                ["powershell.exe", "-NoProfile", "-File", str(copied_builder), "-InspectSourceSet"],
                [
                    "powershell.exe", "-NoProfile", "-File", str(BUILDER_PATH),
                    "-ProjectRoot", str(extracted), "-InspectSourceSet",
                ],
            ):
                completed = subprocess.run(
                    arguments,
                    cwd=extracted.parent,
                    capture_output=True,
                    text=True,
                    timeout=30,
                    check=True,
                )
                observed = json.loads(completed.stdout)
                self.assertEqual(expected, observed["sourceInputs"])
                self.assertEqual(VERIFIER.source_set_sha256(expected), observed["sourceSetSha256"])

            # Inspection must traverse the real package dependency closure, not
            # merely hash the broad source ledger.
            closure_lock = extracted / "src/PegasusCortex.LocalTransport/packages.lock.json"
            closure_lock.unlink()
            closure_rejected = subprocess.run(
                ["powershell.exe", "-NoProfile", "-File", str(copied_builder), "-InspectSourceSet"],
                cwd=extracted.parent,
                capture_output=True,
                text=True,
                timeout=30,
            )
            self.assertNotEqual(0, closure_rejected.returncode)
            self.assertIn("Source leaf escaped the product root or is missing", closure_rejected.stderr)
            shutil.copy2(ROOT / "src/PegasusCortex.LocalTransport/packages.lock.json", closure_lock)

            (extracted / "PegasusCortex.slnx").write_text(
                '<Solution><Project Path="src/Unrelated/Unrelated.csproj" /></Solution>\n',
                encoding="utf-8",
            )
            rejected = subprocess.run(
                [
                    "powershell.exe", "-NoProfile", "-File", str(BUILDER_PATH),
                    "-ProjectRoot", str(extracted), "-InspectSourceSet",
                ],
                cwd=extracted.parent,
                capture_output=True,
                text=True,
                timeout=30,
            )
            self.assertNotEqual(0, rejected.returncode)
            self.assertIn(
                "PegasusCortex.slnx does not identify the standalone MCP entry project.",
                rejected.stderr,
            )

    def test_powershell_filter_copies_only_declared_win_x64_runtime_assets(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            raw = root / "raw"
            destination = root / "filtered"
            (raw / "runtimes/win-x64/native").mkdir(parents=True)
            (raw / "runtimes/linux-x64/native").mkdir(parents=True)
            destination.mkdir()
            (raw / "PegasusCortex.Mcp.Local.dll").write_bytes(b"managed")
            (raw / "PegasusCortex.Mcp.Local.runtimeconfig.json").write_text("{}\n", encoding="utf-8")
            (raw / "runtimes/win-x64/native/e_sqlite3.dll").write_bytes(b"win")
            (raw / "runtimes/linux-x64/native/libe_sqlite3.so").write_bytes(b"linux")
            deps = {
                "targets": {
                    ".NETCoreApp,Version=v10.0": {
                        "SQLitePCLRaw.lib.e_sqlite3/2.1.11": {
                            "runtimeTargets": {
                                "runtimes/linux-x64/native/libe_sqlite3.so": {
                                    "rid": "linux-x64", "assetType": "native",
                                },
                                "runtimes/win-x64/native/e_sqlite3.dll": {
                                    "rid": "win-x64", "assetType": "native",
                                },
                            }
                        }
                    }
                }
            }
            (raw / "PegasusCortex.Mcp.Local.deps.json").write_text(
                json.dumps(deps, separators=(",", ":")) + "\n",
                encoding="utf-8",
            )

            source = BUILDER_PATH.read_text(encoding="utf-8")
            prefix = source[:source.index("$root = Resolve-SafeProjectRoot")]
            harness = root / "filter-harness.ps1"
            harness.write_text(
                prefix
                + "\nCopy-RidFilteredX64Publish -RawRoot $ProjectRoot -DestinationRoot $OutputRoot -RuntimeIdentifier 'win-x64'\n"
                + "$prefixPath = \"$([IO.Path]::GetFullPath($OutputRoot).TrimEnd('\\'))\\\"\n"
                + "@((Get-ChildItem -LiteralPath $OutputRoot -Recurse -File -Force | Sort-Object FullName | "
                + "ForEach-Object { $_.FullName.Substring($prefixPath.Length).Replace('\\', '/') })) "
                + "| ConvertTo-Json -Compress\n",
                encoding="utf-8",
            )
            completed = subprocess.run(
                [
                    "powershell.exe", "-NoProfile", "-File", str(harness),
                    "-ProjectRoot", str(raw), "-OutputRoot", str(destination),
                ],
                cwd=root,
                capture_output=True,
                text=True,
                timeout=30,
                check=True,
            )
            self.assertEqual(
                [
                    "PegasusCortex.Mcp.Local.deps.json",
                    "PegasusCortex.Mcp.Local.dll",
                    "PegasusCortex.Mcp.Local.runtimeconfig.json",
                    "runtimes/win-x64/native/e_sqlite3.dll",
                ],
                json.loads(completed.stdout),
            )

    def test_static_verification_accepts_exact_fixture_and_build_result(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            fixture = X64PackageFixture(pathlib.Path(temporary)); fixture.write_package()
            manifest = VERIFIER.verify_package(fixture.package, fixture.source, run_smoke=False)
            self.assertEqual("win-x64", manifest["runtimeIdentifier"])
            self.assertTrue(manifest["_verification"]["buildResultSha256"])

    def test_payload_tamper_and_build_result_tamper_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            fixture = X64PackageFixture(pathlib.Path(temporary)); fixture.write_package(tamper_payload=True)
            with self.assertRaisesRegex(RuntimeError, "Payload bytes"):
                VERIFIER.verify_package(fixture.package, fixture.source, run_smoke=False)
        with tempfile.TemporaryDirectory() as temporary:
            fixture = X64PackageFixture(pathlib.Path(temporary)); fixture.write_package(tamper_build_result=True)
            with self.assertRaisesRegex(RuntimeError, "build-result"):
                VERIFIER.verify_package(fixture.package, fixture.source, run_smoke=False)

    def test_independent_source_schema_and_lock_reconstruction_rejects_omissions(self) -> None:
        for keyword, message in (("omit_source", "sourceInputs"), ("omit_schema", "schema set"), ("omit_lock", "dependencyLocks")):
            with self.subTest(keyword=keyword), tempfile.TemporaryDirectory() as temporary:
                fixture = X64PackageFixture(pathlib.Path(temporary)); fixture.write_package(**{keyword: True})
                with self.assertRaisesRegex(RuntimeError, message):
                    VERIFIER.verify_package(fixture.package, fixture.source, run_smoke=False)

    def test_runtime_asset_filter_requires_exact_win_x64_dependency_subset(self) -> None:
        for keyword in ("omit_runtime", "foreign_runtime", "nested_managed"):
            with self.subTest(keyword=keyword), tempfile.TemporaryDirectory() as temporary:
                fixture = X64PackageFixture(pathlib.Path(temporary)); fixture.write_package(**{keyword: True})
                with self.assertRaisesRegex(RuntimeError, "runtime assets|unexpected app directory"):
                    VERIFIER.verify_package(fixture.package, fixture.source, run_smoke=False)

    def test_source_reparse_and_project_reference_escape_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            fixture = X64PackageFixture(pathlib.Path(temporary))
            with mock.patch.object(VERIFIER, "_is_reparse", return_value=True):
                with self.assertRaisesRegex(RuntimeError, "Source root"):
                    VERIFIER.expected_source_inputs(fixture.source)
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            fixture = X64PackageFixture(root)
            outside = root / "outside.csproj"
            outside.write_text("<Project />\n", encoding="utf-8")
            fixture.project.write_text(
                '<Project><ItemGroup><ProjectReference Include="../../../outside.csproj" /></ItemGroup></Project>\n',
                encoding="utf-8",
            )
            with self.assertRaisesRegex(RuntimeError, "escaped source root"):
                VERIFIER.expected_project_closure(fixture.source)

    def test_zip_rejects_encryption_non_regular_and_reparse_metadata(self) -> None:
        destination = pathlib.Path(tempfile.gettempdir()) / "x64-zip-test"
        cases = (
            zip_entry("app/a.dll", encrypted=True),
            zip_entry("app/a.dll", create_system=3, external_attr=(stat.S_IFLNK | 0o777) << 16),
            zip_entry("app/a.dll", external_attr=VERIFIER.FILE_ATTRIBUTE_REPARSE_POINT),
            zip_entry("app/a.dll", create_system=7),
            zip_entry("app/a.dll", external_attr=VERIFIER.FILE_ATTRIBUTE_DIRECTORY),
            zip_entry("app/a.dll", external_attr=VERIFIER.FILE_ATTRIBUTE_VOLUME_LABEL),
        )
        for entry in cases:
            with self.subTest(entry=entry.external_attr, encrypted=entry.flag_bits):
                with self.assertRaises(RuntimeError):
                    VERIFIER.safe_zip_entries(FakeArchive(entry), destination)

    def test_zip_rejects_alias_reserved_prefix_and_overlong_destinations(self) -> None:
        destination = pathlib.Path(tempfile.gettempdir()) / "x64-zip-test"
        archives = (
            FakeArchive(zip_entry("app/A.dll"), zip_entry("app/a.DLL")),
            FakeArchive(zip_entry("app//a.dll")),
            FakeArchive(zip_entry("app/./a.dll")),
            FakeArchive(zip_entry("./app/a.dll")),
            FakeArchive(zip_entry("app/name. ")),
            FakeArchive(zip_entry("app/CON.txt")),
            FakeArchive(zip_entry("app"), zip_entry("app/a.dll")),
            FakeArchive(zip_entry("app"), zip_entry("app/", create_system=3, external_attr=(stat.S_IFDIR | 0o700) << 16)),
            FakeArchive(zip_entry("app"), zip_entry("app/sub/", create_system=3, external_attr=(stat.S_IFDIR | 0o700) << 16)),
        )
        for archive in archives:
            with self.subTest(entries=[entry.filename for entry in archive.infolist()]):
                with self.assertRaises(RuntimeError):
                    VERIFIER.safe_zip_entries(archive, destination)
        long_destination = pathlib.Path("C:/") / ("d" * 200)
        with self.assertRaisesRegex(RuntimeError, "240 UTF-16"):
            VERIFIER.safe_zip_entries(FakeArchive(zip_entry("app/" + "x" * 50)), long_destination)

    def test_duplicate_zip_entry_and_source_drift_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            fixture = X64PackageFixture(pathlib.Path(temporary)); fixture.write_package(duplicate=True)
            with self.assertRaisesRegex(RuntimeError, "collision"):
                VERIFIER.verify_package(fixture.package, fixture.source, run_smoke=False)
        with tempfile.TemporaryDirectory() as temporary:
            fixture = X64PackageFixture(pathlib.Path(temporary)); fixture.write_package()
            (fixture.source / "VERSION.json").write_text("{}\n", encoding="utf-8")
            with self.assertRaises(RuntimeError):
                VERIFIER.verify_package(fixture.package, fixture.source, run_smoke=False)

    def test_tool_contract_checks_complete_schema_and_annotations(self) -> None:
        tools = []
        for name in sorted(VERIFIER.EXPECTED_TOOLS):
            properties, required = VERIFIER.TOOL_ARGUMENTS[name]
            tools.append({
                "name": name, "title": name, "description": "A bounded test description for the exact contract.",
                "inputSchema": {"type": "object", "properties": {key: {} for key in properties}, "required": sorted(required), "additionalProperties": False},
                "annotations": {"title": name, "readOnlyHint": name not in VERIFIER.WRITABLE_TOOLS, "destructiveHint": False, "idempotentHint": True, "openWorldHint": False},
            })
        self.assertRegex(VERIFIER.validate_tool_definitions(tools), r"^[0-9a-f]{64}$")
        tools[0]["annotations"]["openWorldHint"] = True
        with self.assertRaisesRegex(RuntimeError, "annotations"):
            VERIFIER.validate_tool_definitions(tools)


if __name__ == "__main__":
    unittest.main()
