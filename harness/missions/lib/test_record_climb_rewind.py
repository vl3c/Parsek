"""Unit cells for mission `record_climb_rewind` (lane WR-1).

Five groups, each guarding a different way this lane can be silently wrong:

  1. THE DELEGATION. Each hop is science_bench_recover, frame for frame: the
     first frame's actions are SBR's, a hop's failure ends this mission under its
     own name, and a hop whose SBR rows did not hold never reaches the rewind.
  2. THE SEAM SEQUENCE. Commit census -> rewind -> backward clock -> editor ->
     launch -> ready -> recorder, each advancing on its OWN tag's OK only, each
     refusal and each silence a named give-up.
  3. THE HOLD. It ends on the first poll at or past the target and not one poll
     earlier, and a stopped clock, a timeout, a craft off the pad or a lost craft
     each end it by name.
  4. THE EVIDENCE. Every row is machine-carried and stamped with its frame's UT,
     and a full scripted run through the real runner resolves MISSION-OK.
  5. PARAMS / SCHEMA / SHELL SYNC.

NO krpc, NO KSP, NO network.
"""

import ast
import dataclasses
import inspect
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
import record_climb_rewind         # noqa: E402
import science_bench_recover       # noqa: E402
from test_shells import FakeMissionControl, run  # noqa: E402

SCHEMA_PATH = os.path.join(_MISSIONS, "record_climb_rewind.schema.toml")
SBR_SCHEMA_PATH = os.path.join(_MISSIONS, "science_bench_recover.schema.toml")

CRAFT = "Jumping Flea"

# CS-3's flight half with a landed-only collect and no transmit (the pod's internal
# antenna cannot send science), plus the one required rewind-half key.
PARAMS = {
    "throttle": 1.0,
    "apoapsisWindowMeters": {"min": 6000, "max": 30000},
    "chuteArmMaxRateMps": 30,
    "chuteFullDeployAltMeters": 2500,
    "landedSituations": ["LANDED", "SPLASHED"],
    "ascentTimeoutSeconds": 90,
    "coastTimeoutSeconds": 180,
    "descentTimeoutSeconds": 360,
    "collectMinExperiments": 1,
    "collectTimeoutSeconds": 120,
    "transmitMaxExperiments": 0,
    "transmitMinScienceGain": 0.5,
    "transmitTimeoutSeconds": 120,
    "recoverMinFundsGain": 1.0,
    "recoverTimeoutSeconds": 180,
    "craftName": CRAFT,
}

# The rewind-half keys this mission adds to SBR's.
RCR_KEYS = {"craftName", "editorFacility", "holdPastFirstRecoverySeconds",
            "holdTimeoutSeconds", "holdStartRecording", "committedFrames",
            "rewindFrames", "spaceCenterFrames", "editorFrames", "launchFrames",
            "readyFrames", "readyDebounceFrames", "recordFrames",
            "minUtRegressionSeconds"}

HOP1_T0 = 100.0
HOP1_RECOVERED_UT = HOP1_T0 + 12.0       # the hop script's vessel-gone frame
HOLD_TARGET_UT = HOP1_RECOVERED_UT + 30.0


def params(**over):
    out = dict(PARAMS)
    out.update(over)
    return out


def machine(**over):
    return mlib.rcr_initial_state(mlib.rcr_params_from_dict(params(**over)))


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


def sci(**kw):
    """A LIVE snapshot with the science lane readable (SBR's test shape)."""
    base = dict(science_experiment_count=2, science_data_count=0,
                science_available_count=2, vessel_recoverable=mlib.RECOVERABLE_YES,
                career_funds=1000.0, career_science=10.0)
    base.update(kw)
    return snap(**base)


def gone(ut, **kw):
    """A frame with no active vessel (Space Center, editor, mid-reload)."""
    return snap(ut=ut, vessel_lost=True, **kw)


def seam(tag, ut, result="OK", payload=(), live=False, **kw):
    """A frame carrying ONE terminal seam reply for ``tag``."""
    kw.setdefault("situation", "PRE_LAUNCH" if live else "")
    return snap(ut=ut, vessel_lost=not live, seam_command_tag=tag,
                seam_command_result=result, seam_command_payload=tuple(payload), **kw)


