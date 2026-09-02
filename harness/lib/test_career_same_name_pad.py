"""Fixture + spec gates for `career-same-name-pad` and `L6-career-same-name-recover`.

WHAT THIS FILE GUARDS. `career-same-name-pad` is the purpose-built subject for
the recovery correlator's stage-2 proof
(`todo-and-known-bugs.md` -> KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY): a
career carrying TWO committed same-name recordings of a PRIOR launch, a live
craft whose launch guid conclusively differs from theirs, and NO banked flight
science, so the recover mission can actually reach the RECOVER phase the
correlator fires in.

WHY IT HAD TO BE BUILT, measured rather than argued: L6 reading run 1
(`2026-09-02_1137`) flew the same mission over `career-earned-pad` - L3's
PRODUCED save, which already carries the two same-name recordings - and the
flight landed and collected but TRANSMIT credited 0.0 career science, because L3
had already banked the launchpad biome to its cap. The mission's structural
transmit -> recover gate failed BEFORE recovery, and the schema forbids a
transmit floor below 0.001, so no param rescues it. The banked-science conflict
is intrinsic to reusing a produced save; the fix is to take the RECORDINGS from
the produced save and the CAREER from the pre-flight one.

IT READS OUTSIDE `harness/`, deliberately, joining the small set of cells that
do (`CommittedBatchTallySourceSyncTests`, `test_doc_spec_sync.py`,
`test_the_c_sharp_writer_still_emits_pointcount`, `test_career_earned_pad.py`).
The fixture is DERIVED from `Source/Parsek.Tests/Fixtures/C2CareerPostFix/`, so a
re-harvest of that xUnit fixture MUST re-run the builder; reading the base here
is what turns "must" into a red instead of a stale fixture flying a live lane.

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

FIXTURE_NAME = "career-same-name-pad"
FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", FIXTURE_NAME)
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
FIXTURE_META = os.path.join(FIXTURE_DIR, "persistent.loadmeta")
SPEC_PATH = os.path.join(_HARNESS, "scenarios", "L6-career-same-name-recover.toml")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_career_same_name_pad.py")
    spec = importlib.util.spec_from_file_location("build_career_same_name_pad", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class CareerSameNamePadFixtureDriftTests(unittest.TestCase):
    """WIRES `build_career_same_name_pad.py --check` INTO THE SUITE."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def _host_sfs(self):
        return os.path.join(_HARNESS, "fixtures", "saves",
                            self.builder.HOST_NAME, "persistent.sfs")

    def test_the_committed_fixture_satisfies_every_post_condition(self):
        problems = self.builder.verify(
            self.builder.read_lines(FIXTURE_SFS), self.builder.CREW_NAME)
        self.assertEqual([], problems, "; ".join(problems))

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_rebuild(self):
        # THE DRIFT GUARD, and the re-harvest tripwire. Raw bytes rather than the
        # CRLF-normalized line lists, for the reason the sibling drift cells
        # state: `read_lines` would call a fixture that had lost its CRLFs
        # identical to one that had not.
        builder = self.builder
        rebuilt = builder.build(
            builder.read_lines(self._host_sfs()),
            builder.read_lines(os.path.join(builder.RECORDINGS_DIR, "persistent.sfs")),
            "%s (CAREER)" % FIXTURE_NAME)
        produced = "\r\n".join(rebuilt).encode("utf-8")
        with open(FIXTURE_SFS, "rb") as fh:
            committed = fh.read()
        self.assertEqual(committed, produced,
                         "%s has drifted from what build_career_same_name_pad.py "
                         "produces from the current career-science-pad and "
                         "C2CareerPostFix; re-run the builder and commit"
                         % FIXTURE_NAME)

    def test_the_committed_loadmeta_is_byte_identical_to_a_fresh_rebuild(self):
        # Its own cell because the loadmeta is built from the HOST's meta plus the
        # produced save's clock, so a host re-derivation moves it without moving
        # one byte of the save text the cell above compares.
        builder = self.builder
        rebuilt = builder.build_loadmeta(
            builder.read_lines(os.path.join(
                _HARNESS, "fixtures", "saves", builder.HOST_NAME,
                "persistent.loadmeta")),
            builder.read_lines(FIXTURE_SFS))
        produced = "\r\n".join(rebuilt).encode("utf-8")
        with open(FIXTURE_META, "rb") as fh:
            committed = fh.read()
        self.assertEqual(committed, produced,
                         "%s's persistent.loadmeta has drifted from a fresh "
                         "rebuild; re-run the builder and commit" % FIXTURE_NAME)

    def test_the_loadmeta_clock_agrees_with_the_save(self):
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        fs = builder.find_node(lines, "FLIGHTSTATE")
        save_ut = builder.get_value(lines, fs, "UT")
        meta = dict(
            line.split(" = ", 1) for line in builder.read_lines(FIXTURE_META)
            if " = " in line)
        self.assertEqual(save_ut, meta.get("UT"))
        self.assertEqual("1", meta.get("vesselCount"))
        self.assertEqual("CAREER", meta.get("gameMode"))

    def test_the_science_is_unbanked_which_is_the_whole_point(self):
        # THE CELL THAT WOULD HAVE SAVED L6's FIRST READING RUN. A `Science` node
        # under ResearchAndDevelopment is a BANKED subject; the recover mission
        # only reaches RECOVER through a TRANSMIT that credits at least
        # `transmitMinScienceGain`, and the schema's floor is 0.001, so a fixture
        # whose launchpad biome is already at cap can never reach the correlator.
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        rnd = builder._scenario_node(lines, "ResearchAndDevelopment")
        self.assertIsNotNone(rnd)
        banked = builder.child_nodes(lines, rnd, "Science")
        self.assertEqual([], banked,
                         "the fixture banks %d Science subject(s): a second flight "
                         "over the same biome would transmit for a 0.0 pool rise"
                         % len(banked))

    def test_the_live_launch_guid_differs_from_every_recorded_one(self):
        # THE SUBJECT, save side. `IsConclusiveLaunchGuidMismatch` compares the
        # live `Vessel.id` against each candidate's `recordedVesselGuid`; with a
        # shared guid the filter drops nothing and the lane measures
        # `guidDropped=0` on a fixture built to make it drop two.
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        fs = builder.find_node(lines, "FLIGHTSTATE")
        ship = builder.child_nodes(lines, fs, "VESSEL")[0]
        guid = builder.get_value(lines, ship, "pid")
        recorded = builder._values_named(lines, "recordedVesselGuid")
        self.assertEqual(builder.NEW_LAUNCH_GUID, guid)
        self.assertEqual([builder.RECORDED_VESSEL_GUID] * 2, recorded)
        self.assertNotIn(guid, recorded)

    def test_the_craft_baked_persistent_id_still_collides(self):
        # THE OTHER HALF OF THE SUBJECT, and it is deliberate rather than an
        # oversight: KSP bakes `persistentId` into the `.craft` and reuses it on
        # every launch, so a genuine relaunch DOES collide on pid while carrying a
        # fresh `Vessel.id`. Re-stamping it would build a fixture where a pid-only
        # correlator could not go wrong either, i.e. one that proves nothing.
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        fs = builder.find_node(lines, "FLIGHTSTATE")
        ship = builder.child_nodes(lines, fs, "VESSEL")[0]
        self.assertEqual(builder.RECORDED_VESSEL_PERSISTENT_ID,
                         builder.get_value(lines, ship, "persistentId"))
        self.assertEqual([builder.RECORDED_VESSEL_PERSISTENT_ID] * 2,
                         builder._values_named(lines, "vesselPersistentId"))

    def test_both_recordings_name_the_craft_the_flight_will_recover(self):
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        self.assertEqual([builder.RECORDED_VESSEL_NAME] * 2,
                         builder._values_named(lines, "vesselName"))

    def test_the_recordings_lie_wholly_in_the_past(self):
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        fs = builder.find_node(lines, "FLIGHTSTATE")
        ut = float(builder.get_value(lines, fs, "UT"))
        ends = [float(v) for v in builder._values_named(lines, "explicitEndUT")]
        self.assertTrue(ends)
        self.assertGreater(ut, max(ends))

    def test_no_rewind_hint_points_at_an_uncommitted_save(self):
        # `Parsek/Saves/` is pruned by the builder, so a surviving
        # `rewindSave = parsek_rw_*` would dangle - the shape
        # `CommittedFixtureRewindSaveTests` forbids corpus-wide.
        with open(FIXTURE_SFS, encoding="utf-8") as fh:
            text = fh.read()
        self.assertNotIn("rewindSave = parsek_rw_", text)
        self.assertFalse(os.path.isdir(os.path.join(FIXTURE_DIR, "Parsek", "Saves")))

    def test_the_earned_ledger_did_not_come_along(self):
        # The produced save's ledger credits exactly the science this fixture must
        # leave un-banked, and the recalculation engine patches KSP state from the
        # ledger. Copying it would re-create the blocker.
        self.assertFalse(os.path.isdir(
            os.path.join(FIXTURE_DIR, "Parsek", "GameState")))


