"""Unit tests for the rewind-loop lane: the GENERALIZED command-seam path
(``ACTION_PARSEK_SEAM_COMMAND`` + its pure id / line / poll-window helpers) and
the ``r1_rewind_loop`` phase machine.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network)::

    cd harness && python -m unittest discover -s missions/lib -q

Each test names the regression it guards. Every cell in here was MUTATION-VERIFIED
(break the guarantee in mlib, confirm the named test reds); the mutation each cell
covers is stated in its docstring, because a test that passes under its own
mutation is not covering anything.
"""

import math
import unittest
from dataclasses import replace

import mlib
from mlib import Action, TelemetrySnapshot


def snap(**kw):
    return TelemetrySnapshot(**kw)


R1_MISSION_PARAMS = {
    "targetApoapsisMeters": 80000,
    "targetPeriapsisMeters": 80000,
    "apoErrorMeters": 5000,
    "periErrorMeters": 5000,
    "eccentricityMax": 0.02,
    "inclinationErrorDeg": 2.0,
    "ascentTimeoutSeconds": 420,
    "circularizeTimeoutSeconds": 300,
    "rewindPointId": "rp_b9_root",
    "rewindSlot": 1,
    "minUtRegressionSeconds": 5.0,
    "minAltitudeChangeMeters": 1000.0,
}


def r1_params(**over):
    d = dict(R1_MISSION_PARAMS)
    d.update(over)
    return mlib.r1_params_from_dict(d)


def at_orbit(state):
    """Force the nested B2 leg to its CLEAN terminal (reached ORBIT, no verdict),
    the state r1_decide reads to open the rewind cycle."""
    ascent = replace(state.ascent, phase=mlib.B2_ORBIT, done=True,
                     phases_reached=state.ascent.phases_reached + (mlib.B2_ORBIT,))
    return replace(state, ascent=ascent)


def seam(tag, result="OK", payload=(), **kw):
    """A snapshot carrying ONE tagged terminal seam response."""
    return snap(seam_command_result=result, seam_command_tag=tag,
                seam_command_payload=tuple(payload), **kw)


def drive_to_commit(**over):
    """Machine parked in COMMIT with the CommitTree command already emitted."""
    st = at_orbit(mlib.r1_initial_state(r1_params(**over)))
    st, _ = mlib.r1_decide(st, snap(ut=900.0))
    assert st.phase == mlib.R1_COMMIT, st.phase
    return st


def drive_to_stop(**over):
    """COMMIT answered OK -> parked in STOP with StopRecording emitted."""
    st = drive_to_commit(**over)
    st, _ = mlib.r1_decide(st, seam(mlib.R1_TAG_COMMIT, ut=910.0))
    assert st.phase == mlib.R1_STOP, st.phase
    return st


def drive_to_idle(**over):
    """STOP answered OK -> parked in RECORDER-IDLE with probe 0 emitted."""
    st = drive_to_stop(**over)
    st, _ = mlib.r1_decide(st, seam(mlib.R1_TAG_STOP, ut=911.0))
    assert st.phase == mlib.R1_RECORDER_IDLE, st.phase
    return st


def drive_to_rewind(pre_ut=912.0, pre_alt=80500.0, pre_sit="ORBITING", **over):
    """Probe 0 read recording=false -> parked in REWIND with InvokeRewind emitted,
    and the pre-rewind observation stamped at `pre_ut`."""
    st = drive_to_idle(**over)
    st, _ = mlib.r1_decide(
        st, seam(mlib.r1_state_probe_tag(0), payload=(("recording", "false"),),
                 ut=pre_ut, altitude=pre_alt, situation=pre_sit, body="Kerbin"))
    assert st.phase == mlib.R1_REWIND, st.phase
    return st


def drive_to_verify(**over):
    """REWIND answered OK -> parked in VERIFY."""
    st = drive_to_rewind(**over)
    st, _ = mlib.r1_decide(st, seam(mlib.R1_TAG_REWIND, ut=913.0))
    assert st.phase == mlib.R1_VERIFY, st.phase
    return st


def drive_to_relaunch(post_ut=312.0, post_alt=100.0, post_sit="PRE_LAUNCH", **over):
    """The backward clock was OBSERVED -> through the REWOUND waypoint into
    RELAUNCH, with the relaunch actions already emitted."""
    st = drive_to_verify(**over)
    st, _ = mlib.r1_decide(
        st, snap(ut=post_ut, altitude=post_alt, situation=post_sit, body="Kerbin"))
    assert st.phase == mlib.R1_REWOUND, st.phase
    st, _ = mlib.r1_decide(
        st, snap(ut=post_ut + 1.0, altitude=post_alt, situation=post_sit))
    assert st.phase == mlib.R1_RELAUNCH, st.phase
    return st


def drive_to_loop_points(post_alt=100.0, **over):
    """The rewound craft CLIMBED -> parked in LOOP-POINTS with probe 0 emitted."""
    st = drive_to_relaunch(post_alt=post_alt, **over)
    st, _ = mlib.r1_decide(
        st, snap(ut=320.0, altitude=post_alt + 500.0, situation="FLYING"))
    assert st.phase == mlib.R1_LOOP_POINTS, st.phase
    return st


def drive_to_loop_closed(points=42, **over):
    """The second flight was RECORDED -> terminal LOOP-CLOSED."""
    st = drive_to_loop_points(**over)
    st, _ = mlib.r1_decide(
        st, seam(mlib.r1_loop_probe_tag(0), payload=(("points", str(points)),),
                 ut=321.0, altitude=700.0, situation="FLYING"))
    assert st.phase == mlib.R1_LOOP_CLOSED, st.phase
    return st


# ---------------------------------------------------------------------------
# Generalized seam command path: the pure wire helpers.
# ---------------------------------------------------------------------------


class SeamCommandIdTests(unittest.TestCase):
    """The distinct-sub-id contract. MUTATION: make seam_command_id return the
    bare reserved id (`return reserved`) and DistinctPerTag reds -- which is the
    real defect, because the C# seam SKIPS DUPLICATE IDS, so the second command
    would be a silent no-op whose poll expires as a bogus TIMEOUT."""

    def test_sub_id_is_reserved_dot_tag(self):
        self.assertEqual(mlib.seam_command_id("0003", "rewind"), "0003.rewind")

    def test_distinct_per_tag(self):
        a = mlib.seam_command_id("0003", mlib.R1_TAG_COMMIT)
        b = mlib.seam_command_id("0003", mlib.R1_TAG_REWIND)
        self.assertNotEqual(a, b)

    def test_cannot_collide_with_a_runner_step_id(self):
        # hlib.step_id_for_index formats "%04d" (pure digits); a sub-id always
        # carries a '.', so no sub-id can ever equal a step id.
        sub = mlib.seam_command_id("0003", "rewind")
        self.assertIn(".", sub)
        self.assertFalse(sub.isdigit())

    def test_empty_reserved_or_tag_fails_closed(self):
        self.assertEqual(mlib.seam_command_id("", "rewind"), "")
        self.assertEqual(mlib.seam_command_id("0003", ""), "")
        self.assertEqual(mlib.seam_command_id(None, None), "")


