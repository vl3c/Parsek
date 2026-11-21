"""Synthetic part parity sweep directories for the tests (test_partparity.py, the
fake-KSP smoke legs in test_run_smoke.py).

Writes what the C# capture engine writes for a label (docs/dev/design-part-ghost-
parity.md 5.1 / 5.2): ``manifest.json``, and per case an RGBA atlas PNG (rows real /
ghost, columns the views), a thumbnail and a ``.part.json`` sidecar. The shape
measures of each sidecar are computed by a pixel-by-pixel transcription of
``PartParityMetrics.Measure`` (``reference_measure``), not by the module under test,
so a synthetic sweep checks partparity's recompute against an independent reading of
the C# definitions. Stdlib only; the PNG writer is gui_mirror's.

It also rebuilds the committed real-flight atlases. No image file is committed under
``harness/`` (test_gui_mirror_fidelity.NoCommittedImagesTests), so each atlas of
``testdata/partparity/pp1/`` is stored as ``<caseId>.alpha.gz``: gzip of the magic
``PPA1``, big-endian uint32 width and height, then the alpha plane (width * height
bytes, row-major, top row first, as the PNG stores it). The shape measures read alpha
only and the flight atlases were already re-encoded with zero RGB, so
``materialize_alpha_fixtures`` writes back an RGBA PNG whose pixels equal the original
file's, and the recompute still reads a real PNG through ``gui_mirror.read_png``.
"""

from __future__ import annotations

import gzip
import json
import os
import shutil
import struct
import sys
from typing import Dict, List, Optional, Sequence, Tuple

_TOOLS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools")
if _TOOLS not in sys.path:
    sys.path.insert(0, _TOOLS)

import gui_mirror  # noqa: E402
import partparity  # noqa: E402

VIEWS: Tuple[str, ...] = ("side-a", "side-b", "top", "bottom")

ALPHA_FIXTURE_SUFFIX = ".alpha.gz"
ALPHA_FIXTURE_MAGIC = b"PPA1"
_ALPHA_HEADER = struct.Struct(">II")


def read_alpha_fixture(path: str) -> Tuple[int, int, bytes]:
    """Decode one ``.alpha.gz`` atlas fixture -> (width, height, alpha plane)."""
    with open(path, "rb") as fh:
        raw = gzip.decompress(fh.read())
    head = len(ALPHA_FIXTURE_MAGIC) + _ALPHA_HEADER.size
    if raw[:len(ALPHA_FIXTURE_MAGIC)] != ALPHA_FIXTURE_MAGIC:
        raise ValueError("not a part parity alpha fixture: %s" % path)
    w, h = _ALPHA_HEADER.unpack(raw[len(ALPHA_FIXTURE_MAGIC):head])
    alpha = raw[head:]
    if len(alpha) != w * h:
        raise ValueError("alpha fixture %s holds %d bytes, %dx%d needs %d"
                         % (path, len(alpha), w, h, w * h))
    return w, h, alpha


def alpha_to_rgba(w: int, h: int, alpha: bytes) -> bytes:
    """The RGBA pixels of an atlas with zero RGB and the given alpha plane."""
    px = bytearray(w * h * 4)
    px[3::4] = alpha
    return bytes(px)


def materialize_alpha_fixtures(src_dir: str, dst_dir: str) -> List[str]:
    """Copy a committed fixture label dir into ``dst_dir``, rebuilding every
    ``<caseId>.alpha.gz`` as ``<caseId>.png``. Image files in ``src_dir`` are skipped,
    so the result never depends on one. Returns the rebuilt case ids, sorted."""
    os.makedirs(dst_dir, exist_ok=True)
    rebuilt = []
    for name in sorted(os.listdir(src_dir)):
        src = os.path.join(src_dir, name)
        if not os.path.isfile(src) or name.lower().endswith(".png"):
            continue
        if name.endswith(ALPHA_FIXTURE_SUFFIX):
            case_id = name[:-len(ALPHA_FIXTURE_SUFFIX)]
            w, h, alpha = read_alpha_fixture(src)
            with open(os.path.join(dst_dir, case_id + partparity.ATLAS_SUFFIX), "wb") as fh:
                fh.write(gui_mirror.write_png(w, h, 4, alpha_to_rgba(w, h, alpha)))
            rebuilt.append(case_id)
        else:
            shutil.copyfile(src, os.path.join(dst_dir, name))
    return rebuilt

