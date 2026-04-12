# TODO & Known Bugs

Previous entries (225 bugs, 51 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v1.md`.
Entries 272–303 (78 bugs, 6 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v2.md`.

---

# Known Bugs

## 314. Save/load can prune branched recordings even when sidecars still contain real data

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). During a branched `Kerbal X` flight, the user saved, loaded, and then merged the tree. Two EVA/branch recordings had real sidecars on disk before the load:

- `33ea504b82cd479cbc2198c6701a9228.prec`
- `519ae674050d40e3a462cba6328a1e34.prec`

Collected evidence from `logs/2026-04-12_1549_storage-followup-playtest/`:

- Before the load, both branch recordings were actively flushing real trajectory and part-event data, and their sidecars were rewritten (`SerializeTrajectoryInto` / `SaveRecordingFiles` for both IDs).
- On load, `RecordingStore` logged sidecar epoch mismatches for both branch recordings: `.sfs expects epoch 1, .prec has epoch 2`, then skipped sidecar load entirely.
- Immediately after load, both recordings were finalized as leaf nodes with `points=0 orbitSegs=0`.
- `PruneZeroPointLeaves` then removed both zero-point leaves and the empty branch point.
- The later merge dialog only offered the root and debris leaves; the branched EVA leaves were already gone.
- The final saved tree still points `activeRecordingId = 33ea504b82cd479cbc2198c6701a9228`, but that recording is no longer serialized in the tree body.

The skipped branch sidecars still existed on disk in both the collected snapshot and the live save, so this was not physical sidecar deletion; it was save-tree loss after load/prune.

**Additional symptom:** The root recording `eb12d51ffaa64d80a79d3a0f3886e568` also appears to come back shortened: before the load it was saved with `skippedTopLevelPoints=186`, but after merge it was rewritten with `skippedTopLevelPoints=44` and `pointCount = 44` in `persistent.sfs`. The EVA branch loss is certain; root truncation may be part of the same bug or a secondary issue.

**Root cause / hypothesis:** There is still an unresolved sidecar-epoch drift path for branch recordings around save/load or quickload transitions. When the stale epoch path fires, the loader skips valid branch sidecars, the in-memory recordings look empty, and tree finalization/pruning treats them as disposable. The dangling `activeRecordingId` suggests merge/final-save cleanup is not validating that active/root references still point at serialized recordings.

**Fix direction:** Investigate the branch-recording epoch lifecycle around OnSave/OnLoad, especially the transition where the branch sidecars were written with epoch `2` but the serialized tree nodes still expected epoch `1`. Add a regression that covers: active tree with branched children -> save -> load -> merge, then assert that branch recordings survive with non-zero playback data and tree references remain internally consistent.

**Status:** Open

---

## 315. TreeIntegrity PID collision check fails on historical vessel reuse

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). The in-game suite reported `RecordingTreeIntegrityTests.NoPidCollisionAcrossTrees` failed with `2 vessel PID(s) claimed by multiple trees`. The collected `persistent.sfs` was otherwise structurally clean: `ParentLinksValid` passed, and manual inspection found no dangling `ParentRecordingId` references.

Concrete repro data from `logs/2026-04-12_1549_storage-followup-playtest/`: three committed `Kerbal X` roots from different trees share the same root vessel PID `2708531065`:

- tree `1683a5d7535f4370baf1ca28b7823069` root `081e7b3ce4b84acc946166a0a3b7926e`
- tree `258d8922c99a45d2a1bb4bf5f7aa7070` root `7f7eadcb943941c1a1668cd44f176459`
- tree `2dc3fa77001f4ad19e766cf6f0ac5277` root `641be2f9522d439397f4ea9fa2caabd2`

**Root cause / hypothesis:** `RecordingTree.RebuildBackgroundMap` populates `OwnedVesselPids` from every recording's `VesselPersistentId`, and the in-game test assumes that this set must be globally unique across all committed trees. That assumption appears too strict once the same long-lived vessel is recorded in multiple historical trees or sessions. Current runtime usage in `GhostPlaybackLogic.IsVesselPidOwnedByCommittedTree` only needs boolean membership, not a unique tree owner, so the failure currently looks like a contract mismatch between the test and the runtime meaning of `OwnedVesselPids`, not corrupted save data.

**Fix direction:** Decide which invariant is actually required:

- If tree ownership really must be unique, narrow `OwnedVesselPids` to only the live/background claim set that matters for runtime ownership checks.
- If historical PID reuse is valid, relax or replace `NoPidCollisionAcrossTrees` with a stronger assertion that targets real conflicts in active/pending runtime state rather than archived trees.

**Status:** Open

---

## 316. Breakup debris ghosts can spawn directly into Beyond and never become visible during playback

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). During the `s4` reentry/breakup session, some `Kerbal X Debris` recordings did render normally, but later debris recordings spawned so far from the active watch context that they immediately transitioned into the hidden `Beyond` zone and never became visible to the player.

Collected evidence from `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`:

- Early debris playback did enter the visible path: ghost `#5` transitioned `Physics->Visual dist=4457m`, and ghost `#7` transitioned `Physics->Visual dist=11876m`.
- Later debris playback for the same recording family did not: ghost `#10` spawned, immediately transitioned `Physics->Beyond dist=943546m`, and was hidden by distance LOD in the same tick.
- The same pattern repeated for ghost `#11`, which spawned and immediately transitioned `Physics->Beyond dist=952154m` before being hidden.
- The recordings themselves were present and merged correctly; the issue is playback visibility, not missing recording data.

**Root cause / hypothesis:** Breakup debris recordings that resume later in the chain can spawn from valid snapshots while the active vessel/watch context is nearly 1,000 km away, so the normal distance LOD policy hides them instantly. That makes boosters appear absent even though their ghosts were created and advanced logically.

**Fix direction:** Decide whether breakup/debris ghosts need a watched-chain visibility exemption, a different spawn/watch anchoring rule, or a stricter policy for when distant debris ghosts should be considered meaningful enough to render.

**Status:** Open

---

## 317. Horizon-locked watch camera can align retrograde instead of prograde during reentry playback

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). While watching the `s4` reentry ghost, the user reported that horizon mode pointed the camera retrograde rather than prograde.

Collected evidence from `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`:

- The session entered and re-entered horizon watch mode multiple times during the watched descent:
  - `Watch camera auto-switched to HorizonLocked (alt=606m, body=Kerbin)`
  - `Watch camera auto-switched to Free (alt=70003m, body=Kerbin)`
  - repeated `Watch camera mode toggled to HorizonLocked (user override)` during the watched reentry path
- Current logs do not emit the computed horizon forward vector, selected velocity direction, or a prograde/retrograde label, so the report cannot be proven or disproven from the collected logs alone.

**Root cause / hypothesis:** `WatchModeController.ComputeHorizonForward` currently derives the forward vector from the projected playback velocity. A sign/convention issue during reentry, chain transfer, or negative-relative-velocity cases could flip the watch camera to the retrograde direction.

**Fix direction:** Add one-shot observability around the chosen horizon forward vector and build a focused playback test for descending/reentry trajectories so the prograde direction is asserted rather than inferred visually.

**Status:** Open

---

## ~~318. Recordings window stats can show impossible distance / altitude summaries on loaded surface recordings~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). In the `s4` save, the recordings window showed incorrect `dist` / `max alt` values for recent recordings.

Collected evidence from `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`:

- `TrajectoryMath.ComputeStats` produced suspicious summaries immediately after load:
  - `points=29 segments=0 events=0 maxAlt=608 maxSpeed=175.1 dist=0 range=0 body=Kerbin`
  - `points=13 segments=0 events=0 maxAlt=611 maxSpeed=0.0 dist=2 range=0 body=Kerbin`
  - nearby recordings in the same table pass produced more plausible values (`points=58 ... dist=177`, `points=13 ... dist=126`)
- The recordings window computes these values live from loaded recordings rather than trusting `.sfs` cache fields, so this points at a loaded-trajectory/stats issue, not merely stale serialized UI metadata.

**Root cause:** Two storage-side consistency gaps were involved:

