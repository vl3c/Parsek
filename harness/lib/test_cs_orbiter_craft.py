"""Drift and shape gates for `CS Orbiter.craft` (CS-4's craft, in the `cs-career-mc1`
fixture), `CS Mun Flyby.craft` (CS-5's, `--variant mun`, in `cs-career-ts1`),
`CS Mun Lander.craft` (CS-7's first cut, `--variant lander`, in `cs-career-mun2`),
`CS Mun Science.craft` (CS-6c's, `--variant munsci`, in `cs-career-mun3`) and
`CS Mun Solar Lander.craft` (CS-7's re-cut, `--variant solar`, in `cs-career-mun4`).

The craft is BUILT BY CONSTRUCTION by `harness/tools/build_cs_orbiter_craft.py` from
part tails lifted byte-for-byte out of stock craft. A hand edit to the craft, a change to
the derivation, a part off the tech the lane researches, a stack over the level-0 limits
or a staging order the mission does not expect reds here instead of in a flight.
"""
import importlib.util
import math
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


MUN_CRAFT = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-ts1",
                         "Ships", "VAB", "CS Mun Flyby.craft")
MUN_SPEC = os.path.join(HARNESS_ROOT, "scenarios", "CS-5-career-mun-flyby.toml")
# Every node the CS-5 host (`cs-career-ts1`) has researched; the lane researches none.
TECH_ON_TS1 = ("start", "basicRocketry", "engineering101", "survivability",
               "generalRocketry", "stability")


class CsMunFlybyCraftTests(unittest.TestCase):
    """`CS Mun Flyby.craft` (CS-5's craft, `--variant mun`, in `cs-career-ts1`)."""

    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()
        cls.fig = cls.b.mun_figures()

    def test_the_committed_craft_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], self.b.mun_check())

    def test_the_craft_fits_the_hosts_facilities(self):
        # VAB level 0 (30 parts); Launch Pad level 1 (140 t, 36 m), per the decompiled
        # GameVariables limits the builder names.
        self.assertLessEqual(self.fig["parts"], 30)
        self.assertLessEqual(self.fig["mass"], 140.0)
        self.assertLessEqual(self.fig["height"], 36.0)
        self.assertEqual(self.fig["parts"], len(_parts(_read(MUN_CRAFT))))

    def test_the_craft_carries_the_mun_budget(self):
        # ~3400 to orbit + 860 transfer + up to 200 of corrections and return trim + margin.
        self.assertGreaterEqual(self.fig["dv_total"], 4800.0)
        self.assertGreaterEqual(self.fig["dv_upper"], 1500.0)
        self.assertGreaterEqual(self.fig["liftoff_twr"], 1.25)
        self.assertEqual(0.6, self.fig["booster_thrust_fraction"])

    def test_the_stage_order_is_the_one_the_mission_expects(self):
        table = self.b.stage_table(self.b.mun_layout())
        self.assertEqual({4: ["liquidEngine2", "solidBooster1-1", "solidBooster1-1"],
                          3: ["radialDecoupler", "radialDecoupler"],
                          2: ["Decoupler.1", "liquidEngine2"],
                          1: ["Decoupler.1"],
                          0: ["parachuteSingle"]}, table)

    def test_every_part_is_on_the_hosts_tech(self):
        allowed = set()
        for node in TECH_ON_TS1:
            allowed |= _unlocks(node)
        parts = set(_parts(_read(MUN_CRAFT)))
        self.assertEqual([], sorted(parts - allowed))
        # The two nodes CS-4b researched are what make this craft possible.
        self.assertTrue(parts & _unlocks("generalRocketry"))
        self.assertTrue(parts & _unlocks("stability"))

    def test_the_heat_shield_is_not_staged_and_rides_the_pod(self):
        text = _read(MUN_CRAFT)
        block = text.split("\tpart = HeatShield1_", 1)[1].split("\n}", 1)[0]
        self.assertRegex(block, r"(?m)^\tistg = -1$")
        self.assertRegex(block, r"(?m)^\tattN = top,mk1pod\.v2_\d+_0\|0\.022\|0$")
        self.assertRegex(block, r"name = ModuleDecouple\n(?:\t\t.*\n)*?\t\tstagingEnabled = False")

    def test_the_root_is_the_pod_and_the_ship_is_named_for_the_editor_step(self):
        text = _read(MUN_CRAFT)
        self.assertEqual("mk1pod.v2", _parts(text)[0])
        self.assertRegex(text, r"(?m)^ship = CS Mun Flyby$")
        self.assertRegex(text, r"(?m)^version = 1\.12\.5$")

    def test_every_child_is_linked_from_its_parent(self):
        text = _read(MUN_CRAFT)
        for token in re.findall(r"(?m)^\tpart = (\S+)$", text)[1:]:
            with self.subTest(part=token):
                self.assertIn("\tlink = %s\n" % token, text)

    def test_a_child_of_a_rotated_decoupler_writes_its_offset_in_that_frame(self):
        # Kerbal 1's convention: the +x booster's attPos0 equals the -x one's, and
        # the world offset is that local offset turned by the decoupler's yaw.
        parts = self.b.mun_layout()
        boosters = [p for p in parts if p.name == "solidBooster1-1"]
        self.assertEqual(2, len(boosters))
        for p in boosters:
            self.assertEqual(self.b.MUN_BOOSTER_LOCAL, p.att_pos0)
            world = tuple(a - b for a, b in zip(p.pos, p.parent.pos))
            self.assertAlmostEqual(abs(self.b.MUN_BOOSTER_LOCAL[0]), abs(world[0]), places=6)
            self.assertAlmostEqual(self.b.MUN_BOOSTER_LOCAL[1], world[1], places=9)
        self.assertAlmostEqual(boosters[0].pos[0], -boosters[1].pos[0], places=9)

    def test_the_orbiter_variant_is_unchanged(self):
        # The variant adds tails and parts; the CS-4 craft must still derive byte-for-byte.
        self.assertEqual([], self.b.check())

    def test_the_spec_flies_this_craft_and_stops_autostage_above_the_pod(self):
        with open(MUN_SPEC, "rb") as fh:
            spec = tomllib.load(fh)
        self.assertEqual("fixtures/saves/cs-career-ts1", spec["fixture"]["saveTemplate"])
        steps = spec["driver"]["steps"]
        editor = next(i for i, s in enumerate(steps) if s.get("cmd") == "GoToEditor")
        self.assertEqual("CS Mun Flyby", steps[editor]["args"]["craft"])
        self.assertEqual([], [s for s in steps[:editor] if s.get("cmd") == "KscAction"])
        params = spec["driver"]["missionParams"]
        # MechJeb stops after the upper Swivel's stage; the return's one owed stage is
        # the pod separation.
        self.assertEqual(self.b.MUN_STAGE_CORE_DROP, params["autostageLimit"])
        self.assertEqual(1, params["returnStageCount"])
        self.assertEqual(self.b.MUN_STAGE_POD_SEPARATION, self.b.MUN_STAGE_CORE_DROP - 1)