class L6SpecFixtureSyncTests(unittest.TestCase):
    """The spec and the fixture must name each other."""

    @classmethod
    def setUpClass(cls):
        with open(SPEC_PATH, "rb") as fh:
            cls.spec = tomllib.load(fh)

    def test_the_spec_stages_this_fixture(self):
        self.assertEqual("fixtures/saves/%s" % FIXTURE_NAME,
                         self.spec["fixture"]["saveTemplate"])
        self.assertEqual("none", self.spec["fixture"]["injectedRecordings"])
        self.assertEqual([], self.spec["fixture"]["craft"])

    def test_the_spec_still_pins_the_two_correlator_tokens(self):
        # The lane's whole value is these two lines. A future edit that drops
        # either turns a green run into "the mission flew", which several lanes
        # already prove.
        required = self.spec["expectations"]["logContracts"]["required"]
        self.assertTrue(any("PickRecoveryRecordingId" in t and "guidDropped=2" in t
                            for t in required),
                        "the pick-line token with guidDropped=2 is gone: %r" % required)
        self.assertTrue(any("Recovery kerbal XP recorded" in t for t in required),
                        "the XP-row token is gone: %r" % required)

    def test_the_spec_asks_for_more_recordings_than_the_fixture_stages(self):
        # The fixture stages 2; a run that recovers must have recorded its own
        # launch too, so a floor of 3 is what says "the flight happened".
        self.assertGreaterEqual(self.spec["expectations"]["recordings"]["count"]["min"], 3)

    def test_the_mission_still_carries_a_transmit_floor(self):
        # Not a wish: reading run 1 failed on this gate, and the fixture is the
        # answer to it. If a future edit sets the floor at or below zero the
        # schema rejects the spec, so the assert is that the param is still there
        # and still positive - i.e. the fixture is still load-bearing.
        self.assertGreater(
            float(self.spec["driver"]["missionParams"]["transmitMinScienceGain"]), 0.0)


if __name__ == "__main__":
    unittest.main()