- section-authoritative recordings could keep stale flat `Points` / `OrbitSegments` after merge or optimizer split because `TrackSections` changed but the derived flat lists were left copied from the pre-merge/pre-split source
- `ComputeStats` treated relative-frame flattened points as if their `latitude` / `longitude` / `altitude` fields were absolute surface coordinates, even though relative sections reuse those fields for `(dx, dy, dz)` offsets

That combination was enough to produce contradictory summaries like `maxSpeed>0` with `dist=0`.

**Fix:** Section-authoritative merge/split paths now resync flat trajectory lists from `TrackSections` whenever the section payload can rebuild them losslessly, instead of keeping stale copied flats. `TrajectoryMath.ComputeStats` also now applies section altitude metadata and handles relative-frame point distances/ranges as offset-space measurements instead of feeding them through surface-distance math.

**Status:** ~~Fixed~~

---

## 319. Watch buttons can disable as "no ghost" after chain transfer even when the user expects an in-range watch target

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). The user reported disabled watch buttons while apparently within the ghost camera cutoff distance.

Collected evidence from `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`:

- Before transfer, the group-level watch affordance was valid: `Group Watch button 'Kerbal X' main=#0 "Kerbal X" enabled (hasGhost=True sameBody=True inRange=True)`.
- After `TransferWatch re-target: ghost #1 "Kerbal X" ...`, the same group button flipped to `disabled (no ghost) (hasGhost=False sameBody=False inRange=False)`.
- Later rows for descendant recordings `#12` and `#13` also logged `disabled (no ghost)`, not `disabled (out of range)`.
- Debris rows were separately disabled as `debris`, which is expected and distinct from the reported symptom.

**Root cause / hypothesis:** This does not currently look like a pure cutoff-distance bug. The watched chain can retarget to a descendant ghost while the table/group still evaluates watch eligibility against the group's main recording, whose own ghost is gone. The resulting `no ghost` state looks like a range/cutoff failure to the player even though the underlying reason is target selection/UI state.

**Fix direction:** Decide whether group and row watch affordances should follow the currently watchable chain descendant, or at least surface a clearer reason when the main row is unwatched but an active descendant ghost exists.

**Status:** Open

---

## ~~313. Splashed EVA spawn-at-end can place the kerbal slightly underwater~~

**Observed in:** 0.8.0 (2026-04-12). In the Phase 11.5 playtest bundle, the parent splashed vessel (`#24 "Kerbal X"`) was clamped and spawned at sea level, but the EVA child (`#25 "Raydred Kerman"`) spawned at `alt=-0.2` with `terminal=Splashed`. Log sequence:

- `Clamped altitude for SPLASHED spawn #24 (Kerbal X): 0.4 -> 0`
- `Vessel spawn for #24 (Kerbal X) ... alt=0`
- `Snapshot position override for #25 (Raydred Kerman): alt -0.213434... -> -0.213434...`
- `EVA vessel spawn for #25 (Raydred Kerman) ... alt=-0.2 terminal=Splashed`

**Evidence bundle:** `.tmp/logs/2026-04-12_163227_phase-11-5-branch-validation/`

**Primary log:** `.tmp/logs/2026-04-12_163227_phase-11-5-branch-validation/KSP.log`

**Root cause:** `VesselSpawner.ResolveSpawnPosition` only clamps splashed terminal-state altitudes when `alt > 0`. That fixes the "recorded slightly above the surface" case, but not the observed EVA endpoint case where the final trajectory point is slightly below sea level. The EVA path uses the trajectory endpoint, `OverrideSnapshotPosition` writes the negative altitude into the snapshot unchanged, and `SpawnAtPosition`/terminal-state override does not enforce a sea-surface floor for splashed EVA spawns. The parent vessel takes the clamp; the EVA child does not.

**Test gap:** `SpawnSafetyNetTests.ResolveSpawnPosition_EvaLanded_FallsThroughToSplashedClamp` only covers `alt > 0 -> 0`. There is no regression test for a splashed EVA endpoint with `alt < 0`.

**Fix:** `VesselSpawner.ResolveSpawnPosition` now floors every non-zero `TerminalState.Splashed` altitude to sea level (`0.0`), not just the `alt > 0` case. That applies uniformly to EVA, breakup-continuous, and snapshot-based splashed spawns before `OverrideSnapshotPosition` / `SpawnAtPosition` runs. Added `ResolveSpawnPosition_EvaSplashed_NegativeEndpointAltitude_FloorsToSeaLevel` and updated breakup-continuous coverage so slightly negative terminal samples cannot place a spawned vessel underwater.

**Status:** ~~Fixed~~

---

## ~~312. Duplicate-blocker recovery destroys sibling recordings~~

**Observed in:** 0.8.0 (2026-04-12). Playtest placed 4 "Crater Crawler" ghosts on the runway within ~10 m of each other. Only 2 of 4 spawned -- each new spawn destroyed the previous one. Log sequence showed `Duplicate blocker detected for #6: recovering pid=... at 8m -- likely quicksave-loaded duplicate (#112)` firing for every pair.

**Root cause:** `VesselSpawner.ShouldRecoverBlockerVessel` matched by NAME only. The #112 fix was written for a specific scenario -- KSP's quicksave restores a Parsek-spawned vessel with the same PID while Parsek is also trying to spawn the same recording, creating two copies owned by the same recording. The correct response in that case is to destroy the restored duplicate. But a name-only check cannot distinguish that scenario from "four sibling recordings of the same vessel type landed near each other" -- in the latter, each new spawn found a sibling with the same vesselName and destroyed it, cascading across all four showcases.

**Fix:** `ShouldRecoverBlockerVessel` now also requires the blocker's PID to match THIS recording's own `Recording.SpawnedVesselPersistentId`. That's the only way to be certain the blocker is a duplicate of OURSELVES (the #112 scenario). If the blocker belongs to a sibling recording (different `SpawnedVesselPersistentId`), the check returns false and `CheckSpawnCollisions` falls through to walkback, which finds a clear sub-step along the trajectory and spawns in the correct place.

Signature change: `ShouldRecoverBlockerVessel(Recording rec, string blockerName, string recordingVesselName, uint blockerPid)`. The single call site in `CheckSpawnCollisions` now passes the recording and `blockerVessel.persistentId`. Unit tests in `DuplicateBlockerRecoveryTests` rewritten to cover the new PID-match logic: the #112 self-PID case (recover), the #312 sibling case (walkback), the first-spawn / `SpawnedVesselPersistentId == 0` case (walkback), and preserved null/empty/case-sensitive behavior from the original.

**Status:** Fixed

---

## ~~311. Walkback spawns mid-air on diagonally-descending trajectories~~

**Observed in:** 0.8.0 (2026-04-12). When `TryWalkbackForEndOfRecordingSpawn` steps backward through a trajectory to find a non-overlapping candidate, it historically used the raw trajectory altitude for the clear position. For a vessel diagonally descending onto a landing site, the earlier trajectory points were 10-30 m in the air — walking back found a lateral-clear spot but placed the vessel mid-air, and it fell. Related to #309 (old `ClampAltitudeForLanded` would down-clamp the walkback result aggressively, which *accidentally* masked this, but broke mesh-object positioning).

**Root cause:** The walkback callback used `body.TerrainAltitude(lat, lon)` as the surface reference, which is PQS-only and cannot see the real surface when it includes mesh objects (Island Airfield runway, launchpad, KSC buildings). Either the vessel was spawned underground (mesh-object case) or left mid-air (regular terrain with sparse PQS fallback).

**Fix:** After walkback returns a clear candidate, fire a top-down `Physics.Raycast` at the candidate `(lat, lon)` using the same layer mask as `Vessel.GetHeightFromSurface` (`LayerUtil.DefaultEquivalent | 0x8000 | 0x80000` — default + terrain + buildings). If the raycast hits AND the candidate altitude is more than `WalkbackSurfaceSnapThresholdMeters` (5 m) above the hit, snap the altitude down to `surface + WalkbackSurfaceClearanceMeters` (1 m). If the raycast misses (target area unloaded), fall back to the PQS safety floor via `ClampAltitudeForLanded`. The raycast catches real mesh-object surfaces that PQS terrain alone cannot represent. New helper: `VesselSpawner.TryFindSurfaceAltitudeViaRaycast(body, lat, lon, startAltAboveSurface)`. Uses `FlightGlobals.getAltitudeAtPos` to convert the hit point to ASL altitude.

