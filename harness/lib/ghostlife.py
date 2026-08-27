"""Pure ghost-lifecycle decisions for the flight-scene mesh spawn/destroy verifier.

The regression tripwire this module exists to be: during ghost playback
``GhostPlaybackEngine`` emits ONE ``GhostRenderTrace`` line per ghost mesh that
enters the flight scene and one per mesh that leaves it. Those two lines are the
cheapest honest statement that a ghost RENDERED AT ALL - no polyline, no
manifest, no map required - and nothing in the harness read them. A change that
silently stopped spawning ghost meshes would fly green through every existing
verifier row: the recordings still exist on disk (``recordings.count``), they
still carry points (``recordings.points``), the save's topology is unchanged
(``[expectations.recordings.structure]``), and the render-composition manifest
describes the MAP pipeline rather than the flight-scene mesh.

Shape mirrors ``saveparse.py`` / ``rendercompose.py`` exactly, because those two
are the established instrument pattern here:

  - a tolerant parser over the COLLECTED KSP.log text (``parse_ghost_lifecycle``)
    that never raises: an unrecognised line is counted, never fatal;
  - the measured facets (``observed_ghost_lifecycle_facets``), recorded
    UNCONDITIONALLY on any readable log so a lane can author its first honest
    window off a green report-only run;
  - the ``[expectations.ghostLifecycle]`` spec-surface validator
    (``validate_ghost_lifecycle_expectations``, called from
    ``hlib.validate_spec``) and the evaluator (``evaluate_ghost_lifecycle``),
    REPORT-ONLY unless the block declares ``gating = true``.

Side-effect-free: no KSP, no network, no filesystem. ``run.py`` owns the I/O and
hands the log TEXT in - the same text the ``logContracts`` row already reads, so
this row costs no second read of a multi-hundred-megabyte file.

THE PRODUCER, transcribed from source rather than from a pasted log line
(``Source/Parsek/GhostRenderTrace.cs`` ``BuildPrefix`` +
``Source/Parsek/GhostPlaybackEngine.cs`` ``EmitMeshLifecycleTrace``)::

    [Parsek][INFO][GhostRenderTrace] phase=MeshSpawned rec=<8> recId=<id>
        ghostIndex=<int> frame=<int> currentUT=<F3> playbackUT=<F3>
        vessel=<name> reason=<reason>

Two properties of that line drive the whole parser and are easy to get wrong:

  1. The PREFIX fields are single tokens by construction. ``recId`` runs through
     ``GhostRenderTrace.Token``, which replaces spaces with underscores, and the
     two UTs through ``FormatDouble`` (which can legitimately emit ``NaN`` /
     ``Infinity`` / ``-Infinity``, so they are NOT parsed as bare floats).
  2. The TAIL is NOT tokenised. ``vessel=`` and ``reason=`` come from a
     ``FormattableString.Invariant`` interpolation of raw values, so BOTH may
     contain spaces - vessel names routinely do ("Kerbal X Mk2"), and the
     destroy reasons are English phrases ("playback completed", "watch hold
     expired", "auto-followed to next stage"). Splitting the tail on whitespace
     would truncate every multi-word vessel name and every reason, and a reason
     census built that way would silently fail every ``forbidden`` regex written
     against the real text. The tail is therefore cut at the FIRST ``" reason="``
     after ``vessel=`` and the remainder of the line IS the reason, verbatim.

THE LINE IS ANCHORED ON THE SUBSYSTEM TAG, and that is the ``_anomaly_reasons``
lesson repeated rather than caution: hlib's anomaly sweep used to be a bare
substring search and S1.7's first flight red on a TestRunner line whose own
diagnostic label happened to contain the token. A log line that merely NAMES
``phase=MeshSpawned`` - a test echo, a comment quoted into a message, this
docstring pasted into a report - must not count as a ghost mesh. Only a line
carrying ``[GhostRenderTrace]`` AND the phase head does.

THE TRACER IS OFF BY DEFAULT. ``GhostRenderTrace`` is gated by the
``ghostRenderTracing`` setting, which ``run.py`` pins OFF in the instance
settings sidecar at stage (``hlib.TRACER_SETTING_KEYS``). A spec that wants
these lines MUST drive ``SetSetting ghostRenderTracing=true``; there is no other
route, since the sidecar wins over whatever the fixture save carries. That is
why a declared block measuring ZERO ``MeshSpawned`` lines is a MISMATCH and not
a silent pass - see ``evaluate_ghost_lifecycle``.
"""

from __future__ import annotations

import copy
import re
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Sequence, Tuple

import saveparse


