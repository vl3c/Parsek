"""Unit cells for the CL-3 re-fly crew-tombstone lane (mission
`cl3_refly_crew_tombstone`): the pure `mlib.cl3_decide` machine, its assertion
rows, its declared schema, and the thin shell.

Runnable with the stdlib runner only (NO pytest, NO kRPC, NO KSP, NO network,
NO real filesystem write)::

    cd harness && python -m unittest discover -s missions/lib -q

SIX GROUPS, each naming the failure a live flight would otherwise be first to
discover:

  1. THE CUT IS REAL, AND R1 IS UNTOUCHED. CL-3 is R1 with ASCENT and COMMIT
     removed. Both halves of that sentence need pinning: that the two phases and
     their params / rows are actually gone here, and that the R1 machine they
     were lifted from still carries them (this lane was added ALONGSIDE R1, and
     a "shared refactor" that quietly moved R1 would be a change to a lane with
     three live flights behind it).
  2. THE OPENING. R1 emits StopRecording on its COMMIT->STOP transition; CL-3
     has no phase in front of STOP and must arm the command itself, EXACTLY
     once. A second issue reuses the wire id, and the C# seam SKIPS DUPLICATE
     IDS -- the repeat would be a silent no-op whose poll expires as a bogus
     TIMEOUT that reads like a wedged addon.
  3. EVERY PHASE TRANSITION AND EVERY GIVE-UP, one cell each. The give-ups are
     the point: an operator must read WHICH of the ways the cycle can stall
     actually fired, so every terminal is asserted by its own named text.
  4. THE BACKWARD-CLOCK OBSERVATION, which is the load-bearing cell of the lane.
     `InvokeRewind` answering OK is a COMMANDED reading; the advance is the
     OBSERVED regression of the game clock, and the phase is bounded in FRAMES
     because past a rewind a game-time budget can never expire.
  5. THE ASSERTION ROWS, including the anti-vacuity cases the machine's own
     happy path cannot produce (constructed states, the R1 review's lesson: a
     row whose every conjunct survives mutation is covering nothing).
  6. SCHEMA / SHELL WIRING, checked THROUGH the code the harness actually runs
     (`hlib._validate_mission_params` over the committed TOML), because a schema
     that declares a key the machine never reads is a key nobody tunes.

The rewind tail's semantics are R1's, already mutation-verified in
test_r1_rewind.py; the cells here re-prove them ON THIS MACHINE (a lifted tail
that drifted would otherwise be caught only by a flight) and cover the CL-3
specific wiring. Each cell names the mutation it dies under.
"""

import copy
import math
import os
import re
import sys
import tomllib
import unittest
from dataclasses import fields, replace

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if os.path.join(_HARNESS, "lib") not in sys.path:
    sys.path.insert(0, os.path.join(_HARNESS, "lib"))
# harness/ itself, for `import run` -- the SHELL half of the spec gate
# (resolve_mission_schemas), so the committed spec is validated through the same
# two calls the harness makes rather than through a restated schema registry.
if _HARNESS not in sys.path:
    sys.path.insert(0, _HARNESS)

import mlib                              # noqa: E402
import hlib                              # noqa: E402
import saveparse                         # noqa: E402
import mission_runner                    # noqa: E402
import run as harness_run                # noqa: E402
import cl3_refly_crew_tombstone as cl3   # noqa: E402
from mlib import TelemetrySnapshot       # noqa: E402
from test_shells import FakeMissionControl, run  # noqa: E402

SCHEMA_PATH = os.path.join(_MISSIONS, "cl3_refly_crew_tombstone.schema.toml")
SHELL_PATH = os.path.join(_MISSIONS, "cl3_refly_crew_tombstone.py")
SPEC_PATH = os.path.join(_HARNESS, "scenarios", "CL-3-refly-crew-tombstone.toml")
REGISTRY_PATH = os.path.join(_HARNESS, "coverage", "registry.toml")


def snap(**kw):
    return TelemetrySnapshot(**kw)


def seam(tag, result="OK", payload=(), **kw):
    """A snapshot carrying ONE tagged terminal seam response."""
    return snap(seam_command_result=result, seam_command_tag=tag,
                seam_command_payload=tuple(payload), **kw)


# The injected `rewind-crew-loss` corpus's own target: RP `rp_cl_root`, slot 1 =
# `cl-pod-a`, the CREWED pod whose kerbal died. Slot 0 is the crewless probe, and
# rewinding onto THAT would merge cleanly while tombstoning nothing.
# NOTE: no `minAltitudeChangeMeters` -- the mission no longer carries the
# `vesselStateChanged` row it thresholded, and the schema no longer declares it.
CL3_MISSION_PARAMS = {
    "rewindPointId": "rp_cl_root",
    "rewindSlot": 1,
    "minUtRegressionSeconds": 5.0,
}


def cl3_params(**over):
    d = dict(CL3_MISSION_PARAMS)
    d.update(over)
    return mlib.cl3_params_from_dict(d)


def fresh(**over):
    return mlib.cl3_initial_state(cl3_params(**over))


def drive_to_stop_armed(**over):
    """Frame 1: the machine arms its OWN StopRecording and parks in STOP."""
    st = fresh(**over)
    st, actions = mlib.cl3_decide(st, snap(ut=1000.0))
    assert st.phase == mlib.CL3_STOP, st.phase
    assert [a.seam_verb for a in actions] == ["StopRecording"], actions
    return st


def drive_to_idle(**over):
    """STOP answered OK -> parked in RECORDER-IDLE with probe 0 emitted."""
    st = drive_to_stop_armed(**over)
    st, _ = mlib.cl3_decide(st, seam(mlib.CL3_TAG_STOP, ut=1001.0))
    assert st.phase == mlib.CL3_RECORDER_IDLE, st.phase
    return st


def drive_to_rewind(pre_ut=1002.0, pre_alt=8000.0, pre_sit="FLYING", **over):
    """Probe 0 read recording=false -> parked in REWIND with InvokeRewind emitted
    and the pre-rewind observation stamped at `pre_ut`."""
    st = drive_to_idle(**over)
    st, _ = mlib.cl3_decide(
        st, seam(mlib.r1_state_probe_tag(0), payload=(("recording", "false"),),
                 ut=pre_ut, altitude=pre_alt, situation=pre_sit, body="Kerbin"))
    assert st.phase == mlib.CL3_REWIND, st.phase
    return st


def drive_to_verify(**over):
    """REWIND answered OK -> parked in VERIFY."""
    st = drive_to_rewind(**over)
    st, _ = mlib.cl3_decide(st, seam(mlib.CL3_TAG_REWIND, ut=1003.0))
    assert st.phase == mlib.CL3_VERIFY, st.phase
    return st


def drive_to_relaunch(post_ut=402.0, post_alt=100.0, post_sit="PRE_LAUNCH", **over):
    """The backward clock was OBSERVED -> through the REWOUND waypoint into
    RELAUNCH, with the re-fly actions already emitted."""
    st = drive_to_verify(**over)
    st, _ = mlib.cl3_decide(
        st, snap(ut=post_ut, altitude=post_alt, situation=post_sit, body="Kerbin"))
    assert st.phase == mlib.CL3_REWOUND, st.phase
    st, _ = mlib.cl3_decide(
        st, snap(ut=post_ut + 1.0, altitude=post_alt, situation=post_sit))
    assert st.phase == mlib.CL3_RELAUNCH, st.phase
    return st


def drive_to_loop_points(post_alt=100.0, **over):
    """The rewound craft CLIMBED -> parked in LOOP-POINTS with probe 0 emitted."""
    st = drive_to_relaunch(post_alt=post_alt, **over)
    st, _ = mlib.cl3_decide(
        st, snap(ut=410.0, altitude=post_alt + 500.0, situation="FLYING"))
    assert st.phase == mlib.CL3_LOOP_POINTS, st.phase
    return st


def drive_to_loop_closed(points=42, **over):
    """The re-fly was RECORDED -> terminal LOOP-CLOSED."""
    st = drive_to_loop_points(**over)
    st, _ = mlib.cl3_decide(
        st, seam(mlib.r1_loop_probe_tag(0), payload=(("points", str(points)),),
                 ut=411.0, altitude=700.0, situation="FLYING"))
    assert st.phase == mlib.CL3_LOOP_CLOSED, st.phase
    return st


# ---------------------------------------------------------------------------
# 1. The cut is real, and R1 is untouched.
# ---------------------------------------------------------------------------


class Cl3ShapeTests(unittest.TestCase):
    """CL-3 is R1 MINUS the ascent and the commit. Both halves need pinning."""

    def test_the_phase_list_is_r1s_tail_in_r1s_order(self):
        """MUTATION: reorder the tail (e.g. put RELAUNCH before REWOUND) and this
        reds. The order is the contract every drive helper below assumes."""
        self.assertEqual(
            mlib.CL3_PHASES,
            (mlib.CL3_STOP, mlib.CL3_RECORDER_IDLE, mlib.CL3_REWIND,
             mlib.CL3_VERIFY, mlib.CL3_REWOUND, mlib.CL3_RELAUNCH,
             mlib.CL3_LOOP_POINTS, mlib.CL3_LOOP_CLOSED))
        # The eight phase NAMES are R1's eight surviving names, verbatim, so a
        # reader comparing the two lanes' logs is comparing like with like.
        self.assertEqual(
            mlib.CL3_PHASES,
            tuple(p for p in mlib.R1_PHASES
                  if p not in (mlib.R1_ASCENT, mlib.R1_COMMIT)))

    def test_there_is_no_ascent_or_commit_surface_on_this_lane(self):
        """The cut has to be a real absence, not a phase that exists and is
        skipped: an unreachable phase is a give-up path nobody can read."""
        for gone in ("CL3_ASCENT", "CL3_COMMIT", "CL3_TAG_COMMIT"):
            self.assertFalse(hasattr(mlib, gone),
                             "%s must not exist on the CL-3 lane" % gone)
        names = {f.name for f in fields(mlib.Cl3Params)}
        self.assertNotIn("b2", names)
        self.assertNotIn("commit_frames", names)
        self.assertNotIn("commit_result", {f.name for f in fields(mlib.Cl3State)})

    def test_the_machine_starts_in_stop_not_in_an_ascent(self):
        st = fresh()
        self.assertEqual(st.phase, mlib.CL3_STOP)
        self.assertEqual(st.phases_reached, (mlib.CL3_STOP,))
        self.assertFalse(st.done)

    def test_the_r1_machine_still_carries_the_halves_cl3_dropped(self):
        """ADDED ALONGSIDE, NOT REFACTORED THROUGH. R1 has live flights behind it;
        a "shared" rewrite that moved R1 to serve CL-3 would put those at risk.

        MUTATION: delete R1_ASCENT / R1_COMMIT / commit_frames (i.e. "simplify"
        R1 into CL-3) and this reds."""
        self.assertEqual(mlib.R1_PHASES[0], mlib.R1_ASCENT)
        self.assertIn(mlib.R1_COMMIT, mlib.R1_PHASES)
        self.assertEqual(mlib.R1_TAG_COMMIT, "commit")
        self.assertEqual(mlib.r1_params_from_dict({}).commit_frames, 40)
        self.assertIn("b2", {f.name for f in fields(mlib.R1Params)})

    def test_the_two_lanes_share_the_probe_tag_families(self):
        """The repeated probes reuse R1's families deliberately (the V1
        precedent): they are already per-probe, and already distinct from each
        other, so a LOOP-POINTS probe can never collide with a RECORDER-IDLE
        probe at the same index."""
        self.assertNotEqual(mlib.r1_state_probe_tag(0), mlib.r1_loop_probe_tag(0))
        self.assertEqual(mlib.CL3_TAG_STOP, "stop")
        self.assertEqual(mlib.CL3_TAG_REWIND, "rewind")
        self.assertNotEqual(mlib.CL3_TAG_STOP, mlib.CL3_TAG_REWIND)


# ---------------------------------------------------------------------------
# 2. The pre-flight guard and the opening StopRecording.
# ---------------------------------------------------------------------------


