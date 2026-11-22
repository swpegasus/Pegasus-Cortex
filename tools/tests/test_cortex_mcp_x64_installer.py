"""Static guards for retired profile install and its current V2 compatibility entry."""
from __future__ import annotations

import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]


class LegacyInstallerRetirementTests(unittest.TestCase):
    def test_profile_installer_refuses_before_any_input_or_machine_effect(self) -> None:
        source = (ROOT / "deploy/x64/Install-PegasusCortexX64.ps1").read_text(encoding="utf-8")
        match = re.fullmatch(
            r"(?:#[^\n]*\n|\s)*\[CmdletBinding\(\)\]\s*param\(\s*"
            r"(?:\[(?:string|switch)\]\$\w+(?:\s*=\s*'[^']*')?\s*,?\s*)+"
            r"\)\s*throw \[InvalidOperationException\]::new\(\s*'([^']+)'\)\s*",
            source,
        )
        self.assertIsNotNone(match, "Installer must contain only parameters and its unconditional retirement error.")
        assert match is not None
        self.assertIn("PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED", match.group(1))
        self.assertIn("normal unified Setup", match.group(1))

    def test_command_compatibility_entry_still_targets_v2(self) -> None:
        source = (ROOT / "deploy/x64/Install-PegasusCortexX64.cmd").read_text(encoding="utf-8")
        self.assertIn('"%~dp0Install-PegasusCortexV2X64.ps1" %*', source)
        self.assertNotIn('"%~dp0Install-PegasusCortexX64.ps1"', source)

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
