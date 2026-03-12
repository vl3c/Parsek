# Known Bugs

## 1. Tech tree nodes stay unlocked after rewind
Root cause: `ActionReplay.ReplayCommittedActions` replayed ALL committed milestone events including those from after the rewind point, re-unlocking tech/parts/facilities that shouldn't exist yet. Fix: added `maxUT` parameter that skips events with `ut > rewindUT`. The rewind path passes `RecordingStore.RewindUT`; non-rewind callers use default `double.MaxValue`.

**Status:** Fixed

## 2. Craft orientation wrong on earlier recording playback
Root cause: rotation stored as world-space quaternion without accounting for planetary rotation between recording and playback time. Original fix: `CorrectForBodyRotation` helper computes angular delta from `body.angularVelocity` and `rotationPeriod`. Superseded by format v5: rotation now stored as surface-relative (`v.srfRelRotation`), reconstructed at playback via `body.bodyTransform.rotation * storedRot`. Eliminates time-delta correction entirely. v4 recordings still use `CorrectForBodyRotation` for backward compat (see bug #15).

**Status:** Fixed (v5 format), partially fixed for v4 (see #15)

## 3. Vessels fly erratically during playback
Root cause: ghost GameObjects not registered with KSP's `FloatingOrigin`. Positions set in `Update()` became stale after `FloatingOrigin.LateUpdate()` shifted all registered objects. Fix: store positioning parameters in `GhostPosEntry` structs, re-compute positions in `LateUpdate()` after FloatingOrigin shifts.

**Status:** Fixed

## 4. Green sphere instead of vessel model during playback
One recording appears as a green sphere during playback with slight time warp. Root cause: `ParsekScenario.UpdateRecordingsForTerminalEvent()` cleared `GhostVisualSnapshot` on vessel recovery, causing `GetGhostSnapshot()` to return null and triggering the sphere fallback. Fix: preserve `GhostVisualSnapshot` (immutable) — only clear `VesselSnapshot`.

**Status:** Fixed

## 5. Atmospheric heating trails look wrong
Re-entry heating effects appeared as orange square sprites. Root cause: particle materials created without a texture — Unity renders textureless particles as solid squares. Fix: extract particle texture from stock KSP FX prefab and assign to flame, smoke, and trail materials with proper `_TintColor`.

**Status:** Fixed

## 6. Loop checkboxes not centered in UI cells
Merged `ColW_LoopLabel` + `ColW_LoopToggle` into single `ColW_Loop` column, wrapped toggle in horizontal group with `FlexibleSpace` on both sides.

**Status:** Fixed

## 7. Rewind button inactive for most recordings
Root cause: `StartRecording()` called `FlightRecorder.StartRecording()` with default `isPromotion=false` for chain continuations, creating rewind saves for every segment. Fix: detect continuations via `activeChainId != null` and pass `isPromotion=true` to skip rewind save capture.

**Status:** Fixed

## 8. Exo-atmospheric segment incorrectly has rewind button active
Same root cause as #7 — atmosphere boundary splits created continuation segments with their own rewind saves. Fixed by the same `isPromotion: isContinuation` change.

**Status:** Fixed

## 9. Watch camera does not follow recording segment transitions
Added `FindNextWatchTarget` (chain continuation + tree branching) and `TransferWatchToNextSegment` to auto-follow the camera to the next active ghost when a watched segment ends. Preserves saved camera state for Backspace restore.

**Status:** Fixed

## 10. Ghost wobbles at large distances from Kerbin
Root cause: `GetWorldSurfacePosition` returns `Vector3d` but was truncated to `Vector3` (float) before interpolation. Fix: use `Vector3d` and `Vector3d.Lerp` throughout, only truncating at the final `transform.position` assignment.

**Status:** Fixed

## 11. Verify game actions are recorded and reapplied correctly
Audit of KSP log (2026-03-09) confirms all game actions properly captured: 9 tech unlocks, 54+ part purchases, 10 contract offers — all in 6 milestones. Resource events correctly captured and suppressed during timeline replay. No gaps, no errors.

**Status:** Verified — no issues found

## 12. Vessel destruction during chain recording orphans final segment

When a vessel is destroyed during recording as part of a chain (e.g., crash after re-entry), the final segment is committed as a standalone recording instead of chain index N. The chain context (chainId, chainIndex) is lost during the destruction commit path. Result: the landing/crash segment appears separately in the UI and is disconnected from the chain — user can't navigate to launch via the chain, and rewind only covers the chain segments.

**Reproduction:** Default career → launch R2 → let it fly out of atmosphere and crash on re-entry. The atmo and exo segments form a chain, but the final crash segment is standalone.

**Status:** Fixed — `FallbackCommitSplitRecorder` now preserves chain metadata (ChainId, ChainIndex, phase)

## 13. Rewind button (R) shown for wrong recordings

Some chain recordings that start with a launch don't have the R (rewind) button enabled, while some that don't start with a launch do. Only recordings that begin with a launch should have the rewind button available, since rewind loads the quicksave captured at recording start.

**Root cause:** `StopRecordingForChainBoundary` copies `RewindSaveFileName` into `CaptureAtStop` then nulls `RewindSaveFileName`. When `ResumeAfterFalseAlarm` fires (atmosphere boundary false alarm), it clears `CaptureAtStop` without restoring the rewind save — so the rewind save is permanently lost for that recording chain.

**Fix:** In `ResumeAfterFalseAlarm`, restore `RewindSaveFileName` (and reserved funds/science/rep) from `CaptureAtStop` before clearing it. Only restore if `RewindSaveFileName` is currently empty (to avoid overwriting a legitimate rewind save).

**Status:** Fixed

## 14. Synthetic recording rotation constants are world-space, not surface-relative (v5)

After bumping to format v5 (surface-relative rotation), the synthetic test recordings still use the old KscRot constants `(0.33, -0.63, -0.63, -0.33)` which were world-space rotation values. These are now interpreted as surface-relative, producing incorrect ghost orientation. Need to capture the actual `v.srfRelRotation` from KSP for an upright vessel at KSC and update the constants.

**Status:** Fixed — constants updated to `(-0.7009714, -0.09230039, -0.09728389, 0.7004681)` captured from KSP runtime

## 15. CorrectForBodyRotation still produces visible drift for v4 recordings with large UT deltas

The old v4 body rotation correction (`CorrectForBodyRotation`) accumulates floating-point error when `deltaUT` is large (thousands of seconds). Real recordings from the default career played in the test career have ~18000s delta, producing ~315° correction with visible orientation error. v5 recordings eliminate this, but existing v4 recordings (including the 20 real career recordings added to the injector) still use the old path.

**Status:** Fixed — `RecordingStore.MigrateV4ToV5` auto-converts v4 recordings to v5 at flight-scene load using runtime body rotation data. Migration uses modular angle arithmetic to avoid float drift, saves updated .prec file permanently.

## 16. Orbital recording fidelity during time warp vs real-time

Ghost orientation was wrong during orbital (on-rails) segments — all vessels appeared prograde-locked regardless of their actual attitude.

**Root cause:** Playback derived ghost rotation from velocity direction (`Quaternion.LookRotation(velocity)`) without storing the vessel's actual attitude at the on-rails boundary.

**Fix:** Orbital segments now store the vessel's rotation relative to the orbital velocity frame (prograde/radial/normal) as 4 optional ConfigNode keys (`ofrX/Y/Z/W`). At playback, the orbital frame is reconstructed from the Keplerian orbit at the target UT and the stored offset is applied. A vessel holding retrograde gets a 180-degree yaw offset that persists around the entire orbit.

Additionally, when the PersistentRotation mod is detected at recording time and the vessel is spinning (angular velocity > 0.05 rad/s), the vessel-local angular velocity is stored (`avX/Y/Z`) and the ghost is spun forward during playback, matching what the player saw with PersistentRotation active.

No format version bump — missing keys default to prograde fallback (backward compatible). Old Parsek versions ignore unknown keys (forward compatible).

See `docs/dev/done/design-orbital-rotation.md` for full design.

**Status:** Fixed

## 17. Re-entry flame effects too large and pointing wrong direction

Ghost re-entry heating FX appear as oversized square arrangements (6 spaced-out flames) and the flames point opposite to the movement direction (trailing behind instead of leading ahead). Two separate issues:
1. **Scale/pattern:** Each flame particle system renders as a grid/square pattern instead of a smooth heating glow — many individual flame sprites arranged in a grid-like formation
2. **Direction:** Flame direction vector is inverted relative to the velocity vector — should face into the airstream (prograde) but instead faces retrograde

**Status:** Open — needs investigation of `ReentryFx` particle placement and velocity alignment in ParsekFlight.cs

## 18. Engine nozzle glow persists after engine cutoff

Ghost engine nozzle continues glowing after the engine shutdown event during playback. The `EngineShutdown` part event is recorded and present in the trajectory, but the ghost engine FX particle system is not stopped/cleared when the shutdown event is applied.

**Reproduction:** Record a flight with booster separation (engine burns out → decouples). Watch the ghost playback — the booster nozzle continues glowing even after the engine cutoff event fires.

**Root cause:** Two issues: (1) `EngineShutdown` stopped the exhaust particle FX but did not reset the heat animation emissive glow (`ModuleAnimateHeat` material properties) — the nozzle mesh stayed emissive. (2) If `EngineShutdown` was not recorded before a `Decoupled` event (same-frame burnout+decouple race), engine/RCS particle systems were not explicitly stopped before the part was hidden.

**Fix:** `EngineShutdown` now also calls `ApplyHeatState(heated: false)` to reset nozzle emissive materials. `Decoupled` and `Destroyed` events now defensively call `StopEngineFxForPart` and `StopRcsFxForPart` (plus heat reset) before hiding the part, ensuring no orphaned FX regardless of event ordering.

**Status:** Fixed

## 19. Watch (W) button does not work for looped recording segments

Pressing W to watch a looped recording segment does nothing — the camera does not move to the ghost vessel. This affects all looped segments; non-looped segments work correctly.

**Reproduction:** Enable loop on any recording segment, press W on that segment in the UI. Camera stays on the active vessel instead of switching to the ghost.

**Root cause:** In `UpdateLoopingTimelinePlayback`, the pause window path called `PositionGhostAt` but never called `SetInterpolated`, leaving `lastInterpolatedBodyName` null on the `GhostPlaybackState`. When the ghost was respawned at a cycle boundary and the first frame fell in the pause window, the body name stayed null for the entire pause duration. `IsGhostOnSameBody` returned false → W button was disabled (grayed out). Even outside the pause window, a freshly spawned ghost had null body name for the first frame.

**Fix:** The pause window path now initializes `lastInterpolatedBodyName` and `lastInterpolatedAltitude` from the last trajectory point when they are empty. This ensures `IsGhostOnSameBody` returns true as soon as the ghost exists.

**Status:** Fixed

## 20. Ghost orientation wrong in exo-atmospheric segment after staging

In the second segment of a chain recording (e.g., Kerbin exo after atmosphere exit), the ghost vessel orientation is incorrect after a staging event (booster separation). The ghost appears to have wrong rotation after the decouple event.

**Reproduction:** Record a full flight with staging (R2 default career). Watch the exo-atmospheric chain segment — after the booster separates, the remaining vessel orientation is visibly wrong.

**Status:** Open — needs investigation of rotation handling around decouple events in the playback path

## 21. Ghost build warning spam for snapshot-less recordings

Recordings without a ghost visual snapshot (e.g., "KSC Pad Destroyed" synthetic recording) trigger `Ghost build aborted: no snapshot node` every frame during playback. In one session this produced 60+ identical WARN entries over 4 minutes, filling the log.

**Root cause:** `GhostVisualBuilder.TryBuild()` is called each playback update for active recordings. When snapshot is null it logs WARN and returns, but there's no cooldown or flag to suppress repeated attempts.

**Fix options:** (1) Set a `ghostBuildAttempted` flag on the recording after first failure to skip subsequent attempts, (2) only log once per recording per flight scene, (3) fall back to sphere silently without WARN for recordings known to lack snapshots.

**Status:** Fixed — `SpawnTimelineGhost` now checks `GetGhostSnapshot(rec)` before calling `BuildTimelineGhostFromSnapshot`. When null, skips straight to sphere fallback without the snapshot build attempt. Also downgraded the "no snapshot node" and "no PART nodes" messages from WARN to INFO for cases where the build is attempted directly.

## 22. Facility not found during action replay on rewind

During rewind, action replay logs `Facility upgrade: 'SpaceCenter/LaunchPad' — facility not found, skipping`. The facility upgrade is silently skipped, potentially leaving game state inconsistent.

**Root cause:** `UpgradeableFacility` MonoBehaviours only exist in SpaceCenter scene. In Flight scene (after rewind/quicksave load), `ScenarioUpgradeableFacilities.protoUpgradeables` entries have empty `facilityRefs` lists. This is expected — the quicksave already contains the correct facility level data from save time, so the skip is benign.

**Reproduction:** Rewind to a point before a launchpad upgrade milestone. Check log for "facility not found" warning.

**Status:** Fixed — downgraded from WARN to INFO with explanatory message ("expected in Flight scene where facility refs are unavailable"). The facility level data from the quicksave is authoritative; the action replay skip is harmless.

## 23. Ghost geometry log noise and orphaned .pcrf files

Original report: "Real career recordings missing ghost geometry (sphere fallback)". Investigation revealed the ghost visuals work correctly via `_ghost.craft` snapshots for all real career recordings — the `.pcrf` system was stub-only plumbing that was never completed.

Three issues found:
1. **Misleading log message**: `ParsekScenario` logged `(ghost geometry: fallback)` for all recordings with `.pcrf` stubs, making it sound like ghost visuals were broken. The `GhostGeometryAvailable` field was always `false` since `GhostGeometryCapture.CaptureStub()` only wrote metadata stubs.
2. **Orphaned `.pcrf` files**: 19+ stub files left on disk for recording IDs that no longer existed in save files, created by `CaptureStub` for recordings later deleted.
3. **Dead `.pcrf` stub system**: `GhostGeometryCapture` wrote stub-only `.pcrf` files never consumed by any code. Ghost visuals are built entirely from `_ghost.craft` snapshots via `GhostVisualBuilder.BuildTimelineGhostFromSnapshot()`.

The only actual sphere fallback was "KSC Pad Destroyed" — a synthetic recording intentionally created without a vessel snapshot.

**Fix:** Deleted `GhostGeometryCapture.cs`. Stopped writing ghost geometry fields (`ghostGeometryVersion`, `ghostGeometryStrategy`, `ghostGeometryProbeStatus`, `ghostGeometryPath`, `ghostGeometryAvailable`, `ghostGeometryError`) in save serialization. Kept deserialization for backward compat with existing saves. Removed `CaptureStub` call from `VesselSpawner.SnapshotVessel`. Removed misleading log message. Added `RecordingStore.CleanOrphanFiles()` to scan `Parsek/Recordings/` on load and delete sidecar files for recording IDs not in the save.

**Status:** Fixed

## 24. Part variant renderer fallback on ghost builds

During ghost visual builds, some parts log `Variant active-state fallback: no active variant renderers found`. The ghost part renders but may show incorrect variant appearance (e.g., wrong texture/color for parts with multiple visual variants like fuel tanks).

**Root cause:** `GhostVisualBuilder` attempts to match the recorded part variant by enabling/disabling variant-specific renderers. When no renderer is tagged as active for the recorded variant, all renderers fall back to their default state. However, when the variant has GAMEOBJECT rules in the part config, those rules still filter renderers correctly — the fallback warning was firing even when GAMEOBJECT rule filtering was working.

**Status:** Fixed — variant fallback warning now only fires when BOTH active-state filtering AND GAMEOBJECT rules are unavailable (the true fallback case). When GAMEOBJECT rules exist, variant filtering works correctly and no warning is logged.

## 25. Flaky test: CommittedActionTests.GetCommittedTechIds_MultipleMilestones

`CommittedActionTests.GetCommittedTechIds_MultipleMilestones` intermittently fails when run as part of the full test suite but passes in isolation. Likely a shared static state issue — the test depends on `MilestoneStore` or `RecordingStore` state that another test in the same suite leaves dirty.

**Reproduction:** `dotnet test` (full suite) — fails ~50% of runs. `dotnet test --filter GetCommittedTechIds_MultipleMilestones` — always passes.

**Status:** Open — needs investigation of shared state cleanup between tests