# ---------------------------------------------------------------------------
# The producer's vocabulary (transcribed from the C# emitter).
# ---------------------------------------------------------------------------

# ParsekLog.Write renders "[Parsek][{level}][{subsystem}] {message}", and
# EmitMeshLifecycleTrace routes through GhostRenderTrace.EmitPhase with
# important=true => ParsekLog.Info("GhostRenderTrace", ...). The tag is the
# anchor (see the module docstring's anchoring note).
TRACE_SUBSYSTEM_TAG = "[GhostRenderTrace]"

PHASE_SPAWNED = "MeshSpawned"
PHASE_DESTROYED = "MeshDestroyed"
LIFECYCLE_PHASES: Tuple[str, ...] = (PHASE_SPAWNED, PHASE_DESTROYED)

# The spawn reason is a single fixed token at the one call site
# (QueueOrEmitGhostCreated). Named so the census has something to be checked
# against rather than only counted.
SPAWN_REASON = "ghost-created"

# The prefix, field for field, in BuildPrefix's emit order. Pinned in the test
# suite against the C# source (deliberately, by extracting that ONE return
# expression - never by a regex over the whole file, which reads comments as
# code).
PREFIX_FIELDS: Tuple[str, ...] = (
    "phase", "rec", "recId", "ghostIndex", "frame", "currentUT", "playbackUT")

# One regex for the prefix; the tail is cut by string search, NOT by this
# pattern. A greedy/lazy regex over `vessel=(.*) reason=(.*)` would be a second
# place the "spaces are legal in both" rule has to be right, and the two would
# drift.
_LINE_RE = re.compile(
    r"phase=(?P<phase>MeshSpawned|MeshDestroyed)"
    r"\s+rec=(?P<rec>\S+)"
    r"\s+recId=(?P<rec_id>\S+)"
    r"\s+ghostIndex=(?P<ghost_index>-?\d+)"
    r"\s+frame=(?P<frame>-?\d+)"
    r"\s+currentUT=(?P<current_ut>\S+)"
    r"\s+playbackUT=(?P<playback_ut>\S+)"
    r"\s+vessel=(?P<tail>.*)$")

_REASON_SEP = " reason="


# ---------------------------------------------------------------------------
# Parsed model.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class MeshLifecycleLine:
    """One parsed ``MeshSpawned`` / ``MeshDestroyed`` line.

    ``current_ut`` / ``playback_ut`` stay STRINGS. ``GhostRenderTrace.FormatDouble``
    emits ``NaN`` / ``Infinity`` / ``-Infinity`` for the non-finite cases, and a
    float() over those would either raise or invent a value; nothing this module
    asserts needs them as numbers, so they are carried verbatim for triage.
    """

    phase: str
    rec: str
    rec_id: str
    ghost_index: int
    frame: int
    current_ut: str
    playback_ut: str
    vessel: str
    reason: str


@dataclass(frozen=True)
class GhostLifecycleSnapshot:
    """Every lifecycle line found in one log, plus the parse's own health.

    ``parsed`` is False ONLY when there was no log text to read at all (``None``).
    An EMPTY string parses fine and measures zero lines - "we looked and saw
    none" - which is a different statement from "we never looked", and the
    evaluator says each of them differently.

    ``malformed`` counts lines carrying the subsystem tag AND a lifecycle phase
    head that the field regex could not read. It is reported rather than ignored:
    a producer-side format change would show up here as a nonzero count beside a
    collapsed spawn count, which is a far better triage signal than a bare zero.
    """

    lines: Tuple[MeshLifecycleLine, ...] = ()
    malformed: int = 0
    parsed: bool = True
    error: str = ""

    @property
    def spawns(self) -> Tuple[MeshLifecycleLine, ...]:
        return tuple(l for l in self.lines if l.phase == PHASE_SPAWNED)

    @property
    def destroys(self) -> Tuple[MeshLifecycleLine, ...]:
        return tuple(l for l in self.lines if l.phase == PHASE_DESTROYED)


