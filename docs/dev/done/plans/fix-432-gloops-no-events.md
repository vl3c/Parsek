# Fix #432 — Gloops ghost-only recordings must not capture or apply any game events

**Branch:** `fix/432-gloops-no-events` (off `origin/main`)
**Worktree:** `C:/Users/vlad3/Documents/Code/Parsek/Parsek-432-gloops-no-events`
**Depends on:** #431 (PR #332, merged) — `GameStateRecorder.Emit` tagging funnel establishes ownership of events.
**Related:** #435 (new, aspirational) — multi-recording Gloops trees; #432's per-recording filter makes the tree case a no-op when #435 lands.
**Unrelated to:** #434 (PR #333, open) — shares naming convention only.

## Invariant to enforce

> A Gloops ghost-only recording has zero career-state footprint on the ledger. No ledger action is produced from any recording flagged `IsGhostOnly`. Events captured during a Gloops window flow through their existing #431 pipeline unchanged — tagged with the parallel normal recording's id (if any), otherwise empty-tagged as between-mission state. Gloops never owns events.

## Architectural context

Gloops is on track to be extracted as a standalone mod (see `docs/dev/gloops-recorder-design.md`). The extraction boundary keeps the trajectory recorder, ghost playback engine, and visual mesh builder in Gloops; Parsek (the consumer) keeps the tree/DAG structure, career state, resource ledger, crew reservation, and world presence (CommNet, tracking station). The recorder is **shared** — a single recording pipeline that both Gloops and Parsek feed from; Parsek then layers career state on top of the trajectory data via its metadata envelope around the Gloops `IPlaybackTrajectory` interface.

That extraction direction further justifies the apply-side-only design for #432:

- The recorder is Gloops's concern; career events are Parsek's concern. Guarding at the recorder level (a capture-side `Emit` suppression) would push a Parsek-only concept (the ledger) into the code that becomes Gloops, pulling the extraction boundary out of shape.
- The apply-side filter lives where the career ledger lives — entirely on the Parsek side of the future extraction. After extraction, Gloops has no notion of `IsGhostOnly` as a career-state suppressor; it just sees trajectories, some of which the consumer flagged for non-career rendering. Parsek on its side of the boundary filters out ghost-only recordings before generating ledger actions. No code needs to move across the boundary to preserve #432's behavior.

In short: capture-side guards would be anti-architecture. Apply-side guards match the extraction direction.

## Scope decision — apply-side only

The task brief and the original bug entry sketched both capture-side suppression (guard `GameStateRecorder.Emit`, short-circuit the science-subject and contract-snapshot pipelines) and apply-side codification (skip ghost-only recordings when producing ledger actions). After a world-model clarification from the user on 2026-04-17, the design is **purely apply-side**.

Rationale:

- A Gloops recording is the **visual ghost slice** of whatever is happening — a parallel ghost from point A at T0 to point B at T1, optimizer-post-processed, nothing more. It is not a flight, not a mission, not an owner of career events.
- When the player runs a Gloops recording in parallel with a normal Parsek mission recording (which is possible today — neither blocks the other), events that fire during that window belong to the **normal** recording and should share its commit/discard fate per #431. Suppressing them at Emit would wrongly drop career events from the normal recording too.
- When the player runs Gloops with no normal recording active (auto-record off, or a vessel not eligible for auto-record), events land in `GameStateStore.Events` with an empty tag — exactly the "between-mission career state" path #431 already models. On save, `MilestoneStore.FlushPendingEvents` bundles them into a Committed milestone, crediting the career as if Gloops weren't running. This is the intended and pre-existing behavior; #432 touches none of it.
- Gloops recordings commit via `RecordingStore.CommitGloopsRecording` (`RecordingStore.cs:394-418`), which never calls `LedgerOrchestrator.NotifyLedgerTreeCommitted`. So the `OnRecordingCommitted` event-to-action conversion path is not reached for Gloops today. The only leaks into the ledger are the **recording-walking migration paths** that don't check `IsGhostOnly`. That's the specific hole to close.

## Current-state map (as of 2026-04-17 on `fix/432-gloops-no-events`, post-#431)

### `IsGhostOnly` flag lifecycle

