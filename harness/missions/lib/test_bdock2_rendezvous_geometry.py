"""Unit cells for BDOCK-2's rendezvous fix (todo
BDOCK2-RENDEZVOUS-PHASING-BURN-DEORBITS-THE-INTERCEPTOR).

MechJeb 2.15.1's rendezvous AP deorbited BDOCK-2's Interceptor three ways: a
velocity match at a 26 km pass (2026-10-12 / 10-13), a 121 m/s "Hohmann" from
MechJebLib's free-time TwoImpulseTransfer after a plane match (2026-10-14_1754 /
_1947 / _2037), and an 88 m/s close-range intercept after a machine-planned
transfer missed (2026-10-14_2037). BDOCK-1 flies the same Station orbit (117 x
102 km, ecc 0.011) and gets a plain 17 m/s Hohmann, from a start with the
Station ~58 deg ahead and the launch planes ~0.007 deg apart (no plane match).

THE FIX. The launch hold (spec interceptorLaunchNotBeforeUt) is retuned to start
BDOCK-2 like BDOCK-1: the Station 58 deg ahead AND the launch plane within ~0.01
deg of the Station's. The RENDEZVOUS NODE GUARD never lets a node burn whose
post-node periapsis (kRPC Node.orbit) is under the atmosphere top + 5 km: the
mission ends rendezvous-plan-deorbits before that burn.

The GEOMETRY cells propagate the fixture's Station orbit and the 2026-10-13
Interceptor orbit (coplanar two-body), reproduce both red flights' measured
RENDEZVOUS-start distances, fit the Interceptor LAN against launch UT over seven
measured launches (BDOCK-1 included), and derive the hold UT from both.

NO krpc, NO KSP, NO network.
"""

import math
import os
import re
import sys
import tomllib
import unittest
from dataclasses import replace

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
for _p in (_MISSIONS, _HERE):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import mlib  # noqa: E402
from test_mlib import snap  # noqa: E402

SPEC_PATH = os.path.join(_HARNESS, "scenarios", "BDOCK-2-second-dock-harvest.toml")
FIXTURE_SFS = os.path.join(_HARNESS, "fixtures", "saves", "bdock-recorded",
                           "persistent.sfs")
STATION_PID = 3620499050          # the Station the spec's Dock token names

KERBIN_MU = 3.5316e12
KERBIN_RADIUS = 600000.0
KERBIN_SIDEREAL_DAY = 21549.425   # s; stock Kerbin rotationPeriod

# MEASURED, BDOCK-2 2026-10-13_0309 (mission stdout + the produced save's
# Interceptor .prec.txt ORBIT_SEGMENT that opens at RENDEZVOUS start).
RED_LAUNCH_UT = 8953.388          # PRELAUNCH -> INT-LAUNCH (launch_vessel)
RED_RV_START_UT = 9263.302        # the recorded segment's epoch
RED_RV_START_DISTANCE = 193278.906  # tgtD on the RENDEZVOUS entry frame
RED_NODE_UT = 11178.153           # MechJeb's first rendezvous node
RED_INTERCEPTOR = dict(sma=689897.29001809587, ecc=0.00803450824531253,
                       lan=287.3693053823946, argpe=178.58734555710373,
                       mna=1.6517439786193051, epoch=9263.3021292497324)

NAN = float("nan")


def _station_elements():
    with open(FIXTURE_SFS, "r", encoding="utf-8") as fh:
        text = fh.read()
    for block in re.findall(r"\n\t\tVESSEL\n\t\t\{(.*?)\n\t\t\}", text, re.S):
        if "\n\t\t\tpersistentId = %d\n" % STATION_PID not in block:
            continue
        orbit = dict(re.findall(r"(\w+) = (\S+)",
                                re.search(r"ORBIT\s*\{(.*?)\}", block, re.S).group(1)))
        return dict(sma=float(orbit["SMA"]), ecc=float(orbit["ECC"]),
                    lan=float(orbit["LAN"]), argpe=float(orbit["LPE"]),
                    mna=float(orbit["MNA"]), epoch=float(orbit["EPH"]))
    raise AssertionError("fixture holds no vessel pid %d" % STATION_PID)