def parse_ghost_lifecycle(log_text: Optional[str]) -> GhostLifecycleSnapshot:
    """Scan a collected KSP.log's TEXT for ghost mesh lifecycle lines (pure).

    Never raises. Lines that are not lifecycle lines are skipped in O(1) by the
    two anchors before the regex runs, which matters: this walks a log that is
    routinely hundreds of megabytes.
    """
    if log_text is None:
        return GhostLifecycleSnapshot(parsed=False, error="log text unavailable")
    out: List[MeshLifecycleLine] = []
    malformed = 0
    for raw in log_text.splitlines():
        if TRACE_SUBSYSTEM_TAG not in raw:
            continue
        head = None
        for phase in LIFECYCLE_PHASES:
            if ("phase=" + phase) in raw:
                head = phase
                break
        if head is None:
            continue
        m = _LINE_RE.search(raw)
        if m is None:
            malformed += 1
            continue
        tail = m.group("tail")
        # rfind, not find: `reason=` is the LAST field and its values are the
        # engine's own fixed English phrases, while `vessel=` is player-authored
        # free text - a craft named "X reason=test" must split as
        # vessel="X reason=test", not poison both fields by cutting at the
        # first occurrence.
        cut = tail.rfind(_REASON_SEP)
        if cut < 0:
            # The phase head and every prefix field read, but the tail carries no
            # `reason=`. That is a producer-side shape change, not a vessel name
            # we failed to split - counted as malformed rather than guessed at.
            malformed += 1
            continue
        out.append(MeshLifecycleLine(
            phase=m.group("phase"),
            rec=m.group("rec"),
            rec_id=m.group("rec_id"),
            ghost_index=int(m.group("ghost_index")),
            frame=int(m.group("frame")),
            current_ut=m.group("current_ut"),
            playback_ut=m.group("playback_ut"),
            vessel=tail[:cut],
            reason=tail[cut + len(_REASON_SEP):]))
    return GhostLifecycleSnapshot(lines=tuple(out), malformed=malformed)


# ---------------------------------------------------------------------------
# Spec surface ([expectations.ghostLifecycle]).
#
# The window grammar and the armed-unreddable notch are saveparse's semantics,
# COPIED rather than imported for rendercompose's stated reason: they are that
# module's PRIVATES, and a sibling's private helper is not an API. A drift
# between the copies is caught by a unit cell that runs both over the same
# inputs. GATING_KEY is IMPORTED, not copied - it is a public name and the
# spelling of a spec KEY, so the modules must agree byte-for-byte or an armed
# block silently reads as unarmed.
# ---------------------------------------------------------------------------

GATING_KEY = saveparse.GATING_KEY

GHOST_LIFECYCLE_BLOCK = "ghostLifecycle"

SPAWNED_KEY = "spawned"
REQUIRE_BALANCED_KEY = "requireBalanced"
DESTROYED_REASONS_KEY = "destroyedReasons"
FORBIDDEN_KEY = "forbidden"

# Every window key must also be an unconditional key of
# `observed_ghost_lifecycle_facets` (pinned by a unit cell), because a window
# over a facet the module does not measure could only ever be answered by a
# default - the vacuity `_check_windows_against_facets` refuses to invent.
GHOST_LIFECYCLE_WINDOW_KEYS: Tuple[str, ...] = (SPAWNED_KEY,)
GHOST_LIFECYCLE_ASSERTION_KEYS: Tuple[str, ...] = (
    SPAWNED_KEY, REQUIRE_BALANCED_KEY, DESTROYED_REASONS_KEY)
GHOST_LIFECYCLE_BLOCK_KEYS: Tuple[str, ...] = (
    (GATING_KEY,) + GHOST_LIFECYCLE_ASSERTION_KEYS)

DESTROYED_REASONS_BLOCK_KEYS: Tuple[str, ...] = (FORBIDDEN_KEY,)

# `requireBalanced` DEFAULTS ON when the block is present. Every ghost that
# entered the scene must eventually leave it; a spawn with no matching destroy is
# either a leaked mesh or a run that ended mid-playback, and both are worth a
# reader's attention. Opting OUT is a positive `requireBalanced = false`.
REQUIRE_BALANCED_DEFAULT = True

STATUS_REPORT = saveparse.STATUS_REPORT
STATUS_PASS = saveparse.STATUS_PASS
STATUS_FAIL = saveparse.STATUS_FAIL


def _validate_window(prefix: str, val: Any) -> List[str]:
    """A count assertion is either a bare non-negative int (exact pin) or a
    ``{ min =, max = }`` table with at least one bound, ints >= 0, min <= max.
    (bool is an int subclass in Python - rejected explicitly.)"""
    if isinstance(val, bool):
        return ["%s: %r must be a non-negative int or { min =, max = }" % (prefix, val)]
    if isinstance(val, int):
        return [] if val >= 0 else ["%s: %d must be >= 0" % (prefix, val)]
    if not isinstance(val, dict):
        return ["%s: %r must be a non-negative int or { min =, max = }" % (prefix, val)]
    errs: List[str] = []
    unknown = sorted(k for k in val if k not in ("min", "max"))
    if unknown:
        errs.append("%s: unknown key(s) %s (accepted: min, max)" % (prefix, unknown))
    if not any(k in val for k in ("min", "max")):
        errs.append("%s: an empty window gates nothing - declare min and/or max" % prefix)
    for k in ("min", "max"):
        if k in val and (isinstance(val[k], bool) or not isinstance(val[k], int)
                         or val[k] < 0):
            errs.append("%s.%s: %r must be a non-negative int" % (prefix, k, val[k]))
    lo, hi = val.get("min"), val.get("max")
    if (isinstance(lo, int) and not isinstance(lo, bool)
            and isinstance(hi, int) and not isinstance(hi, bool) and lo > hi):
        errs.append("%s: min %d > max %d" % (prefix, lo, hi))
    return errs


