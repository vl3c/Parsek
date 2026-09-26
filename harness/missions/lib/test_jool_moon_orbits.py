"""Unit tests for coverage wave 11 (roadmap G9): the Tylo / Bop / Pol harvest lanes
B33 / B34 / B35 and their replay lanes V28M / V29M / V30M.

The harvest lanes add NO machine code: each reuses the `b25_laythe_orbit` mission
module (a thin alias over `mlib.b5_decide`) with its target in `missionParams`. So
the cells here re-run each spec's sizing arithmetic against the stock moon elements
rather than exercising mlib again: the burn-done floor sits between the park and the
transfer, the floors clear the terrain, the requested arrival survives a 3x miss
inside the SOI, the dv worst hop fits the craft, and every other moon is a poison.

ASCII only. Runs on the base interpreter (no venv, no krpc).
"""

from __future__ import annotations

import math
import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
HARNESS_ROOT = os.path.dirname(_MISSIONS)
for p in (_HERE, _MISSIONS):
    if p not in sys.path:
        sys.path.insert(0, p)

import mlib  # noqa: E402

MU_JOOL = 2.82528e14
PARK_R = 590_325_784.59          # the jool-park-nerv park radius (B25 header)
DV_AVAILABLE_MPS = 3967.3        # B25's derived capacity at that park
JOOL_MOONS = ("Laythe", "Vall", "Tylo", "Bop", "Pol")

# body -> (sma m, ecc, radius m, mu, soi m, highest terrain m); stock KSP 1.12.5.
MOONS = {
    "Tylo": (68_500_000.0, 0.0, 600_000.0, 2.82489e12, 10_856_518.4, 11_290.0),
    "Bop": (128_500_000.0, 0.235, 65_000.0, 2.4868349e9, 1_221_060.9, 21_757.0),
    "Pol": (179_890_000.0, 0.171, 44_000.0, 7.2170208e8, 1_042_138.9, 5_591.0),
}
LANES = {"Tylo": "B33-tylo-orbit.toml", "Bop": "B34-bop-orbit.toml",
         "Pol": "B35-pol-orbit.toml"}
# Pessimistic v_inf per target (m/s): the Hohmann excess, plus the plane mismatch for Bop.
V_INF_WORST = {"Tylo": 750.0, "Bop": 900.0, "Pol": 490.0}


def _spec(name):
    with open(os.path.join(HARNESS_ROOT, "scenarios", name), "rb") as fh:
        return tomllib.load(fh)


def _params(moon):
    return _spec(LANES[moon])["driver"]["missionParams"]


def _transfer_ecc(r_target):
    return (PARK_R - r_target) / (PARK_R + r_target)


def _capture_dv(moon, pe_alt, v_inf):
    _a, _e, radius, mu, _soi, _h = MOONS[moon]
    r = radius + pe_alt
    return math.sqrt(v_inf ** 2 + 2 * mu / r) - math.sqrt(mu / r)


def _ejection_dv(r_target):
    a = (PARK_R + r_target) / 2.0
    return math.sqrt(MU_JOOL / PARK_R) - math.sqrt(MU_JOOL * (2 / PARK_R - 1 / a))


class HarvestLaneShapeTests(unittest.TestCase):

    def test_each_lane_reuses_the_b25_module_on_the_stripped_fixture(self):
        for moon, name in LANES.items():
            spec = _spec(name)
            self.assertEqual("b25_laythe_orbit", spec["driver"]["mission"], name)
            self.assertEqual("fixtures/saves/jool-park-nerv", spec["fixture"]["saveTemplate"], name)
            p = spec["driver"]["missionParams"]
            self.assertEqual(moon, p["targetBodyName"])
            self.assertEqual("Jool", p["homeBodyName"])
            self.assertNotIn("viaBodyNames", p, "via bodies would make the ecc floor vacuous")
            mlib.b5_params_from_dict(p)       # parses without raising

    def test_every_other_jool_moon_is_a_named_poison(self):
        for moon, name in LANES.items():
            spec = _spec(name)
            forbidden = spec["expectations"]["logContracts"]["forbidden"]
            required = spec["expectations"]["logContracts"]["required"]
            self.assertIn("SOI change boundary suppressed in tree mode: Jool to %s" % moon, required)
            for other in JOOL_MOONS:
                if other == moon:
                    continue
                self.assertIn("SOI change boundary suppressed in tree mode: \\w+ to %s" % other,
                              forbidden, (name, other))
                self.assertIn("SOI change boundary suppressed in tree mode: %s to \\w+" % other,
                              forbidden, (name, other))


