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

import contextlib
import copy
import gzip
import json
import math
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

    def test_a_part_with_a_skipped_depth_mask_is_judged_on_its_pictures(self):
        # The capture repairs the real mask (design 4.2 step 0), so a depth-mask part has no
        # cap: its skipped mask is not a missing mesh, and a picture fail stays a fail.
        side = copy.deepcopy(_load("clydesdale--default--89327fba.part.json"))
        side["structure"]["missingOnGhost"] = [
            {"key": "a/RCSMask", "mesh": "", "rendered": True, "nonVisual": "depth-mask"}]
        self.assertEqual(pp.VERDICT_PASS, pp.judge_case(side).verdict)
        side["views"][0]["colorBlobPx"] = 5000
        c = pp.judge_case(side)
        self.assertEqual(pp.VERDICT_FAIL, c.verdict)
        self.assertFalse(any(r.startswith("depth-mask") for r in c.reasons), c.reasons)
        side["structure"]["missingOnGhost"] = [{"key": "a/b/RCSMask", "mesh": "", "rendered": True}]
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(side).verdict, "pre-key sidecar: no cap either")

    def test_an_unrepaired_depth_mask_is_a_setup_fault_not_a_ghost_fail(self):
        # The repair could not fix a real mask: the real picture is not the player's part, so
        # the case is an error (setup fault), never a ghost fail, and it is a complete record.
        side = copy.deepcopy(_load("clydesdale--default--89327fba.part.json"))
        side["views"][0]["colorBlobPx"] = 5000
        side["flags"] = list(side.get("flags") or []) + ["depth-mask-repaired:1", "depth-mask-unrepaired:1"]
        side["setup"] = {"status": pp.SETUP_DEPTH_MASK_UNREPAIRED,
                         "detail": "depth-mask m3=ModuleDepthMask:unsupported:members-missing"}
        self.assertTrue(pp.is_setup_fault(pp.SETUP_DEPTH_MASK_UNREPAIRED))
        c = pp.judge_case(side)
        self.assertEqual(pp.VERDICT_ERROR, c.verdict)
        self.assertEqual("setup:depth-mask-unrepaired", c.reasons[0])
        self.assertIsNone(pp._sidecar_complete(side, side["caseId"]))

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
                           ("TransformDiffKeys", pp.TRANSFORM_DIFF_KEYS),
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
        self.assertEqual(list(pp.ATLAS_ROWS_STATE_RELOADED),
                         self._string_array(text, "ReloadedStateAtlasRows"))
        self.assertIn("isStateCase && startReloaded ? (string[])ReloadedStateAtlasRows", text)
        self.assertEqual(pp.FLAG_START_RELOADED_PREFIX, self._string_consts(
            self._source("PartParityRampHold.cs"))["FlagReloadedPrefix"])
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
        self.assertEqual(list(pp.FAIRING_CONTEXTS), self._ident_array(text, "FairingContexts", consts))
        self.assertEqual((pp.CONTEXT_FGV, pp.CONTEXT_FGV_PREFIX),
                         (consts["ContextFgv"], consts["ContextFgvPrefix"]))
        self.assertTrue(all(pp.context_class(c) == pp.CONTEXT_FGV for c in pp.FAIRING_CONTEXTS))
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

    # ----- P8 F1: the FX contract (design Appendix E) -----

    def _fx_consts(self):
        """The string consts of PartParityFxRules.cs, a ``= PartParityStateRules.X`` alias
        resolved through that file, plus the sweep's and the state rules' own (by name)."""
        fx_text = self._source("PartParityFxRules.cs")
        rules = self._string_consts(self._source("PartParityStateRules.cs"))
        sweep = self._string_consts(self._source("TestCommandPartParitySweep.cs"))
        consts = self._string_consts(fx_text)
        for name, ref in re.findall(
                r"internal const string (\w+) = PartParityStateRules\.(\w+);", fx_text):
            consts[name] = rules[ref]
        return fx_text, consts, rules, sweep

    @staticmethod
    def _qualified_array(text, name, own, others):
        """The members of one array initializer of const identifiers, each either the file's
        own (``X``) or another type's (``Type.X``, looked up in ``others[Type]``)."""
        m = re.search(r"\b%s\s*=\s*\{(.*?)\};" % re.escape(name), text, flags=re.S)
        if m is None:
            raise AssertionError("array %s not found" % name)
        out = []
        for owner, ident in re.findall(r"(?:\b(\w+)\.)?\b([A-Za-z_]\w*)\b", m.group(1)):
            if owner:
                out.append(others[owner][ident])
            elif ident not in others:
                out.append(own[ident])
        return out

    @staticmethod
    def _num_const(text, name):
        m = re.search(r"internal const (?:int|double) %s = ([0-9.]+);" % re.escape(name), text)
        if m is None:
            raise AssertionError("numeric const %s not found" % name)
        return float(m.group(1))

    def test_the_fx_rules_tables(self):
        text, consts, rules, sweep = self._fx_consts()
        others = {"PartParityStateRules": rules, "TestCommandPartParitySweep": sweep}
        for cs, mirror in (("Families", pp.FX_FAMILIES),
                           ("StochasticFamilies", pp.FX_STOCHASTIC_FAMILIES),
                           ("ArgWords", pp.FX_ARG_WORDS),
                           ("ArgKnownWords", pp.FX_ARG_KNOWN_WORDS), ("States", pp.FX_STATES),
                           ("OrbitContexts", pp.FX_ORBIT_CONTEXTS),
                           ("ExclusionReasons", pp.FX_EXCLUSION_REASONS),
                           ("Views", pp.FX_VIEWS), ("RendererLayers", pp.FX_RENDERER_LAYERS),
                           ("FxLayers", pp.FX_LAYERS), ("Sides", pp.FX_SIDES),
                           ("Measures", pp.FX_MEASURES),
                           ("RcsReportOnlyMeasures", pp.FX_RCS_REPORT_ONLY_MEASURES)):
            with self.subTest(table=cs):
                self.assertEqual(list(mirror), self._qualified_array(text, cs, consts, others))
        for name, mirror in (("ArgAllToken", pp.FX_ARG_ALL), ("ContextPlate", pp.CONTEXT_PLATE),
                             ("ReferenceWaterfall", pp.REFERENCE_WATERFALL),
                             ("PlateauSignalParticles", pp.PLATEAU_SIGNAL_PARTICLES),
                             ("PlateauSignalCoverage", pp.PLATEAU_SIGNAL_COVERAGE),
                             ("ReasonFxMissing", pp.FX_REASON_FX_MISSING),
                             ("ReasonFxExtra", pp.FX_REASON_FX_EXTRA),
                             ("ReasonSmokeMissing", pp.FX_REASON_SMOKE_MISSING),
                             ("ReasonSmokeExtra", pp.FX_REASON_SMOKE_EXTRA),
                             ("TagFxNotPlateauedPrefix", pp.TAG_FX_NOT_PLATEAUED_PREFIX),
                             ("CapFxUnsettled", pp.CAP_FX_UNSETTLED),
                             ("FlagFxFramingClamped", pp.FLAG_FX_FRAMING_CLAMPED),
                             ("FlagRcsKnownDifferencePrefix", pp.FLAG_RCS_KNOWN_DIFFERENCE_PREFIX)):
            with self.subTest(const=name):
                self.assertEqual(mirror, consts[name])
        # FamilyForArg's switch, case by case: plate selects the lamps.
        body = text[text.index("internal static string FamilyForArg("):]
        body = body[:body.index("default: return null;")]
        pairs = re.findall(r"case (\w+): return (\w+);", body)
        self.assertEqual(len(pp.FX_ARG_KNOWN_WORDS), len(pairs))
        self.assertEqual(pp.FX_MAIN_STATES, (consts["StateMainFull"], consts["StateMainLow"]))
        self.assertEqual({"main-full": self._num_const(text, "BurnFullThrottle"),
                          "main-low": self._num_const(text, "BurnLowThrottle")}, pp.FX_MAIN_THROTTLES)
        self.assertIn("PlumeOption(m, StateMainFull, BurnFullThrottle, ", text)
        self.assertIn("PlumeOption(m, StateMainLow, BurnLowThrottle, ", text)
        for arg, family in pairs:
            with self.subTest(arg=arg):
                self.assertEqual(consts[family], pp.fx_family_for_arg(consts[arg]))
        self.assertIsNone(pp.fx_family_for_arg("light"))

    def test_the_fx_measure_constants(self):
        text = self._source("PartParityFxRules.cs")
        for name, mirror in (("Samples", pp.FX_SAMPLES), ("MaxSamples", pp.FX_MAX_SAMPLES),
                             ("CoreFraction", pp.FX_CORE_FRACTION),
                             ("AlphaCovered", pp.FX_ALPHA_COVERED),
                             ("FlameValueMin", pp.FX_FLAME_VALUE_MIN),
                             ("SaturatedValue", pp.FX_SATURATED_VALUE),
                             ("HueBins", pp.FX_HUE_BINS),
                             ("PresenceMinPx", pp.FX_PRESENCE_MIN_PX),
                             ("SmokeMinPx", pp.FX_SMOKE_MIN_PX),
                             ("SplitHalfPct", pp.FX_SPLIT_HALF_PCT),
                             ("PlateLitMinPct", pp.PLATE_LIT_MIN_PCT),
                             ("ThrottleDoneTolerance", pp.FX_THROTTLE_DONE_TOLERANCE),
                             ("PlateLitMinLum", pp.PLATE_LIT_MIN_LUM),
                             ("StripFrames", pp.FX_STRIP_FRAMES)):
            with self.subTest(const=name):
                self.assertEqual(float(mirror), self._num_const(text, name))
        self.assertEqual(pp.FX_LUMINANCE_WEIGHTS,
                         tuple(self._num_const(text, n) for n in ("LuminanceR", "LuminanceG", "LuminanceB")))
        for key, value in pp.FX_THRESHOLDS.items():
            with self.subTest(threshold=key):
                self.assertEqual(value, self._num_const(text, key[0].upper() + key[1:]))
        # Every threshold const of the rules has its mirror (none added on one side only).
        cs_thresholds = re.findall(
            r"internal const double ((?:Origin|Lateral|Offset|Length|Width|Direction|CrossSection|"
            r"Chroma|HueL1|Colour|Brightness|CoreIou)\w*) =", text)
        self.assertEqual(sorted(k[0].upper() + k[1:] for k in pp.FX_THRESHOLDS), sorted(cs_thresholds))
        # The 8-bit tile rule the recompute transcribes, by its code shape.
        self.assertIn("n <= 0 || k <= 0 ? 0 : (510 * k + n) / (2 * n);", text)
        self.assertIn("n <= 0 || alpha <= 0 ? 0 : (2 * alpha * n + 255) / 510;", text)
        self.assertIn("k <= 0 ? 0 : (int)((2 * sum + k) / (2L * k));", text)
        self.assertIn("internal static bool IsCore(int k, int n) => n > 0 && k > 0 && 2 * k >= n;", text)
        self.assertIn("? Math.Max(r, Math.Max(g, b)) >= FlameValueMin", text)
        self.assertIn(": a >= AlphaCovered;", text)
        self.assertIn("r >= SaturatedValue || g >= SaturatedValue || b >= SaturatedValue;", text)
        self.assertIn("LuminanceR * r + LuminanceG * g + LuminanceB * b;", text)
        for n in (1, 3, 16, 255):
            for k in range(n + 1):
                self.assertEqual(k, pp.fx_covered_samples(pp.fx_tile_alpha(k, n), n))

    def test_the_fx_weak_core_rule_is_the_csharp_one(self):
        text = self._source("PartParityFxMetrics.cs")
        self.assertIn("=> realCorePx < PartParityFxRules.PresenceMinPx && "
                      "ghostCorePx < PartParityFxRules.PresenceMinPx;", text)
        # It returns before any other measure is read, after presence was added.
        body = text[text.index("internal static List<string> FailingMeasures(string layer"):]
        body = body[:body.index("internal static string PresenceReason(")]
        self.assertLess(body.index("PresenceReason(layer, real.CorePx, ghost.CorePx)"),
                        body.index("if (BothCoresWeak(real.CorePx, ghost.CorePx)) return"))
        self.assertLess(body.index("if (BothCoresWeak("), body.index("c.OriginDeltaPx"))
        # The entry's convergence follows it, on the whole-set cores.
        body = text[text.index("internal string SplitHalfDisagreement()"):]
        body = body[:body.index("internal List<string> FailingMeasures(bool rcsCase)")]
        self.assertLess(body.index("PartParityFxMetrics.BothCoresWeak(Real.CorePx, Ghost.CorePx)) return null;"),
                        body.index("RealEven, RealOdd"))

    def test_the_fx_sweep_contract(self):
        text = self._source("TestCommandPartParitySweep.cs")
        consts = self._string_consts(text)
        self.assertEqual(list(pp.CONTROL_IDS_FX_ORBIT), self._ident_array(text, "FxOrbitControlIds", consts))
        self.assertEqual(list(pp.CONTROL_IDS_FX_AIR), self._ident_array(text, "FxAirControlIds", consts))
        self.assertEqual(list(pp.OPTIONAL_CONTROL_IDS), self._ident_array(text, "OptionalControlIds", consts))
        self.assertEqual((pp.SETUP_PLATE_NOT_LIT, pp.SETUP_RCS_INPUT_MISMATCH),
                         (consts["SetupPlateNotLit"], consts["SetupRcsInputMismatch"]))
        self.assertEqual(pp.CONTROLS_NOT_APPLICABLE, consts["ControlsNotApplicable"])
        self.assertEqual(pp.SETTING_FX, consts["SettingFx"])
        self.assertEqual(pp.FX_ATLAS_SUFFIX, consts["FxAtlasSuffix"])
        self.assertEqual(pp.STRIP_SUFFIX, consts["StripSuffix"])
        # ControlSetFor reads fx= first; IsFxRun is fx= present; StateSettings adds fx only
        # for an FX run (so a P6 run's settings are what they were). control_set mirrors it.
        body = text[text.index("internal static string[] ControlSetFor("):]
        body = body[:body.index("private static bool IsIdChar(")]
        fx_line = "if (IsFxRun(args)) return (string[])(IsAirRun(args) ? FxAirControlIds : FxOrbitControlIds).Clone();"
        self.assertIn(fx_line, body)
        self.assertLess(body.index(fx_line), body.index("if (!IsStateRun(args))"))
        self.assertIn("internal static bool IsFxRun(PartParitySweepArgs args) => args?.Fx != null;", text)
        self.assertIn("if (IsFxRun(args)) settings.Add(new KeyValuePair<string, string>(SettingFx, FormatFx(args)));",
                      text)
        self.assertIn("if (o == ControlsOk || o == ControlsNotApplicable) ok++;", text)
        self.assertEqual(pp.CONTROL_IDS_FX_ORBIT, pp.control_set({"fx": "all"}))
        self.assertEqual(pp.CONTROL_IDS_FX_AIR, pp.control_set({"fx": "plume", "contexts": "air"}))

    def test_the_fx_json_tables(self):
        text = self._source("PartParityJson.cs")
        for cs, mirror in (("FxKeys", pp.FX_KEYS), ("FxDriveKeys", pp.FX_DRIVE_KEYS),
                           ("FxSideKeys", pp.FX_SIDE_KEYS), ("FxPlateauKeys", pp.FX_PLATEAU_KEYS),
                           ("FxHoldKeys", pp.FX_HOLD_KEYS), ("FxRcsKeys", pp.FX_RCS_KEYS),
                           ("FxFramingKeys", pp.FX_FRAMING_KEYS), ("FxLayerKeys", pp.FX_LAYER_KEYS),
                           ("FxViewKeys", pp.FX_VIEW_KEYS), ("FxAxisKeys", pp.FX_AXIS_KEYS),
                           ("FxSideMeasureKeys", pp.FX_SIDE_MEASURE_KEYS),
                           ("FxCompareKeys", pp.FX_COMPARE_KEYS),
                           ("FxSplitHalfKeys", pp.FX_SPLIT_HALF_KEYS),
                           ("FxHalfKeys", pp.FX_HALF_KEYS), ("FxParticleKeys", pp.FX_PARTICLE_KEYS),
                           ("FxStructureKeys", pp.FX_STRUCTURE_KEYS),
                           ("FxPlateKeys", pp.FX_PLATE_KEYS), ("FxAtlasKeys", pp.FX_ATLAS_KEYS),
                           ("FxStripKeys", pp.FX_STRIP_KEYS),
                           ("FxAtlasRows", pp.FX_ATLAS_ROWS), ("RcsFxAtlasRows", pp.FX_ATLAS_ROWS_RCS),
                           ("StripRows", pp.STRIP_ROWS)):
            with self.subTest(table=cs):
                self.assertEqual(list(mirror), self._string_array(text, cs))
        self.assertIn("isRcsCase ? RcsFxAtlasRows : FxAtlasRows", text)
        self.assertEqual(pp.FX_ATLAS_ROWS_RCS, pp.fx_atlas_rows_for(True))
        # The writer emits the three FX blocks through their own key tables.
        for key, table in (("fx", "FxKeys"), ("fxAtlas", "FxAtlasKeys"), ("strip", "FxStripKeys")):
            self.assertRegex(text, r'M\("%s", KeyedBlock\(%s, c\.\w+\)\)' % (key, table))

    def test_the_fx_words_live_only_in_the_fx_rules(self):
        # P6 does not change (design E.1): no FX family, word, context or reason in the state
        # rules, and P6's tables are what PP-3 to PP-6 plan today.
        rules = self._source("PartParityStateRules.cs")
        for word in ("plume", "plate", "rcs-fire", "burn-full", "burn-low", "heat-medium",
                     "heat-hot") + pp.FX_MAIN_STATES + pp.FX_EXCLUSION_REASONS:
            with self.subTest(word=word):
                self.assertNotIn('"%s"' % word, rules)
        self.assertEqual(("alone", "stacked", "fgv"), pp.ORBIT_CONTEXTS)
        self.assertEqual(("alone", "stacked", "air", "fgv"), pp.KNOWN_CONTEXTS)
        self.assertEqual(12, len(pp.FAMILIES))
        self.assertIn("p8-heat", pp.STATE_EXCLUSION_REASONS)
        self.assertIn("p8-fx", pp.STATE_EXCLUSION_REASONS)
        self.assertTrue(set(pp.FX_FAMILIES) - {"light"} <= set(pp.FX_FAMILIES) - set(pp.FAMILIES))

    def test_with_fx_none_the_p6_lanes_keep_their_control_sets(self):
        # The run-step settings PP-1 to PP-6 write (harness/scenarios/PP-*.toml), with no fx
        # key (a P6 build) and with fx=none: the same control set either way.
        lanes = ((("none", "none"), pp.CONTROL_IDS), (("all", "none"), pp.CONTROL_IDS_ORBIT_STATE),
                 (("all", "air"), pp.CONTROL_IDS_AIR))
        for (states, contexts), want in lanes:
            for extra in ({}, {"fx": "none"}):
                settings = dict({"states": states, "contexts": contexts}, **extra)
                with self.subTest(settings=settings):
                    self.assertEqual(want, pp.control_set(settings))
                    self.assertFalse(pp.is_fx_run(settings))
        self.assertEqual(8, len(pp.CONTROL_IDS))
        self.assertEqual(pp.CONTROL_IDS + ("ctl-state-events-dropped",), pp.CONTROL_IDS_ORBIT_STATE)


