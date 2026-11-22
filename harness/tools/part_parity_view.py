#!/usr/bin/env python3
"""Part ghost parity viewer: a static offline site over one or more sweep directories.

docs/dev/design-part-ghost-parity.md section 8. A sweep directory is what the C#
``PartParitySweep`` verb writes (``manifest.json``, schema ``parsek-part-parity/1``, and
per case ``<caseId>.png`` / ``<caseId>.thumb.png`` / ``<caseId>.part.json``), harvested by
``run.py`` into ``results/<runId>_partparity/<label>/``. This tool reads one or more of
them (shards, or two install profiles such as stock and ReStock) and writes:

  * ``index.html`` - the page: vanilla JS and CSS inline, no CDN, works from ``file://``.
  * ``data.js``    - every manifest and sidecar as ONE JS literal
    (``window.PARITY_DATA = {...};``), because a ``file://`` page cannot ``fetch`` JSON.

Images are never re-encoded: the page crops the atlas with CSS ``background-position``
and differences the two tiles with ``mix-blend-mode: difference`` (canvas pixel reads are
blocked on ``file://``, design D8). They are referenced IN PLACE by relative path, or
copied under ``<out>/img/<k>/`` (``--images copy``; the default when ``--out`` is given,
and the automatic fallback for a sweep on another drive, where no relative path exists).

Verdicts are NOT reimplemented here: every part case is judged by
``harness/lib/partparity.py`` (``judge_case`` / ``judge_view``), and the header's gate
checks are its ``check_controls`` / ``check_completeness`` / ``check_state_press``.
Controls are not judged as parts (design 6): a control's chip is its recorded outcome in
the manifest.

State cases (design Appendix D.7): a part groups part > skin > state > view. A state case
(any state but ``stowed``) carries two judged routes (``routes.event`` / ``routes.start``,
partparity's ``judge_route`` through ``judge_case``) and its ``stateFlags``; each view row
shows the stowed column (the paired stowed case's REAL tile, ``stowedCaseId``, when its
sweep is loaded, else the case's own ``ghost-stowed`` reference row), REAL, GHOST (event
route), GHOST (start route) and the DIFFERENCE. Sweeps of one profile merge (a stowed
sweep and its state sweeps join one part list); controls are exempt: each sweep's
controls keep their own id (``<profile>/<sweep>/<control>``) and controls section.

Layout of this module: a PURE core (``build_site_data`` and the helpers it calls:
profile naming, case rows, facets, regression diff, cross-profile links,
``render_data_js``) that takes parsed content and returns plain data, unit-tested in
``harness/lib/test_part_parity_view.py``; and a thin I/O shell (``load_sweep``,
``plan_images``, ``main``).

Extension point (design 9.2, phase P7): GIF / APNG animation strips are written by this
tool, not the browser, once a sweep carries a frame axis. ``--gif`` is reserved for that
and refuses today; a row's ``strips`` slot is the place the page will read them from.

Usage::

    python harness/tools/part_parity_view.py <dir> [<dir> ...] [--baseline <dir>]
        [--out <dir>] [--images auto|copy|in-place]

A ``<dir>`` may be written ``NAME=<dir>`` to name its profile explicitly. Default output:
``<first dir>/viewer/``. The tool refuses to write into the repository tree (except the
gitignored ``harness/results/``). Stdlib only; ASCII only.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import urllib.parse
from typing import Any, Callable, Dict, Iterable, List, Optional, Sequence, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_LIB = os.path.join(os.path.dirname(_HERE), "lib")
if _LIB not in sys.path:
    sys.path.insert(0, _LIB)

import partparity as pp  # noqa: E402

DATA_SCHEMA = "parsek-part-parity-view/1"
DATA_JS_FILE = "data.js"
INDEX_FILE = "index.html"
DEFAULT_OUT_LEAF = "viewer"
DATA_JS_PREFIX = "window.PARITY_DATA = "

KIND_CONTROL = "control"
KIND_PART = "part"

# A control's chip: its recorded outcome, not a part verdict (design 6).
VERDICT_CONTROL_OK = "control-ok"
VERDICT_CONTROL_BAD = "control-bad"
VERDICT_ORDER: Tuple[str, ...] = (
    VERDICT_CONTROL_BAD, VERDICT_CONTROL_OK, pp.VERDICT_ERROR, pp.VERDICT_FAIL,
    pp.VERDICT_WARN, pp.VERDICT_PASS,
)
PART_VERDICTS: Tuple[str, ...] = (pp.VERDICT_PASS, pp.VERDICT_WARN, pp.VERDICT_FAIL,
                                  pp.VERDICT_ERROR)

# The page's reading order (owner): flagged first - fail, then warn, then error (a setup
# fault, not a ghost finding) - then pass. A part group's verdict is its worst skin by
# this order, so a part with one failing skin and one setup fault reads fail.
SEVERITY: Dict[str, int] = {pp.VERDICT_FAIL: 3, pp.VERDICT_WARN: 2, pp.VERDICT_ERROR: 1,
                            pp.VERDICT_PASS: 0}

SKIN_BASE = "base"
SKIN_VARIANT = "variant"
SKIN_B9 = "b9"
_SKIN_KIND_ORDER = {SKIN_BASE: 0, SKIN_VARIANT: 1, SKIN_B9: 2}
B9_MODULE = "ModuleB9PartSwitch"
TRIGGER_MAX_PHRASES = 2

# Regression mode (``--baseline``): a measure "moved" when it changed by more than these.
# The instrument is deterministic (Appendix B: identical sides read iou 1.0000), so the
# tolerances only absorb MSAA edge noise.
REG_BLOB_MOVE_PX = 8
REG_COLOR_MOVE_PX = 8
REG_IOU_MOVE = 0.002
REG_MOVED = "moved"
REG_NEW = "new"
REG_SAME = "same"
REG_GONE = "gone"

# A sidecar the row could not read.
SIDECAR_MISSING = "sidecar-missing"
SIDECAR_UNREADABLE = "sidecar-unreadable"

# Per-view keys carried to the page (the sidecar's view object minus bulky ones).
VIEW_FIELDS: Tuple[str, ...] = (
    "view", "width", "height", "realPx", "ghostPx", "andPx", "orPx",
    "realOnlyPx", "ghostOnlyPx", "realOnlyTolPx", "ghostOnlyTolPx",
    "realOnlyBlobPx", "ghostOnlyBlobPx", "realOnlyBlobPct", "ghostOnlyBlobPct",
    "bbox", "centroid", "touchesBorder", "colorOverlapPx", "colorOverPx",
    "colorBlobPx", "colorP99", "colorBlockMax", "iou", "matte",
)

# The state flag kinds the page filters on (design D.7), in this order.
STATE_FLAG_FILTER_KINDS: Tuple[str, ...] = (
    pp.FLAG_STATE_NOT_MODELLED_PREFIX.rstrip(":"), pp.FLAG_STATE_NOT_RECORDED,
    pp.FLAG_START_POSE_NOT_FIRST_FRAME, pp.FLAG_STATE_INVISIBLE,
)
ROUTES: Tuple[str, ...] = (pp.ROUTE_EVENT, pp.ROUTE_START)
# The control set a manifest's settings recompute to, by kind (regression pairs controls
# by profile, kind and id, since a stowed sweep and its state sweeps share ctl-empty).
CONTROL_SET_STOWED = "stowed"
CONTROL_SET_ORBIT = "orbit"
CONTROL_SET_AIR = "air"

_PROFILE_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
_NAMED_DIR_RE = re.compile(r"^([A-Za-z0-9][A-Za-z0-9._-]{0,63})=(.+)$")


# ---------------------------------------------------------------------------
# Pure core.
# ---------------------------------------------------------------------------

def sanitize_name(text: str) -> str:
    """Map to the profile alphabet ``[A-Za-z0-9._-]``; never empty, never all dots."""
    out = re.sub(r"[^A-Za-z0-9._-]", "-", text or "").strip("-")[:64]
    if not out or set(out) <= {"."}:
        return "sweep"
    return out


def profile_name(manifest: Optional[Dict], dir_name: str,
                 explicit: Optional[str] = None) -> Tuple[str, Optional[str]]:
    """The profile a sweep's cases are filed under, and a note when it is not the
    manifest's own. The manifest's ``profile`` is used when it is a plain name; the
    P2/P3 writer records the KSP root's last path segment, which on the harness installs
    reads ``..`` (``KSPUtil.ApplicationRootPath`` ends in ``KSP_x64_Data/../``), so such
    a value falls back to the sweep's ``label``, then the directory name."""
    recorded = manifest.get("profile") if isinstance(manifest, dict) else None
    if explicit:
        return sanitize_name(explicit), None
    if isinstance(recorded, str) and _PROFILE_RE.match(recorded) and set(recorded) - {"."}:
        return recorded, None
    label = manifest.get("label") if isinstance(manifest, dict) else None
    fallback = label if isinstance(label, str) and label.strip() else dir_name
    return sanitize_name(fallback), "profile recorded as %r; named after the %s" % (
        recorded, "label" if fallback is label else "directory")


def ordered_case_ids(manifest: Optional[Dict], sidecar_ids: Iterable[str]) -> List[Tuple[str, bool]]:
    """Every case of a sweep, as (caseId, listedInManifest): the controls first (the
    run's control set in its order, design 4.5 / D.2, then any other known control on
    disk, then any other control the manifest records), then the manifest's planned
    cases in index order, then any sidecar on disk the manifest does not list, sorted."""
    on_disk = set(sidecar_ids)
    out: List[Tuple[str, bool]] = []
    seen = set()
    m = manifest if isinstance(manifest, dict) else {}
    recorded_controls = [c.get("id") for c in m.get("controls") or []
                         if isinstance(c, dict) and isinstance(c.get("id"), str)]
    for cid in list(pp.manifest_control_set(m)) + list(pp.ALL_CONTROL_IDS) + recorded_controls:
        if cid in seen or (cid not in recorded_controls and cid not in on_disk):
            continue
        seen.add(cid)
        out.append((cid, cid in recorded_controls))
    planned = [c for c in m.get("cases") or [] if isinstance(c, dict)
               and isinstance(c.get("caseId"), str)]
    planned.sort(key=lambda c: (c.get("index") if isinstance(c.get("index"), int) else 1 << 30,
                                c["caseId"]))
    for c in planned:
        if c["caseId"] not in seen:
            seen.add(c["caseId"])
            out.append((c["caseId"], True))
    for cid in sorted(on_disk - seen):
        out.append((cid, False))
    return out


def _min_iou(views: Sequence[Dict]) -> Optional[float]:
    vals = [pp._num(v.get("iou")) for v in views]
    vals = [v for v in vals if v is not None]
    return min(vals) if vals else None


def _blank(value: Any) -> Optional[str]:
    if value is None:
        return None
    text = str(value)
    return text if text != "" else None


def skin_proof(skin: Optional[Dict]) -> Dict[str, Any]:
    """The sidecar's skin block plus the fields to highlight: ``requested != real``
    (exact: the setup fault ``skin-not-applied``), ``real != ghost`` (case-insensitive,
    the way the builder matches a VARIANT: the ghost defect ``skin-mismatch``), and a
    ``snapshot`` that differs from ``real`` (what the ghost was built from). A name that
    was not read (null or empty) is never compared."""
    s = skin if isinstance(skin, dict) else {}
    proof = {k: s.get(k) for k in pp.SKIN_KEYS}
    marks: Dict[str, str] = {}
    req, real, snap, ghost = (_blank(s.get(k)) for k in ("requested", "real", "snapshot", "ghost"))
    if req is not None and real != req:
        marks["requested"] = marks["real"] = "setup"
    if real is not None and ghost is not None and real.lower() != ghost.lower():
        marks["real"] = marks.get("real", "ghost")
        marks["ghost"] = "ghost"
    if real is not None and snap is not None and real.lower() != snap.lower():
        marks["snapshot"] = "snapshot"
    proof["marks"] = marks
    return proof


def _control_index(manifest: Optional[Dict]) -> Dict[str, Dict]:
    out = {}
    for c in (manifest or {}).get("controls") or []:
        if isinstance(c, dict) and isinstance(c.get("id"), str):
            out[c["id"]] = c
    return out


def _summary_index(manifest: Optional[Dict]) -> Dict[str, Dict]:
    out = {}
    for c in (manifest or {}).get("cases") or []:
        if isinstance(c, dict) and isinstance(c.get("caseId"), str):
            out[c["caseId"]] = c
    return out


_REASON_RES: Tuple[Tuple["re.Pattern[str]", Callable[["re.Match[str]"], str]], ...] = (
    (re.compile(r"^missing-rendered:(.*)$"), lambda m: "ghost missing mesh %s" % m.group(1)),
    (re.compile(r"^extra-rendered:(.*)$"), lambda m: "ghost has extra mesh %s" % m.group(1)),
    (re.compile(r"^shape-blob-px:([^=]*)=(\d+)$"),
     lambda m: "shape blob %s px in %s" % (m.group(2), m.group(1))),
    (re.compile(r"^shape-blob-pct:([^=]*)=([\d.]+)$"),
     lambda m: "shape blob %s%% of the box in %s" % (m.group(2), m.group(1))),
    (re.compile(r"^color-blob-px:([^=]*)=(\d+)$"),
     lambda m: "colour blob %s px in %s" % (m.group(2), m.group(1))),
    (re.compile(r"^skin-mismatch:real=(.*) ghost=(.*)$"),
     lambda m: "skin mismatch: real=%s ghost=%s" % (m.group(1), m.group(2))),
    (re.compile(r"^skin-not-applied:requested=(.*) real=(.*)$"),
     lambda m: "skin not applied: requested=%s real=%s" % (m.group(1), m.group(2))),
    (re.compile(r"^skin-not-modelled:b9$"),
     lambda m: "skin not modelled: the ghost applies no B9 subtype (prefab look)"),
    (re.compile(r"^setup:(.*)$"), lambda m: "setup fault: %s" % m.group(1)),
    (re.compile(r"^%s:no-views$" % pp.ROUTE_START),
     lambda m: "start route not measured (a view has no start block)"),
    (re.compile(r"^(%s|%s):(.+)$" % (pp.ROUTE_EVENT, pp.ROUTE_START)),
     lambda m: "%s route: %s" % (m.group(1), reason_phrase(m.group(2)))),
    (re.compile(r"^flag:%s(%s|%s):(.*)$" % (re.escape(pp.FLAG_STATE_NOT_MODELLED_PREFIX),
                                             pp.ROUTE_EVENT, pp.ROUTE_START)),
     lambda m: "state not modelled on the %s route (%s)" % (m.group(1), m.group(2))),
    (re.compile(r"^flag:%s$" % re.escape(pp.FLAG_STATE_NOT_RECORDED)),
     lambda m: "state not recorded: the press reached the state but no part event was polled"),
    (re.compile(r"^flag:%s$" % re.escape(pp.FLAG_START_POSE_NOT_FIRST_FRAME)),
     lambda m: "start route not at its pose on the first frame"),
    (re.compile(r"^flag:(.*)$"), lambda m: "flag %s" % m.group(1)),
    (re.compile(r"^press-proof: ?(.*)$"),
     lambda m: "press proof contradicts the setup status: %s" % m.group(1)),
    (re.compile(r"^no-views$"), lambda m: "no measured view"),
    (re.compile(r"^%s$" % SIDECAR_MISSING), lambda m: "no sidecar on disk"),
    (re.compile(r"^%s$" % SIDECAR_UNREADABLE), lambda m: "sidecar unreadable"),
)


