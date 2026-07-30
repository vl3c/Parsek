"""Unit tests for the V1 map-dwell lane (``v1_map_dwell_decide`` + the camera
staging actions + the dwell assertion rows).

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q

Each test names the regression it guards. The rewind tail's semantics are the
R1 machine's (already mutation-verified in test_r1_rewind.py); the cells here
cover the V1-SPECIFIC wiring: the flown-geometry UT stamps, the camera
observed gate, the dwell stair, the SOI-crossing milestones, and the
fail-closed sentinels of the new camera channel.
"""

import math
import unittest
from dataclasses import replace

import mlib
from mlib import Action, TelemetrySnapshot


def snap(**kw):
    return TelemetrySnapshot(**kw)


def seam(tag, result="OK", payload=(), **kw):
    """A snapshot carrying ONE tagged terminal seam response."""
    return snap(seam_command_result=result, seam_command_tag=tag,
                seam_command_payload=tuple(payload), **kw)


V1_MISSION_PARAMS = {
    "targetBodyName": "Mun",
    "captureEnabled": True,
    "rewindPointId": "rp_b9_root",
    "rewindSlot": 1,
    "minUtRegressionSeconds": 5.0,
    "dwellStartLeadSeconds": 60.0,
    "dwellHoldSeconds": 45.0,
    "dwellRampStepFrames": 2,
    "soiDwellLeadSeconds": 300.0,
    "soiDwellTrailSeconds": 300.0,
    "soiDwellCrossFactor": 2,
}


def v1_params(**over):
    d = dict(V1_MISSION_PARAMS)
    d.update(over)
    return mlib.v1_map_dwell_params_from_dict(d)


def fresh(**over):
    return mlib.v1_map_dwell_initial_state(v1_params(**over))


def at_committed(state, flight_start=1000.0, soi_entry=17000.0):
    """Force the nested B11 leg to its CLEAN terminal (reached ORBIT-COMMITTED,
    no verdict) and stamp the two flown-geometry UTs the dwell steers by."""
    flight = replace(
        state.flight, phase=mlib.B5_ORBIT_COMMITTED, done=True,
        phases_reached=state.flight.phases_reached + (mlib.B5_ORBIT_COMMITTED,))
    return replace(state, flight=flight, flight_start_ut=flight_start,
                   soi_entry_ut=soi_entry)


def drive_to_stop(**over):
    """Nested terminal consumed -> parked in STOP with StopRecording emitted."""
    st = at_committed(fresh(**over))
    st, actions = mlib.v1_map_dwell_decide(st, snap(ut=22000.0))
    assert st.phase == mlib.V1_STOP, st.phase
    return st


def drive_to_idle(**over):
    st = drive_to_stop(**over)
    st, _ = mlib.v1_map_dwell_decide(st, seam(mlib.V1_TAG_STOP, ut=22001.0))
    assert st.phase == mlib.V1_RECORDER_IDLE, st.phase
    return st


def drive_to_rewind(pre_ut=22002.0, **over):
    st = drive_to_idle(**over)
    st, _ = mlib.v1_map_dwell_decide(
        st, seam(mlib.r1_state_probe_tag(0),
                 payload=(("recording", "false"),), ut=pre_ut,
                 altitude=90000.0, situation="ORBITING"))
    assert st.phase == mlib.V1_REWIND, st.phase
    return st


def drive_to_verify(**over):
    st = drive_to_rewind(**over)
    st, _ = mlib.v1_map_dwell_decide(st, seam(mlib.V1_TAG_REWIND, ut=22003.0))
    assert st.phase == mlib.V1_VERIFY, st.phase
    return st


def drive_to_camera(post_ut=900.0, **over):
    """VERIFY observes the backward clock -> parked in DWELL-CAMERA with the
    three camera staging actions emitted."""
    st = drive_to_verify(**over)
    st, actions = mlib.v1_map_dwell_decide(
        st, snap(ut=post_ut, altitude=70.0, situation="PRE_LAUNCH"))
    assert st.phase == mlib.V1_CAMERA, st.phase
    return st, actions


