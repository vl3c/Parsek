"""Part ghost parity: the pure verifier behind the ``partParity`` row.

docs/dev/design-part-ghost-parity.md sections 5, 6 and 10. The C# capture engine
(``PartParitySweepRunner``) writes one sweep directory per label,
``<KSP root>/parsek-partparity/<label>/``, holding ``manifest.json`` (schema
``parsek-part-parity/1``), and per case ``<caseId>.png`` (the atlas: rows real /
ghost, columns the views, RGBA), ``<caseId>.thumb.png`` and ``<caseId>.part.json``
(schema ``parsek-part-parity-case/1``), plus ``inflight.txt`` / ``isolate.txt`` while
work is owed. ``run.py``'s harvest leg moves that directory into
``results/<runId>_partparity/<label>/``; this module judges it in place.

The C# side is a measuring instrument; every verdict is decided here (design D7), so
the thresholds re-pin with no flight. Five things are judged:

  * per view and per case: the PART verdicts (pass / warn / fail / error). These are
    REPORT-ONLY until a lane declares ``[expectations.partParity] gating = true``;
    armed, a failing case reds ``PARSEK-FAIL(part-parity)``.
  * the CONTROLS (design 4.5): the manifest's recorded control outcomes. Any control
    not ``ok`` is ``INVALID(parity-controls)``: the instrument did not prove it sees
    what it claims to. Gates from the start, armed or not.
  * COMPLETENESS: the sweep reached ``progress.complete = true`` and every planned
    case has a complete sidecar or a recorded setup fault, no case is still owed a
    solo run. Otherwise ``INVALID(parity-incomplete)``. Gates from the start.
  * the INSTRUMENT: for every control case and a seeded 2 % sample of the part cases,
    the shape measures are recomputed from the atlas PNG and compared with the
    sidecar. A mismatch is ``INVALID(parity-instrument)``. Gates from the start.
  * the skin proof (design 3.2): ``requested != real`` is the setup fault
    ``skin-not-applied`` (an ``error`` case), ``real != ghost`` a ``fail``
    (``skin-mismatch``), compared only where both names were read.

Stdlib only, no I/O: the caller lists the directory, parses the JSON and hands a PNG
reader in (``run.py`` passes ``harness/tools/gui_mirror.py``'s ``read_png``).
"""

from __future__ import annotations

import math
import random
import re
from dataclasses import dataclass, field
from typing import Any, Callable, Collection, Dict, Iterable, List, Optional, Sequence, Tuple

# ---------------------------------------------------------------------------
# The C# contract, mirrored (PartParitySourceSyncTests pins each against the source).
# ---------------------------------------------------------------------------

SWEEP_VERB = "PartParitySweep"
SWEEP_ROOT_DIR = "parsek-partparity"
MANIFEST_FILE = "manifest.json"
INFLIGHT_FILE = "inflight.txt"
ISOLATE_FILE = "isolate.txt"
SIDECAR_SUFFIX = ".part.json"
ATLAS_SUFFIX = ".png"
THUMB_SUFFIX = ".thumb.png"

MANIFEST_SCHEMA = "parsek-part-parity/1"
CASE_SCHEMA = "parsek-part-parity-case/1"

# Key tables of PartParityJson.cs, in writer order. The reader needs only a subset;
# the full tables are mirrored so a renamed C# key reds the sync cell, not a flight.
MANIFEST_KEYS: Tuple[str, ...] = (
    "schema", "label", "profile", "gameDataMods", "dllHash", "tile", "views", "measure",
    "settings", "controls", "cases", "exclusions", "progress",
)
CASE_KEYS: Tuple[str, ...] = (
    "schema", "caseId", "part", "title", "source", "partUrl", "modelUrls", "category",
    "tags", "skin", "state", "context", "measure", "atlas", "framing", "ghostRootOffsetM",
    "layers", "flags", "views", "structure", "setup", "log",
)
SKIN_KEYS: Tuple[str, ...] = ("module", "moduleId", "requested", "real", "snapshot", "ghost")
ATLAS_KEYS: Tuple[str, ...] = ("file", "thumb", "tile", "rows", "cols")
VIEW_KEYS: Tuple[str, ...] = (
    "view", "width", "height",
    "realPx", "ghostPx", "andPx", "orPx", "realOnlyPx", "ghostOnlyPx",
    "realOnlyTolPx", "ghostOnlyTolPx", "realOnlyBlobPx", "ghostOnlyBlobPx",
    "realOnlyBlobPct", "ghostOnlyBlobPct",
    "bbox", "centroid", "touchesBorder",
    "colorOverlapPx", "colorOverPx", "colorBlobPx", "colorP99", "colorBlockMax",
    "iou", "matte",
)
STRUCTURE_KEYS: Tuple[str, ...] = (
    "realRenderers", "ghostRenderers", "pairs", "missingOnGhost", "extraOnGhost",
    "transformDiffers", "materialDiffers", "meshDiffers", "renderedDiffers",
)
RENDERER_REF_KEYS: Tuple[str, ...] = ("key", "mesh", "rendered")
SETUP_KEYS: Tuple[str, ...] = ("status", "detail")
CONTROL_KEYS: Tuple[str, ...] = ("id", "subject", "required", "outcome", "detail")
CASE_SUMMARY_KEYS: Tuple[str, ...] = (
    "index", "caseId", "part", "title", "source", "skin", "state", "context", "sidecar",
    "setupStatus", "maxShapeBlobPx", "maxColorBlobPx", "minIou", "missingRendered",
    "extraRendered",
)
PROGRESS_KEYS: Tuple[str, ...] = (
    "complete", "cases", "completeCases", "setupFaults", "controls", "crashed",
)

