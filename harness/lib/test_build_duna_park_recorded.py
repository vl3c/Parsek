"""Fixture gates for `duna-park-recorded`, the HELIOCENTRIC-PARKING subject.

WHAT THIS FILE GUARDS, AND WHY THE SHAPE PINS ARE NOT ENOUGH. `duna-park-recorded`
and `duna-one-recorded` are stripped from the SAME operator save and are both
crewed Duna missions with a multi-segment chain, ascent debris, a decoupled probe
and an EVA. `RECORDED_FIXTURES` pins tree/recording/terminal-state counts and the
recording ids, which distinguishes them as PAYLOADS - but nothing there says the
one thing that makes this a separate subject: that its transfer DEPARTS FROM A
HELIOCENTRIC PARKING ORBIT rather than from Kerbin.

That property lives in the transfer recording's ORBIT_SEGMENT list, which no
harness facet reads. `PARK_SIGNATURE` cells below read it directly, so a
re-harvest that swapped in a different flight - or a `.prec` edit that flattened
the park run - reds here instead of leaving a lane measuring a subject nobody
meant to ship.

IT CANNOT RE-RUN THE BUILD, for the reason `DunaOneRecordedFixtureDriftTests`
gives: the input is a 24 MB collected log directory of a hand-played save that is
not committed and never will be. The claims are made against the RESULT.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "duna-park-recorded")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
SIBLING_DIR = os.path.join(_HARNESS, "fixtures", "saves", "duna-one-recorded")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_duna_park_recorded.py")
    spec = importlib.util.spec_from_file_location(
        "build_duna_park_recorded", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class DunaParkRecordedFixtureDriftTests(unittest.TestCase):
    """WIRES `build_duna_park_recorded.py --check` INTO THE SUITE."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_the_committed_save_satisfies_every_post_condition(self):
        problems = self.builder.verify_save(
            self.builder.read_lines(FIXTURE_SFS))
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_committed_file_tree_satisfies_every_post_condition(self):
        problems = self.builder.verify_tree(FIXTURE_DIR)
        self.assertEqual([], problems, "\n".join(problems))

    def test_every_kept_sidecar_is_overlap_free_and_a_dedupe_fixed_point(self):
        """The byte-stability claim, made over ALL FOURTEEN recordings.

        Broader than the sibling's, which checks only the one it repaired: here
        the repaired recording must be a fixed point AND the other thirteen must
        still need nothing."""
        problems = self.builder.verify_prec(FIXTURE_DIR)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_repaired_recording_carries_the_documented_section_count(self):
        """52 sections in, 48 out - the four drops are enumerated in the tool.

        Pinned as a NUMBER as well as through the fixed-point cell above, because
        the two fail differently: a re-harvest that produced a recording with a
        different section count would still be a fixed point (nothing to dedupe)
        and would slip past every other cell here."""
        prec = os.path.join(FIXTURE_DIR, "Parsek", "Recordings",
                            self.builder.INV2_REPAIR_RECORDING_ID + ".prec")
        with open(prec, "rb") as fh:
            _count_offset, sections = self.builder.read_prec_sections(fh.read())
        self.assertEqual(self.builder.INV2_EXPECTED_SECTIONS_AFTER, len(sections))
        self.assertEqual(
            self.builder.INV2_EXPECTED_SECTIONS_BEFORE
            - len(self.builder.INV2_DROPPED_SECTION_INDICES),
            self.builder.INV2_EXPECTED_SECTIONS_AFTER,
            "the before/after/drop-list constants disagree with each other")