Mask = List[List[bool]]


def rect_mask(tile: int, x0: int, y0: int, w: int, h: int) -> Mask:
    return [[x0 <= x < x0 + w and y0 <= y < y0 + h for x in range(tile)] for y in range(tile)]


def empty_mask(tile: int) -> Mask:
    return [[False] * tile for _ in range(tile)]


def reference_measure(real: Mask, ghost: Mask, area: float) -> Dict:
    """``PartParityMetrics.Measure`` (shape half), transcribed pixel by pixel."""
    h = len(real)
    w = len(real[0])

    def neigh(mask, x, y):
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dx == 0 and dy == 0:
                    continue
                xx, yy = x + dx, y + dy
                if 0 <= xx < w and 0 <= yy < h and mask[yy][xx]:
                    return True
        return False

    m = {"realPx": 0, "ghostPx": 0, "andPx": 0, "orPx": 0, "realOnlyPx": 0,
         "ghostOnlyPx": 0, "realOnlyTolPx": 0, "ghostOnlyTolPx": 0}
    rt = empty_mask(w)
    gt = empty_mask(w)
    for y in range(h):
        for x in range(w):
            r, g = real[y][x], ghost[y][x]
            m["realPx"] += r
            m["ghostPx"] += g
            m["andPx"] += r and g
            m["orPx"] += r or g
            if r and not g:
                m["realOnlyPx"] += 1
                if not neigh(ghost, x, y):
                    rt[y][x] = True
                    m["realOnlyTolPx"] += 1
            elif g and not r:
                m["ghostOnlyPx"] += 1
                if not neigh(real, x, y):
                    gt[y][x] = True
                    m["ghostOnlyTolPx"] += 1

    def blob(mask):
        seen = set()
        best = 0
        for y in range(h):
            for x in range(w):
                if not mask[y][x] or (x, y) in seen:
                    continue
                stack = [(x, y)]
                seen.add((x, y))
                size = 0
                while stack:
                    cx, cy = stack.pop()
                    size += 1
                    for nx, ny in ((cx - 1, cy), (cx + 1, cy), (cx, cy - 1), (cx, cy + 1)):
                        if 0 <= nx < w and 0 <= ny < h and mask[ny][nx] and (nx, ny) not in seen:
                            seen.add((nx, ny))
                            stack.append((nx, ny))
                best = max(best, size)
        return best

    def bbox(mask):
        pts = [(x, y) for y in range(h) for x in range(w) if mask[y][x]]
        if not pts:
            return None
        xs = [p[0] for p in pts]
        ys = [p[1] for p in pts]
        return [min(xs), min(ys), max(xs) - min(xs) + 1, max(ys) - min(ys) + 1]

    def centroid(mask):
        sx = sy = 0.0
        n = 0
        for y in range(h):
            for x in range(w):
                if mask[y][x]:
                    sx += x + 0.5
                    sy += y + 0.5
                    n += 1
        return None if n == 0 else [sx / n, sy / n]

    def border(mask):
        return any(mask[0]) or any(mask[h - 1]) or any(row[0] or row[w - 1] for row in mask)

    m["realOnlyBlobPx"] = blob(rt)
    m["ghostOnlyBlobPx"] = blob(gt)
    m["realOnlyBlobPct"] = 100.0 * m["realOnlyBlobPx"] / area if area > 0 else None
    m["ghostOnlyBlobPct"] = 100.0 * m["ghostOnlyBlobPx"] / area if area > 0 else None
    m["bbox"] = {"real": bbox(real), "ghost": bbox(ghost)}
    m["centroid"] = {"real": centroid(real), "ghost": centroid(ghost)}
    m["touchesBorder"] = {"real": border(real), "ghost": border(ghost)}
    m["iou"] = m["andPx"] / m["orPx"] if m["orPx"] > 0 else 1.0
    return m