- `Source/Parsek/Recording.cs:29` — `public bool IsGhostOnly;` on `Recording`. Serialized only when true at `RecordingTree.cs:526-527`, deserialized at `RecordingTree.cs:953`.
- `Source/Parsek/FlightRecorder.cs:120` — `IsGloopsMode` flag on the recorder. Set once at `ParsekFlight.cs:7460` (`new FlightRecorder { IsGloopsMode = true }`). Read at `FlightRecorder.cs:4207, 4212, 4240, 4717, 5143` to skip resource baselines / rewind save / tree logic / auto-stop on vessel switch.
- `Source/Parsek/ParsekFlight.cs:7542` — `rec.IsGhostOnly = true` set at commit time inside `CommitGloopsRecorderData`. `RecordingStore.cs:402` re-asserts it.
- Read sites: `GhostPlaybackLogic.cs:3001` (loop-from-end semantics), `ParsekFlight.cs:9221` (`if (!rec.IsGhostOnly) warn + return` — delete-button guard inside `DeleteGhostOnlyRecording`, unrelated to spawn), `RecordingTree.cs:526, 953` (serialize/deserialize), `RecordingsTableUI.cs:1313, 2445` (X delete button + group display), `KerbalsModule.cs` (ghost-only chain handoff end-state fallback). **Note: there is no explicit `IsGhostOnly` gate on vessel-spawn-at-ghost-end anywhere in the code.** Gloops recordings don't spawn a real vessel at ghost-end because their commit path (`CommitGloopsRecording`) bypasses `NotifyLedgerTreeCommitted` entirely — so no `VesselSpawn` action is ever produced for a Gloops recording. This is an implicit consequence of the commit-path split, not an explicit per-spawn filter.

### Gloops is strictly single-recording today

Verified by audit 2026-04-17 (documented in the new `#435` TODO entry):

- `gloopsRecorder` is a single `FlightRecorder` instance with no `ActiveTree` (`ParsekFlight.cs:7460`).
- No `BackgroundRecorder` subscription for Gloops; staging produces no debris child.
- `FlightRecorder.HandleVesselSwitchDuringRecording` auto-stops Gloops on any vessel switch (`FlightRecorder.cs:5143-5151`); EVA produces no linked crew child.
- `RecordingStore.CommitGloopsRecording` accepts a single `Recording`, flat-groups it under `"Gloops - Ghosts Only"` (`RecordingStore.cs:394-418`).

So `#432`'s target surface is the single-recording case. The apply-side filter uses `rec.IsGhostOnly` per-recording, so when #435 (multi-recording Gloops trees) lands later, the filter handles trees automatically without re-touching #432.

### Apply-side action-creation paths

- `LedgerOrchestrator.OnRecordingCommitted` (`LedgerOrchestrator.cs:130-194`). Called **only** from `NotifyLedgerTreeCommitted` (`:1546`), `ChainSegmentManager.CommitSegmentCore`, and `ParsekFlight.FallbackCommitSplitRecorder`. **Never fires for Gloops** — `CommitGloopsRecording` does not go through `NotifyLedgerTreeCommitted`. The event-to-action convert at `:141` and science-to-action convert at `:151` therefore don't touch Gloops recordings today.
- `LedgerOrchestrator.CreateVesselCostActions` (`LedgerOrchestrator.cs:397-463`). Called from `OnRecordingCommitted` `:156`. Not reached for Gloops (above). Guard here is defense-in-depth: if a future code path routes a Gloops recording through `OnRecordingCommitted`, the guard keeps the ledger clean. `rec.PreLaunchFunds` is 0 for Gloops (Gloops skips `CapturePreLaunchResources` at `FlightRecorder.cs:4207-4208`), so the `buildCost > 0` gate at `:421` already wouldn't fire even without a guard; the `rec.TerminalStateValue == Recovered` recovery branch at `:439` is the live risk for a cleanly-landed Gloops ship.
- `LedgerOrchestrator.CreateKerbalAssignmentActions` (`LedgerOrchestrator.cs:471-510`). Called from two sites:
  - `:165` inside `OnRecordingCommitted` — not reached for Gloops.
  - `:800` inside `MigrateKerbalAssignments` (`LedgerOrchestrator.cs:769-840`), which walks **every** recording in `RecordingStore.CommittedRecordings` unconditionally. The function extracts crew via `ExtractCrewFromRecording` (`LedgerOrchestrator.cs:537-583`), which reads `rec.GhostVisualSnapshot ?? rec.VesselSnapshot` (`:543`) — not just `VesselSnapshot`. Gloops recordings populate **both** (`ParsekFlight.cs:7546` sets `GhostVisualSnapshot`, `:7550` sets `VesselSnapshot`), so the crew extraction hits the `GhostVisualSnapshot` branch first. **This is the real leak**: a Gloops recording of a crewed vessel today produces `KerbalAssignment` rows on every ledger load, reserving those kerbals for the Gloops loop duration. The user's earlier bug-entry phrasing "leaks into the ledger" is this specific path.
