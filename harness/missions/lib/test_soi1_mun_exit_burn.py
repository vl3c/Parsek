"""Unit cells for the SOI-1 lane (mission `soi1_mun_exit_burn`, scenario
`SOI-1-mun-exit-physics-burn`).

Guards the machine's ways of being silently wrong:
  1. igniting before the AP points prograde, or under a scaled physics step;
  2. crossing the boundary on rails or unpowered while still reporting success;
  3. a rails / physics warp state permitted anywhere near the straddle burn;
  4. a schema / params / shell / spec drift (every param the machine reads must be a
     key the schema declares, derived by an AST walk, not a hand-copied list);
  5. the spec's log tokens drifting from the C# literals they match.

NO krpc, NO KSP, NO network.
"""

import ast
import os
import re
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
_HARNESS = os.path.dirname(_MISSIONS)
_REPO = os.path.dirname(_HARNESS)
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mlib  # noqa: E402
import soi1_mun_exit_burn as shell  # noqa: E402
from test_shells import FakeMissionControl, run  # noqa: E402

SCHEMA = os.path.join(_MISSIONS, "soi1_mun_exit_burn.schema.toml")
SHELL = os.path.join(_MISSIONS, "soi1_mun_exit_burn.py")
SPEC = os.path.join(_HARNESS, "scenarios", "SOI-1-mun-exit-physics-burn.toml")
PHYSICS_SOI_CS = os.path.join(_REPO, "Source", "Parsek", "FlightRecorder.PhysicsSoi.cs")
RECORDER_CS = os.path.join(_REPO, "Source", "Parsek", "FlightRecorder.cs")

NAN = float("nan")


def params(**kw):
    return mlib.soi1_params_from_dict(kw)


def snap(**kw):
    base = dict(ut=1000.0, body="Mun", situation="ORBITING", eccentricity=0.0001,
                periapsis=139526.0, apoapsis=139612.0, crew_count=1, ap_error=2.0,
                warp_mode="NONE", warp_rate=1.0, available_thrust=0.0, throttle=0.0,
                liquid_fuel=592.9)
    base.update(kw)
    return mlib.TelemetrySnapshot(**base)


def kinds(actions):
    return [a.kind for a in actions]


def step(st, s):
    return mlib.soi1_decide(st, s)


def to_escape(p=None):
    """START -> ALIGN -> ESCAPE-BURN at 1x with the AP aligned."""
    st = mlib.soi1_initial_state(p or params())
    st, _ = step(st, snap(ut=1000.0))
    for i in range(st.params.align_debounce_frames):
        st, acts = step(st, snap(ut=1001.0 + i))
    assert st.phase == mlib.SOI1_ESCAPE, st.phase
    return st


def to_coast(p=None):
    st = to_escape(p)
    st, _ = step(st, snap(ut=1010.0, available_thrust=125000.0, throttle=0.5,
                          eccentricity=0.6, time_to_soi=NAN))
    st, acts = step(st, snap(ut=1030.0, available_thrust=125000.0, throttle=0.5,
                             eccentricity=1.21, time_to_soi=6780.0, next_body="Kerbin"))
    assert st.phase == mlib.SOI1_COAST, st.phase
    return st


def to_approach(p=None):
    st = to_coast(p)
    soi_ut = 1030.0 + 6780.0
    st, acts = step(st, snap(ut=soi_ut - 150.0, time_to_soi=150.0, warp_mode="RAILS",
                             warp_rate=10.0))
    assert st.phase == mlib.SOI1_APPROACH, st.phase
    return st


def to_straddle(p=None):
    st = to_approach(p)
    st, acts = step(st, snap(ut=7770.0, time_to_soi=40.0))
    assert st.phase == mlib.SOI1_STRADDLE, st.phase
    return st


def burning(**kw):
    base = dict(throttle=0.1, available_thrust=25000.0)
    base.update(kw)
    return snap(**base)