LANDER_CRAFT = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-mun2",
                            "Ships", "VAB", "CS Mun Lander.craft")
CS7_SPEC = os.path.join(HARNESS_ROOT, "scenarios", "CS-7-career-mun-landing.toml")
CS6B_SPEC = os.path.join(HARNESS_ROOT, "scenarios", "CS-6b-career-astronaut-complex.toml")
# The node CS-7's KSC walk (and CS-6b's) researches before the editor.
TECH_RESEARCHED_FOR_CS7 = ("advRocketry",)


class CsMunLanderCraftTests(unittest.TestCase):
    """`CS Mun Lander.craft` (CS-7's craft, `--variant lander`, in `cs-career-mun2`)."""

    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()
        cls.fig = cls.b.lander_figures()

    def test_the_committed_craft_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], self.b.lander_check())

    def test_the_craft_fits_the_hosts_facilities(self):
        # VAB level 0 (30 parts); Launch Pad level 1 (140 t, 36 m).
        self.assertLessEqual(self.fig["parts"], 30)
        self.assertLessEqual(self.fig["mass"], 140.0)
        self.assertLessEqual(self.fig["height"], 36.0)
        self.assertEqual(self.fig["parts"], len(_parts(_read(LANDER_CRAFT))))

    def test_the_craft_carries_the_mun_landing_budget(self):
        # The lower stages to LKO (about 3130 on this basis, CS-6's flight), the lander
        # from LKO through the injection, corrections, capture, descent, ascent,
        # departure and trim (about 3110), and a lander that can land and lift off.
        self.assertGreaterEqual(self.fig["dv_lower"], 3130.0)
        self.assertGreaterEqual(self.fig["dv_upper"], 3500.0)
        self.assertGreaterEqual(self.fig["dv_total"], 6600.0)
        self.assertGreaterEqual(self.fig["liftoff_twr"], 1.25)
        self.assertGreaterEqual(self.fig["mun_twr"], 3.0)
        self.assertEqual(0.6, self.fig["booster_thrust_fraction"])

    def test_the_stage_order_is_the_one_the_mission_expects(self):
        table = self.b.stage_table(self.b.lander_layout())
        self.assertEqual({4: ["liquidEngine2", "solidBooster1-1", "solidBooster1-1",
                              "solidBooster1-1"],
                          3: ["radialDecoupler"] * 3,
                          2: ["Decoupler.1", "liquidEngine3.v2"],
                          1: ["Decoupler.1"],
                          0: ["parachuteSingle"]}, table)

    def test_every_part_is_on_the_hosts_tech_or_the_walks(self):
        allowed = set()
        for node in TECH_ON_TS1 + TECH_RESEARCHED_FOR_CS7:
            allowed |= _unlocks(node)
        parts = set(_parts(_read(LANDER_CRAFT)))
        self.assertEqual([], sorted(parts - allowed))
        # Advanced Rocketry is what makes the lander: the Terrier and the FL-T400.
        self.assertEqual({"fuelTank", "liquidEngine3.v2"}, parts & _unlocks("advRocketry"))
        self.assertIn("miniLandingLeg", parts & _unlocks("survivability"))

    def test_the_legs_ride_the_landers_lowest_tank_and_deploy_on_gear(self):
        parts = self.b.lander_layout()
        legs = [p for p in parts if p.name == "miniLandingLeg"]
        self.assertEqual(3, len(legs))
        tank = [p for p in parts if p.group == "upper" and p.name.startswith("fuelTank")][-1]
        for leg in legs:
            self.assertIs(tank, leg.parent)
            self.assertEqual(tank.uid, leg.srfn)
            self.assertAlmostEqual(0.6507, (leg.pos[0] ** 2 + leg.pos[2] ** 2) ** 0.5,
                                   places=3)
        self.assertIn("actionGroup = Gear", self.b.TAILS["miniLandingLeg"])
        # The Terrier hangs about 0.88 m below the legs' attach points; the LT-05's
        # stroke is 0.8 m plus its foot. A longer hang means a craft redesign.
        self.assertLess(self.fig["leg_clearance"], 1.0)

    def test_the_heat_shield_is_not_staged_and_rides_the_pod(self):
        text = _read(LANDER_CRAFT)
        block = text.split("\tpart = HeatShield1_", 1)[1].split("\n}", 1)[0]
        self.assertRegex(block, r"(?m)^\tistg = -1$")
        self.assertRegex(block, r"(?m)^\tattN = top,mk1pod\.v2_\d+_0\|0\.022\|0$")

    def test_the_root_is_the_pod_and_the_ship_is_named_for_the_editor_step(self):
        text = _read(LANDER_CRAFT)
        self.assertEqual("mk1pod.v2", _parts(text)[0])
        self.assertRegex(text, r"(?m)^ship = CS Mun Lander$")
        self.assertRegex(text, r"(?m)^version = 1\.12\.5$")

    def test_every_child_is_linked_from_its_parent(self):
        text = _read(LANDER_CRAFT)
        for token in re.findall(r"(?m)^\tpart = (\S+)$", text)[1:]:
            with self.subTest(part=token):
                self.assertIn("\tlink = %s\n" % token, text)

    def test_the_other_variants_are_unchanged(self):
        self.assertEqual([], self.b.check())
        self.assertEqual([], self.b.mun_check())

    def test_cs7_now_flies_the_solar_re_cut_of_this_craft(self):
        # CS-7's first flight (2026-10-27_2326) flew this craft and ran out of charge;
        # the lane now flies `--variant solar` from CS-6c's save. This cut stays
        # derivable (cs-career-mun2 / mun3 carry it) and stays out of the spec.
        with open(CS7_SPEC, "rb") as fh:
            spec = tomllib.load(fh)
        steps = spec["driver"]["steps"]
        editor = next(i for i, s in enumerate(steps) if s.get("cmd") == "GoToEditor")
        self.assertEqual(self.b.SOLAR_SHIP_NAME, steps[editor]["args"]["craft"])
        self.assertNotEqual(self.b.LANDER_SHIP_NAME, steps[editor]["args"]["craft"])

    def test_cs6b_researches_the_same_node(self):
        with open(CS6B_SPEC, "rb") as fh:
            steps = tomllib.load(fh)["driver"]["steps"]
        researched = [s["args"]["node"] for s in steps if s.get("cmd") == "KscAction"
                      and s["args"].get("action") == "research-node"]
        self.assertEqual(list(TECH_RESEARCHED_FOR_CS7), researched)