def reason_phrase(reason: str) -> str:
    """One partparity reason token in plain words (unknown tokens pass through)."""
    for rx, fn in _REASON_RES:
        m = rx.match(reason)
        if m:
            return fn(m)
    return reason


_ROUTE_OPT = r"^(?:(%s|%s):)?" % (pp.ROUTE_EVENT, pp.ROUTE_START)
_BLOB_PX_RE = re.compile(_ROUTE_OPT + r"shape-blob-px:([^=]*)=")
_BLOB_PCT_RE = re.compile(_ROUTE_OPT + r"shape-blob-pct:([^=]*)=")


def trigger_line(reasons: Sequence[str], limit: int = TRIGGER_MAX_PHRASES) -> str:
    """What triggered a verdict, in one short line: the first ``limit`` distinct phrases
    (a shape blob's px and % of one view of one route collapse to the px phrase), then
    ``+n more``.
    Empty for a case with no reason (a pass)."""
    phrases: List[str] = []
    px_views = {(m.group(1), m.group(2)) for m in (_BLOB_PX_RE.match(r) for r in reasons) if m}
    for r in reasons:
        m = _BLOB_PCT_RE.match(r)
        if m and (m.group(1), m.group(2)) in px_views:
            continue
        p = reason_phrase(r)
        if p not in phrases:
            phrases.append(p)
    if not phrases:
        return ""
    head = "; ".join(phrases[:limit])
    return head + (" (+%d more)" % (len(phrases) - limit) if len(phrases) > limit else "")


def worst(verdicts: Iterable[str]) -> str:
    """The worst part verdict by SEVERITY (pass when there is none)."""
    out = pp.VERDICT_PASS
    for v in verdicts:
        if SEVERITY.get(v, 0) > SEVERITY[out]:
            out = v
    return out


ImageUrl = Callable[[str], Optional[str]]


def state_label(state: Optional[str], context: Optional[str]) -> str:
    """A state sub-block's name: the state word, with the context when it is not
    ``alone`` (``stowed (stacked)``, ``semi (air)``)."""
    st = state or pp.STATE_STOWED
    ctx = context or pp.CONTEXT_ALONE
    return st if ctx == pp.CONTEXT_ALONE else "%s (%s)" % (st, ctx)


def state_word(state: Optional[str]) -> str:
    """The state word without the same-word tiebreak of design D.1 (``deployed-m3`` ->
    ``deployed``), what the state filter selects on."""
    st = state or pp.STATE_STOWED
    return st.split(pp.STATE_TIEBREAK_PREFIX, 1)[0]


def is_rest_case(row: Dict) -> bool:
    """The stowed case of a part / skin at ``stowed`` / ``alone``: the case a state case
    pairs with (``stowedCaseId``), and the only kind a stowed sweep holds."""
    return ((row.get("state") or pp.STATE_STOWED) == pp.STATE_STOWED
            and (row.get("context") or pp.CONTEXT_ALONE) == pp.CONTEXT_ALONE)


def control_set_kind(manifest: Optional[Dict]) -> str:
    """Which control set a sweep's settings recompute to (``partparity.control_set``)."""
    ids = pp.manifest_control_set(manifest)
    if ids == pp.CONTROL_IDS:
        return CONTROL_SET_STOWED
    return CONTROL_SET_AIR if ids == pp.CONTROL_IDS_AIR else CONTROL_SET_ORBIT


def _measure_row(v: Dict) -> Dict[str, Any]:
    """One view's (or one view's start block's) measures plus partparity's view verdict."""
    jv = pp.judge_view(v)
    row = {k: v.get(k) for k in VIEW_FIELDS}
    row["verdict"], row["reasons"] = jv.verdict, list(jv.reasons)
    row["trigger"] = trigger_line(jv.reasons)
    return row


def _route_row(route: "pp.RouteJudgement", measured: Sequence[Dict]) -> Dict[str, Any]:
    out = route.to_dict()
    out["trigger"] = trigger_line(route.reasons)
    out["minIou"] = _min_iou(measured)
    return out


def case_row(profile: str, case_id: str, sidecar: Any, summary: Optional[Dict],
             control: Optional[Dict], listed: bool, is_control: bool,
             image_url: Optional[ImageUrl], row_id: Optional[str] = None) -> Dict[str, Any]:
    """One case as the page reads it. ``sidecar`` is the parsed sidecar, None when the
    file is absent, or the string ``SIDECAR_UNREADABLE`` when it would not parse.
    ``image_url(file_name)`` returns the page-relative URL of a file of this sweep, or
    None when the file is absent. A state case carries its two judged routes and its
    state flags exactly as ``partparity.judge_case`` returns them."""
    sc = sidecar if isinstance(sidecar, dict) else None
    summ = summary or {}
    problem = None if sc is not None else (
        SIDECAR_UNREADABLE if sidecar == SIDECAR_UNREADABLE else SIDECAR_MISSING)
    views_raw = [v for v in (sc or {}).get("views") or [] if isinstance(v, dict)]
    state = (sc or {}).get("state") or summ.get("state")
    context = (sc or {}).get("context") or summ.get("context")
    is_state = pp.is_state_case(state)
    is_rest = is_rest_case({"state": state, "context": context})
    start_list = pp.start_views(sc) if sc is not None and is_state else None
    routes = None
    state_flags: List[str] = []
    if sc is not None:
        cv = pp.judge_case(sc)
        verdict, reasons = cv.verdict, list(cv.reasons)
        worst_view, blob_px, blob_pct, color_px = (cv.worst_view, cv.max_shape_blob_px,
                                                   cv.max_shape_blob_pct, cv.max_color_blob_px)
        if cv.is_state_case:
            measured = {pp.ROUTE_EVENT: views_raw, pp.ROUTE_START: start_list or []}
            routes = {r.route: _route_row(r, measured.get(r.route) or []) for r in cv.routes}
            state_flags = list(cv.state_flags)
    else:
        verdict, reasons = pp.VERDICT_ERROR, [problem]
        worst_view, blob_px, blob_pct = None, int(summ.get("maxShapeBlobPx") or 0), None
        color_px = int(summ.get("maxColorBlobPx") or 0)
    part_verdict = verdict
    if is_control:
        outcome = (control or {}).get("outcome")
        verdict = VERDICT_CONTROL_OK if outcome == pp.CONTROLS_OK else VERDICT_CONTROL_BAD
    views = []
    for i, v in enumerate(views_raw):
        row = _measure_row(v)
        start = start_list[i] if start_list is not None and i < len(start_list) else None
        row["start"] = _measure_row(start) if start is not None else None
        ref = v.get("stowedRef")
        row["stowedRef"] = {k: ref.get(k) for k in pp.STOWED_REF_KEYS} \
            if isinstance(ref, dict) else None
        views.append(row)
    structure = (sc or {}).get("structure") if isinstance((sc or {}).get("structure"), dict) else {}
    structure_start = (sc or {}).get("structureStart")
    missing = pp._rendered_entries(structure.get("missingOnGhost"))
    extra = pp._rendered_entries(structure.get("extraOnGhost"))
    skin = (sc or {}).get("skin")
    proof = skin_proof(skin)
    state_proof = (sc or {}).get("stateProof")
    state_proof = state_proof if isinstance(state_proof, dict) else None
    family = (state_proof or {}).get("family") or summ.get("family")
    atlas = (sc or {}).get("atlas") if isinstance((sc or {}).get("atlas"), dict) else {}
    atlas_file = atlas.get("file") or (case_id + pp.ATLAS_SUFFIX)
    setup = (sc or {}).get("setup") if isinstance((sc or {}).get("setup"), dict) else {}
    status = setup.get("status") if sc is not None else (summ.get("setupStatus") or problem)
    skin_display = (summ.get("skin") or _blank(proof.get("requested"))
                    or _blank(proof.get("real")) or "default")
    tags = sorted({str(t) for t in (sc or {}).get("tags") or []})
    if is_control:
        c = control or {}
        trigger = ("outcome %s: %s" % (c.get("outcome") or "absent", c.get("required") or "-"))
    else:
        trigger = trigger_line(reasons)
    index = summ.get("index") if isinstance(summ.get("index"), int) else None
    row = {
        "id": row_id or "%s/%s" % (profile, case_id),
        "profile": profile,
        "caseId": case_id,
        "regKey": case_id,
        "kind": KIND_CONTROL if is_control else KIND_PART,
        "sweep": None,
        "sweepIndex": None,
        "controlSet": None,
        "index": index,
        "trigger": trigger,
        "skinKind": None,
        "listed": listed,
        "sidecarProblem": problem,
        "part": (sc or {}).get("part") or summ.get("part"),
        "title": (sc or {}).get("title") or summ.get("title"),
        "source": (sc or {}).get("source") or summ.get("source"),
        "category": (sc or {}).get("category"),
        "skin": skin_display,
        "skinModule": proof.get("module"),
        "hasSkins": bool(_blank(proof.get("module"))),
        "skinProof": proof,
        "tags": tags,
        "flags": [str(f) for f in (sc or {}).get("flags") or []],
        "state": state,
        "context": context,
        "stateWord": state_word(state),
        "stateLabel": state_label(state, context),
        "isState": is_state,
        "isRest": is_rest,
        "family": family,
        "routes": routes,
        "stateFlags": state_flags,
        "stateProof": state_proof,
        "stowedCaseId": None if is_rest else (sc or {}).get("stowedCaseId"),
        "stowedPair": None,
        "verdict": verdict,
        "partVerdict": part_verdict,
        "reasons": reasons,
        "worstView": worst_view,
        "worstBlobPx": blob_px,
        "worstBlobPct": blob_pct,
        "colorBlobPx": color_px,
        "minIou": _min_iou(views_raw) if sc is not None else pp._num(summ.get("minIou")),
        "missingRendered": len(missing),
        "extraRendered": len(extra),
        "setupStatus": status,
        "setupDetail": setup.get("detail"),
        "control": None if not is_control else {
            k: (control or {}).get(k) for k in ("subject", "required", "outcome", "detail")},
        "atlas": image_url(atlas_file) if image_url else None,
        "tile": atlas.get("tile"),
        "atlasRows": list(atlas.get("rows") or pp.atlas_rows_for(is_state)),
        "atlasCols": list(atlas.get("cols") or []),
        "views": views,
        "structure": structure,
        "structureStart": structure_start if isinstance(structure_start, dict) else None,
        "partUrl": (sc or {}).get("partUrl"),
        "modelUrls": list((sc or {}).get("modelUrls") or []),
        "modules": (sc or {}).get("modules"),
        "framing": (sc or {}).get("framing"),
        "layers": (sc or {}).get("layers"),
        "ghostRootOffsetM": (sc or {}).get("ghostRootOffsetM"),
        "log": [str(l) for l in (sc or {}).get("log") or []],
        "accepted": None,
        "strips": None,
        "regression": None,
        "links": [],
    }
    return row


def sweep_rows(profile: str, manifest: Optional[Dict], sidecars: Dict[str, Any],
               image_url: Optional[ImageUrl], sweep_key: Optional[str] = None,
               sweep_index: int = 0) -> List[Dict[str, Any]]:
    """Every case row of one sweep, in ``ordered_case_ids`` order. A control's row id
    names its sweep (``<profile>/<sweep>/<control>``) when ``sweep_key`` is given: a
    stowed sweep and its state sweeps merge into one profile, and their controls are
    exempt from that merge (design D.7), so no control id is ever a duplicate."""
    controls = _control_index(manifest)
    summaries = _summary_index(manifest)
    set_kind = control_set_kind(manifest)
    rows = []
    for cid, listed in ordered_case_ids(manifest, sidecars.keys()):
        is_control = cid in controls or cid in pp.ALL_CONTROL_IDS
        rid = "%s/%s/%s" % (profile, sweep_key, cid) if is_control and sweep_key else None
        r = case_row(profile, cid, sidecars.get(cid), summaries.get(cid),
                     controls.get(cid), listed, is_control, image_url, rid)
        r["sweep"], r["sweepIndex"] = sweep_key, sweep_index
        if is_control:
            r["controlSet"] = set_kind
            r["regKey"] = "ctl:%s:%s" % (set_kind, cid)
        rows.append(r)
    return rows


