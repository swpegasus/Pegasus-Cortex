"""Deterministic revision-first Heartbeat v2 pilot.

This module is deliberately client/orchestrator-side. It contains no product
mutation adapter, model client, worker launcher, network transport, or live
automation control.
"""

from __future__ import annotations

import os
import stat
import time
import uuid
from dataclasses import dataclass, replace
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable

from .common import (
    CancellationRequested,
    ContractError,
    atomic_write_json,
    canonical_json_bytes,
    format_utc,
    normalize_public_text,
    parse_utc,
    read_json_object,
    require_string,
    sha256_tag,
)
from .paths import is_reparse, validate_direct_state_file


STATUS_UNCHANGED = "unchanged"
STATUS_CHANGED = "changed"
STATUS_ATTENTION = "attention-required"
STATUS_FAILED = "failed"
_STATUSES = (STATUS_UNCHANGED, STATUS_CHANGED, STATUS_ATTENTION, STATUS_FAILED)
_SEVERITY_ORDER = {"info": 0, "warning": 1, "critical": 2}
_CONFIG_FIELDS = {
    "schemaVersion",
    "heartbeatId",
    "enabled",
    "mode",
    "gates",
    "isolation",
    "limits",
    "retry",
}
_GATE_FIELDS = {
    "allowStateWrites",
    "allowProductMutation",
    "allowModelDispatch",
    "allowWorkerDispatch",
    "inheritParentTranscript",
}
_ISOLATION_FIELDS = {
    "profile",
    "allowEnvironmentConfiguration",
    "allowNetwork",
    "integratedAdapters",
}
_LIMIT_FIELDS = {
    "maxAttempts",
    "maxConcurrentRuns",
    "leaseSeconds",
    "coalesceWindowSeconds",
    "maximumDeltas",
    "maximumCoalescedEvents",
    "maximumReportBytes",
}
_RETRY_FIELDS = {"baseSeconds", "jitterMaxSeconds"}
_OBSERVATION_FIELDS = {
    "schemaVersion",
    "source",
    "cursor",
    "baseCursor",
    "revisionInputs",
    "deltas",
    "attentionReasons",
    "probeFailure",
    "declaredRevision",
}
_DELTA_FIELDS = {
    "dedupeKey",
    "category",
    "scope",
    "severity",
    "summary",
    "occurredAtUtc",
}
_METRIC_NAMES = {
    "attentionRequired",
    "changed",
    "eventsEmitted",
    "eventsObserved",
    "failed",
    "runs",
    "unchanged",
}
_STATE_FIELDS = {
    "schemaVersion",
    "heartbeatId",
    "stateHash",
    "lastRevision",
    "lastCursor",
    "completedIdempotencyKeys",
    "retry",
    "attentionLatched",
    "metrics",
    "lastReceipt",
}
_RECEIPT_FIELDS = {
    "schemaVersion",
    "heartbeatId",
    "receiptId",
    "atUtc",
    "status",
    "eventsObserved",
    "eventsEmitted",
    "retryDelaySeconds",
    "consecutiveFailures",
    "totals",
}
_LEASE_FIELDS = {
    "schemaVersion",
    "heartbeatId",
    "idempotencyKey",
    "leaseId",
    "ownerId",
    "ownerPid",
    "acquiredAtUtc",
    "expiresAtUtc",
    "leaseHash",
}
_MAXIMUM_LEASE_BYTES = 2048


class LeaseUnavailable(ContractError):
    """Raised when another run or a stale lease blocks the state boundary."""


