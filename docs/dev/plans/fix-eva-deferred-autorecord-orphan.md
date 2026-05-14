# Fix: Second EVA from same capsule produces an orphan recording that is silently dropped

**Branch:** `plan-eva-deferred-autorecord-orphan`
**Worktree:** `C:/Users/vlad3/Documents/Code/Parsek/Parsek-plan-eva-deferred-autorecord-orphan`
**Companion fix (separate PR):** `plan-eva-nullsolver-subsurface` — independent bug in the same flight, do not bundle.

## Problem

Reproducer (observed in `logs/2026-05-13_2337_eva-kerbals-missing/`):

1. Launch `Kerbal X` (3-crew capsule). Recording starts: `bf8f6c68|Kerbal X|tree`, treeId `4e1d9d29`.
2. EVA Bill from the capsule. `OnCrewOnEva` takes the mid-recording branch path (`ParsekFlight.cs:6363`), creates branch point `f6d1a26a` with `activeChild=6952389d (capsule continuation, pid 2708531065)` and `bgChild=4ba22dea (Bill, pid 1604830044)`. Correct.
3. KSP auto-switches focus to Bill. `HandleTreeBackgroundFlush` (`ParsekFlight.cs:9319`) parks `6952389d` into background and clears `activeTree.ActiveRecordingId = null` at line 9337.
4. Bill plants a flag and the user switches back to the capsule. `OnVesselSwitchComplete` arms post-switch auto-record (`tracked=True`, reason `vessel switch to tracked background member while idle`).
5. The post-switch first-modification trigger (`OnPostSwitchAutoRecordPhysicsFrame`) never fires during the ~20s the user is back inside the capsule — the capsule is settled and motionless, so neither the orbit-change nor landed-motion detector trips.
6. EVA Bob from the capsule. `OnCrewOnEva` is not in the `IsRecording` branch because `recorder == null`, so it never reaches the mid-recording source-vessel guard or branch path. The non-recording auto-record fallback sets `pendingAutoRecord = true` and logs `EVA detected (sit=LANDED) — pending auto-record` (`ParsekFlight.cs:6389`).
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

When `OnCrewOnEva` fires while idle from a source vessel whose recording lives in `activeTree.BackgroundMap`, treat it as a structural split in the existing tree, not as pad-style auto-record. Close the source vessel's background recording as the branch parent, then create the same two-child EVA branch shape that the live mid-recording path produces.

The branch must create the same shape as `CreateSplitBranch` does today: two fresh child recordings under the parent. The active child is whichever vessel KSP has focused by the deferred branch frame, and the other child goes into `BackgroundMap`. The EVA kerbal child is identified by `evaVesselPid` and receives `EvaCrewName` / `ParentRecordingId`; the existing background parent recording is closed as the branch parent, not reused as a child.

When the EVA's source capsule is not tracked and there is no active tree in flight, preserve today's behavior: start a fresh single-node tree. When any active tree exists and the source is not a tracked member, handle the event with a warning and do not arm pad-style auto-record; otherwise `StartRecording` can bind an EVA recorder to an unrelated valid tree head.

Never start a recorder bound to an `activeTree` that has no `ActiveRecordingId` and no plan to create one.

## Invariant to enforce

`recorder.ActiveTree == activeTree` implies `activeTree.ActiveRecordingId != null`, `activeTree.Recordings.ContainsKey(activeTree.ActiveRecordingId)`, and the active tree recording is the slot intended for the live active vessel. Enforce at recorder start time, not at flush time.

## Proposed implementation

### 0. Pre-implementation audits

Before opening the build PR, scan every `ParsekFlight.StartRecording(...)` caller plus the Re-Fly / merge recovery state machines that manipulate `ActiveRecordingId`:

- `Source/Parsek/RewindInvoker.cs`
- `Source/Parsek/MergeJournalOrchestrator.cs`
- `Source/Parsek/LoadTimeSweep.cs`

