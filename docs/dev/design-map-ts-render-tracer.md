# Design: Map / Tracking-Station Render Tracer (`MapRenderTrace`)

*Status: design note (revised after a clean-context Opus review, verdict NEEDS
REWORK on the first draft; this revision addresses every finding). Proposes a gated
observability system for how Parsek renders ghosts in MAP VIEW and the TRACKING
STATION, mirroring the flight-scene mesh tracer (`GhostRenderTrace`). Read-only
instrumentation: it never mutates renderer, orbit, line, icon, or marker state. Off by
default behind a new `mapRenderTracing` setting wired like `ghostRenderTracing`. It
consolidates the six-plus ad-hoc diagnostic tags scattered across the map render path,
folds in the prototype `GhostRenderStateProbe`, and (in an optional second cut) adds
end-of-frame reconciliation of "what Parsek decided" against "what KSP actually
rendered".*

*Base branch for this doc: `fix-ghost-map-icon-frozen-arc` (it carries
`GhostRenderStateProbe` and the inline icon-truth diagnostics this design absorbs).
Implementation should branch from `origin/main` so it can also absorb the
`add-map-ts-draw-logging` / `map-ts-draw-logging` worktrees (see Coordination).*

Related: `docs/dev/observability-audit-2026-04-26.md`,
`docs/dev/plan-observability-logging-visibility.md`,
`Source/Parsek/GhostRenderTrace.cs` (the model), `Source/Parsek/TraceSeparation.cs`
(pre-event window prior art).

