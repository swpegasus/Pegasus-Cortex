#!/usr/bin/env python3
"""Fail-closed compatibility API/CLI for the retired DLL/profile package."""

from __future__ import annotations

import sys

RETIREMENT_CODE = "PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED"
RETIREMENT_MESSAGE = (
    f"{RETIREMENT_CODE}: The framework-dependent DLL/profile package and its "
    "validator were removed. Use tools/Build-PegasusCortexRelease.ps1 and "
    "the normal unified Setup package; clients attach to the existing shared "
    "Pegasus Cortex MCP owner. No inputs were inspected or changes made."
)


def verify_package(
    package_path: object,
    source_root: object,
    *,
    run_smoke: bool,
    dotnet_path: object | None = None,
    build_result_path: object | None = None,
) -> dict[str, object]:
    raise RuntimeError(RETIREMENT_MESSAGE)


def main() -> int:
    print(RETIREMENT_MESSAGE, file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