# TestCommandPartParitySweep: setup statuses, IsSetupFault, the control ids and the
# controls-state tokens.
SETUP_OK = "ok"
SETUP_SKIN_NOT_APPLIED = "skin-not-applied"
SETUP_SPAWN_TIMEOUT = "spawn-timeout"
SETUP_STILL_ANIMATING = "still-animating"
SETUP_DIED_SETTLING = "died-settling"
SETUP_CRASHED = "crashed"
SETUP_STATUSES: Tuple[str, ...] = (
    SETUP_OK, SETUP_SKIN_NOT_APPLIED, SETUP_SPAWN_TIMEOUT, SETUP_STILL_ANIMATING,
    SETUP_DIED_SETTLING, SETUP_CRASHED,
)
CONTROL_IDS: Tuple[str, ...] = (
    "ctl-empty", "ctl-ghost-absent", "ctl-real-vs-real", "ctl-ghost-scale90",
    "ctl-ghost-rot90", "ctl-ghost-flip", "ctl-ghost-missing-mesh", "ctl-wrong-skin",
)
CONTROLS_OK = "ok"
CONTROLS_BAD = "bad"
CONTROLS_PENDING = "pending"
CONTROLS_OFF = "off"
ATLAS_ROWS: Tuple[str, ...] = ("real", "ghost")

# PartParityMetrics: coverage is alpha >= 0.5, i.e. the alpha byte >= 128.
COVERAGE_ALPHA_BYTE = 128


def is_setup_fault(status: Optional[str]) -> bool:
    """TestCommandPartParitySweep.IsSetupFault: anything but ``ok`` and
    ``still-animating`` (a still-animating case is captured and measured anyway)."""
    return status not in (SETUP_OK, SETUP_STILL_ANIMATING)


# ---------------------------------------------------------------------------
# Verdict constants: the ONE place the thresholds live (design 6). Starting guesses,
# re-pinned after the first full reading run against the controls and a hand-checked
# sample; a re-pin is an edit here and needs no flight.
# ---------------------------------------------------------------------------

# A view fails when either largest shape blob (real-only or ghost-only, after the 1 px
# tolerance) exceeds this percentage of the projected shared box area...
SHAPE_BLOB_FAIL_PCT = 0.2
# ...or this many pixels, or when the largest blob of over-T colour pixels exceeds
# COLOR_BLOB_FAIL_PX.
SHAPE_BLOB_FAIL_PX = 64
COLOR_BLOB_FAIL_PX = 64
# warn at this fraction of every fail line.
WARN_FRACTION = 0.25

# The instrument check: every control plus this fraction of the part cases (at least
# one when any part case has an atlas), drawn by a generator seeded with the run id.
RECOMPUTE_SAMPLE_FRACTION = 0.02
# Float measures (centroid, iou) compare within this; every count compares exactly.
RECOMPUTE_FLOAT_TOLERANCE = 1e-9

VERDICT_PASS = "pass"
VERDICT_WARN = "warn"
VERDICT_FAIL = "fail"
VERDICT_ERROR = "error"
VERDICTS: Tuple[str, ...] = (VERDICT_PASS, VERDICT_WARN, VERDICT_FAIL, VERDICT_ERROR)
_RANK = {VERDICT_PASS: 0, VERDICT_WARN: 1, VERDICT_FAIL: 2, VERDICT_ERROR: 3}

# Row statuses (saveparse's vocabulary plus INVALID for the three instrument gates).
STATUS_PASS = "PASS"
STATUS_FAIL = "FAIL"
STATUS_REPORT = "REPORT"
STATUS_INVALID = "INVALID"
STATUS_SKIPPED = "SKIPPED"

# The verdict classes this row can drive (registered in hlib).
INVALID_CONTROLS_SUBKIND = "parity-controls"
INVALID_INSTRUMENT_SUBKIND = "parity-instrument"
INVALID_INCOMPLETE_SUBKIND = "parity-incomplete"
INVALID_SUBKINDS: Tuple[str, ...] = (
    INVALID_CONTROLS_SUBKIND, INVALID_INSTRUMENT_SUBKIND, INVALID_INCOMPLETE_SUBKIND,
)
PARSEK_FAIL_SUBKIND = "part-parity"

# The verifier's own output, written beside the sweep it judged.
VERDICTS_FILE = "partparity-verdicts.json"
VERDICTS_SCHEMA = "parsek-part-parity-verdicts/1"

# How many worst cases the summary line names.
WORST_CASES_IN_SUMMARY = 5

# ---------------------------------------------------------------------------
# The spec surface: [expectations.partParity].
# ---------------------------------------------------------------------------

PART_PARITY_BLOCK = "partParity"
PART_PARITY_BLOCK_KEYS: Tuple[str, ...] = ("gating",)

_LABEL_RE = re.compile(r"^[A-Za-z0-9._-]{1,96}$")


def sweep_labels(spec: Optional[Dict]) -> List[str]:
    """The labels the spec's ``PartParitySweep`` steps name, first-use order, each
    once. A label that is not a literal (a ``${...}`` substitution, a non-string) or
    falls outside the label alphabet is left out: the harvest moves directories by
    name, so it only ever touches a name the spec states verbatim."""
    labels: List[str] = []
    driver = (spec or {}).get("driver") or {}
    for step in driver.get("steps") or []:
        if not isinstance(step, dict) or step.get("cmd") != SWEEP_VERB:
            continue
        label = (step.get("args") or {}).get("label")
        if isinstance(label, str) and _LABEL_RE.match(label) and label not in labels \
                and label not in (".", ".."):
            labels.append(label)
    return labels


def declared_block(expectations: Optional[Dict]) -> Optional[Dict]:
    block = (expectations or {}).get(PART_PARITY_BLOCK)
    return block if isinstance(block, dict) else None


def gating_armed(expectations: Optional[Dict]) -> bool:
    """True iff ``[expectations.partParity] gating = true``. Arming is a per-scenario
    operator decision taken after a report-only reading run (the saveparse arming
    rule); the armed set is pinned by PARTPARITY_ARMED_SPECS in test_hlib.py."""
    block = declared_block(expectations)
    return bool(block) and block.get("gating") is True


def validate_part_parity_expectations(block: Any, steps: Optional[Sequence] = None) -> List[str]:
    """Pre-launch shape checks for ``[expectations.partParity]``. None (no block) is
    valid. ``steps`` is the driver's step list: a declared block with no
    PartParitySweep step has nothing to judge, so it is refused rather than reading
    as a SKIPPED row forever."""
    if block is None:
        return []
    prefix = "expectations.%s" % PART_PARITY_BLOCK
    if not isinstance(block, dict):
        return ["%s must be a table" % prefix]
    errors: List[str] = []
    for key in sorted(block):
        if key not in PART_PARITY_BLOCK_KEYS:
            errors.append("%s.%s is not a known key (known: %s)"
                          % (prefix, key, ", ".join(PART_PARITY_BLOCK_KEYS)))
    if "gating" in block and not isinstance(block.get("gating"), bool):
        errors.append("%s.gating must be a bool" % prefix)
    if steps is not None and not sweep_labels({"driver": {"steps": list(steps)}}):
        errors.append("%s is declared but no %s step names a literal label, so the row "
                      "would have nothing to judge" % (prefix, SWEEP_VERB))
    return errors


