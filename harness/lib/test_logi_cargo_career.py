"""Drift and shape gates for the `logi-cargo-career` fixture (the H73 host).

The fixture is BUILT by `harness/tools/build_logi_cargo_career.py`: `fresh-career`
(CAREER, funds 500,000, science 100, reputation 0, every facility at level 0, an empty
ProgressTracking tree) with `logi-cargo-pad`'s `Logi Cargo Rig` (uncrewed, PRELAUNCH)
as its one vessel, the donor's inert ParsekScenario node (its dead MILESTONE_STATE row
excised) inserted, and `lab-lko-science`'s 29 granted tech nodes added with the science
pool left at 100. It exists so `RouteRewindRedeliveryInGameTest`'s funds-flat assertion
runs with a live Funding singleton. A hand edit to the fixture, a change to either input
save or to TECH_NODES, or a change to the shared recipe reds here instead of in a flight.

Stdlib only; ASCII only; no em dashes.
"""
import importlib.util
import os
import re
import sys
import tomllib
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_logi_cargo_career.py")
SAVES = os.path.join(HARNESS_ROOT, "fixtures", "saves")
FIXTURE_DIR = os.path.join(SAVES, "logi-cargo-career")
SPEC_PATH = os.path.join(HARNESS_ROOT, "scenarios", "H73-logistics-isolated-career.toml")


def _load_builder():
    sys.path.insert(0, os.path.join(HARNESS_ROOT, "tools"))
    spec = importlib.util.spec_from_file_location("build_logi_cargo_career", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n")


def _scenario_body(text, name):
    return re.search(r"\n\t\tname = %s\n(.*?)\n\t}\n" % re.escape(name), text, re.S).group(1)


class LogiCargoCareerDriftTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()
        cls.text = _read(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        cls.base = _read(os.path.join(SAVES, "fresh-career", "persistent.sfs"))

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_build(self):
        self.assertEqual([], self.b.committed_problems(),
                         "re-run python harness/tools/build_logi_cargo_career.py")

    def test_the_vessel_is_the_pad_donors_byte_for_byte(self):
        r = self.b._recipe
        ours = r.read_lines(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        donor = r.read_lines(os.path.join(SAVES, "logi-cargo-pad", "persistent.sfs"))
        a = self.b._vessel_named(ours, "Logi Cargo Rig")
        d = self.b._vessel_named(donor, "Logi Cargo Rig")
        self.assertIsNotNone(a)
        self.assertEqual(donor[d[0]:d[1]], ours[a[0]:a[1]])

    def test_career_with_a_live_funding_pool(self):
        # The lane's whole point: Funding exists, so the redelivery cell's funds-flat
        # assertion runs instead of logging the Sandbox skip.
        self.assertRegex(self.text, r"(?m)^\tMode = CAREER$")
        self.assertIn("\t\tfunds = 500000", _scenario_body(self.text, "Funding"))
        self.assertIn("\t\tsci = 100", _scenario_body(self.text, "ResearchAndDevelopment"))
        self.assertIn("\t\trep = 0", _scenario_body(self.text, "Reputation"))

    def test_one_vessel_the_uncrewed_rig_active(self):
        fs = re.search(r"\n\tFLIGHTSTATE\n\t\{(.*?)\n\t\}\n", self.text, re.S).group(1)
        self.assertEqual(1, len(re.findall(r"\n\t\tVESSEL\n", fs)))
        self.assertIn("\n\t\tactiveVessel = 0\n", fs)
        self.assertNotIn("Ast. VPA-167", self.text)
        self.assertNotRegex(fs, r"\n\t+crew = ")
        roster = re.search(r"\n\tROSTER\n\t\{(.*?)\n\t\}\n", self.text, re.S).group(1)
        self.assertNotIn("state = Assigned", roster)

    def test_the_parsek_node_is_inert_and_present(self):
        body = _scenario_body(self.text, "ParsekScenario")
        self.assertNotIn("MILESTONE_STATE", body)
        self.assertIn("\t\tgameStateEventCount = 2", body)

    def test_the_granted_tech_is_lab_pad_careers_closure(self):
        # The same 29 nodes LAB-12's career host carries, byte for byte.
        def techs(text):
            rnd = _scenario_body(text, "ResearchAndDevelopment")
            return rnd[rnd.index("\t\tTech"):]
        lab = _read(os.path.join(SAVES, "lab-pad-career", "persistent.sfs"))
        self.assertEqual(techs(lab), techs(self.text))
        for part, node in sorted(self.b.RIG_PART_TECH.items()):
            self.assertRegex(
                self.text,
                r"\n\t\t\tid = %s\n\t\t\tstate = Available\n(\t\t\t[^\n]*\n)*?"
                r"\t\t\tpart = %s\n" % (node, re.escape(part)))

    def test_only_the_spliced_surfaces_differ_from_the_base(self):
        def strip(text):
            out = re.sub(r"\n\tFLIGHTSTATE\n\t\{.*?\n\t\}\n", "\n", text, flags=re.S)
            out = re.sub(r"\n\tSCENARIO\n\t\{\n\t\tname = ResearchAndDevelopment\n.*?\n\t\}\n",
                         "\n", out, flags=re.S)
            out = re.sub(r"\n\tSCENARIO\n\t\{\n\t\tname = ParsekScenario\n.*?\n\t\}\n",
                         "\n", out, flags=re.S)
            out = re.sub(r"\n\t(Title|Description) = [^\n]*", "", out)
            return out
        self.assertEqual(strip(self.base), strip(self.text))

    def test_the_lane_stages_this_fixture_and_claims_career(self):
        with open(SPEC_PATH, "rb") as fh:
            spec = tomllib.load(fh)
        self.assertEqual("fixtures/saves/logi-cargo-career", spec["fixture"]["saveTemplate"])
        self.assertIn("career", spec["dimensionsCovered"]["D14"])
        lc = spec["expectations"]["logContracts"]
        self.assertTrue(any("funds unchanged net=" in p for p in lc["required"]))
        self.assertTrue(any("no Funding singleton" in p for p in lc["forbidden"]))


if __name__ == "__main__":
    unittest.main()
