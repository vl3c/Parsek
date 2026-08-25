"""Pure render-composition decisions for the M-A7 verifier row (``renderCompose``).

This module is the M-A7 analogue of ``saveparse.py`` (parse + spec-surface +
evaluate) crossed with ``oracle.py`` (ratified vocabulary tables, pinned stock
math, structured findings). The harness shell (``run.py``) reads the produced
``parsek-render-manifest.txt`` off the KSP root and every DECISION about that
text lives here, side-effect-free: NO KSP, NO network, NO filesystem, NO wall
clock, stdlib only, ASCII only.

Design authority: ``docs/dev/design-autotest-render-composition.md`` (module
M-A7), sections "Oracle independence" and "Verifier rule set". The concrete
manifest schema, the Python API surface and the rule semantics implemented here
are pinned by the implementation spec (``.scout/SPEC.md``); the clock-math
formulas are transcribed from the C# sources per ``.scout/plan-surface.md``
section 5.2 and are cited per function.

What lives here
---------------
1. **Vocabulary + ratified tables.** ``RATIFIED_TOLERANCES`` is keyed by the C#
   constant CODE NAME and is the AUTHORITY; the manifest's ``CONSTANTS`` node is
   TRANSPORT. A drift between the two is an ``RC-CONST`` finding rather than a
   silent re-calibration. ``STOCK_BODY_ROTATION_PERIOD_SECONDS`` /
   ``STOCK_BODY_ORBITAL_PERIOD_SECONDS`` carry ONLY bodies whose value already
   appears in a committed in-repo source, one citation per row (the
   ``mlib.STOCK_BODY_GRAVITY`` discipline); the accessors RAISE on an unknown
   key rather than guessing.
2. **The parser.** ``parse_render_manifest`` runs ``saveparse.parse_sfs`` over
   the text and extracts the header / CONSTANTS / PLAN / CHAIN / OBSERVED
   sections. A torn file or a text with no ``RENDER_MANIFEST`` root is a DEFINED
   fault (``parsed=False`` with a named error), never "zero records" - the
   ``parse_parsek_scenario`` rule, for the same reason (a zero-byte file is
   trivially brace-balanced).
3. **The clock math.** Independent Python re-derivations of the arrival hold,
   the joint (D8) hold, the launch borrow/repay advance, the loiter
   compress/decompress maps and the descent-trigger congruence, transcribed
   from ``GhostPlaybackLogic.SpanClock.cs`` / ``ReaimLoiterCompressor.cs`` /
   ``DescentTrigger.cs``. These exist so the verifier can check BOTH legs of the
   oracle-independence rule: plan-vs-recomputation (solver drift) and
   observed-vs-recomputation (render drift).
4. **The RC rule set.** Twelve rules (``RULE_IDS``), each producing
   ``RenderComposeFinding`` records carrying a REQUIRED non-empty
   ``cited_contract`` (the M-A1 ``Finding.CitedContract`` discipline, first
   carried in Python here).
5. **The spec surface.** ``[expectations.renderComposition]`` with the R9
   ``gating = true`` per-block opt-in, validated with the window grammar and the
   three anti-vacuity notches ``saveparse`` uses (semantics COPIED, privates not
   imported).

Defined-unevaluable discipline (binding)
----------------------------------------
A clause the manifest cannot answer is NEITHER a pass NOR a red: it is counted
by reason in ``observed["renderComposition"]["unevaluableReasons"]`` and totalled
in ``["unevaluable"]``. The three populations Phase 2 has:

- ``seam-data-unavailable-tracing-off`` - the tangent/endpoint capture
  predicates are tracing-gated (SPEC decision 2), so a manifest-only lane
  carries no seam numbers. A lane must arm ``mapRenderTracing`` beside
  ``PARSEK_RENDER_MANIFEST`` to make RC-SEAM numeric clauses evaluable.
  RAISED ONLY when the record family is empty AND the header bit is false, and
  the header bit ``mapRenderTracingOn`` is STICKY on the writer side (was-ever-on
  across the accumulated records, cleared only with them; the instantaneous read
  it replaced stamped ``False`` onto a teardown manifest carrying 107 tracing-
  gated seam records and manufactured this unevaluable out of nothing). So the
  complementary reading is now load-bearing too: header true with an empty seam
  family is a MEASURED absence - the instrument was armed and captured nothing -
  and correctly raises no unevaluable at all.
- ``truncated-section-*`` - a ``TRUNCATED`` record means the accumulation core
  dropped records at a cap, so every count derived from that section is a floor.
  Affected sections become unevaluable, NOT unknown (SPEC RC-UNKNOWN).
- absent instants / absent evidence - a re-timed re-aim window with no
  ``REAIM_WINDOW`` clock event, truth positions absent because the probe never
  ran, a hold whose run was shorter than the local warp step so no engage/release
  pair could be resolved, a dwell whose member mapped to no live unit so it
  carries no recorded-clock stamp.

Schema v1.1 (additive; the version stays 1)
-------------------------------------------
`.scout/schema-v1.1-decisions.md` added five OPTIONAL keys, each of which turns a
clause that used to be structurally unevaluable into a measurable one. Every one
is optional and every reader here tolerates absence, which is why the schema
version did not move:

- ``UNIT.launchBodyName`` / ``UNIT.destinationBodyName`` (decision 1) - the
  oracle-independence leg-1 check becomes an EXACT stock-table row lookup instead
  of a nearest-period heuristic.
- ``CLOCK_EVENT`` kinds ``hold-engage`` / ``hold-release`` (decision 2) - RC-HOLD
  leg 2. Derived from OBSERVATION (a stationary render clock across advancing live
  time), never from re-running the hold formula, so the comparison is not circular.
- ``CLOCK_EVENT.detailD`` (decision 3) - the resolved descent head on a
  ``descent-phase`` event, which is what makes RC-DESCENT's head clauses evaluable.
- ``DWELL.openLoopUT`` / ``DWELL.closeLoopUT`` (decision 4) - the dwell's own
  interval on the RECORDED clock, which is the only clock a loiter cut lives on, so
  RC-CUT's containment clause becomes evaluable.
- ``DWELL.ownerIndex`` (decision 5) - the writer's own unit attribution, used in
  preference to the recId/committedIndex inference.

Two decisions in that pass are NOTES rather than code, recorded here because the
reasoning is what a future reader will want, not the outcome:

- **Decision 7 - the InteriorGap bound stays the unit CADENCE for Phase 2.** The
  design's eventual bound is tighter (the seam gap plus a reseed allowance), but
  that needs seam-gap and reseed instants the manifest does not carry yet, so
  Phase 2 measures the defect it CAN state without inventing evidence: a hold that
  outlives a whole loop cycle is no longer a seam gap under any reading. The
  tightening is Phase 3 and will narrow this bound, never widen it - so nothing
  passing today can start failing because of a mistake made here.
- **Decision 8 - an empty ``warpBuckets`` / ``requireSeamKinds`` list is
  REJECTED at spec validation**, not silently accepted as "assert nothing". An
  empty list reads as an assertion but cannot red, which is the exact anti-vacuity
  shape ``_validate_armed_unreddable`` exists to refuse; an author who means "do
  not assert this" omits the key.

The spec block
--------------
``[expectations.renderComposition]``, one block, per-block ``gating``. Keys:

- ``gating`` (bool) - arm this block. Arming gates on (a) the window/list
  assertions below AND (b) every FAIL-level rule finding: the findings are the
  substance of the module and a block that armed but let a FAIL finding through
  would be fail-open.
- ``dwells`` (window) - anti-vacuity floor on the number of CLOSED dwell records.
  A composition PASS off zero dwells claims nothing.
- ``cycles`` (window) - anti-vacuity floor on the number of distinct observed
  loop cycles (derived from ``cycle-rollover`` clock events). At least two are
  needed before RC-CYCLE can compare anything.
- ``unevaluable`` (window) - bound on the DEFINED-unevaluable total. This is the
  key that stops a run passing because nothing was measurable; an armed lane
  normally declares ``{ max = 0 }`` or a small ceiling with a written rationale.
- ``warpBuckets`` (list of bucket names from ``WARP_BUCKETS``) - each named
  bucket must carry a non-zero frame count. Consumed by RC-WARP, which is INFO
  when the block is unarmed and FAIL when it is armed (design RC-WARP).
- ``requireSeamKinds`` (list of tokens from ``SEAM_KINDS_REQUIRABLE``) - each
  named seam kind must appear at least once in the chain records. Consumed by
  RC-SEAM. The validation vocabulary is NARROWER than the parse vocabulary
  ``SEAM_KINDS`` on purpose (see that tuple's comment): a spec naming a kind the
  writer cannot emit is a pre-launch error, not a lane that flies and reds.

DEVIATION from the spec sketch, stated: the sketch named ``minDwells`` /
``minCycles`` / ``maxUnknownFindings``. Those are spelled here as the neutral
``dwells`` / ``cycles`` / ``unevaluable`` WINDOWS so the grammar is byte-for-byte
``saveparse``'s (bare int = exact pin, ``{min=,max=}`` = window), which is what
``_validate_armed_unreddable`` is written against; a ``minDwells`` key holding a
bare int would read as an exact pin while its NAME promised a floor.
``maxUnknownFindings`` is subsumed: RC-UNKNOWN findings are FAIL level, so an
armed block already gates on them, and the honest residual bound is on the
UNEVALUABLE count, not the unknown one.

ASCII only; stdlib only.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Sequence, Tuple

import saveparse

SCHEMA_VERSION = 1

# ---------------------------------------------------------------------------
# Finding levels + row statuses.
# ---------------------------------------------------------------------------

# The M-A1 three-level model (Source/Parsek/Analyzer/AnalyzerModel.cs:24-30
# `VerdictLevel { Info=0, Warn=1, Fail=2 }`), mirrored - not imported - so the
# Python findings sort into the same buckets the offline analyzer's do.
LEVEL_FAIL = "FAIL"
LEVEL_WARN = "WARN"
LEVEL_INFO = "INFO"
LEVELS: Tuple[str, ...] = (LEVEL_FAIL, LEVEL_WARN, LEVEL_INFO)

# Row statuses, saveparse spelling (saveparse.py:813-816).
STATUS_REPORT = "REPORT"
STATUS_PASS = "PASS"
STATUS_FAIL = "FAIL"
STATUS_SKIPPED = "SKIPPED"

# ---------------------------------------------------------------------------
# Rule registry. Order here is the order findings are emitted in, so a run JSON
# diff between two runs of the same lane is stable.
# ---------------------------------------------------------------------------

RULE_CONST = "RC-CONST"
RULE_COVER = "RC-COVER"
RULE_SEAM = "RC-SEAM"
RULE_HOLD = "RC-HOLD"
RULE_CUT = "RC-CUT"
RULE_DESCENT = "RC-DESCENT"
RULE_CYCLE = "RC-CYCLE"
RULE_ROUTE = "RC-ROUTE"
RULE_OWN = "RC-OWN"
RULE_WARP = "RC-WARP"
RULE_QUAL = "RC-QUAL"
RULE_UNKNOWN = "RC-UNKNOWN"

RULE_IDS: Tuple[str, ...] = (
    RULE_CONST, RULE_COVER, RULE_SEAM, RULE_HOLD, RULE_CUT, RULE_DESCENT,
    RULE_CYCLE, RULE_ROUTE, RULE_OWN, RULE_WARP, RULE_QUAL, RULE_UNKNOWN,
)

# The DEFAULT cited contract per rule (design "Verifier rule set"). A finding may
# cite something narrower; it may never cite nothing.
RULE_CITED_CONTRACTS: Dict[str, str] = {
    RULE_CONST: ("RATIFIED_TOLERANCES + STOCK_BODY_* tables (this module) vs the "
                 "manifest CONSTANTS transport; design 'Oracle independence' leg 1"),
    RULE_COVER: ("GhostPlaybackLogic.DecideUnitMemberRender; "
                 "design-map-render-architecture section 11.3"),
    RULE_SEAM: ("PhaseSeamClassifier, CrossMemberSeamStitcher, SeamEndpointOracle; "
                "design-map-render-architecture 6.1/9.1"),
    RULE_HOLD: ("design-mission-periodicity arrival-flex rules; "
                "design-reaim-launch-hold-seam sections 2-4; GhostRenderDirector hold contract"),
    RULE_CUT: ("ReaimLoiterCompressor.ComputeCuts; design-mission-periodicity "
               "arrival flex-point (partial trim) section"),
    RULE_DESCENT: ("DescentTrigger (trigger congruence + the S4 recorded-site "
                   "invariant ratified 2026-07-07)"),
    RULE_CYCLE: ("design-mission-periodicity zero-drift rules; "
                 "GhostPlaybackLogic.ComputeBoundaryOverlapAdvanceSeconds"),
    RULE_ROUTE: ("RouteTrajectoryLineRenderer.LegWithinDockClip / "
                 "FilterLegsToEndpointBodies; design-logistics section 0"),
    RULE_OWN: ("GhostRenderIntent single-treatment rule; "
               "GhostMapPresence.ResolveMarkerDrawDecision"),
    RULE_WARP: ("design-autotest-render-composition RC-WARP anti-vacuity clause"),
    RULE_QUAL: ("design-autotest-render-composition RC-QUAL trend surface"),
    RULE_UNKNOWN: ("design-autotest-render-composition RC-UNKNOWN catch-all: an "
                   "observed token or dark window no rule claimed"),
}

# ---------------------------------------------------------------------------
# Manifest vocabulary. Every token set below is pinned against a named C# source
# so an unrecognised token in a produced manifest is an RC-UNKNOWN FAIL rather
# than a silently-ignored string.
# ---------------------------------------------------------------------------

MANIFEST_ROOT = "RENDER_MANIFEST"

EXPORT_REASONS: Tuple[str, ...] = ("verb", "scene-exit", "process-teardown")

HOSTS: Tuple[str, ...] = ("Flight", "KSC", "TrackingStation")

# MapRender/RenderSegment.cs:14-21 `enum Treatment { None, StockConic, TracedPath }`.
TREATMENTS: Tuple[str, ...] = ("None", "StockConic", "TracedPath")

# MapRender/RenderSegment.cs:53-62 `enum Coverage { InSegment, InInteriorGap, OutsideWindow }`.
COVERAGES: Tuple[str, ...] = ("InSegment", "InInteriorGap", "OutsideWindow")

# The two coverages that RATIFY an invisible dwell for RC-COVER. Both are a
# POSITIVE statement about why the span is dark: `OutsideWindow` says the
# recording's render window does not reach this instant at all, and
# `InInteriorGap` is the Director's held / no-covering-segment interior gap. The
# third value, `InSegment`, is deliberately NOT here: a covering segment exists
# and the leg still did not draw, which is exactly the defect RC-COVER hunts.
RATIFIED_HIDDEN_COVERAGES: Tuple[str, ...] = ("InInteriorGap", "OutsideWindow")

# LINE_BRANCH.coverage is a DIFFERENT enum: MapRenderTrace's 3-state
# `RenderWindowCoverage` (Unknown / Inside / Outside), PascalCase .ToString().
# Confirmed against the shipped writer's sample fixture, which carries both
# `coverage = Unknown` and `coverage = Inside` on LINE_BRANCH records while its
# DWELL records carry `InSegment`. Two same-named keys, two vocabularies.
RENDER_WINDOW_COVERAGES: Tuple[str, ...] = ("Unknown", "Inside", "Outside")

# MapRender/RenderSegment.cs:24-36 `enum SegmentKind`. An ASSEMBLER-FALLBACK
# chain's PHASE.kind carries these PascalCase names (with `provenance = unknown`)
# where a spine chain carries the lowercase PhaseKind tokens - the shipped writer
# emits whichever source produced the chain, so a chain phase kind is validated
# against the UNION.
SEGMENT_KIND_NAMES: Tuple[str, ...] = (
    "Other", "Ascent", "Loiter", "Eject", "Transfer", "Approach",
    "ArrivalOrbit", "ArrivalLoiter", "Landing", "Surface",
)

# SPEC chain section: per-boundary seam kinds come from the LEGACY assembler
# chain (RenderSegment.LeadingSeam, MapRender/RenderSegment.cs:39-50
# `enum SeamKind { None, Rigid, FlexibleSoi }`), serialized by
# `RenderCompositionRecorder.SeamKindToken` (RenderCompositionRecorder.cs:930-937)
# as exactly three tokens - `rigid`, `flexible-soi`, and `none` for everything
# else. This tuple is the PARSE vocabulary: a token outside it is an RC-UNKNOWN
# FAIL rather than a silently-ignored string.
#
# It used to carry `switch-continuation` as a fourth row, which contradicted the
# three-value enum the comment above it cited. That token belongs to a DIFFERENT
# type - `PhaseSeamClassifier.KindToken` over `PhaseSeamKind`
# (MapRender/PhaseSeamClassifier.cs:18-26) - and the typed spine never reaches the
# manifest's SEAM records (PhaseFactory builds typed phases with NULL seams, which
# is why the recorder reads the assembler chain). Nothing the writer can emit
# spells it, so listing it here only widened the vocabulary against a token that
# cannot appear.
SEAM_KINDS: Tuple[str, ...] = ("rigid", "flexible-soi", "none")

# The subset a SPEC may REQUIRE via `requireSeamKinds`. `none` is a real emitted
# token and stays parse-legal above, but it is the writer's "no distinguished
# seam" default: requiring it asserts that some boundary was uninteresting, which
# is not a coverage claim. A spec naming a kind the writer cannot emit must be a
# PRE-LAUNCH validation error, never a lane that flies and then reds on an
# unsatisfiable clause.
SEAM_KINDS_REQUIRABLE: Tuple[str, ...] = ("rigid", "flexible-soi")

# MapRender/TrajectoryPhase.cs:173-186 `enum PhaseKind` + the grep-stable tokens
# PhaseKindTokens.ToToken emits (:189-206). BOTH spellings are accepted on read
# (see _normalize_phase_kind): the SPEC says "enums as tokens/strings" without
# saying which of the two the writer picks, so the parser normalises rather than
# guessing - and the C# reconciliation pins which one is real.
# `None` is NOT a PhaseKind member: the shipped recorder writes `phaseKind = none`
# for a frame with no covering segment (and `hold` for a Director-held
# byte-identical intent across an InInteriorGap). It is a first-class dwell
# spelling, so it is a first-class row here.
PHASE_KIND_NAMES: Tuple[str, ...] = (
    "Unknown", "None", "Ascent", "DepartureLoiter", "SoiDeparture",
    "HeliocentricTransfer", "SoiArrival", "ArrivalLoiter", "Descent",
    "Surface", "Hold",
)
PHASE_KIND_TOKENS: Dict[str, str] = {
    "unknown": "Unknown", "none": "None", "ascent": "Ascent",
    "departure-loiter": "DepartureLoiter",
    "soi-departure": "SoiDeparture", "heliocentric-transfer": "HeliocentricTransfer",
    "soi-arrival": "SoiArrival", "arrival-loiter": "ArrivalLoiter",
    "descent": "Descent", "surface": "Surface", "hold": "Hold",
}

# MapRender/SegmentProvenance.cs:18-34 `enum SegmentProvenance` + tokens (:44+).
SEGMENT_PROVENANCE_NAMES: Tuple[str, ...] = (
    "Unknown", "Recorded", "FinalizedPredicted", "Synthesized", "FaithfulFallback",
)
SEGMENT_PROVENANCE_TOKENS: Dict[str, str] = {
    "unknown": "Unknown", "recorded": "Recorded",
    "finalized-predicted": "FinalizedPredicted", "synthesized": "Synthesized",
    "faithful-fallback": "FaithfulFallback",
}

# SPEC CHAIN_BUILD.provenance - a DIFFERENT vocabulary from SegmentProvenance:
# which builder produced the chain (typed spine vs the legacy assembler).
CHAIN_PROVENANCES: Tuple[str, ...] = ("spine", "assembler-fallback")

# SPEC: seamSource is pinned to "assembler" for schema v1 (supervisor decision 1:
# production PhaseChain seams are always null, PhaseFactory.cs:236-240).
SEAM_SOURCES: Tuple[str, ...] = ("assembler",)

# Display/RouteTrajectoryLineRenderer.cs:110-121 `enum RouteLineScope`.
ROUTE_SCOPES: Tuple[str, ...] = ("SameBody", "InterBody", "MalformedMixedBodies")

# SPEC OBSERVED.CLOCK_EVENT.kind, plus the two schema v1.1 OBSERVATION-derived hold
# kinds (`.scout/schema-v1.1-decisions.md` decision 2,
# RenderCompositionManifest.ClockHoldEngage / ClockHoldRelease). The pair is a
# MEASUREMENT of a stationary render clock, never a re-run of the hold formula -
# which is what lets RC-HOLD leg 2 compare it against the recomputation without
# the comparison being circular.
CLOCK_EVENT_KINDS: Tuple[str, ...] = (
    "cycle-rollover", "inter-cycle-tail", "boundary-overlap-secondary",
    "descent-phase", "route-dock-crossing", "reaim-window",
    "hold-engage", "hold-release",
)

CLOCK_HOLD_ENGAGE = "hold-engage"
CLOCK_HOLD_RELEASE = "hold-release"

# DescentTrigger phase tokens carried on a descent-phase clock event
# (.scout/plan-surface.md section 5.2(d) descent_phase: Inert/Loiter/Descent/Done).
DESCENT_PHASES: Tuple[str, ...] = ("Inert", "Loiter", "Descent", "Done")

OWNERSHIP_EVENTS: Tuple[str, ...] = ("appear", "disappear")

# SPEC OBSERVED.DWELL warp histogram: frame counts per bucket.
#   warp1x   rate <= 1 (and physics dt)      warpPhys  physics warp > 1
#   warp100  rails <= 100                    warp1000  rails <= 1000
#   warpHigh rails > 1000
WARP_BUCKETS: Tuple[str, ...] = ("warp1x", "warpPhys", "warp100", "warp1000", "warpHigh")
WARP_BUCKETS_ABOVE_1X: Tuple[str, ...] = ("warpPhys", "warp100", "warp1000", "warpHigh")

# The OBSERVED child-node names, so a node name the schema does not define is an
# RC-UNKNOWN FAIL instead of a silently-dropped record.
#
# ``ANOMALY_ECHO`` appears TWICE in the schema under one name, and the two are
# different populations told apart by NESTING, never by name: nested under a
# DWELL it is the per-dwell (reason, count) aggregate; directly under OBSERVED it
# is the standalone per-raise record the review pass added, carrying a VERBATIM
# ``pidKey`` (not necessarily numeric), an optional ``recId``, the raise reason
# and the live ``ut``. The standalone record exists because the nested one can
# only hold a raise that both parses as a uint pid AND lands while a dwell is
# open for that pid; every other raise used to be dropped, so a verifier could
# not tell "nothing was raised" from "the raise had nowhere to land".
OBSERVED_SECTIONS: Tuple[str, ...] = (
    "DWELL", "OPEN_DWELL", "TRANSITION", "SEAM_TANGENT", "SEAM_ENDPOINT",
    "CLOCK_EVENT", "LINE_BRANCH", "OWNERSHIP_CHANGE", "RATIFIED_SKIP",
    "CLOCK_DEFER", "ROUTE_LINE_BUILD", "ROUTE_LEG_DEFER",
    "ROUTE_CODRAW_VIOLATION", "ANOMALY_ECHO", "TRUNCATED",
)

# ---------------------------------------------------------------------------
# Ratified tables. THE TABLE IS THE AUTHORITY, THE MANIFEST HEADER IS TRANSPORT.
#
# Every row cites the C# declaration it pins (file:line, read 2026-08-25 via the
# M-A7 scout inventory .scout/render-seams.md section 6d and
# .scout/infra-constants.md section 6). A drift between a row here and the
# exported CONSTANTS node is an RC-CONST FAIL: either the product moved a
# ratified tolerance without the catalog moving with it, or the export is wrong.
# Re-pinning a row is a deliberate act with a design-doc edit behind it, never a
# "make the test green" edit.
# ---------------------------------------------------------------------------

RATIFIED_TOLERANCES: Dict[str, float] = {
    # Source/Parsek/MapRender/PhaseSeamClassifier.cs:101
    "PhaseSeamClassifier.DefaultTangentToleranceRadians": 0.1,
    # Source/Parsek/MapRender/CrossMemberSeamStitcher.cs:73-74 - an ALIAS of the
    # row above (`= PhaseSeamClassifier.DefaultTangentToleranceRadians`). Both are
    # exported and pinned EQUAL; the alias silently diverging is its own finding.
    "CrossMemberSeamStitcher.TangentToleranceRadians": 0.1,
    # Source/Parsek/MapRender/SeamEndpointOracle.cs:96 (calibration rationale :55-95)
    "SeamEndpointOracle.DefaultRatioTolerance": 1.005,
    # Source/Parsek/Display/GhostTrajectoryPolylineRenderer.cs:1664
    "GhostTrajectoryPolylineRenderer.BridgeMergeSampleCount": 60.0,
    # :1670 (45 degrees in radians)
    "GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians": 0.7853981633974483,
    # :1699 (5 degrees in radians)
    "GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians": 0.08726646259971647,
    # :1713 (0.5 degrees in radians)
    "GhostTrajectoryPolylineRenderer.BridgeChordMinAngleRadians": 0.008726646259971648,
    # :1677
    "GhostTrajectoryPolylineRenderer.BridgeMaxSeamGapSeconds": 120.0,
    # :1685
    "GhostTrajectoryPolylineRenderer.BridgeSeamSharedBoundaryToleranceSeconds": 1.0,
    # :1600 (float in C#)
    "GhostTrajectoryPolylineRenderer.AnchorMaxResidualKm": 50.0,
    # :1603 (float in C#)
    "GhostTrajectoryPolylineRenderer.AnchorMaxRelResidual": 0.05,
    # Source/Parsek/MapRender/ShadowRenderDriver.cs:139
    "ShadowRenderDriver.SeedFreshnessFrames": 2.0,
    # Source/Parsek/Patches/GhostOrbitLinePatch.cs:622 (float in C#)
    "GhostOrbitLinePatch.PolylineReleaseGraceSeconds": 1.5,
    # Source/Parsek/Display/GhostTrajectoryPolylineRenderer.cs:4899 - private in
    # the shipped code; SPEC decision 11 promotes it to internal for export.
    "GhostTrajectoryPolylineRenderer.TangentSeamConicSampleDtSeconds": 1.0,
    # SPEC decision 10: extracted from the MissionLoopUnitBuilder.cs:1059
    # method-local const onto DescentTrigger as DefaultSeamEpsSeconds.
    "DescentTrigger.DefaultSeamEpsSeconds": 1.0,
    # Source/Parsek/Reaim/ReaimLoiterCompressor.cs:22
    "ReaimLoiterCompressor.DefaultKeepRevs": 1.0,
    # :23
    "ReaimLoiterCompressor.DefaultAStepRelThreshold": 0.05,
    # :24
    "ReaimLoiterCompressor.DefaultContiguityEpsilonSeconds": 1.0,
    # :32
    "ReaimLoiterCompressor.DefaultSameOrbitRelThreshold": 0.001,
    # Source/Parsek/Reaim/DestinationArrivalSolver.cs:49 (consumed
    # ArrivalHoldPlanner.cs:352, then clamped by loop slack - the CONSTANT is
    # pinned here, the clamped per-unit value rides the plan section)
    "DestinationArrivalSolver.MaxJointHoldWholePeriods": 64.0,
}

# The alias pairs that must agree with each other, not merely with the table.
RATIFIED_ALIAS_PAIRS: Tuple[Tuple[str, str], ...] = (
    ("CrossMemberSeamStitcher.TangentToleranceRadians",
     "PhaseSeamClassifier.DefaultTangentToleranceRadians"),
)

# Stock body periods. DELIBERATELY MINIMAL, the mlib.STOCK_BODY_GRAVITY
# discipline (harness/missions/lib/mlib.py:1883-1897): a body appears here only
# when its value already sits in a COMMITTED, reviewed in-repo source, and the
# row carries that citation. These are the SECOND source behind the plan
# section's own primitives (design "Oracle independence" leg 1): a plan whose
# rotation period disagrees with the stock table is a finding BEFORE any clock
# math runs, because otherwise a C# bug feeding the wrong period into the plan
# would be faithfully recomputed in Python and both legs would agree.
#
# ADDING A BODY: cite a committed spec header, a committed test fixture, or the
# stock config it was read from, in the row comment. A typo here is a FALSE
# RC-CONST red on a correct run, which is the expensive direction.
STOCK_BODY_ROTATION_PERIOD_SECONDS: Dict[str, float] = {
    # Measured `Rotation(Kerbin) P=21549.425183089825 off=0` in the ExtractConstraints
    # line quoted by harness/scenarios/V22M-kerbin-splashdown-player-loop.toml:44
    # (restated V22K:176, V22T:59, V6M:182, V7M:268, V23M:160).
    "Kerbin": 21549.425183089825,
    # `Rotation(Mun) P=138984.37657447575 off=22937.185293623374`,
    # harness/scenarios/V23M-mun-landing-player-loop.toml:161. The Mun is tidally
    # locked, so this equals its orbital period exactly (V23M:194 states the fact).
    "Mun": 138984.37657447575,
}

STOCK_BODY_ORBITAL_PERIOD_SECONDS: Dict[str, float] = {
    # `Orbital(Mun) same-parent P=138984.37657447575 off=16390.648888550266`,
    # harness/scenarios/V23M-mun-landing-player-loop.toml:162.
    "Mun": 138984.37657447575,
    # `Orbital(Minmus) same-parent P=1077310.5210188075 off=267706.881...`,
    # harness/scenarios/V7M-minmus-player-loop.toml:269.
    "Minmus": 1077310.5210188075,
    # `Orbital(Ike) same-parent P=65517.862134808071 off=17082.834318690002`,
    # harness/scenarios/V14M-ike-player-loop.toml:26 (restated V14T:22).
    "Ike": 65517.862134808071,
    # `Orbital(Laythe) same-parent P=52980.879059379578`,
    # harness/scenarios/V16M-laythe-player-loop.toml:958.
    "Laythe": 52980.879059379578,
    # `Orbital(Gilly) same-parent P=388587.37684792886 off=114979.43693914078`,
    # harness/scenarios/V15M-gilly-player-loop.toml:767.
    "Gilly": 388587.37684792886,
}


def ratified_tolerance(name: str) -> float:
    """The ratified value for a C# constant CODE NAME. RAISES on an unknown key.

    Raising is the point: a caller reaching for a constant the catalog does not
    pin has either misspelled it or is about to compare against a number nobody
    ratified, and both should stop here rather than silently degrade to a
    default."""
    try:
        return RATIFIED_TOLERANCES[str(name)]
    except KeyError:
        raise ValueError(
            "no ratified value for constant %r; known: %s -- add the row with a "
            "file:line citation before comparing against it"
            % (name, sorted(RATIFIED_TOLERANCES))) from None


def body_rotation_period(body: str) -> float:
    """Sidereal rotation period (seconds) of a stock body. RAISES on unknown."""
    try:
        return STOCK_BODY_ROTATION_PERIOD_SECONDS[str(body)]
    except KeyError:
        raise ValueError(
            "no stock rotation period for body %r; known: %s -- add the body "
            "with an in-repo citation before checking a plan primitive against it"
            % (body, sorted(STOCK_BODY_ROTATION_PERIOD_SECONDS))) from None


def body_orbital_period(body: str) -> float:
    """Orbital period (seconds) of a stock body about its parent. RAISES on unknown."""
    try:
        return STOCK_BODY_ORBITAL_PERIOD_SECONDS[str(body)]
    except KeyError:
        raise ValueError(
            "no stock orbital period for body %r; known: %s -- add the body "
            "with an in-repo citation before checking a plan primitive against it"
            % (body, sorted(STOCK_BODY_ORBITAL_PERIOD_SECONDS))) from None


# ---------------------------------------------------------------------------
# Numeric policy. Named, so a tolerance is never an unexplained literal buried
# in a rule body.
# ---------------------------------------------------------------------------

# Constant-vs-table comparison: the manifest writes doubles with ToString("R"),
# which round-trips exactly, so the only admissible slack is float parse noise.
CONSTANT_REL_TOLERANCE = 1e-12

# Plan-primitive vs stock-table comparison. One part per million, NOT the
# constants' 1e-12: a plan primitive is a period that may have been re-derived
# (or, in a hand-authored fixture, quoted to a sane number of digits -
# `21549.425` vs Kerbin's `21549.425183089825` is 8.5e-9 relative), while a
# genuine solver drift - the wrong body, a stale cache, a sidereal-vs-solar mixup -
# is never a parts-per-million effect. A value OUTSIDE the near-match band is not
# attributed to a known body at all (unlisted or modded) and is unevaluable,
# never a red.
PRIMITIVE_REL_TOLERANCE = 1e-6
PRIMITIVE_NEAR_MATCH_REL_BAND = 0.01

# Loiter-cut whole-multiple check. ReaimLoiterCompressor's own snap-tolerant
# floor uses 1e-6 on the revolution count (:140), so the same slack applies to
# the ratio the cut length divided by the run period must land on.
CUT_WHOLE_MULTIPLE_TOLERANCE = 1e-6

# Descent trigger congruence. `(trigger - recordedDeorbit) mod T_rot` is zero by
# construction (DescentTrigger.cs:695-699), so any measurable residual is a
# defect; the band is float noise over a period of ~1e5 s expressed in degrees.
DESCENT_ROTATION_RESIDUAL_TOLERANCE_DEG = 1e-4

# Recomputed-vs-exported UT comparison (both oracle-independence legs). One
# millisecond of game time; the render clock is a double and the formulas here
# are the same arithmetic in the same order.
CLOCK_RECOMPUTE_TOLERANCE_SECONDS = 1e-3

# Region-B / secondary gating epsilon, transcribed from SpanClock.cs:1554-1593.
ADVANCE_COMPARE_EPSILON = 1e-9

# RC-HOLD leg 2: observed held seconds vs the per-cycle recomputation. The observed
# value is NOT an exact instant pair - the recorder brackets the stationary run by
# the two frames that bound it, so it is accurate to within ONE local frame step in
# either direction. Hence twice the local step, floored at two seconds so a 1x lane
# whose step is sub-second is not held to a tolerance narrower than its own sampling
# noise. A hold shorter than the local step produces NO event pair at all and is
# below-resolution unevaluable, never a mismatch (the RC-COVER resolution model).
HOLD_OBSERVED_TOLERANCE_FLOOR_SECONDS = 2.0
HOLD_OBSERVED_TOLERANCE_STEP_FACTOR = 2.0

# RC-CUT containment: the dwell's recorded-clock stamp is the recorder's LATEST
# per-unit sample, which is this frame's or (when the recorder's Update trails the
# render path) the immediately preceding frame's. So an intersection with a cut only
# counts once it exceeds the dwell's own maxUtStep - one frame of skew is the stamp's
# stated accuracy, not a violation.
CUT_CONTAINMENT_MARGIN_STEP_FACTOR = 1.0

# Integer-valued detail slots (a secondary cycle index, a release's cycle index) ride
# as doubles. Half a unit is the only sane "these disagree" threshold for them.
INDEX_COMPARE_EPSILON = 0.5


# ---------------------------------------------------------------------------
# Findings.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class RenderComposeFinding:
    """One verifier observation: the M-A1 ``Finding`` model in Python.

    ``cited_contract`` is REQUIRED and non-empty, exactly as
    ``Source/Parsek/Analyzer/AnalyzerModel.cs``'s ``Finding.CitedContract`` and
    the ``IInvariantRule.CitedContract`` interface member are: a rule that cannot
    name the contract it enforces is a rule nobody can adjudicate, and the
    construction RAISES rather than emitting an uncitable red.
    """

    rule_id: str
    level: str
    target: str
    message: str
    cited_contract: str

    def __post_init__(self) -> None:
        if not str(self.rule_id or "").strip():
            raise ValueError("RenderComposeFinding.rule_id must be non-empty")
        if self.level not in LEVELS:
            raise ValueError("RenderComposeFinding.level %r must be one of %s"
                             % (self.level, list(LEVELS)))
        if not str(self.message or "").strip():
            raise ValueError("RenderComposeFinding.message must be non-empty")
        if not str(self.cited_contract or "").strip():
            raise ValueError(
                "RenderComposeFinding.cited_contract must be non-empty (rule %r, "
                "target %r): a finding that cites no contract cannot be adjudicated"
                % (self.rule_id, self.target))

    def as_text(self) -> str:
        """The flat one-line form the harness row's ``mismatches`` list carries."""
        return "%s [%s] %s: %s" % (self.rule_id, self.level, self.target, self.message)


