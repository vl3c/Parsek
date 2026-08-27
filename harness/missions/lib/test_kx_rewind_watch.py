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
             mlib.KXRW_WATCH, mlib.KXRW_PLAYBACK_WAIT),
            mlib.KXRW_POST_REWIND_PHASES)
        # The union the machine actually consults is the post-rewind block PLUS the
        # rollout, whose FLIGHT->FLIGHT reload is a second, DIFFERENT reason for a
        # dead handle. Two named sets, deliberately not one flat list.
        self.assertEqual((mlib.KXRW_ROLLOUT,) + mlib.KXRW_POST_REWIND_PHASES,
                         mlib.KXRW_VESSEL_LOST_EXPECTED_PHASES)
        # No flight phase may sit in the carve-out: the ascent must still die on a
        # destroyed craft.
        overlap = (set(mlib.KXRW_FLIGHT_PHASES)
                   & set(mlib.KXRW_VESSEL_LOST_EXPECTED_PHASES))
        self.assertEqual(set(), overlap)

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
            seam("watch", "OK", (), ut=55.0, situation="PRE_LAUNCH"),
        ]
        for s in script:
            st, acts = mlib.kxrw_decide(st, s)
            verbs |= {a.seam_verb for a in acts if a.seam_verb}
        self.assertNotIn("StartRecording", verbs)
        self.assertEqual(
            {"RecordingState", "CommitTree", "StopRecording",
             "InvokeRewindToLaunch", "SetSetting", "EnterMapView",
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
    """THE MISSION-VS-PARSEK ORTHOGONALITY RULE, applied to the two render verbs.
    A REFUSED verb is Parsek's finding and belongs to the spec's log contracts; a
    driver-INVALID here would discard the very KSP.log they read."""

    def _to_map_view(self, tree_id="", **over):
        st = dataclasses.replace(machine(**over), phase=mlib.KXRW_MAP_VIEW,
                                 recording_end_ut=400.0, tree_id=tree_id)
        return st

    def test_a_rejected_watch_records_the_verdict_and_flies_on(self):
        st = self._to_map_view()
        st, acts = mlib.kxrw_decide(
            st, seam("map", "ERROR", (("msg", "no-flight-instance"),), ut=250.0))
        self.assertEqual(mlib.KXRW_WATCH, st.phase)
        self.assertEqual("ERROR", st.map_view_result)
        self.assertEqual("no-flight-instance", st.map_view_reject_reason)
        self.assertEqual(["EnterWatchMode"], [a.seam_verb for a in acts])
        st, _ = mlib.kxrw_decide(
            st, seam("watch", "ERROR", (("msg", "unknown-tree"),), ut=251.0))
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
        st = self._to_map_view(tree_id="t_kx")
        _, acts = mlib.kxrw_decide(st, seam("map", "OK", (), ut=250.0))
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
        st = self._to_map_view(tree_id="t_kx")
        st, _ = mlib.kxrw_decide(st, seam("map", "OK", (), ut=250.0))
        st, _ = mlib.kxrw_decide(
            st, seam("watch", "OK", (("index", "2"), ("recId", "rec_abc"),
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
                                 watch_result="ERROR",
                                 watch_reject_reason="already-watching")
        row = [r for r in mlib.evaluate_kxrw_assertions([], p, st)
               if r.name == "renderVerbsDriven"][0]
        self.assertTrue(row.met)
        self.assertTrue(row.detail["metOnRejectionByDesign"])
        self.assertEqual("already-watching", row.detail["enterWatchModeReason"])

    def test_a_verb_that_never_answered_at_all_does_not_meet_the_row(self):
        p = mlib.kxrw_params_from_dict(params())
        st = dataclasses.replace(machine(), map_view_result="OK", watch_result="")
        row = [r for r in mlib.evaluate_kxrw_assertions([], p, st)
               if r.name == "renderVerbsDriven"][0]
        self.assertFalse(row.met)

    def test_a_silent_seam_is_a_transport_fault_and_flakes(self):
        """The distinction the lane depends on: a REFUSED verb rendered a verdict
        and is recorded; a SILENT one rendered nothing, so there is nothing to
        record and the bridge itself is suspect."""
        st = self._to_map_view(watchFrames=3)
        st, _ = mlib.kxrw_decide(st, seam("map", "OK", (), ut=250.0))
        for i in range(6):
            st, _ = mlib.kxrw_decide(st, snap(ut=251.0 + i))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.KXRW_WATCH, st.flake_phase)
        self.assertIn("never answered", st.flake_reason)


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
        st, _ = mlib.kxrw_decide(st, seam("watch", "OK", (), ut=200.0))
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
        st, _ = mlib.kxrw_decide(st, seam("map", "OK", (), ut=992.0,
                                          situation="PRE_LAUNCH"))
        st, _ = mlib.kxrw_decide(st, seam("watch", "OK", (("index", "0"),), ut=993.0,
                                          situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_PLAYBACK_WAIT, st.phase)
        self.assertEqual(1230.0, st.playback_target_ut)     # 1200 commit + 30
        st, _ = mlib.kxrw_decide(st, snap(ut=1231.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.KXRW_DONE, st.phase)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)

        rows = mlib.evaluate_kxrw_assertions([], mlib.kxrw_params_from_dict(pdict),
                                             st)
        unmet = [r.name for r in rows if not r.met]
        self.assertEqual([], unmet, [r.to_dict() for r in rows])
        self.assertEqual(8, len(rows))

    def test_the_machine_is_idempotent_once_done(self):
        st = dataclasses.replace(machine(), done=True, phase=mlib.KXRW_DONE)
        again, acts = mlib.kxrw_decide(st, snap(ut=9999.0))
        self.assertIs(st, again)
        self.assertEqual([], acts)


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

    def test_the_lane_is_not_a_handoff_mission(self):
        """It terminates on exactly the outcome it certifies, so it declares no
        handoff contract (every mission but EVA-4 is absent)."""
        self.assertNotIn("kx_rewind_watch", mlib.MISSION_HANDOFF_CONTRACTS)


if __name__ == "__main__":
    unittest.main()