**Status:** Fixed

---

## ~~310. Spawn collision detection used 2 m-cube blocker approximation~~

**Observed in:** 0.8.0 (2026-04-12). `SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels` approximated every loaded vessel as a 2 m-cube at its `GetWorldPos3D()` position and did an AABB overlap check against the spawn bounds. Large vessels (stations, planes, carriers) were under-represented, letting walkback candidates pass inside their real geometry. Small rovers spawned near a docked station would be flagged as clear despite being inside the station's wings. Conversely, AABB false-positives were possible for sparse vessels with wide bounds.

**Root cause:** The original implementation (#127 or earlier) used `FallbackBoundsSize = 2f` as a conservative placeholder for the blocker vessel's bounds because there was no easy way to get the real bounds from a loaded `Vessel` object. The spawn side had access to the snapshot-computed AABB, but the blocker side used the 2 m cube. This was accurate enough for most rocket-to-rocket cases but broke down for large blockers and for mesh-object-adjacent spawns.

**Fix:** Rewrote `CheckOverlapAgainstLoadedVessels` to use `Physics.OverlapBox` against real part colliders. Each hit is resolved to its owning `Part` via `FlightGlobals.GetPartUpwardsCached` (the same helper KSP uses in `Vessel.CheckGroundCollision`). Non-part hits (terrain, building, runway mesh colliders) are skipped via the null-Part filter — the airfield runway never blocks a spawn, but real vessel parts do.

Layer mask: `LayerUtil.DefaultEquivalent` (0x820001 = bits 0, 17, 23) — the same mask KSP itself uses inside `Vessel.CheckGroundCollision` as the "part-bearing layers" identifier. An earlier revision of this fix used `(1<<0)|(1<<17)|(1<<19)` based on a misreading of Unity's layer map (assumed layer 19 was "PartTriggers"; per Principia's verified layer enum layer 19 is PhysicalObjects and PartTriggers is actually layer 21). Using KSP's own constant directly is both correct and future-proof against layer renumbering.

OverlapBox rotation: `Quaternion.FromToRotation(Vector3.up, upAxis)` where `upAxis` is the surface normal at the spawn position (derived from the enclosing celestial body). Aligns the local-space AABB with the body's local up direction so spawns on the far side of a curved body use a correctly-oriented query box.

Legacy filters (skip debris/EVA/flag, exempt parent vessel PID, skip active vessel) retained. The `BoundsOverlap` pure helper is kept for unit tests using injected predicates.

**Status:** Fixed

---

## ~~309. Rovers on Island Airfield spawn 19 m underground~~

**Observed in:** 0.8.0 (2026-04-12). Rover recordings captured at the Island Airfield runway spawned 19 m below the runway surface, inside the raw PQS terrain. Ghost playback of the same recordings also rendered below the runway. Both spawn and ghost placement were treating the airfield as if it didn't exist.

**Root cause:** Three call sites used `body.TerrainAltitude(lat, lon)` which is PQS-only — it queries `pqsController.GetSurfaceHeight()` and returns the raw planetary surface UNDER any placed mesh object (Island Airfield, launchpad, KSC facilities). For a vessel recorded ON the airfield at alt=133.9 m, `body.TerrainAltitude()` returned ~114.9 m (raw terrain under the runway). The three sites and their bugs:

1. **`ParsekFlight.CaptureTerminalPosition`** stored `rec.TerrainHeightAtEnd = body.TerrainAltitude(...)` — losing the 19 m airfield offset. Downstream consumers computed `recordedClearance = alt - TerrainHeightAtEnd` and got ~19 m, which was meaningless to them.
2. **`VesselSpawner.ClampAltitudeForLanded`** computed `target = terrainAlt + LandedClearanceMeters` and aggressively down-clamped any altitude above the target — specifically to fix #282 (Mk1-3 pod low-clearance clipping). For airfield rovers, this buried them 17 m below the runway.
3. **`VesselGhoster.ApplyTerrainCorrection`** called `TerrainCorrector.ComputeCorrectedAltitude(currentTerrain, recordedAlt, recordedTerrain)` which computed `corrected = currentTerrain + (recordedAlt - recordedTerrain)` — mathematically correct for terrain-relative correction, but `currentTerrain` was PQS-only, so the "corrected" altitude was placed relative to PQS, burying ghosts under the runway.

Decompiling `Vessel.CheckGroundCollision` and `Vessel.GetHeightFromSurface` confirmed the right API: KSP uses `Physics.Raycast` with layer mask `LayerUtil.DefaultEquivalent | 0x8000 | 0x80000` to find the true surface including building colliders. The `Vessel.terrainAltitude` property (documented as "height in meters of the nearest terrain **including buildings**") is reverse-computed from that raycast — available on loaded vessels.

**Fix:** Replaced PQS-only terrain queries with the correct surface-aware sources and changed the clamping philosophy from "force to terrain+2 m" to "trust the recorded altitude, only push up if below PQS safety floor":

1. **`ParsekFlight.CaptureTerminalPosition`** — captures `vessel.terrainAltitude` (raycast-derived, includes buildings) instead of `body.TerrainAltitude()`. Also logs the PQS vs. mesh offset for diagnostics.
2. **`VesselSpawner.ClampAltitudeForLanded`** — rewritten. No more down-clamp. Only clamps UP when `alt < (pqsTerrain + UndergroundSafetyFloorMeters)` (2 m), which only fires when PQS terrain has shifted up since recording (rare: KSP update / terrain mod). The #282 low-clearance case is still caught by this floor. KSP's own `Vessel.CheckGroundCollision` handles part-geometry clipping via `getLowestPoint()` on vessel load, so we don't need to front-run it. Renamed `LandedClearanceMeters` → `UndergroundSafetyFloorMeters` (semantic shift — it's a floor, not a target).
3. **`ParsekFlight.ApplyLandedGhostClearance`** — same philosophy. Trusts the recorded altitude; only pushes up if below `pqsTerrain + 0.5 m`. NaN-fallback legacy path unchanged.
4. **`VesselGhoster.ApplyTerrainCorrection`** — rewritten to apply the underground safety floor (0.5 m above PQS) instead of terrain-relative correction.
5. **Removed `TerrainCorrector.ComputeCorrectedAltitude`** — the terrain-relative correction formula was the core of the bug. Tests that encoded it also removed.

**Test rewrite:** `SpawnSafetyNetTests.ClampAltitudeForLanded_*` updated to match the new semantics. The #282 low-clearance case (176.5 m recorded, 175.6 m PQS terrain, 0.9 m clearance) is preserved as a regression guard — it now triggers the 2 m safety floor and pushes up to 177.6 m. Airfield case (133.9 m recorded, 114.9 m PQS terrain, 19 m mesh offset) passes through unchanged.

**Status:** Fixed

---

## ~~307. Rewind save lost on vessel switch during recording~~

**Observed in:** 0.8.0 (2026-04-12). When the player switches vessels during an active recording session (e.g. switching from a booster to a payload, or clicking a different vessel in the tracking station), the R (rewind) button never appears on recordings committed after the switch.

**Root cause:** `OnVesselSwitchComplete` has two flush paths for the outgoing recorder: (1) still-active recorder transitioned to background (line 1335), and (2) already-backgrounded recorder with pending flush (line 1361). Both paths called `FlushRecorderToTreeRecording` but did not call `CopyRewindSaveToRoot`. The rewind save filename from the outgoing recorder's `CaptureAtStop` was never propagated to the tree root recording. After the switch, `recorder` is set to null, and when the tree is eventually committed, `GetRewindRecording` resolves through the root -- which has a null `RewindSaveFileName`.

Related to T59 (EVA branch case), which fixed the same underlying problem in `CreateSplitBranch`. The vessel-switch paths were missed.

**Fix:** Added `CopyRewindSaveToRoot` calls in both vessel-switch flush paths in `OnVesselSwitchComplete` (lines 1337 and 1362), right after `FlushRecorderToTreeRecording` and before `recorder = null`. Uses `recorder.CaptureAtStop` as primary source with `recorder.RewindSaveFileName` as fallback, consistent with all other flush sites.