def _finding(rule_id: str, level: str, target: str, message: str,
             cited: Optional[str] = None) -> RenderComposeFinding:
    return RenderComposeFinding(
        rule_id=rule_id, level=level, target=str(target),
        message=message, cited_contract=cited or RULE_CITED_CONTRACTS[rule_id])


# ---------------------------------------------------------------------------
# Coercions. Deliberately NOT saveparse's privates (a sibling module's private
# helper is not an API); the semantics are the same, plus the "R"-format
# infinities C# writes as "Infinity" / "-Infinity", which float() accepts.
# ---------------------------------------------------------------------------

NAN = float("nan")


def _to_float(raw: Optional[str], default: float = NAN) -> float:
    if raw is None:
        return default
    try:
        return float(str(raw).strip())
    except (TypeError, ValueError):
        return default


def _to_int(raw: Optional[str], default: Optional[int] = None) -> Optional[int]:
    if raw is None:
        return default
    try:
        return int(str(raw).strip())
    except (TypeError, ValueError):
        return default


def _to_bool(raw: Optional[str], default: Optional[bool] = None) -> Optional[bool]:
    """bool.TryParse semantics; ``default`` when absent or unparseable so an
    ABSENT flag stays distinguishable from a False one (None)."""
    if raw is None:
        return default
    low = str(raw).strip().lower()
    if low == "true":
        return True
    if low == "false":
        return False
    return default


def _num(node: saveparse.SfsNode, key: str, default: float = NAN) -> float:
    return _to_float(node.value(key), default)


def _int(node: saveparse.SfsNode, key: str, default: Optional[int] = None) -> Optional[int]:
    return _to_int(node.value(key), default)


def _flag(node: saveparse.SfsNode, key: str, default: Optional[bool] = None) -> Optional[bool]:
    return _to_bool(node.value(key), default)


def _text(node: saveparse.SfsNode, key: str, default: str = "") -> str:
    raw = node.value(key)
    return default if raw is None else str(raw).strip()


def _split_list(raw: str, sep: str) -> Tuple[str, ...]:
    if not raw:
        return ()
    return tuple(p.strip() for p in raw.split(sep) if p.strip())


def _finite(value: float) -> bool:
    return isinstance(value, float) and math.isfinite(value)


def _normalize_phase_kind(raw: str) -> str:
    """Accept the enum NAME (``Descent``) or the grep token (``descent``).

    Interpretation, flagged for the C# reconciliation: the SPEC says "enums as
    tokens/strings, never ints" without pinning which spelling
    ``RenderCompositionManifest`` writes for ``PhaseKind``, and the C# type has
    both (``PhaseKind`` names and ``PhaseKindTokens.ToToken``). Normalising on
    read means either writer choice parses; an unrecognised spelling stays
    verbatim and reaches RC-UNKNOWN."""
    txt = str(raw or "").strip()
    if not txt:
        return ""
    if txt in PHASE_KIND_NAMES:
        return txt
    return PHASE_KIND_TOKENS.get(txt.lower(), txt)


def _normalize_provenance(raw: str) -> str:
    txt = str(raw or "").strip()
    if not txt:
        return ""
    if txt in SEGMENT_PROVENANCE_NAMES:
        return txt
    return SEGMENT_PROVENANCE_TOKENS.get(txt.lower(), txt)


# ---------------------------------------------------------------------------
# Manifest data types. Field names are the snake_case of the SPEC's camelCase
# keys; the mapping is 1:1 and never renames.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class LoiterCut:
    """One compressed loiter run (SPEC PLAN.UNIT.LOITER_CUT; produced by
    ``ReaimLoiterCompressor.ComputeCuts``, Reaim/ReaimLoiterCompressor.cs:169)."""
    start_ut: float = NAN
    length_seconds: float = NAN


@dataclass(frozen=True)
class PlanMember:
    index: int = -1
    rec_id: str = ""
    start_ut: float = NAN
    end_ut: float = NAN


@dataclass(frozen=True)
class ReaimSchedule:
    first_departure_ut: float = NAN
    synodic_period_seconds: float = NAN
    tof_seconds: float = NAN
    phase_anchor_ut: float = NAN
    cadence_seconds: float = NAN
    prograde: Optional[bool] = None


@dataclass(frozen=True)
class RouteSpec:
    route_id: str = ""
    backing_mission_tree_id: str = ""
    recorded_dock_ut: float = NAN
    recorded_origin_undock_ut: float = NAN
    dispatch_window_period: float = NAN
    scope: str = ""
    excluded_interval_keys: Tuple[str, ...] = ()


@dataclass(frozen=True)
class PlanUnit:
    """One loop unit as the plan section exported it (SPEC PLAN.UNIT; the field
    set mirrors ``GhostPlaybackLogic.SpanClock.cs``'s ``LoopUnit`` per
    .scout/plan-surface.md section 1.2)."""
    host: str = ""
    plan_seq: Optional[int] = None
    signature_hash: str = ""
    owner_index: Optional[int] = None
    span_start_ut: float = NAN
    span_end_ut: float = NAN
    cadence_seconds: float = NAN
    overlap_cadence_seconds: float = NAN
    phase_anchor_ut: float = NAN
    is_reaim: Optional[bool] = None
    has_relaunch_schedule: Optional[bool] = None
    members: Tuple[PlanMember, ...] = ()
    loiter_cuts: Tuple[LoiterCut, ...] = ()
    arrival_hold_seconds: float = NAN
    arrival_hold_at_ut: float = NAN
    arrival_align_period_seconds: float = NAN
    arrival_joint_secondary_period_seconds: float = NAN
    arrival_joint_secondary_tolerance_seconds: float = NAN
    arrival_joint_max_whole_hold_periods: Optional[int] = None
    arrival_amber_reason: str = ""
    launch_body_rotation_period_seconds: float = NAN
    launch_hold_engaged: Optional[bool] = None
    recorded_soi_exit_ut: float = NAN
    descent_member_indices: Tuple[int, ...] = ()
    recorded_deorbit_ut: float = NAN
    descent_end_ut: float = NAN
    destination_body_rotation_period_seconds: float = NAN
    loiter_period_seconds: float = NAN
    capture_shift_seconds: float = NAN
    parking_conic_end_ut: float = NAN
    transfer_member_index: Optional[int] = None
    first_deorbit_leg_start_ut: float = NAN
    transfer_member_recording_id: str = ""
    reaim_schedule: Optional[ReaimSchedule] = None
    route: Optional[RouteSpec] = None
    # OPTIONAL forward-compatible keys, absent from schema v1 (see the module
    # docstring's open questions): the body NAMES behind the two rotation
    # periods. Without them the stock-table cross-check can only ask "does this
    # period correspond to SOME known stock body", never "to the right one".
    launch_body_name: str = ""
    destination_body_name: str = ""

    @property
    def span_seconds(self) -> float:
        return self.span_end_ut - self.span_start_ut


@dataclass(frozen=True)
class ChainPhase:
    kind: str = ""
    provenance: str = ""
    body: str = ""
    start_ut: float = NAN
    end_ut: float = NAN


@dataclass(frozen=True)
class ChainSeam:
    boundary_index: Optional[int] = None
    kind: str = ""


@dataclass(frozen=True)
class ChainBuild:
    pid: Optional[int] = None
    rec_id: str = ""
    committed_index: Optional[int] = None
    ut: float = NAN
    signature: str = ""
    window_index: Optional[int] = None
    provenance: str = ""
    has_reaimed_segments: Optional[bool] = None
    seam_source: str = ""
    phases: Tuple[ChainPhase, ...] = ()
    seams: Tuple[ChainSeam, ...] = ()


@dataclass(frozen=True)
class TruthPosition:
    body: str = ""
    x: float = NAN
    y: float = NAN
    z: float = NAN


@dataclass(frozen=True)
class Dwell:
    """One contiguous (pid, chain segment) render intent (design Terminology)."""
    pid: Optional[int] = None
    rec_id: str = ""
    committed_index: Optional[int] = None
    chain_signature: str = ""
    segment_index: Optional[int] = None
    phase_kind: str = ""
    treatment: str = ""
    visible: Optional[bool] = None
    coverage: str = ""
    frame_body: str = ""
    open_ut: float = NAN
    close_ut: float = NAN
    # Schema v1.1 (decisions 4 + 5): the OWNING unit and the dwell's own interval on
    # the RECORDED clock. Both are OMITTED when the dwell's committed index mapped to
    # no live loop unit, so absence means "unattributable", never "owner 0 at time 0".
    # openUT/closeUT above are LIVE; a loiter cut is an interval on the recorded clock
    # and can only be tested against these.
    owner_index: Optional[int] = None
    open_loop_ut: float = NAN
    close_loop_ut: float = NAN
    frames: Optional[int] = None
    warp: Dict[str, int] = field(default_factory=dict)
    min_head_ut: float = NAN
    max_head_ut: float = NAN
    max_ut_step: float = NAN
    open_position: Optional[TruthPosition] = None
    close_position: Optional[TruthPosition] = None
    marker_decision: Optional[bool] = None
    marker_traced_path: Optional[bool] = None
    marker_polyline: Optional[bool] = None
    marker_icon_suppressed: Optional[bool] = None
    anomaly_echoes: Tuple[Tuple[str, int], ...] = ()
    truncated: bool = False
    is_open: bool = False

    @property
    def duration(self) -> float:
        return self.close_ut - self.open_ut

    @property
    def frames_above_1x(self) -> int:
        return sum(int(self.warp.get(b, 0) or 0) for b in WARP_BUCKETS_ABOVE_1X)


@dataclass(frozen=True)
class Transition:
    pid: Optional[int] = None
    ut: float = NAN
    from_phase_kind: str = ""
    to_phase_kind: str = ""
    from_treatment: str = ""
    to_treatment: str = ""
    from_body: str = ""
    to_body: str = ""
    from_segment_index: Optional[int] = None
    to_segment_index: Optional[int] = None
    chain_signature: str = ""


@dataclass(frozen=True)
class SeamTangent:
    pid: Optional[int] = None
    rec_id: str = ""
    leg_index: Optional[int] = None
    ut: float = NAN
    continuous: Optional[bool] = None
    angle_rad: float = NAN
    tolerance_radians: float = NAN


@dataclass(frozen=True)
class SeamEndpoint:
    pid: Optional[int] = None
    rec_id: str = ""
    ut: float = NAN
    sampled: Optional[bool] = None
    skip_reason: str = ""
    ratio: float = NAN
    endpoint_distance_meters: float = NAN
    soi_radius_meters: float = NAN
    ratio_tolerance: float = NAN
    outside_soi: Optional[bool] = None
    from_body: str = ""
    to_body: str = ""
    recorded_seam_ut: float = NAN
    seam_ut: float = NAN
    clock_convention: str = ""
    seed_kind: str = ""


@dataclass(frozen=True)
class ClockEvent:
    kind: str = ""
    owner_index: Optional[int] = None
    cycle_index: Optional[int] = None
    ut: float = NAN
    detail_a: float = NAN
    detail_b: float = NAN
    detail_c: float = NAN
    detail_s: str = ""
    # Schema v1.1 (decision 3): the OPTIONAL fourth numeric slot, written only by the
    # kinds that measure one. `descent-phase` carries the RESOLVED descent head UT
    # there. NaN = the key was absent = not measured, never "measured zero".
    detail_d: float = NAN


@dataclass(frozen=True)
class LineBranch:
    pid: Optional[int] = None
    rec_id: str = ""
    ut: float = NAN
    reason: str = ""
    line_active: Optional[bool] = None
    draw_icons: Optional[int] = None
    icon_suppressed: Optional[bool] = None
    coverage: str = ""


@dataclass(frozen=True)
class OwnershipChange:
    rec_id: str = ""
    ut: float = NAN
    event: str = ""


@dataclass(frozen=True)
class RatifiedSkip:
    pid: Optional[int] = None
    reason: str = ""
    first_ut: float = NAN
    last_ut: float = NAN
    count: Optional[int] = None


@dataclass(frozen=True)
class ClockDefer:
    first_ut: float = NAN
    last_ut: float = NAN
    count: Optional[int] = None


@dataclass(frozen=True)
class RouteLineBuild:
    route_id: str = ""
    signature: str = ""
    dock_clip_ut: float = NAN
    dispatch_window_period: float = NAN
    scope: str = ""
    resolvable_members: Optional[int] = None
    groups: Optional[int] = None
    total_legs: Optional[int] = None
    transfer_legs_dropped: Optional[int] = None
    ut: float = NAN


@dataclass(frozen=True)
class RouteLegDefer:
    route_id: str = ""
    rec_id: str = ""
    count: Optional[int] = None


@dataclass(frozen=True)
class RouteCoDrawViolation:
    route_id: str = ""
    rec_id: str = ""
    ut: float = NAN
    frame: Optional[int] = None


@dataclass(frozen=True)
class AnomalyEcho:
    """One STANDALONE ``OBSERVED.ANOMALY_ECHO`` record: a single tracer raise.

    ``pid_key`` is the tracer's own pid key VERBATIM and is NOT necessarily
    numeric (``route-0001`` is a real shipped value), which is why it stays a
    string here rather than being coerced to an int and lost. Distinct from the
    DWELL-nested ``ANOMALY_ECHO`` aggregate (``Dwell.anomaly_echoes``): same node
    NAME, different population, told apart by parent."""
    pid_key: str = ""
    rec_id: str = ""
    reason: str = ""
    ut: float = NAN


