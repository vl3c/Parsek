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
- `Source/Parsek/GameStateStore.cs:31` — `AddEvent(GameStateEvent e)` stamps `e.epoch = MilestoneStore.CurrentEpoch` before storing. Single funnel into `events`. The resource-event coalescer at `:38-57` preserves the existing slot's `recordingId` and discards the incoming event's tag on match — that's a hazard the purge rule has to close (see step 3a).
- `Source/Parsek/GameStateStore.cs:237` — `ClearEvents()` wipes the whole list. Stays as-is.
- `Source/Parsek/GameStateStore.cs:188` — `RemoveEvent(GameStateEvent target)` removes by `ut + eventType + key + epoch` match. Add `PurgeEventsForRecordings(ICollection<string> recordingIds, string reason)` helper with batch semantics (step 4).
- `Source/Parsek/GameStateStore.cs:211` — `PruneProcessedEvents` removes from the live list based on the latest committed milestone's EndUT. Called from `ChainSegmentManager.cs:546`, `LedgerOrchestrator.cs:1569`, `ParsekFlight.cs:2464`. After a commit or flush creates a milestone, prune moves the events out of the store.
- `Source/Parsek/MilestoneStore.cs:42` — `CreateMilestone(recordingId, currentUT)` picks events by epoch + UT window + non-filtered type. The `recordingId` parameter tags the milestone; it does **not** filter which events are picked up. Flush path at `:114-118` calls `CreateMilestone(null, currentUT)` on every `OnSave` (`ParsekScenario.cs:517`), which means mid-flight F5 quicksave pulls tagged in-flight events into a `Committed=true` milestone — those events are then pruned from `GameStateStore.events` and are no longer visible to a later `PurgeEventsForRecordings` scan. This is the hole surfaced in the second review; see step 4b below for the fix.
- `Source/Parsek/GameStateRecorder.cs` — 17 `GameStateStore.AddEvent` call sites at lines 204, 251, 277, 302, 315, 365, 396, 446, 477, 568, 584, 629, 660, 691, 787, 1038, 1071. None of them set `recordingId` today; they rely on the AddEvent stamp.
- `Source/Parsek/RecordingStore.cs:981` — `DiscardPendingTree` today does `DeleteRecordingFiles` + `PendingScienceSubjects.Clear()`. Add the event-purge pass here before the files-delete.
- `Source/Parsek/MergeDialog.cs:107-121` — Discard button in the merge dialog calls `RecordingStore.DiscardPendingTree`; no change needed once the store-level purge is in place.
- `Source/Parsek/ParsekScenario.cs:970` — `MilestoneStore.CurrentEpoch++` on revert. Stays as the legacy epoch filter for now; retired in a later cleanup pass per the TODO.
- `Source/Parsek/ParsekFlight.cs:157` — `private RecordingTree activeTree`. Mutation sites for `activeTree.ActiveRecordingId`: `:1522, 1547, 1734, 1750, 2167, 2340, 5139, 5917, 6342` + the `new RecordingTree { ... ActiveRecordingId = rootRecId }` initializer at `:5401`. Each mutation is a transition point where the "currently live recording id" changes.
- `Source/Parsek/Milestone.cs:12` — `Milestone` already carries `RecordingId`. Milestones are only produced on commit (never on pending/discarded trees), so discard purge never needs to touch `MilestoneStore` — events that never became milestones just disappear when the store row is removed. The TODO's "milestones achieved" scope item is covered by the existing commit-gated milestone flow, not by this PR.
- `Source/Parsek/GameStateRecorder.cs:226` — `OnKscSpending` is the only ledger-write bypass around the event funnel, and it is gated by `!IsFlightScene()` (`:492`). During flight — the only path #431 needs to cover — every event goes through the `AddEvent` → purge-able store; no irreversible ledger write sneaks past.
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

### Step 2 — Central `Emit` funnel with explicit tag resolution (decision: reliability + observability)

