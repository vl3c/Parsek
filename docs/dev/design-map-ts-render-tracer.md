# Design: Map / Tracking-Station Render Tracer (`MapRenderTrace`)

*Status: design note. Proposes a unified, gated observability system for how Parsek
renders ghosts in MAP VIEW and the TRACKING STATION, mirroring the flight-scene mesh
tracer (`GhostRenderTrace`). Read-only instrumentation: it never mutates renderer,
orbit, line, icon, or marker state. Off by default behind a new `mapRenderTracing`
setting wired exactly like `ghostRenderTracing`. It consolidates the six-plus ad-hoc
diagnostic tags currently scattered across the map render path, folds in the
prototype `GhostRenderStateProbe`, and adds the one capability the mesh tracer never
needed: end-of-frame reconciliation of "what Parsek decided to draw" against "what
KSP actually rendered".*

*Base branch for this doc: `fix-ghost-map-icon-frozen-arc` (it carries
`GhostRenderStateProbe` and the inline icon-truth diagnostics this design absorbs).
Implementation should branch from `origin/main` so it can also absorb the
`add-map-ts-draw-logging` / `map-ts-draw-logging` worktrees (see Coordination).*

Related: `docs/dev/observability-audit-2026-04-26.md`,
`docs/dev/plan-observability-logging-visibility.md`,
`Source/Parsek/GhostRenderTrace.cs` (the model), `Source/Parsek/TraceSeparation.cs`
(pre-event ring buffer prior art).

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

A second, structural reason the map needs this more than the mesh: on the map, "what
Parsek decided" and "what KSP rendered" genuinely DIVERGE, and the divergence IS the
bug class. The doc comment already on `GhostRenderStateProbe` states it precisely:
`GhostOrbitLinePatch` logs a steady "line.active=True", but KSP or another patch can
toggle `orbitRenderer.line.active` BETWEEN our Postfix invocations, so the orbit line
blinks on screen while our decision log shows no change at all. No amount of better
decision-site logging can see that. Only reading the actual rendered state at
end-of-frame can.

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
4. Reconcile decision-vs-truth: when Parsek's intended state and KSP's actual rendered
   state disagree, emit a mismatch line. This is the headline capability.
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
- Does not change rendering behavior, recording format, or any decision. Pure
  observation.
- Does not replace the mesh tracer. The two are siblings that SHARE the detailed-window
  registry (see Shared window).

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

### Tier B: per-frame rendered truth (change-based)

End-of-frame probe samples the ACTUAL state per tracked ghost and emits one line per
field only when that field changes (`VerboseOnChange`), so a one-frame toggle out and
back is two lines, not silence, and a steady state is one line then quiet:

- renderer: `OrbitRendererBase.{enabled, drawMode, drawIcons}`
- line: reflected `VectorLine.active`
- orbit: `OrbitDriver.orbit.{referenceBody, semiMajorAxis, eccentricity}`
- world position: `Vessel.GetWorldPos3D()` (jump detector, not on-change)

Inside an open detailed window, the probe additionally emits a full per-frame snapshot
line (ungated) so a window captures continuous motion, not just transitions.

### Tier C: anomalies (always emitted + open window + flush ring buffer)

Pure predicates, each with explicit suppression rationale (cf. the mesh tracer's
floating-origin / zero-velocity carve-outs):

- `icon-jump`: world position delta exceeds expected motion. Expected motion is
  orbit-derived (orbital speed * dt * warpRate), an improvement over the prototype's
  fixed 1000 km/frame threshold which was warp-tuned rather than warp-aware. Suppressed
  on floating-origin shift frames (reuse `ReFlySettleStabilityTracker.LastFloatingOriginShiftFrame`,
  as `GhostRenderTrace` does).
- `line-blink`: `line.active` toggled within N frames of a decision that reported
  "steady". This is the failure the prototype was built to catch.
- `orbit-discontinuity`: sma / ecc / body changed without a `SegmentApplied` or SOI
  change explaining it.
- `polyline-orbit-overlap`: `IsRenderingNonOrbitalLeg(rec)` true AND `line.active`
  true for the same recording on the same frame (the seam the `map-trajectory-polyline`
  worktree fought; both surfaces drawing at once).
- `decision-vs-truth`: intended state recorded by a Tier-A/decision hook this frame
  disagrees with the Tier-B probe read at end-of-frame.

