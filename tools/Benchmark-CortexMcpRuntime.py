#!/usr/bin/env python3
"""Measure one Cortex MCP runtime and an optional exact file-bundle baseline."""

from __future__ import annotations

import argparse
import json
import math
import os
import pathlib
import platform
import re
import statistics
import subprocess
import sys
import threading
import time
from typing import Any

try:
    import psutil  # type: ignore[import-not-found]
except ImportError:
    psutil = None


PROTOCOL_VERSION = "2025-11-25"
QUERY = "Pegasus Cortex product boundary"


def percentile(values: list[float], quantile: float) -> float:
    ordered = sorted(values)
    if not ordered:
        raise ValueError("Cannot calculate a percentile for an empty sample.")
    position = (len(ordered) - 1) * quantile
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def describe(values: list[float]) -> dict[str, float | int]:
    return {
        "samples": len(values),
        "minimum": min(values),
        "p50": statistics.median(values),
        "p95": percentile(values, 0.95),
        "mean": statistics.fmean(values),
        "maximum": max(values),
    }


def request(
    request_id: int,
    method: str,
    params: dict[str, Any] | None = None,
) -> dict[str, Any]:
    value: dict[str, Any] = {"jsonrpc": "2.0", "id": request_id, "method": method}
    if params is not None:
        value["params"] = params
    return value


def read_linux_process(pid: int) -> dict[str, float | int]:
    status: dict[str, str] = {}
    for line in pathlib.Path(f"/proc/{pid}/status").read_text(encoding="utf-8").splitlines():
        if ":" in line:
            key, value = line.split(":", 1)
            status[key] = value.strip()

    stat_fields = pathlib.Path(f"/proc/{pid}/stat").read_text(encoding="utf-8").split()
    ticks = os.sysconf("SC_CLK_TCK")

    def kibibytes(name: str) -> int:
        value = status.get(name, "0 kB").split()[0]
        return int(value) * 1024

    return {
        "rssBytes": kibibytes("VmRSS"),
        "peakRssBytes": kibibytes("VmHWM"),
        "privateBytes": kibibytes("VmData"),
        "threads": int(status.get("Threads", "0")),
        "handlesOrFileDescriptors": len(list(pathlib.Path(f"/proc/{pid}/fd").iterdir())),
        "cpuSeconds": (int(stat_fields[13]) + int(stat_fields[14])) / ticks,
    }


def read_process(pid: int) -> dict[str, float | int]:
    if psutil is not None:
        process = psutil.Process(pid)
        memory = process.memory_info()
        full_memory = None
        try:
            full_memory = process.memory_full_info()
        except (psutil.AccessDenied, AttributeError):
            pass
        cpu = process.cpu_times()
        handle_count = (
            process.num_handles()
            if hasattr(process, "num_handles")
            else process.num_fds()
        )
        return {
            "rssBytes": int(memory.rss),
            "peakRssBytes": int(getattr(memory, "peak_wset", memory.rss)),
            "privateBytes": int(
                getattr(full_memory, "uss", getattr(memory, "private", memory.rss))
            ),
            "threads": process.num_threads(),
            "handlesOrFileDescriptors": handle_count,
            "cpuSeconds": float(cpu.user + cpu.system),
        }
    if sys.platform.startswith("linux"):
        return read_linux_process(pid)
    raise RuntimeError("Process resource sampling requires psutil on this platform.")


class ProcessSampler:
    """Collect bounded peak and CPU observations for one MCP process."""

    def __init__(self, pid: int, interval_seconds: float = 0.005) -> None:
        self._pid = pid
        self._interval_seconds = interval_seconds
        self._stop = threading.Event()
        self._samples: list[dict[str, float | int]] = []
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def _run(self) -> None:
        while not self._stop.is_set():
            try:
                self._samples.append(read_process(self._pid))
            except (FileNotFoundError, ProcessLookupError):
                break
            except Exception:
                # Sampling must never affect the protocol workload.
                pass
            self._stop.wait(self._interval_seconds)

    def finish(self) -> dict[str, float | int | None]:
        self._stop.set()
        self._thread.join(timeout=1)
        try:
            self._samples.append(read_process(self._pid))
        except Exception:
            pass
        if not self._samples:
            return {
                "sampleCount": 0,
                "peakRssBytes": None,
                "peakPrivateBytes": None,
                "peakThreads": None,
                "peakHandlesOrFileDescriptors": None,
                "cpuSeconds": None,
            }
        cpu_values = [float(sample["cpuSeconds"]) for sample in self._samples]
        return {
            "sampleCount": len(self._samples),
            "peakRssBytes": max(int(sample["peakRssBytes"]) for sample in self._samples),
            "peakPrivateBytes": max(int(sample["privateBytes"]) for sample in self._samples),
            "peakThreads": max(int(sample["threads"]) for sample in self._samples),
            "peakHandlesOrFileDescriptors": max(
                int(sample["handlesOrFileDescriptors"]) for sample in self._samples
            ),
            "cpuSeconds": max(cpu_values) - min(cpu_values),
        }


