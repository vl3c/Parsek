"""Unit cells for the GS-2 / GS-3 orbital-deploy machine (mlib.gs2_*) and for the
`parkAttached` parameter this lane added to the FORGE-LKO machine.

Pure, offline, krpc-free: every cell drives `mlib.gs2_decide` (or
`mlib.forge_lko_decide`) with hand-built `TelemetrySnapshot` frames, exactly as the
sibling mission suites do. Nothing here talks to KSP.

TWO THINGS THESE CELLS EXIST TO PROTECT, both of which would otherwise only red in a
live flight that costs a KSP boot:

  1. EXACTLY ONE STAGE ACTIVATION. The docking Kerbal X's next stage below the payload
     decoupler is the orbital Skipper, so a second activation would light an engine
     under a stack that is supposed to be coasting - and would move the orbit the
     terminal classification reads, which is the one input GS-2's whole assertion set
     depends on.
  2. `parkAttached` CHANGES NOTHING WHEN UNSET. FORGE-eva2-lko is live-proven and its
     fixture is committed; the default-false path must remain byte-for-byte the
     two-step SEPARATE contract it flew.

ASCII only.
"""

from __future__ import annotations

import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mlib  # noqa: E402


NOMINAL_PARAMS = {
    "settleSituations": ["ORBITING"],
    "settleDebounceFrames": 3,
    "settleTimeoutSeconds": 120,
    "deployTimeoutSeconds": 60,
    "deployDebounceFrames": 2,
    "postDeployDwellSeconds": 45,
    "postDeployDebounceFrames": 3,
    "postDeployTimeoutSeconds": 180,
    "minSafePeriapsisMeters": 75000,
    # The deployed vessel, watched by NAME through the shared sibling channel.
    "siblingVesselName": "Kerbal X Probe",
}


def orbiting(ut, vessel_count=5, periapsis=100000.0, situation="ORBITING",
             sibling_situation="", sibling_present=-1):
    """One in-gate frame of the parked stack.

    The sibling pair defaults to the UNREAD sentinel ("", -1), which is exactly what
    a frame BEFORE the deploy legitimately reads: the released vessel does not exist
    yet, so every pre-split cell below is also a test that UNREAD neither advances
    nor erases the latches."""
    return mlib.TelemetrySnapshot(ut=ut, situation=situation, apoapsis=100000.0,
                                  periapsis=periapsis, vessel_count=vessel_count,
                                  sibling_situation=sibling_situation,
                                  sibling_present=sibling_present)


def drive(state, frames):
    """Feed frames through gs2_decide; return (final_state, [actions per frame])."""
    emitted = []
    for f in frames:
        state, actions = mlib.gs2_decide(state, f)
        emitted.append([a.kind for a in actions])
    return state, emitted


def fresh(**overrides):
    params = dict(NOMINAL_PARAMS)
    params.update(overrides)
    p = mlib.gs2_params_from_dict(params)
    return p, mlib.gs2_initial_state(p)


def fly_nominal(state, dwell_end_ut=60.0):
    """The whole nominal profile: preload, settle, deploy, split, dwell."""
    frames = [orbiting(0.0)]                                   # PRELOAD -> SETTLE
    frames += [orbiting(float(i)) for i in (1, 2, 3)]          # SETTLE debounce -> DEPLOY
    frames += [orbiting(4.0, vessel_count=6),                  # split, debounce 1
               orbiting(5.0, vessel_count=6)]                  # debounce 2 -> POST-DEPLOY
    frames += [orbiting(float(t), vessel_count=6,
                        sibling_situation="ORBITING", sibling_present=1)
               for t in (6.0, 7.0, dwell_end_ut)]              # dwell
    return drive(state, frames)


class Gs2ParamsTests(unittest.TestCase):
    def test_every_key_round_trips(self):
        p = mlib.gs2_params_from_dict(NOMINAL_PARAMS)
        self.assertEqual(("ORBITING",), p.settle_situations)
        self.assertEqual(3, p.settle_debounce)
        self.assertEqual(120.0, p.settle_timeout)
        self.assertEqual(60.0, p.deploy_timeout)
        self.assertEqual(2, p.deploy_debounce)
        self.assertEqual(45.0, p.post_deploy_dwell)
        self.assertEqual(3, p.post_deploy_debounce)
        self.assertEqual(180.0, p.post_deploy_timeout)
        self.assertEqual(75000.0, p.min_safe_periapsis)

    def test_an_empty_dict_yields_the_dataclass_defaults(self):
        """An absent optional key must behave identically to an explicitly
        defaulted one, or the schema's `required = false` is a lie."""
        self.assertEqual(mlib.Gs2Params(), mlib.gs2_params_from_dict({}))
        self.assertEqual(mlib.Gs2Params(), mlib.gs2_params_from_dict(None))

    def test_the_spec_committed_dwell_fits_inside_its_own_timeout(self):
        """hlib does not enforce relations BETWEEN params, so the one relation that
        can make a phase unreachable is asserted here instead: a dwell longer than
        the phase budget could never complete."""
        p = mlib.gs2_params_from_dict(NOMINAL_PARAMS)
        self.assertLess(p.post_deploy_dwell, p.post_deploy_timeout)


