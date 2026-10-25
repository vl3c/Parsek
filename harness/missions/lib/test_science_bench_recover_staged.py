"""science_bench_recover STAGED SCIENCE: pad readings before ignition, an in-flight
run at COAST, and the transmit-skipped tail (`transmitMaxExperiments = 0`).

The shape is CS-1-science-first-hop's after its first reading runs: a start-node
Jumping Flea in a new Science game cannot transmit science (stock's
CommNetVessel.CanScience needs a non-INTERNAL antenna and the Mk1 pod carries only
an INTERNAL one), so the lane takes a reading in every situation the hop passes
through - Goo on the pad, Goo and a crew report in low flight - and recovers it all.

Every cell drives the pure machine or the runner's perform seam; nothing here
needs kRPC. ASCII only.
"""

from __future__ import annotations

import dataclasses
import math
import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
_HARNESS = os.path.dirname(_MISSIONS)
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if os.path.join(_HARNESS, "lib") not in sys.path:
    sys.path.insert(0, os.path.join(_HARNESS, "lib"))

import mlib                                  # noqa: E402
import mission_runner                        # noqa: E402
import test_science_bench_recover as base_t  # noqa: E402

NAN = float("nan")

STAGED_KEYS = {"recoverCreditPool": "science", "recoverMinScienceGain": 5.0,
               "transmitMaxExperiments": 0, "collectMinExperiments": 3,
               "padExperiments": ["mysteryGoo"],
               "flightExperiments": ["mysteryGoo", "crewReport"]}


def stparams(**over):
    out = base_t.params(**STAGED_KEYS)
    out.update(over)
    return out


def fresh(**over):
    return mlib.sbr_initial_state(mlib.sbr_params_from_dict(stparams(**over)))


def ssci(**kw):
    """A live Science-game frame: funds read NaN, three experiments aboard."""
    kw.setdefault("career_funds", NAN)
    kw.setdefault("science_experiment_count", 3)
    kw.setdefault("science_available_count", 3)
    return base_t.sci(**kw)


def pad(ut, data):
    return ssci(ut=ut, stage_solid_fuel=1.0, apoapsis=0, situation="PRE_LAUNCH",
                science_data_count=data)


def kinds(actions):
    return [a.kind for a in actions]


def through_pad(state=None):
    """Drive PAD_COLLECT to the ignition frame; return (state, that frame's actions)."""
    state = state or fresh()
    state, _ = mlib.sbr_decide(state, pad(100.0, 0))
    state, _ = mlib.sbr_decide(state, pad(101.0, 1))
    return mlib.sbr_decide(state, pad(102.0, 1))


# The hop after ignition: burn (ASCENT), burnout (COAST entry), apex, canopy,
# landed. Data counts climb from the pad's 1 to 3 in low flight.
def hop_frames(coast_data=3):
    return [
        ssci(ut=103.0, stage_solid_fuel=0.5, apoapsis=14000, situation="FLYING",
             science_data_count=1),
        ssci(ut=104.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING",
             science_data_count=1),
        ssci(ut=105.0, vertical_speed=5.0, apoapsis=14000, situation="FLYING",
             science_data_count=coast_data),
        ssci(ut=106.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING",
             science_data_count=coast_data),
        ssci(ut=107.0, altitude=5000, apoapsis=14000, situation="FLYING",
             craft_chute_state=mlib.CHUTE_STATE_SEMI_DEPLOYED,
             science_data_count=coast_data),
        ssci(ut=108.0, altitude=2000, apoapsis=14000, situation="FLYING",
             craft_chute_state=mlib.CHUTE_STATE_DEPLOYED, science_data_count=coast_data),
    ]


def landed(ut, data=3, **kw):
    return ssci(ut=ut, altitude=100, apoapsis=14000, situation="LANDED",
                craft_chute_state=mlib.CHUTE_STATE_DEPLOYED, science_data_count=data, **kw)


def flown(coast_data=3):
    state, _ = through_pad()
    state, emitted = base_t.drive_actions(state, *hop_frames(coast_data))
    state, actions = mlib.sbr_decide(state, landed(109.0, coast_data))
    return state, emitted + actions


