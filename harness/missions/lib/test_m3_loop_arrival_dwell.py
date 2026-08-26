"""Unit cells for the m3_loop_arrival_dwell machine (the Tier-2 loop-arrival
map dwell): the MissionConfig arm round trip (tag-gated, fail-closed), the
V1-style OBSERVED camera gate, the span-clock window arithmetic, the
window-stamp evidence, and every named give-up.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP)::

    cd harness && python -m unittest discover -s missions/lib -q
"""

import unittest

import mlib
from mlib import TelemetrySnapshot


def snap(**kw):
    return TelemetrySnapshot(**kw)


def params(**over):
    base = {
        "treeId": "tree-1",
        "departOffsetSeconds": 89448.917,
        "arriveOffsetSeconds": 4474604.451,
        "parkOffsetSeconds": 4506361.14,
        "dwellLeadSeconds": 120.0,
        "dwellHoldSeconds": 60.0,
    }
    base.update(over)
    return mlib.m3_params_from_dict(base)


class M3ParamsTests(unittest.TestCase):
    def test_tree_id_is_required(self):
        with self.assertRaises(ValueError):
            mlib.m3_params_from_dict({"departOffsetSeconds": 1})

    def test_offsets_parse(self):
        p = params()
        self.assertEqual("tree-1", p.tree_id)
        self.assertAlmostEqual(89448.917, p.depart_offset, places=3)


class M3ArmLoopTests(unittest.TestCase):
    def test_first_frame_issues_one_missionconfig_seam_command(self):
        st, acts = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        self.assertTrue(st.arm_issued)
        self.assertEqual(1, len(acts))
        self.assertEqual(mlib.ACTION_PARSEK_SEAM_COMMAND, acts[0].kind)
        self.assertEqual("MissionConfig", acts[0].seam_verb)
        self.assertEqual(mlib.M3_TAG_ARM, acts[0].seam_tag)
        args = dict(acts[0].seam_args)
        self.assertEqual("tree-1", args["tree"])
        self.assertEqual("true", args["loop"])
        self.assertNotIn("intervalSeconds", args)

    def test_interval_param_rides_the_seam_args(self):
        st, acts = mlib.m3_decide(
            mlib.m3_initial_state(params(loopIntervalSeconds=19645697.0)),
            snap(ut=1.0))
        self.assertEqual("19645697.000", dict(acts[0].seam_args)["intervalSeconds"])

    def test_ok_result_reads_the_anchor_and_commands_the_camera(self):
        st, _ = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        st, acts = mlib.m3_decide(st, snap(
            ut=2.0, seam_command_result="OK", seam_command_tag=mlib.M3_TAG_ARM,
            seam_command_payload=(("anchorUt", "9160500.25"), ("unitBuilt", "true"), ("phaseAnchorUt", "24306580.07"),)))
        self.assertEqual(mlib.M3_CAMERA, st.phase)
        self.assertEqual(24306580.07, st.anchor_ut)  # the UNIT phase anchor, never LoopAnchorUT
        kinds = [a.kind for a in acts]
        self.assertEqual([mlib.ACTION_CAMERA_SET_MAP,
                          mlib.ACTION_CAMERA_FOCUS_BODY,
                          mlib.ACTION_CAMERA_SET_POSE], kinds)

    def test_a_stale_tag_never_advances(self):
        st, _ = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        st, acts = mlib.m3_decide(st, snap(
            ut=2.0, seam_command_result="OK", seam_command_tag="othertag",
            seam_command_payload=(("anchorUt", "5.0"), ("unitBuilt", "true"), ("phaseAnchorUt", "5.0"),)))
        self.assertEqual(mlib.M3_ARM_LOOP, st.phase)
        self.assertFalse(st.done)

    def test_error_result_flakes_with_the_decoded_reason(self):
        st, _ = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        st, _ = mlib.m3_decide(st, snap(
            ut=2.0, seam_command_result="ERROR", seam_command_tag=mlib.M3_TAG_ARM,
            seam_command_payload=(("msg", "unknown-tree"),)))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("unknown-tree", st.flake_reason)

    def test_unit_not_built_is_a_named_flake(self):
        # A loop armed whose unit never builds has no span clock; dwelling
        # against the arm-time anchor is exactly the first reading flight
        # empty-map failure, so the machine names it instead.
        st, _ = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        st, _ = mlib.m3_decide(st, snap(
            ut=2.0, seam_command_result="OK", seam_command_tag=mlib.M3_TAG_ARM,
            seam_command_payload=(("anchorUt", "1.0"), ("unitBuilt", "false"),)))
        self.assertTrue(st.done)
        self.assertIn("unit did not build", st.flake_reason)

    def test_unreadable_anchor_is_a_named_flake(self):
        st, _ = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        st, _ = mlib.m3_decide(st, snap(
            ut=2.0, seam_command_result="OK", seam_command_tag=mlib.M3_TAG_ARM,
            seam_command_payload=(("anchorUt", "1.0"), ("unitBuilt", "true"), ("phaseAnchorUt", "bogus"),)))
        self.assertTrue(st.done)
        self.assertIn("phaseAnchorUt", st.flake_reason)