class Gs2InGateTests(unittest.TestCase):
    """`gs2_in_gate` is the SF-2 fail-closed predicate both phases share."""

    def setUp(self):
        self.p = mlib.gs2_params_from_dict(NOMINAL_PARAMS)

    def test_the_parked_stack_is_in_gate(self):
        self.assertTrue(mlib.gs2_in_gate(self.p, orbiting(0.0)))

    def test_a_wrong_situation_is_out_of_gate(self):
        self.assertFalse(mlib.gs2_in_gate(self.p, orbiting(0.0, situation="SUB_ORBITAL")))

    def test_an_unread_situation_is_out_of_gate(self):
        self.assertFalse(mlib.gs2_in_gate(self.p, orbiting(0.0, situation="")))

    def test_a_periapsis_inside_the_atmosphere_is_out_of_gate(self):
        """THE ONE THAT MATTERS FOR THE SCENARIO: a periapsis below the floor means
        `DetermineTerminalState` will demote the terminal to SubOrbital, and the
        focus-slot gate GS-2 exists to measure fires ONLY for Orbiting."""
        self.assertFalse(mlib.gs2_in_gate(self.p, orbiting(0.0, periapsis=60000.0)))

    def test_a_nan_periapsis_fails_closed(self):
        self.assertFalse(mlib.gs2_in_gate(self.p, orbiting(0.0, periapsis=float("nan"))))


class Gs2NominalTests(unittest.TestCase):
    def test_the_nominal_profile_reaches_the_terminal(self):
        _p, st = fresh()
        st, _ = fly_nominal(st)
        self.assertEqual(mlib.GS2_DEPLOYED, st.phase)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(
            (mlib.GS2_PRELOAD, mlib.GS2_SETTLE, mlib.GS2_DEPLOY,
             mlib.GS2_POST_DEPLOY, mlib.GS2_DEPLOYED),
            st.phases_reached)

    def test_preload_commands_throttle_cut_and_sas_exactly_once(self):
        """The sibling watch is INSERTED ahead of these two on the PRELOAD frame, so
        the assertion is on the ordered TAIL plus once-ness, not on the whole list."""
        _p, st = fresh()
        _st, emitted = fly_nominal(st)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_SET_SAS],
                         emitted[0][-2:])
        for frame_actions in emitted[1:]:
            self.assertNotIn(mlib.ACTION_CUT_THROTTLE, frame_actions)
            self.assertNotIn(mlib.ACTION_SET_SAS, frame_actions)
            self.assertNotIn(mlib.ACTION_SET_SIBLING_WATCH, frame_actions)

    def test_exactly_one_stage_activation_over_the_whole_profile(self):
        """GUARD 1 of the module docstring. A second activation would light the
        orbital Skipper under a coasting stack."""
        _p, st = fresh()
        st, emitted = fly_nominal(st)
        activations = sum(kinds.count(mlib.ACTION_ACTIVATE_STAGE) for kinds in emitted)
        self.assertEqual(1, activations)
        self.assertEqual(1, st.stage_activations)

    def test_the_activation_rides_the_settle_to_deploy_edge_with_its_baseline(self):
        """The baseline must be captured in the SAME frame the stage is fired, or the
        rise it is compared against is not necessarily the split."""
        _p, st = fresh()
        frames = [orbiting(0.0)] + [orbiting(float(i), vessel_count=5) for i in (1, 2, 3)]
        st, emitted = drive(st, frames)
        self.assertEqual(mlib.GS2_DEPLOY, st.phase)
        self.assertEqual([mlib.ACTION_ACTIVATE_STAGE], emitted[-1])
        self.assertEqual(5, st.deploy_baseline_vessel_count)

    def test_the_split_latch_and_its_ut_are_carried(self):
        _p, st = fresh()
        st, _ = fly_nominal(st)
        self.assertTrue(st.split_ever_confirmed)
        self.assertEqual(5.0, st.split_ut)
        self.assertEqual(6, st.deploy_peak_vessel_count)


class Gs2DeployEvidenceTests(unittest.TestCase):
    def _to_deploy(self, **overrides):
        _p, st = fresh(**overrides)
        return drive(st, [orbiting(0.0)] + [orbiting(float(i)) for i in (1, 2, 3)])[0]

    def test_a_single_risen_frame_does_not_certify_the_split(self):
        """Debounced for the same reason the shared separation counter is: kRPC can
        return a stale count on the single frame a vessel splits."""
        st = self._to_deploy()
        st, _ = drive(st, [orbiting(4.0, vessel_count=6)])
        self.assertEqual(mlib.GS2_DEPLOY, st.phase)
        self.assertFalse(st.split_ever_confirmed)

    def test_the_streak_resets_on_a_frame_that_falls_back(self):
        st = self._to_deploy()
        st, _ = drive(st, [orbiting(4.0, vessel_count=6),
                           orbiting(5.0, vessel_count=5),
                           orbiting(6.0, vessel_count=6)])
        self.assertEqual(mlib.GS2_DEPLOY, st.phase)
        self.assertFalse(st.split_ever_confirmed)

    def test_an_unread_vessel_count_never_satisfies_the_rise(self):
        """0 is the documented UNREAD sentinel on TelemetrySnapshot.vessel_count, so
        it must fail closed rather than read as 'fewer vessels'."""
        st = self._to_deploy()
        st, _ = drive(st, [orbiting(4.0, vessel_count=0),
                           orbiting(5.0, vessel_count=0)])
        self.assertEqual(mlib.GS2_DEPLOY, st.phase)
        self.assertFalse(st.split_ever_confirmed)

    def test_a_stuck_decoupler_flakes_and_names_both_counts(self):
        st = self._to_deploy()
        st, _ = drive(st, [orbiting(200.0, vessel_count=5)])
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.GS2_DEPLOY, st.flake_phase)
        self.assertIn("no split observed", st.flake_reason)
        self.assertIn("baseline 5", st.flake_reason)

    def test_a_deploy_flake_is_never_an_assert_fail(self):
        """Mission-vs-Parsek orthogonality: a decoupler that did not fire is a driver
        problem, and classifying it as a product defect would file a retryable flake
        against the mod."""
        st = self._to_deploy()
        st, _ = drive(st, [orbiting(200.0, vessel_count=5)])
        self.assertNotEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)


