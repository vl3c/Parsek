"""Unit cells for the KX-REWIND-WATCH lane (mission `kx_rewind_watch`).

Six groups, each guarding a different way this lane can be silently wrong:

  1. THE STAGING DISCIPLINE. The fueled-core discard must be gated on an OBSERVED
     zero throttle and on nothing else. Pinned in BOTH directions: a readback that
     never reaches zero must NOT produce a stage click, and a commanded cut alone
     must NOT satisfy the assertion row.
  2. THE SEAM BRIDGE SEQUENCING. RecordingState BEFORE CommitTree (the tree id has
     to be captured while a recorder is live), the captured id actually reaching
     the rewind's args, the poll-until-idle loop issuing a FRESH tag per probe, and
     the tag gate refusing a previous command's OK.
  3. SCENE-RELOAD TOLERANCE. `vessel_lost` is the NORMAL reading from the rewind
     until the watcher is on the pad, and it must be lethal everywhere before that.
  4. THE RECORD-DO-NOT-FAIL RULE. A REJECTED EnterWatchMode is recorded and flown
     past; a SILENT one is a transport fault and flakes.
  5. THE PLAYBACK-WAIT ARITHMETIC AND ITS CAP.
  6. CRAFT / SCHEMA / SHELL SYNC. The staging plan is a fact about
     `harness/fixtures/ships/Kerbal X.craft`, and every param the machine reads
     has to be a key the schema declares - both derived MECHANICALLY (a craft
     parse, an AST walk) rather than from a hand-copied list.

NO krpc, NO KSP, NO network. Import path matches the sibling suites: discovery
runs from `harness/` with `missions/lib` as the root, and `missions/` is prepended
so `import mission_runner` / `import kx_rewind_watch` resolve.
"""

import ast
import dataclasses
import math
import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if os.path.join(_HARNESS, "lib") not in sys.path:
    sys.path.insert(0, os.path.join(_HARNESS, "lib"))

import mlib                        # noqa: E402
import kx_rewind_watch             # noqa: E402

SCHEMA_PATH = os.path.join(_MISSIONS, "kx_rewind_watch.schema.toml")
CRAFT_PATH = os.path.join(_HARNESS, "fixtures", "ships", "Kerbal X.craft")
# The WATCHER craft, which lives in the lane's save template rather than the
# shared ships dir. Read by a cell below: its `ship = ` line is the name the live
# WATCHER-READY gate has to match, and stock shipped it as a #autoLOC token.
WATCHER_CRAFT_PATH = os.path.join(_HARNESS, "fixtures", "saves",
                                  "gs1-two-stage-pad", "Ships", "VAB",
                                  "Jumping Flea.craft")
MLIB_PATH = os.path.join(_HERE, "mlib.py")

# Live available thrust of the seven-engine Kerbal X stack, near enough: the only
# property any cell depends on is that it is orders of magnitude above the
# live-engine floor, and that the "flamed out" reading below is under the
# fraction of it.
LIT = 2400000.0
FLAMED = 1300000.0


def params(**over):
    """A missionParams dict with only what a cell exercises; everything else takes
    the machine's own default (which is what a spec omitting the key gets)."""
    out = {}
    out.update(over)
    return out


CRAFT = "Kerbal X"
WATCHER = "Jumping Flea"


def machine(**over):
    return mlib.kxrw_initial_state(mlib.kxrw_params_from_dict(params(**over)))


def rolled_out(state, name=CRAFT, debounce=2):
    """Drive a fresh machine through ROLLOUT to PRELAUNCH: one launch_vessel click,
    then `debounce` frames of the active vessel reading back ``name`` in
    PRE_LAUNCH. Every cell below that is not ABOUT the rollout starts here, because
    the rollout is now the machine's first phase."""
    state, acts = mlib.kxrw_decide(state, snap(ut=0.0, situation="PRE_LAUNCH"))
    assert [a.kind for a in acts] == [mlib.ACTION_LAUNCH_VESSEL], acts
    for i in range(max(1, debounce)):
        state, _ = mlib.kxrw_decide(
            state, snap(ut=0.0, situation="PRE_LAUNCH", vessel_name=name))
    assert state.phase == mlib.KXRW_PRELAUNCH, state.phase
    return state


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


def fly(state, **kw):
    """One decide frame with the readings a nominal powered ascent carries."""
    kw.setdefault("available_thrust", LIT)
    kw.setdefault("throttle", 1.0)
    kw.setdefault("situation", "FLYING")
    return mlib.kxrw_decide(state, snap(**kw))


def kinds(actions):
    return [a.kind for a in actions]


def seam(tag, result="OK", payload=(), **kw):
    """A snapshot carrying ONE terminal seam reply, tagged."""
    kw.setdefault("seam_command_tag", tag)
    kw.setdefault("seam_command_result", result)
    kw.setdefault("seam_command_payload", tuple(payload))
    return snap(**kw)


class GravityTurnProgramTests(unittest.TestCase):
    """The pure steering program. Pinned because it is the one place a NaN
    altitude could turn into a commanded attitude."""

    def test_vertical_below_the_turn_start_and_final_above_the_turn_end(self):
        self.assertEqual(90.0, mlib.kxrw_gravity_turn_pitch(0.0, 1000.0, 45000.0, 25.0))
        self.assertEqual(90.0, mlib.kxrw_gravity_turn_pitch(1000.0, 1000.0, 45000.0, 25.0))
        self.assertEqual(25.0, mlib.kxrw_gravity_turn_pitch(45000.0, 1000.0, 45000.0, 25.0))
        self.assertEqual(25.0, mlib.kxrw_gravity_turn_pitch(90000.0, 1000.0, 45000.0, 25.0))

    def test_the_ramp_is_linear_in_between(self):
        mid = mlib.kxrw_gravity_turn_pitch(23000.0, 1000.0, 45000.0, 25.0)
        self.assertAlmostEqual(57.5, mid, places=6)

    def test_an_unreadable_altitude_commands_nothing(self):
        """MUTATION: return 90.0 on NaN instead of None and the AP is steered from
        a garbage reading on every faulted poll."""
        self.assertIsNone(
            mlib.kxrw_gravity_turn_pitch(float("nan"), 1000.0, 45000.0, 25.0))

    def test_a_degenerate_window_does_not_divide_by_zero(self):
        """end == start: at and below the start it is still vertical, ABOVE it
        snaps straight to the final pitch. No ZeroDivisionError either way."""
        self.assertEqual(90.0,
                         mlib.kxrw_gravity_turn_pitch(5000.0, 45000.0, 45000.0, 25.0))
        self.assertEqual(90.0,
                         mlib.kxrw_gravity_turn_pitch(45000.0, 45000.0, 45000.0, 25.0))
        self.assertEqual(25.0,
                         mlib.kxrw_gravity_turn_pitch(60000.0, 45000.0, 45000.0, 25.0))

    def test_the_command_is_suppressed_until_the_pitch_actually_moves(self):
        """The step gate is a COST knob; a machine that re-commanded every poll
        would spend an RPC a frame on hundredths of a degree."""
        st = rolled_out(machine(pitchCommandStepDeg=2.0))
        st, acts = mlib.kxrw_decide(st, snap(ut=0.0, altitude=0.0, throttle=0.0,
                                             available_thrust=LIT))
        self.assertIn(mlib.ACTION_AP_SET_PITCH_HEADING, kinds(acts))
        st, acts = fly(st, ut=1.0, altitude=10.0)      # still 90 deg
        self.assertNotIn(mlib.ACTION_AP_SET_PITCH_HEADING, kinds(acts))
        st, acts = fly(st, ut=2.0, altitude=5000.0)    # ~82 deg: past the step
        self.assertIn(mlib.ACTION_AP_SET_PITCH_HEADING, kinds(acts))
        cmd = [a for a in acts if a.kind == mlib.ACTION_AP_SET_PITCH_HEADING][0]
        self.assertEqual(2, len(cmd.pitch_heading))
        self.assertEqual(90.0, cmd.pitch_heading[1])   # the declared heading


class StagingDisciplineTests(unittest.TestCase):
    """THE ROW THIS LANE EXISTS TO PROTECT: the still-fueled Mainsail core is
    discarded only after the throttle has been READ at zero."""

    def _to_ascent(self, **over):
        # The PRELAUNCH frame reads ZERO available thrust, which is what a craft
        # whose engines have not been lit yet actually reports - and it is the frame
        # the peak tracker sees first, so seeding it with a live reading here would
        # hide the ignition-frame trap the next cell exists for.
        st = rolled_out(machine(**over))
        st, _ = mlib.kxrw_decide(st, snap(ut=0.0, altitude=0.0, throttle=0.0,
                                          available_thrust=0.0))
        self.assertEqual(mlib.KXRW_ASCENT, st.phase)
        return st

    def test_prelaunch_fires_exactly_one_stage_and_stamps_the_launch_ut(self):
        st = rolled_out(machine())
        st, acts = mlib.kxrw_decide(st, snap(ut=1234.0, altitude=0.0, throttle=0.0,
                                             available_thrust=0.0))
        self.assertEqual(1, kinds(acts).count(mlib.ACTION_ACTIVATE_STAGE))
        self.assertIn(mlib.ACTION_SET_THROTTLE, kinds(acts))
        self.assertEqual(1234.0, st.launch_ut)

    def test_the_ignition_frame_zero_thrust_does_not_arm_a_booster_drop(self):
        """The live-engine floor is the 'there WAS an engine' half. MUTATION: drop
        the floor conjunct and `0 <= 0 * fraction` drops the boosters on the pad."""
        st = self._to_ascent()
        st, acts = fly(st, ut=1.0, altitude=5.0, available_thrust=0.0)
        self.assertEqual(mlib.KXRW_ASCENT, st.phase)
        self.assertNotIn(mlib.ACTION_CUT_THROTTLE, kinds(acts))

    def test_an_observed_flameout_cuts_the_throttle_and_only_then_stages(self):
        """GS-1's rule: the settle wait is the ONLY thing between the cut and the
        decoupler, so the flame-out frame emits a CUT and NOTHING else."""
        st = self._to_ascent(stageSettleFrames=2)
        st, _ = fly(st, ut=1.0, altitude=100.0)                 # peak = LIT
        st, acts = fly(st, ut=40.0, altitude=9000.0, available_thrust=FLAMED)
        self.assertEqual(mlib.KXRW_BOOSTER_CUT, st.phase)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE], kinds(acts))
        self.assertEqual("thrust", st.booster_drop_armed_by)
        # Settle frame 1: held but not yet staged.
        st, acts = fly(st, ut=40.5, altitude=9100.0, throttle=0.0,
                       available_thrust=FLAMED)
        self.assertEqual(mlib.KXRW_BOOSTER_CUT, st.phase)
        self.assertEqual([], acts)
        # Settle frame 2: the stage click goes out.
        st, acts = fly(st, ut=41.0, altitude=9200.0, throttle=0.0,
                       available_thrust=FLAMED)
        self.assertEqual(mlib.KXRW_BOOSTER_STAGE, st.phase)
        st, acts = fly(st, ut=41.5, altitude=9300.0, throttle=0.0,
                       available_thrust=FLAMED)
        self.assertEqual([mlib.ACTION_ACTIVATE_STAGE], kinds(acts))
        self.assertEqual(1, st.booster_drops_done)

    def test_the_core_is_never_staged_while_the_throttle_still_reads_live(self):
        """MUTATION: gate CORE-CUT on the settle frames alone (drop the
        `core_cut_throttle_observed` conjunct) and this reds - a live Mainsail is
        staged into the stack above it."""
        st = self._to_ascent(stageSettleFrames=2, boosterStageCount=0,
                             coreDiscardApoapsisMeters=60000.0, stageCutFrames=6)
        st, acts = fly(st, ut=100.0, altitude=50000.0, apoapsis=61000.0)
        self.assertEqual(mlib.KXRW_CORE_CUT, st.phase)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_AP_DISENGAGE],
                         kinds(acts))
        # The throttle NEVER reads zero. No stage click may ever go out.
        emitted = []
        for i in range(12):
            st, acts = fly(st, ut=101.0 + i, altitude=50000.0, apoapsis=61000.0,
                           throttle=0.9)
            emitted += kinds(acts)
            if st.done:
                break
        self.assertNotIn(mlib.ACTION_ACTIVATE_STAGE, emitted)
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("refusing to discard the FUELED core", st.flake_reason)
        self.assertFalse(st.core_discard_commanded)

    def test_an_unread_throttle_is_not_zero(self):
        """NaN fails the gate CLOSED. MUTATION: use `throttle <= eps` without the
        finiteness check and a NaN compares False anyway - but `abs(nan) <= eps` is
        also False, so the real mutation is treating an UNREAD channel as 0.0."""
        self.assertFalse(mlib.kxrw_throttle_is_zero(float("nan"), 0.01))
        self.assertTrue(mlib.kxrw_throttle_is_zero(0.0, 0.01))
        self.assertTrue(mlib.kxrw_throttle_is_zero(0.005, 0.01))
        self.assertFalse(mlib.kxrw_throttle_is_zero(0.5, 0.01))

    def test_the_core_stages_once_the_throttle_is_observed_off(self):
        st = self._to_ascent(stageSettleFrames=2, boosterStageCount=0)
        st, _ = fly(st, ut=100.0, altitude=50000.0, apoapsis=61000.0)
        st, _ = fly(st, ut=101.0, altitude=50000.0, apoapsis=61000.0, throttle=0.0)
        st, acts = fly(st, ut=102.0, altitude=50100.0, apoapsis=61000.0, throttle=0.0)
        self.assertEqual(mlib.KXRW_CORE_DISCARD, st.phase)
        self.assertTrue(st.core_cut_throttle_observed)
        st, acts = fly(st, ut=103.0, altitude=50200.0, apoapsis=61000.0, throttle=0.0)
        self.assertEqual([mlib.ACTION_ACTIVATE_STAGE], kinds(acts))
        self.assertEqual(mlib.KXRW_COAST, st.phase)
        self.assertTrue(st.core_discard_commanded)
        self.assertEqual(50200.0, st.core_discard_altitude)

    def test_the_assertion_row_reads_the_observed_latch_not_the_commanded_one(self):
        """A hand-built state with the stage COMMANDED but the throttle never
        observed off must NOT satisfy the row."""
        p = mlib.kxrw_params_from_dict(params())
        st = dataclasses.replace(machine(), core_discard_commanded=True,
                                 core_cut_throttle_observed=False)
        row = [r for r in mlib.evaluate_kxrw_assertions([], p, st)
               if r.name == "coreDiscardedWithEnginesOff"][0]
        self.assertFalse(row.met)
        self.assertTrue(row.detail["stageCommanded"])
        self.assertFalse(row.detail["throttleObservedZero"])

    def test_the_core_gate_is_held_while_a_booster_drop_is_still_outstanding(self):
        """STAGING IS POSITIONAL. `activate_next_stage` fires whatever istg is
        next, so with drops still owed CORE-DISCARD's single click lands on a
        radial BOOSTER pair - the fueled core stays bolted on and
        `coreDiscardedWithEnginesOff` records TRUE against a separation that never
        happened. MUTATION: consult the core gate with drops outstanding (the shape
        before this fix) and the machine enters CORE-CUT here instead of steering
        on.

        The apoapsis is ALREADY past the discard threshold on every frame below,
        and no booster drop has been armed - the thrust channel never falls and
        both backstops are pushed out of reach - so the ONLY thing holding the core
        gate is the drops-outstanding conjunct."""
        st = self._to_ascent(stageSettleFrames=2, boosterStageCount=3,
                             coreDiscardApoapsisMeters=60000.0,
                             boosterDropBackstopAltitudeMeters=1e12,
                             boosterDropBackstopSeconds=1e12)
        st, _ = fly(st, ut=1.0, altitude=100.0)                  # peak = LIT
        for i in range(6):
            st, acts = fly(st, ut=10.0 + i, altitude=9000.0, apoapsis=61000.0)
            self.assertEqual(mlib.KXRW_ASCENT, st.phase)
            self.assertNotIn(mlib.ACTION_CUT_THROTTLE, kinds(acts))
            self.assertNotIn(mlib.ACTION_ACTIVATE_STAGE, kinds(acts))
        self.assertEqual(0, st.booster_drops_done)
        self.assertNotIn(mlib.KXRW_CORE_CUT, st.phases_reached)

    def test_the_core_gate_opens_the_moment_the_last_drop_is_done(self):
        """The mirror direction: the hold is not a block. Once the declared drops
        are made, an apoapsis already past the threshold takes the machine straight
        into CORE-CUT on the next ASCENT frame."""
        st = self._to_ascent(stageSettleFrames=2, boosterStageCount=1,
                             coreDiscardApoapsisMeters=60000.0)
        st, _ = fly(st, ut=1.0, altitude=100.0)                  # peak = LIT
        # One observed flame-out -> cut -> settle -> the single declared drop.
        st, _ = fly(st, ut=40.0, altitude=9000.0, available_thrust=FLAMED,
                    apoapsis=61000.0)
        self.assertEqual(mlib.KXRW_BOOSTER_CUT, st.phase)
        for ut in (40.5, 41.0):
            st, _ = fly(st, ut=ut, altitude=9000.0, throttle=0.0,
                        available_thrust=FLAMED, apoapsis=61000.0)
        self.assertEqual(mlib.KXRW_BOOSTER_STAGE, st.phase)
        st, acts = fly(st, ut=41.5, altitude=9000.0, throttle=0.0,
                       available_thrust=FLAMED, apoapsis=61000.0)
        self.assertEqual(1, st.booster_drops_done)
        self.assertEqual(mlib.KXRW_ASCENT, st.phase)
        # Drops done, apoapsis past the threshold: the core gate is now live.
        st, acts = fly(st, ut=42.0, altitude=9000.0, throttle=0.0,
                       available_thrust=FLAMED, apoapsis=61000.0)
        self.assertEqual(mlib.KXRW_CORE_CUT, st.phase)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_AP_DISENGAGE],
                         kinds(acts))

    def test_the_held_core_gate_cannot_deadlock_the_ascent(self):
        """The hold is safe ONLY because the drop has backstops of its own: a
        craft whose thrust channel never moves still sheds its stages on altitude
        or on the clock. MUTATION: delete both backstops and this hangs."""
        st = self._to_ascent(stageSettleFrames=1, boosterStageCount=3,
                             boosterDropBackstopSeconds=30.0,
                             coreDiscardApoapsisMeters=60000.0)
        st, _ = fly(st, ut=1.0, altitude=100.0)
        ut = 2.0
        alt = 9000.0
        for _ in range(60):
            # Thrust NEVER falls: only the clock backstop can arm a drop. The
            # altitude still climbs, or the frozen-telemetry detector (rightly)
            # reads a bit-identical stream as a destroyed craft.
            st, _ = fly(st, ut=ut, altitude=alt, throttle=0.0, apoapsis=61000.0)
            ut += 1.0
            alt += 50.0
            if st.phase == mlib.KXRW_CORE_CUT or st.done:
                break
        self.assertFalse(st.done)
        self.assertEqual(3, st.booster_drops_done)
        self.assertEqual(mlib.KXRW_CORE_CUT, st.phase)
        self.assertEqual("clock", st.booster_drop_armed_by)

    def test_every_declared_booster_stage_produces_exactly_one_stage_click(self):
        st = self._to_ascent(stageSettleFrames=2, boosterStageCount=3,
                             coreDiscardApoapsisMeters=1e12,
                             coreDiscardMaxFlightSeconds=1e12)
        st, _ = fly(st, ut=1.0, altitude=100.0)
        clicks = 0
        ut = 40.0
        for _ in range(40):
            st, acts = fly(st, ut=ut, altitude=9000.0, throttle=0.0,
                           available_thrust=FLAMED)
            clicks += kinds(acts).count(mlib.ACTION_ACTIVATE_STAGE)
            ut += 0.5
            if st.phase == mlib.KXRW_ASCENT and st.booster_drops_done >= 3:
                break
        self.assertEqual(3, st.booster_drops_done)
        self.assertEqual(3, clicks)
        # The last drop restores the throttle so the core keeps climbing.
        self.assertIn(mlib.ACTION_SET_THROTTLE, kinds(acts))