# ---------------------------------------------------------------------------
# Per-view and per-case verdicts.
# ---------------------------------------------------------------------------

def _num(value: Any) -> Optional[float]:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if isinstance(value, float) and (math.isnan(value) or math.isinf(value)):
        return None
    return float(value)


@dataclass(frozen=True)
class ViewVerdict:
    view: str
    verdict: str
    reasons: Tuple[str, ...]
    shape_blob_px: int
    shape_blob_pct: Optional[float]
    color_blob_px: int


def judge_view(view: Dict) -> ViewVerdict:
    """One view: ``fail`` when either largest shape blob exceeds SHAPE_BLOB_FAIL_PCT of
    the box area or SHAPE_BLOB_FAIL_PX, or the largest colour blob exceeds
    COLOR_BLOB_FAIL_PX; ``warn`` at WARN_FRACTION of those lines; else ``pass``. A
    percentage the C# side could not compute (null: no box area) is not judged."""
    name = str(view.get("view") or "?")
    blob_px = int(max(_num(view.get("realOnlyBlobPx")) or 0, _num(view.get("ghostOnlyBlobPx")) or 0))
    pcts = [p for p in (_num(view.get("realOnlyBlobPct")), _num(view.get("ghostOnlyBlobPct")))
            if p is not None]
    blob_pct = max(pcts) if pcts else None
    color_px = int(_num(view.get("colorBlobPx")) or 0)

    def over(scale: float) -> List[str]:
        hits = []
        if blob_px > SHAPE_BLOB_FAIL_PX * scale:
            hits.append("shape-blob-px:%s=%d" % (name, blob_px))
        if blob_pct is not None and blob_pct > SHAPE_BLOB_FAIL_PCT * scale:
            hits.append("shape-blob-pct:%s=%.3f" % (name, blob_pct))
        if color_px > COLOR_BLOB_FAIL_PX * scale:
            hits.append("color-blob-px:%s=%d" % (name, color_px))
        return hits

    fail = over(1.0)
    if fail:
        return ViewVerdict(name, VERDICT_FAIL, tuple(fail), blob_px, blob_pct, color_px)
    warn = over(WARN_FRACTION)
    if warn:
        return ViewVerdict(name, VERDICT_WARN, tuple(warn), blob_px, blob_pct, color_px)
    return ViewVerdict(name, VERDICT_PASS, (), blob_px, blob_pct, color_px)


@dataclass(frozen=True)
class CaseVerdict:
    case_id: str
    part: Optional[str]
    skin: Optional[str]
    source: Optional[str]
    verdict: str
    reasons: Tuple[str, ...]
    worst_view: Optional[str]
    max_shape_blob_px: int
    max_shape_blob_pct: Optional[float]
    max_color_blob_px: int
    setup_status: Optional[str]

    def to_dict(self) -> Dict[str, Any]:
        return {"caseId": self.case_id, "part": self.part, "skin": self.skin,
                "source": self.source, "verdict": self.verdict,
                "reasons": list(self.reasons), "worstView": self.worst_view,
                "maxShapeBlobPx": self.max_shape_blob_px,
                "maxShapeBlobPct": self.max_shape_blob_pct,
                "maxColorBlobPx": self.max_color_blob_px,
                "setupStatus": self.setup_status}


def _rendered_entries(entries: Any) -> List[str]:
    keys = []
    for e in entries or []:
        if isinstance(e, dict) and e.get("rendered") is True:
            keys.append(str(e.get("key")))
    return keys


SKIN_B9_MODULE = "ModuleB9PartSwitch"
SKIN_FLAG_MISMATCH = "skin-mismatch"
SKIN_FLAG_NOT_MODELLED_B9 = "skin-not-modelled:b9"


def skin_flags(skin: Optional[Dict]) -> Tuple[str, ...]:
    """The ghost-defect skin flags, exactly ``PartParitySkinRules.GhostFlags``: no module,
    no flags; a B9 module whose ghost name was not read is ``skin-not-modelled:b9`` (the
    builder applies no B9 subtype); otherwise ``skin-mismatch`` when real and ghost differ
    case-insensitively, a null on one side only counting as a difference."""
    if not isinstance(skin, dict) or skin.get("module") is None:
        return ()
    real, ghost = skin.get("real"), skin.get("ghost")
    if skin.get("module") == SKIN_B9_MODULE and ghost is None:
        return (SKIN_FLAG_NOT_MODELLED_B9,)
    if (real is None) != (ghost is None) or (
            real is not None and str(real).lower() != str(ghost).lower()):
        return (SKIN_FLAG_MISMATCH,)
    return ()


def judge_skin(skin: Optional[Dict]) -> Tuple[Optional[str], Tuple[str, ...]]:
    """The skin proof. Returns (verdict or None, reasons). ``requested != real`` (exact)
    is a setup fault (``error``); ``skin-mismatch`` (``skin_flags``) is a ghost defect
    (``fail``) even when the pictures look alike; ``skin-not-modelled:b9`` is a known
    builder gap, reported as ``warn``."""
    if not isinstance(skin, dict):
        return None, ()
    requested, real, ghost = skin.get("requested"), skin.get("real"), skin.get("ghost")
    if requested is not None and real != requested:
        return VERDICT_ERROR, ("skin-not-applied:requested=%s real=%s" % (requested, real),)
    flags = skin_flags(skin)
    if SKIN_FLAG_MISMATCH in flags:
        return VERDICT_FAIL, ("skin-mismatch:real=%s ghost=%s" % (real, ghost),)
    if SKIN_FLAG_NOT_MODELLED_B9 in flags:
        return VERDICT_WARN, (SKIN_FLAG_NOT_MODELLED_B9,)
    return None, ()