class PartParityFxReaderTests(unittest.TestCase):
    """The P8 F1 readers (design E.5): an FX manifest's control set, the FX atlas rows, the
    new setup statuses, the 8-bit tile helpers."""

    def test_control_set_reads_fx(self):
        self.assertEqual(pp.CONTROL_IDS_FX_ORBIT, pp.control_set({"fx": "plume,rcs"}))
        self.assertEqual(pp.CONTROL_IDS_FX_AIR, pp.control_set({"fx": "all", "contexts": "air"}))
        self.assertEqual(pp.CONTROL_IDS, pp.control_set({"fx": "none"}))
        self.assertEqual(11, len(pp.CONTROL_IDS_FX_ORBIT))
        self.assertTrue(set(pp.CONTROL_IDS_FX_ORBIT) | set(pp.CONTROL_IDS_FX_AIR) <= set(pp.ALL_CONTROL_IDS))
        self.assertTrue(set(pp.OPTIONAL_CONTROL_IDS) <= set(pp.CONTROL_IDS_FX_ORBIT))

    def test_a_written_fx_control_set_is_cross_checked(self):
        ok = {"settings": {"fx": "all", "controlSet": ",".join(pp.CONTROL_IDS_FX_ORBIT)}}
        self.assertIsNone(pp.control_set_disagreement(ok))
        stale = {"settings": {"fx": "all", "controlSet": ",".join(pp.CONTROL_IDS)}}
        self.assertIn("recomputed=ctl-empty,ctl-ghost-absent,ctl-fx-real-vs-real", pp.control_set_disagreement(stale))

    def test_the_new_setup_statuses_are_faults(self):
        for status in (pp.SETUP_PLATE_NOT_LIT, pp.SETUP_RCS_INPUT_MISMATCH):
            self.assertIn(status, pp.SETUP_STATUSES)
            self.assertTrue(pp.is_setup_fault(status))

    def test_the_tile_helpers(self):
        self.assertEqual(16, pp.fx_tile_alpha(1, 16))
        self.assertEqual(255, pp.fx_tile_alpha(16, 16))
        self.assertEqual(4, pp.fx_tile_channel(7, 2))
        self.assertEqual(3, pp.fx_tile_channel(5, 2))
        self.assertTrue(pp.fx_is_core(8, 16))
        self.assertFalse(pp.fx_is_core(7, 16))
        self.assertTrue(pp.fx_sample_covered("flame", 0, 16, 0, 0))
        self.assertFalse(pp.fx_sample_covered("smoke", 255, 255, 255, 127))
        self.assertTrue(pp.fx_is_saturated(0, 255, 0))
        self.assertAlmostEqual(255.0, pp.fx_luminance(255, 255, 255), places=9)
        self.assertEqual("event:fx-far:smoke:smoke-missing",
                         pp.fx_reason("event", "fx-far", "smoke", "smoke-missing"))
        self.assertEqual(("real-flame", "ghost-flame", "ghost-start-flame", "real-smoke",
                          "ghost-smoke", "ghost-start-smoke"), pp.fx_atlas_rows_for(False))


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
        self.assertEqual(pp.atlas_rows_for(True) + ("real-start",), pp.atlas_rows_for(True, True))
        self.assertEqual(("real", "ghost"), pp.atlas_rows_for(False, True))
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

    def test_the_context_cap_applies_per_route_and_a_depth_mask_caps_nothing(self):
        doc, _ = _state_case("gear--default--deployed--1", family="gear", ghost=_shifted(),
                             start=_shifted(), tags=(pp.TAG_CONTEXT_DEPENDENT,))
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_WARN, cv.verdict)
        self.assertIn("event:" + pp.REASON_CONTEXT_POSE, cv.reasons)
        self.assertIn("start:" + pp.REASON_CONTEXT_POSE, cv.reasons)
        # The depth-mask picture cap is gone (design 4.2 step 0: the capture repairs the real
        # mask), so a start-route picture fail beside a skipped mask stays a fail; the mask entry
        # itself is non-visual and adds no missing-rendered reason.
        mask = [{"key": "a/mask", "mesh": "m", "rendered": True, "nonVisual": "depth-mask"}]
        doc, _ = _state_case("bay--default--open--1", start=_shifted(),
                             structure_start={"missingOnGhost": mask, "extraOnGhost": []})
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_FAIL, cv.verdict)
        self.assertFalse(any("depth-mask" in r or "missing-rendered" in r for r in cv.reasons))

    def _air(self, rot=2.5, pos=0.0, scl=0.0, context="air", tilt=1.0, wobble=1.0,
             transform="canopy", tags=(), key="chute/canopy", arm=None, **extra):
        entry = {"key": key, "positionDeltaM": pos, "rotationDeltaDeg": rot, "scaleDeltaPct": scl}
        if arm != "absent":
            entry["canopyPivotDistanceM"] = arm
        structure = {"missingOnGhost": [], "extraOnGhost": [], "meshDiffers": [],
                     "renderedDiffers": [], "materialDiffers": [],
                     "transformDiffers": [entry]}
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
                          ("no transform difference and no measured turn",
                           {"transformDiffers": [], "tilt": 0.03, "wobble": 0.05})):
            with self.subTest(label):
                self.assertEqual(pp.VERDICT_FAIL, self._air(**kw).verdict)

    def test_the_canopy_cap_allows_the_arc_of_its_rotation_at_the_lever_arm(self):
        # PARTPARITY-CANOPY-CAP-LEVER-ARM shape 1 (ReStock): a renderer metres from the canopy
        # pivot moves with the turn. The entry turned 2.5 deg (+ the 0.5 deg tolerance = 3,
        # inside the bound 1 + 1 + 1 = 3); at d = 5 m the chord is 2 * 5 * sin(1.5 deg) =
        # 0.2617 m, plus the 1 mm tolerance.
        allowance = pp.canopy_position_allowance_m(
            {"canopyPivotDistanceM": 5.0, "rotationDeltaDeg": 2.5}, 3.0)
        self.assertAlmostEqual(0.001 + 10.0 * math.sin(math.radians(1.5)), allowance, places=9)
        # The turn never exceeds the cap's bound, and a canopy that did not turn gets the
        # tolerance's arc only.
        self.assertAlmostEqual(allowance, pp.canopy_position_allowance_m(
            {"canopyPivotDistanceM": 5.0, "rotationDeltaDeg": 40.0}, 3.0), places=12)
        self.assertAlmostEqual(0.001 + 10.0 * math.sin(math.radians(0.25)), pp.canopy_position_allowance_m(
            {"canopyPivotDistanceM": 5.0, "rotationDeltaDeg": 0.0}, 3.0), places=12)
        self.assertEqual(pp.VERDICT_WARN, self._air(pos=0.25, arm=5.0).verdict)
        self.assertEqual(pp.VERDICT_WARN, self._air(pos=allowance - 1e-6, arm=5.0).verdict)
        for label, kw in (
                ("just past the arc", {"pos": allowance + 1e-6, "arm": 5.0}),
                ("the arc of a short arm", {"pos": 0.25, "arm": 0.5}),
                ("no lever arm: the tolerance alone", {"pos": 0.25, "arm": None}),
                ("an old sidecar without the key", {"pos": 0.25, "arm": "absent"}),
                ("a negative arm", {"pos": 0.25, "arm": -5.0}),
                ("the arm does not lift the rotation bound", {"pos": 0.0, "rot": 3.5, "arm": 5.0}),
                ("the arm does not excuse a scale delta", {"pos": 0.1, "scl": 5.0, "arm": 5.0}),
                ("the arm does not excuse a mesh difference",
                 {"pos": 0.1, "arm": 5.0, "meshDiffers": [{"key": "chute/canopy"}]}),
                ("an arm on a non-canopy renderer", {"pos": 0.1, "arm": 5.0, "key": "chute/housing"}),
                ("a canopy moved without turning", {"pos": 0.25, "rot": 0.0, "arm": 5.0}),
                ("moved further than its own small turn sweeps", {"pos": 0.25, "rot": 0.6, "arm": 5.0})):
            with self.subTest(label):
                self.assertEqual(pp.VERDICT_FAIL, self._air(**kw).verdict)

    def test_a_clean_structure_is_a_sub_tolerance_canopy_turn(self):
        # PARTPARITY-CANOPY-CAP-LEVER-ARM shape 2 (stock): the turn stayed under the
        # structure's 0.5 deg rotation tolerance, so no entry exists; a measured turn of at
        # least CANOPY_MIN_TURN_DEG explains the picture, a canopy that did not turn does not,
        # and the cap's other conditions still hold.
        self.assertEqual(pp.VERDICT_WARN,
                         self._air(transformDiffers=[], tilt=0.08, wobble=0.49).verdict)
        self.assertEqual(pp.VERDICT_WARN,
                         self._air(transformDiffers=[], tilt=0.0, wobble=pp.CANOPY_MIN_TURN_DEG).verdict)
        for label, kw in (
                ("under the floor", {"tilt": 0.0, "wobble": 0.099}),
                ("a material difference", {"materialDiffers": [
                    {"key": "chute/canopy", "field": "mainTextures", "real": "a", "ghost": "b"}]}),
                ("a rendered difference", {"renderedDiffers": [
                    {"key": "chute/canopy", "field": "rendered", "real": "true", "ghost": "false"}]}),
                ("a missing rendered mesh", {"missingOnGhost": [
                    {"key": "chute/canopy", "mesh": "m", "rendered": True, "nonVisual": None}]}),
                ("a timeout capture", {"tags": ("canopy-wobble:3.1",)}),
                ("not an air case", {"context": "alone"})):
            with self.subTest(label):
                kw = dict(kw)
                kw.setdefault("tilt", 0.1)
                kw.setdefault("wobble", 0.4)
                self.assertEqual(pp.VERDICT_FAIL, self._air(transformDiffers=[], **kw).verdict)
        # No structure measured at all (a start ghost that was not built writes
        # structureStart null) is not a clean structure: never capped.
        cap = pp.CanopyCap(3.0, "canopy", 2.0)
        self.assertFalse(pp._canopy_cap_applies({}, cap))
        self.assertFalse(pp._canopy_cap_applies({"transformDiffers": None}, cap))
        self.assertTrue(pp._canopy_cap_applies({"transformDiffers": []}, cap))
        doc, _ = _state_case("chute--default--full--air--1", part="parachuteSingle",
                             family="parachute", state="full", context="air",
                             ghost=_shifted(), start=_shifted(), structure=None,
                             structure_start=None, flags=("ghost-not-built:start",),
                             proof={"canopy": {"tiltDeg": 1.0, "wobbleDeg": 1.0,
                                               "transform": "canopy"}})
        doc["structure"] = None
        doc["structureStart"] = None
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_FAIL, cv.verdict)
        self.assertFalse(any(pp.REASON_CANOPY_AIRFLOW in r for r in cv.reasons))

    def test_the_structure_tolerances_are_the_csharp_ones(self):
        path = os.path.join(CS_DIR, "PartParityStructure.cs")
        with open(path, encoding="utf-8") as fh:
            text = _strip_cs_comments(fh.read())
        for name, mirror in (("PositionToleranceM", pp.STRUCTURE_POSITION_TOLERANCE_M),
                             ("RotationToleranceDeg", pp.STRUCTURE_ROTATION_TOLERANCE_DEG),
                             ("ScaleTolerancePct", pp.STRUCTURE_SCALE_TOLERANCE_PCT)):
            with self.subTest(name):
                m = re.search(r"const double %s\s*=\s*([0-9.]+)\s*;" % name, text)
                self.assertIsNotNone(m, "%s not found in %s" % (name, path))
                self.assertEqual(float(m.group(1)), mirror)
        # The floor sits under the tolerance: a clean structure means every canopy renderer
        # turned less than the tolerance, so a floor over it could never be met.
        self.assertLess(pp.CANOPY_MIN_TURN_DEG, pp.STRUCTURE_ROTATION_TOLERANCE_DEG)
        # The C# key test splits the same way: a path segment equal to the canopy name, the
        # #k suffix dropped (PartParityStructure.IsCanopyRendererKey).
        self.assertIn("IndexOf('#')", text)
        self.assertIn("Split('/')", text)
        self.assertIn('M("canopyPivotDistanceM", Num(t.CanopyPivotDistanceM))',
                      self._json_source())

    def _json_source(self):
        with open(os.path.join(CS_DIR, "PartParityJson.cs"), encoding="utf-8") as fh:
            return fh.read()

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
        # Re-review note 1: the no-press exception is the stowed context shape only (the
        # stacked shroud, and since S8 FGV's intact cases, design D.1).
        for label, mutate in (
                ("context alone", lambda d: d.__setitem__("context", "alone")),
                ("context air", lambda d: d.__setitem__("context", "air")),
                ("context fgv word", lambda d: d.__setitem__("context", "fgvx-1")),
                ("not reached", lambda d: d["stateProof"].__setitem__("reached", False)),
                ("reached null", lambda d: d["stateProof"].__setitem__("reached", None))):
            with self.subTest(label):
                doc, _ = _stacked_stowed_case("liquidengine2--default--stowed--stacked--1")
                mutate(doc)
                self.assertIn("only a reached stowed context case", pp.press_proof_problem(doc))
                self.assertEqual(pp.VERDICT_ERROR, pp.judge_case(doc).verdict)

    def test_a_reached_fgv_intact_case_may_skip_the_press(self):
        for context in pp.FAIRING_CONTEXTS:
            with self.subTest(context):
                doc, _ = _stacked_stowed_case("fairingsize1--default--stowed--x--1")
                doc["context"] = context
                self.assertIsNone(pp.press_proof_problem(doc))

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


