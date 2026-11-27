#!/usr/bin/env python3
"""Unit tests for the part ghost parity viewer (`harness/tools/part_parity_view.py`).

docs/dev/design-part-ghost-parity.md section 8. The pure core (case rows, trigger lines,
part / skin grouping, filters' value sets, regression diff, cross-profile links, the
data.js literal) is tested directly; one cell runs the CLI over a synthetic sweep
directory in a temp dir and checks every file it writes. When `node` is on PATH the
written data.js and the page's script are also put through `node --check`.

Lives under `lib/` because CI discovers `lib/` only (the `test_gui_tree_view.py`
placement). Stdlib only::

    cd harness && python -m unittest discover -s lib -q
"""

import contextlib
import copy
import html.parser
import io
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
import zlib
from unittest import mock

_HERE = os.path.dirname(os.path.abspath(__file__))
_TOOLS = os.path.join(os.path.dirname(_HERE), "tools")
for _p in (_HERE, _TOOLS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import partparity as pp  # noqa: E402
import part_parity_view as ppv  # noqa: E402


def view(name, real_blob=0, ghost_blob=0, color_blob=0, iou=1.0, pct=None):
    return {"view": name, "width": 512, "height": 512, "realPx": 1000, "ghostPx": 1000,
            "andPx": 1000, "orPx": 1000, "realOnlyPx": real_blob, "ghostOnlyPx": ghost_blob,
            "realOnlyTolPx": real_blob, "ghostOnlyTolPx": ghost_blob,
            "realOnlyBlobPx": real_blob, "ghostOnlyBlobPx": ghost_blob,
            "realOnlyBlobPct": pct if pct is not None else 0,
            "ghostOnlyBlobPct": 0, "bbox": {"real": [1, 1, 2, 2], "ghost": [1, 1, 2, 2]},
            "centroid": {"real": [1.5, 1.5], "ghost": [1.5, 1.5]},
            "touchesBorder": {"real": False, "ghost": False}, "colorOverlapPx": 900,
            "colorOverPx": color_blob, "colorBlobPx": color_blob, "colorP99": 0,
            "colorBlockMax": 0, "iou": iou, "matte": None}


def sidecar(case_id, part, *, title=None, source="Squad", views=None, status="ok",
            skin=None, missing=(), tags=(), category="Engine"):
    views = [view(v) for v in ("side-a", "side-b", "top", "bottom")] if views is None else views
    return {
        "schema": pp.CASE_SCHEMA, "caseId": case_id, "part": part, "title": title or part,
        "source": source, "partUrl": "Squad/Parts/%s" % part, "modelUrls": ["Squad/m/%s" % part],
        "category": category, "tags": list(tags),
        "skin": skin or {"module": None, "moduleId": None, "requested": None, "real": None,
                         "snapshot": "", "ghost": None},
        "state": "stowed", "context": "alone", "measure": {},
        "atlas": {"file": case_id + ".png", "thumb": case_id + ".thumb.png", "tile": 512,
                  "rows": ["real", "ghost"], "cols": ["side-a", "side-b", "top", "bottom"]},
        "framing": None, "ghostRootOffsetM": 0, "layers": {"real": [0], "ghost": [0]},
        "flags": [], "views": views,
        "structure": {"realRenderers": 1, "ghostRenderers": 1, "pairs": 1,
                      "missingOnGhost": [{"key": k, "mesh": "m", "rendered": True} for k in missing],
                      "extraOnGhost": [], "transformDiffers": [], "materialDiffers": [],
                      "meshDiffers": [], "renderedDiffers": []},
        "setup": {"status": status, "detail": None},
        "log": ["[Parsek][INFO][PartParity] line </script> for %s" % case_id],
    }


def variant_skin(requested, real=None, ghost=None, module="ModulePartVariants"):
    real = requested if real is None else real
    ghost = real if ghost is None else ghost
    return {"module": module, "moduleId": None, "requested": requested, "real": real,
            "snapshot": real, "ghost": ghost}


def manifest(cases, label="pp1", profile="..", controls_ok=True):
    return {
        "schema": pp.MANIFEST_SCHEMA, "label": label, "profile": profile,
        "gameDataMods": ["Squad", "Parsek"], "dllHash": "ab" * 32, "tile": 512,
        "views": ["side-a", "side-b", "top", "bottom"], "measure": {},
        "settings": {"skins": "all"},
        "controls": [{"id": c, "subject": "subject of %s" % c, "required": "req",
                      "outcome": "ok" if controls_ok or c != "ctl-ghost-flip" else "bad",
                      "detail": ""} for c in pp.CONTROL_IDS],
        "cases": [{"index": i, "caseId": cid, "part": part, "title": part, "source": "Squad",
                   "skin": skin, "state": "stowed", "context": "alone",
                   "sidecar": cid + pp.SIDECAR_SUFFIX, "setupStatus": "ok",
                   "maxShapeBlobPx": 0, "maxColorBlobPx": 0, "minIou": 1,
                   "missingRendered": 0, "extraRendered": 0}
                  for i, (cid, part, skin) in enumerate(cases)],
        "exclusions": [{"part": "flag", "reason": "flag"}],
        "progress": {"complete": True, "cases": len(cases), "completeCases": len(cases),
                     "setupFaults": 0, "controls": "ok" if controls_ok else "bad", "crashed": []},
    }


# The synthetic sweep: a pass part, a fail part (missing rendered mesh), a variant part
# with a base, a warn variant and a B9 subtype, a crashed case, a case the manifest lists
# with no sidecar, and a sidecar the manifest does not list.
CASES = [
    ("alpha--default--00000001", "Alpha", "default"),
    ("bravo--default--00000002", "Bravo", "default"),
    ("tank--basic--00000003", "Tank", "Basic"),
    ("tank--gold--00000004", "Tank", "Gold"),
    ("tank--plume-a--00000005", "Tank", "plume=A"),
    ("crashy--default--00000006", "Crashy", "default"),
    ("ghosty--default--00000007", "Ghosty", "default"),
]


def sweep_sidecars():
    sc = {
        "alpha--default--00000001": sidecar("alpha--default--00000001", "Alpha"),
        "bravo--default--00000002": sidecar("bravo--default--00000002", "Bravo",
                                            missing=["model/skirt"]),
        "tank--basic--00000003": sidecar("tank--basic--00000003", "Tank",
                                         skin=variant_skin("Basic")),
        "tank--gold--00000004": sidecar(
            "tank--gold--00000004", "Tank", skin=variant_skin("Gold"),
            views=[view("side-a", color_blob=100), view("side-b"), view("top"), view("bottom")]),
        "tank--plume-a--00000005": sidecar(
            "tank--plume-a--00000005", "Tank",
            skin={"module": "ModuleB9PartSwitch", "moduleId": "plume", "requested": "A",
                  "real": "A", "snapshot": "A", "ghost": None}),
        "crashy--default--00000006": sidecar("crashy--default--00000006", "Crashy",
                                             status="crashed", views=[]),
        "orphan--default--00000099": sidecar("orphan--default--00000099", "Orphan"),
    }
    for cid in pp.CONTROL_IDS:
        sc[cid] = sidecar(cid, "fuelTankSmallFlat", tags=["control"])
    return sc


def build(**kw):
    sweeps = [{"profile": "pp1", "profileNote": None, "dir": "/x/pp1",
               "manifest": manifest(CASES), "manifestError": None,
               "sidecars": sweep_sidecars(), "imageUrl": lambda n: "../" + n}]
    return ppv.build_site_data(sweeps, **kw)


class ProfileNameTests(unittest.TestCase):
    def test_a_plain_recorded_profile_is_used(self):
        self.assertEqual(("stock-minimal", None),
                         ppv.profile_name({"profile": "stock-minimal", "label": "pp1"}, "d"))

    def test_the_dotdot_profile_falls_back_to_the_label_with_a_note(self):
        name, note = ppv.profile_name({"profile": "..", "label": "pp1"}, "dir")
        self.assertEqual("pp1", name)
        self.assertIn("'..'", note)
        self.assertIn("label", note)

    def test_no_label_falls_back_to_the_directory(self):
        name, note = ppv.profile_name({"profile": ""}, "pp2-2026 x")
        self.assertEqual("pp2-2026-x", name)
        self.assertIn("directory", note)

    def test_an_explicit_name_wins(self):
        self.assertEqual(("restock", None), ppv.profile_name({"profile": "a"}, "d", "restock"))

    def test_split_named_dir(self):
        self.assertEqual(("stock", "C:/x/y"), ppv.split_named_dir("stock=C:/x/y", lambda p: False))
        self.assertEqual((None, "a=b"), ppv.split_named_dir("a=b", lambda p: True))
        self.assertEqual((None, "C:/x"), ppv.split_named_dir("C:/x", lambda p: False))


class CaseRowTests(unittest.TestCase):
    def setUp(self):
        self.data = build()
        self.rows = {c["caseId"]: c for c in self.data["cases"]}

    def test_controls_come_first_then_manifest_order_then_unlisted(self):
        ids = [c["caseId"] for c in self.data["cases"]]
        self.assertEqual(list(pp.CONTROL_IDS), ids[:len(pp.CONTROL_IDS)])
        self.assertEqual([c[0] for c in CASES], ids[len(pp.CONTROL_IDS):-1])
        self.assertEqual("orphan--default--00000099", ids[-1])
        self.assertFalse(self.rows["orphan--default--00000099"]["listed"])

    def test_part_verdicts_are_partparitys(self):
        for cid, sc in sweep_sidecars().items():
            if cid in pp.CONTROL_IDS:
                continue
            self.assertEqual(pp.judge_case(sc).verdict, self.rows[cid]["verdict"], cid)

    def test_triggers_name_what_fired(self):
        self.assertEqual("ghost missing mesh model/skirt",
                         self.rows["bravo--default--00000002"]["trigger"])
        self.assertEqual(pp.VERDICT_FAIL, self.rows["bravo--default--00000002"]["verdict"])
        self.assertEqual("colour blob 100 px in side-a", self.rows["tank--gold--00000004"]["trigger"])
        self.assertEqual(pp.VERDICT_WARN, self.rows["tank--gold--00000004"]["verdict"])
        self.assertEqual("setup fault: crashed", self.rows["crashy--default--00000006"]["trigger"])
        self.assertEqual("", self.rows["alpha--default--00000001"]["trigger"])

    def test_a_listed_case_without_a_sidecar_is_an_error(self):
        r = self.rows["ghosty--default--00000007"]
        self.assertEqual(pp.VERDICT_ERROR, r["verdict"])
        self.assertEqual(ppv.SIDECAR_MISSING, r["sidecarProblem"])
        self.assertEqual("no sidecar on disk", r["trigger"])

    def test_an_unreadable_sidecar_is_an_error(self):
        row = ppv.case_row("p", "x", ppv.SIDECAR_UNREADABLE, None, None, True, False, None)
        self.assertEqual(pp.VERDICT_ERROR, row["verdict"])
        self.assertEqual("sidecar unreadable", row["trigger"])

    def test_a_control_chip_is_its_recorded_outcome_not_a_part_verdict(self):
        sc = sweep_sidecars()
        sc["ctl-ghost-flip"]["views"] = [view("side-a", real_blob=5000)]
        rows = ppv.sweep_rows("p", manifest(CASES, controls_ok=False), sc, None)
        flip = [r for r in rows if r["caseId"] == "ctl-ghost-flip"][0]
        self.assertEqual(ppv.VERDICT_CONTROL_BAD, flip["verdict"])
        self.assertEqual(pp.VERDICT_FAIL, flip["partVerdict"])
        empty = [r for r in rows if r["caseId"] == "ctl-empty"][0]
        self.assertEqual(ppv.VERDICT_CONTROL_OK, empty["verdict"])
        self.assertEqual("control", empty["kind"])

    def test_image_urls_come_from_the_callable_and_absent_files_read_none(self):
        row = self.rows["alpha--default--00000001"]
        self.assertEqual("../alpha--default--00000001.png", row["atlas"])
        none = ppv.case_row("p", "x", sidecar("x", "X"), None, None, True, False, lambda n: None)
        self.assertIsNone(none["atlas"])

    def test_each_view_carries_its_own_verdict_and_trigger(self):
        v = self.rows["tank--gold--00000004"]["views"][0]
        self.assertEqual(("side-a", pp.VERDICT_WARN), (v["view"], v["verdict"]))
        self.assertEqual("colour blob 100 px in side-a", v["trigger"])


class TriggerTests(unittest.TestCase):
    def test_phrases(self):
        self.assertEqual("skin mismatch: real=Gold ghost=Bare",
                         ppv.reason_phrase("skin-mismatch:real=Gold ghost=Bare"))
        self.assertEqual("shape blob 312 px in side-b", ppv.reason_phrase("shape-blob-px:side-b=312"))
        self.assertEqual("ghost has extra mesh k", ppv.reason_phrase("extra-rendered:k"))
        self.assertEqual("skin not applied: requested=A real=B",
                         ppv.reason_phrase("skin-not-applied:requested=A real=B"))
        self.assertEqual("skin not modelled: the ghost shows this B9 subtype only in part (texture, material or module rules)",
                         ppv.reason_phrase("skin-not-modelled:b9"))
        self.assertEqual("something-new", ppv.reason_phrase("something-new"))

    def test_a_views_pct_collapses_into_its_px_phrase_and_extra_phrases_count(self):
        line = ppv.trigger_line(["shape-blob-px:top=300", "shape-blob-pct:top=0.400",
                                 "color-blob-px:top=98", "missing-rendered:a",
                                 "missing-rendered:b"])
        self.assertEqual("shape blob 300 px in top; colour blob 98 px in top (+2 more)", line)

    def test_a_pct_without_px_is_kept(self):
        self.assertEqual("shape blob 0.300% of the box in top",
                         ppv.trigger_line(["shape-blob-pct:top=0.300"]))


class SkinProofTests(unittest.TestCase):
    def test_marks(self):
        self.assertEqual({}, ppv.skin_proof(variant_skin("A"))["marks"])
        m = ppv.skin_proof(variant_skin("A", real="B"))["marks"]
        self.assertEqual("setup", m["requested"])
        m = ppv.skin_proof({"requested": "Gold", "real": "Gold", "snapshot": "Gold",
                            "ghost": "Bare"})["marks"]
        self.assertEqual({"real": "ghost", "ghost": "ghost"}, m)
        self.assertEqual({}, ppv.skin_proof({"requested": "a", "real": "a", "snapshot": "A",
                                             "ghost": "A"})["marks"])
        m = ppv.skin_proof({"requested": "Basic", "real": "Basic", "snapshot": "BW",
                            "ghost": "BW"})["marks"]
        self.assertIn("snapshot", m)
        self.assertEqual({}, ppv.skin_proof({"snapshot": "", "real": None})["marks"])


class GroupTests(unittest.TestCase):
    def setUp(self):
        self.data = build()
        self.groups = {g["part"]: g for g in self.data["groups"]}
        self.rows = {c["id"]: c for c in self.data["cases"]}

    def test_one_group_per_part_and_controls_are_not_grouped(self):
        self.assertEqual({"Alpha", "Bravo", "Tank", "Crashy", "Ghosty", "Orphan"},
                         set(self.groups))
        self.assertEqual(["pp1/pp1/" + c for c in pp.CONTROL_IDS], self.data["controls"])

    def test_skins_order_base_variant_b9(self):
        tank = self.groups["Tank"]
        self.assertEqual(["pp1/tank--basic--00000003", "pp1/tank--gold--00000004",
                          "pp1/tank--plume-a--00000005"], tank["cases"])
        self.assertEqual([ppv.SKIN_BASE, ppv.SKIN_VARIANT, ppv.SKIN_B9],
                         [self.rows[i]["skinKind"] for i in tank["cases"]])

    def test_group_verdict_is_the_worst_skin_and_its_trigger_names_the_skin(self):
        tank = self.groups["Tank"]
        self.assertEqual(pp.VERDICT_WARN, tank["verdict"])
        self.assertEqual("skin Gold: colour blob 100 px in side-a (+1 more skin(s) warn)",
                         tank["trigger"])
        # the B9 subtype warns skin-not-modelled:b9 (its ghost skin is null)
        self.assertEqual({"pass": 1, "warn": 2, "fail": 0, "error": 0}, tank["counts"])
        self.assertEqual("ghost missing mesh model/skirt", self.groups["Bravo"]["trigger"])
        self.assertEqual("", self.groups["Alpha"]["trigger"])

    def test_fail_outranks_error_and_warn_outranks_error(self):
        self.assertEqual(pp.VERDICT_FAIL, ppv.worst(["error", "fail", "pass"]))
        self.assertEqual(pp.VERDICT_WARN, ppv.worst(["error", "warn"]))
        self.assertEqual(pp.VERDICT_ERROR, ppv.worst(["pass", "error"]))
        self.assertEqual(pp.VERDICT_PASS, ppv.worst([]))

    def test_default_order_is_flagged_first(self):
        order = [g["part"] for g in self.data["groups"]]
        self.assertEqual(["Bravo", "Tank", "Crashy", "Ghosty", "Alpha", "Orphan"], order)

    def test_counts_and_filters(self):
        self.assertEqual({"pass": 3, "warn": 2, "fail": 1, "error": 2},
                         self.data["counts"]["cases"])
        self.assertEqual({"pass": 2, "warn": 1, "fail": 1, "error": 2},
                         self.data["counts"]["parts"])
        self.assertEqual({"ok": 8, "bad": 0}, self.data["counts"]["controls"])
        f = self.data["facets"]
        self.assertEqual([["fail", 1], ["warn", 1], ["error", 2], ["pass", 2]], f["verdict"])
        self.assertEqual([["Squad", 6]], f["source"])
        self.assertEqual([], f["regression"])


class RegressionTests(unittest.TestCase):
    def test_moved_new_same_gone(self):
        base_sc = sweep_sidecars()
        base_sc["tank--gold--00000004"] = sidecar("tank--gold--00000004", "Tank",
                                                  skin=variant_skin("Gold"))
        base_sc["zulu--default--00000010"] = sidecar("zulu--default--00000010", "Zulu")
        del base_sc["orphan--default--00000099"]
        base_cases = CASES + [("zulu--default--00000010", "Zulu", "default")]
        baseline = [{"profile": "old-label", "dir": "/b", "manifest": manifest(base_cases),
                     "sidecars": base_sc}]
        data = build(baseline=baseline)
        rows = {c["caseId"]: c for c in data["cases"]}
        reg = data["regression"]
        self.assertEqual({"pp1": "old-label"}, reg["pairs"])
        gold = rows["tank--gold--00000004"]["regression"]
        self.assertEqual(ppv.REG_MOVED, gold["status"])
        self.assertIn("verdict pass -> warn", gold["changes"])
        self.assertIn("colorBlobPx 0 -> 100", gold["changes"])
        self.assertEqual(ppv.REG_NEW, rows["orphan--default--00000099"]["regression"]["status"])
        self.assertEqual(ppv.REG_SAME, rows["alpha--default--00000001"]["regression"]["status"])
        self.assertEqual(["old-label/zulu--default--00000010"], reg["gone"])
        self.assertEqual(ppv.REG_MOVED,
                         [g for g in data["groups"] if g["part"] == "Tank"][0]["regression"])
        self.assertIn([ppv.REG_MOVED, 1], data["facets"]["regression"])

    def test_tolerances(self):
        a = {"verdict": "pass", "setupStatus": "ok", "worstBlobPx": 10, "colorBlobPx": 10,
             "minIou": 0.999, "missingRendered": 0, "extraRendered": 0,
             "skinProof": {"real": "a", "ghost": "a"}}
        b = dict(a, worstBlobPx=10 + ppv.REG_BLOB_MOVE_PX, minIou=0.999 - ppv.REG_IOU_MOVE / 2,
                 skinProof={"real": "A", "ghost": "a"})
        self.assertEqual([], ppv.compare_rows(b, a))
        c = dict(a, worstBlobPx=11 + ppv.REG_BLOB_MOVE_PX, minIou=None)
        self.assertEqual(["worstBlobPx 10 -> 19", "minIou 0.9990 -> null"], ppv.compare_rows(c, a))

    def test_pair_profiles(self):
        self.assertEqual({"a": "a", "b": "c"}, ppv.pair_profiles(["a", "b"], ["a", "c"]))
        self.assertEqual({}, ppv.pair_profiles(["a", "b"], ["c", "d"]))


class CrossProfileTests(unittest.TestCase):
    def test_same_case_then_same_part_links(self):
        sc2 = {"alpha--default--00000001": sidecar("alpha--default--00000001", "Alpha"),
               "tank--dark--0000000a": sidecar("tank--dark--0000000a", "Tank",
                                               skin=variant_skin("Dark"))}
        sweeps = [
            {"profile": "stock", "dir": "/s", "manifest": manifest(CASES), "sidecars": sweep_sidecars()},
            {"profile": "restock", "dir": "/r",
             "manifest": manifest([("alpha--default--00000001", "Alpha", "default"),
                                   ("tank--dark--0000000a", "Tank", "Dark")], label="pp2"),
             "sidecars": sc2},
        ]
        data = ppv.build_site_data(sweeps)
        rows = {c["id"]: c for c in data["cases"]}
        links = rows["stock/alpha--default--00000001"]["links"]
        self.assertEqual([("restock/alpha--default--00000001", "same-case")],
                         [(l["id"], l["match"]) for l in links])
        tank = rows["stock/tank--gold--00000004"]["links"]
        self.assertEqual([("restock/tank--dark--0000000a", "same-part")],
                         [(l["id"], l["match"]) for l in tank])
        g = [x for x in data["groups"] if x["profile"] == "restock" and x["part"] == "Tank"][0]
        self.assertEqual([("stock", "stock/tank--basic--00000003")],
                         [(l["profile"], l["case"]) for l in g["links"]])
        self.assertEqual(["stock", "restock"], data["profiles"])

    def test_a_duplicate_case_in_one_profile_keeps_the_first(self):
        s = {"profile": "p", "dir": "/a", "manifest": manifest(CASES), "sidecars": sweep_sidecars()}
        data = ppv.build_site_data([s, dict(s, dir="/b")])
        self.assertEqual(len(set(c["id"] for c in data["cases"])), len(data["cases"]))
        parts = [c for c in data["cases"] if c["kind"] == ppv.KIND_PART]
        # Every part case is a duplicate line; the controls are exempt from the merge
        # (design D.7): each sweep keeps its own, under its own id.
        self.assertEqual(sorted(c["caseId"] for c in parts),
                         sorted(data["sweeps"][1]["duplicates"]))
        self.assertEqual(["pp1", "pp1-2"], [h["sweep"] for h in data["sweeps"]])
        self.assertEqual(2 * len(pp.CONTROL_IDS), len(data["controls"]))
        self.assertEqual(["pp1", "pp1-2"], [cs["sweep"] for cs in data["controlSets"]])


class HeaderTests(unittest.TestCase):
    def test_gates_are_partparitys(self):
        h = build()["sweeps"][0]
        self.assertTrue(h["controls"]["ok"])
        self.assertFalse(h["completeness"]["ok"])  # ghosty has no sidecar
        self.assertIn("ghosty--default--00000007:no-sidecar", " ".join(h["completeness"]["problems"]))
        self.assertTrue(h["schemaOk"])

    def test_a_missing_manifest_still_lists_the_sidecars(self):
        data = ppv.build_site_data([{"profile": "p", "dir": "/x", "manifest": None,
                                     "manifestError": "absent", "sidecars": sweep_sidecars()}])
        self.assertEqual(len(sweep_sidecars()), len(data["cases"]))
        self.assertIsNone(data["sweeps"][0]["controls"])
        self.assertEqual("absent", data["sweeps"][0]["manifestError"])


class DataJsTests(unittest.TestCase):
    def test_round_trip_ascii_and_no_script_close(self):
        data = build()
        text = ppv.render_data_js(data)
        self.assertTrue(text.startswith(ppv.DATA_JS_PREFIX))
        text.encode("ascii")
        self.assertNotIn("</", text)
        self.assertEqual(json.loads(json.dumps(data)), ppv.parse_data_js(text))


class OutputGuardTests(unittest.TestCase):
    def test_repo_tree_refused_except_harness_results(self):
        repo = os.path.join(tempfile.gettempdir(), "fake-repo")
        self.assertIsNotNone(ppv.out_dir_refusal(os.path.join(repo, "docs", "v"), repo))
        self.assertIsNone(ppv.out_dir_refusal(os.path.join(repo, "harness", "results", "x"), repo))
        self.assertIsNone(ppv.out_dir_refusal(os.path.join(tempfile.gettempdir(), "elsewhere"), repo))
        self.assertIsNone(ppv.out_dir_refusal("/x", None))

    def test_the_real_repo_root_is_found(self):
        root = ppv.find_repo_root(_TOOLS)
        self.assertTrue(os.path.isdir(os.path.join(root, "harness", "tools")))

    def test_relative_url_quotes(self):
        base = os.path.join(tempfile.gettempdir(), "a", "viewer")
        self.assertEqual("../x%20y", ppv.relative_url(base, os.path.join(tempfile.gettempdir(), "a", "x y")))


def _png(path):
    raw = b"\x00\x00\x00\x00\x00"
    body = struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0)

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    with open(path, "wb") as fh:
        fh.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", body) + chunk(b"IDAT", zlib.compress(raw))
                 + chunk(b"IEND", b""))


