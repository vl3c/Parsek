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

import ast
import math
import os
import tomllib
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
    UnsetSlotFlakesOnFrameOne / BadSelectFlakesOnFrameOne red -- the real defect
    being a full ascent flown to reach a leg that InvokeRewind could only ever
    answer REJECTED unknown-slot, or a resolve whose selection can never pick.

    R10 MOVED ONE HALF OF THIS GUARD. An empty ``rewindPointId`` used to be the
    canonical unresolved target and flaked here; it is now the RESOLVE trigger (a
    live RewindPoint id is a fresh Guid, so "ask the game" is the only correct
    value for a lane that injected no fixture). The SLOT still fails closed --
    nothing resolves a slot at runtime -- and the frame-1 fault on the resolve path
    is an out-of-set ``rewindPointSelect``."""

    def test_unset_rewind_point_arms_the_resolve_instead_of_flaking(self):
        # The R10 contract, asserted as the ABSENCE of the old give-up: an empty
        # id must fly the ascent and reach RESOLVE, not die on frame 1.
        st = mlib.r1_initial_state(r1_params(rewindPointId=""))
        out, _actions = mlib.r1_decide(st, snap(ut=100.0))
        self.assertFalse(out.done)
        self.assertEqual(out.phase, mlib.R1_ASCENT)

    def test_unset_slot_flakes_on_frame_one(self):
        st = mlib.r1_initial_state(r1_params(rewindSlot=-1))
        out, actions = mlib.r1_decide(st, snap(ut=100.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("rewind target unresolved", out.flake_reason)
        self.assertEqual(actions, [])

    def test_a_bad_select_on_the_resolve_path_flakes_on_frame_one(self):
        st = mlib.r1_initial_state(
            r1_params(rewindPointId="", rewindPointSelect="newest"))
        out, actions = mlib.r1_decide(st, snap(ut=100.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("rewindPointSelect", out.flake_reason)
        self.assertEqual(actions, [])

    def test_a_bad_select_is_inert_when_the_id_is_supplied(self):
        # The selection is READ only on the resolve path, so an unused typo must
        # not fail a lane whose target was named outright.
        st = mlib.r1_initial_state(r1_params(rewindPointSelect="newest"))
        out, _ = mlib.r1_decide(st, snap(ut=100.0))
        self.assertFalse(out.done)

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
        # The frame-1 give-up this drives is now the SLOT half of the preflight
        # guard: since R10 an empty rewindPointId arms the RESOLVE phase instead
        # of flaking (see R1PreflightGuardTests). The property under test - a
        # frame-1 flake reason reaching the mission verdict verbatim - is unchanged.
        params = r1_params(rewindSlot=-1)
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



# ---------------------------------------------------------------------------
# R10 RESOLVE: the mission side of the runtime-handle path.
# ---------------------------------------------------------------------------

# The ListHandles payload a live rewindpoints enumeration produces, keyed exactly
# as design-autotest-command-seam.md's "#### ListHandles" grammar spells it.
RESOLVE_PAYLOAD_3 = (("kind", "rewindpoints"), ("count", "3"),
                     ("truncated", "false"),
                     ("rp0", "rp_aaaa"), ("rp0ut", "382.7"),
                     ("rp1", "rp_bbbb"), ("rp1ut", "693.3"),
                     ("rp2", "rp_cccc"), ("rp2ut", "8950.6"))


def drive_to_resolve(pre_ut=912.0, **over):
    """The RESOLVE path: no rewindPointId in the params, so probe 0 reading
    recording=false parks the machine in RESOLVE with ListHandles emitted."""
    over.setdefault("rewindPointId", "")
    st = drive_to_idle(**over)
    st, actions = mlib.r1_decide(
        st, seam(mlib.r1_state_probe_tag(0), payload=(("recording", "false"),),
                 ut=pre_ut, altitude=80500.0, situation="ORBITING", body="Kerbin"))
    assert st.phase == mlib.R1_RESOLVE, st.phase
    return st, actions


class R1SeamHandleReadTests(unittest.TestCase):
    """seam_handle_from_payload: the tag-gated, fail-closed handle read.

    MUTATION: drop the tag check (return the payload regardless of tag) and
    ReadsNothingFromAnotherCommandsPayload reds -- the real defect being a machine
    commanding an IRREVERSIBLE rewind against an id the PREVIOUS command answered
    with, which is the stale-result fail-open the whole seam contract exists to
    prevent."""

    def test_reads_a_field_of_its_own_commands_payload(self):
        s = seam(mlib.R1_TAG_RESOLVE, payload=RESOLVE_PAYLOAD_3)
        self.assertEqual("rp_aaaa",
                         mlib.seam_handle_from_payload(s, mlib.R1_TAG_RESOLVE, "rp0"))
        self.assertEqual("3",
                         mlib.seam_handle_from_payload(s, mlib.R1_TAG_RESOLVE, "count"))

    def test_reads_nothing_from_another_commands_payload(self):
        s = seam(mlib.R1_TAG_REWIND, payload=RESOLVE_PAYLOAD_3)
        self.assertEqual("",
                         mlib.seam_handle_from_payload(s, mlib.R1_TAG_RESOLVE, "rp0"))

    def test_an_absent_field_reads_empty(self):
        s = seam(mlib.R1_TAG_RESOLVE, payload=RESOLVE_PAYLOAD_3)
        self.assertEqual("",
                         mlib.seam_handle_from_payload(s, mlib.R1_TAG_RESOLVE, "rp9"))

    def test_the_value_is_returned_raw_for_the_wire(self):
        # NOT decoded: format_seam_command_line passes arg values through verbatim
        # onto a whitespace-delimited line, so decoding here would break the token.
        s = seam(mlib.R1_TAG_RESOLVE, payload=(("rec0name", "Kerbal%20X"),))
        self.assertEqual("Kerbal%20X",
                         mlib.seam_handle_from_payload(s, mlib.R1_TAG_RESOLVE, "rec0name"))


class R1SelectRewindPointKeyTests(unittest.TestCase):
    """The selection arithmetic, on its own so the off-by-one is never discovered
    on a flight. MUTATION: change `count - 1` to `count` and
    LastSelectsTheNewest reds."""

    def test_last_selects_the_newest(self):
        # RewindPoints are in APPEND order, so rp<count-1> is the newest.
        self.assertEqual("rp2", mlib.r1_select_rewind_point_key("last", 3))
        self.assertEqual("rp0", mlib.r1_select_rewind_point_key("last", 1))

    def test_first_selects_the_oldest(self):
        self.assertEqual("rp0", mlib.r1_select_rewind_point_key("first", 3))

    def test_an_unusable_count_or_selection_selects_nothing(self):
        for select, count in (("last", 0), ("last", -1), ("first", 0),
                              ("newest", 3), ("", 3)):
            with self.subTest(select=select, count=count):
                self.assertEqual("", mlib.r1_select_rewind_point_key(select, count))


class R1ResolvePhaseTests(unittest.TestCase):
    """The RESOLVE phase itself.

    MUTATION: make RESOLVE advance on the seam token alone (skip the count / key
    read) and ACountOfZeroIsANamedGiveUp / AnUnreadableCountIsANamedGiveUp red --
    the real defect being an InvokeRewind commanded against an EMPTY id, which the
    seam answers REJECTED unknown-rp after the whole ascent has been flown."""

    def test_the_resolve_is_entered_only_when_no_id_was_supplied(self):
        st, actions = drive_to_resolve()
        self.assertEqual(mlib.R1_RESOLVE, st.phase)
        self.assertEqual(
            [Action(mlib.ACTION_PARSEK_SEAM_COMMAND, seam_verb="ListHandles",
                    seam_args=(("kind", "rewindpoints"),),
                    seam_tag=mlib.R1_TAG_RESOLVE)],
            actions)

    def test_a_supplied_id_skips_the_resolve_entirely(self):
        st = drive_to_rewind()
        self.assertEqual(mlib.R1_REWIND, st.phase)
        self.assertNotIn(mlib.R1_RESOLVE, st.phases_reached)

    def test_last_folds_the_newest_id_into_the_rewind(self):
        st, _ = drive_to_resolve()
        st, actions = mlib.r1_decide(
            st, seam(mlib.R1_TAG_RESOLVE, payload=RESOLVE_PAYLOAD_3, ut=912.5))
        self.assertEqual(mlib.R1_REWIND, st.phase)
        self.assertEqual("rp_cccc", st.resolved_rewind_point_id)
        self.assertEqual(3, st.resolved_rewind_point_count)
        self.assertEqual(
            [Action(mlib.ACTION_PARSEK_SEAM_COMMAND, seam_verb="InvokeRewind",
                    seam_args=(("rp", "rp_cccc"), ("slot", "1")),
                    seam_tag=mlib.R1_TAG_REWIND)],
            actions)

    def test_first_folds_the_oldest_id_into_the_rewind(self):
        st, _ = drive_to_resolve(rewindPointSelect="first")
        st, actions = mlib.r1_decide(
            st, seam(mlib.R1_TAG_RESOLVE, payload=RESOLVE_PAYLOAD_3, ut=912.5))
        self.assertEqual("rp_aaaa", st.resolved_rewind_point_id)
        self.assertEqual(("rp", "rp_aaaa"), actions[0].seam_args[0])

    def test_a_count_of_zero_is_a_named_give_up(self):
        st, _ = drive_to_resolve()
        st, actions = mlib.r1_decide(
            st, seam(mlib.R1_TAG_RESOLVE,
                     payload=(("kind", "rewindpoints"), ("count", "0"),
                              ("truncated", "false")), ut=912.5))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("count=0", st.flake_reason)
        self.assertIn("MULTI-CONTROLLABLE", st.flake_reason)
        self.assertEqual([], actions)

    def test_an_unreadable_count_is_a_named_give_up(self):
        st, _ = drive_to_resolve()
        st, _ = mlib.r1_decide(
            st, seam(mlib.R1_TAG_RESOLVE,
                     payload=(("kind", "rewindpoints"),), ut=912.5))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("no readable `count`", st.flake_reason)

    def test_a_count_that_disagrees_with_the_payload_is_a_named_give_up(self):
        # count=5 with only rp0..rp2 present: the enumeration and its own count
        # disagree, and a guessed id is never commanded.
        st, _ = drive_to_resolve()
        st, _ = mlib.r1_decide(
            st, seam(mlib.R1_TAG_RESOLVE,
                     payload=(("count", "5"),) + RESOLVE_PAYLOAD_3[3:], ut=912.5))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("disagree", st.flake_reason)

    def test_a_refused_resolve_quotes_parseks_own_reason(self):
        st, _ = drive_to_resolve()
        st, _ = mlib.r1_decide(
            st, seam(mlib.R1_TAG_RESOLVE, result="ERROR",
                     payload=(("msg", "kind-arg-missing"),), ut=912.5))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("Parsek's reason: kind-arg-missing", st.flake_reason)

    def test_a_silent_resolve_gives_up_by_frame_count(self):
        st, _ = drive_to_resolve()
        for _ in range(st.params.resolve_frames + 2):
            st, _ = mlib.r1_decide(st, snap(ut=913.0))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("never answered", st.flake_reason)

    def test_the_resolve_tag_collides_with_no_other_r1_tag(self):
        # The C# seam SKIPS duplicate ids, so a collision would make the resolve a
        # silent no-op whose poll then expired as a TIMEOUT.
        tags = {mlib.R1_TAG_COMMIT, mlib.R1_TAG_STOP, mlib.R1_TAG_REWIND,
                mlib.R1_TAG_RESOLVE}
        self.assertEqual(4, len(tags))
        for probe in range(4):
            self.assertNotIn(mlib.r1_state_probe_tag(probe), tags)
            self.assertNotIn(mlib.r1_loop_probe_tag(probe), tags)

    def test_the_effective_id_is_the_param_then_the_resolved_one(self):
        st = drive_to_rewind()
        self.assertEqual("rp_b9_root", mlib.r1_effective_rewind_point_id(st))
        st, _ = drive_to_resolve()
        self.assertEqual("", mlib.r1_effective_rewind_point_id(st))
        st, _ = mlib.r1_decide(
            st, seam(mlib.R1_TAG_RESOLVE, payload=RESOLVE_PAYLOAD_3, ut=912.5))
        self.assertEqual("rp_cccc", mlib.r1_effective_rewind_point_id(st))


class R1ResolveByteIdenticalRegressionTests(unittest.TestCase):
    """THE REGRESSION THIS WAVE MUST NOT CAUSE: with rewindPointId SET, R1's
    emitted actions are byte-identical to what they were before R10.

    MUTATION: make RECORDER-IDLE always route through RESOLVE (drop the
    `if not rewind_point_id` gate) and this reds -- the real defect being an extra
    ListHandles command on three live-proven lanes (R1 / V1 and the CL-3 sibling
    shape), each of which would consume a wire id and a phase the specs' pinned
    logs do not carry."""

    def _sequence(self, **over):
        """Every action the machine emits from ORBIT to the terminal, as one flat
        list, driving the SAME frames on both paths except for the resolve reply."""
        st = at_orbit(mlib.r1_initial_state(r1_params(**over)))
        emitted = []
        frames = [
            snap(ut=900.0),                                             # -> COMMIT
            seam(mlib.R1_TAG_COMMIT, ut=910.0),                         # -> STOP
            seam(mlib.R1_TAG_STOP, ut=911.0),                           # -> IDLE
            seam(mlib.r1_state_probe_tag(0), payload=(("recording", "false"),),
                 ut=912.0, altitude=80500.0, situation="ORBITING", body="Kerbin"),
        ]
        for frame in frames:
            st, actions = mlib.r1_decide(st, frame)
            emitted.extend(actions)
        return st, emitted

    def test_the_supplied_id_path_emits_the_pre_r10_sequence(self):
        st, emitted = self._sequence()
        self.assertEqual(mlib.R1_REWIND, st.phase)
        self.assertEqual(
            [Action(mlib.ACTION_PARSEK_SEAM_COMMAND, seam_verb="CommitTree",
                    seam_args=(), seam_tag=mlib.R1_TAG_COMMIT),
             Action(mlib.ACTION_PARSEK_SEAM_COMMAND, seam_verb="StopRecording",
                    seam_args=(), seam_tag=mlib.R1_TAG_STOP),
             Action(mlib.ACTION_PARSEK_SEAM_COMMAND, seam_verb="RecordingState",
                    seam_args=(), seam_tag=mlib.r1_state_probe_tag(0)),
             Action(mlib.ACTION_PARSEK_SEAM_COMMAND, seam_verb="InvokeRewind",
                    seam_args=(("rp", "rp_b9_root"), ("slot", "1")),
                    seam_tag=mlib.R1_TAG_REWIND)],
            emitted)

    def test_the_two_paths_differ_by_exactly_the_resolve(self):
        _st_a, supplied = self._sequence()
        st_b, resolved = self._sequence(rewindPointId="")
        # Up to the point of divergence the two are the SAME objects.
        self.assertEqual(supplied[:3], resolved[:3])
        # The resolve path stops one action short (it is parked in RESOLVE waiting
        # for the enumeration) and its next action is the ListHandles.
        self.assertEqual(mlib.R1_RESOLVE, st_b.phase)
        self.assertEqual(
            Action(mlib.ACTION_PARSEK_SEAM_COMMAND, seam_verb="ListHandles",
                   seam_args=(("kind", "rewindpoints"),),
                   seam_tag=mlib.R1_TAG_RESOLVE),
            resolved[3])
        # ... and the rewind it then emits differs from the supplied path's ONLY
        # in the rp value, which is the whole content of this feature.
        st_b, actions = mlib.r1_decide(
            st_b, seam(mlib.R1_TAG_RESOLVE, payload=RESOLVE_PAYLOAD_3, ut=912.5))
        self.assertEqual(supplied[3].seam_verb, actions[0].seam_verb)
        self.assertEqual(supplied[3].seam_tag, actions[0].seam_tag)
        self.assertEqual(supplied[3].seam_args[1], actions[0].seam_args[1])
        self.assertNotEqual(supplied[3].seam_args[0], actions[0].seam_args[0])

    def test_the_resolve_phase_is_absent_from_a_supplied_id_run(self):
        st = drive_to_loop_closed()
        self.assertNotIn(mlib.R1_RESOLVE, st.phases_reached)
        self.assertEqual("", st.resolved_rewind_point_id)
        self.assertEqual(-1, st.resolved_rewind_point_count)


class R1ResolveParamTests(unittest.TestCase):
    """The param surface. MUTATION: default rewindPointSelect to "first" and
    TheDefaultSelectionIsTheNewest reds -- the newest is the point a just-flown
    ascent authored, which is the only one the resolve exists to reach."""

    def test_the_default_selection_is_the_newest(self):
        p = mlib.r1_params_from_dict({})
        self.assertEqual(mlib.R1_REWIND_POINT_SELECT_LAST, p.rewind_point_select)
        self.assertEqual(40, p.resolve_frames)

    def test_the_selection_is_read_verbatim(self):
        p = mlib.r1_params_from_dict({"rewindPointSelect": "first"})
        self.assertEqual("first", p.rewind_point_select)


# ---------------------------------------------------------------------------
# THE RE-FLY CONCLUSION PROFILE (`reflyConclusionProfile`, off by default).
# ---------------------------------------------------------------------------
#
# WHAT IT IS FOR, and therefore what these cells guard: nine in-game `Rewind`
# cells skip because the re-fly's PROVISIONAL recording carries no terminal state,
# and `SupersedeCommit.ValidateSupersedeTarget` refuses a provisional whose
# `TerminalStateValue` is null. R1 is the only mission that drives the InvokeRewind
# slot re-fly AND then flies the restored vessel, so it is the only lane that can
# take that flight to an ENDING. The profile rides past the closed loop, cuts the
# throttle and waits - debounced, bounded - for the craft to be destroyed or to
# read landed, then ends the mission IN FLIGHT with nothing committed.


# The scripted flight the inertness cells replay. One entry per r1_decide frame,
# from the top of the rewind cycle to a landed conclusion. Held as data rather than
# as a sequence of calls so BOTH settings are driven through the IDENTICAL frames -
# a replay that differed in its own inputs would prove nothing about the flag.
CONCLUSION_REPLAY_FRAMES = (
    dict(ut=900.0),                                             # -> COMMIT
    dict(seam_tag=mlib.R1_TAG_COMMIT, ut=910.0),                # -> STOP
    dict(seam_tag=mlib.R1_TAG_STOP, ut=911.0),                  # -> RECORDER-IDLE
    dict(seam_tag=mlib.r1_state_probe_tag(0),                   # -> REWIND
         payload=(("recording", "false"),), ut=912.0, altitude=80500.0,
         situation="ORBITING", body="Kerbin"),
    dict(seam_tag=mlib.R1_TAG_REWIND, ut=913.0),                # -> VERIFY
    dict(ut=312.0, altitude=100.0, situation="PRE_LAUNCH",      # -> REWOUND
         body="Kerbin"),
    dict(ut=313.0, altitude=100.0, situation="PRE_LAUNCH"),     # -> RELAUNCH
    dict(ut=320.0, altitude=600.0, situation="FLYING"),         # -> LOOP-POINTS
    dict(seam_tag=mlib.r1_loop_probe_tag(0),                    # -> LOOP-CLOSED
         payload=(("points", "42"),), ut=321.0, altitude=700.0,
         situation="FLYING"),
    # Everything past here is the profile's own leg. On the OFF path the machine is
    # already done and every one of these frames is a no-op.
    dict(ut=322.0, altitude=800.0, situation="FLYING"),         # -> CONCLUDE-COAST
    dict(ut=340.0, altitude=900.0, situation="FLYING"),
    dict(ut=380.0, altitude=20.0, situation="LANDED"),
    dict(ut=381.0, altitude=20.0, situation="LANDED"),          # -> CONCLUDED
)

# The index of the frame whose reading opened the RELAUNCH gate (the climb to
# 600 m). DERIVED, not hand-counted, so it tracks an edit to the script above.
RELAUNCH_GATE_FRAME = next(
    i for i, f in enumerate(CONCLUSION_REPLAY_FRAMES)
    if f.get("situation") == "FLYING" and "seam_tag" not in f)


def _conclusion_frame(spec):
    """One TelemetrySnapshot from a CONCLUSION_REPLAY_FRAMES entry."""
    kw = dict(spec)
    tag = kw.pop("seam_tag", None)
    payload = kw.pop("payload", ())
    if tag is None:
        return snap(**kw)
    return seam(tag, payload=payload, **kw)


def replay_conclusion(**over):
    """Drive the scripted flight and return (state, trace).

    The trace is what a LIVE run would notice on every frame: the phase it reached
    and the exact actions it emitted. Comparing terminals alone would not do - two
    runs can agree at the end and disagree about which frame commanded the stage."""
    st = at_orbit(mlib.r1_initial_state(r1_params(**over)))
    trace = []
    for spec in CONCLUSION_REPLAY_FRAMES:
        st, acts = mlib.r1_decide(st, _conclusion_frame(spec))
        trace.append((st.phase,
                      tuple((a.kind, a.value, a.text, a.seam_verb) for a in acts)))
        if st.done:
            break
    return st, tuple(trace)


def drive_to_conclude_coast(**over):
    """Machine parked in CONCLUDE-COAST with the throttle already cut, having been
    OBSERVED airborne on the re-flight."""
    st = drive_to_loop_closed(reflyConclusionProfile=True, **over)
    assert not st.done, "LOOP-CLOSED must be a waypoint on the profile"
    st, acts = mlib.r1_decide(st, snap(ut=322.0, altitude=800.0, situation="FLYING"))
    assert st.phase == mlib.R1_CONCLUDE_COAST, st.phase
    assert [a.kind for a in acts] == [mlib.ACTION_SET_THROTTLE], acts
    assert acts[0].value == 0.0, acts[0].value
    assert st.refly_airborne_seen
    return st


class R1ReflyConclusionInertnessTests(unittest.TestCase):
    """THE BYTE-INERTNESS CLAIM, made mechanically rather than by reading the diff.

    The flag adds a waypoint at LOOP-CLOSED, two phases past it, one assertion row
    and one frame-1 conflict predicate. Every committed R1 lane must drive EXACTLY
    what it drove before, so this replays one whole scripted flight through the
    machine with the key ABSENT and with it explicitly `false` and compares the two
    things a live run would notice - the phase reached on every frame and the
    action list emitted on every frame."""

    def test_the_off_path_is_byte_identical_with_the_key_absent_or_false(self):
        """MUTATION: make `_r1_enter`'s `done` unconditional on the new phase (drop
        the `not ... refly_conclusion_profile` conjunct's mirror, i.e. stop LOOP-
        CLOSED terminating on the OFF path) and this reds."""
        self.assertNotIn("reflyConclusionProfile", R1_MISSION_PARAMS,
                         "the committed R1 params must not carry the key - this "
                         "cell's whole claim is about what the key's ABSENCE does")
        absent_state, absent_trace = replay_conclusion()
        false_state, false_trace = replay_conclusion(reflyConclusionProfile=False)
        self.assertEqual(absent_trace, false_trace)
        self.assertEqual(absent_state.phase, false_state.phase)
        self.assertEqual(absent_state.verdict, false_state.verdict)
        self.assertEqual(absent_state.phases_reached, false_state.phases_reached)

    def test_the_off_path_still_closes_the_loop_and_stops_there(self):
        """ANTI-VACUITY for the cell above: the comparison is between two runs that
        actually got somewhere, and the OFF path really does terminate at the phase
        the ON path rides past."""
        state, _trace = replay_conclusion()
        self.assertTrue(state.done)
        self.assertIsNone(state.verdict)
        self.assertEqual(mlib.R1_LOOP_CLOSED, state.phase)
        self.assertNotIn(mlib.R1_CONCLUDE_COAST, state.phases_reached)
        self.assertNotIn(mlib.R1_CONCLUDED, state.phases_reached)
        # ...and none of the profile's own evidence was touched.
        self.assertEqual("", state.conclusion_outcome)
        self.assertFalse(state.refly_airborne_seen)

    def test_the_on_path_diverges_only_after_the_relaunch_gate(self):
        """Where the two paths part, asserted as a FRAME INDEX rather than as a
        verdict. Everything up to and including the closed loop is the same flight;
        the flag can only change what happens after it.

        MUTATION: take the branch at the RELAUNCH gate instead of past LOOP-CLOSED
        and the divergence index moves back onto the loop leg, which is exactly the
        shape that would strand `postRewindFlightRecordedSomewhere` unmet."""
        _off_state, off_trace = replay_conclusion()
        on_state, on_trace = replay_conclusion(reflyConclusionProfile=True)
        divergence = next(
            i for i in range(max(len(off_trace), len(on_trace)))
            if i >= len(off_trace) or i >= len(on_trace)
            or off_trace[i] != on_trace[i])
        self.assertGreater(divergence, RELAUNCH_GATE_FRAME)
        self.assertEqual(off_trace[:divergence], on_trace[:divergence])
        # The last common frame is the one that CLOSED THE LOOP, so both loop rows
        # are resolved against real evidence on the profile too.
        self.assertEqual(mlib.R1_LOOP_CLOSED, off_trace[divergence - 1][0])
        self.assertEqual(len(off_trace), divergence)
        self.assertIn(mlib.R1_LOOP_POINTS, on_state.phases_reached)
        self.assertIn(mlib.R1_LOOP_CLOSED, on_state.phases_reached)


class R1ReflyConclusionTests(unittest.TestCase):
    """The conclusion leg itself: the two outcomes, the debounce, the airborne
    precondition and the named give-up."""

    def test_the_loop_closed_waypoint_cuts_the_throttle_and_hands_over(self):
        """MUTATION: emit nothing on the waypoint and a still-burning engine flies
        a re-flight that never ends - the conclusion wait would then expire on its
        frame cap and read as a Parsek finding."""
        st = drive_to_loop_closed(reflyConclusionProfile=True)
        self.assertFalse(st.done, "LOOP-CLOSED is a waypoint on this profile")
        st, acts = mlib.r1_decide(st, snap(ut=322.0, altitude=800.0,
                                           situation="FLYING"))
        self.assertEqual(mlib.R1_CONCLUDE_COAST, st.phase)
        self.assertEqual([mlib.ACTION_SET_THROTTLE], [a.kind for a in acts])
        self.assertEqual(0.0, acts[0].value)

    def test_a_destroyed_re_flight_concludes_in_flight(self):
        """THE OUTCOME THE PROFILE IS BUILT FOR.
        `ParsekFlight.TerminalEvents.ApplyTerminalDestruction` stamps
        `TerminalState.Destroyed` with the scene still FLIGHT, so a destruction is
        the only terminal state a LIVE re-fly can reach without leaving it."""
        st = drive_to_conclude_coast()
        st, _ = mlib.r1_decide(st, snap(ut=400.0, vessel_lost=True))
        self.assertEqual(mlib.R1_CONCLUDE_COAST, st.phase,
                         "one read must not settle it - the debounce is 2")
        self.assertEqual(1, st.conclusion_destroyed_streak)
        st, acts = mlib.r1_decide(st, snap(ut=401.0, vessel_lost=True))
        self.assertEqual(mlib.R1_CONCLUDED, st.phase)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertIsNone(st.loss_reason,
                          "vessel loss is the OUTCOME here, never a terminal fail")
        self.assertEqual(mlib.R1_CONCLUSION_DESTROYED, st.conclusion_outcome)
        self.assertEqual(401.0, st.conclusion_ut)
        # NOTHING is commanded on the way out: the mission ends IN FLIGHT with the
        # recorder live, and the SPEC's own steps close the tree.
        self.assertEqual([], acts)

    def test_a_destroyed_conclusion_stamps_the_last_finite_clock(self):
        """The frame that WITNESSES a destruction is very often the frame whose
        telemetry stopped being readable. MUTATION: stamp `snapshot.ut` blindly and
        the row reports a conclusion it cannot place in time."""
        st = drive_to_conclude_coast()
        st, _ = mlib.r1_decide(st, snap(ut=450.0, altitude=300.0,
                                        situation="FLYING"))
        self.assertEqual(450.0, st.conclusion_last_finite_ut)
        st, _ = mlib.r1_decide(st, snap(ut=float("nan"), vessel_lost=True))
        st, _ = mlib.r1_decide(st, snap(ut=float("nan"), vessel_lost=True))
        self.assertEqual(mlib.R1_CONCLUDED, st.phase)
        self.assertEqual(mlib.R1_CONCLUSION_DESTROYED, st.conclusion_outcome)
        self.assertEqual(450.0, st.conclusion_ut)

    def test_a_landed_re_flight_concludes_in_flight(self):
        st = drive_to_conclude_coast()
        st, _ = mlib.r1_decide(st, snap(ut=400.0, altitude=15.0,
                                        situation="LANDED"))
        self.assertEqual(mlib.R1_CONCLUDE_COAST, st.phase)
        st, acts = mlib.r1_decide(st, snap(ut=401.0, altitude=14.0,
                                           situation="LANDED"))
        self.assertEqual(mlib.R1_CONCLUDED, st.phase)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(mlib.R1_CONCLUSION_LANDED, st.conclusion_outcome)
        self.assertEqual("LANDED", st.conclusion_situation)
        self.assertEqual(14.0, st.conclusion_altitude)
        self.assertEqual([], acts)

    def test_a_splashed_reading_concludes_on_the_default_set(self):
        """SPLASHED ships in the default list, so a water landing is a conclusion
        rather than a wait that expires."""
        st = drive_to_conclude_coast()
        for ut in (400.0, 401.0):
            st, _ = mlib.r1_decide(st, snap(ut=ut, situation="SPLASHED"))
        self.assertEqual(mlib.R1_CONCLUDED, st.phase)
        self.assertEqual(mlib.R1_CONCLUSION_LANDED, st.conclusion_outcome)

    def test_one_stray_landed_frame_does_not_conclude(self):
        """THE DEBOUNCE, driven. MUTATION: conclude on the first agreeing read and
        one glitched poll ends a flight that is still climbing."""
        st = drive_to_conclude_coast()
        st, _ = mlib.r1_decide(st, snap(ut=400.0, altitude=900.0,
                                        situation="LANDED"))
        self.assertEqual(1, st.conclusion_landed_streak)
        self.assertEqual(mlib.R1_CONCLUDE_COAST, st.phase)
        st, _ = mlib.r1_decide(st, snap(ut=401.0, altitude=950.0,
                                        situation="FLYING"))
        self.assertEqual(0, st.conclusion_landed_streak, "the streak starts over")
        self.assertEqual(mlib.R1_CONCLUDE_COAST, st.phase)
        self.assertEqual("", st.conclusion_outcome)

    def test_a_stray_vessel_lost_frame_does_not_conclude(self):
        """The mirror direction: a terminal that CERTIFIES a destruction gets the
        same treatment as one that condemns."""
        st = drive_to_conclude_coast()
        st, _ = mlib.r1_decide(st, snap(ut=400.0, vessel_lost=True))
        self.assertEqual(1, st.conclusion_destroyed_streak)
        st, _ = mlib.r1_decide(st, snap(ut=401.0, altitude=900.0,
                                        situation="FLYING"))
        self.assertEqual(0, st.conclusion_destroyed_streak)
        self.assertEqual(mlib.R1_CONCLUDE_COAST, st.phase)
        self.assertEqual("", st.conclusion_outcome)

    def test_a_craft_never_seen_airborne_cannot_conclude_landed(self):
        """THE HARD PRECONDITION, and it is not defensive programming: GS-1 flight
        1 MEASURED KSP reporting `situation = LANDED` at alt 230 m climbing at
        113 m/s. MUTATION: drop the `refly_airborne_seen` conjunct and the stale
        pad read ends the re-flight on the frames right after the relaunch."""
        st = replace(drive_to_conclude_coast(), refly_airborne_seen=False)
        for ut in (400.0, 401.0, 402.0):
            st, _ = mlib.r1_decide(st, snap(ut=ut, altitude=230.0,
                                            situation="LANDED"))
        self.assertEqual(mlib.R1_CONCLUDE_COAST, st.phase)
        self.assertEqual(0, st.conclusion_landed_streak)
        self.assertEqual("", st.conclusion_outcome)

    def test_a_craft_never_seen_airborne_can_still_be_destroyed(self):
        """NOT SYMMETRIC, deliberately: a destruction is unambiguous whatever the
        situation field says, and gating it would make a pad-side loss - a real
        conclusion with a real Destroyed stamp - hang to the frame cap."""
        st = replace(drive_to_conclude_coast(), refly_airborne_seen=False)
        for ut in (400.0, 401.0):
            st, _ = mlib.r1_decide(st, snap(ut=ut, vessel_lost=True))
        self.assertEqual(mlib.R1_CONCLUDED, st.phase)
        self.assertEqual(mlib.R1_CONCLUSION_DESTROYED, st.conclusion_outcome)

    def test_a_re_flight_that_never_concludes_flakes_by_name(self):
        """A bounded wait, and its give-up says which of the two readings never
        arrived. MUTATION: let the phase run unbounded and the mission hangs to the
        whole-run budget with no named give-up - the exact failure the section
        header's frame-budget discipline exists to prevent."""
        st = drive_to_conclude_coast(reflyConclusionFrames=4)
        ut = 400.0
        for _ in range(10):
            st, _ = mlib.r1_decide(st, snap(ut=ut, altitude=900.0,
                                            situation="FLYING"))
            ut += 1.0
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.R1_CONCLUDE_COAST, st.flake_phase)
        self.assertIn("never reached a terminal conclusion", st.flake_reason)
        self.assertIn("LANDED", st.flake_reason)
        self.assertIn("airborne seen=true", st.flake_reason)
        self.assertIn("ValidateSupersedeTarget", st.flake_reason)
        self.assertEqual("", st.conclusion_outcome)

    def test_a_vessel_lost_outside_the_conclusion_phase_is_still_lethal(self):
        """The exemption is ONE phase wide, never a blanket fail-open. MUTATION:
        carve the whole post-rewind block into R1_VESSEL_LOSS_EXEMPT_PHASES and a
        craft destroyed on the way up reads as a healthy re-flight."""
        st = drive_to_relaunch(reflyConclusionProfile=True)
        st, _ = mlib.r1_decide(st, snap(ut=330.0, vessel_lost=True))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn(mlib.R1_RELAUNCH, st.loss_reason)


