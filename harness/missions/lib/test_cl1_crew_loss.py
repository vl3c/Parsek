"""Unit cells for the CL-1 crew-loss atom (mission `cl1_pod_impact`, scenario
`CL-1-pod-impact`).

Four groups, each guarding a different failure mode:

  1. THE PURE MACHINE (`mlib.cl1_decide` / `evaluate_cl1_assertions`). CL-1 inverts
     the suite's vessel-loss rule - the death is the SUCCESS terminal - so the
     guards that normally come free from `resolve_flight_verdict`'s loss branch
     have to be re-established here and pinned here.
  2. THE SHELL, driven end to end over a scripted flight with no krpc and no KSP.
  3. THE RUNNER'S ROSTER READ, the one channel every assertion depends on,
     including its three distinct outcomes and its independence from the vessel.
  4. FIXTURE / SPEC / REGISTRY SYNC. The spec names a kerbal by string and the
     fixture has to actually carry that kerbal, aboard, in a career save whose
     difficulty flags make the pinned log token reachable. Nothing else checks
     that pairing, and getting it wrong costs a live flight to discover.

NO krpc, NO KSP, NO network. Import path matches the sibling suites: discovery
runs from `harness/` with `missions/lib` as the root, and `missions/` is
prepended so `import mission_runner` / `import cl1_pod_impact` resolve.
"""

import os
import re
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

import mlib                    # noqa: E402
import mission_runner          # noqa: E402
import cl1_pod_impact          # noqa: E402
import oracle                  # noqa: E402
from test_shells import FakeMissionControl, run, snap  # noqa: E402

SPEC_PATH = os.path.join(_HARNESS, "scenarios", "CL-1-pod-impact.toml")
FIXTURE_SFS = os.path.join(_HARNESS, "fixtures", "saves",
                           "career-pad-craft", "persistent.sfs")
REGISTRY_PATH = os.path.join(_HARNESS, "coverage", "registry.toml")

PARAMS = {
    "throttle": 1.0,
    "crewName": "Jebediah Kerman",
    "flightTimeoutSeconds": 300.0,
    "landedSituations": ["LANDED", "SPLASHED"],
}


def params(**over):
    out = dict(PARAMS)
    out.update(over)
    return out


def fresh(**over):
    """A CL-1 machine already past PRELAUNCH, i.e. in FLIGHT with the watch armed."""
    state = mlib.cl1_initial_state(mlib.cl1_params_from_dict(params(**over)))
    state, _actions = mlib.cl1_decide(state, snap(ut=0.0))
    return state


def drive(state, *statuses, **kw):
    """Feed a run of roster readings as consecutive live frames."""
    ut = kw.pop("ut", 1.0)
    for i, status in enumerate(statuses):
        state, _ = mlib.cl1_decide(
            state, snap(ut=ut + i, altitude=kw.get("altitude", 1000.0),
                        situation=kw.get("situation", "FLYING"),
                        vessel_lost=kw.get("vessel_lost", False),
                        crew_roster_status=status))
    return state


# ---------------------------------------------------------------------------
# 1. The pure machine.
# ---------------------------------------------------------------------------


class Cl1PrelaunchTests(unittest.TestCase):
    """The one frame on which CL-1 acts at all."""

    def test_prelaunch_arms_the_roster_watch_before_it_ignites(self):
        # The watch action must be emitted, and it must carry the spec's crewName:
        # it is the argument to SpaceCenter.GetKerbal, so a dropped or empty name
        # silently disables every gate this mission has.
        state = mlib.cl1_initial_state(mlib.cl1_params_from_dict(PARAMS))
        state, actions = mlib.cl1_decide(state, snap(ut=9.06))
        kinds = [a.kind for a in actions]
        self.assertEqual(
            [mlib.ACTION_SET_ROSTER_WATCH, mlib.ACTION_SET_THROTTLE,
             mlib.ACTION_ACTIVATE_STAGE], kinds)
        self.assertEqual("Jebediah Kerman", actions[0].text)
        self.assertEqual(1.0, actions[1].value)
        self.assertEqual(mlib.CL1_FLIGHT, state.phase)

    def test_exactly_one_stage_activation_is_ever_emitted(self):
        # LOAD-BEARING FOR THE WHOLE PROFILE: the fixture craft stages the booster
        # at istg=1 and the PARACHUTE at istg=0, so a second activation would open
        # the chute and the craft would survive. Drive a long flight and assert the
        # machine never emits another one.
        state = mlib.cl1_initial_state(mlib.cl1_params_from_dict(PARAMS))
        stages = 0
        for i in range(200):
            state, actions = mlib.cl1_decide(
                state, snap(ut=float(i), altitude=5000.0, situation="FLYING",
                            crew_roster_status=mlib.ROSTER_STATUS_ASSIGNED))
            stages += sum(1 for a in actions if a.kind == mlib.ACTION_ACTIVATE_STAGE)
        self.assertEqual(1, stages)

    def test_the_first_frames_unread_roster_is_not_read_as_evidence(self):
        # On the PRELAUNCH frame the channel is necessarily unread (the watch is
        # armed BY that frame's action), so the unread give-up must not start
        # counting there.
        state = mlib.cl1_initial_state(mlib.cl1_params_from_dict(PARAMS))
        state, _ = mlib.cl1_decide(state, snap(ut=0.0))
        self.assertEqual(0, state.roster_unread_streak)


