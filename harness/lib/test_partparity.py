"""Unit tests for the part ghost parity verifier (``harness/lib/partparity.py``) and its
hlib decisions (the harvest plan, retention, the archive file set, the verdict classes).

Design: docs/dev/design-part-ghost-parity.md sections 5, 6 and 10.

Fixtures. ``testdata/partparity/pp1/`` is COPIED from the real green P2 flight
(``automation/stock-minimal/parsek-partparity/pp1/``, run ``2026-11-21_1739``, automation
DLL sha256 prefix a46e8133): the manifest and ten of its sidecars verbatim, and five atlases.
``ctl-empty.png`` is the C# encoder's own file. The four others
(``ctl-ghost-missing-mesh``, ``ctl-ghost-scale90``, ``ctl-ghost-rot90``,
``decoupler.2--basic--6fabf1e2``) keep the alpha channel byte for byte and zero RGB,
re-encoded with gui_mirror.write_png so they cost ~25 KB instead of ~1 MB: the shape
measures read alpha only, so the recompute must still match the C# numbers exactly.
Synthetic sweeps come from ``_partparity_fixture.py``, whose measures are a
pixel-by-pixel transcription of ``PartParityMetrics.Measure``.

Run: ``cd harness && python -m unittest discover -s lib -q``.
"""

import copy
import json
import os
import random
import re
import shutil
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "..", "tools"))

import _partparity_fixture as fx  # noqa: E402
import gui_mirror  # noqa: E402
import hlib  # noqa: E402
import partparity as pp  # noqa: E402

PP1 = os.path.join(HERE, "testdata", "partparity", "pp1")
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
CS_DIR = os.path.join(REPO_ROOT, "Source", "Parsek", "TestCommands")
RUN_ID = "2026-11-21_1739_PP-1-part-parity-stock"


def _load(name):
    with open(os.path.join(PP1, name), encoding="utf-8") as fh:
        return json.load(fh)


def _fixture_sweep():
    names = sorted(os.listdir(PP1))
    sidecars = {n[:-len(pp.SIDECAR_SUFFIX)]: _load(n) for n in names
                if n.endswith(pp.SIDECAR_SUFFIX)}
    atlases = tuple(n for n in names if n.endswith(pp.ATLAS_SUFFIX)
                    and not n.endswith(pp.THUMB_SUFFIX))
    return pp.SweepInput("pp1", True, _load("manifest.json"), None, sidecars, atlases)


def _read_dir(label_dir, label="pp1"):
    names = sorted(os.listdir(label_dir))
    sidecars = {}
    for n in names:
        if n.endswith(pp.SIDECAR_SUFFIX):
            with open(os.path.join(label_dir, n), encoding="utf-8") as fh:
                sidecars[n[:-len(pp.SIDECAR_SUFFIX)]] = json.load(fh)
    with open(os.path.join(label_dir, pp.MANIFEST_FILE), encoding="utf-8") as fh:
        manifest = json.load(fh)
    atlases = tuple(n for n in names if n.endswith(pp.ATLAS_SUFFIX)
                    and not n.endswith(pp.THUMB_SUFFIX))
    return pp.SweepInput(label, True, manifest, None, sidecars, atlases)


class RecomputeMatchesTheCSharpSidecarsTests(unittest.TestCase):
    """The instrument check against the C# instrument itself: the Python recompute of
    the real flight's atlases reproduces every shape number the C# sidecar wrote."""

    ATLASES = ("ctl-empty", "ctl-ghost-missing-mesh", "ctl-ghost-scale90",
               "ctl-ghost-rot90", "decoupler.2--basic--6fabf1e2")

    def test_every_committed_atlas_recomputes_to_its_sidecar(self):
        for cid in self.ATLASES:
            with self.subTest(case=cid):
                atlas = gui_mirror.read_png(os.path.join(PP1, cid + ".png"))
                self.assertEqual([], pp.recompute_case(_load(cid + pp.SIDECAR_SUFFIX), atlas))

    def test_the_fixture_is_not_vacuous(self):
        # The missing-mesh control's side-a real-only blob is the 306 px cube; scale90
        # has thousands of mismatch pixels; rot90 has both sides; the C# empty control
        # has zero coverage. A recompute that read nothing would not match these.
        mm = _load("ctl-ghost-missing-mesh.part.json")["views"][0]
        self.assertEqual(306, mm["realOnlyBlobPx"])
        self.assertGreater(_load("ctl-ghost-scale90.part.json")["views"][0]["realOnlyTolPx"], 10000)
        rot = _load("ctl-ghost-rot90.part.json")["views"][0]
        self.assertGreater(rot["ghostOnlyBlobPx"], 0)
        self.assertGreater(rot["realOnlyBlobPx"], 0)
        self.assertEqual(0, _load("ctl-empty.part.json")["views"][0]["orPx"])

    def test_one_flipped_alpha_byte_is_a_mismatch(self):
        # Negative control: turn one covered real pixel of the cube's side-a tile off.
        w, h, bpp, px = gui_mirror.read_png(os.path.join(PP1, "ctl-ghost-missing-mesh.png"))
        side = _load("ctl-ghost-missing-mesh.part.json")
        bbox = side["views"][0]["bbox"]["real"]
        x = bbox[0] + bbox[2] - 2          # inside the cube, at the +X edge of the box
        y = bbox[1] + bbox[3] // 2
        buf = bytearray(px)
        buf[(y * w + x) * 4 + 3] = 0
        diffs = pp.recompute_case(side, (w, h, bpp, bytes(buf)))
        self.assertTrue(any(d.startswith("side-a.realPx:") for d in diffs), diffs)

    def test_a_tampered_sidecar_number_is_a_mismatch(self):
        side = _load("ctl-ghost-scale90.part.json")
        atlas = gui_mirror.read_png(os.path.join(PP1, "ctl-ghost-scale90.png"))
        for key, delta in (("realOnlyBlobPx", 1), ("ghostOnlyTolPx", 1), ("andPx", -1)):
            with self.subTest(key=key):
                bad = copy.deepcopy(side)
                bad["views"][2][key] += delta
                diffs = pp.recompute_case(bad, atlas)
                self.assertEqual(1, len(diffs), diffs)
                self.assertTrue(diffs[0].startswith("top.%s:" % key), diffs)
        bad = copy.deepcopy(side)
        bad["views"][1]["centroid"]["real"][0] += 0.01
        self.assertTrue(any("centroid.real" in d for d in pp.recompute_case(bad, atlas)))
        bad = copy.deepcopy(side)
        bad["views"][0]["bbox"]["ghost"][2] += 1
        self.assertTrue(any("bbox.ghost" in d for d in pp.recompute_case(bad, atlas)))

    def test_an_atlas_whose_layout_disagrees_is_a_mismatch(self):
        side = _load("ctl-empty.part.json")
        atlas = gui_mirror.read_png(os.path.join(PP1, "ctl-empty.png"))
        bad = copy.deepcopy(side)
        bad["atlas"]["cols"] = bad["atlas"]["cols"][:3]
        self.assertTrue(pp.recompute_case(bad, atlas))
        bad = copy.deepcopy(side)
        bad["atlas"]["tile"] = 256
        self.assertTrue(pp.recompute_case(bad, atlas))


