# Career Earnings Bundle — Review

## Meta: the plan file is missing

The reviewer was asked to audit `docs/dev/done/plans/career-earnings-bundle.md`. That file does **not exist** in this worktree. `docs/dev/plans/` only contains `fix-three-bugs.md`, `kerbals-cold-start-and-roster-coverage.md`, and `rsw-departure-aware-spawn-warp.md`. No career-earnings-bundle plan was authored.

This review therefore reports my independent findings and treats each as a "must-be-in-the-plan" item. Any future plan should be measured against the checklist at the bottom.

---

## 1. Independent findings (from source, before reading any plan)

### 1.1 Bug #405 — KSC contract/part events never reach the ledger

**Verified.** `GameStateRecorder.cs`:

- `OnContractAccepted` (`cs:168-210`), `OnContractCompleted` (`cs:212-225`), `OnContractFailed` (`cs:227-240`), `OnContractCancelled` (`cs:242-255`), `OnPartPurchased` (`cs:319-339`) all call `GameStateStore.AddEvent` but none call `LedgerOrchestrator.OnKscSpending(evt)`.
- For comparison: `OnTechResearched:316`, `OnKerbalAdded:380`, `OnProgressComplete:712`, `PollFacilityState:857` all have the `if (!IsFlightScene()) LedgerOrchestrator.OnKscSpending(evt);` tail.
- `LedgerOrchestrator.OnKscSpending(evt)` (`LedgerOrchestrator.cs:902`) dispatches via `GameStateEventConverter.ConvertEvent` and that switch already handles `ContractAccepted/Completed/Failed/Cancelled` and `PartPurchased` correctly (`GameStateEventConverter.cs:103-113, 88`). So the dispatch path exists — the handlers just never call it.

**Proposed fix:** one-line addition to each of the five handlers, guarded by `IsFlightScene()`:

```csharp
if (!IsFlightScene())
    LedgerOrchestrator.OnKscSpending(evt);
```

