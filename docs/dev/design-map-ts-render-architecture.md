# Design: Map / Tracking-Station Ghost Render Architecture (Clean Modular Rewrite)

*Status: Step-3 design document for a clean, modular rewrite of the map-view and
Tracking-Station ghost-render path. The implementation will REPLACE the current
smear of Harmony patches + OnGUI passes + lifecycle ticks with a new module whose
internal structure mirrors the gameplay model, reusing existing solver/playback
code and proven snippets but re-organizing WHO decides what. It does NOT specify
method-level code changes — that is Step 4 (explore + plan). It does NOT change the
recording format, the re-aim solver, or the periodicity scheduler; it changes how the
renderer CONSUMES them.*

*Supersedes the precursor note `design-map-ts-looped-mission-render.md` (folded in).
Builds on: `docs/dev/done/plans/reaim-interplanetary-transfers.md`,
`docs/dev/done/plans/reaim-loiter-compression.md`,
`docs/dev/design-mission-periodicity.md` (its "replay as-is, no re-aim" decision is
superseded — see that doc's banner), `docs/dev/design-mission-abstractions.md`,
`docs/parsek-ghost-trajectory-rendering-design.md`, and
`docs/dev/design-map-ts-render-tracer.md`.*

> **Template map (per `development-workflow.md` §3):** Problem = §1; Terminology = §2;
> Mental Model = §3; Data Model = §6.2 + §6.10; Behavior = §9; Edge Cases = §10; What
> Doesn't Change = §11; Backward Compatibility = §12; Diagnostic Logging = §13; Test
> Plan = §14.

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

3. **The Tracking Station already animates looped ghosts, but only at the per-recording
   level** (the Phase-F span-clock remap: `ResolveTrackingStationSampleUT`, wired through
   `ParsekTrackingStation.Update` → `UpdateTrackingStationGhostLifecycle`). It positions
   each member at its own remapped UT, but it has **no chain re-assembly** (the
   heterogeneous recorded→generated→recorded hand-off, treatment switching, the
   make-before-break swap) and, by design, no overlap machinery (it renders a single span
   instance). So the TS gap is narrower than "unimplemented" — it is "extend the existing
   Phase-F remap into the chain model," not "build TS animation from nothing."

The fix is to make the renderer's internal model *match the gameplay model*: a
variable-structure **chain** of typed segments joined at typed seams, with one
per-frame decision owner over the Parsek-owned surfaces and dumb follower surfaces. The
rewrite is the opportunity to collapse the scattered coordination into that structure.

## 2. Terminology

- **Ghost** — a playback-only representation of a recorded vessel on the map/TS. The
  player *watches* ghosts; ghost interaction (focus / target / Fly / re-fly) is already
  implemented and out of scope.
- **Segment** — a contiguous stretch of a ghost's path with one render treatment AND one
  body frame.
  - **Recorded segment** — replayed exactly from recorded data (ascent, burns, in-SOI
    arcs, landing). Non-conic stretches are *recorded-point* segments.
  - **Generated segment** — computed deterministically by the solver, not backed by
    recorded points (the heliocentric transfer). Always a clean conic.
- **Sub-chain** — a maximal run of recorded segments anchored to one body's frame: the
  **departure sub-chain** (launch body) and the **arrival sub-chain** (destination body).
- **Joint** — a boundary between two segments. **Rigid** joints must connect cleanly
  (sub-chain-internal seams, the ascent↔orbit and orbit↔landing hand-offs). **Flexible**
  joints carry slack and a *tolerated, not-hidden* visible discontinuity (the two SOI
  boundary seams).
- **Treatment** — how a segment is drawn: **stock-conic** (a stock KSP object —
  proto-vessel icon + KSP orbit line) or **traced-path** (our drawn polyline of recorded
  points + marker/label icon).
- **Chain** — the per-cycle-window, per-instance ordered list of typed segments for one
  ghost, assembled from its (possibly re-aimed) trajectory + the loop unit's
  window/anchor/cuts.
- **Cycle window / instance** — `[launch UT … end UT]` of one relaunch. A self-overlapping
  mission has several staggered *instances* live at once; each is a distinct ghost with
  its own chain and clock position. A looped ghost renders only while the live clock is
  inside one of its cycle windows.
- **Scheduler** — the existing re-aim + periodicity machinery that chooses *when* (launch
  UT, loiter counts, arrival window) so recorded arcs play against live bodies that
  ≈ match the recording. The renderer consumes its output and does not re-plan.
- **Solver** — the orbital-mechanics engine behind the scheduler (Lambert transfer,
  patched-conic encounter). A replaceable module (see §6.9).
- **Render intent** — the renderer's single per-frame, per-ghost decision: visible?,
  treatment, drive-UT, icon position, line spec, label.

## 3. Mental model (gameplay, necessity first)

### 3.1 The chain

A looped mission's path is a **variable-structure chain**, assembled per cycle window
from the recorded segment classification — never a fixed template:

```
 ── DEPARTURE SUB-CHAIN ──────────►  ⟂  ──TRANSFER──  ⟂  ──── ARRIVAL SUB-CHAIN ─────►
 launch · ascent · [loiter] · eject     (generated)      SOI-entry · approach · [loiter] · land
 anchored to LAUNCH BODY frame          clean conic       anchored to DESTINATION BODY frame
   rigid recorded segments              away from         rigid recorded segments
        ▲ flexible: launch UT,          the SOI seams        ▲ flexible: arrival window
          departure loiter count                              (→ moon config), arrival loiter
        ⟂ flexible SOI exit               ⟂ flexible SOI entry
```

This is the **general target model**. What is actually renderable in v1 depends on the
mission type (§4): a faithful same-parent mission renders the whole chain; a re-aimed
cross-parent mission renders the conic subset + orbital arrival.

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
| Heliocentric transfer | — | **generated, deterministic** |
| Destination SOI entry (angle + timing) | time + tolerated seam | flexible joint |
| In-SOI approach (incl. moon flybys) | — | **rigid** (recorded, exact) |
| Arrival loiter count | integer, bounded | flexible |
| Landing | — | **rigid** (recorded, exact) |

The generated transfer is a **clean conic away from the SOI seams**. Its v1 stitching
anchors to the recorded SOI-exit state and swaps only the post-exit velocity to the
solver's departure velocity, so the departure SOI edge carries an accepted velocity
discontinuity (the flexible-seam artifact) — not a "byte-perfect" join. "Looks perfect"
means the conic body itself is clean, not that the SOI seam is continuous.

### 3.4 Variable structure

Optional pieces drop out per mission: *direct departure* ⇒ no departure parking loiter;
*direct landing / atmospheric slowdown* ⇒ no arrival loiter; a *same-body* mission has no
heliocentric transfer. The chain is read from each recording's segment classification,
not stamped from a template.

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
  levels nobody cross-references. Accepted for v1.

### 3.6 Rendering scope (only while it happens)

A looped mission contributes nothing to the map until its launch. The wait is pure UI: a
**Warp-to-launch** button (jumps the global clock to ~15 s before the calculated launch
UT) and a **Time-to-launch** countdown in the Missions tab. The renderer never draws an
idle or future mission and never previews the chain ahead of the icon (an end-to-end
forward preview is a deferred advanced feature). At any instant the ghost occupies exactly
one segment; the renderer draws *that* segment, with the full traced path of the current
segment visible so the player sees where the ghost is going within it.

## 4. Scope (v1)

The renderable chain depends on the mission type, because re-aim only engages for
cross-parent targets and currently surfaces only conic segments:

- **Same-parent / same-body missions (Mun, Minmus) — FAITHFUL replay.** Re-aim does not
  engage; the recording plays as-is with periodicity-scheduled launch windows. The
  **whole chain renders** (recorded ascent/loiter/approach/landing as TracedPath +
  conics), because `Points` / `TrackSections` are present. This is the richest v1 case.
- **Cross-parent interplanetary missions (Duna, Eve, …) — RE-AIMED.** `ReaimedTrajectory`
  substitutes the heliocentric leg and exposes **`OrbitSegments` only** (`Points` /
  `TrackSections` are empty on the re-aimed adapter), and re-aim v1 is **orbital arrival,
  no re-stitched descent** (`reaim-interplanetary-transfers.md` defers the target-surface
  S4 leg). So the v1 renderable interplanetary chain is the **conic subset**: departure
  orbit/ejection, the generated transfer, and the arrival capture orbit — ending at
  orbital arrival. The recorded ascent and descent *surface tracks* do not render for a
  re-aimed mission until a re-aim-adapter prerequisite lands (see below).
- **Non-looped exact recordings** keep working unchanged (the degenerate single-sub-chain
  case, span clock = identity).
- **Map view (in flight) ↔ Tracking Station parity** is required for what each case renders.

**Named prerequisite (out of this module, gating the full interplanetary chain):**
re-timing the recorded surface/atmospheric ascent and descent tracks onto the synthesized
spans — i.e. populating `Points` / `TrackSections` on the re-aimed adapter so the
TracedPath treatment has data on a re-aimed mission. Until then, interplanetary v1 is
conic-only / orbital-arrival. This is a solver/adapter task (§6.9), not a render task; the
render architecture is designed for the full chain so it needs no change when the data
arrives.

- **Deferred:** Jool / multi-moon destinations (mini-system), gravity assists, multi-hop
  chains, plane-aware / complex transfers, the end-to-end forward chain preview, the
  re-aimed descent/ascent surface-track re-stitch (above), and erasing the SOI-boundary
  *direction* seam.

## 5. Responsibility split (load-bearing)

Three layers, three owners. The point is to keep the renderer simple and put the hard
correctness in the already-working scheduler/solver:

- **Solver owns *the orbital mathematics*** (transfer generation, encounter solve) behind
  a replaceable module boundary (§6.9). We do **not** implement orbital mechanics from
  scratch — the boundary lets a library replace the current hand-rolled Lambert with zero
  render-side impact.
- **Scheduler owns *when*.** Launch UT, loiter counts, arrival window — chosen so recorded
  arcs play against live bodies that ≈ match the recording, including the high-visibility
  moon-flyby phases.
- **Renderer owns *drawing the current segment*.** It depends only on the *segment output*
  (the `OrbitSegments` / recorded points it is handed), never on the solver internals or
  scheduler reasoning. It draws recorded arcs relative to the relevant body **at the
  bodies' live positions** and **never repositions or nudges a celestial body**. A moon
  that isn't where it was recorded is a scheduler miss — the renderer **logs it but does
  not correct it**.

## 6. Target architecture

### 6.1 Layered view

```
            ┌──────────────────────────────────────────────────────────────┐
  SOLVER →  │  Orbital-solver module (REPLACEABLE, §6.9): transfer/Lambert  │
  MODULE    │  + patched-conic, behind ITransferSolver. May be a library.   │
            ├──────────────────────────────────────────────────────────────┤
  REUSED →  │  Scheduler stack: MissionLoopUnitBuilder (owns LoopUnitSet),  │
            │  re-aim pipeline (ReaimedTrajectory, ReaimPlaybackResolver,    │
            │  classifier/planner/loiter-compressor), MissionPeriodicity,    │
            │  span clock (TryComputeSpanLoopUT / DecompressSpanUT),         │
            │  IPlaybackTrajectory                                           │
            ├──────────────────────────────────────────────────────────────┤
   NEW   →  │  L1  ChainAssembler   (IPlaybackTrajectory + LoopUnit +       │  per-(window,instance)
  MODULE    │       declined/faithful signal) → GhostRenderChain            │  cached; assigns
  "Parsek.  │                                                               │  treatments + frames
   MapRender"│  L2  ChainSampler     Sample(chain, liveUT) → GhostSample    │  pure, scene-agnostic
            │  L3  GhostRenderDirector  per-frame, per-instance:            │  single owner over
            │       chain+sampler → GhostRenderIntent                       │  PARSEK-owned surfaces
            ├───────────────────────────────┬──────────────────────────────┤
            │  L4  IGhostRenderTreatment     │  IGhostMapScene (adapter)    │
            │   • StockConicTreatment        │   • project pos → scene space │
            │     (stock proto + KSP orbit,  │   • camera / scene gate       │  ← SCENE SPLIT
            │      MANAGED vs KSP)           │   • proto-vessel lifecycle    │
            │   • TracedPathTreatment        │   • single floating-origin    │
            │     (polyline + marker, OWNED) │     frame per cycle           │
            │   (pure followers of intent)   │  Impls: MapViewScene,         │
            │                                │         TrackingStationScene  │
            ├───────────────────────────────┴──────────────────────────────┤
            │  L5  GhostRenderReconciler  (REUSES MapRenderTrace/Probe):    │  pervasive, always-on
            │       assert rendered-truth == intent at every decision point │  observability
            └──────────────────────────────────────────────────────────────┘
   ATOMS (UNCHANGED): TrajectoryPoint · OrbitSegment · TrackSection · terminal state · bounds
```

### 6.2 The central new abstraction: `GhostRenderChain`

This is the object the old code lacked. For one ghost *instance* in one cycle window it is
the ordered list of `RenderSegment`s — a **render-oriented view** over existing playback
data, not a copy of it.

```
struct RenderSegment
    SegmentKind  Kind            // Ascent, Loiter, Eject, Transfer, Approach, ArrivalOrbit, Landing, ...
    Treatment    Treatment       // StockConic | TracedPath  (assigned at assembly)
    double       StartUT, EndUT   // ASSEMBLED-chain clock (post-trim, post-reanchor)
    string       FrameBodyName    // EXACTLY ONE body frame per segment (see split rule)
    SegmentPayload Payload        // conic: an OrbitSegment ref;  traced: a Points/TrackSection span ref
    bool         IsGenerated      // true for the re-aimed transfer
    SeamKind     LeadingSeam, TrailingSeam   // Rigid | FlexibleSoi | None

class GhostRenderChain
    string  RecordingId
    int     CommittedIndex        // positional index, the LoopUnitSet contract
    int     InstanceKey           // cycle/instance discriminator — distinguishes overlap instances
    IReadOnlyList<RenderSegment> Segments   // ordered by StartUT; gaps allowed only at FlexibleSoi seams
    double  WindowStartUT, WindowEndUT
    bool    IsFaithfulFallback    // solver declined → recorded-as-is (see §6.9, §10)
    // built once per (BuildSignature, reaim-window index, InstanceKey); cached; O(log n) locate by UT
```

Two assembler rules the reviews surfaced:

- **Treatment assignment is the unified orbit-vs-polyline decision** (today split inside
  the polyline renderer): a segment is **StockConic** iff it corresponds to an
  `OrbitSegment` renderable as a true conic at its UTs (above surface — reuse the existing
  below-surface-exclusion logic `IsOrbitSegmentBelowSurface` + `ComputeOrbitalCoverIntervals`
  in `GhostTrajectoryPolylineRenderer.cs`, so a periapsis-below-radius arc is NOT a conic and
  its descent samples become a TracedPath).
  Otherwise it is **TracedPath**. The re-aimed transfer is an `OrbitSegment` with
  `IsGenerated=true`, assigned StockConic. One place for this rule removes the read-side
  ambiguity that produced "position deep inside the planet" bugs.
- **One body frame per segment.** A recorded arc that grazes a moon's SOI changes body
  frame mid-arc; the assembler **splits the `RenderSegment` at every intra-arc SOI
  crossing** so each has exactly one `FrameBodyName`. (A moon flyby is therefore both a
  scheduler phase constraint *and* a renderer frame split.)

### 6.3 `ChainSampler` (L2)

`Sample(chain, liveUT) → GhostSample`. Scene-agnostic and pure. Steps:

1. Map `liveUT` → assembled-chain UT via the **span clock** (`TryComputeSpanLoopUT`,
   including `DecompressSpanUT` for loiter cuts and the schedule for phase-locked
   launches). For a non-looped exact recording this is the identity. Because it is pure in
   `liveUT`, a quickload is simply a different sample — no persisted recorded-UT is trusted.
2. Locate the `RenderSegment` containing that UT (O(log n)); return
   `{segment, treatment, frameBody, interpolated position OR conic elements, phase,
   coverage}`. **Coverage is three-valued:** `InSegment`, `InInteriorGap` (between two
   chain segments — e.g. a flexible-seam UT gap the clock landed inside), or `OutsideWindow`
   (pre-launch / post-end / inter-cycle tail). The Director treats these differently (§6.4).

This collapses the 3–4 duplicate `(recording, UT) → where/what` resolvers into one.

### 6.4 `GhostRenderDirector` (L3) — the single owner over Parsek surfaces

Once per frame, before any surface draws, for each ghost instance: sample the chain and
emit a `GhostRenderIntent`. The Director **subsumes every Parsek-owned scattered gate**
that exists today: `activeLegRecordings`, `ghostsWithSuppressedIcon`, the frame/real-time
grace windows, `IsPolylineOwningGhostPhase`, the stale-segment / body-frame continuity
dance, the loop epoch shift, and the flight-map presence drive currently in
`ParsekPlaybackPolicy.CheckPendingMapVessels` (§7). Surface ownership and visibility among
the Parsek surfaces become one function with one output, testable in isolation.

Three behaviors the reviews require here:

- **Gap classification.** `InInteriorGap` (mid-chain, e.g. warp stepped across a
  flexible-seam gap) ⇒ **hold the last intent / suppress the icon-jump anomaly**, do not
  blink the ghost off. `OutsideWindow` ⇒ render nothing (retire). The difference is
  load-bearing at high warp where one frame can step over a whole segment.
- **Make-before-break swaps** at a treatment transition are decided here, atomically — the
  incoming treatment is placed and verified before the outgoing tears down. If a KSP
  constraint (proto-vessel orbit re-seed latency, open question §15.1) forces a one-frame
  settle, it is modeled explicitly here as a bounded swap state, NOT as a scattered grace
  window.
- **Single-owner scope is explicit.** The Director fully owns the Parsek surfaces
  (TracedPath polyline/marker, and the *decision* to show/hide the stock surface). It does
  **not** own KSP's internal `line.active` toggling — that boundary stays a managed
  contract (§6.5).

### 6.5 Treatments (L4) — pure followers

`StockConicTreatment` and `TracedPathTreatment` stop deciding visibility. Each frame they
read the intent: *am I the active treatment for this instance? then draw at `DriveUT` /
`Line`; else stand down.* No treatment reads another treatment's flag.

- **StockConicTreatment** uses stock KSP objects (proto-vessel + `OrbitRenderer` line), per
  "use stock objects as much as possible" — the loiter, transfer, and arrival orbit are
  clean conics, so the stock line gives the stock look and the full conic *ahead* of the
  icon for free. **This surface is MANAGED, not fully owned:** KSP co-owns `line.active`
  (it toggles it during SOI transitions / floating-origin shifts), so the treatment
  re-asserts the intent every frame and the reconciler catches residual blinks. Honestly: a
  per-frame Parsek↔KSP contract survives here for this one surface; the rewrite does not
  eliminate it, it *localizes and instruments* it (one place re-asserts, the reconciler
  flags divergence) instead of spreading it across patches.
- **TracedPathTreatment** fully owns its polyline + marker/label (nobody else touches them),
  so the icon and the path are produced together from one `DriveUT` and cannot disagree.

**Invariants enforced structurally (for the Parsek-owned decision):**
1. Exactly one treatment active per instance per frame (kills polyline/orbit double-draw).
2. Icon always on and always on its current segment's line (kills icon blink and
   icon-off-line drift).
