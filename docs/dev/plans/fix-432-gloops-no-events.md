# Fix #432 — Gloops ghost-only recordings must not capture or apply any game events

**Branch:** `fix/432-gloops-no-events` (off `origin/main`)
**Worktree:** `C:/Users/vlad3/Documents/Code/Parsek/Parsek-432-gloops-no-events`
**Depends on:** #431 (PR #332, merged) — the `GameStateRecorder.Emit` funnel.
**Unrelated to:** #434 (PR #333, open) — same naming convention, independent scope.

## Invariant to enforce

> A Gloops ghost-only recording has zero career-state footprint. No `GameStateEvent` is captured while the Gloops recorder is live, and no ledger action is produced from a committed `IsGhostOnly` recording.

## Why the naive "check active recording's IsGhostOnly" accessor does not work

The bug entry and the user's task brief both propose an accessor like `ParsekFlight.GetActiveRecordingGhostOnlyFlag()` that probes the active recording's `IsGhostOnly` flag. That probe returns the wrong answer for the live-capture window because of how Gloops is wired:

- `ParsekFlight.gloopsRecorder` (`ParsekFlight.cs:7426`) is a **parallel** `FlightRecorder` instance with `IsGloopsMode = true`. It runs **alongside** the normal `recorder` (`ParsekFlight.cs:55`); neither suppresses the other. `StartGloopsRecording` (`ParsekFlight.cs:7445`) has no gate against an already-running normal auto-record and vice versa.
- A Gloops `Recording` object does not exist until commit. `CommitGloopsRecorderData` (`ParsekFlight.cs:7523-7562`) builds the `Recording`, sets `rec.IsGhostOnly = true` at `ParsekFlight.cs:7542`, and pushes it straight into `committedRecordings` via `RecordingStore.CommitGloopsRecording` (`RecordingStore.cs:394`). It never enters `pendingTree` and never becomes `activeTree.ActiveRecordingId`.
- `ParsekFlight.GetActiveRecordingIdForTagging()` (`ParsekFlight.cs:35-36`) returns `Instance?.activeTree?.ActiveRecordingId ?? ""` — the **normal** active recording's id, or `""`. It cannot point at a Gloops recording during the live window.

So the live-capture gate has to be "is a Gloops recorder currently sampling?" — which is already exposed as `ParsekFlight.IsGloopsRecording` (`ParsekFlight.cs:7430`, `gloopsRecorder?.IsRecording ?? false`). That is the accessor the Emit guard reads.

The apply-side gate is a different question: committed Gloops recordings live in `RecordingStore.CommittedRecordings` with `rec.IsGhostOnly == true`, and the ledger's action-generation paths walk that list. On the apply side we do check `rec.IsGhostOnly`.

## Current-state map (as of 2026-04-17 on `fix/432-gloops-no-events`, post-#431)

### `IsGhostOnly` declaration and lifecycle

- `Source/Parsek/Recording.cs:29` — `public bool IsGhostOnly;` on the `Recording` object. Serialized at `RecordingTree.cs:526-527` (only written when true), deserialized at `RecordingTree.cs:953`.
- `Source/Parsek/ParsekFlight.cs:7542` — `rec.IsGhostOnly = true;` set at commit time inside `CommitGloopsRecorderData`.
- `Source/Parsek/RecordingStore.cs:402` — belt-and-braces: `CommitGloopsRecording` re-asserts `rec.IsGhostOnly = true;` before adding to `committedRecordings`.
- `Source/Parsek/FlightRecorder.cs:120` — `internal bool IsGloopsMode { get; set; }`, set once at `ParsekFlight.cs:7460` (`new FlightRecorder { IsGloopsMode = true }`). Read at `FlightRecorder.cs:4207, 4212, 4240, 4717, 5143` to skip resource baselines / rewind save / tree logic / auto-stop on vessel switch.

### Gloops lifecycle — start, stop, auto-commit, discard

- Start: `ParsekFlight.StartGloopsRecording` (`ParsekFlight.cs:7445-7482`) allocates `gloopsRecorder`, calls `gloopsRecorder.StartRecording(isPromotion: false)`, stashes a ghost-visual snapshot. Does **not** stop or block the normal auto-recorder.
- Stop (manual): `ParsekFlight.StopGloopsRecording` (`ParsekFlight.cs:7488-7501`) → `CommitGloopsRecorderData` (`ParsekFlight.cs:7523`) → `RecordingStore.CommitGloopsRecording` (`RecordingStore.cs:394`).
- Stop (auto, on vessel switch): `FlightRecorder.HandleVesselSwitchDuringRecording` (`FlightRecorder.cs:5140-5151`) calls `StopRecording()` then sets `GloopsAutoStoppedByVesselSwitch = true`. `ParsekFlight.CheckGloopsAutoStoppedByVesselSwitch` (`ParsekFlight.cs:7507-7516`) fires on the next frame and commits. **Gloops does NOT stash into `LimboVesselSwitch`** — that state is only ever set by the normal-recording tree path.
- Discard: `ParsekFlight.DiscardGloopsInProgress` (`ParsekFlight.cs:7567`) and `DiscardLastGloopsRecording` (referenced at `GloopsRecorderUI.cs:152`).
- UI entry point: `Source/Parsek/UI/GloopsRecorderUI.cs` — only the "Start Recording" button at `:109`, reached via `flight.StartGloopsRecording()` at `:120`. There is no `switch-to-Gloops mid-flight` affordance.

