"""Fixture gates for `miner-rover-landed` (the mining / ISRU host).

The fixture is DERIVED by `harness/tools/build_miner_rover_landed.py` from
`fresh-sandbox` (the base) and V's real `Mining Rover RC` VESSEL node
(`fixtures/donors/isru-mining-rover/mining-rover-rc.vessel`), so a change to either must
re-run the builder. These cells turn that "must" into a red, and pin the properties the
mining lanes depend on: the rover is vessel index 0 and active, LANDED,
crewless, keeps its own persistentId, carries four drills, one ISRU and two ore tanks,
holds no ReStock module (the harness instance is stock), starts with empty Ore /
LiquidFuel / Oxidizer / MonoPropellant tanks that still have room, and every converter
timestamp sits at the fixture UT so nothing catches up on load. Vessel index 1 is the
`Observer Probe`, a crewless clone of `eva2-lko-crewed`'s `Kerbal X Probe` in low Kerbin
orbit with re-derived identities: the FLIGHT host a lane needs after Rewind-to-Launch
strips the rover (TimeJump and Real Spawn Control require FLIGHT).

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "miner-rover-landed")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
FIXTURE_META = os.path.join(FIXTURE_DIR, "persistent.loadmeta")
PROBE_DONOR_SFS = os.path.join(_HARNESS, "fixtures", "saves", "eva2-lko-crewed", "persistent.sfs")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_miner_rover_landed.py")
    spec = importlib.util.spec_from_file_location("build_miner_rover_landed", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def _resources(block):
    """[(name, amount, maxAmount)] of every RESOURCE node in a VESSEL block."""
    out = []
    for i, line in enumerate(block):
        if line == "\t\t\t\tRESOURCE":
            values = {}
            j = i + 2
            while block[j] != "\t\t\t\t}":
                key, _, value = block[j].strip().partition(" = ")
                values.setdefault(key, value)
                j += 1
            out.append((values["name"], float(values["amount"]), float(values["maxAmount"])))
    return out


class MinerRoverLandedFixtureTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.text = _read(FIXTURE_SFS)
        cls.lines = cls.text.split("\n")
        cls.blocks = cls.builder.vessel_blocks(cls.lines)
        start, end, _ = cls.blocks[0]
        cls.rover = cls.lines[start:end]
        start, end, _ = cls.blocks[1]
        cls.probe = cls.lines[start:end]

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_miner_rover_landed.py")

    def test_rover_is_index_zero_and_active_probe_is_index_one(self):
        self.assertEqual(["Mining Rover RC", "Observer Probe"], [b[2] for b in self.blocks])
        self.assertEqual(2, sum(1 for l in self.lines if l == "\t\tVESSEL"))
        self.assertIn("\t\tactiveVessel = 0", self.lines)
        meta = _read(FIXTURE_META).split("\n")
        self.assertIn("vesselCount = 2", meta)
        self.assertIn("UT = 30", meta)

    def test_ut_is_thirty(self):
        fs = self.lines.index("\tFLIGHTSTATE")
        self.assertEqual("\t\tUT = 30", self.lines[fs + 3])
        self.assertIn("\t\t\tlct = 30", self.rover)
        self.assertIn("\t\t\tlastUT = 30", self.rover)
        self.assertIn("\t\t\tmet = 0", self.rover)
        self.assertIn("\t\t\t\tEPH = 30", self.rover)

    def test_rover_is_landed_crewless_and_keeps_its_identity(self):
        self.assertIn("\t\t\tsit = LANDED", self.rover)
        self.assertIn("\t\t\tpersistentId = 110406740", self.rover)
        self.assertEqual(110406740, self.builder.rover_persistent_id())
        self.assertIn("\t\t\tpid = e585079827a441ee8026bf1afad27961", self.rover)
        self.assertFalse(any(l.strip().startswith("crew = ") for l in self.rover))

    def test_part_census(self):
        names = [self.rover[i + 2].strip()[len("name = "):]
                 for i, l in enumerate(self.rover) if l == "\t\t\tPART"]
        self.assertEqual(27, len(names))
        self.assertEqual(4, names.count("MiniDrill"))
        self.assertEqual(1, names.count("MiniISRU"))
        self.assertEqual(2, names.count("RadialOreTank"))

    def test_no_restock_text_anywhere(self):
        self.assertNotIn("ModuleRestock", self.text)
        # Case-insensitive on the rover only: the base's LoaderInfo lists the dev
        # install's ReStock assemblies, which a save load does not act on.
        self.assertFalse([l for l in self.rover if "restock" in l.lower()])

    def test_propellant_and_ore_tanks_start_empty_with_room(self):
        res = _resources(self.rover)
        emptied = [r for r in res if r[0] in ("Ore", "LiquidFuel", "Oxidizer", "MonoPropellant")]
        self.assertEqual({"Ore": 2, "LiquidFuel": 1, "Oxidizer": 1, "MonoPropellant": 2},
                         {n: sum(1 for r in emptied if r[0] == n) for n in
                          ("Ore", "LiquidFuel", "Oxidizer", "MonoPropellant")})
        for name, amount, max_amount in emptied:
            self.assertEqual(0.0, amount, name)
            self.assertGreater(max_amount, 0.0, name)
        charge = [r for r in res if r[0] == "ElectricCharge"]
        self.assertTrue(charge)
        self.assertTrue(all(r[1] > 0 for r in charge), "ElectricCharge stays verbatim")

    def test_every_converter_timestamp_is_the_fixture_ut(self):
        stamps = [l.strip() for l in self.rover if l.strip().startswith("lastUpdateTime = ")]
        self.assertEqual(25, len(stamps))
        self.assertEqual({"lastUpdateTime = 30"}, set(stamps))

    def test_drills_retracted_and_converters_off(self):
        deployed = [l.strip() for l in self.rover if l.strip().startswith("isDeployed = ")]
        self.assertEqual(["isDeployed = False"] * 4, deployed)
        activated = {l.strip() for l in self.rover if l.strip().startswith("IsActivated = ")}
        self.assertEqual({"IsActivated = False"}, activated)

    def test_observer_probe_orbits_crewless_at_the_fixture_ut(self):
        self.assertIn("\t\t\ttype = Probe", self.probe)
        self.assertIn("\t\t\tsit = ORBITING", self.probe)
        self.assertIn("\t\t\t\tREF = 1", self.probe)
        self.assertFalse(any(l.strip().startswith("crew = ") for l in self.probe))
        self.assertEqual(13, sum(1 for l in self.probe if l == "\t\t\tPART"))
        for key, value in (("lct", "30"), ("lastUT", "30"), ("met", "0")):
            self.assertIn("\t\t\t%s = %s" % (key, value), self.probe)
        self.assertIn("\t\t\t\tEPH = 30", self.probe)
        self.assertFalse([l for l in self.probe
                          if l.strip().startswith("lastUpdateTime = ") and l.strip() != "lastUpdateTime = 30"])

    def test_observer_probe_identities_are_derived_and_disjoint(self):
        pid = self.builder.probe_persistent_id()
        self.assertIn("\t\t\tpersistentId = %d" % pid, self.probe)
        donor = _read(PROBE_DONOR_SFS).split("\n")
        donor_block = [b for b in self.builder.vessel_blocks(donor) if b[2] == "Kerbal X Probe"]
        self.assertEqual(1, len(donor_block))
        donor_lines = donor[donor_block[0][0]:donor_block[0][1]]

        def ids(block):
            return {l.strip() for l in block if l.strip().startswith(("pid = ", "persistentId = "))}

        self.assertEqual(15, len(ids(self.probe)))  # vessel pid + vessel + 13 part persistentIds
        self.assertFalse(ids(donor_lines) & ids(self.probe))
        self.assertFalse(ids(self.rover) & ids(self.probe))
        uids = lambda block: {l.strip() for l in block if l.strip().startswith("uid = ")}
        self.assertFalse(uids(self.rover) & uids(self.probe))

    def test_carries_the_inert_parsek_scenario_node(self):
        # A focusable save boots through the FLIGHT route, where a node-less save gets
        # no ParsekScenario MODULE and Parsek records nothing (CL-1 flight 1).
        i = self.lines.index("\t\tname = ParsekScenario")
        self.assertEqual("\tSCENARIO", self.lines[i - 2])
        self.assertEqual(["\t\tscene = 7, 5, 8, 6", "\t\tmissionHideArchived = False",
                          "\t\tgameStateEventCount = 1", "\t}"], self.lines[i + 1:i + 5])


if __name__ == "__main__":
    unittest.main()