class RecomputeMatchesTheReferenceTranscriptionTests(unittest.TestCase):
    """The fast whole-row recompute against a pixel-by-pixel transcription of the C#
    definitions, over random masks (blobs, edges, borders, thin lines, empty sides)."""

    def test_random_masks(self):
        rnd = random.Random(20261121)
        for trial in range(60):
            tile = rnd.choice((5, 8, 13, 16))
            real = [[rnd.random() < 0.45 for _ in range(tile)] for _ in range(tile)]
            ghost = [[(c if rnd.random() < 0.8 else not c) for c in row] for row in real]
            if trial % 7 == 0:
                ghost = fx.empty_mask(tile)
            if trial % 11 == 0:
                real = fx.empty_mask(tile)
            ref = fx.reference_measure(real, ghost, tile * tile * 0.5)
            rows = lambda m: [int("".join("1" if b else "0" for b in r), 2) for r in m]  # noqa: E731
            got = pp.recompute_view(rows(real), rows(ghost), tile)
            for key in pp.RECOMPUTED_INT_KEYS + ("bbox", "touchesBorder"):
                self.assertEqual(ref[key], got[key], (trial, key))
            for side in ("real", "ghost"):
                if ref["centroid"][side] is None:
                    self.assertIsNone(got["centroid"][side])
                else:
                    self.assertAlmostEqual(ref["centroid"][side][0], got["centroid"][side][0], 12)
                    self.assertAlmostEqual(ref["centroid"][side][1], got["centroid"][side][1], 12)
            self.assertAlmostEqual(ref["iou"], got["iou"], 12)

    def test_blob_connectivity_is_four(self):
        # Two diagonal pixels are two blobs of 1, not one blob of 2.
        rows = [0b10, 0b01]
        self.assertEqual(1, pp.largest_blob(rows, 2))
        self.assertEqual(3, pp.largest_blob([0b110, 0b010], 3))
        # A U shape joins through its base.
        self.assertEqual(7, pp.largest_blob([0b101, 0b101, 0b111], 3))

    def test_the_tolerance_is_the_eight_neighbourhood(self):
        # A real-only pixel diagonally adjacent to a ghost pixel is not counted.
        real = [0b100, 0b000, 0b000]
        ghost = [0b000, 0b010, 0b000]
        got = pp.recompute_view(real, ghost, 3)
        self.assertEqual(1, got["realOnlyPx"])
        self.assertEqual(0, got["realOnlyTolPx"])
        far = pp.recompute_view([0b100, 0, 0], [0, 0, 0b001], 3)
        self.assertEqual(1, far["realOnlyTolPx"])

    def test_the_coverage_threshold_is_alpha_byte_128(self):
        # A 2 x 2 tile with alphas 127 | 128 over 255 | 0: bit (1 - x) is pixel x.
        px = bytes([0, 0, 0, 127, 0, 0, 0, 128, 0, 0, 0, 255, 0, 0, 0, 0])
        self.assertEqual([0b01, 0b10], pp.tile_alpha_rows(px, 2, 4, 0, 0, 2))
        with self.assertRaises(ValueError):
            pp.tile_alpha_rows(px[:12], 2, 3, 0, 0, 2)

    def test_a_synthetic_sweep_recomputes_clean_end_to_end(self):
        tmp = tempfile.mkdtemp(prefix="pp-synth-")
        try:
            fx.build_sweep(tmp, parts=4)
            sweep = _read_dir(tmp)
            for cid, side in sweep.sidecars.items():
                with self.subTest(case=cid):
                    atlas = gui_mirror.read_png(os.path.join(tmp, side["atlas"]["file"]))
                    self.assertEqual([], pp.recompute_case(side, atlas))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