On any anomaly, flush a short pre-event ring buffer (TraceSeparation pattern) so the
frames LEADING UP to the blink/jump are dumped too, not just the ones after. The mesh
tracer only opens forward windows; map bugs usually need the lead-in.

---

## Core architecture

A static `internal` class `MapRenderTrace`, structurally a sibling of
`GhostRenderTrace`, plus one MonoBehaviour for the end-of-frame truth pass (fold in
`GhostRenderStateProbe`).

### Identity key

The mesh tracer keys by `(recordingId, ghostIndex)`. The map world keys by
`Vessel.persistentId` (`GhostMapPresence.ghostMapVesselPids`), with three parallel maps
in `GhostMapPresence`: `vesselsByRecordingIndex`, `vesselsByChainPid`, and the reverse
`vesselPidToRecordingId`.

Decision: key tracer state by `recordingId` (stable across index shuffles and the key
that lets the map tracer share the mesh tracer's window registry), and join to the live
proto-vessel via the existing `vesselPidToRecordingId` reverse map. The end-of-frame
probe iterates `ghostMapVesselPids` and resolves each pid back to its recordingId for
keying.

### RenderSurface enum

Mirror `GhostRenderTrace.RenderSurface`. Every emitted line carries `surface=` so a
grep slices by surface:

- `ProtoOrbitLine` - the scaled-space Vectrosity orbit line
- `ProtoIcon` - the native KSP map icon driven by the OrbitDriver
- `Polyline` - the non-orbital `GhostTrajectoryPolylineRenderer` leg
- `ImguiLabeledMarker` - the flight-scene `ParsekUI.DrawMapMarkers` labeled marker
- `AtmosphericMarker` - the TS `ParsekTrackingStation.DrawAtmosphericMarkers` marker

### Two-layer capture

1. Decision layer: gated emits at each Parsek decision site recording WHAT we decided
   and WHY (the reason strings already in the scattered logs). Each decision hook also
   stores the per-pid intended state for this frame into a small frame-scoped map.
2. Truth layer: one observer MonoBehaviour at `[DefaultExecutionOrder(10000)]` (after
   every patch and the polyline Driver at -50 have run) reads the ACTUAL rendered state
   and (a) emits Tier-B change-based truth, (b) runs Tier-C anomaly predicates, and
   (c) runs `ReconcileMapRenderState(intended, actual)` and emits any mismatch.

`ReconcileMapRenderState` is a pure function taking the recorded intended-state and the
probe-read actual-state and returning a list of mismatch tokens. This is the unit-test
surface for the headline capability and has zero Unity dependencies.

### Detailed-window + ring buffer

Reuse the mesh tracer's window machinery: `detailedUntilByRecording` keyed by
recordingId, `OpenDetailedWindow(recordingId, ut, seconds, reason)`,
`IsDetailedWindowOpen`. Add a small per-pid pre-event ring buffer (TraceSeparation
style, a few hundred entries, cheap append) that flushes on Tier-C anomalies.

### Shared window with `GhostRenderTrace` (strong payoff)

Extract the window registry (the `detailedUntilByRecording` dictionary plus
`OpenDetailedWindow` / `IsDetailedWindowOpen`) into a tiny shared
`RenderTraceWindow` helper used by BOTH tracers, keyed by recordingId. Then a mesh
anomaly opens the window and the map tracer ALSO emits full detail for that recording
on the same frames, and vice versa. One grep, full-stack "where did this ghost go
wrong, mesh and map together". This is a small refactor of `GhostRenderTrace` (move
two fields and three methods) and is the single highest-leverage structural choice in
this design.

---

## Settings toggle: `mapRenderTracing`

Wired identically to `ghostRenderTracing`. Five touch points (anchors are
current-as-of this branch; match the `ghostRenderTracing` lines next to them):

1. `Source/Parsek/ParsekSettings.cs` (next to `ghostRenderTracing`, ~line 54):

   ```csharp
   [GameParameters.CustomParameterUI("Map/TS render tracing (Warning: huge logs)",
       toolTip = "When enabled, write detailed map and tracking-station ghost render diagnostics to KSP.log. Leave off for normal playtests.")]
   public bool mapRenderTracing = false;
   ```