### Capture-side event paths (the sites the guard must cover)

The #431 funnel is `GameStateRecorder.Emit(GameStateEvent evt, string source)` at `GameStateRecorder.cs:58-83`. Every `GameStateEvent` creation in that file routes through it — 17 sites, verified by `grep "Emit(" Source/Parsek/GameStateRecorder.cs`: lines 285, 341, 369, 395, 409, 459, 491, 542, 574, 665, 681, 726, 757, 788, 884, 1136, 1172.

Two capture pipelines live **outside** the `Emit` funnel:

- `GameStateRecorder.cs:832` — `PendingScienceSubjects.Add(new PendingScienceSubject { ... })` inside `OnScienceReceived`. This is the only `PendingScienceSubjects.Add` call site (`grep PendingScienceSubjects.Add Source/Parsek`). The subject is later committed into the ledger by `GameStateEventConverter.ConvertScienceSubjects` via `LedgerOrchestrator.OnRecordingCommitted` — Gloops recordings never call that path (see below), but a subject sitting in the pending list bleeds into **the next normal recording's** commit via `LedgerOrchestrator.NotifyLedgerTreeCommitted` (`LedgerOrchestrator.cs:1519-1560`). That is the real leak on the capture side.
- `GameStateRecorder.cs:292` — `GameStateStore.AddContractSnapshot(guid, contractNode)` inside `OnContractAccepted`, fired immediately after the `ContractAccepted` `Emit` at `:285`. If the `Emit` is suppressed the snapshot should be too, otherwise a contract-revert snapshot lingers for a contract that was never recorded as accepted. `AddContractSnapshot` is defined at `GameStateStore.cs:120`; this is the only production caller.

### Apply-side event paths (recording → ledger)

The ledger is written at action-creation time, not at walk time. Paths that translate recordings into `GameAction`s:

- `LedgerOrchestrator.OnRecordingCommitted` (`LedgerOrchestrator.cs:130-194`). Called only from `NotifyLedgerTreeCommitted` (`LedgerOrchestrator.cs:1546`), `ChainSegmentManager.CommitSegmentCore`, and `ParsekFlight.FallbackCommitSplitRecorder` — **none of which run for Gloops.** `CommitGloopsRecording` (`RecordingStore.cs:394-418`) does NOT call `NotifyLedgerTreeCommitted`. So the event-to-action convert at line `:141` and the science-to-action convert at line `:151` never touch Gloops recordings today.
- `LedgerOrchestrator.CreateVesselCostActions` (`LedgerOrchestrator.cs:397-463`). Called from `OnRecordingCommitted` line `:156`. Not reached for Gloops (no commit path), but the routine blindly reads `rec.PreLaunchFunds - firstFunds` — because `FlightRecorder.cs:4207-4208` skips `CapturePreLaunchResources` in Gloops mode, `rec.PreLaunchFunds` is `0` and the resulting `buildCost` is strongly negative, so the `> 0` gate at `:421` means no action would be emitted even if the path were reached. The recovery-funds branch at `:439` gates on `TerminalStateValue == Recovered`, which a manual Gloops stop does not set.
- `LedgerOrchestrator.CreateKerbalAssignmentActions` (`LedgerOrchestrator.cs:471-510`). Called from two sites:
  - `:165` inside `OnRecordingCommitted` — not reached for Gloops (above).
  - `:800` inside `MigrateKerbalAssignments` (`LedgerOrchestrator.cs:769-840`), which walks **every** recording in `RecordingStore.CommittedRecordings` at `:771-796` and calls `CreateKerbalAssignmentActions(rec.RecordingId, rec.StartUT, rec.EndUT)` unconditionally. The function extracts crew from `rec.VesselSnapshot` via `ExtractCrewFromRecording` (LedgerOrchestrator.cs, same file). **A Gloops recording of a crewed vessel does have a `VesselSnapshot`** — set by `CommitGloopsRecorderData` at `ParsekFlight.cs:7550`. So today `MigrateKerbalAssignments` can generate `KerbalAssignment` actions for a Gloops recording, causing those kerbals to be reserved by `KerbalsModule.ProcessAction` for the Gloops loop duration. This is a real leak.