Record the audit result in the build PR description. The requirement is simple: no flow may call `ParsekFlight.StartRecording` while `activeTree != null` and `ActiveRecordingId` is null/missing or points at a recording for a different live vessel pid. If the audit finds a legitimate transient-null or transient-mismatch recovery window, fix that caller to reassign a valid active recording before recorder start; do not weaken the guard.

### 1. Detect the tracked-parent case in `OnCrewOnEva`

Add a small route helper for the non-recording half of `OnCrewOnEva`, then call it before the existing `ShouldQueueAutoRecordOnEva` fallback. This path must run even when `autoRecordOnEva` is disabled, because this is not "start a new EVA pad recording"; it is a structural split of a tree member that Parsek is already tracking.

```
bool hasSourceVessel = data.from?.vessel != null;
if (!IsRecording && TryStartEvaBranchFromBackgroundParent(data))
    return;

if (!ShouldQueueAutoRecordOnEva(
    hasSourceVessel,
    autoRecordOnEvaEnabled: ParsekSettings.Current?.autoRecordOnEva != false))
{
    ...
}
```

`TryStartEvaBranchFromBackgroundParent` should return `true` once it has handled a tracked-parent event, including fail-loud handled cases that must not fall through to `pendingAutoRecord`:

```
private bool TryStartEvaBranchFromBackgroundParent(
    GameEvents.FromToAction<Part, Part> data)
{
    if (pendingSplitInProgress)
    {
        LogSplitSkip(
            "OnCrewOnEva",
            "pendingSplitInProgress",
            data.from?.vessel?.persistentId ?? 0u,
            data.to?.vessel?.persistentId ?? 0u);
        return true;
    }

    if (activeTree == null
        || data.from?.vessel == null)
        return false;

    Vessel sourceVessel = data.from.vessel;
    uint sourcePid = sourceVessel.persistentId;
    if (!TryResolveTrackedBackgroundParentRecording(
            activeTree,
            sourcePid,
            out string parentRecordingId,
            out string resolveDiagnostic))
    {
        string activeTreeHeadReason;
        uint activePid = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0u;
        bool activeTreeHeadValid =
            CanStartRecorderWithActiveTreeHead(activeTree, activePid, out activeTreeHeadReason);
        ParsekLog.Warn("Flight",
            $"OnCrewOnEva: source pid={sourcePid} is not a tracked background parent " +
            $"({resolveDiagnostic}; activeTreeHeadValid={activeTreeHeadValid}" +
            (activeTreeHeadValid ? "" : $", reason={activeTreeHeadReason}") +
            "); refusing orphan auto-record while an active tree exists");
        return true;
    }

    if (string.IsNullOrEmpty(parentRecordingId)
        || activeTree.Recordings == null
        || !activeTree.Recordings.ContainsKey(parentRecordingId))
    {
        ParsekLog.Warn("Flight",
            $"OnCrewOnEva: source pid={sourcePid} is in BackgroundMap but recording " +
            $"'{parentRecordingId ?? "<null>"}' is missing; refusing orphan auto-record");
        return true;
    }

    string kerbalName = ExtractEvaKerbalName(data);
    if (string.IsNullOrEmpty(kerbalName))
    {
        ParsekLog.Warn("Flight",
            $"OnCrewOnEva: tracked background source pid={sourcePid} but EVA crew name is missing; refusing orphan auto-record");
        return true;
    }

    StartCoroutine(DeferredEvaBranchFromBackgroundParent(
        parentRecordingId,
        sourceVessel.persistentId,
        kerbalName,
        data));
    return true;
}
```

This helper belongs in the non-recording path. Putting `recorder == null` logic inside the existing source-vessel-mismatch branch would be unreachable because that branch only executes while `IsRecording == true`.

For testability, keep the decision predicate behind this instance method pure: pass source pid, active pid, whether an active tree exists, whether the source is in `BackgroundMap`, whether the mapped recording exists, live-recorder pid, `pendingSplitInProgress`, and source/crew-name availability into an `internal static` evaluator that returns a small enum such as `StartDeferredBackgroundParentBranch`, `QueueFreshAutoRecord`, `SkipLiveSourceMismatch`, or `HandledFailLoud`.