def sweep_header(profile: str, note: Optional[str], dir_label: str,
                 manifest: Optional[Dict], manifest_error: Optional[str],
                 sidecars: Dict[str, Any], rows: Sequence[Dict],
                 inflight: Sequence[str] = (), isolate: Sequence[str] = (),
                 verdicts_doc: Optional[Dict] = None, duplicates: Sequence[str] = (),
                 sweep_key: Optional[str] = None) -> Dict:
    """The manifest header the page shows above the grid, with partparity's controls
    (state controls re-judged from their sidecars), completeness, press and writer
    cross-checks computed over this sweep."""
    m = manifest if isinstance(manifest, dict) else {}
    parsed = {k: v for k, v in sidecars.items() if isinstance(v, dict)}
    unreadable = {k: None for k, v in sidecars.items() if not isinstance(v, dict)}
    with_unreadable = dict(parsed, **unreadable)
    controls = pp.check_controls(m, with_unreadable) if manifest is not None else None
    completeness = (pp.check_completeness(m, with_unreadable, isolate, inflight)
                    if manifest is not None else None)
    press = pp.check_state_press(m, with_unreadable) if manifest is not None else None
    instrument: List[str] = []
    if manifest is not None:
        instrument.extend(pp.case_row_problems(m, with_unreadable))
        for p in (pp.run_shape_problem(m, with_unreadable), pp.control_set_disagreement(m)):
            if p:
                instrument.append(p)
    control_ids = set(_control_index(manifest)) | set(pp.ALL_CONTROL_IDS)
    state_cvs = [pp.judge_case(sc) for cid, sc in parsed.items()
                 if cid not in control_ids and pp.is_state_case(sc.get("state"))]
    counts = {v: 0 for v in VERDICT_ORDER}
    for r in rows:
        counts[r["verdict"]] = counts.get(r["verdict"], 0) + 1
    progress = m.get("progress") if isinstance(m.get("progress"), dict) else {}
    vd = None
    if isinstance(verdicts_doc, dict):
        vd = {"status": verdicts_doc.get("status"),
              "invalidSubkind": verdicts_doc.get("invalidSubkind"),
              "runId": verdicts_doc.get("runId"),
              "recomputed": len((verdicts_doc.get("recompute") or {}).get("cases") or []),
              "recomputeMismatches": len((verdicts_doc.get("recompute") or {}).get("mismatches") or []),
              "problems": list(verdicts_doc.get("problems") or [])}
    return {
        "profile": profile,
        "profileNote": note,
        "sweep": sweep_key,
        "dir": dir_label,
        "manifestError": manifest_error,
        "schema": m.get("schema"),
        "schemaOk": m.get("schema") == pp.MANIFEST_SCHEMA if manifest is not None else False,
        "label": m.get("label"),
        "recordedProfile": m.get("profile"),
        "dllHash": m.get("dllHash"),
        "gameDataMods": list(m.get("gameDataMods") or []),
        "tile": m.get("tile"),
        "views": list(m.get("views") or []),
        "settings": m.get("settings") if isinstance(m.get("settings"), dict) else {},
        "measure": m.get("measure") if isinstance(m.get("measure"), dict) else {},
        "progress": progress,
        "exclusions": [e for e in m.get("exclusions") or [] if isinstance(e, dict)],
        "controlSet": control_set_kind(manifest) if manifest is not None else None,
        "controls": None if controls is None else {
            "ok": controls.ok, "state": controls.state, "outcomes": dict(controls.outcomes),
            "problems": list(controls.problems),
            "expected": len(pp.manifest_control_set(m))},
        "completeness": None if completeness is None else {
            "ok": completeness.ok, "planned": completeness.planned,
            "complete": completeness.complete, "setupFaults": completeness.setup_faults,
            "problems": list(completeness.problems)},
        "statePress": None if press is None else press.to_dict(),
        "stateFlagCounts": pp.state_flag_counts(state_cvs) if state_cvs else None,
        "stateCases": len(state_cvs),
        "instrumentProblems": instrument,
        "crashed": list(progress.get("crashed") or []),
        "counts": counts,
        "cases": len(rows),
        "duplicates": list(duplicates),
        "verdictsFile": vd,
    }


def skin_kind(row: Dict, is_first: bool) -> str:
    """``base`` for a part's first case in manifest order (skin ordinal 0: the C# sorts
    a part's cases by ordinal, base, then the ModulePartVariants VARIANTs, then the
    B9 SUBTYPEs), ``b9`` for a case requesting a ModuleB9PartSwitch subtype, else
    ``variant``."""
    if is_first:
        return SKIN_BASE
    proof = row.get("skinProof") or {}
    if proof.get("module") == B9_MODULE and _blank(proof.get("requested")) is not None:
        return SKIN_B9
    return SKIN_VARIANT


def _order_key(row: Dict) -> Tuple[int, int, str]:
    """Load order: the sweep (merged sweeps of one profile keep their own index
    sequences), then the manifest index, then the case id."""
    return (row["sweepIndex"] if isinstance(row.get("sweepIndex"), int) else 0,
            row["index"] if isinstance(row.get("index"), int) else 1 << 30, row["caseId"])


def state_order_key(row: Dict) -> Tuple:
    """A state sub-block's place inside its skin (design D.7): stowed first (alone, then
    the context cases), then the families in the D.1 table order, each family's states
    in the ``KNOWN_STATES`` order; load order breaks a tie."""
    st = row.get("state") or pp.STATE_STOWED
    ctx = row.get("context") or pp.CONTEXT_ALONE
    fam = row.get("family")
    word = state_word(st)
    return ((0 if st == pp.STATE_STOWED else 1),
            (0 if ctx == pp.CONTEXT_ALONE else 1),
            pp.FAMILIES.index(fam) if fam in pp.FAMILIES else len(pp.FAMILIES),
            pp.KNOWN_STATES.index(word) if word in pp.KNOWN_STATES else len(pp.KNOWN_STATES),
            pp.KNOWN_CONTEXTS.index(ctx) if ctx in pp.KNOWN_CONTEXTS else len(pp.KNOWN_CONTEXTS),
            st) + _order_key(row)


def _case_trigger(rows: Sequence[Dict], verdict: str, label: Callable[[Dict], str],
                  noun: str) -> str:
    """The trigger of a block of cases: the first case at the block's verdict, named
    by ``label`` when the block holds more than one case, then ``+n more``."""
    flagged = [r for r in rows if r["verdict"] == verdict and verdict != pp.VERDICT_PASS]
    if not flagged:
        return ""
    if len(rows) == 1:
        return rows[0]["trigger"]
    first = flagged[0]
    return "%s: %s" % (label(first), first["trigger"]) + (
        " (+%d more %s %s)" % (len(flagged) - 1, noun, verdict) if len(flagged) > 1 else "")


def _flag_kinds(flags: Iterable[str]) -> List[str]:
    kinds = {f.split(":", 1)[0] for f in flags}
    return [k for k in STATE_FLAG_FILTER_KINDS if k in kinds]


def group_parts(rows: Sequence[Dict]) -> List[Dict[str, Any]]:
    """The page's blocks: one GROUP per (profile, part), holding its SKINS in the order
    base, variants, B9 subtypes, and inside each skin its STATES (design D.7): the
    stowed case first, then the state and context cases in ``state_order_key`` order.
    A part's skins are its stowed (``stowed`` / ``alone``) cases; a state case joins the
    skin of its paired stowed case (``stowedCaseId``), else the skin of the same name,
    else a skin of its own (a state sweep loaded without its stowed sweep). Each row's
    ``skinKind`` is set in place. A group's verdict is its worst case by SEVERITY and
    its trigger names that case's skin (and state) when the part has more than one
    case. Default order: severity (fail, warn, error, pass), then title. Controls are
    not grouped (they have their own section)."""
    buckets: Dict[Tuple[str, str], List[Dict]] = {}
    for r in rows:
        if r["kind"] != KIND_PART:
            continue
        name = str(r.get("part") or r["caseId"])
        buckets.setdefault((r["profile"], name.lower()), []).append(r)
    groups = []
    for (profile, _), members in buckets.items():
        members.sort(key=_order_key)
        rest = [r for r in members if is_rest_case(r)]
        for i, r in enumerate(rest):
            r["skinKind"] = skin_kind(r, i == 0)
        skins: List[Dict[str, Any]] = [{"skin": r["skin"], "skinKind": r["skinKind"],
                                        "rows": [r], "lead": r} for r in rest]
        by_rest = {r["caseId"]: s for r, s in zip(rest, skins)}
        for r in members:
            if is_rest_case(r):
                continue
            target = by_rest.get(r.get("stowedCaseId") or "")
            if target is None:
                target = next((s for s in skins
                               if str(s["skin"]).lower() == str(r["skin"]).lower()), None)
            if target is None:
                target = {"skin": r["skin"], "skinKind": skin_kind(r, not skins),
                          "rows": [], "lead": r}
                skins.append(target)
            r["skinKind"] = target["skinKind"]
            target["rows"].append(r)
        skins.sort(key=lambda s: (_SKIN_KIND_ORDER[s["skinKind"]],) + _order_key(s["lead"]))
        ordered: List[Dict] = []
        skin_out = []
        for s in skins:
            s["rows"].sort(key=state_order_key)
            ordered.extend(s["rows"])
            sv = worst(r["verdict"] for r in s["rows"])
            skin_out.append({
                "skin": s["skin"], "skinKind": s["skinKind"], "verdict": sv,
                "trigger": _case_trigger(s["rows"], sv, lambda r: r["stateLabel"], "state(s)"),
                "states": len([r for r in s["rows"] if not is_rest_case(r)]),
                "cases": [r["id"] for r in s["rows"]],
            })
        has_states = any(not is_rest_case(r) for r in ordered)
        verdict = worst(r["verdict"] for r in ordered)
        if has_states:
            trigger = _case_trigger(ordered, verdict,
                                    lambda r: "skin %s %s" % (r["skin"], r["stateLabel"]),
                                    "case(s)")
        else:
            trigger = _case_trigger(ordered, verdict, lambda r: "skin %s" % r["skin"],
                                    "skin(s)")
        lead = skins[0]["lead"]
        regs = [(r.get("regression") or {}).get("status") for r in ordered]
        reg = REG_MOVED if REG_MOVED in regs else (REG_NEW if REG_NEW in regs else (
            REG_SAME if REG_SAME in regs else None))
        groups.append({
            "id": "%s/part/%s" % (profile, lead.get("part") or lead["caseId"]),
            "profile": profile,
            "part": lead.get("part"),
            "title": lead.get("title"),
            "source": lead.get("source"),
            "category": lead.get("category"),
            "verdict": verdict,
            "trigger": trigger,
            "regression": reg,
            "cases": [r["id"] for r in ordered],
            "skins": skin_out,
            "hasStates": has_states,
            "states": [w for w in pp.KNOWN_STATES if any(r["stateWord"] == w for r in ordered)],
            "families": [f for f in pp.FAMILIES if any(r.get("family") == f for r in ordered)],
            "stateFlags": _flag_kinds(f for r in ordered for f in r.get("stateFlags") or []),
            "counts": {v: sum(1 for r in ordered if r["verdict"] == v) for v in PART_VERDICTS},
        })
    groups.sort(key=lambda g: (-SEVERITY.get(g["verdict"], 0),
                               str(g.get("title") or g.get("part") or "").lower(), g["id"]))
    links: Dict[str, List[Dict]] = {}
    for g in groups:
        links.setdefault(str(g.get("part") or "").lower(), []).append(g)
    for g in groups:
        g["links"] = [{"id": o["id"], "profile": o["profile"], "verdict": o["verdict"],
                       "case": o["cases"][0]}
                      for o in links.get(str(g.get("part") or "").lower(), [])
                      if o["profile"] != g["profile"] and g.get("part")]
    return groups


def page_counts(rows: Sequence[Dict], groups: Sequence[Dict]) -> Dict[str, Any]:
    """The header counter: part CASES by verdict (pass / warn / fail / error), the part
    groups by verdict, and the controls ok / bad."""
    parts = [r for r in rows if r["kind"] == KIND_PART]
    ctl = [r for r in rows if r["kind"] == KIND_CONTROL]
    return {
        "cases": {v: sum(1 for r in parts if r["verdict"] == v) for v in PART_VERDICTS},
        "parts": {v: sum(1 for g in groups if g["verdict"] == v) for v in PART_VERDICTS},
        "controls": {"ok": sum(1 for r in ctl if r["verdict"] == VERDICT_CONTROL_OK),
                     "bad": sum(1 for r in ctl if r["verdict"] != VERDICT_CONTROL_OK)},
    }


def facets(rows: Sequence[Dict], groups: Sequence[Dict]) -> Dict[str, List[List[Any]]]:
    """The filters the page offers (owner: verdict, source, a search box; design D.7:
    state, family and state flags once a state case is loaded), as
    [value, count-of-part-groups] pairs; ``regression`` only in regression mode."""
    def tally(values: Iterable[str]) -> Dict[str, int]:
        out: Dict[str, int] = {}
        for v in values:
            out[v] = out.get(v, 0) + 1
        return out
    sources = tally(str(g.get("source") or "(none)") for g in groups)
    verdicts = tally(g["verdict"] for g in groups)
    regs = tally(g["regression"] for g in groups if g.get("regression"))
    any_states = any(g.get("hasStates") for g in groups)
    states = tally(w for g in groups for w in g.get("states") or []) if any_states else {}
    families = tally(f for g in groups for f in g.get("families") or []) if any_states else {}
    flags = tally(f for g in groups for f in g.get("stateFlags") or [])
    return {
        "verdict": [[v, verdicts[v]] for v in (pp.VERDICT_FAIL, pp.VERDICT_WARN,
                                               pp.VERDICT_ERROR, pp.VERDICT_PASS)
                    if v in verdicts],
        "source": [[k, sources[k]] for k in sorted(sources, key=lambda s: s.lower())],
        "regression": [[k, regs[k]] for k in (REG_MOVED, REG_NEW, REG_SAME) if k in regs],
        "state": [[k, states[k]] for k in pp.KNOWN_STATES if k in states],
        "family": [[k, families[k]] for k in pp.FAMILIES if k in families],
        "stateFlag": [[k, flags[k]] for k in STATE_FLAG_FILTER_KINDS if k in flags],
    }


def _iou_change(label: str, a: Optional[float], b: Optional[float]) -> Optional[str]:
    if (a is None) != (b is None) or (a is not None and abs(b - a) > REG_IOU_MOVE):
        return "%s %s -> %s" % (label, "null" if a is None else "%.4f" % a,
                                "null" if b is None else "%.4f" % b)
    return None


