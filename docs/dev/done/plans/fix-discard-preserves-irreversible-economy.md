# Plan: discarding a recording must preserve irreversible live-gameplay economy

## Bug (confirmed, workflow wf_518320aa)

A career contract accepted at Mission Control (KSC, `recordingId=""`, forwarded direct to the ledger -> ACTIVE) and **completed during a recorded flight** (the `ContractCompleted` event is tagged with the recording id, so it is NOT direct-forwarded; it only becomes a ledger action at commit) **loses its completion when that recording is discarded**:

1. `GameStateRecorder.OnContractCompleted` tags the completion with the live recording id (Emit, GameStateRecorder.cs:71-75) -> `ShouldForwardDirectLedgerEvent` false -> stays a tagged store event awaiting commit.
2. Discard -> `GameStateStore.PurgeEventsForRecordings` (GameStateStore.cs:311-348) drops the tagged completion (the untagged KSC accept + its snapshot survive).
3. Recalc: `ContractsModule` sees accept-without-complete -> contract ACTIVE again.
4. `KspStatePatcher.PatchContracts` restore loop (KspStatePatcher.cs:2078-2161) re-registers it Active via `Contract.Load` + `Register`, while KSP keeps it in `ContractsFinished` (no discard path reverts KSP).

Result: contract in BOTH the accomplished archive and the in-progress tab, re-completable for a duplicate reward; and KSP funds drop on patch (the ledger never credited the purged completion). The same purge-on-discard mechanism affects **science** (`PendingScienceSubjects.Clear()` on discard, RecordingStore.cs:2842/3059) and **milestone achievements** (`MilestoneAchieved` events purged via `MilestoneStore.PurgeTaggedEvents`).

The dangerous trigger is automatic: the scene-exit no-op `CommittedRestoreClone` auto-discard routes through the purging core, and `SwitchSegmentNoOpClassifier.IsNoOpSegment` is blind to game-state events, so a contract-completing-but-otherwise-boring segment is silently auto-discarded with the completion purged.

## Decisions (user, 2026-06-27)

- **Semantics = preserve, not revert.** A non-rewind discard has no quicksave to roll KSP back; KSP already applied the contract/science/milestone irreversibly. Discard drops only the ghost/trajectory; the ledger must keep what KSP applied so the two stay consistent.
- **Scope = contracts + science + milestones.** Irreversible terminal events: `ContractCompleted` / `ContractFailed` / `ContractCancelled`, `MilestoneAchieved`, and collected science (`PendingScienceSubjects`). (Tech/funds/rep out of scope for this change.)

## Fix

Add a re-home step that runs on the **non-rewind purging discard cores only**, BEFORE the purge/clear, converting the discarded recordings' irreversible economy into **direct** ledger actions (recordingId cleared) so they survive. No per-event recalc — the discard's existing post-discard recalc credits them.

### New API: `LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(ICollection<string> discardedRecordingIds, string reason)`

1. **Guards** (refined by the impl review).
   - No re-fly *marker* guard: `MergeDiscardRanToCompletion` tries `TryDiscardActiveReFlyAttempt` first, which handles+short-circuits any re-fly-scoped discard (that path reverts KSP via quicksave + purges directly). A blanket "marker present -> skip" would wrongly skip re-home when discarding an unrelated tree while a re-fly is active elsewhere.
   - **DO guard the abandon path (BLOCKER from impl review).** `DiscardPendingTree` is ALSO reached from `ParsekScenario.DiscardPendingTreeAndAbandonDeferredFlightResults`, whose callers are KSP-reverting / cross-save: quickload-backwards, revert, and stale-pending-from-a-different-save on initial load. There KSP's economy is rolled back (or belongs to another save), so preserving would diverge the ledger the OTHER way (credit economy KSP no longer reflects; worst case cross-save contamination). Fix: `DiscardPendingTree(bool preserveIrreversibleLiveGameplay = true)`; the genuine path (`DiscardPendingTreeAndRecalculate`) keeps the default true, the abandon path passes `false`. The other two cores (`TryDiscardActiveSwitchSegmentAttempt`, `AutoDiscardActiveTreeCore`) are never reached from a KSP-reverting path, so they re-home unconditionally.
   - **Committed-id exclusion** centralized in the helper: it skips any event/subject whose `recordingId` is a committed recording id (`RecordingStore.IsCommittedRecordingId`) — committed history is already in the ledger. `DiscardPendingTree` pre-excludes committed ids anyway; `AutoDiscardActiveTreeCore`'s committed-restore-clone callers can pass committed ids, so the helper guards uniformly (dedup is the backstop).