**Status:** Fixed

---

## ~~306. Ghost engine nozzles always glow red~~

**Observed in:** 0.8.0 (2026-04-12). Engine nozzle parts on ghost vessels permanently displayed a red/orange emissive glow, as if overheating. Stock KSP engines do not glow during normal operation -- the emissive channel is driven at runtime by the thermal system (`part.temperature / part.maxTemperature`).

**Root cause:** `BuildHeatMaterialStates` and `CollectReentryGlowMaterials` in `GhostVisualBuilder.cs` read `coldEmission` from the cloned prefab material via `materialClone.GetColor(emissiveProperty)`. Engine nozzle prefab materials have non-zero `_EmissiveColor` values baked in. Since ghost parts have no temperature simulation, this inherited emissive became the permanent baseline -- the nozzle always glowed at the prefab's emissive level.

**Fix:** Two changes: (1) Force `coldEmission = Color.black` and clear the emissive property on cloned materials immediately after cloning, in both `BuildHeatMaterialStates` (per-part heat) and `CollectReentryGlowMaterials` (whole-ghost reentry glow). (2) Decouple thermal animation from engine/RCS throttle -- removed `ApplyHeatState` calls from `EngineIgnited/Shutdown/Throttle` and `RCSActivated/Stopped` handlers in `GhostPlaybackLogic`. Thermal glow now driven purely by `ThermalAnimationCold/Medium/Hot` events from `ModuleAnimateHeat` polling. Thresholds adjusted: cool <40%, warm 40-80%, hot >80% (was <10%, 33%+, 66%+). Hysteresis gaps [0.35, 0.40) and [0.75, 0.80).

**Status:** Fixed

---

## ~~304. Raw #autoLOC keys in standalone recording vessel names~~

**Observed in:** Sandbox (2026-04-10). Rovers and stock vessels launched from runway/island airfield showed `#autoLOC_501182` instead of "Crater Crawler" in the Recordings Manager, timeline entries, and log messages.

**Root cause:** Three call sites read `FlightGlobals.ActiveVessel.vesselName` (which is a raw `#autoLOC_XXXX` key for stock vessels) and passed it to `RecordingStore.StashPending()` without calling `Recording.ResolveLocalizedName()`. `BuildCaptureRecording` (FlightRecorder.cs:4541) resolved the name correctly, but `StashPending` creates a new Recording object with whatever string is passed in, discarding the resolved name.

**Fix:** In `StashPendingOnSceneChange`, `ShowPostDestructionMergeDialog`, and `CommitFlight`, prefer `CaptureAtStop.VesselName` (already resolved by `BuildCaptureRecording`) with `ResolveLocalizedName` on the fallback path.

**Status:** Fixed

---

## ~~305. Standalone recordings lost on revert-to-launch~~

**Observed in:** Sandbox (2026-04-10). Rover recordings from runway/island airfield were silently discarded on revert-to-launch. The user had to manually commit via the Commit Flight button instead of getting the merge dialog automatically. Tree recordings (rockets with staging) survived reverts via the Limbo state mechanism, but standalone recordings had no equivalent.

**Root cause:** `DiscardStashedOnQuickload` (ParsekScenario.cs) unconditionally discards pending standalone recordings on any FLIGHT->FLIGHT transition with UT regression. Tree recordings survive because `PendingTreeState.Limbo` is explicitly preserved. Standalone recordings had no Limbo equivalent.

**Fix:** Added `PendingStandaloneState` enum (parallel to `PendingTreeState`) with `Finalized` and `Limbo` values. `StashPendingOnSceneChange` assigns `Limbo` when the destination scene is FLIGHT. `DiscardStashedOnQuickload` preserves Limbo standalones (mirroring tree Limbo preservation). A new Limbo dispatch block in `OnLoad` (parallel to the tree Limbo dispatch) decides:
- If `ScheduleActiveStandaloneRestoreOnFlightReady` is set (F5/F9 mid-recording): discard the stale Limbo standalone, let the restore resume from F5 data.
- If no restore is scheduled (revert-to-launch): finalize the Limbo standalone for the merge dialog.

Design aligned with bug #271 (standalone/tree unification) — the two modes now share symmetric state tracking.

**Status:** Fixed

---

## ~~298b. FlightRecorder missing allEngineKeys -- #298 dead engine sentinels only work for BackgroundRecorder~~