@dataclass(frozen=True)
class TruncatedRecord:
    section: str = ""
    pid: Optional[int] = None
    kind: str = ""
    dropped_count: Optional[int] = None


@dataclass(frozen=True)
class ManifestSnapshot:
    """The parsed manifest. ``parsed=False`` => ``error`` names the fault and
    every count below is UNUSABLE (never "zero records")."""

    parsed: bool = False
    error: str = ""
    schema_version: Optional[int] = None
    export_ut: float = NAN
    export_reason: str = ""
    scene: str = ""
    save_name: str = ""
    env_armed: Optional[bool] = None
    force_armed: Optional[bool] = None
    map_render_tracing_on: Optional[bool] = None
    constants: Dict[str, float] = field(default_factory=dict)
    constants_unparsed: Tuple[str, ...] = ()
    units: Tuple[PlanUnit, ...] = ()
    chain_builds: Tuple[ChainBuild, ...] = ()
    dwells: Tuple[Dwell, ...] = ()
    open_dwells: Tuple[Dwell, ...] = ()
    transitions: Tuple[Transition, ...] = ()
    seam_tangents: Tuple[SeamTangent, ...] = ()
    seam_endpoints: Tuple[SeamEndpoint, ...] = ()
    clock_events: Tuple[ClockEvent, ...] = ()
    line_branches: Tuple[LineBranch, ...] = ()
    ownership_changes: Tuple[OwnershipChange, ...] = ()
    ratified_skips: Tuple[RatifiedSkip, ...] = ()
    clock_defers: Tuple[ClockDefer, ...] = ()
    route_line_builds: Tuple[RouteLineBuild, ...] = ()
    route_leg_defers: Tuple[RouteLegDefer, ...] = ()
    route_codraw_violations: Tuple[RouteCoDrawViolation, ...] = ()
    anomaly_echoes: Tuple[AnomalyEcho, ...] = ()
    truncated: Tuple[TruncatedRecord, ...] = ()
    unknown_observed_sections: Tuple[str, ...] = ()


# ---------------------------------------------------------------------------
# The parser.
# ---------------------------------------------------------------------------


def _parse_position(node: saveparse.SfsNode, prefix: str) -> Optional[TruthPosition]:
    """Truth positions ride the DWELL as ``openBody/openX/openY/openZ`` (H18) and
    are OMITTED when the probe never sampled. Absent => None, which every
    position clause treats as defined-unevaluable rather than as the origin."""
    body = node.value(prefix + "Body")
    xs = node.value(prefix + "X")
    if body is None and xs is None:
        return None
    return TruthPosition(body=str(body or "").strip(),
                         x=_num(node, prefix + "X"),
                         y=_num(node, prefix + "Y"),
                         z=_num(node, prefix + "Z"))


def _parse_dwell(node: saveparse.SfsNode, is_open: bool) -> Dwell:
    warp = {}
    for bucket in WARP_BUCKETS:
        warp[bucket] = int(_int(node, bucket, 0) or 0)
    echoes = tuple(
        (_text(e, "reason"), int(_int(e, "count", 0) or 0))
        for e in node.nodes_named("ANOMALY_ECHO"))
    return Dwell(
        pid=_int(node, "pid"),
        rec_id=_text(node, "recId"),
        committed_index=_int(node, "committedIndex"),
        chain_signature=_text(node, "chainSignature"),
        segment_index=_int(node, "segmentIndex"),
        phase_kind=_normalize_phase_kind(_text(node, "phaseKind")),
        treatment=_text(node, "treatment"),
        visible=_flag(node, "visible"),
        coverage=_text(node, "coverage"),
        frame_body=_text(node, "frameBody"),
        open_ut=_num(node, "openUT"),
        close_ut=_num(node, "closeUT"),
        owner_index=_int(node, "ownerIndex"),
        open_loop_ut=_num(node, "openLoopUT"),
        close_loop_ut=_num(node, "closeLoopUT"),
        frames=_int(node, "frames"),
        warp=warp,
        min_head_ut=_num(node, "minHeadUT"),
        max_head_ut=_num(node, "maxHeadUT"),
        max_ut_step=_num(node, "maxUtStep"),
        open_position=_parse_position(node, "open"),
        close_position=_parse_position(node, "close"),
        marker_decision=_flag(node, "markerDecision"),
        marker_traced_path=_flag(node, "markerTracedPath"),
        marker_polyline=_flag(node, "markerPolyline"),
        marker_icon_suppressed=_flag(node, "markerIconSuppressed"),
        anomaly_echoes=echoes,
        truncated=bool(_flag(node, "truncated", False)),
        is_open=is_open)


def _parse_unit(node: saveparse.SfsNode) -> PlanUnit:
    schedule_node = node.first("REAIM_SCHEDULE")
    schedule = None
    if schedule_node is not None:
        schedule = ReaimSchedule(
            first_departure_ut=_num(schedule_node, "firstDepartureUT"),
            synodic_period_seconds=_num(schedule_node, "synodicPeriodSeconds"),
            tof_seconds=_num(schedule_node, "tofSeconds"),
            phase_anchor_ut=_num(schedule_node, "phaseAnchorUT"),
            cadence_seconds=_num(schedule_node, "cadenceSeconds"),
            prograde=_flag(schedule_node, "prograde"))
    route_node = node.first("ROUTE")
    route = None
    if route_node is not None:
        route = RouteSpec(
            route_id=_text(route_node, "routeId"),
            backing_mission_tree_id=_text(route_node, "backingMissionTreeId"),
            recorded_dock_ut=_num(route_node, "recordedDockUT"),
            recorded_origin_undock_ut=_num(route_node, "recordedOriginUndockUT"),
            dispatch_window_period=_num(route_node, "dispatchWindowPeriod"),
            scope=_text(route_node, "scope"),
            excluded_interval_keys=_split_list(
                _text(route_node, "excludedIntervalKeys"), ";"))
    descent_raw = _split_list(_text(node, "descentMemberIndices"), ",")
    descent_indices = tuple(
        i for i in (_to_int(t) for t in descent_raw) if i is not None)
    return PlanUnit(
        host=_text(node, "host"),
        plan_seq=_int(node, "planSeq"),
        signature_hash=_text(node, "signatureHash"),
        owner_index=_int(node, "ownerIndex"),
        span_start_ut=_num(node, "spanStartUT"),
        span_end_ut=_num(node, "spanEndUT"),
        cadence_seconds=_num(node, "cadenceSeconds"),
        overlap_cadence_seconds=_num(node, "overlapCadenceSeconds"),
        phase_anchor_ut=_num(node, "phaseAnchorUT"),
        is_reaim=_flag(node, "isReaim"),
        has_relaunch_schedule=_flag(node, "hasRelaunchSchedule"),
        members=tuple(
            PlanMember(index=int(_int(m, "index", -1) or -1),
                       rec_id=_text(m, "recId"),
                       start_ut=_num(m, "startUT"),
                       end_ut=_num(m, "endUT"))
            for m in node.nodes_named("MEMBER")),
        loiter_cuts=tuple(
            LoiterCut(start_ut=_num(c, "startUT"),
                      length_seconds=_num(c, "lengthSeconds"))
            for c in node.nodes_named("LOITER_CUT")),
        arrival_hold_seconds=_num(node, "arrivalHoldSeconds"),
        arrival_hold_at_ut=_num(node, "arrivalHoldAtUT"),
        arrival_align_period_seconds=_num(node, "arrivalAlignPeriodSeconds"),
        arrival_joint_secondary_period_seconds=_num(
            node, "arrivalJointSecondaryPeriodSeconds"),
        arrival_joint_secondary_tolerance_seconds=_num(
            node, "arrivalJointSecondaryToleranceSeconds"),
        arrival_joint_max_whole_hold_periods=_int(
            node, "arrivalJointMaxWholeHoldPeriods"),
        arrival_amber_reason=_text(node, "arrivalAmberReason"),
        launch_body_rotation_period_seconds=_num(
            node, "launchBodyRotationPeriodSeconds"),
        launch_hold_engaged=_flag(node, "launchHoldEngaged"),
        recorded_soi_exit_ut=_num(node, "recordedSoiExitUT"),
        descent_member_indices=descent_indices,
        recorded_deorbit_ut=_num(node, "recordedDeorbitUT"),
        descent_end_ut=_num(node, "descentEndUT"),
        destination_body_rotation_period_seconds=_num(
            node, "destinationBodyRotationPeriodSeconds"),
        loiter_period_seconds=_num(node, "loiterPeriodSeconds"),
        capture_shift_seconds=_num(node, "captureShiftSeconds"),
        parking_conic_end_ut=_num(node, "parkingConicEndUT"),
        transfer_member_index=_int(node, "transferMemberIndex"),
        first_deorbit_leg_start_ut=_num(node, "firstDeorbitLegStartUT"),
        transfer_member_recording_id=_text(node, "transferMemberRecordingId"),
        reaim_schedule=schedule,
        route=route,
        launch_body_name=_text(node, "launchBodyName"),
        destination_body_name=_text(node, "destinationBodyName"))


def _parse_chain_build(node: saveparse.SfsNode) -> ChainBuild:
    return ChainBuild(
        pid=_int(node, "pid"),
        rec_id=_text(node, "recId"),
        committed_index=_int(node, "committedIndex"),
        ut=_num(node, "ut"),
        signature=_text(node, "signature"),
        window_index=_int(node, "windowIndex"),
        provenance=_text(node, "provenance"),
        has_reaimed_segments=_flag(node, "hasReaimedSegments"),
        seam_source=_text(node, "seamSource"),
        phases=tuple(
            ChainPhase(kind=_normalize_phase_kind(_text(p, "kind")),
                       provenance=_normalize_provenance(_text(p, "provenance")),
                       body=_text(p, "body"),
                       start_ut=_num(p, "startUT"),
                       end_ut=_num(p, "endUT"))
            for p in node.nodes_named("PHASE")),
        seams=tuple(
            ChainSeam(boundary_index=_int(s, "boundaryIndex"), kind=_text(s, "kind"))
            for s in node.nodes_named("SEAM")))


def parse_render_manifest(text: Optional[str]) -> ManifestSnapshot:
    """Parse ``parsek-render-manifest.txt`` text into a ``ManifestSnapshot``.

    Never raises. Two DEFINED faults, both fail-loud (``parsed=False``):

    - the text is not well-formed ConfigNode (``saveparse.parse_sfs`` error:
      unbalanced close, unclosed at EOF, brace with no name, dangling name);
    - the text carries no top-level ``RENDER_MANIFEST`` node, or carries more
      than one. The first case covers the degenerate truncation an interrupted
      write leaves behind - a zero-byte file is trivially brace-balanced and
      WOULD otherwise read as a clean all-zero manifest, which is exactly the
      trap ``saveparse.parse_parsek_scenario``'s "no GAME node" check exists to
      close (its adversarial finding 1). A manifest that cannot be parsed must
      never read as "zero records".

    The header's ``schemaVersion`` is parsed but NOT enforced here: a version
    mismatch is a FINDING (RC-UNKNOWN) rather than a parse refusal, so a run
    against a newer recorder still reports what it could read.
    """
    res = saveparse.parse_sfs(text)
    if not res.ok:
        return ManifestSnapshot(parsed=False, error=res.error)
    roots = res.root.nodes_named(MANIFEST_ROOT)
    if not roots:
        return ManifestSnapshot(
            parsed=False,
            error="no top-level %s node (empty, truncated-to-empty, or "
                  "non-manifest text)" % MANIFEST_ROOT)
    if len(roots) > 1:
        return ManifestSnapshot(
            parsed=False,
            error="%d %s nodes in one file" % (len(roots), MANIFEST_ROOT))
    root = roots[0]

    constants: Dict[str, float] = {}
    constants_unparsed: List[str] = []
    const_node = root.first("CONSTANTS")
    if const_node is not None:
        for key, raw in const_node.values:
            val = _to_float(raw)
            if math.isnan(val) and str(raw).strip().lower() != "nan":
                constants_unparsed.append(key)
            else:
                constants[key] = val

    units: List[PlanUnit] = []
    plan_node = root.first("PLAN")
    if plan_node is not None:
        units = [_parse_unit(u) for u in plan_node.nodes_named("UNIT")]

    chain_builds: List[ChainBuild] = []
    chain_node = root.first("CHAIN")
    if chain_node is not None:
        chain_builds = [_parse_chain_build(c)
                        for c in chain_node.nodes_named("CHAIN_BUILD")]

    obs = root.first("OBSERVED")
    dwells: List[Dwell] = []
    open_dwells: List[Dwell] = []
    transitions: List[Transition] = []
    tangents: List[SeamTangent] = []
    endpoints: List[SeamEndpoint] = []
    clock_events: List[ClockEvent] = []
    line_branches: List[LineBranch] = []
    ownership: List[OwnershipChange] = []
    skips: List[RatifiedSkip] = []
    defers: List[ClockDefer] = []
    route_builds: List[RouteLineBuild] = []
    route_defers: List[RouteLegDefer] = []
    codraws: List[RouteCoDrawViolation] = []
    echoes: List[AnomalyEcho] = []
    truncs: List[TruncatedRecord] = []
    unknown_sections: List[str] = []
    if obs is not None:
        for child in obs.nodes:
            name = child.name
            if name == "DWELL":
                # The shipped writer marks a dwell still OPEN at export with
                # `openAtExport = True` on the ordinary DWELL node rather than
                # emitting a distinct node kind (its accepted SPEC deviation 3).
                # Both spellings are tolerated; either way an open dwell is kept
                # OUT of `dwells`, because a half-observed interval must not
                # count toward a coverage or anti-vacuity floor.
                if _flag(child, "openAtExport", False):
                    open_dwells.append(_parse_dwell(child, is_open=True))
                else:
                    dwells.append(_parse_dwell(child, is_open=False))
            elif name == "OPEN_DWELL":
                open_dwells.append(_parse_dwell(child, is_open=True))
            elif name == "TRANSITION":
                transitions.append(Transition(
                    pid=_int(child, "pid"), ut=_num(child, "ut"),
                    from_phase_kind=_normalize_phase_kind(_text(child, "fromPhaseKind")),
                    to_phase_kind=_normalize_phase_kind(_text(child, "toPhaseKind")),
                    from_treatment=_text(child, "fromTreatment"),
                    to_treatment=_text(child, "toTreatment"),
                    from_body=_text(child, "fromBody"),
                    to_body=_text(child, "toBody"),
                    from_segment_index=_int(child, "fromSegmentIndex"),
                    to_segment_index=_int(child, "toSegmentIndex"),
                    chain_signature=_text(child, "chainSignature")))
            elif name == "SEAM_TANGENT":
                tangents.append(SeamTangent(
                    pid=_int(child, "pid"), rec_id=_text(child, "recId"),
                    leg_index=_int(child, "legIndex"), ut=_num(child, "ut"),
                    continuous=_flag(child, "continuous"),
                    angle_rad=_num(child, "angleRad"),
                    tolerance_radians=_num(child, "toleranceRadians")))
            elif name == "SEAM_ENDPOINT":
                endpoints.append(SeamEndpoint(
                    pid=_int(child, "pid"), rec_id=_text(child, "recId"),
                    ut=_num(child, "ut"), sampled=_flag(child, "sampled"),
                    skip_reason=_text(child, "skipReason"),
                    ratio=_num(child, "ratio"),
                    endpoint_distance_meters=_num(child, "endpointDistanceMeters"),
                    soi_radius_meters=_num(child, "soiRadiusMeters"),
                    ratio_tolerance=_num(child, "ratioTolerance"),
                    outside_soi=_flag(child, "outsideSoi"),
                    from_body=_text(child, "fromBody"), to_body=_text(child, "toBody"),
                    recorded_seam_ut=_num(child, "recordedSeamUT"),
                    seam_ut=_num(child, "seamUT"),
                    clock_convention=_text(child, "clockConvention"),
                    seed_kind=_text(child, "seedKind")))
            elif name == "CLOCK_EVENT":
                clock_events.append(ClockEvent(
                    kind=_text(child, "kind"),
                    owner_index=_int(child, "ownerIndex"),
                    cycle_index=_int(child, "cycleIndex"),
                    ut=_num(child, "ut"),
                    detail_a=_num(child, "detailA"), detail_b=_num(child, "detailB"),
                    detail_c=_num(child, "detailC"), detail_s=_text(child, "detailS"),
                    detail_d=_num(child, "detailD")))
            elif name == "LINE_BRANCH":
                line_branches.append(LineBranch(
                    pid=_int(child, "pid"), rec_id=_text(child, "recId"),
                    ut=_num(child, "ut"), reason=_text(child, "reason"),
                    line_active=_flag(child, "lineActive"),
                    draw_icons=_int(child, "drawIcons"),
                    icon_suppressed=_flag(child, "iconSuppressed"),
                    coverage=_text(child, "coverage")))
            elif name == "OWNERSHIP_CHANGE":
                ownership.append(OwnershipChange(
                    rec_id=_text(child, "recId"), ut=_num(child, "ut"),
                    event=_text(child, "event")))
            elif name == "RATIFIED_SKIP":
                skips.append(RatifiedSkip(
                    pid=_int(child, "pid"), reason=_text(child, "reason"),
                    first_ut=_num(child, "firstUT"), last_ut=_num(child, "lastUT"),
                    count=_int(child, "count")))
            elif name == "CLOCK_DEFER":
                defers.append(ClockDefer(
                    first_ut=_num(child, "firstUT"), last_ut=_num(child, "lastUT"),
                    count=_int(child, "count")))
            elif name == "ROUTE_LINE_BUILD":
                route_builds.append(RouteLineBuild(
                    route_id=_text(child, "routeId"),
                    signature=_text(child, "signature"),
                    dock_clip_ut=_num(child, "dockClipUT"),
                    dispatch_window_period=_num(child, "dispatchWindowPeriod"),
                    scope=_text(child, "scope"),
                    resolvable_members=_int(child, "resolvableMembers"),
                    groups=_int(child, "groups"),
                    total_legs=_int(child, "totalLegs"),
                    transfer_legs_dropped=_int(child, "transferLegsDropped"),
                    ut=_num(child, "ut")))
            elif name == "ROUTE_LEG_DEFER":
                route_defers.append(RouteLegDefer(
                    route_id=_text(child, "routeId"), rec_id=_text(child, "recId"),
                    count=_int(child, "count")))
            elif name == "ROUTE_CODRAW_VIOLATION":
                codraws.append(RouteCoDrawViolation(
                    route_id=_text(child, "routeId"), rec_id=_text(child, "recId"),
                    ut=_num(child, "ut"), frame=_int(child, "frame")))
            elif name == "ANOMALY_ECHO":
                # STANDALONE (parent = OBSERVED). The DWELL-nested aggregate of
                # the same node NAME is read inside _parse_dwell and never
                # reaches here, because this loop walks OBSERVED's own children.
                echoes.append(AnomalyEcho(
                    pid_key=_text(child, "pidKey"),
                    rec_id=_text(child, "recId"),
                    reason=_text(child, "reason"),
                    ut=_num(child, "ut")))
            elif name == "TRUNCATED":
                truncs.append(TruncatedRecord(
                    section=_text(child, "section"), pid=_int(child, "pid"),
                    kind=_text(child, "kind"),
                    dropped_count=_int(child, "droppedCount")))
            else:
                unknown_sections.append(name)

    return ManifestSnapshot(
        parsed=True, error="",
        schema_version=_int(root, "schemaVersion"),
        export_ut=_num(root, "exportUT"),
        export_reason=_text(root, "exportReason"),
        scene=_text(root, "scene"),
        save_name=_text(root, "saveName"),
        env_armed=_flag(root, "envArmed"),
        force_armed=_flag(root, "forceArmed"),
        map_render_tracing_on=_flag(root, "mapRenderTracingOn"),
        constants=constants, constants_unparsed=tuple(constants_unparsed),
        units=tuple(units), chain_builds=tuple(chain_builds),
        dwells=tuple(dwells), open_dwells=tuple(open_dwells),
        transitions=tuple(transitions), seam_tangents=tuple(tangents),
        seam_endpoints=tuple(endpoints), clock_events=tuple(clock_events),
        line_branches=tuple(line_branches), ownership_changes=tuple(ownership),
        ratified_skips=tuple(skips), clock_defers=tuple(defers),
        route_line_builds=tuple(route_builds), route_leg_defers=tuple(route_defers),
        route_codraw_violations=tuple(codraws), anomaly_echoes=tuple(echoes),
        truncated=tuple(truncs),
        unknown_observed_sections=tuple(dict.fromkeys(unknown_sections)))


# ---------------------------------------------------------------------------
# Clock math. Independent Python re-derivations, transcribed EXACTLY from the
# C# sources per .scout/plan-surface.md section 5.2 (line citations per
# function). These are leg 1 of the oracle-independence rule: the verifier
# re-derives what the plan claims from the plan's PRIMITIVES, so a solver drift
# and a render drift are separable findings.
#
# Two transcription traps, both preserved deliberately:
#   * C#'s `%` can be NEGATIVE for negative operands. Where the C# code applies
#     the double-mod normalisation, so does this - and where it does not, this
#     does not either, because copying the C# result is the point.
#   * `w0 <= 0` returns w0 UNCHANGED (the 13b regression fence). Returning 0.0
#     would look equivalent and is not: an Off/Drop unit carries its own value.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class JointHoldInputs:
    """The D8 joint-hold extra primitives (SpanClock.cs:837-873)."""
    t_rot: float = NAN
    tolerance_seconds: float = NAN
    entry_offset0: float = NAN
    max_whole_periods: int = 0


def arrival_align_hold(recorded_arrival_ut: float, entry_live_ut: float,
                       t_rot: float) -> float:
    """W_0: the entry-time realignment hold (SpanClock.cs:745; authority
    design-mission-periodicity.md arrival-flex section)."""
    if not (t_rot > 0.0) or math.isnan(t_rot) or math.isinf(t_rot):
        return 0.0
    if math.isnan(recorded_arrival_ut) or math.isnan(entry_live_ut):
        return 0.0
    return (recorded_arrival_ut - entry_live_ut) % t_rot


def per_loop_arrival_hold(w0: float, n: int, cadence: float, t_align: float) -> float:
    """W_N: the per-cycle realigned hold (SpanClock.cs:775).

    ``w0 <= 0`` returns ``w0`` unchanged (Off/Drop fence: 0 stays 0), as does a
    degenerate ``t_align``. The double mod keeps the result in [0, t_align)
    despite C#'s signed ``%``."""
    if not (w0 > 0.0):
        return w0
    if math.isnan(t_align) or math.isinf(t_align) or t_align <= 0.0:
        return w0
    return ((w0 - n * (cadence % t_align)) % t_align + t_align) % t_align


def circular_phase_error(delta: float, period: float) -> float:
    """Shortest circular distance of ``delta`` from a whole ``period``
    (MissionPeriodicity.cs:931)."""
    if not (period > 0.0) or math.isnan(period) or math.isinf(period):
        return 0.0
    m = delta % period
    return min(m, period - m)


def per_loop_joint_hold(w0: float, n: int, cadence: float, t_station: float,
                        t_rot: float, tol: float, entry_offset0: float,
                        max_k: int) -> float:
    """The D8 joint hold: the station-keeping hold plus whole station periods
    until the DESTINATION rotation phase is within ``tol`` (SpanClock.cs:837-873).

    ``delta`` is ABSOLUTE (entry offset + N*cadence + base), never an
    accumulating running total - which is what makes the per-cycle drift zero.
    First hit wins; a bounded defensive best is returned when nothing hits."""
    base = per_loop_arrival_hold(w0, n, cadence, t_station)
    if not (base > 0.0) and not (w0 > 0.0):
        return base
    for p in (t_station, t_rot):
        if math.isnan(p) or math.isinf(p) or p <= 0.0:
            return base
    if math.isnan(tol) or tol < 0.0:
        return base
    if math.isnan(entry_offset0) or math.isnan(cadence) or max_k <= 0:
        return base
    delta = entry_offset0 + n * cadence + base
    best_i, best_err = 0, float("inf")
    for i in range(0, max_k + 1):
        err = circular_phase_error(delta + i * t_station, t_rot)
        if err <= tol:
            return base + i * t_station
        if err < best_err:
            best_err, best_i = err, i
    return base + best_i * t_station


def compress_span_ut(t: float, cuts: Sequence[LoiterCut]) -> float:
    """Span UT -> compressed (rendered) UT: ``t`` minus the part of each cut at
    or before ``t`` (SpanClock.cs:691). PARTIAL credit inside a cut is
    deliberate - a UT that lands mid-cut maps to the cut's start."""
    removed = 0.0
    for c in cuts:
        if math.isnan(c.start_ut) or math.isnan(c.length_seconds):
            continue
        if t <= c.start_ut:
            continue
        removed += min(c.length_seconds, t - c.start_ut)
    return t - removed


