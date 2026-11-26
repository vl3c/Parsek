"""Drift and shape gates for `CS Flea Twin Probe.craft` (RFN-1 / RFN-2's craft, physically in
`cs-career-mun4/Ships/VAB/`, its one consumer).

The craft is DERIVED by `harness/tools/build_cs_flea_twin_probe_craft.py` from the CS Flea
Probe derivation (`build_cs_flea_probe_craft.layout`) with a second stand. A hand edit, a
change to either derivation, or a pid the lanes name drifting reds here instead of in a
flight whose decouple refuses `partaction-part-not-found`.
"""
import importlib.util
import os
import re
import tomllib
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS = os.path.join(HARNESS_ROOT, "tools")
BUILDER = os.path.join(TOOLS, "build_cs_flea_twin_probe_craft.py")
SCENARIOS = os.path.join(HARNESS_ROOT, "scenarios")
LANES = ("RFN-1-refly-nested-separation-merge-f9.toml",
         "RFN-2-refly-nested-separation-discard.toml")


def _load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _parts(text):
    out = []
    for blk in text.split("\nPART\n{\n")[1:]:
        token = re.match(r"\tpart = (\S+)\n", blk).group(1)
        pid = int(re.search(r"(?m)^\tpersistentId = (\d+)$", blk).group(1))
        out.append((token, pid, blk.split("\n}", 1)[0]))
    return out


class CsFleaTwinProbeCraftTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.b = _load(BUILDER, "build_cs_flea_twin_probe_craft")
        with open(cls.b.OUT, encoding="utf-8", newline="") as fh:
            cls.text = fh.read()
        cls.parts = _parts(cls.text)

    def test_the_committed_craft_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], self.b.check())

    def test_it_carries_two_stands_and_three_command_parts(self):
        names = [t.rsplit("_", 1)[0] for t, _, _ in self.parts]
        self.assertEqual(14, len(names))
        self.assertEqual(2, names.count("radialDecoupler"))
        self.assertEqual(2, names.count("probeCoreSphere.v2"))
        self.assertEqual(1, names.count("mk1pod.v2"))

    def test_the_named_decoupler_pids_are_the_two_decouplers(self):
        dec = [pid for t, pid, _ in self.parts if t.startswith("radialDecoupler_")]
        self.assertEqual([self.b.STAND_A_DECOUPLER_PID, self.b.STAND_B_DECOUPLER_PID], dec)

    def test_every_persistent_id_is_fresh_and_distinct_from_the_cs_flea_probe(self):
        pids = [pid for _, pid, _ in self.parts]
        self.assertEqual(len(pids), len(set(pids)))
        probe = _load(os.path.join(TOOLS, "build_cs_flea_probe_craft.py"), "probe_b")
        probe_pids = {int(m) for m in re.findall(r"(?m)^\tpersistentId = (\d+)$", probe.build())}
        self.assertFalse(set(pids) & probe_pids)

    def test_the_booster_links_both_decouplers(self):
        host = [blk for t, _, blk in self.parts if t == "solidBooster.sm.v2_4294392496"][0]
        for uid in (self.b.STANDS[0][1][0], self.b.STANDS[1][1][0]):
            self.assertIn("\tlink = radialDecoupler_%d\n" % uid, host + "\n")

    def test_the_lanes_name_the_craft_and_its_decoupler_pids(self):
        for fname in LANES:
            with self.subTest(lane=fname):
                with open(os.path.join(SCENARIOS, fname), "rb") as fh:
                    spec = tomllib.load(fh)
                crafts = [s["args"].get("craft") for s in spec["driver"]["steps"]
                          if s["cmd"] == "GoToEditor"]
                self.assertIn(self.b.SHIP_NAME, crafts)
                pids = {s["args"].get("partPid") for s in spec["driver"]["steps"]
                        if s["cmd"] == "PartAction" and s["args"].get("button") == "decouple"}
                self.assertEqual({str(self.b.STAND_A_DECOUPLER_PID),
                                  str(self.b.STAND_B_DECOUPLER_PID)}, pids)


if __name__ == "__main__":
    unittest.main()