@dataclass(frozen=True)
class PilotConfig:
    """Validated local execution policy for one pilot invocation."""

    heartbeat_id: str
    enabled: bool
    mode: str
    allow_state_writes: bool
    allow_product_mutation: bool
    allow_model_dispatch: bool
    allow_worker_dispatch: bool
    inherit_parent_transcript: bool
    profile: str
    allow_environment_configuration: bool
    allow_network: bool
    integrated_adapters: tuple[str, ...]
    max_attempts: int
    max_concurrent_runs: int
    lease_seconds: int
    coalesce_window_seconds: int
    maximum_deltas: int
    maximum_coalesced_events: int
    maximum_report_bytes: int
    retry_base_seconds: tuple[int, int, int]
    retry_jitter_max_seconds: tuple[int, int, int]

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "PilotConfig":
        if not isinstance(value, dict):
            raise ContractError("pilot config must be an object")
        if set(value) != _CONFIG_FIELDS:
            raise ContractError("pilot config fields do not match schema version 2")
        if value.get("schemaVersion") != 2:
            raise ContractError("pilot config schemaVersion must be 2")
        gates = value.get("gates")
        limits = value.get("limits")
        retry = value.get("retry")
        isolation = value.get("isolation")
        if (
            not isinstance(gates, dict)
            or not isinstance(limits, dict)
            or not isinstance(retry, dict)
            or not isinstance(isolation, dict)
        ):
            raise ContractError("pilot config requires gates, limits, retry, and isolation objects")
        if set(gates) != _GATE_FIELDS:
            raise ContractError("pilot config gate fields do not match schema version 2")
        if set(limits) != _LIMIT_FIELDS:
            raise ContractError("pilot config limit fields do not match schema version 2")
        if set(retry) != _RETRY_FIELDS:
            raise ContractError("pilot config retry fields do not match schema version 2")
        if set(isolation) != _ISOLATION_FIELDS:
            raise ContractError("pilot config isolation fields do not match schema version 2")

        mode = value.get("mode")
        if mode not in ("report-only", "pilot"):
            raise ContractError("mode must be report-only or pilot")
        bases = _require_integer_triplet(retry.get("baseSeconds"), "retry.baseSeconds")
        jitters = _require_integer_triplet(retry.get("jitterMaxSeconds"), "retry.jitterMaxSeconds")
        if bases != (60, 300, 900):
            raise ContractError("retry.baseSeconds must preserve the 1/5/15-minute schedule")
        if jitters != (0, 0, 0):
            raise ContractError("retry jitter must be zero for the exact 1/5/15-minute schedule")

        raw_adapters = isolation.get("integratedAdapters")
        if not isinstance(raw_adapters, list) or any(
            not isinstance(item, str) for item in raw_adapters
        ):
            raise ContractError("isolation.integratedAdapters must be a string array")

        config = cls(
            heartbeat_id=require_string(value.get("heartbeatId"), "heartbeatId", 96),
            enabled=_require_bool(value.get("enabled"), "enabled"),
            mode=mode,
            allow_state_writes=_require_bool(gates.get("allowStateWrites"), "gates.allowStateWrites"),
            allow_product_mutation=_require_bool(
                gates.get("allowProductMutation"), "gates.allowProductMutation"
            ),
            allow_model_dispatch=_require_bool(
                gates.get("allowModelDispatch"), "gates.allowModelDispatch"
            ),
            allow_worker_dispatch=_require_bool(
                gates.get("allowWorkerDispatch"), "gates.allowWorkerDispatch"
            ),
            inherit_parent_transcript=_require_bool(
                gates.get("inheritParentTranscript"), "gates.inheritParentTranscript"
            ),
            profile=require_string(isolation.get("profile"), "isolation.profile", 64),
            allow_environment_configuration=_require_bool(
                isolation.get("allowEnvironmentConfiguration"),
                "isolation.allowEnvironmentConfiguration",
            ),
            allow_network=_require_bool(isolation.get("allowNetwork"), "isolation.allowNetwork"),
            integrated_adapters=tuple(raw_adapters),
            max_attempts=_require_integer(limits.get("maxAttempts"), "limits.maxAttempts", 3, 3),
            max_concurrent_runs=_require_integer(
                limits.get("maxConcurrentRuns"), "limits.maxConcurrentRuns", 1, 1
            ),
            lease_seconds=_require_integer(limits.get("leaseSeconds"), "limits.leaseSeconds", 15, 300),
            coalesce_window_seconds=_require_integer(
                limits.get("coalesceWindowSeconds"), "limits.coalesceWindowSeconds", 1, 3600
            ),
            maximum_deltas=_require_integer(
                limits.get("maximumDeltas"), "limits.maximumDeltas", 1, 64
            ),
            maximum_coalesced_events=_require_integer(
                limits.get("maximumCoalescedEvents"), "limits.maximumCoalescedEvents", 1, 16
            ),
            maximum_report_bytes=_require_integer(
                limits.get("maximumReportBytes"), "limits.maximumReportBytes", 512, 8192
            ),
            retry_base_seconds=bases,
            retry_jitter_max_seconds=jitters,
        )
        if config.allow_product_mutation:
            raise ContractError("the local pilot has no product mutation adapter")
        if config.inherit_parent_transcript:
            raise ContractError("Heartbeat v2 forbids inherited parent transcripts")
        if config.profile != "standalone-local":
            raise ContractError("the pilot supports only the standalone-local profile")
        if config.allow_environment_configuration:
            raise ContractError("standalone-local forbids environment-driven configuration")
        if config.allow_network:
            raise ContractError("standalone-local forbids network access")
        if config.integrated_adapters:
            raise ContractError("standalone-local forbids integrated adapters")
        if config.allow_model_dispatch or config.allow_worker_dispatch:
            raise ContractError("the isolated pilot contains no model or worker dispatcher")
        if config.mode == "report-only" and (
            config.allow_state_writes
            or config.allow_model_dispatch
            or config.allow_worker_dispatch
        ):
            raise ContractError("report-only mode forbids state writes and dispatch")
        return config


@dataclass(frozen=True)
class Delta:
    """One bounded public change marker supplied by the coordination source."""

    dedupe_key: str
    category: str
    scope: str
    severity: str
    summary: str
    occurred_at_utc: datetime

    @classmethod
    def from_dict(cls, value: Any, index: int) -> "Delta":
        if not isinstance(value, dict):
            raise ContractError(f"deltas[{index}] must be an object")
        if set(value) != _DELTA_FIELDS:
            raise ContractError(f"deltas[{index}] fields do not match schema version 2")
        severity = value.get("severity")
        if severity not in _SEVERITY_ORDER:
            raise ContractError(f"deltas[{index}].severity is invalid")
        return cls(
            dedupe_key=require_string(value.get("dedupeKey"), f"deltas[{index}].dedupeKey", 128),
            category=require_string(value.get("category"), f"deltas[{index}].category", 48),
            scope=require_string(value.get("scope"), f"deltas[{index}].scope", 96),
            severity=severity,
            summary=normalize_public_text(value.get("summary"), f"deltas[{index}].summary", 192),
            occurred_at_utc=parse_utc(value.get("occurredAtUtc"), f"deltas[{index}].occurredAtUtc"),
        )

    def revision_value(self) -> dict[str, str]:
        return {
            "category": self.category,
            "dedupeKey": self.dedupe_key,
            "occurredAtUtc": format_utc(self.occurred_at_utc),
            "scope": self.scope,
            "severity": self.severity,
            "summary": self.summary,
        }


@dataclass(frozen=True)
class Observation:
    """A complete bounded snapshot used to derive one deterministic revision."""

    source: str
    cursor: str | None
    base_cursor: str | None
    revision_inputs: dict[str, Any]
    deltas: tuple[Delta, ...]
    attention_reasons: tuple[str, ...]
    probe_failure: str | None
    declared_revision: str | None

    @classmethod
    def from_dict(cls, value: dict[str, Any], config: PilotConfig) -> "Observation":
        if not isinstance(value, dict):
            raise ContractError("observation must be an object")
        unknown_fields = set(value) - _OBSERVATION_FIELDS
        if unknown_fields:
            raise ContractError("observation contains fields outside schema version 2")
        if value.get("schemaVersion") != 2:
            raise ContractError("observation schemaVersion must be 2")
        revision_inputs = value.get("revisionInputs")
        if not isinstance(revision_inputs, dict):
            raise ContractError("revisionInputs must be an object")
        if len(canonical_json_bytes(revision_inputs)) > 65_536:
            raise ContractError("revisionInputs exceeds 65536 canonical bytes")

        raw_deltas = value.get("deltas", [])
        if not isinstance(raw_deltas, list) or len(raw_deltas) > config.maximum_deltas:
            raise ContractError(f"deltas must contain at most {config.maximum_deltas} entries")
        deltas = tuple(Delta.from_dict(item, index) for index, item in enumerate(raw_deltas))

        raw_attention = value.get("attentionReasons", [])
        if not isinstance(raw_attention, list) or len(raw_attention) > 8:
            raise ContractError("attentionReasons must contain at most 8 entries")
        attention = tuple(
            normalize_public_text(item, f"attentionReasons[{index}]", 160)
            for index, item in enumerate(raw_attention)
        )

        cursor = _optional_string(value.get("cursor"), "cursor", 256)
        base_cursor = _optional_string(value.get("baseCursor"), "baseCursor", 256)
        probe_failure = _optional_string(value.get("probeFailure"), "probeFailure", 160)
        declared_revision = _optional_string(value.get("declaredRevision"), "declaredRevision", 80)
        return cls(
            source=require_string(value.get("source"), "source", 96),
            cursor=cursor,
            base_cursor=base_cursor,
            revision_inputs=revision_inputs,
            deltas=deltas,
            attention_reasons=attention,
            probe_failure=probe_failure,
            declared_revision=declared_revision,
        )

    def revision(self) -> str:
        material = {
            "attentionReasons": list(self.attention_reasons),
            "baseCursor": self.base_cursor,
            "cursor": self.cursor,
            "deltas": [
                delta.revision_value()
                for delta in sorted(
                    self.deltas,
                    key=lambda item: (
                        item.dedupe_key,
                        format_utc(item.occurred_at_utc),
                        item.category,
                        item.scope,
                        item.summary,
                    ),
                )
            ],
            "revisionInputs": self.revision_inputs,
            "source": self.source,
        }
        return sha256_tag(canonical_json_bytes(material))


