# TODO & Known Bugs

---

## TODO — Release & Distribution

### T1. Add Parsek.version file (KSP mod convention)

Standard `.version` file enables AVC (Add-on Version Checker) and CKAN to detect available updates. Place in `GameData/Parsek/Parsek.version`.

```json
{
    "NAME": "Parsek",
    "URL": "https://raw.githubusercontent.com/vl3c/Parsek/main/GameData/Parsek/Parsek.version",
    "VERSION": { "MAJOR": 0, "MINOR": 5, "PATCH": 0 },
    "KSP_VERSION": { "MAJOR": 1, "MINOR": 12, "PATCH": 5 },
    "KSP_VERSION_MIN": { "MAJOR": 1, "MINOR": 12, "PATCH": 0 },
    "KSP_VERSION_MAX": { "MAJOR": 1, "MINOR": 12, "PATCH": 99 }
}
```

**Priority:** Should-do for 0.5.1

### T2. UI version display

Show "Parsek v0.5.0" somewhere in the main Parsek window (title bar or footer). Currently the version is only in `AssemblyInfo.cs` and not visible to the player.

**Priority:** Should-do for 0.5.1

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

### T4. Release automation

GitHub Actions workflow to build, run tests, package `GameData/Parsek/` into a zip, and create a GitHub release on tag push. Currently all release packaging is manual.

**Priority:** Nice-to-have

---

## TODO — Performance

### ~~T5. ReduceFidelity and SimplifyToOrbitLine full implementations~~ DONE

`ReduceFidelity` now disables 75% of renderers by index (keeps every 4th) for a coarse LOD silhouette. `SimplifyToOrbitLine` hides ghost mesh with `simplified` flag (orbit line rendering deferred to future). Both have frame-skip flags (`fidelityReduced`/`simplified`) to avoid re-processing. Caps-resolved branch restores fidelity and re-shows simplified ghosts.

### T6. LOD culling for distant ghost meshes

Don't render full ghost meshes far from camera. Unity LOD groups or manual distance culling.

**Priority:** Low — only matters with many ghosts in Zone 2

### T7. Ghost mesh unloading outside active time range

Ghost meshes built for recordings whose UT range is far from current playback time could be unloaded and rebuilt on demand.

**Priority:** Low — memory optimization

### T8. Particle system pooling for engine/RCS FX

Engine and RCS particle systems are instantiated per ghost. Pooling would reduce GC pressure with many active ghosts.

**Priority:** Low

### ~~T9. Background ghost cachedIdx persistence (bug #62)~~ DONE

Cached trajectory lookup index on `GhostChain.CachedTrajectoryIndex` instead of resetting to 0 each frame. `FindWaypointIndex` safely falls back to binary search if the index is stale after a recording change. O(n) → O(1) amortized.

### ~~T10. RealVesselExists O(n) per frame (bug #49)~~ DONE

Replaced `RealVesselExists` linear scan with frame-cached `HashSet<uint>`. Manual invalidation via `InvalidateVesselCache()` called as first line of `UpdateTimelinePlaybackViaEngine` (avoids `Time.frameCount` crash in tests). Cache rebuilt lazily on first call per frame. `vesselExistsOverride` bypass preserved for tests.

---

## TODO — Features

### T25. Fairing internal truss structure after jettison

After fairing jettison, the ghost currently shows just the payload and base adapter. KSP's real vessel can show an internal truss structure (Cap/Truss meshes controlled by `ModuleStructuralNodeToggle.showMesh`). The prefab meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can set correctly. A procedural truss mesh was attempted but removed due to insufficient visual quality.

To implement properly: either rescale prefab Cap/Truss meshes from XSECTION data (need to reverse-engineer the mesh unit geometry), or generate higher-fidelity procedural geometry with proper materials.

**Priority:** Low — cosmetic, only visible briefly after fairing jettison

### T11. Ghost map presence KSP integration (bug #60)

`GhostMapPresence` pure data layer is complete. 4 KSP integration points need in-game API investigation: tracking station registration, map orbit lines, nav target support, cleanup on despawn.

**Priority:** Medium — improves ghost world presence

### T12. EVA recording scope expansion (bug #56)

`OnCrewOnEva` only records EVAs from launch pad. In-flight EVAs (suborbital, flying, orbiting) are not recorded.

**Priority:** Medium — design limitation

### T13. UI subgroup enable/loop checkboxes (bug #50)

Recording subgroups in the UI are missing bulk enable and loop toggle checkboxes. Only top-level groups have them.

**Priority:** Low — UI polish

### T14. Controlled children recording after breakup (bug #61)

When crash/breakup creates controlled children (surviving probe cores), no recording segments are started for them. Would need multi-vessel background recording to track their trajectories.

**Priority:** Low — edge case

### T15. Crash-safe pending recording recovery

If the game crashes with a merge dialog pending, recording data is lost from memory. Solution: write a `pending_manifest.cfg` to `Parsek/Recordings/` when stashed, auto-recover on next load.

**Priority:** Low — data safety improvement

### T16. Planetarium.right drift compensation

KSP's inertial reference frame may drift over very long time warp. Could cause ghost orientation mismatch for interplanetary segments. Needs empirical measurement first.

**Priority:** Low — may not be needed

---

## TODO — Code Quality

### T17. Game actions recording redesign (Phase 8)

Redesign milestone capture, resource budgeting, and action replay validated per game mode: sandbox (no resources), science (science only), career (full). See roadmap Phase 8.

**Priority:** High — correctness across game modes

### T18. Log contract checker error whitelist (bug #63)

`ParsekLogContractChecker` has no whitelist for intentional error-path test scenarios. Currently no tests need this.

**Priority:** Low — test infrastructure

### ~~T19. FlightRecorder per-frame List&lt;PartEvent&gt; allocations~~ DONE

Methods now append to a caller-owned reusable buffer instead of allocating per call.

### ~~T20. ParsekFlight.TimelineGhosts allocates new Dictionary on every access~~ DONE

Cached per-frame via `Time.frameCount`.

### ~~T21. ParsekUI double ComputeTotal calls~~ DONE

Cached per-frame via `GetCachedBudget()` helper.

### ~~T22. GhostSoftCapManager.EvaluateCaps not tested when Enabled=false~~ DONE

Added `EvaluateCaps_Disabled_ReturnsEmpty_EvenAboveAllThresholds` test.

### ~~T23. SessionMerger frame trimming not tested~~ DONE

Added `ResolveOverlaps_FramesTrimmedToResultBoundaries` test with multi-frame sections.

### ~~T24. EnvironmentDetector untested for ORBITING and ESCAPING situations~~ DONE

Added 5 tests covering ORBITING(32) and ESCAPING(64) with thrust/no-thrust and airless body variants.

---

## TODO — Refactor Deferred Items

Items identified during refactor-2 (March 2026) but deferred because they require architectural changes beyond mechanical extraction. Full details in [refactor-2-deferred.md](plans/refactor-2-deferred.md).

A broader refactor-3 audit (March 2026) identified additional structural opportunities beyond these deferred items. Full analysis in [refactor-3-audit.md](plans/refactor-3-audit.md).

### T25. ParsekFlight TimelinePlaybackController extraction (D20)

