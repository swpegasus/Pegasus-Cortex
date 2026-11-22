"""Contract tests for the sanitized local benchmark harness."""

from __future__ import annotations

import copy
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from heartbeat_v2.benchmark import (
    REPORT_SCHEMA,
    _run_heartbeat_trials,
    _sanitize_context_text,
    validate_sanitized_report,
)
from heartbeat_v2.common import ContractError


class BenchmarkHarnessTests(unittest.TestCase):
    def test_fixed_heartbeat_trials_prove_ten_zero_activity_unchanged_cycles(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            with patch(
                "socket.socket",
                side_effect=AssertionError("benchmark attempted network access"),
            ):
                result = _run_heartbeat_trials(Path(directory) / "heartbeat")

        self.assertEqual(20, result["cycleCount"])
        self.assertEqual(10, result["statusCounts"]["changed"])
        self.assertEqual(10, result["statusCounts"]["unchanged"])
        self.assertEqual(0, result["activityCounts"]["modelDispatches"])
        self.assertEqual(0, result["activityCounts"]["workerDispatches"])
        self.assertEqual(0, result["activityCounts"]["knowledgeWrites"])
        self.assertTrue(
            result["unchangedContract"][
                "zeroModelWorkerLeaseStateKnowledgeAndTranscriptActivity"
            ]
        )
        self.assertLessEqual(
            result["unchangedContract"]["maximumPayloadBytes"],
            512,
        )

    def test_context_sanitizer_removes_paths_task_ids_and_prompt_references(self) -> None:
        sanitized = _sanitize_context_text(
            "Open `D:\\Private Project\\secret.md`; task f88f03ff791f using "
            "START_SECRET_AGENT.md.",
            "fixture",
            240,
        )
        self.assertNotIn("D:\\", sanitized)
        self.assertNotIn("f88f03ff791f", sanitized)
        self.assertNotIn("START_SECRET_AGENT.md", sanitized)
        self.assertIn("<local-path>", sanitized)
        self.assertIn("<id>", sanitized)
        self.assertIn("<prompt-ref>", sanitized)

    def test_committed_aggregate_report_is_sanitized_and_complete(self) -> None:
        report_path = Path(__file__).resolve().parents[1] / "benchmark-report.json"
        report = json.loads(report_path.read_text(encoding="utf-8"))
        validate_sanitized_report(report)

        self.assertEqual(REPORT_SCHEMA, report["schema"])
        self.assertEqual(20, report["heartbeat"]["cycleCount"])
        self.assertEqual(20, report["recovery"]["trialCount"])
        self.assertEqual(0, report["recovery"]["criteria"]["task"]["notPassed"])
        self.assertEqual(
            10,
            report["recovery"]["criteria"]["checkpoint"]["notMeasurable"],
        )
        self.assertTrue(report["safety"]["targetAuthorityReadOnly"])

    def test_report_validator_rejects_absolute_paths_and_digest_values(self) -> None:
        report_path = Path(__file__).resolve().parents[1] / "benchmark-report.json"
        report = json.loads(report_path.read_text(encoding="utf-8"))
        path_leak = copy.deepcopy(report)
        path_leak["limitations"].append("D:\\private\\file.txt")
        with self.assertRaises(ContractError):
            validate_sanitized_report(path_leak)

        digest_leak = copy.deepcopy(report)
        digest_leak["limitations"].append("sha256:" + ("a" * 64))
        with self.assertRaises(ContractError):
            validate_sanitized_report(digest_leak)


if __name__ == "__main__":
    unittest.main()