class R1ReflyConclusionRowTests(unittest.TestCase):
    """The ninth row. ADDITIVE, never substituting: the profile drives every leg
    the eight existing rows describe, so a run that concludes still has to satisfy
    all of them."""

    def test_the_row_is_absent_with_the_key_off(self):
        """MUTATION: append it unconditionally and every committed R1 lane grows a
        row over a wait it never performed - which would fail every green flight."""
        st, _ = replay_conclusion()
        rows = mlib.evaluate_r1_assertions([], r1_params(), st)
        names = [r.name for r in rows]
        self.assertEqual(8, len(rows), names)
        self.assertNotIn("reflyConcludedInFlight", names)

    def test_the_row_is_appended_and_met_on_a_concluded_run(self):
        st, _ = replay_conclusion(reflyConclusionProfile=True)
        self.assertEqual(mlib.R1_CONCLUDED, st.phase)
        params = r1_params(reflyConclusionProfile=True)
        rows = mlib.evaluate_r1_assertions([], params, st)
        names = [r.name for r in rows]
        self.assertEqual(9, len(rows), names)
        self.assertEqual("reflyConcludedInFlight", names[-1])
        # The eight that were there before are unchanged, in order.
        self.assertEqual(
            ["reachedOrbitBeforeRewind", "treeCommittedBeforeRewind",
             "recorderIdleBeforeRewind", "clockRewound", "vesselStateChanged",
             "postRewindFlightObserved", "postRewindFlightRecordedSomewhere",
             "rewindSeamAccepted"], names[:-1])
        self.assertEqual([], [r.name for r in rows if not r.met],
                         [r.to_dict() for r in rows])
        row = rows[-1]
        self.assertEqual(mlib.R1_CONCLUSION_LANDED, row.value)
        self.assertEqual("LANDED", row.detail["observedSituation"])
        self.assertEqual(381.0, row.detail["concludedUT"])
        self.assertTrue(row.detail["airborneSeen"])
        self.assertEqual(mlib.R1_CONCLUSION_DEBOUNCE_K, row.detail["debounceK"])
        self.assertEqual("observed", row.detail["channel"])
        self.assertEqual(["LANDED", "SPLASHED"], row.detail["acceptedSituations"])

    def test_the_row_fails_when_no_conclusion_was_observed(self):
        """THE BOUND, read off the row rather than off the flake reason. MUTATION:
        make the row read `R1_CONCLUDED in phases` alone (or default it to met) and
        a re-flight that never ended reports a green conclusion."""
        st = drive_to_conclude_coast()
        params = r1_params(reflyConclusionProfile=True)
        rows = mlib.evaluate_r1_assertions([], params, st)
        row = [r for r in rows if r.name == "reflyConcludedInFlight"][0]
        self.assertFalse(row.met)
        self.assertIsNone(row.value)
        self.assertEqual("UNREAD", row.detail["observedSituation"])
        self.assertIsNone(row.detail["concludedUT"])

    def test_the_row_names_a_destroyed_outcome(self):
        st = drive_to_conclude_coast()
        for ut in (400.0, 401.0):
            st, _ = mlib.r1_decide(st, snap(ut=ut, vessel_lost=True))
        rows = mlib.evaluate_r1_assertions(
            [], r1_params(reflyConclusionProfile=True), st)
        row = [r for r in rows if r.name == "reflyConcludedInFlight"][0]
        self.assertTrue(row.met)
        self.assertEqual(mlib.R1_CONCLUSION_DESTROYED, row.value)
        # HONEST SCOPE: the mission observes that the FLIGHT ended, never that
        # Parsek stamped a TerminalState on the re-fly PROVISIONAL.
        self.assertIn("TerminalState", row.detail["doesNotProve"])


