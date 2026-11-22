"""Unit tests for the part ghost parity verifier (``harness/lib/partparity.py``) and its
hlib decisions (the harvest plan, retention, the archive file set, the verdict classes).

Design: docs/dev/design-part-ghost-parity.md sections 5, 6 and 10.

Fixtures. ``testdata/partparity/pp1/`` is COPIED from the real green P2 flight
(``automation/stock-minimal/parsek-partparity/pp1/``, run ``2026-11-21_1739``, automation
DLL sha256 prefix a46e8133): the manifest and ten of its sidecars verbatim, and five atlases
(``ctl-empty``, ``ctl-ghost-missing-mesh``, ``ctl-ghost-scale90``, ``ctl-ghost-rot90``,
``decoupler.2--basic--6fabf1e2``) whose alpha channel is kept byte for byte with zero RGB.
No image file is committed (test_gui_mirror_fidelity.NoCommittedImagesTests), so each atlas
is stored as ``<caseId>.alpha.gz`` (its alpha plane, ``_partparity_fixture.read_alpha_fixture``)
and ``setUpModule`` rebuilds the label dir in a temp dir with real RGBA PNGs written by
gui_mirror.write_png; PP1 names that temp dir and PP1_COMMITTED the committed one. The shape
measures read alpha only, so the recompute must still match the C# numbers exactly.
Synthetic sweeps come from ``_partparity_fixture.py``, whose measures are a
pixel-by-pixel transcription of ``PartParityMetrics.Measure``.

Run: ``cd harness && python -m unittest discover -s lib -q``.
"""

import copy
import gzip
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

PP1_COMMITTED = os.path.join(HERE, "testdata", "partparity", "pp1")
# The materialized label dir (setUpModule): the committed files plus a PNG per atlas.
PP1 = None
_PP1_TMP = None
REPO_ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
CS_DIR = os.path.join(REPO_ROOT, "Source", "Parsek", "TestCommands")
RUN_ID = "2026-11-21_1739_PP-1-part-parity-stock"


def setUpModule():
    global PP1, _PP1_TMP
    _PP1_TMP = tempfile.mkdtemp(prefix="parsek-partparity-pp1-")
    PP1 = os.path.join(_PP1_TMP, "pp1")
    fx.materialize_alpha_fixtures(PP1_COMMITTED, PP1)


def tearDownModule():
    global PP1, _PP1_TMP
    if _PP1_TMP:
        shutil.rmtree(_PP1_TMP, ignore_errors=True)
    PP1 = _PP1_TMP = None


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