- `LedgerOrchestrator.RecalculateAndPatch` (`LedgerOrchestrator.cs:612-672`). Builds `actions` from `Ledger.Actions` at `:653`, sorts, walks. The walk is action-driven, not recording-driven, so it does not need to re-probe `IsGhostOnly` — but as a belt-and-braces guard (which is what the bug asks for under "Apply-side defense in depth") a filter at `:653` that drops actions whose `RecordingId` belongs to a ghost-only recording closes the "future regression routes events through a Gloops recording" hole.
- `RecalculationEngine.Recalculate` (`RecalculationEngine.cs:136-226`). Pure: takes a `List<GameAction>`, sorts, dispatches. No direct `Recording` access; needs no change.

### Interaction with #431's tagging

- `GameStateRecorder.Emit` (`GameStateRecorder.cs:58-83`) reads the tag from `ResolveCurrentRecordingTag()` at `:60`, which in turn reads `ParsekFlight.GetActiveRecordingIdForTagging()` — the **normal** active recording id.
- During a Gloops capture window where the normal auto-recorder is also running, events would be tagged with the **normal** recording's id (not a Gloops id). Without #432, those events ride with the normal recording's fate: they'd be purged on normal-recording discard, preserved on normal-recording commit — exactly the opposite of what the Gloops invariant demands.
- With #432's capture-side guard in place, the event never reaches `Emit`, so no tag is stamped at all. A Gloops recording never has an id that owns events in `GameStateStore.Events` or in any `Milestone.Events` list. Therefore:
  > `PurgeEventsForRecordings(idsToPurge)` (`GameStateStore.cs:261`) called with a Gloops id in the set is a no-op by construction — the HashSet lookup at `GameStateStore.cs:282` (the `set.Contains(events[i].recordingId)` filter) never matches.
- That invariant is a useful regression check for the test matrix (step 7 below).

## Plan

### Step 1 — Capture-side guard: central Emit prologue + two out-of-funnel sites

**Decision: central guard at `Emit` + explicit guard at each of the two out-of-funnel sites (science subjects, contract snapshots).** Rationale:

- 17 `Emit` call sites in `GameStateRecorder.cs` all funnel through line `:82`. One guard at the top of `Emit` covers all 17 with a single code path, matching the #431 architecture.
- `PendingScienceSubjects.Add` (`:832`) and `AddContractSnapshot` (`GameStateRecorder.cs:292`) do not go through `Emit`. Two extra early-returns.
- Per-site guards at the 17 `Emit` call sites would multiply boilerplate and spread the invariant across the file, contradicting #431's "one funnel" design.

Concrete change in `GameStateRecorder.cs`, after the existing tag-resolve block:

```csharp
// GameStateRecorder.cs — inside Emit, before the existing AddEvent call.
internal static void Emit(GameStateEvent evt, string source)
{
    // #432: Gloops (ghost-only) recordings must have zero career footprint.
    // When the Gloops recorder is live, suppress event capture entirely.
    if (ParsekFlight.IsGloopsCaptureActive())
    {
        ParsekLog.Verbose("GameStateRecorder",
            $"Gloops capture active — suppressed {evt.eventType} event (key='{evt.key}', source='{source ?? ""}')");
        return;
    }

    string tag = ResolveCurrentRecordingTag();
    ... // existing body unchanged
}
```

And at the two out-of-funnel sites:

```csharp
// GameStateRecorder.cs:285-292 — OnContractAccepted. Guard wraps BOTH the Emit
// and the contract-snapshot write. A snapshot without its accept event would
// leak into a later contract-revert path. Gate before Emit so the log line order
// reads as "snapshot suppressed because Gloops active".
if (ParsekFlight.IsGloopsCaptureActive())
{
    ParsekLog.Verbose("GameStateRecorder",
        $"Gloops capture active — suppressed ContractAccepted (guid={guid})");
    return;
}
Emit(evt, "ContractAccepted");
try { GameStateStore.AddContractSnapshot(guid, contractNode); ... }
```

```csharp
// GameStateRecorder.cs:826-840 — OnScienceReceived. PendingScienceSubjects.Add
// is its own pipeline; guard it independently. The list is consumed on the NEXT
// normal-recording commit (LedgerOrchestrator.NotifyLedgerTreeCommitted:1519),
// so a leaked subject would credit science to an unrelated career mission.
if (ParsekFlight.IsGloopsCaptureActive())
{
    ParsekLog.Verbose("GameStateRecorder",
        $"Gloops capture active — suppressed PendingScienceSubjects.Add (subject={subject.id})");
    return;
}
PendingScienceSubjects.Add(new PendingScienceSubject { ... });
```

The two explicit guards are small enough that they don't warrant factoring into a helper. They also document the two non-Emit capture pipelines for the next person reading the file.

Note that the duplicate Emit-guard inside `Emit` itself still fires for the `ContractAccepted` path the second time (we early-return before it) — so in practice `Emit` only sees the guard on the 15 sites that aren't contract-accepted / science-received. This is fine: the guard is idempotent and its Verbose log stays a useful signal for the "some handler slipped through" case.

### Step 2 — Add the `ParsekFlight.IsGloopsCaptureActive()` accessor

