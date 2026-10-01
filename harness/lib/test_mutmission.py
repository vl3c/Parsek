"""Unit tests for mutmission + missionverify (the mission-verdict mutation checker,
phase 2 PR 3).

A synthetic archived run (record + mission result built with the real
``mlib.build_mission_result``) and a fake mission shell, so nothing here depends on
an archive being present; one cell drives the real ``b1_pad_hop`` shell. Each edit
type has a positive cell (it kills on the real chain) and the vacuity cells break
one decision path at a time by monkeypatching it and assert the checker names
exactly the affected gates VACUOUS, so a sweep's zero-vacuous result cannot come
from a checker that never reds.
"""

import ast
import copy
import json
import math
import os
import sys
import tempfile
import tomllib
import unittest
from types import SimpleNamespace

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
for _p in (_HERE, os.path.join(_HARNESS, "missions"), os.path.join(_HARNESS, "missions", "lib")):
    if _p not in sys.path:
        sys.path.append(_p)

import hlib  # noqa: E402
import missionverify  # noqa: E402
import mlib  # noqa: E402
import mutlib  # noqa: E402
import mutmission  # noqa: E402
from test_mutsave import Patched  # noqa: E402

MISSION = "fake_orbit"


class FakeShell:
    """A mission shell: two telemetry assertions, both computed from what the
    machine observed (phases) and what the frames read (apoapsis)."""

    def __init__(self, constant_row=False, raise_on=None):
        self.constant_row = constant_row
        self.raise_on = raise_on

    def build_state(self, params):
        if self.raise_on == "build":
            raise KeyError("targetApoapsis")
        return SimpleNamespace(phase="PRELAUNCH", phases_reached=("PRELAUNCH",),
                               verdict=None, loss_reason=None)

    def evaluate(self, frames, params, state=None):
        if self.raise_on == "evaluate":
            raise ValueError("no frames")
        lo, hi = params.get("window", [70000.0, 90000.0])
        aps = [f.apoapsis for f in frames if math.isfinite(f.apoapsis)]
        peak = max(aps) if aps else None
        rows = [mlib.AssertionOutcome("reachedOrbit", "ORBIT" in state.phases_reached,
                                      state.phases_reached[-1], {"required": "ORBIT"}),
                mlib.AssertionOutcome("apoapsisWindow", peak is not None and lo <= peak <= hi,
                                      peak if peak is not None else float("nan"),
                                      {"window": [lo, hi]})]
        if self.constant_row:
            rows.append(mlib.AssertionOutcome("alwaysFine", True, None, {}))
        return rows


ROWS = [mlib.AssertionOutcome("reachedOrbit", True, "ORBIT", {"required": "ORBIT"}),
        mlib.AssertionOutcome("apoapsisWindow", True, 80123.5, {"window": [70000.0, 90000.0]})]


def _result(rows=ROWS, verdict=None, reason=None):
    v, r = mlib.resolve_flight_verdict(mutmission.clean_state(), rows)
    obj = mlib.build_mission_result(
        mission=MISSION, verdict=verdict or v, reason=reason or r,
        phases_reached=["PRELAUNCH", "ASCENT", "ORBIT"], connect_attempts=1,
        connected_seconds=0.2, rpc_port=50000, assertions=rows, wall_seconds=120.0,
        krpc_client_version="0.5.4", krpc_server_version="0.5.4")
    return mlib.serialize_mission_result(obj)


def _steps(outcome=True):
    steps = [{"cmd": "LoadGame", "id": "0001", "expect": "OK", "verdict": "OK", "met": True},
             {"cmd": "SetSetting", "id": "0002", "expect": "OK", "verdict": "OK", "met": True},
             {"phase": "mission", "id": "0003", "expect": "MISSION-OK", "verdict": "MISSION-OK",
              "missionVerdict": "MISSION-OK", "met": True, "subkind": None},
             {"cmd": "CommitTree", "id": "0004", "expect": "OK", "verdict": "OK", "met": True}]
    if outcome:
        steps.append({"cmd": "EvaExit", "id": "0005", "expect": "OK", "verdict": "OK", "met": True})
    steps.append({"cmd": "FlushAndQuit", "id": "0006", "expect": "OK", "verdict": "OK", "met": True})
    return steps


