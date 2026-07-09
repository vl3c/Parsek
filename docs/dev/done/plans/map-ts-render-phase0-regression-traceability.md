# Phase 0 Regression Traceability: §11.5 matrix row -> scenario

> **STATUS: CURRENT (Phase 0 DoD artifact).** This table is the migration plan's required
> "scenario -> design-§11.5-matrix-row traceability" deliverable
> ([`map-ts-render-overhaul-migration.md`](map-ts-render-overhaul-migration.md) §2). Every situation in
> §11.5 of [`../design-map-ts-render-architecture.md`](../../design-map-ts-render-architecture.md) maps to
> EITHER a headless regression scenario (a named xUnit test), OR a dedicated in-game scenario/test, OR an
> explicit out-of-scope entry with a one-line reason. No row is left unmapped, so coverage holes cannot
> pass silently.

---

## The instruments this table maps onto

The Phase 0 safety net is four cooperating pieces (the parity diff is the same oracle in every case; only
the reference set + the capture surface differ):

1. **`RenderParityOracle`** (`Source/Parsek/MapRender/RenderParityOracle.cs`) - the pure recorded-vs-
   rendered (faithful) / intended-vs-rendered (synthesized) geometry-diff. Unit-tested by
   `Source/Parsek.Tests/RenderParityOracleTests.cs`.
2. **`RenderGeometrySampler`** (`Source/Parsek/MapRender/RenderGeometrySampler.cs`) - the pure reframing /
   flattening / sample-UT helpers. Unit-tested by `Source/Parsek.Tests/RenderGeometrySamplerTests.cs`.
3. **`MapRenderProbe.TrySampleAndEmitFaithfulOrbitParity`** - the WIRED Unity capture path that samples a
   live ghost's rendered orbit + recorded reference and emits the `parity-drift` Tier-C anomaly. It builds
   the recorded reference PHASE-MATCHED to the live rendered orbit (its epoch baked with the SAME loop
   shift `StockConicTreatment.SeedAndDriveLive` used, via `BuildPhaseMatchedReferenceOrbit`) and samples
   BOTH orbits at the SAME UTs, so a faithful LOOP ghost reads ~0 drift instead of a false
   orbit-diameter drift. The pure-orchestration core (`MapRenderProbe.ComputeFaithfulOrbitParity`,
   returning a `FaithfulParitySample`) is the seam the in-game baseline test calls directly. The Unity
   capture is validated by the in-game `RenderParitySamplerFixtureTest` (capture-harness) and the in-game
   `RenderParityBaselineTest` (positive zero-drift gate, with a LOOP-SHIFTED variant that drives a
   non-zero baked loop shift end-to-end through the real seam and asserts zero drift).
   (2026-07-04 stack-review B1 correction: the live lens was silently SKIPPING loop-shifted ghosts in
   production - the covering-segment lookup ran at the director icon-drive clock, loopShift past the
   recorded span; the faithful lens now looks the recorded segment up on the RECORDED clock,
   `ResolveFaithfulLookupUT` = currentUT - loopShift, and a DIRECTOR-convention in-game loop baseline
   pins the production wiring - the earlier loop-shifted variant hand-fed the recorded clock and could
   not catch this.)
4. **The HEADLESS regression scenario set** (`Source/Parsek.Tests/RenderParityRegressionScenarioTests.cs`) - representative synthetic geometry per §11.5 row, asserting the oracle's VERDICT (known-good -> zero
   drift; deliberately drifted -> drift flagged), in both faithful and synthesized modes.

**How a "scenario" maps to a row.** The oracle's headless contract takes already-framed flat `double[]`
XYZ triples, so a headless scenario models a row's GEOMETRY directly (an orbit via a pure Kepler
circle/ellipse, an ascent/descent via a vertical metre profile, a heliocentric arc via a wide circle, a
re-aim/draw bug via a rotation/offset). The live Unity capture path (reading `OrbitDriver.orbit` /
`GetWorldPos3D`) is exercised by the two in-game tests, NOT re-modelled per row in the headless suite.