class M3DwellFlowTests(unittest.TestCase):
    def _armed(self):
        st, _ = mlib.m3_decide(mlib.m3_initial_state(params()), snap(ut=1.0))
        st, _ = mlib.m3_decide(st, snap(
            ut=2.0, seam_command_result="OK", seam_command_tag=mlib.M3_TAG_ARM,
            seam_command_payload=(("anchorUt", "9999.0"), ("unitBuilt", "true"), ("phaseAnchorUt", "10000.0"),)))
        return st

    def test_camera_gate_is_observed_not_commanded(self):
        st = self._armed()
        held, acts = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Flight"))
        self.assertEqual(mlib.M3_CAMERA, held.phase)
        self.assertEqual([], acts)
        moved, acts = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Map"))
        self.assertEqual(mlib.M3_WARP_DEPART, moved.phase)
        # The leg is a seam TimeJump issued on the NEXT frame (the legs are
        # epoch shifts since flight 3 measured warp-rate capping at Duna).
        moved, acts = mlib.m3_decide(moved, snap(ut=3.5))
        self.assertEqual(mlib.ACTION_PARSEK_SEAM_COMMAND, acts[-1].kind)
        self.assertEqual("TimeJump", acts[-1].seam_verb)
        self.assertEqual("m3jumpdepart", acts[-1].seam_tag)
        # anchor 10000 + depart offset - lead
        self.assertAlmostEqual(10000.0 + 89448.917 - 120.0,
                               float(dict(acts[-1].seam_args)["ut"]), 3)

    def test_full_dwell_sequence_stamps_ordered_windows(self):
        st = self._armed()
        st, _ = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Map"))
        depart = 10000.0 + 89448.917
        arrive = 10000.0 + 4474604.451
        park = 10000.0 + 4506361.14
        st, _ = mlib.m3_decide(st, snap(ut=3.5))              # jump issued
        st, _ = mlib.m3_decide(st, snap(ut=depart - 119.0))   # jump arrived
        st, _ = mlib.m3_decide(st, snap(ut=depart - 119.0))   # stamp + hold
        st, acts = mlib.m3_decide(st, snap(ut=depart - 50.0))  # hold done
        self.assertEqual(mlib.M3_WARP_ARRIVE, st.phase)
        st, acts = mlib.m3_decide(st, snap(ut=depart - 49.0))  # jump issued
        self.assertEqual("m3jumparrive", acts[-1].seam_tag)
        st, _ = mlib.m3_decide(st, snap(ut=arrive - 119.0))
        st, _ = mlib.m3_decide(st, snap(ut=arrive - 119.0))
        st, acts = mlib.m3_decide(st, snap(ut=arrive - 50.0))
        self.assertEqual(mlib.M3_HOLD_PARK, st.phase)
        st, acts = mlib.m3_decide(st, snap(ut=arrive - 49.0))  # park jump issued
        self.assertEqual("m3jumppark", acts[-1].seam_tag)
        st, _ = mlib.m3_decide(st, snap(ut=park - 119.0))     # stamp + hold
        st, _ = mlib.m3_decide(st, snap(ut=park - 50.0))
        self.assertTrue(st.done)
        rows = mlib.evaluate_m3_assertions((), st.params, st.phases_reached, st)
        self.assertTrue(all(r.met for r in rows),
                        [(r.name, r.met) for r in rows])

    def test_hold_cancels_residual_warp_on_entry(self):
        st = self._armed()
        st, _ = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Map"))
        depart = 10000.0 + 89448.917
        st, _ = mlib.m3_decide(st, snap(ut=depart - 119.0))
        st, acts = mlib.m3_decide(st, snap(ut=depart - 119.0, warp_mode="RAILS"))
        self.assertIn(mlib.ACTION_CANCEL_WARP, [a.kind for a in acts])

    def test_hold_park_survives_a_long_in_phase_warp_wait(self):
        # The first V2 reading flight's machine bug, pinned: HOLD-PARK warps
        # to its window INSIDE the phase, so the stall budget must run from
        # the hold STAMP, never from phase entry (a 31,756 s warp wait made
        # the old phase-entry budget fire with zero hold accumulated).
        st = self._armed()
        st, _ = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Map"))
        depart = 10000.0 + 89448.917
        arrive = 10000.0 + 4474604.451
        park = 10000.0 + 4506361.14
        st, _ = mlib.m3_decide(st, snap(ut=depart - 119.0))
        st, _ = mlib.m3_decide(st, snap(ut=depart - 119.0))
        st, _ = mlib.m3_decide(st, snap(ut=depart - 50.0))
        st, _ = mlib.m3_decide(st, snap(ut=arrive - 119.0))
        st, _ = mlib.m3_decide(st, snap(ut=arrive - 119.0))
        st, _ = mlib.m3_decide(st, snap(ut=arrive - 50.0))
        self.assertEqual(mlib.M3_HOLD_PARK, st.phase)
        # the in-phase jump leg: issue, then land WAY past the 600 s hold
        # budget as measured from phase entry -- the stamp frame must not
        # trip a phase-entry budget (the flight-1 bug, now under the jump
        # shape; the jump lands at target - lead by construction but a late
        # sample is legal)
        st, _ = mlib.m3_decide(st, snap(ut=arrive - 49.0))  # jump issued
        self.assertFalse(st.done)
        st, _ = mlib.m3_decide(st, snap(ut=park + 1.0))     # stamp (late)
        self.assertFalse(st.done,
                         "the stamp frame must not trip a phase-entry budget")
        st, _ = mlib.m3_decide(st, snap(ut=park + 62.0))    # hold done
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)

    def test_missing_window_stamp_fails_the_dwell_row_closed(self):
        p = params()
        st = mlib.m3_initial_state(p)
        rows = mlib.evaluate_m3_assertions((), p, ("ARM-LOOP",), st)
        by = {r.name: r for r in rows}
        self.assertFalse(by["dwelledAllWindows"].met)
        self.assertFalse(by["loopArmed"].met)


if __name__ == "__main__":
    unittest.main()


class M3FlightSceneVariantTests(unittest.TestCase):
    """mapCamera=false (the V3 flight-arrival lane): ARM-LOOP hands straight
    to the first TimeJump leg with ZERO camera actions, and the camera row
    renames itself honestly."""

    def _armed_ok(self, p):
        st, _ = mlib.m3_decide(mlib.m3_initial_state(p), snap(ut=1.0))
        return mlib.m3_decide(st, snap(
            ut=2.0, seam_command_result="OK",
            seam_command_tag=mlib.M3_TAG_ARM,
            seam_command_payload=(("anchorUt", "9160500.25"),
                                  ("unitBuilt", "true"),
                                  ("phaseAnchorUt", "9160400.0"),)))

    def test_no_camera_arm_ok_enters_warp_depart_with_no_actions(self):
        st, acts = self._armed_ok(params(mapCamera=False))
        self.assertEqual(mlib.M3_WARP_DEPART, st.phase)
        self.assertEqual([], acts)
        self.assertEqual(9160400.0, st.anchor_ut)

    def test_map_camera_default_still_enters_camera_phase(self):
        st, acts = self._armed_ok(params())
        self.assertEqual(mlib.M3_CAMERA, st.phase)
        self.assertTrue(acts)

    def test_row_renames_to_flight_scene_direct(self):
        p = params(mapCamera=False)
        st = mlib.m3_initial_state(p)
        rows = mlib.evaluate_m3_assertions(
            (), p,
            phases_reached=(mlib.M3_ARM_LOOP, mlib.M3_WARP_DEPART,
                            mlib.M3_DONE),
            state=st)
        names = [r.name for r in rows]
        self.assertIn("flightSceneDirect", names)
        self.assertNotIn("mapCameraObserved", names)

    def test_default_keeps_the_map_camera_row_name(self):
        p = params()
        rows = mlib.evaluate_m3_assertions(
            (), p, phases_reached=(), state=mlib.m3_initial_state(p))
        self.assertIn("mapCameraObserved", [r.name for r in rows])