- `LedgerOrchestrator.PopulateUnpopulatedCrewEndStates` (`LedgerOrchestrator.cs:1040-1058`). Safety-net pass called from `RecalculateAndPatch` (`:651`) that walks every committed recording and mutates `rec.CrewEndStates` via `KerbalsModule.PopulateCrewEndStates(rec)` when needed. For a Gloops recording this does an in-memory mutation that serves no purpose (the CrewEndStates are never read for a recording that produces no actions), but it is **not** a ledger leak — no ledger row is produced. Intentionally **not guarded** by this PR: the mutation is harmless, the cost is trivial, and adding the guard would mean touching a third function that doesn't write to the ledger. Documented here so the next reader doesn't wonder why.
- `LedgerOrchestrator.RecalculateAndPatch` (`LedgerOrchestrator.cs:612-672`). Builds `actions` from `Ledger.Actions` at `:653`, sorts, walks. Action-driven — doesn't re-probe `IsGhostOnly`. Suitable place for the belt-and-braces ledger purge (mutates `Ledger.Actions`, so raw-ledger consumers see a clean list too).
- `RecalculationEngine.Recalculate` (`RecalculationEngine.cs:136-226`). Pure over a `List<GameAction>` — no change needed.

### Capture-side paths — unchanged

- `GameStateRecorder.Emit` (`GameStateRecorder.cs:58-83`) — no guard added. Events during a Gloops window are tagged via `ResolveCurrentRecordingTag()` with whatever the normal recorder's state says, exactly as they are today under #431.
- `OnContractAccepted` snapshot (`GameStateRecorder.cs:292`) — unchanged.
- `OnScienceReceived` / `PendingScienceSubjects.Add` (`GameStateRecorder.cs:832`) — unchanged.

### Interaction with #431's tagging — no change needed

- Events fired during a Gloops window with an active normal recorder get tagged with the normal recording's id. They share the normal recording's commit/discard fate. Discarding the normal recording purges them; committing it keeps them.
- Events fired during a Gloops window with no active normal recorder get empty tags. They flow into the between-mission career state via the save-time `FlushPendingEvents` path.
- **No event ever receives a Gloops recording's id as a tag** (a Gloops recording's id doesn't exist until commit, and `GetActiveRecordingIdForTagging` resolves to the normal recorder's active tree, never to the Gloops recorder). Therefore:
  > `GameStateStore.PurgeEventsForRecordings(idsToPurge)` called with a Gloops id in the set is a no-op by construction — the HashSet lookup at `GameStateStore.cs:277` (`set.Contains(e.recordingId)`) never matches.

Codified as a test case (test 5 below), not as runtime code.

## Plan

### Step 1 — Action-creation early-return on `rec.IsGhostOnly`

Both functions already call `FindRecordingById` (`LedgerOrchestrator.cs:592`) to resolve the `Recording` object and then read fields off it. Add an early-return right after the null check.

```csharp
// LedgerOrchestrator.cs:471 — CreateKerbalAssignmentActions.
// Insert the ghost-only guard after the existing "if (rec == null) return result;" at :477.
var rec = FindRecordingById(recordingId);
if (rec == null) return result;

if (rec.IsGhostOnly)
{
    ParsekLog.Verbose(Tag,
        $"CreateKerbalAssignmentActions: recording '{recordingId}' is ghost-only — skipping");
    return result;
}

if (NeedsCrewEndStatePopulation(rec)) ...
```