class Cl1SuccessTerminalTests(unittest.TestCase):
    """CREW-LOST: the inversion, and everything that keeps it honest."""

    def test_dead_after_alive_aboard_reaches_crew_lost_and_resolves_mission_ok(self):
        state = drive(fresh(), "Assigned", "Assigned", "Dead", "Dead")
        self.assertEqual(mlib.CL1_CREW_LOST, state.phase)
        self.assertTrue(state.done)
        # THE INVERSION, pinned: no loss_reason and verdict left None, so
        # resolve_flight_verdict falls through to the assertions instead of
        # short-circuiting to ASSERT-FAIL as it does for every other machine.
        self.assertIsNone(state.loss_reason)
        self.assertIsNone(state.verdict)
        self.assertTrue(state.skip_settle_tail)
        outcomes = mlib.evaluate_cl1_assertions(
            [], mlib.cl1_params_from_dict(PARAMS), state)
        verdict, reason = mlib.resolve_flight_verdict(state, outcomes)
        self.assertEqual(mlib.MISSION_OK, verdict)
        self.assertEqual("all telemetry assertions met", reason)

    def test_missing_is_accepted_as_well_as_dead(self):
        # FIXTURE-INDEPENDENCE, not a hedge: stock settles a dead kerbal at Dead
        # with MissingCrewsRespawn off and at Missing with it on. Accepting one
        # only would pin the machine to one save's difficulty flag.
        state = drive(fresh(), "Assigned", "Assigned", "Missing", "Missing")
        self.assertEqual(mlib.CL1_CREW_LOST, state.phase)
        self.assertEqual("Missing", state.crew_loss_status)

    def test_not_in_roster_after_alive_aboard_is_a_crew_loss(self):
        # The SAME reading that means "misspelled crewName" before the kerbal was
        # seen aboard means "the kerbal left the roster" after it.
        state = drive(fresh(), "Assigned", "Assigned", "NotInRoster", "NotInRoster")
        self.assertEqual(mlib.CL1_CREW_LOST, state.phase)
        self.assertIsNone(state.loss_reason)

    def test_one_dead_frame_does_not_terminate(self):
        # Debounce. A terminal that CERTIFIES gets the same two-agreeing-frames
        # treatment as one that condemns.
        state = drive(fresh(), "Assigned", "Assigned", "Dead")
        self.assertFalse(state.done)
        self.assertEqual(mlib.CL1_FLIGHT, state.phase)
        self.assertEqual(1, state.not_alive_streak)

    def test_a_disagreeing_frame_resets_the_not_alive_streak(self):
        state = drive(fresh(), "Assigned", "Assigned", "Dead", "Assigned", "Dead")
        self.assertFalse(state.done)
        self.assertEqual(1, state.not_alive_streak)

    def test_dead_without_ever_observing_alive_aboard_never_succeeds(self):
        # THE PRECONDITION. A fixture whose kerbal was already dead at load must
        # never reach the SUCCESS terminal, however many not-alive frames it
        # produces. (It now reds by name instead of running to the budget - see
        # test_a_kerbal_already_dead_at_load_is_named_instead_of_timing_out - but
        # THIS cell is about the terminal it must not reach, which is the part
        # that would be a false green.)
        state = drive(fresh(), *(["Dead"] * 20))
        self.assertFalse(state.crew_alive_aboard_seen)
        self.assertNotEqual(mlib.CL1_CREW_LOST, state.phase)
        self.assertNotEqual(mlib.MISSION_OK,
                            mlib.resolve_flight_verdict(
                                state,
                                mlib.evaluate_cl1_assertions(
                                    [], mlib.cl1_params_from_dict(PARAMS), state))[0])

    def test_available_does_not_earn_the_alive_aboard_latch(self):
        # Available means "in the roster, not on a vessel". The precondition is
        # specifically alive AND ABOARD, so only Assigned earns it.
        state = drive(fresh(), "Available")
        self.assertFalse(state.crew_alive_aboard_seen)
        self.assertEqual(0, state.alive_aboard_streak)

    def test_the_alive_aboard_latch_is_sticky(self):
        # A kerbal who was aboard and is now dead must not have that erased by the
        # frames that follow (the reading necessarily stops being Assigned).
        state = drive(fresh(), "Assigned", "Assigned", "Available", "Dead", "Dead")
        self.assertTrue(state.crew_alive_aboard_seen)
        self.assertEqual(mlib.CL1_CREW_LOST, state.phase)

    def test_a_pre_latch_flip_flop_neither_latches_nor_condemns(self):
        # Assigned -> Available -> Assigned before the latch is earned. Neither
        # run reaches K, so nothing latches and nothing condemns; the next two
        # agreeing Assigned frames earn the latch normally.
        state = drive(fresh(), "Assigned", "Available", "Assigned")
        self.assertFalse(state.done)
        self.assertFalse(state.crew_alive_aboard_seen)
        state = drive(state, "Assigned", ut=20.0)
        self.assertTrue(state.crew_alive_aboard_seen)

    def test_a_duplicated_or_out_of_order_ut_changes_nothing(self):
        # The budget is a subtraction against the phase-entry stamp and nothing
        # else reads UT, so a repeated or regressing frame stamp is inert.
        state = fresh()
        for ut in (5.0, 5.0, 4.0, 5.0):
            state, _ = mlib.cl1_decide(
                state, snap(ut=ut, altitude=100.0,
                            crew_roster_status="Assigned"))
        self.assertFalse(state.done)
        self.assertTrue(state.crew_alive_aboard_seen)

    def test_the_success_terminal_is_unreachable_without_the_latch(self):
        # THE INVARIANT, proved by exhaustion rather than by example: over EVERY
        # sequence of four readings drawn from the whole observable alphabet, the
        # machine never reaches CREW-LOST with the alive-aboard latch unset. That
        # is the property a false green would have to violate.
        #
        # It also EXPLAINS the one surviving mutant in the CL-1 mutation run.
        # Deleting `crew_alive_aboard_seen` from step 5's gate does not break this
        # property, because step 4 (crew-watch-never-aboard) always fires first on
        # any latch-less not-alive run - every not-alive frame is also a
        # never-aboard frame. So that mutant is EQUIVALENT, not uncaught. The
        # conjunct stays as the direct statement of the invariant; this cell is
        # what holds the invariant if the conjunct is ever removed.
        alphabet = (mlib.ROSTER_STATUS_UNREAD, mlib.ROSTER_STATUS_NOT_IN_ROSTER,
                    mlib.ROSTER_STATUS_AVAILABLE, mlib.ROSTER_STATUS_ASSIGNED,
                    mlib.ROSTER_STATUS_DEAD, mlib.ROSTER_STATUS_MISSING)
        checked = 0
        for a in alphabet:
            for b in alphabet:
                for c in alphabet:
                    for d in alphabet:
                        state = fresh()
                        for i, status in enumerate((a, b, c, d)):
                            if state.done:
                                break
                            state, _ = mlib.cl1_decide(
                                state, snap(ut=float(i + 1), altitude=1000.0,
                                            situation="FLYING",
                                            crew_roster_status=status))
                        checked += 1
                        if state.phase == mlib.CL1_CREW_LOST:
                            self.assertTrue(
                                state.crew_alive_aboard_seen,
                                "CREW-LOST reached without the alive-aboard latch "
                                "on sequence %r" % ((a, b, c, d),))
        self.assertEqual(len(alphabet) ** 4, checked)

    def test_the_machine_is_idempotent_once_done(self):
        state = drive(fresh(), "Assigned", "Assigned", "Dead", "Dead")
        again, actions = mlib.cl1_decide(state, snap(ut=999.0, crew_roster_status="Dead"))
        self.assertIs(state, again)
        self.assertEqual([], actions)


