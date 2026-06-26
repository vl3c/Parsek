# Design: Unified Trajectory & Map/TS Render Architecture

> **STATUS: CURRENT** — the single design for Parsek's trajectory/phase model and the map +
> Tracking-Station ghost render. This doc **fully supersedes** the former
> `design-map-ts-render-tracer.md`, `parsek-ghost-trajectory-rendering-design.md`, and the
> `docs/dev/plans/maprender-*` / `map-ts-render-rewrite-phases.md` plans (the two design docs are
> marked SUPERSEDED in place pointing here; the maprender plans remain as historical implementation
> records). **Scope this cycle:** map + Tracking-Station implementation; the model is
> designed to be flight-adoptable, with the flight 3D-mesh path as an explicit later phase (§17).
> Derived from a read-only research sweep, a code-fidelity + architecture review, and a
> gameplay-completeness review. The detailed phased **migration plan is a separate follow-up doc**;
> §16 is the outline.

> **Template map** (per `development-workflow.md` §3): Problem = §1 · Terminology = §2 · Mental
> Model = §3 · Scope = §4 · Data Model = §5–§7 · Behavior = §8 · Producers = §9 · SOI/moon-rich =
> §10 · Edge Cases = §11 · What Doesn't Change = §12 · Backward Compatibility = §13 · Diagnostic
> Logging + Parity Oracle = §14 · Test Plan = §15 · Migration outline = §16 · Deferred = §17.

---

## 1. Problem & motivation

Parsek renders the recorded trajectory of past missions as "ghost" overlays in the **flight map**,
the **Tracking Station**, and (separately) the **flight 3D scene**. The map/TS render path was
already rewritten once into a clean modular pipeline (`Parsek.MapRender`) and that rewrite is
**design-complete and cut over** — it is NOT greenfield. But three structural problems remain, and
they are what this design addresses:

1. **The cutover is incomplete — ownership is data, not control.** A pure *decision* pipeline
   (`ShadowRenderDriver → ChainAssembler → ChainSampler → GhostRenderDirector → treatments`) decides
   what every ghost should look like, but a *legacy draw* pipeline (`GhostOrbitLinePatch` ~1,443 LOC
   + the autonomous `GhostTrajectoryPolylineRenderer.Driver` + IMGUI markers) still paints the
   pixels. The two are joined only by per-pid side-channel dicts (`seedByPid`, `tracedPathByPid`,
   `drewNonOrbitalLegRecordings`, `ghostsWithSuppressedIcon`) read with a ±2-frame freshness
   tolerance (`SeedFreshnessFrames = 2`). The treatments' `Apply`/`StandDown` are nearly inert. This
   is the single biggest source of map-render bugs and the hardest thing to debug. (Of these four,
   `seedByPid` is the one structural *survivor* of the cutover — KSP owns the stock icon-drive site, so
   the MANAGED `StockConic` treatment must keep a per-frame seed hand-off, §4/§8; the other three are
   eliminated.)

2. **The render atom is inert — every load-bearing distinction is re-derived from geometry.** The
   atom is a `readonly struct RenderSegment` keyed by a 2-value `Treatment` enum, a **cosmetic**
   `SegmentKind` (10 declared, only `Transfer`/`Loiter`/`Surface` ever assigned), and a bare
   `string FrameBodyName`. Which *phase* a segment is, whether it is *recorded or synthetic*, which
   *reference frame* it lives in, and what *continuity* its seam needs are all re-computed from raw
   geometry at three sites or threaded as 5-argument boolean predicates
   (`ShadowRenderDriver.ShouldSkipReaimSegment`). Provenance is decided by **reference equality**
   (`ReferenceEquals(effective, recorded)`) plus a body-name re-match — brittle.

3. **The phase vocabulary is missing.** The user's mission model is a composition of named phases —
   ascent, departure/destination loiter, SOI departure/arrival, SOI-exit/entry holds, heliocentric
   transfer (planet→planet, heliocentric-park→planet, →station), descent/landing — joined by seams.
   None of these is a first-class object; "phase identity" is split across **four** non-authoritative
   vocabularies (render `SegmentKind`, `ReaimMissionPlan` fields, `DescentHeadPhase`, the recorder's
   `SegmentPhase`/`SplitEnvironmentClass`). And several phases the model demands have **no producer**:
   the cross-SOI continuous chain, the orbit↔landing re-stitch, nested-SOI (Jool) navigation, and
   heliocentric-object (station) rendezvous.

**Goal:** one composable OOP model — phases as first-class polymorphic objects that own their
provenance, frame, seam contract, and draw — that the map and Tracking Station consume through a
single owned pipeline (no side-channel), with the model **designed to be flight-adoptable later**.
Make the recorded-vs-synthetic boundary an explicit, typed decision rather than scattered geometry
re-derivation. Define every phase in the user's taxonomy (so the model is complete and Jool/station
have a home) while building only the highest-value missing producer (descent re-stitch) in v1.

---

## 2. Terminology

- **Ghost** — a rendered overlay of a past recording (one orbit icon + line, or one polyline + marker).
- **Recording / member** — a stored trajectory (`Recording`). A mission spans several recordings
  (launch → transfer → landing) sequenced by the span clock.
- **Span clock** — the time-axis machinery (`GhostPlaybackLogic`: `ResolveTrackingStationSampleUT`,
  `TryComputeSpanLoopUT`, `LoopUnit`/`LoopCut`/`CompressSpanUT`/`InsertHold`) that maps a live UT to
  an assembled-chain UT, handling looping, loiter compression, and hold insertion. **Already shared**
  by flight, KSC, TS, and map-presence — the proven cross-scene contract.
