"""Drift and shape gates for the `lab-lko-science` fixture (the science-lab lanes' host).

The fixture is BUILT by `harness/tools/build_lab_lko_science.py`: `fresh-science`
(SCIENCE_SANDBOX) with the LABX Orbital Lab's orbiting VESSEL, crew rows,
ProgressTracking and ParsekScenario node spliced in from `lab-lko-sandbox` (the LABX-1
harvest), plus every tech node the craft needs researched. A hand edit to the fixture,
a change to either input save, or a change to the shared recipe reds here instead of in
a flight.
"""
import importlib.util
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_lab_lko_science.py")
SAVES = os.path.join(HARNESS_ROOT, "fixtures", "saves")
FIXTURE_DIR = os.path.join(SAVES, "lab-lko-science")


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_lab_lko_science", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n")


def _scenario_body(text, name):
    return re.search(r"\n\t\tname = %s\n(.*?)\n\t}\n" % re.escape(name), text, re.S).group(1)


class LabLkoScienceDriftTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.text = _read(os.path.join(FIXTURE_DIR, "persistent.sfs"))

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_build(self):
        self.assertEqual([], self.builder.check())

    def test_the_vessel_is_the_harvested_donors_byte_for_byte(self):
        recipe = self.builder._recipe
        fixture = recipe.read_lines(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        donor = recipe.read_lines(os.path.join(SAVES, "lab-lko-sandbox", "persistent.sfs"))
        ours = self.builder._vessel_named(fixture, "LABX Orbital Lab")
        theirs = self.builder._vessel_named(donor, "LABX Orbital Lab")
        self.assertIsNotNone(ours)
        self.assertEqual(donor[theirs[0]:theirs[1]], fixture[ours[0]:ours[1]])

    def test_science_sandbox_with_one_orbiting_crewed_vessel(self):
        self.assertRegex(self.text, r"(?m)^\tMode = SCIENCE_SANDBOX$")
        self.assertIn("\t\tsci = 100", _scenario_body(self.text, "ResearchAndDevelopment"))
        self.assertEqual(1, len(re.findall(r"(?m)^\t\tVESSEL$", self.text)))
        self.assertRegex(self.text, r"(?m)^\t\t\tsit = ORBITING$")
        self.assertRegex(self.text, r"(?m)^\t\t\t\tcrew = Valentina Kerman$")
        self.assertRegex(self.text, r"(?m)^\t\t\t\tcrew = Bob Kerman$")
        self.assertNotIn("type = SpaceObject", self.text)

    def test_the_parsek_footprint_is_the_inert_node_only(self):
        body = _scenario_body(self.text, "ParsekScenario")
        self.assertIn("\t\tgameStateEventCount = 12", body)
        for child in ("MILESTONE_STATE", "RECORDING_TREE", "LEDGER"):
            self.assertNotIn(child, body)
        self.assertFalse(os.path.exists(os.path.join(FIXTURE_DIR, "Parsek")))

    def test_every_craft_part_tech_node_is_researched(self):
        rnd = _scenario_body(self.text, "ResearchAndDevelopment")
        ids = re.findall(r"(?m)^\t\t\tid = (\S+)$", rnd)
        self.assertEqual(["start"] + [t[0] for t in self.builder.TECH_NODES], ids)
        for part, tech in sorted(self.builder.VESSEL_PART_TECH.items()):
            with self.subTest(part=part):
                self.assertIn(tech, ids)
                node = re.search(r"\t\t\tid = %s\n(.*?)\n\t\t}" % re.escape(tech),
                                 rnd, re.S).group(1)
                self.assertIn("\t\t\tpart = %s" % part, node.split("\n"))

    def test_the_lab_sits_behind_the_ctt_node_not_stock_advexploration(self):
        # The automation instance runs CommunityTechTree: the lab is in
        # specializedCommandModules. A stock-tree re-derivation would red here.
        self.assertEqual("specializedCommandModules",
                         self.builder.VESSEL_PART_TECH["Large.Crewed.Lab"])
        self.assertNotIn("id = advExploration", self.text)

    def test_a_missing_tech_node_would_be_caught(self):
        lines = list(self.builder.build_all()["persistent.sfs"])
        i = lines.index("\t\t\tpart = Large.Crewed.Lab")
        del lines[i]
        self.assertTrue(any("Large.Crewed.Lab is not unlocked" in p
                            for p in self.builder.verify(lines)))

    def test_a_scientist_out_of_the_lab_would_be_caught(self):
        lines = [l.replace("crew = Bob Kerman", "crew = Bill Kerman")
                 for l in self.builder.build_all()["persistent.sfs"]]
        self.assertTrue(any("Bob Kerman is seated" in p
                            for p in self.builder.verify(lines)))


if __name__ == "__main__":
    unittest.main()