# ---------------------------------------------------------------------------
# THE RAILS WARP STAIR (M-A7 RC-WARP), flag-gated and inert by default.
# ---------------------------------------------------------------------------

ANCHOR = 10000.0
DEPART = ANCHOR + 89448.917
ARRIVE = ANCHOR + 4474604.451
PARK = ANCHOR + 4506361.14

# The scripted window sequence both stair classes drive. Frame 0 arms, frame 1
# answers the seam, frame 2 passes the OBSERVED camera gate, and the rest walk
# the three legs and their three windows. Snapshots only - no stair in here.
_LEAD_IN = (
    dict(ut=1.0),
    dict(ut=2.0, seam_command_result="OK", seam_command_tag="m3arm",
         seam_command_payload=(("anchorUt", "9999.0"), ("unitBuilt", "true"),
                               ("phaseAnchorUt", "10000.0"))),
    dict(ut=3.0, camera_mode="Map"),
    dict(ut=3.5),                       # depart TimeJump issued
    dict(ut=DEPART - 119.0),            # arrived -> enter HOLD-DEPART
)


def _act_shape(a):
    return (a.kind, a.seam_verb, a.seam_tag, a.value)


def _drive(p, frames):
    """Replay `frames` through the machine, returning (state, per-frame action
    shapes). One list entry per frame, so a golden comparison is positional."""
    st = mlib.m3_initial_state(p)
    stream = []
    for kw in frames:
        st, acts = mlib.m3_decide(st, snap(**kw))
        stream.append((st.phase, tuple(_act_shape(a) for a in acts)))
    return st, stream


# THE GOLDEN, transcribed from the machine as it stood BEFORE the stair landed
# (commit 48943a79c's `mlib.py`, driven through the identical frame script and
# diffed field-by-field: phases, action kinds, seam verbs, seam tags, values,
# seam args, camera poses, the assertion rows and the terminal verdict all
# compared EQUAL). It is baked here as a literal rather than re-derived from
# git so the cell is a fixture-independent statement that survives the commit
# it was measured against.
_PRE_RC_WARP_STREAM = (
    ("ARM-LOOP", (("parsek_seam_command", "MissionConfig", "m3arm", None),)),
    ("CAMERA", (("camera_set_map", None, None, None),
                ("camera_focus_body", None, None, None),
                ("camera_set_pose", None, None, None))),
    ("WARP-DEPART", ()),
    ("WARP-DEPART", (("parsek_seam_command", "TimeJump", "m3jumpdepart", None),)),
    ("HOLD-DEPART", ()),
    ("HOLD-DEPART", (("cancel_warp", None, None, None),)),
    ("HOLD-DEPART", ()),
    ("WARP-ARRIVE", ()),
    ("WARP-ARRIVE", (("parsek_seam_command", "TimeJump", "m3jumparrive", None),)),
    ("HOLD-ARRIVE", ()),
    ("HOLD-ARRIVE", ()),
    ("HOLD-PARK", ()),
    ("HOLD-PARK", (("parsek_seam_command", "TimeJump", "m3jumppark", None),)),
    ("HOLD-PARK", ()),
    ("DONE", (("cancel_warp", None, None, None),)),
    ("DONE", ()),
)

_FULL_DWELL_FRAMES = _LEAD_IN + (
    dict(ut=DEPART - 119.0, warp_mode="RAILS"),   # stamp + residual-warp cancel
    dict(ut=DEPART - 118.0),
    dict(ut=DEPART - 50.0),                       # hold done -> WARP-ARRIVE
    dict(ut=DEPART - 49.0),                       # arrive TimeJump issued
    dict(ut=ARRIVE - 119.0),
    dict(ut=ARRIVE - 119.0),
    dict(ut=ARRIVE - 50.0),
    dict(ut=ARRIVE - 49.0),                       # park TimeJump issued
    dict(ut=PARK - 119.0),
    dict(ut=PARK - 50.0),
    dict(ut=PARK - 49.0),
)


class M3WarpStairInertnessTests(unittest.TestCase):
    """THE MANDATORY HALF. V2, V3F and V3R are ARMED lanes flying this machine
    today and none of them asks for a stair; the RC-WARP extension is only
    safe if omitting `dwellRampFactors` leaves them byte-identical. These
    cells state that in three independent directions - the action stream, the
    machine state, and the assertion-row surface - because a regression could
    reach any one of them alone."""

    def test_the_empty_stair_replays_the_pre_rc_warp_action_stream(self):
        st, stream = _drive(params(), _FULL_DWELL_FRAMES)
        self.assertEqual(list(_PRE_RC_WARP_STREAM), stream,
                         "the default (empty) stair changed the emitted action "
                         "stream; V2 / V3F / V3R fly this machine and their "
                         "flown shape must not move")
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)

    def test_the_empty_stair_emits_no_rails_warp_action_anywhere(self):
        _, stream = _drive(params(), _FULL_DWELL_FRAMES)
        kinds = [a[0] for _, acts in stream for a in acts]
        self.assertNotIn(mlib.ACTION_SET_RAILS_WARP, kinds)

    def test_the_empty_stair_never_touches_the_ramp_state(self):
        st, _ = _drive(params(), _FULL_DWELL_FRAMES)
        self.assertFalse(st.ramp_active)
        self.assertEqual((), st.ramp_ended_reasons)
        self.assertEqual(0, st.ramp_index)
        self.assertEqual(0, st.ramp_step_frame)
        self.assertEqual(0, st.ramp_frames_used)

    def test_the_empty_stair_keeps_exactly_the_three_original_rows(self):
        st, _ = _drive(params(), _FULL_DWELL_FRAMES)
        rows = mlib.evaluate_m3_assertions((), st.params, st.phases_reached, st)
        self.assertEqual(["loopArmed", "mapCameraObserved", "dwelledAllWindows"],
                         [r.name for r in rows])
        self.assertTrue(all(r.met for r in rows))

    def test_an_absent_key_is_an_empty_stair_not_v1s_own_default(self):
        """V1's `v1_map_dwell_params_from_dict` defaults an ABSENT
        `dwellRampFactors` to its own non-empty ladder. Copying that here
        would have silently put a stair into three armed lanes."""
        self.assertEqual((), params().dwell_ramp_factors)
        self.assertEqual((), params(dwellRampFactors=[]).dwell_ramp_factors)

    def test_an_explicit_empty_list_is_as_inert_as_an_absent_key(self):
        _, absent = _drive(params(), _FULL_DWELL_FRAMES)
        _, explicit = _drive(params(dwellRampFactors=[]), _FULL_DWELL_FRAMES)
        self.assertEqual(absent, explicit)