class Gs2SettleAndDwellTests(unittest.TestCase):
    def test_a_fixture_that_never_settles_flakes_naming_the_reading(self):
        _p, st = fresh()
        st, _ = drive(st, [orbiting(0.0, situation="SUB_ORBITAL"),
                           orbiting(500.0, situation="SUB_ORBITAL")])
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.GS2_SETTLE, st.flake_phase)
        self.assertIn("SUB_ORBITAL", st.flake_reason)

    def test_the_dwell_must_actually_elapse(self):
        """In-gate frames are not enough: the phase has to have RUN for the dwell, or
        the recorder has not authored the post-split surfaces the commit needs."""
        _p, st = fresh()
        st, _ = drive(st, [orbiting(0.0)] + [orbiting(float(i)) for i in (1, 2, 3)]
                      + [orbiting(4.0, vessel_count=6), orbiting(5.0, vessel_count=6)]
                      + [orbiting(float(t), vessel_count=6) for t in (6.0, 7.0, 8.0)])
        self.assertEqual(mlib.GS2_POST_DEPLOY, st.phase)
        self.assertTrue(st.post_ever_stable)

    def test_a_decayed_orbit_after_the_split_flakes_with_the_was_stable_wording(self):
        _p, st = fresh()
        st, _ = drive(st, [orbiting(0.0)] + [orbiting(float(i)) for i in (1, 2, 3)]
                      + [orbiting(4.0, vessel_count=6), orbiting(5.0, vessel_count=6)]
                      + [orbiting(float(t), vessel_count=6) for t in (6.0, 7.0, 8.0)]
                      + [orbiting(400.0, vessel_count=6, periapsis=10000.0)])
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.GS2_POST_DEPLOY, st.flake_phase)
        self.assertIn("at least once", st.flake_reason)

    def test_a_post_deploy_that_was_never_stable_flakes_with_the_other_wording(self):
        _p, st = fresh()
        st, _ = drive(st, [orbiting(0.0)] + [orbiting(float(i)) for i in (1, 2, 3)]
                      + [orbiting(4.0, vessel_count=6), orbiting(5.0, vessel_count=6)]
                      + [orbiting(6.0, vessel_count=6, situation="SUB_ORBITAL"),
                         orbiting(400.0, vessel_count=6, situation="SUB_ORBITAL")])
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("never held", st.flake_reason)


class Gs2SiblingWatchTests(unittest.TestCase):
    """The deployed vessel, watched by NAME through the shared sibling channel.

    THE HOLE THIS CLOSES is GS-1 flight 3's, applied here before it could bite: kRPC
    telemetry is ACTIVE-VESSEL-SCOPED, and the SCENE EXIT is what stamps a recording's
    terminal state, so a mission blind to the deployed leaf can commit a tree whose
    terminal nobody asserted. It bites more QUIETLY in this lane than it did in GS-1's:
    every one of GS-2's required log tokens would still pass with a SubOrbital deployed
    leaf, because `TerminalOutcomeQualifiesInternal` sends Orbiting AND SubOrbital down
    the same non-focus branch and both return `stableLeafUnconcluded`."""

    def test_the_watch_is_armed_on_the_first_frame(self):
        """Armed at PRELOAD, long before the vessel exists - the deploy CREATES it, so
        there is nothing to resolve until then and a watch armed later would miss the
        frames right after the split."""
        _p, st = fresh()
        _st, emitted = drive(st, [orbiting(0.0)])
        self.assertEqual(mlib.ACTION_SET_SIBLING_WATCH, emitted[0][0])

    def test_the_watch_carries_the_configured_name(self):
        _p, st = fresh()
        _st, actions = mlib.gs2_decide(st, orbiting(0.0))
        watch = [a for a in actions if a.kind == mlib.ACTION_SET_SIBLING_WATCH]
        self.assertEqual(1, len(watch))
        self.assertEqual("Kerbal X Probe", watch[0].text)

    def test_no_name_configured_arms_nothing(self):
        """An unconfigured watch must leave every pre-existing behaviour untouched."""
        _p, st = fresh(siblingVesselName="")
        _st, actions = mlib.gs2_decide(st, orbiting(0.0))
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_SET_SAS],
                         [a.kind for a in actions])

    def test_presence_is_latched_and_the_situation_carried(self):
        _p, st = fresh()
        st, _ = drive(st, [orbiting(0.0),
                           orbiting(1.0, sibling_situation="ORBITING",
                                    sibling_present=1)])
        self.assertTrue(st.sibling_seen_present)
        self.assertEqual("ORBITING", st.sibling_last_situation)

    def test_the_unread_sentinel_neither_proves_nor_erases(self):
        """("", -1) is a FAULTED read. It must not earn presence, and it must not
        erase a presence already observed."""
        _p, st = fresh()
        st, _ = drive(st, [orbiting(0.0)])
        self.assertFalse(st.sibling_seen_present)
        st, _ = drive(st, [orbiting(1.0, sibling_situation="ORBITING",
                                    sibling_present=1),
                           orbiting(2.0)])
        self.assertTrue(st.sibling_seen_present)
        self.assertEqual("ORBITING", st.sibling_last_situation)

    def test_enumerated_absent_never_earns_presence(self):
        """("", 0) is the CORRECT reading before the split, and it is also what a
        misspelled watch name reads - which is why it can never earn the latch."""
        _p, st = fresh()
        st, _ = drive(st, [orbiting(0.0, sibling_present=0),
                           orbiting(1.0, sibling_present=0)])
        self.assertFalse(st.sibling_seen_present)
        self.assertEqual("", st.sibling_last_situation)

    def test_a_present_but_unreadable_situation_does_not_erase_the_last_good_one(self):
        """("", 1) is present-but-unreadable. Presence is real; the situation is not,
        so the last NON-EMPTY reading must survive it."""
        _p, st = fresh()
        st, _ = drive(st, [orbiting(0.0),
                           orbiting(1.0, sibling_situation="ORBITING",
                                    sibling_present=1),
                           orbiting(2.0, sibling_situation="", sibling_present=1)])
        self.assertEqual("ORBITING", st.sibling_last_situation)

    def test_a_vessel_lost_frame_does_not_touch_the_latches(self):
        """A vessel_lost snapshot carries benign defaults for every vessel-scoped
        channel, so reading one as sibling truth would fabricate an observation."""
        _p, st = fresh()
        st, _ = drive(st, [orbiting(0.0),
                           orbiting(1.0, sibling_situation="ORBITING",
                                    sibling_present=1)])
        st, _ = mlib.gs2_decide(st, mlib.TelemetrySnapshot(
            ut=2.0, vessel_lost=True, sibling_situation="LANDED", sibling_present=1))
        self.assertEqual("ORBITING", st.sibling_last_situation)