- **Treatment** — *how* a segment is drawn: `StockConic` (KSP's own orbit line + icon, seeded by us)
  vs `TracedPath` (our Vectrosity polyline) vs (new) `SuppressedMarker` (an IMGUI marker when there
  is no conic to seed).
- **Provenance** — *where the geometry came from*: recorded, finalize-time-predicted,
  playback-time-synthesized, or faithful-fallback.
- **Phase** — a named composable trajectory unit (ascent, transfer, descent, hold, …). The new
  first-class abstraction this design introduces.
- **Seam / stitch** — the join between two adjacent phases, carrying a continuity contract (G0/G1).
- **Faithful** — replaying recorded geometry exactly (no re-aim). **Re-aimed** — a looped window
  whose heliocentric leg is re-solved to hit the moved target.
- **Fail-closed-to-faithful** — when a synthetic producer is unsupported, fall back to exact recorded
  replay rather than render a broken/synthetic guess.
- **MANAGED vs OWNED treatment** — OWNED = the treatment draws the pixels itself (TracedPath, marker).
  MANAGED = KSP draws (the stock orbit icon/line) and the treatment can only *seed/re-assert* state
  every frame (StockConic) because KSP re-propagates it at the live clock.

---

## 3. Mental model — the layered architecture

The design is a strict top-down layering. Each layer depends only on the ones below it. The existing
proven kernels (`OrbitSegment`, the span clock, the `Reaim/*` solvers, `OrbitArcSampler`,
`ChainAssembler`'s orbit-vs-polyline decision) are **kept and become the implementation** of the new
types — this is a re-typing and consolidation, not a rewrite.

```
LAYER 0 — PRIMITIVES (pure, Unity-free, KSP-API-free where possible)
  ITrajectorySample            split the frame-overloaded TrajectoryPoint into typed views
  SegmentProvenance (NEW enum) Recorded | FinalizedPredicted | Synthesized | FaithfulFallback
  AnchorFrame (NEW union)      Body | ParentGeneratedConic | LiveVessel | RecordedAnchor
  ReferenceFrame, SegmentEnvironment   (EXIST — reused)

LAYER 1 — TYPED SEGMENT VIEW  (in-memory typed read-view over the EXISTING substrate)
  OrbitalState (= OrbitSegment, reused)   the universal Keplerian currency
  ITrackSegmentView                        a typed read-view over TrackSection's 3 nullable lists
  IPlaybackTrajectory (EXISTS, reused)     the ~10-field playback slice of the Recording god-object
  --- NOTE: the persisted substrate (Recording / TrackSection / OrbitSegment structs) is NOT
      retyped here. See §4 (substrate out of scope) and §13.

LAYER 2 — PHASE  (the named composable unit — the heart of this design)
  TrajectoryPhase (abstract) -> Ascent | DepartureLoiter | SoiDeparture | HeliocentricTransfer
                              | SoiArrival | ArrivalLoiter | Descent | Surface | Hold
  PhaseSeam (NEW)             SeamKind + ContinuityOrder(G0/G1) + SoiCrossing? + onCamera
  a phase OWNS: its provenance, its anchor frame, its treatment choice, its seam contracts

LAYER 3 — COMPOSITES
  PhaseChain        (= GhostRenderChain++)  ordered phases for ONE (member, cycle-instance)
  MissionComposite  (NEW — DEFINED in full; v1 builds only the minimal cross-member stitcher)
                    owns CROSS-MEMBER seams (descent re-stitch, launch-rotation)
  NestedSoiSubtree  (NEW — DEFINED; v1 fail-closed)  moon-rich Jool body tree

LAYER 4 — ONE OWNED ENGINE + PER-SCENE ADAPTERS
  PlaybackResolver  (= ShadowRenderDriver, renamed)   resolve -> assemble -> sample -> decide
  GhostRenderDirector (EXISTS, reused)                 the single decision owner
  IPhaseTreatment    StockConic(MANAGED) | TracedPath(OWNED) | SuppressedMarker(OWNED)
  ISceneAdapter      MapViewScene | TrackingStationScene | (later) FlightMeshSceneAdapter
                     scene.Apply(intent) replaces the legacy Harmony-patch side-channel
```

**The one structural move that matters:** LAYER 2 makes the phase a first-class polymorphic object
(the GMAT "command-owns-behavior" pattern) that owns its provenance/frame/seam/draw, replacing the
`Treatment` enum + assembler `if/else` + director `switch` + 5-boolean predicates + reference-equality
provenance. Adding a new phase or treatment becomes one new subclass, not a five-site edit.

---

## 4. Scope (v1) — what we build vs define

> **Non-negotiable v1 acceptance criteria (gate every migration step, §16):**
> 1. **Do not regress anything that already works.** The §16 migration is parity-gated and reversible —
>    no working draw surface is deleted until the §14 parity oracle is green on it; §12 lists the
>    must-not-regress invariants.
> 2. **Full logging coverage through `MapRenderTrace`** (§14) — every new phase / treatment / seam /
>    provenance / anchor-resolve / lifecycle / retire / fail-closed event integrates with the existing
>    tracer's Tier-A/B/C model. If it isn't logged, it isn't done.
> 3. **Full test coverage** (§15) — every new pure method gets an xUnit test; everything runtime-only
>    gets an in-game test. A step is not complete until both are green.

| Area | Decision | Rationale |
|---|---|---|
| **Scenes** | **Map + Tracking Station only.** Flight 3D-mesh untouched; model designed flight-adoptable. | Map+TS already share the pure pipeline; flight is a different world (live GameObject in floating-origin space) whose geometry source (`.pann`) is unimplemented. |
| **Cutover** | **Full collapse of OWNED surfaces.** TracedPath polyline + IMGUI markers + the 430-line line-visibility cascade become thin `scene.Apply(intent)` shims; delete `tracedPathByPid` + `drewNonOrbitalLegRecordings`. **StockConic stays permanently MANAGED**: keep `seedByPid` (re-homed as the re-assert channel) and `ghostsWithSuppressedIcon` (re-homed as the `SuppressedMarker` tier). | KSP re-propagates the stock icon at the live clock every FixedUpdate; the per-frame re-seed is structurally unavoidable. "Full collapse" applies to surfaces Parsek itself draws. |
| **Phase model** | **Polymorphic `TrajectoryPhase` hierarchy.** Replaces the struct+enum+switch+predicates. **No object pooling in v1.** | Chains are cached by `BuildChainSignature` (not rebuilt per frame), so phase-object allocation is an amortized chain-build cost, not a frame-loop cost. Add pooling only if a profile shows chain churn. |
| **Substrate** | **Out of scope.** Keep `Recording` / `TrackSection` / `OrbitSegment` / `TrajectoryPoint` as-is. | Their struct value-copy semantics are load-bearing for `RecordingOptimizer.SplitAtUT` and the Re-Fly `RecordingTreeSplitter` rollback. The phase layer reads them via a typed *view*; no retype needed. God-object split is a separate later effort. |
| **Descent re-stitch** | **v1 BUILD.** First-class `Descent` phase + a **minimal cross-member seam-stitcher** for the G1 orbit↔landing join. Full `MissionComposite` is *defined* in the model; only the minimal stitcher is *built*. | Highest-value visible fix (every landing). The seam is cross-member; the minimal stitcher is the smallest owner that makes it buildable, absorbable by the future `MissionComposite`. |
| **Cross-SOI ~62° kink** | **Define-only, no v1 fix.** Define `SoiCrossing`; render current `FlexibleSoi` behavior. | No bad bugs observed in current state; out of scope for this overhaul. The real whole-patched-conic-chain synthesis is a separate test-gated effort (built+reverted #1169–#1171; needs a clean direct no-park no-moon Kerbin→Duna validatable recording first). |
| **Nested-SOI / Jool** | **Define `NestedSoiSubtree`; v1 fail-closed-to-faithful.** | Recorded Jool tours already render faithfully; the only thing fail-closed disables is synthetic moon-to-moon re-aim, which doesn't exist. Generalize the 1-hop / `MaxConstrainedMoons=1` / 8-patch caps later. |
| **Station rendezvous** | **Define the moving-target phase; v1 fail-closed-to-faithful.** | Least-supported phase; the classifier requires a body target. Recorded approaches render faithfully; build the synthetic moving-target producer later. |

**Also built in v1 (not a row above):** the heliocentric-park→planet re-aim variant (§6) — a shipped
distinct producer (master-list item #10), not deferred. **Deferred known gap:** single-recording re-aim
*ascent re-time* — the `AscentPhase` class exists but its producer fail-opens (§11), so item #1 (ascent)
is not rendered for the single-recording re-aim path in v1.

---

## 5. Layer 0 — primitives & the recorded-vs-synthetic types

### 5.1 `SegmentProvenance` (NEW enum) — collapses the overloaded `isPredicted`

`OrbitSegment.isPredicted` is overloaded today (re-aim synthetic = `false`; recorded-extrapolated
tail = `true`). Replace the *render-view* provenance signal with an explicit enum (the persisted
struct is unchanged — see §13):

```
enum SegmentProvenance {
  Recorded,            // exact recorded geometry, immutable
  FinalizedPredicted,  // predicted-tail written ONCE at scene exit, then immutable
  Synthesized,         // re-aim / re-time / re-rotate, IN-MEMORY ONLY, never persisted
  FaithfulFallback     // a producer was unsupported -> we chose exact recorded replay
}
```

`Synthesized` is a **derived, in-memory-only** flag. It must never reach disk (§13). Provenance is
set **by the producer** that emits the phase (not re-derived by reference equality downstream).

### 5.2 `AnchorFrame` (NEW discriminated union) — kills the body-name string

`RenderSegment.FrameBodyName` is a bare string and the only render-layer notion of "which frame."
The depot-double / dock-renders-absolute bug exists because a string can't say "anchored to a *live
vessel* that is the same craft as a recorded anchor." Replace with:

```
abstract AnchorFrame
  BodyAnchor(string bodyName)                      // the v1 common case
  ParentAnchoredChild(RecordingId parentId)        // controlled-decoupled lander/probe (IsDebris=false)
  ParentGeneratedConicAnchor(PhaseId parentConic)  // moon riding a generated Jool-centric arc
  LiveVesselAnchor(Guid launchGuid)                // rendezvous / docking to a live same-craft vessel
  RecordedAnchorTrajectory(RecordingId anchorId)   // non-loop relative sections
```

v1 implements `BodyAnchor` + `ParentAnchoredChild` + (for fail-closed station/Jool) the *type* of the
others. Resolution contracts that must be honored (from the CLAUDE.md parent-anchored / Relative-anchor
invariants):

- **`ParentAnchoredChild`** (the controlled-decoupled lander/probe — `IsDebris = false`,
  `ParentAnchorRecordingId != null`; it passes the `IsDebris` gate and *does* render): the faithful phase
  reads `TrackSection.bodyFixedFrames` as the **PRIMARY** surface (`AbsoluteSample`: degrees lat/lon/alt +
  `srfRelRotation`) and `frames` (anchor-local metres) as the **SECONDARY/fallback** for loop-anchored
  chains. Body-fixed primary requires **≥2 samples** and a playback UT **inside the `bodyFixedFrames`
  endpoint range**; an out-of-range UT must **RETIRE** the ghost — never clamp to a stale child offset.
- **`RecordedAnchorTrajectory` under loop / re-aim:** the contract splits — a **non-loop** Relative
  section resolves through the recorded anchor trajectory (`TrackSection.anchorRecordingId`), while a
  **loop** Relative section stays on the live-PID contract (`Recording.LoopAnchorVesselId`). When a
  re-aimed/looped mission shifts the anchor recording's UT out from under a dependent member, the member
  **fails closed to faithful** (rendezvous/station-approach re-fly needs Relative on — do not blanket
  disable it).
- **`LiveVesselAnchor`** resolution reuses the existing guid-gated docking patch (this is the dock-anchor
  / depot-double fix *and* the general live-vs-recorded same-craft dedup — see §11.2).
- **`BodyAnchor` failure** (a missing/renamed modded body name) fails closed (`SuppressedMarker` or hide),
  never NRE; never-visited stock bodies resolve normally.

### 5.3 `ITrajectorySample` — split the frame-overloaded `TrajectoryPoint`

`TrajectoryPoint.latitude/longitude/altitude` mean **degrees** in Absolute frames but **metres along
the anchor's local axes** in Relative frames, with no discriminator on the struct — the documented
"ghost inside the planet" trap. The persisted struct stays as-is (§13); the *typed view* the phase
layer reads is discriminated:

```
interface ITrajectorySample { double Ut; ReferenceFrame Frame; }
  AbsoluteSample  : lat/lon/alt are degrees; resolves via body.GetWorldSurfacePosition
  RelativeSample  : x/y/z are metres on the anchor's local axes; resolves via ApplyRelativeLocalOffset
  OrbitalState (= OrbitSegment)  the Keplerian currency
```

The view is produced once, at chain-assembly time, by resolving `TrackSection.referenceFrame` — so no
downstream reader can mis-read a Relative sample as lat/lon. For a `ParentAnchoredChild` member (§5.2),
the view also routes the dual surface: `bodyFixedFrames` → `AbsoluteSample` (primary), `frames` →
`RelativeSample` (secondary), with the ≥2-sample and in-range checks applied at this layer so an
out-of-range UT produces *no sample* (→ retire) rather than a clamped one.

---

## 6. Layer 2 — the `TrajectoryPhase` hierarchy

A phase is a polymorphic object owning a contiguous run of trajectory with one provenance, one anchor
frame, one treatment, and its seam contracts.

```
abstract class TrajectoryPhase {
  PhaseId           Id;                 // stable RUNTIME render-layer identity (NOT the persisted
                                        // Mission.ExcludedIntervalKeys / <head>/segN selection keys,
                                        // which remain a composition-layer persistence concern, §4)
  PhaseKind         Kind;               // Ascent | DepartureLoiter | SoiDeparture | ...
  SegmentProvenance Provenance;
  AnchorFrame       Anchor;
  double            StartUt, EndUt;     // assembled-chain clock
  PhaseSeam         LeadingSeam, TrailingSeam;

  abstract Treatment ResolveTreatment(); // was ChainAssembler's orbit-vs-polyline if/else
  abstract IEnumerable<RenderSegment> Emit(SampleContext ctx); // the geometry it contributes
  abstract bool CoversUt(double ut);
}
```

Concrete phases (each maps to existing code that becomes its implementation):

| Phase class | Treatment | Provenance (typical) | Implemented by (existing code reused) |
|---|---|---|---|
| `AscentPhase` | TracedPath | Recorded | `ChainAssembler.AppendTracedRuns/FlushRun` (single-recording re-aim ascent re-time is a **deferred known gap** — class present, producer fail-open; see §11) |
| `DepartureLoiterPhase` | StockConic | Recorded; **Synthesized** for heliocentric-park→planet (s15) | role-blind conic emit (departure-vs-arrival split is NEW, from `EnvironmentToPhase`); loiter cut via `ReaimLoiterCompressor`; **park-departure** LAN-re-phased via `RotateLanForParkRephase`, stamped by `DecideDepartureAnchor` |
| `SoiDeparturePhase` | StockConic/TracedPath | Recorded or Synthesized | `ReaimClassifier.RecordedSoiExitUT` |
| `HeliocentricTransferPhase` | StockConic | **Synthesized** (re-aim) or Recorded (faithful) | `ReaimSegmentAssembler.ReplaceHeliocentricLeg` + `UvLambert` |
| `SoiArrivalPhase` | StockConic/TracedPath | Recorded or Synthesized | `ReaimClassifier.ArrivalLeg`; intra-arc moon split |
| `ArrivalLoiterPhase` | StockConic | Recorded | same role-blind conic emit (departure-vs-arrival split is NEW, from `EnvironmentToPhase`); `DestinationLoiterTrim` |
| `DescentPhase` | TracedPath | Recorded | `DescentTrigger.*` (**v1: + cross-member stitcher, §9.1**) |
| `SurfacePhase` | TracedPath | Recorded | traced runs below surface |
| `HoldPhase` | (none — visible "parked" identity) | n/a (clock insertion) | `ArrivalHoldPlanner` / launch-hold; **promoted to a named phase** |

**`HoldPhase`** is promoted from the current invisible `InInteriorGap` clock insertion to a first-class
named phase, so "parked at the SOI boundary / arrival hold" has a real identity for debugging and
composition. It renders quietly (held prior intent), but it exists in the chain.

**Heliocentric-park → planet (the s15 case)** is a distinct shipped producer (master-list item #10),
**not** deferred: it is a `DepartureLoiterPhase` carrying `Provenance = Synthesized` — the recorded park
copy LAN-re-phased via `ReaimSegmentAssembler.RotateLanForParkRephase` and stamped by
`DecideDepartureAnchor` (admissibility-gated to near-circular co-orbital parks; fails closed on
non-equatorial) — whose `TrailingSeam` joins a `HeliocentricTransferPhase`. Built in v1.

**`SegmentKind` is retired.** The legacy 10-value cosmetic enum (`Eject`/`Approach`/`ArrivalOrbit`/
`Landing` etc. were never assigned; only `Transfer`/`Loiter`/`Surface` were) is subsumed by `PhaseKind`
(`Eject`→`SoiDeparture`, `Approach`/`ArrivalOrbit`→`SoiArrival`, `Landing`→`Descent`, …). `RenderSegment`
keeps only its geometry payload (treatment + UTs + frame + conic), so no fifth phase vocabulary survives
the overhaul.

**A faithful (non-re-aim) mission's leaf phase identity** does NOT come from the re-aim plan — it comes
from `SegmentPhaseClassifier.EnvironmentToPhase` + `Recording.SegmentPhase` +
`RecordingOptimizer.SplitEnvironmentClass`. The phase factory must read those for faithful members and
the `ReaimMissionPlan` only for re-aimed members. (This was under-stated in the dossier's model;
called out by the critique.)

### 6.1 `PhaseSeam` (NEW) — the stitch with a continuity order

```
class PhaseSeam {
  SeamKind        Kind;              // Rigid | FlexibleSoi | SwitchContinuation  (Rigid/FlexibleSoi EXIST)
  ContinuityOrder Continuity;        // G0 (position) | G1 (position + tangent)   (NEW)
  SoiCrossing?    Crossing;          // non-null at a body change (NEW, §10)
  bool            OnCamera;          // does it ever render in view?
}
```

- `Rigid + G1` = ascent↔orbit and **orbit↔landing (descent re-stitch)** — numerically enforced (a
  tangent mismatch raises a new reconciler anomaly `rigid-seam-tangent-discontinuity`). **v1 scope:** the
  ascent↔orbit seam is enforced only WITHIN a member (the chain-decomposed faithful case); the
  CROSS-member launch-rotation variant is the deferred `MissionComposite` responsibility (§17). The
  orbit↔landing seam is the ONE cross-member seam built in v1 (the minimal stitcher, §9.1).
- `FlexibleSoi + G0` = the two recorded↔synthetic SOI boundaries (the kink lives here; tolerated in v1).
- `SwitchContinuation + G0` = the **vessel-switch continuation** member boundary
  (`VesselSwitchContinuation`: mothership→lander, Fly/Switch-To) — an **accepted coverage-handoff**,
  *not* a position-match (vessel B's first sample need not meet vessel A's terminal). Like
  launch-rotation it is **not a built geometric seam in v1**: members render as independent span-clock-
  sequenced chains; the enforced cross-member version is deferred to `MissionComposite` (§17).

---

## 7. The recorded-vs-synthetic decision rule (explicit)

> **A phase replays RECORDED geometry unless it is the heliocentric / inter-moon transfer leg AND a
> re-aim window solve succeeds** (→ `Synthesized`, in-memory only). **Finalize-time predicted tails**
> are recorded-once-then-immutable (`FinalizedPredicted`). **Clock transforms** (loop / loiter / hold /
> descent-anchor) never mutate recorded DATA — but **per-window in-memory COPIES may be rotated or
> re-timed** (LAN re-phase, capture re-time, body-fixed derotation). The `Synthesized` flag is derived
> in-memory and never persisted. When a producer is unsupported, the phase is `FaithfulFallback`.

Two refinements the critique forced in:

1. The rule is **not** purely "clock never touches geometry": LAN re-phase and capture re-time change
   the **orientation/epoch** of recorded *copies*. The invariant is narrower — *recorded source structs
   are copied-then-transformed, never mutated in place, and copies are never written back to disk.*
2. Provenance is decided **per phase by the producer**, not per member/per window by a downstream
   5-boolean predicate. One member can contain a `Synthesized` transfer phase and a `Recorded` arrival
   phase. The factory stamps provenance at emit time; nothing downstream re-derives it.

The three immutability clauses, enforced by construction (not discipline):
(a) recorded structs are copied before any transform; (b) first play is exact-UT (window 0 = recorded
span, the re-aim schedule floors `PhaseAnchorUT` past `spanEndUT`); (c) no code path from a
`Synthesized` phase to disk.

---

## 8. Behavior — the per-frame flow

Per ghost, per frame, in one owned pipeline (no side-channel):

```
PlaybackResolver.RunFrame(ghost):                       (= ShadowRenderDriver.RunFrame, renamed)
  traj      = ResolveTrajectory(ghost)                  // faithful or ReaimedTrajectory
  chain     = GetOrBuildPhaseChain(traj, signature)     // CACHED by BuildChainSignature (+ |w{window})
              -> PhaseFactory builds TrajectoryPhase[] from env-class (faithful) or ReaimMissionPlan
  sampleUt  = ChainSampler.Sample(chain, liveUt)        // span clock: ResolveTrackingStationSampleUT
  intent    = GhostRenderDirector.Decide(sample, prior) // 3-case switch: InSegment | InteriorGap | Outside
  scene.Apply(intent)                                   // <-- OWNED: replaces the legacy patch reads
```

`ISceneAdapter.Apply(GhostRenderIntent)` is the cutover's payoff. Per treatment:

- **`TracedPath` (OWNED):** the adapter draws the Vectrosity polyline directly (the `onPreCull` host
  stays the sanctioned shared draw site). No `tracedPathByPid` dict.
- **`SuppressedMarker` (OWNED):** the adapter draws the IMGUI marker. This is the promoted
  `ghostsWithSuppressedIcon` floor — the permanent fallback for no-conic ghosts (below-atmosphere
  descent, off-arc/window-clamp, no-bounds loiter/terminal/atmospheric). Kept, re-homed, not deleted.
- **`StockConic` (MANAGED):** the adapter re-asserts the seed each frame via `SeedAndDriveLive`
  (epoch-bake) so KSP's live-clock icon re-propagation lands on the loop-shifted orbit. **`seedByPid`
  is KEPT — and so is its consume side:** the `GhostOrbitLinePatch` prefix still reads
  `TryGetFreshStockConicSeed(pid, Time.frameCount)` with the ±`SeedFreshnessFrames = 2` tolerance
  (`GhostOrbitLinePatch.cs:396`), because KSP — not Parsek — owns the icon-drive site, so the patch
  prefix is the only interception point. The cutover **renames** this read into the documented
  MANAGED-treatment contract; it does **not** remove it, and the ±2-frame freshness coupling persists.
  "Full collapse" applies only to the OWNED surfaces (TracedPath, markers), which lose their
  side-channels entirely.

The legacy `GhostOrbitLinePatch` Prefix/Postfix shrink to: (a) the StockConic icon-drive seed apply,
and (b) nothing else — the ~430-line line-visibility cascade and the autonomous polyline Driver's
ownership logic move into `DescentPhase`/`HeliocentricTransferPhase`/the director and are deleted.

---

## 9. Producers — v1 build targets

### 9.1 Descent re-stitch (v1 BUILD) — the minimal cross-member stitcher

**Problem:** deorbit legs live in the **transfer** member; the landing lives in the **descent** member
(`DescentTrigger.TryComputeTransferDeorbitHead`, `DescentTrigger.cs:265-302`). The deorbit-tail legs
are already head-gated/re-anchored inside the transfer member today (`ResolveTransferLegHeadUT`, exposed
only during the `Loiter` phase), but they own no first-class `Descent`/seam phase — and the per-member
`PhaseChain` cannot see across members, so the G1 orbit↔landing seam has no owner.

**v1 design:** a focused `CrossMemberSeamStitcher` (the minimal slice of the future `MissionComposite`).
It must absorb BOTH the existing clock logic AND add the geometry seam:
- Input: the transfer member's terminal `SoiArrivalPhase`/deorbit run + the descent member's leading
  `DescentPhase`.
- **Re-home the existing UT-domain join** (the hard part, already shipped, today scattered in
  `DescentTrigger`): the swept deorbit head = `recordedDeorbitUT + (currentUT - triggerUT)`, the
  `captureShift` phase-alignment that lands the body-fixed descent on the parking orbit at the same
  rotation phase, and the per-leg head-gate (`ResolveTransferLegHeadUT`). This is what actually makes
  the deorbit legs sweep to the seam on the loop clock — it is what gives the seam an owner.
- **Add the G1 continuity assertion on top:** a numerical **tangent match** — the capture orbit's
  velocity direction at SOI/atmosphere entry matched to the recorded descent's first-sample tangent —
  over one `PhaseSeam { Rigid, G1, OnCamera=true }`. A tangent discontinuity beyond tolerance raises
  `rigid-seam-tangent-discontinuity`.
- It promotes the deorbit arc into a visible first-class phase (no longer hidden in the transfer member).
- It is designed as a standalone owner that the full `MissionComposite` (§17) absorbs unchanged.

So v1 takes ownership of the existing `captureShift`/swept-head clock logic, not just a new tangent
check — that re-homing is the cross-member work this decision budgets for. **Ordering:** the
swept-deorbit-head UT join must compose AFTER the arrival-hold + destination-loiter-trim span-clock remap
(periodicity Phase 3b/4), not before, so the deorbit alignment is neither double-applied nor bypassed.

**Not in v1:** the full `MissionComposite` (launch-rotation cross-member seam, mission-wide composite
ownership). Defined in the model; built later.

### 9.2 Define-only producers (v1 fail-closed-to-faithful)

- **`SoiCrossing` / cross-SOI kink** — defined (§10), rendered as today's `FlexibleSoi` G0 seam. No
  geometry fix. The whole-patched-conic-chain synthesis is a separate test-gated effort.
- **`NestedSoiSubtree` / Jool** — defined (§10). Recorded Jool tours render faithfully; no synthetic
  moon-to-moon re-aim (`ReaimClassifier` single-hop guard, `MaxConstrainedMoons=1`, 8-patch cap stay).
- **Station rendezvous** — the moving-target phase is defined: a `HeliocentricTransferPhase` whose
  arrival `AnchorFrame` is `LiveVesselAnchor` (a moving vessel, not a body center), joined by a
  `PhaseSeam { FlexibleSoi, G0 }` to an arrival-side **`HoldPhase`** that hosts the station-period hold
  (`ArrivalHoldPlanner`'s station-period branch — *not* an `ArrivalLoiterPhase`, since there is no body
  to loiter around). v1: recorded approaches render faithfully; the classifier returns unsupported (a
  free heliocentric station fails the direct-child-of-common-ancestor test) → `FaithfulFallback`. The
  synthetic moving-target producer is deferred.

---

## 10. SOI / moon-rich (Jool) model

`SoiCrossing` unifies the ≥4 places a body change is re-derived today (`GhostOrbitBodyChanged`,
`MapRenderProbe.bodyChanged`, the `lastLineToggleBody` blink guard, `SeamBetween`'s string compare):

```
class SoiCrossing {
  string fromBody, toBody;
  double crossingUt, soiRadius;
  StateVector exitState, entryState;   // for future continuity work; v1 records but doesn't enforce
}
```

`NestedSoiSubtree` models a moon-rich body as a tree (Jool → {Laythe, Vall, Tylo, …} → sub-SOIs).
`IBodyInfo.ReferenceBodyName` already gives the parent chain; `UvLambert` is body-agnostic (μ is a
parameter), so a future recursive per-leg solver reuses the existing pure kernels. v1 **defines** the
type and renders recorded tours faithfully; the recursive synthetic producer + a joint
configuration-period alignment are deferred. The `FrameBodyName`→`AnchorFrame` widening
(`ParentGeneratedConicAnchor`) is what lets a moon-to-moon child ride a Jool-centric generated arc once
the producer exists.

---

## 11. Edge cases & gameplay situations

This section enumerates the situations a real KSP playthrough produces and how the model handles each.
The model documents the render *mechanism* (§3–§9); this section pins it to the *situations* so the
coverage is demonstrable. Legend in §11.6: **covered** / **partial** / **gap→addressed-here** /
**out-of-scope-ok**.

### 11.1 Vessel topology & lifecycle (couple / decouple / retire)

The render model sees a mission as N member chains sequenced by the span clock; topology events are
*where members begin and end*. The cross-member geometric seam is owned only for descent in v1 (§9.1);
the others are sequenced by the span clock and the existing presence lifecycle.

- **Debris / breakup pieces (`IsDebris = true`) are excluded from the render pipeline entirely.** The
  `GhostMapPresence` `IsDebris` early-returns (`GhostMapPresence.cs:13198`, `:4932`) keep booster /
  breakup ghosts out of map/TS presence; the `PhaseFactory`/`PlaybackResolver` is **never invoked** for
  them. This is a preserved invariant — the new pipeline must keep skipping `IsDebris`, not synthesize a
  chain.
- **Controlled-decoupled child** (a lander/probe off a decoupler: `IsDebris = false`,
  `ParentAnchorRecordingId != null`) **does** enter the pipeline and is a first-class parent-anchored
  member — see the `AnchorFrame.ParentAnchoredChild` routing in §5.2 (`bodyFixedFrames` primary,
  `frames` secondary, ≥2-sample minimum, out-of-range → **retire**, never clamp).
- **Dock / board merge** (two live vessels → one at `dockUT`): the absorbed-side member chain **retires**
  at `dockUT` and the merged-child chain begins (a make-before-break *member* swap). v1 renders this via
  the span-clock member sequencing + the presence lifecycle; the unified *docked-stretch* presence
  across two trees is deferred (§17 cross-tree dock).
- **Undock / EVA split** (one vessel → N at `splitUT`): the parent chain ends, each child begins a
  faithful member chain at `splitUT`. EVA is the same seam (a kerbal becomes its own vessel, rendered as
  a faithful `TracedPath`/`SuppressedMarker` member). Claw grab/release is the generic couple/decouple
  seam, not docking-port-specific.
- **Terminal / destructive retire (mid-window):** a member whose recorded trajectory ends in
  crash/impact/explosion **mid-window** resolves to `Coverage = OutsideWindow → Hidden`, **not**
  `InInteriorGap` hold. The retire trigger is recorded end-of-data / `TerminalKindClassifier`, not
  `WindowEndUT`. This closes the documented "sub-surface ghost not retiring" bug class. (See §12 for the
  preserved `IsTerminalStateEligibleForMapPresence` gate that keeps Destroyed/Recovered out of presence.)

### 11.2 Navigation, session & scene

- **Active-vessel switch** (Fly from TS/KSC via `StockActionIntentMarker`, Map Switch-To, in-bubble
  `[`/`]`): the ghost matching the newly-active live vessel's **launch identity** drops, guid-gated via
  `VesselLaunchIdentity` (`persistentId` is craft-baked, not launch-unique), and the ghost for the vessel
  switched away from re-appears. `BuildChainSignature` / the chain cache invalidate on active-vessel
  change. The `RemoveAllGhostVesselsBeforeStockFly` teardown-before-Fly is preserved (§12).
- **Re-Fly / Rewind-to-Separation / supersede:** the render layer is deliberately **`[ERS-exempt]`
  physical-visibility** — it walks `RecordingStore.CommittedRecordings` raw; supersede/ERS/ELS
  *effectiveness* is enforced **upstream** by the `GhostMapPresence` ProtoVessel lifecycle (a superseded
  recording loses its ProtoVessel, so the per-ghost driver never runs). `BuildChainSignature` (incl.
  `OrbitSegments.Count`/`Points.Count`) invalidates on a re-fly split; a mid-merge `MergeJournal` crash
  must leave no orphan ghost ProtoVessel.
- **Clock readiness (cold-load / quickload):** the `PlaybackResolver` **defers render when
  `liveUT <= 0`** (cold `OnLoad` / pre-time-init — the Planetarium UT=0 trap) rather than sampling the
  span clock at UT=0, mirroring `LedgerOrchestrator.IsCurrentUtReadyForCutoff`; otherwise a degenerate TS
  ghost would place on the first cold-load frames (TS presence is stock-automatic the moment a
  ProtoVessel exists). A **quickload mid-gap** cold-starts the Director cleanly with no stale prior intent
  (resolve `InSegment` if covered, else hide).
- **Scene transitions** (FLIGHT↔MAP toggle, FLIGHT→TS→FLIGHT, →KSC, →EDITOR, →MAIN-MENU): a cold-start
  mid-segment must **not** one-frame-blank (model the proto re-seed settle); **flight-map↔TS render
  parity** is a v1 requirement (carried forward from the superseded doc). The proto-vessel
  re-seed-latency vs make-before-break atomicity question is carried to §17.
- **Camera focus / watch-mode / set-as-target on a ghost** across a treatment swap or scene change: a
  `SuppressedMarker` has **no ProtoVessel** to focus/target, so the adapter must persist/re-home a
  focus-anchor or explicitly log that a `SuppressedMarker` is not targetable.

### 11.3 Composition & playback

- **Overlap (N simultaneous instances of one looping mission):** `PhaseChain` carries an `InstanceKey`,
  but map/TS render exactly **one** ghost for the whole overlapping mission at the **selected cycle's**
  span-clock head-UT (N-instance is flight-mesh-only; the model leaves the slot for flight adoption).
  Caveat: an overlap member sitting at a seam gap holds prior intent, so the single rendered ghost can
  appear frozen — the "overlap renders nothing (gap-hold)" symptom.
- **v1 cross-member story (concrete):** a looped multi-member mission renders as **N independent
  `PhaseChain`s sequenced by the span clock**; the **only** cross-member geometric seam built in v1 is
  descent re-stitch (§9.1). Launch-rotation, switch-continuation, and SOI-straddle ride the existing
  span-clock launch-hold / `FlexibleSoi`; `MissionComposite` is deferred (§17).
- **BG-on-rails member:** a background on-rails recording emits **no env-classified `TrackSection`s**
  (orbit-bridge-only), so the faithful `PhaseFactory` path has no `SegmentPhase`/`SplitEnvironmentClass`
  to read and **must fall through to an all-orbital chain** (Loiter/Transfer + `FlexibleSoi` at body
  changes) with **no Descent/Surface phase**, even for an atmosphere-grazing eccentric orbit. The factory
  must tolerate absent `SegmentPhase` data without asserting.
- **Debris-in-loop boundary:** ordinary **body-anchored** debris rides along via the kept span-clock
  debris seam; **transfer-leg / generated-conic** debris (needing `ParentGeneratedConicAnchor`) is
  **fail-closed in v1** (the "transfer-phase debris cannot be produced in v1" conclusion) — it does not
  render on its parent's generated arc.
- **Synthetic `RouteBackingMission` (logistics):** consumed transparently — the phase layer takes a
  neutral `LoopUnitSet`. A `PhaseChain`'s `WindowStartUt`/`WindowEndUt` may fall **mid-recording**
  (route end-trim-to-dock via `ExcludedIntervalKeys`/`ComputeTrimmedMemberWindows`); the assembler must
  clip phases to the trimmed window.
- **Warp through a `HoldPhase` / compressed loiter / descent re-anchor:** a single high-warp frame can
  advance `liveUt` across an entire `HoldPhase` (newly named, §6) or compressed loiter; `HoldPhase.CoversUt`
  and the span clock must resolve to the correct post-hold assembled-UT and never freeze the ghost for
  multiple frames.

### 11.4 Environment & rendering

- **No-conic ghosts:** below-atmosphere descent, off-arc/window-clamp, no-bounds loiter/terminal →
  `SuppressedMarker`. Never a blank icon.
- **Single-recording interplanetary:** `ReaimedTrajectory.Points` is empty by design, dropping the
  ascent for a single-recording re-aim — a **deferred known gap** (the `AscentPhase` class exists to host
  the eventual re-time fix; producer fail-opens, §4).
- **Zoom-cull:** the OWNED `TracedPath` must absorb the stock map-line zoom behavior (the Vectrosity
  2D/3D swap) rather than re-implement it — folded into the treatment.
- **Suborbital hop:** Ascent→ballistic→Descent within one body — `TracedPath` for the atmospheric
  portions, `StockConic` only for an above-atmosphere ExoBallistic arc; `FlexibleSoi` never engages.
  Prelaunch/landed/splashed/rover initial states are `SurfacePhase` + `SuppressedMarker` (terrain-aware
  altitude is a flight-adapter concern, out of v1).
- **`BodyAnchor` resolution failure** (a renamed/removed modded body absent from `FlightGlobals.Bodies`)
  must **fail-closed** (`SuppressedMarker` or hide), never NRE. Discovery level does **not** block
  `CelestialBody` resolution — all stock bodies exist regardless of whether the player has visited them,
  so a never-visited stock body renders normally; only a missing body name fails.
- **Warp / reseed:** the `InInteriorGap` HOLD, the `GhostOrbitDominantBodyPatch` SOI-block, and the CoMD
  re-snap stay (they prevent KSP reseeding a re-aimed heliocentric ghost to a degenerate hyperbola).
  Preserved verbatim. The KSP-API render gates (§12) are the `StockConic` boundary conditions.

### 11.5 Gameplay-situations coverage matrix

Demonstrates completeness; "where" names the owning phase/treatment/seam/lifecycle.

| Situation | Handling | Where |
|---|---|---|
| Staging/decoupler debris (`IsDebris`) | out-of-scope-ok | excluded upstream (`IsDebris` gate, §11.1) |
| Controlled-decoupled child (lander off a stage) | covered | `ParentAnchoredChild` anchor (§5.2), bodyFixedFrames-primary, out-of-range retire |
| Docking (two → one at dockUT) | covered | absorbed-side chain retires; span-clock member swap (§11.1); cross-tree composition deferred (§17) |
| Undocking / EVA split (one → N) | covered | parent chain ends, child chains begin (§11.1) |
| EVA kerbal | covered | faithful `TracedPath`/`SuppressedMarker` member |
| Asteroid/comet claw grab+release | covered | generic couple/decouple seam (§11.1) |
| Structural breakup (`onPartJointBreak`) | covered | debris excluded; controlled survivor is an ordinary member |
| Reentry burn-up / explosion / destruction mid-recording | gap→addressed | mid-window terminal retire → Hidden (§11.1) |
| Vessel recovery (KSC / flight Recover) | covered | terminal-eligibility gate + live-vs-ghost dedup (§12) |
| Cross-tree dock (foreign-tree vessel) | partial | anchor via `LiveVesselAnchor`; cross-tree composition deferred (§17) |
| Fly from TS / KSC marker / Map Switch-To | covered | `StockActionIntent` teardown-before-Fly (§11.2, §12) |
| In-bubble vessel switch (`[`/`]`) | covered | guid-gated dedup, chain-cache invalidation (§11.2) |
| Re-Fly / Rewind supersede mid-session | covered | ERS-exempt render; effectiveness via ProtoVessel lifecycle (§11.2, §12) |
| Rewinding a looped mission | covered | `LoopUnitSet` rebuild from post-split indices; cache invalidation |
| Quicksave/quickload mid-record/loop/gap | gap→addressed | Director cold-starts with no prior intent (§11.2) |
| Cold load, Planetarium UT=0 | gap→addressed | defer-render guard `liveUt <= 0` (§11.2) |
| Load a different save / new game | out-of-scope-ok | ProtoVessel teardown + schema-compat reject (stated) |
| Scene transition mid-segment / mid-seam | gap→addressed | no one-frame blank; flight-map↔TS parity (§11.2) |
| Camera focus / target a ghost across swap | partial | `SuppressedMarker` not targetable; re-home focus anchor (§11.2) |
| Warp through HoldPhase / loiter / descent re-anchor | gap→addressed | `HoldPhase.CoversUt` warp-step-safe (§11.3) |
| Warp-reseed-lag at SOI / orbit-raise gaps | covered | `InInteriorGap` hold + SOI-block + CoMD re-snap (§11.4) |
| Warp across a re-aim synodic window boundary | covered | `\|w{window}` chain-signature token |
| Loop a single recording | covered | span clock |
| Loop a whole multi-member mission | covered | N independent `PhaseChain`s on the span clock (§11.3) |
| Looping mission while player flies live nearby | covered | disjoint tree indices |
| Overlap (N instances) | partial | one ghost at the selected cycle head; gap-hold caveat (§11.3) |
| Loiter compression / arrival hold / loiter trim | covered | span-clock transforms |
| Chain continuation across vessel switches | partial | `SwitchContinuation` seam — deferred to MissionComposite (§6.1, §17) |
| Mixed faithful + re-aimed members | covered | provenance per-phase by the producer (§7) |
| Re-aim with a large synodic target move | covered | arrival hold/trim composes with the deorbit head (§9.1) |
| Synthetic RouteBackingMission, trimmed window | covered | mid-recording window clip (§11.3) |
| Debris shed on a re-aimed/generated transfer leg | out-of-scope-ok | `ParentGeneratedConicAnchor` fail-closed in v1 (§11.3) |
| BG-on-rails recorded vessel | covered | all-orbital chain, no Descent/Surface phase (§11.3) |
| Atmospheric descent to landing/splashdown | covered | `DescentPhase` + cross-member stitcher (§9.1) |
| Prelaunch / landed / splashed / rover | covered | `SurfacePhase` + `SuppressedMarker` (§11.4) |
| Suborbital hop | covered | Ascent→ballistic→Descent, no `FlexibleSoi` (§11.4) |
| Aerobraking (many periapsis grazes) | covered | alternating conic/traced (efficiency note only) |
| Escape / hyperbolic / Sun-escape | covered | `StockConic` + `FlexibleSoi` at SOI |
| Single-level SOI transition (Kerbin→Mun, →Sun) | covered | `SoiCrossing` `FlexibleSoi` G0 (§10) |
| Moon-rich / nested-SOI Jool tour | covered (faithful) | synthetic moon-to-moon re-aim fail-closed (§10) |
| Multiple ghosts at different bodies | covered | independent per-body `PhaseChain`s |
| Ghost at a never-visited body | covered | renders normally; only a missing body name fails (§11.4) |
| Career-economy: ghost must not corrupt the ledger | covered | `ghostMapVesselPids` guard timing invariant (§12) |
| TS automatic ghost presence | covered | `DiscoveryLevels.Owned` + forced `VesselType` (§12) |
| Map filters / `MapView.fetch==null` cold-start | covered | KSP-API render gates (§12) |

---

## 12. What does NOT change (must-not-regress)

**Core render spine:** the Director decision spine (the 3-case `Decide`); the `SeedAndDriveLive`
epoch-bake; the kept icon floor (now the `SuppressedMarker` tier); fail-to-faithful; the immutability
HARD RULE; the span clock + `LoopUnitSet` **positional-index** contract (flight/KSC/TS/map all re-derive
it); the `ReaimPlaybackResolver.Shared` single cache; the `GhostOrbitDominantBodyPatch` SOI-block + CoMD
re-snap; the observability triad (`MapRenderTrace`/`MapRenderProbe`/`GhostRenderReconciler`).

**Presence-lifecycle invariants (the cutover re-homes ProtoVessel lifecycle into the scene adapter — it
must NOT shift these):**

- The **terminal-eligibility gate** `IsTerminalStateEligibleForMapPresence`
  (`GhostMapPresence.cs:3952`/`:4997`/`:13208`) and the live-persisted-vessel-vs-ghost dedup in
  `ResolveMapPresenceGhostSource` (incl. the `loopMemberInWindow` carve-out) — preserved as scene-adapter
  concerns, distinct from `LiveVesselAnchor`. (Drop them and ghosts for Destroyed/Recovered recordings
  start rendering.)
- The **`ghostMapVesselPids` guard register/de-register TIMING is invariant** — a ghost ProtoVessel must
  never momentarily look real to recovery/CommNet/contracts/ledger. This is the one way the overhaul
  could trip the documented career-corruption class (science 124.8→1.0 + funds clawback); career-economy
  isolation otherwise stays out of the render layer.
- The render layer is deliberately **`[ERS-exempt]` physical-visibility**: supersede / ERS / ELS
  *effectiveness* is owned by the `GhostMapPresence` ProtoVessel lifecycle (a superseded recording loses
  its ProtoVessel), **not** the phase factory. The factory/`PlaybackResolver` keeps skipping `IsDebris`.
- The **`StockActionIntent` teardown-before-Fly** (`RemoveAllGhostVesselsBeforeStockFly` + the TS-Fly
  drift capture) and the prior doc's **"Fly / re-fly unchanged"** carve-out are preserved.

**KSP-API render gates (the MANAGED-`StockConic` boundary conditions the `seedByPid` re-assert must
preserve):** `MapView.fetch == null` cold-start no-op; `DiscoveryLevels.Owned` + the forced `VesselType`
for the ghost ProtoVessel; the `Orbit.nextPatch`-past-`FINAL` / non-`activePatch` garbage hazard; and
`ScaledSpace.LocalToScaledSpace` (map/TS-required, flight-forbidden). The TS cold-start path is still an
open in-game probe (dossier).

**Composition layer (untouched by the render overhaul):** mission grouping / visibility / renaming
(`GroupHierarchyStore`, `MissionGroupLink`, `MissionStore` selection) and the persisted
`Mission.ExcludedIntervalKeys` selection keys.

---

## 13. Backward compatibility

Pre-1.0, **no migration** (per the project's no-back-compat rule). But two hard invariants:

- **The persisted substrate is untouched.** `Recording`, `TrackSection`, `OrbitSegment`,
  `TrajectoryPoint` keep their current struct shapes and `RecordingStore.CurrentRecordingSchemaGeneration`.
  The phase layer is a runtime in-memory read-view; nothing it produces is serialized. This preserves
  the value-copy contract that `RecordingOptimizer.SplitAtUT` (tail-clones straddling `OrbitSegment`s)
  and the Re-Fly `RecordingTreeSplitter` (struct write-backs + rollback ledger) depend on.
- **`Synthesized` provenance never persists.** The two write epochs stay distinct:
  finalize-time-predicted (written once, then immutable) vs playback-time-synthesized (never written).

---

## 14. Diagnostic logging & MapRenderTrace integration (a v1 priority)

**Full logging coverage through the existing tracer is non-negotiable** — the KSP.log is the primary
debugging tool; if it didn't get logged, it didn't happen. Every new abstraction **integrates with the
existing `MapRenderTrace` / `MapRenderProbe` observability** (off by default behind the `mapRenderTracing`
setting), NOT ad-hoc `ParsekLog` calls. This section **supersedes the separate tracer design doc**
(`design-map-ts-render-tracer.md`), folding its model in. The tracer + probe stay read-only — they never
mutate render/orbit/line/icon/marker state.

The tracer's tiered model is the contract every new component emits through:

- **Tier-A structural events** (`EmitStructural` → `Info`, opens a detailed window): phase-chain assembled
  (phase count + kinds + seams + provenance); member-seam events (dock-merge / undock-split / EVA /
  terminal-retire); active-vessel-switch dedup; scene-transition cold-start; clock-defer (UT≤0).
- **Tier-B change-based truth** (`EmitOnChange` → `Verbose`, per-pid signature dict, **warp-stable keys**):
  per-frame phase identity (`PhaseKind` + `Provenance` + `Treatment` + `AnchorFrame`); the active seam;
  `SuppressedMarker` on/off; the StockConic seed re-assert; `HoldPhase` enter/exit.
- **Tier-C anomalies** (`EmitAnomaly` → `Warn`, pure Unity-free predicates): the existing
  `icon-jump` / `line-blink` / `decision-vs-truth` / `polyline-orbit-overlap`, **plus** the new
  `rigid-seam-tangent-discontinuity` (G1 descent seam), `parity-drift` (the oracle below),
  `retire-not-held` (a terminal member that held instead of hiding), `anchor-resolve-fail`
  (BodyAnchor / parent-anchor resolution failed → fail-closed), `clock-not-ready` (sampled at UT≤0).
- **New trace surfaces:** extend `RenderSurface` so every owned draw (proto orbit line, forward arc,
  polyline, marker) and every new phase/seam/lifecycle event appears in the appear/disappear EVENT
  logging. **Every per-frame / per-ghost line uses warp-stable rate-limit keys** — never key on a value
  that advances every frame (the #1063/#1064 lesson). Per-collection iteration uses the batch-counter +
  single-summary convention.

**New parity oracle (required — replaces the now-circular reconciler):** `GhostRenderReconciler` today
measures "intent vs the OLD rendered truth," which goes **circular** once the cutover completes (no old
truth). Replace it with a **recorded-vs-rendered geometry diff**: sample the rendered phase geometry and
compare against the recorded source (faithful) or the producer's intended arc (synthesized), within
tolerance — emitted as the `parity-drift` Tier-C anomaly **and** asserted in tests (§15). **Scope
honesty:** for `Synthesized` phases the oracle validates *draw-fidelity* (rendered == intended arc), NOT
*solve-correctness* (intended arc == physically-correct transfer) — the latter (e.g. the plane-tilt /
near-180° handedness bugs the dossier lists as open) stays the re-aim solver's own test surface.

---

## 15. Test plan (full coverage is a v1 priority)

**Every new method with logic gets a unit test; everything that needs live KSP gets an in-game test. A
migration step (§16) is not complete until both are green.** Per project convention: pure/decision logic
is `internal static` for direct testability; log-assertion tests (`ParsekLog.TestSinkForTesting`) verify
code paths executed and logged the expected data; classes touching shared static state use
`[Collection("Sequential")]` + the matching `ResetForTesting()`.

**Pure / headless (xUnit) — one suite per new pure component:**

- the `PhaseFactory`: env-class → faithful phases (`SegmentPhaseClassifier` path) AND `ReaimMissionPlan` →
  re-aimed phases; the heliocentric-park→planet variant; BG-on-rails → all-orbital chain (tolerates absent
  `SegmentPhase` without asserting); single-recording empty-`Points` handling.
- `SegmentProvenance` stamping (per-phase by the producer); the recorded-vs-synthetic decision rule (§7).
- each `TrajectoryPhase` subclass's `ResolveTreatment` / `CoversUt` / `Emit` — incl. `HoldPhase.CoversUt`
  warp-step-safety across a whole hold; mid-window terminal retire → `OutsideWindow`/Hidden.
- `PhaseSeam`: the G1 tangent-match math; `Rigid`/`FlexibleSoi`/`SwitchContinuation` classification; the
  `rigid-seam-tangent-discontinuity` predicate.
- `AnchorFrame` resolution: `BodyAnchor` (+ missing-body fail-closed, never NRE); `ParentAnchoredChild`
  dual-surface routing (≥2-sample, out-of-range → **retire-not-clamp**); `RecordedAnchorTrajectory`
  loop-vs-non-loop contract.
- the cross-member stitcher: the swept-deorbit-head / `captureShift` UT join + the G1 seam + the ordering
  vs arrival-hold / loiter-trim.
- the clock-readiness guard (UT≤0 → defer); the §14 parity-diff math; and **all new `MapRenderTrace` pure
  predicates / formatters**. Add `LogContractTests` for every new trace tag / level / format.

**In-game (`RuntimeTests.cs` / `ExtendedRuntimeTests`) — for everything runtime-only:**

- map+TS render parity (one orbit icon/line or one polyline+marker per ghost, matching flight);
  flight-map↔TS parity across a scene toggle.
- descent re-stitch renders the deorbit arc and lands with a continuous G1 seam (zero
  `rigid-seam-tangent-discontinuity`).
- `SuppressedMarker` fires for below-atmosphere / no-bounds ghosts (never a blank icon).
- fail-closed-to-faithful for Jool / station / transfer-leg debris.
- the **parity oracle** reports zero geometry drift on a faithful mission AND on a re-aimed loop.
- lifecycle: dock-merge retires the absorbed-side ghost at `dockUT`; undock/EVA split renders N child
  chains; `IsDebris` recordings render nothing; a controlled-decoupled child retires out-of-range.
- session/scene: cold-load with UT=0 defers (no degenerate TS ghost); active-vessel Fly/Switch-To dedups
  the live craft's ghost; the `ghostMapVesselPids` register/de-register timing keeps a ghost invisible to
  recovery (a career-safety assertion).
- a `mapRenderTracing`-on run emits the expected Tier-A/B/C lines (tracer-coverage test).

**Generators (`Tests/Generators/`):** synthetic recordings for each phase kind, a cross-member descent
mission, a dock/undock topology mission, a parent-anchored-child mission, and a looped re-aim mission;
plus an injected synthetic recording for end-to-end coverage (per the post-change checklist).

---

## 16. Migration outline (the detailed plan is the next deliverable)

High-level sequencing only — the full phased migration plan is the next document.

1. **Introduce Layer 0/2 types alongside the current pipeline** (no behavior change): `TrajectoryPhase`
   hierarchy, `SegmentProvenance`, `AnchorFrame`, `PhaseSeam` — built and unit-tested, not yet wired.
2. **Phase factory** producing `TrajectoryPhase[]` from the existing `ChainAssembler` inputs; assert
   byte-parity of the **geometry fields only** (`Treatment`, `StartUt`, `EndUt`, `FrameBodyName`,
   conic-element payload) against the current assembler before cutting over. The net-new
   `PhaseKind`/`Provenance` are deliberately NOT in the parity set — the legacy `SegmentKind` is
   cosmetic so parity on it proves nothing, and richer kinds can't byte-match by construction; they are
   validated separately by the §15 phase-factory tests + the §14 oracle.
3. **Re-home the side-channels into `scene.Apply(intent)`** surface by surface (TracedPath, then
   markers), each behind the parity oracle; keep StockConic's `seedByPid` as the MANAGED re-assert.
   Preserve the flight-view `currentUt == 0` secondary-head sentinel (`GhostMapPresence.cs:12203`, the
   inert-secondary-marker convention) as an explicit `ISceneAdapter` scene-input contract — or replace it
   with an `IsSecondaryHeadActive` flag — when re-homing presence.
4. **Delete** the autonomous polyline Driver ownership logic + the 430-line cascade once parity holds.
5. **Build the cross-member stitcher + `DescentPhase` G1 seam**; validate in-game.
6. **Define (not build)** `NestedSoiSubtree`, `SoiCrossing`, the station moving-target phase as
   fail-closed types.
7. **Swap the reconciler** to the recorded-vs-rendered parity oracle.

Each step is independently shippable and reversible; nothing deletes a working surface until the parity
oracle is green on it.

---

## 17. Deferred (defined here, built later)

- The whole-patched-conic-chain cross-SOI synthesis (the ~62° kink fix) — needs a validatable test
  mission first.
- The full `MissionComposite` Layer-3 (absorbs the minimal stitcher; owns launch-rotation, the
  `SwitchContinuation` member boundary, and all cross-member seams).
- **Cross-tree dock COMPOSITION** — a post-dock shared journey spanning two `TreeId`s renders as **two
  independent member chains** in v1 (no unified docked-stretch presence). Distinct from the
  `LiveVesselAnchor` dock-*anchor* fix (§5.2); the tree-spanning `MissionComposite` selection is the later
  fix (`Mission` is rigidly single-tree today). The v1 two-chains behavior is expected, not a bug.
- **Open question carried from the superseded doc:** proto-vessel re-seed-latency vs make-before-break
  swap atomicity (the one-frame settle on a treatment swap / scene cold-start).
- The recursive nested-SOI synthetic producer + joint configuration-period alignment (Jool-5).
- The synthetic moving-target station-rendezvous producer.
- **Flight-mesh fold-in as a third `ISceneAdapter`** — explicitly out of model. The three flight
  positioners (`GhostPlaybackEngine.ghostStates`, `overlapGhosts`, `ParsekFlight.PositionChainGhosts`)
  and the unimplemented `.pann` anchor-correction pipeline are out-of-model unknowns; the shared
  contract is limited to the already-verified seams (span clock, `IPlaybackTrajectory`,
  `ReaimPlaybackResolver.Shared`).
- Substrate retype (god-object split, typed `TrackSegment` subtypes, killing the dual orbital
  representation) — separate effort, carries the Re-Fly value-copy/rollback cost.
```