def _polar(el, ut, lon_shift_deg=0.0, time_shift=0.0):
    """(radius m, true longitude deg) of a coplanar two-body orbit at ``ut``. A
    launch delayed by ``time_shift`` flies the same pad-relative trajectory
    ``time_shift`` later and Kerbin-rotation further east (``lon_shift_deg``)."""
    n = math.sqrt(KERBIN_MU / el["sma"] ** 3)
    m = el["mna"] + n * (ut - time_shift - el["epoch"])
    e = m
    for _ in range(60):
        e -= (e - el["ecc"] * math.sin(e) - m) / (1.0 - el["ecc"] * math.cos(e))
    nu = 2.0 * math.atan2(math.sqrt(1 + el["ecc"]) * math.sin(e / 2),
                          math.sqrt(1 - el["ecc"]) * math.cos(e / 2))
    r = el["sma"] * (1.0 - el["ecc"] * math.cos(e))
    return r, (el["lan"] + el["argpe"] + math.degrees(nu) + lon_shift_deg) % 360.0


def _geometry(delay):
    """Station lead (deg) and distance (m) at RENDEZVOUS start, and the closest
    approach (m, ut) over the Interceptor's next orbit, for a launch ``delay`` s
    after the red run's."""
    station = _station_elements()
    shift = 360.0 * delay / KERBIN_SIDEREAL_DAY
    t0 = RED_RV_START_UT + delay

    def sep(ut):
        rs, ls = _polar(station, ut)
        ri, li = _polar(RED_INTERCEPTOR, ut, shift, delay)
        d = math.radians(ls - li)
        return math.sqrt(rs * rs + ri * ri - 2 * rs * ri * math.cos(d)), (
            (ls - li + 180.0) % 360.0 - 180.0)

    period = 2 * math.pi * math.sqrt(RED_INTERCEPTOR["sma"] ** 3 / KERBIN_MU)
    dist0, lead0 = sep(t0)
    best = min((sep(t0 + period * k / 2000.0)[0], t0 + period * k / 2000.0)
               for k in range(2001))
    return lead0, dist0, best[0], best[1], station




# ---------------------------------------------------------------------------
# LAUNCH PLANE. The Interceptor's LAN is the inertial longitude of the pad at
# launch plus a fixed ascent offset: LAN = c + 360 / sidereal_day * t_launch.
# MEASURED (launch_vessel UT, LAN of the first circular segment after
# INT-SEPARATE in each run's produced .prec.txt; BDOCK-1 from the fixture's
# Interceptor-half probe, epoch 698.1).
LAN_SAMPLES = (
    (8952.79, 287.149),     # BDOCK-2 2026-09-23_1704
    (8953.27, 287.799),     # BDOCK-2 2026-10-12_1748
    (8953.388, 287.369),    # BDOCK-2 2026-10-13_0309
    (9176.1, 290.968),      # BDOCK-2 2026-10-14_1754
    (9176.558, 290.734),    # BDOCK-2 2026-10-14_1947
    (9177.798, 290.660),    # BDOCK-2 2026-10-14_2037
    (388.454, 143.736),     # BDOCK-1 2026-10-11_1400
)
INTERCEPTOR_INC = 0.0875   # deg, the mean of the same segments (0.0868..0.0882)
TARGET_LEAD_DEG = 58.0     # BDOCK-1's Station lead at RENDEZVOUS start (tgtD 683 km)
WK = 360.0 / KERBIN_SIDEREAL_DAY


def _lan_offset():
    return sum(lan - WK * ut for ut, lan in LAN_SAMPLES) / len(LAN_SAMPLES)


def _relative_inclination(launch_ut):
    """(relative inclination deg, LAN difference deg) of the Interceptor's
    plane against the fixture Station's, small-angle."""
    st = _station_elements_full()
    lan = (_lan_offset() + WK * launch_ut) % 360.0
    a = (INTERCEPTOR_INC * math.sin(math.radians(lan))
         - st["inc"] * math.sin(math.radians(st["lan"])),
         -INTERCEPTOR_INC * math.cos(math.radians(lan))
         + st["inc"] * math.cos(math.radians(st["lan"])))
    return math.hypot(*a), (lan - st["lan"] + 180.0) % 360.0 - 180.0


