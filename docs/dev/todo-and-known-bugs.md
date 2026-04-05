# TODO & Known Bugs

Previous entries (225 bugs, 51 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v1.md`.

---

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

### T4. Release automation

GitHub Actions workflow to build, run tests, package `GameData/Parsek/` into a zip, and create a GitHub release on tag push. Currently all release packaging is manual.

**Priority:** Nice-to-have

## TODO — Performance & Optimization

### T6. LOD culling for distant ghost meshes

Don't render full ghost meshes far from camera. Unity LOD groups or manual distance culling.

**Priority:** Low — only matters with many ghosts in Zone 2

### T7. Ghost mesh unloading outside active time range

Ghost meshes built for recordings whose UT range is far from current playback time could be unloaded and rebuilt on demand.

**Priority:** Low — memory optimization

### T8. Particle system pooling for engine/RCS FX

Engine and RCS particle systems are instantiated per ghost. Pooling would reduce GC pressure with many active ghosts.

**Priority:** Low

## TODO — Ghost Visuals

### T25. Fairing internal truss structure after jettison

After fairing jettison, the ghost currently shows just the payload and base adapter. KSP's real vessel can show an internal truss structure (Cap/Truss meshes controlled by `ModuleStructuralNodeToggle.showMesh`). The prefab meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can set correctly. A procedural truss mesh was attempted but removed due to insufficient visual quality.

To implement properly: either rescale prefab Cap/Truss meshes from XSECTION data (need to reverse-engineer the mesh unit geometry), or generate higher-fidelity procedural geometry with proper materials.

**Priority:** Low — cosmetic, only visible briefly after fairing jettison

## TODO — Recording Accuracy

### T16. Planetarium.right drift compensation

KSP's inertial reference frame may drift over very long time warp. Could cause ghost orientation mismatch for interplanetary segments. Needs empirical measurement first.

**Priority:** Low — may not be needed

## TODO — Compatibility

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Low — v1 targets stock only, mod compat is best-effort

---

# Known Bugs

## 46. EVA kerbals disappear in water after spawn

Player landed in water, EVA'd 3 kerbals from the pad vessel, but 2 of them disappeared. May be KSP's known behavior of destroying EVA kerbals in certain water situations, or a Parsek crew reservation/dedup conflict.

**Observed in:** Same session as #45. After vessel spawn with crew dedup (3 crew removed from spawn snapshot because they were already on the pad vessel from revert), the pad vessel retained the real crew. Player EVA'd from pad vessel; 2 of 3 kerbals vanished shortly after EVA.

**Root cause:** Needs investigation. Possible causes:
1. KSP destroys EVA kerbals that land in water (known vanilla behavior in some situations)
2. Parsek `crewReplacements` dict interfered with EVA kerbal persistence
3. Crew dedup removed crew from the wrong vessel

**Impact:** Medium — crew members lost unexpectedly.

**Status:** Open — deferred to resource/game actions tracking redesign

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

## 125. Engine plate covers / fairings not visible on ghost

Engine plates (`EnginePlate1` etc.) have protective covers (interstage fairings) that are built by `ModuleProceduralFairing` at runtime — similar to stock procedural fairings but integrated into the engine plate part. These covers are not visible on ghost vessels during playback. The ghost shows the engine plate base and attached engines but the fairing shell is missing.

Likely same root cause as the original procedural fairing work: the fairing mesh is generated procedurally from XSECTION data at runtime by `ModuleProceduralFairing`, not stored as a static mesh on the prefab. The ghost builder's `GenerateFairingConeMesh` may not be triggered for engine plate fairings, or the engine plate's MODULE config may differ from standalone fairings.

**Priority:** Medium — visually noticeable on any vessel using engine plates

**Status:** Partially fixed — variant filter fix ensures the correct shroud mesh IS cloned (9 MR including Shroud3x2 for "Medium" variant), but the shroud is not visually correct in-game. The `ModuleJettison` lists ALL shroud variants (Shroud3x0-3x4) — the jettison system collects all of them, including non-active variants that were filtered by the variant system. Additionally, the shroud mesh may be positioned incorrectly (user reports it might be rendered upward instead of downward). The shroud meshes are external SharedAssets models whose transform positioning relative to the engine plate needs investigation.

## 132. Policy RunSpawnDeathChecks and FlushDeferredSpawns are TODO stubs

`ParsekPlaybackPolicy.RunSpawnDeathChecks()` and `FlushDeferredSpawns()` are placeholder stubs. The old path equivalents in `ParsekFlight.UpdateTimelinePlaybackViaEngine()` still call the original `FlushDeferredSpawns(committed)` directly, so deferred spawns work. But spawn-death detection (vessel dies immediately after spawning → re-spawn with death count tracking) is not wired through the policy yet.

The pure predicates (`ShouldAbandonSpawnDeathLoop`, `ShouldFlushDeferredSpawns`, `ShouldSkipDeferredSpawn`) are tested; only the integration through the policy event handlers is missing.

**Investigation notes:** `FlushDeferredSpawns` works correctly in ParsekFlight — moving to policy is pure refactoring with heavy state dependency (`pendingSpawnRecordingIds`, `pendingWatchRecordingId`, `SpawnVesselOrChainTip`, `DeferredActivateVessel`). Low value, high risk. Spawn-death detection is genuinely absent: `SpawnDeathCount` is never read in the engine path, and no `onVesselTerminated` subscription exists to detect spawned vessel destruction. Implementing requires event subscription + PID-to-recording matching. **Dormant bug:** policy and ParsekFlight have duplicate `pendingSpawnRecordingIds`/`pendingWatchRecordingId` fields — policy populates its copies in `HandlePlaybackCompleted` but the flush reads ParsekFlight's copies. Currently works because both paths add to the same set independently, but could diverge.

**Priority:** Low — edge case (rapid spawn-death cycles), FlushDeferredSpawns works via existing path

**Status:** Open — deferred (spawn-death detection needs design, FlushDeferredSpawns move is low-value refactor)

## 133. Forwarding properties in ParsekFlight add ~500 lines of indirection

After T25 extraction, ParsekFlight still has forwarding properties (`ghostStates => engine.ghostStates`, `overlapGhosts => engine.overlapGhosts`, etc.) and bridge methods that external callers (scene change, camera follow, delete, preview) use.

**Priority:** Low — tech debt, no functional impact

**Status:** Partially fixed — removed 6 dead forwarding methods, inlined 4 remaining call sites. 7 forwarding properties retained as ergonomic aliases — `ghostStates` alone has 17 usages. Actual indirection was ~40 lines, not ~500.

## 154. parsek_38.png texture compression warning

KSP warns `Texture resolution is not valid for compression` for the 38x38 toolbar icon. Not a power-of-two size so KSP can't DXT-compress it.

**Fix:** Resize to 32x32 or 64x64.

**Priority:** Low — cosmetic, icon works fine uncompressed

## 156. Missing test coverage from lifecycle simulation

Areas identified by code path simulation that lack unit tests:

1. `HandleVesselSwitchDuringRecording` with `Stop` decision — no test verifies recording data is committed/stashed rather than orphaned (fixed by #155, no regression test — requires Unity runtime)
2. `CacheEngineModules` with partially-loaded vessel — null vessel tested; null part entries require Unity `Vessel` instance (not feasible in unit tests)
3. `CheckAtmosphereBoundary` → `HandleAtmosphereBoundarySplit` → `HandleSoiChangeSplit` in sequence — requires full `FlightRecorder` instance state (in-game integration test only)

**Priority:** Low — test infrastructure, requires Unity runtime

**Status:** Partially fixed — item 4 done (7 tests). Items 1-3 deferred to in-game testing.

## 157. Green sphere ghost for debris after ghost-only merge decision

When a debris recording is set to "ghost-only" in the merge dialog, `ApplyVesselDecisions` nulls `VesselSnapshot`. If `GhostVisualSnapshot` was also null (debris destroyed before snapshot copy), `GetGhostSnapshot` returns null and the ghost falls back to a green sphere.

**Partial fix:** `ApplyVesselDecisions` now copies `VesselSnapshot` to `GhostVisualSnapshot` before nulling the spawn snapshot, if `GhostVisualSnapshot` is not already set.

**Remaining issue:** If the debris was destroyed before ANY snapshot was captured (both null at recording time), the sphere fallback is unavoidable. Could improve by capturing snapshot at the moment of breakup rather than deferring.

**Priority:** Low — cosmetic (sphere fallback for very-short-lived debris)

**Status:** Partially fixed

## 159. EVA auto-recordings have no rewind save — R button absent

EVA recordings started from non-launch situations (landed base, orbiting station) have no `RewindSaveFileName` because rewind saves are only captured for chain root / launch recordings. The R button in the recordings window doesn't appear for these recordings.

**Priority:** Low — design gap, not a bug. Rewind save belongs to the original launch, not each EVA.

**Status:** Open — needs design decision: should EVA auto-records capture their own rewind save?

## 160. Log spam: remaining sources after ComputeTotal removal

After removing ResourceBudget.ComputeTotal logging (52% of output), remaining spam sources:
- GhostVisual HIERARCHY/DIAG dumps (~344 lines per session, rate-limited per-key but burst on build)
- GhostVisual per-part cloning details (~370 lines)
- Flight "applied heat level Cold" (46 lines, logs no-change steady state)
- RecordingStore SerializeTrackSections per-recording verbose (184 lines)
- KSCSpawn "Spawn not needed" at INFO level (54 lines)
- BgRecorder CheckpointAllVessels checkpointed=0 at INFO (15 lines)

**Priority:** Low — VERBOSE level, but adds up

**Status:** Open — tracked for next log audit round

## 166. R buttons disabled after tree commit — rewind saves consumed

After a recording tree is committed via the merge dialog, all R buttons for that tree's recordings become disabled because the rewind quicksave files were deleted during tree promotion. Only the root recording had a rewind save; branch recordings never had one.

**Priority:** Low — by design for tree recordings, but confusing UX

**Status:** Open — design gap: should tree branches inherit the root's rewind save?

## ~~185. Investigate spawning idle vessels earlier or trimming recording tail~~

After an EVA, the vessel left behind is a static recording (ghost) right up to the moment the tree was committed, even though it stopped moving much earlier. Consider either spawning the vessel as real when it enters its final resting state, or trimming the end of the recording if nothing changes after the last meaningful event.

**Fix:** `RecordingOptimizer.TrimBoringTail` already handles this for standalone recordings. It was not applying to breakup-continuous tree recordings because `IsLeafRecording` rejected them (had `ChildBranchPointId != null`). Fixed by checking `IsEffectiveLeafForVessel` before rejecting.

**Status:** Fixed

## 186. Initial launch recording shows T+ countdown instead of "past" status

In the Parsek recordings window, the initial launch recording (parent of a tree) shows "T+5m 23s" in the Status column while child recordings show "Landed". It may be more appropriate to show "past" or the terminal state. Additionally, these tree recordings have no Phase column value.

**Status:** TODO

## 187. Centralize time conversion system

All time formatting (FormatDuration, FormatCountdown, KSPUtil.PrintDateCompact) should use a centralized system that respects the game's calendar settings (day length, year length). Currently FormatDuration hardcodes 6h days / 426d years. Audit all time conversion call sites and unify.

**Status:** TODO

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

## 194. W (watch) button stays enabled on one booster after separation

After booster separation, 3 of 4 boosters correctly have W disabled, but one stays enabled. The watch eligibility check (`HasActiveGhost && sameBody && inRange`) doesn't check `IsDebris`. A debris recording can have an active timeline ghost but shouldn't be watchable.

**Status:** TODO — fix

## 196. Ghost icon popup window should appear next to cursor

The popup spawned via `PopupDialog.SpawnPopupDialog` consistently appears at screen center despite attempts to reposition. KSP's `SpawnPopupDialog` forces `localPosition=Vector3.zero` after anchor setup. Need to use the same approach as KSP's `MapContextMenu`: anchor at (0,0), then set `localPosition` via `CanvasUtil.ScreenToUISpacePos`.

**Status:** TODO — deferred

## 203. Green dot ghost markers at wrong positions near Mun after scene reload

Two compounding issues: (1) `TerminalOrbitBody` is null on all recordings at load time — `HasOrbitData(Recording)` returns false for all 62 recordings, preventing ProtoVessel creation from the initial scan. (2) After FLIGHT→FLIGHT scene reload, ghost map vessel positions jump from Mun-relative (~11M m) to world-frame (~2B m) — the coordinate frame shifts during scene reload and positions aren't corrected.

**Status:** TODO — needs investigation into why TerminalOrbitBody is never set, and coordinate frame correction after scene reload.

## 217. Settings window GUILayout exception (Layout/Repaint mismatch)

`DrawSettingsWindow` throws `ArgumentException: Getting control N's position in a group with only N controls when doing repaint`. Unity IMGUI bug caused by conditional `GUILayout` calls whose condition changes between Layout and Repaint passes. The window is stuck at 10px height and non-functional. 72 exceptions per session when the settings window is opened.

**Fix:** Ensure all `GUILayout` calls in `DrawSettingsWindow` execute identically in both Layout and Repaint passes. Wrap conditionals around content only (not layout elements).

## 218. Crash breakup debris not recorded when recorder tears down before coalescer

When a vessel crashes during an active recording, the recorder is stopped and committed before the coalescer's 0.5s window elapses. By the time the coalescer emits the BREAKUP event, there is no active tree or recorder to attach it to. The main vessel recording is saved but the crash debris tree structure is lost.

**Priority:** Low — the vessel recording itself is preserved; only debris ghosts are missing.

## 219. Ghost creation fails for orbital debris chain ("no orbit data")

`CreateGhostVessel` repeatedly fails for certain orbital debris chains with `no orbit data for chain pid=NNNN`. The orbit segment data exists in the recording but the ghost system cannot access it at creation time. Fires on every flight scene entry.

**Priority:** Low — only affects debris ghost visibility in orbit.

## 220. PopulateCrewEndStates called repeatedly for 0-point intermediate recordings

Intermediate tree recordings with 0 trajectory points but non-null VesselSnapshot trigger `PopulateCrewEndStates` on every recalculation walk (36 times in a typical session). These recordings can never have crew.

**Priority:** Low — performance optimization, no functional impact.

## ~~224. Vessel not spawned at end of playback when parts break off on splashdown~~

Recording `f8fd04e5` (Kerbal X, chainIndex=1) had both `childBranchPointId` (breakup at UT 102.8, parts broke off on splashdown impact) AND `terminalState = Splashed`. The non-leaf check in `ShouldSpawnAtRecordingEnd` suppressed spawn even though this recording was the effective leaf for its vessel (no same-PID continuation child existed).

**Root cause:** `ProcessBreakupEvent` (foreground breakup handler) sets `ChildBranchPointId` on the active recording but does NOT create a same-PID continuation — by design, the foreground recording continues through breakups. Only debris children are created. The non-leaf check treated all recordings with `ChildBranchPointId` as non-leaf without checking whether a same-PID continuation actually exists.

**Fix:** (1) `IsEffectiveLeafForVessel` checks if any child of the branch point shares the same `VesselPersistentId` — if not, the recording IS the effective leaf. Both non-leaf checks (primary + safety net) skip for effective leaves. (2) `ProcessBreakupEvent` refreshes `VesselSnapshot` post-breakup so the spawned vessel reflects the surviving parts, not the pre-breakup configuration.

**Status:** Fixed

## ~~226. ForceSpawnNewVessel transient flag is fragile~~

`ForceSpawnNewVessel` was a transient (not serialized) flag on Recording, set at scene entry and consumed at spawn time. The flag could be lost if Recording objects were recreated mid-scene (auto-save, quicksave, RecordingStore rebuild).

**Fix:** Replaced per-recording transient flag with a single static `RecordingStore.SceneEntryActiveVesselPid` (set once at scene entry). `SpawnVesselOrChainTip` now checks `rec.VesselPersistentId == SceneEntryActiveVesselPid` to bypass PID dedup statelessly. A complementary `activeVesselSharesPid` runtime check covers mid-scene vessel switches. Removed `ForceSpawnNewVessel` field, `MarkForceSpawnOnActiveVesselRecordings`, `MergeDialog.MarkForceSpawnOnTreeRecordings`, and all flag-setting code.

**Status:** Fixed

---

# In-Game Tests

- [x] Vessels propagate naturally along orbits after FF (no position freezing)
- [x] Resource converters don't burst after FF jump
