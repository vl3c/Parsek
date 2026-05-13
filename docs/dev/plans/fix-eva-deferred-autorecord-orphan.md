# Fix: Second EVA from same capsule produces an orphan recording that is silently dropped

**Branch:** `plan-eva-deferred-autorecord-orphan`
**Worktree:** `C:/Users/vlad3/Documents/Code/Parsek/Parsek-plan-eva-deferred-autorecord-orphan`
**Companion fix (separate PR):** `plan-eva-nullsolver-subsurface` — independent bug in the same flight, do not bundle.

## Problem

Reproducer (observed in `logs/2026-05-13_2337_eva-kerbals-missing/`):

1. Launch `Kerbal X` (3-crew capsule). Recording starts: `bf8f6c68|Kerbal X|tree`, treeId `4e1d9d29`.
2. EVA Bill from the capsule. `OnCrewOnEva` takes the mid-recording branch path (`ParsekFlight.cs:6363`), creates branch point `f6d1a26a` with `activeChild=6952389d (capsule continuation, pid 2708531065)` and `bgChild=4ba22dea (Bill, pid 1604830044)`. Correct.
3. KSP auto-switches focus to Bill. `HandleTreeBackgroundFlush` (`ParsekFlight.cs:3319`) parks `6952389d` into background and clears `activeTree.ActiveRecordingId = null` at line 3337.
4. Bill plants a flag and the user switches back to the capsule. `OnVesselSwitchComplete` arms post-switch auto-record (`tracked=True`, reason `vessel switch to tracked background member while idle`).
5. The post-switch first-modification trigger (`OnPostSwitchAutoRecordPhysicsFrame`) never fires during the ~20s the user is back inside the capsule — the capsule is settled and motionless, so neither the orbit-change nor landed-motion detector trips.
6. EVA Bob from the capsule. `OnCrewOnEva` finds `recorder == null` so the source-vessel guard at `ParsekFlight.cs:6333` skips the mid-recording branch path; the auto-record fallback sets `pendingAutoRecord = true` and logs `EVA detected (sit=LANDED) — pending auto-record` (`ParsekFlight.cs:6389`).
7. `HandleDeferredAutoRecordEva` (`ParsekFlight.cs:9646`) calls `StartRecording(suppressStartScreenMessage: true)`.
8. `StartRecording` (`ParsekFlight.cs:9709`) sees `activeTree != null` so it skips the single-node-tree creation block, binds `recorder.ActiveTree = activeTree`, and starts the recorder. But it never assigns `activeTree.ActiveRecordingId` and never adds a `Recording` entry for Bob to the tree.
9. Log shows `Recording started: vessel="Bob Kerman", parts=1, points=0, treeRec=-` — the `treeRec=-` is the smoking gun.
10. Bob walks, plants flag `b`. Recorder captures 38 trajectory points + 1 flag event over 18.7s.
11. Scene exit: `FinalizeTreeOnSceneChange` flushes the recorder. `FlushRecorderToTreeRecording` (`ParsekFlight.cs:3105`) hits the `tree.ActiveRecordingId == null` branch at line 3110-3114, logs `WARN [Flight] FlushRecorderToTreeRecording: no active recording id in tree`, sets `rec.IsRecording = false`, and **returns without appending any data to any recording**.
12. `CommitTree` enumerates 11 children for tree `4e1d9d29` (Kerbal X launch + 7 debris + 1 probe + capsule continuation + Bill). No entry for Bob. No `.prec` or `_vessel.craft` for pid `599175061` lands on disk.

The user sees one EVA kerbal (Bill, the branch worked) and zero record of Bob's EVA or his flag.

The same shape can repeat any time the user EVAs after a previous EVA in the same flight without the post-switch trigger having armed an active recording on the capsule first. Single-EVA flights are unaffected.

## Root cause

`HandleDeferredAutoRecordEva` and the underlying `StartRecording` were built for the standalone path: caller hands in an active vessel, recording starts. With always-tree mode (`#271`), `StartRecording` was retrofitted to wrap a new single-node tree only when `activeTree == null`. The blind spot is the third state:

- `activeTree != null` **and** `activeTree.ActiveRecordingId == null` (active tree exists but has no current head — the previous head was parked to background by a prior branch flush).

In that state `StartRecording` does neither: it neither creates a new tree, nor adds a `Recording` entry for the new vessel to the existing tree, nor sets `activeTree.ActiveRecordingId` to the new recording. The recorder runs visibly attached to the tree (`recorder.ActiveTree = activeTree`) but the tree has no slot to flush into.

`FlushRecorderToTreeRecording` then silently drops the data, with only a `WARN` to mark the loss. The drop is symptomatic — the actual mistake is upstream.

## Goal

