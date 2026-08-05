"""Unit cells for the GS-1 auto-chute-booster lane (mission
`gs1_auto_chute_booster`, scenarios `GS-1-auto-chute-booster` +
`FORGE-gs1-two-stage`, craft builder `harness/tools/build_gs1_craft.py`).

Four groups, each guarding a different way this lane can be silently wrong:

  1. THE PURE MACHINE (`mlib.gs1_decide` / `evaluate_gs1_assertions`). The two
     things that must not be commanded latches are the separation and the canopy,
     and both are pinned here in BOTH directions: a stage click that fired nothing
     must NOT satisfy separationObserved, and a canopy that never opened must NOT
     satisfy craftCanopyObserved.
  2. THE SHELL, driven end to end over a scripted flight with no krpc and no KSP.
  3. THE CRAFT. The .craft is authored by construction and nothing else in the repo
     can tell whether it still says what the scenario needs it to say - most of all
     that BOTH sides of the decoupler carry a ModuleCommand (without which no
     RewindPoint is authored and the whole scenario is vacuous) and that the
     booster chutes share the decoupler's stage.
  4. SPEC / REGISTRY SYNC. The spec's mission params have to satisfy the schema the
     mission declares, its dimension claims have to exist in the registry, and the
     forge has to point at the craft the builder writes. Getting any of those wrong
     costs a live flight to discover.

NO krpc, NO KSP, NO network. Import path matches the sibling suites: discovery
runs from `harness/` with `missions/lib` as the root, and `missions/` is prepended
so `import mission_runner` / `import gs1_auto_chute_booster` resolve.
"""

import dataclasses
import importlib.util
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
import gs1_auto_chute_booster      # noqa: E402
from test_shells import FakeMissionControl, run, snap  # noqa: E402

SPEC_PATH = os.path.join(_HARNESS, "scenarios", "GS-1-auto-chute-booster.toml")
FORGE_SPEC_PATH = os.path.join(_HARNESS, "scenarios", "FORGE-gs1-two-stage.toml")
SCHEMA_PATH = os.path.join(_MISSIONS, "gs1_auto_chute_booster.schema.toml")
REGISTRY_PATH = os.path.join(_HARNESS, "coverage", "registry.toml")

# Live thrust of the booster's LV-T45 at sea level, near enough: the only property
# any cell depends on is that it is orders of magnitude above the epsilon.
LIT = 168000.0

GS1_PARAMS = {
    "throttle": 1.0,
    "stagingApoapsisMeters": 700.0,
    "stagingMaxAltMeters": 1500.0,
    "apoapsisWindowMeters": {"min": 600.0, "max": 1600.0},
    "stageSettleFrames": 2,
    "chuteArmMaxRateMps": 30.0,
    "chuteFullDeployAltMeters": 600.0,
    "landedSituations": ["LANDED", "SPLASHED"],
    "ascentTimeoutSeconds": 90.0,
    "coastTimeoutSeconds": 120.0,
    "stageTimeoutSeconds": 60.0,
    "descentTimeoutSeconds": 300.0,
    "siblingVesselName": "GS1 Auto-Chute Booster Probe",
    "siblingDownTimeoutSeconds": 240.0,
}


def params(**over):
    out = dict(GS1_PARAMS)
    out.update(over)
    return out


def machine(**over):
    return mlib.gs1_initial_state(mlib.gs1_params_from_dict(params(**over)))


def replace_state_unarmed(state):
    """Clear the chute-armed latch so the below-floor streak is reachable. The
    machine arms at the apex by design, and the give-up only counts UNARMED
    frames, so exercising it needs the latch cleared."""
    return dataclasses.replace(state, chute_commanded=False,
                               chute_armed_altitude=None, chute_armed_rate=None)


def step(state, **kw):
    kw.setdefault("available_thrust", LIT)
    return mlib.gs1_decide(state, snap(**kw))


def fly(state, **kw):
    """``step`` with the booster present and airborne - the reading every frame after
    the split carries on a nominal flight."""
    kw.setdefault("sibling_present", 1)
    kw.setdefault("sibling_situation", "FLYING")
    return step(state, **kw)


def to_separation(**over):
    """Drive PRELAUNCH -> ASCENT -> STAGE -> the frame the SEPARATION is emitted on.
    Returns (state, actions_of_that_frame); the machine is then in COAST.

    The shape mirrors the MEASURED flight profile: the throttle cut fires well below
    the apex while the craft is still climbing hard, the settle wait holds a poll,
    and the separation happens WITH AIRSPEED so the two halves actually come apart
    (flight 2 separated at the apex, drifted 5 m, and they collided)."""
    state = machine(**over)
    state, _ = step(state, ut=0.0, altitude=70.0, situation="PRE_LAUNCH")
    # Climb until the apoapsis target trips the throttle cut (still ascending fast).
    state, _ = step(state, ut=2.0, altitude=200.0, apoapsis=300.0,
                    vertical_speed=90.0, situation="FLYING")
    state, _ = step(state, ut=4.0, altitude=500.0, apoapsis=750.0,
                    vertical_speed=60.0, situation="FLYING")
    assert state.phase == mlib.GS1_STAGE, state.phase
    # stageSettleFrames frames in STAGE; the last one emits the separation, still
    # climbing.
    actions = []
    for i in range(int(params(**over)["stageSettleFrames"])):
        state, actions = step(state, ut=5.0 + i, altitude=560.0 + 40.0 * i,
                              apoapsis=780.0, vertical_speed=50.0 - 10.0 * i,
                              situation="FLYING")
    assert state.phase == mlib.GS1_COAST, state.phase
    return state, actions


def to_descent(**over):
    """``to_separation`` plus the ballistic climb and the APEX frame, which is where
    the upper stage's chute is armed. Returns (state, actions_of_the_apex_frame)."""
    state, _ = to_separation(**over)
    state, _ = fly(state, ut=9.0, altitude=760.0, apoapsis=780.0,
                   vertical_speed=20.0, situation="FLYING", available_thrust=0.0)
    state, actions = fly(state, ut=11.0, altitude=800.0, apoapsis=780.0,
                         vertical_speed=-2.0, situation="FLYING",
                         available_thrust=0.0)
    assert state.phase == mlib.GS1_DESCENT, state.phase
    return state, actions


def to_sibling_down(**over):
    """``to_descent`` plus a chuted descent and the pod's touchdown. Leaves the
    machine in SIBLING-DOWN with the booster still airborne."""
    state, _ = to_descent(**over)
    for i in range(3):
        state, _ = fly(state, ut=13.0 + i, altitude=500.0 - 100.0 * i,
                       vertical_speed=-8.0, situation="FLYING",
                       available_thrust=0.0,
                       craft_chute_state=mlib.CHUTE_STATE_DEPLOYED)
    state, _ = fly(state, ut=30.0, altitude=71.0, vertical_speed=-5.0,
                   situation="LANDED", available_thrust=0.0,
                   craft_chute_state=mlib.CHUTE_STATE_DEPLOYED)
    assert state.phase == mlib.GS1_SIBLING_DOWN, state.phase
    return state


# ---------------------------------------------------------------------------
# 1. The pure machine.
# ---------------------------------------------------------------------------