class SeamBridgeSequencingTests(unittest.TestCase):
    """RecordingState -> CommitTree -> StopRecording -> poll-until-idle ->
    InvokeRewindToLaunch, and the tag gate that keeps one reply from satisfying
    the next phase."""

    def _to_tree_state(self, **over):
        over.setdefault("boosterStageCount", 0)
        over.setdefault("stageSettleFrames", 1)
        over.setdefault("coastSeconds", 5.0)
        st = rolled_out(machine(**over))
        st, _ = mlib.kxrw_decide(st, snap(ut=0.0, altitude=0.0, throttle=0.0,
                                          available_thrust=LIT))
        st, _ = fly(st, ut=100.0, altitude=50000.0, apoapsis=61000.0, throttle=0.0)
        st, _ = fly(st, ut=101.0, altitude=50000.0, apoapsis=61000.0, throttle=0.0)
        self.assertEqual(mlib.KXRW_CORE_DISCARD, st.phase)
        st, _ = fly(st, ut=102.0, altitude=50000.0, apoapsis=61000.0, throttle=0.0)
        self.assertEqual(mlib.KXRW_COAST, st.phase)
        st, acts = fly(st, ut=110.0, altitude=51000.0, apoapsis=61000.0, throttle=0.0)
        self.assertEqual(mlib.KXRW_TREE_STATE, st.phase)
        return st, acts

    def test_the_tree_id_is_read_before_the_commit_is_issued(self):
        """ORDERING, and it is not cosmetic: after the commit there is no live
        recorder guaranteed to name a tree, and InvokeRewindToLaunch is REJECTED
        `unknown-tree` without one."""
        st, acts = self._to_tree_state()
        self.assertEqual(1, len(acts))
        self.assertEqual("RecordingState", acts[0].seam_verb)
        self.assertEqual("tree0", acts[0].seam_tag)
        st, acts = mlib.kxrw_decide(
            st, seam("tree0", "OK", (("tree", "t_abc123"), ("recording", "true")),
                     ut=111.0))
        self.assertEqual("t_abc123", st.tree_id)
        self.assertEqual(mlib.KXRW_COMMIT, st.phase)
        self.assertEqual(["CommitTree"], [a.seam_verb for a in acts])

    def test_a_reply_with_no_tree_field_reprobes_under_a_fresh_tag(self):
        """A reused tag is a reused wire id, and the C# seam SKIPS duplicate ids -
        every probe after the first would be silently dropped."""
        st, _ = self._to_tree_state()
        st, acts = mlib.kxrw_decide(st, seam("tree0", "OK", (), ut=111.0))
        self.assertEqual(mlib.KXRW_TREE_STATE, st.phase)
        self.assertEqual(["tree1"], [a.seam_tag for a in acts])
        st, acts = mlib.kxrw_decide(st, seam("tree1", "OK", (("tree", "t_x"),),
                                             ut=112.0))
        self.assertEqual("t_x", st.tree_id)

    def test_a_previous_commands_ok_never_advances_the_next_phase(self):
        """THE TAG GATE, fail-closed. MUTATION: drop the tag check in
        `_kxrw_seam_result` and COMMIT advances on the TREE-STATE reply."""
        st, _ = self._to_tree_state()
        st, _ = mlib.kxrw_decide(st, seam("tree0", "OK", (("tree", "t1"),), ut=111.0))
        self.assertEqual(mlib.KXRW_COMMIT, st.phase)
        # The tree0 OK is still riding the snapshot. COMMIT must not read it.
        st, acts = mlib.kxrw_decide(st, seam("tree0", "OK", (("tree", "t1"),),
                                             ut=112.0))
        self.assertEqual(mlib.KXRW_COMMIT, st.phase)
        self.assertEqual([], acts)
        self.assertEqual("", st.commit_result)

    def test_the_commit_ok_stamps_the_recorded_end_ut(self):
        st, _ = self._to_tree_state()
        st, _ = mlib.kxrw_decide(st, seam("tree0", "OK", (("tree", "t1"),), ut=111.0))
        st, acts = mlib.kxrw_decide(st, seam("commit", "OK", (), ut=222.0))
        self.assertEqual(222.0, st.recording_end_ut)
        self.assertEqual(mlib.KXRW_STOP, st.phase)
        self.assertEqual(["StopRecording"], [a.seam_verb for a in acts])

    def test_the_rewind_is_never_commanded_while_the_recorder_reads_live(self):
        """The dispatcher REJECTS `recording-active`. Ordering alone is an
        assumption; this is a reading."""
        st, _ = self._to_tree_state(idleFrames=6)
        st, _ = mlib.kxrw_decide(st, seam("tree0", "OK", (("tree", "t1"),), ut=111.0))
        st, _ = mlib.kxrw_decide(st, seam("commit", "OK", (), ut=222.0))
        st, acts = mlib.kxrw_decide(st, seam("stop", "OK", (), ut=223.0))
        self.assertEqual(mlib.KXRW_RECORDER_IDLE, st.phase)
        self.assertEqual(["idle0"], [a.seam_tag for a in acts])
        # recording=true -> re-probe with a FRESH tag; never a rewind.
        st, acts = mlib.kxrw_decide(
            st, seam("idle0", "OK", (("recording", "true"),), ut=224.0))
        self.assertEqual(mlib.KXRW_RECORDER_IDLE, st.phase)
        self.assertEqual(["RecordingState"], [a.seam_verb for a in acts])
        self.assertEqual(["idle1"], [a.seam_tag for a in acts])
        # recording=false -> and only now the rewind, carrying the captured tree.
        st, acts = mlib.kxrw_decide(
            st, seam("idle1", "OK", (("recording", "false"),), ut=225.0))
        self.assertEqual(mlib.KXRW_REWIND, st.phase)
        self.assertEqual(1, len(acts))
        self.assertEqual("InvokeRewindToLaunch", acts[0].seam_verb)
        self.assertEqual((("tree", "t1"),), acts[0].seam_args)
        self.assertEqual(225.0, st.pre_rewind_ut)

    def test_an_unreadable_recording_field_refuses_to_command_the_rewind(self):
        """FAIL CLOSED: an unverified gate is not permission to command an
        irreversible world load."""
        st, _ = self._to_tree_state()
        st, _ = mlib.kxrw_decide(st, seam("tree0", "OK", (("tree", "t1"),), ut=111.0))
        st, _ = mlib.kxrw_decide(st, seam("commit", "OK", (), ut=222.0))
        st, _ = mlib.kxrw_decide(st, seam("stop", "OK", (), ut=223.0))
        st, acts = mlib.kxrw_decide(st, seam("idle0", "OK", (), ut=224.0))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("refusing to command InvokeRewindToLaunch", st.flake_reason)
        self.assertEqual([], acts)

    def test_an_unreadable_clock_never_becomes_the_pre_rewind_stamp(self):
        """`pre_rewind_ut` is the SPACECENTER gate's ONLY `before`, and the gate is
        the whole OBSERVED evidence that the world moved. A non-finite stamp can
        never be compared to anything, so the regression reads NaN forever - after
        an IRREVERSIBLE world load has already been commanded. MUTATION: stamp
        `snapshot.ut` unguarded (the shape before this fix) and the recording=false
        frame below commands the rewind and poisons the gate."""
        st, _ = self._to_tree_state(idleFrames=6)
        st, _ = mlib.kxrw_decide(st, seam("tree0", "OK", (("tree", "t1"),), ut=111.0))
        st, _ = mlib.kxrw_decide(st, seam("commit", "OK", (), ut=222.0))
        st, _ = mlib.kxrw_decide(st, seam("stop", "OK", (), ut=223.0))
        # The recorder READS idle, but on a frame whose clock is unreadable.
        st, acts = mlib.kxrw_decide(
            st, seam("idle0", "OK", (("recording", "false"),), ut=float("nan")))
        self.assertEqual(mlib.KXRW_RECORDER_IDLE, st.phase)
        self.assertFalse(st.done)
        self.assertTrue(math.isnan(st.pre_rewind_ut))
        # HOLD AND RE-PROBE, under a FRESH tag (the C# seam skips duplicate ids).
        self.assertEqual(["RecordingState"], [a.seam_verb for a in acts])
        self.assertEqual(["idle1"], [a.seam_tag for a in acts])
        # A readable clock on the next probe releases it, and stamps THAT frame.
        st, acts = mlib.kxrw_decide(
            st, seam("idle1", "OK", (("recording", "false"),), ut=225.0))
        self.assertEqual(mlib.KXRW_REWIND, st.phase)
        self.assertEqual(225.0, st.pre_rewind_ut)
        self.assertEqual("InvokeRewindToLaunch", acts[0].seam_verb)

    def test_a_clock_that_never_reads_burns_the_bound_and_names_the_stamp(self):
        """FAIL CLOSED with a DISTINCT give-up: 'the seam never answered' would
        send an operator after the bridge when the bridge answered every time."""
        st, _ = self._to_tree_state(idleFrames=3)
        st, _ = mlib.kxrw_decide(st, seam("tree0", "OK", (("tree", "t1"),), ut=111.0))
        st, _ = mlib.kxrw_decide(st, seam("commit", "OK", (), ut=222.0))
        st, _ = mlib.kxrw_decide(st, seam("stop", "OK", (), ut=223.0))
        verbs = []
        for i in range(8):
            st, acts = mlib.kxrw_decide(
                st, seam("idle%d" % i, "OK", (("recording", "false"),),
                         ut=float("nan")))
            verbs += [a.seam_verb for a in acts]
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_RECORDER_IDLE, st.flake_phase)
        self.assertIn("`ut` was unreadable", st.flake_reason)
        self.assertIn("refusing to command an irreversible rewind",
                      st.flake_reason)
        self.assertNotIn("InvokeRewindToLaunch", verbs)
        self.assertNotIn(mlib.KXRW_REWIND, st.phases_reached)

    def test_a_rejected_rewind_reports_parseks_own_reason(self):
        """The runner collapses REJECTED into `ERROR`; the refusal word lives in
        the decoded `msg` payload, and the give-up must quote it rather than guess
        (R1 flight 1's lesson)."""
        st, _ = self._to_tree_state()
        st, _ = mlib.kxrw_decide(st, seam("tree0", "OK", (("tree", "t1"),), ut=111.0))
        st, _ = mlib.kxrw_decide(st, seam("commit", "OK", (), ut=222.0))
        st, _ = mlib.kxrw_decide(st, seam("stop", "OK", (), ut=223.0))
        st, _ = mlib.kxrw_decide(
            st, seam("idle0", "OK", (("recording", "false"),), ut=224.0))
        st, _ = mlib.kxrw_decide(
            st, seam("rewind", "ERROR", (("msg", "merge-journal-in-flight"),),
                     ut=225.0))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual("merge-journal-in-flight", st.rewind_reject_reason)
        self.assertIn("merge-journal-in-flight", st.flake_reason)
        self.assertIn("tree=t1", st.flake_reason)

    def test_a_failed_commit_never_reaches_the_stop_or_the_rewind(self):
        st, _ = self._to_tree_state()
        st, _ = mlib.kxrw_decide(st, seam("tree0", "OK", (("tree", "t1"),), ut=111.0))
        st, acts = mlib.kxrw_decide(st, seam("commit", "ERROR", (), ut=222.0))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual([], acts)
        self.assertNotIn(mlib.KXRW_STOP, st.phases_reached)
        self.assertNotIn(mlib.KXRW_REWIND, st.phases_reached)

    def test_the_poll_window_for_the_new_verb_matches_the_reload_straddle(self):
        """InvokeRewindToLaunch is the same quicksave-copy + cold-load shape as
        InvokeRewind, and hlib sizes its C# dispatch budget off InvokeRewind's row.
        The mission-side WALL poll must sit above that budget or a healthy load
        that used its whole deferral reads as a wedged addon."""
        self.assertEqual(mlib.seam_command_poll_seconds("InvokeRewind"),
                         mlib.seam_command_poll_seconds("InvokeRewindToLaunch"))
        self.assertGreater(mlib.seam_command_poll_seconds("InvokeRewindToLaunch"),
                           mlib.SEAM_COMMAND_POLL_SECONDS_DEFAULT)


class SceneReloadToleranceTests(unittest.TestCase):
    """`vessel_lost` is the EXPECTED reading from the rewind until the watcher is
    on the pad (SPACECENTER has no active vessel at all), and it must stay lethal
    everywhere before that."""

    def test_the_post_rewind_block_is_exactly_the_phases_with_no_live_vessel(self):
        self.assertEqual(
            (mlib.KXRW_REWIND, mlib.KXRW_SPACECENTER, mlib.KXRW_AUTORECORD_OFF,
             mlib.KXRW_WATCHER_LAUNCH, mlib.KXRW_WATCHER_READY, mlib.KXRW_MAP_VIEW,
             mlib.KXRW_MAP_EXIT, mlib.KXRW_WATCH, mlib.KXRW_PLAYBACK_WAIT),
            mlib.KXRW_POST_REWIND_PHASES)
        # MAP-EXIT sits in the block for the SAME reason MAP-VIEW does, and the
        # block's own rule is that it is ONE CONTIGUOUS RUN from the rewind to the
        # end - carving a single render phase back out would put a hole in the
        # middle of it, which is precisely the "per-phase sprinkle" the header
        # forbids. MUTATION: drop MAP-EXIT here and the tuple stops being contiguous.
        self.assertEqual(
            mlib.KXRW_PHASES[mlib.KXRW_PHASES.index(mlib.KXRW_REWIND):
                             mlib.KXRW_PHASES.index(mlib.KXRW_DONE)],
            mlib.KXRW_POST_REWIND_PHASES)
        # The union the machine actually consults is the post-rewind block PLUS the
        # rollout, whose FLIGHT->FLIGHT reload is a second reason for a dead
        # handle, PLUS the impact profile's post-crash block, which is a third
        # (the craft was deliberately destroyed, the throwaway launch reloads the
        # scene over the wreckage, the exit tears it down and the RELOAD cold-boots
        # a save). THREE named sets, deliberately not one flat list: a future edit
        # has to say which reason it is extending.
        self.assertEqual((mlib.KXRW_ROLLOUT,) + mlib.KXRW_IMPACT_PHASES
                         + mlib.KXRW_POST_REWIND_PHASES,
                         mlib.KXRW_VESSEL_LOST_EXPECTED_PHASES)
        self.assertEqual(
            (mlib.KXRW_IMPACT_SETTLE, mlib.KXRW_SC_EXIT, mlib.KXRW_SC_COMMITTED,
             mlib.KXRW_TEMP_LAUNCH, mlib.KXRW_TEMP_READY),
            mlib.KXRW_IMPACT_PHASES)
        # No flight phase may sit in the carve-out: the ascent must still die on a
        # destroyed craft. The impact profile's first two phases are BOTH flight
        # phases and NEITHER is in the carve-out - IMPACT-AUTORECORD-OFF holds a
        # live coasting stack, and IMPACT-COAST treats a loss as the impact SIGNAL
        # in the generic block by NAME rather than by joining this set - so the
        # invariant below stays exact. MUTATION: put either in KXRW_IMPACT_PHASES
        # and a craft destroyed before the profile ever disarmed anything stops
        # ending the mission.
        overlap = (set(mlib.KXRW_FLIGHT_PHASES)
                   & set(mlib.KXRW_VESSEL_LOST_EXPECTED_PHASES))
        self.assertEqual(set(), overlap)
        for phase in (mlib.KXRW_IMPACT_AUTORECORD_OFF, mlib.KXRW_IMPACT_COAST):
            self.assertIn(phase, mlib.KXRW_FLIGHT_PHASES)
            self.assertNotIn(phase, mlib.KXRW_VESSEL_LOST_EXPECTED_PHASES)

    def test_a_lost_vessel_in_a_flight_phase_is_a_deterministic_failure(self):
        st = rolled_out(machine())
        st, _ = mlib.kxrw_decide(st, snap(ut=0.0, altitude=0.0, throttle=0.0,
                                          available_thrust=LIT))
        st, _ = mlib.kxrw_decide(st, snap(ut=30.0, vessel_lost=True))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("vessel-lost", st.loss_reason)

    def test_the_rewind_and_spacecenter_phases_survive_a_dead_vessel_handle(self):
        st = dataclasses.replace(machine(), phase=mlib.KXRW_REWIND,
                                 pre_rewind_ut=500.0, tree_id="t1")
        for i in range(4):
            st, acts = mlib.kxrw_decide(st, snap(ut=500.0, vessel_lost=True))
            self.assertFalse(st.done)
        self.assertEqual(4, st.post_rewind_vessel_lost_frames)
        # The reply lands on a still-vessel-less frame; the machine must take it.
        st, _ = mlib.kxrw_decide(st, seam("rewind", "OK", (), ut=500.0,
                                          vessel_lost=True))
        self.assertEqual(mlib.KXRW_SPACECENTER, st.phase)
        # ...and the SPACECENTER clock gate reads `ut` off a vessel_lost snapshot.
        st, acts = mlib.kxrw_decide(st, snap(ut=250.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_AUTORECORD_OFF, st.phase)
        self.assertEqual(250.0, st.ut_regression)
        # The SetSetting step is dispatched with NO active vessel, and must be:
        # SPACECENTER has none, and the disarm has to precede the watcher launch.
        self.assertEqual(["SetSetting"], [a.seam_verb for a in acts])

    def test_a_rewind_that_never_moved_the_clock_is_not_a_rewind(self):
        """The seam's OK is a COMMANDED reading. MUTATION: advance SPACECENTER on
        the OK alone and a no-op verb flies the whole watch leg over nothing."""
        st = dataclasses.replace(machine(spaceCenterFrames=4),
                                 phase=mlib.KXRW_SPACECENTER, pre_rewind_ut=500.0,
                                 rewind_result="OK", tree_id="t1")
        for i in range(6):
            st, _ = mlib.kxrw_decide(st, snap(ut=500.0 + i, vessel_lost=True))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("never ran backward", st.flake_reason)

    def test_the_watcher_launch_is_one_click_and_then_a_debounced_settle(self):
        # Entered only from AUTORECORD-OFF's OK (pinned by the ordering cell in
        # AutoRecordDisarmTests), so auto-record is already off by here.
        st = dataclasses.replace(machine(watcherReadyDebounceFrames=2),
                                 phase=mlib.KXRW_WATCHER_LAUNCH,
                                 autorecord_off_result="OK")
        st, acts = mlib.kxrw_decide(st, snap(ut=250.0, vessel_lost=True))
        self.assertEqual(1, len(acts))
        self.assertEqual(mlib.ACTION_LAUNCH_VESSEL, acts[0].kind)
        self.assertEqual("Jumping Flea", acts[0].text)
        self.assertEqual("LaunchPad", acts[0].launch_site)
        self.assertEqual(mlib.KXRW_WATCHER_READY, st.phase)
        # The scene is still tearing down: vessel_lost holds the streak at zero.
        st, _ = mlib.kxrw_decide(st, snap(ut=251.0, vessel_lost=True))
        self.assertEqual(0, st.watcher_ready_streak)
        st, _ = mlib.kxrw_decide(st, snap(ut=252.0, situation="PRE_LAUNCH",
                                          vessel_name=WATCHER))
        self.assertEqual(mlib.KXRW_WATCHER_READY, st.phase)
        st, acts = mlib.kxrw_decide(st, snap(ut=253.0, situation="PRE_LAUNCH",
                                             vessel_name=WATCHER))
        self.assertEqual(mlib.KXRW_MAP_VIEW, st.phase)
        self.assertTrue(st.watcher_ready_observed)
        self.assertEqual(["EnterMapView"], [a.seam_verb for a in acts])


class AutoRecordDisarmTests(unittest.TestCase):
    """NOTHING IN THIS LANE ISSUES StartRecording. The scenario pins
    `autoRecordOnLaunch=true` and Parsek's first-staging-on-the-pad trigger starts
    the Kerbal X recording at PRELAUNCH's OWN stage click - so the setting must
    stay armed for the whole flight, and must be turned OFF before the watcher
    launches or the Jumping Flea brings a second recorder with it."""

    def _to_spacecenter(self, **over):
        return dataclasses.replace(machine(**over), phase=mlib.KXRW_SPACECENTER,
                                   pre_rewind_ut=1200.0, rewind_result="OK",
                                   tree_id="t_kx", recording_end_ut=1200.0)

    def test_no_phase_issues_start_recording_anywhere_in_the_lane(self):
        """THE CONTRACT, swept rather than trusted: the recorder is started by
        Parsek's own trigger, so a StartRecording anywhere here would mean the
        machine had stopped believing that. MUTATION: add one and this reds."""
        verbs = set()
        st = rolled_out(machine(boosterStageCount=0, stageSettleFrames=1,
                                coastSeconds=1.0))
        st, acts = mlib.kxrw_decide(st, snap(ut=0.0, altitude=0.0, throttle=0.0,
                                             available_thrust=0.0))
        verbs |= {a.seam_verb for a in acts if a.seam_verb}
        script = [
            snap(ut=100.0, altitude=50000.0, apoapsis=61000.0, throttle=0.0,
                 situation="FLYING"),
            snap(ut=101.0, altitude=50000.0, apoapsis=61000.0, throttle=0.0,
                 situation="FLYING"),
            snap(ut=102.0, altitude=50000.0, apoapsis=61000.0, throttle=0.0,
                 situation="FLYING"),
            snap(ut=110.0, altitude=51000.0, apoapsis=61000.0, throttle=0.0,
                 situation="FLYING"),
            seam("tree0", "OK", (("tree", "t_kx"),), ut=111.0),
            seam("commit", "OK", (), ut=200.0),
            seam("stop", "OK", (), ut=201.0),
            seam("idle0", "OK", (("recording", "false"),), ut=202.0),
            seam("rewind", "OK", (), ut=202.0, vessel_lost=True),
            snap(ut=50.0, vessel_lost=True),
            seam("autorec", "OK", (), ut=50.5, vessel_lost=True),
            snap(ut=51.0, vessel_lost=True),
            snap(ut=52.0, situation="PRE_LAUNCH", vessel_name=WATCHER),
            snap(ut=53.0, situation="PRE_LAUNCH", vessel_name=WATCHER),
            seam("map", "OK", (), ut=54.0, situation="PRE_LAUNCH"),
            seam("mapexit", "OK", (), ut=54.5, situation="PRE_LAUNCH"),
            seam("watch", "OK", (), ut=55.0, situation="PRE_LAUNCH"),
        ]
        for s in script:
            st, acts = mlib.kxrw_decide(st, s)
            verbs |= {a.seam_verb for a in acts if a.seam_verb}
        self.assertNotIn("StartRecording", verbs)
        self.assertEqual(
            {"RecordingState", "CommitTree", "StopRecording",
             "InvokeRewindToLaunch", "SetSetting", "EnterMapView", "ExitMapView",
             "EnterWatchMode"},
            verbs)

    def test_the_disarm_runs_after_the_observed_clock_and_before_the_launch(self):
        """THE ORDERING, and both halves are load-bearing. Any EARLIER and the
        disarm kills the trigger the Kerbal X's own recording depends on; any LATER
        and the watcher has already auto-started a second recorder."""
        st = self._to_spacecenter()
        # The clock has NOT gone back yet: no SetSetting may go out.
        st, acts = mlib.kxrw_decide(st, snap(ut=1200.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_SPACECENTER, st.phase)
        self.assertEqual([], acts)
        # It goes back -> the disarm, and ONLY the disarm.
        st, acts = mlib.kxrw_decide(st, snap(ut=985.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_AUTORECORD_OFF, st.phase)
        self.assertEqual(1, len(acts))
        self.assertEqual(mlib.ACTION_PARSEK_SEAM_COMMAND, acts[0].kind)
        self.assertEqual("SetSetting", acts[0].seam_verb)
        self.assertEqual((("name", "autoRecordOnLaunch"), ("value", "false")),
                         acts[0].seam_args)
        self.assertEqual("autorec", acts[0].seam_tag)
        # No launch until it answers OK.
        st, acts = mlib.kxrw_decide(st, snap(ut=986.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_AUTORECORD_OFF, st.phase)
        self.assertEqual([], acts)
        st, acts = mlib.kxrw_decide(st, seam("autorec", "OK", (), ut=987.0,
                                             vessel_lost=True))
        self.assertEqual(mlib.KXRW_WATCHER_LAUNCH, st.phase)
        self.assertEqual("OK", st.autorecord_off_result)
        self.assertEqual([], acts)
        st, acts = mlib.kxrw_decide(st, snap(ut=988.0, vessel_lost=True))
        self.assertEqual(mlib.ACTION_LAUNCH_VESSEL, acts[0].kind)

    def test_the_setting_name_matches_the_key_hlib_and_the_specs_use(self):
        """Anchored to the REAL key, not a spelling copied out of a comment:
        `hlib.spec_expects_live_recording` matches on this exact name and the
        committed specs write it, so a drift here is a silent no-op setting."""
        self.assertEqual("autoRecordOnLaunch", mlib.KXRW_AUTORECORD_SETTING)
        self.assertEqual("false", mlib.KXRW_AUTORECORD_OFF_VALUE)

    def test_a_refused_disarm_is_fatal_unlike_a_refused_render_verb(self):
        """The asymmetry is the point. A refused EnterWatchMode is a PARSEK finding
        and is recorded; a refused SetSetting is a DRIVER-SEQUENCING failure, and
        flying on would put an unasked-for second recording into the very save the
        spec's log contracts read. MUTATION: record-and-advance here and this
        reds."""
        st = self._to_spacecenter()
        st, _ = mlib.kxrw_decide(st, snap(ut=985.0, vessel_lost=True))
        st, acts = mlib.kxrw_decide(
            st, seam("autorec", "ERROR", (("msg", "unknown-setting"),), ut=986.0,
                     vessel_lost=True))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_AUTORECORD_OFF, st.flake_phase)
        self.assertEqual("ERROR", st.autorecord_off_result)
        self.assertIn("unknown-setting", st.flake_reason)
        self.assertIn("SECOND recording", st.flake_reason)
        self.assertEqual([], acts)
        self.assertNotIn(mlib.KXRW_WATCHER_LAUNCH, st.phases_reached)

    def test_a_silent_disarm_hits_its_frame_bound(self):
        st = self._to_spacecenter(autoRecordOffFrames=3)
        st, _ = mlib.kxrw_decide(st, snap(ut=985.0, vessel_lost=True))
        for i in range(6):
            st, _ = mlib.kxrw_decide(st, snap(ut=986.0 + i, vessel_lost=True))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_AUTORECORD_OFF, st.flake_phase)
        self.assertIn("never answered", st.flake_reason)

    def test_the_disarm_evidence_rides_the_watcher_row(self):
        """A PRECONDITION of the launch, not a ninth outcome: the machine cannot
        reach WATCHER-LAUNCH without it, so it is carried as detail."""
        p = mlib.kxrw_params_from_dict(params())
        st = dataclasses.replace(machine(), watcher_ready_observed=True,
                                 watcher_ready_situation="PRE_LAUNCH",
                                 autorecord_off_result="OK")
        rows = mlib.evaluate_kxrw_assertions([], p, st)
        self.assertEqual(8, len(rows))          # still eight, not nine
        row = [r for r in rows if r.name == "watcherOnPad"][0]
        self.assertTrue(row.met)
        self.assertEqual("OK", row.detail["autoRecordDisabled"])
        self.assertEqual("autoRecordOnLaunch", row.detail["autoRecordSetting"])

    def test_the_recorded_span_starts_at_the_prelaunch_click(self):
        """Because the stage click IS what auto-record fires on, `launch_ut` is the
        recording's own start rather than an approximation - so the span row needs
        no offset. MUTATION: stamp launch_ut anywhere but the click frame and the
        span stops describing the recording."""
        st = rolled_out(machine())
        st, acts = mlib.kxrw_decide(st, snap(ut=4242.0, altitude=0.0, throttle=0.0,
                                             available_thrust=0.0))
        self.assertIn(mlib.ACTION_ACTIVATE_STAGE, kinds(acts))
        self.assertEqual(4242.0, st.launch_ut)

    def test_the_machine_never_reads_recorder_state_before_tree_state(self):
        """Nothing may ASSUME a live recorder. The first read of recorder state at
        all is TREE-STATE, and it fails closed on an empty `tree=` - which is
        exactly the shape 'auto-record never fired' produces."""
        st = rolled_out(machine(boosterStageCount=0, stageSettleFrames=1,
                                coastSeconds=1.0, treeStateFrames=2))
        st, _ = mlib.kxrw_decide(st, snap(ut=0.0, altitude=0.0, throttle=0.0,
                                          available_thrust=0.0))
        for ut in (100.0, 101.0, 102.0):
            st, acts = fly(st, ut=ut, altitude=50000.0, apoapsis=61000.0,
                           throttle=0.0)
            self.assertEqual([], [a for a in acts if a.seam_verb])
        st, acts = fly(st, ut=110.0, altitude=51000.0, apoapsis=61000.0,
                       throttle=0.0)
        self.assertEqual(mlib.KXRW_TREE_STATE, st.phase)
        self.assertEqual(["RecordingState"], [a.seam_verb for a in acts])
        # Auto-record never fired -> the reply names no tree -> fail closed.
        for i in range(5):
            st, _ = mlib.kxrw_decide(st, seam("tree%d" % i, "OK", (), ut=111.0 + i))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("no readable `tree` field", st.flake_reason)
        self.assertEqual("", st.tree_id)


class RolloutTests(unittest.TestCase):
    """THE LANE MUST FLY ITS OWN CRAFT. `activate_next_stage` stages whatever is
    ACTIVE, and what is active at scene entry is whatever the fixture left on the
    pad - for this lane's save, a different craft sitting in PRE_LAUNCH. Every
    assertion row downstream would pass while measuring it."""

    def test_the_first_action_of_the_mission_launches_the_declared_craft(self):
        st = machine()
        self.assertEqual(mlib.KXRW_ROLLOUT, st.phase)
        st, acts = mlib.kxrw_decide(st, snap(ut=0.0, situation="PRE_LAUNCH"))
        self.assertEqual(1, len(acts))
        self.assertEqual(mlib.ACTION_LAUNCH_VESSEL, acts[0].kind)
        self.assertEqual(CRAFT, acts[0].text)
        self.assertEqual("LaunchPad", acts[0].launch_site)
        # Exactly ONE click, however long the reload takes.
        st, acts = mlib.kxrw_decide(st, snap(ut=0.5, vessel_lost=True))
        self.assertEqual([], acts)

    def test_no_stage_is_ever_commanded_before_the_craft_is_confirmed(self):
        """THE WHOLE POINT. MUTATION: start the machine at PRELAUNCH (the shape
        before this phase existed) and the very first action is an ACTIVATE_STAGE
        against the pad occupant."""
        st = machine(rolloutFrames=6)
        emitted = []
        for i in range(10):
            st, acts = mlib.kxrw_decide(
                st, snap(ut=float(i), situation="PRE_LAUNCH",
                         vessel_name="GS1 Auto-Chute Booster"))
            emitted += kinds(acts)
            if st.done:
                break
        self.assertNotIn(mlib.ACTION_ACTIVATE_STAGE, emitted)
        self.assertNotIn(mlib.ACTION_SET_THROTTLE, emitted)
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_ROLLOUT, st.flake_phase)
        self.assertIn("GS1 Auto-Chute Booster", st.flake_reason)
        self.assertIn("never became the active vessel", st.flake_reason)

    def test_the_reload_frames_are_tolerated_not_fatal(self):
        """kRPC's LaunchVessel is a FLIGHT->FLIGHT reload, so the handle is
        legitimately dead for a few frames (the B-DOCK Interceptor precedent)."""
        st = machine()
        st, _ = mlib.kxrw_decide(st, snap(ut=0.0, situation="PRE_LAUNCH"))
        for i in range(5):
            st, _ = mlib.kxrw_decide(st, snap(ut=float(i), vessel_lost=True))
            self.assertFalse(st.done)
        self.assertEqual(5, st.rollout_vessel_lost_frames)
        self.assertEqual(0, st.rollout_ready_streak)   # a lost frame settles nothing
        st, _ = mlib.kxrw_decide(st, snap(ut=6.0, situation="PRE_LAUNCH",
                                          vessel_name=CRAFT))
        st, _ = mlib.kxrw_decide(st, snap(ut=7.0, situation="PRE_LAUNCH",
                                          vessel_name=CRAFT))
        self.assertEqual(mlib.KXRW_PRELAUNCH, st.phase)
        self.assertTrue(st.rollout_ready_observed)
        self.assertEqual(CRAFT, st.rollout_vessel_name)

    def test_an_unread_name_channel_deadlocks_into_the_named_giveup(self):
        """FAIL CLOSED: a shell that forgot `read_vessel_name` leaves the ""
        sentinel, which matches no declared name. It must burn the bound and SAY SO
        - never fly whatever happened to be on the pad."""
        st = machine(rolloutFrames=4)
        for i in range(8):
            st, _ = mlib.kxrw_decide(st, snap(ut=float(i), situation="PRE_LAUNCH"))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("read_vessel_name", st.flake_reason)

    def test_a_right_name_in_a_wrong_situation_does_not_settle(self):
        """The situation conjunct: a craft of the right name that is already flying
        (or wreckage of it) is not a craft that just rolled out."""
        self.assertFalse(mlib.kxrw_launch_settled(False, CRAFT, "FLYING", CRAFT,
                                                  ("PRE_LAUNCH",)))
        self.assertTrue(mlib.kxrw_launch_settled(False, CRAFT, "PRE_LAUNCH", CRAFT,
                                                 ("PRE_LAUNCH",)))

    def test_the_settle_predicate_fails_closed_on_every_missing_half(self):
        # A lost frame settles nothing, even with the right name.
        self.assertFalse(mlib.kxrw_launch_settled(True, CRAFT, "PRE_LAUNCH", CRAFT,
                                                  ("PRE_LAUNCH",)))
        # An UNREAD name never matches a DECLARED one.
        self.assertFalse(mlib.kxrw_launch_settled(False, "", "PRE_LAUNCH", CRAFT,
                                                  ("PRE_LAUNCH",)))
        # A DIFFERENT craft in the right situation never matches.
        self.assertFalse(mlib.kxrw_launch_settled(
            False, "GS1 Auto-Chute Booster", "PRE_LAUNCH", CRAFT, ("PRE_LAUNCH",)))
        # Whitespace is tolerated; identity is not guessed at.
        self.assertTrue(mlib.kxrw_launch_settled(False, " Kerbal X ", "PRE_LAUNCH",
                                                 CRAFT, ("PRE_LAUNCH",)))
        # No declared name -> situation only, which is the honest degrade.
        self.assertTrue(mlib.kxrw_launch_settled(False, "", "PRE_LAUNCH", "",
                                                 ("PRE_LAUNCH",)))

    def test_the_watcher_gate_is_the_same_gate(self):
        """A launch is a scene reload either time, so the weaker of the two gates
        would be the hole. MUTATION: drop the name conjunct from WATCHER-READY and
        this reds."""
        st = dataclasses.replace(machine(watcherReadyDebounceFrames=2),
                                 phase=mlib.KXRW_WATCHER_LAUNCH,
                                 autorecord_off_result="OK")
        st, _ = mlib.kxrw_decide(st, snap(ut=250.0, vessel_lost=True))
        # The KERBAL X is still the active vessel name at this point in a real
        # reload; it must not satisfy the watcher gate.
        for i in range(3):
            st, _ = mlib.kxrw_decide(st, snap(ut=251.0 + i, situation="PRE_LAUNCH",
                                              vessel_name=CRAFT))
            self.assertEqual(mlib.KXRW_WATCHER_READY, st.phase)
        self.assertEqual(0, st.watcher_ready_streak)
        st, _ = mlib.kxrw_decide(st, snap(ut=260.0, situation="PRE_LAUNCH",
                                          vessel_name=WATCHER))
        st, _ = mlib.kxrw_decide(st, snap(ut=261.0, situation="PRE_LAUNCH",
                                          vessel_name=WATCHER))
        self.assertEqual(mlib.KXRW_MAP_VIEW, st.phase)
        self.assertEqual(WATCHER, st.watcher_ready_vessel_name)

    def test_the_craft_file_name_and_the_expected_vessel_name_are_separable(self):
        """THE RUN-KILLER THIS PAIR EXISTS FOR. `launch_vessel` resolves a craft by
        its FILE name, but the name the active vessel READS BACK is that file's
        `ship = ` line - and stock craft files write it as a `#autoLOC_*`
        localization token that KSP hands back RAW (`ShipConstruct.LoadShip` /
        `Vessel.GetName` never localize; a launched Jumping Flea persists
        `name = #autoLOC_501224`). A gate wired to the file name can therefore
        never settle on a stock craft. MUTATION: gate on `craft_name` /
        `watcher_craft_name` (the shape before this fix) and both cells below
        deadlock into their give-ups."""
        st = machine(craftName="Jumping Flea",
                     rolloutExpectedVesselName="#autoLOC_501224")
        st, acts = mlib.kxrw_decide(st, snap(ut=0.0, situation="PRE_LAUNCH"))
        # The FILE name is what gets launched...
        self.assertEqual("Jumping Flea", acts[0].text)
        # ...and the declared VESSEL name is what settles the gate.
        for _ in range(2):
            st, _ = mlib.kxrw_decide(st, snap(ut=1.0, situation="PRE_LAUNCH",
                                              vessel_name="#autoLOC_501224"))
        self.assertEqual(mlib.KXRW_PRELAUNCH, st.phase)
        self.assertTrue(st.rollout_ready_observed)

    def test_the_watcher_name_is_separable_the_same_way(self):
        st = dataclasses.replace(
            machine(watcherCraftName="Jumping Flea",
                    watcherExpectedVesselName="#autoLOC_501224",
                    watcherReadyDebounceFrames=2),
            phase=mlib.KXRW_WATCHER_LAUNCH, autorecord_off_result="OK")
        st, acts = mlib.kxrw_decide(st, snap(ut=250.0, vessel_lost=True))
        self.assertEqual("Jumping Flea", acts[0].text)
        for ut in (251.0, 252.0):
            st, _ = mlib.kxrw_decide(st, snap(ut=ut, situation="PRE_LAUNCH",
                                              vessel_name="#autoLOC_501224"))
        self.assertEqual(mlib.KXRW_MAP_VIEW, st.phase)
        self.assertTrue(st.watcher_ready_observed)

    def test_each_expected_name_defaults_to_its_craft_name(self):
        """A spec that declares neither gets the old behaviour verbatim, which is
        correct for a craft whose `ship =` line IS a literal."""
        p = mlib.kxrw_params_from_dict(params(craftName="Kerbal X",
                                              watcherCraftName="Jumping Flea"))
        self.assertEqual("Kerbal X", p.rollout_expected_vessel_name)
        self.assertEqual("Jumping Flea", p.watcher_expected_vessel_name)
        # An empty declaration is not a licence to skip the read - it falls back to
        # the craft name rather than degrading to a situation-only gate.
        p = mlib.kxrw_params_from_dict(params(craftName="Kerbal X",
                                              rolloutExpectedVesselName=""))
        self.assertEqual("Kerbal X", p.rollout_expected_vessel_name)

    def test_the_committed_watcher_fixture_carries_a_literal_ship_name(self):
        """The FIXTURE half of the fix, read off the file rather than trusted: the
        committed Jumping Flea shipped stock's `ship = #autoLOC_501224`, which is
        what the live gate would have had to match. MUTATION: restore the token and
        this reds here instead of on a flight."""
        with open(WATCHER_CRAFT_PATH, encoding="utf-8", errors="replace") as fh:
            first = fh.readline().strip()
        self.assertEqual("ship = %s" % WATCHER, first)

    def test_the_shell_opts_into_the_name_channel(self):
        """REQUIRED, not nice-to-have: both launch gates deadlock without it."""
        control = kx_rewind_watch.make_control()
        self.assertTrue(control._read_vessel_name)

    def test_the_identity_evidence_reaches_the_result_rows(self):
        p = mlib.kxrw_params_from_dict(params())
        st = dataclasses.replace(machine(), launch_ut=10.0, recording_end_ut=210.0,
                                 rollout_vessel_name=CRAFT,
                                 rollout_vessel_lost_frames=4,
                                 watcher_ready_observed=True,
                                 watcher_ready_vessel_name=WATCHER,
                                 autorecord_off_result="OK")
        rows = {r.name: r for r in mlib.evaluate_kxrw_assertions([], p, st)}
        self.assertEqual(8, len(rows))
        self.assertEqual(CRAFT, rows["recordedSpanSeconds"].detail["craft"])
        self.assertEqual(CRAFT,
                         rows["recordedSpanSeconds"].detail["rolloutObservedName"])
        self.assertEqual(4, rows["recordedSpanSeconds"].detail[
            "rolloutVesselLostFrames"])
        self.assertEqual(WATCHER, rows["watcherOnPad"].detail["observedName"])

    def test_the_settle_counter_is_phase_frames_and_nothing_else(self):
        """`cut_frames_held` was provably always equal to `phase_frames`; a
        duplicate that is only ever equal is a duplicate that can drift, and the one
        that drifts is what the decoupler gate reads. MUTATION: reintroduce it and
        this reds."""
        self.assertNotIn("cut_frames_held",
                         {f.name for f in dataclasses.fields(mlib.KxrwState)})


class RenderVerbsAreRecordedNotJudgedTests(unittest.TestCase):
    """THE MISSION-VS-PARSEK ORTHOGONALITY RULE, applied to the three render verbs.
    A REFUSED verb is Parsek's finding and belongs to the spec's log contracts; a
    driver-INVALID here would discard the very KSP.log they read."""

    def _to_map_view(self, tree_id="", **over):
        st = dataclasses.replace(machine(**over), phase=mlib.KXRW_MAP_VIEW,
                                 recording_end_ut=400.0, tree_id=tree_id)
        return st

    def _to_watch(self, tree_id="", **over):
        """MAP-VIEW's terminal, then MAP-EXIT's, landing the machine in WATCH with
        the map CLOSED - which is the state every watch cell below is about."""
        st = self._to_map_view(tree_id=tree_id, **over)
        st, acts = mlib.kxrw_decide(st, seam("map", "OK", (), ut=250.0))
        self.assertEqual(mlib.KXRW_MAP_EXIT, st.phase)
        self.assertEqual(["ExitMapView"], [a.seam_verb for a in acts])
        st, acts = mlib.kxrw_decide(st, seam("mapexit", "OK", (), ut=250.2))
        self.assertEqual(mlib.KXRW_WATCH, st.phase)
        # NO EnterWatchMode rides the map close either: that one frame of eagerness
        # is what the GS-4 reading run lost its whole watch leg to.
        self.assertEqual([], acts)
        return st

    def _to_first_watch_attempt(self, tree_id="", **over):
        """...and then the frame on which WATCH issues its first EnterWatchMode.
        `launch_ut` is UNREAD on these hand-built states, so the window gate fails
        OPEN and the attempt goes out on the phase's first frame (that degrade has
        its own cell in WatchEntryRaceTests)."""
        st = self._to_watch(tree_id=tree_id, **over)
        return mlib.kxrw_decide(st, snap(ut=250.5))

    def test_a_rejected_watch_records_the_verdict_and_flies_on(self):
        st = self._to_map_view()
        st, acts = mlib.kxrw_decide(
            st, seam("map", "ERROR", (("msg", "no-flight-instance"),), ut=250.0))
        self.assertEqual(mlib.KXRW_MAP_EXIT, st.phase)
        self.assertEqual("ERROR", st.map_view_result)
        self.assertEqual("no-flight-instance", st.map_view_reject_reason)
        self.assertEqual(["ExitMapView"], [a.seam_verb for a in acts])
        st, acts = mlib.kxrw_decide(st, seam("mapexit", "OK", (), ut=250.2))
        self.assertEqual(mlib.KXRW_WATCH, st.phase)
        self.assertEqual([], acts)
        st, acts = mlib.kxrw_decide(st, snap(ut=250.5))
        self.assertEqual(["EnterWatchMode"], [a.seam_verb for a in acts])
        st, _ = mlib.kxrw_decide(
            st, seam("watch0", "ERROR", (("msg", "unknown-tree"),), ut=251.0))
        self.assertFalse(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.phase)
        self.assertEqual("ERROR", st.watch_result)
        self.assertEqual("unknown-tree", st.watch_reject_reason)

    def test_the_watch_command_is_scoped_to_the_captured_tree_and_carries_no_index(self):
        """THE SCOPE IS THE POINT. Unscoped, the C# auto-select walks EVERY
        committed recording and takes the first with
        HasActiveGhost && SameBody && WithinVisualRange - and by this step the lane
        has authored booster debris and a controlled-decoupled core child, all live
        ghosts over the same pad. A debris pick answers OK and every spec token
        still passes while the lane measures the wrong subject. MUTATION: send no
        args and this reds.

        No `index` though: the committed-list index is an ordering nothing in a
        mission run owns."""
        _, acts = self._to_first_watch_attempt(tree_id="t_kx")
        self.assertEqual("EnterWatchMode", acts[0].seam_verb)
        self.assertEqual((("tree", "t_kx"),), acts[0].seam_args)
        self.assertNotIn("index", dict(acts[0].seam_args))

    def test_an_uncaptured_tree_sends_no_scope_rather_than_an_empty_one(self):
        """The honest degrade: the C# side reads an empty `tree` as NO scope
        anyway, so emitting `tree=` would claim a narrowing that is not happening.
        Unreachable in practice - TREE-STATE fails closed on an empty `tree=` - and
        pinned so it stays a degrade rather than a silent blank arg."""
        self.assertEqual((("tree", "t1"),), mlib.kxrw_watch_seam_args("t1"))
        self.assertEqual((("tree", "t1"),), mlib.kxrw_watch_seam_args("  t1  "))
        self.assertEqual((), mlib.kxrw_watch_seam_args(""))
        self.assertEqual((), mlib.kxrw_watch_seam_args("   "))
        self.assertEqual((), mlib.kxrw_watch_seam_args(None))

    def test_the_selected_index_is_carried_as_evidence_not_as_a_gate(self):
        """`tree=` narrows the walk to this flight's members, but the pick INSIDE
        the tree is still first-match-wins by committed index - so which recording
        was actually watched has to be visible in the result JSON. It is evidence:
        the row is met either way."""
        st, _ = self._to_first_watch_attempt(tree_id="t_kx")
        st, _ = mlib.kxrw_decide(
            st, seam("watch0", "OK", (("index", "2"), ("recId", "rec_abc"),
                                      ("watching", "true")), ut=251.0))
        self.assertEqual("2", st.watch_selected_index)
        self.assertEqual("rec_abc", st.watch_selected_rec_id)
        p = mlib.kxrw_params_from_dict(params())
        row = [r for r in mlib.evaluate_kxrw_assertions([], p, st)
               if r.name == "renderVerbsDriven"][0]
        self.assertEqual("t_kx", row.detail["enterWatchModeScopeTree"])
        self.assertEqual("2", row.detail["enterWatchModeSelectedIndex"])
        self.assertEqual("rec_abc", row.detail["enterWatchModeSelectedRecId"])

    def test_a_refused_render_verb_still_meets_the_row_and_says_so(self):
        p = mlib.kxrw_params_from_dict(params())
        st = dataclasses.replace(machine(), map_view_result="ERROR",
                                 map_view_reject_reason="no-flight-instance",
                                 map_exit_result="ERROR",
                                 map_exit_reject_reason="mapview-refused",
                                 watch_result="ERROR",
                                 watch_reject_reason="already-watching")
        row = [r for r in mlib.evaluate_kxrw_assertions([], p, st)
               if r.name == "renderVerbsDriven"][0]
        self.assertTrue(row.met)
        self.assertTrue(row.detail["metOnRejectionByDesign"])
        self.assertEqual("already-watching", row.detail["enterWatchModeReason"])
        self.assertEqual("mapview-refused", row.detail["exitMapViewReason"])
        self.assertEqual("ERROR/ERROR/ERROR", row.value)

    def test_a_verb_that_never_answered_at_all_does_not_meet_the_row(self):
        p = mlib.kxrw_params_from_dict(params())
        st = dataclasses.replace(machine(), map_view_result="OK",
                                 map_exit_result="OK", watch_result="")
        row = [r for r in mlib.evaluate_kxrw_assertions([], p, st)
               if r.name == "renderVerbsDriven"][0]
        self.assertFalse(row.met)
        # ...and the map CLOSE is a FULL MEMBER of the row, not a courtesy: a run
        # that opened the map, watched under the overlay and never closed it did not
        # drive the sequence the operator rule names. MUTATION: drop the exit
        # conjunct from the row and this half reds.
        st = dataclasses.replace(machine(), map_view_result="OK",
                                 map_exit_result="", watch_result="OK")
        row = [r for r in mlib.evaluate_kxrw_assertions([], p, st)
               if r.name == "renderVerbsDriven"][0]
        self.assertFalse(row.met)
        self.assertEqual("OK/NONE/OK", row.value)

    def test_a_silent_seam_is_a_transport_fault_and_flakes(self):
        """The distinction the lane depends on: a REFUSED verb rendered a verdict
        and is recorded; a SILENT one rendered nothing, so there is nothing to
        record and the bridge itself is suspect."""
        st = self._to_watch(watchFrames=3)
        for i in range(6):
            st, _ = mlib.kxrw_decide(st, snap(ut=251.0 + i))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_WATCH, st.flake_phase)
        self.assertIn("never answered", st.flake_reason)