class HandoffTests(unittest.TestCase):
    def test_the_park_is_accepted_and_points_prograde_under_the_slew_warp(self):
        st = mlib.soi1_initial_state(params())
        st, acts = step(st, snap())
        self.assertEqual(mlib.SOI1_ALIGN, st.phase)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_AP_POINT_PROGRADE,
                          mlib.ACTION_SET_PHYSICS_WARP], kinds(acts))
        self.assertEqual(1.0, acts[2].value)

    def test_index_zero_runs_the_slew_at_1x(self):
        st = mlib.soi1_initial_state(params(alignPhysicsWarpIndex=0))
        st, acts = step(st, snap())
        self.assertNotIn(mlib.ACTION_SET_PHYSICS_WARP, kinds(acts))

    def test_each_refusal_is_named(self):
        cases = [
            (dict(body="Kerbin"), "not at Mun"),
            (dict(body=""), "not at Mun"),
            (dict(situation="SUB_ORBITAL"), "not ORBITING"),
            (dict(eccentricity=0.3), "not near-circular"),
            (dict(eccentricity=NAN), "not near-circular"),
            (dict(periapsis=5000.0), "periapsis"),
            (dict(crew_count=-1), "crew"),
        ]
        for kw, needle in cases:
            st = mlib.soi1_initial_state(params(startMinPeriapsisMeters=100000))
            st, acts = step(st, snap(**kw))
            self.assertEqual([], acts, kw)
            self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict, kw)
            self.assertIn(needle, st.loss_reason, kw)

    def test_vessel_lost_is_assert_fail_in_any_phase(self):
        st = to_coast()
        st, acts = step(st, mlib.TelemetrySnapshot(vessel_lost=True))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("vessel-lost", st.loss_reason)


class AlignTests(unittest.TestCase):
    def _aligned_under_warp(self):
        st = mlib.soi1_initial_state(params())
        st, _ = step(st, snap())
        for i in range(3):
            st, acts = step(st, snap(ut=1001.0 + i, warp_mode="PHYSICS", warp_rate=2.0))
        return st, acts

    def test_alignment_needs_consecutive_frames(self):
        st = mlib.soi1_initial_state(params())
        st, _ = step(st, snap())
        st, _ = step(st, snap(ut=1001.0))
        st, _ = step(st, snap(ut=1002.0, ap_error=40.0))
        st, _ = step(st, snap(ut=1003.0))
        st, _ = step(st, snap(ut=1004.0, ap_error=NAN))
        self.assertEqual(mlib.SOI1_ALIGN, st.phase)

    def test_the_slew_warp_drops_to_1x_before_any_throttle_up(self):
        st, acts = self._aligned_under_warp()
        self.assertEqual([mlib.ACTION_SET_PHYSICS_WARP], kinds(acts))
        self.assertEqual(0.0, acts[0].value)
        self.assertEqual(mlib.SOI1_ALIGN, st.phase)
        # Still warped: no second drop, no ignition.
        st, acts = step(st, snap(ut=1004.0, warp_mode="PHYSICS", warp_rate=2.0))
        self.assertEqual([], acts)
        st, acts = step(st, snap(ut=1005.0))
        self.assertEqual([mlib.ACTION_SET_THROTTLE], kinds(acts))
        self.assertEqual(0.5, acts[0].value)
        self.assertEqual(mlib.SOI1_ESCAPE, st.phase)

    def test_an_align_that_never_converges_flakes_by_name(self):
        st = mlib.soi1_initial_state(params(alignTimeoutSeconds=60))
        st, _ = step(st, snap(ut=1000.0))
        st, acts = step(st, snap(ut=1061.0, ap_error=90.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.SOI1_ALIGN, st.flake_phase)
        self.assertIn("did not converge", st.flake_reason)
        self.assertIn(mlib.ACTION_CANCEL_WARP, kinds(acts))


class EscapeTests(unittest.TestCase):
    def test_cut_needs_the_target_eccentricity_and_an_soi_change(self):
        st = to_escape()
        st, acts = step(st, snap(ut=1020.0, available_thrust=125000.0,
                                 eccentricity=1.25, time_to_soi=NAN))
        self.assertEqual(mlib.SOI1_ESCAPE, st.phase)
        st, acts = step(st, snap(ut=1021.0, available_thrust=125000.0,
                                 eccentricity=1.19, time_to_soi=7000.0))
        self.assertEqual(mlib.SOI1_ESCAPE, st.phase)
        st, acts = step(st, snap(ut=1022.0, available_thrust=125000.0,
                                 eccentricity=1.2, time_to_soi=6800.0,
                                 next_body="Kerbin"))
        self.assertEqual(mlib.SOI1_COAST, st.phase)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE], kinds(acts))
        self.assertEqual(1.2, st.escape_cut_eccentricity)
        self.assertEqual("Kerbin", st.escape_cut_next_body)

    def test_a_falling_periapsis_ends_the_attempt(self):
        st = to_escape(params(escapeMinPeriapsisMeters=100000))
        st, acts = step(st, snap(ut=1010.0, available_thrust=125000.0,
                                 periapsis=90000.0, eccentricity=0.1))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("not pointing prograde", st.loss_reason)
        self.assertIn(mlib.ACTION_CUT_THROTTLE, kinds(acts))

    def test_unlit_and_dry_engines_fail_by_name(self):
        st = to_escape(params(igniteFrames=2))
        for i in range(2):
            st, _ = step(st, snap(ut=1010.0 + i))
        self.assertIn("never lit", st.loss_reason)
        st = to_escape()
        st, _ = step(st, snap(ut=1010.0, available_thrust=125000.0, eccentricity=0.4))
        st, _ = step(st, snap(ut=1011.0, available_thrust=0.0, eccentricity=0.5))
        self.assertIn("propellant exhausted", st.loss_reason)

    def test_an_escape_toward_another_body_fails(self):
        st = to_escape()
        st, _ = step(st, snap(ut=1010.0, available_thrust=125000.0, eccentricity=1.3,
                              time_to_soi=6000.0, next_body="Minmus"))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("Minmus", st.loss_reason)


