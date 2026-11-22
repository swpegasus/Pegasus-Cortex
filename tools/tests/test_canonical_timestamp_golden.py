from __future__ import annotations

import base64
import datetime as dt
import hashlib
import json
import pathlib
import unittest


PROJECT_ROOT = pathlib.Path(__file__).resolve().parents[2]
VECTOR_PATH = PROJECT_ROOT / "schemas" / "cortex-canonical-timestamp-v1.vectors.json"


def reseal_timestamp(input_json: str) -> bytes:
    """Apply the Python side of the language-neutral timestamp canonicalization."""

    value = json.loads(input_json)
    raw_timestamp = value["capturedAt"]
    if not isinstance(raw_timestamp, str) or not raw_timestamp.endswith("Z"):
        raise ValueError("The golden input must contain a Z-form UTC timestamp.")
    parsed = dt.datetime.fromisoformat(raw_timestamp.removesuffix("Z") + "+00:00")
    if parsed.utcoffset() != dt.timedelta(0):
        raise ValueError("The golden timestamp did not parse as UTC.")
    value["capturedAt"] = parsed.astimezone(dt.timezone.utc).isoformat(
        timespec="microseconds"
    )
    return json.dumps(
        value,
        allow_nan=False,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")


class CanonicalTimestampGoldenTests(unittest.TestCase):
    def test_python_reseals_z_as_the_shared_plus_zero_offset_vector(self) -> None:
        fixture = json.loads(VECTOR_PATH.read_text(encoding="utf-8"))
        self.assertEqual(1, fixture["schemaVersion"])
        self.assertEqual(
            "pegasus.cortex.canonical-timestamp-vectors/1.0",
            fixture["contractId"],
        )
        self.assertEqual(1, len(fixture["vectors"]))
        vector = fixture["vectors"][0]
        self.assertEqual("utc-z-to-explicit-offset", vector["id"])
        self.assertIn('.123456Z"', vector["inputJson"])

        actual = reseal_timestamp(vector["inputJson"])
        expected = base64.b64decode(vector["canonicalUtf8Base64"], validate=True)
        self.assertEqual(expected, actual)
        self.assertEqual(vector["canonicalJson"], actual.decode("utf-8"))
        self.assertIn("+00:00", vector["canonicalJson"])
        self.assertNotIn('.123456Z"', vector["canonicalJson"])
        self.assertEqual(vector["sha256"], hashlib.sha256(actual).hexdigest())


if __name__ == "__main__":
    unittest.main()
