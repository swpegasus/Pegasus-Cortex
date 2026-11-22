"""Offline guards for the retired DLL/profile builder and validator."""
from __future__ import annotations

import importlib.util
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
TOOL_PATH = ROOT / "tools/Test-CortexMcpX64Package.py"
BUILDER_PATH = ROOT / "tools/New-CortexMcpX64Package.ps1"
SPEC = importlib.util.spec_from_file_location("cortex_x64_verifier", TOOL_PATH)
assert SPEC is not None and SPEC.loader is not None
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)
RETIRED_PS = re.compile(
    r"\A(?:#[^\n]*\n|\s)*\[CmdletBinding\(\)\]\s*param\(\s*"
    r"(?:\[(?:string|switch)\]\$\w+(?:\s*=\s*'[^']*')?\s*,?\s*)+"
    r"\)\s*throw \[InvalidOperationException\]::new\(\s*'([^']+)'\)\s*\Z"
)


class CortexMcpX64RouteRetirementTests(unittest.TestCase):
    def test_builder_has_only_a_refusal_and_fails_before_input_or_output(self) -> None:
        match = RETIRED_PS.fullmatch(BUILDER_PATH.read_text(encoding="utf-8"))
        self.assertIsNotNone(match, "Retired builder must contain only parameters and its refusal.")
        assert match is not None
        self.assertIn(VERIFIER.RETIREMENT_CODE, match.group(1))
        self.assertIn("Build-PegasusCortexRelease.ps1", match.group(1))
        builder_literal = str(BUILDER_PATH).replace("'", "''")
        parser = (
            "$tokens = $null; $errors = $null; "
            "[System.Management.Automation.Language.Parser]::ParseFile("
            f"'{builder_literal}', [ref]$tokens, [ref]$errors) | Out-Null; "
            "if ($errors.Count -ne 0) { $errors | ForEach-Object { "
            "[Console]::Error.WriteLine($_.Message) }; exit 1 }"
        )
        parsed = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", parser],
            cwd=ROOT, capture_output=True, text=True, timeout=30, check=False,
        )
        self.assertEqual(0, parsed.returncode, parsed.stderr)
        with tempfile.TemporaryDirectory() as temporary:
            missing_source = pathlib.Path(temporary) / "must-not-read"
            output = pathlib.Path(temporary) / "must-not-exist"
            completed = subprocess.run(
                [
                    "powershell.exe", "-NoProfile", "-NonInteractive",
                    "-ExecutionPolicy", "Bypass", "-File", str(BUILDER_PATH),
                    "-ProjectRoot", str(missing_source), "-OutputRoot", str(output),
                    "-InspectSourceSet",
                ],
                cwd=ROOT, capture_output=True, text=True, timeout=30, check=False,
            )
            diagnostic = completed.stdout + completed.stderr
            self.assertNotEqual(0, completed.returncode)
            self.assertIn(VERIFIER.RETIREMENT_CODE, diagnostic)
            self.assertIn("Build-PegasusCortexRelease.ps1", diagnostic)
            self.assertFalse(output.exists())
            self.assertFalse(missing_source.exists())

    def test_validator_cli_and_api_refuse_without_reading_a_package(self) -> None:
        completed = subprocess.run(
            [sys.executable, str(TOOL_PATH), "does-not-exist.zip"],
            cwd=ROOT, capture_output=True, text=True, timeout=30, check=False,
        )
        diagnostic = completed.stdout + completed.stderr
        self.assertEqual(2, completed.returncode)
        self.assertIn(VERIFIER.RETIREMENT_CODE, diagnostic)
        self.assertIn("Build-PegasusCortexRelease.ps1", diagnostic)
        # Opaque objects cannot be opened, resolved or interpreted as paths.
        with self.assertRaisesRegex(RuntimeError, VERIFIER.RETIREMENT_CODE):
            VERIFIER.verify_package(object(), object(), run_smoke=False)


if __name__ == "__main__":
    unittest.main()