The guard sits above the existing `NeedsCrewEndStatePopulation` / `KerbalsModule.PopulateCrewEndStates` call on `:479-480`, which is a deliberate side-effect: a Gloops recording no longer triggers KSP roster lookups via that path. (The separate `PopulateUnpopulatedCrewEndStates` safety net at `:1040-1058` still hits it once per recalculation; see the current-state map above for why that's OK.)

```csharp
// LedgerOrchestrator.cs:397 — CreateVesselCostActions.
var rec = FindRecordingById(recordingId);
if (rec == null)
{
    ParsekLog.Verbose(Tag,
        $"CreateVesselCostActions: recording '{recordingId}' not found in CommittedRecordings — skipping");
    return result;
}
if (rec.IsGhostOnly)
{
    ParsekLog.Verbose(Tag,
        $"CreateVesselCostActions: recording '{recordingId}' is ghost-only — skipping");
    return result;
}

if (rec.Points.Count == 0) ...
```

These two early-returns close the real leak at `MigrateKerbalAssignments:800`. A Gloops-crewed recording on next ledger load returns an empty `desired` from `CreateKerbalAssignmentActions`; `KerbalAssignmentActionsMatch(existing, empty)` returns false when there are stale rows (from pre-fix builds); `Ledger.ReplaceActionsForRecording(KerbalAssignment, rec.RecordingId, empty)` cleans them out. One-shot self-heal with no extra code.

### Step 2 — Belt-and-braces purge in `RecalculateAndPatch` (mutates `Ledger.Actions`)

Remove any action whose `RecordingId` maps to an `IsGhostOnly` recording **from `Ledger.Actions` itself**, not just from a walk-time copy. Raw-ledger consumers — the Timeline window (`TimelineBuilder.cs:21` reads `ledgerActions`), the career-state windows, ledger serialization — all read `Ledger.Actions` directly. Filtering only the walk copy would leave those consumers with a dirty view. The purge runs at the top of `RecalculateAndPatch` (called on commit, rewind, warp exit, load), so the ledger stays clean across every normal lifecycle event.

```csharp
// LedgerOrchestrator.cs — inside RecalculateAndPatch, before the actions copy.
// #432: purge any ghost-only-tagged actions from the ledger before the walk.
// Mutates Ledger.Actions directly so raw-ledger consumers never see stale rows.
// Idempotent — no-op when no ghost-only recordings exist or no actions are
// tagged with them.
PurgeGhostOnlyActionsFromLedger();

var actions = new List<GameAction>(Ledger.Actions);
RecalculationEngine.Recalculate(actions);
```

```csharp
// LedgerOrchestrator.cs — new internal static helper.
internal static int PurgeGhostOnlyActionsFromLedger()
{
    var recs = RecordingStore.CommittedRecordings;
    if (recs == null || recs.Count == 0) return 0;

    var ghostOnlyIds = new HashSet<string>(StringComparer.Ordinal);
    for (int i = 0; i < recs.Count; i++)
    {
        var r = recs[i];
        if (r != null && r.IsGhostOnly && !string.IsNullOrEmpty(r.RecordingId))
            ghostOnlyIds.Add(r.RecordingId);
    }

    if (ghostOnlyIds.Count == 0) return 0;

    int removedTotal = 0;
    foreach (var id in ghostOnlyIds)
        removedTotal += Ledger.RemoveActionsForRecording(id);

    if (removedTotal > 0)
        ParsekLog.Info(Tag,
            $"PurgeGhostOnlyActionsFromLedger: removed {removedTotal} action(s) tagged with ghost-only recordings");

    return removedTotal;
}
```

```csharp
// Ledger.cs — new helper. Removes every action with the given RecordingId
// regardless of Type. Null/empty recordingId is a no-op. Used by the
// #432 purge + any future code that needs cross-type ledger cleanup.
internal static int RemoveActionsForRecording(string recordingId)
{
    if (string.IsNullOrEmpty(recordingId)) return 0;

    int removed = 0;
    for (int i = actions.Count - 1; i >= 0; i--)
    {
        if (string.Equals(actions[i].RecordingId, recordingId, StringComparison.Ordinal))
        {
            actions.RemoveAt(i);
            removed++;
        }
    }
    if (removed > 0)
        ParsekLog.Verbose("Ledger",
            $"RemoveActionsForRecording: recordingId='{recordingId}', removed={removed}, total={actions.Count}");
    return removed;
}
```

`Ledger.ReplaceActionsForRecording(type, id, new)` already exists but keys on (type, id) — useful for `MigrateKerbalAssignments` which owns only `KerbalAssignment`. For the ghost-only purge we need cross-type removal, hence the new helper.

Performance: O(recordings + actions) per call. `CommittedRecordings` is typically under 100 entries, `Ledger.Actions` under a few thousand. Reverse walk on `Ledger.Actions` makes each remove O(1). Negligible versus the existing sort + walk in `RecalculationEngine.Recalculate`.

### Step 3 — No capture-side changes

Confirmed by the 2026-04-17 clarification. `GameStateRecorder.Emit`, the `OnContractAccepted` snapshot call, and the `OnScienceReceived` `PendingScienceSubjects.Add` are untouched. Events flow through the #431 pipeline exactly as they do today.

### Step 4 — No accessor on `ParsekFlight`

Originally proposed `GetActiveRecordingGhostOnlyFlag()` / `IsGloopsCaptureActive()`. With the capture-side dropped, the accessor has no caller — the apply-side code already has the `Recording` object in hand and reads `rec.IsGhostOnly` directly. Dropped as dead code.

### Step 5 — Retroactive save cleanup covered by the purge

Pre-fix saves with stale ledger rows tagged with a ghost-only recording id (any action type — `KerbalAssignment`, `FundsSpending`, `FundsEarning`, etc.) are cleaned by the step-2 purge on the first `RecalculateAndPatch` after load. `OnKspLoad` calls `RecalculateAndPatch` after running `MigrateKerbalAssignments`, so the sequence on load is: reconcile → migrate-kerbal-assignments (type-specific rewrite for `KerbalAssignment`) → recalculate (cross-type ghost-only purge, then walk). The purge is a superset of the migration's ghost-only handling; the migration still does its own work for non-ghost-only recordings where it matters most.

Event-store side: events captured during pre-fix Gloops windows were already tagged correctly per #431 (with the normal recording's id, or empty for Gloops-solo) — Gloops never owned them in the first place. No cleanup needed.

### Step 6 — Tests

New `Source/Parsek.Tests/GloopsEventSuppressionTests.cs`. `[Collection("Sequential")]` — touches `RecordingStore`, `Ledger`, `LedgerOrchestrator` static state. `ParsekLog.TestSinkForTesting` captures log lines for assertions. File name kept despite the design flip so the test discovery tooling doesn't break; tests describe what the filter does.

1. **`create_kerbal_assignment_actions_returns_empty_for_ghost_only`** — build a synthetic `Recording` with `IsGhostOnly = true` and a `VesselSnapshot` containing one crew entry. Add to `committedRecordings`. Call `LedgerOrchestrator.CreateKerbalAssignmentActions(recId, startUT, endUT)`. Assert the result is empty and the Verbose `"is ghost-only — skipping"` line fired.
2. **`create_kerbal_assignment_actions_returns_rows_for_normal`** — same setup but `IsGhostOnly = false`. Assert the result contains one `KerbalAssignment` action.
3. **`create_vessel_cost_actions_returns_empty_for_ghost_only`** — synthetic `Recording` with `IsGhostOnly = true`, `PreLaunchFunds = 5000`, first-point funds 4000, `TerminalStateValue = Recovered`, last-point funds 9000, penultimate-point funds 5000. Call `CreateVesselCostActions`. Without the guard the result would hold a `FundsSpending` (cost=1000) + `FundsEarning` (recovery=4000); with the guard, asserts empty and the Verbose `"is ghost-only — skipping"` line fired.
4. **`PurgeGhostOnlyActionsFromLedger_DropsGhostOnlyRowsAndMutatesLedger`** — seed `committedRecordings` with one `IsGhostOnly = true` (id `"gloops-A"`) and one normal recording (id `"normal-B"`). Seed `Ledger.Actions` with three entries: one tagged `"gloops-A"`, one tagged `"normal-B"`, one with empty `RecordingId`. Call the helper directly. Assert `Ledger.Actions.Count == 2`, ghost-only row gone from `Ledger.Actions` itself (not just filtered out of a copy), normal and empty-tag rows retained. Empty-tag preservation is deliberate — audited against `InitialFunds` / `InitialScience` / `InitialReputation` seed actions and KSC-spending-forwarded actions at `LedgerOrchestrator.cs:625, 631, 637, 1350`, all of which legitimately use empty `RecordingId`.
5. **`PurgeGhostOnlyActionsFromLedger_PreservesMigratedOldSaveActionsWithNullTag`** — covers `MigrateOldSaveEvents` output (null `RecordingId`). Asserts null-tag `FundsSpending` survives alongside empty-tag seeds; ghost-only row is removed. Documents the intentional tradeoff that old-save migrated rows can't be mapped back to a recording and must be preserved.
6. **`PurgeGhostOnlyActionsFromLedger_NoGhostOnlyRecordings_Noop`** — ensures the helper is a clean no-op when no ghost-only recordings exist. `Ledger.Actions` unchanged, `removed == 0`.
7. **`PurgeGhostOnlyActionsFromLedger_RemovesAllTypesForGhostOnlyRecording`** — seeds three actions of different types (`KerbalAssignment` + `FundsSpending` + `FundsEarning`) all tagged to a ghost-only recording. Asserts all three removed. Differentiates from `ReplaceActionsForRecording`'s per-type semantics — the purge is cross-type.
8. **`RecalculateAndPatch_PurgesGhostOnlyActions_AndSecondCallIsNoop`** — seeds one ghost-only recording and one stale action tagged with its id. Triggers `RecalculateAndPatch`. Asserts the `"PurgeGhostOnlyActionsFromLedger: removed 1 action(s)"` Info log fired and the row is gone from `Ledger.Actions`. A second `RecalculateAndPatch` call does NOT fire the purge log — the ledger is already clean (contrast with the old walk-filter design where the log would fire every recalc).
9. **`RecalculateAndPatch_NoPurgeLog_WhenNoGhostOnlyRecordings`** — same but no ghost-only recording. Asserts the purge log did NOT fire, normal action survives.
10. **`PurgeEventsForRecordings_RemovesForcedGloopsTaggedEvent_LiveAndSnapshot`** — pins the `GameStateStore.PurgeEventsForRecordings` mechanism across live events + orphan contract snapshots. Forces a Gloops-tagged `ContractAccepted` via `GameStateRecorder.TagResolverForTesting`, adds a matching `ContractSnapshot`, asserts both are removed by the purge. The test exercises the purge path for a safety story — the default resolver never produces Gloops-tagged events in the first place (invariant held by architecture: `GetActiveRecordingIdForTagging` reads `activeTree.ActiveRecordingId`, which `CommitGloopsRecording` does not touch).
11. **`OnKspLoad_RewritesStaleGhostOnlyKerbalRowsToEmpty`** — simulates a pre-fix save with a stale `KerbalAssignment` row on a Gloops recording. Calls `OnKspLoad`. Asserts the row is gone (either via `MigrateKerbalAssignments` → `ReplaceActionsForRecording` OR via the subsequent `RecalculateAndPatch` → `PurgeGhostOnlyActionsFromLedger`; both run in sequence, test only pins the final state).

Tautological resolver test dropped — `ParsekFlight.GetActiveRecordingIdForTagging` reads `Instance?.activeTree?.ActiveRecordingId` and `Instance` is null in xUnit (no MonoBehaviour setup), so any assertion on its return value is trivially satisfied. The "Gloops never owns events" invariant is architectural (no code path assigns a Gloops id to `activeTree.ActiveRecordingId`, verified by code audit, documented in plan) rather than unit-testable without a full ParsekFlight/activeTree harness.

All tests use `ParsekLog.ResetTestOverrides()` + `LedgerOrchestrator.ResetForTesting` hooks in Dispose. Test setup adds synthetic recordings via `RecordingStore.AddRecordingWithTreeForTesting` (`:2170`). No new Gloops-side test hook is needed. `GameStateRecorder.TagResolverForTesting` is used only in test 10 to force-resolve a Gloops-tagged event into the store.

### Step 7 — Post-build verification

`dotnet build` + `dotnet test` from the worktree. Deploy and grep the deployed DLL for the new `"PurgeGhostOnlyActionsFromLedger"` or `"is ghost-only — skipping"` UTF-16 string to confirm the correct build is live. Manual playtest recipe:

1. Start a career save, launch a crewed plane.
2. Open Gloops window, Start Recording.
3. Fly the plane around for ~30 seconds, then Stop. Confirm the Gloops recording appears in the recordings table under `"Gloops - Ghosts Only"`.
4. Open the Career State / Kerbals windows. Confirm the plane's kerbal is **not** listed as reserved for the Gloops recording's duration.
5. Check `KSP.log` for `"CreateKerbalAssignmentActions: recording '<id>' is ghost-only — skipping"` (the Gloops recording's id) alongside the normal migration output.
6. Quickload an older save created on a pre-#432 build where a Gloops recording already has stale KerbalAssignment rows. Confirm the one-shot self-heal fires (`MigrateKerbalAssignments` repairs it).

### Step 8 — Docs updates (staged in the implementation commit per CLAUDE.md)

- **`CHANGELOG.md`** — one user-facing line under `## 0.8.2`. Suggested wording: `Gloops (ghost-only) recordings no longer leak kerbal assignments or vessel costs into the career ledger — they are purely visual.` Fits the repo's 1-line CHANGELOG style.
- **`docs/dev/todo-and-known-bugs.md`** — strike the `## 432.` header and rewrite the body as a ~~DONE~~ status note describing the apply-side-only design, per CLAUDE.md's "doc updates per commit, not per PR" rule. (Already updated on this branch to reflect the refined scope; the implementation commit flips it to ~~done~~.)
- **`docs/user-guide.md`** — append to the Gloops Flight Recorder section. Suggested line: `Gloops recordings are purely visual. They never charge funds for the vessel you captured, never reserve the kerbals aboard for the loop duration, never complete contracts, and never credit science or milestones — those stay with your normal mission recording (if any) or your between-mission career state.`
- **`.claude/CLAUDE.md`** — no change.

### Step 9 — Scope

**In scope:**
- `CreateKerbalAssignmentActions` and `CreateVesselCostActions` early-return on `rec.IsGhostOnly`.
- `LedgerOrchestrator.PurgeGhostOnlyActionsFromLedger` + `Ledger.RemoveActionsForRecording` helper, called from `RecalculateAndPatch` to mutate `Ledger.Actions` in place.
- New `Source/Parsek.Tests/GloopsEventSuppressionTests.cs`.
- Doc updates per step 8.

**Out of scope:**
- Capture-side event suppression — events during a Gloops window belong to the parallel normal recording or between-mission state under #431; no Gloops involvement.
- Retroactive save cleanup (beta; pre-fix saves self-heal on next load via step 1's one-shot migration rewrite).
- `ParsekFlight` accessor (dead code — no caller).
- Multi-recording Gloops trees (new feature in `#435`; #432's per-recording filter handles them automatically when they land).
- Removing `MilestoneStore.CurrentEpoch` — separate cleanup per #431's retirement-follow-up TODO.

## Risks

- **`PurgeGhostOnlyActionsFromLedger` overhead on every `RecalculateAndPatch`.** O(recordings + actions). Negligible next to the existing sort + walk. Reverse walk on `Ledger.Actions` makes each remove O(1). The helper allocates a `HashSet<string>` every call, but early-returns before touching `Ledger.Actions` if the set is empty — so steady-state saves with no ghost-only recordings pay only the HashSet allocation (skip the mutation walk entirely). If the HashSet allocation ever shows up in a profile, cache the ghost-only id set on `RecordingStore.Committed` changes; for now, keep it simple.
- **A future code path accidentally generating a Gloops-tagged action.** Caught by the purge (step 2) with an Info log identifying the count. Fires once per `RecalculateAndPatch` that finds any ghost-only rows to remove — so steady-state post-purge it's silent. A fresh non-zero count after a commit/rewind is a signal to find and fix the new path.
- **Multi-recording Gloops (#435) future interaction.** When #435 lands, every leaf in a Gloops tree will have `IsGhostOnly = true`. The existing per-recording `FindRecordingById(...).IsGhostOnly` and `PurgeGhostOnlyActionsFromLedger` logic both read per-recording flags, so the tree case works automatically. No #432 re-work needed.

## Sequencing

Independent of #434. Can ship in any order relative to it. No hard dependency on further work.

## Open questions

None outstanding after the 2026-04-17 clarification:

- Concurrent Gloops + normal recording: events go to the normal recording. Confirmed — the apply-side-only design preserves this automatically.
- Gloops-solo + event: empty-tag between-mission state per #431 existing behavior. Confirmed — no change.
- Accessor name / existence: dropped as dead code.
- Retroactive save cleanup: beta, not needed.
- Multi-recording Gloops: aspirational, tracked in `#435`; #432's per-recording filter future-proofs the ledger-side.