def decompress_span_ut(c: float, cuts: Sequence[LoiterCut]) -> float:
    """Compressed UT -> span UT (SpanClock.cs:714). Note the running ``t`` in the
    comparison: each re-inflated cut moves the threshold for the next."""
    t = c
    for cut in cuts:
        if math.isnan(cut.start_ut) or math.isnan(cut.length_seconds):
            continue
        if cut.start_ut <= t:
            t += cut.length_seconds
    return t


def joint_entry_offset(phase_anchor_ut: float, arrival_hold_at_ut: float,
                       span_start_ut: float, cuts: Sequence[LoiterCut]) -> float:
    """``entry_offset0`` for the joint solve (SpanClock.cs:809-810, :1423-1424)."""
    return (phase_anchor_ut
            + (compress_span_ut(arrival_hold_at_ut, cuts) - span_start_ut)
            - arrival_hold_at_ut)


def compressed_span_seconds(span_start_ut: float, span_end_ut: float,
                            cuts: Sequence[LoiterCut]) -> float:
    """The unit's span less its cuts, with the C# guard: a total cut that is
    non-positive or swallows the whole span is IGNORED (SpanClock.cs:1713-1749)."""
    span = span_end_ut - span_start_ut
    total_cut = 0.0
    for c in cuts:
        if _finite(c.length_seconds):
            total_cut += c.length_seconds
    if 0.0 < total_cut < span:
        return span - total_cut
    return span


def clamp_hold_to_cycle(hold: float, compressed_span: float, cadence: float) -> float:
    """The clock clamp (SpanClock.cs:1485-1486): a hold may not push the cycle
    past its own cadence."""
    if hold > 0.0 and compressed_span + hold > cadence:
        return max(0.0, cadence - compressed_span)
    return hold


def resolve_per_loop_arrival_hold(w0: float, n: int, cadence: float, t_align: float,
                                  joint: Optional[JointHoldInputs] = None) -> float:
    """Dispatch: joint (D8) variant when its primitives are present and valid,
    plain realignment otherwise (SpanClock.cs:801-805 validity gate)."""
    if joint is None:
        return per_loop_arrival_hold(w0, n, cadence, t_align)
    return per_loop_joint_hold(w0, n, cadence, t_align, joint.t_rot,
                               joint.tolerance_seconds, joint.entry_offset0,
                               int(joint.max_whole_periods or 0))


def per_loop_launch_advance(phase_anchor_ut: float, span_start_ut: float, n: int,
                            cadence: float, t_sid_signed: float) -> float:
    """delta_N: the launch-alignment borrow (SpanClock.cs:905).

    ``abs()`` on the sidereal period is PadAlignLaunch's retrograde handling, not
    a defensive tidy. ``off_n`` is a launch DISPLACEMENT, not an epoch, so the
    result is a sawtooth in N rather than a monotone drift."""
    t_sid = abs(t_sid_signed)
    if math.isnan(t_sid) or math.isinf(t_sid) or t_sid <= 0.0:
        return 0.0
    if math.isnan(phase_anchor_ut) or math.isnan(span_start_ut):
        return 0.0
    off_n = (phase_anchor_ut - span_start_ut) + n * cadence
    return ((off_n % t_sid) + t_sid) % t_sid


def capped_launch_advance(phase_anchor_ut: float, span_start_ut: float,
                          span_end_ut: float, cadence: float, window: int,
                          t_sid: float, cuts: Sequence[LoiterCut],
                          arrival_hold_seconds: float, t_align: float,
                          joint: Optional[JointHoldInputs] = None) -> float:
    """delta_N capped by the LAUNCHING cycle's slack (SpanClock.cs:939).

    The hold that eats the slack is the one resolved for cycle ``window - 1``:
    the borrow is taken from the cycle that is about to launch, not the one that
    just did."""
    delta = per_loop_launch_advance(phase_anchor_ut, span_start_ut, window,
                                    cadence, t_sid)
    if not (delta > 0.0):
        return 0.0
    compressed = compressed_span_seconds(span_start_ut, span_end_ut, cuts)
    w = arrival_hold_seconds if (arrival_hold_seconds > 0.0
                                 and not math.isinf(arrival_hold_seconds)) else 0.0
    if w > 0.0:
        w = resolve_per_loop_arrival_hold(w, window - 1, cadence, t_align, joint)
    if w > 0.0 and compressed + w > cadence:
        w = max(0.0, cadence - compressed)
    slack = max(0.0, cadence - compressed - w)
    return delta if delta < slack else slack


def boundary_overlap_advance(phase_anchor_ut: float, span_start_ut: float,
                             span_end_ut: float, cadence: float, window: int,
                             t_sid: float, cuts: Sequence[LoiterCut],
                             arrival_hold_seconds: float, t_align: float,
                             joint: Optional[JointHoldInputs] = None) -> float:
    """The boundary-overlap secondary advance (SpanClock.cs:1002).

    GATED, not blanket-uncapped: the raw delta is used only when it actually
    EXCEEDS the capped one, which is the condition RC-CYCLE checks against every
    observed ``boundary-overlap-secondary`` clock event."""
    raw = per_loop_launch_advance(phase_anchor_ut, span_start_ut, window,
                                  cadence, t_sid)
    if not (raw > 0.0):
        return 0.0
    capped = capped_launch_advance(phase_anchor_ut, span_start_ut, span_end_ut,
                                   cadence, window, t_sid, cuts,
                                   arrival_hold_seconds, t_align, joint)
    return raw if raw > capped + ADVANCE_COMPARE_EPSILON else capped


def apply_hold_to_phase(phase: float, pos: float, hold: float) -> float:
    """Insert a hold of ``hold`` seconds at compressed position ``pos``
    (SpanClock.cs:1040). Before the hold: unchanged. Inside: frozen at ``pos``.
    After: shifted back by the hold."""
    if math.isnan(hold) or hold <= 0.0:
        return phase
    if phase <= pos:
        return phase
    if phase <= pos + hold:
        return pos
    return phase - hold


def apply_loiter_extension(phase: float, insert_pos: float, ext_len: float,
                           wrap_period: float) -> float:
    """The M4b loiter EXTENSION (schedule path only, SpanClock.cs:1065).
    ``ext_len`` is a whole multiple of ``wrap_period`` by construction."""
    if math.isnan(ext_len) or ext_len <= 0.0 or math.isnan(insert_pos):
        return phase
    if math.isnan(wrap_period) or math.isinf(wrap_period) or wrap_period <= 0.0:
        return phase
    if phase <= insert_pos:
        return phase
    if phase < insert_pos + ext_len:
        return insert_pos + ((phase - insert_pos) % wrap_period)
    return phase - ext_len


def launch_residual_seam_deg(raw_delta_n: float, effective_advance: float,
                             t_sid: float) -> float:
    """The launch-hold residual seam angle (design-reaim-launch-hold-seam.md
    section 5; SpanClock.cs:1640-1643). In [0, 360)."""
    if not _finite(raw_delta_n) or not _finite(effective_advance):
        return NAN
    if not _finite(t_sid) or t_sid <= 0.0:
        return NAN
    return (((raw_delta_n - effective_advance) % t_sid) + t_sid) % t_sid / t_sid * 360.0


def rotation_aligned_trigger(entry_ut: float, recorded_deorbit_ut: float,
                             t_rot: float, offset: float = 0.0) -> float:
    """The descent trigger UT: the first instant at or after ``entry_ut`` whose
    body-rotation phase matches the recorded deorbit's (DescentTrigger.cs:242).
    Result lies in [entry_ut, entry_ut + t_rot)."""
    for v in (entry_ut, recorded_deorbit_ut, t_rot):
        if math.isnan(v) or math.isinf(v):
            return NAN
    if t_rot <= 0.0:
        return NAN
    off = 0.0 if (math.isnan(offset) or math.isinf(offset)) else offset
    phase = (recorded_deorbit_ut + off - entry_ut) % t_rot
    return entry_ut + phase


def compute_descent_timing(n: int, phase_anchor_ut: float, cadence: float,
                           span_start_ut: float, recorded_deorbit_ut: float,
                           t_rot: float, capture_shift: float,
                           cuts: Sequence[LoiterCut],
                           site_align_offset: float = 0.0
                           ) -> Tuple[float, float, float]:
    """(parkingConicEndUT, entryUT, triggerUT) for cycle ``n``
    (DescentTrigger.cs:48; .scout/plan-surface.md 5.2(d)).

    ``capture_shift`` is <= 0 by construction (a re-aimed geometric transfer is
    never longer than the recorded one at this seam)."""
    conic_end = recorded_deorbit_ut + capture_shift
    entry_offset = compress_span_ut(conic_end, cuts) - span_start_ut
    entry_ut = phase_anchor_ut + n * cadence + entry_offset
    trigger_ut = rotation_aligned_trigger(entry_ut, recorded_deorbit_ut, t_rot,
                                          site_align_offset)
    return conic_end, entry_ut, trigger_ut


def descent_site_rotation_residual_deg(trigger_ut: float,
                                       recorded_deorbit_ut: float,
                                       t_rot: float) -> float:
    """The congruence metric RC-DESCENT asserts is ~0 (DescentTrigger.cs:695-699).

    Zero BY CONSTRUCTION when the trigger came from ``rotation_aligned_trigger``
    with a zero site-align offset, which is exactly why a non-zero reading is a
    defect rather than a tolerance question."""
    if not _finite(trigger_ut) or not _finite(recorded_deorbit_ut):
        return NAN
    if not _finite(t_rot) or t_rot <= 0.0:
        return NAN
    r = ((trigger_ut - recorded_deorbit_ut) % t_rot + t_rot) % t_rot
    return 360.0 * min(r, t_rot - r) / t_rot


def site_align_offset_seconds(rotation_deg: float, t_rot: float) -> float:
    """The S4 arrival-restitch site-align offset (Reaim/ArrivalRestitch.cs:93).

    PLAN-CARRIED, not independently recomputable: ``rotation_deg`` comes from the
    restitch solve, so the verifier can only check the CONVERSION here, never
    re-derive the rotation itself."""
    for x in (rotation_deg, t_rot):
        if math.isnan(x) or math.isinf(x):
            return 0.0
    if t_rot <= 0.0:
        return 0.0
    return (rotation_deg / 360.0 * t_rot) % t_rot


def descent_window_end_ut(trigger_ut: float, descent_end_ut: float,
                          recorded_deorbit_ut: float) -> float:
    """The live UT at which the descent window closes (DescentTrigger.cs:81)."""
    return trigger_ut + (descent_end_ut - recorded_deorbit_ut)


def whole_multiple_ratio(length: float, period: float) -> Optional[float]:
    """``length / period`` when both are usable, else None (the caller then
    treats the clause as defined-unevaluable rather than as a violation)."""
    if not _finite(length) or not _finite(period) or period <= 0.0:
        return None
    return length / period


def is_whole_multiple(length: float, period: float,
                      tolerance: float = CUT_WHOLE_MULTIPLE_TOLERANCE) -> Optional[bool]:
    """Is ``length`` an exact non-negative whole multiple of ``period``?

    None when unevaluable. The snap tolerance mirrors ReaimLoiterCompressor's own
    ``floor(x + 1e-6)`` revolution count (:140), because the cut length is
    ``(wholeRevs - keepRevs) * period`` computed in the same doubles."""
    ratio = whole_multiple_ratio(length, period)
    if ratio is None:
        return None
    if ratio < -tolerance:
        return False
    return abs(ratio - round(ratio)) <= tolerance


# ---------------------------------------------------------------------------
# Rule evaluation.
# ---------------------------------------------------------------------------

# Defined-unevaluable reasons. Every one of these is a POSITIVE statement about
# why a clause could not be answered, not a catch-all bucket.
UNEVAL_SEAM_TRACING_OFF = "seam-data-unavailable-tracing-off"
UNEVAL_TRUNCATED = "truncated-section"
UNEVAL_NO_CYCLE_BOUNDARIES = "no-cycle-rollover-events"
UNEVAL_NO_DWELLS_FOR_UNIT = "no-dwells-attributable-to-unit"
UNEVAL_REAIM_INSTANT_ABSENT = "reaimed-seam-instant-absent"
UNEVAL_SEAM_SKIPPED = "seam-endpoint-skipped"
UNEVAL_HOLD_EVIDENCE_ABSENT = "hold-observed-evidence-absent"
UNEVAL_HOLD_PRIMITIVES_ABSENT = "hold-primitives-absent"
UNEVAL_HOLD_RELEASE_ABSENT = "hold-release-absent-below-resolution"
# A stationary run the PLANNED hold cannot account for. The arrival hold is
# inserted ONCE per cycle, so at most one run per (owner, cycle) can be it; the
# review-pass detector legitimately emits several (a loiter stall, a Director
# hold across an interior gap, a second stall after a warp change). The plan
# primitives predict one number and cannot say which run is which, so the runs
# the rule does not attribute are COUNTED here rather than compared against a
# prediction that was never about them.
UNEVAL_HOLD_RUN_UNATTRIBUTED = "hold-run-not-attributable-to-planned-hold"
# A hold RUN observed on a unit whose plan carries no hold at all (no
# arrivalHoldSeconds, no launch hold). There is no prediction to compare it
# against, so it cannot be judged - but it also must not vanish: the clock DID
# stand still. WARN + counted, so "no finding" keeps meaning "nothing happened".
UNEVAL_HOLD_OBSERVED_WITHOUT_PLAN = "hold-observed-without-plan"
UNEVAL_CUT_PERIOD_ABSENT = "cut-run-period-absent"
UNEVAL_CUT_CONTAINMENT = "cut-dwell-containment-needs-recorded-clock"
UNEVAL_DESCENT_PRIMITIVES_ABSENT = "descent-primitives-absent"
UNEVAL_DESCENT_HEAD_ABSENT = "descent-head-absent-from-schema"
UNEVAL_POSITIONS_ABSENT = "truth-positions-absent"
UNEVAL_PRIMITIVE_BODY_UNKNOWN = "plan-primitive-body-unidentified"
UNEVAL_ROUTE_LEG_DETAIL_ABSENT = "route-per-leg-detail-absent"
UNEVAL_WARP_HOLD_ABSENT = "warp-hold-traversal-evidence-absent"
UNEVAL_MARKER_DECISION_ABSENT = "marker-decision-absent"
# RC-COVER's two resolution-model reasons.
#   * A dark window inside a RATIFIED_SKIP hull. The record is a HULL WITH A
#     COUNT (firstUT / lastUT / count), not a list of intervals, so it can say
#     "N frames in this bracket declined to render" and nothing about WHICH
#     instants. Treating the hull as covered would pass a genuinely dark window
#     that merely shares a bounding box with those N skips; treating it as a gap
#     would red the ratified skip working as designed. It is neither: it is
#     defined-unevaluable, counted here.
#   * No positive step evidence for the scope being judged. Without a maxUtStep
#     the verifier does not know the sampling rate, so it cannot say a window is
#     WIDER than the resolution - and a zero floor would make every window wider
#     by construction, converting "unmeasured" into FAIL.
UNEVAL_COVER_RATIFIED_SKIP_HULL = "ratified-skip-hull"
UNEVAL_COVER_RESOLUTION_ABSENT = "below-resolution-evidence-absent"
UNEVAL_COVER_SKIPS_TRUNCATED = "ratified-skip-section-truncated"

UNEVALUABLE_REASONS: Tuple[str, ...] = (
    UNEVAL_SEAM_TRACING_OFF, UNEVAL_TRUNCATED, UNEVAL_NO_CYCLE_BOUNDARIES,
    UNEVAL_NO_DWELLS_FOR_UNIT, UNEVAL_REAIM_INSTANT_ABSENT, UNEVAL_SEAM_SKIPPED,
    UNEVAL_HOLD_EVIDENCE_ABSENT, UNEVAL_HOLD_PRIMITIVES_ABSENT,
    UNEVAL_HOLD_RELEASE_ABSENT, UNEVAL_HOLD_RUN_UNATTRIBUTED,
    UNEVAL_HOLD_OBSERVED_WITHOUT_PLAN,
    UNEVAL_CUT_PERIOD_ABSENT, UNEVAL_CUT_CONTAINMENT,
    UNEVAL_DESCENT_PRIMITIVES_ABSENT, UNEVAL_DESCENT_HEAD_ABSENT,
    UNEVAL_POSITIONS_ABSENT, UNEVAL_PRIMITIVE_BODY_UNKNOWN,
    UNEVAL_ROUTE_LEG_DETAIL_ABSENT, UNEVAL_WARP_HOLD_ABSENT,
    UNEVAL_MARKER_DECISION_ABSENT,
    UNEVAL_COVER_RATIFIED_SKIP_HULL, UNEVAL_COVER_RESOLUTION_ABSENT,
    UNEVAL_COVER_SKIPS_TRUNCATED,
)

# TRUNCATED.section spellings. The review pass made the family-cap marker
# consistently `<SECTION>:global` with pid 0 (a PER-PID cap drop keeps the plain
# token and the real pid), left the whole-export ceiling as `ALL:global`, and
# added one reserved row for the fail-closed clock-event dedupe.
TRUNCATED_GLOBAL_SUFFIX = ":global"
TRUNCATED_ALL_SECTION = "ALL" + TRUNCATED_GLOBAL_SUFFIX
# The RESERVED row the writer folds every further DISTINCT truncation key into
# once the marker list itself hits `MaxTruncationRecords`
# (`RenderCompositionManifest.TruncationOverflowSection` / `...OverflowKind`).
# The drop is still counted but no longer names its section, so like ALL:global
# it stands every section down.
TRUNCATED_OVERFLOW_SECTION = "TRUNCATED" + TRUNCATED_GLOBAL_SUFFIX
TRUNCATED_OVERFLOW_KIND = "distinct-key-overflow"
TRUNCATED_CLOCK_EVENT_DEDUPE = "CLOCK_EVENT:dedupe-exhausted"


class _Ctx:
    """Mutable accumulator threaded through the rule evaluators.

    Also the per-evaluation MEMO for the two derivations every rule re-derives
    (the dwells attributable to a unit, and a unit's cycle windows). Both are
    pure functions of the snapshot, so caching them for one evaluation cannot
    change a verdict - it only stops the rule set walking every dwell once per
    rule per unit."""

    def __init__(self, snapshot: ManifestSnapshot, block: Optional[Dict]) -> None:
        self.snap = snapshot
        self.block = block if isinstance(block, dict) else None
        self.armed = bool(self.block and self.block.get(GATING_KEY) is True)
        self.findings: List[RenderComposeFinding] = []
        self.unevaluable: Dict[str, int] = {}
        self.metrics: Dict[str, Any] = {}
        self.truncated_sections = {t.section for t in snapshot.truncated if t.section}
        self._dwell_cache: Dict[Any, List[Dwell]] = {}
        self._cycle_cache: Dict[Any, List[Tuple[int, float, float]]] = {}

    def add(self, rule_id: str, level: str, target: Any, message: str,
            cited: Optional[str] = None) -> None:
        self.findings.append(_finding(rule_id, level, target, message, cited))

    def uneval(self, reason: str, count: int = 1) -> None:
        self.unevaluable[reason] = self.unevaluable.get(reason, 0) + count

    def unknown_token(self, target: Any, label: str, value: str,
                      vocabulary: Sequence[str], note: str = "") -> bool:
        """RC-UNKNOWN FAIL for one observed token outside its pinned vocabulary.

        Returns True IFF a finding was raised, so a site that must also skip the
        record can branch on the return value instead of repeating the
        membership test. A BLANK value is never "unknown" (absent is not wrong)
        and returns False, which is the behaviour every call site had before this
        helper existed."""
        text = str(value or "").strip()
        if not text or text in vocabulary:
            return False
        message = "unknown %s %r (known: %s)" % (label, text, list(vocabulary))
        if note:
            message += " - " + note
        self.add(RULE_UNKNOWN, LEVEL_FAIL, target, message)
        return True

    def dwells_for_unit(self, unit: PlanUnit) -> List[Dwell]:
        """Memoized ``_dwells_for_unit``.

        Keyed on the unit OBJECT's identity, not on its owner index: the owner
        index is the writer's attribution key but it is NOT unique across the
        manifest (two hosts each run their own LoopUnitSet, so `Flight` owner 0
        and `TrackingStation` owner 0 are different units), and the attribution
        falls back to the unit's own MEMBER set whenever a dwell carries no
        ownerIndex - so two units sharing an index would get each other's
        member-matched dwells. The rules iterate `snap.units`, which the snapshot
        holds for this _Ctx's whole life, so identity is stable and the hit rate
        is total."""
        key = id(unit)
        cached = self._dwell_cache.get(key)
        if cached is None:
            cached = _dwells_for_unit(self.snap, unit)
            self._dwell_cache[key] = cached
        return cached

    def cycle_windows(self, owner_index: Optional[int]
                      ) -> List[Tuple[int, float, float]]:
        """Memoized ``_cycle_windows``."""
        cached = self._cycle_cache.get(owner_index)
        if cached is None:
            cached = _cycle_windows(self.snap, owner_index)
            self._cycle_cache[owner_index] = cached
        return cached

    def section_truncated(self, section: str) -> bool:
        """Did the accumulation core drop records that would have fed ``section``?

        Membership only. The unevaluable ledger entry is written ONCE, by
        RC-UNKNOWN over the TRUNCATED records themselves, so a section two rules
        both decline to evaluate is not counted twice.

        FIVE spellings answer yes for a section S, all of them from the review
        pass's single admission helper (per-pid cap, then family cap, then the
        whole-export ceiling):

        - ``S``                      - a PER-PID cap drop, carrying the real pid;
        - ``S:global``               - a FAMILY cap drop, pid 0;
        - ``ALL:global``             - the whole-export ceiling, which truncates
          every section from some point onward, so no section is complete;
        - ``TRUNCATED:global``       - the marker list itself overflowed
          ``MaxTruncationRecords``, so past that point a drop is still counted but
          STOPS NAMING ITS SECTION. The dropped records could have belonged to any
          section, so this row stands EVERY section down, exactly like
          ``ALL:global`` - reading it as "only the TRUNCATED section is
          incomplete" would let a real drop pass unnoticed under a rule that
          checked its own section and found nothing;
        - ``CLOCK_EVENT:dedupe-exhausted`` (for S == ``CLOCK_EVENT`` only) - the
          fail-closed dedupe table filled and the CLOCK_EVENT stream stopped
          accepting events. Reading that row as a clean pass is exactly the trap
          it was introduced to close.
        """
        if section in self.truncated_sections:
            return True
        if section + TRUNCATED_GLOBAL_SUFFIX in self.truncated_sections:
            return True
        if TRUNCATED_ALL_SECTION in self.truncated_sections:
            return True
        if TRUNCATED_OVERFLOW_SECTION in self.truncated_sections:
            return True
        if section == "CLOCK_EVENT" and \
                TRUNCATED_CLOCK_EVENT_DEDUPE in self.truncated_sections:
            return True
        return False


def _unit_label(unit: PlanUnit) -> str:
    return "unit[host=%s planSeq=%s owner=%s]" % (
        unit.host or "?", unit.plan_seq, unit.owner_index)


def _unit_rec_ids(unit: PlanUnit) -> set:
    return {m.rec_id for m in unit.members if m.rec_id}


def _unit_indices(unit: PlanUnit) -> set:
    return {m.index for m in unit.members if m.index is not None and m.index >= 0}


def _dwells_for_unit(snapshot: ManifestSnapshot, unit: PlanUnit) -> List[Dwell]:
    """Dwells attributable to a unit.

    Schema v1.1 stamps the OWNING unit on the dwell (decision 5), so a dwell that
    carries one is attributed by that and nothing else - it is the writer's own
    answer, not an inference. Without it (schema v1, or a member index no unit
    claimed) attribution falls back to the unit's MEMBER recIds with committedIndex
    behind them; an id-less unattributable dwell is then left out rather than
    silently assigned."""
    rec_ids = _unit_rec_ids(unit)
    indices = _unit_indices(unit)
    out = []
    for d in snapshot.dwells:
        if d.owner_index is not None and unit.owner_index is not None:
            if d.owner_index == unit.owner_index:
                out.append(d)
            continue
        if d.rec_id and d.rec_id in rec_ids:
            out.append(d)
        elif not d.rec_id and d.committed_index in indices:
            out.append(d)
    return out