def _validate_gating(prefix: str, block: Dict) -> List[str]:
    if GATING_KEY in block and not isinstance(block[GATING_KEY], bool):
        return ["%s.%s: %r must be a bool" % (prefix, GATING_KEY, block[GATING_KEY])]
    return []


def _validate_armed_unreddable(prefix: str, block: Dict,
                               window_keys: Tuple[str, ...]) -> List[str]:
    """An ARMED window whose only bound is ``min = 0`` can never fail (counts are
    never negative), so the key states an assertion and asserts nothing."""
    if block.get(GATING_KEY) is not True:
        return []
    errs: List[str] = []
    for key in window_keys:
        win = block.get(key)
        if not isinstance(win, dict):
            continue
        if win.get("min") == 0 and not isinstance(win.get("min"), bool) \
                and "max" not in win:
            errs.append(
                "%s.%s: an ARMED { min = 0 } window can never red (counts are "
                "never negative) - give it a max, raise the min, or drop the key"
                % (prefix, key))
    return errs


def _validate_destroyed_reasons(prefix: str, val: Any) -> List[str]:
    """``destroyedReasons = { forbidden = ["re", ...] }``.

    The patterns are COMPILE-VALIDATED here, exactly like
    ``logContracts.required`` / ``forbidden``: a broken regex discovered
    post-flight costs a whole KSP boot to learn, and (worse) an evaluator that
    swallowed the compile error would report "nothing matched" for a clause that
    never ran.
    """
    if not isinstance(val, dict):
        return ["%s: %r must be a table" % (prefix, val)]
    errs: List[str] = []
    unknown = sorted(k for k in val if k not in DESTROYED_REASONS_BLOCK_KEYS)
    if unknown:
        errs.append("%s: unknown key(s) %s (accepted: %s)"
                    % (prefix, unknown, list(DESTROYED_REASONS_BLOCK_KEYS)))
    if FORBIDDEN_KEY not in val:
        errs.append("%s: declared with no `%s` list - it asserts nothing"
                    % (prefix, FORBIDDEN_KEY))
        return errs
    pats = val.get(FORBIDDEN_KEY)
    label = "%s.%s" % (prefix, FORBIDDEN_KEY)
    if not isinstance(pats, (list, tuple)):
        return errs + ["%s: %r must be a list of regex strings" % (label, pats)]
    if not pats:
        errs.append("%s: an empty list asserts nothing - name at least one "
                    "pattern, or drop the key" % label)
    for pat in pats:
        if not isinstance(pat, str):
            errs.append("%s: %r must be a string" % (label, pat))
            continue
        try:
            re.compile(pat)
        except re.error as exc:
            errs.append("%s: %r is not a valid regex (%s)" % (label, pat, exc))
    return errs


def validate_ghost_lifecycle_expectations(block: Any) -> List[str]:
    """Validate the ``[expectations.ghostLifecycle]`` spec surface (pre-launch,
    pure). ``None`` => no block declared => valid.

    NOTE THE ONE NOTCH THAT IS DELIBERATELY ABSENT, because its absence is a
    decision and not an omission: saveparse / rendercompose both reject
    ``gating = true`` with ZERO assertion keys, on the grounds that such a block
    passes green off an observation that measured nothing. THAT IS NOT TRUE HERE.
    An armed bare block still carries two floors of its own - ``requireBalanced``
    defaults ON, and a declared block measuring zero ``MeshSpawned`` lines is a
    defined mismatch - so ``[expectations.ghostLifecycle] gating = true`` on its
    own asserts "at least one ghost mesh spawned, and every one that spawned was
    destroyed". Refusing it would refuse the block's most useful minimal form.
    """
    if block is None:
        return []
    if not isinstance(block, dict):
        return ["expectations.%s: must be a table" % (GHOST_LIFECYCLE_BLOCK,)]
    prefix = "expectations." + GHOST_LIFECYCLE_BLOCK
    errs: List[str] = []
    unknown = sorted(k for k in block if k not in GHOST_LIFECYCLE_BLOCK_KEYS)
    if unknown:
        errs.append("%s: unknown key(s) %s (accepted: %s)"
                    % (prefix, unknown, list(GHOST_LIFECYCLE_BLOCK_KEYS)))
    errs.extend(_validate_gating(prefix, block))
    errs.extend(_validate_armed_unreddable(prefix, block,
                                           GHOST_LIFECYCLE_WINDOW_KEYS))
    if SPAWNED_KEY in block:
        errs.extend(_validate_window("%s.%s" % (prefix, SPAWNED_KEY),
                                     block[SPAWNED_KEY]))
    if REQUIRE_BALANCED_KEY in block and not isinstance(
            block[REQUIRE_BALANCED_KEY], bool):
        errs.append("%s.%s: %r must be a bool"
                    % (prefix, REQUIRE_BALANCED_KEY, block[REQUIRE_BALANCED_KEY]))
    if DESTROYED_REASONS_KEY in block:
        errs.extend(_validate_destroyed_reasons(
            "%s.%s" % (prefix, DESTROYED_REASONS_KEY),
            block[DESTROYED_REASONS_KEY]))
    return errs


