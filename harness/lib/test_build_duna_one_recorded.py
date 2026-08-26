"""Fixture gates for `duna-one-recorded`, the first FREE-PLAY harvested subject.

WHAT THIS FILE GUARDS. Every other RECORDED-state fixture is the verbatim output
of a driven harness run, so "did anyone edit it?" is answered by the run id in
`test_saveparse.RECORDED_FIXTURES`. This one is the operator's own hand-played
save put through a STRIP RECIPE (`harness/tools/build_duna_one_recorded.py`), and
a recipe that is not re-run is a recipe that rots. These cells wire that tool's
`--check` mode into the suite so a hand-edit of the committed bytes - or a future
harvest that quietly produces a different shape - reds here rather than on the
RC-WARP lane's next flight.

IT CANNOT RE-RUN THE BUILD, which is the one thing
`CareerEarnedPadFixtureDriftTests` does that this file does not, and the reason is
worth stating rather than leaving as a gap: that builder's inputs are two
COMMITTED fixtures, so re-splicing them and comparing bytes is cheap and total.
This fixture's input is a 24 MB collected log directory of a hand-played save
(`logs/2026-08-25_1537_s15-duna-one-manifest-run2`) that is not committed and
never will be. So the byte-stability claim is made against the RESULT instead:
re-running the INV2 containment dedupe over the committed `.prec` must drop
NOTHING, and the section list must be overlap-free in the exact sense
`Inv2NoDoubleCover` means. Together those say "the repair ran and its output is a
fixed point", which is the property a re-run would have proven.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "duna-one-recorded")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_duna_one_recorded.py")
    spec = importlib.util.spec_from_file_location("build_duna_one_recorded", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class DunaOneRecordedFixtureDriftTests(unittest.TestCase):
    """WIRES `build_duna_one_recorded.py --check` INTO THE SUITE."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_the_committed_save_satisfies_every_post_condition(self):
        problems = self.builder.verify_save(self.builder.read_lines(FIXTURE_SFS))
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_committed_file_tree_satisfies_every_post_condition(self):
        problems = self.builder.verify_tree(FIXTURE_DIR)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_inv2_repair_is_a_fixed_point(self):
        """The byte-stability claim: re-running the dedupe must drop nothing."""
        problems = self.builder.verify_prec(FIXTURE_DIR)
        self.assertEqual([], problems, "\n".join(problems))

    def test_the_repaired_recording_carries_the_documented_section_count(self):
        """75 sections in, 69 out - the six drops are enumerated in the tool.

        Pinned as a NUMBER as well as through the fixed-point cell above, because
        the two fail differently: a re-harvest that produced a recording with a
        different section count would still be a fixed point (nothing to dedupe)
        and would slip past every other cell here."""
        prec = os.path.join(FIXTURE_DIR, "Parsek", "Recordings",
                            self.builder.INV2_REPAIR_RECORDING_ID + ".prec")
        with open(prec, "rb") as fh:
            blob = fh.read()
        _count_offset, sections = self.builder.read_prec_sections(blob)
        self.assertEqual(self.builder.INV2_EXPECTED_SECTIONS_AFTER, len(sections))
        self.assertEqual(
            self.builder.INV2_EXPECTED_SECTIONS_BEFORE
            - len(self.builder.INV2_DROPPED_SECTION_INDICES),
            self.builder.INV2_EXPECTED_SECTIONS_AFTER,
            "the before/after/drop-list constants disagree with each other")

    def test_the_dropped_spans_are_gone_and_the_kept_ones_are_not(self):
        """The repair removed COVERAGE-REDUNDANT copies, not coverage.

        Each of the six dropped spans is still covered by a surviving section -
        that is the whole containment argument - so the assertion here cannot be
        "the span is absent". What CAN be asserted is the pair the defect was
        made of: the span no longer appears TWICE."""
        prec = os.path.join(FIXTURE_DIR, "Parsek", "Recordings",
                            self.builder.INV2_REPAIR_RECORDING_ID + ".prec")
        with open(prec, "rb") as fh:
            blob = fh.read()
        _count_offset, sections = self.builder.read_prec_sections(blob)
        spans = [(s["startUT"], s["endUT"]) for s in sections]
        duplicated = sorted(set(s for s in spans if spans.count(s) > 1))
        self.assertEqual([], duplicated,
                         "these spans are still carried by two sections: %r"
                         % (duplicated,))
        # And the four SOI seams the RECORDED_FIXTURES provenance quotes must
        # still be section boundaries: a repair that ate one would silently move
        # every offset in that comment.
        boundaries = set(x for span in spans for x in span)
        for label, ut in self.builder.SOI_SEAM_UTS:
            self.assertIn(ut, boundaries, "the %s seam moved" % label)

    def test_the_mirror_still_describes_the_same_section_list(self):
        """`.prec.txt` is a live test input; the two files must agree per index."""
        recordings = os.path.join(FIXTURE_DIR, "Parsek", "Recordings")
        prec = os.path.join(recordings,
                            self.builder.INV2_REPAIR_RECORDING_ID + ".prec")
        with open(prec, "rb") as fh:
            _count_offset, sections = self.builder.read_prec_sections(fh.read())
        with open(prec + ".txt", "r", encoding="utf-8", newline="") as fh:
            text = fh.read()
        spans = self.builder.text_section_spans(text.replace("\r\n", "\n"))
        self.assertEqual(len(sections), len(spans))
        for section, span in zip(sections, spans):
            self.assertEqual((section["startUT"], section["endUT"]),
                             (span[2], span[3]),
                             "section %d differs between the binary and its mirror"
                             % section["index"])


