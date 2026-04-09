# TODO & Known Bugs

Previous entries (225 bugs, 51 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v1.md`.

---

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

### ~~T4. Release automation~~

`scripts/release.py` — builds Release, runs tests, validates version consistency (`Parsek.version` vs `AssemblyInfo.cs`), packages `GameData/Parsek/` zip (DLL + version file + toolbar textures).

**Status:** Done

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

## TODO — Ghost Visuals

### T25. Fairing internal truss structure after jettison

After fairing jettison, the ghost currently shows just the payload and base adapter. KSP's real vessel can show an internal truss structure (Cap/Truss meshes controlled by `ModuleStructuralNodeToggle.showMesh`). The prefab meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can set correctly. A procedural truss mesh was attempted but removed due to insufficient visual quality.

To implement properly: either rescale prefab Cap/Truss meshes from XSECTION data (need to reverse-engineer the mesh unit geometry), or generate higher-fidelity procedural geometry with proper materials.

**Priority:** Low — cosmetic, only visible briefly after fairing jettison

## TODO — Recording Accuracy

### ~~T16. Planetarium.right drift compensation~~

Not needed. Orbital ghost rotation stores vessel orientation relative to the local orbital frame (velocity + radial), not an inertial reference. Playback reconstructs the orbital frame from live orbit state each frame, so any `Planetarium.right` drift is irrelevant.

**Status:** Closed — not needed (orbital frame-relative design is inherently drift-proof)

### ~~T52. Record start/end position with body, biome, and situation~~

Four new fields on Recording: `StartBodyName`, `StartBiome`, `StartSituation`, `EndBiome`. Captured via `ScienceUtil.GetExperimentBiome` at recording start/end. Timeline shows "Landed at Midlands on Mun". Serialized in .sfs metadata (additive, no format version change). Propagated through optimizer splits, chain boundaries, session merge.

**Status:** Fixed (Phase 10)

## TODO — Compatibility

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Last phase of roadmap — v1 targets stock only, mod compat is best-effort

---

# Known Bugs

## ~~46. EVA kerbals disappear in water after spawn~~

Player landed in water, EVA'd 3 kerbals from the pad vessel, but 2 of them disappeared. May be KSP's known behavior of destroying EVA kerbals in certain water situations, or a Parsek crew reservation/dedup conflict.

**Observed in:** Same session as #45. After vessel spawn with crew dedup (3 crew removed from spawn snapshot because they were already on the pad vessel from revert), the pad vessel retained the real crew. Player EVA'd from pad vessel; 2 of 3 kerbals vanished shortly after EVA.

**Root cause:** Same mechanism as #233. `RemoveReservedEvaVessels` deletes EVA vessels whose crew name is in `crewReplacements`, including player-created EVA vessels. The crew names were reserved from committed recording snapshots. When `SwapReservedCrewInFlight` ran (on scene re-entry or recording commit), it found the player's EVA vessels and destroyed them. Cause 1 (KSP water behavior) may also be a factor for some disappearances.

**Fix:** Two guards added to `RemoveReservedEvaVessels`: (1) loaded EVA vessels are skipped entirely — they're actively in the physics bubble, not stale quicksave remnants (bug #46). (2) Vessels whose `persistentId` matches a committed recording's `SpawnedVesselPersistentId` are skipped (bug #233).

**Status:** Fixed

## ~~95. Committed recordings are mutated in several places after commit~~

Recordings should be frozen after commit (immutable trajectory + events, mutable playback state only). Audit found several places where immutable fields on committed recordings are mutated:

1. **`ParsekFlight.cs:993`** — Continuation vessel destroyed: sets `VesselDestroyed = true` and nulls `VesselSnapshot`. Snapshot lost permanently; vessel cannot re-spawn after revert.
2. **`ChainSegmentManager.cs:492`** — EVA boarding: nulls `VesselSnapshot` on committed recording (next chain segment is expected to spawn, but revert breaks that assumption).
3. **`ParsekFlight.cs:2785,3595`** — Continuation sampling: `Points.Add` appends trajectory points to committed recordings. Intentional continuation design, but points from the abandoned timeline persist after revert.
4. **`ParsekFlight.cs:2820-2831`** — Continuation snapshot refresh: replaces or mutates `VesselSnapshot` in-place on committed recordings, overwriting the commit-time position with a later state.
5. **`ParsekFlight.cs:3629-3641`** — Undock continuation snapshot refresh: same pattern for `GhostVisualSnapshot`.
6. **`ParsekScenario.cs:2469-2472`** — `UpdateRecordingsForTerminalEvent`: can still mutate committed recordings that match by vessel name but haven't spawned yet (name collision edge case; spawned recordings are now guarded).

**Fix:** Items 1-2 fixed earlier (snapshot no longer nulled; `VesselDestroyed` flag gates spawn). Item 6 fixed by #94. Items 3-5 fixed with continuation boundary rollback: `ContinuationBoundaryIndex` tracks the commit-time point count, `PreContinuationVesselSnapshot`/`PreContinuationGhostSnapshot` back up pre-continuation snapshots. On normal stop, boundary is cleared (data baked as canonical). On revert/rewind, `RollbackContinuationData` truncates points back to the boundary and restores snapshots. Rollback called from all three revert paths (rewind `ResetRecordingPlaybackFields`, standalone `RestoreStandaloneMutableState`, tree recording reset loop). Bake-in at all 5 normal stop sites (StopAllContinuations, boarding, vessel-switch termination, tree branch, tree promotion, sibling switch). Vessel-destroyed paths intentionally don't bake (revert undoes destruction). Known limitation: save during active continuation bakes implicitly (boundary is `[NonSerialized]`).

**Status:** Fixed

## ~~112. Aeris 4A spawn blocked by own spawned copy — permanent overlap~~

After rewinding and re-entering flight, the Aeris 4A recording's spawn-at-end tried to place a new vessel at the same position where a previously-spawned (but not cleaned up) Aeris 4A already sat. The spawn collision detector correctly blocked it, but because the overlap is permanent (both vessels at the same runway position), this triggered bug #110's infinite retry loop. The log showed `Spawn blocked: overlaps with #autoLOC_501176 at 5m — will retry next frame` repeating every frame for the remainder of the session.

**Root cause:** `CleanupOrphanedSpawnedVessels` recovered one copy, but a second Aeris 4A was loaded from the save and occupied the spawn slot. The duplicate presence is partly caused by bug #109 (cleanup skipped on second rewind, leaving a stale vessel in the save). The spawn system has no dedup against already-present matching vessels.

**Fix:** Three-layer defense: (1) #110 — 150-frame abandon prevents infinite retry. (2) #109 — guard prevents cleanup data loss on second rewind. (3) Defensive duplicate recovery in `CheckSpawnCollisions` — when a collision blocker's name matches the recording's vessel name, recover the blocker once via `ShipConstruction.RecoverVesselFromFlight` then re-check. `DuplicateBlockerRecovered` flag on Recording prevents recovery loops. Also fixed pre-existing gap: `CollisionBlockCount`/`SpawnAbandoned` now reset by `ResetRecordingPlaybackFields`.

**Status:** Fixed

## ~~125. Engine plate covers / fairings not visible on ghost~~

Engine plates (`EnginePlate1` etc.) have protective covers (interstage fairings) that are built by `ModuleProceduralFairing` at runtime — similar to stock procedural fairings but integrated into the engine plate part. These covers were not visible on ghost vessels during playback.

**Fix:** Variant filter fix (PR #124) ensures the correct shroud mesh is cloned. Engine skirts now display correctly on ghost vessels in-game.

**Status:** Fixed

## 132. Policy RunSpawnDeathChecks and FlushDeferredSpawns are TODO stubs

`RunSpawnDeathChecks()` now iterates committed recordings each frame, checks if spawned vessel PIDs still exist via `FlightRecorder.FindVesselByPid`, increments `SpawnDeathCount` on death, and either resets for re-spawn or abandons after `MaxSpawnDeathCycles`. New pure predicate `ShouldCheckForSpawnDeath` in `GhostPlaybackLogic`.

`FlushDeferredSpawns()` moved from `ParsekFlight` to `ParsekPlaybackPolicy`, eliminating the split-brain bug where the policy populated its own `pendingSpawnRecordingIds` in `HandlePlaybackCompleted` but the flush read from ParsekFlight's never-populated duplicate set. The policy now owns the full lifecycle: queue during warp → flush when warp ends.

**Status:** Fixed

## 133. Forwarding properties in ParsekFlight add ~500 lines of indirection

After T25 extraction, ParsekFlight still has forwarding properties (`ghostStates => engine.ghostStates`, `overlapGhosts => engine.overlapGhosts`, etc.) and bridge methods that external callers (scene change, camera follow, delete, preview) use.

**Priority:** Low — tech debt, no functional impact

**Status:** Resolved — removed 6 dead forwarding methods, inlined 4 call sites, removed 2 dead private forwarding properties (`overlapGhosts`, `loopPhaseOffsets` — zero internal callers). 3 remaining properties (`ghostStates`, `activeExplosions`, `loadedAnchorVessels`) have active internal usages.

## ~~154. parsek_38.png texture compression warning~~

KSP warns `Texture resolution is not valid for compression` for the 38x38 toolbar icon. Not a power-of-two size so KSP can't DXT-compress it.

**Fix:** Replaced 38x38 and 24x24 toolbar icons with 64x64 and 32x32 power-of-two versions. Updated references in ParsekFlight, ParsekKSC, and release.py.

**Status:** Fixed

## ~~156. Missing test coverage from lifecycle simulation~~

Areas identified by code path simulation that lack unit tests:

1. `HandleVesselSwitchDuringRecording` with `Stop` decision — decision logic (`DecideOnVesselSwitch`) fully unit tested. Integration path is linear teardown (BuildCaptureRecording → null patch → IsRecording=false), same pattern as every other branch.
2. `CacheEngineModules` with partially-loaded vessel — single `if (p == null) continue` guard. Cannot reliably reproduce partial loading in tests.
3. `CheckAtmosphereBoundary` → `HandleAtmosphereBoundarySplit` chain — predicate `ShouldSplitAtAtmosphereBoundary` fully unit tested. Integration requires crossing atmosphere boundary (70km altitude), not feasible without orbital maneuver.

**Status:** Resolved — all decision logic extracted to pure/static methods and fully unit tested. Remaining gaps are mechanical integration (set flag → read flag → stop/restart) with no complex logic. Risk too low to justify the cost of in-game tests requiring orbital maneuvers or partial vessel loading.

## ~~157. Green sphere ghost for debris after ghost-only merge decision~~

When a debris recording is set to "ghost-only" in the merge dialog, `ApplyVesselDecisions` nulls `VesselSnapshot`. If `GhostVisualSnapshot` was also null (debris destroyed before snapshot copy), `GetGhostSnapshot` returns null and the ghost falls back to a green sphere.

**Partial fix (earlier):** `ApplyVesselDecisions` copies `VesselSnapshot` to `GhostVisualSnapshot` before nulling the spawn snapshot.

**Full fix:** Pre-capture vessel snapshots at split detection time (when debris vessels are still alive) and store them in the CrashCoalescer. When `CreateBreakupChildRecording` runs 0.5s later and the vessel is gone, use the pre-captured snapshot as fallback for both `GhostVisualSnapshot` and `VesselSnapshot`.

**Status:** Fixed

## 159. ~~EVA auto-recordings have no rewind save — R button absent~~

**Status:** Resolved — tree-aware rewind lookup: branch recordings resolve the rewind save through the tree root via `RecordingStore.GetRewindRecording()`. R button now appears for all tree members.

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

## 166. ~~R buttons disabled after tree commit — rewind saves consumed~~

**Status:** Resolved — same fix as #159. Tree branches now resolve the root's rewind save via `RecordingStore.GetRewindRecording()`. `InitiateRewind` and `ShowRewindConfirmation` use the owner recording's fields for correct vessel stripping and UT display.

## ~~185. Investigate spawning idle vessels earlier or trimming recording tail~~

After an EVA, the vessel left behind is a static recording (ghost) right up to the moment the tree was committed, even though it stopped moving much earlier. Consider either spawning the vessel as real when it enters its final resting state, or trimming the end of the recording if nothing changes after the last meaningful event.

**Fix:** `RecordingOptimizer.TrimBoringTail` already handles this for standalone recordings. It was not applying to breakup-continuous tree recordings because `IsLeafRecording` rejected them (had `ChildBranchPointId != null`). Fixed by checking `IsEffectiveLeafForVessel` before rejecting.

**Status:** Fixed

## ~~186. Initial launch recording shows T+ countdown instead of "past" status~~

In the Parsek recordings window, the initial launch recording (parent of a tree) shows "T+5m 23s" in the Status column while child recordings show "Landed". It may be more appropriate to show "past" or the terminal state. Additionally, these tree recordings have no Phase column value.

**Root cause:** Continuation sampling appends trajectory points to committed recordings, extending their `EndUT` past the current time. The status logic (`DrawRecordingRow`, `GetGroupStatus`, `GetStatusOrder`) compared only `now <= rec.EndUT` to classify a recording as "active", without checking whether the recording was already committed with a terminal state.

**Fix:** Added `&& !rec.TerminalStateValue.HasValue` guard to all three status classification paths: individual row display, group/chain aggregate status, and sort key computation. Recordings with a terminal state now always show their terminal state (e.g., "Landed", "Orbiting") regardless of `EndUT`. Group status also picks the best non-debris terminal state instead of always showing "past". Phase column is empty by design for tree roots (different children may have different phases).

**Status:** Fixed

## ~~187. Centralize time conversion system~~

All time formatting (FormatDuration, FormatCountdown, KSPUtil.PrintDateCompact) should use a centralized system that respects the game's calendar settings (day length, year length). Currently FormatDuration hardcodes 6h days / 426d years. Audit all time conversion call sites and unify.

**Fix:** Created `ParsekTimeFormat` static class as single source of truth for calendar constants and time formatting. `FormatDuration` (compact), `FormatDurationFull` (all components), and `FormatCountdown` all respect `GameSettings.KERBIN_TIME`. Replaced 4 duplicate `FormatDuration` implementations (RecordingsTableUI, MergeDialog, TimelineEntryDisplay, ParsekUI) and moved calendar constants from SelectiveSpawnUI. 37 new unit tests covering both Kerbin and Earth calendars.

**Status:** Fixed

## ~~188. Spawned surface vessels clutter map view during ascent~~

During ascent, map view shows green dot icons for past recordings' spawned vessels sitting on the ground. These are real KSP vessels spawned at recording end — they correctly show in map view because they're actual vessels. This is expected behavior — they're real vessels and map view correctly shows them.

**Status:** Closed — not a bug, expected KSP behavior

## 189b. Ghost escape orbit line stops short of Kerbin SOI edge

For hyperbolic escape orbits, KSP's `OrbitRendererBase.UpdateSpline` draws the geometric hyperbola from `-acos(-1/e)` to `+acos(-1/e)` using circular trig (cos/sin), which clips at a finite distance (~12,000 km for e=1.342). The active vessel shows the full escape trajectory to the SOI boundary because it uses `PatchedConicSolver` + `PatchRendering`. Ghost ProtoVessels don't get a `PatchedConicSolver`.

**Options:**
1. Draw a custom LineRenderer through the recording's trajectory points (accurate but significant work)
2. Extend the orbit line beyond the hyperbola asymptote with a straight-line segment to the SOI exit point
3. Give the ghost a `PatchedConicSolver` (complex, may conflict with KSP internals)

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability) — cosmetic, same tier as T25 fairing truss

## ~~194. W (watch) button stays enabled on one booster after separation~~

After booster separation, 3 of 4 boosters correctly have W disabled, but one stays enabled. The watch eligibility check (`HasActiveGhost && sameBody && inRange`) doesn't check `IsDebris`. A debris recording can have an active timeline ghost but shouldn't be watchable.

**Fix:** Added `&& !rec.IsDebris` to the individual recording row watch eligibility check in RecordingsTableUI. Added "Debris is not watchable" tooltip. Group-level W button already filtered debris via `FindGroupMainRecordingIndex`.

**Status:** Fixed

## 196. Ghost icon popup window should appear next to cursor

The popup spawned via `PopupDialog.SpawnPopupDialog` consistently appears at screen center despite attempts to reposition. KSP's `SpawnPopupDialog` forces `localPosition=Vector3.zero` after anchor setup. Need to use the same approach as KSP's `MapContextMenu`: anchor at (0,0), then set `localPosition` via `CanvasUtil.ScreenToUISpacePos`.

**Status:** TODO — deferred

## ~~203. Green dot ghost markers at wrong positions near Mun after scene reload~~

Two compounding issues: (1) `TerminalOrbitBody` is null on all recordings at load time — `HasOrbitData(Recording)` returns false for all 62 recordings, preventing ProtoVessel creation from the initial scan. (2) After FLIGHT→FLIGHT scene reload, ghost map vessel positions jump from Mun-relative (~11M m) to world-frame (~2B m) — the coordinate frame shifts during scene reload and positions aren't corrected.

**Root cause:** `SaveRecordingMetadata` / `LoadRecordingMetadata` (used by standalone recordings) never serialized the 8 terminal orbit fields (`tOrbBody`, `tOrbInc`, `tOrbEcc`, `tOrbSma`, `tOrbLan`, `tOrbArgPe`, `tOrbMna`, `tOrbEpoch`). Tree recordings were unaffected because `RecordingTree.SaveRecordingInto` / `LoadRecordingFrom` already handled these fields. After save/load, all standalone recordings had `TerminalOrbitBody = null`, so `HasOrbitData` returned false and no ghost map ProtoVessels could be created. Issue (2) was a consequence: without valid orbit data, ghost positions computed from stale or zero orbital elements produced world-frame coordinates instead of body-relative.

**Fix:** Added terminal orbit field serialization to `SaveRecordingMetadata` and `LoadRecordingMetadata`, matching the existing pattern in `RecordingTree`.

**Status:** Fixed

## ~~217. Settings window GUILayout exception (Layout/Repaint mismatch)~~

`DrawSettingsWindow` throws `ArgumentException: Getting control N's position in a group with only N controls when doing repaint`. Unity IMGUI bug caused by conditional `GUILayout` calls whose condition changes between Layout and Repaint passes. The window is stuck at 10px height and non-functional. 72 exceptions per session when the settings window is opened.

**Fix:** Removed the early `return` in `DrawGhostSettings` that conditionally skipped ghost cap slider controls when `ghostCapEnabled` was false. Sliders are now always drawn (for IMGUI Layout/Repaint consistency) but grayed out via `GUI.enabled` when caps are disabled.

**Status:** Fixed

## 218. Crash breakup debris not recorded when recorder tears down before coalescer

When a vessel crashes during an active recording, the recorder is stopped and committed before the coalescer's 0.5s window elapses. By the time the coalescer emits the BREAKUP event, there is no active tree or recorder to attach it to. The main vessel recording is saved but the crash debris tree structure is lost.

**Priority:** Low — the vessel recording itself is preserved; only debris ghosts are missing.

**Fix:** `ShowPostDestructionMergeDialog` now waits for the crash coalescer's 0.5s window to expire before stopping the recorder. `TickCrashCoalescer` in Update() naturally emits the BREAKUP while the recorder is still alive, allowing `PromoteToTreeForBreakup` to create the tree with debris child recordings. A 5s real-time timeout (via `Time.unscaledTime`) prevents infinite wait if UT stops advancing (game pause). After tree creation, the continuation recorder is marked `VesselDestroyedDuringRecording = true` (it never saw the original `OnVesselWillDestroy` event) and control redirects to `ShowPostDestructionTreeMergeDialog`.

**Status:** Fixed

## ~~219. Ghost creation fails for orbital debris chain ("no orbit data")~~

`CreateGhostVessel` repeatedly fails for certain orbital debris chains with `no orbit data for chain pid=NNNN`. The orbit segment data exists in the recording but the ghost system cannot access it at creation time. Fires on every flight scene entry.

**Root cause:** `CaptureTerminalOrbit` only runs when `FindVesselByPid` returns a live vessel. Orbital debris with 30s TTL is often destroyed by finalization time.

**Fix:** `PopulateTerminalOrbitFromLastSegment` recovers terminal orbit fields from the last `OrbitSegment` when the vessel is gone at finalization time. Called in `FinalizeIndividualRecording` when vessel is null but recording has orbit segments.

**Status:** Fixed

## 220. PopulateCrewEndStates called repeatedly for 0-point intermediate recordings

Intermediate tree recordings with 0 trajectory points but non-null VesselSnapshot trigger `PopulateCrewEndStates` on every recalculation walk (36 times in a typical session). These recordings can never have crew.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

## ~~224. Vessel not spawned at end of playback when parts break off on splashdown~~

Recording `f8fd04e5` (Kerbal X, chainIndex=1) had both `childBranchPointId` (breakup at UT 102.8, parts broke off on splashdown impact) AND `terminalState = Splashed`. The non-leaf check in `ShouldSpawnAtRecordingEnd` suppressed spawn even though this recording was the effective leaf for its vessel (no same-PID continuation child existed).

**Root cause:** `ProcessBreakupEvent` (foreground breakup handler) sets `ChildBranchPointId` on the active recording but does NOT create a same-PID continuation — by design, the foreground recording continues through breakups. Only debris children are created. The non-leaf check treated all recordings with `ChildBranchPointId` as non-leaf without checking whether a same-PID continuation actually exists.

**Fix:** (1) `IsEffectiveLeafForVessel` checks if any child of the branch point shares the same `VesselPersistentId` — if not, the recording IS the effective leaf. Both non-leaf checks (primary + safety net) skip for effective leaves. (2) `ProcessBreakupEvent` refreshes `VesselSnapshot` post-breakup so the spawned vessel reflects the surviving parts, not the pre-breakup configuration.

**Status:** Fixed

## ~~226. ForceSpawnNewVessel transient flag is fragile~~

`ForceSpawnNewVessel` was a transient (not serialized) flag on Recording, set at scene entry and consumed at spawn time. The flag could be lost if Recording objects were recreated mid-scene (auto-save, quicksave, RecordingStore rebuild).

**Fix:** Replaced per-recording transient flag with a single static `RecordingStore.SceneEntryActiveVesselPid` (set once at scene entry). `SpawnVesselOrChainTip` now checks `rec.VesselPersistentId == SceneEntryActiveVesselPid` to bypass PID dedup statelessly. A complementary `activeVesselSharesPid` runtime check covers mid-scene vessel switches. Removed `ForceSpawnNewVessel` field, `MarkForceSpawnOnActiveVesselRecordings`, `MergeDialog.MarkForceSpawnOnTreeRecordings`, and all flag-setting code.

**Status:** Fixed

## ~~227. Mid-tree spawn entry for vessel with EVA/staging branch~~

When a kerbal EVAs or a stage separates, the tree creates a branch point. The vessel's current recording segment ends at that UT and a continuation recording starts as a tree child. The timeline shows a premature "Spawn: Kerbal X" at the branch time because `IsChainMidSegment` only checks chain segments (optimizer splits), not tree continuation segments.

The root recording has `ChildBranchPointId` set which means it's effectively a mid-tree segment, not a leaf. The vessel should show a single continuous presence from launch to final capsule spawn — EVA kerbals and staging debris are separate branches, not interruptions of the main vessel's timeline.

**Fix:** Added `HasSamePidTreeContinuation` helper to `TimelineBuilder` — flat-list equivalent of `GhostPlaybackLogic.IsEffectiveLeafForVessel`. Two-sided fix: (1) suppress parent spawn when a same-PID continuation child exists, (2) allow tree-child leaf recordings to produce spawn entries when they are the effective leaf for their vessel. Breakup-only recordings (no same-PID continuation) correctly still spawn.

**Status:** Fixed

## ~~228. Crew reassignment entries appear when kerbals EVA~~

When a kerbal EVAs from a vessel, KSP internally reassigns the remaining crew. The game actions system captures these as KerbalAssignment actions, which appear in the detailed timeline view. These are real KSP events but feel redundant — the player didn't decide to reassign crew, KSP did it automatically as a side effect of the EVA.

**Fix:** `TimelineBuilder.BuildEvaBranchKeys` collects `(parentRecordingId, startUT)` pairs from EVA recordings. `CollectGameActionEntries` skips KerbalAssignment actions whose `(RecordingId, UT)` matches an EVA branch key. The EVA entry already communicates the crew change.

**Status:** Fixed

## ~~229. Crew death (CREW_LOST) not shown in timeline~~

When a kerbal dies (e.g., Bob hits the ground without a parachute), the timeline had no entry type for crew death — the event was invisible. The recording's terminal state shows "Destroyed" but the destroyed spawn entry is correctly filtered (can't spawn a destroyed vessel).

