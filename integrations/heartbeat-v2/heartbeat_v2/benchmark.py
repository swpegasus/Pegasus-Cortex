"""Reproducible, sanitized local pilot and recovery benchmark harness.

The harness has no network, model, worker, knowledge, service, or automation
adapter. Target projects are opened read-only. Heartbeat state and bounded
brief fixtures live only in one automatically removed temporary directory.
"""

from __future__ import annotations

import argparse
import json
import re
import statistics
import sys
import tempfile
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

from .common import (
    ContractError,
    atomic_write_json,
    canonical_json_bytes,
    normalize_public_text,
    require_string,
    sha256_tag,
)
from .heartbeat import (
    Observation,
    PilotConfig,
    STATUS_CHANGED,
    STATUS_UNCHANGED,
    run_pilot,
)
from .recovery_probe_brief import (
    MAXIMUM_RECOVERY_PROBE_BRIEF_BYTES,
    RECOVERY_ORDER,
    RecoveryProbeContext,
    read_recovery_context,
)


REPORT_SCHEMA = "pegasus.cortex.heartbeat-v2-benchmark-report/1"
BENCHMARK_BRIEF_SCHEMA = "pegasus.cortex.benchmark-recovery-brief/1"
HEARTBEAT_PAIR_COUNT = 10
RECOVERY_TRIALS_PER_PROJECT = 10
MAXIMUM_REPORT_BYTES = 16_384
_FIXED_START = datetime(2026, 7, 30, 12, 0, 0, tzinfo=timezone.utc)
_REPORT_FIELDS = {
    "schema",
    "profile",
    "heartbeat",
    "recovery",
    "safety",
    "limitations",
}
_BRIEF_FIELDS = {
    "schema",
    "projectAlias",
    "rootId",
    "currentTask",
    "gate",
    "nextAction",
    "checkpoint",
    "recoveryOrderPrefix",
    "sourceHashes",
    "sourcePayloadBytes",
    "authority",
}
_WINDOWS_ABSOLUTE = re.compile(
    r"(?i)(?:[a-z]:[\\/]|\\\\[^\\/\s]+[\\/])[^`\"'<>\r\n,;|)]*"
)
_POSIX_HOME = re.compile(r"(?i)/(?:home|users)/[^/\s]+(?:/[^\s`\"'<>]*)?")
_TASK_IDENTIFIER = re.compile(
    r"(?i)\b(?:[0-9a-f]{8,64}|[A-Z]{2,}-[0-9]{8}-[0-9]{1,6})\b"
)
_PROMPT_REFERENCE = re.compile(r"(?i)\b[A-Z][A-Z0-9_-]*AGENT[A-Z0-9_.-]*\.md\b")
_SHA256_VALUE = re.compile(r"^sha256:[0-9a-f]{64}$")


def run_benchmark(
    pegasus_nexus_root: Path,
    neural_commander_root: Path,
) -> dict[str, Any]:
    """Run the fixed 20-cycle/20-recovery-trial benchmark."""

    targets = (
        ("pegasus-nexus", pegasus_nexus_root),
        ("neural-commander", neural_commander_root),
    )
    with tempfile.TemporaryDirectory(prefix="heartbeat-v2-benchmark-") as directory:
        temporary_root = Path(directory)
        heartbeat = _run_heartbeat_trials(temporary_root / "heartbeat")
        recovery_projects = [
            _run_recovery_trials(
                alias,
                root,
                temporary_root / "recovery" / alias,
            )
            for alias, root in targets
        ]

    report = {
        "schema": REPORT_SCHEMA,
        "profile": {
            "name": "standalone-local",
            "deterministicFixture": "paired-revision-sequence-v1",
            "heartbeatCycles": HEARTBEAT_PAIR_COUNT * 2,
            "recoveryTrials": RECOVERY_TRIALS_PER_PROJECT * len(targets),
        },
        "heartbeat": heartbeat,
        "recovery": {
            "trialCount": sum(project["trialCount"] for project in recovery_projects),
            "projects": recovery_projects,
            "criteria": _merge_criteria(recovery_projects),
        },
        "safety": {
            "temporaryStateAndBriefStorageOnly": True,
            "targetAuthorityReadOnly": all(
                project["authorityStable"] for project in recovery_projects
            ),
            "modelDispatches": heartbeat["activityCounts"]["modelDispatches"],
            "workerDispatches": heartbeat["activityCounts"]["workerDispatches"],
            "knowledgeWrites": heartbeat["activityCounts"]["knowledgeWrites"],
            "networkCalls": 0,
            "serviceCalls": 0,
            "automationMutations": 0,
            "targetProjectWrites": 0,
        },
        "limitations": [
            "logical-cold-starts-only-os-cache-uncontrolled",
            "brief-generation-preparation-excluded-from-recovery-timing",
            "common-cortex-authority-prefix-only",
            "task-identifiers-intentionally-excluded",
            "checkpoint-requires-explicit-stable-checkpoint-section",
            "authority-stability-covers-read-source-set-not-entire-project",
            "single-windows-host-no-arm64-or-live-integration",
        ],
    }
    validate_sanitized_report(report)
    return report