def _local_max_ut_step(ctx: "_Ctx", unit: PlanUnit, ut: float) -> float:
    """The warp resolution local to ``ut`` for one unit: the maxUtStep of the
    dwell(s) covering that instant, or - when nothing covers it - the unit's widest
    step. NaN when the unit has no step evidence at all, which the caller must treat
    as "the resolution is UNMEASURED", never as zero.

    THE CONTRACT (both callers depend on it): a NaN here is the absence of
    evidence, and only a POSITIVE return is evidence. Zero or negative is a
    degenerate stamp, not a measurement of an infinitely fine sampling rate -
    reading it as one would make every dark window "wider than the resolution"
    and turn an unmeasured run into a wall of reds."""
    dwells = ctx.dwells_for_unit(unit)
    covering = [d.max_ut_step for d in dwells
                if _finite(d.max_ut_step) and _finite(d.open_ut) and _finite(d.close_ut)
                and d.open_ut <= ut <= d.close_ut]
    if covering:
        return max(covering)
    anywhere = [d.max_ut_step for d in dwells if _finite(d.max_ut_step)]
    return max(anywhere) if anywhere else NAN


def _cycle_windows(snapshot: ManifestSnapshot,
                   owner_index: Optional[int]) -> List[Tuple[int, float, float]]:
    """(cycleIndex, startUT, endUT) per observed cycle, derived from the
    ``cycle-rollover`` clock events of one owner. Needs at least two rollovers to
    bound one cycle; the trailing open cycle is deliberately NOT synthesised
    against the export UT (a cycle the export cut in half is not a cycle)."""
    events = [e for e in snapshot.clock_events
              if e.kind == "cycle-rollover" and _finite(e.ut)
              and (owner_index is None or e.owner_index == owner_index)]
    events.sort(key=lambda e: e.ut)
    out = []
    for i in range(len(events) - 1):
        idx = events[i].cycle_index
        out.append((int(idx) if idx is not None else i,
                    events[i].ut, events[i + 1].ut))
    return out


def _merge_intervals(spans: Sequence[Tuple[float, float]]) -> List[Tuple[float, float]]:
    usable = sorted((a, b) for a, b in spans if _finite(a) and _finite(b) and b > a)
    merged: List[Tuple[float, float]] = []
    for lo, hi in usable:
        if merged and lo <= merged[-1][1]:
            if hi > merged[-1][1]:
                merged[-1] = (merged[-1][0], hi)
        else:
            merged.append((lo, hi))
    return merged


def _complement(window: Tuple[float, float],
                covered: Sequence[Tuple[float, float]]) -> List[Tuple[float, float]]:
    lo, hi = window
    gaps: List[Tuple[float, float]] = []
    cursor = lo
    for a, b in _merge_intervals(covered):
        if b <= lo or a >= hi:
            continue
        a = max(a, lo)
        b = min(b, hi)
        if a > cursor:
            gaps.append((cursor, a))
        cursor = max(cursor, b)
    if cursor < hi:
        gaps.append((cursor, hi))
    return gaps


# --- RC-CONST ---------------------------------------------------------------


def _rule_const(ctx: _Ctx) -> None:
    """Header CONSTANTS vs RATIFIED_TOLERANCES, plus the oracle-independence
    leg-1 check of the plan's own body-period PRIMITIVES against the stock
    tables. The table is the authority; a drift is a finding either way."""
    snap = ctx.snap
    if snap.schema_version is not None and snap.schema_version != SCHEMA_VERSION:
        ctx.add(RULE_UNKNOWN, LEVEL_FAIL, "header.schemaVersion",
                "manifest schemaVersion %s != verifier SCHEMA_VERSION %d - the "
                "record shapes below were read against a contract the writer did "
                "not promise" % (snap.schema_version, SCHEMA_VERSION))
    ctx.unknown_token("header.exportReason", "exportReason",
                      snap.export_reason, EXPORT_REASONS)
    for key in snap.constants_unparsed:
        ctx.add(RULE_CONST, LEVEL_FAIL, "CONSTANTS." + key,
                "value is not a parseable double - the ratified comparison "
                "cannot run against it")
    for name, expected in sorted(RATIFIED_TOLERANCES.items()):
        if name not in snap.constants:
            ctx.add(RULE_CONST, LEVEL_FAIL, "CONSTANTS." + name,
                    "ratified constant absent from the exported CONSTANTS node - "
                    "the catalog pins %r and the manifest carries no transport "
                    "for it" % expected)
            continue
        actual = snap.constants[name]
        if not _finite(actual) or not math.isclose(
                actual, expected, rel_tol=CONSTANT_REL_TOLERANCE, abs_tol=0.0):
            ctx.add(RULE_CONST, LEVEL_FAIL, "CONSTANTS." + name,
                    "exported %r drifted from the ratified %r" % (actual, expected))
    for alias, canonical in RATIFIED_ALIAS_PAIRS:
        a, b = snap.constants.get(alias), snap.constants.get(canonical)
        if a is None or b is None:
            continue
        if not math.isclose(a, b, rel_tol=CONSTANT_REL_TOLERANCE, abs_tol=0.0):
            ctx.add(RULE_CONST, LEVEL_FAIL, "CONSTANTS." + alias,
                    "alias %r == %r diverged from %r == %r; the C# declares the "
                    "first as `= %s`" % (alias, a, canonical, b, canonical))
    for name in sorted(snap.constants):
        if name not in RATIFIED_TOLERANCES:
            ctx.add(RULE_CONST, LEVEL_WARN, "CONSTANTS." + name,
                    "constant exported but not pinned by RATIFIED_TOLERANCES - "
                    "add a cited row or stop exporting it")
    for unit in ctx.snap.units:
        _check_plan_primitive(ctx, unit, "launchBodyRotationPeriodSeconds",
                              unit.launch_body_rotation_period_seconds,
                              unit.launch_body_name,
                              STOCK_BODY_ROTATION_PERIOD_SECONDS)
        _check_plan_primitive(ctx, unit, "destinationBodyRotationPeriodSeconds",
                              unit.destination_body_rotation_period_seconds,
                              unit.destination_body_name,
                              STOCK_BODY_ROTATION_PERIOD_SECONDS)


def _check_plan_primitive(ctx: _Ctx, unit: PlanUnit, key: str, value: float,
                          body_name: str, table: Dict[str, float]) -> None:
    """Oracle-independence leg 1 for one plan primitive.

    With a body NAME present (a forward-compatible key schema v1 does not
    define) the check is exact against the table row. Without one, the only
    honest question is "is this value a NEAR miss of some ratified period" -
    within the near-match band but not equal is a drift finding; outside it, the
    value belongs to a body the table does not carry (unlisted or modded) and the
    clause is defined-unevaluable, never a red."""
    if not _finite(value) or value <= 0.0:
        return
    if body_name:
        if body_name not in table:
            ctx.uneval(UNEVAL_PRIMITIVE_BODY_UNKNOWN)
            return
        expected = table[body_name]
        if not math.isclose(value, expected, rel_tol=PRIMITIVE_REL_TOLERANCE,
                            abs_tol=0.0):
            ctx.add(RULE_CONST, LEVEL_FAIL, "%s.%s" % (_unit_label(unit), key),
                    "leg 1 (solver drift): plan primitive %r for body %r "
                    "disagrees with the stock table's %r"
                    % (value, body_name, expected))
        return
    nearest_body, nearest_rel = None, None
    for body, period in table.items():
        if period <= 0.0:
            continue
        rel = abs(value - period) / period
        if nearest_rel is None or rel < nearest_rel:
            nearest_body, nearest_rel = body, rel
    if nearest_rel is None or nearest_rel > PRIMITIVE_NEAR_MATCH_REL_BAND:
        ctx.uneval(UNEVAL_PRIMITIVE_BODY_UNKNOWN)
        return
    if nearest_rel > PRIMITIVE_REL_TOLERANCE:
        ctx.add(RULE_CONST, LEVEL_FAIL, "%s.%s" % (_unit_label(unit), key),
                "leg 1 (solver drift): plan primitive %r sits %.6g relative from "
                "the stock %s period %r and matches no other ratified body - a "
                "near miss of a known period is a drifted primitive, not a "
                "different body" % (value, nearest_rel, nearest_body,
                                    table[nearest_body]))


# --- RC-COVER ---------------------------------------------------------------


def _cover_resolution(ctx: _Ctx, unit: PlanUnit, in_cycle: Sequence[Dwell],
                      ut: float) -> Optional[float]:
    """The warp resolution to judge one dark window at, or ``None`` for "the
    manifest carries no positive step evidence for this scope".

    Routed through ``_local_max_ut_step``'s contract rather than a hardcoded
    seconds floor. Scope order: the dwells INSIDE the judged cycle first (they
    are the frames that actually rendered the window), then the unit-wide answer
    ``_local_max_ut_step`` gives for the instant. Only a POSITIVE step is
    evidence; NaN, zero and negative are all "unmeasured", and the caller must
    count an unmeasured window as defined-unevaluable rather than red it."""
    local = [d.max_ut_step for d in in_cycle
             if _finite(d.max_ut_step) and d.max_ut_step > 0.0]
    if local:
        return max(local)
    step = _local_max_ut_step(ctx, unit, ut)
    if _finite(step) and step > 0.0:
        return step
    return None


def _rule_cover(ctx: _Ctx) -> None:
    """Per unit per observed cycle: the union of dwell spans plus the cataloged
    dark windows must equal the cycle window, at the resolution the cycle's own
    warp histogram supports.

    Three populations are DEFINED-UNEVALUABLE rather than red, each for a stated
    reason (see the UNEVAL_COVER_* comments): a window the manifest carries no
    step evidence to size, a window inside a RATIFIED_SKIP hull, and - when the
    RATIFIED_SKIP section itself was truncated - any window a dropped skip record
    might have explained."""
    snap = ctx.snap
    if ctx.section_truncated("DWELL"):
        return
    unexplained = 0
    below_resolution = 0
    resolution_absent = 0
    ratified_hidden = 0
    # A truncated RATIFIED_SKIP section means an unknown number of skip hulls
    # never reached the manifest, so a window one of them would have covered is
    # indistinguishable from a genuine gap. Neither pass nor red: counted.
    skips_truncated = ctx.section_truncated("RATIFIED_SKIP")
    for unit in snap.units:
        dwells = ctx.dwells_for_unit(unit)
        if not dwells:
            ctx.uneval(UNEVAL_NO_DWELLS_FOR_UNIT)
            continue
        windows = ctx.cycle_windows(unit.owner_index)
        if not windows:
            ctx.uneval(UNEVAL_NO_CYCLE_BOUNDARIES)
            continue
        rec_ids = _unit_rec_ids(unit)
        pids = {d.pid for d in dwells if d.pid is not None}
        skips = [s for s in snap.ratified_skips
                 if s.pid in pids and _finite(s.first_ut) and _finite(s.last_ut)]
        tails = [e.ut for e in snap.clock_events
                 if e.kind == "inter-cycle-tail" and _finite(e.ut)
                 and (e.owner_index is None or e.owner_index == unit.owner_index)]
        # A descent-phase event's ownerIndex is the DESCENT MEMBER index, not the
        # unit's ownerIndex (shipped writer convention) - matching it against the
        # unit's owner would silently match nothing and quietly drop the
        # explanation, so the membership test is against descentMemberIndices.
        descent_members = set(unit.descent_member_indices)
        idle_descent = [e.ut for e in snap.clock_events
                        if e.kind == "descent-phase" and _finite(e.ut)
                        and e.detail_s in ("Inert", "Done")
                        and (e.owner_index is None
                             or e.owner_index in descent_members)]
        for cycle_index, lo, hi in windows:
            if not (_finite(lo) and _finite(hi) and hi > lo):
                continue
            in_cycle = [d for d in dwells
                        if _finite(d.open_ut) and _finite(d.close_ut)
                        and d.close_ut > lo and d.open_ut < hi]
            # RATIFIED_SKIP hulls are DELIBERATELY NOT fed into `covered`. The
            # record is a HULL WITH A COUNT (firstUT / lastUT / count), not a
            # list of intervals: it says N frames between two instants declined
            # to render and nothing about WHICH instants. Treating the bracket as
            # covered silently passes a genuinely dark window that merely shares
            # a bounding box with those skips - and the wider the bracket, the
            # more it hides. A window inside a hull is counted as unevaluable
            # below instead, so the skip explains the window without ever
            # asserting coverage the record cannot support.
            #
            # ONLY A VISIBLE DWELL IS COVERAGE. The design states RC-COVER as the
            # union of VISIBLE dwells plus the cataloged holds / cuts / tail /
            # ratified hidden windows. An invisible dwell is a render INTENT that
            # produced no line: the span it occupies is dark on screen. Feeding it
            # into `covered` would make the exact defect class this rule exists to
            # catch - a leg that is planned, dwelt on, and never drawn - self-
            # explaining, because the leg's own dwell would paper over its own
            # darkness. So:
            #   * visible dwell                 -> coverage;
            #   * invisible + OutsideWindow      \  the two RATIFIED hidden-window
            #   * invisible + InInteriorGap      /  shapes: cataloged, counted
            #                                       unevaluable, not coverage;
            #   * invisible + InSegment         -> NOT coverage and NOT cataloged;
            #                                      its span stays dark and falls
            #                                      through to the gap walk below.
            # `visible is not False` rather than `visible` deliberately, matching
            # RC-OWN: the writer always emits the key, so only an explicit False
            # is a statement of invisibility. An ABSENT key would otherwise flip
            # every dwell in the manifest to "dark" and red the whole run on a
            # parse gap, which is a verifier defect wearing a product defect's
            # clothes.
            covered = [(d.open_ut, d.close_ut) for d in in_cycle
                       if d.visible is not False]
            hidden_windows = [(d.open_ut, d.close_ut) for d in in_cycle
                              if d.visible is False
                              and d.coverage in RATIFIED_HIDDEN_COVERAGES]
            ratified_hidden += len(hidden_windows)
            covered.extend(hidden_windows)
            for gap_lo, gap_hi in _complement((lo, hi), covered):
                width = gap_hi - gap_lo
                resolution = _cover_resolution(ctx, unit, in_cycle,
                                               0.5 * (gap_lo + gap_hi))
                if resolution is None:
                    resolution_absent += 1
                    ctx.uneval(UNEVAL_COVER_RESOLUTION_ABSENT)
                    continue
                if width <= resolution:
                    below_resolution += 1
                    continue
                # CLOSED-interval overlap against the hull, singletons included:
                # a hull of zero width still brackets the instant it names.
                if any(gap_lo <= s.last_ut and s.first_ut <= gap_hi
                       for s in skips):
                    ctx.uneval(UNEVAL_COVER_RATIFIED_SKIP_HULL)
                    continue
                explained = any(gap_lo <= t <= gap_hi
                                for t in tails + idle_descent)
                if explained:
                    continue
                if skips_truncated:
                    ctx.uneval(UNEVAL_COVER_SKIPS_TRUNCATED)
                    continue
                unexplained += 1
                ctx.add(RULE_COVER, LEVEL_FAIL,
                        "%s cycle=%d" % (_unit_label(unit), cycle_index),
                        "unexplained dark window [%r, %r] (%.6g s) wider than the "
                        "local warp resolution %.6g s; recIds=%s"
                        % (gap_lo, gap_hi, width, resolution,
                           sorted(rec_ids)[:4]))
    ctx.metrics["coverUnexplainedGaps"] = unexplained
    ctx.metrics["coverBelowResolutionGaps"] = below_resolution
    ctx.metrics["coverResolutionAbsentGaps"] = resolution_absent
    # How much of `covered` came from RATIFIED hidden windows rather than from a
    # line that actually drew. A run where this dominates is a reading run, not a
    # clean one, and the number is what says so.
    ctx.metrics["coverRatifiedHiddenSpans"] = ratified_hidden


# --- RC-SEAM ----------------------------------------------------------------


def _rule_seam(ctx: _Ctx) -> None:
    """Transitions classified against the chain SEAM records and the numeric
    tangent / endpoint measurements."""
    snap = ctx.snap
    tracing = bool(snap.map_render_tracing_on)
    seams_by_signature: Dict[str, Dict[int, str]] = {}
    kinds_seen: Dict[str, int] = {}
    for build in snap.chain_builds:
        table = seams_by_signature.setdefault(build.signature, {})
        for seam in build.seams:
            if seam.boundary_index is not None:
                table[seam.boundary_index] = seam.kind
            if seam.kind:
                kinds_seen[seam.kind] = kinds_seen.get(seam.kind, 0) + 1
            ctx.unknown_token(
                "CHAIN_BUILD[%s].SEAM[%s]" % (build.signature, seam.boundary_index),
                "seam kind", seam.kind, SEAM_KINDS)
        for pi, phase in enumerate(build.phases):
            ctx.unknown_token(
                "CHAIN_BUILD[%s].PHASE[%d].kind" % (build.signature, pi),
                "phase kind", phase.kind,
                tuple(PHASE_KIND_NAMES) + tuple(SEGMENT_KIND_NAMES),
                "spine chains emit PhaseKind tokens %s; assembler-fallback "
                "chains emit SegmentKind names %s"
                % (sorted(PHASE_KIND_TOKENS), list(SEGMENT_KIND_NAMES)))
        ctx.unknown_token("CHAIN_BUILD[%s].provenance" % build.signature,
                          "chain provenance", build.provenance, CHAIN_PROVENANCES)
        ctx.unknown_token("CHAIN_BUILD[%s].seamSource" % build.signature,
                          "seamSource", build.seam_source, SEAM_SOURCES,
                          "schema v1 pins the single value above")
    ctx.metrics["seamKinds"] = kinds_seen

    # Numeric clause 1: rigid tangents.
    max_angle = NAN
    if snap.seam_tangents:
        for t in snap.seam_tangents:
            if _finite(t.angle_rad) and (math.isnan(max_angle)
                                         or t.angle_rad > max_angle):
                max_angle = t.angle_rad
            tol = t.tolerance_radians
            if not _finite(tol) or tol <= 0.0:
                tol = ratified_tolerance("CrossMemberSeamStitcher.TangentToleranceRadians")
            if not _finite(t.angle_rad):
                continue
            if t.angle_rad > tol:
                ctx.add(RULE_SEAM, LEVEL_FAIL,
                        "SEAM_TANGENT[pid=%s leg=%s ut=%r]"
                        % (t.pid, t.leg_index, t.ut),
                        "rigid-seam tangent angle %.9g rad exceeds tolerance "
                        "%.9g rad" % (t.angle_rad, tol))
            elif t.continuous is False:
                ctx.add(RULE_SEAM, LEVEL_FAIL,
                        "SEAM_TANGENT[pid=%s leg=%s ut=%r]"
                        % (t.pid, t.leg_index, t.ut),
                        "record self-inconsistent: continuous=False while the "
                        "angle %.9g rad is inside tolerance %.9g rad"
                        % (t.angle_rad, tol))
    elif not tracing:
        ctx.uneval(UNEVAL_SEAM_TRACING_OFF)
    ctx.metrics["maxTangentAngleRad"] = max_angle

    # Numeric clause 2: FlexibleSoi endpoint ratios.
    max_ratio = NAN
    if snap.seam_endpoints:
        for e in snap.seam_endpoints:
            if e.sampled is False or e.skip_reason:
                if "reaim" in (e.skip_reason or "").lower():
                    ctx.uneval(UNEVAL_REAIM_INSTANT_ABSENT)
                else:
                    ctx.uneval(UNEVAL_SEAM_SKIPPED)
                continue
            if not _finite(e.ratio):
                ctx.uneval(UNEVAL_SEAM_SKIPPED)
                continue
            if math.isnan(max_ratio) or e.ratio > max_ratio:
                max_ratio = e.ratio
            tol = e.ratio_tolerance
            if not _finite(tol) or tol < 1.0:
                tol = ratified_tolerance("SeamEndpointOracle.DefaultRatioTolerance")
            if e.ratio > tol:
                ctx.add(RULE_SEAM, LEVEL_FAIL,
                        "SEAM_ENDPOINT[pid=%s ut=%r %s->%s]"
                        % (e.pid, e.ut, e.from_body or "?", e.to_body or "?"),
                        "endpoint/SOI ratio %.9g exceeds tolerance %.9g at the "
                        "seam instant" % (e.ratio, tol))
    elif not tracing:
        ctx.uneval(UNEVAL_SEAM_TRACING_OFF)
    ctx.metrics["maxEndpointRatio"] = max_ratio

    # Classification clause: a body change may never be classified rigid
    # (PhaseSeamClassifier.Classify - body change WINS over rigid).
    reaim_windows = {(e.detail_s, int(e.detail_a))
                     for e in snap.clock_events
                     if e.kind == "reaim-window" and _finite(e.detail_a)}
    for tr in snap.transitions:
        table = seams_by_signature.get(tr.chain_signature, {})
        kind = table.get(tr.to_segment_index, "")
        body_changed = bool(tr.from_body and tr.to_body
                            and tr.from_body != tr.to_body)
        if body_changed and kind == "rigid":
            ctx.add(RULE_SEAM, LEVEL_FAIL,
                    "TRANSITION[pid=%s ut=%r]" % (tr.pid, tr.ut),
                    "body change %s->%s classified as a rigid seam; "
                    "PhaseSeamClassifier.Classify ranks a body change ABOVE rigid"
                    % (tr.from_body, tr.to_body))
        if body_changed and not snap.seam_endpoints and not tracing:
            ctx.uneval(UNEVAL_SEAM_TRACING_OFF)
    ctx.metrics["reaimWindows"] = len(reaim_windows)

    required = (ctx.block or {}).get("requireSeamKinds")
    if isinstance(required, (list, tuple)):
        for kind in required:
            if kinds_seen.get(str(kind), 0) <= 0:
                ctx.add(RULE_SEAM, LEVEL_FAIL if ctx.armed else LEVEL_INFO,
                        "requireSeamKinds", "spec requires seam kind %r and the "
                        "manifest's chain records carry none" % (kind,))


# --- RC-HOLD ----------------------------------------------------------------


def _joint_inputs(unit: PlanUnit) -> Optional[JointHoldInputs]:
    """The D8 joint primitives when the validity gate (SpanClock.cs:801-805)
    passes: destination rotation finite and > 0, budget > 0, hold instant known."""
    t_rot = unit.destination_body_rotation_period_seconds
    budget = unit.arrival_joint_max_whole_hold_periods or 0
    if not _finite(t_rot) or t_rot <= 0.0 or budget <= 0:
        return None
    if not _finite(unit.arrival_hold_at_ut):
        return None
    if not _finite(unit.arrival_joint_secondary_tolerance_seconds):
        return None
    return JointHoldInputs(
        t_rot=t_rot,
        tolerance_seconds=unit.arrival_joint_secondary_tolerance_seconds,
        entry_offset0=joint_entry_offset(unit.phase_anchor_ut,
                                         unit.arrival_hold_at_ut,
                                         unit.span_start_ut, unit.loiter_cuts),
        max_whole_periods=int(budget))


def _hold_run_ordinal(event: ClockEvent) -> Optional[int]:
    """The hold run's 0-based ORDINAL within its ``(ownerIndex, cycleIndex)``.

    Review-pass convention (both events): ``detailA`` is the ordinal, NOT a
    repeat of the cycle index. A cycle that stalls twice emits ordinal 0 and
    ordinal 1, so "one pair per (owner, cycle)" is no longer true and pairing on
    the cycle alone would silently match a release to the wrong engage.

    ``None`` when the slot is absent, which pairs only with another absent
    ordinal - a manifest that carries no ordinal at all still resolves its single
    pair per cycle."""
    if not _finite(event.detail_a):
        return None
    return int(round(event.detail_a))


def _hold_observed_tolerance(ctx: "_Ctx", unit: PlanUnit, ut: float) -> float:
    """The leg-2 comparison band at one instant: ``max(2 s, 2 * local maxUtStep)``.

    Both halves are load-bearing. The step factor is the recorder's own accuracy
    (the run is bracketed by the frames either side of it, so one step of error in
    each direction); the floor keeps a fine-grained 1x lane from being held to a
    tolerance tighter than its sampling noise."""
    step = _local_max_ut_step(ctx, unit, ut)
    scaled = 0.0 if not _finite(step) else HOLD_OBSERVED_TOLERANCE_STEP_FACTOR * step
    return max(HOLD_OBSERVED_TOLERANCE_FLOOR_SECONDS, scaled)