`PartStateSeeder.EmitEngineSeedEvents` emits `EngineShutdown` sentinels for dead engines
using `sets.allEngineKeys` (#298). `BackgroundRecorder.BuildPartTrackingSetsFromState` sets
`allEngineKeys = state.allEngineKeys`, but `FlightRecorder.BuildCurrentTrackingSets` omits it
entirely. FlightRecorder has no `allEngineKeys` field, so `SeedEngines` populates it on a
temporary `PartTrackingSets` that is immediately discarded. The subsequent `EmitSeedEvents`
call creates a new set with an empty `allEngineKeys`, emitting zero sentinels.

**Fix:** Add `private HashSet<ulong> allEngineKeys` to FlightRecorder and include it in
`BuildCurrentTrackingSets`. `SeedEngines` will then populate FlightRecorder's own set
(same reference pattern as `activeEngineKeys`), and the follow-up `EmitSeedEvents` will
see the populated set and emit the sentinels.

**Status:** ~~Fixed~~

---

## ~~297. FallbackCommitSplitRecorder orphans tree continuation data as standalone recording~~

When a vessel is destroyed during tree recording and the split recorder can't resume,
`FallbackCommitSplitRecorder` stashes captured data as a standalone recording via
`RecordingStore.StashPending`, ignoring the active tree. The tree root is truncated
(missing post-breakup trajectory) and the continuation becomes an ungrouped standalone.

Real-world repro: Kerbal X standalone recording promoted to tree at first breakup (root
gets 47 points). More breakups add debris children, root continues recording (83 more
points in buffer). Vessel crashes, joint break triggers `DeferredJointBreakCheck`,
classified as WithinSegment, `ResumeSplitRecorder` detects vessel dead, calls
`FallbackCommitSplitRecorder` which stashes 83-point continuation as standalone.

**Fix:** Extracted `TryAppendCapturedToTree` -- when `activeTree != null`, appends captured
data to the active tree recording (fallback to root) via `AppendCapturedDataToRecording`,
sets terminal state and metadata. Standalone path only runs when not in tree mode.

**Status:** ~~Fixed~~

---

## ~~296. EVA kerbal who planted flag did not appear after spawn~~

Log shows KSCSpawn successfully spawned the EVA kerbal (Bill Kerman, pid=484546861), but the user reports not seeing it. Originally attributed to post-spawn physics destruction.

**Likely duplicate of T57.** The scenario is identical: surface EVA near the launchpad, parent vessel already spawned, kerbal never materialized. The "successfully spawned" log was misleading -- `VesselSpawned = true` is set for abandoned spawns too (prevents vessel-gone reset). T57's fix (exempt parent vessel from EVA collision checks) addresses the root cause. Verify in next playtest.

**Status:** ~~Closed as likely duplicate of T57.~~

---

## ~~290. F5/F9 quicksave/quickload interaction with recordings~~

Broad investigation of F5/F9 + recording system interactions. Bug #292 (F9 after merge drops recordings) was the original smoking gun.

**Bug found:** `CleanOrphanFiles` (cold-start only) built its known-ID set from committed recordings/trees but not the pending tree. On cold-start resume, `TryRestoreActiveTreeNode` stashes the active tree into `pendingTree` before `CleanOrphanFiles` runs, so branch recordings (debris, EVA) were invisible to the orphan scanner and their sidecar files deleted. Data survived the first session (in memory) but non-active branches had `FilesDirty=false`, so sidecars were not rewritten. Second cold start degraded them to 0 points.

**Fix:** Extracted `BuildKnownRecordingIds()` (internal static) from `CleanOrphanFiles`; now includes pending tree recording IDs.

**Other scenarios verified safe:** auto-merge + F9 (no optimizer, no corruption), CommitTreeFlight + F9 (same), rewind save staleness (frozen by design), quicksave auto-refresh (user-initiated only to avoid re-entering OnSave).

**Status:** ~~Fixed~~

---

## ~~290b. RestoreActiveTreeFromPending fails on #autoLOC vessel names~~

**Observed in:** Sandbox (2026-04-11). After F9 quickload, the restore coroutine waited 3s for vessel "Kerbal X" to become active, but KSP's `vesselName` was still the raw `#autoLOC_501232` key (localization not yet resolved). The vessel WAS loaded (correct `persistentId` logged), but the name-only match failed. Tree stayed in Limbo, no recording started, and the orphaned tree eventually triggered a spurious merge dialog.

**Root cause:** `RestoreActiveTreeFromPending` matched only by `vesselName`, which is unreliable immediately after quickload because KSP defers localization resolution.

**Fix:** Added PID-based matching (`VesselPersistentId` vs `Vessel.persistentId`) as the primary check, with name match as secondary fallback. Same change applied to the EVA parent chain walk. PID is locale-proof and available immediately.

**Status:** ~~Fixed~~

---

## ~~290d. MaxDistanceFromLaunch never computed for tree recordings~~

**Observed in:** Sandbox (2026-04-11). All tree recordings had `MaxDistanceFromLaunch = 0.0` despite having real trajectory data (125+ points). `IsTreeIdleOnPad` returned true for all 7 recordings, discarding the entire flight on scene exit to Space Center.

**Root cause:** `MaxDistanceFromLaunch` is computed in `VesselSpawner.ComputeMaxDistance`, which is called from `BuildCaptureRecording`. Tree recordings reach finalization via `ForceStop` (scene exit), which intentionally skips `BuildCaptureRecording` because vessel state may be unreliable during scene transitions. BgRecorder never computes it either. Result: every tree recording has the default `0.0`.

**Fix (three parts):**
1. `FinalizeIndividualRecording` now calls `VesselSpawner.BackfillMaxDistance(rec)` for recordings with `MaxDistanceFromLaunch <= 0.0` and `Points.Count >= 2`. Extracted `ComputeMaxDistanceCore` from the existing private `ComputeMaxDistance` method.
2. `IsTreeIdleOnPad` now requires at least one recording with `Points.Count > 0` before classifying a tree as idle (guards against 0-point recordings from epoch mismatch).
3. `TryRestoreActiveTreeNode` now skips .sfs replacement when the pending tree is already `Finalized` -- the in-memory tree has post-finalize data (MaxDistanceFromLaunch, terminal states) that the .sfs version lacks because KSP's OnSave runs BEFORE `FinalizeTreeOnSceneChange`.

**Status:** ~~Fixed~~

---

## ~~290e. TerrainHeightAtEnd not captured for unloaded vessels at scene exit~~

**Observed in:** Sandbox (2026-04-11). Rover ghosts appeared under the runway surface and spawn-at-end placed the vessel below the runway, causing it to clip through and explode. The rover was a background vessel (player was controlling EVA kerbal) when the scene exited.

**Root cause:** `CaptureTerminalPosition` (which captures `TerrainHeightAtEnd`) is only called when `finalizeVessel != null`. Unloaded background vessels are not findable at scene exit, so `TerrainHeightAtEnd` stays NaN. The spawn safety net falls back to PQS terrain height (~64.8m), which is below the runway structure surface (~70m).

**Fix:** In the `isSceneExit` fallback path of `FinalizeIndividualRecording`, capture terrain height from the last trajectory point's lat/lon coordinates via `body.TerrainAltitude()` for Landed/Splashed recordings.

**Status:** ~~Fixed~~

---

## ~~290f. SegmentPhase classified LANDED vessels as "atmo"~~

**Observed in:** Sandbox (2026-04-11). Rover recordings on the runway showed "Kerbin atmo" instead of "Kerbin surface" in the Phase column.

**Root cause:** Three code paths (`TagSegmentPhaseIfMissing`, `StopRecording`, `ChainSegmentManager`) classified SegmentPhase by altitude alone (`altitude < atmosphereDepth ? "atmo" : "exo"`), ignoring the vessel's situation. A landed rover on Kerbin is technically within the atmosphere by altitude, but its situation is LANDED.

**Fix:** All three sites now check `Vessel.Situations.LANDED/SPLASHED/PRELAUNCH` first and assign "surface" phase. Also added `phaseStyleSurface` (orange) to the UI, changed atmo to blue, exo to light purple.

**Status:** ~~Fixed~~

---

## ~~290g. LaunchSiteName missing from tree recordings~~

**Observed in:** Sandbox (2026-04-11). Site column empty for most recordings in the Recordings window.

**Root cause:** `FlushRecorderIntoActiveTreeForSerialization` (called during OnSave) did not copy `LaunchSiteName` or start location fields from the recorder to the tree recording. Only `FlushRecorderToTreeRecording` (normal stop path via `BuildCaptureRecording`) copied them.

**Fix:** Added LaunchSiteName, StartBodyName, StartBiome, StartSituation copy to `FlushRecorderIntoActiveTreeForSerialization`.

**Status:** ~~Fixed~~

---

## ~~290c. F5/F9 epoch mismatch — BgRecorder and force-writes advance sidecar epoch past .sfs~~

**Observed in:** Sandbox (2026-04-11). F5 quicksave, fly with staging (debris created), F9 quickload. All background recordings lost trajectory data (0 points). Same mismatch on scene exit: Bob Kerman EVA recording lost to epoch drift, then auto-discarded by idle-on-pad false positive.

**Root cause:** `BackgroundRecorder.PersistFinalizedRecording` and the scene-exit force-write loop both call `SaveRecordingFiles`, which unconditionally increments `SidecarEpoch`. Between an F5 (which writes .sfs with epoch N) and F9, BgRecorder can call `PersistFinalizedRecording` multiple times (once per `OnBackgroundVesselWillDestroy`, once per `EndDebrisRecording`), advancing the .prec epoch to N+2 or beyond. On quickload, .sfs expects epoch N but .prec has epoch N+2, triggering `ShouldSkipStaleSidecar` and silently dropping all trajectory data.

**Fix:** Added `bool incrementEpoch = true` parameter to `SaveRecordingFiles`. Out-of-band callers (BgRecorder `PersistFinalizedRecording`, scene-exit force-write loop) pass `incrementEpoch: false` so the .prec keeps the epoch from the last OnSave. OnSave and commit paths use the default `true`, preserving original #270 cross-scene staleness detection.

**Status:** ~~Fixed~~

---

## ~~286. Full-tree crash leaves nothing to continue with — `CanPersistVessel` blocks all `Destroyed` leaves~~

**Fix:** Option (c) implemented. When `spawnCount == 0 && decisions.Count > 0`, the merge dialog body shows: "No flight branches produced a vessel that can continue flying. The recordings will play back as ghosts, but no vessel will be placed." Screen message changed from generic "Merged to timeline!" to "Merged to timeline (no surviving vessels)". Wording is terminal-state-agnostic (covers Destroyed, Recovered, Docked, Boarded). The `CanPersistVessel` blocking logic is unchanged -- the player just needed to know why nothing spawned.

---

## 160. Log spam: remaining sources after ComputeTotal removal

After removing ResourceBudget.ComputeTotal logging (52% of output), remaining spam sources:
- GhostVisual HIERARCHY/DIAG dumps (~344 lines per session, rate-limited per-key but burst on build)
- GhostVisual per-part cloning details (~370 lines)
- Flight "applied heat level Cold" (46 lines, logs no-change steady state)
- RecordingStore SerializeTrackSections per-recording verbose (184 lines)
- KSCSpawn "Spawn not needed" at INFO level (54 lines)
- BgRecorder CheckpointAllVessels checkpointed=0 at INFO (15 lines)

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

**Status:** Open

---

## 189b. Ghost escape orbit line stops short of Kerbin SOI edge

For hyperbolic escape orbits, KSP's `OrbitRendererBase.UpdateSpline` draws the geometric hyperbola from `-acos(-1/e)` to `+acos(-1/e)` using circular trig (cos/sin), which clips at a finite distance (~12,000 km for e=1.342). The active vessel shows the full escape trajectory to the SOI boundary because it uses `PatchedConicSolver` + `PatchRendering`. Ghost ProtoVessels don't get a `PatchedConicSolver`.

**Options:**
1. Draw a custom LineRenderer through the recording's trajectory points (accurate but significant work)
2. Extend the orbit line beyond the hyperbola asymptote with a straight-line segment to the SOI exit point
3. Give the ghost a `PatchedConicSolver` (complex, may conflict with KSP internals)

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability) — cosmetic, same tier as T25 fairing truss