def _run_heartbeat_trials(temporary_root: Path) -> dict[str, Any]:
    temporary_root.mkdir(parents=True, exist_ok=False)
    state_path = temporary_root / "state.json"
    config = _benchmark_config()
    elapsed: list[int] = []
    payload_bytes: list[int] = []
    statuses: list[str] = []
    responses: list[dict[str, Any]] = []

    previous_cursor: str | None = None
    for pair_index in range(HEARTBEAT_PAIR_COUNT):
        cursor = f"cursor-{pair_index:02d}"
        observation = Observation.from_dict(
            {
                "schemaVersion": 2,
                "source": "deterministic-local-fixture",
                "cursor": cursor,
                "baseCursor": previous_cursor,
                "revisionInputs": {
                    "fixture": "paired-revision-sequence-v1",
                    "sequence": pair_index,
                },
                "deltas": [],
                "attentionReasons": [],
            },
            config,
        )
        for repetition in range(2):
            now = _FIXED_START + timedelta(seconds=(pair_index * 2) + repetition)
            started = time.perf_counter_ns()
            response = run_pilot(
                config,
                observation,
                state_path,
                state_root=temporary_root,
                runtime_enable=True,
                now=now,
            )
            elapsed.append(time.perf_counter_ns() - started)
            responses.append(response)
            statuses.append(response["status"])
            payload_bytes.append(len(canonical_json_bytes(response)))
        previous_cursor = cursor

    expected = [
        status
        for _ in range(HEARTBEAT_PAIR_COUNT)
        for status in (STATUS_CHANGED, STATUS_UNCHANGED)
    ]
    if statuses != expected:
        raise ContractError("heartbeat benchmark did not produce the fixed paired status sequence")

    unchanged = [
        response for response in responses if response["status"] == STATUS_UNCHANGED
    ]
    zero_activity_fields = (
        "modelDispatched",
        "workerDispatched",
        "leaseAcquired",
        "stateWritten",
        "knowledgeWritten",
        "parentTranscriptInherited",
    )
    unchanged_zero_activity = all(
        all(response.get(field) is False for field in zero_activity_fields)
        for response in unchanged
    )
    unchanged_bounded = all(
        len(canonical_json_bytes(response)) <= 512 for response in unchanged
    )
    if len(unchanged) < 10 or not unchanged_zero_activity or not unchanged_bounded:
        raise ContractError("unchanged heartbeat benchmark contract was not satisfied")
    if Path(f"{state_path}.lease").exists():
        raise ContractError("heartbeat benchmark left a lease behind")

    return {
        "cycleCount": len(responses),
        "statusCounts": {
            "changed": statuses.count(STATUS_CHANGED),
            "unchanged": statuses.count(STATUS_UNCHANGED),
            "attentionRequired": 0,
            "failed": 0,
        },
        "activityCounts": {
            "modelDispatches": sum(bool(item["modelDispatched"]) for item in responses),
            "workerDispatches": sum(bool(item["workerDispatched"]) for item in responses),
            "leaseAcquisitions": sum(bool(item["leaseAcquired"]) for item in responses),
            "stateWrites": sum(bool(item["stateWritten"]) for item in responses),
            "knowledgeWrites": sum(bool(item["knowledgeWritten"]) for item in responses),
        },
        "unchangedContract": {
            "sampleCount": len(unchanged),
            "maximumPayloadBytes": max(
                len(canonical_json_bytes(item)) for item in unchanged
            ),
            "within512Bytes": unchanged_bounded,
            "zeroModelWorkerLeaseStateKnowledgeAndTranscriptActivity": unchanged_zero_activity,
        },
        "responsePayloadBytes": _integer_stats(payload_bytes),
        "elapsedNanoseconds": _integer_stats(elapsed),
    }