@dataclass(frozen=True)
class ProbeResult:
    status: str
    revision: str
    idempotency_key: str
    reason: str
    events: tuple[dict[str, Any], ...]
    observed_count: int
    cursor_advanced: bool
    failure: bool


def default_state(heartbeat_id: str) -> dict[str, Any]:
    """Return a new in-memory external-state value."""

    return seal_state({
        "schemaVersion": 2,
        "heartbeatId": heartbeat_id,
        "stateHash": "",
        "lastRevision": None,
        "lastCursor": None,
        "completedIdempotencyKeys": [],
        "retry": {"attempt": 0, "nextAttemptAtUtc": None},
        "attentionLatched": False,
        "metrics": {
            "attentionRequired": 0,
            "changed": 0,
            "eventsEmitted": 0,
            "eventsObserved": 0,
            "failed": 0,
            "runs": 0,
            "unchanged": 0,
        },
        "lastReceipt": None,
    })


def seal_state(value: dict[str, Any]) -> dict[str, Any]:
    """Return a deep JSON snapshot with a deterministic equality hash."""

    captured = read_json_value(value)
    captured["stateHash"] = ""
    material = dict(captured)
    material.pop("stateHash")
    captured["stateHash"] = sha256_tag(canonical_json_bytes(material))
    return captured


def read_json_value(value: dict[str, Any]) -> dict[str, Any]:
    """Deep-copy a JSON object through the canonical parser."""

    import json

    return json.loads(canonical_json_bytes(value).decode("utf-8"))


def validate_state(value: dict[str, Any], heartbeat_id: str) -> dict[str, Any]:
    """Validate the subset additionally enforced by the JSON Schema."""

    if set(value) != _STATE_FIELDS:
        raise ContractError("external state fields do not match schema version 2")
    if value.get("schemaVersion") != 2 or value.get("heartbeatId") != heartbeat_id:
        raise ContractError("external state identity does not match the pilot")
    state_hash = value.get("stateHash")
    if not _is_sha256(state_hash) or state_hash != seal_state(value)["stateHash"]:
        raise ContractError("external state equality hash does not match its content")
    keys = value.get("completedIdempotencyKeys")
    retry = value.get("retry")
    metrics = value.get("metrics")
    if not isinstance(keys, list) or len(keys) > 64:
        raise ContractError("completedIdempotencyKeys must contain at most 64 entries")
    if any(not _is_sha256(item) for item in keys):
        raise ContractError("completedIdempotencyKeys contains an invalid digest")
    if len(keys) != len(set(keys)):
        raise ContractError("completedIdempotencyKeys must be unique")
    if not isinstance(retry, dict) or not isinstance(metrics, dict):
        raise ContractError("state retry and metrics must be objects")
    if set(retry) != {"attempt", "nextAttemptAtUtc"}:
        raise ContractError("state retry fields do not match schema version 2")
    if not isinstance(value.get("attentionLatched"), bool):
        raise ContractError("state attentionLatched must be a boolean")
    last_revision = value.get("lastRevision")
    if last_revision is not None and not _is_sha256(last_revision):
        raise ContractError("state lastRevision is invalid")
    last_cursor = value.get("lastCursor")
    if last_cursor is not None:
        require_string(last_cursor, "state.lastCursor", 256)
    attempt = _require_integer(retry.get("attempt"), "state.retry.attempt", 0, 3)
    next_attempt = retry.get("nextAttemptAtUtc")
    if next_attempt is not None:
        parse_utc(next_attempt, "state.retry.nextAttemptAtUtc")
    if (attempt == 0) != (next_attempt is None):
        raise ContractError("state retry timestamp must be present exactly when attempt is nonzero")
    if value["attentionLatched"] != (attempt == 3):
        raise ContractError("state attention latch must correspond to the third failure")
    _validate_metrics(metrics, "state.metrics")
    receipt = value.get("lastReceipt")
    if receipt is not None:
        _validate_receipt(receipt, heartbeat_id, metrics, attempt)
    return value


def _validate_receipt(
    receipt: Any,
    heartbeat_id: str,
    metrics: dict[str, Any],
    attempt: int,
) -> None:
    """Validate the content-free persisted receipt embedded in external state."""

    if not isinstance(receipt, dict) or set(receipt) != _RECEIPT_FIELDS:
        raise ContractError("state lastReceipt fields do not match schema version 2")
    if receipt.get("schemaVersion") != 2 or receipt.get("heartbeatId") != heartbeat_id:
        raise ContractError("state lastReceipt identity does not match the pilot")
    if not _is_sha256(receipt.get("receiptId")):
        raise ContractError("state lastReceipt.receiptId is invalid")
    parse_utc(receipt.get("atUtc"), "state.lastReceipt.atUtc")
    if receipt.get("status") not in _STATUSES:
        raise ContractError("state lastReceipt.status is invalid")
    _require_integer(
        receipt.get("eventsObserved"),
        "state.lastReceipt.eventsObserved",
        0,
        2**63 - 1,
    )
    _require_integer(
        receipt.get("eventsEmitted"),
        "state.lastReceipt.eventsEmitted",
        0,
        2**63 - 1,
    )
    retry_delay = receipt.get("retryDelaySeconds")
    if retry_delay is not None:
        _require_integer(
            retry_delay,
            "state.lastReceipt.retryDelaySeconds",
            0,
            990,
        )
    consecutive = _require_integer(
        receipt.get("consecutiveFailures"),
        "state.lastReceipt.consecutiveFailures",
        0,
        3,
    )
    if consecutive != attempt:
        raise ContractError("state receipt consecutiveFailures does not match retry state")
    totals = receipt.get("totals")
    if not isinstance(totals, dict):
        raise ContractError("state lastReceipt.totals must be an object")
    _validate_metrics(totals, "state.lastReceipt.totals")
    if totals != metrics:
        raise ContractError("state lastReceipt totals do not match state metrics")


