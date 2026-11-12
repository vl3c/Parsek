#!/usr/bin/env python3
"""Build ONE self-contained interactive HTML page out of map-geometry dumps, so
the map / tracking-station trajectory rendering of looped missions and supply
route ships can be judged by eye against numbers instead of against a memory of the
game.

Inputs, per shots directory (`results/<runId>_shots`, written by the harness):

  * `<label>.map.json` - the `parsek-map-geometry/1` dump the automation-only
    `DumpMapGeometry label=<label>` seam verb writes: every Parsek trajectory
    line, orbit and icon drawn on the map at one instant, plus the planets.
    Contract: `docs/dev/design-map-geometry-dump.md`.
  * `<label>.png` - the `CaptureScreenshot label=<label>` frame with the same
    label, when the lane took one.

Output: ONE html file (no network, no external fonts or scripts) with a rail of
captures on the left and, for the selected capture, to-scale SVG drawings
generated from the numbers (whole system top-down, a top-down and an edge-on
close-up per body that carries a line, and a perspective "as the camera saw
it" view beside the screenshot), plus a measured table per ghost. Facts only:
nothing on the page is a pass / fail verdict. A companion index JSON lists the
lanes, captures and counts.

Usage::

    python harness/tools/map_mirror.py --shots results/<runId>_shots [--shots ...] \
        --out map-mirror.html [--index map-mirror-index.json] \
        [--budget-mb 16] [--no-photos] [--stamp S] [-v]

Stdlib only. Unit tests: `harness/lib/test_map_mirror.py`. Design:
`docs/dev/design-map-mirror.md`.
"""

from __future__ import annotations

import argparse
import base64
import datetime
import glob
import json
import math
import os
import re
import struct
import sys

# The PNG read / re-encode and the script-safe JSON are borrowed from the GUI
# mirror rather than copied: both are pure, stdlib-only and import-side-effect
# free there, and one copy means one place to fix a decoder bug.
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
from gui_mirror import (  # noqa: E402
    json_for_script, read_png, run_sort_key, subsample, write_png)

SCHEMA_ID = "parsek-map-geometry/1"
INDEX_SCHEMA = "parsek-map-mirror-index/1"
DUMP_SUFFIX = ".map.json"
DEFAULT_BUDGET_BYTES = 16 * 1024 * 1024
LINE_KINDS = ("orbit", "polyline", "tail", "bridge")
# Kerbin calendar (stock KSP defaults): 6 h days, 426 day years.
DAY_SECONDS = 6 * 3600
YEAR_DAYS = 426
# The vertical field of view the camera view assumes for a dump without
# `camera.fovDeg` (a writer older than that key).
CAMERA_FOV_DEG = 60.0
# What each `ghosts[].treatment` token means (design-map-geometry-dump.md);
# shown as the token's hover text. An unknown token is shown as written.
TREATMENTS = {
    "StockConic": "a visible Director intent: the stock proto-vessel icon and orbit line",
    "TracedPath": "a visible Director intent: Parsek's own polyline and marker",
    "SuppressedMarker": "no visible intent; the icon-suppression floor drives the marker",
    "None": "the Director's intent for this ghost is hidden this frame",
    "NoIntent": "a proto ghost the Director holds no intent for",
    "DriverOnly": "a pid-0 entry: Parsek lines or a marker drew, no proto vessel",
}


class DumpError(ValueError):
    """A dump the page cannot read; the message names the field."""


# --------------------------------------------------------------------------
# vector math (pure)
# --------------------------------------------------------------------------

def v_add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def v_sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def v_scale(a, s):
    return (a[0] * s, a[1] * s, a[2] * s)


def v_dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def v_cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def v_len(a):
    return math.sqrt(v_dot(a, a))


def v_norm(a):
    n = v_len(a)
    if n == 0:
        raise ValueError("zero-length vector")
    return v_scale(a, 1.0 / n)


def frame_axes(up, right):
    """(R, U, F): `right` made perpendicular to `up`, and F = R x U.

    The cross product is the plain component formula, which is what Unity's
    `Vector3.Cross` computes; with Unity's left-handed world that F is where the
    top of the screen is for a camera looking straight down -U with R to its
    right. So a prograde (counter-clockwise from north) orbit draws
    counter-clockwise, as it does in game."""
    u = v_norm(up)
    r = v_norm(v_sub(right, v_scale(u, v_dot(right, u))))
    return r, u, v_cross(r, u)


def basis_top_down(up, right):
    """Screen x / screen y / view direction for looking straight down -up."""
    r, u, f = frame_axes(up, right)
    return r, f, v_cross(r, f)


def basis_edge_on(up, right, azimuth_deg=0.0):
    """Looking horizontally, with up on screen. Azimuth 0 keeps screen x = right
    (so it lines up under the top-down view) and looks along +F."""
    r, u, f = frame_axes(up, right)
    a = math.radians(azimuth_deg)
    x = v_add(v_scale(r, math.cos(a)), v_scale(f, math.sin(a)))
    return x, u, v_cross(x, u)


def basis_camera(up, right, pitch_deg, heading_deg):
    """The map camera's screen axes from its pitch and heading.

    Modelled on KSP's PlanetariumCamera pivot: rotate by heading about up, then
    by pitch about the pivot's right; the camera sits behind the pivot looking
    along its forward. Pitch 90 is straight down (the top-down view); heading
    turns clockwise seen from above."""
    r, u, f = frame_axes(up, right)
    p, h = math.radians(pitch_deg), math.radians(heading_deg)
    x = v_add(v_scale(r, math.cos(h)), v_scale(f, -math.sin(h)))
    y = v_add(v_add(v_scale(r, math.sin(p) * math.sin(h)), v_scale(u, math.cos(p))),
              v_scale(f, math.sin(p) * math.cos(h)))
    return x, y, v_cross(x, y)


def basis_from_camera(forward, up):
    """The rendering camera's own screen axes from the dump's `camera.forward` /
    `camera.up` (world unit vectors): screen x = up x forward (Unity's right for
    a left-handed world), screen y = up made perpendicular to forward, view
    direction = forward. Raises ValueError for a zero or parallel pair."""
    d = v_norm(forward)
    perp = v_sub(up, v_scale(d, v_dot(up, d)))
    if v_len(perp) < 1e-6 * max(v_len(up), 1e-300):
        raise ValueError("camera up is parallel to forward")
    y = v_norm(perp)
    return v_cross(y, d), y, d


def angle_deg(a, b):
    """The angle between two vectors in degrees (0 .. 180)."""
    c = v_dot(v_norm(a), v_norm(b))
    return math.degrees(math.acos(max(-1.0, min(1.0, c))))


def camera_model_check(axes, camera):
    """How far the page's pitch / heading camera model lands from the camera's
    own recorded frame: {"forwardDeg", "upDeg"} (angles between the model's view
    direction and `camera.forward`, and between its screen up and `camera.up`),
    or None when the dump lacks either half. A fact for reading the dump, and the
    check on `basis_camera`'s convention once a real dump carries both."""
    if not camera or camera.get("forward") is None or camera.get("up") is None:
        return None
    if camera.get("pitchDeg") is None or camera.get("headingDeg") is None:
        return None
    x, y, d = basis_camera(axes["up"], axes["right"], camera["pitchDeg"],
                           camera["headingDeg"])
    return {"forwardDeg": angle_deg(d, camera["forward"]),
            "upDeg": angle_deg(y, camera["up"])}


def close_ring(points):
    """A body's orbit ring is written OPEN (64 points over one period, the first
    not repeated); the closed polyline adds the first point at the end. A ring
    already closed (or too short to be one) is returned as it is."""
    if not points or len(points) < 3:
        return list(points or [])
    if tuple(points[0]) == tuple(points[-1]):
        return list(points)
    return list(points) + [points[0]]


def project(p, basis):
    """A point onto a basis' screen plane: (x, y) in metres, y up."""
    return v_dot(p, basis[0]), v_dot(p, basis[1])


# --------------------------------------------------------------------------
# body frames and measurements (pure)
# --------------------------------------------------------------------------

def body_offset(bodies, frm, to):
    """Where body `frm`'s centre sits relative to body `to`'s centre. Every
    `pos` in the dump is relative to the root, so the chain is one subtraction;
    named for the chain so a future dump that writes parent-relative positions
    changes one function."""
    return v_sub(bodies[frm]["pos"], bodies[to]["pos"])


def to_frame(bodies, frm, point, to):
    """A point given relative to body `frm` -> relative to body `to`."""
    if frm == to:
        return tuple(point)
    return v_add(body_offset(bodies, frm, to), point)


def is_descendant(bodies, name, ancestor):
    """Whether body `name` is `ancestor` or orbits inside it (moon of, ...)."""
    seen = set()
    while name is not None and name not in seen:
        if name == ancestor:
            return True
        seen.add(name)
        name = (bodies.get(name) or {}).get("parent")
    return False


def point_segment_distance(p, a, b):
    ab = v_sub(b, a)
    den = v_dot(ab, ab)
    if den == 0:
        return v_len(v_sub(p, a))
    t = max(0.0, min(1.0, v_dot(v_sub(p, a), ab) / den))
    return v_len(v_sub(p, v_add(a, v_scale(ab, t))))


def point_polyline_distance(p, pts):
    """(distance, index of the nearest segment's first vertex); (None, None) for
    an empty polyline. One point is a polyline of one vertex."""
    if not pts:
        return None, None
    if len(pts) == 1:
        return v_len(v_sub(p, pts[0])), 0
    best, idx = None, None
    for i in range(len(pts) - 1):
        d = point_segment_distance(p, pts[i], pts[i + 1])
        if best is None or d < best:
            best, idx = d, i
    return best, idx


def max_deviation(points, reference):
    """The largest distance from any drawn vertex to the reference polyline."""
    if not points or not reference:
        return None
    return max(point_polyline_distance(p, reference)[0] for p in points)


def altitude_range(points, radius):
    """(lowest, highest) distance above a sphere of `radius`, over the vertices."""
    if not points:
        return None, None
    alts = [v_len(p) - radius for p in points]
    return min(alts), max(alts)


# How far (relative) a drawn orbit line's radii may sit from its conic's apsides.
CONIC_FIT_TOLERANCE = 0.02


def conic_apsis_radii(conic):
    """(periapsis radius, apoapsis radius or None for an open orbit), from the body
    centre, or (None, None) without a usable conic."""
    if not conic:
        return None, None
    sma, ecc = conic.get("sma"), conic.get("ecc")
    if sma is None or ecc is None or ecc < 0.0:
        return None, None
    rp = sma * (1.0 - ecc)
    if not rp > 0.0:
        return None, None
    return rp, (sma * (1.0 + ecc) if ecc < 1.0 else None)


def conic_fit(points, conic, clipped, soi=None):
    """Whether a drawn orbit line's vertices are what a draw of its own conic in
    this frame would hold, judged by their distances from the body centre only
    (cheap and frame-free). It catches a line drawn from a stale frame's vertices:
    offset off the body, shaped like an older orbit, or an arc clipped in an earlier
    frame while this frame says stock drew the whole conic. The rule, with a
    relative tolerance of CONIC_FIT_TOLERANCE:

      * drawn whole by stock (`clipped` false or absent): stock samples the whole
        ellipse, and a hyperbola over a range symmetric about periapsis, so the
        nearest vertex is within the tolerance of the periapsis radius, and for an
        ellipse the farthest within the tolerance of the apoapsis radius;
      * clipped to an arc by Parsek (`clipped` true): an arc need not reach either
        apsis, so every vertex lies between the apsis radii widened by the
        tolerance; an open orbit has no apoapsis, so its arc is bounded above by
        the body's sphere of influence instead (when the dump names one).

    Returns None when there is nothing to check, else a dict with `fits`, the
    measured `minR` / `maxR`, the conic's `rp` / `ra` (None when open) and the
    `rule` applied, in words."""
    rp, ra = conic_apsis_radii(conic)
    if rp is None or not points:
        return None
    radii = [v_len(p) for p in points]
    lo, hi = min(radii), max(radii)
    tol = CONIC_FIT_TOLERANCE
    pct = "%g%%" % (100 * tol)
    if clipped:
        upper = ra if ra is not None else soi
        fits = lo >= (1.0 - tol) * rp and (upper is None or hi <= (1.0 + tol) * upper)
        if ra is not None:
            rule = "clipped arc: every vertex between the apsis radii (+-%s)" % pct
        elif soi is not None:
            rule = ("clipped open arc: every vertex between periapsis and the sphere of "
                    "influence (+-%s)" % pct)
        else:
            rule = "clipped open arc: no vertex nearer than periapsis (-%s)" % pct
    elif ra is not None:
        fits = abs(lo - rp) <= tol * rp and abs(hi - ra) <= tol * ra
        rule = "whole ellipse: nearest and farthest vertex within %s of the apsis radii" % pct
    else:
        fits = abs(lo - rp) <= tol * rp
        rule = "whole open orbit: nearest vertex within %s of the periapsis radius" % pct
    return {"fits": bool(fits), "minR": lo, "maxR": hi, "rp": rp, "ra": ra, "rule": rule}


