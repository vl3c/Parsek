"""Drift and shape gates for the `coalescer-pad` fixture (H62's host).

The fixture is DERIVED from `gs1-two-stage-pad` by `harness/tools/build_coalescer_pad.py`
(read its docstring for why): the same craft with the decoupler moved into the first
stage so the Coalescer cells' single `ActivateNextStage()` separates a controlled
child on the pad instead of lighting the engine. These cells make that derivation
mechanical rather than remembered: a hand edit to either save, or a change to the
derivation, reds here instead of in a live flight that would read as a vacuous
`passed=0 skipped=2` batch.
"""
import importlib.util
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_coalescer_pad.py")
FIXTURE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "coalescer-pad", "persistent.sfs")
SOURCE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gs1-two-stage-pad", "persistent.sfs")


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_coalescer_pad", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _part_stages(sfs_text: str, vessel_name: str):
    """{part name: (istg, sqor)} for the named VESSEL, first occurrence of each part."""
    builder = _load_builder()
    start, end = builder._vessel_span(sfs_text, vessel_name)
    vessel = sfs_text[start:end]
    out = {}
    for m in re.finditer(r"\n\t\t\tPART\n\t\t\t\{\n\t\t\t\tname = ([^\n]+)\n((?:\t\t\t\t[^\n]*\n)*)", vessel):
        name, body = m.group(1), m.group(2)
        istg = re.search(r"\n?\t\t\t\tistg = (-?\d+)\n", "\n" + body)
        sqor = re.search(r"\n?\t\t\t\tsqor = (-?\d+)\n", "\n" + body)
        out.setdefault(name, (int(istg.group(1)), int(sqor.group(1))))
    return out


class CoalescerPadDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derivation_changes_exactly_the_two_stage_assignments(self):
        source = open(SOURCE, encoding="utf-8").read()
        derived = open(FIXTURE, encoding="utf-8").read()
        src_lines, out_lines = source.split("\n"), derived.split("\n")
        self.assertEqual(len(src_lines), len(out_lines), "the derivation must not add or drop lines")
        changed = [(a.strip(), b.strip()) for a, b in zip(src_lines, out_lines) if a != b]
        self.assertEqual(
            [("istg = 1", "istg = 2"), ("sqor = 1", "sqor = 2"),
             ("istg = 2", "istg = 1"), ("sqor = 2", "sqor = 1")],
            changed)

    def test_the_first_stage_on_the_pad_is_the_decoupler_and_the_engine_stays_unlit(self):
        builder = _load_builder()
        text = open(FIXTURE, encoding="utf-8").read()
        stages = _part_stages(text, builder.VESSEL_NAME)
        start, end = builder._vessel_span(text, builder.VESSEL_NAME)
        current = int(re.search(r"\n\t\t\tstg = (\d+)\n", text[start:end]).group(1))
        # ActivateNextStage fires inverse stage `stg - 1`: that must be the decoupler
        # alone among the stage-bearing parts, with the engine one stage later.
        self.assertEqual(3, current)
        self.assertEqual((2, 2), stages["Decoupler.1"])
        self.assertEqual((1, 1), stages["liquidEngine2"])
        first_stage = sorted(n for n, (istg, _) in stages.items() if istg == current - 1)
        self.assertEqual(["Decoupler.1"], first_stage)
        # Both halves of the split keep a controller: the pod above the decoupler and
        # the probe core below it (the cells skip on fewer than two command parts).
        self.assertIn("mk1pod.v2", stages)
        self.assertIn("probeCoreOcto2.v2", stages)

    def test_the_source_fixture_still_lights_the_engine_first(self):
        # The premise the derivation rests on, pinned so a future gs1 re-forge that
        # already puts the decoupler first makes this builder visibly redundant.
        builder = _load_builder()
        stages = _part_stages(open(SOURCE, encoding="utf-8").read(), builder.VESSEL_NAME)
        self.assertEqual((1, 1), stages["Decoupler.1"])
        self.assertEqual((2, 2), stages["liquidEngine2"])


if __name__ == "__main__":
    unittest.main()