class AlphaFixtureRoundTripTests(unittest.TestCase):
    """The committed alpha fixtures rebuild to PNGs that read back to the same alpha
    plane through gui_mirror.read_png, with zero RGB, so the recompute below reads what
    the C# capture wrote."""

    def test_the_committed_atlases_are_exactly_the_alpha_fixtures(self):
        names = sorted(os.listdir(PP1_COMMITTED))
        alpha = sorted(n[:-len(fx.ALPHA_FIXTURE_SUFFIX)] for n in names
                       if n.endswith(fx.ALPHA_FIXTURE_SUFFIX))
        self.assertEqual(sorted(RecomputeMatchesTheCSharpSidecarsTests.ATLASES), alpha)
        rebuilt = sorted(n[:-len(pp.ATLAS_SUFFIX)] for n in os.listdir(PP1)
                         if n.endswith(pp.ATLAS_SUFFIX) and not n.endswith(pp.THUMB_SUFFIX))
        self.assertEqual(alpha, rebuilt)

    def test_every_rebuilt_png_round_trips_to_its_alpha_bytes(self):
        for cid in RecomputeMatchesTheCSharpSidecarsTests.ATLASES:
            with self.subTest(case=cid):
                w, h, alpha = fx.read_alpha_fixture(
                    os.path.join(PP1_COMMITTED, cid + fx.ALPHA_FIXTURE_SUFFIX))
                rw, rh, bpp, px = gui_mirror.read_png(os.path.join(PP1, cid + pp.ATLAS_SUFFIX))
                self.assertEqual((w, h, 4), (rw, rh, bpp))
                self.assertEqual(alpha, px[3::4])
                self.assertEqual(px, fx.alpha_to_rgba(w, h, alpha))
                side = _load(cid + pp.SIDECAR_SUFFIX)
                self.assertEqual(w, len(side["atlas"]["cols"]) * side["atlas"]["tile"])

    def test_a_corrupt_alpha_fixture_is_refused(self):
        tmp = tempfile.mkdtemp(prefix="parsek-partparity-alpha-")
        try:
            bad = os.path.join(tmp, "x" + fx.ALPHA_FIXTURE_SUFFIX)
            with open(bad, "wb") as fh:
                fh.write(gzip.compress(fx.ALPHA_FIXTURE_MAGIC + bytes([0, 0, 0, 2, 0, 0, 0, 2, 0])))
            with self.assertRaises(ValueError):
                fx.read_alpha_fixture(bad)
            with open(bad, "wb") as fh:
                fh.write(gzip.compress(b"XXXX" + bytes(12)))
            with self.assertRaises(ValueError):
                fx.read_alpha_fixture(bad)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


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
        self.assertEqual(pp.VERDICT_FAIL, j(self._view(colorBlobPx=257)).verdict)
        self.assertEqual(pp.VERDICT_WARN, j(self._view(colorBlobPx=256)).verdict)

    def test_the_warn_lines_are_a_quarter(self):
        j = pp.judge_view
        self.assertEqual(pp.VERDICT_PASS, j(self._view(realOnlyBlobPx=16)).verdict)
        self.assertEqual(pp.VERDICT_WARN, j(self._view(realOnlyBlobPx=17)).verdict)
        self.assertEqual(pp.VERDICT_PASS, j(self._view(realOnlyBlobPct=0.05)).verdict)
        self.assertEqual(pp.VERDICT_WARN, j(self._view(realOnlyBlobPct=0.051)).verdict)
        self.assertEqual(pp.VERDICT_PASS, j(self._view(colorBlobPx=64)).verdict)
        self.assertEqual(pp.VERDICT_WARN, j(self._view(colorBlobPx=65)).verdict)

    def test_an_unmeasured_percentage_is_not_judged(self):
        self.assertEqual(pp.VERDICT_PASS,
                         pp.judge_view(self._view(realOnlyBlobPct=None, ghostOnlyBlobPct=None)).verdict)

    def test_the_real_flight_part_cases_read_as_measured(self):
        # decoupler.2: colour blobs 40 / 18 px, under the 64 px warn line -> pass;
        # clydesdale still animating -> judged, not an error.
        dec = pp.judge_case(_load("decoupler.2--basic--6fabf1e2.part.json"))
        self.assertEqual(pp.VERDICT_PASS, dec.verdict)
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

    def test_a_context_dependent_part_caps_picture_fails_at_warn(self):
        side = copy.deepcopy(_load("clydesdale--default--89327fba.part.json"))
        side["views"][0]["colorBlobPx"] = 5000
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(side).verdict)
        side["tags"] = ["context-dependent"]
        c = pp.judge_case(side)
        self.assertEqual(pp.VERDICT_WARN, c.verdict)
        self.assertIn(pp.REASON_CONTEXT_POSE, c.reasons)
        side["structure"]["extraOnGhost"] = [{"key": "plume", "mesh": "p", "rendered": True}]
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(side).verdict,
                         "a structural defect still fails on a context-dependent part")

    def test_a_part_with_a_skipped_depth_mask_caps_picture_fails_at_warn(self):
        side = copy.deepcopy(_load("clydesdale--default--89327fba.part.json"))
        side["views"][0]["colorBlobPx"] = 5000
        side["structure"]["missingOnGhost"] = [
            {"key": "a/RCSMask", "mesh": "", "rendered": True, "nonVisual": "depth-mask"}]
        c = pp.judge_case(side)
        self.assertEqual(pp.VERDICT_WARN, c.verdict)
        self.assertIn(pp.REASON_DEPTH_MASK, c.reasons)
        side["structure"]["missingOnGhost"] = [
            {"key": "a/fx", "mesh": "", "rendered": True, "nonVisual": "fx"}]
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(side).verdict, "an effect mesh caps nothing")
        side["structure"]["missingOnGhost"] = [{"key": "a/b/RCSMask", "mesh": "", "rendered": True}]
        self.assertEqual(pp.VERDICT_WARN, pp.judge_case(side).verdict, "pre-key sidecar: by name")

    def test_a_non_visual_renderer_missing_on_the_ghost_is_not_a_missing_mesh(self):
        side = copy.deepcopy(_load("clydesdale--default--89327fba.part.json"))
        for entry in ({"key": "a/RCSMask", "mesh": "", "rendered": True, "nonVisual": "depth-mask"},
                      {"key": "a/fx", "mesh": "", "rendered": True, "nonVisual": "fx"},
                      # a sidecar from before the nonVisual key: the path decides
                      {"key": "a/RCSjet/Waterfall/FX/fx-cylinder/CylinderMesh", "mesh": "",
                       "rendered": True},
                      {"key": "a/b/solarshroud_mask", "mesh": "", "rendered": True}):
            side["structure"]["missingOnGhost"] = [entry]
            self.assertEqual(pp.VERDICT_PASS, pp.judge_case(side).verdict, entry["key"])
        side["structure"]["missingOnGhost"] = [
            {"key": "a/Mask", "mesh": "m", "rendered": True, "nonVisual": None}]
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(side).verdict,
                         "a nonVisual key that is null is a visible renderer, whatever its name")
        side["structure"]["missingOnGhost"] = [{"key": "model/Tank", "mesh": "m", "rendered": True}]
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(side).verdict)

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
        # PartParitySkinRules.GhostFlags: a variant part whose ghost applied no variant
        # differs (null on one side only); names match case-insensitively; no module, no
        # comparison; a B9 part's unread ghost is the known gap, a warn.
        side["skin"] = {"module": "ModulePartVariants", "requested": "Basic", "real": "Basic",
                        "snapshot": "Basic", "ghost": None}
        c = pp.judge_case(side)
        self.assertEqual(pp.VERDICT_FAIL, c.verdict)
        self.assertIn("skin-mismatch:real=Basic ghost=None", c.reasons)
        side["skin"]["ghost"] = "basic"
        self.assertEqual(pp.VERDICT_PASS, pp.judge_case(side).verdict)
        side["skin"] = {"module": None, "requested": None, "real": None, "snapshot": None,
                        "ghost": "Dark"}
        self.assertEqual(pp.VERDICT_PASS, pp.judge_case(side).verdict)
        side["skin"] = {"module": "ModuleB9PartSwitch", "moduleId": "meshSwitch",
                        "requested": "Short", "real": "Short", "snapshot": "Short", "ghost": None}
        c = pp.judge_case(side)
        self.assertEqual(pp.VERDICT_WARN, c.verdict)
        self.assertIn("skin-not-modelled:b9", c.reasons)

    def test_the_skin_flags_cross_check_the_csharp_flags(self):
        side = copy.deepcopy(_load("clydesdale--default--89327fba.part.json"))
        self.assertIsNone(pp.skin_flag_disagreement(side))
        side["skin"] = {"module": "ModulePartVariants", "requested": "Dark", "real": "Dark",
                        "snapshot": "Dark", "ghost": "Light"}
        self.assertEqual("skin flags: sidecar=- derived=skin-mismatch",
                         pp.skin_flag_disagreement(side))
        side["flags"] = list(side["flags"]) + ["skin-mismatch"]
        self.assertIsNone(pp.skin_flag_disagreement(side))
        side["views"] = []
        side["flags"] = []
        self.assertIsNone(pp.skin_flag_disagreement(side), "no views: no ghost was built")
        # a failed ghost build still captures views; the C# writes no skin flags for it
        side = copy.deepcopy(_load("clydesdale--default--89327fba.part.json"))
        side["skin"] = {"module": "ModulePartVariants", "requested": None, "real": "Dark",
                        "snapshot": "Dark", "ghost": None}
        side["flags"] = ["ghost-not-built"]
        self.assertIsNone(pp.skin_flag_disagreement(side))
        self.assertNotIn("skin-mismatch:real=Dark ghost=None", pp.judge_case(side).reasons)

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
                    (lambda s: s.pop("ctl-empty"), "ctl-empty:no-sidecar"),
                    (lambda s: s["part0--default--00000000"].__setitem__("atlas", None),
                     "part0--default--00000000:no-atlas")):
                side = copy.deepcopy(sweep.sidecars)
                mutate(side)
                k = pp.check_completeness(sweep.manifest, side, atlases=sweep.atlases)
                self.assertFalse(k.ok)
                self.assertIn(needle, " ".join(k.problems))
            # a case with views whose atlas file was not harvested
            self.assertTrue(pp.check_completeness(sweep.manifest, sweep.sidecars,
                                                  atlases=sweep.atlases).ok)
            gone = tuple(a for a in sweep.atlases if not a.startswith("part1--"))
            k = pp.check_completeness(sweep.manifest, sweep.sidecars, atlases=gone)
            self.assertFalse(k.ok)
            self.assertIn("part1--default--00000001:atlas-missing:part1--default--00000001",
                          " ".join(k.problems))
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

    def _damage_host(self, reason="parts", detail="pid=7 parts 13->9 at before wave 3"):
        path = os.path.join(self.tmp, pp.MANIFEST_FILE)
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
        doc["progress"].update({"complete": False, "hostDamaged": True,
                                "hostDamage": reason, "hostDamageDetail": detail})
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(doc, fh)

    def test_a_damaged_host_is_invalid_host_damaged_ahead_of_everything(self):
        # A stopped sweep also shows a pending control and an incomplete sweep: the
        # host damage is their cause, so it names the row; the symptoms stay listed.
        fx.build_sweep(self.tmp, control_outcome="pending", complete=False)
        self._damage_host()
        res = pp.evaluate_sweep(_read_dir(self.tmp), RUN_ID, False, gui_mirror.read_png,
                                lambda n: os.path.join(self.tmp, n))
        self.assertEqual((pp.STATUS_INVALID, pp.INVALID_HOST_DAMAGED_SUBKIND),
                         (res.status, res.invalid_subkind))
        self.assertEqual("host damaged: reason=parts detail=pid=7 parts 13->9 at before wave 3",
                         res.problems[0])
        self.assertTrue(any(p.startswith("controls: ") for p in res.problems))
        self.assertTrue(any(p.startswith("completeness: ") for p in res.problems))
        # Armed or not: a damaged host never reads as part findings.
        res = pp.evaluate_sweep(_read_dir(self.tmp), RUN_ID, True, gui_mirror.read_png,
                                lambda n: os.path.join(self.tmp, n))
        self.assertEqual(pp.INVALID_HOST_DAMAGED_SUBKIND, res.invalid_subkind)
        self.assertEqual(pp.INVALID_HOST_DAMAGED_SUBKIND,
                         pp.verdicts_document(res, RUN_ID)["invalidSubkind"])

    def test_host_damage_reads_only_a_true_flag(self):
        self.assertIsNone(pp.host_damage({}))
        self.assertIsNone(pp.host_damage({"progress": None}))
        self.assertIsNone(pp.host_damage({"progress": {"hostDamaged": False, "hostDamage": "parts"}}))
        self.assertIsNone(pp.host_damage({"progress": {"hostDamaged": "true"}}))
        self.assertEqual("host damaged: reason=gone detail=None",
                         pp.host_damage({"progress": {"hostDamaged": True, "hostDamage": "gone"}}))
        # A clean sweep (the fixture writes no host keys, like a pre-guard manifest) reports.
        res, _ = self._eval(parts=2)
        self.assertEqual(pp.STATUS_REPORT, res.status)

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

    def test_a_missing_atlas_is_invalid_incomplete(self):
        fx.build_sweep(self.tmp, parts=3)
        victim = [n for n in os.listdir(self.tmp) if n.startswith("part2--")
                  and n.endswith(pp.ATLAS_SUFFIX) and not n.endswith(pp.THUMB_SUFFIX)]
        self.assertEqual(1, len(victim))
        os.remove(os.path.join(self.tmp, victim[0]))
        res = pp.evaluate_sweep(_read_dir(self.tmp), RUN_ID, False, gui_mirror.read_png,
                                lambda n: os.path.join(self.tmp, n))
        self.assertEqual(pp.INVALID_INCOMPLETE_SUBKIND, res.invalid_subkind)
        self.assertIn("atlas-missing", " ".join(res.problems))

    def test_a_skin_flag_disagreement_is_invalid_instrument(self):
        fx.build_sweep(self.tmp, parts=2)
        path = os.path.join(self.tmp, "part1--default--00000001" + pp.SIDECAR_SUFFIX)
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
        doc["skin"] = {"module": "ModulePartVariants", "moduleId": None, "requested": "Dark",
                       "real": "Dark", "snapshot": "Dark", "ghost": "Light"}
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(doc, fh)
        res = pp.evaluate_sweep(_read_dir(self.tmp), RUN_ID, False, gui_mirror.read_png,
                                lambda n: os.path.join(self.tmp, n))
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        self.assertIn("part1--default--00000001 skin flags: sidecar=- derived=skin-mismatch",
                      res.recompute_mismatches)

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
        row = pp.combine([mk(pp.STATUS_INVALID, pp.INVALID_CONTROLS_SUBKIND),
                          mk(pp.STATUS_INVALID, pp.INVALID_HOST_DAMAGED_SUBKIND)], False)
        self.assertEqual(pp.INVALID_HOST_DAMAGED_SUBKIND, row.invalid_subkind)
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
    PartParityMetrics.cs, PartParityHostIntegrity.cs, PartParityStateRules.cs, and
    PartParityCaseCapture.cs only to see that it reads its atlas rows from
    PartParityJson.AtlasRowsFor; comment-stripped), so a renamed C# key or a changed
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
                           ("ProgressKeys", pp.PROGRESS_KEYS),
                           ("StartKeys", pp.START_KEYS), ("StowedRefKeys", pp.STOWED_REF_KEYS),
                           ("StateProofKeys", pp.STATE_PROOF_KEYS),
                           ("EndpointKeys", pp.ENDPOINT_KEYS),
                           ("StateEventKeys", pp.STATE_EVENT_KEYS),
                           ("StartFirstFrameKeys", pp.START_FIRST_FRAME_KEYS),
                           ("SunTrackerKeys", pp.SUN_TRACKER_KEYS),
                           ("CanopyKeys", pp.CANOPY_KEYS),
                           ("ContextNodeKeys", pp.CONTEXT_NODE_KEYS)):
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
        consts = self._string_consts(text)
        self.assertEqual(list(pp.SETUP_STATUSES), self._ident_array(text, "SetupStatuses", consts))
        self.assertEqual((pp.SETUP_ACTION_FAILED, pp.SETUP_STATE_TIMEOUT),
                         (consts["SetupActionFailed"], consts["SetupStateTimeout"]))
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

    def test_the_host_integrity_contract(self):
        text = self._source("PartParityHostIntegrity.cs")
        self.assertEqual(pp.HOST_BASELINE_FILE, self._const(text, "BaselineFileName"))
        self.assertEqual(pp.HOST_DAMAGE_FILE, self._const(text, "DamageFileName"))
        reasons = {n: self._const(text, n) for n in ("DamageGone", "DamageParts", "DamageSituation")}
        m = re.search(r"DamageReasons\s*=\s*\{(.*?)\};", text, flags=re.S)
        self.assertEqual(list(pp.HOST_DAMAGE_REASONS),
                         [reasons[n] for n in re.findall(r"\b(Damage\w+)\b", m.group(1))])
        # The payload's two host keys, last and in this order.
        sweep = self._source("TestCommandPartParitySweep.cs")
        keys = re.findall(r'new KeyValuePair<string, string>\("(\w+)"', sweep)
        self.assertEqual(["hostDamaged", "hostDamage"], keys[-2:])

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

    @staticmethod
    def _string_consts(text):
        return dict(re.findall(r'internal const string (\w+) = "([^"\\]*)";', text))

    @staticmethod
    def _ident_array(text, name, consts):
        """The members of one C# array initializer of const identifiers, resolved."""
        m = re.search(r"\b%s\s*=\s*\{(.*?)\};" % re.escape(name), text, flags=re.S)
        if m is None:
            raise AssertionError("array %s not found" % name)
        return [consts[n] for n in re.findall(r"\b([A-Za-z_]\w*)\b", m.group(1))
                if n != "TestCommandPartParitySweep"]

    def test_the_atlas_rows_live_in_the_pure_writer(self):
        # The row tables are PartParityJson's (AtlasRowsFor); the live capture reads its rows
        # there, so it holds no literal of its own (design D.2).
        text = self._source("PartParityJson.cs")
        self.assertEqual(list(pp.ATLAS_ROWS), self._string_array(text, "StowedAtlasRows"))
        self.assertEqual(list(pp.ATLAS_ROWS_STATE), self._string_array(text, "StateAtlasRows"))
        self.assertIn("isStateCase ? StateAtlasRows : StowedAtlasRows", text)
        capture = self._source("PartParityCaseCapture.cs")
        self.assertIn("PartParityJson.AtlasRowsFor(", capture)
        self.assertNotIn('"ghost-start"', capture)

    def test_the_control_sets_and_the_state_settings(self):
        text = self._source("TestCommandPartParitySweep.cs")
        consts = self._string_consts(text)
        self.assertEqual(list(pp.CONTROL_IDS), self._ident_array(text, "ControlIds", consts))
        self.assertEqual(list(pp.CONTROL_IDS_ORBIT_STATE),
                         self._ident_array(text, "OrbitStateControlIds", consts))
        self.assertEqual(list(pp.CONTROL_IDS_AIR), self._ident_array(text, "AirControlIds", consts))
        self.assertEqual(list(pp.ALL_CONTROL_IDS), self._ident_array(text, "AllControlIds", consts))
        self.assertEqual((pp.CONTROL_STATE_EVENTS_DROPPED, pp.CONTROL_STATE_EVENTS_DROPPED_AIR),
                         (consts["ControlStateEventsDropped"], consts["ControlStateEventsDroppedAir"]))
        self.assertEqual((pp.ARG_NONE, pp.STATES_ALL),
                         (consts["ArgNoneToken"], consts["StatesAllToken"]))
        self.assertEqual((pp.SETTING_STATES, pp.SETTING_CONTEXTS, pp.SETTING_CONTROL_SET),
                         (consts["SettingStates"], consts["SettingContexts"],
                          consts["SettingControlSet"]))
        # ControlSetFor's three branches, by their code shape: no state run -> ControlIds,
        # an air run -> AirControlIds, else OrbitStateControlIds. control_set mirrors them.
        body = text[text.index("internal static string[] ControlSetFor("):]
        body = body[:body.index("private static bool IsIdChar(")]
        self.assertIn("if (!IsStateRun(args)) return (string[])ControlIds.Clone();", body)
        self.assertIn("IsAirRun(args) ? AirControlIds : OrbitStateControlIds", body)
        self.assertIn("args != null && (args.States != null || args.Contexts != null)", text)
        self.assertEqual(pp.CONTROL_IDS, pp.control_set({}))
        self.assertEqual(pp.CONTROL_IDS_AIR, pp.control_set({"contexts": "air"}))
        self.assertEqual(pp.CONTROL_IDS_ORBIT_STATE, pp.control_set({"states": "all"}))

    def test_the_state_rules_contract(self):
        text = self._source("PartParityStateRules.cs")
        consts = self._string_consts(text)
        self.assertEqual(list(pp.FAMILIES), self._ident_array(text, "Families", consts))
        self.assertEqual(list(pp.KNOWN_STATES), self._ident_array(text, "KnownStates", consts))
        self.assertEqual(list(pp.KNOWN_CONTEXTS), self._ident_array(text, "KnownContexts", consts))
        self.assertEqual(list(pp.ORBIT_CONTEXTS), self._ident_array(text, "OrbitContexts", consts))
        self.assertEqual(list(pp.STATE_EXCLUSION_REASONS),
                         self._ident_array(text, "ExclusionReasons", consts))
        self.assertEqual(list(pp.ACTION_RESULTS), self._ident_array(text, "ActionResults", consts))
        self.assertEqual(list(pp.GHOST_OUTCOMES), self._ident_array(text, "GhostOutcomes", consts))
        sweep = self._string_consts(self._source("TestCommandPartParitySweep.cs"))
        self.assertEqual(list(pp.PRESS_GATE_STATUSES),
                         self._ident_array(text, "PressGateStatuses", sweep))
        self.assertEqual(pp.STATE_TIEBREAK_PREFIX, consts["ModuleTiebreakPrefix"])
        self.assertEqual(pp.STATE_STOWED, consts["StateStowed"])
        self.assertEqual(pp.CONTEXT_AIR, consts["ContextAir"])
        self.assertEqual((pp.ROUTE_EVENT, pp.ROUTE_START), (consts["RouteEvent"], consts["RouteStart"]))
        for name, mirror in (
                ("FlagStateNotModelledPrefix", pp.FLAG_STATE_NOT_MODELLED_PREFIX),
                ("FlagStateNotRecorded", pp.FLAG_STATE_NOT_RECORDED),
                ("FlagStartPoseNotFirstFrame", pp.FLAG_START_POSE_NOT_FIRST_FRAME),
                ("FlagStateInvisible", pp.FLAG_STATE_INVISIBLE),
                ("FlagSunTrackerFirstFramePrefix", pp.FLAG_SUN_TRACKER_FIRST_FRAME_PREFIX),
                ("TagStillTracking", pp.TAG_STILL_TRACKING),
                ("TagCanopyWobblePrefix", pp.TAG_CANOPY_WOBBLE_PREFIX),
                ("TagContextHiddenPrefix", pp.TAG_CONTEXT_HIDDEN_PREFIX),
                ("TagJettisonDebrisHiddenPrefix", pp.TAG_JETTISON_DEBRIS_HIDDEN_PREFIX)):
            with self.subTest(const=name):
                self.assertEqual(mirror, consts[name])
        host = self._string_consts(self._source("PartParityHostIntegrity.cs"))
        self.assertEqual(pp.HOST_MODE_AIR, host["ModeAir"])