class _Balance(html.parser.HTMLParser):
    VOID = {"meta", "link", "br", "img", "input", "hr"}

    def __init__(self):
        super().__init__()
        self.stack, self.errors = [], []

    def handle_starttag(self, tag, attrs):
        if tag not in self.VOID:
            self.stack.append(tag)

    def handle_endtag(self, tag):
        if not self.stack or self.stack[-1] != tag:
            self.errors.append("unbalanced </%s> (open: %s)" % (tag, self.stack[-3:]))
        else:
            self.stack.pop()


class CliTests(unittest.TestCase):
    def setUp(self):
        quiet = contextlib.ExitStack()
        quiet.enter_context(contextlib.redirect_stdout(io.StringIO()))
        quiet.enter_context(contextlib.redirect_stderr(io.StringIO()))
        self.addCleanup(quiet.close)
        self.tmp = tempfile.mkdtemp(prefix="ppv-")
        self.sweep = os.path.join(self.tmp, "pp1 sweep")
        os.makedirs(self.sweep)
        with open(os.path.join(self.sweep, pp.MANIFEST_FILE), "w") as fh:
            json.dump(manifest(CASES), fh)
        for cid, sc in sweep_sidecars().items():
            with open(os.path.join(self.sweep, cid + pp.SIDECAR_SUFFIX), "w") as fh:
                json.dump(sc, fh)
            if sc["setup"]["status"] != "crashed":
                _png(os.path.join(self.sweep, cid + pp.ATLAS_SUFFIX))
                _png(os.path.join(self.sweep, cid + pp.THUMB_SUFFIX))
        with open(os.path.join(self.sweep, "broken" + pp.SIDECAR_SUFFIX), "w") as fh:
            fh.write("{not json")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _data(self, out):
        with open(os.path.join(out, ppv.DATA_JS_FILE), encoding="ascii") as fh:
            return ppv.parse_data_js(fh.read())

    def test_default_output_references_images_in_place(self):
        self.assertEqual(0, ppv.main([self.sweep]))
        out = os.path.join(self.sweep, ppv.DEFAULT_OUT_LEAF)
        self.assertEqual({ppv.INDEX_FILE, ppv.DATA_JS_FILE}, set(os.listdir(out)))
        data = self._data(out)
        rows = {c["caseId"]: c for c in data["cases"]}
        self.assertEqual("pp1", data["profiles"][0])
        self.assertEqual(ppv.SIDECAR_UNREADABLE, rows["broken"]["sidecarProblem"])
        self.assertIsNone(rows["crashy--default--00000006"]["atlas"])
        resolved = 0
        for c in data["cases"]:
            if c["atlas"]:
                self.assertTrue(c["atlas"].startswith("../"), c["atlas"])
                path = os.path.normpath(os.path.join(out, *c["atlas"].split("/")))
                self.assertTrue(os.path.isfile(path), path)
                resolved += 1
        self.assertGreater(resolved, 10)
        with open(os.path.join(out, ppv.INDEX_FILE), encoding="ascii") as fh:
            page = fh.read()
        p = _Balance()
        p.feed(page)
        self.assertEqual([], p.errors)
        self.assertEqual([], p.stack)
        self._node_check(out, page)

    def test_out_copies_images_and_a_rerun_keeps_them(self):
        out = os.path.join(self.tmp, "site")
        self.assertEqual(0, ppv.main([self.sweep, "--out", out]))
        data = self._data(out)
        atlases = [c["atlas"] for c in data["cases"] if c["atlas"]]
        self.assertTrue(all(a.startswith("img/0/") for a in atlases))
        for a in atlases:
            self.assertTrue(os.path.isfile(os.path.join(out, *a.split("/"))))
        sweeps = [ppv.load_sweep(self.sweep)]
        copies = ppv.plan_images(sweeps, out, "copy")
        ppv.build_site_data(sweeps)
        self.assertEqual(0, ppv.copy_images(copies)[0])

    def test_baseline_and_two_profiles(self):
        out = os.path.join(self.tmp, "site2")
        self.assertEqual(0, ppv.main(["stock=" + self.sweep, "--baseline", self.sweep,
                                      "--out", out]))
        data = self._data(out)
        self.assertEqual({"stock": "pp1"}, data["regression"]["pairs"])
        self.assertEqual(len(data["cases"]), data["regression"]["counts"]["same"])
        out2 = os.path.join(self.tmp, "site3")
        self.assertEqual(0, ppv.main(["stock=" + self.sweep, "restock=" + self.sweep,
                                      "--baseline", self.sweep, "--out", out2]))
        data = self._data(out2)
        self.assertEqual(["stock", "restock"], data["profiles"])
        # Two current profiles against one baseline profile of another name: ambiguous,
        # so nothing pairs and every case reads new rather than a guessed comparison.
        self.assertEqual({}, data["regression"]["pairs"])
        self.assertEqual(["pp1"], data["regression"]["unpairedBaselineProfiles"])
        self.assertTrue(all(c["links"] for c in data["cases"]))

    def test_refusals(self):
        self.assertEqual(2, ppv.main([os.path.join(self.tmp, "nope")]))
        with contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertEqual(2, ppv.main([self.sweep, "--gif-format", "gif"]))
        self.assertIn("pass --gif", err.getvalue())
        root = ppv.find_repo_root(_TOOLS)
        self.assertEqual(2, ppv.main([self.sweep, "--out", os.path.join(root, "docs", "pp-view")]))
        self.assertFalse(os.path.exists(os.path.join(root, "docs", "pp-view")))

    def _node_check(self, out, page):
        node = shutil.which("node")
        if not node:
            return
        script = re.findall(r"<script>(.*?)</script>", page, re.S)
        self.assertEqual(1, len(script))
        js = os.path.join(self.tmp, "page.js")
        with open(js, "w", encoding="ascii") as fh:
            fh.write(script[0])
        for f in (js, os.path.join(out, ppv.DATA_JS_FILE)):
            r = subprocess.run([node, "--check", f], capture_output=True, text=True)
            self.assertEqual(0, r.returncode, r.stderr)


# ---------------------------------------------------------------------------
# State cases (design Appendix D.7). The synthetic set is built with partparity's own
# state-sweep helpers (test_partparity), so its sidecars are the shape S4 judges.
# ---------------------------------------------------------------------------

PROFILE = "stock-minimal"
STOWED_IDS = {"longAntenna": "longantenna--default--deadbeef",
              "landingLeg": "landingleg--default--deadbeef",
              "liquidEngine2": "liquidengine2--default--deadbeef",
              "parachuteSingle": "parachutesingle--default--deadbeef"}
ANT_DEPLOYED = "longantenna--default--deployed--0000a001"
LEG_DEPLOYED = "landingleg--default--deployed--0000a002"
LEG_LIT = "landingleg--default--lit--0000a003"
BAY_OPEN = "mk2cargobays--default--open--0000a004"
ENG_STACKED = "liquidengine2--default--stowed--stacked--0000a005"
ENG_JETTISONED = "liquidengine2--default--jettisoned--stacked--0000a006"
CHUTE_SEMI = "parachutesingle--default--semi--air--0000b001"
CHUTE_FULL = "parachutesingle--default--full--air--0000b002"


def _tpp():
    import test_partparity as tpp  # the S4 state-sweep helpers
    return tpp


def orbit_cases():
    tpp = _tpp()
    return [
        tpp._state_case(ANT_DEPLOYED, part="longAntenna"),
        tpp._state_case(LEG_DEPLOYED, part="landingLeg", family="gear",
                        proof={"module": "ModuleWheelDeployment", "action": "EventToggle"},
                        start=tpp._shifted()),
        tpp._state_case(LEG_LIT, part="landingLeg", family="light", state="lit",
                        proof={"module": "ModuleLight", "action": "LightsOn",
                               "ghostEvent": "no-info"}),
        tpp._state_case(BAY_OPEN, part="mk2CargoBayS", family="cargo-bay", state="open",
                        proof={"module": "ModuleAnimateGeneric", "action": "Toggle"}),
        tpp._stacked_stowed_case(ENG_STACKED),
        tpp._state_case(ENG_JETTISONED, part="liquidEngine2", family="jettison",
                        state="jettisoned", context="stacked",
                        proof={"module": "ModuleJettison", "action": "Jettison"}),
    ]


def air_cases():
    tpp = _tpp()
    proof = {"module": "ModuleParachute", "action": "Deploy"}
    return [
        tpp._state_case(CHUTE_SEMI, part="parachuteSingle", family="parachute", state="semi",
                        context="air", proof=proof),
        tpp._state_case(CHUTE_FULL, part="parachuteSingle", family="parachute", state="full",
                        context="air", proof=proof),
    ]


def _relabel(label_dir, label):
    path = os.path.join(label_dir, pp.MANIFEST_FILE)
    with open(path, encoding="utf-8") as fh:
        m = json.load(fh)
    m["label"] = label
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(m, fh)


def write_synthetic_state_set(root):
    """Three sweep dirs of one profile under ``root``: ``pp1`` (stowed, with the stowed
    cases the state cases pair with; the cargo bay has none), ``pp3`` (an orbit state
    run) and ``pp5`` (an air run). Returns the three dirs."""
    tpp = _tpp()
    fx = tpp.fx
    stowed, orbit, air = (os.path.join(root, n) for n in ("pp1", "pp3", "pp5"))
    m = fx.build_sweep(stowed, label="pp1", parts=1, tile=tpp.STILE)
    body = fx.rect_mask(tpp.STILE, 4, 4, 4, 4)
    for part, cid in STOWED_IDS.items():
        fx.write_case(stowed, cid, part, [body] * 4, [body] * 4, tpp.STILE)
        m["cases"].append({"index": len(m["cases"]), "caseId": cid, "part": part,
                           "title": part, "source": "Squad", "skin": "default",
                           "state": "stowed", "context": "alone",
                           "sidecar": cid + pp.SIDECAR_SUFFIX, "setupStatus": "ok",
                           "maxShapeBlobPx": 0, "maxColorBlobPx": 0, "minIou": 1,
                           "missingRendered": 0, "extraRendered": 0})
    m["progress"]["cases"] = m["progress"]["completeCases"] = len(m["cases"])
    with open(os.path.join(stowed, pp.MANIFEST_FILE), "w", encoding="utf-8") as fh:
        json.dump(m, fh)
    tpp._build_state_sweep(orbit, orbit_cases())
    tpp._build_state_sweep(air, air_cases(), settings={"states": "semi,full", "contexts": "air"})
    _relabel(air, "pp5")
    return stowed, orbit, air


class _StateSetMixin:
    def setUp(self):
        quiet = contextlib.ExitStack()
        quiet.enter_context(contextlib.redirect_stdout(io.StringIO()))
        quiet.enter_context(contextlib.redirect_stderr(io.StringIO()))
        self.addCleanup(quiet.close)
        self.tmp = tempfile.mkdtemp(prefix="ppv-state-")
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.dirs = write_synthetic_state_set(self.tmp)

    def data(self, dirs=None, **kw):
        return ppv.build_site_data([ppv.load_sweep(d) for d in (dirs or self.dirs)], **kw)

    @staticmethod
    def rows(data):
        return {c["caseId"]: c for c in data["cases"] if c["kind"] == ppv.KIND_PART}

    @staticmethod
    def group(data, part):
        return [g for g in data["groups"] if g["part"] == part][0]


class StateRowTests(_StateSetMixin, unittest.TestCase):
    def test_state_rows_carry_partparitys_routes_and_flags(self):
        rows = self.rows(self.data())
        for doc, _ in orbit_cases() + air_cases():
            cv = pp.judge_case(doc)
            r = rows[doc["caseId"]]
            self.assertEqual(cv.verdict, r["verdict"], doc["caseId"])
            self.assertEqual(list(cv.reasons), r["reasons"])
            self.assertEqual(cv.worst_view, r["worstView"])
            self.assertEqual(list(cv.state_flags), r["stateFlags"])
            if cv.is_state_case:
                self.assertEqual({x.route: x.verdict for x in cv.routes},
                                 {k: v["verdict"] for k, v in r["routes"].items()})
            else:
                self.assertIsNone(r["routes"])
        leg = rows[LEG_DEPLOYED]
        self.assertEqual(pp.VERDICT_FAIL, leg["verdict"])
        self.assertEqual(pp.VERDICT_PASS, leg["routes"]["event"]["verdict"])
        self.assertEqual(pp.VERDICT_FAIL, leg["routes"]["start"]["verdict"])
        self.assertTrue(leg["worstView"].startswith("start:"), leg["worstView"])
        self.assertTrue(leg["trigger"].startswith("start route: shape blob"), leg["trigger"])
        self.assertEqual(pp.VERDICT_FAIL, leg["views"][0]["start"]["verdict"])
        self.assertEqual(pp.VERDICT_PASS, leg["views"][0]["verdict"])
        self.assertEqual(set(pp.STOWED_REF_KEYS), set(leg["views"][0]["stowedRef"]))
        lit = rows[LEG_LIT]
        self.assertEqual(["state-not-modelled:event:no-info"], lit["stateFlags"])
        self.assertEqual(pp.VERDICT_WARN, lit["verdict"])
        self.assertEqual("state not modelled on the event route (no-info)", lit["trigger"])
        self.assertEqual(list(pp.ATLAS_ROWS_STATE), leg["atlasRows"])
        self.assertTrue(leg["isState"])
        self.assertFalse(rows[ENG_STACKED]["isState"])
        self.assertFalse(rows[ENG_STACKED]["isRest"])
        self.assertEqual("stowed (stacked)", rows[ENG_STACKED]["stateLabel"])

    def test_route_reason_phrases(self):
        self.assertEqual("event route: shape blob 300 px in side-a",
                         ppv.reason_phrase("event:shape-blob-px:side-a=300"))
        self.assertEqual("start route not measured (a view has no start block)",
                         ppv.reason_phrase("start:no-views"))
        self.assertEqual("start route not at its pose on the first frame",
                         ppv.reason_phrase("flag:start-pose-not-first-frame"))
        self.assertIn("no part event", ppv.reason_phrase("flag:state-not-recorded"))
        self.assertTrue(ppv.reason_phrase("press-proof: setup=ok action=Extend actionResult="
                                          "missing reached=False").startswith("press proof"))
        # px and pct of one view collapse per route, not across routes
        line = ppv.trigger_line(["event:shape-blob-px:top=300", "event:shape-blob-pct:top=0.4",
                                 "start:shape-blob-pct:top=0.5"], limit=5)
        self.assertEqual("event route: shape blob 300 px in top; "
                         "start route: shape blob 0.5% of the box in top", line)
        self.assertEqual("deployed", ppv.state_word("deployed-m3"))


