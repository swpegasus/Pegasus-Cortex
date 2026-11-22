"""Standard-library contract tests for the isolated Heartbeat v2 producer."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

from heartbeat_v2.common import (
    CancellationRequested,
    ContractError,
    canonical_json_bytes,
    parse_utc,
)
from heartbeat_v2.heartbeat import (
    Lease,
    LeaseUnavailable,
    Observation,
    PilotConfig,
    STATUS_ATTENTION,
    STATUS_CHANGED,
    STATUS_FAILED,
    STATUS_UNCHANGED,
    default_state,
    probe_revision,
    run_pilot,
    seal_state,
    validate_state,
)


NOW = datetime(2026, 7, 30, 12, 0, 0, tzinfo=timezone.utc)


def config_value(*, mode: str = "report-only", state_writes: bool = False) -> dict:
    """Return a fresh, explicitly standalone configuration value."""

    return {
        "schemaVersion": 2,
        "heartbeatId": "test-heartbeat",
        "enabled": True,
        "mode": mode,
        "gates": {
            "allowStateWrites": state_writes,
            "allowProductMutation": False,
            "allowModelDispatch": False,
            "allowWorkerDispatch": False,
            "inheritParentTranscript": False,
        },
        "isolation": {
            "profile": "standalone-local",
            "allowEnvironmentConfiguration": False,
            "allowNetwork": False,
            "integratedAdapters": [],
        },
        "limits": {
            "maxAttempts": 3,
            "maxConcurrentRuns": 1,
            "leaseSeconds": 90,
            "coalesceWindowSeconds": 300,
            "maximumDeltas": 64,
            "maximumCoalescedEvents": 16,
            "maximumReportBytes": 8192,
        },
        "retry": {
            "baseSeconds": [60, 300, 900],
            "jitterMaxSeconds": [0, 0, 0],
        },
    }


def config(*, mode: str = "report-only", state_writes: bool = False) -> PilotConfig:
    return PilotConfig.from_dict(config_value(mode=mode, state_writes=state_writes))


def observation(
    pilot_config: PilotConfig,
    *,
    cursor: str = "cursor-1",
    base_cursor: str | None = None,
    deltas: list[dict[str, object]] | None = None,
    attention: list[str] | None = None,
    declared_revision: str | None = None,
    probe_failure: str | None = None,
) -> Observation:
    value: dict[str, object] = {
        "schemaVersion": 2,
        "source": "test-source",
        "cursor": cursor,
        "baseCursor": base_cursor,
        "revisionInputs": {"registry": "r1", "tasks": {"cortex": "r2"}},
        "deltas": deltas or [],
        "attentionReasons": attention or [],
    }
    if declared_revision is not None:
        value["declaredRevision"] = declared_revision
    if probe_failure is not None:
        value["probeFailure"] = probe_failure
    return Observation.from_dict(value, pilot_config)


def read_state(path: Path, heartbeat_id: str) -> dict:
    value = json.loads(path.read_text(encoding="utf-8"))
    return validate_state(value, heartbeat_id)


class RevisionProbeTests(unittest.TestCase):
    def test_checked_in_config_is_disabled_and_all_schemas_are_json_objects(self) -> None:
        integration_root = Path(__file__).resolve().parents[1]
        checked_in = json.loads(
            (integration_root / "config.pilot.json").read_text(encoding="utf-8")
        )
        parsed = PilotConfig.from_dict(checked_in)
        self.assertFalse(parsed.enabled)
        self.assertEqual("standalone-local", parsed.profile)
        for schema_path in sorted((integration_root / "schemas").glob("*.json")):
            with self.subTest(schema=schema_path.name):
                schema = json.loads(schema_path.read_text(encoding="utf-8"))
                self.assertIsInstance(schema, dict)
                self.assertIn("$schema", schema)

    def test_unchanged_is_under_512_bytes_and_has_zero_activity(self) -> None:
        pilot_config = config()
        observed = observation(pilot_config)
        state = default_state(pilot_config.heartbeat_id)
        state["lastRevision"] = observed.revision()
        state["lastCursor"] = observed.cursor
        state = seal_state(state)

        with tempfile.TemporaryDirectory() as directory:
            state_root = Path(directory)
            state_path = state_root / "state.json"
            state_path.write_bytes(canonical_json_bytes(state) + b"\n")
            before = state_path.read_bytes()
            result = run_pilot(
                pilot_config,
                observed,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=NOW,
            )
            after = state_path.read_bytes()
            self.assertFalse(Path(f"{state_path}.lease").exists())

        self.assertEqual(STATUS_UNCHANGED, result["status"])
        for field in (
            "modelDispatched",
            "workerDispatched",
            "knowledgeWritten",
            "leaseAcquired",
            "stateWritten",
            "parentTranscriptInherited",
        ):
            self.assertFalse(result[field], field)
        self.assertLessEqual(len(canonical_json_bytes(result)), 512)
        self.assertEqual(before, after)

    def test_disabled_short_circuits_before_path_or_state_access(self) -> None:
        pilot_config = config()
        result = run_pilot(
            pilot_config,
            observation(pilot_config),
            Path("unapproved/and/missing/state.json"),
            runtime_enable=False,
            now=NOW,
        )
        self.assertEqual(STATUS_UNCHANGED, result["status"])
        self.assertEqual("pilot_disabled", result["reason"])
        self.assertFalse(result["leaseAcquired"])
        self.assertFalse(result["stateWritten"])

    def test_changed_deltas_are_deduplicated_and_coalesced(self) -> None:
        pilot_config = config()
        deltas = [
            {
                "dedupeKey": "task:cortex",
                "category": "task",
                "scope": "cortex",
                "severity": "info",
                "summary": "First bounded update",
                "occurredAtUtc": "2026-07-30T12:00:00Z",
            },
            {
                "dedupeKey": "task:cortex",
                "category": "task",
                "scope": "cortex",
                "severity": "warning",
                "summary": "Latest bounded update",
                "occurredAtUtc": "2026-07-30T12:01:00Z",
            },
        ]
        result = probe_revision(
            observation(pilot_config, deltas=deltas),
            default_state(pilot_config.heartbeat_id),
            pilot_config,
        )
        self.assertEqual(STATUS_CHANGED, result.status)
        self.assertEqual(1, len(result.events))
        self.assertEqual(2, result.events[0]["count"])
        self.assertEqual("warning", result.events[0]["severity"])

    def test_cursor_gap_requires_attention(self) -> None:
        pilot_config = config()
        state = default_state(pilot_config.heartbeat_id)
        state["lastCursor"] = "cursor-1"
        result = probe_revision(
            observation(pilot_config, cursor="cursor-3", base_cursor="cursor-2"),
            state,
            pilot_config,
        )
        self.assertEqual(STATUS_ATTENTION, result.status)
        self.assertEqual("cursor_gap", result.reason)

    def test_declared_revision_mismatch_fails_closed(self) -> None:
        pilot_config = config()
        result = probe_revision(
            observation(pilot_config, declared_revision="sha256:" + ("0" * 64)),
            default_state(pilot_config.heartbeat_id),
            pilot_config,
        )
        self.assertEqual(STATUS_FAILED, result.status)
        self.assertTrue(result.failure)

    def test_isolation_policy_rejects_transcript_network_environment_and_adapters(self) -> None:
        mutations = (
            ("parent transcript", ("gates", "inheritParentTranscript"), True),
            ("model dispatch", ("gates", "allowModelDispatch"), True),
            ("worker dispatch", ("gates", "allowWorkerDispatch"), True),
            ("environment config", ("isolation", "allowEnvironmentConfiguration"), True),
            ("network", ("isolation", "allowNetwork"), True),
            ("integrated adapter", ("isolation", "integratedAdapters"), ["hybrid"]),
            ("hybrid profile", ("isolation", "profile"), "hybrid"),
        )
        for label, (section, field), replacement in mutations:
            with self.subTest(label=label):
                value = config_value(mode="pilot", state_writes=True)
                value[section][field] = replacement
                with self.assertRaises(ContractError):
                    PilotConfig.from_dict(value)

    def test_poisoned_environment_cannot_create_implicit_hybrid_mode(self) -> None:
        pilot_config = config()
        observed = observation(pilot_config)
        clean = run_pilot(
            pilot_config,
            observed,
            None,
            runtime_enable=True,
            now=NOW,
        )
        poisoned = {
            "PEGASUS_CORTEX_RUNTIME_CONNECTION": "https://live.invalid",
            "PEGASUS_CORTEX_AUTHORITY_FILE": "C:\\poison\\authority.json",
            "PEGASUS_CORTEX_ROW_KEY_FILE": "C:\\poison\\row.key",
            "PEGASUS_CORTEX_MIGRATION_CONNECTION": "https://migration.invalid",
            "PEGASUS_CORTEX_TLS_POLICY": "insecure",
            "HTTP_PROXY": "http://proxy.invalid",
            "HTTPS_PROXY": "http://proxy.invalid",
        }
        with patch.dict(os.environ, poisoned, clear=False):
            with patch(
                "socket.socket",
                side_effect=AssertionError("standalone producer attempted network access"),
            ):
                isolated = run_pilot(
                    pilot_config,
                    observed,
                    None,
                    runtime_enable=True,
                    now=NOW,
                )

        self.assertEqual(canonical_json_bytes(clean), canonical_json_bytes(isolated))
        self.assertEqual(
            "validated-recovery-probe-brief-only",
            isolated["contextPolicy"],
        )
        self.assertFalse(isolated["modelDispatched"])
        self.assertFalse(isolated["workerDispatched"])
        self.assertFalse(isolated["parentTranscriptInherited"])


class StatefulPilotTests(unittest.TestCase):
    def test_stateful_changed_run_is_idempotent_and_second_run_is_no_write(self) -> None:
        pilot_config = config(mode="pilot", state_writes=True)
        observed = observation(pilot_config)
        with tempfile.TemporaryDirectory() as directory:
            state_root = Path(directory)
            state_path = state_root / "state.json"
            first = run_pilot(
                pilot_config,
                observed,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=NOW,
            )
            after_first = state_path.read_bytes()
            saved = read_state(state_path, pilot_config.heartbeat_id)
            second = run_pilot(
                pilot_config,
                observed,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=NOW,
            )
            after_second = state_path.read_bytes()
            lease_exists = Path(f"{state_path}.lease").exists()

        self.assertEqual(STATUS_CHANGED, first["status"])
        self.assertTrue(first["stateWritten"])
        self.assertTrue(first["leaseAcquired"])
        self.assertEqual(1, len(saved["completedIdempotencyKeys"]))
        self.assertTrue(saved["completedIdempotencyKeys"][0].startswith("sha256:"))
        self.assertEqual(STATUS_UNCHANGED, second["status"])
        self.assertFalse(second["leaseAcquired"])
        self.assertEqual(after_first, after_second)
        self.assertFalse(lease_exists)

    def test_retry_schedule_is_1_5_15_and_third_failure_latches_attention(self) -> None:
        pilot_config = config(mode="pilot", state_writes=True)
        failed_observation = observation(
            pilot_config,
            probe_failure="bounded source timeout",
        )
        with tempfile.TemporaryDirectory() as directory:
            state_root = Path(directory)
            state_path = state_root / "state.json"

            first = run_pilot(
                pilot_config,
                failed_observation,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=NOW,
            )
            state_one = read_state(state_path, pilot_config.heartbeat_id)
            first_due = parse_utc(
                state_one["retry"]["nextAttemptAtUtc"],
                "first retry",
            )
            delay_one = int((first_due - NOW).total_seconds())
            before_retry = state_path.read_bytes()
            early = run_pilot(
                pilot_config,
                failed_observation,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=first_due - timedelta(seconds=1),
            )
            self.assertEqual(before_retry, state_path.read_bytes())

            second = run_pilot(
                pilot_config,
                failed_observation,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=first_due,
            )
            state_two = read_state(state_path, pilot_config.heartbeat_id)
            second_due = parse_utc(
                state_two["retry"]["nextAttemptAtUtc"],
                "second retry",
            )
            delay_two = int((second_due - first_due).total_seconds())

            third = run_pilot(
                pilot_config,
                failed_observation,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=second_due,
            )
            state_three = read_state(state_path, pilot_config.heartbeat_id)
            third_due = parse_utc(
                state_three["retry"]["nextAttemptAtUtc"],
                "attention recheck",
            )
            delay_three = int((third_due - second_due).total_seconds())
            latched_bytes = state_path.read_bytes()
            latched = run_pilot(
                pilot_config,
                failed_observation,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=third_due,
            )
            self.assertEqual(latched_bytes, state_path.read_bytes())

        self.assertEqual(STATUS_FAILED, first["status"])
        self.assertEqual(STATUS_UNCHANGED, early["status"])
        self.assertEqual("retry_not_due", early["reason"])
        self.assertFalse(early["leaseAcquired"])
        self.assertFalse(early["stateWritten"])
        self.assertEqual(STATUS_FAILED, second["status"])
        self.assertEqual(STATUS_ATTENTION, third["status"])
        self.assertEqual("failure_limit_reached", third["reason"])
        self.assertEqual(STATUS_ATTENTION, latched["status"])
        self.assertEqual("failure_attention_latched", latched["reason"])
        self.assertFalse(latched["leaseAcquired"])
        self.assertFalse(latched["stateWritten"])
        self.assertEqual(60, delay_one)
        self.assertEqual(300, delay_two)
        self.assertEqual(900, delay_three)
        self.assertEqual(3, state_three["retry"]["attempt"])
        self.assertTrue(state_three["attentionLatched"])
        self.assertEqual(3, state_three["lastReceipt"]["consecutiveFailures"])

    def test_successful_probe_resets_failure_streak(self) -> None:
        pilot_config = config(mode="pilot", state_writes=True)
        failed_observation = observation(pilot_config, probe_failure="temporary")
        healthy_observation = observation(pilot_config)
        with tempfile.TemporaryDirectory() as directory:
            state_root = Path(directory)
            state_path = state_root / "state.json"
            run_pilot(
                pilot_config,
                failed_observation,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=NOW,
            )
            failed_state = read_state(state_path, pilot_config.heartbeat_id)
            retry_at = parse_utc(
                failed_state["retry"]["nextAttemptAtUtc"],
                "retry timestamp",
            )
            result = run_pilot(
                pilot_config,
                healthy_observation,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=retry_at,
            )
            recovered = read_state(state_path, pilot_config.heartbeat_id)

        self.assertEqual(STATUS_CHANGED, result["status"])
        self.assertEqual(0, recovered["retry"]["attempt"])
        self.assertIsNone(recovered["retry"]["nextAttemptAtUtc"])
        self.assertFalse(recovered["attentionLatched"])

    def test_lease_identity_is_stable_for_same_idempotency_boundary(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            state_path = Path(directory) / "state.json"
            with Lease(
                state_path,
                "test-heartbeat",
                "sha256:" + ("a" * 64),
                90,
                NOW,
            ):
                first = json.loads(
                    (Path(f"{state_path}.lease") / "lease.json").read_text(
                        encoding="utf-8"
                    )
                )
            with Lease(
                state_path,
                "test-heartbeat",
                "sha256:" + ("a" * 64),
                90,
                NOW + timedelta(seconds=1),
            ):
                second = json.loads(
                    (Path(f"{state_path}.lease") / "lease.json").read_text(
                        encoding="utf-8"
                    )
                )

        self.assertEqual(first["leaseId"], second["leaseId"])
        self.assertNotEqual(first["ownerId"], second["ownerId"])
        self.assertEqual(first["idempotencyKey"], second["idempotencyKey"])
        self.assertTrue(first["leaseHash"].startswith("sha256:"))

    def test_expired_lease_reclaim_is_owner_hash_safe(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            state_path = Path(directory) / "state.json"
            first = Lease(
                state_path,
                "test-heartbeat",
                "sha256:" + ("b" * 64),
                90,
                NOW,
            )
            second = Lease(
                state_path,
                "test-heartbeat",
                "sha256:" + ("c" * 64),
                90,
                NOW + timedelta(seconds=91),
            )
            second_metadata_path = Path(f"{state_path}.lease") / "lease.json"
            second_metadata = second_metadata_path.read_bytes()

            # The expired owner's late cleanup must not remove its replacement.
            first.release()
            self.assertTrue(second_metadata_path.exists())
            self.assertEqual(second_metadata, second_metadata_path.read_bytes())
            second.release()
            self.assertFalse(Path(f"{state_path}.lease").exists())

    def test_concurrent_expired_reclaim_has_one_winner(self) -> None:
        # Repetition makes the Windows overlapping-directory-rename race
        # observable without relying on one scheduler interleaving.
        for _ in range(25):
            with tempfile.TemporaryDirectory() as directory:
                state_path = Path(directory) / "state.json"
                expired = Lease(
                    state_path,
                    "test-heartbeat",
                    "sha256:" + ("d" * 64),
                    90,
                    NOW,
                )
                barrier = threading.Barrier(2)

                def acquire(suffix: str) -> Lease | Exception:
                    barrier.wait()
                    try:
                        return Lease(
                            state_path,
                            "test-heartbeat",
                            "sha256:" + (suffix * 64),
                            90,
                            NOW + timedelta(seconds=91),
                        )
                    except Exception as exception:
                        return exception

                with ThreadPoolExecutor(max_workers=2) as executor:
                    outcomes = tuple(executor.map(acquire, ("e", "f")))
                winners = tuple(item for item in outcomes if isinstance(item, Lease))
                failures = tuple(
                    item for item in outcomes if isinstance(item, LeaseUnavailable)
                )
                self.assertEqual(1, len(winners))
                self.assertEqual(1, len(failures))
                self.assertTrue(Path(f"{state_path}.lease.guard").is_file())
                expired.release()
                winners[0].release()
                self.assertFalse(Path(f"{state_path}.lease").exists())

    def test_reparse_mutation_guard_fails_closed_when_supported(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            state_path = Path(directory) / "state.json"
            target = Path(directory) / "guard-target"
            target.write_bytes(b"unchanged")
            guard = Path(f"{state_path}.lease.guard")
            try:
                guard.symlink_to(target)
            except (OSError, NotImplementedError):
                self.skipTest("local symlink creation is unavailable")

            with self.assertRaisesRegex(LeaseUnavailable, "mutation guard"):
                Lease(
                    state_path,
                    "test-heartbeat",
                    "sha256:" + ("9" * 64),
                    90,
                    NOW,
                )

            self.assertEqual(b"unchanged", target.read_bytes())
            self.assertFalse(Path(f"{state_path}.lease").exists())

    def test_concurrent_process_reclaim_has_one_winner(self) -> None:
        worker = """