The task brief proposes `GetActiveRecordingGhostOnlyFlag()`. That name suggests a probe of the active recording's flag, which we established above does not work for the live-capture window — it would return `false` every time because the Gloops `Recording` does not exist in the tree yet.

The right name is one that describes the live state the guard actually checks. Use `IsGloopsCaptureActive` — parallels `IsGloopsRecording` (the existing instance property at `ParsekFlight.cs:7430`) but is static and null-safe, like `GetActiveRecordingIdForTagging` and `HasLiveRecorderForTagging` (`ParsekFlight.cs:35, 43`):

```csharp
// ParsekFlight.cs — near the existing tagging accessors at :35.
/// <summary>
/// #432: true when a Gloops ghost-only recorder is currently sampling. The
/// GameStateRecorder.Emit prologue and the two out-of-funnel capture sites
/// (PendingScienceSubjects.Add, AddContractSnapshot) consult this to suppress
/// career-state capture during Gloops flights.
///
/// Unlike GetActiveRecordingIdForTagging, this does not probe the active tree —
/// a Gloops Recording does not exist in the tree until CommitGloopsRecording
/// runs, so the live-window gate must read the recorder state directly.
/// </summary>
internal static bool IsGloopsCaptureActive()
{
    if (GloopsCaptureActiveForTesting != null)
        return GloopsCaptureActiveForTesting();
    return Instance != null && Instance.IsGloopsRecording;
}

/// <summary>
/// #432: test hook for IsGloopsCaptureActive, mirrors #431's TagResolverForTesting
/// pattern in GameStateRecorder. Tests set this to a fixed bool supplier so they
/// don't have to spin up the ParsekFlight MonoBehaviour.
/// </summary>
internal static System.Func<bool> GloopsCaptureActiveForTesting;
```

A corresponding `ParsekFlight.ResetTestOverrides()` clears `GloopsCaptureActiveForTesting = null` alongside any future test hooks on this class. (No such reset exists on `ParsekFlight` today; add it. `GameStateRecorder.ResetForTesting` at `GameStateRecorder.cs:115` is a clear model.)

**LimboVesselSwitch fallback — NOT needed.** The #431 `ResolveCurrentRecordingTag` fallback (`GameStateRecorder.cs:100-104`) exists because a normal recording stashes its pending tree into `LimboVesselSwitch` during a vessel switch and events captured in that window still belong to the outgoing recording. **Gloops has no such stash.** `FlightRecorder.HandleVesselSwitchDuringRecording` (`FlightRecorder.cs:5143-5151`) auto-stops the Gloops recorder immediately on vessel-switch detection, and `CheckGloopsAutoStoppedByVesselSwitch` commits it on the next frame. By the time any ambiguity window opens, `gloopsRecorder.IsRecording` is already `false`, so `IsGloopsCaptureActive()` returns `false`. Events fired during the switch belong to the incoming vessel's normal recording (or no recording), which is the correct place for them.

### Step 3 — Apply-side: action-creation guards + belt-and-braces walk filter

**At action-creation.** `CreateKerbalAssignmentActions` (`LedgerOrchestrator.cs:471`) and `CreateVesselCostActions` (`LedgerOrchestrator.cs:397`) both already call `FindRecordingById` (`LedgerOrchestrator.cs:592`) to resolve the `Recording`. Add an early-return:

```csharp
// LedgerOrchestrator.cs:471 — CreateKerbalAssignmentActions, after FindRecordingById.
if (rec == null) return result;
if (rec.IsGhostOnly)
{
    ParsekLog.Verbose(Tag,
        $"CreateKerbalAssignmentActions: recording '{recordingId}' is ghost-only — skipping");
    return result;
}
```

```csharp
// LedgerOrchestrator.cs:401 — CreateVesselCostActions, after FindRecordingById.
if (rec == null) { ... return result; }
if (rec.IsGhostOnly)
{
    ParsekLog.Verbose(Tag,
        $"CreateVesselCostActions: recording '{recordingId}' is ghost-only — skipping");
    return result;
}
```

This closes the real leak at `LedgerOrchestrator.cs:800` where `MigrateKerbalAssignments` walks every `CommittedRecordings` entry and would synthesize `KerbalAssignment` actions for a crewed Gloops recording.

**Belt-and-braces walk filter.** Add a pre-pass filter inside `LedgerOrchestrator.RecalculateAndPatch` (`LedgerOrchestrator.cs:612`) that drops actions whose `RecordingId` maps to an `IsGhostOnly` recording. This catches:

- Stale actions from a pre-#432 save where `MigrateKerbalAssignments` already ran on a Gloops recording and deposited `KerbalAssignment` rows into the ledger file on disk.
- Future regressions where some new code path produces a `GameAction` with a Gloops `RecordingId`.