When `OnCrewOnEva` fires from a vessel whose recording lives in `activeTree.BackgroundMap`, treat it the same way as a `Mid-recording EVA detected` would have if the parent recorder had been live — produce an `EVA` branch point from the parent's recording with `activeChild` = a fresh tree recording for the new EVA kerbal, `bgChild` = the parent capsule's existing background recording.

When the EVA's source capsule is NOT tracked by any tree (true "pad EVA from a vessel Parsek does not know"), preserve today's behavior: start a fresh single-node tree.

Never start a recorder bound to an `activeTree` that has no `ActiveRecordingId` and no plan to create one.

## Invariant to enforce

`recorder.ActiveTree == activeTree` implies `activeTree.ActiveRecordingId != null` AND `activeTree.Recordings.ContainsKey(activeTree.ActiveRecordingId)`. Enforce at recorder start time, not at flush time.

## Proposed implementation

### 1. Detect the tracked-parent case in `OnCrewOnEva`

In `ParsekFlight.cs:6332-6342`, the `source-vessel-mismatch` branch currently returns immediately when `data.from.vessel.persistentId != recorder.RecordingVesselId`. Before that early return, when `recorder == null` (no live recording at all), check whether the source vessel is tracked in `activeTree.BackgroundMap`. If yes, route to a new branch entry point:

```
if (recorder == null
    && activeTree != null
    && data.from?.vessel != null
    && activeTree.BackgroundMap.TryGetValue(
           data.from.vessel.persistentId, out string parentBgRecordingId))
{
    // Promote the parent capsule's background recording, then take the
    // mid-recording EVA branch path. The next OnCrewOnEva tick (or the
    // same one after promotion) creates the branch with parentBgRecordingId
    // as the bgChild's source.
    PromoteForEvaBranch(parentBgRecordingId, data.from.vessel, kerbalName, data);
    return;
}
```

The simplest implementation reuses the existing branch-creation path. Two viable shapes — both should be evaluated by reviewers:

**Option A (preferred): promote-then-branch.** Promote the parent capsule's background recording to active via `PromoteRecordingFromBackground` first (gives us a live recorder on the parent), then synthesize the same `Mid-recording EVA detected` path that worked for Bill at step 2. Re-runs the existing tested code, no parallel branch-creation logic.

**Option B: synthesize a branch without promoting.** Build the `BranchPoint` and `Recording` entries directly from the background `bgChild` (parent capsule) without re-promoting it to a live recorder. Avoids briefly starting a recorder on the capsule that we immediately stop. More code, more new edge cases.

Both produce the same on-disk tree shape. Option A is the safer first cut.

### 2. Guard `StartRecording` against the orphan state

Add a precondition at the top of `StartRecording` (`ParsekFlight.cs:9709`):

```
if (activeTree != null
    && string.IsNullOrEmpty(activeTree.ActiveRecordingId)
    && !isContinuation)
{
    ParsekLog.Warn("Flight",
        $"StartRecording: refusing to start with active tree '{activeTree.Id}' " +
        "that has no ActiveRecordingId — would produce an orphan recording. " +
        "Callers must promote a background recording or create a branch first.");
    return;
}
```

This is a belt-and-braces guard. The real fix is upstream (step 1), but the guard makes the failure mode loud — the recorder simply doesn't start and the user sees no toast, instead of starting silently and discarding data 20s later.

Reviewer question: is there any legitimate caller that wants `recorder` running while the tree has no head? Searched call sites suggest no; all branch / promote paths assign `ActiveRecordingId` before or during the path. If any caller depends on the old behavior we either teach it to assign first or convert the guard to a `Debug.Assert` style fail-loud-in-Editor.

### 3. Surface the silent-drop site

`FlushRecorderToTreeRecording` at `ParsekFlight.cs:3110-3114` currently logs `WARN` and drops. With (1) and (2) in place this branch is unreachable, but keep the log and convert the drop into a `Debug.LogError` (or stronger `WARN` with point/event counts) so a future regression is impossible to miss:

```
ParsekLog.Warn("Flight",
    $"FlushRecorderToTreeRecording: no active recording id in tree '{tree.Id}' — " +
    $"DROPPING {rec.Recording?.Count ?? 0} points, " +
    $"{rec.PartEvents?.Count ?? 0} part events, " +
    $"{rec.FlagEvents?.Count ?? 0} flag events. This is a bug; report to maintainers.");
```

### 4. Tests

xUnit (`Source/Parsek.Tests/`):

