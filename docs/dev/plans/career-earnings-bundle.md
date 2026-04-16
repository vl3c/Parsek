# Career Earnings Bundle — Implementation Plan

**Branch:** `fix/career-earnings-bundle` (worktree at `C:\Users\vlad3\Documents\Code\Parsek-career-earnings-bundle`)
**Source:** synthesized from Plan agent draft + independent review (`career-earnings-bundle-review.md`). The review is authoritative wherever it disagrees with the draft and supersedes the todo's own fix suggestions where it cites concrete code.

This PR closes 11 interlocked career-mode earnings bugs: **#394, #395, #396, #397, #398, #400, #401, #402, #403, #404, #405**. It also fixes one latent dedup-key collision (§F) that `#405` would surface.

Read `career-earnings-bundle-review.md` alongside this plan — the review contains the hands-on source verification for every claim below.

---

## 0. Goal

After this PR:

- Contract accept/complete/fail/cancel and `PartPurchased` events at KSC reach the ledger in real time and survive scene transitions.
- `PatchContracts` no longer wipes the Offered bucket or `ContractsFinished` history.
- `PatchFunds` does not drag the pool to zero, because earning channels (recovery, contract rewards, milestones) actually emit `FundsEarning` / `ReputationEarning` actions.
- Milestone funds and reputation rewards land in the ledger at the moment KSP applies them.
- Science subjects committed at flight-end actually reach `ScienceEarning` actions (the clear-before-read race is gone).
- The existing c1 and sci1 bricked saves are repaired on first load (one-shot migration).
- Any future regression in the same shape is loud: a commit-time reconciliation diagnostic watches the dropped `FundsChanged`/`ReputationChanged`/`ScienceChanged` channel for disagreements with the real earning channels.
- `ScienceSubjectPatch` reads from the ledger module instead of the store, so a broken ledger can no longer masquerade as a correct Archive display.

---

## 1. Bugs in scope (final list)

| Bug | Summary | Fix section |
|---|---|---|
| #405 | Contract + `PartPurchased` handlers missing `OnKscSpending` | §A |
| #404 | `PatchContracts` wipes Offered + Finished | §B |
| #397 | `PendingScienceSubjects` cleared before orchestrator reads them | §C |
| #400 | Milestone reward funds/rep hardcoded to 0 | §D |
| #403 | `FundsEarning`/`ReputationEarning` never emitted | §E (composite: closed by §A + §B + §D + terminal-state check) |
| #F (new) | `GetActionKey` collision for KSC `FundsSpending` | §F |
| #398 | `ContractOffered` events accumulate in store | §G |
| #402 | `PatchFunds` drags balance to 0 (symptom) | §H (defensive WARN) |
| #394 | `FundsChanged/ScienceChanged/ReputationChanged` silently dropped | §I (commit-time reconciliation WARN) |
| #395 | `ScienceSubjectPatch` masks ledger regressions at Archive | §J |
| #401 | c1 save recovery migration | §K |
| #396 | sci1 save recovery migration | §K |

**Explicitly deferred** (follow-up PR, outside this bundle):

- **#399** (`ScienceModule.ComputeTotalSpendings` same-UT walk bug) — suspect, unverified, distinct root cause. Tracked in todo.

---

## 2. Per-bug implementation

### §A. #405 — add `OnKscSpending` to five handlers

**File:** `Source/Parsek/GameActions/GameStateRecorder.cs`

Five handlers each get one line appended (modeled on the existing tail in `OnTechResearched:316`, `OnKerbalAdded:380`, `OnProgressComplete:712`, `PollFacilityState:857`):

```csharp
if (!IsFlightScene())
    LedgerOrchestrator.OnKscSpending(evt);
```

Targets:
- `OnContractAccepted` (`cs:168-210`) — append before method exit
- `OnContractCompleted` (`cs:212-225`)
- `OnContractFailed` (`cs:227-240`)
- `OnContractCancelled` (`cs:242-255`)
- `OnPartPurchased` (`cs:319-339`)

**`OnContractOffered` intentionally does NOT get the call** — see §G.

**Dispatch verification:** `LedgerOrchestrator.OnKscSpending(evt)` at `LedgerOrchestrator.cs:902` dispatches via `GameStateEventConverter.ConvertEvent`, whose switch at `cs:103-113` already handles `ContractAccepted/Completed/Failed/Cancelled` via `ConvertContractAccepted/Completed/Failed/Cancelled`. `PartPurchased` is at `cs:88`. All paths exist and work.