def _validate_metrics(metrics: dict[str, Any], field_name: str) -> None:
    if set(metrics) != _METRIC_NAMES:
        raise ContractError(f"{field_name} fields do not match schema version 2")
    for metric_name in sorted(_METRIC_NAMES):
        _require_integer(
            metrics.get(metric_name),
            f"{field_name}.{metric_name}",
            0,
            2**63 - 1,
        )


def _is_sha256(value: Any) -> bool:
    return (
        isinstance(value, str)
        and len(value) == 71
        and value.startswith("sha256:")
        and all(character in "0123456789abcdef" for character in value[7:])
    )


def load_state(path: Path | None, heartbeat_id: str) -> dict[str, Any]:
    """Load external state when present, otherwise use a new in-memory value."""

    if path is None or not path.exists():
        return default_state(heartbeat_id)
    return validate_state(read_json_object(path, 65_536), heartbeat_id)


def probe_revision(
    observation: Observation,
    state: dict[str, Any],
    config: PilotConfig,
) -> ProbeResult:
    """Classify an observation without mutation or dispatch."""

    revision = observation.revision()
    if observation.declared_revision is not None and observation.declared_revision != revision:
        return _probe_result(
            config.heartbeat_id,
            STATUS_FAILED,
            revision,
            observation,
            "declared_revision_mismatch",
            (),
            failure=True,
        )
    if observation.probe_failure is not None:
        return _probe_result(
            config.heartbeat_id,
            STATUS_FAILED,
            revision,
            observation,
            "source_probe_failed",
            (),
            failure=True,
        )

    events = coalesce_deltas(observation.deltas, config)
    previous_cursor = state.get("lastCursor")
    cursor_reason: str | None = None
    if previous_cursor is None:
        if observation.base_cursor is not None:
            cursor_reason = "unexpected_initial_base_cursor"
    elif observation.cursor == previous_cursor:
        if observation.deltas:
            cursor_reason = "delta_present_without_cursor_advance"
    elif observation.base_cursor != previous_cursor:
        cursor_reason = "cursor_gap"

    if cursor_reason is not None:
        return _probe_result(
            config.heartbeat_id,
            STATUS_ATTENTION,
            revision,
            observation,
            cursor_reason,
            events,
        )
    if observation.attention_reasons or any(
        event["severity"] == "critical" for event in events
    ):
        return _probe_result(
            config.heartbeat_id,
            STATUS_ATTENTION,
            revision,
            observation,
            "source_attention",
            events,
        )

    idempotency_key = _idempotency_key(config.heartbeat_id, revision, observation.cursor)
    cursor_advanced = observation.cursor != previous_cursor
    if (
        state.get("lastRevision") == revision
        or idempotency_key in state.get("completedIdempotencyKeys", [])
    ):
        return ProbeResult(
            status=STATUS_UNCHANGED,
            revision=revision,
            idempotency_key=idempotency_key,
            reason="revision_match",
            events=(),
            observed_count=len(observation.deltas),
            cursor_advanced=False,
            failure=False,
        )
    return ProbeResult(
        status=STATUS_CHANGED,
        revision=revision,
        idempotency_key=idempotency_key,
        reason="revision_advanced",
        events=events,
        observed_count=len(observation.deltas),
        cursor_advanced=cursor_advanced,
        failure=False,
    )


def coalesce_deltas(deltas: tuple[Delta, ...], config: PilotConfig) -> tuple[dict[str, Any], ...]:
    """Deterministically coalesce duplicate changes into bounded time buckets."""

    grouped: dict[tuple[str, int], list[Delta]] = {}
    for delta in deltas:
        bucket = int(delta.occurred_at_utc.timestamp()) // config.coalesce_window_seconds
        grouped.setdefault((delta.dedupe_key, bucket), []).append(delta)
    if len(grouped) > config.maximum_coalesced_events:
        raise ContractError(
            f"coalesced event count exceeds {config.maximum_coalesced_events}"
        )

    result: list[dict[str, Any]] = []
    for (dedupe_key, _), items in sorted(grouped.items(), key=lambda item: item[0]):
        ordered = sorted(
            items,
            key=lambda item: (
                item.occurred_at_utc,
                item.category,
                item.scope,
                item.summary,
            ),
        )
        highest = max(ordered, key=lambda item: _SEVERITY_ORDER[item.severity])
        latest = ordered[-1]
        result.append(
            {
                "category": latest.category,
                "count": len(ordered),
                "dedupeKey": dedupe_key,
                "firstAtUtc": format_utc(ordered[0].occurred_at_utc),
                "lastAtUtc": format_utc(latest.occurred_at_utc),
                "scope": latest.scope,
                "severity": highest.severity,
                "summary": latest.summary,
            }
        )
    return tuple(result)


