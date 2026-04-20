# Incomplete-Ballistic Extrapolation

## Problem

A player launches a crewed capsule on a suborbital arc and, mid-flight, decides to leave — to the Space Center, Tracking Station, or main menu. Today, the ghost replay of that flight freezes in mid-air at the moment the player exited: a plane at 3 km hangs motionless in the sky; a missile near apoapsis stops as if time stopped; a hyperbolic probe halts at the moment of departure instead of continuing toward its target. The player returns later, watches the ghost play back, and sees a broken illusion.

Stock KSP handles the equivalent "unloaded vessel" case by destroying the real vessel on atmosphere entry (for atmo bodies) or preserving it on rails (for orbiting / above-atmo). Parsek's ghost playback should match this mental model — the ghost should visibly follow its natural trajectory forward until it either lands in a stable orbit, impacts the atmosphere/ground of some body (and visibly disappears), or flies off into deep space.

## Terminology

- **Recording** — a stored timeline for one vessel: trajectory points, orbit segments, events, a terminal state, etc.
- **OrbitSegment** — one coast arc expressed as Kepler elements (sma/ecc/inc/argPe/lan/mna/epoch) in a body's frame, valid over `[startUT, endUT]`. Already in the codebase. This plan reuses the type for all new data (predicted + extrapolated); no new parallel list is introduced.
- **Predicted segment** — an `OrbitSegment` captured from `vessel.patchedConicSolver` at scene exit. Flagged by a new `isPredicted: true`.
- **Extrapolated segment** — an `OrbitSegment` computed by this plan's `BallisticExtrapolator` when the patched-conic chain does not reach a natural terminus. Also flagged `isPredicted: true`; distinguishable from solver-captured predicted segments only by the log trail and by whether it was produced in the extrapolation pass. Same shape on disk.
- **Last covered UT** — the UT at the end of the latest source in the precedence stack (recorded frames → patched chain). Extrapolation starts here.
- **Cutoff altitude** — body-dependent altitude at which extrapolation terminates. Has atmosphere → `body.atmosphereDepth`. No atmosphere → terrain altitude at projected lat/lon (with sea-level fallback).
- **Current SOI** — the body whose sphere of influence contains the ghost's position at playback UT.
- **Terminal UT** — the UT at which the ghost's visual lifetime ends. Stored as `ExplicitEndUT` on the Recording (existing field) — no new field. For extrapolated recordings, `ExplicitEndUT` is set to the atmo/ground contact UT or horizon-cap UT, and the existing `EndUT` property (which already prefers `ExplicitEndUT` when set) naturally becomes the authoritative playback / spawn / timeline boundary. All existing `EndUT` consumers inherit correct behaviour for free.

## Mental Model

A recording's trajectory timeline is a stack of sources, earliest first:

```
      live flight                scene exit                              terminalUT
          │                          │                                        │
          ▼                          ▼                                        ▼
 ┌─ recorded frames ──┐ ┌─ KSP patched-conic chain ──┐ ┌─ extrapolated arcs ──┐
 │                    │ │                              │ │                     │
 │ (actual traversed) │ │ (predicted by solver at      │ │ (Kepler, drag-free, │
 │                    │ │  scene exit, pure coast)     │ │  chained across     │
 │                    │ │                              │ │  SOIs)              │
 └────────────────────┘ └──────────────────────────────┘ └─────────────────────┘
                                                                              │
                                                                              ▼
                                                                    destroyed / orbiting
                                                                    (one of the stable
                                                                     or non-stable
                                                                     terminal states)
```

At playback, the ghost reads position from the appropriate source for the current UT. The transition between sources is invisible to the player — the ghost just keeps moving.

The extrapolator's only job is to choose the terminal bucket (`Destroyed` vs `Orbiting`) and produce the arcs covering `[endOfPatchedChain, terminalUT]`. The existing spawn gate (`ShouldSpawnAtRecordingEnd`) handles everything downstream: `Destroyed` is blocked from spawn and RSW; `Orbiting` spawns normally.

Map view in v1 continues to use the existing single-`OrbitRenderer` path: one active segment drawn per ghost at a time, in the segment's native body frame, matching the ghost's current SOI. The change in v1 is that the renderer's selection pool now includes predicted/extrapolated segments alongside traversed ones, so the drawn line extends past the recording's live-flight end.

The broader target — multi-segment chain with camera-focus-driven frame selection and ancestor-frame transforms (a Mun-flyby arc transformed into Kerbin frame when focused on Kerbin, etc.) — lives in a separate Future phase that introduces a new renderer. Core Principles describes that target rule so the v1 implementation does not paint itself into a corner.

## Core Principles

**Ghosts are real vessels in the player's mind.** They are on real trajectories at real times, and the player expects to see where they are going.

**Only stable terminal states spawn.** The existing gate in `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` (`GhostPlaybackLogic.cs:3636`) rejects every non-stable state (`SubOrbital`, `Destroyed`, `Docked`, `Boarded`, `Recovered`) from both RSW and the deferred spawn queue. This plan does not touch the gate — it only changes what terminal state a recording ends up with after extrapolation.

**Recordings capture actual paths, never hypotheticals.** An abandoned ghost executes zero planned burns. The patched-conic chain we snapshot must reflect pure coast; maneuver-node deltas are excluded by construction (walk only up to the first `MANEUVER` transition).

**Stock KSP is the arbiter of reality.** `Vessel.Situations` is the authoritative classification for whether extrapolation should run. Stock events drive crew-reservation state. Parsek mirrors; it does not invent outcomes.