class Cl1NamedFailureTerminalTests(unittest.TestCase):
    """The three named non-success ends. Each exists because the outcome it names
    is DETERMINISTIC and would otherwise be reported as an unnamed budget
    timeout - the B1 `chute-arm-window-missed` lesson."""

    def test_unknown_crew_name_assert_fails_fast_and_names_itself(self):
        state = drive(fresh(crewName="Jebeddiah Kermin"), "NotInRoster", "NotInRoster")
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("crew-watch-never-aboard", state.loss_reason)
        self.assertIn("no kerbal of that name", state.loss_reason)
        self.assertIn("Jebeddiah Kermin", state.loss_reason)
        # It is an ASSERT-FAIL, so resolve_flight_verdict short-circuits on the
        # loss_reason before the assertions can see a machine that flew nothing.
        outcomes = mlib.evaluate_cl1_assertions(
            [], mlib.cl1_params_from_dict(PARAMS), state)
        verdict, reason = mlib.resolve_flight_verdict(state, outcomes)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, verdict)
        self.assertIn("crew-watch-never-aboard", reason)

    def test_a_kerbal_already_dead_at_load_is_named_instead_of_timing_out(self):
        # THE fixture fault the alive-aboard precondition exists to catch. It used
        # to merely refuse to succeed and then burn the whole FLIGHT budget into an
        # unnamed flake; the machine KNOWS it is watching a corpse and now says so.
        state = drive(fresh(), "Dead", "Dead")
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("crew-watch-never-aboard", state.loss_reason)
        self.assertIn("already not alive", state.loss_reason)
        self.assertNotEqual(mlib.CL1_CREW_LOST, state.phase)

    def test_a_kerbal_never_aboard_is_named_instead_of_timing_out(self):
        # Available forever: the kerbal is in the roster and alive but was never
        # on a vessel (an empty pod, or the spec naming the wrong kerbal).
        state = drive(fresh(), "Available", "Available")
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("crew-watch-never-aboard", state.loss_reason)
        self.assertIn("never aboard a vessel", state.loss_reason)

    def test_an_unread_frame_breaks_the_never_aboard_run(self):
        # Fail-closed: a blind frame proves nothing either way, so it must not
        # help CONDEMN any more than it helps certify.
        state = drive(fresh(), "Available", mlib.ROSTER_STATUS_UNREAD, "Available")
        self.assertFalse(state.done)
        self.assertEqual(1, state.never_aboard_streak)

    def test_an_empty_crew_name_reds_before_anything_is_ignited(self):
        # `crewName = ""` passes the schema (hlib's "string" check is an
        # isinstance), arms a watch the runner treats as UNARMED, and would then
        # surface minutes later as the RETRYABLE `roster-channel-lost` flake -
        # blaming the kRPC channel for a spec typo. Named at PRELAUNCH instead,
        # and no stage is activated.
        state = mlib.cl1_initial_state(mlib.cl1_params_from_dict(params(crewName="")))
        state, actions = mlib.cl1_decide(state, snap(ut=0.0))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("crew-watch-unnamed", state.loss_reason)
        self.assertEqual([], actions)

    def test_one_not_in_roster_frame_does_not_condemn(self):
        state = drive(fresh(), "NotInRoster")
        self.assertFalse(state.done)

    def test_crew_survived_impact_assert_fails_and_names_the_situation(self):
        state = drive(fresh(), "Assigned", "Assigned")
        state = drive(state, "Assigned", "Assigned", situation="LANDED", ut=10.0)
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("crew-survived-impact", state.loss_reason)
        self.assertIn("LANDED", state.loss_reason)

    def test_one_landed_frame_does_not_condemn(self):
        state = drive(fresh(), "Assigned", "Assigned")
        state = drive(state, "Assigned", situation="LANDED", ut=10.0)
        self.assertFalse(state.done)

    def test_a_frame_that_is_both_landed_and_dead_resolves_as_the_death(self):
        # ORDERING, pinned. Debris coming to rest on the same frame the roster
        # reads Dead is a death, not a survival; the loss check runs first.
        state = drive(fresh(), "Assigned", "Assigned")
        state = drive(state, "Dead", "Dead", situation="LANDED", ut=10.0)
        self.assertEqual(mlib.CL1_CREW_LOST, state.phase)
        self.assertIsNone(state.loss_reason)

    def test_a_landing_followed_one_frame_later_by_the_death_is_the_death(self):
        # THE STAGGERED CASE, and the shape the real event actually has: the wreck
        # settles into LANDED and the roster flips one ~0.5 s poll later. Step 5
        # only pre-empts step 6 when the death debounce is ALREADY complete, so
        # without the not-alive conjunct on `landed` the SURVIVED streak completed
        # one frame first and red a successful flight with a reason that
        # contradicted itself inside one line ("with the crew still alive; ...
        # lastRoster=Dead"). Opus review panel 2026-07-28, reviewer 1, finding 1.
        state = drive(fresh(), "Assigned", "Assigned")
        state, _ = mlib.cl1_decide(state, snap(ut=10.0, altitude=70.0,
                                               situation="LANDED",
                                               crew_roster_status="Assigned"))
        self.assertEqual(1, state.landed_alive_streak)
        state, _ = mlib.cl1_decide(state, snap(ut=11.0, altitude=70.0,
                                               situation="LANDED",
                                               crew_roster_status="Dead"))
        self.assertFalse(state.done,
                         "a not-alive frame must RESET the survival streak")
        self.assertEqual(0, state.landed_alive_streak)
        state, _ = mlib.cl1_decide(state, snap(ut=12.0, altitude=70.0,
                                               situation="LANDED",
                                               crew_roster_status="Dead"))
        self.assertEqual(mlib.CL1_CREW_LOST, state.phase)
        self.assertIsNone(state.loss_reason)

    def test_roster_channel_lost_is_a_named_flake_not_an_assert_fail(self):
        # A dead roster channel means the kRPC channel is broken, not that the
        # flight went wrong. FLAKE is retryable in hlib, which is the correct
        # treatment; ASSERT-FAIL would file an infrastructure fault as a defect.
        state = drive(fresh(), "Assigned", "Assigned",
                      *([mlib.ROSTER_STATUS_UNREAD] * mlib.CL1_ROSTER_UNREAD_GIVEUP_FRAMES))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertIsNone(state.loss_reason)
        self.assertIn("roster-channel-lost", state.flake_reason)
        verdict, reason = mlib.resolve_flight_verdict(state, [])
        self.assertEqual(mlib.MISSION_FLAKE, verdict)
        self.assertIn("roster-channel-lost", reason)

    def test_a_transient_unread_frame_does_not_trip_the_give_up(self):
        state = fresh()
        for _ in range(20):
            state = drive(state, mlib.ROSTER_STATUS_UNREAD,
                          mlib.ROSTER_STATUS_UNREAD, "Assigned")
            self.assertFalse(state.done)
        self.assertEqual(0, state.roster_unread_streak)

    def test_every_failure_reason_carries_the_observed_channel_state(self):
        # A failure that does not name what the channel was saying is a failure
        # someone has to re-derive from a 2 MB log.
        state = drive(fresh(), "Assigned", "Assigned")
        state = drive(state, "Assigned", "Assigned", situation="SPLASHED", ut=10.0)
        self.assertIn("lastRoster=Assigned", state.loss_reason)
        self.assertIn("aliveAboardObserved=yes", state.loss_reason)
        self.assertIn("last altitude", state.loss_reason)