def _station_elements_full():
    with open(FIXTURE_SFS, "r", encoding="utf-8") as fh:
        text = fh.read()
    for block in re.findall(r"\n\t\tVESSEL\n\t\t\{(.*?)\n\t\t\}", text, re.S):
        if "\n\t\t\tpersistentId = %d\n" % STATION_PID in block:
            orbit = dict(re.findall(r"(\w+) = (\S+)", re.search(
                r"ORBIT\s*\{(.*?)\}", block, re.S).group(1)))
            return dict(inc=float(orbit["INC"]), lan=float(orbit["LAN"]))
    raise AssertionError("fixture holds no vessel pid %d" % STATION_PID)


def _lead_crossings(lo_ut, hi_ut, lead=TARGET_LEAD_DEG):
    """Launch UTs in [lo, hi] at which the Station is ``lead`` deg ahead at
    RENDEZVOUS start (bisection on the propagated geometry)."""
    station = _station_elements()

    def f(launch_ut):
        delay = launch_ut - RED_LAUNCH_UT
        t0 = RED_RV_START_UT + delay
        ls = _polar(station, t0)[1]
        li = _polar(RED_INTERCEPTOR, t0, 360.0 * delay / KERBIN_SIDEREAL_DAY, delay)[1]
        return ((ls - li + 180.0) % 360.0 - 180.0 - lead + 180.0) % 360.0 - 180.0
    out = []
    x, fx = lo_ut, f(lo_ut)
    while x < hi_ut:
        y = x + 20.0
        fy = f(y)
        if fx < 0.0 <= fy:
            a, b = x, y
            for _ in range(40):
                m = (a + b) / 2.0
                if (f(a) < 0.0) == (f(m) < 0.0):
                    a = m
                else:
                    b = m
            out.append((a + b) / 2.0)
        x, fx = y, fy
    return out


def _spec_params():
    with open(SPEC_PATH, "rb") as fh:
        return tomllib.load(fh)["driver"]["missionParams"]


class LaunchGeometryModelTests(unittest.TestCase):
    """The propagation model reproduces the measured flights before it picks
    the launch UT."""

    def test_the_red_launch_reproduces_the_measured_distance_and_the_26_km_pass(self):
        lead, dist, ca, ca_ut, station = _geometry(0.0)
        self.assertAlmostEqual(15.75, lead, delta=0.05)
        self.assertAlmostEqual(RED_RV_START_DISTANCE, dist, delta=1500.0)
        self.assertLess(ca, station["sma"] / 25.0)
        self.assertAlmostEqual(RED_NODE_UT, ca_ut, delta=5.0)

    def test_the_9176_launch_reproduces_its_measured_distance(self):
        # 2026-10-14_1754: RENDEZVOUS entry read tgtD=615,629.5 m.
        _, dist, _, _, _ = _geometry(9176.1 - RED_LAUNCH_UT)
        self.assertAlmostEqual(615629.5, dist, delta=5000.0)

    def test_the_lan_model_fits_every_sample_including_bdock1(self):
        c = _lan_offset()
        for ut, lan in LAN_SAMPLES:
            with self.subTest(ut=ut):
                self.assertAlmostEqual(lan, (c + WK * ut) % 360.0, delta=1.0)

    def test_the_red_launches_had_a_plane_match_and_bdock1_did_not(self):
        # MechJeb plane-matches above 0.05 deg relative inclination.
        self.assertGreater(_relative_inclination(8953.388)[0], 0.15)
        self.assertGreater(_relative_inclination(9176.1)[0], 0.15)
        bdock1, _ = _relative_inclination(388.454)
        # BDOCK-1's Station LAN is the fixture's; its Interceptor sat ~0.007 deg off.
        self.assertLess(bdock1, 0.02)