class Gs2SiblingAssertionTests(unittest.TestCase):
    def _row(self, state, frames):
        rows = {o.name: o for o in mlib.evaluate_gs2_assertions(
            frames, state.params, phases_reached=state.phases_reached, state=state)}
        return rows["deployedSiblingOrbiting"]

    def test_met_when_the_deployed_vessel_was_seen_orbiting(self):
        _p, st = fresh()
        st, _ = fly_nominal(st)
        row = self._row(st, [orbiting(0.0)])
        self.assertTrue(row.met)
        self.assertEqual("ORBITING", row.value)
        self.assertTrue(row.detail["seenPresent"])

    def test_UNMET_when_the_deployed_vessel_was_never_seen(self):
        """The name that does not resolve. This is ALSO what protects GS-3: it targets
        the same vessel by name with SimulateStockSwitchClick, and a name that does not
        resolve there costs a whole flight."""
        _p, st = fresh()
        frames = [orbiting(0.0)] + [orbiting(float(i)) for i in (1, 2, 3)] + [
            orbiting(4.0, vessel_count=6, sibling_present=0),
            orbiting(5.0, vessel_count=6, sibling_present=0)]
        st, _ = drive(st, frames)
        row = self._row(st, frames)
        self.assertFalse(row.met)
        self.assertFalse(row.detail["seenPresent"])
        self.assertEqual("Kerbal X Probe", row.detail["watchedVessel"])

    def test_UNMET_when_the_deployed_vessel_is_suborbital(self):
        """THE ONE THIS ROW EXISTS FOR. Every required log token would still pass here:
        Orbiting and SubOrbital take the SAME non-focus classifier branch and both
        return `stableLeafUnconcluded`, so the promotion fires and the RP still does
        not reap. Only this row notices that the scenario measured something else."""
        _p, st = fresh()
        frames = [orbiting(0.0)] + [orbiting(float(i)) for i in (1, 2, 3)] + [
            orbiting(4.0, vessel_count=6), orbiting(5.0, vessel_count=6),
            orbiting(6.0, vessel_count=6, sibling_situation="SUB_ORBITAL",
                     sibling_present=1)]
        st, _ = drive(st, frames)
        row = self._row(st, frames)
        self.assertFalse(row.met)
        self.assertEqual("SUB_ORBITAL", row.value)
        self.assertTrue(row.detail["seenPresent"])

    def test_a_LATER_real_out_of_gate_reading_REPLACES_an_earlier_good_one(self):
        """THE CELL THAT MAKES THE LATCH FALSIFIABLE, and the analogue of the hole
        Lane A found in the GS-1 streak. Without it, `sibling_last_situation` could be
        sticky-first-good, or "any accepted reading ever seen", and every other cell in
        this class would still pass. Feeding SUB_ORBITAL only (as
        test_UNMET_when_the_deployed_vessel_is_suborbital does) cannot tell those
        implementations apart from the real one; feeding a GOOD reading and then a
        REAL bad one can."""
        _p, st = fresh()
        frames = [orbiting(0.0)] + [orbiting(float(i)) for i in (1, 2, 3)] + [
            orbiting(4.0, vessel_count=6), orbiting(5.0, vessel_count=6),
            orbiting(6.0, vessel_count=6, sibling_situation="ORBITING",
                     sibling_present=1),
            orbiting(7.0, vessel_count=6, sibling_situation="SUB_ORBITAL",
                     sibling_present=1)]
        st, _ = drive(st, frames)
        self.assertEqual("SUB_ORBITAL", st.sibling_last_situation)
        row = self._row(st, frames)
        self.assertFalse(row.met)

    def test_a_LATER_good_reading_replaces_an_earlier_bad_one(self):
        """The converse, and it documents WHY last-wins is the right semantics here
        rather than a debounce: the terminal state is stamped at SCENE EXIT, so what
        matters is where the deployed vessel ENDED UP, not whether it ever read
        out-of-gate on the way. A probe that settles into its orbit is fine."""
        _p, st = fresh()
        frames = [orbiting(0.0)] + [orbiting(float(i)) for i in (1, 2, 3)] + [
            orbiting(4.0, vessel_count=6), orbiting(5.0, vessel_count=6),
            orbiting(6.0, vessel_count=6, sibling_situation="SUB_ORBITAL",
                     sibling_present=1),
            orbiting(7.0, vessel_count=6, sibling_situation="ORBITING",
                     sibling_present=1)]
        st, _ = drive(st, frames)
        row = self._row(st, frames)
        self.assertTrue(row.met)
        self.assertEqual("ORBITING", row.value)

    def test_UNMET_when_present_but_the_situation_was_NEVER_readable(self):
        """`("", 1)` on every frame: presence is observed, the situation never is. The
        membership test then runs against the empty latch and must FAIL CLOSED - an
        implementation that treated "no contrary reading" as a pass would be asserting
        the scenario's central premise from an absence of evidence."""
        _p, st = fresh()
        frames = [orbiting(0.0)] + [orbiting(float(i)) for i in (1, 2, 3)] + [
            orbiting(4.0, vessel_count=6), orbiting(5.0, vessel_count=6),
            orbiting(6.0, vessel_count=6, sibling_situation="", sibling_present=1),
            orbiting(7.0, vessel_count=6, sibling_situation="", sibling_present=1)]
        st, _ = drive(st, frames)
        self.assertTrue(st.sibling_seen_present)
        self.assertEqual("", st.sibling_last_situation)
        row = self._row(st, frames)
        self.assertFalse(row.met)
        self.assertIsNone(row.value)

    def test_auto_met_when_the_gate_is_off(self):
        """Same discipline as forge_lko's minCrew: an unconfigured gate is off, not
        failed, so no pre-existing caller is affected by the row existing."""
        _p, st = fresh(siblingVesselName="")
        st, _ = drive(st, [orbiting(0.0)])
        row = self._row(st, [orbiting(0.0)])
        self.assertTrue(row.met)
        self.assertIsNone(row.detail["watchedVessel"])