class Gs1PhaseSequenceTests(unittest.TestCase):

    def test_prelaunch_sets_throttle_and_ignites_on_the_first_frame(self):
        state, actions = step(machine(), ut=0.0, altitude=70.0,
                              situation="PRE_LAUNCH")
        # The sibling watch is armed FIRST (it issues no RPC and must be live before
        # anything can split), then throttle, then ignition.
        self.assertEqual([mlib.ACTION_SET_SIBLING_WATCH, mlib.ACTION_SET_THROTTLE,
                          mlib.ACTION_ACTIVATE_STAGE], [a.kind for a in actions])
        self.assertEqual(1.0, actions[1].value)
        self.assertEqual(mlib.GS1_ASCENT, state.phase)

    def test_ascent_cuts_throttle_at_the_target_and_hands_off_to_descent(self):
        # The cut and the separation must never ride the same frame: stageSettleFrames
        # is the only thing between them, and firing a decoupler while the lower stage
        # is still pushing drives the spent booster INTO the upper stage.
        state = machine()
        state, _ = step(state, ut=0.0, altitude=70.0, situation="PRE_LAUNCH")
        state, actions = step(state, ut=3.0, altitude=400.0, apoapsis=705.0,
                              vertical_speed=70.0, situation="FLYING")
        self.assertEqual([mlib.ACTION_CUT_THROTTLE], [a.kind for a in actions])
        self.assertEqual(mlib.GS1_STAGE, state.phase)

    def test_the_separation_happens_WITH_AIRSPEED_not_at_the_apex(self):
        # THE REGRESSION CELL FOR FLIGHT 2'S KILL. Flight 2 staged at the apex, where
        # there is no airspeed and therefore no differential drag: the halves parted
        # by 5.49 m, fell together (upper 84.1 m, booster 85.9 m over 3.6 s), and the
        # upper stage struck the booster's topmost part 2.0 m below it, exploding it.
        # The separation must therefore be emitted while the craft is still climbing
        # hard, so the booster's canopies can bite and pull it away.
        state, actions = to_separation()
        self.assertIn(mlib.ACTION_ACTIVATE_STAGE, [a.kind for a in actions])
        self.assertGreater(state.stage_ut, 0.0)
        # The machine's own record of the separation frame must show it CLIMBING.
        self.assertEqual(mlib.GS1_COAST, state.phase,
                         "after separating the machine coasts to the apex; reaching "
                         "DESCENT here would mean it thought it was already falling")
        self.assertFalse(state.chute_commanded,
                         "the upper chute must NOT be armed at separation - it is "
                         "armed at the apex, which is still ahead")

    def test_coast_holds_the_whole_ballistic_climb_after_the_separation(self):
        # THE REGRESSION CELL FOR FLIGHT 1'S ROOT CAUSE, re-pointed at COAST's new
        # position. Replays the MEASURED post-cut climb (alt 230 -> 287 -> 339 -> 444,
        # all still ascending, while the ORBIT apoapsis DECAYS 957 -> 940 -> 926 ->
        # 908 under drag) and asserts the machine does not reach DESCENT. The decaying
        # apoapsis is why the apex signal has to be the vertical speed: the reading
        # falls while the craft rises, so it names neither the apex altitude nor its
        # moment.
        state, _ = to_separation()
        for ut, alt, ap, vs in ((30.08, 230.3, 957.2, 112.9),
                                (30.60, 287.3, 940.2, 106.0),
                                (31.10, 338.8, 926.5, 100.2),
                                (32.20, 444.0, 908.6, 89.0)):
            state, actions = step(state, ut=ut, altitude=alt, apoapsis=ap,
                                  vertical_speed=vs, situation="FLYING",
                                  available_thrust=0.0)
            self.assertEqual(mlib.GS1_COAST, state.phase,
                             "reached DESCENT at alt %s while still climbing at %s "
                             "m/s - this is flight 1's failure" % (alt, vs))
            self.assertEqual([], actions)
        # The apex, at last: DESCENT, and the arm rides that same frame.
        state, actions = step(state, ut=41.0, altitude=850.0, apoapsis=890.0,
                              vertical_speed=-2.0, situation="FLYING",
                              available_thrust=0.0)
        self.assertEqual(mlib.GS1_DESCENT, state.phase)
        self.assertIn(mlib.ACTION_DEPLOY_CHUTE, [a.kind for a in actions])

    def test_ascent_falling_past_the_target_still_cuts_before_it_stages(self):
        # The fallback: a burn that ran long, or a poll stall across the top, leaves
        # ASCENT with the vertical speed already negative. The cut still rides that
        # frame and the separation still does NOT -- which is the whole reason the
        # settle wait survives.
        state = machine(stagingApoapsisMeters=50000.0)
        state, _ = step(state, ut=0.0, altitude=70.0, situation="PRE_LAUNCH")
        state, actions = step(state, ut=9.0, altitude=800.0, apoapsis=810.0,
                              vertical_speed=-1.0, situation="FLYING")
        self.assertEqual([mlib.ACTION_CUT_THROTTLE], [a.kind for a in actions],
                         "the cut must ride this frame and the separation must NOT")
        self.assertEqual(mlib.GS1_STAGE, state.phase)

    def test_the_separation_waits_out_stage_settle_frames(self):
        state = machine()
        state, _ = step(state, ut=0.0, altitude=70.0, situation="PRE_LAUNCH")
        state, cut = step(state, ut=3.0, altitude=400.0, apoapsis=705.0,
                          vertical_speed=70.0, situation="FLYING")
        self.assertEqual([mlib.ACTION_CUT_THROTTLE], [a.kind for a in cut])
        state, first = step(state, ut=3.5, altitude=430.0, apoapsis=760.0,
                            vertical_speed=60.0, situation="FLYING")
        self.assertEqual([], first, "the first settle frame emits nothing")
        self.assertEqual(mlib.GS1_STAGE, state.phase)
        state, second = step(state, ut=4.0, altitude=460.0, apoapsis=760.0,
                             vertical_speed=55.0, situation="FLYING")
        self.assertIn(mlib.ACTION_ACTIVATE_STAGE, [a.kind for a in second])
        self.assertEqual(mlib.GS1_COAST, state.phase)

    def test_exactly_two_stage_activations_are_ever_emitted(self):
        # THE STAGING CONTRACT IN THE MACHINE. The craft has three stages and the
        # mission may only click TWO of them: ignite, then decoupler-plus-booster-
        # chutes. The third (the upper stage's own Mk16) is armed through the
        # parachute verbs so its deployAltitude can be pinned. A third click would
        # arm the upper chute at an unintended moment.
        state = machine()
        stages = 0
        for i in range(200):
            state, actions = step(
                state, ut=float(i), altitude=700.0, apoapsis=760.0,
                vertical_speed=-5.0, situation="FLYING",
                available_thrust=LIT if i < 6 else 0.0)
            stages += sum(1 for a in actions if a.kind == mlib.ACTION_ACTIVATE_STAGE)
            if state.done:
                break
        self.assertEqual(2, stages)