class M3WarpStairEmissionTests(unittest.TestCase):
    """The armed half: the stair actually climbs, is held, guards itself and
    hands back to the 1x hold. Factors and step frames are small so the cells
    read as arithmetic rather than as a transcript."""

    FACTORS = [2, 3, 4, 5, 4, 3, 2]
    STEP = 3

    def _p(self, **over):
        base = dict(dwellRampFactors=self.FACTORS, dwellRampStepFrames=self.STEP,
                    dwellRampFrames=600)
        base.update(over)
        return params(**base)

    def _at_first_hold_frame(self, p):
        """Drive to HOLD-DEPART and take the stamp frame, returning
        (state, that frame's actions)."""
        st = mlib.m3_initial_state(p)
        for kw in _LEAD_IN:
            st, _ = mlib.m3_decide(st, snap(**kw))
        self.assertEqual(mlib.M3_HOLD_DEPART, st.phase)
        return mlib.m3_decide(st, snap(ut=DEPART - 119.0))

    def test_the_stamp_frame_takes_the_window_ut_and_commands_factor_zero(self):
        """The window-arrival stamp is taken BEFORE the stair moves, so
        `dwelledAllWindows` keeps measuring the leg's arrival rather than the
        stair's end - the stair is added evidence, not a replacement."""
        st, acts = self._at_first_hold_frame(self._p())
        self.assertAlmostEqual(DEPART - 119.0, st.depart_window_ut, places=3)
        self.assertTrue(st.ramp_active)
        self.assertEqual([(mlib.ACTION_SET_RAILS_WARP, 2.0)],
                         [(a.kind, a.value) for a in acts])

    def test_each_factor_is_commanded_in_order_after_step_frames_frames(self):
        p = self._p()
        st, acts = self._at_first_hold_frame(p)
        commanded = [(0, acts[0].value)]
        for i in range(1, 200):
            st, acts = mlib.m3_decide(st, snap(ut=DEPART - 119.0 + i))
            for a in acts:
                if a.kind == mlib.ACTION_SET_RAILS_WARP:
                    commanded.append((i, a.value))
            if not st.ramp_active:
                break
        self.assertEqual([float(f) for f in self.FACTORS],
                         [v for _, v in commanded])
        # Held STEP frames each: the stamp frame plus one step per factor.
        self.assertEqual([i * self.STEP for i in range(len(self.FACTORS))],
                         [i for i, _ in commanded])

    def test_the_stair_cancels_warp_at_its_end_and_marks_itself_completed(self):
        p = self._p()
        st, _ = self._at_first_hold_frame(p)
        last = []
        for i in range(1, 200):
            st, last = mlib.m3_decide(st, snap(ut=DEPART - 119.0 + i))
            if not st.ramp_active:
                break
        self.assertEqual([mlib.ACTION_CANCEL_WARP], [a.kind for a in last])
        self.assertEqual((mlib.M3_RAMP_ENDED_COMPLETED,), st.ramp_ended_reasons)
        self.assertEqual(mlib.M3_HOLD_DEPART, st.phase,
                         "the stair hands back to its OWN window's 1x hold, "
                         "never to the next phase")

    def test_the_1x_hold_remainder_runs_from_the_stairs_end(self):
        """`hold_started_ut` is the 1x ACCUMULATOR and is re-stamped when the
        stair ends. It is deliberately not left at the stair's start: with
        `hold_timeout` measured off it, a legitimate multi-hundred-second
        ladder would otherwise read as a frozen clock and flake."""
        p = self._p()
        st, _ = self._at_first_hold_frame(p)
        end_ut = None
        for i in range(1, 200):
            end_ut = DEPART - 119.0 + i
            st, _ = mlib.m3_decide(st, snap(ut=end_ut))
            if not st.ramp_active:
                break
        self.assertAlmostEqual(end_ut, st.hold_started_ut, places=6)
        # dwell_hold is 60 s in this fixture: still holding at +59, advanced
        # at +60.
        held, _ = mlib.m3_decide(st, snap(ut=end_ut + 59.0))
        self.assertEqual(mlib.M3_HOLD_DEPART, held.phase)
        moved, _ = mlib.m3_decide(st, snap(ut=end_ut + 60.0))
        self.assertEqual(mlib.M3_WARP_ARRIVE, moved.phase)

    def test_the_next_windows_leg_target_is_the_early_exit_guard(self):
        """Mirrors V1's soi-guard: checked BEFORE the stair advances, ends the
        stair with its own honest reason, and cancels warp rather than letting
        a high factor carry the clock into the following dwell."""
        p = self._p()
        st, _ = self._at_first_hold_frame(p)
        guard = ARRIVE - p.dwell_lead
        st, acts = mlib.m3_decide(st, snap(ut=guard))
        self.assertFalse(st.ramp_active)
        self.assertEqual((mlib.M3_RAMP_ENDED_WINDOW_GUARD,), st.ramp_ended_reasons)
        self.assertEqual([mlib.ACTION_CANCEL_WARP], [a.kind for a in acts])
        self.assertEqual(0, st.ramp_index, "the guard fires before the advance")

    def test_the_guard_does_not_fire_one_second_short_of_the_next_leg(self):
        p = self._p()
        st, _ = self._at_first_hold_frame(p)
        st, _ = mlib.m3_decide(st, snap(ut=ARRIVE - p.dwell_lead - 1.0))
        self.assertTrue(st.ramp_active)

    def test_the_last_window_has_no_guard_at_all(self):
        """The parked tail has no next leg to protect, so its guard is NaN and
        can never fire - V1's `_v1_soi_target` shape."""
        p = self._p()
        st = mlib.m3_initial_state(p)
        st = st.__class__(**{**st.__dict__, "phase": mlib.M3_HOLD_PARK,
                             "anchor_ut": ANCHOR})
        self.assertTrue(mlib._m3_ramp_guard_ut(st, None) != mlib._m3_ramp_guard_ut(st, None))

    def test_a_stalled_stair_flakes_by_name_inside_its_frame_budget(self):
        p = self._p(dwellRampStepFrames=1000, dwellRampFrames=4)
        st, _ = self._at_first_hold_frame(p)
        for i in range(1, 20):
            st, _ = mlib.m3_decide(st, snap(ut=DEPART - 119.0 + i * 0.001))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("warp stair never finished", st.flake_reason)

    def test_all_three_windows_run_a_stair_and_the_row_carries_them(self):
        p = self._p()
        st = mlib.m3_initial_state(p)
        for kw in _LEAD_IN:
            st, _ = mlib.m3_decide(st, snap(**kw))
        ut = DEPART - 119.0
        # Walk the whole dwell on a 1 s poll, nudging the clock onto each leg's
        # arrival instant when the machine asks for the jump.
        targets = {mlib.M3_WARP_ARRIVE: ARRIVE - 119.0,
                   mlib.M3_HOLD_PARK: PARK - 119.0}
        jumped = set()
        for _ in range(4000):
            st, acts = mlib.m3_decide(st, snap(ut=ut))
            if st.done:
                break
            for a in acts:
                if a.seam_verb == "TimeJump" and a.seam_tag not in jumped:
                    jumped.add(a.seam_tag)
                    ut = float(dict(a.seam_args)["ut"])
            ut += 1.0
        self.assertTrue(st.done, st.flake_reason)
        self.assertIsNone(st.verdict, st.flake_reason)
        self.assertEqual((mlib.M3_RAMP_ENDED_COMPLETED,) * 3,
                         st.ramp_ended_reasons)
        rows = {r.name: r for r in
                mlib.evaluate_m3_assertions((), p, st.phases_reached, st)}
        self.assertIn("warpStairDriven", rows)
        self.assertTrue(rows["warpStairDriven"].met)
        self.assertTrue(rows["dwelledAllWindows"].met)

    def test_the_row_is_unmet_when_a_window_never_ran_its_stair(self):
        p = self._p()
        st = mlib.m3_initial_state(p)
        rows = {r.name: r for r in
                mlib.evaluate_m3_assertions((), p, (mlib.M3_ARM_LOOP,), st)}
        self.assertFalse(rows["warpStairDriven"].met)
        self.assertEqual([], rows["warpStairDriven"].value)