class LaunchHoldUtTests(unittest.TestCase):
    """THE DERIVATION of interceptorLaunchNotBeforeUt: of the launch UTs that
    start RENDEZVOUS with the Station 58 deg ahead (one every ~2,203 s), the
    one whose launch plane is closest to the Station's."""

    def test_the_spec_ut_is_the_derived_one(self):
        crossings = _lead_crossings(9000.0, 26000.0)
        self.assertEqual(8, len(crossings))
        best = min(crossings, key=lambda u: _relative_inclination(u)[0])
        self.assertAlmostEqual(22421.5, best, delta=2.0)
        spec_ut = _spec_params()["interceptorLaunchNotBeforeUt"]
        self.assertAlmostEqual(best, spec_ut, delta=2.0)

    def test_the_spec_ut_meets_both_constraints(self):
        spec_ut = _spec_params()["interceptorLaunchNotBeforeUt"]
        lead, _, ca, _, station = _geometry(spec_ut - RED_LAUNCH_UT)
        rel, dlan = _relative_inclination(spec_ut)
        self.assertAlmostEqual(TARGET_LEAD_DEG, lead, delta=0.5)
        self.assertLess(rel, 0.012)        # MechJeb's plane-match threshold is 0.05
        self.assertLess(abs(dlan), 7.0)
        self.assertGreater(ca, 10.0 * station["sma"] / 25.0)

    def test_the_hold_tolerance_keeps_the_geometry(self):
        spec_ut = _spec_params()["interceptorLaunchNotBeforeUt"]
        early = spec_ut - mlib.SDOCK_LAUNCH_HOLD_TOLERANCE_S
        self.assertAlmostEqual(TARGET_LEAD_DEG,
                               _geometry(early - RED_LAUNCH_UT)[0], delta=0.6)
        self.assertLess(_relative_inclination(early)[0], 0.012)


# 2026-10-14 RENDEZVOUS nodes, mission stdout + the produced saves: the 6.3 m/s
# plane match (pe unchanged) and the 121.1 m/s node whose orbit was sma 644,059 m
# ecc 0.0893 (pe -13.5 km).
CONFIRM_PLANE_NODE = dict(ut=9487.012, node_count=1, node_ut=10041.303,
                          node_dv=6.327, node_next_pe=84330.0)
CONFIRM_BAD_NODE = dict(ut=10049.001, node_count=1, node_ut=10239.116,
                        node_dv=121.123,
                        node_next_pe=644059.08 * (1 - 0.089295) - 600000.0)
# BDOCK-1 2026-10-11_1400 (PASS): its Hohmann raise (17.2 m/s, pe kept at 84 km)
# and its arrival velocity match (14.6 m/s onto the Station's 117 x 102 km orbit).
BDOCK1_NODES = (dict(ut=699.47, node_count=1, node_ut=7748.234, node_dv=17.219,
                     node_next_pe=84341.0),
                dict(ut=7757.306, node_count=1, node_ut=8672.480, node_dv=14.606,
                     node_next_pe=101700.0))


def rv_frame(**kw):
    base = dict(apoapsis=95505.0, periapsis=84300.0, altitude=94500.0,
                situation="ORBITING", body="Kerbin", atmosphere_depth=70000.0,
                mj_rendezvous_enabled=True, target_set=True,
                target_distance=500000.0, target_rel_speed=60.0)
    base.update(kw)
    return snap(**base)


def rv_state(**kw):
    st = mlib.bdock_initial_state(mlib.BDockParams())
    st = replace(st, phase=mlib.BDOCK_RENDEZVOUS, phase_entry_ut=9486.492,
                 phases_reached=st.phases_reached + (mlib.BDOCK_RENDEZVOUS,),
                 rendezvous_ever_enabled=True, rendezvous_min_distance=math.inf)
    return replace(st, **kw)


def _kinds(actions):
    return [a.kind for a in actions]