def _reloaded_case(case_id="engine--default--burn-full--0000aaaa", start=None, real_start=None, **kw):
    """A state case whose start route was compared with the loaded real part: the fifth atlas
    row, the flag, and every ``start`` block measured against that row."""
    start = start or _stowed()
    real_start = real_start or start
    doc, masks = _state_case(case_id, start=start, **kw)
    for v in doc["views"]:
        rec = fx.view_record(v["view"], real_start, start, STILE)
        v["start"] = {k: rec[k] for k in pp.START_KEYS}
    doc["atlas"]["rows"] = list(pp.ATLAS_ROWS_STATE_RELOADED)
    doc["flags"] = list(doc["flags"]) + [pp.FLAG_START_RELOADED_PREFIX + "1"]
    return doc, masks + (real_start,)


class StartReloadTests(unittest.TestCase):
    """E.4 "Start-route reload": the start route against the real part as a load leaves it."""

    def _recompute(self, doc, masks):
        n = len(doc["views"])
        rows = [[m] * n for m in masks]
        return pp.recompute_case(doc, (n * STILE, len(rows) * STILE, 4, _atlas_rows(rows)))

    def test_the_start_route_is_judged_and_recomputed_against_the_loaded_row(self):
        # The ghost's start pose differs from the burning part and equals the loaded one.
        doc, masks = _reloaded_case()
        self.assertEqual([], self._recompute(doc, masks))
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_PASS, cv.verdict, cv.reasons)
        # The same start ghost against the burning part (no reload) fails: the row decides.
        plain, plain_masks = _state_case("engine--default--burn-full--0000aaaa", start=_stowed())
        self.assertEqual([], self._recompute(plain, plain_masks))
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(plain).verdict)

    def test_a_ghost_wrong_after_a_load_still_fails(self):
        doc, masks = _reloaded_case(start=_stowed(), real_start=_deployed())
        self.assertEqual([], self._recompute(doc, masks))
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)

    def test_a_start_block_measured_from_the_burning_row_is_a_mismatch(self):
        doc, masks = _reloaded_case()
        for v in doc["views"]:
            rec = fx.view_record(v["view"], _deployed(), _stowed(), STILE)
            v["start"] = {k: rec[k] for k in pp.START_KEYS}
        self.assertTrue(any("/start" in d for d in self._recompute(doc, masks)))

    def test_the_event_route_is_untouched_by_the_loaded_row(self):
        doc, masks = _reloaded_case(ghost=_stowed())
        self.assertEqual([], self._recompute(doc, masks))
        self.assertTrue(any(r.startswith("event:shape-blob")
                            for r in pp.judge_case(doc).reasons))

    def test_the_flag_and_the_row_go_together(self):
        doc, _masks = _reloaded_case()
        self.assertIsNone(pp.start_reload_problem(doc))
        bare = copy.deepcopy(doc)
        bare["flags"] = []
        self.assertIn("flag=false", pp.start_reload_problem(bare))
        four = copy.deepcopy(doc)
        four["atlas"]["rows"] = list(pp.ATLAS_ROWS_STATE)
        self.assertIn("flag=true", pp.start_reload_problem(four))
        stowed = copy.deepcopy(doc)
        stowed["state"], stowed["context"] = "stowed", "alone"
        self.assertIsNotNone(pp.start_reload_problem(stowed))
        # It is checked on every case of a sweep, sampled or not, through the view-block walk.
        self.assertIn(pp.start_reload_problem(bare), pp.state_view_block_problems(bare))
        self.assertIn(pp.start_reload_problem(stowed), pp.state_view_block_problems(stowed))
        self.assertEqual([], pp.state_view_block_problems(doc))

    def test_a_sweep_with_a_rowless_flag_is_invalid(self):
        cases = [_state_case("engine%d--default--deployed--0000aaa%d" % (i, i)) for i in range(3)]
        for doc, _masks in cases:
            doc["flags"] = [pp.FLAG_START_RELOADED_PREFIX + "2"]
        root = tempfile.mkdtemp(prefix="pp-reload-")
        self.addCleanup(shutil.rmtree, root, True)
        d = os.path.join(root, "s")
        _build_state_sweep(d, cases)
        res = _judge_fx_dir(d)
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        # The sampled case's recompute names its rows, the two unsampled ones the walk names.
        self.assertEqual(2, sum("start reload" in m for m in res.recompute_mismatches))
        self.assertEqual(1, sum("atlas rows=" in m and "expected" in m for m in res.recompute_mismatches))


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


# ---------------------------------------------------------------------------
# P8 F4: the FX judge (design Appendix E.4 to E.6). The measures are anchored on the
# committed C# fixture (F1b's builder wrote its numbers and its atlas); the judge runs on
# synthetic plumes drawn straight as 8-bit tiles.
# ---------------------------------------------------------------------------

FX_FIXTURE = os.path.join(REPO_ROOT, "Source", "Parsek.Tests", "Fixtures", "PartParity", "fx",
                          "fxfixtureplume--default--burn-full--aae6c591")
FTILE, FN = 48, 16
FX_AXIS = {"originPx": [4, 24], "dirPx": [1, 0], "nozzleDiameterPx": 10}
ORANGE, GREEN, GREY = (200, 100, 50), (50, 200, 100), (90, 90, 96)


def _fx_fixture():
    with open(FX_FIXTURE + pp.SIDECAR_SUFFIX, encoding="utf-8") as fh:
        return json.load(fh), gui_mirror.read_png(FX_FIXTURE + pp.FX_ATLAS_SUFFIX)


def _fx_tile(pixels):
    """RGBA bytes of a tile from {(x, y): (k, (r, g, b))}."""
    out = bytearray(FTILE * FTILE * 4)
    for (x, y), (k, rgb) in pixels.items():
        i = (y * FTILE + x) * 4
        out[i:i + 4] = bytes(rgb) + bytes([pp.fx_tile_alpha(k, FN)])
    return bytes(out)


def _plume(x0=4, y0=19, length=34, width=10, rgb=ORANGE, skew=0.0):
    """A side-on plume: a box of one-sample envelope whose middle 70 % is core. ``skew``
    turns the core inside the same envelope."""
    px = {}
    for x in range(x0, x0 + length):
        for y in range(y0, y0 + width):
            u, v = (x - x0) / (length - 1) - 0.5, (y - y0) / (width - 1) - 0.5
            px[(x, y)] = (FN if abs(v - skew * u) <= 0.35 else 1, rgb)
    return _fx_tile(px)


def _disc(radius=10, cx=24, cy=24, rgb=ORANGE):
    return _fx_tile({(x, y): (FN, rgb) for x in range(FTILE) for y in range(FTILE)
                     if (x + 0.5 - cx) ** 2 + (y + 0.5 - cy) ** 2 <= radius * radius})


FX_BLANK = _fx_tile({})
FX_DARK = (FX_BLANK, FX_BLANK)
# One side's tiles: {layer: (the side and far views, the bottom view)}.
REAL_FX = {"flame": (_plume(), _disc()),
           "smoke": (_plume(y0=14, width=20, rgb=GREY), _disc(14, rgb=GREY))}
NO_FX = {"flame": FX_DARK, "smoke": FX_DARK}


def _fx_side(table, layer, view):
    side, bottom = table[layer]
    return bottom if view == pp.FX_VIEW_AXIS else side


_FX_MEASURED = {}


def _fx_measured(tile, view):
    key = (tile, view)
    if key not in _FX_MEASURED:
        _FX_MEASURED[key] = pp.fx_measure_tile(tile, FTILE, 0, 0, FTILE, FN, view, FX_AXIS)
    return _FX_MEASURED[key]


def _fx_entry(route, view, layer, real, ghost):
    r, g = _fx_measured(real, view), _fx_measured(ghost, view)
    half = {"samples": FN // 2, "real": r.measures, "ghost": g.measures,
            "compare": pp.fx_compare(view, FX_AXIS, r, g)}
    doc = {"view": view, "route": route, "layer": layer, "width": FTILE, "height": FTILE,
           "axis": FX_AXIS, "real": r.measures, "ghost": g.measures, "compare": half["compare"],
           "splitHalf": {"even": half, "odd": copy.deepcopy(half)}}
    return json.loads(json.dumps(doc))  # no object shared between two entries


def _fx_case(case_id="engine--default--burn-full--0000aaaa", event=None, start=None, all_=None,
             family="plume", state="burn-full", control=False, reference=None, real=REAL_FX,
             rcs=None, structure=None, **kw):
    """(sidecar, body masks, FX tiles by atlas row and view) of one stochastic FX case. Each
    side is a {layer: tiles} table; a ghost side is the real one with its table's layers
    replaced."""
    sides = {"real": real, "ghost": dict(real, **(event or {})),
             "ghost-start": dict(real, **(start or {})), "ghost-all": dict(real, **(all_ or {}))}
    if control:
        sides["ghost-start"] = NO_FX
    doc, masks = _state_case(case_id, part=kw.pop("part", "liquidEngine2.v2"), family=family,
                             state=state, **kw)
    doc["stateProof"]["events"] = [{"type": "EngineIgnited", "ut": 10.0, "pid": 100000, "midx": 0}]
    block = {k: None for k in pp.FX_KEYS}
    block.update({"family": family, "samples": FN, "reference": reference, "rcs": rcs,
                  "structure": structure if structure is not None else {
                      "fxMissingOnGhost": [], "fxExtraOnGhost": [], "fxDiffers": [],
                      "lightsDiffer": []},
                  "views": [_fx_entry(r, v, layer, _fx_side(real, layer, v),
                                      _fx_side(sides[pp.FX_ROUTE_ROW[r]], layer, v))
                            for r, v, layer in pp.fx_expected_entries(family, control)]})
    doc["fx"] = block
    rows = pp.fx_atlas_rows_for(family == "rcs")
    doc["fxAtlas"] = {"file": case_id + pp.FX_ATLAS_SUFFIX, "rows": list(rows),
                      "cols": list(pp.FX_VIEWS), "tile": FTILE}
    tiles = {(row, v): _fx_side(sides[row.rsplit("-", 1)[0]], row.rsplit("-", 1)[1], v)
             for row in rows for v in pp.FX_VIEWS}
    return doc, masks, tiles


def _fx_atlas(doc, tiles):
    """(w, h, 4, pixels) of a case's FX atlas."""
    rows, cols = doc["fxAtlas"]["rows"], doc["fxAtlas"]["cols"]
    out = bytearray()
    for row in rows:
        for y in range(FTILE):
            for v in cols:
                out += tiles[(row, v)][y * FTILE * 4:(y + 1) * FTILE * 4]
    return len(cols) * FTILE, len(rows) * FTILE, 4, bytes(out)


def _fx_fails(doc):
    """{(route, view, layer): failing measures}, the passing entries left out."""
    rcs = doc["fx"]["family"] == "rcs"
    fails = {(e["route"], e["view"], e["layer"]): pp.fx_failing_measures(e, rcs)
             for e in doc["fx"]["views"]}
    return {k: v for k, v in fails.items() if v}


# The broken plumes: the event ghost's broken layer, and the only measures it fails.
BROKEN_PLUMES = {
    "off": ({"flame": FX_DARK}, {"presence"}),
    "smoke-dropped": ({"smoke": FX_DARK}, {"smoke"}),
    "short": ({"flame": (_plume(length=20), _disc())}, {"length"}),
    "wide": ({"flame": (_plume(y0=16, width=16), _disc())}, {"width"}),
    "shifted-along": ({"flame": (_plume(x0=10), _disc())}, {"origin"}),
    "shifted-across": ({"flame": (_plume(y0=25), _disc())}, {"lateral"}),
    "shifted": ({"flame": (_plume(x0=10, y0=25), _disc())}, {"origin", "lateral"}),
    "turned": ({"flame": (_plume(skew=0.8), _disc())}, {"direction"}),
    "hue": ({"flame": (_plume(rgb=GREEN), _disc(rgb=GREEN))}, {"colour"}),
    "thin-section": ({"flame": (_plume(), _disc(7))}, {"cross-section"}),
    "offset-section": ({"flame": (_plume(), _disc(cx=27))}, {"cross-section"}),
}


def _broken(name):
    return BROKEN_PLUMES[name][0]