class McpSession:
    """Minimal persistent newline-delimited JSON-RPC client."""

    def __init__(self, command: list[str]) -> None:
        self._next_id = 1
        self._stderr: list[bytes] = []
        self.process = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            bufsize=0,
        )
        if self.process.stdin is None or self.process.stdout is None or self.process.stderr is None:
            raise RuntimeError("MCP process pipes were not created.")
        self._stderr_thread = threading.Thread(target=self._read_stderr, daemon=True)
        self._stderr_thread.start()
        self.sampler = ProcessSampler(self.process.pid)

    def _read_stderr(self) -> None:
        assert self.process.stderr is not None
        while True:
            block = self.process.stderr.read(4096)
            if not block:
                break
            if sum(len(item) for item in self._stderr) < 65536:
                self._stderr.append(block)

    def call(
        self,
        method: str,
        params: dict[str, Any] | None = None,
    ) -> tuple[dict[str, Any], float, int]:
        request_id = self._next_id
        self._next_id += 1
        payload = json.dumps(
            request(request_id, method, params),
            separators=(",", ":"),
        ).encode("utf-8") + b"\n"
        assert self.process.stdin is not None
        assert self.process.stdout is not None
        started = time.perf_counter_ns()
        self.process.stdin.write(payload)
        self.process.stdin.flush()
        raw = self.process.stdout.readline()
        elapsed_ms = (time.perf_counter_ns() - started) / 1_000_000
        if not raw:
            raise RuntimeError(
                f"MCP process ended before response; exit={self.process.poll()}; "
                f"stderr={b''.join(self._stderr)[:1000]!r}"
            )
        response = json.loads(raw)
        if response.get("id") != request_id:
            raise RuntimeError("MCP response ID did not match its request.")
        if "error" in response:
            raise RuntimeError(f"MCP request failed: {response['error']!r}")
        result = response.get("result")
        if not isinstance(result, dict):
            raise RuntimeError("MCP response result was not an object.")
        return result, elapsed_ms, len(raw)

    def notify(self, method: str, params: dict[str, Any] | None = None) -> None:
        value: dict[str, Any] = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            value["params"] = params
        payload = json.dumps(value, separators=(",", ":")).encode("utf-8") + b"\n"
        assert self.process.stdin is not None
        self.process.stdin.write(payload)
        self.process.stdin.flush()

    def initialize(self) -> tuple[dict[str, Any], float, int]:
        result, elapsed, size = self.call(
            "initialize",
            {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {},
                "clientInfo": {"name": "cortex-platform-benchmark", "version": "1"},
            },
        )
        if result.get("protocolVersion") != PROTOCOL_VERSION:
            raise RuntimeError("MCP protocol negotiation changed.")
        self.notify("notifications/initialized", {})
        return result, elapsed, size

    def tool(
        self,
        name: str,
        arguments: dict[str, Any],
    ) -> tuple[dict[str, Any], float, int]:
        result, elapsed, size = self.call(
            "tools/call",
            {"name": name, "arguments": arguments},
        )
        structured = result.get("structuredContent")
        if not isinstance(structured, dict):
            raise RuntimeError(f"Tool {name} returned no structured content.")
        return structured, elapsed, size

    def close(self) -> tuple[dict[str, float | int | None], str, int]:
        resources = self.sampler.finish()
        if self.process.stdin is not None:
            self.process.stdin.close()
        try:
            exit_code = self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            try:
                exit_code = self.process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                self.process.kill()
                exit_code = self.process.wait(timeout=2)
        self._stderr_thread.join(timeout=1)
        stderr = b"".join(self._stderr).decode("utf-8", errors="replace")
        return resources, stderr, exit_code