`OnContractOffered` deliberately does NOT get this call — see §1.5 (#398) below; offered is transient noise.

### 1.2 Bug #404 — `PatchContracts` destroys Offered + Finished buckets

**Verified.** `KspStatePatcher.PatchContracts` (`KspStatePatcher.cs:585-763`):

- Line 614–635 iterates `currentContracts` and unregisters every entry whose `ContractState == Active`. That part is fine — it avoids double registration on re-load.
- Line 637–640: `currentContracts.Clear(); ContractSystem.Instance.ContractsFinished.Clear();` — this is the destructive code. `currentContracts` holds ALL states (Offered, Active, Declined, Cancelled, Failed, Completed), not just Active. Blowing it away wipes the Offered bucket KSP uses for the Mission Control browse list. `ContractsFinished` is game history — Parsek has no module walking it, so nothing restores it.
- The restore phase (line 653–742) only walks `activeIds = contracts.GetActiveContractIds()`, which is keyed on `ContractsModule.activeContracts` — Accept-only. Offered and Finished are never repopulated.

**Root-cause chain:** After `#405` is fixed and the ledger has the Active contracts, `PatchContracts` will restore them correctly — but the Offered bucket still needs to survive. The minimal fix is to **not clear** `currentContracts` wholesale; instead, iterate and `Remove` only entries that are `Contract.State.Active` (and whose id is not in `activeIds`, to preserve Active-still-in-ledger ids in-place and avoid an unnecessary unregister/recreate cycle). `ContractsFinished.Clear()` must be deleted outright.

**Better-than-minimal fix:** only remove Active contracts whose id is absent from `activeIds`. Leave all other states alone. Then only create+register contracts whose id is in `activeIds` but not already present in `currentContracts`. This preserves as much live KSP state as possible and removes the destroy-then-regenerate anti-pattern (which is also what causes #398).

**`ContractsFinished`:** do not touch. Any future finished-state patcher should be additive (Add missing), never `Clear()`.

### 1.3 Bug #403 — `FundsEarning` / `ReputationEarning` never emitted

**Partially verified, partially wrong as stated in the todo.**

- The claim that `ConvertEvents` drops `FundsChanged` is **true** — `GameStateEventConverter.cs:121-130` explicitly `return null;` for `FundsChanged/ScienceChanged/ReputationChanged/CrewStatusChanged/CrewRemoved/ContractOffered/ContractDeclined/FacilityDowngraded`.
- The claim that `CreateVesselCostActions` "silently bails" is **likely false**. Reading `LedgerOrchestrator.cs:220-286`: it emits `FundsEarning(Recovery)` whenever `rec.TerminalStateValue == TerminalState.Recovered && rec.Points.Count >= 2 && recoveryAmount > 0`. If c1 had no FundsEarning actions, the most likely cause is that the recordings in c1 never ended with `TerminalState.Recovered` — they committed on revert or crash, not on recovery. The plan must not "fix" `CreateVesselCostActions`; it must verify whether recovery commits set the terminal state correctly, and if not, fix that path.
- Contract rewards: already extracted from the completed-event `detail` field in `ConvertContractCompleted` (`GameStateEventConverter.cs:372-404`). Once #405 is fixed and `ContractComplete` actions enter the ledger, this runs correctly. **No converter-side change needed for contracts.**
- Milestone rewards: currently 0. Handled via #400 below.

**Conclusion:** #403 is not a single bug. It is the union of {#400 milestone 0, #405 contract 0, and "Recovery action never runs because terminal state is wrong"}. The plan should **not** touch the converter's drop block to re-emit `FundsChanged` events as `FundsEarning` — that path has no dedup against the three existing earning paths and will double-count (see §5.1 hidden risk).

**The only defensive converter change worth making** is turning the `FundsChanged`/`ReputationChanged` drop from silent to a diagnostic assertion: at recording commit time, sum the effective earnings produced by the real channels and compare against the sum of `valueAfter - valueBefore` from the dropped `FundsChanged` events in the same window. Log a WARN if the two disagree by more than a tolerance. This is the cross-check #394 asks for.

### 1.4 Bug #402 — `PatchFunds` drags balance to 0

**Verified as a symptom.** `KspStatePatcher.PatchFunds` (`cs:107-147`) does `Funding.Instance.AddFunds(targetFunds - currentFunds, TransactionReasons.None)`. When `targetFunds == 0` (clamped by `FundsModule.GetAvailableFunds`), any positive current funds get wiped on the next recalc.

**Defensive guard (cheap, worth shipping):**

```csharp
if (delta < 0 && Math.Abs(delta) > 0.10 * currentFunds && currentFunds > 1000.0)
{
    ParsekLog.Warn(Tag,
        $"PatchFunds: suspicious drawdown delta={delta:F1} on current={currentFunds:F1} " +
        $"(>10% of pool) — module may be missing earning actions");
}
```

Do not make it a hard abort — legitimate walks can subtract large amounts after a revert. WARN only.

### 1.5 Bug #400 — Milestone rewards hardcoded to 0

**Verified.** `GameStateRecorder.cs:687-698` writes `detail = ""` for `MilestoneAchieved`. `ConvertMilestoneAchieved` (`GameStateEventConverter.cs:460-482`) reads `funds=` and `rep=` from detail; absent = 0.

**Decompiled ground truth:**
- `ProgressNode.Complete()` exists on the base class (confirmed via `ilspycmd Assembly-CSharp.dll -t ProgressNode`). It sets `complete=true`, sets `AchieveDate`, and fires `GameEvents.OnProgressComplete.Fire(this)`.
- `ProgressTracking.OnAchievementComplete` (registered for `OnProgressComplete`) is **empty** in stock KSP. The actual reward is applied by **subclass `Complete()` overrides** and/or other subscribers. A Harmony prefix/postfix on `ProgressNode.Complete` itself captures the pre- and post-Fire state — that works because `Complete()` fires synchronously and all subscribers run before `Complete()` returns. The postfix sees funds/rep after all reward subscribers have applied their deltas.
- **TransactionReasons.Progression = 0x80000** — confirmed via decompile. `Progression` is the reason used for milestone reward transactions.

**Fix approach (recommended):** Harmony prefix/postfix on `ProgressNode.Complete`:

```csharp
[HarmonyPatch(typeof(ProgressNode), nameof(ProgressNode.Complete))]
internal static class ProgressNodeCompletePatch {
    internal static void Prefix(ProgressNode __instance, out (double funds, float rep) __state) {
        __state = (
            Funding.Instance != null ? Funding.Instance.Funds : 0.0,
            Reputation.Instance != null ? Reputation.Instance.reputation : 0f
        );
    }
    internal static void Postfix(ProgressNode __instance, (double funds, float rep) __state) {
        // GameStateRecorder.CaptureMilestoneDelta(__instance, __state.funds, __state.rep);
    }
}
```

Then in `GameStateRecorder`, expose a pre-event cache keyed by `node.Id` that `OnProgressComplete` consumes when emitting the `MilestoneAchieved` event — because `OnProgressComplete` also fires from inside `ProgressNode.Complete`, the ordering is:

1. `ProgressNodeCompletePatch.Prefix` caches pre-values.
2. `ProgressNode.Complete` runs, fires `OnProgressComplete`.
3. Subscribers (reward appliers + `GameStateRecorder.OnProgressComplete`) run.
4. `ProgressNodeCompletePatch.Postfix` reads post-values and computes delta.

The problem: `GameStateRecorder.OnProgressComplete` needs the delta, but the postfix runs AFTER it. The clean solution is to flip the flow: postfix is where the `MilestoneAchieved` event is emitted (not `OnProgressComplete`), and `GameStateRecorder.OnProgressComplete` becomes a no-op OR stays as a validation log only. Alternatively, `OnProgressComplete` emits the event with the prefix-cached pre-value and the postfix patches the event's detail in-place once the delta is known.

**Simpler alternative — Harmony postfix on `Funding.AddFunds` filtered by `reason==Progression`:** capture the amount into a short-lived `lastProgressionFunds` static, read it in `OnProgressComplete`. Same for `Reputation.AddReputation`. Cheaper and less order-dependent than pre/post around `Complete()`.

**Even simpler — `GameVariables.Instance.GetProgressFunds(node)/GetProgressRep(node)`:** this API has been stable since KSP 1.0. The "too fragile" comment in `GameStateRecorder.cs:687-698` is likely over-cautious. Verify in decompile, then use it directly. This is the path of least regression risk — no new Harmony patch, no pre/post state juggling, no correlation concerns.

### 1.6 Bug #397 — PendingScienceSubjects cleared before orchestrator reads them

**Verified — confirmed by call-site trace.**

The flow is:
1. `CommitPendingTree()` → `CommitTree()` → `FinalizeTreeCommit()` (`RecordingStore.cs:799-842`).
2. `FinalizeTreeCommit` at line 823–824 calls `GameStateStore.CommitScienceSubjects(PendingScienceSubjects)` then `PendingScienceSubjects.Clear()`.
3. Caller (`ParsekFlight.cs:1410`) **then** calls `LedgerOrchestrator.NotifyLedgerTreeCommitted(treeToCommit)`.
4. `NotifyLedgerTreeCommitted` → `OnRecordingCommitted` (`LedgerOrchestrator.cs:97-140`) at line 106–108 calls `ConvertScienceSubjects(GameStateRecorder.PendingScienceSubjects, ...)`.
5. By step 5, the list is empty. Zero `ScienceEarning` actions ever enter the ledger.

Same story for `CommitRecordingDirect` at line 348–349 vs `ChainSegmentManager.cs:527`.

**Clear-site inventory (grep-verified):**

| # | File:line | Context | Current behavior | Needed action |
|---|---|---|---|---|
| 1 | `RecordingStore.cs:348-349` | `CommitRecordingDirect`, after `CommitScienceSubjects` | Clears before orchestrator runs | **Move out** — let tail-site handle it |
| 2 | `RecordingStore.cs:393` | `ClearCommitted` (nuke-all) | Clears on full reset | **Keep** — reset is a distinct path |
| 3 | `RecordingStore.cs:420` | `CommitTree` duplicate guard | Clears on duplicate skip | **Keep** — duplicate tree means the subjects belong to nothing; drop them |
| 4 | `RecordingStore.cs:823-824` | `FinalizeTreeCommit` tail | Clears before orchestrator runs | **Move out** |
| 5 | `RecordingStore.cs:911` | `DiscardPendingTree` | Clears on user discard | **Keep** — user discarded the tree |
| 6 | `RecordingStore.cs:1984` | `ResetForTesting` | Test-only reset | **Keep** |

**Fix:** delete clears at sites 1 and 4. Add a single clear in the tail of `LedgerOrchestrator.OnRecordingCommitted` **after** `ConvertScienceSubjects` has read them. **Important corner case:** the list is read inside `OnRecordingCommitted`, which is called once per recording in a tree. If a tree has multiple recordings, the FIRST `OnRecordingCommitted` call will clear the list, and subsequent recordings in the same tree will see an empty list. So the clear must happen in `NotifyLedgerTreeCommitted` AFTER the foreach loop, not inside `OnRecordingCommitted`.

But wait — `NotifyLedgerTreeCommitted` is only called for tree commits; `ChainSegmentManager.CommitSegmentCore` calls `OnRecordingCommitted` directly. So the clear must happen in BOTH places: after the foreach in `NotifyLedgerTreeCommitted`, and immediately after `OnRecordingCommitted` in `ChainSegmentManager.cs:527`.

**Additional corner case (`DeduplicateAgainstLedger` throw path):** the plan's todo says "add the clear to the failure path inside `DeduplicateAgainstLedger`". I disagree — if dedupe throws, we lose the science subjects but the exception propagates and the whole commit is in a half-broken state anyway. The right fix is a `try/finally` in `OnRecordingCommitted` that always clears after `ConvertScienceSubjects` runs, **regardless** of whether subsequent steps throw. This keeps the invariant "if the subjects were read, they are cleared" tight.

**Also needed:** `ScienceSubjectPatch` hardening (#395) — reading from `ScienceModule.GetSubjectCreditedTotal` instead of `GameStateStore.committedSubjects` removes a second source of truth and makes the regression un-maskable.

### 1.7 Bug #398 — ContractOffered accumulation

**Verified.** `OnContractOffered` (`GameStateRecorder.cs:154-166`) unconditionally calls `GameStateStore.AddEvent`. Each `PatchContracts` wipe causes `ContractSystem` to regenerate the Offered bucket on its next tick, firing `onOffered` for each new contract — and since they have fresh GUIDs, `GameStateStore.AddEvent` never deduplicates.

Root cause is #404 (clear-and-regenerate loop). Once #404 is fixed, the cascade stops.

**Additional minimal fix:** change `OnContractOffered` to NOT call `GameStateStore.AddEvent`. Offered contracts are not a player action; they are transient advertisements. Log at Verbose only. This fix is independent of #404 and prevents other future regressions in the same shape. Keep `onOffered` subscribed so we still get the diagnostic log.

### 1.8 Bug #394 — `ScienceChanged` dropped silently

**Verified.** Covered by the cross-check diagnostic in §1.3 above. The converter's drop block is the right place for `FundsChanged/ScienceChanged/ReputationChanged`, but the drop should be paired with a reconciliation log at commit time.

### 1.9 Bug #395 — `ScienceSubjectPatch` masking

**Verified as low-priority hardening.** See §1.6 tail.

### 1.10 Bug #401 — c1 save recovery migration

**Verified needed.** `LedgerOrchestrator.OnKspLoad` must detect "store has events that would have produced actions but ledger has zero matching actions" and synthesize the missing actions on load. For c1: iterate `GameStateStore.Events`, for each `Contract*/PartPurchased` event, check if a matching action exists in `Ledger.Actions` at the same UT; if not, synthesize via `ConvertEvent` and `Ledger.AddAction`. Run recalculation after.

### 1.11 Bug #396 — sci1 save recovery migration

**Verified needed.** Iterate `GameStateStore.CommittedScienceSubjects`, check if `Ledger.Actions` has a matching `ScienceEarning`, synthesize if missing. Same shape as #401.

---

## 2. Agreements with (a hypothetical) plan

N/A — no plan exists. See the "must-fix checklist" in §6 as the substitute for this section.

---

## 3. Disagreements — N/A

No plan to disagree with. The section below preserves the items the user asked to check against a phantom plan.

### 3.1 `GameStateEventConverter.cs:121-130` drop block

**User asked:** "what are the EXACT event types being dropped and why? Is the reviewer's proposed split (emit on `VesselRecovery`, drop others) actually correct — or are there other valid earning channels the plan misses?"

Exact types dropped: `FundsChanged`, `ScienceChanged`, `ReputationChanged`, `CrewStatusChanged`, `CrewRemoved`, `ContractOffered`, `ContractDeclined`, `FacilityDowngraded`.

A "split on `VesselRecovery`" approach is **wrong** and must not land in the plan:

- Recovery earnings are already handled by `LedgerOrchestrator.CreateVesselCostActions` reading recording point `funds` deltas. Also emitting `FundsEarning(Recovery)` from `FundsChanged(VesselRecovery)` would **double-count** the recovery — two actions at the same UT, neither dedup-covered because the dedup key for `FundsEarning` is `RecordingId`, which would be the same for both.
- Contract rewards: captured by `ConvertContractCompleted` extracting `fundsReward` from the event `detail`. Also emitting `FundsEarning(ContractReward)` from `FundsChanged` would double-count.
- Progression (milestone): captured via `MilestoneAchievement.MilestoneFundsAwarded` once #400 is fixed. Also emitting `FundsEarning(Progression)` from `FundsChanged` would double-count.
- Strategies (`StrategyInput/Output`) and Cheating are edge cases that the current architecture does not handle at all; a long-term design would funnel them through the same dedicated channels, not through a generic `FundsChanged` → `FundsEarning` bridge.

**The correct posture** for the drop block is: keep it as a drop, add a commit-time reconciliation cross-check that sums the dropped `valueAfter - valueBefore` and compares against the sum of effective earnings actions the current commit window produced. If they disagree, log WARN with the delta and the channel breakdown.

### 3.2 `RecordingStore.cs` four clear sites

**User asked:** "read all 4 and verify that moving the clear to `LedgerOrchestrator.OnRecordingCommitted` tail does not leak the list on paths that don't go through commit (rewinds, scene transitions, quickload-discard, error paths, `DeduplicateAgainstLedger` throw)."

There are actually **six** clear sites (see §1.6 inventory). The plan must treat sites 2, 3, 5, 6 as "keep" (they are discard paths, not commit paths) and only move sites 1 and 4.

**Leak paths to verify on rewind/quickload/scene-transition/error:**

- **Rewind:** `RewindContext.ResetForTesting` (test-only) and `ParsekFlight.cs` rewind path — verify the rewind sequence calls `DiscardPendingTree` or `ClearCommitted` on its discard branch; both keep the clear. If rewind instead calls `CommitPendingTree`, the new "clear in orchestrator tail" path handles it.
- **Scene transition:** `ParsekScenario.OnSave/OnLoad` — if scene transition commits any in-flight data, it must go through `NotifyLedgerTreeCommitted` for the clear to fire. Verify there is no orphan path that lands in `committedRecordings` without touching the orchestrator.
- **Quickload-discard:** `DiscardPendingTree` keeps the clear. Good.
- **Error paths:** `DeduplicateAgainstLedger` throw — wrap the tail of `OnRecordingCommitted` in a `try/finally` so the clear is guaranteed when `ConvertScienceSubjects` has already returned. **Do not** rely on `DeduplicateAgainstLedger` throwing "gracefully" — C# exceptions are exceptions.

### 3.3 `KspStatePatcher.PatchContracts` downstream dependencies

**User asked:** "Is there any downstream code that depends on `ContractsFinished` being empty, or on the offered bucket being re-populated by Parsek after clear?"

No. Grep of `ContractsFinished` in the Parsek sources shows only the one use-site in `PatchContracts:640, 725`. The `725` line (adding restored contracts with `IsFinished()` state to `ContractsFinished`) is reachable only when a ledger snapshot restores a finished-state contract, which never happens because `GetActiveContractIds()` only yields actives. The entire Finished branch is dead code in practice and can be deleted along with the `Clear()` at line 640.

No Parsek code depends on the Offered bucket being re-populated — KSP's own `ContractSystem` tick generates Offered contracts. Parsek must simply not destroy them.

### 3.4 `OnKscSpending` dispatch for contract event types

**User asked:** "does it actually correctly handle the contract event types listed in the plan (`ContractAccept`, `ContractComplete`, `ContractFail`, `ContractCancel`), or does the plan assume a dispatch path that doesn't exist?"

It does — `LedgerOrchestrator.OnKscSpending(evt)` at `cs:902-926` calls `GameStateEventConverter.ConvertEvent(evt, null)`, and the converter's switch at `cs:103-113` maps all four contract events to `ConvertContract*` which build the right `GameAction`. Verified end-to-end.

**One caveat:** `OnKscSpending` assigns `action.Sequence = kscSequenceCounter++` (line 916–917) for deterministic ordering. On a recording-commit path (`NotifyLedgerTreeCommitted`), the same contract event might be captured twice — once via the real-time `OnKscSpending` call (new, after #405 fix) and once via `ConvertEvents` during commit. `DeduplicateAgainstLedger` handles this match-by-(type, UT, key) and drops the commit-time duplicate. So the #405 fix is dedup-safe as long as `GetActionKey` returns the correct key for contracts — which it does (`cs:201-204`: `ContractId`).

### 3.5 `GetActionKey` collision risk for `PartPurchased`

**User asked:** "The plan flags a latent collision risk for `PartPurchased` at KSC — verify the claim and propose whether to include it in this PR or defer."

Verified. `GetActionKey` for `FundsSpending` returns `a.RecordingId ?? ""` (`cs:196`). For KSC `PartPurchased` actions written via `OnKscSpending`, the converter passes `recordingId=null` (`cs:906`), so the action gets `RecordingId = null` → `GetActionKey` returns `""`. **Every KSC PartPurchased collides with every other KSC PartPurchased** in dedup — the first one wins, all others at the same-ish UT get dropped as "duplicate".

This is a real bug and must ship in the same PR as #405. The fix is to use the event's `key` (part name) as the dedup key for `FundsSpending(Other)`:

```csharp
case GameActionType.FundsSpending:
    return a.RecordingId ?? "" + ":" + (a.FundsSpendingSource == FundsSpendingSource.Other
        ? (a.PartName ?? "")   // or wherever the part name is stored
        : "");
```

Or more generally: add a `PartName` field (or reuse an existing one) on `GameAction` for `FundsSpending(Other)` and include it in the dedup key.

If this is out-of-scope for the first PR, then `#405` fix for `OnPartPurchased` **should be deferred** until the dedup key is corrected, because committing `OnPartPurchased` real-time with a broken dedup is worse than leaving `PartPurchased` in the commit-window path where `recordingId` is non-null and the collision doesn't bite.

### 3.6 `ProgressNode.Complete` Harmony target

Verified via decompile. Method exists on the base class, and subclass rewards apply synchronously through the `OnProgressComplete` fire-chain. See §1.5 for the pre/post-state capture approach and the simpler `GameVariables.GetProgressFunds` alternative.

### 3.7 `TransactionReasons` enum stringification

Verified via decompile:

```csharp
[Flags]
public enum TransactionReasons {
    ...
    VesselRecovery = 0x20,
    Progression = 0x80000,
    ContractReward = 4,
    ...
}
```

`reason.ToString()` on `VesselRecovery` yields `"VesselRecovery"`. Confirmed. Caveat: it's a `[Flags]` enum, so bitwise combinations produce comma-separated strings like `"ContractReward, VesselRecovery"`. KSP's own call sites all pass single flags, but any future KSP patch or mod that combines flags would cause string comparison misses. Use `(reason & TransactionReasons.VesselRecovery) != 0` for robustness, not string match. This makes the "split on reason" approach (which I argued against anyway in §3.1) even more fragile.

---

## 4. Gaps

### Blockers

**G1. `PartPurchased` dedup collision (§3.5).** Any plan that adds `OnKscSpending` to `OnPartPurchased` without fixing `GetActionKey` for `FundsSpending` will cause KSC part-purchase actions to silently drop each other. Must ship in the same PR.

**G2. Tree-level clear location (§1.6).** The naive fix of "clear in `OnRecordingCommitted` tail" clears the list on the first recording in a tree, leaving subsequent recordings with nothing to read. The clear must happen in `NotifyLedgerTreeCommitted` after the foreach, AND in `ChainSegmentManager.CommitSegmentCore` after the orchestrator call. Needs a `try/finally` wrapper to handle thrown exceptions.

**G3. `CreateVesselCostActions` terminal-state dependency.** The todo's suggestion that `CreateVesselCostActions` "silently bails" is a misdiagnosis. It only runs for `TerminalState.Recovered` recordings. Verify c1 recordings were committed as `Recovered` — if not, the real fix is upstream in the commit path, not in `CreateVesselCostActions`. The plan must call this out explicitly so the implementer doesn't go "fix" the wrong file.

### Major

**G4. Cross-check (#394) must be in-scope.** Leaving `FundsChanged`/`ScienceChanged`/`ReputationChanged` as a silent drop is what allowed #397/#403/#400 to go undetected for this long. Wire it into the `OnRecordingCommitted` tail as a log-only reconciliation. Extremely cheap. Would have caught all three bugs on day one.

**G5. `#401` / `#396` save-recovery migrations are not optional.** The c1 and sci1 saves are bricked. Shipping the code fix without the one-shot migration leaves those players stuck. Both migrations must be in the same PR as the fixes — not a follow-up — otherwise the fix ships to testers who can't reproduce the baseline bad state and the migration goes untested.

**G6. `OnContractOffered` should stop calling `AddEvent` (#398).** One-line change, removes accumulation even after #404 is fixed, prevents any future clear-and-regenerate cascade. No reason to defer.

**G7. `ScienceSubjectPatch` hardening (#395).** Switch to `ScienceModule.GetSubjectCreditedTotal` as the single source of truth. Prevents the display layer from ever hiding a ledger regression again. Low effort, high insurance value.

**G8. Defensive `PatchFunds` WARN (#402).** Five-line defensive log. Ship it independently of the #403 fix. Cheap insurance if any future bug produces a similar drain.

### Minor

**G9. `PatchContracts` dead code.** The `IsFinished` branch at line 723–726 is unreachable with current `GetActiveContractIds`. Delete when touching this method.

**G10. CHANGELOG hard-rule compliance.** The CLAUDE.md rule is 1 line, ≤2 sentences, user-facing. Nine bugs × multiple lines each will blow this up if each bug gets its own CHANGELOG entry. Group them under a single user-facing line: *"Fixed a cascade of career-mode funding bugs that drained funds to zero after commits and prevented contract/part/milestone rewards from reaching the ledger (#397, #400, #402, #403, #404, #405)."* All technical detail belongs in `todo-and-known-bugs.md` under each bug number.

---

## 5. Hidden risks

### 5.1 Double-counting if anyone "fixes" the drop block

If a future engineer, reading the bug reports, decides to "simply stop dropping `FundsChanged`", they will introduce silent double-counting with the recovery channel and the milestone channel. The drop block must have a comment explaining WHY and a unit test asserting the drop still happens for `FundsChanged` in a commit that already has a `FundsEarning(Recovery)` action.

### 5.2 `Contract.Register()` re-subscription side effects

`PatchContracts` currently unregisters all Active contracts before rebuilding. After the `#404` fix, contracts that remain Active across a recalc will NOT be unregistered (we're only removing the ones not in `activeIds`). This means their `Register()` → parameter subscriptions stay hot. Most KSP contracts are fine with this, but mods that assume parameters get re-subscribed on every recalc cycle may break. Risk is low but real — add a regression test that loads a career save with one Active contract and walks ten recalc cycles, asserting the contract's parameter subscription count stays stable.

### 5.3 `CrewHired` double-count risk

`OnKerbalAdded:380` already has `OnKscSpending`. But `CreateKerbalAssignmentActions` also creates `KerbalAssignment` actions at commit time from vessel snapshot. These are different action types (`KerbalHire` vs `KerbalAssignment`), so they don't dedup against each other via `GetActionKey`. If they should dedup (e.g., the first flight of a just-hired pilot), there's latent drift. Not critical for this bundle but worth a dedicated check.

### 5.4 Harmony patch target stability

Patching `ProgressNode.Complete` is stable (base class method, stable for 10 years). Patching `Funding.AddFunds` filtered by reason is also stable. But several reward-applying subscribers run inside `OnProgressComplete.Fire()`, and the ORDER in which they run is undefined. If a mod's `OnProgressComplete` handler runs BEFORE Parsek's `OnProgressComplete` handler AND Parsek reads post-reward state from that handler, the order dependency is fragile. Preferred: do the capture in the `Complete` postfix (after all Fire subscribers have run), not inside Parsek's `OnProgressComplete`.

### 5.5 `GameVariables.GetProgressFunds` body dependency

The `GameVariables.GetProgressFunds(node)` API is body-dependent — `Mun/Landing` yields different funds than `Kerbin/Landing`. Whatever captures the reward must pass the right `CelestialBody` context. The existing `QualifyMilestoneId` already walks reflection for the body field — reuse that logic. Alternatively, snapshot via pre/post deltas and sidestep this entirely.

### 5.6 Epoch isolation on save-recovery migrations (#401/#396)

`GameStateEvent.epoch` tags events to a specific game epoch. The migration must respect the current epoch — only re-synthesize actions for events matching `MilestoneStore.CurrentEpoch`, otherwise old-branch events leak into the new epoch's ledger after a revert. Not obvious from the bug text; must be in the plan.

---

## 6. Verdict

**REJECT** — because the plan file does not exist. There is nothing to approve or revise. Write a plan first.

### Must-fix checklist for the plan author

When the plan is written, it must include all of the following — no more, no less:

1. **#405 capture fix** — five `OnKscSpending` call additions to `OnContractAccepted/Completed/Failed/Cancelled/OnPartPurchased`, guarded by `IsFlightScene()`. One-line each. Must come with §G1 dedup key fix.

2. **#404 patcher fix** — replace `currentContracts.Clear()` and `ContractsFinished.Clear()` with filtered removal that only touches `Contract.State.Active` entries not in `activeIds`. Delete the `ContractsFinished` clear entirely. Delete the dead `IsFinished` restore branch.

3. **#400 milestone reward capture** — recommend `GameVariables.Instance.GetProgressFunds/Rep/Sci(body, node)` as the primary approach, with Harmony pre/post on `ProgressNode.Complete` as a fallback. Write the captured values into the `MilestoneAchieved` event `detail` field so `ConvertMilestoneAchieved` picks them up without further changes.

4. **#397 PendingScienceSubjects leak** — move clear OUT of `RecordingStore.CommitRecordingDirect:349` and `FinalizeTreeCommit:824`. Add clear to `NotifyLedgerTreeCommitted` after the foreach loop (in a `try/finally`), and to `ChainSegmentManager.CommitSegmentCore:527` after `OnRecordingCommitted` returns (also in `try/finally`). Keep clears at `ClearCommitted:393`, `CommitTree` duplicate-guard:420, `DiscardPendingTree:911`, `ResetForTesting:1984`.

5. **#398 Offered accumulation** — delete `GameStateStore.AddEvent` call in `OnContractOffered`, keep the log.

6. **#394 cross-check diagnostic** — reconcile dropped `FundsChanged`/`ReputationChanged`/`ScienceChanged` sums against effective earnings in the commit window. Log-only WARN on mismatch. Runs at `OnRecordingCommitted` tail.

7. **#402 defensive WARN** — add "removing >10% of pool in one call" WARN to `PatchFunds`. Five lines. Ships independently.

8. **#395 ScienceSubjectPatch hardening** — switch to reading from `ScienceModule.GetSubjectCreditedTotal` instead of `GameStateStore.committedSubjects`.

9. **#401 + #396 save-recovery migrations** — iterate `GameStateStore.Events` and `GameStateStore.CommittedScienceSubjects` on `OnKspLoad`, synthesize missing actions, run one recalc pass. Respect `MilestoneStore.CurrentEpoch` (see §5.6). Must ship in the same PR — not a follow-up.

10. **Dedup key collision fix (#G1)** — `GetActionKey` for `FundsSpending` must include the part/event key when `RecordingId` is null; otherwise KSC `PartPurchased` actions silently collide.

### Tests the plan must specify

- `GameStateRecorderTests`: each of the 5 #405 handlers emits an `OnKscSpending` call. Assert via `ParsekLog.TestSinkForTesting`.
- `KspStatePatcherTests`: `PatchContracts` preserves Offered and ContractsFinished buckets; only active-not-in-ledger contracts get removed.
- `LedgerOrchestratorTests`: `OnRecordingCommitted` reads `PendingScienceSubjects` before clear; `NotifyLedgerTreeCommitted` with a two-recording tree reads BOTH recordings worth of subjects before clearing.
- `GameStateEventConverterTests`: `GetActionKey` for `FundsSpending(Other)` with `RecordingId=null` must disambiguate by part name.
- `MilestonesModuleTests`: milestone with captured funds/rep flows into FundsModule/ReputationModule correctly.
- `FundsModuleTests`: regression — a commit window with a dropped `FundsChanged(VesselRecovery)` event and a `FundsEarning(Recovery)` action produces exactly ONE running-balance increment, not two.
- `KspStatePatcherTests`: `PatchFunds` logs WARN when delta > 10% of pool.
- `GameStateEventConverterTests`: `FundsChanged`/`ReputationChanged`/`ScienceChanged` are still dropped (regression guard for §5.1).
- Save-recovery migration: load a synthetic `events.pgse` with 2 accepted contracts and 8 part-purchased events, empty ledger; after `OnKspLoad`, assert ledger has 10 matching actions and `FundsModule.GetAvailableFunds()` is non-zero.
- Log-capture tests for all new logging (CLAUDE.md requirement).
- All tests touching shared static state must carry `[Collection("Sequential")]`.

### Documentation the plan must touch

- `CHANGELOG.md` — ONE line grouping all bugs, user-facing only, ≤2 sentences (CLAUDE.md HARD RULE).
- `docs/dev/todo-and-known-bugs.md` — mark each of #394, #395, #396, #397, #398, #400, #401, #402, #403, #404, #405 as ~~done~~; if any are intentionally deferred (e.g., #399 suspect), keep them open with an explicit note.
- `docs/dev/done/plans/career-earnings-bundle.md` — write the plan, then fold it into `done/` when the PR merges.
- No `.claude/CLAUDE.md` update needed — no workflow change.