class StagedDefaultsTests(unittest.TestCase):
    """Absent keys are the landed-only machine, byte for byte at the decision."""

    def test_no_pad_list_starts_at_the_legs_own_prelaunch_and_ignites(self):
        state = base_t.fresh()
        self.assertEqual(mlib.B1_PRELAUNCH, state.phase)
        state, actions = mlib.sbr_decide(state, base_t.FLIGHT_FRAMES[0])
        self.assertEqual([mlib.ACTION_SET_THROTTLE, mlib.ACTION_ACTIVATE_STAGE],
                         kinds(actions))

    def test_the_builder_defaults(self):
        p = mlib.sbr_params_from_dict(base_t.params())
        self.assertEqual((), p.pad_experiments)
        self.assertEqual((), p.flight_experiments)
        self.assertEqual(60.0, p.pad_collect_timeout)
        self.assertFalse(p.transmit_skipped)

    def test_no_staged_row_without_staged_keys(self):
        state, _ = base_t.flown()
        names = [r.name for r in mlib.evaluate_sbr_assertions([], state.params, state)]
        self.assertNotIn("stagedScienceObserved", names)
        self.assertIn("scienceTransmittedObserved", names)


class PadCollectTests(unittest.TestCase):

    def test_the_machine_starts_in_pad_collect(self):
        state = fresh()
        self.assertEqual(mlib.SBR_PAD_COLLECT, state.phase)
        self.assertEqual((mlib.SBR_PAD_COLLECT,), state.phases_reached)

    def test_the_first_frame_runs_the_pad_list_and_does_not_ignite(self):
        state, actions = mlib.sbr_decide(fresh(), pad(100.0, 0))
        self.assertEqual([mlib.ACTION_RUN_SCIENCE_EXPERIMENTS], kinds(actions))
        self.assertEqual("mysteryGoo", actions[0].text)
        self.assertEqual(100.0, state.phase_entry_ut)

    def test_ignition_waits_for_the_debounced_data(self):
        state, _ = mlib.sbr_decide(fresh(), pad(100.0, 0))
        state, actions = mlib.sbr_decide(state, pad(101.0, 0))
        self.assertEqual([], kinds(actions))
        state, actions = mlib.sbr_decide(state, pad(102.0, 1))
        self.assertEqual([], kinds(actions), "one agreeing frame is not the debounce")
        self.assertEqual(mlib.SBR_PAD_COLLECT, state.phase)

    def test_the_debounced_frame_hands_itself_to_the_leg_which_ignites(self):
        state, actions = through_pad()
        self.assertEqual([mlib.ACTION_SET_THROTTLE, mlib.ACTION_ACTIVATE_STAGE],
                         kinds(actions))
        self.assertEqual(mlib.B1_ASCENT, state.phase)
        self.assertEqual((mlib.SBR_PAD_COLLECT, mlib.B1_PRELAUNCH, mlib.B1_ASCENT),
                         state.phases_reached)
        self.assertEqual(1, state.pad_data_observed)

    def test_the_reemit_carries_the_name_filter(self):
        state, _ = mlib.sbr_decide(fresh(), pad(100.0, 0))
        emitted = []
        for i in range(mlib.SBR_ACTION_REEMIT_FRAMES):
            state, actions = mlib.sbr_decide(state, pad(100.5 + i * 0.1, 0))
            emitted.extend(actions)
        self.assertEqual(1, len(emitted))
        self.assertEqual("mysteryGoo", emitted[0].text)

    def test_no_data_through_the_budget_is_an_assert_fail(self):
        state, _ = mlib.sbr_decide(fresh(), pad(100.0, 0))
        state, _ = mlib.sbr_decide(state, pad(161.0, 0))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("pad-collect-produced-no-data", state.loss_reason)
        self.assertIn("mysteryGoo", state.loss_reason)

    def test_the_budget_is_configurable(self):
        state, _ = mlib.sbr_decide(fresh(padCollectTimeoutSeconds=200.0), pad(100.0, 0))
        state, _ = mlib.sbr_decide(state, pad(161.0, 0))
        self.assertFalse(state.done)

    def test_a_vessel_loss_on_the_pad_is_an_assert_fail(self):
        state, _ = mlib.sbr_decide(fresh(), pad(100.0, 0))
        state, _ = mlib.sbr_decide(state, base_t.snap(ut=101.0, vessel_lost=True))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("vessel-lost-on-pad", state.loss_reason)

    def test_a_dark_channel_on_the_pad_flakes(self):
        state, _ = mlib.sbr_decide(fresh(), pad(100.0, 0))
        for i in range(mlib.SBR_SCIENCE_UNREAD_GIVEUP_FRAMES):
            state, _ = mlib.sbr_decide(state, ssci(
                ut=101.0 + i, situation="PRE_LAUNCH",
                science_experiment_count=mlib.SCIENCE_COUNT_UNREAD,
                science_data_count=mlib.SCIENCE_COUNT_UNREAD))
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertEqual(mlib.SBR_PAD_COLLECT, state.flake_phase)
        self.assertIn("science-channel-dark", state.flake_reason)