class DunaParkSignatureTests(unittest.TestCase):
    """THE CELLS THAT MAKE THIS A DIFFERENT SUBJECT FROM `duna-one-recorded`.

    Every number here was measured off the committed `.prec.txt`."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.segments = cls.builder.read_top_level_orbit_segments(
            os.path.join(FIXTURE_DIR, "Parsek", "Recordings",
                         cls.builder.PARK_TRANSFER_RECORDING_ID + ".prec.txt"))

    def test_the_park_signature_holds(self):
        problems = self.builder.verify_park_signature(FIXTURE_DIR)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_park_run_is_three_consecutive_sun_segments_at_one_sma(self):
        b = self.builder
        park = [self.segments[i] for i in b.PARK_SEGMENT_INDICES]
        self.assertEqual([b.PARK_BODY] * 3, [s["body"] for s in park])
        smas = [float(s["sma"]) for s in park]
        spread = (max(smas) - min(smas)) / b.PARK_SMA
        self.assertLess(spread, b.PARK_SMA_REL_TOLERANCE,
                        "the three park segments' sma spread %g relative - they "
                        "are no longer one parking orbit" % spread)

    def test_the_park_run_is_a_long_coast_not_a_transfer_arc(self):
        """A transfer arc between Kerbin and Duna is bounded by the transfer
        time; a PHASING orbit is held deliberately. 13.5 Ms (about 156 Kerbin
        days) is the measured value; the floor is set an order of magnitude
        below it so a genuinely different park still passes while a flattened
        one - or a swap to the direct sibling's profile - does not."""
        b = self.builder
        held = b.PARK_END_UT - b.PARK_START_UT
        self.assertAlmostEqual(13502219.935593963, held, places=3)
        self.assertGreater(held, 1.0e6)

    def test_the_departure_burn_is_an_element_step_out_of_the_park(self):
        """The park is only a park if something LEAVES it. The departure segment
        must differ from the park sma by orders more than the tolerance the park
        run itself has to satisfy."""
        b = self.builder
        departure = self.segments[b.DEPARTURE_SEGMENT_INDEX]
        step = abs(float(departure["sma"]) - b.PARK_SMA) / b.PARK_SMA
        self.assertGreater(step, 0.1,
                           "the segment after the park run is only %g relative "
                           "away from it: no departure burn" % step)
        self.assertGreater(step, b.PARK_SMA_REL_TOLERANCE * 1000)

    def test_it_actually_reaches_duna(self):
        b = self.builder
        duna = [s for s in self.segments if s["body"] == "Duna"]
        self.assertTrue(duna, "this subject must REACH Duna")
        self.assertEqual(b.DUNA_SOI_ENTRY_UT, float(duna[0]["startUT"]))
        # Arrival is HYPERBOLIC and capture follows: an eccentricity above 1 in
        # the first Duna segment and below 1 in the last is the difference
        # between a flyby and a mission that stays.
        self.assertGreater(float(duna[0]["ecc"]), 1.0)
        self.assertLess(float(duna[-1]["ecc"]), 1.0)

    def test_the_direct_sibling_has_no_such_park(self):
        """THE CONTRAST, ASSERTED RATHER THAN DESCRIBED. `duna-one-recorded`'s
        transfer also carries three consecutive Sun segments - which is exactly
        why the two subjects are easy to confuse - but they are ONE conic split
        by warp, and its departure burn happens inside Kerbin's SOI. The
        distinguishing fact is that NOTHING follows its Sun run at a different
        heliocentric sma: the next body change is Duna. If a future re-harvest
        ever made the sibling a parking mission too, this fixture would stop
        being a distinct subject, and that must red here."""
        b = self.builder
        sibling_mirror = os.path.join(
            SIBLING_DIR, "Parsek", "Recordings",
            "61e9177193444e329247d0e8288cf91e.prec.txt")
        if not os.path.isfile(sibling_mirror):
            self.skipTest("duna-one-recorded is not committed beside this one")
        segments = b.read_top_level_orbit_segments(sibling_mirror)
        sun = [s for s in segments if s["body"] == "Sun"]
        self.assertTrue(sun, "the direct sibling has no Sun segment at all")
        smas = [float(s["sma"]) for s in sun]
        spread = (max(smas) - min(smas)) / min(smas)
        self.assertLess(spread, 1e-3,
                        "the sibling's Sun segments now differ by %g relative - "
                        "it may have become a parking mission too" % spread)
        # ... and the sibling's single heliocentric sma is NOT this fixture's
        # park sma, so the two subjects cannot be confused by that number alone.
        self.assertGreater(abs(smas[0] - b.PARK_SMA) / b.PARK_SMA, 0.1)