Rejected the "static context channel that `AddEvent` reads implicitly" approach after review — hidden coupling, easy to leak in tests, drift-prone. Instead route every event through a single lit funnel in `GameStateRecorder` that resolves the tag at emit time, logs the stamp, and warns on drift.

```csharp
// GameStateRecorder.cs — new central funnel
internal static void Emit(GameStateEvent evt, string source = null)
{
    string tag = ResolveCurrentRecordingTag();
    if (string.IsNullOrEmpty(evt.recordingId))
        evt.recordingId = tag ?? "";

    ParsekLog.Verbose("GameStateRecorder",
        $"Emit: {evt.eventType} key='{evt.key}' tag='{evt.recordingId}' source='{source ?? ""}'");

    bool inFlight = HighLogic.LoadedScene == GameScenes.FLIGHT;
    bool midSwitch = RecordingStore.PendingTreeStateValue == PendingTreeState.LimboVesselSwitch;
    if (!inFlight && !midSwitch && !string.IsNullOrEmpty(tag))
        ParsekLog.Warn("GameStateRecorder",
            $"Emit drift: event '{evt.eventType}' tagged '{tag}' outside flight and outside LimboVesselSwitch — stale tag?");
    if (inFlight && string.IsNullOrEmpty(tag) && HasLiveRecorder())
        ParsekLog.Warn("GameStateRecorder",
            $"Emit drift: event '{evt.eventType}' in-flight with live recorder but empty tag");

    GameStateStore.AddEvent(evt);
}

// Test hook: tests substitute a fixed-id resolver so they don't need ParsekFlight alive.
internal static Func<string> TagResolverForTesting;

internal static string ResolveCurrentRecordingTag()
{
    if (TagResolverForTesting != null)
        return TagResolverForTesting() ?? "";

    // Primary: live active tree in flight.
    var live = ParsekFlight.GetActiveRecordingIdForTagging();
    if (!string.IsNullOrEmpty(live)) return live;

    // Secondary: pending tree mid-vessel-switch — events captured between stash and
    // restore belong to the outgoing recording's mission. See LimboVesselSwitch design note below.
    if (RecordingStore.PendingTreeStateValue == PendingTreeState.LimboVesselSwitch)
    {
        var pend = RecordingStore.PendingTree?.ActiveRecordingId;
        if (!string.IsNullOrEmpty(pend)) return pend;
    }
    return "";
}
```

`ParsekFlight.Instance` already exists (`ParsekFlight.cs:26`, set in `Start` at `:473`, cleared at `:847`). Only the `GetActiveRecordingIdForTagging()` accessor is new — `internal static string GetActiveRecordingIdForTagging() => Instance?.activeTree?.ActiveRecordingId ?? "";`. No extra lifecycle plumbing needed.

Call-site rewrite: every `GameStateStore.AddEvent(...)` inside `GameStateRecorder.cs` (17 sites at lines 204, 251, 277, 302, 315, 365, 396, 446, 477, 568, 584, 629, 660, 691, 787, 1038, 1071) switches to `Emit(...)`. `GameStateStore.AddEvent` stays — it's still the single in-process storage funnel — but only `Emit` feeds it during normal operation. Tests that want to insert raw events can still call `AddEvent` directly.

**LimboVesselSwitch window** — events captured during a vessel-switch stash belong to the outgoing recording (same mission, same commit/discard fate). The fallback in `ResolveCurrentRecordingTag` returns `RecordingStore.PendingTree.ActiveRecordingId` while in that state. On discard of the tree, those events are purged because their tag is one of the tree's recording ids. On commit (vessel-switch complete → normal flight), they survive.

**Clear hooks for `ParsekFlight.Instance` + logging of tag transitions** — log every `activeTree.ActiveRecordingId` assignment at Verbose with the previous and new value; clear `Instance` in `OnDisable` / `OnDestroy`. The clear is a belt-and-braces move — the resolver already handles a null Instance by returning the pending-tree fallback or "".

### Step 3 — Resource-coalescing must be tag-aware (second review, must-fix)

