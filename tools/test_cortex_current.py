#!/usr/bin/env python3
"""Dependency-free tests for the project-local CURRENT.json generator."""

from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest

import cortex_current


START = """# Start Here

## Current Recovery

- Current task: bounded task
- Gate: local source only
- Checkpoint: unversioned candidate
- Next action: run verification
"""


class CurrentProjectionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        (self.root / ".cortex").mkdir()
        (self.root / "START_HERE.md").write_text(START, encoding="utf-8")
        for relative in ("AGENTS.md", ".cortex/STATE.md", ".cortex/TASKS.md", ".cortex/RECOVERY.md"):
            (self.root / relative).write_text(f"# {relative}\n", encoding="utf-8")
        (self.root / ".cortex/meta.json").write_text(
            json.dumps({
                "project": {"name": "Fixture"},
                "cortexCorrelation": {
                    "projectId": "fixture",
                    "projectLinkId": f"plink_{'a' * 64}",
                },
            }),
            encoding="utf-8",
        )

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def generate(self, timestamp: str = "2026-07-31T20:00:00Z") -> dict:
        projection = cortex_current.build_projection(self.root, timestamp)
        _, size = cortex_current.write_projection(self.root, projection)
        self.assertLessEqual(size, cortex_current.MAXIMUM_UTF8_BYTES)
        return projection

    def test_projection_is_bounded_and_copies_explicit_hot_fields(self) -> None:
        projection = self.generate()
        self.assertEqual(cortex_current.SCHEMA, projection["schema"])
        self.assertEqual("bounded task", projection["current"]["task"])
        self.assertEqual("local source only", projection["current"]["gate"])
        self.assertEqual(f"plink_{'a' * 64}", projection["project"]["projectLinkId"])
        self.assertEqual(1, projection["authority"]["revision"])
        self.assertEqual(5, projection["authority"]["sourceCount"])

    def test_multiline_hot_fields_are_folded_without_losing_context(self) -> None:
        (self.root / "START_HERE.md").write_text(
            """# Start Here

## Current Recovery

- Current task: seal one portable source
  for Windows x64 and Linux ARM64.
- Gate: preserve rollback
  and stop for the requested restart.
- Checkpoint: focused verification passed
  from an extracted source root.
- Next action: run the exact matrix
  and seal matching packages.
""",
            encoding="utf-8",
        )
        projection = self.generate()
        self.assertEqual(
            "seal one portable source for Windows x64 and Linux ARM64.",
            projection["current"]["task"],
        )
        self.assertEqual(
            "preserve rollback and stop for the requested restart.",
            projection["current"]["gate"],
        )
        self.assertEqual(
            "focused verification passed from an extracted source root.",
            projection["current"]["checkpoint"],
        )
        self.assertEqual(
            "run the exact matrix and seal matching packages.",
            projection["current"]["nextAction"],
        )

    def test_authority_hash_matches_core_compatibility_vector(self) -> None:
        sources = []
        for source_id, character in (
            ("start_here", "a"),
            ("agents", "b"),
            ("cortex_state", "c"),
            ("cortex_tasks", "d"),
            ("cortex_recovery", "e"),
        ):
            digest = character * 64
            sources.append({
                "sourceId": source_id,
                "revisionId": f"sha256-{digest}",
                "sha256": digest,
            })
        self.assertEqual(
            "8ccfbcd5fe7335915c33bf94cc33dea4349cb019ad2da38281ac79dff6ce889a",
            cortex_current.compute_authority_set_hash(sources),
        )

    def test_revision_is_stable_until_authority_changes(self) -> None:
        first = self.generate()
        second = self.generate("2026-07-31T20:01:00Z")
        self.assertEqual(first["authority"], second["authority"])
        (self.root / ".cortex/STATE.md").write_text("# changed\n", encoding="utf-8")
        third = self.generate("2026-07-31T20:02:00Z")
        self.assertEqual(2, third["authority"]["revision"])
        self.assertNotEqual(
            first["authority"]["authoritySetHash"],
            third["authority"]["authoritySetHash"],
        )

    def test_missing_hot_field_fails_closed(self) -> None:
        (self.root / "START_HERE.md").write_text(
            START.replace("- Gate: local source only\n", ""), encoding="utf-8"
        )
        with self.assertRaisesRegex(cortex_current.CurrentProjectionError, "Gate"):
            cortex_current.build_projection(self.root, "2026-07-31T20:00:00Z")

    def test_project_link_change_advances_revision_and_invalidates_old_projection(self) -> None:
        first = self.generate()
        meta_path = self.root / ".cortex/meta.json"
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        meta["cortexCorrelation"]["projectLinkId"] = f"plink_{'b' * 64}"
        meta_path.write_text(json.dumps(meta), encoding="utf-8")
        with self.assertRaisesRegex(cortex_current.CurrentProjectionError, "project identity"):
            cortex_current.verify_projection(
                self.root, "2026-07-31T20:01:00Z", 3600
            )
        second = self.generate("2026-07-31T20:02:00Z")
        self.assertEqual(2, second["authority"]["revision"])
        self.assertEqual(
            first["authority"]["authoritySetHash"],
            second["authority"]["authoritySetHash"],
        )

    def test_verify_detects_stale_and_mismatched_authority(self) -> None:
        self.generate()
        result = cortex_current.verify_projection(
            self.root, "2026-07-31T20:01:00Z", 3600
        )
        self.assertEqual("valid", result["status"])
        with self.assertRaisesRegex(cortex_current.CurrentProjectionError, "stale"):
            cortex_current.verify_projection(
                self.root, "2026-07-31T22:00:01Z", 3600
            )
        (self.root / ".cortex/STATE.md").write_text("# mismatch\n", encoding="utf-8")
        with self.assertRaisesRegex(cortex_current.CurrentProjectionError, "hash"):
            cortex_current.verify_projection(
                self.root, "2026-07-31T20:01:00Z", 3600
            )

    def test_linked_authority_is_rejected_when_supported(self) -> None:
        target = self.root / "state-target.md"
        target.write_text("# linked\n", encoding="utf-8")
        state = self.root / ".cortex/STATE.md"
        state.unlink()
        try:
            state.symlink_to(target)
        except OSError:
            self.skipTest("the current token cannot create symbolic links")
        with self.assertRaisesRegex(cortex_current.CurrentProjectionError, "linked"):
            cortex_current.build_projection(self.root, "2026-07-31T20:00:00Z")


if __name__ == "__main__":
    unittest.main(verbosity=2)
