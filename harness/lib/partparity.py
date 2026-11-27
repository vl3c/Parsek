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
the thresholds re-pin with no flight. Six things are judged:

  * the HOST (design 4.1): the C# host-integrity guard stopped the sweep because the
    host vessel lost parts, left its orbit or vanished (``progress.hostDamaged``). The
    unrun cases are the instrument's fault, never part findings:
    ``INVALID(parity-host-damaged)``, ahead of every other gate. Gates from the start.

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
  * the STATE PRESS (design D.6, a state run only): per family, when more than half of
    its cases end ``action-failed`` or ``state-timeout``, the press / wait / poll path of
    that family is broken, not its ghosts: ``INVALID(parity-state-press)``, after the
    controls gate and before the recompute.

A state case (design D.2, D.5, D.6: any state but ``stowed``) has two judged routes, the
event route (the view measures and ``structure``) and the start route (each view's
``start`` block and ``structureStart``); its verdict is the worse of the two, each reason
prefixed by its route. The stowed reference row (``stowedRef``) is not judged; it only
yields the report-only flag ``state-invisible``. A stowed case is judged exactly as
before P6.

An FX case (design Appendix E, an ``fx`` block: engine plumes, RCS puffs, heat glow, lamps on
a plate) is a state case whose stochastic effects are judged from accumulated 8-bit tiles on
top of its body routes (``judge_fx``), recomputed from its FX atlas, and whose Waterfall
cases read ``reference``.

Stdlib only, no I/O: the caller lists the directory, parses the JSON and hands a PNG
reader in (``run.py`` passes ``harness/tools/gui_mirror.py``'s ``read_png``).
"""

from __future__ import annotations

import math
import random
import re

import slots
from dataclasses import dataclass, field, replace
from typing import Any, Callable, Collection, Dict, FrozenSet, Iterable, List, Optional, Sequence, Tuple

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
    "stateProof", "structureStart", "stowedCaseId",
    "fx", "fxAtlas", "strip",
    "burst", "dust",
    "motion", "motionAtlas",
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
    "start", "stowedRef",
)
# A state case's view blocks (design D.2): `start` carries the full measure set of a view
# (real vs ghost-start), `stowedRef` exactly the shape keys the recompute checks for it.
START_KEYS: Tuple[str, ...] = VIEW_KEYS[1:-2]
STOWED_REF_KEYS: Tuple[str, ...] = (
    "realPx", "ghostPx", "realOnlyPx", "ghostOnlyPx", "realOnlyTolPx", "ghostOnlyTolPx",
    "realOnlyBlobPx", "ghostOnlyBlobPx", "realOnlyBlobPct", "ghostOnlyBlobPct",
)
STATE_PROOF_KEYS: Tuple[str, ...] = (
    "family", "module", "moduleIndex", "state", "action", "eventVisible", "actionResult",
    "reached", "waitS", "endpoint", "events", "ghostEvent", "ghostStart",
    "startFirstFrame", "sunTracker", "canopy", "nodes",
)
ENDPOINT_KEYS: Tuple[str, ...] = ("field", "value")
STATE_EVENT_KEYS: Tuple[str, ...] = ("type", "ut", "pid", "midx")
START_FIRST_FRAME_KEYS: Tuple[str, ...] = ("atTarget", "trackerDeg")
SUN_TRACKER_KEYS: Tuple[str, ...] = ("targetDeg", "eventDeg", "startFirstFrameDeg")
CANOPY_KEYS: Tuple[str, ...] = ("tiltDeg", "wobbleDeg", "transform")
CONTEXT_NODE_KEYS: Tuple[str, ...] = ("node", "part")
STRUCTURE_KEYS: Tuple[str, ...] = (
    "realRenderers", "ghostRenderers", "pairs", "missingOnGhost", "extraOnGhost",
    "transformDiffers", "materialDiffers", "meshDiffers", "renderedDiffers",
)
RENDERER_REF_KEYS: Tuple[str, ...] = ("key", "mesh", "rendered", "nonVisual")
# One structure.transformDiffers entry (PartParityJson.TransformDiffKeys). The canopy lever
# arm is written on an air case's canopy renderers only (null elsewhere, and absent from
# sidecars written before it existed).
TRANSFORM_DIFF_KEYS: Tuple[str, ...] = (
    "key", "positionDeltaM", "rotationDeltaDeg", "scaleDeltaPct", "canopyPivotDistanceM",
)
CANOPY_PIVOT_DISTANCE_KEY = "canopyPivotDistanceM"
SETUP_KEYS: Tuple[str, ...] = ("status", "detail")
CONTROL_KEYS: Tuple[str, ...] = ("id", "subject", "required", "outcome", "detail")
CASE_SUMMARY_KEYS: Tuple[str, ...] = (
    "index", "caseId", "part", "title", "source", "skin", "state", "context", "sidecar",
    "setupStatus", "maxShapeBlobPx", "maxColorBlobPx", "minIou", "missingRendered",
    "extraRendered", "family", "actionResult", "reached",
)
PROGRESS_KEYS: Tuple[str, ...] = (
    "complete", "cases", "completeCases", "setupFaults", "controls", "crashed",
    "hostDamaged", "hostDamage", "hostDamageDetail",
)
# PartParityHostIntegrity: the host damage reasons the manifest's progress.hostDamage
# names, and the label files that carry the guard across steps.
HOST_DAMAGE_REASONS: Tuple[str, ...] = ("gone", "parts", "situation")
HOST_BASELINE_FILE = "host.txt"
HOST_DAMAGE_FILE = "hostdamage.txt"
# host.txt's `mode=` for an air run's baseline (absent = an orbit run).
HOST_MODE_AIR = "air"

# TestCommandPartParitySweep: setup statuses, IsSetupFault, the control ids and the
# controls-state tokens.
SETUP_OK = "ok"
SETUP_SKIN_NOT_APPLIED = "skin-not-applied"
SETUP_SPAWN_TIMEOUT = "spawn-timeout"
SETUP_STILL_ANIMATING = "still-animating"
SETUP_DIED_SETTLING = "died-settling"
SETUP_CRASHED = "crashed"
# A real depth-mask module the capture's repair could not fix (design 4.2 step 0): the real
# picture is not the part a player sees, so the case is a setup fault (``error``), like
# ``skin-not-applied``; the sidecar also carries ``depth-mask-unrepaired:<count>``.
SETUP_DEPTH_MASK_UNREPAIRED = "depth-mask-unrepaired"
SETUP_ACTION_FAILED = "action-failed"
SETUP_STATE_TIMEOUT = "state-timeout"
# P8 (design Appendix E.5): a plate case whose real side lit too little of the plate, and an
# rcs case whose fired nozzles differ from the prediction. Both are setup faults (``error``).
SETUP_PLATE_NOT_LIT = "plate-not-lit"
SETUP_RCS_INPUT_MISMATCH = "rcs-input-mismatch"
# P9 (design Appendix G.4): a burst case whose real side drew nothing it was meant to, and a
# dust case whose stock raycast never hit a surface. Both are setup faults (``error``).
SETUP_BURST_NOT_TRIGGERED = "burst-not-triggered"
SETUP_DUST_NO_STOCK_HIT = "dust-no-stock-hit"
SETUP_STATUSES: Tuple[str, ...] = (
    SETUP_OK, SETUP_SKIN_NOT_APPLIED, SETUP_SPAWN_TIMEOUT, SETUP_STILL_ANIMATING,
    SETUP_DIED_SETTLING, SETUP_CRASHED, SETUP_DEPTH_MASK_UNREPAIRED, SETUP_ACTION_FAILED,
    SETUP_STATE_TIMEOUT, SETUP_PLATE_NOT_LIT, SETUP_RCS_INPUT_MISMATCH,
    SETUP_BURST_NOT_TRIGGERED, SETUP_DUST_NO_STOCK_HIT,
)
# The stowed run's eight controls (ControlIds): a run without states= / contexts=.
CONTROL_IDS: Tuple[str, ...] = (
    "ctl-empty", "ctl-ghost-absent", "ctl-real-vs-real", "ctl-ghost-scale90",
    "ctl-ghost-rot90", "ctl-ghost-flip", "ctl-ghost-missing-mesh", "ctl-wrong-skin",
)
CONTROL_STATE_EVENTS_DROPPED = "ctl-state-events-dropped"
CONTROL_STATE_EVENTS_DROPPED_AIR = "ctl-state-events-dropped-air"
# OrbitStateControlIds / AirControlIds (design D.2).
CONTROL_IDS_ORBIT_STATE: Tuple[str, ...] = CONTROL_IDS + (CONTROL_STATE_EVENTS_DROPPED,)
CONTROL_IDS_AIR: Tuple[str, ...] = (
    "ctl-empty", "ctl-ghost-absent", "ctl-real-vs-real", CONTROL_STATE_EVENTS_DROPPED_AIR,
)
# The FX run's controls (FxOrbitControlIds / FxAirControlIds, design E.5).
CONTROL_IDS_FX_ORBIT: Tuple[str, ...] = (
    "ctl-empty", "ctl-ghost-absent", "ctl-fx-real-vs-real", "ctl-fx-plume-off",
    "ctl-fx-plume-short", "ctl-fx-plume-hue", "ctl-fx-plume-shift", "ctl-fx-smoke-dropped",
    "ctl-heat-events-dropped", "ctl-plate-light-off", "ctl-plate-light-off-point",
)
CONTROL_IDS_FX_AIR: Tuple[str, ...] = (
    "ctl-empty", "ctl-ghost-absent", "ctl-fx-real-vs-real-air", "ctl-fx-plume-off-air",
)
# The burst and dust run's controls (BurstControlIds / DustControlIds, design G.4).
CONTROL_IDS_BURST: Tuple[str, ...] = (
    "ctl-burst-empty", "ctl-burst-real-vs-real", "ctl-burst-off", "ctl-burst-late",
    "ctl-burst-power", "ctl-burst-shift", "ctl-burst-hue",
)
CONTROL_IDS_DUST: Tuple[str, ...] = ("ctl-dust-real-vs-real", "ctl-dust-off", "ctl-dust-above-reach")
# The motion run's controls (MotionOrbitControlIds / MotionAirControlIds, Appendix H.3).
CONTROL_MOTION_REAL_VS_REAL = "ctl-motion-real-vs-real"
CONTROL_MOTION_GHOST_SLOW = "ctl-motion-ghost-slow"
CONTROL_MOTION_GHOST_LATE = "ctl-motion-ghost-late"
CONTROL_MOTION_REAL_VS_REAL_AIR = "ctl-motion-real-vs-real-air"
CONTROL_IDS_MOTION_ORBIT: Tuple[str, ...] = (
    "ctl-empty", "ctl-ghost-absent", "ctl-real-vs-real", CONTROL_MOTION_REAL_VS_REAL,
    CONTROL_MOTION_GHOST_SLOW, CONTROL_MOTION_GHOST_LATE,
)
CONTROL_IDS_MOTION_AIR: Tuple[str, ...] = (
    "ctl-empty", "ctl-ghost-absent", CONTROL_MOTION_REAL_VS_REAL_AIR,
)
# OptionalControlIds: no subject on the install records ``n/a`` (ControlsNotApplicable),
# never ``bad``.
OPTIONAL_CONTROL_IDS: Tuple[str, ...] = ("ctl-plate-light-off-point",)
# KnownDifferenceControlIds: ``n/a`` while a known ghost difference stands (G.4, G.8 item 3).
KNOWN_DIFFERENCE_CONTROL_IDS: Tuple[str, ...] = ("ctl-dust-above-reach",)
ALL_CONTROL_IDS: Tuple[str, ...] = (
    CONTROL_IDS_ORBIT_STATE + (CONTROL_STATE_EVENTS_DROPPED_AIR,)
    + CONTROL_IDS_FX_ORBIT[2:] + CONTROL_IDS_FX_AIR[2:]
    + CONTROL_IDS_BURST + CONTROL_IDS_DUST
    + CONTROL_IDS_MOTION_ORBIT[3:] + CONTROL_IDS_MOTION_AIR[2:]
)

# The states= / contexts= selection (TestCommandPartParitySweep, PartParityStateRules) and
# the manifest settings a run records it under.
ARG_NONE = "none"
STATES_ALL = "all"
SETTING_STATES = "states"
SETTING_CONTEXTS = "contexts"
SETTING_CONTROL_SET = "controlSet"
# TestCommandPartParitySweep.SettingFx: written by an FX run only; absent reads ``none``.
SETTING_FX = "fx"
# SettingMotion / SettingFrames: written by a motion run only (Appendix H.2).
SETTING_MOTION = "motion"
SETTING_FRAMES = "frames"
STATE_STOWED = "stowed"
KNOWN_STATES: Tuple[str, ...] = (
    "stowed", "deployed", "open", "lit", "semi", "full", "jettisoned", "separated",
)
CONTEXT_ALONE = "alone"
CONTEXT_STACKED = "stacked"
CONTEXT_AIR = "air"
# The class word of the FGV contexts (PartParityStateRules.ContextFgv / ContextClass): a
# contexts= csv names it to select every fgv-<n>-<name> context (design D.10 S8).
CONTEXT_FGV = "fgv"
CONTEXT_FGV_PREFIX = "fgv-"
KNOWN_CONTEXTS: Tuple[str, ...] = (CONTEXT_ALONE, CONTEXT_STACKED, CONTEXT_AIR, CONTEXT_FGV)
ORBIT_CONTEXTS: Tuple[str, ...] = (CONTEXT_ALONE, CONTEXT_STACKED, CONTEXT_FGV)
# FGV-1's six configurations as contexts (PartParityStateRules.FairingContexts), FGV's order.
FAIRING_CONTEXTS: Tuple[str, ...] = (
    "fgv-1-plain-capped", "fgv-2-interstage-truss", "fgv-3-open-top", "fgv-4-colour-variant",
    "fgv-5-engine-plate", "fgv-6-engine-shroud",
)


def context_class(context: Optional[str]) -> Optional[str]:
    """PartParityStateRules.ContextClass: ``fgv`` for every ``fgv-<n>-<name>`` context,
    the context itself otherwise."""
    if isinstance(context, str) and context.startswith(CONTEXT_FGV_PREFIX):
        return CONTEXT_FGV
    return context


def is_context_case(context: Optional[str]) -> bool:
    """A context that authors parts beside the subject and may plan a stowed case with no
    press: the stacked shroud context and the FGV contexts (not alone, not air)."""
    return context_class(context) in (CONTEXT_STACKED, CONTEXT_FGV)
STATE_TIEBREAK_PREFIX = "-m"


def _setting(settings: Optional[Dict], key: str) -> str:
    value = settings.get(key) if isinstance(settings, dict) else None
    return value if isinstance(value, str) and value else ARG_NONE


def is_air_run(settings: Optional[Dict]) -> bool:
    """TestCommandPartParitySweep.IsAirRun over a manifest's ``settings``: ``contexts=air``."""
    return CONTEXT_AIR in _setting(settings, SETTING_CONTEXTS).split(",")


def is_fx_run(settings: Optional[Dict]) -> bool:
    """TestCommandPartParitySweep.IsFxRun over a manifest's ``settings``: ``fx`` other than
    ``none`` (a manifest written before P8 has no ``fx`` and reads ``none``)."""
    return _setting(settings, SETTING_FX) != ARG_NONE


def _first_fx_word(settings: Optional[Dict]) -> Optional[str]:
    fx = _setting(settings, SETTING_FX)
    return None if fx in (ARG_NONE, FX_ARG_ALL) else fx.split(",")[0]


def is_burst_run(settings: Optional[Dict]) -> bool:
    """TestCommandPartParitySweep.IsBurstRun: ``fx`` of burst words (one kind per run)."""
    return _first_fx_word(settings) in FX_BURST_ARG_WORDS


def is_dust_run(settings: Optional[Dict]) -> bool:
    """TestCommandPartParitySweep.IsDustRun: ``fx=dust``."""
    return _first_fx_word(settings) in FX_DUST_ARG_WORDS


def is_ground_run(settings: Optional[Dict]) -> bool:
    """TestCommandPartParitySweep.IsGroundRun: ``contexts=ground``."""
    return CONTEXT_GROUND in _setting(settings, SETTING_CONTEXTS).split(",")


def is_motion_run(settings: Optional[Dict]) -> bool:
    """TestCommandPartParitySweep.IsMotionRun over a manifest's ``settings``: ``motion``
    other than ``none`` (a manifest written before P7 has no ``motion`` and reads ``none``)."""
    return _setting(settings, SETTING_MOTION) != ARG_NONE


def control_set(settings: Optional[Dict]) -> Tuple[str, ...]:
    """TestCommandPartParitySweep.ControlSetFor, recomputed from a manifest's ``settings``
    (design D.2, E.5, G.4, H.3): a burst or dust run its own set; a motion run its motion
    set (the air one for ``contexts=air``); an FX run its FX set (the air one for
    ``contexts=air``); no ``states`` / ``contexts`` (absent or ``none``, as every manifest
    written before P6 reads) is the stowed eight; an air run its four; any other state run
    the eight plus ``ctl-state-events-dropped``."""
    if is_burst_run(settings):
        return CONTROL_IDS_BURST
    if is_dust_run(settings):
        return CONTROL_IDS_DUST
    if is_motion_run(settings):
        return CONTROL_IDS_MOTION_AIR if is_air_run(settings) else CONTROL_IDS_MOTION_ORBIT
    if is_fx_run(settings):
        return CONTROL_IDS_FX_AIR if is_air_run(settings) else CONTROL_IDS_FX_ORBIT
    if (_setting(settings, SETTING_STATES) == ARG_NONE
            and _setting(settings, SETTING_CONTEXTS) == ARG_NONE):
        return CONTROL_IDS
    return CONTROL_IDS_AIR if is_air_run(settings) else CONTROL_IDS_ORBIT_STATE


def is_state_case(state: Optional[str]) -> bool:
    """TestCommandPartParitySweep.IsStateCase: any state but ``stowed`` (four atlas rows)."""
    return (state or STATE_STOWED) != STATE_STOWED


# The transformation families (PartParityStateRules.Families, design D.1 table order):
# the press gate's family table (design D.6) and the viewer's state order.
FAMILIES: Tuple[str, ...] = (
    "deployable", "gear", "ladder", "animation-group", "cargo-bay", "animate-generic",
    "airbrake", "light", "cabin-light", "parachute", "jettison", "fairing",
)
# The setup statuses the press gate counts per family (PartParityStateRules.PressGateStatuses).
PRESS_GATE_STATUSES: Tuple[str, ...] = (SETUP_ACTION_FAILED, SETUP_STATE_TIMEOUT)
# The exclusion reasons a state run writes (PartParityStateRules.ExclusionReasons).
STATE_EXCLUSION_REASONS: Tuple[str, ...] = (
    "ground-deploy", "no-pose", "fairing-bay", "bay-deploy-not-animated", "cs-deploy-sign",
    "jettison-blocked-by-node", "jettison-node-missing", "p7-robotics", "p8-heat", "p8-fx",
    "not-polled", "no-animation", "fairing-no-context", "auto-deploy",
)
# stateProof.actionResult and the two ghost routes' outcomes (design D.2, D.5).
ACTION_RESULTS: Tuple[str, ...] = ("invoked", "missing", "threw", "refused")
GHOST_OUTCOME_APPLIED = "applied"
GHOST_OUTCOMES: Tuple[str, ...] = (GHOST_OUTCOME_APPLIED, "no-info", "pose-not-sampled", "not-reached")
ROUTE_EVENT = "event"
ROUTE_START = "start"
# The flags and tags a state case writes (design D.3 to D.6).
FLAG_STATE_NOT_MODELLED_PREFIX = "state-not-modelled:"
FLAG_STATE_NOT_RECORDED = "state-not-recorded"
FLAG_START_POSE_NOT_FIRST_FRAME = "start-pose-not-first-frame"
FLAG_STATE_INVISIBLE = "state-invisible"
FLAG_SUN_TRACKER_FIRST_FRAME_PREFIX = "sun-tracker-first-frame:"
TAG_STILL_TRACKING = "still-tracking"
TAG_CANOPY_WOBBLE_PREFIX = "canopy-wobble:"
TAG_CONTEXT_HIDDEN_PREFIX = "context-hidden:"
TAG_JETTISON_DEBRIS_HIDDEN_PREFIX = "jettison-debris-hidden:"
CONTROLS_OK = "ok"
CONTROLS_BAD = "bad"
CONTROLS_PENDING = "pending"
CONTROLS_OFF = "off"
CONTROLS_NOT_APPLICABLE = "n/a"
# PartParityJson.StowedAtlasRows / StateAtlasRows (AtlasRowsFor).
ATLAS_ROWS: Tuple[str, ...] = ("real", "ghost")
ATLAS_ROWS_STATE: Tuple[str, ...] = ("real", "ghost", "ghost-start", "ghost-stowed")
# PartParityJson.ReloadedStateAtlasRows: the real part as a load leaves its glow ramp, which
# the ``start`` blocks are measured against (E.4, "Start-route reload").
ROW_REAL_START = "real-start"
ATLAS_ROWS_STATE_RELOADED: Tuple[str, ...] = ATLAS_ROWS_STATE + (ROW_REAL_START,)
# PartParityRampHold.FlagReloadedPrefix: written exactly when that row is.
FLAG_START_RELOADED_PREFIX = "start-route-reloaded:"


def atlas_rows_for(state_case: bool, start_reloaded: bool = False) -> Tuple[str, ...]:
    """PartParityJson.AtlasRowsFor: four rows for a state case (five with a reloaded start
    route), two for a stowed one."""
    if not state_case:
        return ATLAS_ROWS
    return ATLAS_ROWS_STATE_RELOADED if start_reloaded else ATLAS_ROWS_STATE


def start_reloaded(sidecar: Optional[Dict]) -> bool:
    """The case's flag says its start route was compared with the loaded real part."""
    flags = sidecar.get("flags") if isinstance(sidecar, dict) else None
    return any(isinstance(f, str) and f.startswith(FLAG_START_RELOADED_PREFIX)
               for f in (flags if isinstance(flags, list) else []))

# PartParityMetrics: coverage is alpha >= 0.5, i.e. the alpha byte >= 128.
COVERAGE_ALPHA_BYTE = 128


def is_setup_fault(status: Optional[str]) -> bool:
    """TestCommandPartParitySweep.IsSetupFault: anything but ``ok`` and
    ``still-animating`` (a still-animating case is captured and measured anyway)."""
    return status not in (SETUP_OK, SETUP_STILL_ANIMATING)


# ---------------------------------------------------------------------------
# The P8 FX contract (design Appendix E, piece F1), mirrored from PartParityFxRules.cs,
# PartParityJson.cs and TestCommandPartParitySweep.cs (PartParitySourceSyncTests'
# test_the_fx_* cells). Every constant the FX measures (F1b) and the FX judge (F4) read
# lives here, so neither adds a mirrored literal.
# ---------------------------------------------------------------------------

# Families (E.1 order), the fx= words (``plate`` selects the lamps) and the state words.
FX_FAMILIES: Tuple[str, ...] = ("plume", "rcs", "heat", "light")
FX_STOCHASTIC_FAMILIES: Tuple[str, ...] = ("plume", "rcs")
FX_ARG_WORDS: Tuple[str, ...] = ("plume", "rcs", "heat", "plate")
# Every fx= word a run may name: those of ``all``, then the ones it asks for by name.
FX_ARG_KNOWN_WORDS: Tuple[str, ...] = FX_ARG_WORDS + ("plume-main",)
FX_ARG_ALL = "all"
# The plume cases on the vessel's main throttle (PartParityFxOption.MainThrottle); every other
# plume case runs on the engine's independent throttle with the vessel's at 0.
FX_MAIN_STATES: Tuple[str, ...] = ("main-full", "main-low")
FX_STATES: Tuple[str, ...] = ("burn-full", "burn-low") + FX_MAIN_STATES + (
    "rcs-fire", "heat-medium", "heat-hot", "lit")
CONTEXT_PLATE = "plate"
FX_ORBIT_CONTEXTS: Tuple[str, ...] = (CONTEXT_ALONE, CONTEXT_PLATE)
FX_EXCLUSION_REASONS: Tuple[str, ...] = (
    "fx-reentry-camera", "fx-blackbody", "fx-ablator-char", "fx-needs-air", "fx-throttle-locked",
    "fx-no-light",
)
# fx.reference of a case judged reference-only (E.6).
REFERENCE_WATERFALL = "waterfall"


def fx_family_for_arg(word: Optional[str]) -> Optional[str]:
    """PartParityFxRules.FamilyForArg: ``plate`` selects the ``light`` family; each P9 word its
    own family."""
    return {"plume": "plume", "rcs": "rcs", "heat": "heat", "plate": "light",
            "plume-main": "plume",
            "explode": "explode", "vessel-explode": "vessel-explode",
            "burst-custom": "burst-custom", "decouple": "decouple", "dust": "dust"}.get(word or "")


# Views, layers and sides (E.3); the FX atlas's rows and columns (E.5).
FX_VIEWS: Tuple[str, ...] = ("fx-side-a", "fx-side-b", "fx-bottom", "fx-far")
FX_RENDERER_LAYERS: Tuple[str, ...] = ("body", "flame", "smoke")
FX_LAYERS: Tuple[str, ...] = ("flame", "smoke")
FX_SIDES: Tuple[str, ...] = ("real", "ghost", "ghostStart", "ghostAll")
FX_ATLAS_ROWS: Tuple[str, ...] = (
    "real-flame", "ghost-flame", "ghost-start-flame", "real-smoke", "ghost-smoke", "ghost-start-smoke",
)
FX_ATLAS_ROWS_RCS: Tuple[str, ...] = FX_ATLAS_ROWS + ("ghost-all-flame", "ghost-all-smoke")
STRIP_ROWS: Tuple[str, ...] = ("real", "ghost")
FX_ATLAS_SUFFIX = ".fx.png"
STRIP_SUFFIX = ".strip.png"
FX_STRIP_FRAMES = 12


def fx_atlas_rows_for(rcs_case: bool) -> Tuple[str, ...]:
    """PartParityJson.FxAtlasRowsFor: eight rows for an rcs case, six otherwise."""
    return FX_ATLAS_ROWS_RCS if rcs_case else FX_ATLAS_ROWS


# The FX key tables of PartParityJson.cs (every key always written, null when not measured).
FX_KEYS: Tuple[str, ...] = (
    "family", "module", "moduleIndex", "engineMode", "throttle", "drive", "warmupS", "plateau",
    "hold", "rcs", "samples", "framing", "layers", "cullingMask", "views", "particles",
    "structure", "inventory", "plate", "reference",
)
FX_DRIVE_KEYS: Tuple[str, ...] = (
    "ignited", "finalThrust", "currentThrottle", "recordedPower", "cheat", "mainThrottle", "independentThrottle",
)
# PartParityFxRules.ThrottleDoneTolerance, and the throttle each main-throttle word burns at
# (BurnFullThrottle, BurnLowThrottle).
FX_THROTTLE_DONE_TOLERANCE = 0.01
FX_MAIN_THROTTLES: Dict[str, float] = {"main-full": 1.0, "main-low": 0.3}
FX_SIDE_KEYS: Tuple[str, ...] = FX_SIDES
FX_PLATEAU_KEYS: Tuple[str, ...] = ("signal", "settled", "waitS")
FX_HOLD_KEYS: Tuple[str, ...] = ("maxDriftM", "stepM", "steps")
FX_RCS_KEYS: Tuple[str, ...] = ("input", "predicted", "fired", "nozzles", "ghostHidden", "powerRatio")
FX_FRAMING_KEYS: Tuple[str, ...] = ("near", "far", "clamped")
FX_LAYER_KEYS: Tuple[str, ...] = FX_RENDERER_LAYERS
FX_VIEW_KEYS: Tuple[str, ...] = (
    "view", "route", "layer", "width", "height", "axis", "real", "ghost", "compare", "splitHalf",
)
FX_AXIS_KEYS: Tuple[str, ...] = ("originPx", "dirPx", "nozzleDiameterPx")
FX_SIDE_MEASURE_KEYS: Tuple[str, ...] = (
    "corePx", "envelopePx", "saturatedPx", "originPx", "lengthPx", "widthPx", "coreCentroid",
    "meanColor", "chromaticity", "luminance", "hueHistogram",
)
FX_COMPARE_KEYS: Tuple[str, ...] = (
    "originDeltaPx", "lateralOffsetPx", "lengthRatio", "widthRatio", "directionDeg",
    "crossSectionAreaRatio", "crossSectionOffset", "chromaDistance", "hueL1", "brightnessRatio",
    "coreIou",
)
FX_SPLIT_HALF_KEYS: Tuple[str, ...] = ("even", "odd")
FX_HALF_KEYS: Tuple[str, ...] = ("samples", "real", "ghost", "compare")
FX_PARTICLE_KEYS: Tuple[str, ...] = ("count", "speed", "direction", "size", "alongP95", "acrossP95")
FX_STRUCTURE_KEYS: Tuple[str, ...] = ("fxMissingOnGhost", "fxExtraOnGhost", "fxDiffers", "lightsDiffer")
FX_PLATE_KEYS: Tuple[str, ...] = ("litPct", "lightType", "range", "spotAngle", "cullingMask")
FX_ATLAS_KEYS: Tuple[str, ...] = ("file", "rows", "cols", "tile")
FX_STRIP_KEYS: Tuple[str, ...] = ("file", "frames")

# The plateau signal vocabulary (E.2, E0 U5).
PLATEAU_SIGNAL_PARTICLES = "particles"
PLATEAU_SIGNAL_COVERAGE = "coverage"

# The measures (E.4), their reason tokens and the tags, flags and caps an FX case writes.
FX_MEASURES: Tuple[str, ...] = (
    "presence", "origin", "lateral", "length", "width", "direction", "cross-section", "colour",
    "brightness", "smoke", "core-iou",
)
FX_RCS_REPORT_ONLY_MEASURES: Tuple[str, ...] = ("length", "width", "brightness")
FX_REASON_FX_MISSING = "fx-missing"
FX_REASON_FX_EXTRA = "fx-extra"
FX_REASON_SMOKE_MISSING = "smoke-missing"
FX_REASON_SMOKE_EXTRA = "smoke-extra"
TAG_FX_NOT_PLATEAUED_PREFIX = "fx-not-plateaued:"
CAP_FX_UNSETTLED = "fx-unsettled"
FLAG_FX_FRAMING_CLAMPED = "fx-framing-clamped"
FLAG_RCS_KNOWN_DIFFERENCE_PREFIX = "rcs-known-difference:"

# The 8-bit tile rule and the measure constants (E.4), every one PartParityFxRules'.
FX_SAMPLES = 16
FX_MAX_SAMPLES = 255
FX_CORE_FRACTION = 0.5
FX_ALPHA_COVERED = 128
FX_FLAME_VALUE_MIN = 16
FX_SATURATED_VALUE = 255
FX_HUE_BINS = 12
FX_LUMINANCE_WEIGHTS: Tuple[float, float, float] = (0.2126, 0.7152, 0.0722)
FX_PRESENCE_MIN_PX = 64
FX_SMOKE_MIN_PX = 256
FX_SPLIT_HALF_PCT = 0.10
PLATE_LIT_MIN_PCT = 0.02
PLATE_LIT_MIN_LUM = 8

# The threshold defaults (starting guesses, re-pinned from the reading runs and the controls).
FX_THRESHOLDS: Dict[str, float] = {
    "originFailMinPx": 4.0, "originFailLengthFraction": 0.10,
    "lateralFailMinPx": 4.0, "lateralFailLengthFraction": 0.05, "offsetWarnFraction": 0.5,
    "lengthFailFraction": 0.25, "lengthWarnFraction": 0.125,
    "widthFailFraction": 0.30, "widthWarnFraction": 0.15,
    "directionFailDeg": 10.0, "directionWarnDeg": 5.0,
    "crossSectionAreaFailMin": 0.6, "crossSectionAreaFailMax": 1.6,
    "crossSectionAreaWarnMin": 0.8, "crossSectionAreaWarnMax": 1.25,
    "crossSectionOffsetFail": 0.1,
    "chromaFail": 0.06, "hueL1Fail": 0.5, "colourWarnFraction": 0.25,
    "brightnessWarnMin": 0.5, "brightnessWarnMax": 2.0, "coreIouWarnMin": 0.5,
}


def fx_tile_alpha(k: int, n: int) -> int:
    """PartParityFxRules.TileAlpha: ``floor(255 k / n + 0.5)``, in integers."""
    return 0 if n <= 0 or k <= 0 else (510 * k + n) // (2 * n)


def fx_covered_samples(alpha: int, n: int) -> int:
    """PartParityFxRules.CoveredSamples: ``round(alpha n / 255)``, exact for n up to 255."""
    return 0 if n <= 0 or alpha <= 0 else (2 * alpha * n + 255) // 510


def fx_tile_channel(total: int, k: int) -> int:
    """PartParityFxRules.TileChannel: the covered samples' mean, ``floor(sum / k + 0.5)``."""
    return 0 if k <= 0 else (2 * total + k) // (2 * k)


def fx_is_core(k: int, n: int) -> bool:
    """PartParityFxRules.IsCore: covered in at least half the samples."""
    return n > 0 and k > 0 and 2 * k >= n


def fx_sample_covered(layer: str, r: int, g: int, b: int, a: int) -> bool:
    """PartParityFxRules.SampleCovered: a flame sample by its max channel, a smoke one by alpha."""
    if layer == "flame":
        return max(r, g, b) >= FX_FLAME_VALUE_MIN
    return a >= FX_ALPHA_COVERED


def fx_is_saturated(r: int, g: int, b: int) -> bool:
    """PartParityFxRules.IsSaturated: a clipped channel (colour and brightness skip the pixel)."""
    return r >= FX_SATURATED_VALUE or g >= FX_SATURATED_VALUE or b >= FX_SATURATED_VALUE


def fx_luminance(r: int, g: int, b: int) -> float:
    """PartParityFxRules.Luminance: Rec. 709 over the 8-bit colour."""
    wr, wg, wb = FX_LUMINANCE_WEIGHTS
    return wr * r + wg * g + wb * b


def fx_reason(route: str, view: str, layer: str, reason: str) -> str:
    """PartParityFxRules.FormatReason: ``<route>:<view>:<layer>:<reason>``."""
    return "%s:%s:%s:%s" % (route or "", view or "", layer or "", reason or "")


# ---------------------------------------------------------------------------
# The P9 burst and dust contract (design Appendix G, piece X1), mirrored from
# PartParityFxRules.Burst.cs, PartParityJson.cs and TestCommandPartParitySweep.cs
# (PartParitySourceSyncTests' test_the_burst_* cells). Every constant the burst measures
# (X3) and the burst judge (X5) read lives here, so neither adds a mirrored literal.
# ---------------------------------------------------------------------------

# Families (G.7 order) and the fx= words (each selects its own family).
BUILT_FAMILIES: Tuple[str, ...] = ("explode", "vessel-explode", "burst-custom", "decouple", "dust")
BURST_FAMILIES: Tuple[str, ...] = ("explode", "vessel-explode", "burst-custom", "decouple")
DUST_FAMILIES: Tuple[str, ...] = ("dust",)
FX_BURST_ARG_WORDS: Tuple[str, ...] = ("explode", "vessel-explode", "burst-custom", "decouple")
FX_DUST_ARG_WORDS: Tuple[str, ...] = ("dust",)
# Every word an fx= csv may name (KnownArgWords); fx=all stays FX_ARG_WORDS (P8).
FX_KNOWN_ARG_WORDS: Tuple[str, ...] = FX_ARG_KNOWN_WORDS + FX_BURST_ARG_WORDS + FX_DUST_ARG_WORDS

# State words; a stack is vessel-explode-x<n>, a custom explosion custom-l<L>.
BUILT_STATES: Tuple[str, ...] = (
    "explode", "puff", "decouple", "joint-break", "dust-low", "dust-high", "dust-water",
)
STATE_VESSEL_EXPLODE_PREFIX = "vessel-explode-x"
STATE_CUSTOM_EXPLOSION_PREFIX = "custom-l"
# The dust run's context (not a P6 context: KNOWN_CONTEXTS does not hold it) and the burst one.
CONTEXT_GROUND = "ground"
CONTEXT_BURST = CONTEXT_AIR
# Every context a contexts= csv may name (TestCommandPartParitySweep.KnownRunContexts).
KNOWN_RUN_CONTEXTS: Tuple[str, ...] = KNOWN_CONTEXTS + (CONTEXT_GROUND,)

# The ground context's slots (G.2.2): heights as fractions of each part's maxDistance.
SURFACE_SLOT_TERRAIN = "terrain"
SURFACE_SLOT_WATER = "water"
DUST_SLOT_STATES: Tuple[str, ...] = ("dust-low", "dust-high", "dust-water")
DUST_SLOT_SURFACES: Tuple[str, ...] = (SURFACE_SLOT_TERRAIN, SURFACE_SLOT_TERRAIN, SURFACE_SLOT_WATER)
DUST_SLOT_FRACTIONS: Tuple[float, ...] = (0.2, 0.7, 0.4)
DUST_ABOVE_REACH_FRACTION = 1.2
# ModuleSurfaceFX's stock field defaults and the pad's FX tag.
SURFACE_FX_DEFAULT_THRUST_PROVIDER_MODULE_INDEX = -1
SURFACE_FX_DEFAULT_FX_MAX = 1.0
SURFACE_FX_DEFAULT_MAX_DISTANCE_M = 50.0
SURFACE_FX_DEFAULT_FALLOFF = 2.0
LAUNCHPAD_FX_TAG = "LaunchpadFX"
DUST_SURFACES: Tuple[str, ...] = ("terrain", "water", "launchpad", "none")
DUST_SURFACE_NONE = "none"

# Part facts (stock's Part / ModuleDecouplerBase field defaults).
DEFAULT_EXPLOSION_POTENTIAL = 0.5
DEFAULT_DECOUPLE_FX_GROUP = "decouple"

# The burst subjects (G.2.1, G.5).
VESSEL_EXPLODE_STACK_PARTS: Tuple[str, ...] = ("fuelTank", "fuelTankSmallFlat")
VESSEL_EXPLODE_STACK_SIZES: Tuple[int, ...] = (1, 2, 3, 6)
CUSTOM_EXPLOSION_LENGTHS_M: Tuple[float, ...] = (1.0, 5.0, 10.0, 15.0, 20.0)
TERMINAL_EXPLOSION_LENGTH_DIVISOR_M = 20.0
BURST_CUSTOM_PART = "parsek.customExplosion"
JOINT_BREAK_PARTS: Tuple[str, ...] = ("fuelTank", "trussPiece1x")
STAND_IN_CUSTOM = "custom"
STAND_IN_PUFF = "puff"

# The burst window, bins and trials (G.2.1).
BURST_WINDOW_S = 4.0
DECOUPLE_BURST_WINDOW_S = 2.0
BURST_BIN_S = 0.25
BURST_TRIALS = 4
BURST_TRIAL_SPACING_M = 40.0
BURST_GHOST_SEPARATION_M = 20.0
FXMONGER_MERGE_DISTANCE_M = 10.0
BURST_TAG_RADIUS_M = 10.0

# Views and layers (G.2.1, G.2.2).
BURST_VIEWS: Tuple[str, ...] = ("burst-side", "burst-top", "burst-far")
DUST_VIEWS: Tuple[str, ...] = ("dust-side", "dust-top", "dust-far")
DUST_AXIS_VIEW = "dust-top"
LAYER_DUST = "dust"
BURST_LAYERS: Tuple[str, ...] = ("flame", "smoke")

# The measures (G.3), their reasons and lines.
BURST_MEASURES: Tuple[str, ...] = (
    "presence", "onset", "peak-time", "duration", "peak-area", "radius", "centroid-offset",
    "colour", "brightness", "smoke", "schedule",
)
DUST_MEASURES: Tuple[str, ...] = (
    "presence", "origin", "lateral", "length", "width", "cross-section", "colour", "brightness",
)
BURST_REASON_MISSING = "burst-missing"
BURST_REASON_EXTRA = "burst-extra"
DUST_REASON_MISSING = "dust-missing"
DUST_REASON_EXTRA_PREFIX = "dust-extra:"
# The G.3 lines (starting guesses). Bin deltas fail over their fail line and warn at or over
# their warn line; the rest fail / warn outside their band or over their line.
BURST_THRESHOLDS: Dict[str, float] = {
    "burstOnsetFailBins": 1, "burstPeakTimeFailBins": 2, "burstPeakTimeWarnBins": 1,
    "burstDurationFailFraction": 0.40, "burstDurationWarnFraction": 0.20,
    "burstPeakAreaFailMin": 0.6, "burstPeakAreaFailMax": 1.6,
    "burstPeakAreaWarnMin": 0.8, "burstPeakAreaWarnMax": 1.25,
    "burstRadiusPercentile": 0.95,
    "burstRadiusFailMin": 0.7, "burstRadiusFailMax": 1.4,
    "burstRadiusWarnMin": 0.85, "burstRadiusWarnMax": 1.18,
    "burstCentroidOffsetFail": 0.3, "burstCentroidOffsetWarn": 0.15,
    "burstSchedulePositionToleranceM": 1.0, "burstSchedulePowerEpsilon": 0.0001,
}
LEVEL_OK = "ok"
LEVEL_WARN = "warn"
LEVEL_FAIL = "fail"
BUILT_EXCLUSION_REASONS: Tuple[str, ...] = (
    "decouple-no-fx-group", "dust-no-thrust-provider", "burst-subject-absent",
)

# The controls (G.4).
BURST_CONTROL_PARTS: Tuple[str, ...] = ("fuelTank", "fuelTankSmallFlat")
DUST_CONTROL_PARTS: Tuple[str, ...] = ("liquidEngine2.v2",)
DUST_ABOVE_REACH_GHOST_JUDGED = False
BURST_LATE_CONTROL_S = 0.5
BURST_SHIFT_CONTROL_M = 3.0
BURST_POWER_CONTROL_INDEX_STEP = 1

# The burst and dust sidecar blocks (PartParityJson), every key always written.
BURST_KEYS: Tuple[str, ...] = (
    "family", "module", "moduleIndex", "potential", "stackSize", "standIn", "lengthM", "fxGroup",
    "windowS", "binS", "bins", "trials", "samples", "trigger", "schedule", "scheduleCompare",
    "atlas", "views", "structure",
)
BURST_SIDE_KEYS: Tuple[str, ...] = ("real", "ghost")
BURST_TRIGGER_KEYS: Tuple[str, ...] = ("frame", "ut", "realTagged", "ghostTagged", "ghostEvent")
BURST_SCHEDULE_ENTRY_KEYS: Tuple[str, ...] = ("frame", "t", "offsetM", "power", "prefab", "index")
BURST_SCHEDULE_COMPARE_KEYS: Tuple[str, ...] = (
    "realCount", "ghostCount", "countDiffers", "indexDiffers", "powerDiffers", "maxOffsetDeltaM",
)
BURST_ATLAS_KEYS: Tuple[str, ...] = ("file", "rows", "views", "bins", "tile")
BURST_VIEW_KEYS: Tuple[str, ...] = (
    "view", "layer", "width", "height", "source", "real", "ghost", "compare", "splitHalf",
)
BURST_SOURCE_KEYS: Tuple[str, ...] = ("realPx", "ghostPx", "diameterPx")
BURST_SIDE_MEASURE_KEYS: Tuple[str, ...] = ("bins", "onsetBin", "peakBin", "lastCoreBin", "peakCorePx")
BURST_BIN_MEASURE_KEYS: Tuple[str, ...] = (
    "corePx", "envelopePx", "saturatedPx", "coreCentroid", "radiusPx", "meanColor", "chromaticity",
    "luminance", "hueHistogram",
)
BURST_COMPARE_KEYS: Tuple[str, ...] = (
    "presence", "onsetDeltaBins", "peakTimeDeltaBins", "durationDelta", "peakAreaRatio", "radiusRatio",
    "centroidOffset", "chromaDistance", "hueL1", "brightnessRatio", "chromaDistanceMean", "hueL1Mean",
)
BURST_HALF_KEYS: Tuple[str, ...] = ("trials", "samples", "real", "ghost", "compare")
BURST_ATLAS_ROWS: Tuple[str, ...] = ("real-flame", "ghost-flame", "real-smoke", "ghost-smoke")
DUST_KEYS: Tuple[str, ...] = (
    "family", "module", "moduleIndex", "engineModule", "engineModuleIndex", "surface", "slotFraction",
    "heightM", "maxDistanceM", "falloff", "fxMax", "throttle", "drive", "warmupS", "plateau", "hold",
    "samples", "raycast", "framing", "layers", "atlas", "views", "structure",
)
DUST_RAYCAST_KEYS: Tuple[str, ...] = (
    "surface", "hits", "misses", "distanceM", "scaledFx", "pointM", "normal", "samples",
)
DUST_RAYCAST_SAMPLE_KEYS: Tuple[str, ...] = ("hit", "surface", "distanceM", "scaledFx")
DUST_ATLAS_ROWS: Tuple[str, ...] = ("real-dust", "ghost-dust", "real-flame", "ghost-flame")
BURST_ATLAS_SUFFIX = ".burst.png"
DUST_ATLAS_SUFFIX = ".dust.png"


def is_burst_family(family: Optional[str]) -> bool:
    return family in BURST_FAMILIES


def is_dust_family(family: Optional[str]) -> bool:
    return family in DUST_FAMILIES


def vessel_explode_state(parts: int) -> str:
    """PartParityFxRules.VesselExplodeState."""
    return "%s%d" % (STATE_VESSEL_EXPLODE_PREFIX, parts)


def burst_window_for(family: Optional[str]) -> float:
    """PartParityFxRules.BurstWindowFor: 2 s for a decouple case, 4 s for every other burst."""
    return DECOUPLE_BURST_WINDOW_S if family == "decouple" else BURST_WINDOW_S


def burst_bin_count(window_s: float) -> int:
    """PartParityFxRules.BurstBinCount: 16 bins for 4 s, 8 for 2 s."""
    if window_s is None or window_s != window_s or window_s <= 0.0:
        return 0
    return int(round(window_s / BURST_BIN_S))


def burst_bin_index(t: float, window_s: float) -> int:
    """PartParityFxRules.BurstBinIndex: floor(t / 0.25), -1 before the trigger or past the window."""
    bins = burst_bin_count(window_s)
    if t is None or t != t or t < 0.0 or bins == 0:
        return -1
    b = int(math.floor(t / BURST_BIN_S))
    return b if b < bins else -1


def is_even_trial(trial: int) -> bool:
    """PartParityFxRules.IsEvenTrial: trials 1 and 3 (0-based 0, 2) are the even half."""
    return trial % 2 == 0


def terminal_explosion_power(length_m: Optional[float]) -> float:
    """PartParityFxRules.TerminalExplosionPower: Clamp01(length / 20)."""
    if length_m is None or length_m != length_m:
        return 0.0
    return min(1.0, max(0.0, length_m / TERMINAL_EXPLOSION_LENGTH_DIVISOR_M))


def fxmonger_prefab_index(power: Optional[float], prefab_count: int) -> int:
    """PartParityFxRules.FxMongerPrefabIndex: (int)(Clamp01(power) * (n - 1)), -1 with no prefab."""
    if prefab_count <= 0 or power is None or power != power:
        return -1
    return int(min(1.0, max(0.0, power)) * (prefab_count - 1))


def stock_surface_fx_scale(thrust_fraction: float, fx_max: float, distance_m: float,
                           max_distance_m: float, falloff: float) -> float:
    """PartParityFxRules.StockSurfaceFxScale: thrust / maxThrust * fxMax * (1 - d / maxDistance)^falloff."""
    vals = (thrust_fraction, fx_max, distance_m, max_distance_m, falloff)
    if any(v is None or v != v for v in vals) or max_distance_m <= 0.0 \
            or distance_m < 0.0 or distance_m >= max_distance_m:
        return 0.0
    return thrust_fraction * fx_max * math.pow(1.0 - distance_m / max_distance_m, falloff)


def dust_extra_reason(surface: Optional[str]) -> str:
    """PartParityFxRules.DustExtraReason: ``dust-extra:<surface>``."""
    return DUST_REASON_EXTRA_PREFIX + (surface or DUST_SURFACE_NONE)


def _burst_band(v: float, fail_min: float, fail_max: float, warn_min: float, warn_max: float) -> str:
    if v < fail_min or v > fail_max:
        return LEVEL_FAIL
    if v < warn_min or v > warn_max:
        return LEVEL_WARN
    return LEVEL_OK


def burst_measure_level(measure: str, value: Optional[float]) -> Optional[str]:
    """PartParityFxRules.BurstMeasureLevel: the G.3 line of one numeric burst measure (``ok`` /
    ``warn`` / ``fail``), None for a measure with no numeric line or an undefined value."""
    if value is None or value != value or value in (float("inf"), float("-inf")):
        return None
    t = BURST_THRESHOLDS
    a = abs(value)
    if measure == "onset":
        return LEVEL_FAIL if a > t["burstOnsetFailBins"] else LEVEL_OK
    if measure == "peak-time":
        if a > t["burstPeakTimeFailBins"]:
            return LEVEL_FAIL
        return LEVEL_WARN if a >= t["burstPeakTimeWarnBins"] else LEVEL_OK
    if measure == "duration":
        if a > t["burstDurationFailFraction"]:
            return LEVEL_FAIL
        return LEVEL_WARN if a > t["burstDurationWarnFraction"] else LEVEL_OK
    if measure == "peak-area":
        return _burst_band(value, t["burstPeakAreaFailMin"], t["burstPeakAreaFailMax"],
                           t["burstPeakAreaWarnMin"], t["burstPeakAreaWarnMax"])
    if measure == "radius":
        return _burst_band(value, t["burstRadiusFailMin"], t["burstRadiusFailMax"],
                           t["burstRadiusWarnMin"], t["burstRadiusWarnMax"])
    if measure == "centroid-offset":
        if a > t["burstCentroidOffsetFail"]:
            return LEVEL_FAIL
        return LEVEL_WARN if a > t["burstCentroidOffsetWarn"] else LEVEL_OK
    if measure == "brightness":
        return (LEVEL_WARN if value < FX_THRESHOLDS["brightnessWarnMin"]
                or value > FX_THRESHOLDS["brightnessWarnMax"] else LEVEL_OK)
    return None


def burst_colour_level(chroma_distance: Optional[float], hue_l1: Optional[float]) -> Optional[str]:
    """PartParityFxRules.BurstColourLevel: E.4's colour lines over a burst (either undefined
    value reads as unmeasured)."""
    c = chroma_distance is not None and chroma_distance == chroma_distance
    h = hue_l1 is not None and hue_l1 == hue_l1
    if not c and not h:
        return None
    t = FX_THRESHOLDS
    if (c and chroma_distance > t["chromaFail"]) or (h and hue_l1 > t["hueL1Fail"]):
        return LEVEL_FAIL
    if ((c and chroma_distance > t["colourWarnFraction"] * t["chromaFail"])
            or (h and hue_l1 > t["colourWarnFraction"] * t["hueL1Fail"])):
        return LEVEL_WARN
    return LEVEL_OK


# ---------------------------------------------------------------------------
# The P7 motion contract (Appendix H; PartParityMotionRules.cs, PartParityJson.cs,
# mirrored by PartParitySourceSyncTests.test_the_motion_* cells). Every constant the
# motion judge (M4) reads lives here, and the timing measures of H.1 are recomputed
# from a sidecar's per-frame arrays by ``motion_timing`` exactly as the C# writes them.
# ---------------------------------------------------------------------------

# Families (H.1 table order); the motion= words; the last two families are reading-only.
MOTION_FAMILIES: Tuple[str, ...] = (
    "deployable", "gear", "ladder", "animation-group", "cargo-bay", "animate-generic",
    "airbrake", "parachute", "robotics", "control-surface", "light", "cabin-light",
)
MOTION_ARG_WORDS: Tuple[str, ...] = MOTION_FAMILIES[:10]
MOTION_ARG_LIGHT = "light"
MOTION_ARG_KNOWN_WORDS: Tuple[str, ...] = MOTION_ARG_WORDS + (MOTION_ARG_LIGHT,)
MOTION_ARG_ALL = "all"
MOTION_KINDS: Tuple[str, ...] = ("clip", "servo", "surface", "canopy", "fade")
MOTION_KIND_SERVO = "servo"
MOTION_STATE_PREFIX = "motion-"
MOTION_STATES: Tuple[str, ...] = (
    "motion-deployed", "motion-open", "motion-semi", "motion-full", "motion-swept", "motion-lit",
)
CONTEXT_RADIAL = "radial"
MOTION_ORBIT_CONTEXTS: Tuple[str, ...] = (CONTEXT_ALONE, CONTEXT_RADIAL)
MOTION_RADIAL_FILLER_PART = "fuelTankSmallFlat"
# The servo modules the robotic poll reads, and the rotor P7 excludes.
MOTION_SERVO_MODULES: Tuple[str, ...] = (
    "ModuleRoboticServoHinge", "ModuleRoboticRotationServo", "ModuleRoboticServoPiston",
)
MOTION_ROTOR_MODULE = "ModuleRoboticServoRotor"
MOTION_EXCLUSION_REASONS: Tuple[str, ...] = (
    "motion-physics", "light-fade", "synthesized-from-trajectory", "loop-phase", "ground-scan",
    "rotor-spin-not-primed", "not-polled",
    "ground-deploy", "no-pose", "fairing-bay", "bay-deploy-not-animated", "no-animation", "auto-deploy",
)
MOTION_COUNTS_LITERAL = "motion cases:"

# The frame schedule (H.2, PartParityMotionRules.PlanFrameTargets).
MOTION_MIN_PREDICTED_S = 0.25
MOTION_MAX_PREDICTED_S = 30.0
MOTION_DEFAULT_FRAMES = 8
MOTION_MIN_FRAMES_PER_MOTION = 4
MOTION_MAX_FRAMES_PER_MOTION = 16
MOTION_DOUBLING_BLOCK_MOTIONS = 4
MOTION_WINDOW_PAD_S = 0.5
MOTION_WINDOW_CAP_S = 40.0
MOTION_SERVO_STOP_HOLD_S = 0.25
MOTION_SCOUT_STEP_S = 0.05
MOTION_SCOUT_MERGE_EPSILON_S = 1e-9
MOTION_FRAMING_GROW_FRACTION = 0.2
MOTION_TILE_PX = 256
MOTION_DEFAULT_VIEWS: Tuple[str, ...] = ("side-a", "side-b")
MOTION_SERVO_SWEEP_DEG = 45.0
MOTION_PISTON_SWEEP_FRACTION = 0.5

# Progress (H.1).
MOTION_TRANSFORM_STILL_DEG = 0.5
MOTION_TRANSFORM_STILL_M = 0.001
MOTION_TRANSFORM_STILL_SCALE = 0.001
MOTION_CHANNELS: Tuple[str, ...] = ("position", "rotation", "scale")
MOTION_CROSSING_LEVELS: Tuple[float, ...] = (0.02, 0.10, 0.50, 0.90, 0.98)
MOTION_SHAPE_BAND_LOW = 0.05
MOTION_SHAPE_BAND_HIGH = 0.95
MOTION_MATCH_JUMP_MAX = 0.25
MOTION_SCOUT_RENDER_TOLERANCE = 1e-4

# Flags (H.1, H.2) and their thresholds.
MOTION_SNAP_LOW = 0.1
MOTION_SNAP_HIGH = 0.9
MOTION_SNAP_REAL_MIN_FRAMES = 3
MOTION_STEP_REAL_RISE = 0.05
MOTION_STEP_STILL_EPSILON = 1e-4
MOTION_STEP_MIN_HOLDS = 2
MOTION_STEP_HOLD_LOW = 0.02
MOTION_STEP_HOLD_HIGH = 0.98
MOTION_NOT_MODELLED_REAL = 0.9
MOTION_NOT_MODELLED_GHOST = 0.1
FLAG_MOTION_SNAP = "motion-snap"
FLAG_MOTION_STEPPED = "motion-stepped"
FLAG_MOTION_NOT_MODELLED = "motion-not-modelled"
FLAG_MOTION_WINDOW_CAPPED = "motion-window-capped"
FLAG_MOTION_CLIPPED_AT_BORDER = "motion-clipped-at-border"
FLAG_FRAME_JITTER_PREFIX = "frame-jitter:"
FLAG_MOTION_NO_MATCH = "no-match"
FLAG_SCOUT_RENDER_DISAGREE = "scout-render-disagree"
FLAG_TRAVEL_DIFFERS = "travel-differs"
FLAG_PROGRESS_DISAGREE = "progress-disagree"

# Verdict thresholds (H.3; starting guesses, re-pinned from the reading runs).
MOTION_LAG_FAIL_MIN_S = 0.5
MOTION_LAG_FAIL_REAL_FRACTION = 0.25
MOTION_DURATION_RATIO_FAIL_MIN = 0.6
MOTION_DURATION_RATIO_FAIL_MAX = 1.67
MOTION_CURVE_SHAPE_FAIL = 0.30
MOTION_SERVO_ANGLE_FAIL_MIN_DEG = 5.0
MOTION_SERVO_ANGLE_FAIL_SWEEP_FRACTION = 0.15
MOTION_DURATION_RATIO_WARN_MIN = 0.8
MOTION_DURATION_RATIO_WARN_MAX = 1.25
MOTION_WARN_FRACTION = 0.5
MOTION_MIN_FRAMES = 5
MOTION_REAL_REACHED_PROGRESS = 0.9
MOTION_TRAVEL_DIFFERS_MIN = 0.9
MOTION_TRAVEL_DIFFERS_MAX = 1.1
MOTION_PROGRESS_DISAGREE_FRAMES = 2

# The motion controls (H.3): subjects and the judges' lines.
MOTION_CONTROL_PART = "longAntenna"
MOTION_CONTROL_SLOW_PART = "airbrake1"
MOTION_CONTROL_AIR_PART = "parachuteSingle"
MOTION_LATE_SHIFT_S = 1.0
MOTION_SLOW_CLIP_FACTOR = 2.0
MOTION_REAL_VS_REAL_LAG_MOTION_FRACTION = 0.02
MOTION_REAL_VS_REAL_DURATION_TOLERANCE = 0.05
MOTION_REAL_VS_REAL_CURVE_SHAPE_MAX = 0.05
MOTION_SLOW_RATIO_MIN = 1.9
MOTION_SLOW_RATIO_MAX = 2.1
MOTION_LATE_LAG_MIN = 0.98
MOTION_LATE_LAG_MAX = 1.02
MOTION_LATE_DURATION_TOLERANCE = 0.02

# Files and the sidecar's motion blocks (PartParityJson.cs; every key always written).
MOTION_ATLAS_SUFFIX = ".motion.png"
MOTION_KEYS: Tuple[str, ...] = (
    "family", "kind", "module", "moduleIndex", "pressUT", "predictedS", "ghostClipS",
    "eventDelayS", "ghostStartDelayS", "framesPerMotion", "window", "scout", "frames",
    "transforms", "timing",
)
MOTION_WINDOW_KEYS: Tuple[str, ...] = ("endS", "spacingS", "capped")
MOTION_SCOUT_KEYS: Tuple[str, ...] = ("stepS", "s", "progress")
MOTION_FRAME_KEYS: Tuple[str, ...] = (
    "k", "targetS", "realS", "ut", "realClock", "ghostClock", "realProgress", "ghostProgress",
    "matchedS", "matchedProgress", "imgProgress", "views", "flags",
)
MOTION_FRAME_VIEW_KEYS: Tuple[str, ...] = ("view", "same", "matched")
MOTION_TRANSFORM_KEYS: Tuple[str, ...] = ("path", "channel", "travelReal", "travelGhost", "real", "ghost")
MOTION_TIMING_KEYS: Tuple[str, ...] = (
    "real", "ghost", "lagS", "onsetLagS", "endLagS", "durationRatio", "curveShapeDev",
    "staggerDev", "travelRatio", "angleMaxDevDeg", "flags",
)
MOTION_CROSSING_KEYS: Tuple[str, ...] = ("t02", "t10", "t50", "t90", "t98")
MOTION_ATLAS_KEYS: Tuple[str, ...] = ("file", "tile", "rows", "cols")
MOTION_ROW_REAL = "real"
MOTION_ROW_GHOST = "ghost"
MOTION_ROW_GHOST_MATCHED = "ghost-matched"
# The timing keys motion_timing recomputes as numbers (every key but the crossings and flags).
MOTION_TIMING_NUMBER_KEYS: Tuple[str, ...] = MOTION_TIMING_KEYS[2:-1]


def is_motion_state(state: Optional[str]) -> bool:
    """PartParityMotionRules.IsMotionState: a ``motion-`` word (any tiebreak suffix)."""
    return isinstance(state, str) and state.startswith(MOTION_STATE_PREFIX)


def motion_atlas_rows(views: Sequence[str], matched: bool = True) -> Tuple[str, ...]:
    """PartParityJson.MotionAtlasRows: per view ``real:<view>``, ``ghost:<view>`` and, when
    ``matched``, ``ghost-matched:<view>``."""
    rows: List[str] = []
    for v in views or ():
        rows.append("%s:%s" % (MOTION_ROW_REAL, v))
        rows.append("%s:%s" % (MOTION_ROW_GHOST, v))
        if matched:
            rows.append("%s:%s" % (MOTION_ROW_GHOST_MATCHED, v))
    return tuple(rows)


def motion_block(sidecar: Optional[Dict]) -> Optional[Dict]:
    """The sidecar's ``motion`` block, or None for every other case."""
    block = sidecar.get("motion") if isinstance(sidecar, dict) else None
    return block if isinstance(block, dict) else None


# ----- the frame schedule (PartParityMotionRules) -----

def _finite(v: Any) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v)


