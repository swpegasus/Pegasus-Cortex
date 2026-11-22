"""Command-line entry points for the disabled local Heartbeat v2 pilot."""

from __future__ import annotations

import argparse
import signal
import sys
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from .common import (
    CancellationRequested,
    ContractError,
    canonical_json_bytes,
    parse_utc,
    read_json_object,
)
from .heartbeat import (
    PilotConfig,
    Observation,
    STATUS_ATTENTION,
    STATUS_FAILED,
    failure_response,
    run_pilot,
)
from .recovery_probe_brief import (
    RECOVERY_PROBE_BRIEF_RELATIVE_PATH,
    build_recovery_probe_brief,
    validate_recovery_probe_brief,
    write_recovery_probe_brief,
)


def main(argv: list[str] | None = None) -> int:
    parser = _parser()
    arguments = parser.parse_args(argv)
    cancellation = threading.Event()
    _register_cancellation(cancellation)

    try:
        if arguments.command == "probe":
            return _probe(arguments, cancellation)
        if arguments.command == "recovery-probe-brief-generate":
            return _recovery_probe_brief_generate(arguments)
        if arguments.command == "recovery-probe-brief-validate":
            return _recovery_probe_brief_validate(arguments)
        raise ContractError("unknown command")
    except CancellationRequested as exc:
        _emit(failure_response("heartbeat-v2-local-pilot", str(exc)))
        return 130
    except ContractError as exc:
        _emit(failure_response("heartbeat-v2-local-pilot", str(exc)))
        return 2


def _probe(arguments: argparse.Namespace, cancellation: threading.Event) -> int:
    config = PilotConfig.from_dict(read_json_object(arguments.config, 16_384))
    observation = Observation.from_dict(
        read_json_object(arguments.observation, 131_072),
        config,
    )
    response = run_pilot(
        config,
        observation,
        arguments.state,
        state_root=arguments.state_root,
        runtime_enable=arguments.enable_pilot,
        cancellation_requested=cancellation.is_set,
    )
    _emit(response)
    if response["status"] == STATUS_FAILED:
        return 2
    if response["status"] == STATUS_ATTENTION:
        return 3
    return 0


def _recovery_probe_brief_generate(arguments: argparse.Namespace) -> int:
    if not arguments.write:
        raise ContractError(
            "recovery-probe brief generation requires the explicit --write gate"
        )
    generated = parse_utc(arguments.generated_at_utc, "generated-at-utc")
    output = arguments.project_root / RECOVERY_PROBE_BRIEF_RELATIVE_PATH
    brief = build_recovery_probe_brief(
        arguments.project_root,
        generated_at_utc=generated,
        max_age_seconds=arguments.max_age_seconds,
    )
    byte_count = write_recovery_probe_brief(
        arguments.project_root,
        output,
        brief,
    )
    _emit(
        {
            "status": "generated",
            "contextSetId": brief["contextSetId"],
            "bytes": byte_count,
            "path": RECOVERY_PROBE_BRIEF_RELATIVE_PATH.as_posix(),
        }
    )
    return 0


def _recovery_probe_brief_validate(arguments: argparse.Namespace) -> int:
    now = (
        parse_utc(arguments.now_utc, "now-utc")
        if arguments.now_utc
        else datetime.now(timezone.utc)
    )
    result = validate_recovery_probe_brief(
        arguments.project_root,
        arguments.project_root / RECOVERY_PROBE_BRIEF_RELATIVE_PATH,
        now_utc=now,
    )
    _emit(result)
    return 0


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Disabled-by-default local Heartbeat v2 pilot"
    )
    commands = parser.add_subparsers(dest="command", required=True)

    probe = commands.add_parser("probe", help="classify one local coordination snapshot")
    probe.add_argument("--config", type=Path, required=True)
    probe.add_argument("--observation", type=Path, required=True)
    probe.add_argument("--state", type=Path)
    probe.add_argument(
        "--state-root",
        type=Path,
        help="explicit private root; required whenever --state is supplied",
    )
    probe.add_argument(
        "--enable-pilot",
        action="store_true",
        help="second opt-in gate; config.enabled must also be true",
    )

    generate = commands.add_parser(
        "recovery-probe-brief-generate",
        help="generate the internal disposable recovery-probe brief",
    )
    generate.add_argument("--project-root", type=Path, required=True)
    generate.add_argument(
        "--generated-at-utc",
        required=True,
        help="explicit UTC timestamp used to make generation reproducible",
    )
    generate.add_argument("--max-age-seconds", type=int, default=86_400)
    generate.add_argument("--write", action="store_true")

    validate = commands.add_parser(
        "recovery-probe-brief-validate",
        help="fail closed if the internal recovery-probe brief is stale or mismatched",
    )
    validate.add_argument("--project-root", type=Path, required=True)
    validate.add_argument("--now-utc")
    return parser


def _register_cancellation(cancellation: threading.Event) -> None:
    def request_cancellation(signum: int, frame: Any) -> None:
        cancellation.set()

    signal.signal(signal.SIGINT, request_cancellation)
    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, request_cancellation)


def _emit(value: dict[str, Any]) -> None:
    sys.stdout.buffer.write(canonical_json_bytes(value) + b"\n")
    sys.stdout.buffer.flush()


if __name__ == "__main__":
    raise SystemExit(main())