class Gs1SeparationLatchTests(unittest.TestCase):
    """separationObserved is an OBSERVED latch.

    Every cell here drives ``to_separation`` rather than ``to_descent``: the latch
    must be exercised against thrust readings the CELL supplies, and to_descent
    feeds zero-thrust coast frames that would certify it first.
 B1 shipped four months of green
    nightlies on a chute that never opened because its terminal read a COMMANDED
    latch; these cells are what stop the same class of lie about the split."""

    def test_a_stage_click_that_fired_nothing_does_not_satisfy_the_row(self):
        # The decoupler did not blow (a mis-numbered istg, a decoupler with no
        # ModuleDecouple in its stage): the upper stage still carries the engine,
        # so AvailableThrust never falls. The machine COMMANDED the stage, and the
        # row must still be UNMET.
        state, _ = to_separation()
        self.assertTrue(state.stage_commanded)
        for i in range(20):
            state, _ = step(state, ut=10.0 + i, altitude=700.0 - 20 * i,
                            apoapsis=760.0, vertical_speed=-20.0,
                            situation="FLYING", available_thrust=LIT)
        rows = {r.name: r for r in mlib.evaluate_gs1_assertions(
            [], mlib.gs1_params_from_dict(GS1_PARAMS), state=state)}
        self.assertFalse(rows["separationObserved"].met)
        self.assertTrue(rows["separationObserved"].detail["stageCommanded"],
                        "the commanded half must still be REPORTED, so the result "
                        "JSON carries both halves of the distinction")

    def test_thrust_falling_away_after_the_stage_satisfies_the_row(self):
        state, _ = to_separation()
        for i in range(3):
            state, _ = step(state, ut=10.0 + i, altitude=700.0, apoapsis=760.0,
                            vertical_speed=-10.0, situation="FLYING",
                            available_thrust=0.0)
        self.assertTrue(state.separation_seen)
        rows = {r.name: r for r in mlib.evaluate_gs1_assertions(
            [], mlib.gs1_params_from_dict(GS1_PARAMS), state=state)}
        self.assertTrue(rows["separationObserved"].met)

    def test_a_single_zero_thrust_frame_does_not_certify(self):
        # Debounced for the same reason the canopy latch is: a terminal that
        # CERTIFIES deserves the same treatment as one that condemns, and kRPC can
        # return a stale or mid-transition reading on the one frame a vessel splits.
        state, _ = to_separation()
        state, _ = step(state, ut=10.0, altitude=700.0, vertical_speed=-10.0,
                        situation="FLYING", available_thrust=0.0)
        self.assertFalse(state.separation_seen)
        state, _ = step(state, ut=11.0, altitude=690.0, vertical_speed=-12.0,
                        situation="FLYING", available_thrust=LIT)
        self.assertEqual(0, state.separation_streak)

    def test_a_craft_whose_engine_never_lit_cannot_satisfy_the_row(self):
        # THE OTHER HALF, and the one a naive "thrust is zero now" gate would miss:
        # a craft that never had a live engine reads 0.0 from the first frame, so
        # "thrust is zero" is satisfied by a rocket that never flew. The latch
        # therefore requires a real POSITIVE pre-stage reading first.
        state = machine()
        state, _ = step(state, ut=0.0, altitude=70.0, situation="PRE_LAUNCH",
                        available_thrust=0.0)
        state, _ = step(state, ut=3.0, altitude=70.0, apoapsis=705.0,
                        vertical_speed=1.0, situation="FLYING",
                        available_thrust=0.0)
        for i in range(8):
            state, _ = step(state, ut=4.0 + i, altitude=70.0, apoapsis=705.0,
                            vertical_speed=-1.0, situation="FLYING",
                            available_thrust=0.0)
        self.assertIsNone(state.pre_stage_thrust)
        self.assertFalse(state.separation_seen)

    def test_a_nonfinite_thrust_read_never_advances_either_half(self):
        state, _ = to_separation()
        for i in range(5):
            state, _ = step(state, ut=10.0 + i, altitude=700.0,
                            vertical_speed=-10.0, situation="FLYING",
                            available_thrust=float("nan"))
        self.assertFalse(state.separation_seen)


class Gs1StagingCeilingTests(unittest.TestCase):
    """stagedBelowCeiling is what keeps the separated booster inside stock's
    ~2.25 km physics bubble. A run that staged high would satisfy every other row
    and still lose the booster to the unloaded-vessel deletion rule."""

    def test_a_low_separation_meets_the_ceiling(self):
        state, _ = to_descent()
        rows = {r.name: r for r in mlib.evaluate_gs1_assertions(
            [], mlib.gs1_params_from_dict(GS1_PARAMS), state=state)}
        self.assertTrue(rows["stagedBelowCeiling"].met)
        self.assertLessEqual(rows["stagedBelowCeiling"].value, 1500.0)

    def test_a_high_separation_reds_the_row(self):
        state = machine(stagingApoapsisMeters=40000.0)
        state, _ = step(state, ut=0.0, altitude=70.0, situation="PRE_LAUNCH")
        state, _ = step(state, ut=30.0, altitude=8000.0, apoapsis=41000.0,
                        vertical_speed=200.0, situation="FLYING")
        for _ in range(3):
            state, _ = step(state, ut=40.0, altitude=9000.0, apoapsis=41000.0,
                            vertical_speed=5.0, situation="FLYING")
        rows = {r.name: r for r in mlib.evaluate_gs1_assertions(
            [], mlib.gs1_params_from_dict(GS1_PARAMS), state=state)}
        self.assertFalse(rows["stagedBelowCeiling"].met)

    def test_a_separation_that_never_happened_reds_the_row_rather_than_passing(self):
        # Fails CLOSED: no stage altitude was ever captured, so the row is UNMET
        # rather than vacuously true against a missing reading.
        rows = {r.name: r for r in mlib.evaluate_gs1_assertions(
            [], mlib.gs1_params_from_dict(GS1_PARAMS), state=machine())}
        self.assertFalse(rows["stagedBelowCeiling"].met)


class Gs1CanopyTests(unittest.TestCase):
    """B1's contract verbatim, re-pinned here because it is the row a future edit
    is most likely to relax back into a commanded latch."""

    def test_the_arm_rides_the_first_slow_descent_frame_with_the_altitude_write(self):
        state, actions = to_descent()
        kinds = [a.kind for a in actions]
        self.assertIn(mlib.ACTION_SET_CHUTE_DEPLOY_ALTITUDE, kinds)
        self.assertIn(mlib.ACTION_DEPLOY_CHUTE, kinds)
        self.assertLess(kinds.index(mlib.ACTION_SET_CHUTE_DEPLOY_ALTITUDE),
                        kinds.index(mlib.ACTION_DEPLOY_CHUTE),
                        "the deployAltitude write must precede the arm on the SAME "
                        "frame, so the module's first ACTIVE FixedUpdate already "
                        "sees the raised altitude")

    def test_a_commanded_but_never_open_canopy_is_unmet(self):
        state, _ = to_descent()
        self.assertTrue(state.chute_commanded)
        for i in range(10):
            state, _ = step(state, ut=20.0 + i, altitude=600.0 - 30 * i,
                            vertical_speed=-40.0, situation="FLYING",
                            available_thrust=0.0,
                            craft_chute_state=mlib.CHUTE_STATE_ARMED)
            if state.done:
                break
        rows = {r.name: r for r in mlib.evaluate_gs1_assertions(
            [], mlib.gs1_params_from_dict(GS1_PARAMS), state=state)}
        self.assertFalse(rows["craftCanopyObserved"].met)
        self.assertTrue(rows["craftCanopyObserved"].detail["armCommanded"])

    def test_two_consecutive_deployed_reads_certify_and_stay_sticky(self):
        state, _ = to_descent()
        for i in range(2):
            state, _ = step(state, ut=20.0 + i, altitude=500.0,
                            vertical_speed=-8.0, situation="FLYING",
                            available_thrust=0.0,
                            craft_chute_state=mlib.CHUTE_STATE_DEPLOYED)
        self.assertTrue(state.craft_chute_full_seen)
        # A canopy CUT or destroyed on a later frame must not erase a descent that
        # really happened.
        state, _ = step(state, ut=30.0, altitude=100.0, vertical_speed=-8.0,
                        situation="FLYING", available_thrust=0.0,
                        craft_chute_state=mlib.CHUTE_STATE_CUT)
        self.assertTrue(state.craft_chute_full_seen)

    def test_being_below_the_floor_while_CLIMBING_never_counts(self):
        # THE OTHER HALF OF FLIGHT 1'S KILL. The give-up counts unarmed frames below
        # chuteFullDeployAltMeters. On a hop whose apex is at or under that floor the
        # craft is below it FROM THE PAD, so before the falling conjunct the predicate
        # was satisfiable by construction and tripped two frames into DESCENT while
        # the craft was still going UP (MEASURED alt 339 then 444, +100 and +89 m/s).
        #
        # The COAST reorder makes that unreachable a SECOND way (DESCENT is now only
        # entered once the craft is falling), so this cell drives the conjunct
        # DIRECTLY: a DESCENT frame that momentarily reads a POSITIVE vertical speed
        # -- a bounce, a glitched sample, a gust -- must not advance the streak.
        # Defence in depth is deliberate: the two guards fail independently.
        state, _ = to_descent(chuteFullDeployAltMeters=5000.0)
        self.assertEqual(mlib.GS1_DESCENT, state.phase)
        state = replace_state_unarmed(state)
        for i in range(8):
            state, _ = step(state, ut=20.0 + i, altitude=300.0 + 100.0 * i,
                            vertical_speed=90.0, situation="FLYING",
                            available_thrust=0.0)
            self.assertFalse(state.done,
                             "gave up on a climbing frame below the floor")
            self.assertEqual(0, state.below_floor_streak)

    def test_the_arm_window_missed_terminal_is_named_and_fast(self):
        # Without this branch a skipped poll across the apex turns a DETERMINISTIC
        # failure into an unnamed descent-budget MISSION-FLAKE.
        state = machine(chuteArmMaxRateMps=1.0)
        state, _ = step(state, ut=0.0, altitude=70.0, situation="PRE_LAUNCH")
        state, _ = step(state, ut=3.0, altitude=400.0, apoapsis=705.0,
                        vertical_speed=70.0, situation="FLYING")
        for i in range(6):
            state, _ = step(state, ut=4.0 + i, altitude=900.0 - 150.0 * i,
                            vertical_speed=-150.0, situation="FLYING",
                            available_thrust=0.0)
            if state.done:
                break
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("chute-arm-window-missed", state.loss_reason)


