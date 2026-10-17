"""Drift and shape gates for the `lab-pad-career` fixture (the LAB-12 host).

The fixture is BUILT by `harness/tools/build_lab_pad_career.py`: `strategy-career` (CAREER,
funds 500,000, reputation 25, Administration level 0, an empty STRATEGIES node) with
`lab-pad-seeded`'s FLIGHTSTATE (the LABX Orbital Lab PRELAUNCH on the pad, its lab at 12.5
science), crew rows, inert ParsekScenario node and tech nodes spliced in, and the science
pool seeded to 400 so a Patents Licensing activation (263.25 science at the seam's factor
0.05) leaves 136.75. A hand edit to the fixture, a change to either input save, or a change
to the shared recipe reds here instead of in a flight.

Stdlib only; ASCII only; no em dashes.
"""
import importlib.util
import os
import re
import sys
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_lab_pad_career.py")
SAVES = os.path.join(HARNESS_ROOT, "fixtures", "saves")
FIXTURE_DIR = os.path.join(SAVES, "lab-pad-career")


def _load_builder():
    sys.path.insert(0, os.path.join(HARNESS_ROOT, "tools"))
    spec = importlib.util.spec_from_file_location("build_lab_pad_career", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n")


def _scenario_body(text, name):
    return re.search(r"\n\t\tname = %s\n(.*?)\n\t}\n" % re.escape(name), text, re.S).group(1)


class LabPadCareerDriftTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()
        cls.text = _read(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        cls.base = _read(os.path.join(SAVES, "strategy-career", "persistent.sfs"))

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_build(self):
        self.assertEqual([], self.b.committed_problems(),
                         "re-run python harness/tools/build_lab_pad_career.py")

    def test_the_vessel_is_the_pad_donors_byte_for_byte(self):
        r = self.b._recipe
        ours = r.read_lines(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        donor = r.read_lines(os.path.join(SAVES, "lab-pad-seeded", "persistent.sfs"))
        a = self.b._vessel_named(ours, "LABX Orbital Lab")
        d = self.b._vessel_named(donor, "LABX Orbital Lab")
        self.assertEqual(donor[d[0]:d[1]], ours[a[0]:a[1]])

    def test_career_with_the_pools_a_patents_activation_needs(self):
        self.assertRegex(self.text, r"(?m)^\tMode = CAREER$")
        self.assertIn("\t\tsci = 400", _scenario_body(self.text, "ResearchAndDevelopment"))
        self.assertIn("\t\tfunds = 500000", _scenario_body(self.text, "Funding"))
        self.assertIn("\t\trep = 25", _scenario_body(self.text, "Reputation"))
        # Patents Licensing: initialCostScience lerp(135, 2700, 0.05) = 263.25, no funds or
        # reputation cost, so the seed must cover it and leave room for the transmit.
        self.assertAlmostEqual(263.25, self.b.PATENTS_SETUP_SCIENCE, places=6)
        self.assertGreater(400.0 - self.b.PATENTS_SETUP_SCIENCE, 100.0)

    def test_one_free_strategy_slot_and_no_strategy_active(self):
        strat = _scenario_body(self.text, "StrategySystem")
        self.assertIn("\t\tSTRATEGIES\n\t\t{\n\t\t}", strat)
        fac = _scenario_body(self.text, "ScenarioUpgradeableFacilities")
        self.assertIn("\t\tSpaceCenter/Administration\n\t\t{\n\t\t\tlvl = 0\n\t\t}", fac)

    def test_only_the_spliced_surfaces_differ_from_the_base(self):
        # Outside FLIGHTSTATE, ROSTER, the R&D node, the inserted ParsekScenario and the
        # title / description, the save is strategy-career line for line.
        def strip(text):
            out = re.sub(r"\n\tFLIGHTSTATE\n\t\{.*?\n\t\}\n", "\n", text, flags=re.S)
            out = re.sub(r"\n\tROSTER\n\t\{.*?\n\t\}\n", "\n", out, flags=re.S)
            out = re.sub(r"\n\tSCENARIO\n\t\{\n\t\tname = ResearchAndDevelopment\n.*?\n\t\}\n", "\n", out, flags=re.S)
            out = re.sub(r"\n\tSCENARIO\n\t\{\n\t\tname = ParsekScenario\n.*?\n\t\}\n", "\n", out, flags=re.S)
            out = re.sub(r"\n\t(Title|Description) = [^\n]*", "", out)
            return out
        self.assertEqual(strip(self.base), strip(self.text))

    def test_the_crew_rows_are_assigned_and_the_applicant_kept(self):
        roster = re.search(r"\n\tROSTER\n\t\{(.*?)\n\t\}\n", self.text, re.S).group(1)
        for name in ("Valentina Kerman", "Bob Kerman"):
            row = re.search(r"\n\t\t\tname = %s\n(.*?)\n\t\t\}" % name, roster, re.S).group(1)
            self.assertIn("\n\t\t\tstate = Assigned", row)
        self.assertIn("name = Verhat Kerman", roster)

    def test_the_lab_and_antenna(self):
        self.assertIn("\t\t\t\t\tstoredScience = 12.5\n", self.text)
        self.assertIn("\t\t\t\t\tdataStored = 204\n", self.text)
        self.assertIn("\t\t\t\tname = SurfAntenna\n", self.text)
        self.assertIn("\t\t\tsit = PRELAUNCH\n", self.text)


if __name__ == "__main__":
    unittest.main()
