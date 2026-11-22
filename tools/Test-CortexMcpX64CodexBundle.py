#!/usr/bin/env python3
"""Retired standalone Codex bundle verifier; never reads or starts a payload."""

RETIREMENT_MESSAGE = (
    "PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED: The standalone Codex bundle "
    "and private-runtime smoke were removed. Use the normal unified Setup "
    "package and its verification; clients attach to the existing shared "
    "Pegasus Cortex MCP owner. No inputs were inspected or changes made."
)


def main() -> int:
    raise SystemExit(RETIREMENT_MESSAGE)


if __name__ == "__main__":
    raise SystemExit(main())