def compare_rows(cur: Dict, base: Dict) -> List[str]:
    """What moved between a case and its baseline: verdict, setup status, the worst
    shape blob, the colour blob, the minimum IoU, the rendered missing / extra counts and
    the live skin names; for a state case also each judged route (``event`` / ``start``:
    verdict, shape and colour blob, IoU) and the state flags. Empty when nothing moved
    past the REG_* tolerances."""
    changes = []

    def diff_text(key: str):
        if cur.get(key) != base.get(key):
            changes.append("%s %s -> %s" % (key, base.get(key), cur.get(key)))

    diff_text("verdict")
    diff_text("setupStatus")
    for key, tol in (("worstBlobPx", REG_BLOB_MOVE_PX), ("colorBlobPx", REG_COLOR_MOVE_PX),
                     ("missingRendered", 0), ("extraRendered", 0)):
        a, b = int(base.get(key) or 0), int(cur.get(key) or 0)
        if abs(b - a) > tol:
            changes.append("%s %d -> %d" % (key, a, b))
    iou = _iou_change("minIou", base.get("minIou"), cur.get("minIou"))
    if iou:
        changes.append(iou)
    for key in ("real", "ghost"):
        x = _blank((base.get("skinProof") or {}).get(key))
        y = _blank((cur.get("skinProof") or {}).get(key))
        if (x or "").lower() != (y or "").lower():
            changes.append("skin.%s %s -> %s" % (key, x, y))
    base_routes, cur_routes = base.get("routes") or {}, cur.get("routes") or {}
    for route in ROUTES:
        a, b = base_routes.get(route), cur_routes.get(route)
        if a is None and b is None:
            continue
        if a is None or b is None:
            changes.append("%s route %s -> %s" % (route, "absent" if a is None else "judged",
                                                  "absent" if b is None else "judged"))
            continue
        if a.get("verdict") != b.get("verdict"):
            changes.append("%s.verdict %s -> %s" % (route, a.get("verdict"), b.get("verdict")))
        for key, tol in (("maxShapeBlobPx", REG_BLOB_MOVE_PX),
                         ("maxColorBlobPx", REG_COLOR_MOVE_PX)):
            x, y = int(a.get(key) or 0), int(b.get(key) or 0)
            if abs(y - x) > tol:
                changes.append("%s.%s %d -> %d" % (route, key, x, y))
        iou = _iou_change("%s.minIou" % route, a.get("minIou"), b.get("minIou"))
        if iou:
            changes.append(iou)
    fa, fb = list(base.get("stateFlags") or []), list(cur.get("stateFlags") or [])
    if set(fa) != set(fb):
        added = [f for f in fb if f not in fa]
        gone = [f for f in fa if f not in fb]
        changes.append("stateFlags %s" % " ".join(["+" + f for f in added] + ["-" + f for f in gone]))
    return changes


def pair_profiles(current: Sequence[str], baseline: Sequence[str]) -> Dict[str, str]:
    """current profile -> baseline profile: the same name first; then, when exactly one
    profile on each side is left unmatched, those two (a re-run whose label changed)."""
    pairs = {p: p for p in current if p in baseline}
    left_c = [p for p in current if p not in pairs]
    left_b = [p for p in baseline if p not in pairs.values()]
    if len(left_c) == 1 and len(left_b) == 1:
        pairs[left_c[0]] = left_b[0]
    return pairs


def apply_regression(rows: List[Dict], baseline_rows: Sequence[Dict],
                     baseline_dirs: Sequence[str]) -> Dict[str, Any]:
    """Mark every row ``moved`` / ``new`` / ``same`` against the baseline (in place) and
    return the summary, including the baseline cases no current sweep has (``gone``).
    Rows pair by profile and ``regKey``: the case id for a part case (a state case is
    then compared route by route, ``compare_rows``), the control set kind and id for a
    control. The first row of a key on each side is the one compared."""
    cur_profiles = list(dict.fromkeys(r["profile"] for r in rows))
    base_profiles = list(dict.fromkeys(r["profile"] for r in baseline_rows))
    pairs = pair_profiles(cur_profiles, base_profiles)
    base_index: Dict[Tuple[str, str], Dict] = {}
    for r in baseline_rows:
        base_index.setdefault((r["profile"], r["regKey"]), r)
    counts = {REG_MOVED: 0, REG_NEW: 0, REG_SAME: 0}
    matched = set()
    for r in rows:
        bp = pairs.get(r["profile"])
        key = (bp, r["regKey"])
        base = base_index.get(key) if bp is not None and key not in matched else None
        if base is None:
            r["regression"] = {"status": REG_NEW, "changes": [], "baseline": None}
            counts[REG_NEW] += 1
            continue
        matched.add(key)
        changes = compare_rows(r, base)
        status = REG_MOVED if changes else REG_SAME
        r["regression"] = {"status": status, "changes": changes,
                           "baseline": {"verdict": base.get("verdict"),
                                        "worstBlobPx": base.get("worstBlobPx"),
                                        "colorBlobPx": base.get("colorBlobPx"),
                                        "minIou": base.get("minIou"),
                                        "routes": {k: (v or {}).get("verdict") for k, v in
                                                   (base.get("routes") or {}).items()}
                                        if base.get("routes") else None}}
        counts[status] += 1
    gone = sorted("%s/%s" % k for k in base_index if k not in matched
                  and k[0] in pairs.values())
    return {"dirs": list(baseline_dirs), "pairs": pairs, "counts": counts,
            "gone": gone, "unpairedBaselineProfiles":
                [p for p in base_profiles if p not in pairs.values()]}


def apply_cross_links(rows: List[Dict]) -> None:
    """Link each case to the same case in every OTHER loaded profile (same ``regKey``:
    the case id, or for a control its set kind and id), else to that profile's cases of
    the same part name (another skin, or a case id that moved), so stock and ReStock
    read side by side. In place."""
    by_case: Dict[str, List[Dict]] = {}
    by_part: Dict[str, List[Dict]] = {}
    for r in rows:
        by_case.setdefault(r["regKey"], []).append(r)
        if r.get("part"):
            by_part.setdefault(str(r["part"]).lower(), []).append(r)
    for r in rows:
        links = []
        linked_profiles = set()
        for o in by_case.get(r["regKey"], []):
            if o["profile"] != r["profile"]:
                links.append({"id": o["id"], "profile": o["profile"], "skin": o["skin"],
                              "verdict": o["verdict"], "match": "same-case"})
                linked_profiles.add(o["profile"])
        if r["kind"] == KIND_PART and r.get("part"):
            for o in by_part.get(str(r["part"]).lower(), []):
                if o["profile"] != r["profile"] and o["profile"] not in linked_profiles \
                        and o["kind"] == KIND_PART:
                    links.append({"id": o["id"], "profile": o["profile"], "skin": o["skin"],
                                  "verdict": o["verdict"], "match": "same-part"})
        r["links"] = links


def apply_stowed_pairs(rows: List[Dict]) -> None:
    """Point each state case at its paired stowed case (``stowedCaseId``, same profile)
    when that case is loaded: the page's stowed column reads that case's REAL tile.
    Unpaired, the column falls back to the case's own ``ghost-stowed`` row. In place."""
    index = {(r["profile"], r["caseId"]): r for r in rows if r["kind"] == KIND_PART}
    for r in rows:
        sid = r.get("stowedCaseId")
        pair = index.get((r["profile"], sid)) if sid else None
        r["stowedPair"] = pair["id"] if pair is not None and pair is not r \
            and is_rest_case(pair) else None


def sweep_keys(sweeps: Sequence[Dict]) -> List[str]:
    """One key per loaded sweep, unique across the site: the manifest's label (else the
    directory name), sanitized, suffixed ``-<n>`` on a repeat."""
    keys: List[str] = []
    for s in sweeps:
        m = s.get("manifest") if isinstance(s.get("manifest"), dict) else {}
        label = m.get("label") if isinstance(m.get("label"), str) and m.get("label") else None
        base = sanitize_name(label or os.path.basename(os.path.normpath(s.get("dir") or "")))
        key, n = base, 2
        while key in keys:
            key, n = "%s-%d" % (base, n), n + 1
        keys.append(key)
    return keys


def build_site_data(sweeps: Sequence[Dict], baseline: Optional[Sequence[Dict]] = None,
                    generated_utc: Optional[str] = None) -> Dict[str, Any]:
    """The whole ``data.js`` payload. Each sweep is a dict with ``profile``,
    ``profileNote``, ``dir``, ``manifest``, ``manifestError``, ``sidecars`` (case id ->
    parsed / None / SIDECAR_UNREADABLE), ``imageUrl`` (callable or None) and optional
    ``inflight`` / ``isolate`` lines and ``verdicts`` (a parsed partparity-verdicts.json).
    Sweeps of one profile merge (shards, or a stowed sweep and its state sweeps); a part
    case id seen twice in one profile keeps the first and is listed in the later sweep's
    ``duplicates``. Controls are exempt: each sweep's controls keep their own ids and
    their own entry in ``controlSets``."""
    rows: List[Dict] = []
    headers: List[Dict] = []
    control_sets: List[Dict] = []
    seen = set()
    keys = sweep_keys(sweeps)
    for k, s in enumerate(sweeps):
        sweep = sweep_rows(s["profile"], s.get("manifest"), s.get("sidecars") or {},
                           s.get("imageUrl"), keys[k], k)
        kept, dups = [], []
        for r in sweep:
            if r["id"] in seen:
                dups.append(r["caseId"])
                continue
            seen.add(r["id"])
            kept.append(r)
        rows.extend(kept)
        header = sweep_header(s["profile"], s.get("profileNote"), s.get("dir") or "",
                              s.get("manifest"), s.get("manifestError"),
                              s.get("sidecars") or {}, kept, s.get("inflight") or (),
                              s.get("isolate") or (), s.get("verdicts"), dups, keys[k])
        headers.append(header)
        ctl = [r["id"] for r in kept if r["kind"] == KIND_CONTROL]
        if ctl:
            control_sets.append({"profile": s["profile"], "sweep": keys[k],
                                 "kind": header["controlSet"], "ids": ctl})
    regression = None
    if baseline is not None:
        base_rows: List[Dict] = []
        base_keys = sweep_keys(baseline)
        for k, b in enumerate(baseline):
            base_rows.extend(sweep_rows(b["profile"], b.get("manifest"),
                                        b.get("sidecars") or {}, None, base_keys[k], k))
        regression = apply_regression(rows, base_rows, [b.get("dir") or "" for b in baseline])
    apply_cross_links(rows)
    apply_stowed_pairs(rows)
    groups = group_parts(rows)
    return {
        "schema": DATA_SCHEMA,
        "generatedUtc": generated_utc,
        "thresholds": pp.thresholds(),
        "regressionTolerances": {"blobPx": REG_BLOB_MOVE_PX, "colorPx": REG_COLOR_MOVE_PX,
                                 "iou": REG_IOU_MOVE},
        "sweeps": headers,
        "profiles": list(dict.fromkeys(h["profile"] for h in headers)),
        "regression": regression,
        "counts": page_counts(rows, groups),
        "facets": facets(rows, groups),
        "controls": [r["id"] for r in rows if r["kind"] == KIND_CONTROL],
        "controlSets": control_sets,
        "groups": groups,
        "cases": rows,
    }


def render_data_js(data: Dict[str, Any]) -> str:
    """One JS statement; ASCII (``\\u`` escapes), and ``</`` broken so the literal is
    safe even if inlined into a ``<script>`` element."""
    text = json.dumps(data, ensure_ascii=True, separators=(",", ":"), allow_nan=False,
                      default=str)
    return DATA_JS_PREFIX + text.replace("</", "<\\/") + ";\n"


def parse_data_js(text: str) -> Dict[str, Any]:
    """The inverse of ``render_data_js`` (tests and tooling)."""
    if not text.startswith(DATA_JS_PREFIX):
        raise ValueError("not a parity data.js")
    body = text[len(DATA_JS_PREFIX):].rstrip()
    if body.endswith(";"):
        body = body[:-1]
    return json.loads(body)


def relative_url(from_dir: str, target: str) -> Optional[str]:
    """``target`` relative to ``from_dir`` as a percent-quoted POSIX URL, or None when no
    relative path exists (another drive on Windows)."""
    try:
        rel = os.path.relpath(target, from_dir)
    except ValueError:
        return None
    parts = rel.replace("\\", "/").split("/")
    return "/".join(urllib.parse.quote(p) for p in parts)


def inside(path: str, root: str) -> bool:
    p, r = os.path.normcase(os.path.abspath(path)), os.path.normcase(os.path.abspath(root))
    try:
        return os.path.commonpath([p, r]) == r
    except ValueError:
        return False


def find_repo_root(start: str) -> Optional[str]:
    cur = os.path.abspath(start)
    while True:
        if os.path.exists(os.path.join(cur, ".git")):
            return cur
        parent = os.path.dirname(cur)
        if parent == cur:
            return None
        cur = parent


def out_dir_refusal(out_dir: str, repo_root: Optional[str]) -> Optional[str]:
    """Why ``out_dir`` may not be written, or None. The repository tree is off limits
    (house rule), except the gitignored ``harness/results/`` the harvest writes to."""
    if repo_root is None or not inside(out_dir, repo_root):
        return None
    if inside(out_dir, os.path.join(repo_root, "harness", "results")):
        return None
    return "refusing to write into the repository tree (%s); pass --out outside it" % out_dir


def split_named_dir(arg: str, isdir: Callable[[str], bool] = os.path.isdir) -> Tuple[Optional[str], str]:
    """``NAME=<dir>`` -> (NAME, dir); a plain existing directory is never split."""
    if isdir(arg):
        return None, arg
    m = _NAMED_DIR_RE.match(arg)
    if m:
        return m.group(1), m.group(2)
    return None, arg


# ---------------------------------------------------------------------------
# I/O shell.
# ---------------------------------------------------------------------------

def _read_json(path: str) -> Tuple[Optional[Any], Optional[str]]:
    try:
        with open(path, "r", encoding="utf-8-sig") as fh:
            return json.load(fh), None
    except FileNotFoundError:
        return None, "absent"
    except (OSError, ValueError) as exc:
        return None, "unreadable: %s" % exc


def _read_lines(path: str) -> List[str]:
    try:
        with open(path, "r", encoding="utf-8-sig") as fh:
            return fh.read().splitlines()
    except OSError:
        return []