`GameStateStore.AddEvent` coalesces `FundsChanged / ScienceChanged / ReputationChanged` within `ResourceCoalesceEpsilon` (0.1s). Today the match at `GameStateStore.cs:38-57` ignores `recordingId`: if an untagged career slot sits in the store and a tagged in-flight event lands within epsilon, the merged slot keeps the empty tag and the incoming tag is dropped. That event then survives a later `DiscardPendingTree` purge — a silent leak.

Add tag equality to the coalesce match:

```csharp
if (existing.eventType == e.eventType &&
    Math.Abs(existing.ut - e.ut) <= ResourceCoalesceEpsilon &&
    string.Equals(existing.recordingId ?? "", e.recordingId ?? "", StringComparison.Ordinal))
{
    existing.valueAfter = e.valueAfter;
    events[i] = existing;
    ParsekLog.VerboseRateLimited("GameStateStore", "resource-coalesce",
        $"Coalesced {e.eventType} event at ut={e.ut:F2} tag='{e.recordingId}'");
    return;
}
```

If the tags differ, fall through and append as a new event. Cost: at most one extra event per epsilon window when the scene transitions between tagged and untagged; negligible.

### Step 3b — `GameStateStore.AddEvent` stamps epoch only

```csharp
// GameStateStore.cs
internal static void AddEvent(GameStateEvent e)
{
    e.epoch = MilestoneStore.CurrentEpoch;
    // recordingId was set by Emit (production) or the caller (tests).
    // No auto-stamp here — keeps the hidden-global anti-pattern out.
    ...
}
```

The event's `recordingId` is **always** set at `Emit` time. `AddEvent` does not touch it. Direct-test callers pass an explicit `recordingId` or leave it empty.

### Step 4 — Purge on discard (must walk milestones too)

Second review surfaced a hole: `MilestoneStore.FlushPendingEvents` (called on every `OnSave`) moves tagged in-flight events into a `Committed=true` milestone, and `PruneProcessedEvents` removes them from `GameStateStore.events`. An F5 quicksave mid-flight → merge-dialog Discard sequence would find nothing in the live events list and leak every captured event through the surviving milestone.

The fix is to walk both stores on purge. `MilestoneStore` needs a mutable accessor for the events list (or a purge helper of its own that `GameStateStore.PurgeEventsForRecordings` calls through).

```csharp
// In GameStateStore.cs
internal static int PurgeEventsForRecordings(ICollection<string> recordingIds, string reason)
{
    if (recordingIds == null || recordingIds.Count == 0) return 0;
    var set = recordingIds as HashSet<string> ?? new HashSet<string>(recordingIds);

    // 1. Live events list
    var liveRemoved = new List<GameStateEvent>();
    for (int i = events.Count - 1; i >= 0; i--)
    {
        if (!string.IsNullOrEmpty(events[i].recordingId) && set.Contains(events[i].recordingId))
        {
            liveRemoved.Add(events[i]);
            events.RemoveAt(i);
        }
    }

    // 2. Milestone event lists — covers the F5-then-discard path.
    // Any tagged events that were flushed into a milestone must also go;
    // if a milestone ends up empty it is discarded.
    var milestoneRemoved = MilestoneStore.PurgeTaggedEvents(set, reason);

    // 3. Contract snapshots — only for purged ContractAccepted events.
    //    See step 4a below.
    var allPurged = new List<GameStateEvent>(liveRemoved);
    allPurged.AddRange(milestoneRemoved);
    int snapshotsRemoved = PurgeOrphanedContractSnapshots(allPurged);

    ParsekLog.Info("GameStateStore",
        $"PurgeEventsForRecordings ({reason}): live={liveRemoved.Count}, milestone={milestoneRemoved.Count}, snapshots={snapshotsRemoved}, ids={recordingIds.Count}");
    return liveRemoved.Count + milestoneRemoved.Count;
}
```

