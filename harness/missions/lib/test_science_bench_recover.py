"""Unit cells for the CAREER-EARNING ATOM (mission `science_bench_recover`) and
for the three capability verbs it is built on: run science, transmit science,
recover the vessel.

Four groups, each guarding a different failure mode:

  1. THE PURE MACHINE (`mlib.sbr_decide` / `evaluate_sbr_assertions`). Both
     directions of every gate, the delegation to B1, and each of the five named
     wrong-outcome terminals and three named flakes (two channel, one driver)
     - because a
     deterministic outcome filed as an unnamed budget timeout is the exact defect
     class this library keeps paying for.
  2. THE SHELL, driven end to end over a scripted flight with no krpc and no KSP.
  3. THE RUNNER'S NEW CHANNELS AND VERBS. The reads that decide everything
     (experiment enumeration, career pools, Vessel.Recoverable) and the three
     perform branches, including the two properties that cost a flight to get
     wrong: the career pools survive a vessel_lost frame, and the recover verb
     never raises out of `perform`.
  4. SCHEMA / CONTRACT SYNC. The schema declares exactly the keys the pure params
     builder reads, the handoff contract says what this mission does not verify,
     and the six new snapshot channels stay out of the shared status surfaces.

NO krpc, NO KSP, NO network. Import path matches the sibling suites: discovery
runs from `harness/` with `missions/lib` as the root, and `missions/` is
prepended so `import mission_runner` / `import science_bench_recover` resolve.
"""

import dataclasses
import math
import os
import subprocess
import sys
import tomllib
import types
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
_REPO_ROOT = os.path.dirname(_HARNESS)                   # the repo checkout
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if os.path.join(_HARNESS, "lib") not in sys.path:
    sys.path.insert(0, os.path.join(_HARNESS, "lib"))

import mlib                        # noqa: E402
import mission_runner              # noqa: E402
import science_bench_recover       # noqa: E402
from test_shells import FakeMissionControl, run, snap  # noqa: E402

SCHEMA_PATH = os.path.join(_MISSIONS, "science_bench_recover.schema.toml")

PARAMS = {
    # Flight half (B1's keys verbatim).
    "throttle": 1.0,
    "apoapsisWindowMeters": {"min": 6000, "max": 30000},
    "chuteArmMaxRateMps": 30,
    "chuteFullDeployAltMeters": 2500,
    "landedSituations": ["LANDED", "SPLASHED"],
    "ascentTimeoutSeconds": 90,
    "coastTimeoutSeconds": 180,
    "descentTimeoutSeconds": 360,
    # Career half.
    "collectMinExperiments": 2,
    "collectTimeoutSeconds": 120,
    "transmitMinScienceGain": 1.0,
    "transmitTimeoutSeconds": 120,
    "recoverMinFundsGain": 100.0,
    "recoverTimeoutSeconds": 120,
}


def params(**over):
    out = dict(PARAMS)
    out.update(over)
    return out


def sci(**kw):
    """A LIVE snapshot with the science lane armed: the six career channels
    default to READABLE-and-benign here (rather than to the UNREAD sentinels a
    bare TelemetrySnapshot carries) so a cell only writes the field it exercises.
    Anything a cell wants dark it passes explicitly."""
    base = dict(science_experiment_count=2, science_data_count=0,
                science_available_count=2, vessel_recoverable=mlib.RECOVERABLE_YES,
                career_funds=1000.0, career_science=10.0)
    base.update(kw)
    return snap(**base)


def lost(ut, **kw):
    """A vessel_lost snapshot carrying the perform seam's ISSUED stamp - i.e. the
    frame the runner really emits AFTER a recovery it actually issued.

    The stamp belongs on this frame rather than on an earlier live one because
    that is where it genuinely lands: a successful `Vessel.Recover()` removes the
    craft at once, so the read that fails and the result that says the verb went
    out arrive on the same snapshot. A cell that wants the OTHER shape - a craft
    that went away with no recovery issued behind it - passes a bare
    ``snap(vessel_lost=True, ...)``, which leaves the channel at its "" UNREAD
    sentinel and is exactly what a break-up looks like."""
    base = dict(vessel_lost=True,
                recover_request_result=mlib.RECOVER_REQUEST_ISSUED)
    base.update(kw)
    return snap(ut=ut, **base)