def _record(outcome=True, verifiers=None):
    rec = {"driver": {"steps": _steps(outcome)}}
    if verifiers is not None:
        rec["verifiers"] = verifiers
    return rec


SPEC = {"id": "T-mission", "driver": {"kind": "autopilot", "mission": MISSION,
                                      "missionParams": {"window": [70000.0, 90000.0]}}}


def _lane(shell=None, record=None, text=None, spec=SPEC):
    inputs = mutmission.MissionInputs(record if record is not None else _record(),
                                      text if text is not None else _result(),
                                      shell if shell is not None else FakeShell())
    lane = mutlib.check_mission_lane(spec, "test", inputs)
    return lane, {g.label: g for g in lane.mission_gates}


class BaselineTests(unittest.TestCase):

    def test_a_consistent_archive_replays_green_and_every_gate_is_proven(self):
        lane, gates = _lane()
        self.assertEqual(mutlib.BASELINE_GREEN, lane.baseline, lane.baseline_reasons)
        self.assertEqual({"PROVEN"}, {g.verdict for g in lane.mission_gates},
                         [(g.label, g.reason) for g in lane.mission_gates if g.verdict != "PROVEN"])
        for label in ("mission.assert[reachedOrbit]", "mission.assert[apoapsisWindow]",
                      "mission.blind[reachedOrbit]", "mission.blind[apoapsisWindow]",
                      "mission:blind-verdict", "missionOutcome[EvaExit#0005]",
                      "mission:torn", "mission:schema", "mission:no-verdict",
                      "mission:no-assertions", "mission:vessel-lost", "mission:flake"):
            self.assertIn(label, gates)
        self.assertEqual(0, lane.count(mutlib.SURVIVED))

    def test_a_non_ok_archive_is_not_green(self):
        text = _result(rows=[ROWS[0], mlib.AssertionOutcome("apoapsisWindow", False, None, {})])
        lane, _g = _lane(text=text)
        self.assertEqual(mutlib.BASELINE_NOT_GREEN, lane.baseline)
        self.assertIn("archived mission verdict MISSION-ASSERT-FAIL", lane.baseline_reasons[0])

    def test_a_reason_the_real_resolver_does_not_reproduce_is_not_green(self):
        lane, _g = _lane(text=_result(reason="all fine, trust me"))
        self.assertEqual(mutlib.BASELINE_NOT_GREEN, lane.baseline)
        self.assertIn("the real resolve_flight_verdict", lane.baseline_reasons[0])

    def test_an_archived_row_the_composition_does_not_reproduce_is_not_green(self):
        good = {"mission": {"status": "PASS", "missionVerdict": "MISSION-OK", "subkind": ""}}
        lane, _g = _lane(record=_record(verifiers=good))
        self.assertEqual(mutlib.BASELINE_GREEN, lane.baseline, lane.baseline_reasons)
        bad = {"mission": {"status": "PASS", "missionVerdict": "MISSION-OK", "subkind": "x"}}
        lane, _g = _lane(record=_record(verifiers=bad))
        self.assertEqual(mutlib.BASELINE_NOT_GREEN, lane.baseline)
        self.assertIn("replayed mission row", lane.baseline_reasons[0])

    def test_a_record_with_no_mission_row_is_not_green(self):
        rec = _record()
        rec["driver"]["steps"] = [s for s in rec["driver"]["steps"] if s.get("phase") != "mission"]
        lane, _g = _lane(record=rec)
        self.assertEqual(mutlib.BASELINE_NOT_GREEN, lane.baseline)