class PartParityStateReaderTests(unittest.TestCase):
    """The P6 readers (design D.2): the control set a manifest's settings recompute to, the
    atlas rows of a case, the state-case test."""

    def test_control_set_reads_the_settings(self):
        self.assertEqual(pp.CONTROL_IDS, pp.control_set(None))
        self.assertEqual(pp.CONTROL_IDS, pp.control_set({"states": "none", "contexts": "none"}))
        self.assertEqual(pp.CONTROL_IDS_ORBIT_STATE, pp.control_set({"states": "deployed,lit"}))
        self.assertEqual(pp.CONTROL_IDS_ORBIT_STATE, pp.control_set({"contexts": "stacked"}))
        self.assertEqual(pp.CONTROL_IDS_AIR, pp.control_set({"states": "all", "contexts": "air"}))
        self.assertEqual(9, len(pp.CONTROL_IDS_ORBIT_STATE))
        self.assertEqual(len(pp.ALL_CONTROL_IDS), len(set(pp.ALL_CONTROL_IDS)))

    def test_atlas_rows_and_state_cases(self):
        self.assertEqual(("real", "ghost"), pp.atlas_rows_for(False))
        self.assertEqual(("real", "ghost", "ghost-start", "ghost-stowed"), pp.atlas_rows_for(True))
        self.assertFalse(pp.is_state_case("stowed"))
        self.assertFalse(pp.is_state_case(None))
        self.assertTrue(pp.is_state_case("deployed-m3"))

    def test_the_new_setup_statuses_are_faults_and_known(self):
        for status in pp.PRESS_GATE_STATUSES:
            self.assertIn(status, pp.SETUP_STATUSES)
            self.assertTrue(pp.is_setup_fault(status))

    def test_the_view_blocks_are_subsets_of_the_view_keys(self):
        self.assertEqual(pp.VIEW_KEYS[1:-2], pp.START_KEYS)
        self.assertTrue(set(pp.STOWED_REF_KEYS) <= set(pp.START_KEYS))


# ---------------------------------------------------------------------------
# P6 S4: state-case verdicts (design D.6) on synthetic state sweeps. A state case's
# atlas has four rows (real, ghost, ghost-start, ghost-stowed); each view carries the
# event-route measures, a `start` block (real vs ghost-start, START_KEYS) and a
# `stowedRef` block (real vs ghost-stowed, STOWED_REF_KEYS), every one computed by the
# fixture's pixel-by-pixel transcription of PartParityMetrics.Measure.
# ---------------------------------------------------------------------------

STILE = 12
SAREA = STILE * STILE * 0.5  # the box area fx.view_record uses


def _rect(x, y, w, h):
    return fx.rect_mask(STILE, x, y, w, h)


def _deployed():
    return _rect(2, 2, 8, 8)


def _stowed():
    return _rect(4, 4, 4, 4)


def _state_view(view, real, ghost, start, stowed):
    rec = fx.view_record(view, real, ghost, STILE)
    srec = fx.view_record(view, real, start, STILE)
    rec["start"] = {k: srec[k] for k in pp.START_KEYS}
    ref = fx.reference_measure(real, stowed, SAREA)
    rec["stowedRef"] = {k: ref[k] for k in pp.STOWED_REF_KEYS}
    return rec