class CoastTests(unittest.TestCase):
    def test_the_warp_targets_the_soi_exit_minus_the_lead_once(self):
        st = to_coast()
        st, acts = step(st, snap(ut=1031.0, time_to_soi=6779.0))
        self.assertEqual([mlib.ACTION_WARP_TO_UT], kinds(acts))
        self.assertAlmostEqual(1031.0 + 6779.0 - 150.0, acts[0].value)
        # A running native warp is left alone.
        st, acts = step(st, snap(ut=2000.0, time_to_soi=5810.0, warp_mode="RAILS",
                                 warp_rate=1000.0, warping_to=7660.0))
        self.assertEqual([], acts)

    def test_an_idle_warp_is_reissued_a_bounded_number_of_times(self):
        st = to_coast(params(maxWarpIssues=2))
        issued = 0
        for i in range(40):
            st, acts = step(st, snap(ut=1031.0 + i, time_to_soi=6779.0 - i))
            issued += kinds(acts).count(mlib.ACTION_WARP_TO_UT)
            if st.done:
                break
        self.assertEqual(2, issued)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("went idle", st.flake_reason)

    def test_arrival_cancels_the_warp_and_repoints(self):
        st = to_coast()
        st, acts = step(st, snap(ut=7655.0, time_to_soi=155.0, warp_mode="PHYSICS",
                                 warp_rate=2.0))
        self.assertEqual(mlib.SOI1_APPROACH, st.phase)
        self.assertEqual([mlib.ACTION_CANCEL_WARP, mlib.ACTION_AP_POINT_PROGRADE],
                         kinds(acts))

    def test_an_soi_change_during_the_coast_is_the_on_rails_path_and_fails(self):
        st = to_coast()
        st, acts = step(st, snap(ut=8000.0, body="Kerbin", time_to_soi=NAN,
                                 warp_mode="RAILS", warp_rate=1000.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("on rails", st.loss_reason)
        self.assertIn(mlib.ACTION_CANCEL_WARP, kinds(acts))

    def test_a_lost_escape_fails(self):
        st = to_coast()
        st, _ = step(st, snap(ut=1031.0, time_to_soi=NAN))
        self.assertIn("no SOI change", st.loss_reason)


class ApproachAndStraddleTests(unittest.TestCase):
    def test_ignition_waits_for_1x_and_the_lead_not_for_alignment(self):
        st = to_approach()
        st, acts = step(st, snap(ut=7700.0, time_to_soi=35.0, warp_mode="RAILS",
                                 warp_rate=5.0, ap_error=45.0))
        self.assertEqual([mlib.ACTION_CANCEL_WARP], kinds(acts))
        self.assertEqual(mlib.SOI1_APPROACH, st.phase)
        st, acts = step(st, snap(ut=7701.0, time_to_soi=60.0, ap_error=45.0))
        self.assertEqual([], acts)
        st, acts = step(st, snap(ut=7725.0, time_to_soi=39.0, ap_error=45.0))
        self.assertEqual([mlib.ACTION_SET_THROTTLE], kinds(acts))
        self.assertEqual(0.1, acts[0].value)
        self.assertEqual(45.0, st.ignite_ap_error)

    def test_a_crossing_before_ignition_fails_as_unpowered(self):
        st = to_approach()
        st, _ = step(st, snap(ut=7700.0, body="Kerbin", time_to_soi=NAN))
        self.assertIn("unpowered", st.loss_reason)

    def test_the_straddle_stamps_both_sides_and_ends_after_the_trail(self):
        st = to_straddle()
        st, _ = step(st, burning(ut=7790.0, time_to_soi=20.0))
        st, _ = step(st, burning(ut=7809.5, time_to_soi=0.5))
        self.assertEqual(7809.5, st.last_home_ut)
        st, _ = step(st, burning(ut=7810.0, body="Kerbin", time_to_soi=NAN))
        self.assertEqual(7810.0, st.cross_ut)
        st, acts = step(st, burning(ut=7830.0, body="Kerbin"))
        self.assertEqual(mlib.SOI1_STRADDLE, st.phase)
        st, acts = step(st, burning(ut=7840.0, body="Kerbin"))
        self.assertEqual(mlib.SOI1_DONE, st.phase)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_AP_DISENGAGE], kinds(acts))
        rows = {r.name: r for r in mlib.evaluate_soi1_assertions([], st.params, st)}
        self.assertTrue(all(r.met for r in rows.values()),
                        [(r.name, r.detail) for r in rows.values() if not r.met])

    def test_a_warped_straddle_frame_is_counted_and_unmeets_the_crossing(self):
        st = to_straddle()
        st, acts = step(st, burning(ut=7790.0, time_to_soi=20.0, warp_mode="PHYSICS",
                                    warp_rate=2.0))
        self.assertEqual([mlib.ACTION_CANCEL_WARP], kinds(acts))
        st, _ = step(st, burning(ut=7809.0, time_to_soi=1.0))
        st, _ = step(st, burning(ut=7810.0, body="Kerbin"))
        st, _ = step(st, burning(ut=7841.0, body="Kerbin"))
        rows = {r.name: r for r in mlib.evaluate_soi1_assertions([], st.params, st)}
        self.assertFalse(rows["crossedUnderThrustAt1x"].met)
        self.assertEqual(1, st.straddle_warp_frames)

    def test_a_crossing_too_soon_after_ignition_unmeets_the_crossing(self):
        st = to_straddle()
        st, _ = step(st, burning(ut=7772.0, time_to_soi=1.0))
        st, _ = step(st, burning(ut=7773.0, body="Kerbin"))
        st, _ = step(st, burning(ut=7804.0, body="Kerbin"))
        rows = {r.name: r for r in mlib.evaluate_soi1_assertions([], st.params, st)}
        self.assertFalse(rows["crossedUnderThrustAt1x"].met)

    def test_an_unpowered_crossing_frame_unmeets_the_crossing(self):
        st = to_straddle()
        st, _ = step(st, burning(ut=7790.0, time_to_soi=20.0))
        st, _ = step(st, burning(ut=7810.0, body="Kerbin", throttle=0.0))
        st, _ = step(st, burning(ut=7841.0, body="Kerbin"))
        rows = {r.name: r for r in mlib.evaluate_soi1_assertions([], st.params, st)}
        self.assertFalse(rows["crossedUnderThrustAt1x"].met)

    def test_a_dry_engine_during_the_straddle_fails(self):
        st = to_straddle()
        st, _ = step(st, burning(ut=7790.0, time_to_soi=20.0))
        st, _ = step(st, burning(ut=7791.0, time_to_soi=19.0, available_thrust=0.0))
        self.assertIn("propellant exhausted", st.loss_reason)


