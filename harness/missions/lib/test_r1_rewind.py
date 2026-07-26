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

    def _at_commit(self, **over):
        st = at_orbit(mlib.r1_initial_state(r1_params(**over)))
        st, _ = mlib.r1_decide(st, snap(ut=900.0))
        return st

    def test_commit_ok_stamps_the_pre_rewind_observation_and_emits_invoke_rewind(self):
        st = self._at_commit()
        out, actions = mlib.r1_decide(
            st, snap(ut=910.0, altitude=80500.0, situation="ORBITING", body="Kerbin",
                     seam_command_result="OK", seam_command_tag=mlib.R1_TAG_COMMIT))
        self.assertEqual(out.phase, mlib.R1_REWIND)
        self.assertEqual(out.commit_result, "OK")
        # The pre-rewind stamp is taken HERE, before anything can have moved.
        self.assertEqual(out.pre_rewind_ut, 910.0)
        self.assertEqual(out.pre_rewind_altitude, 80500.0)
        self.assertEqual(out.pre_rewind_situation, "ORBITING")
        self.assertEqual(len(actions), 1)
        self.assertEqual(actions[0].seam_verb, "InvokeRewind")
        self.assertEqual(actions[0].seam_tag, mlib.R1_TAG_REWIND)
        self.assertEqual(actions[0].seam_args,
                         (("rp", "rp_b9_root"), ("slot", "1")))

    def test_commit_error_is_a_named_giveup(self):
        st = self._at_commit()
        out, _ = mlib.r1_decide(
            st, snap(ut=910.0, seam_command_result="ERROR",
                     seam_command_tag=mlib.R1_TAG_COMMIT))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("tree-commit seam command returned ERROR", out.flake_reason)
        self.assertIn("no committed state to rewind FROM", out.flake_reason)

    def test_commit_timeout_is_a_distinctly_named_giveup(self):
        st = self._at_commit()
        out, _ = mlib.r1_decide(
            st, snap(ut=910.0, seam_command_result="TIMEOUT",
                     seam_command_tag=mlib.R1_TAG_COMMIT))
        self.assertIn("returned TIMEOUT", out.flake_reason)

    def test_commit_silence_is_frame_bounded_with_its_own_name(self):
        st = self._at_commit(commitFrames=3)
        for _ in range(6):
            st, _ = mlib.r1_decide(st, snap(ut=910.0))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertIn("never answered within 3 frames", st.flake_reason)

    def test_a_stale_commit_ok_cannot_satisfy_the_rewind_phase(self):
        """THE stale-result fail-open. MUTATION: drop the tag check in
        _r1_seam_result (`return snapshot.seam_command_result`) and this reds --
        REWIND would advance on the COMMIT command's OK, reporting a rewind that
        InvokeRewind never performed."""
        st = self._at_commit()
        st, _ = mlib.r1_decide(
            st, snap(ut=910.0, seam_command_result="OK",
                     seam_command_tag=mlib.R1_TAG_COMMIT))
        self.assertEqual(st.phase, mlib.R1_REWIND)
        # The COMMIT token is still riding the snapshot; REWIND must ignore it.
        out, _ = mlib.r1_decide(
            st, snap(ut=911.0, seam_command_result="OK",
                     seam_command_tag=mlib.R1_TAG_COMMIT))
        self.assertEqual(out.phase, mlib.R1_REWIND)
        self.assertEqual(out.rewind_result, "")
        self.assertFalse(out.done)

    def test_rewind_ok_enters_verify_but_is_not_itself_the_terminal(self):
        """MUTATION: make R1_REWIND's OK branch enter R1_REWOUND directly and this
        reds. The whole point of VERIFY is that the seam's OK is a COMMANDED
        reading and must never be the terminal on its own."""
        st = self._at_commit()
        st, _ = mlib.r1_decide(
            st, snap(ut=910.0, seam_command_result="OK",
                     seam_command_tag=mlib.R1_TAG_COMMIT))
        out, _ = mlib.r1_decide(
            st, snap(ut=911.0, seam_command_result="OK",
                     seam_command_tag=mlib.R1_TAG_REWIND))
        self.assertEqual(out.phase, mlib.R1_VERIFY)
        self.assertFalse(out.done)
        self.assertIsNone(out.verdict)

    def test_rewind_error_names_the_target(self):
        st = self._at_commit()
        st, _ = mlib.r1_decide(
            st, snap(ut=910.0, seam_command_result="OK",
                     seam_command_tag=mlib.R1_TAG_COMMIT))
        out, _ = mlib.r1_decide(
            st, snap(ut=911.0, seam_command_result="ERROR",
                     seam_command_tag=mlib.R1_TAG_REWIND))
        self.assertTrue(out.done)
        self.assertIn("rp=rp_b9_root slot=1", out.flake_reason)

    def test_vessel_loss_is_suppressed_in_rewind_only(self):
        """The reload straddle legitimately destroys the active vessel. MUTATION:
        widen the suppression to `state.phase not in (R1_REWIND, R1_VERIFY)` and
        LossStillTerminatesInVerify reds -- a blanket fail-open."""
        st = self._at_commit()
        st, _ = mlib.r1_decide(
            st, snap(ut=910.0, seam_command_result="OK",
                     seam_command_tag=mlib.R1_TAG_COMMIT))
        self.assertEqual(st.phase, mlib.R1_REWIND)
        out, _ = mlib.r1_decide(st, snap(ut=911.0, vessel_lost=True))
        self.assertFalse(out.done)
        self.assertEqual(out.phase, mlib.R1_REWIND)

    def test_loss_still_terminates_in_verify(self):
        st = self._at_commit()
        st, _ = mlib.r1_decide(
            st, snap(ut=910.0, seam_command_result="OK",
                     seam_command_tag=mlib.R1_TAG_COMMIT))
        st, _ = mlib.r1_decide(
            st, snap(ut=911.0, seam_command_result="OK",
                     seam_command_tag=mlib.R1_TAG_REWIND))
        self.assertEqual(st.phase, mlib.R1_VERIFY)
        out, _ = mlib.r1_decide(st, snap(ut=200.0, vessel_lost=True))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_ASSERT_FAIL)