def skin_flag_disagreement(sidecar: Dict) -> Optional[str]:
    """None when the sidecar's own ``skin-*`` flags (written by the C# runner) equal the
    flags this module derives from its ``skin`` block; else the difference. Only a case
    with views is compared (the C# side computes the flags only for a built ghost)."""
    if not isinstance(sidecar, dict) or not sidecar.get("views"):
        return None
    wrote = sorted(f for f in sidecar.get("flags") or []
                   if isinstance(f, str) and f.startswith("skin-") and f != "skin-not-applied")
    derived = sorted(skin_flags(sidecar.get("skin")))
    if wrote == derived:
        return None
    return "skin flags: sidecar=%s derived=%s" % (",".join(wrote) or "-", ",".join(derived) or "-")


def judge_case(sidecar: Dict) -> CaseVerdict:
    """Case verdict = the worst view, raised to ``fail`` by a missing / extra RENDERED
    mesh in the structural diff or a skin mismatch; ``error`` for a setup fault (the
    measurements, if any, are not a finding about the ghost)."""
    case_id = str(sidecar.get("caseId") or "?")
    skin = sidecar.get("skin") if isinstance(sidecar.get("skin"), dict) else {}
    status = (sidecar.get("setup") or {}).get("status") if isinstance(sidecar.get("setup"), dict) else None
    views = [v for v in sidecar.get("views") or [] if isinstance(v, dict)]
    judged = [judge_view(v) for v in views]
    max_px = max([j.shape_blob_px for j in judged], default=0)
    pcts = [j.shape_blob_pct for j in judged if j.shape_blob_pct is not None]
    max_pct = max(pcts) if pcts else None
    max_color = max([j.color_blob_px for j in judged], default=0)
    worst = None
    verdict = VERDICT_PASS
    reasons: List[str] = []
    for j in judged:
        if _RANK[j.verdict] > _RANK[verdict]:
            verdict, worst = j.verdict, j.view
        reasons.extend(j.reasons)
    structure = sidecar.get("structure") if isinstance(sidecar.get("structure"), dict) else {}
    missing = _rendered_entries(structure.get("missingOnGhost"))
    extra = _rendered_entries(structure.get("extraOnGhost"))
    if missing or extra:
        reasons.extend(["missing-rendered:%s" % k for k in missing])
        reasons.extend(["extra-rendered:%s" % k for k in extra])
        if _RANK[VERDICT_FAIL] > _RANK[verdict]:
            verdict = VERDICT_FAIL
    skin_verdict, skin_reasons = judge_skin(skin)
    reasons.extend(skin_reasons)
    if skin_verdict is not None and _RANK[skin_verdict] > _RANK[verdict]:
        verdict = skin_verdict
    if status is None or is_setup_fault(status):
        reasons.insert(0, "setup:%s" % (status or "missing"))
        verdict = VERDICT_ERROR
    elif not views:
        reasons.insert(0, "no-views")
        verdict = VERDICT_ERROR
    return CaseVerdict(case_id, sidecar.get("part"), skin.get("requested"),
                       sidecar.get("source"), verdict, tuple(reasons), worst, max_px,
                       max_pct, max_color, status)


# ---------------------------------------------------------------------------
# Controls and completeness.
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class ControlsCheck:
    ok: bool
    state: Optional[str]
    outcomes: Tuple[Tuple[str, str], ...]
    problems: Tuple[str, ...]


def check_controls(manifest: Dict) -> ControlsCheck:
    """Every control of design 4.5 must be recorded with outcome ``ok``, and the
    manifest's own controls state must read ``ok``. A sweep run with ``controls=false``
    (state ``off``) proves nothing about the instrument, so it fails this check too."""
    outcomes: Dict[str, str] = {}
    for c in manifest.get("controls") or []:
        if isinstance(c, dict) and isinstance(c.get("id"), str):
            outcomes[c["id"]] = str(c.get("outcome"))
    problems: List[str] = []
    for cid in CONTROL_IDS:
        got = outcomes.get(cid)
        if got is None:
            problems.append("%s:absent" % cid)
        elif got != CONTROLS_OK:
            problems.append("%s:%s" % (cid, got))
    state = (manifest.get("progress") or {}).get("controls") \
        if isinstance(manifest.get("progress"), dict) else None
    if state != CONTROLS_OK:
        problems.append("progress.controls=%s" % state)
    ordered = tuple((cid, outcomes[cid]) for cid in CONTROL_IDS if cid in outcomes)
    return ControlsCheck(not problems, state, ordered, tuple(problems))


def _sidecar_complete(sidecar: Optional[Dict], case_id: str,
                      atlases: Optional[Collection[str]] = None) -> Optional[str]:
    """None when the sidecar is a complete record of ``case_id``; else why not. With
    ``atlases`` (the harvested atlas file names), a case with views must also name an
    atlas that was harvested: its pictures are the evidence the viewer shows."""
    if not isinstance(sidecar, dict):
        return "no-sidecar"
    if sidecar.get("schema") != CASE_SCHEMA:
        return "schema=%s" % sidecar.get("schema")
    if sidecar.get("caseId") != case_id:
        return "caseId=%s" % sidecar.get("caseId")
    setup = sidecar.get("setup")
    status = setup.get("status") if isinstance(setup, dict) else None
    if status not in SETUP_STATUSES:
        return "setup.status=%s" % status
    if is_setup_fault(status):
        return None  # a recorded setup fault is a complete record
    views = sidecar.get("views")
    if not isinstance(views, list) or not views:
        return "no-views"
    if atlases is not None:
        meta = sidecar.get("atlas")
        name = meta.get("file") if isinstance(meta, dict) else None
        if not name:
            return "no-atlas"
        if name not in atlases:
            return "atlas-missing:%s" % name
    return None


@dataclass(frozen=True)
class CompletenessCheck:
    ok: bool
    planned: int
    complete: int
    setup_faults: int
    problems: Tuple[str, ...]


