# Fix #431 — Events captured during a recording share the recording's commit/discard fate

**Branch:** `fix/431-event-purge-on-discard` (off `feat/416-career-window`)
**Worktree:** `C:/Users/vlad3/Documents/Code/Parsek/Parsek-431-event-purge-on-discard`
**Ships before:** #434 (revert auto-discard rebases on top of this).

## Problem

`GameStateEvent`s captured during a flight are tagged with the current `MilestoneStore.CurrentEpoch` and stored globally in `GameStateStore.Events`. Revert advances the epoch and an epoch filter hides the abandoned branch from milestone / ledger walks.

But `RecordingStore.DiscardPendingTree` (the merge dialog's Discard button path) does **not** advance the epoch and only clears `GameStateRecorder.PendingScienceSubjects` (`RecordingStore.cs:991`). Every other event captured during the flight — contract-accepted, tech-researched, crew status changes, facility upgrades, contract completions — stays in `GameStateStore.Events` with the current epoch, is picked up by the next milestone bundle, and sticks on the career ledger forever. The player sees a contract marked Completed (and its funds/rep credited) for a mission they explicitly discarded.

## Invariant to enforce

A `GameStateEvent` captured during the lifetime of a recording shares that recording's commit/discard fate. Commit → event stays. Discard → event is purged.

Events captured outside a live recording (at KSC, Tracking Station, or during the merge-dialog wait window after Abort Mission) stay untagged and belong to career between-mission state — they are never purged by `DiscardPendingTree`.

## Current-state map (as of 2026-04-17 on `fix/431-event-purge-on-discard`)

- `Source/Parsek/GameStateEvent.cs` — `public struct GameStateEvent` with fields `ut, eventType, key, detail, valueBefore, valueAfter, epoch`. `SerializeInto` / `DeserializeFrom` handle ConfigNode round-trip. New `recordingId` field goes here.
- `Source/Parsek/GameStateStore.cs:31` — `AddEvent(GameStateEvent e)` stamps `e.epoch = MilestoneStore.CurrentEpoch` before storing. This is the single funnel through which every event enters the store. New `e.recordingId` stamp goes here.
- `Source/Parsek/GameStateStore.cs:237` — `ClearEvents()` wipes the whole list. Stays as-is.
- `Source/Parsek/GameStateStore.cs:188` — `RemoveEvent(GameStateEvent target)` removes by `ut + eventType + key + epoch` match. Add `PurgeEventsForTree(IEnumerable<string> recordingIds, string reason)` helper with batch semantics.
- `Source/Parsek/GameStateRecorder.cs` — 17 `GameStateStore.AddEvent` call sites at lines 204, 251, 277, 302, 315, 365, 396, 446, 477, 568, 584, 629, 660, 691, 787, 1038, 1071. None of them set `recordingId` today; they rely on the AddEvent stamp.
- `Source/Parsek/RecordingStore.cs:981` — `DiscardPendingTree` today does `DeleteRecordingFiles` + `PendingScienceSubjects.Clear()`. Add the event-purge pass here before the files-delete.
- `Source/Parsek/MergeDialog.cs:107-121` — Discard button in the merge dialog calls `RecordingStore.DiscardPendingTree`; no change needed once the store-level purge is in place.
- `Source/Parsek/ParsekScenario.cs:970` — `MilestoneStore.CurrentEpoch++` on revert. Stays as the legacy epoch filter for now; retired in a later cleanup pass per the TODO.
- `Source/Parsek/ParsekFlight.cs:157` — `private RecordingTree activeTree`. Mutation sites for `activeTree.ActiveRecordingId`: `:1522, 1547, 1734, 1750, 2167, 2340, 5139, 5917, 6342` + `activeTree = new RecordingTree` at `:5395`. Each mutation is a transition point where the "currently live recording id" changes.
- `Source/Parsek/ParsekScenario.cs:764` — revert detection. After revert + auto-discard (#434), the purge runs through the same `DiscardPendingTree` path.

### Why per-recording, not per-tree

The TODO (line 221) calls out EVA-split chains: "if the parent recording commits but a child recording is discarded (or vice versa), only the discarded one's events are purged." The current merge dialog only offers Merge/Discard at the tree level, so the distinction is latent today, but the data granularity should be per-recording so finer slicing is possible later without another schema change. A whole-tree discard just translates to "purge events for every recording id in the tree" — cost is O(events × tree recordings), unchanged.

### What a "live recording" means for tagging

Tag an event with `recordingId` only when:

- `ParsekFlight.activeTree != null` AND `activeTree.ActiveRecordingId != null` — there is a live recorder on a leaf.

Do not tag when:

- No flight scene, no active tree (player at KSC / Tracking Station / Editor between missions).
- Active tree exists but no active leaf (background-only tree — the player switched to a vessel without recording).
- Pending tree is stashed in Limbo / LimboVesselSwitch during scene change (the live recorder is paused; events captured during the transition window belong to between-scenes state).
- The deferred-merge-dialog wait window (after Abort Mission, before the dialog surfaces) — `activeTree` is null outside flight, so this is automatic.

## Plan

### Step 1 — Add `recordingId` to `GameStateEvent`

```csharp
public struct GameStateEvent
{
    ...
    public string recordingId;   // null / empty for untagged career events

    public void SerializeInto(ConfigNode node)
    {
        ...
        if (!string.IsNullOrEmpty(recordingId))
            node.AddValue("recordingId", recordingId);
    }

    public static GameStateEvent DeserializeFrom(ConfigNode node)
    {
        ...
        e.recordingId = node.GetValue("recordingId") ?? "";
        return e;
    }
}
```

Existing saves deserialize cleanly with empty recordingId (pre-#431 events are treated as untagged career events — they have already committed, so this is the correct default).

### Step 2 — Live-recording context channel

Add a static context pair on `RecordingStore` so the static `GameStateStore.AddEvent` funnel can read it without a `ParsekFlight` reference:

```csharp
// In RecordingStore.cs
internal static string CurrentRecordingIdForTagging;  // null when no live recording

internal static void SetCurrentRecordingForTagging(string recordingId, string context)
{
    if (CurrentRecordingIdForTagging == recordingId) return;
    var prev = CurrentRecordingIdForTagging;
    CurrentRecordingIdForTagging = recordingId;
    ParsekLog.Verbose("RecordingStore",
        $"Tagging context: '{prev ?? "<none>"}' -> '{recordingId ?? "<none>"}' ({context})");
}
```

`ParsekFlight` calls `RecordingStore.SetCurrentRecordingForTagging(activeTree.ActiveRecordingId, reason)` at every `activeTree.ActiveRecordingId =` assignment site (listed above). One helper in `ParsekFlight` wrapping the assign + set keeps the call sites tidy.

Clear the context on:
- `activeTree = null` (tree disposed after commit/discard).
- Scene change out of flight (before `StashActiveTreeAsPendingLimbo` runs — stashing pauses tagging).
- `RecordingStore.DiscardPendingTree` / `CommitPendingTree` (defensive — activeTree should be null by then, but make sure).

### Step 3 — Stamp on AddEvent

```csharp
// In GameStateStore.cs
internal static void AddEvent(GameStateEvent e)
{
    e.epoch = MilestoneStore.CurrentEpoch;
    if (string.IsNullOrEmpty(e.recordingId))
        e.recordingId = RecordingStore.CurrentRecordingIdForTagging ?? "";
    ...
}
```

Only auto-stamp when the caller left `recordingId` empty — preserves the ability to deliberately tag an event (e.g. test fixtures).

### Step 4 — Purge on discard

```csharp
// In GameStateStore.cs
internal static int PurgeEventsForRecordings(ICollection<string> recordingIds, string reason)
{
    if (recordingIds == null || recordingIds.Count == 0) return 0;
    var set = recordingIds as HashSet<string> ?? new HashSet<string>(recordingIds);
    int removed = 0;
    for (int i = events.Count - 1; i >= 0; i--)
    {
        if (!string.IsNullOrEmpty(events[i].recordingId) && set.Contains(events[i].recordingId))
        {
            events.RemoveAt(i);
            removed++;
        }
    }
    // Contract snapshots referenced only by the purged events also go.
    int snapshotsRemoved = PurgeOrphanedContractSnapshots(/* collect purged-event keys */);
    ParsekLog.Info("GameStateStore",
        $"PurgeEventsForRecordings: removed {removed} events + {snapshotsRemoved} snapshots ({reason}, ids={recordingIds.Count})");
    return removed;
}
```

Extend `RecordingStore.DiscardPendingTree`:

```csharp
public static void DiscardPendingTree()
{
    if (pendingTree == null) { ... }

    var idsToPurge = new HashSet<string>();
    foreach (var rec in pendingTree.Recordings.Values)
        if (!string.IsNullOrEmpty(rec.RecordingId))
            idsToPurge.Add(rec.RecordingId);
    GameStateStore.PurgeEventsForRecordings(idsToPurge, $"DiscardPendingTree '{pendingTree.TreeName}'");

    foreach (var rec in pendingTree.Recordings.Values)
        DeleteRecordingFiles(rec);
    GameStateRecorder.PendingScienceSubjects.Clear();
    ...
}
```

Contract snapshots: `GameStateStore.contractSnapshots` is keyed by contract GUID. Walk the snapshots and remove any whose GUID matches a purged event's `key` (the accept/complete event's key is the contract GUID). Self-contained helper next to `PurgeEventsForRecordings`.

### Step 5 — Tests

`Source/Parsek.Tests/DiscardFateTests.cs` (new, `[Collection("Sequential")]`):

1. Captured event → committed tree → event survives. Synthetic: seed `CurrentRecordingIdForTagging = "rec-A"`, AddEvent contract-accepted, stash pending tree containing rec-A, CommitPendingTree, assert event still present.
2. Captured event → discarded tree → event purged. Same seeding, DiscardPendingTree, assert event gone, contract snapshot gone.
3. KSC event (no live recording) → tree discarded → event survives. Seed `CurrentRecordingIdForTagging = ""`, AddEvent, pending tree has different rec ids, DiscardPendingTree, assert event still present.
4. EVA-split tree (two recordings, two events one per rec) → whole-tree discard → both events purged.
5. Pre-#431 saved event (empty recordingId) → present on load → discard any pending tree → event survives.
6. Round-trip through Save/Load: captured event with recordingId round-trips through `GameStateEvent.SerializeInto` / `DeserializeFrom`, survives `GameStateStore.SaveEventFile` / `LoadEventFile`.

All tests use `ParsekLog.TestSinkForTesting` to assert the `[GameStateStore] PurgeEventsForRecordings` log line fires (or doesn't).

### Step 6 — Docs

- Mark #431 as ~~done~~ in `docs/dev/todo-and-known-bugs.md`.
- Add a one-line note in `docs/user-guide.md` under the Merge Dialog section: "Discard fully undoes the mission's career effects — any contracts, milestones, tech, and resource changes captured during the flight are reversed."
- CHANGELOG: one line, user-facing ("Discarding a recording now reverses the career effects (contracts, milestones, tech, funds) it captured.").

### Step 7 — Post-build checks

`dotnet build` + `dotnet test` from the worktree. Verify the deployed DLL contains a new distinctive UTF-16 string (e.g. `PurgeEventsForRecordings` or the user-facing discard message). Manual playtest recipe: accept a contract in flight, discard the recording via merge dialog, assert the contract is back to Available and funds/rep unchanged.

## Out of scope

- **Retiring the epoch filter.** TODO says ship as a cleanup pass after #431 + #432 prove out. Leave `MilestoneStore.CurrentEpoch++` on revert for now.
- **Retroactive purge of saves with pre-fix leaked events.** Document in CHANGELOG that only new discards are cleaned; already-committed stray events stay on the ledger.
- **Deleting an already-committed recording from the Recordings Manager.** The commit was the decision point. Those events are already bundled into milestones.
- **Per-leaf Discard in the merge dialog.** The data shape supports it, but the UI stays tree-level in this PR.
- **Gloops ghost-only recordings** — covered by #432 (Gloops never captures events), so the purge is a no-op for them.

## Risks

- **A `GameStateEvent` with empty `recordingId` loaded from an older save looks identical to a new untagged KSC event.** That is actually correct — those events have already committed to the career's ledger and should not be purged. Document this explicitly in the step-1 change.
- **Context channel staleness.** If `ParsekFlight` sets the context and then crashes / exits flight without clearing it, a later KSC event could be mis-tagged. Mitigation: clear in `OnDestroy` / scene change guards, and add a log assertion in tests that the context is null outside flight.
- **Contract snapshot purge heuristic.** Matching snapshot GUIDs to purged event keys is correct for contract accept/complete events but doesn't handle the edge case of a snapshot captured outside a recording and later referenced by one. In practice `AddContractSnapshot` is called from inside `OnContractAccepted` which fires during flight — same flight lifecycle as the event. Low risk but worth a test.
- **Performance.** Purging is O(events × recordings-in-tree). With N events / tree ~5-50 and typical event counts in the hundreds, this is negligible. Not worth an index.

## Sequencing with #434

This fix is the dependency. #434 (worktree `Parsek-434-revert-autodiscard`, branch `fix/434-revert-autodiscard`) adds `RecordingStore.DiscardPendingTree()` to the `isRevert` branch in `ParsekScenario.OnLoad`. Once this #431 work lands and merges into `feat/416-career-window`, rebase `fix/434-revert-autodiscard` on the updated base and implement per its plan doc. The combination gives: revert on crash report → auto-discard → events purged → clean career state, no ghost from the reverted mission.