**Fix:** Added `TimelineEntryType.CrewDeath` (T1 significance). `CollectRecordingEntries` iterates `rec.CrewEndStates` and emits a "Lost: {name} ({vessel})" entry at `rec.EndUT` for each kerbal with `KerbalEndState.Dead`. Red-tinted display color distinguishes death entries from other timeline items.

**Status:** Fixed

## ~~230. LaunchSiteName leaks to chain continuation segments~~

`FlightDriver.LaunchSiteName` persists from the original launch for the entire flight session. Chain continuation recordings (after dock/undock) that aren't EVAs or promotions picked up the stale value.

**Fix:** `CaptureStartLocation` now checks `BoundaryAnchor.HasValue` — if set, this is a chain continuation, not a fresh launch. Skips launch site capture alongside EVA and promotion guards.

**Status:** Fixed

## ~~231. Vessels and EVA kerbals spawn high in the air at end of recording~~

Vessels and EVA kerbals with `terminal=Landed` spawned at their last trajectory point altitude (still falling), then KSP reclassified from LANDED→FLYING and they fell and crashed. Multiple root causes: (1) EVA recordings returned early from `ResolveSpawnPosition` before altitude clamping; (2) LANDED altitude clamp only fixed underground spawns; (3) KSC spawn path and SpawnTreeLeaves path had no altitude clamping at all; (4) snapshot rotation was from mid-flight descent, not landing orientation.