def _rule_hold(ctx: _Ctx) -> None:
    """Leg 1: the plan's own hold value must be a normalised realignment result
    and the per-cycle recomputation must respect the clock clamp. Leg 2: the
    OBSERVED hold, against that same recomputation.

    Leg 2 became evaluable with the schema v1.1 hold pair (decision 2). The pair is
    the important part: ``hold-release.detailC`` is a MEASUREMENT of how long the
    render clock stood still, derived from the frame stream, NOT a second evaluation
    of the hold formula - so comparing it against the Python recomputation actually
    tests the product rather than testing arithmetic against itself. Everything the
    pair cannot answer stays defined-unevaluable:

    - no pair at all for a unit that plans a hold: ``hold-observed-evidence-absent``
      (the hold never engaged, or every hold was shorter than the local warp step);
    - an engage with no release: ``hold-release-absent-below-resolution`` - the run
      was still open at export, or the release frame was warped over.

    "Frozen dwell" is still NOT used as a substitute observation: dwell truth
    positions are BODY-RELATIVE (H18), so a landed or surface-parked ghost is
    stationary in exactly the same way a held one is."""
    snap = ctx.snap
    hold_durations: List[float] = []
    observed_holds: List[float] = []
    unplanned_events = 0
    for unit in snap.units:
        w0 = unit.arrival_hold_seconds
        t_align = unit.arrival_align_period_seconds
        if not _finite(w0) or w0 <= 0.0:
            # No PLANNED hold on this unit. Wave-1 simply skipped it, which left
            # hold pairs observed on such a unit claimed by no rule at all - the
            # one shape the defined-unevaluable doctrine refuses, because "no
            # finding" then means both "nothing happened" and "something happened
            # and nobody looked". The clock DID stall on a unit whose plan has no
            # hold to explain it: report it, and count it.
            unplanned_events += _hold_observed_without_plan(ctx, unit)
            continue
        hold_durations.append(w0)
        if not _finite(t_align) or t_align <= 0.0:
            ctx.uneval(UNEVAL_HOLD_PRIMITIVES_ABSENT)
            continue
        if w0 >= t_align:
            ctx.add(RULE_HOLD, LEVEL_FAIL,
                    "%s.arrivalHoldSeconds" % _unit_label(unit),
                    "leg 1 (solver drift): W_0 %.9g is not normalised into "
                    "[0, alignPeriod=%.9g); ArrivalAlignHold returns a modulo "
                    "result by construction" % (w0, t_align))
        joint = _joint_inputs(unit)
        compressed = compressed_span_seconds(unit.span_start_ut, unit.span_end_ut,
                                             unit.loiter_cuts)
        windows = ctx.cycle_windows(unit.owner_index)
        if not windows:
            ctx.uneval(UNEVAL_NO_CYCLE_BOUNDARIES)
        for cycle_index, _lo, _hi in windows:
            raw = resolve_per_loop_arrival_hold(w0, cycle_index,
                                                unit.cadence_seconds, t_align,
                                                joint)
            clamped = clamp_hold_to_cycle(raw, compressed, unit.cadence_seconds)
            if not _finite(clamped):
                # Mirrors leg 2's skip: a non-finite recomputation is a clause
                # the plan primitives could not answer, and an UNCOUNTED skip is
                # a silent pass - the one shape the defined-unevaluable doctrine
                # exists to refuse.
                ctx.uneval(UNEVAL_HOLD_PRIMITIVES_ABSENT)
                continue
            if clamped < 0.0:
                ctx.add(RULE_HOLD, LEVEL_FAIL,
                        "%s cycle=%d" % (_unit_label(unit), cycle_index),
                        "leg 1 (solver drift): recomputed hold %.9g is negative"
                        % clamped)
            if _finite(unit.cadence_seconds) and unit.cadence_seconds > 0.0 \
                    and compressed + clamped > unit.cadence_seconds + \
                    CLOCK_RECOMPUTE_TOLERANCE_SECONDS:
                ctx.add(RULE_HOLD, LEVEL_FAIL,
                        "%s cycle=%d" % (_unit_label(unit), cycle_index),
                        "leg 1 (solver drift): compressed span %.9g + hold %.9g "
                        "exceeds cadence %.9g after the clock clamp"
                        % (compressed, clamped, unit.cadence_seconds))
        _hold_leg_2(ctx, unit, w0, t_align, joint, compressed, observed_holds)

    # The InteriorGap bound: the "held forever" defect the live instruments
    # cannot see. The bound is the unit's OWN cadence - an interior-gap hold that
    # outlives a whole loop cycle is not a seam gap any more.
    interior_max = NAN
    cadence_by_rec: Dict[str, float] = {}
    for unit in snap.units:
        for m in unit.members:
            if m.rec_id and _finite(unit.cadence_seconds) and unit.cadence_seconds > 0:
                cadence_by_rec[m.rec_id] = unit.cadence_seconds
    for d in snap.dwells:
        if d.coverage != "InInteriorGap":
            continue
        dur = d.duration
        if not _finite(dur):
            continue
        if math.isnan(interior_max) or dur > interior_max:
            interior_max = dur
        bound = cadence_by_rec.get(d.rec_id)
        if bound is None:
            ctx.uneval(UNEVAL_HOLD_PRIMITIVES_ABSENT)
            continue
        if dur > bound:
            ctx.add(RULE_HOLD, LEVEL_FAIL,
                    "DWELL[pid=%s recId=%s openUT=%r]" % (d.pid, d.rec_id, d.open_ut),
                    "InteriorGap hold ran %.9g s, longer than the unit's own "
                    "cadence %.9g s - a hold that outlives a full loop cycle is "
                    "the held-forever defect, not a seam gap" % (dur, bound))
    ctx.metrics["maxInteriorGapSeconds"] = interior_max
    ctx.metrics["planHoldSeconds"] = hold_durations
    ctx.metrics["observedHoldSeconds"] = observed_holds
    ctx.metrics["holdEventsWithoutPlan"] = unplanned_events


def _hold_observed_without_plan(ctx: _Ctx, unit: PlanUnit) -> int:
    """Hold runs observed on a unit whose plan carries NO hold.

    Returns the number of hold EVENTS reported (engages plus releases).

    A unit with ``arrivalHoldSeconds <= 0`` and no launch hold has no planned
    reason for its render clock to stand still, so a detected run is either a
    product defect (something froze the clock that should not have) or a detector
    artifact. The verifier cannot tell those apart from the manifest alone -
    there is no prediction to compare against - so this is a WARN plus a counted
    unevaluable, never a FAIL and never a silent skip.

    A LAUNCH hold is a planned stall the plan does not express as a number, so a
    unit carrying ``launchHoldEngaged`` is excluded: its runs are explained."""
    if unit.launch_hold_engaged:
        return 0
    runs = [e for e in ctx.snap.clock_events
            if e.kind in (CLOCK_HOLD_ENGAGE, CLOCK_HOLD_RELEASE)
            and e.owner_index == unit.owner_index]
    if not runs:
        return 0
    engages = sum(1 for e in runs if e.kind == CLOCK_HOLD_ENGAGE)
    ctx.add(RULE_HOLD, LEVEL_WARN, _unit_label(unit),
            "observed clock stall on a unit with no planned hold "
            "(arrivalHoldSeconds=%r, launchHoldEngaged=%r): %d hold-engage / "
            "%d hold-release event(s) the plan cannot account for"
            % (unit.arrival_hold_seconds, unit.launch_hold_engaged,
               engages, len(runs) - engages))
    ctx.uneval(UNEVAL_HOLD_OBSERVED_WITHOUT_PLAN)
    return len(runs)


def _hold_leg_2(ctx: _Ctx, unit: PlanUnit, w0: float, t_align: float,
                joint: Optional[JointHoldInputs], compressed: float,
                observed_holds: List[float]) -> None:
    """One unit's observed hold run(s) vs the per-cycle recomputation.

    PAIRING KEY (review-pass convention): ``(ownerIndex, cycleIndex, detailA)``,
    where ``detailA`` is the run's 0-based ORDINAL within its (owner, cycle) and
    ``cycleIndex`` is the cycle at ENGAGE on BOTH events (a run straddling a
    rollover keeps one identity). Multiple pairs per cycle are legal and expected:
    the detector accumulates stall time, so a cycle that stalls twice emits
    ordinal 0 and ordinal 1. Pairing on the cycle alone would match the second
    release to the first engage and compare the wrong two instants.

    ATTRIBUTION, and why the numeric comparison is not simply per-release: the
    plan predicts ONE arrival hold per cycle, inserted once at one compressed
    position. Several stationary runs in a cycle are therefore not several
    arrival holds - the extras are a loiter stall, a Director hold across an
    interior gap, a stall resumed after a warp change - and the plan primitives
    say nothing about them. So exactly one run per cycle is compared (the
    LONGEST, the only candidate the single prediction can be about) and every
    other run is counted ``hold-run-not-attributable-to-planned-hold``.

    Picking the longest cannot hide a defect, which is the direction that
    matters: if the clock froze longer than planned the longest run is the one
    compared and it reds; if the planned hold never happened, the longest of the
    short runs is compared against the large expected value and it reds. What it
    refuses to do is red a LEGAL multi-stall cycle for carrying a second run the
    plan never described. The STRUCTURAL clauses (ordinal shape, missing engage,
    engage-after-release, negative duration) run on EVERY release regardless -
    they are defects in the record, not questions about attribution."""
    snap = ctx.snap
    engages = [e for e in snap.clock_events
               if e.kind == CLOCK_HOLD_ENGAGE and e.owner_index == unit.owner_index]
    releases = [e for e in snap.clock_events
                if e.kind == CLOCK_HOLD_RELEASE and e.owner_index == unit.owner_index]
    if not releases:
        # An engage with no release is a DIFFERENT statement from no hold at all:
        # the run was still open at export, or its release frame was warped over.
        ctx.uneval(UNEVAL_HOLD_RELEASE_ABSENT if engages
                   else UNEVAL_HOLD_EVIDENCE_ABSENT)
        return
    engage_by_run = {}
    for eng in engages:
        engage_by_run.setdefault((eng.cycle_index, _hold_run_ordinal(eng)), eng)
    # The one comparable run per cycle: longest measured stall wins, ties broken
    # by the lowest ordinal so the choice is deterministic across runs.
    compared: Dict[Any, Any] = {}
    for e in releases:
        if e.cycle_index is None or not _finite(e.detail_c):
            continue
        best = compared.get(e.cycle_index)
        if best is None:
            compared[e.cycle_index] = e
            continue
        ord_e = _hold_run_ordinal(e)
        ord_b = _hold_run_ordinal(best)
        if (e.detail_c, -(ord_e if ord_e is not None else 0)) > \
                (best.detail_c, -(ord_b if ord_b is not None else 0)):
            compared[e.cycle_index] = e
    for e in releases:
        ordinal = _hold_run_ordinal(e)
        label = "%s hold-release cycle=%s run=%s" % (
            _unit_label(unit), e.cycle_index,
            "?" if ordinal is None else ordinal)
        if e.cycle_index is None or not _finite(e.detail_c):
            ctx.uneval(UNEVAL_HOLD_EVIDENCE_ABSENT)
            continue
        # The pinned detail convention: detailA is a RUN ORDINAL, 0-based and
        # non-negative. It used to be pinned as a repeat of the cycle index; the
        # stall-accumulation detector replaced that, so the shape check moved
        # with it rather than being dropped.
        # `>=` on the integrality half deliberately: a value exactly half a unit
        # from an integer rounds either way, so it names no run at all.
        if _finite(e.detail_a) and (
                e.detail_a < -INDEX_COMPARE_EPSILON
                or abs(e.detail_a - round(e.detail_a)) >= INDEX_COMPARE_EPSILON):
            ctx.add(RULE_UNKNOWN, LEVEL_FAIL, label,
                    "hold-release detailA %r is not a 0-based whole run ordinal; "
                    "the schema pins detailA as the run's ordinal within its "
                    "(ownerIndex, cycleIndex)" % (e.detail_a,))
        engage = engage_by_run.get((e.cycle_index, ordinal))
        if engage is None:
            ctx.add(RULE_HOLD, LEVEL_FAIL, label,
                    "hold-release with no hold-engage for the same "
                    "(owner, cycle, run ordinal); the pair is emitted by ONE run "
                    "detector, so a release alone means the engage was lost, not "
                    "that a hold appeared from nowhere")
        elif _finite(engage.ut) and _finite(e.ut) and engage.ut > e.ut:
            ctx.add(RULE_HOLD, LEVEL_FAIL, label,
                    "hold-engage at %r is AFTER its release at %r"
                    % (engage.ut, e.ut))
        observed = e.detail_c
        observed_holds.append(observed)
        if observed < 0.0:
            ctx.add(RULE_HOLD, LEVEL_FAIL, label,
                    "observed held duration %.9g s is negative" % observed)
        if compared.get(e.cycle_index) is not e:
            # A legal extra stall in this cycle. Counted, never compared against
            # a prediction that was about a different run.
            ctx.uneval(UNEVAL_HOLD_RUN_UNATTRIBUTED)
            continue
        raw = resolve_per_loop_arrival_hold(w0, int(e.cycle_index),
                                            unit.cadence_seconds, t_align, joint)
        expected = clamp_hold_to_cycle(raw, compressed, unit.cadence_seconds)
        if not _finite(expected):
            ctx.uneval(UNEVAL_HOLD_PRIMITIVES_ABSENT)
            continue
        tolerance = _hold_observed_tolerance(ctx, unit, e.ut)
        if abs(observed - expected) > tolerance:
            ctx.add(RULE_HOLD, LEVEL_FAIL, label,
                    "leg 2 (render drift): the render clock stood still for %.9g s "
                    "but the per-cycle hold recomputed from the plan primitives is "
                    "%.9g s (delta %.9g s, tolerance %.9g s = max(%.9g, %.9g x the "
                    "local warp step))"
                    % (observed, expected, observed - expected, tolerance,
                       HOLD_OBSERVED_TOLERANCE_FLOOR_SECONDS,
                       HOLD_OBSERVED_TOLERANCE_STEP_FACTOR))


# --- RC-CUT -----------------------------------------------------------------


def _rule_cut(ctx: _Ctx) -> None:
    """Every loiter cut is an exact whole multiple of the run period, positive,
    inside the span, and non-overlapping with its siblings."""
    snap = ctx.snap
    ratios: List[float] = []
    for unit in snap.units:
        period = unit.loiter_period_seconds
        cuts = list(unit.loiter_cuts)
        for i, cut in enumerate(cuts):
            label = "%s.LOITER_CUT[%d]" % (_unit_label(unit), i)
            if not _finite(cut.length_seconds) or not _finite(cut.start_ut):
                ctx.add(RULE_CUT, LEVEL_FAIL, label,
                        "cut carries a non-finite startUT %r / lengthSeconds %r"
                        % (cut.start_ut, cut.length_seconds))
                continue
            if cut.length_seconds <= 0.0:
                ctx.add(RULE_CUT, LEVEL_FAIL, label,
                        "cut length %.9g is not positive; ComputeCuts emits a cut "
                        "only for a run with wholeRevs > keepRevs"
                        % cut.length_seconds)
            whole = is_whole_multiple(cut.length_seconds, period)
            if whole is None:
                ctx.uneval(UNEVAL_CUT_PERIOD_ABSENT)
            else:
                ratio = whole_multiple_ratio(cut.length_seconds, period)
                if ratio is not None:
                    ratios.append(ratio)
                if not whole:
                    ctx.add(RULE_CUT, LEVEL_FAIL, label,
                            "cut length %.9g is %.9g run periods (%.9g s), not a "
                            "whole multiple; ComputeCuts builds "
                            "(wholeRevs - keepRevs) * period, so a partial trim "
                            "here is the flexible-SOI-edge mechanism in the wrong "
                            "place" % (cut.length_seconds, ratio, period))
            if _finite(unit.span_start_ut) and _finite(unit.span_end_ut):
                if cut.start_ut < unit.span_start_ut or \
                        cut.start_ut > unit.span_end_ut:
                    ctx.add(RULE_CUT, LEVEL_FAIL, label,
                            "cut starts at %r, outside the unit span [%r, %r]"
                            % (cut.start_ut, unit.span_start_ut, unit.span_end_ut))
        ordered = sorted((c for c in cuts
                          if _finite(c.start_ut) and _finite(c.length_seconds)),
                         key=lambda c: c.start_ut)
        for a, b in zip(ordered, ordered[1:]):
            if a.start_ut + a.length_seconds > b.start_ut:
                ctx.add(RULE_CUT, LEVEL_FAIL, _unit_label(unit),
                        "loiter cuts overlap: [%r, +%r] runs into [%r, +%r]; "
                        "DetectRuns emits maximal CONTIGUOUS non-overlapping runs"
                        % (a.start_ut, a.length_seconds, b.start_ut,
                           b.length_seconds))
        if cuts and not _check_cut_containment(ctx, unit, cuts):
            # "No dwell samples inside a cut" needs the RECORDED clock for each
            # dwell. Schema v1.1 stamps it (openLoopUT / closeLoopUT), but only for
            # a dwell whose member mapped to a live unit - without that the dwell's
            # LIVE openUT/closeUT cannot answer the question at all.
            ctx.uneval(UNEVAL_CUT_CONTAINMENT)
    ctx.metrics["cutWholeRatios"] = ratios


def _check_cut_containment(ctx: _Ctx, unit: PlanUnit,
                           cuts: Sequence[LoiterCut]) -> bool:
    """NO DWELL SAMPLE may land INSIDE a compressed loiter cut.

    COMPRESS SEMANTICS, stated because the direction is easy to invert: a cut is an
    interval on the RECORDED clock that the span clock REMOVES from playback. A
    rendered frame whose recorded instant falls in ``[startUT, startUT + length)``
    therefore showed a stretch of recording the cut said would never be shown - that
    is the violation.

    The test is on the two stamped SAMPLES (the dwell's open and close recorded
    instants), not on the interval between them, and the difference is load-bearing:
    a cut is a DISCONTINUITY in the recorded clock, not a break in the dwell key, so
    one dwell legitimately straddles a cut - opening before it and closing after it -
    while never rendering a single instant inside it. Flagging the straddle would red
    the compressor working exactly as designed. What the interval between two samples
    did is unobserved either way; the manifest carries two instants per dwell, so two
    instants is what this may honestly assert.

    Returns False when no dwell of this unit carries the recorded-clock stamps, so
    the caller can record the clause as defined-unevaluable rather than as a pass.
    """
    stamped = [d for d in ctx.dwells_for_unit(unit)
               if _finite(d.open_loop_ut) and _finite(d.close_loop_ut)]
    if not stamped:
        return False
    for d in stamped:
        # The stamp is the recorder's latest per-unit sample, accurate to one frame
        # step; a sample within that of a cut edge is the stamp's own slop, not
        # evidence.
        margin = (CUT_CONTAINMENT_MARGIN_STEP_FACTOR * d.max_ut_step
                  if _finite(d.max_ut_step) and d.max_ut_step > 0.0 else 0.0)
        for i, cut in enumerate(cuts):
            if not (_finite(cut.start_ut) and _finite(cut.length_seconds)
                    and cut.length_seconds > 0.0):
                continue
            cut_end = cut.start_ut + cut.length_seconds
            for label, sample in (("openLoopUT", d.open_loop_ut),
                                  ("closeLoopUT", d.close_loop_ut)):
                depth = min(sample - cut.start_ut, cut_end - sample)
                if depth <= margin:
                    continue
                ctx.add(RULE_CUT, LEVEL_FAIL,
                        "%s.LOITER_CUT[%d]" % (_unit_label(unit), i),
                        "dwell [pid=%s recId=%s] %s = %r lies %.9g s inside the cut "
                        "[%r, %r) the span clock compressed away, so a rendered "
                        "frame showed recording the cut removed (margin %.9g s = "
                        "the dwell's own maxUtStep)"
                        % (d.pid, d.rec_id, label, sample, depth, cut.start_ut,
                           cut_end, margin))
    return True


# --- RC-DESCENT -------------------------------------------------------------


def _rule_descent(ctx: _Ctx) -> None:
    """Trigger congruence (leg 1 recomputation + the exported residual), and at
    most one descent member rendering at any instant."""
    snap = ctx.snap
    # A descent-phase event names its DESCENT MEMBER index in ownerIndex (shipped
    # writer convention), so the unit is resolved through descentMemberIndices.
    unit_by_descent_member: Dict[int, PlanUnit] = {}
    for u in snap.units:
        for idx in u.descent_member_indices:
            unit_by_descent_member.setdefault(idx, u)
    residuals: List[float] = []
    for e in snap.clock_events:
        if e.kind != "descent-phase":
            continue
        ctx.unknown_token("CLOCK_EVENT[descent-phase ut=%r]" % e.ut,
                          "descent phase token", e.detail_s, DESCENT_PHASES)
        if _finite(e.detail_c):
            residuals.append(e.detail_c)
            if e.detail_c > DESCENT_ROTATION_RESIDUAL_TOLERANCE_DEG:
                ctx.add(RULE_DESCENT, LEVEL_FAIL,
                        "CLOCK_EVENT[descent-phase ut=%r cycle=%s]"
                        % (e.ut, e.cycle_index),
                        "site rotation residual %.9g deg exceeds %.9g deg; the "
                        "trigger is congruent to the recorded deorbit phase BY "
                        "CONSTRUCTION, so a measurable residual is a defect"
                        % (e.detail_c, DESCENT_ROTATION_RESIDUAL_TOLERANCE_DEG))
        unit = unit_by_descent_member.get(e.owner_index)
        if unit is None or e.cycle_index is None:
            ctx.uneval(UNEVAL_DESCENT_PRIMITIVES_ABSENT)
            continue
        t_rot = unit.destination_body_rotation_period_seconds
        needed = (unit.phase_anchor_ut, unit.cadence_seconds, unit.span_start_ut,
                  unit.recorded_deorbit_ut, t_rot, unit.capture_shift_seconds)
        if not all(_finite(v) for v in needed) or t_rot <= 0.0:
            ctx.uneval(UNEVAL_DESCENT_PRIMITIVES_ABSENT)
            continue
        _conic, entry_ut, trigger_ut = compute_descent_timing(
            int(e.cycle_index), unit.phase_anchor_ut, unit.cadence_seconds,
            unit.span_start_ut, unit.recorded_deorbit_ut, t_rot,
            unit.capture_shift_seconds, unit.loiter_cuts)
        if _finite(e.detail_a) and _finite(trigger_ut):
            if abs(e.detail_a - trigger_ut) > CLOCK_RECOMPUTE_TOLERANCE_SECONDS:
                ctx.add(RULE_DESCENT, LEVEL_FAIL,
                        "CLOCK_EVENT[descent-phase cycle=%s]" % e.cycle_index,
                        "leg 2 (render drift): observed triggerUT %r != the value "
                        "%r recomputed from the plan primitives (delta %.9g s)"
                        % (e.detail_a, trigger_ut, e.detail_a - trigger_ut))
        if _finite(e.detail_b) and _finite(entry_ut):
            if abs(e.detail_b - entry_ut) > CLOCK_RECOMPUTE_TOLERANCE_SECONDS:
                ctx.add(RULE_DESCENT, LEVEL_FAIL,
                        "CLOCK_EVENT[descent-phase cycle=%s]" % e.cycle_index,
                        "leg 2 (render drift): observed entryUT %r != the value %r "
                        "recomputed from the plan primitives (delta %.9g s)"
                        % (e.detail_b, entry_ut, e.detail_b - entry_ut))
    ctx.metrics["descentResidualsDeg"] = residuals
    _descent_head_clauses(ctx, unit_by_descent_member)

    for unit in snap.units:
        if not unit.descent_member_indices:
            continue
        members = [d for d in snap.dwells
                   if d.committed_index in set(unit.descent_member_indices)
                   and d.visible is not False
                   and _finite(d.open_ut) and _finite(d.close_ut)]
        members.sort(key=lambda d: d.open_ut)
        # RUNNING FURTHEST-CLOSE per member, not a consecutive-pair compare.
        # Sorted by open UT, the overlapping pair need not be adjacent: one long
        # dwell, then a short dwell of the SAME member nested inside it, then a
        # dwell of a DIFFERENT member that overlaps the long one - a pairwise walk
        # only ever compares the short one and passes the real concurrency.
        furthest_by_member: Dict[Any, Dwell] = {}
        for d in members:
            for idx in sorted(furthest_by_member, key=str):
                prev = furthest_by_member[idx]
                if idx == d.committed_index or d.open_ut >= prev.close_ut:
                    continue
                ctx.add(RULE_DESCENT, LEVEL_FAIL, _unit_label(unit),
                        "two descent members render concurrently: member %s "
                        "[%r, %r] overlaps member %s [%r, %r]"
                        % (prev.committed_index, prev.open_ut, prev.close_ut,
                           d.committed_index, d.open_ut, d.close_ut))
                break
            cur = furthest_by_member.get(d.committed_index)
            if cur is None or d.close_ut > cur.close_ut:
                furthest_by_member[d.committed_index] = d