The fresh EVA fallback is allowed only when `activeTree == null`. If an active tree exists and the source vessel is not a tracked background parent, handle the event with a warning and do not arm `pendingAutoRecord`; otherwise `StartRecording` can bind the new EVA recorder to an unrelated valid tree head.

`BackgroundMap` remains the authoritative signal for "currently background-tracked parent". Do not scan `Recordings` directly as a fallback because historical, terminal, and non-leaf recordings can share vessel pids and are not necessarily live parents. To defend against stale maps after load/restore, wrap lookup in a helper that rebuilds once only when the tree has no active head:

```
private static bool TryResolveTrackedBackgroundParentRecording(
    RecordingTree tree,
    uint sourcePid,
    out string recordingId,
    out string diagnostic)
{
    recordingId = null;
    diagnostic = "no tree";
    if (tree == null || sourcePid == 0)
        return false;

    if (tree.BackgroundMap != null
        && tree.BackgroundMap.TryGetValue(sourcePid, out recordingId))
    {
        diagnostic = "background-map-hit";
        return true;
    }

    if (string.IsNullOrEmpty(tree.ActiveRecordingId))
    {
        tree.RebuildBackgroundMap();
        if (tree.BackgroundMap != null
            && tree.BackgroundMap.TryGetValue(sourcePid, out recordingId))
        {
            diagnostic = "background-map-hit-after-rebuild";
            return true;
        }
        diagnostic = "background-map-miss-after-rebuild";
        return false;
    }

    diagnostic = "background-map-miss-active-head-present";
    return false;
}
```

### 2. Close the background parent and create the branch after KSP settles focus

Do not require `FlightGlobals.ActiveVessel` to still be the source capsule in `OnCrewOnEva`. The existing live-recorder `DeferredEvaBranch` deliberately waits one frame and supports EVA-active, ship-active, and ambiguous focus states. The background-parent path needs the same timing tolerance.

Use a new `DeferredEvaBranchFromBackgroundParent` coroutine that waits one frame, determines `activeChild` / `backgroundChild` using the same logic as `DeferredEvaBranch`, then closes the source background recording and creates the two fresh child recordings. This avoids the unsafe shape where `PromoteRecordingFromBackground` calls `FlightRecorder.StartRecording()` while KSP may already have focused the EVA kerbal.

Implementation sketch:

```
private IEnumerator DeferredEvaBranchFromBackgroundParent(
    string parentRecordingId,
    uint sourcePid,
    string kerbalName,
    GameEvents.FromToAction<Part, Part> data)
{
    yield return null;

    double evaEventUT = Planetarium.GetUniversalTime();
    var evaInvolved = new List<Vessel>(2);
    if (data.from?.vessel != null) evaInvolved.Add(data.from.vessel);
    if (data.to?.vessel != null && data.to.vessel != data.from?.vessel)
        evaInvolved.Add(data.to.vessel);

    Vessel evaVessel = data.to?.vessel;
    Vessel shipVessel = data.from?.vessel;
    if (evaVessel == null || shipVessel == null || shipVessel.persistentId != sourcePid)
    {
        ParsekLog.Warn("Flight",
            $"DeferredEvaBranchFromBackgroundParent: invalid vessels sourcePid={sourcePid} " +
            $"shipPid={shipVessel?.persistentId ?? 0u} evaPid={evaVessel?.persistentId ?? 0u}");
        yield break;
    }

    uint evaPid = evaVessel.persistentId;
    if (!CheckBranchDeduplication(evaEventUT, evaPid))
        yield break;

    Vessel currentActive = FlightGlobals.ActiveVessel;
    Vessel activeChild;
    Vessel backgroundChild;
    if (currentActive != null && currentActive.persistentId == evaPid)
    {
        activeChild = evaVessel;
        backgroundChild = shipVessel;
    }
    else if (currentActive != null && currentActive.persistentId == sourcePid)
    {
        activeChild = shipVessel;
        backgroundChild = evaVessel;
    }
    else
    {
        activeChild = evaVessel;
        backgroundChild = shipVessel;
    }

    CreateSplitBranchFromBackgroundParent(
        parentRecordingId,
        sourcePid,
        activeChild,
        backgroundChild,
        evaEventUT,
        kerbalName,
        evaPid,
        branchPath: "background-parent");
}
```