def check_completeness(manifest: Dict, sidecars: Dict[str, Optional[Dict]],
                       isolate_lines: Sequence[str] = (),
                       inflight_lines: Sequence[str] = (),
                       atlases: Optional[Collection[str]] = None) -> CompletenessCheck:
    """The sweep reached ``progress.complete = true``; every planned case (the
    manifest's ``cases[]``, which already leaves the exclusions out) and, when controls
    ran, every control has a complete sidecar or a recorded setup fault; and no case
    is still owed a solo run (``isolate.txt``). A stale ``inflight.txt`` at the end
    names a case the last step left in flight, so it is a problem too. ``sidecars``
    maps case id -> parsed sidecar (None: present but unreadable); ``atlases``, when
    given, is the set of harvested atlas file names a case with views must name."""
    problems: List[str] = []
    progress = manifest.get("progress") if isinstance(manifest.get("progress"), dict) else {}
    if progress.get("complete") is not True:
        problems.append("progress.complete=%s" % progress.get("complete"))
    planned_ids: List[str] = []
    for entry in manifest.get("cases") or []:
        if isinstance(entry, dict) and isinstance(entry.get("caseId"), str):
            planned_ids.append(entry["caseId"])
    if not planned_ids:
        problems.append("no planned cases")
    if progress.get("controls") != CONTROLS_OFF:
        planned_ids = list(CONTROL_IDS) + [c for c in planned_ids if c not in CONTROL_IDS]
    complete = faults = 0
    incomplete: List[str] = []
    for cid in planned_ids:
        why = _sidecar_complete(sidecars.get(cid), cid, atlases)
        if why is None:
            complete += 1
            status = ((sidecars.get(cid) or {}).get("setup") or {}).get("status")
            if is_setup_fault(status):
                faults += 1
        else:
            incomplete.append("%s:%s" % (cid, why))
    if incomplete:
        problems.append("%d case(s) without a complete sidecar: %s"
                        % (len(incomplete), ", ".join(incomplete[:5])
                           + (" ..." if len(incomplete) > 5 else "")))
    owed = [l.strip() for l in isolate_lines if l.strip() and not l.strip().startswith("#")]
    if owed:
        problems.append("isolate.txt owes %d solo run(s): %s" % (len(owed), ", ".join(owed[:5])))
    stale = [l.strip() for l in inflight_lines if l.strip() and not l.strip().startswith("#")]
    if stale:
        problems.append("inflight.txt left behind: %s" % ", ".join(stale[:5]))
    return CompletenessCheck(not problems, len(planned_ids), complete, faults, tuple(problems))


# ---------------------------------------------------------------------------
# The Python recompute of the shape measures (PartParityMetrics.Measure, shape half).
#
# Definitions, exactly the C# ones: coverage is the alpha byte >= 128; a real-only
# pixel counts toward the TOLERANT mismatch only when no ghost-covered pixel lies in
# its 8-neighbourhood (and the mirror for ghost-only; including the pixel itself is
# equivalent, since a real-only pixel is not ghost-covered); a blob is a 4-connected
# component; bbox is [x, y, w, h] of the covered pixels; the centroid is the mean of
# the pixel centres (x + 0.5, y + 0.5); iou is and / or, 1 when both sides are empty.
#
# Rows are Python ints (bit (w - 1 - x) is pixel x), so the dilation, the masks and
# the counts are whole-row integer operations, and the blobs are found over row runs.
# ---------------------------------------------------------------------------

_BIT_TABLE = bytes((0x31 if b >= COVERAGE_ALPHA_BYTE else 0x30) for b in range(256))


def tile_alpha_rows(pixels: bytes, width: int, bpp: int, x0: int, y0: int,
                    tile: int) -> List[int]:
    """The coverage rows of one atlas tile: ``tile`` ints, bit ``tile - 1 - x`` set
    when pixel x of that row has alpha >= 0.5."""
    if bpp != 4:
        raise ValueError("atlas is not RGBA (bytes per pixel %d)" % bpp)
    rows = []
    stride = width * 4
    for y in range(tile):
        start = (y0 + y) * stride + x0 * 4
        alpha = pixels[start + 3:start + tile * 4:4]
        if len(alpha) != tile:
            raise ValueError("atlas tile out of range at row %d" % (y0 + y))
        rows.append(int(alpha.translate(_BIT_TABLE), 2))
    return rows


def _dilate(rows: Sequence[int], width: int) -> List[int]:
    full = (1 << width) - 1
    horiz = [(r | (r << 1) | (r >> 1)) & full for r in rows]
    out = []
    n = len(horiz)
    for y in range(n):
        v = horiz[y]
        if y > 0:
            v |= horiz[y - 1]
        if y + 1 < n:
            v |= horiz[y + 1]
        out.append(v)
    return out


def _runs(row: int, width: int) -> List[Tuple[int, int]]:
    """[x_start, x_end] (inclusive) runs of set pixels in one row."""
    if not row:
        return []
    text = format(row, "0%db" % width)
    return [(m.start(), m.end() - 1) for m in re.finditer("1+", text)]


def largest_blob(rows: Sequence[int], width: int) -> int:
    """Size of the largest 4-connected component (0 when empty): runs of one row join
    runs of the next row they overlap in x, union-find over the runs."""
    parent: List[int] = []
    size: List[int] = []

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    prev: List[Tuple[int, int, int]] = []
    for row in rows:
        cur: List[Tuple[int, int, int]] = []
        for a, b in _runs(row, width):
            idx = len(parent)
            parent.append(idx)
            size.append(b - a + 1)
            for pa, pb, pidx in prev:
                if pb < a or pa > b:
                    continue
                ra, rb = find(idx), find(pidx)
                if ra != rb:
                    if size[ra] < size[rb]:
                        ra, rb = rb, ra
                    parent[rb] = ra
                    size[ra] += size[rb]
            cur.append((a, b, idx))
        prev = cur
    best = 0
    for i in range(len(parent)):
        if parent[i] == i and size[i] > best:
            best = size[i]
    return best


def _bit_weights(width: int) -> List[int]:
    """W_k: the int whose bit b is set when bit k of b is set (b < width)."""
    weights = []
    k = 0
    while (1 << k) < width:
        w = 0
        for b in range(width):
            if (b >> k) & 1:
                w |= 1 << b
        weights.append(w)
        k += 1
    return weights


