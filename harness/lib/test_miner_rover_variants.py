"""Fixture gates for `miner-rover-full` and `miner-rover-starved` (lane MINE-11).

Both are DERIVED by `harness/tools/build_miner_rover_variants.py` from the committed
`miner-rover-landed`, so a change to it must re-run the builder. These cells pin what the
MINE-11a / MINE-11b lanes depend on. Full: the ore tanks hold 149.8 of 150 and the
LiquidFuel / Oxidizer / MonoPropellant tanks are full, everything else is the base's.
Starved: no RTG and no ModuleGenerator aboard, every ElectricCharge amount 0, 23 parts whose
index references are consistent, and every surviving part byte-identical to the base's apart
from its renumbered references and its EC amount. In both, the miner is index 0 and active,
the Observer Probe is the base's, and the identities are verbatim.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import re
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(_HARNESS, "fixtures", "saves")
BASE_SFS = os.path.join(SAVES, "miner-rover-landed", "persistent.sfs")
SCENARIOS = os.path.join(_HARNESS, "scenarios")


def _load(name):
    path = os.path.join(_HARNESS, "tools", name + ".py")
    sys.path.insert(0, os.path.join(_HARNESS, "tools"))
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def _resource_totals(block):
    """{name: (amount, maxAmount)} summed over every RESOURCE node of a VESSEL block."""
    out = {}
    for i, line in enumerate(block):
        if line != "\t\t\t\tRESOURCE":
            continue
        values = {}
        j = i + 2
        while block[j] != "\t\t\t\t}":
            key, _, value = block[j].strip().partition(" = ")
            values.setdefault(key, value)
            j += 1
        amount, cap = out.get(values["name"], (0.0, 0.0))
        out[values["name"]] = (amount + float(values["amount"]), cap + float(values["maxAmount"]))
    return out


class _Base(unittest.TestCase):
    VARIANT = None

    @classmethod
    def setUpClass(cls):
        cls.builder = _load("build_miner_rover_variants")
        cls.lines = _read(os.path.join(SAVES, cls.VARIANT, "persistent.sfs")).split("\n")
        blocks = cls.builder.landed.vessel_blocks(cls.lines)
        cls.names = [b[2] for b in blocks]
        cls.miner = cls.lines[blocks[0][0]:blocks[0][1]]
        cls.probe = cls.lines[blocks[1][0]:blocks[1][1]]
        base = _read(BASE_SFS).split("\n")
        b = cls.builder.landed.vessel_blocks(base)
        cls.base_miner = base[b[0][0]:b[0][1]]
        cls.base_probe = base[b[1][0]:b[1][1]]

    def _shared(self):
        self.assertEqual([], self.builder.committed_problems(self.VARIANT),
                         "re-run python harness/tools/build_miner_rover_variants.py")
        self.assertEqual(["Mining Rover RC", "Observer Probe"], self.names)
        self.assertIn("\t\tactiveVessel = 0", self.lines)
        self.assertEqual(self.base_probe, self.probe)
        for key in ("pid", "persistentId", "sit", "lct", "lastUT"):
            prefix = "\t\t\t" + key + " = "
            self.assertEqual([l for l in self.base_miner if l.startswith(prefix)],
                             [l for l in self.miner if l.startswith(prefix)], key)


class MinerRoverFullFixtureTests(_Base):
    VARIANT = "miner-rover-full"

    def test_shared_shape(self):
        self._shared()

    def test_ore_nearly_full_and_fuel_tanks_full(self):
        totals = _resource_totals(self.miner)
        self.assertAlmostEqual(149.8, totals["Ore"][0], places=6)
        self.assertEqual(150.0, totals["Ore"][1])
        for name in ("LiquidFuel", "Oxidizer", "MonoPropellant"):
            self.assertEqual(totals[name][1], totals[name][0], name)
            self.assertGreater(totals[name][0], 0.0, name)
        self.assertEqual(_resource_totals(self.base_miner)["ElectricCharge"],
                         totals["ElectricCharge"])

    def test_only_amount_lines_differ_from_the_base(self):
        self.assertEqual(len(self.base_miner), len(self.miner))
        changed = [(a, b) for a, b in zip(self.base_miner, self.miner) if a != b]
        self.assertEqual(6, len(changed))
        for a, b in changed:
            self.assertEqual("\t\t\t\t\tamount = 0", a)
            self.assertTrue(b.startswith("\t\t\t\t\tamount = "), b)


class MinerRoverStarvedFixtureTests(_Base):
    VARIANT = "miner-rover-starved"

    def test_shared_shape(self):
        self._shared()

    def test_no_rtg_no_generator_no_charge(self):
        self.assertFalse([l for l in self.miner if l.strip() == "name = rtg"])
        self.assertFalse([l for l in self.miner if l.strip() == "name = ModuleGenerator"])
        totals = _resource_totals(self.miner)
        self.assertEqual(0.0, totals["ElectricCharge"][0])
        self.assertEqual(_resource_totals(self.base_miner)["ElectricCharge"][1],
                         totals["ElectricCharge"][1])
        for name in ("Ore", "LiquidFuel", "Oxidizer", "MonoPropellant"):
            self.assertEqual(0.0, totals[name][0], name)

    def test_part_index_references_are_consistent(self):
        blocks = self.builder.part_blocks(self.miner)
        self.assertEqual(23, len(blocks))
        names = [self.miner[s + 2].strip()[len("name = "):] for s, _e in blocks]
        self.assertEqual(4, names.count("MiniDrill"))
        self.assertEqual(1, names.count("MiniISRU"))
        self.assertEqual(2, names.count("RadialOreTank"))
        ref = re.compile(r"^\t\t\t\t(parent|sym|srfN|attN) = (?:[^,]*, )?(-?\d+)")
        for idx, (s, e) in enumerate(blocks):
            syms = []
            for line in self.miner[s:e]:
                m = ref.match(line)
                if not m:
                    continue
                target = int(m.group(2))
                self.assertLess(target, len(blocks), line)
                if m.group(1) == "sym":
                    syms.append(target)
            for target in syms:
                # Pairwise symmetry: the partner names this part back.
                ps, pe = blocks[target]
                self.assertIn("\t\t\t\tsym = %d" % idx, self.miner[ps:pe])
                self.assertEqual(names[idx], names[target])

    def test_surviving_parts_are_the_bases_but_for_references_and_charge(self):
        base_blocks = self.builder.part_blocks(self.base_miner)
        kept = [b for i, b in enumerate(base_blocks) if i not in self.builder.RTG_INDICES]
        ref = re.compile(r"^\t\t\t\t(parent|sym|srfN|attN) = ")
        for (bs, be), (s, e) in zip(kept, self.builder.part_blocks(self.miner)):
            old = self.base_miner[bs:be]
            new = self.miner[s:e]
            self.assertEqual(len(old), len(new))
            for a, b in zip(old, new):
                if a != b:
                    self.assertTrue(ref.match(a) or a.startswith("\t\t\t\t\tamount = "), (a, b))


class MineElevenSpecsNameTheirFixtureTests(unittest.TestCase):
    """The two lanes fly the fixture whose state their gates assume."""

    def test_each_lane_names_its_fixture(self):
        for spec, fixture in (("MINE-11a-rover-full-tanks.toml", "miner-rover-full"),
                              ("MINE-11b-rover-ec-starved.toml", "miner-rover-starved")):
            text = _read(os.path.join(SCENARIOS, spec))
            self.assertIn('saveTemplate = "fixtures/saves/%s"' % fixture, text, spec)


if __name__ == "__main__":
    unittest.main()
