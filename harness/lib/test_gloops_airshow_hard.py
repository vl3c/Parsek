"""Drift and shape gates for the `gloops-airshow-hard` fixture (RF-16 / RF-17's host).

The fixture is DERIVED from `gloops-airshow` by
`harness/tools/build_gloops_airshow_hard.py` (read its docstring for why): the S4.1
host with the three Hard-preset FLIGHT flags off, so a re-fly runs on a
`Flight.CanRestart = false` game (KSP-SETTINGS-AUDIT S7). These cells make that
derivation mechanical rather than remembered: a hand edit to either save, or a
change to the derivation, reds here instead of in a flight whose gate line would
simply be absent.
"""
import importlib.util
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_gloops_airshow_hard.py")
FIXTURE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gloops-airshow-hard", "persistent.sfs")
SOURCE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gloops-airshow", "persistent.sfs")
SCENARIOS = os.path.join(HARNESS_ROOT, "scenarios")
HOSTED_SPECS = ("RF-16-hard-preset-refly-exit-merge.toml",
                "RF-17-hard-preset-refly-exit-discard.toml")


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_gloops_airshow_hard", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _flight_flags(sfs_text: str) -> dict:
    """{key: value} of PARAMETERS/FLIGHT plus PARAMETERS' own `preset`."""
    builder = _load_builder()
    p_start, p_end = builder._node_span(sfs_text, "PARAMETERS", "\t")
    f_start, f_end = builder._node_span(sfs_text, "FLIGHT", "\t\t", p_start, p_end)
    out = dict(re.findall(r"(?m)^\t\t\t(\w+) = (.*)$", sfs_text[f_start:f_end]))
    out["preset"] = re.search(r"(?m)^\t\tpreset = (.*)$", sfs_text[p_start:p_end]).group(1)
    return out


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read()


class GloopsAirshowHardDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derivation_changes_exactly_the_four_values(self):
        src_lines, out_lines = _read(SOURCE).split("\n"), _read(FIXTURE).split("\n")
        self.assertEqual(len(src_lines), len(out_lines), "the derivation must not add or drop lines")
        changed = [(a.strip(), b.strip()) for a, b in zip(src_lines, out_lines) if a != b]
        self.assertEqual(
            [("preset = Normal", "preset = Hard"),
             ("CanQuickLoad = True", "CanQuickLoad = False"),
             ("CanRestart = True", "CanRestart = False"),
             ("CanLeaveToEditor = True", "CanLeaveToEditor = False")],
            changed)

    def test_the_fixture_is_a_canrestart_false_game_that_can_still_leave_the_flight(self):
        flags = _flight_flags(_read(FIXTURE))
        self.assertEqual("Hard", flags["preset"])
        self.assertEqual("False", flags["CanRestart"])
        self.assertEqual("False", flags["CanLeaveToEditor"])
        self.assertEqual("False", flags["CanQuickLoad"])
        # The exit the owner ruling routes Merge / Discard through must stay open.
        self.assertEqual("True", flags["CanLeaveToSpaceCenter"])

    def test_the_source_fixture_is_still_a_normal_game(self):
        # The premise the derivation rests on: S4.1's host keeps Retry reachable, so
        # the gate's Hard-preset line is RF-16 / RF-17's alone.
        flags = _flight_flags(_read(SOURCE))
        self.assertEqual("Normal", flags["preset"])
        self.assertEqual("True", flags["CanRestart"])

    def test_the_hosted_specs_stage_this_fixture(self):
        for name in HOSTED_SPECS:
            with self.subTest(spec=name):
                text = _read(os.path.join(SCENARIOS, name))
                self.assertRegex(
                    text, r'(?m)^saveTemplate\s*=\s*"fixtures/saves/gloops-airshow-hard"')
                self.assertRegex(text, r'(?m)^injectedRecordings\s*=\s*"rewind-b9"')


if __name__ == "__main__":
    unittest.main()
