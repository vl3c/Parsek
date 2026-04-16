# Career Earnings Bundle — Implementation Review

Reviewer: independent audit of `fix/career-earnings-bundle` (12 commits ahead of `origin/main`), Apr 15 2026.
Environment: `KSPDIR=C:\Users\vlad3\Documents\Code\Parsek\Kerbal Space Program`.

---

## 1. Verdict

**APPROVE WITH MINOR REVISIONS.**

The bundle lands all 11 bugs (#394, #395, #396, #397, #398, #400, #401, #402, #403, #404, #405) plus the §F latent dedup-key collision. Every plan section has an implementation that matches the plan intent, with two deviations that are justified and documented in the commits (see §6). Build is clean in Release; the full test suite is green (6272 pass, 1 pre-existing skip).

### Must-fix punch list

None are merge-blockers. The items below are revision-grade (fix in a follow-up push before merge or track as an explicit follow-up todo, reviewer's preference):

1. **§C throw-path test is a no-op** (`PendingScienceSubjectsClearTests.NotifyLedgerTreeCommitted_OrchestratorThrows_StillClears`). The test author acknowledged it can't actually trigger a throw inside `OnRecordingCommitted`, so the `try/finally` invariant is asserted by code inspection only. Add a fault-injection hook (internal static `Action<Recording>` on `LedgerOrchestrator` that the test can set to throw) or delete the test and leave a comment in the plan review noting the gap.
2. **§K silent gap: historical `MilestoneAchieved`, `CrewHired`, `TechResearched`, and `FacilityUpgraded` events are never migrated.** `IsRecoverableEventType` only accepts five types. The plan acknowledges milestones are deliberately excluded (historical zero-reward) but is silent on the other three. For c1/sci1 this is fine (those saves don't need those paths), but a broken save with missing tech/facility/hire actions would silently stay broken on load. Flag as an explicit acceptable gap in the todo and reconsider before calling the migration "complete."
3. **§K synthesized `ScienceEarning` uses `UT = 0.0`**, which ignores when the subject was actually earned. Harmless for recovery but can confuse any code that sorts or partitions actions by UT. Document the choice or use `MilestoneStore.CurrentEpoch`'s start UT if one is tracked.
4. **§D `EnrichPendingMilestoneRewards` mutates `Ledger.Actions[i]` through the element reference without signaling the recalc engine** that a prior action changed (line 819-820). `GameAction` is a class so the mutation is visible, but any cached derived state (per-module running totals) may be stale until the next `RecalculateAndPatch`. Verify that milestone enrichment is always followed by a recalc — it appears to not be, because the postfix fires mid-`Complete()`, well before any commit. Worth a smoke test in live KSP.
5. **§D `ProgressRewardPatch.Postfix`'s catch swallows exceptions** and logs WARN only. This is defensively correct at a Harmony boundary, but the plan asks for no swallowed catches without logging. It *is* logged, so this passes the rule — noting for clarity.

None of the above blocks merge in my judgment.

---

## 2. Build + test results

### `dotnet build -c Release` (from `Source/Parsek/`)

```
Parsek -> C:\Users\vlad3\Documents\Code\Parsek-career-earnings-bundle\Source\Parsek\bin\Release\Parsek.dll
Copied to C:\Users\vlad3\Documents\Code\Parsek\Kerbal Space Program\GameData\Parsek\Plugins

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Release build is clean.

### `dotnet test` (from `Source/Parsek.Tests/`)

```
Passed!  - Failed:     0, Passed:  6272, Skipped:     1, Total:  6273, Duration: 6 s
```

Zero failures. The single skip is a pre-existing `GhostPlaybackEngineTests.SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT` case that requires Unity runtime (covered by the in-game suite). Not related to this bundle.

---

## 3. Per-step verification table

| Plan | Commit | File(s) | Plan match | Test adequacy | Logging | Notes |
|---|---|---|---|---|---|---|
| §F dedup key | `0dac08f7` | `GameAction.cs`, `GameStateEventConverter.cs`, `LedgerOrchestrator.cs` | yes | strong (6 cases) | n/a (pure data) | `DedupKey` added to `GameAction`, `ConvertPartPurchased` populates from `evt.key`, `GetActionKey` returns `recId + ":" + dedup`. Exactly as planned. |
| §A #405 OnKscSpending | `e6f731fc` | `GameStateRecorder.cs`, `GameStateRecorderLedgerTests.cs` | yes | strong (7 cases) | adequate (existing `KSC spending recorded` log in `OnKscSpending`) | All 5 handlers (`OnContractAccepted/Completed/Failed/Cancelled/PartPurchased`) append `if (!IsFlightScene()) LedgerOrchestrator.OnKscSpending(evt);`. `OnContractOffered` intentionally omitted. |
| §B #404 PatchContracts | `9df97225` | `KspStatePatcher.cs`, `PatchContractsPreservationTests.cs` | yes | strong (7 cases) | adequate (refreshed summary log) | Old destructive `Clear()`+`ContractsFinished.Clear()` gone. Filter-remove via `PartitionContractsForPatch` (pure static helper). Dead `IsFinished` branch deleted. Uses `ContractGuid` (Guid), not `ContractID` (long). |
| §G #398 ContractOffered no-store | `afdc702f` | `GameStateRecorder.cs`, `OnContractOfferedStoreTests.cs` | yes | strong (3 cases incl. IL-scan regression guard) | Verbose diagnostic log retained | One-line delete of `GameStateStore.AddEvent`. Reflection IL-scan test will trip if anyone re-adds an `AddEvent` call. |
| §C #397 PendingScienceSubjects | `575a3653` | `RecordingStore.cs`, `LedgerOrchestrator.cs`, `ChainSegmentManager.cs`, `ParsekFlight.cs`, `PendingScienceSubjectsClearTests.cs` | yes | acceptable (5 cases; throw-path test weakened — see deep-dive) | adequate (Verbose log with before/atClear counts at each clear site) | Old clears at `RecordingStore.cs:348/824` gone. Three new try/finally clear sites: `NotifyLedgerTreeCommitted` (tree path, post-foreach), `CommitSegmentCore` (chain path), `FallbackCommitSplitRecorder` (direct path — the third upstream site the implementer found). Discard-path clears preserved. |
| §D #400 milestone rewards | `dcd7c77e` | `GameStateRecorder.cs`, `GameStateStore.cs`, `Patches/ProgressRewardPatch.cs`, `MilestoneRewardCaptureTests.cs` | yes (with justified deviation — see §6) | strong (7 cases) | adequate (Info log on enrichment, Verbose on unknown node) | Pivoted from `GameVariables.GetProgress*` (doesn't exist in KSP) to Harmony postfix on `ProgressNode.AwardProgress`. Two-phase flow: zero-reward event emitted by `OnProgressComplete`, enriched in-place by the postfix via `EnrichPendingMilestoneRewards`. `IsReplayingActions` suppression guard present. `GameStateStore.UpdateEventDetail` added for struct-safe mutation. |
| §H #402 drawdown WARN | `1921afca` | `KspStatePatcher.cs`, `PatchFundsSanityTests.cs` | yes | strong (7 cases) | WARN on threshold crossing | `IsSuspiciousDrawdown` pure helper extracted. WARN added in both `PatchFunds` and `PatchScience`. Never aborts. |
| §I #394 reconciliation | `1a81c698` | `GameStateEventConverter.cs`, `LedgerOrchestrator.cs`, `EarningsReconciliationTests.cs` | yes | strong (8 cases) | WARN on mismatch, silent on match | `ReconcileEarningsWindow` pure static, called at tail of `OnRecordingCommitted` (after dedup, before recalc). Drop block at `GameStateEventConverter.cs:121-130` preserved with expanded comment explaining WHY. |
| §J #395 ScienceSubjectPatch | `39498f76` | `Patches/ScienceSubjectPatch.cs`, `ScienceSubjectPatchHardeningTests.cs` | yes | strong (5 cases) | n/a (read-path postfix) | `TryResolveCommittedScience` reads from `LedgerOrchestrator.Science.GetSubjectCredited` (the plan said `GetSubjectCreditedTotal`, actual method is `GetSubjectCredited` — same semantics). Store fallback only when orchestrator uninitialized. |
| §K #401/#396 save recovery | `7e04cc63` | `LedgerOrchestrator.cs`, `GameStateStore.cs`, `ParsekScenario.cs`, `LedgerRecoveryMigrationTests.cs` | partial (silent gap for MilestoneAchieved/CrewHired/TechResearched/FacilityUpgraded — see §4.f and revisions) | strong (12 cases) | Info/Verbose/Warn appropriately | `TryRecoverBrokenLedgerOnLoad` synthesizes for 5 event types + science subjects. Epoch isolation via `MilestoneStore.CurrentEpoch`. Idempotency via `LedgerHasMatchingAction` / `LedgerHasMatchingScienceEarning`. Called after `LedgerOrchestrator.OnLoad()` and epoch restore, before main recalc path runs. |
| §E #403 verification | `96f28bf7` | `VesselCostRecoveryRegressionTests.cs` | yes | strong (3 cases) | n/a | No code change, as planned. Regression test locks in `CreateVesselCostActions` behavior for `TerminalState.Recovered`. |

---

## 4. Deep-dive findings

### a. §C `PendingScienceSubjects` clear correctness

**Verified correct on source inspection. Tests are weaker than the invariant they claim to lock in.**

Old clear sites at `RecordingStore.cs:348/824` are gone. Grep confirms the surviving `PendingScienceSubjects.Clear()` call sites are:

- `RecordingStore.cs:397` — `ClearCommitted` (reset path, keep)
- `RecordingStore.cs:424` — `CommitTree` duplicate guard (keep)
- `RecordingStore.cs:917` — `DiscardPendingTree` (keep)
- `RecordingStore.cs:1990` — `ResetForTesting` (keep)
- `ParsekFlight.cs:5608` — `ClearRecording` (user-initiated, legitimate discard)
- `ParsekScenario.cs:206` — Quickload discard (legitimate)
- **New: `ChainSegmentManager.cs:539`** (inside `try/finally` after `OnRecordingCommitted`)
- **New: `LedgerOrchestrator.cs:1427`** (inside `try/finally` after the `foreach` in `NotifyLedgerTreeCommitted`)
- **New: `ParsekFlight.cs:2429`** (inside `try/finally` in `FallbackCommitSplitRecorder` — the third upstream site the implementer flagged)

`OnRecordingCommitted` itself does NOT clear the list — correct, because in the tree path a second recording in the same commit batch must still see the same subjects. The clear fires after the foreach in `NotifyLedgerTreeCommitted` and after the single `OnRecordingCommitted` in `ChainSegmentManager.CommitSegmentCore` / `FallbackCommitSplitRecorder`.

**Throw-path caveat:** The test `NotifyLedgerTreeCommitted_OrchestratorThrows_StillClears` cannot actually trigger a throw inside `OnRecordingCommitted`. The test body comment admits this: *"we skip actual NPE creation (dict rejects null values by convention) and instead test the happy path plus a separately-crafted 'no orchestrator' assertion."* The `try/finally` in the source is still present (verified by reading `LedgerOrchestrator.cs:1083-1430`), so the invariant holds — but the test is effectively a happy-path duplicate of an earlier test. Recommend either a fault-injection hook or deleting the test and accepting the gap.

**Multi-recording tree test** (`NotifyLedgerTreeCommitted_MultiRecording_AllRecordingsSeeSubjectsBeforeClear`) does exercise the critical invariant: three subjects, two recordings, both recordings produce ScienceEarning actions from the same non-empty list. This is the test that would have failed under the naive "clear in OnRecordingCommitted tail" approach.

### b. §F dedup key collision

**Verified correct.** `GameAction` gains a new `DedupKey` field (not serialized). `ConvertPartPurchased` populates it from `evt.key` (the part name). `GetActionKey` for `FundsSpending` now returns `(a.RecordingId ?? "") + ":" + (a.DedupKey ?? "")`. Commit-path FundsSpending (VesselBuild) has RecordingId set and DedupKey null so the composite stays unique. Test `FundsSpendingDedupKeyTests.TwoDifferentParts_BothLandInLedger` is the specific regression guard, and `GameStateRecorderLedgerTests.OnKscSpending_TwoDifferentParts_BothLandInLedger` backs it up from the OnKscSpending entry point.

The plan shipped §F before §A as required (`0dac08f7` precedes `e6f731fc` in commit order).

### c. §D milestone reward capture (the big pivot)

**Verified: the deviation from the plan is justified and the implementation is sound.**

1. **`GameVariables.GetProgressFunds/Rep/Science` does NOT exist.** I ran `ilspycmd "...Assembly-CSharp.dll" -t GameVariables` and grepped for `progressfunds|progressrep|progressscience` — zero hits. The plan's recommended API does not exist in stock KSP. The implementer's pivot is necessary.
2. **`ProgressNode.AwardProgress` DOES exist.** Decompile confirms `protected void AwardProgress(string description, float funds = 0f, float science = 0f, float reputation = 0f, CelestialBody body = null)`. The Harmony attribute `[HarmonyPatch(typeof(ProgressNode), "AwardProgress", typeof(string), typeof(float), typeof(float), typeof(float), typeof(CelestialBody))]` targets it correctly.
3. **Parameter order match:** decompile has `(string, float funds, float science, float reputation, CelestialBody)`. Postfix reads `(ProgressNode __instance, float funds, float science, float reputation)` — matching name-by-position because Harmony binds by name, and the decompile's parameter names are `funds`/`science`/`reputation` so name-matching works.
4. **Event-enrichment flow:**
   - `OnProgressComplete` emits a `MilestoneAchieved` event via `BuildMilestoneDetail(0, 0f, 0)` (zero rewards), stashes the `ProgressNode -> event` mapping in `PendingMilestoneEventByNode`, and (when at KSC) calls `OnKscSpending(evt)` — which writes a zero-reward `MilestoneAchievement` action to the ledger.
   - KSP subclass then calls `AwardProgress(funds, sci, rep)`.
   - `ProgressRewardPatch.Postfix` fires → `EnrichPendingMilestoneRewards(node, funds, rep, sci)`.
   - `EnrichPendingMilestoneRewards` rewrites the store event's detail via `GameStateStore.UpdateEventDetail` (struct-safe — pulls the struct out, updates, puts it back) AND scans the ledger for the matching `MilestoneAchievement` action (by type + UT + MilestoneId) and mutates `MilestoneFundsAwarded` / `MilestoneRepAwarded` on the found entry. `GameAction` is a class so the mutation is visible.
   - Entry removed from `PendingMilestoneEventByNode`.
5. **Replay suppression guard present:** `ProgressRewardPatch.Postfix` bails early if `GameStateRecorder.IsReplayingActions`, which prevents double-enrichment during `RecalculateAndPatch`. Verbose log confirms the skip.
6. **Sandbox/science mode safety:** `AwardProgress` early-returns before doing reward arithmetic when not in career mode (confirmed in decompile: `if (HighLogic.CurrentGame.Mode != Game.Modes.CAREER)`), so the postfix still fires with zero-valued args — matching pre-fix behavior. The KSP call still passes 0,0,0 into the postfix, so the enrichment writes zeros, which is idempotent with the initial event.

**Open smoke-test concern:** mutating `Ledger.Actions[i]` via direct field write (lines 819-820) does not trigger a recalc. The next `RecalculateAndPatch` (caused by any subsequent action) will pick up the updated values, but if no such action occurs, the ledger's derived state (module running totals, budget) is stale until save/load. Verify in live KSP that a milestone at KSC immediately reflects in the Actions window balance, or add a `RecalculateAndPatch()` call after `EnrichPendingMilestoneRewards` when the action was found.

### d. §I reconciliation diagnostic

**Verified correct.**

- Drop block at `GameStateEventConverter.cs:121-130` is STILL PRESENT, now with an expanded 14-line comment explaining WHY re-emission would double-count and pointing at `ReconcileEarningsWindow`. The regression guard is loud and clear.
- `ReconcileEarningsWindow` runs at the tail of `OnRecordingCommitted` (line 145) — log-only, never emits actions. Uses `fundsTol = 1.0`, `repTol = 0.1`, `sciTol = 0.1` for floating-point tolerance.
- Silent-on-match behavior verified by `EarningsReconciliationTests.Reconcile_PerfectMatch_NoWarn`.
- WARN-on-mismatch verified by `Reconcile_MissingFundsEarning_LogsWarn`.

One note: the function signature takes `vesselCostActions` and `scienceActions` but does not actually use them — `newActions` already contains them. The implementer kept the parameters for clarity. Minor code smell, not a bug.

### e. §K save recovery migration

**Verified correct with acceptable gaps.**

- `TryRecoverBrokenLedgerOnLoad` is called from `ParsekScenario.OnLoad` at line 1603, AFTER `LedgerOrchestrator.OnLoad()` (line 1543) which loads the ledger, AFTER the epoch is restored from the save (line 1577), and only when `!RewindContext.IsRewinding`. The order is correct: migration needs both the loaded ledger AND the restored epoch.
- Epoch isolation: `if (evt.epoch != currentEpoch) { skippedByEpoch++; continue; }` — exactly as planned. Test `Recovery_EpochIsolation_SkipsOldEpochEvents` verifies.
- Idempotency: `LedgerHasMatchingAction(evt)` guards the synthesis. Test `Recovery_Idempotent_SecondCallIsNoOp` verifies a second call is a no-op.
- Science subject migration uses `LedgerHasMatchingScienceEarning(subjectId, sci)` with `>=` comparison so multi-experiment top-ups don't synthesize duplicates.
- Defensive try/catch in `ParsekScenario.OnLoad` wraps the migration call and logs WARN on throw — a migration bug cannot block OnLoad.
- Auto-triggered `RecalculateAndPatch()` runs after recovery so derived state heals.

**Gap 1: MilestoneAchievement events are not migrated.** `IsRecoverableEventType` excludes `MilestoneAchieved`. The commit message notes "historical milestones had funds=0/rep=0 hardcoded" — correct rationale: those events in c1/sci1 have zero rewards in their detail string anyway, so synthesizing them would add zero-reward actions that serve no purpose.

**Gap 2 (not acknowledged): `CrewHired`, `TechResearched`, `FacilityUpgraded` are also not migrated,** despite having mappings in `MapEventTypeToActionType`. For c1/sci1 specifically this is fine — those saves have the `CrewHired` actions (the todo says so explicitly: "ledger.pgld has only 14 actions: ... 7 KerbalHire, 2 ScienceSpending"). But any future broken save with missing hires or tech-research will stay broken silently. Recommend: explicitly document in the todo entry that the migration intentionally covers only five event types (ContractAccepted, ContractCompleted, ContractFailed, ContractCancelled, PartPurchased) plus science subjects, and any other missing action types will require a follow-up migration.

**Gap 3: synthesized `ScienceEarning` uses `UT = 0.0`.** The in-code comment explains: "first pseudo-UT (close to zero) so the synthesized action slots in at the beginning of the current epoch." Acceptable for recovery, but surfaces as a weird low-UT action in the Actions window. Flag but don't block.

### f. §B `PatchContracts` filtered remove

**Verified correct.**

- `currentContracts.Clear()` is gone.
- `ContractSystem.Instance.ContractsFinished.Clear()` is gone.
- The `IsFinished` branch at old `cs:723-726` is deleted — replaced by a WARN log that says "snapshot loaded in state={X} (expected Active) — skipping, not mutating Finished bucket."
- Filtering uses `ContractGuid` (Guid), not `ContractID` (long). Verified in diff: `activeIdSet` is a `HashSet<Guid>`, and the code iterates `currentContracts` checking `c.ContractGuid` and `c.ContractState == Contract.State.Active`.
- `PartitionContractsForPatch` is a pure `internal static` helper taking `IReadOnlyList<ContractFilterEntry>` + `HashSet<Guid>` → `List<Guid> toRemove`, `HashSet<Guid> surviving`. Covered by 7 test cases in `PatchContractsPreservationTests`.

Active contracts already present in the ledger skip the unregister/recreate cycle via `skippedExisting++` — which is exactly the intent (preserving hot parameter subscriptions as the plan flagged as a §5.2 risk).

**`Contract.Register()` re-subscription side effects risk from plan §7:** The implementation *avoids* re-registering any contract that survives in-place. That's the safe answer and it is what landed. No test walks 10 recalc cycles as the plan suggested, but the code path no longer invokes `Register()` for unchanged contracts, so the cycle the test would catch doesn't exist anymore.

---

## 5. Plan-level findings

### CHANGELOG.md

**Compliant with the HARD RULE.** The new entry is exactly one line:

> `#394`, `#395`, `#396`, `#397`, `#398`, `#400`, `#401`, `#402`, `#403`, `#404`, `#405` Fixed a cascade of career-mode ledger bugs that drained funds to zero, lost accepted contracts on scene transition, pinned science at the starting seed, and zeroed out milestone funds/rep rewards. Broken sci1/c1 saves are repaired automatically on first load.

Two sentences, under 400 chars, all 11 bug numbers, user-facing.

### docs/dev/todo-and-known-bugs.md

All 11 headings struck through (`## ~~405. ...~~` through `## ~~394. ...~~`). Each Fix section rewritten to describe what actually landed, with file paths, test file names, and mentions of the relevant review section. Every entry ends with `**Status:** ~~Fixed~~`.

#399 stays open with `**Status:** TODO. Suspect, unverified. **Unblocked by the career-earnings-bundle PR (#394-#405) — the ledger is now accurate enough that this same-UT walk bug should be reproducible and fixable in isolation.**` Correct per the plan.

### Commit message policy

No `Co-Authored-By` trailers in any of the 12 commits (verified via `git log --format="%(trailers)"`). Every commit message follows the project convention (type + bug number + subject, with body).

### Test conventions

- All new test classes touching shared state have `[Collection("Sequential")]`: `FundsSpendingDedupKeyTests`, `GameStateRecorderLedgerTests`, `OnContractOfferedStoreTests`, `PendingScienceSubjectsClearTests`, `MilestoneRewardCaptureTests`, `EarningsReconciliationTests`, `LedgerRecoveryMigrationTests`. (`PatchContractsPreservationTests` is pure static helper and doesn't touch shared state — no collection needed.)
- All log-capture tests use `ParsekLog.TestSinkForTesting = line => logLines.Add(line)` in the constructor and `ParsekLog.ResetTestOverrides()` in `Dispose()` — matching the canonical pattern from `RewindLoggingTests.cs`.
- No tests are `[Fact(Skip=)]` in the new files. The single pre-existing skip in `GhostPlaybackEngineTests` is unrelated.

### Logging

Every new code path has explicit logs:

- `OnKscSpending` re-uses the existing `KSC spending recorded: type=...` log line.
- `PatchContracts` new summary log includes removedStale/restored/skippedExisting/noSnapshot/noType/typeNotFound/loadFailed/ledgerActive/kspTotal.
- `PendingScienceSubjects` clear sites each log `cleared PendingScienceSubjects (before=X, atClear=Y)` — noise-suppressed when both are zero.
- `EnrichPendingMilestoneRewards` logs Info on enrichment, Verbose on unknown node, and Verbose in the suppression guard.
- `ReconcileEarningsWindow` logs WARN on funds/rep/sci mismatch with window bounds.
- `TryRecoverBrokenLedgerOnLoad` logs a per-event Verbose and a WARN summary with synthesized counts; Verbose when no recovery needed.
- `IsSuspiciousDrawdown` drives a WARN in both `PatchFunds` and `PatchScience`.

No dead log lines observed.

### Out-of-scope files

None. Every file touched maps to a plan section. Docs-only commit `eea92774` touches only CHANGELOG.md and todo-and-known-bugs.md. The first commit `0dac08f7` also stages the plan + review docs which were previously untracked — acknowledged in the commit message.

---

## 6. Deviations: justified vs unjustified

| # | Deviation | Justified? | Notes |
|---|---|---|---|
| 1 | §D: `GameVariables.GetProgressFunds/Rep/Science` → Harmony postfix on `ProgressNode.AwardProgress` | **YES** | Verified via `ilspycmd` that `GameVariables.GetProgress*` does NOT exist in stock KSP. The plan was wrong; the implementer followed the review's fallback guidance correctly. |
| 2 | §D: `QualifyMilestoneId` simplified to return string only (not `(id, body)` tuple) | **YES** | The body is provided directly to the Harmony postfix as a method parameter (`CelestialBody body` on `AwardProgress`), so `QualifyMilestoneId` doesn't need to extract it via reflection. Cleaner than the plan. |
| 3 | §J: plan said `ScienceModule.GetSubjectCreditedTotal`; shipped as `ScienceModule.GetSubjectCredited` | **YES** | Method name difference only, same semantics (returns `SubjectState.CreditedTotal`). Trivial. |
| 4 | §C: discovered a third upstream clear site in `ParsekFlight.FallbackCommitSplitRecorder` not listed in the plan's inventory | **YES** | Plan only listed two (`NotifyLedgerTreeCommitted` and `CommitSegmentCore`); the implementer correctly added the third. This was the explicit "implementer may find a 3rd site" scenario the reviewer flagged. |
| 5 | §K: `IsRecoverableEventType` excludes `MilestoneAchieved` | **YES (documented)** | Commit message explains: historical milestones had funds=0/rep=0 hardcoded, so synthesis would add zero-reward actions that serve no purpose. |
| 6 | §K: `IsRecoverableEventType` excludes `CrewHired`, `TechResearched`, `FacilityUpgraded` despite having mappings | **Not justified explicitly.** | Acceptable for c1/sci1 but a silent gap for other broken saves. Recommend adding an explicit comment to `IsRecoverableEventType` explaining the scope limit. |
| 7 | §I: `ReconcileEarningsWindow` takes `vesselCostActions` and `scienceActions` parameters but doesn't use them | **Minor** | Signature noise. Not a bug, but could be trimmed for clarity. |

---

## 7. Risks and follow-ups

### To file as new todo entries (or deferred items)

1. **Migration coverage gap.** Broken saves with missing `CrewHired`, `TechResearched`, or `FacilityUpgraded` ledger actions will stay silently broken. Extend `IsRecoverableEventType` if a future playtest surfaces such a save. Alternatively, accept the scope limit and document it in todo-and-known-bugs.md under the migration section.
2. **Milestone enrichment recalc timing.** `EnrichPendingMilestoneRewards` mutates `Ledger.Actions[i]` directly without triggering `RecalculateAndPatch()`. Until the next ledger-altering action, the derived module state is stale. Smoke-test in live KSP: earn a milestone at KSC, check that the Actions window balance updates immediately. If not, add a recalc call at the end of `EnrichPendingMilestoneRewards`.
3. **§C throw-path invariant is code-only.** Replace the soft test with a fault-injection hook (e.g., `internal static Action<string, double, double>? OnRecordingCommittedForTesting` that the test sets to throw) so the `try/finally` is actually exercised.
4. **Sequence counter resetting across scenes.** `OnKscSpending` uses a static `kscSequenceCounter++`. Not cleared on epoch changes or save loads — if a save has 10 KSC spendings in the old session, the new session's first KSC spending gets sequence=11. Unrelated to this bundle but worth checking.

### Smoke tests recommended before merge

1. **Load c1 save with the bundle applied.** Verify: `TryRecoverBrokenLedgerOnLoad` logs a WARN summary with non-zero funds/contract counts, then `RecalculateAndPatch` runs, then `PatchFunds` does NOT log a `suspicious drawdown` WARN, and in-game Funds reads non-zero.
2. **Load sci1 save with the bundle applied.** Verify: migration synthesizes the 9 science subjects, R&D pool reflects the recovered 16.44 science, and the Science Archive display is consistent with the R&D pool.
3. **Fresh career mode playtest.** Accept a contract at KSC, complete a flight, verify the contract reward lands in the ledger on commit. Build a vessel (part purchases at VAB), verify all `PartPurchased` events dedupe correctly and land as distinct actions.
4. **Milestone at KSC.** E.g., achieve `FirstLaunch` on launchpad. Verify the Actions window shows non-zero `MilestoneFundsAwarded` on the action immediately (this is the Gap #2 smoke test).
5. **Complete a recording tree with two recordings that both earn science.** Verify both recordings produce `ScienceEarning` actions (the multi-recording §C invariant).

### Follow-up PRs

- #399 (`ScienceModule.ComputeTotalSpendings` same-UT walk) — now unblocked.
- Migration coverage extension (see risk 1).
- Milestone enrichment recalc timing (see risk 2) — pending live smoke test result.

---

## Summary

Strong implementation of a complex 11-bug bundle. Plan adherence is high, test coverage is strong across all deep-dive areas, the two deviations (Harmony pivot in §D and three-site scope for §C clears) are both well-justified with decompile evidence. CHANGELOG and todo are clean. Build and tests are green. Approve with minor revisions tracked as follow-up todos; none of the revisions are merge blockers in my assessment.

---

## Revision follow-up

Commit `7d77606a` — `fix(career-earnings): address review revisions (fault injector, scope doc, param cleanup)`

Addresses three of the five items in §1's must-fix punch list:

1. **§1.1 / §C throw-path test is a no-op.** Added `internal static Action<string> LedgerOrchestrator.OnRecordingCommittedFaultInjector` invoked at the entry of `OnRecordingCommitted`. Rewrote `PendingScienceSubjectsClearTests.NotifyLedgerTreeCommitted_OrchestratorThrows_StillClears` to force a sentinel `SentinelFaultException` through the hook, assert it bubbles via `Assert.Throws<SentinelFaultException>`, then assert `PendingScienceSubjects` is empty and the clear log line fired. Hook reset in `Dispose` and wrapped in `try/finally` in the test body. The `try/finally` invariant inside `NotifyLedgerTreeCommitted` is now exercised against real code rather than asserted by inspection. Files: `Source/Parsek/GameActions/LedgerOrchestrator.cs`, `Source/Parsek.Tests/PendingScienceSubjectsClearTests.cs`.
2. **§6.6 / §K `IsRecoverableEventType` scope undocumented.** Added a 9-line comment above the method explaining that only contract state changes and `PartPurchased` are migrated (the event types #405/#404 actually stripped from c1/sci1), while `MilestoneAchieved` (historical zero-reward) and `CrewHired`/`TechResearched`/`FacilityUpgraded` (real-time `OnKscSpending` paths were never broken, so c1/sci1 already have those actions) are deliberately excluded. Added a matching "Follow-up / known limitation" line under bug #401 in `docs/dev/todo-and-known-bugs.md` noting when to extend the list. Files: `Source/Parsek/GameActions/LedgerOrchestrator.cs`, `docs/dev/todo-and-known-bugs.md`.
3. **§6.7 / §I `ReconcileEarningsWindow` unused parameters.** Verified on inspection that `vesselCostActions` and `scienceActions` were dead — only `newActions` (which already contains them after `OnRecordingCommitted`'s `actions.AddRange` calls) was enumerated. Dropped both parameters from the method signature, updated the xmldoc with a `<para>` block explaining the merging, and updated all 8 call sites in `Source/Parsek.Tests/EarningsReconciliationTests.cs`. The call site in `OnRecordingCommitted` at the tail of the commit pipeline now passes `(events, actions, startUT, endUT)`. Files: `Source/Parsek/GameActions/LedgerOrchestrator.cs`, `Source/Parsek.Tests/EarningsReconciliationTests.cs`.

Items §1.2 (coverage gap for Crew/Tech/Facility — documented in the todo but not implemented, by choice), §1.3 (synthesized `ScienceEarning` UT=0 — comment in source already explains), §1.4 (milestone enrichment recalc timing — still needs live smoke test), and §1.5 (`ProgressRewardPatch.Postfix` swallowed-catch note — no action, review already conceded this passes the rule) were not touched in this pass.

Test results post-revision: `dotnet test` reports `Passed: 6272, Failed: 0, Skipped: 1` (identical to the pre-revision baseline; the skip is the unrelated pre-existing `GhostPlaybackEngineTests.SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT`).