class SeamCommandLineTests(unittest.TestCase):
    """The request-channel wire shape. MUTATION: reorder the parts (put cmd before
    id) or drop the args loop and the matching cell reds."""

    def test_no_arg_line_is_byte_identical_to_the_live_proven_commit_line(self):
        # _perform_seam_commit writes exactly "id=%s cmd=CommitTree"; a CommitTree
        # issued through the generalized path must be the same bytes.
        self.assertEqual(
            mlib.format_seam_command_line("0003.commit", "CommitTree", ()),
            "id=0003.commit cmd=CommitTree")

    def test_args_append_in_declaration_order(self):
        self.assertEqual(
            mlib.format_seam_command_line(
                "0003.rewind", "InvokeRewind", (("rp", "rp_b9_root"), ("slot", "1"))),
            "id=0003.rewind cmd=InvokeRewind rp=rp_b9_root slot=1")

    def test_none_args_is_the_no_arg_line(self):
        self.assertEqual(
            mlib.format_seam_command_line("0003.x", "RecordingState", None),
            "id=0003.x cmd=RecordingState")


class SeamCommandPollWindowTests(unittest.TestCase):
    """Two-phase verbs need a longer poll than a one-frame verb. MUTATION: return
    the default for every verb and TwoPhaseVerbsGetALongerWindow reds -- the real
    defect being a manufactured TIMEOUT over a healthy scene reload."""

    def test_default_matches_the_commit_bridge(self):
        self.assertEqual(mlib.seam_command_poll_seconds("CommitTree"), 120.0)
        self.assertEqual(mlib.seam_command_poll_seconds("RecordingState"), 120.0)

    def test_two_phase_verbs_get_a_longer_window(self):
        self.assertGreater(mlib.seam_command_poll_seconds("InvokeRewind"),
                           mlib.seam_command_poll_seconds("CommitTree"))
        self.assertGreater(mlib.seam_command_poll_seconds("AnswerMergeDialog"),
                           mlib.seam_command_poll_seconds("CommitTree"))

    def test_unknown_verb_gets_the_default(self):
        self.assertEqual(mlib.seam_command_poll_seconds("NoSuchVerb"), 120.0)
        self.assertEqual(mlib.seam_command_poll_seconds(""), 120.0)


class SeamCommandAdditivityTests(unittest.TestCase):
    """The additive contract: the generalized path must not move a byte of the
    live-proven CommitTree path or of any existing mission's telemetry."""

    def test_commit_tree_action_constant_is_unchanged(self):
        self.assertEqual(mlib.ACTION_PARSEK_COMMIT_TREE, "parsek_commit_tree")

    def test_seam_command_is_a_distinct_action_kind(self):
        self.assertNotEqual(mlib.ACTION_PARSEK_SEAM_COMMAND,
                            mlib.ACTION_PARSEK_COMMIT_TREE)

    def test_new_snapshot_fields_default_to_unread_sentinels(self):
        s = snap()
        self.assertEqual(s.seam_command_result, "")
        self.assertEqual(s.seam_command_tag, "")
        self.assertEqual(s.seam_command_payload, ())
        # The pre-existing commit channel is untouched.
        self.assertEqual(s.seam_commit_result, "")

    def test_new_action_fields_default_to_none(self):
        a = Action(mlib.ACTION_ACTIVATE_STAGE)
        self.assertIsNone(a.seam_verb)
        self.assertIsNone(a.seam_args)
        self.assertIsNone(a.seam_tag)

    def test_new_fields_are_not_in_the_status_snapshot_block(self):
        # snapshot_dict is an explicit key list; adding a key there would move the
        # status file of EVERY mission. seam_commit_result set the precedent.
        d = mlib.snapshot_dict(snap())
        for key in ("seamCommandResult", "seamCommandTag", "seamCommandPayload"):
            self.assertNotIn(key, d)

    def test_new_state_fields_are_not_in_the_machine_state_line(self):
        # format_machine_state emits EVERY MACHINE_STATE_FIELDS key unconditionally
        # (absent renders '-'), so adding one moves every mission's machine line.
        keys = {key for _attr, key in mlib.MACHINE_STATE_FIELDS}
        for key in ("utRegression", "rewindResult", "preRewindUt"):
            self.assertNotIn(key, keys)

    def test_action_still_hashable_and_comparable(self):
        # Action is frozen/hashable by contract (the `crew` / `landing_config`
        # tuple precedent); seam_args must be a tuple, never a dict.
        a = Action(mlib.ACTION_PARSEK_SEAM_COMMAND, seam_verb="InvokeRewind",
                   seam_args=(("rp", "x"),), seam_tag="rewind")
        b = Action(mlib.ACTION_PARSEK_SEAM_COMMAND, seam_verb="InvokeRewind",
                   seam_args=(("rp", "x"),), seam_tag="rewind")
        self.assertEqual(a, b)
        self.assertEqual(len({a, b}), 1)


# ---------------------------------------------------------------------------
# R1 machine: the pre-flight fail-closed guard.
# ---------------------------------------------------------------------------