**Dedup safety:** commit-time `ConvertEvents` would re-emit the same action from `GameStateStore.Events`. `DeduplicateAgainstLedger` handles this via `GetActionKey` returning `ContractId` for contracts (`LedgerOrchestrator.cs:201-204`). **Contracts dedup correctly.** `FundsSpending` does NOT — that's §F, which must ship in the same commit as this handler.

**Logging:** The existing `OnKscSpending` at `LedgerOrchestrator.cs:902-926` already logs `"KSC spending recorded: type=...`". No new log lines needed in the handlers themselves, but each handler's existing log (e.g. `GameStateRecorder.cs:204`) stays.

### §B. #404 — stop destroying Offered and Finished

**File:** `Source/Parsek/GameActions/KspStatePatcher.cs`, `PatchContracts` (`cs:585-763`)

Replace the destructive block at lines 614–640:

```csharp
// OLD (destructive):
for (int i = 0; i < currentContracts.Count; i++) {
    if (currentContracts[i].ContractState == Contract.State.Active) {
        currentContracts[i].Unregister();
    }
}
currentContracts.Clear();
ContractSystem.Instance.ContractsFinished.Clear();
```

with a filtered remove that only touches Active contracts absent from `activeIds`:

```csharp
// NEW: only remove Active contracts no longer in the ledger
var activeIdSet = new HashSet<Guid>(activeIds);
int removedCount = 0;
for (int i = currentContracts.Count - 1; i >= 0; i--) {
    var c = currentContracts[i];
    if (c.ContractState != Contract.State.Active) continue;   // preserve Offered/Declined/etc.
    if (activeIdSet.Contains(c.ContractID)) continue;          // preserve Active contracts still in ledger
    c.Unregister();
    currentContracts.RemoveAt(i);
    removedCount++;
}
// NOTE: ContractsFinished is append-only game history. Parsek must not mutate it.
```

Then in the restore phase (lines 653–742), change the restore loop to **skip** contracts whose id is already present in `currentContracts` (they survived the filtered remove) — only create + register contracts genuinely missing.

**Delete dead code:** The `IsFinished` branch at `cs:723-726` (adding to `ContractsFinished`) is unreachable because `GetActiveContractIds` only yields actives. Delete it.

**Logging:** replace the existing summary log with:

```csharp
ParsekLog.Info(Tag,
    $"PatchContracts: removed {removedCount} stale Active contract(s), " +
    $"restored {restoredCount}, registered {registeredCount}; " +
    $"Offered/Finished preserved (ledgerActive={activeIds.Count}, kspTotal={currentContracts.Count})");
```

### §C. #397 — move `PendingScienceSubjects` clear

**File:** `Source/Parsek/RecordingStore.cs` + `Source/Parsek/GameActions/LedgerOrchestrator.cs` + `Source/Parsek/ChainSegmentManager.cs`

**Clear site inventory (six, per review §1.6):**

| # | File:line | Current | Action |
|---|---|---|---|
| 1 | `RecordingStore.cs:348-349` (`CommitRecordingDirect`) | Clear before orchestrator runs | **DELETE clear** |
| 2 | `RecordingStore.cs:393` (`ClearCommitted` reset) | Clear on full reset | Keep |
| 3 | `RecordingStore.cs:420` (`CommitTree` duplicate guard) | Clear on duplicate skip | Keep |
| 4 | `RecordingStore.cs:823-824` (`FinalizeTreeCommit` tail) | Clear before orchestrator runs | **DELETE clear** |
| 5 | `RecordingStore.cs:911` (`DiscardPendingTree`) | Clear on user discard | Keep |
| 6 | `RecordingStore.cs:1984` (`ResetForTesting`) | Test-only reset | Keep |

**Add new clear sites** (after orchestrator has read the list), in `try/finally`:

1. `LedgerOrchestrator.NotifyLedgerTreeCommitted` — after the foreach loop that calls `OnRecordingCommitted` for each recording in the tree. Wrap the foreach in `try { ... } finally { GameStateRecorder.PendingScienceSubjects.Clear(); ParsekLog.Verbose(Tag, "NotifyLedgerTreeCommitted: cleared PendingScienceSubjects"); }`.
2. `ChainSegmentManager.CommitSegmentCore` at `cs:527` — immediately after the single-recording `OnRecordingCommitted` call. Same `try/finally` shape.