class NormalizeCameraModeTests(unittest.TestCase):
    """Mutation: return the raw name -> the machine's 'Map' gate never fires."""

    def test_map_normalizes_pascal(self):
        self.assertEqual(mlib.normalize_camera_mode("map"), "Map")

    def test_snake_case_normalizes(self):
        self.assertEqual(mlib.normalize_camera_mode("free"), "Free")

    def test_empty_and_none_fail_closed(self):
        self.assertEqual(mlib.normalize_camera_mode(""), "")
        self.assertEqual(mlib.normalize_camera_mode(None), "")


class CameraChannelAdditivityTests(unittest.TestCase):
    """Mutation: default camera_mode to a real mode -> every non-opted mission
    could satisfy a camera gate it never read."""

    def test_snapshot_camera_mode_defaults_unread(self):
        self.assertEqual(TelemetrySnapshot().camera_mode, "")

    def test_action_camera_pose_defaults_none(self):
        self.assertIsNone(Action(mlib.ACTION_SET_THROTTLE, 1.0).camera_pose)

    def test_action_with_pose_still_hashable_and_comparable(self):
        a = Action(mlib.ACTION_CAMERA_SET_POSE, camera_pose=(45.0, 0.0, 4e7))
        b = Action(mlib.ACTION_CAMERA_SET_POSE, camera_pose=(45.0, 0.0, 4e7))
        self.assertEqual(a, b)
        self.assertEqual(hash(a), hash(b))


class V1ParamsTests(unittest.TestCase):
    def test_flight_half_parses_through_b5(self):
        p = v1_params(targetBodyName="Mun", captureEnabled=True)
        self.assertEqual(p.b5.target_body, "Mun")
        self.assertTrue(p.b5.capture_enabled)

    def test_ramp_factors_parse_from_list(self):
        p = v1_params(dwellRampFactors=[2, 3, 2])
        self.assertEqual(p.ramp_factors, (2, 3, 2))

    def test_ramp_factors_default_stair(self):
        self.assertEqual(v1_params().ramp_factors, (2, 3, 4, 5, 4, 3, 2))


class V1PreflightGuardTests(unittest.TestCase):
    """Mutation: drop the frame-1 guard -> a spec with no rewind target flies
    the whole ~20-minute B11 profile before discovering the dwell was never
    reachable."""

    def test_unset_rewind_point_flakes_on_frame_one(self):
        st, _ = mlib.v1_map_dwell_decide(fresh(rewindPointId=""), snap(ut=1.0))
        self.assertTrue(st.done)
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("rewind target unresolved", st.flake_reason)

    def test_unset_slot_flakes_on_frame_one(self):
        st, _ = mlib.v1_map_dwell_decide(fresh(rewindSlot=-1), snap(ut=1.0))
        self.assertTrue(st.done)
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)


class V1FlightStampTests(unittest.TestCase):
    """Mutation: drop either stamp -> the dwell has no warp-in floor / no SOI
    target and the whole tail silently mis-steers."""

    def test_flight_start_ut_stamped_on_first_frame(self):
        st, _ = mlib.v1_map_dwell_decide(fresh(), snap(ut=123.5))
        self.assertEqual(st.flight_start_ut, 123.5)

    def test_flight_start_ut_stamps_once(self):
        st, _ = mlib.v1_map_dwell_decide(fresh(), snap(ut=123.5))
        st, _ = mlib.v1_map_dwell_decide(st, snap(ut=200.0))
        self.assertEqual(st.flight_start_ut, 123.5)

    def test_soi_entry_ut_stamped_on_first_target_body_frame(self):
        st, _ = mlib.v1_map_dwell_decide(fresh(), snap(ut=100.0, body="Kerbin"))
        self.assertFalse(math.isfinite(st.soi_entry_ut))
        st, _ = mlib.v1_map_dwell_decide(st, snap(ut=16500.0, body="Mun"))
        self.assertEqual(st.soi_entry_ut, 16500.0)
        st, _ = mlib.v1_map_dwell_decide(st, snap(ut=17000.0, body="Mun"))
        self.assertEqual(st.soi_entry_ut, 16500.0)