def atlas_bytes(real_tiles: Sequence[Mask], ghost_tiles: Sequence[Mask], tile: int) -> bytes:
    """The RGBA atlas: row 0 the real tiles, row 1 the ghost tiles; a covered pixel is
    opaque grey, an uncovered one transparent black."""
    cols = len(real_tiles)
    width = cols * tile
    out = bytearray(width * 2 * tile * 4)
    for row, tiles in enumerate((real_tiles, ghost_tiles)):
        for c, mask in enumerate(tiles):
            for y in range(tile):
                for x in range(tile):
                    if mask[y][x]:
                        i = ((row * tile + y) * width + c * tile + x) * 4
                        out[i:i + 4] = b"\x80\x80\x80\xff"
    return bytes(out)


def view_record(view: str, real: Mask, ghost: Mask, tile: int, color_blob_px: int = 0) -> Dict:
    area = tile * tile * 0.5
    m = reference_measure(real, ghost, area)
    rec = {"view": view, "width": tile, "height": tile}
    rec.update(m)
    rec.update({"colorOverlapPx": m["andPx"], "colorOverPx": color_blob_px,
                "colorBlobPx": color_blob_px, "colorP99": 0, "colorBlockMax": 0,
                "matte": {"real": {"opaque": m["realPx"], "edge": 0, "inverted": 0},
                          "ghost": {"opaque": m["ghostPx"], "edge": 0, "inverted": 0}}})
    return {k: rec.get(k) for k in partparity.VIEW_KEYS}


def sidecar(case_id: str, part: str, views: List[Dict], tile: int,
            status: str = "ok", skin: Optional[Dict] = None,
            missing: Sequence[Dict] = (), extra: Sequence[Dict] = ()) -> Dict:
    cols = [v["view"] for v in views]
    doc = {
        "schema": partparity.CASE_SCHEMA, "caseId": case_id, "part": part, "title": part,
        "source": "Squad", "partUrl": "Squad/Parts/x/" + part, "modelUrls": [],
        "category": "FuelTank", "tags": [],
        "skin": skin or {"module": None, "moduleId": None, "requested": None, "real": None,
                         "snapshot": "", "ghost": None},
        "state": "stowed", "context": "alone",
        "measure": {"coverageAlpha": 0.5, "tolerancePx": 1, "blobConnectivity": 4},
        "atlas": None if not views else {"file": case_id + partparity.ATLAS_SUFFIX,
                                         "thumb": case_id + partparity.THUMB_SUFFIX,
                                         "tile": tile, "rows": ["real", "ghost"], "cols": cols},
        "framing": None, "ghostRootOffsetM": 0, "layers": {"real": [0], "ghost": [0]},
        "flags": [], "views": views,
        "structure": {"realRenderers": 1, "ghostRenderers": 1, "pairs": 1,
                      "missingOnGhost": list(missing), "extraOnGhost": list(extra),
                      "transformDiffers": [], "materialDiffers": [], "meshDiffers": [],
                      "renderedDiffers": []},
        "setup": {"status": status, "detail": None},
        "log": [],
    }
    return {k: doc.get(k) for k in partparity.CASE_KEYS}