class R1ReflyConclusionConflictTests(unittest.TestCase):
    """The frame-1 conflict predicate: a params set the profile cannot honour dies
    before the launch click, not after an ascent AND a rewind."""

    def test_the_predicate_is_inert_on_every_lane_that_does_not_declare_it(self):
        conflict = mlib.r1_refly_conclusion_profile_conflict
        self.assertEqual("", conflict(r1_params()))
        self.assertEqual("", conflict(mlib.r1_params_from_dict({})))
        self.assertEqual("", conflict(r1_params(rewindPointId="",
                                                rewindPointSelect="first")))
        self.assertEqual("", conflict(r1_params(reflyConclusionProfile=False,
                                                reflyConclusionSituations=[])))
        # ...and the flag ALONE is enough: every knob it needs has a default.
        self.assertEqual("", conflict(r1_params(reflyConclusionProfile=True)))

    def test_an_empty_situation_list_is_refused_by_name_on_frame_one(self):
        """`[]` is a spec author saying "accept nothing". The params reader takes
        the key's own value rather than treating [] as absent precisely so this
        refusal lands on frame 1. MUTATION: restore an `or (...)` default in the
        reader and this cell reds because the machine flies happily with the
        default set the spec did not ask for."""
        p = r1_params(reflyConclusionProfile=True, reflyConclusionSituations=[])
        self.assertEqual((), p.refly_conclusion_situations)
        self.assertIn("EMPTY reflyConclusionSituations",
                      mlib.r1_refly_conclusion_profile_conflict(p))
        st = mlib.r1_initial_state(p)
        st, acts = mlib.r1_decide(st, snap(ut=0.0, situation="PRE_LAUNCH"))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.R1_ASCENT, st.flake_phase)
        self.assertEqual([], acts, "nothing is commanded on a refused params set")

    def test_a_bound_that_bounds_nothing_is_refused_by_name(self):
        p = r1_params(reflyConclusionProfile=True, reflyConclusionFrames=0)
        self.assertIn("reflyConclusionFrames=0",
                      mlib.r1_refly_conclusion_profile_conflict(p))
        st, acts = mlib.r1_decide(mlib.r1_initial_state(p),
                                  snap(ut=0.0, situation="PRE_LAUNCH"))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual([], acts)

    def test_the_pre_existing_target_guards_still_fire_first(self):
        """The new gate is placed LAST in the frame-1 block, so an existing lane's
        target fault still reads exactly the way it always did."""
        p = r1_params(rewindSlot=-1, reflyConclusionProfile=True,
                      reflyConclusionSituations=[])
        st, _ = mlib.r1_decide(mlib.r1_initial_state(p), snap(ut=0.0))
        self.assertIn("rewind target unresolved", st.flake_reason)