class ViewAndCaseVerdictTests(unittest.TestCase):

    @staticmethod
    def _view(**kw):
        v = {"view": "side-a", "realOnlyBlobPx": 0, "ghostOnlyBlobPx": 0,
             "realOnlyBlobPct": 0.0, "ghostOnlyBlobPct": 0.0, "colorBlobPx": 0}
        v.update(kw)
        return v

    def test_the_fail_lines(self):
        j = pp.judge_view
        self.assertEqual(pp.VERDICT_PASS, j(self._view()).verdict)
        # "exceeds": the line itself is not over it.
        self.assertEqual(pp.VERDICT_WARN, j(self._view(realOnlyBlobPx=64)).verdict)
        self.assertEqual(pp.VERDICT_FAIL, j(self._view(realOnlyBlobPx=65)).verdict)
        self.assertEqual(pp.VERDICT_FAIL, j(self._view(ghostOnlyBlobPx=65)).verdict)
        self.assertEqual(pp.VERDICT_FAIL, j(self._view(ghostOnlyBlobPct=0.21)).verdict)
        self.assertEqual(pp.VERDICT_FAIL, j(self._view(colorBlobPx=65)).verdict)
        self.assertEqual(pp.VERDICT_WARN, j(self._view(colorBlobPx=64)).verdict)

    def test_the_warn_lines_are_a_quarter(self):
        j = pp.judge_view
        self.assertEqual(pp.VERDICT_PASS, j(self._view(realOnlyBlobPx=16)).verdict)
        self.assertEqual(pp.VERDICT_WARN, j(self._view(realOnlyBlobPx=17)).verdict)
        self.assertEqual(pp.VERDICT_PASS, j(self._view(realOnlyBlobPct=0.05)).verdict)
        self.assertEqual(pp.VERDICT_WARN, j(self._view(realOnlyBlobPct=0.051)).verdict)
        self.assertEqual(pp.VERDICT_PASS, j(self._view(colorBlobPx=16)).verdict)
        self.assertEqual(pp.VERDICT_WARN, j(self._view(colorBlobPx=17)).verdict)

    def test_an_unmeasured_percentage_is_not_judged(self):
        self.assertEqual(pp.VERDICT_PASS,
                         pp.judge_view(self._view(realOnlyBlobPct=None, ghostOnlyBlobPct=None)).verdict)

    def test_the_real_flight_part_cases_read_as_measured(self):
        # decoupler.2: colour blobs 40 / 18 px -> warn on side-a; clydesdale still
        # animating -> judged, not an error.
        dec = pp.judge_case(_load("decoupler.2--basic--6fabf1e2.part.json"))
        self.assertEqual(pp.VERDICT_WARN, dec.verdict)
        self.assertEqual("side-a", dec.worst_view)
        self.assertEqual(40, dec.max_color_blob_px)
        cly = pp.judge_case(_load("clydesdale--default--89327fba.part.json"))
        self.assertEqual(pp.SETUP_STILL_ANIMATING, cly.setup_status)
        self.assertEqual(pp.VERDICT_PASS, cly.verdict)
        # The missing-mesh control, judged as a part, fails on shape and structure.
        mm = pp.judge_case(_load("ctl-ghost-missing-mesh.part.json"))
        self.assertEqual(pp.VERDICT_FAIL, mm.verdict)
        self.assertIn("missing-rendered:PartParityPlantedCube", mm.reasons)
        self.assertEqual(318, mm.max_shape_blob_px)

    def test_structure_fails_only_on_rendered_entries(self):
        side = copy.deepcopy(_load("clydesdale--default--89327fba.part.json"))
        side["structure"]["missingOnGhost"] = [{"key": "a/b", "mesh": "m", "rendered": False}]
        self.assertEqual(pp.VERDICT_PASS, pp.judge_case(side).verdict)
        side["structure"]["extraOnGhost"] = [{"key": "plume", "mesh": "p", "rendered": True}]
        c = pp.judge_case(side)
        self.assertEqual(pp.VERDICT_FAIL, c.verdict)
        self.assertIn("extra-rendered:plume", c.reasons)

    def test_the_skin_proof(self):
        side = copy.deepcopy(_load("clydesdale--default--89327fba.part.json"))
        side["skin"] = {"module": "ModulePartVariants", "moduleId": None, "requested": "Dark",
                        "real": "Dark", "snapshot": "Dark", "ghost": "Light"}
        c = pp.judge_case(side)
        self.assertEqual(pp.VERDICT_FAIL, c.verdict)
        self.assertIn("skin-mismatch:real=Dark ghost=Light", c.reasons)
        side["skin"]["real"] = "Light"
        c = pp.judge_case(side)
        self.assertEqual(pp.VERDICT_ERROR, c.verdict)
        self.assertTrue(any(r.startswith("skin-not-applied:") for r in c.reasons))
        # A ghost name the build did not read is not compared (P2 sidecars: ghost=null).
        side["skin"] = {"module": "ModulePartVariants", "requested": "Basic", "real": "Basic",
                        "snapshot": "Basic", "ghost": None}
        self.assertEqual(pp.VERDICT_PASS, pp.judge_case(side).verdict)

    def test_a_setup_fault_is_an_error_and_no_views_is_an_error(self):
        side = copy.deepcopy(_load("clydesdale--default--89327fba.part.json"))
        for status in (pp.SETUP_CRASHED, pp.SETUP_SPAWN_TIMEOUT, pp.SETUP_DIED_SETTLING,
                       pp.SETUP_SKIN_NOT_APPLIED):
            side["setup"]["status"] = status
            self.assertEqual(pp.VERDICT_ERROR, pp.judge_case(side).verdict, status)
        side["setup"]["status"] = pp.SETUP_OK
        side["views"] = []
        c = pp.judge_case(side)
        self.assertEqual(pp.VERDICT_ERROR, c.verdict)
        self.assertEqual("no-views", c.reasons[0])