class Cl1VesselLostTests(unittest.TestCase):
    """`vessel_lost` is a TERMINAL everywhere else in mlib and deliberately is not
    one here: the pod being destroyed is the expected midpoint of this flight, and
    the machine still needs the roster frame that says the kerbal died with it."""

    def test_a_vessel_lost_frame_carrying_a_dead_roster_still_reaches_crew_lost(self):
        # THE WHOLE REASON the roster read is independent of the active vessel.
        state = drive(fresh(), "Assigned", "Assigned")
        state = drive(state, "Dead", "Dead", vessel_lost=True, ut=10.0)
        self.assertEqual(mlib.CL1_CREW_LOST, state.phase)
        self.assertTrue(state.vessel_lost_seen)

    def test_vessel_lost_with_an_unread_roster_flakes_rather_than_passing(self):
        state = drive(fresh(), "Assigned", "Assigned")
        state = drive(state, *([mlib.ROSTER_STATUS_UNREAD]
                               * mlib.CL1_ROSTER_UNREAD_GIVEUP_FRAMES),
                      vessel_lost=True, ut=10.0)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertNotEqual(mlib.CL1_CREW_LOST, state.phase)

    def test_a_vessel_lost_frame_does_not_contribute_altitude_evidence(self):
        state = drive(fresh(), "Assigned", altitude=8000.0)
        before = state.last_finite_altitude
        state, _ = mlib.cl1_decide(
            state, snap(ut=50.0, altitude=99999.0, vessel_lost=True,
                        crew_roster_status="Assigned"))
        self.assertEqual(before, state.last_finite_altitude)

    def test_a_vessel_lost_frame_is_never_read_as_a_landing(self):
        # The benign default situation on a vessel_lost snapshot is "", but a
        # caller could construct one carrying a stale landed situation; the
        # survived terminal is live-frames-only either way.
        state = drive(fresh(), "Assigned", "Assigned")
        state = drive(state, "Assigned", "Assigned", situation="LANDED",
                      vessel_lost=True, ut=10.0)
        self.assertFalse(state.done)


class Cl1BudgetTests(unittest.TestCase):

    def test_flight_over_budget_flakes_naming_the_phase(self):
        state = fresh(flightTimeoutSeconds=30.0)
        state, _ = mlib.cl1_decide(
            state, snap(ut=1000.0, crew_roster_status="Assigned"))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertEqual(mlib.CL1_FLIGHT, state.flake_phase)

    def test_a_non_finite_ut_never_trips_the_timeout(self):
        state = fresh(flightTimeoutSeconds=1.0)
        state, _ = mlib.cl1_decide(
            state, snap(ut=float("nan"), crew_roster_status="Assigned"))
        self.assertFalse(state.done)

    def test_the_success_terminal_beats_the_budget_on_the_same_frame(self):
        # ORDERING, pinned: within one frame the loss check (step 5) runs BEFORE
        # the budget check (step 7), so a frame that both completes the death
        # debounce and blows the budget resolves as the death rather than as an
        # unnamed timeout. (A death that has only STARTED its debounce when the
        # budget expires still flakes, which is correct: the budget is 2.5x the
        # measured flight, so reaching it at all means the run is anomalous.)
        state = drive(fresh(flightTimeoutSeconds=100.0), "Assigned", "Assigned", "Dead")
        state, _ = mlib.cl1_decide(
            state, snap(ut=9999.0, crew_roster_status="Dead"))
        self.assertEqual(mlib.CL1_CREW_LOST, state.phase)
        self.assertIsNone(state.verdict)