def _atlas_rows(rows_of_tiles):
    cols = len(rows_of_tiles[0])
    width = cols * STILE
    out = bytearray(width * len(rows_of_tiles) * STILE * 4)
    for row, tiles in enumerate(rows_of_tiles):
        for c, mask in enumerate(tiles):
            for y in range(STILE):
                for x in range(STILE):
                    if mask[y][x]:
                        i = ((row * STILE + y) * width + c * STILE + x) * 4
                        out[i:i + 4] = b"\x80\x80\x80\xff"
    return bytes(out)


def _state_case(case_id, part="longAntenna", family="deployable", state="deployed",
                context="alone", real=None, ghost=None, start=None, stowed=None,
                status="ok", detail=None, proof=None, flags=(), tags=(),
                structure=None, structure_start=None, with_views=True):
    """(sidecar, masks) of one state case; the masks feed _write_state_case's atlas."""
    real = real or _deployed()
    ghost = ghost or _deployed()
    start = start or _deployed()
    stowed = stowed or _stowed()
    views = [_state_view(v, real, ghost, start, stowed) for v in fx.VIEWS] if with_views else []
    doc = fx.sidecar(case_id, part, views, STILE, status)
    doc["state"], doc["context"] = state, context
    doc["setup"]["detail"] = detail
    if views:
        doc["atlas"]["rows"] = list(pp.ATLAS_ROWS_STATE)
    doc["flags"], doc["tags"] = list(flags), list(tags)
    if structure is not None:
        doc["structure"] = structure
    base = {"family": family, "module": "ModuleDeployableAntenna", "moduleIndex": 0,
            "state": state, "action": "Extend", "eventVisible": True,
            "actionResult": "invoked", "reached": True, "waitS": 1.5,
            "endpoint": {"field": "deployState", "value": "EXTENDED"},
            "events": [{"type": "DeployableExtended", "ut": 10.0, "pid": 100000, "midx": 0}],
            "ghostEvent": "applied", "ghostStart": "applied",
            "startFirstFrame": {"atTarget": True, "trackerDeg": None},
            "sunTracker": {"targetDeg": None, "eventDeg": None, "startFirstFrameDeg": None},
            "canopy": {"tiltDeg": None, "wobbleDeg": None}, "nodes": None}
    base.update(proof or {})
    doc["stateProof"] = base
    doc["structureStart"] = structure_start if structure_start is not None \
        else copy.deepcopy(doc["structure"])
    doc["stowedCaseId"] = part.lower() + "--default--deadbeef"
    return doc, (real, ghost, start, stowed)


def _write_state_case(label_dir, doc, masks):
    if doc["views"]:
        n = len(doc["views"])
        rows = [[m] * n for m in masks]
        with open(os.path.join(label_dir, doc["atlas"]["file"]), "wb") as fh:
            fh.write(gui_mirror.write_png(n * STILE, len(rows) * STILE, 4, _atlas_rows(rows)))
    with open(os.path.join(label_dir, doc["caseId"] + pp.SIDECAR_SUFFIX), "w",
              encoding="utf-8") as fh:
        json.dump(doc, fh)


def _stacked_stowed_case(case_id, part="liquidEngine2"):
    """(sidecar, masks) of the stacked stowed shroud case S3 writes: state ``stowed``,
    context ``stacked``, the stowed two-row shape, a stateProof that pressed nothing
    (action and actionResult null, reached true, endpoint context / stowed)."""
    body = fx.rect_mask(STILE, 3, 3, 6, 6)
    views = [fx.view_record(v, body, body, STILE) for v in fx.VIEWS]
    doc = fx.sidecar(case_id, part, views, STILE)
    doc["state"], doc["context"] = "stowed", "stacked"
    doc["stateProof"] = {"family": "jettison", "module": "ModuleJettison", "moduleIndex": 1,
                         "state": "stowed", "action": None, "eventVisible": None,
                         "actionResult": None, "reached": True, "waitS": None,
                         "endpoint": {"field": "context", "value": "stowed"}, "events": None,
                         "ghostEvent": None, "ghostStart": None,
                         "startFirstFrame": {"atTarget": None, "trackerDeg": None},
                         "sunTracker": {"targetDeg": None, "eventDeg": None,
                                        "startFirstFrameDeg": None},
                         "canopy": {"tiltDeg": None, "wobbleDeg": None},
                         "nodes": [{"node": "bottom", "part": "fuelTankSmallFlat"}]}
    doc["structureStart"] = None
    doc["stowedCaseId"] = part.lower() + "--default--deadbeef"
    return doc, (body, body)


def _events_dropped_control():
    # The event-route ghost built without the polled events: it stays stowed.
    return _state_case(pp.CONTROL_STATE_EVENTS_DROPPED, ghost=_stowed())


def _build_state_sweep(label_dir, cases, settings=None, control_outcomes=None,
                       state_control=None, write_control_set=True):
    """A COMPLETE state-run sweep: the run's control set (stowed controls as in
    fx.build_sweep, the state control from ``state_control``) and ``cases``, a list of
    (sidecar, masks). Returns the manifest."""
    os.makedirs(label_dir, exist_ok=True)
    settings = dict(settings or {"states": "all", "contexts": "none"})
    ids = pp.control_set(settings)
    if write_control_set:
        settings.setdefault("controlSet", ",".join(ids))
    four = lambda m: [m] * len(fx.VIEWS)  # noqa: E731
    body = fx.rect_mask(STILE, 3, 3, 6, 6)
    empty = fx.empty_mask(STILE)
    for cid in ids:
        if cid in pp.STATE_CONTROL_IDS:
            doc, masks = state_control or _events_dropped_control()
            if cid != doc["caseId"]:
                doc = dict(doc, caseId=cid, atlas=dict(doc["atlas"], file=cid + ".png"))
            _write_state_case(label_dir, doc, masks)
        elif cid == "ctl-empty":
            fx.write_case(label_dir, cid, "fuelTankSmallFlat", four(empty), four(empty), STILE)
        elif cid == "ctl-ghost-absent":
            fx.write_case(label_dir, cid, "fuelTankSmallFlat", four(body), four(empty), STILE)
        else:
            fx.write_case(label_dir, cid, "mk1pod.v2", four(body),
                          four(fx.rect_mask(STILE, 2, 3, 6, 6)), STILE)
    rows = []
    for i, (doc, masks) in enumerate(cases):
        _write_state_case(label_dir, doc, masks)
        proof = doc.get("stateProof") or {}
        rows.append({"index": i, "caseId": doc["caseId"], "part": doc["part"],
                     "title": doc["part"], "source": "Squad", "skin": "default",
                     "state": doc["state"], "context": doc["context"],
                     "sidecar": doc["caseId"] + pp.SIDECAR_SUFFIX,
                     "setupStatus": doc["setup"]["status"], "maxShapeBlobPx": 0,
                     "maxColorBlobPx": 0, "minIou": 1, "missingRendered": 0,
                     "extraRendered": 0, "family": proof.get("family"),
                     "actionResult": proof.get("actionResult"), "reached": proof.get("reached")})
    outcomes = dict(control_outcomes or {})
    all_ok = all(outcomes.get(c, "ok") == "ok" for c in ids)
    manifest = {
        "schema": pp.MANIFEST_SCHEMA, "label": "pp3", "profile": "stock-minimal",
        "gameDataMods": ["Parsek", "Squad"], "dllHash": "0" * 64, "tile": STILE,
        "views": list(fx.VIEWS), "measure": {}, "settings": settings,
        "controls": [{"id": c, "subject": "s", "required": "r",
                      "outcome": outcomes.get(c, "ok"), "detail": ""} for c in ids],
        "cases": rows, "exclusions": [],
        "progress": {"complete": True, "cases": len(rows), "completeCases": len(rows),
                     "setupFaults": 0, "controls": "ok" if all_ok else "bad", "crashed": []},
    }
    with open(os.path.join(label_dir, pp.MANIFEST_FILE), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh)
    return manifest


def _shifted():
    # The deployed shape 3 px to the right: a shape fail in every view.
    return _rect(5, 2, 7, 8)