def clamp_predicted_seconds(t: Any) -> float:
    """ClampPredictedSeconds: T in [0.25, 30] s; not a finite positive time reads 30."""
    if not _finite(t) or t <= 0.0:
        return MOTION_MAX_PREDICTED_S
    return min(max(float(t), MOTION_MIN_PREDICTED_S), MOTION_MAX_PREDICTED_S)


def motion_spacing_seconds(predicted_s: Any, frames_per_motion: int) -> float:
    """SpacingSeconds: T / frames."""
    return clamp_predicted_seconds(predicted_s) / max(1, frames_per_motion)


def motion_frame_target_seconds(k: int, predicted_s: Any, frames_per_motion: int) -> float:
    """FrameTargetSeconds: k T / n for the first block of 4 n intervals, each further block
    at twice the previous block's spacing."""
    if k <= 0:
        return 0.0
    s = motion_spacing_seconds(predicted_s, frames_per_motion)
    block = MOTION_DOUBLING_BLOCK_MOTIONS * max(1, frames_per_motion)
    m = 0 if k <= block else (k - 1) // block
    return block * s * (2.0 ** m - 1.0) + (k - m * block) * s * 2.0 ** m


def _nonneg(v: Any) -> float:
    return float(v) if _finite(v) and v >= 0.0 else 0.0


def motion_window_end_seconds(real_done_s: Any, ghost_run_s: Any, control_extra_s: Any = 0.0) -> Tuple[float, bool]:
    """WindowEndSeconds: (end, capped)."""
    end = _nonneg(real_done_s) + _nonneg(ghost_run_s) + MOTION_WINDOW_PAD_S + _nonneg(control_extra_s)
    capped = end > MOTION_WINDOW_CAP_S
    return (MOTION_WINDOW_CAP_S if capped else end), capped


def plan_motion_frame_targets(predicted_s: Any, real_done_s: Any, ghost_run_s: Any,
                              control_extra_s: Any = 0.0,
                              frames_per_motion: int = MOTION_DEFAULT_FRAMES) -> Dict[str, Any]:
    """PlanFrameTargets: ``{"targetsS", "endS", "spacingS", "capped"}``."""
    end, capped = motion_window_end_seconds(real_done_s, ghost_run_s, control_extra_s)
    targets: List[float] = []
    k = 0
    while True:
        t = motion_frame_target_seconds(k, predicted_s, frames_per_motion)
        if t > end + 1e-9:
            break
        targets.append(t)
        k += 1
    return {"targetsS": targets, "endS": end,
            "spacingS": motion_spacing_seconds(predicted_s, frames_per_motion), "capped": capped}


def motion_frame_jitter_tag(target_s: float, actual_s: float, predicted_s: Any,
                            frames_per_motion: int = MOTION_DEFAULT_FRAMES) -> Optional[str]:
    """FrameJitterTag: ``frame-jitter:<ms>`` for a frame later than half a frame interval."""
    late = actual_s - target_s
    if not late > motion_spacing_seconds(predicted_s, frames_per_motion) / 2.0:
        return None
    ms = math.floor(late * 1000.0 + 0.5) if late >= 0 else -math.floor(-late * 1000.0 + 0.5)
    return FLAG_FRAME_JITTER_PREFIX + str(int(ms))


def motion_picture_progress(a: float, b: float) -> float:
    """PictureProgress: a / (a + b), 0 when both are 0."""
    total = a + b
    return a / total if total > 0 else 0.0


def motion_matched_offset(scout_s: Sequence[float], scout_p: Sequence[float], target: float) -> Optional[float]:
    """MatchedOffset (H.2 step 2): None when the ghost never reaches the target or the
    bracketing scout interval jumps more than MOTION_MATCH_JUMP_MAX."""
    if not scout_s or not scout_p or len(scout_s) != len(scout_p):
        return None
    if target <= 0.0:
        return scout_s[0]
    for k, p in enumerate(scout_p):
        if p < target:
            continue
        if k == 0:
            return scout_s[0]
        dp = p - scout_p[k - 1]
        if dp > MOTION_MATCH_JUMP_MAX:
            return None
        if target >= 1.0:
            return scout_s[k]
        return scout_s[k - 1] + (target - scout_p[k - 1]) * (scout_s[k] - scout_s[k - 1]) / dp
    return None


# ----- the timing measures (PartParityMotionRules.Timing), None for not measured -----

def motion_crossing(s: Sequence[float], p: Sequence[float], level: float) -> Optional[float]:
    """Crossing: the first time p reaches level, linear between the bracketing points."""
    if not s or not p or len(s) != len(p):
        return None
    for k, v in enumerate(p):
        if v is None or not v >= level:
            continue
        if k == 0:
            return s[0]
        if p[k - 1] is None or s[k] is None or s[k - 1] is None:
            return None
        return s[k - 1] + (level - p[k - 1]) * (s[k] - s[k - 1]) / (v - p[k - 1])
    return None


def motion_crossings(s: Sequence[float], p: Sequence[float]) -> List[Optional[float]]:
    return [motion_crossing(s, p, level) for level in MOTION_CROSSING_LEVELS]


def motion_interpolate(s: Sequence[float], p: Sequence[float], at: float) -> Optional[float]:
    """Interpolate: linear between points, the end values outside them."""
    if not s or not p or len(s) != len(p):
        return None
    if at <= s[0]:
        return p[0]
    for k in range(1, len(s)):
        if at > s[k]:
            continue
        span = s[k] - s[k - 1]
        return p[k - 1] + (at - s[k - 1]) * (p[k] - p[k - 1]) / span if span > 0 else p[k]
    return p[-1]


def motion_shape_deviation(real_s: Sequence[float], real_p: Sequence[float], ghost_s: Sequence[float],
                           ghost_p: Sequence[float], lag_s: Optional[float]) -> Optional[float]:
    """ShapeDeviation: max |ghost(t + lag) - real(t)| over the real points inside the band."""
    if lag_s is None or real_s is None or real_p is None or len(real_s) != len(real_p):
        return None
    worst: Optional[float] = None
    for t, p in zip(real_s, real_p):
        if not (MOTION_SHAPE_BAND_LOW < p < MOTION_SHAPE_BAND_HIGH):
            continue
        g = motion_interpolate(ghost_s, ghost_p, t + lag_s)
        if g is None:
            continue
        d = abs(g - p)
        if worst is None or d > worst:
            worst = d
    return worst


def _motion_diff(a: Optional[float], b: Optional[float]) -> Optional[float]:
    return None if a is None or b is None else a - b


def _motion_ratio(num: Optional[float], den: Optional[float]) -> Optional[float]:
    return None if num is None or den is None or den <= 0 else num / den


def motion_not_modelled(real_p: Sequence[float], ghost_p: Sequence[float]) -> bool:
    """NotModelled: the real reaches 0.9 and the ghost never 0.1."""
    real_n = [v for v in real_p or () if v is not None]
    ghost_n = [v for v in ghost_p or () if v is not None]
    r = max(real_n) if real_n else None
    g = max(ghost_n) if ghost_n else None
    return r is not None and r >= MOTION_NOT_MODELLED_REAL and (g is None or g < MOTION_NOT_MODELLED_GHOST)