def _shape(rows: Sequence[int], width: int, weights: Sequence[int]) -> Dict[str, Any]:
    count = 0
    sum_x = 0
    sum_y = 0
    min_x = min_y = None
    max_x = max_y = None
    border = False
    left = 1 << (width - 1)
    for y, r in enumerate(rows):
        if not r:
            continue
        c = r.bit_count()
        count += c
        sum_y += y * c
        bit_sum = 0
        for k, w in enumerate(weights):
            bit_sum += (1 << k) * (r & w).bit_count()
        sum_x += (width - 1) * c - bit_sum
        lo_x = width - r.bit_length()
        hi_x = width - 1 - ((r & -r).bit_length() - 1)
        min_x = lo_x if min_x is None else min(min_x, lo_x)
        max_x = hi_x if max_x is None else max(max_x, hi_x)
        if min_y is None:
            min_y = y
        max_y = y
        if r & left or r & 1:
            border = True
    if count and (min_y == 0 or max_y == len(rows) - 1):
        border = True
    if not count:
        return {"px": 0, "bbox": None, "centroid": None, "touchesBorder": False}
    return {"px": count,
            "bbox": [min_x, min_y, max_x - min_x + 1, max_y - min_y + 1],
            "centroid": [(sum_x + 0.5 * count) / count, (sum_y + 0.5 * count) / count],
            "touchesBorder": border}


def recompute_view(real_rows: Sequence[int], ghost_rows: Sequence[int],
                   width: int) -> Dict[str, Any]:
    """The shape measures of one view from its two coverage-row lists, keyed as the
    sidecar keys them."""
    full = (1 << width) - 1
    weights = _bit_weights(width)
    dil_real = _dilate(real_rows, width)
    dil_ghost = _dilate(ghost_rows, width)
    real_only_tol: List[int] = []
    ghost_only_tol: List[int] = []
    out = {"realPx": 0, "ghostPx": 0, "andPx": 0, "orPx": 0, "realOnlyPx": 0,
           "ghostOnlyPx": 0, "realOnlyTolPx": 0, "ghostOnlyTolPx": 0}
    for r, g, dr, dg in zip(real_rows, ghost_rows, dil_real, dil_ghost):
        ro = r & ~g & full
        go = g & ~r & full
        rt = ro & ~dg & full
        gt = go & ~dr & full
        out["realPx"] += r.bit_count()
        out["ghostPx"] += g.bit_count()
        out["andPx"] += (r & g).bit_count()
        out["orPx"] += (r | g).bit_count()
        out["realOnlyPx"] += ro.bit_count()
        out["ghostOnlyPx"] += go.bit_count()
        out["realOnlyTolPx"] += rt.bit_count()
        out["ghostOnlyTolPx"] += gt.bit_count()
        real_only_tol.append(rt)
        ghost_only_tol.append(gt)
    out["realOnlyBlobPx"] = largest_blob(real_only_tol, width)
    out["ghostOnlyBlobPx"] = largest_blob(ghost_only_tol, width)
    rs = _shape(real_rows, width, weights)
    gs = _shape(ghost_rows, width, weights)
    out["bbox"] = {"real": rs["bbox"], "ghost": gs["bbox"]}
    out["centroid"] = {"real": rs["centroid"], "ghost": gs["centroid"]}
    out["touchesBorder"] = {"real": rs["touchesBorder"], "ghost": gs["touchesBorder"]}
    out["iou"] = out["andPx"] / out["orPx"] if out["orPx"] > 0 else 1.0
    return out


RECOMPUTED_INT_KEYS: Tuple[str, ...] = (
    "realPx", "ghostPx", "andPx", "orPx", "realOnlyPx", "ghostOnlyPx",
    "realOnlyTolPx", "ghostOnlyTolPx", "realOnlyBlobPx", "ghostOnlyBlobPx",
)


def _float_equal(a: Any, b: Any) -> bool:
    if a is None or b is None:
        return a is None and b is None
    try:
        return abs(float(a) - float(b)) <= RECOMPUTE_FLOAT_TOLERANCE * max(1.0, abs(float(b)))
    except (TypeError, ValueError):
        return False


def compare_view(recorded: Dict, recomputed: Dict) -> List[str]:
    """Every difference between a sidecar view and its recompute, as
    ``<view>.<key>:<recorded>!=<recomputed>``."""
    view = str(recorded.get("view") or "?")
    diffs: List[str] = []
    for key in RECOMPUTED_INT_KEYS:
        if recorded.get(key) != recomputed[key]:
            diffs.append("%s.%s:%s!=%s" % (view, key, recorded.get(key), recomputed[key]))
    for side in ATLAS_ROWS:
        rb = (recorded.get("bbox") or {}).get(side)
        if rb != recomputed["bbox"][side]:
            diffs.append("%s.bbox.%s:%s!=%s" % (view, side, rb, recomputed["bbox"][side]))
        rt = (recorded.get("touchesBorder") or {}).get(side)
        if rt != recomputed["touchesBorder"][side]:
            diffs.append("%s.touchesBorder.%s:%s!=%s"
                         % (view, side, rt, recomputed["touchesBorder"][side]))
        rc = (recorded.get("centroid") or {}).get(side)
        nc = recomputed["centroid"][side]
        if (rc is None) != (nc is None) or (
                rc is not None and not (isinstance(rc, list) and len(rc) == 2
                                        and _float_equal(rc[0], nc[0])
                                        and _float_equal(rc[1], nc[1]))):
            diffs.append("%s.centroid.%s:%s!=%s" % (view, side, rc, nc))
    if not _float_equal(recorded.get("iou"), recomputed["iou"]):
        diffs.append("%s.iou:%s!=%s" % (view, recorded.get("iou"), recomputed["iou"]))
    return diffs


PngReader = Callable[[str], Tuple[int, int, int, bytes]]


def recompute_case(sidecar: Dict, atlas: Tuple[int, int, int, bytes]) -> List[str]:
    """Recompute every view of one case from its decoded atlas (``(w, h, bpp,
    pixels)``, rows top-down) and return the differences with the sidecar. An atlas
    whose layout does not match the sidecar's ``atlas`` block is itself a difference."""
    width, height, bpp, pixels = atlas
    meta = sidecar.get("atlas") if isinstance(sidecar.get("atlas"), dict) else {}
    tile = meta.get("tile")
    cols = meta.get("cols") or []
    rows = meta.get("rows") or []
    if not isinstance(tile, int) or tile <= 0 or not cols:
        return ["atlas block unusable: tile=%s cols=%s" % (tile, cols)]
    if list(rows) != list(ATLAS_ROWS):
        return ["atlas rows=%s (expected %s)" % (rows, list(ATLAS_ROWS))]
    if width != tile * len(cols) or height != tile * len(ATLAS_ROWS):
        return ["atlas size %dx%d != %d cols x %d rows of %d px"
                % (width, height, len(cols), len(ATLAS_ROWS), tile)]
    if bpp != 4:
        return ["atlas is not RGBA (bytes per pixel %d)" % bpp]
    diffs: List[str] = []
    views = [v for v in sidecar.get("views") or [] if isinstance(v, dict)]
    if len(views) != len(cols):
        diffs.append("views=%d atlas cols=%d" % (len(views), len(cols)))
    for view in views:
        name = view.get("view")
        if name not in cols:
            diffs.append("%s: not in the atlas columns %s" % (name, cols))
            continue
        if view.get("width") != tile or view.get("height") != tile:
            diffs.append("%s.size:%sx%s!=%d" % (name, view.get("width"), view.get("height"), tile))
            continue
        c = cols.index(name)
        real_rows = tile_alpha_rows(pixels, width, bpp, c * tile, 0, tile)
        ghost_rows = tile_alpha_rows(pixels, width, bpp, c * tile, tile, tile)
        diffs.extend(compare_view(view, recompute_view(real_rows, ghost_rows, tile)))
    return diffs


