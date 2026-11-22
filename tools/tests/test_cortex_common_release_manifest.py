from __future__ import annotations

import copy
import contextlib
import io
import json
import os
import pathlib
import sys
import tempfile
import unittest


TOOLS = pathlib.Path(__file__).resolve().parents[1]
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))

import CortexCommonReleaseManifest as common  # noqa: E402
import CortexVerificationManifest as verification  # noqa: E402


RELEASE = "1.2026.08.07.12.00.A"
CAPTURED = "2026-08-07T19:00:00.000000Z"


class CommonReleaseManifestTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temporary.name)
        for name in ("artifacts", "bundles", "extract", "receipts", "manifests", "src"):
            (self.root / name).mkdir()
        self._write_json(
            "VERSION.json",
            {
                "assemblyVersion": "1.0.0.0",
                "fileVersion": "1.2026.807.1200",
                "nuGetVersion": "1.20260807.1200-a",
                "releaseVersion": RELEASE,
                "schemaVersion": 1,
            },
        )
        (self.root / "Directory.Version.props").write_text(
            """<Project><PropertyGroup>
  <PegasusReleaseVersion>1.2026.08.07.12.00.A</PegasusReleaseVersion>
  <PegasusNuGetVersion>1.20260807.1200-a</PegasusNuGetVersion>
  <PegasusAssemblyVersion>1.0.0.0</PegasusAssemblyVersion>
  <PegasusFileVersion>1.2026.807.1200</PegasusFileVersion>
</PropertyGroup></Project>\n""",
            encoding="utf-8",
        )
        (self.root / "src" / "product.cs").write_text("sealed source\n", encoding="utf-8")
        self.bundle_path = self.root / "bundles" / "source.zip"
        self.bundle = verification.create_source_bundle(
            self.root,
            self.bundle_path,
            source_paths=("Directory.Version.props", "VERSION.json", "src"),
            captured_at=CAPTURED,
        )
        verification.extract_source_bundle(self.root, "bundles/source.zip", self.root / "extract" / "source")
        self.receipt_path = self._write_receipt()
        self.inputs = self._write_package_evidence()

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def _write_json(self, relative: str, value: object) -> pathlib.Path:
        path = self.root / pathlib.PurePosixPath(relative)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        return path

    def _write_receipt(self, *, accepted: bool = True) -> pathlib.Path:
        command = {
            "argv": ["bounded-test"],
            "checks": {"failed": 0 if accepted else 1, "passed": 2, "skipped": 1},
            "exitCode": 0 if accepted else 1,
            "id": "exact-source-matrix",
            "observedMetrics": {},
            "parser": "exit-only",
            "parserAccepted": True,
            "skipDetails": [{"name": "native branch", "reason": "current platform is Windows"}],
            "terminationReason": "completed",
            "timeoutSeconds": 30,
            "workingDirectory": ".",
        }
        payload = {
            "accepted": accepted,
            "afterSourceInventorySha256": self.bundle["source"]["sha256"],
            "beforeSourceInventorySha256": self.bundle["source"]["sha256"],
            "capturedAtUtc": CAPTURED,
            "commands": [command],
            "contractId": verification.EXECUTION_RECEIPT_CONTRACT_ID,
            "extractedRoot": "extract/source",
            "sourceInventorySha256": self.bundle["source"]["sha256"],
            "testedSourceBundle": self.bundle["bundle"],
        }
        payload["seal"] = {
            "algorithm": "sha256",
            "canonicalization": "sorted-key-compact-json-utf8",
            "payloadSha256": verification.sha256_bytes(verification.canonical_json_bytes(payload)),
        }
        path = self.root / "receipts" / ("accepted.json" if accepted else "rejected.json")
        verification.write_manifest(path, payload)
        return path

    def _write_package_evidence(self) -> dict[str, str]:
        source_inputs = copy.deepcopy(self.bundle["source"]["files"])
        ledger = "".join(
            f"{item['sha256']}  {item['bytes']}  {item['path']}\n" for item in source_inputs
        )
        source_set = verification.sha256_bytes(ledger.encode("utf-8"))

        x64_archive = self.root / "artifacts" / "package-win-x64.zip"
        x64_archive.write_bytes(b"deterministic-x64-package")
        x64_archive_sha = verification.sha256_file(x64_archive)
        x64_manifest = self._write_json(
            "artifacts/x64-package-manifest.json",
            {
                "mcpContract": {"verified": True},
                "releaseVersion": RELEASE,
                "runtimeIdentifier": "win-x64",
                "schemaVersion": 1,
                "sdk": {"actualVersion": common.SDK_VERSION, "version": common.SDK_VERSION},
                "sourceInputs": source_inputs,
                "sourceSetSha256": source_set,
            },
        )
        x64_manifest_sha = verification.sha256_file(x64_manifest)
        x64_build = self._write_json(
            "artifacts/x64-build-result.json",
            {
                "deliveryFileCount": 4,
                "manifestSha256": x64_manifest_sha,
                "offlineStdioSmoke": False,
                "releaseVersion": RELEASE,
                "runtimeIdentifier": "win-x64",
                "schemaVersion": 1,
                "smokeRequired": True,
                "sourceSetSha256": source_set,
                "zipBytes": x64_archive.stat().st_size,
                "zipName": x64_archive.name,
                "zipSha256": x64_archive_sha,
                "verified": False,
            },
        )
        self._write_json(
            "artifacts/x64-verifier.json",
            {
                "buildResultSha256": verification.sha256_file(x64_build),
                "manifestSha256": x64_manifest_sha,
                "mcpContract": {"verified": True},
                "offlineStdioSmoke": True,
                "packageBytes": x64_archive.stat().st_size,
                "packageName": x64_archive.name,
                "packageSha256": x64_archive_sha,
                "releaseVersion": RELEASE,
                "runtimeIdentifier": "win-x64",
                "schemaVersion": 1,
                "sourceSetSha256": source_set,
                "verified": True,
            },
        )

        arm_archive = self.root / "artifacts" / "package-linux-arm64.tar.gz"
        arm_archive.write_bytes(b"deterministic-arm-package")
        arm_archive_sha = verification.sha256_file(arm_archive)
        runtime_archive = self.root / "artifacts" / "dotnet-runtime-linux-arm64.tar.gz"
        runtime_archive.write_bytes(b"accepted-arm-runtime")
        runtime_sha512 = common._sha512_file(runtime_archive)
        arm_manifest = self._write_json(
            "artifacts/arm-package-manifest.json",
            {
                "releaseVersion": RELEASE,
                "runtimeIdentifier": "linux-arm64",
                "runtimeVersion": "10.0.10",
                "runtime": {
                    "archiveName": runtime_archive.name,
                    "sha256": verification.sha256_file(runtime_archive),
                    "sha512": runtime_sha512,
                },
                "schemaVersion": 3,
                "sdk": {"actualVersion": common.SDK_VERSION, "version": common.SDK_VERSION},
                "sourceInputs": source_inputs,
            },
        )
        arm_manifest_sha = verification.sha256_file(arm_manifest)
        self._write_json(
            "artifacts/arm-build-result.json",
            {
                "packageArchiveBytes": arm_archive.stat().st_size,
                "packageArchiveSha256": arm_archive_sha,
                "packageManifestSha256": arm_manifest_sha,
                "releaseVersion": RELEASE,
                "runtimeIdentifier": "linux-arm64",
                "schemaVersion": 1,
                "status": "built",
            },
        )
        self._write_json(
            "artifacts/arm-verifier.json",
            {
                "aarch64ElfVerified": True,
                "packageArchiveSha256": arm_archive_sha,
                "packageManifestSha256": arm_manifest_sha,
                "portableIntegratedFailClosed": True,
                "releaseVersion": RELEASE,
                "runtimeIdentifier": "linux-arm64",
                "safeExtractionVerified": True,
                "status": "pass",
            },
        )
        return {
            "release_version": RELEASE,
            "tested_source_bundle": "bundles/source.zip",
            "execution_receipt": "receipts/accepted.json",
            "x64_build_result": "artifacts/x64-build-result.json",
            "x64_manifest": "artifacts/x64-package-manifest.json",
            "x64_archive": "artifacts/package-win-x64.zip",
            "x64_verifier_result": "artifacts/x64-verifier.json",
            "arm_build_result": "artifacts/arm-build-result.json",
            "arm_manifest": "artifacts/arm-package-manifest.json",
            "arm_archive": "artifacts/package-linux-arm64.tar.gz",
            "arm_verifier_result": "artifacts/arm-verifier.json",
            "arm_runtime_version": "10.0.10",
            "arm_runtime_archive": "artifacts/dotnet-runtime-linux-arm64.tar.gz",
        }

    def _build(self, **overrides: str) -> dict[str, object]:
        values = dict(self.inputs)
        values.update(overrides)
        return common.build_manifest(self.root, **values)

    def test_create_is_deterministic_and_preserves_named_skips(self) -> None:
        first = self._build()
        second = self._build()
        self.assertEqual(first, second)
        self.assertTrue(first["accepted"])
        self.assertEqual(first["execution"]["checks"], {"failed": 0, "passed": 2, "skipped": 1})
        self.assertEqual(first["execution"]["namedSkips"][0]["name"], "native branch")
        self.assertEqual(first["toolchain"]["dotnetSdk"], "10.0.400")

    def test_write_is_no_overwrite_and_verify_replays_all_evidence(self) -> None:
        manifest = self._build()
        output = self.root / "manifests" / "common.json"
        verification.write_manifest(output, manifest)
        self.assertEqual(common.verify_manifest(self.root, "manifests/common.json"), manifest)
        with self.assertRaisesRegex(verification.ManifestError, "never overwritten"):
            verification.write_manifest(output, manifest)

    def test_rejected_exact_source_receipt_cannot_be_accepted(self) -> None:
        rejected = self._write_receipt(accepted=False)
        manifest = self._build(execution_receipt=rejected.relative_to(self.root).as_posix())
        self.assertFalse(manifest["accepted"])
        self.assertEqual(manifest["execution"]["checks"]["failed"], 1)

    def test_unaccepted_x64_verifier_fails_closed(self) -> None:
        verifier_path = self.root / self.inputs["x64_verifier_result"]
        value = json.loads(verifier_path.read_text(encoding="utf-8"))
        value["verified"] = False
        verifier_path.write_text(json.dumps(value), encoding="utf-8")
        with self.assertRaisesRegex(common.CommonReleaseError, "x64 build"):
            self._build()

    def test_x64_verifier_must_bind_exact_build_result(self) -> None:
        verifier_path = self.root / self.inputs["x64_verifier_result"]
        value = json.loads(verifier_path.read_text(encoding="utf-8"))
        value["buildResultSha256"] = "b" * 64
        verifier_path.write_text(json.dumps(value), encoding="utf-8")
        with self.assertRaisesRegex(common.CommonReleaseError, "x64 build"):
            self._build()

    def test_package_source_inputs_must_match_tested_source_bundle(self) -> None:
        path = self.root / self.inputs["arm_manifest"]
        value = json.loads(path.read_text(encoding="utf-8"))
        value["sourceInputs"][0]["sha256"] = "a" * 64
        path.write_text(json.dumps(value), encoding="utf-8")
        with self.assertRaisesRegex(common.CommonReleaseError, "not bound"):
            self._build()

    def test_version_authorities_must_match(self) -> None:
        with self.assertRaisesRegex(common.CommonReleaseError, "VERSION.json"):
            self._build(release_version="1.2026.08.07.12.01.A")

    def test_absolute_evidence_path_is_rejected(self) -> None:
        with self.assertRaisesRegex(verification.ManifestError, "project-relative"):
            self._build(x64_archive=str((self.root / self.inputs["x64_archive"]).resolve()))

    def test_linked_evidence_is_rejected_when_supported(self) -> None:
        target = self.root / self.inputs["x64_archive"]
        link = self.root / "artifacts" / "linked-x64.zip"
        try:
            try:
                os.symlink(target, link)
            except OSError:
                self.skipTest("symbolic-link creation is unavailable")
            with self.assertRaisesRegex(verification.ManifestError, "link or reparse"):
                self._build(x64_archive="artifacts/linked-x64.zip")
        finally:
            link.unlink(missing_ok=True)

    def test_manifest_tamper_is_detected(self) -> None:
        manifest = self._build()
        output = self.root / "manifests" / "tampered.json"
        verification.write_manifest(output, manifest)
        value = json.loads(output.read_text(encoding="utf-8"))
        value["accepted"] = False
        output.write_bytes(verification.formatted_manifest_bytes(value))
        with self.assertRaisesRegex(common.CommonReleaseError, "seal does not match"):
            common.verify_manifest(self.root, "manifests/tampered.json")

    def test_noncanonical_manifest_encoding_is_rejected(self) -> None:
        output = self.root / "manifests" / "noncanonical.json"
        output.write_text(json.dumps(self._build(), separators=(",", ":")), encoding="utf-8")
        with self.assertRaisesRegex(common.CommonReleaseError, "not canonically encoded"):
            common.verify_manifest(self.root, "manifests/noncanonical.json")

    def test_hard_linked_evidence_is_rejected_when_supported(self) -> None:
        target = self.root / self.inputs["x64_archive"]
        link = self.root / "artifacts" / "hard-linked-x64.zip"
        try:
            try:
                os.link(target, link)
            except OSError:
                self.skipTest("hard-link creation is unavailable")
            with self.assertRaisesRegex(common.CommonReleaseError, "hard-linked"):
                self._build(x64_archive="artifacts/hard-linked-x64.zip")
        finally:
            link.unlink(missing_ok=True)

    def test_cli_create_and_verify(self) -> None:
        args = ["create", "--root", str(self.root), "--output", "manifests/cli.json"]
        for name, value in self.inputs.items():
            args.extend(["--" + name.replace("_", "-"), value])
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(common.main(args), 0)
            self.assertEqual(
                common.main(["verify", "--root", str(self.root), "--manifest", "manifests/cli.json"]),
                0,
            )
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(common.main(args), 1)


if __name__ == "__main__":
    unittest.main()