class WarpPolicyTests(unittest.TestCase):
    def test_rails_only_in_the_coast(self):
        for phase in mlib.SOI1_PHASES:
            st = mlib.Soi1State(params=params(), phase=phase)
            self.assertEqual(phase == mlib.SOI1_COAST, mlib.soi1_rails_warp_permitted(st),
                             phase)

    def test_physics_only_for_the_slew_and_the_warp_tail(self):
        p = params()
        for phase in mlib.SOI1_PHASES:
            got = mlib.soi1_max_physics_warp(mlib.Soi1State(params=p, phase=phase))
            want = {mlib.SOI1_ALIGN: 2.0, mlib.SOI1_COAST: 4.0}.get(phase, 0.0)
            self.assertEqual(want, got, phase)
        self.assertEqual(0.0, mlib.soi1_max_physics_warp(
            mlib.Soi1State(params=params(alignPhysicsWarpIndex=0), phase=mlib.SOI1_ALIGN)))

    def test_the_straddle_flakes_any_warp_in_the_runner_guard(self):
        st = mlib.Soi1State(params=params(), phase=mlib.SOI1_STRADDLE)
        for mode, rate in (("PHYSICS", 2.0), ("RAILS", 5.0)):
            self.assertTrue(mlib.is_unexpected_warp(
                mode, rate, mlib.soi1_rails_warp_permitted(st),
                max_physics_warp=mlib.soi1_max_physics_warp(st)), mode)