> **STATUS (2026-06-06): IMPLEMENTED and instrumenting BOTH scenes.** `MapRenderTrace` /
> `MapRenderProbe` ship behind the `mapRenderTracing` setting (off by default) and cover the
> flight map view AND the Tracking Station: the structural / lifecycle / first-position emits,
> the Tier-B change-based truth, the Tier-C anomaly predicates (icon-jump, line-blink,
> icon-off-orbit), the decision-vs-truth + polyline-orbit-overlap reconciliation, and the IMGUI
> marker-surface coverage. The Tracking-Station render-tracer coverage was completed in #1064:
> GAP-1 (the TS overlap marker-decision line now carries the REAL `ride` field, was hardcoded
> `ride=not-attempted`), GAP-2 (a first-class `surface=Polyline` leg-draw trace covering both
> scenes), and C-1 (the finer TS skip reason on the per-ghost decision line), plus a 4096-entry
> cap on the marker-decision change-detection (decision-signature) dict. All per-frame /
> per-cycle trace and log lines use WARP-STABLE rate-limit keys (the #1063/#1064 lesson: never
> key a rate-limit on a value that advances every frame at warp - cycle / UT / seed / frame -
> it mints a fresh key per frame and defeats the throttle). The shared `RenderTraceWindow` /
> recordingId-keyed window and the per-pid pre-event ring buffer stay DEFERRED (the "optional
> second cut" notes below remain accurate for those two pieces). The design body below is
> unchanged.*

---

## Why this exists

The flight-scene mesh tracer (`GhostRenderTrace`) instruments essentially ONE
per-frame placement path: the engine's `RenderInRangeGhost`. "What we decided" and
"what rendered" are the same thing there, because the engine sets the ghost
`Transform` and that transform IS the truth.

The map / tracking-station render path is the opposite. It is smeared across about
six Harmony patches, two OnGUI passes, and a 0.25 s lifecycle tick, each running at a
different execution order and cadence, and each carrying its OWN ad-hoc diagnostic
logging under a different tag:

- `GhostMap` (orbit-update decision, icon-pos-delta, lifecycle summary, body-frame
  cache, destroy decision)
- `GhostOrbitLine` (line.active / drawIcons decision)
- `GhostOrbitArc` (arc clip)
- `TrackingStation` (atmospheric-marker summary, per-marker draw)
- `GhostRenderProbe` (the opt-in end-of-frame probe prototype)
- `ReaimSeam` (map-apply seam)

Investigating one wrong icon today means grepping six tags and hand-stitching frames
across patches that fire at different times. The fragmentation is the problem, and it
is why one-off probes keep getting hand-added: the `fix-ghost-map-icon-frozen-arc`
branch alone added "icon-orientation truth", "orbit-line vs vessel-transform
longitude divergence", "per-frame ghost icon-position truth", and a whole new
`GhostRenderStateProbe` MonoBehaviour; two sibling worktrees added "change-based +
stall" draw logging on top.

A second, structural reason the map needs this more than the mesh: on the map, for the
proto-vessel surfaces, "what Parsek decided" and "what KSP rendered" genuinely
DIVERGE, and the divergence IS the bug class. The doc comment already on
`GhostRenderStateProbe` states it precisely: `GhostOrbitLinePatch` logs a steady
"line.active=True", but KSP or another patch can toggle `orbitRenderer.line.active`
BETWEEN our Postfix invocations, so the orbit line blinks on screen while our decision
log shows no change at all. Reading the actual rendered state at end-of-frame catches
that directly. This is exactly what the prototype `GhostRenderStateProbe` already does,
which is why the first cut below is "fold the probe in and gate it" rather than the
heavier reconciliation layer.

---

## Goals

1. One tracer, one tag namespace, one gating switch, one `key=value` line schema for
   the entire map / TS render surface. A single grep filter lights up every surface
   around an event.
2. Log every discrete render lifecycle / structural event, always, when the tracer is
   enabled (the explicit comprehensiveness requirement):
   - every new ghost map vessel created (and destroyed)
   - the ghost's first (init) position placed on its orbit / trajectory
   - every new orbit segment applied to a ghost (segment transition, source change,
     SOI change, loop epoch shift application)
   - every orbit-line / icon / polyline / marker visibility transition
3. Capture continuous per-frame rendered truth WITHOUT per-frame spam: change-based
   (`VerboseOnChange`) plus window-gated detail, so every transition is recorded but
   steady state is not re-logged each frame.
4. (Optional second cut) Reconcile decision-vs-truth for the PROTO-VESSEL surfaces:
   when Parsek's intended state and KSP's actual rendered state disagree, emit a
   mismatch line.
5. Off by default. Read-only. Zero idle cost beyond a null-check plus a bool check
   when disabled, matching `GhostRenderTrace` and `TraceSeparation`.
6. Fully unit-testable: pure gate, pure anomaly predicates, pure reconciliation,
   Unity-ECall isolation, `ForceEnabledForTesting` + frame-counter override seams.

## Non-goals

- Not a performance profiler. Frame timing stays in the `Diagnostics/` subsystem
  (`RollingTimingBuffer` etc.); the tracer may READ a count from there for context but
  does not own timing.
- Not always-on. It is a forensic tool, like `ghostRenderTracing`. The warning label
  ("huge logs") carries over.
- Pure observation for the tracer itself (no recording-format or decision changes).
  The one production change it requires is completing the `vesselPidToRecordingId`
  reverse map for chain ghosts (see Identity key): a latent-gap fix that, as a side
  effect, makes chain ghosts correctly participate in polyline-ownership icon hiding
  (the same behavior timeline ghosts already get). That is a small, intended
  rendering-behavior change for chain ghosts and must be validated in a playtest.

---

## Recommended first cut (probe-only MVP)

The original draft led with a decision-hook + reconciliation layer. The review
correctly flagged that as the riskiest, least-grounded part. The motivating bug class
(line blinks / icon teleports while our decision log shows nothing) is detectable from
the END-OF-FRAME TRUTH READ ALONE, which the prototype already implements. So the
recommended sequence is:

- MVP (Phases 1-3): fold `GhostRenderStateProbe` into `MapRenderProbe`, gate it on the
  new `mapRenderTracing` setting, emit Tier-A structural events + Tier-B change-based
  truth + the Tier-C jump / line-blink / orbit-discontinuity anomalies. This is
  pid-keyed and does NOT need recordingId, the reverse-map fix, the shared window, or
  reconciliation. It already captures the blink/jump bug class.
- Optional second cut (Phases 4-7): decision hooks + intended-state capture +
  `decision-vs-truth` reconciliation + the shared window with `GhostRenderTrace`. Build
  this only if a real bug needs the "why we decided X" attribution that the truth read
  alone cannot give.

The rest of this doc specifies the full system; the phased plan marks the MVP cut.

---

## What gets logged

Three tiers, matching the `GhostRenderTrace` gate model (force / important / windowed):

### Tier A: structural events (always emitted when enabled)

These are the user-enumerated "every new ghost / segment / init position / all
rendering" events. Each is `important = true` (emits regardless of window) AND opens a
detailed window so the surrounding frames get full per-frame detail.

| Event phase | Fired when | Opens window |
| --- | --- | --- |
| `GhostCreated` | a ghost map vessel is created (lifecycle tick / first map appearance) | InitialWindow |
| `GhostDestroyed` | a ghost map vessel is destroyed / retired | (short) |
| `FirstPosition` | the ghost's first world position is placed on its orbit/trajectory | InitialWindow |
| `SegmentApplied` | a new `OrbitSegment` is applied (segment index change, source change Segment/TerminalOrbit/StateVector, SOI/body change, loop epoch shift applied) | SegmentChangeWindow |
| `LineVisibilityChange` | `line.active` or `drawIcons` decision transitions | SectionChangeWindow |
| `PolylineLegChange` | a polyline leg activates / deactivates for a recording, or `IsRenderingNonOrbitalLeg` flips | SectionChangeWindow |
| `MarkerVisibilityChange` | an IMGUI / atmospheric marker starts or stops drawing, or its skip-reason changes | SectionChangeWindow |

`FirstPosition` has two emit paths for the same phase: in the MVP it is probe-derived
(emitted on the first end-of-frame truth read for a pid, needs no recordingId or
decision hook); in the second cut it is also decision-derived at the orbit-apply caller,
carrying the applied `OrbitSegment`. Same phase tag, two sources. The MVP emits
`GhostCreated` / `GhostDestroyed` from the lifecycle path and `FirstPosition` from the
first probe read; `SegmentApplied` and the decision-derived variants are second-cut.

### Tier B: per-frame rendered truth (change-based, PROTO surfaces only)

The end-of-frame probe samples the ACTUAL state per tracked ghost and emits one line
per field only when that field changes (`VerboseOnChange`), so a one-frame toggle out
and back is two lines, not silence, and steady state is one line then quiet:

- renderer: `OrbitRendererBase.{enabled, drawMode, drawIcons}`
- line: `VectorLine.active`, read directly via the public `OrbitRendererBase.OrbitLine` property (no reflection: the field is `orbitLine`, exposed by `OrbitLine`)
- orbit: `OrbitDriver.orbit.{referenceBody, semiMajorAxis, eccentricity}`
- world position: `Vessel.GetWorldPos3D()` (jump detector, not on-change)

Inside an open detailed window, the probe additionally emits a full per-frame snapshot
line (ungated) so a window captures continuous motion, not just transitions.

Limitation (explicit): the probe runs in `LateUpdate` at execution order 10000, which
is BEFORE the OnGUI phase. The two IMGUI surfaces (`DrawMapMarkers`,
`DrawAtmosphericMarkers`) draw in OnGUI, so the probe cannot read their rendered truth.
This is acceptable because for IMGUI the decision and the draw are the SAME call: the
marker is blitted at exactly the world position the code computed, so there is no
decision-vs-truth gap to reconcile. IMGUI surfaces are therefore decision-only (Tier A
visibility transitions + the existing `meshVsTraj` gap diagnostic), and Tier B / the
reconciliation layer cover only the proto-vessel surfaces, where KSP or another patch
can mutate state between our decision and the render.

### Tier C: anomalies (always emitted + open window + flush ring buffer)

Pure predicates, each with explicit suppression rationale (cf. the mesh tracer's
floating-origin / zero-velocity carve-outs):

- `icon-jump`: position delta exceeds expected motion. (Shipped refinement, branch
  `maprender-iconjump-warp`: the delta is measured in the orbit's BODY-RELATIVE frame
  `GetWorldPos3D - referenceBody.position`, NOT the raw world frame - comparing a
  raw-world delta against a body-centered orbital speed flagged smooth fast coasts of
  distant ghosts at high warp as false positives, because the reference-body's own
  world motion dominates the raw delta.) Expected motion is
  orbit-derived (orbital speed * dt * warpRate), an improvement over the prototype's
  fixed 1000 km/frame threshold which was warp-tuned rather than warp-aware. Pin the
  `dt` source explicitly (`Time.unscaledDeltaTime` for the visual frame) and read
  `TimeWarp.CurrentRate`; keep the prototype's fixed threshold as a floor for
  degenerate / near-zero-velocity orbits. Suppressed on floating-origin shift frames
  (reuse `ReFlySettleStabilityTracker.LastFloatingOriginShiftFrame`, as
  `GhostRenderTrace` does) and on the first frame after a per-pid state reset (see
  scene-transition clearing below).
- `line-blink`: `line.active` toggled within N frames. Detectable from the truth read
  alone (no decision needed), so it is in the MVP.
- `orbit-discontinuity`: sma / ecc / body changed between truth reads without a
  `SegmentApplied` (second cut) or SOI change explaining it. In the MVP this degrades
  to a plain change-log; the "without an explaining event" qualifier needs the decision
  layer.
- `polyline-orbit-overlap`: `IsRenderingNonOrbitalLeg(rec)` true AND `line.active`
  true for the same ghost on the same frame (the seam the `map-trajectory-polyline`
  worktree fought; both surfaces drawing at once).
- `decision-vs-truth` (second cut only): a same-frame intended-state recorded by a
  decision hook disagrees with the end-of-frame truth read. See the staleness rule
  under Two-layer capture.

On any anomaly, flush a short pre-event ring buffer so the frames LEADING UP to the
blink/jump are dumped too, not just the ones after. This is modeled on
`TraceSeparation`'s pre-event window, but note `TraceSeparation` keeps a single global
ring (`TraceSeparation.cs:29`); the map tracer needs a small per-pid ring, which is new
code, not a drop-in reuse.

---

## Core architecture

A static `internal` class `MapRenderTrace`, structurally a sibling of
`GhostRenderTrace`, plus one MonoBehaviour for the end-of-frame truth pass (fold in
`GhostRenderStateProbe`).

### Identity key

Two different keys for two different purposes, which resolves both the chain-ghost gap
and the recordingId-collision concern the review raised:

- Per-ghost STATE (prevWorldPos, prev field snapshots, per-pid ring buffer, frame
  intent) is keyed by `Vessel.persistentId`. This is the map world's native key
  (`GhostMapPresence.ghostMapVesselPids`) and covers EVERY ghost population with no
  gap: timeline (`vesselsByRecordingIndex`), chain (`vesselsByChainPid`), loop, and
  overlap. The MVP uses only this key.
- The detailed WINDOW (and, in the second cut, the shared registry with the mesh
  tracer) is keyed by `recordingId`, which is coarse on purpose: opening a window for a
  recording should light up all of that recording's surfaces. recordingId is mostly 1:1
  with a live proto-vessel on the map side because a looped mission replays through a
  single re-pinned proto (`GhostMapPresence` per-pid epoch shift +
  `ghostOrbitLoopShiftedPids`), unlike the flight mesh where loop members spawn
  multiple ghosts per recording (which is why the mesh tracer keys state by
  `(recordingId, ghostIndex)`).

recordingId is needed only for the window/second-cut, and is resolved per pid via
`GhostMapPresence.vesselPidToRecordingId`. That reverse map is currently INCOMPLETE: it
is written only inside `TrackRecordingGhostVessel` (`GhostMapPresence.cs:9032, 9044`),
which runs on the recording-index path; the chain create path (`CreateGhostVessel`,
around `GhostMapPresence.cs:2203-2206`) registers the pid in `ghostMapVesselPids` via
`BuildAndLoadGhostProtoVesselCore` (`:9455`) and sets `vesselsByChainPid[...] = vessel`,
but never writes `vesselPidToRecordingId`. So chain-tip ghosts have no reverse entry
today. The second cut must complete it: in `CreateGhostVessel` (where both `vessel` and
`traj` are in scope) write `vesselPidToRecordingId[vessel.persistentId] =
traj.RecordingId`, keyed by the live ghost `vessel.persistentId` (the same key
`TrackRecordingGhostVessel` uses at `:9032`), NOT `chain.OriginalVesselPid` (the
`vesselsByChainPid` key). This is a latent-gap fix worth making regardless, and it is
the ONLY production code the tracer touches. Until it lands, the MVP is unaffected
because it keys state by pid and does not need recordingId.

### RenderSurface enum

Mirror `GhostRenderTrace.RenderSurface`. Every emitted line carries `surface=` so a
grep slices by surface:

- `ProtoOrbitLine` - the scaled-space Vectrosity orbit line
- `ProtoIcon` - the native KSP map icon driven by the OrbitDriver
- `Polyline` - the non-orbital `GhostTrajectoryPolylineRenderer` leg
- `ImguiLabeledMarker` - the flight-scene `ParsekUI.DrawMapMarkers` labeled marker
- `AtmosphericMarker` - the TS `ParsekTrackingStation.DrawAtmosphericMarkers` marker

### Two-layer capture (second cut)

1. Decision layer: gated emits at each Parsek decision site recording WHAT we decided
   and WHY (the reason strings already in the scattered logs). Each decision hook also
   stores the per-pid intended state into a frame-scoped map, STAMPED with the Unity
   `frameCount` at which it was written.
2. Truth layer: one observer MonoBehaviour at `[DefaultExecutionOrder(10000)]` (after
   every patch and the polyline Driver at -50 have run) reads the ACTUAL rendered state
   and (a) emits Tier-B change-based truth, (b) runs Tier-C anomaly predicates, and
   (c) for proto surfaces only, runs `ReconcileMapRenderState(intended, actual)`.

Cadence / staleness rule (addresses a real false-positive risk): decision hooks fire at
four different cadences - a ~0.25 s lifecycle tick (`UpdateTrackingStationGhostLifecycle`),
per-physics `OrbitDriver.updateFromParameters` (icon-drive Prefix), per-render
`OrbitRendererBase.LateUpdate` (line Postfix), and per-OnGUI (markers). Reconciliation
compares intended-vs-actual ONLY when the stored intent was stamped on the SAME Unity
frame as the truth read (`IntentFreshnessFrames = 0`). As implemented, intent is recorded
only from the per-render line Postfix (same LateUpdate frame as the order-10000 probe), so
no slack is needed; allowing slack would reconcile a STALE intent against a later
grace-defer frame that legitimately changed the rendered state without re-recording it (a
false positive). Intent older than that is dropped, not flagged: a lifecycle-tick
intent that is 0.24 s (many frames under warp) stale is NOT a mismatch, it is just old.
Lifecycle-tick state changes are surfaced as Tier-A structural events, not as per-frame
reconciliation inputs. `ReconcileMapRenderState(intended, actual)` is a pure function
returning a list of mismatch tokens and is the unit-test surface for this layer.

### Detailed-window + ring buffer

Reuse the mesh tracer's window machinery: `detailedUntilByRecording` keyed by
recordingId, `OpenDetailedWindow(recordingId, ut, seconds, reason)`,
`IsDetailedWindowOpen`. Add a small per-pid pre-event ring buffer (new code) that
flushes on Tier-C anomalies.

### Shared window with `GhostRenderTrace` (second cut, re-specified)

The first draft called this "a pure move (no behavior change)". It is NOT, as written,
because `GhostRenderTrace.OpenDetailedWindow` early-returns on `!IsEnabled`
(`GhostRenderTrace.cs:234`) with `IsEnabled` hard-wired to `ghostRenderTracing`
(`:871-873`), and `GhostRenderTraceTests.cs:259` asserts the window does not open when
disabled. The correct, behavior-preserving design:

- Extract ONLY the registry (the `detailedUntilByRecording` dictionary plus a gate-free
  `Open` / `IsOpen` pair) into a shared `RenderTraceWindow` helper.
- Each tracer keeps its OWN gated wrapper: `GhostRenderTrace.OpenDetailedWindow` retains
  its `if (!IsEnabled) return;` then calls the shared `Open`. So the mesh tracer's
  disabled-behavior and the `:259` test contract are preserved.
- Cross-leak is already prevented at the EMIT site, not the window: `GhostRenderTrace`
  emits go through `ShouldEmitPhase`, which checks `IsEnabled` FIRST
  (`GhostRenderTrace.cs:617-620`) before consulting the window. So `mapRenderTracing` on
  with `ghostRenderTracing` off cannot make the mesh tracer emit, even if a map-opened
  window is visible in the shared registry. The window being open is necessary but not
  sufficient to emit; the per-tracer `IsEnabled` is the sufficient gate.

With that, the payoff is real and safe: a mesh anomaly opens the window and the map
tracer also emits full detail for that recording on the same frames, and vice versa,
giving full-stack mesh+map correlation from one grep, with neither tracer emitting when
its own setting is off. This depends on the completed reverse map (above), so it is
second-cut only.

---

## Settings toggle: `mapRenderTracing`

Wired like `ghostRenderTracing`. The first draft under-counted the mirror sites; the
COMPLETE set (verified against every `ghostRenderTracing` occurrence) is below. Missing
the save block in particular ships a setting that loads and restores but never persists.

1. `Source/Parsek/ParsekSettings.cs` (next to `ghostRenderTracing`, ~line 54):

   ```csharp
   [GameParameters.CustomParameterUI("Map/TS render tracing (Warning: huge logs)",
       toolTip = "When enabled, write detailed map and tracking-station ghost render diagnostics to KSP.log. Leave off for normal playtests.")]
   public bool mapRenderTracing = false;
   ```

2. `Source/Parsek/ParsekSettingsPersistence.cs` - mirror EVERY `ghostRenderTracing`
   site:
   - `private const string MapRenderTracingKey = "mapRenderTracing";` (~line 45)
   - `private static bool? storedMapRenderTracing;` backing field (~line 60)
   - load parse alongside `ghostRenderTracingStr` (~line 166), including the
     no-value-uses-default verbose line (~line 174)
   - debug-summary fragment in the load summary (~line 231)
   - restore block mirroring `:316-323`
   - `internal static void RecordMapRenderTracing(bool value)` mirroring
     `RecordGhostRenderTracing` (`:398`), including the dedup early-out (`:407`)
   - SAVE / `AddValue` block mirroring `:522-523` (REQUIRED for persistence; omitted in
     the first draft)
   - debug-summary fragment in the save summary (~line 546)
   - `ResetForTesting` clear mirroring `:569`
   - test accessor `GetStoredMapRenderTracing()` (`:635`) and setter
     `SetStoredMapRenderTracingForTesting(bool?)` (`:682`) - needed by the persistence
     round-trip unit test

3. `Source/Parsek/UI/SettingsWindowUI.cs`:
   - a `GUILayout.Toggle` in `DrawDiagnosticsSettings` immediately after the
     `ghostRenderTracing` toggle (`:446-454`), calling
     `ParsekSettingsPersistence.RecordMapRenderTracing(...)` and logging
     `Setting changed: mapRenderTracing=...`
   - the reset-to-defaults path that sets `s.ghostRenderTracing = false` (`:194/204`)
     also sets `s.mapRenderTracing = false` and records it

4. `MapRenderTrace.IsEnabled`:

   ```csharp
   private static bool IsEnabled =>
       ForceEnabledForTesting
       || (ParsekSettings.Current != null && ParsekSettings.Current.mapRenderTracing);
   ```

5. Fold `GhostRenderStateProbe.Enabled` (`GhostRenderStateProbe.cs:59`, currently a
   hardcoded `false`) into this setting: the end-of-frame probe's `LateUpdate`
   early-returns unless `MapRenderTrace.IsEnabled`. Delete the standalone `Enabled`
   flag.

Both representations are required to match the existing pattern: the
`[GameParameters.CustomParameterUI]` attribute drives the stock difficulty-settings
screen, and the manual `SettingsWindowUI` toggle drives the Parsek settings window.
`ghostRenderTracing` carries both; `mapRenderTracing` must too.

---

## Hook placement

Decision-layer hooks (second cut: record intended state + emit gated decision line).
Anchors are current-as-of this branch; treat as "around":

| Site | File:anchor | Surface | Contributes | recordingId in scope? |
| --- | --- | --- | --- | --- |
| `GhostOrbitIconDrivePatch.Prefix` | `Patches/GhostOrbitLinePatch.cs:107` | ProtoIcon | clamped `driveUT`, epoch shift, on-arc decision, propagated lat/lon/alt + mna, divergence-from-line | via pid reverse map |
| `GhostOrbitLinePatch.Postfix` | `Patches/GhostOrbitLinePatch.cs:534` | ProtoOrbitLine / ProtoIcon | intended `line.active` / `drawIcons` + reason | via pid reverse map |
| `GhostOrbitArcPatch.Prefix` | `Patches/GhostOrbitLinePatch.cs:919` | ProtoOrbitLine | arc bounds `fromE`/`toE`, ecc, sma, clipped draw range | via pid reverse map |
| `GhostMapPresence.UpdateGhostOrbitForRecording` | `GhostMapPresence.cs:6736` | ProtoOrbitLine | `SegmentApplied` + `FirstPosition`: applied `OrbitSegment`, source, world pos, body-frame bounds cache write | yes (recordingIndex -> id) |
| `GhostMapPresence.UpdateGhostOrbit` (chain) | `GhostMapPresence.cs:2248` | ProtoOrbitLine | chain-pid `SegmentApplied` + `FirstPosition` | via reverse map (post-fix); no `traj` in scope at :2248 |
| `GhostMapPresence.Update*GhostLifecycle` | `GhostMapPresence.cs:5801` | (lifecycle) | `GhostCreated` / `GhostDestroyed` | yes |
| `GhostTrajectoryPolylineRenderer.Driver.LateUpdate` | `Display/GhostTrajectoryPolylineRenderer.cs:921` | Polyline | which legs drew, head-UT gate, `IsRenderingNonOrbitalLeg` transition | yes (rec.RecordingId) |
| `ParsekUI.DrawMapMarkers` | `ParsekUI.cs:1069` | ImguiLabeledMarker | marker pos source (mesh vs traj), `meshVsTraj` gap, skip reason (decision-only) | yes |
| `ParsekTrackingStation.DrawAtmosphericMarkers` | `ParsekTrackingStation.cs:304` | AtmosphericMarker | per-marker draw + `AtmosphericMarkerSkipReason` transition (decision-only) | yes |

Hook `SegmentApplied` / `FirstPosition` at the two CALLERS
(`UpdateGhostOrbitForRecording`, `UpdateGhostOrbit`) rather than at the shared private
`ApplyOrbitToVessel` (`GhostMapPresence.cs:7715`), which is `private` and keyed by a
bare `Vessel`. `UpdateGhostOrbitForRecording` has the recordingIndex (hence id) in
scope; `UpdateGhostOrbit(uint chainPid, ...)` at `:2248` has only `chainPid` and must
resolve the id through the completed reverse map (`vesselPidToRecordingId`) or the
chain's tip recording id, so this caller hook is second-cut, after the reverse-map fix.
The callers already log "Orbit updated" today, so the hook lands where the existing
diagnostic is.

Truth-layer hook (the folded `GhostRenderStateProbe`):

| Site | Order | Contributes |
| --- | --- | --- |
| `MapRenderProbe.LateUpdate` (was `GhostRenderStateProbe`) | `[DefaultExecutionOrder(10000)]` | Tier-B change-based proto truth, Tier-C anomalies, and (second cut) proto-surface reconciliation vs the frame's same-frame intent |

Two fold-in fixes for the probe (both from the review):
- Clear per-pid state (`prevWorldPos` at `GhostRenderStateProbe.cs:74`, plus the
  on-change snapshots) on scene transition, otherwise a stale `prevWorldPos` carried
  across a TS <-> flight switch fires a spurious `icon-jump` on re-entry. Subscribe a
  scene-switch clear, or clear whenever `ghostMapVesselPids` is rebuilt.
- Iterate `ghostMapVesselPids` directly and resolve each pid to its `Vessel` (cached
  lookup) instead of scanning all of `FlightGlobals.Vessels` and filtering
  (`GhostRenderStateProbe.cs:99-109`). Enabled-path cost becomes O(ghosts), not O(all
  vessels). Idle cost is already zero (gated off).

---

## Log schema

Reuse `GhostRenderTrace`'s `key=value` line format and helpers (`FormatVector3`,
`FormatVector3d`, `FormatQuaternion`, `FormatDouble`, `Token`, `ShortId`, `Bool`).
Single subsystem tag `MapRenderTrace`. Prefix on every line:

```
phase=<Phase> surface=<Surface> rec=<shortId> recId=<full> pid=<persistentId>
frame=<unityFrame> currentUT=<liveUT> effUT=<recordedClockUT>
```

(rec/recId are `<none>` for chain ghosts until the reverse-map fix lands; pid is always
present.)

Representative lines:

```
phase=GhostCreated  surface=ProtoIcon      pid=100037 frame=1182 currentUT=1234.500 effUT=1234.500 vessel=Munar_Probe body=Mun
phase=FirstPosition surface=ProtoOrbitLine pid=100037 frame=1183 worldPos=(...) sma=... ecc=... reason=first-apply
phase=SegmentApplied surface=ProtoOrbitLine pid=100037 segIndex=3 source=Segment body=Mun sma=... ecc=... soiChanged=true loopShift=0.000
phase=LineVisibilityChange surface=ProtoOrbitLine pid=100037 lineActive=true drawIcons=ALL reason=visible-body-frame bounds=[...]
phase=Reconcile     surface=ProtoOrbitLine pid=100037 reason=line-toggled-after-decision intended=lineActive:true actual=lineActive:false  (IMPORTANT)
phase=Anomaly       surface=ProtoIcon      pid=100037 reason=icon-jump dPos=34211883m expectedDP=2860m warpRate=1000  (IMPORTANT)
```