2. `Source/Parsek/ParsekSettingsPersistence.cs` (mirror every `ghostRenderTracing`
   site):
   - `private const string MapRenderTracingKey = "mapRenderTracing";` (~line 45)
   - a `storedMapRenderTracing` nullable backing field
   - load parse alongside `ghostRenderTracingStr` (~line 166)
   - `internal static void RecordMapRenderTracing(bool value)` mirroring
     `RecordGhostRenderTracing` (~line 398)
   - restore block mirroring lines 316-323
   - the two debug-summary string fragments (~lines 231, 546)

3. `Source/Parsek/UI/SettingsWindowUI.cs`:
   - a `GUILayout.Toggle` in `DrawDiagnosticsSettings` immediately after the
     `ghostRenderTracing` toggle (~line 454), calling
     `ParsekSettingsPersistence.RecordMapRenderTracing(...)` and logging
     `Setting changed: mapRenderTracing=...`
   - the reset-to-defaults path that sets `s.ghostRenderTracing = false` (~lines
     194/204) also sets `s.mapRenderTracing = false` and records it

4. `MapRenderTrace.IsEnabled`:

   ```csharp
   private static bool IsEnabled =>
       ForceEnabledForTesting
       || (ParsekSettings.Current != null && ParsekSettings.Current.mapRenderTracing);
   ```

5. Fold `GhostRenderStateProbe.Enabled` (currently a hardcoded `false` opt-in bool)
   into this setting: the end-of-frame probe's `LateUpdate` early-returns unless
   `MapRenderTrace.IsEnabled`. Delete the standalone `Enabled` flag.

Note both representations are required to match the existing pattern: the
`[GameParameters.CustomParameterUI]` attribute drives the stock difficulty-settings
screen, and the manual `SettingsWindowUI` toggle drives the Parsek settings window.
`ghostRenderTracing` carries both; `mapRenderTracing` must too.

---

## Hook placement

Decision-layer hooks (record intended state + emit gated decision line). Anchors are
current-as-of this branch; treat as "around":

| Site | File:anchor | Surface | Contributes |
| --- | --- | --- | --- |
| `GhostOrbitIconDrivePatch.Prefix` | `Patches/GhostOrbitLinePatch.cs:107` | ProtoIcon | clamped `driveUT`, epoch shift, on-arc decision, propagated lat/lon/alt + mna, divergence-from-line |
| `GhostOrbitLinePatch.Postfix` | `Patches/GhostOrbitLinePatch.cs:534` | ProtoOrbitLine / ProtoIcon | intended `line.active` / `drawIcons` + reason (visible-body-frame / below-atmosphere / polyline-owns / stale-segment / terminal-visible / grace / out-of-body-frame) |
| `GhostOrbitArcPatch.Prefix` | `Patches/GhostOrbitLinePatch.cs:919` | ProtoOrbitLine | arc bounds `fromE`/`toE`, ecc, sma, clipped draw range |
| `GhostMapPresence.UpdateGhostOrbitForRecording` | `GhostMapPresence.cs:6736` | ProtoOrbitLine | `SegmentApplied`: applied `OrbitSegment`, source, world pos, body-frame bounds cache write |
| `GhostMapPresence.UpdateGhostOrbit` (chain) | `GhostMapPresence.cs:2248` | ProtoOrbitLine | chain-pid `SegmentApplied` |
| `GhostMapPresence.ApplyOrbitToVessel` | `GhostMapPresence.cs:7715` | ProtoOrbitLine / ProtoIcon | orbit elements set, SOI/body change, loop epoch shift; `FirstPosition` on first apply |
| `GhostMapPresence.Update*GhostLifecycle` | `GhostMapPresence.cs:5801` | (lifecycle) | `GhostCreated` / `GhostDestroyed` |
| `GhostTrajectoryPolylineRenderer.Driver.LateUpdate` | `Display/GhostTrajectoryPolylineRenderer.cs:921` | Polyline | which legs drew, head-UT gate, `IsRenderingNonOrbitalLeg` publish transition |
| `ParsekUI.DrawMapMarkers` | `ParsekUI.cs:1069` | ImguiLabeledMarker | marker pos source (mesh vs traj), `meshVsTraj` gap, skip reason |
| `ParsekTrackingStation.DrawAtmosphericMarkers` | `ParsekTrackingStation.cs:304` | AtmosphericMarker | per-marker draw + `AtmosphericMarkerSkipReason` transition |