class Cl1AssertionTests(unittest.TestCase):

    def test_both_assertions_met_only_on_a_real_observed_death(self):
        state = drive(fresh(), "Assigned", "Assigned", "Dead", "Dead")
        rows = {o.name: o for o in mlib.evaluate_cl1_assertions(
            [], mlib.cl1_params_from_dict(PARAMS), state)}
        self.assertEqual({"crewAliveAboardObserved", "crewLostObserved"}, set(rows))
        self.assertTrue(rows["crewAliveAboardObserved"].met)
        self.assertTrue(rows["crewLostObserved"].met)
        # The terminal READING is the evidence value, so the result JSON records
        # WHICH not-alive status this fixture's difficulty flags produced.
        self.assertEqual("Dead", rows["crewLostObserved"].value)

    def test_a_flight_that_never_saw_the_kerbal_leaves_both_unmet(self):
        state = drive(fresh(), "Available")
        rows = {o.name: o for o in mlib.evaluate_cl1_assertions(
            [], mlib.cl1_params_from_dict(PARAMS), state)}
        self.assertFalse(rows["crewAliveAboardObserved"].met)
        self.assertFalse(rows["crewLostObserved"].met)

    def test_crew_lost_is_unmet_when_the_terminal_was_never_reached(self):
        state = drive(fresh(), "Assigned", "Assigned", "Dead")
        rows = {o.name: o for o in mlib.evaluate_cl1_assertions(
            [], mlib.cl1_params_from_dict(PARAMS), state)}
        self.assertTrue(rows["crewAliveAboardObserved"].met)
        self.assertFalse(rows["crewLostObserved"].met)

    def test_the_accepted_set_is_reported_on_the_row(self):
        state = drive(fresh(), "Assigned", "Assigned", "Dead", "Dead")
        rows = {o.name: o for o in mlib.evaluate_cl1_assertions(
            [], mlib.cl1_params_from_dict(PARAMS), state)}
        detail = rows["crewLostObserved"].to_dict()
        # LITERALS, not the constants they come from: comparing a row against the
        # same constant that produced it survives any change to the constant's
        # CONTENT, so it pins plumbing rather than policy.
        self.assertEqual(["Dead", "Missing", "NotInRoster"], detail["accepted"])
        self.assertEqual(2, detail["debounceK"])


class RosterStatusNormalizationTests(unittest.TestCase):

    def test_krpc_lowercase_enum_names_normalize_to_the_gated_spelling(self):
        for raw, want in (("available", "Available"), ("assigned", "Assigned"),
                          ("dead", "Dead"), ("missing", "Missing")):
            self.assertEqual(want, mlib.normalize_roster_status(raw))

    def test_an_empty_or_none_read_is_the_fail_closed_unread_sentinel(self):
        for raw in ("", None):
            self.assertEqual(mlib.ROSTER_STATUS_UNREAD,
                             mlib.normalize_roster_status(raw))

    def test_the_unread_sentinel_matches_no_gate(self):
        self.assertNotIn(mlib.ROSTER_STATUS_UNREAD, mlib.ROSTER_STATUS_NOT_ALIVE)
        self.assertNotEqual(mlib.ROSTER_STATUS_UNREAD, mlib.ROSTER_STATUS_ASSIGNED)

    def test_the_snapshot_channel_defaults_to_unread(self):
        # Every mission but CL-1 leaves this field alone, so its default has to be
        # the sentinel that satisfies nothing.
        self.assertEqual(mlib.ROSTER_STATUS_UNREAD,
                         mlib.TelemetrySnapshot().crew_roster_status)


# ---------------------------------------------------------------------------
# 2. The shell, end to end over scripted telemetry.
# ---------------------------------------------------------------------------


def flight_frames(tail):
    """A scripted CL-1 flight: pad frame, ascent, coast, then ``tail``."""
    frames = [snap(ut=0.0, altitude=70.0, situation="PRE_LAUNCH")]
    frames += [snap(ut=float(i), altitude=1000.0 * i, situation="FLYING",
                    crew_roster_status="Assigned") for i in range(1, 13)]
    return frames + list(tail)


class Cl1ShellTests(unittest.TestCase):

    def test_a_scripted_fatal_flight_returns_mission_ok(self):
        tail = [snap(ut=120.0 + i, altitude=200.0, situation="FLYING",
                     crew_roster_status="Dead") for i in range(3)]
        code, result = run(cl1_pod_impact.SPEC, PARAMS,
                           FakeMissionControl(flight_frames(tail)))
        self.assertEqual(0, code)
        self.assertEqual(mlib.MISSION_OK, result["verdict"])
        self.assertIn(mlib.CL1_CREW_LOST, result["phasesReached"])
        self.assertTrue(all(a["met"] for a in result["assertions"]))

    def test_the_shell_arms_the_roster_watch_exactly_once(self):
        tail = [snap(ut=120.0 + i, altitude=200.0, situation="FLYING",
                     crew_roster_status="Dead") for i in range(3)]
        control = FakeMissionControl(flight_frames(tail))
        run(cl1_pod_impact.SPEC, PARAMS, control)
        watches = [a for a in control.actions
                   if a.kind == mlib.ACTION_SET_ROSTER_WATCH]
        self.assertEqual(1, len(watches))
        self.assertEqual("Jebediah Kerman", watches[0].text)

    def test_the_shell_never_commands_a_parachute(self):
        # CL-1's profile is "no chute". A deploy action reaching the seam would
        # invalidate the whole scenario, silently.
        tail = [snap(ut=120.0 + i, altitude=200.0, situation="FLYING",
                     crew_roster_status="Dead") for i in range(3)]
        control = FakeMissionControl(flight_frames(tail))
        run(cl1_pod_impact.SPEC, PARAMS, control)
        for a in control.actions:
            self.assertNotIn(a.kind, (mlib.ACTION_DEPLOY_CHUTE,
                                      mlib.ACTION_SET_CHUTE_DEPLOY_ALTITUDE))

    def test_a_craft_that_lands_with_its_crew_alive_reds_by_name(self):
        tail = [snap(ut=120.0 + i, altitude=70.0, situation="LANDED",
                     crew_roster_status="Assigned") for i in range(4)]
        code, result = run(cl1_pod_impact.SPEC, PARAMS,
                           FakeMissionControl(flight_frames(tail)))
        self.assertNotEqual(0, code)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, result["verdict"])
        self.assertIn("crew-survived-impact", result["reason"])

    def test_the_mission_declares_no_handoff_contract(self):
        # CL-1 terminates ON the outcome it certifies (unlike EVA-4, whose process
        # exits before its subject's fate exists), so it must NOT appear in the
        # handoff table - a disclaimer here would be false.
        self.assertIsNone(mlib.mission_handoff_contract("cl1_pod_impact"))

    def test_the_shell_skips_the_settle_tail(self):
        self.assertEqual(0, cl1_pod_impact.SPEC.settle_frames)

    def test_the_mission_never_permits_warp(self):
        self.assertFalse(cl1_pod_impact.SPEC.allow_rails_warp)
        self.assertEqual(0.0, cl1_pod_impact.SPEC.max_physics_warp)