`CreateSplitBranchFromBackgroundParent` should reuse the pure `BuildSplitBranchData` helper and as much of `CreateSplitBranch`'s child/snapshot/background-map/start-recorder tail as practical, but it must not require `pendingSplitRecorder`. Required steps:

- validate `activeTree`, parent recording id, child vessels, and parent recording existence
- validate that `sourcePid` still maps to `parentRecordingId` in `BackgroundMap`
- set `parentRecording.ChildBranchPointId = bp.Id`
- add the branch point and two child recordings
- set `activeTree.ActiveRecordingId = activeChild.RecordingId`
- stage `BackgroundMap` so only the non-active child is present while recorder start runs
- start a new `FlightRecorder` for `activeChild`, then verify `recorder.RecordingVesselId == activeChild.VesselPersistentId`
- if recorder startup fails or binds the wrong pid, roll back the staged branch point, child recordings, parent `ChildBranchPointId`, `ActiveRecordingId`, and touched `BackgroundMap` entries
- after recorder startup succeeds, append exactly one structural boundary sample for the parent path by calling `backgroundRecorder.OnVesselRemovedFromBackground(sourcePid)`, then call `backgroundRecorder.OnVesselBackgrounded(...)` for the non-active child; do not append the same source-parent snapshot through both background and foreground recorders
- clear `pendingAutoRecord` and keep the existing `Tree branch created: type=EVA, ...` log shape with `path=background-parent`

If `activeChild` is not the currently active vessel when recorder start is attempted, fail loudly and do not start a recorder into the wrong child slot. The deferred focus selection should make that rare; the guard exists to prevent wrong-slot corruption.

The existing live-recorder Bill path keeps the default `DeferredEvaBranch` / `CreateSplitBranch` flow and should produce the same tree/log shape it does today, aside from any shared helper extraction.

### 3. Guard `StartRecording` against invalid active-tree heads

Add a precondition in `StartRecording` after stale chain-state cleanup and before allocating a new `FlightRecorder`. Extract the predicate into a pure helper so xUnit can test it without Unity globals:

```
internal static bool CanStartRecorderWithActiveTreeHead(
    RecordingTree tree,
    uint activeVesselPid,
    out string reason)
{
    reason = null;
    if (tree == null)
        return true;
    if (string.IsNullOrEmpty(tree.ActiveRecordingId))
    {
        reason = $"active tree '{tree.Id ?? "<no-tree-id>"}' has no ActiveRecordingId";
        return false;
    }
    if (tree.Recordings == null || !tree.Recordings.ContainsKey(tree.ActiveRecordingId))
    {
        reason = $"active tree '{tree.Id ?? "<no-tree-id>"}' points at missing recording '{tree.ActiveRecordingId}'";
        return false;
    }
    Recording activeRec = tree.Recordings[tree.ActiveRecordingId];
    uint recordingPid = ResolveLiveTreeRecordingPidForRestore(activeRec);
    if (activeVesselPid != 0
        && recordingPid != 0
        && recordingPid != activeVesselPid)
    {
        reason = $"active tree '{tree.Id ?? "<no-tree-id>"}' active recording '{tree.ActiveRecordingId}' " +
            $"belongs to live pid={recordingPid}, not active vessel pid={activeVesselPid}";
        return false;
    }
    return true;
}
```

Caller sketch:

```
uint activePid = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0u;
if (!CanStartRecorderWithActiveTreeHead(activeTree, activePid, out string activeTreeHeadReason))
{
    ParsekLog.WarnRateLimited("Flight",
        $"start-recording-invalid-active-tree-head-{activeTree?.Id ?? "none"}",
        $"StartRecording: refusing to start with active tree because {activeTreeHeadReason}; " +
        "callers must promote a background recording, prepare a fresh tree recording, or create a branch first.");
    return;
}
```

