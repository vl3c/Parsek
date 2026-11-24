"""P5 of the part ghost parity tool: the committed baseline, the accepted list and the
regression gate (design-part-ghost-parity.md section 10, Appendix F), plus the writer tool
``harness/tools/part_parity_baseline.py`` and the viewer's ``--pinned`` mode.

The last class reads the COMMITTED files: ``harness/partparity/accepted.toml`` must parse
and name open or closed todo ids that exist, every ``baseline-<profile>.json`` must
validate, and every armed PP lane must have a baseline section for each label it sweeps.
"""

import contextlib
import io
import json
import os
import re
import shutil
import sys
import tempfile
import tomllib
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.join(os.path.dirname(HERE), "tools")
for _p in (HERE, TOOLS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import _partparity_fixture as fx  # noqa: E402
import gui_mirror  # noqa: E402
import partparity as pp  # noqa: E402
import test_partparity as tp  # noqa: E402  (the FX case builders)
import part_parity_baseline as ppb  # noqa: E402
import part_parity_view as ppv  # noqa: E402

HARNESS = os.path.dirname(HERE)
REPO_ROOT = os.path.dirname(HARNESS)
COMMITTED_DIR = os.path.join(HARNESS, pp.BASELINE_DIR_NAME)
SCENARIOS = os.path.join(HARNESS, "scenarios")
TODO_DOC = os.path.join(REPO_ROOT, "docs", "dev", "todo-and-known-bugs.md")


def _view(view="side-a", real=0, ghost=0, color=0, iou=1.0, pct=None, start=None):
    v = {"view": view, "realOnlyBlobPx": real, "ghostOnlyBlobPx": ghost, "colorBlobPx": color,
         "iou": iou, "realOnlyBlobPct": pct, "ghostOnlyBlobPct": None}
    if start is not None:
        v["start"] = start
    return v


def _case(cid, views=None, status="ok", tags=(), structure=None, skin=None, state=None,
          flags=(), proof=None):
    doc = {"caseId": cid, "part": cid.split("--")[0], "source": "Squad",
           "skin": skin or {}, "setup": {"status": status, "detail": None},
           "views": views if views is not None else [_view()],
           "structure": structure or {}, "tags": list(tags), "flags": list(flags),
           "state": state}
    if proof is not None:
        doc["stateProof"] = proof
    return doc


def _manifest(cids, label="pp1", profile="stock-minimal", dll="aa"):
    return {"schema": pp.MANIFEST_SCHEMA, "label": label, "profile": profile, "dllHash": dll,
            "cases": [{"caseId": c} for c in cids], "progress": {"complete": True}}


def _entry(case, view="*", measure="*", noisy=False, profile=None, todo="SOME-TODO-1"):
    return pp.AcceptedEntry(case, view, measure, "known", todo, profile, noisy)


def _gate(sidecars, section, accepted=(), label="pp1", profile="stock-minimal", planned=None):
    manifest = _manifest(planned if planned is not None else sorted(sidecars), label, profile)
    verdicts = [pp.judge_case(s) for _c, s in sorted(sidecars.items())]
    return pp.gate_sweep(label, profile, manifest, sidecars, verdicts, section, list(accepted))


def _section(sidecar_runs):
    """A baseline section from runs of {cid: sidecar}."""
    runs = [("run%d" % i, _manifest(sorted(r)), r) for i, r in enumerate(sidecar_runs)]
    return pp.build_baseline_section(runs, "2026-11-22T00:00:00Z")


def _kinds(findings):
    return sorted(f.kind for f in findings)


class MeasureAndBaselineTests(unittest.TestCase):

    def test_a_stowed_case_keys_its_views_bare(self):
        m = pp.case_measures(_case("a--x--1", [_view("side-a", 3, 4, 5, 0.9), _view("top")]))
        self.assertEqual({"side-a": {"realOnlyBlobPx": 3, "ghostOnlyBlobPx": 4, "colorBlobPx": 5,
                                     "iou": 0.9},
                          "top": {"realOnlyBlobPx": 0, "ghostOnlyBlobPx": 0, "colorBlobPx": 0,
                                  "iou": 1.0}}, m)

    def test_a_state_case_keys_both_routes_and_skips_the_stowed_reference(self):
        v = _view("side-a", 1, 2, 3, 0.8, start={"realOnlyBlobPx": 9, "ghostOnlyBlobPx": 0,
                                                 "colorBlobPx": 7, "iou": 0.5})
        v["stowedRef"] = {"realOnlyBlobPx": 999}
        m = pp.case_measures(_case("a--x--deployed--1", [v], state="deployed"))
        self.assertEqual(["event:side-a", "start:side-a"], sorted(m))
        self.assertEqual(9, m["start:side-a"]["realOnlyBlobPx"])
        self.assertEqual(1, m["event:side-a"]["realOnlyBlobPx"])

    def test_a_null_measure_is_not_baselined(self):
        m = pp.case_measures(_case("a--x--1", [_view(iou=None)]))
        self.assertNotIn("iou", m["side-a"])

    def test_the_band_spans_the_runs_and_identity_bands_are_left_out(self):
        e = pp.baseline_case_entry([_case("a--x--1", [_view(color=10, iou=0.99991)]),
                                    _case("a--x--1", [_view(color=40, iou=0.99995)])])
        self.assertEqual({"side-a": {"colorBlobPx": [10, 40], "iou": [0.9999, 1.0]}}, e["views"])
        self.assertEqual(["ok"], e["setup"])
        self.assertTrue(e["measured"])

    def test_a_setup_fault_run_adds_its_status_but_no_band(self):
        e = pp.baseline_case_entry([_case("a--x--1", [_view(color=10)]),
                                    _case("a--x--1", [_view(color=9000)], status="crashed")])
        self.assertEqual({"side-a": {"colorBlobPx": [10, 10]}}, e["views"])
        self.assertEqual(["crashed", "ok"], e["setup"])
        only_fault = pp.baseline_case_entry([_case("a--x--1", status="spawn-timeout")])
        self.assertFalse(only_fault["measured"])

    def test_the_section_is_the_union_of_planned_part_cases_without_controls(self):
        r1 = {"a--x--1": _case("a--x--1"), "ctl-empty": _case("ctl-empty")}
        r2 = {"b--x--2": _case("b--x--2", [_view(real=5)])}
        runs = [("r1", _manifest(sorted(r1), dll="h1"), r1), ("r2", _manifest(sorted(r2), dll="h2"), r2)]
        sec = pp.build_baseline_section(runs)
        self.assertEqual(["a--x--1", "b--x--2"], sorted(sec["cases"]))
        self.assertEqual(["r1", "r2"], sec["runs"])
        self.assertEqual(["h1", "h2"], sec["dllHashes"])
        self.assertEqual({}, sec["cases"]["a--x--1"]["views"])

    def test_validate_baseline(self):
        good = {"schema": pp.BASELINE_SCHEMA, "profile": "p", "labels": {
            "pp1": {"cases": {"a--x--1": {"setup": ["ok"], "measured": True,
                                          "views": {"side-a": {"colorBlobPx": [1, 2]}}}}}}}
        self.assertEqual([], pp.validate_baseline(good, "p"))
        lost = json.loads(json.dumps(good))
        del lost["labels"]["pp1"]["cases"]["a--x--1"]["measured"]
        self.assertTrue(pp.validate_baseline(lost, "p"), "dropping measured would switch drift off")
        self.assertTrue(pp.validate_baseline(good, "other"))
        bad = json.loads(json.dumps(good))
        bad["labels"]["pp1"]["cases"]["a--x--1"]["views"]["side-a"]["colorBlobPx"] = [3, 2]
        self.assertTrue(pp.validate_baseline(bad, "p"))
        bad["labels"]["pp1"]["cases"]["a--x--1"]["views"]["side-a"] = {"wat": [1, 2]}
        self.assertTrue(pp.validate_baseline(bad, "p"))
        self.assertTrue(pp.validate_baseline({"schema": "x"}))
        self.assertTrue(pp.validate_baseline([]))


class AcceptedListTests(unittest.TestCase):

    def _parse(self, rows, schema=1):
        return pp.parse_accepted({"schema": schema, "accepted": rows})

    def _row(self, **kw):
        row = {"case": "a--x--1", "view": "*", "measure": "color-blob", "reason": "seam lines",
               "todo": "GHOST-PARITY-RESIDUALS-2026-11-22"}
        row.update(kw)
        return row

    def test_a_good_entry_parses(self):
        entries, errors = self._parse([self._row(profile="stock-minimal", noisy=True)])
        self.assertEqual([], errors)
        self.assertEqual(pp.AcceptedEntry("a--x--1", "*", "color-blob", "seam lines",
                                          "GHOST-PARITY-RESIDUALS-2026-11-22", "stock-minimal", True),
                         entries[0])

    def test_no_file_is_an_empty_list(self):
        self.assertEqual(([], []), pp.parse_accepted(None))

    def test_refusals(self):
        cases = {
            "missing todo": self._row(todo=""),
            "placeholder reason": self._row(reason="TODO"),
            "placeholder todo": self._row(todo="TBD"),
            "lower-case todo": self._row(todo="some-todo"),
            "bad view": self._row(view="sideways"),
            "bad route": self._row(view="later:side-a"),
            "bad measure": self._row(measure="colour"),
            "bare flag": self._row(measure="flag:"),
            "unknown key": self._row(why="x"),
            "bad case": self._row(case="Has Spaces"),
            "noisy not bool": self._row(noisy="yes"),
        }
        for name, row in cases.items():
            with self.subTest(name):
                _entries, errors = self._parse([row])
                self.assertTrue(errors, name)
        _e, errors = self._parse([self._row()], schema=2)
        self.assertTrue(errors)
        _e, errors = self._parse([self._row(), self._row()])
        self.assertTrue(any("duplicate" in e for e in errors))
        _e, errors = pp.parse_accepted({"schema": 1, "accepted": [], "extra": 1})
        self.assertTrue(errors)

    def test_route_qualified_and_flag_views_parse(self):
        _e, errors = self._parse([self._row(view="start:bottom", measure="flag:state-not-modelled:*"),
                                  self._row(view="event:top", measure="shape-blob")])
        self.assertEqual([], errors)

    def test_covers(self):
        e = _entry("a--x--1", "side-a", "color-blob")
        self.assertTrue(e.covers("a--x--1", "p", "side-a", "color-blob"))
        self.assertTrue(e.covers("a--x--1", "p", "event:side-a", "color-blob"), "a bare view covers both routes")
        self.assertFalse(e.covers("a--x--1", "p", "side-b", "color-blob"))
        self.assertFalse(e.covers("a--x--1", "p", None, "color-blob"), "a case-level reason needs view *")
        self.assertFalse(e.covers("a--x--1", "p", "side-a", "shape-blob"))
        self.assertFalse(e.covers("b--x--1", "p", "side-a", "color-blob"))
        routed = _entry("a--x--1", "start:side-a", "color-blob")
        self.assertFalse(routed.covers("a--x--1", "p", "event:side-a", "color-blob"))
        self.assertTrue(routed.covers("a--x--1", "p", "start:side-a", "color-blob"))
        for group in pp.PICTURE_GROUPS:
            self.assertTrue(_entry("a--x--1").covers("a--x--1", "p", "side-a", group))
        for group in ("structure", "skin", "flag:start-pose-not-first-frame", pp.GROUP_ANY):
            self.assertFalse(_entry("a--x--1").covers("a--x--1", "p", None, group), group)
        flag = _entry("a--x--1", measure="flag:state-not-modelled:*")
        self.assertTrue(flag.covers("a--x--1", "p", None, "flag:state-not-modelled:event:no-info"))
        self.assertFalse(flag.covers("a--x--1", "p", None, "flag:start-pose-not-first-frame"))
        prof = _entry("a--x--1", profile="modded-compat")
        self.assertFalse(prof.covers("a--x--1", "stock-minimal", "side-a", "color-blob"))
        self.assertTrue(prof.covers("a--x--1", "modded-compat", "side-a", "color-blob"))


class FailReasonTests(unittest.TestCase):

    def test_only_reasons_over_the_fail_line_hold_a_case_at_fail(self):
        cv = pp.judge_case(_case("a--x--1", [_view("side-a", color=300), _view("top", color=100)]))
        self.assertEqual(pp.VERDICT_FAIL, cv.verdict)
        frs = pp.fail_reasons(cv)
        self.assertEqual([("color-blob", "side-a", True)], [(f.group, f.view, f.numeric) for f in frs])

    def test_a_warn_or_pass_case_has_none(self):
        self.assertEqual([], pp.fail_reasons(pp.judge_case(_case("a--x--1", [_view(color=100)]))))
        self.assertEqual([], pp.fail_reasons(pp.judge_case(_case("a--x--1"))))

    def test_structure_skin_and_fail_floor_flags_are_qualitative(self):
        s = {"missingOnGhost": [{"key": "model/Bell", "mesh": "bell", "rendered": True}]}
        cv = pp.judge_case(_case("a--x--1", structure=s))
        self.assertEqual([("structure", None, False)], [(f.group, f.view, f.numeric) for f in pp.fail_reasons(cv)])
        skin = {"module": "ModulePartVariants", "requested": "A", "real": "A", "ghost": "B"}
        cv = pp.judge_case(_case("a--x--1", skin=skin))
        self.assertEqual(["skin"], [f.group for f in pp.fail_reasons(cv)])

    def test_a_capped_route_holds_nothing_but_its_structure(self):
        cv = pp.judge_case(_case("a--x--1", [_view(real=5000, pct=40.0)], tags=[pp.TAG_CONTEXT_DEPENDENT]))
        self.assertEqual(pp.VERDICT_WARN, cv.verdict)
        s = {"extraOnGhost": [{"key": "model/Wheel", "mesh": "w", "rendered": True}]}
        cv = pp.judge_case(_case("a--x--1", [_view(real=5000, pct=40.0)],
                                 tags=[pp.TAG_CONTEXT_DEPENDENT], structure=s))
        self.assertEqual(pp.VERDICT_FAIL, cv.verdict)
        self.assertEqual(["structure"], [f.group for f in pp.fail_reasons(cv)])

    def test_route_prefixes_reach_the_view_key(self):
        reasons = ("start:shape-blob-px:side-a=900", "start:shape-blob-pct:side-a=4.5",
                   "event:color-blob-px:top=20", "flag:start-pose-not-first-frame",
                   "flag:state-not-modelled:start:no-info")
        cv = pp.CaseVerdict("a--x--d--1", "a", None, None, pp.VERDICT_FAIL, reasons, None, 0, None, 0, "ok")
        got = [(f.group, f.view) for f in pp.fail_reasons(cv)]
        self.assertEqual([("shape-blob", "start:side-a"), ("shape-blob", "start:side-a"),
                          ("flag:start-pose-not-first-frame", None)], got)

    def test_an_unexplained_fail_fails_closed(self):
        cv = pp.CaseVerdict("a--x--1", "a", None, None, pp.VERDICT_FAIL, ("mystery",), None, 0, None, 0, "ok")
        frs = pp.fail_reasons(cv)
        self.assertEqual(1, len(frs))
        self.assertEqual(pp.GROUP_ANY, frs[0].group)
        self.assertTrue(frs[0].reason.startswith("unexplained-fail:"))


class DriftRuleTests(unittest.TestCase):

    def test_blob_drift_uses_the_larger_of_the_absolute_and_relative_allowance(self):
        band = [0, 0]
        self.assertEqual(0, pp.measure_drift("colorBlobPx", pp.DRIFT_COLOR_ABS_PX, band))
        self.assertEqual(1, pp.measure_drift("colorBlobPx", pp.DRIFT_COLOR_ABS_PX + 1, band))
        self.assertEqual(0, pp.measure_drift("realOnlyBlobPx", pp.DRIFT_SHAPE_ABS_PX, band))
        self.assertEqual(1, pp.measure_drift("ghostOnlyBlobPx", pp.DRIFT_SHAPE_ABS_PX + 1, band))
        big = [1000, 2000]
        limit = 2000 + pp.DRIFT_BLOB_REL * 2000
        self.assertEqual(0, pp.measure_drift("colorBlobPx", limit, big))
        self.assertEqual(1, pp.measure_drift("colorBlobPx", limit + 1, big))
        self.assertEqual(-1, pp.measure_drift("colorBlobPx", 1000 - 501, big))
        self.assertEqual(0, pp.measure_drift("colorBlobPx", 500, big))

    def test_iou_drift_is_a_drop_below_the_band(self):
        band = [0.95, 0.97]
        self.assertEqual(0, pp.measure_drift("iou", 0.95 - pp.DRIFT_IOU_ABS + 1e-6, band))
        self.assertEqual(1, pp.measure_drift("iou", 0.95 - pp.DRIFT_IOU_ABS - 1e-6, band))
        self.assertEqual(-1, pp.measure_drift("iou", 0.97 + pp.DRIFT_IOU_ABS + 1e-6, band))


class GateTests(unittest.TestCase):

    def test_an_unchanged_sweep_is_ok(self):
        run = {"a--x--1": _case("a--x--1", [_view(color=30)]), "b--x--2": _case("b--x--2")}
        g = _gate(run, _section([run]))
        self.assertTrue(g.ok, [f.text() for f in g.reds])
        self.assertEqual([], g.notes)

    def test_a_drift_past_tolerance_reds_and_names_the_measure(self):
        base = {"a--x--1": _case("a--x--1")}
        now = {"a--x--1": _case("a--x--1", [_view(ghost=pp.DRIFT_SHAPE_ABS_PX + 1)])}
        g = _gate(now, _section([base]))
        self.assertEqual([pp.GATE_DRIFT], _kinds(g.reds))
        self.assertIn("side-a ghostOnlyBlobPx", g.reds[0].detail)

    def test_a_drift_inside_tolerance_or_toward_better_passes(self):
        base = {"a--x--1": _case("a--x--1", [_view(color=4000)])}
        g = _gate({"a--x--1": _case("a--x--1", [_view(color=4100)])}, _section([base]))
        self.assertTrue(g.ok)
        g = _gate({"a--x--1": _case("a--x--1", [_view(color=0)])}, _section([base]))
        self.assertTrue(g.ok)
        self.assertEqual([pp.NOTE_IMPROVED, pp.NOTE_STALE_ACCEPTED][:1], _kinds(g.notes))

    def test_a_new_failing_case_reds_and_a_new_passing_case_is_a_note(self):
        base = {"a--x--1": _case("a--x--1")}
        now = {"a--x--1": _case("a--x--1"), "n--x--9": _case("n--x--9", [_view(color=999)]),
               "p--x--8": _case("p--x--8")}
        g = _gate(now, _section([base]))
        self.assertEqual([pp.GATE_UNACCEPTED_FAIL], _kinds(g.reds))
        self.assertEqual("n--x--9", g.reds[0].case_id)
        self.assertIn("new case", g.reds[0].detail)
        self.assertEqual(sorted([pp.NOTE_NEW_CASE, pp.NOTE_NEW_CASE]), _kinds(g.notes))

    def test_a_numeric_fail_inside_its_band_is_a_near_line_note(self):
        run1 = {"a--x--1": _case("a--x--1", [_view(color=240)])}
        run2 = {"a--x--1": _case("a--x--1", [_view(color=250)])}
        now = {"a--x--1": _case("a--x--1", [_view(color=270)])}
        g = _gate(now, _section([run1, run2]))
        self.assertTrue(g.ok, [f.text() for f in g.reds])
        self.assertEqual([pp.NOTE_NEAR_LINE_FAIL], _kinds(g.notes))

    def test_a_qualitative_fail_reds_unless_accepted(self):
        s = {"missingOnGhost": [{"key": "model/Bell", "mesh": "bell", "rendered": True}]}
        base = {"a--x--1": _case("a--x--1")}
        now = {"a--x--1": _case("a--x--1", structure=s)}
        g = _gate(now, _section([base]))
        self.assertEqual([pp.GATE_UNACCEPTED_FAIL], _kinds(g.reds))
        g = _gate(now, _section([base]), [_entry("a--x--1", measure="structure")])
        self.assertTrue(g.ok)
        self.assertEqual([pp.NOTE_ACCEPTED_FAIL], _kinds(g.notes))
        self.assertEqual(1, len(g.accepted))

    def test_a_noisy_wildcard_never_hides_a_missing_mesh_a_skin_mismatch_or_an_unexplained_fail(self):
        accepted = [_entry("a--x--1", "*", "*", noisy=True)]
        base = {"a--x--1": _case("a--x--1", [_view(color=500)])}
        mesh = {"missingOnGhost": [{"key": "model/Bell", "mesh": "bell", "rendered": True}]}
        skin = {"module": "ModulePartVariants", "requested": "A", "real": "A", "ghost": "B"}
        for name, doc in (("mesh", _case("a--x--1", [_view(color=900)], structure=mesh)),
                          ("skin", _case("a--x--1", [_view(color=900)], skin=skin))):
            with self.subTest(name):
                g = _gate({"a--x--1": doc}, _section([base]), accepted)
                self.assertEqual([pp.GATE_UNACCEPTED_FAIL], _kinds(g.reds))
        cv = pp.CaseVerdict("a--x--1", "a", None, None, pp.VERDICT_FAIL, ("mystery",), None, 0, None, 0, "ok")
        self.assertEqual(1, len(pp.unaccepted_fails([cv], "p", accepted)))

    def test_an_accepted_fail_still_gates_its_drift_unless_noisy(self):
        base = {"a--x--1": _case("a--x--1", [_view(color=500)])}
        now = {"a--x--1": _case("a--x--1", [_view(color=5000)])}
        accepted = [_entry("a--x--1", "side-a", "color-blob")]
        g = _gate(now, _section([base]), accepted)
        self.assertEqual([pp.GATE_DRIFT], _kinds(g.reds))
        noisy = [_entry("a--x--1", "side-a", "color-blob", noisy=True)]
        g = _gate(now, _section([base]), noisy)
        self.assertTrue(g.ok, [f.text() for f in g.reds])
        other_view = [_entry("a--x--1", "top", "color-blob", noisy=True)]
        g = _gate(now, _section([base]), other_view)
        self.assertFalse(g.ok)

    def test_a_case_the_sweep_no_longer_plans_reds_as_gone(self):
        base = {"a--x--1": _case("a--x--1"), "b--x--2": _case("b--x--2")}
        g = _gate({"a--x--1": _case("a--x--1")}, _section([base]))
        self.assertEqual([pp.GATE_GONE], _kinds(g.reds))
        self.assertEqual("b--x--2", g.reds[0].case_id)

    def test_no_baseline_section_reds_once(self):
        g = _gate({"a--x--1": _case("a--x--1", [_view(color=400)])}, None)
        self.assertEqual([pp.GATE_NO_BASELINE], _kinds(g.reds))

    def test_a_fault_on_a_baselined_case_never_passes(self):
        base = {"a--x--1": _case("a--x--1")}
        g = _gate({"a--x--1": _case("a--x--1", status="spawn-timeout")}, _section([base]))
        self.assertEqual(([], [pp.GATE_UNMEASURED]), (g.reds, _kinds(g.unmeasured)))
        # The ghost builder runs inside the prepare guard, so its exception reads crashed.
        g = _gate({"a--x--1": _case("a--x--1", status="crashed")}, _section([base]))
        self.assertEqual([pp.GATE_UNACCEPTED_FAIL], _kinds(g.reds))
        # A fault the baseline itself recorded, and one on a case it never held, are notes.
        for section in (_section([base, {"a--x--1": _case("a--x--1", status="spawn-timeout")}]),
                        _section([{"b--x--2": _case("b--x--2")}])):
            g = _gate({"a--x--1": _case("a--x--1", status="spawn-timeout")}, section,
                      planned=["a--x--1"] + [c for c in section["cases"] if c != "a--x--1"][:0])
            self.assertEqual([], [f for f in g.reds if f.kind != pp.GATE_GONE])
            self.assertEqual([], g.unmeasured)
            self.assertEqual([pp.NOTE_SETUP_FAULT], _kinds(g.notes))

    def test_a_planned_baselined_case_with_no_sidecar_is_unmeasured(self):
        base = {"a--x--1": _case("a--x--1"), "b--x--2": _case("b--x--2")}
        g = _gate({"a--x--1": _case("a--x--1")}, _section([base]), planned=["a--x--1", "b--x--2"])
        self.assertEqual([pp.GATE_UNMEASURED], _kinds(g.unmeasured))

    def test_what_the_capture_lost_is_unmeasured_never_a_pass(self):
        def state(**kw):
            v = _view("side-a", start={"realOnlyBlobPx": 0, "ghostOnlyBlobPx": 0, "colorBlobPx": 0, "iou": 1.0})
            doc = _case("a--x--1", [v], state="deployed", structure={"pairs": 1},
                        proof={"state": "deployed", "action": "Extend", "actionResult": "invoked", "reached": True,
                               "ghostEvent": "applied", "ghostStart": "applied", "events": [{"type": "x"}]})
            doc["structureStart"] = {"pairs": 1}
            doc.update(kw)
            return doc
        whole = _case("a--x--1", [_view("side-a"), _view("top")])
        m = _manifest(["a--x--1"])
        m["views"] = ["side-a", "top"]
        self.assertIsNone(pp.unmeasured_reason(whole, m))
        self.assertIsNone(pp.unmeasured_reason(state(), dict(m, views=["side-a"])))
        lost = {"view": (_case("a--x--1", [_view("side-a")]), m),
                "null": (_case("a--x--1", [_view("side-a"), _view("top", color=None)]), m),
                "throw": (_case("a--x--1", whole["views"], flags=["exception-capture-top"]), m),
                "error": (_case("a--x--1", whole["views"], flags=["capture-error:top"]), m),
                "structure": (dict(whole, structure=None), m),
                "start structure": (state(structureStart=None), dict(m, views=["side-a"])),
                "start route": (state(views=[_view("side-a")]), dict(m, views=["side-a"]))}
        for name, (doc, manifest) in lost.items():
            with self.subTest(name):
                self.assertIsNotNone(pp.unmeasured_reason(doc, manifest))

    def test_a_view_dropped_from_the_manifest_too_is_still_owed(self):
        base = {"a--x--1": _case("a--x--1", [_view("side-a"), _view("top")])}
        m = _manifest(["a--x--1"])
        m["views"] = ["side-a", "top"]
        section = pp.build_baseline_section([("r", m, base)])
        self.assertEqual(["side-a", "top"], section["views"])
        now = {"a--x--1": _case("a--x--1", [_view("top")])}
        cut = dict(m, views=["top"])
        g = pp.gate_sweep("pp1", "stock-minimal", cut, now, [pp.judge_case(now["a--x--1"])], section, [])
        self.assertEqual([pp.GATE_UNMEASURED], _kinds(g.unmeasured))

    def test_an_error_on_a_case_baselined_still_animating_is_not_a_note(self):
        base = {"a--x--1": _case("a--x--1", status="still-animating")}
        skin = {"module": "ModulePartVariants", "requested": "A", "real": "B", "ghost": "B"}
        g = _gate({"a--x--1": _case("a--x--1", status="still-animating", skin=skin)}, _section([base]))
        self.assertEqual([pp.GATE_UNMEASURED], _kinds(g.unmeasured))

    def test_a_hidden_renderer_whose_name_contains_mask_reds(self):
        hidden = {"renderedDiffers": [{"key": "model/obj_damask", "real": "true", "ghost": "false"}]}
        self.assertEqual(["hidden"], [f.group for f in pp.qualitative_differences(_case("a--x--1", structure=hidden))])

    def test_a_hidden_renderer_or_an_unmodelled_state_reds_on_a_passing_noisy_case(self):
        noisy = [_entry("a--x--1", "*", "*", noisy=True)]
        base = {"a--x--1": _case("a--x--1")}
        hidden = {"renderedDiffers": [{"key": "model/A", "field": "rendered", "real": "true", "ghost": "false"}]}
        drawn = {"renderedDiffers": [{"key": "model/A", "field": "rendered", "real": "false", "ghost": "true"}]}
        for name, doc, group in (
                ("hidden", _case("a--x--1", structure=hidden), "hidden"),
                ("drawn", _case("a--x--1", structure=drawn), "drawn"),
                ("not built", _case("a--x--1", flags=["ghost-not-built:start"]), "flag:ghost-not-built:start"),
                ("not modelled", _case("a--x--1", flags=["state-not-modelled:event:not-reached"]), "flag:state-not-modelled:*"),
                ("not recorded", _case("a--x--1", flags=["state-not-recorded"]), "flag:state-not-recorded")):
            with self.subTest(name):
                g = _gate({"a--x--1": doc}, _section([base]), noisy)
                self.assertEqual([pp.GATE_UNACCEPTED_FAIL], _kinds(g.reds))
                g = _gate({"a--x--1": doc}, _section([base]), [_entry("a--x--1", "*", group)])
                self.assertEqual([], g.reds)
        g = _gate({"a--x--1": _case("a--x--1", structure=hidden)}, _section([base]), [_entry("a--x--1", "*", "drawn")])
        self.assertEqual(1, len(g.reds), "accepting one direction does not accept the other")

    def test_a_blank_ghost_reds_on_a_noisy_case(self):
        base = {"a--x--1": _case("a--x--1", [_view(iou=0.6)])}
        noisy = [_entry("a--x--1", "*", "*", noisy=True)]
        self.assertEqual([], _gate({"a--x--1": _case("a--x--1", [_view(iou=0.31)])}, _section([base]), noisy).reds)
        g = _gate({"a--x--1": _case("a--x--1", [_view(iou=0.0)])}, _section([base]), noisy)
        self.assertEqual([pp.GATE_DRIFT], _kinds(g.reds))

    def test_a_blob_percentage_that_prints_as_its_line_is_a_numeric_reason(self):
        cv = pp.judge_case(_case("a--x--1", [_view(real=20, pct=0.2003)]))
        self.assertEqual(pp.VERDICT_FAIL, cv.verdict)
        self.assertEqual([("shape-blob", True)], [(f.group, f.numeric) for f in pp.fail_reasons(cv)])

    def test_the_profile_is_the_spec_s_else_the_manifest_s_without_a_slot_copy(self):
        self.assertEqual("stock-minimal", pp.profile_of({"profile": "stock-minimal-2"}, "stock-minimal"))
        self.assertEqual("stock-minimal", pp.profile_of({"profile": "stock-minimal-2"}, None))
        self.assertEqual("modded-compat", pp.profile_of({"profile": "modded-compat"}, None))
        self.assertIsNone(pp.profile_of({"profile": ".."}, None))

    def test_a_case_baselined_only_as_a_fault_is_judged_as_new(self):
        base = {"a--x--1": _case("a--x--1", status="spawn-timeout")}
        g = _gate({"a--x--1": _case("a--x--1", [_view(color=30)])}, _section([base]))
        self.assertTrue(g.ok)
        self.assertEqual([pp.NOTE_NEW_CASE], _kinds(g.notes))
        g = _gate({"a--x--1": _case("a--x--1", [_view(color=300)])}, _section([base]))
        self.assertEqual([pp.GATE_UNACCEPTED_FAIL], _kinds(g.reds))

    def test_an_accepted_entry_that_matched_nothing_is_a_stale_note(self):
        run = {"a--x--1": _case("a--x--1")}
        g = _gate(run, _section([run]), [_entry("a--x--1", measure="color-blob"),
                                         _entry("zz--x--1", measure="color-blob")])
        self.assertTrue(g.ok)
        self.assertEqual([pp.NOTE_STALE_ACCEPTED], _kinds(g.notes), "only the swept case is stale")

    def test_a_profile_scoped_entry_does_not_cover_another_profile(self):
        base = {"a--x--1": _case("a--x--1", [_view(color=300)])}
        accepted = [_entry("a--x--1", measure="color-blob", profile="modded-compat")]
        g = _gate({"a--x--1": _case("a--x--1", [_view(color=300)])}, None, accepted)
        self.assertEqual([pp.GATE_NO_BASELINE], _kinds(g.reds))
        self.assertEqual([], [f for f in g.notes if f.kind == pp.NOTE_ACCEPTED_FAIL])
        self.assertTrue(pp.unaccepted_fails([pp.judge_case(base["a--x--1"])], "stock-minimal", accepted))
        self.assertFalse(pp.unaccepted_fails([pp.judge_case(base["a--x--1"])], "modded-compat", accepted))

    def test_a_state_case_drifts_per_route(self):
        def state(start_real):
            v = _view("side-a", start={"realOnlyBlobPx": start_real, "ghostOnlyBlobPx": 0,
                                       "colorBlobPx": 0, "iou": 1.0})
            return _case("a--x--deployed--1", [v], state="deployed",
                         proof={"state": "deployed", "family": "deployable", "action": "Extend",
                                "actionResult": "invoked", "reached": True,
                                "ghostEvent": "applied", "ghostStart": "applied",
                                "events": [{"type": "x"}]})
        base = {"a--x--deployed--1": state(0)}
        g = _gate({"a--x--deployed--1": state(pp.DRIFT_SHAPE_ABS_PX + 5)}, _section([base]))
        self.assertEqual([pp.GATE_DRIFT], _kinds([f for f in g.reds]))
        self.assertTrue(g.reds[0].detail.startswith("start:side-a realOnlyBlobPx"))
        g = _gate({"a--x--deployed--1": state(pp.DRIFT_SHAPE_ABS_PX + 5)}, _section([base]),
                  [_entry("a--x--deployed--1", "event:side-a", "shape-blob", noisy=True)])
        self.assertFalse(g.ok, "an event-route entry does not cover the start route")


class FxGateTests(unittest.TestCase):
    """FX cases in the gate (design E.4, E.6)."""

    ID = "engine--default--burn-full--0000aaaa"

    def _case(self, **kw):
        doc = tp._fx_case(**kw)[0]
        doc["structure"] = doc.get("structure") or {"pairs": 1}
        return doc

    def _gate_of(self, doc, base=None, accepted=()):
        return _gate({self.ID: doc}, _section([{self.ID: base or doc}]), accepted)

    def test_a_baselined_reference_case_never_reds_on_drift_or_fails(self):
        base = self._case(reference="waterfall")
        worse = self._case(reference="waterfall", ghost=tp._shifted(), event=tp._broken("off"))
        for v in worse["views"]:
            v["realOnlyBlobPx"] = v["colorBlobPx"] = 99999
            v["iou"] = 0.0
        g = self._gate_of(worse, base)
        self.assertEqual(([], []), (g.reds, g.unmeasured))
        self.assertIn(pp.NOTE_REFERENCE, _kinds(g.notes))
        self.assertTrue(_section([{self.ID: base}])["cases"][self.ID]["reference"])
        self.assertNotIn("reference", _section([{self.ID: self._case()}])["cases"][self.ID])
        # Drift does red the same pictures on an ordinary case.
        plain = self._case()
        for v in plain["views"]:
            v["colorBlobPx"] = 99999
        self.assertTrue(self._gate_of(plain, self._case()).reds)

    def test_a_judged_case_that_turns_into_a_reference_is_not_a_pass(self):
        g = self._gate_of(self._case(reference="waterfall"), self._case())
        self.assertEqual([pp.GATE_UNMEASURED], _kinds(g.unmeasured))

    def test_a_body_fail_does_not_drop_the_fx_reasons(self):
        both = self._case(event=tp._broken("off"), ghost=tp._shifted())
        groups = {f.group for f in pp.fail_reasons(pp.judge_case(both))}
        self.assertTrue(groups & {pp.GROUP_SHAPE, pp.GROUP_STRUCTURE}, groups)
        self.assertIn("fx:presence", groups)
        # Accepting every body picture measure and structure does not accept the plume.
        accepted = [_entry(self.ID, "*", "*", noisy=True), _entry(self.ID, "*", "structure")]
        reds = self._gate_of(both, accepted=accepted).reds
        self.assertTrue(reds)
        self.assertTrue(all("fx-" in r.detail for r in reds), [r.detail for r in reds])

    def test_an_fx_only_fail_is_explained_by_its_measures(self):
        for name, measures in (("off", "presence"), ("short", "length")):
            with self.subTest(name):
                doc = self._case(event=tp._broken(name))
                cv = pp.judge_case(doc)
                self.assertEqual(pp.VERDICT_FAIL, cv.verdict)
                frs = pp.fail_reasons(cv)
                self.assertTrue(frs and all(f.group.startswith("fx:") for f in frs), frs)
                # Presence decides alone; a measure is judged by its band.
                self.assertEqual({name == "short"}, {f.numeric for f in frs})
                self.assertIn("fx:" + measures, {f.group for f in frs})
                self.assertTrue(self._gate_of(doc, self._case()).reds)
                accepted = [_entry(self.ID, "*", g, noisy=name == "short") for g in {f.group for f in frs}]
                self.assertEqual([], self._gate_of(doc, self._case(), accepted).reds)
                # An entry that is not noisy waives the fail, not the drift past its band.
                accepted = [_entry(self.ID, "*", g) for g in {f.group for f in frs}]
                self.assertEqual([pp.GATE_DRIFT] * 3 if name == "short" else [],
                                 _kinds(self._gate_of(doc, self._case(), accepted).reds))

    def test_a_missing_fx_asset_is_its_own_reason(self):
        structure = {"fxMissingOnGhost": [{"key": "fx/Plume", "rendered": True}], "fxExtraOnGhost": [],
                     "fxDiffers": [], "lightsDiffer": []}
        frs = pp.fail_reasons(pp.judge_case(self._case(structure=structure)))
        self.assertEqual([("fx:structure", "missing-fx:fx/Plume")], [(f.group, f.reason) for f in frs])

    def test_a_warn_level_fx_finding_is_not_a_fail_reason(self):
        cv = pp.judge_case(self._case(event=tp._broken("off")))
        warn = pp.FxEntryVerdict("event", "fx-side-a", "flame", pp.VERDICT_WARN,
                                 (pp.FxFinding("length", pp.VERDICT_WARN, "length=1.2"),), False)
        cv = pp.replace(cv, fx=pp.replace(cv.fx, entries=cv.fx.entries + (warn,)))
        self.assertNotIn("length=1.2", " ".join(f.reason for f in pp.fail_reasons(cv)))

    def test_an_fx_fail_with_nothing_to_show_fails_closed_beside_a_body_fail(self):
        cv = pp.judge_case(self._case(event=tp._broken("off"), ghost=tp._shifted()))
        blind = pp.replace(cv, fx=pp.replace(cv.fx, entries=(), structure={"missing": [], "extra": []}))
        unread = pp.replace(cv, fx=None)
        for case in (blind, unread):
            with self.subTest(fx=case.fx is not None):
                frs = pp.fail_reasons(case)
                self.assertTrue(any(f.reason.startswith("unexplained-fx:") for f in frs), frs)
                # No accepted entry covers an unexplained reason.
                everything = [_entry(self.ID, "*", "*", noisy=True), _entry(self.ID, "*", "structure")]
                self.assertTrue(pp.unaccepted_fails([case], "stock-minimal", everything))

    def test_a_capped_fx_fail_is_no_fail_reason_and_a_standing_one_is(self):
        # E.4: the unsettled entry's own fail is a warn; the same plume missing in the settled
        # views still reds, and nothing names the capped view.
        loosen = tp.FxJudgeTests._loosen
        doc = self._case(event=tp._broken("off"))
        loosen(doc, views=("fx-side-a",))
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_FAIL, cv.verdict)
        # The reason TEXT still holds the capped finding: only the judgement says it is capped.
        self.assertIn("event:fx-side-a:flame:fx-missing", cv.reasons)
        self.assertEqual({"event:%s" % v for v in pp.FX_VIEWS if v != "fx-side-a"},
                         {f.view for f in pp.fail_reasons(cv)})
        g = self._gate_of(doc, self._case())
        self.assertEqual(len(pp.FX_VIEWS) - 1, len(g.reds), [f.text() for f in g.reds])
        # Every entry of the missing plume unsettled: a warn, so no reason and no red.
        doc = self._case(event=tp._broken("off"))
        loosen(doc)
        cv = pp.judge_case(doc)
        self.assertEqual((pp.VERDICT_WARN, []), (cv.verdict, pp.fail_reasons(cv)))
        g = self._gate_of(doc, self._case())
        self.assertEqual(([], []), (g.reds, g.unmeasured))

    def test_a_body_fail_beside_capped_fx_fails_is_gated_by_its_body_reasons(self):
        # No unsettled sample is in a body route, so the cap never holds it.
        doc = self._case(event=tp._broken("off"))
        tp.FxJudgeTests._loosen(doc)
        doc["views"][0]["colorBlobPx"] = 10 ** 6
        cv = pp.judge_case(doc)
        self.assertEqual((pp.VERDICT_FAIL, pp.VERDICT_WARN), (cv.verdict, cv.fx.verdict))
        self.assertEqual({pp.GROUP_COLOR}, {f.group for f in pp.fail_reasons(cv)})
        self.assertEqual([pp.GATE_DRIFT], _kinds(self._gate_of(doc, self._case()).reds))

    def _weak_smoke(self, flame=None, **kw):
        """Two weak smoke cores with something drawn: every smoke entry is skipped."""
        smear = tp._fx_tile({(x, 0): (1, tp.GREY) for x in range(tp.FTILE)})
        real = {"flame": flame or tp.REAL_FX["flame"], "smoke": (smear, smear)}
        return self._case(real=real, **kw)

    def test_a_skipped_fx_entry_is_unmeasured_and_never_a_silent_pass(self):
        doc = self._weak_smoke()
        skipped = ["skipped:%s:%s:smoke" % (r, v) for r in ("event", "start") for v in pp.FX_VIEWS]
        self.assertEqual(tuple(sorted(skipped)), pp.fx_unmeasured(pp.judge_case(doc)))
        g = self._gate_of(doc, self._case())
        self.assertEqual(([], False), (g.reds, g.ok))
        self.assertEqual([pp.GATE_UNMEASURED], _kinds(g.unmeasured))
        self.assertIn("skipped:event:fx-far:smoke", g.unmeasured[0].detail)
        # A red beside it stays a red, and both are said.
        g = self._gate_of(self._weak_smoke(event={"flame": tp.FX_DARK}), self._case())
        self.assertEqual(({pp.GATE_UNACCEPTED_FAIL}, [pp.GATE_UNMEASURED]),
                         (set(_kinds(g.reds)), _kinds(g.unmeasured)))
        # No accepted entry makes it measured.
        g = self._gate_of(doc, self._case(), [_entry(self.ID, "*", "*", noisy=True),
                                              _entry(self.ID, "*", "fx:smoke")])
        self.assertEqual([pp.GATE_UNMEASURED], _kinds(g.unmeasured))

    def test_a_skip_the_baseline_runs_hold_is_a_note(self):
        doc = self._weak_smoke()
        entry = _section([{self.ID: doc}])["cases"][self.ID]
        self.assertEqual(8, len(entry["fxUnmeasured"]))
        self.assertNotIn("fxUnmeasured", _section([{self.ID: self._case()}])["cases"][self.ID])
        g = self._gate_of(doc, doc)
        self.assertTrue(g.ok)
        self.assertEqual([pp.NOTE_FX_UNMEASURED], _kinds(g.notes))
        # Only what the baseline holds: the same case with its flame skipped too is unmeasured.
        smear = tp._fx_tile({(x, 0): (1, tp.GREY) for x in range(tp.FTILE)})
        g = self._gate_of(self._weak_smoke(flame=(smear, smear)), doc)
        self.assertEqual([pp.GATE_UNMEASURED], _kinds(g.unmeasured))
        self.assertIn(":flame", g.unmeasured[0].detail)
        self.assertNotIn(":smoke", g.unmeasured[0].detail)
        # A new case says it too, as a note (as its other faults do).
        section = _section([{self.ID: doc}])
        section["cases"] = {}
        self.assertEqual([pp.NOTE_FX_UNMEASURED, pp.NOTE_NEW_CASE], _kinds(_gate({self.ID: doc}, section).notes))
        # The committed shape is checked.
        bad = {"schema": pp.BASELINE_SCHEMA, "profile": "p", "labels": {"pp1": {"cases": {
            self.ID: dict(entry, fxUnmeasured="x")}}}}
        self.assertTrue(any("fxUnmeasured" in e for e in pp.validate_baseline(bad, "p")))

    def test_a_layer_that_went_empty_on_both_sides_is_unmeasured(self):
        dark = dict(tp.REAL_FX, smoke=tp.FX_DARK)
        doc = self._case(real=dark)
        self.assertEqual(pp.VERDICT_PASS, pp.judge_case(doc).verdict)
        empty = {"empty:%s:%s:smoke" % (r, v) for r in ("event", "start") for v in pp.FX_VIEWS}
        self.assertEqual(empty, set(pp.fx_unmeasured(pp.judge_case(doc))))
        g = self._gate_of(doc, self._case())
        self.assertEqual(([], [pp.GATE_UNMEASURED]), (g.reds, _kinds(g.unmeasured)))
        self.assertIn("empty:event:fx-far:smoke", g.unmeasured[0].detail)
        # One view of it alone is enough.
        one = self._case()
        for e in one["fx"]["views"]:
            if (e["view"], e["layer"]) == ("fx-far", "flame"):
                for side in ("real", "ghost"):
                    e[side] = dict(e[side], corePx=0, envelopePx=0)
        g = self._gate_of(one, self._case())
        self.assertEqual([pp.GATE_UNMEASURED], _kinds(g.unmeasured))
        # A core with no envelope (or the reverse) is drawn.
        for key in ("corePx", "envelopePx"):
            for side in ("real", "ghost"):
                e = {"real": {"corePx": 0, "envelopePx": 0}, "ghost": {"corePx": 0, "envelopePx": 0}}
                self.assertTrue(pp.fx_entry_empty(e))
                e[side][key] = 1
                self.assertFalse(pp.fx_entry_empty(e))
        # No accepted entry makes it measured; the baseline's own empty layer is no finding.
        accepted = [_entry(self.ID, "*", "*", noisy=True), _entry(self.ID, "*", "fx:smoke"),
                    _entry(self.ID, "*", "fx:presence")]
        self.assertEqual([pp.GATE_UNMEASURED], _kinds(self._gate_of(doc, self._case(), accepted).unmeasured))
        g = self._gate_of(doc, doc)
        self.assertEqual((True, []), (g.ok, g.notes))
        # A layer only one side lost is a presence red, as ever.
        g = self._gate_of(self._case(event={"smoke": tp.FX_DARK}), self._case())
        self.assertEqual({pp.GATE_UNACCEPTED_FAIL}, set(_kinds(g.reds)))

    def test_an_empty_and_a_skipped_layer_are_one_state(self):
        # A stray particle decides which of two weak cores reads skipped and which empty.
        empty = self._case(real=dict(tp.REAL_FX, smoke=tp.FX_DARK))
        for now, base in ((empty, self._weak_smoke()), (self._weak_smoke(), empty)):
            g = self._gate_of(now, base)
            self.assertEqual(([], []), (g.reds, g.unmeasured))
        self.assertEqual({"skipped:a", "empty:a", "fx-x"}, pp.fx_known_tokens(["skipped:a", "fx-x"]))

    def _measured(self, key, value, view="fx-side-a", route="event", half=None, **kw):
        """A clean case with one compare value set, in both sample halves too (``half`` there)."""
        doc = self._case(**kw)
        for e in doc["fx"]["views"]:
            if (e["route"], e["view"], e["layer"]) == (route, view, "flame"):
                e["compare"][key] = value
                for h in pp.FX_SPLIT_HALF_KEYS:
                    e["splitHalf"][h]["compare"][key] = value if half is None else half
        return doc

    # key -> (view, identity, a warn-level value, a fail, the same fail on the other side)
    BANDED = {"originDeltaPx": ("fx-side-a", 0.0, 3.0, 9.0, -9.0),
              "lateralOffsetPx": ("fx-side-a", 0.0, 3.0, 9.0, -9.0),
              "lengthRatio": ("fx-side-a", 1.0, 1.2, 1.5, 0.5),
              "widthRatio": ("fx-side-a", 1.0, 1.25, 1.6, 0.4),
              "directionDeg": ("fx-side-a", 0.0, 8.0, 20.0, -20.0),
              "crossSectionAreaRatio": ("fx-bottom", 1.0, 1.5, 2.2, 0.2),
              "crossSectionOffset": ("fx-bottom", 0.0, 0.08, 0.2, None),
              "chromaDistance": ("fx-side-a", 0.0, 0.05, 0.12, None),
              "hueL1": ("fx-side-a", 0.0, 0.4, 1.0, None)}

    def test_a_banded_measure_reds_only_past_its_band(self):
        self.assertEqual(sorted(pp.FX_BAND_KEYS), sorted(self.BANDED))
        for key, (view, same, near, far, other) in self.BANDED.items():
            with self.subTest(key):
                def case(value, **kw):
                    return self._measured(key, value, view, **kw)
                worse = case(far)
                self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(worse).verdict)
                # Clean in every run: the fail line decides.
                g = self._gate_of(worse, self._case())
                self.assertEqual([pp.GATE_DRIFT], _kinds(g.reds), [f.text() for f in g.reds])
                # Capped in the runs (one half alone showed it), standing now: its band holds it.
                capped = case(far, half=same)
                self.assertEqual(pp.VERDICT_WARN, pp.judge_case(capped).verdict)
                g = self._gate_of(worse, capped)
                self.assertEqual(([], [], [pp.NOTE_NEAR_LINE_FAIL]), (g.reds, g.unmeasured, _kinds(g.notes)))
                band = _section([{self.ID: capped}])["cases"][self.ID]["fx"]
                self.assertEqual({"event:%s:flame" % view: {key: [far, far]}}, band)
                # Not the other route's, nor another view's, nor the other side's.
                moved = self._measured(key, far, view, "start")
                self.assertEqual([pp.GATE_DRIFT], _kinds(self._gate_of(moved, capped).reds))
                if other is not None:
                    self.assertEqual([pp.GATE_DRIFT], _kinds(self._gate_of(case(other), capped).reds))
                # Far past it: red, accepted or not, unless the entry is noisy.
                gone = case(same + 4 * (far - same))
                group = "fx:" + pp.fail_reasons(pp.judge_case(gone))[0].fx[1].measure
                self.assertEqual([pp.GATE_DRIFT], _kinds(self._gate_of(gone, capped).reds))
                self.assertEqual([pp.GATE_DRIFT], _kinds(self._gate_of(
                    gone, capped, [_entry(self.ID, "*", group)]).reds))
                self.assertEqual([], self._gate_of(gone, capped, [_entry(self.ID, "*", group, noisy=True)]).reds)
                # A new case has no band: an uncovered fail reds as one.
                section = _section([{self.ID: capped}])
                section["cases"] = {}
                self.assertEqual([pp.GATE_UNACCEPTED_FAIL], _kinds(_gate({self.ID: worse}, section).reds))
                # What only drifts under the line is no fail, so no red.
                self.assertEqual([], self._gate_of(case(near), self._case()).reds)

    def test_the_fx_limit_is_never_under_the_fail_line(self):
        limit = pp.fx_drift_limit
        self.assertEqual(1.0, limit(None, "hueL1", "colour:hueL1", 0.0))
        self.assertEqual(1.0, limit([0.0, 0.2], "hueL1", "colour:hueL1", 0.0))  # 0.4 of the line
        self.assertAlmostEqual(1.3, limit([0.0, 0.4], "hueL1", "colour:hueL1", 0.0))
        self.assertAlmostEqual(3.0, limit([0.0, 1.0], "hueL1", "colour:hueL1", 0.0))  # hi 2 x 1.5
        # A signed measure: each side by the band's end on it.
        self.assertAlmostEqual(2.7, limit([-18.0, 8.0], "directionDeg", "direction-", 0.0))
        self.assertAlmostEqual(1.3, limit([-18.0, 8.0], "directionDeg", "direction+", 0.0))
        # Pixels against the current plume's own line (10 % of its length here).
        self.assertAlmostEqual(1.5, limit([0.0, 40.0], "originDeltaPx", "origin+", 400.0))
        self.assertAlmostEqual(15.0, limit([0.0, 40.0], "originDeltaPx", "origin+", 10.0))

    def test_fx_severity_is_one_at_every_fail_line(self):
        # The judge's own line (fx_entry_findings) and the band's unit agree, both sides.
        for key, (view, same, _near, far, other) in self.BANDED.items():
            for value in (far, other):
                if value is None:
                    continue
                with self.subTest(key=key, value=value):
                    lo, hi = same, value
                    for _ in range(60):  # bisect the judge's line
                        mid = (lo + hi) / 2.0
                        fails = pp.judge_case(self._measured(key, mid, view)).verdict == pp.VERDICT_FAIL
                        lo, hi = (lo, mid) if fails else (mid, hi)
                    doc = self._measured(key, value, view)
                    length = next(e for e in doc["fx"]["views"] if e["view"] == view)["real"]["lengthPx"] or 0.0
                    self.assertAlmostEqual(1.0, pp.fx_severity(key, hi, length)[1], 6)
                    f = pp.fail_reasons(pp.judge_case(doc))[0].fx[1]
                    self.assertEqual((f.side, f.severity), pp.fx_severity(key, value, length))

    def test_only_what_is_judged_is_banded(self):
        def bands(*docs):
            return _section([{self.ID: d} for d in docs])["cases"][self.ID].get("fx")
        self.assertIsNone(bands(self._case()))
        self.assertIsNone(bands(self._measured("lengthRatio", 1.12)))
        # From half the line: a case near it in the runs may cross it by the tolerance.
        near = self._measured("lengthRatio", 1.2)
        self.assertEqual({"event:fx-side-a:flame": {"lengthRatio": [1.2, 1.2]}}, bands(near))
        for value, kind in ((1.32, pp.NOTE_NEAR_LINE_FAIL), (1.34, pp.GATE_DRIFT)):
            g = self._gate_of(self._measured("lengthRatio", value), near)
            self.assertEqual([kind], _kinds(g.reds + g.notes))
        self.assertEqual({"event:fx-side-a:flame": {"lengthRatio": [1.0, 1.3]}},
                         bands(self._case(), self._measured("lengthRatio", 1.3)))
        # A layer presence decides, or with two weak cores, has no measure to band.
        self.assertIsNone(bands(self._case(event=tp._broken("off"))))
        faint = self._weak_smoke()
        for e in faint["fx"]["views"]:
            if e["layer"] == "smoke":
                e["compare"]["lengthRatio"] = 3.0
        self.assertIsNone(bands(faint))
        # R4: what an rcs case only reports.
        rcs = self._measured("lengthRatio", 3.0, family="rcs", state="rcs-fire", rcs=tp.FxJudgeTests.RCS)
        self.assertIsNone(bands(rcs))
        self.assertIsNotNone(bands(self._measured("originDeltaPx", 9.0, family="rcs", state="rcs-fire",
                                                  rcs=tp.FxJudgeTests.RCS)))
        # A run that faulted adds nothing.
        fault = self._measured("lengthRatio", 3.0)
        fault["setup"] = {"status": "crashed", "detail": None}
        self.assertIsNone(bands(self._case(), fault))
        # The committed shape is checked.
        entry = _section([{self.ID: self._measured("lengthRatio", 1.3)}])["cases"][self.ID]
        def errs(**kw):
            return pp.validate_baseline({"schema": pp.BASELINE_SCHEMA, "profile": "p", "labels": {
                "pp1": {"cases": {self.ID: dict(entry, **kw)}}}}, "p")
        self.assertEqual([], errs())
        for bad in ("x", {"a": 1}, {"a": {"lengthRatio": [2, 1]}}, {"a": {"nonsense": [1, 2]}}):
            self.assertTrue(errs(fx=bad), bad)

    def _plate(self, color=0, start_color=0, **kw):
        """A lit lamp's plate case as the C# writes one: the one view ``plate``, both routes."""
        doc = self._case()
        tp.FxJudgeTests._as_family(doc, "light", "lit", plate={"litPct": 0.4}, views=None)
        view = dict(doc["views"][0], view=pp.PLATE_VIEW, colorBlobPx=color)
        view["start"] = dict(view["start"], colorBlobPx=start_color)
        doc.update({"context": pp.CONTEXT_PLATE, "views": [view]})
        doc.update(kw)
        return doc

    def _plate_gate(self, doc, base, accepted=()):
        m = dict(_manifest([self.ID]), views=list(tp.fx.VIEWS))
        section = pp.build_baseline_section([("r", m, {self.ID: base})])
        return pp.gate_sweep("pp7", "stock-minimal", m, {self.ID: doc}, [pp.judge_case(doc)],
                             section, list(accepted))

    def test_a_plate_case_is_measured_by_its_plate(self):
        g = self._plate_gate(self._plate(), self._plate())
        self.assertEqual((True, []), (g.ok, g.notes), [f.text() for f in g.unmeasured])
        # A ghost light that went out (or wrong) reds, on either route.
        for kw in ({"color": 900}, {"start_color": 900}):
            g = self._plate_gate(self._plate(**kw), self._plate())
            self.assertEqual([pp.GATE_DRIFT], _kinds(g.reds))
            self.assertIn(":plate colorBlobPx", g.reds[0].detail)
        # Its own measures are owed: no plate, a route lost, a null measure.
        whole = self._plate()["views"][0]
        for views in ([], [dict(whole, view="side-a")], [dict(whole, start=None)],
                      [dict(whole, colorBlobPx=None)], [dict(whole, start=dict(whole["start"], iou=None))]):
            g = self._plate_gate(self._plate(views=views), self._plate())
            self.assertEqual(([], [pp.GATE_UNMEASURED]), (g.reds, _kinds(g.unmeasured)), views)
        # The structure of both routes too, and a plate that reads under its floor.
        for key in ("structure", "structureStart"):
            g = self._plate_gate(self._plate(**{key: None}), self._plate())
            self.assertEqual([pp.GATE_UNMEASURED], _kinds(g.unmeasured))
        dim = self._plate()
        dim["fx"]["plate"] = {"litPct": 0.01}
        self.assertEqual([pp.GATE_UNMEASURED], _kinds(self._plate_gate(dim, self._plate()).unmeasured))
        # Only a plate case: any other still owes the manifest's views.
        other = self._plate(context="alone")
        self.assertIn("side-a", pp.unmeasured_reason(other, {"views": ["side-a"]}))
        # An accepted entry may name the view.
        row = {"case": self.ID, "view": "event:plate", "measure": "color-blob", "reason": "r x", "todo": "A-TODO"}
        self.assertEqual([], pp.parse_accepted({"schema": 1, "accepted": [row]})[1])

    def test_an_fx_check_that_did_not_run_is_unmeasured(self):
        lists = {"fxMissingOnGhost": None, "fxExtraOnGhost": [], "fxDiffers": [], "lightsDiffer": []}
        doc = self._case(structure=lists)
        self.assertEqual((pp.FLAG_FX_STRUCTURE_NOT_MEASURED,), pp.fx_unmeasured(pp.judge_case(doc)))
        self.assertEqual([pp.GATE_UNMEASURED], _kinds(self._gate_of(doc, self._case()).unmeasured))
        self.assertTrue(self._gate_of(doc, doc).ok)
        dark = self._case(real=tp.NO_FX)
        lost = pp.fx_unmeasured(pp.judge_case(dark))
        self.assertEqual({pp.FLAG_FX_NO_REAL_EFFECT}, {t for t in lost if not t.startswith("empty:")})
        self.assertEqual(16, len(lost) - 1)
        self.assertEqual(set(pp.FX_FLAGS_RAISE_TO_WARN), set(pp._FX_NOT_RUN_FLAGS) | {pp.FLAG_FX_WEAK_CORE})

    def test_a_reference_case_or_entry_and_one_weak_core_owe_no_fx_measure(self):
        # A Waterfall case is never gated, its skipped entries included.
        ref = self._weak_smoke(reference="waterfall")
        self.assertEqual((), pp.fx_unmeasured(pp.judge_case(ref)))
        g = self._gate_of(ref, self._case(reference="waterfall"))
        self.assertEqual(([], [], [pp.NOTE_REFERENCE]), (g.reds, g.unmeasured, _kinds(g.notes)))
        # A lamp's FX entries are reference only.
        cv = pp.judge_case(self._weak_smoke())
        shown = pp.replace(cv, fx=pp.replace(cv.fx, entries=tuple(
            pp.replace(e, reference=True) for e in cv.fx.entries)))
        self.assertEqual((), pp.fx_unmeasured(shown))
        # One weak core: every measure is judged (E.4), so nothing is owed.
        small = {"flame": (tp._plume(length=8, width=8), tp._disc(2))}
        cv = pp.judge_case(self._case(event=small))
        self.assertTrue(any(f.startswith(pp.FLAG_FX_WEAK_CORE) for f in cv.fx.flags), cv.fx.flags)
        self.assertEqual((), pp.fx_unmeasured(cv))

    def test_the_accepted_list_takes_fx_measures_and_views_and_no_others(self):
        def errs(**kw):
            row = {"case": self.ID, "view": "event:fx-side-a", "measure": "fx:length", "reason": "r x", "todo": "A-TODO"}
            row.update(kw)
            return pp.parse_accepted({"schema": 1, "accepted": [row]})[1]
        self.assertEqual([], errs())
        self.assertEqual([], errs(measure="fx:structure", view="*"))
        self.assertTrue(errs(measure="fx:nonsense"))
        self.assertTrue(errs(view="event:fx-nonsense"))
        self.assertFalse(_entry(self.ID, "*", "*").covers(self.ID, None, "event:fx-side-a", "fx:length"))


