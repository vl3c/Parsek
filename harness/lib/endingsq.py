"""Pure recording-ENDING measures (the ``[expectations.recordings.endings]`` block of
the saveParse verifier row).

WHAT IT MEASURES. How each named vessel's recording ENDED: its terminal state, how
many PREDICTED orbit segments its chain carries, how long the recording runs past its
last real (physics or background) sample, and the altitude its last predicted segment
ends at. Those four numbers are the whole claim of an "ignored stage falls to the
ground" lane: a stage Parsek stopped watching mid-air ends ``Destroyed`` at a predicted
ground impact, so its chain carries ``isPredicted = True`` segments, its end UT lies
well past the moment Parsek stopped sampling it, and the predicted arc ends at the
ground rather than at the close altitude or at the atmosphere top.

"WATCHED". The tail is measured from the last datum Parsek RECORDED rather than
predicted: the latest real POINT sample, or the end of a NON-predicted orbit segment
(an on-rails background vessel is watched through its orbit segments, not through
samples, until the commit closes them). So the tail reads how far the ending lies past
the moment Parsek stopped watching the stage.

WHERE THE NUMBERS COME FROM. Only what the produced save holds, never a log line:

  - the ``.sfs`` RECORDING node (``RecordingTreeRecordCodec``): ``vesselName``,
    ``terminalState`` (absent = the recording has no ending: a chain piece before
    the tip), ``explicitEndUT`` (written only when set), ``chainId``, and the sparse
    ending flags ``terminalPredicted`` / ``quietEnd`` (written only when true) and
    ``tOrbBody`` (the terminal orbit's body, written only with a terminal orbit);
  - the readable sidecar mirror ``<id>.prec.txt`` (``TrajectoryTextSidecarCodec``,
    pinned ON for every harness run by ``hlib.PINNED_ON_SETTING_KEYS``): ``POINT``
    samples (``ut``) inside ``TRACK_SECTION`` nodes (``ref`` 0 Absolute / 1 Relative
    are real samples; 2 OrbitalCheckpoint frames are densified from orbit elements
    and are not), ``ORBIT_SEGMENT`` nodes (``startUT`` / ``endUT`` / ``sma`` / ``ecc``
    / ``mna`` radians / ``epoch`` / ``body`` / ``isPredicted``) at the top level or
    inside an OrbitalCheckpoint section. A flat write and a section write can carry
    the SAME segment twice, so segments are de-duplicated by (startUT, endUT, body).

PER CHAIN, NOT PER ROW. The optimizer splits one vessel's recording into chain pieces
that share a ``chainId`` (an atmosphere exit, a re-entry). Only the TIP carries the
terminal, and a re-entry tip can hold nothing but predicted segments. So every facet
of an ended recording is measured over its whole chain: the last real sample is the
latest one in any piece, the predicted segments are those of every piece, and the
altitude is read off the chain's latest-ending predicted segment. A recording with no
``chainId`` is its own chain.

THE ALTITUDE. Two-body position from the segment's own elements (KSP-native: ``mna``
in radians at ``epoch``), radius at the segment's ``endUT``, minus the body radius.
It is drag-free by construction, which is the owner's ruling for the predicted fall:
a prediction that stops at the ground reads a few metres to a few kilometres (terrain),
one that stops at the close altitude or at 70 km reads tens of kilometres.

Side-effect-free; stdlib only; ASCII only. ``run.py`` owns the I/O (it hands over the
same ``samplingq.SamplingInput`` the sampling block reads).
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Sequence, Tuple

import saveparse

ENDINGS_BLOCK = "endings"  # nested under [expectations.recordings]
VESSELS_KEY = "vessels"
ENDINGS_BLOCK_KEYS: Tuple[str, ...] = (saveparse.GATING_KEY, VESSELS_KEY)

# Per-vessel assertion keys.
ENDED_KEY = "ended"
TERMINALS_KEY = "terminalStates"
PREDICTED_KEY = "predictedSegments"
TAIL_KEY = "tailSeconds"
ALTITUDE_KEY = "endAltitudeMeters"
# Per-recording flags of the ending (each a bool every ended recording must match) and the
# terminal orbit's body (a non-empty list of accepted names; no terminal orbit never passes).
PREDICTED_FLAG_KEY = "terminalPredicted"
QUIET_END_KEY = "quietEnd"
ORBIT_BODIES_KEY = "terminalOrbitBodies"
BOOL_FLAG_KEYS: Tuple[str, ...] = (PREDICTED_FLAG_KEY, QUIET_END_KEY)
VESSEL_ASSERTION_KEYS: Tuple[str, ...] = (
    ENDED_KEY, TERMINALS_KEY, PREDICTED_KEY, TAIL_KEY, ALTITUDE_KEY,
    PREDICTED_FLAG_KEY, QUIET_END_KEY, ORBIT_BODIES_KEY)
# Windows that apply to EVERY ended recording of the vessel (not to the count).
PER_RECORDING_WINDOW_KEYS: Tuple[str, ...] = (PREDICTED_KEY, TAIL_KEY, ALTITUDE_KEY)

# Reference frames whose POINT samples are real (TrackSection.ReferenceFrame).
REAL_SAMPLE_REFS: Tuple[str, ...] = ("0", "1")

# TrackSectionSource.Predicted (Source/Parsek/Trajectory/TrackSection.cs): a section Parsek
# predicted past the last real sample (a chute descent to touchdown). Its POINTs are never
# real samples, whatever its frame. The text codec writes `src` only when not Active.
PREDICTED_SECTION_SRC = "3"

# Stock body (gravitational parameter m^3/s^2, radius m), KSP 1.12.5. A body not
# listed leaves the altitude unmeasured (a defined mismatch when it is asserted).
BODY_GM_AND_RADIUS: Dict[str, Tuple[float, float]] = {
    "Sun": (1.1723328e18, 261600000.0),
    "Moho": (1.6860938e11, 250000.0),
    "Eve": (8.1717302e12, 700000.0),
    "Gilly": (8289449.8, 13000.0),
    "Kerbin": (3.5316e12, 600000.0),
    "Mun": (6.5138398e10, 200000.0),
    "Minmus": (1.7658e9, 60000.0),
    "Duna": (3.0136321e11, 320000.0),
    "Ike": (1.8568369e10, 130000.0),
    "Dres": (2.1484489e10, 138000.0),
    "Jool": (2.82528e14, 6000000.0),
    "Laythe": (1.962e12, 500000.0),
    "Vall": (2.074815e11, 300000.0),
    "Tylo": (2.82528e12, 600000.0),
    "Bop": (2.4868349e9, 65000.0),
    "Pol": (7.2170208e8, 44000.0),
    "Eeloo": (7.4410815e10, 210000.0),
}


# ---------------------------------------------------------------------------
# Parsing the readable mirror.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class OrbitSeg:
    start_ut: float
    end_ut: float
    sma: float
    ecc: float
    mna: float
    epoch: float
    body: str
    predicted: bool


@dataclass(frozen=True)
class MirrorFacts:
    """What one ``.prec.txt`` says about where its recording ended."""
    last_real_sample_ut: Optional[float]   # None = no real sample at all
    max_ut: Optional[float]                # latest UT the trajectory reaches
    segments: Tuple[OrbitSeg, ...]
    error: str = ""


def _f(raw: Optional[str]) -> Optional[float]:
    if raw is None:
        return None
    try:
        v = float(raw)
    except ValueError:
        return None
    return v if math.isfinite(v) else None


def _seg(node: "saveparse.SfsNode") -> Optional[OrbitSeg]:
    vals = [_f(node.value(k)) for k in ("startUT", "endUT", "sma", "ecc", "mna", "epoch")]
    if any(v is None for v in vals):
        return None
    return OrbitSeg(start_ut=vals[0], end_ut=vals[1], sma=vals[2], ecc=vals[3],
                    mna=vals[4], epoch=vals[5], body=node.value("body") or "",
                    predicted=(node.value("isPredicted") or "").strip().lower() == "true")


def parse_mirror(text: Optional[str]) -> MirrorFacts:
    """Read one mirror. Never raises; a ConfigNode fault lands in ``error``."""
    res = saveparse.parse_sfs(text)
    root = res.root
    real: List[float] = []
    every: List[float] = []
    segs: Dict[Tuple[float, float, str], OrbitSeg] = {}

    def add_segments(node: "saveparse.SfsNode") -> None:
        for sn in node.nodes_named("ORBIT_SEGMENT"):
            s = _seg(sn)
            if s is None:
                continue
            segs.setdefault((s.start_ut, s.end_ut, s.body), s)
            every.append(s.end_ut)

    sections = root.nodes_named("TRACK_SECTION")
    for sec in sections:
        ref = sec.value("ref") or "0"
        predicted = (sec.value("src") or "") == PREDICTED_SECTION_SRC
        end = _f(sec.value("endUT"))
        if end is not None:
            every.append(end)
        for p in sec.nodes_named("POINT"):
            ut = _f(p.value("ut"))
            if ut is None:
                continue
            every.append(ut)
            if ref in REAL_SAMPLE_REFS and not predicted:
                real.append(ut)
        add_segments(sec)
    top_points = [u for u in (_f(p.value("ut")) for p in root.nodes_named("POINT"))
                  if u is not None]
    every.extend(top_points)
    if not real:
        # A flat write with no per-section frames: its top-level POINT list is the
        # recorded trajectory.
        real.extend(top_points)
    add_segments(root)
    ordered = tuple(sorted(segs.values(), key=lambda s: (s.start_ut, s.end_ut, s.body)))
    return MirrorFacts(last_real_sample_ut=max(real) if real else None,
                       max_ut=max(every) if every else None,
                       segments=ordered, error=res.error)


# ---------------------------------------------------------------------------
# Two-body altitude.
# ---------------------------------------------------------------------------


def radius_at_ut(seg: OrbitSeg, ut: float) -> Optional[float]:
    """Orbit radius (m) at ``ut`` from the segment's elements, or None when the body
    is unknown or the elements are degenerate."""
    body = BODY_GM_AND_RADIUS.get(seg.body)
    if body is None or seg.sma == 0.0 or seg.ecc < 0.0:
        return None
    gm = body[0]
    a, e = seg.sma, seg.ecc
    n = math.sqrt(gm / abs(a) ** 3)
    m = seg.mna + n * (ut - seg.epoch)
    if e < 1.0:
        m = math.fmod(m, 2.0 * math.pi)
        ea = m if e < 0.8 else math.pi
        for _ in range(100):
            d = (ea - e * math.sin(ea) - m) / (1.0 - e * math.cos(ea))
            ea -= d
            if abs(d) < 1e-12:
                break
        return a * (1.0 - e * math.cos(ea))
    if e == 1.0:
        return None
    h = math.asinh(m / e)
    for _ in range(100):
        d = (e * math.sinh(h) - h - m) / (e * math.cosh(h) - 1.0)
        h -= d
        if abs(d) < 1e-12:
            break
    return a * (1.0 - e * math.cosh(h))


def altitude_at_end(seg: OrbitSeg) -> Optional[float]:
    r = radius_at_ut(seg, seg.end_ut)
    if r is None:
        return None
    return r - BODY_GM_AND_RADIUS[seg.body][1]


# ---------------------------------------------------------------------------
# Facets.
# ---------------------------------------------------------------------------


def _chain_key(rec: "saveparse.RecordingRow") -> str:
    return ("chain:" + rec.chain_id) if rec.chain_id else ("rec:" + rec.recording_id)


def _max_opt(values: Sequence[Optional[float]]) -> Optional[float]:
    vals = [v for v in values if v is not None]
    return max(vals) if vals else None


def _round(v: Optional[float], nd: int = 1) -> Optional[float]:
    return None if v is None else round(v, nd)


def measure_ending(rec: "saveparse.RecordingRow",
                   chain: Sequence["saveparse.RecordingRow"],
                   mirrors: Dict[str, MirrorFacts]) -> Dict[str, Any]:
    """The facets of ONE ended recording, measured over its chain."""
    missing = [m.recording_id for m in chain if m.recording_id not in mirrors]
    own = mirrors.get(rec.recording_id)
    watched: List[Optional[float]] = []
    for m in chain:
        mf = mirrors.get(m.recording_id)
        if mf is None:
            continue
        watched.append(mf.last_real_sample_ut)
        watched.extend(s.end_ut for s in mf.segments if not s.predicted)
    last_watched = _max_opt(watched)
    predicted: Dict[Tuple[float, float, str], OrbitSeg] = {}
    for m in chain:
        mf = mirrors.get(m.recording_id)
        if mf is None:
            continue
        for s in mf.segments:
            if s.predicted:
                predicted.setdefault((s.start_ut, s.end_ut, s.body), s)
    end_ut = _max_opt([rec.explicit_end_ut, own.max_ut if own else None])
    tail = ((end_ut - last_watched)
            if (end_ut is not None and last_watched is not None) else None)
    last_pred = max(predicted.values(), key=lambda s: s.end_ut) if predicted else None
    alt = altitude_at_end(last_pred) if last_pred is not None else None
    return {
        "recordingId": rec.recording_id,
        "terminal": saveparse.terminal_state_name(rec.terminal_state),
        "terminalPredicted": rec.terminal_predicted,
        "quietEnd": rec.quiet_end,
        "terminalOrbitBody": rec.terminal_orbit_body or None,
        "chainMembers": len(chain),
        "missingMirrors": len(missing),
        "predictedSegments": len(predicted),
        "lastWatchedUT": _round(last_watched, 2),
        "endUT": _round(end_ut, 2),
        "explicitEndUT": _round(rec.explicit_end_ut, 2),
        "tailSeconds": _round(tail, 1),
        "lastPredictedBody": last_pred.body if last_pred else None,
        "lastPredictedEndUT": _round(last_pred.end_ut, 2) if last_pred else None,
        "endAltitudeMeters": _round(alt, 0),
    }


def observed_endings_facets(block: Optional[Dict],
                            snapshot: Optional["saveparse.ParsekSaveSnapshot"],
                            data: Optional[Any]) -> Dict[str, Any]:
    """Facets for a declared block: one entry per DECLARED vessel name (``{}`` when
    no block, or an unreadable save). ``data`` is a ``samplingq.SamplingInput``."""
    if not isinstance(block, dict) or snapshot is None or not snapshot.parsed:
        return {}
    texts: Dict[str, str] = dict(getattr(data, "prec_texts", None) or {})
    mirrors: Dict[str, MirrorFacts] = {}
    parse_errors = 0
    for rid in sorted(texts):
        mf = parse_mirror(texts[rid])
        if mf.error:
            parse_errors += 1
        mirrors[rid] = mf
    census = saveparse.census_recordings(snapshot)
    chains: Dict[str, List["saveparse.RecordingRow"]] = {}
    for rec in census:
        chains.setdefault(_chain_key(rec), []).append(rec)
    vessels = block.get(VESSELS_KEY) if isinstance(block.get(VESSELS_KEY), dict) else {}
    per_vessel: Dict[str, Any] = {}
    for name in sorted(vessels):
        rows = [r for r in census if r.vessel_name == name]
        ended = [r for r in rows if r.terminal_state is not None]
        terminals: Dict[str, int] = {}
        for r in ended:
            t = saveparse.terminal_state_name(r.terminal_state)
            terminals[t] = terminals.get(t, 0) + 1
        per_vessel[name] = {
            "recordings": len(rows),
            "ended": len(ended),
            "terminals": terminals,
            "endings": [measure_ending(r, chains[_chain_key(r)], mirrors) for r in ended],
        }
    return {
        "mirrors": len(mirrors),
        "missingMirrors": int(getattr(data, "missing_mirrors", 0) or 0),
        "parseErrors": parse_errors,
        "vessels": per_vessel,
    }


# ---------------------------------------------------------------------------
# Spec surface.
# ---------------------------------------------------------------------------


def _validate_signed_window(prefix: str, val: Any) -> List[str]:
    """Like ``saveparse._validate_window`` but the bounds may be negative (an
    altitude below the datum is a real reading)."""
    if isinstance(val, bool) or not isinstance(val, (int, dict)):
        return ["%s: %r must be an int or { min =, max = }" % (prefix, val)]
    if isinstance(val, int):
        return []
    errs: List[str] = []
    unknown = sorted(k for k in val if k not in ("min", "max"))
    if unknown:
        errs.append("%s: unknown key(s) %s (accepted: min, max)" % (prefix, unknown))
    if not any(k in val for k in ("min", "max")):
        errs.append("%s: an empty window gates nothing - declare min and/or max" % prefix)
    for k in ("min", "max"):
        if k in val and (isinstance(val[k], bool) or not isinstance(val[k], int)):
            errs.append("%s.%s: %r must be an int" % (prefix, k, val[k]))
    lo, hi = val.get("min"), val.get("max")
    if (isinstance(lo, int) and not isinstance(lo, bool)
            and isinstance(hi, int) and not isinstance(hi, bool) and lo > hi):
        errs.append("%s: min %d > max %d" % (prefix, lo, hi))
    return errs


def validate_endings_expectations(block: Any) -> List[str]:
    """Validate ``[expectations.recordings.endings]``. None => valid (absent)."""
    if block is None:
        return []
    prefix = "expectations.recordings.endings"
    if not isinstance(block, dict):
        return ["%s: must be a table" % prefix]
    errs: List[str] = []
    unknown = sorted(k for k in block if k not in ENDINGS_BLOCK_KEYS)
    if unknown:
        errs.append("%s: unknown key(s) %s (accepted: %s)"
                    % (prefix, unknown, list(ENDINGS_BLOCK_KEYS)))
    errs.extend(saveparse._validate_gating(prefix, block))
    vessels = block.get(VESSELS_KEY)
    if vessels is None:
        if block.get(saveparse.GATING_KEY) is True:
            errs.append("%s: gating = true with no %s table gates nothing"
                        % (prefix, VESSELS_KEY))
        return errs
    if not isinstance(vessels, dict) or not vessels:
        errs.append("%s.%s: must be a non-empty table of { \"<vessel name>\" = { ... } }"
                    % (prefix, VESSELS_KEY))
        return errs
    armed = block.get(saveparse.GATING_KEY) is True
    for name, spec in vessels.items():
        vp = "%s.%s.%s" % (prefix, VESSELS_KEY, name)
        if not isinstance(name, str) or not name.strip():
            errs.append("%s: a vessel name must be a non-empty string" % vp)
            continue
        if not isinstance(spec, dict):
            errs.append("%s: must be a table" % vp)
            continue
        bad = sorted(k for k in spec if k not in VESSEL_ASSERTION_KEYS)
        if bad:
            errs.append("%s: unknown key(s) %s (accepted: %s)"
                        % (vp, bad, list(VESSEL_ASSERTION_KEYS)))
        if armed and not any(k in spec for k in VESSEL_ASSERTION_KEYS):
            errs.append("%s: an armed vessel entry with no assertion key gates nothing" % vp)
        for key in (ENDED_KEY, PREDICTED_KEY, TAIL_KEY):
            if key in spec:
                errs.extend(saveparse._validate_window("%s.%s" % (vp, key), spec[key]))
        if armed:
            errs.extend(saveparse._validate_armed_unreddable(
                vp, dict(spec, **{saveparse.GATING_KEY: True}),
                (ENDED_KEY, PREDICTED_KEY, TAIL_KEY)))
        if ALTITUDE_KEY in spec:
            errs.extend(_validate_signed_window("%s.%s" % (vp, ALTITUDE_KEY), spec[ALTITUDE_KEY]))
        for key in BOOL_FLAG_KEYS:
            if key in spec and not isinstance(spec[key], bool):
                errs.append("%s.%s: %r must be a bool" % (vp, key, spec[key]))
        if ORBIT_BODIES_KEY in spec:
            want_b = spec[ORBIT_BODIES_KEY]
            if (not isinstance(want_b, list) or not want_b
                    or not all(isinstance(w, str) and w.strip() for w in want_b)):
                errs.append("%s.%s: must be a non-empty array of body names"
                            % (vp, ORBIT_BODIES_KEY))
        if TERMINALS_KEY in spec:
            want = spec[TERMINALS_KEY]
            if (not isinstance(want, list) or not want
                    or not all(isinstance(w, str) for w in want)):
                errs.append("%s.%s: must be a non-empty array of terminal-state names"
                            % (vp, TERMINALS_KEY))
            else:
                unknown_t = sorted(w for w in want if w not in saveparse.TERMINAL_STATE_NAMES)
                if unknown_t:
                    errs.append("%s.%s: unknown name(s) %s (accepted: %s)"
                                % (vp, TERMINALS_KEY, unknown_t,
                                   list(saveparse.TERMINAL_STATE_NAMES)))
    return errs


def has_assertion(block: Any) -> bool:
    """True when the block declares at least one per-vessel assertion key."""
    if not isinstance(block, dict) or not isinstance(block.get(VESSELS_KEY), dict):
        return False
    return any(isinstance(spec, dict) and any(k in spec for k in VESSEL_ASSERTION_KEYS)
               for spec in block[VESSELS_KEY].values())


def _check_signed(label: str, window: Any, measured: float, out: List[str]) -> None:
    """A window check over a (possibly negative) integer reading."""
    if isinstance(window, bool):
        return
    if isinstance(window, int):
        if measured != window:
            out.append("%s %d != %d" % (label, measured, window))
        return
    if not isinstance(window, dict):
        return
    lo, hi = window.get("min"), window.get("max")
    if isinstance(lo, int) and not isinstance(lo, bool) and measured < lo:
        out.append("%s %d < min %d" % (label, measured, lo))
    if isinstance(hi, int) and not isinstance(hi, bool) and measured > hi:
        out.append("%s %d > max %d" % (label, measured, hi))


def evaluate_endings(block: Dict, facets: Dict[str, Any]) -> List[str]:
    """Mismatches for a declared block over measured facets. Defined mismatches,
    whether or not a window asks for the number: no facets at all, a mirror that
    did not parse, and - for an asserted per-recording window - a chain member
    with no mirror or a reading that cannot be taken (no real sample, no predicted
    segment, an unknown body). An absent number is never read as a pass."""
    out: List[str] = []
    prefix = "recordings.endings"
    if not facets:
        out.append("%s: not measured (unreadable save or no vessels table)" % prefix)
        return out
    if facets.get("parseErrors"):
        out.append("%s: %d .prec.txt mirror(s) did not parse" % (prefix, facets["parseErrors"]))
    vessels = block.get(VESSELS_KEY) if isinstance(block.get(VESSELS_KEY), dict) else {}
    for name in sorted(vessels):
        spec = vessels[name] if isinstance(vessels[name], dict) else {}
        got = (facets.get("vessels") or {}).get(name) or {"ended": 0, "endings": []}
        label = "%s.%s" % (prefix, name)
        if ENDED_KEY in spec:
            saveparse._check_window("%s.ended" % label, spec[ENDED_KEY], got["ended"], out)
        want_t = spec.get(TERMINALS_KEY)
        for e in got["endings"]:
            rid = (e.get("recordingId") or "")[:8]
            rl = "%s[%s]" % (label, rid)
            if isinstance(want_t, list) and e.get("terminal") not in want_t:
                out.append("%s.terminal %s not in %s" % (rl, e.get("terminal"), want_t))
            for key in BOOL_FLAG_KEYS:
                if isinstance(spec.get(key), bool) and bool(e.get(key)) != spec[key]:
                    out.append("%s.%s %s != %s" % (rl, key, bool(e.get(key)), spec[key]))
            want_b = spec.get(ORBIT_BODIES_KEY)
            if isinstance(want_b, list) and e.get("terminalOrbitBody") not in want_b:
                out.append("%s.terminalOrbitBody %s not in %s"
                           % (rl, e.get("terminalOrbitBody") or "(none)", want_b))
            asked = [k for k in PER_RECORDING_WINDOW_KEYS if k in spec]
            if asked and e.get("missingMirrors"):
                out.append("%s: %d chain member(s) carry no .prec.txt mirror - not measured"
                           % (rl, e["missingMirrors"]))
            for key in asked:
                value = e.get(key)
                if value is None:
                    out.append("%s.%s: not measured" % (rl, key))
                    continue
                if key == ALTITUDE_KEY:
                    _check_signed("%s.%s" % (rl, key), spec[key], int(round(value)), out)
                else:
                    saveparse._check_window("%s.%s" % (rl, key), spec[key],
                                            int(math.floor(value)), out)
    return out