class V1FlightDelegationTests(unittest.TestCase):
    def test_flight_flake_propagates_as_named_giveup(self):
        st = fresh()
        flight = replace(st.flight, done=True, verdict=mlib.MISSION_FLAKE,
                         flake_phase="MJ-ASCENT")
        st = replace(st, flight=flight)
        st, _ = mlib.v1_map_dwell_decide(st, snap(ut=100.0))
        self.assertTrue(st.done)
        self.assertIn("delegated B11 flight leg terminated", st.flake_reason)
        self.assertIn("MJ-ASCENT", st.flake_reason)

    def test_clean_commit_without_soi_entry_is_a_named_giveup(self):
        """Fail CLOSED before the rewind: a flight whose SOI entry was never
        observed has no recorded crossing for the dwell to re-visit."""
        st = at_committed(fresh())
        st = replace(st, soi_entry_ut=float("nan"))
        st, _ = mlib.v1_map_dwell_decide(st, snap(ut=22000.0))
        self.assertTrue(st.done)
        self.assertIn("SOI-entry UT was never observed", st.flake_reason)

    def test_clean_commit_opens_stop_with_stop_seam_command(self):
        st = at_committed(fresh())
        st, actions = mlib.v1_map_dwell_decide(st, snap(ut=22000.0))
        self.assertEqual(st.phase, mlib.V1_STOP)
        stops = [a for a in actions
                 if a.kind == mlib.ACTION_PARSEK_SEAM_COMMAND
                 and a.seam_verb == "StopRecording"]
        self.assertEqual(len(stops), 1)
        self.assertEqual(stops[0].seam_tag, mlib.V1_TAG_STOP)

    def test_vessel_loss_suppressed_only_in_rewind(self):
        """The InvokeRewind scene reload legitimately destroys the active
        vessel; anywhere else a lost vessel terminates."""
        st = drive_to_rewind()
        st2, _ = mlib.v1_map_dwell_decide(st, snap(ut=22002.5, vessel_lost=True))
        self.assertFalse(st2.done)
        st3 = drive_to_idle()
        st3, _ = mlib.v1_map_dwell_decide(st3, snap(ut=22001.5, vessel_lost=True))
        self.assertTrue(st3.done)
        self.assertEqual(st3.verdict, mlib.MISSION_ASSERT_FAIL)


class V1RewindTailTests(unittest.TestCase):
    """The R1-verbatim semantics, wired through the V1 tags."""

    def test_no_invoke_rewind_before_an_observed_idle_reading(self):
        st = drive_to_idle()
        st2, actions = mlib.v1_map_dwell_decide(
            st, seam(mlib.r1_state_probe_tag(0),
                     payload=(("recording", "true"),), ut=22001.5))
        self.assertEqual(st2.phase, mlib.V1_RECORDER_IDLE)
        self.assertFalse(any(a.seam_verb == "InvokeRewind" for a in actions
                             if a.kind == mlib.ACTION_PARSEK_SEAM_COMMAND))

    def test_idle_reading_false_emits_invoke_rewind_with_rp_and_slot(self):
        st = drive_to_idle()
        st2, actions = mlib.v1_map_dwell_decide(
            st, seam(mlib.r1_state_probe_tag(0),
                     payload=(("recording", "false"),), ut=22002.0))
        self.assertEqual(st2.phase, mlib.V1_REWIND)
        rewinds = [a for a in actions
                   if a.kind == mlib.ACTION_PARSEK_SEAM_COMMAND
                   and a.seam_verb == "InvokeRewind"]
        self.assertEqual(len(rewinds), 1)
        self.assertIn(("rp", "rp_b9_root"), rewinds[0].seam_args)
        self.assertIn(("slot", "1"), rewinds[0].seam_args)

    def test_unreadable_recording_field_fails_closed(self):
        st = drive_to_idle()
        st2, _ = mlib.v1_map_dwell_decide(
            st, seam(mlib.r1_state_probe_tag(0), payload=(), ut=22001.5))
        self.assertTrue(st2.done)
        self.assertIn("no readable", st2.flake_reason)

    def test_rewind_reject_reason_is_surfaced(self):
        st = drive_to_rewind()
        st2, _ = mlib.v1_map_dwell_decide(
            st, seam(mlib.V1_TAG_REWIND, result="ERROR",
                     payload=(("msg", "recording-active"),), ut=22003.0))
        self.assertTrue(st2.done)
        self.assertIn("recording-active", st2.flake_reason)

    def test_verify_requires_the_observed_backward_clock(self):
        st = drive_to_verify()
        st2, _ = mlib.v1_map_dwell_decide(
            st, snap(ut=22004.0, altitude=90000.0, situation="ORBITING"))
        self.assertEqual(st2.phase, mlib.V1_VERIFY)

    def test_verify_observed_regression_stages_the_camera(self):
        st, actions = drive_to_camera(post_ut=900.0)
        kinds = [a.kind for a in actions]
        self.assertIn(mlib.ACTION_CAMERA_SET_MAP, kinds)
        self.assertIn(mlib.ACTION_CAMERA_FOCUS_BODY, kinds)
        self.assertIn(mlib.ACTION_CAMERA_SET_POSE, kinds)
        focus = [a for a in actions if a.kind == mlib.ACTION_CAMERA_FOCUS_BODY]
        self.assertEqual(focus[0].text, "Kerbin")
        self.assertTrue(math.isfinite(st.ut_regression))
        self.assertGreaterEqual(st.ut_regression, 5.0)


