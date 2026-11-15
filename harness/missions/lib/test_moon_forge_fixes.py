"""Unit cells for the three moon-to-moon forge fixes of 2026-11-15.

The first flights of B43 / B44 / B46 (runs 2026-11-15_1617, _1622_a2, _1632_a2,
_1638, _1743_a2) all ended on the mission side:

  B44  the unaimed Laythe escape left almost RADIALLY, the Jool-frame orbit
       straddled Laythe's own orbit, and the coast met Laythe again before the
       stage-2 node. Fix: `escapeExtraParkOrbits`, the pass the burn waits for.
  B46  dropping off rails warp moved a 45.55 Mm apoapsis by 39.5 km with no
       thrust, which read as a finished burn, and the stage-2 TRANSFER-BURN
       exited 478 s BEFORE its node. Fix: a relative burn-changed floor.
  B43  every course correction (85.6 .. 169 m/s) was refused by the spec's
       50 m/s cap; spec change only (cap 120), pinned here.

Run:
    cd harness && python -m unittest discover -s missions/lib -q
"""

import math
import os
import sys
import tomllib
import unittest
from dataclasses import replace

_MISSIONS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
_LIB = os.path.dirname(os.path.abspath(__file__))
if _LIB not in sys.path:
    sys.path.insert(0, _LIB)

import mlib                    # noqa: E402
import test_b26_laythe_vall as t26  # noqa: E402

HARNESS_ROOT = os.path.dirname(os.path.dirname(_LIB))


def _spec(name):
    with open(os.path.join(HARNESS_ROOT, "scenarios", name), "rb") as fh:
        return tomllib.load(fh)


# ---------------------------------------------------------------------------
# A small patched-conic model of the Laythe escape, used to show WHICH pass the
# B44 spec picks and why. Stock constants; the park is `laythe-park-nerv`'s own
# ORBIT node (REF 9 = Laythe). Laythe's elements are stock (circular, inc 0,
# mEp 3.14 at epoch 0), in the same element frame as the vessel's.
MU_JOOL = 2.82528e14
R_JOOL = 6.0e6
MU_LAYTHE = 1.962e12
SOI_LAYTHE = 3723645.8
LAYTHE = dict(a=27184000.0, e=0.0, i=0.0, lpe=0.0, lan=0.0, mna=3.14, eph=0.0)
PARK = dict(a=572085.80370560708, e=0.027697758715375592, i=16.372447690149517,
            lpe=315.12576860932603, lan=184.98306525700974,
            mna=2.4660729135790693, eph=28817025.337051559)
# The escape node B44 flight 1 placed (`gate escapeNodeUt ... ->28818204.509`):
# the first periapsis pass the min-lead rule accepts.
FLOWN_NODE_UT = 28818204.509


def _rot(lan, inc, argp):
    co, so = math.cos(lan), math.sin(lan)
    ci, si = math.cos(inc), math.sin(inc)
    cw, sw = math.cos(argp), math.sin(argp)
    return [[co * cw - so * sw * ci, -co * sw - so * cw * ci, so * si],
            [so * cw + co * sw * ci, -so * sw + co * cw * ci, -co * si],
            [sw * si, cw * si, ci]]


def _mv(m, v):
    return [sum(m[r][c] * v[c] for c in range(3)) for r in range(3)]


def _kepler(mean, e):
    ecc_anom = mean
    for _ in range(60):
        ecc_anom -= (ecc_anom - e * math.sin(ecc_anom) - mean) / (1 - e * math.cos(ecc_anom))
    return ecc_anom


def _state(o, mu, t):
    a, e = o["a"], o["e"]
    mean = (o["mna"] + math.sqrt(mu / a ** 3) * (t - o["eph"])) % (2 * math.pi)
    ea = _kepler(mean, e)
    nu = 2 * math.atan2(math.sqrt(1 + e) * math.sin(ea / 2),
                        math.sqrt(1 - e) * math.cos(ea / 2))
    p = a * (1 - e * e)
    r = p / (1 + e * math.cos(nu))
    pos = [r * math.cos(nu), r * math.sin(nu), 0.0]
    vel = [-math.sqrt(mu / p) * math.sin(nu), math.sqrt(mu / p) * (e + math.cos(nu)), 0.0]
    m = _rot(math.radians(o["lan"]), math.radians(o["i"]), math.radians(o["lpe"]))
    return _mv(m, pos), _mv(m, vel)


def _norm(v):
    return math.sqrt(sum(x * x for x in v))