---

## 220. PopulateCrewEndStates called repeatedly for 0-point intermediate recordings

Intermediate tree recordings with 0 trajectory points but non-null VesselSnapshot trigger `PopulateCrewEndStates` on every recalculation walk (36 times in a typical session). These recordings can never have crew.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

---

## ~~241. Ghost fuel tanks have wrong color variant~~

Parts whose base/default variant is implicit (not a VARIANT node) showed the wrong color. KSP stores the base variant's display name (e.g., "Basic") as `moduleVariantName`, but no VARIANT node has that name — `TryFindSelectedVariantNode` fell through to `variantNodes[0]` (e.g., Orange) instead of keeping the prefab default. Fix: `MatchVariantNode` returns false when the snapshot names a variant with no matching VARIANT node, so callers skip variant rule application and the prefab's base materials are preserved.

**Fix:** PR #198

---

## ~~242. Ghost engine PREFAB_PARTICLE FX fires perpendicular to thrust axis~~

On Mammoth, Twin Boar, RAPIER, Twitch, Ant, Spider, Puff, and other engines, ghost PREFAB_PARTICLE FX (smoke trails, small engine flames) fired sideways instead of along the thrust axis.

**Root cause:** Ghost model parent transforms (thrustTransform, smokePoint, FXTransform) have their local +Y axis pointing sideways, not along the thrust axis. PREFAB_PARTICLE FX emits along local +Y (Unity ParticleSystem default cone axis). Without rotation correction, particles fire perpendicular to the nozzle.

In KSP's live game, the part model hierarchy is oriented within the vessel so that transforms end up with +Y along thrust. In our ghost, the cloned prefab model keeps the raw model-space orientation where +Y is typically sideways.

**Investigation:** Decompiled `PrefabParticleFX.OnInitialize` — uses `NestToParent` (resets localRotation to identity) then sets `Quaternion.AngleAxis(localRotation.w, localRotation)` from config. Decompiled `ModelMultiParticleFX.OnInitialize` — same pattern with `Quaternion.Euler(localRotation)`. Decompiled `NestToParent` — calls `SetParent(parent)` with worldPositionStays=true, then explicitly resets localPosition=zero and localRotation=identity.

Added `LogFxDirection` diagnostic that logs `emitWorld` (local +Y in world space) and `angleFromDown` for every FX instance. This revealed the pattern: entries with explicit -90 X rotation (from config or fallback) had correct angleFromDown=180, while entries with identity had wrong angleFromDown=90.

Exception: SSME (Vector) has `thrustTransformYup` where +Y already points along thrust — applying -90 X there breaks it.

**Fix:** In `ProcessEnginePrefabFxEntries`, when config has no `localRotation`, check `ghostFxParent.up.y` at process time. If abs(y) > 0.5, the parent +Y already aligns with the thrust axis — use identity. Otherwise apply `Quaternion.Euler(-90, 0, 0)` to rotate emission onto the thrust axis. Existing entries with explicit config `localRotation` (jets with `1,0,0,-90`) are unaffected.

**Remaining:** MODEL_MULTI_PARTICLE entries without config localRotation (Mammoth `ks25_Exhaust`, Twin Boar `ks1_Exhaust`, SSME `hydroLOXFlame`, ion engine `IonPlume`) still show angleFromDown=90 in the diagnostic log. These use `KSPParticleEmitter` (legacy particle system) rather than Unity `ParticleSystem`, so their emission axis may differ from +Y — visually they appear correct despite the diagnostic reading. May need separate investigation if visual issues are reported.

**Status:** ~~Fixed~~ (PREFAB_PARTICLE path)

### 242b. Multi-mode engine ghosts show both modes simultaneously

RAPIER and Panther (and any other `ModuleEnginesFX` multi-mode engines) rendered FX for all engine modes at once instead of only the active mode. Ghost would show jet exhaust and rocket exhaust simultaneously.

**Root cause:** `TryBuildEngineFX` scanned ALL EFFECTS groups for every engine module on a part. Multi-mode engines like RAPIER have separate EFFECTS groups per mode (e.g. `running_closed` for rocket, `running_open` for jet). Without filtering, each `EngineGhostInfo` contained particles from all modes.

**Fix:** Added `GetModuleEffectGroupNames(ModuleEngines)` which downcasts to `ModuleEnginesFX` and reads `runningEffectName`, `powerEffectName`, `spoolEffectName`, `directThrottleEffectName`. These names are used to filter EFFECTS groups so each engine module only scans its own referenced groups. Base `ModuleEngines` (not FX) returns empty set and falls through to scanning all groups (backward compat). Removed the old RAPIER `midx>0` skip that suppressed the second engine module entirely. RAPIER white flame fallback guarded by `modelFxEntries.Count == 0` to avoid doubling with per-module model exhaust. Added RAPIER mode-switch showcase recording with per-moduleIndex events demonstrating jet-to-rocket-to-jet switching.