```csharp
// In MilestoneStore.cs
internal static List<GameStateEvent> PurgeTaggedEvents(HashSet<string> recordingIds, string reason)
{
    var removed = new List<GameStateEvent>();
    int emptiedMilestones = 0;
    for (int i = milestones.Count - 1; i >= 0; i--)
    {
        var m = milestones[i];
        for (int j = m.Events.Count - 1; j >= 0; j--)
        {
            var e = m.Events[j];
            if (!string.IsNullOrEmpty(e.recordingId) && recordingIds.Contains(e.recordingId))
            {
                removed.Add(e);
                m.Events.RemoveAt(j);
            }
        }
        if (m.Events.Count == 0)
        {
            milestones.RemoveAt(i);
            emptiedMilestones++;
        }
    }
    if (removed.Count > 0 || emptiedMilestones > 0)
        ParsekLog.Info("MilestoneStore",
            $"PurgeTaggedEvents ({reason}): {removed.Count} events removed, {emptiedMilestones} milestones dropped");
    ResourceBudget.Invalidate();
    return removed;
}
```

The milestone walk is last-to-first so `RemoveAt` stays O(1) per remove, and empties are dropped in the same pass.

### Step 4a — Contract snapshot purge

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

Contract snapshots rule: `AddContractSnapshot` is only called from `OnContractAccepted` (`GameStateRecorder.cs:211`; verified — no other production callers). A snapshot belongs to its accept event. Deleting snapshots by "any purged event key" would break the case where a contract was accepted pre-flight (untagged event + untagged snapshot, both retained) and then completed/failed during the discarded recording — the completion event purges, the accept event survives, but the snapshot would wrongly go with it.

Correct rule: walk the combined purged-event list (live + milestone), find only `ContractAccepted` events inside it, collect their GUIDs, and delete snapshots for exactly those GUIDs. Snapshots belonging to retained `ContractAccepted` events are untouched.

```csharp
internal static int PurgeOrphanedContractSnapshots(List<GameStateEvent> purgedEvents)
{
    if (purgedEvents == null || purgedEvents.Count == 0) return 0;
    var guidsToRemove = new HashSet<string>();
    for (int i = 0; i < purgedEvents.Count; i++)
        if (purgedEvents[i].eventType == GameStateEventType.ContractAccepted &&
            !string.IsNullOrEmpty(purgedEvents[i].key))
            guidsToRemove.Add(purgedEvents[i].key);

    int removed = 0;
    for (int i = contractSnapshots.Count - 1; i >= 0; i--)
        if (guidsToRemove.Contains(contractSnapshots[i].contractGuid))
        {
            contractSnapshots.RemoveAt(i);
            removed++;
        }
    return removed;
}
```

`PurgeEventsForRecordings` captures the list of events it removes, then calls this helper with that list.

### Epoch filter cohabitation (decision: instrument, don't retire yet)

The legacy epoch filter lives at `MilestoneStore.cs:62` (the `if (e.epoch != epoch) { skippedEpoch++; continue; }` line inside `CreateMilestone`). Cohab log: when that branch fires for an event whose `recordingId` is non-empty AND the corresponding recording still exists as committed (via `RecordingStore.IsCommittedRecordingId(e.recordingId)` — a small helper), emit `ParsekLog.Warn` with both pieces of state. This surfaces drift where the two mechanisms disagree (e.g. an event filtered by epoch but still tagged with a live recording, or vice-versa).

The epoch filter itself stays in this PR; retiring it is a deliberate follow-up once #432 lands and the combined correctness has been playtested.

### Step 5 — Tests

`Source/Parsek.Tests/DiscardFateTests.cs` (new, `[Collection("Sequential")]`). All tests swap the tag resolver via `GameStateRecorder.TagResolverForTesting = () => "rec-A"` (or whatever) so they don't need `ParsekFlight` alive. Tests use `ParsekLog.TestSinkForTesting` to assert the `PurgeEventsForRecordings` / `Emit` log lines fire as expected.

