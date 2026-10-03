"""Pure recording-sampling quality measures (the ``[expectations.recordings.sampling]``
block of the saveParse verifier row).

WHAT IT MEASURES. The active-vessel recorder (``FlightRecorder.OnPhysicsFrame``) takes
a sample on a physics frame when ``TrajectoryMath.ShouldRecordPoint`` says so: always
once ``elapsed >= maxInterval``, never while ``elapsed < minInterval``, and in between
when the velocity direction turned more than ``velocityDirThreshold`` degrees or the
speed changed by more than ``speedChangeThreshold`` percent since the last sample.
``elapsed`` is GAME time (``Planetarium.GetUniversalTime()``). The four numbers come
from the install-wide ``samplingDensity`` preset (``ParsekSettings.cs``:
``GetMinSampleInterval`` / ``GetMaxSampleInterval`` / ``GetVelocityDirThreshold`` /
``GetSpeedChangeThreshold``), mirrored in ``DENSITY_PRESETS`` below and kept in step by
``test_samplingq.DensityPresetSourceSyncTests``, which reads ``ParsekSettings.cs``.

PHYSICS WARP. KSP's physics warp (TimeWarp.Modes.LOW, 2x-4x) sets
``Time.fixedDeltaTime = 0.02 * rate`` (decompiled ``TimeWarp.updateRate``): every
physics frame still runs, but each advances game time by ``0.02 * rate`` seconds. So a
recorder that spaces by game time keeps its bounds under physics warp up to a coarser
frame quantum, and that is exactly what this module holds it to: the same min / max /
threshold contract as at 1x, with a ONE-FRAME allowance of ``0.02 * rate``. The rate of
each frame comes from the recorder's own section-close line (``warpRuns=`` on
``TrackSection closed:``, ``Source/Parsek/SectionWarpRuns.cs``), parsed by
``parse_warp_runs``.

Measured per adjacent sample pair inside one ACTIVE, ABSOLUTE track section (the
focused vessel's per-frame physics sections; background and relative sections run
proximity cadences and anchor-local coordinates, so they are out of scope):

  - ``overMax``   gap > maxInterval + one frame (the backstop failed);
  - ``subMin``    0 < gap < minInterval (a forced or high-fidelity sample; reported,
                  and the wrong-density tell: a denser preset than declared leaves
                  many);
  - ``duplicates`` gap == 0 inside a section (section boundaries share a seed point,
                  so equality ACROSS sections is legitimate and not counted);
  - ``backsteps`` gap < 0;
  - ``dirMisses`` / ``speedMisses`` the turn (or speed change) already exceeded the
                  threshold one frame BEFORE the later sample was taken, estimated
                  linearly over the gap - i.e. the trigger should have fired earlier;
  - ``jumps``     the chord between the two positions exceeds what the faster of the
                  two speeds (plus the body's surface rotation, since positions are
                  body-fixed) could cover in the gap - a teleport;
  - ``triggered`` gaps that closed before the max backstop (a trigger fired).

Each pair is classified ``1x`` / ``phys`` / ``mixed`` / ``unknown`` by the warp runs.

Side-effect-free; stdlib only; ASCII only. ``run.py`` owns the I/O.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass, field
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

import saveparse

# ---------------------------------------------------------------------------
# Constants.
# ---------------------------------------------------------------------------

SAMPLING_BLOCK = "sampling"  # nested under [expectations.recordings]

# KSP's base physics frame (TimeWarp.updateRate: fixedDeltaTime = 0.02 * timeScale).
PHYSICS_FRAME_SECONDS = 0.02

# (minInterval s, maxInterval s, velocityDirThreshold deg, speedChangeThreshold %),
# mirrored from ParsekSettings.cs (source-synced by test_samplingq).
DENSITY_PRESETS: Dict[str, Tuple[float, float, float, float]] = {
    "low": (0.5, 8.0, 6.0, 12.0),
    "medium": (0.2, 3.0, 2.0, 5.0),
    "high": (0.05, 1.0, 0.5, 1.0),
}

CLASS_NORMAL = "1x"
CLASS_PHYS = "phys"
CLASS_MIXED = "mixed"
CLASS_UNKNOWN = "unknown"
PAIR_CLASSES: Tuple[str, ...] = (CLASS_NORMAL, CLASS_PHYS, CLASS_MIXED, CLASS_UNKNOWN)

# Trigger-miss tolerance: the linear estimate of the change one frame before the
# later sample may exceed the threshold by this fraction (plus a small absolute
# floor) before it counts as a miss. Real ascents turn non-uniformly inside a gap.
MISS_RELATIVE_TOLERANCE = 0.25
DIR_MISS_ABS_TOLERANCE_DEG = 0.05
SPEED_MISS_ABS_TOLERANCE = 0.002
# Jump tolerance on the chord bound.
JUMP_RELATIVE_TOLERANCE = 0.05
JUMP_ABS_TOLERANCE_M = 1.0
# The recorder ignores direction below this speed (TrajectoryMath.ShouldRecordPoint).
DIRECTION_MIN_SPEED = 0.1

EPS = 1e-6

# Stock body (radius m, sidereal rotation period s). Only the rotation-speed slack of
# the jump bound uses these; a body not listed skips the jump measure for its pairs
# (counted as `jumpUnmeasured`).
BODY_RADIUS_AND_ROTATION: Dict[str, Tuple[float, float]] = {
    "Sun": (261600000.0, 432000.0),
    "Moho": (250000.0, 1210000.0),
    "Eve": (700000.0, 80500.0),
    "Gilly": (13000.0, 28255.0),
    "Kerbin": (600000.0, 21549.425),
    "Mun": (200000.0, 138984.38),
    "Minmus": (60000.0, 40400.0),
    "Duna": (320000.0, 65517.859),
    "Ike": (130000.0, 65517.862),
    "Dres": (138000.0, 34800.0),
    "Jool": (6000000.0, 36000.0),
    "Laythe": (500000.0, 52980.879),
    "Vall": (300000.0, 105962.09),
    "Tylo": (600000.0, 211926.36),
    "Bop": (65000.0, 544507.43),
    "Pol": (44000.0, 901902.62),
    "Eeloo": (210000.0, 19460.0),
}

# ---------------------------------------------------------------------------
# Parsing: the readable sidecar mirror (.prec.txt) and the warp-run log token.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class SamplePoint:
    ut: float
    lat: float
    lon: float
    alt: float
    body: str
    vel: Tuple[float, float, float]


@dataclass(frozen=True)
class SectionSamples:
    recording_id: str
    index: int
    env: str
    ref: str
    source: str  # "" when the key is absent (Active, the codec's default)
    start_ut: float
    end_ut: float
    points: Tuple[SamplePoint, ...]

    @property
    def in_scope(self) -> bool:
        """Active (source absent or 0) Absolute (ref 0) per-frame section."""
        return self.source in ("", "0") and self.ref == "0" and len(self.points) >= 2


@dataclass(frozen=True)
class PrecParse:
    recording_id: str
    sections: Tuple[SectionSamples, ...]
    error: str = ""


def _f(raw: Optional[str]) -> Optional[float]:
    if raw is None:
        return None
    try:
        v = float(raw)
    except ValueError:
        return None
    return v if math.isfinite(v) else None


def parse_prec_text(text: Optional[str], recording_id: str = "") -> PrecParse:
    """Parse one ``.prec.txt`` readable mirror into its track sections and their
    POINT samples. Never raises; a ConfigNode fault lands in ``error``."""
    res = saveparse.parse_sfs(text)
    root = res.root
    rid = root.value("recordingId") or recording_id
    sections: List[SectionSamples] = []
    for idx, sec in enumerate(root.nodes_named("TRACK_SECTION")):
        pts: List[SamplePoint] = []
        for p in sec.nodes_named("POINT"):
            ut = _f(p.value("ut"))
            lat, lon, alt = _f(p.value("lat")), _f(p.value("lon")), _f(p.value("alt"))
            vx, vy, vz = _f(p.value("velX")), _f(p.value("velY")), _f(p.value("velZ"))
            if ut is None:
                # A NaN / missing UT is an INV1 analyzer finding; keep it visible
                # here as a backstep-shaped hole rather than dropping it silently.
                ut = float("nan")
            pts.append(SamplePoint(
                ut=ut, lat=lat if lat is not None else float("nan"),
                lon=lon if lon is not None else float("nan"),
                alt=alt if alt is not None else float("nan"),
                body=p.value("body") or "",
                vel=(vx or 0.0, vy or 0.0, vz or 0.0)))
        sections.append(SectionSamples(
            recording_id=rid, index=idx,
            env=sec.value("env") or "", ref=sec.value("ref") or "",
            source=sec.value("src") or "",
            start_ut=_f(sec.value("startUT")) or 0.0,
            end_ut=_f(sec.value("endUT")) or 0.0,
            points=tuple(pts)))
    return PrecParse(recording_id=rid, sections=tuple(sections), error=res.error)


@dataclass(frozen=True)
class WarpRun:
    kind: str       # "1x" / "phys" / "rails"
    count: int
    first_ut: float
    last_ut: float
    max_rate: float  # physics-warp runs only; 1.0 otherwise


# `[Parsek][INFO][Recorder] TrackSection closed: ... warpRuns=<runs>` - anchored on the
# ACTIVE recorder's tag so a background close line (tag BgRecorder) or a quoted echo
# never counts.
_CLOSE_RE = re.compile(r"\[Parsek\]\[INFO\]\[Recorder\] TrackSection closed: .* warpRuns=(?P<runs>\S+)")
_RUN_RE = re.compile(
    r"^(?P<kind>1x|phys|rails):(?P<n>\d+):(?P<a>-?[0-9.]+)-(?P<b>-?[0-9.]+)(?::max=(?P<max>[0-9.]+))?$")


def parse_warp_runs(log_text: Optional[str]) -> Tuple[Tuple[WarpRun, ...], int]:
    """Every ``warpRuns=`` run on an active-recorder section-close line, plus the
    count of close lines that carried the token. A DLL without the token yields
    ``((), 0)`` - the caller turns that into a defined mismatch."""
    runs: List[WarpRun] = []
    lines = 0
    for line in (log_text or "").splitlines():
        if "TrackSection closed:" not in line or "warpRuns=" not in line:
            continue
        m = _CLOSE_RE.search(line)
        if not m:
            continue
        lines += 1
        token = m.group("runs")
        if token == "-":
            continue
        for part in token.split(","):
            rm = _RUN_RE.match(part)
            if not rm:
                continue
            mx = rm.group("max")
            runs.append(WarpRun(kind=rm.group("kind"), count=int(rm.group("n")),
                                first_ut=float(rm.group("a")),
                                last_ut=float(rm.group("b")),
                                max_rate=float(mx) if mx else 1.0))
    return tuple(runs), lines


# The log formats run bounds with F3, so a sample UT matches a bound to within half a
# millisecond.
_RUN_BOUND_SLACK = 0.0006


def classify_ut(ut: float, runs: Sequence[WarpRun]) -> Tuple[str, float]:
    """(kind, rate) of the run containing ``ut``. A UT inside both a physics run
    and a 1x run (a boundary seed shared by two sections) reads as physics, the
    conservative side for the one-frame allowance. ("unknown", 1.0) when none."""
    best: Optional[Tuple[str, float]] = None
    for r in runs:
        if r.first_ut - _RUN_BOUND_SLACK <= ut <= r.last_ut + _RUN_BOUND_SLACK:
            if r.kind == "phys":
                if best is None or best[0] != "phys" or r.max_rate > best[1]:
                    best = ("phys", r.max_rate)
            elif best is None:
                best = (r.kind, 1.0)
    return best if best is not None else ("unknown", 1.0)


def classify_pair(a_ut: float, b_ut: float, runs: Sequence[WarpRun]) -> Tuple[str, float]:
    ka, ra = classify_ut(a_ut, runs)
    kb, rb = classify_ut(b_ut, runs)
    rate = max(ra, rb)
    if ka == "phys" and kb == "phys":
        return CLASS_PHYS, rate
    if ka == "1x" and kb == "1x":
        return CLASS_NORMAL, 1.0
    if "unknown" in (ka, kb) or "rails" in (ka, kb):
        return CLASS_UNKNOWN, rate
    return CLASS_MIXED, rate


# ---------------------------------------------------------------------------
# Measures.
# ---------------------------------------------------------------------------


def _norm(v: Sequence[float]) -> float:
    return math.sqrt(sum(c * c for c in v))


def _angle_deg(a: Sequence[float], b: Sequence[float]) -> float:
    na, nb = _norm(a), _norm(b)
    if na <= 0.0 or nb <= 0.0:
        return 0.0
    c = sum(x * y for x, y in zip(a, b)) / (na * nb)
    return math.degrees(math.acos(max(-1.0, min(1.0, c))))


def _body_fixed_xyz(p: SamplePoint, radius: float) -> Tuple[float, float, float]:
    r = radius + p.alt
    la, lo = math.radians(p.lat), math.radians(p.lon)
    return (r * math.cos(la) * math.cos(lo), r * math.cos(la) * math.sin(lo), r * math.sin(la))


def _new_class_row() -> Dict[str, Any]:
    return {"gaps": 0, "overMax": 0, "subMin": 0, "duplicates": 0, "backsteps": 0,
            "dirMisses": 0, "speedMisses": 0, "jumps": 0, "jumpUnmeasured": 0,
            "triggered": 0, "maxGap": 0.0, "sumGap": 0.0, "maxJumpRatio": 0.0,
            "maxRate": 1.0, "_gapList": []}


@dataclass
class PairFinding:
    recording_id: str
    section: int
    ut: float
    kind: str
    detail: str


def measure_sections(sections: Iterable[SectionSamples], density: str,
                     runs: Sequence[WarpRun]) -> Tuple[Dict[str, Any], List[PairFinding]]:
    """The per-class facet rows over every in-scope section, plus the first few
    offending pairs per measure (for the report). ``density`` must be a key of
    ``DENSITY_PRESETS``."""
    mn, mx, dir_deg, spd_pct = DENSITY_PRESETS[density]
    spd = spd_pct / 100.0
    rows: Dict[str, Dict[str, Any]] = {c: _new_class_row() for c in PAIR_CLASSES}
    findings: List[PairFinding] = []
    sections_in_scope = 0

    def note(kind: str, sec: SectionSamples, ut: float, detail: str) -> None:
        if sum(1 for f in findings if f.kind == kind) < 5:
            findings.append(PairFinding(sec.recording_id, sec.index, ut, kind, detail))

    for sec in sections:
        if not sec.in_scope:
            continue
        sections_in_scope += 1
        pts = sec.points
        for i in range(1, len(pts)):
            a, b = pts[i - 1], pts[i]
            cls, rate = classify_pair(a.ut, b.ut, runs)
            row = rows[cls]
            if rate > row["maxRate"]:
                row["maxRate"] = rate
            frame = PHYSICS_FRAME_SECONDS * rate
            g = b.ut - a.ut
            if math.isnan(g) or g < 0.0:
                row["backsteps"] += 1
                note("backstep", sec, b.ut, "ut %r -> %r" % (a.ut, b.ut))
                continue
            if g <= EPS:
                row["duplicates"] += 1
                note("duplicate", sec, b.ut, "repeated ut %r" % (b.ut,))
                continue
            row["gaps"] += 1
            row["sumGap"] += g
            row["_gapList"].append(g)
            if g > row["maxGap"]:
                row["maxGap"] = g
            if g > mx + frame + EPS:
                row["overMax"] += 1
                note("overMax", sec, b.ut, "gap %.3f > max %.3f + frame %.3f" % (g, mx, frame))
            if g < mn - EPS:
                row["subMin"] += 1
            if g < mx - frame - EPS:
                row["triggered"] += 1
            # Trigger misses: the change one frame before b, estimated linearly.
            # The frame before b only counts as trigger-eligible when it sat
            # clearly past the min floor: the recorder compares a double elapsed
            # against the float minInterval (0.2f > 0.2), so a floor-limited
            # sample routinely lands at min + one frame.
            eligible_before = g - frame
            if eligible_before > mn + 0.5 * frame and g > 0.0:
                frac = eligible_before / g
                sa, sb = _norm(a.vel), _norm(b.vel)
                if sa > DIRECTION_MIN_SPEED and sb > DIRECTION_MIN_SPEED:
                    ang = _angle_deg(a.vel, b.vel) * frac
                    if ang > dir_deg * (1.0 + MISS_RELATIVE_TOLERANCE) + DIR_MISS_ABS_TOLERANCE_DEG:
                        row["dirMisses"] += 1
                        note("dirMiss", sec, b.ut, "turn %.2f deg before the sample (threshold %.2f)"
                             % (ang, dir_deg))
                rel = abs(sb - sa) / max(sa, DIRECTION_MIN_SPEED) * frac
                if rel > spd * (1.0 + MISS_RELATIVE_TOLERANCE) + SPEED_MISS_ABS_TOLERANCE:
                    row["speedMisses"] += 1
                    note("speedMiss", sec, b.ut, "speed change %.2f%% before the sample (threshold %.2f%%)"
                         % (rel * 100.0, spd_pct))
            # Continuity.
            body = BODY_RADIUS_AND_ROTATION.get(a.body) if a.body == b.body else None
            coords_ok = all(math.isfinite(x) for x in (a.lat, a.lon, a.alt, b.lat, b.lon, b.alt))
            if body is None or not coords_ok:
                row["jumpUnmeasured"] += 1
            else:
                radius, period = body
                pa, pb = _body_fixed_xyz(a, radius), _body_fixed_xyz(b, radius)
                chord = _norm([x - y for x, y in zip(pa, pb)])
                surf = 2.0 * math.pi * (radius + max(a.alt, b.alt, 0.0)) / period
                reach = (max(_norm(a.vel), _norm(b.vel)) + surf) * g
                ratio = chord / reach if reach > 0.0 else (0.0 if chord <= JUMP_ABS_TOLERANCE_M else float("inf"))
                if ratio > row["maxJumpRatio"]:
                    row["maxJumpRatio"] = ratio
                if chord > reach * (1.0 + JUMP_RELATIVE_TOLERANCE) + JUMP_ABS_TOLERANCE_M:
                    row["jumps"] += 1
                    note("jump", sec, b.ut, "chord %.1f m > reach %.1f m in %.3f s" % (chord, reach, g))

    out: Dict[str, Any] = {"density": density, "sectionsInScope": sections_in_scope,
                           "bounds": {"minInterval": mn, "maxInterval": mx,
                                      "velocityDirThresholdDeg": dir_deg,
                                      "speedChangeThresholdPct": spd_pct}}
    for cls, row in rows.items():
        gaps = sorted(row.pop("_gapList"))
        row["meanGap"] = round(row["sumGap"] / row["gaps"], 4) if row["gaps"] else 0.0
        row["p50Gap"] = round(gaps[len(gaps) // 2], 4) if gaps else 0.0
        row["minGap"] = round(gaps[0], 4) if gaps else 0.0
        row["maxGap"] = round(row["maxGap"], 4)
        row["maxJumpRatio"] = round(row["maxJumpRatio"], 4)
        row.pop("sumGap")
        out[cls] = row
    return out, findings


# Totals that the assertion keys read (summed over every pair class).
TOTAL_KEYS: Tuple[str, ...] = (
    "gaps", "overMax", "subMin", "duplicates", "backsteps", "dirMisses", "speedMisses",
    "jumps", "triggered")


def totals(facets: Dict[str, Any]) -> Dict[str, int]:
    return {k: sum(int(facets.get(c, {}).get(k, 0)) for c in PAIR_CLASSES) for k in TOTAL_KEYS}


@dataclass(frozen=True)
class SamplingInput:
    """What run.py hands the evaluator: every readable sidecar mirror of the
    produced save (``{recordingId: text}``), the collected KSP.log text, and how
    many ``.prec`` sidecars carried no ``.prec.txt`` mirror (an unmeasured
    recording is a defined mismatch, never a silent skip)."""

    prec_texts: Dict[str, str] = field(default_factory=dict)
    log_text: str = ""
    missing_mirrors: int = 0


def observed_sampling_facets(block: Optional[Dict], data: Optional[SamplingInput]) -> Dict[str, Any]:
    """Facets for a declared block (``{}`` when no block or no density)."""
    if not isinstance(block, dict):
        return {}
    density = block.get(DENSITY_KEY)
    if not isinstance(density, str) or density not in DENSITY_PRESETS:
        return {}
    data = data or SamplingInput()
    runs, close_lines = parse_warp_runs(data.log_text)
    sections: List[SectionSamples] = []
    parse_errors = 0
    for rid in sorted(data.prec_texts):
        parsed = parse_prec_text(data.prec_texts[rid], rid)
        if parsed.error:
            parse_errors += 1
        sections.extend(parsed.sections)
    facets, findings = measure_sections(sections, density, runs)
    facets["recordings"] = len(data.prec_texts)
    facets["missingMirrors"] = data.missing_mirrors
    facets["parseErrors"] = parse_errors
    facets["warpCloseLines"] = close_lines
    facets["physRuns"] = sum(1 for r in runs if r.kind == "phys")
    facets["physRunMaxRate"] = round(max([r.max_rate for r in runs if r.kind == "phys"] or [1.0]), 3)
    facets["totals"] = totals(facets)
    facets["findings"] = ["%s %s#%d ut=%.3f %s" % (f.kind, f.recording_id[:8], f.section, f.ut, f.detail)
                          for f in findings]
    return facets


# ---------------------------------------------------------------------------
# Spec surface.
# ---------------------------------------------------------------------------

DENSITY_KEY = "density"
# Windows over the totals (every pair class).
SAMPLING_TOTAL_WINDOW_KEYS: Tuple[str, ...] = (
    "overMax", "subMin", "duplicates", "backsteps", "dirMisses", "speedMisses", "jumps")
# Windows over the physics-warp class alone (the anti-vacuity floors).
SAMPLING_PHYS_WINDOW_KEYS: Tuple[str, ...] = ("physGaps", "physTriggered")
# Windows over the pairs that touch physics warp (the `phys` and `mixed` classes).
# `warpDuplicates` exists because duplicate sample UTs are a PRE-EXISTING 1x recorder
# defect (todo SECTION-DUPLICATE-UT-SAMPLES: 1524 in-section duplicates over 542
# archived saves, at staging / off-rails boundaries), so a lane whose subject is
# physics warp pins the warp-touching duplicates and reports the total.
SAMPLING_WARP_WINDOW_KEYS: Tuple[str, ...] = ("warpDuplicates",)
SAMPLING_ASSERTION_KEYS: Tuple[str, ...] = (
    SAMPLING_TOTAL_WINDOW_KEYS + SAMPLING_PHYS_WINDOW_KEYS + SAMPLING_WARP_WINDOW_KEYS)
SAMPLING_BLOCK_KEYS: Tuple[str, ...] = (saveparse.GATING_KEY, DENSITY_KEY) + SAMPLING_ASSERTION_KEYS


def measured_value(facets: Dict[str, Any], key: str) -> int:
    if key == "physGaps":
        return int(facets.get(CLASS_PHYS, {}).get("gaps", 0))
    if key == "physTriggered":
        return int(facets.get(CLASS_PHYS, {}).get("triggered", 0))
    if key == "warpDuplicates":
        return sum(int(facets.get(c, {}).get("duplicates", 0)) for c in (CLASS_PHYS, CLASS_MIXED))
    return int(facets.get("totals", {}).get(key, 0))


def validate_sampling_expectations(block: Any) -> List[str]:
    """Validate ``[expectations.recordings.sampling]``. None => valid (absent)."""
    if block is None:
        return []
    prefix = "expectations.recordings.sampling"
    if not isinstance(block, dict):
        return ["%s: must be a table" % prefix]
    errs: List[str] = []
    unknown = sorted(k for k in block if k not in SAMPLING_BLOCK_KEYS)
    if unknown:
        errs.append("%s: unknown key(s) %s (accepted: %s)" % (prefix, unknown, list(SAMPLING_BLOCK_KEYS)))
    density = block.get(DENSITY_KEY)
    if not isinstance(density, str) or density not in DENSITY_PRESETS:
        errs.append("%s.%s: %r must be one of %s (the samplingDensity preset the run "
                    "flew; the bounds are derived from it)" % (prefix, DENSITY_KEY, density,
                                                                sorted(DENSITY_PRESETS)))
    errs.extend(saveparse._validate_gating(prefix, block))
    errs.extend(saveparse._validate_armed_empty(prefix, block, SAMPLING_ASSERTION_KEYS))
    errs.extend(saveparse._validate_armed_unreddable(prefix, block, SAMPLING_ASSERTION_KEYS))
    for key in SAMPLING_ASSERTION_KEYS:
        if key in block:
            errs.extend(saveparse._validate_window("%s.%s" % (prefix, key), block[key]))
    return errs


def evaluate_sampling(block: Dict, facets: Dict[str, Any]) -> List[str]:
    """Mismatches for a declared block over measured facets. Defined mismatches
    (whether or not a window is declared): no facets (bad density), a sidecar with
    no readable mirror, a mirror that did not parse, no in-scope section at all,
    and - when a physics-class window is declared - a log with no ``warpRuns=``
    token (a DLL that predates the per-frame warp classification)."""
    out: List[str] = []
    if not facets:
        out.append("recordings.sampling: not measured (density %r is not a preset)"
                   % (block.get(DENSITY_KEY),))
        return out
    if facets.get("missingMirrors"):
        out.append("recordings.sampling: %d recording sidecar(s) carry no .prec.txt "
                   "mirror - the sampling is not fully measured" % facets["missingMirrors"])
    if facets.get("parseErrors"):
        out.append("recordings.sampling: %d .prec.txt mirror(s) did not parse"
                   % facets["parseErrors"])
    if not facets.get("sectionsInScope"):
        out.append("recordings.sampling: no active absolute per-frame section to measure")
    if any(k in block for k in SAMPLING_PHYS_WINDOW_KEYS + SAMPLING_WARP_WINDOW_KEYS)             and not facets.get("warpCloseLines"):
        out.append("recordings.sampling: no `warpRuns=` token on any recorder section-close "
                   "line - the deployed DLL predates the per-frame warp classification")
    for key in SAMPLING_ASSERTION_KEYS:
        if key in block:
            saveparse._check_window("recordings.sampling.%s" % key, block[key],
                                    measured_value(facets, key), out)
    return out