class V1CameraGateTests(unittest.TestCase):
    """Mutation: advance on the COMMAND instead of the readback -> a dwell
    whose map camera never engaged claims the map render surface anyway."""

    def test_unread_camera_mode_never_advances(self):
        st, _ = drive_to_camera()
        st2, _ = mlib.v1_map_dwell_decide(st, snap(ut=901.0))
        self.assertEqual(st2.phase, mlib.V1_CAMERA)

    def test_unread_camera_mode_is_a_named_giveup_at_the_frame_bound(self):
        st, _ = drive_to_camera()
        p = st.params
        for i in range(p.camera_frames + 1):
            st, _ = mlib.v1_map_dwell_decide(st, snap(ut=901.0 + i))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertIn("camera_mode", st.flake_reason)

    def test_observed_map_mode_warps_in_toward_flight_start(self):
        st, _ = drive_to_camera(post_ut=900.0)
        st2, actions = mlib.v1_map_dwell_decide(
            st, snap(ut=901.0, camera_mode="Map"))
        self.assertEqual(st2.phase, mlib.V1_WARP_IN)
        self.assertTrue(st2.camera_map_observed)
        warps = [a for a in actions if a.kind == mlib.ACTION_WARP_TO_UT]
        self.assertEqual(len(warps), 1)
        self.assertEqual(warps[0].value, 1000.0 + 60.0)

    def test_already_past_warp_in_target_holds_immediately(self):
        st, _ = drive_to_camera(post_ut=900.0)
        st = replace(st, flight_start_ut=800.0)
        st2, actions = mlib.v1_map_dwell_decide(
            st, snap(ut=901.0, camera_mode="Map"))
        self.assertEqual(st2.phase, mlib.V1_HOLD)
        self.assertFalse(any(a.kind == mlib.ACTION_WARP_TO_UT for a in actions))


def drive_to_hold(**over):
    st, _ = drive_to_camera(**over)
    st, _ = mlib.v1_map_dwell_decide(st, snap(ut=901.0, camera_mode="Map"))
    assert st.phase == mlib.V1_WARP_IN, st.phase
    st, _ = mlib.v1_map_dwell_decide(st, snap(ut=1060.5, camera_mode="Map"))
    assert st.phase == mlib.V1_HOLD, st.phase
    return st


