"""Unit cells for the B-DOCK IN-ORBIT INVARIANT (todo
BDOCK2-RENDEZVOUS-PHASING-BURN-DEORBITS-THE-INTERCEPTOR).

From RENDEZVOUS on, a B-DOCK craft must stay in orbit. BDOCK-2 2026-10-12_1748 and
2026-10-13_0309 executed a 96.7 m/s rendezvous node at ut 11178.2 that left the
Interceptor on a 97 x 31 km orbit, fell into the atmosphere at ut 11494 and crashed
at ut ~12010, ending only on `vessel-lost`. The invariant ends such a flight when
the craft is below the atmosphere top on a sub-atmospheric periapsis, naming the
node that caused it, and the reason token keeps run.py from retrying it.

The frames below are the MEASURED telemetry of those runs (mission stdout
`telemetry` lines) and of the two green runs that must never trip it:
BDOCK-2 2026-09-23_1704 (the same kind of node, pe down to -101 km while the
altitude stayed above 89 km, recovered by MechJeb's close-range intercept burns)
and BDOCK-1 2026-10-11_1400 (pe never below 84 km).

NO krpc, NO KSP, NO network.
"""

import math
import os
import sys
import unittest
from dataclasses import replace

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
for _p in (_MISSIONS, _HERE, os.path.join(_HARNESS, "lib")):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import mlib  # noqa: E402
from test_mlib import snap  # noqa: E402

KERBIN_ATMO = 70000.0
NAN = float("nan")


def frame(ut, ap, pe, alt, node_ut=NAN, node_dv=NAN, nodes=1, **kw):
    base = dict(ut=ut, apoapsis=ap, periapsis=pe, altitude=alt, node_count=nodes,
                node_ut=node_ut, node_dv=node_dv, atmosphere_depth=KERBIN_ATMO,
                body="Kerbin", situation="ORBITING" if pe >= KERBIN_ATMO else "SUB_ORBITAL",
                mj_rendezvous_enabled=True, target_set=True,
                target_distance=26_000.0, target_rel_speed=60.0)
    base.update(kw)
    return snap(**base)


# BDOCK-2 2026-10-13_0309, RENDEZVOUS: the hold released at 1x, the 96.7 m/s node
# at ut 11178.153 burned (pe 84 -> 31 km), MechJeb's follow-up nodes, then the
# atmosphere entry at ut 11494.13 (alt 69,987 m) with pe 26,253 m.
RED_BURN = [
    frame(11170.71, 95302.3, 84363.6, 89868.5, 11178.153, 96.573),
    frame(11176.95, 90535.9, 73255.1, 90098.9, 11178.153, 68.513),
    frame(11180.07, 95271.3, 42183.3, 90076.7, 11178.153, 18.201),
    frame(11183.19, 96978.8, 32804.5, 89919.0, 11178.153, 2.540),
    frame(11189.45, 97261.9, 31304.0, 89549.9, 11189.2, 5.127),
    frame(11308.03, 85784.8, 26240.3, 82887.8, 11760.434, 6.334),
]
RED_ENTRY = [
    frame(11492.05, 85780.954, 26253.203, 70176.188, 11760.434, 6.337),
    frame(11493.09, 85780.954, 26253.203, 70081.825, 11760.434, 6.337),
    frame(11494.13, 85780.947, 26253.203, 69987.259, 11760.434, 6.337,
          situation="FLYING"),
    frame(11495.17, 85780.889, 26253.199, 69892.438, 11760.434, 6.337,
          situation="FLYING"),
    frame(11496.21, 85780.819, 26253.163, 69797.423, 11760.434, 6.337,
          situation="FLYING"),
]

# BDOCK-2 2026-09-23_1704 (PASS): the same kind of node at ut 11177.581
# (85.975 m/s), pe down to -101 km under MechJeb's intercept burns, altitude never
# below 89 km, then back up to a 182 x 102 km orbit.
GREEN_TRANSIENT = [
    frame(11174.70, 95284.1, 84343.6, 89918.6, 11177.581, 85.975),
    frame(11178.82, 92918.7, 48762.5, 90019.7, 11177.581, 23.814),
    frame(11187.02, 95109.1, 33598.8, 89716.8, 11177.581, 0.217),
    frame(11189.06, 95107.9, 33371.9, 89597.4, 11203.381, 316.926),
    frame(11199.44, 89515.8, 701.3, 89185.4, 11203.381, 228.830),
    frame(11216.08, 110090.4, -101024.3, 91437.1, 11203.381, 1.274),
    frame(11259.74, 109211.9, -65939.4, 99209.2, 11259.401, 8.911),
    frame(11284.10, 106520.1, 62393.6, 101951.9, 11283.081, 103.411),
    frame(11288.26, 153762.0, 102021.3, 102049.1, 11283.081, 23.803),
]

