"""Fixture gates for `refly-a-recorded`, the RE-FLY CONTINUATION subject.

WHAT THIS FILE GUARDS, and why the guard is unusual. Every other fixture drift
test asserts that a fixture still carries the payload its lanes read. This one
asserts that it still carries TWO DEFECTS - the split that drops the open bit,
and the predicted tail with a subsurface periapsis - because that is what the
RF-1 / RF-7M / RF-7T reading runs are authored against. A fixture whose defect
shapes healed is not a fixture that got better; it is a fixture whose lanes now
assert nothing, and the failure mode of NOT gating that is a green lane over a
subject that vanished.

So the cells below are deliberately two-sided. `mergeState` ABSENT on the chain
TIP and a periapsis BELOW the body radius are asserted as facts of these bytes,
each with a comment naming what a re-harvest against a fixed DLL would do to it
and what the operator must do then (re-fly the lane, do not re-pin the fixture).

IT CANNOT RE-RUN THE BUILD from the source, the same limit
`DunaOneRecordedFixtureDriftTests` states: the input is a 12 MB collected log
directory (`logs/2026-09-08_2317_refly-a-manual`) that is not committed and
never will be. What it CAN do is re-run every post-condition
`build_refly_a_recorded.py --check` runs, in process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "refly-a-recorded")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
RECORDINGS_DIR = os.path.join(FIXTURE_DIR, "Parsek", "Recordings")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_refly_a_recorded.py")
    spec = importlib.util.spec_from_file_location("build_refly_a_recorded", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ReflyARecordedFixtureDriftTests(unittest.TestCase):
    """WIRES `build_refly_a_recorded.py --check` INTO THE SUITE."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_the_committed_save_satisfies_every_post_condition(self):
        problems = self.builder.verify_save(self.builder.read_lines(FIXTURE_SFS))
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_committed_file_tree_satisfies_every_post_condition(self):
        problems = self.builder.verify_tree(FIXTURE_DIR)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_committed_sidecars_still_carry_the_predicted_tail(self):
        problems = self.builder.verify_prec(FIXTURE_DIR)
        self.assertEqual([], problems, "\n".join(problems))