class DunaOneDedupePredicateTests(unittest.TestCase):
    """The pure predicate, exercised on synthetic shapes rather than the fixture.

    `find_redundant_sections` is the one piece of judgement in the repair, so it
    gets cells of its own: the fixture proves it produced the right answer ONCE,
    these prove it will answer the same way on the shapes it is allowed to see -
    and REFUSE to answer on the one it is not (a partial overlap, which cannot be
    repaired by dropping a section and must red instead of shipping)."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    @staticmethod
    def _sections(*triples):
        return [{"index": i, "startUT": a, "endUT": b, "start": 0, "end": size}
                for i, (a, b, size) in enumerate(triples)]

    def test_disjoint_touching_sections_drop_nothing(self):
        sections = self._sections((0.0, 10.0, 100), (10.0, 20.0, 100))
        self.assertEqual([], self.builder.find_redundant_sections(sections))
        self.assertEqual([], self.builder.overlapping_pairs(sections))

    def test_an_exact_duplicate_keeps_the_larger_payload(self):
        """The SOI-seam shape: a frame-less shell beside the checkpoint that
        carries the span's ORBIT_SEGMENT. The bigger record must survive."""
        sections = self._sections((0.0, 10.0, 65), (0.0, 10.0, 170))
        self.assertEqual([0], self.builder.find_redundant_sections(sections))

    def test_a_contained_section_is_dropped_whichever_side_it_sits_on(self):
        sections = self._sections((0.0, 100.0, 170), (0.0, 40.0, 65),
                                  (40.0, 100.0, 170))
        self.assertEqual([1, 2], self.builder.find_redundant_sections(sections))

    def test_a_partial_overlap_is_left_alone(self):
        """NOT repairable by dropping: neither section contains the other, so
        dropping either would lose coverage. The predicate must decline, and
        `repair_prec` turns the surviving overlap into a hard stop."""
        sections = self._sections((0.0, 60.0, 170), (40.0, 100.0, 170))
        self.assertEqual([], self.builder.find_redundant_sections(sections))
        self.assertEqual([(0, 1)], self.builder.overlapping_pairs(sections))

    def test_the_dedupe_never_changes_the_covered_union(self):
        sections = self._sections((0.0, 100.0, 170), (0.0, 40.0, 65),
                                  (40.0, 100.0, 170), (100.0, 130.0, 170))
        drop = set(self.builder.find_redundant_sections(sections))
        before = self.builder._covered_union(sections)
        after = self.builder._covered_union(
            [s for s in sections if s["index"] not in drop])
        self.assertEqual(before, after)


if __name__ == "__main__":
    unittest.main()
