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
        self.assertEqual("skin not modelled: the ghost applies no B9 subtype (prefab look)",
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
        # the B9 subtype warns skin-not-modelled:b9 (the builder applies no B9 subtype)
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
        self.assertEqual(2, ppv.main([self.sweep, "--gif"]))
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


class PageContractTests(unittest.TestCase):
    """The owner's page rules, as far as text can show them."""

    def test_offline_ascii_and_identity_captions(self):
        page = ppv.INDEX_HTML
        page.encode("ascii")
        self.assertNotRegex(page, r"https?://|//cdn")
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
        case_keys = set(data["cases"][0])
        group_keys = set(data["groups"][0])
        used_c = set(re.findall(r"\bc\.([A-Za-z]+)", ppv.INDEX_HTML))
        used_g = set(re.findall(r"\bg\.([A-Za-z]+)", ppv.INDEX_HTML))
        js_props = {"length", "id", "_i", "_text", "_built", "_group", "cases"}
        self.assertEqual(set(), used_c - case_keys - js_props, "page reads c.<key> the core never writes")
        self.assertEqual(set(), used_g - group_keys - js_props, "page reads g.<key> the core never writes")


if __name__ == "__main__":
    unittest.main()