**Tolerance-calibration caveat (carried from the migration plan).** The zero-drift baseline is captured
on KNOWN-GOOD scenarios ONLY. Today's output carries open bugs (icon-off-orbit, warp-reseed-lag,
depot-double, sub-surface-ghost-not-retiring); those are tracked as **expected-to-change**, NOT baselined,
so a later incidental fix does not trip `parity-drift` and read as a regression. Where a §11.5 row is one
of those documented-buggy classes, the table marks it expected-to-change and points at the headless
DRIFTED scenario that pins the failure shape (so the eventual fix has a target), without adding it to the
zero-drift baseline.

---

## Runtime-only rows (synthetic recordings cannot exercise these)

The migration plan calls out six row classes that a synthetic recording / headless diff cannot exercise
because they are about KSP runtime LIFECYCLE / CLOCK / SCENE state, not geometry. Each needs a DEDICATED
in-game scenario. They are listed here once and cross-referenced from the table:

| Runtime-only concern | Why headless cannot cover it | Phase-0 status |
|---|---|---|
| Cold-load, Planetarium UT=0 | `liveUT <= 0` defer-render guard fires only on a real cold `OnLoad` frame | **In-game scenario REQUIRED** (deferred to the producer's phase; see below) |
| Quickload mid-record/loop/gap | Director cold-start with no stale prior intent needs a real quickload | **In-game scenario REQUIRED** |
| Scene transition mid-segment / mid-seam (no one-frame blank; flight-map<->TS parity) | proto re-seed settle is a multi-frame runtime behavior | **Partially covered now** by the FLIGHT + TRACKSTATION `RenderParityBaselineTest` variants (render parity across the two scenes); the no-one-frame-blank settle is producer-phase in-game |
| Warp through `HoldPhase` / compressed loiter / descent re-anchor | a single high-warp frame advancing `liveUt` across a hold is a real-clock event | **In-game scenario REQUIRED** (HoldPhase is a Phase-1+ type; no producer yet to test in Phase 0) |
| Dock-merge retire at `dockUT` | the make-before-break member swap is a presence-lifecycle runtime event | **In-game scenario REQUIRED** |
| Overlap gap-hold | the single-rendered-ghost-holds-prior-intent symptom is a per-frame runtime state | **In-game scenario REQUIRED** |

**Phase-0 disposition of the runtime-only rows.** Phase 0 builds the OBSERVER (oracle + sampler +
tracer skeleton), not the producers, so most runtime-only rows have no Phase-0 code to assert against yet.
Their dedicated in-game scenarios are authored **in the phase that introduces the producing code** (the
plan's "everything runtime-only gets an in-game test" DoD applies per phase): cold-load UT=0 / quickload /
warp-through-HoldPhase land with the `PlaybackResolver` (Phase 3, §11.2/§11.3); dock-merge retire and
overlap gap-hold land with the presence-lifecycle re-home (Phase 3/4, §11.1/§11.3). Phase 0 records the
REQUIREMENT here so the hole is visible, and ships the one runtime row it CAN cover now: flight-map<->TS
render parity, via the two-scene `RenderParityBaselineTest`.

---

## §11.5 coverage matrix -> Phase-0 scenario mapping

Legend: **headless** = a named test in `RenderParityRegressionScenarioTests`. **in-game** = a named
`[InGameTest]`. **oos** = out-of-scope-ok (with reason). **expected-to-change** = documented-buggy,
pinned but not baselined. **runtime-only** = needs a dedicated in-game scenario in the producing phase.

| § Situation | Mapping | Scenario / test (or reason) |
|---|---|---|
| Staging/decoupler debris (`IsDebris`) | oos | Excluded upstream by the `IsDebris` gate; the factory/resolver is never invoked, so there is no rendered geometry to diff. |
| Controlled-decoupled child (lander off a stage) | headless (faithful) + in-game capture | Faithful body-relative orbit arc: `Faithful_BgOnRailsAllOrbital_KnownGood_ZeroDrift` covers the all-orbital member shape; the `ParentAnchoredChild` dual-surface (>=2-sample / out-of-range-retire) routing is a producer-phase in-game (Phase 1/3). |
| Docking (two -> one at dockUT) | runtime-only | Dock-merge retire-at-`dockUT` (see runtime-only table); presence-lifecycle in-game in Phase 3/4. |
| Undocking / EVA split (one -> N) | runtime-only | Member-chain begin/end at `splitUT` is a presence-lifecycle event; in-game in the producing phase. |
| EVA kerbal | oos (Phase 0) | Renders as a faithful `TracedPath`/`SuppressedMarker` member; its geometry is the same faithful-arc / vertical-profile shape already covered by `Faithful_*`; no distinct Phase-0 geometry. |
| Asteroid/comet claw grab+release | oos (Phase 0) | Generic couple/decouple seam - same member-swap lifecycle as dock/undock; no distinct Phase-0 geometry. |
| Structural breakup (`onPartJointBreak`) | oos | Debris excluded; the controlled survivor is an ordinary member (covered by `Faithful_*`). |
| Reentry burn-up / explosion / destruction mid-recording | expected-to-change | The "sub-surface-ghost-not-retiring" bug class - mid-window terminal retire is a Phase-6 fix. Pinned (geometry-side) by `Faithful_DescentArcOffset_FlagsDrift`; NOT baselined. The retire trigger itself is a presence-lifecycle in-game in Phase 6. |
| Vessel recovery (KSC / flight Recover) | oos | Terminal-eligibility gate + live-vs-ghost dedup keep recovered recordings out of presence; no rendered geometry. |
| Cross-tree dock (foreign-tree vessel) | oos (deferred §17) | `LiveVesselAnchor` cross-tree composition is deferred; fail-closed in v1. |
| Fly from TS / KSC marker / Map Switch-To | runtime-only | `StockActionIntent` teardown-before-Fly is a navigation/scene lifecycle event; existing switch in-game tests + producer-phase coverage. |
| In-bubble vessel switch (`[`/`]`) | runtime-only | guid-gated dedup + chain-cache invalidation is a runtime active-vessel-change event. |
| Re-Fly / Rewind supersede mid-session | runtime-only | ERS-exempt render; effectiveness via the ProtoVessel lifecycle - a presence-lifecycle runtime event. |
| Rewinding a looped mission | runtime-only | `LoopUnitSet` rebuild + cache invalidation on a post-split index change - runtime. |
| Quicksave/quickload mid-record/loop/gap | runtime-only | Director cold-start, no prior intent (see runtime-only table). |
| Cold load, Planetarium UT=0 | runtime-only | `liveUt <= 0` defer-render guard (see runtime-only table). |
| Load a different save / new game | oos | ProtoVessel teardown + schema-compat reject; no rendered geometry to diff. |
| Scene transition mid-segment / mid-seam | runtime-only (partial now) | No-one-frame-blank settle is producer-phase in-game; **flight-map<->TS render parity is covered now** by `RenderParityBaselineTest` (FLIGHT + TRACKSTATION variants). |
| Camera focus / target a ghost across swap | oos (partial, §11.2) | `SuppressedMarker` not targetable / re-home focus anchor - a scene-adapter lifecycle concern, no geometry diff. |
| Warp through HoldPhase / loiter / descent re-anchor | runtime-only | `HoldPhase.CoversUt` warp-step-safe (see runtime-only table); HoldPhase has no Phase-0 producer. |
| Warp-reseed-lag at SOI / orbit-raise gaps | expected-to-change | The documented warp-reseed-lag bug class - `InInteriorGap` hold + SOI-block + CoMD re-snap. NOT baselined; the wrong-reseed shape is pinned by `Faithful_WrongBodyOrbit_DifferentScale_FlagsDrift`. |
| Warp across a re-aim synodic window boundary | runtime-only | `\|w{window}` chain-signature token invalidation - a runtime cache event. |
| Loop a single recording | headless (faithful) | `Faithful_LoopShiftedGhost_SameOrbit_ZeroDrift` (loop shift sets phase, not shape -> zero drift). (2026-07-04 stack-review B1 correction: the LIVE faithful lens was blind to loop ghosts until the recorded-clock lookup fix; live loop measurement is now pinned by the DIRECTOR-convention in-game loop baseline.) |
| Loop a whole multi-member mission | headless (faithful) | N independent `PhaseChain`s - each member's faithful arc is covered by `Faithful_*`; `Faithful_LoopShiftedGhost_SameOrbit_ZeroDrift` exercises the per-member loop invariant. (2026-07-04: the same B1 recorded-clock-lookup correction applies to each loop member's LIVE measurement.) |
| Looping mission while player flies live nearby | oos (Phase 0) | Disjoint tree indices - a presence-selection concern; the rendered geometry of each loop member is the faithful-arc case already covered. |
| Overlap (N instances) | runtime-only | One ghost at the selected cycle head; the gap-hold caveat is a per-frame runtime symptom (see runtime-only table). |
| Loiter compression / arrival hold / loiter trim | headless (synthesized) | Span-clock transforms reshape WHERE/WHEN, not the arc; `Synthesized_ReaimedLoopIntendedArc_KnownGood_ZeroDrift` covers a trimmed/transformed intended arc drawn faithfully. |
| Chain continuation across vessel switches | oos (deferred §6.1/§17) | `SwitchContinuation` seam is deferred to `MissionComposite`; fail-closed in v1. |
| Mixed faithful + re-aimed members | headless (synthesized) | `Synthesized_MixedFaithfulReaimedMembers_IntendedArc_ZeroDrift` (the re-aimed member's intended arc) + `Faithful_*` (the faithful members). |
| Re-aim with a large synodic target move | headless (synthesized) | `Synthesized_ReaimedLoopIntendedArc_KnownGood_ZeroDrift` (good draw) + `Synthesized_ReaimedArc_DrawnRotated_FlagsDrift` (draw-fidelity bug). |
| Synthetic RouteBackingMission, trimmed window | headless (synthesized) | The mid-recording window clip reshapes the visible span; the clipped intended arc drawn faithfully is the `Synthesized_*` known-good case. |
| Debris shed on a re-aimed/generated transfer leg | oos | `ParentGeneratedConicAnchor` fail-closed in v1; does not render on its parent's generated arc. |
| BG-on-rails recorded vessel | headless (faithful) | `Faithful_BgOnRailsAllOrbital_KnownGood_ZeroDrift` (all-orbital chain, no Descent/Surface phase). |
| Atmospheric descent to landing/splashdown | headless (faithful + synthesized) | `Faithful_DescentToLanding_KnownGood_ZeroDrift` (faithful descent), `Synthesized_DescentReStitchIntendedG1Arc_KnownGood_ZeroDrift` (the Phase-6 re-stitch intended arc), `Faithful_DescentArcOffset_FlagsDrift` + `Synthesized_DescentReStitch_SeamDiscontinuity_FlagsDrift` (drift). |
| Prelaunch / landed / splashed / rover | oos (Phase 0) | `SurfacePhase` + `SuppressedMarker`; terrain-aware altitude is a flight-adapter concern out of v1, and the rendered indicator is a marker not a diffable arc. |
| Suborbital hop | headless (faithful) | Ascent -> ballistic -> descent within one body: `Faithful_AtmosphericAscent_KnownGood_ZeroDrift` + `Faithful_DescentToLanding_KnownGood_ZeroDrift` cover the traced portions. |
| Aerobraking (many periapsis grazes) | headless (faithful) | `Faithful_AerobrakingEccentric_KnownGood_ZeroDrift` (the eccentric conic arc shape) + `Faithful_AerobrakingWrongEccentricity_FlagsDrift` (wrong-ecc drift). |
| Escape / hyperbolic / Sun-escape | headless (faithful) | `Faithful_SoiCrossing_KnownGood_ZeroDrift` (the heliocentric/open-arc faithful case; the oracle's hyperbolic half-span fallback is also unit-covered). |
| Single-level SOI transition (Kerbin->Mun, ->Sun) | headless (faithful) + in-game | `Faithful_SoiCrossing_KnownGood_ZeroDrift` (post-crossing heliocentric arc in the new body's frame); the live cross-body-suppression frame is exercised by the in-game capture/baseline. |
| Moon-rich / nested-SOI Jool tour | oos (faithful fail-closed) | Synthetic moon-to-moon re-aim is fail-closed in v1 (renders recorded faithfully); the faithful per-leg arc is the `Faithful_*` case. |
| Multiple ghosts at different bodies | oos (Phase 0) | Independent per-body `PhaseChain`s - each ghost's arc is an independent faithful diff already covered; the multi-ghost selection is a presence concern. |
| Ghost at a never-visited body | in-game | Renders normally (discovery level does not block `CelestialBody` resolution); the `BodyAnchor`-resolves-for-a-stock-body path is exercised by the in-game baseline (Kerbin resolves regardless of visit state). Only a MISSING body name fails (fail-closed, no NRE), a producer-phase in-game. |
| Career-economy: ghost must not corrupt the ledger | oos | The `ghostMapVesselPids` guard-timing invariant is a presence/ledger concern, not a render-geometry diff (§12 must-not-regress). |
| TS automatic ghost presence | in-game | `DiscoveryLevels.Owned` + forced `VesselType` - exercised by the TRACKSTATION `RenderParityBaselineTest` variant (a ghost renders and reports zero drift in the TS scene). |
| Map filters / `MapView.fetch==null` cold-start | oos | KSP-API render gate (no-op when `MapView.fetch == null`); a §12 must-not-regress boundary condition, no geometry diff. |

---

## Coverage summary

**45 §11.5 rows total** (every row mapped; the categories below sum to 45). The headless suite backing the
mappings is **20 `[Fact]`s** in `RenderParityRegressionScenarioTests` (a row can map to more than one test - e.g. the descent row maps to four - so the test count exceeds the row count it serves).

- **Headless-mapped rows: 13** - 6 faithful, 4 synthesized, and 3 combined headless+in-game. Covers
  ascent / orbit / descent / suborbital / aerobraking / escape / SOI-crossing / BG-on-rails / loop /
  re-aim / mixed-members / per-scenario-tolerance / no-measurement-safety, known-good AND drifted, both
  oracle modes.
- **In-game scenarios (Phase 0, shipped now):** flight-map<->TS render parity (the 2-scene
  `RenderParityBaselineTest`), TS automatic ghost presence, ghost-at-a-stock-body resolution, and the
  capture-harness (`RenderParitySamplerFixtureTest`). Two rows are in-game-primary; three more pair an
  in-game leg with a headless leg.
- **Runtime-only rows (dedicated in-game scenario REQUIRED, authored in the producing phase): 12** - cold-load UT=0, quickload mid-gap, dock + undock/EVA member swaps, Fly/Switch-To, in-bubble switch,
  Re-Fly/Rewind supersede, rewind-a-loop, overlap gap-hold, warp-through-HoldPhase, warp across a synodic
  boundary, and the scene-transition no-blank settle (whose flight-map<->TS-parity half ships now).
- **Expected-to-change (documented-buggy, pinned but NOT baselined): 2** - reentry/destruction
  mid-recording retire (Phase-6 fix) and warp-reseed-lag; the icon-off-orbit rotation class is
  geometry-pinned by `Faithful_LoopRotationBug_OffOrbitArc_FlagsDrift`.
- **Out-of-scope-ok (with reason): 18** - debris-excluded, deferred-to-§17 composition,
  presence/selection/ledger concerns with no diffable geometry, EVA/claw member-swap lifecycle, and
  marker-only (`SuppressedMarker`) surfaces.

No row is unmapped. The runtime-only rows are explicitly flagged so a later phase cannot ship its
producing code without the corresponding in-game scenario (the per-phase DoD enforces it).