class R1PreflightGuardTests(unittest.TestCase):
    """MUTATION: delete the frame-1 rewind-target check in r1_decide and
    UnsetRewindPointFlakesOnFrameOne / UnsetSlotFlakesOnFrameOne red -- the real
    defect being a full ascent flown to reach a leg that InvokeRewind could only
    ever answer REJECTED unknown-rp."""

    def test_unset_rewind_point_flakes_on_frame_one(self):
        st = mlib.r1_initial_state(r1_params(rewindPointId=""))
        out, actions = mlib.r1_decide(st, snap(ut=100.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("rewind target unresolved", out.flake_reason)
        self.assertEqual(actions, [])

    def test_unset_slot_flakes_on_frame_one(self):
        st = mlib.r1_initial_state(r1_params(rewindSlot=-1))
        out, _ = mlib.r1_decide(st, snap(ut=100.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)

    def test_resolved_target_does_not_trip_the_guard(self):
        st = mlib.r1_initial_state(r1_params())
        out, _ = mlib.r1_decide(st, snap(ut=100.0))
        self.assertFalse(out.done)
        self.assertEqual(out.phase, mlib.R1_ASCENT)


# ---------------------------------------------------------------------------
# R1 machine: the delegated ascent leg.
# ---------------------------------------------------------------------------


class R1AscentDelegationTests(unittest.TestCase):
    """The ascent must be the LIVE-PROVEN B2 machine, not a re-implementation.
    MUTATION: replace the b2_decide call with a hand-rolled transition and
    DelegatesEveryAscentFrameToB2 reds."""

    def test_delegates_every_ascent_frame_to_b2(self):
        st = mlib.r1_initial_state(r1_params())
        # B2's PRELAUNCH emits the four launch actions; R1 must pass them through
        # verbatim on the same frame.
        _out, actions = mlib.r1_decide(st, snap(ut=100.0))
        kinds = [a.kind for a in actions]
        self.assertEqual(
            kinds,
            [mlib.ACTION_MJ_SET_TARGET_APOAPSIS, mlib.ACTION_MJ_ENABLE_AUTOSTAGE,
             mlib.ACTION_MJ_ENGAGE_ASCENT, mlib.ACTION_ACTIVATE_STAGE])

    def test_nested_ascent_state_advances_and_is_carried(self):
        st = mlib.r1_initial_state(r1_params())
        out, _ = mlib.r1_decide(st, snap(ut=100.0))
        self.assertEqual(out.ascent.phase, mlib.B2_MJ_ASCENT)
        self.assertEqual(out.phase, mlib.R1_ASCENT)

    def test_ascent_flake_propagates_as_a_named_r1_giveup(self):
        st = mlib.r1_initial_state(r1_params())
        st = replace(st, ascent=replace(st.ascent, phase=mlib.B2_MJ_ASCENT,
                                        phase_entry_ut=100.0))
        # Blow the ascent budget: b2 flakes, R1 must name WHICH ascent phase died.
        out, _ = mlib.r1_decide(st, snap(ut=100.0 + 500.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn(mlib.B2_MJ_ASCENT, out.flake_reason)
        self.assertIn("rewind cycle was never reached", out.flake_reason)

    def test_ascent_vessel_loss_propagates_as_assert_fail_not_flake(self):
        st = mlib.r1_initial_state(r1_params())
        st = replace(st, ascent=replace(st.ascent, phase=mlib.B2_MJ_ASCENT))
        out, _ = mlib.r1_decide(st, snap(ut=200.0, vessel_lost=True))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost", out.loss_reason)

    def test_clean_orbit_opens_the_cycle_with_a_commit_tree_seam_command(self):
        st = at_orbit(mlib.r1_initial_state(r1_params()))
        out, actions = mlib.r1_decide(st, snap(ut=900.0))
        self.assertEqual(out.phase, mlib.R1_COMMIT)
        seam = [a for a in actions if a.kind == mlib.ACTION_PARSEK_SEAM_COMMAND]
        self.assertEqual(len(seam), 1)
        self.assertEqual(seam[0].seam_verb, "CommitTree")
        self.assertEqual(seam[0].seam_tag, mlib.R1_TAG_COMMIT)

    def test_done_without_orbit_is_a_named_giveup_not_a_silent_advance(self):
        """MUTATION: drop the `B2_ORBIT not in phases_reached` check and this reds --
        a nested machine that reports done with a clean verdict but never orbited
        would otherwise walk straight into COMMIT."""
        st = mlib.r1_initial_state(r1_params())
        st = replace(st, ascent=replace(st.ascent, done=True))  # no ORBIT reached
        out, _ = mlib.r1_decide(st, snap(ut=900.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("never reached", out.flake_reason)


# ---------------------------------------------------------------------------
# R1 machine: COMMIT / REWIND, and the stale-result fail-open.
# ---------------------------------------------------------------------------


class R1SeamPhaseTests(unittest.TestCase):

    def test_commit_ok_goes_to_stop_not_straight_to_rewind(self):
        """THE FLIGHT-1 REGRESSION. Flight 1 (2026-07-26) went COMMIT -> REWIND and
        Parsek answered `reject cmd=InvokeRewind reason=recording-active`: the
        commit-then-keep-flying promotion re-arms a recorder ~14 ms after the
        commit returns OK.

        MUTATION: make the COMMIT OK branch enter R1_REWIND with
        `_r1_rewind_action(...)` (the pre-fix machine) and this reds."""
        st = drive_to_commit()
        out, actions = mlib.r1_decide(st, seam(mlib.R1_TAG_COMMIT, ut=910.0))
        self.assertEqual(out.phase, mlib.R1_STOP)
        self.assertEqual(out.commit_result, "OK")
        self.assertEqual([(a.seam_verb, a.seam_tag) for a in actions],
                         [("StopRecording", mlib.R1_TAG_STOP)])

    def test_no_invoke_rewind_is_ever_emitted_before_a_recorder_idle_reading(self):
        """The precondition as a whole-run property rather than a per-branch one:
        walk the chain and assert InvokeRewind is emitted ONLY after a
        RecordingState reply actually read recording=false.

        MUTATION: move the rewind action to the STOP OK branch (an order-only fix
        with no observation) and this reds."""
        st = drive_to_idle()
        emitted = []

        # Probe 0 says the recorder is STILL LIVE -> must NOT command the rewind.
        st, actions = mlib.r1_decide(
            st, seam(mlib.r1_state_probe_tag(0), payload=(("recording", "true"),),
                     ut=912.0))
        emitted += [a.seam_verb for a in actions]
        self.assertNotIn("InvokeRewind", emitted)
        self.assertEqual(st.phase, mlib.R1_RECORDER_IDLE)
        self.assertFalse(st.recorder_idle_observed)

        # Probe 1 reads idle -> now, and only now, the rewind is commanded.
        st, actions = mlib.r1_decide(
            st, seam(mlib.r1_state_probe_tag(1), payload=(("recording", "false"),),
                     ut=913.0))
        emitted += [a.seam_verb for a in actions]
        self.assertEqual(emitted.count("InvokeRewind"), 1)
        self.assertTrue(st.recorder_idle_observed)
        self.assertEqual(st.phase, mlib.R1_REWIND)

    def test_each_recorder_state_probe_gets_a_distinct_tag(self):
        """A reused tag is a reused wire id, and the C# seam SKIPS DUPLICATE IDS -
        every probe after the first would be silently dropped and the poll would
        expire on a reply that was never coming.

        MUTATION: make `r1_state_probe_tag` return a constant and this reds."""
        st = drive_to_idle()
        tags = []
        for i in range(3):
            st, actions = mlib.r1_decide(
                st, seam(mlib.r1_state_probe_tag(i),
                         payload=(("recording", "true"),), ut=912.0 + i))
            tags += [a.seam_tag for a in actions]
        self.assertEqual(tags, ["state1", "state2", "state3"])
        self.assertEqual(len(set(tags)), len(tags))

    def test_a_recorder_that_never_goes_idle_is_a_named_giveup(self):
        """MUTATION: delete the `recording == "true"` branch's frame bound and this
        HANGS. The give-up must NAME the promotion and the gate, not just time
        out."""
        st = drive_to_idle(idleFrames=3)
        for i in range(12):
            st, _ = mlib.r1_decide(
                st, seam(mlib.r1_state_probe_tag(st.state_probe),
                         payload=(("recording", "true"),), ut=912.0 + i))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("still read recording=true", st.flake_reason)
        self.assertIn("recording-active", st.flake_reason)

    def test_an_unreadable_recording_field_fails_closed(self):
        """An OK reply whose payload has no `recording` field must NOT be treated
        as idle. MUTATION: fall through to the rewind on an unreadable field and
        this reds."""
        st = drive_to_idle()
        out, actions = mlib.r1_decide(
            st, seam(mlib.r1_state_probe_tag(0), payload=(("tree", "t1"),), ut=912.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("no readable `recording` field", out.flake_reason)
        self.assertEqual(actions, [])

    def test_stop_seam_error_is_a_named_giveup_that_names_the_gate(self):
        st = drive_to_stop()
        out, _ = mlib.r1_decide(st, seam(mlib.R1_TAG_STOP, result="ERROR", ut=911.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("StopRecording seam command returned ERROR", out.flake_reason)
        self.assertIn("recording-active", out.flake_reason)

    def test_stop_silence_is_frame_bounded(self):
        st = drive_to_stop(stopFrames=3)
        for _ in range(8):
            st, _ = mlib.r1_decide(st, snap(ut=911.0))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertIn("StopRecording seam command never answered within 3 frames",
                      st.flake_reason)

    def test_rewind_stamps_the_pre_rewind_observation_at_the_command_frame(self):
        """The stamp is taken on the frame the rewind is COMMANDED (the idle
        reading), not back at the commit, so VERIFY compares against the tightest
        possible 'before'."""
        st = drive_to_idle()
        out, actions = mlib.r1_decide(
            st, seam(mlib.r1_state_probe_tag(0), payload=(("recording", "false"),),
                     ut=912.0, altitude=80500.0, situation="ORBITING", body="Kerbin"))
        self.assertEqual(out.pre_rewind_ut, 912.0)
        self.assertEqual(out.pre_rewind_altitude, 80500.0)
        self.assertEqual(out.pre_rewind_situation, "ORBITING")
        self.assertEqual(len(actions), 1)
        self.assertEqual(actions[0].seam_verb, "InvokeRewind")
        self.assertEqual(actions[0].seam_args, (("rp", "rp_b9_root"), ("slot", "1")))

    def test_commit_error_is_a_named_giveup(self):
        st = drive_to_commit()
        out, _ = mlib.r1_decide(st, seam(mlib.R1_TAG_COMMIT, result="ERROR", ut=910.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("tree-commit seam command returned ERROR", out.flake_reason)
        self.assertIn("no committed state to rewind FROM", out.flake_reason)

    def test_commit_timeout_is_a_distinctly_named_giveup(self):
        st = drive_to_commit()
        out, _ = mlib.r1_decide(st, seam(mlib.R1_TAG_COMMIT, result="TIMEOUT", ut=910.0))
        self.assertIn("returned TIMEOUT", out.flake_reason)

    def test_commit_silence_is_frame_bounded_with_its_own_name(self):
        st = drive_to_commit(commitFrames=3)
        for _ in range(6):
            st, _ = mlib.r1_decide(st, snap(ut=910.0))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertIn("never answered within 3 frames", st.flake_reason)

    def test_a_stale_commit_ok_cannot_satisfy_the_stop_phase(self):
        """THE stale-result fail-open. MUTATION: drop the tag check in
        _r1_seam_result and this reds -- STOP would advance on the COMMIT
        command's OK, reporting a stop StopRecording never performed."""
        st = drive_to_stop()
        out, _ = mlib.r1_decide(st, seam(mlib.R1_TAG_COMMIT, ut=911.0))
        self.assertEqual(out.phase, mlib.R1_STOP)
        self.assertEqual(out.stop_result, "")
        self.assertFalse(out.done)

    def test_a_stale_stop_ok_cannot_satisfy_the_recorder_idle_phase(self):
        st = drive_to_idle()
        out, _ = mlib.r1_decide(st, seam(mlib.R1_TAG_STOP, ut=912.0))
        self.assertEqual(out.phase, mlib.R1_RECORDER_IDLE)
        self.assertFalse(out.recorder_idle_observed)
        self.assertFalse(out.done)

    def test_rewind_ok_enters_verify_but_is_not_itself_the_terminal(self):
        """MUTATION: make R1_REWIND's OK branch enter R1_REWOUND directly and this
        reds. The whole point of VERIFY is that the seam's OK is a COMMANDED
        reading and must never be the terminal on its own."""
        st = drive_to_rewind()
        out, _ = mlib.r1_decide(st, seam(mlib.R1_TAG_REWIND, ut=913.0))
        self.assertEqual(out.phase, mlib.R1_VERIFY)
        self.assertFalse(out.done)
        self.assertIsNone(out.verdict)

    def test_a_rejected_rewind_surfaces_parseks_own_reason(self):
        """FLIGHT-1 DIAGNOSABILITY REGRESSION. The old wording guessed ("the re-fly
        never started, or its post-load marker never landed") and sent the operator
        hunting the RewindPoint while the response carried the real answer.

        MUTATION: drop the `msg` read (restore the speculative text) and this
        reds."""
        st = drive_to_rewind()
        out, _ = mlib.r1_decide(
            st, seam(mlib.R1_TAG_REWIND, result="ERROR",
                     payload=(("msg", "recording-active"),), ut=913.0))
        self.assertTrue(out.done)
        self.assertEqual(out.rewind_reject_reason, "recording-active")
        self.assertIn("Parsek's reason: recording-active", out.flake_reason)
        self.assertIn("rp=rp_b9_root slot=1", out.flake_reason)
        self.assertNotIn("post-load marker never landed", out.flake_reason)

    def test_a_percent_encoded_reason_is_decoded_in_the_giveup(self):
        st = drive_to_rewind()
        encoded = "refly-gate%20Rewind%20point%20is%20marked%20corrupted"
        out, _ = mlib.r1_decide(
            st, seam(mlib.R1_TAG_REWIND, result="ERROR",
                     payload=(("msg", encoded),), ut=913.0))
        self.assertIn("refly-gate Rewind point is marked corrupted", out.flake_reason)

    def test_a_reasonless_rejection_admits_it_rather_than_guessing(self):
        st = drive_to_rewind()
        out, _ = mlib.r1_decide(st, seam(mlib.R1_TAG_REWIND, result="TIMEOUT", ut=913.0))
        self.assertIn("the response carried no msg= reason", out.flake_reason)

    def test_vessel_loss_is_suppressed_in_rewind_only(self):
        """The reload straddle legitimately destroys the active vessel. MUTATION:
        widen the suppression to include R1_VERIFY and LossStillTerminatesInVerify
        reds -- a blanket fail-open."""
        st = drive_to_rewind()
        out, _ = mlib.r1_decide(st, snap(ut=913.0, vessel_lost=True))
        self.assertFalse(out.done)
        self.assertEqual(out.phase, mlib.R1_REWIND)

    def test_loss_still_terminates_in_verify(self):
        st = drive_to_verify()
        out, _ = mlib.r1_decide(st, snap(ut=200.0, vessel_lost=True))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_ASSERT_FAIL)

    def test_loss_still_terminates_in_stop_and_recorder_idle(self):
        """The suppression is ONE phase wide. A craft destroyed while the recorder
        is being stopped is still a destroyed craft."""
        for st in (drive_to_stop(), drive_to_idle()):
            out, _ = mlib.r1_decide(st, snap(ut=911.0, vessel_lost=True))
            self.assertTrue(out.done, st.phase)
            self.assertEqual(out.verdict, mlib.MISSION_ASSERT_FAIL)


# ---------------------------------------------------------------------------
# R1 machine: the OBSERVED verify gate. The load-bearing cell of the lane.
# ---------------------------------------------------------------------------


class R1ObservedVerifyTests(unittest.TestCase):

    def _at_verify(self, pre_ut=912.0, **over):
        return drive_to_verify(pre_ut=pre_ut, **over)

    def test_backward_clock_is_the_advance(self):
        st = self._at_verify()
        out, actions = mlib.r1_decide(
            st, snap(ut=312.0, altitude=1200.0, situation="FLYING", body="Kerbin"))
        self.assertEqual(out.phase, mlib.R1_REWOUND)
        self.assertAlmostEqual(out.ut_regression, 600.0)
        self.assertEqual(out.post_rewind_ut, 312.0)
        # REWOUND is a WAYPOINT, not the terminal: a rewind that is never flown
        # again is half a loop, and it is the half that leaves the re-fly's
        # provisional empty (flight 2). MUTATION: set `done` on REWOUND and this
        # reds.
        self.assertFalse(out.done)
        self.assertIsNone(out.verdict)
        self.assertEqual([a.kind for a in actions],
                         [mlib.ACTION_SET_THROTTLE, mlib.ACTION_ACTIVATE_STAGE])

    def test_a_forward_clock_never_satisfies_the_gate(self):
        """THE anti-commanded cell. MUTATION: replace the gate with
        `if state.rewind_result == "OK"` (i.e. trust the seam's own OK) and this
        reds -- the machine would call a rewind that never moved the clock done."""
        cur = self._at_verify(verifyFrames=4)
        for i in range(8):
            cur, _ = mlib.r1_decide(
                cur, snap(ut=1000.0 + i, altitude=80500.0, situation="ORBITING"))
            if cur.done:
                break
        self.assertTrue(cur.done)
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)
        self.assertIn("never ran backward", cur.flake_reason)
        self.assertIn("COMMANDED reading", cur.flake_reason)

    def test_a_sub_floor_regression_never_satisfies_the_gate(self):
        """MUTATION: drop the floor (`regression > 0`) and this reds. A 0.5 s
        wobble is float noise, not a rewind."""
        cur = self._at_verify(verifyFrames=3)
        for _ in range(6):
            cur, _ = mlib.r1_decide(cur, snap(ut=911.5, altitude=1200.0))
            if cur.done:
                break
        self.assertTrue(cur.done)
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)

    def test_exactly_the_floor_satisfies_the_gate(self):
        st = self._at_verify()
        out, _ = mlib.r1_decide(st, snap(ut=907.0, altitude=1200.0))
        self.assertEqual(out.phase, mlib.R1_REWOUND)
        self.assertAlmostEqual(out.ut_regression, 5.0)

    def test_a_non_finite_clock_never_satisfies_the_gate(self):
        """Fail-closed on an unread channel: NaN must not compare its way into a
        pass. MUTATION: drop the _is_finite guards and this raises or passes."""
        cur = self._at_verify(verifyFrames=2)
        for _ in range(5):
            cur, _ = mlib.r1_decide(cur, snap(ut=float("nan"), altitude=1200.0))
            if cur.done:
                break
        self.assertTrue(cur.done)
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)

    def test_verify_is_frame_bounded_not_game_time_bounded(self):
        """THE reason VERIFY counts frames. MUTATION: bound VERIFY with
        `snapshot.ut - phase_entry_ut > budget` and this HANGS (never terminates),
        because after a rewind that difference is negative forever."""
        cur = self._at_verify(verifyFrames=5)
        for i in range(20):
            cur, _ = mlib.r1_decide(cur, snap(ut=911.9 - i * 0.01, altitude=1200.0))
            if cur.done:
                break
        self.assertTrue(cur.done, "VERIFY must terminate on a FRAME budget")
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)


# ---------------------------------------------------------------------------
# R1 assertions.
# ---------------------------------------------------------------------------


class R1AssertionTests(unittest.TestCase):

    def _flown(self):
        """The WHOLE loop: ascent, commit, stop, idle, rewind, observed backward
        clock, second flight, recorded points."""
        return r1_params(), drive_to_loop_closed()

    def test_full_cycle_meets_every_row(self):
        params, st = self._flown()
        rows = mlib.evaluate_r1_assertions([], params, st)
        self.assertTrue(mlib.all_assertions_met(rows), [r.name for r in rows if not r.met])
        self.assertEqual([r.name for r in rows],
                         ["reachedOrbitBeforeRewind", "treeCommittedBeforeRewind",
                          "recorderIdleBeforeRewind", "clockRewound",
                          "vesselStateChanged", "postRewindFlightObserved",
                          "postRewindFlightRecordedSomewhere", "rewindSeamAccepted"])

    def test_the_observed_rows_are_labelled_observed(self):
        params, st = self._flown()
        rows = {r.name: r for r in mlib.evaluate_r1_assertions([], params, st)}
        self.assertEqual(rows["clockRewound"].detail["channel"], "observed")
        self.assertEqual(rows["vesselStateChanged"].detail["channel"], "observed")
        self.assertEqual(rows["recorderIdleBeforeRewind"].detail["channel"], "observed")
        # And the seam's own OK is labelled for what it is.
        self.assertEqual(rows["rewindSeamAccepted"].detail["channel"], "commanded")

    def test_the_recorder_idle_row_carries_the_read_value_not_the_stop_verbs_ok(self):
        """MUTATION: set `idle_met` from `stop_result == "OK"` and this reds. The
        StopRecording verb's OK is a COMMANDED reading; the row's evidence is the
        `recording` field READ back off RecordingState."""
        params, st = self._flown()
        row = {r.name: r for r in mlib.evaluate_r1_assertions([], params, st)}[
            "recorderIdleBeforeRewind"]
        self.assertEqual(row.value, "false")
        self.assertEqual(row.detail["stopSeamResult"], "OK")
        # A state where the stop returned OK but nothing was ever READ must fail.
        blind = replace(st, recorder_idle_observed=False, recorder_idle_reading="")
        blind_row = {r.name: r for r in mlib.evaluate_r1_assertions([], params, blind)}[
            "recorderIdleBeforeRewind"]
        self.assertFalse(blind_row.met)
        self.assertEqual(blind_row.detail["stopSeamResult"], "OK")

    def test_a_commanded_ok_alone_does_not_meet_the_assertions(self):
        """THE anti-vacuity cell. A state where InvokeRewind returned OK but no
        backward clock was ever observed must NOT evaluate green. MUTATION: drop
        clockRewound/vesselStateChanged from the returned list and this reds."""
        params, st = self._flown()
        st = replace(st, ut_regression=float("nan"),
                     post_rewind_ut=float("nan"),
                     post_rewind_altitude=float("nan"),
                     post_rewind_situation="")
        rows = mlib.evaluate_r1_assertions([], params, st)
        self.assertFalse(mlib.all_assertions_met(rows))
        unmet = {r.name for r in rows if not r.met}
        self.assertIn("clockRewound", unmet)
        self.assertIn("vesselStateChanged", unmet)
        # The commanded row is still met -- which is exactly why it can never be
        # the only row.
        self.assertTrue({r.name: r for r in rows}["rewindSeamAccepted"].met)

    def test_the_reject_reason_rides_the_commanded_row(self):
        params = r1_params()
        st = drive_to_rewind()
        st, _ = mlib.r1_decide(
            st, seam(mlib.R1_TAG_REWIND, result="ERROR",
                     payload=(("msg", "recording-active"),), ut=913.0))
        row = {r.name: r for r in mlib.evaluate_r1_assertions([], params, st)}[
            "rewindSeamAccepted"]
        self.assertFalse(row.met)
        self.assertEqual(row.detail["rejectReason"], "recording-active")

    def test_a_situation_change_alone_satisfies_the_corroboration_row(self):
        params, st = self._flown()
        st = replace(st, pre_rewind_altitude=1200.0, post_rewind_altitude=1200.5)
        rows = {r.name: r for r in mlib.evaluate_r1_assertions([], params, st)}
        self.assertTrue(rows["vesselStateChanged"].met)

    def test_no_change_at_all_fails_the_corroboration_row(self):
        params, st = self._flown()
        st = replace(st, pre_rewind_altitude=1200.0, post_rewind_altitude=1200.5,
                     pre_rewind_situation="FLYING", post_rewind_situation="FLYING")
        rows = {r.name: r for r in mlib.evaluate_r1_assertions([], params, st)}
        self.assertFalse(rows["vesselStateChanged"].met)

    def test_rows_serialize_without_nan(self):
        params, st = self._flown()
        st = replace(st, ut_regression=float("nan"))
        for row in mlib.evaluate_r1_assertions([], params, st):
            d = row.to_dict()
            for key, value in d.items():
                self.assertFalse(isinstance(value, float) and not math.isfinite(value),
                                 "%s.%s is non-finite" % (row.name, key))

    def test_flake_reason_reaches_the_mission_verdict(self):
        params = r1_params(rewindPointId="")
        st = mlib.r1_initial_state(params)
        st, _ = mlib.r1_decide(st, snap(ut=100.0))
        verdict, reason = mlib.resolve_flight_verdict(
            st, mlib.evaluate_r1_assertions([], params, st))
        self.assertEqual(verdict, mlib.MISSION_FLAKE)
        self.assertIn("rewind target unresolved", reason)


class R1LoopClosureTests(unittest.TestCase):
    """The SECOND flight. Flight 2 (2026-07-26) rewound correctly and still red
    the run: R1 tore down without re-flying, so the re-fly session's provisional
    recording carried ZERO Points and `SupersedeCommit.ValidateSupersedeTarget`
    refused to write any supersede rows (`reason=empty Points`). A rewind that is
    never flown again is half a loop."""

    def test_rewound_is_a_waypoint_that_commands_a_second_flight(self):
        """MUTATION: make `_r1_enter` terminal on R1_REWOUND (the pre-fix
        machine, which is exactly what flight 2 flew) and this reds."""
        st = drive_to_verify()
        out, actions = mlib.r1_decide(
            st, snap(ut=312.0, altitude=100.0, situation="PRE_LAUNCH"))
        self.assertEqual(out.phase, mlib.R1_REWOUND)
        self.assertFalse(out.done, "REWOUND must not be the terminal")
        self.assertEqual([(a.kind, a.value) for a in actions],
                         [(mlib.ACTION_SET_THROTTLE, 1.0),
                          (mlib.ACTION_ACTIVATE_STAGE, None)])

    def test_the_relaunch_gate_is_measured_altitude_not_the_commanded_throttle(self):
        """MUTATION: advance RELAUNCH on `state.phase_frames > 0` (i.e. trust the
        throttle command) and this reds. Sending throttle is COMMANDED; climbing
        is OBSERVED."""
        st = drive_to_relaunch(post_alt=100.0)
        # Sitting on the pad with the throttle commanded open: no climb, no
        # advance, no matter how many frames.
        for _ in range(5):
            st, _ = mlib.r1_decide(st, snap(ut=313.0, altitude=100.0,
                                            situation="PRE_LAUNCH"))
            self.assertEqual(st.phase, mlib.R1_RELAUNCH)
            self.assertFalse(st.done)
        # A real climb advances it.
        out, actions = mlib.r1_decide(
            st, snap(ut=320.0, altitude=650.0, situation="FLYING"))
        self.assertEqual(out.phase, mlib.R1_LOOP_POINTS)
        self.assertAlmostEqual(out.relaunch_altitude_gain, 550.0)
        self.assertEqual([(a.seam_verb, a.seam_tag) for a in actions],
                         [("RecordingState", "loop0")])

    def test_a_sub_floor_climb_never_satisfies_the_relaunch_gate(self):
        st = drive_to_relaunch(post_alt=100.0, relaunchFrames=3)
        for _ in range(8):
            st, _ = mlib.r1_decide(st, snap(ut=313.0, altitude=150.0,
                                            situation="FLYING"))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("never climbed", st.flake_reason)
        self.assertIn("empty-provisional condition", st.flake_reason)

    def test_flying_is_not_enough_the_flight_must_be_RECORDED(self):
        """THE flight-2 condition, as a machine gate. The craft climbed, but the
        recording is still empty -> the merge would refuse supersede rows. This
        MUST NOT reach the terminal.

        MUTATION: advance LOOP-POINTS on any OK reply (drop the `points > 0`
        test) and this reds."""
        st = drive_to_loop_points()
        cur = st
        for i in range(80):
            cur, _ = mlib.r1_decide(
                cur, seam(mlib.r1_loop_probe_tag(cur.loop_probe),
                          payload=(("points", "0"),), ut=321.0 + i, altitude=700.0))
            if cur.done:
                break
        self.assertTrue(cur.done)
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)
        self.assertNotIn(mlib.R1_LOOP_CLOSED, cur.phases_reached)
        self.assertIn("points=0", cur.flake_reason)
        self.assertIn("empty Points", cur.flake_reason)

    def test_recorded_points_close_the_loop(self):
        st = drive_to_loop_closed(points=137)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(st.phase, mlib.R1_LOOP_CLOSED)
        self.assertEqual(st.loop_points_read, 137)
        self.assertEqual(
            st.phases_reached,
            (mlib.R1_ASCENT, mlib.R1_COMMIT, mlib.R1_STOP, mlib.R1_RECORDER_IDLE,
             mlib.R1_REWIND, mlib.R1_VERIFY, mlib.R1_REWOUND, mlib.R1_RELAUNCH,
             mlib.R1_LOOP_POINTS, mlib.R1_LOOP_CLOSED))

    def test_an_unreadable_points_field_fails_closed(self):
        """MUTATION: treat an unparseable `points` as 0-and-keep-probing (or as
        success) and this reds. An unread count is not evidence of a recording."""
        st = drive_to_loop_points()
        out, _ = mlib.r1_decide(
            st, seam(mlib.r1_loop_probe_tag(0), payload=(("tree", "t1"),), ut=321.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("no readable `points` field", out.flake_reason)
        self.assertEqual(out.loop_points_read, -1)

    def test_loop_probes_use_their_own_tag_family(self):
        """`loop*` never collides with the RECORDER-IDLE `state*` family even at
        the same index. MUTATION: reuse `r1_state_probe_tag` for the loop probes
        and this reds."""
        self.assertNotEqual(mlib.r1_loop_probe_tag(0), mlib.r1_state_probe_tag(0))
        st = drive_to_loop_points()
        tags = []
        for i in range(3):
            st, actions = mlib.r1_decide(
                st, seam(mlib.r1_loop_probe_tag(i), payload=(("points", "0"),),
                         ut=321.0 + i, altitude=700.0))
            tags += [a.seam_tag for a in actions]
        self.assertEqual(tags, ["loop1", "loop2", "loop3"])

    def test_the_unread_points_sentinel_is_minus_one_not_zero(self):
        """The row demands a POSITIVE read. -1 (never read) and 0 (read, empty)
        are different diagnoses and neither is evidence of a recording.

        The machine cannot currently reach LOOP-CLOSED with a non-positive count
        (it only enters on `points > 0`), so the last two cases are CONSTRUCTED:
        they pin the ROW's contract so a future path into LOOP-CLOSED that skips
        the read inherits a proven guard instead of an assumed one. MUTATION:
        `points_read >= 0` and this reds on the zero case."""
        params = r1_params()
        st = drive_to_relaunch()
        self.assertEqual(st.loop_points_read, -1)
        rows = {r.name: r for r in mlib.evaluate_r1_assertions([], params, st)}
        self.assertFalse(rows["postRewindFlightRecordedSomewhere"].met)
        self.assertEqual(rows["postRewindFlightRecordedSomewhere"].value, -1)

        closed = drive_to_loop_closed(points=7)
        for bogus in (0, -1):
            blind = replace(closed, loop_points_read=bogus)
            row = {r.name: r
                   for r in mlib.evaluate_r1_assertions([], params, blind)}[
                       "postRewindFlightRecordedSomewhere"]
            self.assertFalse(row.met, "points=%d must not satisfy the row" % bogus)
            self.assertEqual(row.value, bogus)

    def test_the_loop_rows_are_observed_and_both_required(self):
        params, st = r1_params(), drive_to_loop_closed(points=42)
        rows = {r.name: r for r in mlib.evaluate_r1_assertions([], params, st)}
        self.assertEqual(rows["postRewindFlightObserved"].detail["channel"], "observed")
        self.assertEqual(rows["postRewindFlightRecordedSomewhere"].detail["channel"], "observed")
        self.assertEqual(rows["postRewindFlightRecordedSomewhere"].value, 42)
        self.assertTrue(rows["postRewindFlightObserved"].met)

    def test_a_rewind_without_a_second_flight_does_not_evaluate_green(self):
        """ITEM 4 / anti-vacuity: the flight-2 shape (rewind, then teardown) must
        NEVER read as a closed loop. This is the machine-level guard that keeps
        the finding tested now that R1's happy path routes around it; the
        SCENARIO-level guard is S4.1, which drives rewind-then-teardown with no
        flying at all.

        MUTATION: drop either loop row from evaluate_r1_assertions and this
        reds."""
        params = r1_params()
        st = drive_to_verify()
        st, _ = mlib.r1_decide(st, snap(ut=312.0, altitude=100.0,
                                        situation="PRE_LAUNCH"))
        rows = mlib.evaluate_r1_assertions([], params, st)
        self.assertFalse(mlib.all_assertions_met(rows))
        unmet = {r.name for r in rows if not r.met}
        self.assertIn("postRewindFlightObserved", unmet)
        self.assertIn("postRewindFlightRecordedSomewhere", unmet)
        # The rewind half is genuinely met -- which is the point: the run is only
        # unmet on the LOOP half, so the diagnosis is unambiguous.
        met = {r.name for r in rows if r.met}
        self.assertIn("clockRewound", met)
        self.assertIn("vesselStateChanged", met)


class R1AssertionRowIndependenceTests(unittest.TestCase):
    """DIRECT-STATE cells for the rows a machine-driven test cannot falsify.

    `evaluate_r1_assertions` exists to be the INDEPENDENT judge of a run, so each
    row must be shown to fail on a state that does not satisfy it - even when the
    machine cannot currently produce that state. The 2026-07-26 review found three
    rows whose every conjunct survived mutation because the happy-path drive
    always satisfied them; these cells pin them from constructed states."""

    def _closed(self):
        return r1_params(), drive_to_loop_closed(points=42)

    def test_reached_orbit_row_fails_when_the_ascent_never_orbited(self):
        """MUTATION: `orbit_met = True` and this reds."""
        params, st = self._closed()
        blind = replace(st, ascent=replace(st.ascent, phases_reached=(mlib.B2_PRELAUNCH,)))
        row = {r.name: r for r in mlib.evaluate_r1_assertions([], params, blind)}[
            "reachedOrbitBeforeRewind"]
        self.assertFalse(row.met)
        self.assertEqual(row.value, mlib.B2_PRELAUNCH)
        # And it is met on the real state, so the cell is not vacuously red.
        real = {r.name: r for r in mlib.evaluate_r1_assertions([], params, st)}[
            "reachedOrbitBeforeRewind"]
        self.assertTrue(real.met)

    def test_tree_committed_row_fails_on_a_non_ok_token_and_on_a_short_run(self):
        """MUTATION: `commit_met = True`, or dropping either conjunct, and this
        reds. Both halves matter: the token must be OK AND the machine must have
        actually left COMMIT."""
        params, st = self._closed()
        rows = lambda s: {r.name: r for r in mlib.evaluate_r1_assertions([], params, s)}
        self.assertTrue(rows(st)["treeCommittedBeforeRewind"].met)
        # Token not OK.
        self.assertFalse(rows(replace(st, commit_result="ERROR"))[
            "treeCommittedBeforeRewind"].met)
        self.assertFalse(rows(replace(st, commit_result=""))[
            "treeCommittedBeforeRewind"].met)
        # Token OK but the run never reached STOP (the commit never took effect).
        never_stopped = replace(st, phases_reached=(mlib.R1_ASCENT, mlib.R1_COMMIT))
        self.assertFalse(rows(never_stopped)["treeCommittedBeforeRewind"].met)

    def test_post_rewind_flight_row_fails_on_each_conjunct_independently(self):
        """MUTATION: `flew_met = True`, dropping the finite check, dropping the
        floor compare, or dropping the phase check - each reds a distinct case
        below. The review found every conjunct of this row survived mutation."""
        params, st = self._closed()
        rows = lambda s: {r.name: r for r in mlib.evaluate_r1_assertions([], params, s)}
        self.assertTrue(rows(st)["postRewindFlightObserved"].met)
        # (a) never measured. NOTE NaN alone does NOT exercise `_is_finite`:
        # `nan >= floor` is already False, so the guard is redundant there. What
        # `_is_finite` actually guards is INFINITY, which compares True against
        # any floor - so both are asserted.
        self.assertFalse(rows(replace(st, relaunch_altitude_gain=float("nan")))[
            "postRewindFlightObserved"].met)
        self.assertFalse(rows(replace(st, relaunch_altitude_gain=float("inf")))[
            "postRewindFlightObserved"].met,
            "an infinite altitude reading is a broken channel, not a climb")
        # (b) measured but below the floor
        below = replace(st, relaunch_altitude_gain=params.relaunch_min_altitude_gain - 1.0)
        self.assertFalse(rows(below)["postRewindFlightObserved"].met)
        # (c) a NEGATIVE gain (the craft sank) must never satisfy a climb row
        self.assertFalse(rows(replace(st, relaunch_altitude_gain=-500.0))[
            "postRewindFlightObserved"].met)
        # (d) gain recorded but the machine never reached LOOP-POINTS
        early = replace(st, phases_reached=(mlib.R1_ASCENT, mlib.R1_RELAUNCH))
        self.assertFalse(rows(early)["postRewindFlightObserved"].met)

    def test_the_recorded_tree_evidence_rides_the_row(self):
        """The flight-3 diagnosis (points landed in a DIFFERENT tree) must reach
        the result JSON. MUTATION: drop the `recordedTree` detail and this reds."""
        params = r1_params()
        st = drive_to_loop_points()
        st, _ = mlib.r1_decide(
            st, seam(mlib.r1_loop_probe_tag(0),
                     payload=(("points", "24"), ("tree", "820de77e")),
                     ut=321.0, altitude=700.0, situation="FLYING"))
        row = {r.name: r for r in mlib.evaluate_r1_assertions([], params, st)}[
            "postRewindFlightRecordedSomewhere"]
        self.assertEqual(row.detail["recordedTree"], "820de77e")
        self.assertIn("doesNotProve", row.detail)

    def test_every_row_carries_a_channel_label(self):
        """A reader must be able to tell observed evidence from a commanded ack
        without consulting the source."""
        params, st = self._closed()
        for row in mlib.evaluate_r1_assertions([], params, st):
            self.assertIn(row.detail.get("channel"), ("observed", "commanded"),
                          "%s has no channel label" % row.name)


class R1SeamPayloadReaderTests(unittest.TestCase):
    """`_r1_seam_payload` / `_r1_reject_reason` read a payload field under the SAME
    tag gate `_r1_seam_result` applies to the token.

    HONEST SCOPE: at every CURRENT call site the two gates are redundant - the
    payload is only read on a branch the token gate already opened, so no machine
    path can reach a mismatched-tag payload read, and a machine-level cell cannot
    discriminate the gate's removal. These cells test the HELPER's contract
    directly, so a future call site that reads a payload WITHOUT first checking the
    token (which is what would make the gate load-bearing) inherits a guard that is
    already proven rather than one that was only ever assumed."""

    def test_payload_is_read_under_the_matching_tag(self):
        s = seam("rewind", payload=(("rp", "rp_x"), ("slot", "1")))
        self.assertEqual(mlib._r1_seam_payload(s, "rewind", "rp"), "rp_x")
        self.assertEqual(mlib._r1_seam_payload(s, "rewind", "slot"), "1")

    def test_a_payload_under_a_different_tag_is_not_read(self):
        """MUTATION: drop the tag check in _r1_seam_payload and this reds."""
        s = seam("commit", payload=(("recording", "false"),))
        self.assertEqual(mlib._r1_seam_payload(s, "rewind", "recording"), "")
        self.assertEqual(mlib._r1_seam_payload(s, mlib.r1_state_probe_tag(0),
                                               "recording"), "")

    def test_a_missing_key_reads_empty(self):
        s = seam("state0", payload=(("tree", "t1"),))
        self.assertEqual(mlib._r1_seam_payload(s, "state0", "recording"), "")

    def test_reject_reason_decodes_and_is_tag_gated(self):
        s = seam("rewind", result="ERROR", payload=(("msg", "refly-gate%20nope"),))
        self.assertEqual(mlib._r1_reject_reason(s, "rewind"), "refly-gate nope")
        self.assertEqual(mlib._r1_reject_reason(s, "commit"), "")

    def test_because_admits_an_absent_reason_rather_than_guessing(self):
        self.assertIn("recording-active", mlib._r1_because("recording-active"))
        self.assertEqual(mlib._r1_because(""), "the response carried no msg= reason")


class R1SeamValueDecodeTests(unittest.TestCase):
    """`decode_seam_value` -- the inverse of the C# TestCommandProtocol.Encode.
    MUTATION: return the input unchanged and DecodesPercentEscapes reds."""

    def test_decodes_percent_escapes(self):
        self.assertEqual(
            mlib.decode_seam_value("refly-gate%20Rewind%20point%20is%20marked%20corrupted"),
            "refly-gate Rewind point is marked corrupted")

    def test_literal_values_pass_through(self):
        self.assertEqual(mlib.decode_seam_value("recording-active"), "recording-active")
        self.assertEqual(mlib.decode_seam_value(""), "")
        self.assertEqual(mlib.decode_seam_value(None), "")

    def test_malformed_escapes_never_raise(self):
        for bad in ("%", "%Z", "%ZZ tail", "100%"):
            self.assertEqual(mlib.decode_seam_value(bad), bad)

    def test_multibyte_utf8_round_trips(self):
        self.assertEqual(mlib.decode_seam_value("a%C3%A9b"), "a\u00e9b")


class R1ParamsTests(unittest.TestCase):

    def test_ascent_params_are_the_b2_params_over_the_same_dict(self):
        """MUTATION: fork the ascent tuning (e.g. hardcode target_apoapsis) and
        this reds -- the delegated leg must never drift from the live-proven one."""
        p = r1_params()
        self.assertEqual(p.b2, mlib.b2_params_from_dict(R1_MISSION_PARAMS))

    def test_unread_sentinels_are_the_defaults(self):
        p = mlib.r1_params_from_dict({})
        self.assertEqual(p.rewind_point_id, "")
        self.assertEqual(p.rewind_slot, -1)


if __name__ == "__main__":
    unittest.main()