# ---------------------------------------------------------------------------
# SPAN-CLOCK COMPRESSION (2026-08-25). The V24W reading flight measured an
# EMPTY observation: three windows built as `phaseAnchorUt + RECORDED offset`
# against a unit logging `loiterCuts=1 cutSeconds=11393869
# compressedSpan=7001129/18394999`, so all three offsets (11.47M / 18.33M /
# 18.39M) sat past the 7.00M compressed span in the inter-cycle tail, where the
# clock parks at spanEnd and nothing renders. Zero GhostCreated lines, empty
# manifest, PASS/REPORT.
#
# The V24W numbers are used verbatim throughout so a reader can check the cells
# against the game's own logged line rather than against invented arithmetic.
# ---------------------------------------------------------------------------

# span=[52569234.178200819, 70964232.983117744] -> 18,394,998.8 raw.
V24W_SPAN = 18394998.804916926
V24W_CUT_LENGTH = 11393869.0
V24W_COMPRESSED_SPAN = V24W_SPAN - V24W_CUT_LENGTH        # 7,001,129.8
# The cut is the pre-departure parking loiter, so it lies wholly before the
# departure offset (which is what makes all three compressed offsets land
# inside the span - the arithmetic the fix turns on).
V24W_CUT_START = 30000.0
V24W_DEPART = 11474541.813229546
V24W_ARRIVE = 18329155.146610025
V24W_PARK = 18390509.0
V24W_ANCHOR = 5348663714.300271

V24W_CUT_WIRE = "%r:%r" % (V24W_CUT_START, V24W_CUT_LENGTH)


def v24w_params(**over):
    base = {
        "treeId": "1ccdb19215034ac19f3a8e31697b05ed",
        "departOffsetSeconds": V24W_DEPART,
        "arriveOffsetSeconds": V24W_ARRIVE,
        "parkOffsetSeconds": V24W_PARK,
        "dwellLeadSeconds": 300.0,
        "dwellHoldSeconds": 900.0,
        # The spec's own value, and it MUST exceed dwellHoldSeconds: the stall
        # detector fires between the two, so the 600 s default would flake a
        # 900 s hold by construction.
        "holdTimeoutSeconds": 1800.0,
    }
    base.update(over)
    return mlib.m3_params_from_dict(base)


def arm_payload(cut_count=None, cuts=None, compressed_span=None,
                anchor=V24W_ANCHOR, span_seconds=None):
    """The MissionConfig OK payload, with the cut keys OPTIONAL so a cell can
    reproduce an older DLL (keys absent) as easily as a current one."""
    kv = [("anchorUt", "1.0"), ("unitBuilt", "true"),
          ("phaseAnchorUt", repr(anchor))]
    if cut_count is not None:
        kv.append(("loiterCutCount", str(cut_count)))
        kv.append(("loiterCuts", cuts if cuts is not None else ""))
    if compressed_span is not None:
        kv.append(("compressedSpanSeconds", repr(compressed_span)))
    if span_seconds is not None:
        kv.append(("spanSeconds", repr(span_seconds)))
    return tuple(kv)


