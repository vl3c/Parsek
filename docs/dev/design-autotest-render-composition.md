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
discontinuity detector, every in-game parity assertion samples one frame, high
warp is untested by any automated lane, the route overview line surface has
zero scenario declarers, and the one lens that catches "the arc dead-ends one
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
4. "Synthesized never persists" and copy-then-transform immutability are
   enforced by convention only.
5. The `PadAlignLaunch` -> `DestinationArrivalAlign` composition ordering is
   flagged "a real bug if got wrong" with no named test.
6. Nothing asserts continuity ACROSS TIME (all parity is one-frame), nothing
   runs at high warp in any automated lane, and the route overview line
   (registry cell D10) has zero declarers.

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

1. **Plan**: when a host pushes loop units (`ParsekFlight` / `ParsekKSC` /
   `ParsekTrackingStation` `SetLoopUnits` seams), serialize each unit's plan
   once per builder signature: member indices with `MemberStartUT`/
   `MemberEndUT`, `CadenceSeconds`, `OverlapCadenceSeconds`, `PhaseAnchorUT`,
   `LoopCuts`, arrival-hold fields (`ArrivalHoldSeconds`, `ArrivalHoldAtUT`,
   `ArrivalAlignPeriodSeconds`, joint fields, `ArrivalAmberReason`),
   launch-hold fields (`LaunchBodyRotationPeriodSeconds`,
   `LaunchHoldEngaged`, `RecordedSoiExitUT`), descent-trigger fields
   (`DescentMemberIndices`, `RecordedDeorbitUT`, `DescentEndUT`,
   captureShift), `IsReaim` + the re-aim window schedule, and for route-backed
   units the `Route` identity, `BackingMissionTreeId`, `RecordedDockUT`,
   `RecordedOriginUndockUT`, excluded interval keys, and
   `ClassifyRouteScope` result. Also the PRIMITIVE inputs the clock math was
   derived from (body rotation periods, parking period, recorded boundary
   UTs) so Layer 3 can recompute independently.
2. **Chain**: at `ShadowRenderDriver.GetOrBuildChain`, on each build
   (cache-keyed, so once per signature + window index): the `PhaseChain`
   phases with `PhaseKind`, provenance, body, UT bounds, seam kinds, and
   `chainHasReaimedSegments`.
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
   endpoint bodies, dock-clip boundary, and per-frame-aggregate skip counts
   (`IsRenderingNonOrbitalLeg` deferrals).
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

- **Numeric contracts ride the manifest header.** The C# exporter writes the
  live values of every tolerance the verifier needs, keyed by their code
  names: `PhaseSeamClassifier.DefaultTangentToleranceRadians`,
  `SeamEndpointOracle` ratio tolerance, the four bridge bands +
  `BridgeMaxSeamGapSeconds`, `AnchorMaxResidualKm` / `AnchorMaxRelResidual`,
  `SeedFreshnessFrames`, descent member-selection epsilon, loiter-run
  detection thresholds, joint-hold whole-period budget. The Python verifier
  NEVER hardcodes these numbers; it reads them from the header. A C#-side
  source-sync test pins the exported name set against the constants (the
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
  admitted to `RESERVED_EXPECTATION_BLOCKS` alongside `route`/`loop` until
  the first declarer lands, and armed only through the established
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
   is a finding naming which leg.
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
windows (below-atmosphere, member trims, dock clip) and InteriorGap holds
must equal the planned span. Any residual dark window is unexplained.
CitedContract: `GhostPlaybackLogic.DecideUnitMemberRender`,
render-architecture section 11.3.

**RC-SEAM (FAIL)** - every transition classifies into exactly one catalog
item and satisfies its numbers: Rigid within the tangent tolerance;
FlexibleSoi endpoint-vs-SOI-sphere ratio within tolerance AT THE HANDOFF UT
(evaluated per seam kind and per epoch, which retires the single-global-ratio
calibration problem the standalone oracle has); bridges within their band
and never across a body change; SwitchContinuation exempt from position
match by contract. CitedContract: `PhaseSeamClassifier`,
`CrossMemberSeamStitcher`, `SeamEndpointOracle`, render-architecture 6.1/9.1.

**RC-HOLD (FAIL)** - each observed hold matches the recomputed per-cycle
value (arrival hold realigned by the align period; launch borrow repaid at
the recorded SOI exit, netting to zero); a hold is stationary (position at
engage vs release within the float-grid tolerance for that frame); and every
InteriorGap hold is bounded (default bound: the longest planned seam gap in
the chain plus one reseed interval; exceeding it is the "held forever"
defect the current instruments cannot see). CitedContract:
periodicity arrival-flex rules, `design-reaim-launch-hold-seam.md` 2-4,
`GhostRenderDirector` hold contract.

**RC-CUT (FAIL)** - every loiter cut is an exact whole multiple of the run
period; no dwell samples inside a cut; a partial trim appears only in the
flexible SOI-edge region. CitedContract: `ReaimLoiterCompressor`,
periodicity 258-268.

**RC-DESCENT (FAIL)** - trigger UT congruent to the recorded deorbit
rotation phase; descent head monotone and never before `RecordedDeorbitUT`;
at most one descent member rendering at any instant; the landing dwell's
terminal position within tolerance of the recorded site. CitedContract:
`DescentTrigger`, the S4 recorded-site invariant (ratified 2026-07-07).

**RC-CYCLE (FAIL)** - cycle N and cycle N+1 have isomorphic dwell/transition
structure (same sequence of segment ids, treatments, seam kinds) and
zero accumulated per-cycle drift in the clock checks; boundary-overlap
secondary appears only when the plan's raw delta exceeds the capped advance,
and hands off to the next primary without a gap. CitedContract: periodicity
zero-drift rules, `ComputeBoundaryOverlapAdvanceSeconds`.

**RC-ROUTE (FAIL)** - nothing renders past `RecordedDockUT`; the overview
line's kept/dropped leg accounting matches the scope classification
(same-body draws all non-orbital legs, inter-body drops exactly the
non-endpoint-body legs, round-trip stands down, malformed draws nothing); no
frame where both the overview line and the ghost polyline drew one member's
leg. CitedContract: logistics section 0, `RouteTrajectoryLineRenderer`.

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
- **Scenario ids**: none reserved here; composition lanes extend existing V
  specs with a manifest step + `[expectations.renderComposition]` block
  rather than minting a parallel lane family. New ids, if ever needed, get
  reserved in the roadmap's register like everything else.
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
  calibration problem (RC-SEAM evaluates per seam kind at the recorded
  handoff epoch, with the plan in hand); the "watch it and squint" operator
  step for composed renders.
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
- In-game: one `RenderComposition` category cell that arms the env flag,
  plays a synthetic loop fixture for two cycles at 1x, exports, and asserts
  the manifest parses and RC-COVER holds - the smoke path that keeps the
  C# half honest between harness flights.