**Fix:** Merged EVA and breakup-continuous into a single `useTrajectoryEndpoint` path with no early return. LANDED clamp sets `alt = terrainAlt + 2m` clearance (prevents burying lower parts underground while keeping drop minimal). Applied `ResolveSpawnPosition` + `OverrideSnapshotPosition` to all three spawn paths (flight scene, KSC, tree leaves). Snapshot rotation overridden with last trajectory point's `srfRelRotation` for surface terminals. Extracted `ClampAltitudeForLanded` as pure testable method. All 9 `RespawnVessel` call sites audited.

**Status:** Fixed

## ~~232. Green sphere fallback for debris ghosts with no snapshot~~

Debris recordings from mid-air booster collisions have no vessel snapshot. The ghost visual builder falls back to a green sphere. User sees distracting green balls appearing during watch mode playback. KSC ghost path already skips ghosts with no snapshot (`ParsekKSC.cs:473`); flight scene should do the same for debris.

**Fix:** Early return in `SpawnGhost` when `traj.IsDebris && GetGhostSnapshot(traj) == null` — skips ghost creation entirely with a log message. Non-debris keeps sphere fallback as safety net. Confirmed in log: ghosts #8 and #10 ("Kerbal X Debris") were hitting sphere fallback with `parts=0`.

**Status:** Fixed

## ~~233. Spawned EVA vessel deleted by crew reservation on scene re-entry~~

After Parsek spawns an EVA vessel at recording end, switching vessels triggers FLIGHT→FLIGHT scene reload. `CrewReservationManager.RemoveReservedEvaVessels()` re-runs on scene entry: `ReserveCrewIn` re-adds the kerbal to `crewReplacements`, then `RemoveReservedEvaVessels` finds the spawned EVA vessel and deletes it because the kerbal's name is in the replacements dict.

**Root cause:** The reservation system can't distinguish a stale EVA vessel from a quicksave revert (should be removed) from one spawned by Parsek's recording system (should be kept).

**Fix:** `ShouldRemoveEvaVessel` now accepts optional `vesselPid` and `spawnedVesselPids` parameters. `RemoveReservedEvaVessels` builds a `HashSet<uint>` of `SpawnedVesselPersistentId` values from `RecordingStore.CommittedRecordings` via `BuildSpawnedVesselPidSet`. EVA vessels whose PID matches a committed recording's spawned PID are kept. Logs guarded vessel count separately.

**Status:** Fixed

## ~~234. Per-part identity regeneration on spawn~~

`RegenerateVesselIdentity` only regenerates vessel-level `pid` (GUID) and zeroes `persistentId`. Per-part IDs are untouched. LazySpawner's `MakeUnique` regenerates six ID types: vessel `vesselID`, vessel `persistentId` (via `FlightGlobals.CheckVesselpersistentId`), per-part `persistentId` (via `FlightGlobals.GetUniquepersistentId`), per-part `flightID` (via `ShipConstruction.GetUniqueFlightID`), per-part `missionID`, and per-part `launchID` (via `game.launchID++`). Without per-part regeneration, spawned copies share part PIDs with the original vessel, which can cause tracking station/map view conflicts and is likely a contributing factor to #112 (spawn blocked by own copy — duplicate part PIDs persist across spawns).

**Reference:** `docs/mods-references/LazySpawner-architecture-analysis.md` §3 (MakeUnique method)

**Priority:** High — likely root cause or contributing factor for #112 and PID collision issues in multi-spawn scenarios

**Status:** Fixed — `RegeneratePartIdentities` with delegate injection regenerates persistentId, flightID (uid), missionID (mid), and launchID per PART node. Returns old→new PID mapping for robotics patching.

## ~~235. Add IgnoreGForces after ProtoVessel.Load on spawn~~

`RespawnVessel` and `SpawnAtPosition` call `ProtoVessel.Load()` but do not call `vessel.IgnoreGForces(240)` on the newly created vessel. VesselMover demonstrates this is critical: without it, KSP calculates extreme g-forces from the position correction after load and can destroy the vessel immediately. The `MaxSpawnDeathCycles = 3` guard may be treating the symptom of exactly this.

Currently `IgnoreGForces(240)` is only called during ghost positioning (`ParsekFlight.cs:6657`), not after real vessel spawn. A single call right after `pv.Load()` + `pv.vesselRef` validation in both spawn paths could eliminate an entire class of spawn-death cycles.

**Reference:** `docs/mods-references/VesselMover-architecture-analysis.md` §2-3 (g-force suppression)

**Priority:** High — may eliminate spawn-death cycles entirely; low risk (single API call)

**Status:** Fixed — `IgnoreGForces(240)` added after `pv.Load()` in both `RespawnVessel` and `SpawnAtPosition`.

## ~~236. Verify isBackingUp flag in TryBackupSnapshot~~

`TryBackupSnapshot` calls `vessel.BackupVessel()` without explicitly setting `vessel.isBackingUp = true`. LazySpawner explicitly sets this flag because it is required for PartModules to fully serialize their state — without it, some modules silently drop data from the ProtoVessel snapshot. `BackupVessel()` may handle this internally (needs decompilation to verify), but if it doesn't, this could cause incomplete module data leading to broken spawns or ghost visual issues.

**Investigation:** Decompile `Vessel.BackupVessel()` to check whether it sets `isBackingUp` internally. If not, wrap the call:
```csharp
vessel.isBackingUp = true;
ProtoVessel pv = vessel.BackupVessel();
vessel.isBackingUp = false;
```

**Reference:** `docs/mods-references/LazySpawner-architecture-analysis.md` §2 (VesselToProtoVessel)

**Priority:** Medium — needs investigation before deciding if a fix is needed

**Status:** Done — `BackupVessel()` sets `isBackingUp = true` internally (confirmed via decompilation). No fix needed. Added documentation comment in `TryBackupSnapshot`.

## ~~237. Clean up global PID registry on identity regeneration~~

When `RegenerateVesselIdentity` sets `persistentId = "0"`, it does not remove the old part PIDs from `FlightGlobals.PersistentUnloadedPartIds`. LazySpawner explicitly calls `FlightGlobals.PersistentUnloadedPartIds.Remove(snapshot.persistentId)` before reassigning each part's persistent ID. Without cleanup, phantom entries accumulate in the global registry over many spawn/revert cycles in a session.

This is a slow leak: each spawn without cleanup adds stale entries. Over a long play session with many spawns and reverts, it could cause PID allocation collisions or unnecessary memory usage.

**Priority:** Medium — degradation over long sessions, low risk fix

**Status:** Fixed — `CollectPartPersistentIds` extracts old PIDs; `RegenerateVesselIdentity` removes them from `FlightGlobals.PersistentUnloadedPartIds` before reassigning.

## ~~238. Robotics reference patching for Breaking Ground DLC vessels~~

