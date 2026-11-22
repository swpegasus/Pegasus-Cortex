"""Self-tests for the internal disposable recovery-probe brief."""

from __future__ import annotations

import os
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

from heartbeat_v2.common import ContractError, canonical_json_bytes
from heartbeat_v2.paths import validate_relative_path
from heartbeat_v2.recovery_probe_brief import (
    MAXIMUM_RECOVERY_PROBE_BRIEF_BYTES,
    RECOVERY_PROBE_BRIEF_RELATIVE_PATH,
    RECOVERY_ORDER,
    build_recovery_probe_brief,
    validate_recovery_probe_brief,
    write_recovery_probe_brief,
)


GENERATED = datetime(2026, 7, 30, 12, 0, 0, tzinfo=timezone.utc)


class RecoveryProbeBriefTests(unittest.TestCase):
    def test_brief_is_bounded_hash_bound_and_current(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._write_authority(root)
            brief = build_recovery_probe_brief(root, generated_at_utc=GENERATED)
            output = root / RECOVERY_PROBE_BRIEF_RELATIVE_PATH
            byte_count = write_recovery_probe_brief(root, output, brief)
            result = validate_recovery_probe_brief(
                root,
                output,
                now_utc=GENERATED + timedelta(hours=1),
            )

        self.assertLessEqual(byte_count, MAXIMUM_RECOVERY_PROBE_BRIEF_BYTES)
        self.assertLessEqual(
            len(canonical_json_bytes(brief) + b"\n"),
            MAXIMUM_RECOVERY_PROBE_BRIEF_BYTES,
        )
        self.assertEqual(
            "pegasus.cortex.recovery-probe-brief/1.0",
            brief["schema"],
        )
        self.assertEqual("valid", result["status"])
        self.assertEqual("f88f03ff791f", brief["currentTask"]["id"])
        self.assertEqual("1.2026.07.21.21.54.B", brief["checkpoint"]["source"])
        self.assertFalse(brief["authority"]["parentTranscriptInherited"])

    def test_source_change_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._write_authority(root)
            brief = build_recovery_probe_brief(root, generated_at_utc=GENERATED)
            output = root / RECOVERY_PROBE_BRIEF_RELATIVE_PATH
            write_recovery_probe_brief(root, output, brief)
            (root / ".cortex/STATE.md").write_text(
                "# State\n\n## Next Step\nChanged after generation.\n",
                encoding="utf-8",
            )
            with self.assertRaises(ContractError):
                validate_recovery_probe_brief(root, output, now_utc=GENERATED)

    def test_root_identity_prevents_cross_root_brief_reuse(self) -> None:
        with tempfile.TemporaryDirectory() as first_directory:
            with tempfile.TemporaryDirectory() as second_directory:
                first_root = Path(first_directory)
                second_root = Path(second_directory)
                self._write_authority(first_root)
                self._write_authority(second_root)
                copied = build_recovery_probe_brief(
                    first_root,
                    generated_at_utc=GENERATED,
                )
                output = second_root / RECOVERY_PROBE_BRIEF_RELATIVE_PATH
                write_recovery_probe_brief(second_root, output, copied)
                with self.assertRaises(ContractError):
                    validate_recovery_probe_brief(
                        second_root,
                        output,
                        now_utc=GENERATED,
                    )

                local = build_recovery_probe_brief(
                    second_root,
                    generated_at_utc=GENERATED,
                )

        self.assertNotEqual(copied["rootId"], local["rootId"])
        self.assertNotEqual(copied["contextSetId"], local["contextSetId"])

    def test_poisoned_environment_does_not_change_brief_or_use_network(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._write_authority(root)
            clean = build_recovery_probe_brief(
                root,
                generated_at_utc=GENERATED,
            )
            poisoned = {
                "PEGASUS_CORTEX_RUNTIME_CONNECTION": "https://live.invalid",
                "PEGASUS_CORTEX_AUTHORITY_FILE": "C:\\poison\\authority.json",
                "PEGASUS_CORTEX_ROW_KEY_FILE": "C:\\poison\\row.key",
                "PEGASUS_CORTEX_MIGRATION_CONNECTION": "https://migration.invalid",
                "PEGASUS_CORTEX_TLS_POLICY": "insecure",
                "HTTP_PROXY": "http://proxy.invalid",
                "HTTPS_PROXY": "http://proxy.invalid",
            }
            with patch.dict(os.environ, poisoned, clear=False):
                with patch(
                    "socket.socket",
                    side_effect=AssertionError("brief producer attempted network access"),
                ):
                    isolated = build_recovery_probe_brief(
                        root,
                        generated_at_utc=GENERATED,
                    )

        self.assertEqual(canonical_json_bytes(clean), canonical_json_bytes(isolated))

    def test_expired_brief_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._write_authority(root)
            brief = build_recovery_probe_brief(
                root,
                generated_at_utc=GENERATED,
                max_age_seconds=300,
            )
            output = root / RECOVERY_PROBE_BRIEF_RELATIVE_PATH
            write_recovery_probe_brief(root, output, brief)
            with self.assertRaises(ContractError):
                validate_recovery_probe_brief(
                    root,
                    output,
                    now_utc=GENERATED + timedelta(seconds=300),
                )

    def test_future_brief_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._write_authority(root)
            brief = build_recovery_probe_brief(
                root,
                generated_at_utc=GENERATED + timedelta(seconds=61),
            )
            output = root / RECOVERY_PROBE_BRIEF_RELATIVE_PATH
            write_recovery_probe_brief(root, output, brief)
            with self.assertRaises(ContractError):
                validate_recovery_probe_brief(root, output, now_utc=GENERATED)

    def test_recovery_order_mismatch_fails_generation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._write_authority(root)
            agents = root / "AGENTS.md"
            agents.write_text(
                agents.read_text(encoding="utf-8").replace("11. `.cortex/RULES.md`\n", ""),
                encoding="utf-8",
            )
            with self.assertRaises(ContractError):
                build_recovery_probe_brief(root, generated_at_utc=GENERATED)

    def test_unapproved_output_path_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._write_authority(root)
            brief = build_recovery_probe_brief(root, generated_at_utc=GENERATED)
            with self.assertRaises(ContractError):
                write_recovery_probe_brief(root, root / "brief.json", brief)

    def test_unsafe_relative_paths_are_rejected(self) -> None:
        for value in (
            "../state.json",
            "/absolute.json",
            "C:\\absolute.json",
            "nested//state.json",
            "NUL.json",
            "trailing. /state.json",
        ):
            with self.subTest(value=value):
                with self.assertRaises(ContractError):
                    validate_relative_path(value)

    def test_symlinked_authority_file_is_rejected_when_supported(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self._write_authority(root)
            target = root / ".cortex/README.md"
            target.unlink()
            try:
                target.symlink_to(root / ".cortex/STATE.md")
            except OSError:
                # Keep the environmental skip stable and content-safe; the OS
                # exception includes temporary absolute paths on Windows.
                self.skipTest("local symlink creation is unavailable")
            with self.assertRaises(ContractError):
                build_recovery_probe_brief(root, generated_at_utc=GENERATED)

    @staticmethod
    def _write_authority(root: Path) -> None:
        for relative in RECOVERY_ORDER:
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f"# {path.stem}\n", encoding="utf-8")

        order = "\n".join(
            f"{index}. `{relative}`" for index, relative in enumerate(RECOVERY_ORDER, start=1)
        )
        (root / "AGENTS.md").write_text(
            f"# Agents\n\n## Recovery Order\n\n{order}\n",
            encoding="utf-8",
        )
        (root / "START_HERE.md").write_text(
            "# Start Here\n\n"
            "## Current Working Rule\n\n"
            "Read-only inspection is allowed; all mutation needs explicit authority.\n\n"
            "## Next Step\n\n"
            "Use `1.2026.07.21.21.54.B` as the current accepted source checkpoint. "
            "Await the next bounded task.\n",
            encoding="utf-8",
        )
        (root / ".cortex/TASKS.md").write_text(
            "# Tasks\n\n"
            "## Active\n\n"
            "- [ ] Complete provisional task `f88f03ff791f`.\n"
            "  Keep every live gate closed.\n\n"
            "## Done\n\n"
            "- [x] Earlier task.\n",
            encoding="utf-8",
        )
        (root / ".cortex/STATE.md").write_text(
            "# State\n\n## Next Step\nRemain available.\n",
            encoding="utf-8",
        )


if __name__ == "__main__":
    unittest.main()