class NodeGuardTests(unittest.TestCase):

    def test_the_deorbiting_nodes_trip(self):
        self.assertTrue(mlib.rv_node_deorbits(1, CONFIRM_BAD_NODE["node_next_pe"],
                                              70000.0))
        # 2026-10-13: the 96.7 m/s velocity match at the 26 km pass, pe 31 km;
        # 2026-10-14_2037: the 88.2 m/s close-range intercept, pe 5.9 km.
        self.assertTrue(mlib.rv_node_deorbits(1, 31304.0, 70000.0))
        self.assertTrue(mlib.rv_node_deorbits(1, 5913.0, 70000.0))

    def test_plane_match_and_bdock1_nodes_do_not(self):
        for node in (CONFIRM_PLANE_NODE,) + BDOCK1_NODES:
            with self.subTest(node_ut=node["node_ut"]):
                self.assertFalse(mlib.rv_node_deorbits(
                    1, node["node_next_pe"], 70000.0))

    def test_the_floor_is_the_atmosphere_top_plus_the_margin(self):
        self.assertEqual(75000.0, mlib.rv_guard_floor(70000.0))
        self.assertTrue(mlib.rv_node_deorbits(1, 74999.0, 70000.0))
        self.assertFalse(mlib.rv_node_deorbits(1, 75000.0, 70000.0))
        self.assertEqual(mlib.RV_GUARD_AIRLESS_FLOOR_M, mlib.rv_guard_floor(0.0))

    def test_unread_inputs_and_no_node_fail_open(self):
        self.assertFalse(mlib.rv_node_deorbits(0, -13450.0, 70000.0))
        self.assertFalse(mlib.rv_node_deorbits(1, NAN, 70000.0))
        self.assertFalse(mlib.rv_node_deorbits(1, -13450.0, NAN))

    def test_bdock1_and_the_plane_match_fly_on(self):
        st = rv_state()
        for node in (CONFIRM_PLANE_NODE,) + BDOCK1_NODES:
            st, actions = mlib.bdock_decide(st, rv_frame(**node))
            self.assertNotIn(mlib.ACTION_MJ_DISABLE_RENDEZVOUS, _kinds(actions))
        self.assertFalse(st.done)

    def test_a_deorbiting_node_ends_the_mission_before_it_burns(self):
        st, actions = mlib.bdock_decide(rv_state(), rv_frame(**CONFIRM_BAD_NODE))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertTrue(st.loss_reason.startswith("rendezvous-plan-deorbits: "))
        self.assertIn("dv=121.1 m/s would leave pe=-13", st.loss_reason)
        self.assertEqual([mlib.ACTION_MJ_DISABLE_RENDEZVOUS,
                          mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES], _kinds(actions))

    def test_an_armed_rails_hold_is_torn_down_on_the_trip(self):
        st = rv_state(rv_hold_ut=10100.0, rv_hold_node_ut=10239.116,
                      rv_hold_warp_cmd=10100.0)
        st, actions = mlib.bdock_decide(st, rv_frame(**CONFIRM_BAD_NODE))
        self.assertTrue(st.done)
        self.assertIn(mlib.ACTION_CANCEL_WARP, _kinds(actions))


T = 22422.0


def _prelaunch(target=T):
    params = replace(mlib.sdock_params_from_dict({}), launch_not_before_ut=target)
    return mlib.sdock_initial_state(params)


def _pad(ut, warp="NONE", warping_to=NAN):
    return snap(ut=ut, warp_mode=warp, warping_to=warping_to,
                altitude=116184.3, apoapsis=117171.5,
                periapsis=101410.0, situation="ORBITING", body="Kerbin")


