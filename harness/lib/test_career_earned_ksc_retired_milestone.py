"""Drift and shape gates for the `career-earned-ksc-retired-milestone` fixture (TL-1's host).

The fixture is `career-earned-ksc` plus one `LEDGER_TOMBSTONES` node retiring the
`Kerbin/Science` milestone row, written by
`harness/tools/build_career_earned_ksc_retired_milestone.py`. These cells keep the
derivation mechanical (a hand edit, or a change to the base, reds here instead of in a live
boot) and pin the identities TL-1's log contract quotes.

Stdlib only; ASCII only; no em dashes.
"""
import importlib.util
import os
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_career_earned_ksc_retired_milestone.py")
SPEC = os.path.join(HARNESS_ROOT, "scenarios", "TL-1-timeline-retired-milestone-copy.toml")


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_career_earned_ksc_retired_milestone", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class CareerEarnedKscRetiredMilestoneFixtureTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()
        cls.files = cls.b.expected_files()

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], self.b.check())

    def test_the_derived_save_satisfies_every_post_condition(self):
        self.assertEqual([], self.b.verify_files(self.files))

    def test_the_only_change_from_the_base_is_the_tombstone_node(self):
        base = self.b._read(os.path.join(self.b.BASE_DIR, "persistent.sfs"))
        derived = self.files["persistent.sfs"]
        nl = b"\r\n"
        lines = derived.split(nl)
        block = [l.encode("ascii") for l in self.b.tombstone_lines()]
        at = lines.index(block[0])
        self.assertEqual(block, lines[at:at + len(block)])
        self.assertEqual(base.split(nl), lines[:at] + lines[at + len(block):])
        for rel, data in self.files.items():
            if rel != "persistent.sfs":
                self.assertEqual(self.b._read(os.path.join(self.b.BASE_DIR, rel)), data, rel)

    def test_verify_rejects_each_broken_shape(self):
        b = self.b
        lf = b._lf
        sfs = lf(self.files["persistent.sfs"])
        ledger = lf(self.files["Parsek/GameState/ledger.pgld"])
        stones = lf(self.files["Parsek/GameState/milestones.pgsm"])
        self.assertEqual([], b.verify_shape(sfs, ledger, stones))
        self.assertTrue(b.verify_shape(sfs.replace(b.RETIRED_ACTION_ID, "act_other"), ledger, stones),
                        "a tombstone on another row must red")
        self.assertTrue(b.verify_shape(sfs + "\n\t\tRECORDING_SUPERSEDES\n", ledger, stones),
                        "a supersede row must red")
        self.assertTrue(b.verify_shape(sfs, ledger, stones.replace(
            "recordingId = " + b.TAGGED_RECORDING_ID, "recordingId = rec_gone")),
            "a copy tagged elsewhere must red")
        self.assertTrue(b.verify_shape(sfs, ledger.replace(
            "milestoneId = Kerbin/Science", "milestoneId = Kerbin/Other"), stones),
            "a retired row that is not the milestone must red")

    def test_the_spec_quotes_the_fixture_identities(self):
        with open(SPEC, encoding="utf-8") as fh:
            text = fh.read()
        self.assertIn("career-earned-ksc-retired-milestone", text)
        self.assertIn(self.b.RETIRED_MILESTONE_ID, text)
        self.assertIn(self.b.RETIRED_UT, text)


if __name__ == "__main__":
    unittest.main()
