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

**Fix:** Completely reworked across multiple PRs: square particle sprites replaced with smooth fire streak trails (#30), then replaced again with mesh-surface fire particles matching the stock KSP `aeroFX` intensity formula from `Physics.cfg` (#32). Fire shell overlay added and reentry meshes rebuilt on decouple events. Direction and scale issues resolved.

**Status:** Fixed

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

**Fix:** Resolved by the v5 surface-relative rotation overhaul (`9775e8f`) and the orbital-frame rotation work (`c0a8ced`, `8c869a4`). Rotation is now stored as `srfRelRotation` (surface-relative) and reconstructed at playback via `bodyTransform.rotation * storedRot`. Old v4 recordings auto-migrated to v5 at flight-scene load. The rotation system was completely rewritten after this bug was filed — ghost rotation is correct regardless of staging/decouple events.

**Status:** Fixed

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

**Root cause:** `ComputeStatsTests` and `SyntheticRecordingTests` called `MilestoneStore.ResetForTesting()` but were missing `[Collection("Sequential")]`. xUnit runs test classes without a collection attribute in parallel. When these classes ran in parallel with `CommittedActionTests`, they wiped the `MilestoneStore` mid-test.

**Fix:** Added `[Collection("Sequential")]` to `ComputeStatsTests` and `SyntheticRecordingTests`. Verified 5 consecutive full-suite runs with 0 failures.

**Status:** Fixed

## 26. EVA crew swap fails after merging from KSC

When `autoMerge` is off and an EVA recording is merged via the dialog in KSC (not Flight), the crew reservation (Valentina → Agasel) is created correctly, but on revert `SwapReservedCrewInFlight` finds 0 matches on the active vessel. The reserved crew member (Valentina) is not in the active vessel's part crew list after revert, causing a duplicate kerbal on spawn.

**Reproduction:** Career mode → disable auto-merge → EVA Valentina from pad → walk around → go to KSC → merge dialog appears → click Merge → revert to launch → ghost shows different kerbal walking, but Valentina also spawns at the end = 2 Valentinas.

**Root cause:** Two issues:
1. The rewind is on the parent vessel recording, so `PreProcessRewindSave` strips the rocket's name — not the EVA kerbal's. The EVA vessel survives the strip.
2. `SwapReservedCrewInFlight` only iterates `ActiveVessel.parts` crew. EVA kerbals are separate vessels, so the swap finds no match.

**Status:** Fixed — two-layer fix:
1. `PreProcessRewindSave` now also strips EVA child recording vessels from the rewind save (root cause)
2. `SwapReservedCrewInFlight` removes reserved EVA vessels as defense-in-depth

## 27. F9 quickload can silently overwrite a pending recording

If the player has an unresolved pending recording (merge dialog not yet shown/clicked) AND an active recording in progress, pressing F9 quickload causes `OnSceneChangeRequested` to stash the active recording as a new pending — overwriting the old pending silently. The old recording's data and crew reservations are lost.

**Reproduction:** Requires both an active recording and an unresolved pending simultaneously — very rare in practice. Could theoretically happen if: record flight A → go to KSC (pending A) → launch new vessel → record flight B → F9 quickload (pending A overwritten by pending B).

**Root cause:** `RecordingStore.StashPending` overwrites the existing `pendingRecording` static field without checking if one already exists. No warning logged. Crew reservations from the old pending are leaked (kerbals stuck in Assigned status).

**Fix:** Added a guard at the top of `StashPending`: if a pending recording already exists, unreserve its crew via `UnreserveCrewInSnapshot` and call `DiscardPending()` (which cleans up sidecar files) before creating the new pending. Logs a WARN with both vessel names.

**Status:** Fixed

## 28. Building collision does not set TerminalState.Destroyed

When a vessel crashes into a KSC building (VAB, launchpad tower, etc.), the recording's `TerminalStateValue` is left as `null` instead of being set to `Destroyed`. Ghost playback shows the vessel flying into the building and disappearing without an explosion — both in flight scene and KSC view.

**Reproduction:** Launch a rocket, steer it into the VAB or a launchpad structure. Commit the recording. Watch ghost playback — no explosion at the end despite the vessel being destroyed.

**Root cause:** The destruction detection path that sets `TerminalStateValue = TerminalState.Destroyed` doesn't fire for building collisions. The vessel is destroyed by KSP's building collision system, but the recording commit path may not reach the code that sets the terminal state.

**Observed in:** KSP.log from KSC ghost testing (2026-03-14). Recordings with `terminal=` (null) despite vessels being destroyed by building collisions.

**Root cause (confirmed):** Race condition in `OnSceneChangeRequested`. When the vessel is destroyed and a scene change fires in the same frame (building collision destroying the only vessel), `ShowPostDestructionMergeDialog` (yields 1 frame) is killed by the scene change before it can set `TerminalState.Destroyed`. The `OnSceneChangeRequested` fallback path sets terminal state from `FlightGlobals.ActiveVessel.situation`, but `ActiveVessel` is null (destroyed). A secondary gap: if ActiveVessel switched to debris with `LANDED` situation, the terminal state would be `Landed` instead of `Destroyed`.

**Fix:** Extracted `ApplyDestroyedFallback` — after the situation-based terminal state inference in `OnSceneChangeRequested`, checks `wasDestroyed` flag and overrides any non-Destroyed terminal state. Covers both null (ActiveVessel gone) and wrong-situation (ActiveVessel is debris) cases.

**Status:** Fixed

## 29. Ghost parts missing or in wrong visual state during playback

Some vessel parts are missing or display incorrectly during ghost playback (both flight and KSC view). Known cases:
- Rover wheels (`roverWheel1` etc.) not visible on ghost
- Landing gear (`SmallGearBay`) showing incorrect deploy state — may appear stowed when they should be deployed or vice versa
- Deployable parts (solar panels, antennas) potentially showing wrong initial state

**Reproduction:** Record a vessel with rover wheels or landing gear. Watch the ghost playback — wheels may be missing entirely, gear may appear in wrong position.

**Root cause (suspected):** Multiple potential causes:
1. **Missing prefab resolution:** `GhostVisualBuilder.AddPartVisuals` clones meshes from the part prefab. Some parts (rover wheels, robotic parts) may have complex model hierarchies or use SkinnedMeshRenderer with external bones that the cloning process doesn't handle correctly.
2. **Snapshot initial state mismatch:** The ghost snapshot captures part MODULE state at recording start. If gear/wheels are in a transitional animation state when captured, the ghost starts with that intermediate visual. Part events then replay on top of the wrong initial state.
3. **Animation sampling gaps:** `SampleDeployableStates` samples animation at t=0 (stowed) and t=1 (deployed). Parts with non-standard animation setups or parts that use multiple animation clips may not be sampled correctly.

**Observed in:** KSC ghost testing (2026-03-14). Visible on multiple vessel types.

**Status:** Open

## 30. All RCS thrusters fire constantly during ghost playback

During ghost playback, all RCS thrusters on the vessel fire at full power continuously, even when the original vessel was only making small SAS attitude corrections. The visual result is every RCS block showing full exhaust plumes at all times, which looks unrealistic and distracting — especially on vessels with many RCS blocks (e.g., 8-10 thrusters all lit up simultaneously).

**Root cause:** The recording system polls `rcs.rcs_active && rcs.rcsEnabled` every physics frame (`FlightRecorder.CheckRcsState`, line 2387). KSP's SAS system makes constant micro-corrections, briefly activating individual RCS thrusters for 1-3 frames at a time. This produces a rapid stream of `RCSActivated` → `RCSStopped` events (potentially dozens per second across all thrusters). During playback, these rapid fire/stop cycles blend together visually into what appears to be continuous full-power firing on all thrusters.

Additionally, `ComputeRcsPower` normalizes thrust across all nozzles (`sum / (thrusterPower * count)`), which can report full power even when only a subset of nozzles are firing for a micro-correction. The 0.01 throttle-change deadband in `CheckRcsTransition` doesn't filter out the rapid on/off cycling.

**Desired behavior:** Ghost RCS should only show visually significant thrust events — sustained translation burns or large rotation corrections that the player intentionally commanded. Brief SAS micro-corrections should be filtered out or aggregated.

**Possible fixes:**
1. **Minimum duration filter:** Only emit `RCSActivated` if the thruster stays active for N consecutive physics frames (e.g., 5+ frames ≈ 0.1s). Would eliminate SAS micro-correction noise.
2. **Per-nozzle recording:** Record individual nozzle thrust values instead of aggregate power, so playback can show which specific nozzles fired and in which direction.
3. **Hysteresis:** Require RCS to be inactive for N frames before emitting `RCSStopped`, preventing rapid on/off cycling.

**Observed in:** Sandbox career (2026-03-14). Visible on vessels with RCS blocks: ghost #9 (rcs=8), ghost #5 (rcs=8), ghost #10 (rcs=3), ghost #12 (rcs=10). No RCS events were recorded in this session (playback-only), but the FX build chain created particle systems for all RCS modules.

**Status:** Open

## 31. Engine shroud/cover not rendered correctly for some engines

Some engines display their protective shroud/cover incorrectly during ghost playback. The shroud may appear missing, partially rendered, or in the wrong variant configuration. This affects engines that have multiple shroud variants (different sizes for different tank diameters) or engines with complex jettison transform hierarchies.

**Root cause:** `GhostVisualBuilder.AddPartVisuals` resolves jettison transforms by looking up `ModuleJettison.jettisonName` in the clone map. However, engines with part variants (e.g., Mainsail with `fairing`/`fairingSmall`, Skipper with `fairing`/`fairing2`, KE-1 "Vector" with `Shroud2x3`/`Shroud2x4`) have multiple shroud meshes on the prefab but only the active variant's mesh is included in the clone. When the jettison lookup finds the prefab transform but not the clone, it logs `"Jettison 'X' found on prefab but not in cloneMap"` and skips it.

The active variant's shroud is detected and tracked correctly via the `cloneMap` hit path. The 275 "not in cloneMap" messages in the log (2026-03-14 session) are for non-active variant shrouds and are expected. However, the actual rendering issue may occur when:
1. The GAMEOBJECT variant rules hide/show shroud transforms in a way that doesn't match what the ghost clone captured
2. The shroud mesh scale or position differs between the prefab default and the recorded variant

**Observed in:** KSC ghost testing (2026-03-14). Affected engine parts include `LiquidEngineKE-1` (72+60 variant misses), `engineLargeSkipper.v2` (25 variant misses), `LiquidEngineLV-T91`/`LiquidEngineLV-TX87` (18 variant misses).

**Status:** Open — needs in-game visual verification to identify which specific engines show incorrect shrouds

## 32. Launch Escape System (LES) plume effects need verification

The Launch Escape System (`LaunchEscapeSystem` part) has 5 `thrustTransform` nozzles with `Squad/FX/LES_Thruster` particle effects. The ghost build chain correctly creates particle systems for all 5 nozzles, and playback events fire in the correct sequence (`Decoupled` → `EngineIgnited` → `EngineShutdown`). However, the LES uses a specialized SRB-style plume effect that may not match the stock visual appearance.

**Needs verification:**
- Are the LES plume particle systems using the correct effect group? The ghost FX builder clones from `MODEL_MULTI_PARTICLE` configs in the EFFECTS node — verify this matches the LES thruster visual.
- The LES has a unique exhaust pattern (5 angled nozzles in a ring). Verify the particle system positions/rotations match the actual nozzle geometry on the ghost model.
- Compare ghost LES firing visual to stock LES firing in-game — check plume color, size, and direction.

**Observed in:** Log analysis (2026-03-14). Ghost build succeeds with 6 MeshRenderers and 5 thruster FX systems. Engine FX `playing=False` on initial ignition frame was observed for LES (90 occurrences) — the particle system may not visually start until the second frame after `EngineIgnited`.

**Status:** Open — needs in-game visual comparison

## 33. Crash sequence: vessel stays visually intact until final explosion

When a vessel crashes, some recordings show the ghost vessel staying fully intact during the impact sequence until the final explosion effect fires. The ghost should show parts breaking off progressively as the vessel disintegrates, but instead the full vessel model persists and then disappears all at once with the explosion.

**Root cause (from log analysis):** The crash destruction sequence records individual `Decoupled` and `Destroyed` part events as the vessel breaks apart. For ghost #9 (2026-03-14 session), the crash sequence has ~50 events across Decoupled + Destroyed types over a very short time window. One part (`SmallGearBay` pid=4174212781) received 6 separate `Decoupled` events — likely from multiple parent-child joint breaks during rapid disassembly.

The issue may be that:
1. **Event timing compression:** All crash events happen within milliseconds of each other at the same UT (same physics frame or consecutive frames). During playback at normal speed, the interpolation window may not resolve these individual events — they all fire on the same rendered frame, making the visual jump from "intact" to "exploded" with no intermediate breakup.
2. **Decoupled event subtree hiding:** `Decoupled` events hide the entire part subtree below the decoupled part. If the root part decouples early in the sequence, it could hide the entire vessel before the child `Destroyed` events have a chance to show progressive breakup.
3. **Terminal explosion timing:** `ShouldTriggerExplosion` fires when the ghost reaches the end of the trajectory. If the explosion fires at the same frame as the crash events, the explosion visual masks the breakup sequence.

**Observed in:** Sandbox career (2026-03-14). Multiple crash recordings show this behavior. Ghost #9 crash sequence has the most detailed event chain.

**Status:** Open

## 34. ShouldTriggerExplosion log spam (performance)

`ShouldTriggerExplosion` logs a VERBOSE message every frame for every ghost, even for ghosts that can never trigger an explosion (terminal state = Recovered, null, or already fired). In the 2026-03-14 session (21 ghosts, ~4.5 minutes), this produced 46,380 log lines — 39% of all Parsek output.

**Breakdown by skip reason:**
- "terminalState=Recovered, not Destroyed": ~21,000 lines (ghosts that ended with recovery)
- "terminalState=null, not Destroyed": ~7,500 lines (tree root recordings without terminal state)
- "already fired": ~5,700 lines (ghost that already exploded, checked every subsequent frame)

Combined with GhostVisual VERBOSE output (62,229 lines / 53%), these two subsystems produce 92% of all Parsek log output.

**Fix:** Replaced `ParsekLog.Verbose` with `ParsekLog.VerboseRateLimited` in `ShouldTriggerExplosion` skip paths (already-fired and not-Destroyed). One-time paths (ghost null, will fire) remain as plain Verbose. Rate-limit keys are per-ghost-index so each ghost logs once then suppresses.

**Status:** Fixed

## 35. Engine FX diagnostic shows `playing=False` on first ignition frame

The engine FX diagnostic log (`SetEngineEmission` line 6140) reports `playing=False` on the first frame after `EngineIgnited` because Unity's `ParticleSystem.isPlaying` doesn't reflect the current frame's `Play()` call — it returns the previous simulation step's state.

**Root cause:** `SetEngineEmission` (ParsekFlight.cs:6108) calls `ps.Play()` correctly, but the diagnostic reads `ps.isPlaying` in the same frame (line 6138), before Unity has processed the play request. The particle system starts emitting from the next frame as expected.

**Visual impact:** None — this is a logging artifact. The 462 `playing=False` log entries in the 2026-03-14 session are from the rate-limited diagnostic (0.5s interval) logging once at ignition time. The particle FX visually appears correctly from the next rendered frame.

**Status:** Not a bug — logging artifact only

## 36. GhostVisual VERBOSE output dominates log (performance)

`GhostVisualBuilder` VERBOSE diagnostics produced 62,229 log lines in the 2026-03-14 session — 53% of all Parsek output. This includes per-part mesh renderer enumeration, FX placement diagnostics, variant fallback messages, and jettison transform resolution. All of this is re-emitted on every loop cycle rebuild for all ghosts.

Combined with the ShouldTriggerExplosion spam (bug #34, now fixed), these two subsystems were responsible for 92% of all Parsek log output.

**Breakdown of high-volume GhostVisual messages:**
- Variant fallback ("no active variant renderers and no GAMEOBJECT rules"): 1,869 lines — mostly `strutConnector` (588x), `pointyNoseConeB` (144x), `Panel0` (144x). These parts have `ModulePartVariants` with texture-only variants, no GAMEOBJECT rules. The fallback correctly includes all renderers.
- Jettison "found on prefab but not in cloneMap": 275 lines — non-active variant shroud transforms. Expected and harmless.
- Per-part MeshRenderer counts, FX nozzle counts, hierarchy dumps: bulk of the remaining lines.

**Fix:** Rate-limited the highest-volume per-part build diagnostics using `VerboseRateLimited` with 60-second intervals and per-part-name keys. Affected messages: part summary, variant selection/fallback, per-MeshRenderer/SkinnedMeshRenderer cloning, modelRoot DIAG, jettison cloneMap misses, engine hierarchy dump, outside-model MR warnings. Each message logs once on first ghost build, then is suppressed for 60s (well beyond a typical 10-30s loop cycle).

**Status:** Fixed

## 37. Ghost parts show wrong texture variant

When a player selects a non-default part variant (e.g., a different paint scheme on the Mk1-3 Command Pod, or an orange fuel tank variant), the ghost renders with the prefab's default texture instead of the recorded variant's texture. The part geometry (shape) is correct — only the visual appearance (texture/color) is wrong.

**Root cause:** `GhostVisualBuilder.TryGetSelectedVariantGameObjectStates` (line ~3458) only processes `GAMEOBJECTS` rules from the selected `VARIANT` config, which control geometry visibility (enable/disable sub-meshes). `TEXTURE` and `MATERIAL` variant rules are completely unsupported — they are never read from the variant config, and no `SetTexture` or material property overrides are applied to the ghost's cloned renderers. No warning is logged for the skipped rules.

**Affected parts:** All parts with `ModulePartVariants` that use TEXTURE or MATERIAL rules for variant differentiation. Common examples:
- Command pods with paint schemes (mk1pod_v2, mk1-3pod, mk2LanderCabin_v2)
- Fuel tanks with color variants (fuelTank, fuelTankSmall, Rockomax series)
- Structural adapters with color variants
- Making History size 1.5 parts

Parts with geometry-only variants (GAMEOBJECTS rules) are handled correctly — this bug only affects texture/material-based variants.

**Distinction from bug #31:** Bug #31 is about engine shroud *geometry* (jettison transform) not rendering for non-active variant meshes. This bug is about the *surface appearance* (texture/color) being wrong even when the correct geometry is shown.

**Identified by:** Part coverage audit (2026-03-15)

**Status:** Open