class StateGroupingTests(_StateSetMixin, unittest.TestCase):
    def test_part_skin_state_with_the_stowed_case_first(self):
        data = self.data()
        ant = self.group(data, "longAntenna")
        self.assertEqual(1, len(ant["skins"]))
        self.assertEqual(["%s/%s" % (PROFILE, STOWED_IDS["longAntenna"]),
                          "%s/%s" % (PROFILE, ANT_DEPLOYED)], ant["skins"][0]["cases"])
        self.assertEqual(ant["cases"], ant["skins"][0]["cases"])
        # two families of one part: one sub-block each, in the D.1 table order
        leg = self.group(data, "landingLeg")
        self.assertEqual([STOWED_IDS["landingLeg"], LEG_DEPLOYED, LEG_LIT],
                         [i.split("/", 1)[1] for i in leg["cases"]])
        self.assertEqual(pp.VERDICT_FAIL, leg["verdict"])
        self.assertTrue(leg["trigger"].startswith("skin default deployed: start route"),
                        leg["trigger"])
        self.assertEqual(["stowed", "deployed", "lit"], leg["states"])
        self.assertEqual(["gear", "light"], leg["families"])
        self.assertEqual(["state-not-modelled"], leg["stateFlags"])
        eng = self.group(data, "liquidEngine2")
        self.assertEqual([STOWED_IDS["liquidEngine2"], ENG_STACKED, ENG_JETTISONED],
                         [i.split("/", 1)[1] for i in eng["cases"]])
        chute = self.group(data, "parachuteSingle")
        self.assertEqual([STOWED_IDS["parachuteSingle"], CHUTE_SEMI, CHUTE_FULL],
                         [i.split("/", 1)[1] for i in chute["cases"]])

    def test_the_stowed_column_pairs_with_the_loaded_stowed_case(self):
        data = self.data()
        rows = self.rows(data)
        self.assertEqual("%s/%s" % (PROFILE, STOWED_IDS["longAntenna"]),
                         rows[ANT_DEPLOYED]["stowedPair"])
        self.assertEqual("%s/%s" % (PROFILE, STOWED_IDS["parachuteSingle"]),
                         rows[CHUTE_SEMI]["stowedPair"])
        # the cargo bay's stowed case is not in the stowed sweep: no pair, the page falls
        # back to the case's own ghost-stowed row, and the case forms its own base skin
        self.assertIsNone(rows[BAY_OPEN]["stowedPair"])
        bay = self.group(data, "mk2CargoBayS")
        self.assertEqual([ppv.SKIN_BASE], [s["skinKind"] for s in bay["skins"]])
        self.assertEqual(1, bay["skins"][0]["states"])
        # without the stowed sweep nothing pairs, and no case is lost
        alone = self.data(self.dirs[1:])
        self.assertTrue(all(c["stowedPair"] is None for c in alone["cases"]))
        self.assertEqual(len(orbit_cases()) + len(air_cases()),
                         len([c for c in alone["cases"] if c["kind"] == ppv.KIND_PART]))

    def test_merge_by_profile_with_controls_exempt_and_no_duplicate_line(self):
        data = self.data()
        self.assertEqual([PROFILE], data["profiles"])
        self.assertEqual([[], [], []], [h["duplicates"] for h in data["sweeps"]])
        self.assertEqual(["pp1", "pp3", "pp5"], [h["sweep"] for h in data["sweeps"]])
        self.assertEqual([(ppv.CONTROL_SET_STOWED, 8), (ppv.CONTROL_SET_ORBIT, 9),
                          (ppv.CONTROL_SET_AIR, 4)],
                         [(cs["kind"], len(cs["ids"])) for cs in data["controlSets"]])
        self.assertEqual(len(set(data["controls"])), len(data["controls"]))
        self.assertIn("%s/pp3/%s" % (PROFILE, pp.CONTROL_STATE_EVENTS_DROPPED), data["controls"])
        self.assertIn("%s/pp5/%s" % (PROFILE, pp.CONTROL_STATE_EVENTS_DROPPED_AIR),
                      data["controls"])
        self.assertEqual({"ok": 21, "bad": 0}, data["counts"]["controls"])
        # a part case id present in two merged sweeps IS a duplicate line
        dup = self.data([self.dirs[0], self.dirs[1], self.dirs[1]])
        self.assertEqual(sorted(c["caseId"] for c, _ in orbit_cases()),
                         sorted(dup["sweeps"][2]["duplicates"]))

    def test_headers_carry_partparitys_state_gates(self):
        heads = {h["sweep"]: h for h in self.data()["sweeps"]}
        self.assertIsNone(heads["pp1"]["statePress"])
        self.assertIsNone(heads["pp1"]["stateFlagCounts"])
        self.assertEqual(ppv.CONTROL_SET_STOWED, heads["pp1"]["controlSet"])
        self.assertTrue(heads["pp3"]["statePress"]["ok"])
        self.assertEqual(9, heads["pp3"]["controls"]["expected"])
        self.assertTrue(heads["pp3"]["controls"]["ok"], heads["pp3"]["controls"]["problems"])
        self.assertEqual({"state-not-modelled": 1}, heads["pp3"]["stateFlagCounts"])
        self.assertEqual([], heads["pp3"]["instrumentProblems"])
        self.assertEqual(4, heads["pp5"]["controls"]["expected"])

    def test_a_state_control_recorded_ok_is_re_judged_from_its_sidecar(self):
        tpp = _tpp()
        d = os.path.join(self.tmp, "pp3-bad-control")
        # the events-dropped control whose ghost DID deploy: recorded ok, judged bad
        tpp._build_state_sweep(d, orbit_cases()[:1],
                               state_control=tpp._state_case(pp.CONTROL_STATE_EVENTS_DROPPED))
        h = self.data([d])["sweeps"][0]
        self.assertFalse(h["controls"]["ok"])
        self.assertTrue(any("recorded-ok-judged-bad" in p for p in h["controls"]["problems"]),
                        h["controls"]["problems"])

    def test_a_state_control_recorded_ok_but_judged_bad_reads_bad_everywhere(self):
        # Review of PR #2770: zero the blob counts in pp3's ctl-state-events-dropped
        # stowedRef; the manifest outcome stays ok.
        path = os.path.join(self.dirs[1], pp.CONTROL_STATE_EVENTS_DROPPED + pp.SIDECAR_SUFFIX)
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
        for v in doc["views"]:
            for k in v["stowedRef"]:
                if "Blob" in k:
                    v["stowedRef"][k] = 0
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(doc, fh)
        outcome, detail = pp.judge_state_control(doc)
        self.assertEqual(pp.CONTROLS_BAD, outcome)
        data = self.data()
        row = [c for c in data["cases"]
               if c["id"] == "%s/pp3/%s" % (PROFILE, pp.CONTROL_STATE_EVENTS_DROPPED)][0]
        self.assertEqual("ok", row["control"]["outcome"])
        self.assertEqual(ppv.VERDICT_CONTROL_BAD, row["verdict"])
        self.assertEqual((pp.CONTROLS_BAD, detail),
                         (row["control"]["judged"], row["control"]["judgedDetail"]))
        self.assertIn(detail, row["trigger"])
        self.assertEqual({"ok": 20, "bad": 1}, data["counts"]["controls"])
        # The Controls box is bad when any control row is not control-ok; the Sweeps box
        # is bad when a sweep's controls gate failed (the header the page reads).
        heads = {h["sweep"]: h for h in data["sweeps"]}
        self.assertFalse(heads["pp3"]["controls"]["ok"])
        self.assertTrue(heads["pp1"]["controls"]["ok"] and heads["pp5"]["controls"]["ok"])
        self.assertIn("(h.controls && !h.controls.ok)", ppv.INDEX_HTML)
        self.assertIn('bad = ids.filter(function (id) { return CASE[id].verdict !== "control-ok" '
                      '&& CASE[id].verdict !== "control-na"; })',
                      ppv.INDEX_HTML)
        # an untouched state control recorded ok stays ok, judged ok
        air = [c for c in data["cases"] if c["caseId"] == pp.CONTROL_STATE_EVENTS_DROPPED_AIR][0]
        self.assertEqual((ppv.VERDICT_CONTROL_OK, pp.CONTROLS_OK),
                         (air["verdict"], air["control"]["judged"]))

    def test_a_label_named_profile_says_how_to_merge(self):
        name, note = ppv.profile_name({"profile": "..", "label": "pp3"}, "d")
        self.assertEqual("pp3", name)
        self.assertIn("NAME=DIR", note)

    def test_state_filters(self):
        f = self.data()["facets"]
        self.assertEqual(["stowed", "deployed", "open", "lit", "semi", "full", "jettisoned"],
                         [k for k, _ in f["state"]])
        self.assertIn(["stowed", 5], f["state"])
        self.assertEqual(["deployable", "gear", "cargo-bay", "light", "parachute", "jettison"],
                         [k for k, _ in f["family"]])
        self.assertEqual([["state-not-modelled", 1]], f["stateFlag"])
        stowed_only = build()["facets"]
        self.assertEqual(([], [], []), (stowed_only["state"], stowed_only["family"],
                                        stowed_only["stateFlag"]))


class StateRegressionTests(_StateSetMixin, unittest.TestCase):
    def test_pairs_by_case_id_and_route(self):
        tpp = _tpp()
        base_dir = os.path.join(self.tmp, "base-pp3")
        cases = orbit_cases()
        # the baseline's landing leg start route was clean; the current one fails it
        cases[1] = tpp._state_case(LEG_DEPLOYED, part="landingLeg", family="gear",
                                   proof={"module": "ModuleWheelDeployment",
                                          "action": "EventToggle"})
        tpp._build_state_sweep(base_dir, cases)
        data = self.data(baseline=[ppv.load_sweep(self.dirs[0]), ppv.load_sweep(base_dir)])
        rows = self.rows(data)
        leg = rows[LEG_DEPLOYED]["regression"]
        self.assertEqual(ppv.REG_MOVED, leg["status"])
        self.assertIn("start.verdict pass -> fail", leg["changes"])
        self.assertNotIn("event.verdict pass -> fail", leg["changes"])
        self.assertEqual({"event": "pass", "start": "pass"}, leg["baseline"]["routes"])
        self.assertEqual(ppv.REG_SAME, rows[ANT_DEPLOYED]["regression"]["status"])
        self.assertEqual(ppv.REG_NEW, rows[CHUTE_SEMI]["regression"]["status"])
        # controls pair by set kind: the stowed and orbit ctl-empty are two pairs
        ctl = [c for c in data["cases"] if c["caseId"] == "ctl-empty"]
        self.assertEqual(3, len(ctl))
        self.assertEqual([ppv.REG_SAME, ppv.REG_SAME, ppv.REG_NEW],
                         [c["regression"]["status"] for c in ctl])

    def test_compare_rows_route_and_flag_changes(self):
        base = {"verdict": "warn", "routes": {"event": {"verdict": "pass", "maxShapeBlobPx": 0,
                                                        "maxColorBlobPx": 0, "minIou": 1.0},
                                              "start": {"verdict": "pass", "maxShapeBlobPx": 0,
                                                        "maxColorBlobPx": 0, "minIou": 1.0}},
                "stateFlags": ["state-not-recorded"]}
        cur = json.loads(json.dumps(base))
        cur["routes"]["start"].update(maxShapeBlobPx=40, minIou=0.9)
        cur["stateFlags"] = ["start-pose-not-first-frame"]
        self.assertEqual(["start.maxShapeBlobPx 0 -> 40", "start.minIou 1.0000 -> 0.9000",
                          "stateFlags +start-pose-not-first-frame -state-not-recorded"],
                         ppv.compare_rows(cur, base))
        del cur["routes"]["start"]
        self.assertIn("start route judged -> absent", ppv.compare_rows(cur, base))


class StateSiteTests(_StateSetMixin, unittest.TestCase):
    def test_a_site_over_stowed_orbit_and_air_sweeps(self):
        out = os.path.join(self.tmp, "site")
        self.assertEqual(0, ppv.main(list(self.dirs) + ["--out", out]))
        with open(os.path.join(out, ppv.DATA_JS_FILE), encoding="ascii") as fh:
            data = ppv.parse_data_js(fh.read())
        self.assertEqual([[], [], []], [h["duplicates"] for h in data["sweeps"]])
        rows = {c["id"]: c for c in data["cases"]}
        for g in data["groups"]:
            for sk in g["skins"]:
                for cid in sk["cases"]:
                    c = rows[cid]
                    if c["isState"]:
                        self.assertTrue(c["atlas"], cid)
                        self.assertTrue(os.path.isfile(os.path.join(out, *c["atlas"].split("/"))))
                        if c["stowedPair"]:
                            self.assertIn(c["stowedPair"], sk["cases"])
        with open(os.path.join(out, ppv.INDEX_FILE), encoding="ascii") as fh:
            page = fh.read()
        p = _Balance()
        p.feed(page)
        self.assertEqual([], p.errors)
        CliTests._node_check(self, out, page)


FX_BURN = "liquidengine2.v2--default--burn-full--0000f001"
FX_SHORT = "engineshort--default--burn-full--0000f002"
FX_RCS = "rcsblock.v2--default--rcs-fire--0000f003"
FX_REF = "engineref--default--burn-full--0000f004"
FX_REF_OK = "engineref--default--burn-low--0000f005"
FX_JET = "jetengine--default--burn-full--0000f006"
FX_LOOSE = "engineloose--default--burn-full--0000f007"
FX_RCS_BLOCK = {"input": [1, 0, 0], "predicted": [0, 1], "fired": [0, 1], "nozzles": 4,
                "ghostHidden": [2, 3], "powerRatio": 0.5}
FX_STRIP_K = 4


def _strip_png(path, tpp, frames=FX_STRIP_K):
    """A strip whose two rows step together: frame j is one grey level, the ghost row's
    green channel tells the rows apart."""
    n = 8
    out = bytearray()
    for row in range(2):
        for _y in range(n):
            for j in range(frames):
                out += bytes((40 + 50 * j, 255 if row else 40 + 50 * j, 40 + 50 * j, 255)) * n
    with open(path, "wb") as fh:
        fh.write(tpp.gui_mirror.write_png(frames * n, 2 * n, 4, bytes(out)))


def fx_cases():
    """(sidecar, masks, tiles) of the synthetic FX sweep: a clean plume, a short one, an rcs
    case whose ghost fires every nozzle, two Waterfall cases (would fail / would pass), a
    static jet that draws nothing, and a plume that did not converge."""
    tpp = _tpp()
    plume = [
        tpp._fx_case(FX_BURN),
        tpp._fx_case(FX_SHORT, part="engineShort", event=tpp._broken("short")),
        tpp._fx_case(FX_RCS, part="RCSBlock.v2", family="rcs", state="rcs-fire",
                     rcs=FX_RCS_BLOCK, all_={"flame": (tpp._plume(y0=2, width=40), tpp._disc(20))}),
        tpp._fx_case(FX_REF, part="engineRef", reference="waterfall", event=tpp._broken("off")),
        tpp._fx_case(FX_REF_OK, part="engineRef", state="burn-low", reference="waterfall"),
        tpp._fx_case(FX_JET, part="JetEngine", real=tpp.NO_FX),
        tpp._fx_case(FX_LOOSE, part="engineLoose", event=tpp._broken("short")),
    ]
    rcs = plume[2][0]
    rcs["fx"]["drive"] = {"ignited": None, "finalThrust": None, "currentThrottle": None,
                          "recordedPower": 0.25, "cheat": True}
    rcs["fx"]["inventory"] = {"real": ["a", "a", "b"], "ghost": ["a", "c"], "ghostStart": ["a"],
                              "ghostAll": ["a", "a", "b"]}
    rcs["fx"]["structure"]["lightsDiffer"] = ["x real=1 ghost=2", "x real=1 ghost=2"]
    for loose in plume[6][0]["fx"]["views"]:
        half = loose["splitHalf"]
        if (loose["route"], loose["layer"]) == ("event", "flame") and half["even"]["real"]["lengthPx"]:
            half["odd"]["real"]["lengthPx"] = half["even"]["real"]["lengthPx"] * 2
    for doc, _m, _t in plume:
        doc["strip"] = {"file": doc["caseId"] + pp.STRIP_SUFFIX, "frames": FX_STRIP_K}
        for c in ("ghostEvent", "ghostStart"):
            doc["stateProof"][c] = "applied"
    return plume


def write_synthetic_fx_sweep(root, name="pp7"):
    tpp = _tpp()
    d = os.path.join(root, name)
    cases = fx_cases()
    tpp._build_fx_sweep(d, cases)
    _relabel(d, name)
    for doc, _m, _t in cases:
        _strip_png(os.path.join(d, doc["strip"]["file"]), tpp)
    return d


_FX_TEMPLATE = []


def fx_sweep_copy(root, name="pp7"):
    """A fresh copy of the synthetic FX sweep (built once per run of this module)."""
    if not _FX_TEMPLATE:
        tmp = tempfile.mkdtemp(prefix="ppv-fx-template-")
        unittest.addModuleCleanup(shutil.rmtree, tmp, True)
        unittest.addModuleCleanup(_FX_TEMPLATE.clear)
        _FX_TEMPLATE.append(write_synthetic_fx_sweep(tmp))
    d = os.path.join(root, name)
    shutil.copytree(_FX_TEMPLATE[0], d)
    return d


class _FxSetMixin:
    def setUp(self):
        quiet = contextlib.ExitStack()
        quiet.enter_context(contextlib.redirect_stdout(io.StringIO()))
        quiet.enter_context(contextlib.redirect_stderr(io.StringIO()))
        self.addCleanup(quiet.close)
        self.tmp = tempfile.mkdtemp(prefix="ppv-fx-")
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.dir = fx_sweep_copy(self.tmp)
        self.sweep = ppv.load_sweep(self.dir)
        self.sweep["imageUrl"] = lambda n: "../" + n if n in self.sweep["files"] else None
        self.data = ppv.build_site_data([self.sweep])
        self.by_id = {c["caseId"]: c for c in self.data["cases"]}

    def _edit(self, case_id, fn, d=None):
        path = os.path.join(d or self.dir, case_id + pp.SIDECAR_SUFFIX)
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
        fn(doc)
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(doc, fh)

    def _edit_manifest(self, fn, d=None):
        path = os.path.join(d or self.dir, pp.MANIFEST_FILE)
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
        fn(doc)
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(doc, fh)


class FxRowTests(_FxSetMixin, unittest.TestCase):
    def test_the_fx_half_is_partparitys(self):
        for cid, sc in self.sweep["sidecars"].items():
            row, cv = self.by_id[cid], pp.judge_case(sc)
            if cv.fx is None:
                self.assertIsNone(row["fx"], cid)
                continue
            want, fx = cv.fx.to_dict(), row["fx"]
            for key in ("family", "wouldBe", "verdict", "routes", "unsettled", "flags"):
                self.assertEqual(want[key], fx[key], (cid, key))
            judged = {(e["route"], e["view"], e["layer"]): e for e in want["entries"]}
            for e in fx["entries"]:
                j = judged.pop((e["route"], e["view"], e["layer"]), None)
                if j is None:
                    self.assertEqual((pp.FX_ROUTE_ALL, None, None, []),
                                     (e["route"], e["verdict"], e["regKey"], e["reasons"]))
                else:
                    self.assertEqual((j["verdict"], j["reasons"]), (e["verdict"], e["reasons"]))
            self.assertEqual({}, judged, cid)
            if row["kind"] == ppv.KIND_PART:
                self.assertEqual(cv.verdict, row["verdict"])
        self.assertIsNone(self.by_id["ctl-heat-events-dropped"]["fx"]["atlas"])

    def test_the_verdicts_of_the_synthetic_sweep(self):
        self.assertEqual(
            {FX_BURN: "pass", FX_SHORT: "fail", FX_RCS: "pass", FX_REF: "reference",
             FX_REF_OK: "reference", FX_JET: "warn", FX_LOOSE: "warn"},
            {c["caseId"]: c["verdict"] for c in self.data["cases"] if c["kind"] == "part"})
        self.assertEqual(("fail", "pass"),
                         (self.by_id[FX_REF]["fx"]["wouldBe"], self.by_id[FX_REF_OK]["fx"]["wouldBe"]))
        self.assertIn(pp.FLAG_FX_NO_REAL_EFFECT, self.by_id[FX_JET]["fx"]["flags"])
        self.assertIn(pp.CAP_FX_UNSETTLED, self.by_id[FX_LOOSE]["fx"]["flags"])
        capped = [r for e in self.by_id[FX_LOOSE]["fx"]["entries"] for r in e["reasons"] if r["capped"]]
        self.assertEqual({("length", "warn")}, {(r["measure"], r["level"]) for r in capped})
        self.assertIn(pp.REASON_FX_UNSETTLED, self.by_id[FX_LOOSE]["reasons"])

    def test_entries_carry_both_halves_and_never_the_hue_histogram(self):
        e = self.by_id[FX_SHORT]["fx"]["entries"][0]
        for half in (e, e["even"], e["odd"]):
            self.assertEqual(set(ppv.FX_SIDE_FIELDS), set(half["real"]))
            self.assertEqual(set(ppv.FX_SIDE_FIELDS), set(half["ghost"]))
            self.assertEqual(set(pp.FX_COMPARE_KEYS), set(half["compare"]))
        self.assertNotIn("hueHistogram", json.dumps(self.data["cases"]))
        self.assertEqual((8, 8), (e["even"]["samples"], e["odd"]["samples"]))

    def test_empty_is_nothing_drawn_on_either_side(self):
        self.assertTrue(all(e["empty"] for e in self.by_id[FX_JET]["fx"]["entries"]))
        off = {(e["route"], e["layer"]): e["empty"] for e in self.by_id[FX_REF]["fx"]["entries"]}
        self.assertFalse(off[("event", "flame")])  # the ghost side alone is empty

    def test_regression_keys(self):
        keys = {(e["route"], e["view"], e["layer"]): e["regKey"]
                for e in self.by_id[FX_RCS]["fx"]["entries"]}
        self.assertEqual(FX_RCS + "/fx/fx-side-a/flame", keys[("event", "fx-side-a", "flame")])
        self.assertEqual(FX_RCS + "/fx/fx-far/smoke/start", keys[("start", "fx-far", "smoke")])
        self.assertIsNone(keys[("all", "fx-side-a", "flame")])

    def test_pictures_and_a_missing_one(self):
        fx = self.by_id[FX_BURN]["fx"]
        self.assertEqual(("../" + FX_BURN + pp.FX_ATLAS_SUFFIX, list(pp.FX_ATLAS_ROWS),
                          list(pp.FX_VIEWS)), (fx["atlas"], fx["atlasRows"], fx["atlasCols"]))
        self.assertEqual(("../" + FX_BURN + pp.STRIP_SUFFIX, FX_STRIP_K, ["real", "ghost"]),
                         (fx["strip"], fx["stripFrames"], fx["stripRows"]))
        self.assertEqual(list(pp.FX_ATLAS_ROWS_RCS), self.by_id[FX_RCS]["fx"]["atlasRows"])
        os.remove(os.path.join(self.dir, FX_BURN + pp.STRIP_SUFFIX))
        os.remove(os.path.join(self.dir, FX_BURN + pp.FX_ATLAS_SUFFIX))
        sweep = ppv.load_sweep(self.dir)
        sweep["imageUrl"] = lambda n: n if n in sweep["files"] else None
        fx = [c for c in ppv.build_site_data([sweep])["cases"] if c["caseId"] == FX_BURN][0]["fx"]
        self.assertEqual((None, None), (fx["atlas"], fx["strip"]))
        self.assertTrue(fx["atlasFile"] and fx["stripFile"])  # the page names what is missing

    def test_the_rcs_known_difference_row(self):
        row = self.by_id[FX_RCS]
        rcs = row["fx"]["rcs"]
        self.assertEqual("rcs-known-difference:nozzles=2/4,power=0.500", rcs["flag"])
        self.assertEqual((2, 4, 0.5, 0.25, 2, True),
                         (rcs["fired"], rcs["total"], rcs["powerRatio"], rcs["recordedPower"],
                          rcs["ghostHidden"], not rcs["noGap"]))
        self.assertEqual(pp.judge_case(self.sweep["sidecars"][FX_RCS]).fx.to_dict()["rcs"],
                         {"powerRatio": rcs["powerRatio"], "reportedOnly": rcs["reportedOnly"]})
        drawn = [e for e in row["fx"]["entries"] if e["route"] == pp.FX_ROUTE_ALL and not e["empty"]]
        self.assertEqual({(v, l) for v in pp.FX_VIEWS for l in pp.FX_LAYERS},
                         {(e["view"], e["layer"]) for e in drawn})
        # never a verdict, never a trigger
        self.assertEqual(("pass", ""), (row["verdict"], row["trigger"]))
        self.assertIsNone(self.by_id[FX_BURN]["fx"]["rcs"])
        # a block with every nozzle at the recorded power is no difference
        full = dict(FX_RCS_BLOCK, predicted=[0], fired=[0], nozzles=1, ghostHidden=[], powerRatio=1.0)
        doc = _tpp()._fx_case(FX_RCS, family="rcs", state="rcs-fire", rcs=full)[0]
        doc["flags"] = ["rcs-known-difference:nozzles=1/1,power=1.000"]
        one = ppv.case_row("p", FX_RCS, doc, None, None, True, False, None)["fx"]["rcs"]
        self.assertTrue(one["noGap"])
        self.assertEqual(doc["flags"][0], one["flag"])  # the written flag still lists the case

    def test_inventory_diff_and_tallies(self):
        fx = self.by_id[FX_RCS]["fx"]
        self.assertEqual({"real": 3, "ghost": 2, "ghostStart": 1, "ghostAll": 3},
                         fx["inventory"]["counts"])
        self.assertEqual({"realOnly": [["a", 1], ["b", 1]], "ghostOnly": [["c", 1]]},
                         fx["inventory"]["diff"]["ghost"])
        self.assertEqual({"realOnly": [], "ghostOnly": []}, fx["inventory"]["diff"]["ghostAll"])
        self.assertEqual([["x real=1 ghost=2", 2]], fx["structure"]["lightsDiffer"])
        self.assertEqual({}, ppv.fx_inventory_diff(None)["diff"])

    def test_a_damaged_fx_sidecar_never_costs_the_page(self):
        def build_with(**edits):
            sc = json.loads(json.dumps(self.sweep["sidecars"][FX_RCS]))
            for block, value in edits.items():
                sc[block].update(value)
            sweep = dict(self.sweep, sidecars=dict(self.sweep["sidecars"], **{FX_RCS: sc}))
            data = ppv.build_site_data([sweep])
            ppv.render_data_js(data)
            return [c for c in data["cases"] if c["caseId"] == FX_RCS][0]["fx"]
        for odd in (5, 1.5, True, None, "x", {}, []):
            with self.subTest(odd=odd):
                fx = build_with(fxAtlas={"rows": odd, "cols": odd})
                self.assertEqual(([], []), (fx["atlasRows"], fx["atlasCols"]))
        for odd in (5, [], ["a"], {}, {"a": 1}, None, ""):
            with self.subTest(file=odd):
                fx = build_with(fxAtlas={"file": odd}, strip={"file": odd, "frames": "4"})
                self.assertEqual((None, None, None), (fx["atlas"], fx["strip"], fx["stripFrames"]))
        for odd in (float("nan"), float("inf"), [float("nan")], {"a": float("inf")}, 10 ** 400):
            with self.subTest(value=str(odd)[:9]):
                build_with(fx={"throttle": odd, "module": odd, "plate": odd, "hold": odd})
        views = json.loads(json.dumps(self.sweep["sidecars"][FX_RCS]["fx"]["views"]))
        views[0]["compare"]["lengthRatio"] = 10 ** 400
        build_with(fx={"views": views})
        for key in pp.FX_KEYS:
            for odd in (None, 5, "x", [], {}, [None]):
                with self.subTest(key=key, odd=odd):
                    build_with(fx={key: odd})

    def test_triggers(self):
        self.assertIn("event route: flame length 0.576 in fx-side-a", self.by_id[FX_SHORT]["trigger"])
        self.assertTrue(self.by_id[FX_REF]["trigger"].startswith("reference, would be fail: "))
        self.assertIn("ghost draws no flame in fx-side-a", self.by_id[FX_REF]["trigger"])
        self.assertEqual("", self.by_id[FX_REF_OK]["trigger"])
        self.assertIn("the real part draws no effect", self.by_id[FX_JET]["trigger"])
        self.assertEqual("effect did not converge (capped at warn)",
                         ppv.reason_phrase(pp.REASON_FX_UNSETTLED))
        self.assertEqual("ghost missing effect k", ppv.reason_phrase("missing-fx:k"))
        self.assertEqual("ghost draws smoke the real part does not in fx-far",
                         ppv.reason_phrase("fx-far:smoke:smoke-extra"))

    def test_the_half_that_decided_leads_the_trigger(self):
        tpp = _tpp()
        doc = tpp._fx_case(FX_SHORT, event=tpp._broken("short"))[0]
        e = next(e for e in doc["fx"]["views"] if e["view"] == "fx-side-a")
        e["compare"]["coreIou"] = 0.4  # a warn-level finding before the failing views
        doc["views"][0]["colorBlobPx"] = 100  # and a warn-level body
        line = ppv.trigger_line(ppv.trigger_reasons(pp.judge_case(doc), doc))
        self.assertTrue(line.startswith("event route: flame length"), line)
        self.assertNotIn("core-iou", line)
        doc["views"][0]["colorBlobPx"] = pp.COLOR_BLOB_FAIL_PX + 1  # now the body fails too
        line = ppv.trigger_line(ppv.trigger_reasons(pp.judge_case(doc), doc))
        self.assertTrue(line.startswith("event route: colour blob"), line)

    def test_a_skin_flag_or_setup_that_decided_leads_a_lesser_fx_half(self):
        tpp = _tpp()

        def line(edit, **kw):
            doc = tpp._fx_case(FX_SHORT, **kw)[0]
            for e in doc["fx"]["views"]:
                e["compare"]["coreIou"] = 0.4  # the FX half is warn
            edit(doc)
            return ppv.trigger_line(ppv.trigger_reasons(pp.judge_case(doc), doc))
        self.assertTrue(line(lambda d: d.update(skin=variant_skin("A", ghost="B")))
                        .startswith("skin mismatch"))
        self.assertTrue(line(lambda d: d["stateProof"]["startFirstFrame"].update(atTarget=False))
                        .startswith("start route not at its pose"))
        self.assertTrue(line(lambda d: [v.pop("start") for v in d["views"]], event=tpp._broken("short"))
                        .startswith("start route not measured"))
        self.assertTrue(line(lambda d: d["setup"].update(status="crashed")).startswith("setup fault"))
        self.assertTrue(line(lambda d: None).startswith("event route: flame core-iou"))