def escape_outcome(node_ut, vsoi):
    """(exit UT, Jool-frame periapsis alt, apoapsis alt, prograde share of v_rel)
    for a prograde escape burn at the park periapsis nearest `node_ut`."""
    n = math.sqrt(MU_LAYTHE / PARK["a"] ** 3)
    mean = (PARK["mna"] + n * (node_ut - PARK["eph"])) % (2 * math.pi)
    t_pe = node_ut - mean / n if mean < math.pi else node_ut + (2 * math.pi - mean) / n
    r, v = _state(PARK, MU_LAYTHE, t_pe)
    rp, vp = _norm(r), _norm(v)
    v_need = math.sqrt(vsoi ** 2 + 2 * MU_LAYTHE * (1 / rp - 1 / SOI_LAYTHE))
    a = 1 / (2 / rp - v_need ** 2 / MU_LAYTHE)
    e = rp * v_need ** 2 / MU_LAYTHE - 1
    p = a * (1 - e * e)
    nu = math.acos((p / SOI_LAYTHE - 1) / e)
    ea = 2 * math.atan(math.sqrt((1 - e) / (1 + e)) * math.tan(nu / 2))
    t_exit = t_pe + (ea - e * math.sin(ea)) / math.sqrt(MU_LAYTHE / a ** 3)
    h = [r[1] * v[2] - r[2] * v[1], r[2] * v[0] - r[0] * v[2], r[0] * v[1] - r[1] * v[0]]
    pv = [x / rp for x in r]
    w = [x / _norm(h) for x in h]
    q = [w[1] * pv[2] - w[2] * pv[1], w[2] * pv[0] - w[0] * pv[2], w[0] * pv[1] - w[1] * pv[0]]
    vs = math.sqrt(MU_LAYTHE * (2 / SOI_LAYTHE - 1 / a))
    fpa = math.atan2(e * math.sin(nu), 1 + e * math.cos(nu))
    ang = nu + math.pi / 2 - fpa
    r_rel = [SOI_LAYTHE * (math.cos(nu) * pv[k] + math.sin(nu) * q[k]) for k in range(3)]
    v_rel = [vs * (math.cos(ang) * pv[k] + math.sin(ang) * q[k]) for k in range(3)]
    r_l, v_l = _state(LAYTHE, MU_JOOL, t_exit)
    big_r = [r_l[k] + r_rel[k] for k in range(3)]
    big_v = [v_l[k] + v_rel[k] for k in range(3)]
    rr = _norm(big_r)
    sma = 1 / (2 / rr - sum(x * x for x in big_v) / MU_JOOL)
    hj = [big_r[1] * big_v[2] - big_r[2] * big_v[1], big_r[2] * big_v[0] - big_r[0] * big_v[2],
          big_r[0] * big_v[1] - big_r[1] * big_v[0]]
    ecc = math.sqrt(max(0.0, 1 - sum(x * x for x in hj) / (MU_JOOL * sma)))
    prograde = sum(v_rel[k] * v_l[k] for k in range(3)) / _norm(v_l)
    return t_exit, sma * (1 - ecc) - R_JOOL, sma * (1 + ecc) - R_JOOL, prograde / vs


class EscapeExtraParkOrbitsTests(unittest.TestCase):
    def setUp(self):
        self.p = mlib.b5_params_from_dict(t26.relay_params())
        self.period = 2.0 * math.pi * math.sqrt(t26.PARK_SMA ** 3 / t26.MU_LAYTHE)

    def test_absent_is_zero_and_the_node_is_unchanged(self):
        self.assertEqual(0, self.p.escape_extra_park_orbits)
        plan = mlib.escape_node_plan(self.p, t26.relay_snap(time_to_periapsis=900.0))
        self.assertAlmostEqual(plan.node_ut, t26.PARK_UT + 900.0, delta=1e-6)

    def test_k_adds_k_whole_park_periods_and_keeps_the_dv(self):
        base = mlib.escape_node_plan(self.p, t26.relay_snap(time_to_periapsis=900.0))
        p5 = mlib.b5_params_from_dict(t26.relay_params(escapeExtraParkOrbits=5))
        plan = mlib.escape_node_plan(p5, t26.relay_snap(time_to_periapsis=900.0))
        self.assertEqual("", plan.reason)
        self.assertAlmostEqual(plan.node_ut, base.node_ut + 5 * self.period, delta=1e-6)
        self.assertAlmostEqual(plan.dv, base.dv, delta=1e-9)

    def test_k_stacks_on_the_min_lead_roll(self):
        # The min-lead rule picks the pass first; k counts from THAT pass.
        p2 = mlib.b5_params_from_dict(t26.relay_params(escapeExtraParkOrbits=2))
        plan = mlib.escape_node_plan(p2, t26.relay_snap(time_to_periapsis=10.0))
        self.assertAlmostEqual(plan.node_ut, t26.PARK_UT + 10.0 + 3 * self.period,
                               delta=1e-6)

    def test_a_non_whole_or_negative_count_is_refused_at_load(self):
        for bad in (-1, 1.5, True, "3"):
            with self.assertRaises(ValueError, msg=repr(bad)):
                mlib.b5_params_from_dict(t26.relay_params(escapeExtraParkOrbits=bad))

    def test_the_b44_spec_waits_five_passes_and_parses(self):
        mp = _spec("B44-laythe-tylo-transfer.toml")["driver"]["missionParams"]
        self.assertEqual(5, mp["escapeExtraParkOrbits"])
        self.assertEqual(5, mlib.b5_params_from_dict(mp).escape_extra_park_orbits)