Truth-layer hook (the folded `GhostRenderStateProbe`):

| Site | Order | Contributes |
| --- | --- | --- |
| `MapRenderProbe.LateUpdate` (was `GhostRenderStateProbe`) | `[DefaultExecutionOrder(10000)]` | Tier-B change-based truth, Tier-C anomalies, reconciliation vs the frame's recorded intended-state |

---

## Log schema

Reuse `GhostRenderTrace`'s `key=value` line format and helpers (`FormatVector3`,
`FormatVector3d`, `FormatQuaternion`, `FormatDouble`, `Token`, `ShortId`, `Bool`).
Single subsystem tag `MapRenderTrace`. Prefix on every line:

```
phase=<Phase> surface=<Surface> rec=<shortId> recId=<full> pid=<persistentId>
frame=<unityFrame> currentUT=<liveUT> effUT=<recordedClockUT>
```

Representative lines:

```
phase=GhostCreated  surface=ProtoIcon      rec=a1b2c3d4 pid=100037 frame=1182 currentUT=1234.500 effUT=1234.500 vessel=Munar_Probe body=Mun
phase=FirstPosition surface=ProtoOrbitLine rec=a1b2c3d4 pid=100037 frame=1183 worldPos=(...) sma=... ecc=... reason=first-apply
phase=SegmentApplied surface=ProtoOrbitLine rec=a1b2c3d4 pid=100037 segIndex=3 source=Segment body=Mun sma=... ecc=... soiChanged=true loopShift=0.000
phase=LineVisibilityChange surface=ProtoOrbitLine rec=a1b2c3d4 pid=100037 lineActive=true drawIcons=ALL reason=visible-body-frame bounds=[...]
phase=Reconcile     surface=ProtoOrbitLine rec=a1b2c3d4 pid=100037 reason=line-toggled-after-decision intended=lineActive:true actual=lineActive:false  (IMPORTANT)
phase=Anomaly       surface=ProtoIcon      rec=a1b2c3d4 pid=100037 reason=icon-jump dPos=34211883m expectedDP=2860m warpRate=1000  (IMPORTANT)
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
  map-apply. Lifecycle COUNT summaries (created/destroyed/updated per tick) can stay as
  `GhostMap` aggregate lines or move under the tracer as a periodic summary; either way
  the per-ghost decision detail comes from the tracer.

The reconciliation layer is genuinely new and is the reason to build a system rather
than just renaming tags.

---

## Performance and gating discipline

Per the project Visual and Recording Design Principle, many ghosts render at once and
the probe does per-frame, per-ghost reflection (`VectorLine.active`) plus position
sampling. That is forbidden in normal play. Mitigations, all already established in the
codebase:

- Off by default. When disabled, every entry point is a `null`-check plus a bool check
  and returns. No reflection, no sampling, no allocation. This matches
  `GhostRenderTrace` and `TraceSeparation`.
- Reflection `FieldInfo` / `PropertyInfo` cached once at first use (already done in the
  prototype).
- Tier-B is change-based, not per-frame dumps. Full per-frame detail only inside open
  windows.
- Probe runs only in `FLIGHT` and `TRACKSTATION` and only when `ghostMapVesselPids`
  is non-empty (already gated this way in the prototype).
- Soft rate-limit on runaway anomalies (e.g. a hyperbola fling), as the prototype does
  on the jump detector.

---

## Testability

Mirror `GhostRenderTrace`'s test design (`GhostRenderTraceTests`):

- `ForceEnabledForTesting` flag and a `FrameCounterOverrideForTesting` /
  `FloatingOriginFrameOverrideForTesting` seam so xUnit drives deterministic frames.
- Pure predicates with no Unity dependency, unit-tested directly:
  `EvaluateGate(...)` (reason strings), `IsIconJump(dPos, expected, warpRate, foFrame, currentFrame)`,
  `IsOrbitDiscontinuity(...)`, `ReconcileMapRenderState(intended, actual)`.
- Unity-ECall isolation: any method that touches `Time.frameCount`,
  `Transform.position`, `Vessel.GetWorldPos3D`, or reflected `VectorLine.active` is
  isolated in its own helper (the prototype already does this for the reflection path)
  so the JIT verifier in the xUnit runtime never walks an ECall on an unreachable
  branch.
- Log-assertion tests using `ParsekLog.TestSinkForTesting` to confirm each structural
  event and anomaly emits the expected `phase=` and `reason=` tokens.
- In-game test (`InGameTests/RuntimeTests.cs`, mirroring the existing
  `ghostRenderTracing` round-trip test at ~line 3702): flip `mapRenderTracing` on,
  spawn a ghost in the TS, assert the probe emits `GhostCreated` and at least one
  Tier-B truth line, restore the setting.

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
   branch needs that code (or to re-create the probe). Sequence the frozen-arc PR first,
   or cherry-pick the probe.
3. Extracting the shared `RenderTraceWindow` touches `GhostRenderTrace`. Keep that a
   pure move (no behavior change) with its existing tests green, in its own commit.
4. `[ERS-exempt]` scope: the probe and the polyline renderer already carry ERS-exempt
   rationale (physical-visibility scope). Any new file that reads
   `RecordingStore.CommittedRecordings` must either route through `EffectiveState` or
   be added to `scripts/ers-els-audit-allowlist.txt` with a one-line rationale, or the
   grep gate (`GrepAuditTests`) fails the build.

---

## Open questions / decisions to confirm

1. Window sharing: extract `RenderTraceWindow` shared with `GhostRenderTrace` (full
   stack correlation) vs keep a separate map-only window registry (simpler, no
   cross-tracer coupling). Recommendation: extract; the correlation payoff is large and
   the refactor is small.
2. Lifecycle COUNT summaries: keep the existing `GhostMap` aggregate per-tick
   created/destroyed/updated summary as-is, or re-home it under the tracer as a periodic
   `Summary` phase. Recommendation: keep the aggregate where it is (it is not per-ghost
   detail); the tracer owns the per-ghost structural events.
3. Expected-motion model for the jump detector: orbit-derived (orbital speed * dt *
   warpRate) vs the prototype's fixed threshold. Recommendation: orbit-derived, with the
   fixed threshold as a floor for degenerate orbits.
4. Single setting vs two: one `mapRenderTracing` covering both map view and TS, or
   split. Recommendation: one setting; map and TS share the same render surfaces and
   the same code paths.

---

## Phased implementation plan

Build in small, reviewable, independently-green steps (a `docs/dev/plans/` companion
plan can break these into commits):

1. Shared `RenderTraceWindow` extraction from `GhostRenderTrace` (pure move, tests
   stay green).
2. `MapRenderTrace` skeleton: `IsEnabled`, `RenderSurface`, formatters (reuse), the
   gate, `OpenDetailedWindow` via the shared window, `EmitRaw`. Plus the
   `mapRenderTracing` setting wiring (the five touch points above). No hooks yet;
   pure-predicate unit tests.
3. Fold `GhostRenderStateProbe` into `MapRenderProbe`: Tier-B change-based truth + the
   jump anomaly, gated by `mapRenderTracing`. Delete the standalone `Enabled` bool.
4. Decision hooks for the proto-vessel surface (`SegmentApplied`, `FirstPosition`,
   `LineVisibilityChange`, arc), recording intended state for reconciliation.
5. `ReconcileMapRenderState` + the `decision-vs-truth`, `line-blink`,
   `polyline-orbit-overlap`, `orbit-discontinuity` anomalies + the pre-event ring
   buffer flush.
6. Decision hooks for the IMGUI surfaces (`ParsekUI.DrawMapMarkers`,
   `ParsekTrackingStation.DrawAtmosphericMarkers`) and the polyline Driver
   (`PolylineLegChange`).
7. Migrate / delete the scattered inline diagnostics and the two worktrees' change-based
   logs onto the tracer.
8. In-game round-trip test; CHANGELOG + `todo-and-known-bugs.md` + `.claude/CLAUDE.md`
   file-layout entry for the new files.

## Post-change checklist (when implemented)

- `ParsekScenario` OnSave/OnLoad: not needed; the setting persists via
  `ParsekSettingsPersistence`, not the scenario.
- Test generators: not needed; the tracer reads live render state, not recording data.
- Settings persistence round-trips (`mapRenderTracing` load / record / restore /
  reset).
- `dotnet test` green, including the `GhostRenderTrace` tests after the window
  extraction.
- `.claude/CLAUDE.md`: add `MapRenderTrace.cs` / `MapRenderProbe.cs` to the key-files
  list; note the shared `RenderTraceWindow`.
