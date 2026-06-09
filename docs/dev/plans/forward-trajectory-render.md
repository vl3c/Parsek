# Plan: Forward trajectory rendering (flight-map + tracking-station)

Status: DESIGN / PLAN. No production code proposed-as-merged here; this is the
design to review before implementation on branch
`claude/flight-map-trajectory-render-io6be0`.

## Problem statement

In the flight map and the tracking station, a ghost's trajectory line is drawn
**one element at a time** — only the orbit segment / non-orbital leg that the
**icon currently sits on**. Past elements have already disappeared (the window
advances), and *future* elements are never drawn. The result is that a ghost on
an ascent or a transfer shows only the short arc under the icon, with no
indication of where it is heading.

The request: render the **future** portion of the trajectory ahead of the icon
too, as **one continuous, seamlessly chained line** (orbit arcs and non-orbital
polylines already meet at shared boundaries — today they are merely drawn
separately, one at a time), with two hard stop conditions so we never clutter
the map:

1. **Past stays gone.** Only render from the icon's current element onward;
   completed past elements keep disappearing (no change there).
2. **Stop before the first full-loop closed orbit.** When the forward chain
   reaches a segment that covers a complete revolution (`ecc < 1` **and**
   `endUT − startUT ≥ period`), do **not** draw it and stop — we never render a
   full repeating ellipse.
3. **Stop at the first SOI change.** Render only what is in the **current
   reference body / SOI**; the moment the next element is a different
   `bodyName`, stop (exclude the next-SOI element).

### Confirmed decisions (from the maintainer)

- **"Closed orbit" = a full-loop segment** (span ≥ orbital period). Eccentric
  *transfer* arcs (`ecc < 1` but only a partial sweep) are still drawn; only a
  genuine full-revolution parking orbit terminates the chain.
- **Icon already on a full-loop closed orbit → keep current behaviour** (stock
  full ellipse / clipped arc; no forward extension — there is nothing ahead
  before the closed orbit itself).
- **Predicted/extrapolated future elements ARE drawn** in the forward chain
  (`isPredicted == true` segments and ballistic-tail legs included). Stop
  conditions still apply.
- **Same visual style** as the current element — one uniform, solid orbit-line
  colour (`MapView.OrbitLinesMaterial`), so current + future read as a single
  continuous line, not a dimmed "future" tint.

## How rendering works today (current mechanism)

Both scenes share the same two production surfaces (the
`GhostTrajectoryPolylineRenderer.Driver` scene gate is `FLIGHT || TRACKSTATION`,
`GhostTrajectoryPolylineRenderer.cs:1856`):

### A. Orbit arc — stock `OrbitRenderer` + `GhostOrbitArcPatch`

Each ghost is a lightweight proto-vessel with **one** stock
`OrbitDriver`/`OrbitRenderer`. Parsek seeds that single driver with the **one**
`OrbitSegment` whose `[startUT, endUT]` brackets the live playback UT
(`TrajectoryMath.FindOrbitSegment` / `TryGetOrbitWindowForMapDisplay`;
`GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel`). `GhostOrbitArcPatch.
UpdateSpline` (`GhostOrbitLinePatch.cs:1004-1123`) then **clips** the stock
ellipse/hyperbola to that segment's eccentric-anomaly arc (`fromE→toE`, 180
samples via `getPositionFromEccAnomalyWithSemiMinorAxis`, `drawStart=0 /
drawEnd=179`, open arc). One arc, with the moving icon, for the current segment
only. A full-period segment (`endUT-startUT ≥ orbit.period`) falls through to
stock and draws the complete ellipse (`GhostOrbitLinePatch.cs:1038`).

### B. Non-orbital legs — `GhostTrajectoryPolylineRenderer.Driver`