class Gs1TerminalTests(unittest.TestCase):

    def test_the_pods_touchdown_hands_off_to_the_booster_wait(self):
        # THE REGRESSION CELL FOR FLIGHT 3. The pod landing is NOT the end of the
        # flight: flight 3 terminated here, `ExitToSpaceCenter` fired while the
        # booster was still under canopy, and the booster's recording closed
        # `terminal=SubOrbital` -> `IsUnfinishedFlight=true ... reason=
        # stableLeafUnconcluded` -> CommittedProvisional -> no reap.
        state, _ = to_descent()
        state, _ = fly(state, ut=40.0, altitude=72.0, vertical_speed=-5.0,
                       situation="LANDED", available_thrust=0.0)
        self.assertEqual(mlib.GS1_SIBLING_DOWN, state.phase)
        self.assertFalse(state.done)

    def test_a_landed_situation_ends_the_flight_when_no_sibling_is_watched(self):
        # With no siblingVesselName there is nothing to wait for, so the pod's
        # touchdown is still terminal. Keeps the machine usable for a single-stage
        # profile and keeps the handoff from being unconditional.
        state, _ = to_descent(siblingVesselName="")
        state, _ = step(state, ut=40.0, altitude=72.0, vertical_speed=-5.0,
                        situation="LANDED", available_thrust=0.0)
        self.assertEqual(mlib.GS1_LANDED, state.phase)
        self.assertTrue(state.done)
        self.assertIsNone(state.verdict,
                          "LANDED leaves the verdict to the assertions, exactly as "
                          "B1's terminal does")

    def test_a_stale_pad_LANDED_reading_cannot_end_the_flight(self):
        # MEASURED, and it had not bitten yet only by luck: KSP reported
        # `situation = LANDED` up to ut 30.08, at alt 230 m, CLIMBING at 113 m/s.
        # An ungated `situation in landedSituations` would have declared the flight
        # over on such a frame and resolved every terminal row against a craft still
        # on its way up. Same defect class as the one filed against CL-1 ("a craft
        # that never launched satisfies landed with crew alive").
        state = machine()
        state, _ = step(state, ut=0.0, altitude=1.9, situation="PRE_LAUNCH")
        state, _ = step(state, ut=29.58, altitude=172.4, apoapsis=950.5,
                        vertical_speed=117.2, situation="LANDED")
        state, _ = step(state, ut=30.08, altitude=230.3, apoapsis=957.2,
                        vertical_speed=112.9, situation="LANDED")
        self.assertFalse(state.airborne_seen,
                         "nothing so far has been an airborne situation")
        self.assertFalse(state.done)
        self.assertNotEqual(mlib.GS1_LANDED, state.phase)
        rows = {r.name: r for r in mlib.evaluate_gs1_assertions(
            [snap(situation="LANDED")], mlib.gs1_params_from_dict(GS1_PARAMS),
            state=state)}
        self.assertFalse(rows["landedSituation"].met,
                         "a tail frame reading LANDED must not satisfy the row when "
                         "the machine never reached its own gated terminal")
        self.assertFalse(rows["landedSituation"].detail["airborneSeen"])

    def test_the_landed_terminal_works_once_the_craft_has_been_airborne(self):
        state, _ = to_descent()
        self.assertTrue(state.airborne_seen)
        state, _ = fly(state, ut=40.0, altitude=72.0, vertical_speed=-5.0,
                       situation="LANDED", available_thrust=0.0)
        self.assertNotEqual(mlib.GS1_DESCENT, state.phase,
                            "the pod's touchdown must be accepted; it hands off to "
                            "the booster wait rather than terminating")

    def test_a_vessel_loss_is_a_deterministic_assert_fail_not_a_success(self):
        # DELIBERATELY UNLIKE B1: there is no success-by-destruction terminal here.
        # This scenario's whole subject is a ROUTINE flight that must NOT flood
        # Unfinished Flights, so a destroyed active vessel is a different scenario,
        # not a pass - and the reason NAMES the canopy state and the split.
        state, _ = to_descent()
        state, _ = step(state, ut=40.0, altitude=300.0, situation="FLYING",
                        vessel_lost=True)
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("craftChute=", state.loss_reason)
        self.assertIn("separationObserved=", state.loss_reason)

    def test_a_stuck_phase_flakes_by_name_rather_than_hanging(self):
        state = machine()
        state, _ = step(state, ut=0.0, altitude=70.0, situation="PRE_LAUNCH")
        state, _ = step(state, ut=1000.0, altitude=70.0, apoapsis=10.0,
                        vertical_speed=1.0, situation="FLYING")
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertEqual(mlib.GS1_ASCENT, state.flake_phase)

    def test_the_machine_is_idempotent_once_done(self):
        state = to_sibling_down()
        for i in range(2):
            state, _ = step(state, ut=41.0 + i, situation="LANDED",
                            sibling_present=1, sibling_situation="LANDED")
        self.assertTrue(state.done)
        again, actions = step(state, ut=50.0, situation="LANDED")
        self.assertEqual(state, again)
        self.assertEqual([], actions)