class EvaluateSweepGateTests(unittest.TestCase):

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="pp-gate-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _sweep(self, **kw):
        fx.build_sweep(self.tmp, **kw)
        names = sorted(os.listdir(self.tmp))
        sidecars = {}
        for n in names:
            if n.endswith(pp.SIDECAR_SUFFIX):
                with open(os.path.join(self.tmp, n), encoding="utf-8") as fh:
                    sidecars[n[:-len(pp.SIDECAR_SUFFIX)]] = json.load(fh)
        with open(os.path.join(self.tmp, pp.MANIFEST_FILE), encoding="utf-8") as fh:
            manifest = json.load(fh)
        atlases = tuple(n for n in names if n.endswith(pp.ATLAS_SUFFIX) and not n.endswith(pp.THUMB_SUFFIX))
        return pp.SweepInput("pp1", True, manifest, None, sidecars, atlases)

    def _eval(self, sweep, gating, gate_input):
        return pp.evaluate_sweep(sweep, "run", gating, gui_mirror.read_png,
                                 lambda n: os.path.join(self.tmp, n), gate_input)

    def _section_of(self, sweep):
        return pp.build_baseline_section([("r", sweep.manifest, sweep.sidecars)])

    def test_armed_with_a_matching_baseline_passes_and_reports_the_gate(self):
        sweep = self._sweep(parts=3)
        res = self._eval(sweep, True, pp.GateInput("p", self._section_of(sweep)))
        self.assertEqual(pp.STATUS_PASS, res.status)
        self.assertTrue(res.gate.ok)
        doc = pp.verdicts_document(res, "run")
        self.assertTrue(doc["gate"]["ok"])
        self.assertIn("gate=ok", pp.format_summary(pp.combine([res], True)))
        self.assertTrue(pp.detail_record(pp.combine([res], True))["sweeps"][0]["gate"]["ok"])

    def test_armed_a_failing_case_the_list_accepts_passes_and_one_it_does_not_fails(self):
        # A 2 px shift keeps IoU at the noisy floor (0.5); a larger one reds noisy cases too.
        sweep = self._sweep(parts=2, part_ghost_shift=2)
        clean = self._sweep(parts=2)
        section = self._section_of(clean)
        ids = sorted(c for c in sweep.sidecars if c not in pp.ALL_CONTROL_IDS)
        sweep = self._sweep(parts=2, part_ghost_shift=2)
        res = self._eval(sweep, True, pp.GateInput("p", section))
        self.assertEqual(pp.STATUS_FAIL, res.status)
        self.assertTrue(any(p.startswith("gate:") for p in res.problems))
        # Accepting the shape difference waives the fail; a noisy entry also waives the
        # drift the shift causes against the clean baseline.
        accepted = tuple(_entry(c, measure="*", noisy=True) for c in ids)
        res = self._eval(sweep, True, pp.GateInput("p", section, accepted))
        self.assertEqual(pp.STATUS_PASS, res.status, [f.text() for f in res.gate.reds])

    def test_report_only_records_the_gate_and_never_fails(self):
        sweep = self._sweep(parts=2, part_ghost_shift=3)
        res = self._eval(sweep, False, pp.GateInput("p", None))
        self.assertEqual(pp.STATUS_REPORT, res.status)
        self.assertFalse(res.gate.ok)

    def test_an_unmeasured_case_is_invalid_and_a_red_outranks_it(self):
        sweep = self._sweep(parts=2)
        section = self._section_of(sweep)
        ids = sorted(c for c in sweep.sidecars if c not in pp.ALL_CONTROL_IDS)
        sweep.sidecars[ids[0]]["views"][0]["colorBlobPx"] = None
        res = self._eval(sweep, True, pp.GateInput("p", section))
        self.assertEqual((pp.STATUS_INVALID, pp.INVALID_INCOMPLETE_SUBKIND), (res.status, res.invalid_subkind))
        sweep.sidecars[ids[1]]["structure"]["missingOnGhost"] = [{"key": "m", "mesh": "x", "rendered": True}]
        self.assertEqual(pp.STATUS_FAIL, self._eval(sweep, True, pp.GateInput("p", section)).status)

    def test_a_read_error_reds_an_armed_sweep(self):
        sweep = self._sweep(parts=2)
        res = self._eval(sweep, True, pp.GateInput("p", self._section_of(sweep), (), ("accepted.toml unreadable",)))
        self.assertEqual(pp.STATUS_FAIL, res.status)

    def test_without_a_gate_input_the_pre_p5_rule_holds(self):
        sweep = self._sweep(parts=2, part_ghost_shift=3)
        self.assertEqual(pp.STATUS_FAIL, self._eval(sweep, True, None).status)
        self.assertIsNone(self._eval(sweep, True, None).gate)