class FxMeasureTests(unittest.TestCase):
    """The Python transcription of PartParityFxMetrics against the committed C# fixture."""

    def test_the_c_sharp_fixture_recomputes(self):
        side, atlas = _fx_fixture()
        self.assertEqual([], pp.fx_block_problems(side))
        self.assertEqual([], pp.recompute_fx_case(side, atlas))
        self.assertEqual(16, len(side["fx"]["views"]))
        e = side["fx"]["views"][0]
        self.assertEqual((386, 400, 179), tuple(e["real"][k] for k in pp.FX_EXACT_KEYS))

    def test_every_value_of_the_fixture_is_bit_identical(self):
        side, (w, _h, _bpp, px) = _fx_fixture()
        rows, tile, n = side["fxAtlas"]["rows"], side["fxAtlas"]["tile"], side["fx"]["samples"]
        checked = 0
        for e in side["fx"]["views"]:
            col = pp.FX_VIEWS.index(e["view"])
            got = {}
            for name, row in (("real", "real"), ("ghost", pp.FX_ROUTE_ROW[e["route"]])):
                y = rows.index("%s-%s" % (row, e["layer"])) * tile
                got[name] = pp.fx_measure_tile(px, w, col * tile, y, tile, n, e["view"], e["axis"])
                for key in pp.FX_SIDE_MEASURE_KEYS:
                    self.assertEqual(e[name][key], got[name].measures[key], (e["view"], name, key))
                    checked += 1
            for key, value in pp.fx_compare(e["view"], e["axis"], got["real"], got["ghost"]).items():
                self.assertEqual(e["compare"][key], value, (e["view"], e["route"], key))
                checked += 1
        self.assertEqual(528, checked)

    def test_every_tampered_fixture_number_is_a_mismatch(self):
        side, atlas = _fx_fixture()
        targets = [("real", k) for k in pp.FX_SIDE_MEASURE_KEYS] \
            + [("ghost", k) for k in pp.FX_SIDE_MEASURE_KEYS] \
            + [("compare", k) for k in pp.FX_COMPARE_KEYS]
        for block, key in targets:
            with self.subTest(block=block, key=key):
                bad = copy.deepcopy(side)
                hit = next(e for e in bad["fx"]["views"] if e[block][key] is not None)
                value = hit[block][key]
                if isinstance(value, list):
                    value[-1] += 0.001
                else:
                    hit[block][key] = value + (1 if key in pp.FX_EXACT_KEYS else 1e-6)
                diffs = pp.recompute_fx_case(bad, atlas)
                self.assertEqual(1, len(diffs), diffs)
                self.assertIn("%s.%s" % (block, key), diffs[0])
                hit[block][key] = None
                self.assertEqual(1, len(pp.recompute_fx_case(bad, atlas)))

    def test_the_float_tolerance_is_a_billionth(self):
        side, atlas = _fx_fixture()
        compare = side["fx"]["views"][0]["compare"]
        compare["chromaDistance"] += 5e-10
        self.assertEqual([], pp.recompute_fx_case(side, atlas))
        compare["chromaDistance"] += 5e-9
        self.assertEqual(1, len(pp.recompute_fx_case(side, atlas)))

    def test_a_count_must_be_an_integer(self):
        side, atlas = _fx_fixture()
        side["fx"]["views"][0]["real"]["corePx"] = 386.0
        self.assertEqual(1, len(pp.recompute_fx_case(side, atlas)))

    def test_a_mutated_atlas_pixel_is_a_mismatch(self):
        side, (w, h, bpp, px) = _fx_fixture()
        tile = side["fxAtlas"]["tile"]
        # A fully covered, unsaturated core pixel of the real flame in fx-side-a.
        i = next(p * 4 for p in range(tile * w) if p % w < tile and px[p * 4 + 3] == 255
                 and not pp.fx_is_saturated(*px[p * 4:p * 4 + 3]))
        for what, channel, value in (("colour", 0, px[i] ^ 1), ("uncovered", 3, 0),
                                     ("no sample count", 3, 250)):
            with self.subTest(what):
                buf = bytearray(px)
                buf[i + channel] = value
                self.assertTrue(pp.recompute_fx_case(side, (w, h, bpp, bytes(buf))))
        # A colour on an uncovered pixel of a row that holds a plume.
        row = (i // 4 // w) * w * 4
        blank = next(j for j in range(row, row + tile * 4, 4) if px[j + 3] == 0)
        buf = bytearray(px)
        buf[blank] = 9
        diffs = pp.recompute_fx_case(side, (w, h, bpp, bytes(buf)))
        self.assertTrue(any("colour on an uncovered pixel" in d for d in diffs), diffs)

    def test_a_layout_disagreement_is_a_mismatch(self):
        side, atlas = _fx_fixture()
        for key, value in (("tile", 32), ("rows", list(pp.FX_ATLAS_ROWS_RCS)),
                           ("cols", list(pp.FX_VIEWS[:3])), ("tile", None)):
            with self.subTest(key=key, value=value):
                bad = copy.deepcopy(side)
                bad["fxAtlas"][key] = value
                self.assertEqual(1, len(pp.recompute_fx_case(bad, atlas)))
        for samples in (0, None, 256, 16.0):
            bad = copy.deepcopy(side)
            bad["fx"]["samples"] = samples
            self.assertEqual(1, len(pp.recompute_fx_case(bad, atlas)))
        bad = copy.deepcopy(side)
        bad["fx"]["views"][3]["route"] = "all"  # a plume atlas has no ghost-all row
        self.assertTrue(any("not in the fx atlas" in d for d in pp.recompute_fx_case(bad, atlas)))
        bad = copy.deepcopy(side)
        bad["fx"]["views"][3]["width"] = 63
        self.assertEqual(1, len(pp.recompute_fx_case(bad, atlas)))
        self.assertEqual(["fx atlas is not RGBA (bytes per pixel 3)"],
                         pp.recompute_fx_case(side, (atlas[0], atlas[1], 3, atlas[3])))

    def test_hue_bins_and_wraps(self):
        self.assertEqual(-1, pp.fx_hue_bin(9, 9, 9))
        self.assertEqual([0, 4, 8, 2, 6, 10], [pp.fx_hue_bin(*c) for c in (
            (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (0, 255, 255), (255, 0, 255))])
        self.assertEqual(11, pp.fx_hue_bin(255, 0, 1))
        self.assertEqual([180.0, -170.0, 180.0], [pp.fx_wrap_deg(d) for d in (-180.0, 190.0, 540.0)])

    def test_no_axis_on_the_axis_view_or_a_zero_direction(self):
        self.assertIsNone(pp.fx_axis("fx-bottom", FX_AXIS))
        self.assertIsNone(pp.fx_axis("fx-far", dict(FX_AXIS, dirPx=[0, 0])))
        self.assertIsNone(pp.fx_axis("fx-far", dict(FX_AXIS, originPx=None)))
        self.assertIsNone(pp.fx_axis("fx-far", None))
        self.assertEqual((4, 24, 0.6, 0.8), pp.fx_axis("fx-far", dict(FX_AXIS, dirPx=[3, 4])))


class FxJudgeTests(unittest.TestCase):
    """judge_case on FX cases: the measures per route, the caps and the classes."""

    def test_a_matching_case_passes(self):
        doc, _m, tiles = _fx_case()
        self.assertEqual([], pp.fx_block_problems(doc))
        self.assertEqual([], pp.recompute_fx_case(doc, _fx_atlas(doc, tiles)))
        c = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_PASS, c.verdict, c.reasons)
        self.assertEqual((("event", "pass"), ("start", "pass")), c.fx.routes)
        self.assertEqual(16, len(c.fx.entries))
        self.assertEqual((), c.fx.flags)

    def test_each_broken_plume_fails_only_its_own_measure(self):
        for name, (broken, want) in BROKEN_PLUMES.items():
            with self.subTest(name):
                doc, _m, tiles = _fx_case(event=broken)
                self.assertEqual([], pp.recompute_fx_case(doc, _fx_atlas(doc, tiles)))
                fails = _fx_fails(doc)
                self.assertEqual(want, {m for ms in fails.values() for m in ms}, fails)
                self.assertEqual({"event"}, {k[0] for k in fails})
                c = pp.judge_case(doc)
                self.assertEqual(pp.VERDICT_FAIL, c.verdict)
                self.assertEqual((("event", "fail"), ("start", "pass")), c.fx.routes)

    def test_the_broken_fixture_fails_its_own_measures(self):
        # The committed C# atlas: a short event ghost; a hue-rotated, shifted, smoke-less start one.
        fails = _fx_fails(_fx_fixture()[0])
        by_route = {r: {m for k, ms in fails.items() if k[0] == r for m in ms} for r in ("event", "start")}
        self.assertEqual({"length"}, by_route["event"])
        self.assertEqual({"origin", "lateral", "colour", "cross-section", "smoke"}, by_route["start"])

    def test_the_start_route_is_judged_on_its_own(self):
        c = pp.judge_case(_fx_case(start=_broken("short"))[0])
        self.assertEqual((("event", "pass"), ("start", "fail")), c.fx.routes)
        self.assertEqual(pp.VERDICT_FAIL, c.verdict)
        self.assertEqual({"start:%s:flame:length=0.576" % v for v in pp.FX_VIEWS if v != "fx-bottom"},
                         set(c.reasons))

    def test_reasons_name_route_view_and_layer(self):
        c = pp.judge_case(_fx_case(event=_broken("smoke-dropped"))[0])
        self.assertEqual({"event:%s:smoke:smoke-missing" % v for v in pp.FX_VIEWS}, set(c.reasons))
        c = pp.judge_case(_fx_case(real=dict(REAL_FX, flame=FX_DARK), event={"flame": REAL_FX["flame"]})[0])
        self.assertEqual({"event:%s:flame:fx-extra" % v for v in pp.FX_VIEWS}, set(c.reasons))

    def test_the_lines(self):
        def levels(compare, length=100.0):
            e = {"layer": "flame", "real": {"corePx": 100, "lengthPx": length},
                 "ghost": {"corePx": 100}, "compare": compare}
            return [(f.measure, f.level) for f in pp.fx_entry_findings(e, False)]
        for compare, want in (
                ({"originDeltaPx": 10.0}, [("origin", "warn")]),
                ({"originDeltaPx": -10.1}, [("origin", "fail")]),
                ({"originDeltaPx": 5.0}, []),
                ({"lateralOffsetPx": 5.0}, [("lateral", "warn")]),
                ({"lateralOffsetPx": -5.1}, [("lateral", "fail")]),
                ({"lengthRatio": 0.75}, [("length", "warn")]),
                ({"lengthRatio": 1.26}, [("length", "fail")]),
                ({"lengthRatio": 1.125}, []),
                ({"widthRatio": 0.69}, [("width", "fail")]),
                ({"widthRatio": 1.2}, [("width", "warn")]),
                ({"directionDeg": -10.1}, [("direction", "fail")]),
                ({"directionDeg": 6.0}, [("direction", "warn")]),
                ({"directionDeg": 5.0}, []),
                ({"crossSectionAreaRatio": 0.59}, [("cross-section", "fail")]),
                ({"crossSectionAreaRatio": 1.61}, [("cross-section", "fail")]),
                ({"crossSectionAreaRatio": 1.3}, [("cross-section", "warn")]),
                ({"crossSectionOffset": 0.11}, [("cross-section", "fail")]),
                ({"crossSectionOffset": 0.1}, []),
                ({"chromaDistance": 0.061}, [("colour", "fail")]),
                ({"chromaDistance": 0.016}, [("colour", "warn")]),
                ({"hueL1": 0.51}, [("colour", "fail")]),
                ({"hueL1": 0.126}, [("colour", "warn")]),
                ({"brightnessRatio": 0.1}, [("brightness", "warn")]),
                ({"coreIou": 0.1}, [("core-iou", "warn")]),
                ({"lengthRatio": None, "directionDeg": None}, [])):
            with self.subTest(compare):
                self.assertEqual(want, levels(compare))
        # The pixel floors hold on a short plume.
        self.assertEqual([("origin", "fail"), ("lateral", "warn")],
                         levels({"lateralOffsetPx": 4.0, "originDeltaPx": 4.1}, length=10.0))

    def test_presence_floors(self):
        self.assertEqual("fx-missing", pp.fx_presence_reason("flame", 64, 0))
        self.assertIsNone(pp.fx_presence_reason("flame", 63, 0))
        self.assertEqual("smoke-extra", pp.fx_presence_reason("smoke", 0, 256))
        self.assertIsNone(pp.fx_presence_reason("smoke", 255, 0))
        self.assertIsNone(pp.fx_presence_reason("flame", 64, 1))

    def _weak_entry(self, real, ghost, layer="flame", real_env=None, ghost_env=None, compare=None):
        side = lambda core, env: {"corePx": core, "envelopePx": core if env is None else env,
                                  "lengthPx": 100.0}
        return {"route": "event", "view": "fx-far", "layer": layer, "real": side(real, real_env),
                "ghost": side(ghost, ghost_env), "compare": compare or {}}

    WILD = {"originDeltaPx": -29.0, "lateralOffsetPx": 40.0, "lengthRatio": 1.373, "widthRatio": 3.0,
            "directionDeg": 50.0, "crossSectionAreaRatio": 9.0, "crossSectionOffset": 2.0,
            "chromaDistance": 0.5, "hueL1": 1.5, "brightnessRatio": 9.0, "coreIou": 0.0}

    def test_two_weak_cores_are_judged_on_presence_only(self):
        # The ReStock Flea's smoke against itself (PP-8 `2026-11-23_1112`): cores 0, envelopes
        # 1570 and 3680 px, length 1.37, origin -29 px, all of it stray particles.
        for real, ghost in ((0, 0), (63, 63), (63, 0), (0, 63)):
            for layer in pp.FX_LAYERS:
                with self.subTest(real=real, ghost=ghost, layer=layer):
                    e = self._weak_entry(real, ghost, layer, 1570, 3680, self.WILD)
                    self.assertEqual([], pp.fx_entry_findings(e, False))
                    self.assertEqual("fx-weak-core:event:fx-far:%s:real=%d,ghost=%d" % (layer, real, ghost),
                                     pp.fx_weak_core_flag(e))
        # One core at the minimum is enough for every measure to be judged, either way round.
        for real, ghost in ((64, 63), (63, 64), (64, 64)):
            for layer in pp.FX_LAYERS:
                e = self._weak_entry(real, ghost, layer, None, None, self.WILD)
                self.assertEqual(["origin", "lateral", "length", "width", "direction", "cross-section",
                                  "colour"], pp.fx_failing_measures(e), (real, ghost, layer))

    def test_one_weak_core_never_passes_silently(self):
        """Every (weak, strong) pair, both ways round and on both layers, with every other
        measure matching: presence fails it or the flag names it."""
        for layer in pp.FX_LAYERS:
            for strong in range(64, 300):
                for weak in range(64):
                    for real, ghost in ((weak, strong), (strong, weak)):
                        e = self._weak_entry(real, ghost, layer)
                        failed = [f.measure for f in pp.fx_entry_findings(e, False)
                                  if f.level == pp.VERDICT_FAIL]
                        flag = pp.fx_weak_core_flag(e)
                        self.assertTrue(failed or flag, (layer, real, ghost))
                        if weak == 0 and strong >= (256 if layer == "smoke" else 64):
                            self.assertEqual(["smoke" if layer == "smoke" else "presence"], failed)
                            self.assertIsNone(flag)  # presence says it

    def test_nothing_drawn_and_strong_cores_raise_no_weak_flag(self):
        self.assertIsNone(pp.fx_weak_core_flag(self._weak_entry(0, 0)))
        self.assertIsNone(pp.fx_weak_core_flag(self._weak_entry(64, 64)))
        self.assertIsNone(pp.fx_weak_core_flag({}))

    def test_a_missing_plume_still_fails(self):
        # Real core over the minimum, ghost core 0.
        c = pp.judge_case(_fx_case(event={"flame": FX_DARK})[0])
        self.assertEqual(pp.VERDICT_FAIL, c.verdict)
        self.assertEqual({"event:%s:flame:fx-missing" % v for v in pp.FX_VIEWS}, set(c.reasons))

    def _faint(self, real_px, ghost_px):
        """A case whose smoke is a one-sample smear on each side: an envelope, no core."""
        tile = lambda n: _fx_tile({(x, y): (1, GREY) for x in range(FTILE) for y in range(FTILE)
                                   if y * FTILE + x < n})
        real = dict(REAL_FX, smoke=(tile(real_px), tile(real_px)))
        ghost = {"smoke": (tile(ghost_px), tile(ghost_px))}
        return _fx_case(real=real, event=ghost, start=ghost)

    def test_two_empty_cores_with_different_envelopes_warn_and_never_pass(self):
        for real_px, ghost_px in ((300, 2000), (2000, 0), (0, 2000), (500, 500)):
            with self.subTest(real=real_px, ghost=ghost_px):
                doc, _m, tiles = self._faint(real_px, ghost_px)
                self.assertEqual([], pp.fx_block_problems(doc))
                self.assertEqual([], pp.recompute_fx_case(doc, _fx_atlas(doc, tiles)))
                c = pp.judge_case(doc)
                self.assertEqual(pp.VERDICT_WARN, c.verdict)
                want = {"fx-weak-core:%s:%s:smoke:real=0,ghost=0" % (r, v)
                        for r in ("event", "start") for v in pp.FX_VIEWS}
                self.assertEqual(want, set(c.fx.flags))
                self.assertEqual({"flag:" + f for f in want}, set(c.reasons))
                self.assertEqual(want, set(c.to_dict()["fx"]["flags"]))

    def test_a_weak_core_never_hides_a_fail_elsewhere(self):
        smear = _fx_tile({(x, 0): (1, GREY) for x in range(FTILE)})
        doc = _fx_case(real=dict(REAL_FX, smoke=(smear, smear)), event={"flame": FX_DARK})[0]
        c = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_FAIL, c.verdict)
        self.assertTrue(any(f.startswith("fx-weak-core:") for f in c.fx.flags))
        # Nor do its unsettled halves cap the fail (PP-9 `2026-11-23_1050`,
        # liquidengine--size1--burn-low: smoke halves 21 and 12 px over an 18 px core).
        for e in doc["fx"]["views"]:
            if e["layer"] == "smoke":
                e["splitHalf"]["odd"]["real"]["lengthPx"] = 1.0
        c = pp.judge_case(doc)
        self.assertEqual((pp.VERDICT_FAIL, ()), (c.verdict, c.fx.unsettled))
        # A judged layer that did not settle still caps (E.4).
        for e in doc["fx"]["views"]:
            if e["layer"] == "flame" and e["route"] == "event":
                e["splitHalf"]["odd"]["real"]["corePx"] = 0
        self.assertEqual(pp.VERDICT_WARN, pp.judge_case(doc).verdict)

    def test_a_skipped_entry_says_so(self):
        c = pp.judge_case(self._faint(300, 2000)[0])
        smoke = [e for e in c.fx.entries if e.layer == "smoke"]
        self.assertEqual({pp.VERDICT_SKIPPED}, {e.verdict for e in smoke})
        self.assertEqual({pp.VERDICT_PASS}, {e.verdict for e in c.fx.entries if e.layer == "flame"})
        self.assertEqual({"skipped"}, {e["verdict"] for e in c.to_dict()["fx"]["entries"]
                                       if e["layer"] == "smoke"})
        # It counts as a pass: the flag raises the case, the routes read pass.
        self.assertEqual((("event", "pass"), ("start", "pass")), c.fx.routes)
        self.assertNotIn(pp.VERDICT_SKIPPED, pp.VERDICTS)
        # Two empty pictures agree, and are judged.
        smear = _fx_tile({(x, 0): (1, GREY) for x in range(FTILE)})
        for real, ghost, skipped in ((FX_DARK, FX_DARK, False), ((smear, smear), FX_DARK, True),
                                     (FX_DARK, (smear, smear), True)):
            e = pp.judge_case(_fx_case(real=dict(REAL_FX, smoke=real), event={"smoke": ghost})[0])
            got = {x.verdict for x in e.fx.entries if x.layer == "smoke" and x.route == "event"}
            self.assertEqual({pp.VERDICT_SKIPPED if skipped else pp.VERDICT_PASS}, got)
        gone = pp.judge_case(_fx_case(event=_broken("off"))[0])
        self.assertIn(pp.VERDICT_FAIL, {x.verdict for x in gone.fx.entries if x.layer == "flame"})

    def test_the_real_vs_real_control_skips_two_weak_cores_and_no_more(self):
        doc = _fx_case(case_id="ctl-fx-real-vs-real", control=True)[0]
        smoke = [e for e in doc["fx"]["views"] if e["layer"] == "smoke"]
        self.assertEqual(4, len(smoke))
        for e in smoke:
            e["real"]["corePx"] = e["ghost"]["corePx"] = 0
            e["compare"].update(originDeltaPx=-29.0, lengthRatio=1.373)
        self.assertEqual((pp.CONTROLS_OK, ""), pp.judge_fx_control("ctl-fx-real-vs-real", doc))
        smoke[0]["ghost"]["corePx"] = 64
        self.assertEqual((pp.CONTROLS_BAD, "fails origin,length"), pp.judge_fx_control("ctl-fx-real-vs-real", doc))

    def test_a_dim_plume_only_warns(self):
        dim = (60, 30, 15)
        c = pp.judge_case(_fx_case(event={"flame": (_plume(rgb=dim), _disc(rgb=dim))})[0])
        self.assertEqual(pp.VERDICT_WARN, c.verdict)
        self.assertTrue(all(":brightness=" in r for r in c.reasons), c.reasons)

    RCS = {"input": [1, 0, 0], "predicted": [0, 1], "fired": [0, 1], "nozzles": 4,
           "ghostHidden": 2, "powerRatio": 0.5}

    def test_an_rcs_case_never_fails_on_length_width_or_brightness(self):
        small = {"flame": (_plume(length=12, y0=22, width=4, rgb=(40, 20, 10)), _disc(rgb=(40, 20, 10)))}
        doc, _m, tiles = _fx_case(family="rcs", state="rcs-fire", event=small, rcs=self.RCS,
                                  all_={"flame": (_plume(y0=2, width=40, rgb=GREEN), _disc(20))})
        self.assertEqual([], pp.fx_block_problems(doc))
        self.assertEqual([], pp.recompute_fx_case(doc, _fx_atlas(doc, tiles)))
        c = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_WARN, c.verdict, c.reasons)
        # The ghost's 24 px core on the 34 px tile is the weak one of a pair: named, still judged.
        self.assertTrue(all(":core-iou=" in r or r.startswith("flag:fx-weak-core:event:")
                            for r in c.reasons), c.reasons)
        self.assertEqual("rcs-known-difference:nozzles=2/4,power=0.500", c.fx.flags[0])
        d = c.to_dict()["fx"]["rcs"]
        self.assertEqual(0.5, d["powerRatio"])
        row = next(r for r in d["reportedOnly"]
                   if (r["route"], r["view"], r["layer"]) == ("event", "fx-far", "flame"))
        self.assertAlmostEqual(11.0 / 33.0, row["length"])
        self.assertAlmostEqual(3.0 / 9.0, row["width"])
        self.assertAlmostEqual(0.2, row["brightness"])
        # The ghost-all rows are written and recomputed, never judged.
        self.assertEqual(8, sum(1 for e in doc["fx"]["views"] if e["route"] == "all"))
        self.assertEqual({"event", "start"}, {e.route for e in c.fx.entries})
        # The same gap on a plume fails length and width.
        self.assertEqual({"length", "width"}, {m for ms in _fx_fails(_fx_case(event=small)[0]).values()
                                               for m in ms})

    def test_an_rcs_case_still_fails_on_the_judged_measures(self):
        for name in ("off", "shifted-along", "shifted-across", "turned", "hue"):
            with self.subTest(name):
                doc = _fx_case(family="rcs", state="rcs-fire", rcs=self.RCS, event=_broken(name))[0]
                self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)

    def test_the_known_difference_flag(self):
        kd = lambda **rcs: pp.fx_rcs_known_difference({"fx": {"family": "rcs", "rcs": rcs}})  # noqa: E731
        self.assertEqual("rcs-known-difference:nozzles=1/4,power=null",
                         kd(fired=[2], nozzles=[0, 1, 2, 3], powerRatio=None))
        self.assertEqual("rcs-known-difference:nozzles=4/4,power=0.900",
                         kd(fired=4, nozzles=4, powerRatio=0.9))
        self.assertIsNone(kd(fired=4, nozzles=4, powerRatio=1.0))
        self.assertIsNone(kd(fired=None, nozzles=4, powerRatio=1.0))
        # A written flag stands; no second one is derived beside it.
        doc = _fx_case(family="rcs", state="rcs-fire", rcs=self.RCS)[0]
        doc["flags"] = ["rcs-known-difference:nozzles=1/4,power=0.250"]
        c = pp.judge_case(doc)
        self.assertEqual(("rcs-known-difference:nozzles=1/4,power=0.250",), c.fx.flags)
        self.assertEqual(pp.VERDICT_PASS, c.verdict)

    def test_a_reference_case_never_fails(self):
        for name in BROKEN_PLUMES:
            with self.subTest(name):
                c = pp.judge_case(_fx_case(event=_broken(name), reference="waterfall")[0])
                self.assertEqual((pp.VERDICT_REFERENCE, pp.VERDICT_FAIL), (c.verdict, c.fx.would_be))
                self.assertEqual(pp.VERDICT_FAIL, c.to_dict()["fx"]["wouldBe"])
        # Nor on its body routes.
        doc = _fx_case(reference="waterfall", ghost=_shifted())[0]
        c = pp.judge_case(doc)
        self.assertEqual((pp.VERDICT_REFERENCE, pp.VERDICT_FAIL), (c.verdict, c.fx.would_be))
        # A setup fault is still an error.
        doc["setup"] = {"status": "state-timeout", "detail": None}
        c = pp.judge_case(doc)
        self.assertEqual((pp.VERDICT_ERROR, None), (c.verdict, c.fx.would_be))
        self.assertEqual(pp.VERDICT_PASS, pp.judge_case(_fx_case(reference="waterfall")[0]).fx.would_be)

    def test_a_reference_marker_on_a_judged_family_is_an_instrument_problem(self):
        doc = _fx_case()[0]
        self._as_family(doc, "heat", "heat-hot", reference="waterfall", views=None)
        self.assertIn("fx.reference=waterfall on a heat case", pp.fx_block_problems(doc))
        doc["views"][0]["colorBlobPx"] = 5000
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)
        doc = _fx_case(event=_broken("off"), reference="other")[0]
        self.assertIn("fx.reference=other on a plume case", pp.fx_block_problems(doc))
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)

    @staticmethod
    def _as_family(doc, family, state, **fx_values):
        doc["state"] = doc["stateProof"]["state"] = state
        doc["fx"]["family"] = doc["stateProof"]["family"] = family
        doc["fx"].update(fx_values)

    def test_the_family_must_agree_with_the_case(self):
        # The family is what decides whether the FX is judged at all.
        for family in ("light", "heat"):
            doc = _fx_case(event=_broken("off"))[0]
            doc["fx"].update({"family": family, "plate": {"litPct": 0.4}})
            self.assertIn("fx.family=%s on a burn-full case" % family, pp.fx_block_problems(doc))
            self.assertEqual(pp.VERDICT_ERROR, pp.judge_case(doc).verdict)
            doc["stateProof"]["family"] = family  # the state word alone still tells
            self.assertEqual(pp.VERDICT_ERROR, pp.judge_case(doc).verdict)
        doc = _fx_case(state="burn-low-m2")[0]
        self.assertEqual([], pp.fx_block_problems(doc))
        for family in ("rcs", None):
            doc["stateProof"]["family"] = family
            self.assertEqual(["fx.family=plume stateProof.family=%s" % family], pp.fx_block_problems(doc))
            self.assertEqual(pp.VERDICT_ERROR, pp.judge_case(doc).verdict)

    def test_a_plume_case_says_which_throttle_drove_it(self):
        drive = {"ignited": True, "currentThrottle": 1.0, "recordedPower": 1.0}
        for state, throttle, main, independent, ok in (
                ("main-full", 1.0, 1.0, False, True), ("main-low-m2", 0.3, 0.3, False, True),
                ("main-low", 0.3, 0.295, False, True),
                ("main-full", 1.0, 0.0, False, False), ("main-full", 1.0, 1.0, True, False),
                ("main-full", 1.0, 1.0, None, False), ("main-full", 1.0, None, False, False),
                ("main-low", 0.3, 1.0, False, False), ("main-low", 1.0, 1.0, False, False),
                ("main-low", None, 0.3, False, False),
                ("burn-full", 1.0, 0.0, True, True), ("burn-low-m1", 0.3, 0.0, True, True),
                ("burn-full", 1.0, None, None, True),  # a sidecar from before the keys
                ("burn-full", 1.0, 1.0, True, False), ("burn-low", 0.3, 0.0, False, False)):
            with self.subTest(state=state, main=main, independent=independent):
                doc = _fx_case(state=state)[0]
                doc["fx"]["throttle"] = throttle
                doc["fx"]["drive"] = dict(drive, mainThrottle=main, independentThrottle=independent)
                problems = pp.fx_block_problems(doc)
                self.assertEqual(ok, not problems, problems)
                self.assertEqual(not ok, pp.judge_case(doc).verdict == pp.VERDICT_ERROR)
        # No drive block at all: an older burn case is not asked, a main case is.
        doc = _fx_case(state="burn-full")[0]
        self.assertEqual([], pp.fx_block_problems(doc))
        doc = _fx_case(state="main-full")[0]
        self.assertIn("on a main-full case", pp.fx_block_problems(doc)[0])
        # A case that never reached its throttle is a setup fault, not a contradiction.
        doc["setup"] = {"status": "state-timeout", "detail": None}
        self.assertEqual([], pp.fx_block_problems(doc))

    def test_a_dark_plate_is_an_error_never_a_pass(self):
        doc = _fx_case()[0]
        for plate in (None, {"litPct": None}, {"litPct": 0.0}, {"litPct": 0.0199}):
            for status in ("ok", "still-animating"):
                with self.subTest(plate=plate, status=status):
                    self._as_family(doc, "light", "lit", plate=plate, views=None)
                    doc["setup"] = {"status": status, "detail": None}
                    self.assertEqual(pp.VERDICT_ERROR, pp.judge_case(doc).verdict)
                    self.assertTrue(any("on an ok case" in p for p in pp.fx_block_problems(doc)))
        doc["setup"] = {"status": "plate-not-lit", "detail": None}
        self.assertEqual([], pp.fx_block_problems(doc))
        self._as_family(doc, "light", "lit", plate={"litPct": 0.02})
        doc["setup"] = {"status": "ok", "detail": None}
        self.assertEqual(pp.VERDICT_PASS, pp.judge_case(doc).verdict)

    def test_an_ok_rcs_case_fired_the_predicted_nozzles(self):
        for rcs in (None, dict(self.RCS, fired=[]), dict(self.RCS, fired=[2, 3]),
                    dict(self.RCS, fired=None), dict(self.RCS, nozzles=1),
                    dict(self.RCS, predicted=None)):
            with self.subTest(rcs):
                doc = _fx_case(family="rcs", state="rcs-fire", rcs=rcs)[0]
                self.assertEqual(pp.VERDICT_ERROR, pp.judge_case(doc).verdict)
                self.assertTrue(any("on an ok case" in p for p in pp.fx_block_problems(doc)))
        doc = _fx_case(family="rcs", state="rcs-fire", rcs=dict(self.RCS, fired=[1, 0]))[0]
        self.assertEqual([], pp.fx_block_problems(doc))

    def test_an_entry_without_an_axis_is_an_instrument_problem(self):
        for axis in (None, dict(FX_AXIS, dirPx=None), dict(FX_AXIS, originPx=None),
                     dict(FX_AXIS, dirPx=[0, 0]), dict(FX_AXIS, dirPx=["a", 1])):
            with self.subTest(axis):
                doc = _fx_case()[0]
                doc["fx"]["views"][0]["axis"] = axis
                self.assertTrue(any("axis" in p for p in pp.fx_block_problems(doc)))
        # Consistently measured without one, the atlas recompute agrees; the shape does not.
        doc, _m, tiles = _fx_case(event=_broken("short"))
        for e in doc["fx"]["views"]:
            rows = ("real-" + e["layer"], "%s-%s" % (pp.FX_ROUTE_ROW[e["route"]], e["layer"]))
            r, g = [pp.fx_measure_tile(tiles[(row, e["view"])], FTILE, 0, 0, FTILE, FN, e["view"], None)
                    for row in rows]
            e.update(axis=None, real=r.measures, ghost=g.measures,
                     compare=pp.fx_compare(e["view"], None, r, g))
        self.assertEqual([], pp.recompute_fx_case(doc, _fx_atlas(doc, tiles)))
        self.assertIn("fx:event:fx-far:flame: no exhaust axis", pp.fx_block_problems(doc))

    def test_a_bottom_view_without_its_nozzle_diameter_warns(self):
        doc = _fx_case()[0]
        for e in doc["fx"]["views"]:
            if e["view"] == "fx-bottom":
                e["axis"] = dict(FX_AXIS, dirPx=None, nozzleDiameterPx=None)
                for block in (e, e["splitHalf"]["even"], e["splitHalf"]["odd"]):
                    block["compare"]["crossSectionOffset"] = None
        self.assertEqual([], pp.fx_block_problems(doc))
        c = pp.judge_case(doc)
        self.assertEqual((pp.VERDICT_WARN, (pp.FLAG_FX_NOZZLE_NOT_MEASURED,)), (c.verdict, c.fx.flags))

    def test_a_lost_or_bent_measure_is_an_instrument_problem_sampled_or_not(self):
        doc = _fx_case(event=_broken("short"))[0]
        for key in pp.FX_COMPARE_KEYS:
            for value in (None, "0.5", float("nan"), True):
                bad = copy.deepcopy(doc)
                for e in bad["fx"]["views"]:
                    if e["compare"][key] is not None:
                        e["compare"][key] = value
                with self.subTest(key=key, value=value):
                    self.assertTrue(pp.fx_block_problems(bad))
        for key in pp.FX_SIDE_MEASURE_KEYS:
            for value in (None, "x", [], 10 ** 400):
                bad = copy.deepcopy(doc)
                bad["fx"]["views"][0]["real"][key] = value
                with self.subTest(key=key, value=str(value)[:8]):
                    self.assertTrue(pp.fx_block_problems(bad))
                    pp.judge_case(bad)  # a verdict, never a raise
        bad = copy.deepcopy(doc)
        bad["fx"]["views"][0]["compare"]["lengthRatio"] = 1.0
        self.assertTrue(any("lengthRatio" in p for p in pp.fx_block_problems(bad)))
        bad = copy.deepcopy(doc)
        bad["fx"]["views"][0]["splitHalf"]["odd"]["real"]["lengthPx"] = None
        self.assertTrue(pp.fx_block_problems(bad))
        bad = copy.deepcopy(doc)
        bad["fx"]["views"][0]["real"]["corePx"] = 10 ** 6  # more core than envelope
        self.assertTrue(pp.fx_block_problems(bad))

    def test_a_case_with_no_real_effect_warns(self):
        c = pp.judge_case(_fx_case(real=NO_FX)[0])
        self.assertEqual((pp.VERDICT_WARN, (pp.FLAG_FX_NO_REAL_EFFECT,)), (c.verdict, c.fx.flags))
        # Smoke alone is an effect (a stock jet draws no flame).
        self.assertEqual(pp.VERDICT_PASS, pp.judge_case(_fx_case(real=dict(REAL_FX, flame=FX_DARK))[0]).verdict)

    def test_unmeasured_structure_lists_warn_and_malformed_ones_are_a_problem(self):
        lists = {"fxMissingOnGhost": [], "fxExtraOnGhost": [], "fxDiffers": [], "lightsDiffer": []}
        for key in ("fxMissingOnGhost", "fxExtraOnGhost"):
            doc = _fx_case(structure=dict(lists, **{key: None}))[0]
            c = pp.judge_case(doc)
            self.assertEqual((pp.VERDICT_WARN, (pp.FLAG_FX_STRUCTURE_NOT_MEASURED,)), (c.verdict, c.fx.flags))
            self.assertEqual([], pp.fx_block_problems(doc))
            for bad in ({"key": "a"}, 3):
                self.assertTrue(pp.fx_block_problems(_fx_case(structure=dict(lists, **{key: bad}))[0]))
        doc = _fx_case()[0]
        doc["fx"]["structure"].pop("fxExtraOnGhost")
        self.assertTrue(pp.fx_block_problems(doc))

    @staticmethod
    def _loosen(doc, route="event", layer="flame", views=pp.FX_VIEWS, side="real"):
        """Unsettles the entries named: their odd half's length is 0.89 of the even one's (the
        axis view has none: its core goes to 0)."""
        for e in doc["fx"]["views"]:
            if (e["route"], e["layer"]) == (route, layer) and e["view"] in views:
                m = e["splitHalf"]["odd"][side]
                if m["lengthPx"] is None:
                    m["corePx"] = 0
                else:
                    m["lengthPx"] *= 0.89

    def test_an_unsettled_entry_caps_its_own_fails_at_warn(self):
        doc = _fx_case(event=_broken("off"))[0]
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)
        self._loosen(doc)
        c = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_WARN, c.verdict)
        self.assertEqual(pp.REASON_FX_UNSETTLED, c.reasons[-1])
        self.assertIn(pp.CAP_FX_UNSETTLED, c.fx.flags)
        self.assertTrue(c.fx.unsettled[0].startswith("event:fx-side-a:flame:real length even="))
        capped = [f for e in c.fx.entries if e.layer == "flame" and e.route == "event" for f in e.findings]
        self.assertEqual({("presence", pp.VERDICT_WARN, True)}, {(f.measure, f.level, f.capped) for f in capped})
        self.assertEqual({True}, {r["capped"] for e in c.to_dict()["fx"]["entries"] for r in e["reasons"]})
        # One unsettled entry says nothing about the others: the plume is missing in three views.
        doc = _fx_case(event=_broken("off"))[0]
        self._loosen(doc, views=("fx-side-a",))
        c = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_FAIL, c.verdict)
        self.assertNotIn(pp.REASON_FX_UNSETTLED, c.reasons)
        self.assertIn(pp.CAP_FX_UNSETTLED, c.fx.flags)
        self.assertEqual({"fx-side-a": pp.VERDICT_WARN, "fx-side-b": pp.VERDICT_FAIL,
                          "fx-bottom": pp.VERDICT_FAIL, "fx-far": pp.VERDICT_FAIL},
                         {e.view: e.verdict for e in c.fx.entries if (e.route, e.layer) == ("event", "flame")})
        # The other route's fails are not this one's.
        doc = _fx_case(event=_broken("off"), start=_broken("off"))[0]
        self._loosen(doc)
        self.assertEqual((("event", pp.VERDICT_WARN), ("start", pp.VERDICT_FAIL)), pp.judge_case(doc).fx.routes)

    def test_the_cap_never_holds_what_no_sample_decides(self):
        lists = {"fxMissingOnGhost": ["fx_a<b"], "fxExtraOnGhost": [], "fxDiffers": [], "lightsDiffer": []}
        doc = _fx_case(structure=lists)[0]
        self._loosen(doc)
        c = pp.judge_case(doc)  # a rendered asset the ghost lacks
        self.assertEqual((pp.VERDICT_FAIL, pp.VERDICT_FAIL), (c.verdict, c.fx.verdict))
        doc = _fx_case(event=_broken("off"))[0]
        self._loosen(doc)
        doc["views"][0]["colorBlobPx"] = 10 ** 6  # the body route (PP-9's nozzle glow)
        c = pp.judge_case(doc)
        self.assertEqual((pp.VERDICT_FAIL, pp.VERDICT_WARN), (c.verdict, c.fx.verdict))
        self.assertNotIn(pp.REASON_FX_UNSETTLED, c.reasons)

    def test_smoke_unsettled_in_one_view_is_unsettled_in_all(self):
        # PP-9 `2026-11-23_1050`, solidbooster1-1: real smoke core 0 in every view (stray puffs,
        # one view's halves 18 and 12 px long), the ghost's smoke there: noise, not smoke-extra.
        smear = _fx_tile({(x, 0): (1, GREY) for x in range(FTILE)})
        doc = _fx_case(real=dict(REAL_FX, smoke=(smear, smear)),
                       event={"smoke": REAL_FX["smoke"]}, start={"smoke": REAL_FX["smoke"]})[0]
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)
        for route in ("event", "start"):
            self._loosen(doc, route, "smoke", ("fx-side-b",))
        c = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_WARN, c.verdict)
        self.assertEqual({"smoke-extra"}, {f.reason for e in c.fx.entries for f in e.findings if f.capped})
        # A flame's views are judged apart (above), and smoke never caps a flame.
        doc = _fx_case(event=_broken("off"))[0]
        self._loosen(doc, layer="smoke")
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)

    def test_beside_an_unsettled_entry_a_fail_stands_only_when_both_halves_show_it(self):
        def judged(name, spoil):
            doc = _fx_case(event=_broken(name))[0]
            self._loosen(doc, layer="smoke")  # the case has an unsettled entry
            for e in doc["fx"]["views"]:
                if (e["route"], e["layer"]) == ("event", "flame"):
                    spoil(e)
            return pp.judge_case(doc)

        for name, key, agree, other in (
                ("wide", "widthRatio", 1.0, 0.5), ("shifted-along", "originDeltaPx", 0.0, -30.0),
                ("shifted-across", "lateralOffsetPx", 0.0, -30.0), ("turned", "directionDeg", 0.0, -60.0)):
            with self.subTest(name):
                self.assertEqual(pp.VERDICT_FAIL, judged(name, lambda e: None).verdict)
                for value in (agree, other):  # one half passes, or fails the other way
                    c = judged(name, lambda e: e["splitHalf"]["odd"]["compare"].update({key: value}))
                    self.assertEqual(pp.VERDICT_WARN, c.verdict, (name, value))
                    self.assertIn(pp.REASON_FX_UNSETTLED, c.reasons)
        # Without an unsettled entry the same halves decide nothing: no settled fail is lowered.
        doc = _fx_case(event=_broken("wide"))[0]
        for e in doc["fx"]["views"]:
            e["splitHalf"]["odd"]["compare"]["widthRatio"] = 1.0
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)

    def test_beside_an_unsettled_entry_a_measure_noisy_against_itself_is_capped(self):
        def doc_of(side, dx):
            doc = _fx_case(event=_broken("offset-section"))[0]
            self._loosen(doc, layer="smoke")
            for e in doc["fx"]["views"]:
                if e["view"] == "fx-bottom" and e["layer"] == "flame":
                    e["splitHalf"]["odd"][side]["coreCentroid"][0] += dx
            return doc

        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc_of("real", 0.0)).verdict)
        for side in ("real", "ghost"):
            c = pp.judge_case(doc_of(side, 20.0))
            self.assertEqual(pp.VERDICT_WARN, c.verdict)
            e = next(e for e in doc_of(side, 20.0)["fx"]["views"]
                     if (e["route"], e["view"], e["layer"]) == ("event", "fx-bottom", "flame"))
            self.assertEqual({"cross-section"}, set(pp.fx_self_failing(e, False)))
        # A malformed half proves no noise (fx_block_problems is the finding).
        e["splitHalf"]["odd"]["ghost"]["hueHistogram"] = "x"
        self.assertEqual(frozenset(), pp.fx_self_failing(e, False))
        e["axis"] = {"originPx": [0, 0], "dirPx": None, "nozzleDiameterPx": "x"}
        self.assertEqual(frozenset(), pp.fx_self_failing(e, False))
        # Nor does an integer whose square no double holds, and the judge does not raise on it.
        big = 10 ** 200
        for spoil in (lambda x: x["axis"].update(dirPx=[big, 0]),
                      lambda x: x["splitHalf"]["odd"]["real"].update(coreCentroid=[big, 0]),
                      lambda x: x["splitHalf"]["odd"]["ghost"].update(chromaticity=[big, 0])):
            doc = doc_of("real", 0.0)
            for x in doc["fx"]["views"]:
                if x["axis"] is not None:
                    x["axis"] = dict(x["axis"])
                spoil(x)
            self.assertEqual("engine--default--burn-full--0000aaaa", pp.judge_case(doc).case_id)

    def test_an_unsettled_case_lowers_nothing_else(self):
        doc = _fx_case()[0]
        doc["fx"]["views"][0]["splitHalf"]["odd"]["real"]["lengthPx"] *= 0.89
        c = pp.judge_case(doc)
        self.assertEqual((pp.VERDICT_PASS, (pp.CAP_FX_UNSETTLED,)), (c.verdict, c.fx.flags))
        # The unjudged ghost-all rows cap nothing.
        doc = _fx_case(family="rcs", state="rcs-fire", rcs=self.RCS, event=_broken("off"))[0]
        e = next(e for e in doc["fx"]["views"] if e["route"] == "all")
        e["splitHalf"]["odd"]["ghost"]["lengthPx"] = 1.0
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)

    def test_the_split_half_rule(self):
        def halves(even, odd, layer="flame"):
            blank = {"corePx": 0, "lengthPx": None, "widthPx": None}
            return pp.fx_split_half_disagreement({"layer": layer, "real": {"corePx": 64}, "splitHalf": {
                "even": {"real": blank, "ghost": dict(blank, **even)},
                "odd": {"real": blank, "ghost": dict(blank, **odd)}}})
        self.assertIsNone(halves({"lengthPx": 100.0}, {"lengthPx": 90.0}))
        self.assertEqual("ghost length even=100.000 odd=89.900",
                         halves({"lengthPx": 100.0}, {"lengthPx": 89.9}))
        self.assertIsNone(halves({"lengthPx": 100.0}, {"lengthPx": None}))
        self.assertEqual("ghost width even=10.000 odd=12.000", halves({"widthPx": 10.0}, {"widthPx": 12.0}))
        self.assertEqual("ghost presence even=64 odd=0", halves({"corePx": 64}, {}))
        self.assertIsNone(halves({"corePx": 255}, {}, "smoke"))
        # Both whole-set cores weak: stray particles, no convergence to read. One is not enough.
        for real, ghost, settled in ((63, 63, True), (0, 0, True), (64, 0, False), (0, 64, False)):
            e = {"layer": "smoke", "real": {"corePx": real}, "ghost": {"corePx": ghost}, "splitHalf": {
                "even": {"ghost": {"corePx": 441, "lengthPx": 21.0}}, "odd": {"ghost": {"corePx": 0, "lengthPx": 12.0}}}}
            self.assertEqual(settled, pp.fx_split_half_disagreement(e) is None, (real, ghost))
        side, _atlas = _fx_fixture()
        self.assertEqual([None] * 16, [pp.fx_split_half_disagreement(e) for e in side["fx"]["views"]])

    def test_a_body_difference_still_fails_the_case(self):
        c = pp.judge_case(_fx_case(ghost=_shifted())[0])
        self.assertEqual((pp.VERDICT_FAIL, pp.VERDICT_PASS), (c.verdict, c.fx.verdict))

    def test_the_fx_structure_raise(self):
        s = {"fxMissingOnGhost": [{"key": "fx-smoke", "rendered": False}], "fxExtraOnGhost": [],
             "fxDiffers": ["a", "b"], "lightsDiffer": []}
        c = pp.judge_case(_fx_case(structure=s)[0])
        self.assertEqual((pp.VERDICT_PASS, 2), (c.verdict, c.fx.structure["fxDiffers"]))
        for entry in ({"key": "fx-flame", "rendered": True}, {"key": "fx-flame"}, "fx-flame"):
            with self.subTest(entry=entry):
                c = pp.judge_case(_fx_case(structure=dict(s, fxMissingOnGhost=[entry]))[0])
                self.assertEqual(pp.VERDICT_FAIL, c.verdict)
                self.assertIn("missing-fx:fx-flame", c.reasons)
        c = pp.judge_case(_fx_case(structure=dict(s, fxExtraOnGhost=["x"]))[0])
        self.assertIn("extra-fx:x", c.reasons)
        doc = _fx_case()[0]
        doc["fx"]["structure"] = None
        c = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_WARN, c.verdict)
        self.assertIn("flag:" + pp.FLAG_FX_STRUCTURE_NOT_MEASURED, c.reasons)

    def test_a_case_without_its_entries_is_an_error(self):
        doc = _fx_case()[0]
        doc["fx"]["views"].pop()
        c = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_ERROR, c.verdict)
        self.assertIn("fx-no-entries:1 lost, first start:fx-far:smoke", c.reasons)
        self.assertTrue(any("lacks 1 entr" in p for p in pp.fx_block_problems(doc)))
        doc["fx"]["views"] = None
        self.assertEqual(pp.VERDICT_ERROR, pp.judge_case(doc).verdict)

    def test_the_tags_are_judged_not_caps(self):
        doc = _fx_case(event=_broken("short"))[0]
        doc["tags"] = ["fx-not-plateaued:ghost"]
        doc["fx"]["plateau"] = {"real": {"signal": "coverage", "settled": False, "waitS": 8.0}}
        doc["fx"]["framing"] = {"near": 1.0, "far": 80.0, "clamped": True}
        c = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_FAIL, c.verdict)
        self.assertEqual(("fx-not-plateaued:ghost", "fx-not-plateaued:real", "fx-framing-clamped"),
                         c.fx.flags)

    def test_a_lamps_flame_layer_is_reference(self):
        doc = _fx_case(event=_broken("off"))[0]
        self._as_family(doc, "light", "lit", plate={"litPct": 0.4})
        c = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_PASS, c.verdict, c.reasons)
        self.assertTrue(all(e.reference for e in c.fx.entries))
        self.assertIn(pp.VERDICT_FAIL, {e.verdict for e in c.fx.entries})
        # The plate pictures (the body routes) stay judged.
        doc["views"][0]["colorBlobPx"] = 5000
        self.assertEqual(pp.VERDICT_FAIL, pp.judge_case(doc).verdict)

    def test_a_non_fx_case_has_no_fx_half(self):
        c = pp.judge_case(_state_case("a--default--deployed--00000000")[0])
        self.assertIsNone(c.fx)
        self.assertNotIn("fx", c.to_dict())

    def test_the_block_shape(self):
        doc = _fx_case()[0]
        for what, mutate in (
                ("family", lambda f: f.update(family="warp")),
                ("samples", lambda f: f.update(samples=None)),
                ("a compare key", lambda f: f["views"][0]["compare"].pop("hueL1")),
                ("a side key", lambda f: f["views"][0]["ghost"].pop("corePx")),
                ("a float count", lambda f: f["views"][0]["ghost"].update(corePx=1.5)),
                ("hue bins", lambda f: f["views"][0]["real"].update(hueHistogram=[0.0] * 11)),
                ("an axis key", lambda f: f["views"][0]["axis"].pop("nozzleDiameterPx")),
                ("the size", lambda f: f["views"][0].update(width=10 ** 6)),
                ("no half", lambda f: f["views"][0]["splitHalf"].update(odd=None)),
                ("half samples", lambda f: f["views"][0]["splitHalf"]["odd"].update(samples=16)),
                ("half side key", lambda f: f["views"][0]["splitHalf"]["even"]["ghost"].pop("widthPx")),
                ("half compare", lambda f: f["views"][0]["splitHalf"]["even"].update(compare=None)),
                ("a repeated entry", lambda f: f["views"].append(copy.deepcopy(f["views"][0]))),
                ("an unknown route", lambda f: f["views"][0].update(route="rcs")),
                ("the fx keys", lambda f: f.pop("drive"))):
            with self.subTest(what):
                bad = copy.deepcopy(doc)
                mutate(bad["fx"])
                self.assertTrue(pp.fx_block_problems(bad))
        bad = copy.deepcopy(doc)
        bad["fx"]["views"] = None
        bad["setup"] = {"status": "state-timeout", "detail": None}
        self.assertEqual([], pp.fx_block_problems(bad))