class Gs2LossTests(unittest.TestCase):
    def test_a_vessel_loss_is_an_assert_fail_in_every_phase(self):
        for phase_frames in ([], [orbiting(0.0)],
                             [orbiting(0.0)] + [orbiting(float(i)) for i in (1, 2, 3)]):
            _p, st = fresh()
            st, _ = drive(st, phase_frames)
            st, _ = mlib.gs2_decide(st, mlib.TelemetrySnapshot(ut=99.0, vessel_lost=True))
            self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
            self.assertIn("vessel-lost", st.loss_reason)

    def test_a_done_state_is_inert(self):
        _p, st = fresh()
        st, _ = fly_nominal(st)
        after, actions = mlib.gs2_decide(st, orbiting(999.0, vessel_count=6))
        self.assertIs(st, after)
        self.assertEqual([], actions)


class Gs2AssertionTests(unittest.TestCase):
    def _outcomes(self, state, frames):
        return {o.name: o for o in mlib.evaluate_gs2_assertions(
            frames, state.params, phases_reached=state.phases_reached, state=state)}

    def test_a_nominal_run_meets_all_six_rows(self):
        _p, st = fresh()
        live = {"sibling_situation": "ORBITING", "sibling_present": 1}
        frames = [orbiting(0.0)] + [orbiting(float(i)) for i in (1, 2, 3)] + [
            orbiting(4.0, vessel_count=6), orbiting(5.0, vessel_count=6),
            orbiting(6.0, vessel_count=6, **live),
            orbiting(7.0, vessel_count=6, **live),
            orbiting(60.0, vessel_count=6, **live)]
        st, _ = drive(st, frames)
        rows = self._outcomes(st, frames)
        self.assertEqual({"settled", "deployObserved", "singleStageActivation",
                          "focusStillOrbiting", "periapsisSafe",
                          "deployedSiblingOrbiting"}, set(rows))
        for name, row in rows.items():
            self.assertTrue(row.met, "%s should be met on a nominal run" % name)

    def test_deploy_observed_is_unmet_when_the_split_never_confirmed(self):
        _p, st = fresh()
        frames = [orbiting(0.0)] + [orbiting(float(i)) for i in (1, 2, 3)]
        st, _ = drive(st, frames)
        rows = self._outcomes(st, frames)
        self.assertTrue(rows["settled"].met)
        self.assertFalse(rows["deployObserved"].met)

    def test_periapsis_safe_reads_the_last_FINITE_frame(self):
        """An unreadable final frame must not fabricate a pass, and must not erase a
        good one either."""
        _p, st = fresh()
        frames = [orbiting(0.0), orbiting(1.0, periapsis=float("nan"))]
        st, _ = drive(st, frames)
        rows = self._outcomes(st, frames)
        self.assertTrue(rows["periapsisSafe"].met)
        self.assertEqual(100000.0, rows["periapsisSafe"].value)

    def test_periapsis_safe_is_unmet_with_no_finite_reading_at_all(self):
        _p, st = fresh()
        frames = [mlib.TelemetrySnapshot(ut=0.0, periapsis=float("nan"))]
        st, _ = drive(st, frames)
        rows = self._outcomes(st, frames)
        self.assertFalse(rows["periapsisSafe"].met)
        self.assertIsNone(rows["periapsisSafe"].value)

    def test_single_stage_activation_is_unmet_at_zero(self):
        _p, st = fresh()
        frames = [orbiting(0.0)]
        st, _ = drive(st, frames)
        rows = self._outcomes(st, frames)
        self.assertFalse(rows["singleStageActivation"].met)
        self.assertEqual(0, rows["singleStageActivation"].value)

    def test_evaluate_tolerates_a_missing_state(self):
        """`MissionSpec.evaluate` is called with the terminated state, but a shell that
        passes None must degrade to UNMET rather than raise."""
        rows = {o.name: o for o in mlib.evaluate_gs2_assertions(
            [orbiting(0.0)], mlib.gs2_params_from_dict(NOMINAL_PARAMS))}
        self.assertFalse(rows["deployObserved"].met)
        self.assertFalse(rows["singleStageActivation"].met)