def ghost_lifecycle_expectation_warnings(expectations: Optional[Dict],
                                         steps: Optional[Sequence[Dict]] = None
                                         ) -> List[str]:
    """Pre-launch WARNINGS (never errors) for a declared block.

    ONE warning, and it is the coupling this row's whole value depends on: the
    ``GhostRenderTrace`` producer is gated by the ``ghostRenderTracing`` setting,
    which ``run.py`` pins OFF in the instance settings sidecar at stage and which
    the sidecar layer applies OVER whatever the fixture save carries. So the ONLY
    route to a non-empty measurement is a ``SetSetting ghostRenderTracing=true``
    step, and a spec declaring the block without one is guaranteed to measure
    zero lines.

    WHY THIS IS A WARNING AND NOT AN ERROR, deliberately breaking with the
    ``ExportRenderManifest`` / ``renderComposition`` coupling rule that rejects
    its equivalent pre-launch. Two reasons, and the second is the load-bearing
    one. (1) The failure mode the ERROR exists to stop - a quiet green off a
    surface nobody armed - cannot happen here: zero ``MeshSpawned`` lines under a
    declared block is a DEFINED mismatch, so the tracer-off run reds loudly (or
    reports loudly) rather than passing. (2) An error would refuse the vacuity
    floor's own NEGATIVE CONTROL - the flight an operator has to fly once to
    prove the floor fires - which is exactly the shape "declare the block, drop
    the SetSetting step" takes.
    """
    expectations = expectations or {}
    block = expectations.get(GHOST_LIFECYCLE_BLOCK)
    if not isinstance(block, dict):
        return []
    for step in (steps or []):
        step = step or {}
        if step.get("cmd") != "SetSetting":
            continue
        args = step.get("args", {}) or {}
        if str(args.get("name")) == "ghostRenderTracing" \
                and str(args.get("value")).lower() == "true":
            return []
    return ["expectations.%s: declared with no `SetSetting ghostRenderTracing "
            "= true` step - run.py pins that tracer OFF in the instance sidecar "
            "at stage and the sidecar wins over the fixture save, so this run "
            "will measure ZERO MeshSpawned lines (a defined mismatch). Add the "
            "step, or keep the block only if this run IS the tracer-off "
            "negative control" % (GHOST_LIFECYCLE_BLOCK,)]


def declared_ghost_lifecycle_blocks(expectations: Optional[Dict]) -> Tuple[str, ...]:
    """``("ghostLifecycle",)`` when the spec declares the block, else ``()``."""
    expectations = expectations or {}
    if isinstance(expectations.get(GHOST_LIFECYCLE_BLOCK), dict):
        return (GHOST_LIFECYCLE_BLOCK,)
    return ()


def declared_ghost_lifecycle_block(expectations: Optional[Dict]
                                   ) -> Optional[Dict[str, Any]]:
    """The declared block's KEY/VALUES (a deep copy), or ``None``.

    The audit surface ``rendercompose`` grew after run ``2026-08-25_1811``: a
    negative control whose own result cannot say WHAT it asserted is not a
    control. Copied so a later mutation of the spec dict cannot rewrite the
    record of what ran.
    """
    expectations = expectations or {}
    block = expectations.get(GHOST_LIFECYCLE_BLOCK)
    return copy.deepcopy(block) if isinstance(block, dict) else None


def armed_ghost_lifecycle_blocks(expectations: Optional[Dict]) -> Tuple[str, ...]:
    """The declared subset carrying ``gating = true``. Arming is PER-BLOCK."""
    expectations = expectations or {}
    block = expectations.get(GHOST_LIFECYCLE_BLOCK)
    if isinstance(block, dict) and block.get(GATING_KEY) is True:
        return (GHOST_LIFECYCLE_BLOCK,)
    return ()


