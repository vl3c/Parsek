# TODO & Known Bugs

Previous entries (225 bugs, 51 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v1.md`.
Entries 272–303 (78 bugs, 6 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v2.md`.

---

# Known Bugs

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

### T6. LOD culling for distant ghost meshes

Don't render full ghost meshes far from camera. Unity LOD groups or manual distance culling.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

### T7. Ghost mesh unloading outside active time range

Ghost meshes built for recordings whose UT range is far from current playback time could be unloaded and rebuilt on demand.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

### T8. Particle system pooling for engine/RCS FX

Engine and RCS particle systems are instantiated per ghost. Pooling would reduce GC pressure with many active ghosts.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

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

## TODO — Nice to have

### ~~T53. Watch camera mode selection~~

**Done.** V key toggles between Free and Horizon-Locked during watch mode. Auto-selects horizon-locked below atmosphere (or 50km on airless bodies), free above. horizonProxy child transform on cameraPivot provides the rotated reference frame; pitch/heading compensation prevents visual snap on mode switch.

### ~~T54. Timeline spawn entries should show landing location~~

Already implemented — `GetVesselSpawnText()` in `TimelineEntryDisplay.cs` includes biome and body via `InjectBiomeIntoSituation()`. Launch entries also include launch site name via `GetRecordingStartText()`.

---

# In-Game Tests

- [x] Vessels propagate naturally along orbits after FF (no position freezing)
- [x] Resource converters don't burst after FF jump