class BaselineToolTests(unittest.TestCase):

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="pp-tool-")
        self.base_dir = os.path.join(self.tmp, "partparity")
        os.makedirs(self.base_dir)
        # The fake sweeps carry no controls; the instrument checks have their own cell below.
        ok = mock.Mock(problems=[])
        for name in ("check_controls", "check_completeness"):
            patcher = mock.patch.object(pp, name, return_value=ok)
            patcher.start()
            self.addCleanup(patcher.stop)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _sweep_dir(self, name, cases, label="pp1", complete=True):
        d = os.path.join(self.tmp, name + "_partparity", label)
        os.makedirs(d)
        m = _manifest(sorted(cases), label)
        m["progress"]["complete"] = complete
        with open(os.path.join(d, pp.MANIFEST_FILE), "w", encoding="utf-8") as fh:
            json.dump(m, fh)
        for cid, doc in cases.items():
            with open(os.path.join(d, cid + pp.SIDECAR_SUFFIX), "w", encoding="utf-8") as fh:
                json.dump(doc, fh)
        return d

    def _run(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            rc = ppb.main(["--baseline-dir", self.base_dir] + list(argv))
        return rc, out.getvalue(), err.getvalue()

    def _accept(self, text):
        with open(os.path.join(self.base_dir, pp.ACCEPTED_FILE), "w", encoding="utf-8") as fh:
            fh.write("schema = 1\n" + text)

    def test_write_refuses_an_unaccepted_fail_and_prints_a_skeleton_that_will_not_parse(self):
        d = self._sweep_dir("r1", {"a--x--1": _case("a--x--1", [_view(color=400)])})
        rc, out, err = self._run("write", "--label", "pp1", d)
        self.assertEqual(ppb.EXIT_UNACCEPTED, rc)
        self.assertIn("[[accepted]]", out)
        self.assertFalse(os.path.exists(os.path.join(self.base_dir, "baseline-stock-minimal.json")))
        _entries, errors = pp.parse_accepted(tomllib.loads("schema = 1\n" + out))
        self.assertTrue(errors, "the skeleton must be completed by hand")

    def test_write_then_check_round_trip_and_other_labels_are_kept(self):
        self._accept('[[accepted]]\ncase = "a--x--1"\nview = "side-a"\nmeasure = "color-blob"\n'
                     'reason = "seam line"\ntodo = "SOME-TODO-1"\nnoisy = true\n')
        r1 = self._sweep_dir("r1", {"a--x--1": _case("a--x--1", [_view(color=400)]), "b--x--2": _case("b--x--2")})
        r2 = self._sweep_dir("r2", {"a--x--1": _case("a--x--1", [_view(color=300)]), "b--x--2": _case("b--x--2")})
        other = self._sweep_dir("o1", {"c--x--3": _case("c--x--3")}, label="pp3")
        self.assertEqual(0, self._run("write", "--label", "pp3", other)[0])
        rc, out, err = self._run("write", "--label", "pp1", r1, r2)
        self.assertEqual(0, rc, err)
        path = os.path.join(self.base_dir, "baseline-stock-minimal.json")
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
        self.assertEqual([], pp.validate_baseline(doc, "stock-minimal"))
        self.assertEqual(["pp1", "pp3"], sorted(doc["labels"]))
        self.assertEqual(["r1", "r2"], doc["labels"]["pp1"]["runs"])
        self.assertEqual({"side-a": {"colorBlobPx": [300, 400]}}, doc["labels"]["pp1"]["cases"]["a--x--1"]["views"])
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
        self.assertEqual(ppb.render_baseline(doc), text, "the file is the canonical rendering")
        self.assertEqual(0, self._run("check", r2)[0])
        bad = self._sweep_dir("r3", {"a--x--1": _case("a--x--1"), "b--x--2": _case("b--x--2", [_view(real=500)])})
        rc, out, _ = self._run("check", bad)
        self.assertEqual(ppb.EXIT_RED, rc)
        self.assertIn("RED  drift b--x--2", out)

    def test_write_refuses_mixed_labels_and_incomplete_sweeps(self):
        d1 = self._sweep_dir("r1", {"a--x--1": _case("a--x--1")})
        d2 = self._sweep_dir("r2", {"a--x--1": _case("a--x--1")}, label="pp3")
        self.assertEqual(ppb.EXIT_USAGE, self._run("write", "--label", "pp1", d1, d2)[0])
        d3 = self._sweep_dir("r3", {"a--x--1": _case("a--x--1")}, complete=False)
        self.assertEqual(ppb.EXIT_USAGE, self._run("write", "--label", "pp1", d3)[0])

    def test_write_refuses_a_run_whose_controls_or_completeness_fail(self):
        d = self._sweep_dir("r1", {"a--x--1": _case("a--x--1")})
        for name in ("check_controls", "check_completeness"):
            with mock.patch.object(pp, name, return_value=mock.Mock(problems=["bad"])):
                self.assertEqual(ppb.EXIT_USAGE, self._run("write", "--label", "pp1", d)[0], name)
        self.assertEqual(0, self._run("write", "--label", "pp1", d)[0])

    def test_an_fx_sweep_is_pinned_from_the_archive(self):
        # The archive keeps no atlas: the FX one and the strip are taken as present too.
        cid = FxGateTests.ID
        clean = FxGateTests()._case()
        near = FxGateTests()._measured("lengthRatio", 1.3)
        capped = FxGateTests()._measured("hueL1", 0.8, half=0.1)
        for doc, name in ((clean, "c.strip.png"), (near, "n.strip.png")):
            doc["strip"] = {"file": name}
        r1 = self._sweep_dir("f1", {cid: clean}, label="pp7")
        r2 = self._sweep_dir("f2", {cid: near}, label="pp7")
        with mock.patch.object(pp, "check_completeness", return_value=mock.Mock(problems=[])) as seen:
            self._accept('[[accepted]]\ncase = "%s"\nview = "*"\nmeasure = "fx:length"\n'
                         'reason = "known"\ntodo = "SOME-TODO-1"\n' % cid)
            rc, out, err = self._run("write", "--label", "pp7", r1, r2)
        self.assertEqual(0, rc, err)
        for call, doc in zip(seen.call_args_list, (clean, near)):
            for block in ("atlas", "fxAtlas", "strip"):
                self.assertIn(doc[block]["file"], call.args[4])
        self.assertIn("fxBanded=1", out)
        # A band past its fail line is said, capped or accepted.
        self.assertIn("fx band past its line: %s event:fx-side-a:flame lengthRatio [1, 1.3] (1.20 lines)" % cid, out)
        self.assertEqual(0, self._run("check", r2)[0])
        # spread names the FX movement in fail lines.
        rc, out, _ = self._run("spread", r1, r2)
        self.assertIn("fx (fail lines): n=", out)
        self.assertIn("d=1.20       %s event:fx-side-a:flame lengthRatio 0.00 -> 1.20" % cid, out)
        # A fail the sample halves do not repeat needs no accepted entry, and its band holds it.
        r3 = self._sweep_dir("f3", {cid: capped}, label="pp7")
        rc, out, err = self._run("write", "--label", "pp7", r3)
        self.assertEqual(0, rc, err)
        self.assertIn("hueL1 [0.8, 0.8] (1.60 lines)", out)

    def test_run_id_of_both_layouts(self):
        self.assertEqual("2026-11-22_0033_PP-1", ppb.run_id_of(os.path.join("x", "2026-11-22_0033_PP-1_partparity", "pp1")))
        self.assertEqual("2026-11-22_0033_PP-1", ppb.run_id_of(os.path.join("x", "2026-11-22_0033_PP-1", "partparity", "pp1")))

    def test_spread_reports_the_largest_moves(self):
        a = {"a--x--1": _case("a--x--1", [_view(color=10)])}
        b = {"a--x--1": _case("a--x--1", [_view(color=500)])}
        lines = ppb.spread_table(a, b)
        self.assertTrue(any(l.startswith("colorBlobPx: n=1 nonzero=1") for l in lines), lines)
        self.assertTrue(any("DRIFT" in l for l in lines))
        lines = ppb.spread_table(a, b, skip=[_entry("a--x--1", measure="color-blob", noisy=True)])
        self.assertTrue(any(l.startswith("colorBlobPx: no pairs") for l in lines), lines)


class ViewerPinnedModeTests(unittest.TestCase):

    def test_pinned_mode_marks_moved_new_same_and_gone(self):
        base = {"a--x--1": _case("a--x--1"), "b--x--2": _case("b--x--2"), "g--x--4": _case("g--x--4")}
        section = _section([base])
        doc = {"schema": pp.BASELINE_SCHEMA, "profile": "stock-minimal", "labels": {"pp1": section}}
        now = {"a--x--1": _case("a--x--1"), "b--x--2": _case("b--x--2", [_view(real=900)]),
               "n--x--3": _case("n--x--3")}
        sweep = {"profile": "stock", "dir": "d", "manifest": _manifest(sorted(now)), "sidecars": now,
                 "imageUrl": None}
        data = ppv.build_site_data([sweep], None, None, {"dir": "pinned", "baselines": {"stock-minimal": doc},
                                                         "accepted": [], "errors": []})
        status = {r["caseId"]: r["regression"]["status"] for r in data["cases"]}
        self.assertEqual({"a--x--1": "same", "b--x--2": "moved", "n--x--3": "new"}, status)
        self.assertEqual(["stock/g--x--4"], data["regression"]["gone"])
        self.assertEqual({"moved": 1, "new": 1, "same": 1}, data["regression"]["counts"])
        self.assertTrue(data["regression"]["pinned"])
        now["a--x--1"] = _case("a--x--1", [_view(color=None)])
        data = ppv.build_site_data([dict(sweep, sidecars=now)], None, None, {
            "dir": "pinned", "baselines": {"stock-minimal": doc}, "accepted": [], "errors": []})
        self.assertEqual("moved", {r["caseId"]: r["regression"]["status"] for r in data["cases"]}["a--x--1"])


class CommittedFilesTests(unittest.TestCase):
    """The committed baseline files and accepted list stay usable, and every armed lane
    has what its gate reads."""

    def _accepted(self):
        path = os.path.join(COMMITTED_DIR, pp.ACCEPTED_FILE)
        with open(path, "rb") as fh:
            return pp.parse_accepted(tomllib.load(fh))

    def _baselines(self):
        out = {}
        for name in sorted(os.listdir(COMMITTED_DIR)):
            m = re.match(r"^baseline-(.+)\.json$", name)
            if m:
                with open(os.path.join(COMMITTED_DIR, name), encoding="utf-8") as fh:
                    out[m.group(1)] = json.load(fh)
        return out

    def test_the_accepted_list_parses(self):
        entries, errors = self._accepted()
        self.assertEqual([], errors)
        self.assertTrue(entries)

    def test_every_accepted_todo_id_is_a_todo_header(self):
        with open(TODO_DOC, encoding="utf-8") as fh:
            headers = set(re.findall(r"^## ~*([A-Z0-9][A-Z0-9-]*[A-Z0-9])", fh.read(), re.MULTILINE))
        entries, _ = self._accepted()
        missing = sorted({e.todo for e in entries} - headers)
        self.assertEqual([], missing, "accepted.toml names todo ids with no ## header")

    def test_every_baseline_file_validates_and_renders_canonically(self):
        for profile, doc in self._baselines().items():
            with self.subTest(profile):
                self.assertEqual([], pp.validate_baseline(doc, profile))
                with open(os.path.join(COMMITTED_DIR, pp.baseline_file_name(profile)), encoding="utf-8") as fh:
                    self.assertEqual(ppb.render_baseline(doc), fh.read())

    def test_every_accepted_case_is_baselined_on_its_profile(self):
        baselines = self._baselines()
        entries, _ = self._accepted()
        for e in entries:
            profiles = [e.profile] if e.profile else list(baselines)
            found = any(e.case in (sec.get("cases") or {})
                        for p in profiles for sec in ((baselines.get(p) or {}).get("labels") or {}).values())
            self.assertTrue(found, "accepted case %s is in no baseline (typo?)" % e.case)

    def test_no_committed_entry_hides_a_missing_mesh(self):
        # A missing or hidden renderer, a ghost not built and a state not recorded must red on
        # EVERY baselined case, the accepted ones included.
        entries, _ = self._accepted()
        for group in (pp.GROUP_STRUCTURE, pp.GROUP_SKIN, pp.GROUP_HIDDEN, "flag:ghost-not-built",
                      "flag:ghost-not-built:start", "flag:state-not-recorded"):
            for profile, doc in self._baselines().items():
                for label, sec in doc["labels"].items():
                    covered = [c for c in sec["cases"] if any(e.covers(c, profile, None, group) for e in entries)]
                    self.assertEqual([], covered, "%s %s/%s" % (group, profile, label))

    def test_the_stored_tolerances_are_the_live_ones(self):
        for profile, doc in self._baselines().items():
            for label, sec in doc["labels"].items():
                self.assertEqual(pp.drift_tolerances(), sec["tolerances"], "%s/%s" % (profile, label))

    def test_every_armed_part_parity_lane_has_a_baseline_section_per_label(self):
        baselines = self._baselines()
        armed = []
        for name in sorted(os.listdir(SCENARIOS)):
            if not name.endswith(".toml"):
                continue
            with open(os.path.join(SCENARIOS, name), "rb") as fh:
                spec = tomllib.load(fh)
            if not pp.gating_armed(spec.get("expectations") or {}):
                continue
            armed.append(name)
            doc = baselines.get(spec.get("instanceProfile"))
            self.assertIsNotNone(doc, "%s is armed but has no baseline file" % name)
            for label in pp.sweep_labels(spec):
                self.assertIn(label, doc["labels"], "%s is armed but label %s has no baseline" % (name, label))
        self.assertTrue(armed, "P5 armed no lane")


if __name__ == "__main__":
    unittest.main()
