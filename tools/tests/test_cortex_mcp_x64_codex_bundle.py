"""Static retirement guards; never execute a bundler, payload or private runtime."""
from __future__ import annotations

import ast
import json
import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
PLUGIN = ROOT / "integrations/codex/pegasus-cortex-client"
RETIRED_PS = re.compile(
    r"\A(?:#[^\n]*\n|\s)*\[CmdletBinding\(\)\]\s*param\(\s*"
    r"(?:\[(?:string|switch)\]\$\w+(?:\s*=\s*'[^']*')?\s*,?\s*)+"
    r"\)\s*throw \[InvalidOperationException\]::new\(\s*'([^']+)'\)\s*\Z"
)


class CodexBundleRetirementTests(unittest.TestCase):
    def test_builder_only_accepts_legacy_arguments_and_refuses_without_effects(self) -> None:
        source = (ROOT / "tools/New-CortexMcpX64CodexBundle.ps1").read_text(encoding="utf-8")
        match = RETIRED_PS.fullmatch(source)
        self.assertIsNotNone(match, "Builder must contain only parameters and its unconditional retirement error.")
        assert match is not None
        self.assertIn("PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED", match.group(1))
        self.assertIn("New-PegasusCortexUnifiedProduct.ps1", match.group(1))

    def test_verifier_has_no_archive_process_or_import_effect(self) -> None:
        source = (ROOT / "tools/Test-CortexMcpX64CodexBundle.py").read_text(encoding="utf-8")
        module = ast.parse(source)
        self.assertFalse(any(isinstance(node, (ast.Import, ast.ImportFrom)) for node in ast.walk(module)))
        functions = [node for node in module.body if isinstance(node, ast.FunctionDef)]
        self.assertEqual([node.name for node in functions], ["main"])
        self.assertEqual(len(functions[0].body), 1)
        self.assertIsInstance(functions[0].body[0], ast.Raise)
        for node in ast.walk(module):
            if isinstance(node, ast.Call):
                self.assertIsInstance(node.func, ast.Name)
                self.assertIn(node.func.id, {"SystemExit", "main"})
        constants = [node for node in module.body if isinstance(node, ast.Assign)]
        self.assertEqual(len(constants), 1)
        self.assertIn("PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED", ast.literal_eval(constants[0].value))

    def test_skills_plugin_cannot_register_another_connector(self) -> None:
        manifest = json.loads((PLUGIN / ".codex-plugin/plugin.json").read_text(encoding="utf-8"))
        self.assertNotIn("mcpServers", manifest)
        self.assertNotIn("MCP", manifest["interface"]["capabilities"])
        self.assertFalse((PLUGIN / ".mcp.json").exists())
        self.assertEqual(manifest["skills"], "./skills/")


if __name__ == "__main__":
    unittest.main()