Tier-A / Tier-C lines go to `ParsekLog.Info` (important). Tier-B and in-window detail
go to `ParsekLog.Verbose` / `VerboseOnChange` / `VerboseRateLimited`.

---

## What this consolidates / replaces

The tracer is a net SIMPLIFICATION of the current logging, not an addition on top:

- Absorbs and deletes the inline frozen-arc diagnostics: per-frame icon-position
  truth, icon-orientation truth fields, orbit-line vs vessel-transform longitude
  divergence, frame-to-frame jump detector. These move out of the decision hot paths
  into the gated probe.
- Folds `GhostRenderStateProbe` in entirely (its `Enabled` bool becomes the
  `mapRenderTracing` setting; its sampling becomes Tier-B; its jump detector becomes a
  Tier-C anomaly).
- Re-homes the existing scattered logs under one tag and one window: `GhostMap`
  orbit-update / icon-pos-delta / body-frame-cache, `GhostOrbitLine` decision,
  `GhostOrbitArc` clip, `TrackingStation` atmospheric-marker summary, `ReaimSeam`
  map-apply. Lifecycle COUNT summaries (created/destroyed/updated per tick) STAY as the
  existing `GhostMap` aggregate lines (they are not per-ghost detail); the tracer owns
  the per-ghost structural events.