class MapClosedBeforeTheWatchTests(unittest.TestCase):
    """THE OPERATOR RULE: the replay is WATCHED IN FLIGHT VIEW, never under the map
    overlay. Opening the map is the "go to it" half of the player workflow; the
    watching itself has to happen with the map CLOSED, or the ghost meshes this
    whole lane exists to see are behind the overlay rather than on screen. The
    machine used to walk MAP-VIEW -> WATCH with the map still open, so the watch
    camera drove under it - and nothing in the lane could tell, because a map that
    never closed leaves no token and no row."""

    def _to_map_view(self, **over):
        return dataclasses.replace(machine(**over), phase=mlib.KXRW_MAP_VIEW,
                                   recording_end_ut=400.0, tree_id="t_kx")

    def test_map_exit_sits_between_the_open_and_the_watch(self):
        """The phase order IS the rule. MUTATION: move MAP-EXIT after WATCH and the
        map closes only once the watch camera has already driven under it."""
        self.assertEqual(31, len(mlib.KXRW_PHASES))
        self.assertEqual(len(set(mlib.KXRW_PHASES)), len(mlib.KXRW_PHASES))
        i = mlib.KXRW_PHASES.index
        self.assertEqual(i(mlib.KXRW_MAP_VIEW) + 1, i(mlib.KXRW_MAP_EXIT))
        self.assertEqual(i(mlib.KXRW_MAP_EXIT) + 1, i(mlib.KXRW_WATCH))
        # Its own wire id: the C# seam SKIPS DUPLICATE IDS, so a close reusing the
        # open's tag would be a silent no-op whose poll expires as a TIMEOUT that
        # reads like a wedged addon.
        self.assertNotEqual(mlib.KXRW_TAG_MAP, mlib.KXRW_TAG_MAP_EXIT)

    def test_the_map_view_terminal_commands_the_close(self):
        st, acts = mlib.kxrw_decide(self._to_map_view(),
                                    seam("map", "OK", (), ut=250.0))
        self.assertEqual(mlib.KXRW_MAP_EXIT, st.phase)
        self.assertEqual(1, len(acts))
        self.assertEqual(mlib.ACTION_PARSEK_SEAM_COMMAND, acts[0].kind)
        self.assertEqual("ExitMapView", acts[0].seam_verb)
        self.assertEqual(mlib.KXRW_TAG_MAP_EXIT, acts[0].seam_tag)
        self.assertEqual((), acts[0].seam_args)
        # No EnterWatchMode rides it either - the hold owns that (WatchEntryRace).
        st, acts = mlib.kxrw_decide(st, snap(ut=250.2))
        self.assertEqual(mlib.KXRW_MAP_EXIT, st.phase)
        self.assertEqual([], acts)

    def test_any_map_exit_terminal_records_the_verdict_and_advances(self):
        """RECORD-DON'T-FAIL, identical to MAP-VIEW's: a refused render verb is a
        PARSEK finding carried by the spec's log contracts, and failing the mission
        on it would be driver-INVALID - discarding the very KSP.log they read.
        MUTATION: flake on a non-OK here and a product refusal becomes a retry."""
        for result, reason in (("OK", ""), ("ERROR", "mapview-refused"),
                               ("ERROR", "mapview-unavailable"),
                               ("TIMEOUT", "")):
            st, _ = mlib.kxrw_decide(self._to_map_view(),
                                     seam("map", "OK", (), ut=250.0))
            args = (("msg", reason),) if reason else ()
            st, acts = mlib.kxrw_decide(st, seam("mapexit", result, args,
                                                 ut=250.5))
            self.assertEqual(mlib.KXRW_WATCH, st.phase, result)
            self.assertFalse(st.done, result)
            self.assertIsNone(st.verdict, result)
            self.assertEqual([], acts, result)
            self.assertEqual(result, st.map_exit_result)
            self.assertEqual(reason, st.map_exit_reject_reason)

    def test_the_close_verdict_reaches_the_render_row(self):
        """The exit verdict is surfaced exactly the way the open's and the watch's
        are: the row value reads <open>/<close>/<watch>."""
        st, _ = mlib.kxrw_decide(self._to_map_view(),
                                 seam("map", "OK", (), ut=250.0))
        st, _ = mlib.kxrw_decide(
            st, seam("mapexit", "ERROR", (("msg", "mapview-refused"),), ut=250.5))
        st, _ = mlib.kxrw_decide(st, snap(ut=251.0))
        st, _ = mlib.kxrw_decide(st, seam("watch0", "OK", (("index", "0"),),
                                          ut=251.5))
        p = mlib.kxrw_params_from_dict(params())
        row = [r for r in mlib.evaluate_kxrw_assertions([], p, st)
               if r.name == "renderVerbsDriven"][0]
        self.assertTrue(row.met)
        self.assertEqual("OK/ERROR/OK", row.value)
        self.assertEqual("ERROR", row.detail["exitMapViewResult"])
        self.assertEqual("mapview-refused", row.detail["exitMapViewReason"])

    def test_a_silent_close_is_a_transport_fault_and_flakes(self):
        """Same asymmetry the open carries: a REFUSED close rendered a verdict and
        is recorded; a SILENT one rendered nothing, so the bridge itself is
        suspect. The bound is `mapViewFrames`, REUSED deliberately - the close is
        the same kind of wait as the open (one single-phase toggle verb answering
        on the next frame), and a second knob would be two names for one number."""
        st, _ = mlib.kxrw_decide(self._to_map_view(mapViewFrames=3),
                                 seam("map", "OK", (), ut=250.0))
        for i in range(8):
            st, acts = mlib.kxrw_decide(st, snap(ut=251.0 + i))
            self.assertEqual([], acts)
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_MAP_EXIT, st.flake_phase)
        self.assertIn("ExitMapView", st.flake_reason)
        self.assertIn("never answered", st.flake_reason)
        self.assertNotIn(mlib.KXRW_WATCH, st.phases_reached)