When part `persistentId`s are regenerated during spawn (once #234 is implemented), `ModuleRoboticController` (KAL-1000) references to those parts break because the controller stores part PIDs in its `CONTROLLEDAXES`/`CONTROLLEDACTIONS` ConfigNodes. LazySpawner fixes this by walking module ConfigNodes and remapping old→new PIDs using a `Dictionary<uint, uint>` built during ID regeneration.

Only relevant for vessels with Breaking Ground DLC robotics parts using KAL-1000 controllers. Should be implemented alongside #234.

**Reference:** `docs/mods-references/LazySpawner-architecture-analysis.md` §4 (UpdateRoboticsReferences)

**Priority:** Low-Medium — only affects DLC robotics vessels, but completely breaks their controllers if hit

**Status:** Fixed — `PatchRoboticsReferences` walks MODULE nodes for `ModuleRoboticController`, remaps PIDs in CONTROLLEDAXES/CONTROLLEDACTIONS/SYMPARTS using mapping from #234.

## ~~239. Post-spawn velocity zeroing for physics stabilization~~

VesselMover applies a multi-frame stabilization pattern after spawn: `IgnoreGForces(240)` + `SetWorldVelocity(zero)` + `angularVelocity = zero` + `angularMomentum = zero`. Parsek spawns rely on KSP to settle the vessel naturally after `ProtoVessel.Load()`, which can cause visible physics jitter or bouncing on surface spawns.

Consider a lightweight post-spawn stabilization: zero all velocities on the spawned vessel for 1-2 frames after load. This is more conservative than VesselMover's per-frame approach (which is for interactive repositioning) but would suppress the initial physics impulse from spawn.

**Reference:** `docs/mods-references/VesselMover-architecture-analysis.md` §2 (velocity zeroing)

**Priority:** Low — cosmetic (physics jitter on surface spawn), partially mitigated by #231's rotation fix

**Status:** Fixed — `ApplyPostSpawnStabilization` zeroes linear + angular velocity for LANDED/SPLASHED/PRELAUNCH. `ShouldZeroVelocityAfterSpawn` guards against orbital situations.

## ~~240. Atmospheric ghost markers not appearing in Tracking Station~~

`ParsekTrackingStation.OnGUI` had a terminal state filter (`TerminalState != Orbiting && != Docked → skip`) that blocked atmospheric trajectory markers for SubOrbital, Destroyed, Recovered, and Landed recordings. This meant non-orbital ghosts never showed map markers during their active flight window in the tracking station. Users had to exit and re-enter TS to trigger ghost creation through the lifecycle update path.

Root cause: the terminal state filter was appropriate for proto-vessel ghosts (which need orbital data) but was incorrectly applied to trajectory-interpolated atmospheric markers. The UT range check already handles temporal visibility.

Additionally, proto-vessel ghosts created by deferred commit (merge/approval dialog) took up to 2 seconds to appear because `UpdateTrackingStationGhostLifecycle` only ran on a fixed interval.

**Status:** Fixed — removed terminal state filter from atmospheric marker path. Extracted `ShouldDrawAtmosphericMarker` as testable pure method. Added committed-count change detection in `Update()` to force immediate lifecycle tick after dialog commits.

## 241. Ghost fuel tanks have wrong color variant

Some fuel tanks on ghost vessels display with the wrong color/texture variant during playback. The ghost visual builder clones the prefab model, but KSP fuel tanks can have multiple texture variants (e.g., Orange, White, Gray via `ModulePartVariants`). The variant selection from the vessel snapshot may not be applied to the ghost clone.

**Priority:** Low — cosmetic, ghost shape is correct

## 242. Ghost engine smoke emits perpendicular to flame direction

On some engines, the smoke/exhaust particle effect fires sideways (perpendicular to the thrust axis) instead of along it. The flame plume itself is oriented correctly but the secondary smoke effect has a wrong emission direction. Likely a particle system `rotation` or `shape.rotation` not being transformed correctly when cloning engine FX from the prefab EFFECTS config.

**Priority:** Low — cosmetic, only noticeable on certain engine models

## ~~243. Watch camera does not reset to anchor at distance limit~~

When the ghost passes the user-configured distance limit (e.g. 3000 km set in Settings), the watch camera should snap back to the anchor vessel. Instead it stays on the ghost.

**Observed in:** Mun mission 2026-04-08. Logs in `logs/2026-04-08_mun-mission/`.

**Status:** Fixed — removed unconditional orbital exemption from ShouldExitWatchForCutoff.

## ~~244. ProtoVessel not generated during Mun transit (icon-only the entire way)

While the vessel was travelling from Kerbin to Mun on a transfer orbit, a ProtoVessel ghost was never created — the ghost stayed as an icon-only marker the entire transit. Should have transitioned to orbit-line ProtoVessel once above atmosphere.

**Root cause:** `CheckPendingMapVessels` rate-limited orbit update calls `FindOrbitSegment` which returns null in gaps between orbit segments (normal — every off-rails burn creates a gap). The code at `ParsekPlaybackPolicy.cs:791-796` interprets null as "past all orbit segments" and permanently removes the ProtoVessel + removes the index from `lastMapOrbitByIndex`. The index is never re-added to `pendingMapVessels`, so when the next segment starts (1.4s later), nothing creates a new ProtoVessel. The ghost stays icon-only for the rest of the flight (including the entire Kerbin→Mun transfer orbit and all Mun orbit segments).

**Fix:** When `FindOrbitSegment` returns null, check if there are future orbit segments (`startUT > currentUT`). If so, re-add to `pendingMapVessels` instead of permanently dropping.

**Observed in:** Mun mission 2026-04-08. Logs in `logs/2026-04-08_mun-mission/`.

**Status:** Fixed — re-queue to pendingMapVessels when future orbit segments exist.

## ~~245. Ghost icon position incorrect during warp with Mun focus~~

With focus view set on Mun and warping at slow speed, the ghost icon position was incorrect (not tracking the vessel's actual trajectory).

**Observed in:** Mun mission 2026-04-08.

**Root cause:** Hidden ghosts (beyond visual range, `SetActive(false)`) have stale `transform.position` after FloatingOrigin shifts. `DrawMapMarkers` drew markers at stale world-space positions.

**Status:** Fixed — skip `!activeSelf` ghosts in DrawMapMarkers. Same fix as #247.

## ~~246. EVA on Mun generates multiple "Mun approach" recordings instead of one EVA recording

Bob's EVA on the Mun surface generated a bunch of "Mun approach" segment recordings instead of a single EVA recording. The atmosphere/altitude boundary splitting logic is firing incorrectly on the surface of an airless body.

**Root cause:** EVA kerbal on Mun surface oscillates between `LANDED` and `FLYING`/`SUB_ORBITAL` situations during walks/hops. `EnvironmentDetector.Classify` checks `situation` first — when LANDED, returns Surface correctly. But when KSP briefly flips to FLYING (EVA physics jitter, jetpack hops), the function falls through the surface check, skips the atmosphere check (Mun has no atmosphere), and hits the airless approach check: `altitude (2785m) < approachAltitude (25000m)` → returns `Approach`. The 0.5s Surface↔Approach debounce is too short, producing 16 alternating Surface/Approach track sections over a 455s EVA. The optimizer then splits at each environment-class boundary.

**Fix:** In `EnvironmentDetector.Classify`, force `Surface` when altitude is very low above terrain on an airless body (e.g., < 100m AGL) regardless of KSP's vessel situation. Also increase the Surface↔Approach debounce to match `SurfaceSpeedDebounceSeconds` (3.0s).

**Observed in:** Mun mission 2026-04-08.

**Status:** Fixed — near-surface override (< 100m AGL on airless body) + increased Surface↔Approach debounce to 3.0s.

## ~~247. Ghost icons show in Mun orbit for landed vessel and EVA kerbal

During the EVA on the Mun surface, the map icons for KerbalX and Bob appeared in Mun orbit instead of on the surface. The ProtoVessel ghost orbital elements don't represent the surface position correctly.

**Observed in:** Mun mission 2026-04-08.

**Status:** Fixed — same root cause and fix as #245 (stale transform.position on hidden ghosts).

## ~~248. EVA boarding misclassified~~ as vessel destruction (Bob shows Destroyed after boarding)

Bob's first EVA recording on the Mun gets `terminal=Destroyed` instead of `terminal=Boarded` because the boarding event is misclassified as a normal vessel switch in tree mode.

**Root cause:** Race condition — no physics frame runs between `onCrewBoardVessel` and `onVesselChange`. `DecideOnVesselSwitch` (in `OnPhysicsFrame`) never executes, so `ChainToVesselPending` is never set. `OnVesselSwitchComplete` falls through to the generic tree vessel-switch path: transitions the EVA vessel to background, KSP destroys the EVA vessel (standard boarding behavior), `DeferredDestructionCheck` sees the vessel is gone, `IsTrulyDestroyed` returns true → `TerminalState = Destroyed`. The `pendingBoardingTargetPid` was set correctly by `onCrewBoardVessel` but `OnVesselSwitchComplete` never checks it. The boarding confirmation expires unused 10 frames later.

**Fix:** In `OnVesselSwitchComplete`, check `pendingBoardingTargetPid != 0 && recorder.RecordingStartedAsEva` before the `ChainToVesselPending` guard at line 1333. If detected, either set `ChainToVesselPending = true` so `HandleTreeBoardMerge` runs normally, or handle the boarding transition inline (flush EVA data, set `TerminalState.Boarded`, create the merge branch).

**Key locations:** `ParsekFlight.OnVesselSwitchComplete` (line 1302), `FlightRecorder.DecideOnVesselSwitch` (line 5312, correct but never runs), `ParsekFlight.HandleTreeBoardMerge` (line 4232, sets Boarded but never invoked), `DeferredDestructionCheck` (line 3050, incorrectly classifies as destruction).

**Observed in:** Mun mission 2026-04-08.

**Status:** Fixed — check pendingBoardingTargetPid in OnVesselSwitchComplete before ChainToVesselPending guard.

## ~~249. Planted flag not visible during ghost playback~~

While watching the Mun recording, a flag planted during the original EVA did not appear during playback. The flag was correctly captured (`FlagEvent` in recording) but never spawned because `ApplyFlagEvents` was gated behind the `hiddenByZone` early return in `RenderInRangeGhost`. The ghost was in the Beyond zone (Mun from Kerbin = 11.4 Mm).

**Root cause:** Flag events are fundamentally different from visual part events (mesh toggles) — they spawn permanent world vessels. They were incorrectly treated as visual effects and skipped when the ghost was hidden.

**Status:** Fixed — moved `ApplyFlagEvents` before the zone-based rendering skip in `RenderInRangeGhost`.

## ~~250. End column shows "-" for almost all recordings~~

In the recordings window expanded stats, almost all recordings show "-" in the End column. Only the final mission recordings (leaves) have an end entry. Interior tree recordings and chain mid-segments have null `TerminalStateValue` by design — only leaf recordings get terminal states.

**Root cause:** `FormatEndPosition` returns "-" when `TerminalStateValue` is null. This is correct for individual recordings, but the UI should propagate the leaf's terminal state to chain groups or show the chain tip's end position. Alternatively, chain mid-segments could inherit the next segment's start position as their end.

**Observed in:** Mun mission 2026-04-08.

**Status:** Fixed — FormatEndPosition falls back to SegmentBodyName + SegmentPhase for mid-segments.

## ~~251. Recording phase label not updated after SOI change back to Kerbin~~

On return from Mun, after exiting the Mun's SOI back into Kerbin's SOI, the recording status/phase should show "Kerbin exo" (exoatmospheric around Kerbin). Instead it still shows the Mun phase or no phase.

**Root cause:** `OnVesselSOIChanged` closed the orbit segment but not the TrackSection. A single section spanned both SOIs with the same environment class (ExoBallistic). The optimizer only split on environment class changes, so no split occurred. `SegmentBodyName` derived from `Points[0].bodyName` used the old SOI body.

**Status:** Fixed — close/reopen TrackSection at SOI boundary + split on body change in optimizer.

## ~~252. Recording groups have no hide checkbox~~

Group headers in the recordings window do not have a hide checkbox to toggle hide for all recordings in the group at once. Only individual recordings have hide toggles.

**Status:** Fixed — group hide checkbox now toggles Hidden on all member recordings.

## 253. Kerbin texture disappears during capsule descent watch at ~1100 km

While watching the recording of capsule descent, the Kerbin terrain/atmosphere texture disappeared when the camera anchor was approximately 1100 km from the camera. This is a KSP/Unity scaled-space transition issue: KSP switches between the high-detail terrain mesh and the scaled-space sphere at a distance threshold that depends on camera position relative to the body. When the watch camera is anchored on a vessel far from the ghost (which is near Kerbin), the camera-to-body distance calculation may exceed KSP's scaledSpace transition threshold, causing the terrain to unload.

**Observed in:** Mun mission 2026-04-08.

**Priority:** Low — KSP engine limitation, not fixable from mod code without overriding PQS/scaledSpace transition logic. Workaround: reduce watch cutoff distance in Settings.

## ~~254. Capsule spawned with wrong crew (Siemon instead of Jeb)~~

At the end of the mission, the capsule was spawned with Siemon Kerman inside instead of Jebediah Kerman. The crew reservation/swap system assigned the wrong replacement, or the snapshot crew data was incorrect.

**Root cause:** Double-swap cascade. (1) First recording commits: Jeb reserved → Leia hired as depth-0 stand-in → live vessel swapped Jeb→Leia. (2) Second recording captures the vessel snapshot which now contains Leia (the stand-in, not Jeb). (3) Second recording commits: `PopulateCrewEndStates` sees Leia as a real crew member → reserves Leia → generates Siemon as Leia's depth-0 stand-in → live vessel swapped Leia→Siemon. The recording system doesn't know that Leia is a temporary replacement for Jeb — it treats stand-in names as real crew.

**Fix:** In `PopulateCrewEndStates` or the snapshot capture path, reverse-map stand-in names back to their original kerbals using `crewReplacements` before creating new reservations. If a crew member in the snapshot is already a known replacement, use the original kerbal's name instead.

**Observed in:** Mun mission 2026-04-08.

**Status:** Fixed — reverse-map stand-in names through CrewReplacements before inferring end states.

## ~~255. Engine FX killed during ghost playback after booster decouple~~

During ghost playback of a Kerbal X, the first two booster pairs decouple with engine FX visible, but when the last pair separates and after, all engine FX are off — the Mainsail plume disappears even though it should still be firing.

**Root cause:** `StopRecordingForChainBoundary` calls `FinalizeRecordingState(emitTerminalEvents: true)` which appends `EngineShutdown` events for all active engines (including the Mainsail). When the joint break is classified as `DebrisSplit` (boosters = uncontrolled debris), `ResumeAfterFalseAlarm` continues recording but does not remove the orphaned shutdown events from `PartEvents`. Each booster decouple adds another orphaned `EngineShutdown` for the Mainsail.

**Status:** Fixed — save `PartEvents.Count` before finalization, truncate back in `ResumeAfterFalseAlarm`.

## TODO — Nice to have

### T53. Watch camera mode selection

Allow the player to change the camera mode during ghost watch playback. Currently the camera is always fixed-orientation relative to the ghost. A mode where the ground is oriented at the bottom of the screen (horizon-locked) would be more intuitive for atmospheric flight and surface operations.

**Priority:** Nice-to-have

### T54. Timeline spawn entries should show landing location

Some timeline "Spawn:" lines show generic "Landed" without specifying where. Should include biome and body context when available, matching the recordings table End column format.

**Priority:** Low — timeline display completeness

## 256. EVA recording runaway sampling — 138K points in 33 seconds

"Bob Kerman" EVA recording (rec[28]) produced 138,648 trajectory points in 33 seconds (4,200 pts/sec). The adaptive sampler should cap at ~3-5 pts/sec. This single recording is 86.9 MB — 93% of the save's total Parsek storage and the primary cause of the 8.7-second initial load time.

**Likely cause:** EVA physics jitter on the surface causes velocity direction to oscillate every frame, defeating the 2-degree direction-change threshold in `ShouldRecordPoint`. The speed-change threshold (5%) may also trigger constantly during surface contact bouncing.

**Investigate:** Check `FlightRecorder.ShouldRecordPoint` behavior during EVA on surfaces. May need EVA-specific sampling overrides (larger thresholds or minimum interval floor for EVA vessels).

**Priority:** High — directly causes performance and storage issues

## 257. Orbit segment with negative SMA (hyperbolic escape)

In-game test `OrbitSegmentBodiesValid` fails: "Orbit segment for 'Mun' has non-positive SMA=-931047.895195401". Negative SMA is physically correct for hyperbolic orbits (eccentricity > 1) but the test asserts positive SMA.

**Root cause:** Test assertion bug. Hyperbolic escape orbits have negative SMA by definition. `CreateOrbitSegmentFromVessel` correctly copies `v.orbit.semiMajorAxis`.

**Fix:** Test now skips the `SMA > 0` assertion when `eccentricity > 1.0` (hyperbolic orbit).

**Status:** Fixed

## 258. Non-chronological trajectory points in recording

In-game test `CommittedRecordingsHaveValidData` fails: recording `ab105395ae5547b0b70c1eb9bb41ca9f` (Bob Kerman EVA) has point 159 UT going backward by ~33 seconds. Trajectory points should be monotonically increasing in UT.

**Root cause:** Quickload during recording resets game time to quicksave UT, but the recorder continued appending points without trimming the stale future-timeline data. `CommitRecordedPoint` and `SamplePosition` had no time monotonicity guard.

**Fix:** Added `TrimRecordingToUT` method to `FlightRecorder`. Both `CommitRecordedPoint` and `SamplePosition` now detect time regression (UT going backward by >1s) and trim stale points, part events, and orbit segments before appending the new point.

**Status:** Fixed (prospective — existing corrupted recording data will still fail; fix prevents recurrence)

## 259. Orbital recordings missing TerminalOrbitBody

In-game test `OrbitalRecordingsHaveTerminalOrbit` reports 4 orbital recordings without `TerminalOrbitBody` set. This is a regression guard for #203/#219.

**Root cause:** Three code paths set `TerminalStateValue` to Orbiting/Docked without also calling `CaptureTerminalOrbit`: (1) `CaptureSceneExitState` in ParsekFlight, (2) vessel-switch chain termination in ChainSegmentManager, (3) debris recording end in BackgroundRecorder. Then `FinalizeIndividualRecording` skips orbit capture because `TerminalStateValue.HasValue` is already true.

**Fix:** Added `CaptureTerminalOrbit` calls to all three source paths. Added defensive backfill in `FinalizeIndividualRecording`: when `TerminalStateValue` is Orbiting/SubOrbital/Docked but `TerminalOrbitBody` is null, try vessel orbit capture or fall back to last orbit segment.

**Status:** Fixed

## ~~260. Remove .pcrf ghost geometry scaffolding~~

`.pcrf` (ghost geometry cache) was planned to cache pre-built ghost meshes to avoid PartLoader resolution on every spawn. Never implemented — `GhostVisualBuilder` always builds from the vessel snapshot directly. Fields were loaded from ConfigNode (`ghostGeometryPath` / `ghostGeometryAvailable` / `ghostGeometryError`) but never written, so they always defaulted on next save anyway.

**Fix:** Deleted `BuildGhostGeometryRelativePath` from `RecordingPaths`; removed `GhostGeometryRelativePath` / `GhostGeometryAvailable` / `GhostGeometryCaptureError` fields from `Recording`; removed `.pcrf` from `RecordingStore.RecordingFileSuffixes` (orphan-detection); removed `geometryFileBytes` from `StorageBreakdown`; removed the `.pcrf` stat call from `DiagnosticsComputation.ComputeStorageBreakdown`; removed the no-op load blocks from `ParsekScenario` and `RecordingTree`; removed the `RecordingOptimizer.MergeInto` / `SplitAtSection` invalidation lines (no field to clear). 5 test sites updated, 4 doc files updated. Stale `.pcrf` files left over from old saves get cleaned up as orphans by the existing scan path.

**Status:** Fixed

## ~~261. Diagnostics playback budget shows 0.0 ms instead of N/A on first frame~~

The diagnostics report showed "Playback budget: 0.0 ms avg, 0.0 ms peak (0.0s window)" instead of "N/A" in the rolling-window edge case.

**Root cause:** Two layers. (1) `FormatReport` checked `playbackFrameHistory.IsEmpty` against the **live** buffer but formatted values from a **stale** snapshot — race window where buffer is non-empty but snapshot is from when it wasn't. (2) Even reading the snapshot, the existing avg/peak/window fields can't distinguish "no data" from "data is genuinely 0.0 ms" when the buffer has entries that are all *outside* the 4 s rolling window: `ComputeStats` writes 0/0/0 and returns, but `IsEmpty` says false.

**Fix:** Added `playbackEntriesInWindow` to `MetricSnapshot`, populated from a new 4th `out` parameter on `RollingTimingBuffer.ComputeStats`. `FormatReport` now reads `snapshot.playbackEntriesInWindow > 0` instead of querying the live buffer — it's a pure function of its snapshot argument for the playback line. New regression test `E10b_StaleEntriesOutsideWindow_FormatShowsNA` covers the buffer-non-empty-but-window-empty case; new `RollingTimingBuffer` test `ComputeStats_AllEntriesOutsideWindow_ReportsZeroEntries` covers the underlying primitive.

**Status:** Fixed

## ~~262. Diagnostics missing _vessel.craft warnings for tree sub-recordings~~

Tree continuation recordings, ghost-only-merged debris, and chain mid-segments legitimately have null `VesselSnapshot` / `GhostVisualSnapshot` in memory, and `RecordingStore.SaveRecordingFiles` only writes `_vessel.craft` / `_ghost.craft` when the corresponding in-memory snapshot is non-null. The diagnostics storage scan was warning "Missing sidecar file" for every such recording on every scan.

**Fix:** New pure predicate `DiagnosticsComputation.ShouldExpectSidecarFile(rec, type)` mirrors the save-side write conditions: `.prec` always expected, `_vessel.craft` only when `rec.VesselSnapshot != null`, `_ghost.craft` only when `rec.GhostVisualSnapshot != null`. `SafeGetFileSize` gained a `warnIfMissing` parameter; `ComputeStorageBreakdown` passes `false` for the snapshot files when the predicate says no file is expected. `.prec` keeps warning on missing (always written = always expected). 4 unit tests for the predicate (trajectory always, vessel gated, ghost gated, null recording).

**Status:** Fixed

## ~~263. Ghost mesh inaccurate after decoupling boosters~~

During the 2026-04-09 KerbalX playtest, the ghost visual showed 3 boosters still attached to the final stage even after they should have decoupled. The snapshot held all 6 radial decouplers, but only 3 `Decoupled` PartEvents ended up in the committed `.prec` file, so `HidePartSubtree` hid only 3 of the 6 booster subtrees at playback.

**Root cause:** When a symmetry group of radial decouplers fires, the 6 individual `onPartJointBreak` events race with KSP's vessel-split processing (`Part.decouple()` creates the new vessel and calls `CleanSymmetryVesselReferencesRecursively` before `PartJoint.OnJointBreak` fires). Events captured by `FlightRecorder.OnPartJointBreak` then travel through `StopRecordingForChainBoundary` → `ResumeAfterFalseAlarm` → tree promotion → `FlushRecorderToTreeRecording`, and somewhere in that pipeline half of the events consistently disappear. Exact drop point was not conclusively identified from logs alone (all 6 events appear in the verbose `[Recorder] Part event: Decoupled` output, but only 3 per pair end up in `tree.PartEvents` at `SessionMerger` time).

**Fix:** Deterministic safety net in `DeferredJointBreakCheck`. After classifying a joint break as `DebrisSplit`/`StructuralSplit` and calling `ResumeSplitRecorder`, scan `newVesselPids` and emit a `Decoupled` PartEvent for each new debris vessel's root part via `FlightRecorder.RecordFallbackDecoupleEvent`. Every new debris vessel has exactly one root part, and that root is by construction the part that separated from the recording vessel, so emitting a `Decoupled` event for it hides the correct subtree through `HidePartSubtree`. Must run *after* `ResumeSplitRecorder` so the fallback events survive `ResumeAfterFalseAlarm`'s terminal-event trim.

**Dedup source:** scans `PartEvents` directly for an existing `Decoupled` entry with matching pid, NOT the parallel `decoupledPartIds` tracking set. PR #161 review (by code-reviewer) caught that the original implementation checked `decoupledPartIds`, which `OnPartJointBreak` populates at the same time as `PartEvents.Add` — so if all 6 events appeared in the verbose log (as they did in the 2026-04-09 playtest), they were all in `decoupledPartIds` and the fallback would silently skip every recovery attempt. `decoupledPartIds` is never pruned when events are stripped downstream, so it can drift out of sync with the serialized list. The fallback must check what will actually be serialized. `PartEvents` is small at deferred-check time (tens of entries), so the linear scan is negligible.

When `OnPartJointBreak` already captured all events naturally and they survived the pipeline, the fallback is a no-op. When events are dropped anywhere between OnPartJointBreak and file write, the fallback recovers them from the authoritative post-split vessel topology.

**Key locations:**
- `FlightRecorder.RecordFallbackDecoupleEvent` — `internal`, scans `PartEvents` for dedup (testable)
- `ParsekFlight.EmitFallbackDecoupleEventsForNewVessels` — resolves new vessels (live + captured), emits events
- `ParsekFlight.DeferredJointBreakCheck` — calls the fallback after `ResumeSplitRecorder`

**Tests:** `Bug263DecoupleFallbackTests.cs` — 7 unit tests, critically including `RecordFallbackDecoupleEvent_RecoversEvent_WhenPartEventsDroppedButDecoupledSetStale` which fails deterministically against the pre-review dedup logic (regression guard for the review-identified gap).

**Status:** Fixed

## ~~264. EVA kerbal not spawned at exact recorded final position~~

During the 2026-04-09 Butterfly Rover playtest, Valentina's EVA ended near the rover. When her ghost vessel was spawned from the recording, she was placed on top of the rover instead of at her exact recorded final position. The two terminal positions were ~170 m apart (lat/lon differ by ~0.0009°/0.0012°), so spawn should have been clearly distinct.

**Root cause (verified via decompile of `ProtoVessel.Load`, `Vessel.LandedOrSplashed`, `OrbitDriver.updateFromParameters`):** `ProtoVessel.Load` correctly sets `vesselRef.transform.position` from the snapshot's lat/lon/alt on frame 0 — so `OverrideSnapshotPosition` is not in itself the bug. The bug fires one frame later: `OrbitDriver.updateMode` is initialized from `Vessel.LandedOrSplashed` at load time, and when the loaded EVA is classified as `FLYING` / `SUB_ORBITAL` / `ORBITING` (any residual `srfSpeed` from walking, a jetpack drift, or a hop puts it there — only perfectly still kerbals get `LandedOrSplashed == true`) the driver runs in `UPDATE` mode. On the first physics tick, `OrbitDriver.updateFromParameters` fires `vessel.SetPosition(body.position + orbit.pos − localCoM)` reading `orbit.pos` from the snapshot's **stale** ORBIT Keplerian elements, which were captured when the kerbal was still on the parent vessel's ladder, and overwrites the corrected transform with the parent position. `KerbalEVA.autoGrabLadderOnStart` (the original suspicion) is trigger-collider driven and can't fire at 170 m — hypothesis eliminated.

**Fix:** Route EVA (and breakup-continuous) spawns through `VesselSpawner.SpawnAtPosition` (`VesselSpawner.cs:115`) — the path added in #171 for orbital spawns — which rebuilds the ORBIT subnode from the endpoint's lat/lon/alt + last-point velocity before `ProtoVessel.Load` runs. With a coherent orbit, `updateFromParameters` on the first physics tick reads the *correct* position from the *correct* orbit and nothing overwrites the transform. Extract `OverrideSituationFromTerminalState` helper that generalizes the existing FLYING → ORBITING override (#176) to also cover FLYING → LANDED/SPLASHED so a walking kerbal at alt > 0 is classified as LANDED and gets `updateMode = IDLE` (belt-and-suspenders for the same failure mode). Add optional `Quaternion? rotation = null` parameter to `SpawnAtPosition` so breakup-continuous spawns preserve `lastPt.rotation`. Remove the `!isEva` guard on bounding-box collision checks in `CheckSpawnCollisions` so the spawn-collision safety net applies to EVA kerbals too. Add a subdivided trajectory walkback (`SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided` + `VesselSpawner.TryWalkbackForEndOfRecordingSpawn`) that steps backward from the endpoint with 1.5 m linear lat/lon/alt sub-steps until a collision-free candidate is found, much finer than the pre-existing point-granularity `WalkbackAlongTrajectory` used by `VesselGhoster` for chain-tip spawns. Walkback triggers immediately on first collision (no 5 s timeout — end-of-recording spawns have no running ghost to extend during a wait). On exhaustion, sets `SpawnAbandoned = true` and the new transient `Recording.WalkbackExhausted = true`. Keeps the existing `OverrideSnapshotPosition` calls on the EVA and breakup-continuous paths as defense-in-depth for the `RespawnVessel` fallback (if `SpawnAtPosition` returns 0 the fallback still places the kerbal at the recorded lat/lon/alt on frame 0, even if the stale-orbit bug re-fires on frame 1 — acceptable for a degraded fallback).

**Status:** Fixed for the `VesselSpawner.SpawnOrRecoverIfTooClose` in-flight spawn path. Partial closure — see Follow-ups.

**Follow-ups (separate PRs):**
- `VesselGhoster.TryWalkbackSpawn` at `VesselGhoster.cs:421-432` uses the same old lat/lon/alt override pattern (writes lat/lon/alt, calls `RespawnVessel`) — safe for LANDED chain tips where `updateMode = IDLE` but potentially broken for FLYING tips. Should migrate to `SpawnAtPosition` + `WalkbackAlongTrajectorySubdivided`.
- `ParsekFlight.SpawnTreeLeaves` at `ParsekFlight.cs:5926-5991` (scene-load tree leaf spawn) doesn't route EVA through `SpawnAtPosition` either. Same stale-orbit bug reproduces when resuming a saved game where a tree has a leaf EVA waiting to spawn.
- `ParsekKSC.cs:780-847` (KSC-scene spawn path) uses the same old `OverrideSnapshotPosition` + `RespawnVessel` pattern. An EVA recording that's still pending spawn when the player returns to KSC routes through this path. Low probability but not zero.
- `VesselSpawner.StripEvaLadderState` (`VesselSpawner.cs:1034`) writes the literal FSM state `"idle"` which is not a valid `KerbalEVA` state name (real names are `st_idle_gr` / `st_idle_fl` / `st_swim_idle`). `StartEVA` catches the unknown-state exception and falls back to a `SurfaceContact`-driven default state so this is functionally correct but cosmetically broken. Either use a real state name or remove the assignment entirely and let the fallback handle it.
- Triple-correction-layer cleanup: `CorrectUnsafeSnapshotSituation` (runs in `PrepareSnapshotForSpawn`) corrects `snapshot.sit`, then `SpawnAtPosition.DetermineSituation` ignores `snapshot.sit` and recomputes, then `OverrideSituationFromTerminalState` is a third layer. Replacing `DetermineSituation` with "read corrected `snapshot.sit` first, fall through to altitude/velocity classifier only if still FLYING" would produce a cleaner invariant with no new helper.

## 265. Ghost audio + BackgroundRecorder seed-skip — in-game test coverage gap

xUnit can't exercise any code path that touches `UnityEngine.AudioSource` (directly or transitively via the `audioInfos` foreach) because the test runner can't load `UnityEngine.AudioModule.dll` — attempts produce *"ECall methods must be packaged into a system module"* even for a null-state early return. This blocks unit test coverage for:

- `GhostPlaybackLogic.PauseAllAudio` / `UnpauseAllAudio` null-guard and iteration paths
- `GhostPlaybackEngine.PauseAllGhostAudio` / `UnpauseAllGhostAudio` loop paths
- `BackgroundRecorder.InitializeLoadedState` seed-event skip predicate (needs a live Vessel + tree, not just the `PartEvents.Count > 0` check which would be tautological)
- `ParsekFlight.FinalizeIndividualRecording` backfill order-of-operations (#259 fix) — needs a live Vessel

**Fix plan:** add `InGameTest` coverage under `Source/Parsek/InGameTests/` that runs inside a live KSP runtime where these types actually work. Specifically:

- A category under `[InGameTest(Category = "GhostAudio")]` that spawns a ghost with a known audio source, fires `GameEvents.onGamePause`, asserts the audio source is paused, fires `onGameUnpause`, asserts resume.
- A `FinalizeTreeBackfill` in-game test that constructs a recording in memory with `TerminalStateValue = Orbiting` but empty `TerminalOrbitBody` and a mock orbit segment, runs the finalize path, asserts `TerminalOrbitBody` is populated via the fallback.
- A `BackgroundRecorderSeedSkip` in-game test that initializes a background state on a recording that already has part events, asserts no duplicate seed events were emitted.

**Priority:** Low — the code paths are simple enough that review caught the issues in commit 77bce7c, and the production playtest will exercise them end-to-end. In-game tests harden against future regressions.

## 266. Tree-preservation on vessel switch (quickload-resume follow-up)

PR #160 routes `isVesselSwitch` through `FinalizePendingLimboTreeForRevert` to match mainline behavior, but the ideal outcome on a vessel switch (user clicks a distant vessel triggering FLIGHT→FLIGHT scene reload) is to keep the active tree alive with the old vessel backgrounded, so the mission doesn't fragment.

**Fix plan:** On `Limbo + isVesselSwitch`, instead of finalizing the tree, move the old active recording's PID into `tree.BackgroundMap`, leave the tree in place (not stashed), install it back as `ParsekFlight.activeTree`, and start a fresh recorder only if the new active vessel has its own recording context. The next vessel switch back to the old vessel triggers `PromoteFromBackground` via the existing in-session switch path. Requires careful handling of the background recorder subscription lifecycle.

**Priority:** Medium — better than finalizing but not a regression, so can wait

## 267. Quickload-resume: restore coroutine reentrancy guard

The `ScheduleActiveTreeRestoreOnFlightReady` flag and the `RestoreActiveTreeFromPending` coroutine (`ParsekFlight.cs`) don't have a re-entry guard. If `onVesselChange` or another reactive handler mutates `activeTree` or the pending tree during the coroutine's 3-second vessel wait, the restore could see inconsistent state. Documented in the design doc's Risks section.

**Fix plan:** Add `static bool restoringActiveTree` guard on `ParsekFlight`. Set at coroutine start, clear on completion or failure. `onVesselChange` / `OnVesselSwitchComplete` handlers check the flag and skip tree mutations while the restore is running. Also consider using a one-shot check in `RestoreActiveTreeFromPending` that refuses to run if `activeTree != null` when it enters (meaning someone beat it to the slot).

**Priority:** Low — no reported issue yet, but prevents a hard-to-diagnose race

## 268. Quickload-resume: snapshot preservation through revert finalization

The old `CommitTreeRevert` preserved vessel snapshots so the merge dialog could offer respawn. `FinalizePendingLimboTreeForRevert` (PR #160) does not — by OnLoad time the vessel refs are gone and we fall back to whatever snapshots were stashed before the scene reload, which may be stale or null. Affects autoMerge=off + revert + dialog-driven respawn.

**Fix plan:** Capture a live snapshot of the active vessel inside `StashActiveTreeAsPendingLimbo` (which runs during `OnSceneChangeRequested`, while the vessel is still referenceable), and attach it to the tree's active recording so the merge dialog can use it if the Limbo tree takes the finalize path. Costs one snapshot copy per scene reload but preserves the existing UX.

**Priority:** Low — regression only visible on autoMerge=off reverts, assess after playtest

## 269. In-game test coverage for quickload-resume flow

PR #160 ships with 29 unit tests covering static state transitions, dispatch decisions, and isolated predicates, but the full scenario — quicksave → scene reload → quickload → restore coroutine → resumed recording appends to same chain segment — can only run inside live KSP.

**Fix plan:** Add an `InGameTests/RuntimeTests.cs` category `[InGameTest(Category = "QuickloadResume", Scene = GameScenes.FLIGHT)]` covering: (1) quickload mid-recording resumes with same `activeRecordingId` and points match pre-F5 UT; (2) real revert finalizes the tree (merge dialog if autoMerge off); (3) vessel switch finalizes the tree (temporary — update when #266 lands); (4) double-F9 idempotency; (5) quickload into a non-flight scene does not try to restore; (6) rewind path (via R button) does not conflict with restore; (7) cold-start "resume saved game" triggers restore via OnFlightReady.

**Priority:** Medium — the Unity-dependent gaps in PR #160's unit tests should close before the next major refactor

## 270. Sidecar file (.prec) version staleness across save points

Latent pre-existing architectural limitation of the v3 external sidecar format: sidecar files (`saves/<save>/Parsek/Recordings/*.prec`) are shared across ALL save points for a given save slot. If the player quicksaves in flight at T2, exits to TS at T3 (which rewrites the sidecars with T3 data), then quickloads the T2 save, the .sfs loads the T2 active tree metadata but `LoadRecordingFiles` hydrates from T3 sidecars on disk — a mismatch.

Not introduced by PR #160, but PR #160's quickload-resume path makes it more reachable (previously, quickloading between scene changes always finalized the tree, so the tree was effectively "new" each time).

**Fix plan (long-term):** version sidecar files per save point — stamp each `.prec` with the save epoch or a hash, refuse to load mismatched versions. Alternatively, never rewrite sidecars for committed trees; treat them as immutable.

**Priority:** Low — rare user workflow (quicksave in flight + exit to TS + quickload), and the worst case is playback inconsistency, not data loss. Flag again if it bites during playtest.

## ~~275. Watch button tooltip blank when ghost not built~~

Reported as "Watch buttons broke" in the post-PR-#163 playtest. The W button in the Recordings window showed greyed-out with no tooltip, making it look broken. Two causes:

1. **Kerbal X launch recording had 0 points** (bug #273) → no ghost was ever built → `hasGhost=false` → button correctly disabled but tooltip empty.
2. **Fresh flight started before all committed recordings' time windows** — ghosts were in the future (`UT < recording.StartUT`), not yet built, so every W button was disabled with no explanation.

**Fix:** `RecordingsTableUI.cs` now sets explicit tooltips for all disabled states:
- `!hasGhost` → "No active ghost — recording is in the past/future or has no trajectory points"
- `!sameBody` → "Ghost is on a different body" (unchanged)
- `!inRange` → "Ghost is beyond camera cutoff" (unchanged)
- Enabled state → "Follow ghost in watch mode" / "Exit watch mode" depending on `isWatching` (was previously blank)

Applied to both the per-row W button and the group-level W button. Purely cosmetic — the underlying data-loss fix (#273) is what makes the Kerbal X W button actually come back to life when the ghost eventually builds.

---

## 279. Watch button unavailable from F5 moment onwards (2026-04-09 playtest)

Reported alongside bugs #276/#277/#278 in the 2026-04-09 playtest session (`logs/2026-04-09_recording-flow-bugs/`). After F5 at UT 369.2, the Watch button for the Kerbal X tree and its children was effectively unusable for the rest of the flight — the user couldn't preview any of the just-recorded flight data.

**Likely conflated with bug #278.** If the whole Kerbal X tree ends up with `spawnable=0` because every leaf has `hasSnapshot=False canPersist=False terminal=Destroyed` (see bug #278), then every W button on every leaf is correctly disabled by the existing "no spawnable ghost" gate — it's a side effect of the debris-snapshot-TTL bug, not a separate UI issue. Fix #278 first and re-test.

**Direct log evidence is thin:** only two `[VERBOSE][UI]` disabled-state lines in the whole playtest log, both from rewind (`Rewind already in progress`). No Watch click events logged in the 17:44:38 → 17:47:21 window. That's either "user didn't click" or "click was silently ignored" — the UI code's disabled-state log lines are verbose-level and the row-level gating may not log on every poll. Add an `[INFO]` log at the Watch row when the disabled state flips, so future playtests can distinguish "user didn't try" from "UI was broken".

**Investigation remaining:**
- Confirm the hypothesis: reproduce bug #278 in isolation, verify that fixing it restores Watch availability.
- Audit `ParsekUI` / `RecordingsTableUI` Watch button gating predicates. List every condition under which it greys out, and make sure each has a tooltip (#275 was the last pass at this — the 2026-04-09 playtest suggests there's still coverage missing).
- Add instrumented logging of Watch button state transitions (enabled → disabled and vice versa) at INFO level, keyed by recording id.

**Priority:** Medium — blocks on #278 investigation. If #278's fix restores availability, this entry becomes the "add disabled-state transition logging" follow-up and can drop to Low.

---

## 278. Capsule not spawned at end of recording (2026-04-09 playtest)

After the Kerbal X flight in the 2026-04-09 playtest, no vessel was spawned at end-of-recording for the user to continue playing with. The Bob Kerman EVA recording is the only one that *could* have spawned (it had a snapshot), but it terminated `Destroyed` with `canPersist=False` — see `KSP.log:11548`.

Shared root cause with the booster-debris-ghost-not-rendered bug (tracked separately by a parallel investigation — look for the entry added alongside the 2026-04-09 playtest batch): the 27 `Kerbal X Debris` sub-recordings in the tree all have `hasSnapshot=False canPersist=False terminal=Destroyed` at merge time (L11539–11547), even though `BgRecorder` logged `hasSnapshot=True` when each debris vessel was split off (L9700 etc.). The snapshots are captured at split time but lost somewhere between capture and merge — the current best hypothesis is the 60 s TTL flush that runs when the debris vessel is destroyed.

```
11559: [MergeDialog] Tree merge dialog: tree='Kerbal X', recordings=29, spawnable=0
```

With `spawnable=0` the whole tree is non-spawnable and no capsule ends up on the surface after Kerbal X's final crash — even the root Kerbal X section (which has trajectory data) is not spawned because it terminates `Destroyed` too.

**Investigation target:** `BgRecorder` → `Recording.Snapshot` → TTL expiry path. Trace why a snapshot captured with `hasSnapshot=True` comes back as `hasSnapshot=False` at merge. Suspect `OnVesselWillDestroy` hook or the 60 s TTL timer clearing the snapshot field along with the vessel reference.

**Related:** the booster-debris-ghost-not-rendered bug is being investigated by a parallel agent and shares this root cause. Their fix may resolve this one too. Re-scope after their diagnosis lands.

**Priority:** High — this is the user's main "nothing to continue playing with after a crash recording" complaint, and it also cascades into bug #279 (Watch unavailable, because `spawnable=0` disables W buttons).

---

## 277. Wrong crew spawned at recording end (2026-04-09 playtest)

Reported in the 2026-04-09 playtest (`logs/2026-04-09_recording-flow-bugs/`). The Kerbal X rocket crew was Jeb (Pilot) / Bill (Engineer) / Bob (Scientist), but at merge-dialog time only two of the three stand-in swaps succeeded.

**Smoking gun at `KSP.log:12836-12840`:**
```
12836: [CrewReservation] Swapped 'Jebediah Kerman' → 'Zelsted Kerman'  in part 'Mk1-3 Command Pod'
12837: [CrewReservation] Swapped 'Bill Kerman'     → 'Siford Kerman'   in part 'Mk1-3 Command Pod'
12838: [CrewReservation] Crew swap complete: 2 succeeded — refreshed vessel crew display
12839: [CrewReservation] Removing reserved EVA vessel 'Bob Kerman' (pid=1857874769)
12840: [CrewReservation] Removed 1 reserved EVA vessel(s)
```

Only 2 swaps completed. The Bob→Carsy swap never ran because Bob was on an EVA vessel (rec `d768a28f`) at the moment of merge rather than in the command pod. The code took the "Removing reserved EVA vessel" branch and kicked Carsy out of the reservation pool without ever placing her in any vessel. Net result: the command pod ends up with `Zelsted + Siford + the original Bob`, and the scientist slot is still the pre-merge Bob rather than the stand-in Carsy. The merge dialog reports `spawnable=0` (see also bug #278) so the user never sees the resulting crew assignment in-flight, but the roster is still wrong.

**Fix direction:** `CrewReservationManager.SwapCrewInPart` (or equivalent — confirm exact name) handles the in-pod case; the EVA-at-merge-time case falls through to the "remove reserved EVA vessel" branch and silently drops the reservation. Either:
- (a) If the kerbal being swapped out is currently on an EVA vessel, recover-and-place — move the stand-in into the command pod's scientist slot at merge time, regardless of where the original was.
- (b) Or: block the merge dialog with an error when an EVA-reserved kerbal can't be swapped, so the user knows the crew assignment is incomplete.

Option (a) is probably right — the user's intent is "generate stand-in crew for this flight". EVA'd kerbals at merge time should still be replaced.

**Tests needed:**
- Unit: `CrewReservationManager` swap with EVA'd original — assert stand-in is placed in the pod, original remains on the EVA vessel (or is removed with the reservation, depending on chosen semantics).
- Integration: reproduce the 2026-04-09 scenario (crew goes EVA, commands merge) — assert all three crew slots get stand-ins.

**Priority:** High — crew correctness is user-visible and sticky (wrong roster persists across sessions).

---

## ~~276. F5 → EVA → F9 commits the EVA walk as an orphan recording instead of discarding it~~

Exposed by the 2026-04-09 playtest with the save in `logs/2026-04-09_recording-flow-bugs/`. After the Kerbal X tree was merged at UT 369.2, the player F5'd, EVA'd Siford Kerman, walked ~24 s, then F9'd. Expected: the Siford EVA recording is discarded as part of the time-travel undo. Actual: it was committed as orphan standalone recording `6ea90fa7` (see `KSP.log:13568` — `OnSave: saving 30 committed recordings` immediately after the F9 return). The user repeated the pattern three times in a row (Siford, Megely, Katsey) and all three became committed orphans at UT 369.2.

**Smoking gun in the `[RecState]` log:**
```
12901: Game State Saved to saves/s32/quicksave             # F5 at UT 369.2
12920: [Scenario] Vessel switch detected: ... → 'Siford Kerman' (EVA)
13009: [#117][StartRecording:post] mode=sa pid=1892431707 ut=373.1
13107: [#120][OnSceneChangeRequested] ut=397.2              # F9 → scene teardown
13116: [RecordingStore] Stashed pending recording: 178 points from Siford Kerman
13184: [#123][OnLoad:settings-applied] ut=369.2             # UT regressed 397.2 → 369.2
13194: [Scenario] OnLoad: revert detection — savedEpoch=0, currentEpoch=0,
       savedRecNodes=0, savedTreeRecs=29, memoryRecordings=29, ...,
       isVesselSwitch=True, isRevert=False
13195: [#125][OnLoad:revert-decided=N]                      # neither revert nor discard ran
13568: [Scenario] OnSave: saving 30 committed recordings    # orphan committed
```

**Root cause — two independent failures:**

1. **`vesselSwitchPending` mis-classified as fresh.** The `onVesselSwitching` event fired at L12920 when Siford bailed out (EVA). PR #274 added a frame-count staleness cap of 300 frames (~6 s at 50 FPS) to filter out this kind of leakage. But under low render FPS at loaded KSC (`[WARN][Diagnostics] Playback frame budget exceeded: 26.3ms` + many active physics vessels → ~12 fps), 24 s of EVA walking only advances ~288 frames — *just* under the 300 cap. The stale flag was classified fresh at OnLoad time (L13194: `isVesselSwitch=True`).

2. **Count/epoch revert signals don't fire for post-merge F5.** Even with `isVesselSwitch` correctly false, the remaining revert signal is `savedEpoch < currentEpoch || savedRecCount < memoryCount`. When F5 happens *after* a merge, both sides have the same 29 committed recordings and same epoch 0. Nothing trips `isRevert`, so the existing "discard pending stashed-this-transition" branch at `ParsekScenario.OnLoad` L567 never runs. The stashed Siford standalone survives to the next OnSave and is committed as orphan #30.

**Fix — orthogonal UT-backwards signal:**

Added a clock-regression check independent of vessel-switch/epoch/count. A quickload is the only legitimate way `Planetarium.GetUniversalTime()` can go backwards between `OnSceneChangeRequested` and the next `OnLoad` — time-warp, SOI transitions, and normal scene changes all preserve or advance UT. Rewinds short-circuit OnLoad at L441 via `RewindContext.IsRewinding` before revert detection runs, so that path is unaffected.

1. **`ParsekScenario`**: added private static `lastSceneChangeRequestedUT = -1.0` and `StampSceneChangeRequestedUT(double)` setter.
2. **`ParsekFlight.OnSceneChangeRequested`**: stamps `Planetarium.GetUniversalTime()` into it at the top of the method.
3. **`ParsekScenario.OnLoad`** revert-detection block: reads and consumes the stamp. New pure helper `IsQuickloadOnLoad(preChangeUT, currentUT, epsilon=0.1)` — returns true when `currentUT < preChangeUT - epsilon`. If true *and* `isFlightToFlight`:
   - Force `isVesselSwitch = false` (with a log line showing the flag age, so future diagnostics can see whether the 60-frame cap is also getting close to its limit).
   - Call new `DiscardStashedOnQuickload(preChangeUT, currentUT)` helper, which discards `HasPending && PendingStashedThisTransition` (via existing `DiscardPending`, which deletes sidecar files) and discards `HasPendingTree && PendingStashedThisTransition && state != Limbo`. **Limbo pending trees are explicitly preserved** — they're the quickload-resume carrier for tree-mode F5/F9, handled by the existing `ScheduleActiveTreeRestoreOnFlightReady` path further down in OnLoad.
   - Also clears `GameStateRecorder.PendingScienceSubjects` — the list is not serialized to .sfs so any entries accumulated between F5 and F9 are, by definition, from the discarded future timeline and would otherwise mis-attach to the next committed recording.

4. **`VesselSwitchPendingMaxAgeFrames` tightened from 300 → 60** as defense in depth. At 60 fps this is ~1 s (plenty for a same-frame tracking-station reload), at 12 fps it's still 5 s — far under any realistic EVA walk duration. The count/staleness check from #274 remains the primary defense against EVA leakage; UT-backwards is the secondary defense against the specific F5-post-merge case where count/epoch signals are blind.

5. **Reset sites**: `lastSceneChangeRequestedUT` is consumed to `-1.0` in OnLoad, and also reset in `OnMainMenuTransition` alongside `lastOnSaveScene` to prevent leakage across save loads.

**Tests** (`QuickloadDiscardTests.cs`, 15 cases):
- Pure helper: unset, unchanged, forward, backward, sub-epsilon noise, exact-epsilon boundary (strict `<` semantics), negative epsilon defensive refusal, and the exact Siford 397.2→369.2 playtest numbers as a named regression.
- State + log assertions: pending standalone discard path, pending-not-this-transition preservation, non-Limbo tree discard, Limbo tree preservation (the tree-mode resume carrier), stale science subject clear, empty-state header-only logging.
- Narrative regression: low-FPS EVA leak (288 frames) rejected by the tighter 60-frame cap — protects the primary defense.

The `IsVesselSwitchFlagFresh_MaxAgeConstantValue` pin test (updated from 300 to 60) is the review speed bump — any future loosening of the cap must be justified at the test site.

---

## ~~274. vesselSwitchPending stale-flag leak — F9 after EVA finalizes tree instead of resuming~~

Exposed by the post-PR-#163 playtest trail. The player launched, decoupled (promoted standalone→tree), EVAed Bill Kerman, EVAed Bob Kerman, F5'd, then F9'd. Expected: restore-and-resume the tree. Actual: tree was auto-committed (lost in-flight continuity) + Kerbal X root recording came back with 0 points (bug #273).

**Smoking gun in the `[RecState]` log:**
- `[#128][OnLoad:revert-decided=N]` — `isRevert=false`
- `[#129][OnLoad:limbo-dispatched]`
- `[#130][FinalizeLimboForRevert:entry]` ← ran anyway

`FinalizePendingLimboTreeForRevert` only runs when `isRevert || isVesselSwitch`. With `isRevert=false`, the only remaining trigger was `isVesselSwitch=true`. That meant `vesselSwitchPending` was set at OnLoad time.

**Root cause:** `vesselSwitchPending` is set by KSP's `onVesselSwitching` GameEvent, which fires on EVERY vessel focus change — including EVAs. EVAs don't trigger scene reloads, so the flag sat sticky for minutes (thousands of frames) until the next F9 consumed it. `OnLoad` then mis-identified the quickload as a tracking-station vessel switch and routed the Limbo tree into finalize instead of restore.

**Fix:** stamp `vesselSwitchPendingFrame = Time.frameCount` alongside the flag in `OnVesselSwitching`. In `OnLoad`, use new pure-static `ParsekScenario.IsVesselSwitchFlagFresh(pending, pendingFrame, currentFrame, maxAgeFrames)` with `maxAgeFrames = 300` (~6 seconds at 50 FPS — covers tracking-station reload without letting minute-old EVA leakage through). xUnit tests cover flag-not-set, never-stamped, same-frame, within-max-age, at-limit, just-past-limit, EVA-leakage-scenario (10000-frame gap), monotonic guard, and the constant value lock-in.

---

## ~~273. Tree recording trajectory lost on scene reload (FilesDirty audit)~~

After PR #163 fixed the OnFlightReady ordering bug, a second F5/F9 playtest with an active tree showed the Kerbal X launch recording (88+ points) coming back from the save with 0 points. Sidecar file on disk: 61 bytes (just the header — no POINT nodes).

**Root cause:** Tree recordings created / mutated in-flight at ~33 sites across `ParsekFlight.cs`, `ChainSegmentManager.cs`, and `BackgroundRecorder.cs` modified `Recording.Points`/`PartEvents`/etc. without setting `FilesDirty = true`. `SaveActiveTreeIfAny` only calls `SaveRecordingFiles` for recordings where `FilesDirty == true`, so none of those recordings ever had their `.prec` sidecars written during in-flight F5 saves. On scene reload, `TryRestoreActiveTreeNode` read the empty `.prec` files and produced 0-point recordings. The in-memory tree (with the actual trajectory data) was then discarded by the new load, and `FinalizeTreeCommit`'s final dirty pass wrote 61-byte empty files as the "authoritative" on-disk state.

**Fix (initial scope, ParsekFlight + ChainSegmentManager):**
1. `ParsekFlight.PromoteToTreeForBreakup` — creates rootRec from standalone `CaptureAtStop` on first breakup
2. `ParsekFlight.CreateSplitBranch` (first-split path) — creates rootRec from standalone `CaptureAtStop` on first split
3. `ParsekFlight.AppendCapturedDataToRecording` — static helper called from `CreateSplitBranch` subsequent-split path and `CreateMergeBranch`
4. `ParsekFlight.FlushRecorderToTreeRecording` — flushes recorder buffer into a tree recording on vessel switch / scene change
5. `ChainSegmentManager.SampleContinuationVessel` — extends a committed recording's trajectory with continuation samples after EVA
6. `ChainSegmentManager.StartUndockContinuation` — creates a new committed recording with a seed point for the undocked sibling vessel

**Fix (extended scope, BackgroundRecorder — from PR #164 review):**

Code review on the initial fix flagged that `BackgroundRecorder.cs` contained 27 more mutation sites (`treeRec.PartEvents.Add` / `.Points.Add` / `.OrbitSegments.Add` / `.TrackSections.Add`) with zero `FilesDirty` marks — the same class of bug, not exercised by the foreground Kerbal X playtest but latent for any F5/F9 scenario with background-tracked vessels. Extended the fix to cover all of BackgroundRecorder:

- `OnBackgroundPartDie` (part death event)
- `OnBackgroundPartJointBreak` (decouple event)
- `OnBackgroundPhysicsFrame` (trajectory point sampling)
- `CloseOrbitSegment` (on-rails orbit segment emission)
- `SampleBoundaryPoint` (on-rails → physics boundary)
- `FlushTrackSectionsToRecording` (finalization)
- `PollPartEvents` — 17 child `CheckXState` methods (parachute, jettison, engine, RCS, deployable, ladder, animation group, aero surface, control surface, robot arm, heat, generic animation, light, gear, cargo bay, fairing, robotic). Uses a count-delta pattern: capture `treeRec.PartEvents.Count` before polling, compare after, mark dirty only if the delta is positive. Single guard covers 19 individual `.Add` calls without 19 inline dirty-mark lines.
- `FinalizeAllForCommit` terminal-event `AddRange` — `FlushTrackSectionsToRecording` early-exits when `trackSections.Count == 0`, so its dirty mark is skipped. A background vessel finalized with no accumulated sections (e.g., one that just entered loaded state and was immediately finalized) would otherwise leave the terminal engine/RCS/robotic `AddRange` unpersisted. Now marks dirty explicitly when `terminalEvents.Count > 0`.
- `InitializeLoadedState` seed-event `AddRange` — fires when a background vessel transitions to loaded physics for the first time. If an `OnSave` happens before any subsequent poll emits an event, the seed events would be lost. Now marks dirty explicitly when `seedEvents.Count > 0`.

**Helper:** Introduced `Recording.MarkFilesDirty()` instance method with a comprehensive docstring pointing at this entry, making the invariant grep-able and discoverable from IDE hover. All new sites (ParsekFlight + ChainSegmentManager + BackgroundRecorder) use the helper. Pre-existing `FilesDirty = true` direct assignments in `RecordingStore.cs` are left alone (they work; scope creep to churn them).

**Tests:**
- xUnit: 3 tests for `AppendCapturedDataToRecording` (non-null source marks dirty, null source leaves flag alone, existing points preserved)
- Source-scrape regression guards (`Bug273_MethodBody_ContainsMarkFilesDirtyCall`) — one `[Theory]` test with 6 `[InlineData]` rows, one per fix site. Each finds the method body via brace-depth walk and asserts it contains a `MarkFilesDirty()` call. Catches accidental removal of a single line in any of the 6 named methods.

---

## ~~272. Entire launch tree destroyed on F5/F9 quickload-resume~~

Observed in the 2026-04-09 playtest with the new `[RecState]` observability (PR #162): after the user's first real F5+F9 with an active tree (Kerbal X launch, 46 recordings, 331-point root, Bob Kerman EVA), the whole tree vanished and a fresh standalone recording started for the EVA kerbal.

**Root cause:** `ParsekFlight.OnFlightReady` called `StartCoroutine(RestoreActiveTreeFromPending())` BEFORE `ResetFlightReadyState()`. When `FlightGlobals.ActiveVessel` already matches the target vessel name on the first iteration of the restore wait loop, the `break` exits without hitting `yield return null`, so Unity runs the entire coroutine body synchronously inside `StartCoroutine()`. Control returned to `OnFlightReady`, which then called `ResetFlightReadyState()` → line 4163 `activeTree = null` destroyed everything the restore had just set up (and tore down `backgroundRecorder`, cleared chain state). Log proof from the playtest, all same millisecond:

```
[#124][Restore:after-start] mode=tree tree=d64334a2|Kerbal X rec=f3527c60|Bob Kerman
Resetting flight-ready state
BgRecorder Shutdown complete — all background states cleared
Chain ClearAll: all chain state reset
Timeline has 0 committed recording(s)
```

**Fix:** move `ResetFlightReadyState()` to run BEFORE the restore coroutine. Reset always clears scene-scoped state from the previous flight; restore then rebuilds fresh state on top. Both sync-coroutine and async-coroutine paths work correctly with the new order. Also added a `[RecState]` emission at `ResetFlightReadyState` entry for observability of this boundary, and a regression guard test (`OnFlightReadyOrderingTests`) that file-scrapes `ParsekFlight.cs` to assert the ordering invariant plus a phase-sequence test walking the post-fix emission order.

**Recovery for affected saves:** the in-memory tree is lost but the `quicksave.sfs` still contains the full `RECORDING_TREE isActive=True` node. Loading that save with the fix applied rebuilds the tree from the inline node via `TryRestoreActiveTreeNode` → `RestoreActiveTreeFromPending`.

---

## 271. Investigate unifying standalone and tree recorder modes

Parsek currently has two recorder modes with divergent code paths:

- **Standalone mode** — single `FlightRecorder`, flat `Recording` list, no `activeTree`. Scene-change path: `StashPendingOnSceneChange` in `ParsekFlight.cs`.
- **Tree mode** — `activeTree` (`RecordingTree`) with multiple recordings, branches, chain continuations. Scene-change path: `FinalizeTreeOnSceneChange` → `StashActiveTreeAsPendingLimbo` / `CommitTreeSceneExit`.

Parity bugs surface when a fix gets applied to one mode but not the other (observed with PR #160's quickload-resume: fix landed for tree mode only). Rule of thumb is now tracked as a memory/feedback item: any change to one mode must also be applied to the other until these are unified.

**Investigate:** can the two modes be merged into a single unified architecture? Tree mode is structurally a superset of standalone — a standalone recording is effectively a single-recording tree with no branches. A unified mode might:
- Always allocate a `RecordingTree` at recording start, even for trivial single-recording missions
- Eliminate `StashPendingOnSceneChange` and route everything through the tree path
- Delete the `pendingRecording` slot in favor of the pending-tree slot
- Unify the merge dialog, commit paths, and save/load serialization

Risks / open questions:
- UI assumptions: does the recordings table distinguish "single recording" from "tree with one recording"? Any visual differences the player would notice?
- Migration: what about existing saves with standalone pending recordings? Do they round-trip through a single-recording-tree form, or do we keep a migration shim?
- Performance: trees carry more per-recording overhead (BranchPoints dict, BackgroundMap, TreeId lookups). Is that cost acceptable for trivial recordings?
- Edge cases: non-flight scenes (KSC, TS) currently interact with both modes differently; verify the unified path covers them.

**Priority:** Medium — not blocking any release, but every parity fix widens the surface. Unifying would collapse the maintenance cost at the root.

---

# In-Game Tests

- [x] Vessels propagate naturally along orbits after FF (no position freezing)
- [x] Resource converters don't burst after FF jump