def conic_apsides(conic, radius):
    """(periapsis altitude, apoapsis altitude or None for an open orbit)."""
    if not conic:
        return None, None
    sma, ecc = conic.get("sma"), conic.get("ecc")
    if sma is None or ecc is None:
        return None, None
    pe = sma * (1.0 - ecc) - radius
    ap = sma * (1.0 + ecc) - radius if ecc < 1.0 else None
    return pe, ap


def _sig(x):
    a = abs(x)
    if a >= 100:
        return "%.0f" % x
    if a >= 10:
        return "%.1f" % x
    return "%.2f" % x


def format_distance(m):
    """Metres -> a short string in m / km / Mm / Gm (3 significant figures)."""
    if m is None:
        return "-"
    # Unit picked on the value as it will print, so 999.9999 reads 1.00 km.
    a = float("%.3g" % abs(m))
    for unit, f in (("Gm", 1e9), ("Mm", 1e6), ("km", 1e3)):
        if a >= f:
            return "%s %s" % (_sig(m / f), unit)
    if a == 0:
        return "0 m"
    if a < 0.01:
        return "%.1g m" % m
    return "%s m" % _sig(m)


def format_ut(ut):
    """Game seconds -> `Y1 D058 01:02:03` on the stock Kerbin calendar."""
    if ut is None:
        return "-"
    neg = ut < 0
    t = abs(ut)
    year = int(t // (DAY_SECONDS * YEAR_DAYS))
    rem = t - year * DAY_SECONDS * YEAR_DAYS
    day = int(rem // DAY_SECONDS)
    rem -= day * DAY_SECONDS
    hh = int(rem // 3600)
    mm = int((rem - hh * 3600) // 60)
    ss = int(rem - hh * 3600 - mm * 60)
    return "%sY%d D%03d %02d:%02d:%02d" % ("-" if neg else "", year + 1, day + 1, hh, mm, ss)


def format_duration(sec):
    if sec is None:
        return "-"
    sec = abs(sec)
    d = int(sec // DAY_SECONDS)
    rem = sec - d * DAY_SECONDS
    h = int(rem // 3600)
    m = int((rem - h * 3600) // 60)
    s = rem - h * 3600 - m * 60
    if d:
        return "%dd %dh %02dm" % (d, h, m)
    if h:
        return "%dh %02dm" % (h, m)
    if m:
        return "%dm %02ds" % (m, int(s))
    return "%.1fs" % s


# --------------------------------------------------------------------------
# reading a dump
# --------------------------------------------------------------------------

def _num(v, where, required=True):
    if v is None and not required:
        return None
    if isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v):
        raise DumpError("%s is %r, expected a finite number" % (where, v))
    return float(v)


def _vec(v, where):
    if not isinstance(v, list) or len(v) != 3:
        raise DumpError("%s is %r, expected [x, y, z]" % (where, _short(v)))
    return tuple(_num(c, "%s[%d]" % (where, i)) for i, c in enumerate(v))


def proto_icon_faded(drawn, source, lines):
    """True when a drawn proto icon is invisible because stock faded its orbit line.

    Stock's icon colour takes its alpha from the orbit renderer's `lineOpacity`, the
    same value the dump writes as the orbit line's `opacity`. A ghost has one proto
    orbit line; with no orbit line or no opacity key (an older dump) the dump's own
    `drawn` reading stands.
    """
    if not drawn or source != "proto":
        return False
    opacities = [ln["opacity"] for ln in lines
                 if ln["kind"] == "orbit" and ln["opacity"] is not None]
    return bool(opacities) and all(o == 0.0 for o in opacities)


def _points(v, where, required=True):
    if v is None and not required:
        return None
    if not isinstance(v, list):
        raise DumpError("%s is %r, expected a list of [x, y, z]" % (where, _short(v)))
    return [_vec(p, "%s[%d]" % (where, i)) for i, p in enumerate(v)]


def _short(v):
    text = repr(v)
    return text if len(text) <= 60 else text[:57] + "..."


def _str(v, default=""):
    return v if isinstance(v, str) else default


def _list(obj, key, where):
    v = obj.get(key)
    if v is None:
        return None
    if not isinstance(v, list):
        raise DumpError("%s.%s is %r, expected a list" % (where, key, _short(v)))
    return v


def _obj(v, where):
    if not isinstance(v, dict):
        raise DumpError("%s is %r, expected an object" % (where, _short(v)))
    return v


def validate_dump(obj):
    """A parsed dump -> (clean dump, notes). Raises DumpError, naming the field,
    on a wrong schema or a shape the page cannot draw. Optional fields may be
    absent; a recoverable oddity (an unknown body name on a line, a count that
    disagrees with the file) is a note on the page instead."""
    _obj(obj, "the file")
    schema = obj.get("schema")
    if schema != SCHEMA_ID:
        raise DumpError("schema is %r, expected %r" % (schema, SCHEMA_ID))
    notes = []
    raw_bodies = _list(obj, "bodies", "dump")
    if not raw_bodies:
        raise DumpError("bodies is missing or empty")
    bodies = []
    names = set()
    for i, b in enumerate(raw_bodies):
        w = "bodies[%d]" % i
        _obj(b, w)
        name = b.get("name")
        if not isinstance(name, str) or not name:
            raise DumpError("%s.name is %r, expected a body name" % (w, name))
        if name in names:
            raise DumpError("%s.name %r appears twice" % (w, name))
        names.add(name)
        bodies.append({
            "name": name,
            "parent": b.get("parent") if isinstance(b.get("parent"), str) else None,
            "radius": _num(b.get("radius"), w + ".radius"),
            "soi": _num(b.get("soi"), w + ".soi", required=False),
            # The root sits at the origin by definition; tolerate its pos left out.
            "pos": (_vec(b.get("pos"), w + ".pos")
                    if b.get("pos") is not None or isinstance(b.get("parent"), str)
                    else (0.0, 0.0, 0.0)),
            "orbit": _points(b.get("orbit"), w + ".orbit", required=False),
        })
    by_name = {b["name"]: b for b in bodies}
    root = obj.get("root")
    if not isinstance(root, str) or root not in by_name:
        roots = [b["name"] for b in bodies if b["parent"] is None]
        if len(roots) != 1:
            raise DumpError("root is %r and no single parentless body stands in" % (root,))
        notes.append("root %r is not a body; using %r, the one body without a parent"
                     % (root, roots[0]))
        root = roots[0]
    for b in bodies:
        if b["parent"] is not None and b["parent"] not in by_name:
            notes.append("body %s names parent %r, which is not in bodies; its orbit "
                         "ring is not drawn" % (b["name"], b["parent"]))
            b["orbit"] = None

    def known(body, where):
        if body in by_name:
            return True
        notes.append("%s is relative to %r, which is not in bodies; it is not drawn "
                     "or measured" % (where, body))
        return False

    raw_ghosts = _list(obj, "ghosts", "dump")
    if raw_ghosts is None:
        raise DumpError("ghosts is missing")
    ghosts = []
    for gi, g in enumerate(raw_ghosts):
        w = "ghosts[%d]" % gi
        _obj(g, w)
        icon = g.get("icon") if isinstance(g.get("icon"), dict) else {}
        body = _str(g.get("body"))
        lines = []
        for li, ln in enumerate(_list(g, "lines", w) or []):
            lw = "%s.lines[%d]" % (w, li)
            _obj(ln, lw)
            lbody = ln.get("body")
            if not isinstance(lbody, str) or not lbody:
                raise DumpError("%s.body is %r, expected a body name" % (lw, lbody))
            ut = ln.get("ut")
            if ut is not None:
                if not isinstance(ut, list) or len(ut) != 2:
                    raise DumpError("%s.ut is %r, expected [t0, t1]" % (lw, _short(ut)))
                ut = [_num(ut[0], lw + ".ut[0]"), _num(ut[1], lw + ".ut[1]")]
            conic = ln.get("conic") if isinstance(ln.get("conic"), dict) else None
            if conic is not None:
                conic = {k: _num(conic.get(k), "%s.conic.%s" % (lw, k), required=False)
                         for k in ("sma", "ecc", "incDeg", "lanDeg", "argPeDeg",
                                   "meanAnomalyAtEpoch", "epoch")}
            kind = _str(ln.get("kind"), "?")
            if kind not in LINE_KINDS:
                notes.append("%s has kind %r, which the page does not know; drawn as "
                             "a polyline" % (lw, kind))
            opacity = _num(ln.get("opacity"), lw + ".opacity", required=False)
            if opacity is not None and not 0.0 <= opacity <= 1.0:
                notes.append("%s.opacity is %r, outside 0..1; read as unknown" % (lw, opacity))
                opacity = None
            clipped = ln.get("clipped")
            lines.append({
                "kind": kind,
                "body": lbody,
                "known": known(lbody, lw),
                "active": bool(ln.get("active")),
                # Stock's zoom fade (orbit lines; absent in older dumps): 0 means stock
                # did not draw the line this frame, whatever the mesh flag said.
                "opacity": opacity,
                "drawMode": _str(ln.get("drawMode")) or None,
                "clipped": clipped if isinstance(clipped, bool) else None,
                "points": _points(ln.get("points"), lw + ".points"),
                "ut": ut,
                "conic": conic,
                "reference": _points(ln.get("reference"), lw + ".reference", required=False),
                "referenceKind": _str(ln.get("referenceKind")),
            })
        ipos = icon.get("pos")
        icon_drawn = bool(icon.get("drawn"))
        # Stock tints the proto icon with its orbit line's fade opacity
        # (OrbitRendererBase.objectNode_OnUpdateIcon: colour alpha = lineOpacity), so an
        # icon whose orbit line stock faded to 0 is not on screen, whatever the dump's
        # renderer-enabled reading says. A marker icon is Parsek's own and does not fade.
        icon_faded = proto_icon_faded(icon_drawn, _str(icon.get("source")), lines)
        md = g.get("markerDecision") if isinstance(g.get("markerDecision"), dict) else None
        if md is not None:
            md = {k: bool(md.get(k)) for k in
                  ("draw", "directorTracedPath", "polylineOwning", "iconSuppressed")}
        ghosts.append({
            "recordingId": _str(g.get("recordingId")),
            # A pid-0 entry may carry no vessel name; its recording id stands in.
            "vesselName": _str(g.get("vesselName")) or _str(g.get("recordingId"), "(unnamed)"),
            "pid": g.get("pid") if isinstance(g.get("pid"), int) else None,
            "committedIndex": (g.get("committedIndex")
                               if isinstance(g.get("committedIndex"), int) else None),
            "sampleUT": _num(g.get("sampleUT"), w + ".sampleUT", required=False),
            "loopCycle": g.get("loopCycle") if isinstance(g.get("loopCycle"), int) else None,
            "treatment": _str(g.get("treatment")),
            "body": body,
            "bodyKnown": (body in by_name) if body else False,
            "treatmentText": TREATMENTS.get(_str(g.get("treatment")), ""),
            "icon": {"drawn": icon_drawn and not icon_faded,
                     "faded": icon_faded,
                     "pos": _vec(ipos, w + ".icon.pos") if ipos is not None else None,
                     "source": _str(icon.get("source"))},
            "markerDecision": md,
            "lines": lines,
        })
        if ghosts[-1]["icon"]["pos"] is not None and body not in by_name:
            notes.append("%s.icon is relative to %r, which is not in bodies; it is not "
                         "drawn or measured" % (w, body))
    if obj.get("routeLines"):
        notes.append("the dump carries a routeLines block, which the writer stopped "
                     "emitting; it is not drawn, measured or counted")

    cam = obj.get("camera") if isinstance(obj.get("camera"), dict) else None
    if cam is None:
        notes.append("no camera block; the camera view starts top-down on the root")
        cam = {}
    camera = {"target": _str(cam.get("target")),
              "pitchDeg": _num(cam.get("pitchDeg"), "camera.pitchDeg", required=False),
              "headingDeg": _num(cam.get("headingDeg"), "camera.headingDeg", required=False),
              "distance": _num(cam.get("distance"), "camera.distance", required=False),
              "forward": (_vec(cam.get("forward"), "camera.forward")
                          if cam.get("forward") is not None else None),
              "up": (_vec(cam.get("up"), "camera.up") if cam.get("up") is not None else None),
              "fovDeg": _num(cam.get("fovDeg"), "camera.fovDeg", required=False)}
    if camera["forward"] is not None or camera["up"] is not None:
        try:
            if camera["forward"] is None or camera["up"] is None:
                raise ValueError("half")
            basis_from_camera(camera["forward"], camera["up"])
        except ValueError:
            notes.append("camera.forward / camera.up are missing one half, zero or "
                         "parallel; the camera view falls back to pitch and heading")
            camera["forward"] = camera["up"] = None
    if camera["fovDeg"] is not None and not 0.0 < camera["fovDeg"] < 180.0:
        notes.append("camera.fovDeg %r is not a field of view; the camera view assumes "
                     "%g deg" % (camera["fovDeg"], CAMERA_FOV_DEG))
        camera["fovDeg"] = None
    axes_in = obj.get("axes") if isinstance(obj.get("axes"), dict) else None
    if axes_in is None:
        notes.append("no axes block; assumed up = +y, right = +x")
        axes = {"up": (0.0, 1.0, 0.0), "right": (1.0, 0.0, 0.0)}
    else:
        axes = {"up": _vec(axes_in.get("up"), "axes.up"),
                "right": _vec(axes_in.get("right"), "axes.right")}
        try:
            frame_axes(axes["up"], axes["right"])
        except ValueError:
            raise DumpError("axes.up and axes.right are zero or parallel")

    counts = obj.get("counts") if isinstance(obj.get("counts"), dict) else {}
    # The writer's `lines` / `points` count the ghost lines; body rings and
    # reference paths are context and are not counted.
    held = {"ghosts": len(ghosts),
            "lines": sum(len(g["lines"]) for g in ghosts),
            "points": sum(len(ln["points"]) for g in ghosts for ln in g["lines"])}
    for key in ("ghosts", "lines", "points"):
        if key in counts and counts.get(key) != held[key]:
            notes.append("counts.%s says %r, the file holds %d" % (key, counts.get(key),
                                                                  held[key]))
    unknown = counts.get("unknownBodyItems")
    unknown = unknown if isinstance(unknown, int) and not isinstance(unknown, bool) else None
    if unknown:
        notes.append("the writer skipped %d item%s relative to a body it could not name "
                     "(counts.unknownBodyItems)" % (unknown, "" if unknown == 1 else "s"))
    dropped = counts.get("dropped") if isinstance(counts.get("dropped"), dict) else None
    if dropped is not None:
        dropped = {k: dropped.get(k) for k in ("ghosts", "lines", "bodies")
                   if isinstance(dropped.get(k), int) and not isinstance(dropped.get(k), bool)}
    if counts.get("truncated"):
        notes.append("the writer TRUNCATED this dump (counts.truncated = true): "
                     + (", ".join("%s %d" % (k, v) for k, v in dropped.items() if v)
                        if dropped and any(dropped.values())
                        else "some entries") + " not written")
    return {
        "label": _str(obj.get("label")),
        "scene": _str(obj.get("scene")),
        "ut": _num(obj.get("ut"), "ut", required=False),
        "frame": obj.get("frame") if isinstance(obj.get("frame"), int) else None,
        "warpRate": _num(obj.get("warpRate"), "warpRate", required=False),
        "camera": camera,
        "axes": axes,
        "root": root,
        "bodies": bodies,
        "ghosts": ghosts,
        "counts": {"declared": dict({k: counts.get(k) for k in
                                     ("ghosts", "lines", "points", "truncated")},
                                    unknownBodyItems=unknown, dropped=dropped),
                   "held": held},
    }, notes


def load_dump(path):
    try:
        with open(path, "r", encoding="utf-8") as fh:
            obj = json.load(fh)
    except UnicodeDecodeError as exc:
        raise DumpError("not UTF-8 text: %s" % exc)
    except ValueError as exc:
        raise DumpError("not JSON: %s" % exc)
    return validate_dump(obj)


# --------------------------------------------------------------------------
# what the page measures
# --------------------------------------------------------------------------

def line_order(lines):
    """Indices with every timed line first, by start time, then end time, then
    dump position, and the untimed ones after them in dump order. That is the
    writer's own order (`MapGeometryDecisions.OrderLines`), so on writer output it
    is the dump order; it is re-derived so a hand-made or older file reads the
    same way."""
    idx = list(range(len(lines)))
    idx.sort(key=lambda i: ((0, lines[i]["ut"][0], lines[i]["ut"][1], i) if lines[i].get("ut")
                            else (1, 0.0, 0.0, i)))
    return idx


def is_drawn(line, bodies):
    """An inactive line is listed, never drawn or measured: the writer sends it
    with no points (the renderer's cache entry exists, nothing was drawn). A line
    at stock fade opacity 0 was not drawn either (an older writer may still mark
    it active with a stale frame's points). A line relative to a body the dump
    does not list cannot be placed."""
    if line.get("opacity") is not None and line["opacity"] <= 0.0:
        return False
    return bool(line["active"] and line["points"] and line["body"] in bodies)


def is_faded(line):
    """A line the dump names as at stock fade opacity 0: not drawn this frame."""
    return line.get("opacity") is not None and line["opacity"] <= 0.0


def _m(v):
    return {"m": v, "text": format_distance(v)}


def measure_ghost(ghost, bodies):
    """The facts table for one ghost (values in metres plus their text)."""
    lines = ghost["lines"]
    rows = []
    for i, ln in enumerate(lines):
        b = bodies.get(ln["body"])
        drawn = is_drawn(ln, bodies)
        radius = b["radius"] if b else None
        lo, hi = altitude_range(ln["points"], radius) if drawn else (None, None)
        pe, ap = conic_apsides(ln["conic"], radius) if b else (None, None)
        dev = (max_deviation(ln["points"], ln["reference"])
               if drawn and ln["reference"] else None)
        fit = (conic_fit(ln["points"], ln["conic"], ln.get("clipped"),
                         b["soi"] if b else None)
               if drawn and ln["kind"] == "orbit" and ln["conic"] else None)
        rows.append({
            "index": i, "kind": ln["kind"], "body": ln["body"], "active": ln["active"],
            "drawn": drawn, "points": len(ln["points"]),
            "opacity": ln.get("opacity"),
            "faded": is_faded(ln),
            "conicFit": fit,
            "ut": ln["ut"],
            "utText": ("%s .. %s (%s)" % (format_ut(ln["ut"][0]), format_ut(ln["ut"][1]),
                                         format_duration(ln["ut"][1] - ln["ut"][0]))
                       if ln["ut"] else "-"),
            "lowest": _m(lo), "highest": _m(hi),
            "conicPe": _m(pe) if ln["conic"] else None,
            "conicAp": _m(ap) if ln["conic"] else None,
            "reference": (_m(dev) if drawn and ln["reference"] else None),
            "referenceKind": ln["referenceKind"],
            "referencePoints": len(ln["reference"]) if ln["reference"] else 0,
            "gapToNext": None,
        })
    order = line_order(lines)
    chain = [i for i in order if is_drawn(lines[i], bodies)]
    for a, b in zip(chain, chain[1:]):
        la, lb = lines[a], lines[b]
        end = la["points"][-1]
        start = to_frame(bodies, lb["body"], lb["points"][0], la["body"])
        rows[a]["gapToNext"] = {"to": b, "m": v_len(v_sub(start, end)),
                                "text": format_distance(v_len(v_sub(start, end))),
                                "crossBody": la["body"] != lb["body"]}
    icon = None
    ipos = ghost["icon"]["pos"]
    if ipos is not None and ghost["body"] in bodies:
        best = None
        for i, ln in enumerate(lines):
            if not is_drawn(ln, bodies):
                continue
            pts = [to_frame(bodies, ln["body"], p, ghost["body"]) for p in ln["points"]]
            d, _seg = point_polyline_distance(ipos, pts)
            if best is None or d < best[0]:
                best = (d, i)
        if best is not None:
            icon = {"m": best[0], "text": format_distance(best[0]), "line": best[1]}
    return {"lines": rows, "order": order,
            "timeOrdered": bool(chain) and all(lines[i].get("ut") for i in chain),
            "icon": icon,
            "offConic": sum(1 for r in rows if r["conicFit"] and not r["conicFit"]["fits"]),
            "faded": sum(1 for r in rows if r["faded"])}


def closeup_bodies(dump):
    """The bodies that get a close-up: every body a drawn line or icon is
    relative to, plus the parent of a moon that carries a line. Ordered as the
    dump lists bodies, so a planet comes before its moons."""
    by = {b["name"]: b for b in dump["bodies"]}
    want = set()
    for g in dump["ghosts"]:
        for ln in g["lines"]:
            if is_drawn(ln, by):
                want.add(ln["body"])
        if g["icon"]["pos"] is not None and g["body"] in by:
            want.add(g["body"])
    for name in list(want):
        parent = by[name]["parent"]
        if parent is not None and parent != dump["root"] and parent in by:
            want.add(parent)
    # The root's own close-up would be the whole-system view again.
    want.discard(dump["root"])
    return [b["name"] for b in dump["bodies"] if b["name"] in want]


# --------------------------------------------------------------------------
# shots directories
# --------------------------------------------------------------------------

_SHOTS_DIR = re.compile(r"(?P<run>\d{4}-\d{2}-\d{2}_\d{4})_(?P<spec>.+?)"
                        r"(?P<sfx>(?:_run\d+)?(?:_a\d+)?)_shots$")


def run_and_lane(path):
    """`<runId>_shots` -> (run id, lane id). run.py's run id is
    `<stamp>_<specId>[_run<N>][_a<N>]` (hlib.format_run_id); the lane is the spec
    id. A directory not named that way is its own run and lane."""
    base = os.path.basename(os.path.normpath(path))
    m = _SHOTS_DIR.match(base)
    if not m:
        name = base[:-len("_shots")] if base.endswith("_shots") else base
        return name, name
    return m.group("run") + "_" + m.group("spec") + m.group("sfx"), m.group("spec")


def png_size(path):
    """(width, height) off the IHDR chunk, or None for a file that is not a PNG."""
    try:
        with open(path, "rb") as fh:
            head = fh.read(24)
    except OSError:
        return None
    if head[:8] != b"\x89PNG\r\n\x1a\n" or head[12:16] != b"IHDR":
        return None
    return struct.unpack(">II", head[16:24])


def scan_shots_dir(path, verbose=False):
    run_id, lane = run_and_lane(path)
    caps = []
    for name in sorted(os.listdir(path)):
        if not name.endswith(DUMP_SUFFIX):
            continue
        label = name[:-len(DUMP_SUFFIX)]
        file_path = os.path.join(path, name)
        png = os.path.join(path, label + ".png")
        cap = {"id": "%s/%s" % (run_id, label), "label": label, "runId": run_id,
               "lane": lane, "file": file_path, "png": png if os.path.isfile(png) else None,
               "dump": None, "notes": [], "error": None}
        try:
            cap["dump"], cap["notes"] = load_dump(file_path)
            if cap["dump"]["label"] and cap["dump"]["label"] != label:
                cap["notes"].append("the file says label %r; filed under its name %r"
                                    % (cap["dump"]["label"], label))
        except (DumpError, OSError) as exc:
            cap["error"] = str(exc)
            if verbose:
                sys.stderr.write("map-mirror: unreadable %s: %s\n" % (file_path, exc))
        caps.append(cap)
    return caps


def _cap_sort_key(cap):
    d = cap["dump"] or {}
    return (cap["lane"], run_sort_key(cap["runId"]),
            d.get("ut") if d.get("ut") is not None else float("inf"),
            d.get("frame") if d.get("frame") is not None else 0, cap["label"])


# --------------------------------------------------------------------------
# photographs under the budget
# --------------------------------------------------------------------------

def _rgb(w, h, bpp, px):
    if bpp == 3:
        return px
    if bpp == 4:
        out = bytearray(w * h * 3)
        out[0::3] = px[0::4]
        out[1::3] = px[1::4]
        out[2::3] = px[2::4]
        return bytes(out)
    raise ValueError("unsupported channel count %d" % bpp)


# (colour floor, subsample factor) steps tried in order once the originals do
# not fit. A map view is mostly black space and thin lines, so the floor costs
# little; the subsample is what actually shrinks a 1080p frame.
PHOTO_LADDER = ((8, 1), (16, 2), (24, 3), (32, 4), (32, 6))


def embed_photos(caps, payload_budget, verbose=False):
    """Inline each capture's PNG as a data URI, re-encoding down the ladder until
    the base64 total fits `payload_budget`. Returns the bytes used. A photo the
    stdlib decoder cannot read is left out with a note when the originals do not
    fit; nothing is ever silently dropped."""
    jobs = [c for c in caps if c.get("png")]
    if not jobs:
        return 0
    raw = {}
    for c in jobs:
        with open(c["png"], "rb") as fh:
            raw[c["id"]] = fh.read()
    total = sum(len(b) * 4 // 3 + 32 for b in raw.values())
    if total <= payload_budget:
        for c in jobs:
            size = png_size(c["png"]) or (0, 0)
            c["photo"] = {"src": "data:image/png;base64,"
                          + base64.b64encode(raw[c["id"]]).decode("ascii"),
                          "w": size[0], "h": size[1], "note": ""}
        return total
    decoded = {}
    for c in jobs:
        try:
            w, h, bpp, px = read_png(c["png"])
            decoded[c["id"]] = (w, h, _rgb(w, h, bpp, px))
        except Exception as exc:  # any decoder refusal: say so, keep going
            c["photo"] = {"src": "", "w": 0, "h": 0,
                          "note": "screenshot not inlined: it could not be re-encoded "
                                  "to fit the budget (%s)" % exc}
            if verbose:
                sys.stderr.write("map-mirror: %s: %s\n" % (c["png"], exc))
    blobs, used = {}, 0
    for quant, factor in PHOTO_LADDER:
        blobs, used = {}, 0
        for cid, (w, h, px) in decoded.items():
            sw, sh, spx = subsample(w, h, 3, px, factor)
            blob = write_png(sw, sh, 3, spx, quant)
            blobs[cid] = (blob, sw, sh, w, h, quant, factor)
            used += len(blob) * 4 // 3 + 32
        if used <= payload_budget:
            break
    for c in jobs:
        if c["id"] in blobs:
            blob, sw, sh, w, h, quant, factor = blobs[c["id"]]
            c["photo"] = {"src": "data:image/png;base64," + base64.b64encode(blob).decode("ascii"),
                          "w": sw, "h": sh,
                          "note": "re-encoded to fit the page budget: %dx%d of %dx%d, "
                                  "colour floor %d" % (sw, sh, w, h, quant)}
    return used


# --------------------------------------------------------------------------
# the page model
# --------------------------------------------------------------------------

def _r(v):
    """10 significant figures: sub-centimetre inside an SOI, which is far below
    a pixel at any zoom the page allows, and a third of the bytes of repr."""
    if isinstance(v, float):
        return float("%.10g" % v)
    if isinstance(v, (list, tuple)):
        return [_r(x) for x in v]
    if isinstance(v, dict):
        return {k: _r(x) for k, x in v.items()}
    return v


def _page_capture(cap, ghost_colour_of):
    base = {"id": cap["id"], "label": cap["label"], "runId": cap["runId"],
            "lane": cap["lane"], "file": os.path.basename(cap["file"]),
            "notes": cap["notes"], "error": cap["error"],
            "hasPng": bool(cap.get("png")), "photo": cap.get("photo")}
    d = cap["dump"]
    if d is None:
        return base
    bodies = {b["name"]: b for b in d["bodies"]}
    ghosts = []
    for g in d["ghosts"]:
        gg = dict(g)
        gg["lines"] = [{k: v for k, v in ln.items()} for ln in g["lines"]]
        gg["colour"] = ghost_colour_of(g)
        gg["measured"] = measure_ghost(g, bodies)
        ghosts.append(gg)
    base.update({
        "offConic": sum(g["measured"]["offConic"] for g in ghosts),
        "faded": sum(g["measured"]["faded"] for g in ghosts),
        "scene": d["scene"], "ut": d["ut"], "utText": format_ut(d["ut"]),
        "frame": d["frame"], "warpRate": d["warpRate"], "camera": d["camera"],
        "axes": d["axes"], "root": d["root"],
        # The writer's body ring is OPEN (64 points, first not repeated).
        "bodies": [dict(b, orbit=close_ring(b["orbit"]) if b["orbit"] else b["orbit"])
                   for b in d["bodies"]],
        "ghosts": ghosts, "counts": d["counts"],
        "closeups": closeup_bodies(d),
        "cameraCheck": camera_model_check(d["axes"], d["camera"]),
    })
    return _r(base)


def scan_all(dirs, verbose=False):
    caps = []
    for d in dirs:
        caps.extend(scan_shots_dir(d, verbose=verbose))
    caps.sort(key=_cap_sort_key)
    return caps


def build_model(dirs, with_photos=True, payload_budget=DEFAULT_BUDGET_BYTES,
                stamp="", verbose=False, caps=None):
    """`caps` (from `scan_all`) skips the directory scan; the photo fields on it
    are rewritten by this call."""
    if caps is None:
        caps = scan_all(dirs, verbose=verbose)
    if with_photos:
        photo_bytes = embed_photos(caps, payload_budget, verbose=verbose)
    else:
        photo_bytes = 0
        for c in caps:
            if c.get("png"):
                c["photo"] = {"src": "", "w": 0, "h": 0,
                              "note": "screenshot present but left out (--no-photos)"}
    # One colour per recording across the whole page, so a ghost keeps its colour
    # from capture to capture while the owner steps through a lane.
    rec_ids = []
    for c in caps:
        for g in (c["dump"] or {}).get("ghosts", []):
            key = g["recordingId"] or g["vesselName"]
            if key not in rec_ids:
                rec_ids.append(key)

    def colour_of(g):
        return rec_ids.index(g["recordingId"] or g["vesselName"])

    lanes = []
    for c in caps:
        if not lanes or lanes[-1]["lane"] != c["lane"]:
            lanes.append({"lane": c["lane"], "captures": []})
        lanes[-1]["captures"].append(c["id"])
    return {
        "schema": INDEX_SCHEMA,
        "generatedUtc": stamp,
        "lanes": lanes,
        "captures": [_page_capture(c, colour_of) for c in caps],
        "photoBytes": photo_bytes,
        "cameraFovDeg": CAMERA_FOV_DEG,
    }


def build_index(model):
    lanes = []
    by_id = {c["id"]: c for c in model["captures"]}
    unreadable = []
    for lane in model["lanes"]:
        rows = []
        for cid in lane["captures"]:
            c = by_id[cid]
            if c.get("error"):
                unreadable.append({"id": cid, "file": c["file"], "reason": c["error"]})
                rows.append({"id": cid, "label": c["label"], "runId": c["runId"],
                             "unreadable": c["error"], "screenshot": c["hasPng"]})
                continue
            held = c["counts"]["held"]
            rows.append({
                "id": cid, "label": c["label"], "runId": c["runId"], "scene": c["scene"],
                "ut": c["ut"], "ghosts": held["ghosts"], "lines": held["lines"],
                "points": held["points"],
                "truncated": bool(c["counts"]["declared"].get("truncated")),
                "screenshot": c["hasPng"],
                "screenshotInlined": bool((c.get("photo") or {}).get("src")),
                "closeups": c["closeups"],
                "offConicLines": c["offConic"],
                "fadedLines": c["faded"],
                "notes": c["notes"],
            })
        lanes.append({"lane": lane["lane"], "captures": rows})
    return {
        "schema": INDEX_SCHEMA,
        "generatedUtc": model["generatedUtc"],
        "captureCount": len(model["captures"]),
        "unreadableCount": len(unreadable),
        "lanes": lanes,
        "unreadable": unreadable,
        "photoBytes": model["photoBytes"],
    }


# --------------------------------------------------------------------------
# the page
# --------------------------------------------------------------------------

CSS = r"""
:root{
  --page:#f6f7f9; --panel:#ffffff; --rail:#eef0f3; --ink:#1d2329; --dim:#5d6873;
  --edge:#cfd5dc; --sel:#d7e5f7; --accent:#1f62b8; --warn:#9a5b00; --bad:#b3261e;
  --space:#fbfcfd; --body:#b9c2cc; --bodyedge:#6f7c88; --sun:#f2b632;
  --ring:#9aa6b2; --soi:#8a96a3; --ref:#c0008f;
  --g0:#1f62b8; --g1:#c2580a; --g2:#14804a; --g3:#8a3fbf; --g4:#0b7f8c;
  --g5:#9a7400; --g6:#a63c3c; --g7:#4a5866;
}
@media (prefers-color-scheme: dark){
  :root:not([data-theme="light"]){
    --page:#111418; --panel:#181c21; --rail:#14181c; --ink:#e3e7ec; --dim:#98a3ae;
    --edge:#2c333b; --sel:#22364f; --accent:#7fb2ff; --warn:#e3a54a; --bad:#ff8a80;
    --space:#0b0e12; --body:#4b5561; --bodyedge:#9aa6b2; --sun:#f5c451;
    --ring:#5f6b77; --soi:#6f7b88; --ref:#ff5fd2;
    --g0:#6aa8ff; --g1:#ff9b54; --g2:#4fd08a; --g3:#c48bff; --g4:#3fd0dd;
    --g5:#e6c34d; --g6:#ff7a7a; --g7:#aab6c2;
  }
}
:root[data-theme="dark"]{
  --page:#111418; --panel:#181c21; --rail:#14181c; --ink:#e3e7ec; --dim:#98a3ae;
  --edge:#2c333b; --sel:#22364f; --accent:#7fb2ff; --warn:#e3a54a; --bad:#ff8a80;
  --space:#0b0e12; --body:#4b5561; --bodyedge:#9aa6b2; --sun:#f5c451;
  --ring:#5f6b77; --soi:#6f7b88; --ref:#ff5fd2;
  --g0:#6aa8ff; --g1:#ff9b54; --g2:#4fd08a; --g3:#c48bff; --g4:#3fd0dd;
  --g5:#e6c34d; --g6:#ff7a7a; --g7:#aab6c2;
}
*{box-sizing:border-box}
body{margin:0;background:var(--page);color:var(--ink);
  font:13px/1.45 system-ui,-apple-system,"Segoe UI",Arial,sans-serif}
#top{display:flex;align-items:center;gap:12px;padding:7px 12px;background:var(--rail);
  border-bottom:1px solid var(--edge);position:sticky;top:0;z-index:20;flex-wrap:wrap}
#top h1{font-size:14px;margin:0}
#top .meta{color:var(--dim);font-size:12px}
#top .bad{color:var(--bad)}
.facts .bad{color:var(--bad)}
.tok{border-bottom:1px dotted var(--dim);cursor:help}
#top .sp{flex:1}
button{background:var(--panel);color:var(--ink);border:1px solid var(--edge);border-radius:4px;
  padding:2px 8px;font:12px system-ui,Arial,sans-serif;cursor:pointer}
button:hover{border-color:var(--accent)}
#wrap{display:flex;align-items:flex-start}
#rail{width:250px;flex:0 0 250px;background:var(--rail);border-right:1px solid var(--edge);
  height:calc(100vh - 38px);overflow:auto;position:sticky;top:38px;padding-bottom:30px}
#rail h2{font-size:11px;text-transform:uppercase;letter-spacing:.06em;color:var(--dim);
  margin:12px 10px 3px}
#rail .run{font-size:11px;color:var(--dim);margin:4px 10px 1px 14px}
#rail .cap{padding:4px 10px 4px 14px;cursor:pointer;border-left:3px solid transparent}
#rail .cap:hover{background:var(--sel)}
#rail .cap.sel{background:var(--sel);border-left-color:var(--accent)}
#rail .cap .l{font-weight:600;word-break:break-all}
#rail .cap .s{font-size:11px;color:var(--dim)}
#rail .cap.err .l{color:var(--bad)}
#rail .cap.offc .s{color:var(--bad)}
#main{flex:1;min-width:0;padding:10px 14px 60px}
#main h2{font-size:15px;margin:2px 0 4px}
#main h3{font-size:13px;margin:18px 0 6px;color:var(--dim);text-transform:uppercase;
  letter-spacing:.05em}
.facts{color:var(--dim);font-size:12px}
.facts b{color:var(--ink);font-weight:600}
.notes{margin:6px 0;padding:6px 10px;border-left:3px solid var(--warn);background:var(--panel);
  color:var(--warn);font-size:12px}
.err{margin:10px 0;padding:10px;border-left:3px solid var(--bad);background:var(--panel);
  color:var(--bad)}
#legend{display:flex;flex-wrap:wrap;gap:6px 14px;align-items:center;margin:8px 0;padding:6px 8px;
  background:var(--panel);border:1px solid var(--edge);border-radius:4px;font-size:12px}
#legend label{display:flex;align-items:center;gap:5px;cursor:pointer}
#legend svg{vertical-align:middle}
.row{display:flex;gap:10px;flex-wrap:wrap;align-items:flex-start}
.panel{background:var(--panel);border:1px solid var(--edge);border-radius:4px;
  flex:1 1 420px;min-width:300px;position:relative}
.panel .ph{display:flex;align-items:center;gap:6px;padding:4px 8px;border-bottom:1px solid var(--edge);
  font-size:12px;flex-wrap:wrap}
.panel .ph b{font-weight:600}
.panel .ph .sp{flex:1}
.panel .ph .ro{color:var(--dim);font-size:11px}
.panel .sv{position:relative;overflow:hidden;background:var(--space);cursor:grab;touch-action:none}
.panel .sv.drag{cursor:grabbing}
.panel .sv svg{display:block;user-select:none}
.photo{flex:1 1 420px;min-width:300px}
.photo img{width:100%;display:block;border:1px solid var(--edge);border-radius:4px;cursor:zoom-in}
.photo.big{flex-basis:100%}
.photo.big img{cursor:zoom-out}
.photo .none{padding:30px 10px;border:1px dashed var(--edge);border-radius:4px;color:var(--dim);
  text-align:center;background:var(--panel)}
.photo .pn{font-size:11px;color:var(--dim);margin-top:3px}
table.m{border-collapse:collapse;font-size:12px;margin:4px 0 10px;background:var(--panel)}
table.m th,table.m td{border:1px solid var(--edge);padding:3px 7px;text-align:left;vertical-align:top}
table.m th{background:var(--rail);font-weight:600}
table.m td.n{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
table.m td.bad{color:var(--bad);font-weight:700;white-space:nowrap}
table.m td.fit{white-space:nowrap}
.sw{display:inline-block;width:11px;height:11px;border-radius:2px;vertical-align:-1px;margin-right:4px}
.gh{margin:12px 0 2px;font-size:13px}
#tip{position:fixed;z-index:30;pointer-events:none;background:var(--panel);color:var(--ink);
  border:1px solid var(--edge);border-radius:4px;padding:5px 8px;font-size:12px;max-width:420px;
  box-shadow:0 2px 8px rgba(0,0,0,.25);display:none;white-space:pre-line}
svg text{font:11px system-ui,Arial,sans-serif;fill:var(--ink);paint-order:stroke;
  stroke:var(--space);stroke-width:3px;stroke-linejoin:round}
svg text.dim{fill:var(--dim)}
svg .body{fill:var(--body);stroke:var(--bodyedge);stroke-width:1}
svg .body.sun{fill:var(--sun);stroke:var(--sun)}
svg .body.tiny{stroke-dasharray:2 1.5}
svg .soi{fill:none;stroke:var(--soi);stroke-width:1;stroke-dasharray:3 4;opacity:.7}
svg .ring{fill:none;stroke:var(--ring);stroke-width:1;opacity:.85}
svg .ring.occ{opacity:.25}
svg .ln{fill:none;stroke-linecap:round;stroke-linejoin:round}
svg .k-orbit{stroke-width:1.8}
svg .k-polyline{stroke-width:2.4}
svg .k-tail{stroke-width:2;stroke-dasharray:1 4}
svg .k-bridge{stroke-width:3.2;stroke-dasharray:3 2}
svg .k-ref{stroke:var(--ref);stroke-width:1.6;stroke-dasharray:5 4}
svg .occ{opacity:.22}
svg .hit{fill:none;stroke:transparent;stroke-width:10;pointer-events:stroke}
svg .hitp{fill:transparent;pointer-events:all}
svg .scale{stroke:var(--ink);stroke-width:1.5;fill:none}
"""

JS = r"""
'use strict';
var M = window.__MAPMIRROR__;
var BY = {};
M.captures.forEach(function(c){ BY[c.id] = c; });
var ORDER = [];
M.lanes.forEach(function(l){ l.captures.forEach(function(id){ ORDER.push(id); }); });
var S = {cap: null, hidden: {}, views: []};
var NG = 8;

/* ---------------- math (mirrors the Python helpers) ---------------- */
function add(a,b){ return [a[0]+b[0],a[1]+b[1],a[2]+b[2]]; }
function sub(a,b){ return [a[0]-b[0],a[1]-b[1],a[2]-b[2]]; }
function mul(a,s){ return [a[0]*s,a[1]*s,a[2]*s]; }
function dot(a,b){ return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]; }
function cross(a,b){ return [a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]]; }
function norm(a){ var n=Math.sqrt(dot(a,a)); return n ? mul(a,1/n) : a; }
function rad(d){ return d*Math.PI/180; }
function axesOf(c){
  var U = norm(c.axes.up);
  var R = norm(sub(c.axes.right, mul(U, dot(c.axes.right, U))));
  return {R:R, U:U, F:cross(R,U)};
}
function basisTop(A){ return {X:A.R, Y:A.F, D:cross(A.R,A.F)}; }
function basisEdge(A, az){
  var X = add(mul(A.R, Math.cos(rad(az))), mul(A.F, Math.sin(rad(az))));
  return {X:X, Y:A.U, D:cross(X, A.U)};
}
function basisCam(A, pitch, hdg){
  var p = rad(pitch), h = rad(hdg);
  var X = add(mul(A.R, Math.cos(h)), mul(A.F, -Math.sin(h)));
  var Y = add(add(mul(A.R, Math.sin(p)*Math.sin(h)), mul(A.U, Math.cos(p))),
              mul(A.F, Math.sin(p)*Math.cos(h)));
  return {X:X, Y:Y, D:cross(X,Y)};
}
/* The camera's own frame from the dump: x = up x forward (Unity's right), y = up
   made perpendicular to forward, D = forward. */
function basisFrom(f, u){
  var D = norm(f), Y = norm(sub(u, mul(D, dot(u, D))));
  return {X:cross(Y, D), Y:Y, D:D};
}
/* Rodrigues: v turned by `deg` about the unit axis k. */
function rot(v, k, deg){
  var a = rad(deg), c = Math.cos(a), s = Math.sin(a);
  return add(add(mul(v, c), mul(cross(k, v), s)), mul(k, dot(k, v)*(1-c)));
}
function sig(x){ var a=Math.abs(x); return a>=100 ? x.toFixed(0) : a>=10 ? x.toFixed(1) : x.toFixed(2); }
function fmtD(m){
  if (m == null) return '-';
  var a = Number(Math.abs(m).toPrecision(3)), u = [['Gm',1e9],['Mm',1e6],['km',1e3]];
  for (var i=0;i<u.length;i++) if (a >= u[i][1]) return sig(m/u[i][1])+' '+u[i][0];
  if (a === 0) return '0 m';
  if (a < 0.01) return m.toPrecision(1)+' m';
  return sig(m)+' m';
}
function niceLen(m){
  var p = Math.pow(10, Math.floor(Math.log10(m))), f = m/p;
  return (f >= 5 ? 5 : f >= 2 ? 2 : 1) * p;
}
function esc(s){ return String(s).replace(/[&<>"]/g, function(ch){
  return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[ch]; }); }
function el(tag, cls, txt){ var e=document.createElement(tag); if (cls) e.className=cls;
  if (txt != null) e.textContent = txt; return e; }
function gcol(i){ return 'var(--g'+(i % NG)+')'; }

/* ---------------- scene content ---------------- */
function bodyMap(c){ var m={}; c.bodies.forEach(function(b){ m[b.name]=b; }); return m; }
function isDesc(B, name, anc){
  var seen = {};
  while (name != null && !seen[name]) { if (name === anc) return true; seen[name]=1;
    name = B[name] ? B[name].parent : null; }
  return false;
}
/* Everything a view draws, in metres relative to the frame body's centre. */
function content(c, frame, onlyUnder){
  var B = bodyMap(c), o = B[frame].pos;
  function off(name){ return sub(B[name].pos, o); }
  function inc(name){ return B[name] && (!onlyUnder || isDesc(B, name, onlyUnder)); }
  var it = {bodies:[], rings:[], lines:[], refs:[], icons:[]};
  c.bodies.forEach(function(b){
    if (!inc(b.name)) return;
    it.bodies.push({b:b, c:off(b.name)});
    if (b.orbit && b.parent && B[b.parent] && (!onlyUnder || b.name !== onlyUnder)) {
      /* rings arrive closed: the page model closes the writer's open ring */
      var po = off(b.parent);
      it.rings.push({b:b, pts:b.orbit.map(function(p){ return add(po, p); })});
    }
  });
  c.ghosts.forEach(function(g, gi){
    g.lines.forEach(function(ln, li){
      /* An inactive line is listed in the table only (the writer sends it empty), and
         so is a line at stock fade opacity 0 (stock did not draw it this frame). */
      if (!inc(ln.body) || !ln.active || !ln.points.length) return;
      if (ln.opacity != null && ln.opacity <= 0) return;
      var lo = off(ln.body);
      var pts = ln.points.map(function(p){ return add(lo, p); });
      it.lines.push({g:g, gi:gi, ln:ln, li:li, pts:pts});
      if (ln.reference) it.refs.push({g:g, gi:gi, ln:ln, li:li,
        pts:ln.reference.map(function(p){ return add(lo, p); })});
    });
    if (g.icon.pos && inc(g.body)) it.icons.push({g:g, gi:gi, p:add(off(g.body), g.icon.pos)});
  });
  return it;
}

/* ---------------- tooltips ---------------- */
function lineTip(c, g, ln, li){
  var t = g.vesselName + '  -  line #' + li + ' ' + ln.kind +
    '\nrelative to ' + ln.body + ', ' + ln.points.length + ' points';
  if (ln.ut) t += '\ntime ' + ln.ut[0].toFixed(0) + ' .. ' + ln.ut[1].toFixed(0) + ' s (' +
    ((ln.ut[1]-ln.ut[0])/3600).toFixed(2) + ' h)';
  var row = g.measured.lines[li];
  if (row) t += '\naltitude ' + row.lowest.text + ' .. ' + row.highest.text;
  if (ln.conic) t += '\nconic: sma ' + fmtD(ln.conic.sma) + ', ecc ' + ln.conic.ecc +
    ', inc ' + ln.conic.incDeg + ' deg';
  if (ln.opacity != null) t += '\nstock fade opacity ' + ln.opacity.toFixed(3) +
    (ln.opacity < 1 ? ' (drawn faded)' : '');
  if (row && row.conicFit) t += '\n' + (row.conicFit.fits ? 'fits its conic' : 'OFF ITS CONIC') +
    ': vertices ' + fmtD(row.conicFit.minR) + ' .. ' + fmtD(row.conicFit.maxR) + ' from the centre, conic ' +
    fmtD(row.conicFit.rp) + ' .. ' + (row.conicFit.ra != null ? fmtD(row.conicFit.ra) : 'open');
  t += '\nrecording ' + g.recordingId;
  return t;
}
function pointTip(c, B, body, p, head){
  var r = B[body] ? Math.sqrt(dot(p,p)) - B[body].radius : null;
  return head + '\naltitude above ' + body + ': ' + fmtD(r);
}

/* ---------------- one view ---------------- */
function View(spec){
  this.spec = spec;
  this.reset();
}
View.prototype.reset = function(){
  var sp = this.spec;
  this.az = sp.az || 0; this.pitch = sp.pitch; this.hdg = sp.hdg;
  this.cf = sp.camF || null; this.cu = sp.camU || null; this.turned = false;
  this.tf = null;
};
View.prototype.basis = function(){
  var A = this.spec.A;
  if (this.spec.mode === 'top') return basisTop(A);
  if (this.spec.mode === 'edge') return basisEdge(A, this.az);
  if (this.cf) return basisFrom(this.cf, this.cu);
  return basisCam(A, this.pitch, this.hdg);
};
/* Where a point lands on the view plane, in metres: [x, y, k], k the local
   metres-per-metre scale (1 for the orthographic views). The camera view is a
   perspective one, like the game's: the eye sits `distance` behind the pivot
   along the view direction and x, y are measured on the plane through the
   pivot. A point at or behind the eye returns null. */
View.prototype.projector = function(){
  var B = this.basis(), sp = this.spec;
  if (!sp.persp) return {B:B, f:function(p){ return [dot(p,B.X), dot(p,B.Y), 1]; }, eye:null};
  var d = sp.distance, ctr = sp.center || [0,0,0], eye = sub(ctr, mul(B.D, d)), near = d*1e-3;
  return {B:B, eye:eye, f:function(p){
    var v = sub(p, eye), z = dot(v, B.D);
    if (!(z > near)) return null;
    var k = d / z;
    return [dot(v,B.X)*k, dot(v,B.Y)*k, k];
  }};
};
View.prototype.fit = function(which){
  var PR = this.projector(), it = this.spec.it, w = this.w, h = this.h;
  var xs = [], ys = [];
  function addP(p, r){ var q = PR.f(p); if (!q) return; r=(r||0)*q[2];
    xs.push(q[0]-r, q[0]+r); ys.push(q[1]-r, q[1]+r); }
  if (which === 'camera') {
    var d = this.spec.distance || 1e6;
    var half = d * Math.tan(rad(this.spec.fov/2));
    this.tf = {s: (h/2)/half, ox: 0, oy: 0};
    return;
  }
  if (which === 'body' && this.spec.frame) {
    var bb = it.bodies.filter(function(x){ return x.b.name === this.spec.frame; }, this)[0];
    if (bb) addP(bb.c, bb.b.radius*3);
  } else if (which === 'soi' && this.spec.frame) {
    var fb = it.bodies.filter(function(x){ return x.b.name === this.spec.frame; }, this)[0];
    if (fb) addP(fb.c, fb.b.soi || fb.b.radius*4);
  } else if (which === 'all') {
    it.bodies.forEach(function(x){ addP(x.c, x.b.radius); });
    it.rings.forEach(function(x){ x.pts.forEach(function(p){ addP(p); }); });
  } else {
    var any = false;
    it.lines.forEach(function(x){ if (S.hidden[x.gi]) return; x.pts.forEach(function(p){ addP(p); any=true; }); });
    it.icons.forEach(function(x){ if (!S.hidden[x.gi]) { addP(x.p); any=true; } });
    /* The body is NOT part of this fit: a 5 km hop beside a 600 km planet has to
       fill the view, and the disc edge still shows where the ground is. */
    if (!any || !xs.length) return this.fit(this.spec.frame ? 'body' : 'all');
  }
  if (!xs.length) { this.tf = {s:1e-6, ox:0, oy:0}; return; }
  var x0=Math.min.apply(null,xs), x1=Math.max.apply(null,xs);
  var y0=Math.min.apply(null,ys), y1=Math.max.apply(null,ys);
  var bw = Math.max(x1-x0, 1000), bh = Math.max(y1-y0, 1000);
  this.tf = {s: Math.min((w-40)/bw, (h-40)/bh), ox:(x0+x1)/2, oy:(y0+y1)/2};
};
View.prototype.mount = function(host){
  var self = this, sp = this.spec;
  var panel = el('div', 'panel');
  if (sp.flex) panel.style.flex = sp.flex;
  var ph = el('div', 'ph');
  ph.appendChild(el('b', null, sp.title));
  this.ro = el('span', 'ro');
  ph.appendChild(this.ro);
  ph.appendChild(el('span', 'sp'));
  (sp.fits || []).forEach(function(f){
    var b = el('button', null, f[1]);
    b.title = f[2];
    b.onclick = function(){ self.fit(f[0]); self.draw(); };
    ph.appendChild(b);
  });
  var rb = el('button', null, 'reset');
  rb.title = 'Back to the opening view (double-click does the same).';
  rb.onclick = function(){ self.reset(); self.fit(sp.fitDefault); self.draw(); };
  ph.appendChild(rb);
  panel.appendChild(ph);
  var sv = el('div', 'sv');
  panel.appendChild(sv);
  host.appendChild(panel);
  this.sv = sv;
};
/* Measured only once every panel of the page is in the DOM: a panel mounted alone
   in its row is as wide as the row until its sibling arrives. */
View.prototype.start = function(){
  var self = this, sp = this.spec, sv = this.sv;
  this.w = Math.max(280, Math.floor(sv.clientWidth || 600));
  this.h = sp.height || Math.round(this.w * (sp.aspect || 0.62));
  sv.style.height = this.h + 'px';
  this.fit(sp.fitDefault);
  this.draw();
  var drag = null;
  sv.addEventListener('wheel', function(e){
    e.preventDefault();
    var r = sv.getBoundingClientRect(), mx = e.clientX - r.left, my = e.clientY - r.top;
    var tf = self.tf, k = Math.pow(1.0015, -e.deltaY);
    var ux = tf.ox + (mx - self.w/2)/tf.s, uy = tf.oy - (my - self.h/2)/tf.s;
    tf.s *= k;
    tf.ox = ux - (mx - self.w/2)/tf.s; tf.oy = uy + (my - self.h/2)/tf.s;
    self.queue();
  }, {passive:false});
  sv.addEventListener('pointerdown', function(e){
    if (e.button !== 0) return;
    drag = {x:e.clientX, y:e.clientY, rot: e.shiftKey && sp.mode !== 'top'};
    sv.classList.add('drag');
    try { sv.setPointerCapture(e.pointerId); } catch (err) {}
  });
  sv.addEventListener('pointermove', function(e){
    if (!drag) return;
    var dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    drag.x = e.clientX; drag.y = e.clientY;
    if (drag.rot) {
      if (sp.mode === 'edge') self.az = (self.az + dx*0.4) % 360;
      else if (self.cf) {
        /* turn the recorded camera about the pivot: yaw about the system's up,
           then pitch about the camera's own screen x */
        self.cf = rot(self.cf, sp.A.U, dx*0.4); self.cu = rot(self.cu, sp.A.U, dx*0.4);
        var bx = basisFrom(self.cf, self.cu).X;
        self.cf = rot(self.cf, bx, dy*0.4); self.cu = rot(self.cu, bx, dy*0.4);
        self.turned = true;
      }
      else { self.hdg = (self.hdg + dx*0.4) % 360;
             self.pitch = Math.max(-90, Math.min(90, self.pitch + dy*0.4)); }
    } else { self.tf.ox -= dx/self.tf.s; self.tf.oy += dy/self.tf.s; }
    self.queue();
  });
  function end(){ drag = null; sv.classList.remove('drag'); }
  sv.addEventListener('pointerup', end);
  sv.addEventListener('pointercancel', end);
  sv.addEventListener('dblclick', function(){ self.reset(); self.fit(sp.fitDefault); self.draw(); });
  sv.addEventListener('mousemove', tipMove);
  sv.addEventListener('mouseleave', tipHide);
};
View.prototype.queue = function(){
  var self = this;
  if (this.pending) return;
  this.pending = true;
  window.requestAnimationFrame(function(){ self.pending = false; self.draw(); });
};
View.prototype.draw = function(){
  var sp = this.spec, c = sp.cap, it = sp.it, PR = this.projector(), B = PR.B, w = this.w, h = this.h;
  var tf = this.tf, s = tf.s, BM = bodyMap(c), eye = PR.eye;
  var out = [], tips = [];
  /* screen pixel of a point, with its local scale as [2]; null behind the eye */
  function P(p){ var q = PR.f(p); return q ? [w/2 + (q[0]-tf.ox)*s, h/2 - (q[1]-tf.oy)*s, q[2]] : null; }
  function tip(t){ tips.push(t); return tips.length-1; }
  var spheres = it.bodies.map(function(x){ return {c:x.c, r:x.b.radius}; });
  /* Behind a body as seen along the view ray: the fixed view direction for the
     orthographic views, the ray from the eye in the camera view. */
  function occ(p){
    var u = eye ? norm(sub(p, eye)) : B.D;
    for (var i=0;i<spheres.length;i++){
      var q = sub(p, spheres[i].c), dep = dot(q, u), r = spheres[i].r;
      var perp2 = dot(q,q) - dep*dep;
      if (perp2 >= r*r) continue;
      var near = -Math.sqrt(r*r - perp2);
      if (dep > near + 1e-6*r && (!eye || dot(sub(eye, spheres[i].c), u) < near)) return true;
    }
    return false;
  }
  var X0=-w, X1=2*w, Y0=-h, Y1=2*h;
  function clip(a, b){
    var t0=0, t1=1, dx=b[0]-a[0], dy=b[1]-a[1];
    var pq = [[-dx, a[0]-X0],[dx, X1-a[0]],[-dy, a[1]-Y0],[dy, Y1-a[1]]];
    for (var i=0;i<4;i++){
      var p=pq[i][0], q=pq[i][1];
      if (p === 0) { if (q < 0) return null; continue; }
      var r = q/p;
      if (p < 0) { if (r > t1) return null; if (r > t0) t0 = r; }
      else { if (r < t0) return null; if (r < t1) t1 = r; }
    }
    return [[a[0]+t0*dx, a[1]+t0*dy],[a[0]+t1*dx, a[1]+t1*dy]];
  }
  function f1(v){ return (Math.round(v*10)/10).toString(); }
  /* Polyline -> {vis, occ} path data, split where it passes behind a body and
     clipped to a margin around the view so far-off points never reach the SVG. */
  function paths(pts){
    var acc = {vis:[], occ:[]}, last = {vis:null, occ:null};
    function seg(kind, a, b){
      var cs = clip(a, b);
      if (!cs) return;
      var l = last[kind];
      if (!l || Math.abs(l[0]-cs[0][0]) > 0.05 || Math.abs(l[1]-cs[0][1]) > 0.05)
        acc[kind].push('M'+f1(cs[0][0])+' '+f1(cs[0][1]));
      acc[kind].push('L'+f1(cs[1][0])+' '+f1(cs[1][1]));
      last[kind] = cs[1];
    }
    var prevP = null, prevO = false, prevW = null;
    for (var i=0;i<pts.length;i++){
      var W = pts[i], Q = P(W);
      if (!Q) { prevP = null; continue; }
      var O = occ(W);
      if (prevP) {
        if (O === prevO) seg(O ? 'occ' : 'vis', prevP, Q);
        else {
          var lo = prevW, hi = W;
          for (var k=0;k<10;k++){ var mid = mul(add(lo,hi),0.5);
            if (occ(mid) === prevO) lo = mid; else hi = mid; }
          var M2 = P(lo);
          seg(prevO ? 'occ' : 'vis', prevP, M2);
          seg(O ? 'occ' : 'vis', M2, Q);
        }
      }
      prevP = Q; prevO = O; prevW = W;
    }
    return {vis: acc.vis.join(''), occ: acc.occ.join('')};
  }
  function onScreen(q, m){ return !!q && q[0] > -m && q[0] < w+m && q[1] > -m && q[1] < h+m; }
  var labels = [];
  /* rings and SOI first, bodies, then lines on top */
  it.rings.forEach(function(x){
    var d = paths(x.pts);
    var t = tip(x.b.name + ' orbit around ' + x.b.parent);
    if (d.vis) out.push('<path class="ring" d="'+d.vis+'"/>');
    if (d.occ) out.push('<path class="ring occ" d="'+d.occ+'"/>');
    out.push('<path class="hit" data-t="'+t+'" d="'+d.vis+d.occ+'"/>');
  });
  it.bodies.forEach(function(x){
    var q = P(x.c), b = x.b;
    if (!q) return;
    var rp = b.radius*s*q[2], rs = (b.soi || 0)*s*q[2];
    if (b.soi && rs > 6 && rs < 60000 && onScreen(q, rs))
      out.push('<circle class="soi" cx="'+f1(q[0])+'" cy="'+f1(q[1])+'" r="'+f1(rs)+'"/>');
    if (!onScreen(q, rp + 20)) return;
    var tiny = rp < 2.5, r = tiny ? 2.5 : rp;
    if (r > 60000) return;
    var t = tip(b.name + '\nradius ' + fmtD(b.radius) + (b.soi ? ', sphere of influence ' + fmtD(b.soi) : '') +
      (tiny ? '\n(drawn at minimum size; true size ' + (rp < 0.01 ? '<0.01' : rp.toFixed(2)) + ' px)' : ''));
    out.push('<circle class="body'+(b.parent ? '' : ' sun')+(tiny ? ' tiny' : '')+'" data-t="'+t+
      '" cx="'+f1(q[0])+'" cy="'+f1(q[1])+'" r="'+f1(r)+'"/>');
    var top = sp.frame || c.root;
    labels.push({x:q[0]+r+3, y:q[1]-r*0.3-2, text:b.name, cls:'dim',
      pri: b.name === top ? 0 : b.parent === top ? 0.3 : 1 + 1/(1+b.radius)});
  });
  /* `pts` are frame-relative (where to draw), `raw` body-relative (the altitude). */
  function endpoints(pts, raw, col, head, body){
    if (!pts.length) return;
    var a = P(pts[0]), z = P(pts[pts.length-1]);
    var ta = tip(pointTip(c, BM, body, raw[0], 'start of ' + head));
    if (onScreen(a, 10)) out.push('<circle class="hitp" data-t="'+ta+'" cx="'+f1(a[0])+'" cy="'+f1(a[1])+'" r="3.6" style="fill:'+col+';stroke:var(--space);stroke-width:1"/>');
    var tz = tip(pointTip(c, BM, body, raw[raw.length-1], 'end of ' + head));
    if (onScreen(z, 10)) out.push('<circle class="hitp" data-t="'+tz+'" cx="'+f1(z[0])+'" cy="'+f1(z[1])+'" r="3.4" style="fill:var(--space);stroke:'+col+';stroke-width:1.8"/>');
  }
  it.lines.forEach(function(x){
    if (S.hidden[x.gi]) return;
    var col = gcol(x.g.colour), d = paths(x.pts);
    var kind = ['orbit','polyline','tail','bridge'].indexOf(x.ln.kind) >= 0 ? x.ln.kind : 'polyline';
    var cls = 'ln k-' + kind;
    var t = tip(lineTip(c, x.g, x.ln, x.li));
    /* Stock's fade: a line caught part-way (0 < opacity < 1) is drawn faded, never
       below a visible floor; the hover gives the exact value. */
    var fade = (x.ln.opacity != null && x.ln.opacity < 1) ? ';stroke-opacity:' + Math.max(0.15, x.ln.opacity).toFixed(3) : '';
    if (d.vis) out.push('<path class="'+cls+'" style="stroke:'+col+fade+'" d="'+d.vis+'"/>');
    if (d.occ) out.push('<path class="'+cls+' occ" style="stroke:'+col+fade+'" d="'+d.occ+'"/>');
    out.push('<path class="hit" data-t="'+t+'" d="'+d.vis+d.occ+'"/>');
  });
  it.refs.forEach(function(x){
    if (S.hidden[x.gi]) return;
    var d = paths(x.pts);
    var row = x.g.measured.lines[x.li];
    var t = tip('reference path (' + (x.ln.referenceKind || 'unlabelled') + ') of ' + x.g.vesselName +
      ' line #' + x.li + '\n' + x.ln.reference.length + ' points; drawn points lie at most ' +
      (row && row.reference ? row.reference.text : '-') + ' from it');
    var dd = d.vis + d.occ;
    if (dd) out.push('<path class="ln k-ref" d="'+dd+'"/><path class="hit" data-t="'+t+'" d="'+dd+'"/>');
  });
  it.lines.forEach(function(x){
    if (S.hidden[x.gi]) return;
    endpoints(x.pts, x.ln.points, gcol(x.g.colour), x.g.vesselName + ' line #' + x.li + ' (' + x.ln.kind + ')', x.ln.body);
  });
  it.icons.forEach(function(x){
    if (S.hidden[x.gi]) return;
    var q = P(x.p);
    if (!onScreen(q, 10)) return;
    var col = gcol(x.g.colour), k = 6.5;
    var m = x.g.measured.icon;
    var t = tip(x.g.vesselName + ' icon ' + (x.g.icon.drawn ? '(drawn)' : (x.g.icon.faded ? '(not on screen: stock faded it out with its orbit line)' : '(NOT drawn this frame)')) +
      '\nrelative to ' + x.g.body + (m ? '\nnearest drawn point of its own lines: ' + m.text + ' (line #' + m.line + ')' : ''));
    var dpath = 'M'+f1(q[0])+' '+f1(q[1]-k)+'L'+f1(q[0]+k)+' '+f1(q[1])+'L'+f1(q[0])+' '+f1(q[1]+k)+'L'+f1(q[0]-k)+' '+f1(q[1])+'Z';
    out.push('<path class="hitp" data-t="'+t+'" d="'+dpath+'" style="' + (x.g.icon.drawn ?
      'fill:'+col+';stroke:var(--space);stroke-width:1.5' :
      'fill:none;stroke:'+col+';stroke-width:1.5;stroke-dasharray:2 2') + '"/>');
    labels.push({x:q[0]+k+3, y:q[1]+4, text:x.g.vesselName, pri:0.5, cls:''});
  });
  /* labels: greedy, highest priority first, skip a label that would overlap one already placed */
  labels.sort(function(a,b){ return a.pri - b.pri; });
  var placed = [];
  labels.forEach(function(L){
    var bw = L.text.length*6.3 + 4, box = [L.x, L.y-10, L.x+bw, L.y+3];
    if (box[2] < 0 || box[0] > w || box[3] < 0 || box[1] > h) return;
    for (var i=0;i<placed.length;i++){ var p = placed[i];
      if (box[0] < p[2] && box[2] > p[0] && box[1] < p[3] && box[3] > p[1]) return; }
    placed.push(box);
    out.push('<text class="'+L.cls+'" x="'+f1(L.x)+'" y="'+f1(L.y)+'">'+esc(L.text)+'</text>');
  });
  /* scale bar + true scale */
  var len = niceLen(110/s), px = len*s;
  out.push('<path class="scale" d="M12 '+(h-14)+'v-5M12 '+(h-14)+'h'+f1(px)+'v-5"/>');
  out.push('<text x="'+f1(16+px)+'" y="'+(h-13)+'">'+esc(fmtD(len))+'</text>');
  out.push('<text class="dim" x="'+(w-8)+'" y="15" text-anchor="end">1 px = '+esc(fmtD(1/s))+
    (sp.persp ? ' at the pivot' : '')+'</text>');
  var hint = 'wheel zoom, drag pan, double-click reset' + (sp.mode === 'top' ? '' : ', shift-drag rotate');
  out.push('<text class="dim" x="'+(w-8)+'" y="'+(h-8)+'" text-anchor="end">'+hint+'</text>');
  this.tips = tips;
  this.sv.innerHTML = '<svg width="'+w+'" height="'+h+'" viewBox="0 0 '+w+' '+h+'">'+out.join('')+'</svg>';
  this.sv._view = this;
  if (sp.mode === 'edge') this.ro.textContent = ' azimuth ' + this.az.toFixed(0) + ' deg';
  if (sp.mode === 'cam') {
    /* how far below the system's horizontal plane the view looks */
    var down = Math.asin(Math.max(-1, Math.min(1, -dot(B.D, sp.A.U)))) * 180/Math.PI;
    this.ro.textContent = this.cf ? ' recorded frame' + (this.turned ? ' (turned)' : '') +
      ', ' + down.toFixed(1) + ' deg down' :
      ' pitch ' + this.pitch.toFixed(1) + ' deg, heading ' + this.hdg.toFixed(1) + ' deg';
  }
};

/* ---------------- tooltip ---------------- */
var TIP = null;
function tipMove(e){
  var t = e.target, v = e.currentTarget._view;
  var n = t && t.getAttribute ? t.getAttribute('data-t') : null;
  if (n == null || !v || !v.tips[n]) { tipHide(); return; }
  TIP.textContent = v.tips[n];
  TIP.style.display = 'block';
  var x = e.clientX + 14, y = e.clientY + 14;
  var r = TIP.getBoundingClientRect();
  if (x + r.width > window.innerWidth - 8) x = e.clientX - r.width - 10;
  if (y + r.height > window.innerHeight - 8) y = e.clientY - r.height - 10;
  TIP.style.left = x + 'px'; TIP.style.top = y + 'px';
}
function tipHide(){ if (TIP) TIP.style.display = 'none'; }

/* ---------------- rail ---------------- */
function buildRail(){
  var rail = document.getElementById('rail');
  M.lanes.forEach(function(lane){
    rail.appendChild(el('h2', null, lane.lane));
    var runs = {};
    lane.captures.forEach(function(id){ runs[BY[id].runId] = 1; });
    var multi = Object.keys(runs).length > 1, lastRun = null;
    lane.captures.forEach(function(id){
      var c = BY[id];
      if (multi && c.runId !== lastRun) { rail.appendChild(el('div', 'run', c.runId)); lastRun = c.runId; }
      var d = el('div', 'cap' + (c.error ? ' err' : ''));
      d.id = 'r-' + id;
      d.appendChild(el('div', 'l', c.label));
      d.appendChild(el('div', 's', c.error ? 'unreadable: ' + c.error :
        (c.scene || '?') + '  ' + c.utText + '  ' + c.ghosts.length + ' ghost' + (c.ghosts.length === 1 ? '' : 's') +
        (c.hasPng ? '' : '  (no screenshot)') +
        (c.offConic ? '  ' + c.offConic + ' OFF ITS CONIC' : '') +
        (c.faded ? '  ' + c.faded + ' faded' : '')));
      if (c.offConic) d.classList.add('offc');
      d.title = c.error ? c.file + ': ' + c.error : c.file;
      d.onclick = function(){ select(id); };
      rail.appendChild(d);
    });
  });
}

/* ---------------- the selected capture ---------------- */
function legend(c, host){
  var lg = el('div'); lg.id = 'legend';
  function sample(style, extra){
    return '<svg width="34" height="10"><path d="M2 5H32" style="fill:none;'+style+'" '+(extra||'')+'/></svg>';
  }
  c.ghosts.forEach(function(g, gi){
    var lab = el('label');
    var cb = el('input'); cb.type = 'checkbox'; cb.checked = !S.hidden[gi];
    cb.onchange = function(){ S.hidden[gi] = !cb.checked; S.views.forEach(function(v){ v.draw(); }); };
    lab.appendChild(cb);
    var sw = el('span', 'sw'); sw.style.background = gcol(g.colour);
    lab.appendChild(sw);
    lab.appendChild(document.createTextNode(g.vesselName));
    lab.title = 'recording ' + g.recordingId + (g.pid != null ? ', pid ' + g.pid : '');
    lg.appendChild(lab);
  });
  var key = el('span');
  key.innerHTML =
    sample('stroke:var(--ink);stroke-width:1.8') + ' orbit &nbsp; ' +
    sample('stroke:var(--ink);stroke-width:2.4') + ' polyline &nbsp; ' +
    sample('stroke:var(--ink);stroke-width:2;stroke-dasharray:1 4;stroke-linecap:round') + ' tail &nbsp; ' +
    sample('stroke:var(--ink);stroke-width:3.2;stroke-dasharray:3 2') + ' bridge &nbsp; ' +
    sample('stroke:var(--ref);stroke-width:1.6;stroke-dasharray:5 4') + ' reference path &nbsp; ' +
    sample('stroke:var(--ink);stroke-width:1.8;opacity:.38') + ' behind a body &nbsp; ' +
    '<svg width="12" height="12"><circle cx="6" cy="6" r="3.6" style="fill:var(--ink)"/></svg> start ' +
    '<svg width="12" height="12"><circle cx="6" cy="6" r="3.4" style="fill:var(--panel);stroke:var(--ink);stroke-width:1.8"/></svg> end &nbsp; ' +
    '<svg width="14" height="14"><path d="M7 1L13 7L7 13L1 7Z" style="fill:var(--ink)"/></svg> icon ' +
    '<svg width="14" height="14"><path d="M7 1L13 7L7 13L1 7Z" style="fill:none;stroke:var(--ink);stroke-dasharray:2 2"/></svg> icon not drawn';
  lg.appendChild(key);
  host.appendChild(lg);
}

function cameraSpec(c, A){
  var B = bodyMap(c), cam = c.camera, frame = c.root, center = [0,0,0], note = '';
  if (cam.target && B[cam.target]) frame = cam.target;
  else {
    var g = c.ghosts.filter(function(g){ return g.vesselName === cam.target && B[g.body]; })[0];
    if (g) { frame = g.body; center = g.icon.pos || (g.lines[0] && g.lines[0].body === g.body && g.lines[0].points[0]) || [0,0,0]; }
    else note = ' (target ' + (cam.target || 'none') + ' not found; centred on ' + c.root + ')';
  }
  var own = !!(cam.forward && cam.up), fov = cam.fovDeg != null ? cam.fovDeg : M.cameraFovDeg;
  var how = own ? 'the recorded camera frame (forward, up)' : 'pitch ' + (cam.pitchDeg != null ? cam.pitchDeg : 90) +
    ' and heading ' + (cam.headingDeg != null ? cam.headingDeg : 0) + ' (no recorded camera frame)';
  return {cap:c, A:A, it:content(c, frame, null), frame:null, mode:'cam',
          pitch: cam.pitchDeg != null ? cam.pitchDeg : 90, hdg: cam.headingDeg != null ? cam.headingDeg : 0,
          camF: own ? cam.forward : null, camU: own ? cam.up : null,
          persp: cam.distance != null, distance: cam.distance || 1e6, fov: fov,
          center: center, fitDefault: 'camera',
          title: 'As the camera saw it' + note,
          fits: [['camera', 'camera', 'The dump\'s own view: ' + how + ', distance ' + fmtD(cam.distance) + ', ' +
                   fov.toFixed(1) + ' deg vertical field of view' + (cam.fovDeg != null ? '' : ' (assumed; the dump has none)') + '.'],
                 ['lines', 'fit lines', 'Zoom to every drawn line and icon.']]};
}

function measuredTables(c, host){
  c.ghosts.forEach(function(g){
    var m = g.measured;
    var hd = el('div', 'gh');
    var md = g.markerDecision;
    hd.innerHTML = '<span class="sw" style="background:'+gcol(g.colour)+'"></span><b>' + esc(g.vesselName) + '</b> ' +
      '<span class="facts">recording ' + esc(g.recordingId || '-') +
      (g.pid != null ? (g.pid === 0 ? ', pid 0 (no ghost map vessel)' : ', pid ' + g.pid) : '') +
      (g.committedIndex != null ? (g.committedIndex < 0 ? ', not in the committed list' : ', committed #' + g.committedIndex) : '') +
      (g.loopCycle != null ? ', loop cycle ' + g.loopCycle : '') +
      (g.sampleUT != null ? ', sample ut ' + g.sampleUT.toFixed(1) : '') +
      (g.treatment ? ', treatment <span class="tok" title="' + esc(g.treatmentText || 'not a token this page knows') + '">' +
        esc(g.treatment) + '</span>' : '') + ', body now ' + esc(g.body || '-') +
      ', icon ' + (g.icon.drawn ? 'drawn' : (g.icon.faded ? 'not drawn (stock fade)' : 'not drawn')) + (g.icon.source ? ' (' + esc(g.icon.source) + ')' : '') +
      (md ? ', marker decision: draw ' + (md.draw ? 'yes' : 'no') + ', director traced path ' + (md.directorTracedPath ? 'yes' : 'no') +
        ', polyline owning ' + (md.polylineOwning ? 'yes' : 'no') + ', icon suppressed ' + (md.iconSuppressed ? 'yes' : 'no') : '') +
      '</span>';
    host.appendChild(hd);
    var p = el('div', 'facts');
    p.innerHTML = 'Icon to the nearest drawn point of its own lines: <b>' + (m.icon ? esc(m.icon.text) +
      '</b> (line #' + m.icon.line + ')' : '-</b> (no icon position)') + '. Lines in ' +
      (m.timeOrdered ? 'time order' : 'dump order (not every drawn line has a time range)') +
      '; the gap column joins drawn lines only (an inactive line is listed, not drawn or measured). ' +
      'Opacity is stock\'s orbit-line fade (0 = stock did not draw it this frame). Conic fit compares a drawn ' +
      'orbit line\'s vertex distances from the body centre with its own conic\'s apsis radii (2%).';
    if (m.icon) p.title = m.icon.m.toFixed(3) + ' m';
    host.appendChild(p);
    var t = el('table', 'm');
    var hdr = ['#','kind','body','active','opacity','points','time range','lowest alt','highest alt',
               'conic Pe / Ap alt','conic fit','gap to next line','reference: max distance'];
    t.innerHTML = '<tr>' + hdr.map(function(x){ return '<th>'+x+'</th>'; }).join('') + '</tr>';
    m.order.forEach(function(i){
      var r = m.lines[i];
      function num(v){ return '<td class="n" title="'+(v && v.m != null ? v.m.toFixed(3)+' m' : '')+'">'+esc(v ? v.text : '-')+'</td>'; }
      var gap = r.gapToNext ? '<td class="n" title="'+r.gapToNext.m.toFixed(3)+' m">'+esc(r.gapToNext.text)+' to #'+r.gapToNext.to+
        (r.gapToNext.crossBody ? ' (across bodies)' : '')+'</td>' : '<td class="n">-</td>';
      var conic = r.conicPe ? '<td class="n" title="'+r.conicPe.m.toFixed(3)+' m">'+esc(r.conicPe.text)+' / '+esc(r.conicAp && r.conicAp.m != null ? r.conicAp.text : 'open')+'</td>' : '<td class="n">-</td>';
      var ref = r.reference ? '<td class="n" title="'+r.reference.m.toFixed(3)+' m, '+r.referencePoints+' reference points">'+esc(r.reference.text)+' ('+esc(r.referenceKind || '?')+')</td>' : '<td class="n">-</td>';
      var fit = r.conicFit ? '<td class="'+(r.conicFit.fits ? 'fit' : 'bad')+'" title="'+esc(r.conicFit.rule)+
        ': vertices '+r.conicFit.minR.toFixed(0)+' .. '+r.conicFit.maxR.toFixed(0)+' m from the centre, conic '+
        r.conicFit.rp.toFixed(0)+' .. '+(r.conicFit.ra != null ? r.conicFit.ra.toFixed(0) : 'open')+' m">'+
        (r.conicFit.fits ? 'fits its conic' : 'OFF ITS CONIC')+'</td>' : '<td>-</td>';
      var act = r.faded ? 'no, not drawn (stock fade)' : (r.active ? 'yes' : 'no (not drawn)');
      var op = '<td class="n">' + (r.opacity != null ? r.opacity.toFixed(3) : '-') + '</td>';
      var tr = el('tr');
      tr.innerHTML = '<td>'+i+'</td><td>'+esc(r.kind)+'</td><td>'+esc(r.body)+'</td><td>'+act+
        '</td>'+op+'<td class="n">'+r.points+'</td><td>'+esc(r.utText)+'</td>'+num(r.lowest)+num(r.highest)+conic+fit+gap+ref;
      t.appendChild(tr);
    });
    host.appendChild(t);
  });
}

function select(id){
  var c = BY[id];
  if (!c) return;
  S.cap = id; S.views = []; S.hidden = {};
  Array.prototype.forEach.call(document.querySelectorAll('#rail .cap'), function(d){
    d.classList.toggle('sel', d.id === 'r-' + id); });
  var row = document.getElementById('r-' + id);
  if (row && row.scrollIntoView) row.scrollIntoView({block:'nearest'});
  try { history.replaceState(null, '', '#cap=' + encodeURIComponent(id)); } catch (e) {}
  var main = document.getElementById('main');
  main.innerHTML = '';
  main.appendChild(el('h2', null, c.label));
  if (c.error) {
    main.appendChild(el('div', 'facts', c.runId + ' / ' + c.file));
    main.appendChild(el('div', 'err', 'This capture could not be read: ' + c.error));
    return;
  }
  var f = el('div', 'facts');
  var cam = c.camera;
  f.innerHTML = '<b>' + esc(c.lane) + '</b> run ' + esc(c.runId) + ' &nbsp; scene <b>' + esc(c.scene || '?') +
    '</b> &nbsp; game time <b>' + esc(c.utText) + '</b> (ut ' + (c.ut != null ? c.ut.toFixed(1) : '-') + ' s) &nbsp; frame ' +
    (c.frame != null ? c.frame : '-') + ' &nbsp; warp x' + (c.warpRate != null ? c.warpRate : '-') +
    ' &nbsp; camera ' + esc(cam.target || '-') + ' pitch ' + (cam.pitchDeg != null ? cam.pitchDeg.toFixed(1) : '-') +
    ' heading ' + (cam.headingDeg != null ? cam.headingDeg.toFixed(1) : '-') + ' distance ' + esc(fmtD(cam.distance)) +
    (cam.fovDeg != null ? ' fov ' + cam.fovDeg.toFixed(1) : '') +
    ' &nbsp; ' + c.counts.held.ghosts + ' ghosts, ' + c.counts.held.lines + ' lines, ' + c.counts.held.points + ' points';
  var dc = c.counts.declared, dr = dc.dropped, drop = [];
  if (dr) Object.keys(dr).forEach(function(k){ if (dr[k]) drop.push(k + ' ' + dr[k]); });
  if (dc.unknownBodyItems) f.innerHTML += ' &nbsp; <span class="bad">' + dc.unknownBodyItems + ' unknown-body item' +
    (dc.unknownBodyItems === 1 ? '' : 's') + ' skipped by the writer</span>';
  if (drop.length) f.innerHTML += ' &nbsp; <span class="bad">dropped by the writer: ' + esc(drop.join(', ')) + '</span>';
  if (c.cameraCheck) f.innerHTML += ' &nbsp; <span title="Angle between the view direction the page derives from pitch and heading and the recorded camera.forward, and the same for up. The camera view uses the recorded frame.">' +
    'pitch/heading model vs recorded camera: forward ' + c.cameraCheck.forwardDeg.toFixed(1) + ' deg, up ' + c.cameraCheck.upDeg.toFixed(1) + ' deg apart</span>';
  main.appendChild(f);
  if (c.notes && c.notes.length) {
    var n = el('div', 'notes');
    n.innerHTML = c.notes.map(esc).join('<br>');
    main.appendChild(n);
  }
  legend(c, main);
  var A = axesOf(c);
  /* camera vs screenshot */
  main.appendChild(el('h3', null, 'The camera view beside the screenshot'));
  var row1 = el('div', 'row');
  main.appendChild(row1);
  var ph = c.photo;
  var aspect = ph && ph.w && ph.h ? ph.h / ph.w : 0.5625;
  var camSpec = cameraSpec(c, A);
  camSpec.aspect = aspect;
  var camHost = el('div'); camHost.style.flex = '1 1 420px'; camHost.style.minWidth = '300px';
  row1.appendChild(camHost);
  var pdiv = el('div', 'photo');
  if (ph && ph.src) {
    var img = el('img'); img.src = ph.src; img.alt = 'screenshot ' + c.label;
    img.title = 'click to enlarge / shrink';
    img.onclick = function(){ pdiv.classList.toggle('big'); };
    pdiv.appendChild(img);
    if (ph.note) pdiv.appendChild(el('div', 'pn', ph.note));
  } else {
    pdiv.appendChild(el('div', 'none', ph && ph.note ? ph.note : 'no screenshot with this label'));
  }
  row1.appendChild(pdiv);
  var v = new View(camSpec); v.mount(camHost); S.views.push(v);
  /* close-ups */
  c.closeups.forEach(function(name){
    main.appendChild(el('h3', null, 'Close-up: ' + name));
    var r = el('div', 'row');
    main.appendChild(r);
    var it = content(c, name, name);
    [['top', 'Top-down around ' + name], ['edge', 'Edge-on around ' + name]].forEach(function(m){
      var vv = new View({cap:c, A:A, it:it, frame:name, mode:m[0], title:m[1], height:400, fitDefault:'lines',
        fits:[['lines','fit lines','Zoom to the lines, icons and the body.'],
              ['body','near body','Zoom to three times ' + name + '\'s radius: ascents, parking orbits, landings.'],
              ['soi','fit SOI','Zoom to ' + name + '\'s sphere of influence.']]});
      vv.mount(r); S.views.push(vv);
    });
  });
  /* whole system */
  main.appendChild(el('h3', null, 'Whole system, top-down'));
  var r3 = el('div', 'row');
  main.appendChild(r3);
  var sys = new View({cap:c, A:A, it:content(c, c.root, null), frame:null, mode:'top',
    title:'Whole system from above', height:520, fitDefault:'all',
    fits:[['all','fit all','Every body and orbit ring.'],['lines','fit lines','Every drawn line and icon.']]});
  sys.mount(r3); S.views.push(sys);
  main.appendChild(el('h3', null, 'Measured'));
  measuredTables(c, main);
  S.views.forEach(function(vw){ vw.start(); });
}

function init(){
  TIP = document.getElementById('tip');
  var hdr = document.getElementById('hdr');
  var bad = M.captures.filter(function(c){ return c.error; });
  hdr.innerHTML = 'generated ' + esc(M.generatedUtc || '-') + ' &nbsp; ' + M.captures.length + ' capture' +
    (M.captures.length === 1 ? '' : 's') + ' in ' + M.lanes.length + ' lane' + (M.lanes.length === 1 ? '' : 's') +
    (bad.length ? ' &nbsp; <span class="bad">' + bad.length + ' unreadable: ' +
      bad.map(function(c){ return esc(c.file); }).join(', ') + '</span>' : '');
  document.getElementById('theme').onclick = function(){
    var r = document.documentElement, cur = r.getAttribute('data-theme');
    var dark = cur ? cur === 'dark' : (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
    r.setAttribute('data-theme', dark ? 'light' : 'dark');
    if (S.cap) select(S.cap);
  };
  buildRail();
  document.addEventListener('keydown', function(e){
    if (e.target && /INPUT|TEXTAREA|SELECT/.test(e.target.tagName)) return;
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
    e.preventDefault();
    var i = ORDER.indexOf(S.cap) + (e.key === 'ArrowDown' ? 1 : -1);
    if (i >= 0 && i < ORDER.length) select(ORDER[i]);
  });
  var want = null, m = /cap=([^&]+)/.exec(location.hash || '');
  if (m) want = decodeURIComponent(m[1]);
  if (want && BY[want]) select(want);
  else if (ORDER.length) select(ORDER[0]);
  else document.getElementById('main').appendChild(el('div', 'err', 'No .map.json captures were found.'));
  var rt = null;
  window.addEventListener('resize', function(){ clearTimeout(rt); rt = setTimeout(function(){ if (S.cap) select(S.cap); }, 200); });
}
init();
"""


def render_html(model):
    return "\n".join([
        "<!doctype html>",
        '<html lang="en"><head><meta charset="utf-8">',
        '<meta name="viewport" content="width=device-width,initial-scale=1">',
        "<title>Parsek map mirror</title>",
        "<style>%s</style>" % CSS,
        "</head><body>",
        '<div id="top"><h1>Parsek map mirror</h1><span class="meta" id="hdr"></span>'
        '<span class="sp"></span><button id="theme" title="Switch light / dark">theme</button></div>',
        '<div id="wrap"><div id="rail"></div><div id="main"></div></div>',
        '<div id="tip"></div>',
        "<script>window.__MAPMIRROR__=",
        json_for_script(model),
        ";</script>",
        "<script>%s</script>" % JS,
        "</body></html>",
        "",
    ])


def _expand(shots):
    dirs = []
    for d in shots:
        for part in ([d] if os.path.isdir(d) else sorted(glob.glob(d))):
            if os.path.isdir(part) and part not in dirs:
                dirs.append(part)
    return dirs


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--shots", action="append", required=True,
                    help="a `<runId>_shots` directory (glob allowed); repeatable")
    ap.add_argument("--out", required=True, help="the html page to write")
    ap.add_argument("--index", default=None, help="also write this index JSON")
    ap.add_argument("--budget-mb", type=float, default=16.0,
                    help="refuse to write a page larger than this (default 16)")
    ap.add_argument("--no-photos", action="store_true", help="do not inline screenshots")
    ap.add_argument("--stamp", default="",
                    help="the page's generation stamp; defaults to now (UTC). Pass "
                         "one for byte-identical output")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args(argv)

    dirs = _expand(args.shots)
    if not dirs:
        sys.stderr.write("map-mirror: no shots directories matched\n")
        return 2
    budget = int(args.budget_mb * 1024 * 1024)
    stamp = args.stamp or datetime.datetime.now(
        datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    # Measure the page without photographs first, so the photos get exactly what
    # is left of the budget (less a small margin for the data-URI wrappers).
    caps = scan_all(dirs, verbose=args.verbose)
    bare = build_model(dirs, with_photos=False, stamp=stamp, caps=caps)
    bare_size = len(render_html(bare).encode("utf-8"))
    if args.no_photos:
        model = bare
    else:
        model = build_model(dirs, with_photos=True,
                            payload_budget=max(0, budget - bare_size - 64 * 1024),
                            stamp=stamp, verbose=args.verbose, caps=caps)
    html = render_html(model)
    size = len(html.encode("utf-8"))
    # Measured BEFORE the file is opened: an over-budget page that has already
    # been written is an over-budget page someone will open anyway.
    if size > budget:
        sys.stderr.write(
            "map-mirror: REFUSED, %.2f MB is over the %.2f MB budget - nothing written "
            "to %s. Re-run with a larger --budget-mb, or with --no-photos.\n"
            % (size / 1048576.0, args.budget_mb, args.out))
        return 2
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(html)
    if args.index:
        with open(args.index, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(build_index(model), fh, indent=1, sort_keys=True)
            fh.write("\n")
    bad = len([c for c in model["captures"] if c.get("error")])
    sys.stderr.write("map-mirror: %d captures (%d unreadable) in %d lanes -> %s "
                     "(%.2f MB of %.2f MB)\n"
                     % (len(model["captures"]), bad, len(model["lanes"]), args.out,
                        size / 1048576.0, args.budget_mb))
    return 0


if __name__ == "__main__":
    sys.exit(main())
