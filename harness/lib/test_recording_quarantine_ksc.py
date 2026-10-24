"""Fixture gates for `recording-quarantine-ksc` (the QR-1 host).

The fixture is DERIVED by `harness/tools/build_recording_quarantine_ksc.py` from
`career-earned-ksc`, so a change to the base must re-run the builder. These cells turn
that "must" into a red and pin the values QR-1's log contract quotes: every content hash
and the purged byte count are re-derived here and compared with the literals in the spec,
so a fixture edit cannot leave the lane pinning stale hashes.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
SPEC = os.path.join(_HARNESS, "scenarios", "QR-1-recording-quarantine-grace-and-expiry.toml")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_recording_quarantine_ksc.py")
    spec = importlib.util.spec_from_file_location("build_recording_quarantine_ksc", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class RecordingQuarantineKscFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()
        cls.files = cls.b.expected_files()
        cls.values = cls.b.derived_values()
        with open(SPEC, encoding="utf-8") as fh:
            cls.spec_text = fh.read()

    def test_committed_bytes_match_the_builder(self):
        self.assertEqual([], self.b.check())

    def test_the_shape_verifies(self):
        self.assertEqual([], self.b.verify_shape(self.files))

    def test_verify_rejects_each_broken_shape(self):
        b = self.b
        rec = b.REC
        broken = dict(self.files)
        broken["%s/%s.prec" % (rec, b.R2)] = b"x"
        self.assertTrue(b.verify_shape(broken), "an R2 head must red")
        broken = dict(self.files)
        broken["%s/%s_vessel.craft" % (rec, b.R1)] = b._base("%s/%s_vessel.craft" % (rec, b.R1))
        self.assertTrue(b.verify_shape(broken), "an R1 head holding the named bytes must red")
        broken = dict(self.files)
        broken["persistent.sfs"] = broken["persistent.sfs"].replace(b"ghostHash = ", b"precHash = ")
        self.assertTrue(b.verify_shape(broken), "a prec hash must red")
        broken = dict(self.files)
        del broken["%s/%s/%s_vessel.craft" % (b.QUARANTINE, b.EXPIRED_DATE, b.O2_EXPIRED)]
        self.assertTrue(b.verify_shape(broken), "a .prec without its _vessel.craft must red")

    def test_every_collector_state_is_present(self):
        b = self.b
        v = self.values
        want = [
            "%s/%s.%s_ghost.craft" % (b.VERSIONS, b.R1, v["r1GhostNamedHash"]),
            "%s/%s/%s.%s_vessel.craft" % (b.VERSION_QUARANTINE, b.EXPIRED_DATE, b.R1, v["r1VesselNamedHash"]),
            "%s/%s/%s.%s_ghost.craft" % (b.VERSION_QUARANTINE, b.EXPIRED_DATE, b.O4_VERSION, v["o4Hash"]),
        ]
        for suffix in b.SET_SUFFIXES:
            want.append("%s/%s/%s%s" % (b.QUARANTINE, b.EXPIRED_DATE, b.R2, suffix))
            want.append("%s/%s%s" % (b.REC, b.O1_FRESH, suffix))
            want.append("%s/%s/%s%s" % (b.QUARANTINE, b.EXPIRED_DATE, b.O2_EXPIRED, suffix))
            want.append("%s/%s%s" % (b.QUARANTINE, b.O3_FLAT, suffix))
        for rel in want:
            self.assertIn(rel, self.files, rel)
        # The version files carry their real content hash in their names.
        for rel, data in self.files.items():
            if rel.startswith(b.VERSIONS + "/") and rel.endswith(".craft"):
                name = rel.rsplit("/", 1)[1]
                stem = name.rsplit("_", 1)[0]
                self.assertEqual(stem.rsplit(".", 1)[1], b.content_hash(data), rel)

    def test_the_expired_folder_is_expired_and_the_counts_hold(self):
        # IsQuarantineExpired: now >= folder date + 14 days. 2020-01-01 is expired for
        # any clock the lane flies under; five files per set feed the 5-counts.
        self.assertEqual("2020-01-01", self.b.EXPIRED_DATE)
        self.assertEqual(5, len(self.b.SET_SUFFIXES))
        o2 = [r for r in self.files if "/%s/%s" % (self.b.EXPIRED_DATE, self.b.O2_EXPIRED) in r]
        self.assertEqual(5, len(o2))

    def test_the_spec_pins_the_derived_values(self):
        v = self.values
        for key in ("r1VesselNamedHash", "r1GhostNamedHash", "r1VesselNewerHash",
                    "r1GhostNewerHash", "o4Hash"):
            self.assertIn(v[key], self.spec_text, key)
        self.assertIn("bytesFreed=%d " % v["bytesFreed"], self.spec_text)
        self.assertIn('saveTemplate       = "fixtures/saves/recording-quarantine-ksc"', self.spec_text)


if __name__ == "__main__":
    unittest.main()