class FlightRunTests(unittest.TestCase):

    def test_the_run_fires_on_coast_entry_not_during_the_burn(self):
        state, _ = through_pad()
        frames = hop_frames()
        state, burn = mlib.sbr_decide(state, frames[0])
        self.assertNotIn(mlib.ACTION_RUN_SCIENCE_EXPERIMENTS, kinds(burn))
        state, burnout = mlib.sbr_decide(state, frames[1])
        self.assertEqual(mlib.B1_COAST, state.phase)
        runs = [a for a in burnout if a.kind == mlib.ACTION_RUN_SCIENCE_EXPERIMENTS]
        self.assertEqual(1, len(runs))
        self.assertEqual("mysteryGoo,crewReport", runs[0].text)
        self.assertIn(mlib.ACTION_CUT_THROTTLE, kinds(burnout))

    def test_the_rerun_is_bounded_and_stops_once_the_data_is_there(self):
        state, _ = through_pad()
        frames = hop_frames()
        state, _ = mlib.sbr_decide(state, frames[0])
        state, _ = mlib.sbr_decide(state, frames[1])
        emitted = []
        # A long coast with the in-flight data never appearing.
        for i in range(mlib.SBR_ACTION_REEMIT_FRAMES * (mlib.SBR_ACTION_REEMIT_LIMIT + 3)):
            state, actions = mlib.sbr_decide(state, ssci(
                ut=104.1 + i * 0.01, vertical_speed=50.0, apoapsis=14000,
                altitude=3000.0 + i, situation="FLYING", science_data_count=1))
            emitted.extend(a for a in actions if a.kind == mlib.ACTION_RUN_SCIENCE_EXPERIMENTS)
        self.assertEqual(mlib.SBR_ACTION_REEMIT_LIMIT, len(emitted))
        # Once the data is there, no further run.
        state, actions = mlib.sbr_decide(state, ssci(
            ut=110.0, vertical_speed=40.0, apoapsis=14000, situation="FLYING",
            science_data_count=3))
        self.assertNotIn(mlib.ACTION_RUN_SCIENCE_EXPERIMENTS, kinds(actions))

    def test_no_flight_run_without_a_flight_list(self):
        state, _ = through_pad(fresh(flightExperiments=[]))
        _, emitted = base_t.drive_actions(state, *hop_frames(coast_data=1))
        self.assertNotIn(mlib.ACTION_RUN_SCIENCE_EXPERIMENTS, kinds(emitted))

    def test_the_airborne_max_is_carried(self):
        state, _ = flown()
        self.assertEqual(3, state.airborne_data_max)
        self.assertEqual(mlib.SBR_COLLECT, state.phase)


class TransmitSkippedTests(unittest.TestCase):

    def test_collect_goes_straight_to_recover_with_no_transmit(self):
        state, _ = flown()
        state, emitted = base_t.drive_actions(state, landed(110.0), landed(111.0))
        self.assertEqual(mlib.SBR_RECOVER, state.phase)
        self.assertNotIn(mlib.ACTION_TRANSMIT_SCIENCE, kinds(emitted))
        self.assertNotIn(mlib.SBR_TRANSMIT, state.phases_reached)

    def test_the_whole_run_recovers_on_the_science_pool(self):
        state, _ = flown()
        state = base_t.drive(
            state, landed(110.0, career_science=0.0), landed(111.0, career_science=0.0),
            landed(112.0, career_science=0.0, vessel_recoverable=mlib.RECOVERABLE_YES),
            landed(113.0, career_science=0.0, vessel_recoverable=mlib.RECOVERABLE_YES))
        self.assertEqual(1, state.recover_asks)
        state = base_t.drive(state, base_t.lost(114.0, career_funds=NAN,
                                                career_science=13.5))
        self.assertEqual(mlib.SBR_RECOVERED, state.phase)
        self.assertIsNone(state.verdict)
        rows = {r.name: r for r in mlib.evaluate_sbr_assertions([], state.params, state)}
        self.assertNotIn("scienceTransmittedObserved", rows)
        self.assertTrue(all(r.met for r in rows.values()), rows)
        self.assertEqual(13.5, rows["vesselRecoveredObserved"].value)

    def test_the_transmitting_machine_is_unchanged_by_a_cap_of_one(self):
        self.assertFalse(mlib.sbr_params_from_dict(stparams(
            transmitMaxExperiments=1)).transmit_skipped)


