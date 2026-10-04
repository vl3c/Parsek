"""Unit cells for the kRPC recovery of a NON-active Real-Spawned chain tip (mission
`ci11_krpc_recover`, scenario `CI-11-chain-tip-krpc-recover-no-respawn`, fixture
`gloops-airshow` + the `chain-tip-recovery` preset).

Five groups:

  1. THE PURE PICK AND CLASSIFIER: `mlib.pick_unique_named` (exactly one vessel by
     that name, active or not) and `mlib.classify_recover_request`.
  2. THE PURE MACHINE (`mlib.krec_decide`): the settle / ask order, the issued ->
     scene-watch hand-off, the typed refusals, the two frame-bounded give-ups.
  3. THE RUNNER PERFORM (`KrpcMissionControl._perform_recover_named_vessel`) over a
     fake SpaceCenter, and its dispatch in `perform`.
  4. THE SHELL, driven end to end over scripted frames with no krpc and no KSP.
  5. SPEC / SCHEMA / CONTRACT SYNC.

NO krpc, NO KSP, NO network.
"""

import ast
import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)

import mlib                        # noqa: E402
import mission_runner              # noqa: E402
import ci11_krpc_recover           # noqa: E402
from test_shells import FakeMissionControl, run, snap  # noqa: E402

SPEC_PATH = os.path.join(_HARNESS, "scenarios",
                         "CI-11-chain-tip-krpc-recover-no-respawn.toml")
SCHEMA_PATH = os.path.join(_MISSIONS, "ci11_krpc_recover.schema.toml")
RUNNER_PATH = os.path.join(_MISSIONS, "mission_runner.py")

PARAMS = mlib.KRecParams(target_name="CTR Lander", start_settle_seconds=3.0,
                         request_timeout_frames=5, scene_timeout_frames=10,
                         scene_lost_frames=3)
ISSUED = mlib.RECOVER_REQUEST_ISSUED

# The pad vessel stays active; after the ask stock tears FLIGHT down.
HAPPY = [
    snap(ut=600.0),                                                     # KR-START
    snap(ut=603.0),                                                     # -> KR-RECOVER + ask
    snap(ut=603.5, recover_request_result=ISSUED),                      # -> KR-SCENE
    snap(ut=0.0, vessel_lost=True, recover_request_result=ISSUED),
    snap(ut=0.0, vessel_lost=True, recover_request_result=ISSUED),
    snap(ut=0.0, vessel_lost=True, recover_request_result=ISSUED),      # -> KR-RECOVERED
]


def _walk(frames, params=PARAMS):
    st = mlib.krec_initial_state(params)
    log = []
    for f in frames:
        st, actions = mlib.krec_decide(st, f)
        log.append((st.phase, [(a.kind, a.text) for a in actions]))
        if st.done:
            break
    return st, log


class PickAndClassifyTests(unittest.TestCase):

    def test_exactly_one_match_is_picked_wherever_it_sits(self):
        self.assertEqual((1, mlib.RECOVER_REQUEST_UNREAD),
                         mlib.pick_unique_named(["mk1-capsule", "CTR Lander"], "CTR Lander"))
        self.assertEqual((0, mlib.RECOVER_REQUEST_UNREAD),
                         mlib.pick_unique_named(["CTR Lander", "mk1-capsule"], "CTR Lander"))

    def test_no_match_refuses(self):
        self.assertEqual((None, mlib.RECOVER_REQUEST_NO_MATCH),
                         mlib.pick_unique_named(["mk1-capsule"], "CTR Lander"))
        self.assertEqual((None, mlib.RECOVER_REQUEST_NO_MATCH),
                         mlib.pick_unique_named([], "CTR Lander"))

    def test_two_vessels_with_the_name_refuse_as_ambiguous(self):
        self.assertEqual((None, mlib.RECOVER_REQUEST_AMBIGUOUS),
                         mlib.pick_unique_named(["CTR Lander", "x", "CTR Lander"],
                                                "CTR Lander"))

    def test_the_match_is_exact_and_an_empty_name_never_picks(self):
        self.assertEqual((None, mlib.RECOVER_REQUEST_NO_MATCH),
                         mlib.pick_unique_named(["CTR Lander Debris", "ctr lander"],
                                                "CTR Lander"))
        self.assertEqual((None, mlib.RECOVER_REQUEST_NO_MATCH),
                         mlib.pick_unique_named(["", "a"], ""))

    def test_classify(self):
        self.assertEqual(mlib.RECOVER_OUTCOME_ISSUED, mlib.classify_recover_request(ISSUED))
        for token in mlib.RECOVER_REQUEST_DECLINES + mlib.RECOVER_NAMED_PICK_REFUSALS:
            self.assertEqual(mlib.RECOVER_OUTCOME_REFUSED,
                             mlib.classify_recover_request(token), token)
        for token in (mlib.RECOVER_REQUEST_UNREAD, None, "SOMETHING-NEW"):
            self.assertEqual(mlib.RECOVER_OUTCOME_PENDING,
                             mlib.classify_recover_request(token), token)

    def test_the_pick_refusals_stay_out_of_the_sbr_reask_tuple(self):
        for token in mlib.RECOVER_NAMED_PICK_REFUSALS:
            self.assertNotIn(token, mlib.RECOVER_REQUEST_DECLINES)
        self.assertNotIn(mlib.RECOVER_REQUEST_UNREAD, mlib.RECOVER_NAMED_PICK_REFUSALS)


