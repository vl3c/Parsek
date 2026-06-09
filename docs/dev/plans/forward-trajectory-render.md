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

Inputs the helper already has cheap access to:
- `Recording.OrbitSegments` (sorted; with `bodyName`, `startUT/endUT`, `ecc`,
  `sma`, `isPredicted`).
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

- `double ComputeForwardStopUT(orbitSegments, legs/cover, currentUT, µByBody)`
  → walk the interleaved timeline forward from the element containing
  `currentUT`; return the UT of the first stop condition (full-loop closed arc
  start, or body-change boundary), else end-of-data. SOI = `bodyName` of the
  current element vs the next; the first differing-body element ends the window.

  *(If the implementation chooses to reuse `GhostRenderChain` directly — see
  Step 4 Option 2 — this becomes a tiny `chain.Segments` sub-range scan: from
  `LocateSegmentIndex(currentUT)`, advance while next seam is `Rigid` and the
  next `StockConic` is not a full-loop closed orbit.)*

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

- **Cache.** Key the sampled forward-arc `VectorLine`s per `recordingId` by
  `(currentElementIndex, bodyName)` — segment geometry is static, so re-sample
  only when the icon crosses into a new current element. Re-use the renderer's
  existing per-leg `VectorLine` cache lifecycle.

### Step 4 — Two implementation routes (recommendation)

- **Option 1 (RECOMMENDED — ships now, low-risk).** Implement at the two
  production draw surfaces (B′ + C above) with the standalone pure forward-window
  helper (Step 1). It draws the forward range directly and does not disturb the
  Director's single-intent decision (which keeps governing the current element's
  icon + arc-clip routing). This is the proposed plan.

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
- **Gaps between same-body segments** (recorder mode transitions): already
  coalesced by `TrajectoryMath.CoalesceSameOrbitFragments`
  (`ChainAssembler.cs:76`); reuse it so a fragmented parking coast does not split
  the forward chain.

## Testing

- **xUnit (pure):** `IsFullLoopClosedOrbit` (period boundary, `ecc ≥ 1`,
  degenerate `sma`); `ComputeForwardStopUT` (SOI-change stop, full-loop stop,
  multi transfer-arc chain that walks several segments, icon-on-closed-orbit →
  empty range, predicted-included). Inject synthetic `µByBody`.
- **xUnit (renderer logic):** the new overlap leg-gate (replacing
  `ShouldDrawLegAtHeadUT`) — boundary inclusivity, current+future disjointness.
- **In-game (`InGameTests/RuntimeTests.cs`):** visual confirmation in FLIGHT map
  and TRACKSTATION — an ascent→transfer ghost shows a continuous line from the
  icon forward, terminating at the parking orbit and at the SOI edge.

## Files expected to change

- `TrajectoryMath.cs` (or new `ForwardRenderWindow.cs`) — pure Step 1 helpers.
- New shared `OrbitArcSampler` (Step 2) — extracted from `GhostOrbitArcPatch`.
- `Display/GhostTrajectoryPolylineRenderer.cs` — forward leg-gate (B′) + forward
  arc `VectorLine`s (C) + cache.
- `Source/Parsek.Tests/` — pure unit tests.
- `InGameTests/RuntimeTests.cs` — visual test.
- `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md` — entries.

## Risk / performance

Bounded: N ghosts × (typically 0–3 forward elements) × cached sampling; the
heavy sampling path runs only when the icon crosses into a new current element.
The forward legs reuse the existing per-leg draw budget (200-point cap, §1.3).
No new per-frame allocation beyond the cached forward `VectorLine`s.