class ReflyAContinuationDefectTests(unittest.TestCase):
    """DEFECT (1): the optimizer split that did not carry the open bit.

    Pinned SEPARATELY from the builder's own post-conditions rather than only
    through them, because these two cells are the ones whose failure means
    something other than "someone edited the fixture": they mean the shape the
    RF-1 lane reproduces is gone."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.lines = cls.builder.read_lines(FIXTURE_SFS)

    def _recording(self, recording_id):
        b = self.builder
        scn = b.parsek_scenario(self.lines)
        tree = b.child_nodes(self.lines, scn, "RECORDING_TREE")[0]
        for node in b.child_nodes(self.lines, tree, "RECORDING"):
            if b.get_value(self.lines, node, "recordingId") == recording_id:
                return node
        self.fail("recording %s is not in the tree" % recording_id)

    def test_the_head_keeps_the_promotion_and_the_tip_keeps_the_terminal(self):
        b = self.builder
        head = self._recording(b.POD_HEAD_ID)
        tip = self._recording(b.POD_TIP_ID)
        self.assertEqual("CommittedProvisional",
                         b.get_value(self.lines, head, "mergeState"))
        self.assertEqual("3", b.get_value(self.lines, head, "terminalState"),
                         "the HEAD's terminal is SubOrbital, re-derived at the "
                         "second scene exit after the split nulled it")
        self.assertEqual("4", b.get_value(self.lines, tip, "terminalState"),
                         "the TIP carries the Destroyed the extrapolator stamped")
        self.assertEqual((b.POD_CHAIN_ID, "0"),
                         (b.get_value(self.lines, head, "chainId"),
                          b.get_value(self.lines, head, "chainIndex")))
        self.assertEqual((b.POD_CHAIN_ID, "1"),
                         (b.get_value(self.lines, tip, "chainId"),
                          b.get_value(self.lines, tip, "chainIndex")))

    def test_the_tip_carries_no_merge_state_key(self):
        """THE DEFECT ITSELF, as an ABSENCE.

        `RecordingTreeRecordCodec` omits `mergeState` exactly when the value is
        `Immutable` and reads a missing key back as `Immutable`, so no key means
        the tip is CLOSED - and open/closed is read from the tip
        (`UnfinishedFlightClassifier.IsSlotEffectiveTipOpen`). If this cell ever
        fails because a key APPEARED, the fix landed and these bytes are no
        longer the defect's subject: RF-1 must be re-flown against the fixed DLL
        and this fixture re-harvested, NOT re-pinned."""
        b = self.builder
        tip = self._recording(b.POD_TIP_ID)
        self.assertIsNone(b.get_value(self.lines, tip, "mergeState"))

    def test_the_rewind_point_is_gone_and_the_supersede_row_is_live(self):
        """The two halves of the aftermath, in one cell.

        The RP was reaped by the same tip predicate (`ReapOrphanedRPs: reaped=1`),
        which is why no lane can re-fly THIS save; the probe's supersede row
        survived and is the only supersede topology in the committed corpus."""
        b = self.builder
        scn = b.parsek_scenario(self.lines)
        self.assertEqual([], b.child_nodes(self.lines, scn, "REWIND_POINTS"))
        rows = b.child_nodes(self.lines, scn, "RECORDING_SUPERSEDES")
        self.assertEqual(1, len(rows))
        entries = b.child_nodes(self.lines, rows[0], "ENTRY")
        self.assertEqual(1, len(entries))
        self.assertEqual(b.SUPERSEDE_OLD_ID,
                         b.get_value(self.lines, entries[0], "oldRecordingId"))
        self.assertEqual(b.SUPERSEDE_NEW_ID,
                         b.get_value(self.lines, entries[0], "newRecordingId"))


class ReflyARenderDefectTests(unittest.TestCase):
    """DEFECT (2): the predicted tail no surface drew."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        path = os.path.join(RECORDINGS_DIR, cls.builder.POD_TIP_ID + ".prec.txt")
        with open(path, "r", encoding="utf-8", errors="replace", newline="") as fh:
            cls.segments = cls.builder.read_top_level_orbit_segments(fh.read())

    def test_the_tip_carries_two_predicted_segments_to_the_impact_ut(self):
        b = self.builder
        predicted = [s for s in self.segments if s["isPredicted"] == "True"]
        self.assertEqual(2, len(predicted))
        self.assertEqual(b.PREDICTED_TAIL_START_UT, float(predicted[0]["startUT"]))
        self.assertEqual(b.PREDICTED_IMPACT_UT, float(predicted[-1]["endUT"]))
        # The tail extends the recording past the last RECORDED sample with zero
        # points in that span - the 1270 s hole the operator saw on the map.
        self.assertGreater(b.PREDICTED_IMPACT_UT - b.POD_TIP_LAST_RECORDED_UT,
                           1000.0)

    def test_every_tip_segment_has_a_subsurface_periapsis(self):
        """THE GEOMETRIC TRIGGER, arithmetic-anchored on the real elements.

        `IsOrbitSegmentBelowSurface` drops a conic whose periapsis radius is
        below the body radius from all three of its consumers, which is why the
        forward-arc pass drew nothing. A segment that CLEARED the surface would
        be drawn as an arc and RF-7M's headline clause would have no subject."""
        b = self.builder
        for index, seg in enumerate(self.segments):
            radius = b.periapsis_radius(float(seg["sma"]), float(seg["ecc"]))
            self.assertLess(radius, b.KERBIN_RADIUS_M,
                            "TIP segment %d clears the surface at %.3f m"
                            % (index, radius))
        # And the coast segment's margin, quoted so a drift is legible as a
        # number rather than as a boolean: 9,154.55 m inside the planet. The
        # value is re-measured off the committed bytes rather than copied from
        # any prose about them, which is why it is exact to ten digits here.
        coast = self.segments[1]
        self.assertAlmostEqual(
            9154.554461101652,
            b.KERBIN_RADIUS_M - b.periapsis_radius(float(coast["sma"]),
                                                   float(coast["ecc"])),
            delta=1.0)

    def test_the_chain_head_carries_no_segments_at_all(self):
        """Why map presence fell to `state-vector-fallback`.

        Source resolution reads the chain HEAD, and the split left every segment
        on the TIP. A HEAD that grew segments would mean the second defect's
        mechanism changed."""
        b = self.builder
        path = os.path.join(RECORDINGS_DIR, b.POD_HEAD_ID + ".prec.txt")
        with open(path, "r", encoding="utf-8", errors="replace", newline="") as fh:
            self.assertEqual([], b.read_top_level_orbit_segments(fh.read()))


class ReflyASegmentReaderTests(unittest.TestCase):
    """The two pure helpers, on synthetic shapes rather than on the fixture.

    The fixture proves they answered correctly ONCE; these prove they answer the
    same way on the shapes they are allowed to see - in particular that a segment
    NESTED inside an OrbitalCheckpoint TrackSection is not counted, which is the
    one trap in this format (the TIP's `.prec.txt` holds four `ORBIT_SEGMENT`
    blocks and exactly three of them are the recording's own)."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_a_nested_segment_is_not_top_level(self):
        text = "\n".join([
            "ORBIT_SEGMENT", "{", "\tstartUT = 1.0", "\tisPredicted = True", "}",
            "TRACK_SECTION", "{", "\tORBIT_SEGMENT", "\t{",
            "\t\tstartUT = 2.0", "\t\tisPredicted = False", "\t}", "}",
        ])
        got = self.builder.read_top_level_orbit_segments(text)
        self.assertEqual(1, len(got))
        self.assertEqual("1.0", got[0]["startUT"])

    def test_crlf_input_reads_the_same(self):
        text = "ORBIT_SEGMENT\r\n{\r\n\tstartUT = 1.0\r\n}\r\n"
        self.assertEqual([{"startUT": "1.0"}],
                         self.builder.read_top_level_orbit_segments(text))

    def test_periapsis_radius_matches_the_renderer_predicate(self):
        b = self.builder
        # The real coast conic: 9,154.55 m inside Kerbin.
        self.assertAlmostEqual(590845.4455388983,
                               b.periapsis_radius(865777.71214532177,
                                                  0.31755526014312058),
                               delta=1e-6)
        # A circular orbit well clear of the surface is NOT below it.
        self.assertGreater(b.periapsis_radius(700000.0, 0.0), b.KERBIN_RADIUS_M)


if __name__ == "__main__":
    unittest.main()
