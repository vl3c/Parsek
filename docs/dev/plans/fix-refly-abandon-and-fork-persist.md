# Fix plan — Re-Fly abandon-and-retry leak + in-place-continuation fork persist

Two pre-existing Parsek bugs surfaced during PR #872 playtest in `logs/2026-05-16_2226_pr872-groups-investigation/`. They are independent — one is a session-lifecycle leak upstream of the supersede pipeline; the other is a save-serialization gap downstream of the merge tail. Both are reproducible from the frozen `s11-save-evidence/persistent.sfs`. Pre-1.0 dev policy applies: no backwards-compat shim for the broken save; pick the correct contract end-to-end.

PR #872's splitter, its 10 named merge-journal phases (`Begin → Split → Supersede → Tombstone → Finalize → Durable1Done → RpReap → MarkerCleared → Durable2Done → Complete`) with their 5 durable-save barriers (`begin / split / durable1 / durable2 / final`), and the chain-then-supersede composite walker are all working correctly in both repro traces. The bugs sit on either side of that pipeline.

**Bug 2's fix adds one new phase (`TreeMerge`) and one new durable barrier (`treemerge`)** between `Begin` and `Split`, taking the totals to 11 phases and 6 durable barriers.

## Frozen evidence

`logs/2026-05-16_2226_pr872-groups-investigation/s11-save-evidence/EVIDENCE-SUMMARY.md` and `persistent.sfs`. KSP.log + Player.log are in the parent directory.

## Bug 1 — Abandon-and-retry Re-Fly leaks NotCommitted provisional; closure walk then poisons it

### Repro trace from KSP.log (mission #2, `rp_47d35e19`)