class Gs1BoosterWaitTests(unittest.TestCase):
    """SIBLING-DOWN: the bounded wait on a stage this mission never flies.

    Flight 3 (`2026-08-05_0807`) is why it exists. The pod landed, the mission
    concluded, `ExitToSpaceCenter` fired, and the booster - still under six canopies
    from a higher separation - closed its recording `terminal=SubOrbital`. Parsek
    then did the right thing (`IsUnfinishedFlight=true rec=81e48efe... reason=
    stableLeafUnconcluded slot=1 focusSlot=0 terminal=SubOrbital side=child` ->
    `CommitTree promoted rec=81e48efe... to CommittedProvisional`), the RP could not
    reap, and the spec red on its own forbidden pattern."""

    def test_the_watch_is_armed_on_the_very_first_frame(self):
        # Armed before anything flies, and by NAME: the booster does not exist until
        # the split, so a handle taken any earlier would be stale and one taken later
        # would need a frame nobody schedules.
        state, actions = step(machine(), ut=0.0, altitude=2.0,
                              situation="PRE_LAUNCH", available_thrust=0.0)
        armed = [a for a in actions if a.kind == mlib.ACTION_SET_SIBLING_WATCH]
        self.assertEqual(1, len(armed))
        self.assertEqual("GS1 Auto-Chute Booster Probe", armed[0].text)

    def test_no_watch_action_when_no_sibling_is_declared(self):
        state, actions = step(machine(siblingVesselName=""), ut=0.0, altitude=2.0,
                              situation="PRE_LAUNCH", available_thrust=0.0)
        self.assertEqual([], [a for a in actions
                              if a.kind == mlib.ACTION_SET_SIBLING_WATCH])

    def test_the_wait_holds_while_the_booster_is_still_flying(self):
        state = to_sibling_down()
        for i in range(20):
            state, _ = step(state, ut=31.0 + i, situation="LANDED",
                            sibling_present=1, sibling_situation="FLYING")
            self.assertFalse(state.done, "concluded while the booster was airborne")
        self.assertEqual(mlib.GS1_SIBLING_DOWN, state.phase)

    def test_two_agreeing_landed_reads_conclude_the_wait(self):
        state = to_sibling_down()
        state, _ = step(state, ut=31.0, situation="LANDED",
                        sibling_present=1, sibling_situation="LANDED")
        self.assertFalse(state.done, "one read must not settle it")
        state, _ = step(state, ut=32.0, situation="LANDED",
                        sibling_present=1, sibling_situation="LANDED")
        self.assertTrue(state.done)
        self.assertEqual(mlib.GS1_LANDED, state.phase)
        self.assertEqual("LANDED", state.sibling_outcome)

    def test_a_faulted_enumeration_neither_advances_nor_erases_the_streak(self):
        # The roster-watch discipline: an UNREAD pair is evidence in NEITHER
        # direction, so it must not certify the wait and must not reset progress
        # toward it either.
        state = to_sibling_down()
        state, _ = step(state, ut=31.0, situation="LANDED",
                        sibling_present=1, sibling_situation="LANDED")
        self.assertEqual(1, state.sibling_landed_streak)
        state, _ = step(state, ut=32.0, situation="LANDED", sibling_present=-1)
        self.assertEqual(1, state.sibling_landed_streak,
                         "a faulted read erased progress")
        self.assertFalse(state.done)
        state, _ = step(state, ut=33.0, situation="LANDED",
                        sibling_present=1, sibling_situation="LANDED")
        self.assertTrue(state.done)

    def test_a_present_but_UNREADABLE_situation_holds_the_streak(self):
        # THE THIRD TRI-STATE READING, and the one easiest to get wrong. The shell
        # returns ("", 1) for "the vessel is there but its situation read faulted".
        # That is a PARTIAL FAULT, not an observation that the booster is still
        # flying, so it must HOLD the landed streak - the same "evidence in neither
        # direction" rule as the ("", -1) pair. An earlier cut tested
        # `situation in landedSituations` directly, so the empty string failed the
        # test and ERASED progress; found by Lane B restating the contract back.
        state = to_sibling_down()
        state, _ = step(state, ut=31.0, situation="LANDED",
                        sibling_present=1, sibling_situation="LANDED")
        self.assertEqual(1, state.sibling_landed_streak)
        state, _ = step(state, ut=32.0, situation="LANDED",
                        sibling_present=1, sibling_situation="")
        self.assertEqual(1, state.sibling_landed_streak,
                         "a present-but-unreadable frame erased progress")
        self.assertFalse(state.done)
        state, _ = step(state, ut=33.0, situation="LANDED",
                        sibling_present=1, sibling_situation="LANDED")
        self.assertTrue(state.done)
        self.assertEqual("LANDED", state.sibling_outcome)

    def test_a_REAL_non_landed_reading_does_erase_the_streak(self):
        # The other side of the same coin: a genuine FLYING/SUB_ORBITAL reading is
        # real evidence the booster is still up, so it MUST reset. Without this the
        # hold above would have quietly turned the debounce into "any two landed
        # reads ever", which is not a debounce.
        state = to_sibling_down()
        state, _ = step(state, ut=31.0, situation="LANDED",
                        sibling_present=1, sibling_situation="LANDED")
        self.assertEqual(1, state.sibling_landed_streak)
        state, _ = step(state, ut=32.0, situation="LANDED",
                        sibling_present=1, sibling_situation="SUB_ORBITAL")
        self.assertEqual(0, state.sibling_landed_streak)
        self.assertFalse(state.done)

    def test_presence_always_clears_the_absent_streak(self):
        # Absence and presence are mutually exclusive observations, so ANY present
        # reading - even an unreadable one - resets progress toward "destroyed".
        state = to_sibling_down()
        state, _ = step(state, ut=31.0, situation="LANDED", sibling_present=0)
        self.assertEqual(1, state.sibling_absent_streak)
        state, _ = step(state, ut=32.0, situation="LANDED",
                        sibling_present=1, sibling_situation="")
        self.assertEqual(0, state.sibling_absent_streak)
        self.assertFalse(state.done)

    def test_a_destroyed_booster_CONCLUDES_the_mission_rather_than_failing_it(self):
        # MISSION-vs-PARSEK ORTHOGONALITY. A booster that blew up still stopped
        # flying, so the mission's job is done: it stays MISSION-OK, the tail runs,
        # the tree commits, and the SPEC's log contracts red on the
        # CommittedProvisional promotion and the missing reap. Failing the row here
        # would make the run driver-INVALID and DISCARD the very evidence those
        # contracts read.
        state = to_sibling_down()
        for i in range(2):
            state, _ = step(state, ut=31.0 + i, situation="LANDED",
                            sibling_present=0)
        self.assertTrue(state.done)
        self.assertEqual(mlib.GS1_SIBLING_DESTROYED, state.sibling_outcome)
        rows = {r.name: r for r in mlib.evaluate_gs1_assertions(
            [snap(situation="LANDED")], mlib.gs1_params_from_dict(GS1_PARAMS),
            state=state)}
        self.assertTrue(rows["boosterConcluded"].met,
                        "a destroyed booster CONCLUDED; the spec reds, not the driver")
        self.assertEqual(mlib.GS1_SIBLING_DESTROYED,
                         rows["boosterConcluded"].value)

    def test_a_never_present_sibling_is_not_read_as_destroyed(self):
        # THE MISSPELLED-NAME GUARD. Absence only means "destroyed" once the vessel
        # has actually been seen; otherwise a typo in siblingVesselName would settle
        # the wait instantly and green.
        # NOT `to_descent`: that helper reports the sibling PRESENT on every frame
        # after the split, which is exactly the premise this cell has to deny.
        state = machine(siblingVesselName="Nonexistent Booster")
        for ut, alt, ap, vs, thr, sit in ((0.0, 70.0, 74.0, 0.0, LIT, "PRE_LAUNCH"),
                                          (2.0, 200.0, 300.0, 90.0, LIT, "FLYING"),
                                          (4.0, 500.0, 750.0, 60.0, LIT, "FLYING"),
                                          (5.0, 560.0, 780.0, 50.0, LIT, "FLYING"),
                                          (6.0, 600.0, 780.0, 40.0, LIT, "FLYING"),
                                          (11.0, 800.0, 780.0, -2.0, 0.0, "FLYING")):
            state, _ = step(state, ut=ut, altitude=alt, apoapsis=ap,
                            vertical_speed=vs, available_thrust=thr, situation=sit,
                            sibling_present=0)
        self.assertEqual(mlib.GS1_DESCENT, state.phase)
        state, _ = step(state, ut=30.0, altitude=71.0, vertical_speed=-5.0,
                        situation="LANDED", available_thrust=0.0,
                        craft_chute_state=mlib.CHUTE_STATE_DEPLOYED,
                        sibling_present=0)
        self.assertEqual(mlib.GS1_SIBLING_DOWN, state.phase)
        self.assertFalse(state.sibling_seen_present)
        for i in range(10):
            state, _ = step(state, ut=31.0 + i, situation="LANDED",
                            sibling_present=0)
            self.assertFalse(state.done, "a never-present sibling settled the wait")
        self.assertEqual(0, state.sibling_absent_streak)

    def test_the_wait_flakes_by_name_rather_than_hanging(self):
        state = to_sibling_down()
        state, _ = step(state, ut=10000.0, situation="LANDED",
                        sibling_present=1, sibling_situation="FLYING")
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertEqual(mlib.GS1_SIBLING_DOWN, state.flake_phase)

    def test_a_timeout_leaves_the_row_unmet_and_says_what_it_was_doing(self):
        state = to_sibling_down()
        state, _ = step(state, ut=10000.0, situation="LANDED",
                        sibling_present=1, sibling_situation="SUB_ORBITAL")
        rows = {r.name: r for r in mlib.evaluate_gs1_assertions(
            [snap(situation="LANDED")], mlib.gs1_params_from_dict(GS1_PARAMS),
            state=state)}
        row = rows["boosterConcluded"]
        self.assertFalse(row.met)
        self.assertTrue(row.detail["everObservedPresent"])
        self.assertEqual("SUB_ORBITAL", row.detail["lastSituation"],
                         "the row must name what the booster was doing - SUB_ORBITAL "
                         "is flight 3's exact failure and the reading that "
                         "distinguishes 'wait too short' from 'watch never resolved'")


