"""Drift and shape gates for the `career-earned-ksc` fixture.

The fixture is the xUnit career base `Source/Parsek.Tests/Fixtures/C2CareerPostFix/`
copied into `harness/fixtures/saves/` by `harness/tools/build_career_earned_ksc.py`
with the two hygiene edits `build_career_earned_pad.py` applies (rewindSave hints
stripped, `Parsek/Saves/` not copied) and nothing else - so it is the SAME career as
`career-earned-pad` minus the spliced pad craft, and boots to the Space Center. These
cells keep the derivation mechanical: a hand edit to the fixture, a re-harvest of the
base, or a drift between the two siblings' shared payload reds here instead of in a
live boot.
"""
import importlib.util
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_career_earned_ksc.py")
FIXTURE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-earned-ksc")
PAD_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-earned-pad")


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_career_earned_ksc", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read_text(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


class CareerEarnedKscFixtureDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derived_save_satisfies_every_post_condition(self):
        builder = _load_builder()
        self.assertEqual([], builder.verify_shape(_read_text(os.path.join(FIXTURE_DIR, "persistent.sfs"))))

    def test_verify_rejects_each_broken_shape(self):
        builder = _load_builder()
        good = _read_text(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        self.assertEqual([], builder.verify_shape(good))
        self.assertTrue(builder.verify_shape(good.replace("Mode = CAREER", "Mode = SANDBOX", 1)))
        self.assertTrue(builder.verify_shape(good + "\n\t\tVESSEL\n\t\t{\n\t\t}\n"))
        self.assertTrue(builder.verify_shape(good.replace("state = Offered", "state = Declined")))
        self.assertTrue(builder.verify_shape(good.replace("state = Offered", "state = Active", 1)))
        self.assertTrue(builder.verify_shape(good + "\n\t\t\t\trewindSave = parsek_rw_abc123\n"))

    def test_the_fixture_is_vessel_less_and_routes_to_the_space_center(self):
        # TestCommandLoadGame.DecideLoadRoute takes the NoVesselSpaceCenter route for a
        # save with no focusable vessel; that route is what the ResourceTopBar and
        # Mission Control cells need, and what the pad sibling deliberately forgoes.
        sfs = _read_text(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        self.assertIsNone(re.search(r"^\t+VESSEL$", sfs, re.M))
        meta = _read_text(os.path.join(FIXTURE_DIR, "persistent.loadmeta"))
        self.assertIn("vesselCount = 0", meta.replace(" ", "").replace("vesselCount=", "vesselCount = "))

    def test_the_contracts_are_offered_and_none_is_active(self):
        sfs = _read_text(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        self.assertGreaterEqual(sfs.count("state = Offered"), 1)
        self.assertNotIn("state = Active", sfs)

    def test_the_payload_is_the_pad_siblings_payload(self):
        # Same ledger, same recordings, same milestones: the two fixtures are one
        # career seen from two scenes. The pad's ledger carries ONE extra row (its
        # ContractAccept splice), so the base's ledger must be a prefix of the pad's.
        ksc_ledger = _read_text(os.path.join(FIXTURE_DIR, "Parsek", "GameState", "ledger.pgld"))
        pad_ledger = _read_text(os.path.join(PAD_DIR, "Parsek", "GameState", "ledger.pgld"))
        self.assertTrue(pad_ledger.startswith(ksc_ledger.rstrip("\r\n")),
                        "the pad sibling's ledger no longer extends this fixture's ledger")
        for rel in ("Parsek/GameState/milestones.pgsm", "Parsek/GameState/events.pgse"):
            self.assertEqual(_read_text(os.path.join(FIXTURE_DIR, rel)), _read_text(os.path.join(PAD_DIR, rel)), rel)
        ksc_recs = sorted(os.listdir(os.path.join(FIXTURE_DIR, "Parsek", "Recordings")))
        pad_recs = sorted(os.listdir(os.path.join(PAD_DIR, "Parsek", "Recordings")))
        self.assertEqual(pad_recs, ksc_recs)

    def test_no_rewind_quicksave_is_committed(self):
        self.assertFalse(os.path.isdir(os.path.join(FIXTURE_DIR, "Parsek", "Saves")))
        sfs = _read_text(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        self.assertNotIn("rewindSave = parsek_rw_", sfs)


if __name__ == "__main__":
    unittest.main()
