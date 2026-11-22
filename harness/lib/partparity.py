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
    "stateProof", "structureStart", "stowedCaseId",
    "fx", "fxAtlas", "strip",
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
SETUP_STATUSES: Tuple[str, ...] = (
    SETUP_OK, SETUP_SKIN_NOT_APPLIED, SETUP_SPAWN_TIMEOUT, SETUP_STILL_ANIMATING,
    SETUP_DIED_SETTLING, SETUP_CRASHED, SETUP_DEPTH_MASK_UNREPAIRED, SETUP_ACTION_FAILED,
    SETUP_STATE_TIMEOUT, SETUP_PLATE_NOT_LIT, SETUP_RCS_INPUT_MISMATCH,
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
# OptionalControlIds: no subject on the install records ``n/a`` (ControlsNotApplicable),
# never ``bad``.
OPTIONAL_CONTROL_IDS: Tuple[str, ...] = ("ctl-plate-light-off-point",)
ALL_CONTROL_IDS: Tuple[str, ...] = (
    CONTROL_IDS_ORBIT_STATE + (CONTROL_STATE_EVENTS_DROPPED_AIR,)
    + CONTROL_IDS_FX_ORBIT[2:] + CONTROL_IDS_FX_AIR[2:]
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


def control_set(settings: Optional[Dict]) -> Tuple[str, ...]:
    """TestCommandPartParitySweep.ControlSetFor, recomputed from a manifest's ``settings``
    (design D.2, E.5): an FX run its FX set (the air one for ``contexts=air``); no
    ``states`` / ``contexts`` (absent or ``none``, as every manifest written before P6 reads)
    is the stowed eight; an air run its four; any other state run the eight plus
    ``ctl-state-events-dropped``."""
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


def atlas_rows_for(state_case: bool) -> Tuple[str, ...]:
    """PartParityJson.AtlasRowsFor: four rows for a state case, two for a stowed one."""
    return ATLAS_ROWS_STATE if state_case else ATLAS_ROWS

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
FX_ARG_ALL = "all"
FX_STATES: Tuple[str, ...] = ("burn-full", "burn-low", "rcs-fire", "heat-medium", "heat-hot", "lit")
CONTEXT_PLATE = "plate"
FX_ORBIT_CONTEXTS: Tuple[str, ...] = (CONTEXT_ALONE, CONTEXT_PLATE)
FX_EXCLUSION_REASONS: Tuple[str, ...] = (
    "fx-reentry-camera", "fx-blackbody", "fx-ablator-char", "fx-needs-air", "fx-throttle-locked",
)
# fx.reference of a case judged reference-only (E.6).
REFERENCE_WATERFALL = "waterfall"


def fx_family_for_arg(word: Optional[str]) -> Optional[str]:
    """PartParityFxRules.FamilyForArg: ``plate`` selects the ``light`` family."""
    return {"plume": "plume", "rcs": "rcs", "heat": "heat", "plate": "light"}.get(word or "")


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
FX_DRIVE_KEYS: Tuple[str, ...] = ("ignited", "finalThrust", "currentThrottle", "recordedPower", "cheat")
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
    # A state case only (design D.6); a stowed case leaves them empty and its verdicts
    # document entry carries exactly the keys it carried before P6.
    state: Optional[str] = None
    family: Optional[str] = None
    context: Optional[str] = None
    routes: Tuple["RouteJudgement", ...] = ()
    state_flags: Tuple[str, ...] = ()

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
    stowed reference row is not judged. A stowed case is judged exactly as before P6."""
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
    proof = sidecar.get("stateProof") if isinstance(sidecar.get("stateProof"), dict) else {}
    return CaseVerdict(case_id, sidecar.get("part"), skin.get("requested"),
                       sidecar.get("source"), verdict, tuple(reasons), worst, max_px,
                       max_pct, max_color, status,
                       state=state if state_case else None,
                       family=proof.get("family") if state_case else None,
                       context=sidecar.get("context") if state_case else None,
                       routes=routes, state_flags=flags)


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
    re-judged from its sidecar (``judge_state_control``) and a disagreement is a problem."""
    outcomes: Dict[str, str] = {}
    for c in manifest.get("controls") or []:
        if isinstance(c, dict) and isinstance(c.get("id"), str):
            outcomes[c["id"]] = str(c.get("outcome"))
    ids = manifest_control_set(manifest)
    problems: List[str] = []
    for cid in ids:
        got = outcomes.get(cid)
        if got is None:
            problems.append("%s:absent" % cid)
        elif got != CONTROLS_OK:
            problems.append("%s:%s" % (cid, got))
        elif sidecars is not None and cid in STATE_CONTROL_IDS:
            outcome, detail = judge_state_control(sidecars.get(cid))
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
        ctl_ids = manifest_control_set(manifest)
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
    else the sidecar's ``stateProof.family``, else ``PRESS_UNKNOWN_FAMILY`` (a crashed
    case's sidecar may carry no proof, so a family-less case is still counted); its status
    the sidecar's ``setup.status``, else the row's ``setupStatus``. None when no planned
    case is a state case (a stowed run: the gate does not apply)."""
    ctl = set(ALL_CONTROL_IDS)
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
            family = PRESS_UNKNOWN_FAMILY
        setup = (side or {}).get("setup")
        status = setup.get("status") if isinstance(setup, dict) else None
        detail = setup.get("detail") if isinstance(setup, dict) else None
        if status is None:
            status = entry.get("setupStatus")
        per_family.setdefault(family, []).append((cid, status, detail))
    if not per_family:
        return None
    order = {f: i for i, f in enumerate(FAMILIES)}
    rows: List[Tuple[str, int, int]] = []
    problems: List[str] = []
    for family in sorted(per_family, key=lambda f: (order.get(f, len(order)), f)):
        cases = per_family[family]
        failed = [c for c in cases if c[1] in PRESS_GATE_STATUSES]
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


def state_view_block_problems(sidecar: Dict) -> List[str]:
    """The view-block shape a state case must carry (design D.2, D.6): every view has a
    ``start`` block with every ``START_KEYS`` key and a ``stowedRef`` block with every
    ``STOWED_REF_KEYS`` key. A stowed case, or a case with no views, has no problem. Run
    over every state case, not only the recompute sample."""
    if not is_state_case(sidecar.get("state")):
        return []
    problems: List[str] = []
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
    counts exactly, the blob percentages against the view's box area. A missing block or
    key is a difference."""
    width, height, bpp, pixels = atlas
    want_rows = list(ATLAS_ROWS_STATE)
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
            rec = dict(start)
            rec["view"] = "%s/%s" % (name, ROUTE_START)
            diffs.extend(compare_view(rec, recompute_view(real_rows, start_rows, tile)))
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

    counts = _count(cases)
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
            status = STATUS_FAIL if counts[VERDICT_FAIL] else STATUS_PASS
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
                       completeness, cases, recompute_ids, mismatches, counts, press)


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
                     + ("" if s.press is None else " press=%s" % ("ok" if s.press.ok else "bad")))
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
    check ran) adds ``statePress`` and ``stateFlags``; a stowed sweep's document is
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
    if sweep.press is not None:
        doc["statePress"] = sweep.press.to_dict()
        doc["stateFlags"] = state_flag_counts(sweep.cases)
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
                               "reasons": list(w.reasons[:3])} for w in s.worst()]}
                   for s in row.sweeps],
    }