2. **Contract terminal + milestone events**: scan `GameStateStore.Events` for events whose `recordingId` is in `discardedRecordingIds` and whose `eventType` is in the irreversible set {`ContractCompleted`, `ContractFailed`, `ContractCancelled`, `MilestoneAchieved`}. For each, `GameStateEventConverter.ConvertEvent(evt, recordingId: null)` -> direct action; collect.
3. **Science**: take `PendingScienceSubjects` whose `recordingId` is in `discardedRecordingIds`, copy with `recordingId=""`, `ConvertScienceSubjects(copies, recordingId: null, minCaptureUT, maxCaptureUT)` -> direct science actions; collect separately (need `CommitScienceActions`).
4. `DeduplicateAgainstLedger` the combined list (prevents a double if any already landed). Assign `AllocateKscSequence()` to each survivor.
5. `Ledger.AddActions(deduped)`; if science survivors exist, `GameStateStore.CommitScienceActions(scienceSurvivors)`.
6. Log a one-line summary (counts per kind). NO recalc.

### Wiring (the two non-rewind purging cores only)

- `RecordingStore.DiscardPendingTree` — call `PreserveIrreversibleLiveGameplayOnDiscard(idsToPurge, ...)` right BEFORE `PurgeEventsForRecordings` (RecordingStore.cs:2813-2815) and BEFORE `PendingScienceSubjects.Clear()` (:2842). Use the same `idsToPurge` set (pending-only; committed-overlap ids already excluded).
- `RecordingStore.TryDiscardActiveSwitchSegmentAttempt` — call it once right BEFORE `PurgeEventsForRecordings` (RecordingStore.cs:2965) with `ownedIds` (this is before the disposition branch split, so it covers both the clone branch that clears `PendingScienceSubjects` at :3059 and the prune branch that does not).

After re-home, scoped-remove the re-homed science subjects from `PendingScienceSubjects` (the re-homed subjects are now direct ledger actions; leaving the originals is harmless due to cross-recording scope-skip + dedup + the SubjectMaxValue cap, but removing them keeps the static list clean — esp. for the switch-segment prune branch which never clears).

- `ParsekFlight.AutoDiscardActiveTreeCore` (ParsekFlight.cs:2746) — **also in scope** (plan-review follow-up finding). This non-purging core (idle-on-pad / standalone no-op / Case-B / committed-resume) tears down the uncommitted active tree and then recalcs via `RecalculateAndPatchForCurrentTimelineIfFutureActions` (:2805), which ALWAYS recalcs+patches (full replay when no future actions). A contract accepted at KSC (direct, ledger-active) and completed during such a flight has its completion tagged-but-uncommitted; teardown drops it (never credited) and the recalc re-lists the contract active -> same desync. Insert the re-home at the TOP of `AutoDiscardActiveTreeCore`, collecting `activeTree`'s recording ids BEFORE `activeTree = null`. This core does NOT purge the store events (pre-existing orphan behavior, left unchanged and out of scope); the re-homed direct ledger actions are the ledger truth, and the orphaned tagged store events are economy-safe (not recalc inputs; `ConvertEvents`' cross-recording scope-skip prevents a different recording's commit from re-crediting them).

NOT touched: `MergeDialog.ReFlyDiscard.cs` purges (:199/:283) and `PurgeEventsForRecordingAfterUT` (re-fly/rewind; KSP reverted via quicksave).

### No-op classifier

Unchanged. With re-home in place, even a silently auto-discarded contract-completing segment preserves its economy. The ghost (boring trajectory) is still correctly dropped.

## Tests

- **Flip** the two existing tests that codify the buggy purge as correct, to assert re-home instead:
  - `DiscardFateTests.cs:312 ContractAcceptedAtKsc_CompletedInDiscardedMission_*` — after discard, a direct `ContractComplete` ledger action exists for the contract (it is NOT re-listed active; reward retained).
  - `DiscardFateTests.cs:343 ContractAcceptedAndCompletedInDiscardedMission_*` — completion preserved as a direct action even when accept was also tagged+purged.
- **New** xUnit:
  - Contract completion re-homed on `DiscardPendingTree` -> ledger has a terminal contract action (recordingId null), contract not active.
  - Same on `TryDiscardActiveSwitchSegmentAttempt`.
  - `MilestoneAchieved` re-homed (reward retained).
  - Science subject for the discarded recording re-homed (direct ScienceEarning action + committed-subject cache), not silently cleared.
  - Re-fly guard: with `ActiveReFlySessionMarker` set, re-home is a no-op.
  - No double-count: re-homing an event already present in the ledger is deduped.
- Full suite green.

## Risks / mitigations

- **Double-count**: discarded recordings never run `OnRecordingCommitted`, and `DeduplicateAgainstLedger` covers any overlap -> credited once. KSP credited it live; the ledger now matches.
- **Re-fly**: excluded by core selection + the marker guard.
- **Ordering**: re-home before purge/clear (events/subjects still present), recalc after (existing discard recalc).