def gating_armed(expectations: Optional[Dict]) -> bool:
    """True iff the ghostLifecycle block is armed. NO committed spec arms it as
    of this module landing (the row ships REPORT-ONLY); promotion is an operator
    decision taken after a report-only reading run, pinned by the harness
    allowlist sweep in ``test_hlib.py``."""
    return bool(armed_ghost_lifecycle_blocks(expectations))


def require_balanced(block: Optional[Dict]) -> bool:
    """``requireBalanced``, defaulting ON for a declared block (see
    ``REQUIRE_BALANCED_DEFAULT``). A non-bool value was rejected at spec
    validation; tolerated here as the default so a drifted spec cannot crash the
    verifier mid-chain (saveparse's rule)."""
    if not isinstance(block, dict):
        return False
    val = block.get(REQUIRE_BALANCED_KEY, REQUIRE_BALANCED_DEFAULT)
    return val if isinstance(val, bool) else REQUIRE_BALANCED_DEFAULT


# ---------------------------------------------------------------------------
# Measured facets.
# ---------------------------------------------------------------------------


def _census(values: Sequence[str]) -> Dict[str, int]:
    """Count occurrences, sorted for a stable run-JSON diff. A blank rides as
    ``(blank)`` rather than being dropped: a value the writer could not name is
    still an occurrence."""
    counts: Dict[str, int] = {}
    for v in values:
        key = v if v else "(blank)"
        counts[key] = counts.get(key, 0) + 1
    return dict(sorted(counts.items()))


def unbalanced_recordings(snapshot: Optional[GhostLifecycleSnapshot]
                          ) -> Tuple[Dict[str, str], ...]:
    """Recordings that spawned a mesh and never destroyed one, in first-spawn
    order, each carrying the VESSEL NAME off its first spawn line.

    The vessel name is the point of carrying a dict rather than a bare id: an
    8-char ``rec=`` prefix and a 32-hex ``recId=`` name nothing a reader can act
    on, and the operator reading a red row needs to know WHICH craft's ghost
    leaked.

    Balance is per-RECORDING, not per (recording, ghostIndex) pair, and that is a
    deliberate weakening. ``ghostIndex`` is the engine's ``ghostStates`` key and
    is REUSED across spawns within one run, so a per-index ledger would need
    ordered pairing and would call a legitimate respawn/redestroy cycle
    unbalanced. "This recording's ghost was seen leaving the scene at least once"
    is the claim that survives loop playback, overlap retarget and zone
    transitions - which is the population this row exists to watch.
    """
    if snapshot is None or not snapshot.parsed:
        return ()
    destroyed = {l.rec_id for l in snapshot.destroys}
    out: List[Dict[str, str]] = []
    seen: set = set()
    for line in snapshot.spawns:
        if line.rec_id in seen or line.rec_id in destroyed:
            continue
        seen.add(line.rec_id)
        out.append({"recId": line.rec_id, "rec": line.rec, "vessel": line.vessel})
    return tuple(out)


def observed_ghost_lifecycle_facets(snapshot: Optional[GhostLifecycleSnapshot]
                                    ) -> Dict[str, Any]:
    """The MEASURED facets, mirroring the ``[expectations.*]`` block layout the
    way ``saveparse.observed_structure_facets`` does. ``None`` / unparsed => ``{}``
    (ABSENT means "not measured", never zero).

    Recorded UNCONDITIONALLY on a readable log - no block needed - because that
    is how a lane earns its first honest window off a green report-only run.
    """
    if snapshot is None or not snapshot.parsed:
        return {}
    spawn_ids: List[str] = []
    for line in snapshot.spawns:
        if line.rec_id not in spawn_ids:
            spawn_ids.append(line.rec_id)
    destroy_ids = {l.rec_id for l in snapshot.destroys}
    unbalanced = unbalanced_recordings(snapshot)
    return {
        GHOST_LIFECYCLE_BLOCK: {
            # THE window-key facet, named exactly as its spec key and written
            # unconditionally (a window key that could be absent would be a
            # window answered by a default).
            SPAWNED_KEY: len(spawn_ids),
            # Line-level counts. `spawned` counts DISTINCT recordings; these
            # count LINES, and the two differ whenever a ghost respawns (loop
            # playback, overlap retarget, a zone transition). Reported
            # separately so a reader can tell one ghost seen five times from
            # five ghosts seen once.
            "spawnLines": len(snapshot.spawns),
            "destroyLines": len(snapshot.destroys),
            "destroyedRecordings": len(destroy_ids),
            "unbalanced": [dict(u) for u in unbalanced],
            "spawnedRecordingIds": list(spawn_ids),
            # The reason censuses. `destroyedReasons` is what a `forbidden`
            # pattern is written against, so an operator sizing a first clause
            # reads the real text off a green run instead of guessing at the
            # English.
            "destroyedReasons": _census([l.reason for l in snapshot.destroys]),
            "spawnReasons": _census([l.reason for l in snapshot.spawns]),
            "vessels": sorted({l.vessel for l in snapshot.lines if l.vessel}),
            # Producer-shape triage (see GhostLifecycleSnapshot.malformed).
            "malformed": snapshot.malformed,
        },
    }