The reconciliation layer (second cut) is genuinely new; the MVP is mostly
consolidation plus the existing probe.

---

## Performance and gating discipline

Per the project Visual and Recording Design Principle, many ghosts render at once and
the probe does per-frame, per-ghost truth reads (`OrbitRendererBase.OrbitLine.active`
plus the renderer / orbit fields) plus position sampling. That is forbidden in normal
play. Mitigations, all already established:

- Off by default. When disabled, every entry point is a `null`-check plus a bool check
  and returns. No truth reads, no sampling, no allocation. This matches
  `GhostRenderTrace` (`if (!IsEnabled) return;`).
- The line truth is read directly through the public `OrbitRendererBase.OrbitLine`
  property (no reflection). `VectorLine.active` is a compile-time member, the same
  access `GhostOrbitLinePatch` uses to toggle the ghost orbit line.
- Tier-B is change-based, not per-frame dumps. Full per-frame detail only inside open
  windows.
- Probe runs only in `FLIGHT` and `TRACKSTATION` and only when `ghostMapVesselPids` is
  non-empty (already gated this way in the prototype, `:92-97`). Enabled-path iteration
  is O(ghosts) after the fold-in fix above.
- Soft rate-limit on runaway anomalies (e.g. a hyperbola fling), as the prototype does
  on the jump detector (`:171-175`).

