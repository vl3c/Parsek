# Parsek Edge Cases TODO

Identified during vessel persistence implementation. Prioritized by likelihood and severity.

## Critical (will hit these in testing)

- [ ] **Resource deltas applied multiple times on revert** — `LastAppliedResourceIndex` is NOT reset in OnLoad's revert path, only `VesselSpawned` is. Every revert re-applies the same funds/science/reputation deltas. Runaway inflation.
- [ ] **`initialLoadDone` flag never resets** — Loading a different save game within the same KSP session skips OnLoad entirely. Save B shows Save A's recordings.
- [ ] **Multiple recordings sharing same crew** — Record with Jeb, merge+keep. Record with Jeb again (before first vessel spawns). Both snapshots have Jeb. First vessel spawns and unreserves Jeb. Second vessel tries to spawn with Jeb but he's now on vessel #1.
- [ ] **Time warp skips entire recording range** — Player warps from UT 100 to UT 5000. Recording EndUT is 500. Ghost was never active, but the `pastEnd && !ghostActive` immediate-spawn path fires. Vessel appears without any ghost playback (confusing but functional).

## High (likely to encounter)

- [ ] **SOI change during recording** — Recording starts on Kerbin, goes to Mun orbit. `InterpolateAndPosition` uses `before.bodyName` to find the body, but consecutive points may have different bodies. Lerp between Kerbin-relative and Mun-relative positions = garbage coordinates.
- [ ] **Docking during recording** — Active vessel changes identity. Snapshot captures the docked composite. Respawned vessel is the whole docked assembly, potentially duplicating the station it docked to.
- [ ] **Vessel switching during recording** — Player switches to another vessel mid-recording with `[` or `]`. `FlightGlobals.ActiveVessel` changes. SamplePosition records the wrong vessel's position.
- [ ] **Multiple reverts spawn duplicate vessels** — Record, merge+keep, vessel spawns, revert, OnLoad resets `VesselSpawned=false`, vessel spawns again. Two copies of the same vessel if the first one persisted (e.g. tracked in Tracking Station).

## Medium (edge cases worth knowing about)

- [ ] **F9 pressed while game paused** — `HandleInput` runs during pause. Recording starts but `InvokeRepeating` may not tick while paused, or captures stale positions.
- [ ] **Crew dies during recording** — Kerbal dies, `onVesselWillDestroy` fires, `vesselDestroyedDuringRecording = true`. But the kerbal's death status isn't synced with the snapshot. Respawning the vessel brings back a dead kerbal.
- [ ] **Merge dialog appears in wrong scene** — Esc > Abort Mission fires `OnSceneChangeRequested(SPACECENTER)`. Recording stashed. `OnFlightReady` doesn't fire in Space Center, so merge dialog never shows. Recording stuck as pending forever.
- [ ] **Quicksave/quickload bypasses revert logic** — F5/F9 quicksave/load doesn't go through the "Revert to Launch" path. OnLoad fires but from quicksave data. The `initialLoadDone` flag skips it, preserving session recordings, but `VesselSpawned` flags aren't reset.
- [ ] **Crew reserved but never spawned (forever reserved)** — Recording committed with "Merge + Keep Vessel", crew reserved. Player never reaches EndUT or discards. Crew permanently stuck as Assigned.
- [ ] **Deferred spawn at EndUT while launching from same pad** — RespawnVessel called at EndUT while player is actively launching from the same pad with a new vessel. Physics glitches or collisions.

## Low (unlikely but worth tracking)

- [ ] **DistanceFromLaunch calculation with SOI change** — First point on Kerbin, vessel now orbiting Mun. GetWorldSurfacePosition on Kerbin with Mun-relative lat/lon/alt = wrong result.
- [ ] **Vessel unloaded at snapshot time** — BackupVessel() on an unloaded vessel might return incomplete data.
- [ ] **Recording duration zero** — Single sample point before revert. StartUT = EndUT. Ghost playback logic may behave unexpectedly.
- [ ] **Negative resource delta with insufficient resources** — Could leave player with negative funds.
- [ ] **Float precision in science/reputation** — Large values lose precision due to float storage.
- [ ] **Latitude/longitude precision at poles** — Recording at lat=90, lon=any is ambiguous for positioning.
- [ ] **ConfigNode.SetValue silent failure** — SetValue returns false if key doesn't exist; doesn't create new values.
- [ ] **Save file locale issues** — Double.TryParse without InvariantCulture in OnLoad could fail with comma-decimal locales.