MUNSCI_CRAFT = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-mun3",
                            "Ships", "VAB", "CS Mun Science.craft")
CS6C_SPEC = os.path.join(HARNESS_ROOT, "scenarios", "CS-6c-career-mun-science.toml")


class CsMunScienceCraftTests(unittest.TestCase):
    """`CS Mun Science.craft` (CS-6c's craft, `--variant munsci`, in `cs-career-mun3`)."""

    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()
        cls.fig = cls.b.munsci_figures()

    def test_the_committed_craft_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], self.b.munsci_check())

    def test_the_other_variants_are_unchanged(self):
        # The variant adds tails, parts and two keyword arguments to mun_layout /
        # mun_figures whose defaults derive CS Mun Flyby: all three crafts must still
        # derive byte-for-byte.
        self.assertEqual([], self.b.check())
        self.assertEqual([], self.b.mun_check())
        self.assertEqual([], self.b.lander_check())

    def test_the_craft_fits_the_hosts_facilities(self):
        # VAB level 0 (30 parts: exactly at the limit); Launch Pad level 1 (140 t, 36 m).
        self.assertEqual(30, self.fig["parts"])
        self.assertLessEqual(self.fig["mass"], 140.0)
        self.assertLessEqual(self.fig["height"], 36.0)
        self.assertEqual(self.fig["parts"], len(_parts(_read(MUNSCI_CRAFT))))

    def test_it_is_cs_mun_flyby_with_the_upper_tanks_merged_and_the_science_added(self):
        flyby = _parts(_read(MUN_CRAFT))
        mine = _parts(_read(MUNSCI_CRAFT))
        self.assertEqual(4, flyby.count("fuelTankSmall") - mine.count("fuelTankSmall"))
        self.assertEqual(2, mine.count("fuelTank"))
        added = sorted(set(mine) - set(flyby))
        self.assertEqual(["GooExperiment", "fuelTank", "sensorThermometer"], added)
        self.assertEqual(2, mine.count("GooExperiment"))
        self.assertEqual(2, mine.count("sensorThermometer"))
        # The same propellant in every stage as CS Mun Flyby's.
        layout = self.b.munsci_layout()
        flyby_layout = self.b.mun_layout()
        for which in ("upper", "core", "boosters"):
            mine_prop = sum(self.b._pprop(p) for p in layout if p.group == which)
            theirs = sum(self.b._pprop(p) for p in flyby_layout if p.group == which)
            self.assertAlmostEqual(theirs, mine_prop, places=6, msg=which)

    def test_the_craft_keeps_the_mun_budget(self):
        # CS-6's route (orbit, transfer, corrections, capture, departure, trim) plus
        # the two lowering nodes (about 85 m/s) on the CS Mun Flyby basis.
        self.assertGreaterEqual(self.fig["dv_total"], 4800.0)
        self.assertGreaterEqual(self.fig["dv_upper"], 2450.0)
        self.assertGreaterEqual(self.fig["liftoff_twr"], 1.25)
        flyby = self.b.mun_figures()
        # The science costs the upper stage under 80 m/s and adds 0.11 t to the pod.
        self.assertLess(flyby["dv_upper"] - self.fig["dv_upper"], 80.0)
        self.assertAlmostEqual(0.11, self.fig["reentry_mass"] - flyby["reentry_mass"],
                               places=6)

    def test_the_stage_order_is_cs_mun_flybys(self):
        self.assertEqual(self.b.mun_stage_want(),
                         self.b.stage_table(self.b.munsci_layout()))

    def test_every_part_is_on_the_hosts_tech(self):
        allowed = set()
        for node in TECH_ON_TS1 + TECH_RESEARCHED_FOR_CS7:
            allowed |= _unlocks(node)
        parts = set(_parts(_read(MUNSCI_CRAFT)))
        self.assertEqual([], sorted(parts - allowed))
        self.assertIn("GooExperiment", _unlocks("start"))
        self.assertIn("sensorThermometer", _unlocks("engineering101"))
        self.assertIn("fuelTank", _unlocks("advRocketry"))

    def test_the_science_rides_the_pod_unstaged_and_off_the_hatch(self):
        parts = self.b.munsci_layout()
        pod = parts[0]
        science = [p for p in parts if p.name in ("GooExperiment", "sensorThermometer")]
        self.assertEqual(4, len(science))
        for p in science:
            with self.subTest(part=p.key):
                self.assertIs(pod, p.parent)
                self.assertEqual(pod.uid, p.srfn)
                self.assertEqual(1, p.attm)
                self.assertEqual((-1, -1), (p.istg, p.sidx))
                self.assertEqual("pod", p.group)
                self.assertIn(p.uid, pod.links)
        clear = self.b.munsci_hatch_clearances(parts)
        self.assertEqual(4, len(clear))
        self.assertTrue(all(d >= 59.0 for d in clear.values()), clear)

    def test_the_pairs_are_the_donor_attach_turned_about_the_pod_axis(self):
        parts = self.b.munsci_layout()
        pod = parts[0]
        for name, pos in (("GooExperiment", self.b.MUNSCI_GOO_POS),
                          ("sensorThermometer", self.b.MUNSCI_THERMO_POS)):
            pair = [p for p in parts if p.name == name]
            local = [tuple(a - b for a, b in zip(p.pos, pod.pos)) for p in pair]
            for got, want in zip(local[0], pos):
                self.assertAlmostEqual(want, got, places=9)
            for got, want in zip(local[1], (-pos[0], pos[1], -pos[2])):
                self.assertAlmostEqual(want, got, places=9)
            self.assertEqual([pair[1].uid], pair[0].syms)
        # The rotation algebra: quat_y(180) * q is the donor turned half a turn about
        # the pod axis; AeroEquus' own second Goo (placed by stock's symmetry) agrees.
        q2 = self.b._quat_mul(self.b._quat_tuple_y(180.0), self.b.MUNSCI_GOO_ROT)
        self.assertAlmostEqual(1.0, sum(c * c for c in q2), places=6)
        donor2 = (-0.00661472697, -0.707075894, 0.00661472231, -0.707075834)
        for got, want in zip(q2, donor2):
            self.assertAlmostEqual(want, got, places=6)

    def test_the_tails_are_the_donors_unedited(self):
        self.assertIn("name = ModuleScienceExperiment", self.b.TAILS["GooExperiment"])
        self.assertIn("name = ModuleScienceExperiment", self.b.TAILS["sensorThermometer"])
        # The thermometer's sensor stays off: it draws no ElectricCharge.
        self.assertIn("sensorActive = False", self.b.TAILS["sensorThermometer"])
        for key in ("GooExperiment", "sensorThermometer"):
            self.assertNotIn("actionGroup = Custom", self.b.TAILS[key])
            self.assertIn("Deployed = False", self.b.TAILS[key])

    def test_the_root_is_the_pod_and_the_ship_is_named_for_the_editor_step(self):
        text = _read(MUNSCI_CRAFT)
        self.assertEqual("mk1pod.v2", _parts(text)[0])
        self.assertRegex(text, r"(?m)^ship = CS Mun Science$")
        self.assertRegex(text, r"(?m)^version = 1\.12\.5$")

    def test_every_child_is_linked_from_its_parent(self):
        text = _read(MUNSCI_CRAFT)
        for token in re.findall(r"(?m)^\tpart = (\S+)$", text)[1:]:
            with self.subTest(part=token):
                self.assertIn("\tlink = %s\n" % token, text)

    def test_the_spec_flies_this_craft_and_stops_autostage_above_the_pod(self):
        with open(CS6C_SPEC, "rb") as fh:
            spec = tomllib.load(fh)
        self.assertEqual("fixtures/saves/cs-career-mun3", spec["fixture"]["saveTemplate"])
        steps = spec["driver"]["steps"]
        editor = next(i for i, s in enumerate(steps) if s.get("cmd") == "GoToEditor")
        self.assertEqual("CS Mun Science", steps[editor]["args"]["craft"])
        self.assertFalse(any(s.get("cmd") == "KscAction" for s in steps[:editor]))
        params = spec["driver"]["missionParams"]
        self.assertEqual(self.b.MUN_STAGE_CORE_DROP, params["autostageLimit"])
        self.assertEqual(1, params["returnStageCount"])
        # The two runs name exactly the experiment parts the craft carries: each part
        # holds one reading.
        counts = {"GooExperiment": 0, "sensorThermometer": 0}
        by_experiment = {"mysteryGoo": "GooExperiment",
                         "temperatureScan": "sensorThermometer"}
        for key in ("sciHighExperiments", "sciLowExperiments"):
            for name in params[key]:
                if name in by_experiment:
                    counts[by_experiment[name]] += 1
        self.assertEqual(self.b.MUNSCI_SCIENCE_PARTS, counts)

