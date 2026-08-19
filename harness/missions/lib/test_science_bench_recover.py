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
        self.assertTrue(state.recover_requested)

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
        self.assertFalse(state.recover_requested)

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
        state = drive(state, snap(ut=14.0, vessel_lost=True, career_funds=1500.0,
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
        self.assertFalse(state.recover_requested)
        state = drive(state, snap(ut=14.0, vessel_lost=True, career_funds=1000.0))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-before-recovery", state.loss_reason)
        self.assertNotEqual(mlib.SBR_RECOVERED, state.phase)

    def test_at_a_zero_floor_a_purely_pre_loss_funds_pair_never_certifies(self):
        # THE BLOCKER, half two. The credit must straddle the event. With the
        # post-loss pool dark, `fundsGain` is a perfectly finite 0.0 and the floor
        # is 0.0 - the old gate read that as a recovery. It must now reach the
        # grace terminal instead, because nothing was observed ACROSS the
        # recovery.
        state = self._in_recover(recoverMinFundsGain=0.0)
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        self.assertTrue(state.recover_requested)
        self.assertEqual(0.0, mlib._sbr_funds_gain(state))
        self.assertIsNone(mlib._sbr_recovery_credit(state))
        state = drive(state, *[snap(ut=14.0 + i, vessel_lost=True,
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
        state = drive(state, snap(ut=14.0, vessel_lost=True, career_funds=1000.0))
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
        state = drive(state, snap(ut=14.0, vessel_lost=True, career_funds=1500.0))
        rows = {o.name: o for o in mlib.evaluate_sbr_assertions(
            [], mlib.sbr_params_from_dict(PARAMS), state)}
        self.assertEqual(mlib._sbr_recovery_credit(state),
                         rows["vesselRecoveredObserved"].value)

    def test_the_recover_budget_expiry_names_a_request_that_was_made(self):
        # The likelier live shape of `recover-never-completed`: we asked and the
        # craft never went away. Only the not-asked path was covered before.
        state = self._in_recover(recoverTimeoutSeconds=30)
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        self.assertTrue(state.recover_requested)
        state = drive(state, *[sci(ut=14.0 + i * 20, situation="LANDED",
                                   vessel_recoverable=mlib.RECOVERABLE_YES)
                               for i in range(5)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("recover-never-completed", state.flake_reason)
        self.assertIn("requested=True", state.flake_reason)

    def test_the_craft_gone_with_no_credit_is_a_break_up_not_a_recovery(self):
        # A destroyed craft is also "gone". Only the funds credit tells them apart,
        # which is why the terminal needs both conjuncts.
        state = self._in_recover()
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        state = drive(state, *[snap(ut=14.0 + i, vessel_lost=True, career_funds=1000.0)
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
        state = drive(state, *[snap(ut=14.0 + i, vessel_lost=True,
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
        state = drive(state, *[snap(ut=14.0 + i, vessel_lost=True, career_funds=1000.0)
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
        state = drive(state, *[snap(ut=14.0 + i, vessel_lost=True,
                                    career_funds=float("nan")) for i in range(lag)])
        self.assertFalse(state.done)
        state = drive(state, snap(ut=99.0, vessel_lost=True, career_funds=1500.0))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)

    def test_the_recover_budget_expiry_is_a_flake_naming_whether_it_asked(self):
        state = self._in_recover(recoverTimeoutSeconds=10)
        state = drive(state, *[sci(ut=12.0 + i * 5, situation="LANDED",
                                   vessel_recoverable=mlib.RECOVERABLE_UNREAD)
                               for i in range(5)])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIn("recover-never-completed", state.flake_reason)
        self.assertIn("requested=False", state.flake_reason)


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
        state = drive(state, *[snap(ut=14.0 + i, vessel_lost=True, career_funds=1000.0)
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
        return drive(state, snap(ut=14.0, vessel_lost=True, career_funds=1500.0,
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
        self.assertTrue(detail["recoveryRequested"])

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
    frames += [snap(ut=14.0, vessel_lost=True, career_funds=1500.0, career_science=25.0)]
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
        control()._perform_recover_vessel(v)
        self.assertEqual(1, v.recovered)

    def test_an_unrecoverable_craft_is_never_asked(self):
        # READ BEFORE ASK: kRPC's Recover() throws on exactly this, and the throw
        # would report MISSION-ERROR instead of the machine's named terminal.
        v = _FakeVessel(recoverable=False)
        control()._perform_recover_vessel(v)
        self.assertEqual(0, v.recovered)

    def test_an_unreadable_recoverable_flag_is_never_asked(self):
        v = _FakeVessel(recoverable_raises=True)
        control()._perform_recover_vessel(v)
        self.assertEqual(0, v.recovered)

    def test_a_throwing_recover_never_escapes_perform(self):
        v = _FakeVessel(recoverable=True, recover_raises=True)
        control()._perform_recover_vessel(v)   # must not raise
        self.assertEqual(0, v.recovered)


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

    def test_that_snapshot_drives_the_machine_to_the_success_terminal(self):
        # End to end across the seam: a runner-produced vessel_lost frame alone
        # must be able to complete the recovery terminal.
        state = in_recover()
        state = drive(state,
                      sci(ut=12.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES),
                      sci(ut=13.0, situation="LANDED", vessel_recoverable=mlib.RECOVERABLE_YES))
        snapshot = self._escalate(self._control())
        state, _ = mlib.sbr_decide(state, snapshot)
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)


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

    def test_the_unread_sentinel_is_distinct_from_a_real_zero(self):
        self.assertNotEqual(0, mlib.SCIENCE_COUNT_UNREAD)
        self.assertNotEqual(mlib.RECOVERABLE_NO, mlib.RECOVERABLE_UNREAD)

    def test_the_new_channels_stay_out_of_the_shared_status_surfaces(self):
        # The seam_command_result precedent: a key added to either moves the
        # status-file block or the machine line of EVERY mission in the library.
        keys = set(mlib.snapshot_dict(mlib.TelemetrySnapshot()))
        for field in ("science_experiment_count", "science_data_count",
                      "science_available_count", "vessel_recoverable",
                      "career_funds", "career_science"):
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

    def test_the_shell_skips_the_settle_tail(self):
        # The RECOVERED terminal means the craft has been removed from the game,
        # so a settle tail would gather only vessel_lost frames.
        self.assertEqual(0, science_bench_recover.SPEC.settle_frames)

    def test_the_shell_imports_with_no_krpc_installed(self):
        self.assertNotIn("krpc", sys.modules,
                         "import krpc must stay inside KrpcMissionControl.open so the "
                         "base interpreter can import every shell")


if __name__ == "__main__":
    unittest.main()