class _FaultyVessel(object):
    """A kRPC vessel handle whose `.name` raises - the real failure mode when a
    vessel is destroyed or unloaded between the enumeration and the read."""

    def __init__(self, name=None, raises=False):
        self._name = name
        self._raises = raises

    @property
    def name(self):
        if self._raises:
            raise RuntimeError("RPC failed for this vessel")
        return self._name

    @property
    def situation(self):
        return type("S", (), {"name": "landed"})()


class _FakeSpaceCenter(object):
    def __init__(self, vessels):
        self.vessels = vessels


class SiblingReadFaultTests(unittest.TestCase):
    """`_read_sibling_situation`'s THREE-VALUED contract, at the one place it is
    easiest to collapse: a PER-VESSEL read fault.

    The first cut swept past a faulting handle with `continue` and, if the watched
    name was never matched, returned ("", 0) - the enumerated-and-ABSENT
    OBSERVATION. But the field that faulted IS the name, so the watched vessel
    cannot be ruled out: "not found" was unproven. Two such polls satisfy the
    absent-debounce, GS-1 concludes DESTROYED for a live booster, the mission ends
    early, the world-mutating tail is SKIPPED, and the spec reds on a missing reap -
    a driver fault misattributed as a product red. Found by the PR #1425 review."""

    def _control(self, vessels, name="GS1 Auto-Chute Booster Probe"):
        c = mission_runner.KrpcMissionControl()
        c._sibling_watch_name = name
        return c, _FakeSpaceCenter(vessels)

    def test_a_fault_while_the_watched_vessel_is_NOT_found_is_the_fault_sentinel(self):
        c, sc = self._control([_FaultyVessel(raises=True)])
        self.assertEqual(("", -1), c._read_sibling_situation(sc),
                         "a blind sweep that found nothing must NOT report absence")

    def test_a_fault_on_an_UNRELATED_vessel_still_yields_a_normal_observation(self):
        # THE OVER-CORRECTION GUARD. Treating any fault as blindness would make the
        # channel useless in a busy save: debris and asteroids fault routinely. Once
        # the watched vessel HAS been matched, an unrelated fault is irrelevant.
        c, sc = self._control([_FaultyVessel(raises=True),
                               _FaultyVessel(name="GS1 Auto-Chute Booster Probe")])
        self.assertEqual(("LANDED", 1), c._read_sibling_situation(sc))

    def test_a_CLEAN_sweep_that_finds_nothing_is_a_real_absence(self):
        # The other side: with no fault, "not found" IS an observation, and it is
        # what a genuinely destroyed booster reads.
        c, sc = self._control([_FaultyVessel(name="Some Other Craft")])
        self.assertEqual(("", 0), c._read_sibling_situation(sc))

    def test_an_unarmed_watch_never_enumerates(self):
        c, sc = self._control([_FaultyVessel(raises=True)], name="")
        self.assertEqual(("", -1), c._read_sibling_situation(sc))


class Gs1AssertionShapeTests(unittest.TestCase):

    def test_five_rows_in_a_stable_order(self):
        rows = mlib.evaluate_gs1_assertions(
            [], mlib.gs1_params_from_dict(GS1_PARAMS), state=machine())
        self.assertEqual(["apoapsisWindow", "separationObserved",
                          "stagedBelowCeiling", "craftCanopyObserved",
                          "landedSituation", "boosterConcluded"],
                         [r.name for r in rows])

    def test_the_apoapsis_row_gates_on_the_PEAK_not_a_passing_frame(self):
        # B1's semantics verbatim: a hop that passes THROUGH the window and peaks
        # above it is UNMET.
        frames = [snap(apoapsis=500.0), snap(apoapsis=9000.0), snap(apoapsis=8000.0)]
        rows = {r.name: r for r in mlib.evaluate_gs1_assertions(
            frames, mlib.gs1_params_from_dict(GS1_PARAMS), state=machine())}
        self.assertFalse(rows["apoapsisWindow"].met)
        self.assertEqual(9000.0, rows["apoapsisWindow"].value)

    def test_every_row_serializes_to_json_safe_values(self):
        rows = mlib.evaluate_gs1_assertions(
            [], mlib.gs1_params_from_dict(GS1_PARAMS), state=machine())
        for row in rows:
            d = row.to_dict()
            self.assertIn("name", d)
            self.assertIn("met", d)
            self.assertIsInstance(d["met"], bool)


# ---------------------------------------------------------------------------
# 2. The shell, end to end, with no krpc and no KSP.
# ---------------------------------------------------------------------------


def _nominal_frames():
    """A scripted nominal flight: ignite, climb, stage, canopy, pod down, BOOSTER
    down. The tail is what flight 3 lacked - the pod's touchdown is not the end."""
    frames = [snap(ut=0.0, altitude=70.0, situation="PRE_LAUNCH",
                   available_thrust=LIT)]
    frames.append(snap(ut=2.0, altitude=300.0, apoapsis=400.0, vertical_speed=90.0,
                       situation="FLYING", available_thrust=LIT))
    frames.append(snap(ut=4.0, altitude=560.0, apoapsis=760.0, vertical_speed=55.0,
                       situation="FLYING", available_thrust=LIT))
    for i in range(3):
        frames.append(snap(ut=5.0 + i, altitude=690.0 + 5 * i, apoapsis=770.0,
                           vertical_speed=8.0 - 4.0 * i, situation="FLYING",
                           available_thrust=LIT))
    for i in range(12):
        frames.append(snap(ut=9.0 + i, altitude=700.0 - 55.0 * i, apoapsis=740.0,
                           vertical_speed=-7.0, situation="FLYING",
                           available_thrust=0.0,
                           craft_chute_state=mlib.CHUTE_STATE_DEPLOYED,
                           sibling_present=1, sibling_situation="FLYING"))
    frames.append(snap(ut=30.0, altitude=71.0, vertical_speed=-5.0,
                       situation="LANDED", available_thrust=0.0,
                       craft_chute_state=mlib.CHUTE_STATE_DEPLOYED,
                       sibling_present=1, sibling_situation="FLYING"))
    # The booster is still under canopy when the pod is down; the mission waits.
    for i in range(6):
        frames.append(snap(ut=31.0 + i, situation="LANDED", available_thrust=0.0,
                           craft_chute_state=mlib.CHUTE_STATE_DEPLOYED,
                           sibling_present=1, sibling_situation="FLYING"))
    for i in range(3):
        frames.append(snap(ut=40.0 + i, situation="LANDED", available_thrust=0.0,
                           craft_chute_state=mlib.CHUTE_STATE_DEPLOYED,
                           sibling_present=1, sibling_situation="LANDED"))
    return frames