SOLAR_CRAFT = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-mun4",
                           "Ships", "VAB", "CS Mun Solar Lander.craft")
# The nodes CS-6c researched after its flight: basicScience and electrics (the Z-200).
TECH_RESEARCHED_FOR_POWER = ("basicScience", "electrics")


def _same_rotation(a, b):
    """q and -q are one rotation: |<a, b>| == 1 for unit quaternions."""
    return abs(abs(sum(x * y for x, y in zip(a, b))) - 1.0) < 1e-6


class CsMunSolarLanderCraftTests(unittest.TestCase):
    """`CS Mun Solar Lander.craft` (CS-7's craft, `--variant solar`, in
    `cs-career-mun4`): the outrigger stance with nose cones on the outriggers and
    CS Mun Lander's Thumpers on TT-38Ks on the core (the 2026-11-08 staging cut)."""

    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()
        cls.fig = cls.b.solar_figures()

    def test_the_committed_craft_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], self.b.solar_check())

    def test_the_other_variants_are_unchanged(self):
        # lander_layout / lander_stack_height gained keyword arguments and a Z-200 term
        # whose defaults derive CS Mun Lander: every committed craft must still derive.
        self.assertEqual([], self.b.check())
        self.assertEqual([], self.b.mun_check())
        self.assertEqual([], self.b.lander_check())
        self.assertEqual([], self.b.munsci_check())

    def test_the_craft_fits_the_hosts_facilities(self):
        # VAB level 0 (30 parts: exactly at the limit); Launch Pad level 1 (140 t, 36 m).
        self.assertEqual(30, self.fig["parts"])
        self.assertLessEqual(self.fig["mass"], 140.0)
        self.assertLessEqual(self.fig["height"], 36.0)
        self.assertEqual(self.fig["parts"], len(_parts(_read(SOLAR_CRAFT))))

    def test_it_is_cs_mun_lander_with_the_coned_outriggers_and_a_z200(self):
        lander = _parts(_read(LANDER_CRAFT))
        mine = _parts(_read(SOLAR_CRAFT))
        # The three cones move from the Thumpers' tops to the outriggers' tops; the
        # Thumpers keep their TT-38Ks; the power is one Z-200; one core FL-T400 goes.
        self.assertEqual(3, lander.count("noseCone"))
        self.assertEqual(3, mine.count("noseCone"))
        self.assertEqual(3, lander.count("radialDecoupler"))
        self.assertEqual(3, mine.count("radialDecoupler"))
        self.assertEqual(lander.count("Decoupler.1"), mine.count("Decoupler.1"))
        self.assertEqual(["batteryBankMini"],
                         [n for n in mine if n in ("batteryBankMini", "batteryPack",
                                                   "solarPanels5")])
        self.assertEqual(3, mine.count("miniLandingLeg"))
        self.assertEqual(3, mine.count("solidBooster1-1"))
        # The lander: one stack FL-T400 and three FL-T400 outriggers (CS Mun Lander's
        # 2 x FL-T400 + FL-T200); the core: 6 FL-T400 (7).
        self.assertEqual(0, mine.count("fuelTankSmall"))
        self.assertEqual(1 + 3 + self.b.SOLAR_CORE_TANKS, mine.count("fuelTank"))
        self.assertEqual(self.b.LANDER_CORE_TANKS - 1, self.b.SOLAR_CORE_TANKS)
        layout = self.b.solar_layout()
        base = self.b.lander_layout()

        def prop(parts, which):
            return sum(self.b._pprop(p) for p in parts if p.group == which)
        # 3 t of propellant moves from the core (one FL-T400 less) to the lander (three
        # FL-T200 become FL-T400); the Thumpers are CS Mun Lander's.
        self.assertAlmostEqual(prop(base, "upper") + 3.0, prop(layout, "upper"), places=9)
        self.assertAlmostEqual(prop(base, "core") - 2.0, prop(layout, "core"), places=9)
        self.assertAlmostEqual(prop(base, "boosters"), prop(layout, "boosters"), places=9)

        def leg_above_terrier(parts):
            terrier = [p for p in parts if p.name == "liquidEngine3.v2"][0]
            return {round(p.pos[1] - terrier.y, 9) for p in parts
                    if p.name == "miniLandingLeg"}
        self.assertEqual(leg_above_terrier(base), leg_above_terrier(layout))
        self.assertAlmostEqual(self.b.lander_figures()["leg_clearance"],
                               self.fig["leg_clearance"], places=12)
        self.assertEqual([p.pos for p in base if p.name == "mk1pod.v2"],
                         [p.pos for p in layout if p.name == "mk1pod.v2"])

    def test_the_outriggers_carry_a_leg_and_a_cone_each(self):
        b = self.b
        parts = b.solar_layout()
        tank = [p for p in parts if p.key.startswith("utank")]
        self.assertEqual(["fuelTank"], [p.name for p in tank])
        tank = tank[0]
        arms = [p for p in parts if p.key.startswith("arm")]
        self.assertEqual(3, len(arms))
        core = [p for p in parts if p.key.startswith("ctank")]
        for arm in arms:
            with self.subTest(arm=arm.key):
                self.assertEqual("fuelTank", arm.name)
                self.assertIs(tank, arm.parent)
                self.assertEqual(tank.uid, arm.srfn)
                self.assertEqual((1, -1, 1, "upper"),
                                 (arm.istg, arm.sidx, arm.attm, arm.group))
                local = tuple(x - y for x, y in zip(arm.pos, tank.pos))
                # Science Jr.'s Thumper axis off a 1.25 m core.
                self.assertAlmostEqual(0.630977273 + 0.796787977,
                                       math.hypot(local[0], local[2]), places=9)
                self.assertAlmostEqual(
                    tank.y + b.NODE_OFFSET["fuelTank"]["bottom"],
                    arm.y + b.NODE_OFFSET["fuelTank"]["bottom"], places=9)
                got = tuple(float(c) for c in arm.rot.split(","))
                want = tuple(float(c) for c in
                             b._quat_y(180.0 - b._azimuth_deg(local)).split(","))
                self.assertTrue(_same_rotation(want, got))
                kids = {p.name: p for p in parts if p.parent is arm}
                self.assertEqual({"miniLandingLeg", "noseCone"}, set(kids))
                # The leg on the outer wall, attPos0 / attRot0 in the outrigger's frame.
                leg = kids["miniLandingLeg"]
                self.assertEqual(arm.uid, leg.srfn)
                leg_local = tuple(x - y for x, y in zip(leg.pos, arm.pos))
                self.assertAlmostEqual(b._azimuth_deg(arm.pos), b._azimuth_deg(leg_local),
                                       places=6)
                self.assertAlmostEqual(0.0, leg.att_pos0[2], places=9)
                self.assertAlmostEqual(-math.hypot(leg_local[0], leg_local[2]),
                                       leg.att_pos0[0], places=9)
                self.assertEqual("0,0,0,1", leg.att_rot0)
                # The cone on the top node: stack attached on the outrigger's axis, no
                # relative turn; nothing on the bottom node (THE STAGING).
                cone = kids["noseCone"]
                parent = cone.parent
                self.assertEqual(0, cone.attm)
                self.assertIsNone(cone.srfn)
                self.assertEqual("0,0,0,1", cone.att_rot0)
                self.assertEqual(parent.rot, cone.rot)
                self.assertAlmostEqual(parent.pos[0], cone.pos[0], places=12)
                self.assertAlmostEqual(parent.pos[2], cone.pos[2], places=12)
                self.assertAlmostEqual(parent.y + b.NODE_OFFSET["fuelTank"]["top"],
                                       cone.y + b.NODE_OFFSET["noseCone"]["bottom01"],
                                       places=9)
                self.assertIn(("top", cone.uid, b.NODE_OFFSET["fuelTank"]["top"]),
                              parent.attn)
                self.assertIn(("bottom01", parent.uid, b.NODE_OFFSET["noseCone"]["bottom01"]),
                              cone.attn)
                self.assertEqual(("upper", -1), (cone.group, cone.sidx))
                self.assertNotIn("bottom", [n for n, _, _ in arm.attn])
        # The Thumpers are CS Mun Lander's: same TT-38Ks on the core's second tank from
        # the bottom, same placement relative to the core's bottom.
        base = b.lander_layout()

        def boosters(parts):
            bottom = [p for p in parts if p.key.startswith("ctank")][-1].y
            return sorted((p.name, tuple(round(c, 9) for c in
                                         (p.pos[0], p.pos[1] - bottom, p.pos[2])),
                           p.istg, p.group, p.parent.name)
                          for p in parts if p.name in ("radialDecoupler", "solidBooster1-1"))
        self.assertEqual(boosters(base), boosters(parts))
        hatch = b._azimuth_deg(b.MUNSCI_HATCH_POS)
        for arm in arms:
            off = abs((b._azimuth_deg(arm.pos) - hatch + 180.0) % 360.0 - 180.0)
            self.assertGreaterEqual(off, 59.9, arm.key)

    def test_only_the_thumper_tops_meet_the_airflow(self):
        # The outrigger cut flew six (three open Thumper tops, three outrigger tops) on
        # the launch stage and three on the core's; this cut closes the outriggers' and
        # keeps the Thumpers' three, as the first solar cut flew them (3392 m/s).
        self.assertEqual({"launch": 3, "core": 0}, self.fig["open_faces"])
        self.assertEqual(self.b.SOLAR_OPEN_FACES, self.fig["open_faces"])
        self.assertAlmostEqual(3392.0, self.b.SOLAR_FIRST_CUT_LKO_COST, places=6)
        self.assertGreaterEqual(self.fig["budget"]["at"]["first"]["margin"], 0.30)
        # The counter itself: CS Mun Lander's coned Thumpers read 0, without the cones 3.
        self.assertEqual({"launch": 0, "core": 0},
                         self.b.solar_open_faces(self.b.lander_layout()))
        self.assertEqual({"launch": 3, "core": 0},
                         self.b.solar_open_faces(self.b.lander_layout(cones=False)))

    def test_the_stance_tips_past_the_flown_slopes(self):
        # The Mun's equatorial band read 7-14 deg over 10 m at the flown site; the first
        # solar cut tipped at under 6 deg and fell over on both 2026-11-04 attempts; the
        # outrigger cut stood through the 2026-11-07 EVA, flag and boarding.
        new, old = self.fig["stance"], self.fig["stance_previous"]
        self.assertAlmostEqual(1.428 + 0.625, new["foot_radius"], delta=0.03)
        self.assertAlmostEqual(new["foot_radius"] / 2.0, new["edge"], delta=0.01)
        self.assertLess(old["tip_deg"], 7.0)
        self.assertGreaterEqual(new["tip_deg"], self.b.SOLAR_MIN_TIP_DEG)
        self.assertGreaterEqual(new["tip_deg"], 14.0 + 6.0)
        self.assertLess(new["com_height"], old["com_height"] - 1.5)

    def test_the_lander_reaches_lko_with_the_quarter_margin(self):
        b = self.b
        budget = self.fig["budget"]
        # The flight's own burns from LKO plus 25% on the landing, the Mun ascent, the
        # departure and the trim.
        self.assertAlmostEqual(842 + 126 + 266 + 1.25 * (710 + 753 + 220 + 100),
                               budget["need"], places=6)
        self.assertAlmostEqual(4418.0, b.SOLAR_FLOWN_LKO_COST, places=6)
        # The lander may pay about 1000 m/s of the Kerbin ascent; the margin holds to an
        # ascent of at least SOLAR_MIN_BREAKEVEN_LKO_COST (CS-6's clean 3130 + 270).
        self.assertGreaterEqual(budget["allowance"], 900.0)
        self.assertGreaterEqual(budget["breakeven"], b.SOLAR_MIN_BREAKEVEN_LKO_COST)
        self.assertGreaterEqual(b.SOLAR_MIN_BREAKEVEN_LKO_COST, 3130.0 + 250.0)
        self.assertGreaterEqual(budget["at"]["clean"]["margin"], 0.25)
        # On the flown ascent's cost the margin would be gone: the fix is the drag.
        self.assertLess(budget["at"]["flown"]["margin"], 0.25)
        self.assertGreaterEqual(self.fig["dv_upper"], 3500.0)
        self.assertGreaterEqual(self.fig["dv_total"], 6600.0)
        self.assertGreaterEqual(self.fig["liftoff_twr"], 1.25)
        self.assertGreaterEqual(self.fig["stages"][1]["twr_vac"], 1.0)
        self.assertGreaterEqual(self.fig["mun_twr"], 3.0)

    def test_the_stage_order_drops_the_thumpers_on_the_tt38ks(self):
        want = self.b.lander_stage_want()
        self.assertEqual(want, self.b.solar_stage_want())
        self.assertEqual(want, self.b.stage_table(self.b.solar_layout()))

    def test_the_staging_simulation_drops_each_group_and_keeps_the_lander(self):
        b = self.b
        text = _read(SOLAR_CRAFT)
        self.assertEqual([], b.solar_staging(b.solar_layout(), text))
        names = lambda uids: sorted(u.rsplit("_", 1)[0] for u in uids)
        sim = dict((stage, (dropped, left)) for stage, dropped, left in b.staging_sim(text))
        dropped, left = sim[b.LANDER_STAGE_BOOSTER_DROP]
        self.assertEqual(["solidBooster1-1"] * 3, names(dropped))
        dropped, left = sim[b.LANDER_STAGE_CORE_DROP]
        self.assertEqual(["Decoupler.1"] + ["fuelTank"] * b.SOLAR_CORE_TANKS
                         + ["liquidEngine2"] + ["radialDecoupler"] * 3, names(dropped))
        # The lander keeps its four tanks, three legs and three cones to the separation.
        lander = names(left)
        self.assertEqual(4, lander.count("fuelTank"))
        self.assertEqual(3, lander.count("miniLandingLeg"))
        self.assertEqual(3, lander.count("noseCone"))
        self.assertEqual(16, len(left))
        dropped, left = sim[b.LANDER_STAGE_POD_SEPARATION]
        self.assertEqual(["HeatShield1", "batteryBankMini", "mk1pod.v2", "parachuteSingle"],
                         names(left))
        # Each liquid engine draws from its own stage's tanks only.
        tree = b.craft_tree(text)
        _, left = sim[b.LANDER_STAGE_LAUNCH]
        for engine, want in (("liquidEngine2", b.SOLAR_CORE_TANKS), ("liquidEngine3.v2", 4)):
            uid = [u for u, n in tree.items() if n["name"] == engine][0]
            self.assertEqual(want, len(b.engine_feeds(tree, uid, left)), engine)

    def test_the_staging_gate_refuses_the_flown_hung_thumpers(self):
        # baf036d's fuel cut: each Thumper dropped by a TD-12 along the core, 0.18 m
        # beside it (both 2026-11-07 attempts lost in MJ-ASCENT, THE STAGING).
        bad = self.b.solar_staging(self.b.solar_hung_layout())
        self.assertEqual(3, len(bad), bad)
        for line in bad:
            self.assertRegex(line, r"^stage 3 drops solidBooster1-1_\d+ along the stack "
                                   r"axis 0\.18 m beside the stack \(< 1\.0\)$")

    def test_the_staging_gate_refuses_a_misstaged_decoupler(self):
        # The pod's TD-12 fired with the boosters would take the whole lander away.
        b = self.b
        parts = b.solar_layout()
        dec1 = [p for p in parts if p.key == "dec1"][0]
        dec1.istg = dec1.sqor = b.LANDER_STAGE_BOOSTER_DROP
        bad = b.solar_staging(parts)
        self.assertTrue(any("off the lander" in m for m in bad), bad)

    def test_every_part_is_on_the_hosts_tech(self):
        allowed = set()
        for node in TECH_ON_TS1 + TECH_RESEARCHED_FOR_CS7 + TECH_RESEARCHED_FOR_POWER:
            allowed |= _unlocks(node)
        parts = set(_parts(_read(SOLAR_CRAFT)))
        self.assertEqual([], sorted(parts - allowed))
        self.assertIn("batteryBankMini", _unlocks("electrics"))
        self.assertEqual({"batteryBankMini"}, parts - set(_parts(_read(LANDER_CRAFT))))

    def test_the_z200_rides_the_pod_under_the_mk16(self):
        b = self.b
        parts = b.solar_layout()
        pod = parts[0]
        bat = [p for p in parts if p.name == "batteryBankMini"]
        self.assertEqual(1, len(bat))
        bat = bat[0]
        chute = [p for p in parts if p.name == "parachuteSingle"][0]
        self.assertIs(pod, bat.parent)
        self.assertIs(bat, chute.parent)
        self.assertEqual(("pod", -1, 0), (bat.group, bat.sidx, bat.attm))
        self.assertEqual([bat.uid] + [u for u in pod.links if u != bat.uid], pod.links)
        self.assertNotIn(chute.uid, pod.links)
        self.assertAlmostEqual(pod.y + b.NODE_OFFSET["mk1pod.v2"]["top"],
                               bat.y + b.NODE_OFFSET["batteryBankMini"]["bottom"], places=9)
        self.assertAlmostEqual(bat.y + b.NODE_OFFSET["batteryBankMini"]["top"],
                               chute.y + b.NODE_OFFSET["parachuteSingle"]["bottom"],
                               places=9)
        # The chute keeps its stage; the pod's top node now names the Z-200.
        self.assertEqual((0, 0), (chute.istg, chute.sidx))
        self.assertIn(("top", bat.uid, b.NODE_OFFSET["mk1pod.v2"]["top"]), pod.attn)
        self.assertEqual(("VAB/ComSat Lx.craft", 0), b.DONORS["batteryBankMini"])
        self.assertEqual([("ElectricCharge", 200.0, 200.0)],
                         b.tail_resources("batteryBankMini"))

    def test_the_electric_charge_budget(self):
        ec = self.fig["ec"]
        # The pod's 50 + the Z-200's 200, all on the pod (the return keeps it).
        self.assertEqual(250.0, ec["capacity"])
        self.assertEqual(250.0, ec["pod_capacity"])
        self.assertEqual(0, ec["panels"])
        # The worst case measured on the panelled cuts' two flights, gross of their
        # panels (THE ELECTRIC-CHARGE BUDGET): 156 EC, needing 195 with the margin.
        self.assertAlmostEqual(156.0, ec["worst"], places=6)
        self.assertAlmostEqual(195.0, ec["need"], places=6)
        self.assertEqual(0.25, self.b.SOLAR_EC_MARGIN)
        self.assertGreaterEqual(ec["margin"], self.b.SOLAR_EC_MARGIN)
        self.assertAlmostEqual(0.599, ec["margin"], delta=0.001)
        # The lines nearest the watchdog, read off the rows.
        lines = dict((ph, used) for ph, used, _ in self.b.SOLAR_EC_WORST_CASE)
        self.assertEqual(9.3, lines["landed settle (SAS on the ground)"])
        self.assertEqual(42.6, lines["descent"])
        self.assertEqual(43.1, lines["Mun ascent"])
        self.assertFalse([m for m in self.b.solar_problems() if "ElectricCharge" in m])

    def test_the_electric_charge_gate_refuses_the_pod_alone(self):
        # Without the Z-200 the pod's own 50 EC is short of the measured worst case.
        parts = [p for p in self.b.solar_layout() if p.name != self.b.SOLAR_BATTERY_NAME]
        ec = self.b.solar_ec_budget(parts)
        self.assertEqual(50.0, ec["pod_capacity"])
        self.assertLess(ec["pod_capacity"] - self.b.SOLAR_EC_FLOOR, ec["need"])

    def test_the_root_is_the_pod_and_the_ship_is_named_for_the_editor_step(self):
        text = _read(SOLAR_CRAFT)
        self.assertEqual("mk1pod.v2", _parts(text)[0])
        self.assertRegex(text, r"(?m)^ship = CS Mun Solar Lander$")
        self.assertRegex(text, r"(?m)^version = 1\.12\.5$")

    def test_every_child_is_linked_from_its_parent(self):
        text = _read(SOLAR_CRAFT)
        for token in re.findall(r"(?m)^\tpart = (\S+)$", text)[1:]:
            with self.subTest(part=token):
                self.assertIn("\tlink = %s\n" % token, text)
        # Each Thumper rides a TT-38K by surface attach (THE STAGING); no TD-12 holds one.
        self.assertEqual(3, len(re.findall(r"(?m)^\tsrfN = srfAttach,radialDecoupler_\d+$",
                                           text)))
        self.assertEqual(0, len(re.findall(r"(?m)^\tattN = top,Decoupler\.1_\d+_0\|3\.92\|0$",
                                           text)))

    def test_the_spec_flies_this_craft_from_cs6cs_save(self):
        with open(CS7_SPEC, "rb") as fh:
            spec = tomllib.load(fh)
        # The ladder is a chain: CS-7 flies from CS-6c's produced save, which holds
        # every node the craft needs, so CS-7 runs no KSC step.
        self.assertEqual("fixtures/saves/cs-career-mun4", spec["fixture"]["saveTemplate"])
        steps = spec["driver"]["steps"]
        editor = next(i for i, s in enumerate(steps) if s.get("cmd") == "GoToEditor")
        self.assertEqual("CS Mun Solar Lander", steps[editor]["args"]["craft"])
        self.assertFalse(any(s.get("cmd") == "KscAction" for s in steps[:editor]))
        with open(CS6C_SPEC, "rb") as fh:
            cs6c_steps = tomllib.load(fh)["driver"]["steps"]
        researched = [s["args"]["node"] for s in cs6c_steps
                      if s.get("cmd") == "KscAction"
                      and s["args"].get("action") == "research-node"]
        self.assertEqual(list(TECH_RESEARCHED_FOR_POWER), researched)
        host = _read(os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-career-mun4",
                                  "persistent.sfs"))
        for node in TECH_ON_TS1 + TECH_RESEARCHED_FOR_CS7 + TECH_RESEARCHED_FOR_POWER:
            self.assertRegex(host, r"\n\t\tTech\n\t\t\{\n\t\t\tid = %s\n\t\t\tstate = "
                                   r"Available\n" % node, node)
        params = spec["driver"]["missionParams"]
        # MechJeb stops after the Terrier's stage; the return's one owed stage is the
        # pod separation; the landing extends the LT-05s; the coast releases the wheels.
        self.assertEqual(self.b.LANDER_STAGE_CORE_DROP, params["autostageLimit"])
        self.assertEqual(1, params["returnStageCount"])
        self.assertTrue(params["landingDeployGears"])
        self.assertTrue(params["ascentCoastWheelRelease"])
        self.assertGreater(params["electricChargeFloor"], 0.0)
        # The EC budget's margin is taken above the watchdog's own floor.
        self.assertEqual(self.b.SOLAR_EC_FLOOR, params["electricChargeFloor"])
        self.assertEqual(self.b.LANDER_STAGE_POD_SEPARATION,
                         self.b.LANDER_STAGE_CORE_DROP - 1)
        # The rollout debit is the builder's cost plus KSP's +35, as a 100-funds window.
        rollout = [r for r in spec["expectations"]["logContracts"]["required"]
                   if "VesselRollout" in r and "2932" not in r]
        self.assertEqual(1, len(rollout))
        predicted = round(self.fig["cost"]) + 35
        self.assertRegex("Game state: FundsChanged -%d (VesselRollout)" % predicted,
                         rollout[0])


if __name__ == "__main__":
    unittest.main()