Do not exempt continuations. In this method, any recorder attached to a non-null `activeTree` needs a valid `ActiveRecordingId` flush slot that belongs to the active vessel. Existing valid callers should satisfy that contract:

- fresh first recording: `activeTree == null`, then `StartRecording` creates the single-node tree
- post-switch fresh recording: `PrepareActiveTreeForFreshPostSwitchRecording` adds a recording and sets `ActiveRecordingId` before `StartRecording`
- promotion / split / restore paths: they set `ActiveRecordingId` before calling `FlightRecorder.StartRecording(isPromotion: true)` directly

This guard makes any missed caller fail loudly instead of visibly recording data that cannot be flushed or, worse, flushed into the wrong recording. The Bob path should be handled before `pendingAutoRecord` is armed, so it should not reach this guard in the repaired flow.

### 4. Stop deferred EVA retry loops on invalid tree head

`HandleDeferredAutoRecordEva` currently retries every frame when `StartRecording` fails because it only clears `pendingAutoRecord` after `IsRecording` becomes true. Add an explicit invalid-tree-head branch before calling `StartRecording`:

```
    uint activePid = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0u;
    if (!CanStartRecorderWithActiveTreeHead(activeTree, activePid, out string activeTreeHeadReason))
{
    ParsekLog.WarnRateLimited("Flight",
        $"deferred-eva-invalid-active-tree-head-{activeTree?.Id ?? "none"}",
        $"HandleDeferredAutoRecordEva: clearing pending auto-record because active tree head is invalid: " +
        $"{activeTreeHeadReason}; refusing orphan auto-record");
    pendingAutoRecord = false;
    chainManager.PendingContinuation = false;
    chainManager.PendingEvaName = null;
    ParsekLog.ScreenMessage("Recording not started: invalid Parsek tree state", 3f);
    return;
}
```

This is a last-resort unwind for future missed callers. The repaired second-EVA path should never arm `pendingAutoRecord` for a tracked background parent in the first place.

### 5. Surface the silent-drop site

`FlushRecorderToTreeRecording` at `ParsekFlight.cs:3110-3114` currently logs `WARN` and drops. With the tracked-parent route and the recorder-start guard in place this branch is unreachable, but keep the log and convert the drop into a `Debug.LogError` (or stronger `WARN` with point/event counts) so a future regression is impossible to miss:

```
ParsekLog.Warn("Flight",
    $"FlushRecorderToTreeRecording: no active recording id in tree '{tree.Id}' — " +
    $"DROPPING pid={rec.RecordingVesselId} " +
    $"points={rec.Recording?.Count ?? 0} " +
    $"orbitSegments={rec.OrbitSegments?.Count ?? 0} " +
    $"partEvents={rec.PartEvents?.Count ?? 0} " +
    $"flagEvents={rec.FlagEvents?.Count ?? 0} " +
    $"segmentEvents={rec.SegmentEvents?.Count ?? 0} " +
    $"trackSections={rec.TrackSections?.Count ?? 0}. " +
    "This is a bug; report to maintainers.");
```

Mirror the same missing-id diagnostics for the `!tree.Recordings.TryGetValue(recId, out treeRec)` branch.

### 6. Tests

xUnit (`Source/Parsek.Tests/`):

- `EvaBranchFromBackgroundParentDecisionTests.cs` — pure-static route decision helper for the non-recording half of `OnCrewOnEva`. Cases:
  - idle + source pid in `BackgroundMap` + active vessel pid equals source pid + recording exists -> `StartDeferredBackgroundParentBranch`
  - idle + source pid in `BackgroundMap` + active vessel pid equals EVA pid + recording exists -> `StartDeferredBackgroundParentBranch`
  - idle + no active tree + source not in `BackgroundMap` -> `QueueFreshAutoRecord`
  - idle + active tree exists + source not in `BackgroundMap` + valid active tree head -> `HandledFailLoud`
  - idle + active tree exists + source not in `BackgroundMap` + invalid active tree head -> `HandledFailLoud`
  - idle + source pid in `BackgroundMap` but recording id is missing from `Recordings` -> `HandledFailLoud`
  - idle + `pendingSplitInProgress` -> `HandledFailLoud` / skip, with no `pendingAutoRecord`
  - live recorder + source pid equals `recorder.RecordingVesselId` -> existing mid-recording branch path
  - live recorder + source pid differs -> existing source-vessel-mismatch skip