def _flame_of(rgb):
    return {"flame": (_plume(rgb=rgb), _disc(rgb=rgb))}


@contextlib.contextmanager
def owner_flagged(reason="smoke-extra", todo="PARTPARITY-ORBIT-SMOKE-PUFF-NOISE"):
    """An owner's mark on ``reason`` while the block runs (the committed set may be empty)."""
    saved, pp.OWNER_FLAGGED_REASONS = pp.OWNER_FLAGGED_REASONS, {reason: todo}
    try:
        yield
    finally:
        pp.OWNER_FLAGGED_REASONS = saved


class OwnerFlagTests(unittest.TestCase):
    """A flagged reason stays in sight whatever the case's verdict, and moves none."""

    def setUp(self):
        flag = owner_flagged()
        flag.__enter__()
        self.addCleanup(flag.__exit__, None, None, None)

    def _case(self, **kw):
        real = dict(REAL_FX, smoke=FX_DARK)
        return _fx_case(real=real, event={"smoke": REAL_FX["smoke"]}, start={"smoke": REAL_FX["smoke"]}, **kw)[0]

    def test_a_flagged_finding_marks_the_case(self):
        doc = self._case()
        cv = pp.judge_case(doc)
        self.assertIn("event:fx-side-a:smoke:smoke-extra", cv.reasons)
        self.assertEqual(("smoke-extra",), pp.owner_flags(cv))
        self.assertIn("owner-flagged:smoke-extra", cv.fx.flags)
        self.assertEqual(1, pp.fx_case_counts([cv])["flags"]["owner-flagged"])

    def test_a_capped_one_still_marks_it(self):
        doc = self._case()
        for e in pp.fx_entries(doc):
            if e["layer"] == "smoke":
                e["splitHalf"]["odd"]["ghost"] = dict(e["splitHalf"]["odd"]["ghost"], corePx=0)
        cv = pp.judge_case(doc)
        self.assertEqual(pp.VERDICT_WARN, cv.verdict, cv.reasons)
        self.assertEqual(("smoke-extra",), pp.owner_flags(cv))

    def test_the_mark_moves_nothing_and_a_clean_or_reference_case_has_none(self):
        self.assertEqual((), pp.owner_flags(pp.judge_case(_fx_case()[0])))
        self.assertEqual(pp.VERDICT_PASS, pp.judge_case(_fx_case()[0]).verdict)
        ref = pp.judge_case(self._case(reference=pp.REFERENCE_WATERFALL))
        self.assertEqual((pp.VERDICT_REFERENCE, ()), (ref.verdict, pp.owner_flags(ref)))
        flagged = pp.judge_case(self._case())
        saved, pp.OWNER_FLAGGED_REASONS = pp.OWNER_FLAGGED_REASONS, {}
        try:
            plain = pp.judge_case(self._case())
        finally:
            pp.OWNER_FLAGGED_REASONS = saved
        self.assertEqual((plain.verdict, plain.reasons), (flagged.verdict, flagged.reasons))

    def test_the_todo_that_owns_a_committed_flag_is_open(self):
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "docs", "dev",
                            "todo-and-known-bugs.md")
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
        committed = self._committed
        for todo in committed.values():
            self.assertIn("\n## %s:" % todo, text, "flag a reason only while its todo is open")
        # The booster smoke is the ghost's fixed defect, its rest noise: no mark on it.
        self.assertNotIn("smoke-extra", committed)

    _committed = dict(pp.OWNER_FLAGGED_REASONS)