# ---------------------------------------------------------------------------
# forge_lko `parkAttached` (the parameter this lane added to the LIVE-PROVEN
# orbital forge). The default-false path must not move.
# ---------------------------------------------------------------------------

FORGE_BASE = {
    "craftName": "Kerbal X",
    "launchSite": "LaunchPad",
    "crewNames": ["Valentina Kerman", "Bob Kerman"],
    "minCrew": 2,
    "targetApoapsisMeters": 100000,
    "targetPeriapsisMeters": 100000,
    "apoErrorMeters": 10000,
    "periErrorMeters": 10000,
    "separationTimeoutSeconds": 120,
    "parkSituations": ["ORBITING"],
    "parkDwellSeconds": 60,
    "parkTimeoutSeconds": 600,
    "parkDebounceFrames": 3,
    "maxAngularVelocityRadPerSec": 0.05,
    "minSafePeriapsisMeters": 75000,
}


def forge_state(**overrides):
    params = dict(FORGE_BASE)
    params.update(overrides)
    p = mlib.forge_lko_params_from_dict(params)
    return p, mlib.forge_lko_initial_state(p)


def at_circularize(state, vessel_count=5):
    """Drive the machine to the CIRCULARIZE phase with a known vessel-count baseline,
    without re-deriving the launch / ascent gates."""
    from dataclasses import replace
    return replace(state, phase=mlib.FLKO_ASCENT, phase_entry_ut=0.0,
                   phases_reached=(mlib.FLKO_PRELAUNCH, mlib.FLKO_LAUNCH,
                                   mlib.FLKO_ASCENT))


def ascent_done(ut, vessel_count=5, periapsis=0.0):
    return mlib.TelemetrySnapshot(ut=ut, apoapsis=100000.0, periapsis=periapsis,
                                  mj_ascent_complete=True, vessel_count=vessel_count,
                                  situation="SUB_ORBITAL")


