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

**Priority:** Low — v1 targets stock only, mod compat is best-effort

---

# Known Bugs

## ~~46. EVA kerbals disappear in water after spawn~~

Player landed in water, EVA'd 3 kerbals from the pad vessel, but 2 of them disappeared. May be KSP's known behavior of destroying EVA kerbals in certain water situations, or a Parsek crew reservation/dedup conflict.

**Observed in:** Same session as #45. After vessel spawn with crew dedup (3 crew removed from spawn snapshot because they were already on the pad vessel from revert), the pad vessel retained the real crew. Player EVA'd from pad vessel; 2 of 3 kerbals vanished shortly after EVA.

**Root cause:** Same mechanism as #233. `RemoveReservedEvaVessels` deletes EVA vessels whose crew name is in `crewReplacements`, including player-created EVA vessels. The crew names were reserved from committed recording snapshots. When `SwapReservedCrewInFlight` ran (on scene re-entry or recording commit), it found the player's EVA vessels and destroyed them. Cause 1 (KSP water behavior) may also be a factor for some disappearances.

**Fix:** Two guards added to `RemoveReservedEvaVessels`: (1) loaded EVA vessels are skipped entirely — they're actively in the physics bubble, not stale quicksave remnants (bug #46). (2) Vessels whose `persistentId` matches a committed recording's `SpawnedVesselPersistentId` are skipped (bug #233).

**Status:** Fixed

## 95. Committed recordings are mutated in several places after commit

Recordings should be frozen after commit (immutable trajectory + events, mutable playback state only). Audit found several places where immutable fields on committed recordings are mutated:

1. **`ParsekFlight.cs:993`** — Continuation vessel destroyed: sets `VesselDestroyed = true` and nulls `VesselSnapshot`. Snapshot lost permanently; vessel cannot re-spawn after revert.
2. **`ChainSegmentManager.cs:492`** — EVA boarding: nulls `VesselSnapshot` on committed recording (next chain segment is expected to spawn, but revert breaks that assumption).
3. **`ParsekFlight.cs:2785,3595`** — Continuation sampling: `Points.Add` appends trajectory points to committed recordings. Intentional continuation design, but points from the abandoned timeline persist after revert.
4. **`ParsekFlight.cs:2820-2831`** — Continuation snapshot refresh: replaces or mutates `VesselSnapshot` in-place on committed recordings, overwriting the commit-time position with a later state.
5. **`ParsekFlight.cs:3629-3641`** — Undock continuation snapshot refresh: same pattern for `GhostVisualSnapshot`.
6. **`ParsekScenario.cs:2469-2472`** — `UpdateRecordingsForTerminalEvent`: can still mutate committed recordings that match by vessel name but haven't spawned yet (name collision edge case; spawned recordings are now guarded).

Items 1-2 are highest risk (snapshot destruction). Items 3-5 are part of the continuation mechanism's design but violate the frozen-recording principle. Item 6 is a residual edge case from #94.

**Priority:** Medium — the continuation mutations are by design and rarely hit in practice, but the snapshot nulling (items 1-2) can cause the same no-spawn-after-revert symptom as #94 if the continuation vessel is destroyed or boards before revert.

**Status:** Partially fixed — items 1-2 fixed (snapshot no longer nulled on committed recordings; `VesselDestroyed` flag gates spawn and is reset by `ResetRecordingPlaybackFields` on revert/rewind). Item 6 already fixed by #94 (committed recordings fully skipped in `UpdateRecordingsForTerminalEvent`). Items 3-5 deferred as known tech debt (continuation mechanism design; would require a separate ContinuationData overlay to fix properly).

## 112. Aeris 4A spawn blocked by own spawned copy — permanent overlap

After rewinding and re-entering flight, the Aeris 4A recording's spawn-at-end tried to place a new vessel at the same position where a previously-spawned (but not cleaned up) Aeris 4A already sat. The spawn collision detector correctly blocked it, but because the overlap is permanent (both vessels at the same runway position), this triggered bug #110's infinite retry loop. The log showed `Spawn blocked: overlaps with #autoLOC_501176 at 5m — will retry next frame` repeating every frame for the remainder of the session.

**Root cause:** `CleanupOrphanedSpawnedVessels` recovered one copy, but a second Aeris 4A was loaded from the save and occupied the spawn slot. The duplicate presence is partly caused by bug #109 (cleanup skipped on second rewind, leaving a stale vessel in the save). The spawn system has no dedup against already-present matching vessels.

**Note:** The infinite retry loop symptom is resolved by bug #110's fix (spawn abandoned after 150 frames). The root cause of duplicate vessel presence remains a separate issue.

**Priority:** Medium (consequence of #110 infinite retry — infinite loop symptom now resolved)

**Status:** Open (root cause of duplicate vessel remains; infinite loop symptom resolved by #110 fix)

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

**Status:** Partially fixed — removed 6 dead forwarding methods, inlined 4 remaining call sites. 7 forwarding properties retained as ergonomic aliases — `ghostStates` alone has 17 usages. Actual indirection was ~40 lines, not ~500.

## ~~154. parsek_38.png texture compression warning~~

KSP warns `Texture resolution is not valid for compression` for the 38x38 toolbar icon. Not a power-of-two size so KSP can't DXT-compress it.

**Fix:** Replaced 38x38 and 24x24 toolbar icons with 64x64 and 32x32 power-of-two versions. Updated references in ParsekFlight, ParsekKSC, and release.py.

**Status:** Fixed

## 156. Missing test coverage from lifecycle simulation

Areas identified by code path simulation that lack unit tests:

1. `HandleVesselSwitchDuringRecording` with `Stop` decision — no test verifies recording data is committed/stashed rather than orphaned (fixed by #155, no regression test — requires Unity runtime)
2. `CacheEngineModules` with partially-loaded vessel — null vessel tested; null part entries require Unity `Vessel` instance (not feasible in unit tests)
3. `CheckAtmosphereBoundary` → `HandleAtmosphereBoundarySplit` → `HandleSoiChangeSplit` in sequence — requires full `FlightRecorder` instance state (in-game integration test only)

**Priority:** Low — test infrastructure, requires Unity runtime

**Status:** Partially fixed — item 4 done (7 tests). Items 1-3 deferred to in-game testing.

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

## 188. Spawned surface vessels clutter map view during ascent

During ascent, map view shows green dot icons for past recordings' spawned vessels sitting on the ground. These are real KSP vessels spawned at recording end — they correctly show in map view because they're actual vessels. But they're distracting during flight. Consider options: defer surface vessel spawns until tracking station visit, add a map filter toggle, or mark spawned ground vessels as debris type to reduce visual clutter.

**Status:** TODO

## 189b. Ghost escape orbit line stops short of Kerbin SOI edge

For hyperbolic escape orbits, KSP's `OrbitRendererBase.UpdateSpline` draws the geometric hyperbola from `-acos(-1/e)` to `+acos(-1/e)` using circular trig (cos/sin), which clips at a finite distance (~12,000 km for e=1.342). The active vessel shows the full escape trajectory to the SOI boundary because it uses `PatchedConicSolver` + `PatchRendering`. Ghost ProtoVessels don't get a `PatchedConicSolver`.

**Options:**
1. Draw a custom LineRenderer through the recording's trajectory points (accurate but significant work)
2. Extend the orbit line beyond the hyperbola asymptote with a straight-line segment to the SOI exit point
3. Give the ghost a `PatchedConicSolver` (complex, may conflict with KSP internals)

**Status:** TODO — needs custom rendering solution

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

---

# In-Game Tests

- [x] Vessels propagate naturally along orbits after FF (no position freezing)
- [x] Resource converters don't burst after FF jump