Ascent / burn / descent phases (no usable Keplerian arc) are drawn by the
autonomous DDOL `Driver.LateUpdate`, which walks `RecordingStore.
CommittedRecordings`, and for each ghost draws **only the leg under the icon**
via the head-only gate `ShouldDrawLegAtHeadUT(legStart, legEnd, headUT) =>
headUT ∈ [legStart, legEnd]` (`GhostTrajectoryPolylineRenderer.cs:513`, called
at `:2022` and `:2083`). Each leg is a Vectrosity `VectorLine` in scaled space,
drawn with the **solid** `MapView.OrbitLinesMaterial` (`:1607`) — i.e. legs
already look like stock orbit lines, which is exactly the uniform style we want
for future arcs too.

### Data model

- `OrbitSegment` (`OrbitSegment.cs`): `startUT/endUT` + 6 Kepler elements +
  `epoch` + a single `bodyName` (reference body) + `isPredicted`.
  `Recording.OrbitSegments` is a time-sorted flat list.
- **SOI change = consecutive segments with different `bodyName`.** "Closed" is
  implicit (`ecc < 1` elliptical; `span ≥ period` is a full revolution). There
  is no explicit `isClosed` flag.
- **The interleaved timeline already exists** in the `MapRender/` Director
  rewrite: `ChainAssembler.Build` (`MapRender/ChainAssembler.cs`) produces a
  `GhostRenderChain` whose `Segments` is the **ordered, seam-classified** list
  of `RenderSegment`s — `StockConic` (above-surface orbit arc, carrying the
  `OrbitSegment`) and `TracedPath` (non-orbital leg) interleaved by `StartUT`,
  with each adjacent-pair seam classified `Rigid` (same body) or `FlexibleSoi`
  (body change = SOI crossing; `ChainAssembler.cs:201-202`). `GhostRenderChain.
  LocateSegmentIndex` already does the O(log n) "which element is the icon on"
  lookup. **This is precisely the unified forward-walk substrate the feature
  needs** — the forward window is a clean sub-range of `chain.Segments`.

### The Director's role (important for where the forward pass lives)

The `MapRender/` Director pipeline runs **unconditionally every frame** in both
FLIGHT (`ParsekFlight.cs:19386`) and TRACKSTATION (`ParsekTrackingStation.cs:269`)
— `ShadowRenderDriver.Enabled => true` (8e S4 dropped the director-drive gate;
the off-by-default `mapRenderTracing` / `MapRenderTrace.IsEnabled` setting gates
only the trace *emit* + the `MapRenderProbe` reconcile, NOT the loop). Each frame
`ShadowRenderDriver.RunFrame` builds a `GhostRenderChain` per ghost (cached in
`chainByPid`), samples it, and calls `GhostRenderDirector.Decide`.

> Do not be misled by `RunFrame`'s own XML doc-comment
> (`ShadowRenderDriver.cs:244-245`), which still says "Caller MUST gate on
> `MapRenderTrace.IsEnabled`". That comment is STALE (pre-8e-S4); both live call
> sites gate only on `ShadowRenderDriver.Enabled` (verified above), so the chain
> IS available in normal play. Reading the method comment instead of the call
> sites would wrongly push you toward over-building Option 1's standalone helper.
> Worth a separate one-line chore to fix the comment.

It is named a "shadow" because it **does not itself paint pixels** — the literal
drawing is still the stock `OrbitRenderer` (surface A, current arc + icon) and
the autonomous `GhostTrajectoryPolylineRenderer.Driver` (surface B, current leg).
But its decisions are **not** inert: `RunFrame` populates `seedByPid` /
`tracedPathByPid`, which the production patches read via
`ShadowRenderDriver.IsDirectorDriveActive` / `IsDirectorTracedPathActive` to
switch the arc-clip to live bounds + bake the epoch (`GhostOrbitLinePatch.cs:1053`)
and to suppress the stock icon/line so the polyline owns a leg
(`GhostOrbitLinePatch.cs:130/555`, `GhostTrajectoryPolylineRenderer.cs:2007`). So
the Director's **routing decisions drive production every frame**; it just does
not own the draw call.

