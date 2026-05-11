# Trajectory frame overview — what we record vs. what we render

**Date:** 2026-05-11
**Branch:** `claude/investigate-trajectory-logic-OtzVx`
**Scope:** Recording side (Absolute vs Relative — and relative to what) ↔ playback side (which surface is used to position a ghost), with explicit answers for Re-Fly ghosts and debris ghosts.

This is an audit, not a plan. All claims carry `file:line` citations.

---

## 0. Quick reference

### 0.1 `ReferenceFrame` enum — `TrackSection.cs:34-39`

| Value | Storage | Semantics |
|---|---|---|
| `Absolute = 0` | `frames` populated, `absoluteFrames = null` | Body-fixed surface coords. `point.latitude/longitude` in degrees, `altitude` in metres. `point.rotation = srfRelRotation`. |
| `Relative = 1` | `frames` populated, `absoluteFrames` populated in v7+ | `latitude/longitude/altitude` are anchor-local Cartesian **metres** — NOT degrees. Rotation is anchor-local: `Inverse(anchor.rot) * focusWorldRot`. |
| `OrbitalCheckpoint = 2` | `checkpoints` (List&lt;OrbitSegment&gt;) populated | Patched-conic Keplerian elements. Reference body in `OrbitSegment.bodyName`. Effectively "absolute" in the patched-conic frame. |

### 0.2 Format versions that gate trajectory storage — `RecordingStore.cs:105-114`

```
v4  LaunchToLaunchLoopIntervalFormatVersion
v5  PredictedOrbitSegmentFormatVersion             — OrbitSegment.isPredicted
v6  RelativeLocalFrameFormatVersion                — Relative switches to anchor-local metres
v7  RelativeAbsoluteShadowFormatVersion            — Relative sections also carry absoluteFrames shadow
v8  BoundarySeamFlagFormatVersion
v9  TerrainGroundClearanceFormatVersion
v10 StructuralEventFlagFormatVersion
v11 RecordingAnchorChainFormatVersion              — TrackSection.anchorRecordingId (was anchorVesselId)
v12 DebrisParentRecordingFormatVersion = Current   — Recording.DebrisParentRecordingId
```

### 0.3 The render rule (the one-line model) — `docs/dev/plans/ghost-anchor-recording-chain-plan.md:42-52`

```
ghost_world(t) = anchor_recording_world(t) + anchor_local_offset(t)
```