class Gs1ShellTests(unittest.TestCase):

    def test_a_nominal_flight_resolves_mission_ok_with_all_five_rows_met(self):
        control = FakeMissionControl(_nominal_frames())
        code, result = run(gs1_auto_chute_booster.SPEC, GS1_PARAMS, control)
        self.assertEqual(0, code, result)
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        rows = {r["name"]: r for r in result["assertions"]}
        self.assertEqual(6, len(rows))
        for name, row in rows.items():
            self.assertTrue(row["met"], "%s unmet: %r" % (name, row))

    def test_the_shell_reads_the_chute_channel(self):
        # read_chute=True is LOAD-BEARING, not diagnostic: craftCanopyObserved gates
        # on the OBSERVED ParachuteState and a control built without it would leave
        # the field empty on every frame, so the row could never be met.
        control = gs1_auto_chute_booster.make_control()
        self.assertTrue(control._read_chute)

    def test_the_shell_names_the_mission_the_spec_names(self):
        spec = _read(SPEC_PATH)
        self.assertEqual(gs1_auto_chute_booster.MISSION_NAME,
                         spec["driver"]["mission"])
        self.assertEqual(gs1_auto_chute_booster.MISSION_NAME,
                         gs1_auto_chute_booster.SPEC.name)


# ---------------------------------------------------------------------------
# 3. The craft. WIRES THE BUILDER'S `--check` INTO THE SUITE, the way
#    `FixtureDriftTests` does for career-pad-craft.
# ---------------------------------------------------------------------------


class CraftDriftTests(unittest.TestCase):
    """The craft is authored by construction and NOTHING else in the repo can tell
    whether it still says what the scenario needs. Unwired, `build_gs1_craft.py`
    would be prose with a shebang: a hand edit to the .craft, or a change to the
    derivation, would surface in a live forge flight instead of here."""

    @classmethod
    def setUpClass(cls):
        path = os.path.join(_HARNESS, "tools", "build_gs1_craft.py")
        spec = importlib.util.spec_from_file_location("build_gs1_craft", path)
        cls.builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.builder)

    def test_the_committed_craft_satisfies_every_post_condition(self):
        problems = self.builder.verify(self.builder.read_lines(
            self.builder.CRAFT_PATH))
        self.assertEqual([], problems)

    def test_the_committed_craft_is_byte_identical_to_a_fresh_rebuild(self):
        self.assertEqual(self.builder.read_lines(self.builder.CRAFT_PATH),
                         self.builder.build(),
                         "the committed craft has drifted from what "
                         "build_gs1_craft.py produces; re-run --write and commit, "
                         "or explain the divergence")

    def test_both_sides_of_the_decoupler_carry_command_authority(self):
        # THE CELL THIS WHOLE LANE RESTS ON. `SegmentBoundaryLogic.
        # IdentifyControllableChildren` filters by IsTrackableVessel (SpaceObject OR
        # ModuleCommand) and `IsMultiControllableSplit` needs >= 2. Drop the probe
        # core and the staging split authors NO RewindPoint, `TryAuthorRewindPoint-
        # ForBreakup` logs "Single-controllable split: no RP", and GS-1 becomes a
        # plain pad hop that asserts nothing while still looking green on most rows.
        text = "\n".join(self.builder.read_lines(self.builder.CRAFT_PATH))
        self.assertIn("mk1pod.v2_", text)
        self.assertIn("probeCoreOcto2.v2_", text)
        self.assertGreaterEqual(text.count("name = ModuleCommand"), 2)

    def test_the_booster_chutes_share_the_decouplers_stage(self):
        # THAT IS WHAT "AUTO-CHUTE BOOSTER" MEANS. One click fires the separator and
        # arms the canopies; split them across two stages and the mission's single
        # separation click leaves the booster falling unchuted, it is destroyed on
        # impact, its slot resolves `crashed` instead of `stableTerminal`, and the
        # RP does NOT reap - the exact opposite of what this scenario asserts.
        recs = self.builder.part_records(
            self.builder.read_lines(self.builder.CRAFT_PATH))
        by = {}
        for name, rec in recs:
            by.setdefault(name, []).append(int(rec["istg"]))
        self.assertEqual(sorted(set(by["Decoupler.1"])),
                         sorted(set(by["parachuteRadial"])))
        self.assertEqual(6, len(by["parachuteRadial"]))

    def test_the_stages_fire_in_the_order_the_mission_assumes(self):
        # KSP fires stages in DESCENDING istg order and the mission clicks exactly
        # twice: ignite, then separate. The upper chute must be strictly below both
        # so the second click cannot reach it.
        recs = self.builder.part_records(
            self.builder.read_lines(self.builder.CRAFT_PATH))
        by = {}
        for name, rec in recs:
            by.setdefault(name, []).append(int(rec["istg"]))
        self.assertGreater(by["liquidEngine2"][0], by["Decoupler.1"][0])
        self.assertGreater(by["Decoupler.1"][0], by["parachuteSingle"][0])

    def test_the_root_is_the_upper_stage(self):
        # KSP takes the FIRST PART as the root, and the mission flies the upper
        # stage: a root on the booster side would leave focus with the discarded
        # half at separation.
        recs = self.builder.part_records(
            self.builder.read_lines(self.builder.CRAFT_PATH))
        self.assertEqual("mk1pod.v2", recs[0][0])


# ---------------------------------------------------------------------------
# 4. Spec / schema / registry / forge sync.
# ---------------------------------------------------------------------------


def _read(path):
    with open(path, "rb") as fh:
        return tomllib.load(fh)


class SpecSyncTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.spec = _read(SPEC_PATH)
        cls.forge = _read(FORGE_SPEC_PATH)
        cls.schema = _read(SCHEMA_PATH)
        cls.registry = _read(REGISTRY_PATH)

    def test_every_required_schema_param_is_declared_by_the_spec(self):
        declared = self.spec["driver"]["missionParams"]
        for name, facets in self.schema["params"].items():
            if facets.get("required"):
                self.assertIn(name, declared,
                              "the schema requires %s and the spec omits it" % name)

    def test_the_test_fixture_params_match_the_committed_spec(self):
        # The fake-flight cells above are only evidence about the LIVE profile if
        # they exercise the live values. This is `test_shells.py`'s
        # MissionParamsMatchTheSpecs contract, applied to this lane.
        declared = self.spec["driver"]["missionParams"]
        for key, value in GS1_PARAMS.items():
            self.assertIn(key, declared)
            self.assertEqual(value, declared[key],
                             "GS1_PARAMS[%r] has drifted from the spec" % key)
        self.assertEqual(sorted(GS1_PARAMS), sorted(declared))

    def test_the_staging_ceiling_is_not_below_the_steering_target(self):
        # A ceiling under the target would make stagedBelowCeiling a permanent red
        # no matter what Parsek did - a gate that condemns unconditionally is not a
        # gate. (The ceiling is deliberately ABOVE the target to absorb a poll's
        # worth of overshoot.)
        p = self.spec["driver"]["missionParams"]
        self.assertGreaterEqual(p["stagingMaxAltMeters"], p["stagingApoapsisMeters"])

    def test_the_apoapsis_window_contains_the_steering_target(self):
        p = self.spec["driver"]["missionParams"]
        window = p["apoapsisWindowMeters"]
        self.assertLessEqual(window["min"], p["stagingApoapsisMeters"])
        self.assertLessEqual(p["stagingApoapsisMeters"], window["max"])

    def test_every_dimension_claim_exists_in_the_registry(self):
        for dim, values in self.spec["dimensionsCovered"].items():
            self.assertIn(dim, self.registry)
            for value in values:
                self.assertIn(value, self.registry[dim]["values"],
                              "%s claims unknown %s value %r" % (SPEC_PATH, dim, value))

    def test_the_rewind_block_is_ARMED_and_its_windows_are_unrelaxed(self):
        # ARMED 2026-08-05 after the reading run `2026-08-05_0824` (flight 4, PASS
        # attempt 1) measured rewindPoints=0 supersedeRows=0 tombstones=0 with every
        # window already met, so arming moved no verdict. This cell replaced an
        # earlier `..._declared_but_not_armed` one, which was correct only while the
        # arming was pending.
        #
        # WHAT IT GUARDS NOW is the thing that can still go wrong: the windows are
        # the ASSERTION (all `max = 0`), and three flights red on them for three
        # different DRIVER defects without a single product defect. A future run that
        # goes red here must be fixed at the driver or the craft, never by widening
        # `rewindPoints` to `{min = 1}` - that would turn the guard into a
        # description of whatever happened to fly.
        rewind = self.spec["expectations"]["rewind"]
        self.assertIs(True, rewind.get("gating"))
        # IF THIS CELL FIRES WITH `supersedeRows == {"min": 1}`, THE NEGATIVE CONTROL
        # WAS NOT REVERTED. That value is the deliberate temporary edit the arming
        # workflow flies to watch the gate FAIL (a gate nobody has seen fail is an
        # assumption), and the workflow's last step is to put it back. GS-1 drives no
        # rewind, so a supersede row cannot legitimately exist here and `{min: 1}` is
        # never a shippable pin - revert to `{max: 0}` rather than teaching this cell
        # to accept it.
        self.assertEqual({"max": 0}, rewind["rewindPoints"])
        self.assertEqual({"max": 0}, rewind["supersedeRows"],
                         "supersedeRows is not {max: 0} - if it reads {min: 1} this "
                         "is the un-reverted arming negative control, not a new pin")
        self.assertEqual({"max": 0}, rewind["tombstones"])
        # The structure block stays REPORT-ONLY: gating is PER BLOCK, and its windows
        # are single-sample readings rather than measurements worth gating.
        structure = self.spec["expectations"]["recordings"]["structure"]
        self.assertNotIn("gating", structure)

    def test_arming_the_rewind_block_is_paired_with_the_allowlist_entry(self):
        # THE PAIR MUST NOT DRIFT APART. `test_hlib.py::SaveStructureVerifierWiringTests`
        # reds when the set of gating-armed specs changes without an explicit edit
        # citing the run ids; this cell states the same coupling from the lane's own
        # side, so removing either half is caught from both directions.
        allowlist = os.path.join(_HARNESS, "lib", "test_hlib.py")
        with open(allowlist, encoding="utf-8") as fh:
            text = fh.read()
        armed = bool(self.spec["expectations"]["rewind"].get("gating"))
        listed = '"GS-1-auto-chute-booster.toml"' in text
        self.assertEqual(armed, listed,
                         "GS-1 arms save-structure gating but is not in "
                         "test_hlib's ARMED_ALLOWLIST (or vice versa) - arming is a "
                         "per-scenario operator decision and the allowlist entry is "
                         "the record of it")

    def test_the_unfinished_flight_promotion_lines_are_forbidden(self):
        # THE REGRESSION GUARD ITSELF. These two are the exact lines
        # `ApplyRewindProvisionalMergeStates` emits when it demotes a recording to
        # CommittedProvisional, i.e. when an Unfinished Flight is opened. A routine
        # two-stage flight whose stages both LAND must open none, and only the
        # forbidden list can express "this must not appear".
        forbidden = self.spec["expectations"]["logContracts"]["forbidden"]
        self.assertIn("CommitTree promoted rec=", forbidden)
        self.assertIn("CommitTree promoted chain-tip rec=", forbidden)

    def test_the_reap_tokens_are_required_and_non_vacuous(self):
        required = self.spec["expectations"]["logContracts"]["required"]
        self.assertIn("Reaped rp=\\S+ bp=\\S+ slots=2", required)
        reaped = [p for p in required if p.startswith("ReapOrphanedRPs:")]
        self.assertEqual(1, len(reaped))
        self.assertIn("[1-9]", reaped[0],
                      "a count that can legitimately be 0 must be pinned non-zero; "
                      "`reaped=0` is what a no-op sweep emits")
        self.assertIn("remaining=0", reaped[0],
                      "remaining=0 is the LEAK assertion and is the whole point")

    def test_the_multi_controllable_token_pins_two_pids(self):
        # `Controllable split children: [a]` is emitted for a SINGLE-controllable
        # split, which is exactly the case where no RewindPoint is authored. The
        # two-pid form is the difference between "the classifier ran" and "the
        # classifier saw two commanders".
        required = self.spec["expectations"]["logContracts"]["required"]
        self.assertIn("Controllable split children: \\[[0-9]+,[0-9]+\\]", required)

    def test_the_exit_route_pins_auto_merge_and_a_live_tree(self):
        # autoMerge=true is MANDATORY: without it
        # SceneExitInterceptor.ShouldShowDialogBeforeSceneChange returns
        # RegularMerge and the exit verb REJECTS with msg=dialog-required, so the
        # run proves nothing. hasActiveTree=true is what distinguishes this route
        # from CL-2's stashed-pending-tree one.
        steps = self.spec["driver"]["steps"]
        settings = {s.get("args", {}).get("name"): s.get("args", {}).get("value")
                    for s in steps if s.get("cmd") == "SetSetting"}
        self.assertEqual("true", settings.get("autoMerge"))
        self.assertEqual("true", settings.get("autoRecordOnLaunch"))
        self.assertTrue(any(s.get("cmd") == "ExitToSpaceCenter" for s in steps))
        required = self.spec["expectations"]["logContracts"]["required"]
        self.assertTrue(any("autoMerge=true hasActiveTree=true" in p
                            for p in required))

    def test_no_commit_tree_step_is_driven(self):
        # A seam CommitTree WOULD succeed here (the vessel lands, so the recorder is
        # live) and would still be wrong: the reaper only runs from
        # ParsekScenario.OnLoad, and a CommitTree + FlushAndQuit tail never leaves
        # FLIGHT. The RP would be on disk at quit, indistinguishable from the leak
        # this spec exists to detect.
        cmds = [s.get("cmd") for s in self.spec["driver"]["steps"]]
        self.assertNotIn("CommitTree", cmds)

    def test_the_forge_launches_the_craft_the_builder_writes(self):
        path = os.path.join(_HARNESS, "tools", "build_gs1_craft.py")
        spec = importlib.util.spec_from_file_location("build_gs1_craft_sync", path)
        builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(builder)
        self.assertEqual(builder.SHIP_NAME,
                         self.forge["driver"]["missionParams"]["craftName"])
        self.assertEqual("fixtures/saves/%s" % builder.BASE_NAME,
                         self.forge["fixture"]["saveTemplate"])
        self.assertTrue(os.path.isfile(builder.CRAFT_PATH),
                        "the forge base must actually carry the craft it launches; "
                        "kRPC launch_vessel resolves "
                        "<save>/Ships/VAB/<craftName>.craft")

    def test_the_forge_seeds_the_crew_the_pod_needs(self):
        # mk1pod.v2's ModuleCommand carries minimumCrew = 1, so an EMPTY pod is an
        # UNCONTROLLED upper stage: kRPC could read it, but throttle and staging
        # would do nothing and GS-1 would burn its whole budget on the pad.
        crew = self.forge["driver"]["missionParams"].get("crewNames") or []
        self.assertEqual(1, len(crew))

    def test_the_spec_points_at_the_fixture_the_forge_produces(self):
        self.assertEqual("fixtures/saves/gs1-two-stage-pad",
                         self.spec["fixture"]["saveTemplate"])
        self.assertEqual("none", self.spec["fixture"]["injectedRecordings"],
                         "the RewindPoint under test is authored by KSP physics "
                         "during the flight, NOT injected - that is the whole "
                         "point of this lane and the one thing every other rewind "
                         "spec does differently")


if __name__ == "__main__":
    unittest.main()