class Lease:
    """One exclusive, bounded local state-write lease.

    Expired, well-formed leases are reclaimed by atomically renaming the whole
    lease directory to a unique quarantine name. Malformed or unsealed records
    remain fail-closed because they do not carry trustworthy expiry authority.
    """

    def __init__(
        self,
        state_path: Path,
        heartbeat_id: str,
        idempotency_key: str,
        lease_seconds: int,
        now: datetime,
    ) -> None:
        self._directory = Path(f"{state_path}.lease")
        self._released = False
        self._state_path = state_path
        lease_id = _lease_id(state_path, heartbeat_id, idempotency_key)
        owner_pid = os.getpid()
        owner_id = sha256_tag(
            canonical_json_bytes(
                {
                    "leaseId": lease_id,
                    "ownerPid": owner_pid,
                    "startedAtUtc": format_utc(now),
                }
            )
        )
        self._owned_record = _seal_lease(
            {
                "schemaVersion": 2,
                "heartbeatId": heartbeat_id,
                "idempotencyKey": idempotency_key,
                "leaseId": lease_id,
                "ownerId": owner_id,
                "ownerPid": owner_pid,
                "acquiredAtUtc": format_utc(now),
                "expiresAtUtc": format_utc(now + timedelta(seconds=lease_seconds)),
                "leaseHash": "",
            }
        )
        self._publish_sealed_lease(now)

    def _publish_sealed_lease(self, now: datetime) -> None:
        staging = self._quarantine_path("stage", self._owned_record["leaseHash"])
        staging_created = False
        try:
            staging.mkdir(parents=False, exist_ok=False)
            staging_created = True
            atomic_write_json(
                staging / "lease.json",
                self._owned_record,
                _MAXIMUM_LEASE_BYTES,
            )
            staged = _read_valid_lease(staging, self._state_path)
            if canonical_json_bytes(staged) != canonical_json_bytes(
                self._owned_record
            ):
                raise ContractError("staged lease metadata changed before publication")

            # Windows can report success to two overlapping directory-renames
            # that started against the same source. Serialize the short
            # inspect/reclaim/publish boundary with a crash-released OS file
            # lock so one contender cannot move another contender's quarantine.
            with _LeaseMutationGuard(self._state_path):
                if self._directory.exists():
                    self._reclaim_expired(now)
                try:
                    staging.rename(self._directory)
                except OSError as exc:
                    raise LeaseUnavailable(
                        "another run acquired the external-state lease during publication"
                    ) from exc
        except Exception:
            if staging_created:
                self._remove_owned_directory(staging)
            raise

    def _reclaim_expired(self, now: datetime) -> None:
        try:
            observed = _read_valid_lease(self._directory, self._state_path)
        except (ContractError, OSError) as exc:
            raise LeaseUnavailable(
                "an untrusted external-state lease requires operator attention"
            ) from exc
        if now < parse_utc(observed["expiresAtUtc"], "lease.expiresAtUtc"):
            raise LeaseUnavailable("another run owns the active external-state lease")

        quarantine = self._quarantine_path("reclaim", observed["leaseHash"])
        try:
            self._directory.rename(quarantine)
        except OSError as exc:
            raise LeaseUnavailable(
                "another run changed the external-state lease during recovery"
            ) from exc
        try:
            captured = _read_valid_lease(quarantine, self._state_path)
            if canonical_json_bytes(captured) != canonical_json_bytes(observed):
                self._restore_quarantine(quarantine)
                raise LeaseUnavailable(
                    "the external-state lease changed during recovery"
                )
            if now < parse_utc(captured["expiresAtUtc"], "lease.expiresAtUtc"):
                self._restore_quarantine(quarantine)
                raise LeaseUnavailable(
                    "the external-state lease was renewed during recovery"
                )
            self._remove_exact_record_directory(quarantine)
        except LeaseUnavailable:
            raise
        except (ContractError, OSError) as exc:
            self._restore_quarantine(quarantine)
            raise LeaseUnavailable(
                "the quarantined external-state lease could not be verified"
            ) from exc

    def _quarantine_path(self, action: str, lease_hash: str) -> Path:
        token = uuid.uuid4().hex[:12]
        return self._directory.with_name(
            f"{self._directory.name}.{action}-{lease_hash[7:19]}-{token}"
        )

    def _restore_quarantine(self, quarantine: Path) -> None:
        if not quarantine.exists() or self._directory.exists():
            return
        try:
            quarantine.rename(self._directory)
        except OSError:
            # Never overwrite a lease acquired while the quarantine was held.
            return

    @staticmethod
    def _remove_exact_record_directory(directory: Path) -> None:
        metadata = directory / "lease.json"
        metadata.unlink()
        directory.rmdir()

    def _remove_owned_directory(self, directory: Path) -> None:
        try:
            if not directory.exists():
                return
            metadata = directory / "lease.json"
            if metadata.exists():
                observed = _read_valid_lease(directory, self._state_path)
                if canonical_json_bytes(observed) != canonical_json_bytes(
                    self._owned_record
                ):
                    return
                self._remove_exact_record_directory(directory)
            else:
                directory.rmdir()
        except (ContractError, OSError):
            # Cleanup is ownership-conservative: an uncertain directory remains
            # for operator attention instead of deleting another owner's data.
            return

    def release(self) -> None:
        if self._released:
            return
        try:
            try:
                # A bounded wait lets a simultaneous failed acquisition finish
                # its short validation without leaving this lease live until
                # expiry. The guard is still fail-closed after the deadline.
                with _LeaseMutationGuard(self._state_path, wait_seconds=1.0):
                    try:
                        observed = _read_valid_lease(self._directory, self._state_path)
                    except (ContractError, OSError):
                        return
                    if canonical_json_bytes(observed) != canonical_json_bytes(
                        self._owned_record
                    ):
                        return

                    quarantine = self._quarantine_path("release", observed["leaseHash"])
                    try:
                        self._directory.rename(quarantine)
                        captured = _read_valid_lease(quarantine, self._state_path)
                        if canonical_json_bytes(captured) != canonical_json_bytes(
                            self._owned_record
                        ):
                            self._restore_quarantine(quarantine)
                            return
                        self._remove_exact_record_directory(quarantine)
                    except (ContractError, OSError):
                        self._restore_quarantine(quarantine)
                        return
            except LeaseUnavailable:
                # Ownership-conservative cleanup: expiry recovery remains
                # available if the local mutation guard cannot be acquired.
                return
        finally:
            self._released = True

    def __enter__(self) -> "Lease":
        return self

    def __exit__(self, exc_type: object, exc: object, traceback: object) -> None:
        self.release()