**Status:** ~~Fixed~~ (PR #220)

---

## ~~242c. Ghost variant geometry not toggled -- extra FX on multi-variant parts~~

Parts with `ModulePartVariants` that toggle geometry via GAMEOBJECTS (e.g. Poodle DoubleBell/SingleBell) show all variant geometry on the ghost, including inactive variants. The Poodle ghost has 3 thrustTransforms (2 from DoubleBell + 1 from SingleBell) instead of 2, producing 3 flames instead of 2.

**Root cause:** Ghost model mesh filtering already excluded inactive variant renderers (#241), but the engine FX builder (`EngineFxBuilder`) and RCS FX builder (`TryBuildRcsFX`) discovered transforms from the raw prefab without variant awareness. `engine.thrustTransforms` (populated by KSP from ALL matching transforms regardless of variant state) was the primary source for the Poodle bug. `FindTransformsRecursive` calls in model/prefab FX methods were secondary sources. `MirrorTransformChain` then created ghost transforms for inactive-variant objects.

**Fix:** Threaded `selectedVariantGameObjects` into `TryBuildEngineFX` and `TryBuildRcsFX`. Extracted `IsAncestorChainEnabledByVariantRule` (pure, testable) from `IsRendererEnabledByVariantRule`. Filter applied at 5 points: `FindNamedTransformsCached`, `ProcessEngineLegacyFx` (engine.thrustTransforms), `ProcessEngineModelFxEntries`, `ProcessEnginePrefabFxEntries`, and `TryBuildRcsFX` (FindTransformsRecursive). Affects 9 engine parts and 3 RCS parts with GAMEOBJECTS variant rules.

**Status:** ~~Fixed~~

---

## ~~270. Sidecar file (.prec) version staleness across save points~~

Latent pre-existing architectural limitation of the v3 external sidecar format: sidecar files (`saves/<save>/Parsek/Recordings/*.prec`) are shared across ALL save points for a given save slot. If the player quicksaves in flight at T2, exits to TS at T3 (which rewrites the sidecars with T3 data), then quickloads the T2 save, the .sfs loads the T2 active tree metadata but `LoadRecordingFiles` hydrates from T3 sidecars on disk — a mismatch.

Not introduced by PR #160, but PR #160's quickload-resume path makes it more reachable (previously, quickloading between scene changes always finalized the tree, so the tree was effectively "new" each time).

**Fix:** Added `SidecarEpoch` counter to Recording, incremented on every `SaveRecordingFiles` write. The epoch is stamped into both the .prec file and the .sfs metadata. On load, `LoadRecordingFiles` validates that the .prec epoch matches the .sfs epoch. On mismatch (stale sidecar from a later save), trajectory load is skipped and a warning is logged. Committed recordings are unaffected (FilesDirty stays false after first write, so .prec is never overwritten and epochs always match). Backward compatible: old saves without epoch (SidecarEpoch=0) skip validation entirely.

**Status:** ~~Fixed~~

---

## ~~271. Investigate unifying standalone and tree recorder modes~~

**Fix:** Always-tree mode -- `ParsekFlight.StartRecording` creates a single-node `RecordingTree` for every recording. Eliminated `StashPendingOnSceneChange`, `PromoteToTreeForBreakup`, `RestoreActiveStandaloneFromPending`, `ShowPostDestructionMergeDialog`, `CommitFlight`, `PendingStandaloneState`, `VesselSwitchDecision.Stop`. Chain system (`StashPending`/`CommitPending`) and standalone merge dialog retained for backward compat -- to be unified when chain system is removed.

**Status:** ~~Fixed~~

---

# TODO

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

---

## TODO — Performance & Optimization

### T61. Continue Phase 11.5 recording storage shrink work

The first five storage slices are in place: representative fixture coverage, `v1`
section-authoritative `.prec` sidecars, alias-mode ghost snapshot dedupe, header-dispatched binary
`v2` `.prec` sidecars, and exact sparse `v3` defaults for stable per-point body/career fields.
Remaining high-value work should stay measurement-gated and follow
`docs/dev/plans/phase-11-5-recording-storage-optimization.md`:

- fresh live-corpus rebaseline against current `v3` sidecars
- additional sparse payload work only where exact reconstruction and real byte wins are proven
- post-commit, error-bounded trajectory thinning only after the format wins are re-measured
- any further snapshot-side work should preserve current alias semantics and stay covered by
  sidecar/load diagnostics

**Priority:** Current Phase 11.5 follow-on work

---

### ~~T6. LOD culling for distant ghost meshes~~

Implemented in `0.8.1` as the shipped Flight ghost LOD policy:

- `0 - 2300 m`: full fidelity
- `2300 m - 50000 m`: reduced mesh / no part events / muted expensive FX
- `50000 m - 120000 m`: hidden mesh, logical playback retained
- watched ghosts inside cutoff bypass the distance degradation path
- diagnostics now report live `full / reduced / hidden / watched override` counts

**Status:** Fixed in `0.8.1`

### T7. Ghost mesh unloading outside active time range

Ghost meshes built for recordings whose UT range is far from current playback time could be unloaded and rebuilt on demand.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

### ~~T8. Particle system pooling for engine/RCS FX~~

Phase 11.5 investigation completed as a measurement-first pass without touching FX behavior.

What shipped:

- playback diagnostics now capture live engine/RCS ghost counts, module counts, particle-system counts, and last-frame ghost spawn/destroy timings
- the showcase injection workflow can run the focused diagnostics/observability slice before mutating the save
- the in-game test runner layout/order was cleaned up so diagnostics and FX-heavy categories are easier to run repeatedly during playtests

Outcome:

- the injected showcase validation passed `Diagnostics` and `PartEventFX`
- exported logs did not show a clear FX-specific correctness or performance regression that justifies touching the current engine/RCS FX lifecycle
- the only notable failure in that bundle was `GhostCountReasonable` (`246` ghosts), which points at overall ghost population pressure rather than FX pooling specifically

Conclusion: no pooling or FX lifecycle optimization is scheduled now. Re-open only if future profiling shows playback spikes, spawn/destroy spikes, or GC pressure that clearly correlates with FX-heavy ghost churn.

**Status:** ~~Closed for Phase 11.5 -- measurement shipped, optimization deferred unless future evidence justifies it~~

---

## TODO — Ghost Visuals

### T25. Fairing internal truss structure after jettison

After fairing jettison, the ghost currently shows just the payload and base adapter. KSP's real vessel can show an internal truss structure (Cap/Truss meshes controlled by `ModuleStructuralNodeToggle.showMesh`). The prefab meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can set correctly. A procedural truss mesh was attempted but removed due to insufficient visual quality.

To implement properly: either rescale prefab Cap/Truss meshes from XSECTION data (need to reverse-engineer the mesh unit geometry), or generate higher-fidelity procedural geometry with proper materials.

**Priority:** Low — cosmetic, only visible briefly after fairing jettison

---

## TODO — Compatibility

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Last phase of roadmap — v1 targets stock only, mod compat is best-effort

---

## TODO — Recording Data Integrity

### ~~T60. Add regression coverage and diagnostics for R/FF enablement reasons~~

**Evidence bundle:** `.tmp/logs/2026-04-12_163227_phase-11-5-branch-validation/`

**Primary log:** `.tmp/logs/2026-04-12_163227_phase-11-5-branch-validation/KSP.log`

Available local playtest logs show R/FF row enablement is driven by recording/runtime state, not ghost distance. Examples:

- `logs/2026-04-10_engine-fx-regression/KSP.log:15443` — `FF #13 "Kerbal X": disabled — Stop recording before fast-forwarding`
- `logs/2026-04-12_0348_bugfixes-2-walkback/KSP.log:23067` — `R #4 "Crater Crawler": disabled — Stop recording before rewinding`
- `logs/2026-04-12_0213_bugfixes-2/KSP.log:12946-12961` — `BeginRewind` is immediately followed by `R #0 "Crater Crawler": disabled — Rewind already in progress`

The local `s3` / LOD archive (`logs/2026-04-11_2339_290-rover-underground/KSP.log`) predates those explicit UI-reason lines, but it does show two long active tree-recording windows:

- `StartRecording succeeded` at `9137`, then the tree is finalized/committed at `11702-12030`
- `StartRecording succeeded` at `12502`, then the tree is finalized/committed at `14659-15003`

That means the long disabled interval reported for the `s3` save lines up with an active recording window; distance is not involved, and the most likely reason in that bundle is the normal `Stop recording before rewinding/fast-forwarding` guard.

The governing code is `RecordingsTableUI` + `RecordingStore.CanRewind/CanFastForward`. Distance/watch state is only used for the `W` button path and should never affect R/FF availability.

**Conclusion:** No real R/FF distance bug found. The runtime behavior was already correct: row/group R/FF enablement comes from recording timing/save/runtime state, while watch distance only gates `W`. In the archived `s3` / LOD save, the long disabled interval tracks active recording; in later reason-logged bundles, the same buttons also legitimately disable during an active rewind.

**Fix:** Added focused coverage for:

- `CanFastForward`: 0-point recordings return `Recording not available`
- `CanFastForward`: current/past recordings return `Recording is not in the future`
- `CanRewind`: tree branches with no root rewind save return `No rewind save available` / `Rewind save file missing`
- transient blocks: `isRecording`, `IsRewinding`, and `HasPendingTree`
- UI-level guard that distance/watch state never changes R/FF enablement
- testable core helpers: `CanFastForwardAtUT` and `CanRewindWithResolvedSaveState` preserve the runtime guard order while making the missing reason cases unit-testable

**Priority:** Medium — current runtime logic is mostly correct, but the failure modes are easy to misread in playtests unless we lock them down with tests and explicit reasoning

**Status:** ~~Fixed~~

---

### ~~T55. AppendCapturedDataToRecording does not copy FlagEvents or SegmentEvents~~

`AppendCapturedDataToRecording` (ParsekFlight.cs:1836) appends Points, OrbitSegments,
PartEvents, and TrackSections, but omits FlagEvents and SegmentEvents. By contrast,
`FlushRecorderToTreeRecording` (ParsekFlight.cs:1675) correctly copies all six lists
including FlagEvents with a stable sort.

This is a pre-existing gap affecting all three call sites of `AppendCapturedDataToRecording`:
- `CreateSplitBranch` line 2137 (merge parent recording with split data at breakup)
- `CreateMergeBranch` line 2283 (merge parent recording with dock/board continuation)
- `TryAppendCapturedToTree` line 1886 (bug #297 fix -- fallback append to tree)

In practice, FlagEvents are rare (only emitted for flag planting, which is uncommon during
breakup/dock/merge boundaries), and SegmentEvents are typically empty in `CaptureAtStop`
because they are emitted into the recorder's PartEvents list, not the SegmentEvents list.
So data loss from this gap is unlikely but possible.

**Fix:** Add FlagEvents and SegmentEvents to `AppendCapturedDataToRecording`, using the same
stable-sort pattern as `FlushRecorderToTreeRecording` (lines 1712-1714). For FlagEvents use
`FlightRecorder.StableSortByUT(target.FlagEvents, e => e.ut)`. SegmentEvents have a `ut`
field and should use the same pattern. Add tests to `AppendCapturedDataTests.cs` verifying
both event types are appended and sorted.

**Fix:** Added FlagEvents and SegmentEvents (with stable sort) to both `AppendCapturedDataToRecording`
and `FlushRecorderToTreeRecording`. Two new tests in `AppendCapturedDataTests.cs`.

**Priority:** Low -- unlikely to cause visible data loss, but should be fixed for correctness

**Status:** ~~Fixed~~

---

### ~~T56. Remove standalone RECORDING format entirely~~

Follow-up to bug #271 (always-tree unification). The runtime now always creates tree recordings, and the injector now produces RECORDING_TREE nodes for all synthetic recordings.

~~Critical subtask (done):~~ Removed the temporary `TreeId != null` skip in `CanAutoSplitIgnoringGhostTriggers`. The existing `RunOptimizationPass` code already added split recordings to `tree.Recordings` and updated `BranchPoint.ParentRecordingIds`; the skip was the only thing preventing tree splits. Added `RebuildBackgroundMap()` after optimization passes for tree consistency. Fixed `TraceLineagePids` to follow chain links so root lineage PID collection works after optimizer splits.

~~Steps 1-5 (done):~~ Deleted `StashPending`/`CommitPending`/`DiscardPending` and the `pendingRecording` slot. Replaced with `CreateRecordingFromFlightData` (factory) and `CommitRecordingDirect` (commit without pending slot). Deleted `MergeDialog.Show(Recording)`, `ShowStandaloneDialog`, `ShowChainDialog`. Deleted standalone RECORDING serialization (`SaveStandaloneRecordings`/`LoadStandaloneRecordingsFromNodes`). Deleted `PARSEK_ACTIVE_STANDALONE` migration shim. Rewrote `ChainSegmentManager.CommitSegmentCore` to use new API. Cleaned ~27 standalone references from ParsekScenario. Updated `FlightResultsPatch` to use `HasPendingTree`. Deleted `GetRecommendedAction`/`MergeDefault`, `AutoCommitGhostOnly(Recording)`, `RestoreStandaloneMutableState`, `isStandalone` flag.

~~Step 6 (done):~~ Collapsed `committedRecordings` into `committedTrees`. Changed `CommittedRecordings` to `IReadOnlyList<Recording>`. Added `AddRecordingWithTreeForTesting` helper. Set `TreeId` on chain segments via `ChainSegmentManager.ActiveTreeId`. `FinalizeTreeCommit` skips already-committed recordings. Migrated ~93 test `.Add()` calls across 24 files.

**Status:** ~~Done~~

**Status:** ~~Fixed (PR #214) -- standalone RECORDING format removed, all recordings are tree recordings, CommittedRecordings is IReadOnlyList~~

---

### ~~T57. EVA spawn-at-end blocked by parent vessel collision~~

EVA recordings created by mid-flight EVA (tree branch) fail to spawn at end because the entire EVA trajectory overlaps with the already-spawned parent vessel. The spawn collision walkback exhausts every point in the trajectory and abandons the spawn.

**Fix:** Added `exemptVesselPid` parameter to `CheckOverlapAgainstLoadedVessels` and threaded it through `CheckSpawnCollisions` and `TryWalkbackForEndOfRecordingSpawn`. New `ResolveParentVesselPid` resolves the parent recording's `VesselPersistentId` via `ParentRecordingId`. EVA spawns skip the parent vessel during collision walkback while still detecting other vessels.

**Status:** ~~Fixed~~

---

### ~~T58. Debris/booster ghost engines show running effects at zero throttle after staging~~

When a booster separates (staging, decouple), the debris recording inherits the engine state from the moment of separation. If the engine was running at separation, the ghost plays back engine FX (flame, smoke) even though the throttle is 0 on the separated stage.

**Root cause:** `MergeInheritedEngineState` added inherited engines to the child's `activeEngineKeys` even when `SeedEngines` had already found the engine part but determined it was non-operational (fuel severed by decoupling). The check at line 1870 only verified the key wasn't in `activeEngineKeys`, not whether `SeedEngines` had already assessed the engine.

**Fix:** Added `allEngineKeys` parameter to `MergeInheritedEngineState`. `SeedEngines` adds ALL engine parts (operational or not) to `allEngineKeys`. If an inherited engine key is in `allEngineKeys` but not `activeEngineKeys`, the child vessel has the engine but it's non-operational -- skip inheritance. Only inherit when the engine wasn't found by `SeedEngines` at all (KSP timing issue).

**Status:** ~~Fixed~~

---

### ~~T59. Rewind save lost after mid-recording EVA branch~~

The R button never appears in the recordings table because `RewindSaveFileName` is lost during the EVA branch flow. `BuildCaptureRecording` copies the filename into `CaptureAtStop` then clears the recorder field. The EVA child recorder never captures one. At commit, only the current (EVA) recorder is checked -- both sources are null.

**Fix:** Extracted `CopyRewindSaveToRoot` (ParsekFlight, internal static) that copies `RewindSaveFileName`, reserved budget (funds/science/rep), and pre-launch budget from a `CaptureAtStop` to the tree's root recording. Called from four sites: `CreateSplitBranch` (the primary T59 fix -- copies at branch time before the EVA recorder takes over), `FinalizeTreeRecordings`, `StashActiveTreeAsPendingLimbo`, and `MergeCommitFlush`. First-wins semantics: root fields are only set if currently empty.

**Status:** ~~Fixed~~

---

## 308. Reserved kerbals appear assignable in VAB/SPH crew dialog

**Observed in:** 0.8.0 (2026-04-12). Reserved kerbals (those whose recordings are playing back as ghosts) appear auto-assigned to vessel seats in the VAB/SPH crew dialog. The player sees them in the crew panel and thinks the reservation system failed.

**Root cause:** KSP's `KerbalRoster.DefaultCrewForVessel` auto-assigns all Available kerbals into command pod seats. Reserved kerbals stay at Available status by design (changing rosterStatus caused tug-of-war bugs with `ValidateAssignments` -- see `CrewDialogFilterPatch` history). The existing `CrewDialogFilterPatch` (prefix on `BaseCrewAssignmentDialog.AddAvailItem`) correctly filters reserved kerbals from the Available crew list, but `DefaultCrewForVessel` runs before that filter, so reserved kerbals are already seated in the manifest. The flight-ready swap (`SwapReservedCrewInFlight` in `CrewReservationManager.cs`) catches this at launch time, but the user sees the wrong crew in the editor.

**Fix:** Added `CrewAutoAssignPatch` (Harmony prefix on `BaseCrewAssignmentDialog.RefreshCrewLists`). Walks the `VesselCrewManifest` before UI list creation and replaces any reserved crew with their stand-ins from `CrewReservationManager.CrewReplacements`. If no stand-in is available, the seat is cleared. Pure decision logic extracted into `DecideSlotAction` (internal static) for testability. 8 unit tests in `CrewAutoAssignPatchTests.cs`. Files: `Patches/CrewAutoAssignPatch.cs`.

**Status:** Fixed

---

## TODO — Nice to have

### ~~T53. Watch camera mode selection~~

**Done.** V key toggles between Free and Horizon-Locked during watch mode. Auto-selects horizon-locked below atmosphere (or 50km on airless bodies), free above. horizonProxy child transform on cameraPivot provides the rotated reference frame; pitch/heading compensation prevents visual snap on mode switch.

### ~~T54. Timeline spawn entries should show landing location~~

Already implemented — `GetVesselSpawnText()` in `TimelineEntryDisplay.cs` includes biome and body via `InjectBiomeIntoSituation()`. Launch entries also include launch site name via `GetRecordingStartText()`.

---

# In-Game Tests

- [x] Vessels propagate naturally along orbits after FF (no position freezing)
- [x] Resource converters don't burst after FF jump