class ShellRoundTripTests(unittest.TestCase):
    def _frames(self):
        f = [snap(ut=1000.0)]
        f += [snap(ut=1001.0 + i, warp_mode="PHYSICS", warp_rate=2.0) for i in range(3)]
        f += [snap(ut=1004.0)]
        f += [snap(ut=1010.0, available_thrust=125000.0, throttle=0.5, eccentricity=0.6),
              snap(ut=1030.0, available_thrust=125000.0, throttle=0.5, eccentricity=1.21,
                   time_to_soi=6780.0, next_body="Kerbin"),
              snap(ut=1031.0, time_to_soi=6779.0),
              snap(ut=7660.0, time_to_soi=150.0),
              snap(ut=7700.0, time_to_soi=110.0),
              snap(ut=7771.0, time_to_soi=39.0),
              burning(ut=7790.0, time_to_soi=20.0),
              burning(ut=7809.5, time_to_soi=0.5),
              burning(ut=7810.0, body="Kerbin"),
              burning(ut=7841.0, body="Kerbin")]
        return f

    def test_the_happy_path_is_mission_ok_with_the_handoff_disclaimer(self):
        control = FakeMissionControl(self._frames())
        code, result = run(shell.SPEC, {}, control)
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertEqual(0, code)
        self.assertEqual(mlib.SOI1_DONE, result["phasesReached"][-1])
        self.assertIn("physicsSoiSectionSplit", result["reason"])
        self.assertIn(mlib.ACTION_AP_POINT_PROGRADE, [a.kind for a in control.actions])

    def test_the_shell_wires_the_machine_and_the_warp_callables(self):
        self.assertIs(shell.SPEC.allow_rails_warp, mlib.soi1_rails_warp_permitted)
        self.assertIs(shell.SPEC.max_physics_warp, mlib.soi1_max_physics_warp)
        self.assertEqual(0, shell.SPEC.settle_frames)
        self.assertEqual("soi1_mun_exit_burn", shell.MISSION_NAME)