class HarvestSizingTests(unittest.TestCase):

    def test_the_ecc_floor_sits_between_the_park_and_the_whole_transfer_band(self):
        for moon in MOONS:
            a, e = MOONS[moon][0], MOONS[moon][1]
            low_ecc = _transfer_ecc(a * (1 + e))         # intercept at the moon's apoapsis
            floor = _params(moon)["ejectionEccFloor"]
            self.assertGreater(floor, 0.05, moon)        # the park's ecc cap
            self.assertLess(floor, low_ecc - 0.1, moon)

    def test_the_floors_clear_the_terrain_and_are_ordered(self):
        for moon in MOONS:
            p = _params(moon)
            terrain = MOONS[moon][5]
            self.assertGreater(p["parkMinPeriapsisMeters"], terrain, moon)
            self.assertGreater(p["targetPeriapsisFloorMeters"], p["parkMinPeriapsisMeters"], moon)

    def test_the_request_survives_a_3x_miss_both_ways(self):
        for moon in MOONS:
            p = _params(moon)
            _a, _e, radius, _mu, soi, _h = MOONS[moon]
            req = p["courseCorrectPeriapsisMeters"]
            self.assertLess(3 * req, soi - radius, moon)            # 3x long is still an encounter
            self.assertGreater(0.29 * req, p["targetPeriapsisFloorMeters"], moon)  # B25's k

    def test_the_park_ceiling_admits_a_3x_long_arrival(self):
        for moon in MOONS:
            p = _params(moon)
            self.assertGreaterEqual(p["parkMaxApoapsisMeters"], 3 * p["courseCorrectPeriapsisMeters"],
                                    moon)

    def test_the_window_budget_covers_one_synodic_wait(self):
        p_park = 2 * math.pi * math.sqrt(PARK_R ** 3 / MU_JOOL)
        for moon in MOONS:
            p_moon = 2 * math.pi * math.sqrt(MOONS[moon][0] ** 3 / MU_JOOL)
            synodic = 1.0 / (1.0 / p_moon - 1.0 / p_park)
            self.assertGreater(_params(moon)["transferBurnTimeoutSeconds"], 2 * synodic, moon)

    def test_the_coast_budget_covers_the_slowest_transfer(self):
        for moon in MOONS:
            a, e = MOONS[moon][0], MOONS[moon][1]
            at = (PARK_R + a * (1 + e)) / 2.0
            half = math.pi * math.sqrt(at ** 3 / MU_JOOL)
            self.assertGreater(_params(moon)["coastTimeoutSeconds"], 2 * half, moon)

    def test_the_worst_hop_fits_the_craft(self):
        for moon in MOONS:
            p = _params(moon)
            a, e = MOONS[moon][0], MOONS[moon][1]
            eject = max(_ejection_dv(a * (1 - e)), _ejection_dv(a * (1 + e)))
            capture = _capture_dv(moon, p["targetPeriapsisFloorMeters"], V_INF_WORST[moon])
            corrections = 4 * p["maxCorrectionDvMps"]
            self.assertLess(eject + capture + corrections, DV_AVAILABLE_MPS * 0.75, moon)


# --------------------------------------------------------------------------------
# THE REPLAY LANES: each V lane's TimeJump table is derived off its fixture's bytes
# with V16M's recipe. The same derivation must reproduce V16M's committed jumps on
# `laythe-orbit-recorded`, which is what makes the derivation trustworthy.
# --------------------------------------------------------------------------------
import re  # noqa: E402

V_LANES = {"Tylo": ("V28M-tylo-player-loop.toml", "tylo-orbit-recorded"),
           "Bop": ("V29M-bop-player-loop.toml", "bop-orbit-recorded"),
           "Pol": ("V30M-pol-player-loop.toml", "pol-orbit-recorded"),
           "Laythe": ("V16M-laythe-player-loop.toml", "laythe-orbit-recorded")}