class V1DwellTests(unittest.TestCase):
    def test_hold_waits_out_the_full_dwell_before_ramping(self):
        st = drive_to_hold()
        st2, _ = mlib.v1_map_dwell_decide(st, snap(ut=1080.0, camera_mode="Map"))
        self.assertEqual(st2.phase, mlib.V1_HOLD)

    def test_hold_elapsed_is_observed_and_opens_the_ramp(self):
        st = drive_to_hold()
        st2, actions = mlib.v1_map_dwell_decide(
            st, snap(ut=1060.5 + 46.0, camera_mode="Map"))
        self.assertEqual(st2.phase, mlib.V1_RAMP)
        self.assertGreaterEqual(st2.hold_elapsed, 45.0)
        rails = [a for a in actions if a.kind == mlib.ACTION_SET_RAILS_WARP]
        self.assertEqual(len(rails), 1)
        self.assertEqual(rails[0].value, 2.0)

    def _ramp_state(self):
        st = drive_to_hold()
        st, _ = mlib.v1_map_dwell_decide(
            st, snap(ut=1106.5, camera_mode="Map"))
        assert st.phase == mlib.V1_RAMP, st.phase
        return st

    def test_ramp_advances_the_stair_every_step_frames(self):
        st = self._ramp_state()
        ut = 1107.0
        seen = []
        for _ in range(40):
            st, actions = mlib.v1_map_dwell_decide(
                st, snap(ut=ut, camera_mode="Map"))
            ut += 10.0
            seen.extend(a.value for a in actions
                        if a.kind == mlib.ACTION_SET_RAILS_WARP)
            if st.phase != mlib.V1_RAMP:
                break
        # dwellRampStepFrames=2 in the test params: the full default stair
        # (2,3,4,5,4,3,2; the first factor was commanded at ramp entry).
        self.assertEqual(seen[:6], [3.0, 4.0, 5.0, 4.0, 3.0, 2.0])
        self.assertEqual(st.phase, mlib.V1_SOI_WARP)
        self.assertEqual(st.ramp_ended_reason, mlib.V1_RAMP_ENDED_COMPLETED)

    def test_ramp_completion_warps_native_toward_soi_lead(self):
        st = self._ramp_state()
        ut = 1107.0
        last_actions = []
        for _ in range(40):
            st, actions = mlib.v1_map_dwell_decide(
                st, snap(ut=ut, camera_mode="Map"))
            ut += 10.0
            last_actions = actions
            if st.phase != mlib.V1_RAMP:
                break
        warps = [a for a in last_actions if a.kind == mlib.ACTION_WARP_TO_UT]
        self.assertEqual(len(warps), 1)
        self.assertEqual(warps[0].value, 17000.0 - 300.0)

    def test_ramp_soi_guard_exits_early_to_the_crossing(self):
        """Mutation: drop the guard -> a 1000x stair overshoots the recorded
        crossing and the headline moment is warped clean over (the exact
        failure MapRenderWarpControl exists to debug manually)."""
        st = self._ramp_state()
        st2, actions = mlib.v1_map_dwell_decide(
            st, snap(ut=16750.0, camera_mode="Map"))
        self.assertEqual(st2.phase, mlib.V1_SOI_CROSS)
        self.assertEqual(st2.ramp_ended_reason, mlib.V1_RAMP_ENDED_SOI_GUARD)
        rails = [a for a in actions if a.kind == mlib.ACTION_SET_RAILS_WARP]
        self.assertEqual(rails[0].value, 2.0)

    def test_soi_warp_arrival_enters_the_crossing_at_the_cross_factor(self):
        st = self._ramp_state()
        st = replace(st, phase=mlib.V1_SOI_WARP, phase_frames=0,
                     phases_reached=st.phases_reached + (mlib.V1_SOI_WARP,))
        st2, actions = mlib.v1_map_dwell_decide(
            st, snap(ut=16700.5, camera_mode="Map"))
        self.assertEqual(st2.phase, mlib.V1_SOI_CROSS)
        rails = [a for a in actions if a.kind == mlib.ACTION_SET_RAILS_WARP]
        self.assertEqual(len(rails), 1)
        self.assertEqual(rails[0].value, 2.0)

    def test_soi_cross_completes_only_past_entry_plus_trail(self):
        st = self._ramp_state()
        st = replace(st, phase=mlib.V1_SOI_CROSS, phase_frames=0,
                     phases_reached=st.phases_reached + (mlib.V1_SOI_CROSS,))
        st2, _ = mlib.v1_map_dwell_decide(
            st, snap(ut=17100.0, camera_mode="Map"))
        self.assertEqual(st2.phase, mlib.V1_SOI_CROSS)
        st3, actions = mlib.v1_map_dwell_decide(
            st2, snap(ut=17301.0, camera_mode="Map"))
        self.assertEqual(st3.phase, mlib.V1_DONE)
        self.assertTrue(st3.done)
        self.assertIsNone(st3.verdict)
        self.assertEqual(st3.soi_cross_exit_ut, 17301.0)
        self.assertTrue(any(a.kind == mlib.ACTION_CANCEL_WARP
                            for a in actions))


