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
        # not pass instantly and green. Many frames, no success, no assert-fail:
        # the machine keeps flying and the budget owns the outcome.
        state = drive(fresh(), *(["Dead"] * 20))
        self.assertFalse(state.done)
        self.assertFalse(state.crew_alive_aboard_seen)
        self.assertNotEqual(mlib.CL1_CREW_LOST, state.phase)

    def test_available_does_not_earn_the_alive_aboard_latch(self):
        # Available means "in the roster, not on a vessel". The precondition is
        # specifically alive AND ABOARD, so only Assigned earns it.
        state = drive(fresh(), "Available", "Available", "Available")
        self.assertFalse(state.crew_alive_aboard_seen)

    def test_the_alive_aboard_latch_is_sticky(self):
        # A kerbal who was aboard and is now dead must not have that erased by the
        # frames that follow (the reading necessarily stops being Assigned).
        state = drive(fresh(), "Assigned", "Assigned", "Available", "Dead", "Dead")
        self.assertTrue(state.crew_alive_aboard_seen)
        self.assertEqual(mlib.CL1_CREW_LOST, state.phase)

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
        self.assertIn("crew-watch-name-unknown", state.loss_reason)
        self.assertIn("Jebeddiah Kermin", state.loss_reason)
        # It is an ASSERT-FAIL, so resolve_flight_verdict short-circuits on the
        # loss_reason before the assertions can see a machine that flew nothing.
        outcomes = mlib.evaluate_cl1_assertions(
            [], mlib.cl1_params_from_dict(PARAMS), state)
        verdict, reason = mlib.resolve_flight_verdict(state, outcomes)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, verdict)
        self.assertIn("crew-watch-name-unknown", reason)

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
        self.assertIn("roster=Assigned", state.loss_reason)
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
        before = state.peak_altitude
        state, _ = mlib.cl1_decide(
            state, snap(ut=50.0, altitude=99999.0, vessel_lost=True,
                        crew_roster_status="Assigned"))
        self.assertEqual(before, state.peak_altitude)

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
        state = drive(fresh(), "Available", "Available")
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
        self.assertEqual(list(mlib.ROSTER_STATUS_NOT_ALIVE), detail["accepted"])
        self.assertEqual(mlib.CL1_ROSTER_DEBOUNCE_K, detail["debounceK"])


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

    def test_commit_tree_runs_after_the_mission(self):
        # The kerbal-death ledger row is created at COMMIT time, which is why the
        # mission must be MET (an unmet one drives the cleanup tail only and skips
        # CommitTree, and the scenario could not reach its own subject).
        cmds = [s.get("cmd") for s in self.spec["driver"]["steps"]]
        self.assertIn("CommitTree", cmds)
        self.assertGreater(cmds.index("CommitTree"),
                           [s.get("phase") for s in self.spec["driver"]["steps"]].index("mission"))

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


class LedgerManifestTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.spec = _read_spec()
        cls.block = cls.spec["expectations"]["ledger"]

    def test_the_kerbal_death_kind_is_admitted_by_the_oracle(self):
        self.assertIn("kerbal-death", oracle.KINDS)

    def test_the_declared_manifest_parses_clean(self):
        parsed = oracle.parse_manifest_entries(self.block["manifest"])
        self.assertEqual((), parsed.errors)
        self.assertEqual(1, len(parsed.entries))
        self.assertEqual("kerbal-death", parsed.entries[0].kind)

    def test_the_death_is_declared_pool_neutral(self):
        # Nothing in Source/Parsek ever constructs a
        # ReputationPenaltySource.KerbalDeath action, so there is no repo-derivable
        # magnitude to declare; zero is the strongest falsifiable claim before a
        # flight. If a live run reds on reputation, RE-PIN from observedAfter=.
        entry = self.block["manifest"][0]
        self.assertEqual(0.0, entry["funds"])
        self.assertEqual(0.0, entry["science"])
        self.assertEqual(0.0, entry["reputation"])

    def test_a_zero_rep_entry_leaves_expected_equal_to_the_seed(self):
        # The arithmetic the scenario actually asserts: with every facet at zero,
        # expected == seed, so ANY pool movement in the produced save reds.
        parsed = oracle.parse_manifest_entries(self.block["manifest"])
        self.assertTrue(parsed.ok)
        seed = oracle.SeedBaseline(funds=500000.0, science=100.0, reputation=0.0)
        expected = oracle.compute_expected(seed, parsed.entries)
        self.assertEqual(500000.0, expected.funds)
        self.assertEqual(100.0, expected.science)
        self.assertEqual(0.0, expected.reputation)

    def test_the_seed_comes_from_the_template(self):
        self.assertEqual("template", self.block["seedFrom"])


if __name__ == "__main__":
    unittest.main()