class _LeaseMutationGuard:
    """Serialize live-lease directory mutation across threads and processes.

    The adjacent guard file is intentionally persistent. Removing a lock file
    after use would allow one process to keep a lock on the old file while a
    second process creates and locks a different file at the same path.
    Closing the descriptor releases the OS lock after normal exit or a crash.
    """

    def __init__(self, state_path: Path, *, wait_seconds: float = 0.0) -> None:
        self._path = Path(f"{state_path}.lease.guard")
        self._wait_seconds = max(0.0, wait_seconds)
        self._descriptor: int | None = None

    def __enter__(self) -> "_LeaseMutationGuard":
        flags = os.O_RDWR | os.O_CREAT
        flags |= getattr(os, "O_BINARY", 0)
        flags |= getattr(os, "O_CLOEXEC", 0)
        flags |= getattr(os, "O_NOFOLLOW", 0)
        try:
            descriptor = os.open(self._path, flags, 0o600)
        except OSError as exc:
            raise LeaseUnavailable(
                "the external-state lease mutation guard is unavailable"
            ) from exc

        try:
            self._validate_direct_guard(descriptor)
            deadline = time.monotonic() + self._wait_seconds
            while True:
                try:
                    self._try_lock(descriptor)
                    break
                except OSError as exc:
                    if time.monotonic() >= deadline:
                        raise LeaseUnavailable(
                            "another run is changing the external-state lease"
                        ) from exc
                    time.sleep(0.01)
        except Exception:
            os.close(descriptor)
            raise

        self._descriptor = descriptor
        return self

    def _validate_direct_guard(self, descriptor: int) -> None:
        try:
            opened = os.fstat(descriptor)
            current = self._path.lstat()
            reparse = is_reparse(self._path)
        except (ContractError, OSError) as exc:
            raise LeaseUnavailable(
                "the external-state lease mutation guard could not be verified"
            ) from exc
        if (
            not stat.S_ISREG(opened.st_mode)
            or not stat.S_ISREG(current.st_mode)
            or reparse
            or (opened.st_dev, opened.st_ino) != (current.st_dev, current.st_ino)
        ):
            raise LeaseUnavailable(
                "the external-state lease mutation guard is not a direct regular file"
            )

    @staticmethod
    def _try_lock(descriptor: int) -> None:
        if os.name == "nt":
            import msvcrt

            os.lseek(descriptor, 0, os.SEEK_SET)
            msvcrt.locking(descriptor, msvcrt.LK_NBLCK, 1)
            return

        import fcntl

        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def __exit__(self, exc_type: object, exc: object, traceback: object) -> None:
        descriptor = self._descriptor
        self._descriptor = None
        if descriptor is None:
            return
        try:
            os.close(descriptor)
        except OSError:
            # Closing a descriptor is the OS-backed unlock boundary. Do not
            # mask an operation result if the host reports a late close error.
            return


def run_pilot(
    config: PilotConfig,
    observation: Observation,
    state_path: Path | None,
    *,
    state_root: Path | None = None,
    runtime_enable: bool,
    now: datetime | None = None,
    cancellation_requested: Callable[[], bool] | None = None,
) -> dict[str, Any]:
    """Run one bounded pilot cycle.

    Unchanged exits before lease acquisition, state writes, model dispatch,
    worker dispatch, forking, or product mutation.
    """

    current_time = (now or datetime.now(timezone.utc)).astimezone(timezone.utc)
    cancelled = cancellation_requested or (lambda: False)
    if not config.enabled or not runtime_enable:
        return _unchanged_response(config.heartbeat_id, "pilot_disabled")
    _throw_if_cancelled(cancelled)

    if state_path is not None:
        if state_root is None:
            raise ContractError("an external state path requires an explicit approved state root")
        _, state_path = validate_direct_state_file(state_root, state_path)
    elif state_root is not None:
        raise ContractError("state_root is invalid without an external state path")

    state = load_state(state_path, config.heartbeat_id)
    if state["attentionLatched"]:
        return _attention_response(
            config.heartbeat_id,
            "failure_attention_latched",
            state.get("lastRevision"),
            state["retry"].get("nextAttemptAtUtc"),
        )
    retry_after = state["retry"].get("nextAttemptAtUtc")
    if retry_after is not None and current_time < parse_utc(
        retry_after,
        "state.retry.nextAttemptAtUtc",
    ):
        return _unchanged_response(
            config.heartbeat_id,
            "retry_not_due",
            state.get("lastRevision"),
            retry_after,
        )
    result = probe_revision(observation, state, config)
    if result.status == STATUS_UNCHANGED:
        response = _unchanged_response(
            config.heartbeat_id,
            result.reason,
            result.revision,
        )
        if len(canonical_json_bytes(response)) > 512:
            raise ContractError("unchanged response exceeds 512 bytes")
        return response

    # Report-only mode never acquires a lease or mutates external state.
    if config.mode == "report-only":
        return _report(
            config,
            result,
            current_time,
            _preview_state(config, state, result, current_time),
            persisted=False,
            lease_acquired=False,
        )

    if not config.allow_state_writes or state_path is None:
        raise ContractError("stateful pilot mode requires an approved external state path")
    _throw_if_cancelled(cancelled)

    try:
        lease = Lease(
            state_path,
            config.heartbeat_id,
            result.idempotency_key,
            config.lease_seconds,
            current_time,
        )
    except LeaseUnavailable:
        return _attention_response(
            config.heartbeat_id,
            "lease_unavailable",
            result.revision,
            None,
        )

    with lease:
        # Reload under the lease so a completed competing run is idempotent.
        state = load_state(state_path, config.heartbeat_id)
        if state["attentionLatched"]:
            return _attention_response(
                config.heartbeat_id,
                "failure_attention_latched",
                state.get("lastRevision"),
                state["retry"].get("nextAttemptAtUtc"),
                lease_acquired=True,
            )
        retry_after = state["retry"].get("nextAttemptAtUtc")
        if retry_after is not None and current_time < parse_utc(
            retry_after,
            "state.retry.nextAttemptAtUtc",
        ):
            # A competing run scheduled retry after our pre-lease read. Since
            # this invocation did acquire a lease, it must not claim the
            # zero-activity "unchanged" status.
            return _attention_response(
                config.heartbeat_id,
                "retry_not_due_after_lease",
                state.get("lastRevision"),
                retry_after,
                lease_acquired=True,
            )
        result = probe_revision(observation, state, config)
        if result.status == STATUS_UNCHANGED:
            # This run was initially changed and acquired a lease before a
            # competing run completed the same idempotency key. Never label a
            # response "unchanged" after lease activity.
            result = replace(
                result,
                status=STATUS_CHANGED,
                reason="idempotent_concurrent_completion",
            )
            return _report(
                config,
                result,
                current_time,
                state,
                persisted=False,
                lease_acquired=True,
            )
        _throw_if_cancelled(cancelled)

        result = _apply_failure_limit(config, state, result)
        updated = _updated_state(config, state, result, observation.cursor, current_time)
        # Validate the exact caller-visible report before advancing revision,
        # cursor, retry, or idempotency state. This prevents a size failure from
        # converting the next retry into a false unchanged result.
        report = _report(
            config,
            result,
            current_time,
            updated,
            persisted=True,
            lease_acquired=True,
        )
        _throw_if_cancelled(cancelled)
        atomic_write_json(state_path, updated, 65_536)
        # Recheck after the same-directory replacement. This detects a changed
        # path boundary even though portable Python cannot make the entire
        # Windows name lookup handle-relative.
        validate_direct_state_file(state_root, state_path)
        return report


