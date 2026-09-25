"""Unit cells for the BAY-1 lane (mission `bay1_runway_bays`, D7 `bays`).

Guards the machine's four ways of being silently wrong:
  1. staging anything before the rolled-out craft reads back as the active vessel;
  2. firing the stage click (the auto-record trigger) without brakes and zero
     throttle, or opening a bay before the recorder has sampled the closed pose;
  3. treating a mid-cycle vessel loss as anything but a failure;
  4. a schema / params / shell drift (every param the machine reads must be a key
     the schema declares, derived by an AST walk, not a hand-copied list).

NO krpc, NO KSP, NO network.
"""

import ast
import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mlib  # noqa: E402

SCHEMA = os.path.join(_MISSIONS, "bay1_runway_bays.schema.toml")
SHELL = os.path.join(_MISSIONS, "bay1_runway_bays.py")


def params(**kw):
    base = {"craftName": "Mallard", "recordSettleFrames": 3, "bayHoldFrames": 4,
            "rolloutFrames": 20}
    base.update(kw)
    return mlib.bay1_params_from_dict(base)


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


def pad(ut=100.0, name="Mallard"):
    return snap(ut=ut, situation="PRE_LAUNCH", vessel_name=name)


def kinds(actions):
    return [a.kind for a in actions]


def run_to_arm(st):
    st, acts = mlib.bay1_decide(st, snap(vessel_lost=True))
    assert kinds(acts) == [mlib.ACTION_LAUNCH_VESSEL]
    st, _ = mlib.bay1_decide(st, pad())
    st, _ = mlib.bay1_decide(st, pad())
    assert st.phase == mlib.BAY1_ARM
    return st


class RolloutGateTests(unittest.TestCase):
    def test_first_frame_launches_the_craft_on_the_runway(self):
        st = mlib.bay1_initial_state(params())
        st, acts = mlib.bay1_decide(st, snap(vessel_lost=True))
        self.assertEqual([mlib.ACTION_LAUNCH_VESSEL], kinds(acts))
        self.assertEqual("Mallard", acts[0].text)
        self.assertEqual("Runway", acts[0].launch_site)

    def test_empty_craft_name_flakes_before_launching(self):
        st = mlib.bay1_initial_state(params(craftName=""))
        st, acts = mlib.bay1_decide(st, snap())
        self.assertEqual([], acts)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)

    def test_reload_frames_and_a_wrong_vessel_never_settle(self):
        st = mlib.bay1_initial_state(params())
        st, _ = mlib.bay1_decide(st, snap(vessel_lost=True))
        for s in (snap(vessel_lost=True), pad(name="GS1 Auto-Chute Booster"),
                  snap(situation="LANDED", vessel_name="Mallard"), pad(name="")):
            st, acts = mlib.bay1_decide(st, s)
            self.assertEqual([], acts)
            self.assertEqual(mlib.BAY1_ROLLOUT, st.phase)
            self.assertIsNone(st.verdict)

    def test_debounce_needs_consecutive_settled_frames(self):
        st = mlib.bay1_initial_state(params())
        st, _ = mlib.bay1_decide(st, snap(vessel_lost=True))
        st, _ = mlib.bay1_decide(st, pad())
        st, _ = mlib.bay1_decide(st, snap(vessel_lost=True))  # streak reset
        self.assertEqual(mlib.BAY1_ROLLOUT, st.phase)
        st, _ = mlib.bay1_decide(st, pad())
        st, _ = mlib.bay1_decide(st, pad())
        self.assertEqual(mlib.BAY1_ARM, st.phase)
        self.assertTrue(st.rollout_ready_observed)

    def test_rollout_give_up_is_a_named_flake(self):
        st = mlib.bay1_initial_state(params(rolloutFrames=3))
        st, _ = mlib.bay1_decide(st, snap(vessel_lost=True))
        for _ in range(5):
            st, _ = mlib.bay1_decide(st, snap(vessel_lost=True))
            if st.done:
                break
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.BAY1_ROLLOUT, st.flake_phase)
        self.assertIn("never read back", st.flake_reason)