# The scripted pad hop that drives the delegated B1 sub-machine to LANDED. Copied
# in SHAPE from the proven B1 happy path in test_shells (fuel burn -> apex -> arm
# -> canopy -> landed), with the science channels armed on every frame.
FLIGHT_FRAMES = [
    sci(ut=0.0, stage_solid_fuel=1.0, apoapsis=14000, situation="PRE_LAUNCH"),
    sci(ut=1.0, stage_solid_fuel=0.5, apoapsis=14000, situation="FLYING"),
    sci(ut=2.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING"),
    sci(ut=3.0, vertical_speed=5.0, apoapsis=14000, situation="FLYING"),
    sci(ut=4.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING"),
    sci(ut=5.0, altitude=5000, apoapsis=14000, situation="FLYING",
        craft_chute_state=mlib.CHUTE_STATE_SEMI_DEPLOYED),
    sci(ut=6.0, altitude=2000, apoapsis=14000, situation="FLYING",
        craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
]

LANDED_FRAME = sci(ut=7.0, altitude=100, apoapsis=14000, situation="LANDED",
                   craft_chute_state=mlib.CHUTE_STATE_DEPLOYED)


def fresh(**over):
    return mlib.sbr_initial_state(mlib.sbr_params_from_dict(params(**over)))


def flown(**over):
    """An SBR machine driven through the delegated flight leg to the frame that
    lands it, i.e. sitting in COLLECT with the sweep already issued."""
    state = fresh(**over)
    actions = []
    for frame in FLIGHT_FRAMES:
        state, actions = mlib.sbr_decide(state, frame)
    state, actions = mlib.sbr_decide(state, LANDED_FRAME)
    return state, actions


def _snap_fields(snapshot):
    """The snapshot's fields as a dict, so a cell can re-issue a scripted frame
    with one channel overridden."""
    from dataclasses import asdict
    return asdict(snapshot)


def drive(state, *frames):
    """Feed a run of frames as consecutive polls; return the final state."""
    for frame in frames:
        state, _ = mlib.sbr_decide(state, frame)
    return state


def drive_actions(state, *frames):
    """Feed frames and return (state, every action emitted across them)."""
    emitted = []
    for frame in frames:
        state, actions = mlib.sbr_decide(state, frame)
        emitted.extend(actions)
    return state, emitted


def in_transmit(**over):
    """An SBR machine parked at TRANSMIT entry: flown, landed, data collected,
    science pool readable at 10.0 and not yet moved."""
    state, _ = flown(**over)
    return drive(state,
                 sci(ut=8.0, situation="LANDED", science_data_count=2,
                     career_science=10.0),
                 sci(ut=9.0, situation="LANDED", science_data_count=2,
                     career_science=10.0))


def _asked(state, ut=12.0):
    """Drive the two debounced Recoverable=YES frames that make the machine EMIT
    the recover verb. The perform seam has said nothing yet, so `recover_issued`
    is still false on the way out - the caller decides what the seam reports."""
    return drive(state,
                 sci(ut=ut, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                 sci(ut=ut + 1.0, situation="LANDED",
                     vessel_recoverable=mlib.RECOVERABLE_YES))


def in_recover(**over):
    """An SBR machine parked at RECOVER entry: the science credit has landed and
    the funds baseline is frozen at 1000.0. The recovery has NOT been asked for -
    the phase has to OBSERVE Vessel.Recoverable first."""
    return drive(in_transmit(**over),
                 sci(ut=10.0, situation="LANDED", career_science=25.0),
                 sci(ut=11.0, situation="LANDED", career_science=25.0))


# ---------------------------------------------------------------------------
# 1. The pure machine.
# ---------------------------------------------------------------------------


class SbrFlightDelegationTests(unittest.TestCase):
    """The flight leg is B1's, and it has to STAY B1's."""

    def test_the_machine_starts_inside_b1s_own_prelaunch(self):
        state = fresh()
        self.assertEqual(mlib.B1_PRELAUNCH, state.phase)
        self.assertEqual(mlib.B1_PRELAUNCH, state.flight.phase)

    def test_the_first_frame_emits_b1s_ignition_verbatim(self):
        # If this ever stops matching b1_decide's PRELAUNCH actions the delegation
        # has been forked, which is the thing composition exists to prevent.
        state, actions = mlib.sbr_decide(fresh(), FLIGHT_FRAMES[0])
        self.assertEqual([mlib.ACTION_SET_THROTTLE, mlib.ACTION_ACTIVATE_STAGE],
                         [a.kind for a in actions])
        self.assertEqual(mlib.B1_ASCENT, state.phase)

    def test_the_phase_mirrors_the_sub_machine_every_frame(self):
        state = fresh()
        for frame in FLIGHT_FRAMES:
            state, _ = mlib.sbr_decide(state, frame)
            self.assertEqual(state.flight.phase, state.phase)

    def test_the_landing_frame_falls_through_into_collect_in_the_same_frame(self):
        # Deliberate: the craft is at rest and its situation is settled, so
        # waiting a poll to start collecting buys nothing and costs budget.
        state, actions = flown()
        self.assertEqual(mlib.SBR_COLLECT, state.phase)
        self.assertIn(mlib.ACTION_RUN_SCIENCE_EXPERIMENTS, [a.kind for a in actions])
        self.assertIn(mlib.B1_LANDED, state.phases_reached)
        self.assertIn(mlib.SBR_COLLECT, state.phases_reached)

    def test_a_flight_leg_flake_ends_the_mission_naming_the_leg(self):
        # A pad hop that never burns its fuel runs the ASCENT budget out. The
        # career tail must not swallow that or re-name it.
        state = fresh(ascentTimeoutSeconds=5)
        state = drive(state, *[sci(ut=float(i), stage_solid_fuel=1.0, situation="FLYING")
                               for i in range(12)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertTrue(state.flake_reason.startswith("flight-leg "), state.flake_reason)
        self.assertNotIn(mlib.SBR_COLLECT, state.phases_reached)

    def test_a_flight_leg_assert_fail_carries_its_reason_verbatim(self):
        # The chute-arm window missed: B1's own named ASSERT-FAIL.
        state = fresh()
        state = drive(state,
                      sci(ut=0.0, stage_solid_fuel=1.0, situation="PRE_LAUNCH"),
                      sci(ut=1.0, stage_solid_fuel=0.0, situation="FLYING"),
                      sci(ut=2.0, vertical_speed=-200.0, altitude=8000, situation="FLYING"),
                      sci(ut=3.0, vertical_speed=-210.0, altitude=1000, situation="FLYING"),
                      sci(ut=4.0, vertical_speed=-220.0, altitude=900, situation="FLYING"))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("chute-arm-window-missed", state.loss_reason)
        self.assertTrue(state.loss_reason.startswith("flight-leg "), state.loss_reason)

    def test_b1s_own_DOWN_success_is_this_missions_dead_end(self):
        # DOWN means the craft is GONE after a verified canopy: B1's success and
        # SBR's dead end, because there is nothing left to recover. It must be
        # NAMED, not allowed to fall into the career tail.
        flight = mlib.B1State(params=mlib.b1_params_from_dict(params()),
                              phase=mlib.B1_DOWN, done=True)
        carried = mlib._sbr_carry_flight_terminal(fresh(), flight)
        self.assertTrue(carried.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, carried.verdict)
        self.assertIn("no recoverable craft", carried.loss_reason)
        self.assertIn(mlib.B1_DOWN, carried.loss_reason)


class SbrCollectTests(unittest.TestCase):
    """COLLECT: run the experiments, wait for OBSERVED stored data."""

    def test_the_observed_data_count_advances_the_machine(self):
        state, _ = flown()
        state, actions = drive_actions(
            state,
            sci(ut=8.0, situation="LANDED", science_data_count=2),
            sci(ut=9.0, situation="LANDED", science_data_count=2))
        self.assertEqual(mlib.SBR_TRANSMIT, state.phase)
        self.assertIn(mlib.ACTION_TRANSMIT_SCIENCE, [a.kind for a in actions])

    def test_one_ready_frame_does_not_advance(self):
        state, _ = flown()
        state = drive(state, sci(ut=8.0, situation="LANDED", science_data_count=2))
        self.assertEqual(mlib.SBR_COLLECT, state.phase)
        self.assertEqual(1, state.data_ready_streak)

    def test_a_disagreeing_frame_resets_the_ready_streak(self):
        state, _ = flown()
        state = drive(state,
                      sci(ut=8.0, situation="LANDED", science_data_count=2),
                      sci(ut=9.0, situation="LANDED", science_data_count=1),
                      sci(ut=10.0, situation="LANDED", science_data_count=2))
        self.assertEqual(mlib.SBR_COLLECT, state.phase)
        self.assertEqual(1, state.data_ready_streak)

    def test_a_count_under_the_required_minimum_never_advances(self):
        state, _ = flown()
        state = drive(state, *[sci(ut=8.0 + i, situation="LANDED", science_data_count=1)
                               for i in range(6)])
        self.assertEqual(mlib.SBR_COLLECT, state.phase)

    def test_an_unread_data_count_is_not_read_as_zero_or_as_ready(self):
        # -1 is UNREAD. It must satisfy nothing, and it must not be compared as a
        # number against the required minimum either.
        state, _ = flown(collectMinExperiments=1)
        state = drive(state,
                      sci(ut=8.0, situation="LANDED",
                          science_data_count=mlib.SCIENCE_COUNT_UNREAD),
                      sci(ut=9.0, situation="LANDED",
                          science_data_count=mlib.SCIENCE_COUNT_UNREAD))
        self.assertEqual(mlib.SBR_COLLECT, state.phase)
        self.assertEqual(0, state.data_ready_streak)

    def test_an_observed_empty_craft_is_named_no_experiments_aboard(self):
        state, _ = flown()
        state = drive(state,
                      sci(ut=8.0, situation="LANDED", science_experiment_count=0,
                          science_data_count=0, science_available_count=0),
                      sci(ut=9.0, situation="LANDED", science_experiment_count=0,
                          science_data_count=0, science_available_count=0))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("no-experiments-aboard", state.loss_reason)

    def test_one_empty_reading_does_not_condemn(self):
        state, _ = flown()
        state = drive(state, sci(ut=8.0, situation="LANDED", science_experiment_count=0))
        self.assertFalse(state.done)

    def test_an_unread_enumeration_is_never_the_empty_craft_terminal(self):
        # THE SPLIT THIS LANE EXISTS TO KEEP: -1 blames the CHANNEL (a retryable
        # flake), 0 blames the FIXTURE (a non-retryable assert-fail). Collapsing
        # them files one as the other.
        state, _ = flown()
        state = drive(state, *[sci(ut=8.0 + i, situation="LANDED",
                                   science_experiment_count=mlib.SCIENCE_COUNT_UNREAD)
                               for i in range(mlib.SBR_SCIENCE_UNREAD_GIVEUP_FRAMES)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("science-channel-dark", state.flake_reason)
        self.assertEqual(0, state.no_experiments_streak)

    def test_a_transient_unread_frame_does_not_trip_the_dark_channel_give_up(self):
        state, _ = flown()
        frames = []
        for i in range(mlib.SBR_SCIENCE_UNREAD_GIVEUP_FRAMES * 3):
            dark = (i % 3 != 0)
            frames.append(sci(ut=8.0 + i, situation="LANDED",
                              science_experiment_count=(mlib.SCIENCE_COUNT_UNREAD
                                                        if dark else 2)))
        state = drive(state, *frames)
        self.assertFalse(state.done)

    def test_the_collect_budget_names_what_it_saw(self):
        state, _ = flown(collectTimeoutSeconds=10)
        state = drive(state, *[sci(ut=8.0 + i * 5, situation="LANDED",
                                   science_data_count=0, science_available_count=0)
                               for i in range(5)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("collect-produced-no-data", state.loss_reason)
        # The availability reading is what separates "the modules refused" from
        # "the conditions were never met", so the reason must carry it.
        self.assertIn("available=0", state.loss_reason)

    def test_the_sweep_is_re_emitted_bounded_while_the_gate_is_unmet(self):
        state, actions = flown()
        emitted = [a for a in actions if a.kind == mlib.ACTION_RUN_SCIENCE_EXPERIMENTS]
        self.assertEqual(1, len(emitted))
        state, more = drive_actions(
            state, *[sci(ut=8.0 + i, situation="LANDED", science_data_count=0)
                     for i in range(mlib.SBR_ACTION_REEMIT_FRAMES
                                    * (mlib.SBR_ACTION_REEMIT_LIMIT + 3))])
        runs = [a for a in more if a.kind == mlib.ACTION_RUN_SCIENCE_EXPERIMENTS]
        self.assertEqual(mlib.SBR_ACTION_REEMIT_LIMIT, len(runs),
                         "the re-emit allowance is a BOUND; an unbounded sweep would "
                         "issue one RPC storm per poll for the whole collect budget")


class SbrTransmitTests(unittest.TestCase):
    """TRANSMIT: send the data, wait for the OBSERVED career-pool credit."""

    # Delegates to the module-level helper so sibling classes need it too
    # without instantiating this TestCase.
    def _in_transmit(self, **over):
        return in_transmit(**over)

    def test_the_baseline_is_the_last_pre_transmit_reading(self):
        state = self._in_transmit()
        self.assertEqual(mlib.SBR_TRANSMIT, state.phase)
        self.assertEqual(10.0, state.science_baseline)

    def test_the_baseline_freezes_once_transmit_starts(self):
        # Otherwise the pool chases itself and the gain is always zero: the credit
        # arrives DURING this phase, and a baseline that kept tracking would erase
        # the very movement the gate exists to see.
        state = self._in_transmit()
        state = drive(state, sci(ut=10.0, situation="LANDED", science_data_count=0,
                                 career_science=25.0))
        self.assertEqual(10.0, state.science_baseline)
        self.assertEqual(25.0, state.last_science)

    def test_an_observed_credit_advances_to_recover(self):
        state = self._in_transmit()
        state, actions = drive_actions(
            state,
            sci(ut=10.0, situation="LANDED", science_data_count=0, career_science=25.0),
            sci(ut=11.0, situation="LANDED", science_data_count=0, career_science=25.0))
        self.assertEqual(mlib.SBR_RECOVER, state.phase)
        # RECOVER must NOT ask on entry: it has to OBSERVE Vessel.Recoverable
        # first, because kRPC's Recover() throws when it is false.
        self.assertEqual([], [a for a in actions if a.kind == mlib.ACTION_RECOVER_VESSEL])

    def test_a_credit_under_the_floor_never_advances(self):
        state = self._in_transmit(transmitMinScienceGain=5.0)
        state = drive(state, *[sci(ut=10.0 + i, situation="LANDED", career_science=12.0)
                               for i in range(6)])
        self.assertEqual(mlib.SBR_TRANSMIT, state.phase)

    def test_one_credited_frame_does_not_advance(self):
        state = self._in_transmit()
        state = drive(state, sci(ut=10.0, situation="LANDED", career_science=25.0))
        self.assertEqual(mlib.SBR_TRANSMIT, state.phase)
        self.assertEqual(1, state.science_gain_streak)

    def test_the_transmit_phase_re_emits_the_TRANSMIT_verb_not_the_run_verb(self):
        # The re-emit helper is shared between COLLECT and TRANSMIT and takes the
        # action kind as an argument, so a swapped constant at the TRANSMIT call
        # site would re-run the experiments forever while the credit never lands -
        # and every other cell in this file would stay green.
        state = in_transmit()
        state, emitted = drive_actions(
            state, *[sci(ut=10.0 + i, situation="LANDED", career_science=10.0)
                     for i in range(mlib.SBR_ACTION_REEMIT_FRAMES
                                    * (mlib.SBR_ACTION_REEMIT_LIMIT + 1))])
        kinds = {a.kind for a in emitted}
        self.assertEqual({mlib.ACTION_TRANSMIT_SCIENCE}, kinds)
        self.assertEqual(mlib.SBR_ACTION_REEMIT_LIMIT,
                         len([a for a in emitted
                              if a.kind == mlib.ACTION_TRANSMIT_SCIENCE]))

    def test_an_uncredited_transmit_is_named_not_left_as_a_timeout(self):
        state = self._in_transmit(transmitTimeoutSeconds=10)
        state = drive(state, *[sci(ut=10.0 + i * 5, situation="LANDED",
                                   career_science=10.0) for i in range(5)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("transmit-credited-no-science", state.loss_reason)

    def test_a_pool_that_never_read_finite_is_a_channel_flake_not_an_assert_fail(self):
        # A SANDBOX save reads exactly like this, and so does a drifted kRPC
        # surface. Neither is a wrong flight outcome, so neither may be
        # non-retryable.
        # The pools must be dark from the FIRST frame: a baseline captured earlier
        # would make the gain computable and this would be the assert-fail instead.
        state = fresh(transmitTimeoutSeconds=10)
        for frame in FLIGHT_FRAMES + [LANDED_FRAME]:
            state, _ = mlib.sbr_decide(
                state, snap(**dict(_snap_fields(frame),
                                   career_science=float("nan"),
                                   career_funds=float("nan"))))
        state = drive(state,
                      sci(ut=8.0, situation="LANDED", science_data_count=2,
                          career_science=float("nan")),
                      sci(ut=9.0, situation="LANDED", science_data_count=2,
                          career_science=float("nan")))
        self.assertEqual(mlib.SBR_TRANSMIT, state.phase)
        self.assertIsNone(state.science_baseline)
        state = drive(state, *[sci(ut=10.0 + i * 5, situation="LANDED",
                                   career_science=float("nan")) for i in range(5)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("career-pool-channel-dark", state.flake_reason)

    def test_the_collect_evidence_survives_the_transmit_emptying_the_modules(self):
        # data_count_max is a MAX, not the latest reading: a transmit empties the
        # modules, and the latest reading would erase the very evidence the
        # scienceCollectedObserved row reports.
        state = self._in_transmit()
        state = drive(state, sci(ut=10.0, situation="LANDED", science_data_count=0))
        self.assertEqual(2, state.data_count_max)


class SbrRecoverTests(unittest.TestCase):
    """RECOVER: ask once, then require the craft GONE and the funds RISEN."""

    def _in_recover(self, **over):
        return in_recover(**over)

    def test_the_verb_is_emitted_once_after_a_debounced_recoverable_reading(self):
        state = self._in_recover()
        self.assertEqual(mlib.SBR_RECOVER, state.phase)
        state, actions = drive_actions(
            state,
            sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
            sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        self.assertEqual([mlib.ACTION_RECOVER_VESSEL], [a.kind for a in actions])
        self.assertEqual(1, state.recover_asks)
        # EMISSION IS NOT ISSUE. Nothing has come back from the perform seam yet,
        # so the latch the vessel-loss carve-out reads is still false - which is
        # the whole of the second review finding.
        self.assertFalse(state.recover_issued)

    def test_the_issued_latch_is_set_by_the_seam_not_by_the_emission(self):
        state = _asked(self._in_recover())
        self.assertFalse(state.recover_issued)
        state = drive(state, sci(ut=14.0, situation="LANDED",
                                 recover_request_result=mlib.RECOVER_REQUEST_ISSUED))
        self.assertTrue(state.recover_issued)
        self.assertFalse(state.recover_declined)

    def test_exactly_one_recover_verb_is_ever_emitted(self):
        # HARD REQUIREMENT, not tidiness: stock recovery removes the craft and
        # leaves FLIGHT, so a second action would be performed against a vessel
        # that no longer resolves - and the fly loop does NOT wrap perform(), so
        # the raise would report MISSION-ERROR instead of the RECOVERED it was.
        state = self._in_recover()
        state, actions = drive_actions(
            state, *[sci(ut=12.0 + i, situation="LANDED",
                         vessel_recoverable=mlib.RECOVERABLE_YES) for i in range(60)])
        self.assertEqual(1, len([a for a in actions if a.kind == mlib.ACTION_RECOVER_VESSEL]))

    def test_an_unread_recoverable_channel_never_asks(self):
        state = self._in_recover()
        state, actions = drive_actions(
            state, *[sci(ut=12.0 + i, situation="LANDED",
                         vessel_recoverable=mlib.RECOVERABLE_UNREAD) for i in range(10)])
        self.assertEqual([], actions)
        self.assertFalse(state.recover_issued)

    def test_an_observed_unrecoverable_craft_is_named_before_kRPC_can_throw(self):
        state = self._in_recover()
        state = drive(state,
                      sci(ut=12.0, situation="FLYING", vessel_recoverable=mlib.RECOVERABLE_NO),
                      sci(ut=13.0, situation="FLYING", vessel_recoverable=mlib.RECOVERABLE_NO))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-not-recoverable", state.loss_reason)

    def test_one_unrecoverable_reading_does_not_condemn(self):
        state = self._in_recover()
        state = drive(state, sci(ut=12.0, situation="FLYING",
                                 vessel_recoverable=mlib.RECOVERABLE_NO))
        self.assertFalse(state.done)

    def test_the_craft_gone_plus_the_funds_risen_is_the_success_terminal(self):
        state = self._in_recover()
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        self.assertEqual(1000.0, state.funds_baseline)
        state = drive(state, lost(14.0, career_funds=1500.0,
                                  career_science=25.0))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)
        self.assertTrue(state.done)
        self.assertIsNone(state.verdict)
        self.assertTrue(state.skip_settle_tail)

    def test_a_break_up_BEFORE_the_request_is_a_loss_not_a_recovery(self):
        # THE BLOCKER, half one. RECOVER spends its first polls waiting for a
        # debounced Vessel.Recoverable reading. A craft that explodes in that
        # window was destroyed before anything was asked for, and routing it into
        # the success branch (which the phase-only carve-out did) certified a
        # break-up. Note the funds are readable and unchanged throughout, which is
        # exactly the shape that made it green at a 0.0 floor.
        state = self._in_recover(recoverMinFundsGain=0.0)
        self.assertFalse(state.recover_issued)
        state = drive(state, snap(ut=14.0, vessel_lost=True, career_funds=1000.0))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-before-recovery", state.loss_reason)
        self.assertNotEqual(mlib.SBR_RECOVERED, state.phase)

    # --- the DECLINED ask, and the hole it used to open -------------------
    #
    # THE REVIEWER'S SCENARIO (2026-08-19), replayed as a pair. The runner's
    # read-before-ask lock can decline the verb - a craft still rolling reads
    # Recoverable false for a moment - and with an EMISSION latch nothing told
    # the machine. The latch stayed true through a recovery that never happened,
    # so a craft that then exploded was scoped into the success branch, and at
    # `recoverMinFundsGain = 0.0` (the value the schema allows and the promoted
    # smoke spec used to author) the pool reading on the loss frame satisfied
    # `0.0 >= 0.0`. Same certified break-up as the straddle hole, reached by the
    # other door.

    def test_a_declined_ask_then_a_break_up_is_a_loss_not_a_recovery(self):
        state = self._in_recover(recoverMinFundsGain=0.0)
        state = _asked(state)
        self.assertEqual(1, state.recover_asks)
        # The settle flicker: the perform seam read Recoverable false and never
        # issued anything. A READABLE, UNMOVED funds pool throughout - the exact
        # shape that certified before.
        state = drive(state, sci(ut=14.0, situation="LANDED",
                                 vessel_recoverable=mlib.RECOVERABLE_NO,
                                 recover_request_result=mlib.RECOVER_REQUEST_DECLINED))
        self.assertTrue(state.recover_declined)
        self.assertFalse(state.recover_issued)
        state = drive(state, snap(ut=15.0, vessel_lost=True, career_funds=1000.0,
                                  recover_request_result=mlib.RECOVER_REQUEST_DECLINED))
        self.assertNotEqual(mlib.SBR_RECOVERED, state.phase)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-before-recovery", state.loss_reason)

    def test_the_same_frames_under_the_old_emission_latch_certified_it(self):
        """THE REPLAY NEGATIVE CONTROL for the cell above.

        The old code latched on EMISSION, which is reproduced exactly by setting
        the latch by hand on the frame the verb went out - nothing else about the
        machine or the frames changes. The identical settle-flicker-then-explosion
        run then reaches RECOVERED, which is what makes the cell above a fix
        rather than a restatement."""
        from dataclasses import replace as _replace
        state = self._in_recover(recoverMinFundsGain=0.0)
        state = _asked(state)
        # THE MUTATION, and it is the whole diff: latch on emission.
        state = _replace(state, recover_issued=True)
        state = drive(state, sci(ut=14.0, situation="LANDED",
                                 vessel_recoverable=mlib.RECOVERABLE_NO,
                                 recover_request_result=mlib.RECOVER_REQUEST_DECLINED))
        state = drive(state, snap(ut=15.0, vessel_lost=True, career_funds=1000.0,
                                  recover_request_result=mlib.RECOVER_REQUEST_DECLINED))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase,
                         "the old emission latch is what certified the break-up")
        self.assertIsNone(state.verdict)

    def test_a_declined_ask_is_re_emitted_once_the_craft_has_settled(self):
        # The machine's chosen answer to a decline: RE-EMIT, bounded, exactly
        # like the COLLECT / TRANSMIT sweeps - because the decline this is for is
        # a settle flicker and the spacing IS the settle wait. Terminating on the
        # first decline would throw away a good flight for one early poll.
        state = self._in_recover()
        state = _asked(state)
        state, actions = drive_actions(
            state, *[sci(ut=14.0 + i, situation="LANDED",
                         vessel_recoverable=mlib.RECOVERABLE_YES,
                         recover_request_result=mlib.RECOVER_REQUEST_DECLINED)
                     for i in range(mlib.SBR_ACTION_REEMIT_FRAMES)])
        self.assertEqual([mlib.ACTION_RECOVER_VESSEL], [a.kind for a in actions])
        self.assertEqual(2, state.recover_asks)

    def test_a_re_ask_that_is_ISSUED_then_certifies_normally(self):
        # End to end through the decline: flicker, re-ask, issue, recovery.
        state = self._in_recover()
        state = _asked(state)
        state = drive(state, *[sci(ut=14.0 + i, situation="LANDED",
                                   vessel_recoverable=mlib.RECOVERABLE_YES,
                                   recover_request_result=mlib.RECOVER_REQUEST_DECLINED)
                               for i in range(mlib.SBR_ACTION_REEMIT_FRAMES)])
        state = drive(state, lost(99.0, career_funds=1500.0))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)

    def test_the_re_ask_is_bounded_and_never_fires_on_seam_silence(self):
        # TWO bounds, and the silence one is the load-bearing half: an ask whose
        # result has not landed yet may still be in flight, and a second verb
        # against a craft the first one already recovered resolves a dead vessel
        # and ends the mission as MISSION-ERROR.
        silent = self._in_recover()
        silent, actions = drive_actions(
            silent, *[sci(ut=12.0 + i, situation="LANDED",
                          vessel_recoverable=mlib.RECOVERABLE_YES)
                      for i in range(mlib.SBR_ACTION_REEMIT_FRAMES * 4)])
        self.assertEqual(1, len([a for a in actions
                                 if a.kind == mlib.ACTION_RECOVER_VESSEL]))
        declined = self._in_recover(recoverTimeoutSeconds=100000)
        declined, actions = drive_actions(
            declined, *[sci(ut=12.0 + i, situation="LANDED",
                            vessel_recoverable=mlib.RECOVERABLE_YES,
                            recover_request_result=mlib.RECOVER_REQUEST_DECLINED)
                        for i in range(mlib.SBR_ACTION_REEMIT_FRAMES
                                       * (mlib.SBR_RECOVER_ASK_LIMIT + 3))])
        self.assertEqual(1 + mlib.SBR_RECOVER_ASK_LIMIT,
                         len([a for a in actions
                              if a.kind == mlib.ACTION_RECOVER_VESSEL]))

    def test_a_PERSISTENT_decline_lands_on_the_named_unrecoverable_terminal(self):
        # A decline that is not a flicker is a craft that cannot be recovered,
        # and the poll channel says so on its own - the ask block stays live
        # while nothing has been ISSUED, so the debounced NO condemns it by name
        # instead of leaving it to a budget timeout.
        state = self._in_recover()
        state = _asked(state)
        state = drive(state, *[sci(ut=14.0 + i, situation="FLYING",
                                   vessel_recoverable=mlib.RECOVERABLE_NO,
                                   recover_request_result=mlib.RECOVER_REQUEST_DECLINED)
                               for i in range(mlib.SBR_OBSERVE_DEBOUNCE_K)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-not-recoverable", state.loss_reason)

    def test_every_decline_token_keeps_the_issued_latch_closed(self):
        # The three non-issued tokens are one class by contract, so a future
        # token added to RECOVER_REQUEST_DECLINES cannot land on one side only.
        for token in mlib.RECOVER_REQUEST_DECLINES:
            state = drive(_asked(self._in_recover()),
                          sci(ut=14.0, situation="LANDED",
                              recover_request_result=token))
            self.assertFalse(state.recover_issued, token)
            self.assertTrue(state.recover_declined, token)

    def test_at_a_zero_floor_a_purely_pre_loss_funds_pair_never_certifies(self):
        # THE BLOCKER, half two. The credit must straddle the event. With the
        # post-loss pool dark, `fundsGain` is a perfectly finite 0.0 and the floor
        # is 0.0 - the old gate read that as a recovery. It must now reach the
        # grace terminal instead, because nothing was observed ACROSS the
        # recovery.
        state = self._in_recover(recoverMinFundsGain=0.0)
        state = _asked(state)
        state = drive(state, sci(ut=13.5, situation="LANDED",
                                 recover_request_result=mlib.RECOVER_REQUEST_ISSUED))
        self.assertTrue(state.recover_issued)
        self.assertEqual(0.0, mlib._sbr_funds_gain(state))
        self.assertIsNone(mlib._sbr_recovery_credit(state))
        state = drive(state, *[lost(14.0 + i,
                                    career_funds=float("nan"))
                               for i in range(mlib.SBR_RECOVER_CREDIT_GRACE_FRAMES)])
        self.assertNotEqual(mlib.SBR_RECOVERED, state.phase)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("career-pool-channel-dark", state.flake_reason)

    def test_at_a_zero_floor_a_readable_unmoved_pool_across_the_loss_still_certifies(self):
        # The complement, and it is the DOCUMENTED meaning of a 0.0 floor: "the
        # pools were readable across the recovery". With a post-loss reading in
        # hand a zero credit is a real observation, so it passes - and a spec that
        # wants the stronger claim raises the floor.
        state = self._in_recover(recoverMinFundsGain=0.0)
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        state = drive(state, lost(14.0, career_funds=1000.0))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)
        self.assertEqual(0.0, mlib._sbr_recovery_credit(state))

    def test_the_certifying_credit_is_the_one_the_assertion_reports(self):
        # A reader reconciling the row against the terminal must see the SAME
        # number, so the row reports the across-the-recovery credit, not the
        # diagnostic latest-reading gain.
        state = self._in_recover()
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        state = drive(state, lost(14.0, career_funds=1500.0))
        rows = {o.name: o for o in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(PARAMS), state)}
        self.assertEqual(mlib._sbr_recovery_credit(state),
                         rows["vesselRecoveredObserved"].value)

    def test_the_recover_budget_expiry_names_a_request_that_was_made(self):
        # The likelier live shape of `recover-never-completed`: we asked and the
        # craft never went away. Only the not-asked path was covered before.
        state = self._in_recover(recoverTimeoutSeconds=30)
        state = _asked(state)
        state = drive(state, *[sci(ut=14.0 + i * 20, situation="LANDED",
                                   vessel_recoverable=mlib.RECOVERABLE_YES,
                                   recover_request_result=mlib.RECOVER_REQUEST_ISSUED)
                               for i in range(5)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("recover-never-completed", state.flake_reason)
        self.assertIn("issued=True", state.flake_reason)

    def test_the_craft_gone_with_no_credit_is_a_break_up_not_a_recovery(self):
        # A destroyed craft is also "gone". Only the funds credit tells them apart,
        # which is why the terminal needs both conjuncts.
        state = self._in_recover()
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        state = drive(state, *[lost(14.0 + i, career_funds=1000.0)
                               for i in range(mlib.SBR_RECOVER_CREDIT_GRACE_FRAMES)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-without-recovery-credit", state.loss_reason)

    def test_a_dark_funds_channel_is_a_flake_not_a_break_up(self):
        # "The pool did not move" and "the pool was never readable" look identical
        # from a None gain and sit on OPPOSITE sides of the retry line. Condemning
        # a dark channel as a break-up would file a kRPC fault as a flight failure
        # and burn the retry that would have fixed it.
        state = self._in_recover()
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES,
                          career_funds=float("nan")),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES,
                          career_funds=float("nan")))
        # Wipe the baseline the earlier frames captured: this cell is about a
        # channel that was NEVER readable, which is the only way both halves are
        # None at the terminal.
        from dataclasses import replace as _replace
        state = _replace(state, funds_baseline=None, last_funds=None)
        state = drive(state, *[lost(14.0 + i,
                                    career_funds=float("nan"))
                               for i in range(mlib.SBR_RECOVER_CREDIT_GRACE_FRAMES)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("career-pool-channel-dark", state.flake_reason)

    def test_a_readable_pool_that_did_not_move_is_still_the_break_up_terminal(self):
        # The negative control for the cell above: with BOTH halves readable the
        # same "no credit" observation is a wrong OUTCOME, not a dark channel.
        state = self._in_recover()
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        state = drive(state, *[lost(14.0 + i, career_funds=1000.0)
                               for i in range(mlib.SBR_RECOVER_CREDIT_GRACE_FRAMES)])
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-without-recovery-credit", state.loss_reason)

    def test_a_credit_that_lags_the_vessel_gone_read_is_not_condemned(self):
        # The two facts arrive on different RPCs; the pool read can lag across
        # stock's recovery + scene change. Without the grace a good recovery is
        # condemned by a read-ordering race.
        state = self._in_recover()
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        lag = mlib.SBR_RECOVER_CREDIT_GRACE_FRAMES - 1
        state = drive(state, *[lost(14.0 + i,
                                    career_funds=float("nan")) for i in range(lag)])
        self.assertFalse(state.done)
        state = drive(state, lost(99.0, career_funds=1500.0))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)

    def test_the_recover_budget_expiry_is_a_flake_naming_whether_it_asked(self):
        state = self._in_recover(recoverTimeoutSeconds=10)
        state = drive(state, *[sci(ut=12.0 + i * 5, situation="LANDED",
                                   vessel_recoverable=mlib.RECOVERABLE_UNREAD)
                               for i in range(5)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("recover-never-completed", state.flake_reason)
        self.assertIn("issued=False", state.flake_reason)


class SbrPreRecoverDwellTests(unittest.TestCase):
    """The OPT-IN pre-recover hold (`preRecoverDwellSeconds`, default 0.0).

    WHY IT EXISTS, in one line: the recording a flight of this mission produces
    ENDS at the recovery, the optimizer's split candidate is the touchdown
    `SurfaceStationary` section, and `CanAutoSplitIgnoringGhostTriggers` refuses a
    split unless both halves clear 5.0 s - which the mission's NATURAL dwell
    straddled at 5.34 / 4.82 / 5.88 s across three L6 flights. The knob buys a
    deterministic count pin on the SPLIT side; the NO-SPLIT side is unreachable
    from here by construction (a hold cannot shorten a tail) and is pinned in C#.

    Both directions of the hinge, both directions of the machine, and the
    fail-open cases. The compatibility statement - the default drives the
    pre-dwell machine frame for frame - is the sibling class below, which replays
    against `origin/main` rather than asserting it here."""

    def test_the_hinge_is_pinned_in_both_directions(self):
        # THE DEFAULT never gates: 0.0 short-circuits before it reads a clock, so
        # even a nonsense pair of UTs answers True.
        self.assertTrue(mlib.sbr_recover_dwell_satisfied(0.0, 100.0, 0.0))
        self.assertTrue(mlib.sbr_recover_dwell_satisfied(0.0, None, float("nan")))
        # A DECLARED hold gates on the elapsed game time since the LANDING frame.
        self.assertFalse(mlib.sbr_recover_dwell_satisfied(12.0, 100.0, 111.9))
        self.assertTrue(mlib.sbr_recover_dwell_satisfied(12.0, 100.0, 112.0))
        self.assertTrue(mlib.sbr_recover_dwell_satisfied(12.0, 100.0, 999.0))
        # `>=`, so exactly landed+D satisfies it (the line above at 112.0).
        self.assertFalse(mlib.sbr_recover_dwell_satisfied(12.0, 100.0, 100.0))

    def test_the_hinge_fails_OPEN_on_a_clock_it_cannot_read(self):
        # A dark UT channel must not turn a calibration knob into a phase-budget
        # flake: this hold exists to move a recordings.count pin, and a flight
        # that lost its clock has bigger problems that OTHER gates already name.
        self.assertTrue(mlib.sbr_recover_dwell_satisfied(12.0, None, 500.0))
        self.assertTrue(mlib.sbr_recover_dwell_satisfied(12.0, float("nan"), 500.0))
        self.assertTrue(mlib.sbr_recover_dwell_satisfied(12.0, 100.0, float("nan")))
        self.assertTrue(mlib.sbr_recover_dwell_satisfied(12.0, 100.0, float("inf")))
        # And a nonsense dwell is read as "no hold" rather than as an infinite one.
        self.assertTrue(mlib.sbr_recover_dwell_satisfied(float("nan"), 100.0, 100.0))
        self.assertTrue(mlib.sbr_recover_dwell_satisfied(-5.0, 100.0, 100.0))
        self.assertTrue(mlib.sbr_recover_dwell_satisfied("x", 100.0, 100.0))

    def test_the_landing_frame_stamps_the_anchor_the_hold_measures_from(self):
        state, _ = flown()
        self.assertEqual(7.0, state.landed_ut)   # LANDED_FRAME's own ut
        # And it SURVIVES the two phase transitions between the landing and the
        # recovery - which is the whole reason it is a state field rather than
        # phase_entry_ut, which each _sbr_enter overwrites.
        state = in_recover()
        self.assertEqual(7.0, state.landed_ut)
        self.assertEqual(11.0, state.phase_entry_ut)

    def test_a_declared_hold_withholds_the_ask_until_it_elapses(self):
        # Landed at 7.0, dwell 12 -> nothing may go out before 19.0, however many
        # debounced Recoverable=YES frames land in between.
        state = in_recover(preRecoverDwellSeconds=12.0)
        state, acts = drive_actions(
            state, *[sci(ut=t, situation="LANDED",
                         vessel_recoverable=mlib.RECOVERABLE_YES)
                     for t in (12.0, 13.0, 14.0, 18.9)])
        self.assertEqual([], acts, "the hold let a verb out early")
        self.assertEqual(0, state.recover_asks)
        self.assertEqual(mlib.SBR_RECOVER, state.phase)
        # The debounce is NOT re-armed by the hold: the streak has been standing
        # for four frames, so the first eligible frame asks at once.
        state, acts = drive_actions(state, sci(ut=19.0, situation="LANDED",
                                               vessel_recoverable=mlib.RECOVERABLE_YES))
        self.assertEqual([mlib.ACTION_RECOVER_VESSEL], [a.kind for a in acts])
        self.assertEqual(1, state.recover_asks)

    def test_the_default_asks_at_exactly_the_frame_it_always_did(self):
        # THE COMPATIBILITY CASE, as a direct A/B on one scripted run: the same
        # frames, once with no key declared and once with 0.0 spelled out.
        for over in ({}, {"preRecoverDwellSeconds": 0.0}):
            state = in_recover(**over)
            state, acts = drive_actions(
                state, sci(ut=12.0, situation="LANDED",
                           vessel_recoverable=mlib.RECOVERABLE_YES))
            self.assertEqual([], acts, over)          # one frame is not a debounce
            state, acts = drive_actions(
                state, sci(ut=13.0, situation="LANDED",
                           vessel_recoverable=mlib.RECOVERABLE_YES))
            self.assertEqual([mlib.ACTION_RECOVER_VESSEL], [a.kind for a in acts], over)

    def test_a_dwell_shorter_than_the_natural_one_changes_nothing(self):
        # THE ASYMMETRY, driven rather than asserted in prose: RECOVER is entered
        # at 11.0 (4 s after the landing) and the debounce completes at 13.0, so a
        # 3 s dwell is ALREADY satisfied and the ask goes out at its natural frame.
        # This is why no value of this param can put a lane BELOW the split floor.
        state = in_recover(preRecoverDwellSeconds=3.0)
        state, acts = drive_actions(
            state, *[sci(ut=t, situation="LANDED",
                         vessel_recoverable=mlib.RECOVERABLE_YES)
                     for t in (12.0, 13.0)])
        self.assertEqual([mlib.ACTION_RECOVER_VESSEL], [a.kind for a in acts])

    def test_the_hold_does_not_suppress_the_named_unrecoverable_terminal(self):
        # PLACEMENT PROOF. The dwell conjunct sits INSIDE the ask condition, so
        # everything above it keeps running while the hold is open. A craft that
        # reads NO twice during a 12 s hold is condemned by name at once instead
        # of waiting the hold out to reach an unnamed budget flake.
        state = in_recover(preRecoverDwellSeconds=12.0)
        state = drive(state,
                      sci(ut=12.0, situation="LANDED",
                          vessel_recoverable=mlib.RECOVERABLE_NO),
                      sci(ut=13.0, situation="LANDED",
                          vessel_recoverable=mlib.RECOVERABLE_NO))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-not-recoverable", state.loss_reason)

    def test_a_hold_longer_than_the_budget_names_itself_in_the_flake(self):
        # An AUTHOR error (dwell >= recoverTimeoutSeconds) is legal TOML, so the
        # reason string has to make it self-diagnosing: `recoverAsks=0` with a
        # standing `preRecoverDwell=` is a hold that outlived its own phase, not a
        # kRPC fault, and the two triages are nothing alike.
        state = in_recover(preRecoverDwellSeconds=60.0, recoverTimeoutSeconds=10)
        state = drive(state, *[sci(ut=12.0 + i * 5, situation="LANDED",
                                   vessel_recoverable=mlib.RECOVERABLE_YES)
                               for i in range(4)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("recover-never-completed", state.flake_reason)
        self.assertIn("preRecoverDwell=60.000", state.flake_reason)
        self.assertIn("landedUT=7.000", state.flake_reason)

    def test_a_lane_that_declares_no_dwell_reports_a_zero_not_a_blank(self):
        state = in_recover(recoverTimeoutSeconds=10)
        state = drive(state, *[sci(ut=12.0 + i * 5, situation="LANDED",
                                   vessel_recoverable=mlib.RECOVERABLE_UNREAD)
                               for i in range(5)])
        self.assertIn("preRecoverDwell=0.000", state.flake_reason)

    def test_a_held_flight_still_reaches_the_success_terminal(self):
        # END TO END with the hold declared: the terminal, the credit and the
        # assertion rows are the ones a 0.0 lane produces, just later.
        state = in_recover(preRecoverDwellSeconds=12.0)
        state = drive(state, *[sci(ut=t, situation="LANDED",
                                   vessel_recoverable=mlib.RECOVERABLE_YES)
                               for t in (12.0, 15.0, 19.0)])
        self.assertTrue(state.recover_asks >= 1)
        state = drive(state, lost(20.0, career_funds=6000.0))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)
        self.assertTrue(state.done)
        self.assertIsNone(state.verdict)


_BASELINE_MODULE_NAME = "_mlib_baseline"


class SbrDwellCompatibilityTests(unittest.TestCase):
    """THE CROSS-VERSION REPLAY: with no dwell declared, this module's SBR machine
    must be `origin/main`'s, frame for frame.

    WHY A REPLAY RATHER THAN AN ASSERTION. `L3-career-science-recover` shares this
    mission with the L6 lanes and is a PINNED, promoted `nightly` spec: its
    `recordings.count = {2, 2}` and its five career-leg logContract tokens were
    measured on the pre-dwell machine. A local cell that says "the default is
    unchanged" is only as good as the frames the author happened to script. This
    one drives BOTH modules - the committed one and the one `git show
    origin/main:harness/missions/lib/mlib.py` produces - over the same scripted
    L3 flight and compares every emitted action, every phase and the terminal. It
    is the GS-6 `partSweepSteps` compatibility discipline (an opt-in phase whose
    empty declaration keeps the prior graph) with the baseline read from git
    instead of restated by hand.

    SELF-SKIPS, with a stated reason, when the baseline ref is not present - a
    shallow CI clone or a worktree with no `origin` remote. The suite that matters
    for this claim is the one an operator runs before a flight, and that tree has
    the ref."""

    # Tried in order. `origin/main` first because a session worktree's local
    # `main` can be behind; a tree with neither self-skips rather than passing
    # vacuously.
    BASELINE_REFS = ("origin/main", "main")

    @classmethod
    def setUpClass(cls):
        cls.baseline = None
        cls.skip_reason = None
        source, ref = cls._read_baseline_source()
        if source is None:
            cls.skip_reason = ref            # carries the reason when source is None
            return
        cls.ref = ref
        module = types.ModuleType(_BASELINE_MODULE_NAME)
        module.__file__ = "<%s:harness/missions/lib/mlib.py>" % (ref,)
        # REGISTERED BEFORE THE EXEC, and it is not optional: `@dataclass` resolves
        # a class's own module out of `sys.modules[cls.__module__]` to spot a
        # KW_ONLY sentinel, so an unregistered module blows up on mlib's first
        # dataclass with a bare `AttributeError: 'NoneType' object has no
        # attribute '__dict__'` that says nothing about the real cause.
        sys.modules[_BASELINE_MODULE_NAME] = module
        try:
            exec(compile(source, module.__file__, "exec"), module.__dict__)
        except Exception as exc:                                 # pragma: no cover
            sys.modules.pop(_BASELINE_MODULE_NAME, None)
            cls.skip_reason = "baseline mlib at %s did not import: %r" % (ref, exc)
            return
        if not hasattr(module, "sbr_decide"):                    # pragma: no cover
            sys.modules.pop(_BASELINE_MODULE_NAME, None)
            cls.skip_reason = "baseline mlib at %s has no sbr_decide" % (ref,)
            return
        cls.baseline = module

    @classmethod
    def tearDownClass(cls):
        # Do not leave a second whole copy of mlib in the interpreter's module
        # table for every sibling suite discovery runs after this one.
        sys.modules.pop(_BASELINE_MODULE_NAME, None)

    @classmethod
    def _read_baseline_source(cls):
        """(source, ref) on success; (None, reason) when no baseline is reachable."""
        for ref in cls.BASELINE_REFS:
            try:
                proc = subprocess.run(
                    ["git", "show", "%s:harness/missions/lib/mlib.py" % (ref,)],
                    cwd=_REPO_ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            except OSError as exc:                               # pragma: no cover
                return None, "git is not runnable here (%r)" % (exc,)
            if proc.returncode == 0 and proc.stdout:
                return proc.stdout.decode("utf-8"), ref
        return None, ("no baseline mlib: none of %s resolves in this tree (a shallow "
                      "clone or a checkout with no origin remote)"
                      % (", ".join(cls.BASELINE_REFS),))

    def setUp(self):
        if self.baseline is None:
            self.skipTest(self.skip_reason)

    @staticmethod
    def _snapshot(module, fields):
        """Build a TelemetrySnapshot of THAT module's own class from a field dict,
        dropping any key the version does not declare (so an unrelated channel
        added on one side does not read as a behaviour change)."""
        declared = {f.name for f in dataclasses.fields(module.TelemetrySnapshot)}
        return module.TelemetrySnapshot(**{k: v for k, v in fields.items()
                                           if k in declared})

    @staticmethod
    def _action_shape(module_a, module_b, action):
        """An action as a comparable tuple over the fields BOTH versions declare."""
        common = sorted({f.name for f in dataclasses.fields(module_a.Action)}
                        & {f.name for f in dataclasses.fields(module_b.Action)})
        return tuple((name, getattr(action, name, None)) for name in common)

    def _replay(self, module, param_dict, script):
        """Drive one module's SBR machine over the script; return the per-frame
        trace a comparison can be made on."""
        state = module.sbr_initial_state(module.sbr_params_from_dict(param_dict))
        trace = []
        for fields in script:
            state, actions = module.sbr_decide(
                state, self._snapshot(module, fields))
            trace.append((state.phase, state.done, state.verdict,
                          state.loss_reason, state.flake_reason,
                          tuple(self._action_shape(mlib, self.baseline, a)
                                for a in actions)))
        return state, trace

    def test_L3s_committed_params_replay_identically_against_the_baseline(self):
        # L3's OWN missionParams, read from the committed spec rather than copied,
        # so a future edit to that spec re-aims this cell instead of staling it.
        with open(os.path.join(_HARNESS, "scenarios",
                               "L3-career-science-recover.toml"), "rb") as fh:
            l3 = tomllib.load(fh)["driver"]["missionParams"]
        self.assertNotIn("preRecoverDwellSeconds", l3,
                         "L3 must stay on the default; its pins were measured there")

        head_state, head_trace = self._replay(mlib, l3, DWELL_REPLAY_SCRIPT)
        base_state, base_trace = self._replay(self.baseline, l3, DWELL_REPLAY_SCRIPT)

        self.assertEqual(len(base_trace), len(head_trace))
        for i, (base, head) in enumerate(zip(base_trace, head_trace)):
            self.assertEqual(base, head,
                             "frame %d (ut=%s) diverges from %s"
                             % (i, DWELL_REPLAY_SCRIPT[i].get("ut"), self.ref))
        # And the end state agrees on everything the harness reads downstream.
        self.assertEqual(base_state.phases_reached, head_state.phases_reached)
        self.assertEqual(base_state.skip_settle_tail, head_state.skip_settle_tail)
        # The script is a REAL flight, not a stub that agrees trivially.
        self.assertEqual(mlib.SBR_RECOVERED, head_state.phase)
        self.assertTrue(head_state.done)
        self.assertIsNone(head_state.verdict)

    def test_the_assertion_rows_are_the_baselines_too(self):
        with open(os.path.join(_HARNESS, "scenarios",
                               "L3-career-science-recover.toml"), "rb") as fh:
            l3 = tomllib.load(fh)["driver"]["missionParams"]
        head_state, _ = self._replay(mlib, l3, DWELL_REPLAY_SCRIPT)
        base_state, _ = self._replay(self.baseline, l3, DWELL_REPLAY_SCRIPT)
        head_rows = [r.to_dict() for r in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(l3), head_state)]
        base_rows = [r.to_dict() for r in self.baseline.evaluate_sbr_assertions(
            [], self.baseline.sbr_params_from_dict(l3), base_state)]
        self.assertEqual(base_rows, head_rows)
        self.assertEqual([], [r for r in head_rows if not r.get("met")], head_rows)

    def test_the_baseline_module_really_is_the_pre_dwell_one(self):
        # THE MUTATION GUARD on this whole class: if `origin/main` already carried
        # the dwell, the replay above would be comparing the change against
        # itself and would pass no matter what. It reds here instead, telling the
        # reader to re-point BASELINE_REFS at the pre-dwell commit.
        self.assertFalse(hasattr(self.baseline, "sbr_recover_dwell_satisfied"),
                         "the baseline at %s ALREADY has the dwell, so this class "
                         "proves nothing - re-point BASELINE_REFS" % (self.ref,))
        self.assertTrue(hasattr(mlib, "sbr_recover_dwell_satisfied"))


# The scripted L3 flight the cross-version replay drives: B1's proven pad-hop
# shape (fuel burn -> apex -> arm -> canopy -> landed) followed by the career
# tail, as PLAIN FIELD DICTS so each module can build its own TelemetrySnapshot
# class from them. Sized against L3's committed params (apoapsis inside
# 6000-30000, one experiment holding data, a science gain over the 0.5 floor, a
# recovery credit over the 1.0 floor).
def _rf(**kw):
    base = dict(science_experiment_count=2, science_data_count=0,
                science_available_count=2, vessel_recoverable=mlib.RECOVERABLE_YES,
                career_funds=1000.0, career_science=10.0)
    base.update(kw)
    return base


DWELL_REPLAY_SCRIPT = [
    _rf(ut=0.0, stage_solid_fuel=1.0, apoapsis=14000, situation="PRE_LAUNCH"),
    _rf(ut=1.0, stage_solid_fuel=0.5, apoapsis=14000, situation="FLYING"),
    _rf(ut=2.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING"),
    _rf(ut=3.0, vertical_speed=5.0, apoapsis=14000, situation="FLYING"),
    _rf(ut=4.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING"),
    _rf(ut=5.0, altitude=5000, apoapsis=14000, situation="FLYING",
        craft_chute_state=mlib.CHUTE_STATE_SEMI_DEPLOYED),
    _rf(ut=6.0, altitude=2000, apoapsis=14000, situation="FLYING",
        craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
    _rf(ut=7.0, altitude=100, apoapsis=14000, situation="LANDED",
        craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
    # COLLECT: two debounced frames holding data.
    _rf(ut=8.0, situation="LANDED", science_data_count=2),
    _rf(ut=9.0, situation="LANDED", science_data_count=2),
    # TRANSMIT: two debounced frames of a credited pool.
    _rf(ut=10.0, situation="LANDED", career_science=25.0),
    _rf(ut=11.0, situation="LANDED", career_science=25.0),
    # RECOVER: two debounced Recoverable=YES frames, then the ask goes out. The
    # credited science pool STAYS at 25.0 from here on - stock does not take it
    # back, and a frame that dropped it to the pre-transmit reading would leave
    # `scienceTransmittedObserved` unmet on a flight that really transmitted.
    _rf(ut=12.0, situation="LANDED", career_science=25.0),
    _rf(ut=13.0, situation="LANDED", career_science=25.0),
    # The recovery: the craft is gone and the funds pool has followed.
    _rf(ut=14.0, vessel_lost=True, career_funds=6000.0, career_science=25.0,
        recover_request_result=mlib.RECOVER_REQUEST_ISSUED),
]


class SbrVesselLossTests(unittest.TestCase):
    """A vessel loss means opposite things either side of the recovery."""

    def test_a_loss_during_collect_is_an_assert_fail(self):
        state, _ = flown()
        state = drive(state, snap(ut=8.0, vessel_lost=True, career_funds=9999.0))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-before-recovery", state.loss_reason)

    def test_a_loss_during_transmit_is_an_assert_fail(self):
        state = in_transmit()
        state = drive(state, snap(ut=10.0, vessel_lost=True, career_funds=9999.0))
        self.assertTrue(state.done)
        self.assertIn("vessel-lost-before-recovery", state.loss_reason)

    def test_a_dark_experiment_channel_after_collect_does_not_flake_the_flight(self):
        # SCOPING, and it is a correctness point: a give-up must name a channel
        # the CURRENT phase depends on. By TRANSMIT the data is already collected
        # and the gate is the career POOL; flaking there would throw away a flight
        # that had already collected and transmitted, for a reading nothing was
        # waiting on. (The pools have their own give-up.)
        state = in_transmit()
        state = drive(state, *[sci(ut=10.0 + i, situation="LANDED",
                                   science_experiment_count=mlib.SCIENCE_COUNT_UNREAD,
                                   science_data_count=mlib.SCIENCE_COUNT_UNREAD,
                                   career_science=10.0)
                               for i in range(mlib.SBR_SCIENCE_UNREAD_GIVEUP_FRAMES * 2)])
        self.assertFalse(state.done)
        self.assertEqual(mlib.SBR_TRANSMIT, state.phase)

    def test_the_same_dark_channel_DOES_flake_during_collect(self):
        # The positive control for the scoping above: in COLLECT the gate reads
        # exactly this channel, so a dark one means the phase can never conclude.
        state, _ = flown()
        state = drive(state, *[sci(ut=8.0 + i, situation="LANDED",
                                   science_experiment_count=mlib.SCIENCE_COUNT_UNREAD)
                               for i in range(mlib.SBR_SCIENCE_UNREAD_GIVEUP_FRAMES)])
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("science-channel-dark", state.flake_reason)

    def test_a_vessel_lost_frame_HOLDS_the_dark_channel_streak_rather_than_resetting_it(self):
        # On that frame every VESSEL-scoped channel is legitimately unread, so
        # counting it would blame the channel for the recovery we asked for.
        #
        # THE STREAK IS SEEDED NONZERO ON PURPOSE. With a streak of 0 the cell
        # passes identically whether lost frames SKIP the accounting or RESET it,
        # so it would discriminate nothing. Seeded, it pins the actual contract:
        # a lost frame is evidence in NEITHER direction and leaves the run alone.
        from dataclasses import replace as _replace
        state = _replace(in_recover(), science_unread_streak=3)
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        before = state.science_unread_streak
        self.assertEqual(3, before, "the seed must survive to the frames under test")
        state = drive(state, *[lost(14.0 + i, career_funds=1000.0)
                               for i in range(3)])
        self.assertEqual(before, state.science_unread_streak)
        self.assertNotEqual(mlib.MISSION_FLAKE, state.verdict)


class SbrIdempotenceTests(unittest.TestCase):

    def test_the_machine_is_idempotent_once_done(self):
        state, _ = flown()
        state = drive(state, snap(ut=8.0, vessel_lost=True))
        self.assertTrue(state.done)
        after, actions = mlib.sbr_decide(state, sci(ut=9.0, situation="LANDED"))
        self.assertIs(state, after)
        self.assertEqual([], actions)

    def test_a_non_finite_ut_never_trips_a_phase_budget(self):
        state, _ = flown(collectTimeoutSeconds=1)
        state = drive(state, sci(ut=float("nan"), situation="LANDED"))
        self.assertFalse(state.done)


class SbrAssertionTests(unittest.TestCase):
    """The four rows, both directions."""

    def _recovered(self):
        state = in_recover()
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        return drive(state, lost(14.0, career_funds=1500.0,
                                 career_science=25.0))

    def test_all_four_met_on_a_real_recovered_flight(self):
        rows = {o.name: o for o in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(PARAMS), self._recovered())}
        self.assertEqual({"flightCompletedObserved", "scienceCollectedObserved",
                          "scienceTransmittedObserved", "vesselRecoveredObserved"},
                         set(rows))
        for name, row in rows.items():
            self.assertTrue(row.met, "%s unmet: %s" % (name, row.to_dict()))

    def test_the_rows_report_the_measured_values_not_just_a_boolean(self):
        rows = {o.name: o for o in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(PARAMS), self._recovered())}
        self.assertEqual(2, rows["scienceCollectedObserved"].value)
        self.assertEqual(15.0, rows["scienceTransmittedObserved"].value)
        self.assertEqual(500.0, rows["vesselRecoveredObserved"].value)
        detail = rows["vesselRecoveredObserved"].to_dict()
        self.assertEqual(1000.0, detail["baseline"])
        self.assertEqual(1500.0, detail["final"])
        self.assertTrue(detail["recoveryIssued"])

    def test_a_flight_that_never_landed_leaves_the_precondition_row_unmet(self):
        rows = {o.name: o for o in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(PARAMS), fresh())}
        self.assertFalse(rows["flightCompletedObserved"].met)
        self.assertFalse(rows["vesselRecoveredObserved"].met)

    def test_the_recovery_row_is_unmet_when_the_terminal_was_never_reached(self):
        state, _ = flown()
        rows = {o.name: o for o in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(PARAMS), state)}
        self.assertTrue(rows["flightCompletedObserved"].met)
        self.assertFalse(rows["scienceCollectedObserved"].met)
        self.assertFalse(rows["scienceTransmittedObserved"].met)
        self.assertFalse(rows["vesselRecoveredObserved"].met)

    def test_a_zero_gain_passes_a_zero_floor_which_is_why_the_schema_forbids_one(self):
        # Named for what it actually shows. The pool here is READABLE and simply
        # has not moved (baseline == final == 10.0), so the gain is a finite 0.0:
        # against a 0.0 floor that PASSES. The predicate is not the guard - the
        # schema's exclusive minimum is, and `SchemaSyncTests` pins it.
        state = in_transmit()
        self.assertEqual(0.0, mlib._sbr_science_gain(state))
        rows = {o.name: o for o in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(params(transmitMinScienceGain=0.0)), state)}
        self.assertTrue(rows["scienceTransmittedObserved"].met)
        rows = {o.name: o for o in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(PARAMS), state)}
        self.assertFalse(rows["scienceTransmittedObserved"].met)

    def test_a_GENUINELY_UNREAD_pool_leaves_the_row_unmet_even_at_a_zero_floor(self):
        # The case the cell above does NOT cover, and the one the fail-closed
        # sentinel exists for: with no finite reading the gain is None, so no
        # floor - not even 0.0 - can be satisfied. An unread pool must never be
        # able to certify a credit.
        from dataclasses import replace as _replace
        state = _replace(in_transmit(), science_baseline=None, last_science=None)
        self.assertIsNone(mlib._sbr_science_gain(state))
        for floor in (0.0, 1.0):
            rows = {o.name: o for o in mlib.evaluate_sbr_assertions(
                [], mlib.sbr_params_from_dict(params(transmitMinScienceGain=floor)),
                state)}
            self.assertFalse(rows["scienceTransmittedObserved"].met, floor)

    def test_an_unread_funds_pool_leaves_the_recovery_row_unmet_at_a_zero_floor(self):
        # The same property on the recovery side, where the floor of 0.0 is
        # SCHEMA-LEGAL - so this is the one that would actually bite.
        state, _ = flown()
        rows = {o.name: o for o in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(params(recoverMinFundsGain=0.0)), state)}
        self.assertFalse(rows["vesselRecoveredObserved"].met)
        self.assertIsNone(rows["vesselRecoveredObserved"].value)

    def test_b1s_apoapsis_window_is_deliberately_not_re_asserted(self):
        # Re-asserting a WINDOW on the hop's shape would make this forge brittle
        # against the one thing it does not care about. If a row ever appears
        # here that gates on the apoapsis, this cell is where that decision gets
        # made on purpose.
        names = [o.name for o in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(PARAMS), self._recovered())]
        self.assertNotIn("apoapsisWindow", names)
        self.assertNotIn("craftCanopyObserved", names)


# ---------------------------------------------------------------------------
# 2. The shell, end to end over scripted telemetry.
# ---------------------------------------------------------------------------


def full_flight_frames():
    """A scripted flight that flies, collects, transmits and is recovered."""
    frames = list(FLIGHT_FRAMES) + [LANDED_FRAME]
    frames += [sci(ut=8.0 + i, situation="LANDED", science_data_count=2,
                   career_science=10.0) for i in range(2)]
    frames += [sci(ut=10.0 + i, situation="LANDED", science_data_count=0,
                   career_science=25.0) for i in range(2)]
    frames += [sci(ut=12.0 + i, situation="LANDED", science_data_count=0,
                   career_science=25.0, vessel_recoverable=mlib.RECOVERABLE_YES)
               for i in range(2)]
    frames += [lost(14.0, career_funds=1500.0, career_science=25.0)]
    return frames


class SbrShellTests(unittest.TestCase):

    def test_a_scripted_full_flight_returns_mission_ok(self):
        code, result = run(science_bench_recover.SPEC, PARAMS,
                           FakeMissionControl(full_flight_frames()))
        self.assertEqual(0, code)
        self.assertEqual(mlib.MISSION_OK, result["verdict"])
        self.assertEqual("science_bench_recover", result["mission"])
        self.assertIn(mlib.SBR_RECOVERED, result["phasesReached"])
        self.assertTrue(all(a["met"] for a in result["assertions"]), result["assertions"])

    def test_the_shell_issues_the_three_verbs_in_order_and_recovers_last(self):
        control = FakeMissionControl(full_flight_frames())
        run(science_bench_recover.SPEC, PARAMS, control)
        kinds = [a.kind for a in control.actions]
        career = [k for k in kinds if k in (mlib.ACTION_RUN_SCIENCE_EXPERIMENTS,
                                            mlib.ACTION_TRANSMIT_SCIENCE,
                                            mlib.ACTION_RECOVER_VESSEL)]
        self.assertEqual([mlib.ACTION_RUN_SCIENCE_EXPERIMENTS,
                          mlib.ACTION_TRANSMIT_SCIENCE,
                          mlib.ACTION_RECOVER_VESSEL], career)
        self.assertEqual(mlib.ACTION_RECOVER_VESSEL, kinds[-1],
                         "nothing may be performed after the recovery: the craft is "
                         "gone and perform() is not wrapped by the fly loop")

    def test_the_shell_still_flies_b1s_pad_hop(self):
        control = FakeMissionControl(full_flight_frames())
        run(science_bench_recover.SPEC, PARAMS, control)
        kinds = [a.kind for a in control.actions]
        for expected in (mlib.ACTION_SET_THROTTLE, mlib.ACTION_ACTIVATE_STAGE,
                         mlib.ACTION_CUT_THROTTLE, mlib.ACTION_SET_CHUTE_DEPLOY_ALTITUDE,
                         mlib.ACTION_DEPLOY_CHUTE):
            self.assertIn(expected, kinds)

    def test_a_craft_with_no_science_part_reds_by_name(self):
        frames = list(FLIGHT_FRAMES) + [LANDED_FRAME]
        frames += [sci(ut=8.0 + i, situation="LANDED", science_experiment_count=0,
                       science_data_count=0, science_available_count=0)
                   for i in range(4)]
        code, result = run(science_bench_recover.SPEC, PARAMS,
                           FakeMissionControl(frames))
        self.assertNotEqual(0, code)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, result["verdict"])
        self.assertIn("no-experiments-aboard", result["reason"])

    def test_a_transmit_that_credits_nothing_reds_by_name(self):
        frames = list(FLIGHT_FRAMES) + [LANDED_FRAME]
        frames += [sci(ut=8.0 + i, situation="LANDED", science_data_count=2,
                       career_science=10.0) for i in range(2)]
        frames += [sci(ut=10.0 + i * 30, situation="LANDED", science_data_count=2,
                       career_science=10.0) for i in range(8)]
        code, result = run(science_bench_recover.SPEC,
                           params(transmitTimeoutSeconds=60),
                           FakeMissionControl(frames))
        self.assertNotEqual(0, code)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, result["verdict"])
        self.assertIn("transmit-credited-no-science", result["reason"])

    def test_the_handoff_disclosure_rides_the_ok_reason(self):
        # A green forge run must not be readable as "the ledger captured it".
        code, result = run(science_bench_recover.SPEC, PARAMS,
                           FakeMissionControl(full_flight_frames()))
        self.assertEqual(0, code)
        self.assertIn("ledgerScienceCapture", result["reason"])
        self.assertIn("ledgerOracle", result["reason"])
        self.assertEqual(mlib.SBR_RECOVERED, result["handoff"]["terminal"])


# ---------------------------------------------------------------------------
# 3. The runner's new channels and verbs.
# ---------------------------------------------------------------------------


class _FakeExperiment:
    def __init__(self, available=True, has_data=False, inoperable=False,
                 raise_on=()):
        self._available = available
        self.has_data_value = has_data
        self._inoperable = inoperable
        self._raise_on = set(raise_on)
        self.ran = 0
        self.transmitted = 0

    @property
    def available(self):
        if "available" in self._raise_on:
            raise RuntimeError("available blew up")
        return self._available

    @property
    def has_data(self):
        if "has_data" in self._raise_on:
            raise RuntimeError("has_data blew up")
        return self.has_data_value

    @property
    def inoperable(self):
        return self._inoperable

    def run(self):
        if "run" in self._raise_on:
            raise RuntimeError("run blew up")
        self.ran += 1
        self.has_data_value = True

    def transmit(self):
        if "transmit" in self._raise_on:
            raise RuntimeError("transmit blew up")
        self.transmitted += 1
        self.has_data_value = False


class _FakeParts:
    def __init__(self, experiments, raises=False, raise_times=None):
        self._experiments = experiments
        self._raises = raises
        # `raise_times=N` raises on the first N reads and then answers normally -
        # a TRANSIENT enumeration fault, which is a different thing from a
        # permanently dark surface and must be classified differently.
        self._raise_times = raise_times
        self.reads = 0

    @property
    def experiments(self):
        self.reads += 1
        if self._raises:
            raise RuntimeError("parts.experiments blew up")
        if self._raise_times is not None and self.reads <= self._raise_times:
            raise RuntimeError("parts.experiments transient fault")
        return list(self._experiments)


class _FakeVessel:
    def __init__(self, experiments=(), parts_raise=False, recoverable=True,
                 recoverable_raises=False, recover_raises=False,
                 parts_raise_times=None):
        self.parts = _FakeParts(list(experiments), raises=parts_raise,
                                raise_times=parts_raise_times)
        self._recoverable = recoverable
        self._recoverable_raises = recoverable_raises
        self._recover_raises = recover_raises
        self.recovered = 0

    @property
    def recoverable(self):
        if self._recoverable_raises:
            raise RuntimeError("recoverable blew up")
        return self._recoverable

    def recover(self):
        if self._recover_raises:
            raise RuntimeError("Vessel is not recoverable")
        self.recovered += 1


class _FakePoolsSpaceCenter:
    def __init__(self, funds=1000.0, science=10.0, raises=False):
        self._funds = funds
        self._science = science
        self._raises = raises

    @property
    def funds(self):
        if self._raises:
            raise RuntimeError("not a career game")
        return self._funds

    @property
    def science(self):
        if self._raises:
            raise RuntimeError("not a career game")
        return self._science


def control():
    return mission_runner.KrpcMissionControl(client_name="test", read_science=True)


class ScienceChannelReadTests(unittest.TestCase):
    """The enumeration whose two failure readings mean opposite things."""

    def test_a_successful_enumeration_reports_the_three_counts(self):
        exps = [_FakeExperiment(available=True, has_data=True),
                _FakeExperiment(available=False, has_data=False),
                _FakeExperiment(available=True, has_data=False)]
        self.assertEqual((3, 1, 2),
                         control()._read_science_channels(_FakeVessel(exps)))

    def test_a_craft_with_no_experiments_reports_a_real_zero(self):
        self.assertEqual((0, 0, 0), control()._read_science_channels(_FakeVessel([])))

    def test_a_raising_enumeration_reports_the_unread_sentinel_triple(self):
        got = control()._read_science_channels(_FakeVessel([], parts_raise=True))
        self.assertEqual((mlib.SCIENCE_COUNT_UNREAD,) * 3, got)

    def test_a_transient_fault_never_fabricates_an_OBSERVED_ZERO(self):
        # THE REGRESSION. An earlier revision swallowed the first read to [], then
        # RE-PROBED to tell "empty" from "unreadable" and DISCARDED the re-probe's
        # result - so a single transient fault on a craft WITH experiments
        # reported (0, 0, 0). Two such polls complete the debounce and condemn
        # `no-experiments-aboard`: a NON-RETRYABLE assert-fail manufactured out of
        # a RETRYABLE channel fault, which is the exact inversion the
        # UNREAD-vs-zero split exists to prevent.
        vessel = _FakeVessel([_FakeExperiment(has_data=True)], parts_raise_times=1)
        first = control()._read_science_channels(vessel)
        self.assertEqual((mlib.SCIENCE_COUNT_UNREAD,) * 3, first,
                         "a faulted read must report UNREAD, never a fabricated 0")
        self.assertEqual(1, vessel.parts.reads,
                         "the surface must be enumerated ONCE per call; a second "
                         "probe is the window the fabricated zero came through")
        # And the very next poll, with the fault gone, reads the truth.
        self.assertEqual((1, 1, 1), control()._read_science_channels(vessel))

    def test_a_single_raising_module_does_not_blind_the_sweep(self):
        exps = [_FakeExperiment(has_data=True),
                _FakeExperiment(has_data=True, raise_on=("has_data",))]
        count, data, _available = control()._read_science_channels(_FakeVessel(exps))
        self.assertEqual(2, count)
        self.assertEqual(1, data)

    def test_an_unarmed_control_never_reads_the_channels(self):
        # Every mission but this lane leaves read_science off, and their snapshots
        # must stay byte-identical - which means not paying the RPCs either.
        unarmed = mission_runner.KrpcMissionControl(client_name="test")
        self.assertFalse(unarmed._read_science)


class CareerPoolReadTests(unittest.TestCase):

    def test_the_pools_are_read_from_the_space_center_not_the_vessel(self):
        funds, science = control()._read_career_pools(_FakePoolsSpaceCenter(1234.0, 56.0))
        self.assertEqual((1234.0, 56.0), (funds, science))

    def test_a_non_career_save_degrades_to_the_nan_sentinel(self):
        funds, science = control()._read_career_pools(_FakePoolsSpaceCenter(raises=True))
        self.assertTrue(math.isnan(funds))
        self.assertTrue(math.isnan(science))

    def test_the_dark_pool_warn_is_latched_once(self):
        lines = []
        orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = lines.append
        try:
            c = control()
            for _ in range(5):
                c._read_career_pools(_FakePoolsSpaceCenter(raises=True))
        finally:
            mission_runner._stdout_sink = orig
        self.assertEqual(1, len([l for l in lines if "career pools UNREADABLE" in l]))


class RecoverableReadTests(unittest.TestCase):

    def test_the_tri_state_maps_both_ways(self):
        self.assertEqual(mlib.RECOVERABLE_YES,
                         control()._read_vessel_recoverable(_FakeVessel(recoverable=True)))
        self.assertEqual(mlib.RECOVERABLE_NO,
                         control()._read_vessel_recoverable(_FakeVessel(recoverable=False)))

    def test_a_raising_read_is_the_unread_sentinel_not_a_false(self):
        got = control()._read_vessel_recoverable(_FakeVessel(recoverable_raises=True))
        self.assertEqual(mlib.RECOVERABLE_UNREAD, got)


class SciencePerformTests(unittest.TestCase):

    def test_run_skips_modules_that_already_hold_data(self):
        # The skip is what makes the machine's bounded re-emit safe.
        held = _FakeExperiment(has_data=True)
        fresh_exp = _FakeExperiment(has_data=False)
        control()._perform_run_science(_FakeVessel([held, fresh_exp]))
        self.assertEqual(0, held.ran)
        self.assertEqual(1, fresh_exp.ran)

    def test_run_skips_unavailable_and_inoperable_modules(self):
        unavailable = _FakeExperiment(available=False)
        inoperable = _FakeExperiment(inoperable=True)
        control()._perform_run_science(_FakeVessel([unavailable, inoperable]))
        self.assertEqual(0, unavailable.ran)
        self.assertEqual(0, inoperable.ran)

    def test_one_refusing_module_does_not_abort_the_sweep(self):
        bad = _FakeExperiment(raise_on=("run",))
        good = _FakeExperiment()
        control()._perform_run_science(_FakeVessel([bad, good]))
        self.assertEqual(1, good.ran)

    def test_transmit_sends_only_modules_holding_data(self):
        held = _FakeExperiment(has_data=True)
        empty = _FakeExperiment(has_data=False)
        control()._perform_transmit_science(_FakeVessel([held, empty]))
        self.assertEqual(1, held.transmitted)
        self.assertEqual(0, empty.transmitted)

    def test_neither_verb_raises_when_the_whole_surface_is_gone(self):
        # perform() is NOT wrapped by the fly loop, so a raise here ends the
        # mission as MISSION-ERROR instead of as a named outcome.
        c = control()
        c._perform_run_science(_FakeVessel([], parts_raise=True))
        c._perform_transmit_science(_FakeVessel([], parts_raise=True))


class RecoverPerformTests(unittest.TestCase):

    def test_a_recoverable_craft_is_recovered(self):
        v = _FakeVessel(recoverable=True)
        c = control()
        c._perform_recover_vessel(v)
        self.assertEqual(1, v.recovered)
        self.assertEqual(mlib.RECOVER_REQUEST_ISSUED, c._recover_request_result)

    def test_an_unrecoverable_craft_is_never_asked(self):
        # READ BEFORE ASK: kRPC's Recover() throws on exactly this, and the throw
        # would report MISSION-ERROR instead of the machine's named terminal.
        v = _FakeVessel(recoverable=False)
        c = control()
        c._perform_recover_vessel(v)
        self.assertEqual(0, v.recovered)
        self.assertEqual(mlib.RECOVER_REQUEST_DECLINED, c._recover_request_result)

    def test_an_unreadable_recoverable_flag_is_never_asked(self):
        v = _FakeVessel(recoverable_raises=True)
        c = control()
        c._perform_recover_vessel(v)
        self.assertEqual(0, v.recovered)
        self.assertEqual(mlib.RECOVER_REQUEST_UNREADABLE, c._recover_request_result)

    def test_a_throwing_recover_never_escapes_perform(self):
        v = _FakeVessel(recoverable=True, recover_raises=True)
        c = control()
        c._perform_recover_vessel(v)   # must not raise
        self.assertEqual(0, v.recovered)
        self.assertEqual(mlib.RECOVER_REQUEST_FAILED, c._recover_request_result)

    def test_every_exit_reports_something_and_only_one_of_them_is_ISSUED(self):
        # THE CONTRACT the machine's issued latch rests on: a perform that
        # RETURNS WITHOUT STAMPING is indistinguishable from one that never ran,
        # and that silence is exactly what let a declined ask read as a recovery.
        outcomes = []
        for v in (_FakeVessel(recoverable=True),
                  _FakeVessel(recoverable=False),
                  _FakeVessel(recoverable_raises=True),
                  _FakeVessel(recoverable=True, recover_raises=True)):
            c = control()
            c._perform_recover_vessel(v)
            self.assertNotEqual(mlib.RECOVER_REQUEST_UNREAD, c._recover_request_result)
            outcomes.append(c._recover_request_result)
        self.assertEqual(1, outcomes.count(mlib.RECOVER_REQUEST_ISSUED))
        for token in outcomes:
            if token != mlib.RECOVER_REQUEST_ISSUED:
                self.assertIn(token, mlib.RECOVER_REQUEST_DECLINES)

    def test_the_result_is_unread_until_a_verb_is_performed(self):
        self.assertEqual(mlib.RECOVER_REQUEST_UNREAD, control()._recover_request_result)


class PerformDispatchTests(unittest.TestCase):
    """`perform` actually ROUTES the three kinds, driven through the real method.

    The sibling cells call the helpers directly, which proves the helpers and
    nothing about the dispatch table. An action kind with no branch falls through
    to `raise ValueError("unknown action kind")`, which ends the mission as
    MISSION-ERROR - a failure mode a helper-only test cannot see."""

    class _Conn:
        def __init__(self, vessel):
            outer_vessel = vessel

            class _SC:
                @property
                def active_vessel(self):
                    return outer_vessel

            self.space_center = _SC()

    def _control_over(self, vessel):
        c = mission_runner.KrpcMissionControl(client_name="test", read_science=True)
        c._conn = self._Conn(vessel)
        return c

    def test_each_of_the_three_kinds_reaches_its_branch(self):
        exp = _FakeExperiment(available=True, has_data=False)
        vessel = _FakeVessel([exp], recoverable=True)
        vessel.control = None
        c = self._control_over(vessel)
        c.perform(mlib.Action(mlib.ACTION_RUN_SCIENCE_EXPERIMENTS))
        self.assertEqual(1, exp.ran)
        c.perform(mlib.Action(mlib.ACTION_TRANSMIT_SCIENCE))
        self.assertEqual(1, exp.transmitted)
        c.perform(mlib.Action(mlib.ACTION_RECOVER_VESSEL))
        self.assertEqual(1, vessel.recovered)

    def test_an_undeclared_kind_still_raises_unknown_action(self):
        # The negative control: the dispatch table's fall-through is what makes
        # the cell above meaningful.
        vessel = _FakeVessel([])
        vessel.control = None
        c = self._control_over(vessel)
        with self.assertRaises(ValueError):
            c.perform(mlib.Action("not_a_real_action_kind"))


class VesselLostCareerPoolCarryTests(unittest.TestCase):
    """THE design property of the career-pool channels, at the runner boundary.

    Recovering the active vessel REMOVES it, so the frame that proves the
    recovery happened necessarily has no vessel on it. If the pools did not
    survive that frame the mission's success terminal would be unobservable and
    every good recovery would report `vessel-lost-without-recovery-credit`.
    Without this cell the whole claim rests on a comment."""

    class _DeadVesselConn:
        """A connection whose active-vessel read always raises but whose CAREER
        pools still answer - i.e. the craft has been recovered and the save's
        career has not gone anywhere."""
        def __init__(self, funds, science):
            outer = self

            class _SC:
                ut = 512.0

                @property
                def active_vessel(self):
                    raise RuntimeError("active vessel recovered")

                @property
                def funds(self):
                    return outer.funds_value

                @property
                def science(self):
                    return outer.science_value

                def get_kerbal(self, name):
                    return None

            self.funds_value = funds
            self.science_value = science
            self.space_center = _SC()

    def _control(self, funds=1500.0, science=25.0, read_science=True):
        c = mission_runner.KrpcMissionControl(client_name="test",
                                              read_science=read_science)
        c._conn = self._DeadVesselConn(funds, science)
        return c

    def _escalate(self, c):
        for _ in range(mission_runner.READ_FAIL_STREAK_LIMIT - 1):
            with self.assertRaises(RuntimeError):
                c.read_snapshot()
        return c.read_snapshot()

    def test_the_vessel_lost_snapshot_carries_both_pools(self):
        snapshot = self._escalate(self._control())
        self.assertTrue(snapshot.vessel_lost)
        self.assertEqual(1500.0, snapshot.career_funds)
        self.assertEqual(25.0, snapshot.career_science)
        self.assertEqual(512.0, snapshot.ut)

    def test_the_vessel_scoped_channels_stay_unread_on_that_frame(self):
        # They are properties OF THE VESSEL and a recovered craft has none;
        # fabricating a 0 there would be the mirror of the pool carry.
        snapshot = self._escalate(self._control())
        self.assertEqual(mlib.SCIENCE_COUNT_UNREAD, snapshot.science_experiment_count)
        self.assertEqual(mlib.SCIENCE_COUNT_UNREAD, snapshot.science_data_count)
        self.assertEqual(mlib.RECOVERABLE_UNREAD, snapshot.vessel_recoverable)

    def test_an_unarmed_lane_leaves_the_pools_at_the_nan_sentinel(self):
        snapshot = self._escalate(self._control(read_science=False))
        self.assertTrue(snapshot.vessel_lost)
        self.assertTrue(math.isnan(snapshot.career_funds))
        self.assertTrue(math.isnan(snapshot.career_science))

    def test_the_vessel_lost_snapshot_carries_the_perform_seam_result(self):
        # THE THIRD CARRIED FACT, and its argument is the pools' argument: a
        # successful Recover() removes the craft at once, so ISSUED and
        # vessel-gone arrive on the SAME snapshot. Dropped here, the machine's
        # issued latch would never be set on the only frame that could set it.
        c = self._control()
        c._perform_recover_vessel(_FakeVessel(recoverable=True))
        snapshot = self._escalate(c)
        self.assertTrue(snapshot.vessel_lost)
        self.assertEqual(mlib.RECOVER_REQUEST_ISSUED, snapshot.recover_request_result)

    def test_a_run_that_never_asked_leaves_that_channel_unread(self):
        # The fail-closed control for the cell above: no perform, no token, so a
        # craft that simply broke up cannot borrow a recovery it never got.
        snapshot = self._escalate(self._control())
        self.assertEqual(mlib.RECOVER_REQUEST_UNREAD, snapshot.recover_request_result)

    def test_that_snapshot_drives_the_machine_to_the_success_terminal(self):
        # End to end across the seam: ONE runner-produced vessel_lost frame,
        # carrying both career pools AND the ISSUED stamp, must be able to
        # complete the recovery terminal by itself.
        state = _asked(in_recover())
        c = self._control()
        c._perform_recover_vessel(_FakeVessel(recoverable=True))
        snapshot = self._escalate(c)
        state, _ = mlib.sbr_decide(state, snapshot)
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)

    def test_the_same_frame_without_the_stamp_is_a_break_up(self):
        # The negative control that makes the cell above mean something: the
        # runner DECLINED the verb (Recoverable false at perform), the craft went
        # away anyway, and the identical vessel_lost frame must NOT certify.
        state = _asked(in_recover())
        c = self._control()
        c._perform_recover_vessel(_FakeVessel(recoverable=False))
        snapshot = self._escalate(c)
        self.assertEqual(mlib.RECOVER_REQUEST_DECLINED, snapshot.recover_request_result)
        state, _ = mlib.sbr_decide(state, snapshot)
        self.assertNotEqual(mlib.SBR_RECOVERED, state.phase)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-before-recovery", state.loss_reason)


# ---------------------------------------------------------------------------
# 4. Schema / contract sync.
# ---------------------------------------------------------------------------


class SchemaSyncTests(unittest.TestCase):
    """The schema is what a spec is validated against; the params builder is what
    the machine actually reads. Nothing else checks that they agree, and a
    divergence costs a live flight to discover."""

    @classmethod
    def setUpClass(cls):
        with open(SCHEMA_PATH, "rb") as fh:
            cls.schema = tomllib.load(fh)
        cls.params = cls.schema["params"]

    def test_the_committed_params_validate_against_the_committed_schema(self):
        import hlib
        self.assertEqual([], hlib._validate_mission_params(PARAMS, self.schema))

    def test_the_flight_half_is_b1s_key_set_exactly(self):
        with open(os.path.join(_MISSIONS, "b1_pad_hop.schema.toml"), "rb") as fh:
            b1 = tomllib.load(fh)["params"]
        for key, decl in b1.items():
            self.assertIn(key, self.params,
                          "the flight half delegates to b1_params_from_dict, so every "
                          "B1 key must be declared here too")
            self.assertEqual(decl.get("required"), self.params[key].get("required"))
            self.assertEqual(decl.get("type"), self.params[key].get("type"))

    def test_every_career_key_is_required(self):
        for key in ("collectMinExperiments", "collectTimeoutSeconds",
                    "transmitMinScienceGain", "transmitTimeoutSeconds",
                    "recoverMinFundsGain", "recoverTimeoutSeconds"):
            self.assertTrue(self.params[key]["required"], key)

    def test_the_transmit_floor_cannot_be_authored_at_zero(self):
        # AT 0.0 the gate `gain >= floor` is satisfied by any two finite readings,
        # including two identical ones, so a transmit that credited nothing would
        # pass. The schema is the guard, not the predicate.
        self.assertGreater(self.params["transmitMinScienceGain"]["min"], 0.0)

    def test_the_collect_minimum_cannot_be_authored_at_zero(self):
        self.assertGreaterEqual(self.params["collectMinExperiments"]["min"], 1)

    def test_the_recover_floor_may_be_zero_and_that_is_the_documented_asymmetry(self):
        # SAFE only because of how the gate is built, and the two facts are pinned
        # together on purpose: a review found this floor sitting on top of a gate
        # that compared the baseline against the merely LATEST reading, so an
        # exploded craft satisfied `0.0 >= 0.0` and was certified RECOVERED. The
        # floor is legal because the terminal ALSO requires the craft observed
        # gone AND a credit measured across the recovery (see
        # SbrRecoverTests.test_at_a_zero_floor_a_purely_pre_loss_funds_pair_never_certifies).
        # If that structure is ever weakened, this floor must go positive with it.
        self.assertEqual(0.0, self.params["recoverMinFundsGain"]["min"])

    def test_every_declared_key_is_read_by_the_params_builder(self):
        # A key the schema requires and the builder ignores is a spec surface that
        # does nothing - the shape of a silent capability gap.
        defaults = mlib.sbr_params_from_dict({})
        probe = dict(PARAMS)
        for key in ("collectMinExperiments", "collectTimeoutSeconds",
                    "transmitMinScienceGain", "transmitTimeoutSeconds",
                    "recoverMinFundsGain", "recoverTimeoutSeconds"):
            probe[key] = 777
            built = mlib.sbr_params_from_dict(probe)
            self.assertNotEqual(getattr(defaults, _CAREER_FIELDS[key]),
                                getattr(built, _CAREER_FIELDS[key]), key)
            probe[key] = PARAMS[key]

    def test_the_flight_params_are_built_from_the_same_dict(self):
        built = mlib.sbr_params_from_dict(PARAMS)
        self.assertEqual(mlib.b1_params_from_dict(PARAMS), built.flight)

    def test_the_dwell_is_declared_OPTIONAL_and_that_is_the_contract(self):
        # REQUIRED would break every committed spec that flies this mission at
        # ADMIT, and worse, it would make "no dwell" unspellable - which is the
        # state L3's pinned measurements were taken in.
        decl = self.params["preRecoverDwellSeconds"]
        self.assertFalse(decl["required"])
        self.assertEqual("number", decl["type"])
        self.assertEqual(0.0, decl["min"])
        self.assertEqual(120.0, decl["max"])

    def test_the_dwell_range_is_CLOSED_at_admit_in_both_directions(self):
        import hlib
        decl = self.params["preRecoverDwellSeconds"]
        self.assertEqual([], hlib._check_param_type("preRecoverDwellSeconds", 0.0, decl))
        self.assertEqual([], hlib._check_param_type("preRecoverDwellSeconds", 12.0, decl))
        for bad in (-1.0, 120.1, 1200):
            self.assertNotEqual(
                [], hlib._check_param_type("preRecoverDwellSeconds", bad, decl),
                "a %r dwell must die at ADMIT, before a KSP process starts" % (bad,))
        # A dwell-carrying params set still validates whole.
        self.assertEqual([], hlib._validate_mission_params(
            params(preRecoverDwellSeconds=12.0), self.schema))

    def test_the_dwell_is_read_by_the_params_builder_and_defaults_to_no_hold(self):
        # The declared-key-does-nothing shape, checked for this key specifically:
        # it is the only OPTIONAL career key, so the required-key loop above
        # cannot cover it.
        self.assertEqual(0.0, mlib.sbr_params_from_dict({}).pre_recover_dwell)
        self.assertEqual(0.0, mlib.sbr_params_from_dict(PARAMS).pre_recover_dwell)
        self.assertEqual(12.0, mlib.sbr_params_from_dict(
            params(preRecoverDwellSeconds=12.0)).pre_recover_dwell)


_CAREER_FIELDS = {
    "collectMinExperiments": "collect_min_experiments",
    "collectTimeoutSeconds": "collect_timeout",
    "transmitMinScienceGain": "transmit_min_science",
    "transmitTimeoutSeconds": "transmit_timeout",
    "recoverMinFundsGain": "recover_min_funds",
    "recoverTimeoutSeconds": "recover_timeout",
}


class ChannelContractTests(unittest.TestCase):

    def test_every_new_channel_defaults_to_its_fail_closed_sentinel(self):
        # Every mission but this lane leaves them alone, so their defaults have to
        # be the values that satisfy nothing.
        s = mlib.TelemetrySnapshot()
        self.assertEqual(mlib.SCIENCE_COUNT_UNREAD, s.science_experiment_count)
        self.assertEqual(mlib.SCIENCE_COUNT_UNREAD, s.science_data_count)
        self.assertEqual(mlib.SCIENCE_COUNT_UNREAD, s.science_available_count)
        self.assertEqual(mlib.RECOVERABLE_UNREAD, s.vessel_recoverable)
        self.assertTrue(math.isnan(s.career_funds))
        self.assertTrue(math.isnan(s.career_science))
        # The perform-seam channel, on the seam_command_result shape: "" means no
        # verb has terminated, and it satisfies nothing.
        self.assertEqual(mlib.RECOVER_REQUEST_UNREAD, s.recover_request_result)
        self.assertEqual("", mlib.RECOVER_REQUEST_UNREAD)

    def test_the_issued_token_is_distinct_from_every_decline(self):
        # A rename that collapsed one into the other would make a declined ask
        # certify, which is the exact defect this channel was added for.
        self.assertNotIn(mlib.RECOVER_REQUEST_ISSUED, mlib.RECOVER_REQUEST_DECLINES)
        self.assertEqual(3, len(set(mlib.RECOVER_REQUEST_DECLINES)))
        self.assertNotIn(mlib.RECOVER_REQUEST_UNREAD, mlib.RECOVER_REQUEST_DECLINES)

    def test_the_unread_sentinel_is_distinct_from_a_real_zero(self):
        self.assertNotEqual(0, mlib.SCIENCE_COUNT_UNREAD)
        self.assertNotEqual(mlib.RECOVERABLE_NO, mlib.RECOVERABLE_UNREAD)

    def test_the_new_channels_stay_out_of_the_shared_status_surfaces(self):
        # The seam_command_result precedent: a key added to either moves the
        # status-file block or the machine line of EVERY mission in the library.
        keys = set(mlib.snapshot_dict(mlib.TelemetrySnapshot()))
        for field in ("science_experiment_count", "science_data_count",
                      "science_available_count", "vessel_recoverable",
                      "career_funds", "career_science",
                      "recover_request_result"):
            self.assertNotIn(field, keys)
        machine_attrs = {attr for attr, _key in mlib.MACHINE_STATE_FIELDS}
        self.assertNotIn("career_funds", machine_attrs)

    def test_the_tail_phase_names_cannot_collide_with_the_delegated_legs(self):
        # `SbrState.phase` is ONE field that mirrors the B1 sub-machine and then
        # walks the tail. A name shared between the two halves would make a phase
        # budget, a status-file line and a phases_reached entry ambiguous about
        # which machine produced it.
        self.assertEqual((mlib.SBR_COLLECT, mlib.SBR_TRANSMIT, mlib.SBR_RECOVER,
                          mlib.SBR_RECOVERED), mlib.SBR_TAIL_PHASES)
        self.assertEqual(set(), set(mlib.SBR_TAIL_PHASES) & set(mlib.B1_PHASES))

    def test_the_three_action_kinds_are_distinct_and_stable(self):
        kinds = [mlib.ACTION_RUN_SCIENCE_EXPERIMENTS, mlib.ACTION_TRANSMIT_SCIENCE,
                 mlib.ACTION_RECOVER_VESSEL]
        self.assertEqual(3, len(set(kinds)))
        # LITERALS, not the constants: these strings are the wire between the pure
        # machine and the runner's perform table, and a rename must be deliberate.
        self.assertEqual(["run_science_experiments", "transmit_science",
                          "recover_vessel"], kinds)

    def test_the_runner_has_a_perform_branch_for_each(self):
        import inspect
        src = inspect.getsource(mission_runner.KrpcMissionControl.perform)
        for kind in ("ACTION_RUN_SCIENCE_EXPERIMENTS", "ACTION_TRANSMIT_SCIENCE",
                     "ACTION_RECOVER_VESSEL"):
            self.assertIn(kind, src,
                          "an action with no perform branch raises "
                          "'unknown action kind' and ends the mission as an ERROR")


class HandoffContractTests(unittest.TestCase):

    def test_the_mission_declares_what_it_does_not_verify(self):
        contract = mlib.mission_handoff_contract("science_bench_recover")
        self.assertIsNotNone(contract)
        self.assertEqual(mlib.SBR_RECOVERED, contract["terminal"])
        self.assertEqual(["ledgerScienceCapture", "ledgerRecoveryCapture"],
                         contract["unverifiedByMission"])

    def test_the_contract_is_a_copy_not_the_module_constant(self):
        contract = mlib.mission_handoff_contract("science_bench_recover")
        contract["unverifiedByMission"].append("mutated")
        self.assertNotIn(
            "mutated",
            mlib.MISSION_HANDOFF_CONTRACTS["science_bench_recover"]["unverifiedByMission"])

    def test_the_ok_reason_names_the_gap_and_its_owners(self):
        reason = mlib.handoff_ok_reason("science_bench_recover",
                                        "all telemetry assertions met")
        self.assertIn("ledgerScienceCapture", reason)
        self.assertIn("ledgerOracle", reason)

    def test_eva4s_contract_is_untouched(self):
        # The table had exactly one entry until now; adding a second must not move
        # the first, whose wording is live-proven.
        self.assertEqual(["kerbalSurvival"],
                         mlib.MISSION_HANDOFF_CONTRACTS["eva4_atmo_chute"]
                         ["unverifiedByMission"])


class ShellWiringTests(unittest.TestCase):

    def test_the_shell_arms_both_opt_in_channels(self):
        c = science_bench_recover.make_control()
        self.assertTrue(c._read_science,
                        "without it every career gate fails closed and the mission "
                        "flakes science-channel-dark six frames after it lands")
        self.assertTrue(c._read_chute,
                        "inherited from B1 and load-bearing: the delegated canopy "
                        "latch and DOWN terminal gate on the OBSERVED chute state")
        self.assertFalse(c._use_mechjeb)

    def test_the_shell_tolerates_the_career_saves_unreadable_maneuver_nodes(self):
        # THE CELL THE FIRST READING RUN BOUGHT. `L3-career-science-recover` run
        # `2026-08-19_1817` (and its identical retry `_1818_a2`) died in 1.2 s at
        # PRELAUNCH: this lane flies a CAREER save, kRPC raises `Maneuver node
        # editing is not available` on an un-upgraded Tracking Station, and three
        # consecutive raises escalate to a `vessel_lost` snapshot the delegated B1
        # leg correctly condemns (`flight-leg vessel-lost (unreadable after
        # repeated telemetry failures)`). CL-3 already carried the identical
        # finding; this lane is the second career flier.
        #
        # The flag is NOT free - turning it on globally broke CL-1, whose
        # `crew-survived-impact` terminal a PAD frame satisfies once blind frames
        # become believable - so this cell exists to say the opt-in is DELIBERATE
        # and to red if it is ever dropped as noise. The reason it is safe here is
        # structural: B1's phase progression cannot be walked from the pad, and
        # `flightCompletedObserved` additionally gates on the peak apoapsis.
        c = science_bench_recover.make_control()
        self.assertTrue(c._tolerate_unreadable_nodes,
                        "without it every career-save flight of this lane dies "
                        "vessel-lost in PRELAUNCH; measured 2026-08-19")

    def test_the_shell_skips_the_settle_tail(self):
        # The RECOVERED terminal means the craft has been removed from the game,
        # so a settle tail would gather only vessel_lost frames.
        self.assertEqual(0, science_bench_recover.SPEC.settle_frames)

    def test_the_shell_imports_with_no_krpc_installed(self):
        self.assertNotIn("krpc", sys.modules,
                         "import krpc must stay inside KrpcMissionControl.open so the "
                         "base interpreter can import every shell")


# ---------------------------------------------------------------------------
# 5. THE FIXTURE THIS LANE FLIES.
#
# `career-science-pad` is built BY CONSTRUCTION from `career-pad-craft` by
# `harness/tools/build_career_science_pad.py`. Two classes keep that honest, and
# they are the CL-1 pair (`SpecFixtureSyncTests` + `FixtureDriftTests` in
# `test_cl1_crew_loss.py`) pointed at this lane: one pairs the committed SPEC
# against the committed fixture BYTES, the other pairs the committed fixture
# bytes against what the recipe produces from the CURRENT base.
#
# Unwired, "the fixture is derived by a committed script" is prose with a
# shebang: if `career-pad-craft` moves, the derived fixture silently stops being
# what the recipe produces, and the first thing that notices is a live flight.
# ---------------------------------------------------------------------------

SPEC_PATH = os.path.join(_HARNESS, "scenarios", "L3-career-science-recover.toml")
FIXTURE_DIR = os.path.join(_HARNESS, "fixtures", "saves", "career-science-pad")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")


def _load_science_pad_builder():
    import importlib.util
    path = os.path.join(_HARNESS, "tools", "build_career_science_pad.py")
    spec = importlib.util.spec_from_file_location("build_career_science_pad", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class CareerSciencePadSpecFixtureSyncTests(unittest.TestCase):
    """The SPEC and the FIXTURE must agree, and nothing else checks that.

    Getting the pairing wrong costs a live flight to discover, which is how this
    lane learned the pairing mattered in the first place: two runs on 2026-08-19
    flew a textbook profile against a craft that stock forbids transmitting
    from."""

    @classmethod
    def setUpClass(cls):
        with open(SPEC_PATH, "rb") as fh:
            cls.spec = tomllib.load(fh)
        with open(FIXTURE_SFS, "r", encoding="utf-8", newline="") as fh:
            cls.sfs = fh.read().replace("\r\n", "\n").split("\n")
        cls.builder = _load_science_pad_builder()

    def test_the_spec_points_at_the_science_pad_fixture(self):
        self.assertEqual("fixtures/saves/career-science-pad",
                         self.spec["fixture"]["saveTemplate"])

    def test_the_fixture_is_a_career_save(self):
        self.assertIn("\tMode = CAREER", self.sfs)

    def test_the_fixture_carries_exactly_one_vessel(self):
        self.assertEqual(1, sum(1 for l in self.sfs if l.strip() == "VESSEL"))

    def test_the_fixture_carries_a_direct_antenna(self):
        # THE WHOLE REASON THIS FIXTURE EXISTS. `SurfAntenna` is the part name of
        # the Communotron 16-S, whose cfg declares `antennaType = DIRECT`; the
        # pod's built-in transmitter is `INTERNAL`, which stock's
        # `ModuleDataTransmitter.CanTransmit()` rejects outright, before CommNet.
        # The name IS the assertion because `antennaType` lives in the part cfg
        # and never in the save.
        #
        # ANCHORED TO THE VESSEL'S OWN PART LIST, not to a line match: a
        # `STOREDPART` in an inventory module carries a `name = ...` line too, and
        # a stowed antenna in a cargo container is NOT a transmitter kRPC can
        # enumerate. Only a direct `PART` child of the VESSEL is one.
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        vessel = builder.child_nodes(
            lines, builder.find_node(lines, "FLIGHTSTATE"), "VESSEL")[0]
        names = [builder.get_value(lines, part, "name")
                 for part in builder.part_nodes(lines, vessel)]
        self.assertIn("SurfAntenna", names,
                      "without a DIRECT antenna ATTACHED to the vessel, the "
                      "TRANSMIT phase runs its whole budget and credits nothing "
                      "(`transmit-credited-no-science`, measured 2026-08-19)")

    def test_the_fixture_carries_an_inert_parsek_scenario_node(self):
        # Load-bearing and it cost a flight to learn (CL-1 flight 1): without the
        # node the FLIGHT focus route never creates the ScenarioModule, so OnSave
        # never runs and the whole flight is recorded in memory and thrown away.
        # INERT: the node must carry no Parsek payload, or the run would start
        # from a store this spec's `recordings.count` window does not describe.
        self.assertIn("\t\tname = ParsekScenario", self.sfs)
        for forbidden in ("RECORDING", "RECORDING_TREE", "GAME_ACTION", "LEDGER"):
            self.assertNotIn("\t\t%s" % forbidden, self.sfs,
                             "the fixture's ParsekScenario node must be inert")


class CareerSciencePadFixtureDriftTests(unittest.TestCase):
    """WIRES `build_career_science_pad.py --check` INTO THE SUITE.

    Same discipline as `FixtureDriftTests` in `test_cl1_crew_loss.py`, one layer
    further down the derivation chain: `career-science-pad` is derived from
    `career-pad-craft`, which is itself derived from `fresh-career` +
    `b1-pad-craft`. CL-1's cell guards the first hop; these guard the second, so
    a move anywhere up the chain reds in the suite rather than in a flight."""

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_science_pad_builder()

    def _base_lines(self):
        return self.builder.read_lines(
            os.path.join(_HARNESS, "fixtures", "saves",
                         self.builder.BASE_NAME, "persistent.sfs"))

    def test_the_committed_fixture_satisfies_every_post_condition(self):
        # The `--check` path, run in-process. Includes the BASE builder's own
        # post-conditions, which `verify` layers in rather than restating.
        problems = self.builder.verify(self.builder.read_lines(FIXTURE_SFS),
                                       self._base_lines(),
                                       self.builder.CREW_NAME)
        self.assertEqual([], problems)

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_rebuild(self):
        # THE DRIFT GUARD, and it compares RAW BYTES rather than the line lists
        # `read_lines` hands back: that helper normalizes CRLF to LF on the way
        # in, so a line-list comparison would call a fixture that had lost its
        # CRLFs (or grown a BOM, or a trailing byte) identical to one that had
        # not - and "byte-identical" is the claim this cell's name makes. The
        # encoding here is exactly what `write_lines` would emit: UTF-8, CRLF
        # joins, no added sentinel.
        rebuilt = self.builder.build(
            self._base_lines(),
            "%s (CAREER)" % self.builder.TARGET_NAME)
        produced = "\r\n".join(rebuilt).encode("utf-8")
        with open(FIXTURE_SFS, "rb") as fh:
            committed = fh.read()
        if committed != produced:
            offset = next(
                (i for i, (a, b) in enumerate(zip(committed, produced))
                 if a != b),
                min(len(committed), len(produced)))
            self.fail("career-science-pad has drifted from what "
                      "build_career_science_pad.py produces from the current "
                      "career-pad-craft; re-run the builder and commit, or "
                      "explain the divergence. First difference at byte %d "
                      "(committed %d bytes, rebuilt %d bytes)"
                      % (offset, len(committed), len(produced)))

    def test_the_splice_left_the_base_craft_byte_identical(self):
        # THE PROMISE TO THE SIX SPECS THAT FLY THE BASE (CL-1, CL-2, CL-3, H26,
        # L2, R7a). The splice is
        # additive: the eight parts `career-pad-craft` flies must survive here
        # unchanged, so B1's measured flight profile still transfers and nothing
        # about the base fixture is re-opened by this one.
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        base = self._base_lines()
        base_parts = builder.part_nodes(
            base, builder.child_nodes(
                base, builder.find_node(base, "FLIGHTSTATE"), "VESSEL")[0])
        parts = builder.part_nodes(
            lines, builder.child_nodes(
                lines, builder.find_node(lines, "FLIGHTSTATE"), "VESSEL")[0])
        self.assertEqual(8, len(base_parts))
        self.assertEqual(8 + len(builder.SPLICE_PARTS), len(parts))
        for index, (base_part, part) in enumerate(zip(base_parts, parts)):
            self.assertEqual(base[base_part[0]:base_part[1]],
                             lines[part[0]:part[1]],
                             "base part %d is no longer byte-identical" % index)

    def test_the_fixture_carries_the_electric_charge_a_transmit_costs(self):
        # The antenna alone would swap one terminal for another. Stock charges
        # `packetResourceCost` per `packetSize` Mits: 156 EC for the three
        # experiments aboard, against the pod's own 50. The derivation is in the
        # builder's module docstring; this cell is what keeps the fixture from
        # drifting under it.
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        vessel = builder.child_nodes(
            lines, builder.find_node(lines, "FLIGHTSTATE"), "VESSEL")[0]
        total = sum(builder._resource_total(lines, p, "ElectricCharge")
                    for p in builder.part_nodes(lines, vessel))
        self.assertGreaterEqual(total, builder.TRANSMIT_EC_REQUIRED)

    def test_the_loadmeta_agrees_with_the_committed_save(self):
        builder = self.builder
        lines = builder.read_lines(FIXTURE_SFS)
        meta = builder.read_lines(
            os.path.join(FIXTURE_DIR, "persistent.loadmeta"))
        self.assertIn("vesselCount = 1", meta)
        self.assertIn("gameMode = CAREER", meta)
        fs = builder.find_node(lines, "FLIGHTSTATE")
        self.assertIn("UT = %s" % builder.get_value(lines, fs, "UT"), meta)


if __name__ == "__main__":
    unittest.main()
