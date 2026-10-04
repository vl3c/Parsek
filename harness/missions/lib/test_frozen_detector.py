"""Frozen-telemetry detector: the hold rule for a non-advancing UT, the per-field
tolerance of the airborne hop machines, the reset / within-tolerance note, and
the nested frozenCount on the machine-state line.

The RB-1 cells replay the dead Flea's logged values (2026-09-27_1353: a
destroyed craft kept reading alt -2.778 / vspd -230.304 / ap 2792.922 /
pe -598378.849 for about 118 polls at 1x) with +-1e-9 noise, the shape that kept
the exact compare from ever reaching 10.

Same import bootstrap as test_observability.py. NO krpc, NO KSP, NO network.
ASCII only; stdlib only.
"""

import math
import os
import sys
import unittest
from dataclasses import replace

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)

import mlib                    # noqa: E402
import mission_runner          # noqa: E402


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


_B1_DICT = {
    "throttle": 1.0,
    "apoapsisWindowMeters": {"min": 6000, "max": 30000},
    "chuteArmMaxRateMps": 30, "chuteFullDeployAltMeters": 2500,
    "landedSituations": ["LANDED", "SPLASHED"],
    "ascentTimeoutSeconds": 90, "coastTimeoutSeconds": 180,
    "descentTimeoutSeconds": 240,
}

# RB-1 dead-Flea readings (mission stdout, telemetry lines from ut=98.720 on).
_RB1 = dict(altitude=-2.778, vertical_speed=-230.304, apoapsis=2792.922,
            periapsis=-598378.849)


def _b1_params(**overrides):
    return mlib.b1_params_from_dict(dict(_B1_DICT, **overrides))


def _b1_state(params, phase=mlib.B1_DESCENT, **overrides):
    """A B1State pinned in ``phase``. DESCENT states default to an armed chute
    (RB-1's Flea armed its chute and still hit at 230 m/s), so the arm-window
    terminal stays out of the way."""
    base = mlib.b1_initial_state(params)
    fields = dict(phase=phase, phase_entry_ut=0.0)
    if phase == mlib.B1_DESCENT:
        fields["chute_deployed"] = True
    fields.update(overrides)
    return replace(base, **fields)


def _rb1_frames(n, start_ut=98.72, noise=1e-9):
    """``n`` 0.5 s polls of the RB-1 values, every field alternating +-noise so
    no two consecutive polls are bit-identical."""
    frames = []
    for i in range(n):
        sign = 1.0 if i % 2 == 0 else -1.0
        fields = {k: v + sign * noise for k, v in _RB1.items()}
        frames.append(snap(ut=start_ut + 0.5 * i, situation="FLYING", **fields))
    return frames


def _drive(decide, state, frames):
    for frame in frames:
        state, _ = decide(state, frame)
        if state.done:
            break
    return state


def _old_advance_frozen_count(prev_sig, prev_count, snapshot, limit):
    """The pre-change detector, verbatim, as the byte-identity oracle."""
    if snapshot.warp_mode != mlib.WARP_NONE:
        return prev_sig, prev_count, False
    curr_sig = mlib.frozen_signature(snapshot)
    advanced = False
    if prev_sig is not None and curr_sig is not None:
        prev_ut, curr_ut = prev_sig[0], curr_sig[0]
        if (mlib._is_finite(prev_ut) and mlib._is_finite(curr_ut)
                and curr_ut > prev_ut):
            advanced = curr_sig[1:] == prev_sig[1:]
    new_count = prev_count + 1 if advanced else 0
    return curr_sig, new_count, (new_count >= limit)