def _benchmark_config() -> PilotConfig:
    return PilotConfig.from_dict(
        {
            "schemaVersion": 2,
            "heartbeatId": "heartbeat-v2-local-benchmark",
            "enabled": True,
            "mode": "pilot",
            "gates": {
                "allowStateWrites": True,
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
    )


def _run_recovery_trials(
    project_alias: str,
    project_root: Path,
    temporary_root: Path,
) -> dict[str, Any]:
    temporary_root.mkdir(parents=True, exist_ok=False)
    initial = _read_benchmark_context(project_root)
    brief_elapsed: list[int] = []
    direct_elapsed: list[int] = []
    brief_bytes: list[int] = []
    direct_bytes: list[int] = []
    criterion_results: dict[str, list[bool | None]] = {
        "task": [],
        "gate": [],
        "nextAction": [],
        "checkpoint": [],
        "sourceHashes": [],
    }

    for trial_index in range(RECOVERY_TRIALS_PER_PROJECT):
        prepared = _read_benchmark_context(project_root)
        brief = _build_benchmark_brief(project_alias, prepared)
        brief_path = temporary_root / f"trial-{trial_index:02d}.json"
        byte_count = atomic_write_json(
            brief_path,
            brief,
            MAXIMUM_RECOVERY_PROBE_BRIEF_BYTES,
        )

        if trial_index % 2 == 0:
            recovered_brief, brief_ns = _time_brief_recovery(
                brief_path,
                project_alias,
            )
            recovered_direct, direct_ns = _time_direct_recovery(project_root)
        else:
            recovered_direct, direct_ns = _time_direct_recovery(project_root)
            recovered_brief, brief_ns = _time_brief_recovery(
                brief_path,
                project_alias,
            )

        direct_projection = _context_projection(recovered_direct)
        criterion_results["task"].append(
            recovered_brief["currentTask"] == direct_projection["currentTask"]
        )
        criterion_results["gate"].append(
            recovered_brief["gate"] == direct_projection["gate"]
        )
        criterion_results["nextAction"].append(
            recovered_brief["nextAction"] == direct_projection["nextAction"]
        )
        criterion_results["sourceHashes"].append(
            recovered_brief["sourceHashes"] == recovered_direct.source_hashes
        )
        if (
            recovered_brief["checkpoint"] is None
            and direct_projection["checkpoint"] is None
        ):
            criterion_results["checkpoint"].append(None)
        else:
            criterion_results["checkpoint"].append(
                recovered_brief["checkpoint"] == direct_projection["checkpoint"]
            )

        brief_elapsed.append(brief_ns)
        direct_elapsed.append(direct_ns)
        brief_bytes.append(byte_count)
        direct_bytes.append(recovered_direct.source_payload_bytes)

    final = _read_benchmark_context(project_root)
    criteria = {
        name: _criterion_stats(values)
        for name, values in criterion_results.items()
    }
    return {
        "projectAlias": project_alias,
        "trialCount": RECOVERY_TRIALS_PER_PROJECT,
        "timedFirstCounts": {
            "brief": RECOVERY_TRIALS_PER_PROJECT // 2,
            "direct": RECOVERY_TRIALS_PER_PROJECT // 2,
        },
        "briefPayloadBytes": _integer_stats(brief_bytes),
        "directPayloadBytes": _integer_stats(direct_bytes),
        "briefToDirectPayloadPermille": (
            sum(brief_bytes) * 1000 // sum(direct_bytes)
        ),
        "elapsedNanoseconds": {
            "brief": _integer_stats(brief_elapsed),
            "direct": _integer_stats(direct_elapsed),
        },
        "criteria": criteria,
        "allMeasurableCriteriaPassed": all(
            item["notPassed"] == 0 for item in criteria.values()
        ),
        "authorityStable": initial.source_hashes == final.source_hashes,
    }


def _read_benchmark_context(project_root: Path) -> RecoveryProbeContext:
    return read_recovery_context(
        project_root,
        require_exact_recovery_order=False,
        checkpoint_heading="Latest Stable Checkpoint",
    )


def _build_benchmark_brief(
    project_alias: str,
    context: RecoveryProbeContext,
) -> dict[str, Any]:
    projection = _context_projection(context)
    normalized_root = (
        str(context.root).casefold() if context.root.drive else str(context.root)
    )
    brief = {
        "schema": BENCHMARK_BRIEF_SCHEMA,
        "projectAlias": project_alias,
        "rootId": f"root_{sha256_tag(normalized_root.encode('utf-8'))[7:39]}",
        "currentTask": projection["currentTask"],
        "gate": projection["gate"],
        "nextAction": projection["nextAction"],
        "checkpoint": projection["checkpoint"],
        "recoveryOrderPrefix": list(RECOVERY_ORDER),
        "sourceHashes": context.source_hashes,
        "sourcePayloadBytes": context.source_payload_bytes,
        "authority": {
            "localFilesDecisive": True,
            "parentTranscriptInherited": False,
            "taskIdentifierIncluded": False,
        },
    }
    if (
        len(canonical_json_bytes(brief) + b"\n")
        > MAXIMUM_RECOVERY_PROBE_BRIEF_BYTES
    ):
        raise ContractError("benchmark recovery brief exceeds 8192 bytes")
    return brief


def _context_projection(context: RecoveryProbeContext) -> dict[str, Any]:
    return {
        "currentTask": {
            "status": context.task["status"],
            "summary": _sanitize_context_text(
                context.task["summary"],
                "current task summary",
                520,
            ),
        },
        "gate": _sanitize_context_text(context.gate, "working gate", 640),
        "nextAction": _sanitize_context_text(
            context.next_action,
            "next action",
            760,
        ),
        "checkpoint": context.checkpoint,
    }


def _sanitize_context_text(value: Any, field_name: str, maximum: int) -> str:
    text = require_string(value, field_name, maximum * 8)
    text = _WINDOWS_ABSOLUTE.sub("<local-path>", text)
    text = _POSIX_HOME.sub("<local-path>", text)
    text = _PROMPT_REFERENCE.sub("<prompt-ref>", text)
    text = _TASK_IDENTIFIER.sub("<id>", text)
    return normalize_public_text(text, field_name, maximum)


def _time_direct_recovery(
    project_root: Path,
) -> tuple[RecoveryProbeContext, int]:
    started = time.perf_counter_ns()
    recovered = _read_benchmark_context(project_root)
    return recovered, time.perf_counter_ns() - started


def _time_brief_recovery(
    brief_path: Path,
    project_alias: str,
) -> tuple[dict[str, Any], int]:
    started = time.perf_counter_ns()
    try:
        payload = brief_path.read_bytes()
    except OSError as exc:
        raise ContractError("unable to read temporary benchmark brief") from exc
    if len(payload) > MAXIMUM_RECOVERY_PROBE_BRIEF_BYTES:
        raise ContractError("temporary benchmark brief exceeds 8192 bytes")
    try:
        value = json.loads(payload.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ContractError("temporary benchmark brief is invalid") from exc
    _validate_benchmark_brief(value, project_alias)
    return value, time.perf_counter_ns() - started


def _validate_benchmark_brief(value: Any, project_alias: str) -> None:
    if not isinstance(value, dict) or set(value) != _BRIEF_FIELDS:
        raise ContractError("benchmark brief fields are invalid")
    if value.get("schema") != BENCHMARK_BRIEF_SCHEMA:
        raise ContractError("benchmark brief schema is invalid")
    if value.get("projectAlias") != project_alias:
        raise ContractError("benchmark brief project alias is invalid")
    root_id = value.get("rootId")
    if (
        not isinstance(root_id, str)
        or not re.fullmatch(r"root_[0-9a-f]{32}", root_id)
    ):
        raise ContractError("benchmark brief root identity is invalid")
    if value.get("recoveryOrderPrefix") != list(RECOVERY_ORDER):
        raise ContractError("benchmark brief recovery prefix is invalid")
    hashes = value.get("sourceHashes")
    if (
        not isinstance(hashes, dict)
        or set(hashes) != set(RECOVERY_ORDER)
        or any(not isinstance(item, str) or not _SHA256_VALUE.fullmatch(item) for item in hashes.values())
    ):
        raise ContractError("benchmark brief source hashes are invalid")
    task = value.get("currentTask")
    if (
        not isinstance(task, dict)
        or set(task) != {"status", "summary"}
        or task.get("status") != "active"
        or not isinstance(task.get("summary"), str)
    ):
        raise ContractError("benchmark brief task projection is invalid")
    for field in ("gate", "nextAction"):
        if not isinstance(value.get(field), str) or not value[field]:
            raise ContractError(f"benchmark brief {field} is invalid")
    checkpoint = value.get("checkpoint")
    if checkpoint is not None and (
        not isinstance(checkpoint, dict)
        or checkpoint.get("representation") != "section-sha256"
        or not _SHA256_VALUE.fullmatch(str(checkpoint.get("source")))
    ):
        raise ContractError("benchmark brief checkpoint is invalid")
    if not isinstance(value.get("sourcePayloadBytes"), int):
        raise ContractError("benchmark brief source payload size is invalid")
    authority = value.get("authority")
    if authority != {
        "localFilesDecisive": True,
        "parentTranscriptInherited": False,
        "taskIdentifierIncluded": False,
    }:
        raise ContractError("benchmark brief authority policy is invalid")


def _criterion_stats(values: list[bool | None]) -> dict[str, int]:
    measurable = [value for value in values if value is not None]
    passed = sum(value is True for value in measurable)
    return {
        "measurable": len(measurable),
        "passed": passed,
        "notPassed": len(measurable) - passed,
        "notMeasurable": len(values) - len(measurable),
    }


def _merge_criteria(projects: list[dict[str, Any]]) -> dict[str, dict[str, int]]:
    names = ("task", "gate", "nextAction", "checkpoint", "sourceHashes")
    return {
        name: {
            field: sum(project["criteria"][name][field] for project in projects)
            for field in ("measurable", "passed", "notPassed", "notMeasurable")
        }
        for name in names
    }


def _integer_stats(values: list[int]) -> dict[str, int]:
    if not values:
        raise ContractError("benchmark statistics require at least one value")
    ordered = sorted(values)
    p95_index = max(0, ((95 * len(ordered) + 99) // 100) - 1)
    return {
        "count": len(ordered),
        "total": sum(ordered),
        "minimum": ordered[0],
        "maximum": ordered[-1],
        "mean": sum(ordered) // len(ordered),
        "median": int(statistics.median(ordered)),
        "p95": ordered[p95_index],
    }


def validate_sanitized_report(report: dict[str, Any]) -> None:
    """Reject report content capable of retaining project-specific material."""

    if set(report) != _REPORT_FIELDS or report.get("schema") != REPORT_SCHEMA:
        raise ContractError("benchmark report fields are invalid")
    payload = canonical_json_bytes(report)
    if len(payload) > MAXIMUM_REPORT_BYTES:
        raise ContractError("benchmark report exceeds 16384 bytes")

    def walk(value: Any, key_path: tuple[str, ...] = ()) -> None:
        if isinstance(value, dict):
            for key, item in value.items():
                lowered = key.casefold()
                if lowered in {
                    "path",
                    "rootpath",
                    "taskid",
                    "prompt",
                    "rawcontent",
                }:
                    raise ContractError("benchmark report contains a forbidden field")
                walk(item, key_path + (key,))
        elif isinstance(value, list):
            for item in value:
                walk(item, key_path)
        elif isinstance(value, str):
            if (
                _WINDOWS_ABSOLUTE.search(value)
                or _POSIX_HOME.search(value)
                or _PROMPT_REFERENCE.search(value)
                or _SHA256_VALUE.fullmatch(value)
                or _TASK_IDENTIFIER.fullmatch(value)
            ):
                raise ContractError("benchmark report contains forbidden project material")

    walk(report)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run the isolated Heartbeat v2 and bounded recovery benchmark"
    )
    parser.add_argument("--pegasus-nexus-root", type=Path, required=True)
    parser.add_argument("--neural-commander-root", type=Path, required=True)
    return parser


def main(argv: list[str] | None = None) -> int:
    arguments = _parser().parse_args(argv)
    try:
        report = run_benchmark(
            arguments.pegasus_nexus_root,
            arguments.neural_commander_root,
        )
    except ContractError:
        sys.stderr.write("benchmark failed closed on a local contract\n")
        return 2
    sys.stdout.buffer.write(canonical_json_bytes(report) + b"\n")
    sys.stdout.buffer.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