class StateCaseJudgeTests(unittest.TestCase):
    """judge_case on a state case: two routes, the flags, the caps (design D.6)."""

    def test_a_matching_state_case_passes_on_both_routes(self):
        doc, _ = _state_case("ant--default--deployed--1")
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_PASS, cv.verdict)
        self.assertEqual(("event", "start"), tuple(r.route for r in cv.routes))
        self.assertEqual("deployed", cv.state)
        self.assertEqual("deployable", cv.family)
        self.assertEqual((), cv.state_flags)
        d = cv.to_dict()
        self.assertEqual({"event", "start"}, set(d["routes"]))
        self.assertEqual([], d["stateFlags"])
        json.dumps(d)

    def test_the_worse_route_wins_and_names_itself(self):
        doc, _ = _state_case("ant--default--deployed--1", start=_shifted())
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_FAIL, cv.verdict)
        self.assertEqual(pp.VERDICT_PASS, cv.routes[0].verdict)
        self.assertEqual(pp.VERDICT_FAIL, cv.routes[1].verdict)
        self.assertTrue(cv.worst_view.startswith("start:"))
        self.assertTrue(cv.reasons and all(r.startswith("start:") for r in cv.reasons))
        doc, _ = _state_case("ant--default--deployed--1", ghost=_shifted())
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_FAIL, cv.verdict)
        self.assertTrue(cv.worst_view.startswith("event:"))
        self.assertTrue(all(r.startswith("event:") for r in cv.reasons))
        # Both fail: the event route names the worst view (a tie keeps the event route).
        doc, _ = _state_case("ant--default--deployed--1", ghost=_shifted(), start=_shifted())
        cv = pp.judge_case(doc)
        self.assertTrue(cv.worst_view.startswith("event:"))
        self.assertTrue(any(r.startswith("start:") for r in cv.reasons))

    def test_the_stowed_reference_row_is_not_judged(self):
        # stowedRef differs hugely (the state is visible) and the case still passes.
        doc, _ = _state_case("ant--default--deployed--1")
        self.assertGreater(doc["views"][0]["stowedRef"]["realOnlyBlobPct"], pp.SHAPE_BLOB_FAIL_PCT)
        self.assertEqual(pp.VERDICT_PASS, pp.judge_case(doc).verdict)

    def test_a_missing_rendered_mesh_on_the_start_ghost_fails_the_start_route(self):
        missing = [{"key": "a/b/dish", "mesh": "dish", "rendered": True, "nonVisual": None}]
        doc, _ = _state_case("ant--default--deployed--1",
                             structure_start={"missingOnGhost": missing, "extraOnGhost": []})
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_FAIL, cv.verdict)
        self.assertIn("start:missing-rendered:a/b/dish", cv.reasons)
        self.assertEqual(pp.VERDICT_PASS, cv.routes[0].verdict)

    def test_each_flag(self):
        cases = (
            ({"ghostEvent": "no-info"}, (), pp.VERDICT_WARN, "state-not-modelled:event:no-info"),
            ({"ghostStart": "pose-not-sampled"}, (), pp.VERDICT_WARN,
             "state-not-modelled:start:pose-not-sampled"),
            ({}, ("state-not-modelled:start:not-reached",), pp.VERDICT_WARN,
             "state-not-modelled:start:not-reached"),
            ({"events": []}, (), pp.VERDICT_WARN, "state-not-recorded"),
            ({}, ("state-not-recorded",), pp.VERDICT_WARN, "state-not-recorded"),
            ({"startFirstFrame": {"atTarget": False, "trackerDeg": None}}, (),
             pp.VERDICT_FAIL, "start-pose-not-first-frame"),
            ({}, ("start-pose-not-first-frame",), pp.VERDICT_FAIL, "start-pose-not-first-frame"),
        )
        for proof, written, verdict, flag in cases:
            with self.subTest(flag=flag, written=written):
                doc, _ = _state_case("ant--default--deployed--1", proof=proof, flags=written)
                cv = pp.judge_case(doc)
                self.assertEqual(verdict, cv.verdict)
                self.assertIn(flag, cv.state_flags)
                self.assertIn("flag:" + flag, cv.reasons)
        # A written flag and its derived twin are one flag.
        doc, _ = _state_case("ant--default--deployed--1", proof={"ghostEvent": "no-info"},
                             flags=("state-not-modelled:event:no-info",))
        self.assertEqual(1, pp.judge_case(doc).state_flags.count("state-not-modelled:event:no-info"))
        # A null (unmeasured) outcome derives nothing.
        doc, _ = _state_case("ant--default--deployed--1",
                             proof={"ghostEvent": None, "events": None,
                                    "startFirstFrame": {"atTarget": None, "trackerDeg": None}})
        self.assertEqual((), pp.judge_case(doc).state_flags)

    def test_a_flag_never_lowers_a_picture_fail(self):
        doc, _ = _state_case("ant--default--deployed--1", ghost=_shifted(),
                             proof={"ghostEvent": "no-info"})
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)

    def test_state_invisible_and_the_tags_are_report_only(self):
        doc, _ = _state_case("ant--default--deployed--1", stowed=_deployed(),
                             flags=("sun-tracker-first-frame:42.0",),
                             tags=("still-tracking", "context-hidden:1",
                                   "jettison-debris-hidden:2", "canopy-wobble:4.5"))
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_PASS, cv.verdict)
        self.assertEqual(("sun-tracker-first-frame:42.0", "state-invisible"), cv.state_flags)
        self.assertFalse(any(r.startswith("flag:") for r in cv.reasons))
        # A visible state is not state-invisible.
        doc, _ = _state_case("ant--default--deployed--1")
        self.assertNotIn("state-invisible", pp.judge_case(doc).state_flags)

    def test_the_context_and_depth_mask_caps_apply_per_route(self):
        doc, _ = _state_case("gear--default--deployed--1", family="gear", ghost=_shifted(),
                             start=_shifted(), tags=(pp.TAG_CONTEXT_DEPENDENT,))
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_WARN, cv.verdict)
        self.assertIn("event:" + pp.REASON_CONTEXT_POSE, cv.reasons)
        self.assertIn("start:" + pp.REASON_CONTEXT_POSE, cv.reasons)
        mask = [{"key": "a/mask", "mesh": "m", "rendered": True, "nonVisual": "depth-mask"}]
        doc, _ = _state_case("bay--default--open--1", start=_shifted(),
                             structure_start={"missingOnGhost": mask, "extraOnGhost": []})
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_WARN, cv.verdict)
        self.assertIn("start:" + pp.REASON_DEPTH_MASK, cv.reasons)
        # The event route has no mask: its fail is not capped by the start route's.
        doc, _ = _state_case("bay--default--open--1", ghost=_shifted(), start=_shifted(),
                             structure_start={"missingOnGhost": mask, "extraOnGhost": []})
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)

    def _air(self, rot=2.5, pos=0.0, scl=0.0, context="air", tilt=1.0, wobble=1.0,
             transform="canopy", tags=(), key="chute/canopy", **extra):
        structure = {"missingOnGhost": [], "extraOnGhost": [], "meshDiffers": [],
                     "renderedDiffers": [], "materialDiffers": [],
                     "transformDiffers": [{"key": key, "positionDeltaM": pos,
                                           "rotationDeltaDeg": rot, "scaleDeltaPct": scl}]}
        structure.update(extra)
        doc, _ = _state_case("chute--default--semi--air--1", part="parachuteSingle",
                             family="parachute", state="semi", context=context,
                             ghost=_shifted(), structure=structure, tags=tags,
                             structure_start=copy.deepcopy(structure),
                             proof={"canopy": {"tiltDeg": tilt, "wobbleDeg": wobble,
                                               "transform": transform}})
        return pp.judge_case(doc)

    def test_the_canopy_cap_on_an_air_case(self):
        cv = self._air()  # 2.5 <= 1.0 + 1.0 + 1.0
        self.assertEqual(pp.VERDICT_WARN, cv.verdict)
        self.assertIn("event:" + pp.REASON_CANOPY_AIRFLOW, cv.reasons)
        for label, kw in (("rotation over the bound", {"rot": 3.5}),
                          ("not an air case", {"context": "alone"}),
                          ("a position delta", {"pos": 0.01}),
                          ("a scale delta", {"scl": 5.0}),
                          ("no canopy angles", {"tilt": None}),
                          ("a mesh difference", {"meshDiffers": [{"key": "chute/canopy"}]}),
                          ("no transform difference", {"transformDiffers": []})):
            with self.subTest(label):
                self.assertEqual(pp.VERDICT_FAIL, self._air(**kw).verdict)

    def test_the_canopy_cap_is_limited_to_the_canopy_and_a_settled_capture(self):
        # Fix 3 of the PR #2766 review: the cap was too wide against D.4.
        self.assertEqual(pp.VERDICT_WARN, self._air(key="model/canopy/mesh#1").verdict)
        housing = [{"key": "chute/canopy", "positionDeltaM": 0.0, "rotationDeltaDeg": 2.5,
                    "scaleDeltaPct": 0.0},
                   {"key": "chute/housing", "positionDeltaM": 0.0, "rotationDeltaDeg": 2.5,
                    "scaleDeltaPct": 0.0}]
        for label, kw in (
                ("a non-canopy renderer rotated too", {"transformDiffers": housing}),
                ("only a non-canopy renderer", {"key": "chute/housing"}),
                ("a canopy-like name is not the canopy", {"key": "chute/canopyCover"}),
                ("a canopy material differs", {"materialDiffers": [
                    {"key": "chute/canopy", "field": "mainTextures", "real": "red",
                     "ghost": "white"}]}),
                ("no canopy transform named", {"transform": None}),
                ("tilt + wobble at the window: a timeout capture", {"tilt": 1.5, "wobble": 1.5,
                                                                   "rot": 1.0}),
                ("a timeout capture tagged canopy-wobble", {"tags": ("canopy-wobble:2.0",)}),
                ("a huge tilt", {"tilt": 85.0, "wobble": 5.0, "rot": 40.0})):
            with self.subTest(label):
                self.assertEqual(pp.VERDICT_FAIL, self._air(**kw).verdict)
        # The bound stays under the ceiling: 1.4 + 1.5 + 1 = 3.9 <= 4.0.
        self.assertEqual(pp.VERDICT_WARN, self._air(tilt=1.4, wobble=1.5, rot=3.85).verdict)
        self.assertEqual(pp.VERDICT_FAIL, self._air(tilt=1.4, wobble=1.5, rot=3.95).verdict)
        self.assertEqual(4.0, pp.CANOPY_ROTATION_CEILING_DEG)
        self.assertTrue(pp.is_canopy_renderer_key("a/canopy", "canopy"))
        self.assertTrue(pp.is_canopy_renderer_key("a/canopy/b#2", "canopy"))
        self.assertFalse(pp.is_canopy_renderer_key("a/canopy2/b", "canopy"))
        self.assertFalse(pp.is_canopy_renderer_key("a/b", ""))

    def test_the_canopy_window_is_the_csharp_one(self):
        path = os.path.join(CS_DIR, "PartParityStateRules.cs")
        with open(path, encoding="utf-8") as fh:
            text = _strip_cs_comments(fh.read())
        m = re.search(r"const double CanopyWindowDeg\s*=\s*([0-9.]+)\s*;", text)
        self.assertIsNotNone(m, "CanopyWindowDeg not found in %s" % path)
        self.assertEqual(float(m.group(1)), pp.CANOPY_WINDOW_DEG)

    def test_an_ok_case_whose_press_proof_says_not_reached_is_an_error(self):
        # Fix 2 of the PR #2766 review: setup ok but the proof contradicts it.
        same = dict(real=_stowed(), ghost=_stowed(), start=_stowed())
        for label, kw, needle in (
                ("reached false", {"proof": {"reached": False, "events": []}}, "reached=False"),
                ("reached null", {"proof": {"reached": None}}, "reached=None"),
                ("threw", {"proof": {"actionResult": "threw"}}, "actionResult=threw"),
                ("refused", {"proof": {"actionResult": "refused", "reached": False}},
                 "actionResult=refused"),
                ("action null on a state case", {"proof": {"action": None}},
                 "stateProof.action=null"),
                ("still-animating, not reached", {"status": pp.SETUP_STILL_ANIMATING,
                                                  "proof": {"reached": False}}, "reached=False")):
            with self.subTest(label):
                doc, _ = _state_case("ant--default--deployed--1", **dict(same, **kw))
                cv = pp.judge_case(doc)
                self.assertEqual(pp.VERDICT_ERROR, cv.verdict)
                self.assertIn(needle, cv.reasons[0])
                self.assertTrue(cv.reasons[0].startswith("press-proof:"))
        doc, _ = _state_case("ant--default--deployed--1")
        doc["stateProof"] = None
        self.assertIn("no stateProof", pp.press_proof_problem(doc))
        # A recorded press fault is not a contradiction; nor is a stowed case.
        doc, _ = _state_case("ant--default--deployed--1", status=pp.SETUP_ACTION_FAILED,
                             with_views=False, proof={"actionResult": "missing", "reached": False})
        self.assertIsNone(pp.press_proof_problem(doc))
        self.assertIsNone(pp.press_proof_problem(_load("decoupler.2--basic--6fabf1e2.part.json")))

    def test_only_a_reached_stacked_stowed_proof_may_skip_the_press(self):
        # Re-review note 1: the no-press exception is the stacked stowed shape only.
        for label, mutate in (
                ("context alone", lambda d: d.__setitem__("context", "alone")),
                ("context air", lambda d: d.__setitem__("context", "air")),
                ("not reached", lambda d: d["stateProof"].__setitem__("reached", False)),
                ("reached null", lambda d: d["stateProof"].__setitem__("reached", None))):
            with self.subTest(label):
                doc, _ = _stacked_stowed_case("liquidengine2--default--stowed--stacked--1")
                mutate(doc)
                self.assertIn("only a reached stacked stowed case", pp.press_proof_problem(doc))
                self.assertEqual(pp.VERDICT_ERROR, pp.judge_case(doc).verdict)

    def test_a_canopy_rotation_is_judged_by_its_size(self):
        # Re-review note 3: a negative delta is a rotation of that size, NaN is refused.
        self.assertEqual(pp.VERDICT_WARN, self._air(rot=-2.5).verdict)
        self.assertEqual(pp.VERDICT_FAIL, self._air(rot=-40.0).verdict)
        self.assertEqual(pp.VERDICT_FAIL, self._air(rot=float("nan")).verdict)

    def test_a_canopy_wobble_flag_refuses_the_cap_too(self):
        # Re-review note 4: the timeout marker may come in flags as well as tags.
        doc_cap = self._air()
        self.assertEqual(pp.VERDICT_WARN, doc_cap.verdict)
        structure = {"missingOnGhost": [], "extraOnGhost": [], "meshDiffers": [],
                     "renderedDiffers": [], "materialDiffers": [],
                     "transformDiffers": [{"key": "chute/canopy", "positionDeltaM": 0.0,
                                           "rotationDeltaDeg": 2.5, "scaleDeltaPct": 0.0}]}
        doc, _ = _state_case("chute--default--semi--air--1", part="parachuteSingle",
                             family="parachute", state="semi", context="air",
                             ghost=_shifted(), structure=structure,
                             structure_start=copy.deepcopy(structure),
                             flags=("canopy-wobble:2.0",),
                             proof={"canopy": {"tiltDeg": 1.0, "wobbleDeg": 1.0,
                                               "transform": "canopy"}})
        self.assertIsNone(pp.canopy_cap_for(doc))
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)

    def test_the_stacked_stowed_context_case_needs_no_press(self):
        doc, _ = _stacked_stowed_case("liquidengine2--default--stowed--stacked--1")
        self.assertIsNone(pp.press_proof_problem(doc))
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_PASS, cv.verdict)
        self.assertEqual((), cv.routes)
        self.assertIsNone(cv.state)

    def test_a_state_case_without_start_blocks_is_an_error(self):
        doc, _ = _state_case("ant--default--deployed--1")
        for v in doc["views"]:
            v["start"] = None
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_ERROR, cv.verdict)
        self.assertIn("start:no-views", cv.reasons)

    def test_a_press_fault_is_an_error_case(self):
        doc, _ = _state_case("ant--default--deployed--1", status=pp.SETUP_ACTION_FAILED,
                             detail="missing", with_views=False,
                             proof={"actionResult": "missing", "reached": False})
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_ERROR, cv.verdict)
        self.assertEqual("setup:action-failed", cv.reasons[0])

    def test_a_stowed_case_keeps_its_pre_p6_verdict_document(self):
        doc = _load("decoupler.2--basic--6fabf1e2.part.json")
        cv = pp.judge_case(doc)
        self.assertEqual((), cv.routes)
        self.assertIsNone(cv.state)
        self.assertEqual({"caseId", "part", "skin", "source", "verdict", "reasons",
                          "worstView", "maxShapeBlobPx", "maxShapeBlobPct",
                          "maxColorBlobPx", "setupStatus"}, set(cv.to_dict()))