def select_recompute_sample(part_case_ids: Sequence[str], run_id: str,
                            fraction: float = RECOMPUTE_SAMPLE_FRACTION) -> List[str]:
    """The seeded sample of part cases the instrument check recomputes:
    ``ceil(fraction * n)`` ids (at least one when any exist), drawn by
    ``random.Random(<run id>)`` from the ids in sorted order, so a run's sample is
    reproducible from its id alone. Returned sorted."""
    ids = sorted(set(part_case_ids))
    if not ids:
        return []
    k = min(len(ids), max(1, int(math.ceil(fraction * len(ids)))))
    return sorted(random.Random(str(run_id)).sample(ids, k))


# ---------------------------------------------------------------------------
# One sweep directory, and the row over every label.
# ---------------------------------------------------------------------------

@dataclass
class SweepInput:
    """What the shell read from one sweep directory. ``manifest`` is None when the
    file is absent or unreadable (``manifest_error`` says which); ``sidecars`` maps a
    case id to its parsed sidecar (None: present but unreadable); ``atlases`` is the
    set of atlas file names present."""
    label: str
    present: bool
    manifest: Optional[Dict] = None
    manifest_error: Optional[str] = None
    sidecars: Dict[str, Optional[Dict]] = field(default_factory=dict)
    atlases: Tuple[str, ...] = ()
    inflight_lines: Tuple[str, ...] = ()
    isolate_lines: Tuple[str, ...] = ()


@dataclass
class SweepResult:
    label: str
    status: str
    invalid_subkind: Optional[str]
    gating: bool
    problems: List[str]
    controls: Optional[ControlsCheck]
    completeness: Optional[CompletenessCheck]
    cases: List[CaseVerdict]
    recompute_ids: List[str]
    recompute_mismatches: List[str]
    counts: Dict[str, int]

    def worst(self, n: int = WORST_CASES_IN_SUMMARY) -> List[CaseVerdict]:
        ranked = [c for c in self.cases if c.verdict in (VERDICT_FAIL, VERDICT_WARN, VERDICT_ERROR)]
        ranked.sort(key=lambda c: (-_RANK[c.verdict], -c.max_shape_blob_px,
                                   -c.max_color_blob_px, c.case_id))
        return ranked[:n]


def _count(cases: Iterable[CaseVerdict]) -> Dict[str, int]:
    counts = {v: 0 for v in VERDICTS}
    n = 0
    for c in cases:
        counts[c.verdict] += 1
        n += 1
    counts["cases"] = n
    return counts


def evaluate_sweep(sweep: SweepInput, run_id: str, gating: bool,
                   read_png: Optional[PngReader],
                   atlas_path: Callable[[str], str]) -> SweepResult:
    """Judge one sweep directory. ``read_png(path) -> (w, h, bpp, pixels)`` decodes an
    atlas; ``atlas_path(file_name)`` resolves a file of this sweep to a path the reader
    takes. ``read_png`` None skips the recompute and records that as an instrument
    problem (the row cannot vouch for the C# numbers without it)."""
    problems: List[str] = []
    if not sweep.present:
        return SweepResult(sweep.label, STATUS_INVALID, INVALID_INCOMPLETE_SUBKIND, gating,
                           ["label directory not harvested"], None, None, [], [], [],
                           _count([]))
    manifest = sweep.manifest
    if manifest is None or manifest.get("schema") != MANIFEST_SCHEMA:
        why = sweep.manifest_error or ("schema=%s" % (manifest or {}).get("schema"))
        return SweepResult(sweep.label, STATUS_INVALID, INVALID_INCOMPLETE_SUBKIND, gating,
                           ["manifest unusable: %s" % why], None, None, [], [], [], _count([]))

    controls = check_controls(manifest)
    completeness = check_completeness(manifest, sweep.sidecars, sweep.isolate_lines,
                                      sweep.inflight_lines, sweep.atlases)

    planned = [e["caseId"] for e in manifest.get("cases") or []
               if isinstance(e, dict) and isinstance(e.get("caseId"), str)]
    part_ids = [cid for cid in planned if cid not in CONTROL_IDS]
    cases = [judge_case(sweep.sidecars[cid]) for cid in part_ids
             if isinstance(sweep.sidecars.get(cid), dict)]

    # The instrument check: every control with an atlas, plus the seeded sample of
    # the part cases with an atlas.
    def has_atlas(cid: str) -> bool:
        s = sweep.sidecars.get(cid)
        meta = s.get("atlas") if isinstance(s, dict) else None
        return isinstance(meta, dict) and meta.get("file") in sweep.atlases

    controls_with_atlas = [cid for cid in CONTROL_IDS if has_atlas(cid)]
    sample = select_recompute_sample([cid for cid in part_ids if has_atlas(cid)], run_id)
    recompute_ids = controls_with_atlas + sample
    mismatches: List[str] = []
    if read_png is None:
        mismatches.append("no PNG reader: the recompute did not run")
    else:
        for cid in recompute_ids:
            sidecar = sweep.sidecars[cid]
            try:
                atlas = read_png(atlas_path(sidecar["atlas"]["file"]))
                diffs = recompute_case(sidecar, atlas)
            except Exception as exc:  # noqa: BLE001 - an unreadable atlas is the finding
                diffs = ["atlas unreadable (%s: %s)" % (type(exc).__name__, exc)]
            mismatches.extend("%s %s" % (cid, d) for d in diffs)
    # The skin cross-check: the C# runner's skin flags against the ones derived here,
    # over every part case (a control builds its ghost, or none, by its own rule).
    for cid in part_ids:
        why = skin_flag_disagreement(sweep.sidecars.get(cid))
        if why:
            mismatches.append("%s %s" % (cid, why))
    if not recompute_ids and read_png is not None:
        mismatches.append("nothing to recompute: no control or part case has an atlas")

    counts = _count(cases)
    if not controls.ok:
        status, subkind = STATUS_INVALID, INVALID_CONTROLS_SUBKIND
        problems.append("controls: " + ", ".join(controls.problems))
    elif mismatches:
        status, subkind = STATUS_INVALID, INVALID_INSTRUMENT_SUBKIND
    elif not completeness.ok:
        status, subkind = STATUS_INVALID, INVALID_INCOMPLETE_SUBKIND
    else:
        subkind = None
        if gating:
            status = STATUS_FAIL if counts[VERDICT_FAIL] else STATUS_PASS
        else:
            status = STATUS_REPORT
    if mismatches:
        problems.append("recompute: %d mismatch(es): %s"
                        % (len(mismatches), "; ".join(mismatches[:5])
                           + (" ..." if len(mismatches) > 5 else "")))
    if not completeness.ok:
        problems.append("completeness: " + "; ".join(completeness.problems))
    return SweepResult(sweep.label, status, subkind, gating, problems, controls,
                       completeness, cases, recompute_ids, mismatches, counts)