**Frame-of-rendering follows camera focus; visibility follows SOI containment** — target rule for multi-segment rendering. At each playback UT, the ghost has one current SOI and the camera has one focused body `F`. For any future coast segment to draw, `F` must be the ghost's current SOI or an ancestor in the SOI hierarchy. When that condition holds, every future coast segment in the chain is rendered in `F`'s frame — natively if the segment's body is `F`, or transformed through the parent chain if the segment's body is a descendant of `F`. When the condition fails, nothing is drawn. Past segments are always hidden.

**v1 rendering scope.** v1 does not implement the full target rule above — the existing map/TS architecture draws one `OrbitSegment` per ghost via a single `OrbitRenderer` line, and expanding to a multi-segment chain with ancestor-frame transforms requires a new renderer. v1 preserves the existing architecture and only expands the data pool the renderer selects from, so the single-segment line continues drawing past recording end using predicted/extrapolated segments. The full focus+ancestry rule targets a Future phase.

## Scope

**In scope:** the uncovered tail `[lastCoveredUT, terminalUT]` of an incomplete recording at scene exit. Snapshot KSP's patched-conic chain first; extrapolate only past the end of that chain.

**Out of scope:**
- Replacing or overlapping recorded frames.
- Continuing to record an unloaded vessel across scenes (Parsek's `BackgroundRecorder` is an in-flight multi-vessel system; there is no cross-scene background recorder to integrate with).
- Mid-recording refresh of the patched snapshot on SOI change or node edit (possible future work).
- Drag modelling in atmosphere. Extrapolation is drag-free by design — a deliberate simplification accepted in discussion (overshoot = "observer assumes residual thrust").
- Changes to RSW, deferred spawn queue, or crew reservations. Existing gates and stock events already handle `Destroyed` correctly.

**Precedence stack** (highest to lowest):

1. Recorded flight frames — whatever the in-flight recorder captured (main vessel or any tree child handled by `BackgroundRecorder`).
2. Patched-conic snapshot — `vessel.patchedConicSolver`'s pre-computed chain captured at scene exit.
3. Kepler extrapolation — this plan, for intervals past the end of the snapshotted chain.

## Data Model

### Existing types (modified)

`OrbitSegment` gains one field:

```csharp
public bool isPredicted;  // true for segments captured from patchedConicSolver at scene exit
                          //   OR produced by BallisticExtrapolator (same shape, same storage)
                          // false for segments traversed during live flight
                          // defaults to false; legacy recordings load with isPredicted = false
```

`Recording` — no new fields. The existing `ExplicitEndUT` is authoritative for extrapolated terminal UT; the existing `OrbitSegments` list holds all predicted + extrapolated segments alongside traversed ones. `EndUT` (property at `Recording.cs:259-270`) already prefers `ExplicitEndUT` when set, so playback, spawn timing, loop timing, timeline entries, watch protection — every `EndUT` consumer documented in "What Doesn't Change" — reach the extrapolated terminus without modification.

### New types

```csharp
internal struct ExtrapolationResult
{
    public TerminalState terminalState;   // Destroyed or Orbiting
    public double terminalUT;
    public string terminalBodyName;
    public Vector3d terminalPosition;
    public Vector3d terminalVelocity;
    public List<OrbitSegment> segments;   // appended into Recording.OrbitSegments with isPredicted = true
    public ExtrapolationFailureReason failureReason;  // none / degenerate-ecc / pqs-unavailable / ...
}

internal struct ExtrapolationLimits
{
    public double maxHorizonYears;     // default 50
    public int maxSoiTransitions;      // default 8
    public double soiSampleStep;       // default 3600 s (coarse SOI-encounter scan)
    public static ExtrapolationLimits Default => new ExtrapolationLimits { ... };
}
```

### Constants

```csharp
internal const int PatchedConicSolverCaptureLimit = 8;   // max patches walked from vessel.orbit.nextPatch
```

### `TerminalState` enum

No change. `Destroyed` already exists (`TerminalState.cs:9`) and is already blocked by `ShouldSpawnAtRecordingEnd` (`GhostPlaybackLogic.cs:3636`) alongside `Recovered`, `Docked`, `Boarded`, `SubOrbital`.

### Persistence

Modern recordings (format v3+, currently v4) serialise via the binary codec in `TrajectorySidecarBinary.cs`; legacy v0–v2 use `ConfigNode`. Both paths must carry the new `OrbitSegment.isPredicted` flag, and `ExplicitEndUT` is already persisted — no new top-level Recording field.

**Format bump: v4 → v5.** Rationale: `OrbitSegment.isPredicted` changes on-disk segment shape. A v4 reader must gracefully default `isPredicted = false` when a v4 sidecar omits the flag; a v5 reader decodes the new flag explicitly.

Binary codec changes (Phase 3 scope):

- Add `isPredicted` to the per-segment encoding in the binary sidecar section that writes orbit segments. One bit in an existing flags byte, or a new byte per segment — decide during implementation based on the surrounding layout.
- Bump `CurrentBinaryVersion` / `SparsePointBinaryVersion` in `TrajectorySidecarBinary.cs` appropriately.
- Bump `RecordingStore.CurrentRecordingFormatVersion` to 5.
- Version-gate the decode path: v4 and earlier → `isPredicted = false` for all segments; v5+ → decode the flag.

Text ConfigNode changes (for legacy-path completeness, even though new recordings don't hit it):

```
SEGMENT { ...existing fields...; isPredicted = False }   // new field
SEGMENT { ...existing fields...; isPredicted = True }
```

All doubles serialised via `ToString("R", CultureInfo.InvariantCulture)` per project convention. `isPredicted` defaults to `False` for legacy load compatibility.

## Behavior

### Scene-exit finalization

Triggered by `FlightRecorder.FinalizeRecordingState(isSceneExit: true)`. Runs during `OnSceneChangeRequested` before the scene tears down. Steps:

1. **Snapshot patched chain.** Call `SnapshotPatchedConicChain(vessel, PatchedConicSolverCaptureLimit)`. Append resulting `OrbitSegment`s (each with `isPredicted = true`) to `recording.OrbitSegments`.
2. **Decide if extrapolation is warranted.** `ShouldExtrapolate(vessel.situation, lastOrbit, lastBody)` returns bool.
3. **Run extrapolator if warranted.** Start state = state vector at end of patched chain (or last recorded frame if chain is empty). Produces `ExtrapolationResult` containing additional `OrbitSegment`s (`isPredicted = true`).
4. **Assign outputs.** Append extrapolator segments to `recording.OrbitSegments`. Set `recording.TerminalStateValue`. Set `recording.ExplicitEndUT = result.terminalUT`. Populate existing terminal-orbit fields if the final segment represents a stable resting orbit.
5. **Short-circuit for solver-predicted impact.** If the patched chain's last patch ends with closest-approach altitude below cutoff on its body, set `TerminalState.Destroyed`, `recording.ExplicitEndUT = lastPatch.EndUT`, skip the extrapolator entirely.

Because `ExplicitEndUT` feeds the existing `Recording.EndUT` property (`Recording.cs:259-270`) via the `ExplicitEndUT > endUT` branch, every existing `EndUT` consumer — `GhostPlaybackEngine` playback end detection, `TimelineBuilder` entries, watch-mode protection, spawn timing, loop timing — automatically uses the extrapolated terminal UT. No threaded changes to those systems; no new field on Recording.

### Playback rendering

During `[startUT, terminalUT]`:

- **Position resolution** (each frame): evaluate whichever source covers the current UT. Recorded frames where they exist; `OrbitSegment` Kepler evaluation for any segment (`isPredicted` or not) whose `[startUT, endUT]` contains the playback UT. Predicted and extrapolated segments go through the same code path as traversed segments — same type, same storage, same lookup.
- **Orientation during extrapolation:** reuse the last captured `srfRelRotation`. No tumble simulation.
- **Map-view lines — v1 scope (existing renderer extended in data only):** The current Parsek map/TS path exposes one active `OrbitSegment` per ghost through a single `OrbitRenderer` line (`GhostMapPresence` / `GhostOrbitLinePatch`). v1 keeps that architecture unchanged and only expands the pool the renderer selects from:
  - Today: the renderer picks the single `OrbitSegment` whose `[startUT, endUT]` contains the playback UT and whose body matches the ghost's current SOI.
  - v1 change: the pool now includes predicted/extrapolated segments alongside traversed ones, so that after the recording ends the renderer continues drawing the ghost's current-SOI orbit line from the extrapolated segments. No new data path; no extrapolated-arc list outside `OrbitSegments`.
  - Coast-vs-thrust rule still applies — line suppressed during thrust-point segments.
  - Past-segments hidden, same as today.
  - Camera focus and SOI-ancestry considerations do not apply in v1: the existing renderer already draws the one segment in its native body's frame; there is no chain of segments and no cross-frame transform.
- **Map-view lines — Future (see edge case 29, separate phase):** Multi-segment chain display + ancestor-frame transforms (Mun-flyby arc transformed into Kerbin frame when focused on Kerbin, etc.) require a new renderer that produces polylines rather than a single stock `OrbitRenderer`. Out of v1 scope. The rule defined in Core Principles ("frame-of-rendering follows camera focus; visibility follows SOI containment") is the target for that phase.

### Ghost despawn

`Recording.ExplicitEndUT` is set to the extrapolated terminus, so `Recording.EndUT` returns that value. At `playbackUT >= Recording.EndUT`, the existing end-of-playback path in `GhostPlaybackEngine` fires.

For a `Destroyed` recording:
- Ghost icon disappears silently (no FX in v1).
- `GhostMapPresence` ProtoVessel (if created) is removed by the existing end-of-playback teardown.
- Spawn path never fires — `ShouldSpawnAtRecordingEnd` returns `(false, "terminal state Destroyed")`.

For `Orbiting` recordings (including horizon-capped extrapolation):
- Normal spawn path runs at `EndUT` via existing gates. No behavioural change from today.

## Edge Cases

Numbered for reference. Each marked v1 (handled now) or Future (deferred).

1. **(v1) Hyperbolic escape to Kerbol.** SOI handoff places the ghost in Kerbol frame. If the resulting Kerbol orbit never re-encounters a body within horizon, terminal = `Orbiting` at horizon, spawns normally.
2. **(v1) Re-entry from a high Kepler arc.** Arc dips back into atmo; cutoff fires at atmo-entry UT; terminal = `Destroyed`. No special case.
3. **(v1) Gas giants (Jool).** `body.atmosphere == true` → cutoff at atmo boundary. Atmo destruction always fires before "surface" contact; no special case for bodies without solid surface.
4. **(v1) Long-range interplanetary direct impact (inner-Kerbol → Jool).** Patched-conic chain captures whatever the solver precomputed; extrapolator chains further via SOI handoff to Jool SOI; crosses atmo boundary; terminates `Destroyed`. One-time cost at finalization.
5. **(v1) Never-impacts trajectory.** Bounded horizon (50 years) / SOI-transitions (8) produces `Orbiting` at horizon; ghost spawns at horizon state. No runaway compute.
6. **(v1) Parked craft / taxiing rover at scene exit.** `vessel.situation == LANDED` → skip extrapolation. Recording terminates in `Landed` as today. Mirrors stock unload behaviour.
7. **(v1) Rover mid-drive.** Same as parked — `LANDED` → skip. Does not "launch" the rover visually.
8. **(v1) Plane abandoned at altitude.** `vessel.situation == FLYING` → extrapolate. Cutoff rule: first altitude crossing of atmo boundary going DOWN, or first ground contact if the whole arc stays below atmo depth. For a low-altitude plane that stays below `atmosphereDepth`, cutoff = ground.
9. **(v1) Mountain vs. sea impact on non-atmo body.** Terrain sampling (Option B with one PQS lookup) handles flat terrain within tens of metres. Cliffs / steep ridges may cause a brief pre-despawn clipping — accepted cosmetic limitation.
10. **(v1) PQS unavailable for target body (unfocused).** Sea-level fallback. Logged. Accepted limitation; ghost may impact slightly below true terrain.
11. **(v1) Body rotation during long falls.** Propagation in body-inertial frame; lat/lon derived at terminal UT using `body.transform.InverseTransformPoint`. Surface rotates correctly underneath.
12. **(v1) Horizontal overshoot on plane-drop.** Ghost continues no-drag arc past where a real plane would stop. Accepted per discussion ("observer assumes residual thrust").
13. **(v1) Recording spans multiple bodies already.** Extrapolation starts from whatever body/UT the last-covered state was in. Existing content untouched.
14. **(v1) Very short extrapolation intervals.** Tiny arcs that terminate within seconds are produced and used as-is.
15. **(v1) Player `maxGeometryPatches` set low.** Snapshot temporarily overrides in a `try/finally`; player's map-view setting is restored before scene change completes.
16. **(v1) Maneuver nodes present at scene exit.** Walk `nextPatch` only up to (not including) first patch where `patchEndTransition == MANEUVER`. Chain is pure coast; no node mutation; no restore logic.
17. **(v1) Player edits/deletes nodes after scene exit.** Irrelevant — snapshot is immutable.
18. **(v1) Patched-conic chain terminates mid-space.** Extrapolator picks up at last patch's `endUT` and chains via SOI handoff to atmo/ground/horizon.
19. **(v1) Patched-conic chain already predicts impact.** Short-circuit: set `TerminalStateValue = Destroyed`, `ExplicitEndUT = lastPatch.EndUT`, skip extrapolator.
20. **(v1) Player exits mid-thrust-burn.** `vessel.situation` is likely `FLYING` or `SUB_ORBITAL` → extrapolate. Start state = current state vector; extrapolation is pure coast from that state forward (matches "abandoned, no further thrust" semantics).
21. **(v1) Player exits with no orbit and no maneuver (pre-launch, on pad).** `vessel.situation == PRELAUNCH` → skip. Stays on the pad.
22. **(v1) Ghost with `Destroyed` state in Real Spawn Control list.** Already filtered out by `ShouldSpawnAtRecordingEnd`. No row shown, no "warp to spawn" offered.
23. **(v1) Ghost with `Destroyed` in deferred spawn queue during warp.** Can't enter — same gate blocks queue entry. No queue changes needed.
24. **(v1) Crew aboard at scene exit of a `Destroyed`-bound ghost.** Crew state is driven by stock KSP events on the real unloaded vessel, not by extrapolation. If stock destroys the real vessel, Jeb dies via stock; if stock preserves it, Jeb lives. Parsek reconciles from stock events as today.
25. **(v1) Reverting a flight mid-playback.** Existing revert infrastructure discards in-progress recordings. `ExplicitEndUT` and predicted/extrapolated `OrbitSegment`s ride along in the same Recording object; nothing special required.
26. **(Future) Camera focused on a body unrelated to ghost's SOI chain.** Ancestor-gating rule: hide all segments. Belongs to the future multi-segment renderer.
27. **(Future) Camera focused on Kerbol with ghosts throughout the Kerbol system.** Would render all ghosts' chains in Kerbol frame via chain-transform; potential clutter to calibrate then. Future phase.
28. **(Future) Player changes camera focus mid-playback.** Will trigger re-evaluation of which ghosts' segments are visible once the focus+ancestry rule lands. Not applicable in v1 (which renders one segment in its native frame regardless of camera).
29. **(Future) Mid-recording refresh of patched chain on SOI change / node edit.** Scene-exit-only capture is v1. Mid-recording refresh would update the stored predictions as the player re-plans, but has no rendering role until the recording actually ends. Defer.
30. **(Future) Background-recorder continuation across scenes.** No such system exists today. If one is added later, it inserts between (1) and (2) in the precedence stack with per-sub-interval gap filling.
31. **(Future) Atmospheric drag during extrapolation.** Drag-free is v1. A drag model would affect in-atmo plane-drop overshoot, but introduces calibration complexity and per-vessel drag data we don't store. Defer.
32. **(Future) Re-entry FX at destruction moment.** Silent disappear is v1. Could trigger stock heat FX for 2–3 s as a visual cue; adds polish, not required.
33. **(Future) Player-exposed sliders for horizon cap or capture limit.** Compile-time constants are v1. Expose later only if playtests reveal a concrete need.

## What Doesn't Change

Explicit list of systems NOT affected by this plan:

- `TerminalState` enum values — `Destroyed` already exists; no additions.
- `ShouldSpawnAtRecordingEnd` — no changes. Existing blocklist handles `Destroyed`.
- RSW / `SelectiveSpawnUI` / `SpawnControlUI` — no UI changes.
- `ParsekPlaybackPolicy` deferred spawn queue — no changes.
- `CrewReservationManager` — no changes.
- `Recording.EndUT` getter and every `EndUT` consumer (`GhostPlaybackEngine` end-of-playback, `TimelineBuilder`, watch protection, spawn timing, loop timing) — no changes. The authoritative boundary shifts via `ExplicitEndUT`, which `EndUT` already prefers. No threading through consumers.
- `GhostMapPresence` ProtoVessel lifecycle — continues to create at spawn and remove at end-of-playback; the end-of-playback UT simply comes from `ExplicitEndUT` now.
- Chain / loop systems — `ChainSegmentManager`, loop playback, overlap handling. A `Destroyed` recording's extrapolated segments play within its single loop cycle; next cycle re-runs from the start identically.
- Save-file main layout (`.sfs`) — new fields live in `.prec` sidecar only; `ExplicitEndUT` is already persisted.
- `FlightCamera` / watch-mode systems — no changes.
- In-flight multi-vessel recording via `BackgroundRecorder` — no changes.
- Thrust-segment icon-only rendering — no changes.
- Map-view renderer (`OrbitRenderer` / `GhostOrbitLinePatch` / `GhostMapPresence` line path) — no code changes in v1. It operates on the `OrbitSegments` list as before; we just grow the list.
- `OrbitArc` / `ExtrapolatedArcs` as separate types — not introduced. Extrapolator produces `OrbitSegment`s with `isPredicted = true`, stored in the existing list.
- `Recording.TerminalUT` as a new field — not introduced. `ExplicitEndUT` is the authoritative boundary.

## Backward Compatibility

- **Format bump v4 → v5** (binary codec in `TrajectorySidecarBinary`, text codec in ConfigNode path). Only change on disk is `OrbitSegment.isPredicted` per-segment. No top-level new Recording fields.
- Legacy v2/v3/v4 recordings load cleanly; missing `isPredicted` defaults to `false`; empty `OrbitSegments` means no extrapolated tail, so playback behaves identically to today.
- v5 recordings loaded by older Parsek builds: the safest path is a strict format-version rejection. If older builds must coexist, we can add a backward-compatible decode that treats the flag as absent — decide during Phase 1 based on how `CurrentRecordingFormatVersion` gates are checked today.

## Diagnostic Logging

Every decision point logs. All lines under `[Parsek][LEVEL][Subsystem]` format via `ParsekLog`. Subsystem tags used: `PatchedSnapshot`, `Extrapolator`, `Playback`, `MapRender`.

### Patched-conic snapshot (`[PatchedSnapshot]`)

- Scene-exit entry: `Info` — vessel name, situation, saved `maxGeometryPatches`, target capture limit.
- Solver null / unavailable: `Warn` — vessel name, reason (e.g., "on rails", "solver disposed"). Short-circuits to no-op.
- Solver.Update() exception: `Error` — exception type + message; confirmation that `maxGeometryPatches` was restored.
- Walk complete: `Verbose` — N patches captured, M patches truncated due to MANEUVER transition, terminal body of last captured patch.
- Short-circuit to `Destroyed`: `Info` — last patch's body, closest-approach altitude, target cutoff altitude, chosen `ExplicitEndUT`.

### Extrapolation guard (`[Extrapolator]`)

- `ShouldExtrapolate` decision: `Verbose` — situation enum value, orbit ecc/peri (if `ORBITING`), decision (true/false), one-word reason ("stable-orbit", "surface-held", "needs-atmo-cutoff", etc.).
- Start state selection: `Verbose` — "from patched-chain-end" or "from last recorded frame", UT, body, altitude.

### Propagator core (`[Extrapolator]`)

- Arc-start: `Verbose` — body, start UT, Kepler elements (sma, ecc, inc).
- Cutoff test: `Verbose` — atmoDepth / terrain altitude, periapsis, decision (intersects / escapes / stays).
- Arc-end (cutoff crossing): `Info` — terminal UT, altitude at crossing, terminal body.
- Arc-end (SOI escape): `Verbose` — SOI exit UT, new parent body, re-entry distance.
- Horizon-cap reached: `Warn` — years elapsed, SOI-transition count, final body, terminal state set to `Orbiting`.
- Degenerate state (zero-speed, parallel vectors, etc.): `Error` — inputs causing degeneracy; fallback to "no extrapolation" with terminal state unchanged.

### Terrain sampling (`[Extrapolator]`)

- Single PQS lookup: `Verbose` — body, lat, lon, altitude returned.
- PQS unavailable: `Warn` — body, reason; fallback to sea level.

### Playback (`[Playback]`)

- Ghost position resolves from a predicted/extrapolated `OrbitSegment`: `VerboseRateLimited` (one shared key per-recording) — recording id, segment body, segment index in list, UT.
- Ghost despawn at end-of-playback: `Info` — recording id, terminal state, `EndUT`, `ExplicitEndUT`, actual playback UT.

### Map-view rendering (`[MapRender]`)

- Per-ghost segment selection (v1): `VerboseRateLimited` (one shared key for the pass) — ghost id, selected segment's body, segment's `isPredicted` flag, segment UT range.
- SOI crossing during playback: `Info` — recording id, old body → new body, UT. Existing behaviour — triggers the existing renderer to switch to the new-SOI segment.
- (Future) Camera focus change: log line belongs to the future multi-segment renderer phase.

### Finalization summary

After scene-exit finalization, emit one `Info` line at `[Extrapolator]` summarising: recording id, patched-patches captured, extrapolation triggered (yes/no), terminal state, terminal UT, elapsed ms. One line per finalized recording — enables quick scan of KSP.log for missed / stuck cases.

## Test Plan

Every test includes a "what makes it fail" justification. Tests without a concrete regression they guard against do not ship.

### Unit tests (xUnit) — `Source/Parsek.Tests/BallisticExtrapolatorTests.cs`

**`ShouldExtrapolate` switch:**

- `Landed_ReturnsFalse` — fails if guard tries to extrapolate a landed rover, "launching" parked craft.
- `Splashed_ReturnsFalse` — fails if guard extrapolates a splashed craft, producing ballistic arcs from ocean surface.
- `Prelaunch_ReturnsFalse` — fails if guard extrapolates PRELAUNCH → ghost takes off from the pad on its own.
- `Docked_ReturnsFalse` — fails if guard extrapolates docked vessel, ignoring its parent vessel's physics.
- `Flying_ReturnsTrue` — fails if in-atmo planes get no extrapolation, regressing to the "ghost frozen mid-air" bug this plan solves.
- `SubOrbital_ReturnsTrue` — fails if above-atmo ballistic arcs get no extrapolation, regressing to mid-arc freeze.
- `Escaping_ReturnsTrue` — fails if hyperbolic arcs get no extrapolation, ghost stuck at ejection point.
- `OrbitingStable_ReturnsFalse` — fails if stable orbits unnecessarily get extrapolation work done.
- `OrbitingDecayingIntoAtmo_ReturnsTrue` — fails if orbit-intersects-atmo case is missed; ghost never gets `Destroyed`.

**Propagator correctness:**

- `Extrapolate_SuborbitalKerbin_TerminatesAtAtmoEntry` — fails on off-by-one at atmo boundary, sign errors in altitude, or if the cutoff is checked only on the ascending side.
- `Extrapolate_InAtmoPlaneArc_TerminatesAtGround` — fails if atmo-bounded arcs (apoapsis below `atmosphereDepth`) incorrectly wait for atmo-boundary cutoff that never fires.
- `Extrapolate_HyperbolicFromKerbin_HandsOffToKerbol` — fails if the old `ecc >= 1.0` bail survives, or if SOI handoff math leaves position/velocity in the wrong frame.
- `Extrapolate_StableOrbit_TerminatesAsOrbiting` — fails if stable orbits get labelled `Destroyed`, blocking spawn incorrectly.
- `Extrapolate_NeverImpacts_HitsHorizonCap` — fails if propagator loops forever on escape trajectories (no horizon guard).
- `Extrapolate_InterplanetaryJoolIntercept_ChainsToJoolAtmo` — fails if SOI chain stops after one transition, missing downstream encounters.
- `Extrapolate_NonAtmoBody_TerminatesAtGround` — fails if atmo cutoff is applied to non-atmo bodies; ghost never despawns.
- `Extrapolate_NonAtmoBody_PQSUnavailable_UsesSeaLevel` — fails if PQS-null causes NRE instead of sea-level fallback.
- `Extrapolate_TerrainAltitude_SingleLookup_LandingAtActualSurface` — fails if terrain sampling is skipped on non-atmo or if lookup uses wrong lat/lon frame.

**Limits and safety:**

- `Extrapolate_MaxSoiTransitionsReached_TerminatesAsOrbiting` — fails if we keep chaining past the soft cap, risking pathological cases.
- `Extrapolate_DegenerateStateVector_ReturnsFailureReason` — fails if zero-speed / parallel-vector inputs crash instead of returning a failure reason.

### Patched-conic snapshot tests — `Source/Parsek.Tests/PatchedConicSnapshotTests.cs`

Because `PatchedConicSolver` and `Orbit` are hard to mock, some tests wrap them behind a thin `IPatchedConicSource` interface; the rest run as in-game tests.

- `Snapshot_SingleOrbitVessel_CapturesOneSegment` — fails if the walk doesn't terminate when `nextPatch == null`.
- `Snapshot_FlybyPredicted_CapturesPreAndPostFlybyPatches` — fails if the walk stops at the first SOI transition, losing post-flyby predictions (the whole point of this capture).
- `Snapshot_StopsAtManeuverTransition` — fails if a `MANEUVER` patchEndTransition is included; ghost would then show a trajectory that assumes the unexecuted burn.
- `Snapshot_NullSolver_ReturnsEmptyList` — fails if on-rails or disposed-solver cases throw NRE.
- `Snapshot_RestoresPlayerLimit` — fails if `maxGeometryPatches` override leaks, degrading the player's map view.
- `Snapshot_RestoresLimitOnUpdateException` — fails if the `finally` block is missing; an exception during `Update()` would break the player's settings.
- `Snapshot_SolverImpactPredicted_TerminalStateIsDestroyed` — fails if we miss the solver's own predicted impact and run the extrapolator unnecessarily (correct result, wasted compute).
- `Snapshot_IsolatesPredictedFromTraversed` — fails if `isPredicted = true` is not set on captured segments, making them indistinguishable from traversed segments in later logic.

### Serialization tests — `Source/Parsek.Tests/RecordingStorageRoundTripTests.cs` (extend existing)

- `OrbitSegment_IsPredicted_RoundTrips_Binary` — fails if the new boolean field is silently dropped in the binary codec's segment encode/decode.
- `OrbitSegment_IsPredicted_RoundTrips_Text` — fails if the legacy ConfigNode path silently drops the flag.
- `ExplicitEndUT_AfterExtrapolation_IsPersisted` — fails if `ExplicitEndUT` is not persisted or gets recomputed on load from trajectory bounds (which would revert the extrapolated terminus).
- `LegacyV4Recording_LoadsWithIsPredictedFalse` — fails if old binary `.prec` files fail to load or produce invalid segments when the flag is absent.
- `V5Recording_RoundTripsThroughBinaryCodec` — fails if the bumped format version is not gated correctly in the encode / decode / version-check path.

### Log-assertion tests

At least one test per subsystem tag that captures log output via `ParsekLog.TestSinkForTesting` and asserts the expected line was produced. This both verifies behavior and ensures diagnostic coverage survives refactoring.

- `Extrapolator_Logs_StartState_OnEntry` — fails if the start-state verbose log disappears; diagnosing a misfire would require reading source.
- `PatchedSnapshot_Logs_PatchCount_AtFinalize` — fails if the summary log disappears; debugging "why is the chain empty?" loses its first stop.
- `Extrapolator_Logs_PQSUnavailable_Warning` — fails if terrain-fallback is silent; operators can't tell a sea-level landing from a terrain landing in the log.
- `Extrapolator_Logs_HorizonCap_Warning` — fails if horizon-cap hits are silent; can't detect runaway trajectories or set sensible defaults for the cap.

### In-game tests — `InGameTests/RuntimeTests.cs`

(Runnable via `Ctrl+Shift+T` in a live KSP session.)

- `ExtrapolationIntegration_PlaneExitMidFlight_GhostFallsAndDespawns` — fails if the full pipeline from scene exit to ghost despawn is broken in live KSP.
- `ExtrapolationIntegration_SuborbitalExitAtApoapsis_GhostFollowsArc` — fails if extrapolated arc playback doesn't drive the ghost's position in the live scene.
- `ExtrapolationIntegration_HyperbolicExit_GhostHandsOffToKerbol` — fails if patched-conics SOI handoff math diverges from stock KSP's frame conventions.
- `PatchedSnapshotIntegration_MunFlybyExit_GhostTrajectoryMatchesMapView` — fails if the ghost's post-finalization trajectory lines in map view differ visibly from what the player saw during flight.
- `PatchedSnapshotIntegration_ManeuverNodeStripped_GhostIgnoresBurn` — fails if a planned (but unexecuted) burn's delta-v contaminates the captured chain.
- `MapRendering_V1_GhostDrawsLineFromExtrapolatedSegment` — fails if the existing `OrbitRenderer` path doesn't select a predicted/extrapolated `OrbitSegment` the same way it selects a traversed one; without this the line would vanish at the recorded-flight end.
- `MapRendering_V1_LineContinuesPastRecordedEnd` — in-game; exit mid-flight, return, observe ghost's line persists past the recording's live-flight end and ends at the extrapolated terminus.
- `MapRendering_V1_ForeignSOISegmentsNotRendered` — fails if the existing single-`OrbitRenderer` accidentally draws multi-body segments simultaneously (v1 stays current-SOI-only; multi-segment chain display is Future).

Note: tests for camera-focus-driven rendering and ancestor-frame transforms belong to the Future renderer phase — not in v1 scope.

### Synthetic recordings — `Tests/Generators/`

- Truncated plane recording (ends mid-cruise at 3 km altitude on Kerbin, `vessel.situation = FLYING`).
- Truncated suborbital recording (ends near apoapsis above `atmosphereDepth` on Kerbin, `vessel.situation = SUB_ORBITAL`).
- Truncated hyperbolic recording (ends pre-SOI-exit on a Kerbin-escape trajectory, `vessel.situation = ESCAPING`).
- Truncated Mun-flyby recording (ends mid-Kerbin-SOI on a trajectory predicted by the solver to flyby Mun and return to Kerbin).
- Truncated Mun-impact recording (ends mid-arc on a ballistic arc that will hit the Mun surface, no atmosphere).

Each injectable via `dotnet test --filter InjectAllRecordings` and verified end-to-end via finalize + playback assertions.

## Locked Decisions (from design discussion 2026-04-20)

- **Q1 maneuver nodes:** walk `nextPatch` only up to (not including) first patch with `patchEndTransition == MANEUVER`. Chain is pure coast.
- **Q2 map/tracking presence for `Destroyed`:** same treatment as any ghost, vanishes at `terminalUT`. Targeting works until then.
- **Q3 RSW visibility:** no UI work. `TerminalState.Destroyed` already blocked by `ShouldSpawnAtRecordingEnd`.
- **Q4 deferred spawn queue:** no changes. Queue entry is gated by the same block.
- **Q5 crew semantics:** extrapolation never marks kerbals dead. Stock events drive reservation state.
- **Q6 mid-recording snapshot refresh:** scene-exit only; no refresh on SOI change / node edit during recording.
- **Q7 background-recorder integration:** n/a; no cross-scene system exists.
- **Q8 destruction visual FX:** silent disappear. Re-entry FX is deferrable polish.
- **Q9 `ShouldExtrapolate` guard:** switch on `vessel.situation`. No altitude/speed thresholds.
- **Q10 map-view rendering (v1):** existing single-`OrbitRenderer` architecture preserved. Data pool expanded to include predicted/extrapolated segments so the line continues past the recorded flight. Multi-segment chain display + ancestor-frame transforms (the focus+ancestry rule described in Core Principles) target a later phase with a new renderer — out of v1 scope.

## Open Questions (deferrable)

1. `maxHorizonYears = 50`, `PatchedConicSolverCaptureLimit = 8`, `maxSoiTransitions = 8`, `soiSampleStep = 3600 s` — initial defaults; calibrate from playtests.
2. Map-view styling for `isPredicted = true` segments — dashed / different colour / identical to traversed? Recommend identical until a readability problem surfaces.
3. Mid-recording refresh of patched snapshot on SOI change / node edit (edge case 26) — defer.
4. Background-recorder continuation across scenes (edge case 27) — defer; no such system today.
5. Atmospheric drag during extrapolation (edge case 28) — defer.
6. Re-entry FX at destruction (edge case 29) — defer.
7. Player-exposed sliders for calibration constants (edge case 30) — defer.

## Work Breakdown

Each phase ships as its own PR, testable independently.

1. **Patched-conic snapshot + `isPredicted` field + persistence.** Add `OrbitSegment.isPredicted`. Implement `SnapshotPatchedConicChain`. Bump `RecordingStore.CurrentRecordingFormatVersion` to v5. Update `TrajectorySidecarBinary` to encode/decode the new flag and version-gate the decode path. Update text ConfigNode path for legacy completeness. Binary + text round-trip tests. In-game test for Mun flyby. Lands first.
2. **`BallisticExtrapolator` module, pure-static, no integration.** `ShouldExtrapolate` switch, single-body Kepler propagator, SOI handoff, terrain sampling, horizon caps. Produces `List<OrbitSegment>` with `isPredicted = true`. Unit tests only. No hooks into `ParsekFlight` yet.
3. **Finalization integration.** Hook `FinalizeRecordingState(isSceneExit: true)` → `SnapshotPatchedConicChain` → `BallisticExtrapolator.Extrapolate`. Append extrapolator segments to `Recording.OrbitSegments`. Set `Recording.ExplicitEndUT = result.terminalUT`. Set `TerminalStateValue`. Log summary line per finalized recording. No new fields on `Recording`; `ExplicitEndUT` is the authoritative boundary downstream.
4. **Playback end-of-recording behaviour.** Verify the existing `EndUT` consumers (`GhostPlaybackEngine` end-of-playback, `TimelineBuilder`, watch protection, spawn timing, loop timing) pick up the extrapolated `EndUT` without code change. Add log assertion tests confirming each consumer sees the post-extrapolation `EndUT`.
5. **Ghost position during extrapolated interval.** Ensure the existing orbit-segment evaluation path (whatever `GhostPlaybackEngine` uses today to get position from an `OrbitSegment`) is exercised by the new `isPredicted = true` segments — they should just work since they're the same type. Add tests. Attitude: freeze last captured `srfRelRotation`.
6. **Map-view rendering v1 — pool extension only.** No renderer rewrite. Verify the existing single-`OrbitRenderer` path draws from the expanded `OrbitSegments` pool (predicted + extrapolated included) and continues the ghost's current-SOI line past the recorded flight. Past-segments hiding and coast-vs-thrust rules unchanged.
7. **In-game integration tests + synthetic recordings.** Mostly testing, no new production code.
8. **Calibration pass** after initial playtests — tune horizon years, capture limit, SOI-sample step if evidence warrants.

Phases 1–6 are v1 production; 7–8 are verification and tuning.

**Out of v1 scope (separate Future phase, not part of this plan):**

- Multi-segment chain display in map view.
- Camera-focus-driven frame selection with SOI-ancestry gating.
- Ancestor-frame transforms (Mun-flyby arc rendered in Kerbin frame etc.).

That phase introduces a new polyline-based renderer for ghost trajectories, replacing the single `OrbitRenderer` per ghost. Design deferred — Core Principles captures the target rule so v1 data layout does not block it.

No RSW, spawn-queue, or crew-reservation work required — the existing gate and stock events handle `Destroyed` correctly. No Recording-level `EndUT` threading required — `ExplicitEndUT` is the single source of truth.

## References

- Prior investigation findings (conversation 2026-04-20): current freeze-at-last-frame behaviour; confirmed zero references to `patchedConicSolver` / `nextPatch` / `maxGeometryPatches` in the codebase today.
- `ParsekFlight.cs:6788` — `FinalizeIndividualRecording`.
- `ParsekFlight.cs:6983` — `InferTerminalStateFromTrajectory`.
- `FlightRecorder.cs:2247-2261` — `CreateOrbitSegmentFromVessel` (current orbit-only capture; extended by this plan).
- `FlightRecorder.cs:4683-4698` — `FinalizeRecordingState` (scene-exit entry point for snapshot).
- `GhostPlaybackLogic.cs:3543-3668` — `ShouldSpawnAtRecordingEnd` (already handles `Destroyed`).
- `GhostPlaybackEngine.cs:1561-1597` — `PositionGhostAtRecordingEndpoint`.
- `GhostExtender.cs:96-128` — `PropagateOrbital` (current `ecc < 1.0` limitation that this plan supersedes).
- `RecordingEndpointResolver.cs:61-105` — orbit-endpoint resolution.
- `ParsekPlaybackPolicy.cs:140-270` — deferred spawn queue (not affected by this plan).
- `TerminalState.cs` — enum values.
- `docs/dev/development-workflow.md` — design-doc + plan/build/review workflow this document follows.
- `docs/dev/plans/rsw-departure-aware-spawn-warp.md` — prior plan on spawn-time state divergence; similar structure.
- KSP API: `PatchedConicSolver`, `Orbit.nextPatch`, `Orbit.patchEndTransition`, `Vessel.Situations`, `CelestialBody.TerrainAltitude`, `CelestialBody.pqsController`.