def motion_snapped(real_p: Sequence[float], ghost_p: Sequence[float]) -> bool:
    """Snapped: the ghost jumps under 0.1 -> over 0.9 between adjacent frames while the real
    takes 3 frames or more."""
    if real_p is None or ghost_p is None or len(real_p) != len(ghost_p):
        return False
    high = next((k for k, v in enumerate(real_p) if v > MOTION_SNAP_HIGH), -1)
    if high < 0:
        return False
    low = next((k for k in range(high - 1, -1, -1) if real_p[k] < MOTION_SNAP_LOW), -1)
    if low < 0 or high - low < MOTION_SNAP_REAL_MIN_FRAMES:
        return False
    return any(ghost_p[k] < MOTION_SNAP_LOW and ghost_p[k + 1] > MOTION_SNAP_HIGH
               for k in range(len(ghost_p) - 1))


def motion_stepped(real_p: Sequence[float], ghost_p: Sequence[float]) -> bool:
    """Stepped: two or more separate holds of the ghost (still, inside the hold band) while
    the real rises more than 0.05 per frame."""
    if real_p is None or ghost_p is None or len(real_p) != len(ghost_p):
        return False
    holds, in_hold = 0, False
    for k in range(len(ghost_p) - 1):
        still = (abs(ghost_p[k + 1] - ghost_p[k]) <= MOTION_STEP_STILL_EPSILON
                 and MOTION_STEP_HOLD_LOW < ghost_p[k] < MOTION_STEP_HOLD_HIGH
                 and real_p[k + 1] - real_p[k] > MOTION_STEP_REAL_RISE)
        if still and not in_hold:
            holds += 1
        in_hold = still
    return holds >= MOTION_STEP_MIN_HOLDS


def motion_max_abs_difference(a: Optional[Sequence[float]], b: Optional[Sequence[float]]) -> Optional[float]:
    """MaxAbsDifference over the points both carry (a None point is skipped)."""
    if a is None or b is None:
        return None
    worst: Optional[float] = None
    for x, y in zip(a, b):
        if x is None or y is None:
            continue
        d = abs(x - y)
        if worst is None or d > worst:
            worst = d
    return worst


def motion_series(block: Optional[Dict]) -> Optional[Dict[str, Any]]:
    """The per-frame arrays of a sidecar ``motion`` block, in the shape ``motion_timing``
    reads: realS / realProgress / ghostSameProgress from ``frames[]``, scoutS / scoutProgress
    from ``scout``, realClock / ghostClock (a servo only, else None) and the transforms."""
    if not isinstance(block, dict):
        return None
    frames = [f for f in (block.get("frames") or []) if isinstance(f, dict)]
    scout = block.get("scout") if isinstance(block.get("scout"), dict) else {}
    servo = block.get("kind") == MOTION_KIND_SERVO
    return {
        "realS": [f.get("realS") for f in frames],
        "realProgress": [f.get("realProgress") for f in frames],
        "ghostSameProgress": [f.get("ghostProgress") for f in frames],
        "scoutS": list(scout.get("s") or []),
        "scoutProgress": list(scout.get("progress") or []),
        "realClock": [f.get("realClock") for f in frames] if servo else None,
        "ghostClock": [f.get("ghostClock") for f in frames] if servo else None,
        "transforms": [t for t in (block.get("transforms") or []) if isinstance(t, dict)],
    }


def motion_timing(series: Optional[Dict[str, Any]]) -> Optional[Dict[str, Any]]:
    """PartParityMotionRules.Timing over the per-frame arrays: a dict with every
    MOTION_TIMING_KEYS key (crossings as MOTION_CROSSING_KEYS dicts, flags in C# order),
    None for not measured. The instrument recompute compares it with the sidecar's
    ``timing`` block within RECOMPUTE_FLOAT_TOLERANCE."""
    if not isinstance(series, dict):
        return None
    rs, rp = series.get("realS") or [], series.get("realProgress") or []
    ss, sp = series.get("scoutS") or [], series.get("scoutProgress") or []
    real = motion_crossings(rs, rp)
    ghost = motion_crossings(ss, sp)
    lag = _motion_diff(ghost[2], real[2])
    out: Dict[str, Any] = {
        "real": dict(zip(MOTION_CROSSING_KEYS, real)),
        "ghost": dict(zip(MOTION_CROSSING_KEYS, ghost)),
        "lagS": lag,
        "onsetLagS": _motion_diff(ghost[0], real[0]),
        "endLagS": _motion_diff(ghost[4], real[4]),
        "durationRatio": _motion_ratio(_motion_diff(ghost[3], ghost[1]), _motion_diff(real[3], real[1])),
        "curveShapeDev": motion_shape_deviation(rs, rp, ss, sp, lag),
        "staggerDev": None,
        "travelRatio": None,
    }
    flags: List[str] = []
    for tr in series.get("transforms") or []:
        d = motion_shape_deviation(rs, tr.get("real"), ss, tr.get("ghost"), lag)
        if d is not None and (out["staggerDev"] is None or d > out["staggerDev"]):
            out["staggerDev"] = d
        r = _motion_ratio(tr.get("travelGhost"), tr.get("travelReal"))
        if r is not None and (out["travelRatio"] is None or abs(r - 1.0) > abs(out["travelRatio"] - 1.0)):
            out["travelRatio"] = r
    tr_ratio = out["travelRatio"]
    if tr_ratio is not None and (tr_ratio < MOTION_TRAVEL_DIFFERS_MIN or tr_ratio > MOTION_TRAVEL_DIFFERS_MAX):
        flags.append(FLAG_TRAVEL_DIFFERS)
    out["angleMaxDevDeg"] = motion_max_abs_difference(series.get("realClock"), series.get("ghostClock"))
    same = series.get("ghostSameProgress") or []
    if motion_not_modelled(rp, sp):
        flags.append(FLAG_MOTION_NOT_MODELLED)
    if motion_snapped(rp, same):
        flags.append(FLAG_MOTION_SNAP)
    if motion_stepped(rp, same):
        flags.append(FLAG_MOTION_STEPPED)
    out["flags"] = flags
    return out


def motion_real_duration(timing: Optional[Dict[str, Any]]) -> Optional[float]:
    """PartParityMotionTiming.RealDurationS: real t98 - t02."""
    real = timing.get("real") if isinstance(timing, dict) else None
    if not isinstance(real, dict):
        return None
    return _motion_diff(real.get("t98"), real.get("t02"))


def motion_lag_fails(lag_s: Optional[float], real_duration_s: Optional[float]) -> bool:
    """LagFails: |lagS| > max(0.5 s, 0.25 T_real)."""
    if lag_s is None:
        return False
    return abs(lag_s) > max(MOTION_LAG_FAIL_MIN_S, MOTION_LAG_FAIL_REAL_FRACTION * (real_duration_s or 0.0))


def motion_duration_fails(ratio: Optional[float]) -> bool:
    """DurationFails: outside [0.6, 1.67]."""
    return ratio is not None and (ratio < MOTION_DURATION_RATIO_FAIL_MIN or ratio > MOTION_DURATION_RATIO_FAIL_MAX)


def motion_progress_disagrees(pose_t50: Optional[float], picture_t50: Optional[float],
                              frame_interval_s: float) -> bool:
    """ProgressDisagrees: the two t50 more than two frame intervals apart."""
    return (pose_t50 is not None and picture_t50 is not None
            and abs(pose_t50 - picture_t50) > MOTION_PROGRESS_DISAGREE_FRAMES * frame_interval_s)