class Cl3PreflightGuardTests(unittest.TestCase):
    """MUTATION: delete the frame-1 rewind-target check in cl3_decide and these
    red -- the real defect being a machine that stops the live recorder, polls it
    idle and drives a whole cycle toward a verb that could only ever answer
    REJECTED unknown-rp."""

    def test_unset_rewind_point_flakes_on_frame_one(self):
        st = fresh(rewindPointId="")
        out, actions = mlib.cl3_decide(st, snap(ut=1000.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("rewind target unresolved", out.flake_reason)
        self.assertIn(mlib.CL3_STOP, out.flake_reason)
        self.assertEqual(actions, [])

    def test_unset_slot_flakes_on_frame_one(self):
        st = fresh(rewindSlot=-1)
        out, actions = mlib.cl3_decide(st, snap(ut=1000.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(actions, [])

    def test_the_guard_fires_before_the_recorder_is_ever_stopped(self):
        """ORDERING MATTERS: stopping a live recorder is a side effect on the
        player's save. A mission that cannot possibly rewind must not take it.

        MUTATION: move the guard below the STOP arm and this reds."""
        st = fresh(rewindPointId="")
        out, actions = mlib.cl3_decide(st, snap(ut=1000.0))
        self.assertFalse(out.stop_command_emitted)
        self.assertEqual([a.seam_verb for a in actions], [])

    def test_a_resolved_target_does_not_trip_the_guard(self):
        st = fresh()
        out, _ = mlib.cl3_decide(st, snap(ut=1000.0))
        self.assertFalse(out.done)
        self.assertEqual(out.phase, mlib.CL3_STOP)


class Cl3StopArmTests(unittest.TestCase):
    """R1 emits StopRecording on its COMMIT->STOP transition. CL-3 has no phase
    in front of STOP and arms the command itself."""

    def test_frame_one_emits_exactly_one_stop_recording_command(self):
        """MUTATION: drop the self-arm (assume some earlier phase emitted it) and
        this reds -- the machine would sit in STOP until stopFrames expired,
        reporting a seam that "never answered" a command nobody ever sent."""
        st = fresh()
        out, actions = mlib.cl3_decide(st, snap(ut=1000.0))
        self.assertEqual(len(actions), 1)
        self.assertEqual(actions[0].kind, mlib.ACTION_PARSEK_SEAM_COMMAND)
        self.assertEqual(actions[0].seam_verb, "StopRecording")
        self.assertEqual(actions[0].seam_tag, mlib.CL3_TAG_STOP)
        self.assertEqual(actions[0].seam_args, ())
        self.assertTrue(out.stop_command_emitted)
        self.assertEqual(out.phase, mlib.CL3_STOP)

    def test_the_stop_command_is_never_issued_twice(self):
        """A reused wire id is one the C# seam SKIPS: the repeat is a silent
        no-op whose poll then expires as a TIMEOUT that reads like a wedged
        addon.

        MUTATION: gate the arm on `phase_frames == 1` instead of the latch, then
        feed a frame that does not advance the phase, and this reds."""
        st = fresh()
        emitted = []
        for i in range(6):
            st, actions = mlib.cl3_decide(st, snap(ut=1000.0 + i))
            emitted += [a.seam_verb for a in actions]
        self.assertEqual(emitted.count("StopRecording"), 1)

    def test_the_arm_frame_still_counts_against_the_stop_budget(self):
        """MUTATION: reset phase_frames on the arm and the silence give-up
        drifts by a frame -- small, but it makes the budget mean something
        different from every other phase's."""
        st = fresh(stopFrames=3)
        for _ in range(8):
            st, _ = mlib.cl3_decide(st, snap(ut=1000.0))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertIn("never answered within 3 frames", st.flake_reason)


# ---------------------------------------------------------------------------
# 3. Phase transitions and give-ups: STOP -> RECORDER-IDLE -> REWIND.
# ---------------------------------------------------------------------------


class Cl3StopPhaseTests(unittest.TestCase):

    def test_stop_ok_goes_to_recorder_idle_and_probes(self):
        """StopRecording answering OK is a COMMANDED reading; the machine must go
        and OBSERVE the recorder state rather than proceed on the ack.

        MUTATION: enter CL3_REWIND from the STOP OK branch and this reds."""
        st = drive_to_stop_armed()
        out, actions = mlib.cl3_decide(st, seam(mlib.CL3_TAG_STOP, ut=1001.0))
        self.assertEqual(out.phase, mlib.CL3_RECORDER_IDLE)
        self.assertEqual(out.stop_result, "OK")
        self.assertEqual([(a.seam_verb, a.seam_tag) for a in actions],
                         [("RecordingState", mlib.r1_state_probe_tag(0))])

    def test_stop_error_is_a_named_giveup_that_names_the_gate(self):
        st = drive_to_stop_armed()
        out, _ = mlib.cl3_decide(
            st, seam(mlib.CL3_TAG_STOP, result="ERROR", ut=1001.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("StopRecording seam command returned ERROR", out.flake_reason)
        self.assertIn("recording-active", out.flake_reason)
        self.assertEqual(out.stop_result, "ERROR")

    def test_stop_timeout_is_distinctly_named(self):
        st = drive_to_stop_armed()
        out, _ = mlib.cl3_decide(
            st, seam(mlib.CL3_TAG_STOP, result="TIMEOUT", ut=1001.0))
        self.assertIn("returned TIMEOUT", out.flake_reason)

    def test_a_stale_rewind_token_cannot_satisfy_the_stop_phase(self):
        """THE stale-result fail-open. MUTATION: drop the tag check in
        _r1_seam_result and this reds -- STOP would advance on a token
        StopRecording never produced."""
        st = drive_to_stop_armed()
        out, _ = mlib.cl3_decide(st, seam(mlib.CL3_TAG_REWIND, ut=1001.0))
        self.assertEqual(out.phase, mlib.CL3_STOP)
        self.assertEqual(out.stop_result, "")
        self.assertFalse(out.done)


class Cl3RecorderIdleTests(unittest.TestCase):
    """The dispatcher's `recording-active` gate carried as an OBSERVED
    precondition. R1 flight 1 (2026-07-26) red exactly here."""

    def test_an_observed_idle_reading_commands_the_rewind_and_stamps_the_before(self):
        st = drive_to_idle()
        out, actions = mlib.cl3_decide(
            st, seam(mlib.r1_state_probe_tag(0), payload=(("recording", "false"),),
                     ut=1002.0, altitude=8000.0, situation="FLYING", body="Kerbin"))
        self.assertEqual(out.phase, mlib.CL3_REWIND)
        self.assertTrue(out.recorder_idle_observed)
        self.assertEqual(out.recorder_idle_reading, "false")
        # The stamp is taken on the frame the rewind is COMMANDED, so VERIFY
        # compares against the tightest possible "before".
        self.assertEqual(out.pre_rewind_ut, 1002.0)
        self.assertEqual(out.pre_rewind_altitude, 8000.0)
        self.assertEqual(out.pre_rewind_situation, "FLYING")
        self.assertEqual(out.pre_rewind_body, "Kerbin")
        self.assertEqual(len(actions), 1)
        self.assertEqual(actions[0].seam_verb, "InvokeRewind")
        self.assertEqual(actions[0].seam_tag, mlib.CL3_TAG_REWIND)
        self.assertEqual(actions[0].seam_args, (("rp", "rp_cl_root"), ("slot", "1")))

    def test_no_invoke_rewind_is_ever_emitted_before_an_idle_reading(self):
        """The precondition as a whole-run property rather than a per-branch one.

        MUTATION: move the rewind action to the STOP OK branch (an order-only
        fix with no observation) and this reds."""
        st = drive_to_idle()
        emitted = []
        st, actions = mlib.cl3_decide(
            st, seam(mlib.r1_state_probe_tag(0), payload=(("recording", "true"),),
                     ut=1002.0))
        emitted += [a.seam_verb for a in actions]
        self.assertNotIn("InvokeRewind", emitted)
        self.assertEqual(st.phase, mlib.CL3_RECORDER_IDLE)
        self.assertFalse(st.recorder_idle_observed)
        st, actions = mlib.cl3_decide(
            st, seam(mlib.r1_state_probe_tag(1), payload=(("recording", "false"),),
                     ut=1003.0))
        emitted += [a.seam_verb for a in actions]
        self.assertEqual(emitted.count("InvokeRewind"), 1)
        self.assertEqual(st.phase, mlib.CL3_REWIND)

    def test_each_probe_gets_a_distinct_tag(self):
        """MUTATION: make the re-probe reuse the current tag and this reds --
        every probe after the first would be a duplicate wire id the seam skips,
        and the poll would expire on a reply that was never coming."""
        st = drive_to_idle()
        tags = []
        for i in range(3):
            st, actions = mlib.cl3_decide(
                st, seam(mlib.r1_state_probe_tag(i),
                         payload=(("recording", "true"),), ut=1002.0 + i))
            tags += [a.seam_tag for a in actions]
        self.assertEqual(tags, ["state1", "state2", "state3"])
        self.assertEqual(len(set(tags)), len(tags))

    def test_a_recorder_that_never_goes_idle_is_a_named_giveup(self):
        """MUTATION: delete the `recording == "true"` branch's frame bound and
        this HANGS."""
        st = drive_to_idle(idleFrames=3)
        for i in range(12):
            st, _ = mlib.cl3_decide(
                st, seam(mlib.r1_state_probe_tag(st.state_probe),
                         payload=(("recording", "true"),), ut=1002.0 + i))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("still read recording=true", st.flake_reason)
        self.assertIn("recording-active", st.flake_reason)
        self.assertIn("autoRecordOnLaunch", st.flake_reason)

    def test_an_unreadable_recording_field_fails_closed(self):
        """An OK reply whose payload has no `recording` field must NOT be treated
        as idle. MUTATION: fall through to the rewind on an unreadable field and
        this reds -- an unverified gate is not permission to command an
        irreversible scene reload."""
        st = drive_to_idle()
        out, actions = mlib.cl3_decide(
            st, seam(mlib.r1_state_probe_tag(0), payload=(("tree", "t1"),), ut=1002.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("no readable `recording` field", out.flake_reason)
        self.assertEqual(actions, [])

    def test_a_seam_error_in_recorder_idle_is_a_named_giveup(self):
        st = drive_to_idle()
        out, _ = mlib.cl3_decide(
            st, seam(mlib.r1_state_probe_tag(0), result="ERROR",
                     payload=(("msg", "no-flight-scene"),), ut=1002.0))
        self.assertTrue(out.done)
        self.assertIn("RecordingState seam command returned ERROR", out.flake_reason)
        self.assertIn("Parsek's reason: no-flight-scene", out.flake_reason)

    def test_recorder_idle_silence_is_frame_bounded(self):
        st = drive_to_idle(idleFrames=3)
        for _ in range(8):
            st, _ = mlib.cl3_decide(st, snap(ut=1002.0))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertIn("never answered within 3 frames", st.flake_reason)

    def test_a_stale_stop_ok_cannot_satisfy_the_recorder_idle_phase(self):
        st = drive_to_idle()
        out, _ = mlib.cl3_decide(st, seam(mlib.CL3_TAG_STOP, ut=1002.0))
        self.assertEqual(out.phase, mlib.CL3_RECORDER_IDLE)
        self.assertFalse(out.recorder_idle_observed)
        self.assertFalse(out.done)


class Cl3RewindPhaseTests(unittest.TestCase):

    def test_rewind_ok_enters_verify_but_is_not_itself_the_terminal(self):
        """MUTATION: make the REWIND OK branch enter CL3_REWOUND directly and
        this reds. The whole point of VERIFY is that the seam's OK is a COMMANDED
        reading and must never be the terminal on its own."""
        st = drive_to_rewind()
        out, actions = mlib.cl3_decide(st, seam(mlib.CL3_TAG_REWIND, ut=1003.0))
        self.assertEqual(out.phase, mlib.CL3_VERIFY)
        self.assertEqual(out.rewind_result, "OK")
        self.assertFalse(out.done)
        self.assertIsNone(out.verdict)
        self.assertEqual(actions, [])

    def test_a_rejected_rewind_surfaces_parseks_own_reason(self):
        """DIAGNOSABILITY. MUTATION: drop the `msg` read and the give-up goes back
        to speculating -- which on R1 flight 1 sent the operator hunting the
        RewindPoint while the response carried `recording-active`."""
        st = drive_to_rewind()
        out, _ = mlib.cl3_decide(
            st, seam(mlib.CL3_TAG_REWIND, result="ERROR",
                     payload=(("msg", "recording-active"),), ut=1003.0))
        self.assertTrue(out.done)
        self.assertEqual(out.rewind_reject_reason, "recording-active")
        self.assertIn("Parsek's reason: recording-active", out.flake_reason)
        self.assertIn("rp=rp_cl_root slot=1", out.flake_reason)

    def test_a_percent_encoded_reason_is_decoded_in_the_giveup(self):
        st = drive_to_rewind()
        out, _ = mlib.cl3_decide(
            st, seam(mlib.CL3_TAG_REWIND, result="ERROR",
                     payload=(("msg", "refly-gate%20unknown-slot"),), ut=1003.0))
        self.assertIn("refly-gate unknown-slot", out.flake_reason)

    def test_a_reasonless_rejection_admits_it_rather_than_guessing(self):
        st = drive_to_rewind()
        out, _ = mlib.cl3_decide(
            st, seam(mlib.CL3_TAG_REWIND, result="TIMEOUT", ut=1003.0))
        self.assertIn("the response carried no msg= reason", out.flake_reason)

    def test_rewind_silence_is_frame_bounded(self):
        st = drive_to_rewind(rewindFrames=3)
        for _ in range(8):
            st, _ = mlib.cl3_decide(st, snap(ut=1003.0))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertIn("InvokeRewind seam command never answered within 3 frames",
                      st.flake_reason)

    def test_vessel_loss_is_suppressed_in_rewind_only(self):
        """The reload straddle legitimately destroys the active vessel.
        MUTATION: widen the suppression to VERIFY and the next cell reds -- a
        blanket fail-open."""
        st = drive_to_rewind()
        out, _ = mlib.cl3_decide(st, snap(ut=1003.0, vessel_lost=True))
        self.assertFalse(out.done)
        self.assertEqual(out.phase, mlib.CL3_REWIND)

    def test_loss_still_terminates_everywhere_else(self):
        """The suppression is ONE phase wide."""
        for st in (drive_to_stop_armed(), drive_to_idle(), drive_to_verify(),
                   drive_to_relaunch(), drive_to_loop_points()):
            out, _ = mlib.cl3_decide(st, snap(ut=500.0, vessel_lost=True))
            self.assertTrue(out.done, st.phase)
            self.assertEqual(out.verdict, mlib.MISSION_ASSERT_FAIL, st.phase)
            self.assertIn("vessel-lost", out.loss_reason)
            self.assertIn(st.phase, out.loss_reason)


# ---------------------------------------------------------------------------
# 4. THE OBSERVED VERIFY GATE. The load-bearing cell of the lane.
# ---------------------------------------------------------------------------


class Cl3ObservedVerifyTests(unittest.TestCase):

    def test_the_backward_clock_is_the_advance(self):
        st = drive_to_verify()
        out, actions = mlib.cl3_decide(
            st, snap(ut=402.0, altitude=100.0, situation="PRE_LAUNCH", body="Kerbin"))
        self.assertEqual(out.phase, mlib.CL3_REWOUND)
        self.assertAlmostEqual(out.ut_regression, 600.0)
        self.assertEqual(out.post_rewind_ut, 402.0)
        self.assertEqual(out.post_rewind_situation, "PRE_LAUNCH")
        # REWOUND is a WAYPOINT, not the terminal: a rewind that is never flown
        # again leaves the provisional empty and the merge tombstones nothing.
        # MUTATION: set `done` on REWOUND and this reds.
        self.assertFalse(out.done)
        self.assertIsNone(out.verdict)
        self.assertEqual([(a.kind, a.value) for a in actions],
                         [(mlib.ACTION_SET_THROTTLE, 1.0),
                          (mlib.ACTION_ACTIVATE_STAGE, None)])

    def test_a_forward_clock_never_satisfies_the_gate(self):
        """THE anti-commanded cell. MUTATION: replace the gate with
        `if state.rewind_result == "OK"` (i.e. trust the seam's own OK) and this
        reds -- the machine would call a rewind that never moved the clock
        done."""
        cur = drive_to_verify(verifyFrames=4)
        for i in range(10):
            cur, _ = mlib.cl3_decide(
                cur, snap(ut=2000.0 + i, altitude=8000.0, situation="FLYING"))
            if cur.done:
                break
        self.assertTrue(cur.done)
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)
        self.assertIn("never ran backward", cur.flake_reason)
        self.assertIn("COMMANDED reading", cur.flake_reason)

    def test_a_sub_floor_regression_never_satisfies_the_gate(self):
        """MUTATION: drop the floor (`regression > 0`) and this reds. A 0.5 s
        wobble is float noise, not a rewind."""
        cur = drive_to_verify(verifyFrames=3)
        for _ in range(8):
            cur, _ = mlib.cl3_decide(cur, snap(ut=1001.5, altitude=100.0))
            if cur.done:
                break
        self.assertTrue(cur.done)
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)
        self.assertNotIn(mlib.CL3_REWOUND, cur.phases_reached)

    def test_exactly_the_floor_satisfies_the_gate(self):
        st = drive_to_verify()
        out, _ = mlib.cl3_decide(st, snap(ut=997.0, altitude=100.0))
        self.assertEqual(out.phase, mlib.CL3_REWOUND)
        self.assertAlmostEqual(out.ut_regression, 5.0)

    def test_a_non_finite_clock_never_satisfies_the_gate(self):
        """Fail-closed on an unread channel: NaN must not compare its way into a
        pass."""
        cur = drive_to_verify(verifyFrames=2)
        for _ in range(6):
            cur, _ = mlib.cl3_decide(cur, snap(ut=float("nan"), altitude=100.0))
            if cur.done:
                break
        self.assertTrue(cur.done)
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)

    def test_verify_is_frame_bounded_not_game_time_bounded(self):
        """THE reason every phase here counts FRAMES. MUTATION: bound VERIFY with
        `snapshot.ut - phase_entry_ut > budget` and this HANGS (never
        terminates), because after a rewind that difference is negative
        forever."""
        cur = drive_to_verify(verifyFrames=5)
        for i in range(30):
            cur, _ = mlib.cl3_decide(cur, snap(ut=1001.9 - i * 0.01, altitude=100.0))
            if cur.done:
                break
        self.assertTrue(cur.done, "VERIFY must terminate on a FRAME budget")
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)


# ---------------------------------------------------------------------------
# 5. The re-fly: REWOUND -> RELAUNCH -> LOOP-POINTS -> LOOP-CLOSED.
# ---------------------------------------------------------------------------


class Cl3ReflyTests(unittest.TestCase):
    """Without this half the provisional stays EMPTY,
    `SupersedeCommit.ValidateSupersedeTarget` refuses to write supersede rows,
    and the merge tombstones nothing -- a green mission proving nothing. R1
    flight 2 (2026-07-26) is that exact shape."""

    def test_rewound_is_a_one_frame_waypoint_into_relaunch(self):
        st = drive_to_verify()
        st, _ = mlib.cl3_decide(st, snap(ut=402.0, altitude=100.0,
                                         situation="PRE_LAUNCH"))
        self.assertEqual(st.phase, mlib.CL3_REWOUND)
        out, actions = mlib.cl3_decide(st, snap(ut=403.0, altitude=100.0,
                                                situation="PRE_LAUNCH"))
        self.assertEqual(out.phase, mlib.CL3_RELAUNCH)
        self.assertEqual(actions, [])
        self.assertFalse(out.done)

    def test_the_relaunch_gate_is_measured_altitude_not_the_commanded_throttle(self):
        """MUTATION: advance RELAUNCH on `state.phase_frames > 0` (i.e. trust the
        throttle command) and this reds. Sending throttle is COMMANDED; climbing
        is OBSERVED."""
        st = drive_to_relaunch(post_alt=100.0)
        for _ in range(5):
            st, _ = mlib.cl3_decide(st, snap(ut=404.0, altitude=100.0,
                                             situation="PRE_LAUNCH"))
            self.assertEqual(st.phase, mlib.CL3_RELAUNCH)
            self.assertFalse(st.done)
        out, actions = mlib.cl3_decide(
            st, snap(ut=410.0, altitude=650.0, situation="FLYING"))
        self.assertEqual(out.phase, mlib.CL3_LOOP_POINTS)
        self.assertAlmostEqual(out.relaunch_altitude_gain, 550.0)
        self.assertEqual([(a.seam_verb, a.seam_tag) for a in actions],
                         [("RecordingState", "loop0")])

    def test_the_peak_gain_is_kept_not_the_latest_reading(self):
        """A craft that climbs and falls back still FLEW. MUTATION: replace the
        running max with the instantaneous gain and this reds."""
        st = drive_to_relaunch(post_alt=100.0, relaunchMinAltitudeGainMeters=400.0)
        st, _ = mlib.cl3_decide(st, snap(ut=405.0, altitude=350.0, situation="FLYING"))
        self.assertEqual(st.phase, mlib.CL3_RELAUNCH)
        self.assertAlmostEqual(st.relaunch_altitude_gain, 250.0)
        out, _ = mlib.cl3_decide(st, snap(ut=406.0, altitude=200.0, situation="FLYING"))
        self.assertAlmostEqual(out.relaunch_altitude_gain, 250.0)

    def test_a_sub_floor_climb_is_a_named_giveup_that_names_the_consequence(self):
        st = drive_to_relaunch(post_alt=100.0, relaunchFrames=3)
        for _ in range(8):
            st, _ = mlib.cl3_decide(st, snap(ut=404.0, altitude=150.0,
                                             situation="FLYING"))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("never climbed", st.flake_reason)
        self.assertIn("empty provisional", st.flake_reason)
        self.assertIn("tombstone nothing", st.flake_reason)

    def test_flying_is_not_enough_the_flight_must_be_recorded(self):
        """The craft climbed, but no recording received it -> the merge would
        refuse supersede rows. This MUST NOT reach the terminal.

        MUTATION: advance LOOP-POINTS on any OK reply (drop the `points > 0`
        test) and this reds."""
        cur = drive_to_loop_points()
        for i in range(80):
            cur, _ = mlib.cl3_decide(
                cur, seam(mlib.r1_loop_probe_tag(cur.loop_probe),
                          payload=(("points", "0"),), ut=411.0 + i, altitude=700.0))
            if cur.done:
                break
        self.assertTrue(cur.done)
        self.assertEqual(cur.verdict, mlib.MISSION_FLAKE)
        self.assertNotIn(mlib.CL3_LOOP_CLOSED, cur.phases_reached)
        self.assertIn("points=0", cur.flake_reason)
        self.assertIn("empty Points", cur.flake_reason)

    def test_recorded_points_close_the_loop(self):
        st = drive_to_loop_closed(points=137)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(st.phase, mlib.CL3_LOOP_CLOSED)
        self.assertEqual(st.loop_points_read, 137)
        self.assertEqual(st.phases_reached, mlib.CL3_PHASES)

    def test_an_unreadable_points_field_fails_closed(self):
        """MUTATION: treat an unparseable `points` as 0-and-keep-probing (or as
        success) and this reds. An unread count is not evidence of a
        recording."""
        st = drive_to_loop_points()
        out, _ = mlib.cl3_decide(
            st, seam(mlib.r1_loop_probe_tag(0), payload=(("tree", "t1"),), ut=411.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("no readable `points` field", out.flake_reason)
        self.assertEqual(out.loop_points_read, -1)

    def test_loop_probes_use_their_own_tag_family(self):
        st = drive_to_loop_points()
        tags = []
        for i in range(3):
            st, actions = mlib.cl3_decide(
                st, seam(mlib.r1_loop_probe_tag(i), payload=(("points", "0"),),
                         ut=411.0 + i, altitude=700.0))
            tags += [a.seam_tag for a in actions]
        self.assertEqual(tags, ["loop1", "loop2", "loop3"])

    def test_a_seam_error_in_loop_points_is_a_named_giveup(self):
        st = drive_to_loop_points()
        out, _ = mlib.cl3_decide(
            st, seam(mlib.r1_loop_probe_tag(0), result="ERROR", ut=411.0))
        self.assertTrue(out.done)
        self.assertIn("post-rewind point count could not be observed",
                      out.flake_reason)

    def test_loop_points_silence_is_frame_bounded(self):
        st = drive_to_loop_points()
        st = replace(st, params=replace(st.params, loop_points_frames=3))
        for _ in range(8):
            st, _ = mlib.cl3_decide(st, snap(ut=411.0, altitude=700.0))
            if st.done:
                break
        self.assertTrue(st.done)
        self.assertIn("RecordingState seam command never answered within 3 frames",
                      st.flake_reason)

    def test_the_recorded_tree_evidence_is_carried(self):
        """R1's flight-3 diagnosis (the points landed in a DIFFERENT tree) must
        reach the result JSON rather than only KSP.log."""
        st = drive_to_loop_points()
        out, _ = mlib.cl3_decide(
            st, seam(mlib.r1_loop_probe_tag(0),
                     payload=(("points", "24"), ("tree", "820de77e")),
                     ut=411.0, altitude=700.0, situation="FLYING"))
        self.assertEqual(out.loop_recorded_tree, "820de77e")


class Cl3TerminalTests(unittest.TestCase):

    def test_a_terminal_machine_ignores_further_frames(self):
        st = drive_to_loop_closed()
        out, actions = mlib.cl3_decide(st, snap(ut=999999.0, vessel_lost=True))
        self.assertEqual(out, st)
        self.assertEqual(actions, [])

    def test_an_unknown_phase_is_a_named_giveup_not_a_silent_pass(self):
        """The fall-through. MUTATION: `return state, []` at the end of
        cl3_decide and a typo'd phase name becomes a silent hang."""
        st = replace(drive_to_verify(), phase="NOT-A-PHASE")
        out, _ = mlib.cl3_decide(st, snap(ut=402.0))
        self.assertTrue(out.done)
        self.assertEqual(out.verdict, mlib.MISSION_FLAKE)
        self.assertIn("unreachable machine phase", out.flake_reason)


# ---------------------------------------------------------------------------
# 6. The assertion rows.
# ---------------------------------------------------------------------------


class Cl3AssertionTests(unittest.TestCase):

    def _flown(self):
        return cl3_params(), drive_to_loop_closed()

    def test_the_full_cycle_meets_every_row(self):
        params, st = self._flown()
        rows = mlib.evaluate_cl3_assertions([], params, st)
        self.assertTrue(mlib.all_assertions_met(rows),
                        [r.name for r in rows if not r.met])
        self.assertEqual([r.name for r in rows],
                         ["recorderIdleBeforeRewind", "clockRewound",
                          "postRewindFlightObserved",
                          "postRewindFlightRecordedSomewhere",
                          "rewindSeamAccepted"])

    def test_the_three_dropped_r1_rows_are_gone_not_faked(self):
        """R1's `reachedOrbitBeforeRewind` / `treeCommittedBeforeRewind` judged
        legs this lane does not have, and `vesselStateChanged` judged a
        world-state contrast this lane's fixture cannot produce. A stand-in row
        would be claiming evidence the run does not produce.

        MUTATION: re-add any of the three (met=True by construction) and this
        reds."""
        params, st = self._flown()
        names = {r.name for r in mlib.evaluate_cl3_assertions([], params, st)}
        self.assertNotIn("reachedOrbitBeforeRewind", names)
        self.assertNotIn("treeCommittedBeforeRewind", names)
        self.assertNotIn("vesselStateChanged", names)

    def test_the_commanded_row_is_last_and_never_alone(self):
        params, st = self._flown()
        rows = mlib.evaluate_cl3_assertions([], params, st)
        self.assertEqual(rows[-1].name, "rewindSeamAccepted")
        self.assertEqual(rows[-1].detail["channel"], "commanded")
        for row in rows[:-1]:
            self.assertEqual(row.detail["channel"], "observed", row.name)

    def test_every_row_carries_a_channel_label(self):
        params, st = self._flown()
        for row in mlib.evaluate_cl3_assertions([], params, st):
            self.assertIn(row.detail.get("channel"), ("observed", "commanded"),
                          "%s has no channel label" % row.name)

    def test_a_commanded_ok_alone_does_not_meet_the_assertions(self):
        """THE anti-vacuity cell. A state where InvokeRewind returned OK but no
        backward clock was ever observed must NOT evaluate green."""
        params, st = self._flown()
        st = replace(st, ut_regression=float("nan"),
                     post_rewind_ut=float("nan"),
                     post_rewind_altitude=float("nan"),
                     post_rewind_situation="")
        rows = mlib.evaluate_cl3_assertions([], params, st)
        self.assertFalse(mlib.all_assertions_met(rows))
        unmet = {r.name for r in rows if not r.met}
        self.assertIn("clockRewound", unmet)
        # The commanded row is still met -- which is exactly why it can never be
        # the only row.
        self.assertTrue({r.name: r for r in rows}["rewindSeamAccepted"].met)

    def test_a_rewind_without_a_refly_does_not_evaluate_green(self):
        """The R1-flight-2 shape, at row level: rewind, then teardown. The rewind
        half reports MET so the diagnosis is unambiguous; the re-fly half does
        not.

        MUTATION: drop either re-fly row from evaluate_cl3_assertions and this
        reds."""
        params = cl3_params()
        st = drive_to_verify()
        st, _ = mlib.cl3_decide(st, snap(ut=402.0, altitude=100.0,
                                         situation="PRE_LAUNCH"))
        rows = mlib.evaluate_cl3_assertions([], params, st)
        self.assertFalse(mlib.all_assertions_met(rows))
        unmet = {r.name for r in rows if not r.met}
        self.assertIn("postRewindFlightObserved", unmet)
        self.assertIn("postRewindFlightRecordedSomewhere", unmet)
        met = {r.name for r in rows if r.met}
        self.assertIn("clockRewound", met)

    def test_the_recorder_idle_row_carries_the_read_value_not_the_stop_verbs_ok(self):
        """MUTATION: set `idle_met` from `stop_result == "OK"` and this reds. The
        StopRecording verb's OK is a COMMANDED reading; the row's evidence is the
        `recording` field READ back off RecordingState."""
        params, st = self._flown()
        row = {r.name: r for r in mlib.evaluate_cl3_assertions([], params, st)}[
            "recorderIdleBeforeRewind"]
        self.assertEqual(row.value, "false")
        self.assertEqual(row.detail["stopSeamResult"], "OK")
        blind = replace(st, recorder_idle_observed=False, recorder_idle_reading="")
        blind_row = {r.name: r
                     for r in mlib.evaluate_cl3_assertions([], params, blind)}[
                         "recorderIdleBeforeRewind"]
        self.assertFalse(blind_row.met)
        self.assertEqual(blind_row.detail["stopSeamResult"], "OK")

    def test_the_world_state_readings_survive_as_evidence_on_the_clock_row(self):
        """The removed `vesselStateChanged` row took a GATE away, not the
        MEASUREMENTS. Pre/post altitude + situation still reach the result JSON
        on `clockRewound`, so the arming follow-up (and any human reading a run)
        still sees the pair.

        MUTATION: drop the four evidence keys from the `clockRewound` detail and
        this reds."""
        params, st = self._flown()
        st = replace(st, pre_rewind_altitude=74.58, post_rewind_altitude=74.58,
                     pre_rewind_situation="PRE_LAUNCH",
                     post_rewind_situation="PRE_LAUNCH")
        row = {r.name: r for r in mlib.evaluate_cl3_assertions([], params, st)}[
            "clockRewound"]
        self.assertEqual(74.58, row.detail["preAltitude"])
        self.assertEqual(74.58, row.detail["postAltitude"])
        self.assertEqual("PRE_LAUNCH", row.detail["preSituation"])
        self.assertEqual("PRE_LAUNCH", row.detail["postSituation"])
        # ...and the evidence is LABELLED non-gating, so nobody reads the pair as
        # a satisfied assertion.
        self.assertIn("worldStateEvidenceIsNotGated", row.detail)
        # The row it rides is still judged by the CLOCK alone: identical world
        # readings do not stop it being met.
        self.assertTrue(row.met)

    def test_identical_world_readings_no_longer_red_the_mission(self):
        """THE REGRESSION CELL FOR THE FIX. The committed fixture produces
        identical pre/post world readings BY CONSTRUCTION (the RP slot vessel is
        a `CreateCopy()` of the host save's active vessel, and
        `StampVesselIdentity` touches no coordinate). While `vesselStateChanged`
        was a row, that made the mission permanently unmet -- and because
        `AnswerMergeDialog` is world-mutating, the default
        `skipTailOnUnmetMission` then SKIPPED the merge, so `CommitTombstones`
        never ran and the scenario could not reach its own subject.

        MUTATION: re-add the row and this reds."""
        params, st = self._flown()
        st = replace(st, pre_rewind_altitude=74.582755251089111,
                     post_rewind_altitude=74.582755251089111,
                     pre_rewind_situation="PRE_LAUNCH",
                     post_rewind_situation="PRE_LAUNCH")
        rows = mlib.evaluate_cl3_assertions([], params, st)
        self.assertTrue(mlib.all_assertions_met(rows),
                        [r.name for r in rows if not r.met])

    def test_the_refly_row_fails_on_each_conjunct_independently(self):
        """DIRECT-STATE cells for the conjuncts the happy path always satisfies
        (the 2026-07-26 R1 review's lesson: a row whose every conjunct survives
        mutation is covering nothing)."""
        params, st = self._flown()
        rows = lambda s: {r.name: r
                          for r in mlib.evaluate_cl3_assertions([], params, s)}
        self.assertTrue(rows(st)["postRewindFlightObserved"].met)
        # (a) never measured. NaN alone does not exercise `_is_finite`
        # (`nan >= floor` is already False); INFINITY does, since it compares
        # True against any floor.
        self.assertFalse(rows(replace(st, relaunch_altitude_gain=float("nan")))[
            "postRewindFlightObserved"].met)
        self.assertFalse(rows(replace(st, relaunch_altitude_gain=float("inf")))[
            "postRewindFlightObserved"].met,
            "an infinite altitude reading is a broken channel, not a climb")
        # (b) measured but below the floor
        below = replace(st,
                        relaunch_altitude_gain=params.relaunch_min_altitude_gain - 1.0)
        self.assertFalse(rows(below)["postRewindFlightObserved"].met)
        # (c) a NEGATIVE gain (the craft sank) never satisfies a climb row
        self.assertFalse(rows(replace(st, relaunch_altitude_gain=-500.0))[
            "postRewindFlightObserved"].met)
        # (d) gain recorded but the machine never reached LOOP-POINTS
        early = replace(st, phases_reached=(mlib.CL3_STOP, mlib.CL3_RELAUNCH))
        self.assertFalse(rows(early)["postRewindFlightObserved"].met)

    def test_the_unread_points_sentinel_is_minus_one_not_zero(self):
        """The row demands a POSITIVE read. -1 (never read) and 0 (read, empty)
        are different diagnoses and neither is evidence of a recording. The last
        two cases are CONSTRUCTED (the machine only enters LOOP-CLOSED on
        `points > 0`) so a future path that skips the read inherits a proven
        guard. MUTATION: `points_read >= 0` and this reds on the zero case."""
        params = cl3_params()
        st = drive_to_relaunch()
        self.assertEqual(st.loop_points_read, -1)
        rows = {r.name: r for r in mlib.evaluate_cl3_assertions([], params, st)}
        self.assertFalse(rows["postRewindFlightRecordedSomewhere"].met)
        self.assertEqual(rows["postRewindFlightRecordedSomewhere"].value, -1)
        closed = drive_to_loop_closed(points=7)
        for bogus in (0, -1):
            blind = replace(closed, loop_points_read=bogus)
            row = {r.name: r
                   for r in mlib.evaluate_cl3_assertions([], params, blind)}[
                       "postRewindFlightRecordedSomewhere"]
            self.assertFalse(row.met, "points=%d must not satisfy the row" % bogus)

    def test_the_recorded_somewhere_row_states_what_it_cannot_prove(self):
        """`RecordingState.points` is the LIVE recorder's count, not the re-fly
        provisional's. The row must SAY so in the result JSON."""
        params, st = self._flown()
        row = {r.name: r for r in mlib.evaluate_cl3_assertions([], params, st)}[
            "postRewindFlightRecordedSomewhere"]
        self.assertIn("doesNotProve", row.detail)
        self.assertIn("provisional", row.detail["doesNotProve"])

    def test_the_reject_reason_rides_the_commanded_row(self):
        params = cl3_params()
        st = drive_to_rewind()
        st, _ = mlib.cl3_decide(
            st, seam(mlib.CL3_TAG_REWIND, result="ERROR",
                     payload=(("msg", "recording-active"),), ut=1003.0))
        row = {r.name: r for r in mlib.evaluate_cl3_assertions([], params, st)}[
            "rewindSeamAccepted"]
        self.assertFalse(row.met)
        self.assertEqual(row.detail["rejectReason"], "recording-active")
        self.assertEqual(row.detail["rewindPointId"], "rp_cl_root")
        self.assertEqual(row.detail["rewindSlot"], 1)

    def test_rows_serialize_without_nan(self):
        params, st = self._flown()
        st = replace(st, ut_regression=float("nan"))
        for row in mlib.evaluate_cl3_assertions([], params, st):
            for key, value in row.to_dict().items():
                self.assertFalse(
                    isinstance(value, float) and not math.isfinite(value),
                    "%s.%s is non-finite" % (row.name, key))

    def test_a_named_flake_reaches_the_mission_verdict(self):
        params = cl3_params(rewindPointId="")
        st = mlib.cl3_initial_state(params)
        st, _ = mlib.cl3_decide(st, snap(ut=1000.0))
        verdict, reason = mlib.resolve_flight_verdict(
            st, mlib.evaluate_cl3_assertions([], params, st))
        self.assertEqual(verdict, mlib.MISSION_FLAKE)
        self.assertIn("rewind target unresolved", reason)


# ---------------------------------------------------------------------------
# 7. Params + the declared schema, checked through the harness's own validator.
# ---------------------------------------------------------------------------


class Cl3ParamsTests(unittest.TestCase):

    # Every knob CL-3 keeps, with the R1 param name it reuses. R1's spellings are
    # deliberate: the phases are the same phases, so a value tuned on one lane
    # must mean the same thing on the other.
    # `min_altitude_change` is NOT here: R1 keeps it to threshold
    # `vesselStateChanged`, a row this lane does not carry (see
    # `mlib.evaluate_cl3_assertions`). Every knob that DOES survive keeps R1's
    # spelling.
    _SHARED = ("rewind_point_id", "rewind_slot", "stop_frames", "idle_frames",
               "rewind_frames", "verify_frames", "min_ut_regression",
               "relaunch_throttle",
               "relaunch_min_altitude_gain", "relaunch_frames",
               "loop_points_frames")

    def test_unread_sentinels_are_the_defaults(self):
        p = mlib.cl3_params_from_dict({})
        self.assertEqual(p.rewind_point_id, "")
        self.assertEqual(p.rewind_slot, -1)

    def test_none_params_parse_to_the_sentinels_rather_than_raising(self):
        p = mlib.cl3_params_from_dict(None)
        self.assertEqual(p.rewind_point_id, "")
        self.assertEqual(p.rewind_slot, -1)

    def test_every_shared_knob_parses_identically_to_r1(self):
        """MUTATION: rename a key (`stopFrames` -> `cl3StopFrames`) or change a
        default and this reds. Two lanes bounding the same phase with the same
        semantics under different key names is a trap for whoever tunes one and
        assumes the other followed."""
        tuned = dict(CL3_MISSION_PARAMS,
                     stopFrames=11, idleFrames=12, rewindFrames=13,
                     verifyFrames=14, minUtRegressionSeconds=15.0,
                     relaunchThrottle=0.75,
                     relaunchMinAltitudeGainMeters=17.0, relaunchFrames=18,
                     loopPointsFrames=19)
        cl3p = mlib.cl3_params_from_dict(tuned)
        r1p = mlib.r1_params_from_dict(tuned)
        for name in self._SHARED:
            self.assertEqual(getattr(cl3p, name), getattr(r1p, name), name)
        # ... and the defaults agree too, not merely the parsed values.
        cl3d, r1d = mlib.cl3_params_from_dict({}), mlib.r1_params_from_dict({})
        for name in self._SHARED:
            self.assertEqual(getattr(cl3d, name), getattr(r1d, name), name)

    def test_the_param_surface_is_exactly_those_eleven_knobs(self):
        """A knob nobody declares is a knob nobody can tune from a spec -- and,
        the direction this lane learned the hard way, a knob nobody READS is a
        knob a future author tunes believing it gates something."""
        self.assertEqual({f.name for f in fields(mlib.Cl3Params)}, set(self._SHARED))
        self.assertNotIn("min_altitude_change",
                         {f.name for f in fields(mlib.Cl3Params)})


class Cl3SchemaTests(unittest.TestCase):
    """The declared schema is what `run.py` injects into `hlib.validate_spec`, so
    it is checked THROUGH the real validator, never restated."""

    @classmethod
    def setUpClass(cls):
        with open(SCHEMA_PATH, "rb") as fh:
            cls.schema = tomllib.load(fh)
        cls.declared = cls.schema["params"]

    def test_the_shell_and_the_schema_sit_side_by_side_under_the_mission_name(self):
        """`run.py::resolve_mission_schemas` registers a mission purely by the
        existence of `<name>.py` + `<name>.schema.toml`, and hlib rejects a
        spec naming a mission with no declared schema."""
        self.assertTrue(os.path.isfile(SHELL_PATH))
        self.assertTrue(os.path.isfile(SCHEMA_PATH))
        self.assertEqual(
            os.path.basename(SHELL_PATH), "%s.py" % cl3.MISSION_NAME)
        self.assertEqual(
            os.path.basename(SCHEMA_PATH), "%s.schema.toml" % cl3.MISSION_NAME)

    def test_only_the_rewind_target_is_required(self):
        """Everything else is a defaulted budget / threshold: a spec author must
        name WHAT to rewind and nothing else."""
        required = {k for k, v in self.declared.items() if v.get("required")}
        self.assertEqual(required, {"rewindPointId", "rewindSlot"})

    def test_the_declared_keys_are_exactly_the_keys_the_machine_reads(self):
        """MUTATION: declare a key `cl3_params_from_dict` never reads (or read
        one the schema never declares) and this reds. A declared-but-unread key
        is a knob a spec author sets and nothing honours."""
        probe = {"rewindPointId": "rp_probe", "rewindSlot": 3,
                 "stopFrames": 41, "idleFrames": 42, "rewindFrames": 43,
                 "verifyFrames": 44, "minUtRegressionSeconds": 45.5,
                 "relaunchThrottle": 0.5,
                 "relaunchMinAltitudeGainMeters": 47.5, "relaunchFrames": 48,
                 "loopPointsFrames": 49}
        self.assertEqual(set(self.declared), set(probe))
        # Each declared key actually LANDS somewhere: parsing the probe dict
        # must differ from the defaults in every field.
        parsed, default = (mlib.cl3_params_from_dict(probe),
                           mlib.cl3_params_from_dict({}))
        for f in fields(mlib.Cl3Params):
            self.assertNotEqual(getattr(parsed, f.name), getattr(default, f.name),
                                "%s did not move when its spec key was set" % f.name)

    def test_the_dead_altitude_knob_is_undeclared_and_this_cell_is_the_only_guard(self):
        """THE GUARD THAT REPLACES THE OLD VACUITY CELLS. `vesselStateChanged` is
        gone, so `minAltitudeChangeMeters` reads nothing and is undeclared here.

        AND THE HONEST LIMIT, MEASURED RATHER THAN ASSUMED: dropping it from the
        schema does NOT make setting it an error.
        `hlib._validate_mission_params` iterates the DECLARED params only, so an
        UNDECLARED key in a spec is silently IGNORED - it does not reject, and it
        does not even warn. So the schema is not the guard; THIS CELL IS. A
        future author who re-adds `minAltitudeChangeMeters` to the spec gets a
        line that looks like a threshold, is honoured by nothing, and passes
        `validate_spec` - which is precisely the failure mode the removal exists
        to prevent, so it must red HERE.

        MUTATION: re-declare the key in the schema, or set it in the spec, and
        this reds."""
        self.assertNotIn("minAltitudeChangeMeters", self.declared)
        spec = _read_spec()
        self.assertNotIn("minAltitudeChangeMeters",
                         spec["driver"]["missionParams"])

        # The silent-ignore, DEMONSTRATED through the real validator with the
        # real injected schema rather than asserted in prose - this is why the
        # cell above cannot be delegated to `validate_spec`.
        with open(REGISTRY_PATH, "rb") as fh:
            registry = tomllib.load(fh)
        schemas, shell_errors = harness_run.resolve_mission_schemas(spec)
        self.assertEqual([], list(shell_errors))
        bad = copy.deepcopy(spec)
        bad["driver"]["missionParams"]["minAltitudeChangeMeters"] = 100.0
        res = hlib.validate_spec(bad, registry, mission_schemas=schemas)
        self.assertTrue(res.ok,
                        "if undeclared mission params now REJECT, this cell's "
                        "premise changed - rewrite it rather than deleting it")
        # ...and the machine would ignore it too: the parsed params carry no such
        # field, so nothing could honour the value even if a spec set it.
        self.assertNotIn(
            "min_altitude_change",
            {f.name for f in fields(mlib.Cl3Params)})

    def test_the_ascent_and_commit_keys_are_absent(self):
        """The CUT, pinned from the schema side. MUTATION: paste R1's ascent
        block back in and this reds -- a required key no machine reads would
        reject every otherwise-valid CL-3 spec."""
        for gone in ("targetApoapsisMeters", "targetPeriapsisMeters",
                     "apoErrorMeters", "periErrorMeters", "eccentricityMax",
                     "inclinationErrorDeg", "ascentTimeoutSeconds",
                     "circularizeTimeoutSeconds", "launchSiteLatitude",
                     "frozenTelemetrySamples", "commitFrames"):
            self.assertNotIn(gone, self.declared)

    def test_a_spec_shaped_params_block_validates_clean(self):
        block = dict(CL3_MISSION_PARAMS, stopFrames=40, idleFrames=40,
                     rewindFrames=40, verifyFrames=40, relaunchThrottle=1.0,
                     relaunchMinAltitudeGainMeters=100.0, relaunchFrames=240,
                     loopPointsFrames=40)
        self.assertEqual(hlib._validate_mission_params(block, self.schema), [])

    def test_a_missing_rewind_target_is_rejected_by_the_validator(self):
        errs = hlib._validate_mission_params({"rewindSlot": 1}, self.schema)
        self.assertTrue(any("rewindPointId" in e for e in errs), errs)

    def test_a_negative_slot_and_a_non_string_id_are_rejected(self):
        errs = hlib._validate_mission_params(
            {"rewindPointId": "rp_cl_root", "rewindSlot": -1}, self.schema)
        self.assertTrue(any("rewindSlot" in e for e in errs), errs)
        errs = hlib._validate_mission_params(
            {"rewindPointId": 7, "rewindSlot": 1}, self.schema)
        self.assertTrue(any("rewindPointId" in e for e in errs), errs)

    def test_an_out_of_range_throttle_is_rejected(self):
        errs = hlib._validate_mission_params(
            dict(CL3_MISSION_PARAMS, relaunchThrottle=1.5), self.schema)
        self.assertTrue(any("relaunchThrottle" in e for e in errs), errs)

    def test_a_zero_relaunch_floor_is_rejected(self):
        """The floor exists so a craft on the pad with a dead engine cannot pass.
        A floor of 0 would make the OBSERVED climb gate vacuous."""
        errs = hlib._validate_mission_params(
            dict(CL3_MISSION_PARAMS, relaunchMinAltitudeGainMeters=0.0),
            self.schema)
        self.assertTrue(any("relaunchMinAltitudeGainMeters" in e for e in errs), errs)


# ---------------------------------------------------------------------------
# 8. The shell, driven end to end over the fake seam.
# ---------------------------------------------------------------------------


class Cl3ShellTests(unittest.TestCase):
    """Guards the SHELL wiring (build_state / decide / evaluate / the MissionSpec
    knobs) rather than the machine, which the cells above own."""

    def _cycle_frames(self, idle_payload=(("recording", "false"),)):
        """The machine arms StopRecording on frame 0; these are the replies."""
        return [
            snap(ut=1000.0, altitude=8000.0, situation="FLYING", body="Kerbin"),
            seam(mlib.CL3_TAG_STOP, ut=1001.0, altitude=8000.0,
                 situation="FLYING", body="Kerbin"),
            seam(mlib.r1_state_probe_tag(0), payload=idle_payload, ut=1002.0,
                 altitude=8000.0, situation="FLYING", body="Kerbin"),
            seam(mlib.CL3_TAG_REWIND, ut=1003.0, altitude=8000.0,
                 situation="FLYING", body="Kerbin"),
        ]

    def _refly_frames(self, points="120"):
        return [
            # VERIFY: the clock ran BACKWARD and the craft is back on the pad.
            snap(ut=402.0, altitude=100.0, situation="PRE_LAUNCH", body="Kerbin"),
            # REWOUND waypoint (the re-fly was commanded on entry).
            snap(ut=403.0, altitude=100.0, situation="PRE_LAUNCH", body="Kerbin"),
            # RELAUNCH: the craft actually climbs.
            snap(ut=410.0, altitude=900.0, situation="FLYING", body="Kerbin"),
            # LOOP-POINTS: the climb was RECORDED.
            seam(mlib.r1_loop_probe_tag(0), payload=(("points", points),),
                 ut=411.0, altitude=1000.0, situation="FLYING", body="Kerbin"),
        ]

    def test_happy_path_writes_mission_ok_with_the_observed_rewind(self):
        frames = self._cycle_frames() + self._refly_frames()
        control = FakeMissionControl(frames)
        code, result = run(cl3.SPEC, dict(CL3_MISSION_PARAMS), control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["mission"], "cl3_refly_crew_tombstone")
        self.assertEqual(result["phasesReached"][-1], mlib.CL3_LOOP_CLOSED)
        # No ascent is ever flown: the FIRST action of the whole mission is the
        # StopRecording that opens the dispatcher's gate.
        self.assertEqual(control.actions[0].seam_verb, "StopRecording")
        seam_actions = [a for a in control.actions
                        if a.kind == mlib.ACTION_PARSEK_SEAM_COMMAND]
        self.assertEqual([(a.seam_verb, a.seam_tag) for a in seam_actions],
                         [("StopRecording", mlib.CL3_TAG_STOP),
                          ("RecordingState", mlib.r1_state_probe_tag(0)),
                          ("InvokeRewind", mlib.CL3_TAG_REWIND),
                          ("RecordingState", mlib.r1_loop_probe_tag(0))])
        # FOUR seam commands, four DISTINCT tags: shared tags would be duplicate
        # wire ids the C# seam silently skips.
        self.assertEqual(len({a.seam_tag for a in seam_actions}), 4)
        self.assertEqual(seam_actions[2].seam_args,
                         (("rp", "rp_cl_root"), ("slot", "1")))
        # No MechJeb action is ever issued on this lane.
        self.assertFalse([a for a in control.actions
                          if str(a.kind).startswith("mj_")], control.actions)
        # The re-fly was actually commanded on the rewound craft.
        kinds = [a.kind for a in control.actions]
        self.assertIn(mlib.ACTION_SET_THROTTLE, kinds)
        self.assertIn(mlib.ACTION_ACTIVATE_STAGE, kinds)
        rows = {a["name"]: a for a in result["assertions"]}
        self.assertTrue(all(a["met"] for a in result["assertions"]),
                        result["assertions"])
        self.assertGreater(rows["clockRewound"]["value"], 5.0)
        self.assertEqual(rows["recorderIdleBeforeRewind"]["value"], "false")
        self.assertEqual(rows["postRewindFlightRecordedSomewhere"]["value"], 120)

    def test_a_rewind_with_no_refly_is_not_mission_ok(self):
        """THE R1-FLIGHT-2 SHAPE end to end: rewind, then never fly. The merge
        this mission feeds would refuse to write supersede rows and tombstone
        nothing, so the mission must not call it a closed loop."""
        frames = self._cycle_frames() + [
            snap(ut=402.0, altitude=100.0, situation="PRE_LAUNCH", body="Kerbin"),
        ]
        frames += [snap(ut=403.0 + i, altitude=100.0, situation="PRE_LAUNCH",
                        body="Kerbin") for i in range(300)]
        control = FakeMissionControl(frames)
        code, result = run(cl3.SPEC, dict(CL3_MISSION_PARAMS), control)
        self.assertNotEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertIn("never climbed", result["reason"])
        self.assertNotIn(mlib.CL3_LOOP_CLOSED, result["phasesReached"])
        self.assertNotEqual(code, 0)
        rows = {a["name"]: a for a in result["assertions"]}
        self.assertTrue(rows["clockRewound"]["met"])
        self.assertFalse(rows["postRewindFlightObserved"]["met"])

    def test_the_rewind_is_never_commanded_while_the_recorder_reads_live(self):
        """THE R1-FLIGHT-1 REGRESSION at shell level, and it is sharper on this
        lane: CL-3 runs with autoRecordOnLaunch ON, so a recorder really can be
        live when the save finishes loading."""
        frames = self._cycle_frames(idle_payload=(("recording", "true"),))
        frames += [seam(mlib.r1_state_probe_tag(i),
                        payload=(("recording", "true"),), ut=1003.0 + i,
                        altitude=8000.0, situation="FLYING")
                   for i in range(1, 60)]
        control = FakeMissionControl(frames)
        _code, result = run(cl3.SPEC, dict(CL3_MISSION_PARAMS), control)
        self.assertEqual(result["verdict"], mlib.MISSION_FLAKE, result)
        self.assertIn("still read recording=true", result["reason"])
        verbs = [a.seam_verb for a in control.actions
                 if a.kind == mlib.ACTION_PARSEK_SEAM_COMMAND]
        self.assertNotIn("InvokeRewind", verbs)
        self.assertEqual(result["phasesReached"][-1], mlib.CL3_RECORDER_IDLE)

    def test_a_seam_ok_with_no_backward_clock_is_not_mission_ok(self):
        """THE anti-vacuity shell cell: InvokeRewind answers OK and the clock
        keeps going FORWARD."""
        frames = self._cycle_frames()
        frames += [snap(ut=1004.0 + i, altitude=8000.0, situation="FLYING")
                   for i in range(60)]
        control = FakeMissionControl(frames)
        code, result = run(cl3.SPEC, dict(CL3_MISSION_PARAMS), control)
        self.assertEqual(result["verdict"], mlib.MISSION_FLAKE, result)
        self.assertIn("never ran backward", result["reason"])
        self.assertNotEqual(code, 0)

    def test_an_unresolved_target_flakes_before_any_action_is_performed(self):
        params = dict(CL3_MISSION_PARAMS, rewindPointId="")
        control = FakeMissionControl(self._cycle_frames())
        code, result = run(cl3.SPEC, params, control)
        self.assertEqual(result["verdict"], mlib.MISSION_FLAKE, result)
        self.assertIn("rewind target unresolved", result["reason"])
        self.assertEqual(control.actions, [],
                         "the recorder must not be stopped once the target is "
                         "unresolvable")
        self.assertNotEqual(code, 0)

    def test_spec_knobs_are_the_one_x_throughout_contract(self):
        """R1 inherits B2's warp permissions because it flies a B2 ascent. CL-3
        flies no ascent and no coast; the ONE leg it flies is the one whose
        RECORDING has to be clean for the merge to mean anything, so a warped
        re-fly is a corrupted provisional.

        MUTATION: copy R1's allow_rails_warp=True / max_physics_warp=4.0 here and
        this reds."""
        self.assertFalse(cl3.SPEC.allow_rails_warp)
        self.assertEqual(cl3.SPEC.max_physics_warp, 0.0)
        # settle_frames 0: the frames after the terminal straddle a just-reloaded
        # scene, and every assertion is machine-carried evidence anyway.
        self.assertEqual(cl3.SPEC.settle_frames, 0)
        self.assertEqual(cl3.SPEC.name, "cl3_refly_crew_tombstone")

    def test_the_shell_delegates_to_the_pure_machine(self):
        """MUTATION: hand-roll a transition in the shell and this reds."""
        st = cl3.build_state(dict(CL3_MISSION_PARAMS))
        self.assertIsInstance(st, mlib.Cl3State)
        self.assertEqual(st.phase, mlib.CL3_STOP)
        out, actions = cl3.decide(st, snap(ut=1000.0))
        self.assertEqual([a.seam_verb for a in actions], ["StopRecording"])
        rows = cl3.evaluate([], dict(CL3_MISSION_PARAMS), out)
        self.assertEqual([r.name for r in rows],
                         [r.name for r in mlib.evaluate_cl3_assertions(
                             [], cl3_params(), out)])

    def test_this_lane_declares_no_handoff_contract(self):
        """A HANDOFF mission is one that terminates BEFORE the outcome it
        certifies (EVA-4). CL-3 terminates on its own observed loop closure, and
        the tombstone it feeds is the scenario's claim, not a mission claim
        deferred to a later verb."""
        self.assertIsNone(mlib.mission_handoff_contract(cl3.MISSION_NAME))

    def test_the_shell_has_no_module_top_krpc_import(self):
        """The lazy-import discipline: `import krpc` lives inside
        `KrpcMissionControl.open`, never at module top, so this module imports
        clean on the base interpreter (no venv)."""
        self.assertNotIn("krpc", sys.modules)
        self.assertIsNotNone(cl3.make_control)
        self.assertIs(mission_runner.MissionSpec, type(cl3.SPEC).__mro__[0])


# ---------------------------------------------------------------------------
# 9. THE SCENARIO SPEC (CL-3-refly-crew-tombstone.toml).
#
# The mission machine above can be perfect and the run still prove nothing: the
# tombstone evidence lives entirely in the spec's logContracts, the claims it
# makes, and the blocks it deliberately does NOT declare. Six groups, each
# naming the failure a live flight would otherwise be first to discover.
# ---------------------------------------------------------------------------


def _read_spec(path=SPEC_PATH):
    with open(path, "rb") as fh:
        return tomllib.load(fh)


def _spec_text(path=SPEC_PATH):
    with open(path, "r", encoding="utf-8") as fh:
        return fh.read()


def _steps(spec):
    return spec["driver"]["steps"]


def _cmds(spec):
    return [s.get("cmd") for s in _steps(spec)]


def _index_of_cmd(spec, cmd):
    for i, s in enumerate(_steps(spec)):
        if s.get("cmd") == cmd:
            return i
    raise AssertionError("no %s step" % cmd)


def _index_of_mission(spec):
    for i, s in enumerate(_steps(spec)):
        if s.get("phase") == "mission":
            return i
    raise AssertionError("no mission step")


def _fixture_save_lines(spec):
    path = os.path.join(_HARNESS, spec["fixture"]["saveTemplate"], "persistent.sfs")
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        return [l.strip() for l in fh.read().split("\n")]


class Cl3SpecValidationTests(unittest.TestCase):
    """The spec must clear the SAME pre-launch gate `run.py` runs, with the SAME
    injected mission-schema registry. A spec error found here costs nothing; found
    on the instance it costs a boot and an operator's attention."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec()
        with open(REGISTRY_PATH, "rb") as fh:
            cls.registry = tomllib.load(fh)

    def test_the_committed_spec_validates_through_the_real_path(self):
        # `harness_run.resolve_mission_schemas` is the shell half that reads
        # harness/missions/<mission>.schema.toml and INJECTS it; hlib's pure
        # validator then drives required-key presence and per-value ranges from
        # it. Driving both here is what proves the mission is REGISTERED, not
        # merely that the TOML parses.
        schemas, shell_errors = harness_run.resolve_mission_schemas(self.spec)
        self.assertEqual([], list(shell_errors))
        self.assertIn(cl3.MISSION_NAME, schemas)
        v = hlib.validate_spec(self.spec, self.registry, mission_schemas=schemas)
        self.assertTrue(v.ok, "errors=%s" % (list(v.errors),))
        self.assertEqual([], list(v.warnings),
                         "a warning is a declared-but-inert surface; resolve it "
                         "rather than shipping it")

    def test_the_spec_drives_this_mission_over_the_autopilot_driver(self):
        self.assertEqual("autopilot", self.spec["driver"]["kind"])
        self.assertEqual(cl3.MISSION_NAME, self.spec["driver"]["mission"])

    def test_the_id_matches_the_filename_and_the_tier_is_operator(self):
        self.assertEqual("CL-3-refly-crew-tombstone", self.spec["id"])
        self.assertEqual(
            self.spec["id"] + ".toml", os.path.basename(SPEC_PATH))
        self.assertEqual("operator", self.spec["tier"])
        self.assertIn("operator", hlib.TIERS)

    def test_the_tags_say_flown_and_no_longer_claim_pending_flight(self):
        # `--tag` is a REAL selector, so the tag has to track the record in BOTH
        # directions: `flown` on an unproven spec would put it in a sweep that
        # means "these work", and `pending-flight` on a spec that HAS flown green
        # excludes it from that same sweep. It has flown, so it carries `flown`.
        tags = self.spec["tags"]
        self.assertIn("flown", tags)
        self.assertNotIn("pending-flight", tags)
        # It also carries `pending-operator`, and that carries an obligation:
        # `test_hlib.py::PendingOperatorTagHonestyTests.CARRIERS` must name it
        # with the debt it owes, in the same commit. That cell reds if it does not.
        self.assertIn("pending-operator", tags)


class Cl3SpecStepListTests(unittest.TestCase):
    """R1's step shape, with the ascent gone. The ORDER is the contract."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec()

    def test_the_step_shape_mirrors_r1_without_the_ascent(self):
        self.assertEqual(
            ["LoadGame", "SetSetting", None, "AnswerMergeDialog",
             "RecordingState", "FlushAndQuit"], _cmds(self.spec))
        self.assertEqual(2, _index_of_mission(self.spec))

    def test_the_steps_are_scoped_to_the_driver_table_not_the_params_sub_table(self):
        # THE TOML TRAP, pinned mechanically: a `steps` key written AFTER the
        # [driver.missionParams] header is scoped to driver.missionParams.steps,
        # and the spec then validates as having ZERO steps and drives nothing.
        self.assertTrue(_steps(self.spec))
        self.assertNotIn("steps", self.spec["driver"]["missionParams"])

    def test_auto_record_on_launch_is_true_and_set_before_the_flight(self):
        # MANDATORY, and the opposite of S4.1's setting. The re-fly leg must be
        # RECORDED: an unrecorded re-fly leaves the provisional with no Points,
        # `ValidateSupersedeTarget` refuses the supersede batch, and the merge
        # tombstones nothing - a green-looking run proving the opposite of this
        # spec's claim.
        setting = [(i, s) for i, s in enumerate(_steps(self.spec))
                   if s.get("cmd") == "SetSetting"
                   and (s.get("args") or {}).get("name") == "autoRecordOnLaunch"]
        self.assertEqual(1, len(setting))
        i, step = setting[0]
        self.assertEqual("true", step["args"]["value"],
                         "the value is the STRING 'true' (the SetSetting arg convention)")
        self.assertLess(i, _index_of_mission(self.spec))

    def test_the_merge_dialog_answers_merge_after_the_mission(self):
        # `merge`, not V1's `discard`: merge is the only choice that reaches
        # CommitSupersede -> AppendRelations -> CommitTombstones. And it is a
        # POST-mission step because its completion contract requires leaving
        # FLIGHT, which ends the mission's own telemetry.
        i = _index_of_cmd(self.spec, "AnswerMergeDialog")
        self.assertGreater(i, _index_of_mission(self.spec))
        self.assertEqual("merge", _steps(self.spec)[i]["args"]["choice"])

    def test_no_conclusion_step_sits_between_the_mission_and_the_dialog(self):
        # The AnswerMergeDialog FIFO-starvation deadlock (design finding A-B1): a
        # blocking scene-changing conclusion would starve the very
        # AnswerMergeDialog that unblocks it.
        between = _cmds(self.spec)[_index_of_mission(self.spec) + 1:
                                   _index_of_cmd(self.spec, "AnswerMergeDialog")]
        self.assertEqual([], between)

    def test_the_merge_dialog_budget_clears_its_c_sharp_deferral_budget(self):
        # A per-step budget below the C# DeferralBudget times the step out from
        # the harness side while the verb is still legitimately waiting for the
        # dialog - an INVALID that looks like a product hang.
        step = _steps(self.spec)[_index_of_cmd(self.spec, "AnswerMergeDialog")]
        self.assertGreaterEqual(
            float(step["budget"]),
            hlib.DISPATCH_DEFERRAL_BUDGET_SECONDS["AnswerMergeDialog"])

    def test_the_merge_dialog_is_world_mutating_so_an_unmet_mission_skips_it(self):
        # ORTHOGONALITY. `hlib.plan_unmet_mission_tail` runs CLEANUP steps only,
        # so a mission that did not fly never merges a junk provisional into the
        # committed corpus. (An earlier revision added "...while this lane's OPEN
        # GATE still reds the mission" - that gate is CLOSED, the row was removed,
        # and this file's own cell forbids exactly that stale prose in the spec.)
        self.assertEqual(hlib.TAIL_ROLE_WORLD_MUTATING,
                         hlib.SEAM_VERB_TAIL_ROLE["AnswerMergeDialog"])

    def test_the_quit_owner_survives_an_unmet_tail(self):
        steps = _steps(self.spec)
        plan = hlib.plan_unmet_mission_tail(
            steps, _index_of_mission(self.spec),
            skip_tail=hlib.spec_skips_tail_on_unmet_mission(self.spec))
        self.assertIn("FlushAndQuit", [d.cmd for d in plan.dispositions if d.run])

    def test_the_mission_step_expects_mission_ok(self):
        step = _steps(self.spec)[_index_of_mission(self.spec)]
        self.assertEqual("MISSION-OK", step["expect"])

    def test_the_wall_budget_covers_every_step_budget(self):
        total = sum(float(s.get("budget", 0) or 0) for s in _steps(self.spec))
        self.assertGreater(float(self.spec["runtime"]["budgetSeconds"]), total,
                           "the wall budget must leave headroom over the step "
                           "budgets for the KSP boot")

    def test_the_last_step_flushes_and_quits(self):
        self.assertEqual("FlushAndQuit", _cmds(self.spec)[-1])

    def test_there_is_no_commit_tree_and_no_stop_recording_step(self):
        # Both are the MISSION's job or unreachable: the tree this lane rewinds is
        # already committed on disk (nothing to CommitTree), and StopRecording is
        # driven from inside the machine as its own first act, so that the
        # dispatcher's `recording-active` gate is carried as an OBSERVED
        # precondition rather than as a step-order assumption.
        for cmd in ("CommitTree", "StopRecording"):
            self.assertNotIn(cmd, _cmds(self.spec))


class Cl3SpecArmedTests(unittest.TestCase):
    """THE ARMING GUARD, now on the far side of it.

    This class shipped asserting the spec was UNARMED and said each cell would
    red on the arming commit ON PURPOSE. They did. Arming happened 2026-08-03
    off the measurement flight `2026-08-03_1834` (PASS attempt 1), which read
    `supersedeRows=1 tombstones=1` REPORT-ONLY - so the windows below make a
    MEASURED behaviour load-bearing rather than asserting an unmeasured one,
    which is the precondition the guard exists to force.

    The cells now pin the ARMED state, so DISARMING is what costs an edit."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec()
        cls.exp = cls.spec["expectations"]

    def test_the_rewind_block_is_armed_on_measured_floors(self):
        # FLOORS, not pins: `min = 1` separates "the merge ran" from "the merge
        # actually retired something". A refused batch writes `Added 0 supersede
        # relations` and tombstones nothing - the silent pass this lane exists to
        # make impossible. No ceilings: the counts depend on how much the re-fly
        # leg recorded, which one flight cannot pin honestly.
        self.assertIn("rewind", self.exp)
        self.assertIn("rewind", saveparse.armed_structure_blocks(self.exp))
        self.assertEqual({"min": 1}, self.exp["rewind"]["supersedeRows"])
        self.assertEqual({"min": 1}, self.exp["rewind"]["tombstones"])
        # rewindPoints stays UNDECLARED: the measurement read 0 because the merge
        # journal REAPS the RP, so asserting its presence post-merge would assert
        # the opposite of correct behaviour.
        self.assertNotIn("rewindPoints", self.exp["rewind"])

    def test_the_structure_block_is_declared_but_left_report_only(self):
        # Gating is PER BLOCK, so structure is recorded without being gated. Its
        # terminal-state mix depends on where the relaunch leg ends, and a single
        # sample is not a contract - only the counts are.
        struct = self.exp["recordings"]["structure"]
        self.assertNotIn("gating", struct)
        self.assertNotIn("structure", saveparse.armed_structure_blocks(self.exp))
        self.assertEqual({"min": 4}, struct["recordings"])
        self.assertNotIn("terminalStates", struct)
        self.assertNotIn("points", self.exp.get("recordings", {}))

    def test_exactly_one_block_arms_gating_and_it_is_rewind(self):
        # Anti-over-arming: a later author adding `gating = true` to structure or
        # points must do it deliberately, with its own measurement.
        self.assertTrue(saveparse.gating_armed(self.exp))
        self.assertEqual(("rewind",), saveparse.armed_structure_blocks(self.exp))
        # ...and not in a block shape this test does not model either: the raw
        # text carries no `gating` key at all, so a future author cannot arm one
        # by declaring a surface these asserts do not enumerate.
        gating_lines = []
        for line in _spec_text().split("\n"):
            stripped = line.strip()
            if stripped.startswith("#"):
                continue
            if "gating" in stripped:
                gating_lines.append(stripped)
        # EXACTLY ONE non-comment `gating` line in the whole file, so a second
        # armed block cannot appear without this cell noticing.
        self.assertEqual(["gating       = true"], gating_lines)

    def test_it_is_on_the_save_structure_armed_allowlist(self):
        # THE CROSS-FILE HALF, read out of the guard's own source rather than
        # restated: `test_hlib.py::SaveStructureVerifierWiringTests.ARMED_ALLOWLIST`
        # is the suite-wide list of specs permitted to arm gating, and this cell is
        # the same statement from the spec's side - so an arming commit has to flip
        # BOTH, which is exactly the deliberateness the allowlist exists to force.
        src = os.path.join(_HARNESS, "lib", "test_hlib.py")
        with open(src, "r", encoding="utf-8") as fh:
            text = fh.read()
        m = re.search(r"ARMED_ALLOWLIST\s*=\s*\{([^}]*)\}", text)
        self.assertIsNotNone(m, "test_hlib.py no longer defines ARMED_ALLOWLIST - "
                                "the save-structure arming guard moved or was removed")
        allowlist = set(re.findall(r'"([^"]+)"', m.group(1)))
        # Flipped by the arming commit: BOTH sides had to change, which is the
        # deliberateness the allowlist exists to force.
        self.assertIn(os.path.basename(SPEC_PATH), allowlist)
        # ...and the allowlist is not empty, or this cell would pass vacuously
        # against a guard that had quietly stopped guarding.
        self.assertTrue(allowlist)

    def test_no_ledger_block_and_declaring_one_would_be_a_hard_spec_error(self):
        # Not a style choice: `hlib.validate_spec` REJECTS [expectations.ledger]
        # paired with InvokeRewind or AnswerMergeDialog, because a rewind/merge
        # rewrites the career pools the seed+manifest contract cannot model (the
        # rewound-career oracle is deferred to L4). This is the mirror of
        # `test_cl2_crew_loss_ledger.py::test_no_rewind_or_merge_dialog_step_pairs_
        # with_the_ledger_block`, and it is why CL-2 and CL-3 are two specs.
        self.assertNotIn("ledger", self.exp)
        bad = copy.deepcopy(self.spec)
        bad["expectations"]["ledger"] = {
            "seedFrom": "template", "tolerances": "default", "manifest": []}
        with open(REGISTRY_PATH, "rb") as fh:
            registry = tomllib.load(fh)
        schemas, _ = harness_run.resolve_mission_schemas(bad)
        v = hlib.validate_spec(bad, registry, mission_schemas=schemas)
        self.assertFalse(v.ok)
        self.assertTrue(any("expectations.ledger" in e for e in v.errors), v.errors)

    def test_the_expected_fail_block_masks_nothing(self):
        # An `expectedFail.bugId` here would demote EVERY failure of its subkind -
        # including a genuine merge-tail regression - under one bug id. S4.1's
        # removed keys document that active-masking failure mode at length.
        self.assertEqual("", self.spec["expectedFail"]["bugId"])
        self.assertNotIn("subkind", self.spec["expectedFail"])

    def test_the_recording_count_is_a_floor_with_no_measured_ceiling(self):
        # A ceiling would be a number nobody has measured: the injected corpus is
        # three recordings before the game boots, the re-fly adds its provisional
        # and the merge adds the fork. CL-2's `{min=1, max=1}` belongs to a
        # one-flight, no-injection profile and must not be copied here.
        count = self.exp["recordings"]["count"]
        self.assertEqual({"min": 1}, count)


class Cl3SpecFixtureSyncTests(unittest.TestCase):
    """The spec cites identifiers that live in C# and in run.py. Each is read
    from its source here rather than restated, so a rename reds locally instead
    of as `REJECTED unknown-rp` on the instance.

    Like `CommittedBatchTallySourceSyncTests`, one cell deliberately reads
    OUTSIDE `harness/`."""

    FIXTURE_CS = os.path.join(
        os.path.dirname(_HARNESS), "Source", "Parsek.Tests", "Generators",
        "RewindCrewLossFixture.cs")

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec()
        with open(cls.FIXTURE_CS, "r", encoding="utf-8", errors="replace") as fh:
            cls.cs = fh.read()

    def _cs_const(self, name):
        m = re.search(r'const\s+string\s+%s\s*=\s*"([^"]+)"' % name, self.cs)
        self.assertIsNotNone(m, "RewindCrewLossFixture has no %s constant" % name)
        return m.group(1)

    def _cs_int(self, name):
        m = re.search(r'const\s+int\s+%s\s*=\s*(\d+)' % name, self.cs)
        self.assertIsNotNone(m, "RewindCrewLossFixture has no %s constant" % name)
        return int(m.group(1))

    def test_the_injection_preset_is_the_crew_loss_corpus_and_hlib_knows_it(self):
        preset = self.spec["fixture"]["injectedRecordings"]
        self.assertEqual("rewind-crew-loss", preset)
        self.assertIn(preset, hlib.INJECTED_RECORDINGS)
        self.assertIn(preset, harness_run.RP_SIDECAR_BY_PRESET)

    def test_the_rewind_point_id_is_the_one_the_c_sharp_fixture_bakes(self):
        # `ParsekTestCommandAddon.InvokeRewindImpl` matches RewindPointId EXACTLY
        # and answers REJECTED unknown-rp otherwise, so a drifted constant is a
        # whole wasted attempt.
        rp = self.spec["driver"]["missionParams"]["rewindPointId"]
        self.assertEqual(self._cs_const("RewindPointId"), rp)
        # run.py's inject POSTCONDITION fail-closes on this exact sidecar path, so
        # a mismatch would also fail the stage rather than the flight.
        self.assertEqual(rp, harness_run.RP_SIDECAR_BY_PRESET["rewind-crew-loss"])

    def test_the_slot_is_the_crewed_pod_and_not_the_crewless_probe(self):
        # THE CELL THAT STOPS A GREEN RUN PROVING THE WRONG THING. Slot 0 is the
        # intact, crewless probe `cl-probe-b`: rewinding onto it would merge
        # perfectly and tombstone NOTHING, because the KerbalAssignment+Dead row
        # Parsek derives at load belongs to the pod.
        slot = self.spec["driver"]["missionParams"]["rewindSlot"]
        self.assertEqual(self._cs_int("PodSlotIndex"), slot)
        self.assertNotEqual(self._cs_int("ProbeSlotIndex"), slot)

    def test_the_template_is_the_career_fixture_with_a_focusable_vessel(self):
        # THREE properties, each load-bearing and each read from the committed
        # fixture: CAREER (the D14 claim, and what makes the death row a career
        # action), a focusable `activeVessel` (LoadGame takes the FOCUS route only
        # then - the SPACECENTER route would DEFER every RequiresFlight verb to a
        # TIMEOUT), and a Parsek footprint (on the FLIGHT focus route LoadGameImpl
        # does not re-save, so a save with no ParsekScenario node produced ZERO
        # recordings on CL-1's first flight).
        self.assertEqual("fixtures/saves/career-pad-craft",
                         self.spec["fixture"]["saveTemplate"])
        lines = _fixture_save_lines(self.spec)
        self.assertIn("Mode = CAREER", lines)
        active = [l for l in lines if l.startswith("activeVessel = ")]
        self.assertEqual(1, len(active))
        self.assertGreaterEqual(int(active[0].split("= ")[1]), 0)
        self.assertTrue(any(l == "name = ParsekScenario" for l in lines),
                        "the template must carry the inert ParsekScenario node")

    def test_the_dead_kerbal_is_in_the_templates_roster(self):
        # The derived KerbalAssignment row resolves a REAL kerbal only if the
        # roster has him; `RewindCrewLossFixture` picked stock's starting
        # commander for exactly this reason.
        name = self._cs_const("DeadCrewName")
        self.assertEqual("Jebediah Kerman", name)
        self.assertIn("name = " + name, _fixture_save_lines(self.spec))


class Cl3SpecLogContractTests(unittest.TestCase):
    """The tombstone evidence IS these patterns. logContract matching is
    presence-only `re.search`, so every numeric guard has to be IN the pattern -
    and each pattern is checked against the line it is meant to match AND
    against the decoy line it must reject."""

    # MEASURED, run `2026-07-28_1509_R1-rewind-loop-flown` (the only archived run
    # that has ever written a supersede row).
    SUPERSEDE_LINE = ("[Parsek][INFO][Supersede] Added 1 supersede relations for "
                      "subtree rooted at b9-booster-a (origin=b9-root "
                      "subtreeCount=1 skippedExisting=0 skippedSelfLink=0 "
                      "skippedExtraSelfLink=0 skippedPreRewindCarveOut=0)")
    SUPERSEDE_REFUSED = ("[Parsek][INFO][Supersede] Added 0 supersede relations for "
                         "subtree rooted at cl-pod-a (origin=cl-stack-root "
                         "subtreeCount=1 skippedExisting=0 skippedSelfLink=0 "
                         "skippedExtraSelfLink=0 skippedPreRewindCarveOut=0)")
    # DERIVED FROM THE CALL SITE (SupersedeCommit.CommitTombstones), NOT yet
    # measured - the first flight is what confirms the rendered text. CL-2's
    # `PopulateCrewEndStates` finding is the standing reminder that a derived
    # token can be one the product never emits on the path taken.
    TOMBSTONE_LINE = ("[Parsek][INFO][LedgerSwap] Tombstoned 1 career actions "
                      "(Contract=0, Milestone=0, Facility=0, Strategy=0, "
                      "ScienceSpending=0, Science=0, Funds=0, Reputation=0, "
                      "Kerbal=1, Other=0); 0 excluded (Seed=0, Rollout=0, Other=0)")
    # The EMPTY-SUBTREE early return is a DIFFERENT literal in the same file.
    TOMBSTONE_EMPTY = ("[Parsek][INFO][LedgerSwap] Tombstoned 0 career actions "
                       "(Contract=0, Milestone=0, Facility=0, Strategy=0, "
                       "ScienceSpending=0, Science=0, Funds=0, Reputation=0, "
                       "Kerbal=0, Other=0); 0 excluded (Seed=0, Rollout=0)")
    # A real merge that tombstoned career rows but NO crew row.
    TOMBSTONE_NO_KERBAL = ("[Parsek][INFO][LedgerSwap] Tombstoned 2 career actions "
                           "(Contract=0, Milestone=2, Facility=0, Strategy=0, "
                           "ScienceSpending=0, Science=0, Funds=0, Reputation=0, "
                           "Kerbal=0, Other=0); 0 excluded (Seed=0, Rollout=0, Other=0)")

    @classmethod
    def setUpClass(cls):
        cls.contracts = _read_spec()["expectations"]["logContracts"]
        cls.required = cls.contracts["required"]

    def _one(self, needle):
        matching = [p for p in self.required if needle in p]
        self.assertEqual(1, len(matching),
                         "expected exactly one required pattern containing %r" % needle)
        return matching[0]

    def test_every_pattern_compiles_and_is_ascii(self):
        # An escaping slip produces a pattern that COMPILES and matches nothing,
        # which reds a green run and looks exactly like a product defect.
        for pattern in self.required + self.contracts["forbidden"]:
            re.compile(pattern)
            pattern.encode("ascii")

    def test_the_refly_milestones_r1_pins_are_all_present(self):
        # Both StartInvoke and the post-load completion must fire: that pair is
        # what distinguishes a real end-to-end re-fly from a verb that returned.
        for needle in ("StartInvoke", "Invocation complete",
                       "ConsumePostLoad: restoring bundle with route-retire cutoffUT=",
                       "Restored: recs=.* pendingScience="):
            self._one(needle)
        # ...and the re-fly leg must be recorded at all, or LOOP-POINTS has
        # nothing to read and the provisional carries no Points.
        self._one("Recording started")

    def test_the_supersede_token_requires_a_non_zero_count(self):
        # `Added 0 supersede relations` is EXACTLY what a refused batch emits, so
        # the count is the difference between "the merge ran" and "the merge
        # actually wrote the relation".
        pattern = self._one("supersede relations")
        self.assertRegex(self.SUPERSEDE_LINE, pattern)
        self.assertNotRegex(self.SUPERSEDE_REFUSED, pattern)

    def test_the_tombstone_token_requires_a_non_zero_kerbal_term(self):
        # BOTH guards matter. `Tombstoned [1-9][0-9]*` rejects the empty-subtree
        # early return; `Kerbal=[1-9]` is what makes the claim about a KERBAL
        # action rather than about any tombstone at all - a merge that retired two
        # milestones and no crew row would satisfy a bare `Tombstoned [1-9]` and
        # prove nothing this spec is about.
        pattern = self._one("Tombstoned")
        self.assertRegex(self.TOMBSTONE_LINE, pattern)
        self.assertNotRegex(self.TOMBSTONE_EMPTY, pattern)
        self.assertNotRegex(self.TOMBSTONE_NO_KERBAL, pattern)

    def test_the_refused_unflown_provisional_token_is_deliberately_absent(self):
        # S4.1 REQUIRES this token as its own positive assertion (it flies nothing,
        # so its provisional legitimately has no trajectory). Here its presence in
        # a log would mean the re-fly leg never flew - the one silent failure the
        # RELAUNCH and LOOP-POINTS gates exist to prevent - so requiring it would
        # be requiring this scenario to fail at its purpose.
        self.assertFalse(any("refused-unflown-provisional" in p
                             for p in self.required))

    def test_the_forbidden_error_pattern_is_kept(self):
        # It has earned its keep on this lane twice: it caught R1-EMPTY-PROVISIONAL,
        # and the merge tail adds new throwing surfaces (`TryCommitReFlySupersede:
        # orchestrator threw ...`, the NotCommitted AppendRelations invariant).
        self.assertIn("\\[Parsek\\]\\[ERROR\\]", self.contracts["forbidden"])

    def test_no_required_pattern_hard_codes_an_ascii_arrow(self):
        # The house trap: several Parsek format strings separate with U+2192, so a
        # spec author reaching for `->` writes a regex that matches nothing.
        for pattern in self.required:
            self.assertNotIn("->", pattern)


class Cl3SpecCoverageClaimTests(unittest.TestCase):
    """CLAIM-IS-NOT-GATE, in both directions: every claimed value must exist in
    the registry AND be backed by a proving token in this spec's own
    logContracts (or by a mechanically-checkable fixture property), and the
    scope-fenced values must stay unclaimed until the evidence exists."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec()
        with open(REGISTRY_PATH, "rb") as fh:
            cls.registry = tomllib.load(fh)
        with open(REGISTRY_PATH, "r", encoding="utf-8") as fh:
            cls.registry_text = fh.read()
        cls.claimed = cls.spec["dimensionsCovered"]
        cls.required = cls.spec["expectations"]["logContracts"]["required"]

    def _has(self, needle):
        return any(needle in p for p in self.required)

    def test_every_claimed_value_exists_in_the_registry(self):
        for dim, values in self.claimed.items():
            for value in values:
                self.assertIn(value, self.registry[dim]["values"],
                              "%s value %r is not in coverage/registry.toml"
                              % (dim, value))

    def test_the_auto_record_claim_has_its_token(self):
        self.assertEqual(["auto-record-launch"], self.claimed["D1"])
        self.assertTrue(self._has("Recording started"))

    def test_every_claimed_d9_value_has_a_proving_token(self):
        proof = {
            "rewind-to-separation": "StartInvoke",
            "refly-gate": "Invocation complete",
            "reconciliation-bundle": "ConsumePostLoad: restoring bundle",
            "read-back-guard": "Restored: recs=",
            # Both ride the merge tail's own line: the supersede rows are written
            # from inside the journal-driven merge, so `Added ...` cannot fire
            # without the journal (R1's precedent, same tokens).
            "supersede-relation": "supersede relations",
            "merge-journal": "supersede relations",
            # ADDED by the arming commit. Gated TWICE: this token, whose
            # `Kerbal=[1-9]` term is what makes it a KERBAL tombstone rather than
            # any tombstone, AND `[expectations.rewind] tombstones = { min = 1 }`
            # armed off the measurement run.
            "tombstones": "Tombstoned",
        }
        self.assertEqual(sorted(proof), sorted(self.claimed["D9"]))
        for value, needle in proof.items():
            self.assertTrue(self._has(needle),
                            "D9 %s is claimed with no proving token (%r)"
                            % (value, needle))

    def test_the_career_claim_is_backed_by_the_committed_template(self):
        # No log token names career mode, so the claim is gated on the FIXTURE -
        # checked against the committed save rather than against prose.
        self.assertIn("career", self.claimed["D14"])
        self.assertIn("Mode = CAREER", _fixture_save_lines(self.spec))

    def test_every_claimed_d12_value_has_a_proving_token(self):
        # `dead-crew-strip` CLAIMED 2026-08-05, and this is the claim-is-not-gate
        # half: the claim is legal only while the spec still REQUIRES the token
        # that gates the registry's re-pinned half (ii).
        self.assertEqual(["dead-crew-strip"], self.claimed.get("D12", []))
        self.assertTrue(self._has("Recomputed after tombstones:"),
                        "D12 dead-crew-strip is claimed with no recompute token")
        # `permanent=0` IS the assertion, and the TRAILING SPACE is load-bearing
        # (without it the term would also match a `permanent=01`+ rendering). A
        # token pinning the COUNT instead would be wrong in a way no flight
        # catches: a same-crew re-fly always leaves the fork's own live
        # reservation, so `0 reservations remain` is structurally unreachable.
        self.assertTrue(self._has("(permanent=0 "),
                        "the D12 token must pin `permanent=0 `, not the bare count")

    def test_the_dead_crew_strip_claim_does_not_ride_an_inverted_stand_in_gate(self):
        # THE INVERSION, pinned so nobody re-adds the candidate that was wrong.
        # A pre-tombstone Dead row MERGES into the reservation entry and sets
        # `IsPermanent`; `PostWalk` `continue`s permanent reservations BEFORE slot
        # creation, so a corpse never gets a slot or a stand-in. STRIPPING the row
        # demotes the entry to temporary -> slot -> stand-in. So a
        # `forbidden = ["Stand-in generated"]` row would pass EXACTLY WHEN THE
        # TOMBSTONE DID NOTHING and fail because it worked. Measured in
        # `2026-08-04_2136`: permanent 1 -> 0 with `Stand-in generated: 'Tilan
        # Kerman' (Pilot) for slot 'Jebediah Kerman'` as the RESULT of the strip.
        forbidden = self.spec["expectations"]["logContracts"].get("forbidden", [])
        self.assertFalse(any("Stand-in generated" in p for p in forbidden),
                         "a Stand-in-generated forbidden row is INVERTED on this lane")
        # ...and the same reasoning forbids requiring the strip to zero the count.
        self.assertFalse(any("0 reservations remain" in p for p in self.required))

    def test_the_scope_fenced_cells_stay_unclaimed(self):
        # THE SCOPE FENCE, with the fence line MOVED once and recorded as moved.
        #
        # D9 `tombstones` was claimed at the 2026-08-03 arming; the cell above
        # pins both of its gates.
        #
        # D12 `dead-crew-strip` was claimed on 2026-08-05, and NOT by relaxing the
        # definition - by RE-PINNING the half of it that no flight could satisfy.
        # The 2026-08-02 pin read (ii) NAME-SCOPED ("no surviving reservation or
        # stand-in for the dead kerbal"). `2026-08-03_1834` measured that failing
        # on a crewed fixture root; `2026-08-04_2136` re-ran it with the root made
        # crewless and settled the discrimination: the root-sourced reservation is
        # GONE and the death-sourced one IS released (`Reservation: 'Jebediah
        # Kerman' endUT=INDEFINITE (Dead), recording 'cl-pod-a'` never reappears
        # after the tombstone), yet `1 reservations remain` again - the survivor
        # being `endUT=Infinity (Unknown), recording 'rec_c5b16ffb…'`, the re-fly
        # session's OWN live fork, with the kerbal alive aboard the active vessel.
        # That is correct by design and structurally unavoidable: the recompute
        # rebuilds from ELS and `ProcessAction` adds an entry for every surviving
        # `KerbalAssignment`. The re-pinned (ii) is `permanent=0` - death-scoped,
        # which is the reading the registry's own rationale sentence already
        # warranted ("a reservation and a stand-in for a CORPSE the merged
        # timeline says never died") - and it is falsifiable in the right
        # direction: a no-op tombstone leaves `permanent=1` and reds. The two
        # cells above are its gate.
        #
        # `tombstone-rep-penalty` stays unclaimed for a DIFFERENT reason - see the
        # cell below: a product change, not a flight.
        self.assertIn("tombstones", self.claimed.get("D9", []))
        self.assertNotIn("tombstones", self.claimed.get("D8", []))
        self.assertNotIn("tombstone-rep-penalty", self.claimed.get("D12", []))
        # D8 is not claimed AT ALL: the merge does recalc the ledger, but no token
        # here names a module or the recalc engine, and this spec cannot carry the
        # oracle facets that back CL-2's D8 claims (the ledger block is illegal
        # alongside InvokeRewind).
        self.assertEqual([], self.claimed.get("D8", []))

    def test_the_rep_penalty_cell_is_recorded_unreachable_not_merely_uncovered(self):
        # A DIFFERENT kind of absence, and the distinction is why this cell is
        # separate from the one above: `tombstone-rep-penalty` is not waiting on a
        # flight, it is waiting on a PRODUCT change. Nothing in Source/Parsek/ ever
        # constructs a `ReputationPenaltySource.KerbalDeath` action, so a crew death
        # produces no Parsek rep row to tombstone. If the registry ever stops saying
        # so, this spec's "will never be claimed here" comment has gone stale.
        self.assertIn("UNREACHABLE BY ANY FLIGHT", self.registry_text)
        # This used to assert `claimed["D12"] == []`, which stopped being the
        # honest check on 2026-08-05 when `dead-crew-strip` was claimed. The
        # guarantee it was really buying is narrower and survives the change:
        # THIS value cannot arrive by an edit that only touches a list...
        self.assertNotIn("tombstone-rep-penalty", self.claimed.get("D12", []))
        # ...and the spec carries the REASON, not merely the omission, so the
        # next author does not re-derive it from the product.
        self.assertIn("UNREACHABLE", _spec_text())

    def test_head_tip_split_and_terminal_kind_classify_stay_unclaimed(self):
        # `head-tip-split` fires only when the closure-root recording SPANS the
        # rewind UT, and `cl-pod-a` BEGINS at the split the RewindPoint marks -
        # the same shape argument that moved the cell off S4.1 to NOBODY.
        # `terminal-kind-classify` is S4.1's.
        for value in ("head-tip-split", "terminal-kind-classify"):
            self.assertNotIn(value, self.claimed["D9"])


class Cl3SpecFeasibilityDerivationTests(unittest.TestCase):
    """THE DERIVATION CL-2's header made the house convention: work a scenario's
    feasibility out of the product's own pure functions BEFORE spending a flight
    on it. The mission's rows depend on numbers this fixture fixes, and the
    derivations are kept mechanical here so they cannot rot into prose.

    IT ALREADY PAID FOR ITSELF ONCE. The derivation found that R1's
    `vesselStateChanged` row was constant-False on a flightless pad-to-pad
    rewind - not strict, unsatisfiable - which would have left the mission
    permanently unmet, the world-mutating `AnswerMergeDialog` skipped by the
    default `skipTailOnUnmetMission`, and `CommitTombstones` never driven. The
    row was REMOVED (remedy (a)) rather than flown into or papered over with a
    0.0 threshold; the cells below pin both the fixture fact that forced the cut
    and the write-up that records it."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec()
        cls.lines = _fixture_save_lines(cls.spec)
        cls.params = mlib.cl3_params_from_dict(cls.spec["driver"]["missionParams"])

    def _fixture_vessels(self):
        """[(name, sit, alt)] for every VESSEL node in the committed template."""
        out, cur, inv = [], {}, False
        for line in self.lines:
            if line == "VESSEL":
                inv, cur = True, {}
                continue
            if not inv:
                continue
            for key in ("name", "type", "sit", "alt"):
                if line.startswith(key + " = ") and key not in cur:
                    cur[key] = line.split("= ", 1)[1]
            if line == "PART":
                out.append(cur)
                inv = False
        return out

    def test_the_ut_floor_is_low_because_the_quicksave_carries_the_templates_ut(self):
        # `ScenarioWriter.BuildRewindPointQuicksave` COPIES the host save and never
        # re-stamps `UT`, so the post-rewind clock is the TEMPLATE's UT and the
        # observed regression is exactly the in-flight game time elapsed since the
        # load - single-digit seconds on a lane that flies nothing first, not R1's
        # minutes. R1's 5.0 s floor would red a CORRECT rewind here.
        ut = [float(l.split("= ")[1]) for l in self.lines if l.startswith("UT = ")]
        self.assertTrue(ut)
        self.assertLess(ut[0], 60.0,
                        "this derivation assumes a low-UT template; re-derive the "
                        "floor if the fixture's clock moves")
        self.assertLessEqual(self.params.min_ut_regression, 1.0)
        self.assertGreater(self.params.min_ut_regression, 0.0,
                           "a zero floor would let float noise satisfy the OBSERVED "
                           "rewind gate")

    def test_the_relaunch_leg_has_an_engine_to_climb_with(self):
        # `BuildRewindPointQuicksave` clones the host save's first non-SpaceObject
        # VESSEL per slot. A vessel-less template would force
        # `BuildSyntheticVessel`'s bare mk1pod, which has NO ENGINE and could never
        # satisfy `postRewindFlightObserved`.
        vessels = self._fixture_vessels()
        donors = [v for v in vessels if v.get("type") != "SpaceObject"]
        self.assertTrue(donors, "no donor vessel: the re-fly would get a bare pod")
        text = "\n".join(self.lines)
        self.assertIn("name = solidBooster.sm.v2", text,
                      "the donor must carry a booster: one stage activation is the "
                      "whole relaunch leg")

    def test_the_fixture_still_produces_the_identical_readings_that_forced_the_cut(self):
        # THE DERIVATION THAT REMOVED A ROW, kept live so it cannot rot into
        # prose. `vesselStateChanged` needed
        # `abs(preAlt - postAlt) >= minAltitudeChange` OR
        # `preSituation != postSituation`. Pre-rewind the active vessel is the
        # template's own craft; post-rewind it is the RP quicksave's slot vessel,
        # which `BuildRewindPointQuicksave` produces by `CreateCopy()`ing that SAME
        # node - `StampVesselIdentity` rewrites only pid / persistentId / name /
        # root-part persistentId and touches NO coordinate. So both readings are
        # the same craft on the same pad, and BOTH disjuncts were false by
        # construction. R1 and V1 satisfy the row only because they FLY first.
        #
        # If the fixture ever DOES move the slot vessels (remedy (b) in the spec
        # header), this cell reds and the row becomes re-addable.
        donors = [v for v in self._fixture_vessels()
                  if v.get("type") != "SpaceObject"]
        self.assertEqual(1, len(donors),
                         "the derivation assumes the ACTIVE vessel and the RP donor "
                         "are the same node; re-derive it if the template grows a "
                         "second controllable vessel")
        self.assertEqual("PRELAUNCH", donors[0]["sit"])
        alt = float(donors[0]["alt"])

        # THE POINT OF THE CUT, driven through the committed params: a state whose
        # world readings are identical still evaluates GREEN, because no row
        # judges them any more. While the row existed this was a permanent
        # MISSION-ASSERT-FAIL, which (AnswerMergeDialog being world-mutating under
        # the default skipTailOnUnmetMission) skipped the merge and left
        # CommitTombstones - this scenario's whole subject - undriven.
        params = self.params
        st = replace(drive_to_loop_closed(),
                     pre_rewind_situation="PRE_LAUNCH",
                     post_rewind_situation="PRE_LAUNCH",
                     pre_rewind_altitude=alt, post_rewind_altitude=alt)
        rows = mlib.evaluate_cl3_assertions([], params, st)
        self.assertNotIn("vesselStateChanged", {r.name for r in rows})
        self.assertTrue(mlib.all_assertions_met(rows),
                        [r.name for r in rows if not r.met])

    def test_the_spec_records_the_removal_where_the_operator_reads_it(self):
        # A derived blocker that is not written where the operator reads it is a
        # blocker rediscovered by a flight - and a blocker that was FIXED but not
        # written up is a fix a later author undoes.
        text = _spec_text()
        self.assertIn("vesselStateChanged", text)
        self.assertIn("REMOVED", text)
        # The vacuous escape hatch must stay named as rejected, not merely unused.
        self.assertIn("0.0", text)
        self.assertNotIn("OPEN GATE", text,
                         "the gate is closed; stale 'OPEN GATE' prose would send "
                         "an operator hunting a blocker that no longer exists")




class ManeuverNodeReadGuardTests(unittest.TestCase):
    """The read that killed CL-3's first flight.

    Run 2026-08-03_1826 died INVALID(mission) 1.2 s in, phasesReached=[STOP],
    because `control.nodes` RAISES ("Maneuver node editing is not available")
    for a career craft sitting PRELAUNCH, and the read was the one unguarded
    channel in read_telemetry. Three consecutive polls on the pad then looked
    exactly like a lost vessel. CL-1 escapes it only because it launches at
    once; this lane polls on the pad while it stops the recorder, so it cannot.
    """

    class _Raises:
        def __init__(self, exc):
            self._exc = exc

        @property
        def nodes(self):
            raise self._exc

    class _Returns:
        def __init__(self, nodes):
            self.nodes = nodes

    def _control(self, tolerate=True):
        return mission_runner.KrpcMissionControl(client_name="test",
                                                 tolerate_unreadable_nodes=tolerate)

    def test_a_refusing_node_read_degrades_to_the_empty_list(self):
        # The exact kRPC message the flight hit.
        handle = self._Raises(RuntimeError(
            "Maneuver node editing is not available. Either the vessel is in a "
            "situation where maneuver nodes cannot be used, or the tracking "
            "station has not been upgraded to a sufficient level."))
        self.assertEqual([], self._control()._read_nodes(handle))

    def test_a_readable_node_list_is_passed_through_untouched(self):
        # Anti-vacuity: the guard must not swallow real readings, or every
        # node-gating machine would silently believe nothing is pending.
        sentinel = ["node-0", "node-1"]
        handle = self._Returns(sentinel)
        self.assertIs(sentinel, self._control()._read_nodes(handle))

    def test_the_warning_is_latched_to_once_per_run(self):
        # A permanently dark channel must name itself without spamming a
        # multi-thousand-poll flight.
        control = self._control()
        handle = self._Raises(RuntimeError("nope"))
        self.assertFalse(control._warned_nodes_read)
        control._read_nodes(handle)
        self.assertTrue(control._warned_nodes_read)
        for _ in range(5):
            self.assertEqual([], control._read_nodes(handle))
        self.assertTrue(control._warned_nodes_read)

    def test_the_default_still_propagates_so_no_other_mission_changes(self):
        # THE CELL THAT SHOULD HAVE EXISTED FIRST. Tolerating the refusal
        # GLOBALLY broke CL-1-pod-impact, a live-proven nightly: with a complete
        # frame instead of a blind one, its crew-survived-impact terminal fires
        # 1.9 s in on a craft still on the pad. Measured same-build:
        # tolerance OFF `2026-08-04_0538` PASS 158 s; tolerance ON `_0535` /
        # `_0536` / `_0541` all INVALID. So the default MUST re-raise, leaving
        # every mission that did not ask for this byte-identical.
        handle = self._Raises(RuntimeError("Maneuver node editing is not available"))
        with self.assertRaises(RuntimeError):
            self._control(tolerate=False)._read_nodes(handle)

    def test_the_default_is_off_on_the_real_constructor(self):
        # Not just the test factory: the shipped default.
        self.assertFalse(
            mission_runner.KrpcMissionControl(client_name="test")
            ._tolerate_unreadable_nodes)

    def test_only_the_two_career_fliers_opt_in_across_every_mission_shell(self):
        # Cross-file: a shell quietly opting in would re-globalise the semantics
        # change this flag exists to confine, so the set is an ALLOWLIST and a new
        # member costs an edit here with its reason.
        #
        # `science_bench_recover.py` joined on 2026-08-19, and it is the SAME
        # finding rather than a new one: it is the second mission to fly a CAREER
        # save, so kRPC refuses the maneuver-node read on its un-upgraded Tracking
        # Station exactly as it does for CL-3. MEASURED, not argued -
        # `L3-career-science-recover`'s first reading run (`2026-08-19_1817` and
        # its retry `_1818_a2`, identical) died in 1.2 s at PRELAUNCH with
        # `flight-leg vessel-lost (unreadable after repeated telemetry failures)`.
        # The CL-1 hazard this flag's docstring records does not reach it: CL-1's
        # `crew-survived-impact` is a terminal a PAD frame satisfies, whereas this
        # lane runs B1's structured phase progression and additionally gates its
        # flight assertion on the peak apoapsis landing inside the window.
        import glob
        opters = []
        for path in glob.glob(os.path.join(_HARNESS, "missions", "*.py")):
            with open(path, "r", encoding="utf-8") as fh:
                if "tolerate_unreadable_nodes=True" in fh.read():
                    opters.append(os.path.basename(path))
        self.assertEqual(["cl3_refly_crew_tombstone.py", "science_bench_recover.py"],
                         sorted(opters))

    def test_any_exception_type_degrades_not_just_runtimeerror(self):
        # The guard exists to keep a read fault off the vessel-lost streak; the
        # streak does not care which exception type kRPC chose.
        for exc in (ValueError("v"), AttributeError("a"), Exception("e")):
            self.assertEqual([], self._control()._read_nodes(self._Raises(exc)))

if __name__ == "__main__":
    unittest.main()