class ControlsAndCompletenessTests(unittest.TestCase):

    def test_the_real_manifest_controls_are_ok(self):
        c = pp.check_controls(_load("manifest.json"))
        self.assertTrue(c.ok, c.problems)
        self.assertEqual(len(pp.CONTROL_IDS), len(c.outcomes))

    def test_a_bad_absent_or_pending_control_fails(self):
        m = _load("manifest.json")
        bad = copy.deepcopy(m)
        bad["controls"][3]["outcome"] = "bad"
        self.assertIn("ctl-ghost-scale90:bad", pp.check_controls(bad).problems)
        absent = copy.deepcopy(m)
        del absent["controls"][7]
        self.assertIn("ctl-wrong-skin:absent", pp.check_controls(absent).problems)
        pending = copy.deepcopy(m)
        pending["progress"]["controls"] = "pending"
        self.assertFalse(pp.check_controls(pending).ok)
        off = copy.deepcopy(m)
        off["controls"] = []
        off["progress"]["controls"] = "off"
        self.assertFalse(pp.check_controls(off).ok)

    def test_the_trimmed_fixture_is_incomplete_by_its_missing_sidecars(self):
        sweep = _fixture_sweep()
        k = pp.check_completeness(sweep.manifest, sweep.sidecars)
        self.assertFalse(k.ok)
        # 20 planned parts + 8 controls; the fixture keeps 2 part sidecars + 8 controls.
        self.assertEqual(28, k.planned)
        self.assertEqual(10, k.complete)
        self.assertIn("18 case(s) without a complete sidecar", k.problems[0])

    def test_a_complete_synthetic_sweep_and_each_way_it_can_be_incomplete(self):
        tmp = tempfile.mkdtemp(prefix="pp-complete-")
        try:
            fx.build_sweep(tmp, parts=3)
            sweep = _read_dir(tmp)
            self.assertTrue(pp.check_completeness(sweep.manifest, sweep.sidecars).ok)
            # progress.complete false
            m = copy.deepcopy(sweep.manifest)
            m["progress"]["complete"] = False
            self.assertIn("progress.complete=False",
                          pp.check_completeness(m, sweep.sidecars).problems)
            # a crashed case is a recorded setup fault: complete
            side = copy.deepcopy(sweep.sidecars)
            side["part1--default--00000001"]["setup"]["status"] = pp.SETUP_CRASHED
            side["part1--default--00000001"]["views"] = []
            k = pp.check_completeness(sweep.manifest, side)
            self.assertTrue(k.ok)
            self.assertEqual(1, k.setup_faults)
            # an unreadable sidecar, a wrong schema, an ok case with no views
            for mutate, needle in (
                    (lambda s: s.__setitem__("part0--default--00000000", None), "no-sidecar"),
                    (lambda s: s["part0--default--00000000"].__setitem__("schema", "x"), "schema=x"),
                    (lambda s: s["part0--default--00000000"].__setitem__("views", []), "no-views"),
                    (lambda s: s.pop("ctl-empty"), "ctl-empty:no-sidecar")):
                side = copy.deepcopy(sweep.sidecars)
                mutate(side)
                k = pp.check_completeness(sweep.manifest, side)
                self.assertFalse(k.ok)
                self.assertIn(needle, " ".join(k.problems))
            # a case still owed a solo run, a stale inflight
            k = pp.check_completeness(sweep.manifest, sweep.sidecars,
                                      isolate_lines=["part2--default--00000002"])
            self.assertIn("isolate.txt owes 1", k.problems[0])
            k = pp.check_completeness(sweep.manifest, sweep.sidecars,
                                      inflight_lines=["part2--default--00000002", "#solo"])
            self.assertIn("inflight.txt left behind: part2--default--00000002", k.problems[0])
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


class SampleTests(unittest.TestCase):

    def test_the_sample_is_seeded_by_the_run_id_and_sized_two_percent(self):
        ids = ["c%04d" % i for i in range(950)]
        a = pp.select_recompute_sample(ids, RUN_ID)
        self.assertEqual(19, len(a))  # ceil(0.02 * 950)
        self.assertEqual(a, pp.select_recompute_sample(list(reversed(ids)), RUN_ID))
        self.assertNotEqual(a, pp.select_recompute_sample(ids, RUN_ID + "_a2"))
        self.assertEqual(sorted(a), a)
        self.assertEqual(1, len(pp.select_recompute_sample(ids[:3], RUN_ID)))
        self.assertEqual([], pp.select_recompute_sample([], RUN_ID))


