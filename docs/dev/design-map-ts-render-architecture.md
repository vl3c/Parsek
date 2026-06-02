# Design: Map / Tracking-Station Ghost Render Architecture (Clean Modular Rewrite)

*Status: Step-3 design document for a clean, modular rewrite of the map-view and
Tracking-Station ghost-render path. The implementation will REPLACE the current
smear of Harmony patches + OnGUI passes + lifecycle ticks with a new module whose
internal structure mirrors the gameplay model, reusing existing solver/playback
code and proven snippets but re-organizing WHO decides what. This doc specifies the
gameplay necessity, the target architecture, the reuse/replace map, behavior, edge
cases, diagnostics, and tests. It does NOT specify method-level code changes — that
is Step 4 (explore + plan). It does NOT change the recording format, the re-aim
solver, or the periodicity scheduler; it changes how the renderer CONSUMES them.*

*Supersedes the precursor note `design-map-ts-looped-mission-render.md` (folded in).
Builds on: `docs/dev/done/plans/reaim-interplanetary-transfers.md`,
`docs/dev/done/plans/reaim-loiter-compression.md`,
`docs/dev/design-mission-periodicity.md` (its "replay as-is, no re-aim" decision is
superseded — see that doc's banner), `docs/dev/design-mission-abstractions.md`,
`docs/parsek-ghost-trajectory-rendering-design.md`, and
`docs/dev/design-map-ts-render-tracer.md`.*

---

## 1. Problem & motivation

Rendering the **exact recorded** trajectory of a single mission in map view and the
Tracking Station already works. The pain — repeated bug rounds (icon jumps, line
blinks, polyline/orbit double-draw, frozen markers, stale loop-shift), the dedicated
render-tracer observability layer, and churn across ~6 Harmony patches plus two OnGUI
passes plus a 0.25 s lifecycle tick — is concentrated in **looped missions**, which
are the basis for supply routes.

The root cause is structural, not a collection of independent bugs:

1. **No single owner of a ghost's on-map representation.** Whether a ghost is visible,
   which surface draws it, and where its icon sits are decided across ~6 decoupled
   patches/passes running at different execution orders and cadences, coordinating
   through implicit, unwritten contracts (e.g. "the polyline renderer publishes
   `activeLegRecordings` at order −50, the orbit-line patch reads it at order 0, and
   nothing toggles `line.active` in between"). When a contract is missed by one frame
   or one new code path, the result is a visible glitch that is *silent in the logs*,
   because each patch logs its decision, not the rendered truth. (That divergence is
   precisely why `design-map-ts-render-tracer.md` exists.)

2. **A looped, re-aimed mission is not a stored trajectory — it is a chain re-assembled
   per launch window.** The heliocentric transfer is regenerated, loiter orbits are
   trimmed to a different count, and the arrival arc is re-anchored to a new UT. The
   current code assumes one continuous recorded path, so every heterogeneous hand-off
   (recorded points → generated conic → recorded points) and every re-anchored joint is
   a fresh opportunity for a glitch.

3. **The Tracking Station has no looped-ghost animation at all today** (the Phase-F gap
   in `design-mission-abstractions.md`): TS positions ghost proto-vessels once at the
   live UT against each recording's recorded window, with no per-frame loop-phase remap.
   So TS is not just buggy for loops — it is unimplemented for them.

The fix is to make the renderer's internal model *match the gameplay model*: a
variable-structure **chain** of typed segments joined at typed seams, with one
per-frame decision owner and dumb follower surfaces. The rewrite is the opportunity to
collapse the scattered coordination into that single structure.

## 2. Terminology

- **Ghost** — a playback-only representation of a recorded vessel on the map/TS. The
  player *watches* ghosts; ghost interaction (focus / target / Fly / re-fly) is already
  implemented and out of scope.
- **Segment** — a contiguous stretch of a ghost's path with one render treatment.
  - **Recorded segment** — replayed exactly from recorded data (ascent, burns, in-SOI
    arcs, landing). Non-conic stretches are *recorded-point* segments.
  - **Generated segment** — computed deterministically, not backed by recorded points
    (the heliocentric transfer). Always a clean conic.
- **Sub-chain** — a maximal run of recorded segments anchored to one body's frame: the
  **departure sub-chain** (launch body) and the **arrival sub-chain** (destination body).
- **Joint** — a boundary between two segments. **Rigid** joints must connect cleanly
  (sub-chain-internal seams, the ascent↔orbit and orbit↔landing hand-offs). **Flexible**
  joints carry slack and a *tolerated, not-hidden* visible discontinuity (the two SOI
  boundary seams).
- **Treatment** — how a segment is drawn: **stock-conic** (a stock KSP object —
  proto-vessel icon + KSP orbit line) or **traced-path** (our drawn polyline of recorded
  points + marker/label icon).
- **Chain** — the per-cycle-window ordered list of typed segments for one ghost,
  assembled from its (possibly re-aimed) trajectory + the loop unit's window/anchor/cuts.
- **Cycle window** — `[launch UT … end UT]` of one relaunch of a looped mission, as
  scheduled. A looped ghost renders only while the live clock is inside a cycle window.
- **Scheduler** — the existing re-aim + periodicity machinery that chooses *when* (launch
  UT, loiter counts, arrival window) so recorded arcs play against live bodies that
  ≈ match the recording. Out of scope to redesign; the renderer consumes its output.
- **Render intent** — the renderer's single per-frame, per-ghost decision: visible?,
  treatment, drive-UT, icon position, line spec, label.

## 3. Gameplay model (necessity first)

### 3.1 The chain

A looped mission's path is a **variable-structure chain**, assembled per cycle window
from the recorded segment classification — never a fixed template:

```
 ── DEPARTURE SUB-CHAIN ──────────►  ⟂  ──TRANSFER──  ⟂  ──── ARRIVAL SUB-CHAIN ─────►
 launch · ascent · [loiter] · eject     (generated)      SOI-entry · approach · [loiter] · land
 anchored to LAUNCH BODY frame          clean conic       anchored to DESTINATION BODY frame
   rigid recorded segments              must look         rigid recorded segments
        ▲ flexible: launch UT,          perfect              ▲ flexible: arrival window
          departure loiter count                              (→ moon config), arrival loiter
        ⟂ flexible SOI exit               ⟂ flexible SOI entry
```

### 3.2 Decoupling at the SOI joints (why re-aim exists)

The flexible SOI joints **decouple** the chain into independent rigid sub-chains. The
SOI joints carry "wait as long as needed" slack (time warp is high there), and the
transfer is *generated* (its time-of-flight flexes to connect whatever departure/arrival
pair the scheduler picks), so the departure end and the arrival end align to their own
bodies **independently** — the transfer + the two SOI seams absorb the join.

This is the structural reason re-aim is necessary. A replay-as-is loop would have to
satisfy launch-site rotation AND destination phase **simultaneously** through one launch
UT and a fixed transfer — a joint resonance of incommensurate periods, effectively
centuries between usable windows. The flexible joint converts that **multiplicative**
scheduling problem into two **independent, frequently-recurring** alignments (launch-body
rotation ~daily; destination phase ~per moon/synodic period).

### 3.3 The flexibility / rigidity map

| Link in the chain | Slack currency | Rigidity |
|---|---|---|
| Moment of launch | continuous time | flexible (master timing knob) |
| Launch → ascent → orbit | — | **rigid** (recorded, exact) |
| Departure loiter count | integer, bounded (~100 → 1–5) | flexible |
| Orbit → SOI exit | — | **rigid** (recorded, exact) |
| Departure SOI exit (angle + timing) | time + tolerated seam | flexible joint |
| Heliocentric transfer | — | **rigid but generated** (deterministic, perfect) |
| Destination SOI entry (angle + timing) | time + tolerated seam | flexible joint |
| In-SOI approach (incl. moon flybys) | — | **rigid** (recorded, exact) |
| Arrival loiter count | integer, bounded | flexible |
| Landing | — | **rigid** (recorded, exact) |

### 3.4 Variable structure

Optional pieces drop out per mission: *direct departure* ⇒ no departure parking loiter;
*direct landing / atmospheric slowdown* ⇒ no arrival loiter; a *same-body* mission
(Kerbin→Mun) has no heliocentric transfer segment. The chain is read from each
recording's segment classification, not stamped from a template.

### 3.5 Correctness priority (where the system must spend effort)

Two kinds of seam error, deliberately ranked:

- **In-SOI moon configuration relative to the vessel → MUST be right.** When a recorded
  arc flies by a moon but the moon isn't where it was recorded, the vessel rides a
  hyperbola and appears to "teleport" past empty space / a misplaced moon. On-camera,
  zoomed, immediately obvious. This is a **scheduler** responsibility — a recorded moon
  flyby is a transient SOI pass, i.e. a binding phase constraint on that moon (the same
  machinery that already makes Mun/Minmus loop timing work well). The renderer draws the
  arc at live positions and relies on the schedule.
- **SOI-boundary exit/entry *direction* mismatch → deferred.** "Exited to the planet's
  right but appears on the left in heliocentric zoom" is off-camera, across two zoom
  levels nobody cross-references. Accepted for v1; "which side gives" at the departure
  seam (re-point the recorded arc vs bend the transfer) is left for later.

### 3.6 Rendering scope (only while it happens)

A looped mission contributes nothing to the map until its launch. The wait is pure UI:
a **Warp-to-launch** button (jumps the global clock to ~15 s before the calculated launch
UT) and a **Time-to-launch** countdown in the Missions tab. The renderer never draws an
idle or future mission and never previews the chain ahead of the icon (an end-to-end
forward preview is a deferred advanced feature). At any instant the ghost occupies exactly
one segment; the renderer draws *that* segment, with the full traced path of the current
segment visible so the player sees where the ghost is going within it.

## 4. Scope (v1)

- **In:** single hop, launch-SOI → one other SOI; destination is a **single-moon** body
  (Duna, Eve, …) or a same-body target (Mun, Minmus). Exact non-looped recordings (the
  degenerate single-sub-chain case) keep working unchanged. Parity between map view (in
  flight) and the Tracking Station.
- **Deferred:** Jool / multi-moon destinations (mini-system), gravity assists, multi-hop
  chains, plane-aware / complex transfers, the end-to-end forward chain preview, and
  erasing the SOI-boundary *direction* seam.

## 5. Responsibility split (load-bearing)

The single most important architectural line. It keeps the renderer simple and puts the
hard correctness in the already-working scheduler:

- **Scheduler owns *when*.** Launch UT, loiter counts, arrival window — chosen so recorded
  arcs play against live bodies that ≈ match the recording, including the high-visibility
  moon-flyby phases.
- **Renderer owns *drawing the current segment*.** It draws recorded arcs relative to the
  relevant body **at the bodies' live positions** and **never repositions or nudges a
  celestial body**. It trusts the schedule. A moon that isn't where it was recorded is a
  scheduler miss — the renderer **logs it but does not correct it**.

## 6. Target architecture

### 6.1 Layered view

```
            ┌──────────────────────────────────────────────────────────────┐
  REUSED →  │  Scheduler stack (UNCHANGED): MissionLoopUnitBuilder,         │
            │  re-aim pipeline (ReaimedTrajectory, classifier, planner,     │
            │  loiter compressor), MissionPeriodicity, span clock           │
            │  (TryComputeSpanLoopUT / DecompressSpanUT), IPlaybackTrajectory│
            ├──────────────────────────────────────────────────────────────┤
   NEW   →  │  L1  ChainAssembler   (IPlaybackTrajectory + LoopUnit)        │  per-window, cached
  MODULE    │                       → GhostRenderChain (ordered RenderSeg.) │  assigns treatments
  "Parsek.  ├──────────────────────────────────────────────────────────────┤
   MapRender"│  L2  ChainSampler     Sample(chain, liveUT) → GhostSample    │  pure, scene-agnostic
            │                       {segment, treatment, pos|conic, phase,  │  uses span clock
            │                        coverage}                              │
            ├──────────────────────────────────────────────────────────────┤
            │  L3  GhostRenderDirector  per-frame, per-ghost:               │  THE single decision
            │      chain+sampler → GhostRenderIntent                        │  owner. subsumes ALL
            │      {visible, treatment, driveUT, iconPos, lineSpec, label}  │  today's gate flags
            ├───────────────────────────────┬──────────────────────────────┤
            │  L4  IGhostRenderTreatment     │  IGhostMapScene (adapter)    │
            │   • StockConicTreatment        │   • project pos → scene space │
            │     (proto-vessel + KSP orbit) │   • camera / scene gate       │  ← SCENE SPLIT
            │   • TracedPathTreatment        │   • proto-vessel lifecycle    │
            │     (polyline + marker/label)  │  Impls: MapViewScene,         │
            │   (pure followers of intent)   │         TrackingStationScene  │
            ├───────────────────────────────┴──────────────────────────────┤
            │  L5  GhostRenderReconciler   assert rendered-truth == intent  │  the tracer, promoted
            └──────────────────────────────────────────────────────────────┘
   ATOMS (UNCHANGED): TrajectoryPoint · OrbitSegment · TrackSection · terminal state · bounds
```

### 6.2 The central new abstraction: `GhostRenderChain`

This is the object the old code lacked. For one ghost in one cycle window it is the
ordered list of `RenderSegment`s — a **render-oriented view** over existing playback data,
not a copy of it.

```
struct RenderSegment
    SegmentKind  Kind            // Ascent, Loiter, Eject, Transfer, Approach, ArrivalOrbit, Landing, ...
    Treatment    Treatment       // StockConic | TracedPath  (assigned at assembly)
    double       StartUT, EndUT   // in the ASSEMBLED-chain clock (post-trim, post-reanchor)
    string       FrameBodyName    // body this segment is anchored to (launch / dest / Sun)
    SegmentPayload Payload        // conic: an OrbitSegment ref;  traced: a Points/TrackSection span ref
    bool         IsGenerated      // true for the re-aimed transfer
    bool         IsFlexibleSeamEntry / IsFlexibleSeamExit   // marks the SOI joints

class GhostRenderChain
    string             RecordingId
    int                CommittedIndex     // positional index, the LoopUnitSet contract
    IReadOnlyList<RenderSegment> Segments  // ordered by StartUT, contiguous (gaps allowed at flexible seams)
    double             WindowStartUT, WindowEndUT
    // built once per (BuildSignature, cycle window); cached; O(log n) locate by UT
```

**Treatment assignment is the unified orbit-vs-polyline decision** (today split inside
the polyline renderer): a segment is **StockConic** iff it corresponds to an `OrbitSegment`
that is renderable as a true conic at its UTs (above surface — reuse the existing
below-surface exclusion so a periapsis-below-radius arc is NOT a conic and its descent
samples become a TracedPath). Otherwise it is **TracedPath** (from `Points` /
`TrackSection` frames). The re-aimed transfer is simply an `OrbitSegment` with
`IsGenerated=true`, assigned StockConic. Putting this rule in ONE place (the assembler)
removes the read-side ambiguity that produced "position deep inside the planet" bugs.

### 6.3 `ChainSampler` (L2)

`Sample(chain, liveUT) → GhostSample`. Scene-agnostic and pure. Steps:

1. Map `liveUT` → assembled-chain UT via the **span clock** (`TryComputeSpanLoopUT`,
   including `DecompressSpanUT` for loiter cuts and the schedule for phase-locked
   launches). For a non-looped exact recording this is the identity.
2. Locate the `RenderSegment` containing that UT (O(log n)); return
   `{segment, treatment, body, interpolated position OR conic elements, phase,
   coverageValid}`. Outside any segment (pre-launch / post-end / inter-cycle tail) →
   `coverageValid=false` (the ghost renders nothing).

This collapses the 3–4 duplicate `(recording, UT) → where/what` resolvers
(`ParsekUI.TryComputeGhostWorldPosition`, the polyline leg builder, the orbit patches,
the flight positioner) into one.

### 6.4 `GhostRenderDirector` (L3) — the single decision owner

Once per frame, before any surface draws, for each ghost: sample the chain and emit a
`GhostRenderIntent`:

```
struct GhostRenderIntent
    bool       Visible
    Treatment  Treatment        // exactly one this frame
    double     DriveUT           // recorded-frame UT to drive the icon/conic at
    Vector3d   IconPosition      // resolved position on the active line
    LineSpec   Line              // conic bounds (arc clip) OR traced-path leg span
    string     Label
```

The Director **subsumes every scattered gate** that exists today: `activeLegRecordings`,
`ghostsWithSuppressedIcon`, the frame/real-time grace windows, `IsPolylineOwningGhostPhase`,
the stale-segment / body-frame-bounds continuity dance, and the loop epoch shift. Surface
ownership and visibility become one function with one output, testable in isolation. The
make-before-break swap at a treatment transition is decided here, atomically — the
incoming treatment is placed and verified before the outgoing one tears down — instead of
being negotiated implicitly between patches.

### 6.5 Treatments (L4) — pure followers

`StockConicTreatment` and `TracedPathTreatment` stop deciding anything. Each frame they
read the intent: *am I the active treatment for this ghost? then draw at `DriveUT` /
`Line`; else stand down.* No treatment ever reads another treatment's flag.

- **StockConicTreatment** keeps using stock KSP objects (proto-vessel + `OrbitRenderer`
  line), per the "use stock objects as much as possible" rule — the loiter, transfer, and
  arrival orbit are clean conics, so the stock orbit line gives the stock look and the
  full conic *ahead* of the icon for free. Because KSP co-owns `line.active` (it toggles
  it during SOI transitions / floating-origin shifts), this surface is **managed**: the
  treatment re-asserts the intent every frame and L5 catches residual blinks. (This is the
  one bounded asymmetry vs the fully-**owned** TracedPath surface; it is the accepted price
  of stock look.)
- **TracedPathTreatment** owns its polyline + marker/label entirely (nobody else touches
  them), so the icon and the path are produced together from one `DriveUT` and cannot
  disagree.

**Invariants enforced structurally:**
1. Exactly one treatment per ghost per frame (kills polyline/orbit double-draw).
2. Icon always on and always on its current segment's line (kills icon blink and
   icon-off-line drift).
3. Stock wherever the segment is a conic; traced-path only for recorded non-conic
   stretches.
4. Current segment only — no forward preview across seams.

### 6.6 Scene split (L4 adapter): `IGhostMapScene`

The only things implemented twice. `MapViewScene` (in flight) and `TrackingStationScene`
each provide: position→scene-space projection, the active camera, the scene-visibility
gate, and proto-vessel spawn/drive lifecycle. The Director, Sampler, Assembler, and
treatment *logic* are written once against this adapter and never know which scene they
are in. **The TS adapter is the largest piece of genuinely new work** — TS has no
looped-ghost animation today; under the rewrite it drives its proto-vessels from the
same Director output as map view.

### 6.7 Per-frame flow & execution order

```
[once per cycle-window change]  ChainAssembler.Build(trajectory, loopUnit) → cached GhostRenderChain
[every frame, fixed early order]
  for each ghost:
     sample   = ChainSampler.Sample(chain, liveUT)
     intent   = GhostRenderDirector.Decide(sample)        // single decision
     scene.Apply(intent) via the active treatment          // pure follower draws
[end of frame]
  GhostRenderReconciler.Check(intent vs actual rendered state)  // permanent assertion
```

The Director runs at a fixed execution order *before* the stock `OrbitRenderer` and the
treatment surfaces, so intent is published before anything reads it — replacing today's
fragile −50/0 ordering contract with an explicit one inside one module.

### 6.8 `GhostRenderReconciler` (L5)

The render tracer's decision-vs-truth probe, promoted from a debugging afterthought to a
permanent assertion: it reads the actual `line.active` / icon / orbit truth at end of
frame and flags any divergence from `intent`. It now compares against a real first-class
`intent` object instead of reverse-engineering scattered decisions.

## 7. Reuse vs Replace (existing code map)

**Reuse unchanged (the scheduler/playback substrate):**
- `IPlaybackTrajectory` (`IPlaybackTrajectory.cs`) — the unified trajectory interface;
  both recorded and `ReaimedTrajectory` implement it. The chain assembles from it.
- `ReaimedTrajectory` (`Reaim/ReaimedTrajectory.cs`) + the re-aim pipeline (classifier,
  window planner, loiter compressor, segment assembler) — substitutes `OrbitSegments`
  with the synthesized transfer per window; delegates `PartEvents`.
- `MissionLoopUnitBuilder` + `LoopUnit` / `LoopUnitSet` (`GhostPlaybackLogic.cs`) — the
  per-window member set, span, cadence, phase anchor, loiter cuts, owner-by-index contract.
- The span clock: `TryComputeSpanLoopUT`, `DecompressSpanUT`, `ResolveTrackingStationSampleUT`
  (`GhostPlaybackLogic.cs`) — the `(liveUT, unit, member) → assembled-UT` math. **Scene-agnostic; the Sampler calls it.**
- `MissionPeriodicity` — phase-lock / next-window scheduling.
- The data atoms (`TrajectoryPoint`, `OrbitSegment`, `TrackSection`, terminal state,
  bounds resolver) and the below-surface-orbit exclusion logic.

**Reuse, but relocated behind the new abstractions:**
- The proto-vessel **lifecycle** in `GhostMapPresence.cs` (create/destroy, `ghostMapVesselPids`,
  bounds caching) — kept, but moved behind `IGhostMapScene`; its ad-hoc per-frame
  *positioning/visibility* decisions are absorbed by the Director.
- The polyline **drawing** primitives in `GhostTrajectoryPolylineRenderer.cs` (Vectrosity
  leg build, scaled-space projection, warp-frozen re-projection) — kept as the
  `TracedPathTreatment` mechanics; its *ownership/visibility* logic (`activeLegRecordings`,
  head-UT gate, grace) is absorbed by the Director.
- The orbit-arc clipping (eccentric-anomaly segment culling) and icon-drive-at-recorded-UT
  mechanics from the orbit patches — kept as `StockConicTreatment` mechanics.

**Replace / delete (the scattered coordination):**
- The cross-patch gate flags and grace windows: `activeLegRecordings`,
  `ghostsWithSuppressedIcon`, `IsPolylineOwningGhostPhase` / `IsPolylineRecentlyOwning…`,
  `ghostOrbitLineGraceUntilFrame`, the stale-segment/body-frame-bounds continuity branches.
  All become the Director's single intent.
- The implicit −50/0 execution-order contract → explicit ordering inside the module.
- Per-surface independent visibility decisions in `GhostOrbitLinePatch` / `GhostOrbitIconDrivePatch`
  / `DrawMapMarkers` → pure followers of intent.

**Diagnostics:** `MapRenderProbe` / `MapRenderTrace` evolve into `GhostRenderReconciler`
(end-of-frame truth vs intent), now with a real intent object to compare against.

**Out of scope (untouched):** the flight-scene 3D ghost mesh path
(`GhostPlaybackEngine`, `IGhostPositioner`, `ParsekPlaybackPolicy`) — those are
flight-only and render meshes, not map surfaces; the new module coexists with them and
draws the map representation. Ghost interaction, recording, the recorder, and the solver.

## 8. Behavior

Per segment kind, as the live clock advances through a cycle window (each renders via its
assigned treatment, icon riding the line):

- **Ascent / atmospheric** (TracedPath): appears at launch; icon rides from the surface up;
  full segment path visible.
- **Loiter / parking orbit** (StockConic): full ellipse; icon rides it. Loiter *count* is
  the scheduler's trim; the renderer plays exactly the orbits present in the chain.
- **Ejection → SOI exit** (body-relative; StockConic or TracedPath by conic test): drawn
  relative to the launch body; ends at the SOI boundary.
- **Heliocentric transfer** (generated; StockConic): appears when the ghost ejects; single
  stock orbit object (heliocentric ellipse + SOI patch by stock default). Must look perfect.
- **SOI entry → approach** (destination-body-relative): drawn at live positions; moon
  flybys render correctly iff the scheduler matched the config.
- **Arrival loiter** (StockConic): optional; as departure loiter.
- **Landing** (TracedPath): must replay exactly; rigid hand-off from the preceding orbit.

Seam transitions over time:
- **Rigid seams** (within a sub-chain; ascent↔orbit; orbit↔landing): visually continuous,
  no jump/blink — the make-before-break swap guarantees the incoming treatment is placed
  before the outgoing tears down.
- **Flexible SOI seams**: a tolerated discontinuity is expected and **not hidden**
  (off-camera, high warp); the chain may carry a UT gap there.

## 9. Edge cases

Each: scenario → expected behavior → [v1 / deferred]. (Reviewers: extend this list.)

1. **Moon flyby with mismatched config.** Recorded arc passes a moon the scheduler left
   out of place → vessel "teleports" past empty space. *Renderer draws as-is at live
   positions, logs the recorded-vs-live moon phase offset, does not correct.* Correctness
   is the scheduler's binding constraint. [v1: constraint exists for Mun/Minmus; extend.]
2. **SOI-boundary direction seam.** Recorded exit direction ≠ heliocentric appearance.
   *Accepted, not corrected.* [Deferred.]
3. **Variable chain structure.** Direct departure (no parking loiter), direct landing (no
   arrival loiter), same-body (no transfer). *Assembled from classification; missing pieces
   are simply absent segments.* [v1.]
4. **Loiter trim continuity.** ~100 orbits → 1–5. *Whole-period cut is position/velocity
   continuous; no seam at the trim; renderer plays only the orbits in the chain.* [v1.]
5. **Pre-launch.** *Nothing on the map; Warp-to-launch + Time-to-launch UI handle the
   wait.* [v1.]
6. **End of cycle / self-overlap.** Period < span ⇒ multiple staggered instances live at
   once. *Each instance is an independent ghost with its own chain + clock position; the
   per-frame contract applies per instance. SC/TS render a single span instance (no overlap
   machinery there), per mission-abstractions.* [v1 within existing caps.]
7. **Debris ride-along.** *Debris is its own ghost following the same contract; not a
   special seam.* [v1.]
8. **Flight map ↔ Tracking Station.** *Identical contract/treatments; only the scene
   adapter differs. TS is new work (no looped animation today).* [v1: parity required.]
9. **Non-looped exact recording.** *Degenerate single-sub-chain chain, span clock = identity;
   must not regress.* [v1.]
10. **KSP toggles `line.active` mid-frame** (SOI transition, floating-origin shift). *The
    managed StockConicTreatment re-asserts intent each frame; the reconciler flags residual
    divergence.* [v1.]
11. **Below-surface orbit segment** (periapsis < body radius). *Fails the conic test ⇒
    TracedPath; descent samples merge into the polyline.* [v1.]
12. **Pause / scene entry mid-cycle / time-warp rate change.** *The sampler is a pure
    function of liveUT; entering a scene or changing warp resolves the same segment at the
    current clock with no special transient.* [v1: verify.]

## 10. What doesn't change

- The recording format, sidecars, and segment classification.
- The re-aim solver, the periodicity scheduler, the span clock, the loiter compressor — the
  renderer consumes their output; it never re-plans.
- Celestial body positions — the renderer never moves a body; moon-config correctness is
  the scheduler's.
- Ghost interaction (focus / target / Fly / re-fly) and the flight-scene 3D ghost meshes.
- The Missions UI, the loop model, and the "one loop per tree" rule.

## 11. Backward compatibility

No save/recording migration: the chain is derived at runtime from existing recorded data +
the existing loop-unit/scheduler output. Existing looped Missions render through the new
module on load with no persisted-state change. The module is a rendering replacement, not a
data change.

## 12. Diagnostic logging

The map/TS render path is otherwise un-debuggable (the whole reason for the tracer). Every
decision must be reconstructable from the log (subsystem tag `MapRender`):

- **Chain assembly** (`Verbose`, per window per ghost): segment count, kinds, treatments,
  UT ranges, which segments are generated, flexible-seam markers.
- **Segment locate** (`VerboseRateLimited`, per ghost): the located segment + its treatment
  for the current UT (shared key to avoid spam).
- **Intent / treatment decision** (`VerboseRateLimited`, on change): active treatment + why
  (segment kind), confirmation the other stands down, the `DriveUT`.
- **Seam transition** (`Info`, on change): rigid vs flexible, outgoing→incoming, UT; for
  flexible SOI seams the tolerated discontinuity magnitude.
- **Moon-config diagnostic** (`Verbose`, arrival sub-chain): recorded-vs-live phase offset
  of any moon the arc passes close to — surfaces scheduler misses.
- **Reconciler anomalies** (`Warn`): decision-vs-truth divergence, icon-jump, line-blink,
  polyline-orbit overlap — the permanent assertions.
- **Pre-launch / no-coverage** (`Verbose`, on change): ghost rendered nothing because the
  clock is outside any cycle window / segment.

## 13. Test plan

Pure / synthetic-recording testable (the heavy correctness lives here):

- **Chain assembly from classification** — synthetic recordings for each structure
  (full interplanetary, direct departure, direct landing, same-body, non-looped) → assert
  the ordered segment list (kinds + treatments + UT ranges). *Guards variable-structure
  assembly and the treatment decision.*
- **Treatment assignment** — a periapsis-below-radius orbit segment → TracedPath; an
  above-surface conic → StockConic; a generated transfer → StockConic. *Guards a conic
  drawn as polyline or vice-versa, and the below-surface merge.*
- **Segment locate** — assembled chain + a UT sweep → exactly one active segment per UT,
  none in gaps. *Guards double-draw and seam gaps.*
- **Span-clock integration** — looped member at various live UTs (incl. loiter cuts and a
  future-dated launch) → correct assembled-chain UT and segment. *Guards loop-shift and
  decompression bugs.*
- **Loiter-trim continuity** — trimmed chain → no positional discontinuity at the cut.
  *Guards a seam appearing at a whole-period trim.*
- **Intent invariants** — driving a chain through all transitions → never two treatments
  active, never icon-off-line. *Guards the structural invariants directly.*
- **Reconciler** — inject a forced `line.active` toggle → the reconciler flags it. *Guards
  the permanent assertion itself.*
- **Log assertion tests** — capture the sink; assert assembly / locate / intent / seam /
  moon-config lines appear with the right fields. *Guards diagnostic coverage surviving
  refactor.*
- **Non-looped regression** — exact single recording renders as the degenerate chain.
  *Guards the already-working exact playback.*

In-game (live KSP): map↔TS parity, a real looped Mun mission watched through a cycle, a
moon-flyby arrival, a high-warp SOI seam, self-overlapping instances.

## 14. Open questions

1. **Chain cache keying.** Key on `BuildSignature` + cycle-window index? Confirm the
   invalidation triggers (config change, body geometry change, cadence change) match the
   scheduler's existing `BuildSignature` so the chain rebuilds exactly when the loop unit does.
2. **Flight map-view UT source.** In flight, does the Director source the ghost's UT from
   the flight playback engine's state or independently re-derive it via the span clock? They
   must agree; re-deriving via the shared pure span clock avoids cross-subsystem state but
   should be confirmed against the engine for the non-looped and overlap cases.
3. **Treatment swap atomicity in practice.** The make-before-break swap assumes the incoming
   treatment can be placed and verified within one frame. Confirm there's no KSP constraint
   (proto-vessel orbit re-seed latency) that forces a one-frame grace — if there is, model it
   explicitly in the Director rather than as a scattered grace window.
4. **How much of `GhostMapPresence` lifecycle is genuinely reusable** vs entangled with the
   old positioning — to be answered in Step-4 exploration.
5. **Additional v1 edge cases** — to be expanded by a dedicated review pass.

## 15. References

- `docs/dev/done/plans/reaim-interplanetary-transfers.md` — deterministic transfer (Lambert).
- `docs/dev/done/plans/reaim-loiter-compression.md` — whole-period loiter trimming.
- `docs/dev/design-mission-periodicity.md` — launch-window scheduling (`P`, constraints,
  countdown); "replay as-is" superseded.
- `docs/dev/design-mission-abstractions.md` — Mission / selection / span-clock / overlap;
  the Phase-F TS gap.
- `docs/parsek-ghost-trajectory-rendering-design.md` — existing rendering surfaces.
- `docs/dev/design-map-ts-render-tracer.md` — the observability layer; its decision-vs-truth
  probe becomes the reconciler.
- Key source seams: `GhostPlaybackLogic.cs` (span clock, LoopUnit/LoopUnitSet),
  `Reaim/ReaimedTrajectory.cs`, `IPlaybackTrajectory.cs`, `GhostMapPresence.cs`,
  `Display/GhostTrajectoryPolylineRenderer.cs`, `Patches/GhostOrbitLinePatch.cs`,
  `MapRenderProbe.cs` / `MapRenderTrace.cs`.