# ---------------------------------------------------------------------------
# 3. The runner's roster read.
# ---------------------------------------------------------------------------


class _FakeKerbal:
    def __init__(self, status_name):
        self.roster_status = type("S", (), {"name": status_name})()


class _FakeSpaceCenter:
    def __init__(self, kerbals=None, raises=False):
        self._kerbals = kerbals or {}
        self._raises = raises
        self.calls = []

    def get_kerbal(self, name):
        self.calls.append(name)
        if self._raises:
            raise RuntimeError("kRPC connection dropped")
        return self._kerbals.get(name)


class RosterReadTests(unittest.TestCase):
    """The one channel every CL-1 assertion depends on."""

    def _control(self, watch=None):
        control = mission_runner.KrpcMissionControl(client_name="test")
        if watch is not None:
            control._roster_watch_name = watch
        return control

    def test_an_unarmed_watch_issues_no_rpc_at_all(self):
        # Every mission but CL-1 leaves the watch unarmed, and their snapshots must
        # stay byte-identical - which means not paying an RPC per poll either.
        sc = _FakeSpaceCenter({"Jebediah Kerman": _FakeKerbal("assigned")})
        self.assertEqual("", self._control()._read_crew_roster_status(sc))
        self.assertEqual([], sc.calls)

    def test_an_armed_watch_returns_the_normalized_status(self):
        sc = _FakeSpaceCenter({"Jebediah Kerman": _FakeKerbal("dead")})
        got = self._control("Jebediah Kerman")._read_crew_roster_status(sc)
        self.assertEqual("Dead", got)
        self.assertEqual(["Jebediah Kerman"], sc.calls)

    def test_a_null_kerbal_is_the_distinct_observed_absent_reading(self):
        # NOT the unread sentinel: GetKerbal returning null is an OBSERVATION, and
        # collapsing it into "" would turn a misspelled crewName from a named
        # terminal into a mystery budget burn.
        sc = _FakeSpaceCenter({})
        self.assertEqual(mlib.ROSTER_STATUS_NOT_IN_ROSTER,
                         self._control("Nobody Kerman")._read_crew_roster_status(sc))

    def test_a_raising_get_kerbal_degrades_to_the_unread_sentinel(self):
        sc = _FakeSpaceCenter(raises=True)
        self.assertEqual("", self._control("Jebediah Kerman")._read_crew_roster_status(sc))

    def test_a_raising_status_property_degrades_to_the_unread_sentinel(self):
        class _Boom:
            @property
            def roster_status(self):
                raise RuntimeError("property blew up")
        sc = _FakeSpaceCenter({"Jebediah Kerman": _Boom()})
        self.assertEqual("", self._control("Jebediah Kerman")._read_crew_roster_status(sc))

    def test_arming_the_watch_never_touches_the_active_vessel(self):
        # The channel exists to outlive the craft; binding its ARMING to a live
        # vessel would defeat that, and `perform` resolves active_vessel first for
        # every other action kind. The fake is an INSTANCE with a raising property,
        # not a class with one: on a class, attribute access returns the property
        # OBJECT (truthy) and never raises, which is exactly how an earlier version
        # of this cell let the regression through a mutation run.
        class _NoVesselSpaceCenter:
            @property
            def active_vessel(self):
                raise RuntimeError("no active vessel")

        class _NoVesselConn:
            def __init__(self):
                self.space_center = _NoVesselSpaceCenter()

        control = mission_runner.KrpcMissionControl(client_name="test")
        control._conn = _NoVesselConn()
        with self.assertRaises(RuntimeError):
            control._conn.space_center.active_vessel  # the fake really does raise
        control.perform(mlib.Action(mlib.ACTION_SET_ROSTER_WATCH, text="Bill Kerman"))
        self.assertEqual("Bill Kerman", control._roster_watch_name)


class VesselLostRosterCarryTests(unittest.TestCase):
    """THE design property of the roster channel, at the runner boundary.

    Every other opt-in channel deliberately carries a benign default on a
    vessel_lost snapshot ("must not fabricate a canopy") because they are
    properties OF THE VESSEL. The roster is a property of the KERBAL, and the
    frame on which the craft becomes unreadable is exactly the frame a crew-loss
    mission most needs it - so this one is populated there on purpose. Without
    this cell the whole vessel-independence claim rests on a comment."""

    class _DeadVesselConn:
        """A connection whose active-vessel read always raises but whose roster
        read works - i.e. the craft is gone and the kerbal's record is not."""
        def __init__(self, status_name):
            outer = self

            class _SC:
                ut = 144.5

                @property
                def active_vessel(self):
                    raise RuntimeError("active vessel destroyed")

                def get_kerbal(self, name):
                    outer.kerbal_reads.append(name)
                    return _FakeKerbal(status_name)

            self.kerbal_reads = []
            self.space_center = _SC()

    def _control(self, status_name):
        control = mission_runner.KrpcMissionControl(client_name="test")
        control._conn = self._DeadVesselConn(status_name)
        control._roster_watch_name = "Jebediah Kerman"
        return control

    def test_the_vessel_lost_snapshot_carries_the_roster_reading(self):
        control = self._control("dead")
        # Below the streak limit the read re-raises (the existing transient path).
        for _ in range(mission_runner.READ_FAIL_STREAK_LIMIT - 1):
            with self.assertRaises(RuntimeError):
                control.read_snapshot()
        snapshot = control.read_snapshot()
        self.assertTrue(snapshot.vessel_lost)
        self.assertEqual("Dead", snapshot.crew_roster_status)
        self.assertEqual(144.5, snapshot.ut)

    def test_that_snapshot_drives_the_machine_to_the_success_terminal(self):
        # End to end across the seam: runner-produced vessel_lost frames alone
        # must be able to complete the death debounce.
        control = self._control("dead")
        for _ in range(mission_runner.READ_FAIL_STREAK_LIMIT - 1):
            with self.assertRaises(RuntimeError):
                control.read_snapshot()
        state = drive(fresh(), "Assigned", "Assigned")
        for _ in range(mlib.CL1_ROSTER_DEBOUNCE_K):
            state, _ = mlib.cl1_decide(state, control.read_snapshot())
        self.assertEqual(mlib.CL1_CREW_LOST, state.phase)

    def test_an_unread_roster_on_that_path_stays_the_fail_closed_sentinel(self):
        control = mission_runner.KrpcMissionControl(client_name="test")
        control._conn = self._DeadVesselConn("dead")
        # No watch armed: the roster read must issue nothing and report UNREAD.
        for _ in range(mission_runner.READ_FAIL_STREAK_LIMIT - 1):
            with self.assertRaises(RuntimeError):
                control.read_snapshot()
        snapshot = control.read_snapshot()
        self.assertTrue(snapshot.vessel_lost)
        self.assertEqual(mlib.ROSTER_STATUS_UNREAD, snapshot.crew_roster_status)
        self.assertEqual([], control._conn.kerbal_reads)