from datetime import datetime, timezone
from pathlib import Path
import sys
import time
from heartbeat_v2.heartbeat import Lease, LeaseUnavailable

state_path = Path(sys.argv[1])
suffix = sys.argv[2]
ready_path = Path(sys.argv[3])
start_path = Path(sys.argv[4])
result_path = Path(sys.argv[5])
release_path = Path(sys.argv[6])
ready_path.write_text("ready", encoding="utf-8")
deadline = time.monotonic() + 10.0
while not start_path.exists():
    if time.monotonic() >= deadline:
        raise SystemExit(2)
    time.sleep(0.005)
try:
    lease = Lease(
        state_path,
        "test-heartbeat",
        "sha256:" + (suffix * 64),
        90,
        datetime(2026, 7, 30, 12, 1, 31, tzinfo=timezone.utc),
    )
except LeaseUnavailable:
    result_path.write_text("unavailable", encoding="utf-8")
    raise SystemExit(0)
result_path.write_text("winner", encoding="utf-8")
while not release_path.exists():
    if time.monotonic() >= deadline:
        lease.release()
        raise SystemExit(3)
    time.sleep(0.005)
lease.release()
"""
        for iteration in range(3):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                state_path = root / "state.json"
                expired = Lease(
                    state_path,
                    "test-heartbeat",
                    "sha256:" + ("a" * 64),
                    90,
                    NOW,
                )
                start = root / "start"
                release = root / "release"
                ready = (root / "ready-0", root / "ready-1")
                results = (root / "result-0", root / "result-1")
                processes = tuple(
                    subprocess.Popen(
                        [
                            sys.executable,
                            "-c",
                            worker,
                            str(state_path),
                            suffix,
                            str(ready[index]),
                            str(start),
                            str(results[index]),
                            str(release),
                        ],
                        stdout=subprocess.PIPE,
                        stderr=subprocess.PIPE,
                        text=True,
                    )
                    for index, suffix in enumerate(("b", "c"))
                )
                deadline = time.monotonic() + 10.0
                while not all(path.exists() for path in ready):
                    if time.monotonic() >= deadline:
                        self.fail(f"process reclaim workers did not become ready: {iteration}")
                    time.sleep(0.005)
                start.write_text("start", encoding="utf-8")
                while not all(path.exists() for path in results):
                    if time.monotonic() >= deadline:
                        self.fail(f"process reclaim workers did not report: {iteration}")
                    time.sleep(0.005)
                self.assertEqual(
                    ["unavailable", "winner"],
                    sorted(path.read_text(encoding="utf-8") for path in results),
                )
                release.write_text("release", encoding="utf-8")
                diagnostics = tuple(process.communicate(timeout=10) for process in processes)
                for process, (stdout, stderr) in zip(processes, diagnostics):
                    self.assertEqual(0, process.returncode, (stdout, stderr))
                expired.release()
                self.assertFalse(Path(f"{state_path}.lease").exists())

    def test_unexpired_malformed_and_metadata_missing_leases_fail_closed(self) -> None:
        pilot_config = config(mode="pilot", state_writes=True)
        observed = observation(pilot_config)
        with tempfile.TemporaryDirectory() as directory:
            state_root = Path(directory)
            state_path = state_root / "state.json"
            active = Lease(
                state_path,
                pilot_config.heartbeat_id,
                "sha256:" + ("1" * 64),
                90,
                NOW,
            )
            active_result = run_pilot(
                pilot_config,
                observed,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=NOW + timedelta(seconds=1),
            )
            self.assertEqual("lease_unavailable", active_result["reason"])
            active.release()

            lease_directory = Path(f"{state_path}.lease")
            lease_directory.mkdir()
            (lease_directory / "lease.json").write_text(
                '{"schemaVersion":2}',
                encoding="utf-8",
            )
            malformed_result = run_pilot(
                pilot_config,
                observed,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=NOW + timedelta(hours=1),
            )
            self.assertEqual("lease_unavailable", malformed_result["reason"])
            (lease_directory / "lease.json").unlink()
            lease_directory.rmdir()

            lease_directory.mkdir()
            missing_result = run_pilot(
                pilot_config,
                observed,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=NOW + timedelta(hours=2),
            )
            self.assertEqual("lease_unavailable", missing_result["reason"])
            self.assertTrue(lease_directory.exists())
            self.assertFalse(state_path.exists())

    def test_crash_before_staged_metadata_never_publishes_live_lease(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            state_path = Path(directory) / "state.json"
            with patch(
                "heartbeat_v2.heartbeat.atomic_write_json",
                side_effect=OSError("simulated metadata write crash"),
            ):
                with self.assertRaises(OSError):
                    Lease(
                        state_path,
                        "test-heartbeat",
                        "sha256:" + ("2" * 64),
                        90,
                        NOW,
                    )

            self.assertFalse(Path(f"{state_path}.lease").exists())
            self.assertEqual(
                [],
                list(Path(directory).glob("state.json.lease.stage-*")),
            )

    def test_existing_lease_fails_closed_without_state_creation(self) -> None:
        pilot_config = config(mode="pilot", state_writes=True)
        observed = observation(pilot_config)
        with tempfile.TemporaryDirectory() as directory:
            state_root = Path(directory)
            state_path = state_root / "state.json"
            Path(f"{state_path}.lease").mkdir()
            result = run_pilot(
                pilot_config,
                observed,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=NOW,
            )
            state_exists = state_path.exists()

        self.assertEqual(STATUS_ATTENTION, result["status"])
        self.assertEqual("lease_unavailable", result["reason"])
        self.assertFalse(result["leaseAcquired"])
        self.assertFalse(result["stateWritten"])
        self.assertFalse(state_exists)

    def test_retry_race_after_lease_never_reports_unchanged(self) -> None:
        pilot_config = config(mode="pilot", state_writes=True)
        observed = observation(pilot_config)
        initial = default_state(pilot_config.heartbeat_id)
        racing = default_state(pilot_config.heartbeat_id)
        racing["retry"] = {
            "attempt": 1,
            "nextAttemptAtUtc": "2026-07-30T12:01:00Z",
        }
        racing = seal_state(racing)
        with tempfile.TemporaryDirectory() as directory:
            state_root = Path(directory)
            state_path = state_root / "state.json"
            with patch(
                "heartbeat_v2.heartbeat.load_state",
                side_effect=[initial, racing],
            ):
                result = run_pilot(
                    pilot_config,
                    observed,
                    state_path,
                    state_root=state_root,
                    runtime_enable=True,
                    now=NOW,
                )

        self.assertEqual(STATUS_ATTENTION, result["status"])
        self.assertEqual("retry_not_due_after_lease", result["reason"])
        self.assertTrue(result["leaseAcquired"])
        self.assertFalse(result["stateWritten"])

    def test_oversized_max_event_report_does_not_advance_state(self) -> None:
        value = config_value(mode="pilot", state_writes=True)
        value["limits"]["maximumReportBytes"] = 512
        small_report_config = PilotConfig.from_dict(value)
        deltas = [
            {
                "dedupeKey": f"task:{index:02d}",
                "category": "task",
                "scope": f"scope-{index:02d}",
                "severity": "warning",
                "summary": f"Bounded event {index:02d} " + ("x" * 170),
                "occurredAtUtc": f"2026-07-30T12:00:{index:02d}Z",
            }
            for index in range(16)
        ]
        observed = observation(small_report_config, deltas=deltas)
        with tempfile.TemporaryDirectory() as directory:
            state_root = Path(directory)
            state_path = state_root / "state.json"
            with self.assertRaisesRegex(
                ContractError,
                "report exceeds maximumReportBytes",
            ):
                run_pilot(
                    small_report_config,
                    observed,
                    state_path,
                    state_root=state_root,
                    runtime_enable=True,
                    now=NOW,
                )
            self.assertFalse(state_path.exists())
            self.assertFalse(Path(f"{state_path}.lease").exists())

            large_report_config = config(mode="pilot", state_writes=True)
            retry = run_pilot(
                large_report_config,
                observation(large_report_config, deltas=deltas),
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=NOW,
            )
            saved = read_state(state_path, large_report_config.heartbeat_id)

        self.assertEqual(STATUS_CHANGED, retry["status"])
        self.assertEqual(16, len(retry["events"]))
        self.assertEqual(16, saved["metrics"]["eventsEmitted"])

    def test_state_hash_detects_unsealed_content_change(self) -> None:
        pilot_config = config(mode="pilot", state_writes=True)
        observed = observation(pilot_config)
        with tempfile.TemporaryDirectory() as directory:
            state_root = Path(directory)
            state_path = state_root / "state.json"
            run_pilot(
                pilot_config,
                observed,
                state_path,
                state_root=state_root,
                runtime_enable=True,
                now=NOW,
            )
            corrupted = json.loads(state_path.read_text(encoding="utf-8"))
            corrupted["metrics"]["runs"] += 1
            state_path.write_bytes(canonical_json_bytes(corrupted) + b"\n")
            with self.assertRaises(ContractError):
                run_pilot(
                    pilot_config,
                    observed,
                    state_path,
                    state_root=state_root,
                    runtime_enable=True,
                    now=NOW,
                )

    def test_state_path_must_be_direct_child_of_explicit_root(self) -> None:
        pilot_config = config(mode="pilot", state_writes=True)
        with tempfile.TemporaryDirectory() as directory:
            state_root = Path(directory)
            nested = state_root / "nested"
            nested.mkdir()
            with self.assertRaises(ContractError):
                run_pilot(
                    pilot_config,
                    observation(pilot_config),
                    nested / "state.json",
                    state_root=state_root,
                    runtime_enable=True,
                    now=NOW,
                )

    def test_cancellation_prevents_state_creation(self) -> None:
        pilot_config = config(mode="pilot", state_writes=True)
        observed = observation(pilot_config)
        with tempfile.TemporaryDirectory() as directory:
            state_root = Path(directory)
            state_path = state_root / "state.json"
            with self.assertRaises(CancellationRequested):
                run_pilot(
                    pilot_config,
                    observed,
                    state_path,
                    state_root=state_root,
                    runtime_enable=True,
                    now=NOW,
                    cancellation_requested=lambda: True,
                )
            self.assertFalse(state_path.exists())


if __name__ == "__main__":
    unittest.main()