def run_cold(command: list[str], count: int) -> dict[str, Any]:
    elapsed: list[float] = []
    rss: list[float] = []
    cpu: list[float] = []
    for _ in range(count):
        started = time.perf_counter_ns()
        session = McpSession(command)
        session.initialize()
        status, _, _ = session.tool("cortex_status", {})
        if status.get("status") != "ready":
            raise RuntimeError("Cold Cortex status was not ready.")
        resources, stderr, exit_code = session.close()
        elapsed.append((time.perf_counter_ns() - started) / 1_000_000)
        if stderr or exit_code != 0:
            raise RuntimeError(f"Cold MCP exit={exit_code}; stderr={stderr[:1000]!r}")
        if resources["peakRssBytes"] is not None:
            rss.append(float(resources["peakRssBytes"]))
        if resources["cpuSeconds"] is not None:
            cpu.append(float(resources["cpuSeconds"]))
    return {
        "launchInitializeStatusExitMilliseconds": describe(elapsed),
        "peakRssBytes": describe(rss) if rss else None,
        "cpuSeconds": describe(cpu) if cpu else None,
    }


def measure_calls(
    count: int,
    operation: Any,
) -> tuple[dict[str, Any], list[int], Any]:
    elapsed: list[float] = []
    sizes: list[int] = []
    last_result: Any = None
    for _ in range(count):
        last_result, duration, payload_bytes = operation()
        elapsed.append(duration)
        sizes.append(payload_bytes)
    return describe(elapsed), sizes, last_result


def run_warm(
    command: list[str],
    status_runs: int,
    search_runs: int,
    read_runs: int,
) -> dict[str, Any]:
    session = McpSession(command)
    initialized, initialize_ms, initialize_bytes = session.initialize()
    tools, tools_ms, tools_bytes = session.call("tools/list", {})
    resources, resources_ms, resources_bytes = session.call("resources/list", {})
    discovery, discover_ms, discover_bytes = session.tool("cortex_discover", {})
    status, status_ms, status_bytes = session.tool("cortex_status", {})
    search_arguments = {
        "text": QUERY,
        "projection": "compact",
        "includeContent": True,
        "limit": 1,
    }
    search, search_ms, search_bytes = session.tool("cortex_search", search_arguments)
    records = search.get("records")
    if not isinstance(records, list) or len(records) != 1:
        raise RuntimeError("Matched Cortex search did not return exactly one record.")
    record_id = records[0].get("recordId")
    content_hash = records[0].get("contentHash")
    if not isinstance(record_id, str) or not isinstance(content_hash, str):
        raise RuntimeError("Matched Cortex record identity was incomplete.")
    read_arguments = {"id": record_id, "projection": "compact"}
    exact, read_ms, read_bytes = session.tool("cortex_read", read_arguments)
    if exact.get("recordId") != record_id or exact.get("contentHash") != content_hash:
        raise RuntimeError("Matched Cortex exact read disagreed with search.")

    for _ in range(10):
        session.tool("cortex_status", {})
        session.tool("cortex_search", search_arguments)
        session.tool("cortex_read", read_arguments)

    workload_resource_before = read_process(session.process.pid)
    workload_started = time.perf_counter()
    status_timing, status_sizes, _ = measure_calls(
        status_runs,
        lambda: session.tool("cortex_status", {}),
    )
    search_timing, search_sizes, final_search = measure_calls(
        search_runs,
        lambda: session.tool("cortex_search", search_arguments),
    )
    read_timing, read_sizes, final_read = measure_calls(
        read_runs,
        lambda: session.tool("cortex_read", read_arguments),
    )
    workload_seconds = time.perf_counter() - workload_started
    workload_resource_after = read_process(session.process.pid)
    process_resources, stderr, exit_code = session.close()
    if stderr or exit_code != 0:
        raise RuntimeError(f"Warm MCP exit={exit_code}; stderr={stderr[:1000]!r}")

    workload_cpu_seconds = max(
        0.0,
        float(workload_resource_after["cpuSeconds"])
        - float(workload_resource_before["cpuSeconds"]),
    )
    process_resources["workloadWallSeconds"] = workload_seconds
    process_resources["workloadCpuSeconds"] = workload_cpu_seconds
    process_resources["oneCoreCpuUtilizationPercent"] = (
        workload_cpu_seconds / workload_seconds * 100 if workload_seconds > 0 else None
    )

    resource_items = resources.get("resources")
    tool_items = tools.get("tools")
    return {
        "identity": {
            "status": status,
            "recordId": record_id,
            "contentHash": content_hash,
            "exactReadMatched": final_read.get("recordId") == record_id,
            "finalSearchMatched": (
                isinstance(final_search.get("records"), list)
                and final_search["records"][0].get("recordId") == record_id
            ),
        },
        "surface": {
            "protocolVersion": initialized.get("protocolVersion"),
            "toolCount": len(tool_items) if isinstance(tool_items, list) else None,
            "resourceCount": (
                len(resource_items) if isinstance(resource_items, list) else None
            ),
            "access": discovery.get("access"),
        },
        "firstCalls": {
            "initialize": {"milliseconds": initialize_ms, "responseBytes": initialize_bytes},
            "toolsList": {"milliseconds": tools_ms, "responseBytes": tools_bytes},
            "resourcesList": {
                "milliseconds": resources_ms,
                "responseBytes": resources_bytes,
            },
            "discover": {"milliseconds": discover_ms, "responseBytes": discover_bytes},
            "status": {"milliseconds": status_ms, "responseBytes": status_bytes},
            "search": {"milliseconds": search_ms, "responseBytes": search_bytes},
            "read": {"milliseconds": read_ms, "responseBytes": read_bytes},
        },
        "measured": {
            "status": {
                "milliseconds": status_timing,
                "responseBytes": describe([float(value) for value in status_sizes]),
            },
            "search": {
                "milliseconds": search_timing,
                "responseBytes": describe([float(value) for value in search_sizes]),
            },
            "read": {
                "milliseconds": read_timing,
                "responseBytes": describe([float(value) for value in read_sizes]),
            },
        },
        "processResources": process_resources,
    }