def _fx_control(cid):
    """(sidecar, masks, tiles or None) of one FX control whose evidence meets its requirement."""
    empty = fx.empty_mask(STILE)
    if cid == "ctl-heat-events-dropped" or cid.startswith("ctl-plate"):
        heat = cid == "ctl-heat-events-dropped"
        doc, masks = _state_case(cid, family="heat" if heat else "light",
                                 state="heat-hot" if heat else "lit",
                                 context="alone" if heat else "plate")
        doc["views"][1]["colorBlobPx"] = pp.COLOR_BLOB_FAIL_PX + 1
        doc["fx"] = {k: None for k in pp.FX_KEYS}
        doc["fx"]["family"] = "heat" if heat else "light"
        if not heat:
            doc["fx"]["plate"] = {"litPct": 0.4, "lightType": "Spot", "range": 60,
                                  "spotAngle": 30, "cullingMask": 1}
        return doc, masks, None
    broken = {"ctl-fx-plume-off": "off", "ctl-fx-plume-off-air": "off", "ctl-fx-plume-short": "short",
              "ctl-fx-plume-hue": "hue", "ctl-fx-plume-shift": "shifted",
              "ctl-fx-smoke-dropped": "smoke-dropped"}.get(cid)
    event = _broken(broken) if broken else (NO_FX if cid == "ctl-ghost-absent" else None)
    doc, masks, tiles = _fx_case(cid, event=event, control=True,
                                 real=NO_FX if cid == "ctl-empty" else REAL_FX)
    if cid in ("ctl-empty", "ctl-ghost-absent"):
        masks = (empty if cid == "ctl-empty" else _deployed(), empty, empty, empty)
        doc["views"] = [_state_view(v, *masks) for v in fx.VIEWS]
    return doc, masks, tiles