class EditTests(unittest.TestCase):

    def test_an_assertion_kill_names_the_assertion_and_routes_driver_invalid(self):
        lane, _g = _lane()
        m = [x for x in lane.mutations if x.target == "assert[apoapsisWindow]"][0]
        self.assertEqual(mutlib.KILLED, m.outcome)
        self.assertIn("INVALID(mission)", m.detail)
        self.assertIn("assertions unmet: apoapsisWindow", m.detail)

    def test_a_row_met_with_no_telemetry_is_vacuous(self):
        rows = ROWS + [mlib.AssertionOutcome("alwaysFine", True, None, {})]
        lane, gates = _lane(shell=FakeShell(constant_row=True), text=_result(rows=rows))
        self.assertEqual("VACUOUS", gates["mission.blind[alwaysFine]"].verdict)
        self.assertIn("certifies without reading telemetry", gates["mission.blind[alwaysFine]"].reason)
        self.assertEqual("PROVEN", gates["mission.blind[apoapsisWindow]"].verdict)
        self.assertEqual("PROVEN", gates["mission.assert[alwaysFine]"].verdict)

    def test_an_evaluator_that_raises_blind_is_unchecked_not_proven(self):
        _lane_, gates = _lane(shell=FakeShell(raise_on="evaluate"))
        self.assertEqual("UNCHECKED", gates["mission.blind[reachedOrbit]"].verdict)
        self.assertEqual("UNCHECKED", gates["mission:blind-verdict"].verdict)

    def test_a_build_state_that_rejects_the_params_is_unchecked(self):
        _lane_, gates = _lane(shell=FakeShell(raise_on="build"))
        self.assertEqual("UNCHECKED", gates["mission.blind[reachedOrbit]"].verdict)
        self.assertIn("build_state raised", gates["mission.blind[reachedOrbit]"].reason)

    def test_no_outcome_verb_means_no_outcome_gate_and_a_note(self):
        lane, gates = _lane(record=_record(outcome=False))
        self.assertFalse([g for g in gates if g.startswith("missionOutcome")])
        self.assertTrue(any("no post-mission outcome verb" in n for n in lane.notes))

    def test_a_nan_detail_is_scrubbed_and_reads_as_mission_not_tooling(self):
        # KXRW-RESULT-NAN-DETAIL: a NaN detail used to make the writer refuse the
        # result (INVALID(tooling-mission), rows lost). The scrub writes it as null
        # and the unmet row classifies as INVALID(mission) naming that row.
        base = mutmission.prepare_baseline(SPEC, mutmission.MissionInputs(
            _record(), _result(), FakeShell()))
        nan_row = [mlib.AssertionOutcome("apoapsisWindow", False, None, {"peak": float("nan")})]
        text, verdict, reason = mutmission.build_result_text(base, nan_row, mutmission.clean_state())
        self.assertIsNotNone(text)
        self.assertNotIn("result serialization failed", reason)
        self.assertIn('"peak": null', text)
        c = mutmission.run_chain(base, text)
        self.assertEqual((hlib.VERDICT_INVALID, "mission"), (c.verdict, c.subkind))

    def test_the_real_b1_shell_blind_replay_reads_every_row_unmet(self):
        import b1_pad_hop
        with open(os.path.join(_HARNESS, "scenarios", "B1-pad-hop.toml"), "rb") as fh:
            spec = tomllib.load(fh)
        params = spec["driver"]["missionParams"]
        state = b1_pad_hop.SPEC.build_state(params)
        for frames in ([], [mutmission.blind_snapshot()] * mutmission.BLIND_FRAMES):
            outs = b1_pad_hop.SPEC.evaluate(frames, params, state)
            self.assertTrue(outs)
            self.assertEqual([], [o.name for o in outs if o.met])