**Status: DONE** — Completed as GhostPlaybackEngine (1553 lines) + ParsekPlaybackPolicy (192 lines) + IPlaybackTrajectory/IGhostPositioner/GhostPlaybackEvents interfaces. ParsekFlight reduced from ~9900 to 8657 lines. Engine has zero Recording references, accesses trajectories via IPlaybackTrajectory interface only. D5 and D8 now done (PR #85). D2 done (T28, CommitSegmentCore extracted).

### ~~T26. ParsekFlight ChainSegmentManager extraction (D21)~~ DONE

Phase 1 (state isolation): 16 chain state fields moved to ChainSegmentManager. ~150 field accesses migrated. Phase 2 (method moves): 12 methods (~505 lines) moved — 8 pure continuation methods (Group A) + 4 commit methods (Group B). `CommitSegmentCore` extracts shared stash/tag/commit/advance pattern. 3 orchestration methods stay on ParsekFlight (StartRecording lifecycle). ParsekFlight net -620 lines.

### ~~T27. GhostVisualBuilder SampleXxxStates unification (D15)~~ DONE (PR #82)

Extracted `SampleAnimationStates` core method with `AnimLookup` enum + `FindAnimation` resolver. 4 methods reduced to thin wrappers, 4 caches consolidated into 1 `animationSampleCache`. Net -139 lines.

### T28. ParsekFlight commit-pattern dedup (D2)

~~Unify `CommitChainSegment`, `CommitDockUndockSegment`, `CommitBoundarySplit`, `HandleVesselSwitchChainTermination`.~~ **DONE** — `CommitSegmentCore` extracts shared stash/tag/commit/advance pattern. All 4 commit methods now delegate to CommitSegmentCore via `Action<Recording>` callback. CommitSegmentCore handles nullable CaptureAtStop for boundary splits.

### T29. BackgroundRecorder Check*State polling dedup (D11)

17 Check*State method pairs (~736 lines) mirror FlightRecorder. Layer 1 (pure transition logic) is already shared. Layer 2 duplication is intentional design for per-vessel state isolation. A shared `PartEventSink` interface could unify Layer 2 but would change the call pattern.

**Priority:** Low — intentional design, not a bug

### ~~T30. ParsekUI window resize drag dedup (D18)~~ DONE

Extracted `HandleResizeDrag` and `DrawResizeHandle` static helpers. 4 drag blocks + 4 handle blocks replaced with 8 one-liner calls. Group Popup passes null for windowName to suppress logging. Latent bugfix: Group Popup now gets `Event.current.Use()` on MouseDrag (other 3 windows already had it).

### T31. ParsekFlight CreateBreakupChildRecording dedup (D1)

4 child-recording creation loops across `ProcessBreakupEvent` + `PromoteToTreeForBreakup` (~160 lines). Blocked: sites diverge in BackgroundMap handling (inline vs bulk). Would require a conditional flag.

**Priority:** Low — moderate savings, moderate risk

### T32. Deep test suite audit — DONE (audit + fixes), edge cases deferred

Audit completed: 110 test files (~55k lines) reviewed by 9 Opus subagents, independently reviewed. Fixes applied: 43 files changed, +170/-1182 lines.

Resolved: 8 exact duplicate pairs deleted, 28 always-passing/tautological tests deleted, 17 classes given IDisposable, 12 tests not calling production code deleted, 4 misleading names fixed, unused log capture removed, dead code removed, `[Collection("Sequential")]` added where needed.

Remaining edge case gaps (P3, not addressed):
- ResourceApplicator KSP-mutation methods (0% direct coverage)
- CrewReservationManager mutation methods (untested)
- ParsekFlight resource replay paths (duplicated logic)
- ~10 specific missing edge cases documented in `docs/dev/done/plans/task-32-test-audit.md` Section 7

**Priority:** Low — the harmful tests are gone; remaining gaps are additive coverage

### ~~T33. Encapsulate GroupHierarchyStore mutable fields~~ DONE

Added 5 accessor methods (`AddHiddenGroup`, `RemoveHiddenGroup`, `IsGroupHidden`, `TryGetGroupParent`, `HasGroupParent`). Migrated all ~20 ParsekUI.cs direct field accesses to use accessors and read-only properties. Tests retain direct field access for setup (pragmatic — encapsulation targets production coupling).

### T34. ChainSegmentManager unit tests

ChainSegmentManager has no dedicated test file. Pure state-machine methods (`ClearAll`, `ClearChainIdentity`, `StopContinuation`, `StopUndockContinuation`) and the commit abort paths are testable without Unity. Continuation sampling logic (`SampleContinuationVessel`) would need mocking for `FlightRecorder.FindVesselByPid` and `RecordingStore`.

**Priority:** Medium — new class with non-trivial state transitions

### T35. ChainSegmentManager field encapsulation

ParsekFlight still reads/writes `chainManager` internal fields directly in ~5 locations (CommitFlight chain tagging, FallbackCommitSplitRecorder, PromoteToTreeForBreakup, vessel destruction handler, EVA crew name set). Consider adding `TagPendingWithChainMetadata(Recording)` and making identity fields read-only with mutation via methods only.

**Priority:** Low — functional but leaky abstraction

### T36. Continuation recording index fragility

`ContinuationRecordingIdx` and `UndockContinuationRecIdx` store `int` indices into `RecordingStore.CommittedRecordings`. If a recording is ever removed from the list while continuation is active, the stored index silently points to the wrong recording or goes out of bounds (bounds check prevents crash but stops continuation). Consider storing `RecordingId` alongside the index for validation.

**Priority:** Low — no code path currently removes committed recordings during flight

### T37. Showcase kerbal-with-flag height mismatch

In the showcase part list, the kerbal holding a flag is not at the same height as the other parts. Likely a Y-offset issue in the showcase positioning/snapshot for the kerbal part.

**Priority:** Low — cosmetic

---

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

**Root cause (investigation 2026-03-15):** Two confirmed issues with rover wheel ghost rendering:

1. **Damaged wheel transforms rendered alongside intact meshes:** Rover wheels with `ModuleWheelDamage` have `damagedTransformName` entries (e.g., `bustedwheel`, `wheelDamaged`) pointing to transforms that contain damaged/broken wheel meshes. These transforms are normally inactive in-game but `GetComponentsInChildren<MeshRenderer>(true)` collects them because it includes inactive objects. The ghost rendered both intact and damaged meshes simultaneously, producing visual artifacts.

   Part config survey:
   - `roverWheelS2`: `damagedTransformName = bustedwheel`
   - `roverWheelM1`: `damagedTransformName = wheelDamaged`
   - `roverWheelTR-2L`: `damagedTransformName = bustedwheel`
   - `roverWheelXL3`: `damagedTransformName = bustedwheel`
   - Landing gear (GearSmall, GearMedium, GearLarge, GearFixed, GearFree, GearExtraLarge) have `ModuleWheelDamage` but no `damagedTransformName` — no damaged mesh to filter.

2. **Possible null sharedMesh on SkinnedMeshRenderers:** Rover wheel tire meshes may be procedurally generated at runtime by KSP's wheel system. If the prefab's `SkinnedMeshRenderer.sharedMesh` is null, the ghost silently skips it with no diagnostic. Added WARN-level logging to identify this in-game.

**Fix (partial):**
- Added `GetDamagedWheelTransformNames(ConfigNode partConfig)` to extract `damagedTransformName` values from all `ModuleWheelDamage` MODULE nodes in the part config
- Added `IsRendererOnDamagedTransform(Transform, HashSet<string>)` to check if a renderer's transform (or any ancestor) matches a damaged transform name
- Both MeshRenderer and SkinnedMeshRenderer loops now skip renderers on damaged transforms with diagnostic logging
- Added WARN-level log for null `sharedMesh` on SkinnedMeshRenderers: identifies whether tire meshes are procedurally generated
- Added summary log per part: counts cloned MeshRenderers, cloned SkinnedMeshRenderers, null-mesh SMR skips, and damaged-wheel renderer skips
- Diagnostic approach: the WARN log for null sharedMesh will appear in KSP.log when tested in-game, confirming whether missing wheel meshes are due to runtime procedural generation (requires separate fix) vs. the damaged transform overlap (now fixed)

**In-game verification (2026-03-15):** All rover wheels and landing gear render correctly in the showcase. KSP.log confirms:
- Zero null-sharedMesh warnings — tire meshes ARE present on prefabs (not procedurally generated)
- Zero SkinnedMeshRenderers on any wheel part — all use regular MeshRenderers
- Damaged wheel filtering working: roverWheel1 skipped 2 renderers, roverWheel2 skipped 1, roverWheelM1-F skipped 1
- Variant textures applied correctly on roverWheelM1-F (Grey variant)

The original "wheels missing" report was likely caused by the damaged mesh overlap (now fixed) or by a specific vessel configuration not reproduced in the showcase.

**Status:** Fixed

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

**Fix:** Added 8-frame debounce (~0.15s at 50Hz) to `CheckRcsState` in both `FlightRecorder` and `BackgroundRecorder`. RCS must be continuously `rcs_active` for 8 consecutive physics frames before `RCSActivated` is emitted. `RCSStopped` fires immediately when activity stops after a sustained activation. Micro-corrections below the threshold are silently filtered — no events emitted. Debounce state tracked in `rcsActiveFrameCount` dictionary, cleared on reset. Pure static helpers `ShouldStartRcsRecording`/`IsRcsRecordingSustained` extracted for testability. No changes to PartEvent struct, serialization, playback, or ghost builder.

**Status:** Fixed (three-part). Recording-side: 8-frame debounce filters SAS micro-corrections. Playback-side: `RestoreAllRcsEmissions` was unconditionally calling `Play()` on ALL RCS particle systems after warp/suppression cycling, even those never activated by an event. Fixed by checking `rateOverTimeMultiplier > 0` before restoring — only RCS modules that received an `RCSActivated` event get restored. Build-side: `TryBuildRcsFX` used `GetComponentInChildren` (singular) which only configured the first `ParticleSystem` per FX model; child systems (glow/smoke) kept `playOnAwake=true` and auto-played on every ghost activation. Fixed by using `GetComponentsInChildren` (plural) to stop, clear, and disable renderers on all child systems.

## 31. Engine shroud/cover not rendered correctly for some engines

Some engines display their protective shroud/cover incorrectly during ghost playback. The shroud may appear missing, partially rendered, or in the wrong variant configuration. This affects engines that have multiple shroud variants (different sizes for different tank diameters) or engines with complex jettison transform hierarchies.

**Root cause:** `GhostVisualBuilder.AddPartVisuals` resolves jettison transforms by looking up `ModuleJettison.jettisonName` in the clone map. However, engines with part variants (e.g., Mainsail with `fairing`/`fairingSmall`, Skipper with `fairing`/`fairing2`, KE-1 "Vector" with `Shroud2x3`/`Shroud2x4`) have multiple shroud meshes on the prefab but only the active variant's mesh is included in the clone. When the jettison lookup finds the prefab transform but not the clone, it logs `"Jettison 'X' found on prefab but not in cloneMap"` and skips it.

The active variant's shroud is detected and tracked correctly via the `cloneMap` hit path. The 275 "not in cloneMap" messages in the log (2026-03-14 session) are for non-active variant shrouds and are expected. However, the actual rendering issue may occur when:
1. The GAMEOBJECT variant rules hide/show shroud transforms in a way that doesn't match what the ghost clone captured
2. The shroud mesh scale or position differs between the prefab default and the recorded variant

**Observed in:** KSC ghost testing (2026-03-14). Affected engine parts include `LiquidEngineKE-1` (72+60 variant misses), `engineLargeSkipper.v2` (25 variant misses), `LiquidEngineLV-T91`/`LiquidEngineLV-TX87` (18 variant misses).

**In-game verification (2026-03-15):** Log analysis of a 126-part shuttle session confirms the variant-aware jettison detection works correctly. The active variant's shroud IS cloned and tracked for all affected engines. The "not in cloneMap" messages (138 in this session) are for inactive variant meshes that were correctly excluded by GAMEOBJECT variant rules. No actual rendering defects observed.

Example — Poodle (liquidEngine2-2.v2, SingleBell variant): `Shroud2` correctly cloned and tracked, `Shroud1` correctly excluded with "not in cloneMap" message.

The verbose "not in cloneMap" messages are informational, not errors. Consider rate-limiting them in a future cleanup pass.

**Status:** Not a bug — working as designed

## 32. Launch Escape System (LES) plume effects need verification

The Launch Escape System (`LaunchEscapeSystem` part) has 5 `thrustTransform` nozzles with `Squad/FX/LES_Thruster` particle effects. The ghost build chain correctly creates particle systems for all 5 nozzles, and playback events fire in the correct sequence (`Decoupled` → `EngineIgnited` → `EngineShutdown`). However, the LES uses a specialized SRB-style plume effect that may not match the stock visual appearance.

**Needs verification:**
- Are the LES plume particle systems using the correct effect group? The ghost FX builder clones from `MODEL_MULTI_PARTICLE` configs in the EFFECTS node — verify this matches the LES thruster visual.
- The LES has a unique exhaust pattern (5 angled nozzles in a ring). Verify the particle system positions/rotations match the actual nozzle geometry on the ghost model.
- Compare ghost LES firing visual to stock LES firing in-game — check plume color, size, and direction.

**Observed in:** Log analysis (2026-03-14). Ghost build succeeds with 6 MeshRenderers and 5 thruster FX systems. Engine FX `playing=False` on initial ignition frame was observed for LES (90 occurrences) — the particle system may not visually start until the second frame after `EngineIgnited`.

**Root cause (confirmed):** `GameDatabase.GetModelPrefab` returns inactive root GameObjects. The cloned FX instance inherited the inactive state, causing `ParticleSystem.Play()` to silently fail. Fix: added `SetActive(true)` after instantiation in the engine MODEL_MULTI_PARTICLE path (matching the existing RCS FX path). Fixes LES plume and silently broken MODEL_MULTI_PARTICLE on other engines.

**Status:** Fixed

## 33. Crash sequence: vessel stays visually intact until final explosion

When a vessel crashes, the ghost stays visually intact until the explosion fires. Parts that individually break off (sep motors, nose cones) are hidden correctly via `Decoupled`/`Destroyed` events, but parts still attached at final impact have no per-part event — they're cleaned up by `HideAllGhostParts` at explosion time.

**Root cause:** KSP's `onPartDie`/`onPartJointBreak` only fire for parts individually destroyed before the vessel is removed. Parts still attached at final vessel destruction get no event. For #autoLOC_8005481 (50 parts), only 10 parts got individual events; the other 40 stayed visible until the explosion. This is expected — the rocket genuinely stayed mostly intact until impact.

**Improvement:** Added `SpawnPartPuffFx` — a small smoke puff (10-20 particles) + spark burst (8-15 particles) at the part's world position when `Decoupled` or `Destroyed` events are applied during ghost playback. Gives visible feedback for individual part separation/destruction even when all events fire on the same frame.

**Status:** Improved — part separation now has visual FX feedback

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

**Update (2026-03-15):** The `playing=False` diagnostic was accurate — the particle systems genuinely were not playing due to the inactive-FX-instance bug (fixed in bug #32). The diagnostic correctly identified the symptom; the underlying cause was the missing `SetActive(true)` call, not a Unity timing quirk.

**Status:** Fixed (root cause was bug #32)

## 36. GhostVisual VERBOSE output dominates log (performance)

`GhostVisualBuilder` VERBOSE diagnostics produced 62,229 log lines in the 2026-03-14 session — 53% of all Parsek output. This includes per-part mesh renderer enumeration, FX placement diagnostics, variant fallback messages, and jettison transform resolution. All of this is re-emitted on every loop cycle rebuild for all ghosts.

Combined with the ShouldTriggerExplosion spam (bug #34, now fixed), these two subsystems were responsible for 92% of all Parsek log output.

**Breakdown of high-volume GhostVisual messages:**
- Variant fallback ("no active variant renderers and no GAMEOBJECT rules"): 1,869 lines — mostly `strutConnector` (588x), `pointyNoseConeB` (144x), `Panel0` (144x). These parts have `ModulePartVariants` with texture-only variants, no GAMEOBJECT rules. The fallback correctly includes all renderers.
- Jettison "found on prefab but not in cloneMap": 275 lines — non-active variant shroud transforms. Expected and harmless.
- Per-part MeshRenderer counts, FX nozzle counts, hierarchy dumps: bulk of the remaining lines.

**Fix:** Rate-limited the highest-volume per-part build diagnostics using `VerboseRateLimited` with 60-second intervals and per-part-name keys. Affected messages: part summary, variant selection/fallback, per-MeshRenderer/SkinnedMeshRenderer cloning, modelRoot DIAG, jettison cloneMap misses, engine hierarchy dump, outside-model MR warnings. Each message logs once on first ghost build, then is suppressed for 60s (well beyond a typical 10-30s loop cycle).

**Status:** Fixed

## 37. KSC ghosts not destroyed when recording is disabled

When the user disables a recording's playback in the KSC scene (unchecks the enable checkbox), the ghost GameObject stays visible in the scene. It is never cleaned up until the player leaves KSC.

**Root cause:** `ParsekKSC.Update()` (line 125) checks `ShouldShowInKSC(rec)` and `continue`s if false — skipping the recording entirely without destroying any existing ghost. In contrast, `ParsekFlight.Update()` explicitly destroys active ghosts when `PlaybackEnabled` is false before continuing.

**Fix:** Before the `continue`, check `kscGhosts` and `kscOverlapGhosts` for the recording index and destroy any active ghosts. Mirrors the pattern from `ParsekFlight`.

**Status:** Fixed

## 38. Merge dialog not shown after vessel destruction in tree mode

When a vessel explodes and the joint break creates a recording tree (`activeTree != null`), the post-destruction merge dialog is never shown. The user must manually revert the flight to trigger the dialog (fallback path via `OnFlightReady`).

**Root cause:** `OnVesselWillDestroy` (ParsekFlight.cs line 1218) guards `ShowPostDestructionMergeDialog` with `activeTree == null` — it only fires in standalone mode. The comment claims "In tree mode, the deferred destruction check handles this already" but `DeferredDestructionCheck` only applies terminal state to background recordings; it never shows a dialog.

The crash sequence: (1) vessel explodes → joint break → `DeferredJointBreakCheck` creates a tree, (2) continuation recording starts on debris/fragments, (3) fragments also destroyed, (4) no dialog fires because `activeTree != null`, (5) `FlightResultsPatch` suppresses KSP's "Catastrophic Failure" dialog expecting Parsek's dialog first — but it never comes.

**Compounding issue:** `FlightResultsPatch` intercepts `FlightResultsDialog.Display` and defers it until the merge dialog completes. When no merge dialog fires, KSP's flight results are permanently suppressed too — the user sees nothing at all.

**Observed in:** KSP.log (2026-03-15). Dynawing flights 2 and 3 — vessel destroyed, tree created by joint break, no dialog until manual revert. Flight 1 worked correctly (standalone mode, no tree).

**Additional symptom:** When watching a non-looped destroyed recording via Watch mode, the camera auto-follows to a tree child recording that has a `VesselSnapshot`. When that child ends, it spawns the vessel (e.g., Dynawing Probe, FLYING) and KSP switches to it as the active vessel. The user is now controlling a spawned vessel in mid-air instead of returning to their pad vessel. The game enters a weird "in flight" state, showing collision warnings when trying to exit to KSC. (The `needsSpawn` guard already prevents spawning for Destroyed/Recovered recordings — verified in code.)

**Fix:** Added `ShowPostDestructionTreeMergeDialog` coroutine triggered from both `OnVesselWillDestroy` (active vessel dies) and `DeferredDestructionCheck` (last background vessel dies). Uses `RecordingTree.AreAllLeavesTerminal` to detect when all tree leaves are dead, reuses `FinalizeTreeRecordings` + `StashPendingTree`, handles autoMerge. `treeDestructionDialogPending` flag prevents duplicate coroutines. `FlightResultsPatch.ClearPending` clears stale results on scene change. Safety net in `OnFlightReady` replays suppressed flight results if no dialog ever fired.

**Status:** Fixed

## 39. Ghost parts show wrong texture variant

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

**Fix:** Extended ghost builder to parse TEXTURE sub-nodes from VARIANT configs as generic property bags. Handles texture URLs, colors, floats, and shader replacements. Materials cloned before modification. Extracted `TryFindSelectedVariantNode` for shared variant-finding logic.

**Status:** Fixed

## 40. SRB nozzle glow persists after burnout

SRB nozzles on ghost vessels remain glowing indefinitely after the SRB runs out of fuel. The exhaust particle FX stops correctly on `EngineShutdown`, but the nozzle mesh stays emissive/hot-looking. Looks wrong after ~5 seconds — no heat source, but nozzle still glows.

**Root cause:** SRB nozzle glow is driven by `FXModuleAnimateThrottle`, not `ModuleAnimateHeat`. Parsek only handles `ModuleAnimateHeat` for heat ghost visuals. The chain of failure:

1. **Ghost build:** `TryGetAnimateHeatAnimation` searches for `ModuleAnimateHeat` only. SRBs have `FXModuleAnimateThrottle` instead → no `HeatGhostInfo` is created.
2. **Prefab clone:** The ghost mesh is cloned from the prefab with the `FXModuleAnimateThrottle` animation at whatever emissive state the prefab model had (often partially or fully glowing).
3. **Recording:** `EngineShutdown` event is recorded correctly when SRB burns out (`isOperational` becomes false on fuel depletion).
4. **Playback:** `EngineShutdown` handler calls `ApplyHeatState(heated: false)`, which looks up `state.heatInfos[pid]` — but no entry exists for this part (step 1), so the call returns false and does nothing.
5. **Result:** Particle exhaust stops, but emissive nozzle glow is permanently frozen.

**Affected parts:** 7 of 9 stock SRBs (all with `FXModuleAnimateThrottle`), plus 26 other engines (jets, ion engine, RAPIER, etc.).

**Fix:** Extended `GhostVisualBuilder` to detect `FXModuleAnimateThrottle` as a fallback heat source. Name-based heuristic ("heat"/"emissive"/"glow"/"color") disambiguates multi-instance parts (Panther, Whiplash). `EngineIgnited`/`EngineThrottle` now call `ApplyHeatState(hot)`. Cold initialization at ghost spawn prevents prefab emissive bleed-through.

**Status:** Fixed

## 41. Spurious Decoupled events on rover wheels under impact stress

When a rover flips or crashes, KSP fires `onPartJointBreak` for wheel parts even though the wheels remain physically attached to the vessel (the joint is stressed but the part stays). Parsek records `Decoupled` events for every `onPartJointBreak` and hides those parts on the ghost. Result: the ghost rover drives around with invisible wheels.

**Observed in:** Sandbox career (2026-03-15). "Test Alibaba" rover (recording `58332bc4a9fd48ac9900c86e1bad5b27`): 4 `roverWheel1` parts received repeated `Decoupled` events totaling 4347 part events for a 37-part rover (117 events per part average). The wheels stayed attached on the real vessel and the rover kept driving. Other parts (`noseconeVS`, `ksp.r.largeBatteryPack`, `telescopicLadderBay`, `longAntenna`, `GooExperiment`, `sensorBarometer`) correctly received `Destroyed` events when they actually broke off.

**Root cause:** `onPartJointBreak` fires for joints under impact stress, not just for permanent separations. KSP wheel joints can break and re-form during collisions — the part never actually leaves the vessel. The recording code treats every `onPartJointBreak` as a permanent `Decoupled` event without verifying that the part actually separated.

**Fix:** Two guards in `OnPartJointBreak`: (1) structural joint filter — compares `joint` against `joint.Child.attachJoint` to skip non-structural breaks (wheel suspension, steering joints under stress); (2) PID deduplication — `decoupledPartIds` HashSet prevents duplicate Decoupled events for the same part. Pure logic extracted to `IsStructuralJointBreak(bool, bool)` for testability.

**Status:** Fixed

## 42. Engine shroud missing at recording start (initial state seeding)

On multi-stage rockets, the second stage engine's protective shroud (ModuleJettison fairing) is missing from the ghost at the start of playback. The shroud should be visible during the first stage burn and only disappear at staging.

**Observed in:** Sandbox career (2026-03-15). "#autoLOC_501218" (large multi-stage rocket in Dynawing Probe tree). SSME engines (PIDs 372523866, 409669795) have `ShroudJettisoned` events firing at the very start of the recording. The ghost builds the `Fairing` mesh correctly (`MR[1] 'Fairing'`, jettison detected) but immediately hides it.

**Root cause:** `jettisonedShrouds` HashSet was cleared but not seeded with already-jettisoned parts at recording start. When the first physics-frame poll ran `CheckJettisonTransition`, any shroud already jettisoned (from a previous stage) was not in the set, so `HashSet.Add` returned true and a spurious `ShroudJettisoned` event was emitted at UT=0. Same issue affected `activeEngineKeys` (engines already running produced spurious `EngineIgnited`), and all other tracking sets (`lightsOn`, `extendedDeployables`, `deployedGear`, `openCargoBays`, `parachuteStates`, `deployedLadders`, `deployedAnimationGroups`, `activeRcsKeys`, etc.).

**Fix:** Added `SeedExistingPartStates` method in `FlightRecorder` that pre-populates all tracking sets by reading the current state of every part on the vessel at recording start. Added matching `SeedBackgroundPartStates` in `BackgroundRecorder`. Previously only `deployedFairings` was seeded; now all 15+ tracking sets are seeded consistently using the same state-reading logic as their respective `CheckXxxState` methods.

**Distinction from bug #31:** Bug #31 is about `ModulePartVariants` geometry selection for shroud transforms. This bug is about `ModuleJettison` timing — the correct shroud mesh is built but hidden too early.

**Status:** Fixed

## 43. Ghost variant texture shader not found: KSP/Emissive Specular

When applying variant TEXTURE rules to ghost parts, `Shader.Find("KSP/Emissive Specular")` returns null. This affects `pointyNoseConeA` and `pointyNoseConeB` whose variants specify `shader = KSP/Emissive Specular`. The texture and color properties are still applied (using the existing shader), but the shader swap fails silently with a WARN log.

**Observed in:** Shroud test session (2026-03-15). 138 warnings across 12+ ghost rebuild cycles for these two nose cone types.

**Root cause:** `Shader.Find()` requires the shader to be loaded in memory. KSP shaders are in shader bundles that may not expose all shaders by name to `Shader.Find()`. The shader exists at runtime (stock parts use it), but the lookup path via string name may not find it.

**Impact:** Low — cosmetic only. The nose cone still renders with the correct texture and colors, just without the shader change (which primarily affects specular/emissive rendering behavior). Visually negligible at playback speed.

**Possible fix:** Cache a reference to known KSP shaders at mod initialization by finding them on existing materials rather than by name. Or accept the fallback as "good enough."

**Status:** Fixed (commit 25ccfa9 — `FindShaderOnRenderers` fallback lookup on existing materials)

## 44. Code cleanup: duplicated seeding logic and growing out-parameter list

Technical debt from the part-audit PR (#46). Two `// TODO:` items in source code:

1. **Seeding duplication (~340 lines):** Extracted shared `PartStateSeeder` static class with `SeedPartStates` method. Both `FlightRecorder.SeedExistingPartStates` and `BackgroundRecorder.SeedBackgroundPartStates` now delegate to it, passing their respective tracking collections via `PartTrackingSets` parameter object. A `seedColorChangerLights` flag handles the one behavioral difference (FlightRecorder polls ColorChanger-based cabin lights; BackgroundRecorder does not). Also fixed BackgroundRecorder's seeding which previously lacked the AnimateGeneric exclusion logic (parts with dedicated handlers were not skipped).

2. **BuildTimelineGhostFromSnapshot out-parameters (10 info lists):** Replaced with `GhostBuildResult` class that bundles the root `GameObject` and all 10 info lists. Method now returns `GhostBuildResult` (null on failure). All backward-compat overloads removed. Call sites in `ParsekFlight.cs` and `ParsekKSC.cs` updated. `PopulateGhostInfoDictionaries` now takes `GhostBuildResult` instead of 10 individual list parameters.

**Status:** Fixed

## 45. Suborbital vessel spawn causes explosion

When a recording's final position is SUB_ORBITAL (e.g., the vessel was on a suborbital trajectory when the recording ended due to crash), the spawned vessel appears mid-air and immediately falls/explodes on contact with terrain or water. The spawn snapshot captures the vessel at its last recorded flight position, not on a surface.

**Observed in:** Mun flight test session (2026-03-18). Vessel spawned as `sit=SUB_ORBITAL` at chain end, then crashed.

**Root cause:** `VesselSpawner.RespawnVessel` spawns the vessel at whatever situation was recorded. If the final situation is SUB_ORBITAL, the vessel materializes in mid-air with no support.

**Impact:** Medium — spawned vessel explodes, player loses the vessel they expected to persist after ghost playback.

**Possible fix:**
1. Don't spawn vessels with SUB_ORBITAL terminal state — treat like Destroyed (ghost-only)
2. Propagate the orbit forward to find where it lands, spawn at that position
3. Add a "safe spawn" check: if situation is SUB_ORBITAL and altitude is low, defer spawn until vessel reaches surface

**Status:** Fixed — SubOrbital added to non-spawnable terminal states in `ShouldSpawnAtRecordingEnd` (option 1)

## 46. EVA kerbals disappear in water after spawn

Player landed in water, EVA'd 3 kerbals from the pad vessel, but 2 of them disappeared. May be KSP's known behavior of destroying EVA kerbals in certain water situations, or a Parsek crew reservation/dedup conflict.

**Observed in:** Same session as #45. After vessel spawn with crew dedup (3 crew removed from spawn snapshot because they were already on the pad vessel from revert), the pad vessel retained the real crew. Player EVA'd from pad vessel; 2 of 3 kerbals vanished shortly after EVA.

**Root cause:** Needs investigation. Possible causes:
1. KSP destroys EVA kerbals that land in water (known vanilla behavior in some situations)
2. Parsek `crewReplacements` dict interfered with EVA kerbal persistence
3. Crew dedup removed crew from the wrong vessel

**Impact:** Medium — crew members lost unexpectedly.

**Status:** Open — needs investigation

## 47. ~~ParsekLog.TestSinkForTesting race condition (test infrastructure)~~

xUnit eagerly instantiates test classes, so one class's constructor can overwrite `TestSinkForTesting` while another class's test method is running. Causes log assertion tests to be flaky.

**Fix applied:** All test override fields (`SuppressLogging`, `ClockOverrideForTesting`, `TestSinkForTesting`, `VerboseOverrideForTesting`) and `rateLimitStateByKey` marked `[ThreadStatic]` so each xUnit thread gets its own copy. No cross-thread interference.

**Status:** Fixed

## 48. ComputeBoundaryDiscontinuity hardcodes Kerbin radius

`SessionMerger.ComputeBoundaryDiscontinuity` uses `const double bodyRadius = 600000.0` (Kerbin). Wrong for Mun (200km), Eve (700km), etc. Diagnostic-only — logged magnitude is inaccurate on non-Kerbin bodies, doesn't affect playback.

**Status:** Fixed — added stock body radii dictionary and `GetBodyRadius` helper; `ComputeBoundaryDiscontinuity` now uses `lastPrev.bodyName` to look up the correct radius

## 49. ~~RealVesselExists O(n) per frame~~

`GhostPlaybackLogic.RealVesselExists` iterates `FlightGlobals.Vessels` linearly. Called per background recording per frame. Negligible with typical vessel counts (10-50), would matter with 100+. Fix: cache PIDs in HashSet, rebuild on vessel add/remove events.

**Status:** Fixed (T10, PR #85) — frame-cached `HashSet<uint>` with manual invalidation via `InvalidateVesselCache()` at start of `UpdateTimelinePlaybackViaEngine`

## 50. UI: subgroups missing enable and loop checkboxes

Recording subgroups in the UI don't have enable (playback toggle) or loop checkboxes. Only top-level recordings show these controls. Subgroup recordings can't be individually toggled for playback or loop mode from the UI.

**Status:** Open

## 51. Chain ID lost on vessel-switch auto-stop (CRITICAL)

When a vessel switch triggers auto-stop during an active chain recording, the stash/commit path drops the chain assignment. The exo/orbital segment gets committed as a standalone recording with `chain=(none)/-1` instead of being linked to the chain.

**Root cause:** The vessel-switch auto-stop path (`HandleVesselSwitchDuringRecording` in FlightRecorder) builds `CaptureAtStop` and sets `IsRecording = false`, but has no access to `ParsekFlight`'s chain fields. The original partial fix only tagged `CaptureAtStop` with chain metadata but never committed the segment as a chain member — it sat orphaned until `OnSceneChangeRequested` stashed it as a standalone pending.

**Fix:** Replaced the tag-only partial fix with `HandleVesselSwitchChainTermination()` — a dedicated handler in `Update()` modeled on `CommitBoundarySplit`. Detects when auto-stop left a stopped recorder with `CaptureAtStop` during an active chain, then: stashes, applies persistence artifacts, tags chain metadata (ChainId, ChainIndex, ChainBranch=0, ParentRecordingId, EvaCrewName), sets VesselPersistentId, derives SegmentPhase/SegmentBodyName and TerminalState from the recorded vessel (not ActiveVessel which changed), commits, reserves crew, cleans up continuation sampling, and terminates the chain. The final segment keeps its VesselSnapshot for spawning. Setting `recorder = null` prevents double-commit via `OnSceneChangeRequested`.

**Status:** Fixed

## 52. CanRewind log spam — 485K lines per session

`RecordingStore.CanRewind` logs at VERBOSE every call, but is called per-recording per-frame from the UI. With 11+ rewind-eligible recordings at 60fps, this produces ~660 log lines/second (80% of total log output).

**Fix:** Removed Verbose log on the success path — CanRewind is a read-only check called per-recording per-frame. Blocked-case logs remain for diagnostics.

**Status:** Fixed

## 53. "re-shown after warp-down" log spam — 16K lines per session

Ghosts toggled SetActive(false)/SetActive(true) every frame in KSC and Flight scenes produce continuous log spam. The re-show logic was designed for one-time warp transitions, not continuous toggling.

**Fix:** Added `loggedReshow` HashSet to deduplicate the re-show log per ghost index. Cleared when warp suppression starts (so the next warp-down cycle logs once) and on ghost destruction.

**Status:** Fixed

## 54. Watch mode follows ghost beyond terrain loading range

Watch mode keeps the ghost visible at any distance (per the earlier fix to skip zone hiding for watched ghosts). But when the ghost exceeds ~120km from the active vessel, KSP's terrain is not loaded around the ghost's position, causing terrain disappearance and floating-point jitter.

**Fix (original):** Watch mode had a 2-second real-time grace period (`WatchModeZoneGraceSeconds`). After grace, if the ghost entered Beyond zone (>120km), Watch exited and the ghost hid normally.

**Fix (superseded by #119):** The grace-period approach was wrong — ascending rockets naturally exceed 120km and the camera is at the ghost, so the user can see it fine. Replaced with a full zone-hide exemption for the watched ghost (see #119).

**Status:** Superseded by #119

## 55. RELATIVE anchor triggers on debris and launch pad structures

The AnchorDetector's 2300m threshold triggers on any nearby vessel, including: launch pad infrastructure, jettisoned fairings, decoupled stages, and debris from staging. These create RELATIVE TrackSections bound to persistent IDs that don't survive revert, causing the ghost to be hidden during playback.

**Root cause:** No filtering on vessel type. The surface-vessel check added earlier (skip LANDED/SPLASHED/PRELAUNCH) only filters the focused vessel, not the anchor candidates.

**Fix:** Added vessel type filtering in `BuildVesselInfoList` to exclude Debris, EVA, SpaceObject, and Flag vessels from anchor candidates. Also skip anchor detection entirely while on the surface (LANDED/SPLASHED/PRELAUNCH).

**Status:** Fixed

## 56. EVA recordings only created from launch pad

`OnCrewOnEva` ignores EVAs when the vessel situation is not "on pad." In-flight EVAs (suborbital, flying, orbiting) are not recorded.

**Status:** Open — medium priority (design limitation)

## 57. Boarding confirmation expired on vessel switch

After a vessel switch, a boarding event was detected but the confirmation timer expired before boarding was confirmed. The boarding was not recorded.

**Root cause:** The boarding confirmation window may be too short, or the boarding was interrupted/cancelled by the player or by another event (EVA, destruction).

**Impact:** Low — boarding not recorded, but kerbal not lost.

**Status:** Open — low priority

## 58. Background vessel recording requires KSP debris persistence enabled

When the player stages/decouples parts, KSP may instantly destroy the separated parts if debris persistence is off or the debris count limit is reached. Parsek's background recording system correctly waits for new vessels to appear in `FlightGlobals.Vessels`, but if KSP destroys them before the deferred check (one frame later), no background recording is created.

**Observed in:** Multiple test sessions (2026-03-18). All staging events classified as `WithinSegment` because no new vessel PIDs appeared after the split.

**Impact:** Booster separation ghosts, detached crew pod ghosts, and any staged-part trajectory recording depends on KSP keeping the separated vessel alive for at least 1-2 frames.

**Possible mitigations:**
1. **Pre-split snapshot:** Before the deferred joint break check, capture a snapshot of the separating part subtree. If the vessel is destroyed before the check, use the snapshot to create a minimal "debris trajectory" recording (position at separation point, ballistic propagation for visual).
2. **Immediate vessel scan:** Instead of deferring one frame, scan `FlightGlobals.Vessels` immediately in `OnPartJointBreak` to catch vessels that exist briefly before KSP destroys them.
3. **User guidance:** Document in mod settings/FAQ that debris persistence should be enabled for full recording fidelity. Add a setting check that warns the user if debris persistence is off.
4. **Synthetic debris trajectory:** When a `PartEvent.Decoupled` fires but no new vessel appears, Parsek could compute an approximate ballistic trajectory for the separated mass (using the vessel's velocity + a separation impulse) and create a visual-only ghost that shows the booster tumbling away. This wouldn't be a real recording but would look correct visually.

**Status:** Fixed — `onPartDeCoupleNewVesselComplete` hook catches debris vessels synchronously during `Part.decouple()` (FixedUpdate), before KSP can destroy them. All 4 booster separations now correctly classified as `DebrisSplit` and fed to the crash coalescer. Debris recording now works via auto-tree promotion (#66 fixed).

## 59. SoftCap ClassifyPriority logs per-frame per-ghost at VERBOSE (log spam)

`GhostSoftCapManager.ClassifyPriority` logs at VERBOSE on every call. Called per-ghost per-frame during cap evaluation. With 20+ ghosts at 60fps, produces ~1200 log lines/second. Measured at 1.09M lines / 71% of total Parsek log output in a test session.

**Fix:** Removed the per-call VERBOSE logs from `ClassifyPriority` entirely — inputs/outputs are already visible in the EvaluateCaps summary log. Also rate-limited related spam in the same commit: looped ghost spawn suppression (INFO → VerboseRateLimited 30s per ghost, was 61K lines), heat state changes (VERBOSE → VerboseRateLimited 5s per part PID, was ~20K lines during reentry).

**Status:** Fixed

## 60. Ghost map presence stubs not implemented (GhostMapPresence.cs)

`GhostMapPresence` has the pure data layer complete (`HasOrbitData`, `ComputeGhostDisplayInfo`) but 4 KSP integration points are stubbed out as TODO comments (lines 108-128):

1. **Register ghost in tracking station** — investigate whether KSP requires a `ProtoVessel` entry or if a custom `MapNode` can be created directly
2. **Create map view orbit line** — distinct color/style to differentiate from real vessel orbits, needs `PlanetariumCamera` API research
3. **Enable ghost as navigation target** — allow player to set ghost as rendezvous target for transfer planning, investigate `Vessel.SetTarget` compatibility
4. **Remove tracking entry on despawn** — clean up when ghost is destroyed or chain tip spawns

Tagged as Phase 6f-1 in code. Requires in-game API investigation.

**Status:** Open — deferred (Phase 6f)

## 61. Controlled children have no recording segments after breakup

`ParsekFlight.cs:2225` — when a crash/breakup creates a recording tree, controlled children (non-debris parts that survive) had no recording segments created for them. The code logged "deferred to Phase 2" but the multi-vessel background recording infrastructure already existed.

**Fix:** `ProcessBreakupEvent` now creates child recordings for controlled children (same pattern as debris but `IsDebris = false`, no TTL). They are added to BackgroundRecorder for trajectory sampling. This also fixes the RELATIVE anchor issue — controlled children now have recordings and can be spawned during playback, making them available as anchor vessels. `PromoteToTreeForBreakup` already handled this case correctly; only the in-tree breakup path was missing.

**Status:** Fixed

## 62. Background ghost positioning cachedIdx not persistent

`ParsekFlight.cs:5030` — `InterpolateAndPosition` for background recording ghosts resets `cachedIdx` to 0 each frame instead of caching it on the ghost state or chain. This means every frame does a full binary search instead of O(1) amortized sequential lookup. No visual impact, minor performance cost with many background ghosts.

**Status:** Open — low priority optimization

## 63. Log contract checker lacks error whitelist

`ParsekLogContractChecker.cs:99` — the `ERR-001` violation flags any ERROR-level log line as a test failure. No whitelist mechanism exists for intentional error-path test scenarios (e.g., testing that invalid input produces an expected error log). Currently no test scenarios require this.

**Status:** Open — low priority (test infrastructure)

## ~~64. Merge dialog shown twice on revert during tree destruction~~

When a vessel is destroyed during tree recording, `ShowPostDestructionTreeMergeDialog` fires and shows the merge dialog. If the user reverts to launch while the dialog is open, the scene teardown destroys the dialog but the pending tree survives in `RecordingStore.pendingTree` (static, persists across scenes). On the new flight scene, `OnFlightReady` detects the orphaned pending tree and shows the dialog again via the fallback path (`Pending tree reached OnFlightReady — showing tree merge dialog (fallback)`).

The revert detection (`isRevert=False`) does not recognize this as a revert, so no special handling kicks in.

**Repro:** Record a flight in tree mode → destroy vessel → merge dialog appears → click "Revert to Launch" → dialog appears a second time.

**Fix:** Added `DiscardPendingTree` / `DiscardPending` guards in the `isRevert` block of `ParsekScenario.OnLoad`, clearing orphaned pending state before `OnFlightReady` can trigger the fallback dialog.

**Status:** Fixed

## 65. Ghost shroud visible at playback start when already jettisoned at recording start

When a recording starts with the engine shroud already jettisoned (e.g., SRB fired before recording began), the recorder correctly seeds this as `already-jettisoned shroud` but the ghost visual builder does not apply this initial state. The ghost is built from the vessel snapshot which includes the shroud MeshRenderer. Since there is no `ShroudJettisoned` part event in the recording (the jettison happened before recording started), the shroud remains visible throughout ghost playback.

Related to bug #42 (engine shroud missing at recording start) and bug #31 (shroud rendering). The `already-jettisoned` state from `FlightRecorder.SeedInitialState` needs to be propagated to the ghost builder's initial part visibility.

**Repro:** Launch a vessel with SRB shroud → wait for shroud to jettison → start recording → stop → commit → play back ghost → shroud is visible on the ghost.

**Status:** Fixed — `PartStateSeeder.EmitSeedEvents` now emits synthetic `ShroudJettisoned` events at `startUT` for all pre-jettisoned shrouds. The existing `ApplyPartEvents` playback applies them on the first frame.

## 66. [v0.5.1] Booster/debris recording: auto-tree creation for DebrisSplit events

Booster separation was correctly **detected** (via `onPartDeCoupleNewVesselComplete` hook, see #58) but separated boosters were not **recorded**. `ProcessBreakupEvent` discarded BREAKUP events when `activeTree == null`.

**Root cause:** Background vessel recording only works in tree mode. Trees were only created by controlled vessel splits (EVA, dock/undock). A standalone recording that stages boosters never entered tree mode.

**Fix:** `PromoteToTreeForBreakup` in ParsekFlight.cs. When `ProcessBreakupEvent` fires without an active tree but with a live recorder:
1. Stops the recorder, captures all accumulated data via `StopRecordingForChainBoundary`
2. Creates a RecordingTree with root recording from CaptureAtStop (same pattern as `CreateSplitBranch`)
3. Creates a continuation recording for the active vessel (child of BREAKUP)
4. Creates debris child recordings from `CrashCoalescer.LastEmittedDebrisPids` (new — coalescer now tracks individual debris PIDs, not just count)
5. Creates BackgroundRecorder, adds debris to BackgroundMap with 30s TTL
6. Starts a new FlightRecorder for the continuation in tree mode

Tree topology: `root → BREAKUP → [continuation, debris1, debris2, ...]`

Also added `BackgroundRecorder.SetDebrisExpiry` for external TTL assignment, and clears stale chain/continuation state on promotion.

**Verified in-game:** 4 SRB separation → 4 debris children created (all alive), 43-50 trajectory points each over 30s TTL, auto-grouped under vessel name in recordings window.

**Status:** Fixed

## 67. [v0.5.1] Camera switches to spawned vessel after watch mode ends at recording boundary

When a ghost recording reaches its end UT during watch mode while time-warping, the camera correctly exits watch mode and returns to the player's vessel. However, the deferred spawn mechanism then switches the camera to the newly spawned vessel, pulling the player away from their real vessel.

**Root cause:** `pendingWatchRecordingId` was set before `ExitWatchMode()` in the warp-deferred code path (`ParsekFlight.cs` ~line 5852). When the deferred spawn executed after warp ended, `ShouldRestoreWatchMode` returned true and `DeferredActivateVessel` switched the camera to the spawned vessel.

**Fix applied:** Removed the `pendingWatchRecordingId` assignment in the recording-end exit path. Watch mode ending because a recording finished is NOT a "pause for later resumption" — it's a final exit that should not trigger camera re-targeting.

**Status:** Fixed

## 68. Ghost parts colored red despite not being hot (engine thrust color leak)

Many ghost parts appear red/orange even when they are not experiencing reentry heating. The engine thrust level coloring system was cloning materials on ALL renderers of heat-animated parts, not just the heat-affected ones.

**Status:** Fixed — `BuildHeatMaterialStates` now filters renderers by the resolved heat transform subtree. Fallback path (material-only animations) only replaces `renderer.materials` when at least one material is tracked (#86 follow-up).

## 69. Procedural fairing shows internal structure on ghost

The procedural fairing ghost displays its internal structural framework/skeleton part sticking out through the fairing shell. The fairing's internal structure mesh should be hidden on the ghost — only the outer shell panels should be visible.

**Status:** Fixed — Prefab Cap/Truss clones (at placeholder scale 2000x10x2000) are permanently hidden. A procedural truss mesh is generated from XSECTION data with horizontal cap discs and vertical struts, hidden by default and revealed on `FairingJettisoned` event. Fairing cone mesh now has a cap disc for truncated tops (#85).

## 70. Deployed solar panels shown retracted on ghost during playback

The rover ghost shows solar panels in their default (retracted) position even though they were extended at recording time. The initial state seeding correctly captures the deployment state (`solarPanels1[pid=873017503](state=EXTENDED)`), but the ghost is built from the vessel snapshot which stores parts in their default configuration. The deployed state from part events is not applied to the ghost mesh at spawn time.

Related to the general "initial part state" problem — the ghost builder needs to apply the recorded deployment state from `SeedInitialState` to the ghost's animated transforms at spawn time, similar to how engine shroud jettison state should be applied (see #65).

**Observed in:** Rover recording session (2026-03-21). Recording shows `deployables=2` (2 solar panels EXTENDED), but ghost renders them retracted.

**Status:** Fixed — `PartStateSeeder.EmitSeedEvents` now emits synthetic `DeployableExtended` events at `startUT` for all pre-extended deployables. Covers all 16 tracking set types including gear, lights, engines, RCS, thermal animations, and more.

## ~~71. GhostCommNetRelay.RegisterNode orphans CommNet nodes on double registration~~

`RegisterNode` at line 237 does `activeGhostNodes[vesselPid] = node` which overwrites any existing entry without removing the old `CommNode` from `CommNetNetwork.Instance.CommNet` first. If called twice for the same PID (e.g., ghost destroyed and recreated), the first node becomes permanently orphaned in the CommNet graph. Fix: check for existing node and `CommNet.Remove()` before registering.

**Fix:** Added guard before `CommNet.Add(node)` that checks `activeGhostNodes` for existing entry and removes the old node from CommNet first, with a warning log.

**Status:** Fixed

## 72. GhostCommNetRelay antenna combination formula wrong for non-combinable strongest

`ComputeCombinedAntennaPowerFromList` uses the strongest antenna's `antennaCombinableExponent` regardless of whether the strongest is itself combinable. KSP's actual formula uses the strongest *combinable* antenna as the base when the overall strongest is non-combinable. Result: ghost relay power is computed higher than KSP would.

**Status:** Open

## 73. SpawnCollisionDetector.CheckWarningProximity includes ActiveVessel and unfiltered vessel types

Unlike `CheckOverlapAgainstLoadedVessels` which properly excludes Debris/EVA/Flag/SpaceObject and `FlightGlobals.ActiveVessel`, the `CheckWarningProximity` method includes everything. The player's own vessel at distance ~0 would always be the "closest vessel", making proximity warnings fire constantly with the player's own name. Should apply the same filters as `CheckOverlapAgainstLoadedVessels`.

**Status:** Fixed — extracted `ShouldSkipVesselType` helper, applied in both `CheckOverlapAgainstLoadedVessels` and `CheckWarningProximity`

## 74. FlightRecorder.SamplePosition ignores RELATIVE mode at on-rails boundary

`SamplePosition` (lines 4320-4356) always records raw `v.latitude/v.longitude/v.altitude`. When called at on-rails transitions while in RELATIVE mode (before mode is cleared at line 4414 and before the track section transition at line 4423), the boundary point has absolute coordinates within a RELATIVE TrackSection, creating a potential discontinuity.

**Status:** Open — verify if the boundary point timing makes this moot in practice

## 75. GhostPlaybackLogic inconsistent negative interval handling

`ComputeLoopPhaseFromUT` (line 275) clamps negative intervals to 0 via `Math.Max(0, intervalSeconds)`, but `TryComputeLoopPlaybackUT` (line 91) and `GetActiveCycles` (line 148) allow negative intervals (overlapping cycles). These methods compute related loop timing — the disagreement could cause visual glitches (wrong phase or missing overlap cycles) when negative intervals are used.

**Status:** Open

## 76. GhostExtender.PropagateOrbital hyperbolic fallback can return negative altitude

Lines 90-97: For `ecc >= 1.0 || sma <= 0`, the fallback returns `(0, 0, sma - bodyRadius)`. When `sma < bodyRadius`, this produces a negative altitude, placing the ghost underground. Should use `Math.Max(0, sma - bodyRadius)`.

**Status:** Mitigated — the LateUpdate terrain clamp (`6996b65`) prevents any ghost from appearing below terrain, and the SMA sanity check in `InterpolateAndPosition` (`ea14b8f`) rejects orbit segments with SMA < 90% body radius. The root GhostExtender fallback formula is still uncorrected.

## 77. TerrainCorrector log format strings use system culture

Lines 37, 42-44 in `TerrainCorrector.cs` use `$"{corrected:F1}"` string interpolation which uses the system locale (comma on some machines). Inconsistent with the project's InvariantCulture policy. Only affects log output, not gameplay or serialization.

**Status:** Fixed — added `CultureInfo.InvariantCulture` field and replaced all 8 `{val:F1}` interpolation sites with `.ToString("F1", IC)`

## 78. RecordingTree.DetermineTerminalState maps DOCKED to Orbiting

`DetermineTerminalState` maps the KSP `DOCKED` situation to `TerminalState.Orbiting`. When called from `EndDebrisRecording`, a debris piece that docks would get `Orbiting` instead of a more appropriate terminal state. Edge case but semantically wrong.

**Status:** Open — unlikely to occur for debris

## ~~79. TimeJumpManager.ExecuteJump mutates caller's chains dictionary~~

Line 278: `chains.Remove(chain.OriginalVesselPid)` is a side effect on the caller's dictionary, not documented in the method signature. Could cause subtle issues if the caller iterates `chains` after calling `ExecuteJump`. The caller should decide which chains to remove.

**Fix:** `SpawnCrossedChainTips` now returns a `List<uint>` of spawned PIDs without mutating the input dict. The caller (`ExecuteJump`) removes them after the call.

**Status:** Fixed

## 80. TimeJumpManager.ExecuteJump has no guard against active time warp

If called during time warp (warp rate > 1), `Planetarium.SetUniversalTime` can interact badly with KSP's internal warp state. There is no guard to ensure warp is stopped before the jump.

**Status:** Open — needs in-game verification

## 81. TrackSection struct shallow copy shares mutable list references

`Recording` copy constructor does `new List<TrackSection>(source.TrackSections)` which copies struct values but shares `frames`/`checkpoints` list references between source and copy. Currently safe because recordings are read-only after construction, but fragile if any code later mutates a copied recording's track section frames.

**Status:** Open — fragile but not currently triggered

## 82. IsDebris, Controllers, SurfacePos not serialized for standalone recordings

These fields are serialized in the `RecordingTree` code path but not in `ParsekScenario.SaveStandaloneRecordings` / `LoadStandaloneRecordingsFromNodes`. Standalone recordings lose these values across save/load. May be intentional if standalone recordings are never debris.

**Status:** Open — verify if standalone debris recordings can exist

## 83. GhostCommNetRelay.ReregisterAllNodes re-adds potentially stale CommNode objects

After CommNet reinitialization, the existing `CommNode` objects in `activeGhostNodes` are re-added via `commNet.Add(kvp.Value)`. If KSP's CommNet reinitialization invalidates old node objects, re-adding them could fail silently. Consider creating fresh `CommNode` objects with the same data.

**Status:** Open — needs in-game verification

## ~~84. GhostPlaybackLogic.ComputeLoopPhaseFromUT integer overflow risk~~

Line 289: `int cycleIndex = (int)(elapsed / cycleDuration)` — for very long-running loops with short cycle durations, the double value can exceed `Int32.MaxValue`. The cast produces undefined behavior in C# (typically `Int32.MinValue` or garbage). A 0.1s recording looping for 250+ in-game days would overflow.

**Fix:** Changed `cycleIndex` from `int` to `long` across all loop phase calculation sites: `ComputeLoopPhaseFromUT`, `GetActiveCycles`, `TryComputeLoopPlaybackUT` (static and instance), `GhostPlaybackState.loopCycleIndex`, event types (`LoopRestartedEvent`, `OverlapExpiredEvent`, `CameraActionEvent`), `GhostSoftCapManager.ClassifyPriority`, `ParsekKSC.TryComputeLoopUT`/`UpdateSingleGhostKsc`, and `ParsekFlight.watchedOverlapCycleIndex`.

**Status:** Fixed

## 85. Fairing nosecone cap missing on ghost

The generated fairing cone mesh (`GenerateFairingConeMesh`) builds a surface-of-revolution from XSECTION data but may not close off the top with a cap face. The topmost fairing cap piece (the nose tip) is visually missing on the ghost. The Cap1-Cap6 transforms from AutoTruss are interstage discs, not the nosecone.

**Repro:** Build a vessel with a procedural fairing → launch → commit → play back ghost → top of fairing has a hole.

**Status:** Fixed — `GenerateFairingConeMesh` now generates a flat disc cap when the top XSECTION has non-zero radius (capRadius).

## 86. Heat material fallback still colors non-heat renderers (material-only animations)

Bug #68 fix limits material cloning to heat-animated transforms, but `FXModuleAnimateThrottle` animations like `overheat` on engines (e.g., `engineLargeSkipper`) animate material properties only (no transform deltas). `SampleAnimateHeatStates` returns 0 transform deltas, triggering the clone-all fallback. All 7 materials are cloned but only 4 are tracked — the 3 untracked clones keep stale colors, causing the red tint on non-heat parts.

**Log evidence:** `AnimateHeat 'engineLargeSkipper.v2': no affected transforms provided, cloning all renderers (fallback)` — `tracked=4 cloned=7`

**Fix:** In the fallback path, don't clone materials that won't be tracked. Only clone materials that have `_Color` or emissive properties. Leave untracked materials as `sharedMaterials`.

**Status:** Fixed — `BuildHeatMaterialStates` fallback path now only assigns `renderer.materials = cloned` when at least one material on that renderer is tracked in `materialStates`.

## 87. Chain boundary commit creates group inside group in UI

When a chain recording hits a boundary split (e.g., atmosphere exit), the committed segments appear as a nested group inside a group in the recordings UI. The RecordingGroup logic may be double-nesting chain segments.

**Root cause:** `HandleAtmosphereBoundarySplit` and `HandleSoiChangeSplit` had no `activeTree != null` guard. During tree mode (after booster separation via `PromoteToTreeForBreakup`), boundary splits still fired and committed chain segments as standalone recordings with group names via `CommitBoundarySplit`, leaking data outside the tree. When `CommitTree` later added the same group name, recordings appeared in both a chain block and tree recordings under the same group.

**Fix:** Added `activeTree != null` early-return guards to both handlers. In tree mode, the recorder keeps accumulating data into the tree recording continuously — boundary crossings are noted in the log but don't trigger standalone chain commits. `ClearBoundaryFlags()` method added to `FlightRecorder` to reset the flags without stopping the recorder.

**Status:** Fixed

## 88. No merge/approval dialog shown on recording commit

After recording completes (via chain boundary or scene change), the recording is auto-committed without showing a dialog asking the user to approve. The merge dialog is currently only shown on revert, not on normal commit flow. User expects to be asked before committing.

**Status:** Open — design decision needed: should normal commits require approval?

## 89. Watch button enabled for distant ghosts beyond visual range

Pressing Watch (W) on a ghost that is beyond visual range (100km+) switches the camera to it, then immediately exits watch mode because the ghost exceeds range. The Watch button should be disabled (or the switch should be prevented) when the ghost is too far away.

**Log evidence:** `EnterWatchMode` at 161km → `Watch exited: ghost exceeded visual range (163142m)` after 2 seconds.

**Status:** Fixed — Watch button now requires `IsGhostWithinVisualRange` (currentZone != Beyond). Tooltip shows "Ghost is beyond visual range" when disabled.

## 90. Watch button enabled for recordings from the past

The Watch (W) button is shown enabled for a recording whose time range is entirely in the past (already finished playing). Watching a finished recording is meaningless — the ghost doesn't exist. The button should be disabled for recordings outside the current playback window.

**Status:** Fixed — Non-looping past ghosts are destroyed (hasGhost=false → button disabled). Looping ghosts that drift beyond range are caught by the visual range check (#89).

## 91. Fairing internal structure shown on ghost when disabled on real vessel

`GetFairingShowMesh` reads `showMesh` from `ModuleStructuralNodeToggle` in the snapshot but returns `True` even when the player set it to hidden. Log shows `showInternalOnJettison=True` when the real vessel has structure hidden. Either the snapshot doesn't contain the updated `showMesh` value, or the field name is wrong.

**Status:** Fixed — Prefab Cap/Truss meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can adjust. Procedural truss mesh generated from XSECTION data replaces them. The `showMesh` field in snapshots was always `True` for this vessel — the visual issue was the enormous prefab-scale meshes, not a reading error.

## 92. Zone rendering policy tests expect old Visual-zone behavior

`GetZoneRenderingPolicy(Visual)` and `ShouldApplyPartEventsForZone(Visual)` were changed to apply part events in the Visual zone (2.3-120km) so that structural changes (fairing jettison, staging, crash destruction) work at altitude. Two existing tests still assert the old behavior (`skipPartEvents = true` for Visual zone). Update them to expect `skipPartEvents = false` and `ShouldApplyPartEventsForZone(Visual) = true`.

**Status:** Fixed (commit 90b73d8)

## 93. Surface vehicle ghost slides away from recorded position during on-rails playback

When a surface vessel (rover, lander) goes on rails during recording (e.g., time warp), an orbit segment is created with SMA ≈ half the body radius. During playback, `InterpolateAndPosition` prioritizes orbit segments over point-based interpolation. The Keplerian orbit path goes through the planet interior — the terrain clamp corrects altitude but the lat/lon follows the orbital path, causing the ghost to slide along the ground away from the runway/landing site. Observed with Crater Crawler rover on the KSC runway: ghost altitude fell to -6281m (clamped back to 65.3m each frame), but the ghost drifted off its recorded position.

**Status:** Fixed (`6996b65`, `ea14b8f`) — three-layer fix: (1) skip orbit segment creation for LANDED/SPLASHED/PRELAUNCH vessels, (2) `IsSurfaceAtUT` skips orbit segments during playback for surface TrackSections, (3) SMA < 90% body radius sanity check rejects sub-surface orbits

## 94. Recovering a spawned vessel prevents re-spawn after revert

Recovering a vessel spawned by Parsek (e.g. cleaning the runway) fires `onVesselRecovered`, which stamps `TerminalState.Recovered` on the committed recording AND nulls its `VesselSnapshot` via `UpdateRecordingsForTerminalEvent`. Both persist through reverts, permanently preventing the ghost from spawning a real vessel. Real Spawn Control also shows 0 candidates.

**Status:** Fixed — `UpdateRecordingsForTerminalEvent` now skips committed recordings where `VesselSpawned == true` or `SpawnedVesselPersistentId != 0`. Defense-in-depth: all three reset paths (Rewind, tree revert, standalone revert) clear post-spawn `Recovered`/`Destroyed` terminal state.

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

## 96. Ghost disappears between recording end and real vessel spawn

When a ghost reaches the end of its recording, it is despawned immediately. If the real vessel spawn is blocked (bounding box overlap) or deferred (warp flush), there is a visible gap where nothing exists at that position — the ghost is gone but the real vessel hasn't appeared yet. The ghost should remain visible at its last recorded position until the real vessel actually spawns, so there is no visual discontinuity.

**Priority:** Medium — cosmetic but noticeable, especially on the runway where spawn blocking is common

**Status:** Fixed — ghost is now held at its final position when spawn is blocked or warp-deferred. `ParsekPlaybackPolicy.HandlePlaybackCompleted` checks spawn success via `rec.VesselSpawned` and adds to `heldGhosts` dict instead of destroying. `RetryHeldGhostSpawns()` runs each frame after engine update, retrying spawn and destroying the ghost on success or after a 5-second real-time timeout. Pure decision logic in `DecideHeldGhostAction` (tested).

## 97. Recording segmentation and grouping for selective looping

Recordings currently capture entire flights as monolithic units. For effective looping and playback control, recordings should be segmentable into phases (takeoff, cruise, landing, docking approach, etc.) so that interesting segments (e.g. a takeoff or landing) can be looped independently. Segments that are visually uninteresting or far from points of interest (long cruise legs, on-rails coasting) should be mergeable into larger compressed segments that play back efficiently without per-frame overhead. This would allow the player to loop a busy runway with takeoffs and landings while skipping the mid-flight portions that happen far away.

**Priority:** Medium — quality-of-life for players building complex multi-mission scenes

**Status:** Open

## 98. Deleting and recreating a save with the same name leaks old recordings

`initialLoadDone` is a static bool that gates whether `OnLoad` clears and reloads recordings from the `.sfs` (initial load) or keeps in-memory state (revert/scene-change). A save-folder-change check at line 227 resets it when the save name differs. But deleting a career and creating a new one with the same name produces an identical `HighLogic.SaveFolder` — `initialLoadDone` stays `true`, so the new save inherits the old save's in-memory recordings. The `Parsek/Recordings/` sidecar files also persist on disk since KSP's save deletion doesn't know about the Parsek subdirectory.

Fix options: (a) compare a fingerprint beyond just the folder name (e.g. save creation timestamp or recording count vs .sfs node count), or (b) reset `initialLoadDone` on main menu entry (`GameScenes.MAINMENU`), or (c) hash the .sfs RECORDING node IDs and compare against in-memory IDs.

**Priority:** Low — only triggers when recreating a save with an identical name in the same KSP session

**Status:** Fixed (commit d398cac — reset initialLoadDone on main menu transition)

## 99. KSC view does not spawn real vessels when ghost timelines complete

At SpaceCenter, ghost playback is visual-only (KSC ghosts rendered by `ParsekKSC`). When a ghost reaches its recording end time, the ghost mesh despawns but no real vessel is spawned. In Flight scene, `UpdateTimelinePlayback` triggers `VesselSpawner.SpawnOrRecoverIfTooClose` when a ghost exits range — this has no equivalent at KSC.

The user expects that after time-warping past all recording end times at KSC, the runway should show the recorded vessels as real spawned vessels (visible in Tracking Station, switchable, persistent). Instead they simply vanish.

**Priority:** Medium — breaks the mental model that recordings produce real vessels regardless of which scene the player is in

**Status:** Fixed — `ParsekKSC.TrySpawnAtRecordingEnd` calls `VesselSpawner.RespawnVessel` when a ghost exits range or time-warps past its end. Uses `GhostPlaybackLogic.ShouldSpawnAtKscEnd` with chain mid-segment suppression via `IsChainMidSegment` (only chain tips spawn, not intermediate segments). Chain looping/disabled derived from RecordingStore. Looping recordings skipped, dedup via `kscSpawnAttempted` HashSet. `ParsekScenario.OnSave` auto-unreserve guarded to skip SpaceCenter scene (prevents snapshot nulling before KSC spawn runs). Spawned vessels appear in Tracking Station and persist in save.

## 100. Compress Launch / Countdown / Status columns in Recordings Manager

The Recordings Manager has three columns that all describe where a recording sits in time: **Launch** (absolute UT), **Countdown** (T- until start), and **Status** (past/active/future + terminal state). These are redundant — at any given moment, only one piece of information is most relevant.

**Option A — Single "State" column (most compact):** Display what's most relevant for the recording's temporal phase. Future: `T-2m 30s` (countdown *is* the state). Active: `Playing` or progress indicator. Past: `Landed` / `Orbiting` / `Recovered` (terminal state *is* the state). Color-code by phase (future=gray, active=green, past=yellow). Eliminates all three columns in favor of one.

**Option B — Two columns ("Time" + "State"):** Time shows countdown if future, launch UT if past, blank if active. State shows terminal state for past, `Playing` for active, `Pending` for future.

**Option C — Keep Launch, merge T- into Status:** Launch stays as an absolute reference (needed for rewind decisions). Status absorbs countdown: shows `T-2m 30s` when future, `Active` when playing, terminal state when past.

**Decision: Option C.** Keep Launch as an absolute reference for rewind planning. Merge Countdown into Status: show `T-2m 30s` when future, `Active` when playing, terminal state (`Landed`/`Orbiting`/`Recovered`) when past. Removes one column, keeps the information users need.

**Priority:** Low — UI polish, no functional impact

**Status:** Open

## ~~101. BackgroundRecorder.SubscribePartEvents never called~~

`BackgroundRecorder.SubscribePartEvents()` (line ~180) subscribes to `GameEvents.onPartDie` and `onPartJointBreak` for background vessels, but is never called from any tree creation path (`CreateSplitBranch`, `PromoteToTreeForBreakup`). Background vessel part destruction events are not captured. Trajectory sampling works (via `PhysicsFramePatch`), so the impact is cosmetic — part events on background debris/vessels won't appear in their recordings.

**Fix:** Added `backgroundRecorder.SubscribePartEvents()` call after `BackgroundRecorder` construction in both `CreateSplitBranch` and `PromoteToTreeForBreakup`.

**Status:** Fixed

## ~~102. CreateSplitBranch omits FlagEvents and SegmentEvents in root recording~~

`CreateSplitBranch` (ParsekFlight.cs ~line 1515-1518) copies Points, OrbitSegments, PartEvents, and TrackSections from `CaptureAtStop` into the root recording but omits `FlagEvents` and `SegmentEvents`. If flags were planted or segment events occurred before the first tree split, they are lost from the root recording. The newer `PromoteToTreeForBreakup` method copies all six data lists.

**Fix:** Added `FlagEvents` and `SegmentEvents` copy lines alongside the existing `PartEvents` copy in `CreateSplitBranch`.

**Status:** Fixed

## 103. Group headers show raw #autoLOC keys instead of resolved vessel names

KSP stock vessels use `#autoLOC_XXXXX` localization keys as vessel names (e.g., `#autoLOC_501220` = GDLV3). These keys propagate into `Recording.VesselName`, `RecordingTree.TreeName`, and group names without resolution. The recordings window shows `#autoLOC_501220 (6)` as the group header instead of `GDLV3 (6)`.

`KSP.Localization.Localizer.Format()` is never called anywhere in the codebase. The fix should resolve names at storage time (when `VesselName`/`TreeName` are assigned from `Vessel.vesselName`) so all 9+ display sites get human-readable names. A `ResolveVesselName(string)` helper that calls `Localizer.Format()` when the name starts with `#` would cover all input sites.

**Priority:** Medium — every stock vessel shows unreadable group headers

**Status:** Fixed (commit 1104b7e — `ResolveVesselName` via `Localizer.Format()` at storage time)

## 104. Multiple launches of same vessel merge into one recording group

`RecordingStore.CommitTree` (line ~309) uses `tree.TreeName` verbatim as the group name. When the same craft is launched multiple times, all trees get the same group name and their recordings collapse into a single group. No way to tell which launch a recording belongs to.

Same issue affects chain recordings: `chainGroupName = RecordingStore.Pending.VesselName` (lines ~2940, ~3554, ~3716 in ParsekFlight.cs).

Fix: disambiguate with a launch number suffix. Check existing group names via `RecordingStore.GetGroupNames()` and append ` (2)`, ` (3)`, etc. Similar to `ParsekUI.GenerateUniqueGroupName` which already implements this pattern for manually-created groups.

**Priority:** Medium — confusing UI when same craft is launched multiple times

**Status:** Fixed — `RecordingStore.GenerateUniqueGroupName` checks existing group names and appends ` (2)`, ` (3)` etc. Applied at all 4 call sites: `CommitTree`, EVA chain, dock/undock chain, boundary split chain.

## 105. Colored bubbles visible in ghost engine/RCS plume FX

Ghost engine and RCS plumes showed colored bubble/sphere artifacts. Root cause: ghost plumes had TWO particle emission sources fighting each other — `KSPParticleEmitter.EmitParticle()` (creates correctly-textured particles) and Unity's emission module (`emission.rateOverTimeMultiplier`) which creates particles using `ParticleSystem.main.startSize` and `ParticleSystemRenderer.material`, neither of which are set from KSP values, producing huge material-less "bubbles".

Fix: Permanently disable Unity's emission module (`emission.enabled = false`) at build time. Keep `KSPParticleEmitter` alive (it handles material setup and particle creation) but control it via reflection (`emit` field) in `SetEngineEmission`/`SetRcsEmission`. `StripKspFxControllers` captures `KSPParticleEmitter` references into `kspEmitters` lists on `EngineGhostInfo`/`RcsGhostInfo` instead of destroying them. `SmokeTrailControl` and `FXPrefab` are still stripped.

**Priority:** Medium — visually distracting on every engine/RCS ghost

**Status:** Fixed

## 106. Watch mode camera follows booster instead of main vessel at BREAKUP

When watching a ghost via Watch mode (W button), at booster separation the camera jumps to follow a separated booster instead of staying with the main core stage.

**Root cause:** `FindNextWatchTarget` (ParsekFlight.cs:8500) correctly prefers children matching the root's `VesselPersistentId`. But the continuation recording often has 0 trajectory points (data flushed at commit time via `FlushRecorderToTreeRecording`, but if the vessel was destroyed shortly after promotion, the continuation may be empty). Ghost spawning skips recordings with `Points.Count < 2` (line 6090-6093), so the continuation ghost is never created. `FindNextWatchTarget` falls back to the first debris child with an active ghost.

**Secondary causes:**
1. Dictionary iteration order in `CommitTree` (`tree.Recordings.Values`) is non-deterministic — a debris recording may appear at a lower committed index than the continuation
2. Brief `watchedRecordingIndex = -1` gap during `TransferWatchToNextSegment` between `ExitWatchMode` and re-assignment could allow KSP camera to re-target

**Fix approach:** Ensure the continuation has trajectory data even when the vessel is short-lived. The root recording's points extend past `ExplicitEndUT` (they cover the 0.5s coalescing window) — the continuation could inherit the post-BREAKUP subset of the root's points at promotion time. Alternatively, `FindNextWatchTarget` could fall back to PID-matching against committed recordings directly (not requiring an active ghost).

**Priority:** Medium — camera behavior surprise during watched booster separations

**Status:** Fixed (`3bd66ea`) — continuation now seeded with post-breakup points at promotion time. Pre-fix saves with 0-point continuations are not migrated; creating a new career save resolves the issue.

## 107. Engine/SRB smoke trails vanish instantly when ghost despawns

When a ghost vessel is destroyed (recording ends, zone exit, loop cycle boundary), all particle systems are destroyed with the ghost GameObject. Engine and SRB exhaust trails that are still visually fading out disappear instantly instead of persisting until they naturally decay. Stock KSP smoke trails linger for several seconds after engine cutoff — ghost trails should do the same.

**Fix approach:** Before destroying the ghost GameObject, detach active particle systems with `ParticleSystem.Stop(withChildren: true, stopBehavior: ParticleSystemStopBehavior.StopEmitting)` and re-parent them to a temporary holder object. The particles continue rendering with existing lifetime/fade settings. Add a cleanup component that destroys the holder once all particles have expired (`ParticleSystem.particleCount == 0`). Only detach systems that have `isPlaying == true` or `particleCount > 0` at despawn time.

**Priority:** Low — cosmetic polish, no functional impact

**Status:** Open

## 108. EngineShutdown event not recorded when engine cuts off

Engine throttle/shutdown events not recorded for some engine types. Ghost engine plumes stay at full intensity from ignition until shutdown (no throttle response), or never stop if shutdown is also missing. Observed on Aeris-4A (save `s10`) with `turboFanEngine` (J-X4 "Whiplash" Turbo Ramjet, `ModuleEnginesFX`): playback log shows `EngineIgnited` and `EngineShutdown` but zero `EngineThrottle` events between them. An earlier recording of the same craft had 238 throttle events but no shutdown — inconsistent behavior suggests a timing or caching issue in `CheckEngineTransition` / `CacheEngineModules`.

Likely cause: `CheckEngineTransition` may not detect the `EngineIgnited → false` transition correctly, or the transition check polls `engine.EngineIgnited && engine.isOperational` which may remain true in certain states (e.g., fuel depletion vs. manual shutdown).

**Partial fix:** `FlightRecorder.EmitTerminalEngineAndRcsEvents` emits synthetic `EngineShutdown`, `RCSStopped`, and `RoboticMotionStopped` events for all entries still in `activeEngineKeys`/`activeRcsKeys`/`activeRoboticKeys` at recording end. Called from `StopRecording()`, `StopRecordingForChainBoundary()`, and `BackgroundRecorder.FinalizeAllForCommit()`. This guarantees ghost plumes shut down at recording boundaries. The original Aeris-4A mid-flight detection issue (missing throttle events / missed shutdown transition during normal polling) may be a separate problem.

**Priority:** Medium — ghost engines keep burning past cutoff, visually incorrect

**Status:** Partially fixed (terminal events added; mid-flight detection issue may remain)

## 109. Missing CleanupOrphanedSpawnedVessels on second Rewind flight-ready

After a second Rewind, `CleanupOrphanedSpawnedVessels` does not run, leaving a previously-spawned vessel in the scene at the spawn coordinates. The first Rewind correctly recovers the old spawned vessel, but the second one skips cleanup entirely. This leaves a stale vessel that blocks future spawns at that location.

**Root cause:** After a rewind, the rewind path in `OnLoad` sets `PendingCleanupPids`/`PendingCleanupNames`, then `ResetAllPlaybackState` zeros all spawn tracking. When the false-positive revert path fires on the subsequent SpaceCenter-to-Flight transition, `CollectSpawnedVesselInfo()` returns empty (PIDs are zero) and overwrites the rewind data with null. `OnFlightReady` then sees null and skips cleanup.

**Fix:** Added a guard in the revert path: if `PendingCleanupPids` or `PendingCleanupNames` are already set, skip collection and keep the existing data. Also changed the flightState strip to use `RecordingStore.PendingCleanupNames` (the authoritative source) instead of the local variable. Added entry/skip logging to `CleanupOrphanedSpawnedVessels` and `OnFlightReady`.

**Priority:** High

**Status:** Fixed

## 110. Spawn collision retry has no limit — infinite loop on permanent overlap

When a spawn is permanently blocked by an immovable vessel (e.g., a landed spawned vessel from a previous cycle that wasn't cleaned up), the spawn retry loop runs every frame indefinitely (~8ms per retry). In one test session this produced 6,270 retries over 27 seconds, flooding ~24,000 lines into KSP.log until the user exited to the main menu. There is no retry limit, timeout, or backoff.

**Fix:** Added `CollisionBlockCount` field to Recording and `MaxCollisionBlocks = 150` (~2.5s at 60fps) constant to VesselSpawner. After 150 consecutive collision-blocked frames, the spawn is abandoned (`VesselSpawned = true` prevents further attempts) and a WARN is logged. The per-frame overlap log was changed from `ParsekLog.Info` to `ParsekLog.VerboseRateLimited` to prevent log flooding. For chain-tip spawns, `WalkbackExhausted` flag on GhostChain prevents repeated trajectory re-scanning after walkback found no valid position. `SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels` per-vessel overlap log also rate-limited. SpawnWarningUI updated with "walkback exhausted" and "spawn abandoned" status text.

**Priority:** High

**Status:** Fixed (branch `fix/spawn-cleanup`)

## 110b. Spawn-die-respawn infinite loop for FLYING vessels

When a recording's end-of-timeline spawn creates a vessel with `sit=FLYING` at sea level, KSP's on-rails aero check immediately destroys it (101.3 kPa pressure). The "spawned vessel gone" check resets spawn state and the next frame spawns again. This produced 24,000+ spawn-die cycles in one 18-minute session (~960K of 1M log lines). Side effects include unbounded `CrewStatusChanged` event accumulation (3,900+ events) and crew status corruption.

**Root cause:** The vessel-gone check unconditionally resets `VesselSpawned=false` when the spawned vessel disappears, with no counter for how many times this has happened. Unlike the collision-block loop (#110), the spawn SUCCEEDS but the vessel immediately dies.

**Fix:** Added `SpawnDeathCount` field to Recording. The vessel-gone check increments it each time a spawned vessel disappears. After `MaxSpawnDeathCycles` (3) cycles, sets `SpawnAbandoned=true` to permanently stop retries. Resets on revert/load (transient field).

**Priority:** Critical

**Status:** Fixed (branch `fix/spawn-cleanup`)

## 111. Auto-record fails for spawned vessels (settle timer not seeded on vessel switch)

When Parsek spawns a vessel from a recording and switches the active vessel to it, `lastLandedUT` is never initialized. Spawned vessels are created directly in LANDED state — there is no situation *transition*, so `OnVesselSituationChange` never fires. `OnVesselSwitchComplete` also did not seed the timer. When the user takes off (LANDED → FLYING), `settledTime` computes as 0 (fallback from `lastLandedUT = -1`), which is less than the 5-second settle threshold, and auto-recording is silently suppressed.

**Repro:** Rewind to a recording → let the timeline ghost play and spawn a real vessel → camera switches to it → user throttles up and takes off → no recording starts. Log shows: `OnVesselSituationChange: LANDED → FLYING after 0.0s (< 5s settle threshold)`.

**Fix approach:** Seed `lastLandedUT` in `OnVesselSwitchComplete` when the new active vessel is already in LANDED or SPLASHED, mirroring the existing init-time seed at `OnFlightReady`.

**Priority:** High

**Status:** Fixed (branch `fix/settle-seed-on-vessel-switch`)

## 112. Aeris 4A spawn blocked by own spawned copy — permanent overlap

After rewinding and re-entering flight, the Aeris 4A recording's spawn-at-end tried to place a new vessel at the same position where a previously-spawned (but not cleaned up) Aeris 4A already sat. The spawn collision detector correctly blocked it, but because the overlap is permanent (both vessels at the same runway position), this triggered bug #110's infinite retry loop. The log showed `Spawn blocked: overlaps with #autoLOC_501176 at 5m — will retry next frame` repeating every frame for the remainder of the session.

**Root cause:** `CleanupOrphanedSpawnedVessels` recovered one copy, but a second Aeris 4A was loaded from the save and occupied the spawn slot. The duplicate presence is partly caused by bug #109 (cleanup skipped on second rewind, leaving a stale vessel in the save). The spawn system has no dedup against already-present matching vessels.

**Note:** The infinite retry loop symptom is resolved by bug #110's fix (spawn abandoned after 150 frames). The root cause of duplicate vessel presence remains a separate issue.

**Priority:** Medium (consequence of #110 infinite retry — infinite loop symptom now resolved)

**Status:** Open (root cause of duplicate vessel remains; infinite loop symptom resolved by #110 fix)

## 113. Audit ghost FX for KSP-native component usage

Bug #105 revealed that fighting KSP's own FX components (KSPParticleEmitter, ModelMultiParticleFX) produces artifacts. The fix — letting KSP handle particle creation natively and controlling `emit` via reflection — produced much better visuals than any approach that tried to replace KSP's emission with Unity's.

Audit all ghost visual systems for similar opportunities to use KSP-native components instead of reimplementing or stripping them:
- **Smoke trails**: currently strip `SmokeTrailControl` which fades smoke by atmospheric density. Could we keep it and let KSP handle density-based fading?
- **Engine heat glow**: `FXModuleAnimateThrottle` animates engine heat materials. Ghost code reimplements this with `HeatGhostInfo`. Could we keep the stock module and drive it?
- **RCS glow**: `FXModuleAnimateRCS` handles RCS nozzle glow. Same question.
- **Other FX components**: check if any stripped components would produce better visuals if kept alive and controlled

General principle: prefer toggling KSP's own components on/off via reflection over reimplementing their behavior. KSP's components handle edge cases (material setup, animation curves, density fading) that are hard to replicate correctly.

**Priority:** Low — improvement opportunity, not a bug

**Status:** Open

## 113. Audit ghost FX for KSP-native component usage

Bug #105 revealed that fighting KSP's own FX components (KSPParticleEmitter, ModelMultiParticleFX) produces artifacts. The fix — letting KSP handle particle creation natively and controlling `emit` via reflection — produced much better visuals than any approach that tried to replace KSP's emission with Unity's.

Audit results:
- **KSPParticleEmitter**: KEPT alive, controlled via `emit` reflection (bug #105 fix, already merged)
- **SmokeTrailControl**: STRIPPED — tested keeping alive but it sets material alpha to 0 on ghosts, making smoke invisible. Needs vessel context to work correctly
- **ModelMultiParticlePersistFX / ModelParticleFX**: KEPT ALIVE — initially stripped (audit #113) but this killed smoke trails. These drive smoke emission; any NREs from missing Part context are non-fatal
- **FXPrefab**: STRIPPED — registers particles with FloatingOrigin, pollutes global state on ghosts
- **FXModuleAnimateThrottle**: KEEP REIMPLEMENTATION — PartModule requiring Part/Vessel context. Current HeatGhostInfo animation sampling is correct: one-shot cached build cost, near-zero runtime (3-level quantized snaps), correct multi-instance disambiguation
- **FXModuleAnimateRCS**: KEEP REIMPLEMENTATION — same PartModule constraints, shares HeatGhostInfo infrastructure

**Priority:** Low — improvement opportunity, not a bug

**Status:** Done

## 114. Non-leaf tree recording spawns vessel at crash endpoint — immediate destruction

When a recording tree ends because the vessel crashed, recording #0 (the root, pre-crash flight) reaches its endpoint and Parsek spawns a real vessel there. But the endpoint was where the vessel was still FLYING at high speed. Spawned as LANDED, the gear snaps and the entire vessel cascade-explodes. The tree's children (crash debris) represent the final state — the root recording should NOT spawn.

**Observed in:** Session 3 (2026-03-22). Aeris 4A crash tree: root recording UT 53-78, crash at UT 100-102. Ghost exits range at UT 78 → spawns vessel → immediate explosion. Happened twice (once after revert, with crew dedup removing Jeb too).

**Root cause:** `ShouldSpawnAtRecordingEnd` had a `ChildBranchPointId` check but this could be null in edge cases (serialization gaps, commit-path variations). Additionally, the snapshot's `sit=FLYING` was not checked independently of `TerminalState`.

**Fix:** Three-layer defense in `ShouldSpawnAtRecordingEnd`:
1. Safety-net tree check: `IsNonLeafInCommittedTree` scans committed tree branch points for parent references, catching non-leaf recordings even when `ChildBranchPointId` is null.
2. Snapshot situation check: `IsSnapshotSituationUnsafe` blocks spawn when snapshot `sit` is FLYING or SUB_ORBITAL, independent of `TerminalState`.
3. Crew protection: `ShouldStripCrewForSpawn` / `StripAllCrewFromSnapshot` strips all crew from spawn snapshots for `TerminalState.Destroyed` recordings, preventing crew death during spawn-death cycles.

28 new tests in `SpawnSafetyNetTests.cs` covering all three mechanisms.

**Priority:** Critical — causes cascading destruction, crew death, and confusing UX

**Status:** Fixed (branch `fix-spawn-cleanup`)

## 115. Crew dedup removes pilot from spawned vessel after revert

After revert, the pilot (Jebediah) is already on the pad vessel from the quicksave. When Parsek spawns the Aeris 4A at recording endpoint, crew dedup finds Jeb "already on a vessel in the scene" and removes him from the spawn snapshot. The vessel spawns crewless.

**Root cause:** The crew reservation system should have replaced Jeb with a temporary kerbal on the pad vessel, but after revert + tree commit, the reservation may have been cleared. Partially mitigated by #114 fix: non-leaf and FLYING recordings no longer spawn, eliminating the primary trigger for this bug.

**Priority:** Medium

**Status:** Open (mitigated by #114 fix — primary trigger eliminated)

## 116. Valentina Kerman lost to Missing status after rewind vessel strip

Rewind stripped 8 orphaned spawned vessels from the save. Valentina was assigned to one of them but wasn't in Parsek's crew reservation system. KSP set her to Missing. Parsek's crew rescue only handles reserved crew (Bill, Dudfrid were rescued), not all crew affected by the strip. Partially mitigated by #114 crew protection: destroyed-vessel spawns now strip crew from snapshot, preventing crew from being placed on doomed vessels.

**Priority:** Medium

**Status:** Open (mitigated by #114 crew protection — fewer crew placed on doomed vessels)

## 117. CanRewind/CanFastForward VERBOSE log spam — 578K lines/session

`RecordingStore.CanRewind` and `RecordingStore.CanFastForward` log at VERBOSE on every blocked call. Called per-recording per-frame from UI. With active recording + multiple recordings, produces 374K+ CanRewind and 184K+ CanFastForward lines in a single session (94% of all log output). Makes log analysis extremely difficult.

**Fix:** Removed all blocked-path VERBOSE logs from both methods. These are read-only UI state checks called per-recording per-frame — the `reason` out-parameter already conveys the block reason to the caller without needing log output. Success-path logs were already removed in #52.

**Priority:** High — blocks effective log analysis

**Status:** Fixed

## 118. "Failed to register main menu hook" WARN on every non-MAINMENU OnLoad

`ParsekScenario.OnLoad` logs WARN when main menu hook registration fails outside MAINMENU scene. Expected behavior (retries on next OnLoad), but WARN level is too high for a benign retry.

**Fix:** Downgraded from `ParsekLog.Warn` to `ParsekLog.Verbose`. This is a benign retry path — not an error condition.

**Priority:** Low

**Status:** Fixed

## 119. Watch mode exits when ghost exceeds 120km — camera is at the ghost

During watch mode, zone distance is measured from the ghost to `FlightGlobals.ActiveVessel` (the pad vessel). A rocket ascending naturally exceeds 120km, but the camera is AT the ghost — the user can see it fine. The Beyond zone check (bug #54's grace-period approach) hid the ghost and exited watch mode after 2 seconds, which was wrong for any flight that climbs above 120km altitude.

**Root cause:** Bug #54 introduced a 2-second grace period for the watched ghost in Beyond zone. After the grace period, `ExitWatchMode()` was called and the ghost was hidden. This was designed for the case where a ghost drifts away from the player, but it also triggers during normal ascent when the camera is right at the ghost the entire time.

**Fix:** Replaced the grace-period logic with a full zone-hide exemption for the watched ghost. When `shouldHideMesh` is true and `isWatchedGhost` is true, `shouldHideMesh` is set to false and the ghost falls through to the visible-zone code path. The watched ghost is never hidden by zone distance. Also reset `watchStartTime = Time.time` in `TransferWatchToNextSegment` so logging timestamps are fresh after a chain transfer.

**Priority:** High — watch mode was unusable for any suborbital or orbital flight

**Status:** Fixed

## 120. Capsule doesn't spawn after revert — PID dedup finds pad vessel instead (tree recordings)

After revert, the quicksave restores the pre-launch vessel on the pad with the same PID as the recording's vessel. When the ghost reaches end-of-recording, `RealVesselExists(pid)` finds the pad vessel and skips spawning. The existing `ForceSpawnNewVessel` flag solves this for standalone recordings (set in `OnFlightReady`), but for TREE recordings the tree is still pending when `OnFlightReady` runs — it gets committed later via the merge dialog callback, so `ForceSpawnNewVessel` is never set.

**Fix:** Added `MergeDialog.MarkForceSpawnOnTreeRecordings(tree, activePid)` — iterates tree recordings, sets `ForceSpawnNewVessel = true` on recordings where `VesselPersistentId == activePid && !VesselSpawned`. Called from: (1) single-leaf "Merge to Timeline" callback, (2) multi-vessel "Commit All" callback, (3) `OnFlightReady` pending tree path (belt-and-suspenders).

**Priority:** High — prevents vessel spawn after revert in tree mode

**Status:** Fixed

## 121. Ghost SKIPPED per-frame spam during collision-blocked spawn

When a recording's ghost is past EndUT and spawn is collision-blocked, `UpdateTimelinePlayback` logs `Ghost SKIPPED (UT already past EndUT) — spawning vessel immediately` every physics frame until the collision block limit (150 frames) is reached. Session 4 showed 151 identical messages in 1.3 seconds for recording #5.

**Fix:** Rate-limit or deduplicate the "Ghost SKIPPED" message during collision-blocked state. Log once when first blocked, suppress until resolved.

**Priority:** Low — VERBOSE level only, does not affect gameplay

**Status:** Open

## 122. Dead→Dead crew status identity transitions logged as events

When a spawned vessel is immediately destroyed (bug #110b), KSP fires `CrewStatusChanged` for crew already marked Dead. `GameStateRecorder` records these `Dead → Dead` no-op transitions as real events. Session 4 showed 10 such events across 3 spawn-death cycles.

**Fix:** Filter identity transitions (`oldStatus == newStatus`) in `GameStateRecorder` before recording.

**Priority:** Low — minor log noise, bounded by #110b's 3-cycle limit

**Status:** Open

## 123. `#autoLOC` localization keys in internal log messages

Bug #103 fixed user-facing group headers, but some internal code paths (`TimeJumpManager`, `SpawnCollisionDetector`, `FlightRecorder`) still pass raw `Vessel.vesselName` (which may be an `#autoLOC_XXXXX` key) to log messages. Session 4 showed `vessel '#autoLOC_501224'` in TimeJump WARN messages and spawn collision logs.

**Fix:** Apply `ResolveVesselName` more broadly to internal log call sites, or accept as cosmetic log-only issue.

**Priority:** Low — log readability only, no gameplay impact

**Status:** Open

## 124. Watch mode exit key conflicts with KSP Abort action group

Backspace was used to exit watch mode and return to the active vessel. But Backspace is also KSP's default Abort action group key — pressing it during watch mode triggers abort on the active vessel (deploying parachutes, firing escape tower, etc.).

**Fix:** Changed exit-watch keybinding from Backspace to `[` or `]` (either bracket key). Updated the on-screen overlay hint from `[Backspace] Return to vessel` to `[ ] Return to vessel`.

**Priority:** Medium — caused unintended abort actions during normal watch mode use

**Status:** Fixed

## 125. Engine plate covers / fairings not visible on ghost

Engine plates (`EnginePlate1` etc.) have protective covers (interstage fairings) that are built by `ModuleProceduralFairing` at runtime — similar to stock procedural fairings but integrated into the engine plate part. These covers are not visible on ghost vessels during playback. The ghost shows the engine plate base and attached engines but the fairing shell is missing.

Likely same root cause as the original procedural fairing work: the fairing mesh is generated procedurally from XSECTION data at runtime by `ModuleProceduralFairing`, not stored as a static mesh on the prefab. The ghost builder's `GenerateFairingConeMesh` may not be triggered for engine plate fairings, or the engine plate's MODULE config may differ from standalone fairings.

**Priority:** Medium — visually noticeable on any vessel using engine plates

**Status:** Open

## 126. Rewind vessel strip fails due to localization key mismatch

`PreProcessRewindSave` searches for `name = Aeris 4A` in the quicksave .sfs, but the vessel is stored as `name = #autoLOC_501176`. The string comparison fails — zero vessels stripped. The original vessel survives rewind and sits on the runway. `CleanupOrphanedSpawnedVessels` also fails for the same reason: it compares `vessel.vesselName` (returns `#autoLOC_501176`) against recording names ("Aeris 4A").

**Fix:** Resolve localization keys before comparing via `Recording.ResolveLocalizedName()` at all 4 comparison sites (`PreProcessRewindSave` x2, `CleanupOrphanedSpawnedVessels`, `StripOrphanedSpawnedVessels`) and at all recording-creation sites that store `vessel.vesselName` without resolving.

**Priority:** High — causes spawn-on-top-of-existing-vessel explosions after rewind

**Status:** Fixed

## 127. SpawnCollisionDetector checks wrong position — trajectory endpoint vs snapshot

`SpawnOrRecoverIfTooClose` computes `spawnPos` from the recording's last trajectory point (the landing/crash site), but `RespawnVessel` spawns at the vessel snapshot's stored position (the runway). The collision check passes because nothing is at the trajectory endpoint, but the vessel materializes at the snapshot position where another vessel already exists.

**Fix:** Read `lat`/`lon`/`alt` from `rec.VesselSnapshot` for the collision check in both `SpawnOrRecoverIfTooClose` and `SpawnAtChainTip`, falling back to trajectory endpoint if snapshot lacks position data.

**Priority:** High — direct cause of spawn-into-existing-vessel explosions

**Status:** Fixed

## 128. Crew replacement not found in roster on second rewind

On the second rewind in session 7, `UnreserveCrewIn` logs `Replacement 'Hadfry Kerman' not found in roster (already removed?)`. Hadfry was hired as a replacement for Jebediah at the start of the second rewind cycle, but by the time unreservation runs after load, Hadfry is gone from the roster. The first rewind cycle (Cerlan Kerman) works fine. The code handles it gracefully (no crash), but the missing replacement may leave stale crew reservation state that accumulates across rewinds.

**Fix:** Investigate ordering between rewind save-load and crew roster cleanup. The replacement may be getting removed by KSP's own roster management before Parsek's unreservation runs.

**Priority:** Low — no crash, handled gracefully, but may cause crew roster drift over many rewinds

**Status:** Open

## 129. Pad vessel from future persists as real after rewind

When a vessel is sitting on the launch pad and the player rewinds to an earlier point, the pad vessel still appears as a real (non-ghost) vessel in the rewound save. It should be stripped from the flight state during rewind since it belongs to the future timeline — the player hasn't launched it yet at the rewound UT.

**Root cause:** `StripOrphanedSpawnedVessels` filters by name first. Unrecorded PRELAUNCH vessels fail the name check and survive because they were placed by KSP's launch system, not spawned by Parsek.

**Fix:** Added `StripFuturePrelaunchVessels` in `ParsekScenario` — a second-pass strip that runs after the name-based strip in `HandleRewindOnLoad`. `PreProcessRewindSave` now captures PIDs of all surviving vessels in the quicksave into `RecordingStore.RewindQuicksaveVesselPids`. The second pass strips PRELAUNCH vessels whose PID is not in this whitelist — they must be from a future launch. Whitelisted PRELAUNCH vessels (the player's pad vessel at rewind time) are preserved.

**Status:** Fixed

## ~~130. GhostDestroyed event has empty vessel name for loop-restarted ghosts~~

When a looping ghost cycle restarts, the engine fires `OnGhostDestroyed` for the old cycle. The event's `Trajectory` field is null/empty because `DestroyGhost` is called after the ghost state is already being torn down. The log shows `GhostDestroyed index=8 vessel=` (empty name).

Root cause: `DestroyGhost` fires the event but the trajectory reference passed by the caller (loop playback) may be null when the ghost is destroyed during cycle rebuild. The trajectory reference should be captured before destruction.

**Fix:** Added `vesselName` field to `GhostPlaybackState`, set at `SpawnGhost` time from `traj.VesselName`. `DestroyGhost` and `HandleGhostDestroyed` now use `state.vesselName` as primary name source with fallback chain: `state?.vesselName ?? traj?.VesselName ?? "Unknown"`.

**Status:** Fixed

## 131. Explosion GO count can reach ~90 for overlapping reentry loops

With 3+ overlapping negative-interval loop ghosts that have Destroyed terminal state (e.g., R2 reentry test), each produces continuous explosions. The `activeExplosions` list grows to ~90 concurrent explosion GameObjects before natural particle lifetime decay brings it back to ~10-12 steady state.

Not a crash or leak (explosions decay naturally), but 90 concurrent particle-emitting GOs could cause frame drops on lower-end hardware.

Possible fix: cap `activeExplosions.Count` and skip new explosions when at cap, or increase explosion pruning frequency.

**Priority:** Low — only occurs with many overlapping destroyed-terminal loops

**Status:** Open

## 132. Policy RunSpawnDeathChecks and FlushDeferredSpawns are TODO stubs

`ParsekPlaybackPolicy.RunSpawnDeathChecks()` and `FlushDeferredSpawns()` are placeholder stubs. The old path equivalents in `ParsekFlight.UpdateTimelinePlaybackViaEngine()` still call the original `FlushDeferredSpawns(committed)` directly, so deferred spawns work. But spawn-death detection (vessel dies immediately after spawning → re-spawn with death count tracking) is not wired through the policy yet.

The pure predicates (`ShouldAbandonSpawnDeathLoop`, `ShouldFlushDeferredSpawns`, `ShouldSkipDeferredSpawn`) are tested; only the integration through the policy event handlers is missing.

**Priority:** Medium — edge case (rapid spawn-death cycles) not handled by new path

**Status:** Open

## 133. Forwarding properties in ParsekFlight add ~500 lines of indirection

After T25 extraction, ParsekFlight still has forwarding properties (`ghostStates => engine.ghostStates`, `overlapGhosts => engine.overlapGhosts`, etc.) and bridge methods (`DestroyTimelineGhost`, `DestroyAllOverlapGhosts`, `UpdateReentryFx`, etc.) that external callers (scene change, camera follow, delete, preview) use. These add ~500 lines that could be eliminated by updating callers to use the engine query API directly.

**Priority:** Low — tech debt, no functional impact

**Status:** Open

## 134. CleanupOrphanedSpawnedVessels destroys freshly-spawned past vessel on first flight after rewind

After rewind to UT ~99.8, entering flight at UT ~111. The Aeris 4A recording (UT 53–78, **before** the rewind point) correctly spawns a LANDED vessel on frame 1 (`VesselSpawned=true`). Half a second later, `CleanupOrphanedSpawnedVessels` runs during `OnFlightReady` and **destroys** the just-spawned vessel because "Aeris 4A" matches the cleanup names list. The vessel-gone check resets `VesselSpawned=false` (spawnDeathCount 1/3), the re-spawn attempt is collision-blocked by Crater Crawler at 14m for 150 frames, and eventually abandoned.

**Root cause:** The rewind path populates `PendingCleanupNames` with ALL recording vessel names (11 names) via `CollectAllRecordingVesselNames()`. This makes sense for stripping future vessels from the save, but `CleanupOrphanedSpawnedVessels` at `OnFlightReady` also uses these same names. By then, past-recording vessels have already been correctly spawned by `UpdateTimelinePlayback`, and the cleanup destroys them.

Different from #109 (missing cleanup on second rewind) — here cleanup runs but is over-aggressive on the first flight. Different from #112 (self-overlap) — here the vessel is destroyed by cleanup, not blocked by its own copy.

**Fix:** Clear `PendingCleanupPids` and `PendingCleanupNames` immediately after `StripOrphanedSpawnedVessels` completes in `HandleRewindOnLoad`. The strip already handled protoVessel cleanup in the flightState; leaving the overbroad names set in PendingCleanupNames caused `CleanupOrphanedSpawnedVessels` in `OnFlightReady` to destroy freshly-spawned past vessels. With the clear, OnFlightReady sees null and skips cleanup. The revert path's `alreadyHasCleanupData` guard sees null and collects fresh data from `CollectSpawnedVesselInfo()` if needed.

**Priority:** High — destroys correctly-spawned vessels after rewind

**Status:** Fixed

## 135. ShouldSpawnAtRecordingEnd VERBOSE log spam — 377K lines/session

`GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` logs a VERBOSE line for every suppressed recording on every call. Called from `UpdateTimelinePlayback()` → `Update()` per-recording per-frame. With 37 recordings and 60fps, this produces ~2,200 lines/second. In the analyzed session: 377,350 lines — 73% of all Parsek log output and the single largest spam source.

Different from #117 (CanRewind/CanFastForward UI spam, now fixed) and #121 (Ghost SKIPPED during collision-blocked spawn).

**Fix:** Remove all VERBOSE logs from `ShouldSpawnAtRecordingEnd`. Like CanRewind/CanFastForward (#117), this is a per-frame read-only check where the `reason` out-parameter already conveys the suppression reason to the caller.

**Priority:** High — blocks effective log analysis

**Status:** Open

## 136. ParsePartPositions: 0/N parts parsed from vessel snapshot

`SpawnCollisionDetector.ParsePartPositions` parsed 0 of 40 parts from the Aeris 4A vessel snapshot, falling back to a 2m bounds estimate. This makes the collision check unreliable — the actual vessel footprint is much larger than 2m but the detector can't compute it.

Different from #127 (wrong position source for collision check, now fixed). This is about the part position parser itself failing to extract coordinates from snapshot PART nodes.

**Fix:** Investigate why `ParsePartPositions` fails on the Aeris 4A snapshot. Likely the PART nodes use a different position format or key name than expected. Add a VERBOSE diagnostic log showing what keys/values the parser found vs expected.

**Priority:** Low — fallback bounds work but are inaccurate

**Status:** Open

## 137. Crew status corruption: reserved kerbals become Missing after post-rewind EVA vessel removal

After rewind, `OnFlightReady` removes reserved EVA vessels (Bob Kerman pid=2373555091, Halemy Kerman pid=2924298993). KSP's removal sets their status from Assigned to Missing. The crew rescue logic (`Rescued Missing crew → Available`) runs earlier during OnLoad but not after OnFlightReady, so these kerbals stay Missing for the rest of the session until the next save/load cycle.

Log evidence: `CrewStatusChanged 'Bob Kerman' Assigned → Missing` at UT 115.6, and `CrewStatusChanged 'Halemy Kerman' Assigned → Missing` at UT 115.6. Similar to #116 (Valentina lost to Missing) but triggered by EVA vessel removal rather than vessel strip.

**Root cause:** `RemoveReservedEvaVessels` calls `vessel.Unload()` which orphans crew — KSP sets their status to Missing. No rescue runs after.

**Fix:** Added `RescueReservedCrewAfterEvaRemoval` in `CrewReservationManager` — called at the end of `RemoveReservedEvaVessels` when `evaRemoved > 0`. Scans the roster for crew matching `ShouldRescueFromMissing` (Missing status AND in the replacements dict) and sets them to `Assigned` (not Available — they're still reserved for spawn). Wrapped in `GameStateRecorder.SuppressCrewEvents` to prevent the status change from being recorded as a game state event. Pure decision method `ShouldRescueFromMissing` extracted for testability.

**Status:** Fixed

# In-Game Tests

- [ ] Vessels propagate naturally along orbits after FF (no position freezing)
- [ ] Resource converters don't burst after FF jump