class B44EscapePassGeometryTests(unittest.TestCase):
    """The model reproduces the flown pass, and the pass the spec picks leaves
    Laythe along its own velocity."""

    PERIOD = 2.0 * math.pi * math.sqrt(PARK["a"] ** 3 / MU_LAYTHE)

    def test_the_model_reproduces_flight_1(self):
        # Flight 1's first Jool frame: ut=28822984.064, pe=19,858,979 m,
        # ap=23,465,228 m (altitudes).
        t_exit, pe, ap, _ = escape_outcome(FLOWN_NODE_UT, 500.0)
        self.assertAlmostEqual(t_exit, 28822984.064, delta=30.0)
        self.assertAlmostEqual(pe, 19_858_979.0, delta=150_000.0)
        self.assertAlmostEqual(ap, 23_465_228.0, delta=500_000.0)

    def test_flight_1s_pass_left_radially_and_straddled_laythes_orbit(self):
        _, pe, ap, prograde = escape_outcome(FLOWN_NODE_UT, 500.0)
        laythe_alt = LAYTHE["a"] - R_JOOL
        self.assertLess(pe, laythe_alt)
        self.assertGreater(ap, laythe_alt)
        self.assertLess(prograde, 0.5)

    def test_the_spec_pass_and_its_neighbours_leave_prograde(self):
        k = _spec("B44-laythe-tylo-transfer.toml")["driver"]["missionParams"][
            "escapeExtraParkOrbits"]
        laythe_alt = LAYTHE["a"] - R_JOOL
        for kk in (k - 1, k, k + 1):
            _, pe, ap, prograde = escape_outcome(FLOWN_NODE_UT + kk * self.PERIOD, 500.0)
            self.assertGreater(prograde, 0.9, msg="k=%d" % kk)
            self.assertGreater(pe, laythe_alt, msg="k=%d" % kk)
            self.assertGreater(ap, 55_000_000.0, msg="k=%d" % kk)


class BurnChangedFloorTests(unittest.TestCase):
    # B46 flight 1: entry ap 45,550,005.703 m, the first settled off-rails
    # reading 45,510,548.753 m, periapsis unchanged, node still 478 s away.
    ENTRY_AP = 45_550_005.703
    ENTRY_PE = 28_813_252.865
    JITTER_AP = 45_510_548.753

    def test_the_off_rails_jitter_is_not_a_burn(self):
        self.assertFalse(mlib._burn_apsis_changed(self.JITTER_AP, self.ENTRY_AP))

    def test_a_real_burn_on_the_same_orbit_is(self):
        self.assertTrue(mlib._burn_apsis_changed(46_500_000.0, self.ENTRY_AP))

    def test_small_orbits_keep_the_absolute_floor(self):
        self.assertTrue(mlib._burn_apsis_changed(111_000.0, 100_000.0))
        self.assertFalse(mlib._burn_apsis_changed(105_000.0, 100_000.0))

    def _stage2_state(self):
        p = mlib.b5_params_from_dict(t26.relay_params())
        st = mlib.b5_initial_state(p)
        return replace(st, burn_entry_ap=self.ENTRY_AP, burn_entry_pe=self.ENTRY_PE,
                       burn_prev_ap=None, burn_prev_pe=None, burn_static_since=None)

    def test_the_b46_frames_no_longer_read_as_a_wedged_finished_burn(self):
        st = self._stage2_state()
        stuck = burned = False
        for i in range(200):
            snap = mlib.TelemetrySnapshot(ut=597_192.996 + i, apoapsis=self.JITTER_AP,
                                          periapsis=self.ENTRY_PE, warp_mode="NONE")
            st, stuck, _nostart, burned = mlib._b5_track_burn_stagnation(st, snap)
        self.assertFalse(burned)
        self.assertFalse(stuck)

    def test_a_wedged_executor_after_a_real_burn_still_reads_stuck(self):
        st = self._stage2_state()
        stuck = False
        for i in range(200):
            snap = mlib.TelemetrySnapshot(ut=597_800.0 + i, apoapsis=60_000_000.0,
                                          periapsis=self.ENTRY_PE, warp_mode="NONE")
            st, stuck, _nostart, burned = mlib._b5_track_burn_stagnation(st, snap)
        self.assertTrue(burned)
        self.assertTrue(stuck)


class B43CorrectionCapTests(unittest.TestCase):
    def test_the_cap_admits_the_first_measured_round(self):
        # Flights 1 and 2 both priced the first round at 85.6 m/s.
        mp = _spec("B43-vall-tylo-transfer.toml")["driver"]["missionParams"]
        self.assertGreaterEqual(mp["maxCorrectionDvMps"], 85.6 * 1.25)


if __name__ == "__main__":
    unittest.main()