class VacuityTests(unittest.TestCase):

    def test_a_resolver_that_ignores_unmet_rows_is_vacuous(self):
        real = mlib.resolve_flight_verdict

        def lenient(state, outcomes):
            if getattr(state, "verdict", None) or getattr(state, "loss_reason", None):
                return real(state, outcomes)
            return mlib.MISSION_OK, "all telemetry assertions met"

        with Patched(mlib, "resolve_flight_verdict", lenient):
            _lane_, gates = _lane()
        self.assertEqual("VACUOUS", gates["mission.assert[reachedOrbit]"].verdict)
        self.assertEqual("VACUOUS", gates["mission:no-assertions"].verdict)
        self.assertEqual("VACUOUS", gates["mission:blind-verdict"].verdict)
        self.assertEqual("PROVEN", gates["mission:vessel-lost"].verdict)
        self.assertEqual("PROVEN", gates["mission.blind[reachedOrbit]"].verdict)

    def test_a_step_map_that_meets_every_verdict_is_vacuous(self):
        with Patched(hlib, "classify_mission_step", lambda v: (True, "")):
            _lane_, gates = _lane()
        for label in ("mission.assert[apoapsisWindow]", "mission:torn", "mission:flake"):
            self.assertEqual("VACUOUS", gates[label].verdict, label)
        self.assertEqual("PROVEN", gates["missionOutcome[EvaExit#0005]"].verdict)

    def test_a_reader_that_drops_the_schema_gate_is_vacuous_only_there(self):
        def no_gate(text):
            try:
                obj = json.loads(text)
            except (ValueError, TypeError):
                return None
            return obj if isinstance(obj, dict) else None

        with Patched(missionverify, "parse_mission_result_text", no_gate):
            _lane_, gates = _lane()
        self.assertEqual("VACUOUS", gates["mission:schema"].verdict)
        self.assertEqual("PROVEN", gates["mission:torn"].verdict)
        self.assertEqual("PROVEN", gates["mission.assert[reachedOrbit]"].verdict)

    def test_an_outcome_classifier_that_never_blames_the_flight_is_vacuous(self):
        with Patched(hlib, "classify_post_mission_outcome_miss",
                     lambda step: (False, "driver-verdict-mismatch")):
            _lane_, gates = _lane()
        g = gates["missionOutcome[EvaExit#0005]"]
        self.assertEqual("VACUOUS", g.verdict)
        self.assertIn("ERROR", g.reason)
        self.assertEqual("PROVEN", gates["mission.assert[reachedOrbit]"].verdict)

    def test_an_all_met_that_passes_an_empty_list_is_vacuous(self):
        with Patched(mlib, "all_assertions_met", lambda outcomes: all(o.met for o in outcomes)):
            _lane_, gates = _lane()
        self.assertEqual("VACUOUS", gates["mission:no-assertions"].verdict)
        self.assertEqual("PROVEN", gates["mission.assert[reachedOrbit]"].verdict)


class SharedPathTests(unittest.TestCase):

    def test_run_py_reads_the_result_through_the_shared_parser(self):
        if _HARNESS not in sys.path:
            sys.path.insert(0, _HARNESS)
        import run  # noqa: E402

        calls = []
        real = missionverify.parse_mission_result_text

        def spy(text):
            calls.append(text)
            return real(text)

        with tempfile.TemporaryDirectory() as tmp:
            p = os.path.join(tmp, "r_mission.json")
            with open(p, "w", encoding="utf-8") as fh:
                fh.write(_result())
            with Patched(missionverify, "parse_mission_result_text", spy):
                self.assertEqual("MISSION-OK", run._read_mission_verdict(p))
        self.assertEqual(1, len(calls))
        self.assertIs(run._stage_subkind_for, missionverify.stage_subkind_for)
        self.assertEqual(missionverify.MISSION_RESULT_SCHEMA, run.MISSION_RESULT_SCHEMA)
        self.assertEqual(mlib.MISSION_RESULT_SCHEMA, missionverify.MISSION_RESULT_SCHEMA)

    def test_run_verifiers_composes_driver_validity_only_through_missionverify(self):
        with open(os.path.join(_HARNESS, "run.py"), "r", encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        fn = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "run_verifiers"][0]
        calls = {(c.func.value.id, c.func.attr) for c in ast.walk(fn)
                 if isinstance(c, ast.Call) and isinstance(c.func, ast.Attribute)
                 and isinstance(c.func.value, ast.Name)}
        self.assertIn(("missionverify", "compose_driver_validity"), calls)
        everywhere = {(c.func.value.id, c.func.attr) for c in ast.walk(tree)
                      if isinstance(c, ast.Call) and isinstance(c.func, ast.Attribute)
                      and isinstance(c.func.value, ast.Name)}
        for decision in ("first_unmet_post_mission_outcome", "classify_post_mission_outcome_miss"):
            self.assertNotIn(("hlib", decision), everywhere, decision)

    def test_the_composition_matches_the_documented_carve_out(self):
        def ev(rows):
            lines = ["id=%s cmd=%s verdict=%s" % (i, c, v) for i, c, v in rows if v]
            return hlib.evaluate_response_stream(lines, [{"id": i, "cmd": c, "expect": "OK"}
                                                         for i, c, _v in rows])
        ok_mission = {"id": "0002", "met": True, "missionVerdict": "MISSION-OK", "subkind": ""}
        seam = missionverify.compose_driver_validity(ev([("0001", "LoadGame", "OK")]), None,
                                                      False, False)
        self.assertEqual((True, ""), (seam.driver_valid, seam.stage_subkind))
        self.assertIsNone(seam.detail_mission)
        rec = missionverify.compose_driver_validity(
            ev([("0001", "LoadGame", "OK"), ("0003", "CommitTree", "ERROR")]), ok_mission,
            False, False)
        self.assertEqual((True, False), (rec.driver_valid, rec.mission_outcome_unmet))
        self.assertEqual("no-gating-verbs", rec.detail_mission_outcome["reason"])
        out = missionverify.compose_driver_validity(
            ev([("0001", "LoadGame", "OK"), ("0003", "EvaExit", "ERROR")]), ok_mission,
            False, False)
        self.assertEqual((True, True, "FAIL"), (out.driver_valid, out.mission_outcome_unmet,
                                                 out.detail_mission_outcome["status"]))
        unmet = missionverify.compose_driver_validity(
            ev([("0001", "LoadGame", "OK")]),
            {"id": "0002", "met": False, "missionVerdict": "MISSION-FLAKE",
             "subkind": "autopilot-flake"}, False, False)
        self.assertEqual((False, "autopilot-flake", "SKIPPED"),
                         (unmet.driver_valid, unmet.stage_subkind,
                          unmet.detail_mission_outcome["status"]))