class Rb1ReplayTests(unittest.TestCase):

    def test_rb1_noise_trips_with_the_hop_tolerance(self):
        params = _b1_params()
        self.assertEqual(params.frozen_tolerance_abs, mlib.HOP_FROZEN_TOLERANCE_ABS)
        frames = _rb1_frames(118)
        state = _b1_state(params)
        for i, frame in enumerate(frames):
            state, _ = mlib.b1_decide(state, frame)
            if state.done:
                break
        # The first poll only seeds the signature; the 11th trips at limit 10,
        # the same sample the logged 3-decimal replay trips at.
        self.assertEqual(i, 10)
        self.assertEqual(state.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("telemetry frozen 10 consecutive samples", state.loss_reason)

    def test_rb1_noise_never_trips_at_tolerance_zero(self):
        # Pins the RB-1 bug: the exact compare resets on every noisy poll.
        params = _b1_params(frozenToleranceAbs=0.0)
        self.assertEqual(params.frozen_tolerance_abs, 0.0)
        state = _drive(mlib.b1_decide, _b1_state(params), _rb1_frames(118))
        self.assertFalse(state.done)
        self.assertEqual(state.frozen_count, 0)

    def test_sbr_delegate_inherits_the_hop_tolerance(self):
        params = mlib.sbr_params_from_dict(dict(_B1_DICT))
        self.assertEqual(params.flight.frozen_tolerance_abs,
                         mlib.HOP_FROZEN_TOLERANCE_ABS)


class HoldRuleTests(unittest.TestCase):
    F = dict(altitude=100.0, vertical_speed=-5.0, apoapsis=2000.0, periapsis=-500.0)

    def _run(self, frames, limit=10, tolerance=0.0):
        sig, count, counts = None, 0, []
        for frame in frames:
            sig, count, _ = mlib._advance_frozen_count(sig, count, frame, limit,
                                                       tolerance)
            counts.append(count)
        return sig, counts

    def test_duplicate_ut_inside_a_frozen_run_holds(self):
        uts = (1.0, 2.0, 3.0, 3.0, 4.0, 5.0)
        _, counts = self._run([snap(ut=u, **self.F) for u in uts])
        self.assertEqual(counts, [0, 1, 2, 2, 3, 4])

    def test_duplicate_ut_with_a_different_field_still_holds(self):
        frames = [snap(ut=1.0, **self.F), snap(ut=2.0, **self.F),
                  snap(ut=2.0, **dict(self.F, altitude=101.0)),
                  snap(ut=3.0, **self.F)]
        _, counts = self._run(frames)
        self.assertEqual(counts, [0, 1, 1, 2])

    def test_backwards_ut_holds_and_rebaselines(self):
        uts = (10.0, 11.0, 12.0, 5.0, 6.0, 7.0)
        sig, counts = self._run([snap(ut=u, **self.F) for u in uts])
        self.assertEqual(counts, [0, 1, 2, 2, 3, 4])
        self.assertEqual(sig[0], 7.0)

    def test_non_finite_ut_holds(self):
        frames = [snap(ut=1.0, **self.F), snap(ut=2.0, **self.F),
                  snap(ut=float("nan"), **self.F), snap(ut=3.0, **self.F)]
        _, counts = self._run(frames)
        self.assertEqual(counts, [0, 1, 1, 2])

    def test_warp_still_holds(self):
        frames = [snap(ut=1.0, **self.F), snap(ut=2.0, **self.F),
                  snap(ut=50.0, warp_mode="RAILS", warp_rate=100.0,
                       **dict(self.F, apoapsis=9999.0)),
                  snap(ut=51.0, **self.F)]
        sig, counts = self._run(frames)
        self.assertEqual(counts, [0, 1, 1, 2])

    def test_a_paused_game_never_increments(self):
        # The paused-clock watchdog owns a stopped clock: the detector neither
        # trips nor restarts across it.
        frames = [snap(ut=1.0, **self.F), snap(ut=2.0, **self.F)]
        frames += [snap(ut=2.0, **self.F) for _ in range(40)]
        _, counts = self._run(frames, limit=3)
        self.assertEqual(max(counts), 1)
        self.assertEqual(counts[-1], 1)

    def test_an_advancing_field_change_still_resets(self):
        frames = [snap(ut=1.0, **self.F), snap(ut=2.0, **self.F),
                  snap(ut=3.0, **dict(self.F, altitude=99.0))]
        _, counts = self._run(frames)
        self.assertEqual(counts, [0, 1, 0])


class ToleranceCompareTests(unittest.TestCase):

    def test_nan_and_inf_never_match_under_a_tolerance(self):
        nan, inf = float("nan"), float("inf")
        self.assertFalse(mlib.frozen_field_matches(nan, nan, 1e-3))
        self.assertFalse(mlib.frozen_field_matches(1.0, nan, 1e-3))
        self.assertFalse(mlib.frozen_field_matches(inf, inf, 1e-3))
        self.assertTrue(mlib.frozen_field_matches(1.0, 1.0 + 1e-4, 1e-3))
        self.assertFalse(mlib.frozen_field_matches(1.0, 1.01, 1e-3))

    def test_a_malformed_tolerance_falls_back_to_exact(self):
        for bad in (0.0, -1.0, float("nan"), None):
            self.assertFalse(mlib.frozen_field_matches(1.0, 1.0 + 1e-9, bad), bad)
            self.assertTrue(mlib.frozen_field_matches(1.0, 1.0, bad), bad)

    def test_tolerance_zero_is_byte_identical_to_the_old_detector(self):
        f = dict(altitude=500.0, vertical_speed=-3.0, apoapsis=1000.0,
                 periapsis=-100.0)
        nan = float("nan")
        frames = []
        ut = 0.0
        for i in range(120):
            ut += 0.5
            kind = i % 12
            if kind in (0, 1, 2, 3):
                fields = f                                     # frozen run
            elif kind == 4:
                fields = dict(f, altitude=500.0 + 1e-9)        # sub-mm jitter
            elif kind == 5:
                fields = dict(f, periapsis=-100.0 + 1e-12)
            elif kind == 6:
                fields = dict(f, vertical_speed=-3.5)          # live change
            elif kind == 7:
                fields = dict(f, apoapsis=nan)                 # NaN field
            elif kind == 8:
                frames.append(snap(ut=ut, warp_mode="RAILS", warp_rate=50.0, **f))
                continue
            else:
                fields = f
            frames.append(snap(ut=ut, **fields))
        old_sig, old_count = None, 0
        new_sig, new_count = None, 0
        for frame in frames:
            old_sig, old_count, old_trip = _old_advance_frozen_count(
                old_sig, old_count, frame, 3)
            new_sig, new_count, new_trip = mlib._advance_frozen_count(
                new_sig, new_count, frame, 3, 0.0)
            self.assertEqual((old_count, old_trip), (new_count, new_trip), frame.ut)
            self.assertEqual(repr(old_sig), repr(new_sig), frame.ut)


class HopLivenessTests(unittest.TestCase):
    """A live hop craft never trips at the default tolerance, even in the
    worst case where the apsides read bit-identical on every poll."""

    def test_six_mps_chute_descent_never_trips(self):
        params = _b1_params()
        state = _b1_state(params, phase_entry_ut=100.0)
        frames = [snap(ut=100.0 + 0.5 * i, altitude=2000.0 - 3.0 * i,
                       vertical_speed=-6.0, apoapsis=1500.0, periapsis=-590000.0,
                       situation="FLYING")
                  for i in range(300)]
        state = _drive(mlib.b1_decide, state, frames)
        self.assertFalse(state.done, state.loss_reason)
        self.assertEqual(state.frozen_count, 0)

    def test_apex_coast_with_vspd_crossing_zero_never_trips(self):
        # Polls straddle the apex symmetrically, so the two apex polls read the
        # SAME altitude bit for bit; only the vertical speed differs.
        g, h0 = 9.81, 8000.0
        params = _b1_params()
        state = _b1_state(params, phase=mlib.B1_COAST, phase_entry_ut=180.0)
        frames = []
        for i in range(-20, 20):
            t = 0.5 * i + 0.25
            frames.append(snap(ut=200.0 + t, altitude=h0 - 0.5 * g * t * t,
                               vertical_speed=-g * t, apoapsis=h0,
                               periapsis=-590000.0, situation="FLYING"))
        self.assertEqual(frames[19].altitude, frames[20].altitude)
        state = _drive(mlib.b1_decide, state, frames)
        self.assertFalse(state.done, state.loss_reason)
        self.assertEqual(state.frozen_count, 0)


class TolerancePlacementTests(unittest.TestCase):

    def test_hop_builders_default_the_tolerance_on(self):
        hop = mlib.HOP_FROZEN_TOLERANCE_ABS
        self.assertEqual(mlib.b1_params_from_dict(dict(_B1_DICT)).frozen_tolerance_abs, hop)
        self.assertEqual(mlib.eva4_params_from_dict({}).frozen_tolerance_abs, hop)
        self.assertEqual(mlib.gs1_params_from_dict({}).frozen_tolerance_abs, hop)
        self.assertEqual(mlib.b4_params_from_dict({}).frozen_tolerance_abs, hop)
        self.assertEqual(
            mlib.gs1_params_from_dict({"frozenToleranceAbs": 0.0}).frozen_tolerance_abs,
            0.0)

    def test_b4_applies_it_only_in_the_descent(self):
        state = mlib.b4_initial_state(mlib.b4_params_from_dict({}))
        for phase in (mlib.B4_MJ_ASCENT, mlib.B4_CIRCULARIZE, mlib.B4_ORBIT,
                      mlib.B4_DEORBIT):
            self.assertEqual(mlib.frozen_tolerance_for_state(
                replace(state, phase=phase)), 0.0, phase)
        for phase in (mlib.B4_REENTRY, mlib.B4_SPLASHDOWN):
            self.assertEqual(mlib.frozen_tolerance_for_state(
                replace(state, phase=phase)), mlib.HOP_FROZEN_TOLERANCE_ABS, phase)

    def test_orbit_machines_stay_exact(self):
        b2 = mlib.b2_initial_state(mlib.b2_params_from_dict({}))
        b5 = mlib.b5_initial_state(mlib.b5_params_from_dict({
            "targetApoapsisMeters": 80000, "targetPeriapsisMeters": 80000,
            "apoErrorMeters": 5000, "periErrorMeters": 5000,
            "ascentTimeoutSeconds": 420, "circularizeTimeoutSeconds": 300}))
        self.assertEqual(mlib.frozen_tolerance_for_state(b2), 0.0)
        self.assertEqual(mlib.frozen_tolerance_for_state(b5), 0.0)
        self.assertFalse(hasattr(b2.params, "frozen_tolerance_abs"))
        self.assertFalse(hasattr(b5.params, "frozen_tolerance_abs"))


class MachineLineTests(unittest.TestCase):

    def test_sbr_machine_line_prints_the_nested_frozen_count(self):
        params = mlib.sbr_params_from_dict(dict(_B1_DICT))
        state = mlib.sbr_initial_state(params)
        state = replace(state, flight=replace(state.flight, frozen_count=7))
        self.assertFalse(hasattr(state, "frozen_count"))
        line = mlib.format_machine_state(state, ut=1.0)
        self.assertIn(" frozenCount=7 ", line)
        self.assertEqual(mlib.machine_state_dict(state)["frozenCount"], 7)

    def test_r1_machine_line_prints_the_nested_frozen_count(self):
        params = mlib.r1_params_from_dict({})
        state = mlib.r1_initial_state(params)
        state = replace(state, ascent=replace(state.ascent, frozen_count=3))
        self.assertIn(" frozenCount=3 ", mlib.format_machine_state(state))

    def test_a_machine_without_a_detector_still_prints_a_dash(self):
        state = mlib.cl1_initial_state(mlib.cl1_params_from_dict({}))
        self.assertIsNone(mlib.frozen_detector_holder(state))
        self.assertIn(" frozenCount=- ", mlib.format_machine_state(state))


class FrozenNoteTests(unittest.TestCase):
    PREV = (10.0, -2.778, -230.304, 2792.922, -598378.849)

    def test_reset_note_names_the_field_with_repr_values(self):
        curr = (10.5, -2.778 + 1e-9, -230.304, 2792.922, -598378.849)
        note = mlib.frozen_count_note(self.PREV, 4, curr, 0, 0.0, "NONE")
        self.assertIsNotNone(note)
        self.assertIn("frozen-telemetry reset count=4->0 field=altitude ", note)
        self.assertIn("altitude=%r->%r" % (-2.778, -2.778 + 1e-9), note)
        self.assertIn("dUt=0.5 warp=NONE tol=0.0", note)
        self.assertNotIn("apoapsis=", note)

    def test_within_tolerance_note_names_the_inexact_field(self):
        curr = (10.5, -2.778, -230.304, 2792.922, -598378.849 + 1e-9)
        note = mlib.frozen_count_note(self.PREV, 4, curr, 5, 1e-3, "NONE")
        self.assertIn("frozen-telemetry within-tolerance count=4->5 "
                      "field=periapsis ", note)
        self.assertIn("tol=0.001", note)

    def test_a_healthy_poll_and_an_exact_freeze_are_silent(self):
        live = (10.5, 12.0, -230.304, 2792.922, -598378.849)
        self.assertIsNone(mlib.frozen_count_note(self.PREV, 0, live, 0, 1e-3, "NONE"))
        exact = (10.5,) + self.PREV[1:]
        self.assertIsNone(mlib.frozen_count_note(self.PREV, 2, exact, 3, 1e-3, "NONE"))
        self.assertIsNone(mlib.frozen_count_note(None, 0, exact, 0, 0.0, "NONE"))

    def test_note_resolves_the_nested_sbr_counter(self):
        params = mlib.sbr_params_from_dict(dict(_B1_DICT))
        prev = mlib.sbr_initial_state(params)
        prev = replace(prev, flight=replace(prev.flight, phase=mlib.B1_DESCENT,
                                            frozen_sig=self.PREV, frozen_count=3))
        new = replace(prev, flight=replace(prev.flight, frozen_count=0))
        frame = snap(ut=10.5, altitude=-2.0, vertical_speed=-230.304,
                     apoapsis=2792.922, periapsis=-598378.849)
        note = mlib.frozen_detector_note(prev, new, frame)
        self.assertIn("reset count=3->0 field=altitude", note)
        self.assertIn("tol=0.001", note)


class _FakeControl(mission_runner.MissionControl):
    def __init__(self, snaps):
        self._snaps = list(snaps)
        self._i = 0
        self.client_version = "0.5.4"
        self.server_version = "0.5.4"

    def open(self, host, rpc_port, stream_port):
        pass

    def read_snapshot(self):
        snap_ = self._snaps[min(self._i, len(self._snaps) - 1)]
        self._i += 1
        return snap_

    def perform(self, action):
        pass

    def close(self):
        pass


class FlyLoopNoteTests(unittest.TestCase):

    def _fly(self, frames, state):
        lines = []
        clock = lambda: 0.0  # noqa: E731
        log = mission_runner.MissionLogger(sink=lines.append, clock=clock)
        final, _ = mission_runner.fly_loop(
            _FakeControl(frames), state, mlib.b1_decide, log, deadline=1e9,
            clock=clock, sleep=lambda _s: None, poll_interval=0.0,
            settle_frames=0)
        return final, [l for l in lines if "frozen-telemetry" in l]

    def test_rb1_crash_logs_the_inexact_fields_then_trips(self):
        final, notes = self._fly(_rb1_frames(40), _b1_state(_b1_params()))
        self.assertEqual(final.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertEqual(len(notes), 10)
        self.assertIn("[Mission][Info][DESCENT] frozen-telemetry within-tolerance "
                      "count=0->1 field=altitude,vertical_speed,apoapsis,periapsis",
                      notes[0])

    def test_exact_mode_logs_a_reset_out_of_a_frozen_run(self):
        f = dict(_RB1)
        frames = [snap(ut=100.0 + 0.5 * i, situation="FLYING", **f) for i in range(4)]
        frames.append(snap(ut=102.0, situation="FLYING",
                           **dict(f, vertical_speed=-230.304 + 1e-9)))
        frames += [snap(ut=102.5 + 0.5 * i, situation="LANDED", **f) for i in range(2)]
        final, notes = self._fly(frames, _b1_state(_b1_params(frozenToleranceAbs=0.0)))
        self.assertEqual(len(notes), 1, notes)
        self.assertIn("frozen-telemetry reset count=3->0 field=vertical_speed "
                      "vertical_speed=-230.304->", notes[0])

    def test_notes_are_capped_per_flight(self):
        # Alternate a frozen pair with an inexact reset so every other poll is
        # a note under the exact compare; the cap bounds the flood.
        f = dict(_RB1)
        frames = []
        for i in range(200):
            alt = -2.778 if (i // 2) % 2 == 0 else -2.778 + 1e-9
            frames.append(snap(ut=100.0 + 0.5 * i, situation="FLYING",
                               **dict(f, altitude=alt)))
        frames.append(snap(ut=300.0, situation="LANDED", **f))
        _, notes = self._fly(frames, _b1_state(_b1_params(frozenToleranceAbs=0.0)))
        self.assertEqual(len(notes), mlib.FROZEN_NOTE_LIMIT + 1)
        self.assertIn("further notes suppressed", notes[-1])


if __name__ == "__main__":
    unittest.main()