class StateRecomputeTests(unittest.TestCase):
    """The four-row recompute and the stowedRef key subset (design D.6)."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="pp-state-recompute-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _written(self, **kw):
        doc, masks = _state_case("ant--default--deployed--1", **kw)
        _write_state_case(self.tmp, doc, masks)
        atlas = gui_mirror.read_png(os.path.join(self.tmp, doc["atlas"]["file"]))
        return doc, atlas

    def test_a_state_atlas_recomputes_clean_on_every_row(self):
        for kw in ({}, {"ghost": _shifted()}, {"start": _shifted()},
                   {"stowed": _deployed()}, {"stowed": fx.empty_mask(STILE)}):
            with self.subTest(kw=sorted(kw)):
                doc, atlas = self._written(**kw)
                self.assertEqual([], pp.recompute_case(doc, atlas))

    def test_a_mutated_number_on_each_row_is_a_mismatch(self):
        doc, atlas = self._written()
        for label, mutate, needle in (
                ("row 1", lambda d: d["views"][0].__setitem__("realPx", 1), "side-a.realPx"),
                ("row 2", lambda d: d["views"][1]["start"].__setitem__("ghostPx", 3),
                 "side-b/start.ghostPx"),
                ("row 2 iou", lambda d: d["views"][1]["start"].__setitem__("iou", 0.5),
                 "side-b/start.iou"),
                ("row 3", lambda d: d["views"][2]["stowedRef"].__setitem__("ghostOnlyBlobPx", 9),
                 "top/stowedRef.ghostOnlyBlobPx")):
            with self.subTest(label):
                d = copy.deepcopy(doc)
                mutate(d)
                diffs = pp.recompute_case(d, atlas)
                self.assertTrue(any(needle in x for x in diffs), diffs)
        # A stowedRef percentage is checked against the box area the view's other
        # non-zero blobs imply (here the event route's, its ghost shifted).
        doc, atlas = self._written(ghost=_shifted())
        self.assertEqual([], pp.recompute_case(doc, atlas))
        d = copy.deepcopy(doc)
        d["views"][3]["stowedRef"]["realOnlyBlobPct"] = 1.0
        self.assertTrue(any("bottom.stowedRef" in x for x in pp.recompute_case(d, atlas)))

    def test_a_flipped_pixel_on_each_row_is_a_mismatch(self):
        doc, (w, h, bpp, px) = self._written()
        for row, needle in ((1, "side-a.ghostPx"), (2, "side-a/start.ghostPx"),
                            (3, "side-a/stowedRef.ghostPx")):
            with self.subTest(row=row):
                buf = bytearray(px)
                buf[((row * STILE) * w + 0) * 4 + 3] = 255  # top-left pixel of the row
                diffs = pp.recompute_case(doc, (w, h, bpp, bytes(buf)))
                self.assertTrue(any(needle in x for x in diffs), diffs)

    def test_a_missing_block_or_key_is_a_mismatch(self):
        doc, atlas = self._written()
        d = copy.deepcopy(doc)
        del d["views"][0]["stowedRef"]["ghostOnlyTolPx"]
        self.assertIn("side-a.stowedRef: missing key(s) ghostOnlyTolPx", pp.recompute_case(d, atlas))
        d = copy.deepcopy(doc)
        d["views"][2]["start"] = None
        self.assertIn("top.start: block missing", pp.recompute_case(d, atlas))
        # Exactly the stowedRef keys: an extra measure there is not checked.
        d = copy.deepcopy(doc)
        d["views"][0]["stowedRef"]["iou"] = 0.123
        self.assertEqual([], pp.recompute_case(d, atlas))

    def test_the_row_layout_is_checked(self):
        doc, atlas = self._written()
        d = copy.deepcopy(doc)
        d["atlas"]["rows"] = ["real", "ghost"]
        self.assertIn("expected", pp.recompute_case(d, atlas)[0])
        w, h, bpp, px = atlas
        self.assertIn("atlas size", pp.recompute_case(doc, (w, h // 2, bpp, px[:len(px) // 2]))[0])

    def test_null_percentages_follow_the_view(self):
        doc, atlas = self._written()
        d = copy.deepcopy(doc)
        for blk in (d["views"][0], d["views"][0]["start"], d["views"][0]["stowedRef"]):
            blk["realOnlyBlobPct"] = blk["ghostOnlyBlobPct"] = None
        self.assertEqual([], pp.recompute_case(d, atlas))
        d["views"][0]["stowedRef"]["realOnlyBlobPct"] = 2.0
        self.assertTrue(any("side-a.stowedRef.realOnlyBlobPct" in x
                            for x in pp.recompute_case(d, atlas)))


class StateSweepTests(unittest.TestCase):
    """evaluate_sweep over a state run: the press gate, the control set and the state
    controls, the instrument checks, the verdicts document (design D.2, D.6)."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="pp-state-sweep-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _eval(self, cases, **kw):
        _build_state_sweep(self.tmp, cases, **kw)
        sweep = _read_dir(self.tmp, "pp3")
        return pp.evaluate_sweep(sweep, RUN_ID, False, gui_mirror.read_png,
                                 lambda n: os.path.join(self.tmp, n)), sweep

    @staticmethod
    def _cases(family="deployable", n=4, failed=0, status=pp.SETUP_ACTION_FAILED, start=0):
        out = []
        for i in range(n):
            cid = "%s%d--default--deployed--%08d" % (family, i, start + i)
            if i < failed:
                out.append(_state_case(cid, part="%s%d" % (family, i), family=family,
                                       status=status, detail="detail-%d" % i,
                                       with_views=False, proof={"reached": False}))
            else:
                out.append(_state_case(cid, part="%s%d" % (family, i), family=family))
        return out

    def test_a_clean_state_run_reports_and_recomputes_the_state_control(self):
        res, _ = self._eval(self._cases() + self._cases("light", 2, start=10))
        self.assertEqual(pp.STATUS_REPORT, res.status, res.problems)
        self.assertEqual(6, res.counts["cases"])
        self.assertEqual(6, res.counts[pp.VERDICT_PASS])
        self.assertIn(pp.CONTROL_STATE_EVENTS_DROPPED, res.recompute_ids)
        self.assertEqual(list(pp.CONTROL_IDS_ORBIT_STATE), res.recompute_ids[:9])
        self.assertEqual([], res.recompute_mismatches)
        self.assertTrue(res.press.ok)
        self.assertEqual((("deployable", 4, 0), ("light", 2, 0)), res.press.families)
        doc = pp.verdicts_document(res, RUN_ID)
        self.assertEqual({"ok": True, "families": {"deployable": {"cases": 4, "pressFailed": 0},
                                                   "light": {"cases": 2, "pressFailed": 0}},
                          "problems": []}, doc["statePress"])
        self.assertIn("stateFlags", doc)
        self.assertEqual({"event", "start"}, set(doc["cases"][0]["routes"]))
        json.dumps(doc)
        self.assertIn("press=ok", pp.format_summary(pp.combine([res], False)))

    def test_a_family_of_all_action_failed_is_invalid_state_press(self):
        res, _ = self._eval(self._cases(failed=4) + self._cases("light", 2, start=10))
        self.assertEqual((pp.STATUS_INVALID, pp.INVALID_STATE_PRESS_SUBKIND),
                         (res.status, res.invalid_subkind))
        self.assertIn("family=deployable 4/4 case(s) action-failed|state-timeout: first "
                      "deployable0--default--deployed--00000000 action-failed: detail-0",
                      res.problems[0])
        self.assertNotIn("family=light", " ".join(res.problems))
        self.assertEqual(4, res.counts[pp.VERDICT_ERROR])
        self.assertEqual(pp.INVALID_STATE_PRESS_SUBKIND,
                         pp.combine([res], False).invalid_subkind)

    def test_the_press_line_is_more_than_half(self):
        res, _ = self._eval(self._cases(failed=2))  # 2 of 4: not over half
        self.assertEqual(pp.STATUS_REPORT, res.status, res.problems)
        shutil.rmtree(self.tmp)
        res, _ = self._eval(self._cases(failed=3, status=pp.SETUP_STATE_TIMEOUT))
        self.assertEqual(pp.INVALID_STATE_PRESS_SUBKIND, res.invalid_subkind)
        shutil.rmtree(self.tmp)
        # A one-case family whose case failed.
        res, _ = self._eval(self._cases() + self._cases("airbrake", 1, failed=1, start=20))
        self.assertEqual(pp.INVALID_STATE_PRESS_SUBKIND, res.invalid_subkind)
        self.assertIn("family=airbrake 1/1", res.problems[0])
        shutil.rmtree(self.tmp)
        # Other setup faults are not press faults.
        res, _ = self._eval(self._cases(failed=4, status=pp.SETUP_SPAWN_TIMEOUT))
        self.assertEqual(pp.STATUS_REPORT, res.status, res.problems)

    def test_the_press_gate_reads_the_sidecar_family_when_the_row_has_none(self):
        cases = self._cases(failed=1, n=1)
        _build_state_sweep(self.tmp, cases)
        sweep = _read_dir(self.tmp, "pp3")
        for row in sweep.manifest["cases"]:
            row["family"] = None
        check = pp.check_state_press(sweep.manifest, sweep.sidecars)
        self.assertFalse(check.ok)
        self.assertIn("family=deployable", check.problems[0])

    def test_the_press_gate_ranks_after_the_controls_and_before_the_recompute(self):
        res, _ = self._eval(self._cases(failed=4),
                            control_outcomes={"ctl-ghost-flip": pp.CONTROLS_BAD})
        self.assertEqual(pp.INVALID_CONTROLS_SUBKIND, res.invalid_subkind)
        self.assertTrue(any(p.startswith("state press:") for p in res.problems))
        shutil.rmtree(self.tmp)
        bad = _events_dropped_control()
        bad[0]["views"][0]["stowedRef"]["realPx"] += 1
        res, _ = self._eval(self._cases(failed=4), state_control=bad)
        self.assertEqual(pp.INVALID_STATE_PRESS_SUBKIND, res.invalid_subkind)
        self.assertTrue(res.recompute_mismatches)
        self.assertTrue(res.problems[0].startswith("state press:"))
        ranked = [pp.combine([pp.SweepResult("x", pp.STATUS_INVALID, sub, False, [], None, None,
                                             [], [], [], pp._count([]))], False).invalid_subkind
                  for sub in pp.INVALID_SUBKINDS]
        self.assertEqual(list(pp.INVALID_SUBKINDS), ranked)
        mixed = [pp.SweepResult(s, pp.STATUS_INVALID, s, False, [], None, None, [], [], [],
                                pp._count([])) for s in pp.INVALID_SUBKINDS]
        order = []
        while mixed:
            sub = pp.combine(mixed, False).invalid_subkind
            order.append(sub)
            mixed = [m for m in mixed if m.invalid_subkind != sub]
        self.assertEqual(list(hlib.PARTPARITY_INVALID_SUBKIND_ORDER), order)

    def test_a_mutated_row_reds_invalid_instrument(self):
        # The state control is always recomputed: a mutated stowedRef count on it.
        bad = _events_dropped_control()
        bad[0]["views"][2]["stowedRef"]["ghostOnlyPx"] += 1
        res, _ = self._eval(self._cases(), state_control=bad)
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        self.assertIn("top/stowedRef.ghostOnlyPx", " ".join(res.recompute_mismatches))
        shutil.rmtree(self.tmp)
        # A flipped pixel of row 2 (ghost-start) on it.
        _build_state_sweep(self.tmp, self._cases())
        path = os.path.join(self.tmp, pp.CONTROL_STATE_EVENTS_DROPPED + pp.ATLAS_SUFFIX)
        w, h, bpp, px = gui_mirror.read_png(path)
        buf = bytearray(px)
        buf[((2 * STILE) * w) * 4 + 3] = 255
        with open(path, "wb") as fh:
            fh.write(gui_mirror.write_png(w, h, bpp, bytes(buf)))
        sweep = _read_dir(self.tmp, "pp3")
        res = pp.evaluate_sweep(sweep, RUN_ID, False, gui_mirror.read_png,
                                lambda n: os.path.join(self.tmp, n))
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        self.assertIn("side-a/start.ghostPx", " ".join(res.recompute_mismatches))

    def test_a_missing_stowed_ref_key_on_any_state_case_is_invalid_instrument(self):
        cases = self._cases(n=8)
        sample = pp.select_recompute_sample([d["caseId"] for d, _ in cases], RUN_ID)
        unsampled = next(d for d, _ in cases if d["caseId"] not in sample)
        del unsampled["views"][1]["stowedRef"]["realOnlyBlobPct"]
        res, _ = self._eval(cases)
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        self.assertIn("%s side-b.stowedRef: missing key(s) realOnlyBlobPct" % unsampled["caseId"],
                      res.recompute_mismatches)

    def test_the_control_set_follows_the_states_setting(self):
        for settings, want in (
                ({"states": "deployed,lit", "contexts": "none"}, pp.CONTROL_IDS_ORBIT_STATE),
                ({"states": "none", "contexts": "stacked"}, pp.CONTROL_IDS_ORBIT_STATE),
                ({"states": "all", "contexts": "air"}, pp.CONTROL_IDS_AIR)):
            with self.subTest(settings=settings):
                shutil.rmtree(self.tmp, ignore_errors=True)
                ctl = (_state_case(pp.CONTROL_STATE_EVENTS_DROPPED_AIR, part="parachuteSingle",
                                   family="parachute", state="semi", context="air",
                                   ghost=_stowed())
                       if want == pp.CONTROL_IDS_AIR else None)
                res, sweep = self._eval(self._cases(), settings=settings, state_control=ctl)
                self.assertEqual(pp.STATUS_REPORT, res.status, res.problems)
                self.assertEqual(want, tuple(cid for cid, _ in res.controls.outcomes))
                self.assertEqual(len(want) + 4, res.completeness.planned)
                # The set's own state control absent from the manifest: a controls fault.
                m = copy.deepcopy(sweep.manifest)
                m["controls"] = [c for c in m["controls"] if c["id"] not in pp.STATE_CONTROL_IDS]
                problems = pp.check_controls(m).problems
                self.assertTrue(any(p.endswith(":absent") and p.split(":")[0]
                                    in pp.STATE_CONTROL_IDS for p in problems), problems)
                # Its sidecar absent: incomplete.
                side = dict(sweep.sidecars)
                side.pop(want[-1])
                self.assertFalse(pp.check_completeness(sweep.manifest, side).ok)
        # A stowed run still requires exactly the eight.
        self.assertEqual(pp.CONTROL_IDS, pp.manifest_control_set({"settings": {"skins": "all"}}))

    def test_a_written_control_set_that_disagrees_is_invalid_instrument(self):
        _build_state_sweep(self.tmp, self._cases())
        sweep = _read_dir(self.tmp, "pp3")
        self.assertIsNone(pp.control_set_disagreement(sweep.manifest))
        sweep.manifest["settings"]["controlSet"] = ",".join(pp.CONTROL_IDS)
        self.assertIn("settings.controlSet=", pp.control_set_disagreement(sweep.manifest))
        res = pp.evaluate_sweep(sweep, RUN_ID, False, gui_mirror.read_png,
                                lambda n: os.path.join(self.tmp, n))
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        # A manifest written before P6 carries no controlSet: nothing to compare.
        self.assertIsNone(pp.control_set_disagreement({"settings": {"skins": "all"}}))

    def test_the_state_control_outcome_is_re_judged(self):
        good, _ = _events_dropped_control()
        self.assertEqual((pp.CONTROLS_OK, ""), pp.judge_state_control(good))
        for label, mutate, detail in (
                ("setup", lambda d: d["setup"].__setitem__("status", pp.SETUP_STATE_TIMEOUT),
                 "setup state-timeout"),
                ("reached", lambda d: d["stateProof"].__setitem__("reached", False),
                 "the press never reached the state"),
                ("events", lambda d: d["stateProof"].__setitem__("events", []),
                 "no event recorded"),
                ("views", lambda d: d.__setitem__("views", []), "no views"),
                ("event route", lambda d: [v.update({"realOnlyBlobPx": 0, "ghostOnlyBlobPx": 0,
                                                     "realOnlyBlobPct": 0.0,
                                                     "ghostOnlyBlobPct": 0.0})
                                           for v in d["views"]],
                 "the event-route views pass on shape"),
                ("stowedRef", lambda d: [v["stowedRef"].update(
                    {"realOnlyBlobPx": 0, "ghostOnlyBlobPx": 0, "realOnlyBlobPct": 0.0,
                     "ghostOnlyBlobPct": 0.0}) for v in d["views"]],
                 "stowedRef passes on shape in every view")):
            with self.subTest(label):
                d = copy.deepcopy(good)
                mutate(d)
                self.assertEqual((pp.CONTROLS_BAD, detail), pp.judge_state_control(d))
        # The manifest says ok, the sidecar says the press never arrived: INVALID(controls).
        bad = _state_case(pp.CONTROL_STATE_EVENTS_DROPPED, ghost=_stowed(),
                          proof={"reached": False})
        res, _ = self._eval(self._cases(), state_control=bad)
        self.assertEqual(pp.INVALID_CONTROLS_SUBKIND, res.invalid_subkind)
        self.assertIn("ctl-state-events-dropped:recorded-ok-judged-bad", res.problems[0])

    def test_family_less_press_faults_still_trip_the_press_gate(self):
        # Fix 1 of the PR #2766 review: a crashed / failed case whose sidecar carries no
        # stateProof has no family on either side, and must still be counted.
        cases = self._cases(n=4, failed=4)
        for doc, _ in cases:
            doc["stateProof"] = None
        _build_state_sweep(self.tmp, cases)
        sweep = _read_dir(self.tmp, "pp3")
        for row in sweep.manifest["cases"]:
            self.assertIsNone(row["family"])
        res = pp.evaluate_sweep(sweep, RUN_ID, True, gui_mirror.read_png,
                                lambda n: os.path.join(self.tmp, n))
        self.assertEqual(pp.INVALID_STATE_PRESS_SUBKIND, res.invalid_subkind)
        self.assertIn("family=%s 4/4" % pp.PRESS_UNKNOWN_FAMILY, res.problems[0])
        # Gating armed, the same sweep must never read PASS.
        self.assertNotEqual(pp.STATUS_PASS, res.status)

    def test_a_stacked_stowed_context_case_in_a_state_run(self):
        # The stowed-shaped case S3 writes in a state / context run: judged as a stowed
        # case, not counted by the press gate, accepted by every writer cross-check.
        res, _ = self._eval(self._cases(n=2) + [_stacked_stowed_case(
            "liquidengine2--default--stowed--stacked--00000099")],
            settings={"states": "none", "contexts": "stacked"})
        self.assertEqual(pp.STATUS_REPORT, res.status, res.problems)
        self.assertEqual([], res.recompute_mismatches)
        self.assertEqual((("deployable", 2, 0),), res.press.families)
        stacked = [c for c in res.cases if c.case_id.endswith("00000099")][0]
        self.assertEqual(pp.VERDICT_PASS, stacked.verdict)
        self.assertNotIn("state", stacked.to_dict())

    def test_an_ok_case_with_a_contradicting_press_proof_is_invalid_instrument(self):
        cases = self._cases(n=3)
        doc, masks = _state_case("bad--default--deployed--00000050", part="bad",
                                 real=_stowed(), ghost=_stowed(), start=_stowed(),
                                 proof={"reached": False, "events": [], "ghostEvent": None,
                                        "ghostStart": None})
        res, _ = self._eval(cases + [(doc, masks)])
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        self.assertIn("bad--default--deployed--00000050 press-proof: setup=ok action=Extend "
                      "actionResult=invoked reached=False", res.recompute_mismatches)
        self.assertEqual(1, res.counts[pp.VERDICT_ERROR])

    def test_the_writer_cross_checks_are_invalid_instrument(self):
        # Fix 4 of the PR #2766 review.
        cases = self._cases(n=60)
        bad = cases[5][0]["caseId"]
        _build_state_sweep(self.tmp, cases)
        base = _read_dir(self.tmp, "pp3")

        def run(mutate):
            sweep = copy.deepcopy(base)
            mutate(sweep)
            return pp.evaluate_sweep(sweep, RUN_ID, False, gui_mirror.read_png,
                                     lambda n: os.path.join(self.tmp, n))
        self.assertEqual(pp.STATUS_REPORT, run(lambda s: None).status)
        for label, mutate, needle in (
                ("sidecar state dropped",
                 lambda s: s.sidecars[bad].__setitem__("state", None),
                 "%s row state/context=deployed/alone sidecar=None/alone" % bad),
                ("row state dropped",
                 lambda s: s.manifest["cases"][5].__setitem__("state", None),
                 "%s row state/context=None/alone sidecar=deployed/alone" % bad),
                ("context differs",
                 lambda s: s.sidecars[bad].__setitem__("context", "stacked"),
                 "%s row state/context=deployed/alone sidecar=deployed/stacked" % bad),
                ("both states stowed, a press recorded",
                 lambda s: (s.sidecars[bad].__setitem__("state", "stowed"),
                            s.manifest["cases"][5].__setitem__("state", "stowed")),
                 "%s stateProof.action=Extend on a case whose state is stowed" % bad),
                ("proof state differs (re-review note 2)",
                 lambda s: s.sidecars[bad]["stateProof"].__setitem__("state", "open"),
                 "%s stateProof.state=open sidecar state=deployed" % bad),
                ("settings dropped", lambda s: s.manifest.__setitem__("settings", {}),
                 "recompute the stowed control set"),
                ("settings say a stowed run",
                 lambda s: s.manifest.__setitem__("settings", {"states": "none",
                                                               "contexts": "none"}),
                 "states=none contexts=none recompute the stowed control set")):
            with self.subTest(label):
                res = run(mutate)
                self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind,
                                 (res.invalid_subkind, res.problems))
                self.assertTrue(any(needle in m for m in res.recompute_mismatches),
                                res.recompute_mismatches)

    def test_state_flag_counts(self):
        a = pp.judge_case(_state_case("a--d--deployed--1", proof={"ghostEvent": "no-info",
                                                                  "ghostStart": "no-info"})[0])
        b = pp.judge_case(_state_case("b--d--deployed--2", stowed=_deployed())[0])
        self.assertEqual({"state-invisible": 1, "state-not-modelled": 1},
                         pp.state_flag_counts([a, b]))


class StatePressWiringTests(unittest.TestCase):
    """The new INVALID class through hlib.classify_verdict."""

    def test_state_press_is_a_terminal_invalid(self):
        self.assertIn(pp.INVALID_STATE_PRESS_SUBKIND, hlib.PARTPARITY_INVALID_SUBKINDS)
        self.assertEqual(set(hlib.PARTPARITY_INVALID_SUBKINDS),
                         set(hlib.PARTPARITY_INVALID_SUBKIND_ORDER))
        self.assertEqual(list(hlib.PARTPARITY_INVALID_SUBKIND_ORDER),
                         sorted(pp._SUBKIND_ORDER, key=pp._SUBKIND_ORDER.get))
        v = hlib.classify_verdict({}, {"part_parity_invalid": True,
                                       "part_parity_subkind": "parity-state-press",
                                       "part_parity_reason": "family=light"}, {}, 1, "once")
        self.assertEqual((hlib.VERDICT_INVALID, "parity-state-press"), (v.verdict, v.subkind))
        self.assertFalse(hlib.should_retry(v, 1, "once"))


if __name__ == "__main__":
    unittest.main()