def hop_frames(t0, funds=1000.0):
    """One SBR pad hop from PRE_LAUNCH to its RECOVERED frame (13 frames): B1's
    proven flight shape, a landed collect, two Recoverable=YES polls (the ask), and
    the vessel-gone frame with the funds pool risen and the recovery ISSUED."""
    f = lambda dt, **kw: sci(ut=t0 + dt, career_funds=funds, **kw)  # noqa: E731
    return [
        f(0.0, stage_solid_fuel=1.0, apoapsis=14000, situation="PRE_LAUNCH"),
        f(1.0, stage_solid_fuel=0.5, apoapsis=14000, situation="FLYING"),
        f(2.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING"),
        f(3.0, vertical_speed=5.0, apoapsis=14000, situation="FLYING"),
        f(4.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING"),
        f(5.0, altitude=5000, apoapsis=14000, situation="FLYING",
          craft_chute_state=mlib.CHUTE_STATE_SEMI_DEPLOYED),
        f(6.0, altitude=2000, apoapsis=14000, situation="FLYING",
          craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
        f(7.0, altitude=100, apoapsis=14000, situation="LANDED",
          craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
        f(8.0, situation="LANDED", science_data_count=1),
        f(9.0, situation="LANDED", science_data_count=1),
        f(10.0, situation="LANDED"),
        f(11.0, situation="LANDED"),
        snap(ut=t0 + 12.0, vessel_lost=True,
             recover_request_result=mlib.RECOVER_REQUEST_ISSUED,
             career_funds=funds + 500.0, career_science=10.0),
    ]


def between_frames(start_recording=True):
    """Hop 1's RECOVERED frame onward to (not including) the hold: census, rewind,
    backward clock, editor, launch, ready, recorder."""
    frames = [
        seam("commit0", 113.0, payload=(("count", "1"),)),
        seam(mlib.RCR_TAG_REWIND, 85.0),
        gone(86.0),
        seam(mlib.RCR_TAG_EDITOR, 87.0),
        seam(mlib.RCR_TAG_LAUNCH, 90.0),
        sci(ut=91.0, situation="PRE_LAUNCH"),
        sci(ut=92.0, situation="PRE_LAUNCH"),
    ]
    if start_recording:
        frames.append(seam(mlib.RCR_TAG_RECORD, 93.0, live=True))
    return frames


def hold_frames(start=94.0, end=HOLD_TARGET_UT):
    """Live pad frames at 1 s spacing from ``start`` up to but NOT including
    ``end``."""
    frames = []
    ut = start
    while ut < end:
        frames.append(sci(ut=ut, situation="PRE_LAUNCH"))
        ut += 1.0
    return frames


def full_run_frames(start_recording=True):
    return (hop_frames(HOP1_T0) + between_frames(start_recording)
            + hold_frames(94.0 if start_recording else 93.0)
            + hop_frames(HOLD_TARGET_UT, funds=1500.0))


def drive(state, frames):
    """Feed frames; return (state, every action emitted)."""
    emitted = []
    for frame in frames:
        state, actions = mlib.rcr_decide(state, frame)
        emitted.extend(actions)
    return state, emitted


def seam_verbs(actions):
    return [(a.seam_verb, a.seam_args, a.seam_tag) for a in actions
            if a.kind == mlib.ACTION_PARSEK_SEAM_COMMAND]


def at(block, **over):
    """A machine driven along the happy path until it ENTERS ``block``."""
    frames = hop_frames(HOP1_T0) + between_frames(over.get("holdStartRecording", True))
    state = machine(**over)
    for frame in frames:
        if state.block == block:
            return state
        state, _ = mlib.rcr_decide(state, frame)
    if state.block == block:
        return state
    raise AssertionError("never reached %s (at %s)" % (block, state.phase))


# ---------------------------------------------------------------------------
# 1. The delegation.
# ---------------------------------------------------------------------------


class HopDelegationTests(unittest.TestCase):

    def test_the_machine_starts_inside_hop_ones_sbr_prelaunch(self):
        state = machine()
        self.assertEqual(mlib.RCR_HOP1, state.block)
        self.assertEqual("HOP1-" + mlib.B1_PRELAUNCH, state.phase)
        self.assertEqual(mlib.B1_PRELAUNCH, state.hop1.phase)

    def test_the_first_frame_emits_sbrs_own_actions_verbatim(self):
        frame = hop_frames(HOP1_T0)[0]
        _, rcr_actions = mlib.rcr_decide(machine(), frame)
        _, sbr_actions = mlib.sbr_decide(
            mlib.sbr_initial_state(mlib.sbr_params_from_dict(params())), frame)
        self.assertEqual(sbr_actions, rcr_actions)
        self.assertEqual([mlib.ACTION_SET_THROTTLE, mlib.ACTION_ACTIVATE_STAGE],
                         [a.kind for a in rcr_actions])

    def test_every_hop_frame_matches_a_bare_sbr_machine(self):
        rcr = machine()
        sbr = mlib.sbr_initial_state(mlib.sbr_params_from_dict(params()))
        for frame in hop_frames(HOP1_T0)[:-1]:
            rcr, a1 = mlib.rcr_decide(rcr, frame)
            sbr, a2 = mlib.sbr_decide(sbr, frame)
            self.assertEqual(a2, a1)
            self.assertEqual(sbr, rcr.hop1)
            self.assertEqual("HOP1-" + sbr.phase, rcr.phase)

    def test_the_recovered_frame_stamps_its_ut_and_asks_for_the_commit_census(self):
        state, actions = drive(machine(), hop_frames(HOP1_T0))
        self.assertEqual(mlib.RCR_COMMITTED, state.block)
        self.assertEqual(HOP1_RECOVERED_UT, state.first_recovered_ut)
        self.assertEqual(mlib.SBR_RECOVERED, state.hop1.phase)
        self.assertEqual([("ListHandles", (("kind", "committed"),), "commit0")],
                         seam_verbs(actions))
        self.assertIn("HOP1-" + mlib.SBR_RECOVERED, state.phases_reached)

    def test_a_hop_one_flake_ends_the_mission_under_its_own_name(self):
        state = machine(ascentTimeoutSeconds=5)
        state, actions = drive(state, [sci(ut=float(i), stage_solid_fuel=1.0,
                                           situation="FLYING") for i in range(12)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertTrue(state.flake_reason.startswith("hop1 flight-leg "),
                        state.flake_reason)
        self.assertEqual("HOP1-" + mlib.B1_ASCENT, state.flake_phase)
        self.assertEqual([], seam_verbs(actions))

    def test_a_hop_one_assert_fail_carries_sbrs_reason(self):
        state, _ = drive(machine(), hop_frames(HOP1_T0)[:3]
                         + [snap(ut=103.0, vessel_lost=True)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertTrue(state.loss_reason.startswith("hop1 "), state.loss_reason)
        self.assertIsNone(state.first_recovered_ut)

    def test_a_hop_whose_sbr_rows_fail_never_reaches_the_rewind(self):
        # flightExperiments declared and never observed airborne: SBR still walks
        # to RECOVERED, and its stagedScienceObserved row is unmet there.
        state, actions = drive(machine(flightExperiments=["crewReport"]),
                               hop_frames(HOP1_T0))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("hop1-assertions-unmet", state.loss_reason)
        self.assertIn("stagedScienceObserved", state.loss_reason)
        self.assertEqual([], seam_verbs(actions))

    def test_the_frozen_detector_holder_is_the_active_hops_b1(self):
        state, _ = drive(machine(), hop_frames(HOP1_T0)[:3])
        self.assertIs(state.hop1.flight, mlib.frozen_detector_holder(state))
        self.assertIsNone(mlib.frozen_detector_holder(at(mlib.RCR_REWIND)))


# ---------------------------------------------------------------------------
# 2. The seam sequence.
# ---------------------------------------------------------------------------


class CommitCensusTests(unittest.TestCase):

    def test_a_zero_count_re_probes_under_a_fresh_tag(self):
        state = at(mlib.RCR_COMMITTED)
        state, actions = mlib.rcr_decide(state, seam("commit0", 113.0,
                                                     payload=(("count", "0"),)))
        self.assertEqual(mlib.RCR_COMMITTED, state.block)
        self.assertEqual([("ListHandles", (("kind", "committed"),), "commit1")],
                         seam_verbs(actions))

    def test_a_count_of_one_sends_the_rewind_with_tree_latest(self):
        state = at(mlib.RCR_COMMITTED)
        state, actions = mlib.rcr_decide(state, seam("commit0", 113.0,
                                                     payload=(("count", "1"),)))
        self.assertEqual(mlib.RCR_REWIND, state.block)
        self.assertEqual([("InvokeRewindToLaunch", (("tree", "latest"),), "rewind")],
                         seam_verbs(actions))
        self.assertEqual(1, state.committed_count)
        self.assertEqual(113.0, state.committed_ut)

    def test_an_unreadable_count_flakes_closed(self):
        state, _ = mlib.rcr_decide(at(mlib.RCR_COMMITTED), seam("commit0", 113.0))
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("count", state.flake_reason)

    def test_another_commands_ok_is_not_this_ones(self):
        state, actions = mlib.rcr_decide(at(mlib.RCR_COMMITTED),
                                         seam("commit7", 113.0, payload=(("count", "1"),)))
        self.assertEqual(mlib.RCR_COMMITTED, state.block)
        self.assertEqual([], actions)

    def test_an_error_flakes_and_silence_flakes_at_the_bound(self):
        state, _ = mlib.rcr_decide(at(mlib.RCR_COMMITTED),
                                   seam("commit0", 113.0, result="ERROR"))
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        state = at(mlib.RCR_COMMITTED, committedFrames=3)
        state, _ = drive(state, [gone(113.0 + i) for i in range(4)])
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("never answered", state.flake_reason)

    def test_zero_counts_past_the_bound_name_the_missing_commit(self):
        state = at(mlib.RCR_COMMITTED, committedFrames=2)
        for i in range(3):
            state, _ = mlib.rcr_decide(state, seam("commit%d" % i, 113.0 + i,
                                                   payload=(("count", "0"),)))
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("hop-1-never-committed", state.flake_reason)


class RewindAndClockTests(unittest.TestCase):

    def test_the_rewind_ok_enters_spacecenter_without_judging_the_clock_yet(self):
        state, actions = mlib.rcr_decide(at(mlib.RCR_REWIND),
                                         seam(mlib.RCR_TAG_REWIND, 85.0))
        self.assertEqual(mlib.RCR_SPACECENTER, state.block)
        self.assertEqual("OK", state.rewind_result)
        self.assertEqual(85.0, state.rewind_ut)
        self.assertEqual([], actions)

    def test_a_refused_rewind_flakes_quoting_parseks_reason(self):
        state, _ = mlib.rcr_decide(
            at(mlib.RCR_REWIND),
            seam(mlib.RCR_TAG_REWIND, 113.0, result="ERROR",
                 payload=(("msg", "no-committed-tree"),)))
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("no-committed-tree", state.flake_reason)
        self.assertEqual("no-committed-tree", state.rewind_reject_reason)

    def test_a_silent_rewind_flakes_at_the_bound(self):
        state, _ = drive(at(mlib.RCR_REWIND, rewindFrames=2),
                         [gone(114.0), gone(115.0), gone(116.0)])
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("InvokeRewindToLaunch", state.flake_reason)

    def test_a_clock_still_at_the_recovery_ut_is_not_a_rewind(self):
        """MUTATION: drop the regression gate and this reds."""
        state, actions = drive(at(mlib.RCR_SPACECENTER),
                               [gone(HOP1_RECOVERED_UT), gone(HOP1_RECOVERED_UT + 5.0),
                                gone(HOP1_RECOVERED_UT - 0.5)])
        self.assertEqual(mlib.RCR_SPACECENTER, state.block)
        self.assertEqual([], actions)

    def test_the_backward_clock_sends_the_editor_with_the_encoded_craft(self):
        state, actions = mlib.rcr_decide(at(mlib.RCR_SPACECENTER), gone(86.0))
        self.assertEqual(mlib.RCR_EDITOR, state.block)
        self.assertEqual(HOP1_RECOVERED_UT - 86.0, state.ut_regression)
        self.assertEqual([("GoToEditor", (("facility", "VAB"), ("craft", "Jumping%20Flea")),
                           "editor")], seam_verbs(actions))

    def test_the_sph_facility_rides_the_editor_args(self):
        state, actions = mlib.rcr_decide(at(mlib.RCR_SPACECENTER, editorFacility="SPH"),
                                         gone(86.0))
        self.assertEqual(("facility", "SPH"), seam_verbs(actions)[0][1][0])

    def test_a_clock_that_never_goes_back_flakes_by_name(self):
        state, _ = drive(at(mlib.RCR_SPACECENTER, spaceCenterFrames=2),
                         [gone(200.0), gone(201.0), gone(202.0)])
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("clock-never-ran-backward", state.flake_reason)


class RelaunchTests(unittest.TestCase):

    def test_the_editor_ok_sends_launch_from_editor_with_no_args(self):
        state, actions = mlib.rcr_decide(at(mlib.RCR_EDITOR),
                                         seam(mlib.RCR_TAG_EDITOR, 87.0))
        self.assertEqual(mlib.RCR_LAUNCH, state.block)
        self.assertEqual([("LaunchFromEditor", (), "launch")], seam_verbs(actions))

    def test_editor_and_launch_refusals_flake_by_name(self):
        for block, tag, verb in ((mlib.RCR_EDITOR, mlib.RCR_TAG_EDITOR, "GoToEditor"),
                                 (mlib.RCR_LAUNCH, mlib.RCR_TAG_LAUNCH,
                                  "LaunchFromEditor")):
            with self.subTest(verb=verb):
                state, _ = mlib.rcr_decide(at(block), seam(tag, 88.0, result="TIMEOUT"))
                self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
                self.assertIn(verb, state.flake_reason)

    def test_ready_tolerates_no_vessel_and_needs_two_live_pad_frames(self):
        state, actions = drive(at(mlib.RCR_READY),
                               [gone(90.5), sci(ut=91.0, situation="PRE_LAUNCH")])
        self.assertEqual(mlib.RCR_READY, state.block)
        self.assertEqual([], actions)
        state, actions = mlib.rcr_decide(state, sci(ut=92.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.RCR_RECORD, state.block)
        self.assertEqual([("StartRecording", (), "record")], seam_verbs(actions))
        self.assertEqual(92.0, state.ready_ut)

    def test_a_relaunch_that_never_settles_flakes(self):
        state, _ = drive(at(mlib.RCR_READY, readyFrames=3),
                         [sci(ut=91.0 + i, situation="FLYING") for i in range(4)])
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("relaunch-never-ready", state.flake_reason)

    def test_hold_start_recording_false_never_sends_start_recording(self):
        state, actions = drive(machine(holdStartRecording=False),
                               full_run_frames(start_recording=False))
        self.assertEqual(mlib.RCR_DONE, state.phase)
        self.assertNotIn("StartRecording", [v for v, _, _ in seam_verbs(actions)])
        self.assertNotIn(mlib.RCR_RECORD, state.phases_reached)

    def test_the_recorder_ok_enters_the_hold_and_a_refusal_flakes(self):
        state, _ = mlib.rcr_decide(at(mlib.RCR_RECORD),
                                   seam(mlib.RCR_TAG_RECORD, 93.0, live=True))
        self.assertEqual(mlib.RCR_HOLD, state.block)
        self.assertEqual(HOLD_TARGET_UT, state.hold_target_ut)
        state, _ = mlib.rcr_decide(at(mlib.RCR_RECORD),
                                   seam(mlib.RCR_TAG_RECORD, 93.0, result="ERROR",
                                        live=True))
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("StartRecording", state.flake_reason)

    def test_a_lost_craft_in_record_is_fatal(self):
        state, _ = mlib.rcr_decide(at(mlib.RCR_RECORD), gone(93.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-on-pad", state.loss_reason)


# ---------------------------------------------------------------------------
# 3. The hold.
# ---------------------------------------------------------------------------


def in_hold(**over):
    state = at(mlib.RCR_RECORD, **over)
    state, _ = mlib.rcr_decide(state, seam(mlib.RCR_TAG_RECORD, 93.0, live=True))
    assert state.block == mlib.RCR_HOLD, state.phase
    return state


class HoldTests(unittest.TestCase):

    def test_the_hold_does_not_end_one_poll_early(self):
        """MUTATION: compare with `>` against target - 1 (or end the hold a poll
        before the target) and this reds."""
        state, actions = drive(in_hold(), hold_frames(94.0) + [
            sci(ut=HOLD_TARGET_UT - 0.001, situation="PRE_LAUNCH")])
        self.assertEqual(mlib.RCR_HOLD, state.block)
        self.assertEqual([], actions)
        self.assertIsNone(state.hold_reached_ut)

    def test_the_target_poll_ignites_hop_two_on_the_same_frame(self):
        state, _ = drive(in_hold(), hold_frames(94.0))
        state, actions = mlib.rcr_decide(state, hop_frames(HOLD_TARGET_UT)[0])
        self.assertEqual(mlib.RCR_HOP2, state.block)
        self.assertEqual(HOLD_TARGET_UT, state.hold_reached_ut)
        self.assertEqual([mlib.ACTION_SET_THROTTLE, mlib.ACTION_ACTIVATE_STAGE],
                         [a.kind for a in actions])
        self.assertIsNot(state.hop1, state.hop2)
        self.assertEqual("HOP2-" + mlib.B1_ASCENT, state.phase)

    def test_the_hold_commands_nothing(self):
        _, actions = drive(in_hold(), hold_frames(94.0))
        self.assertEqual([], actions)

    def test_a_zero_hold_ends_on_the_first_poll_at_the_recovery_ut(self):
        state = in_hold(holdPastFirstRecoverySeconds=0.0)
        self.assertEqual(HOP1_RECOVERED_UT, state.hold_target_ut)
        state, _ = drive(state, hold_frames(94.0, HOP1_RECOVERED_UT))
        self.assertEqual(mlib.RCR_HOLD, state.block)
        state, _ = mlib.rcr_decide(state, hop_frames(HOP1_RECOVERED_UT)[0])
        self.assertEqual(mlib.RCR_HOP2, state.block)

    def test_a_stopped_clock_flakes_and_not_one_poll_before_the_limit(self):
        # The hold entered at UT 93 (the recorder's OK frame), so 93 is not an advance.
        stuck = [sci(ut=93.0, situation="PRE_LAUNCH")] * mlib.RCR_HOLD_CLOCK_STALL_FRAMES
        state, _ = drive(in_hold(), stuck[:-1])
        self.assertFalse(state.done)
        state, _ = mlib.rcr_decide(state, stuck[-1])
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("hold-clock-stalled", state.flake_reason)

    def test_an_advancing_poll_resets_the_stall_run(self):
        frames = ([sci(ut=100.0, situation="PRE_LAUNCH")] * 30
                  + [sci(ut=101.0, situation="PRE_LAUNCH")]
                  + [sci(ut=101.0, situation="PRE_LAUNCH")] * 30)
        state, _ = drive(in_hold(), frames)
        self.assertFalse(state.done)
        self.assertEqual(30, state.hold_stall_frames)

    def test_a_hold_past_its_game_time_budget_flakes(self):
        state = in_hold(holdTimeoutSeconds=5.0, holdPastFirstRecoverySeconds=600.0)
        state, _ = drive(state, [sci(ut=93.0 + i, situation="PRE_LAUNCH")
                                 for i in range(1, 7)])
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("hold-never-reached-target", state.flake_reason)

    def test_a_craft_off_the_pad_is_a_named_assert_fail(self):
        state, _ = drive(in_hold(), [sci(ut=94.0, situation="FLYING")])
        self.assertFalse(state.done)
        state, _ = mlib.rcr_decide(state, sci(ut=95.0, situation="FLYING"))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("pad-craft-left-prelaunch", state.loss_reason)

    def test_landed_and_an_unread_situation_are_not_off_the_pad(self):
        state, _ = drive(in_hold(), [sci(ut=94.0, situation="LANDED"),
                                     sci(ut=95.0, situation=""),
                                     sci(ut=96.0, situation="LANDED")])
        self.assertFalse(state.done)

    def test_a_lost_craft_in_the_hold_is_fatal(self):
        state, _ = mlib.rcr_decide(in_hold(), gone(94.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-on-pad", state.loss_reason)


# ---------------------------------------------------------------------------
# 4. The evidence, and the whole run.
# ---------------------------------------------------------------------------


ROW_NAMES = ["hop1FlightCompletedObserved", "hop1ScienceCollectedObserved",
             "hop1VesselRecoveredObserved", "hop1RecoveredObserved",
             "hop1CommittedObserved", "rewindVerdictOk", "clockObservedBackward",
             "relaunchVerdictsOk", "relaunchReadyObserved", "recorderStartedObserved",
             "holdReachedTarget", "hop2FlightCompletedObserved",
             "hop2ScienceCollectedObserved", "hop2VesselRecoveredObserved",
             "hop2RecoveredObserved"]


class EvidenceTests(unittest.TestCase):

    def _done(self, **over):
        state, actions = drive(machine(**over),
                               full_run_frames(over.get("holdStartRecording", True)))
        return state, actions

    def test_the_happy_path_walks_every_phase_in_order(self):
        state, actions = self._done()
        self.assertTrue(state.done)
        self.assertIsNone(state.verdict)
        self.assertTrue(state.skip_settle_tail)
        blocks = [p for p in state.phases_reached if p in mlib.RCR_PHASES]
        self.assertEqual(list(mlib.RCR_PHASES), blocks)
        self.assertEqual(
            ["ListHandles", "InvokeRewindToLaunch", "GoToEditor", "LaunchFromEditor",
             "StartRecording"], [v for v, _, _ in seam_verbs(actions)])

    def test_every_row_holds_and_is_stamped_from_its_frame(self):
        state, _ = self._done()
        rows = mlib.evaluate_rcr_assertions([], state.params, state)
        self.assertEqual(ROW_NAMES, [r.name for r in rows])
        self.assertEqual([], [r.name for r in rows if not r.met])
        by = {r.name: r for r in rows}
        self.assertEqual(HOP1_RECOVERED_UT, by["hop1RecoveredObserved"].value)
        self.assertEqual(113.0, by["hop1CommittedObserved"].detail["ut"])
        self.assertEqual(85.0, by["rewindVerdictOk"].detail["ut"])
        self.assertEqual(86.0, by["clockObservedBackward"].detail["ut"])
        self.assertEqual(87.0, by["relaunchVerdictsOk"].detail["editorUt"])
        self.assertEqual(90.0, by["relaunchVerdictsOk"].detail["launchUt"])
        self.assertEqual(92.0, by["relaunchReadyObserved"].detail["ut"])
        self.assertEqual(93.0, by["recorderStartedObserved"].detail["ut"])
        self.assertEqual(HOLD_TARGET_UT, by["holdReachedTarget"].detail["targetUt"])
        self.assertEqual(HOLD_TARGET_UT, by["holdReachedTarget"].value)
        self.assertEqual(HOLD_TARGET_UT + 12.0, by["hop2RecoveredObserved"].value)
        self.assertEqual(14000, by["hop2FlightCompletedObserved"].detail["peakApoapsis"])
        self.assertEqual((mlib.MISSION_OK, "all telemetry assertions met"),
                         mlib.resolve_flight_verdict(state, rows))

    def test_the_recorder_row_is_absent_when_not_asked_for(self):
        state, _ = self._done(holdStartRecording=False)
        rows = mlib.evaluate_rcr_assertions([], state.params, state)
        self.assertNotIn("recorderStartedObserved", [r.name for r in rows])
        self.assertEqual([], [r.name for r in rows if not r.met])

    def test_a_fresh_machine_meets_no_rcr_row(self):
        state = machine()
        rows = {r.name: r for r in mlib.evaluate_rcr_assertions([], state.params, state)}
        for name in ROW_NAMES:
            if name.startswith(("hop1", "hop2")) and "Recovered" not in name:
                continue
            self.assertFalse(rows[name].met, name)

    def test_a_hop_two_failure_is_named_hop2(self):
        frames = (hop_frames(HOP1_T0) + between_frames() + hold_frames(94.0)
                  + hop_frames(HOLD_TARGET_UT)[:3]
                  + [snap(ut=HOLD_TARGET_UT + 3.0, vessel_lost=True)])
        state, _ = drive(machine(), frames)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertTrue(state.loss_reason.startswith("hop2 "), state.loss_reason)

    def test_hop_two_asks_for_its_own_recovery_under_hop_ones_standing_token(self):
        # The runner's recover token is a sticky level: once hop 1's recovery is ISSUED
        # it stands on every later snapshot until the next perform (mission_runner never
        # resets it). WR-1's first flight (2026-10-27) red on exactly this: hop 2's SBR
        # latched recover_issued from hop 1's token and never asked (asks=0).
        frames = full_run_frames()
        first_issued = next(i for i, f in enumerate(frames)
                            if f.recover_request_result == mlib.RECOVER_REQUEST_ISSUED)
        sticky = frames[:first_issued + 1] + [
            dataclasses.replace(f, recover_request_result=mlib.RECOVER_REQUEST_ISSUED)
            for f in frames[first_issued + 1:]]
        state, actions = drive(machine(), sticky)
        self.assertTrue(state.done, state.phase)
        self.assertIsNone(state.verdict, state.loss_reason)
        self.assertEqual(1, state.hop1.recover_asks)
        self.assertEqual(1, state.hop2.recover_asks)
        self.assertEqual(2, sum(1 for a in actions if a.kind == mlib.ACTION_RECOVER_VESSEL))

    def test_a_hop_that_has_asked_reads_the_runner_token_unmasked(self):
        hop = mlib.sbr_initial_state(mlib.rcr_params_from_dict(params()).hop)
        issued = snap(ut=1.0, recover_request_result=mlib.RECOVER_REQUEST_ISSUED)
        self.assertEqual(mlib.RECOVER_REQUEST_UNREAD,
                         mlib.rcr_hop_snapshot(hop, issued).recover_request_result)
        asked = dataclasses.replace(hop, recover_asks=1)
        self.assertIs(issued, mlib.rcr_hop_snapshot(asked, issued))

    def test_the_whole_run_through_the_real_runner_is_mission_ok(self):
        control = FakeMissionControl(full_run_frames())
        code, result = run(record_climb_rewind.SPEC, params(), control, budget=3600.0)
        self.assertEqual(0, code, result)
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertIn("not verified here", result["reason"])
        verbs = [a.seam_verb for a in control.actions
                 if a.kind == mlib.ACTION_PARSEK_SEAM_COMMAND]
        self.assertEqual(["ListHandles", "InvokeRewindToLaunch", "GoToEditor",
                          "LaunchFromEditor", "StartRecording"], verbs)


# ---------------------------------------------------------------------------
# 5. Params / schema / shell sync.
# ---------------------------------------------------------------------------


class ParamsTests(unittest.TestCase):

    def test_the_defaults(self):
        p = mlib.rcr_params_from_dict(params())
        self.assertEqual(CRAFT, p.craft_name)
        self.assertEqual("VAB", p.editor_facility)
        self.assertEqual(30.0, p.hold_past_first_recovery)
        self.assertEqual(1800.0, p.hold_timeout)
        self.assertTrue(p.hold_start_recording)
        self.assertEqual((120, 40, 120, 40, 40, 240, 2, 40, 1.0),
                         (p.committed_frames, p.rewind_frames, p.space_center_frames,
                          p.editor_frames, p.launch_frames, p.ready_frames,
                          p.ready_debounce, p.record_frames, p.min_ut_regression))

    def test_craft_name_is_required(self):
        for bad in ({}, {"craftName": ""}, {"craftName": "  "}, {"craftName": 7}):
            with self.subTest(bad=bad):
                over = {k: v for k, v in PARAMS.items() if k != "craftName"}
                over.update(bad)
                with self.assertRaises(ValueError):
                    mlib.rcr_params_from_dict(over)

    def test_the_editor_facility_is_a_closed_set(self):
        self.assertEqual("SPH", mlib.rcr_params_from_dict(
            params(editorFacility="SPH")).editor_facility)
        for bad in ("Hangar", "vab", ""):
            with self.subTest(bad=bad):
                with self.assertRaises(ValueError):
                    mlib.rcr_params_from_dict(params(editorFacility=bad))

    def test_the_sbr_keys_pass_through_to_both_hops(self):
        p = mlib.rcr_params_from_dict(params(preRecoverDwellSeconds=12.0))
        self.assertEqual(mlib.sbr_params_from_dict(params(preRecoverDwellSeconds=12.0)),
                         p.hop)
        state, _ = drive(machine(), full_run_frames())
        self.assertEqual(state.hop1.params, state.hop2.params)

    def test_the_hold_keys_parse(self):
        p = mlib.rcr_params_from_dict(params(holdPastFirstRecoverySeconds=45,
                                             holdTimeoutSeconds=600,
                                             holdStartRecording=False))
        self.assertEqual((45.0, 600.0, False),
                         (p.hold_past_first_recovery, p.hold_timeout,
                          p.hold_start_recording))


def _keys_read_by(fn):
    keys = set()
    for node in ast.walk(ast.parse(inspect.getsource(fn))):
        if (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                and node.func.attr == "get" and isinstance(node.func.value, ast.Name)
                and node.func.value.id == "params" and node.args
                and isinstance(node.args[0], ast.Constant)
                and isinstance(node.args[0].value, str)):
            keys.add(node.args[0].value)
    return keys


class SchemaSyncTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        with open(SCHEMA_PATH, "rb") as fh:
            cls.schema = tomllib.load(fh)
        with open(SBR_SCHEMA_PATH, "rb") as fh:
            cls.sbr = tomllib.load(fh)["params"]
        cls.params = cls.schema["params"]

    def test_the_schema_is_sbrs_declarations_plus_the_rewind_keys(self):
        self.assertEqual(set(self.sbr) | RCR_KEYS, set(self.params))
        self.assertEqual(set(), set(self.sbr) & RCR_KEYS)

    def test_every_sbr_declaration_is_repeated_verbatim(self):
        for key, decl in self.sbr.items():
            self.assertEqual(decl, self.params[key], key)

    def test_the_rewind_keys_are_exactly_what_the_params_builder_reads(self):
        """MUTATION: add a `params.get("newKnob")` to rcr_params_from_dict without a
        schema block, or drop a read, and this reds."""
        read = _keys_read_by(mlib.rcr_params_from_dict)
        self.assertEqual(RCR_KEYS, read)

    def test_the_committed_params_validate(self):
        import hlib
        self.assertEqual([], hlib._validate_mission_params(PARAMS, self.schema))

    def test_admit_refuses_a_missing_craft_and_an_unknown_facility(self):
        import hlib
        no_craft = {k: v for k, v in PARAMS.items() if k != "craftName"}
        self.assertTrue(any("craftName" in e for e in
                            hlib._validate_mission_params(no_craft, self.schema)))
        self.assertTrue(hlib._validate_mission_params(params(editorFacility="Hangar"),
                                                      self.schema))
        self.assertTrue(hlib._validate_mission_params(params(holdStartRecording="yes"),
                                                      self.schema))
        self.assertTrue(hlib._validate_mission_params(
            params(holdPastFirstRecoverySeconds=-1), self.schema))

    def test_the_optional_rewind_keys_are_optional(self):
        for key in RCR_KEYS - {"craftName"}:
            self.assertFalse(self.params[key]["required"], key)
        self.assertTrue(self.params["craftName"]["required"])


class SeamWireTests(unittest.TestCase):

    def test_encode_matches_run_pys_encoder(self):
        with open(os.path.join(_HARNESS, "run.py"), "r", encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        fn = next(n for n in tree.body
                  if isinstance(n, ast.FunctionDef) and n.name == "encode_value")
        scope = {}
        exec(compile(ast.Module(body=[fn], type_ignores=[]), "run.py", "exec"), scope)
        for value in ("Jumping Flea", "VAB", "a=b%c", "tab\there", "Kerbal\u00e9 X", ""):
            self.assertEqual(scope["encode_value"](value), mlib.encode_seam_value(value))
            self.assertEqual(value, mlib.decode_seam_value(mlib.encode_seam_value(value)))
        self.assertEqual("Jumping%20Flea", mlib.encode_seam_value("Jumping Flea"))

    def test_the_new_verbs_poll_above_their_dispatch_budgets(self):
        import hlib
        for verb in ("GoToEditor", "LaunchFromEditor", "StartRecording",
                     "InvokeRewindToLaunch"):
            with self.subTest(verb=verb):
                self.assertGreater(mlib.seam_command_poll_seconds(verb),
                                   hlib.DISPATCH_DEFERRAL_BUDGET_SECONDS[verb])


class ShellWiringTests(unittest.TestCase):

    def test_the_spec_wires_the_machine_with_no_warp_and_no_settle_tail(self):
        spec = record_climb_rewind.SPEC
        self.assertEqual("record_climb_rewind", spec.name)
        self.assertIs(False, spec.allow_rails_warp)
        self.assertEqual(0.0, spec.max_physics_warp)
        self.assertEqual(0, spec.settle_frames)
        self.assertEqual(science_bench_recover.SPEC.allow_rails_warp, spec.allow_rails_warp)
        self.assertEqual(science_bench_recover.SPEC.max_physics_warp, spec.max_physics_warp)

    def test_the_control_is_sbrs_flag_for_flag(self):
        rcr = dict(vars(record_climb_rewind.make_control()))
        sbr = dict(vars(science_bench_recover.make_control()))
        self.assertEqual("record_climb_rewind", rcr.pop("_client_name"))
        sbr.pop("_client_name")
        self.assertEqual(sorted(sbr), sorted(rcr))
        plain = (bool, int, float, str, type(None))
        config = [k for k, v in sbr.items() if isinstance(v, plain)]
        for key in ("_read_chute", "_read_science", "_tolerate_unreadable_nodes",
                    "_use_mechjeb", "_read_vessel_name"):
            self.assertIn(key, config)
        for key in config:
            self.assertEqual(sbr[key], rcr[key], key)
        self.assertTrue(rcr["_tolerate_unreadable_nodes"])
        self.assertFalse(rcr["_read_vessel_name"])

    def test_the_flight_efficiency_registry_names_the_machine(self):
        import flighteff
        self.assertEqual("rcr_decide", flighteff.MISSION_MACHINES["record_climb_rewind"])

    def test_the_handoff_contract(self):
        contract = mlib.mission_handoff_contract("record_climb_rewind")
        self.assertEqual(mlib.RCR_DONE, contract["terminal"])
        self.assertEqual(["recordBandRaised", "secondHopUnpaid",
                          "ledgerThresholdCreditedOnce"], contract["unverifiedByMission"])
        self.assertEqual(["logContracts", "LedgerGroundTruth"], contract["verifiedBy"])

    def test_the_shell_imports_with_no_krpc_installed(self):
        self.assertNotIn("krpc", sys.modules)


if __name__ == "__main__":
    unittest.main()