---

## Testability

Mirror `GhostRenderTrace`'s test design (`GhostRenderTraceTests`):

- `ForceEnabledForTesting` flag and `FrameCounterOverrideForTesting` /
  `FloatingOriginFrameOverrideForTesting` seams so xUnit drives deterministic frames.
- Pure predicates with no Unity dependency, unit-tested directly:
  `EvaluateGate(...)` (reason strings), `IsIconJump(dPos, expectedMotionMeters, currentFrame, floatingOriginShiftFrame, justReset, bodyChanged)` (dPos is the body-relative delta; `bodyChanged` suppresses SOI-crossing frames),
  `IsOrbitDiscontinuity(...)`, and `ReconcileMapRenderState(intended, actual)` (second
  cut).
- Unity-ECall isolation: any method that touches `Time.frameCount`,
  `Transform.position`, `Vessel.GetWorldPos3D`, or `VectorLine.active` (via the
  `OrbitRendererBase.OrbitLine` property) is isolated in its own helper so the JIT
  verifier in the xUnit runtime never walks an ECall on an unreachable branch.
- Log-assertion tests using `ParsekLog.TestSinkForTesting` to confirm each structural
  event and anomaly emits the expected `phase=` and `reason=` tokens.
- Persistence round-trip unit test for `mapRenderTracing` using the
  `Get/SetStoredMapRenderTracingForTesting` accessors and `ResetForTesting`, mirroring
  the existing `ghostRenderTracing` persistence tests (NOT an in-game test; the first
  draft mis-cited `RuntimeTests.cs:3702`, which is a Trace-Sep separation test, not a
  setting round-trip).