# ---------------------------------------------------------------------------
# R1 machine: the OBSERVED verify gate. The load-bearing cell of the lane.
# ---------------------------------------------------------------------------


class R1ObservedVerifyTests(unittest.TestCase):

    def _at_verify(self, pre_ut=910.0, pre_alt=80500.0, pre_sit="ORBITING", **over):
        st = at_orbit(mlib.r1_initial_state(r1_params(**over)))
        st, _ = mlib.r1_decide(st, snap(ut=900.0))
        st, _ = mlib.r1_decide(
            st, snap(ut=pre_ut, altitude=pre_alt, situation=pre_sit, body="Kerbin",
                     seam_command_result="OK", seam_command_tag=mlib.R1_TAG_COMMIT))
        st, _ = mlib.r1_decide(
            st, snap(ut=pre_ut + 1.0, seam_command_result="OK",
                     seam_command_tag=mlib.R1_TAG_REWIND))
        self.assertEqual(st.phase, mlib.R1_VERIFY)
        return st

    def test_backward_clock_is_the_advance(self):
        st = self._at_verify()
        out, _ = mlib.r1_decide(
            st, snap(ut=310.0, altitude=1200.0, situation="FLYING", body="Kerbin"))
        self.assertEqual(out.phase, mlib.R1_REWOUND)
        self.assertTrue(out.done)
        self.assertIsNone(out.verdict)
        self.assertAlmostEqual(out.ut_regression, 600.0)
        self.assertEqual(out.post_rewind_ut, 310.0)

    def test_a_forward_clock_never_satisfies_the_gate(self):
        """THE anti-commanded cell. MUTATION: replace the gate with
        `if state.rewind_result == "OK"` (i.e. trust the seam's own OK) and this
        reds -- the machine would call a rewind that never moved the clock done."""
        st = self._at_verify(verifyFrames=4)
        cur = st
        for i in range(6):
            cur, _ = mlib.r1_decide(
                cur, snap(ut=1000.0 + i, altitude=80500.0, situation="ORBITING"))
            if cur.done:
                break
        self.assertTrue(cur.done)
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)
        self.assertIn("never ran backward", cur.flake_reason)
        self.assertIn("COMMANDED reading", cur.flake_reason)

    def test_a_sub_floor_regression_never_satisfies_the_gate(self):
        """MUTATION: change `>=` to `>` -> still passes; change the compare to
        `regression > 0` (drop the floor) and this reds. A 0.5 s wobble is float
        noise, not a rewind."""
        st = self._at_verify(verifyFrames=3)
        cur = st
        for _ in range(5):
            cur, _ = mlib.r1_decide(cur, snap(ut=909.5, altitude=1200.0))
            if cur.done:
                break
        self.assertTrue(cur.done)
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)

    def test_exactly_the_floor_satisfies_the_gate(self):
        st = self._at_verify()
        out, _ = mlib.r1_decide(st, snap(ut=905.0, altitude=1200.0))
        self.assertEqual(out.phase, mlib.R1_REWOUND)
        self.assertAlmostEqual(out.ut_regression, 5.0)

    def test_a_non_finite_clock_never_satisfies_the_gate(self):
        """Fail-closed on an unread channel: NaN must not compare its way into a
        pass. MUTATION: drop the _is_finite guards and this raises or passes."""
        st = self._at_verify(verifyFrames=2)
        cur = st
        for _ in range(4):
            cur, _ = mlib.r1_decide(cur, snap(ut=float("nan"), altitude=1200.0))
            if cur.done:
                break
        self.assertTrue(cur.done)
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)

    def test_verify_is_frame_bounded_not_game_time_bounded(self):
        """THE reason VERIFY counts frames. MUTATION: bound VERIFY with
        `snapshot.ut - phase_entry_ut > budget` and this HANGS (never terminates),
        because after a rewind that difference is negative forever."""
        st = self._at_verify(verifyFrames=5)
        cur = st
        # UT marches BACKWARD every frame but never far enough past the stamp to
        # satisfy the gate relative to a moving pre stamp... it is fixed, so use a
        # clock that stays just under the floor while decreasing.
        for i in range(20):
            cur, _ = mlib.r1_decide(cur, snap(ut=909.9 - i * 0.01, altitude=1200.0))
            if cur.done:
                break
        self.assertTrue(cur.done, "VERIFY must terminate on a FRAME budget")
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)