```csharp
// LedgerOrchestrator.cs:653 — inside RecalculateAndPatch, before Recalculate.
var actions = new List<GameAction>(Ledger.Actions);

// #432: filter out any actions tagged with a ghost-only recording's id.
// Pure defense-in-depth: action-creation should already skip these, but a
// save from before the action-creation guards landed could hold stale rows.
int beforeFilter = actions.Count;
actions = FilterOutGhostOnlyActions(actions);
int filtered = beforeFilter - actions.Count;
if (filtered > 0)
    ParsekLog.Info(Tag,
        $"RecalculateAndPatch: filtered {filtered} action(s) tagged with ghost-only recordings");

RecalculationEngine.Recalculate(actions);
```

```csharp
// LedgerOrchestrator.cs — new internal static helper.
internal static List<GameAction> FilterOutGhostOnlyActions(List<GameAction> actions)
{
    if (actions == null || actions.Count == 0) return actions ?? new List<GameAction>();

    // Build ghost-only id set once per call. Cheap: CommittedRecordings is usually <100.
    var ghostOnlyIds = new HashSet<string>(StringComparer.Ordinal);
    var recs = RecordingStore.CommittedRecordings;
    for (int i = 0; i < recs.Count; i++)
        if (recs[i].IsGhostOnly && !string.IsNullOrEmpty(recs[i].RecordingId))
            ghostOnlyIds.Add(recs[i].RecordingId);

    if (ghostOnlyIds.Count == 0) return actions;

    var kept = new List<GameAction>(actions.Count);
    for (int i = 0; i < actions.Count; i++)
        if (string.IsNullOrEmpty(actions[i].RecordingId) || !ghostOnlyIds.Contains(actions[i].RecordingId))
            kept.Add(actions[i]);
    return kept;
}
```

**`MigrateKerbalAssignments` — no extra change needed.** Once `CreateKerbalAssignmentActions` early-returns for ghost-only, `KerbalAssignmentActionsMatch(existing, kerbalActions)` at `LedgerOrchestrator.cs:804` trivially matches an empty desired list against any empty existing list → `continue;`, and for a Gloops recording that had stale rows from a pre-fix save, `Ledger.ReplaceActionsForRecording(KerbalAssignment, rec.RecordingId, empty)` cleans them up on next load. That is the one-shot self-heal we want.

### Step 4 — One-time self-heal for pre-fix saves that already leaked Gloops actions

The walk filter in step 3 keeps Gloops actions out of the recalculation, but leaves them sitting in `Ledger.Actions` forever (written back to disk on `OnSave`). That bloats the file and keeps the log line "filtered N actions" firing on every load.

**Decision: clean the ledger on load, not every call.** Add a one-shot pass in `LedgerOrchestrator.OnLoad` (`LedgerOrchestrator.cs:1080`) that calls `FilterOutGhostOnlyActions` once, compares counts, and if anything was removed, calls `Ledger.RemoveActionsForRecording` for each ghost-only id. Logs at Info. This runs after `Ledger.LoadFromFile(path)` at `:1094`.

```csharp
// LedgerOrchestrator.cs — inside OnLoad, after Ledger.LoadFromFile.
int removed = PurgeGhostOnlyActionsFromLedger();
if (removed > 0)
    ParsekLog.Info(Tag,
        $"OnLoad: removed {removed} stale action(s) tagged with ghost-only recordings (pre-#432 save)");
```

The helper walks `Ledger.Actions`, collects ghost-only-tagged entries, and removes them via whatever public API `Ledger` already exposes (`Ledger.RemoveActionsForRecording` or direct list mutation — pick whichever exists; `MigrateKerbalAssignments` uses `Ledger.ReplaceActionsForRecording` at `:809` as precedent). A cheap, idempotent cleanup.

### Step 5 — Interaction with #431's purge, formally

Once steps 1–3 are in place, the Gloops-capture flow is:

1. Player opens Gloops UI, clicks Start.
2. `gloopsRecorder.IsRecording` flips to `true`. The normal auto-recorder may also be running; `activeTree.ActiveRecordingId` holds the normal recording's id (or `""`).
3. A career event fires (e.g. `ContractAccepted`).
4. `GameStateRecorder.OnContractAccepted` sees `ParsekFlight.IsGloopsCaptureActive() == true`, early-returns **before** the `Emit` and **before** the `AddContractSnapshot`. No `GameStateEvent` reaches `GameStateStore.Events`, no snapshot reaches `contractSnapshots`.
5. Player stops Gloops. `CommitGloopsRecording` mints a recording id, flips `IsGhostOnly = true`, adds to `committedRecordings`.

At no point is a `GameStateEvent` tagged with the Gloops recording id. Therefore `GameStateStore.PurgeEventsForRecordings([gloopsId], reason)` (`GameStateStore.cs:261`) finds zero matches: the `events[i].recordingId == gloopsId` check at `:282` never fires. The purge path is a no-op for Gloops ids **by construction of the capture guard**, not by any extra check in `PurgeEventsForRecordings`. This matches the explicit prediction in the bug entry (`todo-and-known-bugs.md:222`: "Gloops ghost-only recordings are covered by #432: they never capture events, so #431's purge logic is a no-op for them").