- Optional in-game smoke test (`InGameTests/RuntimeTests.cs`): flip `mapRenderTracing`
  on, spawn a ghost in the TS, assert the probe emits `GhostCreated` and at least one
  Tier-B truth line, restore the setting. There is no existing round-trip in-game test
  to mirror; write it fresh.

---

## Coordination and migration risks

1. Two sibling worktrees already added map/TS draw logging on a NEWER `origin/main`
   than this base branch: `add-map-ts-draw-logging` (touches
   `GhostTrajectoryPolylineRenderer.cs`, `GhostMapPresence.cs`,
   `ParsekPlaybackPolicy.cs`) and `map-ts-draw-logging` (collapses the per-frame
   GhostMap "Orbit updated" log to on-change). The tracer must SUBSUME these, not stack
   on them, or we double-log. Decision: implement on a branch off `origin/main` and
   land it after (or instead of) those worktrees, migrating their change-based logs
   onto the tracer.
2. `GhostRenderStateProbe` and the inline icon-truth diagnostics live on
   `fix-ghost-map-icon-frozen-arc`, which is not yet on `origin/main`. The implementing
   branch needs that code (sequence the frozen-arc PR first, or cherry-pick the probe).
3. The `RenderTraceWindow` extraction touches `GhostRenderTrace`. Keep it a registry-only
   move with the gate left in each tracer's wrapper (see Shared window) so
   `GhostRenderTraceTests` stays green, in its own commit.
4. `[ERS-exempt]` scope: `GhostMapPresence.cs`, `ParsekUI.cs`,
   `ParsekTrackingStation.cs`, `GhostTrajectoryPolylineRenderer.cs` are already
   allowlisted (`scripts/ers-els-audit-allowlist.txt`). The new probe reads
   `ghostMapVesselPids` + `FlightGlobals.Vessels`, NOT `RecordingStore.CommittedRecordings`,
   so `MapRenderTrace.cs` / `MapRenderProbe.cs` likely need no exemption. If any new
   read of `CommittedRecordings` is added, route through `EffectiveState` or add the
   file to the allowlist with a rationale, or `GrepAuditTests` fails the build.

---

## Open questions / decisions to confirm