class FxControlTests(_FxSetMixin, unittest.TestCase):
    def test_control_set_kinds(self):
        self.assertEqual("fx-orbit", ppv.control_set_kind(self.sweep["manifest"]))
        self.assertEqual("fx-air", ppv.control_set_kind({"settings": {"fx": "plume", "contexts": "air"}}))
        self.assertEqual("stowed", ppv.control_set_kind({"settings": {"fx": "none"}}))
        self.assertEqual("fx-orbit", self.data["controlSets"][0]["kind"])
        self.assertEqual("ctl:fx-orbit:ctl-empty", self.by_id["ctl-empty"]["regKey"])

    def test_not_applicable_is_neither_ok_nor_bad(self):
        row = self.by_id["ctl-plate-light-off-point"]
        self.assertEqual((ppv.VERDICT_CONTROL_NA, "n/a"), (row["verdict"], row["control"]["outcome"]))
        self.assertEqual({"ok": 10, "bad": 0}, self.data["counts"]["controls"])
        self.assertEqual(1, self.data["counts"]["controlsNa"])
        self.assertTrue(self.data["sweeps"][0]["controls"]["ok"])

    def test_a_required_controls_na_is_bad(self):
        self._edit_manifest(lambda m: [c.update(outcome="n/a") for c in m["controls"]
                                       if c["id"] == "ctl-fx-plume-hue"])
        data = ppv.build_site_data([ppv.load_sweep(self.dir)])
        row = [c for c in data["cases"] if c["caseId"] == "ctl-fx-plume-hue"][0]
        self.assertEqual(ppv.VERDICT_CONTROL_BAD, row["verdict"])
        self.assertEqual(1, data["counts"]["controls"]["bad"])

    def test_a_control_recorded_ok_is_re_judged_by_judge_fx_control(self):
        def intact(doc):
            for e in doc["fx"]["views"]:
                e["ghost"], e["compare"] = e["real"], dict(e["compare"], lengthRatio=1.0)
        self._edit("ctl-fx-plume-short", intact)
        data = ppv.build_site_data([ppv.load_sweep(self.dir)])
        row = [c for c in data["cases"] if c["caseId"] == "ctl-fx-plume-short"][0]
        self.assertEqual((ppv.VERDICT_CONTROL_BAD, pp.CONTROLS_BAD, "length passes"),
                         (row["verdict"], row["control"]["judged"], row["control"]["judgedDetail"]))
        self.assertFalse(data["sweeps"][0]["controls"]["ok"])

    def test_a_controls_rows_say_what_failed(self):
        failing = {e["view"]: e["failing"] for e in self.by_id["ctl-fx-plume-off"]["fx"]["entries"]
                   if e["layer"] == "flame"}
        self.assertEqual({v: ["presence"] for v in pp.FX_VIEWS}, failing)
        self.assertIsNone(self.by_id[FX_BURN]["fx"]["entries"][0]["failing"])
        self.assertEqual({"event"}, {e["route"] for e in self.by_id["ctl-fx-plume-off"]["fx"]["entries"]})


class FxPageDataTests(_FxSetMixin, unittest.TestCase):
    def test_reference_is_counted_beside_the_judged_verdicts(self):
        counts = self.data["counts"]
        self.assertEqual({"pass": 2, "warn": 2, "fail": 1, "error": 0}, counts["cases"])
        self.assertEqual({"cases": 2, "parts": 1}, counts["reference"])
        self.assertEqual(2, self.data["sweeps"][0]["counts"][pp.VERDICT_REFERENCE])
        self.assertIn(pp.VERDICT_REFERENCE, ppv.VERDICT_ORDER)
        g = [g for g in self.data["groups"] if g["part"] == "engineRef"][0]
        self.assertEqual((pp.VERDICT_REFERENCE, 2, ["reference"]),
                         (g["verdict"], g["reference"], g["refs"]))
        self.assertIn("would be fail", g["trigger"])
        self.assertEqual("engineRef", self.data["groups"][-1]["part"])  # below the passes

    def test_a_weak_core_reads_as_a_chip_and_a_phrase(self):
        self.assertIn(pp.FLAG_FX_WEAK_CORE, ppv.FX_FLAG_FILTER_KINDS)
        flag = "fx-weak-core:event:fx-far:smoke:real=39,ghost=79177"
        self.assertEqual([pp.FLAG_FX_WEAK_CORE], ppv._fx_flag_kinds([flag]))
        self.assertEqual("event route: weak smoke core in fx-far (real 39 px, ghost 79177 px)",
                         ppv.reason_phrase("flag:" + flag))

    def test_worst_with_reference(self):
        self.assertEqual("reference", ppv.worst(["reference", "reference"]))
        self.assertEqual("pass", ppv.worst(["reference", "pass"]))
        self.assertEqual("error", ppv.worst(["reference", "error"]))

    def test_filters(self):
        f = self.data["facets"]
        self.assertEqual([["plume", 5], ["rcs", 1]], f["family"])
        self.assertEqual([["burn-full", 5], ["burn-low", 1], ["rcs-fire", 1]], f["state"])
        self.assertEqual([["judged", 5], ["reference", 1]], f["reference"])
        self.assertEqual([["rcs-known-difference", 1], ["fx-unsettled", 1],
                          ["fx-no-real-effect", 1]], f["fxFlag"])
        self.assertEqual(["reference", 1], f["verdict"][-1])
        rcs = [g for g in self.data["groups"] if "rcs-known-difference" in g["fxFlags"]]
        self.assertEqual(["RCSBlock.v2"], [g["part"] for g in rcs])
        # a sweep with no FX case offers neither filter
        plain = build()
        self.assertEqual(([], []), (plain["facets"]["reference"], plain["facets"]["fxFlag"]))
        self.assertIsNone(plain["fxThresholds"])
        self.assertIsNone(plain["sweeps"][0]["fx"])

    def test_an_fx_case_joins_its_skin_after_the_p6_states(self):
        rows = [{"state": s, "family": f, "context": c, "sweepIndex": 0, "index": 0, "caseId": s}
                for s, f, c in (("heat-hot", "heat", "alone"), ("burn-full", "plume", "alone"),
                                ("lit", "light", "plate"), ("jettisoned", "jettison", "alone"),
                                ("stowed", None, "alone"), ("rcs-fire", "rcs", "alone"),
                                ("main-full", "plume", "alone"),
                                ("burn-low", "plume", "alone"))]
        rows.sort(key=ppv.state_order_key)
        self.assertEqual(["stowed", "jettisoned", "burn-full", "burn-low", "main-full", "rcs-fire",
                          "heat-hot", "lit"], [r["state"] for r in rows])
        skins = [g for g in self.data["groups"] if g["part"] == "engineRef"][0]["skins"]
        self.assertEqual([[self.by_id[FX_REF]["id"], self.by_id[FX_REF_OK]["id"]]],
                         [s["cases"] for s in skins])

    def test_the_header_counts_effects_and_runs_the_fx_writer_checks(self):
        h = self.data["sweeps"][0]
        self.assertEqual(({"plume": 6, "rcs": 1}, 2, {"fail": 1, "pass": 1}),
                         (h["fx"]["families"], h["fx"]["reference"], h["fx"]["referenceWouldBe"]))
        self.assertNotIn("thresholds", h["fx"])
        self.assertEqual(pp.FX_THRESHOLDS, self.data["fxThresholds"])
        # only FX_LOOSE's hand-edited half is a writer contradiction
        self.assertTrue(all(p.startswith(FX_LOOSE) for p in h["instrumentProblems"]),
                        h["instrumentProblems"])
        self._edit_manifest(lambda m: [c.update(family="rcs") for c in m["cases"]
                                       if c["caseId"] == FX_SHORT])
        h = ppv.build_site_data([ppv.load_sweep(self.dir)])["sweeps"][0]
        self.assertIn(FX_SHORT + " row family=rcs fx.family=plume", h["instrumentProblems"])
        self._edit(FX_BURN, lambda doc: doc["fx"].update(family="heat"))
        h = ppv.build_site_data([ppv.load_sweep(self.dir)])["sweeps"][0]
        self.assertTrue(any(p.startswith(FX_BURN) for p in h["instrumentProblems"]))

    def test_a_plate_case(self):
        doc = _tpp()._fx_control("ctl-plate-light-off")[0]
        row = ppv.case_row("p", "lamp--default--lit--plate--1", doc, None, None, True, False, None)
        self.assertEqual((0.4, [], None), (row["fx"]["plate"]["litPct"], row["fx"]["entries"],
                                           row["fx"]["atlas"]))
        self.assertEqual({}, row["fx"]["routes"])


class FxRegressionTests(_FxSetMixin, unittest.TestCase):
    def _status(self, cur_dir, base_dir):
        data = ppv.build_site_data([ppv.load_sweep(cur_dir)], [ppv.load_sweep(base_dir)])
        return {c["caseId"]: c["regression"] for c in data["cases"]}

    def test_same_then_moved_by_entry_key(self):
        other = fx_sweep_copy(self.tmp, "again")
        reg = self._status(self.dir, other)
        self.assertEqual({ppv.REG_SAME}, {r["status"] for r in reg.values()})

        def longer(doc):
            e = next(e for e in doc["fx"]["views"] if (e["route"], e["view"], e["layer"])
                     == ("start", "fx-far", "flame"))
            e["compare"]["lengthRatio"] = 1.3
            e["compare"]["widthRatio"] = 1.01  # under its warn line: noise
            e["compare"]["originDeltaPx"] = 1.0
            e["compare"]["lateralOffsetPx"] = -9.0
        self._edit(FX_BURN, longer)
        self._edit(FX_REF, lambda d: d["fx"].update(
            views=json.loads(json.dumps(self.sweep["sidecars"][FX_REF_OK]["fx"]["views"]))))
        reg = self._status(self.dir, other)
        key = FX_BURN + "/fx/fx-far/flame/start"
        self.assertIn("verdict pass -> fail", reg[FX_BURN]["changes"])
        self.assertIn(key + " verdict pass -> fail", reg[FX_BURN]["changes"])
        self.assertIn(key + " lengthRatio 1.000 -> 1.300", reg[FX_BURN]["changes"])
        self.assertIn(key + " lateralOffsetPx 0.000 -> -9.000", reg[FX_BURN]["changes"])
        for under in ("widthRatio", "originDeltaPx"):
            self.assertFalse(any(under in c for c in reg[FX_BURN]["changes"]), under)
        self.assertIn("fx.wouldBe fail -> pass", reg[FX_REF]["changes"])
        self.assertIn(FX_REF + "/fx/fx-side-a/flame ghost.corePx 0 -> %d" % next(
            e["ghost"]["corePx"] for e in self.sweep["sidecars"][FX_REF_OK]["fx"]["views"]),
            reg[FX_REF]["changes"])
        self.assertEqual(ppv.REG_SAME, reg[FX_RCS]["status"])

    def test_flags_and_presence(self):
        base = {"flags": ["fx-unsettled"], "entries": [], "wouldBe": None}
        cur = {"flags": ["rcs-known-difference:nozzles=1/4,power=0.250"], "entries": [], "wouldBe": None}
        self.assertEqual(["fx.flags +rcs-known-difference:nozzles=1/4,power=0.250 -fx-unsettled"],
                         ppv.fx_changes(cur, base))
        self.assertEqual(["fx absent -> present"], ppv.fx_changes(cur, None))
        # a rendered FX asset the ghost newly lacks or adds moves a case that already failed
        other = fx_sweep_copy(self.tmp, "struct")
        self._edit(FX_SHORT, lambda d: d["fx"]["structure"].update(
            fxMissingOnGhost=["a", {"key": "b", "rendered": False}], fxExtraOnGhost=["c", "c"]))
        reg = self._status(self.dir, other)[FX_SHORT]
        self.assertEqual(ppv.REG_MOVED, reg["status"])
        self.assertEqual(["fx.missingRendered a 0 -> 1", "fx.extraRendered c 0 -> 2"], reg["changes"])
        self.assertEqual([], ppv.fx_changes(None, None))


class FxSiteTests(_FxSetMixin, unittest.TestCase):
    def test_a_site_with_strips_and_the_known_difference_row(self):
        out = os.path.join(self.tmp, "site")
        self.assertEqual(0, ppv.main([self.dir, "--out", out]))
        with open(os.path.join(out, ppv.DATA_JS_FILE), encoding="ascii") as fh:
            data = ppv.parse_data_js(fh.read())
        for c in data["cases"]:
            if c["kind"] == ppv.KIND_PART:
                for url in (c["fx"]["atlas"], c["fx"]["strip"]):
                    self.assertTrue(os.path.isfile(os.path.join(out, *url.split("/"))), url)
        with open(os.path.join(out, ppv.INDEX_FILE), encoding="ascii") as fh:
            page = fh.read()
        p = _Balance()
        p.feed(page)
        self.assertEqual([], p.errors)
        CliTests._node_check(self, out, page)

    def test_the_diff_of_two_equal_covered_tiles_is_black(self):
        # Each FX layer paints its own opaque ground, so a partly covered pixel differences
        # against its flattened twin (an alpha layer would leave a residue).
        self.assertIn(".tile.fx-flame, .tile.fx-flame .layer { background-color: #000; }", ppv.INDEX_HTML)
        self.assertIn(".tile.fx-smoke, .tile.fx-smoke .layer { background-color: var(--tilebg); }",
                      ppv.INDEX_HTML)

    def test_the_page_reads_only_fx_keys_the_core_writes(self):
        used = set(re.findall(r"\bfx\.([A-Za-z]+)", ppv.INDEX_HTML))
        keys = set(self.by_id[FX_RCS]["fx"]) | set(self.data["sweeps"][0]["fx"])
        self.assertEqual(set(), used - keys)
        entry = set(self.by_id[FX_RCS]["fx"]["entries"][0])
        body = ppv.INDEX_HTML[ppv.INDEX_HTML.index("function fxLine"):ppv.INDEX_HTML.index("function fxViews")]
        self.assertEqual(set(), set(re.findall(r"\be\.([A-Za-z]+)", body)) - entry)


def _fx_baselines(sweep):
    """The baseline files a sweep's own reading would pin: every page state needs one."""
    docs = {c: s for c, s in sweep["sidecars"].items() if c not in pp.ALL_CONTROL_IDS}
    manifest = sweep["manifest"]
    section = pp.build_baseline_section([("run", manifest, docs)], "2026-11-24T00:00:00Z")
    return {pp.profile_of(manifest, None): {"labels": {manifest["label"]: section}}}