class WatchEntryRaceTests(unittest.TestCase):
    """THE GS-4 READING RUN'S ONE FINDING, pinned in both halves.

    That run flew clean (MISSION-OK, ghostLifecycle spawned=8 destroyLines=8
    unbalanced=0) and still red on two required tokens, from one cause: the single
    EnterWatchMode went out at 00:48:27 and Parsek answered REJECTED
    `no-watchable-ghost`, while the parent ghost's `phase=MeshSpawned ...
    vessel=Kerbal X` landed at 00:48:32. Watch never entered, so the parent's
    derender came out `stale past-end ghost (no longer held)` instead of a
    watch-hold reason. The ghost does not exist until the clock reaches
    `launch_ut` - a non-loop ghost replays at its RECORDED absolute UTs and the
    rewind had dropped the clock to launchUT-15 - so the machine now HOLDS for the
    window and RE-ASKS that one refusal. Nothing here became fatal."""

    def _watching(self, **over):
        """A WATCH-phase machine with a REAL launch_ut, so the window gate is live
        rather than degraded."""
        over.setdefault("playbackMarginSeconds", 30.0)
        return dataclasses.replace(machine(**over), phase=mlib.KXRW_WATCH,
                                   launch_ut=1000.0, recording_end_ut=1200.0,
                                   tree_id="t_kx")

    def test_no_attempt_goes_out_before_the_replay_window_opens(self):
        """MUTATION: issue on the phase's first frame (the shape before this fix)
        and the very first command below goes out at launchUT-15, which is what the
        reading run did and what Parsek rightly refused."""
        st = self._watching(watchEntryLeadSeconds=5.0)
        # The whole pre-window walk: the post-rewind clock climbing to launchUT.
        for ut in (985.0, 990.0, 999.9, 1004.99):
            st, acts = mlib.kxrw_decide(st, snap(ut=ut))
            self.assertEqual([], acts, "attempted at ut=%s" % ut)
            self.assertEqual(mlib.KXRW_WATCH, st.phase)
            self.assertEqual(0, st.watch_attempts)
        # launchUT + lead exactly: inclusive, and the ask goes out scoped.
        st, acts = mlib.kxrw_decide(st, snap(ut=1005.0))
        self.assertEqual(["EnterWatchMode"], [a.seam_verb for a in acts])
        self.assertEqual("watch0", acts[0].seam_tag)
        self.assertEqual((("tree", "t_kx"),), acts[0].seam_args)
        self.assertEqual(1, st.watch_attempts)

    def test_the_window_gate_fails_open_on_an_unreadable_clock(self):
        """The OPPOSITE direction from the fueled-core discard's fail-closed read,
        and deliberately: the lead only shortens a race the retry loop already
        fixes, so an unreadable clock must ask rather than burn the phase. MUTATION:
        fail closed here and an unread `ut` costs the whole watch leg to protect
        nothing."""
        self.assertTrue(mlib.kxrw_watch_window_open(float("nan"), 1000.0, 5.0))
        self.assertTrue(mlib.kxrw_watch_window_open(1000.0, float("nan"), 5.0))
        self.assertFalse(mlib.kxrw_watch_window_open(1004.9, 1000.0, 5.0))
        self.assertTrue(mlib.kxrw_watch_window_open(1005.0, 1000.0, 5.0))

    def test_a_no_watchable_ghost_refusal_is_re_asked_under_a_fresh_tag(self):
        """THE FIX. The refusal means "not yet", so the machine waits out the
        cadence and asks again with a NEW wire id - the C# seam SKIPS DUPLICATE
        IDS, so a reused tag would make every retry a silent no-op that then
        expires as a TIMEOUT reading like a wedged addon. MUTATION: advance on the
        first rejection (the shape before this fix) and the lane records a refusal
        it could have ridden out."""
        st = self._watching(watchEntryLeadSeconds=0.0)
        st, acts = mlib.kxrw_decide(st, snap(ut=1000.0))
        self.assertEqual("watch0", acts[0].seam_tag)
        st, acts = mlib.kxrw_decide(
            st, seam("watch0", "ERROR", (("msg", "no-watchable-ghost"),),
                     ut=1000.5))
        # RECORDED but NOT advanced: still WATCH, and no command this frame.
        self.assertEqual(mlib.KXRW_WATCH, st.phase)
        self.assertEqual([], acts)
        self.assertEqual("no-watchable-ghost", st.watch_reject_reason)
        # The cadence is held before re-asking - one command a frame would spend
        # the whole bound on commands.
        tags = []
        ut = 1001.0
        for _ in range(mlib.KXRW_WATCH_RETRY_CADENCE_FRAMES + 2):
            st, acts = mlib.kxrw_decide(st, snap(ut=ut))
            tags += [a.seam_tag for a in acts]
            ut += 0.5
            if tags:
                break
        self.assertEqual(["watch1"], tags)
        # Second refusal, then an OK on the THIRD probe: recorded and advanced.
        st, _ = mlib.kxrw_decide(
            st, seam("watch1", "ERROR", (("msg", "no-watchable-ghost"),), ut=ut))
        self.assertEqual(mlib.KXRW_WATCH, st.phase)
        tags = []
        for _ in range(mlib.KXRW_WATCH_RETRY_CADENCE_FRAMES + 2):
            ut += 0.5
            st, acts = mlib.kxrw_decide(st, snap(ut=ut))
            tags += [a.seam_tag for a in acts]
            if tags:
                break
        self.assertEqual(["watch2"], tags)
        st, _ = mlib.kxrw_decide(
            st, seam("watch2", "OK", (("index", "0"), ("recId", "rec_kx")),
                     ut=ut + 0.5))
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.phase)
        self.assertEqual("OK", st.watch_result)
        self.assertEqual("0", st.watch_selected_index)
        self.assertEqual("rec_kx", st.watch_selected_rec_id)
        self.assertEqual(3, st.watch_attempts)
        # Every tag distinct: three commands, three wire ids.
        self.assertEqual(3, len({"watch0", "watch1", "watch2"}))
        self.assertFalse(st.done)
        self.assertIsNone(st.verdict)

    def test_any_other_rejection_still_records_and_advances_immediately(self):
        """The retry is NARROW by construction: only `no-watchable-ghost` means
        "not yet". A verdict about THIS world is recorded and flown past on the
        spot, exactly as before - re-asking would re-collect the same answer while
        delaying the playback wait that puts the ghost's retire in the log.
        MUTATION: retry on any refusal and this hangs for the whole bound."""
        for reason in ("unknown-tree", "no-flight-instance", "already-watching",
                       "watch-not-entered"):
            st = self._watching(watchEntryLeadSeconds=0.0)
            st, _ = mlib.kxrw_decide(st, snap(ut=1000.0))
            st, acts = mlib.kxrw_decide(
                st, seam("watch0", "ERROR", (("msg", reason),), ut=1000.5))
            self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.phase, reason)
            self.assertEqual([], acts, reason)
            self.assertEqual(reason, st.watch_reject_reason)
            self.assertEqual(1, st.watch_attempts)
            self.assertEqual(1230.0, st.playback_target_ut)   # 1200 + 30
            self.assertFalse(st.done)
        # And the predicate itself, in both directions.
        self.assertTrue(mlib.kxrw_watch_entry_retryable(
            "ERROR", "no-watchable-ghost"))
        self.assertFalse(mlib.kxrw_watch_entry_retryable("OK", ""))
        self.assertFalse(mlib.kxrw_watch_entry_retryable("ERROR", "unknown-tree"))
        self.assertFalse(mlib.kxrw_watch_entry_retryable("", ""))

    def test_the_retry_bound_advances_with_the_last_rejection_recorded(self):
        """Bound exhaustion is NOT a give-up: the ghost's spawn / replay / retire
        is in the log either way and the spec's contracts judge it, so the machine
        records the last refusal and flies the playback wait. MUTATION: flake here
        and a race the lane could not win becomes a driver-INVALID that discards
        the very evidence the contracts read."""
        st = self._watching(watchEntryLeadSeconds=0.0, watchEntryRetryFrames=25)
        ut = 1000.0
        for _ in range(80):
            tag = mlib.kxrw_watch_probe_tag(st.watch_probe)
            if st.watch_awaiting_since >= 0 and st.watch_attempts:
                s = seam(tag, "ERROR", (("msg", "no-watchable-ghost"),), ut=ut)
            else:
                s = snap(ut=ut)
            st, _ = mlib.kxrw_decide(st, s)
            ut += 0.5
            if st.phase != mlib.KXRW_WATCH:
                break
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.phase)
        self.assertFalse(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual("ERROR", st.watch_result)
        self.assertEqual("no-watchable-ghost", st.watch_reject_reason)
        self.assertGreater(st.watch_attempts, 1)
        self.assertEqual(1230.0, st.playback_target_ut)
        # The row is still MET - it certifies the sequence was driven, and how hard
        # the watch had to try is carried as detail.
        p = mlib.kxrw_params_from_dict(params())
        row = [r for r in mlib.evaluate_kxrw_assertions([], p, st)
               if r.name == "renderVerbsDriven"][0]
        self.assertEqual(st.watch_attempts, row.detail["enterWatchModeAttempts"])

    def test_a_stuck_clock_that_never_opens_the_window_names_its_own_giveup(self):
        """The one give-up the hold adds. It is a DRIVER failure (the clock never
        advanced), not a Parsek verdict about a replay - so unlike every refusal it
        flakes, and it says which clock it was waiting on."""
        st = self._watching(watchEntryLeadSeconds=5.0, watchEntryRetryFrames=4)
        for _ in range(10):
            st, acts = mlib.kxrw_decide(st, snap(ut=985.0))
            self.assertEqual([], acts)
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_WATCH, st.flake_phase)
        self.assertIn("never reached the replay window", st.flake_reason)
        self.assertIn("no-watchable-ghost", st.flake_reason)
        self.assertEqual(0, st.watch_attempts)

    def test_a_retry_that_goes_dark_still_trips_the_per_attempt_silence_bound(self):
        """`watchFrames` keeps its own narrower job after this change: it bounds
        ONE attempt's silence, measured from the frame that attempt went out. So a
        RETRY that never answers is caught the same way the first ask is - a
        silent seam is a transport fault at any attempt number."""
        st = self._watching(watchEntryLeadSeconds=0.0, watchFrames=3)
        st, _ = mlib.kxrw_decide(st, snap(ut=1000.0))
        st, _ = mlib.kxrw_decide(
            st, seam("watch0", "ERROR", (("msg", "no-watchable-ghost"),),
                     ut=1000.5))
        ut = 1001.0
        issued = False
        for _ in range(40):
            st, acts = mlib.kxrw_decide(st, snap(ut=ut))
            issued = issued or bool(acts)
            ut += 0.5
            if st.done:
                break
        self.assertTrue(issued, "the retry never went out")
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_WATCH, st.flake_phase)
        self.assertIn("never answered", st.flake_reason)
        self.assertIn("watch1", st.flake_reason)

    def test_the_two_new_knobs_are_declared_and_default_sanely(self):
        p = mlib.kxrw_params_from_dict(params())
        self.assertEqual(5.0, p.watch_entry_lead_seconds)
        self.assertEqual(240, p.watch_entry_retry_frames)
        # The hold alone can need ~(rewind lead + entry lead) of game time, which at
        # the 0.5 s poll is ~40 frames - `watchFrames`' ENTIRE default. That is why
        # the phase budget is a separate, larger knob. MUTATION: bound the hold with
        # watchFrames and the normal case sits on a give-up.
        p2 = mlib.kxrw_params_from_dict(params())
        hold_frames_needed = (15.0 + p2.watch_entry_lead_seconds) / 0.5
        self.assertGreater(p2.watch_entry_retry_frames, hold_frames_needed)


class PlaybackWaitTests(unittest.TestCase):
    """The wait is ARITHMETIC over two stamps the machine took itself, and its cap
    is measured in FRAMES because a stuck clock is what a UT budget cannot see."""

    def test_the_target_is_the_recorded_end_plus_the_margin(self):
        self.assertEqual(430.0, mlib.kxrw_playback_target_ut(400.0, 30.0))

    def test_an_unread_stamp_fails_the_wait_closed(self):
        """MUTATION: default the target to 0.0 on an unread commit UT and the
        mission declares a replay it never watched, on its first frame."""
        self.assertTrue(math.isnan(mlib.kxrw_playback_target_ut(float("nan"), 30.0)))
        self.assertFalse(mlib.kxrw_playback_complete(1e9, float("nan")))
        self.assertFalse(mlib.kxrw_playback_complete(float("nan"), 430.0))

    def test_the_gate_is_inclusive_at_the_target(self):
        self.assertFalse(mlib.kxrw_playback_complete(429.9, 430.0))
        self.assertTrue(mlib.kxrw_playback_complete(430.0, 430.0))

    def test_the_wait_ends_when_the_clock_passes_the_recorded_span(self):
        st = dataclasses.replace(machine(playbackMarginSeconds=30.0),
                                 phase=mlib.KXRW_WATCH, recording_end_ut=400.0)
        # Frame 1 issues the attempt (unread launch_ut -> the window gate fails
        # open); frame 2 carries its terminal.
        st, acts = mlib.kxrw_decide(st, snap(ut=199.0))
        self.assertEqual(["EnterWatchMode"], [a.seam_verb for a in acts])
        st, _ = mlib.kxrw_decide(st, seam("watch0", "OK", (), ut=200.0))
        self.assertEqual(430.0, st.playback_target_ut)
        st, _ = mlib.kxrw_decide(st, snap(ut=429.0))
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.phase)
        self.assertFalse(st.done)
        st, _ = mlib.kxrw_decide(st, snap(ut=431.0))
        self.assertEqual(mlib.KXRW_DONE, st.phase)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)      # the assertions judge, not the machine
        self.assertTrue(st.playback_reached)

    def test_a_stuck_clock_hits_the_frame_cap_and_names_it(self):
        st = dataclasses.replace(machine(playbackWaitFrames=5,
                                         playbackMarginSeconds=30.0),
                                 phase=mlib.KXRW_PLAYBACK_WAIT,
                                 recording_end_ut=400.0,
                                 playback_target_ut=430.0)
        for _ in range(10):
            st, _ = mlib.kxrw_decide(st, snap(ut=200.0))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.flake_phase)
        self.assertIn("never reached the playback target", st.flake_reason)
        self.assertFalse(st.playback_reached)

    def test_the_frame_cap_covers_the_widest_span_the_window_admits(self):
        """THE TWO KNOBS ARE ONE DECISION. The wait walks the clock from the
        post-rewind reading (launchUT - 15 s) to commitUT + margin, i.e.
        span + 45 s of REAL time at 1x (this lane uses no warp anywhere). A cap
        below (span_max + margin + 15) / poll cannot sit through a flight the
        recordedSpanWindowSeconds max explicitly admits - which is exactly what the
        old 1200 default did. MUTATION: drop the cap back to 1200 (or raise the
        span ceiling alone) and this reds."""
        p = mlib.kxrw_params_from_dict(params())
        span_max = p.recorded_span_window[1]
        lead = 15.0                      # RewindToLaunchLeadTimeSeconds
        poll = 0.5                       # the standard mission poll
        needed = (span_max + p.playback_margin + lead) / poll
        self.assertGreater(p.playback_wait_frames, needed,
                           "playbackWaitFrames=%d cannot cover a %.0f s span"
                           % (p.playback_wait_frames, span_max))

    def test_the_recorded_span_row_reads_the_two_machine_stamps(self):
        p = mlib.kxrw_params_from_dict(
            params(recordedSpanWindowSeconds={"min": 60.0, "max": 600.0}))
        st = dataclasses.replace(machine(), launch_ut=1000.0,
                                 recording_end_ut=1230.0)
        row = [r for r in mlib.evaluate_kxrw_assertions([], p, st)
               if r.name == "recordedSpanSeconds"][0]
        self.assertTrue(row.met)
        self.assertEqual(230.0, row.value)
        short = dataclasses.replace(st, recording_end_ut=1005.0)
        row = [r for r in mlib.evaluate_kxrw_assertions([], p, short)
               if r.name == "recordedSpanSeconds"][0]
        self.assertFalse(row.met)