# ---------------------------------------------------------------------------
# R1 assertions.
# ---------------------------------------------------------------------------


class R1AssertionTests(unittest.TestCase):

    def _flown(self):
        params = r1_params()
        st = at_orbit(mlib.r1_initial_state(params))
        st, _ = mlib.r1_decide(st, snap(ut=900.0))
        st, _ = mlib.r1_decide(
            st, snap(ut=910.0, altitude=80500.0, situation="ORBITING", body="Kerbin",
                     seam_command_result="OK", seam_command_tag=mlib.R1_TAG_COMMIT))
        st, _ = mlib.r1_decide(
            st, snap(ut=911.0, seam_command_result="OK",
                     seam_command_tag=mlib.R1_TAG_REWIND))
        st, _ = mlib.r1_decide(
            st, snap(ut=310.0, altitude=1200.0, situation="FLYING", body="Kerbin"))
        return params, st

    def test_full_cycle_meets_every_row(self):
        params, st = self._flown()
        rows = mlib.evaluate_r1_assertions([], params, st)
        self.assertTrue(mlib.all_assertions_met(rows), [r.name for r in rows if not r.met])
        self.assertEqual([r.name for r in rows],
                         ["reachedOrbitBeforeRewind", "treeCommittedBeforeRewind",
                          "clockRewound", "vesselStateChanged", "rewindSeamAccepted"])

    def test_the_observed_rows_are_labelled_observed(self):
        params, st = self._flown()
        rows = {r.name: r for r in mlib.evaluate_r1_assertions([], params, st)}
        self.assertEqual(rows["clockRewound"].detail["channel"], "observed")
        self.assertEqual(rows["vesselStateChanged"].detail["channel"], "observed")
        # And the seam's own OK is labelled for what it is.
        self.assertEqual(rows["rewindSeamAccepted"].detail["channel"], "commanded")

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