**Do NOT** move the clear into `OnRecordingCommitted` tail — trees with multiple recordings would see the list cleared by the first recording, leaving subsequent recordings empty. The review verified both `NotifyLedgerTreeCommitted` (tree path) and `ChainSegmentManager.CommitSegmentCore` (chain segment path) are the only two upstream call-sites that read the list.

**Invariant:** "If `ConvertScienceSubjects` was called, `PendingScienceSubjects` will be cleared before the next commit cycle, regardless of whether anything threw."

**Logging:** each clear site logs a one-line summary with the cleared count.

### §D. #400 — capture milestone reward funds/rep

**File:** `Source/Parsek/GameActions/GameStateRecorder.cs` (modify `OnProgressComplete:667-713`)

**Approach (recommended — simplest, no new Harmony patch):** use `GameVariables.Instance.GetProgressFunds(body, node)` / `GetProgressRep(body, node)` / `GetProgressScience(body, node)`. The API has been stable since KSP 1.0. The existing "too fragile" comment at `cs:687-698` is over-cautious.

```csharp
private void OnProgressComplete(ProgressNode node)
{
    if (node == null || !GameStateStore.IsReady) return;

    var (milestoneId, body) = QualifyMilestoneId(node);  // existing helper

    double fundsReward = 0, scienceReward = 0;
    float repReward = 0;
    try {
        var gv = GameVariables.Instance;
        if (gv != null) {
            fundsReward = gv.GetProgressFunds(body, node);
            repReward = gv.GetProgressRep(body, node);
            scienceReward = gv.GetProgressScience(body, node);
        }
    } catch (Exception e) {
        ParsekLog.Warn(Tag, $"OnProgressComplete: GetProgress* threw for '{milestoneId}': {e.Message}");
    }

    var evt = new GameStateEvent {
        ut = Planetarium.GetUniversalTime(),
        type = GameStateEventType.MilestoneAchieved,
        key = milestoneId,
        detail = $"funds={fundsReward.ToString("R", CultureInfo.InvariantCulture)};" +
                 $"rep={repReward.ToString("R", CultureInfo.InvariantCulture)};" +
                 $"sci={scienceReward.ToString("R", CultureInfo.InvariantCulture)}",
        epoch = GameStateStore.CurrentEpoch
    };

    GameStateStore.AddEvent(evt);
    ParsekLog.Info(Tag,
        $"Milestone '{milestoneId}' achieved: funds={fundsReward:F0}, rep={repReward:F1}, sci={scienceReward:F1}");

    if (!IsFlightScene())
        LedgerOrchestrator.OnKscSpending(evt);
}
```

**Converter side:** `GameStateEventConverter.ConvertMilestoneAchieved` (`cs:460-482`) already parses `funds=`/`rep=`/`sci=` from `detail`. No converter change needed — it will pick up the real values automatically.

**Fallback plan (if `GetProgress*` returns 0 or throws consistently):** Harmony prefix/postfix on `ProgressNode.Complete` capturing `Funding.Instance.Funds` and `Reputation.Instance.reputation` deltas. Base method exists (verified via decompile in review §1.5). Use this only if smoke-testing the `GameVariables` path shows it doesn't work.

**Body context:** `QualifyMilestoneId` already walks reflection to extract `body` from body-specific nodes — reuse its output. For non-body progress nodes, `body = Planetarium.fetch?.Home`.

### §E. #403 composite — no new code, just verify closed by §A + §B + §D

**Finding from review §1.3:** The todo's claim that `CreateVesselCostActions` "silently bails" is a misdiagnosis. Reading `LedgerOrchestrator.cs:220-286`, it emits `FundsEarning(Recovery)` whenever `rec.TerminalStateValue == TerminalState.Recovered && rec.Points.Count >= 2 && recoveryAmount > 0`.

**Action:** do NOT touch `CreateVesselCostActions`. Instead:

1. After §A + §B + §D land, manually verify on a fresh career-mode playtest that recovery events now fire the existing path. If they don't, investigate whether `TerminalStateValue` is being set correctly during recovery commit — this is upstream of `CreateVesselCostActions` and would be a separate bug, not a bandaid in the converter.
2. Add a regression test that constructs a recording with `TerminalStateValue = Recovered` and two points with different `funds` values, runs `CreateVesselCostActions`, asserts one `FundsEarning(Recovery)` action emerges.
3. Leave the drop block in `GameStateEventConverter.cs:121-130` **as-is**. Re-emitting `FundsChanged` as `FundsEarning` would double-count against the existing recovery + contract + milestone channels. This is explicit anti-guidance — see review §3.1 and §5.1.

### §F. Dedup key collision fix for KSC `FundsSpending`

**File:** `Source/Parsek/GameActions/LedgerOrchestrator.cs`, `GetActionKey` (`cs:189-208`)

**Problem:** for `FundsSpending`, `GetActionKey` returns `a.RecordingId ?? ""`. KSC `PartPurchased` actions written via `OnKscSpending` have `recordingId=null`, so every one collides with every other on dedup — the first wins, all others at the same-ish UT are dropped.

**This must ship with §A** because §A adds `OnKscSpending` for `OnPartPurchased`, exposing the latent collision.

**Fix:** extend `GameAction` with a `DedupKey` string field (nullable). Populate it in `ConvertPartPurchased` with the part name from `evt.key`. Update `GetActionKey`:

```csharp
case GameActionType.FundsSpending:
    return (a.RecordingId ?? "") + ":" + (a.DedupKey ?? "");
```

For commit-path `FundsSpending` from `CreateVesselCostActions` (VesselBuild), `DedupKey` stays null; `RecordingId` is set, so keys remain unique.

**Regression test:** `LedgerOrchestratorTests` — seed two `PartPurchased` events at the same UT with different part names, run dedup, assert both survive.

### §G. #398 — stop writing `ContractOffered` to the store

**File:** `Source/Parsek/GameActions/GameStateRecorder.cs` (`OnContractOffered:154-166`)

Delete the `GameStateStore.AddEvent` call. Keep the handler subscribed (the diagnostic log is still useful).

```csharp
private void OnContractOffered(Contract contract)
{
    if (contract == null) return;
    ParsekLog.Verbose("GameStateRecorder",
        $"Game state: ContractOffered '{contract.Title ?? ""}' (diagnostic, not stored)");
}
```

**Downstream safety:** review §3.3 grepped the codebase — no UI or module reads `ContractOffered` from the store. Safe.

### §H. #402 — defensive `PatchFunds` WARN

**File:** `Source/Parsek/GameActions/KspStatePatcher.cs`, `PatchFunds:107-147`

Add after computing `delta`:

```csharp
if (delta < 0 && currentFunds > 1000.0 && Math.Abs(delta) > 0.10 * currentFunds)
{
    ParsekLog.Warn(Tag,
        $"PatchFunds: suspicious drawdown delta={delta:F1} from current={currentFunds:F1} " +
        $"(>10% of pool, target={targetFunds:F1}) — module may be missing earning actions. " +
        $"FundsModule.HasSeed={funds?.HasSeed}");
}
```

Mirror the same defensive block in `PatchScience` for symmetry.

**Do not make it a hard abort** — legitimate walks can subtract large amounts after a revert. WARN only.

**Testable helper:** extract the threshold check into `internal static bool IsSuspiciousDrawdown(double delta, double currentPool)` so `KspStatePatcherTests` can cover it directly.

### §I. #394 — commit-time reconciliation cross-check