def write_case(label_dir: str, case_id: str, part: str, real: Sequence[Mask],
               ghost: Sequence[Mask], tile: int, status: str = "ok",
               skin: Optional[Dict] = None, missing: Sequence[Dict] = (),
               color_blob_px: int = 0) -> Dict:
    views = [view_record(v, r, g, tile, color_blob_px) for v, r, g in zip(VIEWS, real, ghost)]
    doc = sidecar(case_id, part, views, tile, status, skin, missing)
    if views:
        with open(os.path.join(label_dir, case_id + partparity.ATLAS_SUFFIX), "wb") as fh:
            fh.write(gui_mirror.write_png(len(views) * tile, 2 * tile, 4,
                                          atlas_bytes(real, ghost, tile)))
        with open(os.path.join(label_dir, case_id + partparity.THUMB_SUFFIX), "wb") as fh:
            fh.write(gui_mirror.write_png(1, 1, 4, b"\x00\x00\x00\x00"))
    with open(os.path.join(label_dir, case_id + partparity.SIDECAR_SUFFIX), "w",
              encoding="utf-8") as fh:
        json.dump(doc, fh)
    return doc


def build_sweep(label_dir: str, label: str = "pp1", parts: int = 3, tile: int = 12,
                control_outcome: str = "ok", complete: bool = True,
                part_ghost_shift: int = 0) -> Dict:
    """A small COMPLETE sweep: the eight controls (outcome ``control_outcome``) and
    ``parts`` part cases whose ghost is the real rectangle shifted by
    ``part_ghost_shift`` px. Returns the manifest."""
    os.makedirs(label_dir, exist_ok=True)
    four = lambda m: [m] * len(VIEWS)  # noqa: E731
    body = rect_mask(tile, 3, 3, 6, 6)
    for cid in partparity.CONTROL_IDS:
        if cid == "ctl-empty":
            write_case(label_dir, cid, "fuelTankSmallFlat", four(empty_mask(tile)),
                       four(empty_mask(tile)), tile)
        elif cid == "ctl-ghost-absent":
            write_case(label_dir, cid, "fuelTankSmallFlat", four(body), four(empty_mask(tile)), tile)
        elif cid == "ctl-ghost-scale90":
            write_case(label_dir, cid, "fuelTankSmallFlat", four(body),
                       four(rect_mask(tile, 4, 4, 4, 4)), tile)
        else:
            write_case(label_dir, cid, "mk1pod.v2", four(body),
                       four(rect_mask(tile, 2, 3, 6, 6)), tile)
    cases = []
    for i in range(parts):
        cid = "part%d--default--0000000%d" % (i, i)
        ghost = rect_mask(tile, 3 + part_ghost_shift, 3, 6, 6)
        write_case(label_dir, cid, "part%d" % i, four(body), four(ghost), tile)
        cases.append({"index": i, "caseId": cid, "part": "part%d" % i, "title": "part%d" % i,
                      "source": "Squad", "skin": "default", "state": "stowed",
                      "context": "alone", "sidecar": cid + partparity.SIDECAR_SUFFIX,
                      "setupStatus": "ok", "maxShapeBlobPx": 0, "maxColorBlobPx": 0,
                      "minIou": 1, "missingRendered": 0, "extraRendered": 0})
    outcomes_ok = control_outcome == partparity.CONTROLS_OK
    manifest = {
        "schema": partparity.MANIFEST_SCHEMA, "label": label, "profile": "stock-minimal",
        "gameDataMods": ["Parsek", "Squad"], "dllHash": "0" * 64, "tile": tile,
        "views": list(VIEWS), "measure": {}, "settings": {"skins": "all", "controls": "true"},
        "controls": [{"id": cid, "subject": "s", "required": "r",
                      "outcome": control_outcome if cid == "ctl-ghost-flip" else "ok",
                      "detail": ""} for cid in partparity.CONTROL_IDS],
        "cases": cases,
        "exclusions": [{"part": "flag", "reason": "flag"}],
        "progress": {"complete": complete, "cases": parts, "completeCases": parts,
                     "setupFaults": 0,
                     "controls": partparity.CONTROLS_OK if outcomes_ok else control_outcome,
                     "crashed": []},
    }
    with open(os.path.join(label_dir, partparity.MANIFEST_FILE), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh)
    return manifest
