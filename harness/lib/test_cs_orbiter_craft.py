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
# The nodes CS-6c researched after its flight: the Z-100 and the OX-STAT.
TECH_RESEARCHED_FOR_POWER = ("basicScience", "electrics")
# Kerbal 1 (1.12.0) on its FL-T400: each OX-STAT's and each Z-100's attPos0 (noise of
# 1e-7 and below dropped) and rot, the four placements stock's radial symmetry wrote.
KERBAL1_PANELS = (
    ((-0.621777415, 0.575105727, 0.0), (0.0, -0.707106829, 0.0, 0.707106829)),
    ((0.0, 0.575105727, 0.621777415), (0.0, 0.0, 0.0, 1.0)),
    ((0.621777415, 0.575105727, 0.0), (0.0, 0.707106888, 0.0, 0.707106709)),
    ((0.0, 0.575105727, -0.621777415), (0.0, 1.0, 0.0, 0.0)),
)
KERBAL1_BATTERIES = (
    ((-0.448293895, 0.610907614, -0.448293895), (0.0, -0.382683456, 0.0, -0.923879564)),
    ((-0.448293895, 0.610907614, 0.448293895), (0.0, -0.923879564, 0.0, -0.382683456)),
    ((0.448294014, 0.610907614, 0.448293895), (0.0, -0.923879504, 0.0, 0.382683486)),
    ((0.448293805, 0.610907614, -0.448293895), (0.0, -0.382683456, 0.0, 0.923879564)),
)


def _same_rotation(a, b):
    """q and -q are one rotation: |<a, b>| == 1 for unit quaternions."""
    return abs(abs(sum(x * y for x, y in zip(a, b))) - 1.0) < 1e-6