class ReportTests(unittest.TestCase):

    def test_report_lists_vacuous_mission_gates(self):
        rows = ROWS + [mlib.AssertionOutcome("alwaysFine", True, None, {})]
        lane, _g = _lane(shell=FakeShell(constant_row=True), text=_result(rows=rows))
        text = mutlib.render_report([lane], [])
        self.assertIn("mission gates (mission chain edits): proven", text)
        self.assertIn("## Vacuous mission gates", text)
        self.assertIn("mission.blind[alwaysFine]", text)
        self.assertIn("missionGates(proven=", mutlib.summary_line(lane))
        self.assertEqual(1, mutlib.sweep_totals([lane], 0).mission_vacuous)

    def test_an_unreadable_mission_result_is_unchecked_and_the_lane_still_runs(self):
        inputs = mutlib.ArchiveInputs("test", "[LOG 00:00:00.000] boot\n", None, None,
                                      mission=mutmission.MissionInputs(
                                          _record(), None, FakeShell(), "",
                                          "PermissionError: denied"))
        spec = dict(SPEC, expectations={"logContracts": {"required": ["boot"]}})
        lane = mutlib.check_lane(spec, inputs)
        self.assertEqual(mutlib.BASELINE_GREEN, lane.baseline)
        self.assertEqual([("mission:inputs", "UNCHECKED")],
                         [(g.label, g.verdict) for g in lane.mission_gates])
        self.assertIn("unreadable archive", lane.mission_gates[0].reason)

    def test_full_lane_runs_the_mission_edits_when_the_archive_carries_them(self):
        inputs = mutlib.ArchiveInputs("test", "[LOG 00:00:00.000] boot\n", None, None,
                                      mission=mutmission.MissionInputs(
                                          _record(), _result(), FakeShell()))
        spec = dict(SPEC, expectations={"logContracts": {"required": ["boot"]}})
        lane = mutlib.check_lane(spec, inputs)
        self.assertEqual(mutlib.BASELINE_GREEN, lane.baseline)
        self.assertTrue(lane.mission_gates)
        self.assertIn("mission", mutlib.spec_gating_surfaces(spec))


if __name__ == "__main__":
    unittest.main()