def arm(p, payload):
    st, _ = mlib.m3_decide(mlib.m3_initial_state(p), snap(ut=1.0))
    return mlib.m3_decide(st, snap(
        ut=2.0, seam_command_result="OK", seam_command_tag=mlib.M3_TAG_ARM,
        seam_command_payload=payload))


class M3LoiterCutParseTests(unittest.TestCase):
    def test_an_absent_count_key_is_the_older_dll_identity(self):
        """The backward-compatibility contract: a Parsek DLL that never learned
        to publish cuts must leave this machine behaving exactly as before.
        `_b5_seam_payload` spells an absent key as "", so BOTH spellings."""
        for absent in (None, "", "   "):
            cuts, err = mlib.m3_parse_loiter_cuts(absent, None)
            self.assertEqual((), cuts)
            self.assertEqual("", err)

    def test_a_measured_zero_is_also_the_identity(self):
        # duna-direct-recorded's own answer, on nine collected flights:
        # `loiterCuts=0 cutSeconds=0 compressedSpan=4506891/4506891`.
        cuts, err = mlib.m3_parse_loiter_cuts("0", "")
        self.assertEqual((), cuts)
        self.assertEqual("", err)

    def test_one_cut_parses_into_offsets(self):
        cuts, err = mlib.m3_parse_loiter_cuts("1", "30000:11393869")
        self.assertEqual("", err)
        self.assertEqual(1, len(cuts))
        self.assertAlmostEqual(30000.0, cuts[0].start_offset, places=6)
        self.assertAlmostEqual(11393869.0, cuts[0].length, places=6)
        self.assertAlmostEqual(11423869.0, cuts[0].end_offset, places=6)

    def test_several_cuts_parse_in_order(self):
        cuts, err = mlib.m3_parse_loiter_cuts("2", "100:50,400:25")
        self.assertEqual("", err)
        self.assertEqual([(100.0, 50.0), (400.0, 25.0)],
                         [(c.start_offset, c.length) for c in cuts])

    def test_a_nonzero_count_with_an_empty_list_is_refused_not_guessed(self):
        """The C# side is fail-empty rather than fail-partial, so this pair is
        exactly 'there ARE cuts and I could not be told what they are'.
        Compressing anyway would land the dwell somewhere plausible and wrong."""
        cuts, err = mlib.m3_parse_loiter_cuts("1", "")
        self.assertIsNone(cuts)
        self.assertIn("1 loiter cut", err)

    def test_malformed_payloads_are_refused_by_name(self):
        for count, text, needle in (
                ("bogus", "", "loiterCutCount"),
                ("-1", "", "negative"),
                ("1", "30000", "malformed"),
                ("1", "30000:11:22", "malformed"),
                ("1", "a:b", "malformed"),
                ("1", "nan:5", "non-finite"),
                ("1", "10:0", "non-positive"),
                ("1", "10:-5", "non-positive"),
                ("2", "10:5", "lists 1 cut"),
                ("2", "400:25,100:50", "ascending"),
                ("2", "100:500,400:25", "ascending"),
        ):
            cuts, err = mlib.m3_parse_loiter_cuts(count, text)
            self.assertIsNone(cuts, (count, text))
            self.assertIn(needle, err, (count, text))


class M3CompressOffsetTests(unittest.TestCase):
    """`m3_compress_offset` IS `GhostPlaybackLogic.CompressSpanUT`
    (SpanClock.cs:691) in offset space; these cells walk one cut end to end."""

    CUTS = (mlib.M3LoopCut(start_offset=100.0, length=50.0),)

    def test_an_empty_cut_list_is_the_identity(self):
        for offset in (0.0, 120.0, 1e9):
            self.assertEqual((offset, None), mlib.m3_compress_offset(offset, ()))

    def test_before_the_cut_is_unchanged(self):
        self.assertEqual((0.0, None), mlib.m3_compress_offset(0.0, self.CUTS))
        self.assertEqual((99.0, None), mlib.m3_compress_offset(99.0, self.CUTS))

    def test_the_cut_start_is_a_boundary_not_an_inside(self):
        # `t <= cut.StartUT` is the C#'s own `continue`, so the start compresses
        # to itself and is dwellable.
        self.assertEqual((100.0, None), mlib.m3_compress_offset(100.0, self.CUTS))

    def test_inside_the_cut_collapses_and_is_flagged(self):
        for offset in (100.5, 125.0, 149.9):
            compressed, inside = mlib.m3_compress_offset(offset, self.CUTS)
            self.assertAlmostEqual(100.0, compressed, places=6)
            self.assertIsNotNone(inside, offset)
            self.assertEqual(100.0, inside.start_offset)

    def test_the_cut_end_is_the_resume_instant_not_an_inside(self):
        # DecompressSpanUT maps the collapse point back to the cut END, so the
        # end is a real renderable instant.
        self.assertEqual((100.0, None), mlib.m3_compress_offset(150.0, self.CUTS))

    def test_after_the_cut_loses_the_whole_cut(self):
        compressed, inside = mlib.m3_compress_offset(1000.0, self.CUTS)
        self.assertAlmostEqual(950.0, compressed, places=6)
        self.assertIsNone(inside)

    def test_two_cuts_accumulate(self):
        cuts = (mlib.M3LoopCut(100.0, 50.0), mlib.M3LoopCut(400.0, 25.0))
        self.assertAlmostEqual(925.0,
                               mlib.m3_compress_offset(1000.0, cuts)[0], places=6)
        self.assertAlmostEqual(250.0,
                               mlib.m3_compress_offset(300.0, cuts)[0], places=6)

    def test_the_v24w_offsets_land_inside_the_compressed_span(self):
        """The measurement the fix exists for. Uncompressed, all three offsets
        exceed the 7,001,129 s compressed span; compressed, all three fit."""
        cuts = (mlib.M3LoopCut(V24W_CUT_START, V24W_CUT_LENGTH),)
        for offset in (V24W_DEPART, V24W_ARRIVE, V24W_PARK):
            self.assertGreater(offset, V24W_COMPRESSED_SPAN,
                               "the raw offset must be the tail case")
            compressed, inside = mlib.m3_compress_offset(offset, cuts)
            self.assertIsNone(inside)
            self.assertLess(compressed, V24W_COMPRESSED_SPAN,
                            "compressed %r must be dwellable" % offset)
        # And the numbers themselves: depart 11,474,541.8 - 11,393,869 = 80,672.8
        self.assertAlmostEqual(
            V24W_DEPART - V24W_CUT_LENGTH,
            mlib.m3_compress_offset(V24W_DEPART, cuts)[0], places=3)


