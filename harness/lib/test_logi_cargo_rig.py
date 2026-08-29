"""Guards the `Logi Cargo Rig` craft and its forge (spec `FORGE-logi-pad`, craft
builder `harness/tools/build_logi_craft.py`).

The GS-1 precedent, restated because it is the whole reason this file exists: the
craft is authored BY CONSTRUCTION and NOTHING else in the repo can tell whether it
still says what the future H38 isolated-Logistics lane needs. Unwired,
`build_logi_craft.py` would be prose with a shebang - a hand edit to the .craft, or
a change to the derivation, would surface in a live forge flight (or, worse, as a
silent wall of `InGameAssert.Skip` lines in the H38 batch) instead of here.

It lives in `harness/lib/` rather than beside a mission test because this forge adds
NO mission: it drives the existing generic `forge_station`, so there is no
`missions/lib/test_<mission>.py` to hang it off. `discover -s lib` picks it up.

Stdlib only; no KSP, no network. ASCII only.
"""

import importlib.util
import os
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
SCENARIOS = os.path.join(_HARNESS, "scenarios")
FORGE_SPEC = os.path.join(SCENARIOS, "FORGE-logi-pad.toml")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_logi_craft.py")
    spec = importlib.util.spec_from_file_location("build_logi_craft", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read_toml(path):
    with open(path, "rb") as fh:
        return tomllib.load(fh)


class CraftDriftTests(unittest.TestCase):
    """The committed bytes against the derivation, and the derivation against the
    capabilities the Logistics category's skip strings demand."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.lines = cls.builder.read_lines(cls.builder.CRAFT_PATH)
        cls.text = "\n".join(cls.lines)

    def test_the_committed_craft_satisfies_every_post_condition(self):
        self.assertEqual([], self.builder.verify(self.lines))

    def test_verify_rejects_a_craft_that_violates_each_post_condition(self):
        # NEGATIVE CONTROL. The cell above proves the committed craft PASSES verify();
        # it does not prove verify() would notice a craft that should fail - a predicate
        # returning [] unconditionally passes it just as well. Each case here breaks
        # exactly one post-condition and asserts verify() names THAT one, so the cell
        # above is measuring something.
        #
        # 1. Part census: remove one PART block and the census must stop matching.
        cut_at = None
        for i, line in enumerate(self.lines):
            if line.strip() == "PART":
                cut_at = i
                break
        self.assertIsNotNone(cut_at, "expected at least one PART block to cut")
        end = cut_at + 1
        while end < len(self.lines) and self.lines[end].strip() != "PART":
            end += 1
        problems = self.builder.verify(self.lines[:cut_at] + self.lines[end:])
        self.assertTrue(
            any("part census" in p for p in problems),
            "verify() must reject a craft whose part census moved; got %s" % problems)

        # 2. MAX_PARTS: lower the cap under the real count and the committed craft
        #    itself must be rejected, proving the cap is consulted rather than decorative.
        original_cap = self.builder.MAX_PARTS
        try:
            self.builder.MAX_PARTS = 1
            problems = self.builder.verify(self.lines)
            self.assertTrue(any("exceeds the 1-part cap" in p for p in problems),
                            "verify() must enforce MAX_PARTS; got %s" % problems)
        finally:
            self.builder.MAX_PARTS = original_cap

        # 3. UID_CEILING: the UInt32 ceiling that cost the B17 forge an attempt-set.
        original_ceiling = self.builder.UID_CEILING
        try:
            self.builder.UID_CEILING = 1
            problems = self.builder.verify(self.lines)
            self.assertTrue(any("UInt32 ceiling" in p for p in problems),
                            "verify() must enforce UID_CEILING; got %s" % problems)
        finally:
            self.builder.UID_CEILING = original_ceiling

        # The control must not leave the module mutated for the cells that follow.
        self.assertEqual([], self.builder.verify(self.lines))

    def test_the_committed_craft_is_byte_identical_to_a_fresh_rebuild(self):
        self.assertEqual(self.lines, self.builder.build(),
                         "the committed craft has drifted from what "
                         "build_logi_craft.py produces; re-run --write and commit, "
                         "or explain the divergence")

    def test_the_rig_is_a_launchable_pad_rocket(self):
        # L1. `UnloadedFuelVesselFixture` snapshots the ACTIVE PRELAUNCH vessel and
        # re-spawns the copy into a 250 km parking orbit as the unloaded depot; a
        # craft with no engine is not a pad rocket and the forge has nothing to
        # settle PRELAUNCH either.
        self.assertIn("name = ModuleEngines", self.text)

    def test_the_rig_is_controllable_with_nobody_aboard(self):
        # The forge launches crew=[] (see the spec's crewNames comment). Exactly one
        # ModuleCommand, and it must be the probe core: a command POD root would
        # bring its own ModuleInventoryPart and displace the small container from
        # first place in probe order.
        self.assertEqual(1, self.text.count("name = ModuleCommand"))
        records = self.builder.part_records(self.lines)
        self.assertEqual("probeCoreOcto2.v2", records[0][0])

    def test_a_baseconverter_is_aboard_for_the_harvest_window_cells(self):
        # L4. Two HarvestCapture cells skip with "carries no BaseConverter-derived
        # module (harvester / converter / drill); a stock fuel cell suffices - add
        # one to the test craft". A fuel cell is also the only stock converter that
        # can ACTIVATE on the pad - a drill wants ground contact and ore, and says
        # so in its own skip.
        self.assertIn("name = ModuleResourceConverter", self.text)
        self.assertIn("FuelCell_", self.text)

    def test_the_liquid_fuel_tank_is_partly_full_and_flowing(self):
        # L2 + L3. A tank with no headroom fails the delivery top-up; a tank with no
        # stored fuel fails the origin debit; a NO_FLOW tank fails both. The floors
        # are the in-game constants the builder mirrors.
        b = self.builder
        self.assertGreaterEqual(b.TANK_LIQUID_FUEL, b.MIN_DEBITABLE_LIQUID_FUEL)
        self.assertGreaterEqual(b.TANK_MAX_LIQUID_FUEL - b.TANK_LIQUID_FUEL,
                                b.MIN_FREE_LIQUID_FUEL_CAPACITY)
        self.assertLess(b.TANK_LIQUID_FUEL, b.TANK_MAX_LIQUID_FUEL)
        self.assertNotIn("flowState = False", self.text)
        # The Oxidizer is not decoration: the fuel cell consumes LF AND Ox.
        self.assertIn("name = Oxidizer", self.text)

    def test_two_inventory_modules_in_the_order_the_probe_walks(self):
        # L5, THE CELL THE WHOLE H38 INVENTORY SLICE RESTS ON.
        # `Delivery_MultiModule_FirstContainerFullSecondReceives` collects inventory
        # modules in vessel part order, skips below two, fills every empty slot of
        # the FIRST, then needs a LATER one with a free slot. Reorder these two
        # containers and the cell skips while the lane still looks green.
        inventories = self.builder.inventory_part_order(self.lines)
        self.assertEqual(["smallCargoContainer", "cargoContainer"],
                         [owner for owner, _ in inventories])
        self.assertEqual(list(self.builder.STORED_CARGO), inventories[0][1])
        self.assertEqual([], inventories[1][1],
                         "the later container must be EMPTY or the multi-module "
                         "handoff has nowhere to land")
        self.assertGreaterEqual(
            self.builder.FIRST_INVENTORY_SLOTS - len(self.builder.STORED_CARGO), 1,
            "the first inventory module must keep an empty slot")

    def test_the_science_kit_is_spelled_the_way_the_part_cfg_spells_it(self):
        # Squad/Parts/Cargo/ScienceKit/evaScienceKit.cfg declares
        # `name = evaScienceKit`. The in-game multi-module cell probes a candidate
        # list that includes the shorter `evaScience`, for which PartLoader returns
        # null - so a craft stowing "evaScience" would carry a part KSP cannot load.
        self.assertIn("evaScienceKit", self.builder.STORED_CARGO)
        self.assertNotIn("evaScience", [n for n in self.builder.STORED_CARGO
                                        if n != "evaScienceKit"])

    def test_every_part_uid_is_under_the_uint32_ceiling(self):
        # A3. KSP parses the `part = <name>_<uid>` suffix as a UInt32 and kRPC
        # launch_vessel throws server-side above it. This cost FORGE-b17-duna-pad a
        # whole attempt-set; it can only regress here by a hand edit.
        for key, uid in sorted(self.builder._PART_UID.items()):
            self.assertLess(uid, self.builder.UID_CEILING, key)

    def test_the_rig_stays_small_enough_for_the_isolated_lane(self):
        # 38 quickload restores have to fit the isolated lane's step budget and
        # quickload cost scales with part count.
        self.assertLessEqual(len(self.builder.part_records(self.lines)),
                             self.builder.MAX_PARTS)


class ForgeSpecTests(unittest.TestCase):
    """The forge that turns the craft into a pad save."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.forge = _read_toml(FORGE_SPEC)

    def test_the_forge_launches_the_craft_the_builder_writes(self):
        self.assertEqual(self.builder.SHIP_NAME,
                         self.forge["driver"]["missionParams"]["craftName"])

    def test_the_craft_is_resolvable_from_the_staged_save(self):
        # kRPC resolves <save>/Ships/VAB/<craftName>.craft in the STAGED save, and
        # post-promotion that file arrives via the shared-ships overlay rather than
        # physically. The load-bearing clause is the MANIFEST ROW: without one the
        # staged save gets no Ships/VAB at all, `stage_fixture` still reports success
        # (an unlisted save resolves zero entries AND zero errors by design), and the
        # run boots and dies inside the mission as a driver-INVALID against a
        # perfectly good spec.
        expected = os.path.join(_HARNESS, "fixtures", "ships",
                                self.builder.SHIP_NAME + ".craft")
        self.assertEqual(os.path.normcase(os.path.abspath(expected)),
                         os.path.normcase(os.path.abspath(self.builder.CRAFT_PATH)))
        self.assertTrue(os.path.isfile(expected),
                        "the shared ship library must carry the craft the forge "
                        "launches")
        manifest = _read_toml(os.path.join(_HARNESS, "fixtures",
                                           "shared-ships.toml"))["ships"]
        leaf = self.forge["fixture"]["saveTemplate"].rsplit("/", 1)[-1]
        self.assertIn(self.builder.SHIP_NAME, manifest.get(leaf, []),
                      "the forge base %r must declare %r in shared-ships.toml, or "
                      "the staged save carries no craft to launch"
                      % (leaf, self.builder.SHIP_NAME))

    def test_the_promotion_left_exactly_one_copy_of_the_craft(self):
        # THE PROMOTION'S OWN GUARD. A library craft needs >= 2 consumers
        # (hlib.validate_shared_ships_manifest), which is why this one lived
        # physically in the forge base until the harvest produced its second. Both
        # halves are asserted here rather than left to prose: the two consumers are
        # named, and no save-local copy survived the move. The repo-wide
        # content-addressed sweep belongs to SharedShipsManifestTests; this is the
        # craft-specific statement, which names the two saves.
        manifest = _read_toml(os.path.join(_HARNESS, "fixtures",
                                           "shared-ships.toml"))["ships"]
        consumers = sorted(save for save, ships in manifest.items()
                           if self.builder.SHIP_NAME in ships)
        self.assertEqual(["bdock-forge-base", "logi-cargo-pad"], consumers)
        saves_dir = os.path.join(_HARNESS, "fixtures", "saves")
        strays = []
        for dirpath, _dirnames, filenames in os.walk(saves_dir):
            for name in filenames:
                if name == self.builder.SHIP_NAME + ".craft":
                    strays.append(os.path.relpath(os.path.join(dirpath, name),
                                                  saves_dir).replace("\\", "/"))
        self.assertEqual([], sorted(strays),
                         "a save-local copy of the promoted craft survived; the "
                         "library copy is the only one that may exist")

    def test_the_forge_reuses_the_generic_mission_and_stays_operator_tier(self):
        # No new mission and no new harvest tool: the seventh consumer of each.
        self.assertEqual("forge_station", self.forge["driver"]["mission"])
        self.assertEqual("operator", self.forge["tier"])

    def test_the_forge_launches_uncrewed(self):
        # An explicit empty list, not an omitted key: minCrew then defaults to 0 and
        # the pad crew gate is inert by construction rather than by accident.
        self.assertEqual([], self.forge["driver"]["missionParams"]["crewNames"])
        self.assertNotIn("minCrew", self.forge["driver"]["missionParams"])

    def test_the_forge_asserts_nothing_about_parsek(self):
        # A forge failure must classify driver-INVALID, never PARSEK-FAIL.
        contracts = self.forge["expectations"]["logContracts"]
        self.assertEqual([], contracts["required"])
        self.assertIn("\\[Parsek\\]\\[ERROR\\]", contracts["forbidden"])

    def test_the_header_carries_the_harvest_command_verbatim(self):
        # The forge is only half the recipe; the operator needs the exact harvest
        # invocation, with the target name, or the produced save is stamped under
        # the wrong fixture directory.
        with open(FORGE_SPEC, encoding="utf-8") as fh:
            header = fh.read()
        self.assertIn("harvest_bdock_station.py", header)
        self.assertIn("--target-name logi-cargo-pad", header)
        self.assertIn("--expect-situation PRELAUNCH", header)


if __name__ == "__main__":
    unittest.main()