Codify this as a test case (step 7, test `purge_for_gloops_id_is_noop`), not as runtime code. No runtime change needed in `PurgeEventsForRecordings`.

### Step 6 — Edge cases, worked through

1. **Normal recording + Gloops concurrently.** Allowed by the current UI (no gate in `GloopsRecorderUI.cs` or `StartGloopsRecording`). With the step-1 guard, while Gloops is live every career event is suppressed — including events the normal recording should rightfully have captured. **This is a real UX gotcha.** Two ways to resolve:
   - **(a) Accept as user error**, document in the user-guide note at step 9. "Don't run career missions while a Gloops recording is active." Pragmatic: Gloops is decorative and intended for plane flies / airshows, not career missions.
   - **(b) Add a UI gate** in `GloopsRecorderUI.DrawWindow` and `StartGloopsRecording` that blocks Start while `IsRecording == true` (normal recorder running), plus a symmetric check in the normal-recording start paths.
   I recommend **(a)** for this PR — the smaller change and the existing parallel-recorder design is explicit in the bug's framing. (b) is a cleaner UX but widens the scope into UI work that the bug entry does not mention. **Open question for the user** (see final section).
2. **Player switches to a Gloops recording mid-flight.** The UI has no such affordance. `GloopsRecorderUI.DrawWindow` (`UI/GloopsRecorderUI.cs:91-186`) only offers Start / Stop / Preview / Discard for the Gloops recorder itself — there is no switch-recording-type control. On vessel switch, `FlightRecorder.HandleVesselSwitchDuringRecording` (`FlightRecorder.cs:5143-5151`) auto-stops Gloops. Answer: **not possible with the current UI flow** — no code change needed to handle it.
3. **Quicksave during a Gloops flight + F9.** `GameStateStore.Events` serializes to disk on save (via `ParsekScenario.OnSave` → `GameStateStore.SaveEventFile`). Events captured BEFORE the #432 guard landed (i.e., from a save created on a pre-fix build) are already in the save file. F9ing that quicksave loads them back. **Retroactive cleanup is out of scope** — documented in step 10. Step 4's one-time ledger-side self-heal handles the related leak in `Ledger.Actions`, but the `GameStateStore.Events` / `MilestoneStore` side would need a full migration pass that this PR does not ship. A player running a pre-#432 save through a post-#432 build still sees the stale event rows until they manually discard the old Gloops recording; step 11 covers it in the risks section.
4. **Gloops + LimboVesselSwitch fallback.** Covered above in step 2 ("LimboVesselSwitch fallback — NOT needed"). Gloops auto-stops on vessel switch; there is no limbo window for it.
5. **Science subjects pipeline.** Addressed in step 1 with the explicit `OnScienceReceived` guard. `PendingScienceSubjects` is the only science capture target; `GameStateEventConverter.ConvertScienceSubjects` is consumer-side and only fires from the normal `OnRecordingCommitted` path, never from Gloops commit.
6. **Resource events (FundsChanged, ScienceChanged, ReputationChanged).** The `Emit` guard suppresses them identically to other event types. The invariant says Gloops has zero career footprint, and resource events that the game fires during a Gloops flight (e.g. recovery funds credited because the player happened to recover a different vessel while Gloops is running) would today flow into `GameStateStore.Events`. Under #432 they are suppressed. **This is the intended behavior** — Gloops windows explicitly contract to not capture career state. The corollary: real career effects that happen to fire during a Gloops window do not get ledger-replayed later. Documented in step 9's user-guide note. No extra guard; the `Emit` gate covers it.
7. **Auto-commit after vessel-switch auto-stop.** Between `gloopsRecorder.StopRecording()` (`FlightRecorder.cs:5148`) and `CommitGloopsRecorderData` on the next frame (`ParsekFlight.cs:7514`), `gloopsRecorder.IsRecording == false` → `IsGloopsCaptureActive()` returns `false`. Events that fire in that narrow window (e.g. science rewards credited after the vessel switch completes) are captured normally and tagged with whatever recording is newly active. This is correct: by the time the gate has lowered, the Gloops window has closed and those events belong to the next mission's fate.

### Step 7 — Tests

New `Source/Parsek.Tests/GloopsEventSuppressionTests.cs`. `[Collection("Sequential")]` — touches `GameStateRecorder` / `GameStateStore` / `Ledger` / `RecordingStore` static state. All tests swap the Gloops-capture hook via `ParsekFlight.GloopsCaptureActiveForTesting = () => true` (or `false`) so they don't need the MonoBehaviour alive. `ParsekLog.TestSinkForTesting` captures the Verbose suppression log lines for assertions.