class EvaluateSweepTests(unittest.TestCase):

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="pp-eval-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _eval(self, gating=False, read_png=gui_mirror.read_png, **kw):
        fx.build_sweep(self.tmp, **kw)
        sweep = _read_dir(self.tmp)
        return pp.evaluate_sweep(sweep, RUN_ID, gating, read_png,
                                 lambda n: os.path.join(self.tmp, n)), sweep

    def test_a_clean_sweep_reports_and_recomputes_every_control(self):
        res, _ = self._eval(parts=5)
        self.assertEqual(pp.STATUS_REPORT, res.status)
        self.assertIsNone(res.invalid_subkind)
        self.assertEqual(5, res.counts["cases"])
        self.assertEqual(5, res.counts[pp.VERDICT_PASS])
        self.assertEqual(list(pp.CONTROL_IDS) + pp.select_recompute_sample(
            ["part%d--default--0000000%d" % (i, i) for i in range(5)], RUN_ID), res.recompute_ids)
        self.assertEqual([], res.recompute_mismatches)
        doc = pp.verdicts_document(res, RUN_ID)
        self.assertEqual(pp.VERDICTS_SCHEMA, doc["schema"])
        self.assertEqual(5, len(doc["cases"]))
        json.dumps(doc)

    def test_gating_turns_the_part_verdicts_into_pass_or_fail(self):
        res, _ = self._eval(gating=True, parts=2)
        self.assertEqual(pp.STATUS_PASS, res.status)
        shutil.rmtree(self.tmp)
        # The ghost shifted 3 px on a 6 px square: an 18 px blob in a 72 px box area
        # (25 %), far over 0.2 %.
        res, _ = self._eval(gating=True, parts=2, part_ghost_shift=3)
        self.assertEqual(pp.STATUS_FAIL, res.status)
        self.assertEqual(2, res.counts[pp.VERDICT_FAIL])
        shutil.rmtree(self.tmp)
        res, _ = self._eval(gating=False, parts=2, part_ghost_shift=3)
        self.assertEqual(pp.STATUS_REPORT, res.status, "report-only never fails")

    def test_a_bad_control_is_invalid_controls_ahead_of_everything(self):
        res, _ = self._eval(control_outcome="bad", complete=False)
        self.assertEqual(pp.STATUS_INVALID, res.status)
        self.assertEqual(pp.INVALID_CONTROLS_SUBKIND, res.invalid_subkind)

    def test_an_incomplete_sweep_is_invalid_incomplete(self):
        res, _ = self._eval(complete=False)
        self.assertEqual(pp.INVALID_INCOMPLETE_SUBKIND, res.invalid_subkind)

    def test_a_recompute_mismatch_is_invalid_instrument(self):
        fx.build_sweep(self.tmp)
        path = os.path.join(self.tmp, "ctl-ghost-rot90" + pp.SIDECAR_SUFFIX)
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
        doc["views"][0]["ghostOnlyTolPx"] += 1
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(doc, fh)
        res = pp.evaluate_sweep(_read_dir(self.tmp), RUN_ID, False, gui_mirror.read_png,
                                lambda n: os.path.join(self.tmp, n))
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        self.assertTrue(res.recompute_mismatches[0].startswith("ctl-ghost-rot90 side-a.ghostOnlyTolPx"))

    def test_an_unreadable_atlas_and_no_reader_are_instrument_faults(self):
        fx.build_sweep(self.tmp)
        with open(os.path.join(self.tmp, "ctl-empty.png"), "wb") as fh:
            fh.write(b"not a png")
        res = pp.evaluate_sweep(_read_dir(self.tmp), RUN_ID, False, gui_mirror.read_png,
                                lambda n: os.path.join(self.tmp, n))
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        self.assertIn("atlas unreadable", res.recompute_mismatches[0])
        res = pp.evaluate_sweep(_read_dir(self.tmp), RUN_ID, False, None, lambda n: n)
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)

    def test_an_absent_dir_or_manifest_is_incomplete(self):
        res = pp.evaluate_sweep(pp.SweepInput("pp1", False), RUN_ID, False, None, lambda n: n)
        self.assertEqual(pp.INVALID_INCOMPLETE_SUBKIND, res.invalid_subkind)
        res = pp.evaluate_sweep(pp.SweepInput("pp1", True, None, "missing manifest.json"),
                                RUN_ID, False, None, lambda n: n)
        self.assertEqual(pp.INVALID_INCOMPLETE_SUBKIND, res.invalid_subkind)
        self.assertIn("missing manifest.json", res.problems[0])

    def test_the_real_trimmed_fixture(self):
        # Controls ok, the recompute over the five committed atlases clean (the
        # control sidecars without an atlas here are not sampled), and incomplete
        # because 18 of the 20 part sidecars were not committed.
        res = pp.evaluate_sweep(_fixture_sweep(), RUN_ID, False, gui_mirror.read_png,
                                lambda n: os.path.join(PP1, n))
        self.assertTrue(res.controls.ok)
        self.assertEqual([], res.recompute_mismatches)
        self.assertEqual(["ctl-empty", "ctl-ghost-scale90", "ctl-ghost-rot90",
                          "ctl-ghost-missing-mesh", "decoupler.2--basic--6fabf1e2"],
                         res.recompute_ids)
        self.assertEqual(pp.INVALID_INCOMPLETE_SUBKIND, res.invalid_subkind)

    def test_combine_orders_the_invalid_subkinds(self):
        mk = lambda st, sub: pp.SweepResult("x", st, sub, False, [], None, None, [], [], [],  # noqa: E731
                                            pp._count([]))
        row = pp.combine([mk(pp.STATUS_INVALID, pp.INVALID_INCOMPLETE_SUBKIND),
                          mk(pp.STATUS_INVALID, pp.INVALID_CONTROLS_SUBKIND)], False)
        self.assertEqual(pp.INVALID_CONTROLS_SUBKIND, row.invalid_subkind)
        row = pp.combine([mk(pp.STATUS_REPORT, None)], False)
        self.assertEqual(pp.STATUS_REPORT, row.status)
        row = pp.combine([mk(pp.STATUS_PASS, None), mk(pp.STATUS_FAIL, None)], True)
        self.assertEqual(pp.STATUS_FAIL, row.status)

    def test_the_summary_line_names_the_counts_and_the_worst_cases(self):
        res, _ = self._eval(parts=3, part_ghost_shift=3)
        line = pp.format_summary(pp.combine([res], False))
        self.assertIn("status=REPORT gating=False subkind=-", line)
        self.assertIn("label=pp1 status=REPORT cases=3 pass=0 warn=0 fail=3 error=0", line)
        self.assertIn("controls=ok complete=true", line)
        self.assertIn("worst=[part0--default--00000000(fail:", line)
        rec = pp.detail_record(pp.combine([res], False))
        self.assertEqual(3, rec["sweeps"][0]["counts"]["fail"])