class V1AssertionTests(unittest.TestCase):
    def _terminal_state(self):
        st = drive_to_hold()
        st, _ = mlib.v1_map_dwell_decide(st, snap(ut=1106.5, camera_mode="Map"))
        st = replace(st, phase=mlib.V1_SOI_CROSS, phase_frames=0,
                     ramp_ended_reason=mlib.V1_RAMP_ENDED_COMPLETED,
                     phases_reached=st.phases_reached
                     + (mlib.V1_SOI_WARP, mlib.V1_SOI_CROSS))
        st, _ = mlib.v1_map_dwell_decide(st, snap(ut=17301.0, camera_mode="Map"))
        assert st.done and st.verdict is None, (st.phase, st.verdict)
        return st

    def _rows(self, st):
        return {r.name: r for r in mlib.evaluate_v1_map_dwell_assertions(
            [], st.params, state=st)}

    def test_green_path_dwell_rows_all_met(self):
        rows = self._rows(self._terminal_state())
        for name in ("recorderIdleBeforeRewind", "clockRewound",
                     "rewoundBeforeFlightStart", "mapCameraObserved",
                     "dwellHeld1x", "warpRampDriven",
                     "soiEntryUtObservedInFlight", "dwellCrossedRecordedSoiUt"):
            self.assertTrue(rows[name].met, name)

    def test_rows_include_the_delegated_flight_leg(self):
        """Mutation: drop the delegated rows -> the flight half of this
        mission is held to no standard at all."""
        rows = self._rows(self._terminal_state())
        overlap = set(rows) & {
            r.name for r in mlib.evaluate_b5_assertions(
                [], v1_params().b5, phases_reached=(), state=None)}
        self.assertTrue(overlap)

    def test_rewound_after_flight_start_fails_the_scope_row(self):
        """THE VACUITY ROW. A rewind that lands AFTER the flown start leaves
        every flown recording historical (PlaybackScopeTracker), so the whole
        dwell watches an empty map."""
        st = self._terminal_state()
        st = replace(st, post_rewind_ut=st.flight_start_ut + 100.0)
        self.assertFalse(self._rows(st)["rewoundBeforeFlightStart"].met)

    def test_scope_row_accepts_the_tracker_tolerance(self):
        """THE FLIGHT-1 REGRESSION (2026-07-30 run 1917): the rp quicksave is
        authored from the same pre-launch pad state the mission's first frame
        reads, so post-rewind landed 0.22 s AFTER flight_start and a strict
        `<=` failed an otherwise fully green mission. The row must accept the
        PlaybackScopeTracker's own 2.0 s activation tolerance."""
        st = self._terminal_state()
        st = replace(st, post_rewind_ut=st.flight_start_ut + 0.22)
        self.assertTrue(self._rows(st)["rewoundBeforeFlightStart"].met)
        st = replace(st, post_rewind_ut=st.flight_start_ut
                     + mlib.V1_REPLAY_SCOPE_TOLERANCE_SECONDS + 0.01)
        self.assertFalse(self._rows(st)["rewoundBeforeFlightStart"].met)

    def test_unobserved_camera_fails_the_camera_row(self):
        st = self._terminal_state()
        st = replace(st, camera_map_observed=False)
        self.assertFalse(self._rows(st)["mapCameraObserved"].met)

    def test_never_crossing_the_recorded_soi_fails_the_crossing_row(self):
        st = self._terminal_state()
        st = replace(st, soi_cross_exit_ut=float("nan"))
        self.assertFalse(self._rows(st)["dwellCrossedRecordedSoiUt"].met)


if __name__ == "__main__":
    unittest.main()
