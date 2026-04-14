# Fix Plan: #316 Breakup Debris Ghosts Spawn Into Beyond

Status: implemented 2026-04-13 after log/code investigation, targeted regression coverage, bounded post-watch lineage-protection fix, and clean-review follow-up fixes for automatic-exit coverage and failed watch-start retention.

## Background

Open bug:

- `#316` — Breakup debris ghosts can spawn directly into `Beyond` and never become visible during playback.

Archived evidence is in `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`.

## Investigation Summary

### What the archived playtest actually ran

- Archived `git-state.txt` shows the playtest was run on branch `fix/phase-11-5-recording-storage` at commit `0670a18` on 2026-04-12.
- Current investigation branch is based on `HEAD` `ba5fb89`, which already contains commit `c64a9a4` (`Follow watched debris ancestry beyond loop sync`, dated 2026-04-12).

### What happened in the log

The failure sequence in `Player.log` is:

- Watched segment `#2 "Kerbal X"` completes and enters watch hold.
- While watch hold for `#2` is still active, debris ghost `#10` spawns.
- `#10` immediately transitions `Physics->Beyond` at `dist=943546m` and is hidden.
- Watch hold then expires and watch mode exits.
- Later, debris ghost `#11` spawns after watch mode has already exited.
- `#11` also immediately transitions `Physics->Beyond` at `dist=952154m` and is hidden.

Relevant lines:

- `177179-177188`: watched `#2` completes and watch hold starts.
- `177233-177240`: debris `#10` spawns, enters `Beyond`, and is hidden.
- `177248-177257`: watch hold expires and watch mode exits.
- `177975-177982`: debris `#11` spawns, enters `Beyond`, and is hidden.

### Reconstructed topology

From the archived `persistent.sfs` plus the optimizer split order in the log, the effective committed-recording order at playback time is:

1. `#0` `8582d3de...` — `Kerbal X`, chain index 0
2. `#1` `06efb0cf...` — `Kerbal X`, chain index 1
3. `#2` `707490bb...` — `Kerbal X`, chain index 2, watched segment
4. `#10` `aea21e7f...` — `Kerbal X Debris`, `parentBranchPointId=6f18efbf...`
5. `#11` `62e4c901...` — `Kerbal X Debris`, `parentBranchPointId=9e81d9a9...`

Both failing debris recordings are in the same tree as the watched vessel (`treeId=33dada3c3b814c23a1fddc5eb7b3fa86`) and both branch from the watched vessel's ancestry through breakup branch points.

### Root-cause diagnosis

The archived build (`0670a18`) predates both later watch-debris commits:

- `9ec8509` — `Keep watched ghost debris visible beyond distance LOD`
- `c64a9a4` — `Follow watched debris ancestry beyond loop sync`

So `0670a18` only protected the exact watched ghost, not watched-lineage debris.

That splits `#316` into two related but different failures:

1. `#10` — missing watched-lineage protection while watch is still active.
   `#10` branches from the watched vessel's tree ancestry and should have been eligible for watched-lineage visibility, but the archived build had no such fallback. The archived split timings also leave it without a direct `LoopSyncParentIdx`, so ancestry fallback is the relevant current-`HEAD` protection path.

2. `#11` — watch lifetime ends before later debris playback begins.
   `#11` does not spawn until after `ExitWatchMode()` clears `watchedRecordingIndex`, so branch-ancestry protection alone cannot explain or fix it. This is a watch-follow / watch-lifetime policy question, not only an ancestry question.

In both cases the ghosts did spawn correctly. The failure was visibility policy, not recording loss or ghost construction failure.

## Current Code Status

Current `HEAD` contains two relevant post-log fixes:

- `Source/Parsek/GhostPlaybackLogic.cs`
  - watched-lineage debris protection exists and now falls back from loop-sync checks to recursive same-tree branch ancestry (`ParentBranchPointId` -> `BranchPoint.ParentRecordingIds`).
- `Source/Parsek.Tests/ZoneRenderingTests.cs`
  - includes coverage for watched-lineage debris and recursive debris descendants.
- `CHANGELOG.md`
  - records both watch-debris visibility work items.

Focused verification on current `HEAD`:

- `dotnet test Source\\Parsek.Tests\\Parsek.Tests.csproj --filter ZoneRenderingTests`
- Result: `54 passed, 0 failed`

Additional headless characterization on 2026-04-13 before the final fix:

- `dotnet test Source\\Parsek.Tests\\Parsek.Tests.csproj --filter "FullyQualifiedName~Issue316"`
- Result before review follow-up: `3 passed, 0 failed`
- `Issue316_PopulateLoopSync_LateDebrisAfterWatchedSegment_GetsMinusOne`
  proves the archived `#10` / `#11` timings leave both debris recordings without a direct `LoopSyncParentIdx` after final chain splitting.
- `Issue316_IsWatchProtectedRecording_ArchivedSplitLineageFallsBackToBranchAncestry`
  proves current `HEAD`'s recursive same-tree ancestry fallback would still classify both debris recordings as watch-protected while watch remains active, even with `LoopSyncParentIdx == -1`.
- `Issue316_ArchivedSameTreeDebrisOutsideWatchedBranch_IsNotAutoFollowTarget`
  proves current watch auto-follow still ignores active same-tree debris that is not a child of the watched segment's own `ChildBranchPointId`.

Review follow-up on the same day tightened the implementation and added shared headless coverage around the extracted exit/zone/start seams:

- `dotnet test Source\\Parsek.Tests\\Parsek.Tests.csproj --filter "FullyQualifiedName~Issue316"`
- Result after review follow-up: `8 passed, 0 failed`
- `Issue316_FinalizeAutomaticExitForTesting_RetainsLineageProtectionWithoutSpam`
  proves the shared automatic-exit state transition retains the watched-lineage root once and does not spam the retention log on repeated resolution.
- `Issue316_ResolveZoneWatchState_RetainedProtectionKeepsLateDebrisFullFidelity`
  proves the zone-rendering path consumes `WatchProtectionRecordingIndex` from retained post-watch state and keeps late same-lineage debris on the full-fidelity path even in `Beyond`.
- `Issue316_TryCommitWatchSessionStart_NullLoadedState_PreservesExistingLineageProtection`
  proves a failed replacement watch start does not clear an already-retained protection window unless a new watch state is actually committed.

What is still unproven:

- whether current `HEAD` fixes archived `#10` in the real playback path
- whether current `HEAD` does anything for archived `#11`, which spawns after watch has already exited

## Plan

### Plan A — reproduce the archived scenario on current `HEAD` with diagnostics

1. Re-run the archived `2026-04-12` save/log scenario on current `HEAD`.
2. Add temporary diagnostics for the exact archived actors:
   - final committed index -> `recordingId`
   - `StartUT` / `EndUT`
   - `LoopSyncParentIdx`
   - `protectedIndex`
   - `IsWatchProtectedRecording(...)`
3. Use that repro to classify the outcomes separately:
   - `#10`: is it now protected while watch is still active?
   - `#11`: does it still hide because watch mode has already exited?
4. Only after that, decide whether `#316` is fully fixed, partially fixed, or still open.

### Plan B — keep regression coverage anchored to the archived topology

Current headless coverage now includes:

1. A `RecordingStore` / optimizer test proving the archived split shape leaves late debris without a direct loop-sync parent.
2. A watch-protection test proving current `HEAD` falls back from missing loop-sync to same-tree branch ancestry for the archived debris lineage.
3. A watch-target characterization test proving current auto-follow does not retarget to active same-tree debris outside the watched segment's own `ChildBranchPointId`.

Still missing:

4. A stateful watch/zone test defining expected behavior for post-watch debris.
   The test must explicitly answer: if watched-lineage debris spawns after watch hold expires, should it still be forced visible, should watch auto-follow it, or is hiding acceptable?

Keep the existing pure `IsWatchProtectedRecording(...)` unit coverage, but do not treat helper-only coverage as sufficient for `#316`.

### Plan C — if `#10` is fixed but `#11` still reproduces, change watch-lifetime policy

If repro shows current `HEAD` fixes `#10` but not `#11`, the remaining implementation work should target watch lifetime rather than ancestry:

1. Revisit `GhostPlaybackLogic.FindNextWatchTarget(...)` and the current `pidMatchFound -> return -1` behavior.
2. Decide whether watch mode should temporarily fall back to an active same-tree debris child when there is no active same-PID continuation ghost.
3. Prefer a target-selection change over simply lengthening the fixed watch-hold timer, since archived `#11` begins far later than the current `5s` hold window.

### Plan D — docs closure only if scenario repro is clean

If current `HEAD` shows both archived cases are acceptable under the intended design, then:

1. update `docs/dev/todo-and-known-bugs.md`
2. note which post-`0670a18` commits closed the behavior in practice
3. keep the new regression coverage so the scenario does not silently regress

## Recommended Next Action

Implementation and headless regression coverage are complete. The only remaining optional confidence step is a live KSP playback pass on the archived save to confirm the reviewed behavior against the original playtest, but there is no remaining headless evidence gap blocking closure of `#316`.