class SpecSurfaceTests(unittest.TestCase):

    def test_sweep_labels(self):
        spec = {"driver": {"steps": [
            {"cmd": "PartParitySweep", "args": {"label": "pp1", "op": "reset"}},
            {"cmd": "PartParitySweep", "args": {"label": "pp1", "op": "run"}},
            {"cmd": "PartParitySweep", "args": {"label": "b"}},
            {"cmd": "PartParitySweep", "args": {"label": "${x.y}"}},
            {"cmd": "PartParitySweep", "args": {"label": ".."}},
            {"cmd": "PartParitySweep", "args": {"label": "a/b"}},
            {"cmd": "CaptureScreenshot", "args": {"label": "c"}},
        ]}}
        self.assertEqual(["pp1", "b"], pp.sweep_labels(spec))
        self.assertEqual([], pp.sweep_labels({}))

    def test_the_block_validator(self):
        steps = [{"cmd": "PartParitySweep", "args": {"label": "pp1"}}]
        v = pp.validate_part_parity_expectations
        self.assertEqual([], v(None, steps))
        self.assertEqual([], v({"gating": False}, steps))
        self.assertEqual([], v({}, steps))
        self.assertTrue(any("must be a bool" in e for e in v({"gating": "yes"}, steps)))
        self.assertTrue(any("not a known key" in e for e in v({"thresholds": 1}, steps)))
        self.assertTrue(any("no PartParitySweep step" in e for e in v({"gating": False}, [])))
        self.assertTrue(any("must be a table" in e for e in v(1, steps)))
        self.assertTrue(pp.gating_armed({"partParity": {"gating": True}}))
        self.assertFalse(pp.gating_armed({"partParity": {"gating": False}}))
        self.assertFalse(pp.gating_armed({}))

    def test_validate_spec_runs_the_block_validator(self):
        import tomllib
        with open(os.path.join(HERE, "..", "coverage", "registry.toml"), "rb") as fh:
            reg = tomllib.load(fh)
        with open(os.path.join(HERE, "..", "scenarios", "PP-1-part-parity-stock.toml"), "rb") as fh:
            spec = tomllib.load(fh)
        self.assertEqual((), tuple(hlib.validate_spec(spec, reg).errors))
        spec["expectations"]["partParity"]["gating"] = "true"
        self.assertTrue(any("partParity.gating must be a bool" in e
                            for e in hlib.validate_spec(spec, reg).errors))
        del spec["expectations"]["partParity"]["gating"]
        spec["driver"]["steps"] = [s for s in spec["driver"]["steps"]
                                   if s["cmd"] != "PartParitySweep"]
        self.assertTrue(any("no PartParitySweep step" in e
                            for e in hlib.validate_spec(spec, reg).errors))


class HarvestRetentionArchiveDecisionTests(unittest.TestCase):
    """The pure hlib halves of the harvest leg (design 5.1)."""

    def test_the_harvest_plan(self):
        plan = hlib.plan_part_parity_harvest(["pp1", "pp2"], ["pp2", "other"])
        self.assertEqual([("pp1", "absent"), ("pp2", "move")],
                         [(e.label, e.action) for e in plan])
        self.assertEqual("R_partparity", hlib.partparity_results_dir_name("R"))
        self.assertEqual(pp.SWEEP_ROOT_DIR, hlib.PARTPARITY_INSTANCE_ROOT)

    def test_only_a_cross_volume_rename_falls_back_to_copy(self):
        self.assertTrue(hlib.partparity_move_needs_copy(18))
        self.assertTrue(hlib.partparity_move_needs_copy(None, 17))
        self.assertFalse(hlib.partparity_move_needs_copy(13))     # EACCES
        self.assertFalse(hlib.partparity_move_needs_copy(None, 5))  # ERROR_ACCESS_DENIED
        self.assertFalse(hlib.partparity_move_needs_copy(None, None))

    def test_retention_keeps_the_newest_and_the_current_run(self):
        gib = 1024 ** 3
        entries = [("r%d_partparity" % i, float(i), gib) for i in range(10)]
        entries.append(("r3_shots", 99.0, 1))  # never this pass's to prune
        prune = hlib.select_partparity_dirs_to_prune(entries, protect_name="r0_partparity")
        # Byte budget 8 GiB with the protected 1 GiB: newest 7 kept... but the count
        # budget (6) trips first: r9..r4 kept, r3..r1 pruned, r0 protected.
        self.assertEqual(["r1_partparity", "r2_partparity", "r3_partparity"], prune)
        self.assertNotIn("r3_shots", prune)
        big = [("a_partparity", 2.0, 9 * gib), ("b_partparity", 1.0, 1)]
        # Over the byte budget from the newest on: both go, oldest first.
        self.assertEqual(["b_partparity", "a_partparity"],
                         hlib.select_partparity_dirs_to_prune(big))

    def test_the_archive_takes_the_manifest_and_the_sidecars_only(self):
        rows = hlib.select_partparity_archive_files({
            "pp1": ["manifest.json", "a.part.json", "a.png", "a.thumb.png", "inflight.txt",
                    pp.VERDICTS_FILE, "b.part.json"],
            "../x": ["manifest.json"]})
        self.assertEqual([("pp1", "a.part.json"), ("pp1", "b.part.json"),
                          ("pp1", "manifest.json")], rows)
        plan = hlib.plan_results_archive("RID", ["RID.json"], False, None, rows)
        pp_entries = [e for e in plan.entries if e.dest_name.startswith("partparity/")]
        self.assertEqual(["partparity/pp1/a.part.json", "partparity/pp1/b.part.json",
                          "partparity/pp1/manifest.json"], [e.dest_name for e in pp_entries])
        self.assertEqual(("RID_partparity", "pp1", "manifest.json"), pp_entries[-1].source_rel)
        # The archive's own manifest.json stays the run's; the sweep's goes below.
        self.assertNotIn("manifest.json", [e.dest_name for e in plan.entries])
        # No sweep: the plan is the pre-P3 plan exactly.
        self.assertEqual(hlib.plan_results_archive("RID", ["RID.json"], False, None),
                         hlib.plan_results_archive("RID", ["RID.json"], False, None, ()))