def _write_fx_case(label_dir, doc, masks, tiles):
    _write_state_case(label_dir, doc, masks)
    if tiles is not None:
        with open(os.path.join(label_dir, doc["fxAtlas"]["file"]), "wb") as fh:
            fh.write(gui_mirror.write_png(*_fx_atlas(doc, tiles)))


def _build_fx_sweep(label_dir, cases, settings=None, outcomes=None, controls=None):
    """A COMPLETE FX sweep: every control of the run's set (the point-light one ``n/a`` by
    default; an ``n/a`` control writes no sidecar) and ``cases``, a list of (sidecar, masks,
    tiles or None). Returns the manifest."""
    settings = dict(settings or {"fx": "all"})
    outcomes = dict({"ctl-plate-light-off-point": pp.CONTROLS_NOT_APPLICABLE}, **(outcomes or {}))
    manifest = _build_state_sweep(label_dir, [c[:2] for c in cases], settings=settings,
                                  control_outcomes=outcomes)
    for cid in pp.control_set(settings):
        for name in os.listdir(label_dir):
            if name.startswith(cid + "."):
                os.remove(os.path.join(label_dir, name))
        if outcomes.get(cid) != pp.CONTROLS_NOT_APPLICABLE:
            _write_fx_case(label_dir, *((controls or {}).get(cid) or _fx_control(cid)))
    for case in cases:
        _write_fx_case(label_dir, *case)
    manifest["progress"]["controls"] = "ok"
    with open(os.path.join(label_dir, pp.MANIFEST_FILE), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh)
    return manifest


def _judge_fx_dir(label_dir, gating=False, read_png=gui_mirror.read_png):
    return pp.evaluate_sweep(_read_dir(label_dir, "pp7"), RUN_ID, gating, read_png,
                             lambda n: os.path.join(label_dir, n))


def _fx_cases(n=3, **kw):
    return [_fx_case("engine%d--default--burn-full--0000aaa%d" % (i, i), **kw) for i in range(n)]


