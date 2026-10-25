"""`CS Flea.craft` (the scripted campaign's three-Goo Jumping Flea) is DERIVED by
`harness/tools/build_cs_flea_craft.py`; these cells keep the committed bytes equal
to the derivation and pin the rotation the derivation rests on. ASCII only."""

from __future__ import annotations

import math
import os
import re
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
_TOOLS = os.path.join(_HARNESS, "tools")
if _TOOLS not in sys.path:
    sys.path.insert(0, _TOOLS)

import build_cs_flea_craft as builder  # noqa: E402


def _read(path):
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def _parts(text):
    _header, blocks = builder._split_parts(text.replace("\r\n", "\n"))
    return {builder._scalar(b, "part"): b for b in blocks}


class CsFleaCraftTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.base = _read(builder.BASE_CRAFT)
        cls.committed = _read(builder.OUT_CRAFT)

    def test_the_committed_craft_is_the_derivation(self):
        self.assertEqual(builder.build(self.base), self.committed,
                         "re-run harness/tools/build_cs_flea_craft.py")

    def test_the_ship_is_renamed_and_keeps_crlf(self):
        self.assertTrue(self.committed.startswith("ship = CS Flea\r\n"))

    def test_three_goo_units_in_mutual_symmetry_on_the_pod(self):
        parts = _parts(self.committed)
        goos = [pid for pid in parts if pid.startswith("GooExperiment_")]
        self.assertEqual(sorted([builder.GOO_A, builder.GOO_B, builder.GOO_C]), sorted(goos))
        for pid in goos:
            syms = re.findall(r"^\tsym = (.*)$", parts[pid], flags=re.M)
            self.assertEqual(sorted(set(goos) - {pid}), sorted(syms), pid)
            self.assertIn("\tsrfN = srfAttach,mk1pod.v2_4294466236", parts[pid])
        pids = [builder._scalar(parts[p], "persistentId") for p in parts]
        self.assertEqual(len(pids), len(set(pids)), "persistentIds must be unique")

    def test_every_other_part_is_verbatim(self):
        base, built = _parts(self.base), _parts(self.committed)
        for pid, block in base.items():
            if pid.startswith("GooExperiment_"):
                continue
            self.assertEqual(block, built[pid], pid)

    def test_the_rotation_reproduces_the_stock_second_goo(self):
        # VALIDATION of the derivation: the stock Flea's two Goo units are a 2x
        # radial pair, so the first rotated 180 deg about Y must be the second.
        parts = _parts(self.base)
        a, b = parts[builder.GOO_A], parts[builder.GOO_B]
        pos, rot = builder.rotate_about_y(builder._vec(builder._scalar(a, "pos")),
                                          builder._vec(builder._scalar(a, "rot")), 180.0)
        want_pos = builder._vec(builder._scalar(b, "pos"))
        want_rot = builder._vec(builder._scalar(b, "rot"))
        for got, want in zip(pos, want_pos):
            self.assertAlmostEqual(want, got, places=6)
        sign = 1.0 if sum(g * w for g, w in zip(rot, want_rot)) >= 0 else -1.0
        for got, want in zip(rot, want_rot):
            self.assertAlmostEqual(want, sign * got, places=6)

    def test_the_three_sit_120_degrees_apart_at_one_radius(self):
        parts = _parts(self.committed)
        azimuths, radii = [], []
        for pid in (builder.GOO_A, builder.GOO_B, builder.GOO_C):
            x, _y, z = builder._vec(builder._scalar(parts[pid], "pos"))
            radii.append(math.hypot(x, z))
            azimuths.append(math.degrees(math.atan2(z, x)) % 360.0)
        for r in radii:
            self.assertAlmostEqual(radii[0], r, places=6)
        gaps = sorted((azimuths[i] - azimuths[0]) % 360.0 for i in (1, 2))
        self.assertAlmostEqual(120.0, gaps[0], places=4)
        self.assertAlmostEqual(240.0, gaps[1], places=4)


if __name__ == "__main__":
    unittest.main()