class PartParityVerdictWiringTests(unittest.TestCase):
    """The row's verdict classes through hlib.classify_verdict."""

    def _classify(self, **verifiers):
        return hlib.classify_verdict({}, verifiers, {}, 1, "once")

    def test_the_constants_agree(self):
        self.assertEqual(hlib.PARTPARITY_INVALID_SUBKINDS, pp.INVALID_SUBKINDS)
        self.assertEqual(hlib.PARTPARITY_PARSEK_FAIL_SUBKIND, pp.PARSEK_FAIL_SUBKIND)
        self.assertIn(pp.PARSEK_FAIL_SUBKIND, hlib.PARSEK_FAIL_SUBKINDS)
        self.assertEqual(hlib.PARTPARITY_VERB, pp.SWEEP_VERB)

    def test_each_invalid_subkind_is_terminal_and_not_flake_exempt(self):
        for sub in pp.INVALID_SUBKINDS:
            with self.subTest(subkind=sub):
                v = self._classify(part_parity_invalid=True, part_parity_subkind=sub,
                                   part_parity_reason="x")
                self.assertEqual((hlib.VERDICT_INVALID, sub), (v.verdict, v.subkind))
                self.assertFalse(hlib.should_retry(v, 1, "once"))
                self.assertNotIn(sub, hlib.RETRYABLE_INVALID_SUBKINDS)
                self.assertNotIn(sub, hlib.FLAKE_EXEMPT_INVALID_SUBKINDS)

    def test_the_invalid_outranks_the_contract_verifiers_but_not_the_driver(self):
        v = self._classify(part_parity_invalid=True, part_parity_subkind="parity-controls",
                           log_validate_failed=True, expectation_mismatch=True)
        self.assertEqual("parity-controls", v.subkind)
        v = hlib.classify_verdict({"valid": False, "stage_subkind": "seam-timeout"},
                                  {"part_parity_invalid": True,
                                   "part_parity_subkind": "parity-controls"}, {}, 1, "none")
        self.assertEqual("seam-timeout", v.subkind)

    def test_an_armed_failing_part_is_parsek_fail_part_parity(self):
        v = self._classify(part_parity_mismatch=True)
        self.assertEqual((hlib.VERDICT_PARSEK_FAIL, "part-parity"), (v.verdict, v.subkind))
        v = self._classify(part_parity_mismatch=True, ghost_lifecycle_mismatch=True)
        self.assertEqual("ghost-lifecycle", v.subkind)
        self.assertEqual(hlib.VERDICT_PASS, self._classify(part_parity_mismatch=False).verdict)


def _strip_cs_comments(text):
    return "\n".join(re.sub(r"//.*$", "", line) for line in text.splitlines())