class PageStateTests(_FxSetMixin, unittest.TestCase):
    """``known`` / ``accepted`` and the owner's mark on the page (design section 8)."""

    def _lists(self, kind, case_id=FX_SHORT):
        cv = pp.judge_case(self.sweep["sidecars"][case_id])
        entries = [pp.AcceptedEntry(case_id, "*", g, "owner ruling 2026-11-22: cosmetic", "SOME-TODO-1",
                                    kind=kind) for g in sorted({f.group for f in pp.fail_reasons(cv)})]
        return {"accepted": entries, "baselines": _fx_baselines(self.sweep), "errors": []}

    def test_a_sweep_with_no_baseline_shows_the_fail(self):
        lists = dict(self._lists("ruled"), baselines={})
        c = {c["caseId"]: c for c in self._build(lists)["cases"]}[FX_SHORT]
        self.assertEqual((pp.VERDICT_FAIL, None), (c["verdict"], c["accepted"]))

    def _build(self, lists, **kw):
        return ppv.build_site_data([self.sweep], lists=lists, **kw)

    def test_a_ruled_fail_reads_known_and_is_counted_apart(self):
        before = self.data["counts"]["cases"]["fail"]
        for kind, state in (("ruled", pp.PAGE_KNOWN), ("accepted", pp.PAGE_ACCEPTED)):
            data = self._build(self._lists(kind))
            c = {c["caseId"]: c for c in data["cases"]}[FX_SHORT]
            self.assertEqual((state, pp.VERDICT_FAIL), (c["verdict"], c["partVerdict"]))
            self.assertEqual(["SOME-TODO-1"], c["accepted"]["todos"])
            self.assertTrue(c["trigger"].startswith("%s (SOME-TODO-1): " % state))
            counts = data["counts"]
            self.assertEqual(before - 1, counts["cases"]["fail"])
            self.assertEqual({"cases": 1, "parts": 1}, counts["page"][state])
            head = data["sweeps"][0]["counts"]
            self.assertEqual((before - 1, 1), (head[pp.VERDICT_FAIL], head[state]))
            g = [g for g in data["groups"] if g["part"] == "engineShort"][0]
            self.assertEqual(state, g["verdict"])
            self.assertIn([state, 1], data["facets"]["verdict"])

    def test_a_defect_no_list_and_a_wrong_case_show_the_fail(self):
        for lists in (None, self._lists("defect"), self._lists(None),
                      {"accepted": [], "baselines": {}, "errors": []}):
            c = {c["caseId"]: c for c in self._build(lists)["cases"]}[FX_SHORT]
            self.assertEqual((pp.VERDICT_FAIL, None), (c["verdict"], c["accepted"]))

    def test_the_states_rank_under_every_finding_and_over_pass(self):
        order = (pp.VERDICT_FAIL, pp.VERDICT_WARN, pp.VERDICT_ERROR, pp.PAGE_ACCEPTED,
                 pp.PAGE_KNOWN, pp.VERDICT_PASS, pp.VERDICT_REFERENCE)
        sev = [ppv.SEVERITY[v] for v in order]
        self.assertEqual(sorted(sev, reverse=True), sev)
        self.assertEqual(len(set(sev)), len(sev))
        self.assertEqual(pp.VERDICT_WARN, ppv.worst([pp.PAGE_KNOWN, pp.VERDICT_WARN, pp.PAGE_ACCEPTED]))

    def test_the_regression_compares_judged_verdicts(self):
        data = self._build(self._lists("ruled"), baseline=[self.sweep])
        self.assertEqual(0, data["regression"]["counts"][ppv.REG_MOVED])

    def test_an_owner_flagged_case_is_marked_whatever_its_verdict_and_never_known(self):
        def flag(doc):
            for e in doc["fx"]["views"]:
                if e["layer"] == "smoke" and e["route"] == "event":
                    for half in [e] + [e["splitHalf"][h] for h in ("even", "odd")]:
                        half["real"]["corePx"] = 0
        self._edit(FX_BURN, flag)
        self.sweep = dict(ppv.load_sweep(self.dir), imageUrl=self.sweep["imageUrl"])
        with _tpp().owner_flagged():
            data = self._build(self._lists("ruled", FX_BURN))
        plain = {c["caseId"]: c for c in self._build(self._lists("ruled", FX_BURN))["cases"]}[FX_BURN]
        self.assertEqual([], plain["ownerFlags"])  # the committed set marks nothing today
        c = {c["caseId"]: c for c in data["cases"]}[FX_BURN]
        self.assertEqual((["smoke-extra"], pp.VERDICT_FAIL), (c["ownerFlags"], c["verdict"]))
        self.assertEqual(1, data["counts"]["ownerFlagged"])
        self.assertIn([ppv.FX_FLAG_OWNER_FLAGGED, 1], data["facets"]["fxFlag"])
        g = [g for g in data["groups"] if FX_BURN in "".join(g["cases"])][0]
        self.assertEqual(["smoke-extra"], g["ownerFlags"])

    def test_the_committed_lists_are_read_without_pinned(self):
        out = os.path.join(self.tmp, "site2")
        with mock.patch.object(ppv, "load_pinned", wraps=ppv.load_pinned) as lp:
            self.assertEqual(0, ppv.main([self.dir, "--out", out]))
        self.assertEqual(ppv.DEFAULT_PINNED_DIR, lp.call_args[0][0])
        with open(os.path.join(out, ppv.DATA_JS_FILE), encoding="ascii") as fh:
            self.assertIsNone(ppv.parse_data_js(fh.read())["regression"])


# A DOM small enough to run the page's own script under node: every body is built at once
# (no IntersectionObserver), then the harness below opens everything and reads it back.
PAGE_RUNNER = r"""
const fs = require('fs'), req = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const byId = {};
class N {
  constructor(t) { this.tagName = t.toUpperCase(); this.children = []; this.attrs = {}; this.style = {}; this.L = {}; this.parentNode = null; this._t = ''; this.hidden = false; this._c = new Set();
    const st = this.style; Object.defineProperty(st, 'setProperty', {value: (k, v) => { st[k] = String(v); }, enumerable: false});
    const c = this._c; this.classList = {add: x => c.add(x), remove: x => c.delete(x), contains: x => c.has(x), toggle: (x, on) => { if (on === undefined) on = !c.has(x); on ? c.add(x) : c.delete(x); return on; }}; }
  set className(v) { this._c.clear(); String(v).split(/\s+/).forEach(x => x && this._c.add(x)); }
  get className() { return [...this._c].join(' '); }
  set textContent(v) { this.children = []; this._t = v === null ? '' : String(v); }
  get textContent() { return this._t + this.children.map(c => c.textContent).join(''); }
  setAttribute(k, v) { this.attrs[k] = String(v); if (k === 'id') byId[v] = this; if (k === 'value') this.value = String(v); }
  getAttribute(k) { return k in this.attrs ? this.attrs[k] : null; }
  hasAttribute(k) { return k in this.attrs; }
  appendChild(c) { if (!(c instanceof N)) throw new TypeError('not a node: ' + c);
    if (c.tagName === '#FRAG') { c.children.slice().forEach(x => this.appendChild(x)); return c; }
    if (c.parentNode) c.parentNode.children = c.parentNode.children.filter(x => x !== c);
    c.parentNode = this; this.children.push(c); return c; }
  remove() { if (this.parentNode) this.parentNode.children = this.parentNode.children.filter(x => x !== this); }
  addEventListener(e, f) { (this.L[e] = this.L[e] || []).push(f); }
  fire(e) { (this.L[e] || []).forEach(f => f({target: this, preventDefault() {}})); }
  all() { return this.children.reduce((r, c) => r.concat([c], c.all()), []); }
  cls(x) { return this.all().filter(n => n._c.has(x)); }
  querySelectorAll(q) { return q === '.layer[data-src]' ? this.all().filter(n => n._c.has('layer') && n.hasAttribute('data-src')) : []; }
  closest(q) { for (let n = this; n; n = n.parentNode) { if (q === 'dialog' ? n.tagName === 'DIALOG' : n._c.has('tile') && n.hasAttribute('data-case')) return n; } return null; }
  getBoundingClientRect() { return {width: 200, height: 20, top: 0, left: 0, bottom: 0}; }
  showModal() { this.open = true; } close() { this.open = false; } focus() {} blur() {}
}
const body = new N('body'), docL = {};
const document = {body: body, createElement: t => new N(t), createElementNS: (ns, t) => new N(t), createTextNode: t => { const n = new N('#text'); n._t = String(t); return n; }, createDocumentFragment: () => new N('#frag'),
  getElementById: i => { if (!byId[i]) { const n = new N(i === 'zoom' ? 'dialog' : 'div'); body.appendChild(n); n.setAttribute('id', i); } return byId[i]; },
  addEventListener(e, f) { (docL[e] = docL[e] || []).push(f); }, querySelectorAll: () => []};
const text = fs.readFileSync(req.data, 'utf8');
const window = {PARITY_DATA: JSON.parse(text.slice(text.indexOf('=') + 1).trim().replace(/;$/, '')), addEventListener() {}, scrollTo() {}, scrollY: 0,
  matchMedia: () => ({matches: req.reduce === true, addEventListener() {}})};
new Function('window', 'document', 'localStorage', 'location', 'history', 'setTimeout', 'clearTimeout', 'requestAnimationFrame', req.script)(
  window, document, {getItem: () => null, setItem() {}}, {hash: ''}, {replaceState() {}}, () => 0, () => 0, () => 0);
const ctl = document.getElementById('controls'); ctl.open = true; ctl.fire('toggle');
body.all().filter(n => n.tagName === 'DETAILS').forEach(n => { n.open = true; n.fire('toggle'); });
const own = n => n.children.filter(c => c.className !== 'fxbox');
const node = n => ({cls: n.className, text: n.textContent});
const layer = n => ({style: n.getAttribute('style'), src: n.getAttribute('data-src'), cls: n.className});
const fig = f => ({cls: f.className, cap: f.cls('full').map(x => x.textContent).join(''), tile: (f.cls('tile')[0] || {className: ''}).className, layers: f.cls('layer').map(layer)});
const row = r => ({name: r.cls('vname')[0].textContent, figs: r.children.filter(c => c.tagName === 'FIGURE').map(fig), meta: r.cls('vmeta')[0].all().filter(c => c.tagName === 'DIV' && !c.children.some(x => x.tagName === 'DIV')).map(node), chips: r.cls('vc').map(node), metaText: r.cls('vmeta')[0].textContent});
const out = {cases: {}, play: body.classList.contains('stripsplay'), groups: {}};
body.all().filter(n => n._c.has('state') || n._c.has('skin')).forEach(b => {
  if (b.cls('state').length) return;
  const tile = b.cls('tile')[0], id = tile ? tile.getAttribute('data-case') : b.cls('skname')[0].textContent;
  const box = b.children.filter(c => c._c.has('fxbox'))[0];
  const rows = box ? box.children.filter(c => c._c.has('vrow')) : [];
  const known = box ? box.children.filter(c => c._c.has('knownbox'))[0] : null;
  out.cases[id] = {cls: b.className, head: b.children[0].children.map(node), headChips: b.children[0].cls('vc').map(node), text: b.textContent,
    strip: rows.filter(r => r.cls('film').length).map(row), rows: rows.filter(r => !r.cls('film').length).map(row),
    body: b.children.filter(c => c._c.has('vrow')).map(row),
    notes: box ? box.children.filter(c => c._c.has('note')).map(c => c.textContent) : null, box: !!box,
    known: known ? {chips: known.cls('chip').map(node), cap: known.cls('cap')[0].textContent, rows: known.cls('vrow').map(row), all: known.all().map(c => c.className), text: known.textContent} : null,
    tables: b.all().filter(n => n.tagName === 'TABLE').map(t => t.all().filter(n => n.tagName === 'TR').map(r => r.children.map(c => c.textContent))),
    kv: b.cls('kv').map(k => k.children.map(c => c.textContent)).pop() || []};
});
const shown = () => body.cls('group').filter(g => !g.hidden).map(g => g.cls('name')[0].textContent).sort();
const select = (label, v) => { const s = byId.top.all().filter(n => n.tagName === 'SELECT' && n.getAttribute('aria-label') === label)[0]; if (!s) return null; s.value = v; s.fire('change'); const r = shown(); s.value = ''; s.fire('change'); return r; };
const button = label => byId.top.all().filter(n => n.tagName === 'BUTTON' && n.textContent === label)[0];
out.flagged = select('Verdict', 'flagged'); out.reference = select('Reference', 'reference'); out.plume = select('Family', 'plume');
const kb = button('known difference');
if (kb) { kb.fire('click'); out.knownShown = shown(); out.knownPressed = kb.getAttribute('aria-pressed'); kb.fire('click'); out.knownOff = shown().length; }
const pb = byId.stripbtn;
if (pb) { out.btn = [pb.textContent]; for (let i = 0; i < 2; i++) { pb.fire('click'); out.btn.push(pb.textContent, body.classList.contains('stripsplay')); } }
out.zoom = {};
body.all().filter(n => n._c.has('tile') && n.hasAttribute('data-case') && (n.hasAttribute('data-layer') || n.getAttribute('data-view') === 'strip')).forEach(n => {
  const key = [n.getAttribute('data-case'), n.getAttribute('data-view'), n.getAttribute('data-layer')].join('|');
  if (key in out.zoom) return;
  docL.click.forEach(f => f({target: n}));
  out.zoom[key] = document.getElementById('zoombody').all().filter(x => x.tagName === 'FIGURE').map(fig);
});
out.counters = byId.top.children[0].children.map(node);
out.fxlegend = byId.fxlegend ? !byId.fxlegend.hidden : null;
fs.writeFileSync(req.out, JSON.stringify(out));
"""


@unittest.skipUnless(shutil.which("node"), "node is not on PATH")
class FxPageTests(unittest.TestCase):
    """The page's own script over the synthetic FX sweep, read back from the built nodes."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="ppv-fxpage-")
        cls.addClassCleanup(shutil.rmtree, cls.tmp, True)
        sweep = fx_sweep_copy(cls.tmp)
        with contextlib.redirect_stdout(io.StringIO()):
            assert 0 == ppv.main([sweep, "--out", os.path.join(cls.tmp, "site")])
        cls.page = cls.run_page(os.path.join(cls.tmp, "site", ppv.DATA_JS_FILE))
        with open(os.path.join(cls.tmp, "site", ppv.DATA_JS_FILE), encoding="ascii") as fh:
            cls.data = ppv.parse_data_js(fh.read())
        cls.ids = {c["caseId"]: c["id"] for c in cls.data["cases"]}

    @classmethod
    def run_page(cls, data_js, reduce=False):
        page = ppv.INDEX_HTML
        script = page[page.rindex("<script>") + len("<script>"):page.rindex("</script>")]
        paths = [os.path.join(cls.tmp, n) for n in ("runner.js", "req.json", "out.json")]
        with open(paths[0], "w", encoding="utf-8") as fh:
            fh.write(PAGE_RUNNER)
        with open(paths[1], "w", encoding="utf-8") as fh:
            json.dump({"data": data_js, "script": script, "reduce": reduce, "out": paths[2]}, fh)
        r = subprocess.run([shutil.which("node"), paths[0], paths[1]], capture_output=True, text=True)
        if r.returncode:
            raise AssertionError(r.stderr)
        with open(paths[2], encoding="utf-8") as fh:
            return json.load(fh)

    def case(self, case_id):
        return self.page["cases"][self.ids[case_id]]

    def test_the_known_accepted_owner_and_loaded_states_read_on_the_page(self):
        tmp = tempfile.mkdtemp(prefix="ppv-states-")
        self.addCleanup(shutil.rmtree, tmp, True)
        sweep = ppv.load_sweep(fx_sweep_copy(tmp))
        sweep["imageUrl"] = lambda n: n
        docs = sweep["sidecars"]
        for e in docs[FX_BURN]["fx"]["views"]:
            if e["layer"] == "smoke" and e["route"] == "event":
                for half in [e] + [e["splitHalf"][h] for h in ("even", "odd")]:
                    half["real"]["corePx"] = 0
        docs[FX_JET]["atlas"]["rows"] = list(pp.ATLAS_ROWS_STATE_RELOADED)
        docs[FX_JET]["flags"] = [pp.FLAG_START_RELOADED_PREFIX + "1"]
        cv = pp.judge_case(docs[FX_SHORT])
        lists = {"baselines": _fx_baselines(sweep), "errors": [], "accepted": [
            pp.AcceptedEntry(FX_SHORT, "*", g, "owner ruling: fine", "SOME-TODO-1", kind="ruled")
            for g in {f.group for f in pp.fail_reasons(cv)}]}
        with _tpp().owner_flagged():
            data = ppv.build_site_data([sweep], lists=lists)
        path = os.path.join(tmp, "data.js")
        with open(path, "w", encoding="ascii") as fh:
            fh.write(ppv.render_data_js(data))
        page = self.run_page(path)
        ids = {c["caseId"]: c["id"] for c in data["cases"]}
        counters = [c["text"] for c in page["counters"]]
        self.assertIn("known 1", counters)
        self.assertIn("owner-flagged 1", counters)
        self.assertNotIn("accepted", " ".join(counters))
        short = page["cases"][ids[FX_SHORT]]
        self.assertEqual({"state", "known", "quiet"}, set(short["cls"].split()))
        self.assertIn("judged fail: SOME-TODO-1", [h["text"] for h in short["head"]])
        self.assertEqual("vc known", short["headChips"][0]["cls"])
        burn = page["cases"][ids[FX_BURN]]
        self.assertEqual(["owner-flagged:smoke-extra"],
                         [h["text"] for h in burn["head"] if "owner" in h["cls"].split()])
        self.assertNotIn("quiet", burn["cls"].split())
        # A known-only part leaves the flagged filter; an owner-flagged one is at least a warn.
        self.assertNotIn("engineShort", page["flagged"])
        self.assertIn([c["part"] for c in data["cases"] if c["caseId"] == FX_BURN][0], page["flagged"])
        # The loaded real part crops atlas row 5 of 5, the start ghost row 3, the burning one row 1.
        figs = {f["cap"]: f["layers"][0]["style"] for f in page["cases"][ids[FX_JET]]["body"][0]["figs"]}
        at = lambda cap: re.search(r"background-size:\d+% (\d+)%;background-position:[\d.]+% ([\d.]+)%", figs[cap]).groups()
        loaded = "REAL as a load leaves it (the start route's real side)"
        self.assertEqual(("500", "100"), at(loaded))
        self.assertEqual(("500", "50"), at("GHOST start route"))
        self.assertEqual([("500", "0")], [at(c) for c in figs if c.startswith("REAL ") and c != loaded])
        self.assertNotIn(loaded, {f["cap"] for f in short["body"][0]["figs"]})

    def crops(self, figure, case_id):
        """The (atlas row, view) each layer of a figure crops."""
        fx = [c for c in self.data["cases"] if c["caseId"] == case_id][0]["fx"]
        out = []
        for l in figure["layers"]:
            m = re.match(r"background-size:(\d+)% (\d+)%;background-position:([\d.]+)% ([\d.]+)%$", l["style"])
            cols, rows = int(m.group(1)) // 100, int(m.group(2)) // 100
            self.assertEqual((len(fx["atlasCols"]), len(fx["atlasRows"])), (cols, rows))
            self.assertEqual(fx["atlas"], l["src"])
            out.append((fx["atlasRows"][round(float(m.group(4)) / 100 * (rows - 1))],
                        fx["atlasCols"][round(float(m.group(3)) / 100 * (cols - 1))]))
        return out

    def test_the_two_strip_rows_run_in_lock_step(self):
        for cid in (FX_BURN, FX_RCS, FX_REF):
            (strip,) = self.case(cid)["strip"]
            real, ghost = strip["figs"]
            self.assertEqual(["REAL part (stock KSP)", "GHOST event route"], [real["cap"], ghost["cap"]])
            (a,), (b,) = real["layers"], ghost["layers"]
            self.assertEqual("layer film", a["cls"])
            self.assertEqual((a["cls"], a["src"]), (b["cls"], b["src"]))
            # one strip, the rows apart only in which half they crop
            self.assertTrue(a["src"].endswith(cid + pp.STRIP_SUFFIX))
            self.assertIn("background-position:0% 0%;", a["style"])
            self.assertIn("background-position:0% 100%;", b["style"])
            self.assertEqual(a["style"], b["style"].replace("0% 100%", "0% 0%"))
            self.assertIn("background-size:%d%% 200%%" % (FX_STRIP_K * 100), a["style"])
            self.assertIn("--k:%d;" % FX_STRIP_K, a["style"])
            end = float(re.search(r"--sxe:([\d.]+)%", a["style"]).group(1))
            self.assertAlmostEqual(100.0 * FX_STRIP_K / (FX_STRIP_K - 1), end)  # frame j at j/(K-1)
        # every animation the page declares: one keyframe set, one rule, one switch on <body>
        css = ppv.INDEX_HTML[:ppv.INDEX_HTML.index("</style>")]
        self.assertEqual([
            "@keyframes filmstrip { from { background-position-x: 0%; } to { background-position-x: var(--sxe); } }",
            ".layer.film { animation: filmstrip calc(var(--k) * 110ms) steps(var(--k)) infinite; animation-play-state: paused; }",
            "body.stripsplay .layer.film { animation-play-state: running; }"],
            [l for l in css.splitlines() if "animation" in l or "keyframes" in l])

    def test_play_pause_and_reduced_motion(self):
        self.assertTrue(self.page["play"])
        self.assertEqual(["Pause strips", "Play strips", False, "Pause strips", True], self.page["btn"])
        still = self.run_page(os.path.join(self.tmp, "site", ppv.DATA_JS_FILE), reduce=True)
        self.assertFalse(still["play"])
        self.assertEqual(["Play strips", "Pause strips", True], still["btn"][:3])

    def test_the_effect_rows(self):
        rows = self.case(FX_SHORT)["rows"]
        self.assertEqual(["%s %s" % (v, l) for l in pp.FX_LAYERS for v in pp.FX_VIEWS],
                         [r["name"] for r in rows])
        view, layer = rows[3]["name"].split(" ")
        figs = rows[3]["figs"]
        self.assertEqual([[("real-" + layer, view)], [("ghost-" + layer, view)],
                          [("ghost-start-" + layer, view)],
                          [("real-" + layer, view), ("ghost-" + layer, view)]],
                         [self.crops(f, FX_SHORT) for f in figs])
        self.assertEqual(["tile fx-flame"] * 3 + ["tile fx-flame diff"], [f["tile"] for f in figs])
        self.assertEqual("layer blend", figs[3]["layers"][1]["cls"])
        self.assertEqual(["vc fail", "vc pass"], [c["cls"] for c in rows[0]["chips"]])
        self.assertIn("core 204 / 120 px, length 0.58", rows[0]["metaText"])
        self.assertIn({"cls": "tr-fail", "text": "flame length 0.576"}, rows[0]["meta"])
        head = {c["text"]: c["cls"] for c in self.case(FX_SHORT)["head"]}
        self.assertIn("effects event fail", head)
        self.assertIn("effects start pass", head)

    def test_a_layer_nothing_draws_is_named_not_drawn(self):
        jet = self.case(FX_JET)
        self.assertEqual([], jet["rows"])
        self.assertEqual(1, len(jet["notes"]))
        self.assertIn("flame fx-side-a", jet["notes"][0])
        self.assertIn("chip", [c["cls"] for c in jet["head"] if c["text"] == pp.FLAG_FX_NO_REAL_EFFECT])
        off = self.case(FX_REF)  # the ghost alone is empty: the rows stay
        self.assertEqual(8, len(off["rows"]))

    def test_the_rcs_known_difference_row(self):
        c = self.case(FX_RCS)
        known = c["known"]
        self.assertEqual([{"cls": "chip known", "text": "rcs-known-difference"}], known["chips"])
        self.assertEqual("known difference (owner ruling 2026-11-22): the ghost fires all 4 nozzles at "
                         "the module's mean power 0.250; stock fired 2", known["cap"])
        self.assertEqual([r["name"] for r in c["rows"]], [r["name"] for r in known["rows"]])
        first = known["rows"][0]
        self.assertEqual([[("real-flame", "fx-side-a")], [("ghost-all-flame", "fx-side-a")]],
                         [self.crops(f, FX_RCS) for f in first["figs"]])
        self.assertIn("rcsPowerRatio 0.500", first["metaText"])
        for route in ("event", "start"):
            self.assertIn("judged row (%s), reported only: length 1.00, width 1.00, brightness 1.00"
                          % route, first["metaText"])
        # never coloured as a verdict
        self.assertEqual([], [x for x in known["all"] if re.search(r"(^| )(vc|tr-\w+)( |$)", x)])
        self.assertIn({"cls": "chip known", "text": "rcs-known-difference:nozzles=2/4,power=0.500"}, c["head"])
        # the judged rows print their length and width as reported only
        for r in c["rows"]:
            for m in r["meta"]:
                if "length " in m["text"]:
                    self.assertEqual("note", m["cls"])
                    self.assertTrue(m["text"].endswith("(reported only)"))
        self.assertIsNone(self.case(FX_BURN)["known"])
        self.assertEqual((["RCSBlock.v2"], "true", 6),
                         (self.page["knownShown"], self.page["knownPressed"], self.page["knownOff"]))

    def test_the_known_difference_follows_the_flag(self):
        tpp = _tpp()
        rows = {"none": None, "full": dict(FX_RCS_BLOCK, predicted=[0], fired=[0], nozzles=1, powerRatio=1.0)}
        for name, rcs in rows.items():
            doc = tpp._fx_case(FX_RCS, part="RCSBlock.v2", family="rcs", state="rcs-fire", rcs=rcs)[0]
            sweep = {"profile": "p", "dir": "/x", "manifest": None, "manifestError": "absent",
                     "sidecars": {FX_RCS: doc}, "imageUrl": lambda n: n}
            path = os.path.join(self.tmp, name + ".js")
            with open(path, "w", encoding="ascii") as fh:
                fh.write(ppv.render_data_js(ppv.build_site_data([sweep])))
            known = self.run_page(path)["cases"]["p/" + FX_RCS]["known"]
            self.assertEqual([], known["chips"], name)
            self.assertNotIn("owner ruling", known["cap"])
            self.assertIn("not known" if rcs is None else "no difference on this part", known["cap"])

    def test_a_reference_case_shows_its_would_be_verdict_uncoloured(self):
        c = self.case(FX_REF)
        self.assertIn("reference", c["cls"].split())
        self.assertIn({"cls": "vc reference", "text": "reference"}, c["headChips"])
        self.assertIn({"cls": "chip", "text": "would be fail"}, c["head"])
        self.assertIn({"cls": "vc reference", "text": "fail"}, c["headChips"])  # the event effects
        self.assertEqual({"vc reference"}, {x["cls"] for r in c["rows"] for x in r["chips"]})
        self.assertEqual("would be fail", c["rows"][0]["chips"][0]["text"])
        self.assertEqual([], [m for r in c["rows"] for m in r["meta"] if m["cls"].startswith("tr-")])
        self.assertEqual(["JetEngine", "engineLoose", "engineShort"], self.page["flagged"])
        self.assertEqual(["engineRef"], self.page["reference"])
        self.assertIn({"cls": "vc reference", "text": "reference 2"}, self.page["counters"])
        self.assertIn({"cls": "chip", "text": "controls 10/10 ok, 1 n/a"}, self.page["counters"])

    def test_controls_say_what_failed_and_na_says_why(self):
        off = self.case("ctl-fx-plume-off")
        # the smoke is there on both sides: its rows pass
        said = {"flame": "fails presence", "smoke": "passes every FX measure"}
        for r in off["rows"]:
            self.assertIn(said[r["name"].split(" ")[1]], r["metaText"])
        self.assertEqual(8, len(off["rows"]))
        self.assertEqual([], [c for r in off["rows"] for c in r["chips"]])
        self.assertEqual([3] * len(off["rows"]), [len(r["figs"]) for r in off["rows"]])
        na = self.page["cases"]["ctl-plate-light-off-point"]
        self.assertIn("Not applicable", na["text"])
        self.assertIn({"cls": "vc na", "text": "control n/a"}, na["headChips"])
        self.assertIn("ctl-na", na["cls"].split())

    def test_the_details(self):
        c = self.case(FX_LOOSE)
        (table,) = [t for t in c["tables"] if t[0][0] == "event flame"]
        self.assertEqual(["fx-side-a", "even half", "odd half"], table[0][1:4])
        length = [r for r in table if r[0] == "real lengthPx"][0]
        self.assertEqual(length[1], length[2])
        self.assertEqual(float(length[1]) * 2, float(length[3]))  # the half that did not converge
        self.assertIn("lengthRatio", [r[0] for r in table])
        kv = self.case(FX_RCS)["kv"]
        self.assertIn("fired [0,1] of 4 (predicted [0,1]), ghost nozzles hidden 2, input 1.000 0.000 0.000, "
                      "mean power 0.250, rcsPowerRatio 0.500", kv)
        diffs = [r for t in self.case(FX_RCS)["tables"] if t[0] == ["effect difference", "count", "detail"]
                 for r in t[1:]]
        self.assertIn(["lightsDiffer", "x2", "x real=1 ghost=2"], diffs)
        self.assertIn(["inventory ghost ghostOnly", "x1", "c"], diffs)

    def test_zoom(self):
        zoom = self.page["zoom"]
        self.assertEqual(2, len(zoom[self.ids[FX_BURN] + "|strip|"]))
        self.assertEqual([[("real-flame", "fx-far")], [("ghost-flame", "fx-far")],
                          [("ghost-start-flame", "fx-far")],
                          [("real-flame", "fx-far"), ("ghost-flame", "fx-far")],
                          [("real-flame", "fx-far"), ("ghost-start-flame", "fx-far")],
                          [("ghost-all-flame", "fx-far")]],
                         [self.crops(f, FX_RCS) for f in zoom[self.ids[FX_RCS] + "|fx-far|flame"]])

    def test_a_page_with_no_fx_case_is_unchanged(self):
        path = os.path.join(self.tmp, "plain.js")
        with open(path, "w", encoding="ascii") as fh:
            fh.write(ppv.render_data_js(build()))
        page = self.run_page(path)
        self.assertEqual([False], sorted({c["box"] for c in page["cases"].values()}))
        self.assertNotIn("btn", page)
        self.assertNotIn("knownShown", page)
        self.assertIsNone(page["reference"])
        self.assertTrue(self.page["fxlegend"])


class PageContractTests(unittest.TestCase):
    """The owner's page rules, as far as text can show them."""

    def test_offline_ascii_and_identity_captions(self):
        page = ppv.INDEX_HTML
        page.encode("ascii")
        # The one URL on the page is the SVG namespace the motion chart's elements are created
        # in (createElementNS): an identifier, never fetched.
        self.assertEqual(1, page.count('"http://www.w3.org/2000/svg"'))
        self.assertNotRegex(page.replace('"http://www.w3.org/2000/svg"', ""), r"https?://|//cdn")
        self.assertIn("REAL part (stock KSP)", page)
        self.assertIn("GHOST (Parsek reconstruction)", page)
        self.assertIn('<script src="data.js"></script>', page)

    def test_large_sweeps_stay_light(self):
        page = ppv.INDEX_HTML
        self.assertIn("content-visibility: auto", page)
        self.assertIn("contain-intrinsic-size", page)
        self.assertIn("IntersectionObserver", page)
        for tag in re.findall(r"<img\b[^>]*>", page):
            self.assertIn('loading="lazy"', tag)

    def test_no_verdict_input_exists(self):
        # The owner is the final verifier, not the detector: nothing on the page writes
        # a verdict (no textarea, no override control, no storage of verdicts).
        page = ppv.INDEX_HTML
        self.assertNotIn("<textarea", page)
        self.assertNotRegex(page, r"(?i)override|accept this|mark as")

    def test_the_page_reads_only_keys_the_core_writes(self):
        data = build()
        motion = motion_site_data()
        case_keys = set(data["cases"][0]) | {k for c in motion["cases"] for k in c} | set(malformed_motion_row())
        group_keys = set(data["groups"][0]) | {k for g in motion["groups"] for k in g}
        used_c = set(re.findall(r"\bc\.([A-Za-z]+)", ppv.INDEX_HTML))
        used_g = set(re.findall(r"\bg\.([A-Za-z]+)", ppv.INDEX_HTML))
        js_props = {"length", "id", "_i", "_text", "_built", "_group", "cases"}
        self.assertEqual(set(), used_c - case_keys - js_props, "page reads c.<key> the core never writes")
        self.assertEqual(set(), used_g - group_keys - js_props, "page reads g.<key> the core never writes")



