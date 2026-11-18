"""Drift and shape gates for `PSUB-1.craft` (the stock submarine test kit's craft, in
`fixtures/saves/bdock-forge-base/Ships/VAB/`, launched by FORGE-psub-pad).

The craft is BUILT by `harness/tools/build_psub_craft.py` from committed donor tails. A
hand edit to the craft, a drifted donor, a stage table that no longer drops the floats
first, an unlinked part (KSP drops it at load, silently) or a tail that moved its
resources reds here instead of in a forge flight.

Stdlib only; no KSP. ASCII only.
"""
import importlib.util
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_psub_craft.py")
CRAFT = os.path.join(HARNESS_ROOT, "fixtures", "saves", "bdock-forge-base", "Ships", "VAB",
                     "PSUB-1.craft")
FORGE_SPEC = os.path.join(HARNESS_ROOT, "scenarios", "FORGE-psub-pad.toml")


def _load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


builder = _load(BUILDER, "build_psub_craft")


def _committed():
    with open(CRAFT, encoding="utf-8", newline="") as fh:
        return fh.read()


class PsubCraftDriftTests(unittest.TestCase):
    def test_the_committed_craft_is_the_derivation(self):
        self.assertEqual(builder.build(), _committed(),
                         "PSUB-1.craft drifted: run python harness/tools/build_psub_craft.py "
                         "--write and re-read the diff")

    def test_the_check_mode_passes(self):
        self.assertEqual(0, builder.main(["--check"]))

    def test_every_post_condition_holds(self):
        self.assertEqual([], builder.verify(_committed()))

    def test_the_craft_is_lf_and_ascii(self):
        text = _committed()
        self.assertNotIn("\r", text)
        self.assertTrue(all(ord(c) < 128 for c in text))


class PsubCraftShapeTests(unittest.TestCase):
    def test_the_stage_table_drops_floats_then_ballast(self):
        text = _committed()
        rows = {t: (d, tail) for t, d, tail in builder.part_headers(text)}
        parent = {}
        for t, (d, _tail) in rows.items():
            for link in d.get("link", []):
                parent[link] = t
        order = []
        for istg in sorted(builder.stage_table(text), reverse=True):
            decs = builder.stage_table(text)[istg]
            kinds = sorted({c.rsplit("_", 1)[0] for c, p in parent.items() if p in decs})
            order.append(kinds)
        self.assertEqual([["fuelTankSmallFlat"]] * 3 + [["RadialOreTank"]] * 3, order)

    def test_the_ballast_fills_are_heaviest_first(self):
        text = _committed()
        fills = []
        for istg in sorted(builder.stage_table(text), reverse=True):
            for t, d, tail in builder.part_headers(text):
                if t.startswith("RadialOreTank_") and int(d["istg"][0]) == istg:
                    fills.append(int(re.search(r"name = Ore\n\t\tamount = (\d+)", tail).group(1)))
        self.assertEqual([75, 75, 75, 75, 40, 40], fills)

    def test_the_mass_matches_the_docstring_budget(self):
        self.assertAlmostEqual(5.75, builder.mass_tonnes(_committed()), places=3)
        self.assertIn("Total 5.75 t", builder.__doc__)

    def test_each_tail_is_its_donor_tail_up_to_the_resource_amounts(self):
        # Strip the amount lines and every PART tail equals the donor tail it was lifted
        # from: the only edits are the documented resource amounts.
        donors = builder.tails()
        strip = lambda s: re.sub(r"^\t\tamount = \S+\n", "", s, flags=re.M)  # noqa: E731
        for token, _d, tail in builder.part_headers(_committed()):
            name = token.rsplit("_", 1)[0]
            self.assertEqual(strip(donors[name]), strip(tail), token)

    def test_every_radial_pair_sits_outside_the_hull_on_its_azimuth(self):
        import math
        parts = {p.key: p for p in builder.layout()}
        hull = parts["hull"]
        for pair, ring, az0, _istg, _ore in builder.PAIRS:
            for member in (0, 1):
                az = math.radians(az0 + 180.0 * member)
                tank = parts["%s%d_tank" % (pair, member)]
                dx, dz = tank.pos[0] - hull.pos[0], tank.pos[2] - hull.pos[2]
                self.assertGreater(math.hypot(dx, dz), builder.HULL_RADIUS - 0.01, tank.key)
                self.assertAlmostEqual(0.0, math.cos(az) * dz - math.sin(az) * dx, places=5)
                self.assertGreater(math.cos(az) * dx + math.sin(az) * dz, 0.0, tank.key)

    def test_the_forge_launches_this_craft_by_name(self):
        import tomllib
        with open(FORGE_SPEC, "rb") as fh:
            spec = tomllib.load(fh)
        self.assertEqual(builder.SHIP_NAME, spec["driver"]["missionParams"]["craftName"])
        self.assertEqual("fixtures/saves/bdock-forge-base", spec["fixture"]["saveTemplate"])


if __name__ == "__main__":
    unittest.main()
