"""Unit cells for BDOCK-2's rendezvous geometry fix (todo
BDOCK2-RENDEZVOUS-PHASING-BURN-DEORBITS-THE-INTERCEPTOR).

ROOT CAUSE. Decompiled MechJeb 2.15.1 MechJebModuleRendezvousAutopilot.Drive tries,
with no node pending and before its Hohmann / phasing branches,
``NextClosestApproachDistance(target, now) < target.semiMajorAxis / 25`` over ONE
period of the vessel's orbit, and if so places DeltaVToMatchVelocities at that
closest approach. BDOCK-2 launches its Interceptor at the fixture's first frame:
the Station is then 15.75 deg ahead at RENDEZVOUS start and the lower Interceptor
closes 15 deg per orbit, so the pair passes ~26 km apart one orbit later, under
the Station's sma/25 = 28.4 km. Matching the Station's velocity 23 km below it
gave 2026-10-13_0309 a 96.7 m/s node and a 97 x 31 km orbit (a = 664 km, the
Station's speed at r = 690 km); the 2026-09-23_1704 PASS flew the same node.

THE CONFIRM FLIGHT 2026-10-14_1754 flew the launch hold (Station 52 deg ahead,
tgtD 615.6 km) and still deorbited: after a 6.3 m/s plane match MechJeb's
"Hohmann" (MechJebLib TwoImpulseTransfer, a free burn-time / transfer-time local
optimizer with no periapsis term) planned 121.1 m/s at ut 10239.1, 190 s ahead,
onto a 101.6 x -13.5 km orbit. Geometry alone cannot make MechJeb's planner safe.

THE FIX. The RENDEZVOUS NODE GUARD never lets a node burn whose post-node
periapsis (kRPC Node.orbit) is under the atmosphere top + 5 km: it switches the
AP off, clears the node, computes the coplanar Hohmann window from the measured
phase, adds its own prograde node there (kRPC Control.add_node, the vis-viva
Hohmann dv), guards and flies it, and turns the AP back on for the arrival.
MechJeb's transfer operation was tried first and, asked for that time on
2026-10-14_1947, returned a node 3,719 s later (73.5 m/s, pe 59 km). The launch hold stays: it keeps the 26 km pass
out of the first orbit.

The GEOMETRY cells propagate the fixture's Station orbit and the 2026-10-13
Interceptor orbit (coplanar two-body; both inclinations are under 0.09 deg) and
reproduce the measured RENDEZVOUS-start distances of both red flights.

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

import mission_runner  # noqa: E402
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


def _spec_params():
    with open(SPEC_PATH, "rb") as fh:
        return tomllib.load(fh)["driver"]["missionParams"]


class GeometryModelTests(unittest.TestCase):
    """The model reproduces the measured red geometry before it judges the fix."""

    def test_the_red_launch_reproduces_the_measured_distance_and_the_26_km_pass(self):
        lead, dist, ca, ca_ut, station = _geometry(0.0)
        self.assertAlmostEqual(15.75, lead, delta=0.05)
        self.assertAlmostEqual(RED_RV_START_DISTANCE, dist, delta=1500.0)
        # MechJeb's branch condition: the pass at the window's end is under sma/25.
        threshold = station["sma"] / 25.0
        self.assertLess(ca, threshold)
        self.assertAlmostEqual(26300.0, ca, delta=1500.0)
        self.assertAlmostEqual(RED_NODE_UT, ca_ut, delta=5.0)

    def test_the_confirm_flight_geometry_is_reproduced(self):
        # 2026-10-14_1754: the hold launched at ut 9176.1 (cancel at "hold
        # reached ut=9176.1"); RENDEZVOUS entry read tgtD=615,629.5 m.
        lead, dist, ca, _, station = _geometry(9176.1 - RED_LAUNCH_UT)
        self.assertAlmostEqual(615629.5, dist, delta=5000.0)
        self.assertAlmostEqual(52.1, lead, delta=0.3)
        # The 26 km velocity-match pass is out of the first orbit, as designed.
        self.assertGreater(ca, 10.0 * station["sma"] / 25.0)

    def test_the_spec_launch_ut_keeps_the_velocity_match_pass_out(self):
        params = _spec_params()
        delay = params["interceptorLaunchNotBeforeUt"] - RED_LAUNCH_UT
        lead, _, ca, _, station = _geometry(delay)
        self.assertGreater(lead, 25.0)
        self.assertLess(lead, 78.0)
        self.assertGreater(ca, 10.0 * station["sma"] / 25.0)

    def test_the_lead_rate_is_station_mean_motion_minus_kerbin_rotation(self):
        lead0 = _geometry(0.0)[0]
        lead100 = _geometry(100.0)[0]
        station = _station_elements()
        rate = (360.0 / (2 * math.pi * math.sqrt(station["sma"] ** 3 / KERBIN_MU))
                - 360.0 / KERBIN_SIDEREAL_DAY)
        self.assertAlmostEqual(0.1634, rate, places=3)
        self.assertAlmostEqual(rate * 100.0, lead100 - lead0, delta=0.1)


# 2026-10-14_1754 RENDEZVOUS, mission stdout + the produced save's Interceptor
# orbit segments: the 6.3 m/s plane match (pe unchanged) and the 121.1 m/s node
# whose orbit was sma 644,059 m ecc 0.0893 (pe -13.5 km).
CONFIRM_PLANE_NODE = dict(ut=9487.012, node_count=1, node_ut=10041.303,
                          node_dv=6.327, node_next_pe=84330.0)
CONFIRM_BAD_NODE = dict(ut=10049.001, node_count=1, node_ut=10239.116,
                        node_dv=121.123, node_next_pe=644059.08 * (1 - 0.089295) - 600000.0)
# BDOCK-1 2026-10-11_1400 (PASS): its Hohmann raise (17.2 m/s, ap -> 117 km,
# pe kept at 84 km) and its arrival velocity match (14.6 m/s onto the Station's
# 117 x 102 km orbit).
BDOCK1_NODES = (dict(ut=699.47, node_count=1, node_ut=7748.234, node_dv=17.219,
                     node_next_pe=84341.0),
                dict(ut=7757.306, node_count=1, node_ut=8672.480, node_dv=14.606,
                     node_next_pe=101700.0))
# The geometry when the guard trips: Station 47.7 deg ahead, the post-plane-match
# Interceptor sma, the fixture Station sma, Kerbin mu.
GUARD_GEOMETRY = dict(target_phase_deg=47.7, orbit_sma=689901.4,
                      target_sma=709675.6, body_mu=KERBIN_MU)


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


class NodeGuardPureTests(unittest.TestCase):

    def test_the_confirm_flights_node_deorbits(self):
        self.assertTrue(mlib.rv_node_deorbits(1, CONFIRM_BAD_NODE["node_next_pe"],
                                              70000.0))

    def test_the_2026_10_13_velocity_match_node_deorbits(self):
        # 96.7 m/s at the 26 km pass: pe 84 -> 31 km.
        self.assertTrue(mlib.rv_node_deorbits(1, 31304.0, 70000.0))

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

    def test_signed_phase(self):
        r1, v1 = (700.0, 0.0, 0.0), (0.0, 2.2, 0.0)
        ahead = (700.0 * math.cos(math.radians(30)), 700.0 * math.sin(math.radians(30)), 0)
        behind = (ahead[0], -ahead[1], 0.0)
        self.assertAlmostEqual(30.0, mlib.signed_phase_deg(r1, v1, ahead), places=6)
        self.assertAlmostEqual(-30.0, mlib.signed_phase_deg(r1, v1, behind), places=6)
        # The same geometry mirrored (a left-handed frame) keeps the sign.
        m = lambda v: (v[0], v[2], v[1])  # noqa: E731
        self.assertAlmostEqual(30.0, mlib.signed_phase_deg(m(r1), m(v1), m(ahead)),
                               places=6)
        self.assertTrue(math.isnan(mlib.signed_phase_deg(r1, (0, 0, 0), ahead)))

    def test_the_hohmann_window(self):
        wait = mlib.hohmann_wait_seconds(47.7, 689901.4, 709675.6, KERBIN_MU)
        n1 = math.degrees(math.sqrt(KERBIN_MU / 689901.4 ** 3))
        n2 = math.degrees(math.sqrt(KERBIN_MU / 709675.6 ** 3))
        t = math.pi * math.sqrt(((689901.4 + 709675.6) / 2) ** 3 / KERBIN_MU)
        lead_req = 180.0 - n2 * t
        self.assertAlmostEqual(3.7, lead_req, delta=0.1)
        self.assertAlmostEqual((47.7 - lead_req) / (n1 - n2), wait, places=6)
        self.assertAlmostEqual(5670.0, wait, delta=60.0)    # ~3 Interceptor orbits
        # Already past the window: wait a full synodic period, never negative.
        late = mlib.hohmann_wait_seconds(lead_req - 1.0, 689901.4, 709675.6, KERBIN_MU)
        self.assertGreater(late, 40000.0)
        # A higher vessel waits for the target to catch up.
        up = mlib.hohmann_wait_seconds(-40.0, 709675.6, 689901.4, KERBIN_MU)
        self.assertGreater(up, 0.0)
        self.assertIsNone(mlib.hohmann_wait_seconds(NAN, 1.0, 2.0, KERBIN_MU))
        self.assertIsNone(mlib.hohmann_wait_seconds(10.0, 7e5, 7e5, KERBIN_MU))


def _prograde_post_burn_pe(sma, ecc, nu_deg, dv, mu=KERBIN_MU):
    """Periapsis altitude after a pure prograde dv at true anomaly ``nu``
    (2D two-body)."""
    nu = math.radians(nu_deg)
    p = sma * (1 - ecc * ecc)
    r = p / (1 + ecc * math.cos(nu))
    vr = math.sqrt(mu / p) * ecc * math.sin(nu)
    vt = math.sqrt(mu / p) * (1 + ecc * math.cos(nu))
    v = math.hypot(vr, vt)
    vr, vt = vr * (v + dv) / v, vt * (v + dv) / v
    h = r * vt
    energy = (vr * vr + vt * vt) / 2 - mu / r
    a = -mu / (2 * energy)
    e = math.sqrt(max(0.0, 1 + 2 * energy * h * h / (mu * mu)))
    return a * (1 - e) - 600000.0


class OwnTransferNodeTests(unittest.TestCase):
    # 2026-10-14_1947 at the guard trip: sma 689,934 -> 709,291 m.
    R1, R2 = 689934.0, 709291.0

    def test_this_runs_transfer_is_a_15_6_m_s_prograde_raise(self):
        dv = mlib.hohmann_burn_dv(self.R1, self.R2, KERBIN_MU)
        self.assertAlmostEqual(15.60, dv, delta=0.02)
        # BDOCK-1's MechJeb Hohmann for nearly the same raise was 17.2 m/s.
        self.assertLess(dv, 20.0)

    def test_the_post_node_periapsis_stays_at_the_park(self):
        # Wherever along the 95 x 84 km park the window falls, a prograde
        # 15.6 m/s burn never lowers pe below ~84 km: the guard passes it.
        dv = mlib.hohmann_burn_dv(self.R1, self.R2, KERBIN_MU)
        pes = [_prograde_post_burn_pe(self.R1, 0.00803, nu, dv)
               for nu in range(0, 360, 15)]
        self.assertGreater(min(pes), 84000.0)
        self.assertFalse(mlib.rv_node_deorbits(1, min(pes), 70000.0))

    def test_a_lower_target_is_a_retrograde_burn(self):
        self.assertLess(mlib.hohmann_burn_dv(self.R2, self.R1, KERBIN_MU), 0.0)
        self.assertIsNone(mlib.hohmann_burn_dv(NAN, self.R2, KERBIN_MU))
        self.assertIsNone(mlib.hohmann_burn_dv(self.R1, self.R2, 0.0))


class _FakeNode(object):
    def __init__(self, ut, prograde):
        self.ut = ut
        self.prograde = prograde


class _FakeControl(object):
    def __init__(self):
        self.nodes = []
        self.calls = []

    def add_node(self, ut, prograde=0.0, normal=0.0, radial=0.0):
        self.calls.append((ut, prograde, normal, radial))
        node = _FakeNode(ut, prograde)
        self.nodes.append(node)
        return node


class _FakeVessel(object):
    def __init__(self, control):
        self.control = control


class _FakeSpaceCenter(object):
    def __init__(self, vessel):
        self.active_vessel = vessel


class _FakeConn(object):
    def __init__(self, space_center):
        self.space_center = space_center


class OwnTransferRunnerSeamTests(unittest.TestCase):
    """The machine's own-transfer action through the real runner perform():
    one kRPC Control.add_node(ut, prograde=dv) call, nothing else."""

    def test_the_guard_action_adds_one_prograde_node_at_the_window(self):
        fake = _FakeControl()
        c = mission_runner.KrpcMissionControl()
        c._conn = _FakeConn(_FakeSpaceCenter(_FakeVessel(fake)))
        st = rv_state(rv_guard_mode=mlib.RV_GUARD_REPLANNING)
        _, actions = mlib.bdock_decide(st, rv_frame(
            ut=10051.0, mj_rendezvous_enabled=False, node_count=0,
            target_phase_deg=51.04, orbit_sma=689934.0, target_sma=709291.0,
            body_mu=KERBIN_MU))
        self.assertEqual([mlib.ACTION_ADD_MANEUVER_NODE], _kinds_of(actions))
        c.perform(actions[0])
        self.assertEqual(1, len(fake.calls))
        ut, prograde, normal, radial = fake.calls[0]
        self.assertAlmostEqual(actions[0].node_ut, ut, places=6)
        self.assertAlmostEqual(15.60, prograde, delta=0.02)
        self.assertEqual((0.0, 0.0), (normal, radial))
        # A stale second add never stacks a second transfer burn.
        c.perform(actions[0])
        self.assertEqual(1, len(fake.calls))


def _kinds_of(actions):
    return [a.kind for a in actions]


class NodeGuardMachineTests(unittest.TestCase):

    def test_bdock1_and_the_plane_match_never_trip(self):
        st = rv_state()
        for node in (CONFIRM_PLANE_NODE,) + BDOCK1_NODES:
            st, actions = mlib.bdock_decide(st, rv_frame(**node))
            self.assertEqual("", st.rv_guard_mode)
            self.assertNotIn(mlib.ACTION_MJ_DISABLE_RENDEZVOUS, _kinds_of(actions))
        self.assertFalse(st.done)

    def test_the_confirm_flights_node_is_replaced_by_the_own_transfer(self):
        st, actions = mlib.bdock_decide(rv_state(), rv_frame(**CONFIRM_BAD_NODE))
        self.assertEqual(mlib.RV_GUARD_REPLANNING, st.rv_guard_mode)
        self.assertEqual([mlib.ACTION_MJ_DISABLE_RENDEZVOUS,
                          mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES], _kinds_of(actions))
        self.assertIn("dv=121.1", st.rv_guard_trip)
        # AP still on / node still there: wait (re-issue every K polls).
        st, actions = mlib.bdock_decide(st, rv_frame(ut=10050.0, **{
            k: v for k, v in CONFIRM_BAD_NODE.items() if k != "ut"}))
        self.assertEqual(mlib.RV_GUARD_REPLANNING, st.rv_guard_mode)
        # AP off, nodes gone: plan the own transfer at the Hohmann window.
        st, actions = mlib.bdock_decide(st, rv_frame(
            ut=10051.0, mj_rendezvous_enabled=False, node_count=0, **GUARD_GEOMETRY))
        self.assertEqual(mlib.RV_GUARD_OWN_PLANNED, st.rv_guard_mode)
        self.assertEqual([mlib.ACTION_ADD_MANEUVER_NODE], _kinds_of(actions))
        wait = mlib.hohmann_wait_seconds(47.7, 689901.4, 709675.6, KERBIN_MU)
        self.assertAlmostEqual(10051.0 + wait, actions[0].node_ut, places=6)
        self.assertAlmostEqual(mlib.hohmann_burn_dv(689901.4, 709675.6, KERBIN_MU),
                               actions[0].value, places=9)
        self.assertEqual("Transfer", actions[0].text)
        self.assertTrue(st.rv_own_transfer_used)
        own_ut = actions[0].node_ut
        # The planned node (a ~11 m/s prograde raise, pe unchanged) is flown.
        st, actions = mlib.bdock_decide(st, rv_frame(
            ut=10052.0, mj_rendezvous_enabled=False, node_count=1, node_ut=own_ut,
            node_dv=11.2, node_next_pe=84300.0))
        self.assertEqual(mlib.RV_GUARD_OWN_BURNING, st.rv_guard_mode)
        self.assertEqual([mlib.ACTION_MJ_EXECUTE_NODES], _kinds_of(actions))
        # Node gone past its UT: the AP takes the arrival.
        st, actions = mlib.bdock_decide(st, rv_frame(
            ut=own_ut + 20.0, mj_rendezvous_enabled=False, node_count=0,
            apoapsis=115000.0))
        self.assertEqual("", st.rv_guard_mode)
        self.assertEqual([mlib.ACTION_MJ_ENABLE_RENDEZVOUS], _kinds_of(actions))
        self.assertFalse(st.done)
        # A SECOND deorbiting node ends the mission before it burns.
        st, actions = mlib.bdock_decide(st, rv_frame(
            ut=own_ut + 30.0, apoapsis=115000.0, node_count=1, node_ut=own_ut + 300,
            node_dv=90.0, node_next_pe=20000.0))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertTrue(st.loss_reason.startswith("rendezvous-plan-deorbits: "))
        self.assertIn(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES, _kinds_of(actions))

    def _replanning(self):
        st, _ = mlib.bdock_decide(rv_state(), rv_frame(**CONFIRM_BAD_NODE))
        return st

    def test_an_own_node_that_deorbits_is_not_flown(self):
        st = self._replanning()
        st, _ = mlib.bdock_decide(st, rv_frame(
            ut=10051.0, mj_rendezvous_enabled=False, node_count=0, **GUARD_GEOMETRY))
        st, actions = mlib.bdock_decide(st, rv_frame(
            ut=10052.0, mj_rendezvous_enabled=False, node_count=1, node_ut=15700.0,
            node_dv=80.0, node_next_pe=40000.0))
        self.assertTrue(st.done)
        self.assertIn("own Hohmann transfer node would deorbit", st.loss_reason)
        self.assertNotIn(mlib.ACTION_MJ_EXECUTE_NODES, _kinds_of(actions))

    def _own_planned(self):
        st = self._replanning()
        st, actions = mlib.bdock_decide(st, rv_frame(
            ut=10051.0, mj_rendezvous_enabled=False, node_count=0, **GUARD_GEOMETRY))
        return st, actions[0].node_ut

    def test_an_own_node_is_flown_only_on_a_readable_periapsis(self):
        st, own_ut = self._own_planned()
        st, actions = mlib.bdock_decide(st, rv_frame(
            ut=10052.0, mj_rendezvous_enabled=False, node_count=1, node_ut=own_ut,
            node_dv=11.2, node_next_pe=NAN))
        self.assertEqual(mlib.RV_GUARD_OWN_PLANNED, st.rv_guard_mode)
        self.assertEqual([], actions)
        st, actions = mlib.bdock_decide(st, rv_frame(
            ut=10053.0, mj_rendezvous_enabled=False, node_count=1, node_ut=own_ut,
            node_dv=11.2, node_next_pe=84300.0))
        self.assertEqual([mlib.ACTION_MJ_EXECUTE_NODES], _kinds_of(actions))

    def test_a_never_readable_own_node_ends_the_mission(self):
        st, own_ut = self._own_planned()
        for k in range(mlib.RV_GUARD_STEP_FRAMES):
            st, actions = mlib.bdock_decide(st, rv_frame(
                ut=10052.0 + k, altitude=94500.0 - 5.0 * k,
                mj_rendezvous_enabled=False, node_count=1, node_ut=own_ut,
                node_dv=11.2, node_next_pe=NAN))
            self.assertNotIn(mlib.ACTION_MJ_EXECUTE_NODES, _kinds_of(actions))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertIn("own transfer node's periapsis was unreadable", st.loss_reason)

    def test_the_rails_hold_carries_the_own_node_wait_with_the_ap_off(self):
        """The hold arms on the own node and is NOT released 'ap-off' (the AP
        is off on purpose); it holds until the arrival tolerance. MUTATION:
        drop the own-burning exemption and the second frame releases."""
        st, own_ut = self._own_planned()
        base = dict(mj_rendezvous_enabled=False, node_count=1, node_ut=own_ut,
                    node_dv=11.2, node_next_pe=84300.0, available_thrust=250000.0,
                    vessel_mass=10000.0, time_to_soi=NAN, warp_mode="NONE",
                    target_distance=500000.0, target_ca_ut=own_ut + 1000.0,
                    target_ca_distance=400000.0)
        st, _ = mlib.bdock_decide(st, rv_frame(ut=10052.0, **base))
        self.assertEqual(mlib.RV_GUARD_OWN_BURNING, st.rv_guard_mode)
        held_actions = []
        for k in range(1, 6):
            st, actions = mlib.bdock_decide(st, rv_frame(ut=10052.0 + k * 0.5, **base))
            held_actions += actions
        self.assertIsNotNone(st.rv_hold_ut)
        self.assertIn(mlib.ACTION_WARP_TO_UT, _kinds_of(held_actions))
        hold_ut = st.rv_hold_ut
        st, actions = mlib.bdock_decide(st, rv_frame(
            ut=10100.0, **dict(base, warp_mode="RAILS", warp_rate=50.0,
                                 warping_to=hold_ut)))
        self.assertIsNotNone(st.rv_hold_ut)
        self.assertNotIn("ap-off", st.rv_warp_decision)
        self.assertEqual([], actions)
        st, actions = mlib.bdock_decide(st, rv_frame(
            ut=hold_ut - 1.0, **dict(base, warp_mode="RAILS", warp_rate=10.0,
                                       warping_to=hold_ut)))
        self.assertIn("arrived", st.rv_warp_decision)

    def test_an_unreadable_phase_ends_the_mission_after_the_step_budget(self):
        st = self._replanning()
        for k in range(mlib.RV_GUARD_STEP_FRAMES):
            st, _ = mlib.bdock_decide(st, rv_frame(
                ut=10051.0 + k, altitude=94500.0 - 5.0 * k,
                mj_rendezvous_enabled=False, node_count=0))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertIn("target phase was unreadable", st.loss_reason)

    def test_an_armed_rails_hold_is_torn_down_on_the_trip(self):
        st = rv_state(rv_hold_ut=10100.0, rv_hold_node_ut=10239.116,
                      rv_hold_warp_cmd=10100.0)
        st, actions = mlib.bdock_decide(st, rv_frame(**CONFIRM_BAD_NODE))
        self.assertEqual(mlib.ACTION_CANCEL_WARP, actions[0].kind)
        self.assertIsNone(st.rv_hold_ut)


def _prelaunch(target=9195.0):
    params = replace(mlib.sdock_params_from_dict({}), launch_not_before_ut=target)
    return mlib.sdock_initial_state(params)


def _pad(ut, warp="NONE", warping_to=NAN):
    return snap(ut=ut, warp_mode=warp, warping_to=warping_to,
                altitude=116184.3, apoapsis=117171.5,
                periapsis=101410.0, situation="ORBITING", body="Kerbin")


def _kinds(actions):
    return [a.kind for a in actions]


class LaunchHoldTests(unittest.TestCase):

    def test_the_first_frame_warps_to_the_launch_ut(self):
        st, actions = mlib.sdock_decide(_prelaunch(), _pad(8953.388))
        self.assertEqual(mlib.BDOCK_PRELAUNCH, st.phase)
        self.assertEqual([mlib.ACTION_WARP_TO_UT], _kinds(actions))
        self.assertEqual(9195.0, actions[0].value)
        self.assertEqual(1, st.launch_hold_issues)

    def test_it_waits_while_warping_and_launches_on_arrival(self):
        st, _ = mlib.sdock_decide(_prelaunch(), _pad(8953.388))
        st, actions = mlib.sdock_decide(st, _pad(9000.0, "RAILS"))
        self.assertEqual([], actions)
        # KSP's WarpTo lands early: inside the tolerance at 1x is a launch.
        st, actions = mlib.sdock_decide(st, _pad(9195.0 - 13.3))
        self.assertEqual(mlib.BDOCK_INT_LAUNCH, st.phase)
        self.assertEqual([mlib.ACTION_CAPTURE_STATION, mlib.ACTION_LAUNCH_VESSEL],
                         _kinds(actions))

    def test_a_warp_still_running_at_the_target_is_cancelled_once(self):
        st, _ = mlib.sdock_decide(_prelaunch(), _pad(8953.388))
        st, actions = mlib.sdock_decide(st, _pad(9190.0, "RAILS"))
        self.assertEqual([mlib.ACTION_CANCEL_WARP], _kinds(actions))
        st, actions = mlib.sdock_decide(st, _pad(9191.0, "RAILS"))
        self.assertEqual([], actions)
        st, actions = mlib.sdock_decide(st, _pad(9191.5))
        self.assertEqual(mlib.BDOCK_INT_LAUNCH, st.phase)

    def test_a_set_warping_to_counts_as_an_active_warp(self):
        """A native warp_to reads warping_to while the game still reads 1x: no
        re-issue over it, and no launch under it (MUTATION: read warp_mode only
        and both halves red)."""
        st, _ = mlib.sdock_decide(_prelaunch(), _pad(8953.4))
        st, actions = mlib.sdock_decide(st, _pad(8970.0, warping_to=9195.0))
        self.assertEqual([], actions)
        self.assertEqual(1, st.launch_hold_issues)
        st, actions = mlib.sdock_decide(st, _pad(9190.0, warping_to=9195.0))
        self.assertEqual(mlib.BDOCK_PRELAUNCH, st.phase)
        self.assertEqual([mlib.ACTION_CANCEL_WARP], _kinds(actions))
        st, actions = mlib.sdock_decide(st, _pad(9191.0))
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
        st, actions = mlib.sdock_decide(st, _pad(9190.0))
        self.assertEqual(mlib.BDOCK_INT_LAUNCH, st.phase)

    def test_no_hold_and_a_late_first_frame_launch_at_once(self):
        for state, ut in ((mlib.sdock_initial_state(mlib.sdock_params_from_dict({})),
                           8953.388),
                          (_prelaunch(), 9300.0)):
            with self.subTest(ut=ut):
                st, actions = mlib.sdock_decide(state, _pad(ut))
                self.assertEqual(mlib.BDOCK_INT_LAUNCH, st.phase)
                self.assertEqual([mlib.ACTION_CAPTURE_STATION,
                                  mlib.ACTION_LAUNCH_VESSEL], _kinds(actions))

    def test_the_spec_key_reaches_the_params(self):
        params = mlib.sdock_params_from_dict(_spec_params())
        self.assertEqual(9195.0, params.launch_not_before_ut)
        self.assertNotIn("rendezvousMinCaDistanceMeters", _spec_params())


if __name__ == "__main__":
    unittest.main()