**File:** `Source/Parsek/GameActions/LedgerOrchestrator.cs`, inside `OnRecordingCommitted` (tail, in the same `try/finally` as §C's clear or immediately before it).

**Goal:** the drop of `FundsChanged`/`ReputationChanged`/`ScienceChanged` remains silent at convert time, but at commit-time we reconcile the total change the store saw against the total change the ledger emitted. Disagreement = log WARN.

```csharp
// At the tail of OnRecordingCommitted, after convert runs:
double droppedFundsDelta = 0, droppedRepDelta = 0, droppedSciDelta = 0;
foreach (var evt in GameStateStore.Events) {
    if (evt.ut < rec.StartUT || evt.ut > rec.EndUT) continue;
    switch (evt.type) {
        case GameStateEventType.FundsChanged:       droppedFundsDelta += evt.valueAfter - evt.valueBefore; break;
        case GameStateEventType.ReputationChanged:  droppedRepDelta   += evt.valueAfter - evt.valueBefore; break;
        case GameStateEventType.ScienceChanged:     droppedSciDelta   += evt.valueAfter - evt.valueBefore; break;
    }
}

double emittedFundsDelta = /* sum of FundsEarning - FundsSpending this commit window */;
// ... similar for rep, sci ...

const double tol = 0.5;
if (Math.Abs(droppedFundsDelta - emittedFundsDelta) > tol)
    ParsekLog.Warn(Tag, $"Earnings reconciliation: funds dropped={droppedFundsDelta:F1} emitted={emittedFundsDelta:F1} — missing channel?");
// ... similar for rep, sci ...
```

Keep drop comments in `GameStateEventConverter.cs:121-130` explaining **why** these are dropped (review §5.1 — a future engineer must not "fix" them without double-counting).

### §J. #395 — `ScienceSubjectPatch` hardening

**File:** `Source/Parsek/Patches/ScienceSubjectPatch.cs:33-48`

Switch the source of truth from `GameStateStore.TryGetCommittedSubjectScience(...)` to `ScienceModule.GetSubjectCreditedTotal(subjectId)` (add this accessor to `ScienceModule` if it doesn't exist).

This makes the Science Archive display read from the same module the R&D pool patcher reads from — a broken ledger can no longer appear correct on one screen and wrong on another.

**Signal:** the review flagged this as low-effort, high insurance. Once the ledger is authoritative (after §C), this is a ~20-line change.

### §K. #401 + #396 — save recovery migrations

**File:** `Source/Parsek/GameActions/LedgerOrchestrator.cs`, new method `TryRecoverBrokenLedgerOnLoad`, called from `OnKspLoad` before `RecalculateAndPatch`.

**Design:**

```csharp
internal static void TryRecoverBrokenLedgerOnLoad()
{
    int recoveredFunds = 0, recoveredScience = 0, recoveredContracts = 0;
    int currentEpoch = GameStateStore.CurrentEpoch;  // respect epoch isolation — review §5.6

    // 1) Funds/contract events: for each Contract*/PartPurchased event in the store,
    //    check if the ledger has a matching action; if not, synthesize via ConvertEvent.
    foreach (var evt in GameStateStore.Events) {
        if (evt.epoch != currentEpoch) continue;
        if (!IsRecoverableEventType(evt.type)) continue;
        if (LedgerHasMatchingAction(evt)) continue;
        var action = GameStateEventConverter.ConvertEvent(evt, null);
        if (action == null) continue;
        Ledger.AddAction(action);
        if (evt.type == GameStateEventType.ContractAccepted || /* ... */) recoveredContracts++;
        else recoveredFunds++;
    }

    // 2) Science subjects: for each committed subject in the store, check if the ledger
    //    has a matching ScienceEarning; if not, synthesize.
    foreach (var subject in GameStateStore.CommittedScienceSubjects) {
        if (LedgerHasScienceEarningFor(subject.SubjectId, subject.Value)) continue;
        var action = BuildScienceEarningFromSubject(subject);
        Ledger.AddAction(action);
        recoveredScience++;
    }

    if (recoveredFunds > 0 || recoveredScience > 0 || recoveredContracts > 0) {
        ParsekLog.Warn(Tag,
            $"TryRecoverBrokenLedgerOnLoad: synthesized {recoveredFunds} funds/part, " +
            $"{recoveredContracts} contract, {recoveredScience} science actions from store " +
            $"(epoch={currentEpoch}). Ledger marked dirty.");
        Ledger.MarkDirty();
    }
}
```

**Epoch isolation (review §5.6):** migrations only act on events whose `epoch == CurrentEpoch`. Prevents old-branch events from leaking into the new epoch's ledger after a revert.

**Idempotency:** the `LedgerHasMatchingAction(evt)` guard makes repeat loads a no-op. No version flag needed.

**Shipping together with fixes:** the review (§G5) correctly calls out that c1 and sci1 are bricked RIGHT NOW, and shipping the code fix without the migration leaves those players stuck. It also means the implementer will actually exercise the migration on the real fixture saves during smoke testing.

**Fixture:** add a synthetic `broken-career.sfs` fixture under `Source/Parsek.Tests/Fixtures/` with N `ContractOffered`, 2 `ContractAccepted`, 8 `PartPurchased` events and zero corresponding actions. Integration test loads it, calls `TryRecoverBrokenLedgerOnLoad`, asserts the correct action count.

---

## 3. Implementation order

Tackle in this order — each step is independently buildable and testable:

1. **§F dedup key fix** — add `DedupKey` field, update `GetActionKey`, add regression test. Must land before §A because §A will immediately surface the collision.
2. **§A #405 `OnKscSpending` calls** — five one-line additions. Tests: each handler writes to ledger when called at KSC.
3. **§B #404 `PatchContracts` filtered remove** — replace destructive block. Tests: Offered + Finished preserved across recalc.
4. **§G #398 `OnContractOffered` no-store** — one-line delete. Tests: store.Count unchanged.
5. **§C #397 `PendingScienceSubjects` clear move** — delete clears at 348/824, add at `NotifyLedgerTreeCommitted` (after foreach) + `ChainSegmentManager.CommitSegmentCore:527`, both in `try/finally`. Tests: multi-recording tree reads all recordings' subjects before clear; throw in `DeduplicateAgainstLedger` still clears.
6. **§D #400 milestone reward capture** — `GameVariables.GetProgress*` integration. Tests: event detail contains non-zero funds/rep; converter reads them into ledger action.
7. **§H #402 defensive WARN** — `PatchFunds`/`PatchScience` sanity threshold. Tests: helper method + log assertion.
8. **§I #394 reconciliation diagnostic** — commit-time cross-check. Tests: mismatch produces WARN; match is silent.
9. **§J #395 `ScienceSubjectPatch` hardening** — source-of-truth switch. Tests: Archive displays value consistent with `ScienceModule`.
10. **§K #401/#396 save recovery migration** — `TryRecoverBrokenLedgerOnLoad`. Tests: fixture-based integration test + idempotency on second call.
11. **§E #403 verification** — manual smoke test against a fresh career save; add regression test for `CreateVesselCostActions` with `TerminalState.Recovered`. No code change expected here.

After each step: `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` must be green.

---

## 4. Tests (required — every step)

Test file conventions: `[Collection("Sequential")]` on anything touching `ParsekLog`, `GameStateStore`, `RecordingStore`, `LedgerOrchestrator`, `GameStateRecorder.PendingScienceSubjects`. Log-capture via `ParsekLog.TestSinkForTesting` (see `RewindLoggingTests.cs`).

**New test files:**

- `GameStateRecorderLedgerTests.cs` — cover §A. For each of the 5 handlers, assert that when called outside flight scene, `Ledger.Actions` gains the matching action type; when called inside flight, it doesn't.
- `PatchContractsPreservationTests.cs` — cover §B. Stub or mock `ContractSystem.Instance.Contracts` with Offered + Active + Finished entries; call `PatchContracts`; assert Offered untouched, Finished untouched, only stale Actives removed.
- `OnContractOfferedStoreTests.cs` — cover §G. Call handler, assert `GameStateStore.Events.Count` unchanged, assert verbose log.
- `PendingScienceSubjectsClearTests.cs` — cover §C. Three scenarios:
  1. Single-recording commit via `ChainSegmentManager.CommitSegmentCore` — list is cleared after orchestrator returns.
  2. Multi-recording tree commit via `NotifyLedgerTreeCommitted` — all recordings see the list before the post-foreach clear.
  3. `ConvertScienceSubjects` throws inside `OnRecordingCommitted` — `try/finally` still clears the list.
- `MilestoneRewardCaptureTests.cs` — cover §D. Stub `GameVariables.Instance.GetProgress*` to return `(4000, 2, 5)`; fire `OnProgressComplete`; assert event detail parses to non-zero; assert `ConvertMilestoneAchieved` produces an action with those values.
- `PatchFundsSanityTests.cs` — cover §H. `IsSuspiciousDrawdown(-5000, 10000) == true`, `IsSuspiciousDrawdown(-100, 10000) == false`, `IsSuspiciousDrawdown(+5000, 10000) == false`. Plus a log-assertion test that WARN fires.
- `EarningsReconciliationTests.cs` — cover §I. Seed mismatched dropped/emitted deltas, assert WARN log; match produces no log.
- `ScienceSubjectPatchHardeningTests.cs` — cover §J. Stub `ScienceModule.GetSubjectCreditedTotal`, assert patch reads from it not from store.
- `LedgerRecoveryMigrationTests.cs` — cover §K. Load a fixture with broken state, call `TryRecoverBrokenLedgerOnLoad`, assert correct action counts; second call is a no-op; epoch isolation check.
- `FundsSpendingDedupKeyTests.cs` — cover §F. Two KSC `PartPurchased` actions same UT, different part names, both survive dedup.

**Existing tests that must still pass:**

- `GameStateEventConverterTests.cs` — including the existing drop-block tests (regression guard per review §5.1 — `FundsChanged/ScienceChanged/ReputationChanged` still drop, no double-counting).
- `LedgerOrchestratorTests.cs` — existing `OnRecordingCommitted` cases.
- `CommitFlowTests.cs` — existing commit-path behavior.

Run `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` after each step of §3 and before commit. Report exact pass/fail counts in the implementation report.

---

## 5. Documentation updates

### CHANGELOG.md

**HARD RULE (see `.claude/CLAUDE.md`):** 1 line per item, ≤2 sentences, user-facing only. All technical detail goes in `docs/dev/todo-and-known-bugs.md` under the bug number.

**Group all bugs under a single entry** (review §G10):

```markdown
### Bug Fixes
- Fixed a cascade of career-mode ledger bugs that drained funds to zero, lost accepted contracts on scene transition, pinned science at the starting seed, and zeroed out milestone funds/rep rewards (#394, #395, #396, #397, #398, #400, #401, #402, #403, #404, #405). Broken sci1/c1 saves are repaired automatically on first load.
```

One line. Two sentences. All bug numbers included. Technical detail stays in todo.

### docs/dev/todo-and-known-bugs.md

For each of **#394, #395, #396, #397, #398, #400, #401, #402, #403, #404, #405**: strike-through the heading (`## ~~NNN. ...~~`), rewrite the Fix section to describe what actually landed (with file paths and commit shortlog), add `**Status:** ~~Fixed~~` at the end.

Keep **#399** (ComputeTotalSpendings same-UT walk) as open — still suspect/unverified; add a note that it's unblocked by this PR landing.

### docs/dev/plans/career-earnings-bundle.md

When the PR merges, move this plan doc + the review doc to `docs/dev/plans/done/`.

---

## 6. Done definition

1. `dotnet build` green in Release mode from `Source/Parsek/`.
2. `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` green — all new tests in §4 pass, all existing tests still pass.
3. No untouched `PendingScienceSubjects.Clear()` at `RecordingStore.cs:348` or `:824` (verify via grep).
4. `KspStatePatcher.PatchContracts` body contains no unconditional `.Clear()` on `currentContracts` or `ContractsFinished`.
5. `GameStateRecorder.OnContractOffered` does not call `GameStateStore.AddEvent`.
6. `GameStateEventConverter.cs:121-130` drop block is **still present** with an expanded comment explaining why (review §5.1 regression guard).
7. CHANGELOG.md has exactly ONE new entry covering all 11 bugs.
8. todo-and-known-bugs.md has 11 entries marked `~~Fixed~~`.
9. Manual smoke test: load c1 save, verify `TryRecoverBrokenLedgerOnLoad` logs `"synthesized N actions"`, verify no `PatchFunds` sanity WARN fires afterward, verify `Funding.Instance.Funds` is non-zero.
10. Manual smoke test: load sci1 save, verify science subjects are repaired, verify R&D pool reflects the 16.44 recovered science.

---

## 7. Known risks & open questions

See review document `career-earnings-bundle-review.md` §5 for the full list. The top risks carried forward into implementation:

- **Contract.Register() re-subscription side effects** (review §5.2) — preserving Active contracts across recalc means parameter subscriptions stay hot. Add integration test that walks 10 recalc cycles and asserts parameter subscription count is stable.
- **Harmony patch target stability for fallback milestone capture** (review §5.4) — only relevant if the `GameVariables` path fails. Document as fallback-only.
- **Epoch isolation on migration** (review §5.6) — explicit `epoch == CurrentEpoch` check in §K is mandatory.

---

## 8. Out of scope (defer)

- **#399** (`ScienceModule.ComputeTotalSpendings` same-UT walk) — suspect, unverified root cause. Easier to confirm after this PR lands and the ledger is accurate. Stays open in todo.

Everything else in the "career-mode earnings cascade" is in this PR.