_SUBKIND_ORDER = {INVALID_CONTROLS_SUBKIND: 0, INVALID_INSTRUMENT_SUBKIND: 1,
                  INVALID_INCOMPLETE_SUBKIND: 2}


@dataclass(frozen=True)
class RowResult:
    status: str
    invalid_subkind: Optional[str]
    gating: bool
    sweeps: Tuple[SweepResult, ...]

    @property
    def part_fail(self) -> bool:
        return any(s.counts.get(VERDICT_FAIL, 0) for s in self.sweeps)


def combine(sweeps: Sequence[SweepResult], gating: bool) -> RowResult:
    """The row over every label: INVALID when any sweep is (the first subkind in
    controls > instrument > incomplete order), else FAIL / PASS when armed, else
    REPORT."""
    invalid = [s for s in sweeps if s.status == STATUS_INVALID]
    if invalid:
        sub = min((s.invalid_subkind for s in invalid),
                  key=lambda k: _SUBKIND_ORDER.get(k or "", 99))
        return RowResult(STATUS_INVALID, sub, gating, tuple(sweeps))
    if gating:
        status = STATUS_FAIL if any(s.status == STATUS_FAIL for s in sweeps) else STATUS_PASS
    else:
        status = STATUS_REPORT
    return RowResult(status, None, gating, tuple(sweeps))


def format_summary(row: RowResult) -> str:
    """The one-line summary the verifier logs."""
    parts = []
    for s in row.sweeps:
        worst = ",".join("%s(%s:%s)" % (c.case_id, c.verdict,
                                        c.reasons[0] if c.reasons else "-")
                         for c in s.worst())
        parts.append("label=%s status=%s cases=%d pass=%d warn=%d fail=%d error=%d "
                     "controls=%s complete=%s recompute=%d mismatches=%d worst=[%s]"
                     % (s.label, s.status, s.counts["cases"], s.counts[VERDICT_PASS],
                        s.counts[VERDICT_WARN], s.counts[VERDICT_FAIL],
                        s.counts[VERDICT_ERROR],
                        "ok" if s.controls is not None and s.controls.ok
                        else ("-" if s.controls is None else "bad"),
                        "-" if s.completeness is None else str(s.completeness.ok).lower(),
                        len(s.recompute_ids), len(s.recompute_mismatches), worst or "-"))
    return "status=%s gating=%s subkind=%s %s" % (
        row.status, row.gating, row.invalid_subkind or "-", " | ".join(parts) or "no sweeps")


def thresholds() -> Dict[str, Any]:
    return {"shapeBlobFailPct": SHAPE_BLOB_FAIL_PCT, "shapeBlobFailPx": SHAPE_BLOB_FAIL_PX,
            "colorBlobFailPx": COLOR_BLOB_FAIL_PX, "warnFraction": WARN_FRACTION,
            "recomputeSampleFraction": RECOMPUTE_SAMPLE_FRACTION}


def verdicts_document(sweep: SweepResult, run_id: str) -> Dict[str, Any]:
    """``partparity-verdicts.json`` for one label."""
    c = sweep.controls
    k = sweep.completeness
    return {
        "schema": VERDICTS_SCHEMA,
        "label": sweep.label,
        "runId": run_id,
        "status": sweep.status,
        "invalidSubkind": sweep.invalid_subkind,
        "gating": sweep.gating,
        "thresholds": thresholds(),
        "counts": dict(sweep.counts),
        "problems": list(sweep.problems),
        "controls": None if c is None else {
            "ok": c.ok, "state": c.state, "outcomes": dict(c.outcomes),
            "problems": list(c.problems)},
        "completeness": None if k is None else {
            "ok": k.ok, "planned": k.planned, "complete": k.complete,
            "setupFaults": k.setup_faults, "problems": list(k.problems)},
        "recompute": {"cases": list(sweep.recompute_ids),
                      "mismatches": list(sweep.recompute_mismatches)},
        "worst": [w.case_id for w in sweep.worst()],
        "cases": [cv.to_dict() for cv in sweep.cases],
    }


def detail_record(row: RowResult) -> Dict[str, Any]:
    """The result JSON's ``detail.partParity`` (compact: the per-case list stays in
    the verdicts file)."""
    return {
        "status": row.status, "subkind": row.invalid_subkind, "gating": row.gating,
        "sweeps": [{"label": s.label, "status": s.status, "subkind": s.invalid_subkind,
                    "counts": dict(s.counts), "problems": list(s.problems),
                    "recomputed": len(s.recompute_ids),
                    "recomputeMismatches": len(s.recompute_mismatches),
                    "worst": [{"caseId": w.case_id, "verdict": w.verdict,
                               "reasons": list(w.reasons[:3])} for w in s.worst()]}
                   for s in row.sweeps],
    }