class R1ReflyConclusionPhaseBookkeepingTests(unittest.TestCase):
    """The phase tuples. The two new names are listed SEPARATELY from R1_PHASES
    because CL-3's identity claim reads that tuple as the graph every lane drives
    (`CL3_PHASES == R1_PHASES` minus ASCENT / COMMIT / RESOLVE), and these two are
    reachable only when a spec sets the flag."""

    def test_the_profile_phases_are_their_own_tuple_and_the_union_is_exact(self):
        self.assertEqual((mlib.R1_CONCLUDE_COAST, mlib.R1_CONCLUDED),
                         mlib.R1_CONCLUSION_PHASES)
        self.assertEqual(mlib.R1_PHASES + mlib.R1_CONCLUSION_PHASES,
                         mlib.R1_ALL_PHASES)
        self.assertEqual(len(set(mlib.R1_ALL_PHASES)), len(mlib.R1_ALL_PHASES),
                         "a duplicated phase NAME would make phases_reached lie")
        for phase in mlib.R1_CONCLUSION_PHASES:
            self.assertNotIn(phase, mlib.R1_PHASES)

    def test_only_the_conclusion_phase_is_exempt_from_a_lost_vessel(self):
        self.assertEqual((mlib.R1_REWIND, mlib.R1_CONCLUDE_COAST),
                         mlib.R1_VESSEL_LOSS_EXEMPT_PHASES)
        self.assertNotIn(mlib.R1_CONCLUDED, mlib.R1_VESSEL_LOSS_EXEMPT_PHASES)
        self.assertNotIn(mlib.R1_VERIFY, mlib.R1_VESSEL_LOSS_EXEMPT_PHASES)

    def test_the_schema_declares_the_flag_as_a_bounded_free_bool(self):
        """A bound on a bool reads as checked and checks nothing - the repo-wide
        sweep `test_bounds_are_only_declared_on_types_that_consult_them` reds on
        one, and this cell names the same rule at the point of authorship."""
        path = os.path.join(os.path.dirname(os.path.dirname(
            os.path.abspath(__file__))), "r1_rewind_loop.schema.toml")
        with open(path, "rb") as fh:
            schema = tomllib.load(fh)
        decl = schema["params"]["reflyConclusionProfile"]
        self.assertEqual("bool", decl["type"])
        self.assertFalse(decl["required"])
        self.assertNotIn("min", decl)
        self.assertNotIn("max", decl)
        frames = schema["params"]["reflyConclusionFrames"]
        self.assertEqual("int", frames["type"])
        self.assertEqual(1, frames["min"])
        self.assertEqual("list",
                         schema["params"]["reflyConclusionSituations"]["type"])

    def test_every_conclusion_key_the_reader_reads_is_declared(self):
        """AST, never a regex over the source text: a regex also sees comments, and
        this repo has been bitten by that three times. Walks the reader's own
        `params.get("<key>")` calls and requires a schema block for each - an
        undeclared key is never type-checked, so a spec typo lands as a silent
        default."""
        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                               "mlib.py"), encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        fn = next(n for n in ast.walk(tree)
                  if isinstance(n, ast.FunctionDef)
                  and n.name == "r1_params_from_dict")
        read = {n.args[0].value for n in ast.walk(fn)
                if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute)
                and n.func.attr == "get" and n.args
                and isinstance(n.args[0], ast.Constant)
                and isinstance(n.args[0].value, str)}
        conclusion_keys = {k for k in read if k.startswith("reflyConclusion")}
        self.assertEqual(
            {"reflyConclusionProfile", "reflyConclusionFrames",
             "reflyConclusionSituations"}, conclusion_keys)
        path = os.path.join(os.path.dirname(os.path.dirname(
            os.path.abspath(__file__))), "r1_rewind_loop.schema.toml")
        with open(path, "rb") as fh:
            declared = set(tomllib.load(fh)["params"])
        self.assertEqual(set(), conclusion_keys - declared,
                         "the machine reads a key the schema does not declare")


if __name__ == "__main__":
    unittest.main()