class ForgeLkoParkAttachedTests(unittest.TestCase):
    def test_the_parameter_defaults_off(self):
        """FORGE-eva2-lko leaves it unset and is live-proven; the default must be the
        two-step SEPARATE contract."""
        self.assertFalse(mlib.ForgeLkoParams().park_attached)
        self.assertFalse(mlib.forge_lko_params_from_dict({}).park_attached)
        self.assertFalse(mlib.forge_lko_params_from_dict(FORGE_BASE).park_attached)

    def test_the_parameter_parses_true(self):
        p = mlib.forge_lko_params_from_dict(dict(FORGE_BASE, parkAttached=True))
        self.assertTrue(p.park_attached)

    def test_default_off_still_goes_circularize_to_separate_with_one_activation(self):
        _p, st = forge_state()
        st = at_circularize(st)
        st, _ = mlib.forge_lko_decide(st, ascent_done(10.0))
        self.assertEqual(mlib.FLKO_CIRCULARIZE, st.phase)
        st, actions = mlib.forge_lko_decide(st, ascent_done(20.0, periapsis=100000.0))
        self.assertEqual(mlib.FLKO_SEPARATE, st.phase)
        self.assertEqual([mlib.ACTION_ACTIVATE_STAGE], [a.kind for a in actions])
        self.assertEqual(1, st.separate_activations)

    def test_park_attached_skips_separate_and_activates_nothing(self):
        _p, st = forge_state(parkAttached=True)
        st = at_circularize(st)
        st, _ = mlib.forge_lko_decide(st, ascent_done(10.0))
        st, actions = mlib.forge_lko_decide(st, ascent_done(20.0, periapsis=100000.0))
        self.assertEqual(mlib.FLKO_PARK, st.phase)
        self.assertNotIn(mlib.FLKO_SEPARATE, st.phases_reached)
        kinds = [a.kind for a in actions]
        self.assertNotIn(mlib.ACTION_ACTIVATE_STAGE, kinds)
        # The SAME park entry actions the separated path uses.
        self.assertIn(mlib.ACTION_CUT_THROTTLE, kinds)
        self.assertIn(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES, kinds)

    def test_the_circularize_baseline_is_captured_on_the_ascent_edge(self):
        _p, st = forge_state(parkAttached=True)
        st = at_circularize(st)
        st, _ = mlib.forge_lko_decide(st, ascent_done(10.0, vessel_count=7))
        self.assertEqual(7, st.attached_baseline_vessel_count)

    def test_a_vessel_count_rise_during_circularize_latches_the_split(self):
        """THE AUTOSTAGE GUARD: nothing on the attached path activates a stage, so a
        rise can only be MechJeb autostage having fired the payload decoupler."""
        _p, st = forge_state(parkAttached=True)
        st = at_circularize(st)
        st, _ = mlib.forge_lko_decide(st, ascent_done(10.0, vessel_count=5))
        st, _ = mlib.forge_lko_decide(st, ascent_done(11.0, vessel_count=6))
        self.assertTrue(st.attached_split_seen)
        self.assertEqual(6, st.attached_peak_vessel_count)

    def test_the_latch_is_inert_when_park_attached_is_off(self):
        _p, st = forge_state()
        st = at_circularize(st)
        st, _ = mlib.forge_lko_decide(st, ascent_done(10.0, vessel_count=5))
        st, _ = mlib.forge_lko_decide(st, ascent_done(11.0, vessel_count=9))
        self.assertFalse(st.attached_split_seen)

    def test_the_stack_attached_row_replaces_separated_and_is_met_when_intact(self):
        from dataclasses import replace
        p = mlib.forge_lko_params_from_dict(dict(FORGE_BASE, parkAttached=True))
        st = replace(mlib.forge_lko_initial_state(p),
                     attached_baseline_vessel_count=5, attached_peak_vessel_count=5)
        frames = [mlib.TelemetrySnapshot(ut=1.0, situation="ORBITING", crew_count=2,
                                         apoapsis=100000.0, periapsis=100000.0)]
        rows = {o.name: o for o in mlib.evaluate_forge_lko_assertions(
            frames, p, phases_reached=(mlib.FLKO_PARK, mlib.FLKO_ORBIT), state=st)}
        self.assertIn("stackAttached", rows)
        self.assertNotIn("separated", rows)
        self.assertTrue(rows["stackAttached"].met)

    def test_the_stack_attached_row_is_UNMET_when_the_count_rose(self):
        """A waiver ('met because the phase was skipped') would have read green on a
        forge whose core autostaged off mid-coast and stamped a SPLIT fixture."""
        from dataclasses import replace
        p = mlib.forge_lko_params_from_dict(dict(FORGE_BASE, parkAttached=True))
        st = replace(mlib.forge_lko_initial_state(p),
                     attached_baseline_vessel_count=5, attached_peak_vessel_count=6,
                     attached_split_seen=True)
        frames = [mlib.TelemetrySnapshot(ut=1.0, situation="ORBITING", crew_count=2,
                                         apoapsis=100000.0, periapsis=100000.0)]
        rows = {o.name: o for o in mlib.evaluate_forge_lko_assertions(
            frames, p, phases_reached=(mlib.FLKO_PARK, mlib.FLKO_ORBIT), state=st)}
        self.assertFalse(rows["stackAttached"].met)
        self.assertTrue(rows["stackAttached"].detail["vesselCountRose"])
        self.assertEqual(6, rows["stackAttached"].detail["peakVesselCount"])

    def test_the_separated_row_is_unchanged_on_the_default_path(self):
        from dataclasses import replace
        p = mlib.forge_lko_params_from_dict(FORGE_BASE)
        st = replace(mlib.forge_lko_initial_state(p),
                     split_ever_confirmed=True, ignition_ever_confirmed=True)
        frames = [mlib.TelemetrySnapshot(ut=1.0, situation="ORBITING", crew_count=2,
                                         apoapsis=100000.0, periapsis=100000.0)]
        rows = {o.name: o for o in mlib.evaluate_forge_lko_assertions(
            frames, p, phases_reached=(mlib.FLKO_SEPARATE, mlib.FLKO_PARK,
                                       mlib.FLKO_ORBIT), state=st)}
        self.assertIn("separated", rows)
        self.assertNotIn("stackAttached", rows)
        self.assertTrue(rows["separated"].met)


