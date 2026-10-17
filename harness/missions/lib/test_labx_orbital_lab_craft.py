"""Unit tests for the LABX Orbital Lab craft (harness/tools/build_lab_orbit_craft.py),
the craft LABX-1-lab-orbit-ascent flies to low Kerbin orbit for the science-lab lanes.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q

Mirrors the GS-1 and DD1 craft drift cells: the builder's `--check` runs in-process,
so a hand edit to the committed .craft, or a drifted derivation, reds here instead
of in a live flight. The extra cells pin the properties the lab lanes lean on
beyond "it flies": the scientist's seat (A2), the staging that can never drop the
lab (A3), the steady power the lab's research draw needs (A8), the science link
(A9: a DIRECT antenna, since the pod's INTERNAL one cannot transmit) and the
never-staged probe tender on its docking-port pair (A12).
"""

import importlib.util
import math
import os
import tomllib
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.abspath(__file__))))


def _load_builder():
    path = os.path.join(HARNESS_ROOT, "tools", "build_lab_orbit_craft.py")
    spec = importlib.util.spec_from_file_location("build_lab_orbit_craft", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class CraftDriftTests(unittest.TestCase):
    """WIRES THE BUILDER'S `--check` INTO THE SUITE."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_the_committed_craft_satisfies_every_post_condition(self):
        committed = self.builder.read_lines(self.builder.CRAFT_PATH)
        self.assertEqual(self.builder.verify(committed), [])

    def test_the_committed_craft_is_byte_identical_to_a_fresh_rebuild(self):
        committed = self.builder.read_lines(self.builder.CRAFT_PATH)
        self.assertEqual(
            committed, self.builder.build(),
            "the committed LABX craft has DRIFTED from the derivation; re-run "
            "build_lab_orbit_craft.py --write and commit, or explain")

    def test_check_mode_exits_clean(self):
        self.assertEqual(self.builder.main(["--check"]), 0)


class SeatingContractTests(unittest.TestCase):
    """Assumption A2: kRPC fills the one control-source seat with the pilot, then
    seats the leftover named kerbal in the first empty seat in PART order, so the
    pod must be ONE seat, first, and the lab the only other crewable part."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.records = cls.builder.part_records(cls.builder.build())

    def test_the_root_is_the_one_seat_pod_and_the_lab_is_the_next_crewable_part(self):
        crewable = [n for n, _r in self.records if n in self.builder.CREW_CAPACITY]
        self.assertEqual(self.records[0][0], "mk1pod.v2")
        self.assertEqual(crewable, ["mk1pod.v2", "Large.Crewed.Lab"])
        self.assertEqual(self.builder.CREW_CAPACITY["mk1pod.v2"], 1)

    def test_a_three_seat_pod_would_be_caught(self):
        # The mutation the seating contract exists for: a Mk1-3 pod absorbs both
        # names and leaves the lab empty. Re-labelling the root reds the census and
        # the root check, so the swap cannot slip through as a drift alone.
        lines = self.builder.build()
        mutated = [l.replace("mk1pod.v2_", "mk1-3pod_") for l in lines]
        problems = self.builder.verify(mutated)
        self.assertTrue(any("ROOT" in p for p in problems), problems)

    def test_the_pod_is_the_only_seated_command_part(self):
        # Two control sources since the tender (A12): the pod and the seatless OKTO.
        commands = [n for n, r in self.records
                    if "name = ModuleCommand" in r["__body__"]]
        self.assertEqual(sorted(commands), ["mk1pod.v2", "probeCoreOcto.v2"])
        self.assertEqual(self.builder.COMMAND_SEATS["probeCoreOcto.v2"], 0)
        self.assertNotIn("probeCoreOcto.v2", self.builder.CREW_CAPACITY)

    def test_a_seated_tender_core_would_be_caught(self):
        # A HECS-with-seats / lander-can swap in the tender would join the A2 fill.
        original = dict(self.builder.COMMAND_SEATS)
        try:
            self.builder.COMMAND_SEATS["probeCoreOcto.v2"] = 1
            problems = self.builder.verify(self.builder.build())
        finally:
            self.builder.COMMAND_SEATS.clear()
            self.builder.COMMAND_SEATS.update(original)
        self.assertTrue(any("ONLY command part with seats" in p for p in problems),
                        problems)

    def test_the_lab_tail_invents_no_module(self):
        lab = [r for n, r in self.records if n == "Large.Crewed.Lab"]
        self.assertEqual(len(lab), 1)
        self.assertNotIn("\tMODULE", lab[0]["__body__"])


class StagingContractTests(unittest.TestCase):
    """Assumption A3: Mainsail alone in istg 1, TD-25 + Poodle hot-stage in istg 0,
    nothing above the decoupler ever stages."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.records = cls.builder.part_records(cls.builder.build())

    def _istg(self, name):
        return sorted({int(r["istg"]) for n, r in self.records if n == name})

    def test_the_two_stage_hot_stage_contract(self):
        self.assertEqual(self._istg("liquidEngineMainsail.v2"),
                         [self.builder.STAGE_IGNITE])
        self.assertEqual(self._istg("Decoupler.2"), [self.builder.STAGE_SEPARATE])
        self.assertEqual(self._istg("liquidEngine2-2.v2"),
                         [self.builder.STAGE_SEPARATE])

    def test_the_lab_and_everything_above_the_decoupler_never_stage(self):
        for name in ("mk1pod.v2", "Large.Crewed.Lab", "science.module",
                     "GooExperiment", "rtg", "Rockomax16.BW", "SurfAntenna",
                     "dockingPort3", "probeCoreOcto.v2", "batteryBankMini"):
            self.assertEqual(self._istg(name), [-1], name)

    def test_a_staged_lab_would_be_caught(self):
        lines = self.builder.build()
        out, in_lab = [], False
        for line in lines:
            if line.startswith("\tpart = Large.Crewed.Lab_"):
                in_lab = True
            if in_lab and line == "\tistg = -1":
                line, in_lab = "\tistg = 0", False
            out.append(line)
        self.assertTrue(any("Large.Crewed.Lab sits above the decoupler" in p
                            for p in self.builder.verify(out)))


class ScienceLinkTests(unittest.TestCase):
    """Assumption A9: the pod's INTERNAL antenna cannot transmit science, so a DIRECT
    or RELAY ModuleDataTransmitter must be aboard, on a part that never stages."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.records = cls.builder.part_records(cls.builder.build())

    def test_a_direct_or_relay_transmitter_is_aboard_and_never_staged(self):
        links = [(n, r) for n, r in self.records
                 if "name = ModuleDataTransmitter" in r["__body__"]
                 and self.builder.TRANSMITTER_TYPE.get(n) in ("DIRECT", "RELAY")]
        self.assertEqual([n for n, _r in links], ["SurfAntenna"])
        for _n, rec in links:
            self.assertEqual(rec["istg"], "-1")
            self.assertTrue(rec["srfN"].startswith("srfAttach,mk1pod.v2_"))

    def test_every_transmitter_aboard_has_a_pinned_antenna_type(self):
        carriers = sorted({n for n, r in self.records
                           if "name = ModuleDataTransmitter" in r["__body__"]})
        self.assertEqual(carriers, ["SurfAntenna", "mk1pod.v2", "probeCoreOcto.v2"])
        self.assertEqual(self.builder.TRANSMITTER_TYPE["mk1pod.v2"], "INTERNAL")
        self.assertEqual(self.builder.TRANSMITTER_TYPE["probeCoreOcto.v2"], "INTERNAL")
        self.assertEqual(self.builder.TRANSMITTER_TYPE["SurfAntenna"], "DIRECT")

    def test_the_antenna_is_not_deployable(self):
        ant = [r for n, r in self.records if n == "SurfAntenna"]
        self.assertEqual(len(ant), 1)
        self.assertNotIn("ModuleDeployableAntenna", ant[0]["__body__"])

    def test_a_craft_without_the_antenna_would_be_caught(self):
        # The first flown shape: only INTERNAL transmitters aboard.
        # Drop the antenna's whole top-level PART node (an unindented "PART" / "{" /
        # ... / "}" run) and every reference to it.
        lines = self.builder.build()
        out, i = [], 0
        while i < len(lines):
            if (lines[i] == "PART" and i + 2 < len(lines)
                    and lines[i + 2].startswith("\tpart = SurfAntenna_")):
                j = lines.index("}", i + 2)
                i = j + 1
                continue
            if "SurfAntenna_" not in lines[i]:
                out.append(lines[i])
            i += 1
        self.assertFalse(any("SurfAntenna" in l for l in out))
        problems = self.builder.verify(out)
        self.assertTrue(any("no DIRECT or RELAY ModuleDataTransmitter" in p
                            for p in problems), problems)

    def test_a_staged_antenna_would_be_caught(self):
        lines = self.builder.build()
        out, in_ant = [], False
        for line in lines:
            if line.startswith("\tpart = SurfAntenna_"):
                in_ant = True
            if in_ant and line == "\tistg = -1":
                line, in_ant = "\tistg = 0", False
            out.append(line)
        self.assertTrue(any("science-link antenna SurfAntenna must never stage" in p
                            for p in self.builder.verify(out)))


class TenderContractTests(unittest.TestCase):
    """Assumption A12: a seatless probe tender on a face-to-face Jr port pair above
    the pod, never staged, separable only by an explicit undock."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.records = cls.builder.part_records(cls.builder.build())

    def _named(self, name):
        return [r for n, r in self.records if n == name]

    def test_the_tender_stack_order_above_the_pod(self):
        pod = self._named("mk1pod.v2")[0]
        lower, upper = self._named("dockingPort3")
        okto = self._named("probeCoreOcto.v2")[0]
        z200 = self._named("batteryBankMini")[0]
        self.assertIn(lower["part"], pod["link"])
        self.assertEqual(lower["link"], [upper["part"]])
        self.assertEqual(upper["link"], [okto["part"]])
        self.assertEqual(okto["link"], [z200["part"]])
        ys = [float(r["pos"].split(",")[1]) for r in (pod, lower, upper, okto, z200)]
        self.assertEqual(ys, sorted(ys))

    def test_the_ports_meet_face_to_face_like_crater_crawlers_pair(self):
        lower, upper = self._named("dockingPort3")
        self.assertEqual(lower["rot"], "0,0,0,1")
        self.assertEqual(upper["rot"], "1,0,0,0")
        self.assertIn("top,%s_0|0.1474114|0" % upper["part"], lower["attN"])
        self.assertIn("top,%s_0|0.1474114|0" % lower["part"], upper["attN"])
        gap = float(upper["pos"].split(",")[1]) - float(lower["pos"].split(",")[1])
        self.assertAlmostEqual(gap, 2 * 0.1474114, places=6)

    def test_no_port_is_stageable_and_no_decoupler_is_in_the_tender(self):
        for rec in self._named("dockingPort3"):
            self.assertIn("stagingEnabled = False", rec["__body__"])
            self.assertEqual(rec["istg"], "-1")
        decouplers = [n for n, r in self.records
                      if "ModuleDecouple" in r["__body__"]]
        self.assertEqual(decouplers, ["Decoupler.2"])

    def test_a_stageable_port_would_be_caught(self):
        text = "\n".join(self.builder.build())
        i = text.index("name = ModuleDockingNode")
        j = text.index("stagingEnabled = False", i)
        text = text[:j] + "stagingEnabled = True" + text[j + len("stagingEnabled = False"):]
        self.assertTrue(any("must carry stagingEnabled = False" in p
                            for p in self.builder.verify(text.split("\n"))))


class PerformanceTests(unittest.TestCase):
    """Assumptions A7 / A8: the arithmetic the report quotes, held to its floors."""

    @classmethod
    def setUpClass(cls):
        cls.perf = _load_builder().performance()

    def test_pad_twr_and_total_vacuum_dv_clear_the_floors(self):
        self.assertGreater(self.perf["padTwr"], 1.3)
        self.assertGreater(self.perf["totalVacDv"], 3400.0)

    def test_the_quoted_numbers(self):
        self.assertAlmostEqual(self.perf["padMass"], 58.595, places=3)
        self.assertAlmostEqual(self.perf["upperWet"], 16.435, places=3)
        self.assertAlmostEqual(self.perf["padTwr"], 2.40, places=2)
        self.assertAlmostEqual(self.perf["upperVacDv"], 2289.4, delta=1.0)
        self.assertAlmostEqual(self.perf["boosterVacDv"], 2401.4, delta=1.0)

    def test_the_booster_alone_cannot_reach_orbit(self):
        # The stackAttached row needs the decoupler to fire DURING the ascent, so
        # the booster must carry well under an LKO ascent's spend.
        self.assertLess(self.perf["boosterVacDv"], 3000.0)

    def test_rtg_power_covers_the_lab_research_draw(self):
        self.assertGreaterEqual(self.perf["ecPerSecond"], 5.0)


class LaneWiringTests(unittest.TestCase):
    """The LABX-1 spec launches THIS craft with THIS crew order, from the fixture
    that overlays it."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        with open(os.path.join(HARNESS_ROOT, "scenarios",
                               "LABX-1-lab-orbit-ascent.toml"), "rb") as fh:
            cls.spec = tomllib.load(fh)

    def test_the_spec_names_the_craft_and_the_crew_in_seat_order(self):
        params = self.spec["driver"]["missionParams"]
        self.assertEqual(params["craftName"], self.builder.SHIP_NAME)
        self.assertEqual(tuple(params["crewNames"]), self.builder.CREW_NAMES)
        self.assertEqual(params["minCrew"], len(self.builder.CREW_NAMES))
        self.assertTrue(params["parkAttached"])

    def test_the_craft_is_the_library_copy_and_every_consumer_declares_it(self):
        # Promoted to the shared library when the LABX-1 harvest became its second
        # consumer; the builder must write THAT copy, and the spec's host plus both
        # lab fixtures must overlay it.
        library = os.path.join(HARNESS_ROOT, "fixtures", "ships",
                               self.builder.SHIP_NAME + ".craft")
        self.assertEqual(os.path.normcase(os.path.abspath(library)),
                         os.path.normcase(os.path.abspath(self.builder.CRAFT_PATH)))
        self.assertTrue(os.path.isfile(library))
        with open(os.path.join(HARNESS_ROOT, "fixtures", "shared-ships.toml"),
                  "rb") as fh:
            ships = tomllib.load(fh)["ships"]
        leaf = self.spec["fixture"]["saveTemplate"].rstrip("/").rsplit("/", 1)[-1]
        for save in (leaf, "lab-lko-sandbox", "lab-lko-science"):
            self.assertIn(self.builder.SHIP_NAME, ships.get(save, ()), save)


if __name__ == "__main__":
    unittest.main()