# ---------------------------------------------------------------------------
# Evaluation (the verifier decision).
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class GhostLifecycleResult:
    """Verifier outcome. Shape = ``saveparse.SaveStructureResult`` plus the
    ``declared`` audit field ``rendercompose`` added.

    - ``REPORT``: nothing armed (verdict-neutral default). Mismatches are
      RECORDED and move no verdict; the promotion path reads them off the run
      JSON.
    - ``PASS`` / ``FAIL``: the block is armed. ``FAIL`` must be mapped by the
      caller to the ``ghost_lifecycle_mismatch`` verifier flag (PARSEK-FAIL,
      subkind ``ghost-lifecycle``).
    """

    status: str
    gating: bool
    mismatches: Tuple[str, ...]
    armed_mismatches: Tuple[str, ...]
    observed: Dict[str, Any]
    blocks: Tuple[str, ...]
    armed_blocks: Tuple[str, ...]
    parsed: Optional[bool]     # None = the log was never read at all
    parse_error: str
    declared: Optional[Dict[str, Any]] = None


def _check_window(label: str, spec_val: Any, measured: int,
                  mismatches: List[str]) -> None:
    """One count assertion. Bare int = exact pin; table = min/max window. Shapes
    beyond that were rejected at spec validation; tolerated here as no-ops so a
    drifted spec cannot crash the verifier mid-chain (saveparse's rule)."""
    if isinstance(spec_val, bool):
        return
    if isinstance(spec_val, int):
        if measured != spec_val:
            mismatches.append("%s %d != %d" % (label, measured, spec_val))
        return
    if not isinstance(spec_val, dict):
        return
    lo, hi = spec_val.get("min"), spec_val.get("max")
    if isinstance(lo, int) and not isinstance(lo, bool) and measured < lo:
        mismatches.append("%s %d < min %d" % (label, measured, lo))
    if isinstance(hi, int) and not isinstance(hi, bool) and measured > hi:
        mismatches.append("%s %d > max %d" % (label, measured, hi))


def _check_windows_against_facets(block: Dict[str, Any], facets: Dict[str, Any],
                                  mismatches: List[str]) -> None:
    """Every declared window key, checked against the MEASURED facet of the same
    name. ABSENT + WINDOWED IS A MISMATCH (rendercompose's decision, and for its
    reason): a defaulted zero passes a ``{ max = 0 }`` clause off a surface that
    never ran and reds a floor with a number the run never produced. Unreachable
    today - a unit cell pins every window key to an unconditional facet key - and
    that is the point."""
    for key in GHOST_LIFECYCLE_WINDOW_KEYS:
        if key not in block:
            continue
        label = "%s.%s" % (GHOST_LIFECYCLE_BLOCK, key)
        if key not in facets:
            mismatches.append(
                "%s: declared but NOT MEASURED - the log carries no '%s' facet, "
                "so the window has nothing to assert against" % (label, key))
            continue
        _check_window(label, block[key], int(facets.get(key) or 0), mismatches)


def _check_forbidden_reasons(block: Dict[str, Any],
                             snapshot: GhostLifecycleSnapshot,
                             mismatches: List[str]) -> None:
    """``destroyedReasons.forbidden``: no MeshDestroyed reason may MATCH.

    ``re.search``, not ``fullmatch`` - the same contract ``logContracts``
    patterns carry, so a spec can forbid a substring ("explod") without knowing
    the whole English phrase. Patterns were compile-validated at spec validation;
    a broken one here is skipped rather than raised (the drifted-spec rule),
    because the alternative is crashing the verifier chain and losing every other
    row's evidence.
    """
    reasons = block.get(DESTROYED_REASONS_KEY)
    if not isinstance(reasons, dict):
        return
    pats = reasons.get(FORBIDDEN_KEY)
    if not isinstance(pats, (list, tuple)):
        return
    for pat in pats:
        if not isinstance(pat, str):
            continue
        try:
            rx = re.compile(pat)
        except re.error:
            continue
        hits = [l for l in snapshot.destroys if rx.search(l.reason)]
        if not hits:
            continue
        # Name the FIRST hit with its vessel: a mismatch that only says "the
        # pattern matched" sends the reader back to the log to find out which
        # ghost it was.
        first = hits[0]
        mismatches.append(
            "%s.%s.%s: forbidden destroy reason %r matched %d MeshDestroyed "
            "line(s); first: rec=%s vessel=%r reason=%r"
            % (GHOST_LIFECYCLE_BLOCK, DESTROYED_REASONS_KEY, FORBIDDEN_KEY,
               pat, len(hits), first.rec, first.vessel, first.reason))