class KRecMachineTests(unittest.TestCase):

    def test_happy_path_asks_once_and_ends_on_the_torn_down_scene(self):
        st, log = _walk(HAPPY)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertTrue(st.skip_settle_tail)
        self.assertEqual(st.phases_reached, mlib.KREC_PHASES)
        asks = [a for _p, acts in log for a in acts]
        self.assertEqual(asks, [(mlib.ACTION_RECOVER_NAMED_VESSEL, "CTR Lander")])
        self.assertEqual(log[1][0], mlib.KREC_RECOVER)
        outcomes = {o.name: o for o in mlib.evaluate_krec_assertions(
            [], PARAMS, st.phases_reached, st)}
        self.assertTrue(all(o.met for o in outcomes.values()), outcomes)
        self.assertEqual(outcomes["recoverIssued"].value, ISSUED)

    def test_the_ask_waits_for_the_start_dwell(self):
        st = mlib.krec_initial_state(PARAMS)
        for ut in (600.0, 602.9):
            st, actions = mlib.krec_decide(st, snap(ut=ut))
            self.assertEqual((st.phase, actions), (mlib.KREC_START, []))

    def test_the_issue_and_the_first_dark_frame_may_share_a_snapshot(self):
        st, _ = _walk(HAPPY[:2])
        lost = snap(ut=0.0, vessel_lost=True, recover_request_result=ISSUED)
        for _ in range(3):
            st, _ = mlib.krec_decide(st, lost)
        self.assertTrue(st.done)
        self.assertEqual(st.phase, mlib.KREC_RECOVERED)

    def test_a_live_frame_resets_the_dark_streak(self):
        st, _ = _walk(HAPPY[:3])
        lost = snap(ut=0.0, vessel_lost=True, recover_request_result=ISSUED)
        live = snap(ut=604.0, recover_request_result=ISSUED)
        for f in (lost, lost, live, lost, lost):
            st, _ = mlib.krec_decide(st, f)
        self.assertEqual(st.phase, mlib.KREC_SCENE)
        self.assertFalse(st.done)
        st, _ = mlib.krec_decide(st, lost)
        self.assertEqual(st.phase, mlib.KREC_RECOVERED)

    def test_each_refusal_is_a_typed_loss_naming_its_token(self):
        for token in mlib.RECOVER_NAMED_PICK_REFUSALS + mlib.RECOVER_REQUEST_DECLINES:
            st, _ = _walk(HAPPY[:2])
            st, actions = mlib.krec_decide(st, snap(ut=603.5, recover_request_result=token))
            self.assertEqual(actions, [])
            self.assertTrue(st.done, token)
            self.assertEqual(st.verdict, mlib.MISSION_ASSERT_FAIL)
            self.assertIn("recover-named-refused: %s" % token, st.loss_reason)
            self.assertIn("'CTR Lander'", st.loss_reason)
            verdict, reason = mlib.resolve_flight_verdict(st, mlib.evaluate_krec_assertions(
                [], PARAMS, st.phases_reached, st))
            self.assertEqual((verdict, reason), (mlib.MISSION_ASSERT_FAIL, st.loss_reason))

    def test_an_unread_token_flakes_after_its_frame_bound(self):
        st, _ = _walk(HAPPY[:2])
        for i in range(PARAMS.request_timeout_frames):
            st, _ = mlib.krec_decide(st, snap(ut=604.0 + i))
            self.assertFalse(st.done)
        st, _ = mlib.krec_decide(st, snap(ut=700.0))
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertIn("no recover result for 'CTR Lander'", st.flake_reason)

    def test_a_scene_that_never_tears_down_flakes(self):
        st, _ = _walk(HAPPY[:3])
        live = snap(ut=604.0, recover_request_result=ISSUED)
        # The issued frame itself is the scene watch's first frame.
        for _ in range(PARAMS.scene_timeout_frames - 1):
            st, _ = mlib.krec_decide(st, live)
            self.assertFalse(st.done)
        st, _ = mlib.krec_decide(st, live)
        self.assertEqual(st.verdict, mlib.MISSION_FLAKE)
        self.assertEqual(st.flake_phase, mlib.KREC_SCENE)
        self.assertIn("FLIGHT was never torn down", st.flake_reason)
        outcomes = {o.name: o.met for o in mlib.evaluate_krec_assertions(
            [], PARAMS, st.phases_reached, st)}
        self.assertEqual(outcomes, {"recoverIssued": True, "sceneLeftFlight": False})

    def test_a_dark_frame_before_the_ask_is_a_loss_and_asks_nothing(self):
        st = mlib.krec_initial_state(PARAMS)
        st, actions = mlib.krec_decide(st, snap(ut=0.0, vessel_lost=True))
        self.assertEqual(actions, [])
        self.assertEqual(st.verdict, mlib.MISSION_ASSERT_FAIL)
        self.assertIn("vessel-lost-before-recovery", st.loss_reason)

    def test_params_from_dict(self):
        p = mlib.krec_params_from_dict({
            "targetName": "X", "startSettleSeconds": 7, "requestTimeoutFrames": 4,
            "sceneTimeoutFrames": 9, "sceneLostFrames": 2})
        self.assertEqual((p.target_name, p.start_settle_seconds, p.request_timeout_frames,
                          p.scene_timeout_frames, p.scene_lost_frames),
                         ("X", 7.0, 4, 9, 2))