- `EvaBranchFromBackgroundParentTests.cs` — pure-static decision test for the new routing logic in `OnCrewOnEva`. Cases:
  - source vessel tracked in `BackgroundMap` + no live recorder → routes to branch path
  - source vessel not in `BackgroundMap` + no live recorder → routes to fresh auto-record (unchanged)
  - source vessel tracked + live recorder on a different pid → preserves existing source-vessel-mismatch skip
  - source vessel == recorder.RecordingVesselId → preserves existing mid-recording branch path (regression guard for Bill's case)

- `StartRecordingTreeHeadGuardTests.cs` — assert `StartRecording` is a no-op when called with `activeTree != null && activeTree.ActiveRecordingId == null && !isContinuation`. Use the test sink to assert the new warn line fires.

- `FlushRecorderToTreeRecordingDropDiagnosticsTests.cs` — call `FlushRecorderToTreeRecording` with a tree that has `ActiveRecordingId == null` and a recorder holding fake points + part events + flag events. Assert the warn message reports the dropped counts. Regression test only — should be unreachable post-fix but documents the contract.

In-game runtime test (`Source/Parsek/InGameTests/RuntimeTests.cs`, scene `FLIGHT`):

- `EvaTwiceFromSameCapsuleProducesTwoBranches` — load a Kerbal X-style 3-crew vessel on the pad, drive EVA-A, return-to-capsule, EVA-B, scene-exit. Assert:
  - tree has 2 EVA branch points
  - both EVA child recordings appear in `CommitTree` output with non-empty point lists
  - both EVA kerbals have committed `.prec` + `_vessel.craft` files in `saves/<save>/Parsek/Recordings/`

This is the only path that exercises the OnVesselSwitchComplete -> background-flush -> back-to-capsule -> next-EVA chain end-to-end with real KSP events. xUnit can't replicate KSP's vessel-switch timing.

### 5. Logging

`PromoteForEvaBranch` (new) should emit the same structured `Tree branch created: type=EVA, bp=..., activeChild=..., bgChild=..., evaCrew=...` line at `INFO` that the existing mid-recording path emits, with an extra `path=promoted-from-bg-parent` discriminator so log readers can tell the two routes apart.

The new `StartRecording` precondition warn should be unique enough to grep (`"refusing to start with active tree"`) and rate-limited via `ParsekLog.WarnRateLimited` keyed by tree id — a stuck caller could otherwise hammer the log every Update().

## Non-goals

- No change to the post-switch first-modification auto-record system (`#546`). That system arming-but-never-triggering during the capsule-return window is a separate gap; the proposed fix here closes the loss-of-data hole regardless of whether the post-switch trigger ever fires.
- No change to the `Mid-recording EVA detected` path for Bill's case — that path already works.
- No change to the structural-event snapshot pipeline.
- No schema or `RecordingFormatVersion` bump. The tree shape produced by the new path is identical to what an unbroken `Mid-recording EVA detected` would have produced.
- No change to how `FlushRecorderToTreeRecording` flushes data when `ActiveRecordingId` is valid. The drop branch is the only mutation.

## Key files

- `Source/Parsek/ParsekFlight.cs:3105-3115` — current silent-drop site
- `Source/Parsek/ParsekFlight.cs:3319-3346` — `HandleTreeBackgroundFlush` clears `ActiveRecordingId`
- `Source/Parsek/ParsekFlight.cs:3363-3405` — `PromoteRecordingFromBackground` (re-use target for Option A)
- `Source/Parsek/ParsekFlight.cs:6332-6390` — `OnCrewOnEva` decision tree, source-vessel-mismatch early return
- `Source/Parsek/ParsekFlight.cs:9646-9678` — `HandleDeferredAutoRecordEva`, calls `StartRecording`
- `Source/Parsek/ParsekFlight.cs:9709-9830` — `StartRecording`, missing `activeTree != null && ActiveRecordingId == null` branch
- `Source/Parsek/RecordingTree.cs` — `BackgroundMap`, `ActiveRecordingId`, `BranchPoints`
- `logs/2026-05-13_2337_eva-kerbals-missing/KSP.log` lines 57068-60848 — full reproducer trace

## Open questions for reviewers

1. Option A (promote-then-branch) vs Option B (synthesize branch without promotion) — preference?
2. The new `StartRecording` guard: hard refuse (current proposal) or fail-loud + still start a single-node tree as a last-resort recovery? Hard refuse risks the user seeing no recording at all for an unrelated bug; recovery risks masking future regressions.
3. Should the silent-drop log site (step 3) include the recorder's `pid` so we can correlate dropped recordings to KSP's persistent-id space when the bug recurs?
4. Anything in the active-tree restore / Re-Fly paths that legitimately wants a recorder running with `ActiveRecordingId == null`? `RewindInvoker.cs` and `MergeJournalOrchestrator.cs` both manipulate `ActiveRecordingId` during their state machines — need to verify they always reassign before any new `StartRecording`.