The single structural constraint for this feature: `GhostRenderDirector.Decide`
emits **exactly one** `Treatment` per ghost per frame
(`GhostRenderDirector.cs:24-46`) — it is single-element by construction. Neither
draw surface draws a forward *range* today. So **both** implementation routes
below need new draw code regardless; the question is only whether the forward
window is computed standalone (Option 1) or surfaced from the already-live chain
(Option 2).

## Design

The unifying concept is a per-ghost **forward render window**
`[currentElementStartUT, forwardStopUT]`, computed once per frame and honoured by
both production surfaces so the line reads as one continuous chain.

```
forwardStopUT = earliest of:
  • StartUT of the first full-loop closed OrbitSegment after the icon
  • the first SOI / body-change boundary after the icon (FlexibleSoi seam)
  • end of the recording's data
```

The **current** element under the icon still renders exactly as today (full
current orbit segment via stock + its moving icon; full current leg). The new
work is purely the forward extension from the next element up to
`forwardStopUT`. If the icon is already on a full-loop closed orbit,
`forwardStopUT == currentElementStartUT` → empty forward range → current
behaviour, unchanged.

### Step 1 — Pure forward-window computation (always available, unit-tested)

Add a standalone, Unity-free helper (in `TrajectoryMath.cs`, or a small new
`ForwardRenderWindow.cs` — TBD during build). The `GhostRenderChain` IS built
every frame in production (it is not trace-gated), but it is private to
`ShadowRenderDriver` (`chainByPid`); a standalone helper keeps the forward
window decoupled from that pipeline and directly xUnit-testable. (Option 2,
below, instead surfaces the window from the live chain — both are viable.)

**(CRITICAL) Source the forward geometry from the re-aimed EFFECTIVE segments,
NOT raw `Recording.OrbitSegments`.** A re-aim loop ghost's recorded heliocentric
leg is aimed at the target's HISTORICAL position; the Director replaces it per
synodic window with one aimed at the target's CURRENT position
(`GhostMapPresence.ResolveEffectiveMapOrbitSegments`, called from
`ShadowRenderDriver.cs:407`; `ChainAssembler` then builds the chain from that
override). A forward render that walked the raw recorded segments would draw
wrong-aimed forward arcs for re-aimed ghosts: the exact icon-off-orbit defect the
re-aim machinery exists to fix. The SOI stop already excludes the common case
(the re-aimed leg lives in the Sun's SOI, so a ghost on its Kerbin escape stops
at the Kerbin -> Sun boundary, and a ghost on the heliocentric leg has it as the
CURRENT element drawn by stock), but a multi-segment same-SOI transfer (a
mid-course burn splitting the coast) still puts a re-aimable forward arc inside
the window, so this is a correctness requirement, not an optimisation. The helper
stays pure by taking the resolved EFFECTIVE list as an INPUT: the live caller
resolves it via `ResolveEffectiveMapOrbitSegments` (raw == effective, same
reference, for faithful members), and xUnit passes a synthetic list directly.

Inputs the helper takes (all caller-resolved, so the helper stays pure):
- The **effective** (re-aim-resolved) `OrbitSegment` list (sorted; with
  `bodyName`, `startUT/endUT`, `ecc`, `sma`, `isPredicted`). Live caller gets it
  from `ResolveEffectiveMapOrbitSegments`; tests pass a synthetic list.
- The orbital **cover intervals** + below-surface exclusion already computed by
  `GhostTrajectoryPolylineRenderer.ComputeOrbitalCoverIntervals` /
  `IsOrbitSegmentBelowSurface` (`:1335` / `:1312`) — so a leg vs arc at a UT is
  decidable with existing predicates.
- Per-body `gravParameter` (the only KSP-coupled input) — injected as a
  `Func<string,double>` / `BodySurfaceProvider`-style delegate so the helper
  stays pure and xUnit-testable (null/synthetic in tests, FlightGlobals-backed
  live), matching the polyline renderer's existing injection pattern.

New pure functions:

- `bool IsFullLoopClosedOrbit(OrbitSegment seg, double gravParameter)`
  → `ecc < 1` **and** `(endUT − startUT) ≥ period`, where
  `period = 2π·sqrt(a³/µ)`. (Hyperbolic / `ecc ≥ 1` is never a full loop.)

- `double ComputeForwardStopUT(effectiveSegments, legs/cover, currentUT, muByBody)`:
  walk the interleaved timeline forward from the element containing `currentUT`;
  return the UT of the first stop condition, else end-of-data. The CURRENT
  element is tested FIRST so the icon-on-closed-orbit case is explicit: if the
  current element is itself a full-loop closed orbit, return its `startUT` (empty
  forward range, current behaviour unchanged); otherwise advance and stop at the
  start of the first full-loop closed arc after it, or at the first body-change
  boundary (`bodyName` of an element differs from the current element's), else
  end-of-data.

  *(If the implementation chooses to reuse `GhostRenderChain` directly — see
  Step 4 Option 2 — this becomes a tiny `chain.Segments` sub-range scan: from
  `LocateSegmentIndex(ut)`, advance while next seam is `Rigid` and the next
  `StockConic` is not a full-loop closed orbit. Note `LocateSegmentIndex` expects
  the ASSEMBLED-CHAIN-clock UT (`GhostRenderChain.cs:50`), so the live UT must
  first pass through `ChainSampler.Sample`'s loop/span-clock remap
  (`ShadowRenderDriver.cs:284`); a raw live UT mis-locates for looped / span-
  clocked ghosts. The chain is already assembled from the effective segments, so
  Option 2 gets re-aim correctness for free.)*

### Step 2 — Shared Kepler arc sampler

Extract the arc-sampling math from `GhostOrbitArcPatch.UpdateSpline`
(`GhostOrbitLinePatch.cs:1057-1091`) into a pure helper, e.g.
`OrbitArcSampler.SampleSegmentArc(OrbitSegment seg, CelestialBody body,
Vector3d[] outPoints)`: build a throwaway `Orbit`, `SetOrbit` from the segment
elements + body, `EccentricAnomalyAtUT(startUT/endUT)`, apply the elliptical
periapsis-wraparound correction (`ArcAnomalyMath.NeedsPeriapsisWraparound` /
`ApplyPeriapsisWraparound`), then sample N points via
`getPositionFromEccAnomalyWithSemiMinorAxis`. Both the existing patch (path A)
and the new forward-arc renderer call it — no behavioural change to the current
path, just deduplication. (If extraction proves invasive, the forward renderer
carries its own copy; decide during build.)

**Draw-space note (forward arcs use the ARC pipeline, not the leg pipeline).**
The sampler emits BODY-LOCAL points; the forward-arc `VectorLine` (Step 3 C) must
convert them exactly as the stock patch does:
`ScaledSpace.LocalToScaledSpace(orbitPoints, line.points3)`
(`GhostOrbitLinePatch.cs:1091`). It must NOT route through the leg pipeline's
`CelestialBody.GetWorldSurfacePosition(lat,lon,alt) - body.position`
(`GhostTrajectoryPolylineRenderer.cs:560`), which is for surface-relative leg
points. Both land in absolute scaled space, so the arc<->leg seam stays
continuous, but the conversion source differs and the forward arc must use the
Kepler-local one.

### Step 3 — Forward static render (legs + arcs), drawn seamlessly chained

The forward portion is **static lines, no icons** (only the current element
carries the moving icon), which makes the Vectrosity `VectorLine` +
`OrbitLinesMaterial` approach a perfect fit. **Fold the whole forward static
trajectory into `GhostTrajectoryPolylineRenderer`** — it already owns the DDOL
`Driver`, the `FLIGHT||TRACKSTATION` scene gate, the `CommittedRecordings` walk,
the `OrbitLinesMaterial` draw, and the per-`VectorLine` lifecycle. Two changes:

- **(B′) Future legs.** Replace the head-only `ShouldDrawLegAtHeadUT` gate
  (`:513`, used at `:2022`/`:2083`) with an overlap gate against the forward
  window: draw any leg overlapping `[currentElementStart, forwardStopUT]`. The
  current leg still draws in full (including the short stretch behind the icon,
  matching today); completed past legs still drop out.

- **(C) Future arcs (new).** For each forward **orbit** segment in the window
  (i.e. `StockConic` elements after the current one, up to `forwardStopUT`),
  sample it via Step 2 into a Vectrosity `VectorLine` drawn with
  `OrbitLinesMaterial` — identical look to legs and to the stock orbit line.
  One `VectorLine` per future segment (same "a single shared line zeroes every
  vertex outside `drawStart/drawEnd`" constraint the renderer documents at
  `:38-41`).

- **No overlap with surface A.** The forward range starts at the element
  **after** the current one, so the stock `OrbitRenderer` (current arc + icon)
  and the forward arcs never double-draw. The forward legs and the current leg
  are disjoint UT ranges of the same per-recording leg set.

- **(CRITICAL) Forward draws must NOT trip the per-recording "polyline owns this
  phase" ownership signal.** Today every actual leg draw adds the recording to
  `drewNonOrbitalLegRecordings` (the `if (anyDrawn)` publish at `:2161`), which
  `IsRenderingNonOrbitalLeg` / `GhostMapPresence.IsPolylineOwningGhostPhase` read
  and `GhostOrbitArcPatch` consumes at `GhostOrbitLinePatch.cs:600` to **hide the
  stock orbit line AND the proto icon** for that ghost (the marker paths
  `ShouldDrawNonProtoMarkerForGhost` / `TryAnchorMarkerToPolyline` cascade off the
  same signal). That set is **per-`recordingId`, not per-element**: it cannot tell
  "the polyline is drawing the leg the icon sits on" from "the polyline drew a
  FUTURE leg/arc". So the moment a forward leg (B') or forward arc (C) draws while
  the icon is on a `StockConic` arc, the publish flips `IsPolylineOwningGhostPhase`
  true and the **current** stock arc + its moving icon are suppressed, directly
  breaking the "current element renders exactly as today" guarantee (and
  reintroducing the icon-off-line / blank-icon class of bug the MapRender rewrite
  has been fighting). The forward draws MUST therefore either (a) draw their
  `VectorLine`s through a path that does NOT touch `drewNonOrbitalLegRecordings`,
  or (b) narrow the ownership signal so it fires only when the polyline draws the
  element the icon is actually on (the current leg), with forward elements
  excluded. The current-leg draw keeps publishing exactly as today. **There are
  three consumers of the signal, and the fix must keep ALL of them seeing "owns"
  IFF the polyline is drawing the CURRENT element**, not a forward one: the orbit
  LINE Postfix (`GhostOrbitLinePatch.cs:600`), the proto-ICON Prefix
  (`GhostOrbitLinePatch.cs:130`), and the non-proto MARKER decision
  (`GhostMapPresence.ShouldDrawNonProtoMarkerForGhost`, whose `IsPolylineOwning`
  disjunct would otherwise paint a second marker over a still-stock-drawn icon).
  Option (b) covers all three by construction; option (a) must be verified against
  the marker path too. This is a hard prerequisite, not a polish item: resolve it
  inside Step 3 before any forward leg/arc can ship, or the very first forward
  extension on an orbit-bearing ghost regresses the current arc. Likely touches
  the publish logic in `GhostTrajectoryPolylineRenderer.cs` (and possibly the
  ownership reader in `GhostMapPresence.cs`).

- **Cache.** Key the sampled forward-arc `VectorLine`s per `recordingId` by
  `(currentElementIndex, bodyName, reaimWindowSignature)`. The recorded segment
  geometry is static, but the re-aimed effective geometry changes per synodic
  window, so the key MUST include the same re-aim signature the chain cache uses
  (`windowIndex`, the last argument of `BuildChainSignature`,
  `ShadowRenderDriver.cs:414`); a `(currentElementIndex, bodyName)`-only key would
  serve a stale wrong-aimed arc after a window rollover. Re-sample only when one
  of those changes. Re-use the renderer's existing per-leg `VectorLine` cache
  lifecycle. (Option 2 inherits this for free: it reads the already-signature-
  cached `chainByPid`.)

### Step 4 — Two implementation routes (recommendation)

- **Option 1 (RECOMMENDED — ships now, low-risk).** Implement at the two
  production draw surfaces (B′ + C above) with the standalone pure forward-window
  helper (Step 1). It draws the forward range directly and does not disturb the
  Director's single-intent decision (which keeps governing the current element's
  icon + arc-clip routing). This is the proposed plan. **Two hard caveats**
  (both detailed above, both required for correctness, not polish): (1) it must
  feed the forward-window helper the re-aimed EFFECTIVE segments
  (`ResolveEffectiveMapOrbitSegments`), not raw `Recording.OrbitSegments`, or it
  draws wrong-aimed forward arcs for re-aim loop ghosts (Step 1); (2) because it
  draws through `GhostTrajectoryPolylineRenderer`, its forward draws must not flip
  the per-recording `IsPolylineOwningGhostPhase`, or it suppresses the very
  current arc the Director is still routing (Step 3). Note both are things the
  live chain already gets right, which is why Option 2 is the cleaner long-term
  home; Option 1 takes them on as explicit obligations in exchange for shipping
  without touching the Director's draw surfaces.