- `TrackedBackgroundParentResolutionTests.cs` — verify direct `BackgroundMap` hit, `BackgroundMap` miss with `ActiveRecordingId` present, and null-head rebuild recovery. Include a save/load round-trip that reproduces the expected parent-in-`BackgroundMap` state after `RecordingTree.Load()` / `RebuildBackgroundMap()`.

- `StartRecordingTreeHeadGuardTests.cs` — test `CanStartRecorderWithActiveTreeHead`: null tree allows, valid active id with matching pid allows, null/empty `ActiveRecordingId` refuses, missing active recording refuses, active recording pid mismatch refuses. Also assert the rate-limit keys include tree id and call-site context (`start-recording-invalid-active-tree-head-...` vs `deferred-eva-invalid-active-tree-head-...`) so one stuck caller cannot suppress unrelated future guard warnings. A thin integration test can use `ParsekLog.TestSinkForTesting` around the `StartRecording` caller only if existing Unity seams make that practical.

- `DeferredEvaAutoRecordGuardTests.cs` — with `pendingAutoRecord = true`, active EVA vessel, and invalid active tree head, assert `HandleDeferredAutoRecordEva` clears `pendingAutoRecord`, clears pending EVA continuation flags, logs the invalid-head warning, and does not call `StartRecording` repeatedly.

- `FlushRecorderToTreeRecordingDropDiagnosticsTests.cs` — call `FlushRecorderToTreeRecording` with a tree that has `ActiveRecordingId == null` and a recorder holding fake points + part events + flag events. Assert the warn message reports the dropped counts. Regression test only — should be unreachable post-fix but documents the contract.

- `CreateSplitBranchLoggingTests.cs` or an existing split logging test — verify the optional path discriminator appears only for the background-parent route and the default live-recorder route keeps the previous log shape.

In-game runtime test (`Source/Parsek/InGameTests/RuntimeTests.cs`, scene `FLIGHT`):

- `EvaTwiceFromSameCapsuleProducesTwoBranches` — load a Kerbal X-style 3-crew vessel on the pad, drive EVA-A, return-to-capsule, EVA-B, scene-exit. Assert:
  - tree has 2 EVA branch points
  - the second EVA branch parent is the prior capsule background recording, not the tree root and not Bill's EVA recording
  - both EVA kerbal recordings appear in `CommitTree` output with non-empty point lists
  - both EVA kerbals have committed `.prec` + `_vessel.craft` files in `saves/<save>/Parsek/Recordings/`
  - KSP.log contains `path=background-parent`
  - KSP.log does not contain `treeRec=-` for Bob or `FlushRecorderToTreeRecording: no active recording id`

This is the only path that exercises the OnVesselSwitchComplete -> background-flush -> back-to-capsule -> next-EVA chain end-to-end with real KSP events. xUnit can't replicate KSP's vessel-switch timing.

### 7. Logging

Keep the primary branch evidence in the existing structured `Tree branch created: type=EVA, bp=..., activeChild=..., bgChild=..., evaCrew=...` line. Add `path=background-parent` through the background-parent branch helper so log readers can distinguish the repaired route without learning a second log format.

The new `StartRecording` precondition warn should be unique enough to grep (`"refusing to start with active tree"`) and rate-limited via `ParsekLog.WarnRateLimited` keyed by tree id — a stuck caller could otherwise hammer the log every Update().

### 8. Documentation checklist

The implementation PR changes behavior and must update docs in the same commit:

- `CHANGELOG.md` — add/update the current-version entry for second-EVA orphan recording prevention and the fail-loud invalid-head guard.
- `docs/dev/todo-and-known-bugs.md` — mark the second-EVA orphan bug fixed and make the fix description match the implemented route.
- `AGENTS.md` / `.claude/CLAUDE.md` — update only if the implementation changes workflow, commands, file layout, or durable project rules.

## Non-goals

- No change to the post-switch first-modification auto-record system (`#546`). That system arming-but-never-triggering during the capsule-return window is a separate gap; the proposed fix here closes the loss-of-data hole regardless of whether the post-switch trigger ever fires.
- No change to the `Mid-recording EVA detected` path for Bill's case — that path already works.
- No redesign of the structural-event snapshot pipeline. The background-parent path should preserve one parent boundary sample and avoid double-appending the same source-parent event through both background and foreground recorders.
- No schema or `RecordingFormatVersion` bump. The tree shape produced by the new path is identical to what an unbroken `Mid-recording EVA detected` would have produced.
- No change to how `FlushRecorderToTreeRecording` flushes data when `ActiveRecordingId` is valid. Only the invalid-head diagnostics change.

## Key files

- `Source/Parsek/ParsekFlight.cs:3105-3115` — current silent-drop site
- `Source/Parsek/ParsekFlight.cs:9319-9346` — `HandleTreeBackgroundFlush` clears `ActiveRecordingId`
- `Source/Parsek/ParsekFlight.cs:3363-3405` — `PromoteRecordingFromBackground` shows the existing background-removal/promotion contract to avoid misusing when focus has already switched
- `Source/Parsek/ParsekFlight.cs:3906-3968` — `BuildSplitBranchData`, reusable pure branch data helper
- `Source/Parsek/ParsekFlight.cs:4034-4187` — `CreateSplitBranch`, reusable child/snapshot/background-map/start-recorder tail patterns
- `Source/Parsek/ParsekFlight.cs:6332-6390` — `OnCrewOnEva` decision tree, source-vessel-mismatch early return, non-recording EVA fallback
- `Source/Parsek/ParsekFlight.cs:9646-9678` — `HandleDeferredAutoRecordEva`, calls `StartRecording`
- `Source/Parsek/ParsekFlight.cs:9709-9830` — `StartRecording`, missing `activeTree != null && ActiveRecordingId == null` branch
- `Source/Parsek/RecordingTree.cs` — `BackgroundMap`, `ActiveRecordingId`, `BranchPoints`
- `Source/Parsek/RewindInvoker.cs`, `Source/Parsek/MergeJournalOrchestrator.cs`, `Source/Parsek/LoadTimeSweep.cs` — required pre-implementation guard audit
- `logs/2026-05-13_2337_eva-kerbals-missing/KSP.log` lines 57068-60848 — full reproducer trace

## Resolved choices

1. Use a deferred background-parent branch, not simple promote-then-branch. `PromoteRecordingFromBackground` is unsafe if KSP has already focused the EVA kerbal because `FlightRecorder.StartRecording()` records `FlightGlobals.ActiveVessel`.
2. Use a hard fail-loud `StartRecording` guard. Do not create a fallback single-node tree while another active tree exists; that would mask a tree-state bug and could fork the user's timeline silently.
3. Include recorder pid and all buffered-data counts at the flush drop site.
4. Treat `recorder.ActiveTree == activeTree` with no valid/matching `ActiveRecordingId` as invalid for every caller, including continuations. Current restore / Re-Fly flows should reassign the active id before any recorder start; the implementation PR must audit `RewindInvoker`, `MergeJournalOrchestrator`, and `LoadTimeSweep` before adding the guard.
5. Use `BackgroundMap` as the authoritative tracked-parent signal, with one rebuild retry only for null-head trees. Do not fall back to scanning all recordings by pid, and do not allow fresh EVA auto-record while any active tree exists.
6. Clear deferred EVA pending flags when an invalid-head guard blocks auto-record, so the guard cannot create an every-frame retry loop.
