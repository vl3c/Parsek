"""Fixture + spec gates for `career-earned-pad` and `L4-ledger-groundtruth-strict`.

WHAT THIS FILE GUARDS. `career-earned-pad` is the ONLY committed fixture that is
both a CAREER WITH POPULATED PER-IDENTITY FACETS and FOCUSABLE INTO THE FLIGHT
SCENE, and it is the subject the career-ledger B.4 strict gate is armed on. Both
halves of that sentence are load-bearing and neither is obvious from the bytes,
so every one of them is a cell here.

IT READS OUTSIDE `harness/`, deliberately and as a FOURTH such cell alongside
`CommittedBatchTallySourceSyncTests`, `test_doc_spec_sync.py` and
`test_the_c_sharp_writer_still_emits_pointcount`. The fixture is DERIVED from
`Source/Parsek.Tests/Fixtures/C2CareerPostFix/` - the one committed copy of the
save harness run `2026-08-19_2130_L3-career-science-recover` produced - so a
re-harvest of that fixture MUST re-run the builder. Reading the base here is
what turns "must" into a red instead of a stale fixture flying a live gate.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

try:
    import tomllib
except ImportError:  # pragma: no cover - Python < 3.11
    import tomli as tomllib  # type: ignore

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
_REPO = os.path.dirname(_HARNESS)

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "career-earned-pad")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
DONOR_SFS = os.path.join(_HARNESS, "fixtures", "saves", "career-science-pad",
                         "persistent.sfs")
SPEC_PATH = os.path.join(_HARNESS, "scenarios", "L4-ledger-groundtruth-strict.toml")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_career_earned_pad.py")
    spec = importlib.util.spec_from_file_location("build_career_earned_pad", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class CareerEarnedPadFixtureDriftTests(unittest.TestCase):
    """WIRES `build_career_earned_pad.py --check` INTO THE SUITE."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_the_committed_fixture_satisfies_every_post_condition(self):
        problems = self.builder.verify(
            self.builder.read_lines(FIXTURE_SFS), self.builder.CREW_NAME)
        self.assertEqual([], problems)

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_rebuild(self):
        # THE DRIFT GUARD. Raw bytes, not the CRLF-normalized line lists, for the
        # reason CareerSciencePadFixtureDriftTests states: `read_lines` would call
        # a fixture that had lost its CRLFs identical to one that had not.
        #
        # This cell is ALSO the re-harvest tripwire: its base input is the xUnit
        # fixture, so a future re-fly of L3 that replaces C2CareerPostFix reds
        # HERE, naming the builder to re-run, instead of leaving a strict-armed
        # flight measuring a career nobody meant to ship.
        builder = self.builder
        rebuilt = builder.build(
            builder.read_lines(os.path.join(builder.BASE_DIR, "persistent.sfs")),
            builder.read_lines(DONOR_SFS),
            builder.CREW_NAME,
            "%s (CAREER)" % builder.TARGET_NAME)
        produced = "\r\n".join(rebuilt).encode("utf-8")
        with open(FIXTURE_SFS, "rb") as fh:
            committed = fh.read()
        if committed != produced:
            offset = next(
                (i for i, (a, b) in enumerate(zip(committed, produced)) if a != b),
                min(len(committed), len(produced)))
            self.fail("career-earned-pad has drifted from what "
                      "build_career_earned_pad.py produces from the current "
                      "C2CareerPostFix + career-science-pad; re-run the builder "
                      "and commit, or explain the divergence. First difference at "
                      "byte %d (committed %d bytes, rebuilt %d bytes)"
                      % (offset, len(committed), len(produced)))

    def test_the_spliced_vessel_collides_with_no_recorded_identity(self):
        # THE REASON THE BUILDER EXISTS, asserted as its own cell rather than
        # left inside `verify`'s list. The donor craft IS the craft this career
        # flew and RECOVERED, so a verbatim splice would put the recovered vessel
        # back in the save with its recorded identity intact, and
        # `LedgerGroundTruthDiff.CompareRecovery` would raise:
        #   guid match    -> ALWAYS-HARD consistency divergence (IsAlwaysHard)
        #   pid-only match -> report-only, which THIS SPEC'S strict arg promotes
        # Either way the armed run would red on a fixture artifact that reads
        # exactly like a product defect.
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        fs = builder.find_node(lines, "FLIGHTSTATE")
        vessel = builder.child_nodes(lines, fs, "VESSEL")[0]
        guid = builder.get_value(lines, vessel, "pid")
        pid = builder.get_value(lines, vessel, "persistentId")

        self.assertEqual(builder.NEW_VESSEL_GUID, guid)
        self.assertEqual(builder.NEW_VESSEL_PERSISTENT_ID, pid)
        self.assertNotEqual(builder.RECORDED_VESSEL_GUID, guid)
        self.assertNotEqual(builder.RECORDED_VESSEL_PERSISTENT_ID, pid)
        self.assertNotIn(guid, builder._values_named(lines, "recordedVesselGuid"))
        self.assertNotIn(pid, builder._values_named(lines, "vesselPersistentId"))

        # And the donor really is the recovered craft, so the guard is not
        # guarding against nothing.
        donor = builder.read_lines(DONOR_SFS)
        donor_vessel = builder.child_nodes(
            donor, builder.find_node(donor, "FLIGHTSTATE"), "VESSEL")[0]
        self.assertEqual(builder.RECORDED_VESSEL_GUID,
                         builder.get_value(donor, donor_vessel, "pid"))
        self.assertIn(builder.RECORDED_VESSEL_GUID,
                      builder._values_named(lines, "recordedVesselGuid"))

    def test_the_career_clock_is_the_careers_own(self):
        # `build_career_pad_craft.py` replaces the base FLIGHTSTATE wholesale;
        # this builder inserts into it, because the base's `UT = 408.72` is the
        # clock all 14 ledger actions were written against and the donor's is
        # 9.06 - i.e. before every one of them.
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        fs = builder.find_node(lines, "FLIGHTSTATE")
        ut = float(builder.get_value(lines, fs, "UT"))
        ends = [float(v) for v in builder._values_named(lines, "explicitEndUT")]
        self.assertTrue(ends, "the fixture carries no recording")
        self.assertGreater(ut, max(ends))

    def test_the_crew_kerbals_career_log_survived_the_roster_edit(self):
        # The KerbalXp facet's SAVE side. The roster edit is ONE `set_value`
        # rather than the whole-row swap the base builder performs, precisely so
        # these entries survive: the reconstruction credits them off the ledger,
        # so losing them would manufacture a PhantomInRecon divergence that
        # strict promotes to a hard failure.
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        roster = builder.find_node(lines, "ROSTER")
        kerbal = builder._kerbal_named(lines, roster, builder.CREW_NAME)
        self.assertIsNotNone(kerbal)
        self.assertEqual("Assigned", builder.get_value(lines, kerbal, "state"))
        self.assertTrue(builder.contains_line(lines, kerbal, "0 = Recover"))
        self.assertTrue(builder.contains_line(lines, kerbal, "flight = 1"))

    def test_the_career_payload_survived(self):
        # A fixture that came out at the SEED pools would mean the splice had
        # dropped the earned career it exists to carry.
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        self.assertEqual(builder.EXPECT_FUNDS,
                         builder._scenario_value(lines, "Funding", "funds"))
        self.assertEqual(builder.EXPECT_SCIENCE,
                         builder._scenario_value(lines, "ResearchAndDevelopment", "sci"))
        self.assertEqual(builder.EXPECT_REPUTATION,
                         builder._scenario_value(lines, "Reputation", "rep"))
        self.assertTrue(os.path.isfile(
            os.path.join(FIXTURE_DIR, "Parsek", "GameState", "ledger.pgld")))
        self.assertEqual(
            2, len([n for n in os.listdir(
                os.path.join(FIXTURE_DIR, "Parsek", "Recordings"))
                if n.endswith(".prec")]))

    def test_the_loadmeta_agrees_with_the_committed_save(self):
        builder = self.builder
        meta = builder.read_lines(os.path.join(FIXTURE_DIR, "persistent.loadmeta"))
        self.assertIn("vesselCount = 1", meta)
        self.assertIn("gameMode = CAREER", meta)


