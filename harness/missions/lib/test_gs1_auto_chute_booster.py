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
    "apoapsisWindowMeters": {"min": 200.0, "max": 2000.0},
    "stageSettleFrames": 3,
    "chuteArmMaxRateMps": 30.0,
    "chuteFullDeployAltMeters": 1000.0,
    "landedSituations": ["LANDED", "SPLASHED"],
    "ascentTimeoutSeconds": 90.0,
    "stageTimeoutSeconds": 60.0,
    "descentTimeoutSeconds": 300.0,
}


def params(**over):
    out = dict(GS1_PARAMS)
    out.update(over)
    return out


def machine(**over):
    return mlib.gs1_initial_state(mlib.gs1_params_from_dict(params(**over)))


def step(state, **kw):
    kw.setdefault("available_thrust", LIT)
    return mlib.gs1_decide(state, snap(**kw))


def to_stage(**over):
    """Drive PRELAUNCH -> ASCENT -> the frame the separation is emitted on.
    Returns (state, actions_of_that_frame)."""
    state = machine(**over)
    state, _ = step(state, ut=0.0, altitude=70.0, situation="PRE_LAUNCH")
    # Climb until the apoapsis target trips the throttle cut.
    state, _ = step(state, ut=2.0, altitude=200.0, apoapsis=300.0,
                    vertical_speed=90.0, situation="FLYING")
    state, _ = step(state, ut=4.0, altitude=500.0, apoapsis=750.0,
                    vertical_speed=60.0, situation="FLYING")
    assert state.phase == mlib.GS1_STAGE, state.phase
    # stageSettleFrames frames in STAGE; the last one emits the separation.
    actions = []
    for i in range(int(params(**over)["stageSettleFrames"])):
        state, actions = step(state, ut=5.0 + i, altitude=700.0 + i, apoapsis=780.0,
                              vertical_speed=10.0 - 4.0 * i, situation="FLYING")
    return state, actions


# ---------------------------------------------------------------------------
# 1. The pure machine.
# ---------------------------------------------------------------------------


class Gs1PhaseSequenceTests(unittest.TestCase):

    def test_prelaunch_sets_throttle_and_ignites_on_the_first_frame(self):
        state, actions = step(machine(), ut=0.0, altitude=70.0,
                              situation="PRE_LAUNCH")
        self.assertEqual([mlib.ACTION_SET_THROTTLE, mlib.ACTION_ACTIVATE_STAGE],
                         [a.kind for a in actions])
        self.assertEqual(1.0, actions[0].value)
        self.assertEqual(mlib.GS1_ASCENT, state.phase)

    def test_ascent_cuts_throttle_at_the_apoapsis_target_and_stages_nothing_yet(self):
        # LOAD-BEARING SEPARATION OF CONCERNS: the cut and the separation must not
        # ride the same frame. LV-T45 thrust decays over a few physics frames after
        # a throttle-to-zero, and firing the decoupler while the lower stage is
        # still pushing drives the spent booster INTO the upper stage.
        state = machine()
        state, _ = step(state, ut=0.0, altitude=70.0, situation="PRE_LAUNCH")
        state, actions = step(state, ut=3.0, altitude=400.0, apoapsis=705.0,
                              vertical_speed=70.0, situation="FLYING")
        self.assertEqual([mlib.ACTION_CUT_THROTTLE], [a.kind for a in actions])
        self.assertEqual(mlib.GS1_STAGE, state.phase)

    def test_ascent_also_leaves_on_a_negative_vertical_speed(self):
        # The apoapsis target can be overshot or undershot between polls (a burn
        # that flamed out early, a poll stall across the apex). Falling is the
        # fallback exit so the machine can never sit in ASCENT past the apogee
        # waiting for a target it will now never reach.
        state = machine(stagingApoapsisMeters=50000.0)
        state, _ = step(state, ut=0.0, altitude=70.0, situation="PRE_LAUNCH")
        state, actions = step(state, ut=9.0, altitude=800.0, apoapsis=810.0,
                              vertical_speed=-1.0, situation="FLYING")
        self.assertEqual([mlib.ACTION_CUT_THROTTLE], [a.kind for a in actions])
        self.assertEqual(mlib.GS1_STAGE, state.phase)

    def test_the_separation_waits_out_stage_settle_frames(self):
        state = machine()
        state, _ = step(state, ut=0.0, altitude=70.0, situation="PRE_LAUNCH")
        state, _ = step(state, ut=3.0, altitude=400.0, apoapsis=705.0,
                        vertical_speed=70.0, situation="FLYING")
        emitted = []
        for i in range(3):
            state, actions = step(state, ut=4.0 + i, altitude=700.0,
                                  apoapsis=760.0, vertical_speed=5.0,
                                  situation="FLYING")
            emitted.append([a.kind for a in actions])
        # Frames 1 and 2 hold; frame 3 stages (and, being already slow, arms the
        # upper chute on the same frame by falling through into DESCENT).
        self.assertEqual([], emitted[0])
        self.assertEqual([], emitted[1])
        self.assertIn(mlib.ACTION_ACTIVATE_STAGE, emitted[2])
        self.assertEqual(mlib.GS1_DESCENT, state.phase)

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
    """separationObserved is an OBSERVED latch. B1 shipped four months of green
    nightlies on a chute that never opened because its terminal read a COMMANDED
    latch; these cells are what stop the same class of lie about the split."""

    def test_a_stage_click_that_fired_nothing_does_not_satisfy_the_row(self):
        # The decoupler did not blow (a mis-numbered istg, a decoupler with no
        # ModuleDecouple in its stage): the upper stage still carries the engine,
        # so AvailableThrust never falls. The machine COMMANDED the stage, and the
        # row must still be UNMET.
        state, _ = to_stage()
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
        state, _ = to_stage()
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
        state, _ = to_stage()
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
        state, _ = to_stage()
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
        state, _ = to_stage()
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
        state, actions = to_stage()
        kinds = [a.kind for a in actions]
        self.assertIn(mlib.ACTION_SET_CHUTE_DEPLOY_ALTITUDE, kinds)
        self.assertIn(mlib.ACTION_DEPLOY_CHUTE, kinds)
        self.assertLess(kinds.index(mlib.ACTION_SET_CHUTE_DEPLOY_ALTITUDE),
                        kinds.index(mlib.ACTION_DEPLOY_CHUTE),
                        "the deployAltitude write must precede the arm on the SAME "
                        "frame, so the module's first ACTIVE FixedUpdate already "
                        "sees the raised altitude")

    def test_a_commanded_but_never_open_canopy_is_unmet(self):
        state, _ = to_stage()
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
        state, _ = to_stage()
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

    def test_a_landed_situation_ends_the_flight(self):
        state, _ = to_stage()
        state, _ = step(state, ut=40.0, altitude=72.0, vertical_speed=-5.0,
                        situation="LANDED", available_thrust=0.0)
        self.assertEqual(mlib.GS1_LANDED, state.phase)
        self.assertTrue(state.done)
        self.assertIsNone(state.verdict,
                          "LANDED leaves the verdict to the assertions, exactly as "
                          "B1's terminal does")

    def test_a_vessel_loss_is_a_deterministic_assert_fail_not_a_success(self):
        # DELIBERATELY UNLIKE B1: there is no success-by-destruction terminal here.
        # This scenario's whole subject is a ROUTINE flight that must NOT flood
        # Unfinished Flights, so a destroyed active vessel is a different scenario,
        # not a pass - and the reason NAMES the canopy state and the split.
        state, _ = to_stage()
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
        state, _ = to_stage()
        state, _ = step(state, ut=40.0, altitude=72.0, vertical_speed=-5.0,
                        situation="LANDED", available_thrust=0.0)
        again, actions = step(state, ut=41.0, situation="LANDED")
        self.assertEqual(state, again)
        self.assertEqual([], actions)