class CsMunSolarLanderCraftTests(unittest.TestCase):
    """`CS Mun Solar Lander.craft` (CS-7's re-cut, `--variant solar`, in
    `cs-career-mun4`)."""

    @classmethod
    def setUpClass(cls):
        cls.b = _load_builder()
        cls.fig = cls.b.solar_figures()

    def test_the_committed_craft_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], self.b.solar_check())

    def test_the_other_variants_are_unchanged(self):
        # lander_layout / lander_figures gained keyword arguments whose defaults derive
        # CS Mun Lander: every committed craft must still derive byte-for-byte.
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

    def test_it_is_cs_mun_lander_without_the_cones_plus_the_power(self):
        lander = _parts(_read(LANDER_CRAFT))
        mine = _parts(_read(SOLAR_CRAFT))
        self.assertEqual(3, lander.count("noseCone"))
        self.assertEqual(0, mine.count("noseCone"))
        self.assertEqual(2, mine.count("solarPanels5"))
        self.assertEqual(1, mine.count("batteryPack"))
        self.assertEqual(3, lander.count("miniLandingLeg"))
        self.assertEqual(4, mine.count("miniLandingLeg"))
        rest = sorted(n for n in mine
                      if n not in ("solarPanels5", "batteryPack", "miniLandingLeg"))
        self.assertEqual(sorted(n for n in lander
                                if n not in ("noseCone", "miniLandingLeg")), rest)
        # The same propellant in every stage, the Terrier to the digit, and the legs at
        # the same height and radius.
        layout = self.b.solar_layout()
        base = self.b.lander_layout()
        for which in ("upper", "core", "boosters"):
            mine_prop = sum(self.b._pprop(p) for p in layout if p.group == which)
            theirs = sum(self.b._pprop(p) for p in base if p.group == which)
            self.assertAlmostEqual(theirs, mine_prop, places=6, msg=which)
        for name in ("liquidEngine3.v2", "mk1pod.v2"):
            self.assertEqual([p.pos for p in base if p.name == name],
                             [p.pos for p in layout if p.name == name], name)
        reach = {(round(p.pos[1], 9), round(math.hypot(p.pos[0], p.pos[2]), 9))
                 for p in layout if p.name == "miniLandingLeg"}
        self.assertEqual({(round(p.pos[1], 9), round(math.hypot(p.pos[0], p.pos[2]), 9))
                          for p in base if p.name == "miniLandingLeg"}, reach)
        self.assertAlmostEqual(self.b.lander_figures()["leg_clearance"],
                               self.fig["leg_clearance"], places=12)

    def test_the_craft_keeps_the_mun_landing_budget(self):
        self.assertGreaterEqual(self.fig["dv_lower"], 3130.0)
        self.assertGreaterEqual(self.fig["dv_upper"], 3500.0)
        self.assertGreaterEqual(self.fig["dv_total"], 6600.0)
        self.assertGreaterEqual(self.fig["liftoff_twr"], 1.25)
        self.assertGreaterEqual(self.fig["mun_twr"], 3.0)
        # The power and the fourth leg cost the lander under 50 m/s.
        self.assertLess(self.b.lander_figures()["dv_upper"] - self.fig["dv_upper"], 50.0)

    def test_the_stage_order_is_cs_mun_landers(self):
        self.assertEqual(self.b.lander_stage_want(),
                         self.b.stage_table(self.b.solar_layout()))

    def test_every_part_is_on_the_hosts_tech(self):
        allowed = set()
        for node in TECH_ON_TS1 + TECH_RESEARCHED_FOR_CS7 + TECH_RESEARCHED_FOR_POWER:
            allowed |= _unlocks(node)
        parts = set(_parts(_read(SOLAR_CRAFT)))
        self.assertEqual([], sorted(parts - allowed))
        self.assertIn("batteryPack", _unlocks("basicScience"))
        self.assertIn("solarPanels5", _unlocks("electrics"))
        self.assertEqual({"batteryPack", "solarPanels5"},
                         parts - set(_parts(_read(LANDER_CRAFT))))

    def test_the_power_rides_the_lander_tanks_unstaged_and_off_the_hatch(self):
        parts = self.b.solar_layout()
        upper = [p for p in parts if p.key.startswith("utank")]
        power = [p for p in parts if p.name in ("solarPanels5", "batteryPack")]
        self.assertEqual(3, len(power))
        for p in power:
            with self.subTest(part=p.key):
                self.assertEqual("upper", p.group)
                self.assertEqual((1, -1), (p.istg, p.sidx))
                self.assertEqual(1, p.attm)
                self.assertEqual(p.parent.uid, p.srfn)
                self.assertIn(p.uid, p.parent.links)
        panels = [p for p in power if p.name == "solarPanels5"]
        self.assertTrue(all(p.parent is upper[0] for p in panels))
        battery = [p for p in power if p.name == "batteryPack"][0]
        self.assertIs(upper[1], battery.parent)
        clear = self.b.solar_hatch_clearances(parts)
        self.assertEqual(3, len(clear))
        self.assertTrue(all(d >= 59.0 for d in clear.values()), clear)

    def test_the_attach_is_kerbal_1s_and_the_yaw_family_matches_every_donor(self):
        b = self.b
        for pos, rot in KERBAL1_PANELS:
            az = b._azimuth_deg(pos)
            self.assertAlmostEqual(b.SOLAR_PANEL_RADIUS, (pos[0] ** 2 + pos[2] ** 2) ** 0.5,
                                   places=6)
            self.assertAlmostEqual(b.SOLAR_PANEL_DY, pos[1], places=9)
            mine = tuple(float(c) for c in b._quat_y(90.0 - az).split(","))
            self.assertTrue(_same_rotation(rot, mine), (az, rot, mine))
        for pos, rot in KERBAL1_BATTERIES:
            az = b._azimuth_deg(pos)
            self.assertAlmostEqual(b.SOLAR_BATTERY_RADIUS,
                                   (pos[0] ** 2 + pos[2] ** 2) ** 0.5, places=6)
            self.assertAlmostEqual(b.SOLAR_BATTERY_DY, pos[1], places=9)
            mine = tuple(float(c) for c in b._quat_y(270.0 - az).split(","))
            self.assertTrue(_same_rotation(rot, mine), (az, rot, mine))
        # Ours sit where the formula puts them, on their parent tank.
        for p in b.solar_layout():
            if p.name not in ("solarPanels5", "batteryPack"):
                continue
            local = tuple(a - c for a, c in zip(p.pos, p.parent.pos))
            az = b._azimuth_deg(local)
            psi = 90.0 - az if p.name == "solarPanels5" else 270.0 - az
            got = tuple(float(c) for c in p.rot.split(","))
            want = tuple(float(c) for c in b._quat_y(psi).split(","))
            self.assertTrue(_same_rotation(want, got), p.key)

    def test_the_tails_are_the_static_panel_and_the_full_battery(self):
        panel = self.b.TAILS["solarPanels5"]
        self.assertIn("name = ModuleDeployableSolarPanel", panel)
        self.assertIn("deployState = EXTENDED", panel)
        self.assertNotIn("actionGroup = Custom", panel)
        self.assertEqual([("ElectricCharge", 100.0, 100.0)],
                         self.b.tail_resources("batteryPack"))
        self.assertEqual(("VAB/Kerbal 1.craft", 0), self.b.DONORS["solarPanels5"])
        self.assertEqual(("VAB/Kerbal 1.craft", 0), self.b.DONORS["batteryPack"])

    def test_the_electric_charge_budget(self):
        ec = self.fig["ec"]
        # The pod's 50 + the Z-100's 100.
        self.assertEqual(150.0, ec["capacity"])
        self.assertEqual(2, ec["panels"])
        self.assertAlmostEqual(0.70, ec["peak"], places=9)
        # Broadside sun, two panels back to back: 0 edge-on, 0.35 face-on, 2 / pi x 0.35
        # on average.
        self.assertAlmostEqual(0.0, ec["broadside_min"], places=3)
        self.assertAlmostEqual(0.350, ec["broadside_max"], places=3)
        self.assertAlmostEqual(0.223, ec["broadside_avg"], places=3)
        # The first flight's saturated coast draw empties the battery in about 6 min.
        self.assertAlmostEqual(357.0, ec["saturated_hold_s"], delta=1.0)

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
        # No Thumper keeps a top attach node it has nothing on.
        self.assertNotRegex(text, r"(?m)^\tattN = top,noseCone")

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
        self.assertEqual(self.b.LANDER_STAGE_POD_SEPARATION,
                         self.b.LANDER_STAGE_CORE_DROP - 1)


if __name__ == "__main__":
    unittest.main()