class M3CutAwareWindowTests(unittest.TestCase):
    def test_the_arm_reads_the_cuts_and_the_compressed_span(self):
        st, _ = arm(v24w_params(), arm_payload(
            cut_count=1, cuts=V24W_CUT_WIRE,
            compressed_span=V24W_COMPRESSED_SPAN))
        self.assertEqual(mlib.M3_CAMERA, st.phase)
        self.assertEqual(1, len(st.loiter_cuts))
        self.assertAlmostEqual(V24W_CUT_LENGTH, st.loiter_cuts[0].length, 3)
        self.assertAlmostEqual(V24W_COMPRESSED_SPAN, st.compressed_span, 3)

    def test_the_depart_leg_jumps_to_the_compressed_window(self):
        """The whole fix, at the one place a recorded offset becomes a live UT.
        Flight 1 jumped to 5,360,137,956.1 (= anchor + the RAW 11,474,541.8 -
        300); the compressed target is 11,393,869 s earlier."""
        st, _ = arm(v24w_params(), arm_payload(
            cut_count=1, cuts=V24W_CUT_WIRE,
            compressed_span=V24W_COMPRESSED_SPAN))
        st, _ = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Map"))
        st, acts = mlib.m3_decide(st, snap(ut=3.5))
        target = float(dict(acts[-1].seam_args)["ut"])
        self.assertAlmostEqual(
            V24W_ANCHOR + (V24W_DEPART - V24W_CUT_LENGTH) - 300.0, target, 1)
        self.assertAlmostEqual(5360137956.1, V24W_ANCHOR + V24W_DEPART - 300.0,
                               1, "the flight-1 target, for the contrast")

    def test_an_older_dll_payload_keeps_the_uncompressed_window(self):
        """BACKWARD COMPATIBILITY, pinned. No cut keys at all -> the machine
        must build exactly the window it built before this change."""
        st, _ = arm(v24w_params(), arm_payload())
        self.assertEqual((), st.loiter_cuts)
        self.assertIsNone(st.compressed_span)
        st, _ = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Map"))
        st, acts = mlib.m3_decide(st, snap(ut=3.5))
        self.assertAlmostEqual(V24W_ANCHOR + V24W_DEPART - 300.0,
                               float(dict(acts[-1].seam_args)["ut"]), 1)

    def test_a_measured_zero_cut_unit_keeps_the_uncompressed_window(self):
        # V2 / V3F / V3R's subject, through the CURRENT payload.
        st, _ = arm(v24w_params(), arm_payload(
            cut_count=0, cuts="", compressed_span=V24W_SPAN))
        st, _ = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Map"))
        st, acts = mlib.m3_decide(st, snap(ut=3.5))
        self.assertAlmostEqual(V24W_ANCHOR + V24W_DEPART - 300.0,
                               float(dict(acts[-1].seam_args)["ut"]), 1)

    def test_an_unmappable_cut_payload_flakes_by_name(self):
        st, _ = arm(v24w_params(), arm_payload(
            cut_count=1, cuts="", compressed_span=V24W_COMPRESSED_SPAN))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("loiter-cut payload", st.flake_reason)

    def test_an_offset_inside_a_cut_is_a_named_spec_authoring_error(self):
        """DECISION, deliberately not a silent clamp: the cut collapses to one
        compressed instant, so the requested recorded moment never plays. A
        clamp would green a run that dwelled somewhere other than the instant
        its own ledger claims."""
        inside = V24W_CUT_START + V24W_CUT_LENGTH / 2.0
        st, _ = arm(v24w_params(departOffsetSeconds=inside), arm_payload(
            cut_count=1, cuts=V24W_CUT_WIRE,
            compressed_span=V24W_COMPRESSED_SPAN))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("depart window offset", st.flake_reason)
        self.assertIn("INSIDE", st.flake_reason)
        self.assertIn("spec authoring error", st.flake_reason)

    def test_each_window_names_itself_when_it_is_the_one_inside_a_cut(self):
        inside = V24W_CUT_START + 1.0
        for key, label in (("departOffsetSeconds", "depart"),
                           ("arriveOffsetSeconds", "arrive"),
                           ("parkOffsetSeconds", "park")):
            st, _ = arm(v24w_params(**{key: inside}), arm_payload(
                cut_count=1, cuts=V24W_CUT_WIRE,
                compressed_span=V24W_COMPRESSED_SPAN))
            self.assertIn("%s window offset" % label, st.flake_reason)

    def test_a_window_past_the_compressed_span_is_refused_before_it_flies(self):
        """The V24W flight-1 failure itself, now caught at the arm instead of
        after three empty dwells: an offset that compresses to at-or-past the
        active span sits in the inter-cycle tail where nothing renders."""
        st, _ = arm(v24w_params(parkOffsetSeconds=V24W_PARK * 2.0), arm_payload(
            cut_count=1, cuts=V24W_CUT_WIRE,
            compressed_span=V24W_COMPRESSED_SPAN))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("park window offset", st.flake_reason)
        self.assertIn("inter-cycle tail", st.flake_reason)

    def test_the_uncompressed_v24w_offsets_would_have_been_refused(self):
        """Stated as its own cell because it is the counterfactual: had this
        guard existed on 2026-08-25, flight 1 would have named the fault in
        seconds instead of burning 455 wall seconds on an empty map."""
        st, _ = arm(v24w_params(), arm_payload(
            cut_count=0, cuts="", compressed_span=V24W_COMPRESSED_SPAN))
        self.assertTrue(st.done)
        self.assertIn("depart window offset", st.flake_reason)
        self.assertIn("inter-cycle tail", st.flake_reason)

    def test_an_older_dll_cannot_run_the_past_the_span_guard(self):
        # No compressedSpanSeconds -> the guard is skipped rather than guessed.
        st, _ = arm(v24w_params(), arm_payload())
        self.assertEqual(mlib.M3_CAMERA, st.phase)
        self.assertIsNone(st.verdict)

    def test_the_armed_row_carries_the_mapping_only_when_cuts_exist(self):
        with_cuts, _ = arm(v24w_params(), arm_payload(
            cut_count=1, cuts=V24W_CUT_WIRE,
            compressed_span=V24W_COMPRESSED_SPAN))
        rows = {r.name: r for r in mlib.evaluate_m3_assertions(
            (), with_cuts.params, with_cuts.phases_reached, with_cuts)}
        self.assertIn("compressedWindowOffsets", rows["loopArmed"].detail)
        self.assertEqual(
            3, len(rows["loopArmed"].detail["compressedWindowOffsets"]))

        none, _ = arm(v24w_params(), arm_payload(cut_count=0, cuts=""))
        rows = {r.name: r for r in mlib.evaluate_m3_assertions(
            (), none.params, none.phases_reached, none)}
        # BYTE-IDENTICAL to the pre-compression detail dict for a lane whose
        # subject does not compress (V2 / V3F / V3R).
        self.assertEqual(
            {"tree": none.params.tree_id, "required": mlib.M3_CAMERA},
            rows["loopArmed"].detail)

    def test_the_whole_v24w_dwell_completes_on_the_compressed_clock(self):
        """End to end: arm, camera, three legs, three holds, DONE - with every
        window inside the compressed span."""
        p = v24w_params()
        cut = V24W_CUT_LENGTH
        st, _ = arm(p, arm_payload(cut_count=1, cuts=V24W_CUT_WIRE,
                                   compressed_span=V24W_COMPRESSED_SPAN))
        st, _ = mlib.m3_decide(st, snap(ut=3.0, camera_mode="Map"))
        depart = V24W_ANCHOR + V24W_DEPART - cut
        arrive = V24W_ANCHOR + V24W_ARRIVE - cut
        park = V24W_ANCHOR + V24W_PARK - cut
        ut = 3.5
        jumped = set()
        for _ in range(4000):
            st, acts = mlib.m3_decide(st, snap(ut=ut))
            if st.done:
                break
            for a in acts:
                if a.seam_verb == "TimeJump" and a.seam_tag not in jumped:
                    jumped.add(a.seam_tag)
                    ut = float(dict(a.seam_args)["ut"])
            ut += 100.0
        self.assertTrue(st.done, st.flake_reason)
        self.assertIsNone(st.verdict, st.flake_reason)
        # Each window opened INSIDE its lead: at or after the leg's jump target
        # (window - dwellLead) and before the window instant itself, which is
        # what "arrive early and watch the approach" means. The exact stamp
        # depends on the poll step, so the cell states the interval, not a
        # frame count.
        for label, window, stamp in (
                ("depart", depart, st.depart_window_ut),
                ("arrive", arrive, st.arrive_window_ut),
                ("park", park, st.park_window_ut)):
            self.assertGreaterEqual(stamp, window - 300.0, label)
            self.assertLess(stamp, window, label)
        rows = {r.name: r for r in mlib.evaluate_m3_assertions(
            (), p, st.phases_reached, st)}
        self.assertTrue(rows["dwelledAllWindows"].met)


