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

THE FIX. The LAUNCH HOLD (the spec's interceptorLaunchNotBeforeUt) launches at a
UT that starts the rendezvous with the Station ~55 deg ahead, BDOCK-1's proven
geometry; the RENDEZVOUS GEOMETRY GATE refuses to enable the AP when the next-orbit
closest approach is under the spec's floor.

The GEOMETRY cells propagate the fixture's Station orbit and the red run's
recorded Interceptor orbit (coplanar two-body; both inclinations are under
0.09 deg) and reproduce the measured 193.3 km RENDEZVOUS-start distance and the
26 km pass, then show the spec's launch UT clears both MechJeb's threshold and
the gate's floor.

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

    def test_the_spec_launch_ut_clears_mechjebs_branch_and_the_gate(self):
        params = _spec_params()
        delay = params["interceptorLaunchNotBeforeUt"] - RED_LAUNCH_UT
        lead, _, ca, _, station = _geometry(delay)
        # BDOCK-1's proven start (~58 deg ahead) is inside the band; under ~25 deg
        # the next-orbit pass approaches the threshold, over ~78 deg MechJeb's
        # Hohmann is beyond its five-orbit horizon (it would phase instead).
        self.assertGreater(lead, 25.0)
        self.assertLess(lead, 78.0)
        self.assertGreater(ca, 5.0 * params["rendezvousMinCaDistanceMeters"])
        self.assertGreater(params["rendezvousMinCaDistanceMeters"],
                           station["sma"] / 25.0)

    def test_the_lead_rate_is_station_mean_motion_minus_kerbin_rotation(self):
        lead0 = _geometry(0.0)[0]
        lead100 = _geometry(100.0)[0]
        station = _station_elements()
        rate = (360.0 / (2 * math.pi * math.sqrt(station["sma"] ** 3 / KERBIN_MU))
                - 360.0 / KERBIN_SIDEREAL_DAY)
        self.assertAlmostEqual(0.1634, rate, places=3)
        self.assertAlmostEqual(rate * 100.0, lead100 - lead0, delta=0.1)


class GeometryGateTests(unittest.TestCase):

    def test_the_gate_is_off_unless_armed(self):
        for floor in (0.0, -1.0, NAN):
            with self.subTest(floor=floor):
                self.assertIsNone(mlib.rendezvous_geometry_refusal(
                    26300.0, 11178.2, 9263.2, floor))

    def test_an_unread_closest_approach_fails_open(self):
        self.assertIsNone(mlib.rendezvous_geometry_refusal(NAN, NAN, 9263.2, 60000.0))

    def test_the_red_pass_is_refused_and_named(self):
        reason = mlib.rendezvous_geometry_refusal(26300.0, 11178.2, 9263.2, 60000.0)
        self.assertTrue(reason.startswith("rendezvous-geometry: the target passes "
                                          "26300 m from the Interceptor at "
                                          "ut=11178.2 (1915 s ahead), inside the "
                                          "60000 m rendezvous floor"), reason)

    def test_a_far_pass_and_the_floor_itself_are_allowed(self):
        self.assertIsNone(mlib.rendezvous_geometry_refusal(60000.0, 1.0, 0.0, 60000.0))
        self.assertIsNone(mlib.rendezvous_geometry_refusal(480000.0, 1.0, 0.0, 60000.0))


def _set_target_state(floor):
    params = replace(mlib.BDockParams(), rendezvous_min_ca_distance=floor)
    st = mlib.bdock_initial_state(params)
    return replace(st, phase=mlib.BDOCK_SET_TARGET, phase_entry_ut=9262.7,
                   phases_reached=st.phases_reached + (mlib.BDOCK_SET_TARGET,))


def _target_frame(ca_distance, ca_ut=11178.2):
    return snap(ut=9263.24, target_set=True, target_distance=193278.9,
                target_ca_distance=ca_distance, target_ca_ut=ca_ut,
                apoapsis=95440.3, periapsis=84354.3, altitude=90388.2,
                situation="ORBITING", body="Kerbin")


class SetTargetGateTests(unittest.TestCase):

    def test_an_armed_gate_ends_the_mission_before_any_burn(self):
        st, actions = mlib.bdock_decide(_set_target_state(60000.0), _target_frame(26300.0))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertTrue(st.loss_reason.startswith("rendezvous-geometry: "))
        self.assertEqual(mlib.BDOCK_SET_TARGET, st.phase)
        self.assertEqual([], actions)

    def test_a_clear_geometry_enables_the_ap(self):
        st, actions = mlib.bdock_decide(_set_target_state(60000.0),
                                        _target_frame(480000.0))
        self.assertEqual(mlib.BDOCK_RENDEZVOUS, st.phase)
        self.assertEqual([mlib.ACTION_MJ_ENABLE_RENDEZVOUS], [a.kind for a in actions])

    def test_bdock1_default_is_unchanged(self):
        # BDOCK-1 / CI-9 / SD-1 do not set the key: the gate is off.
        self.assertEqual(0.0, mlib.bdock_params_from_dict({}).rendezvous_min_ca_distance)
        st, actions = mlib.bdock_decide(_set_target_state(0.0), _target_frame(26300.0))
        self.assertEqual(mlib.BDOCK_RENDEZVOUS, st.phase)
        self.assertEqual([mlib.ACTION_MJ_ENABLE_RENDEZVOUS], [a.kind for a in actions])

    def test_an_unread_closest_approach_enables_the_ap(self):
        st, _ = mlib.bdock_decide(_set_target_state(60000.0), _target_frame(NAN, NAN))
        self.assertEqual(mlib.BDOCK_RENDEZVOUS, st.phase)


def _prelaunch(target=9195.0):
    params = replace(mlib.sdock_params_from_dict({}), launch_not_before_ut=target)
    return mlib.sdock_initial_state(params)


def _pad(ut, warp="NONE"):
    return snap(ut=ut, warp_mode=warp, altitude=116184.3, apoapsis=117171.5,
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
        self.assertEqual(60000.0, params.bdock.rendezvous_min_ca_distance)


if __name__ == "__main__":
    unittest.main()
