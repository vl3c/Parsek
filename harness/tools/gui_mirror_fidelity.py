#!/usr/bin/env python3
"""Measure how close the generated GUI mirror's rendering is to the game frame
it was generated from.

The mirror (`gui_mirror.py`) claims it cannot drift from the game because every
window, control, string and colour on its page comes out of a census artifact.
That claim has, until now, been checked by eye - and the eye found three class
defects (stale tab headings, a container sampling its own child's colour, doubled
text) which means it will have missed others. This is the instrument: it renders
the page's OWN stage in a headless browser and compares the result against the
census PNG, pixel for pixel, inside the Parsek window rects.

It measures the REAL page. The browser loads the very file a reader opens, at a
deep link (`#cap=<capture id>&bare=1`) that strips the chrome and pins the stage
to 1:1 CSS pixels at the frame's own coordinates. There is no second renderer
here to imitate the first and agree with it for the wrong reasons.

What is measured, per capture, then aggregated per window token and per control
class:

  TEXT     - for every text-bearing leaf control, the INK bounding box in the
             frame against the ink bounding box in the mirror, inside the same
             rect: dx, dy, the width ratio, and a `clipped` flag for a mirror run
             that ends at the rect edge where the frame's does not. This is the
             metric that matters: it is the one that catches a font metric that
             is a few per cent wrong, a label drawn at the wrong offset, and text
             drawn twice.
  FILL     - per painted control, the median colour of the frame's own surface
             against the mirror's over the same uncovered points. The mirror
             SAMPLED those colours off the frame, so a delta here means the page
             is not painting what it stored.
  PRESENCE - controls whose rect carries ink in the frame and none in the mirror
             (a slider handle, a toggle tick, an icon, a scrollbar, an arrow),
             and the reverse. Grouped by (kind, style) so each group is one
             fixable class rather than a list of coordinates.
  WINDOW   - the mean absolute luminance difference over the whole window rect,
             for ranking captures only. It is not a pass/fail number: a window
             whose frame carries a scene behind a transparent gutter will never
             read zero.

Usage::

    python harness/tools/gui_mirror_fidelity.py \
        --shots <dir-of-*.gui.json> [--shots ...] \
        --repo <repo-root> \
        --out-dir <a scratch folder>            # report.json + index.html
        [--browser PATH] [--limit N] [--window main] [--capture <id>]

Outputs go where `--out-dir` says and nowhere else; nothing it writes belongs in
the repository. Stdlib only; the browser is an external tool, and every unit test
passes with no browser installed.

Unit tests: `harness/lib/test_gui_mirror_fidelity.py`.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import gui_mirror as gmi  # noqa: E402

FIDELITY_SCHEMA = "parsek-gui-fidelity/1"

# Where a headless Chromium lives on this machine. Probed in order; `--browser`
# overrides. Nothing else in the tool knows a path.
BROWSER_CANDIDATES = (
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
)

# Ink threshold in luminance units. KSP's skin draws #d6d6d6 text on #313131
# buttons and #444444 windows, a separation of 120-160; an anti-aliased glyph
# edge lands 20-60 above the background. 45 takes the glyph body and the stronger
# half of its edge and leaves the surface alone.
INK_THRESHOLD = 45
# A rect needs this many ink pixels before "there is text here" is a measurement
# rather than a stray anti-aliased pixel off a neighbouring border.
MIN_INK_PIXELS = 4
# Controls below this size carry no measurable glyph run.
MIN_NODE_SIDE = 5
# How far in from a control's own rect the ink is looked for.
#
# Not cosmetic. A button's BORDER contrasts with its fill as strongly as its
# label does, so measuring the whole rect made the frame's ink box the whole
# 980 px border of the Close button while the mirror's was the 33 px word inside
# it: dx 471, width ratio 0.03, and a defect report about the one control on the
# page that was perfectly right. KSP's button and box borders are one to two
# pixels; two pixels in leaves the glyphs and drops the frame.
INK_INSET = 2
# A WINDOW's own chrome is thicker than a control's border - KSP draws a bevel
# and a rounded corner, not a one-pixel line - so the two pixels that clear a
# button do not clear a window. Measured on ib-logistics-basic.png over the
# title band: at an inset of 2 the ink box is (2, 10)-(761, 36), the window's own
# left edge; at 4 it is (650, 23)-(761, 36), the title text, and it stays there
# at 6, 8 and 10. 4 is the first inset that reads the title rather than the
# frame around it.
WINDOW_INK_INSET = 4


# --------------------------------------------------------------------------
# pure: geometry
# --------------------------------------------------------------------------

def intersect(a, b):
    """Rect intersection in (x, y, w, h). Returns None when they do not meet."""
    ax, ay, aw, ah = a
    bx, by, bw, bh = b
    x0, y0 = max(ax, bx), max(ay, by)
    x1, y1 = min(ax + aw, bx + bw), min(ay + ah, by + bh)
    if x1 <= x0 or y1 <= y0:
        return None
    return (x0, y0, x1 - x0, y1 - y0)


def inset(rect, by=INK_INSET):
    """The rect with `by` pixels taken off every side, or None when nothing is
    left. See INK_INSET for why the ink is never looked for at the very edge."""
    x, y, w, h = [int(v) for v in rect]
    if w - 2 * by <= 0 or h - 2 * by <= 0:
        return None
    return (x + by, y + by, w - 2 * by, h - 2 * by)


def abs_nodes(root):
    """Every node of one compact root, with its rect made ABSOLUTE and its clip.

    `compact_tree` stores each child's rect relative to its parent, because that
    is what the page's nested absolutely-positioned divs want. A measurement
    against a screen-sized frame wants the screen coordinates back, so they are
    re-accumulated here.

    `clip` is the intersection of every ancestor's rect, which is what the page
    itself does: `.gn` carries `overflow:hidden`, so a control is only visible on
    the page where all of its ancestors contain it. Measuring outside that would
    charge the mirror for pixels it was never going to draw - and where the GAME
    draws outside it (it clips at scroll views and windows, not at every box),
    that shows up as the `clipped` flag on the text metric instead, which is
    where a reader can act on it.
    """
    out = []
    rect0 = (int(root.get("x", 0)), int(root.get("y", 0)),
             int(root.get("w", 0)), int(root.get("h", 0)))
    kids0 = [(int(c.get("x", 0)) + rect0[0], int(c.get("y", 0)) + rect0[1],
              int(c.get("w", 0)), int(c.get("h", 0)))
             for c in (root.get("c") or ())]
    out.append({"k": "window", "s": root.get("s") or "window", "t": "",
                "bg": root.get("bg"), "leaf": not kids0, "depth": 0, "path": "",
                "rect": rect0, "vis": rect0, "kids": kids0})
    if root.get("title"):
        # The title bar, as its own measurable control. Its height is DERIVED -
        # the band between the window's own top and the top of its first child -
        # rather than taken from the page's CSS, so a title bar that is the wrong
        # height shows up here instead of being measured inside the wrong box.
        top = min([k[1] for k in kids0] or [rect0[1] + rect0[3]])
        band = max(1, min(top - rect0[1], rect0[3]))
        out.append({"k": "title", "s": "window", "t": root["title"],
                    "bg": None, "leaf": True, "depth": 0, "path": "title",
                    "rect": (rect0[0], rect0[1], rect0[2], band),
                    "vis": intersect((rect0[0], rect0[1], rect0[2], band), rect0),
                    "kids": []})

    def walk(node, ox, oy, clip, depth, path):
        rect = (int(node.get("x", 0)) + ox, int(node.get("y", 0)) + oy,
                int(node.get("w", 0)), int(node.get("h", 0)))
        kids = node.get("c") or ()
        vis = intersect(rect, clip) if (rect[2] > 0 and rect[3] > 0) else None
        out.append({
            "k": node.get("k") or "label",
            "s": node.get("s") or "",
            "t": node.get("t") or "",
            "bg": node.get("bg"),
            "leaf": not kids,
            "depth": depth,
            "path": path,
            "rect": rect,
            "vis": vis,
            "kids": [(int(c.get("x", 0)) + rect[0], int(c.get("y", 0)) + rect[1],
                      int(c.get("w", 0)), int(c.get("h", 0))) for c in kids],
        })
        nclip = intersect(rect, clip) or (rect[0], rect[1], 0, 0)
        for i, ch in enumerate(kids):
            walk(ch, rect[0], rect[1], nclip, depth + 1, path + "/%d" % i)

    for i, ch in enumerate(root.get("c") or ()):
        walk(ch, rect0[0], rect0[1], rect0, 1, str(i))
    return rect0, out


# --------------------------------------------------------------------------
# pure: pixels
# --------------------------------------------------------------------------

def to_plane(img):
    """One byte of luminance per pixel: `(w, h, bytes)`.

    Built ONCE per image, because the node walk is the whole cost of a run. The
    test-runner capture carries 4217 controls; re-deriving a float luminance from
    three channels inside every one of their rects, three times over, was 47
    seconds of a capture's 12 - the plane plus one scan per node is the same
    measurement in a fifth of the time. The weights are the integer form of the
    same 0.299 / 0.587 / 0.114 the colour sampler uses, so a threshold tuned on
    one reads the same on the other.

    An image already reduced to a plane is returned unchanged, so a caller can
    pass either and a test can pass the readable one.
    """
    if len(img) == 3:
        return img
    w, h, bpp, px = img
    out = bytearray(w * h)
    for i in range(w * h):
        o = i * bpp
        out[i] = (77 * px[o] + 150 * px[o + 1] + 29 * px[o + 2]) >> 8
    return (w, h, bytes(out))


def _plane_median(plane, x0, y0, x1, y1):
    """The median luminance of a rect, off a 256-bucket histogram rather than a
    sort: the rect is scanned once either way, and the sort was the other half of
    the node walk's cost."""
    w, _h, lp = plane
    hist = [0] * 256
    total = 0
    for yy in range(y0, y1):
        row = lp[yy * w + x0: yy * w + x1]
        total += len(row)
        for v in row:
            hist[v] += 1
    if not total:
        return None
    half = total // 2
    run = 0
    for v in range(256):
        run += hist[v]
        if run > half:
            return v
    return 255