def load_sweep(directory: str, explicit_name: Optional[str] = None) -> Dict[str, Any]:
    """Read one sweep directory: the manifest, every sidecar, the state files and an
    optional ``partparity-verdicts.json``. Never raises on bad content."""
    manifest, manifest_error = _read_json(os.path.join(directory, pp.MANIFEST_FILE))
    if manifest is not None and not isinstance(manifest, dict):
        manifest, manifest_error = None, "not a JSON object"
    names = sorted(os.listdir(directory))
    sidecars: Dict[str, Any] = {}
    for name in names:
        if not name.endswith(pp.SIDECAR_SUFFIX):
            continue
        case_id = name[:-len(pp.SIDECAR_SUFFIX)]
        doc, err = _read_json(os.path.join(directory, name))
        sidecars[case_id] = doc if isinstance(doc, dict) else SIDECAR_UNREADABLE
    verdicts, _ = _read_json(os.path.join(directory, pp.VERDICTS_FILE))
    dir_name = os.path.basename(os.path.normpath(directory))
    profile, note = profile_name(manifest, dir_name, explicit_name)
    return {
        "profile": profile, "profileNote": note, "dir": os.path.abspath(directory),
        "manifest": manifest, "manifestError": manifest_error, "sidecars": sidecars,
        "files": set(names), "inflight": _read_lines(os.path.join(directory, pp.INFLIGHT_FILE)),
        "isolate": _read_lines(os.path.join(directory, pp.ISOLATE_FILE)),
        "verdicts": verdicts if isinstance(verdicts, dict) else None,
    }


def plan_images(sweeps: List[Dict], out_dir: str, mode: str) -> List[Tuple[str, str]]:
    """Give every sweep an ``imageUrl`` callable; return the (src, dst) copies owed.
    ``in-place``: relative to the sweep dir (copy only when no relative path exists);
    ``copy``: under ``<out>/img/<k>/``."""
    copies: List[Tuple[str, str]] = []
    for k, s in enumerate(sweeps):
        files = s["files"]
        rel_base = relative_url(out_dir, s["dir"]) if mode == "in-place" else None
        if rel_base is not None:
            def url(name, _files=files, _base=rel_base):
                if name not in _files:
                    return None
                return (_base + "/" if _base not in ("", ".") else "") + urllib.parse.quote(name)
        else:
            sub = "img/%d" % k

            def url(name, _files=files, _dir=s["dir"], _sub=sub):
                if name not in _files:
                    return None
                copies.append((os.path.join(_dir, name), os.path.join(out_dir, *_sub.split("/"), name)))
                return _sub + "/" + urllib.parse.quote(name)
        s["imageUrl"] = url
        s["imageMode"] = "in-place" if rel_base is not None else "copy"
    return copies


def copy_images(copies: Sequence[Tuple[str, str]]) -> Tuple[int, int]:
    """Copy each image unless an up-to-date copy is there; returns (copied, kept)."""
    copied = kept = 0
    for src, dst in dict.fromkeys(copies):
        try:
            st = os.stat(src)
        except OSError:
            continue
        try:
            dt = os.stat(dst)
            if dt.st_size == st.st_size and dt.st_mtime >= st.st_mtime:
                kept += 1
                continue
        except OSError:
            pass
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
        copied += 1
    return copied, kept


def _write_text(path: str, text: str) -> None:
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="ascii", newline="\n") as fh:
        fh.write(text)
    os.replace(tmp, path)


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(
        description="Static offline viewer over part ghost parity sweep directories "
                    "(docs/dev/design-part-ghost-parity.md section 8).")
    ap.add_argument("dirs", nargs="+", metavar="DIR",
                    help="sweep directory (manifest.json + per-case files); NAME=DIR names "
                         "its profile")
    ap.add_argument("--baseline", action="append", default=[], metavar="DIR",
                    help="an earlier sweep directory to compare with (regression mode); "
                         "repeat for several profiles")
    ap.add_argument("--out", metavar="DIR",
                    help="output directory (default: <first dir>/viewer/)")
    ap.add_argument("--images", choices=("auto", "copy", "in-place"), default="auto",
                    help="reference images in place or copy them under <out>/img/ "
                         "(auto: in place for the default --out, copy otherwise)")
    ap.add_argument("--gif", action="store_true",
                    help="reserved: GIF / APNG animation strips (design 9.2, phase P7)")
    args = ap.parse_args(argv)

    if args.gif:
        print("part_parity_view: --gif is not built yet: animation strips are phase P7 "
              "(design 9.2) and no sweep carries a frame axis", file=sys.stderr)
        return 2

    sweeps = []
    for arg in args.dirs:
        name, directory = split_named_dir(arg)
        if not os.path.isdir(directory):
            print("part_parity_view: not a directory: %s" % directory, file=sys.stderr)
            return 2
        sweeps.append(load_sweep(directory, name))
    baseline = []
    for arg in args.baseline:
        name, directory = split_named_dir(arg)
        if not os.path.isdir(directory):
            print("part_parity_view: baseline is not a directory: %s" % directory,
                  file=sys.stderr)
            return 2
        baseline.append(load_sweep(directory, name))

    out_dir = os.path.abspath(args.out) if args.out else os.path.join(sweeps[0]["dir"],
                                                                       DEFAULT_OUT_LEAF)
    refusal = out_dir_refusal(out_dir, find_repo_root(_HERE))
    if refusal:
        print("part_parity_view: %s" % refusal, file=sys.stderr)
        return 2
    mode = args.images
    if mode == "auto":
        mode = "copy" if args.out else "in-place"
    os.makedirs(out_dir, exist_ok=True)
    copies = plan_images(sweeps, out_dir, mode)

    import datetime
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    data = build_site_data(sweeps, baseline if args.baseline else None, stamp)
    copied, kept = copy_images(copies)
    data_js = render_data_js(data)
    _write_text(os.path.join(out_dir, DATA_JS_FILE), data_js)
    _write_text(os.path.join(out_dir, INDEX_FILE), INDEX_HTML)
    for s in sweeps:
        if s.get("profileNote"):
            print("part_parity_view: %s: %s" % (s["dir"], s["profileNote"]))
        if s.get("imageMode") == "copy" and mode == "in-place":
            print("part_parity_view: %s: no relative path from the output, images copied"
                  % s["dir"])
    print("part_parity_view: wrote %s (%d case(s), %d profile(s), data.js %d KB, images "
          "copied=%d up-to-date=%d)" % (os.path.join(out_dir, INDEX_FILE), len(data["cases"]),
                                         len(data["profiles"]), len(data_js) // 1024,
                                         copied, kept))
    return 0


# ---------------------------------------------------------------------------
# The page. Static: everything it shows comes from data.js. Every dynamic string is
# set through textContent / attributes, never innerHTML.
# ---------------------------------------------------------------------------

INDEX_HTML = r"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light dark">
<title>Part ghost parity</title>
<style>
:root {
  --bg: #f4f5f7; --fg: #1b1f24; --muted: #5a636e; --card: #ffffff; --border: #d3d8df; --code: #eef1f4;
  --focus: #1b5fb4;
  /* identity: which side a picture is. Deliberately not a verdict colour. */
  --real: #1d5fd6; --real-fg: #ffffff; --ghost: #a1239f; --ghost-fg: #ffffff; --diff: #3a3f46; --diff-fg: #ffffff;
  /* verdicts */
  --pass: #2f9a4f; --warn: #d39200; --fail: #d22f2f; --error: #8b9099;
  --pass-soft: #e2f4e7; --warn-soft: #fcf1d2; --fail-soft: #fbe1e1; --error-soft: #eceef1;
  --warn-text: #8a5d00; --tilebg: #8f959c; --t: min(200px, calc((100vw - 132px) / 3));
  --ts: min(170px, calc((100vw - 150px) / 5));
}
@media (prefers-color-scheme: dark) {
  :root {
    --bg: #121519; --fg: #e3e6ea; --muted: #9aa3ae; --card: #1a1f25; --border: #323a44; --code: #222930;
    --focus: #8fbdf5;
    --real: #4f8ff7; --real-fg: #08152b; --ghost: #d257cf; --ghost-fg: #23061f; --diff: #5b626b; --diff-fg: #ffffff;
    --pass: #49c06b; --warn: #f0b429; --fail: #f05252; --error: #8e949d;
    --warn-text: #f0b429; --pass-soft: #163521; --warn-soft: #3d300c; --fail-soft: #431919; --error-soft: #262b31;
  }
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--fg);
  font: 14px/1.4 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
a { color: var(--focus); }
:focus-visible { outline: 3px solid var(--focus); outline-offset: 2px; }
.muted, .note { color: var(--muted); }
.note { font-size: 12px; }
h1 { font-size: 18px; margin: 0; }
h2 { font-size: 16px; margin: 0; }
/* sticky header: counters + filters */
#top { position: sticky; top: 0; z-index: 10; background: var(--bg); border-bottom: 1px solid var(--border);
  padding: 8px 16px; display: flex; flex-wrap: wrap; gap: 6px 14px; align-items: center; }
.counters { display: flex; flex-wrap: wrap; gap: 4px; }
#top label { display: inline-flex; gap: 4px; align-items: center; font-size: 13px; color: var(--muted); }
select, input, button { font: inherit; color: var(--fg); background: var(--card); border: 1px solid var(--border);
  border-radius: 6px; padding: 3px 6px; max-width: 200px; }
