# Design: Render Composition Manifest + Verifier (Module M-A7)

Status: DRAFT (2026-08-24). Module M-A7 of the automated testing initiative.
Extends the visual-validation program in `design-testing-unified.md` section 6
(this module is the structural sibling of items V1-V7 there; it is the
composition-accounting layer none of V1-V7 provides). Plain ASCII, no em
dashes.

---

## Problem

A rendered looped mission or supply route in map view is not one trajectory.
It is a COMPOSITION: N member recordings sequenced by the span clock (with
loiter cuts, arrival holds, launch borrows and repays), a per-synodic-window
SYNTHESIZED heliocentric transfer (re-aim replaces the recorded one), a
cross-member descent re-stitch rotated onto the recorded landing site, forward
arcs, seam bridges, and three competing draw surfaces (owned polyline, KSP's
managed conic line, IMGUI markers) arbitrated per frame. The composition is
DELIBERATELY not seamless: the design docs ratify roughly 40 distinct gaps,
holds, kinks and trims, each with a written contract (whole-period cuts only,
hold durations realigned per loop, the ~62 degree FlexibleSoi kink tolerated,
the inter-body route line's transfer leg deliberately dropped, and so on).

Today the only way to know a composed render is right is to watch it, and at
20+ concurrent supply routes that is not a validation strategy. The existing
observability (MapRenderTrace / MapRenderProbe / the parity oracles) validates
FLICKER and PRESENCE, not composition: every anomaly predicate is a
discontinuity detector, every in-game parity assertion samples one frame, no
automated lane asserts composition or continuity ACROSS TIME at warp (V1
dwells a rails-warp stair under the armed anomaly gate, but that gate sees
flicker, not composition, and the warp step-up canary is batch-excluded), the
route overview line surface has zero scenario declarers, and the one lens that catches "the arc dead-ends one
SOI radius short while the icon arrives" is report-only with known-broken
calibration. A ghost drawn smoothly along a wrong-but-continuous arc, a leg
that never draws, a hold that lasts three cycles, a cut that is not a whole
period - none of it reds anything.

The correctness statement this module makes checkable:

> Every rendered segment of a looped mission or route is accounted for; the
> segments jointly cover the mission origin to destination, cycle after cycle;
> every discontinuity between them is one the composition catalog explains,
> within that item's numeric contract; and nothing outside the catalog exists.

That is an ACCOUNTING PROOF, not a smoothness proof. It is exactly the shape
of validation the intentional-misalignment design allows.

## Player-perspective goal (why this is crucial)

With many routes running, map view must read as a coherent living solar
system: every mission visibly traced end to end, take-off and landing parts
included, seams that meet (or gap only where ratified), icons riding their own
lines, loops closing consistently, no orphaned arcs, no lines diving into
planets, at any warp rate. The operator must never again have to visually
confirm that. Additionally, several RATIFIED behaviors are player-visible
(the FlexibleSoi kink, the route line's transfer gap, frozen-ghost holds);
this module MEASURES them per run even though they pass, so promoting any of
them from "tolerated" to "fixed" becomes a data-driven product decision with a
validatable test bed already in place (the deferred cross-SOI synthesis fix,
ranked #1 in `docs/dev/research/reaim-seam-investigation.md`, needs exactly
this bed).

## Non-goals

- NOT a pixel oracle (that is V5 in `design-testing-unified.md` section 6) and
  NOT a replacement for the tracer/probe anomaly layer; both stay.
- NOT solve-correctness of the re-aim Lambert (the parity-oracle scope-honesty
  rule holds: rendered == intended arc is this layer; intended == physically
  optimal is the solver's own test surface). The verifier DOES independently
  recompute the clock math (holds, cuts, trigger congruence) - see "Oracle
  independence" below - but it does not re-derive transfer orbits.
- NOT flight-scene mesh playback (map + TS + KSC render surfaces and the route
  overview line only; the flight mesh has its own instruments).
- NOT per-frame logging. The manifest is event-driven and bounded (the Visual
  & Recording Design Principle applies to diagnostics too).
- No gameplay effect ever. Unlike `MapRenderWarpControl` (a temporary,
  gameplay-breaking debug aid with a removal recipe), this module observes and
  exports; it never touches warp, camera, or render state.

## Prior art and the specific gaps this closes

Authorities read before this design; the catalog itself is NOT restated here
(a second copy of a moving list is a second thing to leave stale) - each
verifier rule cites its authority:

- `design-map-ts-render-architecture.md` (the render pipeline authority:
  phases, provenance, seam taxonomy sections 5-11, tracer contract section 14,
  test plan section 15).
- `design-mission-periodicity.md` (cadence, holds, cuts, hard-vs-soft rules).
- `design-reaim-launch-hold-seam.md`, `design-reaim-heliocentric-parking-
  departure.md`, `done/plans/reaim-descent-trigger.md`,
  `done/plans/reaim-s4-arrival-restitch.md`, `done/plans/reaim-loiter-
  compression.md` (the clock-transform contracts).
- `parsek-logistics-supply-routes-design.md` section 0 (route backing mission,
  dock clip, endpoint-body filtering).
- `autotest-roadmap.md` visual-validation section (dimension model, gap
  register G1-G4, reserved lane ids, confirmation criteria a/b/c).
- `design-autotest-harness-core.md` (verifier chain), the M-C2 save-parse
  verifier (R9) and `design-autotest-ledger-oracle.md` (M-B2) as the
  pure-verifier precedents this module copies.

Known verification gaps this module is aimed at (each has a stated contract
and no check today):

1. Interior-gap HOLD has no duration bound and no off-camera assertion (only
   the inverse, `retire-not-held`, is instrumented).
2. Nothing enforces that a partial (non-whole-period) trim occurs only in the
   flexible SOI-edge region.
3. The cross-SOI seam endpoint check (`seam-endpoint-outside-soi`) is
   report-only, has never fired, and its single-ratio calibration cannot
   separate benign from defect populations; `RenderParityOracle` skips
   re-aimed members entirely; `loop-seam-teleport` measures the transform,
   not the drawn line.
   ("has never fired" was true when this gap was written and is NO LONGER
   true: the instrument produced its FIRST LIVE RAISE on 2026-08-25, run
   `2026-08-25_1502` (V24W). It stays report-only, and the calibration half
   of this gap is unchanged by one raise - see implementation note 6, which
   carries the echo evidence.)
4. "Synthesized never persists" and copy-then-transform immutability are
   enforced by convention only.
5. The `PadAlignLaunch` -> `DestinationArrivalAlign` composition ordering is
   flagged "a real bug if got wrong" with no named test.
6. Nothing asserts continuity ACROSS TIME (all parity is one-frame), no
   automated lane asserts composition at warp, and the route overview line
   (registry cell D10) has zero declarers.

Carve-outs, so this module's pitch stays honest: gaps 1, 2 and 5 each have a
cheap independent closure (a Tier-C hold-duration bound, a whole-period-cut
unit assertion, a `PadAlignLaunch` -> `DestinationArrivalAlign` ordering unit
test) that should land ahead of M-A7 rather than wait for it, and gap 4 is a
save-side property whose natural home is the existing R9 `saveParse` row or a
unit test, not a render manifest. M-A7 still re-verifies 1, 2 and 5 in the
composed live context (RC-HOLD / RC-CUT), but its unique value is what only a
manifest delivers: cross-time coverage, per-cycle accounting, the warp
matrix, and the RC-QUAL trend record.

---

## Terminology

- **Composition plan**: the intended composition of one loop unit or route,
  as built by `MissionLoopUnitBuilder.Build` (member indices and windows,
  cadences, loiter cuts, arrival/launch holds, descent-trigger fields, re-aim
  window schedule) plus, for routes, the `Route` clip fields
  (`RecordedDockUT`, `ExcludedIntervalKeys`, scope classification).
- **Dwell**: one contiguous interval during which one (pid, chain segment)
  pair held one render intent (treatment + coverage + body frame). Dwells are
  the manifest's unit of observation; per-frame data appears only as
  aggregates inside a dwell.
- **Transition**: the boundary event between two dwells of one pid, carrying
  the sampled exit/enter endpoints and the classified seam kind.
- **Catalog item**: one ratified intentional discontinuity class with a
  numeric contract (e.g. Rigid seam tangent within
  `PhaseSeamClassifier.DefaultTangentToleranceRadians`; loiter cut an exact
  whole multiple of the run period). The catalog is encoded as verifier rules
  citing their authorities, never as a prose copy.
- **Manifest**: the per-run structured export: header (schema version,
  tolerance constants by name, scene, save, run id), plan section, chain
  section, observed section (dwells, transitions, clock events, marker and
  route-line records), each section per pid / per unit / per cycle.
- **Finding**: one verifier observation with a rule id and a verdict level
  (FAIL / WARN / INFO), the M-A1 model. The catch-all rule is: an observed
  transition or dark window no catalog item explains is a FAIL.

---

## Architecture: three layers

### Layer 1 - C# manifest recorder (`Source/Parsek/MapRender/RenderCompositionManifest.cs` + `RenderCompositionRecorder.cs`)

A gated accumulation core + thin hooks at the seams the render pipeline
already exposes. Pure accumulation logic (`RenderCompositionManifest`, plain
data + append/flush methods, no Unity calls) is `internal static`-testable;
the recorder is the thin subscriber.

Gating: env var `PARSEK_RENDER_MANIFEST=1`, read ONCE at addon Awake
(the M-A3 `AutorunHooks` pattern). Unset = zero per-frame work, zero
allocations, nothing written anywhere. Deliberately independent of
`mapRenderTracing` (the tracer is a player-visible setting with its own
instance-wide sidecar-persistence history; this is an automation-only env
hook), though harness lanes will typically arm both.

Capture points (all are existing seams; no new render-path branching):

1. **Plan**: when a host pushes loop units (the three byte-identical
   `DriveMissionLoopUnits` host seams in `ParsekFlight` / `ParsekKSC` /
   `ParsekTrackingStation`; the engine-level `SetLoopUnits` sits below
   them), serialize each unit's plan
   once per builder signature: member indices with `MemberStartUT`/
   `MemberEndUT`, `CadenceSeconds`, `OverlapCadenceSeconds`, `PhaseAnchorUT`,
   `LoiterCuts`, arrival-hold fields (`ArrivalHoldSeconds`, `ArrivalHoldAtUT`,
   `ArrivalAlignPeriodSeconds`, joint fields, `ArrivalAmberReason`),
   launch-hold fields (`LaunchBodyRotationPeriodSeconds`,
   `LaunchHoldEngaged`, `RecordedSoiExitUT`), descent-trigger fields
   (`DescentMemberIndices`, `RecordedDeorbitUT`, `DescentEndUT`,
   captureShift), `IsReaim` + the re-aim window schedule INCLUDING the
   re-timed seam instant per window (the probe's own
   `skip.reaimed-seam-instant-unknown` refusal documents that the recorded
   seam UT is the wrong measurement instant for a re-timed window; without
   the re-timed instant RC-SEAM cannot evaluate re-aimed crossings), and for
   route-backed
   units the `Route` identity, `BackingMissionTreeId`, `RecordedDockUT`,
   `RecordedOriginUndockUT`, excluded interval keys, and
   `ClassifyRouteScope` result. Also the PRIMITIVE inputs the clock math was
   derived from (body rotation periods, parking period, recorded boundary
   UTs) so Layer 3 can recompute independently.
2. **Chain**: at `ShadowRenderDriver.GetOrBuildChain`, on each build
   (cache-keyed, so once per signature + window index): the `PhaseChain`
   phases with `PhaseKind`, provenance, body, UT bounds, seam kinds,
   `chainHasReaimedSegments`, and a chain-provenance flag recording whether
   the spine drove the typed `PhaseChain` or fell back to the legacy
   assembler chain (a factory throw caches a null `PhaseChain` and
   `RunFrame` falls back loudly; a dwell accounted against a chain the
   renderer did not use is a wrong proof).
3. **Dwells and transitions**: at the Director stamp in
   `ShadowRenderDriver.RunFrame` (intent change per pid), at the polyline
   Driver's actual-draw ownership publish, at the `GhostOrbitLinePatch`
   Postfix branch decisions (reason token + `RenderWindowCoverage` stamp),
   and at `GhostMapPresence.ResolveMarkerDrawDecision`. A dwell opens on
   change and closes on the next change; each transition samples the
   trajectory position/velocity at the outgoing segment's end UT and the
   incoming segment's start UT (through the same effective-segment surfaces
   the renderer used), and copies the stitcher's seam record where one exists
   (`CrossMemberSeamStitcher`, `SeamEndpointOracle` values at FlexibleSoi
   crossings).
4. **Clock events**: per cycle: loop rollover (cycle index change), hold
   engage/release with the span-clock's resolved amounts, descent phase
   transitions (`Inert`/`Loiter`/`Descent`/`Done` with `triggerUT`),
   boundary-overlap secondary activation, and the route loop-clock crossing
   of `RecordedDockUT`.
5. **Route overview line**: per `RouteTrajectoryLineRenderer` rebuild
   (signature-gated, so rare): member legs kept/clipped/dropped counts, scope,
   endpoint bodies, dock-clip boundary, per-frame-aggregate skip counts
   (`IsRenderingNonOrbitalLeg` deferrals), and an explicit co-draw
   VIOLATION record appended on any frame where one member's leg is drawn
   by both the overview line and the ghost polyline - recorded on the event
   only, so the per-frame cost lands on the defect, and RC-ROUTE's
   no-double-draw clause becomes checkable as "zero violation records"
   (aggregate skip counts alone cannot prove the absence of a co-draw
   frame).
6. **Aggregates inside a dwell**: frame count, warp-rate histogram (bucketed:
   1x, physics warp, <=100x rails, <=1000x, >1000x), min/max sampled head UT,
   anomaly echoes raised during the dwell (reason + count, from the tracer's
   own raise path when tracing is also on).

Size discipline: event-driven with hard per-pid-per-cycle record caps; a hit
cap appends an explicit `truncated=true` marker record (no silent caps). A
20-route scene at 4x rails should produce kilobytes per cycle, not megabytes;
Phase 1 measures this and pins a budget test.

Export: written as ConfigNode text (`RENDER_MANIFEST` root; doubles in
`ToString("R", InvariantCulture)`) via `FileIOUtils` safe-write to
`parsek-render-manifest.txt` in the KSP root (sibling of
`parsek-test-results.txt`; never inside a save - the manifest is a diagnostic
artifact and the "Synthesized never persists" invariant stays untouched).
Two export triggers: an M-A2 command-seam verb `ExportRenderManifest`
(deferred-completion, returns the path + record counts), and an automatic
flush on scene exit when env-armed. ConfigNode text is chosen over JSON so
Layer 3 reuses `harness/lib/saveparse.py`'s existing fail-loud ConfigNode
parser instead of a second parser.

### Layer 2 - the composition catalog as code

The catalog is encoded twice, deliberately in different forms:

- **Numeric contracts: the header is TRANSPORT, the verifier is AUTHORITY.**
  The C# exporter writes the live values of every tolerance the rules need,
  keyed by their code names: `PhaseSeamClassifier.DefaultTangentToleranceRadians`,
  the `SeamEndpointOracle` ratio tolerance, the bridge constants
  (`BridgeMaxAngleRadians`, `BridgeMinAngleRadians`,
  `BridgeChordMinAngleRadians`, `BridgeMaxSeamGapSeconds`,
  `BridgeSeamSharedBoundaryToleranceSeconds`, `BridgeMergeSampleCount`),
  `AnchorMaxResidualKm` / `AnchorMaxRelResidual`, `SeedFreshnessFrames`, the
  descent member-selection epsilon, the loiter-run detection thresholds, and
  the joint-hold whole-period budget (Phase 1 extracts a named constant for
  any of these that is an inline literal today). The Python verifier carries
  its own RATIFIED-VALUE TABLE for these numbers, updated only deliberately
  and with a citation, and checks that the header MATCHES it: a header value
  that drifted from the ratified table is itself a finding. Without this, a
  one-line C# tolerance change would silently re-tune every armed gate - the
  subject defining its own gate, the exact circularity the
  oracle-independence constraint forbids. A C#-side source-sync test
  additionally pins the exported NAME set against the constants (the
  `CommittedBatchTallySourceSyncTests` pattern), so a renamed or retired
  constant reds at build time instead of silently desynchronizing the
  verifier.
- **Structural rules live in the verifier with citations.** Each rule carries
  a `CitedContract` string naming the production member or doc section that
  defines the contract it checks (the M-A1 rule discipline), so a rule
  asserting a wrong contract dies in review, not as a nightly false alarm.

### Layer 3 - pure Python verifier (`harness/lib/rendercompose.py`) + harness row

Stdlib-only, no KSP, the `saveparse.py` sibling pattern: parse the manifest,
evaluate the rule set, return findings + measured facets. Wired into
`harness/run.py` as a new verifier chain row `renderCompose`:

- SKIPPED on killed / driver-INVALID runs (mission-vs-Parsek orthogonality).
- Measured facets recorded on every driver-valid run that produced a
  manifest; absent manifest on a spec that declared the block is a DEFINED
  mismatch, never a silent pass.
- REPORT-ONLY by default. Gating via a new spec surface
  `[expectations.renderComposition]` with the R9 `gating = true` opt-in,
  admitted to `RESERVED_EXPECTATION_BLOCKS` alongside `route`/`loop` and
  leaving that tuple when the EVALUATOR ships with the sole-owner rule (the
  R9 `rewind` precedent: reservation tracks evaluator ownership, not the
  first declarer), and armed only through the established
  three-run workflow (report-only reading, armed run, negative control) with
  the `ARMED_ALLOWLIST` pin in `harness/lib/test_hlib.py`. Verdict on armed
  mismatch: `PARSEK-FAIL(render-composition)`.

## Oracle independence (binding)

The plan section is emitted by the same code that drives rendering, so a
naive "observed == planned" check could go circular. Three rules keep it
honest, mirroring M-B2:

1. **Recompute the clock math from primitives.** For holds, cuts, launch
   borrow/repay and the descent trigger, the verifier re-derives the expected
   values in Python from the PRIMITIVE inputs the plan section carries
   (rotation periods, parking period, recorded boundary UTs, cadence, cycle
   index) using the formulas the design docs state, and checks BOTH
   directions: C#'s planned values match the recomputation (solver drift),
   and observed matches planned (render drift). A disagreement in either leg
   is a finding naming which leg. The primitives themselves get a second
   source: body rotation and orbital periods are static stock constants
   pinned directly in the Python verifier, and recorded boundary UTs /
   member windows are read independently from the produced save and
   sidecars through the same `saveparse.py` machinery Layer 3 already
   imports. A plan primitive that disagrees with either independent source
   is a finding BEFORE any clock math runs - without this leg, a C# bug
   feeding the wrong period into the plan would be faithfully recomputed in
   Python and both legs would agree (M-B2 earned its independence by
   diffing against KSP's own on-disk save; this module copies that move,
   not just the file format).
2. **The observed side is truth-derived**, not intent-derived: dwell
   open/close events come from actual-draw publishes and the line patch's
   truth-stamped branches wherever a truth surface exists; the Director
   intent is recorded alongside for the decision-vs-truth cross-check, never
   as the sole observation.
3. **Known asymmetric blind spot, stated**: IMGUI marker records are
   decision-only (the probe design's own documented limitation - markers have
   no truth read). Marker rules are therefore capped at WARN until V6's
   post-OnGUI reconcile lands; the doc-level gap stays owned by
   `design-map-ts-render-tracer.md`.

## Verifier rule set (initial)

Rule ids are stable; levels are the defaults before arming decisions.

**RC-COVER (FAIL)** - per unit per observed cycle: the union of visible
dwells, cataloged holds, loiter cuts, the inter-cycle tail, ratified hidden
windows (below-atmosphere, member trims, dock clip, and the count-dependent
overlap spawn throttle and soft-cap suppressions - which activate exactly at
the 20+-route scale this module targets and must classify as ratified, never
as unknown) and InteriorGap holds must equal the planned span, evaluated in
UT with a per-dwell quantization tolerance derived from that dwell's own
warp histogram: at high rails warp one frame steps thousands of UT seconds,
so a dark window narrower than the observed maximum UT step at that point is
UNRESOLVABLE at that warp - reported as below-resolution, never red. Any
residual dark window wider than the local resolution is unexplained. Dwells
accounted against an assembler-fallback chain are flagged via the
chain-provenance record. CitedContract:
`GhostPlaybackLogic.DecideUnitMemberRender`, render-architecture section
11.3.

**RC-SEAM (FAIL)** - every transition classifies into exactly one catalog
item and satisfies its numbers: Rigid within the tangent tolerance;
FlexibleSoi endpoint-vs-SOI-sphere ratio within tolerance at the seam
instant THE PLAN EXPORTS for that window (for a re-timed re-aim window the
recorded seam UT is the wrong measurement instant - the probe's own
`skip.reaimed-seam-instant-unknown` refusal documents this - so a crossing
whose re-timed instant is absent is a defined unevaluable, never a silent
pass), discriminating on unit MODE and seed provenance - which is what
actually retires the standalone oracle's calibration problem, since the
measured record shows benign faithful ratios STRADDLING a defect reading
(a ratio alone cannot separate the populations; `harness/lib/hlib.py`'s own
calibration note); bridges within their band
and never across a body change; SwitchContinuation exempt from position
match by contract. CitedContract: `PhaseSeamClassifier`,
`CrossMemberSeamStitcher`, `SeamEndpointOracle`, render-architecture 6.1/9.1.

CORRECTION (2026-08-26, off V25M's reading run `2026-08-26_1744`): **a
TRANSITION does not necessarily name ONE boundary.** It is a DWELL-stream
event - it fires when the Director's rendered segment index moves - and a
warp step can carry the head clean ACROSS an interior segment, so
`toSegmentIndex` can exceed `fromSegmentIndex + 1` and the record then spans
SEVERAL boundaries. Since `RenderCompositionRecorder.NoteChainBuild` emits
`BoundaryIndex = i` for segment `i`'s `LeadingSeam`, keying the seam table on
`toSegmentIndex` reads the LAST boundary of such a span. Wave-1 did exactly
that and reported a correct `rigid` at the span's final same-body boundary as
a rigid-classified body change. The classification clause now walks EVERY
boundary the transition crossed and takes each one's bodies from the chain's
own `PHASE` records, falling back to the observed `fromBody -> toBody` pair
only for a single-boundary span (where the two agree by construction, which
keeps `assembler-fallback` chains evaluable). A multi-boundary span the PHASE
list cannot resolve is the defined-unevaluable `seam-boundary-bodies-absent`;
a retire (`toSegmentIndex = -1`) or a loop wrap names no boundary. Pure core:
`rendercompose.transition_boundaries`.

**RC-HOLD (FAIL)** - each observed hold matches the recomputed per-cycle
value (arrival hold realigned by the align period; launch borrow repaid at
the recorded SOI exit, netting to zero); a hold is stationary in its OWN
declared frame - the DRAWN/decision position at engage vs release (never a
trajectory re-sample at the held UT, which trivially agrees and proves only
the sampler), compared body-fixed for body-fixed treatments and inertially
for conic treatments, within a tolerance model derived for map-scale double
math in Phase 2 (not the flight-scene `SceneFloatGridToleranceMeters`,
which answers a different question); and every
InteriorGap hold is bounded (default bound: the longest planned seam gap in
the chain plus one reseed interval; exceeding it is the "held forever"
defect the current instruments cannot see). CitedContract:
periodicity arrival-flex rules, `design-reaim-launch-hold-seam.md` 2-4,
`GhostRenderDirector` hold contract.

**RC-CUT (FAIL)** - every loiter cut is an exact whole multiple of the run
period; no dwell samples inside a cut; a partial trim appears only in the
flexible SOI-edge region. CitedContract: `ReaimLoiterCompressor`; the
destination pre-landing trim and flexible-SOI-edge partial-trim rules in
`design-mission-periodicity.md` (the arrival flex-point section; cited by
rule, not line number - that doc is living and line numbers rot).

**RC-DESCENT (FAIL)** - trigger UT congruent to the recorded deorbit
rotation phase; descent head monotone and never before `RecordedDeorbitUT`;
at most one descent member rendering at any instant; the landing dwell's
terminal position within tolerance of the recorded site. CitedContract:
`DescentTrigger`, the S4 recorded-site invariant (ratified 2026-07-07).

**RC-CYCLE (FAIL)** - cycles compared WITHIN the same warp bucket have
isomorphic dwell/transition structure, with segment identity by ROLE
(member index + `PhaseKind`), never by chain or segment id: chains rebuild
per signature + window index, and a re-aim unit's synthesized transfer
differs per synodic window BY DESIGN - structure recurs, geometry does not.
A short dwell legitimately vanishes when a warp step lands inside it (the
InteriorGap hold contract), so cross-bucket structure comparisons are
report-only. Zero accumulated per-cycle drift in the clock checks;
boundary-overlap
secondary appears only when the plan's raw delta exceeds the capped advance,
and hands off to the next primary without a gap. CitedContract: periodicity
zero-drift rules, `ComputeBoundaryOverlapAdvanceSeconds`.

**RC-ROUTE (FAIL)** - scoped per surface: overview-line legs are clipped at
`RecordedDockUT` (`LegWithinDockClip`; a zero/absent clip means no clip) and
ghost dwells stay inside the trimmed member windows the dock-derived
excluded interval keys produce; the overview line's kept/dropped leg
accounting matches the scope classification (same-body draws all
non-orbital legs; inter-body drops exactly the non-endpoint-body legs; on a
round trip the endpoint FILTER stands down and EVERY leg is kept -
stand-down means keep-all, not draw-nothing; malformed draws nothing); and
zero co-draw violation records (capture point 5). CitedContract: logistics
section 0, `RouteTrajectoryLineRenderer`.

**RC-OWN (FAIL)** - exactly one treatment per pid per dwell; ownership
conservation both directions (a published ownership implies a draw record
and vice versa); every visible-intent dwell has an icon or a marker decision
true (never a blank icon). Marker half capped at WARN (see blind spot
above). CitedContract: `GhostRenderIntent` single-treatment rule,
`ResolveMarkerDrawDecision`.

**RC-WARP (FAIL on armed specs)** - anti-vacuity: the manifest's warp
histogram covers the buckets the spec declared, and at least one cataloged
seam and one hold were traversed at above-1x warp. A composition PASS from a
1x-only dwell cannot claim warp coverage.

**RC-QUAL (INFO, always-on measurement)** - the ratified-but-visible
metrics, reported per run for trend tracking, never gating: FlexibleSoi kink
angle and endpoint ratio per seam, hold durations, route transfer-gap span,
InteriorGap hold durations, launch-hold residual seam angle. These are the
inputs to future promote-to-fixed product decisions.

**RC-UNKNOWN (FAIL)** - the catch-all: any transition, dark window, or
observed clock event no rule above claimed.

## Harness integration and lanes

- **Drive pattern**: the V1 map-dwell precedent (mission flies or a fixture
  loads; the runner arms `mapRenderTracing` + `PARSEK_RENDER_MANIFEST`,
  enters map or TS, dwells across a declared warp schedule spanning at least
  two full loop cycles, calls `ExportRenderManifest`, exits). The warp
  schedule is spec-declared per lane and includes step-up and step-down
  transitions across at least one seam and one hold (the known step-up
  hole).
- **First subjects** (committed fixtures, no new flights needed): a
  phase-lock moon loop (V6/V14 class), a re-aim interplanetary landing loop
  (V8/V13 class, exercising re-aim + descent trigger + holds), and a
  faithful return leg (V19 class). Route lanes ride G1: when
  `B27-station-route` / V18M/V18T land, the same manifest + row covers the
  route front door and the D10 overview-line surface; this module does not
  duplicate G1's lane reservations, it consumes them.
- **The warp-schedule subject, added 2026-08-25**: a
  **free-play-validated Kerbin -> Duna re-aim loop** over
  `fixtures/saves/duna-one-recorded`, driven by `m3_loop_arrival_dwell` with a
  rails stair at each of its three span-clock windows
  (`V24W-duna-one-warp-stair`). It is listed separately from the three above
  because the three above CANNOT carry the warp-schedule bullet: every one of
  them moves the clock with instantaneous `TimeJump`s, so their histograms are
  1x-only by construction and RC-WARP is unsatisfiable on them (measured on
  four flights). Two things make this subject the right one. Its provenance is
  a session a HUMAN played and visually validated, so the composition it
  produces is one somebody has already called correct - the only subject in the
  program with that property. And its measured free-play histogram
  (`warp100 4727 / warp1000 15488 / warpHigh 11446`, zero 1x, zero physics) is
  the shape RC-WARP was written for, so the rule's subject is demonstrated
  rather than assumed. The stair is FLAG-GATED on the mission and inert by
  default, so the lanes already armed on this row keep their flown shape
  byte-identically.
- **Scenario ids**: composition lanes normally extend existing V specs with a
  manifest step + `[expectations.renderComposition]` block rather than minting
  a parallel lane family, and the two first subjects did exactly that. The
  warp-schedule lane is the ONE exception and needed a new id, because what it
  changes is the DRIVE SHAPE rather than the expectations - it could not be
  added to a committed lane without moving that lane's flown shape, which the
  S4.1 rule forbids. It is reserved in the roadmap's register like everything
  else (`V24`, and it mints the `W` warp-schedule suffix alongside `M`/`T`/`K`).
- **Negative controls** (confirmation criterion b): each armed spec inverts a
  required COMPOSITION token of its own - e.g. temporarily declare an
  expected hold 60 s longer than planned, or require a seam kind the chain
  does not contain - and must red on exactly
  `PARSEK-FAIL(render-composition)` with `saveParse`, `anomalySweep` and
  `driverValidity` clean.
- **Report-only soak before arming** (criterion a + the R9 workflow): every
  block arms only after a reading run whose facets sit inside the declared
  windows, recorded in `autotest-status.md`.

## Relationship to existing instruments

- **Keeps**: MapRenderTrace/MapRenderProbe (anomaly layer; the manifest
  echoes their raises but does not replace real-time detection), the S0
  coverage instrument, `RenderParityOracle` (geometry fidelity within a
  segment; the manifest checks BETWEEN segments and across time),
  `rigid-seam-tangent-discontinuity` (live raise; RC-SEAM re-evaluates the
  same numbers offline).
- **Supersedes in role**: the standalone `seam-endpoint-outside-soi`
  calibration problem (RC-SEAM discriminates on unit mode and seed
  provenance and evaluates at the plan-exported seam instant, with the plan
  in hand); the "watch it and squint" operator step for composed renders.
- **Out of scope but adjacent**: the two dead gated tokens (`gap-vs-retire`,
  `decision-vs-old-truth`) routing through the unwired reconciler should be
  removed from `ANOMALY_TOKENS` in a separate cleanup PR; noted here so the
  finding is not lost, not claimed by this module.

## Phasing

- **Phase 1 (C#)**: manifest types + recorder + env gate + export verb +
  scene-exit flush; unit tests for the accumulation core, the constant-export
  source-sync pin, and a size-budget test; one manual armed play session
  producing a manifest over a committed loop fixture.
- **Phase 2 (Python)**: parser + rule set + fixtures (synthetic manifests
  forged headlessly, including one per defect class: unexplained gap,
  non-whole-period cut, overlong hold, seam out of tolerance, dock-clip
  violation); `renderCompose` row wired report-only; facets visible in
  results JSON and the contact sheet.
- **Phase 3 (lanes)**: extend two committed V lanes + add the warp schedule;
  three-run arming workflow per lane; negative controls per criterion (b).
- **Phase 4 (routes + product)**: ride G1's B27/V18 for the route surfaces;
  begin the RC-QUAL trend record that feeds promote-to-fixed decisions on
  the ratified visual artifacts.

## Implementation notes (2026-08-25, Phases 1-2)

Phases 1 and 2 shipped together. Where the implementation departed from what
this document specifies, the deviation is RATIFIED and recorded here; the
prose above is otherwise unchanged and still binding. Status (what is shipped,
what is armed) lives in `autotest-status.md`, not here.

Notes 1-5 are the RATIFIED DEVIATIONS. Note 6 is a different kind of entry and
is marked as such: a LIVE-FACT ANNOTATION retiring a factual claim the prose
above makes about an instrument (prior-art gap 3's "has never fired"), added
here rather than rewritten in place so the original claim and the run that
falsified it stay visible together. Later annotations of that kind continue the
same numbering.

1. **`ExportRenderManifest` landed SINGLE-PHASE.** This document called for a
   deferred-completion verb. The export is synchronous - one flush plus one
   safe-write, with nothing to wait for - so the honest shape under the seam's
   own discipline is a single-phase verb answering OK with the written path and
   the record counts in the same dispatch (the `MissionConfig` /
   `SimulateStockSwitchClick` shape). Its one refusal is
   `REJECTED msg=manifest-not-armed`, deliberately not a defer: the arm gate is
   the env var read once at Awake, so waiting could never turn it into a
   success.
2. **Chain-record seam kinds are sourced from the LEGACY ASSEMBLER chain.**
   Production `PhaseChain` seams are always null (`PhaseFactory` builds them
   that way), so a manifest keyed to them would carry nothing. The `CHAIN_BUILD`
   record therefore stamps `seamSource = assembler` and takes its per-boundary
   `SEAM` kinds from `RenderSegment.LeadingSeam` / `TrailingSeam` (the 2-value
   `SeamKind`), while its `PHASE` list still comes from the typed chain when the
   spine drove one. When the spine's own Phase-3 seam work lands, this is the
   record kind to re-point - and RC-SEAM's classification with it.
3. **Seam TANGENT and ENDPOINT measurements require `mapRenderTracing` armed
   ALONGSIDE the env var.** The evaluation sites
   (`ShouldEvaluateTangentSeamAtDraw` and the probe's per-pid gate) are
   tracing-gated, and Phase 1 did NOT widen them: widening a predicate to feed a
   diagnostic changes what the product measures, which is the one thing this
   module must not do. Consequences, both deliberate: a composition lane that
   wants RC-SEAM / RC-QUAL numbers must arm BOTH `PARSEK_RENDER_MANIFEST=1` and
   the `mapRenderTracing` setting; and when tracing was off, the manifest header
   says so (`mapRenderTracingOn = False`) and the verifier reports
   `seam-data-unavailable-tracing-off` as a DEFINED unevaluable that lands in the
   facets, never as a silent pass.
4. **Transition and dwell endpoint TRUTH POSITIONS come from the
   `MapRenderProbe` truth push**, which runs only when tracing is armed (the
   same coupling as note 3, for the same reason). The recorder keeps one latest
   sample per pid and stamps dwell open/close positions from it; with tracing
   off the position keys are omitted entirely and every position clause is
   defined-unevaluable.

5. **RC-OWN's publish->draw direction is capped at LEVEL_WARN pending live
   calibration; the draw->publish direction stays FAIL.** The rule set above
   states ownership conservation without naming a level per direction. The two
   directions turned out to rest on very different evidence.
   `drewNonOrbitalLegRecordings` is the SOLE ownership source and is published
   only on an ACTUAL draw, so a TracedPath dwell that drew with no publish
   covering it is a genuine conservation defect and keeps FAIL. The mirror
   question - "this recording published ownership, where is its draw?" - has two
   RATIFIED populations that legitimately publish without a TracedPath dwell:
   (a) proto-less pid-0 recordings, for which the polyline Driver walk is the
   only renderer and the Director never opens a dwell at all, and (b) the
   Driver-direct bridge / forward-leg population, whose concurrent dwell carries
   the `StockConic` treatment. Both are exempted by name and counted
   (`ownPublishExemptProtoless` / `ownPublishExemptStockConic`). What remains is
   REPORTED at WARN and counted (`ownPublishWithoutDraw`) rather than red,
   because no live composition run has yet calibrated whether a third benign
   population exists. Re-point this to FAIL once a reading run shows the counter
   sitting at zero across the lanes. (Wave-1 shipped this exemption keyed on
   pid-0 DWELLS, which by construction never exist - it was dead code, and the
   proto-less population would have red'd.)

   **AMENDED 2026-08-25 by V6M's reading run (`2026-08-25_2056`): the FAIL
   direction now carries one precondition - the publish surface must have run at
   all.** V6M is the first lane in the suite whose Director ever opened a
   TracedPath dwell, and it raised three RC-OWN FAILs (one per cycle, at the
   arrival brackets) against a manifest with `ownershipChanges = 0`. The reading
   is that the two halves of the contract DO NOT SHARE A GATE. The intent half
   (`ShadowRenderDriver.RunFrame`, which stamps the TracedPath intent that both
   the DWELL records and the stock line/icon suppression read) is driven from
   `ParsekFlight`'s per-frame update and runs whether or not the map is open; the
   publish half (`drewNonOrbitalLegRecordings` -> `NoteOwnershipPublish`) is
   reached only at the END of the polyline Driver's `LateUpdate` walk, whose
   second statement is `if (!MapView.MapIsEnabled) return;`. With the map closed
   the ghost is drawn on no map surface at all, so a visible TracedPath dwell is
   an INTENT record and not evidence of a draw: "a draw implies a publish" is not
   unmet there, it is unasked. No command-seam verb opens the map view (there is
   no `EnterMapView` command and no production path calls `MapView.EnterMapView`),
   so EVERY manifest lane is in this state today - V24W measured
   `ownershipChanges = 0` too and escaped only because it never opened a
   TracedPath dwell. The discriminator is GLOBAL and stays deliberately narrow:
   zero `OWNERSHIP_CHANGE` records ANYWHERE in the manifest stands the direction
   down as the defined-unevaluable `ownership-publish-surface-never-ran`, while
   ONE publish anywhere proves the walk ran past the map gate and every recording
   that then lacks an ownership record keeps its FAIL (that is the
   leg-that-never-draws defect the direction exists to catch). The
   `falls outside every published ownership interval` clause is untouched - it
   already requires that recording to have published. ~~Retiring this
   precondition needs a MAP-OPEN LANE, which needs a new command-seam verb (C# +
   a flown-shape change), not a rule edit.~~

   **DISCHARGED 2026-08-26 by V6M's MAP-OPEN RE-FLY (`2026-08-26_1745`, PASS
   attempt 1). THE PRECONDITION IS SATISFIED AND OWNERSHIP IS CONSERVED ON THIS
   SUBJECT.** R12's `EnterMapView` / `ExitMapView` pair (PR #1539) put the map
   open across every observation window, and the manifest came back with
   `ownershipChanges = 6` - three clean appear/disappear PAIRS on
   `recId 448cd680`, one per TracedPath dwell, at `[296370, 296690]`,
   `[576510, 576830]` and `[985950, 986270]`, THE SAME three brackets whose
   missing records raised the original three FAILs - with `Polyline frame:`
   summaries now present in the collected `KSP.log` (the 2026-08-25 run carried
   zero, which is what proved every `LateUpdate` was bailing at the map gate).
   RC-OWN findings went 3 -> 0, `ownPublishWithoutDraw` reads 0, and
   `ownership-publish-surface-never-ran` is ABSENT from the run's unevaluable
   reasons. So the three earlier FAILs are confirmed as the instrument gap this
   deviation diagnosed and NOT a leg-that-never-draws defect. The stand-down
   above is KEPT AS SHIPPED - it is still the correct reading for the many lanes
   that do not open the map - but it is no longer the state of the whole suite,
   and a pass may read "no RC-OWN finding" as "ownership conserved" on a lane
   that opens the map AND publishes. Note what made this a real test rather than
   a formality: the verb was necessary but not sufficient, since a map-open lane
   whose Director never owned a leg in the window would have published nothing
   and THAT reading would have been a genuine finding. It published.

6. **`seam-endpoint-outside-soi` HAS NOW FIRED LIVE, once, and prior-art gap 3's
   "has never fired" clause is retired.** First raise: run `2026-08-25_1502`
   (V24W reading flight 2, `harness/results/2026-08-25_1502_V24W-duna-one-warp-
   stair_shots/parsek-render-manifest.txt`). It came through the REPORT-ONLY
   channel exactly as designed - absent from `hlib.ANOMALY_TOKENS`, surfaced as
   `anomalySweep.unlistedReasons = ["seam-endpoint-outside-soi"]`, counted in the
   manifest as `anomalyEchoes.seam-endpoint-outside-soi = 1`, and moving no
   verdict (that run's PARSEK-FAIL was the anomaly sweep on three GATED tokens,
   a separate matter). WHERE IT RAISED, because the location is the interesting
   part: the echo sits on the departure-loiter `DWELL` for
   `recId 61e9177193444e329247d0e8288cf91e` (`pidKey 839899670`,
   `ut 5355599259.994832`, `treatment StockConic`, `coverage InSegment`,
   `openAtExport = True`) whose open endpoint is in the `Sun` frame and whose
   close endpoint is at `Duna` - i.e. the heliocentric-to-Duna arrival handoff,
   which is precisely the geometry a cross-SOI endpoint check exists to have an
   opinion about, not an incidental frame. The same record's
   `maxEndpointRatio` context is the run-level `0.4366947421168355` over 1024
   endpoints (an INFO `RC-QUAL` trend row).
   WHAT THIS DOES AND DOES NOT SETTLE. It settles that the instrument is WIRED
   and reachable on real re-aim geometry - the thing a "has never fired" clause
   could not distinguish from dead code. It settles NOTHING about the
   single-ratio calibration, which is the other half of gap 3: one raise on one
   arrival cannot separate a benign SOI-edge endpoint from a defect, and this run
   also carries `decimatedSections.SEAM_ENDPOINT = 292546` plus 41,280 truncated
   and 512 skipped, so the endpoint population it saw is SAMPLED. Do not promote
   the reason into `ANOMALY_TOKENS` off this. The calibration still wants a
   population of raises with their ratios attributed, and the manifests now carry
   per-raise `ANOMALY_ECHO` records with `ut` stamps to build it from offline.

Two further facts a reader of this design needs about what shipped:

- **The manifest is `schemaVersion = 1` and stayed there.** The v1.1 pass added
  only OPTIONAL keys, absent when not measured, which the Python reader already
  tolerated: re-aim launch/destination body names on `PLAN.UNIT`, `detailD` on
  `CLOCK_EVENT` (today only the `descent-phase` head UT), `ownerIndex` and the
  recorded-clock stamps `openLoopUT` / `closeLoopUT` on `DWELL`, and the
  observation-derived `hold-engage` / `hold-release` clock-event kinds. No
  version bump, because absence was already a defined state.
- **`renderComposition` never entered `RESERVED_EXPECTATION_BLOCKS`.** Its
  evaluator ships in the same change as the block (the M-B2 `world` and R9
  `rewind` sole-owner rule), so there is no window in which a spec can declare a
  block nothing reads. DECLARING the block is also what makes the harness set
  `PARSEK_RENDER_MANIFEST=1` at launch, so the declarer set is pinned alongside
  the armed set.

## Risks and open questions

1. **Manifest volume on 20+ routes.** Event-driven capture should hold, but
   the per-pid caps and the Phase 1 budget measurement are the guard, not an
   assumption.
2. **Transition endpoint sampling cost.** Sampling positions at each
   transition touches the effective-segment surfaces; it is per-transition
   (rare), not per-frame, and must reuse the renderer's already-resolved
   surfaces, never re-solve.
3. **KSC host vacuity.** `ParsekKSC` is hard-gated to Kerbin-rooted
   recordings; composition lanes there are meaningful only for
   Kerbin-arrival return subjects (the G2/V20K dependency). The manifest
   records per host; the verifier must not demand KSC records from a
   structurally ineligible subject.
4. **Map renders one instance of an overlapping mission.** Per-instance
   composition is flight-mesh territory; the manifest's overlap coverage is
   the primary/secondary boundary machinery only, stated in RC-CYCLE.
5. **Plan capture on units rebuilt mid-dwell** (route status change, window
   advance): the plan section is per builder signature, so a rebuild appends
   a new plan record; the verifier treats a mid-cycle plan change as a cycle
   boundary for RC-CYCLE purposes.
6. **Where the catalog grows**: a new ratified discontinuity (e.g. the
   deferred MissionComposite seams) must add a manifest record kind + a rule
   + a citation in the same PR, or RC-UNKNOWN will red on it - which is the
   desired failure mode, not a flaw.

## Test plan

- Accumulation core: dwell open/close ordering, cap-and-truncate marker,
  aggregate correctness, InvariantCulture round-trip.
- Constant export: source-sync pin over the exported name set.
- Parser: fail-loud on torn files; node shapes pinned against the C# writer
  (the saveparse discipline).
- Rules: one positive + one violating fixture per rule; recompute-vs-planned
  divergence fixtures for both legs of the independence rule.
- Row: SKIPPED on driver-INVALID; absent-manifest mismatch; gating reachable
  only via the armed allowlist; `--dry-run` enumerates the row armed /
  report-only / facets-only.
- In-game: one `RenderComposition` category cell that arms the recorder via
  a `ForceEnabledForTesting` override (the env var is read once at Awake,
  so a mid-session cell cannot arm it; the override mirrors
  `MapRenderTrace.ForceEnabledForTesting`), plays a synthetic loop fixture
  for two cycles at 1x, exports, and asserts the manifest parses, each
  section is present, and record counts are sane - WELL-FORMEDNESS ONLY.
  The RC-* rules are evaluated in Python alone; asserting RC-COVER in C#
  would encode the coverage rule twice in two languages, the exact
  second-copy drift this doc forbids for the catalog itself.