class HappyPathTests(unittest.TestCase):
    """The whole sequence driven end to end at the decide level, with every
    assertion row met. Guards a phase whose exit was wired to the wrong successor:
    a per-phase cell can pass while the chain does not join up."""

    def test_the_full_lane_reaches_done_with_every_row_met(self):
        pdict = params(boosterStageCount=3, stageSettleFrames=2, coastSeconds=5.0,
                       coreDiscardApoapsisMeters=60000.0,
                       recordedSpanWindowSeconds={"min": 60.0, "max": 600.0})
        st = rolled_out(mlib.kxrw_initial_state(mlib.kxrw_params_from_dict(pdict)))
        st, _ = mlib.kxrw_decide(st, snap(ut=1000.0, altitude=0.0, throttle=0.0,
                                          available_thrust=0.0))
        st, _ = fly(st, ut=1001.0, altitude=200.0)          # peak thrust = LIT
        ut = 1040.0
        for _ in range(40):                                  # the three drops
            st, _ = fly(st, ut=ut, altitude=9000.0, throttle=0.0,
                        available_thrust=FLAMED)
            ut += 0.5
            if st.phase == mlib.KXRW_ASCENT and st.booster_drops_done >= 3:
                break
        self.assertEqual(3, st.booster_drops_done)
        # Climb to the core-discard apoapsis, then cut + discard + coast.
        st, _ = fly(st, ut=1150.0, altitude=55000.0, apoapsis=61000.0)
        self.assertEqual(mlib.KXRW_CORE_CUT, st.phase)
        st, _ = fly(st, ut=1151.0, altitude=55500.0, apoapsis=61000.0, throttle=0.0)
        st, _ = fly(st, ut=1152.0, altitude=56000.0, apoapsis=61000.0, throttle=0.0)
        self.assertEqual(mlib.KXRW_CORE_DISCARD, st.phase)
        st, _ = fly(st, ut=1153.0, altitude=56500.0, apoapsis=61000.0, throttle=0.0)
        self.assertEqual(mlib.KXRW_COAST, st.phase)
        st, acts = fly(st, ut=1160.0, altitude=58000.0, apoapsis=61000.0,
                       throttle=0.0)
        self.assertEqual(mlib.KXRW_TREE_STATE, st.phase)
        # The seam bridge.
        st, _ = mlib.kxrw_decide(st, seam("tree0", "OK", (("tree", "t_kx"),),
                                          ut=1161.0))
        st, _ = mlib.kxrw_decide(st, seam("commit", "OK", (), ut=1200.0))
        st, _ = mlib.kxrw_decide(st, seam("stop", "OK", (), ut=1201.0))
        st, acts = mlib.kxrw_decide(
            st, seam("idle0", "OK", (("recording", "false"),), ut=1202.0))
        self.assertEqual("InvokeRewindToLaunch", acts[0].seam_verb)
        # The reload: every frame arrives vessel_lost, and the clock goes back.
        st, _ = mlib.kxrw_decide(st, seam("rewind", "OK", (), ut=1202.0,
                                          vessel_lost=True))
        st, acts = mlib.kxrw_decide(st, snap(ut=985.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_AUTORECORD_OFF, st.phase)
        self.assertEqual(["SetSetting"], [a.seam_verb for a in acts])
        st, _ = mlib.kxrw_decide(st, seam("autorec", "OK", (), ut=985.5,
                                          vessel_lost=True))
        self.assertEqual(mlib.KXRW_WATCHER_LAUNCH, st.phase)
        st, acts = mlib.kxrw_decide(st, snap(ut=986.0, vessel_lost=True))
        self.assertEqual(mlib.ACTION_LAUNCH_VESSEL, acts[0].kind)
        st, _ = mlib.kxrw_decide(st, snap(ut=990.0, situation="PRE_LAUNCH",
                                          vessel_name=WATCHER))
        st, acts = mlib.kxrw_decide(st, snap(ut=991.0, situation="PRE_LAUNCH",
                                             vessel_name=WATCHER))
        self.assertEqual(mlib.KXRW_MAP_VIEW, st.phase)
        st, acts = mlib.kxrw_decide(st, seam("map", "OK", (), ut=992.0,
                                             situation="PRE_LAUNCH"))
        # THE MAP CLOSES AGAIN BEFORE THE WATCH: the operator rule is that the
        # replay is watched in FLIGHT view, not under the map overlay.
        self.assertEqual(mlib.KXRW_MAP_EXIT, st.phase)
        self.assertEqual(["ExitMapView"], [a.seam_verb for a in acts])
        st, acts = mlib.kxrw_decide(st, seam("mapexit", "OK", (), ut=992.5,
                                             situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_WATCH, st.phase)
        self.assertEqual([], acts)
        # THE HOLD. The replay window opens at launchUT (1000) + lead (5); the
        # post-rewind clock is still walking up from launchUT-15, so no attempt may
        # go out yet - this is exactly the five seconds the reading run lost.
        for ut in (993.0, 998.0, 1004.9):
            st, acts = mlib.kxrw_decide(st, snap(ut=ut, situation="PRE_LAUNCH"))
            self.assertEqual([], acts)
            self.assertEqual(mlib.KXRW_WATCH, st.phase)
        st, acts = mlib.kxrw_decide(st, snap(ut=1005.0, situation="PRE_LAUNCH"))
        self.assertEqual(["EnterWatchMode"], [a.seam_verb for a in acts])
        st, _ = mlib.kxrw_decide(st, seam("watch0", "OK", (("index", "0"),),
                                          ut=1006.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.phase)
        self.assertEqual(1230.0, st.playback_target_ut)     # 1200 commit + 30
        st, _ = mlib.kxrw_decide(st, snap(ut=1231.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_DONE, st.phase)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)

        # EVERY declared phase was actually walked EXCEPT the OPT-INs - the
        # chain joins up end to end. MUTATION: wire any phase's exit to the wrong
        # successor and the skipped one shows up here even when its own per-phase
        # cell passes.
        #
        # THE THREE OPT-INS ARE EXCLUDED BY DESIGN, and this line is the whole
        # compatibility statement: these params declare none of `partSweepSteps`,
        # `impactProfile` or `coastExitProfile`, so COAST advances straight to
        # TREE-STATE and TREE-STATE advances straight to COMMIT, exactly as they
        # did before any of the three features existed. A declaring params set
        # reaching them is the sibling cells (PartSweepTests / ImpactProfileTests /
        # CoastExitProfileTests).
        opt_in = {mlib.KXRW_PART_SWEEP, mlib.KXRW_COAST_EXIT} \
            | set(mlib.KXRW_IMPACT_PHASES) \
            | {mlib.KXRW_IMPACT_AUTORECORD_OFF, mlib.KXRW_IMPACT_COAST}
        self.assertEqual(set(mlib.KXRW_PHASES) - opt_in, set(st.phases_reached))
        self.assertEqual(set(), opt_in & set(st.phases_reached))
        self.assertEqual(31, len(mlib.KXRW_PHASES))

        rows = mlib.evaluate_kxrw_assertions([], mlib.kxrw_params_from_dict(pdict),
                                             st)
        unmet = [r.name for r in rows if not r.met]
        self.assertEqual([], unmet, [r.to_dict() for r in rows])
        self.assertEqual(8, len(rows))
        render = [r for r in rows if r.name == "renderVerbsDriven"][0]
        self.assertEqual("OK/OK/OK", render.value)

    def test_the_machine_is_idempotent_once_done(self):
        st = dataclasses.replace(machine(), done=True, phase=mlib.KXRW_DONE)
        again, acts = mlib.kxrw_decide(st, snap(ut=9999.0))
        self.assertIs(st, again)
        self.assertEqual([], acts)


class PartSweepTests(unittest.TestCase):
    """GS-6's scripted part-event timeline: the OPT-IN phase between COAST and
    TREE-STATE that fires one Parsek part-event family per settle window.

    The compatibility statement lives in HappyPathTests (no declared steps ->
    PART-SWEEP is never entered and the graph is the pre-GS-6 one); these cells
    own the sweep itself."""

    def _at_coast(self, **over):
        """A machine parked in COAST with the sweep params under test."""
        st = machine(**over)
        st = dataclasses.replace(st, phase=mlib.KXRW_COAST, phase_entry_ut=100.0,
                                 phase_frames=1)
        return st

    def test_no_declared_steps_keeps_coast_going_straight_to_tree_state(self):
        # THE COMPATIBILITY HINGE, as a direct call: the pure function that decides
        # it, and then the phase machine agreeing with it.
        self.assertEqual(mlib.KXRW_TREE_STATE, mlib.kxrw_coast_next_phase(()))
        self.assertEqual(mlib.KXRW_TREE_STATE, mlib.kxrw_coast_next_phase([]))
        self.assertEqual(mlib.KXRW_PART_SWEEP,
                         mlib.kxrw_coast_next_phase(("gear-down",)))

        st = self._at_coast()
        st, acts = mlib.kxrw_decide(st, snap(ut=999.0, situation="SUB_ORBITAL"))
        self.assertEqual(mlib.KXRW_TREE_STATE, st.phase)
        self.assertEqual([mlib.ACTION_PARSEK_SEAM_COMMAND], kinds(acts))

    def test_a_declared_sweep_fires_every_step_in_order_then_advances(self):
        steps = ["gear-down", "lights-on", "deployables-out", "chutes-arm"]
        st = self._at_coast(partSweepSteps=steps, partSweepSettleFrames=2)
        st, acts = mlib.kxrw_decide(st, snap(ut=999.0, situation="SUB_ORBITAL"))
        self.assertEqual(mlib.KXRW_PART_SWEEP, st.phase)
        self.assertEqual([], acts)   # entry frame commands nothing

        fired = []
        for _ in range(60):
            st, acts = mlib.kxrw_decide(st, snap(ut=1000.0, situation="SUB_ORBITAL"))
            fired.extend(acts)
            if st.phase != mlib.KXRW_PART_SWEEP:
                break

        self.assertEqual(mlib.KXRW_TREE_STATE, st.phase)
        # The four families, in the spec's order, and NOTHING else - then the one
        # RecordingState probe that opens TREE-STATE.
        self.assertEqual(
            [mlib.ACTION_SET_GEAR, mlib.ACTION_SET_LIGHTS,
             mlib.ACTION_SET_DEPLOYABLES, mlib.ACTION_ARM_CHUTES,
             mlib.ACTION_PARSEK_SEAM_COMMAND],
            kinds(fired))
        self.assertEqual([1.0, 1.0, 1.0], [a.value for a in fired[:3]])

    def test_the_settle_gap_is_honoured_between_steps(self):
        # Two part actions in ONE physics frame can coalesce into a single recorded
        # event, which would under-count families in the replay. MUTATION: drop the
        # gap and both steps land on consecutive frames here.
        st = self._at_coast(partSweepSteps=["gear-down", "gear-up"],
                            partSweepSettleFrames=5)
        st, _ = mlib.kxrw_decide(st, snap(ut=999.0, situation="SUB_ORBITAL"))
        seen = []
        for _ in range(12):
            st, acts = mlib.kxrw_decide(st, snap(ut=1000.0, situation="SUB_ORBITAL"))
            seen.append(len(acts))
            if st.phase != mlib.KXRW_PART_SWEEP:
                break
        # First step on the first frame, then four silent frames, then the second.
        self.assertEqual(1, seen[0])
        self.assertEqual([0, 0, 0, 0], seen[1:5])
        self.assertEqual(1, seen[5])

    def test_an_unknown_step_name_flakes_at_the_entry_gate_rather_than_being_skipped(self):
        # A silently-dropped step is a family that never fires, and the lane would
        # then read as "the applier never logged it" - blaming the product for a
        # spec typo. FAIL CLOSED, naming the vocabulary.
        self.assertEqual(("gear-DOWN", "wings-out"),
                         mlib.kxrw_sweep_steps_valid(
                             ["gear-down", "gear-DOWN", "wings-out"]))
        st = self._at_coast(partSweepSteps=["gear-down", "wings-out"])
        st, acts = mlib.kxrw_decide(st, snap(ut=999.0, situation="SUB_ORBITAL"))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("wings-out", st.flake_reason)
        self.assertIn("gear-down", st.flake_reason)   # names the vocabulary
        self.assertEqual([], acts)

    def test_the_step_vocabulary_maps_one_to_one_onto_real_action_kinds(self):
        # Every name resolves to an Action, no name resolves to None, and the
        # vocabulary the give-up text prints is the same set the mapping holds.
        self.assertEqual(set(mlib.KXRW_SWEEP_STEP_NAMES),
                         set(mlib.KXRW_SWEEP_STEP_ACTIONS))
        for name in mlib.KXRW_SWEEP_STEP_NAMES:
            action = mlib.kxrw_sweep_action_for_step(name)
            self.assertIsNotNone(action, name)
            self.assertTrue(str(action.kind), name)
        self.assertIsNone(mlib.kxrw_sweep_action_for_step("nope"))
        self.assertIsNone(mlib.kxrw_sweep_action_for_step(""))

    def test_every_action_kind_the_sweep_can_emit_touches_a_vessel(self):
        # NONE of the sweep kinds may join VESSEL_FREE_ACTION_KINDS: every one
        # reads `v` / `control` in the runner, and a kind dispatched before the
        # active-vessel resolve would raise there and end the mission as an
        # unclassifiable MISSION-ERROR.
        for name in mlib.KXRW_SWEEP_STEP_NAMES:
            kind = mlib.kxrw_sweep_action_for_step(name).kind
            self.assertNotIn(kind, mlib.VESSEL_FREE_ACTION_KINDS, name)

    def test_the_sweep_is_bounded_so_a_stalled_machine_gives_up_by_name(self):
        st = self._at_coast(partSweepSteps=["gear-down", "gear-up"],
                            partSweepSettleFrames=10000,
                            partSweepFrames=20)
        st, _ = mlib.kxrw_decide(st, snap(ut=999.0, situation="SUB_ORBITAL"))
        for _ in range(40):
            st, _ = mlib.kxrw_decide(st, snap(ut=1000.0, situation="SUB_ORBITAL"))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("PART-SWEEP", st.flake_reason)
        self.assertIn("gear-up", st.flake_reason)

    def test_part_sweep_is_a_flight_phase_so_the_vessel_lost_carve_out_excludes_it(self):
        # It runs on a live, still-recording craft between two flight phases, so a
        # vessel_lost frame in it is a real loss, not the expected post-rewind
        # scene churn.
        self.assertIn(mlib.KXRW_PART_SWEEP, mlib.KXRW_FLIGHT_PHASES)
        self.assertNotIn(mlib.KXRW_PART_SWEEP, mlib.KXRW_POST_REWIND_PHASES)
        self.assertNotIn(mlib.KXRW_PART_SWEEP, mlib.KXRW_VESSEL_LOST_EXPECTED_PHASES)


class ImpactProfileTests(unittest.TestCase):
    """THE SECOND OPT-IN: `impactProfile`, the lane that ends its flight in a
    DELIBERATE CRASH and reaches the rewind through the Space Center.

    THE ASCENT IS NOT PART OF THE BRANCH, and that is the shape the measured
    flights bought. An earlier revision diverged at the LAST BOOSTER DROP (cut
    there, never throttle up) and the stack fell back within 330-374 m of the pad,
    and the launch that followed hung; the pad hypothesis (KSP's
    `PreFlightTests.LaunchSiteClear.Test()` waiting on an obstruction dialog) was
    REFUTED by the far crash of runs 2026-09-08_1429 / _1503_a2, which hung the
    same way with only the clamps at the pad - the blocker is the post-crash EMPTY
    ROSTER, answered by probe-cored launches. The profile keeps the far shape
    because it is the shape every green flight flew: it runs the ordinary ascent,
    discards the fueled core the ordinary way, and lets the unpowered pod stack
    fall from ~60 km onto ground 50 km or more downrange.

    THE PRODUCT FACTS THE REST OF THE BRANCH IS SHAPED BY. Parsek does NOT commit a
    tree in flight once the active vessel is destroyed:
    `ShowPostDestructionTreeMergeDialog` finalizes it and STASHES it as pending,
    after which CommitTree is refused `no-active-tree`; `ExitToSpaceCenter` is what
    commits it (the arrival's auto-commit under autoMerge), and
    `InvokeRewindToLaunch` is RequiresFlight, so a craft has to be on the pad before
    the rewind can be commanded at all - which is GS-4's own post-rewind launch, a
    kRPC call issued from SPACECENTER onto a clear pad. And when the stack breaks up
    KSP hands active-vessel to a SURVIVING FRAGMENT: with `autoRecordOnLaunch` still
    armed Parsek opens a second recording tree on it, which flips the exit gate's
    pinned tree-state token (the spec accepts `hasActiveTree=false
    hasPendingTree=true` and `hasActiveTree=true hasPendingTree=false`, the two
    measured crash shapes, and a fragment tree is neither) and lands in the save
    the spec's log contracts read, so the disarm runs BEFORE the fall.

    The compatibility statement lives in HappyPathTests (neither opt-in declared ->
    none of these phases is entered and the graph is the pre-profile one); the
    first cells here pin the same fact at the ONE frame the branch is taken."""

    IMPACT = {"impactProfile": True, "boosterStageCount": 1,
              "stageSettleFrames": 2}

    def _to_last_drop(self, **over):
        """Drive a fresh machine to the frame the LAST declared booster drop
        clicks, and return that frame's (state, actions).

        The altitude MOVES on every frame, and that is not cosmetic: the
        frozen-telemetry detector reads a stream whose UT advances while the other
        readings stay bit-identical as a destroyed craft, so a fixture that holds
        one altitude across the settle window trips the detector on the ascent."""
        over.setdefault("boosterStageCount", 1)
        over.setdefault("stageSettleFrames", 2)
        st = rolled_out(machine(**over))
        st, _ = mlib.kxrw_decide(st, snap(ut=0.0, altitude=0.0, throttle=0.0,
                                          available_thrust=0.0))
        st, _ = fly(st, ut=1.0, altitude=100.0)                  # peak = LIT
        st, _ = fly(st, ut=40.0, altitude=9000.0, available_thrust=FLAMED)
        self.assertEqual(mlib.KXRW_BOOSTER_CUT, st.phase)
        for ut, alt in ((40.5, 9100.0), (41.0, 9200.0)):
            st, _ = fly(st, ut=ut, altitude=alt, throttle=0.0,
                        available_thrust=FLAMED)
        self.assertEqual(mlib.KXRW_BOOSTER_STAGE, st.phase)
        return fly(st, ut=41.5, altitude=9300.0, throttle=0.0,
                   available_thrust=FLAMED)

    def _to_tree_state(self, **over):
        """Drive a fresh machine through the UNCHANGED ascent to TREE-STATE, and
        return that frame's (state, actions).

        Every step of it is the ordinary lane's: the last drop throttles back up,
        the core gate opens on apoapsis, CORE-CUT holds until the throttle READS
        zero, CORE-DISCARD stages the fueled core and COAST runs its window out.
        The profile's own phases start after this."""
        params_ = dict(self.IMPACT)
        params_.update(over)
        st, _ = self._to_last_drop(**params_)
        self.assertEqual(mlib.KXRW_ASCENT, st.phase)
        st, _ = fly(st, ut=50.0, altitude=40000.0, apoapsis=61000.0)
        self.assertEqual(mlib.KXRW_CORE_CUT, st.phase)
        for ut, alt in ((50.5, 40500.0), (51.0, 41000.0)):
            st, _ = fly(st, ut=ut, altitude=alt, apoapsis=61000.0, throttle=0.0)
        self.assertEqual(mlib.KXRW_CORE_DISCARD, st.phase)
        st, _ = fly(st, ut=51.5, altitude=41500.0, apoapsis=61000.0, throttle=0.0)
        self.assertEqual(mlib.KXRW_COAST, st.phase)
        st, acts = fly(st, ut=75.0, altitude=59000.0, apoapsis=61000.0,
                       throttle=0.0)
        self.assertEqual(mlib.KXRW_TREE_STATE, st.phase)
        return st, acts

    def _at_impact_autorecord_off(self, **over):
        """A machine parked in IMPACT-AUTORECORD-OFF with a captured tree id,
        reached through the real TREE-STATE branch rather than hand-built."""
        st, _ = self._to_tree_state(**over)
        st, acts = mlib.kxrw_decide(st, seam("tree0", "OK", (("tree", "t_kx"),),
                                             ut=76.0, situation="FLYING"))
        self.assertEqual(mlib.KXRW_IMPACT_AUTORECORD_OFF, st.phase)
        return st, acts

    def _coasting(self, **over):
        """A machine parked in IMPACT-COAST, past the disarm.

        `launch_ut` is 0 on this fixture, so every UT a cell hands the coast has to
        stay inside `flightMaxSeconds`: IMPACT-COAST is a FLIGHT phase and the
        whole-flight budget bounds it exactly as it bounds the ascent."""
        st, _ = self._at_impact_autorecord_off(**over)
        st, acts = mlib.kxrw_decide(st, seam("impautorec", "OK", (), ut=76.5,
                                             situation="FLYING"))
        self.assertEqual(mlib.KXRW_IMPACT_COAST, st.phase)
        self.assertEqual([], acts)
        return st

    # ---- (a) the ascent is the ordinary one, on both profiles ---------------

    def test_omitting_the_key_keeps_the_last_drop_throttling_back_up(self):
        """The pre-profile lane, unchanged."""
        st, acts = self._to_last_drop()
        self.assertEqual([mlib.ACTION_ACTIVATE_STAGE, mlib.ACTION_SET_THROTTLE],
                         kinds(acts))
        self.assertEqual(mlib.KXRW_ASCENT, st.phase)
        self.assertNotIn(mlib.ACTION_AP_DISENGAGE, kinds(acts))
        self.assertEqual([], [a for a in acts if a.seam_verb])

    def test_the_last_drop_throttles_back_up_with_the_key_on_too(self):
        """THE FLOWN SHAPE, as a one-frame pin. MUTATION: restore the old branch
        (cut here, no throttle-up, straight to TREE-STATE) and the stack falls
        back within 330-374 m of the pad (runs 2026-09-08_1130 / _1157_a2 and
        _1302 / _1331_a2), the slow near-vertical impact whose crash shape
        (`type=Breakup, cause=CRASH`, the stash path) GS-7's tokens are NOT cut
        to. Those runs hung on the roster, not the pad (see the class docstring);
        the pin keeps the far shape the green flights measured."""
        st, acts = self._to_last_drop(**self.IMPACT)
        self.assertEqual([mlib.ACTION_ACTIVATE_STAGE, mlib.ACTION_SET_THROTTLE],
                         kinds(acts))
        self.assertEqual(mlib.KXRW_ASCENT, st.phase)
        self.assertNotIn(mlib.ACTION_CUT_THROTTLE, kinds(acts))
        self.assertNotIn(mlib.ACTION_AP_DISENGAGE, kinds(acts))
        self.assertEqual([], [a for a in acts if a.seam_verb])
        self.assertNotIn(mlib.KXRW_TREE_STATE, st.phases_reached)
        self.assertEqual(1, st.booster_drops_done)

    def test_the_core_gate_and_the_coast_run_unchanged_on_this_profile(self):
        """WHAT ACTUALLY MAKES THE STACK FALL. The fueled core comes off with the
        throttle READ at zero, exactly as on the ordinary lane, and nothing
        relights: the profile needs no special cut because the core gate already
        left the pod stack unpowered. MUTATION: skip the core gate on this profile
        and the stack still has a Mainsail under it."""
        st, _ = self._to_tree_state()
        for name in (mlib.KXRW_CORE_CUT, mlib.KXRW_CORE_DISCARD, mlib.KXRW_COAST):
            self.assertIn(name, st.phases_reached)
        self.assertTrue(st.core_discard_commanded)
        self.assertTrue(st.core_cut_throttle_observed)
        self.assertEqual(0.0, st.core_cut_throttle_reading)

    # ---- (b) TREE-STATE's opt-in exit: the disarm, not a commit -------------

    def test_tree_state_disarms_auto_record_and_commands_no_commit(self):
        """TWO FACTS IN ONE FRAME. NO CommitTree: the crash is about to make it
        impossible (`no-active-tree`), so issuing one would flake on a verb the
        lane never needed. AND the disarm goes out HERE, before the fall - a
        fragment that inherits active-vessel with the trigger armed opens a second
        recording tree in the save the log contracts read. MUTATION: wire this exit
        to COMMIT, or to IMPACT-COAST with no SetSetting, and either failure
        follows."""
        st, acts = self._at_impact_autorecord_off()
        self.assertEqual("t_kx", st.tree_id)
        self.assertEqual(1, len(acts))
        self.assertEqual(mlib.ACTION_PARSEK_SEAM_COMMAND, acts[0].kind)
        self.assertEqual("SetSetting", acts[0].seam_verb)
        self.assertEqual("impautorec", acts[0].seam_tag)
        self.assertEqual((("name", "autoRecordOnLaunch"), ("value", "false")),
                         acts[0].seam_args)
        # The SAME pair the post-rewind disarm sends, under a DIFFERENT wire id.
        self.assertEqual(mlib.KXRW_TAG_IMPACT_AUTORECORD, acts[0].seam_tag)
        self.assertNotEqual(mlib.KXRW_TAG_AUTORECORD,
                            mlib.KXRW_TAG_IMPACT_AUTORECORD)
        self.assertNotIn(mlib.KXRW_COMMIT, st.phases_reached)
        self.assertNotIn(mlib.KXRW_STOP, st.phases_reached)
        self.assertNotIn(mlib.KXRW_RECORDER_IDLE, st.phases_reached)
        self.assertEqual("", st.commit_result)
        # The recorder was never OBSERVED idle on this path, and the machine must
        # not pretend it was: nothing here stops the recorder, so the evidence
        # simply does not exist.
        self.assertFalse(st.recorder_idle_observed)

    def test_the_disarm_ok_releases_the_fall(self):
        st = self._coasting()
        self.assertEqual("OK", st.impact_autorecord_off_result)
        self.assertEqual("t_kx", st.tree_id)

    def test_a_refused_disarm_is_fatal_and_names_the_fragment_recording(self):
        """FATAL, unlike a refused render verb: this is a DRIVER-SEQUENCING
        precondition. MUTATION: record it and fly on, and the break-up's surviving
        fragment authors a tree of its own in the save the spec reads - which can
        also refuse the ExitToSpaceCenter this profile's commit depends on."""
        for reason in ("unknown-setting", "value-rejected"):
            st, _ = self._at_impact_autorecord_off()
            st, acts = mlib.kxrw_decide(
                st, seam("impautorec", "ERROR", (("msg", reason),), ut=77.0,
                         situation="FLYING"))
            self.assertTrue(st.done, reason)
            self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
            self.assertEqual(mlib.KXRW_IMPACT_AUTORECORD_OFF, st.flake_phase)
            self.assertEqual("ERROR", st.impact_autorecord_off_result)
            self.assertIn(reason, st.flake_reason)
            self.assertIn("SECOND recording tree", st.flake_reason)
            self.assertEqual([], acts)
            self.assertNotIn(mlib.KXRW_IMPACT_COAST, st.phases_reached)

    def test_a_silent_disarm_hits_the_shared_frame_bound(self):
        """It reuses `autoRecordOffFrames` rather than declaring a knob of its own:
        the two phases issue the identical one-frame SetSetting verb, and a second
        name for one number is the one that drifts."""
        st, _ = self._at_impact_autorecord_off(autoRecordOffFrames=3)
        for i in range(8):
            st, _ = mlib.kxrw_decide(st, snap(ut=77.0 + i, altitude=59000.0 - i,
                                              situation="FLYING"))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_IMPACT_AUTORECORD_OFF, st.flake_phase)
        self.assertIn("never answered", st.flake_reason)

    # ---- (c) + (d) the two impact readings ----------------------------------

    def test_a_lost_vessel_in_impact_coast_is_the_signal_not_the_terminal(self):
        """READING 1. MUTATION: leave IMPACT-COAST out of the generic loss block's
        named branch and the very event the profile flew for ends the mission as
        MISSION-ASSERT-FAIL."""
        st = self._coasting()
        st, _ = mlib.kxrw_decide(st, snap(ut=200.0, situation="FLYING",
                                          altitude=5000.0))
        # The crash frame's own clock is UNREADABLE, which is the normal shape:
        # the stamp has to come off the last finite reading or it poisons
        # recording_end_ut, which is what the playback wait targets.
        st, acts = mlib.kxrw_decide(st, snap(ut=float("nan"), vessel_lost=True))
        self.assertFalse(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(mlib.KXRW_IMPACT_SETTLE, st.phase)
        self.assertTrue(st.impact_observed)
        self.assertEqual("vessel-lost", st.impact_observed_by)
        self.assertEqual(200.0, st.impact_ut)
        self.assertEqual(200.0, st.recording_end_ut)
        self.assertEqual([], acts)

    def test_the_frozen_telemetry_trip_in_impact_coast_is_the_same_signal(self):
        """READING 2, and it is not redundant: KSP routinely keeps a live
        active-vessel handle after a crash by handing it to a surviving debris
        piece, whose readings then FREEZE. MUTATION: handle only vessel_lost and
        this whole population ends the mission instead of driving it on."""
        st = self._coasting(frozenTelemetrySamples=3)
        # UT ADVANCES while every other reading stays bit-identical: that stream IS
        # the detector's signature, and it is what a debris handle reports.
        last = float("nan")
        for i in range(8):
            last = 200.0 + 0.5 * i
            st, acts = mlib.kxrw_decide(st, snap(ut=last, altitude=0.0,
                                                 situation="LANDED"))
            if st.phase != mlib.KXRW_IMPACT_COAST:
                break
        self.assertFalse(st.done)
        self.assertEqual(mlib.KXRW_IMPACT_SETTLE, st.phase)
        self.assertTrue(st.impact_observed)
        self.assertEqual("frozen-telemetry", st.impact_observed_by)
        self.assertEqual(last, st.impact_ut)
        self.assertEqual(last, st.recording_end_ut)

    def test_whichever_reading_fires_first_wins_and_the_other_cannot_restamp(self):
        """IDEMPOTENCE. The two readings routinely arrive together (a destroyed
        craft freezes AND goes unreadable), so the second must not move a stamp the
        first already took. MUTATION: drop the `impact_observed` latch and a later
        UT overwrites recording_end_ut, moving the playback target."""
        st = self._coasting()
        st, _ = mlib.kxrw_decide(st, snap(ut=200.0, situation="FLYING"))
        st, _ = mlib.kxrw_decide(st, snap(ut=float("nan"), vessel_lost=True))
        self.assertEqual(200.0, st.impact_ut)
        # More lost frames land in IMPACT-SETTLE: expected there, counted, and no
        # restamp.
        for _ in range(3):
            st, _ = mlib.kxrw_decide(st, snap(ut=260.0, vessel_lost=True))
        self.assertEqual(200.0, st.impact_ut)
        self.assertEqual(200.0, st.recording_end_ut)
        self.assertEqual("vessel-lost", st.impact_observed_by)
        self.assertEqual(3, st.post_impact_vessel_lost_frames)
        # And the direct call is idempotent on its own.
        again = mlib._kxrw_observe_impact(st, snap(ut=260.0), "frozen-telemetry")
        self.assertIs(st, again)

    # ---- (e) the mutation guard ---------------------------------------------

    def test_a_lost_vessel_in_any_other_flight_phase_is_still_fatal(self):
        """THE CARVE-OUT IS ONE PHASE WIDE. MUTATION: widen the impact branch to
        `state.phase in KXRW_FLIGHT_PHASES` and a craft destroyed on the ascent -
        the failure the detector exists for - is silently re-read as a successful
        impact. IMPACT-AUTORECORD-OFF is in the list on purpose: the stack is still
        alive and coasting there, so a loss is a craft destroyed BEFORE the profile
        got to disarm anything."""
        for phase in (mlib.KXRW_ASCENT, mlib.KXRW_BOOSTER_CUT,
                      mlib.KXRW_BOOSTER_STAGE, mlib.KXRW_CORE_CUT,
                      mlib.KXRW_CORE_DISCARD, mlib.KXRW_COAST,
                      mlib.KXRW_PART_SWEEP, mlib.KXRW_IMPACT_AUTORECORD_OFF):
            st = dataclasses.replace(machine(**self.IMPACT), phase=phase)
            st, _ = mlib.kxrw_decide(st, snap(ut=30.0, vessel_lost=True))
            self.assertTrue(st.done, phase)
            self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict, phase)
            self.assertIn("vessel-lost", st.loss_reason)
            self.assertFalse(st.impact_observed, phase)

    # ---- (f) the coast bound ------------------------------------------------

    def test_a_stack_that_never_hits_the_ground_gives_up_by_name(self):
        """Neither reading can see a stack that SURVIVED - a soft landing, a stack
        still flying, a stalled clock - so the bound is the only thing that ends
        this phase in that case, and it has to SAY which of those it is naming."""
        st = self._coasting(impactCoastFrames=5)
        for i in range(12):
            st, acts = mlib.kxrw_decide(st, snap(ut=200.0 + i, altitude=1000.0 - i,
                                                 situation="FLYING"))
            self.assertEqual([], acts)
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_IMPACT_COAST, st.flake_phase)
        self.assertIn("no impact was observed", st.flake_reason)
        self.assertIn("SURVIVED", st.flake_reason)
        self.assertFalse(st.impact_observed)

    # ---- (g) IMPACT-SETTLE -> the exit ---------------------------------------

    def _at_sc_exit(self, **over):
        """A machine parked in SC-EXIT, driven out of IMPACT-SETTLE by holding the
        settle for the frames the params declare."""
        params_ = dict(self.IMPACT)
        params_.update(over)
        st = dataclasses.replace(machine(**params_),
                                 phase=mlib.KXRW_IMPACT_SETTLE, tree_id="t_kx",
                                 impact_autorecord_off_result="OK",
                                 impact_observed=True, impact_observed_by="vessel-lost",
                                 impact_ut=1200.0, recording_end_ut=1200.0)
        settle = mlib.kxrw_params_from_dict(params_).impact_settle_frames
        for _ in range(settle):
            st, _ = mlib.kxrw_decide(st, snap(ut=1200.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_SC_EXIT, st.phase)
        return st

    def test_the_settle_is_held_before_the_exit_is_commanded(self):
        """THE HOLD IS NOT CEREMONY: the C# finalize waits up to 5 s of REAL time
        for the destruction coalescer before it stashes the tree, and the exit
        tears the scene that finalize runs in down. MUTATION: exit on the first
        frame and the run races the very stash the whole hop depends on."""
        st = dataclasses.replace(machine(**self.IMPACT, impactSettleFrames=4),
                                 phase=mlib.KXRW_IMPACT_SETTLE, tree_id="t_kx",
                                 impact_observed=True, impact_ut=1200.0,
                                 recording_end_ut=1200.0)
        for _ in range(3):
            # vessel_lost is the EXPECTED reading here - the craft is gone.
            st, acts = mlib.kxrw_decide(st, snap(ut=1200.0, vessel_lost=True))
            self.assertEqual(mlib.KXRW_IMPACT_SETTLE, st.phase)
            self.assertEqual([], acts)
        st, acts = mlib.kxrw_decide(st, snap(ut=1200.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_SC_EXIT, st.phase)
        self.assertEqual(1, len(acts))
        self.assertEqual(mlib.ACTION_PARSEK_SEAM_COMMAND, acts[0].kind)
        self.assertEqual("ExitToSpaceCenter", acts[0].seam_verb)
        self.assertEqual(mlib.KXRW_TAG_SC_EXIT, acts[0].seam_tag)
        self.assertEqual((), acts[0].seam_args)
        self.assertFalse(st.done)
        self.assertEqual(4, st.post_impact_vessel_lost_frames)
        # NOTHING is stamped here: the pre-rewind clock belongs to TEMP-READY, on
        # the frame the rewind actually goes out.
        self.assertTrue(math.isnan(st.pre_rewind_ut))

    # ---- (h) SC-EXIT --------------------------------------------------------

    def test_the_exit_ok_reads_the_committed_count_rather_than_assuming_it(self):
        """The exit's OK says a SCENE CHANGED; it does not say a tree was
        committed. MUTATION: advance straight to the launch on the OK and the lane
        puts a craft on the pad and rewinds against a world that may still hold a
        pending tree - exactly the COMMANDED-vs-OBSERVED confusion the SPACECENTER
        clock gate exists for one phase later."""
        st = self._at_sc_exit()
        st, acts = mlib.kxrw_decide(st, seam("scexit", "OK", (), ut=1200.0,
                                             vessel_lost=True))
        self.assertEqual(mlib.KXRW_SC_COMMITTED, st.phase)
        self.assertEqual("OK", st.sc_exit_result)
        self.assertEqual(1, len(acts))
        self.assertEqual("ListHandles", acts[0].seam_verb)
        self.assertEqual((("kind", "committed"),), acts[0].seam_args)
        self.assertEqual("sccommit0", acts[0].seam_tag)
        self.assertEqual([], [a for a in acts
                              if a.kind == mlib.ACTION_LAUNCH_VESSEL])

    def test_a_refused_exit_is_fatal_and_quotes_parseks_own_reason(self):
        """Unlike a refused render verb: the exit is this profile's ONLY route to a
        committed tree, so flying on would drive a rewind against a pending one.
        The refusal word is quoted, never guessed at (R1 flight 1's lesson)."""
        st = self._at_sc_exit()
        st, acts = mlib.kxrw_decide(
            st, seam("scexit", "ERROR", (("msg", "dialog-required"),), ut=1200.0,
                     vessel_lost=True))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_SC_EXIT, st.flake_phase)
        self.assertEqual("ERROR", st.sc_exit_result)
        self.assertEqual("dialog-required", st.sc_exit_reject_reason)
        self.assertIn("dialog-required", st.flake_reason)
        self.assertIn("no-active-tree", st.flake_reason)
        self.assertEqual([], acts)
        self.assertNotIn(mlib.KXRW_SC_COMMITTED, st.phases_reached)

    def test_a_silent_exit_hits_its_own_frame_bound(self):
        st = self._at_sc_exit(scExitFrames=3)
        for i in range(8):
            st, _ = mlib.kxrw_decide(st, snap(ut=1200.0, vessel_lost=True))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_SC_EXIT, st.flake_phase)
        self.assertIn("never answered", st.flake_reason)

    # ---- (i) SC-COMMITTED ---------------------------------------------------

    def _at_sc_committed(self, **over):
        st = self._at_sc_exit(**over)
        st, _ = mlib.kxrw_decide(st, seam("scexit", "OK", (), ut=1200.0,
                                          vessel_lost=True))
        self.assertEqual(mlib.KXRW_SC_COMMITTED, st.phase)
        return st

    def test_a_committed_count_releases_the_throwaway_launch(self):
        """count >= 1 is the OBSERVED commit, and what it releases is the launch
        that gives the rewind a FLIGHT scene. The craft launched is the RECORDED
        one, never the watcher (see the crew-roster cell below). MUTATION: rewind
        directly and InvokeRewindToLaunch's RequiresFlight gate refuses it at the
        Space Center."""
        st = self._at_sc_committed()
        st, acts = mlib.kxrw_decide(
            st, seam("sccommit0", "OK", (("kind", "committed"), ("count", "3")),
                     ut=1200.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_TEMP_LAUNCH, st.phase)
        self.assertEqual(3, st.committed_handle_count)
        # NOTHING rides the transition: TEMP-LAUNCH issues its own click on its own
        # first frame, WATCHER-LAUNCH's shape exactly.
        self.assertEqual([], acts)
        st, acts = mlib.kxrw_decide(st, snap(ut=1200.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_TEMP_READY, st.phase)
        self.assertEqual(1, len(acts))
        self.assertEqual(mlib.ACTION_LAUNCH_VESSEL, acts[0].kind)
        self.assertEqual(CRAFT, acts[0].text)
        self.assertEqual("LaunchPad", acts[0].launch_site)
        self.assertTrue(st.temp_launch_commanded)

    def test_a_zero_count_reprobes_under_a_fresh_tag_and_then_names_the_giveup(self):
        """count=0 means the arrival's auto-commit has not landed in the store yet,
        which is a WAIT rather than a verdict - so the phase re-asks. A REUSED tag
        would be a reused wire id, and the C# seam SKIPS DUPLICATE IDS, so every
        re-probe after the first would be a silent no-op. MUTATION: flake on the
        first zero and a healthy run dies on one frame of lag."""
        st = self._at_sc_committed(scCommittedFrames=6)
        st, acts = mlib.kxrw_decide(
            st, seam("sccommit0", "OK", (("count", "0"),), ut=1200.0,
                     vessel_lost=True))
        self.assertEqual(mlib.KXRW_SC_COMMITTED, st.phase)
        self.assertEqual(["sccommit1"], [a.seam_tag for a in acts])
        self.assertEqual(["ListHandles"], [a.seam_verb for a in acts])
        self.assertEqual(0, st.committed_handle_count)
        tags = []
        for i in range(1, 12):
            st, acts = mlib.kxrw_decide(
                st, seam("sccommit%d" % i, "OK", (("count", "0"),), ut=1200.0,
                         vessel_lost=True))
            tags += [a.seam_tag for a in acts]
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_SC_COMMITTED, st.flake_phase)
        self.assertIn("never auto-committed at the Space Center", st.flake_reason)
        self.assertEqual(len(tags), len(set(tags)))       # every wire id distinct
        self.assertNotIn(mlib.KXRW_TEMP_LAUNCH, st.phases_reached)

    def test_an_unreadable_count_fails_closed(self):
        """FAIL CLOSED, the R1 RESOLVE shape: a count the mission could not read is
        not evidence that a tree committed, and the next act is irreversible."""
        for payload in ((), (("count", "many"),)):
            st = self._at_sc_committed()
            st, acts = mlib.kxrw_decide(
                st, seam("sccommit0", "OK", payload, ut=1200.0, vessel_lost=True))
            self.assertTrue(st.done, payload)
            self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
            self.assertEqual(mlib.KXRW_SC_COMMITTED, st.flake_phase)
            self.assertIn("no readable `count` field", st.flake_reason)
            self.assertEqual([], acts)
        # And the shared parse itself, in both directions.
        self.assertEqual(3, mlib._seam_parse_int("3"))
        self.assertIsNone(mlib._seam_parse_int(""))
        self.assertIsNone(mlib._seam_parse_int("many"))

    def test_a_refused_or_silent_list_handles_flakes(self):
        st = self._at_sc_committed()
        st, _ = mlib.kxrw_decide(
            st, seam("sccommit0", "ERROR", (("msg", "kind-arg-missing"),),
                     ut=1200.0, vessel_lost=True))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("kind-arg-missing", st.flake_reason)

        st = self._at_sc_committed(scCommittedFrames=3)
        for _ in range(8):
            st, _ = mlib.kxrw_decide(st, snap(ut=1200.0, vessel_lost=True))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("never answered", st.flake_reason)

    # ---- (j) TEMP-LAUNCH / TEMP-READY: the FLIGHT scene the rewind needs -----

    def _at_temp_launch(self, **over):
        st = self._at_sc_committed(**over)
        st, _ = mlib.kxrw_decide(
            st, seam("sccommit0", "OK", (("count", "2"),), ut=1200.0,
                     vessel_lost=True))
        self.assertEqual(mlib.KXRW_TEMP_LAUNCH, st.phase)
        return st

    def _at_temp_ready(self, **over):
        st = self._at_temp_launch(**over)
        st, _ = mlib.kxrw_decide(st, snap(ut=1200.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_TEMP_READY, st.phase)
        return st

    def test_the_throwaway_craft_is_launched_after_the_committed_read(self):
        """THE PHASE ORDER, as a fact rather than as prose. The launch is GS-4's
        own post-rewind WATCHER-LAUNCH operation: a kRPC call issued from the SPACE
        CENTER, which has worked on every GS-4 / GS-6 flight. What blocks such a
        call is a refused PRE-FLIGHT CHECK, not the scene - it raises a dialog and
        `LaunchVessel` yields on it forever - and the far-downrange crash is what
        keeps `LaunchSiteClear` satisfied."""
        i = mlib.KXRW_PHASES.index
        self.assertLess(i(mlib.KXRW_SC_EXIT), i(mlib.KXRW_TEMP_LAUNCH))
        self.assertLess(i(mlib.KXRW_SC_COMMITTED), i(mlib.KXRW_TEMP_LAUNCH))
        self.assertEqual(i(mlib.KXRW_TEMP_LAUNCH) + 1, i(mlib.KXRW_TEMP_READY))
        self.assertEqual(i(mlib.KXRW_TEMP_READY) + 1, i(mlib.KXRW_REWIND))
        st = self._at_temp_launch()
        st, acts = mlib.kxrw_decide(st, snap(ut=1200.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_TEMP_READY, st.phase)
        self.assertEqual([mlib.ACTION_LAUNCH_VESSEL], kinds(acts))
        self.assertEqual(CRAFT, acts[0].text)
        self.assertTrue(st.temp_launch_commanded)

    def test_the_throwaway_is_the_recorded_craft_and_not_the_watcher(self):
        """THE CREW CONSTRAINT, as the two facts a cell can hold. The crash leaves
        every kerbal `state = Missing`, so `DefaultCrewForVessel` builds an EMPTY
        manifest and only a `minimumCrew = 0` command module is still a control
        source - the recorded Kerbal X's RC-L01, not the Jumping Flea's Mk1 pod.
        The watcher craft would hang kRPC's LaunchVessel inside
        `WaitForVesselPreFlightChecks` on `PreFlightTests.NoControlSources`
        (measured, GS-7 round 3: 2026-09-08_1429 / _1503_a2).

        MUTATION: launch `watcher_craft_name` here (the shape before this fix) and
        the first assert reds; settle TEMP-READY on the WATCHER's name and the
        second does."""
        st = self._at_temp_launch()
        st, acts = mlib.kxrw_decide(st, snap(ut=1200.0, vessel_lost=True))
        self.assertEqual(CRAFT, acts[0].text)
        self.assertNotEqual(WATCHER, acts[0].text)
        # And the settle gate reads the RECORDED craft back. A frame carrying the
        # WATCHER's name in an otherwise acceptable situation settles NOTHING: it
        # is a name this launch cannot produce, so accepting it would let a launch
        # that never arrived be closed by a stale read.
        for ut in (1201.0, 1202.0, 1203.0):
            st, acts = mlib.kxrw_decide(st, snap(ut=ut, situation="PRE_LAUNCH",
                                                 vessel_name=WATCHER))
            self.assertEqual(mlib.KXRW_TEMP_READY, st.phase)
            self.assertEqual(0, st.temp_ready_streak)
            self.assertEqual([], acts)
        self.assertFalse(st.temp_ready_observed)
        self.assertNotIn(mlib.KXRW_REWIND, st.phases_reached)

    def test_the_settled_throwaway_craft_stamps_the_clock_and_commands_the_rewind(self):
        """The settle gate is the launch gate every other launch here uses, and the
        stamp is taken on the SAME frame the rewind goes out, so the SPACECENTER
        gate compares against the tightest possible `before`. The rewind carries
        the tree id captured before the crash, which is the only id anything in
        this run knows."""
        st = self._at_temp_ready(rolloutReadyDebounceFrames=2)
        # The scene load: a lost frame settles nothing, and it is EXPECTED here.
        st, acts = mlib.kxrw_decide(st, snap(ut=1201.0, vessel_lost=True))
        self.assertEqual(0, st.temp_ready_streak)
        self.assertEqual([], acts)
        st, acts = mlib.kxrw_decide(st, snap(ut=1202.0, situation="PRE_LAUNCH",
                                             vessel_name=CRAFT))
        self.assertEqual(mlib.KXRW_TEMP_READY, st.phase)
        self.assertEqual([], acts)
        st, acts = mlib.kxrw_decide(st, snap(ut=1203.0, situation="PRE_LAUNCH",
                                             vessel_name=CRAFT))
        self.assertEqual(mlib.KXRW_REWIND, st.phase)
        self.assertTrue(st.temp_ready_observed)
        self.assertEqual(CRAFT, st.temp_ready_vessel_name)
        self.assertEqual(1203.0, st.pre_rewind_ut)
        self.assertEqual(1, len(acts))
        self.assertEqual("InvokeRewindToLaunch", acts[0].seam_verb)
        self.assertEqual((("tree", "t_kx"),), acts[0].seam_args)
        self.assertEqual(mlib.KXRW_TAG_REWIND, acts[0].seam_tag)

    def test_an_unreadable_clock_refuses_to_command_the_rewind(self):
        """RECORDER-IDLE's refusal, on the profile's own path to the same verb:
        `pre_rewind_ut` is the SPACECENTER gate's ONLY `before`, so a non-finite
        stamp makes the OBSERVED backward clock permanently unprovable - after an
        irreversible world load has already been commanded. MUTATION: stamp
        `snapshot.ut` unguarded and the frame below commands the rewind against
        NaN."""
        st = self._at_temp_ready(rolloutReadyDebounceFrames=1)
        st, acts = mlib.kxrw_decide(st, snap(ut=float("nan"),
                                             situation="PRE_LAUNCH",
                                             vessel_name=CRAFT))
        self.assertEqual(mlib.KXRW_TEMP_READY, st.phase)
        self.assertEqual([], acts)
        self.assertFalse(st.temp_ready_observed)
        self.assertTrue(math.isnan(st.pre_rewind_ut))
        # A readable clock releases it, and stamps THAT frame.
        st, acts = mlib.kxrw_decide(st, snap(ut=1250.0, situation="PRE_LAUNCH",
                                             vessel_name=CRAFT))
        self.assertEqual(mlib.KXRW_REWIND, st.phase)
        self.assertEqual(1250.0, st.pre_rewind_ut)
        self.assertEqual("InvokeRewindToLaunch", acts[0].seam_verb)

    def test_a_clock_that_never_reads_burns_the_bound_and_names_the_stamp(self):
        st = self._at_temp_ready(rolloutReadyDebounceFrames=1,
                                 watcherLaunchFrames=4)
        for _ in range(10):
            st, acts = mlib.kxrw_decide(st, snap(ut=float("nan"),
                                                 situation="PRE_LAUNCH",
                                                 vessel_name=CRAFT))
            self.assertEqual([], acts)
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_TEMP_READY, st.flake_phase)
        self.assertIn("`ut` was unreadable", st.flake_reason)
        self.assertIn("refusing to command an irreversible rewind",
                      st.flake_reason)
        self.assertNotIn(mlib.KXRW_REWIND, st.phases_reached)

    def test_a_throwaway_craft_that_never_settles_names_what_it_was_for(self):
        """And it names BOTH pre-flight checks that produce this shape in the
        field, each of which raises a dialog kRPC's LaunchVessel then yields on
        forever: an OBSTRUCTED PAD (LaunchSiteClear) and NO CONTROL SOURCE
        (NoControlSources, the all-Missing roster). It names the craft it asked
        for, too - the RECORDED one, which is what the second check is why."""
        st = self._at_temp_ready(watcherLaunchFrames=4)
        for _ in range(10):
            st, _ = mlib.kxrw_decide(st, snap(ut=1201.0, vessel_lost=True))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_TEMP_READY, st.flake_phase)
        self.assertIn("never became the active vessel", st.flake_reason)
        self.assertIn("RequiresFlight", st.flake_reason)
        self.assertIn("LaunchSiteClear", st.flake_reason)
        self.assertIn("NoControlSources", st.flake_reason)
        self.assertIn("Missing", st.flake_reason)
        self.assertIn(repr(CRAFT), st.flake_reason)
        self.assertNotIn(mlib.KXRW_REWIND, st.phases_reached)

    # ---- (k) the assertion rows ---------------------------------------------

    def test_the_profile_substitutes_one_row_and_adds_none(self):
        p = mlib.kxrw_params_from_dict(params(**self.IMPACT))
        st = dataclasses.replace(
            machine(**self.IMPACT), launch_ut=1000.0, impact_observed=True,
            impact_observed_by="frozen-telemetry", impact_ut=1200.0,
            recording_end_ut=1200.0, impact_autorecord_off_result="OK",
            core_discard_commanded=True, core_cut_throttle_observed=True,
            tree_id="t_kx", sc_exit_result="OK", committed_handle_count=4,
            sc_commit_probe=1, post_impact_vessel_lost_frames=7,
            temp_ready_vessel_name=CRAFT)
        rows = {r.name: r for r in mlib.evaluate_kxrw_assertions([], p, st)}
        self.assertEqual(8, len(rows))
        self.assertNotIn("treeCommitted", rows)
        # THE CORE ROW IS NOT SUBSTITUTED: this profile runs the core gate the
        # ordinary way, and that discard is what leaves the stack falling.
        self.assertTrue(rows["coreDiscardedWithEnginesOff"].met)

        committed = rows["treeCommittedAtSpaceCenter"]
        self.assertTrue(committed.met)
        self.assertEqual(4, committed.value)
        self.assertEqual("t_kx", committed.detail["tree"])
        self.assertEqual("OK", committed.detail["exitResult"])
        self.assertEqual(4, committed.detail["count"])
        self.assertEqual(2, committed.detail["probes"])
        self.assertNotIn("saveResult", committed.detail)
        self.assertNotIn("reloadResult", committed.detail)
        # WHICH CRAFT bought the rewind's FLIGHT scene, as EVIDENCE only: the
        # throwaway is the RECORDED craft (the post-crash roster is all-Missing,
        # so only a minimumCrew=0 command module still passes NoControlSources),
        # and the row's met test does not read either field.
        self.assertEqual(CRAFT, committed.detail["throwawayCraft"])
        self.assertEqual(CRAFT, committed.detail["throwawayObservedName"])
        unread = dataclasses.replace(st, temp_ready_vessel_name="")
        self.assertEqual(
            "UNREAD",
            {r.name: r for r in mlib.evaluate_kxrw_assertions([], p, unread)}
            ["treeCommittedAtSpaceCenter"].detail["throwawayObservedName"])

        # THE CRASH RIDES THE ROW THAT DESCRIBES THE FLIGHT, not a ninth row.
        span = rows["recordedSpanSeconds"]
        self.assertTrue(span.met)
        self.assertEqual(200.0, span.value)
        self.assertTrue(span.detail["impactObserved"])
        self.assertEqual("frozen-telemetry", span.detail["impactObservedBy"])
        self.assertEqual(1200.0, span.detail["impactUT"])
        self.assertEqual(600, span.detail["impactCoastFrames"])
        self.assertEqual("OK", span.detail["autoRecordDisarmedBeforeImpact"])
        self.assertEqual(7, span.detail["postImpactVesselLostFrames"])

    def test_the_span_row_fails_when_no_impact_was_observed(self):
        """`recording_end_ut` IS the impact stamp on this profile, so an
        unobserved impact leaves the row with no span to measure. MUTATION: leave
        the row on the window alone and a stack that SURVIVED, whose end UT was
        stamped by nothing, reports a healthy span."""
        p = mlib.kxrw_params_from_dict(params(**self.IMPACT))
        # A span inside the window, stamped by an END that no impact produced.
        no_impact = dataclasses.replace(machine(**self.IMPACT), launch_ut=1000.0,
                                        recording_end_ut=1200.0)
        rows = {r.name: r for r in mlib.evaluate_kxrw_assertions([], p, no_impact)}
        self.assertFalse(rows["recordedSpanSeconds"].met)
        self.assertEqual(200.0, rows["recordedSpanSeconds"].value)
        self.assertFalse(rows["recordedSpanSeconds"].detail["impactObserved"])
        self.assertEqual("none",
                         rows["recordedSpanSeconds"].detail["impactObservedBy"])
        self.assertEqual(
            "NONE",
            rows["recordedSpanSeconds"].detail["autoRecordDisarmedBeforeImpact"])
        # The SAME state with the key OFF meets the row: the extra conjunct is the
        # profile's alone.
        off = mlib.kxrw_params_from_dict(params())
        rows = {r.name: r for r in mlib.evaluate_kxrw_assertions([], off,
                                                                 no_impact)}
        self.assertTrue(rows["recordedSpanSeconds"].met)

    def test_the_committed_row_reads_its_own_evidence_and_nothing_else(self):
        """MUTATION: meet `treeCommittedAtSpaceCenter` off the exit result alone
        and a run whose tree never committed passes."""
        p = mlib.kxrw_params_from_dict(params(**self.IMPACT))
        exit_only = dataclasses.replace(machine(**self.IMPACT), tree_id="t_kx",
                                        sc_exit_result="OK",
                                        committed_handle_count=0)
        rows = {r.name: r for r in mlib.evaluate_kxrw_assertions([], p, exit_only)}
        self.assertFalse(rows["treeCommittedAtSpaceCenter"].met)
        self.assertEqual(0, rows["treeCommittedAtSpaceCenter"].value)

        never_read = dataclasses.replace(machine(**self.IMPACT), tree_id="t_kx",
                                         sc_exit_result="")
        rows = {r.name: r for r in mlib.evaluate_kxrw_assertions([], p, never_read)}
        self.assertFalse(rows["treeCommittedAtSpaceCenter"].met)
        self.assertIsNone(rows["treeCommittedAtSpaceCenter"].value)
        self.assertEqual("NONE",
                         rows["treeCommittedAtSpaceCenter"].detail["exitResult"])
        self.assertIsNone(rows["treeCommittedAtSpaceCenter"].detail["count"])
        self.assertEqual(0, rows["treeCommittedAtSpaceCenter"].detail["probes"])

        # The exit conjunct on its own: a count that reads fine over an exit
        # that never answered OK is NOT a commit proof. MUTATION: drop
        # `exit_result == "OK"` from the row and this fixture passes it.
        count_without_exit = dataclasses.replace(
            machine(**self.IMPACT), tree_id="t_kx",
            sc_exit_result="TIMEOUT", committed_handle_count=3)
        rows = {r.name: r for r in
                mlib.evaluate_kxrw_assertions([], p, count_without_exit)}
        self.assertFalse(rows["treeCommittedAtSpaceCenter"].met)
        self.assertEqual(3, rows["treeCommittedAtSpaceCenter"].value)
        self.assertEqual("TIMEOUT",
                         rows["treeCommittedAtSpaceCenter"].detail["exitResult"])

    def test_the_non_impact_rows_are_untouched_by_the_profile(self):
        """THE COMPATIBILITY STATEMENT FOR THE ROWS: with the key omitted the eight
        names and their evidence are byte-identical to what they always were, the
        span row's detail keys included."""
        p = mlib.kxrw_params_from_dict(params())
        st = machine()
        rows = mlib.evaluate_kxrw_assertions([], p, st)
        self.assertEqual(
            ["coreDiscardedWithEnginesOff", "boosterStagesDropped",
             "recordedSpanSeconds", "treeCommitted", "rewoundToLaunch",
             "watcherOnPad", "renderVerbsDriven", "playbackWatchedOut"],
            [r.name for r in rows])
        span_off = [r for r in rows if r.name == "recordedSpanSeconds"][0]
        for key in ("impactObserved", "impactObservedBy", "impactUT",
                    "impactCoastFrames", "autoRecordDisarmedBeforeImpact",
                    "postImpactVesselLostFrames"):
            self.assertNotIn(key, span_off.detail)
        # ...and with it on, the same eight POSITIONS with ONE substituted name.
        pi = mlib.kxrw_params_from_dict(params(**self.IMPACT))
        names = [r.name for r in mlib.evaluate_kxrw_assertions(
            [], pi, machine(**self.IMPACT))]
        self.assertEqual(
            ["coreDiscardedWithEnginesOff", "boosterStagesDropped",
             "recordedSpanSeconds", "treeCommittedAtSpaceCenter",
             "rewoundToLaunch", "watcherOnPad", "renderVerbsDriven",
             "playbackWatchedOut"], names)

    # ---- (l) the conflict gate ----------------------------------------------

    def test_the_profile_and_the_part_sweep_are_refused_on_the_first_frame(self):
        """FAIL CLOSED BEFORE THE ASCENT, which is the GS-6 vocabulary lesson: a
        gate that fires after the flight burns a whole run and then flakes, which
        the retry policy flies again. The sweep fires part actions on the very
        stack this profile needs to fall unattended, and the vocabulary carries
        steps that keep it alive - a survived stack reads to IMPACT-COAST exactly
        as a stalled clock does. MUTATION: drop the gate and a `chutes-deploy`
        turns a spec choice into a give-up about the product."""
        st = machine(impactProfile=True, partSweepSteps=["gear-down"])
        st, acts = mlib.kxrw_decide(st, snap(ut=0.0, situation="PRE_LAUNCH"))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_ROLLOUT, st.flake_phase)
        self.assertIn("mutually exclusive", st.flake_reason)
        self.assertIn("gear-down", st.flake_reason)
        # NOTHING was commanded: not even the rollout's own launch click.
        self.assertEqual([], acts)
        self.assertFalse(st.rollout_launch_commanded)

    def test_the_two_retired_clauses_no_longer_refuse_anything(self):
        """THE MIRROR DIRECTION of the rework. `boosterStageCount` below 1 used to
        be refused because the profile handed off from the LAST BOOSTER DROP; it
        does not any more, so a zero-drop craft simply runs the ordinary core gate.
        And there is no save folder to resolve because there is no reload. MUTATION:
        keep either clause and a perfectly flyable spec dies on frame 1."""
        conflict = mlib.kxrw_impact_profile_conflict
        self.assertEqual("", conflict(mlib.kxrw_params_from_dict(
            params(impactProfile=True, boosterStageCount=0))))
        self.assertEqual("", conflict(mlib.kxrw_params_from_dict(
            params(**self.IMPACT))))
        # ...and a zero-drop machine actually leaves ROLLOUT and flies.
        st = machine(impactProfile=True, boosterStageCount=0)
        st, acts = mlib.kxrw_decide(st, snap(ut=0.0, situation="PRE_LAUNCH"))
        self.assertFalse(st.done)
        self.assertEqual([mlib.ACTION_LAUNCH_VESSEL], kinds(acts))
        # The predicate is INERT on every lane that does not declare the profile.
        self.assertEqual("", conflict(mlib.kxrw_params_from_dict(params())))
        self.assertEqual("", conflict(mlib.kxrw_params_from_dict(
            params(partSweepSteps=["gear-down"]))))
        self.assertIn("mutually exclusive", conflict(mlib.kxrw_params_from_dict(
            params(impactProfile=True, partSweepSteps=["gear-down"]))))

    # ---- (m) the phase bookkeeping ------------------------------------------

    def test_the_impact_phases_sit_where_the_post_rewind_block_stays_contiguous(self):
        """The seven profile phases go BETWEEN the ordinary bridge and REWIND, so
        KXRW_POST_REWIND_PHASES is still the contiguous TAIL of KXRW_PHASES - the
        property the carve-out's own cell asserts. MUTATION: append them after
        DONE (or interleave them past REWIND) and that slice stops matching."""
        self.assertEqual(31, len(mlib.KXRW_PHASES))
        self.assertEqual(len(set(mlib.KXRW_PHASES)), len(mlib.KXRW_PHASES))
        i = mlib.KXRW_PHASES.index
        self.assertEqual(
            (mlib.KXRW_IMPACT_AUTORECORD_OFF, mlib.KXRW_IMPACT_COAST,
             mlib.KXRW_IMPACT_SETTLE, mlib.KXRW_SC_EXIT, mlib.KXRW_SC_COMMITTED,
             mlib.KXRW_TEMP_LAUNCH, mlib.KXRW_TEMP_READY),
            mlib.KXRW_PHASES[i(mlib.KXRW_RECORDER_IDLE) + 1:i(mlib.KXRW_REWIND)])
        self.assertEqual(
            mlib.KXRW_PHASES[i(mlib.KXRW_REWIND):i(mlib.KXRW_DONE)],
            mlib.KXRW_POST_REWIND_PHASES)
        # Distinct wire ids: the C# seam SKIPS DUPLICATE IDS, so a collision here
        # would make the second command a silent no-op whose poll then expires as a
        # TIMEOUT that reads like a wedged addon. The two SetSetting tags are the
        # pair that matters most - they carry identical args.
        tags = {mlib.KXRW_TAG_COMMIT, mlib.KXRW_TAG_STOP, mlib.KXRW_TAG_REWIND,
                mlib.KXRW_TAG_AUTORECORD, mlib.KXRW_TAG_IMPACT_AUTORECORD,
                mlib.KXRW_TAG_MAP, mlib.KXRW_TAG_MAP_EXIT, mlib.KXRW_TAG_SC_EXIT,
                mlib.kxrw_sc_commit_probe_tag(0), mlib.kxrw_tree_probe_tag(0),
                mlib.kxrw_idle_probe_tag(0), mlib.kxrw_watch_probe_tag(0)}
        self.assertEqual(12, len(tags))
        self.assertEqual("sccommit3", mlib.kxrw_sc_commit_probe_tag(3))

    def test_the_new_knobs_are_declared_and_default_sanely(self):
        p = mlib.kxrw_params_from_dict(params())
        self.assertFalse(p.impact_profile)
        self.assertEqual(600, p.impact_coast_frames)
        self.assertEqual(16, p.impact_settle_frames)
        self.assertEqual(240, p.sc_exit_frames)
        self.assertEqual(40, p.sc_committed_frames)
        # IMPACT-AUTORECORD-OFF declares no knob of its own; it reuses the
        # post-rewind disarm's. MUTATION: add a second key and the two numbers
        # start drifting apart for one identical verb.
        self.assertEqual(40, p.auto_record_off_frames)
        # The settle has to outlast the C# finalize's 5 s coalescer wait, at the
        # standard 0.5 s poll. MUTATION: drop the default to 4 frames (2 s) and the
        # exit races the very stash the whole Space Center hop depends on.
        self.assertGreater(p.impact_settle_frames * 0.5, 5.0)
        # A declared bool arrives as a bool, and a missing one defaults false.
        self.assertTrue(
            mlib.kxrw_params_from_dict(params(impactProfile=True)).impact_profile)
        # The three keys the rework retired are gone from the params object, so a
        # spec still declaring them is caught by the schema-sync cell rather than
        # landing as a silent default.
        for gone in ("sc_save_frames", "reload_frames", "reload_save_name"):
            self.assertFalse(hasattr(p, gone), gone)

    def test_the_profile_walks_from_the_tree_probe_to_the_rewind_end_to_end(self):
        """The chain joins up. A per-phase cell can pass while two phases are wired
        to the wrong successor, and this is the cell that would catch it.

        THE VERB SEQUENCE IS THE POINT: the disarm rides the tree read, the exit
        follows the settle, the committed count is READ back, and only then does a
        craft go onto the pad - one launch, between the ListHandles and the
        rewind."""
        st, acts = self._to_tree_state()
        verbs = [a.seam_verb for a in acts if a.seam_verb]
        self.assertEqual(["RecordingState"], verbs)
        script = [
            seam("tree0", "OK", (("tree", "t_kx"),), ut=76.0, situation="FLYING"),
            seam("impautorec", "OK", (), ut=76.5, situation="FLYING"),
        ]
        script += [snap(ut=77.0 + i, altitude=58000.0 - 500.0 * i,
                        situation="FLYING") for i in range(4)]
        script += [snap(ut=200.0, vessel_lost=True)]                 # the impact
        # the settle, whose last frame commands the exit
        script += [snap(ut=200.0, vessel_lost=True) for _ in range(16)]
        script += [seam("scexit", "OK", (), ut=200.0, vessel_lost=True),
                   seam("sccommit0", "OK", (("count", "2"),), ut=200.0,
                        vessel_lost=True),
                   # the launch click, then the scene load and the settle (one
                   # frame short of the debounce, so the rewind rides the frame
                   # driven after the loop)
                   snap(ut=200.0, vessel_lost=True),
                   snap(ut=201.0, vessel_lost=True),
                   snap(ut=201.5, situation="PRE_LAUNCH", vessel_name=CRAFT)]
        launch_at = None
        for s in script:
            st, acts = mlib.kxrw_decide(st, s)
            for a in acts:
                if a.kind == mlib.ACTION_LAUNCH_VESSEL:
                    # The RECORDED craft: the post-crash roster is all-Missing, so
                    # the watcher's crewed pod would hang on NoControlSources.
                    self.assertEqual(CRAFT, a.text)
                    launch_at = len(verbs)
            verbs += [a.seam_verb for a in acts if a.seam_verb]
            self.assertFalse(st.done, st.flake_reason or st.loss_reason)
        self.assertEqual(mlib.KXRW_TEMP_READY, st.phase)
        st, acts = mlib.kxrw_decide(st, snap(ut=202.0, situation="PRE_LAUNCH",
                                             vessel_name=CRAFT))
        verbs += [a.seam_verb for a in acts if a.seam_verb]
        self.assertEqual(mlib.KXRW_REWIND, st.phase)
        # NO CommitTree, NO StopRecording, NO SaveGame and NO LoadGame anywhere on
        # this path: the crash stashed the tree, the Space Center arrival committed
        # it, and the rewind is commanded in that same process.
        self.assertEqual(["RecordingState", "SetSetting", "ExitToSpaceCenter",
                          "ListHandles", "InvokeRewindToLaunch"], verbs)
        # ONE launch, and it sits between the committed read and the rewind.
        self.assertEqual(4, launch_at)
        # Every profile phase walked, in order, and the ordinary bridge's three
        # skipped entirely.
        self.assertEqual(
            [mlib.KXRW_TREE_STATE, mlib.KXRW_IMPACT_AUTORECORD_OFF,
             mlib.KXRW_IMPACT_COAST, mlib.KXRW_IMPACT_SETTLE, mlib.KXRW_SC_EXIT,
             mlib.KXRW_SC_COMMITTED, mlib.KXRW_TEMP_LAUNCH,
             mlib.KXRW_TEMP_READY, mlib.KXRW_REWIND],
            [ph for ph in st.phases_reached
             if ph in (set(mlib.KXRW_IMPACT_PHASES)
                       | {mlib.KXRW_TREE_STATE, mlib.KXRW_IMPACT_AUTORECORD_OFF,
                          mlib.KXRW_IMPACT_COAST, mlib.KXRW_REWIND})])
        for skipped in (mlib.KXRW_COMMIT, mlib.KXRW_STOP,
                        mlib.KXRW_RECORDER_IDLE, mlib.KXRW_PART_SWEEP):
            self.assertNotIn(skipped, st.phases_reached)
        # The core gate DID run - it is what left the stack falling.
        for walked in (mlib.KXRW_CORE_CUT, mlib.KXRW_CORE_DISCARD,
                       mlib.KXRW_COAST):
            self.assertIn(walked, st.phases_reached)
        # The rewind carries the tree captured BEFORE the crash, and the playback
        # wait's stamp is the impact instant.
        self.assertEqual(200.0, st.recording_end_ut)
        self.assertEqual(202.0, st.pre_rewind_ut)


class CoastExitProfileTests(unittest.TestCase):
    """RF-9's opt-in: end the mission in FLIGHT, with the recorder live, once the
    top stack has been OBSERVED coasting above the atmosphere.

    WHAT THE LANE NEEDS FROM IT, and therefore what these cells guard: the
    committed recording has to span an Atmospheric -> ExoBallistic boundary,
    because that is the only boundary the optimizer's `PersistedPhaseChange` split
    predicate cuts on and the SPLIT is the precondition of the sealing defect. The
    machine cannot see sections, so what it CAN do is refuse to hand over until it
    has read the altitude and the situation that produce one - which is the whole
    difference between this and a longer `coastSeconds`.
    """

    # A scripted flight that reaches COAST: rollout, launch, three drops, the core
    # gate, the discard. The discard is BELOW 70 km, which is where it has to be
    # for the boundary to land INSIDE the surviving stack's recording.
    def _to_coast(self, **over):
        over.setdefault("boosterStageCount", 3)
        over.setdefault("stageSettleFrames", 2)
        over.setdefault("coastSeconds", 5.0)
        over.setdefault("coreDiscardApoapsisMeters", 90000.0)
        over.setdefault("recordedSpanWindowSeconds", {"min": 60.0, "max": 600.0})
        st = rolled_out(machine(**over))
        st, _ = mlib.kxrw_decide(st, snap(ut=1000.0, altitude=0.0, throttle=0.0,
                                          available_thrust=0.0))
        st, _ = fly(st, ut=1001.0, altitude=200.0)
        ut = 1040.0
        for _ in range(40):
            st, _ = fly(st, ut=ut, altitude=9000.0, throttle=0.0,
                        available_thrust=FLAMED)
            ut += 0.5
            if st.phase == mlib.KXRW_ASCENT and st.booster_drops_done >= 3:
                break
        assert st.booster_drops_done == 3, st.booster_drops_done
        st, _ = fly(st, ut=1150.0, altitude=58000.0, apoapsis=91000.0)
        assert st.phase == mlib.KXRW_CORE_CUT, st.phase
        st, _ = fly(st, ut=1151.0, altitude=58500.0, apoapsis=91000.0, throttle=0.0)
        st, _ = fly(st, ut=1152.0, altitude=59000.0, apoapsis=91000.0, throttle=0.0)
        assert st.phase == mlib.KXRW_CORE_DISCARD, st.phase
        st, _ = fly(st, ut=1153.0, altitude=59500.0, apoapsis=91000.0, throttle=0.0)
        assert st.phase == mlib.KXRW_COAST, st.phase
        return st

    def _coast_out(self, st, **kw):
        """One post-coast frame with the readings a coasting top stack carries."""
        kw.setdefault("throttle", 0.0)
        kw.setdefault("available_thrust", 0.0)
        kw.setdefault("apoapsis", 91000.0)
        return mlib.kxrw_decide(st, snap(**kw))

    # ---- (a) the byte-inertness claim, made mechanically --------------------

    def test_the_off_path_is_identical_with_the_key_absent_or_false(self):
        """THE COMPATIBILITY STATEMENT, and it is scoped to what it replays. This
        cell drives the DEFAULT-profile flight (no `partSweepSteps`, no
        `impactProfile`) with `coastExitProfile` ABSENT and with it explicitly
        `false`, and compares what a live run would notice: the phase reached on
        every frame and the ACTION LIST emitted on every frame. That covers GS-4 and
        GS-8, which declare neither other opt-in.

        GS-6 and GS-7 declare one each, and the sibling cell below replays THOSE
        params sets the same way rather than leaving them to inspection - the first
        version of this docstring claimed all four lanes off a replay of one.

        Comparing the terminal alone would not do - two runs can agree at the end
        and disagree about which frame commanded the stage."""
        def replay(**over):
            st = self._to_coast(**over)
            trace = []
            ut = 1160.0
            for _ in range(6):
                st, acts = self._coast_out(st, ut=ut, altitude=72000.0,
                                           situation="SUB_ORBITAL")
                trace.append((st.phase,
                              tuple((a.kind, a.value, a.text, a.seam_verb)
                                    for a in acts)))
                ut += 1.0
                if st.done:
                    break
            return st, tuple(trace)

        absent_state, absent_trace = replay()
        false_state, false_trace = replay(coastExitProfile=False)
        self.assertEqual(absent_trace, false_trace)
        self.assertEqual(absent_state.phase, false_state.phase)
        self.assertEqual(absent_state.verdict, false_state.verdict)
        # ...and the OFF path really does reach the phase the ON path replaces,
        # so the comparison above is between two runs that got somewhere.
        self.assertEqual(mlib.KXRW_TREE_STATE, absent_state.phase)
        self.assertNotIn(mlib.KXRW_COAST_EXIT, absent_state.phases_reached)

    def test_the_other_two_opt_ins_are_untouched_by_the_key(self):
        """THE OTHER HALF OF THE CLAIM, replayed rather than reasoned about. GS-6
        declares `partSweepSteps` and GS-7 `impactProfile`; both reach COAST, which
        is the one phase this feature edits, so both are replayed with the key absent
        and with it false and compared frame by frame. MUTATION: move the
        `coast_exit_profile` read in COAST above the sweep decision without the
        conflict gate and the sweep lane's trace diverges here."""
        for over in ({"partSweepSteps": ["gear-down"]}, {"impactProfile": True}):
            def replay(**extra):
                st = self._to_coast(**dict(over, **extra))
                trace = []
                ut = 1160.0
                for _ in range(6):
                    st, acts = self._coast_out(st, ut=ut, altitude=72000.0,
                                               situation="SUB_ORBITAL")
                    trace.append((st.phase,
                                  tuple((a.kind, a.value, a.text, a.seam_verb)
                                        for a in acts)))
                    ut += 1.0
                    if st.done:
                        break
                return st, tuple(trace)

            absent_state, absent_trace = replay()
            false_state, false_trace = replay(coastExitProfile=False)
            self.assertEqual(absent_trace, false_trace, over)
            self.assertEqual(absent_state.phase, false_state.phase, over)
            self.assertEqual(absent_state.verdict, false_state.verdict, over)
            self.assertNotIn(mlib.KXRW_COAST_EXIT, absent_state.phases_reached, over)

    def test_an_empty_situation_list_is_refused_by_name(self):
        """`[]` is a spec author saying "accept nothing", and the gate could then
        never open - the flight would run to its frame cap and flake AFTER an ascent.
        The params reader takes the key's own value rather than treating `[]` as
        absent precisely so this refusal lands on frame 1. MUTATION: restore the
        `or (...)` default and this cell reds because the machine flies happily with
        the default gate the spec did not ask for."""
        p = mlib.kxrw_params_from_dict(params(coastExitProfile=True,
                                              coastExitSituations=[]))
        self.assertEqual((), p.coast_exit_situations)
        self.assertIn("EMPTY coastExitSituations",
                      mlib.kxrw_coast_exit_profile_conflict(p))
        st = machine(coastExitProfile=True, coastExitSituations=[])
        st, acts = mlib.kxrw_decide(st, snap(ut=0.0, situation="PRE_LAUNCH"))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual([], acts)

    def test_the_predicate_is_inert_on_every_lane_that_does_not_declare_it(self):
        conflict = mlib.kxrw_coast_exit_profile_conflict
        self.assertEqual("", conflict(mlib.kxrw_params_from_dict(params())))
        self.assertEqual("", conflict(mlib.kxrw_params_from_dict(
            params(impactProfile=True))))
        self.assertEqual("", conflict(mlib.kxrw_params_from_dict(
            params(partSweepSteps=["gear-down"]))))
        self.assertEqual("", conflict(mlib.kxrw_params_from_dict(
            params(coastExitProfile=True))))

    # ---- (b) the gate itself -------------------------------------------------

    def test_the_gate_wants_both_halves_and_an_actual_reading(self):
        """MUTATION: drop either conjunct and a stack still inside the atmosphere,
        or one already in orbit, hands the scene over - and the recording the spec
        commits then carries one environment (no split) or a closed orbit (no
        predicted impact, so no `reason=crashed` promotion)."""
        met = mlib.kxrw_coast_exit_gate_met
        self.assertTrue(met(72000.0, "SUB_ORBITAL", 71000.0, ("SUB_ORBITAL",)))
        self.assertFalse(met(69000.0, "SUB_ORBITAL", 71000.0, ("SUB_ORBITAL",)))
        self.assertFalse(met(72000.0, "ORBITING", 71000.0, ("SUB_ORBITAL",)))
        self.assertFalse(met(72000.0, "", 71000.0, ("SUB_ORBITAL",)))
        # An UNREAD altitude is not a high one (kxrw_throttle_is_zero's argument).
        self.assertFalse(met(float("nan"), "SUB_ORBITAL", 71000.0,
                             ("SUB_ORBITAL",)))
        # Exactly AT the threshold counts: the bound is inclusive on purpose.
        self.assertTrue(met(71000.0, "SUB_ORBITAL", 71000.0, ("SUB_ORBITAL",)))

    def test_one_in_gate_frame_does_not_settle_it(self):
        st = self._to_coast(coastExitProfile=True)
        st, _ = self._coast_out(st, ut=1160.0, altitude=60000.0,
                                situation="SUB_ORBITAL")
        self.assertEqual(mlib.KXRW_COAST_EXIT, st.phase)
        st, _ = self._coast_out(st, ut=1170.0, altitude=72000.0,
                                situation="SUB_ORBITAL")
        self.assertEqual(mlib.KXRW_COAST_EXIT, st.phase,
                         "the debounce is 2 - one read must not end the flight")
        self.assertFalse(st.coast_exit_observed)
        st, acts = self._coast_out(st, ut=1171.0, altitude=73000.0,
                                   situation="SUB_ORBITAL")
        self.assertEqual(mlib.KXRW_DONE, st.phase)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        # NOTHING is commanded on the way out: the scenario's own
        # ExitToSpaceCenter step is what commits the tree.
        self.assertEqual([], acts)
        self.assertTrue(st.coast_exit_observed)
        self.assertEqual(73000.0, st.coast_exit_altitude)
        self.assertEqual("SUB_ORBITAL", st.coast_exit_situation)
        self.assertEqual(1171.0, st.coast_exit_ut)
        # The span's end is stamped HERE because there is no CommitTree to stamp it.
        self.assertEqual(1171.0, st.recording_end_ut)

    def test_a_broken_streak_starts_over(self):
        st = self._to_coast(coastExitProfile=True)
        st, _ = self._coast_out(st, ut=1160.0, altitude=72000.0,
                                situation="SUB_ORBITAL")
        st, _ = self._coast_out(st, ut=1161.0, altitude=69000.0,
                                situation="SUB_ORBITAL")
        self.assertEqual(0, st.coast_exit_streak)
        st, _ = self._coast_out(st, ut=1162.0, altitude=72000.0,
                                situation="SUB_ORBITAL")
        self.assertEqual(mlib.KXRW_COAST_EXIT, st.phase)

    def test_a_stack_that_never_climbs_out_flakes_by_name(self):
        """A MISSION give-up, never a Parsek finding: an ascent that under-performed
        or a core discarded too low is a driver fact. MUTATION: let it fall through
        to DONE and the run commits a single-environment recording while every
        downstream token reads as a product defect."""
        st = self._to_coast(coastExitProfile=True, coastExitFrames=4)
        ut = 1160.0
        for _ in range(8):
            st, _ = self._coast_out(st, ut=ut, altitude=60000.0,
                                    situation="SUB_ORBITAL")
            ut += 1.0
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_COAST_EXIT, st.flake_phase)
        self.assertIn("never read >= 71000 m", st.flake_reason)
        self.assertIn("SUB_ORBITAL", st.flake_reason)
        # The discard's own altitude rides the give-up: the two numbers together
        # are what say whether the boundary was reachable at all.
        self.assertIn("59500", st.flake_reason)

    # ---- (c) the rows --------------------------------------------------------

    def test_four_rows_not_eight_and_the_fourth_is_the_hand_over(self):
        """A row over a verb nobody issued is not a weaker assertion, it is a false
        one. This profile drives no CommitTree, no rewind, no watcher and no
        playback, so those four rows are GONE rather than failing on every green
        flight. MUTATION: keep them and the lane can never pass."""
        st = self._to_coast(coastExitProfile=True)
        st, _ = self._coast_out(st, ut=1160.0, altitude=64000.0,
                                situation="SUB_ORBITAL")
        st, _ = self._coast_out(st, ut=1170.0, altitude=72000.0,
                                situation="SUB_ORBITAL")
        st, _ = self._coast_out(st, ut=1171.0, altitude=73000.0,
                                situation="SUB_ORBITAL")
        self.assertEqual(mlib.KXRW_DONE, st.phase)
        rows = mlib.evaluate_kxrw_assertions(
            [], mlib.kxrw_params_from_dict(
                params(coastExitProfile=True,
                       recordedSpanWindowSeconds={"min": 60.0, "max": 600.0})), st)
        self.assertEqual(["coreDiscardedWithEnginesOff", "boosterStagesDropped",
                          "recordedSpanSeconds", "handedOverAboveAtmosphere"],
                         [r.name for r in rows])
        self.assertEqual([], [r.name for r in rows if not r.met],
                         [r.to_dict() for r in rows])
        hand = [r for r in rows if r.name == "handedOverAboveAtmosphere"][0]
        self.assertEqual(73000.0, hand.value)
        self.assertEqual("SUB_ORBITAL", hand.detail["observedSituation"])
        self.assertEqual(59500.0, hand.detail["coreDiscardAltitude"])
        span = [r for r in rows if r.name == "recordedSpanSeconds"][0]
        self.assertTrue(span.detail["coastExitObserved"])
        self.assertEqual(171.0, span.value)               # 1171 - 1000

    def test_an_unreached_gate_fails_the_span_row_rather_than_reporting_a_nan(self):
        """The impact profile's discipline: say WHICH half failed rather than
        leaving a NaN to be interpreted."""
        st = self._to_coast(coastExitProfile=True)
        rows = mlib.evaluate_kxrw_assertions(
            [], mlib.kxrw_params_from_dict(
                params(coastExitProfile=True,
                       recordedSpanWindowSeconds={"min": 60.0, "max": 600.0})), st)
        span = [r for r in rows if r.name == "recordedSpanSeconds"][0]
        self.assertFalse(span.met)
        self.assertFalse(span.detail["coastExitObserved"])
        hand = [r for r in rows if r.name == "handedOverAboveAtmosphere"][0]
        self.assertFalse(hand.met)

    # ---- (d) the conflict gate ----------------------------------------------

    def test_the_two_profiles_are_refused_on_the_first_frame(self):
        st = machine(coastExitProfile=True, impactProfile=True)
        st, acts = mlib.kxrw_decide(st, snap(ut=0.0, situation="PRE_LAUNCH"))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_ROLLOUT, st.flake_phase)
        self.assertIn("mutually exclusive", st.flake_reason)
        self.assertEqual([], acts)
        self.assertFalse(st.rollout_launch_commanded)

    def test_the_profile_and_the_part_sweep_are_refused_on_the_first_frame(self):
        st = machine(coastExitProfile=True, partSweepSteps=["gear-down"])
        st, acts = mlib.kxrw_decide(st, snap(ut=0.0, situation="PRE_LAUNCH"))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("mutually exclusive", st.flake_reason)
        self.assertIn("gear-down", st.flake_reason)
        self.assertEqual([], acts)

    # ---- (e) the phase bookkeeping ------------------------------------------

    def test_the_new_phase_sits_where_the_post_rewind_block_stays_contiguous(self):
        """COAST-EXIT goes between COAST and PART-SWEEP, so KXRW_POST_REWIND_PHASES
        is still the contiguous TAIL of KXRW_PHASES - the property the carve-out's
        own cell asserts. MUTATION: append it after DONE and that slice stops
        matching."""
        self.assertEqual(31, len(mlib.KXRW_PHASES))
        i = mlib.KXRW_PHASES.index
        self.assertEqual(i(mlib.KXRW_COAST) + 1, i(mlib.KXRW_COAST_EXIT))
        self.assertEqual(i(mlib.KXRW_COAST_EXIT) + 1, i(mlib.KXRW_PART_SWEEP))
        self.assertEqual(mlib.KXRW_PHASES[i(mlib.KXRW_REWIND):i(mlib.KXRW_DONE)],
                         mlib.KXRW_POST_REWIND_PHASES)
        # A FLIGHT phase: the stack is alive and coasting, so a lost vessel there is
        # a craft destroyed and `flightMaxSeconds` still bounds it.
        self.assertIn(mlib.KXRW_COAST_EXIT, mlib.KXRW_FLIGHT_PHASES)
        self.assertNotIn(mlib.KXRW_COAST_EXIT, mlib.KXRW_POST_REWIND_PHASES)
        self.assertNotIn(mlib.KXRW_COAST_EXIT, mlib.KXRW_IMPACT_PHASES)

    def test_a_lost_vessel_in_the_new_phase_is_still_lethal(self):
        """The stack is ALIVE here - this is not IMPACT-COAST, where a dead handle
        is the signal. MUTATION: carve COAST-EXIT into the expected-loss set and a
        craft destroyed on the way up reads as a successful hand-over."""
        st = self._to_coast(coastExitProfile=True)
        st, _ = self._coast_out(st, ut=1160.0, altitude=65000.0,
                                situation="SUB_ORBITAL")
        self.assertEqual(mlib.KXRW_COAST_EXIT, st.phase)
        st, _ = mlib.kxrw_decide(st, snap(ut=1161.0, vessel_lost=True))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)