- For an **Absolute** section the section's own data gives world coordinates directly.
- For a **Relative** section, walk the anchor chain (each anchor's pose is itself recorded data — never live pose, except in the loop carve-out).
- For an **OrbitalCheckpoint** section, Kepler-propagate.

The chain is a DAG, acyclic by construction (a recording can only anchor on recordings that already existed when its samples were taken).

---

## 1. Recording side — what gets written, in what frame, anchored to what

### 1.1 Two recorder roles

| Recorder | Scope | Writes |
|---|---|---|
| `FlightRecorder` | The focused vessel (and anything physically loaded) | Absolute / Relative TrackSections with per-frame `TrajectoryPoint`s; OrbitalCheckpoint sections on rails |
| `BackgroundRecorder` | All other tracked vessels (loaded near focus, or fully on-rails) | Same TrackSection emission for loaded BG vessels; **no TrackSections at all** for fully on-rails BG (`BackgroundOnRailsState`, `BackgroundRecorder.cs:192-200`) — only `Recording.OrbitSegments` / `Recording.SurfacePos` |

### 1.2 When the foreground recorder picks Absolute

`FlightRecorder.cs` decision points (citations from the recording-side agent):

| Entry | Trigger | Result |
|---|---|---|
| `FlightRecorder.cs:6091` | `StartRecording` initial section | Always `Absolute` at start |
| `FlightRecorder.cs:5540` | `onSurface && isRelativeMode` (we landed) | Close Relative → open `Absolute` |
| `FlightRecorder.cs:5645` | `!shouldBeRelative && isRelativeMode` (anchor proximity lost) | Close Relative → open `Absolute` |
| `FlightRecorder.cs:8102` | Scene exit / off-rails transition | Open `Absolute` |

Absolute is the **default**. Relative is opt-in based on proximity to an eligible anchor recording.

### 1.3 When the foreground recorder picks Relative — and to *what*

`FlightRecorder.cs:5592-5593` — entering/switching Relative mode when `shouldBeRelative && anchorChanged`:

```csharp
StartNewTrackSection(env, ReferenceFrame.Relative, boundaryUT);
ApplyCurrentRecordingAnchorToCurrentTrackSection();
//   → currentTrackSection.anchorRecordingId = currentAnchorRecordingId   (line 5453)
//   → currentTrackSection.anchorVesselId    = 0u                          (line 5454, always 0 in v11+)
```

The anchor is **another recording**, identified by its `RecordingId` string. The live vessel's persistent id is **not** stored in new v11+ Relative sections; `anchorVesselId` is held at zero. The anchor recording is selected from the active scope (tree-local + provisional overlay) by the recorder's eligibility ranking — see `ghost-anchor-recording-chain-plan.md` §3.3.

Per-frame, each Relative sample is converted from the live vessel pose into anchor-local metres:

`FlightRecorder.cs:7404-7433` (`ApplyRelativeOffsetForAnchorPose`):

```csharp
offset = TrajectoryMath.ComputeRelativeLocalOffset(
    focusWorldPos, anchorPose.WorldPos, anchorPose.WorldRotation);
// ...
point.latitude  = offset.x;   // anchor-local dx (metres)
point.longitude = offset.y;   // anchor-local dy (metres)
point.altitude  = offset.z;   // anchor-local dz (metres)
```

`TrajectoryMath.cs:1748-1757` (`ComputeRelativeLocalOffset`):

```
localOffset = Inverse(anchor.rotation) * (focusPos - anchorPos)
```

**The lat/lon/alt fields are reused as metre-scale Cartesian** in Relative sections. The naming is misleading — values commonly fall outside `[-90,90]` / `[-180,180]`. Any code path that reads these fields on a flat point list MUST first dispatch on `section.referenceFrame`. This is called out in `.claude/CLAUDE.md` and guarded by `EccentricOrbitOptimizerInvariantTests`.

### 1.4 The v7+ absolute shadow — recorded *in parallel* on every Relative sample

`FlightRecorder.cs:4881` initialises `absoluteFrames` for Relative sections only:

```csharp
absoluteFrames = refFrame == ReferenceFrame.Relative ? new List<TrajectoryPoint>() : null;
```

`FlightRecorder.cs:8200-8205` appends on each sample:

```csharp
if (currentTrackSection.referenceFrame == ReferenceFrame.Relative
    && absoluteShadowPoint.HasValue)
{
    currentTrackSection.absoluteFrames.Add(absoluteShadowPoint.Value);
}
```

Each shadow entry is a **full `TrajectoryPoint`** (rotation = `srfRelRotation`, velocity, body, altitude) — a body-fixed snapshot of where the focused vessel actually was in world space at that sample. Despite the field name, the shadow is not position-only. It's a complete Absolute-style point alongside the Relative one.

So for any v7+ Relative section there are **two rendering surfaces persisted on disk**:
- `section.frames` — anchor-local offsets (the "real" Relative data)
- `section.absoluteFrames` — body-fixed shadow (fallback / always-on for debris, see §3)

### 1.5 OrbitalCheckpoint — the rails-and-orbit surface

`FlightRecorder.cs:9009, 9067` — opened when a vessel goes on rails. Stores `OrbitSegment[]`, not `TrajectoryPoint[]`:

`OrbitSegment.cs:9-23` carries Keplerian elements + reference body name + `isPredicted` flag.

This is "absolute" in the sense that it's expressed in the patched-conic frame of `bodyName`; it never depends on another recording. `isPredicted = true` is stamped both by live solver snapshots (`PatchedConicSnapshot.cs:319`) and by tail extrapolation (`IncompleteBallisticSceneExitFinalizer.cs:890,1105`, `BallisticExtrapolator`).

### 1.6 BackgroundOnRailsState — no TrackSections at all

`BackgroundRecorder.cs:192-200` — the on-rails state object **omits** `currentTrackSection`, `trackSections`, and `environmentHysteresis`. `OnBackgroundPhysicsFrame` early-returns on `bgVessel.packed`.

Instead it writes:
- `Recording.OrbitSegments` for orbiting BG vessels (`BackgroundRecorder.cs:3233-3254`) — patched-conic absolute
- `Recording.SurfacePos` for landed BG vessels (`BackgroundRecorder.cs:3189-3217`) — body-fixed surface coords
- Nothing for atmosphere (`hasOpenOrbitSegment = false`, `ExplicitEndUT` updated)

Consequence (per `.claude/CLAUDE.md`): an eccentric on-rails BG orbit grazing the atmosphere across N orbits **cannot** generate Atmospheric/ExoBallistic TrackSection toggles. Optimizer-splittable env classifications come from per-frame sampling, which only runs in the loaded path.

### 1.7 Recorder summary table

| Path | Frame | Anchor (what "relative" means) |
|---|---|---|
| Foreground at start | Absolute | n/a |
| Foreground in proximity to eligible recording | Relative | Another `Recording` (by `anchorRecordingId`) |
| Foreground on landing / anchor loss | Absolute | n/a |
| Foreground on rails | OrbitalCheckpoint | n/a (patched-conic, body in `bodyName`) |
| Foreground scene exit tail (ballistic extrapolator) | Absolute (incl. OrbitalCheckpoint where applicable) | n/a — extrapolated tail is body-fixed only; never Relative |
| BG loaded vessel | Same Absolute/Relative logic as foreground | Another `Recording` |
| BG on-rails vessel | No TrackSection — `OrbitSegments`/`SurfacePos` only | n/a |
| **Debris (v12+)** | **Relative, permanently** (§3.1) | **The parent recording** (`DebrisParentRecordingId`) |
| Loop-anchored recording | Relative | A **live vessel PID** in `Recording.LoopAnchorVesselId` (the loop carve-out — see §4) |

---

## 2. Playback side — how each section type renders

### 2.1 The positioner interface

`IGhostPositioner.cs:28-101` defines what the ghost engine asks the host scene to do. ParsekFlight implements it for the flight scene; `GhostMapPresence` provides a separate pure-static resolver for map / tracking-station views (§2.5).

The methods the engine actually dispatches to, by section type:

| Section type | Engine call site | Positioner method |
|---|---|---|
| Absolute | `GhostPlaybackEngine.cs:1182-1189` | `InterpolateAndPosition` (`ParsekFlight.cs:16563-16634`) |
| Relative (general) | `GhostPlaybackEngine.cs:2721-2749` | `InterpolateAndPositionRelative` → `InterpolateAndPositionRecordedRelative` (`ParsekFlight.cs:16636-16684`, then `:22071-23362`) |
| Relative with shadow used (debris) | `GhostPlaybackEngine.cs:2966-2972` | `TryPositionFromRelativeAbsoluteShadow` (`ParsekFlight.cs:16717-16810`) |
| OrbitalCheckpoint | `GhostPlaybackEngine.cs:1182-1190` | `PositionFromOrbit` |
| Loop / overlap | `GhostPlaybackEngine.cs:3297-3339` | `PositionLoop`, with the same router branching first |

### 2.2 Section dispatch — `GhostPlaybackEngine.cs:1150-1215`

For each playback UT and each ghost:

1. `TrajectoryMath.FindTrackSectionForUT()` locates the active section (`GhostPlaybackEngine.cs:2671-2702`).
2. `TryRouteAnchorRotationUnreliable()` is called first (`:1150`, full body `:2796-2909`). This is the v12+ debris / tumble router (§3).
3. The router returns one of three values:
   - `ShadowPositioned` — ghost was placed via `absoluteFrames`. Engine skips legacy positioning.
   - `Hidden` — ghost was hidden (mesh deactivated). Engine skips post-position pipeline.
   - `None` — fall through to the normal positioner.
4. On `None`, the engine picks Absolute / Relative / Orbit based on the section, with `TryPositionRelativeSectionAtPlaybackUT()` handling Relative dispatch and falling back to `InterpolateAndPosition` (absolute) if relative returns false.

### 2.3 Relative resolution (the legacy / non-debris path)

`TryResolveRelativeOffsetWorldPosition` (`ParsekFlight.cs:20997-21080`) and its interpolator wrapper `TryResolveRelativeWorldPosition` (`:20935-20995`):

1. Look up the anchor's world pose at this UT. The lookup is delegated to `RelativeAnchorResolver.TryResolveAnchorPose` (`RelativeAnchorResolver.cs:118-374`).
2. Apply `TrajectoryMath.ResolveRelativePlaybackPosition(anchorWorldPos, anchorWorldRot, dx, dy, dz)` — which implements `worldPos = anchor.rotation * localOffset + anchor.position`.
3. Apply `anchor.rotation * localRot` for the ghost's world rotation.
4. On failure: fall through to absolute-shadow fallback (`TryUseRelativeAbsoluteShadowFallback`, `ParsekFlight.cs:22935-23014`), then to retire (`RetireUnresolvedRecordedRelative`, `:22159-22169` / `:22302-22311`).

The `RelativeAnchorResolver` is recursive: it asks the anchor recording's TrackSections to resolve *their* pose, descending the DAG until it hits an Absolute or OrbitalCheckpoint section.

### 2.4 The unreliable-anchor router (`TryRouteAnchorRotationUnreliable`)

`GhostPlaybackEngine.cs:2796-2909`. This is the v12+ debris / tumble-gate dispatch — the "shadow ladder":

| Tier | Condition | Return | What happens |
|---|---|---|---|
| 1 — `ShadowPositioned` | Section is Relative, `absoluteFrames` covers playback UT, positioner succeeded | `:2849-2854` | Mesh placed from shadow. FX-suppression flag set only when the gate fired. Log line `mode=gated\|always`. |
| 2 — `None` | Shadow not covering AND gate did NOT fire | `:2859-2860` | Legacy positioner runs. Mesh visible, FX normal. |
| 3 — `Hidden` | Shadow not covering AND gate DID fire | `:2862-2908` | Mesh hidden, FX torn down, exit-watch fires, `GhostRenderTrace.EmitGuardSkip` emitted. |

The "gate" is the angular-rate tumbling-parent classifier (PR #793). Post-PR #803 it no longer gates rendering — its result drives only FX suppression. See `docs/dev/plans/debris-always-shadow.md` for the failure-mode analysis that motivated demoting the gate (wide debris brackets, engage / release pops).

`mode=gated` vs `mode=always` in the log line:
- `gated` — real parent tumble, gate fired, FX suppressed
- `always` — steady-state shadow render, gate not firing, FX normal

### 2.5 Map view / tracking station

`GhostMapPresence.cs:5147-5272` — a separate pure-static resolver (`ResolveStateVectorWorldPositionPure`). It does NOT use `IGhostPositioner`. It branches on `referenceFrame` (`:5183-5262`) and, for Relative, calls `RecordedRelativeAnchorPoseResolver.TryResolveSectionAnchorPose()` then the same `TrajectoryMath.ResolveRelativePlaybackPosition` as flight. So the *math is identical*; the difference is that map view resolves one world point per state-vector entry instead of per-frame interpolation.

---

## 3. Debris ghosts — the deep-dive answer

### 3.1 What makes a recording "debris"?

`BackgroundRecorder.cs:1128-1130` — when a background split runs (joint break, separation):

```csharp
child.IsDebris = !hasController;
Recording.ApplyDebrisAnchorContract(child, parentRecordingId);
```

A recording is debris when the new vessel has **no controller part** (no command pod, no probe core). The contract immediately stamps `DebrisParentRecordingId` (`Recording.cs:891-903`) to the parent recording's id. This is permanent and travels in v12 saves; v11 saves omit it.

### 3.2 Recording-side: debris is always Relative-to-parent

`BackgroundRecorder.cs:4299-4302`:

```csharp
if (!string.IsNullOrEmpty(treeRec.DebrisParentRecordingId))
{
    ApplyDebrisAnchorContractToState(state, treeRec, ut);
    return;  // bypass all other anchor detection
}
```

Debris does NOT search for proximity anchors. It is anchored to its parent unconditionally. The recorder writes Relative sections (with v7+ `absoluteFrames` shadow alongside, §1.4) for the entire debris lifetime.

There is one fallback: if the parent recording cannot be resolved at the moment of debris initialisation (`BackgroundRecorder.cs:3338-3371`), the seed falls back to an **Absolute** opening section. This shows up in legacy v11 debris that lacks the v12 contract — see §3.5.

### 3.3 Recording-side: long-lived debris that orbits away

The recorder does NOT mutate a section's frame mid-section. When debris exits parent proximity range, the open Relative section closes and a new **Absolute** section opens. These are discrete sections — there is no frame switch inside a single section's `frames` list.

### 3.4 Playback v12+ — shadow is the source of truth, *unconditionally*

For `IsDebris && DebrisParentRecordingId != null` recordings, whenever the active Relative section has `absoluteFrames` covering the playback UT, the engine renders from the shadow. The legacy `anchor.rotation * localOffset` reconstruction is bypassed.

Why: parent tumble + lerp/slerp asymmetry + recorder-optimiser thin-tail debris brackets produce visible position pops at gate boundaries. See `docs/dev/plans/debris-always-shadow.md` §"Symptom recap" — 60-85 m per-frame deltas in a 2.2 s thin tail bracket, 80 m engage pops at gate fire.

Routing:
- `DebrisRelativePlaybackPolicy.cs:79-97` — `ShouldSkipRecordedRelativeResolverForAuthoredFrameGap` checks shadow coverage and routes through the shadow path.
- `GhostPlaybackEngine.cs:2966-2972` — `positioner.TryPositionFromRelativeAbsoluteShadow` actually places the ghost. Shared `InterpolateAndPosition` machinery, so floating-origin reapply / body lookup / GhostPosEntry semantics are reused.

### 3.5 Playback v11 (legacy debris, no `DebrisParentRecordingId`) — retroactive shadow gate

`LegacyDebrisShadowGate.cs:45-56` fires when ALL of:
1. `traj.IsDebris == true`
2. `traj.DebrisParentRecordingId == null` (this is what makes it "legacy")
3. `section.referenceFrame == Relative`
4. `section.absoluteFrames != null && count > 0`

When eligible, three retroactive gate sites in `ParsekFlight.cs` bypass the resolver and use shadow:
- `:22121-22130` — pre-range (resolver index < 0)
- `:22180-22189` — zero-duration segment
- `:22274-22283` — interpolated segment (mid-range), bypasses resolver before attempting it

Rationale (docs/code comments): legacy v11 debris was recorded with `anchorRecordingId` set to whatever vessel was nearest at sample time, not strictly the parent. The resolver "succeeds" with the wrong anchor; the shadow retroactively fixes broken saves without rewriting sidecars.

### 3.6 Playback pre-v6 / pre-v7 legacy debris

`.claude/CLAUDE.md`: "Legacy v5-and-older `ReferenceFrame.Relative` sections keep the older contract (no anchor-local offset, no absolute shadow) and must replay through the legacy path only; do not auto-reinterpret old RELATIVE payloads as v6/v7 anchor-local data." There is no shadow to fall back on for these; they replay through `RelativeAnchorResolver` only.

### 3.7 The tumbling-parent gate — what it still does

PR #793's angular-rate gate is still evaluated every frame. Post-PR #803 it no longer authorises rendering. Its output now drives only FX suppression (plumes / RCS / audio / reentry FX). The flag `state.anchorRotationShadowRoutedThisFrame` is set from the gate's per-frame fire bit when shadow is the rendering surface; it is false in steady-state always-shadow mode. The mesh comes from shadow as long as shadow covers, period.

### 3.8 Debris edge cases (the questions you'd actually ask)

| Scenario | Behaviour | Citation |
|---|---|---|
| Parent recording deleted | `DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss` returns true. Ghost is retired (mesh disappears). | `DebrisRelativePlaybackPolicy.cs:57-63` |
| Parent is itself a Re-Fly provisional | No special handling. Resolver looks up the anchor id at playback UT; once merged, it's a normal recording. | §4 |
| Debris in loop playback (parent loops) | Debris uses parent's loop clock (`parentLoopUT`). Visible only when `parentLoopUT ∈ [activationStartUT, traj.EndUT]`. Cycle changes rebuild the ghost; out-of-range debris is destroyed. | `GhostPlaybackEngine.cs:681-747` |
| Debris at scene exit (ballistic extrapolation) | Extrapolated tail is written in **Absolute** only — there is no Relative extrapolation. Recorder invariant: a Relative `TrackSection.endUT` must not outlive persistable authored coverage. | `.claude/CLAUDE.md`, `BallisticExtrapolator` |
| Debris that orbits away from parent | Relative section closes, new **Absolute** section opens. Discrete sections, never a mid-section frame mutation. | §3.3 |
| No `frames` coverage AND no `absoluteFrames` coverage at the playback UT | `ShouldRetireOutsideAuthoredRelativeCoverage` returns true; ghost is destroyed with reason "coverage-retired"; mesh disappears. If a later section opens with coverage, the ghost reappears. | `DebrisRelativePlaybackPolicy.cs:65-77, 191-199`; `GhostPlaybackEngine.cs:3199` |

---

## 4. Re-Fly ghosts — during and after

### 4.1 What's a Re-Fly ghost?

`ReFlySessionMarker.cs:21-83` is the singleton written by `RewindInvoker.AtomicMarkerWrite` to mark an active Re-Fly session. It carries:
- `SessionId` — unique per invocation/retry
- `ActiveReFlyRecordingId` — the **new** provisional recording the user is currently flying (NotCommitted)
- `OriginChildRecordingId` — the **old** recording being re-flown (the supersede target)
- `SupersedeTargetId` — the slot's effective recording id at invocation (detects chained Re-Flies)
- `RewindPointId`, `InvokedUT`, `InvokedRealTime`
- `InPlaceContinuation` — true when the same physical vessel is being re-flown (forks the recording)

A "Re-Fly ghost" is the **OLD** (`OriginChildRecordingId`) recording's ghost, played back while the user flies the NEW provisional. Other ghosts in the tree (the unrelated parts of the mission) also keep playing during the session.

### 4.2 During Re-Fly (between RP restore and merge commit)

Both recordings are active:

- **OLD recording** (`OriginChildRecordingId`) is played back as a ghost from its `StartUT` to `InvokedUT`. It renders **in whatever frame it was originally recorded in** — there is no frame translation applied. After Phase D, ghosts in an active Re-Fly tree play back at their **original recorded coordinates** regardless of where the player flies the live vessel. A divergent re-fly visually separates the player's live vessel from the ghost reference frame. See `docs/dev/plans/ghost-anchor-recording-chain-plan.md:14-15` and the "Downstream consequences" section.
- **NEW provisional** (`ActiveReFlyRecordingId`) is being recorded live by `FlightRecorder` in real time, using exactly the same Absolute/Relative decision logic as a normal recording. Frame is determined at runtime by anchor proximity, not by session type.
- In-place continuation inherits debris metadata: `RewindInvoker.cs:235-268` copies `IsDebris`, `DebrisParentRecordingId`, `VesselPersistentId`, `Generation` from the parent into the provisional.

There is no special render path for "Re-Fly ghosts" — they go through the same `IGhostPositioner` paths as any committed recording (`ParsekFlight.cs:2980, 3044-3082`). The distinction is entirely metadata: `MergeState`, `SupersedeTargetId`, `SegmentPhase`.

### 4.3 Post Re-Fly (after `SupersedeCommit`)

`MergeJournalOrchestrator.cs:216` invokes `SupersedeCommit.FlipMergeStateAndClearTransient`. Three things happen:

1. The Re-Fly marker is cleared (`scenario.ActiveReFlySessionMarker = null` at `:241`).
2. The provisional's `MergeState` is flipped:
   - Safe retry slots (crashed before landing/splashed) → `CommittedProvisional` (still retryable)
   - Closed / world-changing outcomes → `Immutable` (sealed)
3. The OLD (superseded) recording is excluded from ERS via `RecordingSupersedeRelation` rows (`AppendRelations` at `:202`). It is **no longer rendered** in flight playback. The OLD recording's ghost was only visible during the live Re-Fly session window.

The NEW recording becomes a normal recording from playback's perspective. The OLD recording's trajectory data stays on disk for ERS/ELS computation and any downstream debris that anchored on it, but it does not render.

### 4.4 Does Re-Fly involve any frame change?

**No.** It is purely a metadata/lifecycle event on top of normal trajectory frames.

- The OLD recording's frame contract is whatever it was at recording time (Absolute/Relative/OrbitalCheckpoint sections, mixed as usual).
- The NEW provisional records in the runtime-determined frame (proximity → Relative, otherwise Absolute, on rails → OrbitalCheckpoint).
- At merge, neither recording's TrackSections are rewritten. Frame is invariant under Re-Fly.

### 4.5 Re-Fly ghost rendering — concrete answer

During Re-Fly the OLD recording renders via:
- Absolute sections — `InterpolateAndPosition` (body-fixed lat/lon/alt)
- Relative sections — recursive anchor resolution via `RelativeAnchorResolver`, which walks **recorded** anchor data, never live state (except in the loop carve-out)
- OrbitalCheckpoint sections — Kepler propagation
- v12+ debris Relative — shadow path

The Re-Fly tree-anchor display offset (the per-frame `live_active(now) - recorded_active(t)` translation) that historically made ghosts visually follow the player has been retired in Phase D (`ghost-anchor-recording-chain-plan.md:26-27, 30-35`). Ghosts now play at original recorded coordinates; the player's live vessel and the ghost reference frame visibly separate on a divergent re-fly.

---

## 5. The loop carve-out (the one place playback uses live PIDs)

`Recording.LoopAnchorVesselId != 0u` marks a recording as loop-anchored to a designated live vessel (e.g. a station the looped craft formation-flies around). This is the **only** non-debris playback path that consults a live vessel pose.

- `RelativeAnchorResolver.cs:301-310` explicitly rejects loop-anchored recordings as anchor chain targets with `AnchorOutOfScope` ("loop-anchor-out-of-scope") — preventing live PIDs from leaking into a non-loop ghost's resolution.
- The loop-anchor lookup itself uses `TryResolveLiveLoopAnchorPose` (`ParsekFlight.cs:21070-21160`), reading `Vessel.transform.position` / `Vessel.transform.rotation` of the PID found via `FlightRecorder.FindVesselByPid`.
- The always-shadow path for v12+ debris is also gated to exclude loop-anchored recordings (`docs/dev/plans/debris-always-shadow.md` §"What this preserves").

Switching loop anchoring to recording-id is a separate future plan; v1 keeps it on live PIDs.

---

## 6. The end-to-end picture (single matrix)

For each section, here's what's recorded and what playback does with it:

| Section kind | Recorded values | Playback render path |
|---|---|---|
| Absolute | Body-fixed lat (°), lon (°), alt (m), srfRelRotation | `InterpolateAndPosition` → `body.GetWorldSurfacePosition(lat, lon, alt)` |
| Relative v6+ (non-debris) | Anchor-local dx, dy, dz (m) in lat/lon/alt fields; anchor-local rotation; `anchorRecordingId` | Resolver walks DAG to recorded anchor pose → `anchor.rot * offset + anchor.pos`; falls back to absolute shadow → retire |
| Relative v6+ (v12 debris) | Same as above PLUS `absoluteFrames` shadow (always present in v7+); `DebrisParentRecordingId` on the Recording | **Shadow path unconditionally** when shadow covers the UT; legacy resolver as fallback; retire if neither covers |
| Relative v11 legacy debris | Anchor-local + `absoluteFrames`, but `DebrisParentRecordingId == null` (and `anchorRecordingId` may point at a non-parent) | `LegacyDebrisShadowGate` fires when shadow available → bypasses resolver, uses shadow. Otherwise resolver. |
| Relative pre-v6 legacy | Old world-space offset contract; no shadow | Legacy resolver only. No reinterpretation. |
| OrbitalCheckpoint | `OrbitSegment[]` (Keplerian + body name + `isPredicted`) | `PositionFromOrbit` — patched-conic propagation |
| Loop-anchored Relative | Same as Relative, but `Recording.LoopAnchorVesselId != 0u` | Live-PID lookup of loop anchor's `Transform.position/rotation`; loop-only |
| On-rails BG (no TrackSection) | `Recording.OrbitSegments` / `Recording.SurfacePos` | Orbit-propagation / surface placement; rendered identically to OrbitalCheckpoint / Absolute |
| Ballistic extrapolated tail | Absolute (or OrbitalCheckpoint where appropriate) — never Relative | Same as Absolute / OrbitalCheckpoint |

---

## 7. Direct answers to the original questions

> **"For all general cases and edge cases, do we record absolute or relative trajectories?"**

Default is Absolute. The recorder switches to **Relative** for the foreground and loaded BG only when the vessel is within proximity to an eligible anchor recording. Debris recordings are recorded **Relative-to-parent unconditionally** for their entire lifetime (v12+). On-rails BG writes neither — it writes `OrbitSegments` / `SurfacePos` directly. Ballistic tail extrapolation is always Absolute.

> **"Relative to what?"**

Always to another **`Recording`**, identified by `anchorRecordingId` (v11+) or `DebrisParentRecordingId` (v12+ debris-only). Not to a live vessel, except in the loop-anchor carve-out, where `Recording.LoopAnchorVesselId` is a live PID. The live player vessel never enters non-loop ghost positioning math after Phase D.

> **"When we render, what do we use, for which segments?"**

- Absolute section → recorded lat/lon/alt + body lookup.
- Relative non-debris → recursive recorded-anchor chain via `RelativeAnchorResolver`; fall back to absolute shadow; retire if both miss.
- Relative v12+ debris → **absolute shadow unconditionally** when it covers; legacy resolver as fallback; retire if neither covers.
- Relative v11 legacy debris → `LegacyDebrisShadowGate` retroactively routes through shadow when available; resolver otherwise.
- OrbitalCheckpoint → Kepler propagation.
- Loop-anchored → live PID anchor (only place a live vessel touches the math).

> **"Re-fly ghosts (during Re-Fly and post Re-Fly)?"**

During Re-Fly the OLD recording renders via its original sections (whichever frame they were recorded in), through the same `IGhostPositioner` paths as any other recording. The NEW provisional is recorded live in the runtime-determined frame (Absolute/Relative by proximity). Post Re-Fly, after `SupersedeCommit`, the OLD recording is excluded by ERS supersede relations and stops rendering. **No frame translation, no frame rewrite — Re-Fly is metadata only.** Phase D removed the live-anchor display translation, so divergent re-flies now show the player and ghost reference frames visibly separating.

> **"Debris ghosts (relative or absolute or mix)?"**

Recording: **Relative-to-parent, permanently, for v12+ debris.** v7+ also writes an `absoluteFrames` shadow alongside every Relative sample (full TrajectoryPoint, body-fixed). Playback: **shadow is the source of truth for v12+ debris** — the legacy `anchor.rot * offset + anchor.pos` reconstruction was demoted because parent tumble and wide debris brackets produce visible position pops. Legacy v11 debris (no `DebrisParentRecordingId`) gets the same shadow treatment via the retroactive `LegacyDebrisShadowGate`. Pre-v6 legacy debris (no shadow) renders only through the legacy resolver.

The tumbling-parent gate still runs per frame but now drives only FX suppression, not rendering.

---

## 8. Notable invariants and footguns

1. **`point.latitude/longitude/altitude` semantics depend on `section.referenceFrame`.** Absolute → degrees+metres. Relative v6+ → metres on all three axes. Reading these from a flat `Recording.Points` list without dispatching on the active section's frame produces positions deep inside the planet for Relative data.
2. **`absoluteFrames` are full TrajectoryPoints**, not position-only. Rotation/velocity/body/altitude all populated.
3. **The optimiser's persistence predicate** treats `TrackSection.isBoundarySeam` as a hard "always wins" override. See `RecordingOptimizer.IsSplittableEnvOrBodyBoundary` and `optimizer-persistence-split.md`.
4. **On-rails BG vessels emit no env-classified TrackSections** — `BackgroundOnRailsState` omits the section list deliberately. Eccentric orbits grazing atmosphere across N orbits cannot produce optimiser-splittable Atmospheric/ExoBallistic toggles.
5. **Ballistic extrapolated tails are never Relative.** A Relative `TrackSection.endUT` must not outlive recorder-persistable authored coverage.
6. **Loop-anchored recordings are excluded from the anchor DAG.** `RelativeAnchorResolver` rejects them with `AnchorOutOfScope` to prevent live-PID leakage into non-loop ghost math.
7. **The Re-Fly live-anchor display translation has been removed.** Ghosts in an active Re-Fly play at their original recorded coordinates regardless of where the player flies. This is the intended product behaviour after Phase D; do not reintroduce a "follow the live vessel" anchor translation.