# BDOCK-1 2026-10-11_1400 (PASS): the lowest periapsis / altitude it read from
# RENDEZVOUS on (84,335 m / 81,324 m), with the first Hohmann node.
BDOCK1_LOW = [
    frame(7615.72, 95596.2, 84342.0, 84900.7, 7748.234, 17.043),
    frame(7747.82, 97459.6, 84415.3, 84144.3, 7748.234, 15.514),
    frame(8000.00, 116759.3, 84334.8, 81324.0, 8672.480, 14.606),
]


def rv_state(**kw):
    st = mlib.bdock_initial_state(mlib.BDockParams())
    st = replace(st, phase=mlib.BDOCK_RENDEZVOUS, phase_entry_ut=9263.242,
                 phases_reached=st.phases_reached + (mlib.BDOCK_RENDEZVOUS,),
                 rendezvous_ever_enabled=True, rendezvous_min_distance=math.inf)
    return replace(st, **kw)


def drive(state, frames, decide=mlib.bdock_decide):
    for f in frames:
        state, _ = decide(state, f)
        if state.done:
            break
    return state


class FloorAndBreachTests(unittest.TestCase):

    def test_floor_is_the_atmosphere_top_or_the_airless_floor(self):
        self.assertEqual(70000.0, mlib.in_orbit_floor(70000.0))
        self.assertEqual(mlib.IN_ORBIT_AIRLESS_FLOOR_M, mlib.in_orbit_floor(0.0))
        self.assertTrue(math.isnan(mlib.in_orbit_floor(NAN)))
        self.assertTrue(math.isnan(mlib.in_orbit_floor(-1.0)))

    def test_the_measured_atmosphere_entry_breaches(self):
        self.assertTrue(mlib.in_orbit_breached(69987.259, 26253.203, KERBIN_ATMO))

    def test_the_frame_before_entry_does_not(self):
        # pe already 26 km, altitude still 70,082 m: not yet re-entered.
        self.assertFalse(mlib.in_orbit_breached(70081.825, 26253.203, KERBIN_ATMO))

    def test_a_low_altitude_on_an_orbit_above_the_floor_does_not(self):
        # Only both together are a re-entry (MUTATION: drop the periapsis term).
        self.assertFalse(mlib.in_orbit_breached(69000.0, 70500.0, KERBIN_ATMO))

    def test_the_green_transient_never_breaches(self):
        for f in GREEN_TRANSIENT:
            with self.subTest(ut=f.ut):
                self.assertFalse(mlib.in_orbit_breached(
                    f.altitude, f.periapsis, f.atmosphere_depth))

    def test_unread_inputs_fail_open(self):
        for alt, pe, depth in ((NAN, 1000.0, KERBIN_ATMO), (1000.0, NAN, KERBIN_ATMO),
                               (1000.0, 1000.0, NAN)):
            with self.subTest(alt=alt, pe=pe, depth=depth):
                self.assertFalse(mlib.in_orbit_breached(alt, pe, depth))

    def test_an_airless_body_uses_the_terrain_floor(self):
        self.assertTrue(mlib.in_orbit_breached(12000.0, 5000.0, 0.0))
        self.assertFalse(mlib.in_orbit_breached(20000.0, 5000.0, 0.0))
        self.assertFalse(mlib.in_orbit_breached(12000.0, 16000.0, 0.0))