input[type=search] { width: 200px; }
input[type=checkbox] { width: auto; }
.shown { color: var(--muted); font-size: 12px; margin-left: auto; }
main { padding: 8px 16px 40px; }
/* verdict chips */
.vc { display: inline-block; font-size: 11px; font-weight: 700; letter-spacing: .03em; line-height: 18px;
  padding: 0 7px; border-radius: 9px; color: #fff; text-transform: uppercase; white-space: nowrap; }
.vc.pass { background: var(--pass); } .vc.warn { background: var(--warn); color: #1d1600; }
.vc.fail { background: var(--fail); } .vc.error { background: var(--error); color: #111; }
.chip { display: inline-block; font-size: 11px; line-height: 18px; padding: 0 7px; border-radius: 9px;
  background: var(--code); border: 1px solid var(--border); white-space: nowrap; }
.chip.moved { border-color: var(--warn); }
/* the legend and the identity captions */
.legend { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin: 4px 0 10px; font-size: 13px; }
.idtag { display: inline-block; padding: 1px 8px; border-radius: 4px; font-weight: 700; font-size: 12px; }
.idtag.real, figcaption.real { background: var(--real); color: var(--real-fg); }
.idtag.ghost, figcaption.ghost { background: var(--ghost); color: var(--ghost-fg); }
.idtag.diff, figcaption.diff { background: var(--diff); color: var(--diff-fg); }
/* sweeps and controls */
details.box { background: var(--card); border: 1px solid var(--border); border-radius: 8px; margin: 0 0 10px; padding: 6px 10px; }
details.box > summary { cursor: pointer; font-weight: 600; }
details.box.bad { border: 3px solid var(--fail); }
details.box.bad > summary { color: var(--fail); }
.kv { display: grid; grid-template-columns: max-content 1fr; gap: 1px 10px; font-size: 12px; margin: 6px 0; }
.kv > div { overflow-wrap: anywhere; }
.kv > div:nth-child(odd) { color: var(--muted); }
.bad-text { color: var(--fail); font-weight: 600; }
/* part groups and skin subgroups */
.group { background: var(--card); border: 4px solid var(--pass); border-radius: 10px; margin: 0 0 14px; padding: 8px 10px;
  content-visibility: auto; contain-intrinsic-size: auto 700px; }
.group.warn { border-color: var(--warn); } .group.fail { border-color: var(--fail); } .group.error { border-color: var(--error); }
.ghead { display: flex; flex-wrap: wrap; gap: 4px 10px; align-items: baseline; }
.ghead .name, .ghead .src { color: var(--muted); font-size: 13px; }
.trigger { flex-basis: 100%; font-size: 13px; font-weight: 600; }
.tr-fail { color: var(--fail); font-weight: 600; } .tr-warn { color: var(--warn-text); font-weight: 600; }
.tr-error { color: var(--muted); font-weight: 600; }
.skin { border: 2px solid var(--pass); border-radius: 8px; margin: 8px 0 0; padding: 6px 8px; }
.skin.warn { border-color: var(--warn); } .skin.fail { border-color: var(--fail); } .skin.error { border-color: var(--error); }
.skin.ctl-ok { border-color: var(--pass); } .skin.ctl-bad { border-color: var(--fail); }
/* state sub-blocks inside a skin (design D.7) */
.state { border: 2px solid var(--pass); border-radius: 8px; margin: 6px 0 0 10px; padding: 4px 8px; }
.state.warn { border-color: var(--warn); } .state.fail { border-color: var(--fail); } .state.error { border-color: var(--error); }
.state.ctl-ok { border-color: var(--pass); } .state.ctl-bad { border-color: var(--fail); }
figure.st { width: var(--ts); } figure.st .tile { width: var(--ts); height: var(--ts); }
figure.ref .tile { outline: 2px dashed var(--fg); outline-offset: -5px; }
figcaption.ref { font-style: italic; }
.shead { display: flex; flex-wrap: wrap; gap: 4px 8px; align-items: baseline; font-size: 13px; }
.shead .skname { font-weight: 600; }
.shead .trig { flex-basis: 100%; font-size: 12px; }
.vrow { display: flex; flex-wrap: wrap; gap: 6px 8px; align-items: flex-start; padding: 6px 0 2px; }
.vname { width: 64px; font-size: 12px; font-weight: 600; padding-top: 18px; }
figure { margin: 0; width: var(--t); }
figcaption { font-size: 11px; font-weight: 700; padding: 1px 4px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
  border-radius: 4px 4px 0 0; }
figcaption .short { display: none; }
.tile { position: relative; width: var(--t); height: var(--t); background: var(--tilebg); overflow: hidden; cursor: zoom-in; border: 0; padding: 0; display: block; }
figure.real .tile { box-shadow: inset 0 0 0 2px var(--real); }
figure.ghost .tile { box-shadow: inset 0 0 0 2px var(--ghost); }
.tile .layer { position: absolute; inset: 0; background-repeat: no-repeat; }
.tile.diff { background: #000; isolation: isolate; }
.tile.diff .layer.blend { mix-blend-mode: difference; }
body.nodiff figure.diff { display: none; }
.vmeta { font-size: 12px; max-width: 220px; padding-top: 18px; }
.vmeta .vc { margin-bottom: 2px; }
.nopic { width: var(--t); height: var(--t); display: flex; align-items: center; justify-content: center; text-align: center;
  font-size: 12px; color: var(--muted); border: 1px dashed var(--border); }
details.more { margin-top: 6px; font-size: 12px; }
details.more > summary { cursor: pointer; color: var(--muted); }
table { border-collapse: collapse; font-size: 12px; margin: 4px 0 8px; }
th, td { border: 1px solid var(--border); padding: 2px 6px; text-align: left; vertical-align: top; overflow-wrap: anywhere; }
th { background: var(--code); }
td.num { text-align: right; font-variant-numeric: tabular-nums; }
td.hit { background: var(--fail-soft); font-weight: 700; }
.tablewrap { overflow-x: auto; max-width: 100%; }
pre { background: var(--code); padding: 6px; border-radius: 6px; font-size: 11px; white-space: pre-wrap; overflow-wrap: anywhere; }
/* the 1:1 dialog */
dialog { border: 1px solid var(--border); border-radius: 10px; background: var(--card); color: var(--fg); max-width: 96vw; max-height: 94vh; padding: 10px; }
dialog::backdrop { background: rgba(0, 0, 0, .6); }
dialog .pair { display: flex; flex-wrap: wrap; gap: 8px; }
dialog figure { width: auto; }
dialog .tile { cursor: default; }
@media (max-width: 640px) {
  :root { --t: calc((100vw - 52px) / 2); --ts: calc((100vw - 52px) / 2); }
  body:not(.nodiff) figure.diff { display: none; }
  figcaption .full { display: none; } figcaption .short { display: inline; }
  .vname { width: 100%; padding-top: 0; } .vmeta { padding-top: 0; max-width: none; }
  input[type=search] { width: 140px; }
}
</style>
</head>
<body>
<div id="top" role="search"></div>
<main>
  <h1>Part ghost parity</h1>
  <div class="legend">
    <span class="idtag real">REAL part (stock KSP)</span>
    <span class="idtag ghost">GHOST (Parsek reconstruction)</span>
    <span class="idtag diff">DIFFERENCE (black = identical)</span>
    <span class="note">Verdicts are automatic (harness/lib/partparity.py); the border colour of a part and of each skin is its verdict. Click a picture for 1:1. j / k: next / previous part.</span>
  </div>
  <p class="note" id="statelegend" hidden>State cases (deployed, open, lit, semi, full, jettisoned): each view shows STOWED (the paired stowed case's REAL part when its sweep is loaded, else the case's own stowed ghost as a dashed reference, not judged), REAL in the state, GHOST event route (the stowed snapshot plus the recorded part events, as a flight replays it), GHOST start route (a recording that starts in the state) and the DIFFERENCE of real vs the event ghost. Both ghost routes are judged; the case takes the worse one.</p>
  <p id="nodata" hidden>data.js did not load: open this page from the folder the viewer wrote, next to its data.js.</p>
  <details class="box" id="sweeps"><summary id="sweepsum">Sweeps</summary><div id="sweepbody"></div></details>
  <details class="box" id="controls"><summary id="ctlsum">Controls</summary><div id="ctlbody"></div></details>
  <div id="groups"></div>
</main>
<dialog id="zoom" aria-label="One view at 1:1"><form method="dialog"><button>Close (Esc)</button></form><div id="zoombody"></div></dialog>
<script src="data.js"></script>
<script>
(function () {
  "use strict";
  var D = window.PARITY_DATA;
  if (!D) { document.getElementById("nodata").hidden = false; return; }
  var CASE = {}, GROUP_EL = {}, CASE_GROUP = {};
  D.cases.forEach(function (c) { CASE[c.id] = c; });
  D.groups.forEach(function (g, i) {
    g._i = i;
    g.cases.forEach(function (id) { CASE_GROUP[id] = g; });
    g._text = [g.title, g.part, g.source, g.profile].concat(g.cases.map(function (id) { var c = CASE[id]; return [c.caseId, c.skin, c.stateLabel, c.family || ""].concat(c.tags || []).concat(c.stateFlags || []).join(" "); })).join(" ").toLowerCase();
  });
  var SEV = {fail: 3, warn: 2, error: 1, pass: 0};
  var state = {verdict: "", source: "", q: "", sort: "severity", regression: "", stateWord: "", family: "", stateFlag: "", diff: true};
  var FILTER_KEYS = ["verdict", "source", "regression", "stateWord", "family", "stateFlag"];
  try { var saved = JSON.parse(localStorage.getItem("parity-view-2") || "null"); if (saved) Object.keys(state).forEach(function (k) { if (k in saved) state[k] = saved[k]; }); } catch (e) {}
  function save() { try { localStorage.setItem("parity-view-2", JSON.stringify(state)); } catch (e) {} }

  function el(tag, attrs, kids) {
    var n = document.createElement(tag);
    if (attrs) Object.keys(attrs).forEach(function (k) {
      var v = attrs[k];
      if (v === null || v === undefined || v === false) return;
      if (k === "text") n.textContent = v;
      else if (k === "cls") n.className = v;
      else if (k === "on") Object.keys(v).forEach(function (ev) { n.addEventListener(ev, v[ev]); });
      else n.setAttribute(k, v === true ? "" : v);
    });
    (kids || []).forEach(function (c) { if (c !== null && c !== undefined) n.appendChild(typeof c === "string" ? document.createTextNode(c) : c); });
    return n;
  }
  function fmt(v, d) { if (v === null || v === undefined) return "-"; if (typeof v === "number") return d === undefined ? (Number.isInteger(v) ? String(v) : v.toFixed(4)) : v.toFixed(d); return String(v); }
  function vchip(v, title) { return el("span", {cls: "vc " + v, text: v, title: title || null}); }

  // ---- header: counters + filters ----
  function buildTop() {
    var top = document.getElementById("top"), cc = D.counts.cases;
    var counters = el("div", {cls: "counters", title: "part cases (one per skin) by verdict"});
    ["fail", "warn", "error", "pass"].forEach(function (v) { counters.appendChild(el("span", {cls: "vc " + v, text: v + " " + cc[v]})); });
    var ctl = D.counts.controls;
    if (ctl.ok + ctl.bad) counters.appendChild(el("span", {cls: "chip", text: "controls " + ctl.ok + "/" + (ctl.ok + ctl.bad) + " ok", style: ctl.bad ? "border-color: var(--fail); color: var(--fail); font-weight: 700" : null}));
    top.appendChild(counters);
    function select(key, label, values, extra) {
      var s = el("select", {"aria-label": label, on: {change: function () { state[key] = s.value; save(); applyFilters(); }}},
        [el("option", {value: "", text: "all"})].concat(extra || []).concat(values.map(function (v) { return el("option", {value: v[0], text: v[0] + " (" + v[1] + ")"}); })));
      s.value = state[key] || ""; if (s.value !== (state[key] || "")) state[key] = "";
      top.appendChild(el("label", null, [label, s]));
    }
    select("verdict", "Verdict", D.facets.verdict, [el("option", {value: "flagged", text: "flagged (fail, warn, error)"})]);
    select("source", "Source", D.facets.source);
    if (D.facets.state.length) select("stateWord", "State", D.facets.state);
    if (D.facets.family.length) select("family", "Family", D.facets.family);
    if (D.facets.stateFlag.length) select("stateFlag", "State flags", D.facets.stateFlag);
    if (D.regression) select("regression", "Baseline", D.facets.regression);
    var timer = null;
    var q = el("input", {type: "search", placeholder: "search  ( / )", value: state.q, "aria-label": "Search parts", id: "q",
      on: {input: function () { clearTimeout(timer); timer = setTimeout(function () { state.q = q.value; save(); applyFilters(); }, 150); }}});
    top.appendChild(q);
    var sort = el("select", {"aria-label": "Sort", on: {change: function () { state.sort = sort.value; save(); order(); applyFilters(); }}},
      [el("option", {value: "severity", text: "flagged first"}), el("option", {value: "name", text: "name"})]);
    sort.value = state.sort;
    top.appendChild(el("label", null, ["Sort", sort]));
    var diff = el("input", {type: "checkbox", checked: state.diff, on: {change: function () { state.diff = diff.checked; save(); document.body.classList.toggle("nodiff", !state.diff); }}});
    top.appendChild(el("label", null, [diff, "difference tile"]));
    top.appendChild(el("span", {cls: "shown", id: "shown", "aria-live": "polite"}));
    document.body.classList.toggle("nodiff", !state.diff);
  }

  // ---- sweeps (collapsed) ----
  function buildSweeps() {
    var body = document.getElementById("sweepbody"), bad = false, parts = [], perProfile = {};
    D.sweeps.forEach(function (h) { perProfile[h.profile] = (perProfile[h.profile] || 0) + 1; });
    D.sweeps.forEach(function (h) {
      var name = h.profile + (perProfile[h.profile] > 1 ? " / " + h.sweep : "");
      var kv = el("div", {cls: "kv"});
      function row(k, v, cls, title) { kv.appendChild(el("div", {text: k})); kv.appendChild(el("div", {cls: cls || null, title: title || null, text: v})); }
      var compOk = h.completeness ? h.completeness.ok : false;
      var hostDamaged = h.progress && h.progress.hostDamaged === true;
      var pressBad = h.statePress && !h.statePress.ok;
      if (!compOk || h.manifestError || hostDamaged || pressBad || h.instrumentProblems.length || h.duplicates.length) bad = true;
      row("profile", h.profile + (h.profileNote ? " (" + h.profileNote + ")" : "") + (perProfile[h.profile] > 1 ? " - merged with " + (perProfile[h.profile] - 1) + " other sweep(s) of this profile" : ""));
      row("label / dir", fmt(h.label) + " - " + h.dir);
      if (h.manifestError) row("manifest", h.manifestError, "bad-text");
      row("DLL", h.dllHash ? h.dllHash.slice(0, 16) + "..." : "-", null, h.dllHash);
      row("mods", h.gameDataMods.join(", ") || "-");
      row("tile / views", fmt(h.tile) + " px, " + h.views.join(", "));
      row("settings", Object.keys(h.settings).map(function (k) { return k + "=" + h.settings[k]; }).join("  "));
      if (hostDamaged) row("host", "DAMAGED (" + fmt(h.progress.hostDamage) + ")" + (h.progress.hostDamageDetail ? ": " + h.progress.hostDamageDetail : "") + " - the sweep stopped; the cases after it were not run", "bad-text");
      if (h.completeness) row("completeness", (compOk ? "complete" : "INCOMPLETE") + ": " + h.completeness.complete + "/" + h.completeness.planned + " sidecars, setup faults " + h.completeness.setupFaults + ", crashed " + h.crashed.length + (h.completeness.problems.length ? " - " + h.completeness.problems.join("; ") : ""), compOk ? null : "bad-text");
      row("exclusions", h.exclusions.map(function (e) { return e.part + " (" + e.reason + ")"; }).join(", ") || "none");
      if (h.controls) row("control set", (h.controlSet || "-") + ", " + h.controls.expected + " control(s)" + (h.controls.ok ? ", ok" : " - " + h.controls.problems.join("; ")), h.controls.ok ? null : "bad-text");
      if (h.statePress) row("state press", (h.statePress.ok ? "ok" : "BAD") + ": " + Object.keys(h.statePress.families).map(function (f) { var x = h.statePress.families[f]; return f + " " + x.pressFailed + "/" + x.cases + " failed"; }).join(", ") + (h.statePress.problems.length ? " - " + h.statePress.problems.join("; ") : ""), h.statePress.ok ? null : "bad-text");
      if (h.stateCases) row("state flags", h.stateCases + " state case(s); " + (Object.keys(h.stateFlagCounts || {}).map(function (k) { return k + " " + h.stateFlagCounts[k]; }).join(", ") || "no state flag"));
      if (h.instrumentProblems.length) row("writer checks", h.instrumentProblems.slice(0, 5).join("; ") + (h.instrumentProblems.length > 5 ? " (+" + (h.instrumentProblems.length - 5) + " more)" : ""), "bad-text");
      if (h.verdictsFile) row("verifier row", fmt(h.verdictsFile.status) + (h.verdictsFile.invalidSubkind ? " (" + h.verdictsFile.invalidSubkind + ")" : "") + ", recompute " + h.verdictsFile.recomputed + " case(s), " + h.verdictsFile.recomputeMismatches + " mismatch(es)", h.verdictsFile.recomputeMismatches ? "bad-text" : null);
      if (h.duplicates.length) row("duplicates", h.duplicates.length + " case id(s) already loaded from another sweep: " + h.duplicates.slice(0, 5).join(", "), "bad-text");
      body.appendChild(el("div", null, [el("h2", {text: name}), kv]));
      parts.push(name + " (" + (compOk ? "complete" : "INCOMPLETE") + ", " + h.cases + " cases" + (h.duplicates.length ? ", " + h.duplicates.length + " DUPLICATE id(s)" : "") + ")");
    });
    if (D.regression) body.appendChild(el("p", {cls: "note", text: "Baseline " + D.regression.dirs.join(", ") + ": moved " + D.regression.counts.moved + ", new " + D.regression.counts["new"] + ", same " + D.regression.counts.same + ", gone " + D.regression.gone.length + (D.regression.gone.length ? " (" + D.regression.gone.slice(0, 20).join(", ") + ")" : "")}));
    body.appendChild(el("p", {cls: "note", text: "Views are fixed in the part's frame: side-a from -Z, side-b from +X, top from +Y, bottom from -Y; the +Z and -X faces are only seen with views=all, and a rotationally symmetric part cannot prove its yaw. Verdict lines: shape blob > " + D.thresholds.shapeBlobFailPx + " px or " + D.thresholds.shapeBlobFailPct + "% of the box, colour blob > " + D.thresholds.colorBlobFailPx + " px, warn at " + D.thresholds.warnFraction + " of those; a rendered mesh missing or extra on the ghost, or a skin mismatch, fails the case. Generated " + (D.generatedUtc || "?") + "."}));
    document.getElementById("sweepsum").textContent = "Sweeps: " + parts.join("; ");
    var box = document.getElementById("sweeps");
    if (bad) { box.classList.add("bad"); box.open = true; }
  }

  // ---- tiles: CSS crops of the atlas, image set only near the viewport ----
  function cropStyle(c, row, col) {
    var cols = Math.max(1, c.atlasCols.length), rows = Math.max(1, c.atlasRows.length);
    return "background-size:" + (cols * 100) + "% " + (rows * 100) + "%;background-position:" +
      (cols > 1 ? col / (cols - 1) * 100 : 0) + "% " + (rows > 1 ? row / (rows - 1) * 100 : 0) + "%";
  }
  function layerEl(c, row, col, cls) { return el("div", {cls: "layer" + (cls ? " " + cls : ""), style: cropStyle(c, row, col), "data-src": c.atlas}); }
  function rowsOf(c) { var r = c.atlasRows.indexOf("real"), g = c.atlasRows.indexOf("ghost"); return [r < 0 ? 0 : r, g < 0 ? 1 : g]; }
  function figure(kind, tileEl, full, short) {
    return el("figure", {cls: kind}, [el("figcaption", {cls: kind, title: full}, [el("span", {cls: "full", text: full}), el("span", {cls: "short", text: short})]), tileEl]);
  }
  function pairFigures(c, v, s) {
    var col = c.atlasCols.indexOf(v.view), rr = rowsOf(c);
    if (!c.atlas || col < 0) return [el("div", {cls: "nopic", text: c.atlas ? "view not in the atlas" : "no atlas image"})];
    function t(kids, label) { var n = el("div", {cls: "tile", role: "img", "aria-label": label, "data-case": c.id, "data-view": v.view}, kids); if (s) { n.style.width = s + "px"; n.style.height = s + "px"; } return n; }
    var real = t([layerEl(c, rr[0], col)], "real " + v.view);
    var ghost = t([layerEl(c, rr[1], col)], "ghost " + v.view);
    var diff = t([layerEl(c, rr[0], col), layerEl(c, rr[1], col, "blend")], "difference " + v.view); diff.classList.add("diff");
    return [figure("real", real, "REAL part (stock KSP)", "REAL"),
            figure("ghost", ghost, "GHOST (Parsek reconstruction)", "GHOST"),
            figure("diff", diff, "DIFFERENCE (black = same)", "DIFF")];
  }
  function viewMeta(v) {
    var blob = Math.max(v.realOnlyBlobPx || 0, v.ghostOnlyBlobPx || 0);
    return el("div", {cls: "vmeta"}, [vchip(v.verdict, v.reasons.join("\n") || null), el("div", {text: "shape blob " + blob + " px, colour blob " + fmt(v.colorBlobPx) + " px, IoU " + fmt(v.iou)}),
      v.trigger ? el("div", {cls: "tr-" + v.verdict, text: v.trigger}) : null]);
  }
  // A state case's view row (design D.7): STOWED | REAL | GHOST event | GHOST start | DIFF.
  // ``all`` (the 1:1 dialog) adds the start-route difference and, when the stowed column
  // is the paired case's real part, the case's own stowed ghost reference.
  function rowIdx(c, name, dflt) { var i = c.atlasRows.indexOf(name); return i < 0 ? dflt : i; }
  function mkTile(owner, layers, label, view, s) {
    var n = el("div", {cls: "tile", role: "img", "aria-label": label, "data-case": owner.id, "data-view": view}, layers);
    if (s) { n.style.width = s + "px"; n.style.height = s + "px"; }
    return n;
  }
  function stateFigures(c, v, s, all) {
    var col = c.atlasCols.indexOf(v.view);
    if (!c.atlas || col < 0) return [el("div", {cls: "nopic", text: c.atlas ? "view not in the atlas" : "no atlas image"})];
    var R = rowIdx(c, "real", 0), G = rowIdx(c, "ghost", 1), GS = rowIdx(c, "ghost-start", 2), GR = rowIdx(c, "ghost-stowed", 3);
    var pair = c.stowedPair ? CASE[c.stowedPair] : null, pcol = pair && pair.atlas ? pair.atlasCols.indexOf(v.view) : -1;
    function one(owner, row, colIx, label) { return mkTile(c, [layerEl(owner, row, colIx)], label, v.view, s); }
    var ref = figure("ghost st ref", one(c, GR, col, "ghost stowed reference " + v.view), "GHOST stowed (reference, not judged)", "STOWED ref");
    var out = [];
    if (pair && pcol >= 0) out.push(figure("real st", one(pair, rowIdx(pair, "real", 0), pcol, "real stowed " + v.view), "REAL stowed (" + pair.caseId + ")", "STOWED"));
    else out.push(ref);
    out.push(figure("real st", one(c, R, col, "real " + c.stateLabel + " " + v.view), "REAL " + c.stateLabel, "REAL"));
    out.push(figure("ghost st", one(c, G, col, "ghost event route " + v.view), "GHOST event route", "GHOST ev"));
    out.push(figure("ghost st", one(c, GS, col, "ghost start route " + v.view), "GHOST start route", "GHOST st"));
    var d = mkTile(c, [layerEl(c, R, col), layerEl(c, G, col, "blend")], "difference real vs event ghost " + v.view, v.view, s); d.classList.add("diff");
    out.push(figure("diff st", d, "DIFFERENCE real vs event ghost", "DIFF ev"));
    if (all) {
      var d2 = mkTile(c, [layerEl(c, R, col), layerEl(c, GS, col, "blend")], "difference real vs start ghost " + v.view, v.view, s); d2.classList.add("diff");
      out.push(figure("diff st", d2, "DIFFERENCE real vs start ghost", "DIFF st"));
      if (pair && pcol >= 0) out.push(ref);
    }
    return out;
  }
  function routeMeta(name, m) {
    var blob = Math.max(m.realOnlyBlobPx || 0, m.ghostOnlyBlobPx || 0);
    return el("div", null, [el("span", {cls: "note", text: name + " "}), vchip(m.verdict, m.reasons.join("\n") || null),
      el("div", {text: "shape blob " + blob + " px, colour blob " + fmt(m.colorBlobPx) + " px, IoU " + fmt(m.iou)}),
      m.trigger ? el("div", {cls: "tr-" + m.verdict, text: m.trigger}) : null]);
  }
  function stateViewMeta(v) {
    var r = v.stowedRef, kids = [routeMeta("event route", v), v.start ? routeMeta("start route", v.start) : el("div", {cls: "note", text: "start route: not measured"})];
    if (r) kids.push(el("div", {cls: "note", text: "real vs stowed ghost: shape blob " + Math.max(r.realOnlyBlobPx || 0, r.ghostOnlyBlobPx || 0) + " px (reference, not judged)"}));
    return el("div", {cls: "vmeta"}, kids);
  }
  function routeChips(c) {
    if (!c.routes) return [];
    return ["event", "start"].filter(function (r) { return c.routes[r]; }).map(function (r) {
      var x = c.routes[r];
      return el("span", {cls: "chip", title: (x.reasons || []).join("\n") || null}, [r + " route ", el("span", {cls: "vc " + x.verdict, text: x.verdict})]);
    });
  }
  function measureTable(title, list) {
    var keys = ["realPx", "ghostPx", "realOnlyTolPx", "ghostOnlyTolPx", "realOnlyBlobPx", "ghostOnlyBlobPx", "realOnlyBlobPct", "ghostOnlyBlobPct", "colorOverlapPx", "colorOverPx", "colorBlobPx", "colorP99", "colorBlockMax", "iou"];
    keys = keys.filter(function (k) { return list.some(function (x) { return x.m && x.m[k] !== undefined && x.m[k] !== null; }); });
    var head = el("tr", null, [el("th", {text: title})].concat(list.map(function (x) { return el("th", {text: x.view}); })));
    var body = el("tbody", null, keys.map(function (k) { return el("tr", null, [el("th", {text: k})].concat(list.map(function (x) { return el("td", {cls: "num", text: x.m ? fmt(x.m[k], k.indexOf("Pct") > 0 ? 3 : undefined) : "-"}); }))); }));
    return el("div", {cls: "tablewrap"}, [el("table", null, [el("thead", null, [head]), body])]);
  }
  function structureTable(label, s) {
    var rows = [];
    s = s || {};
    ["missingOnGhost", "extraOnGhost", "renderedDiffers", "meshDiffers", "transformDiffers", "materialDiffers"].forEach(function (k) {
      (s[k] || []).forEach(function (e) {
        var rest = Object.keys(e).filter(function (x) { return x !== "key"; }).map(function (x) { return x + "=" + (typeof e[x] === "object" ? JSON.stringify(e[x]) : e[x]); }).join("  ");
        rows.push(el("tr", null, [el("td", {cls: (k === "missingOnGhost" || k === "extraOnGhost") && e.rendered === true ? "hit" : null, text: k}), el("td", {text: e.key}), el("td", {text: rest})]));
      });
    });
    var out = [el("div", {cls: "note", text: label + ": renderers real " + fmt(s.realRenderers) + ", ghost " + fmt(s.ghostRenderers) + ", paired " + fmt(s.pairs) + (rows.length ? "" : ", no difference")})];
    if (rows.length) out.push(el("div", {cls: "tablewrap"}, [el("table", null, [el("thead", null, [el("tr", null, [el("th", {text: "kind"}), el("th", {text: "renderer"}), el("th", {text: "detail"})])]), el("tbody", null, rows)])]));
    return out;
  }
  function moreDetails(c) {
    var d = el("details", {cls: "more"}, [el("summary", {text: "details: measures, structure, skin proof, tags, model urls, log"})]);
    d.addEventListener("toggle", function () { if (d.open && !d._built) { d._built = true; fillMore(d, c); } });
    return d;
  }
  function fillMore(d, c) {
    if (c.views.length) {
      d.appendChild(measureTable(c.isState ? "event route" : "measure", c.views.map(function (v) { return {view: v.view, m: v}; })));
      if (c.isState) {
        d.appendChild(measureTable("start route", c.views.map(function (v) { return {view: v.view, m: v.start}; })));
        d.appendChild(measureTable("real vs stowed ghost", c.views.map(function (v) { return {view: v.view, m: v.stowedRef}; })));
      }
    }
    structureTable(c.isState ? "structure, event route" : "structure", c.structure).forEach(function (n) { d.appendChild(n); });
    if (c.isState) structureTable("structure, start route", c.structureStart).forEach(function (n) { d.appendChild(n); });
    var p = c.skinProof || {}, marks = p.marks || {}, sk = ["module", "moduleId", "requested", "real", "snapshot", "ghost"];
    d.appendChild(el("div", {cls: "tablewrap"}, [el("table", null, [el("thead", null, [el("tr", null, [el("th", {text: "skin proof"})].concat(sk.map(function (k) { return el("th", {text: k}); })))]),
      el("tbody", null, [el("tr", null, [el("th", {text: "value"})].concat(sk.map(function (k) { return el("td", {cls: marks[k] ? "hit" : null, text: p[k] === null || p[k] === undefined ? "null" : (p[k] === "" ? "(empty)" : String(p[k]))}); })))])])]));
    var kv = el("div", {cls: "kv"});
    function row(k, v) { kv.appendChild(el("div", {text: k})); kv.appendChild(el("div", {text: v === null || v === undefined || v === "" ? "-" : String(v)})); }
    row("case id", c.caseId); row("setup", (c.setupStatus || "-") + (c.setupDetail ? " - " + c.setupDetail : ""));
    row("tags", (c.tags || []).join(", ")); row("flags", (c.flags || []).join(", "));
    row("part url", c.partUrl); row("model urls", (c.modelUrls || []).join(", "));
    row("modules", c.modules ? (Array.isArray(c.modules) ? c.modules.join(", ") : c.modules) : "not recorded in the sidecar");
    row("category / state / context", [c.category, c.state, c.context].map(function (x) { return x || "-"; }).join(" / "));
    row("ghost root offset m", fmt(c.ghostRootOffsetM));
    if (c.routes) ["event", "start"].forEach(function (r) { var x = c.routes[r]; if (x) row(r + " route", x.verdict + (x.worstView ? " (worst " + x.worstView + ")" : "") + (x.trigger ? ": " + x.trigger : "")); });
    if (c.isState) row("worst view", c.worstView);
    if (c.stateFlags && c.stateFlags.length) row("state flags", c.stateFlags.join(", "));
    var sp = c.stateProof;
    if (sp) row("state proof", [sp.family + "/" + sp.module + "#" + sp.moduleIndex, "action " + sp.action + " (" + sp.actionResult + ")", "reached " + sp.reached, "wait " + fmt(sp.waitS) + " s",
      "endpoint " + (sp.endpoint ? sp.endpoint.field + "=" + sp.endpoint.value : "-"), "events " + (Array.isArray(sp.events) ? sp.events.map(function (e) { return e.type + "@" + fmt(e.ut); }).join(" ") || "none" : "-"),
      "ghost event " + sp.ghostEvent, "ghost start " + sp.ghostStart, "first frame at target " + (sp.startFirstFrame ? sp.startFirstFrame.atTarget : "-")].join("; "));
    if (c.stowedCaseId) row("stowed case", c.stowedCaseId + (c.stowedPair ? "" : " (not loaded: the stowed column is this case's own stowed ghost)"));
    if (c.regression) row("baseline", c.regression.status + (c.regression.changes.length ? ": " + c.regression.changes.join(", ") : ""));
    if (!c.listed) row("manifest", "this sidecar is not listed in the manifest");
    d.appendChild(kv);
    if (c.log.length) d.appendChild(el("pre", {text: c.log.join("\n")}));
  }
  // One case: a stowed skin block (a part with no state case, as before P6), a state
  // sub-block inside a skin container (opts.sub), or a control (opts.control).
  function caseBlock(c, opts) {
    opts = opts || {};
    var isControl = !!opts.control, cls = isControl ? (c.verdict === "control-ok" ? "ctl-ok" : "ctl-bad") : c.verdict;
    var setup = c.setupStatus && c.setupStatus !== "ok" ? el("span", {cls: "chip", text: "setup " + c.setupStatus}) : null;
    var reg = c.regression && c.regression.status !== "same" ? el("span", {cls: "chip moved", text: c.regression.status, title: c.regression.changes.join("\n") || null}) : null;
    var flags = (c.stateFlags || []).map(function (f) { return el("span", {cls: "chip", text: f}); });
    var headKids;
    if (isControl) {
      headKids = [el("span", {cls: "skname", text: c.caseId}), el("span", {cls: "vc " + (c.verdict === "control-ok" ? "pass" : "fail"), text: c.verdict === "control-ok" ? "control ok" : "control BAD"}),
        el("span", {cls: "note", text: (c.control.subject || "") + ". Required: " + (c.control.required || "-") + "."})];
    } else if (opts.sub) {
      headKids = [el("span", {cls: "skname", text: c.stateLabel}), c.family ? el("span", {cls: "chip", text: c.family}) : null, vchip(c.verdict, c.reasons.join("\n") || null)]
        .concat(routeChips(c)).concat(flags).concat([setup, reg, c.trigger ? el("span", {cls: "trig tr-" + c.verdict, text: c.trigger}) : null]);
    } else {
      headKids = [el("span", {cls: "skname", text: "skin " + c.skin}), el("span", {cls: "chip", text: c.skinKind || "-"}), vchip(c.verdict, c.reasons.join("\n") || null),
        setup, reg, c.trigger && !opts.single ? el("span", {cls: "trig tr-" + c.verdict, text: c.trigger}) : null];
    }
    var blk = el("div", {cls: (opts.sub ? "state " : "skin ") + cls, id: "c-" + idOf(c.id)}, [el("div", {cls: "shead"}, headKids)]);
    if (!c.views.length) blk.appendChild(el("div", {cls: "note", text: c.sidecarProblem ? "No sidecar (" + c.sidecarProblem + ")." : "No measured view (setup " + (c.setupStatus || "-") + ")."}));
    c.views.forEach(function (v) {
      var figs = c.isState ? stateFigures(c, v) : pairFigures(c, v), meta = c.isState ? stateViewMeta(v) : viewMeta(v);
      blk.appendChild(el("div", {cls: "vrow"}, [el("div", {cls: "vname", text: v.view})].concat(figs).concat([meta])));
    });
    blk.appendChild(moreDetails(c));
    return blk;
  }
  // A skin with state cases: its stowed case and each state, one bordered sub-block each.
  function skinContainer(sk, single) {
    var hasRest = CASE[sk.cases[0]].isRest;
    var blk = el("div", {cls: "skin " + sk.verdict}, [el("div", {cls: "shead"}, [el("span", {cls: "skname", text: "skin " + sk.skin}), el("span", {cls: "chip", text: sk.skinKind || "-"}), vchip(sk.verdict),
      el("span", {cls: "chip", text: (hasRest ? "stowed + " : "no stowed case loaded, ") + sk.states + " state case(s)"}),
      sk.trigger && !single ? el("span", {cls: "trig tr-" + sk.verdict, text: sk.trigger}) : null])]);
    sk.cases.forEach(function (id) { blk.appendChild(caseBlock(CASE[id], {sub: true})); });
    return blk;
  }
  var ID_SAFE = {};
  function idOf(id) { if (!ID_SAFE[id]) ID_SAFE[id] = "k" + Object.keys(ID_SAFE).length; return ID_SAFE[id]; }

  // Lazy: a group's body is built, and its images set, only near the viewport; the
  // images are dropped again when it scrolls far away, so 1500 atlases never decode at once.
  function setImages(root, on) {
    root.querySelectorAll(".layer[data-src]").forEach(function (l) { l.style.backgroundImage = on ? 'url("' + l.getAttribute("data-src") + '")' : ""; });
  }
  var io = ("IntersectionObserver" in window) ? new IntersectionObserver(function (entries) {
    entries.forEach(function (e) {
      var g = e.target;
      if (e.isIntersecting) { ensureBody(g); setImages(g, true); } else if (g._built) setImages(g, false);
    });
  }, {rootMargin: "1500px 0px"}) : null;
  function ensureBody(sec) {
    if (sec._built) return; sec._built = true;
    var g = sec._group, body = el("div", {cls: "gbody"});
    g.skins.forEach(function (sk) {
      var only = sk.cases.length === 1 ? CASE[sk.cases[0]] : null;
      body.appendChild(only && only.isRest ? caseBlock(only, {single: g.cases.length === 1}) : skinContainer(sk, g.skins.length === 1));
    });
    sec.appendChild(body);
  }
  function groupSection(g) {
    var head = el("div", {cls: "ghead"}, [vchip(g.verdict), el("h2", {text: g.title || g.part}), el("span", {cls: "name", text: g.part}), el("span", {cls: "src", text: g.source || "-"}),
      D.profiles.length > 1 ? el("span", {cls: "chip", text: g.profile}) : null,
      g.skins.length > 1 ? el("span", {cls: "chip", text: g.skins.length + " skins"}) : null,
      g.hasStates ? el("span", {cls: "chip", text: g.states.join(", ")}) : null,
      g.regression && g.regression !== "same" ? el("span", {cls: "chip moved", text: g.regression}) : null]);
    (g.links || []).forEach(function (l) { head.appendChild(el("a", {href: "#case=" + encodeURIComponent(l["case"]), text: "same part in " + l.profile + " (" + l.verdict + ")"})); });
    if (g.trigger) head.appendChild(el("div", {cls: "trigger tr-" + g.verdict, text: g.trigger}));
    var sec = el("section", {cls: "group " + g.verdict, id: "g-" + g._i, "aria-label": (g.title || g.part) + ": " + g.verdict}, [head]);
    sec._group = g;
    var views = 0; g.cases.forEach(function (id) { views += Math.max(1, CASE[id].views.length); });
    sec.style.containIntrinsicSize = "auto " + (60 + g.cases.length * 70 + views * (tilePx() + 34)) + "px";
    GROUP_EL[g.id] = sec;
    if (io) io.observe(sec); else { ensureBody(sec); setImages(sec, true); }
    return sec;
  }
  var _tp = null;
  function tilePx() { if (_tp === null) { var p = el("div", {style: "width: var(--t); position: absolute; visibility: hidden"}); document.body.appendChild(p); _tp = p.getBoundingClientRect().width || 200; p.remove(); } return _tp; }

  // ---- controls ----
  var fillControls = function () {};
  function buildControls() {
    var box = document.getElementById("controls"), body = document.getElementById("ctlbody");
    var ids = D.controls || [], bad = ids.filter(function (id) { return CASE[id].verdict !== "control-ok"; });
    if (!ids.length) { document.getElementById("ctlsum").textContent = "Controls: none recorded (a sweep run with controls=false proves nothing about the instrument)"; box.classList.add("bad"); return; }
    document.getElementById("ctlsum").textContent = "Controls: " + (ids.length - bad.length) + "/" + ids.length + " ok" + (bad.length ? " - NOT OK: " + bad.map(function (id) { return ((D.controlSets || []).length > 1 ? CASE[id].sweep + "/" : "") + CASE[id].caseId; }).join(", ") : " (the instrument proved it sees what it claims to)");
    if (bad.length) { box.classList.add("bad"); box.open = true; }
    fillControls = function () {
      if (box._built) return; box._built = true;
      var sets = D.controlSets || [];
      sets.forEach(function (set) {
        if (sets.length > 1) body.appendChild(el("h2", {text: set.profile + " / " + set.sweep + ": " + set.kind + " control set (" + set.ids.length + ")"}));
        set.ids.forEach(function (id) { body.appendChild(caseBlock(CASE[id], {control: true})); });
      });
      setImages(body, true);
    };
    box.addEventListener("toggle", function () { if (box.open) fillControls(); });
    if (box.open) fillControls();
  }

  // ---- order, filters, navigation ----
  var ORDERED = D.groups.slice();
  function order() {
    ORDERED = D.groups.slice();
    if (state.sort === "name") ORDERED.sort(function (a, b) { var x = (a.title || a.part || "").toLowerCase(), y = (b.title || b.part || "").toLowerCase(); return x < y ? -1 : x > y ? 1 : a._i - b._i; });
    var host = document.getElementById("groups"), frag = document.createDocumentFragment();
    ORDERED.forEach(function (g) { frag.appendChild(GROUP_EL[g.id]); });
    host.appendChild(frag);
  }
  function matches(g) {
    if (state.verdict === "flagged" && g.verdict === "pass") return false;
    if (state.verdict && state.verdict !== "flagged" && !g.cases.some(function (id) { return CASE[id].verdict === state.verdict; })) return false;
    if (state.source && (g.source || "(none)") !== state.source) return false;
    if (state.regression && g.regression !== state.regression) return false;
    if (state.stateWord && g.states.indexOf(state.stateWord) < 0) return false;
    if (state.family && g.families.indexOf(state.family) < 0) return false;
    if (state.stateFlag && g.stateFlags.indexOf(state.stateFlag) < 0) return false;
    if (state.q) { var w = state.q.toLowerCase().split(/\s+/); for (var i = 0; i < w.length; i++) if (w[i] && g._text.indexOf(w[i]) < 0) return false; }
    return true;
  }
  var VISIBLE = [];
  function applyFilters() {
    VISIBLE = [];
    ORDERED.forEach(function (g) { var ok = matches(g); GROUP_EL[g.id].hidden = !ok; if (ok) VISIBLE.push(g); });
    document.getElementById("shown").textContent = VISIBLE.length + " / " + D.groups.length + " part(s) shown";
  }
  function headerHeight() { return document.getElementById("top").getBoundingClientRect().height; }
  var GAP = 8;
  function scrollToEl(t) { window.scrollTo(0, window.scrollY + t.getBoundingClientRect().top - headerHeight() - GAP); }
  function settle(t) {
    scrollToEl(t);
    // content-visibility estimates can move the target once its neighbours render: settle once more.
    requestAnimationFrame(function () { requestAnimationFrame(function () { scrollToEl(t); }); });
  }
  function goTo(sec, target) { ensureBody(sec); setImages(sec, true); settle(target || sec); }
  function currentIndex() {
    var h = headerHeight() + GAP + 4, cur = -1;
    for (var i = 0; i < VISIBLE.length; i++) { var r = GROUP_EL[VISIBLE[i].id].getBoundingClientRect(); if (r.top <= h) cur = i; else break; }
    return cur;
  }
  function step(dir) {
    if (!VISIBLE.length) return;
    var i = currentIndex(), target;
    if (dir > 0) target = Math.min(VISIBLE.length - 1, i + 1);
    else { var r = i >= 0 ? GROUP_EL[VISIBLE[i].id].getBoundingClientRect().top : 0; target = (i >= 0 && r < headerHeight() + GAP - 4) ? i : Math.max(0, i - 1); }
    var g = VISIBLE[target];
    goTo(GROUP_EL[g.id]);
    try { history.replaceState(null, "", "#case=" + encodeURIComponent(g.cases[0])); } catch (e) {}
  }
  function route() {
    var m = /^#case=(.+)$/.exec(location.hash);
    if (!m) return;
    var id = decodeURIComponent(m[1]), c = CASE[id];
    if (!c) return;
    if (c.kind === "control") { var box = document.getElementById("controls"); box.open = true; fillControls(); var t = document.getElementById("c-" + idOf(id)); if (t) settle(t); return; }
    var g = CASE_GROUP[id]; if (!g) return;
    var sec = GROUP_EL[g.id];
    if (sec.hidden) { FILTER_KEYS.forEach(function (k) { state[k] = ""; }); state.q = ""; save(); document.querySelectorAll("#top select").forEach(function (s) { if (s.getAttribute("aria-label") !== "Sort") s.value = ""; }); document.getElementById("q").value = ""; applyFilters(); }
    ensureBody(sec);
    goTo(sec, g.cases[0] === id ? sec : document.getElementById("c-" + idOf(id)));
  }

  // ---- 1:1 ----
  document.addEventListener("click", function (e) {
    var t = e.target.closest ? e.target.closest(".tile[data-case]") : null;
    if (!t || t.closest("dialog")) return;
    openZoom(t.getAttribute("data-case"), t.getAttribute("data-view"));
  });
  document.addEventListener("keydown", function (e) {
    var t = e.target;
    if (e.key === "Enter" && t && t.classList && t.classList.contains("tile") && t.hasAttribute("data-case") && !t.closest("dialog")) { openZoom(t.getAttribute("data-case"), t.getAttribute("data-view")); e.preventDefault(); return; }
    var typing = t && ((t.tagName === "INPUT" && t.type !== "checkbox") || t.tagName === "SELECT" || t.tagName === "TEXTAREA");
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    if (document.getElementById("zoom").open) return;
    if (e.key === "Escape" && typing) { t.blur(); return; }
    if (typing) return;
    if (e.key === "j") { step(1); e.preventDefault(); }
    else if (e.key === "k") { step(-1); e.preventDefault(); }
    else if (e.key === "/") { document.getElementById("q").focus(); e.preventDefault(); }
  });
  function openZoom(id, viewName) {
    var c = CASE[id], v = (c.views || []).filter(function (x) { return x.view === viewName; })[0];
    if (!c || !v) return;
    var body = document.getElementById("zoombody"); body.textContent = "";
    body.appendChild(el("div", {cls: "shead"}, [el("span", {cls: "skname", text: (c.title || c.caseId) + " - skin " + c.skin + (c.isState ? " - " + c.stateLabel : "") + " - " + v.view + " at 1:1"}), vchip(c.kind === "control" ? (c.verdict === "control-ok" ? "pass" : "fail") : v.verdict),
      c.isState && v.start ? el("span", {cls: "chip"}, ["start route ", vchip(v.start.verdict)]) : null]));
    body.appendChild(el("div", {cls: "pair"}, c.isState ? stateFigures(c, v, c.tile || 512, true) : pairFigures(c, v, c.tile || 512)));
    setImages(body, true);
    document.getElementById("zoom").showModal();
  }

  if (D.cases.some(function (c) { return c.isState; })) document.getElementById("statelegend").hidden = false;
  buildTop(); buildSweeps(); buildControls();
  var host = document.getElementById("groups");
  if (!D.groups.length) host.appendChild(el("p", {cls: "note", text: "No part cases in these sweeps."}));
  D.groups.forEach(function (g) { host.appendChild(groupSection(g)); });
  order(); applyFilters();
  window.addEventListener("resize", function () { _tp = null; });
  window.addEventListener("hashchange", route);
  if (location.hash) route();
})();
</script>
</body>
</html>
"""


if __name__ == "__main__":
    sys.exit(main())