1. Build the second-cut reconciliation layer at all, or stop at the probe-only MVP?
   Recommendation: MVP first, add reconciliation only if a real bug needs decision
   attribution that the truth read cannot give.
2. Complete the `vesselPidToRecordingId` reverse map for chain ghosts as part of this
   work (needed by the second cut, useful regardless), or treat it as a separate
   prerequisite fix? Recommendation: include it, scoped as a one-line latent-gap fix.
3. Expected-motion model for the jump detector: orbit-derived
   (orbital speed * dt * warpRate) with the prototype's fixed threshold as a floor.
   Confirm the `dt` and warp-rate sources.
4. Single setting vs two: one `mapRenderTracing` covering both map view and TS, or
   split. Recommendation: one setting; map and TS share the same render surfaces and
   code paths.

---

## Phased implementation plan

MVP (probe-only, no recordingId / reverse-map / reconciliation):

1. `mapRenderTracing` setting wiring (the full set in Settings toggle above) +
   `MapRenderTrace.IsEnabled` + the `RenderSurface` enum + reused formatters. No hooks.
   Pure-predicate + persistence-round-trip unit tests.
2. Fold `GhostRenderStateProbe` into `MapRenderProbe`: Tier-B change-based proto truth
   + the jump / line-blink anomalies, gated by `mapRenderTracing`; delete the standalone
   `Enabled` bool; add the scene-change state clear and the O(ghosts) iteration fix.
3. Tier-A structural events that are observable WITHOUT decision hooks
   (`GhostCreated` / `GhostDestroyed` from the lifecycle path; `FirstPosition` from the
   first truth read for a pid). Migrate / delete the inline frozen-arc diagnostics.

Optional second cut (decision attribution + reconciliation):

4. Complete `vesselPidToRecordingId` for chain ghosts; add the
   recordingId-keyed window; extract the shared `RenderTraceWindow` (registry-only,
   gate in wrappers, `GhostRenderTraceTests` green).
5. Decision hooks for the proto-vessel surfaces (`SegmentApplied`, `FirstPosition`,
   `LineVisibilityChange`, arc) at the callers, recording same-frame-stamped intent.
6. `ReconcileMapRenderState` + `decision-vs-truth` (proto only) + the same-frame
   staleness rule + `polyline-orbit-overlap` + the per-pid pre-event ring flush.
7. Decision hooks for the IMGUI surfaces (`DrawMapMarkers`, `DrawAtmosphericMarkers`,
   decision-only) and the polyline Driver (`PolylineLegChange`); migrate the two sibling
   worktrees' change-based logs onto the tracer.

Always:

8. CHANGELOG + `todo-and-known-bugs.md` + `.claude/CLAUDE.md` file-layout entry for
   `MapRenderTrace.cs` / `MapRenderProbe.cs` (+ shared `RenderTraceWindow` if built).

## Post-change checklist (when implemented)

- `ParsekScenario` OnSave/OnLoad: not needed; the setting persists via
  `ParsekSettingsPersistence`, not the scenario.
- Test generators: not needed; the tracer reads live render state, not recording data.
- Settings persistence round-trips (`mapRenderTracing` load / record / SAVE / restore /
  reset).
- `dotnet test` green, including the `GhostRenderTrace` tests after the (registry-only)
  window extraction.
- `.claude/CLAUDE.md`: add the new files to the key-files list; note the shared
  `RenderTraceWindow` and the completed `vesselPidToRecordingId` reverse map.

---

## Review history

- First draft: clean-context Opus review returned NEEDS REWORK. Findings addressed in
  this revision: (MUST-FIX) chain-tip keying gap in `vesselPidToRecordingId` -> key
  state by pid, complete the reverse map for the second cut; incomplete persistence
  spec -> added the save/`AddValue` block, `ResetForTesting`, and test accessors;
  "pure move" shared window -> re-specified as a registry-only extraction with the gate
  retained in each tracer wrapper and cross-leak prevented at the emit site. (SHOULD)
  reconciliation cannot see IMGUI truth (probe runs before OnGUI) -> stated explicitly,
  IMGUI is decision-only; decision-vs-truth cadence staleness -> same-frame intent rule;
  stale `prevWorldPos` across scene transitions -> scene-change clear; `ApplyOrbitToVessel`
  private/`Vessel`-keyed -> hook at the callers; wrong in-game-test reference ->
  corrected to a persistence round-trip unit test. (CONSIDER) probe-only MVP ->
  promoted to the recommended first cut and reflected in the phased plan; O(all vessels)
  iteration -> O(ghosts) fold-in fix; "reuse TraceSeparation" -> softened to "modeled
  on" (per-pid ring is new code).
- Second review (revised doc): verdict SHIP WITH CHANGES, no MUST-FIX. Applied the
  SHOULD-ADDRESS clarity fixes: reverse-map keyed by `vessel.persistentId` (not the
  chain pid); `UpdateGhostOrbit` (chain) at `:2248` has no `traj` in scope so its caller
  hook is second-cut via the completed reverse map; `FirstPosition` documented as
  MVP-probe vs second-cut-decision variants of one phase; tightened the
  `ghostMapVesselPids` registration site to `BuildAndLoadGhostProtoVesselCore` (`:9455`).
  All other revision claims verified correct against the source.