# ---------------------------------------------------------------------------
# Legacy pages (design H.5 / H.7 M5): a site with no motion case is the page it was before
# the motion viewer, byte for byte in data.js and node for node in the built DOM.
# ---------------------------------------------------------------------------

# The DOM prelude of PAGE_RUNNER (the fake document, the script run, every <details> opened),
# then the whole body dumped: tag, classes, attributes, inline style, own text, children.
DOM_DUMP_RUNNER = PAGE_RUNNER[:PAGE_RUNNER.index("const own =")] + r"""
const dump = n => ({t: n.tagName, c: n.className, a: Object.keys(n.attrs).sort().map(k => [k, n.attrs[k]]),
  s: Object.keys(n.style).sort().map(k => [k, String(n.style[k])]), x: n._t, h: !!n.hidden, k: n.children.map(dump)});
fs.writeFileSync(req.out, JSON.stringify({body: dump(body), cls: body.className}));
"""
LEGACY_STAMP = "2026-01-01T00:00:00Z"


def legacy_sites(root):
    """(name, data.js text with ``root`` replaced, data.js path) of the three legacy sites:
    the plain stowed sweep, the stowed + orbit + air state set and the FX sweep."""
    out = []
    plain = build(generated_utc=LEGACY_STAMP)
    state_dirs = write_synthetic_state_set(os.path.join(root, "state"))
    fx_dir = fx_sweep_copy(os.path.join(root, "fxroot"))
    os.makedirs(os.path.join(root, "fxroot"), exist_ok=True)
    for name, data in (("plain", lambda: plain),
                       ("state", lambda: _site_data(root, state_dirs, "state-site")),
                       ("fx", lambda: _site_data(root, [fx_dir], "fx-site"))):
        text = ppv.render_data_js(data())
        path = os.path.join(root, name + ".js")
        with open(path, "w", encoding="ascii") as fh:
            fh.write(text)
        out.append((name, text.replace(json.dumps(root)[1:-1], "<ROOT>"), path))
    return out


def _site_data(root, dirs, site):
    sweeps = [ppv.load_sweep(d) for d in dirs]
    ppv.plan_images(sweeps, os.path.join(root, site), "copy")
    return ppv.build_site_data(sweeps, None, LEGACY_STAMP)


class LegacyPagePinTests(unittest.TestCase):
    """The legacy sites' data.js and their built DOM hash to the digests taken before the
    motion viewer (M5) was built; a motion case is the only thing that changes a page.
    The plain digests were re-taken once main reworded the B9 skin-not-modelled reason
    (mod parts); with that one line restored they equal the pre-M5 digests."""

    DATA = {
        "plain": "6045cfac3b1e6a09b938060132872fc4b34c490dc9576c7f276a944ca7897e17",
        "state": "853d6b017d3e5fc37eebd85db86da1d4140b74bd91e2c33200a0430f9d036e7c",
        "fx": "4cfeec0569d6d589aabecad3fec781ec6f325a049f019da10900a8f31ad06629",
    }
    DOM = {
        "plain": "d16f23614d48ad33642cda72af6b8b2b8920245b68bc3a4177333026eac3921b",
        "state": "e5ca49e451efbebd82d1a76e64cd99d76a65084442c1470c4e063d956a850f19",
        "fx": "9e316dbdf1ffc1106ca4dac06a8fc521d7a579dd50cd8ebbe4705bd386b85cd6",
    }

    def test_legacy_data_and_dom_are_unchanged(self):
        import hashlib
        tmp = tempfile.mkdtemp(prefix="ppv-legacy-")
        self.addCleanup(shutil.rmtree, tmp, True)
        quiet = contextlib.ExitStack()
        quiet.enter_context(contextlib.redirect_stdout(io.StringIO()))
        self.addCleanup(quiet.close)
        sites = legacy_sites(tmp)
        got_data, got_dom = {}, {}
        for name, text, path in sites:
            got_data[name] = hashlib.sha256(text.encode("ascii")).hexdigest()
            self.assertNotIn('"motion"', text)
            if shutil.which("node"):
                dom = _run_runner(tmp, DOM_DUMP_RUNNER, path)
                text = json.dumps(dom, sort_keys=True).replace(json.dumps(tmp)[1:-1], "<ROOT>")
                got_dom[name] = hashlib.sha256(text.encode("ascii")).hexdigest()
        self.assertEqual(self.DATA, got_data)
        if shutil.which("node"):
            self.assertEqual(self.DOM, got_dom)


def _run_runner(tmp, runner, data_js, reduce=False, extra=None):
    """Run ``runner`` (a node script reading req.json) over the page's script and ``data_js``."""
    page = ppv.INDEX_HTML
    script = page[page.rindex("<script>") + len("<script>"):page.rindex("</script>")]
    paths = [os.path.join(tmp, n) for n in ("runner-x.js", "req-x.json", "out-x.json")]
    with open(paths[0], "w", encoding="utf-8") as fh:
        fh.write(runner)
    req = {"data": data_js, "script": script, "reduce": reduce, "out": paths[2]}
    req.update(extra or {})
    with open(paths[1], "w", encoding="utf-8") as fh:
        json.dump(req, fh)
    r = subprocess.run([shutil.which("node"), paths[0], paths[1]], capture_output=True, text=True)
    if r.returncode:
        raise AssertionError(r.stderr)
    with open(paths[2], encoding="utf-8") as fh:
        return json.load(fh)


# ---------------------------------------------------------------------------
# Motion cases (design Appendix H.5, M5): the rows, the page's clock, scrubber and chart, and
# the animations --gif writes, over M4's synthetic motion sidecars and atlases.
# ---------------------------------------------------------------------------

MO_ANT = "longantenna--default--motion-deployed--0000aaa1"
MO_LATE = "solarpanels4--default--motion-deployed--0000aab1"
MO_SNAP = "mk2cargobays--default--motion-deployed--0000aab2"
MO_FAULT = "telescopicladder--default--motion-deployed--0000aab3"


def motion_cases(late=True):
    tpp = _tpp()
    return tpp._motion_cases() + [
        tpp._motion_case(MO_LATE, part="solarPanels4", ghost=tpp._ramp(1.5, 2.0) if late else None),
        tpp._motion_case(MO_SNAP, part="mk2CargoBayS", ghost=tpp._ramp(1.4, 0.0)),
        tpp._motion_fault(MO_FAULT)]


def write_motion_sweep(root, cases=None, **kw):
    d = os.path.join(root, "pp14")
    _tpp()._build_motion_sweep(d, motion_cases() if cases is None else cases, **kw)
    return d


def _loaded(directory):
    s = ppv.load_sweep(directory)
    s["imageUrl"] = lambda n, _f=s["files"]: n if n in _f else None
    return s


def motion_site_data(cases=None):
    root = tempfile.mkdtemp(prefix="ppv-motion-")
    try:
        return ppv.build_site_data([_loaded(write_motion_sweep(root, cases))], None, LEGACY_STAMP)
    finally:
        shutil.rmtree(root, True)


class MotionRowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="ppv-motionrow-")
        cls.addClassCleanup(shutil.rmtree, cls.tmp, True)
        cls.sweep = _loaded(write_motion_sweep(cls.tmp))
        cls.data = ppv.build_site_data([cls.sweep], None, LEGACY_STAMP)
        cls.rows = {c["caseId"]: c for c in cls.data["cases"]}

    def test_every_motion_half_is_partparitys_judgement(self):
        judged = 0
        for cid, sc in self.sweep["sidecars"].items():
            cv, row = pp.judge_case(sc), self.rows[cid]
            if cv.motion is None:
                self.assertNotIn("motion", row)
                continue
            judged += 1
            m = row["motion"]
            self.assertEqual([cv.motion.verdict, list(cv.motion.reasons), list(cv.motion.flags)],
                             [m["verdict"], m["reasons"], m["flags"]], cid)
            self.assertEqual(ppv._finite(dict(cv.motion.measures)), m["measures"], cid)
            self.assertEqual(ppv.trigger_line(list(cv.motion.reasons)), m["trigger"])
            if row["kind"] == ppv.KIND_PART:
                self.assertEqual(cv.verdict, row["verdict"], cid)
        self.assertEqual(11, judged)  # six parts (the faulted press too) and five control sidecars
        self.assertEqual(pp.VERDICT_FAIL, self.rows[MO_LATE]["motion"]["verdict"])
        self.assertIn(pp.FLAG_MOTION_SNAP, ppv.motion_flag_kinds(self.rows[MO_SNAP]["motion"]["flags"]))
        self.assertEqual(pp.VERDICT_ERROR, self.rows[MO_FAULT]["verdict"])

    def test_the_frames_atlas_and_strips_slot(self):
        row, sc = self.rows[MO_ANT], self.sweep["sidecars"][MO_ANT]
        m = row["motion"]
        self.assertEqual(sc["motionAtlas"]["rows"], m["atlasRows"])
        self.assertEqual(sc["motionAtlas"]["cols"], m["atlasCols"])
        self.assertEqual((MO_ANT + pp.MOTION_ATLAS_SUFFIX, sc["motionAtlas"]["tile"]), (m["atlas"], m["tile"]))
        self.assertEqual({"motionAtlas": m["atlas"], "motionAtlasFile": m["atlasFile"], "animation": None,
                          "animationFile": None, "animationFormat": None}, row["strips"])
        self.assertEqual(["side-a", "side-b"], m["views"])
        self.assertEqual("side-a", m["animationView"])
        self.assertTrue(m["matched"])
        self.assertFalse(self.rows[pp.CONTROL_MOTION_REAL_VS_REAL]["motion"]["matched"])
        self.assertEqual(len(sc["motion"]["frames"]), len(m["frames"]))
        for want, got in zip(sc["motion"]["frames"], m["frames"]):
            self.assertEqual([want["k"], want["realS"], want["realProgress"], want["ghostProgress"]],
                             [got["k"], got["realS"], got["realProgress"], got["ghostProgress"]])
            for wv, gv in zip(want["views"], got["views"]):
                self.assertEqual(wv["same"]["iou"], gv["same"]["iou"])
                self.assertEqual(pp.judge_view(dict(wv["same"], view=wv["view"])).verdict, gv["same"]["verdict"])
        self.assertEqual([1.0], [t["travelRatio"] for t in m["transforms"]])
        self.assertEqual([0.02], [round(t, 6) for t in m["events"]])
        self.assertEqual(ppv.MOTION_SLOW_SPEED, m["slowSpeed"])
        self.assertIsNone(self.rows["ctl-empty"]["strips"])  # a non-motion row keeps its None slot

    def test_play_delays_are_the_real_offsets(self):
        self.assertEqual([250, 250, 20, 1000], ppv.motion_frame_delays_ms([0, 0.25, 0.5, 0.505]))
        self.assertEqual([100, 1000], ppv.motion_frame_delays_ms([0, None]))
        self.assertEqual([300, 1000], ppv.motion_frame_delays_ms(["x", 1], 0.3))
        self.assertEqual([1000], ppv.motion_frame_delays_ms([0]))
        self.assertEqual([250] * 16 + [1000], self.rows[MO_ANT]["motion"]["delaysMs"])

    def test_the_chart_geometry(self):
        m, sc = self.rows[MO_LATE]["motion"], self.sweep["sidecars"][MO_LATE]
        c, t = m["chart"], m["timing"]
        self.assertEqual(len(m["frames"]), len(c["frameX"]))
        self.assertEqual(len(m["frames"]), len(c["real"].split()))
        self.assertEqual(len(sc["motion"]["scout"]["s"]), len(c["ghost"].split()))
        self.assertEqual([c["press"], c["press"]], [c["frameX"][0], ppv.MOTION_CHART_PAD[0]])
        for side in ("real", "ghost"):
            for lv in ppv.MOTION_CHART_LEVELS:
                self.assertEqual(ppv._chart_x(t[side][lv], c["tMax"]), c["marks"][side][lv])
        self.assertEqual([c["marks"]["real"]["t50"], c["marks"]["ghost"]["t50"]], c["lag"])
        self.assertGreater(c["lag"][1], c["lag"][0])  # the late ghost's t50 is right of the real's
        self.assertEqual(ppv._chart_y(0.5), c["levelY"]["t50"])
        for pts in (c["real"], c["ghost"]):
            for xy in pts.split():
                x, y = map(float, xy.split(","))
                self.assertTrue(c["pad"][0] <= x <= c["w"] - c["pad"][1] and c["y1"] <= y <= c["y0"], xy)
        self.assertEqual(1, len(c["events"]))

    def test_normal_routes_sit_under_their_controls(self):
        ids = [c["caseId"] for c in self.data["cases"]]
        grouped = {cid for g in self.data["groups"] for cid in g["cases"]}
        for cid in pp.MOTION_CONTROLS_WITH_NORMAL:
            normal = pp.motion_normal_id(cid)
            parent, row = self.rows[cid], self.rows[normal]
            self.assertEqual(ids.index(cid) + 1, ids.index(normal))
            self.assertEqual((ppv.KIND_CONTROL, parent["verdict"]), (row["kind"], row["verdict"]))
            self.assertEqual("ctl:%s:%s" % (row["controlSet"], normal), row["regKey"])
            self.assertTrue(row["control"]["subject"].startswith("the normal event route " + cid))
            self.assertIn(row["id"], self.data["controls"])
            self.assertNotIn(row["id"], grouped)
        self.assertTrue(self.data["sweeps"][0]["controls"]["ok"])

    def test_a_contradicted_motion_control_reads_bad(self):
        tpp = _tpp()
        late = pp.CONTROL_MOTION_GHOST_LATE
        root = tempfile.mkdtemp(prefix="ppv-motionctl-")
        self.addCleanup(shutil.rmtree, root, True)
        d = write_motion_sweep(root, controls={late: tpp._motion_controls(late, perturbed=False)})
        rows = {c["caseId"]: c for c in ppv.build_site_data([_loaded(d)])["cases"]}
        for cid in (late, pp.motion_normal_id(late)):
            self.assertEqual(ppv.VERDICT_CONTROL_BAD, rows[cid]["verdict"], cid)
            self.assertNotEqual(pp.CONTROLS_OK, rows[cid]["control"]["judged"])
        self.assertEqual(ppv.VERDICT_CONTROL_OK, rows[pp.CONTROL_MOTION_GHOST_SLOW]["verdict"])

    def test_header_facets_and_thresholds(self):
        header = self.data["sweeps"][0]
        planned = [e["caseId"] for e in self.sweep["manifest"]["cases"]]  # partparity counts these
        counts = pp.motion_case_counts(pp.judge_case(self.sweep["sidecars"][cid]) for cid in planned)
        self.assertEqual(6, counts["cases"])
        counts.pop("thresholds")
        self.assertEqual(counts, header["motion"])
        self.assertEqual([], header["instrumentProblems"])
        self.assertTrue(header["completeness"]["ok"])
        self.assertEqual(pp.motion_thresholds(), self.data["motionThresholds"])
        self.assertIn(pp.FLAG_MOTION_SNAP, dict(self.data["facets"]["motionFlag"]))
        self.assertIn("robotics", dict(self.data["facets"]["family"]))
        snap = [g for g in self.data["groups"] if self.rows[MO_SNAP]["id"] in g["cases"]][0]
        self.assertIn(pp.FLAG_MOTION_SNAP, snap["motionFlags"])

    def test_regression_reads_motion_moves(self):
        root = tempfile.mkdtemp(prefix="ppv-motionreg-")
        self.addCleanup(shutil.rmtree, root, True)
        base = _loaded(write_motion_sweep(os.path.join(root, "base"), motion_cases(late=False)))
        data = ppv.build_site_data([self.sweep], [base])
        row = [c for c in data["cases"] if c["caseId"] == MO_LATE][0]
        self.assertEqual("moved", row["regression"]["status"])
        self.assertIn("motion.verdict pass -> fail", row["regression"]["changes"])
        self.assertTrue(any(c.startswith("motion.lagS 0.000 -> ") for c in row["regression"]["changes"]))
        same = [c for c in data["cases"] if c["caseId"] == MO_ANT][0]
        self.assertEqual("same", same["regression"]["status"])
        cur = dict(self.rows[MO_ANT]["motion"])
        self.assertEqual([], ppv.motion_changes(cur, cur))
        nudged = dict(cur, measures=dict(cur["measures"], lagS=0.1))
        self.assertEqual([], ppv.motion_changes(nudged, cur))  # under half the lag line
        moved = dict(cur, measures=dict(cur["measures"], lagS=0.3), flags=["motion-snap"])
        self.assertEqual(["motion.lagS 0.000 -> 0.300", "motion.flags +motion-snap"], ppv.motion_changes(moved, cur))
        self.assertEqual(["motion present -> absent"], ppv.motion_changes(None, cur))