def file_search(records: list[dict[str, Any]], query: str) -> list[dict[str, Any]]:
    terms = re.findall(r"[a-z0-9]+", query.casefold())
    ranked: list[tuple[int, str, dict[str, Any]]] = []
    for record in records:
        title = str(record.get("title", "")).casefold()
        summary = str(record.get("summary", "")).casefold()
        content = str(record.get("content", "")).casefold()
        slug = str(record.get("slug", "")).casefold()
        keywords = " ".join(str(value) for value in record.get("keywords", [])).casefold()
        score = sum(
            title.count(term) * 8
            + slug.count(term) * 6
            + summary.count(term) * 4
            + keywords.count(term) * 3
            + content.count(term)
            for term in terms
        )
        if score > 0:
            ranked.append((score, str(record.get("recordId")), record))
    ranked.sort(key=lambda item: (-item[0], item[1]))
    return [item[2] for item in ranked]


def compact_file_projection(record: dict[str, Any]) -> dict[str, Any]:
    return {
        "recordId": record.get("recordId"),
        "revisionId": record.get("revisionId"),
        "namespace": record.get("namespace"),
        "taxonomy": record.get("taxonomy"),
        "title": record.get("title"),
        "summary": record.get("summary"),
        "content": record.get("content"),
        "contentHash": record.get("contentHash"),
        "freshness": record.get("freshness"),
        "contentTreatment": record.get("contentTreatment"),
        "executable": record.get("executable"),
    }