class Gs1AssertionShapeTests(unittest.TestCase):

    def test_five_rows_in_a_stable_order(self):
        rows = mlib.evaluate_gs1_assertions(
            [], mlib.gs1_params_from_dict(GS1_PARAMS), state=machine())
        self.assertEqual(["apoapsisWindow", "separationObserved",
                          "stagedBelowCeiling", "craftCanopyObserved",
                          "landedSituation"], [r.name for r in rows])

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
    """A scripted nominal flight: ignite, climb, stage, canopy, land."""
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
                           craft_chute_state=mlib.CHUTE_STATE_DEPLOYED))
    frames.append(snap(ut=30.0, altitude=71.0, vertical_speed=-5.0,
                       situation="LANDED", available_thrust=0.0,
                       craft_chute_state=mlib.CHUTE_STATE_DEPLOYED))
    return frames


class Gs1ShellTests(unittest.TestCase):

    def test_a_nominal_flight_resolves_mission_ok_with_all_five_rows_met(self):
        control = FakeMissionControl(_nominal_frames())
        code, result = run(gs1_auto_chute_booster.SPEC, GS1_PARAMS, control)
        self.assertEqual(0, code, result)
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        rows = {r["name"]: r for r in result["assertions"]}
        self.assertEqual(5, len(rows))
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

    def test_the_rewind_block_is_declared_but_not_armed(self):
        # ARMING IS A PER-SCENARIO OPERATOR DECISION taken only after a report-only
        # reading run whose facets match. A spec that quietly grows `gating = true`
        # also reds in test_hlib's ARMED_ALLOWLIST; this cell states the same
        # property from the lane's own side, with the reason attached.
        rewind = self.spec["expectations"]["rewind"]
        self.assertNotIn("gating", rewind)
        self.assertEqual({"max": 0}, rewind["rewindPoints"])
        self.assertEqual({"max": 0}, rewind["supersedeRows"])
        self.assertEqual({"max": 0}, rewind["tombstones"])
        structure = self.spec["expectations"]["recordings"]["structure"]
        self.assertNotIn("gating", structure)

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