def evaluate_ghost_lifecycle(expectations: Optional[Dict],
                             snapshot: Optional[GhostLifecycleSnapshot]
                             ) -> GhostLifecycleResult:
    """Evaluate ``[expectations.ghostLifecycle]`` against a parsed log scan.

    THE VACUITY FLOOR IS THE WHOLE POINT. A declared block over a log with ZERO
    ``MeshSpawned`` lines is a DEFINED mismatch, never a silent pass: this row
    exists to catch ghosts that never rendered, and "nothing rendered" is exactly
    the observation a count-based clause would otherwise green off. The two ways
    to reach it - the ``ghostRenderTracing`` setting never turned on, and the
    playback never spawning a mesh - are indistinguishable from the log and are
    reported as one mismatch naming both.

    Structural faults are DEFINED mismatches whenever the block is declared (the
    ``saveparse.evaluate_save_structure`` shape, and for its reason - a fault
    that produces no mismatch is a fault that passes):

    - ``snapshot is None`` / ``parsed=False``: the collected log was never read.
      With no block declared both degrade to an empty REPORT row.

    Arming semantics: an armed block gates on every mismatch it produced - the
    window, the balance ledger, the forbidden-reason clauses and the vacuity
    floor alike.
    """
    expectations = expectations or {}
    blocks = declared_ghost_lifecycle_blocks(expectations)
    armed = armed_ghost_lifecycle_blocks(expectations)
    block = expectations.get(GHOST_LIFECYCLE_BLOCK)
    block = block if isinstance(block, dict) else None

    mismatches: List[str] = []
    unreadable = snapshot is None or not snapshot.parsed
    parsed: Optional[bool] = None if snapshot is None else bool(snapshot.parsed)
    parse_error = "" if snapshot is None else snapshot.error

    if unreadable:
        observed: Dict[str, Any] = {}
        reason = ("log unreadable: %s" % (snapshot.error,)) if snapshot is not None \
            else ("log absent: no collected KSP.log text to scan for "
                  "GhostRenderTrace mesh lifecycle lines")
        for _b in blocks:
            mismatches.append(reason)
    else:
        observed = observed_ghost_lifecycle_facets(snapshot)
        facets = observed[GHOST_LIFECYCLE_BLOCK]
        if block is not None:
            if facets[SPAWNED_KEY] == 0:
                mismatches.append(
                    "%s: no MeshSpawned lines found (tracer off or nothing "
                    "rendered) - the block was declared, so this is a mismatch "
                    "rather than a vacuous pass" % (GHOST_LIFECYCLE_BLOCK,))
            _check_windows_against_facets(block, facets, mismatches)
            if require_balanced(block):
                # The facets dict two lines up already carries this exact list;
                # reusing it keeps the reported facet and the mismatch rows one
                # computation, so an edit to the balance key cannot make them
                # disagree.
                for row in facets["unbalanced"]:
                    mismatches.append(
                        "%s.%s: rec=%s vessel=%r spawned a ghost mesh and never "
                        "destroyed one"
                        % (GHOST_LIFECYCLE_BLOCK, REQUIRE_BALANCED_KEY,
                           row["rec"], row["vessel"]))
            _check_forbidden_reasons(block, snapshot, mismatches)

    mismatches_t = tuple(dict.fromkeys(mismatches))
    armed_mismatches = mismatches_t if armed else ()
    if armed:
        status = STATUS_PASS if not armed_mismatches else STATUS_FAIL
    else:
        status = STATUS_REPORT
    return GhostLifecycleResult(
        status=status, gating=bool(armed), mismatches=mismatches_t,
        armed_mismatches=armed_mismatches, observed=observed, blocks=blocks,
        armed_blocks=armed, parsed=parsed, parse_error=parse_error,
        # Recorded on EVERY path, including the unreadable one: a control flight
        # that lost its log still has to say which assertion it was carrying.
        declared=declared_ghost_lifecycle_block(expectations))