def run_file_baseline(
    seed_path: pathlib.Path,
    cold_runs: int,
    search_runs: int,
    read_runs: int,
) -> dict[str, Any]:
    seed_bytes = seed_path.read_bytes()
    cold: list[float] = []
    cold_top_ids: list[str] = []
    for _ in range(cold_runs):
        started = time.perf_counter_ns()
        bundle = json.loads(seed_path.read_text(encoding="utf-8"))
        ranked = file_search(bundle["records"], QUERY)
        cold.append((time.perf_counter_ns() - started) / 1_000_000)
        cold_top_ids.append(ranked[0]["recordId"])

    rss_before = read_process(os.getpid())["rssBytes"]
    bundle = json.loads(seed_bytes)
    records = bundle["records"]
    by_id = {record["recordId"]: record for record in records}
    rss_after_load = read_process(os.getpid())["rssBytes"]

    search_elapsed: list[float] = []
    search_payload_sizes: list[float] = []
    top_record: dict[str, Any] | None = None
    for _ in range(search_runs):
        started = time.perf_counter_ns()
        top_record = file_search(records, QUERY)[0]
        projected = compact_file_projection(top_record)
        search_elapsed.append((time.perf_counter_ns() - started) / 1_000_000)
        search_payload_sizes.append(
            float(len(json.dumps(projected, separators=(",", ":")).encode("utf-8")))
        )
    if top_record is None:
        raise RuntimeError("File baseline did not execute a search.")

    read_elapsed: list[float] = []
    read_payload_sizes: list[float] = []
    for _ in range(read_runs):
        started = time.perf_counter_ns()
        projected = compact_file_projection(by_id[top_record["recordId"]])
        read_elapsed.append((time.perf_counter_ns() - started) / 1_000_000)
        read_payload_sizes.append(
            float(len(json.dumps(projected, separators=(",", ":")).encode("utf-8")))
        )

    return {
        "source": {
            "path": str(seed_path),
            "bytes": len(seed_bytes),
            "bundleId": bundle.get("bundleId"),
            "bundleHash": bundle.get("bundleHash"),
            "recordCount": len(records),
        },
        "method": (
            "Direct JSON file open/parse plus a small deterministic in-process "
            "weighted lexical scan. It has no MCP framing, startup validation, "
            "authorization metadata, or cross-process boundary."
        ),
        "identity": {
            "recordId": top_record.get("recordId"),
            "contentHash": top_record.get("contentHash"),
            "allColdRunsSameTopRecord": len(set(cold_top_ids)) == 1,
        },
        "coldOpenParseSearchMilliseconds": describe(cold),
        "warmSearchMilliseconds": describe(search_elapsed),
        "warmReadMilliseconds": describe(read_elapsed),
        "searchPayloadBytes": describe(search_payload_sizes),
        "readPayloadBytes": describe(read_payload_sizes),
        "residentSetIncreaseAfterBundleLoadBytes": max(
            0,
            int(rss_after_load) - int(rss_before),
        ),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--label", required=True)
    launch = parser.add_mutually_exclusive_group(required=True)
    launch.add_argument("--command-json")
    launch.add_argument("--executable")
    parser.add_argument("--argument", action="append", default=[])
    parser.add_argument("--seed", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path)
    parser.add_argument("--cold-runs", type=int, default=12)
    parser.add_argument("--status-runs", type=int, default=1000)
    parser.add_argument("--search-runs", type=int, default=2000)
    parser.add_argument("--read-runs", type=int, default=5000)
    args = parser.parse_args()

    command = (
        json.loads(args.command_json)
        if args.command_json is not None
        else [args.executable, *args.argument]
    )
    if not isinstance(command, list) or not command or not all(
        isinstance(value, str) and value for value in command
    ):
        raise RuntimeError("--command-json must be a non-empty JSON string array.")
    for count in (args.cold_runs, args.status_runs, args.search_runs, args.read_runs):
        if count < 1 or count > 100000:
            raise RuntimeError("Benchmark counts must be between 1 and 100000.")

    result: dict[str, Any] = {
        "schemaVersion": 1,
        "label": args.label,
        "observedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "platform": {
            "system": platform.system(),
            "release": platform.release(),
            "machine": platform.machine(),
            "python": platform.python_version(),
            "processorCount": os.cpu_count(),
        },
        "workload": {
            "query": QUERY,
            "coldRuns": args.cold_runs,
            "statusRuns": args.status_runs,
            "searchRuns": args.search_runs,
            "readRuns": args.read_runs,
        },
        "mcp": {
            "cold": run_cold(command, args.cold_runs),
            "warm": run_warm(
                command,
                args.status_runs,
                args.search_runs,
                args.read_runs,
            ),
        },
    }
    if args.seed is not None:
        result["fileBaseline"] = run_file_baseline(
            args.seed.resolve(strict=True),
            args.cold_runs,
            args.search_runs,
            args.read_runs,
        )

    encoded = json.dumps(result, indent=2, sort_keys=True) + "\n"
    if args.output is not None:
        output = args.output.resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(encoded, encoding="utf-8", newline="\n")
    sys.stdout.write(encoded)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exception:
        print(
            f"Cortex benchmark failed: {type(exception).__name__}: {exception}",
            file=sys.stderr,
        )
        raise SystemExit(1)