class LaunchHoldTests(unittest.TestCase):

    def test_the_first_frame_warps_to_the_launch_ut(self):
        st, actions = mlib.sdock_decide(_prelaunch(), _pad(8953.388))
        self.assertEqual(mlib.BDOCK_PRELAUNCH, st.phase)
        self.assertEqual([mlib.ACTION_WARP_TO_UT], _kinds(actions))
        self.assertEqual(T, actions[0].value)
        self.assertEqual(1, st.launch_hold_issues)

    def test_an_early_warp_landing_is_topped_up_then_launches_on_time(self):
        st, _ = mlib.sdock_decide(_prelaunch(), _pad(8953.388))
        st, actions = mlib.sdock_decide(st, _pad(15000.0, "RAILS"))
        self.assertEqual([], actions)
        # KSP's WarpTo lands ~18 s early (2026-10-14: 9176-9178 for 9195): the
        # hold does not launch 18 s early (3 deg of lead), it re-issues the warp.
        st, actions = mlib.sdock_decide(st, _pad(T - 18.0))
        self.assertEqual(mlib.BDOCK_PRELAUNCH, st.phase)
        self.assertEqual([mlib.ACTION_WARP_TO_UT], _kinds(actions))
        st, actions = mlib.sdock_decide(st, _pad(T - 1.5))
        self.assertEqual(mlib.BDOCK_INT_LAUNCH, st.phase)
        self.assertEqual([mlib.ACTION_CAPTURE_STATION, mlib.ACTION_LAUNCH_VESSEL],
                         _kinds(actions))

    def test_a_warp_still_running_at_the_target_is_cancelled_once(self):
        st, _ = mlib.sdock_decide(_prelaunch(), _pad(8953.388))
        st, actions = mlib.sdock_decide(st, _pad(T - 1.0, "RAILS"))
        self.assertEqual([mlib.ACTION_CANCEL_WARP], _kinds(actions))
        st, actions = mlib.sdock_decide(st, _pad(T - 0.5, "RAILS"))
        self.assertEqual([], actions)
        st, actions = mlib.sdock_decide(st, _pad(T))
        self.assertEqual(mlib.BDOCK_INT_LAUNCH, st.phase)

    def test_a_set_warping_to_counts_as_an_active_warp(self):
        """A native warp_to reads warping_to while the game still reads 1x: no
        re-issue over it, and no launch under it (MUTATION: read warp_mode only
        and both halves red)."""
        st, _ = mlib.sdock_decide(_prelaunch(), _pad(8953.4))
        st, actions = mlib.sdock_decide(st, _pad(8970.0, warping_to=T))
        self.assertEqual([], actions)
        self.assertEqual(1, st.launch_hold_issues)
        st, actions = mlib.sdock_decide(st, _pad(T - 1.0, warping_to=T))
        self.assertEqual(mlib.BDOCK_PRELAUNCH, st.phase)
        self.assertEqual([mlib.ACTION_CANCEL_WARP], _kinds(actions))
        st, actions = mlib.sdock_decide(st, _pad(T - 0.5))
        self.assertEqual(mlib.BDOCK_INT_LAUNCH, st.phase)

    def test_a_warp_that_never_takes_is_reissued_a_bounded_number_of_times(self):
        st, _ = mlib.sdock_decide(_prelaunch(), _pad(8953.4))
        st, actions = mlib.sdock_decide(st, _pad(8958.0))        # < 10 s: no re-issue
        self.assertEqual([], actions)
        issued = 1
        ut = 8953.4
        for _ in range(10):
            ut += mlib.SDOCK_LAUNCH_HOLD_REISSUE_S
            st, actions = mlib.sdock_decide(st, _pad(ut))
            issued += len(actions)
        self.assertEqual(mlib.SDOCK_LAUNCH_HOLD_MAX_ISSUES, issued)
        self.assertEqual(mlib.BDOCK_PRELAUNCH, st.phase)
        # Then it waits at 1x and still launches on time.
        st, actions = mlib.sdock_decide(st, _pad(T))
        self.assertEqual(mlib.BDOCK_INT_LAUNCH, st.phase)

    def test_no_hold_and_a_late_first_frame_launch_at_once(self):
        for state, ut in ((mlib.sdock_initial_state(mlib.sdock_params_from_dict({})),
                           8953.388),
                          (_prelaunch(), T + 100.0)):
            with self.subTest(ut=ut):
                st, actions = mlib.sdock_decide(state, _pad(ut))
                self.assertEqual(mlib.BDOCK_INT_LAUNCH, st.phase)
                self.assertEqual([mlib.ACTION_CAPTURE_STATION,
                                  mlib.ACTION_LAUNCH_VESSEL], _kinds(actions))

    def test_the_spec_key_reaches_the_params(self):
        params = mlib.sdock_params_from_dict(_spec_params())
        self.assertEqual(T, params.launch_not_before_ut)


if __name__ == "__main__":
    unittest.main()