def ink_bbox(img, rect, threshold=INK_THRESHOLD, background=None):
    """The bounding box of the pixels inside `rect` that contrast with the rect's
    own background, plus how many there are.

    The background is the rect's MEDIAN luminance and not a constant: the same
    label style is drawn on a #444444 window, a #292929 content box and a #313131
    button across the corpus, and a fixed reference would read one of the three as
    solid ink. A median is also what survives a rect that is mostly glyph - which
    no label rect is - and a one-pixel border run along an edge, which many are.

    Takes an RGB image or a luminance plane from `to_plane`.

    Returns `(x0, y0, x1, y1, count)` with x1/y1 EXCLUSIVE, or None when nothing
    in the rect contrasts.
    """
    plane = to_plane(img)
    w, h, lp = plane
    x, y, rw, rh = [int(v) for v in rect]
    x0b, y0b = max(0, x), max(0, y)
    x1b, y1b = min(w, x + rw), min(h, y + rh)
    if x1b <= x0b or y1b <= y0b:
        return None
    bg = background
    if bg is None:
        bg = _plane_median(plane, x0b, y0b, x1b, y1b)
    if bg is None:
        return None
    lo, hi = bg - threshold, bg + threshold
    ix0 = iy0 = None
    ix1 = iy1 = 0
    count = 0
    for yy in range(y0b, y1b):
        base = yy * w
        row = lp[base + x0b: base + x1b]
        # The row-level short circuit: most control rects are flat background,
        # and min/max over a bytes row is C-speed where the per-pixel loop below
        # is not.
        if min(row) >= lo and max(row) <= hi:
            continue
        for i, v in enumerate(row):
            if lo <= v <= hi:
                continue
            xx = x0b + i
            count += 1
            if ix0 is None or xx < ix0:
                ix0 = xx
            if iy0 is None:
                iy0 = yy
            if xx + 1 > ix1:
                ix1 = xx + 1
            iy1 = yy + 1
    if ix0 is None:
        return None
    return (ix0, iy0, ix1, iy1, count)