def motion_timing_diffs(recorded: Any, recomputed: Optional[Dict[str, Any]]) -> List[str]:
    """Where a sidecar's ``timing`` block differs from ``motion_timing`` (numbers within
    RECOMPUTE_FLOAT_TOLERANCE, None against null, flags exactly)."""
    if not isinstance(recorded, dict) or recomputed is None:
        return [] if recorded is None and recomputed is None else ["timing:missing"]
    out: List[str] = []

    def same(a: Any, b: Any) -> bool:
        if a is None or b is None:
            return a is None and b is None
        return _finite(a) and _finite(b) and abs(a - b) <= RECOMPUTE_FLOAT_TOLERANCE

    for side in ("real", "ghost"):
        rec = recorded.get(side) if isinstance(recorded.get(side), dict) else {}
        for key in MOTION_CROSSING_KEYS:
            if not same(rec.get(key), recomputed[side][key]):
                out.append("timing:%s.%s" % (side, key))
    for key in MOTION_TIMING_NUMBER_KEYS:
        if not same(recorded.get(key), recomputed[key]):
            out.append("timing:%s" % key)
    if list(recorded.get("flags") or []) != list(recomputed["flags"]):
        out.append("timing:flags")
    return out


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
# Re-pinned 64 -> 256 from the 2026-11-21 reading runs (PP-1 2306, PP-2 2317): one-pixel
# texture-seam lines on otherwise identical parts join into 68-200 px blobs, while every
# diagnosed colour defect was 2000 px or more and the wrong-skin control reads >= 28000.
COLOR_BLOB_FAIL_PX = 256
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
# An FX ENTRY nothing but presence was judged on (E.4, weak cores); never a case verdict.
VERDICT_SKIPPED = "skipped"
VERDICTS: Tuple[str, ...] = (VERDICT_PASS, VERDICT_WARN, VERDICT_FAIL, VERDICT_ERROR)
_RANK = {VERDICT_PASS: 0, VERDICT_SKIPPED: 0, VERDICT_WARN: 1, VERDICT_FAIL: 2, VERDICT_ERROR: 3}

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
INVALID_HOST_DAMAGED_SUBKIND = "parity-host-damaged"
# Design D.6: more than half of one family's state cases could not be pressed or never
# reached the state, so that family's cases say nothing about its ghosts.
INVALID_STATE_PRESS_SUBKIND = "parity-state-press"
INVALID_SUBKINDS: Tuple[str, ...] = (
    INVALID_CONTROLS_SUBKIND, INVALID_INSTRUMENT_SUBKIND, INVALID_INCOMPLETE_SUBKIND,
    INVALID_HOST_DAMAGED_SUBKIND, INVALID_STATE_PRESS_SUBKIND,
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
# P8 F4: the FX judge (design Appendix E.4 to E.6, "F4 as built"). Everything FX that
# decides lives in this block; the shared functions below call into it by name.
#
# Every FX measure is recomputable from the 8-bit FX atlas and the sidecar alone:
# fx_measure_tile / fx_compare transcribe PartParityFxMetrics.MeasureTile / Compare in the
# same order of operations, so an integer matches exactly and a float within 1e-9.
# ---------------------------------------------------------------------------

# A case judged reference-only (E.6): counted apart, never a fail, never gated.
VERDICT_REFERENCE = "reference"
# The route of an rcs case's ``ghost-all`` entries (the event-route ghost with every nozzle
# shown, R4): captured, measured and recomputed, never judged.
FX_ROUTE_ALL = "all"
FX_JUDGED_ROUTES: Tuple[str, ...] = (ROUTE_EVENT, ROUTE_START)
FX_ROUTE_ROW: Dict[str, str] = {ROUTE_EVENT: "ghost", ROUTE_START: "ghost-start",
                                FX_ROUTE_ALL: "ghost-all"}
FX_VIEW_AXIS = "fx-bottom"
FX_LAYER_FLAME = "flame"
FX_LAYER_SMOKE = "smoke"
FX_FAMILY_PLUME = "plume"
FX_FAMILY_RCS = "rcs"
FX_FAMILY_LIGHT = "light"
# What a stochastic case did not measure, so a check did not run: its FX structure lists (the
# inventory raise), the bottom view's nozzle diameter (the cross-section offset), and a real
# side with no effect at all (nothing to compare). Each raises its case to at least ``warn``.
# So does a layer with a weak core (``fx-weak-core:<route>:<view>:<layer>:real=<n>,ghost=<n>``,
# ``fx_weak_core_flag``).
FLAG_FX_STRUCTURE_NOT_MEASURED = "fx-structure-not-measured"
FLAG_FX_NOZZLE_NOT_MEASURED = "fx-nozzle-not-measured"
FLAG_FX_NO_REAL_EFFECT = "fx-no-real-effect"
FLAG_FX_WEAK_CORE = "fx-weak-core"
# Findings the owner looked at and wants kept in sight until a todo settles them, whatever the
# case's verdict (``owner-flagged:<reason>``; it moves no verdict, no cap and no rule). Reason
# -> the OPEN todo that owns it. Empty since 2026-11-24: ``smoke-extra`` was the ghost's smoke
# riding the active vessel (GHOST-SMOKE-RIDES-ACTIVE-VESSEL, fixed); what is left of it is a puff
# either side draws now and then (PARTPARITY-ORBIT-SMOKE-PUFF-NOISE), noise, not an owner's finding.
FLAG_OWNER_FLAGGED_PREFIX = "owner-flagged:"
OWNER_FLAGGED_REASONS: Dict[str, str] = {}
# Flag kinds: the part of a flag before its first ``:``.
FX_FLAGS_RAISE_TO_WARN: Tuple[str, ...] = (
    FLAG_FX_STRUCTURE_NOT_MEASURED, FLAG_FX_NOZZLE_NOT_MEASURED, FLAG_FX_NO_REAL_EFFECT,
    FLAG_FX_WEAK_CORE)
# The family of each FX state word (PartParityFxRules.Enumerate).
FX_STATE_FAMILY: Dict[str, str] = {
    "burn-full": "plume", "burn-low": "plume", "main-full": "plume", "main-low": "plume",
    "rcs-fire": "rcs", "heat-medium": "heat", "heat-hot": "heat", "lit": "light"}
_TIEBREAK_RE = re.compile(r"-m\d+$")
REASON_FX_UNSETTLED = CAP_FX_UNSETTLED + ":capped at warn"
# Route, view, layer of one ``fx.views[]`` entry.
FxEntryKey = Tuple[str, str, str]
EVENT_ENGINE_IGNITED = "EngineIgnited"


def fx_block(sidecar: Optional[Dict]) -> Optional[Dict]:
    """The sidecar's ``fx`` block, or None (every non-FX case writes null)."""
    fx = sidecar.get("fx") if isinstance(sidecar, dict) else None
    return fx if isinstance(fx, dict) else None


def is_fx_stochastic(sidecar: Optional[Dict]) -> bool:
    """A plume or rcs case: the one kind whose FX entries are judged."""
    fx = fx_block(sidecar)
    return fx is not None and fx.get("family") in FX_STOCHASTIC_FAMILIES


def _fx_setup_fault(sidecar: Dict) -> bool:
    setup = sidecar.get("setup") if isinstance(sidecar.get("setup"), dict) else {}
    return is_setup_fault(setup.get("status"))


def fx_entries(sidecar: Optional[Dict]) -> List[Dict]:
    views = (fx_block(sidecar) or {}).get("views")
    return [v for v in views if isinstance(v, dict)] if isinstance(views, list) else []


def _fx_count(side: Any, key: str) -> int:
    return int(_num(side.get(key)) or 0) if isinstance(side, dict) else 0


def fx_real_effect(sidecar: Optional[Dict]) -> bool:
    """The real side drew something: some entry's real envelope is not empty."""
    return any(_fx_count(e.get("real"), "envelopePx") for e in fx_entries(sidecar))


# --- the measures (PartParityFxMetrics.cs) -----------------------------------------------

def fx_hue_bin(r: int, g: int, b: int) -> int:
    """PartParityFxMetrics.HueBin: integer arithmetic, -1 for a grey colour."""
    mx, mn = max(r, g, b), min(r, g, b)
    d = mx - mn
    if d <= 0:
        return -1
    if mx == r:
        hn = g - b
        if hn < 0:
            hn += 6 * d
    elif mx == g:
        hn = b - r + 2 * d
    else:
        hn = r - g + 4 * d
    return min(FX_HUE_BINS * hn // (6 * d), FX_HUE_BINS - 1)


FxAxis = Tuple[float, float, float, float]


def fx_axis(view: Any, axis: Any) -> Optional[FxAxis]:
    """PartParityFxMetrics.TryAxis: (ox, oy, ux, uy); None on the axis view or with no
    origin or a zero direction."""
    if view == FX_VIEW_AXIS or not isinstance(axis, dict):
        return None
    origin, direction = axis.get("originPx"), axis.get("dirPx")
    if not (isinstance(origin, list) and len(origin) >= 2
            and isinstance(direction, list) and len(direction) >= 2):
        return None
    dx, dy = direction[0], direction[1]
    length = math.sqrt(dx * dx + dy * dy)
    if not length > 0.0 or math.isinf(length):
        return None
    return origin[0], origin[1], dx / length, dy / length


def _fx_along(cx: float, cy: float, a: FxAxis) -> float:
    return (cx - a[0]) * a[2] + (cy - a[1]) * a[3]


def _fx_across(cx: float, cy: float, a: FxAxis) -> float:
    return (cy - a[1]) * a[2] - (cx - a[0]) * a[3]


def _fx_pixel_mean(sx: int, sy: int, n: int) -> List[float]:
    return [(sx + 0.5 * n) / n, (sy + 0.5 * n) / n]


def _fx_core_direction(core: Sequence[Tuple[int, int]], centroid: Sequence[float],
                       a: FxAxis) -> Optional[float]:
    """PartParityFxMetrics.CoreDirectionDeg: the line from the centroid of the core's
    upstream half to that of its downstream half, against the axis, in degrees."""
    tc = _fx_along(centroid[0], centroid[1], a)
    up, down = [0, 0, 0], [0, 0, 0]
    for x, y in core:
        half = up if _fx_along(x + 0.5, y + 0.5, a) < tc else down
        half[0] += x
        half[1] += y
        half[2] += 1
    if not up[2] or not down[2]:
        return None
    pu, pd = _fx_pixel_mean(*up), _fx_pixel_mean(*down)
    dt = _fx_along(pd[0], pd[1], a) - _fx_along(pu[0], pu[1], a)
    ds = _fx_across(pd[0], pd[1], a) - _fx_across(pu[0], pu[1], a)
    return math.atan2(ds, dt) * 180.0 / math.pi


def fx_wrap_deg(d: float) -> float:
    """PartParityFxMetrics.WrapDeg: into (-180, 180]."""
    while d > 180.0:
        d -= 360.0
    while d <= -180.0:
        d += 360.0
    return d


@dataclass
class FxTile:
    """One recomputed FX tile: ``measures`` (every FX_SIDE_MEASURE_KEYS key), the core
    pixels, the core's direction, and what breaks the 8-bit tile rule (``problems``)."""
    measures: Dict[str, Any]
    core: frozenset
    direction: Optional[float]
    problems: List[str]


def fx_measure_tile(pixels: bytes, width: int, x0: int, y0: int, tile: int, n: int,
                    view: Any, axis: Any) -> FxTile:
    """PartParityFxMetrics.MeasureTile over the ``tile`` px square at (x0, y0) of an RGBA
    image ``width`` px wide. A pixel that breaks the tile rule (an alpha no sample count
    writes, a colour on an uncovered pixel) is a problem: the C# side cannot have
    written it."""
    a = fx_axis(view, axis)
    k_of = [fx_covered_samples(al, n) for al in range(256)]
    exact = [fx_tile_alpha(k_of[al], n) == al for al in range(256)]
    core_of = [fx_is_core(k, n) for k in k_of]
    problems: List[str] = []

    def problem(text: str) -> None:
        if len(problems) < 3:
            problems.append(text)

    core: List[Tuple[int, int]] = []
    env = sat = sx = sy = un = ur = ug = ub = 0
    bins = [[0, 0, 0] for _ in range(FX_HUE_BINS)]
    t_min = s_min = math.inf
    t_max = s_max = -math.inf
    for y in range(tile):
        start = ((y0 + y) * width + x0) * 4
        row = pixels[start:start + tile * 4]
        if len(row) != tile * 4:
            raise ValueError("fx atlas tile out of range at row %d" % (y0 + y))
        if not row.strip(b"\0"):
            continue
        for x in range(tile):
            i = x * 4
            al = row[i + 3]
            r, g, b = row[i], row[i + 1], row[i + 2]
            if not al:
                if r or g or b:
                    problem("colour on an uncovered pixel (%d, %d)" % (x, y))
                continue
            if not exact[al]:
                problem("alpha %d at (%d, %d) is no count of %d samples" % (al, x, y, n))
            k = k_of[al]
            if k <= 0:
                continue
            env += 1
            saturated = fx_is_saturated(r, g, b)
            if not saturated:
                h = fx_hue_bin(r, g, b)
                if h >= 0:
                    acc = bins[h]
                    acc[0] += r
                    acc[1] += g
                    acc[2] += b
            if a is not None:
                t, s = _fx_along(x + 0.5, y + 0.5, a), _fx_across(x + 0.5, y + 0.5, a)
                t_min, t_max = min(t_min, t), max(t_max, t)
                s_min, s_max = min(s_min, s), max(s_max, s)
            if not core_of[al]:
                continue
            core.append((x, y))
            sx += x
            sy += y
            if saturated:
                sat += 1
                continue
            un += 1
            ur += r
            ug += g
            ub += b
    m: Dict[str, Any] = {k: None for k in FX_SIDE_MEASURE_KEYS}
    m.update({"corePx": len(core), "envelopePx": env, "saturatedPx": sat})
    if core:
        m["coreCentroid"] = _fx_pixel_mean(sx, sy, len(core))
    if a is not None and env:
        m["originPx"], m["lengthPx"], m["widthPx"] = t_min, t_max - t_min, s_max - s_min
    direction = _fx_core_direction(core, m["coreCentroid"], a) if a is not None and core else None
    if un:
        m["meanColor"] = [ur / un, ug / un, ub / un]
        m["luminance"] = fx_luminance(ur, ug, ub) / un
        total = ur + ug + ub
        if total:
            m["chromaticity"] = [ur / total, ug / total]
    m["hueHistogram"] = [fx_luminance(*acc) for acc in bins]
    return FxTile(m, frozenset(core), direction, problems)


def _fx_ratio(num: Optional[float], den: Optional[float]) -> Optional[float]:
    return num / den if num is not None and den is not None and den > 0 else None


def fx_compare(view: Any, axis: Any, real: FxTile, ghost: FxTile) -> Dict[str, Optional[float]]:
    """PartParityFxMetrics.Compare: every FX_COMPARE_KEYS key, ghost minus real."""
    out: Dict[str, Optional[float]] = {k: None for k in FX_COMPARE_KEYS}
    r, g = real.measures, ghost.measures
    a = fx_axis(view, axis)
    rc, gc = r["coreCentroid"], g["coreCentroid"]
    if a is not None:
        if r["originPx"] is not None and g["originPx"] is not None:
            out["originDeltaPx"] = g["originPx"] - r["originPx"]
        out["lengthRatio"] = _fx_ratio(g["lengthPx"], r["lengthPx"])
        out["widthRatio"] = _fx_ratio(g["widthPx"], r["widthPx"])
        if rc is not None and gc is not None:
            out["lateralOffsetPx"] = _fx_across(gc[0], gc[1], a) - _fx_across(rc[0], rc[1], a)
        if real.direction is not None and ghost.direction is not None:
            out["directionDeg"] = fx_wrap_deg(ghost.direction - real.direction)
    elif view == FX_VIEW_AXIS:
        if r["corePx"] > 0:
            out["crossSectionAreaRatio"] = g["corePx"] / r["corePx"]
        nozzle = axis.get("nozzleDiameterPx") if isinstance(axis, dict) else None
        if rc is not None and gc is not None and nozzle is not None and nozzle > 0:
            dx, dy = gc[0] - rc[0], gc[1] - rc[1]
            out["crossSectionOffset"] = math.sqrt(dx * dx + dy * dy) / nozzle
    if r["chromaticity"] is not None and g["chromaticity"] is not None:
        dr = g["chromaticity"][0] - r["chromaticity"][0]
        dg = g["chromaticity"][1] - r["chromaticity"][1]
        out["chromaDistance"] = math.sqrt(dr * dr + dg * dg)
    sa, sb = sum(r["hueHistogram"]), sum(g["hueHistogram"])
    if sa > 0 and sb > 0:
        out["hueL1"] = sum(abs(x / sa - y / sb)
                           for x, y in zip(r["hueHistogram"], g["hueHistogram"]))
    out["brightnessRatio"] = _fx_ratio(g["luminance"], r["luminance"])
    union = len(real.core | ghost.core)
    out["coreIou"] = len(real.core & ghost.core) / union if union else 1.0
    return out


# --- the recompute (E.4) ------------------------------------------------------------------

FX_EXACT_KEYS: Tuple[str, ...] = ("corePx", "envelopePx", "saturatedPx")


def _fx_value_diffs(at: str, recorded: Any, got: Any, exact: bool) -> List[str]:
    if isinstance(got, list):
        if not isinstance(recorded, list) or len(recorded) != len(got):
            return ["%s:%s!=%s" % (at, recorded, got)]
        return [d for i, (a, b) in enumerate(zip(recorded, got))
                for d in _fx_value_diffs("%s[%d]" % (at, i), a, b, exact)]
    if exact:
        ok = type(recorded) is int and recorded == got
    else:
        ok = not isinstance(recorded, (bool, str, list, dict)) and _float_equal(recorded, got)
    return [] if ok else ["%s:%s!=%s" % (at, recorded, got)]


def recompute_fx_case(sidecar: Dict, atlas: Tuple[int, int, int, bytes]) -> List[str]:
    """Recompute every ``fx.views[]`` entry from the decoded FX atlas and return the
    differences with the sidecar (the split halves have no tiles; their shape is
    ``fx_block_problems``'). An atlas whose layout does not match ``fxAtlas`` is itself a
    difference."""
    width, height, bpp, pixels = atlas
    fx = fx_block(sidecar) or {}
    meta = sidecar.get("fxAtlas") if isinstance(sidecar.get("fxAtlas"), dict) else {}
    tile, n = meta.get("tile"), fx.get("samples")
    if type(tile) is not int or tile <= 0:
        return ["fx atlas block unusable: tile=%s" % tile]
    if type(n) is not int or not 0 < n <= FX_MAX_SAMPLES:
        return ["fx.samples=%s" % n]
    rows = list(fx_atlas_rows_for(fx.get("family") == FX_FAMILY_RCS))
    cols = list(FX_VIEWS)
    if list(meta.get("rows") or []) != rows or list(meta.get("cols") or []) != cols:
        return ["fx atlas rows=%s cols=%s (expected %s, %s)"
                % (meta.get("rows"), meta.get("cols"), rows, cols)]
    if width != tile * len(cols) or height != tile * len(rows):
        return ["fx atlas size %dx%d != %d cols x %d rows of %d px"
                % (width, height, len(cols), len(rows), tile)]
    if bpp != 4:
        return ["fx atlas is not RGBA (bytes per pixel %d)" % bpp]
    diffs: List[str] = []
    cache: Dict[Tuple[str, str, str], FxTile] = {}

    def tile_of(row: str, view: str, axis: Any) -> FxTile:
        key = (row, view, repr(axis))
        if key not in cache:
            cache[key] = fx_measure_tile(pixels, width, cols.index(view) * tile,
                                         rows.index(row) * tile, tile, n, view, axis)
            diffs.extend("%s/%s: %s" % (row, view, p) for p in cache[key].problems)
        return cache[key]

    for e in fx_entries(sidecar):
        view, route, layer, axis = e.get("view"), e.get("route"), e.get("layer"), e.get("axis")
        at = fx_reason(str(route), str(view), str(layer), "")
        ghost_row = "%s-%s" % (FX_ROUTE_ROW.get(str(route)), layer)
        if view not in cols or layer not in FX_LAYERS or ghost_row not in rows:
            diffs.append("%s not in the fx atlas" % at)
            continue
        if e.get("width") != tile or e.get("height") != tile:
            diffs.append("%ssize:%sx%s!=%d" % (at, e.get("width"), e.get("height"), tile))
            continue
        real, ghost = tile_of("real-%s" % layer, view, axis), tile_of(ghost_row, view, axis)
        for name, got in (("real", real), ("ghost", ghost)):
            rec = e.get(name) if isinstance(e.get(name), dict) else {}
            for key in FX_SIDE_MEASURE_KEYS:
                diffs.extend(_fx_value_diffs("%s%s.%s" % (at, name, key), rec.get(key),
                                             got.measures[key], key in FX_EXACT_KEYS))
        rec = e.get("compare") if isinstance(e.get("compare"), dict) else {}
        for key, value in fx_compare(view, axis, real, ghost).items():
            diffs.extend(_fx_value_diffs("%scompare.%s" % (at, key), rec.get(key), value, False))
    return diffs


def fx_expected_entries(family: Any, control: bool) -> List[Tuple[str, str, str]]:
    """The (route, view, layer) entries a stochastic case must write: the event and start
    routes (a control: the event route, its pair), and ``all`` on an rcs part case."""
    routes: List[str] = [ROUTE_EVENT] if control else list(FX_JUDGED_ROUTES)
    if family == FX_FAMILY_RCS and not control:
        routes.append(FX_ROUTE_ALL)
    return [(r, v, layer) for r in routes for layer in FX_LAYERS for v in FX_VIEWS]


def _missing_keys(at: str, block: Any, keys: Sequence[str]) -> List[str]:
    if not isinstance(block, dict):
        return ["%s: block missing" % at]
    missing = [k for k in keys if k not in block]
    return ["%s: missing key(s) %s" % (at, ",".join(missing))] if missing else []


def _fx_numbers(value: Any, count: int) -> bool:
    return isinstance(value, list) and len(value) == count and all(_num(v) is not None for v in value)


def _fx_side_problems(at: str, side: Any, has_axis: bool) -> List[str]:
    """One side block: every key, counts that are counts, and each value a number (or a list
    of them) exactly where PartParityFxMetrics.MeasureTile defines it, null elsewhere."""
    problems = _missing_keys(at, side, FX_SIDE_MEASURE_KEYS)
    if problems:
        return problems
    for key in FX_EXACT_KEYS:
        if type(side[key]) is not int or side[key] < 0 or _num(side[key]) is None:
            problems.append("%s.%s=%s" % (at, key, side[key]))
    if problems:
        return problems
    core, env, sat = (side[k] for k in FX_EXACT_KEYS)
    if not sat <= core <= env:
        problems.append("%s: corePx=%d envelopePx=%d saturatedPx=%d" % (at, core, env, sat))
    lit, along = core > sat, has_axis and env > 0
    black = lit and _fx_numbers(side["meanColor"], 3) and not any(side["meanColor"])
    for key, count, defined in (
            ("originPx", 0, along), ("lengthPx", 0, along), ("widthPx", 0, along),
            ("coreCentroid", 2, core > 0), ("meanColor", 3, lit), ("chromaticity", 2, lit and not black),
            ("luminance", 0, lit), ("hueHistogram", FX_HUE_BINS, True)):
        v = side[key]
        if not defined:
            ok = v is None
        else:
            ok = _fx_numbers(v, count) if count else _num(v) is not None
        if not ok:
            problems.append("%s.%s=%s" % (at, key, v))
    return problems


def _fx_compare_problems(at: str, view: Any, axis: Any, block: Dict, size: Any) -> List[str]:
    """One compare block against the side blocks beside it (both already shaped): every value
    they determine is recomputed from them; ``coreIou`` and ``directionDeg``, which need the
    tile, are range-checked. Run on every captured case, so a compare block that lost or bent
    its numbers cannot unjudge a case the atlas recompute did not sample."""
    recorded = block.get("compare")
    problems = _missing_keys(at, recorded, FX_COMPARE_KEYS)
    if problems:
        return problems
    real, ghost = [FxTile(block[name], frozenset(), None, []) for name in ("real", "ghost")]
    for key, value in fx_compare(view, axis, real, ghost).items():
        if key not in ("directionDeg", "coreIou"):
            problems.extend(_fx_value_diffs("%s.%s" % (at, key), recorded[key], value, False))
    iou, turn = _num(recorded["coreIou"]), recorded["directionDeg"]
    if iou is None or not 0.0 <= iou <= 1.0:
        problems.append("%s.coreIou=%s" % (at, recorded["coreIou"]))
    core = min(block["real"]["corePx"], block["ghost"]["corePx"])
    has_axis = fx_axis(view, axis) is not None
    if turn is None:
        # A core of more pixels than a tile row holds has two halves, so a direction.
        bad = has_axis and (type(size) is not int or core > size)
    else:
        bad = not has_axis or not core or _num(turn) is None or abs(turn) > 180.0
    if bad:
        problems.append("%s.directionDeg=%s with a core of %d px" % (at, turn, core))
    return problems


def _fx_measure_problems(at: str, view: Any, axis: Any, block: Dict, size: Any) -> List[str]:
    """The real, ghost and compare blocks of an entry or of one of its halves."""
    has_axis = fx_axis(view, axis) is not None
    problems = [p for name in ("real", "ghost")
                for p in _fx_side_problems("%s.%s" % (at, name), block.get(name), has_axis)]
    return problems or _fx_compare_problems(at + ".compare", view, axis, block, size)


def _fx_half_problems(at: str, entry: Dict, n: Optional[int]) -> List[str]:
    split = entry.get("splitHalf")
    problems = _missing_keys(at, split, FX_SPLIT_HALF_KEYS)
    if problems:
        return problems
    for half in FX_SPLIT_HALF_KEYS:
        block, hat = split[half], "%s.%s" % (at, half)
        missing = _missing_keys(hat, block, FX_HALF_KEYS)
        if missing:
            problems.extend(missing)
            continue
        want = None if n is None else ((n + 1) // 2 if half == "even" else n // 2)
        if want is not None and block["samples"] != want:
            problems.append("%s.samples=%s (expected %d)" % (hat, block["samples"], want))
        problems.extend(_fx_measure_problems(hat, entry.get("view"), entry.get("axis"), block,
                                             entry.get("width")))
    return problems


def _fx_axis_problem(view: Any, axis: Any, required: bool) -> Optional[str]:
    """An entry's axis: null, or its three keys, each null or numbers; a side or far view of a
    plume or rcs case (``required``) needs a usable one (E.4: every side view carries it;
    without one five measures are not defined, so not judged)."""
    if axis is not None:
        missing = _missing_keys("axis", axis, FX_AXIS_KEYS)
        if missing:
            return missing[0]
        for key in ("originPx", "dirPx"):
            if axis[key] is not None and not _fx_numbers(axis[key], 2):
                return "axis.%s=%s" % (key, axis[key])
        if axis["nozzleDiameterPx"] is not None and _num(axis["nozzleDiameterPx"]) is None:
            return "axis.nozzleDiameterPx=%s" % axis["nozzleDiameterPx"]
    if required and view != FX_VIEW_AXIS and fx_axis(view, axis) is None:
        return "no exhaust axis"
    return None


def fx_contradictions(sidecar: Optional[Dict]) -> List[str]:
    """What a captured FX part case says against itself (the pattern of
    ``press_proof_problem``: the case is ``error`` and the sweep INVALID(parity-instrument)):
    ``fx.family``, the switch that decides what is judged, against the state word and
    ``stateProof.family``; an ``ok`` rcs case that fired nothing or not the predicted nozzles
    (the C# side records ``rcs-input-mismatch``); an ``ok`` light case whose real side is
    under the plate floor (``plate-not-lit``, never judged)."""
    fx = fx_block(sidecar)
    if fx is None or _fx_setup_fault(sidecar):
        return []
    family = fx.get("family")
    problems: List[str] = []
    word = _TIEBREAK_RE.sub("", str(sidecar.get("state")))
    if FX_STATE_FAMILY.get(word) != family:
        problems.append("fx.family=%s on a %s case" % (family, sidecar.get("state")))
    proof = sidecar.get("stateProof")  # a missing one is press_proof_problem's
    if isinstance(proof, dict) and proof.get("family") != family:
        problems.append("fx.family=%s stateProof.family=%s" % (family, proof.get("family")))
    if family == FX_FAMILY_RCS:
        rcs = fx.get("rcs") if isinstance(fx.get("rcs"), dict) else {}
        fired, predicted, total = rcs.get("fired"), rcs.get("predicted"), _fx_len(rcs.get("nozzles"))
        same = sorted(map(str, fired)) == sorted(map(str, predicted)) \
            if isinstance(fired, list) and isinstance(predicted, list) else fired == predicted
        if not _fx_len(fired) or not same or total is None or total < _fx_len(fired):
            problems.append("rcs fired=%s predicted=%s nozzles=%s on an ok case"
                            % (fired, predicted, rcs.get("nozzles")))
    if family == FX_FAMILY_LIGHT:
        plate = fx.get("plate") if isinstance(fx.get("plate"), dict) else {}
        lit = _num(plate.get("litPct"))
        if lit is None or lit < PLATE_LIT_MIN_PCT:
            problems.append("plate litPct=%s on an ok case" % plate.get("litPct"))
    if family == FX_FAMILY_PLUME:
        problem = fx_throttle_problem(word, fx)
        if problem is not None:
            problems.append(problem)
    return problems


def fx_throttle_problem(word: str, fx: Dict) -> Optional[str]:
    """The throttle a plume case says drove it against its state word: a ``main-*`` case's
    vessel held the word's main throttle and its engine followed it; any other plume case's
    engine ran on the independent throttle with the vessel's at 0 (a main-throttle case never
    reaches another one). A sidecar written before the two drive keys is not asked, except for
    a ``main-*`` case, which no such build could write."""
    drive = fx.get("drive") if isinstance(fx.get("drive"), dict) else {}
    main, independent = _num(drive.get("mainThrottle")), drive.get("independentThrottle")
    if word in FX_MAIN_STATES:
        want = FX_MAIN_THROTTLES[word]
        if (main is None or independent is not False or _num(fx.get("throttle")) != want
                or abs(main - want) > FX_THROTTLE_DONE_TOLERANCE):
            return ("drive mainThrottle=%s independentThrottle=%s throttle=%s on a %s case"
                    % (drive.get("mainThrottle"), independent, fx.get("throttle"), word))
        return None
    if independent is False or (main is not None and abs(main) > FX_THROTTLE_DONE_TOLERANCE):
        return "drive mainThrottle=%s independentThrottle=%s on a %s case" % (
            drive.get("mainThrottle"), independent, word)
    return None


def fx_block_problems(sidecar: Optional[Dict], control: bool = False) -> List[str]:
    """The shape of an ``fx`` block (E.5), run over every captured FX case, sampled or not:
    a known family that agrees with the case (``fx_contradictions``); ``reference`` null or
    ``waterfall``, and only on a stochastic family; a sample count; every expected entry
    exactly once, with an axis and every value of its blocks where the C# side defines it; the
    split halves' blocks, whose sample counts are the even and odd halves of the set."""
    fx = fx_block(sidecar)
    if fx is None or _fx_setup_fault(sidecar):
        return []
    problems = _missing_keys("fx", fx, FX_KEYS)
    family = fx.get("family")
    if family not in FX_FAMILIES:
        problems.append("fx.family=%s" % family)
    stochastic = family in FX_STOCHASTIC_FAMILIES
    reference = fx.get("reference")
    if reference is not None and (reference != REFERENCE_WATERFALL or not stochastic):
        problems.append("fx.reference=%s on a %s case" % (reference, family))
    if not control:
        problems.extend(fx_contradictions(sidecar))
    structure = fx.get("structure")
    if structure is not None:
        problems.extend(_missing_keys("fx.structure", structure, FX_STRUCTURE_KEYS))
        for key in FX_STRUCTURE_KEYS if isinstance(structure, dict) else ():
            if structure.get(key) is not None and not isinstance(structure[key], list):
                problems.append("fx.structure.%s=%s" % (key, structure[key]))
    views = fx.get("views")
    if views is not None and not (isinstance(views, list) and all(isinstance(v, dict) for v in views)):
        problems.append("fx.views is not a list of entries")
    entries = fx_entries(sidecar)
    if not stochastic and not entries:
        return problems
    n = fx.get("samples")
    if type(n) is not int or not 0 < n <= FX_MAX_SAMPLES:
        problems.append("fx.samples=%s" % n)
        n = None
    meta = sidecar.get("fxAtlas") if isinstance(sidecar.get("fxAtlas"), dict) else {}
    tile = meta.get("tile")
    seen: Dict[Tuple[str, str, str], int] = {}
    for e in entries:
        key = (str(e.get("route")), str(e.get("view")), str(e.get("layer")))
        seen[key] = seen.get(key, 0) + 1
        at = "fx:%s:%s:%s" % key
        problems.extend(_missing_keys(at, e, FX_VIEW_KEYS))
        if key[0] not in FX_ROUTE_ROW or key[1] not in FX_VIEWS or key[2] not in FX_LAYERS:
            problems.append("%s: not a known route, view and layer" % at)
        if type(tile) is not int or tile <= 0 or e.get("width") != tile or e.get("height") != tile:
            problems.append("%s: size %sx%s, tile %s" % (at, e.get("width"), e.get("height"), tile))
        bad_axis = _fx_axis_problem(e.get("view"), e.get("axis"), stochastic)
        if bad_axis is not None:
            problems.append("%s: %s" % (at, bad_axis))
            continue
        problems.extend(_fx_measure_problems(at, e.get("view"), e.get("axis"), e, e.get("width")))
        problems.extend(_fx_half_problems(at + ".splitHalf", e, n))
    if stochastic:
        lost = ["%s:%s:%s" % k for k in fx_expected_entries(family, control) if k not in seen]
        if lost:
            problems.append("fx.views lacks %d entr(ies): %s" % (len(lost), ", ".join(lost[:3])))
    twice = sorted("%s:%s:%s" % k for k, c in seen.items() if c > 1)
    if twice:
        problems.append("fx.views repeats %s" % ", ".join(twice[:3]))
    return problems


def fx_run_problems(manifest: Dict, sidecars: Dict[str, Optional[Dict]]) -> List[str]:
    """Writer cross-checks of an FX run (E.5): a case that carries an ``fx`` block while the
    settings recompute a non-FX control set, a captured case of an FX run with none, a row
    family that is not the sidecar's, and an rcs family whose every captured case drew nothing
    on its real side (no control has an rcs subject, so nothing else would notice a capture
    that cannot see a puff; a stock jet legitimately draws no plume, E.12)."""
    settings = manifest.get("settings") if isinstance(manifest.get("settings"), dict) else {}
    fx_run = is_fx_run(settings)
    problems: List[str] = []
    rcs: List[bool] = []
    for entry in manifest.get("cases") or []:
        cid = entry.get("caseId") if isinstance(entry, dict) else None
        side = sidecars.get(cid) if isinstance(cid, str) else None
        if not isinstance(side, dict) or cid in ALL_CONTROL_IDS:
            continue
        has = fx_block(side) is not None
        if has and not fx_run:
            problems.append("%s carries an fx block but settings fx=%s recompute a non-FX "
                            "control set" % (cid, _setting(settings, SETTING_FX)))
        elif fx_run and not has and not _fx_setup_fault(side):
            problems.append("%s has no fx block in an fx=%s run"
                            % (cid, _setting(settings, SETTING_FX)))
        if has and not _fx_setup_fault(side):
            family = fx_block(side).get("family")
            if entry.get("family") is not None and entry.get("family") != family:
                problems.append("%s row family=%s fx.family=%s" % (cid, entry.get("family"), family))
            if family == FX_FAMILY_RCS:
                rcs.append(fx_real_effect(side))
    if rcs and not any(rcs):
        problems.append("rcs: the real side of all %d captured case(s) drew nothing" % len(rcs))
    return problems


def fx_atlas_problem(sidecar: Dict, atlases: Optional[Collection[str]]) -> Optional[str]:
    """Completeness of a captured stochastic case's pictures: a harvested FX atlas, and the
    strip when it names one (None: complete)."""
    if atlases is None or not is_fx_stochastic(sidecar):
        return None
    for block in ("fxAtlas", "strip"):
        meta = sidecar.get(block)
        name = meta.get("file") if isinstance(meta, dict) else None
        if not name:
            if block == "fxAtlas":
                return "no-fx-atlas"
        elif name not in atlases:
            return "%s-missing:%s" % (block, name)
    return None


def fx_recompute(sidecars: Dict[str, Optional[Dict]], atlases: Collection[str],
                 part_ids: Sequence[str], control_ids: Sequence[str], run_id: str,
                 read_png: Optional["PngReader"],
                 atlas_path: Callable[[str], str]) -> Tuple[List[str], List[str]]:
    """The FX half of the instrument check: every control with an FX atlas and the seeded
    sample of the part cases with one are recomputed from it; every captured FX case has
    its block shape checked. Returns (``<caseId>.fx`` ids recomputed, mismatches)."""
    def file_of(cid: str) -> Optional[str]:
        side = sidecars.get(cid)
        meta = side.get("fxAtlas") if isinstance(side, dict) else None
        name = meta.get("file") if isinstance(meta, dict) else None
        return name if name in atlases else None

    mismatches: List[str] = []
    controls = set(control_ids)
    for cid in list(part_ids) + list(control_ids):
        mismatches.extend("%s %s" % (cid, p)
                          for p in fx_block_problems(sidecars.get(cid), cid in controls))
    if read_png is None:
        return [], mismatches
    ids = [c for c in control_ids if file_of(c)]
    ids += select_recompute_sample([c for c in part_ids if file_of(c)], run_id)
    for cid in ids:
        try:
            diffs = recompute_fx_case(sidecars[cid], read_png(atlas_path(file_of(cid))))
        except Exception as exc:  # noqa: BLE001 - an unreadable atlas is the finding
            diffs = ["fx atlas unreadable (%s: %s)" % (type(exc).__name__, exc)]
        mismatches.extend("%s %s" % (cid, d) for d in diffs)
    return ["%s.fx" % c for c in ids], mismatches


# --- the judge (E.4) ----------------------------------------------------------------------

@dataclass(frozen=True)
class FxFinding:
    """One measure of one entry over a line: ``level`` fail or warn, ``reason`` its token,
    ``side`` the line it crossed without the number (``origin-``, ``length+``,
    ``colour:hueL1``), which two sample halves are compared by. ``capped``: a fail the
    split-half rule holds at ``warn`` (``fx_cap_findings``)."""
    measure: str
    level: str
    reason: str
    side: str = ""
    capped: bool = False


def fx_presence_reason(layer: Any, real_core: int, ghost_core: int) -> Optional[str]:
    """PartParityFxMetrics.PresenceReason."""
    smoke = layer == FX_LAYER_SMOKE
    min_px = FX_SMOKE_MIN_PX if smoke else FX_PRESENCE_MIN_PX
    if real_core >= min_px and ghost_core == 0:
        return FX_REASON_SMOKE_MISSING if smoke else FX_REASON_FX_MISSING
    if ghost_core >= min_px and real_core == 0:
        return FX_REASON_SMOKE_EXTRA if smoke else FX_REASON_FX_EXTRA
    return None


def fx_both_cores_weak(real_core: int, ghost_core: int) -> bool:
    """PartParityFxMetrics.BothCoresWeak: neither core reaches FX_PRESENCE_MIN_PX, on either
    layer, so the tile holds no plume to compare and only presence is judged."""
    return real_core < FX_PRESENCE_MIN_PX and ghost_core < FX_PRESENCE_MIN_PX


def fx_weak_core_flag(entry: Dict) -> Optional[str]:
    """The flag of an entry with a weak core, so none passes silently. Both weak with anything
    drawn (an envelope pixel on either side): its measures were skipped. One weak and presence
    does not decide (its core is under the minimum but not 0, or a smoke core is under
    FX_SMOKE_MIN_PX): every measure is still judged. None otherwise."""
    real = entry.get("real") if isinstance(entry.get("real"), dict) else {}
    ghost = entry.get("ghost") if isinstance(entry.get("ghost"), dict) else {}
    cores = (_fx_count(real, "corePx"), _fx_count(ghost, "corePx"))
    if fx_both_cores_weak(*cores):
        if not (_fx_count(real, "envelopePx") or _fx_count(ghost, "envelopePx")):
            return None
    elif min(cores) >= FX_PRESENCE_MIN_PX or fx_presence_reason(entry.get("layer"), *cores) is not None:
        return None
    return "%s:%s:%s:%s:real=%d,ghost=%d" % (
        (FLAG_FX_WEAK_CORE, entry.get("route"), entry.get("view"), entry.get("layer")) + cores)


def _over(v: float, fail: float, warn: float) -> Optional[str]:
    return VERDICT_FAIL if v > fail else (VERDICT_WARN if v > warn else None)


def _outside(v: float, lo: float, hi: float) -> bool:
    return v < lo or v > hi


def fx_entry_findings(entry: Dict, rcs_case: bool) -> List[FxFinding]:
    """Every E.4 measure of one ``fx.views[]`` entry over its warn or fail line, in
    FX_MEASURES order. A null measure is not measured and never moves a verdict;
    brightness and core IoU are warn-only; an rcs case leaves out
    FX_RCS_REPORT_ONLY_MEASURES (R4). A layer one side lacks is named by its presence
    reason alone: the tile has no second plume for the other measures to compare. Where both
    cores are weak nothing but presence is judged (``fx_weak_core_flag`` raises the case)."""
    th = FX_THRESHOLDS
    layer = entry.get("layer")
    real = entry.get("real") if isinstance(entry.get("real"), dict) else {}
    ghost = entry.get("ghost") if isinstance(entry.get("ghost"), dict) else {}
    compare = entry.get("compare") if isinstance(entry.get("compare"), dict) else {}
    c = {k: _num(compare.get(k)) for k in FX_COMPARE_KEYS}
    length = _num(real.get("lengthPx")) or 0.0
    found: List[FxFinding] = []

    def add(measure: str, level: Optional[str], text: str, side: str) -> None:
        if level is not None:
            found.append(FxFinding(measure, level, text, side))

    def sign(value: float) -> str:
        return "+" if value > 0 else "-"

    presence = fx_presence_reason(layer, _fx_count(real, "corePx"), _fx_count(ghost, "corePx"))
    if presence is not None:
        return [FxFinding("smoke" if layer == FX_LAYER_SMOKE else "presence",
                          VERDICT_FAIL, presence, presence)]
    if fx_both_cores_weak(_fx_count(real, "corePx"), _fx_count(ghost, "corePx")):
        return []
    for measure, key in (("origin", "originDeltaPx"), ("lateral", "lateralOffsetPx")):
        if c[key] is not None:
            fail = max(th[measure + "FailMinPx"], th[measure + "FailLengthFraction"] * length)
            add(measure, _over(abs(c[key]), fail, fail * th["offsetWarnFraction"]),
                "%s=%.2fpx" % (measure, c[key]), measure + sign(c[key]))
    for measure in ("length", "width"):
        ratio = c[measure + "Ratio"]
        if ratio is not None:
            add(measure, _over(abs(ratio - 1.0), th[measure + "FailFraction"],
                               th[measure + "WarnFraction"]), "%s=%.3f" % (measure, ratio),
                measure + sign(ratio - 1.0))
    if c["directionDeg"] is not None:
        add("direction", _over(abs(c["directionDeg"]), th["directionFailDeg"],
                               th["directionWarnDeg"]), "direction=%.2fdeg" % c["directionDeg"],
            "direction" + sign(c["directionDeg"]))
    area = c["crossSectionAreaRatio"]
    if area is not None:
        level = None
        if _outside(area, th["crossSectionAreaFailMin"], th["crossSectionAreaFailMax"]):
            level = VERDICT_FAIL
        elif _outside(area, th["crossSectionAreaWarnMin"], th["crossSectionAreaWarnMax"]):
            level = VERDICT_WARN
        add("cross-section", level, "cross-section=area %.3f" % area,
            "cross-section:area" + sign(area - 1.0))
    offset = c["crossSectionOffset"]
    if offset is not None and offset > th["crossSectionOffsetFail"]:
        add("cross-section", VERDICT_FAIL, "cross-section=offset %.3f" % offset,
            "cross-section:offset")
    for key, line in (("chromaDistance", "chromaFail"), ("hueL1", "hueL1Fail")):
        if c[key] is not None:
            add("colour", _over(c[key], th[line], th[line] * th["colourWarnFraction"]),
                "colour=%s %.4f" % (key, c[key]), "colour:" + key)
    bright = c["brightnessRatio"]
    if bright is not None and _outside(bright, th["brightnessWarnMin"], th["brightnessWarnMax"]):
        add("brightness", VERDICT_WARN, "brightness=%.3f" % bright,
            "brightness" + sign(bright - 1.0))
    if c["coreIou"] is not None and c["coreIou"] < th["coreIouWarnMin"]:
        add("core-iou", VERDICT_WARN, "core-iou=%.3f" % c["coreIou"], "core-iou")
    skip = FX_RCS_REPORT_ONLY_MEASURES if rcs_case else ()
    return [f for m in FX_MEASURES if m not in skip for f in found if f.measure == m]


def fx_failing_measures(entry: Dict, rcs_case: bool = False) -> List[str]:
    """PartParityFxMetrics.FailingMeasures: the measures over their fail line."""
    failing = {f.measure for f in fx_entry_findings(entry, rcs_case) if f.level == VERDICT_FAIL}
    return [m for m in FX_MEASURES if m in failing]


def fx_split_half_disagreement(entry: Dict) -> Optional[str]:
    """PartParityFxViewResult.SplitHalfDisagreement over the written halves: the real side's,
    else the ghost's, as ``<side> <measure> even=<v> odd=<v>``; None when both settled, and
    where both whole-set cores are weak (stray particles must not cap a case that fails
    elsewhere)."""
    whole = [entry.get(s) if isinstance(entry.get(s), dict) else {} for s in ("real", "ghost")]
    if fx_both_cores_weak(*(_fx_count(s, "corePx") for s in whole)):
        return None
    split = entry.get("splitHalf") if isinstance(entry.get("splitHalf"), dict) else {}
    halves = [split.get(h) if isinstance(split.get(h), dict) else {} for h in FX_SPLIT_HALF_KEYS]
    for side in ("real", "ghost"):
        even, odd = [h.get(side) if isinstance(h.get(side), dict) else {} for h in halves]
        cores = (_fx_count(even, "corePx"), _fx_count(odd, "corePx"))
        if fx_presence_reason(entry.get("layer"), *cores) is not None:
            return "%s presence even=%d odd=%d" % ((side,) + cores)
        for name in ("length", "width"):
            a, b = _num(even.get(name + "Px")), _num(odd.get(name + "Px"))
            if a is not None and b is not None and abs(a - b) > FX_SPLIT_HALF_PCT * max(a, b):
                return "%s %s even=%.3f odd=%.3f" % (side, name, a, b)
    return None


def fx_entry_key(entry: Dict) -> FxEntryKey:
    return str(entry.get("route")), str(entry.get("view")), str(entry.get("layer"))


def fx_unsettled_entries(entries: Sequence[Dict]) -> Dict[FxEntryKey, str]:
    """The entries whose own halves disagree, and why (``fx_split_half_disagreement``)."""
    out: Dict[FxEntryKey, str] = {}
    for e in entries:
        why = fx_split_half_disagreement(e)
        if why is not None:
            out[fx_entry_key(e)] = why
    return out


def fx_entry_capped(key: FxEntryKey, unsettled: Collection[FxEntryKey]) -> bool:
    """Whether every fail of this entry is held at ``warn``: its own halves disagree, or it
    is a smoke entry and another view of its route's smoke disagrees. Smoke is one
    world-space trail seen from four views (in orbit a few stray puffs, E.12 U8): a trail
    that did not settle in one view settled in none. A flame's views are judged apart."""
    if key in unsettled:
        return True
    return key[2] == FX_LAYER_SMOKE and any(
        k[0] == key[0] and k[2] == FX_LAYER_SMOKE for k in unsettled)


def fx_self_failing(entry: Dict, rcs_case: bool) -> FrozenSet[str]:
    """The measures one side of an entry fails against ITSELF, its even samples against its
    odd ones: noise at that size is in the picture, so a fail of that measure says nothing
    about the ghost. Direction needs the pixels and is not read here."""
    split = entry.get("splitHalf") if isinstance(entry.get("splitHalf"), dict) else {}
    halves = [split.get(h) if isinstance(split.get(h), dict) else {} for h in FX_SPLIT_HALF_KEYS]
    view, axis = entry.get("view"), entry.get("axis")
    if _fx_axis_problem(view, axis, False) is not None:
        return frozenset()
    out: set = set()
    for side in ("real", "ghost"):
        even, odd = [h.get(side) for h in halves]
        try:
            has_axis = fx_axis(view, axis) is not None
            if any(_fx_side_problems("", m, has_axis) for m in (even, odd)):
                continue  # malformed: fx_block_problems is the finding
            compare = fx_compare(view, axis, FxTile(even, frozenset(), None, []),
                                 FxTile(odd, frozenset(), None, []))
        except ArithmeticError:
            continue  # an integer no double's square can hold: malformed too
        out.update(fx_failing_measures(dict(entry, real=even, ghost=odd, compare=compare),
                                       rcs_case))
    return frozenset(out)


def fx_cap_findings(entry: Dict, findings: Sequence[FxFinding], rcs_case: bool,
                    capped: bool) -> Tuple[FxFinding, ...]:
    """The findings of one entry of a case that has an unsettled entry: a fail stands only in
    an entry that is not ``capped``, when each sample half fails on the same side of the same
    line, and when neither side fails that measure against itself (``fx_self_failing``);
    every other fail counts as ``warn``."""
    split = entry.get("splitHalf") if isinstance(entry.get("splitHalf"), dict) else {}
    halves: List[set] = []
    for h in FX_SPLIT_HALF_KEYS:
        block = split.get(h) if isinstance(split.get(h), dict) else {}
        half = dict(entry, real=block.get("real"), ghost=block.get("ghost"),
                    compare=block.get("compare"))
        halves.append({f.side for f in fx_entry_findings(half, rcs_case)
                       if f.level == VERDICT_FAIL})
    noisy = fx_self_failing(entry, rcs_case)

    def stands(f: FxFinding) -> bool:
        return not capped and all(f.side in h for h in halves) and f.measure not in noisy

    return tuple(replace(f, level=VERDICT_WARN, capped=True)
                 if f.level == VERDICT_FAIL and not stands(f) else f for f in findings)


def fx_entry_skipped(entry: Dict) -> bool:
    """Both cores weak and anything drawn: the entry's measures were skipped (E.4, weak
    cores). Two empty pictures agree, and are judged."""
    sides = [entry.get(s) if isinstance(entry.get(s), dict) else {} for s in ("real", "ghost")]
    return fx_both_cores_weak(*(_fx_count(s, "corePx") for s in sides)) \
        and any(_fx_count(s, "envelopePx") for s in sides)


@dataclass(frozen=True)
class FxEntryVerdict:
    """One judged entry. ``reference``: shown with its would-be verdict, moves nothing (a
    heat or light case's FX entry). Its verdict is the one it counts with: a capped fail is a
    ``warn``, and an entry whose measures were skipped (``fx_entry_skipped``) is ``skipped``,
    which moves nothing (the case's ``fx-weak-core`` flag raises it)."""
    route: str
    view: str
    layer: str
    verdict: str
    findings: Tuple[FxFinding, ...]
    reference: bool

    def to_dict(self) -> Dict[str, Any]:
        return {"route": self.route, "view": self.view, "layer": self.layer,
                "verdict": self.verdict, "reference": self.reference,
                "reasons": [{"measure": f.measure, "level": f.level, "reason": f.reason,
                             "capped": f.capped} for f in self.findings]}


def judge_fx_entry(entry: Dict, rcs_case: bool, reference: bool = False,
                   unsettled: Collection[FxEntryKey] = ()) -> FxEntryVerdict:
    """One entry. ``unsettled``: the unsettled entries of its case, if any; then the
    split-half rule (``fx_cap_findings``) decides which fails stand."""
    findings = tuple(fx_entry_findings(entry, rcs_case))
    if unsettled:
        findings = fx_cap_findings(entry, findings, rcs_case,
                                   fx_entry_capped(fx_entry_key(entry), unsettled))
    verdict = VERDICT_SKIPPED if not findings and fx_entry_skipped(entry) else VERDICT_PASS
    for f in findings:
        if _RANK[f.level] > _RANK[verdict]:
            verdict = f.level
    return FxEntryVerdict(str(entry.get("route")), str(entry.get("view")),
                          str(entry.get("layer")), verdict, findings, reference)


@dataclass(frozen=True)
class FxJudgement:
    """The FX half of one case. ``verdict`` / ``reasons``: the FX measures per route, each entry
    under the split-half cap, and the FX structure raise, which no cap lowers. ``would_be`` (a
    reference case only): the verdict the whole case would have had."""
    family: str
    reference: Optional[str]
    verdict: str
    reasons: Tuple[str, ...]
    routes: Tuple[Tuple[str, str], ...]
    entries: Tuple[FxEntryVerdict, ...]
    unsettled: Tuple[str, ...]
    flags: Tuple[str, ...]
    reported_only: Tuple[Dict[str, Any], ...]
    rcs_power_ratio: Optional[float]
    structure: Dict[str, Any]
    would_be: Optional[str] = None

    @property
    def capped(self) -> bool:
        """Any fail held at ``warn`` by the split-half cap."""
        return any(f.capped for e in self.entries for f in e.findings)

    def to_dict(self) -> Dict[str, Any]:
        out = {"family": self.family, "reference": self.reference, "wouldBe": self.would_be,
               "verdict": self.verdict, "reasons": list(self.reasons),
               "routes": dict(self.routes), "entries": [e.to_dict() for e in self.entries],
               "unsettled": list(self.unsettled), "flags": list(self.flags),
               "structure": dict(self.structure)}
        if self.family == FX_FAMILY_RCS:
            out["rcs"] = {"powerRatio": self.rcs_power_ratio,
                          "reportedOnly": list(self.reported_only)}
        return out


def _fx_len(value: Any) -> Optional[int]:
    if isinstance(value, list):
        return len(value)
    return value if type(value) is int else None


def fx_rcs_known_difference(sidecar: Dict) -> Optional[str]:
    """The R4 flag as PartParityFxRules.FormatRcsKnownDifference writes it, derived from
    ``fx.rcs`` (``fired`` / ``nozzles`` lists or counts); None when the block does not say,
    or when every nozzle fired at the recorded power (no difference)."""
    rcs = (fx_block(sidecar) or {}).get("rcs")
    if not isinstance(rcs, dict):
        return None
    fired, total = _fx_len(rcs.get("fired")), _fx_len(rcs.get("nozzles"))
    power = _num(rcs.get("powerRatio"))
    if fired is None or total is None:
        return None
    text = "null" if power is None else "%.3f" % power
    if fired == total and text == "1.000":
        return None
    return "%snozzles=%d/%d,power=%s" % (FLAG_RCS_KNOWN_DIFFERENCE_PREFIX, fired, total, text)


def fx_flags(sidecar: Dict, unsettled: bool) -> Tuple[str, ...]:
    """The FX flags and tags of a case, none of which fails it: those written in ``flags`` or
    ``tags``, then the ones its ``fx`` block implies and the writer left out (the union, so a
    dropped one cannot hide): ``fx-not-plateaued:<side>``, ``fx-framing-clamped``,
    ``rcs-known-difference:*``; then the ones Python decides: ``fx-unsettled`` and what was not
    measured (``FX_FLAGS_RAISE_TO_WARN``)."""
    fx = fx_block(sidecar) or {}
    out: List[str] = []
    for key in ("flags", "tags"):
        for f in sidecar.get(key) if isinstance(sidecar.get(key), list) else []:
            if isinstance(f, str) and f not in out and (
                    f == FLAG_FX_FRAMING_CLAMPED or f.startswith(TAG_FX_NOT_PLATEAUED_PREFIX)
                    or f.startswith(FLAG_RCS_KNOWN_DIFFERENCE_PREFIX)):
                out.append(f)
    plateau = fx.get("plateau") if isinstance(fx.get("plateau"), dict) else {}
    for side in FX_SIDES:
        block, tag = plateau.get(side), TAG_FX_NOT_PLATEAUED_PREFIX + side
        if isinstance(block, dict) and block.get("settled") is False and tag not in out:
            out.append(tag)
    framing = fx.get("framing")
    if isinstance(framing, dict) and framing.get("clamped") is True \
            and FLAG_FX_FRAMING_CLAMPED not in out:
        out.append(FLAG_FX_FRAMING_CLAMPED)
    if fx.get("family") == FX_FAMILY_RCS and not any(
            f.startswith(FLAG_RCS_KNOWN_DIFFERENCE_PREFIX) for f in out):
        known = fx_rcs_known_difference(sidecar)
        if known is not None:
            out.append(known)
    if unsettled:
        out.append(CAP_FX_UNSETTLED)
    if is_fx_stochastic(sidecar) and not _fx_setup_fault(sidecar):
        structure = fx.get("structure") if isinstance(fx.get("structure"), dict) else {}
        if not all(isinstance(structure.get(k), list) for k in ("fxMissingOnGhost", "fxExtraOnGhost")):
            out.append(FLAG_FX_STRUCTURE_NOT_MEASURED)
        for e in fx_entries(sidecar):
            axis = e.get("axis") if isinstance(e.get("axis"), dict) else {}
            if e.get("view") == FX_VIEW_AXIS and not (_num(axis.get("nozzleDiameterPx")) or 0.0) > 0.0:
                out.append(FLAG_FX_NOZZLE_NOT_MEASURED)
                break
        if not fx_real_effect(sidecar):
            out.append(FLAG_FX_NO_REAL_EFFECT)
        for e in fx_entries(sidecar):
            weak = fx_weak_core_flag(e) if str(e.get("route")) in FX_JUDGED_ROUTES else None
            if weak is not None and weak not in out:
                out.append(weak)
    return tuple(out)


def _fx_rendered_assets(entries: Any) -> List[str]:
    """The keys of the RENDERED FX assets of one ``fx.structure`` list: an entry is a key
    string or a ``{"key", "rendered"}`` object; one that does not say it was not rendered
    counts (a writer that drops the field cannot lower a verdict)."""
    keys = []
    for e in entries if isinstance(entries, list) else []:
        if isinstance(e, str):
            keys.append(e)
        elif isinstance(e, dict) and e.get("rendered") is not False:
            keys.append(str(e.get("key")))
    return keys


def judge_fx(sidecar: Dict) -> Optional[FxJudgement]:
    """The FX half of a case (E.4), None for a case with no ``fx`` block. A plume or rcs
    case: each judged entry's measures, worst per route, each reason
    ``<route>:<view>:<layer>:<reason>``, raised to fail by a rendered FX asset missing or
    extra on the ghost; a case without its entries, or one that contradicts itself
    (``fx_contradictions``), is ``error``. The ``all`` route of an rcs
    case is never judged, and neither are the entries of a heat or light case: a lamp's
    ``flame`` layer is the Waterfall flare its ghost leaves out (E.6)."""
    fx = fx_block(sidecar)
    if fx is None:
        return None
    family = str(fx.get("family"))
    stochastic = family in FX_STOCHASTIC_FAMILIES
    rcs_case = family == FX_FAMILY_RCS
    reference = REFERENCE_WATERFALL if stochastic \
        and fx.get("reference") == REFERENCE_WATERFALL else None
    entries: List[FxEntryVerdict] = []
    reported: List[Dict[str, Any]] = []
    judged = [e for e in fx_entries(sidecar) if str(e.get("route")) in FX_JUDGED_ROUTES]
    loose = fx_unsettled_entries(judged) if stochastic else {}
    unsettled = [fx_reason(*key, why) for key, why in loose.items()]
    for e in judged:
        route = str(e.get("route"))
        entries.append(judge_fx_entry(e, rcs_case, reference=not stochastic, unsettled=loose))
        if rcs_case:
            compare = e.get("compare") if isinstance(e.get("compare"), dict) else {}
            reported.append({"route": route, "view": e.get("view"), "layer": e.get("layer"),
                             "length": _num(compare.get("lengthRatio")),
                             "width": _num(compare.get("widthRatio")),
                             "brightness": _num(compare.get("brightnessRatio"))})
    verdict = VERDICT_PASS
    reasons: List[str] = []
    routes: List[Tuple[str, str]] = []
    missing: List[str] = []
    extra: List[str] = []
    structure = fx.get("structure") if isinstance(fx.get("structure"), dict) else {}
    if stochastic:
        for route in FX_JUDGED_ROUTES:
            worst = VERDICT_PASS
            for j in entries:
                if j.route != route:
                    continue
                reasons.extend(fx_reason(route, j.view, j.layer, f.reason) for f in j.findings)
                if _RANK[j.verdict] > _RANK[worst]:
                    worst = j.verdict
            routes.append((route, worst))
            if _RANK[worst] > _RANK[verdict]:
                verdict = worst
        missing = _fx_rendered_assets(structure.get("fxMissingOnGhost"))
        extra = _fx_rendered_assets(structure.get("fxExtraOnGhost"))
        if missing or extra:
            reasons.extend("missing-fx:%s" % k for k in missing)
            reasons.extend("extra-fx:%s" % k for k in extra)
            verdict = VERDICT_FAIL
        have = {(j.route, j.view, j.layer) for j in entries}
        lost = [k for k in fx_expected_entries(family, False)
                if k[0] in FX_JUDGED_ROUTES and k not in have]
        if lost and not _fx_setup_fault(sidecar):
            reasons.insert(0, "fx-no-entries:%d lost, first %s:%s:%s" % ((len(lost),) + lost[0]))
            verdict = VERDICT_ERROR
    flags = fx_flags(sidecar, bool(unsettled))
    if stochastic and reference is None:
        seen = {f.reason for j in entries for f in j.findings}
        flags += tuple(FLAG_OWNER_FLAGGED_PREFIX + r for r in OWNER_FLAGGED_REASONS if r in seen)
    for f in flags:
        if f.split(":", 1)[0] in FX_FLAGS_RAISE_TO_WARN:
            reasons.append("flag:%s" % f)
            if _RANK[VERDICT_WARN] > _RANK[verdict]:
                verdict = VERDICT_WARN
    contradictions = fx_contradictions(sidecar)
    if contradictions:
        reasons[:0] = contradictions
        verdict = VERDICT_ERROR
    rcs = fx.get("rcs") if isinstance(fx.get("rcs"), dict) else {}
    return FxJudgement(
        family, reference, verdict, tuple(reasons), tuple(routes), tuple(entries),
        tuple(unsettled), flags, tuple(reported),
        _num(rcs.get("powerRatio")) if rcs_case else None,
        {"missing": missing, "extra": extra,
         "fxDiffers": _fx_len(structure.get("fxDiffers")) or 0,
         "lightsDiffer": _fx_len(structure.get("lightsDiffer")) or 0})


def apply_fx(sidecar: Dict, verdict: str, reasons: List[str]) -> Tuple[str, Optional[FxJudgement]]:
    """Fold the FX half into a case's verdict (E.4): the worse of the body and the FX, whose
    entries are already under the split-half cap (a measure that did not converge never
    fails). The body routes, the structure raise and the settled entries keep their verdict:
    no unsettled sample is in them. Appends to ``reasons``, with ``REASON_FX_UNSETTLED`` when
    only capped fails keep the case from ``fail``."""
    fx = judge_fx(sidecar)
    if fx is None:
        return verdict, None
    reasons.extend(fx.reasons)
    if _RANK[fx.verdict] > _RANK[verdict]:
        verdict = fx.verdict
    if fx.capped and verdict == VERDICT_WARN:
        reasons.append(REASON_FX_UNSETTLED)
    return verdict, fx


def apply_fx_reference(verdict: str, fx: Optional[FxJudgement]) -> Tuple[str, Optional[FxJudgement]]:
    """The reference class (E.6): a Waterfall plume or rcs case keeps its would-be verdict
    for display and reads ``reference``, unless it is an ``error`` (a setup fault or a
    writer contradiction is not a finding about the ghost)."""
    if fx is None or fx.reference is None or verdict == VERDICT_ERROR:
        return verdict, fx
    return VERDICT_REFERENCE, replace(fx, would_be=verdict)


# --- the FX control judges (E.5, PartParityFxRules.JudgeFxControl) ------------------------

# Control -> (measures that must fail, measures that must pass).
FX_CONTROL_MEASURES: Dict[str, Tuple[Tuple[str, ...], Tuple[str, ...]]] = {
    "ctl-fx-plume-off": (("presence",), ()),
    "ctl-fx-plume-off-air": (("presence",), ()),
    "ctl-fx-plume-short": (("length",), ("presence", "colour")),
    "ctl-fx-plume-hue": (("colour",), ("presence", "length")),
    "ctl-fx-plume-shift": (("origin", "lateral"), ("length", "colour")),
    "ctl-fx-smoke-dropped": (("smoke",), ("presence",)),
}


def _color_fails(m: Any) -> bool:
    """``PartParitySweepPlan.ColorFails``."""
    return isinstance(m, dict) and (_num(m.get("colorBlobPx")) or 0) > COLOR_BLOB_FAIL_PX


def judge_fx_control(cid: str, sidecar: Optional[Dict]) -> Tuple[str, str]:
    """``PartParityFxRules.JudgeFxControl`` / ``JudgeHeatEventsDroppedControl`` over a
    control's sidecar. The evidence is the event-route entries (the control's pair, its
    ``real`` side the intact one); a measure fails when any entry fails it. Stricter than
    the C# judge in two ways: a plume control whose intact side has no flame core proves
    nothing (``ctl-fx-real-vs-real`` would pass on two empty pictures), and
    ``ctl-fx-plume-off``'s recorded event must be ``EngineIgnited``."""
    if not isinstance(sidecar, dict):
        return CONTROLS_BAD, "no sidecar"
    if _fx_setup_fault(sidecar):
        setup = sidecar.get("setup") if isinstance(sidecar.get("setup"), dict) else {}
        return CONTROLS_BAD, "setup %s" % (setup.get("status") or "<none>")
    proof = sidecar.get("stateProof") if isinstance(sidecar.get("stateProof"), dict) else {}
    events = proof.get("events") if isinstance(proof.get("events"), list) else []
    views = sidecar.get("views")
    views = [v for v in views if isinstance(v, dict)] if isinstance(views, list) else []
    if cid == "ctl-heat-events-dropped":
        if proof.get("reached") is not True:
            return CONTROLS_BAD, "the press never reached heat-hot"
        if not events:
            return CONTROLS_BAD, "no event recorded"
        if not any(_color_fails(v) for v in views):
            return CONTROLS_BAD, "the event-route views pass on colour"
        return CONTROLS_OK, ""
    if cid in ("ctl-plate-light-off", "ctl-plate-light-off-point"):
        plate = (fx_block(sidecar) or {}).get("plate")
        lit = _num(plate.get("litPct")) if isinstance(plate, dict) else None
        if lit is None or lit < PLATE_LIT_MIN_PCT:
            return CONTROLS_BAD, "the real side is under the plate floor"
        if not any(_shape_fails(v) or _color_fails(v) for v in views):
            return CONTROLS_BAD, "the plate views pass"
        return CONTROLS_OK, ""
    entries = [e for e in fx_entries(sidecar) if e.get("route") == ROUTE_EVENT]
    have = {(str(e.get("route")), str(e.get("view")), str(e.get("layer"))) for e in entries}
    if any(k not in have for k in fx_expected_entries(None, True)):
        return CONTROLS_BAD, "the event-route entries are incomplete"
    real_env = any(_fx_count(e.get("real"), "envelopePx") for e in entries)
    ghost_env = any(_fx_count(e.get("ghost"), "envelopePx") for e in entries)
    body_real = any(_num(v.get("realPx")) for v in views)
    body_ghost = any(_num(v.get("ghostPx")) for v in views)
    if cid == "ctl-empty":
        if real_env or ghost_env or body_real or body_ghost:
            return CONTROLS_BAD, "a layer is covered"
        return CONTROLS_OK, ""
    if cid == "ctl-ghost-absent":
        if ghost_env or body_ghost:
            return CONTROLS_BAD, "a ghost layer is covered"
        return (CONTROLS_OK, "") if real_env else (CONTROLS_BAD, "the real side is not covered")
    core = max((_fx_count(e.get("real"), "corePx") for e in entries
                if e.get("layer") == FX_LAYER_FLAME), default=0)
    if core < FX_PRESENCE_MIN_PX:
        return CONTROLS_BAD, "no real core"
    failing = {m for e in entries for m in fx_failing_measures(e)}
    if cid in ("ctl-fx-real-vs-real", "ctl-fx-real-vs-real-air"):
        if failing:
            return CONTROLS_BAD, "fails %s" % ",".join(m for m in FX_MEASURES if m in failing)
        return CONTROLS_OK, ""
    if cid not in FX_CONTROL_MEASURES:
        return CONTROLS_BAD, "not an FX control: %s" % cid
    if cid.startswith("ctl-fx-plume-off") and not any(
            isinstance(e, dict) and e.get("type") == EVENT_ENGINE_IGNITED for e in events):
        return CONTROLS_BAD, "no %s recorded" % EVENT_ENGINE_IGNITED
    must_fail, must_pass = FX_CONTROL_MEASURES[cid]
    for m in must_fail:
        if m not in failing:
            return CONTROLS_BAD, "%s passes" % m
    for m in must_pass:
        if m in failing:
            return CONTROLS_BAD, "%s fails" % m
    return CONTROLS_OK, ""


def control_not_applicable(cid: Any, outcome: Any) -> bool:
    """An optional control recorded ``n/a`` (no eligible subject on the install): met, and
    it owes no sidecar. A required control's ``n/a`` is a problem."""
    return cid in OPTIONAL_CONTROL_IDS and outcome == CONTROLS_NOT_APPLICABLE


# --- the press gate and the documents -----------------------------------------------------

def fx_press_failed(status: Any, detail: Any, sidecar: Optional[Dict]) -> bool:
    """A press that did nothing under a status of its own (E.5: an RCS that never fires, a
    lamp that never lights): a lamp under the plate floor (``plate-not-lit``); an rcs press
    that fired NOTHING, which is ``rcs-input-mismatch`` (the RCS group books ``RCSActivated``
    at zero input, so it does not time out) with an empty ``fired`` set in ``fx.rcs`` or in
    the detail. A press that fired other nozzles than predicted is a wrong prediction, not a
    dead press path."""
    if status == SETUP_PLATE_NOT_LIT:
        return True
    if status != SETUP_RCS_INPUT_MISMATCH:
        return False
    rcs = (fx_block(sidecar) or {}).get("rcs")
    if isinstance(rcs, dict) and rcs.get("fired") is not None:
        return _fx_len(rcs.get("fired")) == 0
    return isinstance(detail, str) and "fired=[]" in detail


def fx_case_counts(cases: Iterable["CaseVerdict"]) -> Optional[Dict[str, Any]]:
    """The FX counts of a sweep (None without an FX case): the cases per family, the
    reference ones and their would-be verdicts, how many carry each FX flag kind, and the
    thresholds that judged them."""
    judged = [c for c in cases if c.fx is not None]
    if not judged:
        return None
    flags: Dict[str, int] = {}
    would: Dict[str, int] = {}
    families: Dict[str, int] = {}
    for c in judged:
        families[c.fx.family] = families.get(c.fx.family, 0) + 1
        for kind in {f.split(":", 1)[0] for f in c.fx.flags}:
            flags[kind] = flags.get(kind, 0) + 1
        if c.verdict == VERDICT_REFERENCE:
            would[str(c.fx.would_be)] = would.get(str(c.fx.would_be), 0) + 1
    return {"cases": len(judged), "families": dict(sorted(families.items())),
            "reference": sum(would.values()), "referenceWouldBe": dict(sorted(would.items())),
            "flags": dict(sorted(flags.items())), "thresholds": dict(FX_THRESHOLDS)}


def fx_summary(cases: Iterable["CaseVerdict"]) -> str:
    """`` reference=<n>`` (E.6) on an FX sweep's summary line, empty otherwise."""
    counts = fx_case_counts(cases)
    return "" if counts is None else " reference=%d" % counts["reference"]


# ---------------------------------------------------------------------------
# Per-view and per-case verdicts.
# ---------------------------------------------------------------------------

def _num(value: Any) -> Optional[float]:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if isinstance(value, float) and (math.isnan(value) or math.isinf(value)):
        return None
    try:
        return float(value)
    except OverflowError:  # a JSON integer no float holds
        return None


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
    # A state case only (design D.6); a stowed case leaves them empty and its verdicts
    # document entry carries exactly the keys it carried before P6.
    state: Optional[str] = None
    family: Optional[str] = None
    context: Optional[str] = None
    routes: Tuple["RouteJudgement", ...] = ()
    state_flags: Tuple[str, ...] = ()
    # A P8 FX case only (design E.4): the FX half of the verdict.
    fx: Optional["FxJudgement"] = None

    @property
    def is_state_case(self) -> bool:
        return self.state is not None

    def to_dict(self) -> Dict[str, Any]:
        out = {"caseId": self.case_id, "part": self.part, "skin": self.skin,
               "source": self.source, "verdict": self.verdict,
               "reasons": list(self.reasons), "worstView": self.worst_view,
               "maxShapeBlobPx": self.max_shape_blob_px,
               "maxShapeBlobPct": self.max_shape_blob_pct,
               "maxColorBlobPx": self.max_color_blob_px,
               "setupStatus": self.setup_status}
        if self.is_state_case:
            out.update({"state": self.state, "family": self.family, "context": self.context,
                        "routes": {r.route: r.to_dict() for r in self.routes},
                        "stateFlags": list(self.state_flags)})
        if self.fx is not None:
            out["fx"] = self.fx.to_dict()
        return out


_NON_VISUAL_FALLBACK_RE = re.compile(r"(^|/)Waterfall(/|$)|mask[^/]*$", re.IGNORECASE)


def is_non_visual(entry: Dict) -> bool:
    """A renderer the ghost leaves out by design: the C# ``nonVisual`` reason
    (``depth-mask`` / ``fx``, from its shader names). A sidecar written before that key
    existed carries no shader, so its path decides: under a ``Waterfall`` segment, or a
    leaf name containing ``mask``."""
    if "nonVisual" in entry:
        return bool(entry.get("nonVisual"))
    return bool(_NON_VISUAL_FALLBACK_RE.search(str(entry.get("key") or "")))


def _rendered_entries(entries: Any) -> List[str]:
    """The keys of RENDERED structure entries that are not non-visual (``is_non_visual``)."""
    keys = []
    for e in entries or []:
        if isinstance(e, dict) and e.get("rendered") is True and not is_non_visual(e):
            keys.append(str(e.get("key")))
    return keys


# PartParityCaseRules.TagContextDependent: wheels, suspension, legs and ground parts,
# whose orbital pose (suspension at full droop, bogies tilted) is not the landed pose
# the ghost keeps by design (owner ruling 2026-11-22: no suspension modelling). Their
# picture differences cap at warn; a missing / extra rendered mesh or a skin mismatch
# still fails.
TAG_CONTEXT_DEPENDENT = "context-dependent"
REASON_CONTEXT_POSE = "context-pose:picture differences capped at warn"
# A part whose real side carries a depth mask the ghost skips by design (owner ruling
# 2026-11-22) is judged like any other: the capture repairs the real mask to the
# depth-only look a player sees (PartParityDepthMaskRepair, design 4.2 step 0), so its
# pictures match the ghost's. A mask the repair could not fix makes the case the setup
# fault ``depth-mask-unrepaired`` (SETUP_DEPTH_MASK_UNREPAIRED), an ``error``.


SKIN_B9_MODULE = "ModuleB9PartSwitch"
SKIN_FLAG_MISMATCH = "skin-mismatch"
# PartParityCaseCapture.FlagGhostNotBuilt: the ghost build failed, so the C# side wrote
# no skin proof flags (it computes them only for a built ghost).
FLAG_GHOST_NOT_BUILT = "ghost-not-built"
SKIN_FLAG_NOT_MODELLED_B9 = "skin-not-modelled:b9"


def skin_flags(skin: Optional[Dict]) -> Tuple[str, ...]:
    """The ghost-defect skin flags, exactly ``PartParitySkinRules.GhostFlags``: no module,
    no flags; a B9 module whose ghost name is null is ``skin-not-modelled:b9`` (the builder
    recorded no subtype, or the subtype carries texture, material or module rules it does not
    apply); otherwise ``skin-mismatch`` when real and ghost differ
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


def judge_skin(skin: Optional[Dict], ghost_built: bool = True) -> Tuple[Optional[str], Tuple[str, ...]]:
    """The skin proof. Returns (verdict or None, reasons). ``requested != real`` (exact)
    is a setup fault (``error``); ``skin-mismatch`` (``skin_flags``) is a ghost defect
    (``fail``) even when the pictures look alike; ``skin-not-modelled:b9`` is a known
    builder gap, reported as ``warn``."""
    if not isinstance(skin, dict):
        return None, ()
    requested, real, ghost = skin.get("requested"), skin.get("real"), skin.get("ghost")
    if requested is not None and real != requested:
        return VERDICT_ERROR, ("skin-not-applied:requested=%s real=%s" % (requested, real),)
    if not ghost_built:
        return None, ()
    flags = skin_flags(skin)
    if SKIN_FLAG_MISMATCH in flags:
        return VERDICT_FAIL, ("skin-mismatch:real=%s ghost=%s" % (real, ghost),)
    if SKIN_FLAG_NOT_MODELLED_B9 in flags:
        return VERDICT_WARN, (SKIN_FLAG_NOT_MODELLED_B9,)
    return None, ()


def skin_flag_disagreement(sidecar: Dict) -> Optional[str]:
    """None when the sidecar's own ``skin-*`` flags (written by the C# runner) equal the
    flags this module derives from its ``skin`` block; else the difference. Only a case
    with views and a built ghost is compared (the C# side computes the flags only for a
    built ghost; a failed build is flagged ``ghost-not-built`` and still captures views)."""
    if not isinstance(sidecar, dict) or not sidecar.get("views"):
        return None
    if FLAG_GHOST_NOT_BUILT in (sidecar.get("flags") or []):
        return None
    wrote = sorted(f for f in sidecar.get("flags") or []
                   if isinstance(f, str) and f.startswith("skin-") and f != "skin-not-applied")
    derived = sorted(skin_flags(sidecar.get("skin")))
    if wrote == derived:
        return None
    return "skin flags: sidecar=%s derived=%s" % (",".join(wrote) or "-", ",".join(derived) or "-")


@dataclass(frozen=True)
class RouteJudgement:
    """One judged route of a case: the view rules, the structural raise and the caps."""
    route: Optional[str]
    verdict: str
    reasons: Tuple[str, ...]
    worst_view: Optional[str]
    max_shape_blob_px: int
    max_shape_blob_pct: Optional[float]
    max_color_blob_px: int

    def to_dict(self) -> Dict[str, Any]:
        return {"route": self.route, "verdict": self.verdict, "reasons": list(self.reasons),
                "worstView": self.worst_view, "maxShapeBlobPx": self.max_shape_blob_px,
                "maxShapeBlobPct": self.max_shape_blob_pct,
                "maxColorBlobPx": self.max_color_blob_px}


# The air case's canopy cap (design D.4): a canopy follows the airflow and wobbles while
# the ghost's holds its sampled pose, so a picture fail whose only structural difference
# is a renderer ROTATION within the measured tilt + wobble + this margin caps at warn.
CANOPY_ROTATION_MARGIN_DEG = 1.0
# PartParityStateRules.CanopyWindowDeg: the capture waits for tilt + wobble under this;
# a capture at or over it is the 4 s timeout one (tagged canopy-wobble:<deg>), which the
# cap refuses. So the bound never exceeds CANOPY_ROTATION_CEILING_DEG.
CANOPY_WINDOW_DEG = 3.0
CANOPY_ROTATION_CEILING_DEG = CANOPY_WINDOW_DEG + CANOPY_ROTATION_MARGIN_DEG
# stateProof.canopy's canopy transform name (ModuleParachute.canopyName), written by the
# air context (S7) and listed in PartParityJson.CanopyKeys / CANOPY_KEYS; a sidecar whose
# value is null or empty is never capped.
CANOPY_TRANSFORM_KEY = "transform"
REASON_CANOPY_AIRFLOW = "canopy-airflow:picture differences capped at warn"
# PartParityStructure.PositionToleranceM / RotationToleranceDeg / ScaleTolerancePct: a
# transformDiffers entry exceeds at least one tolerance; the canopy cap needs scale inside its
# tolerance and position inside its tolerance plus the arc the allowed rotation sweeps at the
# entry's lever arm (canopy_position_allowance_m).
STRUCTURE_POSITION_TOLERANCE_M = 0.001
STRUCTURE_ROTATION_TOLERANCE_DEG = 0.5
STRUCTURE_SCALE_TOLERANCE_PCT = 1.0
# A clean structure (no transformDiffers at all) on an air case in its window is a canopy
# turn under the structure's own 0.5 deg rotation tolerance; it explains a picture fail only
# when the measured turn (|tilt| + |wobble|) is at least this. Below it, even a 5 m lever arm
# sweeps under 1 cm, under a pixel at the air tiles' scale, so the pictures cannot differ
# because of it.
CANOPY_MIN_TURN_DEG = 0.1


@dataclass(frozen=True)
class CanopyCap:
    """What the air case's canopy cap allows: renderer rotations up to ``bound_deg``, on
    renderers under the canopy transform ``transform`` only."""
    bound_deg: float
    transform: str
    # The measured turn |tilt| + |wobble| (the bound without its margin).
    turn_deg: float = 0.0


def canopy_cap_for(sidecar: Dict) -> Optional[CanopyCap]:
    """The canopy cap of an air case (design D.4), or None (no cap). It needs: context
    ``air``; numeric ``stateProof.canopy.tiltDeg`` and ``wobbleDeg`` whose absolute sum is
    under ``CANOPY_WINDOW_DEG`` (the capture found its window; a timeout capture is not
    capped) and no ``canopy-wobble:<deg>`` marker in ``tags`` or ``flags`` (the same
    timeout, wherever the C# side writes it); a non-empty ``stateProof.canopy.transform``
    naming the canopy transform. The bound is |tilt| + |wobble| + 1 deg, never over
    ``CANOPY_ROTATION_CEILING_DEG``."""
    if not isinstance(sidecar, dict) or sidecar.get("context") != CONTEXT_AIR:
        return None
    markers = [m for key in ("tags", "flags")
               for m in (sidecar.get(key) if isinstance(sidecar.get(key), list) else [])]
    if any(isinstance(m, str) and m.startswith(TAG_CANOPY_WOBBLE_PREFIX) for m in markers):
        return None
    proof = sidecar.get("stateProof") if isinstance(sidecar.get("stateProof"), dict) else {}
    canopy = proof.get("canopy") if isinstance(proof.get("canopy"), dict) else {}
    tilt, wobble = _num(canopy.get("tiltDeg")), _num(canopy.get("wobbleDeg"))
    if tilt is None or wobble is None or abs(tilt) + abs(wobble) >= CANOPY_WINDOW_DEG:
        return None
    transform = canopy.get(CANOPY_TRANSFORM_KEY)
    if not isinstance(transform, str) or not transform:
        return None
    turn = abs(tilt) + abs(wobble)
    bound = min(turn + CANOPY_ROTATION_MARGIN_DEG, CANOPY_ROTATION_CEILING_DEG)
    return CanopyCap(bound, transform, turn)


def is_canopy_renderer_key(key: Any, transform: str) -> bool:
    """True when a structure key (a part-relative ``/`` path, maybe ``#k``-suffixed) runs
    through the canopy transform: one of its segments is the canopy transform's name, so
    the renderer is the canopy itself or under it."""
    if not isinstance(key, str) or not transform:
        return False
    return transform in key.split("#", 1)[0].split("/")


def canopy_position_allowance_m(entry: Dict, bound_deg: float) -> float:
    """How far a canopy renderer may sit from its ghost under the canopy cap: the position
    tolerance plus the chord a rigid turn of the canopy subtree sweeps at the renderer's
    lever arm, ``2 d sin(t / 2)``. ``d`` is the entry's ``canopyPivotDistanceM`` (its
    distance from the canopy pivot, written by the C# air context); ``t`` is the entry's OWN
    measured rotation plus the structure's rotation tolerance, never over the cap's bound:
    the records are part-relative, so a rigid turn of the canopy shows on each renderer as
    that same rotation, and a canopy moved WITHOUT turning gets no arc. An entry without a
    readable, non-negative lever arm or rotation gets the tolerance alone (a renderer AT the
    pivot, or a sidecar written before the key existed)."""
    if not isinstance(entry, dict):
        return STRUCTURE_POSITION_TOLERANCE_M
    arm = _num(entry.get(CANOPY_PIVOT_DISTANCE_KEY))
    rot = _num(entry.get("rotationDeltaDeg"))
    if arm is None or arm < 0.0 or rot is None:
        return STRUCTURE_POSITION_TOLERANCE_M
    turn = min(abs(rot) + STRUCTURE_ROTATION_TOLERANCE_DEG, bound_deg)
    return STRUCTURE_POSITION_TOLERANCE_M + 2.0 * arm * math.sin(math.radians(turn) / 2.0)


def _canopy_cap_applies(structure: Dict, cap: Optional[CanopyCap]) -> bool:
    """True when the route's structural diff is explained by the canopy's airflow turn
    within the cap's bound, and nothing else differs (no material, mesh, rendered, missing
    or extra difference). Two shapes: (1) every transform difference is on a canopy renderer
    (``is_canopy_renderer_key``), its rotation within the bound, its scale inside the C#
    tolerance and its position inside ``canopy_position_allowance_m`` (a renderer metres
    from the pivot moves with the turn); (2) no transform differs at all, so every canopy
    renderer turned under the structure's 0.5 deg tolerance, and the measured turn
    (|tilt| + |wobble|, the cap's bound minus its margin) is at least
    ``CANOPY_MIN_TURN_DEG`` (a canopy that did not turn explains nothing)."""
    if cap is None or not isinstance(structure, dict):
        return False
    if _rendered_entries(structure.get("missingOnGhost")) or \
            _rendered_entries(structure.get("extraOnGhost")):
        return False
    if structure.get("materialDiffers") or structure.get("meshDiffers") \
            or structure.get("renderedDiffers"):
        return False
    raw = structure.get("transformDiffers")
    if not isinstance(raw, list):
        # No structure measured (a route whose ghost was not built writes null, which
        # judge_route reads as {}): nothing proves the canopy is the only difference.
        return False
    transforms = [t for t in raw if isinstance(t, dict)]
    if not transforms:
        return cap.turn_deg >= CANOPY_MIN_TURN_DEG
    for t in transforms:
        if not is_canopy_renderer_key(t.get("key"), cap.transform):
            return False
        rot, pos, scl = (_num(t.get("rotationDeltaDeg")), _num(t.get("positionDeltaM")),
                         _num(t.get("scaleDeltaPct")))
        if rot is None or abs(rot) > cap.bound_deg:
            return False
        if pos is None or pos > canopy_position_allowance_m(t, cap.bound_deg):
            return False
        if scl is None or scl > STRUCTURE_SCALE_TOLERANCE_PCT:
            return False
    return True


def judge_route(views: Sequence[Dict], structure: Optional[Dict], tags: Sequence[str],
                route: Optional[str] = None,
                canopy: Optional[CanopyCap] = None) -> RouteJudgement:
    """One route of a case: the worst view, the context-dependent / depth-mask caps (and,
    on an air case, the canopy cap), raised to ``fail`` by a missing / extra RENDERED mesh
    in ``structure``. ``route`` None is a stowed case (reasons carry no prefix); else every
    reason is ``<route>:<reason>``."""
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
    structure = structure if isinstance(structure, dict) else {}
    if TAG_CONTEXT_DEPENDENT in tags and verdict == VERDICT_FAIL:
        verdict = VERDICT_WARN
        reasons.append(REASON_CONTEXT_POSE)
    elif verdict == VERDICT_FAIL and _canopy_cap_applies(structure, canopy):
        verdict = VERDICT_WARN
        reasons.append(REASON_CANOPY_AIRFLOW)
    missing = _rendered_entries(structure.get("missingOnGhost"))
    extra = _rendered_entries(structure.get("extraOnGhost"))
    if missing or extra:
        reasons.extend(["missing-rendered:%s" % k for k in missing])
        reasons.extend(["extra-rendered:%s" % k for k in extra])
        if _RANK[VERDICT_FAIL] > _RANK[verdict]:
            verdict = VERDICT_FAIL
    if route is not None:
        reasons = ["%s:%s" % (route, r) for r in reasons]
    return RouteJudgement(route, verdict, tuple(reasons), worst, max_px, max_pct, max_color)


def start_views(sidecar: Dict) -> Optional[List[Dict]]:
    """The start route's views of a state case: each view's ``start`` block, named after
    its view. None when any view lacks a ``start`` block (the route was not measured)."""
    out: List[Dict] = []
    for v in sidecar.get("views") or []:
        if not isinstance(v, dict):
            continue
        block = v.get("start")
        if not isinstance(block, dict):
            return None
        rec = dict(block)
        rec["view"] = v.get("view")
        out.append(rec)
    return out


# How each state flag moves a state case's verdict (design D.6). Flags not listed
# (state-invisible, sun-tracker-first-frame:<deg>) are report-only.
STATE_FLAGS_RAISE_TO_WARN: Tuple[str, ...] = (FLAG_STATE_NOT_RECORDED,)
STATE_FLAG_PREFIXES_RAISE_TO_WARN: Tuple[str, ...] = (FLAG_STATE_NOT_MODELLED_PREFIX,)
STATE_FLAGS_RAISE_TO_FAIL: Tuple[str, ...] = (FLAG_START_POSE_NOT_FIRST_FRAME,)


def derived_state_flags(sidecar: Dict) -> List[str]:
    """The D.6 flags derived from a state case's ``stateProof`` (the C# side writes the
    same flags into ``flags``; the judge reads the union, so a flag the writer dropped
    cannot hide): a ghost route whose outcome is a string other than ``applied`` is
    ``state-not-modelled:<route>:<outcome>``; a press that ``reached`` its state with an
    empty ``events`` list is ``state-not-recorded``; ``startFirstFrame.atTarget`` false is
    ``start-pose-not-first-frame``. A null (not measured) value derives nothing."""
    proof = sidecar.get("stateProof") if isinstance(sidecar, dict) else None
    if not isinstance(proof, dict):
        return []
    flags: List[str] = []
    for route, key in ((ROUTE_EVENT, "ghostEvent"), (ROUTE_START, "ghostStart")):
        outcome = proof.get(key)
        if isinstance(outcome, str) and outcome and outcome != GHOST_OUTCOME_APPLIED:
            flags.append("%s%s:%s" % (FLAG_STATE_NOT_MODELLED_PREFIX, route, outcome))
    events = proof.get("events")
    if proof.get("reached") is True and isinstance(events, list) and not events:
        flags.append(FLAG_STATE_NOT_RECORDED)
    first = proof.get("startFirstFrame")
    if isinstance(first, dict) and first.get("atTarget") is False:
        flags.append(FLAG_START_POSE_NOT_FIRST_FRAME)
    return flags


def state_invisible(sidecar: Dict) -> bool:
    """True when real vs ``ghost-stowed`` (each view's ``stowedRef``) has no shape blob at
    the warn level in any view: the state does not change the picture (report-only).
    False when there are no views or any view lacks a ``stowedRef`` block."""
    views = [v for v in sidecar.get("views") or [] if isinstance(v, dict)]
    if not views:
        return False
    for v in views:
        ref = v.get("stowedRef")
        if not isinstance(ref, dict):
            return False
        probe = dict(ref)
        probe["view"] = v.get("view")
        probe["colorBlobPx"] = 0
        if judge_view(probe).verdict != VERDICT_PASS:
            return False
    return True


def state_flags(sidecar: Dict) -> Tuple[str, ...]:
    """Every state flag of a case, in a fixed order: the state flags among the C# side's
    ``flags`` (written order), then the derived ones not already written, then
    ``state-invisible`` when it holds. Tags (``still-tracking``, ``canopy-wobble:<deg>``,
    ``context-hidden:<n>``, ``jettison-debris-hidden:<n>``) are not flags and stay out."""
    known = (FLAG_STATE_NOT_RECORDED, FLAG_START_POSE_NOT_FIRST_FRAME, FLAG_STATE_INVISIBLE)
    out: List[str] = []
    for f in sidecar.get("flags") or []:
        if not isinstance(f, str) or f in out:
            continue
        if f in known or f.startswith(FLAG_STATE_NOT_MODELLED_PREFIX) \
                or f.startswith(FLAG_SUN_TRACKER_FIRST_FRAME_PREFIX):
            out.append(f)
    for f in derived_state_flags(sidecar):
        if f not in out:
            out.append(f)
    if FLAG_STATE_INVISIBLE not in out and state_invisible(sidecar):
        out.append(FLAG_STATE_INVISIBLE)
    return tuple(out)


def state_flag_floor(flag: str) -> Optional[str]:
    """The verdict a state flag raises its case to at least, or None (report-only)."""
    if flag in STATE_FLAGS_RAISE_TO_FAIL:
        return VERDICT_FAIL
    if flag in STATE_FLAGS_RAISE_TO_WARN or any(
            flag.startswith(p) for p in STATE_FLAG_PREFIXES_RAISE_TO_WARN):
        return VERDICT_WARN
    return None


ACTION_RESULT_INVOKED = "invoked"


def press_proof_problem(sidecar: Optional[Dict]) -> Optional[str]:
    """A writer contradiction on a case whose setup is NOT a fault (design D.6): the press
    proof says the state was not reached. The C# side records a press that failed as
    ``action-failed`` and a wait that ran out as ``state-timeout``; an ``ok`` (or
    ``still-animating``) case must therefore carry a proof whose ``actionResult`` is
    ``invoked`` and whose ``reached`` is true. Rules:
      * a state case (``is_state_case``) needs a ``stateProof`` with a non-null ``action``;
      * a proof whose ``action`` is non-null needs ``actionResult == "invoked"`` and
        ``reached == true``, whatever the case's state;
      * a proof whose ``action`` is null on a stowed-state case must be a stowed context
        case (context ``stacked`` or an FGV context, ``is_context_case``, with
        ``reached == true``: no press is required); a case with no proof and a stowed
        state (every pre-P6 sidecar, every plain stowed case) is not checked.
    Returns the problem, or None. judge_case makes such a case ``error`` and
    evaluate_sweep lists it as a recompute mismatch, INVALID(parity-instrument): the
    sidecar contradicts itself, so neither its verdict nor its status can be trusted."""
    if not isinstance(sidecar, dict):
        return None
    setup = sidecar.get("setup") if isinstance(sidecar.get("setup"), dict) else {}
    status = setup.get("status")
    if status is None or is_setup_fault(status):
        return None
    state_case = is_state_case(sidecar.get("state"))
    proof = sidecar.get("stateProof")
    if not isinstance(proof, dict):
        return "press-proof: state=%s setup=%s but no stateProof" % (
            sidecar.get("state"), status) if state_case else None
    action = proof.get("action")
    if action is None:
        if state_case:
            return "press-proof: state=%s setup=%s but stateProof.action=null" % (
                sidecar.get("state"), status)
        # The one stowed shape that carries a proof: a stowed context case (the stacked
        # shroud, FGV's intact), which presses nothing but must show its context reached.
        if is_context_case(sidecar.get("context")) and proof.get("reached") is True:
            return None
        return "press-proof: state=%s context=%s setup=%s stateProof.action=null reached=%s " \
               "(only a reached stowed context case may skip the press)" % (
                   sidecar.get("state"), sidecar.get("context"), status, proof.get("reached"))
    result, reached = proof.get("actionResult"), proof.get("reached")
    if result == ACTION_RESULT_INVOKED and reached is True:
        return None
    return "press-proof: setup=%s action=%s actionResult=%s reached=%s" % (
        status, action, result, reached)


def _non_alone(context: Any) -> bool:
    return context is not None and context != CONTEXT_ALONE


def case_row_problems(manifest: Dict, sidecars: Dict[str, Optional[Dict]]) -> List[str]:
    """Writer cross-checks between a planned case's manifest row and its sidecar (design
    D.2, D.6), for every row that is a state or context case on either side (a state other
    than ``stowed`` or a context other than ``alone``, so a stowed sweep compares nothing):
    the row's and the sidecar's ``state`` and ``context`` must be equal (a state dropped to
    null on either side is a problem). Also, anywhere: a sidecar whose ``stateProof.action``
    is non-null while its ``state`` is not a state case (a press on a case judged as
    stowed), and a ``stateProof.state`` that differs from the sidecar's ``state``. A missing
    sidecar is completeness's, not this check's."""
    problems: List[str] = []
    for entry in manifest.get("cases") or []:
        if not isinstance(entry, dict) or not isinstance(entry.get("caseId"), str):
            continue
        cid = entry["caseId"]
        side = sidecars.get(cid)
        if not isinstance(side, dict):
            continue
        rs, rc = entry.get("state"), entry.get("context")
        ss, sc = side.get("state"), side.get("context")
        if (is_state_case(rs) or is_state_case(ss) or _non_alone(rc) or _non_alone(sc)) \
                and (rs != ss or rc != sc):
            problems.append("%s row state/context=%s/%s sidecar=%s/%s" % (cid, rs, rc, ss, sc))
        proof = side.get("stateProof")
        if isinstance(proof, dict) and proof.get("action") is not None \
                and not is_state_case(ss):
            problems.append("%s stateProof.action=%s on a case whose state is %s"
                            % (cid, proof.get("action"), ss))
        if isinstance(proof, dict) and proof.get("state") != ss:
            problems.append("%s stateProof.state=%s sidecar state=%s"
                            % (cid, proof.get("state"), ss))
    return problems


def run_shape_problem(manifest: Dict, sidecars: Dict[str, Optional[Dict]]) -> Optional[str]:
    """A run that planned state or context cases (any row or sidecar with a state other
    than ``stowed`` or a context other than ``alone``) while its ``settings`` recompute the
    stowed control set (``states`` / ``contexts`` absent or ``none``): the manifest does
    not describe the run it holds (design D.2), INVALID(parity-instrument)."""
    if manifest_control_set(manifest) != CONTROL_IDS:
        return None
    for entry in manifest.get("cases") or []:
        if not isinstance(entry, dict):
            continue
        side = sidecars.get(entry.get("caseId")) if isinstance(entry.get("caseId"), str) else None
        side = side if isinstance(side, dict) else {}
        if is_state_case(entry.get("state")) or is_state_case(side.get("state")) \
                or _non_alone(entry.get("context")) or _non_alone(side.get("context")):
            settings = manifest.get("settings") if isinstance(manifest.get("settings"), dict) else {}
            return ("state/context case %s planned but settings states=%s contexts=%s "
                    "recompute the stowed control set"
                    % (entry.get("caseId"), _setting(settings, SETTING_STATES),
                       _setting(settings, SETTING_CONTEXTS)))
    return None


def judge_case(sidecar: Dict) -> CaseVerdict:
    """Case verdict = the worst view, raised to ``fail`` by a missing / extra RENDERED
    mesh in the structural diff or a skin mismatch; ``error`` for a setup fault (the
    measurements, if any, are not a finding about the ghost).

    A state case (design D.6) is judged on two routes, the event route (the views and
    ``structure``) and the start route (each view's ``start`` and ``structureStart``),
    each like a stowed case; the case takes the worse route (the event route on a tie),
    every reason prefixed by its route. Its state flags then raise it:
    ``state-not-modelled:*`` and ``state-not-recorded`` to at least ``warn``,
    ``start-pose-not-first-frame`` to ``fail``; ``state-invisible`` is report-only. A
    state case with views but no ``start`` block is ``error`` (``start:no-views``). The
    stowed reference row is not judged. A stowed case is judged exactly as before P6.

    A P8 FX case (design E.4, an ``fx`` block) adds its FX half (``apply_fx``): the worse of
    the body routes and the FX measures, an entry whose halves did not converge capped at
    ``warn``; a Waterfall one reads ``reference`` (``apply_fx_reference``)."""
    case_id = str(sidecar.get("caseId") or "?")
    skin = sidecar.get("skin") if isinstance(sidecar.get("skin"), dict) else {}
    status = (sidecar.get("setup") or {}).get("status") if isinstance(sidecar.get("setup"), dict) else None
    views = [v for v in sidecar.get("views") or [] if isinstance(v, dict)]
    tags = sidecar.get("tags") if isinstance(sidecar.get("tags"), list) else []
    state = sidecar.get("state")
    state_case = is_state_case(state)
    routes: Tuple[RouteJudgement, ...] = ()
    flags: Tuple[str, ...] = ()
    if not state_case:
        r = judge_route(views, sidecar.get("structure"), tags)
        verdict, reasons, worst = r.verdict, list(r.reasons), r.worst_view
        max_px, max_pct, max_color = r.max_shape_blob_px, r.max_shape_blob_pct, r.max_color_blob_px
    else:
        cap = canopy_cap_for(sidecar)
        ev = judge_route(views, sidecar.get("structure"), tags, ROUTE_EVENT, cap)
        sv_views = start_views(sidecar)
        st = judge_route(sv_views or [], sidecar.get("structureStart"), tags, ROUTE_START, cap)
        routes = (ev, st)
        worse = st if _RANK[st.verdict] > _RANK[ev.verdict] else ev
        verdict = worse.verdict
        worst = None if worse.worst_view is None else "%s:%s" % (worse.route, worse.worst_view)
        reasons = list(ev.reasons) + list(st.reasons)
        max_px = max(ev.max_shape_blob_px, st.max_shape_blob_px)
        pcts = [p for p in (ev.max_shape_blob_pct, st.max_shape_blob_pct) if p is not None]
        max_pct = max(pcts) if pcts else None
        max_color = max(ev.max_color_blob_px, st.max_color_blob_px)
        if views and sv_views is None:
            reasons.append("%s:no-views" % ROUTE_START)
            verdict = VERDICT_ERROR
        flags = state_flags(sidecar)
        for f in flags:
            floor = state_flag_floor(f)
            if floor is None:
                continue
            reasons.append("flag:%s" % f)
            if _RANK[floor] > _RANK[verdict]:
                verdict = floor
    skin_verdict, skin_reasons = judge_skin(
        skin, ghost_built=FLAG_GHOST_NOT_BUILT not in (sidecar.get("flags") or []))
    reasons.extend(skin_reasons)
    if skin_verdict is not None and _RANK[skin_verdict] > _RANK[verdict]:
        verdict = skin_verdict
    verdict, fx = apply_fx(sidecar, verdict, reasons)
    if status is None or is_setup_fault(status):
        reasons.insert(0, "setup:%s" % (status or "missing"))
        verdict = VERDICT_ERROR
    elif not views:
        reasons.insert(0, "no-views")
        verdict = VERDICT_ERROR
    contradiction = press_proof_problem(sidecar)
    if contradiction is not None:
        reasons.insert(0, contradiction)
        verdict = VERDICT_ERROR
    verdict, fx = apply_fx_reference(verdict, fx)
    proof = sidecar.get("stateProof") if isinstance(sidecar.get("stateProof"), dict) else {}
    return CaseVerdict(case_id, sidecar.get("part"), skin.get("requested"),
                       sidecar.get("source"), verdict, tuple(reasons), worst, max_px,
                       max_pct, max_color, status,
                       state=state if state_case else None,
                       family=proof.get("family") if state_case else None,
                       context=sidecar.get("context") if state_case else None,
                       routes=routes, state_flags=flags, fx=fx)


# ---------------------------------------------------------------------------
# Controls and completeness.
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class ControlsCheck:
    ok: bool
    state: Optional[str]
    outcomes: Tuple[Tuple[str, str], ...]
    problems: Tuple[str, ...]


STATE_CONTROL_IDS: Tuple[str, ...] = (CONTROL_STATE_EVENTS_DROPPED,
                                      CONTROL_STATE_EVENTS_DROPPED_AIR)


def manifest_control_set(manifest: Optional[Dict]) -> Tuple[str, ...]:
    """The control set a manifest's ``settings`` recompute to (``control_set``)."""
    settings = manifest.get("settings") if isinstance(manifest, dict) else None
    return control_set(settings if isinstance(settings, dict) else None)


def control_set_disagreement(manifest: Optional[Dict]) -> Optional[str]:
    """None when the manifest's written ``settings.controlSet`` (the ids comma-joined) is
    absent (a manifest written before P6) or equals the set recomputed from its
    ``states`` / ``contexts``; else the difference (design D.2: INVALID(parity-instrument))."""
    settings = manifest.get("settings") if isinstance(manifest, dict) else None
    if not isinstance(settings, dict) or SETTING_CONTROL_SET not in settings:
        return None
    written = settings.get(SETTING_CONTROL_SET)
    ids = tuple(written.split(",")) if isinstance(written, str) and written else ()
    want = manifest_control_set(manifest)
    if ids == want:
        return None
    return "settings.controlSet=%s recomputed=%s" % (written, ",".join(want))


def judge_state_control(sidecar: Optional[Dict]) -> Tuple[str, str]:
    """``PartParitySweepPlan.JudgeStateControl`` over a state control's sidecar (design
    D.6): ``ok`` only when the setup is not a fault, the press reached the state, at least
    one event was recorded, some event-route view fails on shape and some ``stowedRef``
    fails on shape too. Returns (outcome, detail)."""
    if not isinstance(sidecar, dict):
        return CONTROLS_BAD, "no sidecar"
    setup = sidecar.get("setup") if isinstance(sidecar.get("setup"), dict) else {}
    status = setup.get("status")
    if is_setup_fault(status):
        return CONTROLS_BAD, "setup %s" % (status or "<none>")
    proof = sidecar.get("stateProof") if isinstance(sidecar.get("stateProof"), dict) else {}
    if proof.get("reached") is not True:
        return CONTROLS_BAD, "the press never reached the state"
    events = proof.get("events")
    if not isinstance(events, list) or not events:
        return CONTROLS_BAD, "no event recorded"
    views = [v for v in sidecar.get("views") or [] if isinstance(v, dict)]
    if not views:
        return CONTROLS_BAD, "no views"
    if not any(_shape_fails(v) for v in views):
        return CONTROLS_BAD, "the event-route views pass on shape"
    if not any(_shape_fails(v.get("stowedRef")) for v in views):
        return CONTROLS_BAD, "stowedRef passes on shape in every view"
    return CONTROLS_OK, ""


def _shape_fails(m: Any) -> bool:
    """``PartParitySweepPlan.ShapeFails``: the larger shape blob over the px or pct line."""
    if not isinstance(m, dict):
        return False
    blob = max(_num(m.get("realOnlyBlobPx")) or 0, _num(m.get("ghostOnlyBlobPx")) or 0)
    pct = max(_num(m.get("realOnlyBlobPct")) or 0.0, _num(m.get("ghostOnlyBlobPct")) or 0.0)
    return blob > SHAPE_BLOB_FAIL_PX or pct > SHAPE_BLOB_FAIL_PCT


def check_controls(manifest: Dict,
                   sidecars: Optional[Dict[str, Optional[Dict]]] = None) -> ControlsCheck:
    """Every control of the run's set (``control_set`` over the manifest's ``settings``:
    the stowed eight, the orbit state nine or the air four, design D.2) must be recorded
    with outcome ``ok``, and the manifest's own controls state must read ``ok``. A sweep
    run with ``controls=false`` (state ``off``) proves nothing about the instrument, so it
    fails this check too. With ``sidecars``, a state control the manifest records ``ok`` is
    re-judged from its sidecar (``judge_state_control``) and a disagreement is a problem; in
    an FX run every control is (``judge_fx_control``), and an optional one recorded ``n/a``
    (``control_not_applicable``) is met."""
    outcomes: Dict[str, str] = {}
    for c in manifest.get("controls") or []:
        if isinstance(c, dict) and isinstance(c.get("id"), str):
            outcomes[c["id"]] = str(c.get("outcome"))
    ids = manifest_control_set(manifest)
    settings = manifest.get("settings")
    fx_run = is_fx_run(settings if isinstance(settings, dict) else None)
    problems: List[str] = []
    for cid in ids:
        got = outcomes.get(cid)
        if got is None:
            problems.append("%s:absent" % cid)
        elif control_not_applicable(cid, got):
            continue
        elif got != CONTROLS_OK:
            problems.append("%s:%s" % (cid, got))
        elif sidecars is not None and (fx_run or cid in STATE_CONTROL_IDS):
            outcome, detail = (judge_fx_control(cid, sidecars.get(cid)) if fx_run
                               else judge_state_control(sidecars.get(cid)))
            if outcome != CONTROLS_OK:
                problems.append("%s:recorded-ok-judged-%s(%s)" % (cid, outcome, detail))
    state = (manifest.get("progress") or {}).get("controls") \
        if isinstance(manifest.get("progress"), dict) else None
    if state != CONTROLS_OK:
        problems.append("progress.controls=%s" % state)
    ordered = tuple((cid, outcomes[cid]) for cid in ids if cid in outcomes)
    return ControlsCheck(not problems, state, ordered, tuple(problems))


def host_damage(manifest: Dict) -> Optional[str]:
    """None while the C# host-integrity guard never tripped, else one problem line naming
    the reason and detail (``progress.hostDamaged`` true). A damaged host stops the sweep
    with its remaining cases unrun, so the sweep says nothing about those parts."""
    progress = manifest.get("progress") if isinstance(manifest.get("progress"), dict) else {}
    if progress.get("hostDamaged") is not True:
        return None
    return "host damaged: reason=%s detail=%s" % (progress.get("hostDamage"),
                                                  progress.get("hostDamageDetail"))


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
    return fx_atlas_problem(sidecar, atlases)


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
        outcomes = {c.get("id"): c.get("outcome") for c in manifest.get("controls") or []
                    if isinstance(c, dict) and isinstance(c.get("id"), str)}
        ctl_ids = [c for c in manifest_control_set(manifest)
                   if not control_not_applicable(c, outcomes.get(c))]
        planned_ids = list(ctl_ids) + [c for c in planned_ids if c not in ctl_ids]
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


@dataclass(frozen=True)
class PressCheck:
    """The press gate (design D.6). ``families`` maps a family with planned state cases to
    ``(cases, press-failed)``; ``problems`` names each family over the line."""
    ok: bool
    families: Tuple[Tuple[str, int, int], ...]
    problems: Tuple[str, ...]

    def to_dict(self) -> Dict[str, Any]:
        return {"ok": self.ok,
                "families": {f: {"cases": n, "pressFailed": k} for f, n, k in self.families},
                "problems": list(self.problems)}


# The press gate's bucket for a state case whose row and sidecar both lack a family.
PRESS_UNKNOWN_FAMILY = "unknown"


def check_state_press(manifest: Dict, sidecars: Dict[str, Optional[Dict]]) -> Optional[PressCheck]:
    """The fail-open guard on the press path (design D.6): per family with planned cases,
    when more than half of its cases (so also its only case) end ``action-failed`` or
    ``state-timeout`` (``PRESS_GATE_STATUSES``), the family's press, wait or poll path is
    broken: a problem naming the family, its counts and its first such case's status and
    detail. EVERY planned state case counts (``is_state_case`` of its manifest row's
    ``state`` or of its sidecar's; a stowed context case such as the stacked shroud presses
    nothing and does not count, nor do the controls). Its family is the row's ``family``,
    else the sidecar's ``stateProof.family``, else its ``fx.family``, else
    ``PRESS_UNKNOWN_FAMILY`` (a crashed case's sidecar may carry no proof, so a family-less
    case is still counted); the four FX families count like P6's, and a press that did
    nothing under its own status (``fx_press_failed``) counts as failed; a case's status is
    the sidecar's ``setup.status``, else the row's ``setupStatus``. None when no planned
    case is a state case (a stowed run: the gate does not apply)."""
    ctl = set(ALL_CONTROL_IDS)
    dead_press = set()
    per_family: Dict[str, List[Tuple[str, Optional[str], Optional[str]]]] = {}
    for entry in manifest.get("cases") or []:
        if not isinstance(entry, dict) or not isinstance(entry.get("caseId"), str):
            continue
        cid = entry["caseId"]
        if cid in ctl:
            continue
        side = sidecars.get(cid) if isinstance(sidecars.get(cid), dict) else None
        if not (is_state_case(entry.get("state"))
                or (side is not None and is_state_case(side.get("state")))):
            continue
        proof = (side or {}).get("stateProof")
        family = entry.get("family")
        if not isinstance(family, str) or not family:
            family = proof.get("family") if isinstance(proof, dict) else None
        if not isinstance(family, str) or not family:
            family = (fx_block(side) or {}).get("family")
        if not isinstance(family, str) or not family:
            family = PRESS_UNKNOWN_FAMILY
        setup = (side or {}).get("setup")
        status = setup.get("status") if isinstance(setup, dict) else None
        detail = setup.get("detail") if isinstance(setup, dict) else None
        if status is None:
            status = entry.get("setupStatus")
        if fx_press_failed(status, detail, side):
            dead_press.add(cid)
        per_family.setdefault(family, []).append((cid, status, detail))
    if not per_family:
        return None
    order = {f: i for i, f in enumerate(
        FAMILIES + tuple(f for f in FX_FAMILIES if f not in FAMILIES))}
    rows: List[Tuple[str, int, int]] = []
    problems: List[str] = []
    for family in sorted(per_family, key=lambda f: (order.get(f, len(order)), f)):
        cases = per_family[family]
        failed = [c for c in cases if c[1] in PRESS_GATE_STATUSES or c[0] in dead_press]
        rows.append((family, len(cases), len(failed)))
        if 2 * len(failed) > len(cases):
            cid, status, detail = failed[0]
            problems.append("family=%s %d/%d case(s) %s: first %s %s: %s"
                            % (family, len(failed), len(cases), "|".join(PRESS_GATE_STATUSES),
                               cid, status, detail if detail else "-"))
    return PressCheck(not problems, tuple(rows), tuple(problems))


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
    if is_state_case(sidecar.get("state")):
        return _recompute_state_case(sidecar, atlas, tile, list(cols), list(rows))
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


STOWED_REF_INT_KEYS: Tuple[str, ...] = tuple(k for k in STOWED_REF_KEYS if not k.endswith("Pct"))
STOWED_REF_PCT_KEYS: Tuple[str, ...] = tuple(k for k in STOWED_REF_KEYS if k.endswith("Pct"))
# Each blob percentage of a view's three blocks is 100 * blob / the view's projected box
# area (one area per view: the box is shared by every row).
_PCT_BLOB_KEY = {"realOnlyBlobPct": "realOnlyBlobPx", "ghostOnlyBlobPct": "ghostOnlyBlobPx"}


def start_reload_problem(sidecar: Dict) -> Optional[str]:
    """The reloaded start route's flag and its ``real-start`` atlas row go together, on a state
    case only; None when they agree or no atlas was written."""
    atlas = sidecar.get("atlas") if isinstance(sidecar.get("atlas"), dict) else {}
    rows = atlas.get("rows")
    if not isinstance(rows, list):
        return None
    row, flag = ROW_REAL_START in rows, start_reloaded(sidecar)
    if row == flag and not (row and not is_state_case(sidecar.get("state"))):
        return None
    return "start reload: flag=%s but atlas rows=%s" % (str(flag).lower(), rows)


def state_view_block_problems(sidecar: Dict) -> List[str]:
    """The view-block shape a state case must carry (design D.2, D.6): every view has a
    ``start`` block with every ``START_KEYS`` key and a ``stowedRef`` block with every
    ``STOWED_REF_KEYS`` key, and a reloaded start route has both its flag and its row
    (``start_reload_problem``, any case). A stowed case, or a case with no views, has no other
    problem. Run over every case, not only the recompute sample."""
    reload = start_reload_problem(sidecar)
    problems: List[str] = [] if reload is None else [reload]
    if not is_state_case(sidecar.get("state")):
        return problems
    for v in sidecar.get("views") or []:
        if not isinstance(v, dict):
            continue
        name = v.get("view")
        for block, keys in (("start", START_KEYS), ("stowedRef", STOWED_REF_KEYS)):
            got = v.get(block)
            if not isinstance(got, dict):
                problems.append("%s.%s: block missing" % (name, block))
                continue
            missing = [k for k in keys if k not in got]
            if missing:
                problems.append("%s.%s: missing key(s) %s" % (name, block, ",".join(missing)))
    return problems


def _stowed_ref_pct_diffs(view: Dict, ref: Dict) -> List[str]:
    """The ``stowedRef`` blob percentages checked against the view's own box area: a
    percentage is null exactly when the view's is (no box area), 0 for a zero blob, and
    otherwise equals 100 * its blob / the area every non-zero blob of the view's three
    blocks implies (all of them must imply one area). The sidecar does not carry the area
    itself, so a view whose only non-zero blob is the ``stowedRef`` one checks that
    percentage for being a number and nothing more."""
    name = str(view.get("view") or "?")
    diffs: List[str] = []
    area_null = _num(view.get("realOnlyBlobPct")) is None
    areas: List[float] = []
    if not area_null:
        for block in (view, view.get("start"), ref):
            if not isinstance(block, dict):
                continue
            for pk, bk in _PCT_BLOB_KEY.items():
                pct, blob = _num(block.get(pk)), _num(block.get(bk))
                if pct and blob:
                    areas.append(100.0 * blob / pct)
    area = areas[0] if areas else None
    if areas and any(not _float_equal(a, area) for a in areas[1:]):
        diffs.append("%s.stowedRef: blob percentages imply different box areas %s"
                     % (name, sorted(set(round(a, 6) for a in areas))))
    for pk in STOWED_REF_PCT_KEYS:
        got = ref.get(pk)
        blob = _num(ref.get(_PCT_BLOB_KEY[pk])) or 0.0
        if area_null:
            if got is not None:
                diffs.append("%s.stowedRef.%s:%s!=None" % (name, pk, got))
            continue
        if _num(got) is None:
            diffs.append("%s.stowedRef.%s:%s!=<number>" % (name, pk, got))
        elif blob == 0 and _num(got) != 0:
            diffs.append("%s.stowedRef.%s:%s!=0" % (name, pk, got))
        elif area is not None and not _float_equal(got, 100.0 * blob / area):
            diffs.append("%s.stowedRef.%s:%s!=%s" % (name, pk, got, 100.0 * blob / area))
    return diffs


def _recompute_state_case(sidecar: Dict, atlas: Tuple[int, int, int, bytes], tile: int,
                          cols: List[str], rows: List[str]) -> List[str]:
    """A state case's four-row atlas (design D.6): rows 1 (ghost) and 2 (ghost-start)
    against row 0 with the full recomputed key set (the view, then its ``start`` block),
    row 3 (ghost-stowed) against row 0 for exactly the keys ``stowedRef`` carries: the
    counts exactly, the blob percentages against the view's box area. A case whose start route
    was reloaded (``start_reloaded``) has a fifth row, the loaded real part, and its row 2 is
    read against that one. A missing block or key is a difference."""
    width, height, bpp, pixels = atlas
    reloaded = start_reloaded(sidecar)
    want_rows = list(atlas_rows_for(True, reloaded))
    if rows != want_rows:
        return ["atlas rows=%s (expected %s)" % (rows, want_rows)]
    if width != tile * len(cols) or height != tile * len(want_rows):
        return ["atlas size %dx%d != %d cols x %d rows of %d px"
                % (width, height, len(cols), len(want_rows), tile)]
    if bpp != 4:
        return ["atlas is not RGBA (bytes per pixel %d)" % bpp]
    diffs: List[str] = list(state_view_block_problems(sidecar))
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
        start = view.get("start")
        if isinstance(start, dict):
            start_rows = tile_alpha_rows(pixels, width, bpp, c * tile, 2 * tile, tile)
            real_start = real_rows if not reloaded else tile_alpha_rows(
                pixels, width, bpp, c * tile, want_rows.index(ROW_REAL_START) * tile, tile)
            rec = dict(start)
            rec["view"] = "%s/%s" % (name, ROUTE_START)
            diffs.extend(compare_view(rec, recompute_view(real_start, start_rows, tile)))
        ref = view.get("stowedRef")
        if isinstance(ref, dict):
            stowed_rows = tile_alpha_rows(pixels, width, bpp, c * tile, 3 * tile, tile)
            got = recompute_view(real_rows, stowed_rows, tile)
            for key in STOWED_REF_INT_KEYS:
                if key in ref and ref.get(key) != got[key]:
                    diffs.append("%s/stowedRef.%s:%s!=%s" % (name, key, ref.get(key), got[key]))
            diffs.extend(_stowed_ref_pct_diffs(view, ref))
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
# P5: the committed baseline, the accepted-differences list and the regression gate
# (design section 10).
#
# The baseline ``harness/partparity/baseline-<profile>.json`` holds, per sweep label and
# case, the RAW measures of the reading runs that wrote it: for every judged view (a
# state case's two routes as ``event:<view>`` / ``start:<view>``) the band [lo, hi] of
# ``realOnlyBlobPx``, ``ghostOnlyBlobPx``, ``colorBlobPx`` and ``iou`` over those runs,
# leaving out a band that is exactly the identical-sides value (0 px, iou 1). Verdicts
# are never stored: they are recomputed from the sidecars, so a threshold re-pin needs
# no new baseline. ``harness/partparity/accepted.toml`` lists the known differences
# (case / view / measure, reason, todo id), each a ruling or an open todo.
#
# Armed (``gating = true``), a sweep reds PARSEK-FAIL(part-parity) on any of:
#   * drift: a measure moved past its tolerance on the WORSE side of its band (more blob
#     pixels, a lower IoU), unless an accepted entry marked ``noisy`` covers it;
#   * an unaccepted fail: a failing case with a fail-level reason no accepted entry
#     covers, where the reason is qualitative (a missing / extra rendered mesh, a skin
#     mismatch, a state flag that raises to fail) or the case has no baseline (a new
#     case). A numeric fail reason of a baselined case is judged by the drift rule: a
#     measure inside its band's tolerance that crosses the fail line is the noise of a
#     case that sits near the line in every run, not a regression;
#   * a case the baseline holds that the sweep no longer plans (``gone``), and a gated
#     sweep with no baseline section at all (``no-baseline``).
#   * a qualitative difference whatever the case's verdict, unless an entry names its group:
#     a renderer one side draws and the other hides (``renderedDiffers``; a ghost mesh that
#     exists but is disabled is in no missing / extra list), a ghost that was not built
#     (``ghost-not-built*``), a state the ghost does not model or the recorder did not
#     record (``state-not-*``), a case the capture recorded ``crashed`` (the ghost builder
#     runs inside that guard).
# A baselined case the sweep could NOT measure any more (another setup fault, a view or a
# route missing, a null measure or structure, an FX entry ``skipped`` for weak cores or an FX
# check that did not run, ``fx_unmeasured``) is ``unmeasured``: the armed row is
# INVALID(parity-incomplete), because silence is neither a pass nor a ghost defect. A fault
# or an FX check the baseline itself recorded for that case is a note. Moves to the BETTER side, new
# cases that pass, near-line fails and accepted entries that matched nothing are notes:
# the fix that removes a difference removes its accepted entry and re-pins the baseline
# in the same commit (``harness/tools/part_parity_baseline.py``).
# ---------------------------------------------------------------------------

BASELINE_DIR_NAME = "partparity"
BASELINE_SCHEMA = "parsek-part-parity-baseline/1"
ACCEPTED_FILE = "accepted.toml"
ACCEPTED_SCHEMA = 1
ACCEPTED_TABLE = "accepted"


def baseline_file_name(profile: str) -> str:
    """``baseline-<profile>.json``: one file per install profile, one section per label."""
    return "baseline-%s.json" % profile


BASELINE_MEASURES: Tuple[str, ...] = ("realOnlyBlobPx", "ghostOnlyBlobPx", "colorBlobPx", "iou")
# The value identical sides read; a band equal to it on both ends is not stored.
MEASURE_IDENTITY: Dict[str, float] = {"realOnlyBlobPx": 0, "ghostOnlyBlobPx": 0,
                                      "colorBlobPx": 0, "iou": 1.0}
GROUP_SHAPE = "shape-blob"
GROUP_COLOR = "color-blob"
GROUP_IOU = "iou"
GROUP_STRUCTURE = "structure"
GROUP_SKIN = "skin"
# renderedDiffers: the real side draws a renderer the ghost hides / the reverse.
GROUP_HIDDEN = "hidden"
GROUP_DRAWN = "drawn"
GROUP_FLAG_PREFIX = "flag:"
# An FX measure over its fail line (``fx:<measure>`` of FX_MEASURES) or a rendered FX asset
# only one side has (``fx:structure``). Never covered by ``*``.
GROUP_FX_PREFIX = "fx:"
GROUP_FX_STRUCTURE = GROUP_FX_PREFIX + "structure"
GROUP_ANY = "*"
MEASURE_GROUP: Dict[str, str] = {"realOnlyBlobPx": GROUP_SHAPE, "ghostOnlyBlobPx": GROUP_SHAPE,
                                 "colorBlobPx": GROUP_COLOR, "iou": GROUP_IOU}
ACCEPT_GROUPS: Tuple[str, ...] = (GROUP_SHAPE, GROUP_COLOR, GROUP_IOU, GROUP_STRUCTURE,
                                  GROUP_SKIN, GROUP_HIDDEN, GROUP_DRAWN, GROUP_ANY)
# What ``measure = "*"`` covers. Never structure, skin or flag:*, and never the gate's own
# unexplained-fail group: those need an entry that names them.
PICTURE_GROUPS: Tuple[str, ...] = (GROUP_SHAPE, GROUP_COLOR, GROUP_IOU)
VIEW_ANY = "*"
IOU_DIGITS = 4
# TestCommandPartParitySweep.KnownViews (an accepted entry names one of them).
KNOWN_VIEWS: Tuple[str, ...] = ("side-a", "side-b", "top", "bottom", "side-c", "side-d", "hero")

# Drift tolerances (a measure is worse when it leaves its band by more than these). A
# blob's limit is hi + max(abs, rel * hi); an IoU's is lo - abs. Pinned from the measured
# run-to-run spread of matching reading runs on one build (design Appendix F): edge
# shading and seam-line pixels move a stable case's blobs by tens of pixels, and a
# blob that already reads hundreds of pixels moves by a fraction of itself.
DRIFT_SHAPE_ABS_PX = 48
DRIFT_COLOR_ABS_PX = 192
DRIFT_BLOB_REL = 0.5
DRIFT_IOU_ABS = 0.01
# A noisy entry waives the drift rule, not a picture that is gone: an IoU under this
# fraction of its band's low end reds on a noisy case too (a blank ghost reads IoU 0).
NOISY_IOU_FLOOR = 0.5

GATE_DRIFT = "drift"
GATE_UNACCEPTED_FAIL = "unaccepted-fail"
GATE_GONE = "gone"
GATE_NO_BASELINE = "no-baseline"
GATE_RED_KINDS: Tuple[str, ...] = (GATE_DRIFT, GATE_UNACCEPTED_FAIL, GATE_GONE, GATE_NO_BASELINE)
NOTE_IMPROVED = "improved"
NOTE_NEW_CASE = "new-case"
NOTE_NEAR_LINE_FAIL = "near-line-fail"
NOTE_ACCEPTED_FAIL = "accepted-fail"
NOTE_STALE_ACCEPTED = "stale-accepted"
NOTE_SETUP_FAULT = "setup-fault"
NOTE_REFERENCE = "reference"
# An FX entry or check the baseline runs could not judge either (``fx_unmeasured``).
NOTE_FX_UNMEASURED = "fx-unmeasured"
# A baselined case the sweep could not measure (INVALID, not a red).
GATE_UNMEASURED = "unmeasured"

_CASE_ID_RE = re.compile(r"^[a-z0-9._-]{1,128}$")
_TODO_ID_RE = re.compile(r"^[A-Z0-9][A-Z0-9-]*[A-Z0-9]$")
ACCEPTED_KEYS: Tuple[str, ...] = ("case", "profile", "view", "measure", "noisy", "kind", "reason", "todo")
# What an accepted difference IS, read by the page states (``page_state``) and nothing else:
# ``ruled``, the owner ruled on it by name (the todo says so); ``accepted``, a known cause that
# is not the ghost's (an instrument limit, another mod's effect, a stock artifact); ``defect``, a
# real ghost difference not fixed yet. An entry without one reads as a defect does.
KIND_RULED = "ruled"
KIND_ACCEPTED = "accepted"
KIND_DEFECT = "defect"
ACCEPTED_KINDS: Tuple[str, ...] = (KIND_RULED, KIND_ACCEPTED, KIND_DEFECT)
ACCEPTED_REQUIRED: Tuple[str, ...] = ("case", "view", "measure", "reason", "todo")
_PLACEHOLDER_RE = re.compile(r"^\s*(todo|tbd|fixme|xxx|\?+)?\s*$", re.IGNORECASE)


def view_key(view: str, route: Optional[str]) -> str:
    """A judged view's key: the bare view of a stowed case, ``<route>:<view>`` of a state case."""
    return view if route is None else "%s:%s" % (route, view)


def case_measures(sidecar: Dict) -> Dict[str, Dict[str, float]]:
    """The raw measures of one case by view key (``case_measures`` is what the baseline
    stores and the gate compares): every BASELINE_MEASURES value that is a number. A
    stowed case keys its views bare; a state case keys its event route ``event:<view>``
    (the view block itself) and its start route ``start:<view>`` (each view's ``start``
    block). The ``stowedRef`` block is not judged, so it is not baselined."""
    out: Dict[str, Dict[str, float]] = {}
    state_case = is_state_case(sidecar.get("state"))
    for v in sidecar.get("views") or []:
        if not isinstance(v, dict) or not isinstance(v.get("view"), str):
            continue
        blocks = ((ROUTE_EVENT, v), (ROUTE_START, v.get("start"))) if state_case else ((None, v),)
        for route, block in blocks:
            if not isinstance(block, dict):
                continue
            rec = {}
            for m in BASELINE_MEASURES:
                x = _num(block.get(m))
                if x is not None:
                    rec[m] = x if m == "iou" else int(x)
            out[view_key(v["view"], route)] = rec
    return out


def _band_value(measure: str, lo: float, hi: float) -> List[float]:
    if measure == "iou":
        return [math.floor(lo * 10 ** IOU_DIGITS) / 10 ** IOU_DIGITS,
                math.ceil(hi * 10 ** IOU_DIGITS) / 10 ** IOU_DIGITS]
    return [int(lo), int(hi)]


def baseline_case_entry(sidecars: Sequence[Dict]) -> Dict[str, Any]:
    """One case's baseline entry from its sidecars in the reading runs (one per run):
    ``setup`` the sorted set of setup statuses seen, ``views`` the measure bands over the
    runs whose setup is not a fault (a band equal to the identity on both ends is left
    out; a missing view or measure means the identity)."""
    statuses = sorted({str((s.get("setup") or {}).get("status")) for s in sidecars
                       if isinstance(s, dict)})
    bands: Dict[str, Dict[str, List[float]]] = {}
    for s in sidecars:
        if not isinstance(s, dict) or is_setup_fault((s.get("setup") or {}).get("status")):
            continue
        for vk, rec in case_measures(s).items():
            for m, x in rec.items():
                b = bands.setdefault(vk, {}).setdefault(m, [x, x])
                b[0], b[1] = min(b[0], x), max(b[1], x)
    views: Dict[str, Dict[str, List[float]]] = {}
    for vk in sorted(bands):
        kept = {}
        for m in BASELINE_MEASURES:
            if m not in bands[vk]:
                continue
            lo, hi = bands[vk][m]
            if lo == MEASURE_IDENTITY[m] and hi == MEASURE_IDENTITY[m]:
                continue
            kept[m] = _band_value(m, lo, hi)
        if kept:
            views[vk] = kept
    measured = any(isinstance(s, dict) and not is_setup_fault((s.get("setup") or {}).get("status"))
                   for s in sidecars)
    entry = {"setup": statuses, "measured": measured, "views": views}
    if any(isinstance(s, dict) and judge_case(s).verdict == VERDICT_REFERENCE for s in sidecars):
        # Waterfall reference case: never gated (E.6). Written only when true.
        entry["reference"] = True
    fx_lost = sorted({t for s in sidecars if isinstance(s, dict)
                      for t in fx_unmeasured(judge_case(s))})
    if fx_lost:
        # What the runs could not judge of the case's FX. Written only when any.
        entry["fxUnmeasured"] = fx_lost
    return entry


def build_baseline_section(runs: Sequence[Tuple[str, Dict, Dict[str, Optional[Dict]]]],
                           written_utc: Optional[str] = None) -> Dict[str, Any]:
    """A label's baseline section from its reading runs, each ``(run id, manifest,
    sidecars by case id)``. Cases are the union of the runs' planned part cases (controls
    are instrument cases, judged by their recorded outcomes, never baselined)."""
    per_case: Dict[str, List[Dict]] = {}
    hashes: List[str] = []
    views: List[str] = []
    for _run_id, manifest, sidecars in runs:
        views += [str(v) for v in manifest.get("views") or [] if str(v) not in views]
        h = manifest.get("dllHash")
        if isinstance(h, str) and h not in hashes:
            hashes.append(h)
        for entry in manifest.get("cases") or []:
            cid = entry.get("caseId") if isinstance(entry, dict) else None
            if not isinstance(cid, str) or cid in ALL_CONTROL_IDS:
                continue
            per_case.setdefault(cid, [])
            s = sidecars.get(cid)
            if isinstance(s, dict):
                per_case[cid].append(s)
    return {"runs": [r[0] for r in runs], "dllHashes": hashes, "writtenUtc": written_utc, "views": views,
            "tolerances": drift_tolerances(),
            "cases": {cid: baseline_case_entry(per_case[cid]) for cid in sorted(per_case)}}


def drift_tolerances() -> Dict[str, Any]:
    return {"shapeAbsPx": DRIFT_SHAPE_ABS_PX, "colorAbsPx": DRIFT_COLOR_ABS_PX,
            "blobRel": DRIFT_BLOB_REL, "iouAbs": DRIFT_IOU_ABS}


def validate_baseline(doc: Any, profile: Optional[str] = None) -> List[str]:
    """Shape problems of a parsed baseline file (empty: usable)."""
    if not isinstance(doc, dict):
        return ["baseline is not a JSON object"]
    errors = []
    if doc.get("schema") != BASELINE_SCHEMA:
        errors.append("baseline schema=%s (expected %s)" % (doc.get("schema"), BASELINE_SCHEMA))
    if profile is not None and doc.get("profile") != profile:
        errors.append("baseline profile=%s (expected %s)" % (doc.get("profile"), profile))
    labels = doc.get("labels")
    if not isinstance(labels, dict):
        return errors + ["baseline has no labels object"]
    for label, section in sorted(labels.items()):
        if not isinstance(section, dict) or not isinstance(section.get("cases"), dict):
            errors.append("baseline label %s has no cases object" % label)
            continue
        for cid, entry in section["cases"].items():
            if not isinstance(entry, dict) or not isinstance(entry.get("views"), dict):
                errors.append("baseline %s/%s has no views object" % (label, cid))
                continue
            if not isinstance(entry.get("measured"), bool) or not isinstance(entry.get("setup"), list):
                errors.append("baseline %s/%s has no measured / setup" % (label, cid))
            lost = entry.get("fxUnmeasured", [])
            if not isinstance(lost, list) or not all(isinstance(t, str) for t in lost):
                errors.append("baseline %s/%s fxUnmeasured is not a list of strings" % (label, cid))
            for vk, rec in entry["views"].items():
                for m, band in (rec or {}).items():
                    if m not in BASELINE_MEASURES or not (
                            isinstance(band, list) and len(band) == 2
                            and all(_num(x) is not None for x in band) and band[0] <= band[1]):
                        errors.append("baseline %s/%s %s.%s is not a [lo, hi] band"
                                      % (label, cid, vk, m))
    return errors


@dataclass(frozen=True)
class AcceptedEntry:
    """One known difference: ``case`` (exact case id), ``view`` (``*``, a view, or a
    route-qualified ``event:<view>`` / ``start:<view>``), ``measure`` (a group of
    ACCEPT_GROUPS, or ``flag:<flag>`` with an optional trailing ``*`` for a prefix; ``*`` is
    every PICTURE measure and nothing else),
    ``reason`` and ``todo`` (the ruling or open todo that owns it), ``profile`` (None:
    every profile), ``kind`` (ACCEPTED_KINDS or None, for the page alone) and ``noisy`` (the measure moves run to run, so the drift rule is
    waived for it too; otherwise only its fail verdict is waived and drift still gates)."""
    case: str
    view: str
    measure: str
    reason: str
    todo: str
    profile: Optional[str] = None
    noisy: bool = False
    kind: Optional[str] = None

    def covers(self, case_id: str, profile: Optional[str], vk: Optional[str], group: str) -> bool:
        if self.case != case_id:
            return False
        if self.profile is not None and profile is not None and self.profile != profile:
            return False
        if self.view != VIEW_ANY:
            if vk is None:
                return False
            if self.view != vk and not (":" not in self.view and vk.split(":")[-1] == self.view):
                return False
        if self.measure == GROUP_ANY:
            # Pictures only: a wildcard that also waived a missing mesh, a skin mismatch or a
            # state flag would let a hidden renderer through on every noisy case.
            return group in PICTURE_GROUPS
        if self.measure.endswith("*") and self.measure.startswith(GROUP_FLAG_PREFIX):
            return group.startswith(self.measure[:-1])
        return self.measure == group

    def label(self) -> str:
        return "%s %s %s (%s)" % (self.case, self.view, self.measure, self.todo)


def parse_accepted(doc: Any) -> Tuple[List[AcceptedEntry], List[str]]:
    """``accepted.toml`` (already parsed) -> (entries, errors). Every entry needs a case
    id, a view, a measure, a reason and a todo id; a placeholder reason or todo is refused
    (a difference is never accepted silently), as is an exact duplicate."""
    if doc is None:
        return [], []
    if not isinstance(doc, dict):
        return [], ["accepted list is not a table"]
    errors: List[str] = []
    for key in sorted(doc):
        if key not in ("schema", ACCEPTED_TABLE):
            errors.append("accepted list: unknown top-level key %s" % key)
    if doc.get("schema") != ACCEPTED_SCHEMA:
        errors.append("accepted list: schema=%s (expected %d)" % (doc.get("schema"), ACCEPTED_SCHEMA))
    rows = doc.get(ACCEPTED_TABLE, [])
    if not isinstance(rows, list):
        return [], errors + ["accepted list: [[accepted]] must be an array of tables"]
    entries: List[AcceptedEntry] = []
    seen = set()
    for i, row in enumerate(rows):
        where = "accepted[%d]" % i
        if not isinstance(row, dict):
            errors.append("%s is not a table" % where)
            continue
        bad = [k for k in sorted(row) if k not in ACCEPTED_KEYS]
        if bad:
            errors.append("%s: unknown key(s) %s" % (where, ", ".join(bad)))
        missing = [k for k in ACCEPTED_REQUIRED if not isinstance(row.get(k), str) or not row.get(k)]
        if missing:
            errors.append("%s: missing or empty %s" % (where, ", ".join(missing)))
            continue
        case, view, measure = row["case"], row["view"], row["measure"]
        if not _CASE_ID_RE.match(case):
            errors.append("%s: case %r is not a case id" % (where, case))
        if view != VIEW_ANY:
            route, _, bare = view.rpartition(":")
            if bare not in KNOWN_VIEWS + FX_VIEWS or route not in ("", ROUTE_EVENT, ROUTE_START):
                errors.append("%s: view %r is not *, a view or <event|start>:<view>" % (where, view))
        fx_groups = [GROUP_FX_STRUCTURE] + [GROUP_FX_PREFIX + m for m in FX_MEASURES]
        if measure not in ACCEPT_GROUPS and measure not in fx_groups and not (
                measure.startswith(GROUP_FLAG_PREFIX) and len(measure) > len(GROUP_FLAG_PREFIX)):
            errors.append("%s: measure %r is not one of %s, fx:<measure> or flag:<flag>"
                          % (where, measure, ", ".join(ACCEPT_GROUPS)))
        if _PLACEHOLDER_RE.match(row["reason"]):
            errors.append("%s: reason is a placeholder" % where)
        if not _TODO_ID_RE.match(row["todo"]) or _PLACEHOLDER_RE.match(row["todo"]):
            errors.append("%s: todo %r is not a todo id" % (where, row["todo"]))
        profile = row.get("profile")
        if profile is not None and (not isinstance(profile, str) or not profile):
            errors.append("%s: profile must be a non-empty string" % where)
            profile = None
        noisy = row.get("noisy", False)
        if not isinstance(noisy, bool):
            errors.append("%s: noisy must be a bool" % where)
            noisy = False
        kind = row.get("kind")
        if kind is not None and kind not in ACCEPTED_KINDS:
            errors.append("%s: kind %r is not one of %s" % (where, kind, ", ".join(ACCEPTED_KINDS)))
            kind = None
        if kind == KIND_RULED and "owner ruling" not in row["reason"]:
            errors.append("%s: kind ruled needs the owner ruling in its reason" % where)
            kind = None
        entry = AcceptedEntry(case, view, measure, row["reason"], row["todo"], profile, noisy, kind)
        key = (case, view, measure, profile)
        if key in seen:
            errors.append("%s: duplicate of an earlier entry (%s)" % (where, entry.label()))
        seen.add(key)
        entries.append(entry)
    return entries, errors


# Reasons of judge_case, classified for the gate.
_ROUTE_PREFIX_RE = re.compile(r"^(event|start):(.*)$")
_NUMERIC_REASON_RE = re.compile(r"^(shape-blob-px|shape-blob-pct|color-blob-px):([^=]+)=(-?[0-9.]+)$")
_STRUCTURE_REASON_RE = re.compile(r"^(missing-rendered|extra-rendered):")
_CAP_REASONS: Tuple[str, ...] = (REASON_CONTEXT_POSE, REASON_CANOPY_AIRFLOW)
_FX_REASON_RE = re.compile(r"^((event|start):)?fx-[a-z]+(-[a-z]+)?:|^(missing|extra)-fx:")


@dataclass(frozen=True)
class FailReason:
    """A reason that holds a case at ``fail``: ``group`` an ACCEPT_GROUPS member or
    ``flag:<flag>``; ``view`` the view key (None for a case-level reason); ``numeric``
    when it is a measure over its fail line (the drift rule judges it on a baselined case)."""
    reason: str
    group: str
    view: Optional[str]
    numeric: bool


def _split_route(reason: str) -> Tuple[Optional[str], str]:
    m = _ROUTE_PREFIX_RE.match(reason)
    return (m.group(1), m.group(2)) if m else (None, reason)


def _numeric_over_fail_line(kind: str, value: float) -> bool:
    if kind == "shape-blob-px":
        return value > SHAPE_BLOB_FAIL_PX
    if kind == "shape-blob-pct":
        # The reason prints three decimals, so a fail just over the line reads as the line.
        return value >= SHAPE_BLOB_FAIL_PCT
    return value > COLOR_BLOB_FAIL_PX


def fx_fail_reasons(verdict: CaseVerdict) -> List[FailReason]:
    """The fail-level FX findings of a case, read from its FX judgement (its reason text
    mixes warn-level findings in): one per entry and measure over its fail line, one per
    rendered FX asset one side lacks. They carry no baseline band, so each reds unless an
    entry names it. An FX half that failed with no finding to show, or FX reasons with no
    judgement to read, yields one unexplained reason: a body fail never hides it."""
    fx = verdict.fx
    if fx is None:
        texts = [r for r in verdict.reasons if _FX_REASON_RE.match(r)]
        return [FailReason("unexplained-fx:%s" % ",".join(texts[:3]), GROUP_ANY, None, False)] \
            if texts else []
    if fx.verdict != VERDICT_FAIL:
        return []
    out: List[FailReason] = []
    for j in fx.entries:
        if j.reference:
            continue
        for f in j.findings:
            if f.level == VERDICT_FAIL:
                out.append(FailReason(fx_reason(j.route, j.view, j.layer, f.reason),
                                      GROUP_FX_PREFIX + f.measure, view_key(j.view, j.route), False))
    for side in ("missing", "extra"):
        out.extend(FailReason("%s-fx:%s" % (side, k), GROUP_FX_STRUCTURE, None, False)
                   for k in fx.structure.get(side) or [])
    if not out:
        out.append(FailReason("unexplained-fx:%s" % ",".join(fx.reasons[:3]), GROUP_ANY, None, False))
    return out


def fail_reasons(verdict: CaseVerdict) -> List[FailReason]:
    """The reasons that hold a ``fail`` case at fail (empty for any other verdict): a
    numeric view reason over its fail line on a route no cap lowered (the context-pose
    and canopy caps lower a whole route to warn), a missing / extra rendered mesh, a skin
    mismatch, a state flag that raises to fail, and every fail-level FX finding beside
    them. A fail none of these explains yields one ``unexplained`` reason, so the gate
    fails closed."""
    if verdict.verdict != VERDICT_FAIL:
        return []
    capped = set()
    for r in verdict.reasons:
        route, rest = _split_route(r)
        if rest in _CAP_REASONS:
            capped.add(route)
    out: List[FailReason] = []
    for r in verdict.reasons:
        route, rest = _split_route(r)
        m = _NUMERIC_REASON_RE.match(rest)
        if m:
            if route in capped or not _numeric_over_fail_line(m.group(1), float(m.group(3))):
                continue
            group = GROUP_COLOR if m.group(1) == "color-blob-px" else GROUP_SHAPE
            out.append(FailReason(r, group, view_key(m.group(2), route), True))
        elif _STRUCTURE_REASON_RE.match(rest):
            out.append(FailReason(r, GROUP_STRUCTURE, None, False))
        elif rest.startswith(SKIN_FLAG_MISMATCH + ":"):
            out.append(FailReason(r, GROUP_SKIN, None, False))
        elif rest.startswith("flag:") and state_flag_floor(rest[len("flag:"):]) == VERDICT_FAIL:
            out.append(FailReason(r, GROUP_FLAG_PREFIX + rest[len("flag:"):], None, False))
    out.extend(fx_fail_reasons(verdict))
    if not out:
        out.append(FailReason("unexplained-fail:%s" % ",".join(verdict.reasons[:3]),
                              GROUP_ANY, None, False))
    return out


# The flags of an FX check that did not run (``FX_FLAGS_RAISE_TO_WARN`` without the weak-core
# one: a weak core's measures are judged unless the entry is ``skipped``).
_FX_NOT_RUN_FLAGS: Tuple[str, ...] = (FLAG_FX_STRUCTURE_NOT_MEASURED, FLAG_FX_NOZZLE_NOT_MEASURED,
                                      FLAG_FX_NO_REAL_EFFECT)


def fx_unmeasured(verdict: CaseVerdict) -> Tuple[str, ...]:
    """What the FX half of a judged case did not judge, sorted (empty for any other case, an
    ``error`` and a ``reference`` one): ``skipped:<route>:<view>:<layer>`` per judged entry
    whose two weak cores left only presence (``fx_entry_skipped``), and each flag of a check
    that did not run. A skip needs BOTH cores weak, the real one too, so one the baseline runs
    also show is the part's own effect, never a plume the ghost lost."""
    fx = verdict.fx
    if fx is None or verdict.verdict in (VERDICT_ERROR, VERDICT_REFERENCE):
        return ()
    out = {"skipped:%s:%s:%s" % (e.route, e.view, e.layer) for e in fx.entries
           if e.verdict == VERDICT_SKIPPED and not e.reference}
    out.update(f for f in fx.flags if f in _FX_NOT_RUN_FLAGS)
    return tuple(sorted(out))


@dataclass(frozen=True)
class GateFinding:
    kind: str
    case_id: Optional[str]
    detail: str

    def to_dict(self) -> Dict[str, Any]:
        return {"kind": self.kind, "caseId": self.case_id, "detail": self.detail}

    def text(self) -> str:
        return "%s %s: %s" % (self.kind, self.case_id or "-", self.detail)


@dataclass
class GateResult:
    """The regression gate over one sweep. ``reds`` would red an armed lane; ``notes``
    are report-only; ``accepted`` lists the entries that matched a fail reason."""
    label: str
    profile: Optional[str]
    baseline_runs: Tuple[str, ...]
    reds: List[GateFinding]
    notes: List[GateFinding]
    accepted: List[str]
    unmeasured: List[GateFinding] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        return not self.reds and not self.unmeasured

    def counts(self) -> Dict[str, int]:
        out = {k: 0 for k in GATE_RED_KINDS}
        for f in self.reds:
            out[f.kind] = out.get(f.kind, 0) + 1
        for f in self.notes + self.unmeasured:
            out[f.kind] = out.get(f.kind, 0) + 1
        return out

    def to_dict(self) -> Dict[str, Any]:
        return {"label": self.label, "profile": self.profile,
                "baselineRuns": list(self.baseline_runs), "ok": self.ok,
                "tolerances": drift_tolerances(), "counts": self.counts(),
                "reds": [f.to_dict() for f in self.reds],
                "unmeasured": [f.to_dict() for f in self.unmeasured],
                "notes": [f.to_dict() for f in self.notes],
                "accepted": list(self.accepted)}


def _drift_limit(measure: str, band: Sequence[float]) -> float:
    lo, hi = float(band[0]), float(band[1])
    if measure == "iou":
        return lo - DRIFT_IOU_ABS
    absolute = DRIFT_COLOR_ABS_PX if MEASURE_GROUP[measure] == GROUP_COLOR else DRIFT_SHAPE_ABS_PX
    return hi + max(absolute, DRIFT_BLOB_REL * hi)


def _improve_limit(measure: str, band: Sequence[float]) -> float:
    lo, hi = float(band[0]), float(band[1])
    if measure == "iou":
        return hi + DRIFT_IOU_ABS
    absolute = DRIFT_COLOR_ABS_PX if MEASURE_GROUP[measure] == GROUP_COLOR else DRIFT_SHAPE_ABS_PX
    return lo - max(absolute, DRIFT_BLOB_REL * lo)


def measure_drift(measure: str, value: float, band: Sequence[float]) -> int:
    """+1 when ``value`` left ``band`` past its tolerance on the worse side (more blob
    pixels, a lower IoU), -1 past it on the better side, 0 inside."""
    if measure == "iou":
        if value < _drift_limit(measure, band):
            return 1
        return -1 if value > _improve_limit(measure, band) else 0
    if value > _drift_limit(measure, band):
        return 1
    return -1 if value < _improve_limit(measure, band) else 0


def _band(entry: Dict, vk: str, measure: str) -> List[float]:
    rec = (entry.get("views") or {}).get(vk) or {}
    band = rec.get(measure)
    if isinstance(band, list) and len(band) == 2:
        return [float(band[0]), float(band[1])]
    ident = float(MEASURE_IDENTITY[measure])
    return [ident, ident]


def _fmt(measure: str, value: float) -> str:
    return "%.4f" % value if measure == "iou" else "%d" % int(value)


_GATED_FLAG_PREFIXES: Tuple[str, ...] = (FLAG_GHOST_NOT_BUILT, FLAG_STATE_NOT_MODELLED_PREFIX,
                                         FLAG_STATE_NOT_RECORDED)
_CAPTURE_FAULT_PREFIXES: Tuple[str, ...] = ("exception-", "capture-error:")


def qualitative_differences(sidecar: Dict) -> List[FailReason]:
    """Differences that red whatever the verdict (the rules above): hidden / drawn
    renderers of both routes and the ghost-not-built / state-not-* flags, written or
    derived. Non-visual renderers are left out, as in the judge."""
    out: List[FailReason] = []
    for key in ("structure", "structureStart"):
        structure = sidecar.get(key)
        for e in (structure.get("renderedDiffers") or []) if isinstance(structure, dict) else []:
            # No path fallback here: these entries carry no nonVisual key, and a mesh whose
            # name merely contains "mask" must not hide from the gate.
            if not isinstance(e, dict) or e.get("nonVisual"):
                continue
            real, ghost = str(e.get("real")).lower(), str(e.get("ghost")).lower()
            group = GROUP_HIDDEN if (real, ghost) == ("true", "false") else GROUP_DRAWN
            out.append(FailReason("rendered-differs:%s.%s real=%s ghost=%s"
                                  % (key, e.get("key"), real, ghost), group, None, False))
    flags = list(sidecar.get("flags") or [])
    flags += [f for f in derived_state_flags(sidecar) if f not in flags]
    for f in flags:
        if isinstance(f, str) and f.startswith(_GATED_FLAG_PREFIXES):
            out.append(FailReason("flag:%s" % f, GROUP_FLAG_PREFIX + f, None, False))
    return out


def unmeasured_reason(sidecar: Optional[Dict], manifest: Dict,
                      baseline_views: Sequence[str] = ()) -> Optional[str]:
    """Why a case that has no setup fault still says nothing about its ghost, or None:
    a planned view or a route missing, a measure that is not a number, a structure that
    was not measured, a capture that threw."""
    if not isinstance(sidecar, dict):
        return "no readable sidecar"
    for f in sidecar.get("flags") or []:
        if isinstance(f, str) and f.startswith(_CAPTURE_FAULT_PREFIXES):
            return "flag %s" % f
    state_case = is_state_case(sidecar.get("state"))
    current = case_measures(sidecar)
    # The baseline's own view set too: a view dropped from the manifest is still owed.
    views = [str(v) for v in manifest.get("views") or []]
    views += [v for v in baseline_views if v not in views]
    for view in views or sorted({k.split(":")[-1] for k in current}):
        for route in ((ROUTE_EVENT, ROUTE_START) if state_case else (None,)):
            vk = view_key(str(view), route)
            lost = [m for m in BASELINE_MEASURES if m not in current.get(vk, {})]
            if lost:
                return "view %s has no %s" % (vk, ", ".join(lost))
    for key in ("structure", "structureStart") if state_case else ("structure",):
        if not isinstance(sidecar.get(key), dict):
            return "%s was not measured" % key
    return None


def gate_sweep(label: str, profile: Optional[str], manifest: Dict,
               sidecars: Dict[str, Optional[Dict]], verdicts: Sequence[CaseVerdict],
               section: Optional[Dict], accepted: Sequence[AcceptedEntry]) -> GateResult:
    """The regression gate of one sweep against its baseline ``section`` (None: the
    label has no baseline) and the accepted list (the rules are in the section comment
    above). Pure: the caller reads the files."""
    reds: List[GateFinding] = []
    notes: List[GateFinding] = []
    unmeasured: List[GateFinding] = []
    used: List[str] = []
    entries = [e for e in accepted if e.profile is None or profile is None or e.profile == profile]
    if section is None:
        reds.append(GateFinding(GATE_NO_BASELINE, None,
                                "no baseline section for label %s in %s"
                                % (label, baseline_file_name(profile or "?"))))
        base_cases: Dict[str, Dict] = {}
        runs: Tuple[str, ...] = ()
    else:
        base_cases = section.get("cases") if isinstance(section.get("cases"), dict) else {}
        runs = tuple(str(r) for r in section.get("runs") or [])
    planned = [e["caseId"] for e in manifest.get("cases") or []
               if isinstance(e, dict) and isinstance(e.get("caseId"), str)
               and e["caseId"] not in ALL_CONTROL_IDS]
    planned_set = set(planned)
    matched_entries = set()
    for cv in verdicts:
        cid = cv.case_id
        base = base_cases.get(cid) if section is not None else None
        baselined = isinstance(base, dict) and bool(base.get("measured"))
        side = sidecars.get(cid)
        if cv.verdict == VERDICT_ERROR:
            text = ", ".join(cv.reasons[:2]) or "error"
            if section is None or cv.setup_status in ((base or {}).get("setup") or []) \
                    and is_setup_fault(cv.setup_status):
                notes.append(GateFinding(NOTE_SETUP_FAULT, cid, text))
            elif cv.setup_status == SETUP_CRASHED:
                reds.append(GateFinding(GATE_UNACCEPTED_FAIL, cid, "crashed (the ghost builder "
                                        "runs inside that guard): %s" % text))
            elif baselined:
                unmeasured.append(GateFinding(GATE_UNMEASURED, cid, text))
            else:
                notes.append(GateFinding(NOTE_SETUP_FAULT, cid, text))
            continue
        if cv.verdict == VERDICT_REFERENCE:
            # Shown and measured, never gated (E.6). A case the baseline pinned as an ordinary
            # one that now calls itself a reference would otherwise leave the gate unseen.
            if baselined and not base.get("reference"):
                unmeasured.append(GateFinding(GATE_UNMEASURED, cid, "reads reference; its baseline was judged"))
            else:
                notes.append(GateFinding(NOTE_REFERENCE, cid, "reference (Waterfall), not gated"))
            continue
        for fr in fail_reasons(cv) + (qualitative_differences(side) if isinstance(side, dict) else []):
            cover = next((e for e in entries if e.covers(cid, profile, fr.view, fr.group)), None)
            if cover is not None:
                matched_entries.add(cover)
                used.append("%s <- %s" % (fr.reason, cover.label()))
                notes.append(GateFinding(NOTE_ACCEPTED_FAIL, cid, "%s (%s)" % (fr.reason, cover.todo)))
            elif fr.numeric and baselined:
                notes.append(GateFinding(NOTE_NEAR_LINE_FAIL, cid,
                                         "%s (inside its baseline band's tolerance; the drift "
                                         "rule judges it)" % fr.reason))
            elif section is not None:
                reds.append(GateFinding(GATE_UNACCEPTED_FAIL, cid,
                                        "%s%s" % (fr.reason, "" if baselined else " (no baseline: a new case)")))
        if section is None:
            continue
        known = (base or {}).get("fxUnmeasured") or []
        fx_lost = fx_unmeasured(cv)
        fx_new = [t for t in fx_lost if t not in known] if baselined else []
        fx_noted = [t for t in fx_lost if t not in fx_new]
        if fx_new:
            unmeasured.append(GateFinding(GATE_UNMEASURED, cid, "fx not judged: %s" % ", ".join(fx_new)))
        if fx_noted:
            notes.append(GateFinding(NOTE_FX_UNMEASURED, cid, "fx not judged: %s" % ", ".join(fx_noted)))
        if not baselined:
            if base is None:
                notes.append(GateFinding(NOTE_NEW_CASE, cid, "not in the baseline (verdict %s)" % cv.verdict))
            else:
                notes.append(GateFinding(NOTE_NEW_CASE, cid, "baselined only as a setup fault "
                                         "(verdict %s)" % cv.verdict))
            continue
        lost = unmeasured_reason(side, manifest, section.get("views") or ())
        if lost:
            unmeasured.append(GateFinding(GATE_UNMEASURED, cid, lost))
        current = case_measures(side) if isinstance(side, dict) else {}
        for vk in sorted(current):
            for m in BASELINE_MEASURES:
                if m not in current[vk]:
                    continue
                v = current[vk][m]
                band = _band(base, vk, m)
                d = measure_drift(m, v, band)
                if d == 0:
                    continue
                text = "%s %s %s outside band [%s, %s] (limit %s)" % (
                    vk, m, _fmt(m, v), _fmt(m, band[0]), _fmt(m, band[1]),
                    _fmt(m, _drift_limit(m, band) if d > 0 else _improve_limit(m, band)))
                if d < 0:
                    notes.append(GateFinding(NOTE_IMPROVED, cid, text))
                    continue
                cover = next((e for e in entries if e.noisy
                              and e.covers(cid, profile, vk, MEASURE_GROUP[m])), None)
                if cover is not None and m == "iou" and v < NOISY_IOU_FLOOR * band[0]:
                    cover, text = None, text + " and under the noisy floor"
                if cover is not None:
                    matched_entries.add(cover)
                    used.append("drift %s %s <- %s" % (vk, m, cover.label()))
                    continue
                reds.append(GateFinding(GATE_DRIFT, cid, text))
    if section is not None:
        for cid in sorted(base_cases):
            if cid not in planned_set:
                reds.append(GateFinding(GATE_GONE, cid, "in the baseline but not planned by this sweep"))
    swept = {cv.case_id for cv in verdicts}
    for cid in sorted(planned_set & set(base_cases)):
        if cid not in swept and (base_cases[cid] or {}).get("measured"):
            unmeasured.append(GateFinding(GATE_UNMEASURED, cid, "planned but no readable sidecar"))
    for e in entries:
        if e.case in swept and e not in matched_entries:
            notes.append(GateFinding(NOTE_STALE_ACCEPTED, e.case,
                                     "accepted %s %s (%s) matched nothing in this sweep"
                                     % (e.view, e.measure, e.todo)))
    return GateResult(label, profile, runs, reds, notes, used, unmeasured)


# The page's reading of a ``fail`` every reason of which the accepted list explains: neither
# moves the verdict, the counts or the gate, which keep their own.
PAGE_KNOWN = "known"
PAGE_ACCEPTED = "accepted"
PAGE_STATES: Tuple[str, ...] = (PAGE_ACCEPTED, PAGE_KNOWN)
# What no accepted entry can ever put out of sight: a missing, extra or hidden mesh, a skin, an
# effect one side lacks, a fail nothing explains.
PAGE_NEVER_GROUPS: FrozenSet[str] = frozenset((
    GROUP_STRUCTURE, GROUP_SKIN, GROUP_HIDDEN, GROUP_DRAWN, GROUP_FX_STRUCTURE, GROUP_ANY,
    GROUP_FX_PREFIX + "presence", GROUP_FX_PREFIX + "smoke"))


@dataclass(frozen=True)
class PageState:
    """``state``: PAGE_KNOWN (the owner ruled on every reason by name) or PAGE_ACCEPTED (each
    has a known cause that is not the ghost's, or a ruling). ``entries``: what explains it."""
    state: str
    entries: Tuple[AcceptedEntry, ...]

    @property
    def todos(self) -> Tuple[str, ...]:
        return tuple(sorted({e.todo for e in self.entries}))

    def to_dict(self) -> Dict[str, Any]:
        return {"state": self.state, "todos": list(self.todos),
                "reasons": sorted({e.reason for e in self.entries})}


def page_state(verdict: CaseVerdict, sidecar: Optional[Dict], profile: Optional[str],
               accepted: Sequence[AcceptedEntry], section: Optional[Dict],
               gate: Optional["GateResult"]) -> Optional[PageState]:
    """How the page shows a ``fail`` case, or None when it stays red: every fail reason and every
    qualitative difference must be covered by an entry of kind ``ruled`` or ``accepted`` for the
    sweep's own profile, none may be of PAGE_NEVER_GROUPS, and an owner-flagged case always stays
    red. So does a case whose SIZE nothing bounds: the gate must have judged it against a measured
    band of its label's baseline (``section``, ``gate``) and neither red nor lost it, so a
    difference that grew past its band is not the accepted one. (What a ``noisy`` entry waives
    there it waives here too, Appendix F.)"""
    if verdict.verdict != VERDICT_FAIL or not isinstance(sidecar, dict):
        return None
    if profile is None or section is None or gate is None:
        return None
    base = section.get("cases") if isinstance(section.get("cases"), dict) else {}
    band = base.get(verdict.case_id)
    if not isinstance(band, dict) or not band.get("measured"):
        return None
    if verdict.fx is not None and any(f.startswith(FLAG_OWNER_FLAGGED_PREFIX)
                                      for f in verdict.fx.flags):
        return None
    if any(f.case_id == verdict.case_id for f in gate.reds + gate.unmeasured):
        return None
    used: List[AcceptedEntry] = []
    for fr in fail_reasons(verdict) + qualitative_differences(sidecar):
        if fr.group in PAGE_NEVER_GROUPS:
            return None
        cover = [e for e in accepted if e.covers(verdict.case_id, profile, fr.view, fr.group)]
        if not cover or any(e.kind not in (KIND_RULED, KIND_ACCEPTED) for e in cover):
            return None
        used.extend(cover)
    if not used:
        return None
    state = PAGE_KNOWN if all(e.kind == KIND_RULED for e in used) else PAGE_ACCEPTED
    return PageState(state, tuple(dict.fromkeys(used)))


def sweep_page_states(verdicts: Sequence[CaseVerdict], sidecars: Dict[str, Optional[Dict]],
                      profile: Optional[str], accepted: Sequence[AcceptedEntry],
                      section: Optional[Dict], gate: Optional["GateResult"]
                      ) -> Dict[str, PageState]:
    """``page_state`` of every case that has one."""
    out: Dict[str, PageState] = {}
    for cv in verdicts:
        st = page_state(cv, sidecars.get(cv.case_id), profile, accepted, section, gate)
        if st is not None:
            out[cv.case_id] = st
    return out


def page_summary(sweep: "SweepResult") -> str:
    """`` pageKnown=<n> pageAccepted=<n> ownerFlagged=<n>`` on the summary line, each only when
    it is not 0."""
    states = [s.state for s in sweep.page_states.values()]
    flagged = sum(1 for c in sweep.cases if owner_flags(c))
    return "".join(" %s=%d" % (k, n) for k, n in (
        ("pageKnown", states.count(PAGE_KNOWN)), ("pageAccepted", states.count(PAGE_ACCEPTED)),
        ("ownerFlagged", flagged)) if n)


def owner_flags(verdict: CaseVerdict) -> Tuple[str, ...]:
    """The owner-flagged findings of a case (``OWNER_FLAGGED_REASONS`` keys), in flag order."""
    n = len(FLAG_OWNER_FLAGGED_PREFIX)
    return () if verdict.fx is None else tuple(
        f[n:] for f in verdict.fx.flags if f.startswith(FLAG_OWNER_FLAGGED_PREFIX))


def unaccepted_fails(verdicts: Sequence[CaseVerdict], profile: Optional[str],
                     accepted: Sequence[AcceptedEntry],
                     sidecars: Optional[Dict[str, Optional[Dict]]] = None
                     ) -> List[Tuple[str, FailReason]]:
    """Every fail reason of these verdicts (and, with ``sidecars``, every qualitative
    difference) no accepted entry covers: what the baseline
    writer refuses to pin (a difference is never accepted silently)."""
    entries = [e for e in accepted if e.profile is None or profile is None or e.profile == profile]
    out = []
    for cv in verdicts:
        side = (sidecars or {}).get(cv.case_id)
        extra = qualitative_differences(side) if isinstance(side, dict) \
            and cv.verdict not in (VERDICT_ERROR, VERDICT_REFERENCE) else []
        for fr in fail_reasons(cv) + extra:
            if not any(e.covers(cv.case_id, profile, fr.view, fr.group) for e in entries):
                out.append((cv.case_id, fr))
    return out


def profile_of(manifest: Optional[Dict], fallback: Optional[str]) -> Optional[str]:
    """The install profile a sweep ran on: ``fallback`` (the spec's instanceProfile) when the
    caller has one, else the manifest's ``profile`` without a slot copy's ``-<k>`` (the C#
    side writes the install folder's name), else None (``..`` on sweeps before P6)."""
    if fallback:
        return fallback
    p = (manifest or {}).get("profile")
    if not (isinstance(p, str) and re.match(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", p)):
        return None
    try:
        return slots.parse_slot_name(p)[0]
    except slots.SlotError:
        return p


@dataclass(frozen=True)
class GateInput:
    """What the shell read for one sweep's gate: the profile, the label's baseline
    section (None: absent), the accepted entries and any read error (a baseline or
    accepted file that would not load: the gate reds with it)."""
    profile: Optional[str]
    section: Optional[Dict]
    accepted: Tuple[AcceptedEntry, ...] = ()
    errors: Tuple[str, ...] = ()


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
    # The press gate (design D.6); None for a sweep with no state case (a stowed run).
    press: Optional[PressCheck] = None
    # The P5 regression gate (baseline + accepted list); None when the caller passed no
    # GateInput (then an armed sweep fails on any failing case, the pre-P5 rule).
    gate: Optional[GateResult] = None
    # The failing cases the page shows ``known`` or ``accepted`` (``page_state``), by case id.
    page_states: Dict[str, PageState] = field(default_factory=dict)

    def worst(self, n: int = WORST_CASES_IN_SUMMARY) -> List[CaseVerdict]:
        ranked = [c for c in self.cases if c.verdict in (VERDICT_FAIL, VERDICT_WARN, VERDICT_ERROR)]
        ranked.sort(key=lambda c: (-_RANK[c.verdict], -c.max_shape_blob_px,
                                   -c.max_color_blob_px, c.case_id))
        return ranked[:n]


def _count(cases: Iterable[CaseVerdict]) -> Dict[str, int]:
    counts = {v: 0 for v in VERDICTS}
    n = 0
    for c in cases:
        counts[c.verdict] = counts.get(c.verdict, 0) + 1
        n += 1
    counts["cases"] = n
    return counts


def evaluate_sweep(sweep: SweepInput, run_id: str, gating: bool,
                   read_png: Optional[PngReader],
                   atlas_path: Callable[[str], str],
                   gate_input: Optional[GateInput] = None) -> SweepResult:
    """Judge one sweep directory. ``read_png(path) -> (w, h, bpp, pixels)`` decodes an
    atlas; ``atlas_path(file_name)`` resolves a file of this sweep to a path the reader
    takes. ``read_png`` None skips the recompute and records that as an instrument
    problem (the row cannot vouch for the C# numbers without it). ``gate_input`` runs the
    P5 regression gate (``gate_sweep``) over the judged cases: armed, the sweep FAILs on
    any gate red instead of on any failing case; report-only, the gate is recorded."""
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

    controls = check_controls(manifest, sweep.sidecars)
    completeness = check_completeness(manifest, sweep.sidecars, sweep.isolate_lines,
                                      sweep.inflight_lines, sweep.atlases)
    press = check_state_press(manifest, sweep.sidecars)
    ctl_ids = manifest_control_set(manifest)

    planned = [e["caseId"] for e in manifest.get("cases") or []
               if isinstance(e, dict) and isinstance(e.get("caseId"), str)]
    part_ids = [cid for cid in planned if cid not in ALL_CONTROL_IDS]
    cases = [judge_case(sweep.sidecars[cid]) for cid in part_ids
             if isinstance(sweep.sidecars.get(cid), dict)]

    # The instrument check: every control with an atlas, plus the seeded sample of
    # the part cases with an atlas.
    def has_atlas(cid: str) -> bool:
        s = sweep.sidecars.get(cid)
        meta = s.get("atlas") if isinstance(s, dict) else None
        return isinstance(meta, dict) and meta.get("file") in sweep.atlases

    controls_with_atlas = [cid for cid in ctl_ids if has_atlas(cid)]
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
    # The state view blocks (design D.6): every state case with views must carry its
    # start and stowedRef blocks whole, sampled or not (the sampled ones are re-checked by
    # the recompute itself, so they are skipped here to name each problem once).
    sampled = set(recompute_ids) if read_png is not None else set()
    for cid in part_ids + list(ctl_ids):
        s = sweep.sidecars.get(cid)
        if cid in sampled or not isinstance(s, dict):
            continue
        mismatches.extend("%s %s" % (cid, p) for p in state_view_block_problems(s))
    # The run's written control set against the one its settings recompute to (D.2).
    wrong_set = control_set_disagreement(manifest)
    if wrong_set:
        mismatches.append(wrong_set)
    # The FX half (E.4, E.5): the FX atlas recompute, the block shapes, the run shape.
    fx_ids, fx_mismatches = fx_recompute(sweep.sidecars, sweep.atlases, part_ids, ctl_ids,
                                         run_id, read_png, atlas_path)
    mismatches.extend(fx_mismatches)
    mismatches.extend(fx_run_problems(manifest, sweep.sidecars))
    # Writer cross-checks (D.6): a state / context case whose settings say a stowed run,
    # a row and sidecar that disagree on state or context, an ok case whose press proof
    # says the state was never reached.
    shape = run_shape_problem(manifest, sweep.sidecars)
    if shape:
        mismatches.append(shape)
    mismatches.extend(case_row_problems(manifest, sweep.sidecars))
    for cid in part_ids:
        why = press_proof_problem(sweep.sidecars.get(cid))
        if why:
            mismatches.append("%s %s" % (cid, why))
    if not recompute_ids and read_png is not None:
        mismatches.append("nothing to recompute: no control or part case has an atlas")
    recompute_ids = recompute_ids + fx_ids

    counts = _count(cases)
    gate = None
    if gate_input is not None:
        gate = gate_sweep(sweep.label, gate_input.profile, manifest, sweep.sidecars, cases,
                          gate_input.section, gate_input.accepted)
        for err in gate_input.errors:
            gate.reds.insert(0, GateFinding(GATE_NO_BASELINE, None, err))
    page_states = sweep_page_states(
        cases, sweep.sidecars, gate_input.profile, gate_input.accepted, gate_input.section,
        gate) if gate_input is not None else {}
    damaged = host_damage(manifest)
    if damaged:
        # The root cause outranks its symptoms: the controls, completeness and recompute
        # problems a stopped sweep also shows are still listed below.
        status, subkind = STATUS_INVALID, INVALID_HOST_DAMAGED_SUBKIND
        problems.append(damaged)
        if not controls.ok:
            problems.append("controls: " + ", ".join(controls.problems))
    elif not controls.ok:
        status, subkind = STATUS_INVALID, INVALID_CONTROLS_SUBKIND
        problems.append("controls: " + ", ".join(controls.problems))
    elif press is not None and not press.ok:
        status, subkind = STATUS_INVALID, INVALID_STATE_PRESS_SUBKIND
    elif mismatches:
        status, subkind = STATUS_INVALID, INVALID_INSTRUMENT_SUBKIND
    elif not completeness.ok:
        status, subkind = STATUS_INVALID, INVALID_INCOMPLETE_SUBKIND
    else:
        subkind = None
        if gating:
            failed = bool(gate.reds) if gate is not None else bool(counts[VERDICT_FAIL])
            status = STATUS_FAIL if failed else STATUS_PASS
            if gate is not None and gate.unmeasured:
                # A red is a finding even when another case went unmeasured.
                if not failed:
                    status, subkind = STATUS_INVALID, INVALID_INCOMPLETE_SUBKIND
                problems.append("gate: %d baselined case(s) not measured: %s" % (
                    len(gate.unmeasured), "; ".join(f.text() for f in gate.unmeasured[:5])))
            if failed and gate is not None:
                problems.append("gate: %d red(s): %s" % (
                    len(gate.reds), "; ".join(f.text() for f in gate.reds[:5])
                    + (" ..." if len(gate.reds) > 5 else "")))
        else:
            status = STATUS_REPORT
    if press is not None and not press.ok:
        problems.append("state press: " + "; ".join(press.problems))
    if mismatches:
        problems.append("recompute: %d mismatch(es): %s"
                        % (len(mismatches), "; ".join(mismatches[:5])
                           + (" ..." if len(mismatches) > 5 else "")))
    if not completeness.ok:
        problems.append("completeness: " + "; ".join(completeness.problems))
    return SweepResult(sweep.label, status, subkind, gating, problems, controls,
                       completeness, cases, recompute_ids, mismatches, counts, press, gate,
                       page_states)


# The row's INVALID ranking: the host first (its symptoms show in every other gate), then
# the controls, the state press (design D.6: after the controls, before the recompute),
# the instrument recompute, completeness.
_SUBKIND_ORDER = {INVALID_HOST_DAMAGED_SUBKIND: 0, INVALID_CONTROLS_SUBKIND: 1,
                  INVALID_STATE_PRESS_SUBKIND: 2, INVALID_INSTRUMENT_SUBKIND: 3,
                  INVALID_INCOMPLETE_SUBKIND: 4}


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
    host-damaged > controls > state-press > instrument > incomplete order), else
    FAIL / PASS when armed, else REPORT."""
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
                        len(s.recompute_ids), len(s.recompute_mismatches), worst or "-")
                     + ("" if s.press is None else " press=%s" % ("ok" if s.press.ok else "bad"))
                     + ("" if s.gate is None else " gate=%s reds=%d unmeasured=%d drift=%d unaccepted=%d gone=%d "
                        "accepted=%d nearLine=%d improved=%d new=%d"
                        % ("ok" if s.gate.ok else ("red" if s.gate.reds else "unmeasured"),
                           len(s.gate.reds), len(s.gate.unmeasured),
                           s.gate.counts().get(GATE_DRIFT, 0),
                           s.gate.counts().get(GATE_UNACCEPTED_FAIL, 0),
                           s.gate.counts().get(GATE_GONE, 0),
                           s.gate.counts().get(NOTE_ACCEPTED_FAIL, 0),
                           s.gate.counts().get(NOTE_NEAR_LINE_FAIL, 0),
                           s.gate.counts().get(NOTE_IMPROVED, 0),
                           s.gate.counts().get(NOTE_NEW_CASE, 0))))
        parts[-1] += fx_summary(s.cases) + page_summary(s)
    return "status=%s gating=%s subkind=%s %s" % (
        row.status, row.gating, row.invalid_subkind or "-", " | ".join(parts) or "no sweeps")


def thresholds() -> Dict[str, Any]:
    return {"shapeBlobFailPct": SHAPE_BLOB_FAIL_PCT, "shapeBlobFailPx": SHAPE_BLOB_FAIL_PX,
            "colorBlobFailPx": COLOR_BLOB_FAIL_PX, "warnFraction": WARN_FRACTION,
            "recomputeSampleFraction": RECOMPUTE_SAMPLE_FRACTION}


def state_flag_counts(cases: Iterable[CaseVerdict]) -> Dict[str, int]:
    """How many state cases carry each state flag kind (the part before the first ``:``
    of a prefixed flag, e.g. ``state-not-modelled``), sorted by kind."""
    counts: Dict[str, int] = {}
    for c in cases:
        for kind in sorted({f.split(":", 1)[0] for f in c.state_flags}):
            counts[kind] = counts.get(kind, 0) + 1
    return dict(sorted(counts.items()))


def verdicts_document(sweep: SweepResult, run_id: str) -> Dict[str, Any]:
    """``partparity-verdicts.json`` for one label. A sweep with state cases (its press
    check ran) adds ``statePress`` and ``stateFlags``, one with FX cases ``fx``; a stowed sweep's document is
    exactly the pre-P6 one."""
    c = sweep.controls
    k = sweep.completeness
    doc = {
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
    fx_counts = fx_case_counts(sweep.cases)
    if fx_counts is not None:
        doc["fx"] = fx_counts
    if sweep.press is not None:
        doc["statePress"] = sweep.press.to_dict()
        doc["stateFlags"] = state_flag_counts(sweep.cases)
    if sweep.gate is not None:
        doc["gate"] = sweep.gate.to_dict()
    if sweep.page_states:
        doc["pageStates"] = {cid: s.to_dict() for cid, s in sorted(sweep.page_states.items())}
    flagged = {cv.case_id: list(owner_flags(cv)) for cv in sweep.cases if owner_flags(cv)}
    if flagged:
        doc["ownerFlagged"] = flagged
    return doc


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
                               "reasons": list(w.reasons[:3])} for w in s.worst()],
                    "gate": None if s.gate is None else {
                        "ok": s.gate.ok, "profile": s.gate.profile,
                        "baselineRuns": list(s.gate.baseline_runs),
                        "counts": s.gate.counts(),
                        "reds": [f.text() for f in s.gate.reds[:20]]}}
                   for s in row.sweeps],
    }


# --- the burst measures (PartParityBurstMetrics.cs, design G.3, piece X3) ---------------
#
# A stdlib transcription of PartParityBurstMetrics: every value is computed from the 8-bit
# tiles the burst atlas holds, in the C# order of operations, so the recompute reproduces
# the sidecar exactly where integral and to RECOMPUTE_FLOAT_TOLERANCE otherwise.

BURST_EXACT_KEYS: Tuple[str, ...] = ("corePx", "envelopePx", "saturatedPx")


def _nearest_rank(values: List[float], q: float) -> float:
    """PartParityMetrics.NearestRankPercentile: sorted, rank ceil(q n) clamped to 1..n."""
    values = sorted(values)
    rank = int(math.ceil(q * len(values)))
    rank = max(1, min(len(values), rank))
    return values[rank - 1]


def burst_measure_bin(pixels: bytes, width: int, x0: int, y0: int, tile: int, n: int,
                      source: Optional[Sequence[float]]) -> Tuple[Dict[str, Any], List[str]]:
    """PartParityBurstMetrics.MeasureBin over the ``tile`` px square at (x0, y0) of an RGBA
    image ``width`` px wide: every BURST_BIN_MEASURE_KEYS key, and what breaks the tile rule."""
    k_of = [fx_covered_samples(al, n) for al in range(256)]
    exact = [fx_tile_alpha(k_of[al], n) == al for al in range(256)]
    core_of = [fx_is_core(k, n) for k in k_of]
    problems: List[str] = []
    has_source = (isinstance(source, (list, tuple)) and len(source) >= 2
                  and all(_num(v) is not None for v in source[:2]))
    env = core = sat = sx = sy = un = ur = ug = ub = 0
    bins = [[0, 0, 0] for _ in range(FX_HUE_BINS)]
    distances: List[float] = []
    for y in range(tile):
        start = ((y0 + y) * width + x0) * 4
        row = pixels[start:start + tile * 4]
        if len(row) != tile * 4:
            raise ValueError("burst atlas tile out of range at row %d" % (y0 + y))
        if not row.strip(b"\0"):
            continue
        for x in range(tile):
            i = x * 4
            al = row[i + 3]
            r, g, b = row[i], row[i + 1], row[i + 2]
            if not al:
                if (r or g or b) and len(problems) < 3:
                    problems.append("colour on an uncovered pixel (%d, %d)" % (x, y))
                continue
            if not exact[al] and len(problems) < 3:
                problems.append("alpha %d at (%d, %d) is no count of %d samples" % (al, x, y, n))
            if k_of[al] <= 0:
                continue
            env += 1
            saturated = fx_is_saturated(r, g, b)
            if not saturated:
                h = fx_hue_bin(r, g, b)
                if h >= 0:
                    acc = bins[h]
                    acc[0] += r
                    acc[1] += g
                    acc[2] += b
            if has_source:
                dx, dy = x + 0.5 - source[0], y + 0.5 - source[1]
                distances.append(math.sqrt(dx * dx + dy * dy))
            if not core_of[al]:
                continue
            core += 1
            sx += x
            sy += y
            if saturated:
                sat += 1
                continue
            un += 1
            ur += r
            ug += g
            ub += b
    m: Dict[str, Any] = {k: None for k in BURST_BIN_MEASURE_KEYS}
    m.update({"corePx": core, "envelopePx": env, "saturatedPx": sat})
    if core:
        m["coreCentroid"] = _fx_pixel_mean(sx, sy, core)
    if has_source and distances:
        m["radiusPx"] = _nearest_rank(distances, BURST_THRESHOLDS["burstRadiusPercentile"])
    if un:
        m["meanColor"] = [ur / un, ug / un, ub / un]
        m["luminance"] = fx_luminance(ur, ug, ub) / un
        total = ur + ug + ub
        if total:
            m["chromaticity"] = [ur / total, ug / total]
    m["hueHistogram"] = [fx_luminance(*acc) for acc in bins]
    return m, problems


def burst_measure_side(bins: Sequence[Dict[str, Any]]) -> Dict[str, Any]:
    """PartParityBurstMetrics.MeasureSide: onset (first envelope), peak (largest envelope,
    the earliest on a tie), last core bin and largest core."""
    side: Dict[str, Any] = {"bins": list(bins), "onsetBin": None, "peakBin": None,
                            "lastCoreBin": None, "peakCorePx": 0}
    peak_envelope = 0
    for b, m in enumerate(bins):
        if not isinstance(m, dict):
            continue
        env, core = m.get("envelopePx") or 0, m.get("corePx") or 0
        if env > 0 and side["onsetBin"] is None:
            side["onsetBin"] = b
        if env > peak_envelope:
            peak_envelope = env
            side["peakBin"] = b
        if core > 0:
            side["lastCoreBin"] = b
        if core > side["peakCorePx"]:
            side["peakCorePx"] = core
    return side


def burst_presence_reason(layer: str, real: Dict[str, Any], ghost: Dict[str, Any]) -> Optional[str]:
    """PartParityBurstMetrics.PresenceReason: over the window, one side's largest core at the
    layer's minimum and the other side's 0."""
    smoke = layer == FX_LAYER_SMOKE
    low = FX_SMOKE_MIN_PX if smoke else FX_PRESENCE_MIN_PX
    if real["peakCorePx"] >= low and ghost["peakCorePx"] == 0:
        return FX_REASON_SMOKE_MISSING if smoke else BURST_REASON_MISSING
    if ghost["peakCorePx"] >= low and real["peakCorePx"] == 0:
        return FX_REASON_SMOKE_EXTRA if smoke else BURST_REASON_EXTRA
    return None


def _burst_chroma(a: Any, b: Any) -> Optional[float]:
    if a is None or b is None:
        return None
    dr, dg = b[0] - a[0], b[1] - a[1]
    return math.sqrt(dr * dr + dg * dg)


def _burst_hue_l1(a: Any, b: Any) -> Optional[float]:
    if a is None or b is None or len(a) != len(b):
        return None
    sa = sb = 0.0
    for x, y in zip(a, b):
        sa += x
        sb += y
    if not sa > 0 or not sb > 0:
        return None
    d = 0.0
    for x, y in zip(a, b):
        d += abs(x / sa - y / sb)
    return d


def burst_centroid_offset(real_centroid: Any, ghost_centroid: Any, source: Any) -> Optional[float]:
    """PartParityBurstMetrics.CentroidOffset: each centroid from its own side's source point,
    ghost minus real, its length over the source part's box diameter."""
    if real_centroid is None or ghost_centroid is None or not isinstance(source, dict):
        return None
    rs, gs, d = source.get("realPx"), source.get("ghostPx"), _num(source.get("diameterPx"))
    if rs is None or gs is None or d is None or not d > 0:
        return None
    dx = (ghost_centroid[0] - gs[0]) - (real_centroid[0] - rs[0])
    dy = (ghost_centroid[1] - gs[1]) - (real_centroid[1] - rs[1])
    return math.sqrt(dx * dx + dy * dy) / d


def burst_compare(real: Dict[str, Any], ghost: Dict[str, Any], source: Any,
                  layer: str) -> Dict[str, Any]:
    """PartParityBurstMetrics.Compare: every BURST_COMPARE_KEYS key, ghost minus real."""
    c: Dict[str, Any] = {k: None for k in BURST_COMPARE_KEYS}
    c["presence"] = burst_presence_reason(layer, real, ghost)
    if real["onsetBin"] is not None and ghost["onsetBin"] is not None:
        c["onsetDeltaBins"] = float(ghost["onsetBin"] - real["onsetBin"])
    if real["peakBin"] is not None and ghost["peakBin"] is not None:
        c["peakTimeDeltaBins"] = float(ghost["peakBin"] - real["peakBin"])
    if real["lastCoreBin"] is not None and ghost["lastCoreBin"] is not None:
        c["durationDelta"] = (ghost["lastCoreBin"] - real["lastCoreBin"]) / (real["lastCoreBin"] + 1)
    if real["peakCorePx"] > 0:
        c["peakAreaRatio"] = ghost["peakCorePx"] / real["peakCorePx"]
    p = real["peakBin"]
    if p is not None:
        r, g = real["bins"][p], ghost["bins"][p]
        if isinstance(r, dict) and isinstance(g, dict):
            rr, gr = _num(r.get("radiusPx")), _num(g.get("radiusPx"))
            if rr is not None and gr is not None and rr > 0:
                c["radiusRatio"] = gr / rr
            c["centroidOffset"] = burst_centroid_offset(r.get("coreCentroid"), g.get("coreCentroid"), source)
            c["chromaDistance"] = _burst_chroma(r.get("chromaticity"), g.get("chromaticity"))
            c["hueL1"] = _burst_hue_l1(r.get("hueHistogram"), g.get("hueHistogram"))
            rl, gl = _num(r.get("luminance")), _num(g.get("luminance"))
            if rl is not None and gl is not None and rl > 0:
                c["brightnessRatio"] = gl / rl
    chroma = hue = 0.0
    nc = nh = 0
    for r, g in zip(real["bins"], ghost["bins"]):
        if not isinstance(r, dict) or not isinstance(g, dict):
            continue
        d = _burst_chroma(r.get("chromaticity"), g.get("chromaticity"))
        if d is not None:
            chroma += d
            nc += 1
        h = _burst_hue_l1(r.get("hueHistogram"), g.get("hueHistogram"))
        if h is not None:
            hue += h
            nh += 1
    if nc:
        c["chromaDistanceMean"] = chroma / nc
    if nh:
        c["hueL1Mean"] = hue / nh
    return c


def burst_schedule_compare(real: Sequence[Dict], ghost: Sequence[Dict]) -> Dict[str, Any]:
    """PartParityBurstMetrics.CompareSchedule: every BURST_SCHEDULE_COMPARE_KEYS key, and the
    level under ``level``."""
    real, ghost = list(real or []), list(ghost or [])
    t = BURST_THRESHOLDS
    s: Dict[str, Any] = {"realCount": len(real), "ghostCount": len(ghost),
                         "countDiffers": len(real) != len(ghost), "indexDiffers": False,
                         "powerDiffers": False, "maxOffsetDeltaM": None}
    position = False
    for r, g in zip(real, ghost):
        if r.get("index") != g.get("index"):
            s["indexDiffers"] = True
        elif abs(g.get("power") - r.get("power")) > t["burstSchedulePowerEpsilon"]:
            s["powerDiffers"] = True
        ro, go = r.get("offsetM") or [], g.get("offsetM") or []
        total = 0.0
        for i in range(3):
            d = (go[i] if len(go) > i else 0.0) - (ro[i] if len(ro) > i else 0.0)
            total += d * d
        d = math.sqrt(total)
        if s["maxOffsetDeltaM"] is None or d > s["maxOffsetDeltaM"]:
            s["maxOffsetDeltaM"] = d
        if d > t["burstSchedulePositionToleranceM"]:
            position = True
    fail = s["countDiffers"] or s["indexDiffers"] or position
    s["level"] = LEVEL_FAIL if fail else (LEVEL_WARN if s["powerDiffers"] else LEVEL_OK)
    return s


def burst_failing_measures(layer: str, real: Dict[str, Any], ghost: Dict[str, Any],
                           c: Dict[str, Any]) -> List[str]:
    """PartParityBurstMetrics.FailingMeasures: the measures over their fail line, in
    BURST_MEASURES order (presence alone on a layer one side lacks; only presence where both
    largest cores are weak; ``smoke`` alone on the smoke layer)."""
    weak = real["peakCorePx"] < FX_PRESENCE_MIN_PX and ghost["peakCorePx"] < FX_PRESENCE_MIN_PX

    def fails(measure: str, value: Any) -> bool:
        return value is not None and burst_measure_level(measure, value) == LEVEL_FAIL

    if layer == FX_LAYER_SMOKE:
        bad = c["presence"] is not None or (not weak and fails("peak-area", c["peakAreaRatio"]))
        return ["smoke"] if bad else []
    if c["presence"] is not None:
        return ["presence"]
    if weak:
        return []
    out = [m for m, key in (("onset", "onsetDeltaBins"), ("peak-time", "peakTimeDeltaBins"),
                            ("duration", "durationDelta"), ("peak-area", "peakAreaRatio"),
                            ("radius", "radiusRatio"), ("centroid-offset", "centroidOffset"))
           if fails(m, c[key])]
    if (burst_colour_level(c["chromaDistance"], c["hueL1"]) == LEVEL_FAIL
            or burst_colour_level(c["chromaDistanceMean"], c["hueL1Mean"]) == LEVEL_FAIL):
        out.append("colour")
    return out


def _burst_value_diffs(at: str, recorded: Any, got: Any, exact: bool) -> List[str]:
    if isinstance(got, list):
        if not isinstance(recorded, list) or len(recorded) != len(got):
            return ["%s:%s!=%s" % (at, recorded, got)]
        return [d for i, (a, b) in enumerate(zip(recorded, got))
                for d in _burst_value_diffs("%s[%d]" % (at, i), a, b, exact)]
    if got is None or isinstance(got, str):
        return [] if recorded == got else ["%s:%s!=%s" % (at, recorded, got)]
    if exact:
        ok = type(recorded) is int and recorded == got
    else:
        ok = not isinstance(recorded, (bool, str, list, dict)) and _float_equal(recorded, got)
    return [] if ok else ["%s:%s!=%s" % (at, recorded, got)]


def _burst_side_diffs(at: str, recorded: Any, got: Dict[str, Any]) -> List[str]:
    if not isinstance(recorded, dict):
        return ["%s: block missing" % at]
    out: List[str] = []
    for key in ("onsetBin", "peakBin", "lastCoreBin", "peakCorePx"):
        out.extend(_burst_value_diffs("%s.%s" % (at, key), recorded.get(key), got[key], True))
    return out


def recompute_burst_case(sidecar: Dict, atlas: Tuple[int, int, int, bytes]) -> List[str]:
    """Recompute every ``burst.views[]`` entry from the decoded burst atlas, and the schedule
    diff from the schedule; return the differences with the sidecar. Each bin's tile is
    measured; each side's summary and the comparison are recomputed from those measures; the
    split halves have no tiles, so their comparisons are recomputed from their own recorded
    side blocks. An atlas whose layout does not match ``burst.atlas`` is itself a difference."""
    width, height, _bpp, pixels = atlas
    burst = sidecar.get("burst") if isinstance(sidecar.get("burst"), dict) else {}
    meta = burst.get("atlas") if isinstance(burst.get("atlas"), dict) else {}
    tile, bins = meta.get("tile"), meta.get("bins")
    rows, views = meta.get("rows"), meta.get("views")
    if type(tile) is not int or tile <= 0 or type(bins) is not int or bins <= 0:
        return ["burst atlas block unusable: tile=%s bins=%s" % (tile, bins)]
    if list(rows or []) != list(BURST_ATLAS_ROWS) or not isinstance(views, list) or not views:
        return ["burst atlas rows=%s views=%s" % (rows, views)]
    if (width, height) != (len(views) * bins * tile, len(rows) * tile):
        return ["burst atlas is %dx%d, the block says %dx%d" % (
            width, height, len(views) * bins * tile, len(rows) * tile)]
    samples = burst.get("samples") if isinstance(burst.get("samples"), dict) else {}
    diffs: List[str] = []
    for e in burst.get("views") or []:
        view, layer = e.get("view"), e.get("layer")
        at = "%s:%s" % (view, layer)
        if view not in views or layer not in BURST_LAYERS:
            diffs.append("%s: not in the atlas" % at)
            continue
        source = e.get("source") if isinstance(e.get("source"), dict) else {}
        got_sides: Dict[str, Dict[str, Any]] = {}
        for side in BURST_SIDE_KEYS:
            n_of = samples.get(side)
            if not isinstance(n_of, list) or len(n_of) != bins:
                diffs.append("%s: burst.samples.%s=%s" % (at, side, n_of))
                break
            row = rows.index("%s-%s" % (side, layer))
            measured = []
            for b in range(bins):
                n = n_of[b]
                if type(n) is not int or not 0 <= n <= FX_MAX_SAMPLES:
                    diffs.append("%s: %s samples[%d]=%s" % (at, side, b, n))
                    break
                m, problems = burst_measure_bin(pixels, width, (views.index(view) * bins + b) * tile,
                                                row * tile, tile, n, source.get("%sPx" % side))
                diffs.extend("%s.%s.bins[%d]: %s" % (at, side, b, p) for p in problems)
                measured.append(m)
            recorded = (e.get(side) or {}).get("bins") if isinstance(e.get(side), dict) else None
            if not isinstance(recorded, list) or len(recorded) != len(measured):
                diffs.append("%s.%s.bins: %s entries, %d measured" % (
                    at, side, None if not isinstance(recorded, list) else len(recorded), len(measured)))
                continue
            for b, (rec, m) in enumerate(zip(recorded, measured)):
                for key in BURST_BIN_MEASURE_KEYS:
                    diffs.extend(_burst_value_diffs("%s.%s.bins[%d].%s" % (at, side, b, key),
                                                    (rec or {}).get(key), m[key], key in BURST_EXACT_KEYS))
            got_sides[side] = burst_measure_side(measured)
            diffs.extend(_burst_side_diffs("%s.%s" % (at, side), e.get(side), got_sides[side]))
        if len(got_sides) == 2:
            got = burst_compare(got_sides["real"], got_sides["ghost"], source, layer)
            for key in BURST_COMPARE_KEYS:
                diffs.extend(_burst_value_diffs("%s.compare.%s" % (at, key),
                                                (e.get("compare") or {}).get(key), got[key], False))
        halves = e.get("splitHalf") if isinstance(e.get("splitHalf"), dict) else {}
        for half in FX_SPLIT_HALF_KEYS:
            h = halves.get(half)
            if not isinstance(h, dict) or not isinstance(h.get("real"), dict) or not isinstance(h.get("ghost"), dict):
                diffs.append("%s.splitHalf.%s: block missing" % (at, half))
                continue
            sides = {s: burst_measure_side(h[s].get("bins") or []) for s in BURST_SIDE_KEYS}
            for s in BURST_SIDE_KEYS:
                diffs.extend(_burst_side_diffs("%s.splitHalf.%s.%s" % (at, half, s), h[s], sides[s]))
            got = burst_compare(sides["real"], sides["ghost"], source, layer)
            for key in BURST_COMPARE_KEYS:
                diffs.extend(_burst_value_diffs("%s.splitHalf.%s.compare.%s" % (at, half, key),
                                                (h.get("compare") or {}).get(key), got[key], False))
    schedule = burst.get("schedule") if isinstance(burst.get("schedule"), dict) else {}
    got = burst_schedule_compare(schedule.get("real") or [], schedule.get("ghost") or [])
    recorded = burst.get("scheduleCompare") if isinstance(burst.get("scheduleCompare"), dict) else {}
    for key in BURST_SCHEDULE_COMPARE_KEYS:
        exact = key != "maxOffsetDeltaM"
        if isinstance(got[key], bool):
            if recorded.get(key) is not got[key]:
                diffs.append("scheduleCompare.%s:%s!=%s" % (key, recorded.get(key), got[key]))
        else:
            diffs.extend(_burst_value_diffs("scheduleCompare.%s" % key, recorded.get(key), got[key], exact))
    return diffs