class MachineTests(unittest.TestCase):

    def test_the_red_flight_ends_orbit_lost_at_the_atmosphere_entry(self):
        st = drive(rv_state(), RED_BURN + RED_ENTRY)
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        # Third consecutive breached frame (11494.13, 11495.17, 11496.21).
        self.assertEqual(mlib.IN_ORBIT_BREACH_DEBOUNCE, st.orbit_breach_streak)
        reason = st.loss_reason
        self.assertTrue(reason.startswith("orbit-lost: phase RENDEZVOUS: "), reason)
        self.assertIn("pe=26253 m alt=69797 m are below the 70000 m atmosphere top",
                      reason)
        # The burn that caused it: pe first read below 70 km at ut 11180.07, while
        # the 96.7 m/s node (largest dv read for that node UT) was being flown.
        self.assertIn("periapsis fell below it at ut=11180.1, after the node at "
                      "ut=11178.2 planned dv=96.6 m/s", reason)

    def test_the_red_flight_before_entry_is_still_flying(self):
        st = drive(rv_state(), RED_BURN + RED_ENTRY[:2])
        self.assertFalse(st.done)
        self.assertEqual(11180.07, st.orbit_pe_low_ut)
        self.assertEqual(11178.153, st.orbit_pe_low_node_ut)

    def test_two_breached_frames_then_a_recovery_do_not_end_it(self):
        recover = frame(11497.0, 85780.0, 26253.0, 70100.0, 11760.434, 6.337)
        st = drive(rv_state(), RED_BURN + RED_ENTRY[:4] + [recover])
        self.assertFalse(st.done)
        self.assertEqual(0, st.orbit_breach_streak)

    def test_the_green_bdock2_transient_never_ends_it_and_clears_on_recovery(self):
        st = drive(rv_state(), GREEN_TRANSIENT)
        self.assertFalse(st.done)
        self.assertEqual(0, st.orbit_breach_streak)
        # pe climbed back to 102 km: the low-periapsis evidence is cleared.
        self.assertIsNone(st.orbit_pe_low_ut)

    def test_bdock1_never_ends_it(self):
        st = drive(rv_state(), BDOCK1_LOW)
        self.assertFalse(st.done)
        self.assertIsNone(st.orbit_pe_low_ut)

    def test_every_stay_in_orbit_phase_is_guarded(self):
        for phase in (mlib.BDOCK_RENDEZVOUS, mlib.BDOCK_MATCH_VELOCITY, mlib.BDOCK_DOCK,
                      mlib.BDOCK_TRANSFER, mlib.BDOCK_UNDOCK):
            with self.subTest(phase=phase):
                st = drive(rv_state(phase=phase, phase_entry_ut=11490.0),
                           RED_ENTRY[2:])
                self.assertTrue(st.done)
                self.assertTrue(st.loss_reason.startswith(
                    "orbit-lost: phase %s: " % phase))
                self.assertIn("periapsis fell below it at ut=11494.1, after the node "
                              "at ut=11760.4 planned dv=6.3 m/s", st.loss_reason)

    def test_no_node_read_is_named(self):
        frames = [replace(f, node_count=0, node_ut=NAN, node_dv=NAN)
                  for f in RED_ENTRY[2:]]
        st = drive(rv_state(), frames)
        self.assertTrue(st.done)
        self.assertIn("periapsis fell below it at ut=11494.1 with no maneuver node "
                      "read", st.loss_reason)

    def test_the_ascent_phases_are_not_guarded(self):
        # INT-ASCENT reads pe -598 km on the pad and a sub-atmospheric orbit for
        # most of the climb; INT-CIRCULARIZE starts sub-orbital.
        for phase in (mlib.BDOCK_INT_ASCENT, mlib.BDOCK_INT_CIRCULARIZE,
                      mlib.BDOCK_STATION_ASCENT, mlib.BDOCK_STATION_CIRCULARIZE,
                      mlib.BDOCK_INT_SEPARATE):
            with self.subTest(phase=phase):
                st = rv_state(phase=phase, phase_entry_ut=11490.0)
                for f in RED_ENTRY:
                    st, _ = mlib.bdock_decide(st, f)
                self.assertEqual(0, st.orbit_breach_streak)
                self.assertNotEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)

    def test_vessel_lost_still_wins(self):
        st, _ = mlib.bdock_decide(rv_state(), snap(ut=11500.0, vessel_lost=True))
        self.assertTrue(st.loss_reason.startswith("vessel-lost"))

    def test_bdock2_wrapper_ends_on_the_inner_breach(self):
        params = mlib.sdock_params_from_dict({})
        inner = rv_state()
        st = mlib.SDockState(params=params, inner=inner, phase=inner.phase,
                             phase_entry_ut=inner.phase_entry_ut,
                             phases_reached=inner.phases_reached)
        for f in RED_BURN + RED_ENTRY:
            st, _ = mlib.sdock_decide(st, f)
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertTrue(st.loss_reason.startswith("orbit-lost: "))

    def test_an_armed_rendezvous_hold_is_torn_down_on_the_breach(self):
        st = rv_state(rv_hold_ut=11600.0, rv_hold_node_ut=11760.434,
                      rv_hold_warp_cmd=11600.0)
        for f in RED_ENTRY[:-1]:
            st, _ = mlib.bdock_decide(st, f)
        st, actions = mlib.bdock_decide(st, RED_ENTRY[-1])
        self.assertTrue(st.done)
        self.assertIn(mlib.ACTION_CANCEL_WARP, [a.kind for a in actions])


class ReasonTokenSyncTests(unittest.TestCase):

    def test_the_reason_token_is_a_deterministic_mission_reason(self):
        """run.py skips the retry only when hlib names mlib's token: rename either
        side and a deterministic deorbit is flown twice."""
        self.assertIn(mlib.ORBIT_LOST_REASON_TOKEN, hlib.DETERMINISTIC_MISSION_REASONS)
        reason = mlib.format_orbit_lost_reason(
            mlib.BDOCK_RENDEZVOUS, 26253.0, 69797.0, KERBIN_ATMO, 11180.07,
            11178.153, 96.723)
        self.assertTrue(hlib.is_deterministic_mission_reason(reason))
        self.assertEqual("orbit-lost", hlib.mission_reason_token(reason))

    def test_the_airless_wording(self):
        reason = mlib.format_orbit_lost_reason(
            mlib.BDOCK_DOCK, 5000.0, 12000.0, 0.0, None, None, NAN)
        self.assertIn("below the 15000 m airless floor", reason)
        self.assertIn("periapsis already below it on entry", reason)


if __name__ == "__main__":
    unittest.main()