# The prelude with a setTimeout that records instead of runs, then the motion boxes dumped and
# one target case driven: the header button, the scrubber, Play / Pause, the clock and 0.25x.
_PRELUDE = PAGE_RUNNER[:PAGE_RUNNER.index("const own =")]
MOTION_RUNNER = _PRELUDE.replace(
    "const byId = {};",
    "const byId = {}, T = [];\n"
    "const ST = (f, d) => { T.push({f: f, d: d, cleared: false, done: false}); return T.length; }, "
    "CT = i => { if (T[i - 1]) T[i - 1].cleared = true; };", 1).replace(
    "() => 0, () => 0, () => 0);", "ST, CT, () => 0);", 1) + r"""
const pending = () => T.filter(t => !t.cleared && !t.done).map(t => t.d);
const fire = () => { const t = T.filter(t => !t.cleared && !t.done)[0]; t.done = true; t.f(); };
const up = (n, test) => { for (let x = n; x; x = x.parentNode) if (test(x)) return x; return null; };
const fOf = n => { const x = up(n, y => Object.prototype.hasOwnProperty.call(y.style, '--f')); return x ? x.style['--f'] : null; };
const svgAll = b => b.all().filter(n => n.getAttribute('class') !== null);
const snap = b => {
  const tile = b.cls('tile')[0], svg = b.all().filter(n => n.tagName === 'SVG')[0];
  const byCls = c => svgAll(b).filter(n => n.getAttribute('class') === c);
  const marks = {}; svgAll(b).filter(n => n.hasAttribute('data-mark')).forEach(n => { marks[n.getAttribute('data-mark')] = +n.getAttribute('cx'); });
  const lag = byCls('lag')[0], fl = byCls('fline')[0];
  return {id: b.getAttribute('data-case'), text: b.textContent, tile: tile ? tile.getAttribute('data-case') : null, f: b.style['--f'],
    ownF: [b].concat(b.all()).filter(n => Object.prototype.hasOwnProperty.call(n.style, '--f')).length,
    layers: b.cls('layer').map(l => ({style: l.getAttribute('style'), row: l.getAttribute('data-row'), src: l.getAttribute('data-src'), f: fOf(l), blend: l._c.has('blend'),
      cap: up(l, x => x.tagName === 'FIGURE').cls('full')[0].textContent, view: up(l, x => x._c.has('tile')).getAttribute('data-view')})),
    scrub: (s => s ? {value: s.value, min: s.getAttribute('min'), max: s.getAttribute('max'), type: s.getAttribute('type')} : null)(b.cls('mscrub')[0]),
    label: b.cls('mframe').map(n => n.textContent).join(''), btn: b.cls('mplay').length ? [b.cls('mplay')[0].textContent, b.cls('mplay')[0].getAttribute('aria-pressed')] : null,
    slow: b.cls('mslow').length ? [b.cls('mslow')[0].textContent, b.cls('mslow')[0].getAttribute('aria-pressed')] : null,
    metas: b.cls('mmeta').map(n => n.textContent), head: b.children[0].textContent,
    links: b.all().filter(n => n.tagName === 'A').map(a => a.getAttribute('href')),
    chart: svg ? {viewBox: svg.getAttribute('viewBox'), lines: byCls('pl real').concat(byCls('pl ghost')).map(n => n.getAttribute('points')), marks: marks,
      lag: lag ? [+lag.getAttribute('x1'), +lag.getAttribute('x2'), +lag.getAttribute('y1')] : null, press: +byCls('press')[0].getAttribute('x1'),
      events: byCls('ev').map(n => +n.getAttribute('x1')), fline: fl ? [+fl.getAttribute('x1'), +fl.getAttribute('x2')] : null} : null};
};
const boxes = () => body.all().filter(n => n._c.has('mobox'));
const out = {boxes: boxes().map(snap), initialTimers: pending(), steps: []};
const target = boxes().filter(b => snap(b).id === req.target)[0];
const scrub = target.cls('mscrub')[0], btn = target.cls('mplay')[0], slow = target.cls('mslow')[0];
const rec = what => out.steps.push({what: what, box: snap(target), timers: pending()});
const head = byId.stripbtn;
out.headBtn = head ? head.textContent : null;
if (head) { head.fire('click'); out.afterHead = {text: head.textContent, timers: pending(), btns: boxes().map(b => b.cls('mplay').map(x => x.textContent)[0] || null)}; }
scrub.value = '5'; scrub.fire('input'); rec('scrub 5');
btn.fire('click'); rec('play');
fire(); rec('tick');
slow.fire('click'); rec('slow');
scrub.value = String(+scrub.getAttribute('max')); scrub.fire('input'); rec('scrub last');
btn.fire('click'); rec('play at last');
fire(); rec('wrap');
btn.fire('click'); rec('pause');
scrub.value = '3'; scrub.fire('input');
const zt = target.cls('tile')[0]; docL.click.forEach(f => f({target: zt}));
const zb = document.getElementById('zoombody');
out.zoom = {open: !!byId.zoom.open, f: zb.all().filter(n => n._c.has('pair')).map(n => n.style['--f']), layers: zb.cls('layer').map(l => ({f: fOf(l), style: l.getAttribute('style'), row: l.getAttribute('data-row')})), text: zb.textContent};
scrub.value = '6'; scrub.fire('input');
out.zoomAfter = zb.all().filter(n => n._c.has('pair')).map(n => n.style['--f']);
out.tables = target.parentNode.all().filter(n => n.tagName === 'TABLE').map(t => t.all().filter(n => n.tagName === 'TR').map(r => r.children.map(c => c.textContent)));
out.kv = target.parentNode.cls('kv').map(k => k.children.map(c => c.textContent)).pop() || [];
out.caseHead = target.parentNode.children[0].children.map(c => c.textContent);
const shown = () => body.cls('group').filter(g => !g.hidden).map(g => g.cls('name')[0].textContent).sort();
const sel = byId.top.all().filter(n => n.tagName === 'SELECT' && n.getAttribute('aria-label') === 'Motion flags')[0];
if (sel) { out.flagValues = sel.children.map(o => o.getAttribute('value')); sel.value = 'motion-snap'; sel.fire('change'); out.snapShown = shown(); sel.value = ''; sel.fire('change'); }
out.allShown = shown();
out.motionlegend = byId.motionlegend ? !byId.motionlegend.hidden : null;
out.statelegend = byId.statelegend ? !byId.statelegend.hidden : null;
out.sweepRows = byId.sweepbody.cls('kv').map(k => k.children.map(c => c.textContent));
fs.writeFileSync(req.out, JSON.stringify(out));
"""


def crop_at(style):
    """(x percent per --f, y percent) of a motion crop's background-position."""
    m = re.search(r"background-position:calc\(var\(--f\) \* ([\d.e-]+)%\) ([\d.e-]+)%$", style)
    return float(m.group(1)), float(m.group(2))


@unittest.skipUnless(shutil.which("node"), "node is not on PATH")
class MotionPageTests(unittest.TestCase):
    """The page's own script over the synthetic motion sweep: lock step, the scrubber, the
    clock and the chart, read back from the built nodes."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="ppv-motionpage-")
        cls.addClassCleanup(shutil.rmtree, cls.tmp, True)
        site = os.path.join(cls.tmp, "site")
        with contextlib.redirect_stdout(io.StringIO()):
            assert 0 == ppv.main([write_motion_sweep(cls.tmp), "--out", site, "--gif"])
        cls.js = os.path.join(site, ppv.DATA_JS_FILE)
        with open(cls.js, encoding="ascii") as fh:
            cls.data = ppv.parse_data_js(fh.read())
        cls.rows = {c["caseId"]: c for c in cls.data["cases"]}
        cls.page = cls.drive(MO_LATE)

    @classmethod
    def drive(cls, target, reduce=False):
        return _run_runner(cls.tmp, MOTION_RUNNER, cls.js, reduce, {"target": cls.rows[target]["id"]})

    def box(self, page, case_id):
        return [b for b in page["boxes"] if b["id"] == self.rows[case_id]["id"]][0]

    def assert_lock_step(self, box, m, k):
        """Every crop of the box reads the box's one --f, and at frame k it shows column k of
        the atlas row its caption names."""
        self.assertEqual(str(k), box["f"])
        self.assertEqual(1, box["ownF"])  # the box alone carries the frame index
        cols, rows = len(m["atlasCols"]), len(m["atlasRows"])
        want = {"REAL": "real", "GHOST": "ghost", "DIFFERENCE": "real"}
        for l in box["layers"]:
            self.assertEqual(str(k), l["f"])
            x, y = crop_at(l["style"])
            self.assertAlmostEqual(k, k * x / (100.0 / (cols - 1)))
            self.assertAlmostEqual(100.0 / (cols - 1), x)
            row = m["atlasRows"][int(round(y / (100.0 / (rows - 1))))]
            self.assertEqual(l["row"], row)
            self.assertIn("background-size:%d%% %d%%" % (cols * 100, rows * 100), l["style"])
            view = l["view"].split(":", 1)[1]
            kind = ("ghost" if l["blend"] else
                    "ghost-matched" if "same progress" in l["cap"] else want[l["cap"].split()[0]])
            self.assertEqual("%s:%s" % (kind, view), row, l["cap"])
            self.assertEqual(m["atlas"], l["src"])

    def test_every_motion_case_builds_one_box_in_lock_step(self):
        motion = [c for c in self.data["cases"] if c.get("motion")]
        self.assertEqual(sorted(c["id"] for c in motion), sorted(b["id"] for b in self.page["boxes"]))
        for c in motion:
            box, m = self.box(self.page, c["caseId"]), c["motion"]
            if c["caseId"] == MO_LATE:
                continue  # driven by the runner
            if not m["frames"]:  # the faulted press: a box that says so, no crop, no clock
                self.assertEqual(([], None, None), (box["layers"], box["btn"], box["scrub"]))
                self.assertIn("No motion frame in the sidecar.", box["text"])
                continue
            self.assert_lock_step(box, m, 0)
            per_view = 4 if m["matched"] else 3
            self.assertEqual(len(m["views"]) * (per_view + 1), len(box["layers"]))
            self.assertEqual({"type": "range", "min": "0", "max": str(len(m["frames"]) - 1), "value": "0"}, box["scrub"])
            self.assertEqual(len(m["views"]), len(box["metas"]))

    def test_the_scrubber_moves_every_row_and_the_frame_line(self):
        m = self.rows[MO_LATE]["motion"]
        steps = {s["what"]: s for s in self.page["steps"]}
        for what, k in (("scrub 5", 5), ("tick", 6), ("scrub last", len(m["frames"]) - 1), ("wrap", 0)):
            box = steps[what]["box"]
            self.assert_lock_step(box, m, k)
            self.assertEqual(str(k), box["scrub"]["value"])
            self.assertEqual([m["chart"]["frameX"][k]] * 2, box["chart"]["fline"])
            self.assertTrue(box["label"].startswith("frame %d / %d: " % (k + 1, len(m["frames"]))), box["label"])
            f = m["frames"][k]
            self.assertIn("progress real %.3f, ghost %.3f" % (f["realProgress"], f["ghostProgress"]), box["label"])
            same = f["views"][0]["same"]
            self.assertIn("same time %s shape blob %d px" % (same["verdict"], max(same["realOnlyBlobPx"], same["ghostOnlyBlobPx"])),
                          box["metas"][0])

    def test_play_runs_at_the_real_offsets_and_slow_and_pause(self):
        m = self.rows[MO_LATE]["motion"]
        motion_boxes = [c for c in self.data["cases"] if c.get("motion") and len(c["motion"]["frames"]) > 1]
        self.assertEqual(len(motion_boxes), len(self.page["initialTimers"]))  # every clock starts playing
        self.assertEqual("Pause strips", self.page["headBtn"])
        self.assertEqual({"text": "Play strips", "timers": [], "btns": ["Play"] * len(motion_boxes)},
                         dict(self.page["afterHead"], btns=[x for x in self.page["afterHead"]["btns"] if x]))  # the header button pauses every clock
        steps = {s["what"]: s for s in self.page["steps"]}
        self.assertEqual([], steps["scrub 5"]["timers"])
        self.assertEqual(["Pause", "true"], steps["play"]["box"]["btn"])
        self.assertEqual([m["delaysMs"][5]], steps["play"]["timers"])
        self.assertEqual([m["delaysMs"][6]], steps["tick"]["timers"])
        self.assertEqual(["0.25x", "true"], steps["slow"]["box"]["slow"])
        self.assertEqual([m["delaysMs"][6] / m["slowSpeed"]], steps["slow"]["timers"])
        self.assertEqual(["Play", "false"], steps["scrub last"]["box"]["btn"])  # a scrub pauses
        self.assertEqual([ppv.MOTION_LAST_FRAME_HOLD_MS / m["slowSpeed"]], steps["play at last"]["timers"])
        self.assertEqual("0", steps["wrap"]["box"]["f"])  # the held last frame loops to the press
        self.assertEqual(([], ["Play", "false"]), (steps["pause"]["timers"], steps["pause"]["box"]["btn"]))

    def test_reduced_motion_starts_paused(self):
        page = self.drive(MO_ANT, reduce=True)
        self.assertEqual([], page["initialTimers"])
        self.assertEqual({"Play"}, {b["btn"][0] for b in page["boxes"] if b["btn"]})
        self.assertEqual("Play strips", page["headBtn"])

    def test_the_chart_is_the_rows_geometry(self):
        m = self.rows[MO_LATE]["motion"]
        c = self.box(self.page, MO_LATE)["chart"]
        g = m["chart"]
        self.assertEqual("0 0 %d %d" % (g["w"], g["h"]), c["viewBox"])
        self.assertEqual([g["real"], g["ghost"]], c["lines"])
        self.assertEqual({"%s:%s" % (s, lv): g["marks"][s][lv] for s in ("real", "ghost") for lv in ppv.MOTION_CHART_LEVELS}, c["marks"])
        self.assertEqual(g["lag"] + [g["levelY"]["t50"]], c["lag"])
        self.assertEqual(g["press"], c["press"])
        self.assertEqual(g["events"], c["events"])

    def test_the_zoom_follows_the_clock(self):
        z = self.page["zoom"]
        self.assertTrue(z["open"])
        self.assertEqual(["3"], z["f"])
        self.assertEqual({"3"}, {l["f"] for l in z["layers"]})
        self.assertEqual(["6"], self.page["zoomAfter"])
        self.assertIn("frame 4 / ", z["text"])

    def test_chips_animation_link_details_and_filters(self):
        m = self.rows[MO_LATE]["motion"]
        head = " ".join(self.page["caseHead"])
        self.assertIn("lag %.2f s" % m["measures"]["lagS"], head)
        self.assertIn("duration ratio %.2f" % m["measures"]["durationRatio"], head)
        box = self.box(self.page, MO_LATE)
        self.assertEqual([self.rows[MO_LATE]["strips"]["animation"]], box["links"])
        self.assertTrue(box["links"][0].endswith(MO_LATE + ".motion.apng"))
        self.assertIn("fail", box["head"])
        frame_table = [t for t in self.page["tables"] if t[0][0] == "frame"][0]
        self.assertEqual(len(m["frames"]) + 1, len(frame_table))
        self.assertEqual("side-a same time", frame_table[0][9])
        self.assertTrue(any(t[0][0] == "transform" for t in self.page["tables"]))
        kv = dict(zip(self.page["kv"][0::2], self.page["kv"][1::2]))
        self.assertIn("press UT 100.000", kv["motion press"])
        self.assertEqual("+0.020 s after the press", kv["motion events"])
        self.assertEqual(["", "motion-snap"], self.page["flagValues"])
        self.assertEqual(["mk2CargoBayS"], self.page["snapShown"])
        self.assertIn("mk2CargoBayS", self.page["allShown"])
        self.assertTrue(self.page["motionlegend"])
        self.assertIn(self.page["statelegend"], (None, False))  # no state case: never shown
        sweep_kv = dict(zip(self.page["sweepRows"][0][0::2], self.page["sweepRows"][0][1::2]))
        self.assertTrue(sweep_kv["motion"].startswith("%d motion case(s): " % self.data["sweeps"][0]["motion"]["cases"]))

    def test_the_motion_script_reads_only_keys_the_core_writes(self):
        page = ppv.INDEX_HTML
        sec = page[page.index("// ---- motion (design Appendix H.5) ----"):page.index("// One case: a stowed skin block")]
        m = self.rows[MO_LATE]["motion"]
        js = {"length", "slice", "map", "forEach", "filter", "concat", "join", "push", "indexOf"}
        for var, keys in (("m", set(m)), ("C", set(m["chart"])), ("f", set(m["frames"][0]))):
            used = set(re.findall(r"\b%s\.([A-Za-z][A-Za-z0-9]*)" % var, sec))
            self.assertEqual(set(), used - keys - js, "the motion script reads %s.<key> the core never writes" % var)


def _png_chunks(blob):
    assert blob[:8] == b"\x89PNG\r\n\x1a\n"
    i, out = 8, []
    while i < len(blob):
        n = struct.unpack(">I", blob[i:i + 4])[0]
        kind, body = blob[i + 4:i + 8], blob[i + 8:i + 8 + n]
        crc = struct.unpack(">I", blob[i + 8 + n:i + 12 + n])[0]
        assert crc == zlib.crc32(kind + body) & 0xFFFFFFFF, kind
        out.append((kind, body))
        i += 12 + n
    return out


def decode_apng(blob, tmp):
    """(acTL, [(fcTL fields, pixels)]): each frame rebuilt as a still PNG from its IDAT / fdAT
    data and decoded by gui_mirror.read_png, the viewer's own reader."""
    import gui_mirror
    chunks = _png_chunks(blob)
    ihdr = chunks[0][1]
    actl = struct.unpack(">II", [b for k, b in chunks if k == b"acTL"][0])
    frames, seqs, cur = [], [], None
    for kind, body in chunks:
        if kind == b"fcTL":
            f = struct.unpack(">IIIIIHHBB", body)
            seqs.append(f[0])
            cur = [f, b""]
            frames.append(cur)
        elif kind == b"IDAT":
            cur[1] += body
        elif kind == b"fdAT":
            seqs.append(struct.unpack(">I", body[:4])[0])
            cur[1] += body[4:]
    assert seqs == list(range(len(seqs))), seqs
    out = []
    for f, data in frames:
        still = (b"\x89PNG\r\n\x1a\n" + ppv._png_chunk(b"IHDR", struct.pack(">II", f[1], f[2]) + ihdr[8:])
                 + ppv._png_chunk(b"IDAT", data) + ppv._png_chunk(b"IEND", b""))
        path = os.path.join(tmp, "frame.png")
        with open(path, "wb") as fh:
            fh.write(still)
        out.append((f, gui_mirror.read_png(path)))
    return actl, out