class _FakeVessel:
    def __init__(self, name, recoverable=True, name_raises=False):
        self._name = name
        self._name_raises = name_raises
        self.recoverable = recoverable
        self.recovered = 0

    @property
    def name(self):
        if self._name_raises:
            raise RuntimeError("dead handle")
        return self._name

    def recover(self):
        self.recovered += 1


class _FakeSpaceCenter:
    def __init__(self, vessels, raises=False):
        self._vessels = vessels
        self._raises = raises

    @property
    def vessels(self):
        if self._raises:
            raise RuntimeError("vessel list blew up")
        return list(self._vessels)


def _control():
    return mission_runner.KrpcMissionControl(client_name="test")


class NamedRecoverPerformTests(unittest.TestCase):

    def test_the_one_named_vessel_is_recovered_while_another_is_active(self):
        pad = _FakeVessel("mk1-capsule")
        lander = _FakeVessel("CTR Lander")
        c = _control()
        c._perform_recover_named_vessel(_FakeSpaceCenter([pad, lander]), "CTR Lander")
        self.assertEqual((0, 1), (pad.recovered, lander.recovered))
        self.assertEqual(ISSUED, c._recover_request_result)

    def test_no_match_and_ambiguity_ask_nothing(self):
        a, b = _FakeVessel("CTR Lander"), _FakeVessel("CTR Lander")
        for vessels, token in (([_FakeVessel("mk1-capsule")], mlib.RECOVER_REQUEST_NO_MATCH),
                               ([a, b], mlib.RECOVER_REQUEST_AMBIGUOUS)):
            c = _control()
            c._perform_recover_named_vessel(_FakeSpaceCenter(vessels), "CTR Lander")
            self.assertEqual(token, c._recover_request_result)
        self.assertEqual((0, 0), (a.recovered, b.recovered))

    def test_an_unrecoverable_pick_is_declined_by_the_shared_lock(self):
        lander = _FakeVessel("CTR Lander", recoverable=False)
        c = _control()
        c._perform_recover_named_vessel(_FakeSpaceCenter([lander]), "CTR Lander")
        self.assertEqual(0, lander.recovered)
        self.assertEqual(mlib.RECOVER_REQUEST_DECLINED, c._recover_request_result)

    def test_unreadable_surfaces_never_raise_out_of_perform(self):
        c = _control()
        c._perform_recover_named_vessel(_FakeSpaceCenter([], raises=True), "CTR Lander")
        self.assertEqual(mlib.RECOVER_REQUEST_NO_MATCH, c._recover_request_result)
        lander = _FakeVessel("CTR Lander")
        c = _control()
        c._perform_recover_named_vessel(
            _FakeSpaceCenter([_FakeVessel("x", name_raises=True), lander]), "CTR Lander")
        self.assertEqual((ISSUED, 1), (c._recover_request_result, lander.recovered))

    def test_perform_dispatches_the_kind_to_the_named_helper(self):
        # AST, not regex: a comment naming the kind must not satisfy this.
        with open(RUNNER_PATH, "r", encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        perform = next(n for n in ast.walk(tree)
                       if isinstance(n, ast.ClassDef) and n.name == "KrpcMissionControl")
        perform = next(n for n in perform.body
                       if isinstance(n, ast.FunctionDef) and n.name == "perform")
        found = False
        for node in ast.walk(perform):
            if not isinstance(node, ast.If) or not isinstance(node.test, ast.Compare):
                continue
            rhs = node.test.comparators[0]
            if isinstance(rhs, ast.Attribute) and rhs.attr == "ACTION_RECOVER_NAMED_VESSEL":
                calls = [c.func.attr for c in ast.walk(node.body[0])
                         if isinstance(c, ast.Call) and isinstance(c.func, ast.Attribute)]
                self.assertIn("_perform_recover_named_vessel", calls)
                found = True
        self.assertTrue(found)
        self.assertNotIn(mlib.ACTION_RECOVER_NAMED_VESSEL, mlib.VESSEL_FREE_ACTION_KINDS)


class KRecShellTests(unittest.TestCase):

    PARAMS = {"targetName": "CTR Lander", "startSettleSeconds": 3,
              "requestTimeoutFrames": 5, "sceneTimeoutFrames": 10, "sceneLostFrames": 3}

    def test_shell_flies_the_scripted_recovery_to_mission_ok(self):
        control = FakeMissionControl(HAPPY)
        code, result = run(ci11_krpc_recover.SPEC, self.PARAMS, control, budget=600.0)
        self.assertEqual(result["mission"], "ci11_krpc_recover")
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual([(a.kind, a.text) for a in control.actions],
                         [(mlib.ACTION_RECOVER_NAMED_VESSEL, "CTR Lander")])
        self.assertIn("ledgerRecoveryCapture", result["reason"])

    def test_a_refused_pick_is_an_assert_fail(self):
        frames = HAPPY[:2] + [snap(ut=603.5,
                                   recover_request_result=mlib.RECOVER_REQUEST_AMBIGUOUS)]
        code, result = run(ci11_krpc_recover.SPEC, self.PARAMS, FakeMissionControl(frames),
                           budget=600.0)
        self.assertEqual(result["verdict"], mlib.MISSION_ASSERT_FAIL, result)
        self.assertIn("AMBIGUOUS", result["reason"])
        self.assertNotEqual(code, 0)


class KRecSpecSyncTests(unittest.TestCase):

    def _spec(self):
        with open(SPEC_PATH, "rb") as fh:
            return tomllib.load(fh)

    def test_spec_names_this_mission_and_satisfies_the_schema(self):
        spec = self._spec()
        self.assertEqual(spec["driver"]["kind"], "autopilot")
        self.assertEqual(spec["driver"]["mission"], "ci11_krpc_recover")
        with open(SCHEMA_PATH, "rb") as fh:
            schema = tomllib.load(fh)["params"]
        params = spec["driver"]["missionParams"]
        for key, rule in schema.items():
            if rule.get("required"):
                self.assertIn(key, params, key)
        for key in params:
            self.assertIn(key, schema, "undeclared mission param %s" % key)
        self.assertEqual(params["targetName"], "CTR Lander")

    def test_the_mission_runs_after_the_spawn_and_before_the_next_flight(self):
        spec = self._spec()
        self.assertEqual(spec["fixture"]["saveTemplate"], "fixtures/saves/gloops-airshow")
        self.assertEqual(spec["fixture"]["injectedRecordings"], "chain-tip-recovery")
        steps = spec["driver"]["steps"]
        names = [s.get("cmd") or s.get("phase") for s in steps]
        self.assertEqual(names.count("mission"), 1)
        self.assertLess(names.index("RealSpawn"), names.index("mission"))
        self.assertLess(names.index("mission"), names.index("GoToEditor"))
        self.assertLess(names.index("GoToEditor"), names.index("LaunchFromEditor"))
        # No seam verb between the spawn and the mission may switch or recover.
        between = names[names.index("RealSpawn") + 1:names.index("mission")]
        self.assertEqual(set(between), {"RecordingState"})
        self.assertEqual(names[-1], "FlushAndQuit")

    def test_the_census_is_report_only_until_a_reading_run(self):
        self.assertFalse(self._spec()["expectations"]["recordings"]["structure"]["gating"])

    def test_the_handoff_contract_names_the_terminal(self):
        contract = mlib.mission_handoff_contract("ci11_krpc_recover")
        self.assertEqual(contract["terminal"], mlib.KREC_RECOVERED)


if __name__ == "__main__":
    unittest.main()