class Gs2SpecWiringTests(unittest.TestCase):
    """The committed specs and the schema must agree with the machine. These read the
    repo, so a spec edited without its machine (or vice versa) reds here rather than
    in a KSP boot."""

    HARNESS = os.path.dirname(os.path.dirname(_HERE))
    SCENARIOS = os.path.join(HARNESS, "scenarios")

    def _spec(self, name):
        import tomllib
        with open(os.path.join(self.SCENARIOS, name), "rb") as fh:
            return tomllib.load(fh)

    def test_both_gs2_lane_specs_drive_this_mission(self):
        for name in ("GS-2-orbital-probe-deploy.toml", "GS-3-switch-nudge-deployed.toml"):
            self.assertEqual("gs2_orbital_probe_deploy",
                             self._spec(name)["driver"]["mission"], name)

    def test_the_two_specs_share_identical_mission_params(self):
        """A controlled A/B is only controlled if the mission parameters do not move.
        A future re-pin of GS-2's numbers must be mirrored into GS-3 in the SAME
        commit, and this cell is what enforces it."""
        a = self._spec("GS-2-orbital-probe-deploy.toml")["driver"]["missionParams"]
        b = self._spec("GS-3-switch-nudge-deployed.toml")["driver"]["missionParams"]
        self.assertEqual(a, b)

    def test_every_committed_param_is_accepted_by_the_parser(self):
        p = mlib.gs2_params_from_dict(
            self._spec("GS-2-orbital-probe-deploy.toml")["driver"]["missionParams"])
        self.assertEqual(("ORBITING",), p.settle_situations)
        self.assertGreaterEqual(p.min_safe_periapsis, 70000.0,
                                "the floor must clear Kerbin's atmosphere, or the "
                                "focus leaf can close SubOrbital and the focus-slot "
                                "gate never fires")
        self.assertLess(p.post_deploy_dwell, p.post_deploy_timeout)

    def test_the_forge_spec_sets_park_attached(self):
        spec = self._spec("FORGE-gs2-orbital-stack.toml")
        params = spec["driver"]["missionParams"]
        self.assertEqual("forge_lko", spec["driver"]["mission"])
        self.assertTrue(params.get("parkAttached"))
        self.assertTrue(
            mlib.forge_lko_params_from_dict(params).park_attached)

    def test_gs3_drives_exactly_two_switch_clicks_one_of_them_a_refusal_probe(self):
        """The second click is a DELIBERATE `expect = "REJECTED"`: with a session
        armed and a different target, DecidePreSwitchDialogAction returns OpenDialog
        (Case A) and the v1 verb refuses `dialog-required case=A-session`. If a future
        version teaches it to answer the dialog, this cell notices."""
        steps = self._spec("GS-3-switch-nudge-deployed.toml")["driver"]["steps"]
        clicks = [s for s in steps if s.get("cmd") == "SimulateStockSwitchClick"]
        self.assertEqual(2, len(clicks))
        self.assertEqual("OK", clicks[0].get("expect"))
        self.assertEqual("REJECTED", clicks[1].get("expect"))
        self.assertEqual("Kerbal X Probe", clicks[0]["args"]["vessel"])

    def test_gs2_drives_no_switch_click_at_all(self):
        """The A/B's single changed variable, asserted from the other side."""
        steps = self._spec("GS-2-orbital-probe-deploy.toml")["driver"]["steps"]
        self.assertEqual([], [s for s in steps
                              if s.get("cmd") == "SimulateStockSwitchClick"])

    def test_both_specs_arm_the_sibling_watch_on_the_same_vessel(self):
        """The deployed vessel's NAME is the one runtime-resolved string this lane
        depends on twice: GS-2 asserts its situation through the sibling channel, and
        GS-3 targets it with SimulateStockSwitchClick. If they ever disagreed, GS-2
        would go green on one vessel while GS-3 refused target-not-found on another."""
        gs2 = self._spec("GS-2-orbital-probe-deploy.toml")["driver"]
        gs3 = self._spec("GS-3-switch-nudge-deployed.toml")["driver"]
        name = gs2["missionParams"]["siblingVesselName"]
        self.assertEqual("Kerbal X Probe", name)
        self.assertEqual(name, gs3["missionParams"]["siblingVesselName"])
        click = [s for s in gs3["steps"]
                 if s.get("cmd") == "SimulateStockSwitchClick"][0]
        self.assertEqual(name, click["args"]["vessel"])

    def test_both_specs_pin_verbose_logging(self):
        """GS-2's central non-reap token and GS-3's whole prediction set are VERBOSE
        (`IsReapEligible` emits nothing at all when it refuses), so an unpinned
        verboseLogging would make the run unobservable rather than merely quieter."""
        for name in ("GS-2-orbital-probe-deploy.toml", "GS-3-switch-nudge-deployed.toml"):
            steps = self._spec(name)["driver"]["steps"]
            pins = [s for s in steps
                    if s.get("cmd") == "SetSetting"
                    and (s.get("args") or {}).get("name") == "verboseLogging"]
            self.assertEqual(1, len(pins), name)
            self.assertEqual("true", pins[0]["args"]["value"], name)

    def test_gs2_forbids_the_reap_and_gs3_does_not(self):
        """The two specs pin BOTH branches of `IsReapEligible`, and neither may assert
        the other's conclusion."""
        gs2 = self._spec("GS-2-orbital-probe-deploy.toml")
        gs3 = self._spec("GS-3-switch-nudge-deployed.toml")
        self.assertIn("Reaped rp=", gs2["expectations"]["logContracts"]["forbidden"])
        self.assertNotIn("Reaped rp=", gs3["expectations"]["logContracts"]["forbidden"])

    def test_the_rewind_blocks_declare_opposite_rewind_point_windows(self):
        """GS-2 says the RP survives; GS-3 predicts it reaps. If a future edit made
        them agree, the A/B would have quietly stopped being one."""
        gs2 = self._spec("GS-2-orbital-probe-deploy.toml")["expectations"]["rewind"]
        gs3 = self._spec("GS-3-switch-nudge-deployed.toml")["expectations"]["rewind"]
        self.assertEqual({"min": 1}, gs2["rewindPoints"])
        self.assertEqual({"max": 0}, gs3["rewindPoints"])

    def test_neither_rewind_block_is_armed(self):
        """Arming is a per-scenario operator decision taken only after a reading run.
        An unflown spec must not gate on a declared window."""
        for name in ("GS-2-orbital-probe-deploy.toml", "GS-3-switch-nudge-deployed.toml"):
            spec = self._spec(name)
            self.assertNotIn("gating", spec["expectations"]["rewind"], name)
            self.assertNotIn(
                "gating", spec["expectations"]["recordings"]["structure"], name)

    def test_the_decoupler_branch_point_window_uses_jointbreak(self):
        """MEASURED CORRECTION, not a source reading: `ProcessBreakupEvent` is the
        handler name but `CrashCoalescer.cs:161` writes
        `Type = BranchPointType.JointBreak` with `SplitCause = "DECOUPLE"`. GS-1's
        flight 2 declared `Breakup` and the facet read 0."""
        for name in ("GS-2-orbital-probe-deploy.toml", "GS-3-switch-nudge-deployed.toml"):
            bps = self._spec(name)["expectations"]["recordings"]["structure"]["branchPoints"]
            self.assertIn("JointBreak", bps, name)
            self.assertNotIn("Breakup", bps, name)


if __name__ == "__main__":
    unittest.main()