- **Option 2 (future convergence).** Extend `GhostRenderDirector.Decide` to emit
  a forward **range** of intents instead of one, and have the treatments
  (`StockConicTreatment`/`TracedPathTreatment`) + reconciler own the forward
  draw. This is architecturally the "right" home (the chain already is the live
  interleaved timeline, built every frame; the forward window is literally a
  `chain.Segments` sub-range). The blocker is not the Director's liveness (it
  runs unconditionally and already drives routing) but that **its draw surfaces
  do not yet paint a range** — the literal drawing is still the stock single-arc
  + the head-gated polyline, and a forward range entangles the rewrite's open
  questions (make-before-break, gap-hold, per-instance overlap). Recommend
  converging here once the treatments own the live draw call; until then Option 1
  is the pragmatic path, and its pure helpers (Step 1/2) transfer directly into
  Option 2.

## Edge cases

- **Icon on a full-loop closed orbit** → empty forward range → unchanged
  (confirmed).
- **Forward leg/arc drawn while the icon is on an orbit arc** -> must NOT suppress
  the current stock arc. The per-recording polyline-owns signal
  (`drewNonOrbitalLegRecordings` -> `IsPolylineOwningGhostPhase` ->
  `GhostOrbitLinePatch.cs:600`) is element-blind, so this is a hard prerequisite,
  not an edge case to tolerate: see the **(CRITICAL)** bullet in Step 3.