def lzw_decode(data, mcs):
    clear, eoi = 1 << mcs, (1 << mcs) + 1
    size, pos, out = mcs + 1, 0, []
    table, prev = None, None

    def reset():
        return {i: [i] for i in range(clear)}

    table = reset()
    nxt = eoi + 1
    bits = int.from_bytes(data, "little")
    total = len(data) * 8
    while pos + size <= total:
        code = (bits >> pos) & ((1 << size) - 1)
        pos += size
        if code == clear:
            table, nxt, size, prev = reset(), eoi + 1, mcs + 1, None
            continue
        if code == eoi:
            break
        if prev is None:
            entry = table[code]
        else:
            entry = table[code] if code in table else table[prev] + [table[prev][0]]
            if nxt < 4096:
                table[nxt] = table[prev] + [entry[0]]
                nxt += 1
                if nxt == (1 << size) and size < 12:
                    size += 1
        out.extend(entry)
        prev = code
    return out


def decode_gif(blob):
    assert blob[:6] == b"GIF89a"
    w, h, packed, _bg, _aspect = struct.unpack("<HHBBB", blob[6:13])
    assert packed & 0x80
    n = 1 << ((packed & 7) + 1)
    palette = [tuple(blob[13 + 3 * i:16 + 3 * i]) for i in range(n)]
    i, frames, loop, gce = 13 + 3 * n, [], None, None
    while blob[i] != 0x3B:
        if blob[i] == 0x21:
            label, i = blob[i + 1], i + 2
            sub = []
            while blob[i]:
                sub.append(blob[i + 1:i + 1 + blob[i]])
                i += 1 + blob[i]
            i += 1
            if label == 0xF9:
                gce = struct.unpack("<BHB", sub[0][:4])
            elif label == 0xFF and sub[0] == b"NETSCAPE2.0":
                loop = struct.unpack("<H", sub[1][1:3])[0]
        elif blob[i] == 0x2C:
            x, y, fw, fh, fp = struct.unpack("<HHHHB", blob[i + 1:i + 10])
            assert (x, y, fw, fh, fp) == (0, 0, w, h, 0)
            mcs, i = blob[i + 10], i + 11
            data = b""
            while blob[i]:
                data += blob[i + 1:i + 1 + blob[i]]
                i += 1 + blob[i]
            i += 1
            frames.append((gce, lzw_decode(data, mcs)))
        else:
            raise AssertionError("block 0x%02x at %d" % (blob[i], i))
    return w, h, palette, loop, frames


def atlas_tile(atlas, tile, row, col):
    w, _h, bpp, px = atlas
    return b"".join(px[((row * tile + y) * w + col * tile) * bpp:((row * tile + y) * w + (col + 1) * tile) * bpp]
                    for y in range(tile))


def frame_tile(w, bpp, px, tile, j):
    return b"".join(px[(y * w + j * tile) * bpp:(y * w + (j + 1) * tile) * bpp] for y in range(tile))


class MotionAnimationTests(unittest.TestCase):
    """--gif: the APNG and GIF decode back to the motion atlas tiles."""

    @classmethod
    def setUpClass(cls):
        import gui_mirror
        cls.gm = gui_mirror
        cls.tmp = tempfile.mkdtemp(prefix="ppv-gif-")
        cls.addClassCleanup(shutil.rmtree, cls.tmp, True)
        cls.sweep = write_motion_sweep(cls.tmp)
        cls.sites = {}
        for fmt in ppv.ANIMATION_FORMATS:
            out = os.path.join(cls.tmp, "site-" + fmt)
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                assert 0 == ppv.main([cls.sweep, "--out", out, "--gif", "--gif-format", fmt])
            with open(os.path.join(out, ppv.DATA_JS_FILE), encoding="ascii") as fh:
                cls.sites[fmt] = (out, ppv.parse_data_js(fh.read()), buf.getvalue())

    def animated(self, fmt):
        out, data, _ = self.sites[fmt]
        rows = [c for c in data["cases"] if c.get("motion") and c["motion"]["atlasFile"]]
        self.assertEqual(10, len(rows))  # every motion row but the faulted press
        for c in rows:
            yield out, c, self.gm.read_png(os.path.join(self.sweep, c["motion"]["atlasFile"]))

    def expected_rows(self, m):
        view = m["animationView"]
        return [m["atlasRows"].index("%s:%s" % (k, view)) for k in ("real", "ghost", "ghost-matched")
                if "%s:%s" % (k, view) in m["atlasRows"]]

    def test_the_apng_decodes_to_the_atlas_tiles(self):
        for out, c, atlas in self.animated(ppv.ANIMATION_APNG):
            m = c["motion"]
            self.assertEqual(("img/0/" + c["caseId"] + ".motion.apng", "apng"),
                             (c["strips"]["animation"], c["strips"]["animationFormat"]))
            with open(os.path.join(out, *c["strips"]["animation"].split("/")), "rb") as fh:
                blob = fh.read()
            actl, frames = decode_apng(blob, self.tmp)
            rows, tile = self.expected_rows(m), m["tile"]
            self.assertEqual((len(m["atlasCols"]), 0), actl)
            self.assertEqual(len(m["atlasCols"]), len(frames))
            for i, (f, (w, h, bpp, px)) in enumerate(frames):
                self.assertEqual((w, h, bpp), (len(rows) * tile, tile, atlas[2]))
                self.assertEqual((f[3], f[4], f[7], f[8]), (0, 0, 0, 0))  # whole canvas, replaced
                self.assertEqual(m["delaysMs"][i], f[5] * 1000 // f[6])
                for j, r in enumerate(rows):
                    self.assertEqual(atlas_tile(atlas, tile, r, i), frame_tile(w, bpp, px, tile, j), (c["caseId"], i, j))

    def test_the_gif_decodes_to_the_atlas_tiles_over_the_tile_grey(self):
        for out, c, atlas in self.animated(ppv.ANIMATION_GIF):
            m = c["motion"]
            with open(os.path.join(out, *c["strips"]["animation"].split("/")), "rb") as fh:
                w, h, palette, loop, frames = decode_gif(fh.read())
            rows, tile, bpp = self.expected_rows(m), m["tile"], atlas[2]
            self.assertEqual((w, h, loop, len(frames)), (len(rows) * tile, tile, 0, len(m["atlasCols"])))
            bg = ppv.GIF_BACKGROUND
            for i, (gce, idx) in enumerate(frames):
                self.assertEqual(max(2, round(m["delaysMs"][i] / 10.0)), gce[1])
                self.assertEqual(w * h, len(idx))
                got = b"".join(bytes(palette[x]) for x in idx)
                for j, r in enumerate(rows):
                    src = atlas_tile(atlas, tile, r, i)
                    want = b"".join(bytes((ch * src[p + 3] + b * (255 - src[p + 3]) + 127) // 255
                                          for ch, b in zip(src[p:p + 3], bg)) for p in range(0, len(src), bpp))
                    self.assertEqual(want, frame_tile(w, 3, got, tile, j), (c["caseId"], i, j))

    def test_the_cli_reports_and_links(self):
        _, data, text = self.sites[ppv.ANIMATION_GIF]
        self.assertIn("wrote 10 gif animation(s)", text)
        legacy = [c for c in data["cases"] if not c.get("motion")]
        self.assertTrue(legacy)
        self.assertEqual({None}, {c["strips"] for c in legacy})

    def test_gif_on_a_sweep_with_no_motion_case(self):
        tmp = tempfile.mkdtemp(prefix="ppv-gif-legacy-")
        self.addCleanup(shutil.rmtree, tmp, True)
        out = os.path.join(tmp, "site")
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            self.assertEqual(0, ppv.main([fx_sweep_copy(tmp), "--out", out, "--gif"]))
        self.assertIn("no motion case in these sweeps", buf.getvalue())
        found = [n for _r, _d, files in os.walk(out) for n in files if ".motion." in n]
        self.assertEqual([], found)

    def test_an_unreadable_atlas_is_a_problem_not_a_stop(self):
        tmp = tempfile.mkdtemp(prefix="ppv-gif-bad-")
        self.addCleanup(shutil.rmtree, tmp, True)
        d = write_motion_sweep(tmp)
        with open(os.path.join(d, MO_ANT + pp.MOTION_ATLAS_SUFFIX), "wb") as fh:
            fh.write(b"not a png")
        out, err = os.path.join(tmp, "site"), io.StringIO()
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
            self.assertEqual(0, ppv.main([d, "--out", out, "--gif"]))
        self.assertIn(MO_ANT, err.getvalue())
        with open(os.path.join(out, ppv.DATA_JS_FILE), encoding="ascii") as fh:
            rows = {c["caseId"]: c for c in ppv.parse_data_js(fh.read())["cases"]}
        self.assertIsNone(rows[MO_ANT]["strips"]["animation"])
        self.assertIsNotNone(rows[MO_LATE]["strips"]["animation"])

    def test_a_layout_mismatch_raises(self):
        atlas = (24, 24, 4, b"\x00" * 24 * 24 * 4)
        with self.assertRaises(ValueError):
            ppv.motion_animation_frames(atlas, 12, ["real:side-a", "ghost:side-a", "x"], [0, 1], "side-a")
        with self.assertRaises(ValueError):
            ppv.motion_animation_frames(atlas, 12, ["real:side-b", "ghost:side-b"], [0, 1], "side-a")
        w, h, bpp, frames = ppv.motion_animation_frames(atlas, 12, ["real:side-a", "ghost:side-a"], [0, 1], "side-a")
        self.assertEqual((24, 12, 4, 2), (w, h, bpp, len(frames)))

    def test_partial_alpha_and_many_colours(self):
        import random
        rnd = random.Random(7)
        w = h = 40
        frames = [bytes(rnd.randrange(256) for _ in range(w * h * 4)) for _ in range(2)]
        actl, got = decode_apng(ppv.encode_apng(w, h, 4, frames, [30, 1000]), self.tmp)
        self.assertEqual(frames, [px for _f, (_w, _h, _b, px) in got])
        w2, h2, palette, loop, decoded = decode_gif(ppv.encode_gif(w, h, 4, frames, [30, 1000]))
        bg = ppv.GIF_BACKGROUND
        flat = [tuple((ch * f[i + 3] + b * (255 - f[i + 3]) + 127) // 255 for ch, b in zip(f[i:i + 3], bg))
                for f in frames for i in range(0, len(f), 4)]  # straight alpha over the tile grey, rounded
        counts = {}
        for p in flat:
            counts[p] = counts.get(p, 0) + 1
        pal, lut = ppv.median_cut(counts)
        self.assertEqual(256, len(pal))
        self.assertEqual(pal, palette[:256])
        got_px = [palette[x] for _g, idx in decoded for x in idx]
        self.assertEqual([pal[lut[p]] for p in flat], got_px)
        err = sum(abs(a - b) for p, q in zip(flat, got_px) for a, b in zip(p, q)) / (3.0 * len(flat))
        self.assertLess(err, 16)
        self.assertEqual([3, 100], [g[1] for g, _i in decoded])

    def test_lzw_round_trips_through_a_full_table(self):
        import random
        rnd = random.Random(3)
        for mcs, data in ((8, bytes(rnd.randrange(256) for _ in range(30000))), (2, bytes([0, 1, 2, 3] * 900)),
                          (2, b""), (3, bytes([5]))):
            self.assertEqual(list(data), lzw_decode(ppv.lzw_encode(data, mcs), mcs), mcs)

    def test_few_colours_keep_their_exact_palette(self):
        counts = {(1, 2, 3): 5, (4, 5, 6): 1}
        self.assertEqual(([(1, 2, 3), (4, 5, 6)], {(1, 2, 3): 0, (4, 5, 6): 1}), ppv.median_cut(counts))



def _frames(sc):
    return sc["motion"]["frames"]


# Each malformed motion sidecar a writer could leave (the clean review's repros): the mutation
# and a fragment of the problem the case's row must name.
MALFORMED_MOTION = {
    "transforms-int": (lambda sc: sc["motion"].__setitem__("transforms", 5), "motion.transforms: not an array"),
    "events-int": (lambda sc: sc.setdefault("stateProof", {}).__setitem__("events", 5), "stateProof.events: not an array"),
    "events-str": (lambda sc: sc.setdefault("stateProof", {}).__setitem__("events", "ab"), "stateProof.events: not an array"),
    "scout-ints": (lambda sc: sc["motion"].__setitem__("scout", {"s": 5, "progress": 5}), "motion.scout"),
    "transform-real-int": (lambda sc: sc["motion"]["transforms"][0].__setitem__("real", 5), "motion.transforms[0]"),
    "realS-str": (lambda sc: [f.__setitem__("realS", "x") for f in _frames(sc)], "timing:real"),
    "targetS-str": (lambda sc: _frames(sc)[0].__setitem__("targetS", "x"), "motion.frames[0]"),
    "views-int": (lambda sc: _frames(sc)[0].__setitem__("views", 5), "motion.frames[0].views: not an array"),
    "flags-int": (lambda sc: _frames(sc)[0].__setitem__("flags", 5), "motion.frames[0].flags: not an array"),
    "frames-str": (lambda sc: sc["motion"].__setitem__("frames", "abc"), "motion.frames: not an array"),
    "pressUT-str": (lambda sc: sc["motion"].__setitem__("pressUT", "abc"), "motion.pressUT: not a number"),
    "imgProgress-list": (lambda sc: _frames(sc)[0].__setitem__("imgProgress", [1]), "motion.frames[0].imgProgress: not an object"),
    "atlas-list": (lambda sc: sc.__setitem__("motionAtlas", [1]), "motionAtlas: not an object"),
    "atlas-rows-int": (lambda sc: sc.__setitem__("motionAtlas", {"file": "x.png", "rows": 5, "cols": 5, "tile": "a"}),
                       "motionAtlas.rows: not an array"),
}


def _number_or_null(test, value, where):
    test.assertTrue(value is None or (isinstance(value, (int, float)) and not isinstance(value, bool)),
                    "%s: %r" % (where, value))


def malformed_motion_row():
    """MO_ANT's row over a sidecar whose transforms are not an array."""
    tmp = tempfile.mkdtemp(prefix="ppv-malformed-row-")
    try:
        s = _loaded(write_motion_sweep(tmp))
        sc = copy.deepcopy(s["sidecars"][MO_ANT])
        sc["motion"]["transforms"] = 5
        s["sidecars"] = dict(s["sidecars"], **{MO_ANT: sc})
        return [c for c in ppv.build_site_data([s], None, LEGACY_STAMP)["cases"] if c["caseId"] == MO_ANT][0]
    finally:
        shutil.rmtree(tmp, True)


class MalformedMotionTests(unittest.TestCase):
    """A malformed motion sidecar is a problem on its own case's row, never a stop of the
    site build, and partparity's judge reads it without raising."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="ppv-malformed-")
        cls.addClassCleanup(shutil.rmtree, cls.tmp, True)
        cls.dir = write_motion_sweep(cls.tmp)
        cls.sweep = _loaded(cls.dir)

    def rows_with(self, sc):
        s = dict(self.sweep, sidecars=dict(self.sweep["sidecars"], **{MO_ANT: sc}))
        return {c["caseId"]: c for c in ppv.build_site_data([s], None, LEGACY_STAMP)["cases"]}

    def assert_page_numbers(self, m, name):
        for i, f in enumerate(m.get("frames") or ()):
            for key in ("targetS", "realS", "matchedS", "ut", "realProgress", "ghostProgress", "matchedProgress"):
                _number_or_null(self, f.get(key), "%s frames[%d].%s" % (name, i, key))

    def test_the_good_sweep_names_no_problem(self):
        rows = self.rows_with(self.sweep["sidecars"][MO_ANT])
        self.assertEqual([], [cid for cid, c in rows.items() if "motionProblem" in c])

    def test_each_malformed_sidecar_is_a_problem_on_its_row(self):
        for name, (mutate, fragment) in MALFORMED_MOTION.items():
            with self.subTest(name):
                sc = copy.deepcopy(self.sweep["sidecars"][MO_ANT])
                mutate(sc)
                pp.judge_case(sc)  # reads it, never raises
                rows = self.rows_with(sc)
                self.assertIn(fragment, rows[MO_ANT].get("motionProblem", ""))
                self.assertTrue(rows[MO_ANT]["motionProblem"].startswith("the motion sidecar is malformed: "))
                self.assertNotIn("motionProblem", rows[MO_LATE])
                self.assertIsNotNone(rows[MO_LATE].get("motion"))
                if rows[MO_ANT].get("motion"):
                    self.assert_page_numbers(rows[MO_ANT]["motion"], name)

    def test_a_motion_build_that_raises_is_a_problem_on_its_row(self):
        with mock.patch.object(ppv, "motion_row", side_effect=RuntimeError("boom")):
            rows = self.rows_with(self.sweep["sidecars"][MO_ANT])
        self.assertEqual("the motion block would not build: RuntimeError: boom", rows[MO_ANT]["motionProblem"])
        self.assertNotIn("motion", rows[MO_ANT])
        self.assertIn("verdict", rows[MO_ANT])

    def test_partparity_reads_non_array_views_and_flags_as_empty(self):
        for key in ("views", "flags"):
            sc = copy.deepcopy(self.sweep["sidecars"][MO_ANT])
            _frames(sc)[0][key] = 5
            self.assertIn("motion.frames[0].%s: not an array" % key, pp.motion_block_problems(sc))
            self.assertIsNotNone(pp.judge_case(sc).motion)
            self.assertEqual(list(pp.motion_flags(sc)), list(pp.motion_flags(sc)))  # reads, never raises
        sc = copy.deepcopy(self.sweep["sidecars"][MO_ANT])
        _frames(sc)[0]["views"] = 5
        self.assertEqual([], pp.motion_frame_views(_frames(sc)[0], "same"))

    def test_the_site_builds_and_the_page_runs_over_malformed_cases(self):
        tmp = tempfile.mkdtemp(prefix="ppv-malformed-site-")
        self.addCleanup(shutil.rmtree, tmp, True)
        d = write_motion_sweep(tmp)
        bad = [cid for cid, sc in sorted(_loaded(d)["sidecars"].items())
               if pp.motion_block(sc) and cid not in (MO_LATE, MO_FAULT)]
        names = list(MALFORMED_MOTION)
        self.assertTrue(len(bad) >= 4)
        for i, cid in enumerate(bad):
            path = os.path.join(d, cid + pp.SIDECAR_SUFFIX)
            with open(path, encoding="utf-8") as fh:
                sc = json.load(fh)
            MALFORMED_MOTION[names[i % len(names)]][0](sc)
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(sc, fh)
        out = os.path.join(tmp, "site")
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(0, ppv.main([d, "--out", out, "--gif"]))
        js = os.path.join(out, ppv.DATA_JS_FILE)
        with open(js, encoding="ascii") as fh:
            rows = {c["caseId"]: c for c in ppv.parse_data_js(fh.read())["cases"]}
        for cid in bad:
            self.assertIn("motionProblem", rows[cid], cid)
        self.assertNotIn("motionProblem", rows[MO_LATE])
        if shutil.which("node"):
            page = _run_runner(tmp, MOTION_RUNNER, js, False, {"target": rows[MO_LATE]["id"]})
            texts = {b["id"]: b["text"] for b in page["boxes"]}
            self.assertIn(rows[MO_LATE]["id"], texts)


class UnreadableAtlasTests(unittest.TestCase):
    """--gif over a truncated or lying atlas reports the case and carries on."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="ppv-gif-trunc-")
        cls.addClassCleanup(shutil.rmtree, cls.tmp, True)
        cls.dir = write_motion_sweep(cls.tmp)
        cls.atlas = os.path.join(cls.dir, MO_ANT + pp.MOTION_ATLAS_SUFFIX)
        with open(cls.atlas, "rb") as fh:
            cls.orig = fh.read()

    def tearDown(self):
        with open(self.atlas, "wb") as fh:
            fh.write(self.orig)

    def built(self, blob, name):
        with open(self.atlas, "wb") as fh:
            fh.write(blob)
        out, err = os.path.join(self.tmp, "site-" + name), io.StringIO()
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(err):
            self.assertEqual(0, ppv.main([self.dir, "--out", out, "--gif"]), name)
        with open(os.path.join(out, ppv.DATA_JS_FILE), encoding="ascii") as fh:
            rows = {c["caseId"]: c for c in ppv.parse_data_js(fh.read())["cases"]}
        self.assertIn(MO_ANT, err.getvalue(), name)
        self.assertIsNone(rows[MO_ANT]["strips"]["animation"], name)
        self.assertIsNotNone(rows[MO_LATE]["strips"]["animation"], name)

    def test_a_truncated_atlas_is_a_problem_not_a_stop(self):
        # 20: a truncated IHDR (struct.error); 60 and len - 12: a short IDAT
        for cut in (8, 12, 20, 33, 40, 60, 200):
            self.built(self.orig[:cut], "cut%d" % cut)

    def test_an_atlas_claiming_a_taller_image_is_a_problem_not_a_stop(self):
        n = struct.unpack(">I", self.orig[8:12])[0]
        body = bytearray(self.orig[16:16 + n])
        body[4:8] = struct.pack(">I", struct.unpack(">I", bytes(body[4:8]))[0] * 3)
        self.built(self.orig[:8] + ppv._png_chunk(b"IHDR", bytes(body)) + self.orig[20 + n:], "tall")


if __name__ == "__main__":
    unittest.main()