def _descent_head_clauses(ctx: _Ctx,
                          unit_by_descent_member: Dict[int, PlanUnit]) -> None:
    """The two head clauses schema v1.1 made evaluable (decision 3, ``detailD``).

    1. The head is NEVER before the recording's own deorbit instant. The re-anchored
       head is ``recordedDeorbitUT + (currentUT - triggerUT)`` and the trigger gates
       it, so a head below the deorbit means the clip was entered from behind.
    2. The head is MONOTONE within one (member, cycle) run. Deliberately not across
       cycles: every cycle re-anchors the clip at the deorbit instant, so a global
       monotonicity check would red on the loop working correctly.

    ``detailD`` is absent on Inert / Loiter / Done events by design (there IS no head
    outside the Descent phase), so only a Descent event without one is counted as
    defined-unevaluable."""
    snap = ctx.snap
    runs: Dict[Tuple[Any, Any], List[Tuple[float, float]]] = {}
    missing = 0
    for e in snap.clock_events:
        if e.kind != "descent-phase":
            continue
        if not _finite(e.detail_d):
            if e.detail_s == "Descent":
                missing += 1
            continue
        unit = unit_by_descent_member.get(e.owner_index)
        if unit is not None and _finite(unit.recorded_deorbit_ut) \
                and e.detail_d < unit.recorded_deorbit_ut - CLOCK_RECOMPUTE_TOLERANCE_SECONDS:
            ctx.add(RULE_DESCENT, LEVEL_FAIL,
                    "CLOCK_EVENT[descent-phase member=%s cycle=%s]"
                    % (e.owner_index, e.cycle_index),
                    "descent head %r is BEFORE the recording's own deorbit instant "
                    "%r; the re-anchored head starts AT the deorbit and runs "
                    "forward, so a head below it is a clip entered from behind"
                    % (e.detail_d, unit.recorded_deorbit_ut))
        runs.setdefault((e.owner_index, e.cycle_index), []).append((e.ut, e.detail_d))
    if missing:
        ctx.uneval(UNEVAL_DESCENT_HEAD_ABSENT, missing)
    for (member, cycle), samples in sorted(
            runs.items(), key=lambda kv: (str(kv[0][0]), str(kv[0][1]))):
        ordered = sorted((s for s in samples if _finite(s[0])), key=lambda s: s[0])
        for (ut_a, head_a), (ut_b, head_b) in zip(ordered, ordered[1:]):
            if head_b < head_a - CLOCK_RECOMPUTE_TOLERANCE_SECONDS:
                ctx.add(RULE_DESCENT, LEVEL_FAIL,
                        "CLOCK_EVENT[descent-phase member=%s cycle=%s]"
                        % (member, cycle),
                        "descent head went BACKWARDS inside one cycle: %r at ut %r "
                        "then %r at ut %r; ComputeDescentEffectiveHeadUT is "
                        "strictly forward in currentUT by construction, which is "
                        "what keeps it freeze-free against the insert-only "
                        "arrival hold" % (head_a, ut_a, head_b, ut_b))


# --- RC-CYCLE ---------------------------------------------------------------


def _warp_totals(dwells: Sequence[Dwell]) -> Dict[str, int]:
    """Per-bucket frame totals over a dwell set. ONE definition, used by the
    dominant-bucket pick and by both halves of RC-WARP, so the three can never
    disagree about what "frames in bucket b" means."""
    totals = {b: 0 for b in WARP_BUCKETS}
    for d in dwells:
        for b in WARP_BUCKETS:
            totals[b] += int(d.warp.get(b, 0) or 0)
    return totals


def _uts_inside(dwells: Sequence[Dwell], uts: Sequence[float]) -> int:
    """How many of ``uts`` land inside the CLOSED span of any of ``dwells``.

    Both RC-WARP halves ask the same question of two different instant lists
    (transition instants, hold-event instants); sharing the predicate is what
    keeps "was this instant traversed above 1x" one definition rather than two
    copies drifting apart."""
    spans = [(d.open_ut, d.close_ut) for d in dwells
             if _finite(d.open_ut) and _finite(d.close_ut)]
    return sum(1 for ut in uts
               if any(lo <= ut <= hi for lo, hi in spans))


def _dominant_warp_bucket(dwells: Sequence[Dwell]) -> str:
    totals = _warp_totals(dwells)
    best = max(WARP_BUCKETS, key=lambda b: totals[b])
    return best if totals[best] > 0 else ""


def _rule_cycle(ctx: _Ctx) -> None:
    """Same-warp-bucket cycles are isomorphic BY ROLE, rollovers are monotone,
    and a boundary-overlap secondary appears only when the raw advance actually
    exceeds the capped one."""
    snap = ctx.snap
    for unit in snap.units:
        windows = ctx.cycle_windows(unit.owner_index)
        if len(windows) < 2:
            ctx.uneval(UNEVAL_NO_CYCLE_BOUNDARIES)
            continue
        dwells = ctx.dwells_for_unit(unit)
        roles: Dict[int, Tuple[Tuple[Any, str], ...]] = {}
        buckets: Dict[int, str] = {}
        for cycle_index, lo, hi in windows:
            # MIDPOINT membership, not open_ut. A dwell that opens within one
            # frame of a rollover lands in either cycle depending on which side
            # of the boundary its first sample happened to fall, and that jitter
            # changes the ROLE STRUCTURE the isomorphism clause compares - so two
            # identical cycles could red purely on sampling phase. A dwell's
            # midpoint is where it actually spent its time and moves only when
            # the dwell really straddles the boundary.
            inside = [d for d in dwells
                      if _finite(d.open_ut) and _finite(d.close_ut)
                      and lo <= 0.5 * (d.open_ut + d.close_ut) < hi]
            roles[cycle_index] = tuple(sorted(
                (d.committed_index, d.phase_kind) for d in inside))
            buckets[cycle_index] = _dominant_warp_bucket(inside)
        ordered = [w[0] for w in windows]
        for a, b in zip(ordered, ordered[1:]):
            if roles[a] == roles[b]:
                continue
            if buckets[a] and buckets[a] == buckets[b]:
                ctx.add(RULE_CYCLE, LEVEL_FAIL, _unit_label(unit),
                        "cycles %d and %d share warp bucket %s but their role "
                        "structures differ: %r vs %r (roles are "
                        "(memberIndex, phaseKind); chain and segment ids rebuild "
                        "per window BY DESIGN and are never compared)"
                        % (a, b, buckets[a], roles[a], roles[b]))
            else:
                ctx.add(RULE_CYCLE, LEVEL_INFO, _unit_label(unit),
                        "cycles %d (%s) and %d (%s) differ in role structure "
                        "across warp buckets - report-only: a short dwell "
                        "legitimately vanishes when a warp step lands inside it"
                        % (a, buckets[a] or "?", b, buckets[b] or "?"))
        # Rollover monotonicity + the per-cycle residual trend (RC-QUAL input).
        residuals = []
        for cycle_index, lo, hi in windows:
            if hi <= lo:
                ctx.add(RULE_CYCLE, LEVEL_FAIL, _unit_label(unit),
                        "cycle %d rollover window [%r, %r] is not forward in UT"
                        % (cycle_index, lo, hi))
            if _finite(unit.cadence_seconds) and unit.cadence_seconds > 0:
                residuals.append((hi - lo) - unit.cadence_seconds)
        ctx.metrics.setdefault("cycleLengthResidualsSeconds", []).extend(residuals)

    units_by_owner = {u.owner_index: u for u in snap.units
                      if u.owner_index is not None}
    for e in snap.clock_events:
        if e.kind != "boundary-overlap-secondary":
            continue
        unit = units_by_owner.get(e.owner_index)
        if unit is None or e.cycle_index is None:
            ctx.uneval(UNEVAL_NO_CYCLE_BOUNDARIES)
            continue
        t_sid = unit.launch_body_rotation_period_seconds
        if not _finite(t_sid) or t_sid <= 0.0 or not _finite(unit.cadence_seconds):
            ctx.uneval(UNEVAL_HOLD_PRIMITIVES_ABSENT)
            continue
        # PINNED CONVENTION (schema v1.1, decision 6): `cycleIndex` is the PRIMARY
        # cycle index N - the continuing instance the camera follows - and `detailA`
        # is the SECONDARY, N+1, the early-launching concurrent ghost. The gate the
        # recorder observed is `advNext > cappedAdvNext` evaluated at N+1
        # (SpanClock.cs computes both with `cycleIndex + 1`), so the recomputation
        # must run on the SECONDARY window; running it on the primary would test a
        # window nobody gated on. detailA is authoritative when present; N+1 is the
        # documented fallback for a manifest that predates the pin.
        primary = int(e.cycle_index)
        if _finite(e.detail_a):
            window = int(e.detail_a)
            if abs(e.detail_a - (primary + 1)) > INDEX_COMPARE_EPSILON:
                ctx.add(RULE_CYCLE, LEVEL_FAIL,
                        "CLOCK_EVENT[boundary-overlap-secondary cycle=%d]" % primary,
                        "secondary cycle index %r is not the primary's successor "
                        "%d; SpanLoopFrame.SecondaryCycleIndex is cycleIndex + 1 by "
                        "construction" % (e.detail_a, primary + 1))
        else:
            window = primary + 1
        raw = per_loop_launch_advance(unit.phase_anchor_ut, unit.span_start_ut,
                                      window, unit.cadence_seconds, t_sid)
        capped = capped_launch_advance(
            unit.phase_anchor_ut, unit.span_start_ut, unit.span_end_ut,
            unit.cadence_seconds, window, t_sid, unit.loiter_cuts,
            unit.arrival_hold_seconds, unit.arrival_align_period_seconds,
            _joint_inputs(unit))
        if not (raw > capped + ADVANCE_COMPARE_EPSILON):
            ctx.add(RULE_CYCLE, LEVEL_FAIL,
                    "CLOCK_EVENT[boundary-overlap-secondary cycle=%d]" % primary,
                    "secondary emitted while the raw launch advance %.9g does NOT "
                    "exceed the capped advance %.9g; the secondary is GATED on "
                    "raw > capped, not blanket-uncapped" % (raw, capped))


# --- RC-ROUTE ---------------------------------------------------------------


def leg_within_dock_clip(leg_start_ut: float, dock_clip_ut: float) -> bool:
    """``RouteTrajectoryLineRenderer.LegWithinDockClip`` (Display/
    RouteTrajectoryLineRenderer.cs:178-182), transcribed EXACTLY.

    A zero or absent clip means NO clip. The predicate tests the leg's START
    only - the end UT is deliberately not consulted, and a Python re-derivation
    that "fixed" that would stop measuring the product."""
    if not _finite(dock_clip_ut) or dock_clip_ut <= 0.0:
        return True
    if not _finite(leg_start_ut):
        return True
    return leg_start_ut < dock_clip_ut


def _rule_route(ctx: _Ctx) -> None:
    snap = ctx.snap
    for b in snap.route_line_builds:
        label = "ROUTE_LINE_BUILD[%s]" % (b.route_id or "?")
        # SITE-SPECIFIC `continue`, kept explicit: an unrecognised scope means
        # the per-scope clauses below would be adjudicating against a rule set
        # that does not apply, so the record is reported and then skipped.
        if ctx.unknown_token(label + ".scope", "route scope", b.scope, ROUTE_SCOPES):
            continue
        dropped = b.transfer_legs_dropped
        total = b.total_legs
        if b.scope == "MalformedMixedBodies":
            if total is not None and total > 0:
                ctx.add(RULE_ROUTE, LEVEL_FAIL, label,
                        "malformed-mixed-bodies scope built %d legs; a malformed "
                        "route draws NOTHING" % total)
        elif b.scope == "SameBody":
            if dropped:
                ctx.add(RULE_ROUTE, LEVEL_FAIL, label,
                        "same-body scope dropped %d transfer leg(s); the endpoint "
                        "filter runs only for InterBody, so same-body keeps every "
                        "non-orbital leg" % dropped)
        elif b.scope == "InterBody":
            if dropped is None or total is None:
                ctx.uneval(UNEVAL_ROUTE_LEG_DETAIL_ABSENT)
            elif dropped > total:
                ctx.add(RULE_ROUTE, LEVEL_FAIL, label,
                        "inter-body scope dropped %d of %d legs - more legs "
                        "dropped than built" % (dropped, total))
            elif dropped == 0 and total > 0:
                # The ROUND-TRIP stand-down (origin == destination) legitimately
                # keeps every leg; without per-leg bodies the two cases are not
                # separable, so this is a reading, not a red.
                ctx.uneval(UNEVAL_ROUTE_LEG_DETAIL_ABSENT)
        if b.groups is not None and b.groups < 0:
            ctx.add(RULE_ROUTE, LEVEL_FAIL, label,
                    "negative group count %d" % b.groups)

    for v in snap.route_codraw_violations:
        ctx.add(RULE_ROUTE, LEVEL_FAIL,
                "ROUTE_CODRAW_VIOLATION[%s recId=%s]" % (v.route_id, v.rec_id),
                "route line and ghost polyline painted the same recording at "
                "ut=%r frame=%s; DrawAll skips a member the polyline renderer "
                "owns (the IsRenderingNonOrbitalLeg arbitration)"
                % (v.ut, v.frame))

    drawn_recs = {d.rec_id for d in snap.dwells
                  if d.rec_id and d.visible is not False}
    for unit in snap.units:
        route = unit.route
        if route is None:
            continue
        ctx.unknown_token("%s.ROUTE.scope" % _unit_label(unit),
                          "route scope", route.scope, ROUTE_SCOPES)
        clip = route.recorded_dock_ut
        if not _finite(clip) or clip <= 0.0:
            continue
        for m in unit.members:
            if not m.rec_id or m.rec_id not in drawn_recs:
                continue
            if not leg_within_dock_clip(m.start_ut, clip):
                ctx.add(RULE_ROUTE, LEVEL_FAIL,
                        "%s member[%s recId=%s]"
                        % (_unit_label(unit), m.index, m.rec_id),
                        "member starts at %r, at or after the dock clip %r, yet a "
                        "dwell rendered it; LegWithinDockClip keeps a leg only "
                        "while legStartUT < dockClipUT" % (m.start_ut, clip))


# --- RC-OWN -----------------------------------------------------------------


def _rule_own(ctx: _Ctx) -> None:
    """One treatment per pid per instant, ownership conservation both
    directions, and the (WARN-capped) marker half."""
    snap = ctx.snap
    for d in snap.dwells + snap.open_dwells:
        ctx.unknown_token("DWELL[pid=%s openUT=%r].treatment" % (d.pid, d.open_ut),
                          "treatment", d.treatment, TREATMENTS)
        ctx.unknown_token("DWELL[pid=%s openUT=%r].coverage" % (d.pid, d.open_ut),
                          "coverage", d.coverage, COVERAGES)
        ctx.unknown_token("DWELL[pid=%s openUT=%r].phaseKind" % (d.pid, d.open_ut),
                          "phase kind", d.phase_kind, PHASE_KIND_NAMES,
                          "the grep tokens %s normalise onto those names"
                          % (sorted(PHASE_KIND_TOKENS),))

    for lb in snap.line_branches:
        ctx.unknown_token("LINE_BRANCH[pid=%s ut=%r].coverage" % (lb.pid, lb.ut),
                          "render-window coverage", lb.coverage,
                          RENDER_WINDOW_COVERAGES,
                          "note this is MapRenderTrace's 3-state "
                          "RenderWindowCoverage, NOT the segment Coverage enum "
                          "the DWELL records carry")

    by_pid: Dict[Any, List[Dwell]] = {}
    for d in snap.dwells:
        if d.pid is None or not (_finite(d.open_ut) and _finite(d.close_ut)):
            continue
        by_pid.setdefault(d.pid, []).append(d)
    for pid, items in sorted(by_pid.items(), key=lambda kv: str(kv[0])):
        items.sort(key=lambda d: d.open_ut)
        # RUNNING MAX-CLOSE sweep, not a consecutive-pair compare. Sorted by open
        # UT, an overlap need not be between ADJACENT entries: one long dwell
        # followed by a short one that fits inside it hides every later overlap
        # with the long one from a pairwise walk. Carrying the furthest close seen
        # so far catches the non-adjacent case at the same cost.
        widest = None
        for d in items:
            if widest is not None and d.open_ut < widest.close_ut:
                ctx.add(RULE_OWN, LEVEL_FAIL, "pid=%s" % pid,
                        "two dwells overlap in UT ([%r, %r] treatment=%s and "
                        "[%r, %r] treatment=%s); exactly one treatment is active "
                        "per ghost per frame"
                        % (widest.open_ut, widest.close_ut, widest.treatment or "?",
                           d.open_ut, d.close_ut, d.treatment or "?"))
            if widest is None or d.close_ut > widest.close_ut:
                widest = d

    # Ownership conservation, both directions.
    #
    # The publish->draw direction carries three ratified exemptions and one cap.
    # (a) A recId with NO dwell anywhere in the manifest is the PROTO-LESS pid-0
    #     population: the polyline Driver walk is its only renderer and the
    #     Director never opens a dwell for it, so there is no dwell to intersect.
    #     (Wave-1 keyed this exemption on pid-0 DWELLS, which by construction
    #     never exist - it exempted nothing and the population red'd.)
    # (b) A published span whose CONCURRENT dwell for that recId is StockConic is
    #     the Driver-direct bridge / forward-leg population: the polyline draw
    #     host is ratified, the draw is real, it simply is not a TracedPath dwell.
    # (c) Whatever publish still has no draw is capped at WARN pending live
    #     calibration - see the design doc's ratified deviation #5. The MIRROR
    #     direction (a draw with no publish) stays FAIL: `drewNonOrbitalLegRecordings`
    #     is the SOLE ownership source and is published on an ACTUAL draw, so a
    #     draw that never published is a real ownership defect, not a blind spot.
    dwelt_recs = {d.rec_id for d in snap.dwells + snap.open_dwells if d.rec_id}
    publish_no_draw = 0
    publish_exempt_protoless = 0
    publish_exempt_stockconic = 0
    owned_intervals: Dict[str, List[Tuple[float, float]]] = {}
    for ch in snap.ownership_changes:
        ctx.unknown_token(
            "OWNERSHIP_CHANGE[recId=%s ut=%r].event" % (ch.rec_id, ch.ut),
            "ownership event", ch.event, OWNERSHIP_EVENTS)
    for rec_id in sorted({c.rec_id for c in snap.ownership_changes if c.rec_id}):
        events = sorted((c for c in snap.ownership_changes
                         if c.rec_id == rec_id and _finite(c.ut)),
                        key=lambda c: c.ut)
        open_at = None
        spans: List[Tuple[float, float]] = []
        for c in events:
            if c.event == "appear" and open_at is None:
                open_at = c.ut
            elif c.event == "disappear" and open_at is not None:
                spans.append((open_at, c.ut))
                open_at = None
        if open_at is not None and _finite(snap.export_ut):
            spans.append((open_at, snap.export_ut))
        owned_intervals[rec_id] = spans
        if rec_id not in dwelt_recs:
            # Exemption (a): proto-less pid-0 - no dwell exists to intersect.
            publish_exempt_protoless += len(spans)
            continue
        traced = [(d.open_ut, d.close_ut) for d in snap.dwells
                  if d.rec_id == rec_id and d.treatment == "TracedPath"
                  and d.visible is not False
                  and _finite(d.open_ut) and _finite(d.close_ut)]
        stock_conic = [(d.open_ut, d.close_ut) for d in snap.dwells + snap.open_dwells
                       if d.rec_id == rec_id and d.treatment == "StockConic"
                       and _finite(d.open_ut) and _finite(d.close_ut)]
        # CLOSED-interval overlap (>= / <=) in BOTH directions. A one-frame
        # ownership publish and a one-frame dwell are both zero-width intervals,
        # and a strict test says a zero-width span intersects nothing - so the
        # shortest real draw, the exact case the conservation rule most wants to
        # see, would red as "published with no draw" and as "drawn outside every
        # published interval" simultaneously. Touching endpoints participate.
        for lo, hi in spans:
            if any(t_hi >= lo and t_lo <= hi for t_lo, t_hi in traced):
                continue
            if any(s_hi >= lo and s_lo <= hi for s_lo, s_hi in stock_conic):
                # Exemption (b): the Driver-direct StockConic draw host.
                publish_exempt_stockconic += 1
                continue
            publish_no_draw += 1
            ctx.add(RULE_OWN, LEVEL_WARN, "recId=%s" % rec_id,
                    "ownership published over [%r, %r] with no intersecting "
                    "TracedPath dwell and no concurrent StockConic dwell; a "
                    "published ownership implies a draw (capped at WARN pending "
                    "live calibration - design deviation #5)" % (lo, hi))
    for d in snap.dwells:
        if d.treatment != "TracedPath" or d.visible is False:
            continue
        if not d.rec_id or d.pid == 0:
            continue
        spans = owned_intervals.get(d.rec_id)
        if spans is None:
            ctx.add(RULE_OWN, LEVEL_FAIL, "recId=%s" % d.rec_id,
                    "a visible TracedPath dwell [%r, %r] exists with NO ownership "
                    "record for the recording; a draw implies a publish"
                    % (d.open_ut, d.close_ut))
            continue
        # Closed-interval overlap, the mirror of the publish->draw direction above.
        if not any(hi >= d.open_ut and lo <= d.close_ut for lo, hi in spans):
            ctx.add(RULE_OWN, LEVEL_FAIL, "recId=%s" % d.rec_id,
                    "a visible TracedPath dwell [%r, %r] falls outside every "
                    "published ownership interval %r" % (d.open_ut, d.close_ut, spans))
        if d.marker_decision is None:
            ctx.uneval(UNEVAL_MARKER_DECISION_ABSENT)
        elif d.marker_decision is False and d.marker_icon_suppressed is not True:
            # WARN-capped by the design's stated blind spot: IMGUI marker records
            # are DECISION-ONLY (no truth read) until V6's post-OnGUI reconcile.
            ctx.add(RULE_OWN, LEVEL_WARN,
                    "DWELL[pid=%s recId=%s openUT=%r]" % (d.pid, d.rec_id, d.open_ut),
                    "visible dwell with markerDecision=False and no icon "
                    "suppression - a blank icon (decision-only record, capped at "
                    "WARN per the design's marker blind spot)")
    ctx.metrics["ownPublishWithoutDraw"] = publish_no_draw
    ctx.metrics["ownPublishExemptProtoless"] = publish_exempt_protoless
    ctx.metrics["ownPublishExemptStockConic"] = publish_exempt_stockconic


# --- RC-WARP ----------------------------------------------------------------


def _rule_warp(ctx: _Ctx) -> None:
    """Anti-vacuity: the declared warp buckets are actually covered, and at least
    one seam was traversed above 1x. FAIL only on an armed block."""
    snap = ctx.snap
    totals = _warp_totals(snap.dwells + snap.open_dwells)
    ctx.metrics["warpBuckets"] = totals
    level = LEVEL_FAIL if ctx.armed else LEVEL_INFO
    declared = (ctx.block or {}).get("warpBuckets")
    if isinstance(declared, (list, tuple)):
        for name in declared:
            if totals.get(str(name), 0) <= 0:
                ctx.add(RULE_WARP, level, "warpBuckets.%s" % name,
                        "spec declared warp bucket %r and the manifest counted "
                        "zero frames in it - the run did not visit that warp "
                        "regime" % (name,))
    above = [d for d in snap.dwells if d.frames_above_1x > 0]
    seam_uts = [t.ut for t in snap.transitions if _finite(t.ut)]
    seams_above = _uts_inside(above, seam_uts)
    ctx.metrics["seamsAboveOneX"] = seams_above
    if declared and seam_uts and seams_above == 0:
        ctx.add(RULE_WARP, level, "seamsAboveOneX",
                "%d transition(s) observed and none of them fell inside a dwell "
                "that carried an above-1x frame; a composition PASS from a "
                "1x-only traversal cannot claim warp coverage" % len(seam_uts))
    # The hold half. Schema v1.1's hold pair supplies the observation, so this is no
    # longer a blanket "the schema cannot answer": it is unevaluable only when the
    # run carried no hold event at all.
    hold_uts = [e.ut for e in snap.clock_events
                if e.kind in (CLOCK_HOLD_ENGAGE, CLOCK_HOLD_RELEASE) and _finite(e.ut)]
    if not hold_uts:
        ctx.uneval(UNEVAL_WARP_HOLD_ABSENT)
        ctx.metrics["holdsAboveOneX"] = 0
        return
    holds_above = _uts_inside(above, hold_uts)
    ctx.metrics["holdsAboveOneX"] = holds_above
    if declared and holds_above == 0:
        ctx.add(RULE_WARP, level, "holdsAboveOneX",
                "%d hold event(s) observed and none of them fell inside a dwell "
                "that carried an above-1x frame; a composition PASS from a 1x-only "
                "hold traversal cannot claim warp coverage" % len(hold_uts))


# --- RC-QUAL ----------------------------------------------------------------