3. Stock wherever the segment is a conic; traced-path only for recorded non-conic stretches.
4. Current segment only — no forward preview across seams.

### 6.6 Scene split (L4 adapter): `IGhostMapScene`

The only things implemented twice. `MapViewScene` (in flight) and `TrackingStationScene`
each provide: position→scene-space projection, the active camera, the scene-visibility
gate, proto-vessel spawn/drive lifecycle, **a single floating-origin frame per Director
cycle** (so both treatments project against the same origin — closes the swap/shift offset
jump), and **camera-focus continuity across a treatment swap** (re-home focus onto the
incoming surface, or keep a persistent focus anchor, so destroying a focused proto-vessel
mid-swap doesn't drop the camera). The Director/Sampler/Assembler/treatment *logic* are
written once against this adapter and never know which scene they are in.

**TS adapter = extend, not invent.** TS already runs the Phase-F per-recording span-clock
remap (`ResolveTrackingStationSampleUT` via `UpdateTrackingStationGhostLifecycle`). The
work is to drive its proto-vessels from the chain model (treatment switching,
make-before-break) instead of a single per-recording remap, and to keep its by-design
single-span-instance behavior (no overlap machinery in SC/TS).

### 6.7 Per-frame flow & execution order

```
[scheduler stack, unchanged]  MissionLoopUnitBuilder.Build → LoopUnitSet  (the loop-unit owner)
[once per (window, reaim-index, instance) change]
  ChainAssembler.Build(IPlaybackTrajectory, LoopUnit, faithfulSignal) → cached GhostRenderChain
[every frame, fixed early order, before stock OrbitRenderer]
  for each ghost instance:
     sample = ChainSampler.Sample(chain, liveUT)        // pure
     intent = GhostRenderDirector.Decide(sample)        // single Parsek-owned decision
     scene.Apply(intent) via the active treatment        // pure follower draws
     reconciler.NoteIntent(instance, intent)             // observability
[end of frame]
  reconciler.CheckTruth(intent vs actual line/icon/orbit state)   // permanent assertion
```

The Director runs at a fixed execution order *before* the stock `OrbitRenderer` and the
treatment surfaces, replacing today's fragile −50/0 ordering contract with an explicit one
inside one module. **Loop-unit provenance is explicit:** `MissionLoopUnitBuilder.Build` is
already the genuine per-scene loop-unit owner (cached as `cachedLoopUnits` on flight / TS /
KSC); the Director consumes that `LoopUnitSet` directly and simply stops reading the flight
engine's `GhostPlaybackEngine.CurrentLoopUnits` passthrough (a thin relay of the same data).
The bypass is trivial because the directly-buildable source already exists (resolves former
open question 2).

### 6.8 `GhostRenderReconciler` (L5) — reuse the tracer, everywhere

The render tracer is **reused and integrated pervasively**, not promoted at the end.
`MapRenderTrace` / `MapRenderProbe` (their tiered Tier-A structural / Tier-B change /
Tier-C anomaly events, the detailed-window registry, the on-change emit, the end-of-frame
truth probe) become the module's standing observability layer, extended to every new
decision point: assembly, locate, gap classification, intent, treatment swap, seam,
scene-adapter projection, and the moon-config diagnostic. It compares against a real
first-class `intent` object instead of reverse-engineering scattered decisions, and it
gains predicates for the new anomaly classes (polyline origin-shift jump, decision-vs-truth
across a swap, gap-vs-retire misclassification). Logging is a design requirement at every
branch (§13), not optional.

### 6.9 Orbital-solver module boundary (replaceable)

Per the directive *don't implement orbital mechanics from scratch*: the transfer/encounter
mathematics lives behind a narrow interface (e.g. `ITransferSolver` — Lambert solve,
patched-conic encounter), so the current hand-rolled ~150-line Lambert can be swapped for a
library with **zero render-side impact** (the renderer consumes only `OrbitSegments`,
provenance-agnostic). The boundary also defines the **declined/degenerate** contract: when
a window's solve fails (no encounter, energy/eccentricity rejection, steep inclination), the
solver returns a *decline* signal; the scheduler falls back to faithful (un-re-aimed)
replay for that window (`ReaimPlaybackResolver`'s existing "fail to faithful, never
half-applied"), and the `ChainAssembler` marks the chain `IsFaithfulFallback=true` and
assembles the *recorded* transfer (drawn at its recorded, off-window position) rather than a
missing/blank transfer segment. Extracting the solver into its own module is a related
workstream; this doc fixes the *boundary* so the render module is decoupled from it now.

### 6.10 Data model summary

New runtime-only types (nothing persisted): `RenderSegment`, `GhostRenderChain` (§6.2),
`GhostSample` (§6.3 — `{segment, treatment, frameBody, position|conic, phase, coverage}`),
`GhostRenderIntent` (`{visible, treatment, driveUT, iconPosition, lineSpec, label}`), and
the `Treatment` / `SegmentKind` / `SeamKind` / `Coverage` enums. All derived each frame /
each window from existing recorded data + scheduler output; no save-format change.

## 7. Reuse vs Replace (existing code map)

**Reuse unchanged (solver/scheduler/playback substrate):**
- `IPlaybackTrajectory` — the unified trajectory interface; both recorded and
  `ReaimedTrajectory` implement it. The chain assembles from it.
- `ReaimedTrajectory` (`Reaim/ReaimedTrajectory.cs`) + `ReaimPlaybackResolver` (per-window
  cache, "fail to faithful") + the re-aim pipeline — substitutes `OrbitSegments` with the
  synthesized transfer per window; `Points`/`TrackSections` empty (the v1 scope boundary in §4).
- `MissionLoopUnitBuilder` + `LoopUnit` / `LoopUnitSet` — **the loop-unit owner**; the
  Director consumes its output directly (§6.7).
- The span clock: `TryComputeSpanLoopUT`, `DecompressSpanUT`, `ResolveTrackingStationSampleUT`
  (`GhostPlaybackLogic.cs`) — pure, scene-agnostic `(liveUT, unit, member) → assembled-UT`.
- `MissionPeriodicity`; the data atoms; the below-surface-exclusion / orbital-cover logic
  (`IsOrbitSegmentBelowSurface`, `ComputeOrbitalCoverIntervals`) in
  `GhostTrajectoryPolylineRenderer.cs`.

**Reuse, but relocated behind the new abstractions:**
- The proto-vessel **lifecycle** in `GhostMapPresence.cs` (create/destroy,
  `ghostMapVesselPids`, bounds caching) → behind `IGhostMapScene`; its per-frame
  positioning/visibility decisions are absorbed by the Director.
- The polyline **drawing** primitives in `GhostTrajectoryPolylineRenderer.cs` (Vectrosity
  leg build, scaled-space + warp-frozen re-projection) → the `TracedPathTreatment`
  mechanics; its ownership/visibility logic is absorbed by the Director.
- The orbit-arc clipping + icon-drive-at-recorded-UT mechanics from the orbit patches → the
  `StockConicTreatment` mechanics.
- `MapRenderTrace` / `MapRenderProbe` → **reused and extended** as the reconciler (§6.8),
  not rewritten.

**Replace / delete (the scattered coordination):**
- The cross-patch gate flags and grace windows (`activeLegRecordings`,
  `ghostsWithSuppressedIcon`, `IsPolylineOwningGhostPhase` / `…Recently…`,
  `ghostOrbitLineGraceUntilFrame`, stale-segment/body-frame branches) → the Director's intent.
- The implicit −50/0 execution-order contract → explicit ordering inside the module.
- **`ParsekPlaybackPolicy` — SPLIT.** Its 3D-mesh/spawn-decision half stays out of scope;
  its **flight-scene map-presence half** (`CheckPendingMapVessels`,
  `ResolveMapPresenceSampleUT`, the loop-epoch-shift drive) is **in scope** — one of the
  scattered map-decision sites the Director replaces. (Leaving it standing would re-create a
  second parallel flight-map owner, the exact "no single owner" problem this kills.)

**Out of scope (untouched):** the flight-scene 3D ghost **mesh** path —
`GhostPlaybackEngine` (mesh lifecycle/visibility; note the Director stops reading its
`CurrentLoopUnits` passthrough — the loop-unit source is `MissionLoopUnitBuilder.Build`
directly, §6.7), `IGhostPositioner` (mesh positioning), and `ParsekPlaybackPolicy`'s
mesh/spawn half. Ghost interaction, recording, the recorder, and the scheduler's reasoning.

## 8. Renderable chain by mission type (the v1 scope, at a glance)

| Mission type | Re-aim? | Trajectory data available | Renderable chain in v1 |
|---|---|---|---|
| Same-body / same-parent (Mun, Minmus) | no — faithful | full `Points` + `TrackSections` + `OrbitSegments` | **whole chain**: ascent → loiter → approach → loiter → landing (TracedPath + StockConic) |
| Cross-parent interplanetary (Duna, Eve, …) | yes — `ReaimedTrajectory` | `OrbitSegments` only (`Points`/`TrackSections` empty) | **conic subset, to orbital arrival**: departure orbit/eject → generated transfer → arrival capture orbit. Ascent/descent surface tracks deferred behind the §4 prerequisite |
| Non-looped exact recording | n/a | full recorded data | identity (span clock = identity); whole recorded path |

The render module is designed for the whole chain; the interplanetary row is narrower
only because the re-aim adapter does not yet surface recorded surface tracks (§4
prerequisite). When that data arrives, the same assembler/treatments render it with no
architecture change.

## 9. Behavior

Per segment kind, as the live clock advances through a cycle window (each renders via its
assigned treatment, icon riding the line). Which segments exist depends on §4:

- **Ascent / atmospheric** (TracedPath; faithful missions only in v1): appears at launch;
  icon rides from the surface up; full segment path visible.
- **Loiter / parking orbit** (StockConic): full ellipse; icon rides it. Loiter *count* is
  the scheduler's trim; the renderer plays exactly the orbits present in the chain.
- **Ejection → SOI exit** (body-relative; StockConic or TracedPath by the conic test):
  drawn relative to the launch body; ends at the SOI boundary.
- **Heliocentric transfer** (generated; StockConic): appears when the ghost ejects; a single
  stock orbit object (heliocentric ellipse + SOI patch by stock default). Clean conic away
  from the accepted SOI-edge velocity discontinuity.
- **SOI entry → approach** (destination-body-relative, frame-split at any intra-arc moon
  SOI): drawn at live positions; moon flybys render correctly iff the scheduler matched the
  config.
- **Arrival capture orbit** (StockConic): the v1 interplanetary endpoint (orbital arrival).
- **Arrival loiter / Landing** (StockConic / TracedPath): full chain for faithful missions;
  for re-aimed interplanetary, deferred behind the §4 prerequisite.

Seam transitions over time:
- **Rigid seams** (within a sub-chain; ascent↔orbit; orbit↔landing): visually continuous,
  no jump/blink — guaranteed by the make-before-break swap.
- **Flexible SOI seams**: a tolerated discontinuity is expected and **not hidden**
  (off-camera, high warp); the chain may carry a UT gap there (handled as `InInteriorGap`).

## 10. Edge cases

Each: scenario → expected behavior → [v1 / deferred].

1. **Moon flyby with mismatched config.** Recorded arc passes a moon the scheduler left out
   of place → vessel "teleports" past empty space. *Renderer draws as-is at live positions,
   logs the recorded-vs-live moon phase offset, does not correct.* For a re-aimed
   interplanetary mission a flyby renders only if its arc is an on-rails `OrbitSegment`; a
   recorded approach `Points`/`TrackSection` flyby is behind the §4 prerequisite. [v1:
   scheduler constraint exists for Mun/Minmus; extend.]
2. **SOI-boundary direction seam.** Recorded exit direction ≠ heliocentric appearance.
   *Accepted, not corrected.* [Deferred.]
3. **Variable chain structure.** Direct departure, direct landing, same-body. *Assembled
   from classification; missing pieces are absent segments.* [v1.]
4. **Loiter trim continuity.** ~100 → 1–5 orbits. *Whole-period cut is position/velocity
   continuous; no seam at the trim.* [v1.]
5. **Loiter trim degenerate boundary** (cut to sub-one-rev / a member window collapsing to
   zero, e.g. the loiter was the member's whole contribution). *Keep a sub-N-rev loiter
   whole (no cut); a zero-length member renders nothing but a watched ghost holds on its
   nearest live segment — never blinks off mid-watch (reuse the loiter-doc keep-watched
   guard).* [v1.]
6. **Pre-launch.** *Nothing on the map; Warp-to-launch + Time-to-launch UI.* [v1.]
7. **Warp steps across a flexible-seam gap.** One high-warp frame advances `liveUT` from
   before a gap to inside it (or skips a whole segment). *Director's `InInteriorGap` ⇒ hold
   last intent, suppress icon-jump; only `OutsideWindow` retires the ghost.* [v1.]
8. **Self-overlap (period < span).** Several staggered *instances* of one recording live at
   once, at different chain positions, possibly needing different treatments simultaneously.
   *Each instance is a distinct ghost keyed by `InstanceKey`; proto-vessel pids and polyline
   legs are per-instance, NOT per-recording. SC/TS render a single span instance (no overlap
   machinery there).* [v1 within existing caps.]
9. **Debris ride-along (same-SOI / non-transfer).** *Its own ghost following the same
   contract; not a special seam.* [v1.]
10. **Debris decoupling DURING the re-synthesized transfer.** A parent-anchored child off the
    parent mid-transfer has no transfer of its own and is anchored to the parent's frame —
    which under re-aim is a *synthesized* Sun-relative conic differing from the recording.
    *Either the child re-aims congruently with its parent (rides the same rotated transfer)
    or it is declined to faithful and rendered at its recorded position; the assembler must
    not silently anchor it to a frame that was re-synthesized.* (Partial answer: per
    `ReaimPlaybackResolver.HasHeliocentricLegInWindow`, a member with no heliocentric leg
    already passes through faithful; the open part is parent/child *frame coherence*.) [v1 if
    transfer-phase debris can occur; else explicitly deferred with the faithful fallback
    stated. Step-4 probe: whether v1 re-aim recordings retain transfer-phase debris.]
11. **Flight map ↔ Tracking Station.** *Identical contract/treatments; only the scene adapter
    differs. TS = extend the Phase-F remap into the chain model.* [v1: parity required.]
12. **Non-looped exact recording.** *Degenerate single-sub-chain chain, span clock = identity;
    must not regress.* [v1.]
13. **KSP toggles `line.active` mid-frame** (SOI transition, floating-origin shift). *Managed
    StockConicTreatment re-asserts intent each frame; reconciler flags residual divergence.* [v1.]
14. **Below-surface orbit segment** (periapsis < body radius). *Fails the conic test ⇒
    TracedPath; descent samples merge into the polyline.* [v1.]
15. **Floating-origin / Krakensbane shift during a treatment swap or mid-polyline-leg.** *Both
    surfaces read one origin frame per Director cycle (§6.6); reconciler gains a polyline
    origin-shift-jump anomaly.* [v1.]
16. **Camera focused/watching a ghost across a make-before-break swap.** Tearing down the
    focused proto-vessel would drop camera focus. *Scene adapter re-homes focus onto the
    incoming surface / keeps a persistent focus anchor.* [v1 if swaps can occur while focused.]
17. **Re-aim solve declines/degenerates for the live window.** No clean generated transfer.
    *Assembler marks `IsFaithfulFallback`, draws the recorded transfer at its recorded
    (off-window) position; never a blank transfer segment (§6.9).* [v1.]
18. **Re-aim window advances mid-cycle** (cache keyed by member+window, independent of the
    span). *Chain rebuild trigger includes the re-aim-window index, not just `BuildSignature`
    (§6.2 cache key).* [v1.]
19. **Scene switch (flight ↔ TS) mid-segment / mid-seam.** The new scene cold-starts and
    builds only the incoming treatment; proto-vessel re-seed latency may blank the first
    frame. *TS adapter handles cold-start-mid-segment without a one-frame blank (model the
    re-seed settle in the Director, §6.4 / open question §15.1).* [v1: verify.]
20. **Stock patched-conic re-solve differs per scene** (the transfer's downstream SOI patch
    recomputed against live bodies, possibly differently in TS vs flight-map; encounter
    promotion is settings-gated). *StockConicTreatment drives the transfer from the
    assembler's frozen elements and should not let stock insert an unplanned encounter patch;
    if it does, log it as a parity divergence.* [Step-4 probe: how much of the patch chain
    stock re-solves per scene; resolve or explicitly bound the parity risk.]
21. **Pause / time-warp rate change.** *Sampler is pure in `liveUT`; no special transient.* [v1.]

## 11. What doesn't change

- The recording format, sidecars, and segment classification.
- The re-aim solver math, the periodicity scheduler, the span clock, the loiter compressor —
  the renderer consumes their output; it never re-plans. (The solver gains a *module
  boundary*, §6.9, but its behavior is unchanged.)
- Celestial body positions — the renderer never moves a body; moon-config correctness is the
  scheduler's.
- Ghost interaction (focus / target / Fly / re-fly) and the flight-scene 3D ghost meshes.
- The Missions UI, the loop model, and the "one loop per tree" rule.

## 12. Backward compatibility

No save/recording migration: the chain is derived at runtime from existing recorded data +
existing scheduler output. Existing looped Missions render through the new module on load
with no persisted-state change. The module is a rendering replacement, not a data change.

## 13. Diagnostic logging

The map/TS render path is otherwise un-debuggable (the reason the tracer exists), so logging
is pervasive and reuses `MapRenderTrace` (subsystem tag `MapRender`). Every branch logs:

- **Chain assembly** (`Verbose`, per window per instance): segment count, kinds, treatments,
  frames, UT ranges, generated/flexible-seam markers, `IsFaithfulFallback`.
- **Segment locate + coverage** (`VerboseRateLimited`, per instance): located segment +
  treatment + coverage tri-state (InSegment / InInteriorGap / OutsideWindow).
- **Intent / treatment decision** (`VerboseRateLimited`, on change): active treatment + why,
  confirmation the other stands down, `DriveUT`, swap state.
- **Seam transition** (`Info`, on change): rigid vs flexible-SOI, outgoing→incoming, UT, and
  the tolerated discontinuity magnitude at a flexible seam.
- **Moon-config diagnostic** (`Verbose`, arrival sub-chain): recorded-vs-live phase offset of
  any moon the arc passes close to — surfaces scheduler misses (§10.1).
- **Faithful-fallback** (`Warn`): a window declined re-aim (§10.17), with the reason.
- **Reconciler anomalies** (`Warn`): decision-vs-truth divergence, icon-jump, line-blink,
  polyline-origin-shift, polyline-orbit overlap, gap-vs-retire misclassification.

## 14. Test plan

Pure / synthetic-recording testable (the heavy correctness lives here):

- **Chain assembly from classification** — synthetic recordings for each structure (faithful
  same-body full chain, re-aimed interplanetary conic subset, direct departure/landing,
  non-looped) → assert ordered segment list (kinds + treatments + frames + UT ranges).
  *Guards variable-structure assembly + the v1 scope boundary.*
- **Treatment assignment** — below-radius orbit → TracedPath; above-surface conic →
  StockConic; generated transfer → StockConic. *Guards conic/polyline misclassification + the
  below-surface merge.*
- **Intra-arc SOI frame split** — a recorded arc grazing a moon SOI → split into
  one-frame-each segments. *Guards a two-frame segment (the teleport-next-to-moon class).*
- **Segment locate + coverage tri-state** — UT sweep incl. a flexible-seam gap → exactly one
  active segment in-segment, `InInteriorGap` inside a gap, `OutsideWindow` past the end.
  *Guards double-draw, the warp-gap blink (§10.7), and premature retire.*
- **Span-clock + re-aim-window integration** — looped member at various live UTs (loiter cuts,
  future-dated launch, re-aim window advance) → correct assembled-chain UT, segment, and cache
  rebuild. *Guards loop-shift, decompression, and stale-window bugs.*
- **Overlap instance keying** — two instances of one recording at different chain positions →
  distinct `InstanceKey`, distinct surface keys, independent treatments. *Guards the
  shared-RecordingId collision (§10.8).*
- **Faithful-fallback** — a declined solve signal → `IsFaithfulFallback` chain with the
  recorded transfer, never a blank segment. *Guards §10.17.*
- **Intent invariants** — drive a chain through all transitions → never two treatments active,
  never icon-off-line. *Guards the structural invariants directly.*
- **Reconciler** — inject a forced `line.active` toggle / origin shift → flagged. *Guards the
  permanent assertion.*
- **Log assertion tests** — assert assembly / locate / intent / seam / moon-config / fallback
  lines appear with the right fields. *Guards diagnostic coverage surviving refactor.*
- **Non-looped regression** — exact single recording renders as the degenerate chain.

In-game (live KSP): map↔TS parity, a faithful Mun mission watched through a full cycle
(incl. landing), a re-aimed Duna mission to orbital arrival, a moon-flyby arrival, a high-warp
SOI seam, self-overlapping instances, camera-focus through a swap.

## 15. Open questions

1. **Proto-vessel re-seed latency vs make-before-break atomicity.** Does KSP let a
   proto-vessel's orbit be re-seeded and rendered within one frame, or is a bounded one-frame
   settle required? Model the answer explicitly in the Director (§6.4) — Step-4 probe.
2. **Stock patched-conic per-scene divergence** (§10.20) — how much of the transfer's
   downstream patch chain does stock re-solve in TS vs flight-map, and is encounter promotion
   settings-gated differently? Resolve or bound the parity risk in Step 4.
3. **Transfer-phase debris** (§10.10) — do v1 re-aim recordings retain debris that decouples
   during the heliocentric leg? If yes, parent/child re-aim coherence is v1; if no, state the
   faithful fallback and defer.
4. **Solver-module extraction sequencing** — is `ITransferSolver` extracted as part of this
   rewrite or as a parallel workstream? The render module only needs the *boundary* defined;
   the extraction can land independently.
5. **The re-aimed surface-track prerequisite** (§4) — confirm it is a separate adapter task
   (re-timing recorded ascent/descent `Points`/`TrackSections` onto synthesized spans) and not
   smuggled into this rewrite.

## 16. References

- `docs/dev/done/plans/reaim-interplanetary-transfers.md` — deterministic transfer (Lambert),
  v1 = orbital arrival, S4 descent deferred.
- `docs/dev/done/plans/reaim-loiter-compression.md` — whole-period loiter trimming.
- `docs/dev/design-mission-periodicity.md` — launch-window scheduling; "replay as-is" superseded.
- `docs/dev/design-mission-abstractions.md` — Mission / selection / span-clock / overlap; the
  Phase-F TS remap.
- `docs/parsek-ghost-trajectory-rendering-design.md` — existing rendering surfaces.
- `docs/dev/design-map-ts-render-tracer.md` — the observability layer reused as the reconciler.
- Key source seams: `GhostPlaybackLogic.cs` (span clock, LoopUnit/LoopUnitSet),
  `Reaim/ReaimedTrajectory.cs` + `Reaim/ReaimPlaybackResolver.cs`, `IPlaybackTrajectory.cs`,
  `GhostMapPresence.cs`, `ParsekPlaybackPolicy.cs` (`CheckPendingMapVessels`),
  `ParsekTrackingStation.cs` (`UpdateTrackingStationGhostLifecycle`),
  `Display/GhostTrajectoryPolylineRenderer.cs`, `Patches/GhostOrbitLinePatch.cs`,
  `MapRenderProbe.cs` / `MapRenderTrace.cs`.
