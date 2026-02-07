# Parsek Edge Cases TODO

Identified during vessel persistence implementation. Prioritized by likelihood and severity.

## Critical (will hit these in testing)

- [x] **Resource deltas applied multiple times on revert** — `LastAppliedResourceIndex` is NOT reset in OnLoad's revert path, only `VesselSpawned` is. Every revert re-applies the same funds/science/reputation deltas. Runaway inflation. **Fixed:** Reset `LastAppliedResourceIndex = -1` alongside `VesselSpawned` in the revert path.
- [x] **`initialLoadDone` flag never resets** — Loading a different save game within the same KSP session skips OnLoad entirely. Save B shows Save A's recordings. **Fixed:** Track `HighLogic.SaveFolder`; when it changes, reset `initialLoadDone` so the new save's data loads fresh.
- [x] **Time warp skips entire recording range** — Player warps from UT 100 to UT 5000. Recording EndUT is 500. Ghost was never active, but the `pastEnd && !ghostActive` immediate-spawn path fires. Vessel appears without any ghost playback (confusing but functional). **Fixed:** Stop time warp when UT is about to enter a recording's range with a pending vessel spawn; uses frame-over-frame overlap detection plus a 1-real-second look-ahead for high warp rates.

## High (likely to encounter)

- [x] **SOI change during recording** — Recording starts on Kerbin, goes to Mun orbit. `InterpolateAndPosition` uses `before.bodyName` to find the body, but consecutive points may have different bodies. Lerp between Kerbin-relative and Mun-relative positions = garbage coordinates. **Fixed:** Look up each point's body separately in `InterpolateAndPosition`; both world positions are in Unity's global coordinate system so the lerp remains valid across SOI boundaries.
- [x] **Docking during recording** — Active vessel changes identity. Snapshot captures the docked composite. Respawned vessel is the whole docked assembly, potentially duplicating the station it docked to. **Fixed:** Track recording vessel's `persistentId`; `SamplePosition` auto-stops recording when the active vessel changes.
- [x] **Vessel switching during recording** — Player switches to another vessel mid-recording with `[` or `]`. `FlightGlobals.ActiveVessel` changes. SamplePosition records the wrong vessel's position. **Fixed:** Same `persistentId` guard in `SamplePosition` detects the switch and stops recording.
- [x] **Multiple reverts spawn duplicate vessels** — Record, merge+keep, vessel spawns, revert, OnLoad resets `VesselSpawned=false`, vessel spawns again. Two copies of the same vessel if the first one persisted (e.g. tracked in Tracking Station). **Fixed:** Store `SpawnedVesselPersistentId` on spawn; before re-spawning, check `FlightGlobals.Vessels` for a vessel with that pid — if it exists, skip the spawn.

## Medium (edge cases worth knowing about)

- [x] **F9 pressed while game paused** — `HandleInput` runs during pause. Recording starts but `InvokeRepeating` may not tick while paused, or captures stale positions. **Fixed:** `StartRecording()` checks `Time.timeScale` and refuses to start while paused.
- [x] **Crew dies during recording** — Kerbal dies, `onVesselWillDestroy` fires, `vesselDestroyedDuringRecording = true`. But the kerbal's death status isn't synced with the snapshot. Respawning the vessel brings back a dead kerbal. **Fixed:** `RespawnVessel` strips Dead/Missing crew from the snapshot before spawning.
- [x] **Merge dialog appears in wrong scene** — Esc > Abort Mission fires `OnSceneChangeRequested(SPACECENTER)`. Recording stashed. `OnFlightReady` doesn't fire in Space Center, so merge dialog never shows. Recording stuck as pending forever. **Fixed:** `ParsekScenario.OnLoad` auto-commits pending recordings when not in Flight scene, unreserving crew from snapshot since merge options aren't available.
- [x] **Quicksave/quickload bypasses revert logic** — F5/F9 quicksave/load doesn't go through the "Revert to Launch" path. OnLoad fires but from quicksave data. The `initialLoadDone` flag skips it, preserving session recordings, but `VesselSpawned` flags aren't reset. **Fixed:** `LastAppliedResourceIndex` is now persisted in saves and restored on load. On revert, launch quicksave has -1 (correct). On quickload, mid-session value is restored (prevents double-applying deltas).
- [x] **Crew reserved but never spawned (forever reserved)** — Recording committed with "Merge + Keep Vessel", crew reserved. Player never reaches EndUT or discards. Crew permanently stuck as Assigned. **Fixed:** `ParsekScenario.OnLoad` checks all committed recordings; if EndUT has passed without spawn, auto-unreserves crew and nulls the snapshot.
- [x] **Deferred spawn at EndUT while launching from same pad** — RespawnVessel called at EndUT while player is actively launching from the same pad with a new vessel. Physics glitches or collisions. **Fixed:** Before spawning, check distance between spawn position and active vessel. If < 200m, recover for funds instead of spawning physically.

## Low (unlikely but worth tracking)

- [x] **DistanceFromLaunch calculation with SOI change** — First point on Kerbin, vessel now orbiting Mun. GetWorldSurfacePosition on Kerbin with Mun-relative lat/lon/alt = wrong result. **Fixed:** Destroyed case uses each point's own body. Intact case uses `vessel.GetWorldPos3D()` which is always correct regardless of SOI.
- [ ] **Vessel unloaded at snapshot time** — BackupVessel() on an unloaded vessel might return incomplete data.
- [ ] **Recording duration zero** — Single sample point before revert. StartUT = EndUT. Ghost playback logic may behave unexpectedly.
- [ ] **Negative resource delta with insufficient resources** — Could leave player with negative funds.
- [ ] **Float precision in science/reputation** — Large values lose precision due to float storage.
- [ ] **Latitude/longitude precision at poles** — Recording at lat=90, lon=any is ambiguous for positioning.
- [ ] **ConfigNode.SetValue silent failure** — SetValue returns false if key doesn't exist; doesn't create new values.
- [x] **Save file locale issues** — Double.TryParse without InvariantCulture in OnLoad could fail with comma-decimal locales. **Fixed:** All TryParse calls in OnLoad now use `CultureInfo.InvariantCulture`.

## Fixed

- [x] **Multiple recordings sharing same crew** — `SwapReservedCrewInFlight()` swaps reserved kerbals out of the active vessel after reservation, replacing them with their hired replacements.