SMA = {"Laythe": 27_184_000.0, "Tylo": 68_500_000.0, "Bop": 128_500_000.0,
       "Pol": 179_890_000.0}


def _fixture_bytes(fixture, moon):
    root = os.path.join(HARNESS_ROOT, "fixtures", "saves", fixture)
    sfs = open(os.path.join(root, "persistent.sfs"), encoding="utf-8").read()
    tree = re.search(r"RECORDING_TREE\s*\{\s*id = (\w+)", sfs).group(1)
    ut0 = float(re.search(r"explicitStartUT = ([\d.]+)", sfs).group(1))
    end = float(re.search(r"explicitEndUT = ([\d.]+)", sfs).group(1))
    save_ut = float(re.search(r"(?m)^\s*UT = ([\d.]+)", sfs).group(1))
    rec_dir = os.path.join(root, "Parsek", "Recordings")
    txt = [f for f in os.listdir(rec_dir) if f.endswith(".prec.txt")]
    body = open(os.path.join(rec_dir, txt[0]), encoding="utf-8").read()
    segs = [(float(re.search(r"startUT = ([\d.]+)", b).group(1)),
             float(re.search(r"endUT = ([\d.]+)", b).group(1)),
             re.search(r"body = (\w+)", b).group(1))
            for b in re.findall(r"ORBIT_SEGMENT\s*\{(.*?)\n\s*\}", body, re.S)]
    return tree, ut0, end, save_ut, segs


def _derived_jumps(fixture, moon):
    tree, ut0, end, save_ut, segs = _fixture_bytes(fixture, moon)
    moon_segs = [s for s in segs if s[2] == moon]
    seam, last_end = moon_segs[0][0], max(s[1] for s in moon_segs)
    period = 2 * math.pi * math.sqrt(SMA[moon] ** 3 / MU_JOOL)
    k = math.ceil((max(save_ut, end) - ut0) / period - 1e-9)
    anchor = ut0 + k * period
    seam_off = seam - ut0
    park_off = seam_off + (last_end - seam) + 0.707 * (end - last_end)
    jumps = [round(anchor + seam_off + o) for o in (-180, -60, 140)] + [round(anchor + park_off)]
    return tree, jumps, end - ut0 - park_off


def _spec_jumps(name):
    steps = _spec(name)["driver"]["steps"]
    return [int(s["args"]["ut"]) for s in steps if s.get("cmd") == "TimeJump"]


class ReplayLaneTests(unittest.TestCase):

    def test_the_recipe_reproduces_v16ms_committed_cycle_one(self):
        _tree, jumps, _clear = _derived_jumps("laythe-orbit-recorded", "Laythe")
        self.assertEqual(jumps, _spec_jumps("V16M-laythe-player-loop.toml")[:4])

    def test_each_v_lane_jump_table_is_derived_from_its_fixture(self):
        for moon in ("Tylo", "Bop", "Pol"):
            name, fixture = V_LANES[moon]
            tree, jumps, clear = _derived_jumps(fixture, moon)
            self.assertEqual(jumps, _spec_jumps(name), moon)
            self.assertEqual(jumps, sorted(jumps), moon)            # strictly forward
            self.assertGreater(clear, 30.0, moon)                   # park epoch inside the tail
            cfg = [s for s in _spec(name)["driver"]["steps"] if s.get("cmd") == "MissionConfig"]
            self.assertEqual(tree, cfg[0]["args"]["tree"], moon)

    def test_each_v_lane_requires_both_replay_witnesses_on_its_own_moon(self):
        for moon in ("Tylo", "Bop", "Pol"):
            name, fixture = V_LANES[moon]
            spec = _spec(name)
            req = spec["expectations"]["logContracts"]["required"]
            self.assertIn("phase=body-orbit surface=ProtoOrbitLine .*body=%s" % moon, req)
            self.assertIn("seam-endpoint summary evaluated=[1-9]\d* outsideSoi=0", req)
            self.assertEqual("fixtures/saves/" + fixture, spec["fixture"]["saveTemplate"])
            self.assertIn(moon.lower(), spec["dimensionsCovered"]["D14"])
            self.assertTrue(spec["expectations"]["rewind"]["gating"], moon)
            self.assertTrue(spec["expectations"]["recordings"]["structure"]["gating"], moon)


if __name__ == "__main__":
    unittest.main()