class PartParitySourceSyncTests(unittest.TestCase):
    """Reads OUTSIDE harness/: the C# key tables, schema ids, statuses, control ids,
    file names and the measuring constants the reader and the recompute depend on
    (Source/Parsek/TestCommands/PartParityJson.cs, TestCommandPartParitySweep.cs,
    PartParityMetrics.cs, comment-stripped), so a renamed C# key or a changed
    definition reds here rather than as a flight's INVALID(parity-instrument)."""

    @staticmethod
    def _source(name):
        path = os.path.join(CS_DIR, name)
        if not os.path.isfile(path):
            raise AssertionError("the C# part parity source moved; this mirror is vacuous: %s" % path)
        with open(path, encoding="utf-8-sig") as fh:
            return _strip_cs_comments(fh.read())

    @staticmethod
    def _string_array(text, name):
        m = re.search(r"\b%s\s*=\s*\{(.*?)\};" % re.escape(name), text, flags=re.S)
        if m is None:
            raise AssertionError("array %s not found" % name)
        return re.findall(r'"([^"]*)"', m.group(1))

    @staticmethod
    def _const(text, name):
        m = re.search(r"internal const \w+ %s = ([^;]+);" % re.escape(name), text)
        if m is None:
            raise AssertionError("const %s not found" % name)
        return m.group(1).strip().strip('"')

    def test_the_json_key_tables(self):
        text = self._source("PartParityJson.cs")
        for cs, mirror in (("CaseKeys", pp.CASE_KEYS), ("SkinKeys", pp.SKIN_KEYS),
                           ("AtlasKeys", pp.ATLAS_KEYS), ("ViewKeys", pp.VIEW_KEYS),
                           ("StructureKeys", pp.STRUCTURE_KEYS),
                           ("RendererRefKeys", pp.RENDERER_REF_KEYS),
                           ("SetupKeys", pp.SETUP_KEYS), ("ManifestKeys", pp.MANIFEST_KEYS),
                           ("ControlKeys", pp.CONTROL_KEYS),
                           ("CaseSummaryKeys", pp.CASE_SUMMARY_KEYS),
                           ("ProgressKeys", pp.PROGRESS_KEYS)):
            with self.subTest(table=cs):
                self.assertEqual(list(mirror), self._string_array(text, cs))
        self.assertEqual(pp.MANIFEST_SCHEMA, self._const(text, "ManifestSchemaId"))
        self.assertEqual(pp.CASE_SCHEMA, self._const(text, "CaseSchemaId"))

    def test_the_sweep_contract(self):
        text = self._source("TestCommandPartParitySweep.cs")
        self.assertEqual(pp.SWEEP_VERB, self._const(text, "VerbName"))
        self.assertEqual(pp.SWEEP_ROOT_DIR, self._const(text, "RootDirName"))
        self.assertEqual(pp.MANIFEST_FILE, self._const(text, "ManifestFileName"))
        self.assertEqual(pp.INFLIGHT_FILE, self._const(text, "InflightFileName"))
        self.assertEqual(pp.ISOLATE_FILE, self._const(text, "IsolateFileName"))
        self.assertEqual(pp.SIDECAR_SUFFIX, self._const(text, "SidecarSuffix"))
        self.assertEqual(pp.ATLAS_SUFFIX, self._const(text, "AtlasSuffix"))
        self.assertEqual(pp.THUMB_SUFFIX, self._const(text, "ThumbSuffix"))
        statuses = {self._const(text, n) for n in (
            "SetupOk", "SetupSkinNotApplied", "SetupSpawnTimeout", "SetupStillAnimating",
            "SetupDiedSettling", "SetupCrashed")}
        self.assertEqual(set(pp.SETUP_STATUSES), statuses)
        controls = [self._const(text, n) for n in (
            "ControlEmpty", "ControlGhostAbsent", "ControlRealVsReal", "ControlGhostScale90",
            "ControlGhostRot90", "ControlGhostFlip", "ControlGhostMissingMesh",
            "ControlWrongSkin")]
        self.assertEqual(list(pp.CONTROL_IDS), controls)
        m = re.search(r"ControlIds\s*=\s*\{(.*?)\};", text, flags=re.S)
        self.assertEqual(["ControlEmpty", "ControlGhostAbsent", "ControlRealVsReal",
                          "ControlGhostScale90", "ControlGhostRot90", "ControlGhostFlip",
                          "ControlGhostMissingMesh", "ControlWrongSkin"],
                         re.findall(r"\b(Control\w+)\b", m.group(1)))
        self.assertEqual((pp.CONTROLS_OK, pp.CONTROLS_BAD, pp.CONTROLS_PENDING, pp.CONTROLS_OFF),
                         tuple(self._const(text, n) for n in (
                             "ControlsOk", "ControlsBad", "ControlsPending", "ControlsOff")))
        # IsSetupFault: anything but ok and still-animating.
        body = text[text.index("internal static bool IsSetupFault("):]
        body = body[:body.index(";") + 1]
        self.assertIn("SetupOk", body)
        self.assertIn("SetupStillAnimating", body)

    def test_the_measuring_definitions(self):
        text = self._source("PartParityMetrics.cs")
        self.assertEqual(str(pp.COVERAGE_ALPHA_BYTE), self._const(text, "CoverageAlphaByte"))
        self.assertEqual("1", self._const(text, "TolerancePx"))
        self.assertEqual("4", self._const(text, "BlobConnectivity"))
        # The definitions the recompute transcribes, by their code shape.
        self.assertIn("mask[p] = rgba[p * 4 + 3] >= CoverageAlphaByte;", text)
        self.assertIn("if (!AnyInNeighbourhood(ghost, width, height, p))", text)
        self.assertIn("if (!AnyInNeighbourhood(real, width, height, p))", text)
        self.assertIn("m.Iou = m.OrPx > 0 ? (double)m.AndPx / m.OrPx : 1.0;", text)
        self.assertIn("sx += p % width + 0.5;", text)
        flood = text[text.index("internal static int LargestBlob("):text.index("private static void Push(")]
        self.assertEqual(4, flood.count("Push(mask, seen, stack, ref top,"))

    def test_the_atlas_rows_are_real_then_ghost(self):
        text = self._source("PartParityCaseCapture.cs")
        self.assertIn('Rows = new[] { "real", "ghost" }', text)
        self.assertIn("new[] { wc.RealTiles.ToArray(), wc.GhostTiles.ToArray() }", text)


if __name__ == "__main__":
    unittest.main()