class DunaParkCrewTests(unittest.TestCase):
    """The surviving reservation is chosen from the kept EVA, not by name."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.lines = cls.builder.read_lines(FIXTURE_SFS)

    def test_the_kept_slot_serves_the_kept_eva(self):
        b = self.builder
        scn = b.parsek_scenario(self.lines)
        tree = b.child_nodes(self.lines, scn, "RECORDING_TREE")[0]
        eva = [r for r in b.child_nodes(self.lines, tree, "RECORDING")
               if b.get_value(self.lines, r, "recordingId") == b.EVA_RECORDING_ID]
        self.assertEqual(1, len(eva))
        self.assertEqual(b.KEEP_KERBAL_OWNER,
                         b.get_value(self.lines, eva[0], "evaCrewName"))

    def test_the_standin_survives_in_the_roster(self):
        """A CREW_REPLACEMENTS entry naming a kerbal the ROSTER does not carry is
        a dangling reference, and it is exactly the shape the prune could produce
        by accident."""
        b = self.builder
        roster = b.find_node(self.lines, "ROSTER")
        names = {b.get_value(self.lines, k, "name")
                 for k in b.child_nodes(self.lines, roster, "KERBAL")}
        self.assertIn(b.KEEP_KERBAL_OWNER, names)
        self.assertIn(b.KEEP_KERBAL_STANDIN, names)


class DunaParkSegmentReaderTests(unittest.TestCase):
    """The one reader this recipe adds, on synthetic shapes.

    `read_top_level_orbit_segments` must match the UNINDENTED header only: an
    ORBIT_SEGMENT nested inside a TRACK_SECTION is a per-section checkpoint, and
    folding those into the recording's own list would inflate every count and
    scramble the park indices."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def _write(self, tmp, text):
        path = os.path.join(tmp, "x.prec.txt")
        with open(path, "w", encoding="utf-8", newline="") as fh:
            fh.write(text)
        return path

    def test_it_reads_top_level_records_in_order(self):
        import tempfile
        text = ("ORBIT_SEGMENT\n{\n\tsma = 1\n\tbody = Kerbin\n}\n"
                "ORBIT_SEGMENT\n{\n\tsma = 2\n\tbody = Sun\n}\n")
        with tempfile.TemporaryDirectory() as tmp:
            got = self.builder.read_top_level_orbit_segments(
                self._write(tmp, text))
        self.assertEqual([{"sma": "1", "body": "Kerbin"},
                          {"sma": "2", "body": "Sun"}], got)

    def test_it_ignores_a_segment_nested_in_a_track_section(self):
        import tempfile
        text = ("TRACK_SECTION\n{\n\tenv = 2\n"
                "\tORBIT_SEGMENT\n\t{\n\t\tsma = 99\n\t\tbody = Nope\n\t}\n}\n"
                "ORBIT_SEGMENT\n{\n\tsma = 1\n\tbody = Kerbin\n}\n")
        with tempfile.TemporaryDirectory() as tmp:
            got = self.builder.read_top_level_orbit_segments(
                self._write(tmp, text))
        self.assertEqual([{"sma": "1", "body": "Kerbin"}], got)

    def test_it_tolerates_crlf(self):
        import tempfile
        text = "ORBIT_SEGMENT\r\n{\r\n\tsma = 1\r\n\tbody = Sun\r\n}\r\n"
        with tempfile.TemporaryDirectory() as tmp:
            got = self.builder.read_top_level_orbit_segments(
                self._write(tmp, text))
        self.assertEqual([{"sma": "1", "body": "Sun"}], got)


class DunaParkStemTests(unittest.TestCase):
    """`_stem` must strip `.txt` FIRST - see the tool's docstring for the trap."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_the_mirrors_resolve_to_the_same_family(self):
        stem = self.builder._stem
        for name in ("abc.prec", "abc.pann", "abc_vessel.craft",
                     "abc_ghost.craft", "abc.prec.txt", "abc_vessel.craft.txt",
                     "abc_ghost.craft.txt"):
            self.assertEqual("abc", stem(name), name)


if __name__ == "__main__":
    unittest.main()