# ---------------------------------------------------------------------------
# 4. Fixture / spec / registry sync.
# ---------------------------------------------------------------------------


def _read_spec():
    with open(SPEC_PATH, "rb") as fh:
        return tomllib.load(fh)


def _read_fixture():
    with open(FIXTURE_SFS, "r", encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n").split("\n")


class SpecFixtureSyncTests(unittest.TestCase):
    """The spec names a kerbal by STRING and the fixture has to actually carry
    that kerbal, aboard, in a career save whose difficulty flags make the pinned
    log token reachable. Nothing else in the harness checks that pairing, and
    every way of getting it wrong costs a live flight to discover."""

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec()
        cls.sfs = _read_fixture()
        cls.crew = cls.spec["driver"]["missionParams"]["crewName"]

    def test_the_spec_points_at_the_career_pad_fixture(self):
        self.assertEqual("fixtures/saves/career-pad-craft",
                         self.spec["fixture"]["saveTemplate"])

    def test_the_watched_kerbal_is_aboard_the_fixture_vessel(self):
        self.assertIn("crew = %s" % self.crew,
                      [l.strip() for l in self.sfs])

    def test_the_watched_kerbal_is_assigned_in_the_fixture_roster(self):
        # Assigned is what earns the machine's alive-aboard precondition. A fixture
        # whose kerbal is merely Available would never satisfy it, and the run
        # would burn its whole budget instead of naming anything.
        stripped = [l.strip() for l in self.sfs]
        idx = stripped.index("name = %s" % self.crew)
        window = stripped[idx:idx + 20]
        self.assertIn("state = Assigned", window)
        self.assertIn("type = Crew", window,
                      "kRPC GetKerbal scans CrewRoster.Crew; a non-Crew type "
                      "would read NotInRoster from the first frame")

    def test_the_fixture_is_a_career_save(self):
        # A SANDBOX death cannot exercise the ledger at all, which is the whole
        # reason this scenario needed its own fixture.
        self.assertIn("Mode = CAREER", [l.strip() for l in self.sfs])

    def test_the_fixture_suppresses_the_missing_crew_respawn_second_hop(self):
        # LOAD-BEARING FOR THE PINNED LOG TOKEN. The spec requires
        # `CrewStatusChanged '<name>' Assigned ... Dead`. With MissingCrewsRespawn
        # on, stock walks Assigned -> Dead -> Missing; with it off the kerbal
        # settles at Dead. The required token is reachable either way (Dead is the
        # first hop), but a future edit flipping this flag changes the terminal
        # roster reading, and this cell is where that gets noticed.
        self.assertIn("MissingCrewsRespawn = False", [l.strip() for l in self.sfs])

    def test_the_fixture_carries_exactly_one_vessel(self):
        # expectations.recordings.count is pinned at exactly 1; a second tracked
        # vessel in the fixture is a free variable in that pin.
        self.assertEqual(1, sum(1 for l in self.sfs if l.strip() == "VESSEL"))

    def test_the_fixture_has_no_parsek_footprint(self):
        # The analyzer runs in Forbid mode over a fresh save; a ParsekScenario node
        # or a prior recording would also give KerbalsModule.IsManaged a
        # reservation to claim, which SUPPRESSES the CrewStatusChanged emit the
        # spec requires.
        self.assertNotIn("name = ParsekScenario", [l.strip() for l in self.sfs])


class FixtureDriftTests(unittest.TestCase):
    """WIRES THE BUILDER'S `--check` INTO THE SUITE (Opus review panel 2026-07-28,
    reviewer 3, finding 2).

    Known-gate 10's first objection was "a fixture nobody maintains", and the
    answer given is that `career-pad-craft` is derived by a committed script from
    two fixtures their own consumers already maintain. Unwired, that answer is
    prose with a shebang: if `fresh-career` or `b1-pad-craft` moves, the committed
    fixture silently stops being what the recipe produces. These two cells are
    what make the answer mechanical."""

    @classmethod
    def setUpClass(cls):
        import importlib.util
        path = os.path.join(_HARNESS, "tools", "build_career_pad_craft.py")
        spec = importlib.util.spec_from_file_location("build_career_pad_craft", path)
        cls.builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.builder)

    def test_the_committed_fixture_satisfies_every_post_condition(self):
        # The `--check` path, run in-process.
        problems = self.builder.verify(self.builder.read_lines(FIXTURE_SFS),
                                       self.builder.CREW_NAME)
        self.assertEqual([], problems)

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_rebuild(self):
        # THE DRIFT GUARD. Re-runs the splice over the two CURRENT inputs and
        # compares against the committed bytes, so a change to either upstream
        # fixture reds here rather than in a live flight.
        saves = os.path.join(_HARNESS, "fixtures", "saves")
        base = self.builder.read_lines(
            os.path.join(saves, self.builder.BASE_NAME, "persistent.sfs"))
        donor = self.builder.read_lines(
            os.path.join(saves, self.builder.DONOR_NAME, "persistent.sfs"))
        rebuilt = self.builder.build(base, donor, self.builder.CREW_NAME,
                                     "%s (CAREER)" % self.builder.TARGET_NAME)
        self.assertEqual(self.builder.read_lines(FIXTURE_SFS), rebuilt,
                         "career-pad-craft has drifted from what "
                         "build_career_pad_craft.py produces from the current "
                         "fresh-career + b1-pad-craft; re-run the builder and "
                         "commit, or explain the divergence")

    def test_the_loadmeta_agrees_with_the_committed_save(self):
        lines = self.builder.read_lines(FIXTURE_SFS)
        meta = self.builder.read_lines(
            os.path.join(_HARNESS, "fixtures", "saves", "career-pad-craft",
                         "persistent.loadmeta"))
        self.assertIn("vesselCount = 1", meta)
        self.assertIn("gameMode = CAREER", meta)
        fs = self.builder.find_node(lines, "FLIGHTSTATE")
        self.assertIn("UT = %s" % self.builder.get_value(lines, fs, "UT"), meta)


class SpecContractTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec()

    def test_every_required_log_pattern_compiles_and_is_ascii(self):
        # The emitting call sites use U+2192 and U+2014 in their format strings, so
        # a spec author reaching for the "obvious" `->` writes a regex that matches
        # nothing. Every pattern here must stay plain ASCII (project rule) and must
        # compile.
        contracts = self.spec["expectations"]["logContracts"]
        for pattern in contracts["required"] + contracts["forbidden"]:
            re.compile(pattern)
            pattern.encode("ascii")

    def test_no_required_pattern_hard_codes_a_unicode_arrow_or_dash(self):
        for pattern in self.spec["expectations"]["logContracts"]["required"]:
            self.assertNotIn("->", pattern,
                             "the CrewStatusChanged separator is U+2192, not '->'")

    def test_the_crew_status_pattern_names_the_spec_crew_and_ends_at_dead(self):
        crew = self.spec["driver"]["missionParams"]["crewName"]
        matching = [p for p in self.spec["expectations"]["logContracts"]["required"]
                    if "CrewStatusChanged" in p]
        self.assertEqual(1, len(matching))
        self.assertIn(crew, matching[0])
        self.assertTrue(matching[0].rstrip().endswith("Dead"))
        # And it matches a REAL emitted line, arrow and all. The arrow is written
        # as an escape so this file stays plain ASCII while still exercising the
        # exact character the emitting format string uses (U+2192, NOT '->').
        self.assertRegex(
            "[Parsek][INFO][GameStateRecorder] Game state: CrewStatusChanged "
            "'%s' Assigned \u2192 Dead" % crew, matching[0])
        # And it ALSO matches the ASCII-arrow form. The pattern spans the
        # separator with `.*` rather than naming either character, so it survives
        # the emit's separator changing - and this cell reds if someone
        # "simplifies" it to a literal ' -> ' (which would then match nothing
        # real) or hard-codes the U+2192 (which would break on any rewording).
        self.assertRegex(
            "[Parsek][INFO][GameStateRecorder] Game state: CrewStatusChanged "
            "'%s' Assigned -> Dead" % crew, matching[0],
            "the pattern must span the separator with .* so it matches whichever "
            "character the emit uses")

    def test_the_mission_step_expects_mission_ok(self):
        # The inversion in one assertion: this scenario's own step list says the
        # death is the SUCCESS terminal.
        steps = self.spec["driver"]["steps"]
        mission = [s for s in steps if s.get("phase") == "mission"]
        self.assertEqual(1, len(mission))
        self.assertEqual("MISSION-OK", mission[0]["expect"])

    def test_the_spec_drives_no_commit_and_declares_no_ledger_block(self):
        # PROVEN UNREACHABLE, not a preference (Opus review panel 2026-07-28,
        # reviewer 2). When the ACTIVE RECORDED vessel is destroyed in tree mode,
        # ParsekFlight stashes the tree as PENDING and nulls activeTree, so the
        # seam's CommitTree fails its HasActiveTree guard: the archived crash of
        # this exact craft logs `committree no-active-tree` and
        # `OnSave: saving 0 committed tree(s)`
        # (logs/2026-07-20_1829_B1-pad-hop/KSP.log:11310, :11325). An unmet step
        # would make the run driver-INVALID and SKIP every verifier below it, so
        # the scenario would produce no evidence about the death at all.
        #
        # Everything commit-dependent goes with it: the two ledger tokens and
        # `[expectations.ledger]`. This cell is what stops them being re-added
        # without the commit route that makes them reachable.
        cmds = [s.get("cmd") for s in self.spec["driver"]["steps"]]
        self.assertNotIn("CommitTree", cmds)
        self.assertNotIn("ledger", self.spec["expectations"])
        required = self.spec["expectations"]["logContracts"]["required"]
        for commit_only in ("PopulateCrewEndStates", "CreateKerbalAssignmentActions",
                            "committree"):
            self.assertFalse(
                any(commit_only in p for p in required),
                "%s is emitted only on a commit this scenario cannot reach"
                % commit_only)

    def test_the_destruction_path_tokens_are_required(self):
        # The three tokens that pin the recorder-side shape of a crew loss end to
        # end - and the same path that blocks the ledger half, so a change to it
        # reds here first.
        required = self.spec["expectations"]["logContracts"]["required"]
        for token in ("Active vessel destroyed during recording",
                      "Active vessel destroyed in tree mode",
                      "ShowPostDestructionTreeMergeDialog: finalized tree",
                      "pending tree stashed"):
            self.assertIn(token, required)

    def test_no_ledger_dimension_is_claimed(self):
        # D8 is entirely commit-time. Claiming any of it while driving no commit
        # would be coverage this scenario does not earn.
        self.assertNotIn("D8", self.spec["dimensionsCovered"])

    def test_the_scenario_claims_the_new_crew_death_dimension_value(self):
        with open(REGISTRY_PATH, "rb") as fh:
            registry = tomllib.load(fh)
        self.assertIn("crew-death-in-flight", registry["D12"]["values"])
        self.assertIn("crew-death-in-flight",
                      self.spec["dimensionsCovered"]["D12"])

    def test_the_re_fly_crew_cells_stay_unclaimed(self):
        # They are what this atom is meant to be extended into. Claiming them here
        # would hide the backlog the coverage report exists to show.
        claimed = self.spec["dimensionsCovered"]["D12"]
        self.assertNotIn("dead-crew-strip", claimed)
        self.assertNotIn("tombstone-rep-penalty", claimed)


if __name__ == "__main__":
    unittest.main()