class L4SpecFixtureSyncTests(unittest.TestCase):
    """The SPEC and the FIXTURE must agree, and nothing else checks that."""

    @classmethod
    def setUpClass(cls):
        with open(SPEC_PATH, "rb") as fh:
            cls.spec = tomllib.load(fh)
        cls.builder = _load_builder()
        cls.sfs = cls.builder.read_lines(FIXTURE_SFS)

    def _steps(self):
        return self.spec["driver"]["steps"]

    def test_the_spec_points_at_the_earned_pad_fixture(self):
        self.assertEqual("fixtures/saves/career-earned-pad",
                         self.spec["fixture"]["saveTemplate"])

    def test_the_spec_injects_no_recordings(self):
        # The fixture brings its OWN two recordings; an injection preset would
        # add synthetic ones and move `recordings.count` off the fixture's truth.
        self.assertEqual("none", self.spec["fixture"]["injectedRecordings"])

    def test_the_runtests_step_arms_strict(self):
        # THE ARMING ITSELF. Lowercase wire literal, because
        # TestCommandRunTests.TryParseStrictArg accepts only "true"/"false" and a
        # TOML bool would reach the wire as "True" and be REJECTED.
        run = [s for s in self._steps() if s.get("cmd") == "RunTests"]
        self.assertEqual(1, len(run))
        self.assertEqual("LedgerGroundTruth", run[0]["args"]["category"])
        self.assertEqual("true", run[0]["args"]["strict"])
        self.assertIsInstance(run[0]["args"]["strict"], str)

    def test_auto_record_is_turned_off_before_the_batch(self):
        # GUARD 4 of the in-game cell: a LIVE RECORDER makes
        # LedgerGroundTruthHarness Skip instead of run, which would green the
        # flight while measuring nothing. Mandatory, not cosmetic.
        setting = [s for s in self._steps()
                   if s.get("cmd") == "SetSetting"
                   and s.get("args", {}).get("name") == "autoRecordOnLaunch"]
        self.assertEqual(1, len(setting))
        self.assertEqual("false", setting[0]["args"]["value"])
        order = [s.get("cmd") for s in self._steps()]
        self.assertLess(order.index("SetSetting"), order.index("RunTests"))

    def test_the_fixture_is_a_focusable_career(self):
        # The two properties that make a FLIGHT-scene batch possible at all:
        # CAREER mode (the cell is career-only) and exactly one VESSEL with
        # activeVessel indexed onto it (a vessel-less save routes LoadGame to
        # NoVesselSpaceCenter and the batch scene-skips its only declaration).
        self.assertIn("\tMode = CAREER", self.sfs)
        fs = self.builder.find_node(self.sfs, "FLIGHTSTATE")
        self.assertEqual(1, len(self.builder.child_nodes(self.sfs, fs, "VESSEL")))
        self.assertEqual("0", self.builder.get_value(self.sfs, fs, "activeVessel"))

    def test_the_spec_pins_the_strict_true_result_token(self):
        # The `strict=True` half of the ground-truth `result:` line is what makes
        # the arming falsifiable: dropping the arg without re-pinning this token
        # reds the run rather than quietly measuring the default diff.
        required = self.spec["expectations"]["logContracts"]["required"]
        self.assertTrue(any("strict=True" in tok for tok in required),
                        "no required token pins strict=True: %s" % required)


if __name__ == "__main__":
    unittest.main()