def failure_response(
    heartbeat_id: str,
    reason: str,
    *,
    now: datetime | None = None,
) -> dict[str, Any]:
    """Create a bounded fail-closed response for pre-probe contract failures."""

    current_time = (now or datetime.now(timezone.utc)).astimezone(timezone.utc)
    return {
        "heartbeatId": heartbeat_id,
        "status": STATUS_FAILED,
        "reason": normalize_public_text(reason, "failure reason", 160),
        "observedAtUtc": format_utc(current_time),
        "modelDispatched": False,
        "workerDispatched": False,
        "knowledgeWritten": False,
        "leaseAcquired": False,
        "stateWritten": False,
        "parentTranscriptInherited": False,
    }


def _probe_result(
    heartbeat_id: str,
    status: str,
    revision: str,
    observation: Observation,
    reason: str,
    events: tuple[dict[str, Any], ...],
    *,
    failure: bool = False,
) -> ProbeResult:
    return ProbeResult(
        status=status,
        revision=revision,
        idempotency_key=_idempotency_key(heartbeat_id, revision, observation.cursor),
        reason=reason,
        events=events,
        observed_count=len(observation.deltas),
        cursor_advanced=False,
        failure=failure,
    )


def _idempotency_key(heartbeat_id: str, revision: str, cursor: str | None) -> str:
    return sha256_tag(
        canonical_json_bytes(
            {
                "heartbeatId": heartbeat_id,
                "revision": revision,
                "cursor": cursor,
            }
        )
    )


def _unchanged_response(
    heartbeat_id: str,
    reason: str,
    revision: str | None = None,
    retry_after_utc: str | None = None,
) -> dict[str, Any]:
    """Return the only status allowed to imply absolutely no run activity."""

    response: dict[str, Any] = {
        "heartbeatId": heartbeat_id,
        "status": STATUS_UNCHANGED,
        "reason": reason,
        "modelDispatched": False,
        "workerDispatched": False,
        "knowledgeWritten": False,
        "leaseAcquired": False,
        "stateWritten": False,
        "parentTranscriptInherited": False,
    }
    if revision is not None:
        response["revision"] = revision
    if retry_after_utc is not None:
        response["retryAfterUtc"] = retry_after_utc
    if len(canonical_json_bytes(response)) > 512:
        raise ContractError("unchanged response exceeds 512 bytes")
    return response


def _attention_response(
    heartbeat_id: str,
    reason: str,
    revision: str | None,
    retry_after_utc: str | None,
    *,
    lease_acquired: bool = False,
) -> dict[str, Any]:
    """Return a bounded fail-closed attention response with explicit activity."""

    response: dict[str, Any] = {
        "heartbeatId": heartbeat_id,
        "status": STATUS_ATTENTION,
        "reason": reason,
        "modelDispatched": False,
        "workerDispatched": False,
        "knowledgeWritten": False,
        "leaseAcquired": lease_acquired,
        "stateWritten": False,
        "parentTranscriptInherited": False,
    }
    if revision is not None:
        response["revision"] = revision
    if retry_after_utc is not None:
        response["retryAfterUtc"] = retry_after_utc
    return response


def _apply_failure_limit(
    config: PilotConfig,
    state: dict[str, Any],
    result: ProbeResult,
) -> ProbeResult:
    """Escalate the third consecutive failed probe to latched attention."""

    if not result.failure:
        return result
    previous_attempt = int(state["retry"]["attempt"])
    if previous_attempt + 1 < config.max_attempts:
        return result
    return replace(
        result,
        status=STATUS_ATTENTION,
        reason="failure_limit_reached",
    )


def _updated_state(
    config: PilotConfig,
    state: dict[str, Any],
    result: ProbeResult,
    cursor: str | None,
    now: datetime,
) -> dict[str, Any]:
    updated = read_json_value(state)
    metrics = updated["metrics"]
    metrics["runs"] += 1
    metrics[_metric_name(result.status)] += 1
    metrics["eventsObserved"] += result.observed_count
    metrics["eventsEmitted"] += len(result.events)

    retry_delay: int | None = None
    if result.status == STATUS_CHANGED:
        updated["lastRevision"] = result.revision
        updated["lastCursor"] = cursor
        completed = updated["completedIdempotencyKeys"]
        if result.idempotency_key not in completed:
            completed.append(result.idempotency_key)
        updated["completedIdempotencyKeys"] = completed[-64:]
        updated["retry"] = {"attempt": 0, "nextAttemptAtUtc": None}
        updated["attentionLatched"] = False
    elif result.failure:
        previous_attempt = int(updated["retry"].get("attempt", 0))
        attempt = min(previous_attempt + 1, config.max_attempts)
        retry_delay = _retry_delay(config, result.idempotency_key, attempt)
        updated["retry"] = {
            "attempt": attempt,
            "nextAttemptAtUtc": format_utc(now + timedelta(seconds=retry_delay)),
        }
        updated["attentionLatched"] = attempt >= config.max_attempts
    else:
        # A successful probe which needs operator attention breaks the
        # consecutive transport/source-failure streak.
        updated["retry"] = {"attempt": 0, "nextAttemptAtUtc": None}
        updated["attentionLatched"] = False

    receipt = _metrics_receipt(
        config,
        result,
        now,
        metrics,
        retry_delay,
        int(updated["retry"]["attempt"]),
    )
    updated["lastReceipt"] = receipt
    return seal_state(updated)


def _preview_state(
    config: PilotConfig,
    state: dict[str, Any],
    result: ProbeResult,
    now: datetime,
) -> dict[str, Any]:
    """Build an in-memory receipt without advancing persistent retry state."""

    preview = read_json_value(state)
    metrics = preview["metrics"]
    metrics["runs"] += 1
    metrics[_metric_name(result.status)] += 1
    metrics["eventsObserved"] += result.observed_count
    metrics["eventsEmitted"] += len(result.events)
    preview["lastReceipt"] = _metrics_receipt(
        config,
        result,
        now,
        metrics,
        None,
        int(preview["retry"]["attempt"]),
    )
    return preview


