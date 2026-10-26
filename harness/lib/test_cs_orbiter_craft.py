"""Drift and shape gates for `CS Orbiter.craft` (CS-4's craft, in the `cs-career-mc1`
fixture).

The craft is BUILT BY CONSTRUCTION by `harness/tools/build_cs_orbiter_craft.py` from
part tails lifted byte-for-byte out of stock craft. A hand edit to the craft, a change to
the derivation, a part off the tech the lane researches, a stack over the level-0 limits
or a staging order the mission does not expect reds here instead of in a flight.
"""
import importlib.util
import os
import re
import tomllib
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO_ROOT = os.path.dirname(HARNESS_ROOT)
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_cs_orbiter_craft.py")
CRAFT = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-mc1",
                     "Ships", "VAB", "CS Orbiter.craft")
SPEC = os.path.join(HARNESS_ROOT, "scenarios", "CS-4-career-orbit-return.toml")
UNLOCKS_DOC = os.path.join(REPO_ROOT, "docs", "dev", "research",
                           "stock-tech-tree-part-unlocks.md")
# The host's researched nodes plus the two the lane researches before the editor.
TECH_ON_HOST = ("start", "basicRocketry")
TECH_RESEARCHED_BY_THE_LANE = ("engineering101", "survivability")


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_cs_orbiter_craft", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n")


def _parts(text):
    return [p.rsplit("_", 1)[0] for p in re.findall(r"(?m)^\tpart = (\S+)$", text)]


def _unlocks(node):
    """Part names the unlock listing gives `node` (its `## Title (`node`)` table)."""
    text = _read(UNLOCKS_DOC)
    m = re.search(r"(?ms)^## [^\n]*\(`%s`\)\n(.*?)(?=^## |\Z)" % re.escape(node), text)
    if not m:
        raise AssertionError("no section for %s in %s" % (node, UNLOCKS_DOC))
    return set(re.findall(r"(?m)^\| `([^`]+)` \|", m.group(1)))


class CsOrbiterCraftTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()
        cls.fig = cls.b.figures()

    def test_the_committed_craft_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], self.b.check())

    def test_every_tail_matches_its_stock_donor_when_the_install_is_present(self):
        if not os.path.isdir(self.b.DEFAULT_DONOR_SHIPS):
            self.skipTest("no stock-tree install at %s" % self.b.DEFAULT_DONOR_SHIPS)
        self.assertEqual([], self.b.verify_donors(self.b.DEFAULT_DONOR_SHIPS))

    def test_the_craft_fits_the_level0_facilities(self):
        self.assertLessEqual(self.fig["parts"], 30)
        self.assertLessEqual(self.fig["mass"], 18.0)
        self.assertLessEqual(self.fig["height"], 20.0)
        self.assertEqual(self.fig["parts"], len(_parts(_read(CRAFT))))

    def test_the_craft_carries_the_plans_margins(self):
        self.assertGreaterEqual(self.fig["dv_total"], 3700.0)
        self.assertGreaterEqual(self.fig["liftoff_twr"], 1.2)

    def test_the_stage_order_is_the_one_the_mission_expects(self):
        table = self.b.stage_table(self.b.layout())
        self.assertEqual({4: ["solidBooster.v2"],
                          3: ["Decoupler.1", "solidBooster.v2"],
                          2: ["Decoupler.1", "liquidEngine2"],
                          1: ["Decoupler.1"],
                          0: ["parachuteSingle"]}, table)

    def test_the_heat_shield_is_not_staged_and_rides_the_pod(self):
        text = _read(CRAFT)
        block = text.split("\tpart = HeatShield1_", 1)[1].split("\n}", 1)[0]
        self.assertRegex(block, r"(?m)^\tistg = -1$")
        self.assertRegex(block, r"(?m)^\tattN = top,mk1pod\.v2_\d+_0\|0\.022\|0$")
        self.assertRegex(block, r"name = ModuleDecouple\n(?:\t\t.*\n)*?\t\tstagingEnabled = False")

    def test_every_part_is_on_the_tech_the_host_has_or_the_lane_researches(self):
        allowed = set()
        for node in TECH_ON_HOST + TECH_RESEARCHED_BY_THE_LANE:
            allowed |= _unlocks(node)
        self.assertEqual([], sorted(set(_parts(_read(CRAFT))) - allowed))

    def test_the_craft_needs_both_researched_nodes(self):
        # Pins WHY the lane researches them: the TD-12 and the heat shield.
        parts = set(_parts(_read(CRAFT)))
        self.assertTrue(parts & _unlocks("engineering101"))
        self.assertTrue(parts & _unlocks("survivability"))

    def test_the_root_is_the_pod_and_the_ship_is_named_for_the_editor_step(self):
        text = _read(CRAFT)
        self.assertEqual("mk1pod.v2", _parts(text)[0])
        self.assertRegex(text, r"(?m)^ship = CS Orbiter$")
        self.assertRegex(text, r"(?m)^version = 1\.12\.5$")

    def test_every_child_is_linked_from_its_parent(self):
        # A child its parent does not link is dropped at craft load, silently.
        text = _read(CRAFT)
        for token in re.findall(r"(?m)^\tpart = (\S+)$", text)[1:]:
            with self.subTest(part=token):
                self.assertIn("\tlink = %s\n" % token, text)

    def test_the_spec_flies_this_craft_after_researching_both_nodes(self):
        with open(SPEC, "rb") as fh:
            spec = tomllib.load(fh)
        self.assertEqual("fixtures/saves/cs-career-mc1", spec["fixture"]["saveTemplate"])
        steps = spec["driver"]["steps"]
        editor = next(i for i, s in enumerate(steps) if s.get("cmd") == "GoToEditor")
        self.assertEqual("CS Orbiter", steps[editor]["args"]["craft"])
        researched = [s["args"]["node"] for s in steps[:editor]
                      if s.get("cmd") == "KscAction"
                      and s["args"].get("action") == "research-node"]
        self.assertEqual(list(TECH_RESEARCHED_BY_THE_LANE), researched)

    def test_the_mission_stages_match_the_craft(self):
        with open(SPEC, "rb") as fh:
            params = tomllib.load(fh)["driver"]["missionParams"]
        # MechJeb stops after the Swivel stage; the machine's one owed reentry stage
        # is the pod separation; the chute is the mission's deploy, never a stage.
        self.assertEqual(self.b.STAGE_SWIVEL, params["autostageLimit"])
        self.assertEqual(1, params["reentryStageCount"])
        self.assertEqual(self.b.STAGE_POD_SEPARATION, self.b.STAGE_SWIVEL - 1)


if __name__ == "__main__":
    unittest.main()