class CycleTests(unittest.TestCase):
    def test_arm_brakes_and_zeroes_throttle_before_the_stage_click(self):
        st = run_to_arm(mlib.bay1_initial_state(params()))
        st, acts = mlib.bay1_decide(st, pad(ut=110.0))
        self.assertEqual([mlib.ACTION_SET_BRAKES, mlib.ACTION_SET_THROTTLE,
                          mlib.ACTION_ACTIVATE_STAGE], kinds(acts))
        self.assertEqual(1.0, acts[0].value)
        self.assertEqual(0.0, acts[1].value)
        self.assertEqual("PRE_LAUNCH", st.stage_situation)
        self.assertEqual(mlib.BAY1_RECORD_SETTLE, st.phase)

    def test_full_cycle_opens_then_closes_after_the_holds(self):
        st = run_to_arm(mlib.bay1_initial_state(params()))
        st, _ = mlib.bay1_decide(st, pad(ut=110.0))
        emitted = []
        ut = 110.0
        while not st.done:
            ut += 0.5
            st, acts = mlib.bay1_decide(st, pad(ut=ut))
            emitted.extend((ut, a) for a in acts)
        bays = [(u, a.value) for u, a in emitted if a.kind == mlib.ACTION_SET_CARGO_BAYS]
        self.assertEqual([1.0, 0.0], [v for _, v in bays])
        # Settle (3 frames) before open, hold (4 frames) before close.
        self.assertAlmostEqual(111.5, bays[0][0])
        self.assertAlmostEqual(113.5, bays[1][0])
        self.assertEqual(mlib.BAY1_DONE, st.phase)
        self.assertIsNone(st.verdict)
        outcomes = mlib.evaluate_bay1_assertions([], st.params, st)
        self.assertTrue(all(o.met for o in outcomes), [o.to_dict() for o in outcomes])
        self.assertEqual(mlib.MISSION_OK, mlib.resolve_flight_verdict(st, outcomes)[0])

    def test_vessel_loss_mid_cycle_is_an_assert_fail(self):
        st = run_to_arm(mlib.bay1_initial_state(params()))
        st, _ = mlib.bay1_decide(st, pad())
        st, acts = mlib.bay1_decide(st, snap(vessel_lost=True))
        self.assertEqual([], acts)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn(mlib.BAY1_RECORD_SETTLE, st.loss_reason)

    def test_staging_off_prelaunch_fails_the_assertion_row(self):
        st = run_to_arm(mlib.bay1_initial_state(params()))
        st, _ = mlib.bay1_decide(st, snap(ut=110.0, situation="LANDED", vessel_name="Mallard"))
        outcomes = {o.name: o for o in mlib.evaluate_bay1_assertions([], st.params, st)}
        self.assertFalse(outcomes["stagedAtPrelaunch"].met)

    def test_unfinished_machine_fails_bays_cycled(self):
        outcomes = {o.name: o for o in mlib.evaluate_bay1_assertions([], params(), None)}
        self.assertFalse(outcomes["baysCycled"].met)
        self.assertFalse(outcomes["rolloutObserved"].met)

    def test_done_is_idempotent(self):
        st = run_to_arm(mlib.bay1_initial_state(params()))
        st = st.__class__(**{**st.__dict__, "done": True})
        st2, acts = mlib.bay1_decide(st, pad())
        self.assertIs(st, st2)
        self.assertEqual([], acts)


class SchemaSyncTests(unittest.TestCase):
    def test_every_param_the_machine_reads_is_declared(self):
        with open(SCHEMA, "rb") as fh:
            declared = set(tomllib.load(fh)["params"])
        with open(os.path.join(_HERE, "mlib.py"), encoding="utf-8") as fh:
            src = fh.read()
        tree = ast.parse(src)
        fn = next(n for n in ast.walk(tree)
                  if isinstance(n, ast.FunctionDef) and n.name == "bay1_params_from_dict")
        read = set()
        for node in ast.walk(fn):
            if (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "get" and node.args
                    and isinstance(node.args[0], ast.Constant)):
                read.add(node.args[0].value)
        self.assertTrue(read)
        self.assertEqual(set(), read - declared)
        self.assertEqual(set(), declared - read)

    def test_shell_opts_into_the_vessel_name_read(self):
        with open(SHELL, encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        kws = {kw.arg: kw.value for node in ast.walk(tree) if isinstance(node, ast.Call)
               and getattr(node.func, "attr", "") == "KrpcMissionControl"
               for kw in node.keywords}
        self.assertIn("read_vessel_name", kws)
        self.assertIs(True, kws["read_vessel_name"].value)


if __name__ == "__main__":
    unittest.main()
