"""Static guards for retired profile install and the tracked unified release route."""
from __future__ import annotations

import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
RETIRED_PS = re.compile(
    r"\A(?:#[^\n]*\n|\s)*\[CmdletBinding\(\)\]\s*param\(\s*"
    r"(?:\[(?:string|switch)\]\$\w+(?:\s*=\s*'[^']*')?\s*,?\s*)+"
    r"\)\s*throw \[InvalidOperationException\]::new\(\s*'([^']+)'\)\s*\Z"
)


class LegacyInstallerRetirementTests(unittest.TestCase):
    def test_profile_installer_refuses_before_any_input_or_machine_effect(self) -> None:
        source = (ROOT / "deploy/x64/Install-PegasusCortexX64.ps1").read_text(encoding="utf-8")
        match = RETIRED_PS.fullmatch(source)
        self.assertIsNotNone(match, "Installer must contain only parameters and its unconditional retirement error.")
        assert match is not None
        self.assertIn("PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED", match.group(1))
        self.assertIn("normal unified Setup", match.group(1))

    def test_retired_entry_points_to_the_tracked_unified_release_route(self) -> None:
        source = (ROOT / "deploy/x64/Install-PegasusCortexX64.ps1").read_text(encoding="utf-8")
        self.assertIn("tools/Build-PegasusCortexRelease.ps1", source)
        self.assertNotIn("Install-PegasusCortexX64.cmd", source)
        builder = (ROOT / "tools/Build-PegasusCortexRelease.ps1").read_text(encoding="utf-8")
        self.assertIn("New-PegasusCortexUnifiedProduct.ps1", builder)
        self.assertIn("New-PegasusCortexNsisInstaller.ps1", builder)

    def test_operational_installer_is_only_an_effect_free_retirement_stub(self) -> None:
        source = (ROOT / "deploy/x64/Install-PegasusCortexOperationalX64.ps1").read_text(encoding="utf-8")
        match = RETIRED_PS.fullmatch(source)
        self.assertIsNotNone(match, "Operational installer must contain only parameters and its retirement error.")
        assert match is not None
        self.assertIn("PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED", match.group(1))
        self.assertIn("tools/Build-PegasusCortexRelease.ps1", match.group(1))

    def test_guidance_attaches_to_shared_owner_without_fixed_alias_or_bootstrap(self) -> None:
        plugin = ROOT / "integrations/codex/pegasus-cortex-client"
        for name in ("pegasus-cortex-pao.v1.md", "pegasus-cortex-agents.v2.md"):
            source = (plugin / "guidance" / name).read_text(encoding="utf-8")
            self.assertIn("shared Pegasus Cortex MCP connection", source)
            self.assertIn("loopback by default", source)
            self.assertNotIn("pegasus_cortex", source)
        source = (plugin / "skills/pegasus-cortex-client/SKILL.md").read_text(encoding="utf-8")
        transport = source.split("## Transport and process cost", 1)[1].split("\n## ", 1)[0]
        self.assertIn("does not launch a separate adapter", transport)
        self.assertIn("skills and guidance only", transport)
        self.assertNotIn("PegasusCortex.exe", transport)


if __name__ == "__main__":
    unittest.main()