class StagedAssertionTests(unittest.TestCase):

    def _row(self, state):
        rows = {r.name: r for r in mlib.evaluate_sbr_assertions([], state.params, state)}
        return rows["stagedScienceObserved"]

    def test_met_when_the_pad_and_airborne_counts_were_observed(self):
        state, _ = flown()
        row = self._row(state)
        self.assertTrue(row.met)
        self.assertEqual(1, row.detail["padObserved"])
        self.assertEqual(3, row.detail["airborneObserved"])

    def test_a_flight_run_made_up_only_on_the_ground_reds(self):
        # The in-flight data never appeared; the landed COLLECT would run the two
        # missing modules on the ground and satisfy collectMinExperiments, which
        # is exactly what this row exists to catch.
        state, _ = flown(coast_data=1)
        state = base_t.drive(state, landed(110.0, data=3), landed(111.0, data=3))
        self.assertEqual(mlib.SBR_RECOVER, state.phase)
        row = self._row(state)
        self.assertFalse(row.met)
        self.assertEqual(1, row.detail["airborneObserved"])
        self.assertEqual(3, row.detail["airborneRequired"])


class _NamedExperiment(base_t._FakeExperiment):
    def __init__(self, name, **kw):
        super().__init__(**kw)
        self.name = name


class NameFilterRunnerTests(unittest.TestCase):

    def _lines(self, fn):
        lines = []
        orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = lines.append
        try:
            fn()
        finally:
            mission_runner._stdout_sink = orig
        return lines

    def test_one_module_per_listed_name(self):
        goo1, goo2 = _NamedExperiment("mysteryGoo"), _NamedExperiment("mysteryGoo")
        crew = _NamedExperiment("crewReport")
        base_t.control()._perform_run_science(base_t._FakeVessel([goo1, goo2, crew]),
                                              "mysteryGoo")
        self.assertEqual((1, 0, 0), (goo1.ran, goo2.ran, crew.ran))

    def test_a_module_holding_data_never_consumes_a_name(self):
        goo1 = _NamedExperiment("mysteryGoo", has_data=True)
        goo2 = _NamedExperiment("mysteryGoo")
        crew = _NamedExperiment("crewReport")
        base_t.control()._perform_run_science(base_t._FakeVessel([goo1, goo2, crew]),
                                              "mysteryGoo,crewReport")
        self.assertEqual((0, 1, 1), (goo1.ran, goo2.ran, crew.ran))

    def test_an_unmatched_name_is_logged(self):
        crew = _NamedExperiment("crewReport")
        lines = self._lines(lambda: base_t.control()._perform_run_science(
            base_t._FakeVessel([crew]), "mysteryGoo,crewReport"))
        self.assertEqual(1, crew.ran)
        self.assertTrue(any("names=mysteryGoo,crewReport unmatched=mysteryGoo" in l
                            for l in lines), lines)

    def test_no_filter_runs_every_eligible_module(self):
        goo, crew = _NamedExperiment("mysteryGoo"), _NamedExperiment("crewReport")
        lines = self._lines(lambda: base_t.control()._perform_run_science(
            base_t._FakeVessel([goo, crew])))
        self.assertEqual((1, 1), (goo.ran, crew.ran))
        self.assertFalse(any("names=" in l for l in lines))

    def test_perform_routes_the_text_filter(self):
        goo, crew = _NamedExperiment("mysteryGoo"), _NamedExperiment("crewReport")
        vessel = base_t._FakeVessel([goo, crew])
        vessel.control = None
        c = base_t.PerformDispatchTests()._control_over(vessel)
        c.perform(mlib.Action(mlib.ACTION_RUN_SCIENCE_EXPERIMENTS, text="crewReport"))
        self.assertEqual((0, 1), (goo.ran, crew.ran))


class StagedSchemaTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        with open(base_t.SCHEMA_PATH, "rb") as fh:
            cls.schema = tomllib.load(fh)

    def test_the_staged_params_validate(self):
        import hlib
        self.assertEqual([], hlib._validate_mission_params(stparams(), self.schema))
        self.assertEqual([], hlib._validate_mission_params(
            stparams(padCollectTimeoutSeconds=90.0), self.schema))

    def test_the_new_keys_are_optional(self):
        for key in ("padExperiments", "flightExperiments", "padCollectTimeoutSeconds"):
            self.assertFalse(self.schema["params"][key]["required"], key)

    def test_bad_values_die_at_admit(self):
        import hlib
        for bad in ({"padExperiments": "mysteryGoo"},
                    {"flightExperiments": 3},
                    {"padCollectTimeoutSeconds": 5.0}):
            self.assertNotEqual([], hlib._validate_mission_params(stparams(**bad),
                                                                  self.schema), bad)


if __name__ == "__main__":
    unittest.main()