class FxSweepTests(unittest.TestCase):
    """evaluate_sweep over a synthetic FX sweep."""

    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="pp-fx-")
        self.addCleanup(shutil.rmtree, self.root, True)
        self.n = 0

    def _dir(self):
        self.n += 1
        return os.path.join(self.root, "s%d" % self.n)

    def _eval(self, cases, gating=False, read_png=gui_mirror.read_png, **kw):
        d = self._dir()
        _build_fx_sweep(d, cases, **kw)
        return _judge_fx_dir(d, gating, read_png)

    def test_a_clean_sweep_reports(self):
        res = self._eval(_fx_cases())
        self.assertEqual((pp.STATUS_REPORT, []), (res.status, res.problems))
        self.assertEqual(3, res.counts["pass"])
        # Every stochastic control and one sampled case are recomputed from their FX atlas.
        fx_ids = [c for c in res.recompute_ids if c.endswith(".fx")]
        self.assertEqual(8 + 1, len(fx_ids), fx_ids)
        self.assertIn("ctl-fx-plume-hue.fx", fx_ids)

    def test_an_air_run(self):
        case = _fx_case("solid--default--burn-full--air--0000aaaa", context="air")
        res = self._eval([case], settings={"fx": "plume", "contexts": "air"})
        self.assertEqual((pp.STATUS_REPORT, []), (res.status, res.problems))
        self.assertEqual(4, len(res.controls.outcomes))

    def test_a_mutated_fx_atlas_is_invalid_instrument(self):
        cases = _fx_cases(1)
        for name in (cases[0][0]["caseId"], "ctl-fx-real-vs-real"):
            with self.subTest(name):
                d = self._dir()
                _build_fx_sweep(d, cases)
                path = os.path.join(d, name + pp.FX_ATLAS_SUFFIX)
                w, h, bpp, px = gui_mirror.read_png(path)
                buf = bytearray(px)
                i = (24 * w + 20) * 4  # a core pixel of real-flame, fx-side-a
                self.assertEqual(255, buf[i + 3])
                buf[i:i + 4] = b"\0\0\0\0"
                with open(path, "wb") as fh:
                    fh.write(gui_mirror.write_png(w, h, bpp, bytes(buf)))
                res = _judge_fx_dir(d)
                self.assertEqual((pp.STATUS_INVALID, pp.INVALID_INSTRUMENT_SUBKIND),
                                 (res.status, res.invalid_subkind))
                self.assertTrue(all(m.startswith(name + " ") for m in res.recompute_mismatches))
                self.assertTrue(any("real.corePx" in m for m in res.recompute_mismatches))

    def test_a_tampered_sidecar_number_is_invalid_instrument(self):
        cases = _fx_cases(1)
        cases[0][0]["fx"]["views"][0]["compare"]["lengthRatio"] += 0.01
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, self._eval(cases).invalid_subkind)

    def test_a_broken_block_is_invalid_instrument_sampled_or_not(self):
        cases = _fx_cases(3)
        for doc, _m, _t in cases:
            doc["fx"]["views"][0]["splitHalf"]["odd"] = None
        res = self._eval(cases)
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        self.assertEqual(3, sum("splitHalf.odd: block missing" in m for m in res.recompute_mismatches))

    def test_an_unreadable_or_missing_fx_atlas(self):
        cases = _fx_cases(1)
        d = self._dir()
        _build_fx_sweep(d, cases)
        path = os.path.join(d, cases[0][0]["fxAtlas"]["file"])
        with open(path, "wb") as fh:
            fh.write(b"not a png")
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, _judge_fx_dir(d).invalid_subkind)
        os.remove(path)
        res = _judge_fx_dir(d)
        self.assertEqual(pp.INVALID_INCOMPLETE_SUBKIND, res.invalid_subkind)
        self.assertIn("fxAtlas-missing", res.problems[0])
        doc = cases[0][0]
        doc["fxAtlas"] = None
        self.assertEqual("no-fx-atlas", pp.fx_atlas_problem(doc, ()))
        doc["fxAtlas"], doc["strip"] = {"file": "a"}, {"file": "b", "frames": 12}
        self.assertEqual("strip-missing:b", pp.fx_atlas_problem(doc, ("a",)))
        self.assertIsNone(pp.fx_atlas_problem(doc, ("a", "b")))

    def test_a_row_family_that_is_not_the_sidecars(self):
        d = self._dir()
        _build_fx_sweep(d, _fx_cases(1))
        path = os.path.join(d, pp.MANIFEST_FILE)
        with open(path, encoding="utf-8") as fh:
            manifest = json.load(fh)
        manifest["cases"][0]["family"] = "rcs"
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(manifest, fh)
        res = _judge_fx_dir(d)
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        self.assertTrue(any("row family=rcs fx.family=plume" in m for m in res.recompute_mismatches))

    def test_a_capture_that_sees_no_puff_is_invalid_instrument(self):
        kw = dict(family="rcs", state="rcs-fire", rcs=FxJudgeTests.RCS)
        dark = [_fx_case("rcs%d--default--rcs-fire--0000ccc%d" % (i, i), real=NO_FX, **kw) for i in range(2)]
        res = self._eval(dark)
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        self.assertEqual(["rcs: the real side of all 2 captured case(s) drew nothing"], res.recompute_mismatches)
        res = self._eval(dark + [_fx_case("rcs9--default--rcs-fire--0000ccc9", **kw)])
        self.assertEqual((pp.STATUS_REPORT, []), (res.status, res.problems))
        self.assertEqual(2, res.counts["warn"])

    def test_a_malformed_control_gives_a_verdict(self):
        for key in ("route", "view", "layer"):
            for value in ([], {}, None, 3):
                with self.subTest(key=key, value=value):
                    doc = _fx_control("ctl-fx-plume-short")[0]
                    doc["fx"]["views"][2][key] = value
                    self.assertEqual(pp.CONTROLS_BAD, pp.judge_fx_control("ctl-fx-plume-short", doc)[0])
        manifest = {"controls": [{"id": ["ctl-empty"], "outcome": "ok"}], "settings": {"fx": "all"}}
        self.assertFalse(pp.check_completeness(manifest, {}).ok)

    def test_no_png_reader_is_an_instrument_problem(self):
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND,
                         self._eval(_fx_cases(1), read_png=None).invalid_subkind)

    def test_reference_cases_are_counted_apart_and_never_gate(self):
        ref = _fx_case("ref--default--burn-full--0000bbbb", reference="waterfall", event=_broken("off"))
        res = self._eval(_fx_cases(1) + [ref], gating=True)
        self.assertEqual((pp.STATUS_PASS, []), (res.status, res.problems))
        self.assertEqual((2, 1, 0, 1), tuple(res.counts[k] for k in ("cases", "pass", "fail", "reference")))
        row = pp.combine([res], True)
        self.assertFalse(row.part_fail)
        self.assertIn(" reference=1", pp.format_summary(row))
        doc = pp.verdicts_document(res, RUN_ID)
        self.assertEqual({"fail": 1}, doc["fx"]["referenceWouldBe"])
        self.assertEqual({"plume": 2}, doc["fx"]["families"])
        self.assertEqual(pp.FX_THRESHOLDS, doc["fx"]["thresholds"])
        self.assertEqual("reference", doc["cases"][1]["verdict"])
        self.assertEqual([], doc["worst"])
        json.dumps(doc)
        # The same plume without the marker gates.
        self.assertEqual(pp.STATUS_FAIL, self._eval(_fx_cases(1, event=_broken("off")), gating=True).status)

    def test_the_document_of_a_non_fx_sweep_is_unchanged(self):
        d = self._dir()
        _build_state_sweep(d, [_state_case("a--default--deployed--00000000")])
        res = _judge_fx_dir(d)
        self.assertEqual((pp.STATUS_REPORT, []), (res.status, res.problems))
        self.assertNotIn("fx", pp.verdicts_document(res, RUN_ID))
        self.assertNotIn("reference", pp.format_summary(pp.combine([res], False)))
        self.assertNotIn("reference", res.counts)

    def test_the_run_shape(self):
        d = self._dir()
        _build_state_sweep(d, [_fx_cases(1)[0][:2]])
        res = _judge_fx_dir(d)
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        self.assertTrue(any("carries an fx block" in m for m in res.recompute_mismatches))
        res = self._eval([_state_case("a--default--deployed--00000000") + (None,)])
        self.assertEqual(pp.INVALID_INSTRUMENT_SUBKIND, res.invalid_subkind)
        self.assertTrue(any("has no fx block in an fx=all run" in m for m in res.recompute_mismatches))


class FxControlTests(unittest.TestCase):
    """The FX control judges and ``n/a`` (design E.5)."""

    ALL_FX = tuple(dict.fromkeys(pp.CONTROL_IDS_FX_ORBIT + pp.CONTROL_IDS_FX_AIR))

    def test_every_control_meets_its_requirement(self):
        for cid in self.ALL_FX:
            with self.subTest(cid):
                self.assertEqual((pp.CONTROLS_OK, ""), pp.judge_fx_control(cid, _fx_control(cid)[0]))

    def test_every_control_is_bad_on_an_intact_pair(self):
        for cid in self.ALL_FX:
            if cid.startswith("ctl-fx-real-vs-real"):
                continue
            with self.subTest(cid):
                intact = _fx_case(cid, control=True)[0]
                intact["fx"]["plate"] = {"litPct": 0.4}
                self.assertEqual(pp.CONTROLS_BAD, pp.judge_fx_control(cid, intact)[0])

    def test_every_control_is_bad_on_a_setup_fault_or_no_sidecar(self):
        for cid in self.ALL_FX:
            with self.subTest(cid):
                doc = _fx_control(cid)[0]
                doc["setup"] = {"status": "state-timeout", "detail": None}
                self.assertEqual((pp.CONTROLS_BAD, "setup state-timeout"), pp.judge_fx_control(cid, doc))
                self.assertEqual((pp.CONTROLS_BAD, "no sidecar"), pp.judge_fx_control(cid, None))

    def test_the_noise_floor_is_not_vacuous_and_reads_every_measure(self):
        self.assertEqual((pp.CONTROLS_BAD, "no real core"),
                         pp.judge_fx_control("ctl-fx-real-vs-real", _fx_control("ctl-empty")[0]))
        for name, (broken, want) in BROKEN_PLUMES.items():
            with self.subTest(name):
                doc = _fx_case("ctl-fx-real-vs-real", event=broken, control=True)[0]
                self.assertEqual((pp.CONTROLS_BAD, "fails " + ",".join(
                    m for m in pp.FX_MEASURES if m in want)), pp.judge_fx_control("ctl-fx-real-vs-real", doc))

    def test_a_control_that_breaks_a_measure_it_must_pass_is_bad(self):
        doc = _fx_case("ctl-fx-plume-short", event={"flame": (_plume(length=20, rgb=GREEN), _disc())},
                       control=True)[0]
        self.assertEqual((pp.CONTROLS_BAD, "colour fails"), pp.judge_fx_control("ctl-fx-plume-short", doc))
        doc = _fx_control("ctl-fx-plume-hue")[0]
        doc["fx"]["views"][0]["compare"]["lengthRatio"] = 0.5
        self.assertEqual((pp.CONTROLS_BAD, "length fails"), pp.judge_fx_control("ctl-fx-plume-hue", doc))
        doc = _fx_control("ctl-fx-plume-shift")[0]
        for e in doc["fx"]["views"]:
            e["compare"]["lateralOffsetPx"] = 0.0
        self.assertEqual((pp.CONTROLS_BAD, "lateral passes"), pp.judge_fx_control("ctl-fx-plume-shift", doc))

    def test_plume_off_needs_a_recorded_ignition(self):
        doc = _fx_control("ctl-fx-plume-off")[0]
        for events in ([], None, [{"type": "EngineThrottle"}]):
            doc["stateProof"]["events"] = events
            self.assertEqual((pp.CONTROLS_BAD, "no EngineIgnited recorded"),
                             pp.judge_fx_control("ctl-fx-plume-off", doc))

    def test_the_plate_floor_and_the_heat_press(self):
        doc = _fx_control("ctl-plate-light-off")[0]
        for lit in (None, 0.019):
            doc["fx"]["plate"]["litPct"] = lit
            self.assertEqual("the real side is under the plate floor",
                             pp.judge_fx_control("ctl-plate-light-off", doc)[1])
        heat = _fx_control("ctl-heat-events-dropped")[0]
        heat["stateProof"]["reached"] = False
        self.assertEqual(pp.CONTROLS_BAD, pp.judge_fx_control("ctl-heat-events-dropped", heat)[0])

    def test_a_missing_entry_is_bad(self):
        doc = _fx_control("ctl-fx-plume-short")[0]
        doc["fx"]["views"].pop()
        self.assertEqual("the event-route entries are incomplete",
                         pp.judge_fx_control("ctl-fx-plume-short", doc)[1])

    def _sweep(self, **kw):
        with tempfile.TemporaryDirectory(prefix="pp-fxc-") as tmp:
            _build_fx_sweep(os.path.join(tmp, "pp7"), _fx_cases(1), **kw)
            return _judge_fx_dir(os.path.join(tmp, "pp7"))

    def test_n_a_is_met_only_for_an_optional_control(self):
        res = self._sweep()
        self.assertEqual((True, pp.STATUS_REPORT), (res.controls.ok, res.status), res.problems)
        self.assertIn(("ctl-plate-light-off-point", "n/a"), res.controls.outcomes)
        res = self._sweep(outcomes={"ctl-plate-light-off-point": "ok"})
        self.assertEqual((True, pp.STATUS_REPORT), (res.controls.ok, res.status), res.problems)
        for cid in ("ctl-plate-light-off", "ctl-fx-plume-off"):
            with self.subTest(cid):
                res = self._sweep(outcomes={cid: "n/a"})
                self.assertEqual(pp.INVALID_CONTROLS_SUBKIND, res.invalid_subkind)
                self.assertIn("%s:n/a" % cid, res.controls.problems)
        self.assertFalse(pp.control_not_applicable("ctl-plate-light-off-point", "bad"))

    def test_a_recorded_ok_is_re_judged(self):
        res = self._sweep(controls={"ctl-fx-plume-shift": _fx_case("ctl-fx-plume-shift", control=True)})
        self.assertEqual(pp.INVALID_CONTROLS_SUBKIND, res.invalid_subkind)
        self.assertEqual(("ctl-fx-plume-shift:recorded-ok-judged-bad(origin passes)",), res.controls.problems)


class FxPressGateTests(unittest.TestCase):
    """The press gate counts the four FX families (design E.5)."""

    @staticmethod
    def _run(family, state, statuses, context="alone", row_family=True, detail=None):
        cases, sidecars = [], {}
        for i, status in enumerate(statuses):
            cid = "p%d--default--%s--0000000%d" % (i, state, i)
            cases.append({"caseId": cid, "state": state, "context": context,
                          "family": family if row_family else None})
            sidecars[cid] = {"state": state, "stateProof": None, "fx": {"family": family},
                             "setup": {"status": status, "detail": detail}}
        return pp.check_state_press({"cases": cases}, sidecars)

    def test_more_than_half_of_each_family(self):
        for family, state, context in (("plume", "burn-full", "alone"), ("rcs", "rcs-fire", "alone"),
                                       ("heat", "heat-hot", "alone"), ("light", "lit", "plate")):
            for status in pp.PRESS_GATE_STATUSES:
                with self.subTest(family=family, status=status):
                    bad = self._run(family, state, [status, status, "ok"], context)
                    self.assertFalse(bad.ok)
                    self.assertEqual(((family, 3, 2),), bad.families)
                    self.assertTrue(self._run(family, state, [status, "ok"], context).ok)

    def test_the_family_falls_back_to_the_fx_block(self):
        self.assertEqual((("rcs", 1, 1),),
                         self._run("rcs", "rcs-fire", ["state-timeout"], row_family=False).families)

    def test_an_rcs_that_never_fires_is_a_dead_press(self):
        s = pp.SETUP_RCS_INPUT_MISMATCH
        self.assertFalse(self._run("rcs", "rcs-fire", [s], detail="predicted=[0,1] fired=[]").ok)
        # Fired the wrong nozzles: a wrong prediction (an error), not a dead press path.
        self.assertTrue(self._run("rcs", "rcs-fire", [s], detail="predicted=[0,1] fired=[2]").ok)
        self.assertTrue(pp.fx_press_failed(s, None, {"fx": {"rcs": {"fired": []}}}))
        self.assertFalse(pp.fx_press_failed(s, "fired=[]", {"fx": {"rcs": {"fired": [1]}}}))
        self.assertFalse(pp.fx_press_failed("ok", "fired=[]", None))

    def test_a_lamp_that_never_lights_is_a_dead_press(self):
        s = pp.SETUP_PLATE_NOT_LIT
        self.assertFalse(self._run("light", "lit", [s, s, "ok"], "plate").ok)
        self.assertTrue(self._run("light", "lit", [s, "ok"], "plate").ok)

    def test_the_sweep_reads_state_press(self):
        cases = _fx_cases(2)
        for doc, _m, _t in cases:
            doc["setup"] = {"status": "state-timeout", "detail": "no EngineIgnited"}
        with tempfile.TemporaryDirectory(prefix="pp-fxp-") as tmp:
            _build_fx_sweep(os.path.join(tmp, "pp7"), cases)
            res = _judge_fx_dir(os.path.join(tmp, "pp7"))
        self.assertEqual(pp.INVALID_STATE_PRESS_SUBKIND, res.invalid_subkind)
        self.assertEqual(2, res.counts["error"])


if __name__ == "__main__":
    unittest.main()