1. **`contract_accept_during_gloops_is_suppressed`** — `GloopsCaptureActiveForTesting = () => true`, fire a synthetic `ContractAccepted` through the handler. Assert `GameStateStore.Events` count unchanged, `contractSnapshots` unchanged, and a `"Gloops capture active — suppressed ContractAccepted"` Verbose line was logged.
2. **`normal_contract_accept_still_captured`** — `GloopsCaptureActiveForTesting = () => false` (or null), fire a `ContractAccepted`. Assert event was added to the store (and to `contractSnapshots` where appropriate).
3. **`science_subject_during_gloops_is_suppressed`** — `GloopsCaptureActiveForTesting = () => true`, call `OnScienceReceived` directly with a synthetic subject. Assert `PendingScienceSubjects.Count == 0` and the suppress-log line for `OnScienceReceived` fired.
4. **`normal_science_subject_still_captured`** — gate off, same call. Assert `PendingScienceSubjects.Count == 1`.
5. **`resource_event_during_gloops_is_suppressed`** — gate on, fire a `FundsChanged` event. Assert no event in the store, suppress log fired.
6. **`ledger_walk_skips_ghost_only_recording`** — seed `Ledger.Actions` with two `KerbalAssignment` actions, one tagged with a `RecordingId` whose `IsGhostOnly = true` (injected via a test `Recording` added to `RecordingStore`), one tagged with a normal recording id. Call `LedgerOrchestrator.FilterOutGhostOnlyActions` directly. Assert the ghost-only action is filtered, the normal one is kept, and the "filtered N" Info log fires when called from `RecalculateAndPatch`.
7. **`create_kerbal_assignment_actions_returns_empty_for_ghost_only`** — construct a test `Recording` with `IsGhostOnly = true` and a synthetic `VesselSnapshot` containing one crew. Add it to `committedRecordings`. Call `LedgerOrchestrator.CreateKerbalAssignmentActions(recId, startUT, endUT)`. Assert the result is empty and the Verbose "ghost-only — skipping" line fired.
8. **`purge_for_gloops_id_is_noop`** — set `GloopsCaptureActiveForTesting = () => true`, run 5 distinct `Emit` attempts. Then set the gate off, add a normal event tagged with a different id. Call `GameStateStore.PurgeEventsForRecordings` with a fake "gloops-id" (which was never used as a tag because the gate suppressed all 5). Assert `removed == 0` and the live store still contains the one normal event.
9. **`limbo_vessel_switch_does_not_affect_gloops_guard`** — set `RecordingStore.pendingTreeState = LimboVesselSwitch`, gate on. Fire an `Emit`. Assert suppressed regardless of the limbo state. Codifies that the Gloops guard runs before the tag resolver's LimboVesselSwitch fallback.
10. **`on_load_purge_removes_stale_ghost_only_actions`** — seed `Ledger.Actions` with two actions tagged with a ghost-only id, call `LedgerOrchestrator.PurgeGhostOnlyActionsFromLedger()` (the helper from step 4). Assert both actions removed and the "removed N stale action(s)" Info log fires. Complement: same setup but ids are all non-ghost-only → assert nothing removed, no log.
11. **`reset_test_overrides_clears_hook`** — set `GloopsCaptureActiveForTesting = () => true`, call `ParsekFlight.ResetTestOverrides()`. Assert `IsGloopsCaptureActive()` now returns `false` (no Instance in tests, Instance == null ⇒ false).

**Test hook summary:** add `ParsekFlight.GloopsCaptureActiveForTesting` (`Func<bool>`) and `ParsekFlight.ResetTestOverrides()`. Same pattern as `GameStateRecorder.TagResolverForTesting` / `ResetForTesting` (`GameStateRecorder.cs:49, 115`). Yes, the test hook is needed — production tests can't easily instantiate `ParsekFlight` (it's a `MonoBehaviour` with heavy state).

### Step 8 — Post-build verification

`dotnet build` + `dotnet test` from the worktree. After deploy, grep the deployed DLL for a distinctive UTF-16 string introduced by this change (`IsGloopsCaptureActive` or `"Gloops capture active — suppressed"`). Manual playtest recipe:

1. Start a career game, launch a plane.
2. Open Gloops window, click Start Recording.
3. In flight, take an EVA science sample or accept a contract via KAC. (If that isn't reachable mid-Gloops-flight, use a stock save-file edit to force a `FundsChanged` event mid-flight — e.g. `MODIFY_FUNDS`.)
4. Stop Gloops.
5. Check `KSP.log` for `"Gloops capture active — suppressed"` lines.
6. Open Parsek Career State window — confirm the contract / science / funds from step 3 did not appear in the ledger.
7. Delete the Gloops recording. Confirm the career state window still shows no Gloops-derived entries (and no log lines about purged Gloops events — because there were none to purge).

### Step 9 — Docs updates (staged in the implementation commit per CLAUDE.md)

- **`CHANGELOG.md`** — one user-facing line under `## 0.8.2` (or the next active heading). Suggested wording: `Gloops (ghost-only) recordings no longer leak contracts, science, funds, reputation, crew assignments, or milestones into the career ledger — they are purely visual.` Fits the repo's 1-line hard-rule CHANGELOG style.
- **`docs/dev/todo-and-known-bugs.md`** — strike the `## 432.` header and body at `:159-192`, replace the `**Status:** TODO.` line with a status note along the lines of what `#431` got at `:240` (pattern: `**Status:** ~~DONE~~. <one-paragraph summary of approach and file map>`). Keep the "Related edge cases" section intact for reference.
- **`docs/user-guide.md`** — append to the Gloops Flight Recorder section at `:36-46`. Suggested line: `Gloops recordings have no effect on your career — contracts accepted, science collected, funds spent, milestones achieved, and kerbals aboard during a Gloops flight are all ignored by the Parsek ledger. Use normal recordings for missions you want to count.`
- **`.claude/CLAUDE.md`** — no change. The file layout and patterns are unchanged.

### Step 10 — Scope

**In scope for this PR:**
- `GameStateRecorder.Emit` capture-side gate.
- Explicit gates at `OnContractAccepted` (before Emit + snapshot) and `OnScienceReceived` (before `PendingScienceSubjects.Add`).
- `ParsekFlight.IsGloopsCaptureActive()` + `GloopsCaptureActiveForTesting` test hook + `ResetTestOverrides`.
- `CreateKerbalAssignmentActions` and `CreateVesselCostActions` action-creation early-returns on `rec.IsGhostOnly`.
- `LedgerOrchestrator.FilterOutGhostOnlyActions` + `RecalculateAndPatch` pre-pass filter.
- One-time self-heal `PurgeGhostOnlyActionsFromLedger` called from `LedgerOrchestrator.OnLoad`.
- New `Source/Parsek.Tests/GloopsEventSuppressionTests.cs` per step 7.
- Doc updates per step 9.

**Out of scope:**
- **Retroactive cleanup of leaked events in pre-#432 saves.** Events already in `GameStateStore.Events` / `Milestone.Events` / contract snapshots from a pre-fix Gloops flight are not purged by this PR. The ledger-side self-heal (step 4) handles `Ledger.Actions`; the event-store side would need a migration pass that walks every saved event, looks up its tag against currently-committed ghost-only recordings, and removes matches. That migration is cheap but scope-creeps into schema-migration territory; document as a known limitation in the CHANGELOG.
- **UI gate against concurrent normal + Gloops recording.** Documented in step 6 case 1 and the Open Questions section. Would add UI work not scoped to this bug.
- **Allowing Gloops recordings to opt into career capture.** Contradicts the decorative-only design intent (bug entry, `todo-and-known-bugs.md:186-187`). If a player wants career effects, they use a normal recording.
- **Removing `MilestoneStore.CurrentEpoch` filter.** Separate cleanup per the #431 retirement-follow-up TODO at `todo-and-known-bugs.md:216`.

## Risks

- **The `IsGloopsCaptureActive` guard suppresses events that happen during a Gloops window even if the player also has a normal career mission recording concurrently.** See step 6 case 1. Mitigated by the user-guide note in step 9 and flagged as an open question for the implementer.
- **Pre-#432 saves carry stale leaked data.** Step 4 heals `Ledger.Actions`; events in `GameStateStore.Events` and `Milestone.Events` stay behind. A subsequent migration pass could clean them, but it is out of scope here. Players unaffected in practice unless they happened to have a heavy Gloops + career session on an older build.
- **Performance.** `FilterOutGhostOnlyActions` runs on every `RecalculateAndPatch` call — builds a HashSet of ghost-only ids from `CommittedRecordings` (typically <100 entries) and walks `actions` once. O(recordings + actions). Negligible next to the existing sort + walk in `RecalculationEngine.Recalculate`. No index needed.

## Sequencing

Independent of #434. Can ship alongside or after #434 in any order; they touch disjoint code paths (#434 is the revert-auto-discard rebase on top of #431's `DiscardPendingTree`, #432 is about capture gating + ledger filter).

## Open questions for the user before implementation

1. **Concurrent Gloops + normal recording — suppress all, or gate the UI?** (Step 6 case 1 / Risks section.) My default is to suppress all events during the Gloops window and document in the user-guide. Alternative: add a UI gate that blocks Start Gloops while the normal recorder is active, and vice versa. The latter is a behavior change this PR could sneak in, but also widens scope.
2. **Retroactive save-side cleanup — ship a migration, or leave as documented limitation?** Step 10 goes with "documented limitation". A migration would walk `GameStateStore.Events` + every `Milestone.Events` list on load and purge entries whose `recordingId` maps to a ghost-only recording. One-shot pass, idempotent, mirrors step 4's ledger-side heal. Implementer can add it if desired; I scoped it out to keep the PR focused.
3. **Accessor name — `IsGloopsCaptureActive` vs. `GetActiveRecordingGhostOnlyFlag` (task brief wording) vs. something else?** My case for renaming is in the Step 2 body. If the reviewer prefers the brief's original name I'll rename — but the rename-only change doesn't affect correctness.