class DriftTests(unittest.TestCase):
    @staticmethod
    def _read(path):
        with open(path, "r", encoding="utf-8") as fh:
            return fh.read()

    def _schema_keys(self):
        with open(SCHEMA, "rb") as fh:
            return set(tomllib.load(fh)["params"])

    def _machine_keys(self):
        tree = ast.parse(self._read(os.path.join(_HERE, "mlib.py")))
        fn = next(n for n in ast.walk(tree)
                  if isinstance(n, ast.FunctionDef) and n.name == "soi1_params_from_dict")
        return {c.args[0].value for c in ast.walk(fn)
                if isinstance(c, ast.Call) and isinstance(c.func, ast.Attribute)
                and c.func.attr == "get" and c.args
                and isinstance(c.args[0], ast.Constant)}

    def test_every_param_the_machine_reads_is_declared_and_vice_versa(self):
        self.assertEqual(self._machine_keys(), self._schema_keys())

    def test_the_spec_params_are_schema_keys_and_parse(self):
        with open(SPEC, "rb") as fh:
            spec = tomllib.load(fh)
        mp = spec["driver"]["missionParams"]
        self.assertEqual("soi1_mun_exit_burn", spec["driver"]["mission"])
        self.assertLessEqual(set(mp), self._schema_keys())
        p = mlib.soi1_params_from_dict(mp)
        self.assertGreater(p.warp_lead_seconds, p.burn_lead_seconds)
        self.assertGreater(p.burn_lead_seconds, p.min_observed_burn_lead_seconds)

    def test_the_handoff_contract_names_the_terminal_phase(self):
        contract = mlib.mission_handoff_contract("soi1_mun_exit_burn")
        self.assertEqual(mlib.SOI1_DONE, contract["terminal"])

    def test_the_spec_tokens_match_the_csharp_line_shapes(self):
        with open(SPEC, "rb") as fh:
            lc = tomllib.load(fh)["expectations"]["logContracts"]
        required, forbidden = lc["required"], lc["forbidden"]
        armed = ("[Parsek][INFO][Recorder] SOI changed off rails Mun -> Kerbin at "
                 "UT=27560.41: section split deferred to the first sample on the new body")
        split = ("[Parsek][INFO][Recorder] Reference frame transition: Absolute -> "
                 "Absolute at SOI boundary UT=27560.43 onRails=0 source=Active from=Mun "
                 "to=Kerbin closingSample=1")
        on_rails = ("[Parsek][INFO][Recorder] Reference frame transition: Absolute -> "
                    "OrbitalCheckpoint at SOI boundary UT=27560.41 onRails=1 "
                    "source=Checkpoint")
        self.assertTrue(any(re.search(p, armed) for p in required))
        self.assertTrue(any(re.search(p, split) for p in required))
        self.assertTrue(any(re.search(p, on_rails) for p in forbidden))
        self.assertFalse(any(re.search(p, split) for p in forbidden))
        self.assertFalse(any(re.search(p, split.replace("closingSample=1",
                                                        "closingSample=0"))
                             for p in required))

    def test_the_csharp_writers_still_emit_those_literals(self):
        # Interpolated strings are split at their holes, so check the literal runs.
        physics = self._read(PHYSICS_SOI_CS)
        for literal in ("SOI changed off rails ",
                        "section split deferred to the first sample on the new body",
                        "Reference frame transition: ", " at SOI boundary ",
                        " onRails=0 source=", " closingSample="):
            self.assertIn(literal, physics, literal)
        # FlightRecorder is a partial class: the main file plus every FlightRecorder.*.cs.
        src_dir = os.path.dirname(RECORDER_CS)
        partials = sorted(n for n in os.listdir(src_dir)
                          if n.startswith("FlightRecorder.") and n.endswith(".cs"))
        recorder = "\n".join(self._read(os.path.join(src_dir, n)) for n in partials)
        self.assertIn("onRails={(onRails ? 1 : 0)} source={source}", recorder)


if __name__ == "__main__":
    unittest.main()