def _metrics_receipt(
    config: PilotConfig,
    result: ProbeResult,
    now: datetime,
    metrics: dict[str, int],
    retry_delay: int | None,
    consecutive_failures: int,
) -> dict[str, Any]:
    """Return content-free counters suitable for external persistence."""

    receipt = {
        "schemaVersion": 2,
        "heartbeatId": config.heartbeat_id,
        "receiptId": sha256_tag(
            canonical_json_bytes(
                {
                    "atUtc": format_utc(now),
                    "idempotencyKey": result.idempotency_key,
                    "status": result.status,
                }
            )
        ),
        "atUtc": format_utc(now),
        "status": result.status,
        "eventsObserved": result.observed_count,
        "eventsEmitted": len(result.events),
        "retryDelaySeconds": retry_delay,
        "consecutiveFailures": consecutive_failures,
        "totals": dict(metrics),
    }
    return receipt


def _report(
    config: PilotConfig,
    result: ProbeResult,
    now: datetime,
    state: dict[str, Any],
    *,
    persisted: bool,
    lease_acquired: bool,
) -> dict[str, Any]:
    report = {
        "heartbeatId": config.heartbeat_id,
        "status": result.status,
        "reason": result.reason,
        "revision": result.revision,
        "observedAtUtc": format_utc(now),
        "events": list(result.events),
        "cursorAdvanced": result.cursor_advanced,
        "leaseAcquired": lease_acquired,
        "stateWritten": persisted,
        "mutationExecuted": False,
        "modelDispatched": False,
        "workerDispatched": False,
        "knowledgeWritten": False,
        "parentTranscriptInherited": False,
        "contextPolicy": "validated-recovery-probe-brief-only",
        "metricsReceipt": state.get("lastReceipt"),
    }
    if len(canonical_json_bytes(report)) > config.maximum_report_bytes:
        raise ContractError("pilot report exceeds maximumReportBytes")
    return report


def _retry_delay(config: PilotConfig, idempotency_key: str, attempt: int) -> int:
    index = min(max(attempt, 1), 3) - 1
    # Configuration validation pins all jitter entries to zero so the retry
    # contract is exactly one, five, and fifteen minutes.
    return config.retry_base_seconds[index]


def _lease_id(state_path: Path, heartbeat_id: str, idempotency_key: str) -> str:
    normalized_state_path = (
        str(state_path).casefold() if state_path.drive else str(state_path)
    )
    return sha256_tag(
        canonical_json_bytes(
            {
                "heartbeatId": heartbeat_id,
                "idempotencyKey": idempotency_key,
                "statePathHash": sha256_tag(normalized_state_path.encode("utf-8")),
            }
        )
    )


def _seal_lease(value: dict[str, Any]) -> dict[str, Any]:
    captured = read_json_value(value)
    captured["leaseHash"] = ""
    material = dict(captured)
    material.pop("leaseHash")
    captured["leaseHash"] = sha256_tag(canonical_json_bytes(material))
    return captured


def _read_valid_lease(directory: Path, state_path: Path) -> dict[str, Any]:
    directory_stat = directory.lstat()
    if not stat.S_ISDIR(directory_stat.st_mode) or directory.is_symlink():
        raise ContractError("lease boundary must be a direct directory")
    children = tuple(directory.iterdir())
    metadata = directory / "lease.json"
    if children != (metadata,):
        raise ContractError("lease directory must contain exactly lease.json")
    metadata_stat = metadata.lstat()
    if not stat.S_ISREG(metadata_stat.st_mode) or metadata.is_symlink():
        raise ContractError("lease metadata must be a direct regular file")

    value = read_json_object(metadata, _MAXIMUM_LEASE_BYTES)
    if set(value) != _LEASE_FIELDS or value.get("schemaVersion") != 2:
        raise ContractError("lease fields do not match schema version 2")
    heartbeat_id = require_string(value.get("heartbeatId"), "lease.heartbeatId", 96)
    idempotency_key = require_string(
        value.get("idempotencyKey"),
        "lease.idempotencyKey",
        80,
    )
    if not _is_sha256(idempotency_key):
        raise ContractError("lease idempotencyKey is invalid")
    lease_id = value.get("leaseId")
    if (
        not _is_sha256(lease_id)
        or lease_id != _lease_id(state_path, heartbeat_id, idempotency_key)
    ):
        raise ContractError("lease identity does not match its state boundary")
    owner_pid = _require_integer(
        value.get("ownerPid"),
        "lease.ownerPid",
        1,
        2**63 - 1,
    )
    acquired_at = parse_utc(value.get("acquiredAtUtc"), "lease.acquiredAtUtc")
    expires_at = parse_utc(value.get("expiresAtUtc"), "lease.expiresAtUtc")
    if expires_at <= acquired_at or expires_at - acquired_at > timedelta(seconds=300):
        raise ContractError("lease duration is invalid")
    expected_owner = sha256_tag(
        canonical_json_bytes(
            {
                "leaseId": lease_id,
                "ownerPid": owner_pid,
                "startedAtUtc": format_utc(acquired_at),
            }
        )
    )
    if value.get("ownerId") != expected_owner:
        raise ContractError("lease owner identity does not match its metadata")
    if not _is_sha256(value.get("leaseHash")):
        raise ContractError("lease equality hash is invalid")
    if value["leaseHash"] != _seal_lease(value)["leaseHash"]:
        raise ContractError("lease equality hash does not match its content")
    return value


def _throw_if_cancelled(cancelled: Callable[[], bool]) -> None:
    if cancelled():
        raise CancellationRequested("pilot cancellation requested")


def _metric_name(status: str) -> str:
    if status == STATUS_ATTENTION:
        return "attentionRequired"
    return status


def _require_bool(value: Any, field_name: str) -> bool:
    if not isinstance(value, bool):
        raise ContractError(f"{field_name} must be a boolean")
    return value


def _require_integer(value: Any, field_name: str, minimum: int, maximum: int) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or not minimum <= value <= maximum:
        raise ContractError(f"{field_name} must be an integer from {minimum} to {maximum}")
    return value


def _require_integer_triplet(value: Any, field_name: str) -> tuple[int, int, int]:
    if (
        not isinstance(value, list)
        or len(value) != 3
        or any(not isinstance(item, int) or isinstance(item, bool) for item in value)
    ):
        raise ContractError(f"{field_name} must contain exactly three integers")
    return value[0], value[1], value[2]


def _optional_string(value: Any, field_name: str, maximum: int) -> str | None:
    if value is None:
        return None
    return require_string(value, field_name, maximum)
