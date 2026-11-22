from __future__ import annotations

import copy
import contextlib
import datetime as dt
import hashlib
import io
import json
import os
import pathlib
import sys
import tempfile
import time
import unittest
import zipfile
from unittest import mock


TOOLS_ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS_ROOT))

import CortexVerificationManifest as manifest_tool  # noqa: E402


FIXED_ENVIRONMENT = {
    "dotnet": {
        "hostSha256": "a" * 64,
        "runtimeVersions": [{"name": "Microsoft.NETCore.App", "version": "10.0.0"}],
        "sdkVersion": "10.0.100",
        "sdkVersions": ["10.0.100"],
    },
    "operatingSystem": {"machine": "test", "release": "test", "system": "test"},
    "python": {"implementation": "CPython", "version": "3.11.0"},
}
FIXED_TIME = "2026-07-31T20:00:00.000000Z"


class CortexVerificationManifestTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="cortex-manifest-tests-")
        self.root = pathlib.Path(self.temporary.name).resolve()
        for name in ("bundles", "evidence", "extracts", "manifests", "plans", "receipts"):
            (self.root / name).mkdir()

        directory_roots = {
            ".cortex",
            "deploy",
            "docs",
            "integrations",
            "licenses",
            "schemas",
            "src",
            "tests",
            "tools",
        }
        for source_root in manifest_tool.DEFAULT_SOURCE_PATHS:
            path = self.root / source_root
            if source_root in directory_roots:
                path.mkdir(parents=True, exist_ok=True)
                (path / "fixture.txt").write_text(f"{source_root}\n", encoding="utf-8")
            else:
                path.write_text(f"{source_root}\n", encoding="utf-8")
        for authority_name in sorted(manifest_tool.MANDATORY_AUTHORITY_FILES):
            authority_path = self.root / authority_name
            authority_path.parent.mkdir(parents=True, exist_ok=True)
            authority_path.write_text(f"{authority_name}\n", encoding="utf-8")
        (self.root / "src" / "zeta").mkdir()
        (self.root / "src" / "alpha").mkdir()
        (self.root / "src" / "zeta" / "z.cs").write_text("z\n", encoding="utf-8")
        (self.root / "src" / "alpha" / "a.cs").write_text("a\n", encoding="utf-8")
        self.counter = 0
        self.metrics_path = self.root / "evidence" / "metrics.json"
        self._write_metrics()

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def _next(self, prefix: str) -> str:
        self.counter += 1
        return f"{prefix}-{self.counter}"

    def _passing_metrics(self) -> dict[str, object]:
        return {
            "authorityFallback": True,
            "codexRecoveryAb": None,
            "concurrentClients": {"clientCount": 8, "starvationCount": 0, "timeoutCount": 0},
            "deterministicTime": True,
            "exactSearch": {"passed": 5, "regressions": 0, "total": 5},
            "fullBuild": {"errors": 0, "exitCode": 0, "warnings": 0},
            "harnessMatrix": {"failed": 0, "passed": 3, "skipped": 0},
            "offlineRecovery": True,
            "projectBriefAuthority": True,
            "reservedIsolation": {"leakageCount": 0},
            "retrievalRecall": {"hits": 19, "total": 20},
        }

    def _write_metrics(self, metrics: dict[str, object] | None = None) -> None:
        value = {
            "contractId": manifest_tool.ACCEPTANCE_EVIDENCE_CONTRACT_ID,
            "metrics": self._passing_metrics() if metrics is None else metrics,
        }
        self.metrics_path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")

    def _codex_recovery_ab_evidence(self) -> dict[str, object]:
        seed = "ab" * 32
        pair_ids = [
            f"pair-{index:02d}"
            for index in range(1, manifest_tool.CODEX_RECOVERY_AB_PAIR_COUNT + 1)
        ]
        ranked = sorted(
            pair_ids,
            key=lambda pair_id: (
                hashlib.sha256(f"{seed}:{pair_id}".encode("utf-8")).hexdigest(),
                pair_id,
            ),
        )
        control_first = set(ranked[: manifest_tool.CODEX_RECOVERY_AB_PAIR_COUNT // 2])
        control_sources = [
            {
                "path": path,
                "sha256": hashlib.sha256(f"source:{path}".encode("utf-8")).hexdigest(),
            }
            for path in manifest_tool.CODEX_RECOVERY_AB_CONTROL_SOURCES
        ]
        authority_sources = [
            {
                "path": path,
                "sha256": hashlib.sha256(f"source:{path}".encode("utf-8")).hexdigest(),
            }
            for path in manifest_tool.CODEX_RECOVERY_AB_AUTHORITY_SOURCES
        ]
        control_source_set_sha256 = manifest_tool._ab_source_set_sha256(control_sources)
        authority_source_set_sha256 = manifest_tool._ab_source_set_sha256(authority_sources)
        metadata = {
            "authoritySetHash": manifest_tool._ab_authority_set_sha256(authority_sources),
            "model": "gpt-5.4",
            "permissionsSha256": "1" * 64,
            "projectSnapshotSha256": control_source_set_sha256,
            "promptSha256": "3" * 64,
            "reasoningLevel": "high",
            "runtimePackageAttestationSha256": "5" * 64,
            "testedSourceBundleSha256": "6" * 64,
        }
        trials: list[dict[str, object]] = []
        captured_base = dt.datetime(2026, 7, 31, 20, 0, tzinfo=dt.timezone.utc)
        for pair_id in pair_ids:
            for arm in ("control", "treatment"):
                control_order = 1 if pair_id in control_first else 2
                order = control_order if arm == "control" else 3 - control_order
                pair_index = int(pair_id[-2:])
                exact_reads = 1 if arm == "treatment" and int(pair_id[-2:]) % 2 == 0 else 0
                cortex_calls = 1 + exact_reads if arm == "treatment" else 0
                captured_at = captured_base + dt.timedelta(minutes=(pair_index - 1) * 2 + order - 1)
                trials.append(
                    {
                        "arm": arm,
                        "capturedAtUtc": captured_at.isoformat(timespec="microseconds").replace(
                            "+00:00", "Z"
                        ),
                        "freshTask": True,
                        "inheritedHistory": False,
                        "metadata": dict(metadata),
                        "metrics": {
                            "mcp.call.duration_ms": (
                                [8.0] * cortex_calls if arm == "treatment" else None
                            ),
                            "mcp.tools.list.duration_ms": {
                                "samples": [3.0],
                                "unavailableReason": None,
                            },
                            "turn.e2e_duration_ms": 800.0 if arm == "treatment" else 1000.0,
                            "turn.token_usage": {
                                "cachedInputTokens": 500,
                                "outputTokens": 100,
                                "uncachedInputTokens": 250 if arm == "treatment" else 1000,
                            },
                            "turn.tool.call": cortex_calls + 1,
                        },
                        "order": order,
                        "pairId": pair_id,
                        "recovery": {
                            "authorityProbeSources": (
                                copy.deepcopy(authority_sources) if arm == "treatment" else []
                            ),
                            "authorityProbeUsed": arm == "treatment",
                            "cortexCallCount": cortex_calls,
                            "exactReadCount": exact_reads,
                            "fileReadCount": (
                                len(authority_sources) if arm == "treatment" else len(control_sources)
                            ),
                            "fileSources": copy.deepcopy(control_sources) if arm == "control" else [],
                            "fileSourceSetSha256": (
                                authority_source_set_sha256
                                if arm == "treatment"
                                else control_source_set_sha256
                            ),
                            "mode": "cortex" if arm == "treatment" else "file",
                            "projectBriefCalls": 1 if arm == "treatment" else 0,
                        },
                        "runTraceSha256": hashlib.sha256(
                            f"redacted-run:{pair_id}:{arm}".encode("utf-8")
                        ).hexdigest(),
                        "validation": {
                            "authoritySetHash": True,
                            "checkpoint": True,
                            "currentTask": True,
                            "gate": True,
                            "nextAction": True,
                            "revision": True,
                            "state": True,
                        },
                    }
                )
        return {
            "contractId": manifest_tool.CODEX_RECOVERY_AB_EVIDENCE_CONTRACT_ID,
            "randomization": {
                "algorithm": "sha256-balanced-pair-order/1",
                "seed": seed,
            },
            "redacted": True,
            "trials": trials,
        }

    def _create_bundle(
        self,
        name: str | None = None,
        source_paths: tuple[str, ...] | None = None,
    ) -> pathlib.Path:
        path = self.root / "bundles" / (name or f"{self._next('tested')}.zip")
        manifest_tool.create_source_bundle(
            self.root,
            path,
            source_paths=source_paths or manifest_tool.DEFAULT_SOURCE_PATHS,
            captured_at=FIXED_TIME,
        )
        return path

    def _execute(self, bundle: pathlib.Path, *, mutating: bool = False) -> tuple[pathlib.Path, pathlib.Path]:
        stem = self._next("execution")
        plan = self.root / "plans" / f"{stem}.json"
        exit_code = (
            "import pathlib; pathlib.Path('src/alpha/a.cs').write_text('changed')"
            if mutating
            else "raise SystemExit(0)"
        )
        harness_lines = [
            "PASS context-retention-harness",
            "METRIC authority-fallback passed=true",
            "METRIC concurrent-clients clients=8 starvation=0 timeouts=0",
            "METRIC deterministic-time passed=true",
            "METRIC exact-search passed=5 total=5 regressions=0",
            "METRIC offline-recovery passed=true",
            "METRIC project-brief-authority passed=true",
            "METRIC reserved-isolation leakage=0",
            "METRIC retrieval-recall hits=19 total=20",
        ]
        plan.write_text(
            json.dumps(
                {
                    "contractId": manifest_tool.EXECUTION_PLAN_CONTRACT_ID,
                    "commands": [
                        {
                            "id": "exit-probe",
                            "argv": [sys.executable, "-c", exit_code],
                            "parser": "exit-only",
                            "timeoutSeconds": 30,
                            "workingDirectory": ".",
                        },
                        {
                            "id": "release-build",
                            "argv": [
                                sys.executable,
                                "-c",
                                "print('0 Warning(s)'); print('0 Error(s)')",
                            ],
                            "parser": "msbuild",
                            "timeoutSeconds": 30,
                            "workingDirectory": ".",
                        },
                        {
                            "id": "pegasus-harness",
                            "argv": [sys.executable, "-c", f"print({chr(10).join(harness_lines)!r})"],
                            "parser": "pegasus-harness",
                            "timeoutSeconds": 30,
                            "workingDirectory": ".",
                        },
                        {
                            "id": "python-tests",
                            "argv": [
                                sys.executable,
                                "-c",
                                "print('Ran 2 tests in 0.001s'); print(); print('OK')",
                            ],
                            "parser": "python-unittest",
                            "timeoutSeconds": 30,
                            "workingDirectory": ".",
                        }
                    ],
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )
        extraction = self.root / "extracts" / stem
        receipt = self.root / "receipts" / f"{stem}.json"
        manifest_tool.execute_source_bundle(
            self.root,
            bundle.relative_to(self.root).as_posix(),
            plan.relative_to(self.root).as_posix(),
            extraction,
            receipt,
            captured_at=FIXED_TIME,
        )
        return extraction, receipt

    def _write_results(
        self,
        receipt: pathlib.Path,
        *,
        checks: dict[str, int] | None = None,
        additional_gates: list[dict[str, str]] | None = None,
        argv_override: list[str] | None = None,
        evidence_paths: list[str] | None = None,
    ) -> pathlib.Path:
        receipt_value = json.loads(receipt.read_text(encoding="utf-8"))
        commands = receipt_value["commands"]
        results = self.root / "evidence" / f"{self._next('results')}.json"
        value = {
            "contractId": manifest_tool.RESULTS_CONTRACT_ID,
            "acceptanceProfileId": manifest_tool.ACCEPTANCE_PROFILE_ID,
            "additionalGates": additional_gates or [],
            "commands": [
                {
                    "id": command["id"],
                    "argv": argv_override if index == 0 and argv_override else command["argv"],
                    "workingDirectory": command["workingDirectory"],
                    "exitCode": command["exitCode"],
                    "checks": checks if index == 0 and checks is not None else command["checks"],
                    "skipDetails": command["skipDetails"],
                }
                for index, command in enumerate(commands)
            ],
            "evidencePaths": evidence_paths or [self.metrics_path.relative_to(self.root).as_posix()],
            "executionReceiptPath": receipt.relative_to(self.root).as_posix(),
        }
        results.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
        return results

    def _build(
        self,
        bundle: pathlib.Path | None = None,
        *,
        checks: dict[str, int] | None = None,
        additional_gates: list[dict[str, str]] | None = None,
        argv_override: list[str] | None = None,
    ) -> tuple[dict[str, object], pathlib.Path, pathlib.Path]:
        bundle = bundle or self._create_bundle()
        extraction, receipt = self._execute(bundle)
        results = self._write_results(
            receipt,
            checks=checks,
            additional_gates=additional_gates,
            argv_override=argv_override,
        )
        manifest = manifest_tool.build_manifest(
            self.root,
            results.relative_to(self.root).as_posix(),
            tested_source_bundle=bundle.relative_to(self.root).as_posix(),
            captured_at=FIXED_TIME,
            environment=FIXED_ENVIRONMENT,
        )
        return manifest, extraction, results

    def test_bundle_and_manifest_are_deterministic(self) -> None:
        first_bundle = self._create_bundle("first.zip")
        second_bundle = self._create_bundle("second.zip")
        self.assertEqual(first_bundle.read_bytes(), second_bundle.read_bytes())
        first, _, results = self._build(first_bundle)
        second = manifest_tool.build_manifest(
            self.root,
            results.relative_to(self.root).as_posix(),
            tested_source_bundle=first_bundle.relative_to(self.root).as_posix(),
            captured_at=FIXED_TIME,
            environment=FIXED_ENVIRONMENT,
        )
        self.assertEqual(manifest_tool.formatted_manifest_bytes(first), manifest_tool.formatted_manifest_bytes(second))
        self.assertFalse(first["verification"]["accepted"])
        ab_gate = next(
            gate for gate in first["verification"]["gates"] if gate["id"] == "codex-recovery-ab"
        )
        self.assertEqual(ab_gate["status"], "blocked")
        manifest_path = self.root / "manifests" / "accepted.json"
        manifest_tool.write_manifest(manifest_path, first)
        verified = manifest_tool.verify_manifest(
            self.root, manifest_path.relative_to(self.root).as_posix()
        )
        self.assertFalse(verified["verification"]["accepted"])

    def test_pre_ab_candidate_is_sealed_but_not_accepted(self) -> None:
        metrics = self._passing_metrics()
        metrics["codexRecoveryAb"] = None
        self._write_metrics(metrics)
        manifest, _, _ = self._build()
        self.assertFalse(manifest["verification"]["accepted"])
        gate = next(g for g in manifest["verification"]["gates"] if g["id"] == "codex-recovery-ab")
        self.assertEqual(gate["status"], "blocked")

    def test_acceptance_rejects_zero_check_command(self) -> None:
        manifest, _, _ = self._build(checks={"passed": 0, "failed": 0, "skipped": 0})
        self.assertFalse(manifest["verification"]["accepted"])
        self.assertEqual(manifest["verification"]["zeroCheckCommandIds"], ["exit-probe"])

    def test_acceptance_rejects_extra_blocked_or_skipped_gate(self) -> None:
        for status in ("blocked", "skipped"):
            with self.subTest(status=status):
                manifest, _, _ = self._build(
                    additional_gates=[{"id": f"extra-{status}", "status": status, "summary": "Unresolved."}]
                )
                self.assertFalse(manifest["verification"]["accepted"])

    def test_acceptance_profile_rejects_incomplete_source_roots(self) -> None:
        bundle = self._create_bundle(source_paths=("src",))
        manifest, _, _ = self._build(bundle)
        self.assertFalse(manifest["verification"]["accepted"])
        self.assertIn("AGENTS.md", manifest["verification"]["missingRequiredSourceRoots"])

    def test_default_bundle_includes_hot_recovery_authority_bytes(self) -> None:
        bundle = self._create_bundle()
        verified = manifest_tool.verify_source_bundle(
            self.root, bundle.relative_to(self.root).as_posix()
        )
        paths = {record["path"] for record in verified["source"]["files"]}
        self.assertIn("START_HERE.md", paths)
        self.assertIn(".cortex/CURRENT.json", paths)
        self.assertIn(".cortex/RECOVERY.md", paths)
        self.assertIn(".cortex/STATE.md", paths)
        self.assertIn(".cortex/TASKS.md", paths)

    def test_explicit_source_set_omitting_authority_roots_cannot_be_accepted(self) -> None:
        explicit = tuple(
            root
            for root in manifest_tool.DEFAULT_SOURCE_PATHS
            if root not in {".cortex", "START_HERE.md"}
        )
        bundle = self._create_bundle(source_paths=explicit)
        manifest, _, _ = self._build(bundle)
        self.assertFalse(manifest["verification"]["accepted"])
        self.assertEqual(
            manifest["verification"]["missingRequiredSourceRoots"],
            [".cortex", "START_HERE.md"],
        )

    def test_incomplete_cortex_authority_root_cannot_be_accepted(self) -> None:
        (self.root / ".cortex" / "CURRENT.json").unlink()
        bundle = self._create_bundle()
        manifest, _, _ = self._build(bundle)
        self.assertFalse(manifest["verification"]["accepted"])
        self.assertEqual(
            manifest["verification"]["missingRequiredAuthorityFiles"],
            [".cortex/CURRENT.json"],
        )

    def test_file_ancestor_collisions_are_rejected_before_bundle_use(self) -> None:
        with self.assertRaisesRegex(
            manifest_tool.ManifestError, "file/directory ancestor collision"
        ):
            manifest_tool._reject_file_ancestor_collisions(
                ("source", "source/child.txt"), "The test inventory"
            )

    def test_timeout_terminates_descendant_process_tree(self) -> None:
        marker = self.root / "evidence" / "orphan-marker.txt"
        child = (
            "import pathlib,time; "
            "time.sleep(1.5); "
            f"pathlib.Path({str(marker)!r}).write_text('orphan', encoding='utf-8')"
        )
        parent = (
            "import subprocess,sys,time; "
            "subprocess.Popen([sys.executable, '-c', "
            f"{child!r}], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL); "
            "time.sleep(60)"
        )
        result = manifest_tool._run_bounded_command(
            {
                "argv": [sys.executable, "-c", parent],
                "parser": "exit-only",
                "timeoutSeconds": 1,
            },
            self.root,
        )
        self.assertEqual(result["terminationReason"], "timeout")
        self.assertFalse(result["parserAccepted"])
        time.sleep(1.0)
        self.assertFalse(marker.exists(), "A timed-out descendant survived the process boundary.")

    def test_gate_status_is_derived_from_exact_metrics_thresholds(self) -> None:
        metrics = self._passing_metrics()
        metrics["retrievalRecall"] = {"hits": 18, "total": 20}
        self._write_metrics(metrics)
        bundle = self._create_bundle()
        _, receipt = self._execute(bundle)
        results = self._write_results(receipt)
        with self.assertRaisesRegex(manifest_tool.ManifestError, "differ from trusted execution"):
            manifest_tool.build_manifest(
                self.root,
                results.relative_to(self.root).as_posix(),
                tested_source_bundle=bundle.relative_to(self.root).as_posix(),
                captured_at=FIXED_TIME,
                environment=FIXED_ENVIRONMENT,
            )

    def test_named_platform_skips_are_preserved_without_failing_the_matrix_gate(self) -> None:
        metrics = self._passing_metrics()
        metrics["harnessMatrix"] = {"failed": 0, "passed": 10, "skipped": 2}
        gate = next(
            item for item in manifest_tool._derived_gates(metrics) if item["id"] == "harness-matrix"
        )
        self.assertEqual(gate["status"], "passed")
        admitted, coverage = manifest_tool._excluded_coverage_gate(
            [
                {
                    "id": "mcp-local-tests",
                    "skipDetails": [
                        {
                            "name": "recovery reparse probe",
                            "reason": "Symbolic-link creation is unavailable.",
                        }
                    ],
                },
                {
                    "id": "heartbeat-core-tests",
                    "skipDetails": [
                        {
                            "name": "test_reparse_mutation_guard_fails_closed_when_supported",
                            "reason": "local symlink creation is unavailable",
                        }
                    ],
                },
                {
                    "id": "arm64-package-contract-tests",
                    "skipDetails": [
                        {
                            "name": "test_package_root_path_rejects_relative_and_linked_components",
                            "reason": "directory symbolic-link creation is unavailable",
                        },
                        {
                            "name": "test_bounded_subprocess_timeout_reaps_same_group_descendants",
                            "reason": "Process-group cleanup is POSIX-only.",
                        },
                        {
                            "name": "test_bounded_subprocess_preserves_direct_signal_as_negative",
                            "reason": "POSIX signal return codes are Linux-only.",
                        },
                    ],
                },
                {
                    "id": "postgresql-integration-tests",
                    "skipDetails": [
                        {
                            "name": "LIVE integration",
                            "reason": "set PEGASUS_CORTEX_PG_INTEGRATION=1 and supply isolated loopback connections plus an explicit transport profile",
                        }
                    ],
                },
                {
                    "id": "common-release-manifest-tests",
                    "skipDetails": [
                        {
                            "name": "test_linked_evidence_is_rejected_when_supported",
                            "reason": "symbolic-link creation is unavailable",
                        },
                        {
                            "name": "test_hard_linked_evidence_is_rejected_when_supported",
                            "reason": "hard-link creation is unavailable",
                        },
                    ],
                },
            ]
        )
        self.assertEqual(admitted["status"], "passed")
        self.assertEqual(coverage["admissibleCount"], 8)
        rejected, rejected_coverage = manifest_tool._excluded_coverage_gate(
            [
                {
                    "id": "arm64-package-contract-tests",
                    "skipDetails": [
                        {
                            "name": "test_package_root_path_rejects_relative_and_linked_components",
                            "reason": "directory symbolic-link creation was not verified",
                        }
                    ],
                }
            ]
        )
        self.assertEqual(rejected["status"], "failed")
        self.assertEqual(rejected_coverage["unexpectedCount"], 1)

    def test_receipt_retains_only_output_derived_checks_and_metrics(self) -> None:
        bundle = self._create_bundle()
        _, receipt_path = self._execute(bundle)
        receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
        commands = {command["id"]: command for command in receipt["commands"]}
        self.assertEqual(commands["exit-probe"]["checks"], {"failed": 0, "passed": 1, "skipped": 0})
        self.assertEqual(
            commands["release-build"]["observedMetrics"]["fullBuild"],
            {"errors": 0, "exitCode": 0, "warnings": 0},
        )
        self.assertEqual(commands["pegasus-harness"]["observedMetrics"]["retrievalRecall"], {"hits": 19, "total": 20})
        self.assertEqual(commands["python-tests"]["checks"], {"failed": 0, "passed": 2, "skipped": 0})
        self.assertNotIn("stdout", json.dumps(receipt).casefold())
        self.assertNotIn("stderr", json.dumps(receipt).casefold())

    def test_closed_parsers_count_named_harness_and_unittest_skips(self) -> None:
        harness = manifest_tool._parse_command_output(
            "pegasus-harness",
            "PASS alpha\nFAIL beta\nSKIP windows-link :: Privilege unavailable.\n",
            0,
        )
        self.assertEqual(harness["checks"], {"failed": 1, "passed": 1, "skipped": 1})
        self.assertEqual(harness["skipDetails"][0]["name"], "windows-link")
        unittest_summary = manifest_tool._parse_command_output(
            "python-unittest", "Ran 3 tests in 0.010s\n\nOK (skipped=1)\n", 0
        )
        self.assertEqual(unittest_summary["checks"], {"failed": 0, "passed": 2, "skipped": 1})
        verbose_unittest = manifest_tool._parse_command_output(
            "python-unittest",
            "\n".join(
                [
                    "test_alpha (suite.Tests.test_alpha) ... ok",
                    "test_link (suite.Tests.test_link) ... skipped 'symbolic-link creation is unavailable'",
                    "----------------------------------------------------------------------",
                    "Ran 2 tests in 0.010s",
                    "",
                    "OK (skipped=1)",
                ]
            ),
            0,
        )
        self.assertTrue(verbose_unittest["parserAccepted"])
        self.assertEqual(
            verbose_unittest["skipDetails"],
            [{"name": "test_link", "reason": "symbolic-link creation is unavailable"}],
        )
        duplicate_build_summary = manifest_tool._parse_command_output(
            "msbuild",
            "0 Warning(s)\n0 Error(s)\n0 Warning(s)\n0 Error(s)\n",
            0,
        )
        self.assertFalse(duplicate_build_summary["parserAccepted"])

    def test_codex_recovery_ab_parser_derives_metrics_from_twenty_paired_trials(self) -> None:
        evidence = self._codex_recovery_ab_evidence()
        parsed = manifest_tool._parse_command_output(
            "codex-recovery-ab", json.dumps(evidence), 0
        )
        self.assertTrue(parsed["parserAccepted"])
        retained = parsed["observedMetrics"]["codexRecoveryAbEvidence"]
        self.assertEqual(len(retained["trials"]), 40)
        first_control = next(
            trial
            for trial in retained["trials"]
            if trial["pairId"] == "pair-01" and trial["arm"] == "control"
        )
        self.assertEqual(
            first_control["metrics"]["turn.token_usage"],
            {"cachedInputTokens": 500, "outputTokens": 100, "uncachedInputTokens": 1000},
        )
        derived = manifest_tool._derive_codex_recovery_ab_metrics(retained)
        self.assertEqual(
            derived,
            {
                "maxCortexCalls": 2,
                "medianLatencyReductionPercent": 20.0,
                "medianUncachedInputReductionPercent": 75.0,
                "p95LatencyRegressionPercent": -20.0,
                "stateMatches": 20,
                "trials": 20,
            },
        )

        metrics = self._passing_metrics()
        metrics["codexRecoveryAb"] = derived
        self._write_metrics(metrics)
        loaded = manifest_tool.load_acceptance_metrics(
            self.root, [self.metrics_path.relative_to(self.root).as_posix()]
        )
        self.assertEqual(loaded["codexRecoveryAb"], derived)

    def test_codex_recovery_ab_receipt_derivation_uses_trial_evidence_not_aggregate(self) -> None:
        parsed = manifest_tool._parse_command_output(
            "codex-recovery-ab", json.dumps(self._codex_recovery_ab_evidence()), 0
        )
        receipt = {
            "capturedAtUtc": "2026-07-31T21:00:00.000000Z",
            "commands": [
                {
                    "checks": {"failed": 0, "passed": 1, "skipped": 0},
                    "observedMetrics": {
                        "authorityFallback": True,
                        "concurrentClients": {
                            "clientCount": 8,
                            "starvationCount": 0,
                            "timeoutCount": 0,
                        },
                        "deterministicTime": True,
                        "exactSearch": {"passed": 5, "regressions": 0, "total": 5},
                        "offlineRecovery": True,
                        "projectBriefAuthority": True,
                        "reservedIsolation": {"leakageCount": 0},
                        "retrievalRecall": {"hits": 19, "total": 20},
                    },
                    "parser": "pegasus-harness",
                },
                {
                    "checks": {"failed": 0, "passed": 1, "skipped": 0},
                    "observedMetrics": {
                        "fullBuild": {"errors": 0, "exitCode": 0, "warnings": 0}
                    },
                    "parser": "msbuild",
                },
                {
                    "checks": parsed["checks"],
                    "observedMetrics": parsed["observedMetrics"],
                    "parser": "codex-recovery-ab",
                },
            ],
            "testedSourceBundle": {"sha256": "6" * 64},
        }
        retained_evidence = parsed["observedMetrics"]["codexRecoveryAbEvidence"]
        control = next(trial for trial in retained_evidence["trials"] if trial["arm"] == "control")
        treatment = next(
            trial for trial in retained_evidence["trials"] if trial["arm"] == "treatment"
        )
        source_by_path = {
            record["path"]: record["sha256"]
            for record in (
                control["recovery"]["fileSources"]
                + treatment["recovery"]["authorityProbeSources"]
            )
        }
        source_inventory = {
            "files": [
                {"bytes": 1, "path": path, "sha256": sha256}
                for path, sha256 in sorted(source_by_path.items())
            ]
        }
        derived = manifest_tool.derive_acceptance_metrics_from_receipt(
            receipt, source_inventory=source_inventory
        )
        self.assertEqual(derived["codexRecoveryAb"]["stateMatches"], 20)
        self.assertEqual(derived["codexRecoveryAb"]["maxCortexCalls"], 2)

        fabricated = copy.deepcopy(receipt)
        fabricated["commands"][2]["observedMetrics"] = {
            "codexRecoveryAb": {
                "maxCortexCalls": 0,
                "medianLatencyReductionPercent": 99.0,
                "medianUncachedInputReductionPercent": 99.0,
                "p95LatencyRegressionPercent": 0.0,
                "stateMatches": 20,
                "trials": 20,
            }
        }
        with self.assertRaisesRegex(manifest_tool.ManifestError, "unknown codexRecoveryAb"):
            manifest_tool.derive_acceptance_metrics_from_receipt(
                fabricated, source_inventory=source_inventory
            )

        wrong_bundle = copy.deepcopy(receipt)
        wrong_bundle["testedSourceBundle"]["sha256"] = "9" * 64
        with self.assertRaisesRegex(manifest_tool.ManifestError, "not bound"):
            manifest_tool.derive_acceptance_metrics_from_receipt(
                wrong_bundle, source_inventory=source_inventory
            )

        wrong_inventory = copy.deepcopy(source_inventory)
        wrong_inventory["files"][0]["sha256"] = "9" * 64
        with self.assertRaisesRegex(manifest_tool.ManifestError, "tested source inventory"):
            manifest_tool.derive_acceptance_metrics_from_receipt(
                receipt, source_inventory=wrong_inventory
            )

        future_receipt = copy.deepcopy(receipt)
        future_receipt["capturedAtUtc"] = "2026-07-31T19:00:00.000000Z"
        with self.assertRaisesRegex(manifest_tool.ManifestError, "future trial timestamp"):
            manifest_tool.derive_acceptance_metrics_from_receipt(
                future_receipt, source_inventory=source_inventory
            )

    def test_codex_recovery_ab_parser_rejects_untrusted_trial_shapes(self) -> None:
        cases: dict[str, dict[str, object]] = {}

        unpaired = self._codex_recovery_ab_evidence()
        unpaired["trials"] = unpaired["trials"][:-1]  # type: ignore[index]
        cases["unpaired"] = unpaired

        duplicated = self._codex_recovery_ab_evidence()
        duplicated_trials = duplicated["trials"]  # type: ignore[assignment]
        duplicated_trials[1]["pairId"] = duplicated_trials[0]["pairId"]  # type: ignore[index]
        duplicated_trials[1]["arm"] = duplicated_trials[0]["arm"]  # type: ignore[index]
        cases["duplicate"] = duplicated

        nonrandomized = self._codex_recovery_ab_evidence()
        pair_one = [
            trial for trial in nonrandomized["trials"] if trial["pairId"] == "pair-01"  # type: ignore[index]
        ]
        pair_one[0]["order"], pair_one[1]["order"] = pair_one[1]["order"], pair_one[0]["order"]
        cases["nonrandomized"] = nonrandomized

        inconsistent = self._codex_recovery_ab_evidence()
        inconsistent["trials"][1]["metadata"]["model"] = "different-model"  # type: ignore[index]
        cases["inconsistent"] = inconsistent

        inherited = self._codex_recovery_ab_evidence()
        inherited["trials"][0]["inheritedHistory"] = True  # type: ignore[index]
        cases["inherited"] = inherited

        duplicate_trace = self._codex_recovery_ab_evidence()
        duplicate_trace["trials"][1]["runTraceSha256"] = duplicate_trace["trials"][0][  # type: ignore[index]
            "runTraceSha256"
        ]
        cases["duplicate-trace"] = duplicate_trace

        missing_mcp_timing = self._codex_recovery_ab_evidence()
        treatment = next(
            trial for trial in missing_mcp_timing["trials"] if trial["arm"] == "treatment"  # type: ignore[index]
        )
        treatment["metrics"]["mcp.call.duration_ms"] = None
        cases["missing-mcp-timing"] = missing_mcp_timing

        missing_file_provenance = self._codex_recovery_ab_evidence()
        control = next(
            trial for trial in missing_file_provenance["trials"] if trial["arm"] == "control"  # type: ignore[index]
        )
        control["recovery"]["fileReadCount"] = 0
        cases["missing-file-provenance"] = missing_file_provenance

        inconsistent_file_source = self._codex_recovery_ab_evidence()
        second_control = [
            trial for trial in inconsistent_file_source["trials"] if trial["arm"] == "control"  # type: ignore[index]
        ][1]
        second_control["recovery"]["fileSourceSetSha256"] = "8" * 64
        cases["inconsistent-file-source"] = inconsistent_file_source

        reversed_time = self._codex_recovery_ab_evidence()
        pair_one_times = [
            trial for trial in reversed_time["trials"] if trial["pairId"] == "pair-01"  # type: ignore[index]
        ]
        pair_one_times[0]["capturedAtUtc"], pair_one_times[1]["capturedAtUtc"] = (
            pair_one_times[1]["capturedAtUtc"],
            pair_one_times[0]["capturedAtUtc"],
        )
        cases["reversed-time"] = reversed_time

        zero_output = self._codex_recovery_ab_evidence()
        zero_output["trials"][0]["metrics"]["turn.token_usage"]["outputTokens"] = 0  # type: ignore[index]
        cases["zero-output"] = zero_output

        silent_tools_list = self._codex_recovery_ab_evidence()
        silent_tools_list["trials"][0]["metrics"]["mcp.tools.list.duration_ms"] = None  # type: ignore[index]
        cases["silent-tools-list"] = silent_tools_list

        raw_content = self._codex_recovery_ab_evidence()
        raw_content["trials"][0]["prompt"] = "raw prompt"  # type: ignore[index]
        cases["raw-content"] = raw_content

        for name, evidence in cases.items():
            with self.subTest(name=name):
                parsed = manifest_tool._parse_command_output(
                    "codex-recovery-ab", json.dumps(evidence), 0
                )
                self.assertFalse(parsed["parserAccepted"])

        valid_json = json.dumps(self._codex_recovery_ab_evidence())
        duplicate_key_json = valid_json.replace(
            '"redacted": true', '"redacted": true, "redacted": true', 1
        )
        self.assertFalse(
            manifest_tool._parse_command_output(
                "codex-recovery-ab", duplicate_key_json, 0
            )["parserAccepted"]
        )

        explicit_unavailable = self._codex_recovery_ab_evidence()
        for trial in explicit_unavailable["trials"]:  # type: ignore[index]
            trial["metrics"]["mcp.tools.list.duration_ms"] = {
                "samples": None,
                "unavailableReason": "telemetry_not_emitted",
            }
        self.assertTrue(
            manifest_tool._parse_command_output(
                "codex-recovery-ab", json.dumps(explicit_unavailable), 0
            )["parserAccepted"]
        )

    def test_codex_recovery_ab_state_matches_are_derived_per_pair(self) -> None:
        evidence = self._codex_recovery_ab_evidence()
        evidence["trials"][0]["validation"]["currentTask"] = False  # type: ignore[index]
        parsed = manifest_tool._parse_command_output(
            "codex-recovery-ab", json.dumps(evidence), 0
        )
        self.assertTrue(parsed["parserAccepted"])
        derived = manifest_tool._derive_codex_recovery_ab_metrics(
            parsed["observedMetrics"]["codexRecoveryAbEvidence"]
        )
        self.assertEqual(derived["stateMatches"], 19)

    def test_codex_recovery_ab_statistics_preserve_pairing(self) -> None:
        evidence = self._codex_recovery_ab_evidence()
        by_pair: dict[str, dict[str, dict[str, object]]] = {}
        for trial in evidence["trials"]:  # type: ignore[index]
            by_pair.setdefault(trial["pairId"], {})[trial["arm"]] = trial
        for pair_index in range(1, manifest_tool.CODEX_RECOVERY_AB_PAIR_COUNT + 1):
            pair = by_pair[f"pair-{pair_index:02d}"]
            control_value, treatment_value = (100, 900) if pair_index <= 10 else (1000, 90)
            pair["control"]["metrics"]["turn.e2e_duration_ms"] = float(control_value)
            pair["treatment"]["metrics"]["turn.e2e_duration_ms"] = float(treatment_value)
            pair["control"]["metrics"]["turn.token_usage"]["uncachedInputTokens"] = control_value
            pair["treatment"]["metrics"]["turn.token_usage"]["uncachedInputTokens"] = treatment_value

        parsed = manifest_tool._parse_command_output(
            "codex-recovery-ab", json.dumps(evidence), 0
        )
        self.assertTrue(parsed["parserAccepted"])
        derived = manifest_tool._derive_codex_recovery_ab_metrics(
            parsed["observedMetrics"]["codexRecoveryAbEvidence"]
        )
        # Independent arm medians would misleadingly report a 10% improvement;
        # paired deltas expose the order-correlated regression.
        self.assertEqual(derived["medianLatencyReductionPercent"], -354.5)
        self.assertEqual(derived["medianUncachedInputReductionPercent"], -354.5)
        self.assertEqual(derived["p95LatencyRegressionPercent"], 800.0)

    def test_harness_parser_accepts_only_existing_bounded_summary_and_skip_shapes(self) -> None:
        output = "\n".join(
            [
                "PASS alpha, beta",
                "PASS C# canonical UTC +00:00 bytes",
                "SKIP windows-link: Privilege unavailable.",
                "SKIP native Linux filesystem cases (current platform is not Linux)",
                "SKIP LIVE set PEGASUS_CORTEX_POSTGRESQL_INTEGRATION=1 and supply isolated loopback inputs",
                "RESULT 1/1 passed",
                "RESULT 2/2 passed, 3 skipped",
                "SUMMARY passed=2 failed=0",
                "RESULT offline=3 live=skipped",
                "Pegasus Cortex local MCP checks passed (1/1).",
                "Linux offline-journal platform tests passed: 1/1",
            ]
        )
        parsed = manifest_tool._parse_command_output("pegasus-harness", output, 0)
        self.assertTrue(parsed["parserAccepted"])
        self.assertEqual(parsed["checks"], {"failed": 0, "passed": 2, "skipped": 3})
        self.assertEqual(
            [item["name"] for item in parsed["skipDetails"]],
            ["LIVE integration", "native Linux filesystem cases", "windows-link"],
        )

        for invalid in (
            "PASS alpha\nRESULT arbitrary success",
            "PASS alpha\nINFO raw diagnostic",
            "PASS alpha\nSEED C:\\private\\artifact.json",
            "PASS alpha\nSKIP link: token=supersecretvalue",
            "PASS alpha\nSKIP unstructured reason",
        ):
            with self.subTest(invalid=invalid):
                self.assertFalse(
                    manifest_tool._parse_command_output(
                        "pegasus-harness", invalid, 0
                    )["parserAccepted"]
                )

    def test_fabricated_positive_checks_cannot_pass(self) -> None:
        manifest, _, _ = self._build(checks={"passed": 999, "failed": 0, "skipped": 0})
        self.assertFalse(manifest["verification"]["accepted"])
        self.assertFalse(manifest["verification"]["commandsBoundToExecutionReceipt"])

    def test_fabricated_build_metrics_are_rejected(self) -> None:
        metrics = self._passing_metrics()
        metrics["fullBuild"] = {"errors": 0, "exitCode": 0, "warnings": 1}
        self._write_metrics(metrics)
        bundle = self._create_bundle()
        _, receipt = self._execute(bundle)
        results = self._write_results(receipt)
        with self.assertRaisesRegex(manifest_tool.ManifestError, "differ from trusted execution"):
            manifest_tool.build_manifest(
                self.root,
                results.relative_to(self.root).as_posix(),
                tested_source_bundle=bundle.relative_to(self.root).as_posix(),
                captured_at=FIXED_TIME,
                environment=FIXED_ENVIRONMENT,
            )

    def test_fabricated_concurrency_metrics_are_rejected(self) -> None:
        metrics = self._passing_metrics()
        metrics["concurrentClients"] = {"clientCount": 999, "starvationCount": 0, "timeoutCount": 0}
        self._write_metrics(metrics)
        bundle = self._create_bundle()
        _, receipt = self._execute(bundle)
        results = self._write_results(receipt)
        with self.assertRaisesRegex(manifest_tool.ManifestError, "differ from trusted execution"):
            manifest_tool.build_manifest(
                self.root,
                results.relative_to(self.root).as_posix(),
                tested_source_bundle=bundle.relative_to(self.root).as_posix(),
                captured_at=FIXED_TIME,
                environment=FIXED_ENVIRONMENT,
            )

    def test_ab_aggregate_is_not_self_authorizable_without_trial_receipt(self) -> None:
        metrics = self._passing_metrics()
        metrics["codexRecoveryAb"] = {
            "maxCortexCalls": 1,
            "medianLatencyReductionPercent": 99.0,
            "medianUncachedInputReductionPercent": 99.0,
            "p95LatencyRegressionPercent": 0.0,
            "stateMatches": 20,
            "trials": 20,
        }
        self._write_metrics(metrics)
        bundle = self._create_bundle()
        _, receipt = self._execute(bundle)
        results = self._write_results(receipt)
        with self.assertRaisesRegex(manifest_tool.ManifestError, "differ from trusted execution"):
            manifest_tool.build_manifest(
                self.root,
                results.relative_to(self.root).as_posix(),
                tested_source_bundle=bundle.relative_to(self.root).as_posix(),
                captured_at=FIXED_TIME,
                environment=FIXED_ENVIRONMENT,
            )

    def test_results_must_match_bound_execution_command_identities(self) -> None:
        manifest, _, _ = self._build(argv_override=[sys.executable, "-c", "print('different command')"])
        self.assertFalse(manifest["verification"]["accepted"])
        self.assertFalse(manifest["verification"]["commandsBoundToExecutionReceipt"])

    def test_executed_source_must_remain_exact(self) -> None:
        bundle = self._create_bundle()
        extraction, receipt = self._execute(bundle)
        (extraction / "src" / "alpha" / "a.cs").write_text("tampered\n", encoding="utf-8")
        results = self._write_results(receipt)
        with self.assertRaisesRegex(manifest_tool.ManifestError, "no longer matches"):
            manifest_tool.build_manifest(
                self.root,
                results.relative_to(self.root).as_posix(),
                tested_source_bundle=bundle.relative_to(self.root).as_posix(),
                captured_at=FIXED_TIME,
                environment=FIXED_ENVIRONMENT,
            )

    def test_execution_receipt_detects_source_mutation_during_command(self) -> None:
        bundle = self._create_bundle()
        _, receipt = self._execute(bundle, mutating=True)
        value = json.loads(receipt.read_text(encoding="utf-8"))
        self.assertFalse(value["accepted"])

    def test_extraction_preflights_all_destinations_before_writing(self) -> None:
        bundle = self._create_bundle(source_paths=("src",))
        verified = manifest_tool.verify_source_bundle(self.root, bundle.relative_to(self.root).as_posix())
        output = self.root / "extracts" / "preflight"
        real_units = manifest_tool._utf16_units

        def units(value: str) -> int:
            if str(output) in value and value.endswith("z.cs"):
                return manifest_tool.MAX_WINDOWS_PATH_UNITS + 1
            return real_units(value)

        with mock.patch.object(manifest_tool, "verify_source_bundle", return_value=verified), mock.patch.object(
            manifest_tool, "_utf16_units", side_effect=units
        ):
            with self.assertRaisesRegex(manifest_tool.ManifestError, "path budget"):
                manifest_tool.extract_source_bundle(
                    self.root, bundle.relative_to(self.root).as_posix(), output
                )
        self.assertFalse(output.exists())

    def test_bundle_verification_rejects_change_during_stable_handle_read(self) -> None:
        bundle = self._create_bundle(source_paths=("src",))
        real_fstat = os.fstat
        calls = 0

        def changed_fstat(descriptor: int):
            nonlocal calls
            calls += 1
            value = real_fstat(descriptor)
            if calls == 2:
                values = list(value)
                values[8] += 1
                return os.stat_result(values)
            return value

        with mock.patch.object(manifest_tool.os, "fstat", side_effect=changed_fstat):
            with self.assertRaisesRegex(manifest_tool.ManifestError, "changed while"):
                manifest_tool.verify_source_bundle(self.root, bundle.relative_to(self.root).as_posix())

    def test_post_publish_inventory_failure_removes_owned_output(self) -> None:
        actual = manifest_tool.collect_source_inventory(self.root, ("src",))
        changed = copy.deepcopy(actual)
        changed["sha256"] = "b" * 64
        output = self.root / "bundles" / "unstable.zip"
        with mock.patch.object(
            manifest_tool,
            "collect_source_inventory",
            side_effect=[actual, actual, changed],
        ):
            with self.assertRaisesRegex(manifest_tool.ManifestError, "while the bundle was published"):
                manifest_tool.create_source_bundle(
                    self.root,
                    output,
                    source_paths=("src",),
                    captured_at=FIXED_TIME,
                )
        self.assertFalse(output.exists())

    def test_unknown_evidence_contract_and_raw_message_fields_are_rejected(self) -> None:
        bundle = self._create_bundle()
        _, receipt = self._execute(bundle)
        self.metrics_path.write_text(
            json.dumps({"contractId": "pegasus.cortex.unknown/1.0", "messages": ["raw output"]}),
            encoding="utf-8",
        )
        results = self._write_results(receipt)
        with self.assertRaisesRegex(manifest_tool.ManifestError, "unsupported fields|requires evidence contract"):
            manifest_tool.build_manifest(
                self.root,
                results.relative_to(self.root).as_posix(),
                tested_source_bundle=bundle.relative_to(self.root).as_posix(),
                captured_at=FIXED_TIME,
                environment=FIXED_ENVIRONMENT,
            )

    def test_embedded_uri_credentials_are_rejected(self) -> None:
        bundle = self._create_bundle()
        _, receipt = self._execute(bundle)
        results = self._write_results(
            receipt,
            argv_override=["tool", "--endpoint=https://user:password@example.test"],
        )
        with self.assertRaisesRegex(manifest_tool.ManifestError, "credential material"):
            manifest_tool.build_manifest(
                self.root,
                results.relative_to(self.root).as_posix(),
                tested_source_bundle=bundle.relative_to(self.root).as_posix(),
                captured_at=FIXED_TIME,
                environment=FIXED_ENVIRONMENT,
            )

    def test_windows_invalid_and_extended_reserved_names_are_rejected(self) -> None:
        for value in ("evidence/bad?.json", "evidence/COM¹.txt", "evidence/CONIN$.txt"):
            with self.subTest(value=value), self.assertRaises(manifest_tool.ManifestError):
                manifest_tool.resolve_project_path(self.root, value, must_exist=False)

    def test_noncanonical_bundle_metadata_is_rejected(self) -> None:
        bundle = self._create_bundle(source_paths=("src",))
        rewritten = self.root / "bundles" / "noncanonical.zip"
        with zipfile.ZipFile(bundle, "r") as source_archive, zipfile.ZipFile(
            rewritten, "w", compression=zipfile.ZIP_STORED
        ) as destination_archive:
            for info in source_archive.infolist():
                data = source_archive.read(info)
                if info.filename == manifest_tool.BUNDLE_METADATA_NAME:
                    data = json.dumps(json.loads(data), separators=(",", ":")).encode("utf-8")
                destination_archive.writestr(manifest_tool._bundle_zip_info(info.filename), data)
        with self.assertRaisesRegex(manifest_tool.ManifestError, "not canonically encoded"):
            manifest_tool.verify_source_bundle(self.root, rewritten.relative_to(self.root).as_posix())

    def test_deterministic_bundle_requires_explicit_captured_at(self) -> None:
        with self.assertRaisesRegex(manifest_tool.ManifestError, "captured-at is required"):
            manifest_tool.create_source_bundle(
                self.root,
                self.root / "bundles" / "missing-time.zip",
                source_paths=("src",),
                captured_at=None,  # type: ignore[arg-type]
            )
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                manifest_tool._parser().parse_args(
                    ["bundle", "create", "--root", str(self.root), "--output", "bundles/cli.zip"]
                )
            with self.assertRaises(SystemExit):
                manifest_tool._parser().parse_args(
                    [
                        "execute",
                        "--root",
                        str(self.root),
                        "--bundle",
                        "bundles/cli.zip",
                        "--plan",
                        "plans/cli.json",
                        "--extract-output",
                        "extracts/cli",
                        "--receipt-output",
                        "receipts/cli.json",
                    ]
                )
            with self.assertRaises(SystemExit):
                manifest_tool._parser().parse_args(
                    [
                        "create",
                        "--root",
                        str(self.root),
                        "--results",
                        "evidence/results.json",
                        "--output",
                        "manifests/cli.json",
                        "--tested-source-bundle",
                        "bundles/cli.zip",
                    ]
                )

    def test_bundle_verification_rejects_unsafe_entry(self) -> None:
        path = self.root / "bundles" / "unsafe.zip"
        with zipfile.ZipFile(path, mode="w", compression=zipfile.ZIP_STORED) as archive:
            archive.writestr(manifest_tool._bundle_zip_info(manifest_tool.BUNDLE_METADATA_NAME), b"{}")
            archive.writestr(manifest_tool._bundle_zip_info("../escape.txt"), b"escape")
        with self.assertRaises(manifest_tool.ManifestError):
            manifest_tool.verify_source_bundle(self.root, "bundles/unsafe.zip")

    def test_source_inventory_rejects_link_escape_when_supported(self) -> None:
        outside = pathlib.Path(self.temporary.name).parent / f"{self.root.name}-outside.txt"
        outside.write_text("outside\n", encoding="utf-8")
        link = self.root / "src" / "linked.txt"
        try:
            try:
                os.symlink(outside, link)
            except OSError:
                self.skipTest("symbolic-link creation is unavailable")
            with self.assertRaisesRegex(manifest_tool.ManifestError, "link or reparse point"):
                manifest_tool.collect_source_inventory(self.root, ("src",))
        finally:
            link.unlink(missing_ok=True)
            outside.unlink(missing_ok=True)

    def test_evidence_and_results_tampering_are_detected(self) -> None:
        manifest, _, results = self._build()
        path = self.root / "manifests" / "verification.json"
        manifest_tool.write_manifest(path, manifest)
        metrics = self._passing_metrics()
        metrics["retrievalRecall"] = {"hits": 18, "total": 20}
        self._write_metrics(metrics)
        with self.assertRaises(manifest_tool.ManifestError):
            manifest_tool.verify_manifest(self.root, path.relative_to(self.root).as_posix())
        self.assertTrue(results.exists())

    def test_cli_contract_exposes_execution_and_explicit_bundle_time(self) -> None:
        parser = manifest_tool._parser()
        bundle_args = parser.parse_args(
            [
                "bundle",
                "create",
                "--root",
                str(self.root),
                "--output",
                "bundles/cli.zip",
                "--captured-at",
                FIXED_TIME,
            ]
        )
        self.assertEqual(bundle_args.bundle_operation, "create")
        execute_args = parser.parse_args(
            [
                "execute",
                "--root",
                str(self.root),
                "--bundle",
                "bundles/cli.zip",
                "--plan",
                "plans/cli.json",
                "--extract-output",
                "extracts/cli",
                "--receipt-output",
                "receipts/cli.json",
                "--captured-at",
                FIXED_TIME,
            ]
        )
        self.assertEqual(execute_args.operation, "execute")


if __name__ == "__main__":
    unittest.main()