1. Captured event → committed tree → event survives. Seed resolver = "rec-A", `Emit` a ContractAccepted, stash pending tree containing rec-A, `CommitPendingTree`, assert event still present AND event's `recordingId` is still "rec-A" (tag survives commit — invariant test for the TODO's "commit → event stays" promise).
2. Captured event → discarded tree → event purged. Same seeding, `DiscardPendingTree`, assert event gone, contract snapshot gone.
3. KSC event (no live recording) → tree discarded → event survives. Resolver returns "", `Emit` a ContractAccepted, pending tree has different recording ids, `DiscardPendingTree`, assert event still present.
4. EVA-split tree (two recordings, two tagged events, one per recording id) → whole-tree discard → both events purged.
5. Pre-#431 saved event (empty `recordingId` on load) → `DiscardPendingTree` for a tree whose recording ids are all non-empty → pre-existing empty-tagged event survives (guard against over-purge of untagged career events).
6. Round-trip through Save/Load: a `recordingId`-tagged event round-trips through `GameStateEvent.SerializeInto` / `DeserializeFrom` and survives `GameStateStore.SaveEventFile` / `LoadEventFile`.
7. **Contract snapshot heuristic coverage** — accept contract at KSC (untagged), complete it during discarded mission (tagged), discard. Assert the accept event survives, the completion event is purged, **and the snapshot stays** (snapshot belongs to the retained accept event). Mirror test: accept + complete both in the discarded mission → both events and snapshot purged.
8. **LimboVesselSwitch tagging** — pending tree in LimboVesselSwitch state with `ActiveRecordingId = "rec-A"`, flight scene false but switch in flight, `Emit` event → assert tagged with "rec-A"; no drift-warn log line fires.
9. **Drift warnings** — in flight with no active recording + no pending switch, `Emit` with a manually-set tag → warn log fires. Outside flight with no switch + empty tag → no warn.
10. **`UpdateEventDetail` preserves `recordingId`** — seed tagged event, call `UpdateEventDetail` to rewrite the detail, assert `recordingId` unchanged.
11. **Reset hygiene** — `ResetForTesting` hooks in `GameStateStore`, `RecordingStore`, `MilestoneStore`, and a `GameStateRecorder.TagResolverForTesting = null` clear work independently so test isolation holds.
12. **F5-then-discard invariant** (must-fix from second review) — seed resolver = "rec-A", Emit a ContractAccepted, call `MilestoneStore.FlushPendingEvents(currentUT + 1)` to mimic an F5 mid-flight, assert the event is now in a milestone and gone from `GameStateStore.Events`. Pending tree contains rec-A, `DiscardPendingTree`. Assert: event gone from the milestone, milestone itself dropped (it's now empty), contract snapshot gone.
13. **Resource-coalesce tag gate** (must-fix from second review) — Emit a tagged `FundsChanged` and an untagged `FundsChanged` within epsilon. Assert both present in the store as separate events (no silent merge). Repeat with two events same tag → merged to one slot.
14. **Mixed-tag milestone purge** — seed a single milestone with events tagged rec-A, rec-B, and one untagged. Discard a tree containing only rec-A. Assert rec-A events gone from the milestone, rec-B and untagged retained, milestone not dropped.

### Step 6 — Docs

- Mark #431 as ~~done~~ in `docs/dev/todo-and-known-bugs.md`.
- Add a one-line note in `docs/user-guide.md` under the Merge Dialog section: "Discard fully undoes the mission's career effects — any contracts, milestones, tech, and resource changes captured during the flight are reversed."
- CHANGELOG: one line, user-facing ("Discarding a recording now reverses the career effects (contracts, milestones, tech, funds) it captured.").

### Step 7 — Post-build checks

`dotnet build` + `dotnet test` from the worktree. Verify the deployed DLL contains a new distinctive UTF-16 string (e.g. `PurgeEventsForRecordings` or the user-facing discard message). Manual playtest recipe: accept a contract in flight, discard the recording via merge dialog, assert the contract is back to Available and funds/rep unchanged.

## Scope in light of the deterministic-timeline principle

The user's framing: on revert/discard, everything that happened **during the mission and directly caused by it** goes back with the mission — recordings, sub-trees (debris / dock / decouple children), science gathered, milestones achieved, crew EVAs or deaths. Career actions unrelated to any recording (e.g. unlocking a tech node at KSC) stay.

How #431 + #434 deliver that, without bloating scope:

- **Recording files + sub-trees** — `RecordingStore.DiscardPendingTree` already iterates `pendingTree.Recordings.Values` and calls `DeleteRecordingFiles(rec)` per leaf. Covered.
- **Science subjects + contract snapshots + every career event** — #431 adds the `recordingId` tag + `PurgeEventsForRecordings` + snapshot-by-accept heuristic. Covered.
- **Milestones** — `Milestone` carries `RecordingId` (`Milestone.cs:12`). Milestones are produced by `CreateMilestone` from either (a) the commit path (`RecordingStore.CommitTreeDirect` / `CommitTree`) or (b) the flush path called on every `OnSave` (`MilestoneStore.FlushPendingEvents` via `ParsekScenario.cs:517`). The flush path can scoop in-flight tagged events into a `Committed=true` milestone before the player's commit/discard decision has been made — so tagged events do appear in milestones even before (or without) commit. The purge therefore walks `MilestoneStore.Milestones` as well as `GameStateStore.Events` (see step 4). Not covered by construction — covered by the explicit milestone-purge helper.
- **Kerbal roster / funds / science / reputation state** — these live in KSP's save, not ours. On revert KSP restores them from the launch quicksave; on merge-dialog Discard KSP's state never changed (Parsek's ledger replay applies only committed recordings). Covered by KSP + the commit gate, no Parsek purge needed.
- **Ledger** — `LedgerOrchestrator.RecalculateAndPatch` runs on every scene load off the committed recordings; since the discarded tree never commits, it never entered the ledger. Covered by construction.

Everything above the dotted line the user listed is therefore either already handled today (recordings, sub-trees, science) or handled by the commit gate + KSP revert (milestones, kerbals, resources). The new work is just the event store + snapshot purge.

## Out of scope

- **Retiring the epoch filter.** Instrument the cohabitation via the drift-log (above) in this PR, but leave `MilestoneStore.CurrentEpoch++` on revert in place. Retirement ships as a deliberate follow-up once #432 lands and the combined behaviour has been playtested — removing it here would widen blast radius without a correctness win the instrumentation can't already surface.
- **Retroactive purge of saves with pre-fix leaked events.** Document in CHANGELOG that only new discards are cleaned; already-committed stray events stay on the ledger.
- **Deleting an already-committed recording from the Recordings Manager.** The commit was the decision point. Those events are already bundled into milestones.
- **Per-leaf Discard in the merge dialog.** The data shape supports it, but the UI stays tree-level in this PR.
- **Gloops ghost-only recordings** — covered by #432 (Gloops never captures events), so the purge is a no-op for them.

## Risks

- **A `GameStateEvent` with empty `recordingId` loaded from an older save looks identical to a new untagged KSC event.** That is actually correct — those events have already committed to the career's ledger and should not be purged. Documented in step 1.
- **`Emit` → `ParsekFlight.GetActiveRecordingIdForTagging` requires the singleton.** `ParsekFlight.Instance` is null outside flight scenes. The resolver's fallback chain (live → LimboVesselSwitch pending → "") handles that cleanly; drift-warn logs surface any case where the fallback is wrong.
- **Performance.** Purging is O(events × recordings-in-tree). With events in the hundreds and trees usually under 10 recordings, negligible. Not worth an index.

## Sequencing with #434

This fix is the dependency. #434 (worktree `Parsek-434-revert-autodiscard`, branch `fix/434-revert-autodiscard`) adds `RecordingStore.DiscardPendingTree()` to the `isRevert` branch in `ParsekScenario.OnLoad`. Once this #431 work lands and merges into `feat/416-career-window`, rebase `fix/434-revert-autodiscard` on the updated base and implement per its plan doc. The combination gives: revert on crash report → auto-discard → events purged → clean career state, no ghost from the reverted mission.