class CraftAndSchemaSyncTests(unittest.TestCase):
    """The staging plan is a FACT about the committed craft file, and the params
    are a fact about the machine's own reads. Both are derived MECHANICALLY here -
    a hand-copied list is the thing that goes stale."""

    @staticmethod
    def _craft_stages():
        """{istg -> [part names]} parsed off the .craft. `istg` is the part's
        INVERSE stage; KSP fires them highest-first."""
        stages = {}
        cur_name = None
        with open(CRAFT_PATH, encoding="utf-8", errors="replace") as fh:
            for raw in fh:
                line = raw.strip()
                if line.startswith("part = "):
                    cur_name = line[len("part = "):]
                elif line.startswith("istg = ") and cur_name is not None:
                    stages.setdefault(int(line[len("istg = "):]), []).append(cur_name)
                    cur_name = None
        return stages

    def test_the_launch_stage_lights_the_engines_and_releases_the_clamps(self):
        """PRELAUNCH fires exactly ONE stage, so everything the launch needs has to
        share the top istg. MUTATION: re-author the craft with the clamps on their
        own stage and the rocket never leaves the pad."""
        stages = self._craft_stages()
        top = max(stages)
        names = stages[top]
        self.assertTrue(any(n.startswith("liquidEngineMainsail") for n in names),
                        "top stage %d carries no Mainsail: %s" % (top, sorted(set(names))))
        self.assertEqual(6, sum(1 for n in names if n.startswith("liquidEngine2_")),
                         "expected six radial LV-T45s on the launch stage")
        self.assertEqual(3, sum(1 for n in names if n.startswith("launchClamp1")),
                         "expected three launch clamps on the launch stage")

    def test_the_default_booster_stage_count_matches_the_craft(self):
        """The machine drops one pair per declared stage. MUTATION: change the
        default to 2 (or re-author the craft's decoupler stages) and this reds
        instead of a live flight discovering a booster pair still attached."""
        stages = self._craft_stages()
        radial = sorted(istg for istg, names in stages.items()
                        if any(n.startswith("radialDecoupler") for n in names))
        self.assertEqual(3, len(radial), "radial-decoupler stages: %s" % radial)
        for istg in radial:
            self.assertEqual(
                2, sum(1 for n in stages[istg] if n.startswith("radialDecoupler")),
                "stage %d should drop a PAIR" % istg)
        self.assertEqual(len(radial),
                         mlib.kxrw_params_from_dict({}).booster_stage_count)

    def test_the_core_decoupler_fires_after_every_booster_pair(self):
        """The order the machine assumes: all three radial pairs, THEN the fueled
        core. KSP fires highest-istg-first, so the core decoupler must sit BELOW
        every radial one."""
        stages = self._craft_stages()
        radial = [istg for istg, names in stages.items()
                  if any(n.startswith("radialDecoupler") for n in names)]
        stack = [istg for istg, names in stages.items()
                 if any(n.startswith("Decoupler.2") for n in names)]
        self.assertTrue(stack, "no stack decoupler found")
        core = max(i for i in stack if i < min(radial))
        self.assertLess(core, min(radial))
        # And the Poodle ignition + pod separation sit BELOW the core drop: the
        # mission never presses them, and the top stack coasts unpowered.
        poodle = [istg for istg, names in stages.items()
                  if any(n.startswith("liquidEngine2-2") for n in names)]
        self.assertTrue(poodle and max(poodle) < core,
                        "the Poodle must ignite BELOW the core drop; got %s vs %d"
                        % (poodle, core))

    @staticmethod
    def _params_keys_read_by_the_machine():
        """Every spec key `kxrw_params_from_dict` reads, derived by AST.

        AST, NOT regex over the source text: this repo has been bitten three times
        by a source-derived guard reading a COMMENT as code (and by one reading a
        key spelling out of a rationale block). Walking `params.get("<key>", ...)`
        calls sees only real reads."""
        with open(MLIB_PATH, encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        fn = None
        for node in ast.walk(tree):
            if isinstance(node, ast.FunctionDef) and node.name == "kxrw_params_from_dict":
                fn = node
        assert fn is not None, "kxrw_params_from_dict not found"
        keys = set()
        for node in ast.walk(fn):
            if (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "get" and node.args
                    and isinstance(node.args[0], ast.Constant)
                    and isinstance(node.args[0].value, str)):
                keys.add(node.args[0].value)
        return keys

    def test_every_key_the_machine_reads_is_declared_in_the_schema(self):
        """An undeclared key is never type-checked and never range-checked, so a
        spec typo lands as a silent default. MUTATION: add a `params.get("newKnob")`
        without a schema block and this reds."""
        with open(SCHEMA_PATH, "rb") as fh:
            schema = tomllib.load(fh)
        declared = set(schema["params"])
        read = self._params_keys_read_by_the_machine()
        # The window's `min`/`max` are read off the sub-table, not off the spec.
        read -= {"min", "max"}
        self.assertEqual(set(), read - declared,
                         "machine reads keys the schema does not declare: %s"
                         % sorted(read - declared))
        self.assertEqual(set(), declared - read,
                         "schema declares keys the machine never reads (they are "
                         "inert): %s" % sorted(declared - read))

    def test_the_shell_wires_the_machine_and_flies_no_warp(self):
        spec = kx_rewind_watch.SPEC
        self.assertEqual("kx_rewind_watch", spec.name)
        self.assertFalse(spec.allow_rails_warp)
        self.assertEqual(0.0, spec.max_physics_warp)
        self.assertEqual(0, spec.settle_frames)
        st = spec.build_state({})
        self.assertIsInstance(st, mlib.KxrwState)
        self.assertEqual(mlib.KXRW_ROLLOUT, st.phase)
        # The decide/evaluate hooks reach the right mlib entry points.
        moved, acts = spec.decide(st, snap(ut=1.0, situation="PRE_LAUNCH"))
        self.assertEqual([mlib.ACTION_LAUNCH_VESSEL], kinds(acts))
        rows = spec.evaluate([], {}, moved)
        self.assertEqual(8, len(rows))

    def test_the_lane_declares_the_science_bench_axis_of_handoff(self):
        """It DOES declare a handoff contract, and the reversal is deliberate.

        This cell used to assert the opposite - "it terminates on exactly the
        outcome it certifies, so it declares no handoff contract". That reasoning
        is the EVA-4 AXIS: a mission that stops mid-flight and whose world outcome
        happens later. On that axis the old assertion was right and still is; this
        mission does terminate on its own outcome.

        `science_bench_recover` then established a SECOND axis for this table: a
        mission that terminates on its own outcome but has NO VIEW of the product
        claim the flight exists to produce. GS-6 made that axis load-bearing here.
        The PART-SWEEP phase is COMMANDED-ONLY by design - every step is issued and
        none is read back, because whether the ghost REPLAYS a recorded part event
        is the SPEC's question and a mission that failed on a part's behaviour would
        discard the evidence the spec exists to read. So a green MISSION-OK says the
        flight flew and the sequence was driven, and says nothing whatever about
        whether any family reached the ghost.

        Declaring it stops the exact misreading the table was created for:
        "MISSION-OK, so the sweep worked"."""
        contract = mlib.MISSION_HANDOFF_CONTRACTS.get("kx_rewind_watch")
        self.assertIsNotNone(contract, "kx_rewind_watch must declare its contract")
        self.assertIn("ghostPartEventReplay", contract["unverifiedByMission"])
        self.assertIn("logContracts", contract["verifiedBy"])


class RepeatRewindTests(unittest.TestCase):
    """GS-9 (ghost-replay Tier B item 8): `rewindCycles` flies rewind -> watcher ->
    watch -> playback more than once off the SAME committed tree.

    Four properties, each a way the opt-in could be silently wrong:
      1. DEFAULT 1 IS BYTE-IDENTICAL: the whole action trace, the phases walked and
         the eight rows match a params set with no key at all.
      2. EVERY PER-CYCLE COMMAND HAS A FRESH WIRE TAG, and a previous cycle's OK can
         never advance the next cycle (the C# seam skips duplicate ids).
      3. THE SECOND REWIND IS GATED ON AN OBSERVED IDLE RECORDER, in-phase, and the
         loop never leaves the contiguous post-rewind block.
      4. THE NINTH ROW carries every cycle's frozen record and is met only when each
         declared cycle rewound, settled its watcher and watched its playback out."""

    TREE = "t_kx"

    def _d(self, st, snapshot):
        st, acts = mlib.kxrw_decide(st, snapshot)
        self.trace.append((st.phase, tuple((a.kind, a.seam_verb, a.seam_tag,
                                            tuple(a.seam_args or ()))
                                           for a in acts)))
        return st, acts

    def _to_first_rewind(self, **over):
        """The happy path's ascent + bridge, ending in REWIND with cycle 0's
        InvokeRewindToLaunch already emitted. Every frame is recorded in
        `self.trace` so two drives can be compared action for action."""
        self.trace = []
        pdict = params(boosterStageCount=3, stageSettleFrames=2, coastSeconds=5.0,
                       coreDiscardApoapsisMeters=60000.0,
                       recordedSpanWindowSeconds={"min": 60.0, "max": 600.0}, **over)
        st = rolled_out(mlib.kxrw_initial_state(mlib.kxrw_params_from_dict(pdict)))
        st, _ = self._d(st, snap(ut=1000.0, altitude=0.0, throttle=0.0,
                                 available_thrust=0.0))
        st, _ = self._d(st, snap(ut=1001.0, altitude=200.0, available_thrust=LIT,
                                 throttle=1.0, situation="FLYING"))
        ut = 1040.0
        for _ in range(40):
            st, _ = self._d(st, snap(ut=ut, altitude=9000.0, throttle=0.0,
                                     available_thrust=FLAMED, situation="FLYING"))
            ut += 0.5
            if st.phase == mlib.KXRW_ASCENT and st.booster_drops_done >= 3:
                break
        for u, alt, thr in ((1150.0, 55000.0, 1.0), (1151.0, 55500.0, 0.0),
                            (1152.0, 56000.0, 0.0), (1153.0, 56500.0, 0.0),
                            (1160.0, 58000.0, 0.0)):
            st, _ = self._d(st, snap(ut=u, altitude=alt, apoapsis=61000.0,
                                     throttle=thr, available_thrust=LIT,
                                     situation="FLYING"))
        self.assertEqual(mlib.KXRW_TREE_STATE, st.phase)
        st, _ = self._d(st, seam("tree0", "OK", (("tree", self.TREE),), ut=1161.0))
        st, _ = self._d(st, seam("commit", "OK", (), ut=1200.0))
        st, _ = self._d(st, seam("stop", "OK", (), ut=1201.0))
        st, acts = self._d(st, seam("idle0", "OK", (("recording", "false"),),
                                    ut=1202.0))
        self.assertEqual(mlib.KXRW_REWIND, st.phase)
        return st, acts, pdict

    def _post_rewind_leg(self, st, cycle):
        """From REWIND (the command already out) to the PLAYBACK-WAIT entry, under
        cycle ``cycle``'s tags. Returns (state, the watch tag that went out)."""
        st, _ = self._d(st, seam(mlib.kxrw_rewind_tag(cycle), "OK", (),
                                 ut=st.pre_rewind_ut, vessel_lost=True))
        self.assertEqual(mlib.KXRW_SPACECENTER, st.phase)
        st, acts = self._d(st, snap(ut=985.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_AUTORECORD_OFF, st.phase)
        self.assertEqual([mlib.kxrw_autorecord_tag(cycle)], [a.seam_tag for a in acts])
        st, _ = self._d(st, seam(mlib.kxrw_autorecord_tag(cycle), "OK", (), ut=985.5,
                                 vessel_lost=True))
        self.assertEqual(mlib.KXRW_WATCHER_LAUNCH, st.phase)
        st, acts = self._d(st, snap(ut=986.0, vessel_lost=True))
        self.assertEqual([mlib.ACTION_LAUNCH_VESSEL], kinds(acts))
        st, _ = self._d(st, snap(ut=990.0, situation="PRE_LAUNCH", vessel_name=WATCHER))
        st, acts = self._d(st, snap(ut=991.0, situation="PRE_LAUNCH",
                                    vessel_name=WATCHER))
        self.assertEqual(mlib.KXRW_MAP_VIEW, st.phase)
        self.assertEqual([mlib.kxrw_map_tag(cycle)], [a.seam_tag for a in acts])
        st, acts = self._d(st, seam(mlib.kxrw_map_tag(cycle), "OK", (), ut=992.0,
                                    situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_MAP_EXIT, st.phase)
        self.assertEqual([mlib.kxrw_map_exit_tag(cycle)], [a.seam_tag for a in acts])
        st, _ = self._d(st, seam(mlib.kxrw_map_exit_tag(cycle), "OK", (), ut=992.5,
                                 situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_WATCH, st.phase)
        st, acts = self._d(st, snap(ut=1005.0, situation="PRE_LAUNCH"))
        self.assertEqual(["EnterWatchMode"], [a.seam_verb for a in acts])
        watch_tag = acts[0].seam_tag
        st, _ = self._d(st, seam(watch_tag, "OK", (("index", "0"),), ut=1006.0,
                                 situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.phase)
        self.assertEqual(1230.0, st.playback_target_ut)   # 1200 commit + 30
        return st, watch_tag

    def _at_cycle_advance(self, **over):
        """Cycle 1 of 2 watched out: PLAYBACK-WAIT with the first idle probe out."""
        st, _, pdict = self._to_first_rewind(rewindCycles=2, **over)
        st, _ = self._post_rewind_leg(st, 0)
        st, acts = self._d(st, snap(ut=1231.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.phase)
        return st, acts, pdict

    def test_the_default_is_the_single_cycle_lane_byte_for_byte(self):
        traces, rows = [], []
        for over in ({}, {"rewindCycles": 1}):
            st, acts, pdict = self._to_first_rewind(**over)
            self.assertEqual("rewind", acts[0].seam_tag)
            st, watch_tag = self._post_rewind_leg(st, 0)
            self.assertEqual("watch0", watch_tag)
            st, acts = self._d(st, snap(ut=1231.0, situation="PRE_LAUNCH"))
            self.assertEqual(mlib.KXRW_DONE, st.phase)
            self.assertEqual([], acts)
            traces.append(list(self.trace))
            rows.append(mlib.evaluate_kxrw_assertions(
                [], mlib.kxrw_params_from_dict(pdict), st))
        self.assertEqual(traces[0], traces[1])
        # Cycle 0 emits the HISTORICAL constants, verbatim - the byte-identity
        # argument for every lane that declares no key.
        emitted = {tag for _, acts in traces[0] for (_, _, tag, _) in acts if tag}
        self.assertLessEqual({mlib.KXRW_TAG_REWIND, mlib.KXRW_TAG_AUTORECORD,
                              mlib.KXRW_TAG_MAP, mlib.KXRW_TAG_MAP_EXIT}, emitted)
        self.assertFalse(any(t.endswith("c1") or t.startswith("c1idle")
                             for t in emitted), emitted)
        for rs in rows:
            self.assertEqual(8, len(rs))
            self.assertNotIn("rewindCyclesCompleted", [r.name for r in rs])
            self.assertEqual([], [r.name for r in rs if not r.met])
        self.assertEqual([r.name for r in rows[0]], [r.name for r in rows[1]])

    def test_two_cycles_walk_the_post_rewind_block_twice_and_reach_done(self):
        st, acts, pdict = self._to_first_rewind(rewindCycles=2)
        self.assertEqual("rewind", acts[0].seam_tag)
        st, watch0 = self._post_rewind_leg(st, 0)
        self.assertEqual("watch0", watch0)
        # Cycle 1's target: NOT done - an in-phase idle probe instead.
        st, acts = self._d(st, snap(ut=1231.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.phase)
        self.assertFalse(st.done)
        self.assertEqual(1, st.cycles_completed)
        self.assertEqual([("RecordingState", "c1idle0")],
                         [(a.seam_verb, a.seam_tag) for a in acts])
        # The idle reading lands: REWIND under the cycle-1 tag, the SAME tree, and
        # the pre-rewind clock stamped on that frame.
        st, acts = self._d(st, seam("c1idle0", "OK", (("recording", "false"),),
                                    ut=1232.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_REWIND, st.phase)
        self.assertEqual([("InvokeRewindToLaunch", "rewindc1", (("tree", self.TREE),))],
                         [(a.seam_verb, a.seam_tag, tuple(a.seam_args))
                          for a in acts])
        self.assertEqual(1232.0, st.pre_rewind_ut)
        self.assertEqual(1, st.rewind_cycle)
        st, watch1 = self._post_rewind_leg(st, 1)
        # The WATCH family continues rather than restarting at watch0.
        self.assertEqual("watch1", watch1)
        st, acts = self._d(st, snap(ut=1231.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_DONE, st.phase)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(2, st.cycles_completed)
        # THE LOOP NEVER LEFT THE CONTIGUOUS POST-REWIND BLOCK.
        first = st.phases_reached.index(mlib.KXRW_REWIND)
        self.assertEqual(set(), set(st.phases_reached[first:])
                         - set(mlib.KXRW_POST_REWIND_PHASES) - {mlib.KXRW_DONE})
        self.assertEqual(2, st.phases_reached.count(mlib.KXRW_REWIND))
        # EVERY seam tag the whole run emitted is unique.
        emitted = [tag for _, acts in self.trace for (_, _, tag, _) in acts if tag]
        self.assertEqual(len(emitted), len(set(emitted)), emitted)
        rows = mlib.evaluate_kxrw_assertions([], mlib.kxrw_params_from_dict(pdict), st)
        self.assertEqual(9, len(rows))
        self.assertEqual([], [r.name for r in rows if not r.met],
                         [r.to_dict() for r in rows])
        ninth = rows[-1]
        self.assertEqual("rewindCyclesCompleted", ninth.name)
        self.assertEqual(2, ninth.value)
        cycles = ninth.detail["cycles"]
        self.assertEqual([1, 2], [c["cycle"] for c in cycles])
        self.assertEqual(["OK", "OK"], [c["rewindResult"] for c in cycles])
        self.assertEqual([True, True], [c["watcherOnPad"] for c in cycles])
        self.assertEqual([True, True], [c["playbackWatchedOut"] for c in cycles])
        self.assertEqual(["false", "false"], [c["preRewindIdleReading"] for c in cycles])
        self.assertEqual([1202.0, 1232.0], [c["preRewindUT"] for c in cycles])

    def test_a_previous_cycles_ok_never_advances_the_next_cycle(self):
        st, _, _ = self._at_cycle_advance()
        st, _ = self._d(st, seam("c1idle0", "OK", (("recording", "false"),),
                                 ut=1232.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_REWIND, st.phase)
        # Cycle 0's `rewind` OK riding a later snapshot must not count.
        held, _ = self._d(st, seam("rewind", "OK", (), ut=1232.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_REWIND, held.phase)
        st, _ = self._d(st, seam("rewindc1", "OK", (), ut=1232.0, vessel_lost=True))
        st, _ = self._d(st, snap(ut=985.0, vessel_lost=True))
        self.assertEqual(mlib.KXRW_AUTORECORD_OFF, st.phase)
        held, _ = self._d(st, seam("autorec", "OK", (), ut=985.5, vessel_lost=True))
        self.assertEqual(mlib.KXRW_AUTORECORD_OFF, held.phase)

    def test_a_live_recorder_reprobes_then_names_a_parsek_side_observation(self):
        st, _, _ = self._at_cycle_advance(idleFrames=4)
        st, acts = self._d(st, seam("c1idle0", "OK", (("recording", "true"),),
                                    ut=1232.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.phase)
        self.assertEqual(["c1idle1"], [a.seam_tag for a in acts])
        self.assertNotIn("InvokeRewindToLaunch", [a.seam_verb for a in acts])
        probe = 1
        for i in range(10):
            st, acts = self._d(st, seam("c1idle%d" % probe, "OK",
                                        (("recording", "true"),),
                                        ut=1233.0 + i, situation="PRE_LAUNCH"))
            if st.done:
                break
            probe += 1
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.flake_phase)
        self.assertIn("PARSEK-SIDE", st.flake_reason)
        self.assertIn("recording-active", st.flake_reason)
        self.assertIn("rewind 2 of 2", st.flake_reason)

    def test_an_unreadable_clock_reprobes_and_never_becomes_the_stamp(self):
        st, _, _ = self._at_cycle_advance()
        st, acts = self._d(st, seam("c1idle0", "OK", (("recording", "false"),),
                                    ut=float("nan"), situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.phase)
        self.assertEqual(["c1idle1"], [a.seam_tag for a in acts])
        self.assertEqual(1202.0, st.pre_rewind_ut)          # still cycle 0's
        st, acts = self._d(st, seam("c1idle1", "OK", (("recording", "false"),),
                                    ut=1240.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_REWIND, st.phase)
        self.assertEqual(1240.0, st.pre_rewind_ut)

    def test_a_reply_with_no_recording_field_fails_closed(self):
        st, _, _ = self._at_cycle_advance()
        st, acts = self._d(st, seam("c1idle0", "OK", (), ut=1232.0,
                                    situation="PRE_LAUNCH"))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual([], acts)
        self.assertIn("no readable `recording` field", st.flake_reason)

    def test_a_refused_or_silent_probe_flakes(self):
        st, _, _ = self._at_cycle_advance(idleFrames=3)
        refused, _ = self._d(st, seam("c1idle0", "ERROR", (("msg", "x"),),
                                      ut=1232.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.MISSION_FLAKE, refused.verdict)
        self.assertIn("returned ERROR", refused.flake_reason)
        for i in range(5):
            st, _ = self._d(st, snap(ut=1232.0 + i, situation="PRE_LAUNCH"))
            if st.done:
                break
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("never answered", st.flake_reason)

    def test_the_ninth_row_is_unmet_until_every_declared_cycle_completed(self):
        st, _, pdict = self._at_cycle_advance()
        rows = mlib.evaluate_kxrw_assertions([], mlib.kxrw_params_from_dict(pdict), st)
        self.assertEqual(9, len(rows))
        ninth = rows[-1]
        self.assertEqual("rewindCyclesCompleted", ninth.name)
        self.assertFalse(ninth.met)
        self.assertEqual(1, ninth.value)
        self.assertEqual(1, len(ninth.detail["cycles"]))
        self.assertEqual(2, ninth.detail["required"])

    def test_the_next_cycle_resets_its_own_evidence_and_keeps_the_flights(self):
        st, _, _ = self._at_cycle_advance()
        nxt = mlib.kxrw_begin_next_cycle(st, 1500.0)
        for kept in ("tree_id", "launch_ut", "recording_end_ut", "commit_result",
                     "recorder_idle_observed", "cycles_completed", "cycle_history",
                     "post_rewind_vessel_lost_frames"):
            self.assertEqual(getattr(st, kept), getattr(nxt, kept), kept)
        self.assertEqual(1, nxt.rewind_cycle)
        self.assertEqual(1500.0, nxt.pre_rewind_ut)
        self.assertEqual(st.watch_probe + 1, nxt.watch_probe)
        for field_name, blank in (("rewind_result", ""), ("autorecord_off_result", ""),
                                  ("watcher_ready_observed", False),
                                  ("watcher_launch_commanded", False),
                                  ("map_view_result", ""), ("map_exit_result", ""),
                                  ("watch_result", ""), ("watch_attempts", 0),
                                  ("watch_first_attempt_frame", -1),
                                  ("playback_reached", False),
                                  ("cycle_idle_probe", -1)):
            self.assertEqual(blank, getattr(nxt, field_name), field_name)
        self.assertTrue(math.isnan(nxt.ut_regression))
        self.assertTrue(math.isnan(nxt.playback_target_ut))

    def test_every_per_cycle_tag_is_distinct_and_cycle_zero_is_historical(self):
        makers = (mlib.kxrw_rewind_tag, mlib.kxrw_autorecord_tag, mlib.kxrw_map_tag,
                  mlib.kxrw_map_exit_tag)
        self.assertEqual((mlib.KXRW_TAG_REWIND, mlib.KXRW_TAG_AUTORECORD,
                          mlib.KXRW_TAG_MAP, mlib.KXRW_TAG_MAP_EXIT),
                         tuple(f(0) for f in makers))
        tags = []
        for c in range(mlib.KXRW_REWIND_CYCLES_MAX):
            tags += [f(c) for f in makers]
            if c:
                tags += [mlib.kxrw_cycle_idle_probe_tag(c, k) for k in range(12)]
        fixed = [mlib.KXRW_TAG_COMMIT, mlib.KXRW_TAG_STOP, mlib.KXRW_TAG_SC_EXIT,
                 mlib.KXRW_TAG_IMPACT_AUTORECORD]
        families = []
        for k in range(12):
            families += [mlib.kxrw_tree_probe_tag(k), mlib.kxrw_idle_probe_tag(k),
                         mlib.kxrw_watch_probe_tag(k),
                         mlib.kxrw_sc_commit_probe_tag(k)]
        every = tags + fixed + families
        self.assertEqual(len(every), len(set(every)),
                         sorted(t for t in every if every.count(t) > 1))

    def test_the_conflicts_are_refused_on_the_first_frame(self):
        for over, needle in (({"rewindCycles": 0}, "outside [1, 3]"),
                             ({"rewindCycles": 4}, "outside [1, 3]"),
                             ({"rewindCycles": 2, "coastExitProfile": True},
                              "coastExitProfile"),
                             ({"rewindCycles": 2, "impactProfile": True,
                               "watcherCraftName": "GS1 Auto-Chute Booster"},
                              "impactProfile")):
            with self.subTest(over=over):
                st, acts = mlib.kxrw_decide(machine(**over),
                                            snap(ut=0.0, situation="PRE_LAUNCH"))
                self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
                self.assertEqual([], acts)
                self.assertIn(needle, st.flake_reason)
        for over in ({}, {"rewindCycles": 1}, {"rewindCycles": 3},
                     {"rewindCycles": 1, "impactProfile": True},
                     {"rewindCycles": 1, "coastExitProfile": True}):
            with self.subTest(over=over):
                self.assertEqual("", mlib.kxrw_rewind_cycles_conflict(
                    mlib.kxrw_params_from_dict(params(**over))))

    def test_the_schema_bounds_the_param_to_the_machine_range(self):
        with open(SCHEMA_PATH, "rb") as fh:
            schema = tomllib.load(fh)
        decl = schema["params"]["rewindCycles"]
        self.assertEqual(("int", False, 1, mlib.KXRW_REWIND_CYCLES_MAX),
                         (decl["type"], decl["required"], decl["min"], decl["max"]))
        self.assertEqual(1, mlib.KxrwParams().rewind_cycles)
        self.assertEqual(1, mlib.kxrw_params_from_dict({}).rewind_cycles)
        self.assertEqual(2, mlib.kxrw_params_from_dict(
            {"rewindCycles": 2}).rewind_cycles)


if __name__ == "__main__":
    unittest.main()