| Line | UT | Event |
|---|---|---|
| 69940 | 22:16:31.948 | `StartInvoke sess=sess_b3992b50 rp=rp_47d35e19 slot=0` (session B, will abandon) |
| 70289 | 22:16:35.483 | `AddProvisional: rec=rec_675a9193 state=NotCommitted supersedeTarget=ad1ef300` |
| 70291 | 22:16:35.483 | `AtomicMarkerWrite: attached in-place fork rec=rec_675a9193 to tree 'Kerbal X' (id=098f7b63)` |
| 79914 | 22:16:56.033 | `End reason=retry sess=sess_b3992b50 rp=rp_47d35e19 target=Launch` (user clicks Retry) |
| 79919 | 22:16:56.042 | `StartInvoke sess=sess_ef82c3b2 rp=rp_47d35e19 slot=0` (session 2, the retry) |
| **80232** | **22:16:56.687** | **`[Rewind] Zombie discarded rec=rec_675a9193 sess=sess_b3992b50 supersedeTarget=ad1ef300`** (LoadTimeSweep ran during the post-retry RP-quickload OnLoad and removed the orphan from `CommittedRecordings`) |
| 80282 | 22:16:58.762 | `AtomicMarkerWrite: in-place continuation forked — fork rec_3993fbe2 supersedes priorTip ad1ef300` (session 2 provisional added) |
| 90312 | 22:17:29.252 | `Merger.MergeTree: id=rec_675a9193` (tree-commit pass during the retry's merge sequence re-walks the active tree's `Recordings` and re-adds `rec_675a9193` to `CommittedRecordings` because its RECORDING node still lives inside the pending/active tree dict — `LoadTimeSweep` only removed it from the flat list, not from the tree dict) |
| 91072 | 22:17:29.746 | `LedgerOrchestrator: Committed recording 'rec_675a9193': 3 actions added to ledger` (the zombie's session-creating KerbalAssignment actions get re-committed) |
| 91853 | 22:17:30.429 | `ERS Rebuilt: 19 entries from 34 committed` (34, up from 30 — the zombie is back) |
| **91878** | **22:17:30.439** | **`[Supersede] rel=rsr_a121f349 old=rec_675a9193 new=rec_3993fbe2`** — `AppendRelations`'s closure walk found `rec_675a9193` as a PID-peer of the new TIP and wrote an invalid supersede row pointing FROM a `NotCommitted` orphan |

### persistent.sfs evidence

- Line **1646** `recordingId = rec_675a9193…` in tree `098f7b63…`, line **1678** `mergeState = NotCommitted`, line 1679 `creatingSessionId = sess_b3992b50`, line 1680 `supersedeTargetId = ad1ef300`, line 1681 `provisionalForRpId = rp_47d35e19`. `vesselDestroyed = True`.
- Line **3046** `RECORDING_SUPERSEDE_RELATION { oldRecordingId = rec_675a9193 → newRecordingId = rec_3993fbe2 }`. **NotCommitted should never be a supersede source** — that's an invariant violation of the data model.
- Same shape for mission #3: `rec_3cdedee5…` at line 2558, supersede row at 3086.

### Root cause

Three independent contracts collude to produce the bug:

1. **The retry path's marker write does not reap prior NotCommitted provisionals for the same RP**. `RewindInvoker.AtomicMarkerWrite` at `RewindInvoker.cs:1104-1338` is the synchronous critical section that adds the new session's provisional. It writes `provisional.SupersedeTargetId = priorTip` and creates the new `ReFlySessionMarker`, but never enumerates `RecordingStore.CommittedRecordings` looking for prior-session NotCommitted recordings whose `provisionalForRpId == rp.RewindPointId`. The prior session was ended via `End reason=retry` — which clears the marker but **does not touch the prior provisional Recording** because the retry path assumes `LoadTimeSweep` on the next OnLoad will reap any orphans.

2. **`LoadTimeSweep.Run` DOES remove the orphan from the flat `RecordingStore.CommittedRecordings` list** (the "Zombie discarded" log at trace line 80232) — but `RemoveCommittedInternal` at `RecordingStore.cs:728-734` only removes the recording from the flat list. **It does NOT remove it from any tree's `Recordings` dictionary**. The tree dict is the canonical owning collection for that recording.

3. **`RecordingStore.CommitTree` / `FinalizeTreeCommit` re-adds tree-dict recordings back to `CommittedRecordings` on every commit pass**. Verified at `RecordingStore.cs:1363-1386`: `FinalizeTreeCommit` iterates `foreach (var rec in tree.Recordings.Values)`, calls `FindCommittedRecordingIndex(rec)`, and on miss (`existingIndex < 0`) executes `committedRecordings.Add(rec)` (line 1384). Sequence in trace: the retry session 2 records into the active tree (which still holds `rec_675a9193` in its `Recordings` dict because step 2 only cleaned the flat list); the active tree gets committed; `FinalizeTreeCommit` re-adds the orphan. The zombie is back in `committedRecordings`.

The closure walk in `EffectiveState.EnqueuePidPeerSiblings` and `EnqueueChainSiblings` then iterates `recById` (built from `RecordingStore.CommittedRecordings`), finds the re-added zombie as a same-PID peer of the new TIP (both `vesselPersistentId=2708531065`, same `TreeId`, `candStart >= marker.InvokedUT - 0.05`), and enqueues it. `AppendRelations` writes the supersede row.

### Fix design

Pick the **primary fix at the marker-write site** and add a **secondary defense in the closure walk**.

**Primary fix — eager reap of prior NotCommitted provisionals for the same RP at `AtomicMarkerWrite` time.**

New helper in `RewindInvoker.cs`, called from `AtomicMarkerWrite` immediately BEFORE `BuildProvisionalRecording`:

```csharp
/// <summary>
/// Before the new session adds its provisional, reap any prior NotCommitted
/// provisional that targets the same RewindPoint. The user just clicked Retry
/// on the same RP slot, so the prior abandoned attempt's provisional is
/// orphaned by construction.
/// Removes the recording from RecordingStore.CommittedRecordings, every
/// committed tree's Recordings dict (and every pending/active tree),
/// plus sidecar files on disk.
/// Returns the number of recordings reaped.
/// </summary>
internal static int ReapPriorProvisionalsForRp(string rpId, string newSessionId)
```

Implementation:
1. Walk `RecordingStore.CommittedRecordings` for any `rec` with `rec.MergeState == MergeState.NotCommitted && rec.ProvisionalForRpId == rpId && rec.CreatingSessionId != newSessionId`. Build a `List<string>` of victim ids (don't mutate during iteration).
2. For each victim, log `ParsekLog.Info("ReFly", $"ReapPriorProvisional: removing orphan rec={id} priorSess={priorSess} newSess={newSess} rp={rpId} (prior abandoned attempt; same-RP retry started)")`.
3. Remove from flat list via `RecordingStore.RemoveCommittedById(id)`.
4. Walk all `RecordingStore.CommittedTrees` and call `tree.Recordings.Remove(id)` + `tree.RebuildBackgroundMap()` if the dict changed.
5. Walk `RecordingStore.PendingTree?.Recordings` and remove there too.
6. Walk `ParsekFlight.Instance?.ActiveTreeForSerialization?.Recordings` and remove there too. The active tree is where the zombie lives in the s11 trace — step 4 + 5 alone would not catch it.
7. Delete sidecar files via `RecordingStore.DeleteRecordingFiles(rec)` to prevent CleanOrphanFiles warnings on next save.
8. Bump `RecordingStore.StateVersion` once at the end if any victim was found, and `scenario.BumpSupersedeStateVersion()` so ERS invalidates.

Call site in `RewindInvoker.AtomicMarkerWrite` (just inside the `try` block, before `BuildProvisionalRecording`):

```csharp
try
{
    int reaped = ReapPriorProvisionalsForRp(rp.RewindPointId, sessionId);
    if (reaped > 0)
        ParsekLog.Info(SessionTag,
            $"AtomicMarkerWrite: reaped {reaped} prior NotCommitted provisional(s) " +
            $"for rp={rp.RewindPointId} before new sess={sessionId}");

    provisional = BuildProvisionalRecording(rp, selected, originChild, sessionId, stripResult);
    ...
}
```

**Secondary defense — `EnqueuePidPeerSiblings` and `EnqueueChainSiblings` skip `NotCommitted` recordings.**

In `EffectiveState.cs`, add a state guard inside both walks. The primary fix makes this unreachable for the Re-Fly retry case, but the secondary defense fails-loud if a future regression re-introduces NotCommitted orphans into the committed store:

```csharp
// In EnqueuePidPeerSiblings, just after the vesselPersistentId / TreeId /
// visited / candStart checks:
if (cand.MergeState == MergeState.NotCommitted)
{
    ParsekLog.Warn(SupersedeTag,
        $"EnqueuePidPeerSiblings: skipped NotCommitted peer " +
        $"rec={cand.RecordingId} pid={cand.VesselPersistentId} " +
        $"tree={cand.TreeId} sess={cand.CreatingSessionId ?? \"<none>\"} " +
        $"(should have been reaped by AtomicMarkerWrite's ReapPriorProvisionalsForRp " +
        $"or by LoadTimeSweep — investigate)");
    continue;
}
```

Identical guard in `EnqueueChainSiblings`. Both use `Warn` (not `Verbose`) because reaching this code path indicates the primary fix or LoadTimeSweep did not run when it should have.

**Tertiary defense — `AppendRelations` invariant check.** Add to `SupersedeCommit.cs` (the closure iteration loop), as a row-write guard right before `scenario.RecordingSupersedes.Add(rel)`. Pattern mirrors `ValidateSupersedeTarget` at `SupersedeCommit.cs:225-236`: `#if DEBUG` throws (so a developer build crashes loudly on invariant violation during the playtest of any future regression), release builds warn-and-skip (so a shipped player save is not killed by a single bad row):

```csharp
if (recById != null && recById.TryGetValue(oldId, out var oldRec)
    && oldRec != null && oldRec.MergeState == MergeState.NotCommitted)
{
    string msg =
        $"AppendRelations: refusing row old={oldId} new={newRecordingId} " +
        $"because old is NotCommitted (sess={oldRec.CreatingSessionId ?? "<none>"}); " +
        $"data-model invariant violated upstream — investigate";
#if DEBUG
    throw new InvalidOperationException(msg);
#else
    ParsekLog.Warn(Tag, msg);
    continue;
#endif
}
```

### LoadTimeSweep extension (cleanup of the existing tree-dict leak)

`LoadTimeSweep.RemoveDiscardRecordings` at `LoadTimeSweep.cs:1133-1159` currently calls `RecordingStore.RemoveCommittedInternal(rec)` only. Extend to also remove the recording from every tree's `Recordings` dict, the pending tree, and any active tree (mirroring the primary fix's removal logic). This closes the structural leak that allowed `CommitTree` to re-add the zombie. New per-recording log:

```csharp
ParsekLog.Info(RewindTag,
    $"Zombie discarded rec={rec.RecordingId} sess={rec.CreatingSessionId} " +
    $"supersedeTarget={rec.SupersedeTargetId ?? \"<none>\"} " +
    $"removedFromFlatList={fromFlat} removedFromCommittedTrees={fromCommittedTrees} " +
    $"removedFromPendingTree={fromPending} removedFromActiveTree={fromActive}");
```

### Why not just fix the closure walk?

Option-only-secondary (skip NotCommitted in closure walks) would prevent the supersede row but leave the zombie Recording in `RecordingStore.CommittedRecordings`, in tree `Recordings` dicts, with full sidecar files on disk, and visible to UI components that scan committed recordings directly (RecordingsTableUI, MissionList). The user would still see the zombie in the in-game timeline as a phantom "Kerbal X (NotCommitted)" row. The primary fix at the marker-write site removes the root cause; the secondary/tertiary defenses are guards.

### Crash-recovery considerations

The eager reap runs inside `AtomicMarkerWrite`'s synchronous critical section, BEFORE the marker is written and BEFORE any `DurableSave`. A crash mid-reap leaves a partial state where some prior provisionals are removed and some are not — but on next load, `LoadTimeSweep` (with the dict-leak fix above) runs first and reaps any remaining NotCommitted orphans whose `creatingSessionId` no longer matches a live marker. The new marker has not been written yet at crash time, so the reap is idempotent: the next session's marker write will reap whatever's left.

**No new `MergeJournal.Phases.*` entries are needed for Bug 1.** The reap is pre-marker and pre-journal; the merge journal phase tree from PR #872 is untouched.

## Bug 2 — In-place-continuation fork's RECORDING node dropped from RECORDING_TREE on save

### Repro trace from KSP.log (mission #1, `sess_cd9022ac`, fork `rec_12b7252f`)

| Line | UT | Event |
|---|---|---|
| 53293 | 22:15:48.997 | `AtomicMarkerWrite: in-place continuation forked — fork rec_12b7252f supersedes priorTip 575240c7 ... treeAttach=eager` |
| 53296 | 22:15:48.997 | `attached in-place fork rec=rec_12b7252f to tree 'Kerbal X' (id=cde7313f)` (added to pending tree via `EnsureForkAttachedToTree`) |
| 54140 | 22:15:49.533 | `RestoreActiveTreeFromPending: resumed recording tree 'Kerbal X' activeRec='rec_12b7252f'` (`PopPendingTree` → activeTree) |
| 66464 | 22:16:14.122 | `tree=cde7313f tree.recs=14/0 pend.tree=- pend.sa=-` (right before merge: 14 recs in activeTree, no pending tree) |
| 66466 | 22:16:14.122 | `OnSave: saving 3 committed tree(s)` (3 committed trees in `RecordingStore.CommittedTrees` at this point) |
| 66961 | 22:16:15.958 | `CommitTreeSceneExit (autoMerge off): force-wrote 1 dirty sidecar(s) after finalize` |
| 66962 | 22:16:15.958 | `Stashed pending tree 'Kerbal X' (13 recordings, state=Finalized)` |
| **66989** | **22:16:15.961** | **`CommitTree: duplicate tree id='cde7313f' skipped reason=incoming-missing-existing-ids missingRecordingIds=0 missingBranchPointIds=1`** |
| 67843 | 22:16:16.005 | `TryCommitReFlySupersede: invoking MergeJournalOrchestrator for sess=sess_cd9022ac provisional=rec_12b7252f origin=575240c7` |
| 68161 | 22:16:16.444 | `phase=Split` |
| 68404 | 22:16:16.782 | `[Supersede] rel=rsr_b91602ff old=98616eff new=rec_12b7252f` (TIP→fork supersede row) |
| 68503 | 22:16:16.790 | `phase=Durable1Done` |
| 68507 | 22:16:16.790 | `OnSave: saving 29 committed recordings` |
| **68575** | **22:16:16.891** | **`Save: tree='Kerbal X' formatVersion=0 recordings=10 branchPoints=6`** — Tree `cde7313f` written with 10 recordings: HEAD `575240c7`, TIP `98616eff` (newly added by splitter), 7 pre-rewind debris, 1 probe `219c38cc`. **NO `rec_12b7252f`.** |
| 71097 | 22:16:35.685 | `[Anchor] recorded-relative-unresolved: reason=focus-tree-missing recordingId=rec_12b7252f` (downstream symptom — debris cannot find its anchor recording's tree because the fork has no owning tree in `committedTrees`) |

### persistent.sfs evidence

- `rec_12b7252f…` appears 0 times as a RECORDING node anywhere in the save. It is referenced only as:
  - `newRecordingId` in supersede rows at lines 3023 and 3031
  - `retiringRecordingId` in tombstone rows at lines 3130, 3138, 3146, 3154
- Sidecar files for `rec_12b7252f` are preserved on disk in `Parsek/Recordings/` (`.prec`, `.pann`, `_vessel.craft`, `_ghost.craft`).
- Tree 1 (`cde7313fc6574f789d234dca7fb6a45a`) `activeRecordingId = 575240c7…` at line 648 — points at HEAD instead of fork.

### Root cause

The in-place-continuation fork's lifecycle has three sub-bugs that combine:

**(2a) `CommitTreeSceneExit` calls `CommitTree` on the active tree, which is rejected as a duplicate.** Trace line 66989: `CommitTree: duplicate tree id='cde7313f' skipped reason=incoming-missing-existing-ids missingRecordingIds=0 missingBranchPointIds=1`. The incoming active tree has 13+ recordings (incl. the fork) but is missing one BranchPoint from the existing committed tree. `RecordingStore.ShouldReplaceCommittedTree` at `RecordingStore.cs:1678-1741` rejects the replacement because `CountMissingBranchPointIds > 0`:

```csharp
// RecordingStore.cs:1701-1709
int missingRecordingIds = CountMissingRecordingIds(existing, incoming);
int missingBranchPointIds = CountMissingBranchPointIds(existing, incoming);
if (missingRecordingIds > 0 || missingBranchPointIds > 0)
{
    reason = $"incoming-missing-existing-ids missingRecordingIds={missingRecordingIds} ...";
    return false;
}
```

The rejected commit means the active tree (with fork) does not enter `committedTrees`. The committed tree's `Recordings` dict still holds the pre-Re-Fly contents (no fork).

The asymmetry is intentional for the legitimate case (don't lose BPs from a stale incoming tree) but wrong for the Re-Fly active-tree merge case where the active tree may legitimately have fewer BPs than the committed copy (BPs from sibling sub-trees, group hierarchy, etc., may have been pruned during the Re-Fly session). The fix is to make the Re-Fly merge **union** the two trees' content rather than reject the incoming wholesale.

**(2b) `MergeJournalOrchestrator.RunMerge` does not call `CommitTree` on the active tree.** The orchestrator at `MergeJournalOrchestrator.cs:194-298` runs Begin → Split → Supersede → Tombstone → Finalize → Durable1Done → RpReap → MarkerCleared → Durable2Done. **None of these phases migrate the fork from the active tree into a committed tree.** The splitter (`RecordingTreeSplitter.SplitOriginAtRewindUT` at `RecordingTreeSplitter.cs:319-757`) mutates the **committed** tree (it calls `FindCommittedTreeById(origin.TreeId)`) — inserting TIP, retagging BPs, etc. — but never reaches across to the active tree to pull the fork in.

The supersede row `TIP → fork` (trace line 68404) names `rec_12b7252f` (the fork) as `newRecordingId`, but the fork's RECORDING node lives only in the active tree and never makes it into the committed tree's `Recordings` dict.

**(2c) The committed tree's `activeRecordingId` is never promoted to the fork.** The splitter's Step 12 invariant log at `RecordingTreeSplitter.cs:1007-1012` checks whether `tree.ActiveRecordingId == origin.RecordingId` (HEAD) and logs a verbose-level "id preservation kept the active recording pointer valid" message. But for an in-place continuation, **the active recording is conceptually the fork, not HEAD**. The committed tree's `activeRecordingId` is left pointing at HEAD (= origin's id) after the split, which is exactly what we observe in persistent.sfs line 648.

### Fix design

**(2a) Relax `ShouldReplaceCommittedTree` for active-Re-Fly commits.** Add a new helper (or thread a `bool isReFlyActiveCommit` parameter through) that, when the live `ParsekScenario.ActiveReFlySessionMarker` references this tree's id, **merges** the incoming tree's `Recordings` and `BranchPoints` into the existing committed tree instead of replacing-or-skipping. The merge semantics:

- For each key in `incoming.Recordings.Keys` not in `existing.Recordings`: add it.
- For each key in `incoming.Recordings.Keys` AND in `existing.Recordings`: overwrite (the incoming version is what we want — call `existing.Recordings[id] = incoming.Recordings[id]`).
- For each key in `existing.Recordings.Keys` not in `incoming.Recordings`: KEEP. This is the "incoming-missing-existing" case PR #872's invariant currently fails on. Pre-rewind debris that the active session never touched legitimately stays in `existing` but not in `incoming` because the active tree was pruned by `Quickload trim scope = ActiveRecOnly` (see KSP.log line 71407).
- Same union semantics for `BranchPoints` (by id).
- Same union semantics for `RootRecordingId` (keep existing if non-null) and `ActiveRecordingId` (overwrite from incoming when an active Re-Fly marker exists).

New helper:

```csharp
/// <summary>
/// Merge active-Re-Fly tree state into the existing committed tree instead
/// of the strict ShouldReplaceCommittedTree gate. The active session may have
/// pruned BPs / recordings (trim-scope=ActiveRecOnly), so the incoming tree
/// is by construction a partial view; the union preserves the pre-Re-Fly
/// content alongside the session's new fork.
/// </summary>
private static bool TryUnionActiveReFlyTreeIntoCommitted(
    RecordingTree existing, RecordingTree incoming, ReFlySessionMarker marker)
```

Called from `CommitTree` at `RecordingStore.cs:876-885`. **The union mutates the existing committed tree in place and reassigns the `tree` parameter so the 6 downstream helpers (`ApplySessionMergeToRecordings` / `ApplyRewindProvisionalMergeStates` / `PromoteNormalStagingRewindPoints` / `AutoGroupTreeRecordings` / `AdoptOrphanedRecordingsIntoTreeGroup` / `MarkSupersededTerminalSpawnsForContinuedSources` at `RecordingStore.cs:909-916`) all see the post-union tree as their input**. Without that reassignment they would still operate on the incoming (partial) tree and miss the pre-existing recordings/BPs the union just merged back in.

```csharp
if (!ShouldReplaceCommittedTree(committedTrees[i], tree, out var replaceReason))
{
    var liveMarker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
    if (liveMarker != null
        && string.Equals(liveMarker.TreeId, tree.Id, StringComparison.Ordinal)
        && TryUnionActiveReFlyTreeIntoCommitted(committedTrees[i], tree, liveMarker,
              out int addedRecs, out int addedBps, out bool activeIdSwapped))
    {
        ParsekLog.Info("RecordingStore",
            $"CommitTree: unioned active-Re-Fly incoming tree id='{tree.Id}' " +
            $"into existing committed tree (reason={replaceReason}, " +
            $"addedRecordings={addedRecs} addedBranchPoints={addedBps} " +
            $"activeRecordingIdSwapped={activeIdSwapped})");
        // Reassign `tree` to the existing (now-unioned) committed tree so
        // the subsequent helpers at :909-916 operate on the canonical
        // post-union object, not the partial incoming view. The existing
        // tree is the live committed object referenced from
        // committedTrees[i] and is now mutated in place.
        tree = committedTrees[i];
        // Continue past the duplicate-skip into FinalizeTreeCommit's full
        // pipeline below, with replaceCommittedTreeIndex pointing at the
        // existing slot so FinalizeTreeCommit's "updated committed tree"
        // path runs (BumpStateVersion + Verbose-log replace summary).
        // The 6 downstream helpers all then see the canonical tree.
        replaceCommittedTreeIndex = i;
        // Skip the legacy duplicate-skip branch; fall through to the
        // post-loop FinalizeTreeCommit call.
        break;
    }
    Log($"[Parsek] WARNING: Tree '{tree.Id}' already committed — skipping duplicate");
    ParsekLog.Verbose("RecordingStore",
        $"CommitTree: duplicate tree id='{tree.Id}' skipped reason={replaceReason}");
    GameStateRecorder.PendingScienceSubjects.Clear();
    ClearRewindReplayTargetScope();
    return;
}
```

The `break` exits the `for (i = 0; i < committedTrees.Count; i++)` outer loop, falling through to the existing `FinalizeTreeCommit(tree, replaceCommittedTreeIndex)` call at `RecordingStore.cs:915`. With `replaceCommittedTreeIndex` set to `i` and `tree` already aliased to `committedTrees[i]`, `FinalizeTreeCommit`'s "updated committed tree" branch (`updatedCommittedTree = true` at line 1399) takes over — its loop over `tree.Recordings.Values` then re-adds any flat-list-missing entries (which is fine, since the union just made them all present in `tree.Recordings`) and bumps the state version.

**Note**: `TryUnionActiveReFlyTreeIntoCommitted` now takes three `out` parameters so the post-union `Info` log has the actual counters. The helper signature:

```csharp
private static bool TryUnionActiveReFlyTreeIntoCommitted(
    RecordingTree existing, RecordingTree incoming, ReFlySessionMarker marker,
    out int addedRecs, out int addedBps, out bool activeIdSwapped)
```

**(2b) New journal phase `Phases.TreeMerge` between Begin and Split.** Add the constant in `MergeJournal.cs:69` (between Begin and Split) and a new step in `MergeJournalOrchestrator.RunMerge` at `MergeJournalOrchestrator.cs:210-232` that explicitly migrates the active tree's fork-side recordings into the committed tree BEFORE the splitter runs:

```csharp
// Step 1.4: active-tree → committed-tree fork migration.
// Before the splitter touches the committed tree, ensure the session's
// fork recording (marker.ActiveReFlyRecordingId) is in tree.Recordings.
MigrateActiveReFlyForkIntoCommittedTree(marker, provisional, scenario);
AdvancePhase(scenario, MergeJournal.Phases.TreeMerge);
DurableSave("treemerge", persistSynchronously: true);
MaybeInject(Phase.TreeMerge);
```

`MigrateActiveReFlyForkIntoCommittedTree` lives in `MergeJournalOrchestrator.cs` (or as a new `SupersedeCommit` helper):

0. **Precondition — `marker.InPlaceContinuation == true`.** Non-in-place sessions follow a different fork lifecycle: the new flight is captured as a fresh recording by the recorder, which creates/owns its own tree, and the supersede row points from `priorTip` (in `marker.TreeId`) to a fork in a separate tree. Migration into `marker.TreeId`'s committed tree would be incorrect there. If `marker.InPlaceContinuation == false`, log `Verbose("Journal", "MigrateActiveReFlyForkIntoCommittedTree: skipped (non-in-place session, fork lives in own tree)")` and return. (Source check: `RewindInvoker.cs:1195-1247` shows `EnsureForkAttachedToTree` runs only inside `if (inPlaceContinuation)`; non-in-place forks have no eager tree-attach at marker-write time.)
1. Locate the committed tree by `marker.TreeId` via `RecordingStore.CommittedTrees`. If none, log a Warn and return (degenerate case — the original tree was discarded; the merge cannot complete).
2. Locate the active tree via `ParsekFlight.Instance?.ActiveTreeForSerialization` matching the same `TreeId`. If both committed and active exist as distinct objects, call the union helper from (2a) with the active tree as `incoming`.
3. Ensure `provisional` (the fork Recording object) is in `committedTree.Recordings`. If not, call `committedTree.AddOrReplaceRecording(provisional)` and `committedTree.RebuildBackgroundMap()`.
4. Update `committedTree.ActiveRecordingId = provisional.RecordingId` so OnSave's RECORDING_TREE node has the correct active pointer (Bug 2c fix folds in here).
5. Log:

```csharp
ParsekLog.Info("Journal",
  $"MigrateActiveReFlyForkIntoCommittedTree: fork={provisional.RecordingId} " +
  $"committedTreeRecsBefore=N committedTreeRecsAfter=M " +
  $"activeRecordingIdBefore={oldActiveId} activeRecordingIdAfter={provisional.RecordingId} " +
  $"unionedRecs=X unionedBps=Y sess={marker.SessionId}");
```

**(2c) Promote `tree.ActiveRecordingId` to TIP, not HEAD (refinement on 2b).** Re-read of Step 12 in the splitter (`RecordingTreeSplitter.cs:1007-1012`) reveals the existing log just observes that the pointer points at HEAD. The actual contract should be:

- **For in-place continuation merges** (the session 1 case): after the merge tail, `tree.ActiveRecordingId` should point at the fork (since the fork is the new tip of the in-place continuation, conceptually replacing HEAD as "what the user is doing now"). This is what (2b) step 4 above implements.
- **For new-recording-path merges** (no in-place fork — the new flight is a separate Recording from origin): `tree.ActiveRecordingId` should point at the fork's id too, for the same reason.

In both cases the splitter's Step 12 should be REPLACED, not just logged. Update `RecordingTreeSplitter.cs:1007-1012` to actively set:

```csharp
// Step 2.12: promote tree.ActiveRecordingId to TIP if it currently
// references HEAD (origin.RecordingId). After the split, HEAD covers
// the pre-rewind portion and is no longer "the live recording"; TIP
// is the post-rewind continuation. For in-place merges the fork
// (provisional) also lives in the same tree and a later
// MigrateActiveReFlyForkIntoCommittedTree step will refine this to
// the fork id; for non-in-place merges TIP is correct.
if (tree != null
    && string.Equals(tree.ActiveRecordingId, origin.RecordingId, StringComparison.Ordinal))
{
    string priorActive = tree.ActiveRecordingId;
    tree.ActiveRecordingId = tip.RecordingId;
    snapshot.PreSplitActiveRecordingId = priorActive;
    snapshot.ActiveRecordingIdMutated = true;
    ParsekLog.Info(Tag,
        $"Step12: promoted tree.ActiveRecordingId {priorActive} -> {tip.RecordingId} " +
        $"(HEAD -> TIP after split; in-place fork migration may further promote to fork)");
}
```

Add `PreSplitActiveRecordingId` and `ActiveRecordingIdMutated` to `SplitSnapshot` (`RecordingTreeSplitter.cs:64-159`) and undo this mutation in `RollBackInMemory` (line 1049-).

### Crash-recovery

The new `TreeMerge` phase fits between `Begin` and `Split`. `RunMerge` sequence:

1. `phase=Begin` + `DurableSave("begin")` — barrier 1 (existing).
2. `MigrateActiveReFlyForkIntoCommittedTree(marker, provisional, scenario)` — new.
3. `AdvancePhase(TreeMerge)` + `DurableSave("treemerge")` — barrier 2 (new).
4. `SplitOriginAtRewindUT(…)`, `AdvancePhase(Split)` + `DurableSave("split")` — barrier 3 (existing, was barrier 2).
5. …Supersede / Tombstone / Finalize / Durable1Done… (existing).

| Crash point | Journal phase on disk | OnLoad finisher action |
|---|---|---|
| Mid-Begin | `Begin` (no journal yet) or `Begin` (post-DurableSave) | `RunFinisher` dispatches `phase=Begin → RollBack` per existing logic. In-memory state is irrelevant; pre-merge snapshot is what reloads. |
| Mid-TreeMerge, BEFORE `DurableSave("treemerge")` | `Begin` | `RollBack` runs (same as today's mid-Begin path). The in-memory `MigrateActiveReFlyForkIntoCommittedTree` partial mutation is discarded on reload — nothing on disk needs undoing because the durable barrier hasn't fired. Safe. |
| Post-TreeMerge-DurableSave (TreeMerge mutations now persisted) | `TreeMerge` | `IsKnownPostBeginPhase(TreeMerge) → true`, dispatch goes to `CompleteFromPostDurable`. The new `if (journal.Phase == TreeMerge)` block re-runs `MigrateActiveReFlyForkIntoCommittedTree` (idempotent — see below) then advances to `Split` and falls through. |
| Mid-Split (post-TreeMerge-Durable, pre-Split-Durable) | `TreeMerge` | Same as above: re-run migrate (idempotent) → re-run split (idempotent per r3 of fix-supersede-identity-scope). |

**Idempotency of `MigrateActiveReFlyForkIntoCommittedTree` on re-run:**

- `committedTree.Recordings[forkId] = fork` (via `AddOrReplaceRecording`) — overwrite-by-id; second call is a no-op identity overwrite.
- `committedTree.ActiveRecordingId = fork.RecordingId` — string assignment; second call is a no-op.
- `TryUnionActiveReFlyTreeIntoCommitted` — union semantics are idempotent: existing recordings remain (skip if key already present in existing AND already up-to-date by reference), branch points likewise.

Verify the idempotency explicitly with a unit test (see Implementation §Tests).

`MergeJournalOrchestrator.CompleteFromPostDurable` at `MergeJournalOrchestrator.cs:457-593` is a cascade of `if (journal.Phase == Phases.X)` blocks (NOT a `switch`), each advancing `journal.Phase` to the next step and falling through. The new `TreeMerge` block goes BEFORE the existing `Split` block, matching the established pattern:

```csharp
if (journal.Phase == MergeJournal.Phases.TreeMerge)
{
    // Re-run the migrate idempotently. AddOrReplaceRecording overwrites
    // by id; ActiveRecordingId swap is idempotent; the per-tree union
    // helper short-circuits when the fork is already present.
    MigrateActiveReFlyForkIntoCommittedTree(
        scenario.ActiveReFlySessionMarker,
        ResolveProvisional(scenario),
        scenario);
    AdvancePhase(scenario, MergeJournal.Phases.Split);
    stepsDriven++;
}
```

The block uses `journal.Phase` (the live value), not `fromPhase` (the entry value), so an entry at `TreeMerge` drives all the way through `Split → Supersede → … → Complete` per the existing W2/W5 reclassification (r7 fix in `fix-supersede-identity-scope`).

`MergeJournal.IsPostDurablePhase` (`MergeJournal.cs:106-117`) and `IsKnownPostBeginPhase` (`:131-142`) MUST both learn `TreeMerge`. Currently both list 9 phases (everything except `Begin`); the new addition makes them list 10:

```csharp
public static bool IsPostDurablePhase(string phase)
{
    return phase == Phases.TreeMerge   // NEW
        || phase == Phases.Split
        || phase == Phases.Supersede
        || phase == Phases.Tombstone
        || phase == Phases.Finalize
        || phase == Phases.Durable1Done
        || phase == Phases.RpReap
        || phase == Phases.MarkerCleared
        || phase == Phases.Durable2Done
        || phase == Phases.Complete;
}
```

Identical addition in `IsKnownPostBeginPhase`. `MergeJournalOrchestrator.RunFinisher` then dispatches `TreeMerge → CompleteFromPostDurable` automatically via the existing `IsKnownPostBeginPhase` branch — no additional dispatch wiring needed.

Also update `docs/parsek-rewind-to-separation-design.md` phase-classification table (the one fix-supersede-identity-scope r6 added) to add a `TreeMerge` row between `Begin` and `Split`.

## Implementation order

The split-phase "tests first, then impl" was unworkable because Phase 1 tests reference helpers that ship only in Phase 2. Instead, iterate per logical change: each helper lands in the same commit as the tests that exercise it. Test fixtures shared across both bugs (the `S11EvidenceBundle`) can ship in commit 1 because they have no dependency on the under-implementation code.

**Commit 1 — Shared test fixture**

- `Source/Parsek.Tests/Generators/S11EvidenceBundle.cs` — static helper that loads `logs/2026-05-16_2226_pr872-groups-investigation/s11-save-evidence/persistent.sfs` directly via `ConfigNode.Load` and exposes the deserialized `RECORDING`, `RECORDING_TREE`, `RECORDING_SUPERSEDE_RELATION`, and `LEDGER_TOMBSTONE` nodes. Pure-data accessor; does NOT call `ParsekScenario.OnLoad` (Planetarium + Unity GameEvents would crash outside the KSP runtime per the `reference_parsek_scenario_xunit.md` note).
- Smoke test verifying the bundle loads cleanly and the four flagged ids are present (`rec_12b7252f…`, `rec_675a9193…`, `rec_3cdedee5…`, the invalid supersede rows). `[Collection("Sequential")]`.

**Commit 2 — Bug 1 primary fix: `RewindInvoker.ReapPriorProvisionalsForRp` + tests**

Together (the tests cannot compile before the helper exists):
- `RewindInvoker.ReapPriorProvisionalsForRp` new helper + call site in `AtomicMarkerWrite` at `RewindInvoker.cs:1180-1190` (top of the `try` block, before `BuildProvisionalRecording`).
- `RewindAbandonRetryProvisionalReapTests.cs` — three tests, all `[Collection("Sequential")]`:
  - `ReapPriorProvisionalsForRp_RemovesPriorNotCommitted_LogsCount` — seed `committedRecordings` with two `NotCommitted` recordings on the same `provisionalForRpId`, different `creatingSessionId`. Call helper. Assert both removed, `[Parsek][INFO][ReFly] ReapPriorProvisional` logged for each, victim sidecar files deleted.
  - `ReapPriorProvisionalsForRp_DoesNotRemoveCommittedOnSameRp` — seed `committedRecordings` with one `Immutable` recording also tagged to the same RP. Call helper. Assert it survives.
  - `ReapPriorProvisionalsForRp_RemovesFromAllTreesAndPending` — seed the recording in both a `CommittedTrees[0].Recordings` dict and `PendingTree.Recordings` and `ParsekFlight.Instance?.ActiveTreeForSerialization?.Recordings` via the existing test fixtures. Assert all three dicts cleaned post-reap.

**Commit 3 — Bug 1 structural-leak fix: `LoadTimeSweep.RemoveDiscardRecordings` extension**

- Extend `LoadTimeSweep.cs:1133-1159` to also walk committed trees, pending tree, and active tree dicts (mirroring the reap-helper logic).
- Update the existing log site at `LoadTimeSweep.cs:1144-1148` to the 4-flag form (`removedFromFlatList=… removedFromCommittedTrees=… removedFromPendingTree=… removedFromActiveTree=…`). The existing assertion in `Source/Parsek.Tests/LoadTimeSweepTests.cs:767` is `Assert.Contains(logLines, l => l.Contains("[Rewind]") && l.Contains("Zombie discarded rec=rec_zombie"))` — the 4-flag log still begins with `Zombie discarded rec={id}`, so the existing substring assertion continues to pass with no test update. Verified during plan revision.
- New test `LoadTimeSweep_ExtendedRemoveDiscardRecordings_CleansAllCollections` extending the existing `LoadTimeSweepTests` file.

**Commit 4 — Bug 1 defenses: closure-walk + row-write guards**

- `EffectiveState.EnqueuePidPeerSiblings` and `EnqueueChainSiblings` (`EffectiveState.cs:1256-1346`) — add NotCommitted `Warn` guard in each.
- `SupersedeCommit.AppendRelations` (`SupersedeCommit.cs:184-410`) — add the tertiary row-write guard. Pattern: `#if DEBUG throw, else Warn-and-continue` to mirror `ValidateSupersedeTarget` at `SupersedeCommit.cs:225-236`.
- `AppendRelationsNotCommittedGuardTests.cs` — three tests:
  - `AppendRelations_SkipsNotCommittedPidPeer_LogsWarn`
  - `AppendRelations_SkipsNotCommittedChainSibling_LogsWarn`
  - `AppendRelations_RefusesNotCommittedRowAtWriteSite_WarnsInRelease_ThrowsInDebug` (`#if DEBUG` test arm asserts on the throw; release arm asserts on the warn).

**Commit 5 — Bug 1 regression test against the s11 evidence**

- `S11Bug1Regression_AbandonRetryProvisionalTests.cs` — load `S11EvidenceBundle`. Synthesize the abandon-and-retry sequence as a sequence of `AtomicMarkerWrite` calls (first session → call abandon path → second session). Assert: post-retry-marker-write, `RecordingStore.CommittedRecordings` does not contain `rec_675a9193`; after the simulated merge, `scenario.RecordingSupersedes` does NOT contain any row with `OldRecordingId == rec_675a9193`.

**Commit 6 — Bug 2c splitter active-id promotion**

- `RecordingTreeSplitter.SplitSnapshot` (`:64-159`) — new fields `PreSplitActiveRecordingId`, `ActiveRecordingIdMutated`.
- `RecordingTreeSplitter.SplitOriginAtRewindUT` Step 2.12 (`:1007-1012`) — actively promote `tree.ActiveRecordingId` from HEAD (`origin.RecordingId`) to TIP (`tip.RecordingId`).
- `RecordingTreeSplitter.RollBackInMemory` (`:1049-`) — restore `ActiveRecordingId` from snapshot if mutated.
- `RecordingTreeSplitterActiveTreePromotionTests.cs` — two tests:
  - `SplitOriginAtRewindUT_PromotesActiveRecordingIdToTip_LogsTransition`
  - `RollBackInMemory_RestoresPreSplitActiveRecordingId`

**Commit 7 — Bug 2a `CommitTree` union path**

- `RecordingStore.TryUnionActiveReFlyTreeIntoCommitted` — new private static helper.
- `RecordingStore.CommitTree` (`:859-918`) — relax the duplicate-skip gate when a live Re-Fly marker references the tree; reassign `tree = committedTrees[i]` and set `replaceCommittedTreeIndex = i` so downstream helpers see the canonical post-union tree (see §Fix design §2a).
- `CommitTreeUnionsActiveReFlyTreeTests.cs`:
  - `CommitTree_UnionsActiveReFlyTreeWhenIncomingMissingBps_PreservesFork`
  - `CommitTree_RejectsIncomingMissingBpsWithoutActiveReFly` (regression guard for the legitimate strict path)
  - `CommitTree_UnionedTreeFedThroughDownstreamHelpers_NotIncomingPartial` — assert `AutoGroupTreeRecordings` is invoked with a tree that contains both pre-existing pre-rewind debris AND the fork (i.e., the post-union object).

**Commit 8 — Bug 2b `TreeMerge` journal phase**

- `MergeJournal.Phases.TreeMerge` constant (`MergeJournal.cs:69`).
- `MergeJournal.IsPostDurablePhase` (`:106-117`) and `IsKnownPostBeginPhase` (`:131-142`) — both learn `TreeMerge`.
- `MergeJournalOrchestrator.MigrateActiveReFlyForkIntoCommittedTree` — new private static helper. Precondition: `marker.InPlaceContinuation == true` (else Verbose-skip).
- `MergeJournalOrchestrator.RunMerge` (`:210-232`) — insert Step 1.4 invoking the helper, `AdvancePhase(TreeMerge)`, `DurableSave("treemerge", persistSynchronously: true)`, `MaybeInject(Phase.TreeMerge)`. Also add a new `Phase` enum entry **between `Begin` and `Split`** in the crash-injection harness enum at `MergeJournalOrchestrator.cs:83-96` so the string-constant order matches the enum order.
- `MergeJournalOrchestrator.CompleteFromPostDurable` (`:457-593`) — new `if (journal.Phase == MergeJournal.Phases.TreeMerge)` block BEFORE the existing `Split` block, runs the migrate helper idempotently and advances to `Split` (see §Fix design §H1).
- `MergeJournalForkMigrationTests.cs`:
  - `MigrateActiveReFlyForkIntoCommittedTree_InPlace_AddsForkAndUpdatesActiveId`
  - `MigrateActiveReFlyForkIntoCommittedTree_NonInPlace_VerboseSkip` (precondition guard)
  - `MigrateActiveReFlyForkIntoCommittedTree_Idempotent_SecondRunNoChange` — call twice, assert tree.Recordings.Count unchanged after the second call, ActiveRecordingId unchanged, no extra log lines for the no-op second call.
  - `RunFinisher_CrashAtTreeMerge_DrivesForwardThroughSplit` — set journal.Phase = TreeMerge on disk, invoke RunFinisher, assert it dispatches to CompleteFromPostDurable, runs the migrate helper, then advances through Split → Supersede → … → Complete.

**Commit 9 — Bug 2 end-to-end regression**

- `S11Bug2Regression_ForkPersistTests.cs` end-to-end test using `MergeJournalOrchestrator.DurableSaveForTesting` and `MergeJournalOrchestrator.SaveGameForTesting` hooks at `MergeJournalOrchestrator.cs:629` to intercept the `GamePersistence.SaveGame` call (which requires `HighLogic.CurrentGame` and is unreachable from xUnit otherwise). The test installs in-memory delegates that capture the post-merge `ParsekScenario` state into a `ConfigNode` tree, then parses it back and asserts:
  - Tree `cde7313fc6574f789d234dca7fb6a45a` contains `rec_12b7252f4bdc4f2188d5877da9f39431` as a `RECORDING` node.
  - The tree's `activeRecordingId = rec_12b7252f4bdc4f2188d5877da9f39431`.
  - The supersede row `oldRecordingId = 98616eff… → newRecordingId = rec_12b7252f…` is present and points at a tree-resident fork (not a dangling id).

**Commit 10 — Documentation + phase-classification table**

- `docs/parsek-rewind-to-separation-design.md` — phase-classification table gains a `TreeMerge` row between `Begin` and `Split`. Update barrier-count language anywhere it appears ("5 durable barriers" → "6 durable barriers" / "10 named phases" — see §Risk + rollout terminology note).
- `CHANGELOG.md` and `docs/dev/todo-and-known-bugs.md` per the per-commit doc-update convention.

## Risk + rollout

### What could break

- **`TryUnionActiveReFlyTreeIntoCommitted`** changes the `CommitTree` contract for any caller that depends on the strict "incoming must be richer or equal" invariant. Grep call sites of `CommitTree` (8 files per earlier search) and ensure none of them are passing a deliberately-pruned tree expecting rejection. Most look like `MergeDialog.MergeCommitButton`, `SmoothingPipeline`, `BackgroundRecorder`, etc. — none of these should be passing pruned trees outside of an active Re-Fly session.
- **`MigrateActiveReFlyForkIntoCommittedTree`** mutating `committedTree.Recordings` mid-merge could race with other tree consumers. The merge journal is synchronous (no `yield`), so this is safe inside `RunMerge`. The post-Durable1 finisher path is also synchronous. The risk is: a third-party (Harmony patch, etc.) iterating `committedTree.Recordings` during the merge — but this is already true for the splitter's TIP insert at `RecordingTreeSplitter.cs:716-718`, so no new risk class.
- **`NotCommitted` guards in `EnqueueChainSiblings` / `EnqueuePidPeerSiblings`** could over-filter for a future feature that intentionally puts `NotCommitted` recordings into closures. None today.
- **`LoadTimeSweep.RemoveDiscardRecordings` tree-dict cleanup** could legitimately remove a recording that another component still holds a reference to. Grep `Recording rec_` patterns for caches.

### Verification

- `dotnet test --filter S11Bug` — both regressions must pass.
- `dotnet test` full suite — no existing test regressions.
- `pwsh scripts/validate-ksp-log.ps1` — log format unchanged.
- Manual in-game test: load the s11 save, take the same six-Re-Fly sequence the playtest took. Verify persistent.sfs after each merge: tree contains the fork, no NotCommitted orphan rows, no `old=<NotCommitted-id>` supersede rows. Use `InGameTests/RuntimeTests.cs` to add an automated runtime check.
- Grep for `ShouldReplaceCommittedTree` callers and confirm none rely on the strict reject for the Re-Fly path.

### Whether the merge journal needs a new phase

**Yes — `TreeMerge` between `Begin` and `Split`.** It must be a durable phase (own `DurableSave`) because:
- The splitter (next phase) operates on the committed tree's contents and assumes the fork is reachable via tree dict — `TreeMerge` is what makes that true. If `TreeMerge` ran but didn't durably persist, a mid-Split crash would resume with `Phase = Begin` on disk and skip the migration when re-running forward.
- Idempotent re-run is fine: by-id dict insertions and an `ActiveRecordingId` swap are both idempotent (see §Crash-recovery).

Phase count grows from 10 to 11. Durable-save barrier count grows from 5 (`begin / split / durable1 / durable2 / final`) to 6 (`begin / treemerge / split / durable1 / durable2 / final`).

## Out of scope

- **PR #872's split logic itself** — validated in this investigation as working correctly; the splitter is not modified by Bug 2's fix except for the `ActiveRecordingId` promotion in Step 2.12 (which is a pre-existing TODO marker per the existing Step 12 verbose log).
- **The orphan-adopter cross-tree misroute** (separate prior issue) — not addressed here. The Bug 1 reap is keyed on `provisionalForRpId` matching the new session's RP, which the orphan-adopter case does not necessarily satisfy. If the orphan-adopter case shares the same RP id, Bug 1's fix subsumes it; if it crosses trees with different RP ids, a separate fix is needed.
- **Bug 1's tertiary defense** in `SupersedeCommit.AppendRelations` row-write — this is defense-in-depth only, not load-bearing. If the primary fix and secondary defense both work, the tertiary never fires.
- **`MergeJournal.Phases.Complete`** is not modified.

## Open questions / assumptions

Most of these were resolved during the clean-context plan review pass (see `fix-refly-abandon-and-fork-persist-review.md`). Remaining items are tagged as still-open below.

1. **`Recording.ProvisionalForRpId`** — **resolved**: `Recording.cs:266` declares `public string ProvisionalForRpId;` (PascalCase). Use `ProvisionalForRpId` in code.
2. **`ParsekFlight.ActiveTreeForSerialization`** — **resolved**: real internal property at `ParsekFlight.cs:565`, returns the live `activeTree`.
3. **`CommitTreeSceneExit (autoMerge off)` callers of `CommitTree`** — **resolved enough**: the union helper is keyed on the live marker reference, which makes the fix universal across any entry path. Still worth a grep audit of `CommitTree(` call sites during implementation as a sanity check, but no behavior is gated on the caller identity.
4. **`MigrateActiveReFlyForkIntoCommittedTree` for the non-in-place case** — **resolved**: the helper now has a precondition `marker.InPlaceContinuation == true` and Verbose-skips otherwise. Non-in-place forks are owned by their own (newly-created) tree from the start; migration into `marker.TreeId`'s committed tree would be incorrect. Verified at `RewindInvoker.cs:1195-1247`: `EnsureForkAttachedToTree` runs only inside the in-place branch.
5. **Relaxing `ShouldReplaceCommittedTree`'s `incoming-not-richer` gate** — **resolved**: the union helper bypasses both rejection reasons atomically when the live marker matches, so the asymmetry is handled uniformly.
6. **`activeRecordingId` semantics for chained nested Re-Fly** — **still open**: the s11 evidence has no nested-Re-Fly case (Re-Fly inside a Re-Fly). Bug 2c sets `ActiveRecordingId = fork.id` per merge; the composite walker added by fix-supersede-identity-scope r4+ resolves chained HEAD → TIP_1 → fork_1 → HEAD_2 → … if the contract composes correctly. Defer to a follow-up unless implementation surfaces a contradiction.
7. **Single fix covers both `rec_675a9193` and `rec_3cdedee5`** — **resolved**: identical shape per evidence summary (both NotCommitted, both have `ProvisionalForRpId` set, both abandoned-retry).

## Additional defenses + audits flagged by review pass

The clean-context review identified four items worth folding into the implementation, listed here so they're picked up without re-reading the review doc:

1. **`LoadTimeSweep` crash-resume of `AtomicMarkerWrite`-mid-flight orphans.** The Bug 1 crash-recovery argument relies on `LoadTimeSweep` reaping orphans on the next OnLoad if `AtomicMarkerWrite` itself crashed mid-reap. The existing `LoadTimeSweep` reaps NotCommitted recordings whose `creatingSessionId` has no live marker (`LoadTimeSweep.cs:1059-1131`). Verify the existing logic covers a partial-reap crash (it should: if the new session's marker was never written before the crash, the just-arrived prior provisionals' `creatingSessionId` still names a now-marker-less session and falls into the existing reap predicate). Add a unit test `LoadTimeSweep_ReapsCrashMidReapOrphans` exercising this case if the existing tests don't cover it.

2. **`RewindPointReaper` interaction with the new reap helper.** `RewindPointReaper.ReapOrphanedRPs()` at the `RpReap` phase reaps RPs but doesn't touch their provisional recordings. After Bug 1's fix, an RP being reaped during a merge could leave NotCommitted provisionals tagged to that RP id behind. By the time `RpReap` runs in the merge journal, the merge tail has already flipped the relevant provisionals to `CommittedProvisional`/`Immutable` (via `FlipMergeStateAndClearTransient` at the `Finalize` step), so the live merge case is fine. The risk is a crash between TreeMerge and Finalize where an RP is reaped on next OnLoad while a NotCommitted provisional still references it. Document this in the `MergeJournalForkMigrationTests` test commentary; add a sanity test asserting RP reap skips when any session-provisional recording still references the RP.

3. **Grep audit of all `FindRecording*` helpers in `RecordingStore.cs`** — confirm none cache a side-list that would survive the reap helper's removal pass. Per the review, `FindCommittedRecordingByIdForCarveOut` and `FindRecordingById` both resolve through `committedRecordings`, so the reap is sufficient. Adding the audit step explicitly so the implementor verifies before commit 2 ships.

4. **`Recording.ProvisionalForRpId` and `Recording.CreatingSessionId` source-of-truth grep.** Confirm both fields exist as declared (PascalCase, public) on `Recording.cs`. Quick `grep` before writing the helper.

- `Source/Parsek/RewindInvoker.cs` — `AtomicMarkerWrite` reap-prior-provisional hook (Bug 1 primary fix at `:1180-1190`).
- `Source/Parsek/MergeJournalOrchestrator.cs` — new `TreeMerge` phase + `MigrateActiveReFlyForkIntoCommittedTree` helper (Bug 2b) at `:210-232`; finisher learns new phase at `:267-320`.
- `Source/Parsek/RecordingStore.cs` — relaxed `CommitTree` union path for active-Re-Fly (Bug 2a) at `:859-918`, `ShouldReplaceCommittedTree` at `:1678-1741`; `RemoveCommittedInternal` semantics confirmed at `:728-734`.
- `Source/Parsek/LoadTimeSweep.cs` — tree-dict cleanup extension (Bug 1 structural leak fix) at `:1133-1159`.
- `Source/Parsek/RecordingTreeSplitter.cs` — `ActiveRecordingId` promotion in Step 2.12 (Bug 2c) at `:1007-1012`; `SplitSnapshot` field additions at `:64-159`; rollback path at `:1049-`.
