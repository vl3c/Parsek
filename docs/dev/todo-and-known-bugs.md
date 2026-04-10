# TODO & Known Bugs

Previous entries (225 bugs, 51 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v1.md`.

---

## ~~295. Pending standalone recording blocks all rewind buttons after tree merge~~

Surfaced by the 2026-04-10 KerbalX playtest (save s35). After a tree merge, a standalone EVA recording (Jebediah Kerman) remained in the pending slot. `RecordingStore.CanRewind()` unconditionally blocks when `HasPending` is true, disabling all R buttons with "Merge or discard pending recording first". The auto-commit safety net in `ParsekScenario.SafetyNetAutoCommitPending()` only ran at OnSave time (50s later, on game pause to exit).

**Fix:** In `MergeDialog.cs` tree merge button handler, auto-commit any pending standalone recording immediately after `CommitPendingTree()`, following the `AutoCommitGhostOnly` + `CommitPending` + `LedgerOrchestrator.OnRecordingCommitted` pattern. Made `AutoCommitGhostOnly` `internal static` in ParsekScenario.

## ~~296. EVA kerbal crew end states not inferred — permanent reservation~~

EVA kerbal recordings have no extractable crew in their ConfigNode snapshots (`ExtractCrewFromSnapshot` reads PART/crew values, which EVA snapshots don't have). `PopulateCrewEndStates` skipped these recordings entirely, leaving `CrewEndStates = null`. Consequences: crew reservation set to endUT=Infinity (Unknown), kerbal permanently reserved.

Additionally, `RecordingOptimizer.SplitAtSection` did not propagate `EvaCrewName` or `ParentRecordingId` to the second half, so after an optimizer split the tip recording lost its EVA identity.

**Fix:** (1) Propagate `EvaCrewName` and `ParentRecordingId` in `SplitAtSection`. (2) Add EVA fallback in `KerbalsModule.PopulateCrewEndStates`: if snapshot crew is empty and `rec.EvaCrewName` is set, use it as the crew member. (3) Relax `VesselSnapshot != null` guard in `LedgerOrchestrator.OnRecordingCommitted` to also allow EVA recordings. (4) Add same EVA fallback in `LedgerOrchestrator.ExtractCrewFromRecording`.

## ~~297. Phantom terrain crash kills EVA kerbal on vessel switch~~

KSP's terrain collision detection sometimes falsely destroys EVA vessels during pack/unload. When the user switched vessel focus away from Bill Kerman, KSP packed the EVA vessel and within 0.6s reported "crashed through terrain". Parsek correctly processed KSP's `onVesselWillDestroy` and set `terminal=Destroyed`, but the crash was spurious.

**Fix:** Track pre-pack vessel situation in `ParsekFlight.packStates` dictionary (populated at `OnVesselGoOnRails`, cleaned at `OnVesselGoOffRails`). In `DeferredDestructionCheck`, before applying terminal destruction, check if the vessel is an EVA kerbal that was in a safe situation (LANDED/SPLASHED) when packed and was destroyed within 5s. If so, override terminal state to Landed/Splashed instead of Destroyed. Pure static `IsPhantomTerrainCrash` method extracted for testability.

## ~~294. F5/F9 during standalone recording loses all in-progress data~~

Surfaced by the 2026-04-10 engine-plume-bug playtest (`logs/2026-04-10_engine-plume-bug/KSP.log`). After F9 quickload during the Halger Kerman EVA standalone recording, the pending recording was discarded by `DiscardStashedOnQuickload` and no new recording started. 28 seconds of EVA walk lost.

**Root cause**: Tree mode has a full quickload-resume pipeline (`SaveActiveTreeIfAny` → `PARSEK_ACTIVE_TREE` node → `TryRestoreActiveTreeNode` → `RestoreActiveTreeFromPending` coroutine). Standalone mode had nothing equivalent. The in-progress recorder buffer was never serialized to `.sfs` during F5, so F9 had nothing to restore from. `DiscardStashedOnQuickload` correctly discarded the stashed post-F5 data (from `StashPendingOnSceneChange`), but there was no mechanism to restore the F5-point data.

**Fix**: Three-part fix mirroring the tree-mode pattern:
1. `SaveActiveStandaloneIfAny` in `ParsekScenario.OnSave` deep-copies the recorder's buffers into a `PARSEK_ACTIVE_STANDALONE` ConfigNode (points, events, orbit segments, track sections, flag events, start location metadata, rewind save filename). Unlike tree-mode flush, the recorder's buffers are NOT cleared — the recorder keeps running after F5.
2. `TryRestoreActiveStandaloneNode` in `ParsekScenario.OnLoad` deserializes the node into a temporary Recording and sets `ScheduleActiveStandaloneRestoreOnFlightReady`.
3. `RestoreActiveStandaloneFromPending` coroutine in `ParsekFlight.OnFlightReady` waits for the vessel to load (3s timeout), creates a new recorder via `StartRecording(isPromotion: true)`, prepends the saved trajectory data into the recorder's buffers, and restores the original start location. Reuses `restoringActiveTree` guard for reentrancy protection.

Mutually exclusive with tree restore — `TryRestoreActiveStandaloneNode` skips if `ScheduleActiveTreeRestoreOnFlightReady` is already set.

**Status**: Fixed.

---

## ~~293. OnFlightReady fallback merge dialog races with restore coroutine after F9~~

Surfaced by the 2026-04-10 engine-plume-bug playtest (`logs/2026-04-10_engine-plume-bug/KSP.log` line 10604). After F9 quickload with an active tree, the restore coroutine (`RestoreActiveTreeFromPending`) started and yielded waiting for the vessel. But the fallback pending tree check at `ParsekFlight.cs:4211` ran synchronously in the same frame, saw `HasPendingTree` still true (the coroutine hadn't popped it yet), and fired `MergeDialog.ShowTreeDialog`. With autoMerge ON, the tree was committed immediately. The coroutine eventually resumed but the tree was gone — no recording restarted. 28 minutes of orbital flight (UT 695→2376) not recorded.

**Root cause**: PR #184 added the `restoringActiveTree` reentrancy guard and checked it at `OnFlightReady` entry, `FinalizeTreeOnSceneChange`, `OnVesselWillDestroy`, and `OnVesselSwitchComplete`. But it missed the intra-method fallback check at line 4211 (`if (RecordingStore.HasPendingTree)`), which runs AFTER `StartCoroutine` returns (after the coroutine's first yield). The coroutine sets `restoringActiveTree = true` before its first yield, so the guard was already set — it just wasn't being checked.

**Fix**: Added `&& !restoringActiveTree` to both fallback checks (pending tree at line 4211, pending standalone at line 4221). Also added logging for the skip case.

**Status**: Fixed.

---

## ~~292. Recording optimizer "deletes" outer-space recordings after F9 quickload (game-breaking)~~

Surfaced by the 2026-04-10 Kerbal X Mun-flyby playtest (worktree `Parsek-investigate-280-285`, branch `investigate-280-285`). The user reported: *"the rec optimizer (maybe because of f5 f9 interaction?) deleted all the recordings of the trip in outer space (they were ok initially). At the end after a rewind I could only see the launch and 2 very small recordings of the end of the mission. This is game breaking."*

**Investigation findings** (`KSP.log` 00:53–01:06):

1. **00:53:23** — pre-rewind finalize: tree `Kerbal X` (id `b731eff7…`) had 12 recordings. The active vessel recording `885c858bf3e24de2a5e2b801b46cbf3c` had **points=276 orbitSegs=9** — that's the FULL outer-space mission (launch → Mun flyby → Kerbin escape → solar orbit → splashdown).
2. **00:53:30** — user rewound from UT 49558 → UT 11.58 (start of mission). Merge dialog opened with the 12 pre-rewind recordings + 5 from the merge run = **17 recordings** post-merge.
3. **00:53:33** — `RecordingOptimizer.SplitAtSection` ran 5× during the merge, splitting:
   - `885c858…` at UT=146.7 (first half 106 pts/2 sections = launch only; second half 170 pts/23 sections = outer space, kept under a NEW guid)
   - `e203b6e1…` at UT=49246.2 (51+119 pts) — also a post-merge new recording
   - `9da64a57…`, `a8782158…`, `831cd6e6…` — three more splits
   - All splits forwarded permanent state events as seeds (16, 19, 1, 1, 1 events)
4. **00:53:46 → 00:59:02** — `persistent.sfs` saved 6 times, all with `recordings=17`. **The 5 outer-space recordings (`e203b6e1`, `66b0dbad`, `bf3a6b02`, `4649d230`, `89745643`) were correctly persisted at 00:58:50** (verified by reading `Backup/persistent (2026_04_10_00_58_50).sfs` — all 17 ids present). Their `.prec` sidecar files exist on disk too (`saves/s34/Parsek/Recordings/`).
5. **01:03:11** — user opened the menu, pressed F9 (or "Load Game" → quicksave). KSP loaded `quicksave.sfs`, which **had only 12 recordings** (verified by reading `Backup/quicksave (2026_04_10_01_03_41).sfs` — same 12 ids).
6. **01:03:12** — load handler logged `RemoveCommittedTreeById: removed stale committed copy of tree 'Kerbal X' (…, 17 recording(s)) — active-tree restore takes precedence`. The in-memory committed tree (with all 17) was DISCARDED in favor of the loaded tree (12). The "active-tree restore" path retained the 12-recording active state from the loaded `.sfs`, not the 17-recording committed state.
7. **01:03:12** — `FinalizeTreeRecordings` for the post-load tree showed `885c858…` at **points=106 orbitSegs=0** (the split first-half — launch only, no orbit data). The user's rocket recording is now reduced to launch only.
8. **01:03:31** — next save wrote `recordings=11` (after `PruneZeroPointLeaves` removed the empty `3cbb6f0c…` placeholder).

**Root cause**: the **quicksave.sfs is not updated when an active-tree merge or optimizer split happens**. The quicksave at 00:53:16 captured 12 records (pre-merge state). The merge at 00:53:33 created 5 new recording IDs in memory and in `persistent.sfs`, but `quicksave.sfs` was never refreshed. F9 then loaded the stale quicksave, the load handler chose the loaded 12-rec tree over the in-memory 17-rec committed tree, and the 5 outer-space records were silently dropped from the active state. The `.prec` sidecar files survived on disk as orphans.

**The optimizer did not delete anything** — every split kept the source recording's data, just under two IDs. The data was lost because the F9-loaded `.sfs` didn't reference the new IDs that the optimizer/merge had created since the quicksave was made.

**Fix candidates**:
1. **Refresh the quicksave whenever a merge or optimizer split happens** (cheapest correct fix). On `Merger.MergeTree` completion or `RecordingOptimizer.SplitAtSection`, call KSP's `GamePersistence.SaveGame("quicksave", …)` so subsequent F9 loads include the new IDs.
2. **Detect orphaned `.prec` files on load and offer recovery.** When `LoadAllRecordings` scans `saves/<save>/Parsek/Recordings/`, any `.prec` whose recording id is not referenced by any tree node should surface a UI prompt: "5 unreferenced recording files found. Recover?".
3. **Refuse F9 if the in-memory tree has uncommitted merges that the quicksave doesn't include.** Show a warning dialog: "Quickloading will lose 5 recordings created since the last quicksave. Continue?".
4. **Make the load handler prefer the in-memory committed tree over the loaded tree if their IDs match but the committed has more recordings.** Risky — could mask other bugs.

Recommended: combine (1) for prevention + (2) for recovery of saves already affected. Player on the affected save can salvage the 5 outer-space recordings — the `.prec` files at `saves/s34/Parsek/Recordings/{e203b6e1,66b0dbad,bf3a6b02,4649d230,89745643}*.prec` are intact.

**Fix**: New `RecordingStore.RefreshQuicksaveAfterMerge(reason, recordingCount)` helper that calls `GamePersistence.SaveGame("quicksave", HighLogic.SaveFolder, SaveMode.OVERWRITE)` once after a user-initiated merge. Wired only into `MergeDialog.cs:359` (the "Merge to Timeline" button handler), NOT into `RecordingStore.RunOptimizationPass` itself — `RunOptimizationPass` is also called from `ParsekScenario.OnLoad`, and a save-while-loading would re-enter `OnSave` on every `ScenarioModule`. Helper has a `LoadedScene == LOADING` guard, a `CurrentGame == null` guard, a `try/catch` that logs warns on failure (non-fatal: the merge still completes), and an `internal static SaveGameForTesting` seam so `QuicksaveRefreshTests.cs` can intercept the call without invoking the real KSP API. Trade-off documented in CHANGELOG: this advances the player's F5 checkpoint UT to the post-merge state — players who want a pre-merge checkpoint should F5 manually before clicking Merge.

**Status**: Fixed.

---

## ~~290. Multiple playtest bugs: ghost flicker, lost tree on revert, engine skirt, log spam~~

Surfaced by the 2026-04-10 GDLV3 rocket launch playtest. Four related issues:

**Bug A — Ghost icon flicker during time warp.** The zone system's warp exemption (`ShouldExemptFromZoneHide`) had a threshold of `> 4f`. KSP ramps through intermediate warp rates during transitions between warp levels (e.g., 100x → 50x → 10x → 4x → 1x over several frames). When the rate briefly dipped below 4x, the ghost was hidden (zone Beyond = `SetActive(false)`), then re-shown on the next frame when the rate went back above 4x. Result: per-frame flicker of the ghost mesh in map view.

**Fix**: Two changes. (1) Lowered threshold from `> 4f` to `> 1f` in `GhostPlaybackLogic.ShouldExemptFromZoneHide`. Any warp above normal speed now exempts orbital ghosts from zone hiding. At 1x (normal), ghosts are still hidden at Beyond range (>120km) as intended. (2) The >50x ghost warp suppression (`ShouldSuppressGhosts`) is now skipped when `mapViewEnabled` is true. `DrawMapMarkers` skips inactive ghosts (stale position after FloatingOrigin shifts), so suppressing the mesh also killed the icon+text. In map view the mesh is invisible at orbital distances anyway — only the icon matters. New `FrameContext.mapViewEnabled` field populated from `MapView.MapIsEnabled` by the host.

**Bug B — Pending Limbo tree discarded on revert (lost 4 debris recordings).** Flow: user merged GDLV3 tree → `StashActiveTreeAsPendingLimbo` set `PendingStashedThisTransition = true` → quickload discard path preserved the Limbo tree but reset the flag to `false` (line 197 of `ParsekScenario.cs`) → revert discard path (line 808) checked the flag, saw `false`, treated the Limbo tree as "orphaned from a previous flight" and discarded it. The tree contained 5 recordings (root + 4 debris).

**Fix**: The revert discard path now checks the tree state (`PendingTreeState.Limbo` / `LimboVesselSwitch`) instead of relying solely on the `PendingStashedThisTransition` flag. Limbo trees are by definition freshly stashed for OnLoad dispatch and are never treated as orphaned.

**Bug C — Engine skirt visible on ghost (regression).** Continuation recordings created during tree promotion skip seed events (to avoid poisoning `FindLastInterestingUT`). The ghost builder forces jettison transforms active (prefab default), expecting playback `ShroudJettisoned` events to hide them. But continuation recordings have no such events — the seed events are on the root recording, not the continuation.

**Fix**: `GhostVisualBuilder.AddPartVisuals` now checks the snapshot's MODULE data for `isJettisoned = True` at ghost build time. If the shroud was already jettisoned when the snapshot was captured, the jettison transforms are hidden immediately instead of waiting for a playback event. New helper: `GhostVisualBuilder.IsJettisonedInSnapshot(ConfigNode partNode)`.

**Bug D — IsNonLeafInCommittedTree log spam.** The safety-net method `IsNonLeafInCommittedTree` logged at `ParsekLog.Info` level every time it triggered (called per-recording per spawn check, hundreds of times per session). Downgraded to `VerboseRateLimited` with a 30-second rate limit per recording ID.

**Status**: Fixed.

---

## ~~291. In-game test failure: `FlightIntegrationTests.EvaSpawnWalkbackOnOverlap`~~

`parsek-test-results.txt` (run 2026-04-10 01:05:19, scene FLIGHT) reports:

```
FAIL  FlightIntegrationTests.EvaSpawnWalkbackOnOverlap (48.5ms)
      Expected 'WalkbackSubdivided: cleared' log line during spawn
```

The test places the EVA trajectory endpoint on top of the active vessel, expecting the walkback to detect the overlap and walk backward. But `CheckOverlapAgainstLoadedVessels` unconditionally skipped `FlightGlobals.ActiveVessel` (line 539), so no overlap was detected, walkback never triggered, and the log assertion failed. The kerbal spawned directly on the parent vessel and KSP physics pushed them apart, which is why the distance assertions still passed.

**Root cause:** `CheckOverlapAgainstLoadedVessels` had an unconditional `if (other == FlightGlobals.ActiveVessel) continue;` skip. For non-EVA spawns this is correct (the player's vessel shouldn't block its own recording's spawn). For EVA spawns, the active vessel IS the parent rocket — the most common overlap target.

**Fix:** Added `bool skipActiveVessel = true` parameter to `CheckOverlapAgainstLoadedVessels`. `VesselSpawner.CheckSpawnCollisions` passes `skipActiveVessel: false` when `isEva` is true (initial check + post-recovery re-check). `TryWalkbackForEndOfRecordingSpawn` also passes `skipActiveVessel: false` for EVA recordings in the per-sub-step overlap lambda. All other callers (VesselGhoster, in-game tests) use the default `true` — no behavior change.

**Status**: Fixed.

---

## 290. F5/F9 quicksave/quickload interaction with recordings — verification needed

User asked: *"check if the quicksave/quickload and recordings systems work well together now (we fixed some bugs) — investigate deep in the logs"*. Bug #292 is the smoking gun — F9 reloads can silently drop recordings created since the last quicksave was made.

**Open questions**:
1. Are there OTHER scenarios beyond #292 where F5/F9 + recordings interact badly? (e.g., F9 during an active recording, F5 during a merge dialog)
2. Should the quicksave auto-refresh on every committed recording change, not just merges? Trade-off: more I/O vs. data safety.
3. Does the rewind save (`parsek_rw_*.sfs`) have the same staleness problem as the quicksave? The rewind save IS Parsek-managed, so we control when it's written.

**Status**: Open — partially investigated by #292. Needs broader scenarios coverage.

---

## ~~289. End-of-mission spawn-at-end never fires — vessel snapshots are FLYING/SUB_ORBITAL not LANDED/SPLASHED~~

Surfaced by same playtest. The user reported: *"at the end of the mission the kerbals and the capsule were not spawned (no real vessels spawned)"*.

**Investigation findings** (`KSP.log` 00:53:33 — first KSC spawn evaluation post-merge):

```
[KSCSpawn] Spawn not needed for #14 "Kerbal X": snapshot situation unsafe (FLYING/SUB_ORBITAL)
[KSCSpawn] Spawn not needed for #16 "Bob Kerman": snapshot situation unsafe (FLYING/SUB_ORBITAL)
```

The end-of-mission Kerbal X recording (`a8782158805f4cfdb9f69f2ed7f9d025`) and Bob Kerman EVA recording (`831cd6e6b7cc4ea897c647cd691d6879`) both had `terminal=Splashed` (correct — both vessels did splash down at UT ~49533) BUT the `VesselSnapshot.situation` field was FLYING or SUB_ORBITAL. The spawn-at-end policy correctly refuses to materialize vessels at unsafe snapshot situations.

**Why the snapshot is stale**: a vessel snapshot is captured ONCE per recording, at recording start (or at the first transition to background). The end-of-mission recording for Kerbal X started at UT=49537 when the player switched from Bob Kerman back to Kerbal X — line `OnVesselSwitchComplete: seeded lastLandedUT=49537.0 (vessel '#autoLOC_501232' already SPLASHED)` confirms the vessel WAS splashed at switch time. But the snapshot inside `a8782158…` was captured EARLIER (when it was still in background recording during the descent), and is never updated. By the time spawn-at-end evaluates the recording, the snapshot reflects a stale Flying/SubOrbital situation.

**Root cause**: `VesselSnapshot.situation` is captured once and never refreshed when the vessel transitions to a stable terminal state (Landed/Splashed). The recording's `TerminalState` is updated, but the embedded `VesselSnapshot` is not.

**Fix candidates**:
1. **Re-snapshot on terminal transition.** When `Recorder.CheckForTerminalTransition` decides a vessel is now Landed/Splashed/etc, re-take the snapshot from the current vessel state. Cheapest correct fix.
2. **Make spawn-at-end consult `Recording.TerminalState` and the LAST trajectory point's lat/lon/alt** instead of the snapshot situation. Spawn the snapshot at the trajectory's terminal coordinates with situation overridden to `Landed`/`Splashed`. More work but covers cases where re-snapshot fails.
3. **Have spawn-at-end accept FLYING/SUB_ORBITAL snapshots when `terminal=Landed/Splashed`** — trust the terminal state, override the situation field to `Landed` and place at the trajectory's last `landed{Lat,Lon,Alt}` if available. Risky — could spawn vessels at wrong height.

**Fix** (three-part):

1. **Re-snapshot path in `ParsekFlight.FinalizeIndividualRecording` runs OUTSIDE the existing `!TerminalStateValue.HasValue` gate.** The original re-snapshot at lines 6121-6131 only fired when terminal state hadn't been set yet — but the user's case is precisely "terminal state was already set elsewhere (ChainSegmentManager during active recording) so the original block was skipped, but the snapshot is still stale". New block runs after the existing terminal-determination block: if the recording is a leaf with stable terminal (`Landed`/`Splashed`/`Orbiting`) AND `FlightRecorder.FindVesselByPid` returns a live vessel, take a fresh `VesselSpawner.TryBackupSnapshot`, replace `rec.VesselSnapshot`, optionally replace `GhostVisualSnapshot` if null, and **call `rec.MarkFilesDirty()`** so the next sidecar-write run picks up the change. Logs `[Flight] FinalizeIndividualRecording: re-snapshotted '{recId}' with stable terminal state {state} ... [#289]` at Info.

2. **Force-write dirty sidecars in `CommitTreeSceneExit` autoMerge OFF branch (`ParsekFlight.cs:961-970`).** KSP's auto-save fires BEFORE Parsek's scene-exit cleanup runs, so the normal `OnSave → FlushDirtyFiles` path never sees the post-finalize dirty flag. Without an explicit force-write, the next `OnLoad` triggers `TryRestoreActiveTreeNode` which hydrates the tree from stale sidecars, undoing the in-memory re-snapshot. After `FinalizeTreeRecordings`, iterate `activeTree.Recordings.Values` and call `RecordingStore.SaveRecordingFiles(rec)` for any recording with `FilesDirty == true`. Logs `[Flight] CommitTreeSceneExit (autoMerge off): force-wrote {N} dirty sidecar(s) ... [#289]` at Info.

3. **Verified**: `RecordingStore.LoadRecordingFiles` (`RecordingStore.cs:3173-3179`) unconditionally loads `_vessel.craft` regardless of terminal state — so the round-trip (re-snapshot → MarkFilesDirty → force-write → next OnLoad → TryRestoreActiveTreeNode → LoadRecordingFiles) correctly hydrates the fresh snapshot back into memory.

**Scope limitation**: Fix only applies to autoMerge OFF mode (the default). The autoMerge ON branch in `CommitTreeSceneExit` (function at `ParsekFlight.cs:5962-5983`) explicitly nulls `VesselSnapshot` after finalize at line 5975 — fixing this requires preserving snapshots on autoMerge ON, a larger design discussion deferred to a separate PR.

**Tests**: New in-game tests in `RuntimeTests.cs`:
- `FinalizeReSnapshot_StableTerminal_LiveVessel_UpdatesSnapshotAndMarksDirty` — pre-seeds a recording with `TerminalStateValue=Landed` and a stale `sit=FLYING` snapshot, calls `FinalizeIndividualRecording(rec, ut, isSceneExit:true)`, verifies the snapshot's sit field was refreshed AND `rec.FilesDirty == true` AND the `[#289]` log line fired.
- `FinalizeReSnapshot_NonStableTerminal_DoesNotReSnapshot` — same setup with `TerminalStateValue=Destroyed` (non-stable), verifies the stale snapshot is preserved unchanged.

xUnit tests for the Part B force-write iteration are not feasible without invasive Unity-mocking; coverage is via the in-game test plus manual playtest.

**Status**: Fixed.

---

## ~~288. Ghost map icon hidden after re-entry until W (Watch) is pressed — Recording.TerminalOrbit cache empty~~

> **Renumbered from #287** on 2026-04-10. Main shipped a different fix (PR #178, "Spurious terminal EngineShutdown events survive into committed recordings") under #287 while this branch was open. This bug is the second one to claim #287, so it's been moved to #288 to keep the bug-tracking namespace unambiguous. (The #288 slot was previously a stub for "Kerbal X engine flames disappear after staging" — that exact bug is what main fixed as #287, so the stub was a duplicate and has been removed.)

User report: *"on the atmo re-entry from kerbin orbit I had to push the W button for the ghost icon to be visible in map view (it should always be visible)"*.

**Investigation findings** (`KSP.log` 00:58:54 — TRACKSTATION→FLIGHT scene transition with 17 active recordings):

```
[GhostMap] HasOrbitData(Recording): rec=885c858bf3e24de2a5e2b801b46cbf3c body=(null) sma=0 result=False
[GhostMap] HasOrbitData(Recording): rec=e203b6e1eb8a40db8f5592a2b2f92082 body=(null) sma=0 result=False
[GhostMap] HasOrbitData(Recording): rec=66b0dbad7aa94db0a8f89a39d8ffeab7 body=(null) sma=0 result=False
[GhostMap] HasOrbitData(Recording): rec=bf3a6b0216c94e84b2e36ef04bcbc315 body=(null) sma=0 result=False
[GhostMap] HasOrbitData(Recording): rec=831cd6e6b7cc4ea897c647cd691d6879 body=(null) sma=0 result=False
[GhostMap] HasOrbitData(Recording): rec=4649d2306cfc406493b7f87db24195f5 body=(null) sma=0 result=False
[GhostMap] CreateGhostVesselsFromCommittedRecordings: created=0 from 17 recordings (skipped: debris=10 superseded=1 terminal=0 noOrbit=6)
```

All 17 recordings were filtered out: 10 debris (no map vessel by design), 1 superseded by an active vessel, 6 with `noOrbit`. The 6 with `noOrbit` are the meaningful ones — including `885c858…` (which had `orbitSegs=9` pre-rewind, spanning launch → Mun flyby → Kerbin escape → Sun/Orbit) and `e203b6e1…` (with milestones from UT 146 → 28946). They DO have orbit data — but the cached `Recording.TerminalOrbit.body` field was `null` and `sma=0`, so the check returned `result=False` and no map vessel was created.

**Root cause**: `Recording.TerminalOrbit` is only populated by `Flight.FinalizeTreeRecordings.PopulateTerminalOrbitFromLastSegment`, and only on the "vessel pid not found → marking Destroyed" code path during finalize (we see it fire for `a8782158…` post-F9 reload: `PopulateTerminalOrbitFromLastSegment: recovered orbit for 'a8782158805f4cfdb9f69f2ed7f9d025' from segment body=Kerbin sma=300953.7`). For non-terminal in-progress recordings the `TerminalOrbit` cache is left empty, even though `OrbitSegments` contains valid data.

**Why pressing W "fixes" it**: `WatchModeController` reads `OrbitSegments` directly when entering Watch mode, bypassing the broken `TerminalOrbit` cache. So pressing W incidentally builds the icon by going through a different code path. This is the user's accidental workaround.

**Fix**: Eager-populate the `TerminalOrbit*` cache from the last `OrbitSegment` at sidecar-load time. Added a guarded call to `ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec)` in `RecordingStore.LoadRecordingFiles` immediately after `DeserializeTrajectoryFrom` returns (which is where `OrbitSegments` gets populated). The call is only made when `TerminalOrbitBody` is empty AND `OrbitSegments` has data — the helper itself has a "do not overwrite" guard, so it's safe. Logs `Eager-populated TerminalOrbit for {recId} from last orbit segment (body={body}, sma={sma})` so playtest logs can confirm the fix is active.

**Why this is the right scope**: an earlier draft also added a read-side fallback in `GhostMapPresence.HasOrbitData(Recording)` to check `OrbitSegments[Count-1]` when the cache is empty. That broke the existing `ShouldCreate_NullTerminal_WithSegment_UTPastRange_Skipped` test, which encoded a different policy: "an in-progress recording with currentUT past all known segments should NOT be displayed as a stable orbit". The eager-populate-only approach handles the user's case (recordings loaded from disk after a scene transition) without changing the read-side semantics for in-memory recordings or in-progress test scenarios. Active recordings that grow segments mid-flight don't need the cache because the active vessel's map icon is handled by KSP, not Parsek's ghost system.

**Tests**: Existing `WithOrbitSegments_PopulatesTerminalOrbit` and `UsesLastSegment_NotFirst` tests in `BugFixTests.cs` already cover the helper. The new conditional call site in `LoadRecordingFiles` is a simple guard + delegate.

**Status**: Fixed.

---

## ~~287. Spurious terminal EngineShutdown events survive into committed recordings — ghost engine flames go off permanently after booster staging~~

Surfaced by the 2026-04-09 Kerbal X playtest (`logs/2026-04-09_1117_kerbalx-rover`). The user reported that the ghost's KerbalX engine flames (Mainsail + booster liquidEngine2) turned off after staging the boosters and never came back, even though in reality the Mainsail kept burning through orbit insertion.

**Evidence from committed recording `db2e7ab62c6847e0b439237a41172602.prec`:**
- UT 72.40: `EngineShutdown pid=1782153286 part=unknown` (a liquidEngine2 booster that shouldn't be shutdown yet)
- UT 87.94: `EngineShutdown pid=2527095907 part=unknown` (booster engine, still running at this UT in reality)
- UT 104.86: `EngineShutdown pid=2527095907 part=unknown` (same pid, same spurious shutdown pattern)

Each spurious Shutdown is accompanied by a `Decoupled` event from `OnPartJointBreak` at the same UT — but the file is missing one of the paired Decoupleds that the log confirms was emitted. So each boundary shows: 1 real Decoupled + 1 spurious terminal (with 1 Decoupled + 2 terminals missing/extra). The terminal event (with `part="unknown"`) is the smoking gun: only `EmitTerminalEngineAndRcsEvents` in `FlightRecorder.cs` stamps `partName="unknown"` as a literal.

**Log verification:** `ResumeAfterFalseAlarm: removed 5/3/3 orphaned terminal event(s)` at lines 9290, 9494, 9663 of the KSP.log say the terminals WERE removed from the live recorder at each boundary. Yet the saved `.prec` file still has one per boundary.

**Root cause:** `ResumeAfterFalseAlarm`'s index-based `PartEvents.RemoveRange(partEventCountBeforeChainStop, N)` was brittle against any code path that reordered the list between `StopRecordingForChainBoundary` and the resume call. Five call sites used the unstable `List<T>.Sort((a, b) => a.ut.CompareTo(b.ut))` pattern during flush/merge:
1. `ParsekFlight.FlushRecorderIntoActiveTreeForSerialization` (line 228)
2. `ParsekFlight.FlushRecorderToTreeRecording` (line 1612)
3. `ParsekFlight.AppendCapturedDataToRecording` (line 1743)
4. `RecordingOptimizer.MergeInto` (line 227)
5. `SessionMerger.MergePartEvents` (line 422)

When the tree root's events were merged with continuation recorder events at OnSave / MergeTree / commit time, an unstable sort could scramble same-UT events. In the Kerbal X case, a terminal Shutdown at UT 72.94 from the tree root and a seed EngineIgnited at the same UT from the continuation could end up in `Ignited-then-Shutdown` order in the committed `.prec`, leaving playback's `ApplyPartEvents` to call `SetEngineEmission(0f)` as the final event at that UT — flame OFF.

**Fix (two prongs):**

1. **Content-based terminal removal** — `FinalizeRecordingState` now saves the exact terminal events it emitted in a new private `lastEmittedTerminalEvents` field. `ResumeAfterFalseAlarm` calls a new `RemoveLastEmittedTerminals()` helper that matches each saved event against `PartEvents` by `(ut, pid, eventType, moduleIndex)` and removes the matching entry. Robust against any sort reordering or intervening append, because removal is by content not position. New `FlightRecorder.FindTerminalEventIndex(list, target)` pure helper does the (ut, pid, type, midx) tuple match.

2. **Stable sort at all merge sites** — new `FlightRecorder.StableSortPartEventsByUT(List<PartEvent>)` static overload returns a NEW list via LINQ `OrderBy` (stable). All five call sites above replaced their `List<T>.Sort` with this helper. Clear+AddRange pattern: `var sorted = StableSortPartEventsByUT(target.PartEvents); target.PartEvents.Clear(); target.PartEvents.AddRange(sorted);`. The static helper is careful to always return a NEW list (even for null/single-element inputs) to prevent aliasing when callers do the Clear+AddRange dance.

**Tests (`Bug287TerminalCleanupTests.cs`, 10 new xUnit tests):**
- `FindTerminalEventIndex_EmptyList_ReturnsMinusOne` / `MatchByTuple_IgnoresPartName` / `NoMatch_ReturnsMinusOne` / `MatchesFirstOccurrence`
- `ResumeAfterFalseAlarm_ContentBasedRemoval_PreservesDecouplesAndRemovesTerminals` — end-to-end: 2 decouples + 3 terminals → 2 decouples remain
- `RemoveLastEmittedTerminals_SurvivesUnstableSortReordering` — adversarial shuffle proves content-based removal works even when the list is scrambled
- `StableSort_TreeMergeAtSameUT_KeepsShutdownsBeforeIgnitedSeeds` — regression guard for the exact 2026-04-09 scenario (5 tree-root terminal Shutdowns + 5 continuation seed Ignited events all at UT 72.94)
- `StableSortPartEventsByUT_Static_NullInput_ReturnsEmptyList` / `SingleElement_ReturnsNewList` / `ClearThenCopyBack_WorksWithSmallLists` — aliasing regression guard (caught by `AppendCapturedDataTests` on the first fix iteration)

All 5466 tests pass.

**Observability:** `ResumeAfterFalseAlarm`'s log line continues to report the number removed. A new `Warn` fires if any saved terminal isn't found in `PartEvents` at resume time (`RemoveLastEmittedTerminals: N terminal event(s) not found`) — that's a canary for any future race condition that manages to drop a terminal between emit and resume.

**Cleanup:** The old `partEventCountBeforeChainStop` private field + its 10-line bug #281 invariant comment in `StopRecordingForChainBoundary` were deleted — content-based removal obviates them and the stale comment was actively misleading. Bug #281's `StableSortPartEventsByUT` instance method is preserved and still called from `FinalizeRecordingState`. `FlagEvents` and `SegmentEvents` sort sites were also converted to stable via a new generic `FlightRecorder.StableSortByUT<T>(list, utSelector)` helper so every flush/merge site uses the same ordering semantics.

**Status:** Fixed

---

## ~~284. Cascading background-vessel splits create recordings for fragments-of-fragments (and tiny single-part debris)~~

Surfaced by the post-PR-#167 Kerbal X investigation (worktree `Parsek-investigations`, branch `investigation/post-167-followups`). One Kerbal X launch produced **25 debris-related recording entries**: 15 real BgRecorder child recordings (most for single-part vessels living < 0.1 s) plus 10 empty-placeholder recordings (matching #285, originally tracked as #282 before the merge collision with PR #172).

**Investigation findings** (`Kerbal Space Program/KSP.log` 20:42–20:46):

- 41 distinct debris vessels detected by `[Flight] Decouple created vessel during split check`. Of those, 15 entered `BgRecorder` (parts=1 single-part fragments confirmed: pid 2007257458 → recId `b9623c20…` ran for **0.06 s, 1 frame** before destruction).
- 12 `BgRecorder Background vessel split detected` events, of which 5 (33 %) were **secondary or tertiary cascades** — debris that came from already-debris within the same playtest:
  - UT 67.1: parent 2279066451 (created at UT 65.4 as a child)
  - UT 69.0: parent 3530302639 (created at UT 67.1 — 3rd-gen)
  - UT 80.7: parent 1113475743 (created at UT 80.4 — 2nd-gen)
  - UT 82.4: parent 2623144656 (created at UT 80.1 — 2nd-gen)
  - UT 83.0: parent 3126189533 (created at UT 80.7 — 3rd-gen)
  - UT 87.8: parent 140686927 splits with `childCount=4` (one event → four sidecar files).
- All 15 BgRecorder child sidecars are 215–865 bytes — tiny multi-frame recordings for parts that crash within seconds of decoupling.

**Root cause**: there was no recursion-depth gate on `HandleBackgroundVesselSplit`. Every joint break by a debris fragment of a fragment of a fragment spawned its own recording with no upper bound on cascade depth.

**Fix** (this PR — cap at gen=1):

- New `Recording.Generation` field (`int`, default 0). Tracks cascade depth from the primary recording. 0 = active vessel; 1 = primary debris (boosters/fairings decoupled by gen-0); 2+ = fragments-of-fragments.
- New `BackgroundRecorder.MaxRecordingGeneration = 1` constant + `BackgroundRecorder.ShouldSkipForCascadeCap(int parentGeneration)` pure helper.
- New gate at the top of `BackgroundRecorder.HandleBackgroundVesselSplit` (after the empty-children early-out so the log line includes `skippedChildren=N`): if `parentRec.Generation >= MaxRecordingGeneration`, log `Cascade depth cap fired: ...skippedChildren=N` at Info and return without creating any branch, child recordings, or parent continuation. The parent's existing recording continues sampling unchanged into its current `BackgroundVesselState`; the new fragment vessels remain alive in KSP but become Parsek-orphans.
- `Generation` is propagated through every recording-creation path:
  - `BackgroundRecorder.BuildBackgroundSplitBranchData` — children get `parentGeneration + 1`. The pure builder does NOT itself enforce the cap; the cap lives in `HandleBackgroundVesselSplit` (separation of concerns).
  - `BackgroundRecorder.HandleBackgroundVesselSplit` — the parent continuation recording inherits `parentRec.Generation` (same logical vessel = same gen). With `MaxRecordingGeneration=1` this is currently always 0 because the cap returns first; the assignment is kept explicit so a future cap bump (e.g. to 2) just works without re-auditing this site.
  - `ParsekFlight.BuildSplitBranchData` — `activeChild` keeps `parentGeneration` (same vessel continues), `bgChild` gets `parentGeneration + 1` (spinoff).
  - `ParsekFlight.CreateBreakupChildRecording` — debris children inherit `parentGeneration + 1`. Call sites in `ProcessBreakupEvent` use `activeRec.Generation`; call sites in `PromoteToTreeForBreakup` use `rootRec.Generation`.
  - `Recording.ApplyPersistenceArtifactsFrom` — copies `Generation` so the StashPending/commit round-trip preserves it.
  - `RecordingOptimizer.SplitAtSection` — both halves share the same `Generation` (same vessel split into two segments).
- **Persisted in `.sfs` via `RecordingTree.SaveRecordingInto`/`LoadRecordingFrom`.** Only written when non-zero so existing gen-0 recordings stay byte-identical and old saves load cleanly. Without persistence, F5/F9 between a booster decouple and the booster's eventual breakup would silently reset the booster's recording to gen=0 and let the cap miss the secondary breakup; persistence closes that window.

The active path (`CreateBreakupChildRecording`, `BuildSplitBranchData`) does NOT have a gate added. Player-initiated splits are explicit user actions; the cascading-debris problem is exclusively a background-side effect of physics breakup. The active path still propagates `Generation` correctly so that any background descendants of an active gen-1+ vessel will hit the gate when they split.

**Side effect — incidentally fixes all 10 #285 placeholders in the reference playtest**: every parent PID for the 10 `Vessel backgrounded (not found)` events in the post-PR-#167 log was at `Generation >= 1` (5× gen-1 boosters from the active-vessel breakup path, 5× gen-2/3 fragments from earlier bg splits). The new gate fires before the not-found path is reached, so no empty placeholder is ever created. **Bug #285 is left open** because the underlying root cause (deferred check fires after parent destruction) could still bite in a hypothetical gen-0 scenario, even though the current data shows zero such occurrences.

**Tests added** (`Source/Parsek.Tests/BackgroundSplitTests.cs` + `RecordingOptimizerTests.cs`):

- `Recording_DefaultGeneration_IsZero`
- `MaxRecordingGeneration_IsOne`
- `ShouldSkipForCascadeCap_Gen0_ReturnsFalse` / `_Gen1_ReturnsTrue` / `_Gen2_ReturnsTrue` / `_LargeGen_ReturnsTrue`
- `BuildBackgroundSplitBranchData_PropagatesGeneration_Gen0Parent_ChildrenAreGen1`
- `BuildBackgroundSplitBranchData_PropagatesGeneration_Gen1Parent_ChildrenAreGen2`
- `BuildBackgroundSplitBranchData_Gen2Parent_ChildrenAreGen3_NotCapped` (documents that the pure builder does NOT enforce the cap)
- `BuildBackgroundSplitBranchData_DefaultParentGeneration_IsZero`
- `BuildSplitBranchData_PropagatesGeneration_BgChildPlusOne_ActiveChildSame`
- `BuildSplitBranchData_Gen1Parent_ActiveStaysGen1_BgChildBecomesGen2`
- `CreateBreakupChildRecording_PropagatesGeneration_Gen0Parent_ChildIsGen1`
- `CreateBreakupChildRecording_PropagatesGeneration_Gen1Parent_ChildIsGen2`
- `CreateBreakupChildRecording_DefaultParentGeneration_ChildIsGen1`
- `Recording_ApplyPersistenceArtifactsFrom_CopiesGeneration`
- `RecordingTree_SaveLoadRoundTrip_PreservesGeneration` (verifies the .sfs persistence path)
- `RecordingTree_SaveRecordingInto_OmitsGenerationWhenZero` (gen-0 recordings stay byte-identical)
- `RecordingTree_LoadRecordingFrom_LegacyNodeWithoutGeneration_DefaultsToZero` (forward compat with legacy saves)
- `RecordingOptimizerTests.SplitAtSection_CopiesGeneration` (both halves share gen)

`HandleBackgroundVesselSplit` itself can't be unit-tested directly (`FlightGlobals.Vessels` access). Coverage is via the pure helpers + the next in-game playtest (count of `Cascade depth cap fired` log lines should match the count of secondary-breakup events; debris recording count for a Kerbal X launch should drop from 25 → ~4–6).

**Impact**: Eliminates ~80 % of debris recording clutter for any vessel with breaking-up boosters. For the reference Kerbal X playtest, the predicted recording count after fix: 1 main + 4–6 boosters (gen-1) — a 4× reduction. Disk usage drops accordingly.

**Status**: Fixed.

---

## ~~285. Empty parent-continuation recordings created when background vessel splits after parent is already destroyed~~

**Fix:** Guard in `HandleBackgroundVesselSplit` — `FindVesselByPid(parentPid)` before creating parent continuation. If null, skip continuation entirely; parent recording keeps `ChildBranchPointId`, children are still registered. See `fix-285-empty-parent-continuation.md` plan.

> **Renumbered from #282** on 2026-04-09. Main shipped a different fix (PR #172, "Landed ghost vessels and end-of-recording respawned vessels no longer clip into terrain") under the same number while this branch was open. This bug is the second one to claim #282, so it's been moved to the next free number to keep the bug-tracking namespace unambiguous.

Surfaced by the post-PR-#167 Kerbal X playtest (2026-04-09 evening session, `KSP.log` ~20:42–20:46). PR #167 fixed bug #280 (debris trajectory data loss) — visual playback is correct, real debris recordings have data — but the same playtest still emits ~10 `Trajectory file missing for … — recording degraded (0 points)` warnings on every F9 reload, repeated across 3 reloads in a row.

**Root cause** (traced via lifecycle log for `2a85eed8c33d4a7ea319c4ada8c6bc37`):
1. Background debris vessel decouples / fragments while not loaded → `BackgroundRecorder.OnBackgroundPartJointBreak` schedules a deferred split check.
2. The deferred check runs in `ProcessPendingSplitChecks` → `HandleBackgroundVesselSplit`.
3. By the time the deferred check runs, the parent debris vessel has already been destroyed by KSP (further crash, terrain hit, or coalesced fragmentation).
4. `HandleBackgroundVesselSplit` still creates a parent-continuation `Recording` with a fresh GUID and calls `OnVesselBackgrounded(parentPid)`.
5. `OnVesselBackgrounded` takes the "vessel not found" path (line ~957), logs `Vessel backgrounded (not found): pid=… recId=2a85eed8…`, and does NOT create a `BackgroundVesselState` entry in `loadedStates` (no live vessel to track).
6. Immediately after, the debris-end logic logs `Debris recording ended: vesselPid=… recId=2a85eed8… terminal=Destroyed`.
7. The recording sits in `tree.Recordings` with zero `TrackSections`, zero `Points`, zero `PartEvents`, `TerminalState=Destroyed`. It's a logical-null leaf.
8. On commit, the empty recording gets a 61-byte `.prec` sidecar (just `version=0` + `recordingId=…`).
9. On the NEXT load, `LoadRecordingFiles` checks `File.Exists(precPath)` and reads the `.prec` — but the file IS there. Wait — the warning fires anyway. Either the file isn't created until AFTER the load that warns, or the empty file is treated as "missing" by some other check upstream.

Either way, the user-facing symptoms are:
- 10× `Trajectory file missing` warning lines on every reload (cosmetic noise, not data loss).
- 10× `FinalizeTreeRecordings: leaf '…' has no playback data` WARN lines.
- 10× empty 61-byte `.prec` files cluttering the recordings directory.
- Possibly an empty / green-sphere ghost entry in the timeline if the UI doesn't filter `points=0 && terminal=Destroyed` recordings.

**Impact**: Low. Visible debris ghosts that the user cares about (the actual booster fragments) DO have data and play back correctly — the user confirmed this in the post-PR-#167 playtest. The empty placeholders are an internal-state cleanup gap, not a data loss.

**Fix candidates** (pick one or layer):
1. **Don't create the parent continuation in the first place if the parent is dead.** In `HandleBackgroundVesselSplit`, check `FlightRecorder.FindVesselByPid(parentPid)` before creating `parentContRec`. If null, just emit the BranchPoint with the new child recordings and skip the parent continuation creation entirely. The parent's recording stays as-is (with whatever data it had pre-destruction) and gets its `ChildBranchPointId` set as before. Cleanest approach.
2. **Suppress the load-time warning** for recordings with `TerminalStateValue == Destroyed && pointCount == 0` — these are expected-empty by design. Trade-off: hides a real bug if something else creates an empty `Destroyed` recording it shouldn't.
3. **Filter empty Destroyed leafs from the tree** in `FinalizeTreeRecordings` or commit so they don't even reach disk. Aggressive — could remove legitimately-empty recordings (e.g., a vessel destroyed before any sample fired).

Recommended: option 1 (don't create the placeholder).

**Test plan**:
- Unit test: simulate `HandleBackgroundVesselSplit` with `parentPid` not in `FlightGlobals.Vessels` → verify no `tree.Recordings[parentContRecId]` entry created.
- Log assertion: count `Trajectory file missing` lines after a Kerbal X playtest with multiple background debris splits — should be 0.
- Regression: existing fragment-survival tests still pass.

**Priority**: Low. Cosmetic log noise + tiny disk waste; no gameplay impact. Worth fixing as a follow-up to keep the BackgroundRecorder lifecycle clean.

**Update (2026-04-09 — #284 side effect)**: The cascade-depth cap shipped in #284 incidentally eliminates all 10 placeholders observed in the post-PR-#167 reference log because every parent had `Generation >= 1`. The not-found path is unreachable for gen-1+ parents. #285 stays open because a gen-0 parent could still hypothetically reach this path (e.g. the active rocket is destroyed in the same frame as a deferred check). No occurrences observed in current data.

---

## ~~283. MergeTree boundary discontinuity warnings — possible ghost position pops at section boundaries~~

Surfaced by the post-PR-#167 Kerbal X playtest. `MergeTree` emitted 8 `boundary discontinuity` WARN lines across 3 vessels:

- `Kerbal X` — 77.38 m at section[1] ut=38.44, 53.17 m at section[2] ut=85.54
- `Bill Kerman` — 3.79 m at section[1] ut=76.40, 49.12 m at section[3] ut=95.72
- `Jebediah Kerman` — 5.43 m at ut=217.98, 6.04 m at ut=218.36, 6.14 m at ut=220.40, 6.14 m at ut=247.42

Source: `SessionMerger.MergeTree`'s diagnostic that compares the last frame of section N to the first frame of section N+1 and warns if the gap exceeds a threshold. The discontinuity represents a position pop that ghost playback would render as a teleport at the section boundary.

**Root cause (confirmed):** Two independent issues producing spurious or real discontinuity warnings:

1. **Missing boundary point in new section:** When an environment transition occurs (e.g., Atmospheric → ExoBallistic at 70 km), `SamplePosition(v)` records a boundary point into the closing section's frames, then `CloseCurrentTrackSection` + `StartNewTrackSection` opens a new empty section. The new section's first frame doesn't arrive until the next physics frame (or later if adaptive sampling skips), during which the vessel moves at high velocity (77 m at ~2000 m/s near atmosphere boundary). Same pattern in BackgroundRecorder.

2. **Cross-reference-frame false positives:** `ComputeBoundaryDiscontinuity` compared lat/lon/alt fields between ABSOLUTE and RELATIVE sections. RELATIVE sections store dx/dy/dz offsets in those fields — comparing them to ABSOLUTE lat/lon/alt is meaningless and produces garbage distance values.

**Fix:** Boundary point seeding + cross-reference-frame skip.
- `FlightRecorder.UpdateEnvironmentTracking`: captures the boundary point from the closing section and seeds it as the first frame of the new section via `GetLastTrackSectionFrame` + `SeedBoundaryPoint`. Only writes to TrackSection frames (not the flat Recording list, which already has the point).
- `BackgroundRecorder`: same pattern via `GetLastBackgroundFrame` + `SeedBackgroundBoundaryPoint` at the environment transition site.
- `SessionMerger.ComputeBoundaryDiscontinuity`: returns 0 when `prev.referenceFrame != next.referenceFrame`, skipping nonsensical cross-frame comparisons.
- Discontinuity warning log now includes `prevRef`/`nextRef` and `prevSrc`/`nextSrc` for future diagnostics.

**Remaining residual:** Cross-source section boundaries (Active→Background handoff) can still produce small discontinuities because the two recorders sample independently at different moments. This is inherent to the architecture and produces only sub-meter gaps under normal conditions. No fix needed.

**Status:** Fixed

---

## 286. Full-tree crash leaves nothing to continue with — `CanPersistVessel` blocks all `Destroyed` leaves (largely mitigated)

Surfaced as the user-facing complaint behind bug #278 ("nothing to continue playing with after a crash recording"). #278's snapshot-loss fix restores the in-memory `hasSnapshot=True` state for background-split debris, but the merge dialog's `CanPersistVessel` predicate at `MergeDialog.cs:450-467` hard-blocks any leaf whose `TerminalStateValue ∈ {Destroyed, Recovered, Docked, Boarded}` regardless of snapshot availability. When the player crashes the entire launch tree (root vessel + all debris destroyed), every leaf falls into one of those gated states, the merge dialog reports `spawnable=0`, and no vessel ends up on the surface for the player to continue with.

Bob Kerman EVA in the 2026-04-09 playtest log line 11548 illustrates the gating shape: `terminal=Destroyed hasSnapshot=True canPersist=False`. The snapshot survives, the policy blocks the spawn.

**This is a design decision, not a bug.** Three options for the next user discussion:

- **(a) Pre-crash F5 fallback.** When no leaf satisfies `CanPersistVessel`, fall back to spawning the player's most recent quicksave's vessel state. Pro: works without changing recording semantics; the F5 represents an implicit "checkpoint." Con: requires tracking the F5 vessel state across the merge cycle, and the spawned state may not match where the player expects (e.g., F5 was at 50 km altitude, recording continued past F5 to a crash on the ground).
- **(b) Relax `Destroyed` gating when snapshot exists.** Allow `terminal=Destroyed` to spawn from snapshot for at least the root leaf. Pro: minimal change. Con: the snapshot represents a moment-in-time; for a vessel destroyed by physics, the snapshot at split-time may be from many seconds before the crash, in a different orbit/altitude/orientation. Spawning from it could deposit the vessel in a physically-impossible state (e.g., underwater, mid-air, intersecting terrain).
- **(c) Status quo + UX message.** Leave the gating unchanged but surface a clear merge-dialog message: "All recording branches ended in failure (crash/dock/recover) — no vessel can be continued. Use the Tracking Station or load your last quicksave to continue playing." Pro: zero behavioral change, clearly communicates the situation. Con: doesn't actually solve the user's "nothing to continue with" complaint.

**Investigation needed before fix:**
- Confirm whether KSP players actually expect crashes to be "playable from a checkpoint" (vs. quicksave-only). The Parsek model is closer to flight-replay than save-state, so option (c) may be the most defensible default.
- For option (a), enumerate the failure modes: what if the F5 is from a different vessel, a different scene, or there's no F5 at all?
- For option (b), enumerate the safety net: terrain clamping, orbital validity checks, attitude reset, fuel restoration. This is a lot of new code.

**Update (2026-04-10 investigation):** The `spawnable=0` cases in the 2026-04-09 playtests were primarily caused by #278's blanket `Destroyed` stamping, not by a true all-crash scenario. With #278 fixed (PR #173/#176 — `FinalizeIndividualRecording` now uses real vessel `situation`) and #284 shipped (PR #173 — cascade cap reduces debris from ~25 to ~4-6), the current KSP.log shows `spawnable=6-8` for the same Kerbal X launch. Debris that is still SubOrbital at merge time is now correctly labeled `canPersist=True`. The remaining edge case is a genuine full-tree crash where ALL parts are destroyed before merge — rare in practice and arguably correct behavior (`spawnable=0` when everything is truly gone).

**Priority:** Low (downgraded from Medium). The original user complaint is resolved by #278/#284. The remaining scenario (everything truly destroyed) is a design decision for option (c) UX messaging, not an urgent fix.

---

## ~~280. Background debris recordings lose trajectory data on scene reload (second-order bug #273 gap)~~

During the 2026-04-09 Kerbal X playtest after PR #163/#164 merged, the 6 booster debris recordings from the radial decoupler separations all came back as 0-point / 0-section recordings. The main Kerbal X recording (#263) was fine — 88 points, all 6 `Decoupled` events present (via the #263 fallback) — but the debris children that each accumulated 21–22 trajectory frames while backgrounded lost every sample.

**Evidence chain from the playtest log:**
- `17:42:32.879` BackgroundRecorder closed the debris' TrackSection with `frames=22` and called `FlushTrackSectionsToRecording(...) → treeRec.MarkFilesDirty()`.
- `17:42:35.440` `OnSave #31` wrote `ACTIVE tree 'Kerbal X' (16 recording(s))` for quickload resume.
- `17:44:28.987` `OnLoad` from F9 quickload: `[RecordingStore] Trajectory file missing for d1b0a56e… — recording degraded (0 points)` — **16 debris recordings in a row**. The `.prec` sidecars were never written between flush and reload.
- `17:44:33.228` `CommitTree` committed the debris recordings with `sections=0 events=0` and `FlushDirtyFiles` then wrote the now-empty recordings over the (lost) in-memory data.

**Root cause (not fully characterized):** Each finalization site in `BackgroundRecorder` (`OnBackgroundVesselWillDestroy`, `Shutdown`, `HandleBackgroundVesselSplit` → `CloseParentRecording`) flushes TrackSections to `treeRec` and calls `treeRec.MarkFilesDirty()`, which bug #273's PR #164 extension explicitly audited. `SaveActiveTreeIfAny` then iterates `activeTree.Recordings.Values` and is SUPPOSED to call `SaveRecordingFiles(rec)` for every `rec.FilesDirty == true`. But something between the flush and OnSave consistently breaks that contract for debris recordings — `SaveRecordingFiles` is never called for them, and the outer `wrote ACTIVE tree … (N recording(s))` log hides it because `N` is the iteration count, not the dirty/saved count. The exact drop mechanism could not be identified from log + static analysis alone in a reasonable timebox.

**Fix (defensive, pragmatic):** Bypass the FilesDirty → OnSave round-trip entirely for the finalization path. After every `FlushTrackSectionsToRecording` call in a BackgroundRecorder site that is about to let the vessel go (destroyed / shutdown), immediately call a new helper `PersistFinalizedRecording(flushRec, context)` that writes the `.prec` sidecar to disk right then via `RecordingStore.SaveRecordingFiles`. This closes the window between "data lives in memory" and "data lives on disk", no matter what happens to the dirty flag.

**Fix locations:**
- New private helper `BackgroundRecorder.PersistFinalizedRecording(Recording, string context)` — calls `SaveRecordingFiles` and logs success/failure.
- `OnBackgroundVesselWillDestroy` — persists after the destroy-path flush (primary fix for the 2026-04-09 playtest).
- `Shutdown` — persists after each per-vessel flush during scene-change / tree teardown.
- `HandleBackgroundVesselSplit` — already covered indirectly because `OnBackgroundVesselWillDestroy` fires before the deferred split check for the destroy case. Split-but-not-destroyed path does not flush TrackSections to disk because the vessel's state survives into a continuation recording.

**Observability added alongside the fix** (bug #280 diagnostic, no behavior change):
- `SaveActiveTreeIfAny` now logs `SaveActiveTreeIfAny: iterated N recording(s), D dirty, S saved, F failed` so any future FilesDirty-propagation gap is visible in KSP.log without code archaeology. The previous `wrote ACTIVE tree … (N recording(s))` log only reported the iteration count.
- `PersistFinalizedRecording` logs `wrote sidecar for recId=…` at Verbose on success, `failed to write sidecar` at Warn on failure.

**Tests:** Not unit-testable directly — the destroy/shutdown paths require a live `Vessel` + `BackgroundVesselState`. Coverage comes from the next Kerbal X in-game playtest (user validation).

**Status:** Fixed

---

## ~~281. ResumeAfterFalseAlarm index-based RemoveRange can drop decoupled events that share a UT with terminal events (bug #263 root cause)~~

The bug #263 fix (PR #161) added a `DeferredJointBreakCheck` fallback that recovers `Decoupled` PartEvents lost somewhere in the recorder → tree pipeline. Post-merge investigation into the 2026-04-09 Kerbal X playtest pinpointed WHERE they were being lost: `FlightRecorder.FinalizeRecordingState` sorts `PartEvents` by UT AFTER appending terminal engine-shutdown events, but `StopRecordingForChainBoundary` saved `partEventCountBeforeChainStop = PartEvents.Count` BEFORE the finalize. Then `ResumeAfterFalseAlarm` does `PartEvents.RemoveRange(partEventCountBeforeChainStop, N)` — an index-based removal.

The sort was using `List<T>.Sort(Comparison<T>)` which is an introspective sort and **NOT stable**. When the player staged a radial decoupler symmetry group:
- OnPartJointBreak fired and appended two decouples at UT 26.08 (insertion order indices X, X+1)
- FinalizeRecordingState appended 5 terminal engine shutdowns at the same UT 26.08 (indices X+2…X+6)
- `partEventCountBeforeChainStop` = X+2 (saved before the terminal appends)
- `List<T>.Sort` reordered the 7 same-UT events arbitrarily — sometimes shuffling a decouple into indices X+2…X+6
- `RemoveRange(X+2, 5)` then dropped that decouple along with 4 terminals

Pair 1 in the 2026-04-09 playtest hit this: `pid=1009856088` (booster-side) was added to `PartEvents` (the verbose `Part event: Decoupled` log fires), then silently disappeared before the tree commit. The PR #161 fallback recovered it via the new-vessel-root scan, but the underlying sort-vs-index trap remained.

**Fix:** New `FlightRecorder.StableSortPartEventsByUT` uses LINQ `OrderBy(e => e.ut).ToList()` — `OrderBy` is documented as stable, so same-UT events keep their insertion order. Decouples (appended first by `OnPartJointBreak`) stay at indices [X, X+1] after sort; terminals (appended second by `FinalizeRecordingState`) stay at indices [X+2, X+6]. `RemoveRange(partEventCountBeforeChainStop, N)` now only touches real terminals.

**Interaction with bug #263 fallback:** the PR #161 `RecordFallbackDecoupleEvent` safety net is still valuable as a second line of defense against any future race condition, but with this fix the fallback becomes a no-op in the specific scenario it was written for — the decouples now survive the `ResumeAfterFalseAlarm` cycle naturally. The fallback's `RecordFallbackDecoupleEvent_RecoversEvent_WhenPartEventsDroppedButDecoupledSetStale` regression test still guards against the decoupledPartIds-drift scenario.

**Tests:** `Bug263SortTrapTests.cs` — 6 unit tests:
- `StableSortPartEventsByUT_EmptyList_IsNoOp` / `SingleEvent_IsNoOp`
- `StableSortPartEventsByUT_OrdersByUT_Ascending`
- `StableSortPartEventsByUT_SameUTEvents_PreserveInsertionOrder` (decouples + terminals scenario)
- `ResumeAfterFalseAlarm_SameUTDecouples_SurviveRemoveRange` (end-to-end simulation — the true regression guard)
- `StableSortPartEventsByUT_MixedOrder_WithAndWithoutTies_SortsCorrectly`

Each test uses the exact same pids from the 2026-04-09 Kerbal X playtest for traceability.

**Status:** Fixed

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

## ~~132. Policy RunSpawnDeathChecks and FlushDeferredSpawns are TODO stubs~~

`RunSpawnDeathChecks()` now iterates committed recordings each frame, checks if spawned vessel PIDs still exist via `FlightRecorder.FindVesselByPid`, increments `SpawnDeathCount` on death, and either resets for re-spawn or abandons after `MaxSpawnDeathCycles`. New pure predicate `ShouldCheckForSpawnDeath` in `GhostPlaybackLogic`.

`FlushDeferredSpawns()` moved from `ParsekFlight` to `ParsekPlaybackPolicy`, eliminating the split-brain bug where the policy populated its own `pendingSpawnRecordingIds` in `HandlePlaybackCompleted` but the flush read from ParsekFlight's never-populated duplicate set. The policy now owns the full lifecycle: queue during warp → flush when warp ends.

**Status:** Fixed

## ~~133. Forwarding properties in ParsekFlight add ~500 lines of indirection~~

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

## ~~196. Ghost icon popup window should appear next to cursor~~

The popup spawned via `PopupDialog.SpawnPopupDialog` consistently appears at screen center despite attempts to reposition. KSP's `SpawnPopupDialog` forces `localPosition=Vector3.zero` after anchor setup. Need to use the same approach as KSP's `MapContextMenu`: anchor at (0,0), then set `localPosition` via `CanvasUtil.ScreenToUISpacePos`.

**Root cause:** Three compounding issues: (1) anchors at (0.5, 0.5) meant `localPosition=zero` placed the popup at screen center, and subsequent positioning was relative to center instead of a stable origin. (2) Missing `LayoutRebuilder.ForceRebuildLayoutImmediate` — without forcing layout completion before setting position, Unity's end-of-frame layout pass could reset the position. (3) Wrong canvas rect — code used the PopupDialogCanvas parent rect instead of `MapViewCanvasUtil.MapViewCanvasRect` (the main UI canvas), and missed `CanvasUtil.AnchorOffset` to push the menu below the click point.

**Fix:** Matched KSP's stock `MapContextMenu.SetupTransform` pattern: (0,0) anchors, `SetDraggable(false)`, `ForceRebuildLayoutImmediate` before positioning, `ScreenToUISpacePos` with `MapViewCanvasUtil.MapViewCanvasRect`, `AnchorOffset` with `Vector2.down` so the menu opens below the cursor. Edge-clamping (popup near screen edge) is a known cosmetic limitation matching stock behavior — not addressed here.

**Status:** Fixed

## ~~203. Green dot ghost markers at wrong positions near Mun after scene reload~~

Two compounding issues: (1) `TerminalOrbitBody` is null on all recordings at load time — `HasOrbitData(Recording)` returns false for all 62 recordings, preventing ProtoVessel creation from the initial scan. (2) After FLIGHT→FLIGHT scene reload, ghost map vessel positions jump from Mun-relative (~11M m) to world-frame (~2B m) — the coordinate frame shifts during scene reload and positions aren't corrected.

**Root cause:** `SaveRecordingMetadata` / `LoadRecordingMetadata` (used by standalone recordings) never serialized the 8 terminal orbit fields (`tOrbBody`, `tOrbInc`, `tOrbEcc`, `tOrbSma`, `tOrbLan`, `tOrbArgPe`, `tOrbMna`, `tOrbEpoch`). Tree recordings were unaffected because `RecordingTree.SaveRecordingInto` / `LoadRecordingFrom` already handled these fields. After save/load, all standalone recordings had `TerminalOrbitBody = null`, so `HasOrbitData` returned false and no ghost map ProtoVessels could be created. Issue (2) was a consequence: without valid orbit data, ghost positions computed from stale or zero orbital elements produced world-frame coordinates instead of body-relative.

**Fix:** Added terminal orbit field serialization to `SaveRecordingMetadata` and `LoadRecordingMetadata`, matching the existing pattern in `RecordingTree`.

**Status:** Fixed

## ~~217. Settings window GUILayout exception (Layout/Repaint mismatch)~~

`DrawSettingsWindow` throws `ArgumentException: Getting control N's position in a group with only N controls when doing repaint`. Unity IMGUI bug caused by conditional `GUILayout` calls whose condition changes between Layout and Repaint passes. The window is stuck at 10px height and non-functional. 72 exceptions per session when the settings window is opened.

**Fix:** Removed the early `return` in `DrawGhostSettings` that conditionally skipped ghost cap slider controls when `ghostCapEnabled` was false. Sliders are now always drawn (for IMGUI Layout/Repaint consistency) but grayed out via `GUI.enabled` when caps are disabled.

**Status:** Fixed

## ~~218. Crash breakup debris not recorded when recorder tears down before coalescer~~

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

## ~~256. EVA recording runaway sampling — slow-motion adaptive sampler defeated~~

The adaptive sampler had no minimum interval floor, only a max-interval backstop and velocity-based gates. EVA kerbals walking on a surface defeated the velocity gates on every physics frame, producing 1 commit per frame (~28 pts/s for the verified Bob-on-Mun recording at `logs/2026-04-08_mun-mission/Recordings/ab105395ae5547b0b70c1eb9bb41ca9f.prec`, 238 points / 8.34 s). The original bug-report figure of "138K points in 33 s = 4200 pts/s" came from a pre-format-v0 (PR #114) artifact and is not reproducible from current code — at 50 Hz physics and 1 `Recording.Add` per `OnPhysicsFrame`, the absolute ceiling is ~50 pts/s. The architectural defect is real but its current-code magnitude is ~10× the 3 pts/s target, not 1000×.

**Three threshold-defeat mechanisms for slow-moving vessels:**
1. **Direction gate angular sensitivity at low speed:** at 1 m/s walking pace, a 0.05 m/s perpendicular noise component = `arctan(0.05/1) ≈ 2.86°` → exceeds the 2° gate every frame.
2. **Speed gate floor + low base speed:** `Mathf.Max(lastSpeed, 0.1f)` makes the divisor 0.1 at very low speeds, so any 5+ mm/s change is ≥ 5% → fires the speed gate.
3. **Direction gate skipped at near-zero speed** (`currentSpeed ≤ 0.1f`), leaving only the speed gate which fires per #2.

**Foreground/background asymmetry:** `BackgroundRecorder.OnBackgroundPhysicsFrame` already has a hard floor via `ProximityRateSelector` (0.2 / 0.5 / 2.0 s tiers). `FlightRecorder.OnPhysicsFrame` had only the velocity gates. This is the architectural gap closed by the fix.

**Fix:** Added `minInterval` parameter to `TrajectoryMath.ShouldRecordPoint` — a hard floor checked after the first-point exception and after the max-interval backstop (so degenerate `min > max` configs still produce samples). New `ParsekSettings.minSampleInterval` field with 0.05–1.0 s slider, default 0.2 s. The default matches `ProximityRateSelector.DockingInterval` for recorder mode parity. `FlightRecorder`, `ChainSegmentManager.SampleContinuationVessel`, and `BackgroundRecorder.OnBackgroundPhysicsFrame` all pass through the new parameter; the background path additionally folds its separate `state.lastSampleUT` proximity gate into the new floor parameter (single source of truth, dead `lastSampleUT` field removed).

**Companion fix (D):** `FlightRecorder.UpdateAnchorDetection` previously used raw `v.situation` for the `onSurface` check, which can flip-flop LANDED↔FLYING per frame during EVA jitter near a flying vessel. Replaced with new `EnvironmentDetector.IsSurfaceForAnchorDetection` which prefers the debounced environment classification when available (mirrors the #246 near-surface override pattern), falling back to raw situation when no classifier is initialized. Narrow but related to the same jitter source.

**Tests:**
- 7 new xUnit tests in `AdaptiveSamplingTests.cs`: floor blocks within window, allows after, doesn't block first point, doesn't block max backstop (degenerate config), EVA walking pattern caps at ~5 commits/s, high-speed ascent has no commit-count loss vs legacy, regression guard for `minInterval=0` legacy preservation.
- 22 pre-existing `AdaptiveSamplingTests.cs` cases updated to pass `MinInterval = 0f` constant — explicit regression guard against accidentally enabling the floor in legacy tests.
- 3 new xUnit theory groups in `EnvironmentDetectorTests.cs` for `IsSurfaceEnvironment` and `IsSurfaceForAnchorDetection` covering the 6 environment values, the LANDED-vs-debounced-Atmospheric inversion, the FLYING-vs-debounced-SurfaceMobile inversion, and the 7 fallback situation values.
- 1 new in-game test `MinSampleIntervalCapsEvaJitter` in `RuntimeTests.cs` (Category=TrajectoryMath) that reads the live `ParsekSettings.Current?.minSampleInterval`, asserts it is in `(0, 1]`, runs the EVA-jitter simulation against the live settings, and asserts ≤ 7 commits over 1 s (regression guard against settings load failures).

**Status:** Fixed

## ~~257. Orbit segment with negative SMA (hyperbolic escape)~~

In-game test `OrbitSegmentBodiesValid` fails: "Orbit segment for 'Mun' has non-positive SMA=-931047.895195401". Negative SMA is physically correct for hyperbolic orbits (eccentricity > 1) but the test asserts positive SMA.

**Root cause:** Test assertion bug. Hyperbolic escape orbits have negative SMA by definition. `CreateOrbitSegmentFromVessel` correctly copies `v.orbit.semiMajorAxis`.

**Fix:** Test now skips the `SMA > 0` assertion when `eccentricity > 1.0` (hyperbolic orbit).

**Status:** Fixed

## ~~258. Non-chronological trajectory points in recording~~

In-game test `CommittedRecordingsHaveValidData` fails: recording `ab105395ae5547b0b70c1eb9bb41ca9f` (Bob Kerman EVA) has point 159 UT going backward by ~33 seconds. Trajectory points should be monotonically increasing in UT.

**Root cause:** Quickload during recording resets game time to quicksave UT, but the recorder continued appending points without trimming the stale future-timeline data. `CommitRecordedPoint` and `SamplePosition` had no time monotonicity guard.

**Fix:** Added `TrimRecordingToUT` method to `FlightRecorder`. Both `CommitRecordedPoint` and `SamplePosition` now detect time regression (UT going backward by >1s) and trim stale points, part events, and orbit segments before appending the new point.

**Status:** Fixed (prospective — existing corrupted recording data will still fail; fix prevents recurrence)

## ~~259. Orbital recordings missing TerminalOrbitBody~~

In-game test `OrbitalRecordingsHaveTerminalOrbit` reports 4 orbital recordings without `TerminalOrbitBody` set. This is a regression guard for #203/#219.

**Root cause:** Three code paths set `TerminalStateValue` to Orbiting/Docked without also calling `CaptureTerminalOrbit`: (1) `CaptureSceneExitState` in ParsekFlight, (2) vessel-switch chain termination in ChainSegmentManager, (3) debris recording end in BackgroundRecorder. Then `FinalizeIndividualRecording` skips orbit capture because `TerminalStateValue.HasValue` is already true.

**Fix:** Added `CaptureTerminalOrbit` calls to all three source paths. Added defensive backfill in `FinalizeIndividualRecording`: when `TerminalStateValue` is Orbiting/SubOrbital/Docked but `TerminalOrbitBody` is null, try vessel orbit capture or fall back to last orbit segment.

**Status:** Fixed

## ~~260. Remove .pcrf ghost geometry scaffolding~~

`.pcrf` (ghost geometry cache) was planned to cache pre-built ghost meshes to avoid PartLoader resolution on every spawn. Never implemented — `GhostVisualBuilder` always builds from the vessel snapshot directly. Fields were loaded from ConfigNode (`ghostGeometryPath` / `ghostGeometryAvailable` / `ghostGeometryError`) but never written, so they always defaulted on next save anyway.

**Fix:** Deleted `BuildGhostGeometryRelativePath` from `RecordingPaths`; removed `GhostGeometryRelativePath` / `GhostGeometryAvailable` / `GhostGeometryCaptureError` fields from `Recording`; removed `.pcrf` from `RecordingStore.RecordingFileSuffixes` (orphan-detection); removed `geometryFileBytes` from `StorageBreakdown`; removed the `.pcrf` stat call from `DiagnosticsComputation.ComputeStorageBreakdown`; removed the no-op load blocks from `ParsekScenario` and `RecordingTree`; removed the `RecordingOptimizer.MergeInto` / `SplitAtSection` invalidation lines (no field to clear). 5 test sites updated, 4 doc files updated.

**Legacy file cleanup (PR #168 review follow-up):** Code review caught that simply removing `.pcrf` from `RecordingFileSuffixes` made `ExtractRecordingIdFromFileName` return null for `*.pcrf` files, which the orphan scan then treated as "unrecognized" and skipped — leaving stale `.pcrf` files on disk forever. Added `LegacyRecordingFileSuffixes = { ".pcrf" }` array and an `IsLegacySidecarFile` pure helper; `CleanOrphanFiles` now unconditionally deletes legacy sidecars in addition to its existing orphan-by-id-mismatch cleanup. Logged separately as "legacy sidecar file(s)" for visibility. New `IsLegacySidecarFile_Works` Theory test (9 cases).

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
- ~~`VesselSpawner.StripEvaLadderState` (`VesselSpawner.cs:1034`) writes the literal FSM state `"idle"` which is not a valid `KerbalEVA` state name (real names are `st_idle_gr` / `st_idle_fl` / `st_swim_idle`). `StartEVA` catches the unknown-state exception and falls back to a `SurfaceContact`-driven default state so this is functionally correct but cosmetically broken.~~ **Fixed:** replaced `SetValue("state", "idle", true)` with `RemoveValue("state")` so the FSM initializes fresh with the correct `st_*` name picked by `KerbalEVA.StartEVA` based on situation.
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

## ~~266. Tree-preservation on vessel switch (quickload-resume follow-up)~~

PR #160 routed `isVesselSwitch` through `FinalizePendingLimboTreeForRevert` as a temporary punt — when the player clicked a distant vessel in the Tracking Station, the FLIGHT→FLIGHT scene reload destroyed the in-progress mission tree even though the in-session vessel-switch path (`OnVesselSwitchComplete`, `ParsekFlight.cs:1564-1572`) already handled this state correctly by moving the old vessel into `BackgroundMap`.

**Smoking gun:** `logs/2026-04-09_f5f9_verify/KSP.log:11208-11390` (Tree `0529116f|Kerbal X` → `StashTreeLimbo` → `FinalizeLimboForRevert: finalized 1 leaf recording(s)` → `OnLoad: Limbo tree finalized on vessel switch (matches mainline behavior; tree-preservation-on-switch is a follow-up)`) and the same pattern in `logs/2026-04-09_recording-flow-bugs/KSP.log:12074-12287`.

**Architectural challenge:** the `activeTree` field lives on the `ParsekFlight` MonoBehaviour, which is destroyed on scene reload. The tree must cross the scene boundary via `RecordingStore.PendingTree`. The previous restore coroutine (`RestoreActiveTreeFromPending`) name-matches the tree's active recording against the new active vessel — guaranteed to fail on a switch because the player picked a *different* vessel by definition.

**Fix:** pre-transition the tree at stash time so the restore coroutine just has to reinstall it.

1. **New `PendingTreeState.LimboVesselSwitch = 2`** (`RecordingStore.cs`) — distinguishes pre-transitioned trees from the legacy `Limbo` (untriaged) state.
2. **`StashActiveTreeForVesselSwitch(commitUT)`** (`ParsekFlight.cs`) — flushes the recorder, calls the new pure helper `ApplyPreTransitionForVesselSwitch(tree, recorderVesselPid)` which moves the active recording's PID into `BackgroundMap` and nulls `ActiveRecordingId`, then stashes as `LimboVesselSwitch`. Recorder PID has priority over the tree-recording's persisted PID (the tree value can lag across docking / boarding edge cases).
3. **`FinalizeTreeOnSceneChange` dispatcher** — checks `ParsekScenario.IsVesselSwitchPendingFresh()` (new live query exposing the existing freshness check) and routes to the new path. Bails out to legacy `StashActiveTreeAsPendingLimbo` when `pendingTreeDockMerge`, `pendingSplitRecorder`, or `recorder.ChainToVesselPending` is set — those need `Update()` continuation logic that never runs across a scene reload, and the safety-net finalize is the safer fallback (`CanPreTransitionForVesselSwitch`).
4. **`RestoreActiveTreeFromPendingForVesselSwitch` coroutine** — pops the pre-transitioned tree, installs as `activeTree`, re-attaches `BackgroundRecorder`. If the new active vessel's PID is in `tree.BackgroundMap` (round-trip), promotes via existing `PromoteRecordingFromBackground`. Otherwise leaves `recorder = null` (outsider state — identical to the existing in-session outcome at `ParsekFlight.cs:1568-1572` when `BackgroundMap.TryGetValue` returns false).
5. **`ActiveTreeRestoreMode` enum** (`None`/`Quickload`/`VesselSwitch`) replaces the previous `ScheduleActiveTreeRestoreOnFlightReady` bool to prevent the two restore paths from conflicting flags (review feedback).
6. **`OnLoad` Limbo dispatch** (`ParsekScenario.cs`) — four-way decision tree: real revert → finalize; `LimboVesselSwitch` state → vessel-switch restore; `Limbo` + `isVesselSwitch` → safety-net finalize (stash bailed); else → quickload restore.
7. **`SaveActiveTreeIfAny` outsider tolerance** — was guarded with `if (recorder == null) return;` which silently dropped outsider-state trees. Now serializes the tree without flush when `recorder == null`. The `recorder.RewindSaveFileName` access is null-guarded; the rewind save filename is preserved on the tree's root recording at stash time.
8. **`TryRestoreActiveTreeNode` state pick** — picks `LimboVesselSwitch` when the loaded tree has null `ActiveRecordingId` (i.e. was saved in outsider state), `Limbo` otherwise. Handles both cold-start and in-session OnLoad paths.

**Recorder-null audit:** per the review, `recorder == null && activeTree != null` is a novel cross-scene-reload state. Audit was much smaller than feared because the existing in-session `OnVesselSwitchComplete` already creates this state when the new vessel is not in `BackgroundMap` (`logs/2026-04-09_f5f9_verify/KSP.log:11466` shows `mode=tree tree=eda53668 rec=-` in production), so the rest of the codebase already tolerates it. New tolerance only needed in `SaveActiveTreeIfAny` (above).

**Tests:**
- `Source/Parsek.Tests/QuickloadResumeTests.cs` — 11 new tests: `LimboDispatch_*` four-way decision table (revert, vessel-switch pre-transitioned, vessel-switch safety-net, quickload, cold-start outsider), `TryRestoreActiveTreeNode_TreeWith[out]ActiveRecording_StashesAs*`, `PreTransition_*` exercises the pure helper across recorder PID priority, fallback to tree-rec PID, null active rec, both PIDs zero, and BackgroundMap entry preservation across multi-hop switches.
- `Source/Parsek/InGameTests/ExtendedRuntimeTests.cs` — `OutsiderActiveTreeParsesAndRoutesToLimboVesselSwitch_Bug266` (synthesizes an outsider tree from the live tree, asserts the ConfigNode round-trip preserves null `ActiveRecordingId` and that the OnLoad dispatch would route it to `LimboVesselSwitch`) and `PreTransitionForVesselSwitch_LiveTreeShape_Bug266` (validates the pure helper against the live `RecordingTree` shape). The full `OnSave/OnLoad` round-trip is deferred to manual playtest because mutating live `ParsekScenario` state mid-flight isn't safe inside a single in-game test.

**Deferred:** mid-flight `pendingSplitRecorder` preservation across vessel-switch reloads — the in-flight split window cannot survive a scene tear-down, so the safety-net finalize fires for that edge case (matches pre-#266 behavior). `BoundaryAnchor` is also still discarded on the restore (existing limitation, see `RestoreActiveTreeFromPending` :5520-5524 comment). Both are noise rather than data loss; the critical path (preserve the tree across normal TS-mediated switches) is fixed.

## ~~267. Quickload-resume: restore coroutine reentrancy guard~~

Added `internal static bool restoringActiveTree` guard on `ParsekFlight`. Set via try/finally in both `RestoreActiveTreeFromPending` and `RestoreActiveTreeFromPendingForVesselSwitch`. Guards `OnVesselSwitchComplete`, `OnVesselWillDestroy`, `FinalizeTreeOnSceneChange`, and `OnFlightReady` (protects `ResetFlightReadyState` from double-fire). Each guard logs a `[WARN]` when suppressing an event during the restore window.

**Fix:** `ParsekFlight.cs` — `restoringActiveTree` field + 4 guard sites + try/finally on 2 coroutines.

## ~~268. Quickload-resume: snapshot preservation through revert finalization~~

Belt-and-braces snapshot capture added to `StashActiveTreeAsPendingLimbo`. Before stashing the tree, iterates all leaf recordings and captures a fresh `VesselSnapshot` via `VesselSpawner.TryBackupSnapshot` for any leaf with `VesselSnapshot == null` (vessels are still alive in FlightGlobals at stash time). Does NOT overwrite existing snapshots. The primary re-snapshot path is the #289 block in `FinalizeIndividualRecording` which already handles stale snapshots for stable-terminal recordings; this fix covers the narrow edge case where vessels are unloaded before `FinalizePendingLimboTreeForRevert` runs.

**Fix:** `ParsekFlight.cs` — snapshot capture loop in `StashActiveTreeAsPendingLimbo`.

## ~~269. In-game test coverage for quickload-resume flow~~

**Phase 1 (infrastructure):** `TestRunnerShortcut` migrated from `[KSPAddon(KSPAddon.Startup.EveryScene, false)]` to `[KSPAddon(KSPAddon.Startup.Instantly, true)]` + `DontDestroyOnLoad(gameObject)`, mirroring `ParsekHarmony.cs`. Added `GameEvents.onGameSceneLoadRequested` listener to reset `windowHasInputLock` and null `opaqueStyle` on scene change. Added `LOADING` scene guard in `OnGUI`.

**Phase 2 (tests shipped):**
1. `BridgeSurvivesSceneTransition` — canary test verifying DontDestroyOnLoad works across quickload.
2. `Quickload_MidRecording_ResumesSameActiveRecordingId` — F5/F9 mid-recording resumes with same activeRecordingId.
3. `ReentrancyGuard_ClearedAfterRestore` — verifies `restoringActiveTree` is false during normal flight.

**Helpers:** `QuickloadResumeHelpers.cs` with `TriggerQuicksave`, `TriggerQuickload`, `WaitForFlightReady`, `WaitForActiveRecording`.

**Tests deferred:** `RealRevert_FinalizesTree_ShowsMergeDialogWhenAutoMergeOff`, `DoubleF9_Idempotent_NoDoubleStart`, `QuickloadIntoNonFlightScene_DoesNotRestore`, `RewindButton_DoesNotConflictWithRestore` — require more infrastructure validation before shipping.

## 270. Sidecar file (.prec) version staleness across save points

Latent pre-existing architectural limitation of the v3 external sidecar format: sidecar files (`saves/<save>/Parsek/Recordings/*.prec`) are shared across ALL save points for a given save slot. If the player quicksaves in flight at T2, exits to TS at T3 (which rewrites the sidecars with T3 data), then quickloads the T2 save, the .sfs loads the T2 active tree metadata but `LoadRecordingFiles` hydrates from T3 sidecars on disk — a mismatch.

Not introduced by PR #160, but PR #160's quickload-resume path makes it more reachable (previously, quickloading between scene changes always finalized the tree, so the tree was effectively "new" each time).

**Fix plan (long-term):** version sidecar files per save point — stamp each `.prec` with the save epoch or a hash, refuse to load mismatched versions. Alternatively, never rewrite sidecars for committed trees; treat them as immutable.

**Priority:** Low — rare user workflow (quicksave in flight + exit to TS + quickload), and the worst case is playback inconsistency, not data loss. Flag again if it bites during playtest.

## ~~282. Landed ghosts and end-of-recording spawned vessels clip into terrain (2026-04-09 s33 playtest)~~

User report from the second 2026-04-09 playtest (save folder `s33`): "the ghosts that landed went underground and at the end I think they also spawned underground (or did not spawn, not sure)".

**Smoking gun:** three landed leaves of the Kerbal X tree finalized with razor-thin recorded clearances — 0.8 m, 0.9 m, 1.3 m (`logs/2026-04-09_ghosts-underground/KSP.log:13768/13793/13795`). Both spawn-at-end firings (pre-rewind KSC spawn at `L16773-16778`, post-rewind in-flight spawn at `L20599-20622`) used the raw recorded altitude — no `"Clamped altitude for LANDED spawn"` line appears anywhere in the session log. Watch playback at `L20624-L20701` also reported `ghost at alt 176m on Kerbin`, matching the raw recorded altitude.

**Root cause — two sites, same wrong assumption.** KSP's `body.GetWorldSurfacePosition(lat, lon, alt)` places the **root-part origin** at the given altitude. For a Mk1-3 pod, the bounding box is `(2.50, 4.21, 2.50)` with center Y=0.34, so the lowest mesh vertex is **~1.77 m below the root origin**. When the recorded clearance above terrain is less than 1.77 m, the pod bottom clips into terrain — regardless of whether `alt ≥ terrainAlt`.

**Site 1 — `VesselSpawner.ClampAltitudeForLanded`.** Introduced in `#231` to clamp mid-descent snapshots down to `terrainAlt + LandedClearanceMeters` (2 m). The implementation had three branches:
1. `alt > target` → clamp down (the #231 case).
2. `alt < terrainAlt` → clamp up, log "underground".
3. `alt ∈ [terrainAlt, target)` → **leave unchanged**, commented "already in a reasonable range".

The `(2)` and `(3)` branches share the same underlying problem. The "reasonable range" assumption held only if the root part was the lowest point of the vessel, which is false for anything with a command pod on top. All three landed leaves in the playtest fell in the no-op branch. **Fix closes the gap**: any `alt < target` is now clamped up, with log reason `underground` if also below terrain or `low-clearance` if in the gap.

**Site 2 — `ParsekFlight.IGhostPositioner.PositionAtPoint`.** Previously called `PositionGhostAt(state.ghost, point)` directly with the raw recorded altitude. The existing `ClampGhostsToTerrain` pass in `LateUpdate` uses `TerrainCorrector.ClampAltitude(alt, terrain, minClearance = 0.5)` — far too tight for a Mk1-3 pod. **Fix** adds a new internal static helper `ApplyLandedGhostClearance(point, index, vesselName)` that runs only for `traj.TerminalStateValue ∈ {Landed, Splashed}`, looks up `body.TerrainAltitude(lat, lon, true)`, and bumps the point altitude to `max(point.altitude, terrainAlt + LandedGhostClearanceMeters)` before calling `PositionGhostAt`.

**Scope of the playback clamp:** `IGhostPositioner.PositionAtPoint` is called from four sites in `GhostPlaybackEngine` — `HandlePastEndGhost`, `UpdateOverlapPlayback` (loop-cycle boundary), `UpdateExpireAndPositionOverlaps` (overlap expired), and `HandleLoopPauseWindow` (loop pause hold). All four are "position at the final point / crash-site hold" paths. **Normal in-flight playback** goes through `InterpolateAndPosition` → `GetWorldSurfacePosition` and is not currently clamped — it still relies on the existing `ClampGhostsToTerrain` LateUpdate pass with its 0.5 m floor, which is insufficient for tall vessels. The playtest report was about past-end landed ghosts (the `HandleLoopPauseWindow` path), so this fix covers the reported symptom; mid-playback clamping for rovers/walking kerbals is a deliberate follow-up (see below).

**Split constants.** `VesselSpawner.LandedClearanceMeters = 2.0` stays (bug #231's physics-drop concern: dropping a vessel from 5 m broke parts under KSP's physics; 2 m was chosen as the compromise). New `VesselSpawner.LandedGhostClearanceMeters = 4.0` covers ghost playback, which is kinematic (no physics drop damage). 4 m is a pragmatic default: for Mk1-3 it leaves 4.0 − 1.77 = **2.23 m** of visible margin — modest but strictly above terrain. Taller stacks may still sit slightly low, but the alternative (burying the mesh) is worse.

**`state.lastInterpolatedAltitude`** is updated post-clamp so the watch-mode overlay at `WatchModeController.cs:347` reports the corrected altitude instead of the raw underground number, keeping the diagnostic log consistent with the visual.

**Tests:**
- `Source/Parsek.Tests/SpawnSafetyNetTests.cs` — extended `ClampAltitudeForLanded_*` tests with the exact playtest numbers (176.5/175.6/0.9 → 177.6), a `_LowClearanceGap_ClampsUp_Bug282` regression, a `_JustAboveTerrainAtOneCm_ClampsUp` boundary test, and a `LandedGhostClearanceMeters_IsLargerThanSpawnClearance_Bug282` pin on the split constants.
- `Source/Parsek/InGameTests/RuntimeTests.cs` — `LandedGhostClearance_BuriedPoint_ClampsAboveTerrain` spawns a synthetic buried point at the active vessel's lat/lon (terrain + 0.9 m) and asserts `ApplyLandedGhostClearance` lifts it to ≥ `terrain + LandedGhostClearanceMeters`. Companion `LandedGhostClearance_AlreadyClear_Unchanged` verifies the no-op path.

**Follow-ups tracked separately** (see "Scope deliberately NOT in this PR" in `logs/2026-04-09_ghosts-underground/fix-plan.md`):
- **Mid-playback terrain clamp** for rovers and walking kerbals whose recording has a Landed/Splashed terminal state. The architectural fix is to thread terminal state (or a pre-computed clearance value) into `ParsekFlight.ClampGhostsToTerrain` via a new `double minClearanceMeters` field on `GhostPosEntry`, populated at every `ghostPosEntries.Add` site where the trajectory is known to be landed. That single change would cover `InterpolateAndPosition`, `PositionGhostAt`, `PositionGhostAtSurface`, and any future positioning helper through the existing LateUpdate pass. Deferred out of #282 to keep the fix surgical, but the next playtest that shows a buried mid-drive rover should bump this up.
- Per-vessel `rootPartBottomOffset` captured at `FlightRecorder.StartRecording` time, used to derive a vessel-accurate clearance instead of the 4 m constant. Would give Mk1-3 a 0.5 m margin instead of 2.23 m and would cover tall rocket stacks that still sit low under the constant.
- `TerrainCorrector.ComputeCorrectedAltitude` wired into runtime playback (currently only hits `VesselGhoster` at scene-load). Needed when session terrain differs from recorded terrain (PQS regeneration across KSP versions).
- EVA kerbal clearance: the 4 m ghost clamp lifts a walking kerbal ~3.5 m above terrain (visually floating). Cosmetic, defer to the per-vessel offset work.

---

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

## ~~279. Watch button unavailable from F5 moment onwards (2026-04-09 playtest)~~

Reported alongside bugs #276/#277/#278 in the 2026-04-09 playtest session (`logs/2026-04-09_recording-flow-bugs/`). After F5 at UT 369.2, the Watch button for the Kerbal X tree and its children was effectively unusable for the rest of the flight — the user couldn't preview any of the just-recorded flight data.

**Diagnosis:** confirmed downstream of bug #278. With every Kerbal X Debris leaf showing `terminal=Destroyed canPersist=False` after the limbo finalize blanket-stamp, ghost building was skipped for the whole tree, `WatchModeController.HasActiveGhost(idx)` returned false for the main Kerbal X recording (which is what `FindGroupMainRecordingIndex` selects for the group W button), and the per-row buttons had no ghosts to follow either. Not a UI bug — a downstream symptom of the limbo-finalize stamping.

**Fix:** No standalone fix beyond bug #278's PR #176 (which stops the blanket-Destroyed stamp so leaves keep their real terminal state and `canPersist=True`). The Watch button gating predicates at `RecordingsTableUI.cs:769-800` (per-row) and `RecordingsTableUI.cs:1127-1157` (group) were audited and found to already cover all four disabled branches (`IsDebris`, `!hasGhost`, `!sameBody`, `!inRange`) with explicit tooltips after the bug #275 pass.

**Observability follow-through (PR #177):** Added INFO-level logging when the Watch button enabled state flips, keyed by recording index (per-row) or `groupName/mainIdx` (group). The previous diagnostic gap — only two `[VERBOSE][UI]` disabled-state lines in the whole 2026-04-09 playtest log, both irrelevant to the Watch button — is closed. Future playtests can distinguish "user didn't try" from "UI was broken" from a single grep on the `[INFO][UI] Watch button` lines.

```
RecordingsTableUI.cs lastCanWatchByIndex / lastCanWatchByGroup dictionaries
mirror the existing lastCanFF / lastCanRewind transition-logging pattern.
ParsekLog.Info on transition; no log on stable state (handles OnGUI's
multi-event-per-frame firing without spam).
```

**Status:** Fixed.

---

## ~~278. Capsule not spawned at end of recording (2026-04-09 playtest)~~

After the Kerbal X flight in the 2026-04-09 playtest, no vessel was spawned at end-of-recording for the user to continue playing with. The Bob Kerman EVA recording is the only one that *could* have spawned (it had a snapshot), but it terminated `Destroyed` with `canPersist=False` — see `KSP.log:11548`.

**Root cause** (re-diagnosed against current main, not the bug-as-filed): `ParsekScenario.FinalizePendingLimboTreeForRevert` (the OnLoad handler that finalizes a Limbo tree on revert OR vessel switch) was blanket-stamping every leaf without an existing `TerminalStateValue` as `Destroyed`. The original limbo dispatch comment says terminal state is "deferred to OnLoad's dispatch once it knows whether this is a revert or a quickload", but the deferred handler shortcut to Destroyed instead of doing the situation-aware work the live commit path does. An EVA kerbal walking on the surface (`v.situation = LANDED`) at the moment of the switch got stamped Destroyed even though the vessel was still loaded in `FlightGlobals`. Confirmed in both the 17:42 pre-#280 log (Bob Kerman, L11548) and the 20:52 post-#280 log (every leaf is `terminal=Destroyed canPersist=False`, snapshots preserved by #280's fix but still unspawnable). The bug-as-filed thought this was about snapshot loss; that part is bug #280's territory and was fixed in PR #167. The remaining damage is the blanket-Destroyed stamp.

**Fix (PR #176):** `FinalizePendingLimboTreeForRevert` now routes through `ParsekFlight.FinalizeIndividualRecording` per leaf (the same helper `FinalizeTreeRecordings` uses on the live commit path), then `EnsureActiveRecordingTerminalState` for the active non-leaf case, then `PruneZeroPointLeaves` to drop empty placeholders. `FinalizeIndividualRecording` looks up each leaf's vessel via `FlightRecorder.FindVesselByPid`; if alive in `FlightGlobals`, it uses `RecordingTree.DetermineTerminalState((int)v.situation, v)` + `CaptureTerminalOrbit` + `CaptureTerminalPosition`. If gone, it falls back to `Destroyed` + `PopulateTerminalOrbitFromLastSegment` (preserving the previous behavior for the vessel-gone case). Surviving leaves keep their real situation and remain `canPersist=True`. Promoted three helpers from `private` to `internal static`: `FinalizeIndividualRecording`, `EnsureActiveRecordingTerminalState`, `PruneZeroPointLeaves`. Replaced one `Log(...)` instance call with `ParsekLog.Verbose("Flight", ...)` so the method body is static-clean.

**Side effect (intentional, consistency win):** the per-recording finalize now also runs `ExplicitStartUT`/`ExplicitEndUT` bookkeeping on every recording in the tree, including non-leaves. The previous limbo finalize skipped non-leaves entirely. The live commit path (`FinalizeTreeRecordings`) was already doing this — the limbo path was the inconsistent one, so widening it brings the two paths in line. Logged here so future-you (or future-reviewer) doesn't flag it as an unintentional behavior change.

**Tests:** 8 new in `Bug278FinalizeLimboTests.cs` covering the vessel-gone fallback, the existing-state preservation, the non-leaf skip, the explicit-UT bookkeeping, the active-recording-already-terminal short-circuit, the no-active-id no-op, and `PruneZeroPointLeaves` removing empty placeholders. The vessel-found-live branch needs a live KSP runtime (covered by the next in-game playtest).

**Follow-ups (PR #177):** Two follow-up changes shipped in the next PR alongside the #279 logging:

1. **`RecordingStore.SaveRecordingFiles` destructive-delete fix (real regression caught by PR #176).** PR #176's per-leaf `FinalizeIndividualRecording` route reaches the vessel-gone branch at `ParsekFlight.cs:6137` ("rec.VesselSnapshot = null") for any leaf whose vessel is gone at limbo finalize time. The dispatch flow then runs `CommitPendingTree → CommitTree → FinalizeTreeCommit`, which sets `rec.FilesDirty = true` for every recording (`RecordingStore.cs:503`) and calls `FlushDirtyFiles → SaveRecordingFiles` immediately. Without the follow-up fix, `SaveRecordingFiles` saw the just-nulled `VesselSnapshot`, took the destructive-delete branch, and **wiped the on-disk `_vessel.craft` that #280's PR #167 had previously persisted via `OnBackgroundVesselWillDestroy`**. Net effect: PR #176 inadvertently re-introduced the snapshot loss for the vessel-gone-at-limbo case. PR #177 removes the destructive-delete branch — stale-cleanup is the responsibility of explicit deletion paths (`DeleteRecordingFiles`), not of every save. Pinned by `Bug278SnapshotPersistenceTests.DestructiveDelete_RegressionChain_IsReachable_DocumentedBySourceInspection`.

2. **`BackgroundRecorder.EndDebrisRecording` persists at finalization (defense-in-depth).** The #280 fix in PR #167 wired `PersistFinalizedRecording` into `OnBackgroundVesselWillDestroy` and `Shutdown` but not into `EndDebrisRecording`, the `CheckDebrisTTL` termination site for both the TTL-expired branch (vessel still alive after 60 s) and the out-of-bubble branch (`!v.loaded`). PR #177 mirrors the call so all three finalization sites match. Not a root cause of #278 (those debris went through `OnBackgroundVesselWillDestroy`), but closes a real coverage gap that would surface as data loss for long-lived debris that hasn't crashed by the time the player triggers a scene reload.

**Status:** Fixed.

---

## ~~277. Wrong crew spawned at recording end (2026-04-09 playtest)~~

Reported in the 2026-04-09 playtest (`logs/2026-04-09_recording-flow-bugs/`). The Kerbal X rocket crew was Jeb (Pilot) / Bill (Engineer) / Bob (Scientist), but at merge-dialog time only two of the three stand-in swaps succeeded.

**Smoking gun at `KSP.log:12836-12840`:**
```
12836: [CrewReservation] Swapped 'Jebediah Kerman' → 'Zelsted Kerman'  in part 'Mk1-3 Command Pod'
12837: [CrewReservation] Swapped 'Bill Kerman'     → 'Siford Kerman'   in part 'Mk1-3 Command Pod'
12838: [CrewReservation] Crew swap complete: 2 succeeded — refreshed vessel crew display
12839: [CrewReservation] Removing reserved EVA vessel 'Bob Kerman' (pid=1857874769)
12840: [CrewReservation] Removed 1 reserved EVA vessel(s)
```

Only 2 swaps completed. The Bob→Carsy swap never ran because Bob was on an EVA vessel (rec `d768a28f`) at the moment of merge rather than in the command pod. The code took the "Removing reserved EVA vessel" branch and kicked Carsy out of the reservation pool without ever placing her in any vessel. Net result: the command pod ends up with `Zelsted + Siford + (empty Bob seat)`, Carsy floats unused in the roster, and `RescueReservedCrewAfterEvaRemoval` flips Bob back from Missing to Available — so the original Bob is also still usable, doubling the wrongness. The merge dialog reports `spawnable=0` (see also bug #278) so the user never sees the resulting crew assignment in-flight, but the roster is still wrong.

**Long-standing latent footprint.** The diagnostic line `"Crew swap on flight ready: 0 swapped (N reservations exist but no matches on active vessel)"` (logged from `ParsekFlight.cs:4082`) appears in **at least 8 older session logs** going back to 2026-03-22 — sessions 2/3/4/5, the 2026-03-24 rewind bug log, the 2026-03-14 EVA dupe log. This is a several-week-old latent bug where stand-ins are silently misplaced; the 2026-04-09 playtest is the first session where the user-visible symptom (wrong crew in pod) was clearly traced.

**Root cause.** `CrewReservationManager.SwapReservedCrewInFlight` (`Source/Parsek/CrewReservationManager.cs:151`) only iterates `FlightGlobals.ActiveVessel.parts → protoModuleCrew`. The first-pass loop can swap a reservation only when the original kerbal is currently seated in a part of the active vessel. When the original is on a separate EVA vessel (a pre-revert artifact kept alive by Parsek's tree-finalization path), the loop never sees them, the reservation goes unhandled, and `RemoveReservedEvaVessels` (called at the end of the same method) then deletes the EVA vessel without ever placing the stand-in anywhere.

**Fix.** Add an orphan-placement second pass to `SwapReservedCrewInFlight`:

1. **First pass tracks swapped originals** in a new local `HashSet<string> swappedOriginals` instead of just counting.
2. **`PlaceOrphanedReplacements`** iterates `crewReplacements`. For each entry whose original is not in `swappedOriginals`:
   - **Defensive guard**: short-circuit if the original is still seated on the active vessel (Pass 1 may have left them unprocessed via a `failCount` branch like replacement-not-in-roster). Prevents double-placement.
   - Skip if the replacement is not in the roster (Warn) or is Dead (Warn).
   - **Rescue Missing replacements** by setting back to Available before placement, mirroring the existing `ReserveCrewIn` Missing-rescue pattern.
   - Skip if the replacement is already on the active vessel (Info).
   - Call new pure helper `ResolveOrphanSeatFromSnapshots(originalName, snapshots, reverseMap)` which scans `RecordingStore.CommittedRecordings` for any `GhostVisualSnapshot` PART node that lists the original in its `crew` values. Returns `(PartPid, PartName)`.
     - `GhostVisualSnapshot` is used (not `VesselSnapshot`) because it's captured at recording start and contains crew who later EVA'd; `VesselSnapshot` is end-of-recording and would not contain EVA'd crew.
     - Reverse-stand-in mapping handles snapshots from later recordings whose crew lists already contain stand-in names from earlier ones (mirrors `KerbalsModule.ReverseMapCrewNames`).
     - Match key is the kerbal name itself. `VesselName` comparison is intentionally NOT used because two launches can share a vessel name and would falsely cross-match.
   - `FindTargetPartForOrphan(pid, name)` walks `ActiveVessel.parts` with a **strict two-tier match** (PR #175 review): prefer `Part.persistentId == snapshotPartPid` (most reliable for post-revert vessels), then fall back to the first part with matching `partInfo.name` and free capacity. There is intentionally **no "any free seat" tier 3** — a misplaced stand-in (e.g. dropped into a passenger cabin instead of the command pod) is worse than an unplaced one and would silently mask the bug being fixed.
   - Place via `Part.AddCrewmember(replacement)` (the non-indexed overload — KSP picks a free seat in the part). Avoids the unverified `AddCrewmemberAt`-on-empty-seat semantic.
   - Add the original to `swappedOriginals` so `RemoveReservedEvaVessels` proceeds cleanly afterwards.
3. **Single `SpawnCrew` / `onVesselCrewWasModified` firing** at the end if the combined swap count > 0 (was previously only the first-pass count).
4. **Distinct skip/fail counters** in the aggregate summary log (PR #175 review): `rescuedFromMissing`, `skippedReplacementNotInRoster`, `skippedDeadOrMissingReplacement`, `skippedAlreadyOnActiveVessel`, `skippedOriginalStillOnActiveVessel`, `skippedSnapshotMiss`, `skippedNoMatchingPart`. Infrastructural failures use `Warn`; expected-skip cases (already-on-vessel, original-still-seated) use `Info`.
5. **Up-front active-vessel crew name set** built once per swap (PR #175 review): O(parts × crew) build then O(1) lookups in the orphan loop, replacing the previous O(parts × crew) per orphan in `IsReplacementOnActiveVessel`. The local set is updated as placements happen so a subsequent orphan that maps to the same kerbal doesn't false-collide.

Seat resolution lives at swap time rather than capturing it earlier in `SetReplacement` because `SetReplacement` runs on every commit/recalculate cycle (hot path) and would pay the snapshot-walk cost even when no orphan exists. The orphan pass only runs when the swap actually fails to place every replacement.

**Tests** (`Source/Parsek.Tests/Bug277OrphanCrewPlacementTests.cs`, 16 cases): pure-helper coverage of `ResolveOrphanSeatFromSnapshots` — single-part match, multi-part match (returns the part containing the original), multi-snapshot first-wins, original not in any snapshot returns NotFound, null original / null enumerable / null snapshot in list, parts with no `crew` values are skipped, missing `pid` returns 0, missing `name` returns "", reverse-map lookup catches stand-ins in later recordings, reverse-map missing entry doesn't false-match, regression case using exact 2026-04-09 Kerbal X scenario.

In-game tests (`Source/Parsek/InGameTests/RuntimeTests.cs`):
- `Bug277_AddCrewmemberOnFreeSeat_Works` — validates the live `Part.AddCrewmember` API path on a free seat against the active vessel and rolls back.
- `Bug277_PlaceOrphanedReplacements_PlacesStandinFromSnapshot` — full end-to-end integration test (PR #175 review). Builds a synthetic snapshot referencing a real part on the active vessel, registers a fake reservation, calls `PlaceOrphanedReplacements` directly (skipping the surrounding `SpawnCrew` + `RemoveReservedEvaVessels` side effects), asserts the stand-in landed in the right part, then rolls everything back. `PlaceOrphanedReplacements` is `internal` to support this.

**Status:** Fixed.

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