def _rule_qual(ctx: _Ctx) -> None:
    """The ratified-but-visible metrics, reported per run for trend tracking.
    INFO only, never gating: these are the inputs to future promote-to-fixed
    product decisions, not assertions."""
    m = ctx.metrics
    if _finite(m.get("maxTangentAngleRad", NAN)):
        ctx.add(RULE_QUAL, LEVEL_INFO, "seam.tangent",
                "max rigid-seam tangent angle %.9g rad over %d record(s)"
                % (m["maxTangentAngleRad"], len(ctx.snap.seam_tangents)))
    if _finite(m.get("maxEndpointRatio", NAN)):
        ctx.add(RULE_QUAL, LEVEL_INFO, "seam.endpoint",
                "max FlexibleSoi endpoint/SOI ratio %.9g over %d record(s)"
                % (m["maxEndpointRatio"], len(ctx.snap.seam_endpoints)))
    if _finite(m.get("maxInteriorGapSeconds", NAN)):
        ctx.add(RULE_QUAL, LEVEL_INFO, "hold.interiorGap",
                "longest InteriorGap hold %.9g s" % m["maxInteriorGapSeconds"])
    holds = m.get("planHoldSeconds") or []
    if holds:
        ctx.add(RULE_QUAL, LEVEL_INFO, "hold.arrival",
                "planned arrival holds: min %.9g s, max %.9g s over %d unit(s)"
                % (min(holds), max(holds), len(holds)))
    observed = m.get("observedHoldSeconds") or []
    if observed:
        ctx.add(RULE_QUAL, LEVEL_INFO, "hold.observed",
                "observed holds: min %.9g s, max %.9g s over %d release event(s)"
                % (min(observed), max(observed), len(observed)))
    residuals = m.get("descentResidualsDeg") or []
    if residuals:
        ctx.add(RULE_QUAL, LEVEL_INFO, "descent.siteRotationResidual",
                "descent site rotation residual max %.9g deg over %d event(s)"
                % (max(residuals), len(residuals)))


# --- RC-UNKNOWN -------------------------------------------------------------


def _rule_unknown(ctx: _Ctx) -> None:
    """The catch-all. A TRUNCATED section makes the affected records
    defined-UNEVALUABLE, never unknown (SPEC), which is why the truncated marker
    is consulted before anything is called unclaimed."""
    snap = ctx.snap
    for name in snap.unknown_observed_sections:
        ctx.add(RULE_UNKNOWN, LEVEL_FAIL, "OBSERVED." + name,
                "record type no rule claimed (known: %s)"
                % list(OBSERVED_SECTIONS))
    for e in snap.clock_events:
        # NOT routed through ctx.unknown_token: a BLANK kind is genuinely unknown
        # here (an event with no kind is an event no rule can claim), whereas the
        # helper's contract is that an absent token is not a wrong one.
        if e.kind not in CLOCK_EVENT_KINDS:
            ctx.add(RULE_UNKNOWN, LEVEL_FAIL,
                    "CLOCK_EVENT[ut=%r].kind" % e.ut,
                    "unknown clock-event kind %r (known: %s)"
                    % (e.kind, list(CLOCK_EVENT_KINDS)))
    for unit in snap.units:
        ctx.unknown_token("%s.host" % _unit_label(unit), "host", unit.host, HOSTS)
    for t in snap.truncated:
        ctx.uneval("%s-%s" % (UNEVAL_TRUNCATED,
                              (t.section or "unnamed").lower()),
                   int(t.dropped_count or 1))


_RULE_ORDER: Tuple[Tuple[str, Any], ...] = (
    (RULE_CONST, _rule_const),
    (RULE_COVER, _rule_cover),
    (RULE_SEAM, _rule_seam),
    (RULE_HOLD, _rule_hold),
    (RULE_CUT, _rule_cut),
    (RULE_DESCENT, _rule_descent),
    (RULE_CYCLE, _rule_cycle),
    (RULE_ROUTE, _rule_route),
    (RULE_OWN, _rule_own),
    (RULE_WARP, _rule_warp),
    (RULE_QUAL, _rule_qual),
    (RULE_UNKNOWN, _rule_unknown),
)


def evaluate_rules(snapshot: Optional[ManifestSnapshot],
                   block: Optional[Dict] = None
                   ) -> Tuple[Tuple[RenderComposeFinding, ...], Dict[str, int],
                              Dict[str, Any]]:
    """Run every rule over a parsed snapshot.

    Returns ``(findings, unevaluable_by_reason, metrics)``. An absent or
    unparsed snapshot returns empty triples: "no manifest" is a mismatch the
    EVALUATOR raises against the declared blocks, not a rule finding (the
    ``saveparse.evaluate_save_structure`` unreadable-save shape)."""
    if snapshot is None or not snapshot.parsed:
        return (), {}, {}
    ctx = _Ctx(snapshot, block)
    for _rule_id, fn in _RULE_ORDER:
        fn(ctx)
    return tuple(ctx.findings), dict(ctx.unevaluable), dict(ctx.metrics)


# ---------------------------------------------------------------------------
# Spec surface ([expectations.renderComposition]).
#
# The window grammar and the three anti-vacuity notches are saveparse's
# semantics, COPIED rather than imported. FIVE helpers are copies, not four:
# `_validate_window`, `_validate_gating`, `_validate_armed_empty`,
# `_validate_armed_unreddable` and - further down, beside the evaluator -
# `_check_window`. All five are that module's PRIVATES, and a sibling's private
# helper is not an API. The rules are identical and a drift between the two
# copies is caught by a unit cell that runs ALL FIVE over the same inputs.
#
# GATING_KEY is the one thing that is IMPORTED rather than copied: it is a public
# name on saveparse, it is the spelling of a spec KEY (so the two modules must
# agree byte-for-byte or an armed block silently reads as unarmed), and a copy
# would be a second literal to keep in step for no benefit.
# ---------------------------------------------------------------------------

GATING_KEY = saveparse.GATING_KEY

RENDER_COMPOSITION_BLOCK = "renderComposition"
RENDER_COMPOSITION_WINDOW_KEYS: Tuple[str, ...] = ("dwells", "cycles", "unevaluable")
RENDER_COMPOSITION_LIST_KEYS: Tuple[str, ...] = ("warpBuckets", "requireSeamKinds")
RENDER_COMPOSITION_ASSERTION_KEYS: Tuple[str, ...] = (
    RENDER_COMPOSITION_WINDOW_KEYS + RENDER_COMPOSITION_LIST_KEYS)
RENDER_COMPOSITION_BLOCK_KEYS: Tuple[str, ...] = (
    (GATING_KEY,) + RENDER_COMPOSITION_ASSERTION_KEYS)

# The vocabulary each list key is VALIDATED against. `requireSeamKinds` is
# narrowed to SEAM_KINDS_REQUIRABLE deliberately: the parse vocabulary is wider
# (it must tolerate every token the writer can produce), but a spec may only
# require a kind an actual boundary can carry.
_LIST_KEY_VOCABULARY: Dict[str, Tuple[str, ...]] = {
    "warpBuckets": WARP_BUCKETS,
    "requireSeamKinds": SEAM_KINDS_REQUIRABLE,
}


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


def _validate_armed_empty(prefix: str, block: Dict,
                          assertion_keys: Tuple[str, ...]) -> List[str]:
    """``gating = true`` with ZERO assertion keys is a gate the author believes
    is on. It is NOT harmless here even though FAIL findings gate on their own:
    a block with no anti-vacuity floor passes green off a manifest that observed
    nothing at all, which is the exact shape this module exists to refuse."""
    if block.get(GATING_KEY) is True and not any(k in block for k in assertion_keys):
        return ["%s: gating = true with no assertion key gates nothing - "
                "declare at least one of %s" % (prefix, list(assertion_keys))]
    return []


def _validate_armed_unreddable(prefix: str, block: Dict,
                               window_keys: Tuple[str, ...]) -> List[str]:
    """An ARMED window whose only bound is ``min = 0`` can never fail (counts are
    never negative). Third notch of the same rule as the two above."""
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


def _validate_token_list(prefix: str, val: Any,
                         vocabulary: Tuple[str, ...]) -> List[str]:
    """A list assertion is a non-empty list of known tokens, no duplicates. An
    EMPTY list is refused for the same reason an empty window is: it declares an
    assertion and asserts nothing."""
    if not isinstance(val, (list, tuple)):
        return ["%s: %r must be a list of %s" % (prefix, val, list(vocabulary))]
    if not val:
        return ["%s: an empty list asserts nothing - name at least one of %s"
                % (prefix, list(vocabulary))]
    errs: List[str] = []
    seen = set()
    for item in val:
        if not isinstance(item, str):
            errs.append("%s: %r must be a string from %s"
                        % (prefix, item, list(vocabulary)))
            continue
        if item not in vocabulary:
            errs.append("%s: unknown value %r (accepted: %s)"
                        % (prefix, item, list(vocabulary)))
        if item in seen:
            errs.append("%s: %r listed more than once" % (prefix, item))
        seen.add(item)
    return errs


def validate_render_composition_expectations(block: Any) -> List[str]:
    """Validate the ``[expectations.renderComposition]`` spec surface
    (pre-launch, pure). ``None`` => no block declared => valid."""
    if block is None:
        return []
    if not isinstance(block, dict):
        return ["expectations.renderComposition: must be a table"]
    prefix = "expectations." + RENDER_COMPOSITION_BLOCK
    errs: List[str] = []
    unknown = sorted(k for k in block if k not in RENDER_COMPOSITION_BLOCK_KEYS)
    if unknown:
        errs.append("%s: unknown key(s) %s (accepted: %s)"
                    % (prefix, unknown, list(RENDER_COMPOSITION_BLOCK_KEYS)))
    errs.extend(_validate_gating(prefix, block))
    errs.extend(_validate_armed_empty(prefix, block,
                                      RENDER_COMPOSITION_ASSERTION_KEYS))
    errs.extend(_validate_armed_unreddable(prefix, block,
                                           RENDER_COMPOSITION_WINDOW_KEYS))
    for key in RENDER_COMPOSITION_WINDOW_KEYS:
        if key in block:
            errs.extend(_validate_window("%s.%s" % (prefix, key), block[key]))
    for key in RENDER_COMPOSITION_LIST_KEYS:
        if key in block:
            errs.extend(_validate_token_list("%s.%s" % (prefix, key), block[key],
                                             _LIST_KEY_VOCABULARY[key]))
    return errs


def render_composition_expectation_warnings(
        expectations: Optional[Dict]) -> List[str]:
    """Inert-declaration WARNINGS (never errors), mirroring
    ``saveparse.save_structure_expectation_warnings``: an UNARMED block with no
    assertion key still reports the facets, but declares nothing."""
    expectations = expectations or {}
    block = expectations.get(RENDER_COMPOSITION_BLOCK)
    if isinstance(block, dict) and not any(
            k in block for k in RENDER_COMPOSITION_ASSERTION_KEYS):
        return ["%s: declared with no assertion key - the block reports the "
                "measured facets and gates nothing"
                % ("expectations." + RENDER_COMPOSITION_BLOCK)]
    return []


def declared_composition_blocks(expectations: Optional[Dict]) -> Tuple[str, ...]:
    """``("renderComposition",)`` when the spec declares the block, else ``()``.

    ``run.py`` uses a non-empty result to decide whether to set
    ``PARSEK_RENDER_MANIFEST=1`` at launch (SPEC decision 5), so this is a
    launch-time surface as well as an evaluation-time one."""
    expectations = expectations or {}
    if isinstance(expectations.get(RENDER_COMPOSITION_BLOCK), dict):
        return (RENDER_COMPOSITION_BLOCK,)
    return ()


def armed_composition_blocks(expectations: Optional[Dict]) -> Tuple[str, ...]:
    """The declared subset carrying ``gating = true``. Arming is PER-BLOCK."""
    expectations = expectations or {}
    block = expectations.get(RENDER_COMPOSITION_BLOCK)
    if isinstance(block, dict) and block.get(GATING_KEY) is True:
        return (RENDER_COMPOSITION_BLOCK,)
    return ()


def gating_armed(expectations: Optional[Dict]) -> bool:
    """True iff the renderComposition block is armed. NO committed spec arms it
    as of this module landing (the row ships REPORT-ONLY); promotion is an
    operator decision taken after a report-only reading run, pinned by the
    harness allowlist sweep."""
    return bool(armed_composition_blocks(expectations))


# ---------------------------------------------------------------------------
# Measured facets.
# ---------------------------------------------------------------------------


def observed_composition_facets(
        snapshot: Optional[ManifestSnapshot],
        precomputed: Optional[Tuple[Tuple[RenderComposeFinding, ...],
                                    Dict[str, int], Dict[str, Any]]] = None
) -> Dict[str, Any]:
    """The MEASURED facets, mirroring the ``[expectations.*]`` block layout the
    way ``saveparse.observed_structure_facets`` does. ``None`` / unparsed => ``{}``
    (ABSENT means "not measured", never zero).

    Recorded UNCONDITIONALLY on a parseable manifest: that is how a lane earns
    its first honest window off a green report-only run.

    ``precomputed`` is the ``evaluate_rules`` triple the caller already holds, so
    the combined path (``evaluate_render_composition``) runs the rule set ONCE
    over a snapshot instead of twice. It is an optimisation, not a different
    measurement: every facet below is a pure function of the snapshot plus that
    triple.

    The one facet a DECLARED spec block can move is the ``findings`` CENSUS -
    RC-WARP / RC-SEAM raise their list-key rows only when the spec declared
    ``warpBuckets`` / ``requireSeamKinds``, at FAIL rather than INFO once the
    block is armed. That is the honest reading (the census then counts the rows
    the row actually produced), and every other facet - counts, vocabularies, the
    unevaluable ledger, the quality metrics - is block-independent, so a
    standalone call and the combined path agree on all of them."""
    if snapshot is None or not snapshot.parsed:
        return {}
    findings, unevaluable, metrics = (
        precomputed if precomputed is not None
        else evaluate_rules(snapshot, None))
    by_level: Dict[str, Dict[str, int]] = {lvl: {} for lvl in LEVELS}
    for f in findings:
        bucket = by_level[f.level]
        bucket[f.rule_id] = bucket.get(f.rule_id, 0) + 1
    clock_kinds: Dict[str, int] = {}
    for e in snapshot.clock_events:
        clock_kinds[e.kind or "(blank)"] = clock_kinds.get(e.kind or "(blank)", 0) + 1
    treatments: Dict[str, int] = {}
    coverages: Dict[str, int] = {}
    for d in snapshot.dwells:
        treatments[d.treatment or "(blank)"] = treatments.get(d.treatment or "(blank)", 0) + 1
        coverages[d.coverage or "(blank)"] = coverages.get(d.coverage or "(blank)", 0) + 1
    # Cycles are counted per (owner, cycleIndex) PAIR, not by index alone: two
    # units both running their cycle 0 are two observed cycles, and collapsing
    # them would let a two-unit run claim a `cycles = { min = 2 }` floor off a
    # single cycle each.
    window_cache: Dict[Any, List[Tuple[int, float, float]]] = {}

    def _windows(owner: Optional[int]) -> List[Tuple[int, float, float]]:
        if owner not in window_cache:
            window_cache[owner] = _cycle_windows(snapshot, owner)
        return window_cache[owner]

    cycles = sorted({(u.owner_index, w[0]) for u in snapshot.units
                     for w in _windows(u.owner_index)})
    if not cycles:
        cycles = sorted({(None, w[0]) for w in _windows(None)})
    max_step = NAN
    for d in snapshot.dwells:
        if _finite(d.max_ut_step) and (math.isnan(max_step) or d.max_ut_step > max_step):
            max_step = d.max_ut_step
    residuals = metrics.get("cycleLengthResidualsSeconds") or []
    return {
        RENDER_COMPOSITION_BLOCK: {
            # The three window-key facets, named exactly as their spec keys.
            "dwells": len(snapshot.dwells),
            "cycles": len(cycles),
            "unevaluable": sum(unevaluable.values()),
            # The two list-key facets.
            "warpBuckets": metrics.get("warpBuckets", {b: 0 for b in WARP_BUCKETS}),
            "seamKinds": metrics.get("seamKinds", {}),
            # Header triage.
            "schemaVersion": snapshot.schema_version,
            "exportReason": snapshot.export_reason,
            "scene": snapshot.scene,
            "mapRenderTracingOn": snapshot.map_render_tracing_on,
            # Structural counts.
            "openDwells": len(snapshot.open_dwells),
            "planUnits": len(snapshot.units),
            "chainBuilds": len(snapshot.chain_builds),
            "transitions": len(snapshot.transitions),
            "clockEvents": clock_kinds,
            "seamTangents": len(snapshot.seam_tangents),
            "seamEndpoints": len(snapshot.seam_endpoints),
            "lineBranches": len(snapshot.line_branches),
            "ownershipChanges": len(snapshot.ownership_changes),
            "ratifiedSkips": len(snapshot.ratified_skips),
            "clockDefers": len(snapshot.clock_defers),
            "routeLineBuilds": len(snapshot.route_line_builds),
            "routeLegDefers": len(snapshot.route_leg_defers),
            "routeCoDrawViolations": len(snapshot.route_codraw_violations),
            # STANDALONE ANOMALY_ECHO census, per raise reason. Reported, never
            # gated: the tracer's own promote/report calibration (hlib's
            # ANOMALY_TOKENS vs ANOMALY_REASONS_RAISED_UNGATED) already decides
            # which raises are defects, and a second gate here would red a lane
            # for an instrument that deliberately reports.
            "anomalyEchoes": _echo_census(snapshot.anomaly_echoes),
            "truncatedSections": sorted({t.section for t in snapshot.truncated
                                         if t.section}),
            "treatments": treatments,
            "coverages": coverages,
            # Rule outcome census + the defined-unevaluable ledger. A clause the
            # manifest could not answer is COUNTED here, never silently passed.
            "findings": {lvl: by_level[lvl] for lvl in LEVELS},
            "unevaluableReasons": dict(sorted(unevaluable.items())),
            # The RC-QUAL trend surface.
            "quality": {
                "maxTangentAngleRad": _facet_num(metrics.get("maxTangentAngleRad")),
                "maxEndpointRatio": _facet_num(metrics.get("maxEndpointRatio")),
                "maxInteriorGapSeconds": _facet_num(
                    metrics.get("maxInteriorGapSeconds")),
                "maxUtStepSeconds": _facet_num(max_step),
                "planHoldSeconds": [_facet_num(v)
                                    for v in (metrics.get("planHoldSeconds") or [])],
                "observedHoldSeconds": [
                    _facet_num(v)
                    for v in (metrics.get("observedHoldSeconds") or [])],
                "holdsAboveOneX": metrics.get("holdsAboveOneX", 0),
                "descentResidualsDeg": [
                    _facet_num(v)
                    for v in (metrics.get("descentResidualsDeg") or [])],
                "cutWholeRatios": [_facet_num(v)
                                   for v in (metrics.get("cutWholeRatios") or [])],
                "cycleLengthResidualsSeconds": [_facet_num(v) for v in residuals],
                "coverUnexplainedGaps": metrics.get("coverUnexplainedGaps", 0),
                "coverBelowResolutionGaps": metrics.get("coverBelowResolutionGaps", 0),
                "coverResolutionAbsentGaps": metrics.get(
                    "coverResolutionAbsentGaps", 0),
                "seamsAboveOneX": metrics.get("seamsAboveOneX", 0),
            },
        },
    }


def _echo_census(echoes: Sequence[AnomalyEcho]) -> Dict[str, int]:
    """Standalone ANOMALY_ECHO records counted BY REASON, sorted for a stable
    run-JSON diff. A blank reason rides as ``(blank)`` rather than being dropped:
    a raise the writer could not name is still a raise."""
    counts: Dict[str, int] = {}
    for e in echoes:
        key = e.reason or "(blank)"
        counts[key] = counts.get(key, 0) + 1
    return dict(sorted(counts.items()))


def _facet_num(value: Any) -> Optional[float]:
    """NaN / Inf are NOT JSON-serialisable in the strict sense the run JSON is
    read with, so an unmeasured extreme rides as ``None`` (absent = not measured,
    the facet convention) rather than as a NaN literal."""
    if value is None:
        return None
    try:
        f = float(value)
    except (TypeError, ValueError):
        return None
    return f if math.isfinite(f) else None


# ---------------------------------------------------------------------------
# Evaluation (the verifier decision).
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class RenderComposeResult:
    """Verifier outcome. Shape = ``saveparse.SaveStructureResult`` plus the
    structured ``findings`` (the M-A1 model saveparse has no need for).

    - ``REPORT``: nothing armed (verdict-neutral default). Mismatches and
      findings are RECORDED and move no verdict; the promotion path reads them
      off the run JSON.
    - ``PASS`` / ``FAIL``: the block is armed. ``FAIL`` must be mapped by the
      caller to the ``render_composition_mismatch`` verifier flag
      (PARSEK-FAIL, subkind ``render-composition``).

    ``mismatches`` carries every mismatch (window assertions AND every FAIL-level
    finding rendered flat); ``armed_mismatches`` is the verdict-driving subset -
    identical to ``mismatches`` while there is exactly one block, and kept as a
    separate field because the shape must survive a second block being added.
    ``findings`` carries every level, including the INFO trend rows.
    """

    status: str
    gating: bool
    findings: Tuple[RenderComposeFinding, ...]
    mismatches: Tuple[str, ...]
    armed_mismatches: Tuple[str, ...]
    observed: Dict[str, Any]
    blocks: Tuple[str, ...]
    armed_blocks: Tuple[str, ...]
    parsed: Optional[bool]        # None = manifest not read at all
    parse_error: str
    unevaluable: Dict[str, int]

    @property
    def fail_findings(self) -> Tuple[RenderComposeFinding, ...]:
        return tuple(f for f in self.findings if f.level == LEVEL_FAIL)


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


def evaluate_render_composition(
        expectations: Optional[Dict],
        snapshot: Optional[ManifestSnapshot]) -> RenderComposeResult:
    """Evaluate ``[expectations.renderComposition]`` against a parsed manifest.

    Two structural faults are DEFINED mismatches whenever the block is declared
    (the ``saveparse.evaluate_save_structure`` shape, and for the same reason -
    a fault that produces no mismatch is a fault that passes):

    - ``snapshot is None``: the manifest file was absent. On a spec that
      declared the block this is never a silent pass; ``run.py`` sets
      ``PARSEK_RENDER_MANIFEST=1`` precisely because the block was declared, so
      an absent manifest means the recorder never armed, never flushed, or the
      export verb never ran.
    - ``parsed=False``: torn text, or no ``RENDER_MANIFEST`` root. Fail loud;
      never read an unparseable manifest as "zero records".

    With no block declared, both degrade to an empty REPORT row with the fault
    visible in the caller's ``parsed`` / ``parseError`` fields.

    Arming semantics: an armed block gates on the window/list assertions AND on
    every FAIL-level rule finding. The findings are the substance of the module;
    a block that armed but let a FAIL finding through would be fail-open.
    """
    expectations = expectations or {}
    blocks = declared_composition_blocks(expectations)
    armed = armed_composition_blocks(expectations)
    block = expectations.get(RENDER_COMPOSITION_BLOCK)
    block = block if isinstance(block, dict) else None

    mismatches: List[str] = []
    findings: Tuple[RenderComposeFinding, ...] = ()
    unevaluable: Dict[str, int] = {}
    unreadable = snapshot is None or not snapshot.parsed
    parsed: Optional[bool] = None if snapshot is None else bool(snapshot.parsed)
    parse_error = "" if snapshot is None else snapshot.error

    if unreadable:
        # An unreadable manifest measures nothing, so the facets are EMPTY here
        # exactly as `observed_composition_facets` would return them - absent,
        # never zeroed.
        observed: Dict[str, Any] = {}
        if snapshot is None:
            reason = ("manifest absent: no parsek-render-manifest.txt in the KSP "
                      "root (recorder never armed, never flushed, or the "
                      "ExportRenderManifest verb never ran)")
        else:
            reason = "manifest unreadable: %s" % (snapshot.error,)
        for _b in blocks:
            mismatches.append(reason)
    else:
        # ONE rule pass for the whole row: the same triple drives the gating
        # decision and the reported facets.
        findings, unevaluable, metrics = evaluate_rules(snapshot, block)
        observed = observed_composition_facets(
            snapshot, precomputed=(findings, unevaluable, metrics))
        facets = observed[RENDER_COMPOSITION_BLOCK]
        if block is not None:
            for key in RENDER_COMPOSITION_WINDOW_KEYS:
                if key in block:
                    _check_window("%s.%s" % (RENDER_COMPOSITION_BLOCK, key),
                                  block[key], int(facets.get(key, 0) or 0),
                                  mismatches)
            # The list keys are consumed by RC-WARP / RC-SEAM, which raise
            # findings with the right level for the arming state; they are NOT
            # re-checked here, so one declaration never produces two rows.
            #
            # Every FAIL-level finding becomes a mismatch. With the block armed
            # that is what gates; unarmed it is what the promotion path reads.
            mismatches.extend(f.as_text() for f in findings
                              if f.level == LEVEL_FAIL)

    mismatches_t = tuple(dict.fromkeys(mismatches))
    armed_mismatches = mismatches_t if armed else ()
    if armed:
        status = STATUS_PASS if not armed_mismatches else STATUS_FAIL
    else:
        status = STATUS_REPORT
    return RenderComposeResult(
        status=status, gating=bool(armed), findings=findings,
        mismatches=mismatches_t, armed_mismatches=armed_mismatches,
        observed=observed, blocks=blocks, armed_blocks=armed,
        parsed=parsed, parse_error=parse_error, unevaluable=dict(unevaluable))