class M3RawSpanGuardTests(unittest.TestCase):
    """The COARSEST authoring fault, and it is deliberately its own refusal:
    past the recording's RAW span means the offset is pinned against a
    different recording, which no clock arithmetic fixes. Past the COMPRESSED
    span means the right recording read on the wrong clock. Same symptom on a
    flight (an empty map), different repair."""

    def test_an_offset_past_the_raw_span_names_the_raw_span(self):
        st, _ = arm(v24w_params(parkOffsetSeconds=V24W_SPAN + 1.0), arm_payload(
            cut_count=1, cuts=V24W_CUT_WIRE,
            compressed_span=V24W_COMPRESSED_SPAN, span_seconds=V24W_SPAN))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("park window offset", st.flake_reason)
        self.assertIn("raw span", st.flake_reason)
        self.assertIn("different recording", st.flake_reason)

    def test_the_v24w_offsets_pass_the_raw_span_guard(self):
        """All three ARE inside the raw span - the fixture is right, only the
        clock they were read on was wrong."""
        st, _ = arm(v24w_params(), arm_payload(
            cut_count=1, cuts=V24W_CUT_WIRE,
            compressed_span=V24W_COMPRESSED_SPAN, span_seconds=V24W_SPAN))
        self.assertEqual(mlib.M3_CAMERA, st.phase)
        self.assertIsNone(st.verdict)
        self.assertAlmostEqual(V24W_SPAN, st.span_seconds, 3)

    def test_an_older_dll_cannot_run_the_raw_span_guard(self):
        st, _ = arm(v24w_params(parkOffsetSeconds=V24W_SPAN + 1.0), arm_payload())
        self.assertEqual(mlib.M3_CAMERA, st.phase)
        self.assertIsNone(st.span_seconds)


class M3OptionalSeamFloatTests(unittest.TestCase):
    def test_absent_unreadable_and_non_finite_all_read_as_none(self):
        for value in ("", "bogus", "nan", "inf", "-inf"):
            st = snap(ut=2.0, seam_command_result="OK",
                      seam_command_tag=mlib.M3_TAG_ARM,
                      seam_command_payload=(("spanSeconds", value),))
            self.assertIsNone(
                mlib._m3_optional_seam_float(st, "spanSeconds"), value)

    def test_a_readable_finite_value_reads_through(self):
        st = snap(ut=2.0, seam_command_result="OK",
                  seam_command_tag=mlib.M3_TAG_ARM,
                  seam_command_payload=(("spanSeconds", "18394998.8"),))
        self.assertAlmostEqual(
            18394998.8, mlib._m3_optional_seam_float(st, "spanSeconds"), 3)