def median_rgb(img, rect, exclude=()):
    """The median colour of a control's OWN surface - the points its children do
    not cover - which is the same rule `gui_mirror.sample_colors` reads the
    stored colour with. Comparing on any other set would compare two different
    questions."""
    w, h, bpp, px = img
    x, y, rw, rh = [int(v) for v in rect]
    x0, y0 = max(0, x), max(0, y)
    x1, y1 = min(w, x + rw), min(h, y + rh)
    if x1 <= x0 or y1 <= y0:
        return None
    boxes = [(int(c[0]), int(c[1]), int(c[0]) + int(c[2]), int(c[1]) + int(c[3]))
             for c in (exclude or ()) if int(c[2]) > 0 and int(c[3]) > 0]

    def covered(px_, py_):
        for bx0, by0, bx1, by1 in boxes:
            if bx0 <= px_ < bx1 and by0 <= py_ < by1:
                return True
        return False

    pts = gmi._probe(w, bpp, px, x0, y0, x1, y1, 2, covered if boxes else None)
    if not pts:
        pts = gmi._probe(w, bpp, px, x0, y0, x1, y1, 2, None)
    if not pts:
        return None
    return sorted(pts, key=gmi._lum)[len(pts) // 2]


def rgb_delta(a, b):
    """The worst single channel, not a euclidean blend: a fill that is right in
    two channels and 40 out in the third is wrong by 40, and averaging hides
    exactly the tint errors this is here to find."""
    if a is None or b is None:
        return None
    return max(abs(int(a[i]) - int(b[i])) for i in range(3))


def mean_abs_lum_diff(frame, mirror, rect):
    """The window score. Takes images or planes."""
    fw, fh, fp = to_plane(frame)
    mw, mh, mp = to_plane(mirror)
    x, y, rw, rh = [int(v) for v in rect]
    x0, y0 = max(0, x), max(0, y)
    x1, y1 = min(fw, mw, x + rw), min(fh, mh, y + rh)
    if x1 <= x0 or y1 <= y0:
        return None
    total = 0
    n = 0
    for yy in range(y0, y1):
        a = fp[yy * fw + x0: yy * fw + x1]
        b = mp[yy * mw + x0: yy * mw + x1]
        n += len(a)
        for i, v in enumerate(a):
            total += abs(v - b[i])
    return (total / float(n)) if n else None


# --------------------------------------------------------------------------
# pure: the metrics
# --------------------------------------------------------------------------

def text_metric(frame_box, mirror_box, rect):
    """One text control's TEXT reading.

    `dx`/`dy` are where the mirror's ink starts relative to the frame's (positive
    = the mirror draws further right / further down). `wr` is the mirror's ink
    width over the frame's, so 1.0 is a font metric that matches and 1.06 is six
    per cent wide. `clipped` is the case the width ratio cannot express at all:
    the mirror's run ends AT the rect edge while the frame's ends inside it,
    which is text the page cut off and the game did not. It is deliberately NOT
    "the mirror's run is shorter" - a font a few per cent wide runs into the edge
    and is cut with MORE ink than the frame's, not less, so a width comparison
    would miss exactly the case that loses characters.
    """
    out = {"frame": None, "mirror": None, "dx": None, "dy": None, "wr": None,
           "clipped": False, "status": ""}
    if frame_box:
        out["frame"] = list(frame_box[:4])
    if mirror_box:
        out["mirror"] = list(mirror_box[:4])
    if frame_box is None and mirror_box is None:
        out["status"] = "both-empty"
        return out
    if frame_box is None:
        out["status"] = "mirror-only"
        return out
    if mirror_box is None:
        out["status"] = "frame-only"
        return out
    out["status"] = "both"
    out["dx"] = mirror_box[0] - frame_box[0]
    out["dy"] = mirror_box[1] - frame_box[1]
    fw = frame_box[2] - frame_box[0]
    mw = mirror_box[2] - mirror_box[0]
    out["wr"] = (float(mw) / fw) if fw > 0 else None
    right = rect[0] + rect[2]
    at_edge = mirror_box[2] >= right - 1
    frame_inside = frame_box[2] < right - 1
    out["clipped"] = bool(at_edge and frame_inside)
    return out


def presence_key(node):
    """The grouping key for PRESENCE. Kind plus style plus whether the control
    carries text, because the fix for a textless `button` (an icon the mirror
    draws nothing for) is a different fix from the one for a `button` with a
    label."""
    return "%s|%s|%s" % (node["k"], node["s"] or "-",
                         "text" if node["t"] else "notext")


def percentile(vals, q):
    """Nearest-rank percentile. No interpolation: these are pixel counts, and an
    interpolated 1.5 px offset is not a thing that happened."""
    if not vals:
        return None
    s = sorted(vals)
    if len(s) == 1:
        return s[0]
    # floor(x + 0.5), not round(): Python rounds a half to the EVEN neighbour, so
    # round(4.5) is 4, and a percentile that moves with the parity of the sample
    # size is not a percentile anyone can reason about.
    i = int((q / 100.0) * (len(s) - 1) + 0.5)
    return s[max(0, min(i, len(s) - 1))]


def _stat_block(vals):
    absv = [abs(v) for v in vals]
    return {
        "n": len(vals),
        "p50": percentile(absv, 50),
        "p95": percentile(absv, 95),
        "worst": (max(absv) if absv else None),
        "mean": (sum(absv) / float(len(absv)) if absv else None),
    }


def aggregate(records):
    """The per-window and per-class tables, and the corpus totals.

    `records` is one dict per measured capture, as `measure_capture` returns it.
    Pure, so the tables in the report and the tables on the page are the same
    numbers computed once.
    """
    per_window = defaultdict(lambda: {"captures": 0, "dx": [], "dy": [], "wr": [],
                                      "clipped": 0, "texts": 0, "fill": [],
                                      "frameOnly": 0, "mirrorOnly": 0,
                                      "score": []})
    per_class = defaultdict(lambda: {"texts": 0, "dx": [], "dy": [], "wr": [],
                                     "clipped": 0, "fill": [], "frameOnly": 0,
                                     "mirrorOnly": 0, "nodes": 0})
    totals = {"captures": 0, "texts": 0, "dx": [], "dy": [], "wr": [],
              "clipped": 0, "fill": [], "frameOnly": 0, "mirrorOnly": 0,
              "score": []}

    for rec in records:
        win = rec.get("window") or "-"
        pw = per_window[win]
        pw["captures"] += 1
        totals["captures"] += 1
        if rec.get("score") is not None:
            pw["score"].append(rec["score"])
            totals["score"].append(rec["score"])
        for t in rec.get("texts") or ():
            cls = t["class"]
            pc = per_class[cls]
            pc["texts"] += 1
            pw["texts"] += 1
            totals["texts"] += 1
            if t["status"] == "both":
                for bucket in (pw, pc, totals):
                    bucket["dx"].append(t["dx"])
                    bucket["dy"].append(t["dy"])
                    if t["wr"] is not None:
                        bucket["wr"].append(t["wr"])
                if t["clipped"]:
                    pw["clipped"] += 1
                    pc["clipped"] += 1
                    totals["clipped"] += 1
        for f in rec.get("fills") or ():
            if f["delta"] is None:
                continue
            per_class[f["class"]]["fill"].append(f["delta"])
            pw["fill"].append(f["delta"])
            totals["fill"].append(f["delta"])
        for p in rec.get("presence") or ():
            pc = per_class[p["class"]]
            pc["nodes"] += 1
            if p["side"] == "frame":
                pc["frameOnly"] += 1
                pw["frameOnly"] += 1
                totals["frameOnly"] += 1
            elif p["side"] == "mirror":
                pc["mirrorOnly"] += 1
                pw["mirrorOnly"] += 1
                totals["mirrorOnly"] += 1

    def finish(d):
        out = {k: v for k, v in d.items()
               if k not in ("dx", "dy", "wr", "fill", "score")}
        out["dx"] = _stat_block(d["dx"])
        out["dy"] = _stat_block(d["dy"])
        out["wr"] = {"n": len(d["wr"]),
                     "p50": percentile(d["wr"], 50),
                     "p95": percentile(d["wr"], 95),
                     "worst": (max(d["wr"], key=lambda v: abs(v - 1.0))
                               if d["wr"] else None)}
        out["fill"] = _stat_block(d["fill"])
        if "score" in d:
            out["score"] = _stat_block(d["score"])
        return out

    return {
        "windows": {k: finish(v) for k, v in sorted(per_window.items())},
        "classes": {k: finish(v) for k, v in sorted(per_class.items())},
        "totals": finish(totals),
    }


def worst_captures(records, limit):
    """The captures a reader should look at first: the ones with the most clipped
    text, then the widest text offsets, then the worst window score. Clipping
    ranks first because it is the only one of the three that loses information a
    reader of the page needed."""
    def key(r):
        texts = [t for t in (r.get("texts") or ()) if t["status"] == "both"]
        worst_dx = max([abs(t["dx"]) for t in texts] or [0])
        return (-(r.get("clippedCount") or 0), -worst_dx, -(r.get("score") or 0))
    return sorted(records, key=key)[:limit]


# --------------------------------------------------------------------------
# pure: the browser, located but not run
# --------------------------------------------------------------------------

def find_browser(override=None, candidates=BROWSER_CANDIDATES, exists=None):
    """The headless Chromium to drive, or None.

    `exists` is injected so the unit tests can pin both answers on a machine with
    no browser at all - which is the machine CI runs on.
    """
    ex = exists or os.path.exists
    if override:
        return override if ex(override) else None
    for cand in candidates:
        if ex(cand):
            return cand
    return None


def bare_url(page_path, capture_id, foreign=False):
    """The deep link into the generated page for one capture.

    A capture id is `<runId>/<label>`, which carries a slash and, in the dialog
    lanes, characters a URL fragment would eat, so it is percent-encoded. The
    page's own `parseHash` decodes it back, and `parse_hash_query` below is this
    file's copy of that grammar so a test can hold the two together.
    """
    from urllib.parse import quote
    url = "file:///" + os.path.abspath(page_path).replace(os.sep, "/")
    frag = "cap=%s&bare=1" % quote(capture_id, safe="")
    if foreign:
        frag += "&foreign=1"
    return url + "#" + frag


def parse_hash_query(fragment):
    """The page's `parseHash`, in Python, so a test can prove the two agree about
    the grammar the instrument depends on."""
    from urllib.parse import unquote
    out = {}
    for part in str(fragment or "").lstrip("#").split("&"):
        if not part:
            continue
        if "=" in part:
            k, v = part.split("=", 1)
        else:
            k, v = part, "1"
        if not k:
            continue
        out[unquote(k)] = unquote(v)
    return out


# --------------------------------------------------------------------------
# shell: drive the browser
# --------------------------------------------------------------------------

def _png_complete(path):
    """Whether the file on disk is a whole PNG.

    On Windows `msedge.exe` is a launcher: it returns in tens of milliseconds and
    the screenshot lands half a second later, written by a child process. So
    "the process exited" is not "the file is there", and "the file is there" is
    not "the file is finished". A PNG's only honest end-of-file marker is its
    IEND chunk, which is the last TWELVE bytes - zero length, the type, the CRC.
    """
    try:
        if os.path.getsize(path) < 64:
            return False
        with open(path, "rb") as fh:
            if fh.read(8) != b"\x89PNG\r\n\x1a\n":
                return False
            fh.seek(-12, os.SEEK_END)
            return fh.read(12) == (b"\x00\x00\x00\x00IEND"
                                   b"\xae\x42\x60\x82")
    except OSError:
        return False


# The longest path a headless Chromium will open on Windows. Not a guess and not
# cosmetic: it is `MAX_PATH`, and it is the single cause of the worst failure this
# tool had. The screenshots were named after their capture and written beside the
# report, which lives in a 189-character scratch directory - so
# `...work\2026-09-11_1548__ib-missions-recordings-expanded-advanced.mirror.png`
# came to 264 characters, the launch returned 0, wrote nothing, said nothing on
# stderr, and the run then spent 240 seconds of timeout and retry per capture
# before halting. The captures that failed were exactly the ones with long
# labels. Both the profile and the screenshot therefore live in short
# `tempfile.mkdtemp` directories, and this guard refuses anything that creeps
# back towards the limit rather than letting it read as a browser fault.
MAX_BROWSER_PATH = 240


def browser_argv(browser, url, out_png, size, profile_dir, budget_ms=4000):
    """The argument LIST a headless screenshot is launched with.

    Pure, and a list from end to end: it is never joined into a shell string, so
    no path in it can be re-split on a space or have a backslash eaten.

    `--user-data-dir` is mandatory and must be an ABSOLUTE path. A RELATIVE
    fragment reaches Edge as a fragment, and Edge answers with a modal on the
    owner's desktop - "can't read and write to its data directory" - once per
    launch, which on a 230-capture corpus is 230 dialogs.
    """
    if not profile_dir or not os.path.isabs(profile_dir):
        raise ValueError(
            "the browser needs an absolute --user-data-dir; got %r. A relative "
            "one pops Edge's own \"can't read and write to its data directory\" "
            "dialog on the desktop, once per launch." % (profile_dir,))
    for label, path in (("--user-data-dir", profile_dir),
                        ("--screenshot", out_png)):
        if len(path) >= MAX_BROWSER_PATH:
            raise ValueError(
                "%s is %d characters, at or past the %d this browser can open "
                "on Windows: %r. It would return 0, write nothing and say "
                "nothing. Use a short temp directory."
                % (label, len(path), MAX_BROWSER_PATH, path))
    return [browser, "--headless=new", "--user-data-dir=" + profile_dir,
            "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--disable-extensions", "--hide-scrollbars",
            "--force-device-scale-factor=1",
            "--virtual-time-budget=%d" % int(budget_ms),
            "--window-size=%d,%d" % (int(size[0]), int(size[1])),
            "--screenshot=" + out_png, url]


def check_profile_dir(path, exists=None, writable=None):
    """The profile directory has to be there and be writable BEFORE a launch, or
    the browser says so in a dialog and we would say nothing. Returns "" when it
    is usable, else the reason."""
    ex = exists or os.path.isdir
    wr = writable or (lambda p: os.access(p, os.W_OK))
    if not path or not os.path.isabs(path):
        return "the browser profile path %r is not absolute" % (path,)
    if not ex(path):
        return "the browser profile directory %r does not exist" % (path,)
    if not wr(path):
        return "the browser profile directory %r is not writable" % (path,)
    return ""


def screenshot(browser, url, out_png, size, profile_dir=None, timeout=90.0,
               budget_ms=4000, runner=subprocess.run, sleep=time.sleep):
    """One headless screenshot at 1:1, waited for on disk.

    `runner` and `sleep` are injected so a test can drive this with no browser.
    """
    if os.path.exists(out_png):
        os.remove(out_png)
    why = check_profile_dir(profile_dir)
    if why:
        raise RuntimeError(why)
    cmd = browser_argv(browser, url, out_png, size, profile_dir, budget_ms)
    started = time.time()
    # NOT `capture_output=True`. On Windows `msedge.exe` is a launcher whose
    # detached grandchildren inherit the pipe, so `subprocess.run` waits for an
    # EOF that never comes - and its own `timeout` does not save it, because
    # after killing the direct child it goes back to reading the same pipe. Four
    # worker threads sat in that read with the CPU at zero and no browser
    # running, which looked exactly like a slow corpus. The browser's own
    # diagnostics still reach the error message, through a file.
    err_path = out_png + ".err"
    with open(err_path, "wb") as errfh:
        proc = runner(cmd, stdout=subprocess.DEVNULL, stderr=errfh,
                      timeout=timeout)
    last = -1
    while time.time() - started < timeout:
        if _png_complete(out_png):
            size_now = os.path.getsize(out_png)
            if size_now == last:
                try:
                    os.remove(err_path)
                except OSError:
                    pass
                return out_png
            last = size_now
        sleep(0.05)
    err = b""
    try:
        with open(err_path, "rb") as fh:
            err = fh.read()[-400:]
    except OSError:
        pass
    raise RuntimeError(
        "no screenshot after %.0fs for %s (browser rc=%s, stderr=%r)"
        % (timeout, url, getattr(proc, "returncode", "?"), err))


def _profile_for_thread(pool):
    """One short-path temp profile per WORKER THREAD, created on first use.

    Per thread rather than per queue slot: two captures in flight at once sharing
    one profile directory made the second Edge find the first's singleton lock,
    hand the URL over to it and exit 0 having written nothing - a 120 s timeout
    and a retry on about a quarter of the corpus. A thread only ever has one
    launch outstanding, so its own directory cannot contend, and it stays warm
    across the captures that thread takes.
    """
    key = threading.get_ident()
    with _PROFILE_LOCK:
        path = pool.get(key)
        if path is None:
            path = tempfile.mkdtemp(prefix="parsek-fidelity-")
            pool[key] = path
    return path


_PROFILE_LOCK = threading.Lock()


# --------------------------------------------------------------------------
# shell: measure one capture
# --------------------------------------------------------------------------

def looks_unpainted(cap, mirror_img):
    """Whether the mirror screenshot came back with nothing drawn where the
    capture's own windows are.

    The bare stage is `visibility:hidden` until the page sets `data-ready`, so a
    screenshot taken too early is blank. Rather than a second browser call to
    read that DOM marker, the blankness IS the signal: a window rect with no ink
    at all, when the dump says a window stood there, is a page that never
    finished. Returns the reason, or "".
    """
    looked = False
    for root in cap.get("roots") or ():
        if root.get("foreign"):
            continue
        rect = intersect((int(root.get("x", 0)), int(root.get("y", 0)),
                          int(root.get("w", 0)), int(root.get("h", 0))),
                         (0, 0, cap["screen"][0], cap["screen"][1]))
        if not rect or rect[2] < 8 or rect[3] < 8:
            continue
        looked = True
        box = ink_bbox(mirror_img, rect)
        if box and box[4] >= MIN_INK_PIXELS:
            return ""
    # EVERY window, not the first one. Deciding on the first declined four
    # captures whose first root is a small chrome strip - the watch-mode overlay
    # is a 300x22 bar that legitimately carries no ink of its own - while the
    # Spawn Control and Kerbals windows behind it were drawn perfectly well.
    if not looked:
        return ""
    return ("the mirror screenshot is blank over every window rect - the page "
            "never reached its ready marker")


def measure_capture(cap, frame_img, mirror_img):
    """Every metric for one capture. Pure over two decoded images and one capture
    of the page model, so the whole measurement is testable with synthetic PNGs
    and no browser."""
    wins = []
    for root in cap.get("roots") or ():
        if root.get("foreign"):
            continue
        wins.append(root)
    rec = {
        "id": cap["id"],
        "label": cap["label"],
        "window": cap.get("window"),
        "tab": cap.get("tab"),
        "state": cap.get("state"),
        "mode": cap.get("mode"),
        "runId": cap.get("runId"),
        "fixture": cap.get("fixture"),
        "screen": cap.get("screen"),
        "dialog": bool(cap.get("dialog")),
        "windowRects": [],
        "texts": [],
        "fills": [],
        "presence": [],
        "score": None,
        "clippedCount": 0,
        "skipped": "",
    }
    if not wins:
        rec["skipped"] = "no Parsek window in this capture"
        return rec
    if cap.get("dialog"):
        # A PopupDialog is a centred uGUI canvas that overdraws the window rects
        # in the FRAME and has no control tree at all, so every rect under it
        # would read as a difference the mirror could not have avoided. The page
        # shows the photograph for those, which is the honest rendering; there is
        # nothing here to measure.
        rec["skipped"] = "a uGUI modal overdraws the window rects in the frame"
        return rec

    # One luminance plane per image for the whole node walk. See `to_plane`.
    fplane = to_plane(frame_img)
    mplane = to_plane(mirror_img)
    scores = []
    for root in wins:
        wrect, nodes = abs_nodes(root)
        if wrect[2] <= 0 or wrect[3] <= 0:
            continue
        clipped_to_screen = intersect(
            wrect, (0, 0, cap["screen"][0], cap["screen"][1]))
        if not clipped_to_screen:
            continue
        rec["windowRects"].append(list(wrect))
        sc = mean_abs_lum_diff(fplane, mplane, clipped_to_screen)
        if sc is not None:
            scores.append((sc, clipped_to_screen[2] * clipped_to_screen[3]))

        for n in nodes:
            vis = n["vis"]
            if not vis:
                continue
            vis = intersect(vis, (0, 0, cap["screen"][0], cap["screen"][1]))
            if not vis or vis[2] < MIN_NODE_SIDE or vis[3] < MIN_NODE_SIDE:
                continue
            cls = presence_key(n)

            # The ink is measured on LEAF controls only, and that is a
            # correctness rule before it is a cost one. A container's own rect
            # carries its children's glyphs, so "is there ink in it" answers a
            # question about them, not about it - and a window-sized layout group
            # would be counted present on the strength of one label inside it.
            # It is also where the run's time went: the leaves are small and the
            # containers are the page, so the sum of container rect areas is
            # tens of times the frame, scanned twice.
            fb = mb = None
            ink_rect = inset(vis, WINDOW_INK_INSET
                             if n["k"] in ("window", "title")
                             else INK_INSET) or vis
            if n["leaf"]:
                # ONE scan per image, shared by the TEXT and the PRESENCE
                # reading: they ask the same question of the same rect, and
                # asking it twice was half of what was left.
                fbox = ink_bbox(fplane, ink_rect)
                mbox = ink_bbox(mplane, ink_rect)
                fb = fbox if (fbox and fbox[4] >= MIN_INK_PIXELS) else None
                mb = mbox if (mbox and mbox[4] >= MIN_INK_PIXELS) else None

            # ---- TEXT: leaf controls that carry a string ------------------
            if n["t"] and n["leaf"]:
                m = text_metric(fb, mb, ink_rect)
                m["class"] = cls
                m["rect"] = list(vis)
                m["text"] = n["t"][:60]
                rec["texts"].append(m)
                if m["clipped"]:
                    rec["clippedCount"] += 1

            # ---- FILL: controls the page paints a background for ----------
            if n["bg"]:
                fc = median_rgb(frame_img, vis, n["kids"])
                mc = median_rgb(mirror_img, vis, n["kids"])
                rec["fills"].append({
                    "class": cls, "rect": list(vis), "stored": n["bg"],
                    "frame": (gmi._hex(fc) if fc else None),
                    "mirror": (gmi._hex(mc) if mc else None),
                    "delta": rgb_delta(fc, mc),
                })

            # ---- PRESENCE: ink on one side only --------------------------
            # Leaves only (see above), on the INSET rect, so a control the page
            # draws only a border for does not read as "present".
            fi, mi = bool(fb), bool(mb)
            if n["leaf"] and fi != mi:
                rec["presence"].append({
                    "class": cls, "rect": list(vis),
                    "side": "frame" if fi else "mirror",
                    "text": n["t"][:40],
                })

    if scores:
        area = sum(a for _s, a in scores)
        rec["score"] = sum(s * a for s, a in scores) / float(area) if area else None
    return rec


# --------------------------------------------------------------------------
# shell: the report page
# --------------------------------------------------------------------------

REPORT_CSS = """
body{margin:0;background:#141414;color:#d6d6d6;
  font:13px/1.5 "Segoe UI",system-ui,Arial,sans-serif}
h1{font-size:17px;margin:0 0 4px}h2{font-size:14px;margin:26px 0 6px}
.wrap{padding:14px 18px 60px;max-width:1500px}
p.lead{color:#9a9a9a;font-size:12px;max-width:105ch}
table{border-collapse:collapse;font-size:12px;margin:6px 0 16px}
th,td{border:1px solid #2c2c2c;padding:3px 7px;text-align:left;vertical-align:top}
th{background:#1f1f1f;color:#8b8b8b;font-weight:600}
td.n{font-family:Consolas,monospace;text-align:right}
td.bad{color:#d08a4a}td.worse{color:#c05a5a}
.trip{display:flex;gap:8px;flex-wrap:wrap;align-items:flex-start;margin:6px 0 20px}
.trip figure{margin:0}
.trip figcaption{font-size:11px;color:#8b8b8b;margin-bottom:3px}
.trip img{display:block;border:1px solid #000;image-rendering:pixelated}
code{color:#c8d8a8;font-family:Consolas,monospace}
.small{font-size:11px;color:#8b8b8b}
"""


def _fmt(v, nd=2):
    if v is None:
        return "-"
    if isinstance(v, float):
        return ("%%.%df" % nd) % v
    return str(v)


def _heatmap_png(frame, mirror, rect, quant=8):
    """The absolute luminance difference over one window rect, as an image.

    Red where the mirror is darker than the frame and green where it is brighter,
    because "text a few pixels right" and "text missing" look identical in a
    single-channel heatmap and are different defects.
    """
    w, h, fp = to_plane(frame)
    mw, mh, mp = to_plane(mirror)
    x, y, rw, rh = [int(v) for v in rect]
    x0, y0 = max(0, x), max(0, y)
    x1, y1 = min(w, mw, x + rw), min(h, mh, y + rh)
    cw, ch = max(1, x1 - x0), max(1, y1 - y0)
    out = bytearray(cw * ch * 3)
    for yy in range(y0, y1):
        for xx in range(x0, x1):
            d = fp[yy * w + xx] - mp[yy * mw + xx]
            v = min(255, abs(d) * 2)
            d0 = ((yy - y0) * cw + (xx - x0)) * 3
            if d > 0:
                out[d0] = v            # frame brighter: the mirror is missing it
            else:
                out[d0 + 1] = v        # mirror brighter: the mirror drew extra
    return gmi.write_png(cw, ch, 3, bytes(out), quant)


def _crop_png(img, rect, quant=8):
    w, h, bpp, px = img
    cw, ch, cpx = gmi.crop(w, h, bpp, px, int(rect[0]), int(rect[1]),
                           int(rect[2]), int(rect[3]))
    return gmi.write_png(cw, ch, bpp, cpx, quant)


def _img_tag(blob, caption):
    return ('<figure><figcaption>%s</figcaption>'
            '<img src="data:image/png;base64,%s" alt="%s"></figure>'
            % (gmi.esc(caption), base64.b64encode(blob).decode("ascii"),
               gmi.esc(caption)))


def render_report_html(report, triples):
    """The self-contained report page: worst captures first, then the per-window
    and per-class tables. `triples` is a list of (record, [html img tags])."""
    t = report["aggregate"]["totals"]
    rows = []
    rows.append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
    rows.append('<meta name="viewport" content="width=device-width,initial-scale=1">')
    rows.append("<title>Parsek GUI mirror fidelity</title>")
    rows.append("<style>%s</style></head><body><div class=\"wrap\">" % REPORT_CSS)
    rows.append("<h1>Parsek GUI mirror fidelity</h1>")
    rows.append('<p class="lead">%s captures measured of %s in the corpus'
                ' (%s skipped, listed at the end). Every number here is the'
                ' generated page\'s OWN rendering, photographed in a headless'
                ' browser at the page\'s bare deep link, against the census PNG,'
                ' inside the Parsek window rects only.</p>'
                % (report["measured"], report["captures"], report["skippedCount"]))
    rows.append('<table><tr><th>metric</th><th>n</th><th>p50</th><th>p95</th>'
                '<th>worst</th></tr>')
    rows.append('<tr><td>text ink dx (px)</td><td class="n">%s</td>'
                '<td class="n">%s</td><td class="n">%s</td><td class="n">%s</td></tr>'
                % (t["dx"]["n"], _fmt(t["dx"]["p50"], 0), _fmt(t["dx"]["p95"], 0),
                   _fmt(t["dx"]["worst"], 0)))
    rows.append('<tr><td>text ink dy (px)</td><td class="n">%s</td>'
                '<td class="n">%s</td><td class="n">%s</td><td class="n">%s</td></tr>'
                % (t["dy"]["n"], _fmt(t["dy"]["p50"], 0), _fmt(t["dy"]["p95"], 0),
                   _fmt(t["dy"]["worst"], 0)))
    rows.append('<tr><td>text width ratio</td><td class="n">%s</td>'
                '<td class="n">%s</td><td class="n">%s</td><td class="n">%s</td></tr>'
                % (t["wr"]["n"], _fmt(t["wr"]["p50"], 3), _fmt(t["wr"]["p95"], 3),
                   _fmt(t["wr"]["worst"], 3)))
    rows.append('<tr><td>fill colour delta (worst channel)</td>'
                '<td class="n">%s</td><td class="n">%s</td><td class="n">%s</td>'
                '<td class="n">%s</td></tr>'
                % (t["fill"]["n"], _fmt(t["fill"]["p50"], 0),
                   _fmt(t["fill"]["p95"], 0), _fmt(t["fill"]["worst"], 0)))
    rows.append('<tr><td>window luminance score</td><td class="n">%s</td>'
                '<td class="n">%s</td><td class="n">%s</td><td class="n">%s</td></tr>'
                % (t["score"]["n"], _fmt(t["score"]["p50"]),
                   _fmt(t["score"]["p95"]), _fmt(t["score"]["worst"])))
    rows.append("</table>")
    rows.append('<p class="lead">clipped text runs: <code>%d</code> of'
                ' <code>%d</code> measured text controls &nbsp;|&nbsp; ink in the'
                ' frame and none in the mirror: <code>%d</code> controls'
                ' &nbsp;|&nbsp; the reverse: <code>%d</code></p>'
                % (t["clipped"], t["texts"], t["frameOnly"], t["mirrorOnly"]))

    rows.append("<h2>worst captures</h2>")
    rows.append('<p class="lead">Ranked by clipped runs, then the widest text'
                ' offset, then the window score. The triple is the frame crop,'
                ' the mirror crop and the difference: RED is ink the frame has'
                ' and the mirror does not, GREEN is ink the mirror drew and the'
                ' frame does not.</p>')
    for rec, imgs in triples:
        rows.append("<h3 style=\"font-size:13px;margin:14px 0 2px\">%s</h3>"
                    % gmi.esc(rec["id"]))
        rows.append('<div class="small">window <code>%s</code> | clipped %d |'
                    ' worst dx %s px | score %s</div>'
                    % (gmi.esc(rec.get("window") or "-"), rec["clippedCount"],
                       _fmt(max([abs(x["dx"]) for x in rec["texts"]
                                 if x["status"] == "both"] or [0]), 0),
                       _fmt(rec["score"])))
        rows.append('<div class="trip">%s</div>' % "".join(imgs))

    for title, table_key, label in (("per window", "windows", "window"),
                                    ("per control class", "classes",
                                     "kind | style | text")):
        rows.append("<h2>%s</h2>" % title)
        rows.append('<table><tr><th>%s</th><th>texts</th><th>dx p50</th>'
                    '<th>dx p95</th><th>dx worst</th><th>wr p50</th>'
                    '<th>wr worst</th><th>clipped</th><th>fill p95</th>'
                    '<th>frame-only</th><th>mirror-only</th></tr>' % label)
        tbl = report["aggregate"][table_key]
        order = sorted(tbl.items(),
                       key=lambda kv: (-(kv[1]["clipped"]),
                                       -(kv[1]["frameOnly"]),
                                       -(kv[1]["dx"]["worst"] or 0)))
        for k, v in order:
            rows.append("<tr><td>%s</td><td class=\"n\">%s</td>"
                        "<td class=\"n\">%s</td><td class=\"n\">%s</td>"
                        "<td class=\"n\">%s</td><td class=\"n\">%s</td>"
                        "<td class=\"n\">%s</td><td class=\"n%s\">%s</td>"
                        "<td class=\"n\">%s</td><td class=\"n%s\">%s</td>"
                        "<td class=\"n\">%s</td></tr>"
                        % (gmi.esc(k), v["texts"],
                           _fmt(v["dx"]["p50"], 0), _fmt(v["dx"]["p95"], 0),
                           _fmt(v["dx"]["worst"], 0),
                           _fmt(v["wr"]["p50"], 3), _fmt(v["wr"]["worst"], 3),
                           " worse" if v["clipped"] else "", v["clipped"],
                           _fmt(v["fill"]["p95"], 0),
                           " bad" if v["frameOnly"] else "", v["frameOnly"],
                           v["mirrorOnly"]))
        rows.append("</table>")

    if report.get("skipped"):
        rows.append("<h2>not measured</h2><table><tr><th>capture</th>"
                    "<th>why</th></tr>")
        for s in report["skipped"]:
            rows.append("<tr><td>%s</td><td>%s</td></tr>"
                        % (gmi.esc(s["id"]), gmi.esc(s["skipped"])))
        rows.append("</table>")
    rows.append('<p class="small">%s | page %s | browser %s</p>'
                % (gmi.esc(report["schema"]), gmi.esc(report["page"]),
                   gmi.esc(report.get("browser") or "-")))
    rows.append("</div></body></html>")
    return "\n".join(rows)


def assemble_report(records, skipped, page_path, browser, capture_count):
    return {
        "schema": FIDELITY_SCHEMA,
        "page": page_path,
        "browser": browser,
        "captures": capture_count,
        "measured": len(records),
        "skippedCount": len(skipped),
        "aggregate": aggregate(records),
        "records": records,
        "skipped": skipped,
    }


# --------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--shots", action="append", required=True,
                    help="a `<runId>_<specId>_shots` directory; repeatable")
    ap.add_argument("--scenarios", default=None)
    ap.add_argument("--repo", default=None)
    ap.add_argument("--out-dir", required=True,
                    help="where report.json / index.html / the work files go - "
                         "a scratch folder, never a tracked directory")
    ap.add_argument("--browser", default=None,
                    help="path to a headless Chromium; probed when omitted")
    ap.add_argument("--limit", type=int, default=0,
                    help="measure at most N captures (0 = all)")
    ap.add_argument("--window", default=None, help="only this window token")
    ap.add_argument("--capture", action="append", default=None,
                    help="only this capture id (repeatable)")
    ap.add_argument("--triples", type=int, default=10,
                    help="how many worst captures get a frame/mirror/diff triple")
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("--browser-jobs", type=int, default=1,
                    help="how many browser launches may be in flight at once. "
                         "One by default: four heavy pages at once made Edge "
                         "exit 0 with no screenshot and no stderr, and the run "
                         "is Python-bound anyway")
    ap.add_argument("--budget-ms", type=int, default=8000,
                    help="the browser's virtual-time budget per capture")
    ap.add_argument("--timeout", type=float, default=120.0,
                    help="seconds to wait for one screenshot to land")
    ap.add_argument("--page", default=None,
                    help="reuse an already-generated bare-capable page instead "
                         "of building one")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args(argv)

    browser = find_browser(args.browser)
    if not browser:
        sys.stderr.write(
            "gui-mirror-fidelity: no headless browser found. Probed:\n  %s\n"
            "Pass --browser PATH to one, or install Edge or Chrome. Nothing was "
            "measured.\n" % "\n  ".join(
                ([args.browser] if args.browser else list(BROWSER_CANDIDATES))))
        return 3

    out_dir = os.path.abspath(args.out_dir)
    os.makedirs(out_dir, exist_ok=True)
    # No work directory beside the report: every intermediate the browser touches
    # lives in a short temp path (see MAX_BROWSER_PATH), and the only things that
    # land in `--out-dir` are the report, the page and the index.

    dirs = []
    for d in args.shots:
        import glob as _glob
        for part in ([d] if os.path.isdir(d) else sorted(_glob.glob(d))):
            if os.path.isdir(part):
                dirs.append(part)
    if not dirs:
        raise SystemExit("no shots directories matched")

    scenarios = args.scenarios
    if scenarios is None and args.repo:
        scenarios = os.path.join(args.repo, "harness", "scenarios")

    # The page is built WITHOUT photographs: bare mode never shows one, and a
    # 20 MB page reloaded once per capture is the whole run's cost for nothing.
    t0 = time.time()
    sys.stderr.write("gui-mirror-fidelity: building the page from %d shots "
                     "directories...\n" % len(dirs))
    model = gmi.build_model(dirs, scenarios, repo_root=None, with_photos=False,
                            verbose=args.verbose)
    page = args.page or os.path.join(out_dir, "mirror-bare.html")
    if not args.page:
        with open(page, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(gmi.render_html(model))
    sys.stderr.write("gui-mirror-fidelity: page ready in %.0fs (%d captures)\n"
                     % (time.time() - t0, len(model["captures"])))

    # The census PNG for each capture, found the same way the generator found it.
    png_by_id = {}
    for d in dirs:
        scan = gmi.scan_shots_dir(d, scenarios, want_colors=False)
        for cap in scan["captures"]:
            png_by_id["%s/%s" % (cap["runId"], cap["label"])] = cap["png"]

    caps = list(model["captures"])
    if args.window:
        caps = [c for c in caps if c["window"] == args.window]
    if args.capture:
        want = set(args.capture)
        caps = [c for c in caps if c["id"] in want]
    if args.limit:
        caps = caps[:args.limit]
    if not caps:
        raise SystemExit("no captures matched the filters")

    sys.stderr.write("gui-mirror-fidelity: %d captures, page %s, browser %s\n"
                     % (len(caps), page, browser))

    records, skipped = [], []
    profiles = {}
    shots_dir = tempfile.mkdtemp(prefix="parsek-fidelity-shots-")
    _browser_gate = threading.Semaphore(max(1, args.browser_jobs))
    # A launch that FAILS is the browser's problem, not the mirror's, and Edge
    # reports its own failures in a desktop dialog. Repeating one across a
    # 230-capture corpus would put 230 dialogs in front of whoever owns the
    # machine, so the first two-attempt failure stops the batch.
    halt = {"why": ""}

    def one(i_cap):
        _i, cap = i_cap
        if halt["why"]:
            return {"id": cap["id"], "skipped": "batch stopped: " + halt["why"]}
        png = png_by_id.get(cap["id"])
        if not png or not os.path.exists(png):
            return {"id": cap["id"], "skipped": "no census PNG for this capture"}
        if cap.get("dialog"):
            return {"id": cap["id"],
                    "skipped": "a uGUI modal overdraws the window rects in the "
                               "frame"}
        # Numbered, in a SHORT temp directory - never named after the capture
        # beside the report. See MAX_BROWSER_PATH.
        shot_png = os.path.join(shots_dir, "%04d.png" % _i)
        # One retry with a larger virtual-time budget and a profile of its own.
        # The census page carries every capture's control tree, so a slow load is
        # a real thing on a cold profile, and a failed launch is a failed launch,
        # not a fidelity finding. A capture that fails twice is recorded as
        # unmeasured WITH the error - never dropped, and never fatal to a run
        # that has already photographed two hundred others.
        prof = _profile_for_thread(profiles)
        last = None
        for budget in (args.budget_ms, args.budget_ms * 3):
            try:
                # The browser calls go through a semaphore of their own, one by
                # default. Four heavy pages launched at once - the Missions
                # captures carry scroll views with 23 000 px of rows, so their
                # DOM runs to thousands of nodes - made Edge exit 0 with an
                # EMPTY stderr and no screenshot, every time, on the same four
                # captures; each of them alone renders in 0.6 s. The measurement
                # is Python-bound anyway (the node walk, not the browser), so
                # serialising the launches costs almost nothing and removes the
                # whole failure mode.
                with _browser_gate:
                    screenshot(browser, bare_url(page, cap["id"]), shot_png,
                               cap["screen"], profile_dir=prof,
                               timeout=args.timeout, budget_ms=budget)
                last = None
                break
            except (RuntimeError, OSError, subprocess.SubprocessError) as exc:
                last = exc
        if last is not None:
            halt["why"] = "%s: %s" % (cap["id"], last)
            return {"id": cap["id"], "skipped": "the browser produced no "
                                                "screenshot: %s" % last}
        frame = gmi.read_png(png)
        mirror = gmi.read_png(shot_png)
        if mirror[0] < cap["screen"][0] or mirror[1] < cap["screen"][1]:
            return {"id": cap["id"],
                    "skipped": "the headless frame came back %dx%d, smaller than "
                               "the dump's %dx%d"
                               % (mirror[0], mirror[1], cap["screen"][0],
                                  cap["screen"][1])}
        why = looks_unpainted(cap, mirror)
        if why:
            return {"id": cap["id"], "skipped": why}
        rec = measure_capture(cap, frame, mirror)
        rec["_png"] = png
        rec["_shot"] = shot_png
        return rec

    from concurrent.futures import ThreadPoolExecutor
    done = 0
    with ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        for rec in pool.map(one, list(enumerate(caps))):
            done += 1
            if rec.get("skipped"):
                skipped.append({"id": rec["id"], "skipped": rec["skipped"]})
            else:
                records.append(rec)
            if args.verbose or done % 20 == 0:
                sys.stderr.write("  %d/%d\n" % (done, len(caps)))
    # The browser's own temp profiles, gone: they are a few megabytes each and
    # nothing reads them after the run.
    for path in profiles.values():
        shutil.rmtree(path, ignore_errors=True)
    if halt["why"]:
        sys.stderr.write(
            "gui-mirror-fidelity: STOPPED - the browser failed to produce a "
            "screenshot for %s. Nothing after it was measured; the report below "
            "covers what was.\n" % halt["why"])

    report = assemble_report(records, skipped, page, browser, len(caps))

    triples = []
    for rec in worst_captures(records, max(0, args.triples)):
        if not rec.get("windowRects"):
            continue
        rects = rec["windowRects"]
        x0 = min(r[0] for r in rects)
        y0 = min(r[1] for r in rects)
        x1 = max(r[0] + r[2] for r in rects)
        y1 = max(r[1] + r[3] for r in rects)
        box = intersect((x0, y0, x1 - x0, y1 - y0),
                        (0, 0, rec["screen"][0], rec["screen"][1]))
        if not box:
            continue
        frame = gmi.read_png(rec["_png"])
        mirror = gmi.read_png(rec["_shot"])
        imgs = [_img_tag(_crop_png(frame, box), "the game frame"),
                _img_tag(_crop_png(mirror, box), "the mirror's rendering"),
                _img_tag(_heatmap_png(frame, mirror, box), "the difference")]
        triples.append((rec, imgs))

    for rec in records:
        rec.pop("_png", None)
        rec.pop("_shot", None)
    # The screenshots have been read into the report's triples; they are a
    # gigabyte of intermediate frames and nothing reads them again.
    shutil.rmtree(shots_dir, ignore_errors=True)

    with open(os.path.join(out_dir, "report.json"), "w", encoding="utf-8",
              newline="\n") as fh:
        json.dump(report, fh, indent=1, sort_keys=True)
    html = render_report_html(report, triples)
    with open(os.path.join(out_dir, "index.html"), "w", encoding="utf-8",
              newline="\n") as fh:
        fh.write(html)

    t = report["aggregate"]["totals"]
    sys.stderr.write(
        "gui-mirror-fidelity: %d measured, %d skipped | text n=%s dx p50=%s "
        "p95=%s worst=%s | wr p50=%s worst=%s | clipped=%s | fill p95=%s | "
        "frame-only=%s mirror-only=%s -> %s\n"
        % (len(records), len(skipped), t["dx"]["n"], _fmt(t["dx"]["p50"], 0),
           _fmt(t["dx"]["p95"], 0), _fmt(t["dx"]["worst"], 0),
           _fmt(t["wr"]["p50"], 3), _fmt(t["wr"]["worst"], 3), t["clipped"],
           _fmt(t["fill"]["p95"], 0), t["frameOnly"], t["mirrorOnly"],
           os.path.join(out_dir, "index.html")))
    return 0


if __name__ == "__main__":
    sys.exit(main())