- **Hyperbolic future segment inside the SOI** (escape arc before the SOI
  marker) → drawn as an open arc, then the chain stops at the following body
  change (`ecc ≥ 1` is never a "full loop", so it does not itself stop the
  chain).
- **Predicted future elements** → included (confirmed); `isPredicted` does not
  gate them out, and the stop conditions are the only terminators.
- **Below-surface orbit ranges** are already excluded from the orbital cover
  (FIX #27, `IsOrbitSegmentBelowSurface`), so a descent that dips a conic below
  the surface falls to a TracedPath leg and renders via B′, not C — keeping the
  forward chain continuous through descent.
- **Loop-shifted ghosts** (`ghostOrbitEpochShift`): the forward arcs are sampled
  from each segment's own recorded epoch/elements (frame-independent shape), so
  the shift affects only the icon's drive UT, not the static forward geometry.
  The forward-window UTs are resolved in the same clock the head UT is in.
- **Re-aimed loop ghosts** (interplanetary transfer with a synodic-window
  re-aim): forward geometry comes from the EFFECTIVE (re-aimed) segments, not the
  raw recorded ones, and the forward-arc cache keys on the re-aim window
  signature so it re-samples on a window rollover (Step 1 + the Cache bullet). The
  SOI stop keeps the re-aimed Sun leg out of the forward window in the common
  single-coast case; the residual is a multi-segment same-SOI transfer, which the
  effective-segment sourcing handles.
- **Ghost hidden / held in an interior gap** -> draw NO forward range. The Director
  deliberately HIDES or HOLDS a ghost across re-aim trim gaps / interior
  FlexibleSoi gaps (`ShadowRenderDriver.ShouldSkipReaimSegment`,
  `ShadowRenderDriver.cs:364`; `Coverage.InSegment == false`). Option 1's
  standalone helper is blind to that decision and would otherwise compute a
  forward window from whatever segment brackets `currentUT` even while the live
  ghost is invisible. Gate the whole forward pass on the same visibility the
  Director resolved (e.g. skip when the ghost's current sample is not
  `InSegment` / is hidden), so the forward line never appears for a ghost whose
  icon is hidden. Option 2 inherits this from the Director's own intent.
- **Seam continuity** depends on consecutive elements meeting at shared
  boundaries (the current stock arc's endpoint == the first forward element's
  start) and on the polyline `VectorLine`s sharing scaled-map space with the stock
  orbit line. Both hold today (the existing current-leg <-> next-arc seam already
  renders continuously), but the forward arcs inherit that dependency: if a
  recording's adjacent OrbitSegments are NOT geometrically contiguous the forward
  chain shows a visible kink, same as the current single-element path would.
- **Gaps between same-body segments** (recorder mode transitions): already
  coalesced by `TrajectoryMath.CoalesceSameOrbitFragments`
  (`ChainAssembler.cs:76`); reuse it so a fragmented parking coast does not split
  the forward chain.

## Testing

- **xUnit (pure):** `IsFullLoopClosedOrbit` (period boundary, `ecc ≥ 1`,
  degenerate `sma`); `ComputeForwardStopUT` (SOI-change stop, full-loop stop,
  multi transfer-arc chain that walks several segments, icon-on-closed-orbit →
  empty range, predicted-included, **current-element-is-closed-orbit returns its
  own startUT**). Because the helper takes the effective segment list as input,
  feed it a synthetic "re-aimed" list (different elements from the "recorded"
  one) and assert the stop UT / window is computed off the effective list, not the
  recorded one. Inject synthetic `µByBody`.
- **xUnit (renderer logic):** the new overlap leg-gate (replacing
  `ShouldDrawLegAtHeadUT`) — boundary inclusivity, current+future disjointness.
- **In-game (`InGameTests/RuntimeTests.cs`):** visual confirmation in FLIGHT map
  and TRACKSTATION — an ascent→transfer ghost shows a continuous line from the
  icon forward, terminating at the parking orbit and at the SOI edge.

## Files expected to change

- `TrajectoryMath.cs` (or new `ForwardRenderWindow.cs`) — pure Step 1 helpers.
- New shared `OrbitArcSampler` (Step 2) — extracted from `GhostOrbitArcPatch`.
- `Display/GhostTrajectoryPolylineRenderer.cs` — forward leg-gate (B′) + forward
  arc `VectorLine`s (C) + window-signature cache + the ownership-signal fix so
  forward draws do not flip `IsPolylineOwningGhostPhase` (Step 3 CRITICAL).
- `GhostMapPresence.cs` — live caller resolves the effective segments via
  `ResolveEffectiveMapOrbitSegments` for the window helper (`[ERS-exempt]`
  already); possibly the ownership reader if fix option (b) is chosen.
- `Source/Parsek.Tests/` — pure unit tests.
- `InGameTests/RuntimeTests.cs` — visual test.
- `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md` — entries.

## Risk / performance

Bounded: N ghosts × (typically 0–3 forward elements) × cached sampling; the
heavy sampling path runs only when the icon crosses into a new current element.
The forward legs reuse the existing per-leg draw budget (200-point cap, §1.3).
No new per-frame allocation beyond the cached forward `VectorLine`s.
