# Add In-Game Test for StrategyLifecyclePatch (#439 Phase A follow-up)

Status: plan v1. Branch: `test/strategy-lifecycle-in-game`. Scope: tests only, no production changes.

## 1. Context

#439 Phase A (PR #378, shipped in v0.8.2) added `Source/Parsek/Patches/StrategyLifecyclePatch.cs` -- Harmony postfixes on `Strategies.Strategy.Activate` / `Deactivate` that call `GameStateRecorder.OnStrategyActivated` / `OnStrategyDeactivated`, which emit `StrategyActivated` / `StrategyDeactivated` events into `GameStateStore`. xUnit coverage for `StrategyLifecyclePatch.cs` was deferred because `Strategies.Strategy` requires a live `StrategyConfig` plus `PartLoader` and cannot be constructed from a unit test. The existing `#439` todo entry in `docs/dev/todo-and-known-bugs.md` (struck through) notes the in-game Harmony-patch coverage as a deferred follow-up. This plan closes that gap by adding one (optionally two) `[InGameTest]` in the new `StrategyLifecycle` category.

## 2. Stock strategies available at Admin tier 1

Stock strategies (from `GameData/Squad/Strategies/*.cfg`) activatable at Admin tier 1 with zero setup costs:

- `LeadershipInitiative` -- Funds -> Reputation converter.
- `FundraisingCampaign` -- Reputation -> Funds converter.
- `UnpaidResearchProgram` -- Funds -> Science converter.
- `PatentsLicensing` -- Science -> Funds.
- `OpenSourceTechProgram`, `AppreciationCampaign`, `RecoveryTransponderFitting`, `FundingInitiative` -- various zero / minimal cost.

**Chosen target:** **discover at runtime**. Rather than hard-coding a config name, iterate `StrategySystem.Instance.Strategies`, pick the first entry satisfying:

- `!s.IsActive` (not already running)
- `s.Config != null` and `s.CanBeActivated(out _)` returns true (stock gate)
- `s.InitialCostFunds == 0 && s.InitialCostScience == 0 && s.InitialCostReputation == 0`
- `s.MinFactor <= s.Factor <= s.MaxFactor` (usually enforced by CanBeActivated, but check explicitly for resilience against stock config drift)

Skip test with `InGameAssert.Skip("no stock strategy with zero setup cost available at current Admin tier")` if no candidate is found. This is robust across career tiers, Bureaucracy / Strategia-style mod overrides, and future stock rebalances.

## 3. Test design

### Happy path -- `ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents`

Attribute: `[InGameTest(Category = "StrategyLifecycle", Scene = GameScenes.SPACECENTER, Description = "#439 Phase A: StrategyLifecyclePatch postfixes emit StrategyActivated/StrategyDeactivated events into GameStateStore for a real Strategies.Strategy instance.")]`

Return type: `IEnumerator` (one yield between Activate and assertion to absorb a possible deferred-tick if KSP's Strategy.Activate does any post-call bookkeeping in OnUpdate).

Body, sequenced:

1. **Game-mode gate.** If `HighLogic.CurrentGame?.Mode != Game.Modes.CAREER`, `InGameAssert.Skip("StrategySystem is career-only")`.
2. **Singleton gate.** `var system = Strategies.StrategySystem.Instance; if (system == null) InGameAssert.Skip("StrategySystem.Instance is null -- Admin facility may be missing or scene not ready")`.
3. **Candidate discovery.** Iterate `system.Strategies`, pick first zero-cost-inactive-activatable entry per section 2. If none, `InGameAssert.Skip("no zero-setup-cost stock strategy available for testing")`.
4. **Pre-test invariants log.** `ParsekLog.Info("TestRunner", $"StrategyLifecycle test target: configName={s.Config.Name} title={s.Title} setupF={s.InitialCostFunds} setupS={s.InitialCostScience} setupR={s.InitialCostReputation}")`.
5. **Resource snapshot.** Capture `Funding.Instance.Funds`, `ResearchAndDevelopment.Instance.Science`, `Reputation.Instance.reputation` so teardown can verify no drift (defensive -- zero-cost strategy should not touch them).
6. **Event snapshot.** `int eventCountBefore = GameStateStore.EventCount;`. Cheaper than copying the list; the assertion iterates the tail slice `[eventCountBefore..]`.
7. **Install log sink.** `var captured = new List<string>(); var priorSink = ParsekLog.TestSinkForTesting; ParsekLog.TestSinkForTesting = l => { captured.Add(l); priorSink?.Invoke(l); };` (mirror the existing runtime-test pattern).
8. **Act -- Activate.** `bool ok = s.Activate(); yield return null;` (yield one frame for safety even though the postfix is synchronous -- KSP may fire `GameEvents.onStrategyXxx`-adjacent UI-refresh deferrals in `LateUpdate`).
9. **Assert activation.** `InGameAssert.IsTrue(ok, "Strategy.Activate returned false");` `InGameAssert.IsTrue(s.IsActive, "Strategy.IsActive false after Activate returned true");`
10. **Assert event capture.** Walk `GameStateStore.Events` from `eventCountBefore` to `Count-1`, find the first with `eventType == GameStateEventType.StrategyActivated && key == s.Config.Name`. Assert found, assert `ut > 0`, assert `!string.IsNullOrEmpty(detail)`, assert `detail.Contains("title=")`, `detail.Contains("factor=")`, `detail.Contains("setupFunds=")`, `detail.Contains("source=")`, `detail.Contains("target=")`. Match the literal detail shape from `GameStateRecorder.BuildStrategyActivateDetail`. Do NOT over-specify the numeric values -- they are strategy-config-dependent.
11. **Log-line assertion.** `InGameAssert.IsTrue(captured.Any(l => l.Contains("[GameStateRecorder]") && l.Contains("StrategyActivated") && l.Contains(s.Config.Name)), "Expected [GameStateRecorder] StrategyActivated info log")`.
12. **Act -- Deactivate.** `int deactSnapshot = GameStateStore.EventCount; bool off = s.Deactivate(); yield return null;`
13. **Assert deactivation.** `InGameAssert.IsTrue(off)`, `InGameAssert.IsFalse(s.IsActive)`, walk `[deactSnapshot..]` slice for `StrategyDeactivated` with matching `key`, assert `detail.Contains("activeDurationSec=")`.
14. **Log-line assertion (deactivate).** Symmetric.
15. **Teardown invariants.** Assert funds/sci/rep drift is zero (or within `0.01` tolerance for float rounding). If the test is force-using a non-zero-cost strategy (section 12 fallback), restore via `Funding.Instance.AddFunds(+snapshottedDiff, TransactionReasons.None)` etc. **Preferred path is zero-cost, which makes this a trivial drift check.**
16. **Final state assert.** `InGameAssert.IsFalse(s.IsActive, "teardown: strategy still active")`.
17. **Log sink restore.** `ParsekLog.TestSinkForTesting = priorSink;` in a `finally`.

Use a `try/finally` wrapping steps 7 through 17 so a failed assertion still:
- restores the log sink,
- calls `s.Deactivate()` if `s.IsActive`,
- restores funds/sci/rep if modified.

### Optional failure-filter pin -- `FailedActivation_DoesNotEmitEvent`

Attribute: `[InGameTest(Category = "StrategyLifecycle", Scene = GameScenes.SPACECENTER, Description = "#439 Phase A: Activate()=false path (already-active) does NOT emit StrategyActivated (pins the __result==true filter in StrategyLifecyclePatch).")]`

Return type: `void` (single-frame).

Body:

1. Game-mode / singleton / candidate gates identical to happy path.
2. Activate the strategy first (no assertion on events).
3. Snapshot `eventCountBefore = GameStateStore.EventCount`.
4. Call `s.Activate()` again -- KSP's `Activate()` short-circuits when already active; returns false.
5. Assert `ok == false`.
6. Walk `GameStateStore.Events[eventCountBefore..]` for any `StrategyActivated` with `key == s.Config.Name`. Must be **zero**. If found, `InGameAssert.Fail("StrategyLifecyclePatch fired on a failed Activate() -- __result filter broken")`.
7. Teardown: `s.Deactivate()`.

This test alone validates the `if (!__result) return;` branch in `StrategyLifecyclePatch.cs` that cannot be reached from the happy path. It is optional (happy path is sufficient coverage for a test-only PR), but cheap and high-value given how easy the filter is to break.

## 4. `IsReplayingActions` guard

`GameStateRecorder.IsReplayingActions` defaults to `false` and is set to `true` only inside `KspStatePatcher` during the ledger recalculation walk. An in-game test running from the Ctrl+Shift+T test runner is not inside a walk -- the guard is off. **No action needed.** A comment in the test body notes this invariant so a future change to the test runner that starts a walk mid-test is caught.

## 5. `__result == false` branch

Covered by the optional `FailedActivation_DoesNotEmitEvent` test in section 3. Activating an already-active strategy is the cheapest path to `__result == false` without reflection or synthetic strategies.

Alternative (not used): call `Deactivate()` on an already-inactive strategy. Also returns false. Chose already-active Activate() because it leaves the runtime in a well-defined state that the happy-path teardown already handles.

## 6. Failure / skip handling

Match the existing runtime-test idiom. The `InGameAssert.Skip(reason)` primitive throws `InGameTestSkippedException`, which the test runner records as `TestStatus.Skipped` with the reason preserved. Skip conditions:

- Not career mode
- `StrategySystem.Instance == null`
- No zero-cost activatable strategy
- `HighLogic.CurrentGame == null` (defensive)

**Never `InGameAssert.Fail` on environmental preconditions.** The test exists to validate the patch behavior; absent a strategy-capable career the test is genuinely not runnable and Skipped is the correct status.

## 7. Teardown invariance

Guaranteed by the `try/finally` pattern in section 3. Post-test invariants:

- `s.IsActive == false` (strategy deactivated)
- Funds / Science / Reputation unchanged (within 0.01 tolerance)
- `ParsekLog.TestSinkForTesting` restored to pre-test value
- No events purged from `GameStateStore` -- the test only appends test-path events, which is consistent with how a real in-game activate behaves (capture is desired, persistent in the save, and will round-trip through `ParsekScenario.OnSave/OnLoad` -- no special purge needed).

**Open question (documented risk, section 12):** the `StrategyActivated` and `StrategyDeactivated` events emitted by the test *do* persist in the save, and if the save is saved mid-test they will round-trip. This is **identical to the user activating/deactivating a strategy manually** and is the intended behavior; the test does not need to remove them.

## 8. State management across categories

Other tests in batch execution may have already touched `GameStateStore.Events`. The test uses `eventCountBefore = GameStateStore.EventCount` + tail-slice iteration so stale events do not contaminate the assertion. Never assert against `GameStateStore.Events.Count == 1` or absolute indices.

`InGameTestRunner.RunBatch` orders tests by `(RunLast, Category, Name)`, so `StrategyLifecycle` (new category, alphabetically placed) runs in a deterministic slot. Between-test cleanup (`PerformBetweenRunCleanup`) only scrubs ghost state -- it does not touch `GameStateStore`, so tail-slice iteration is necessary and correct.

## 9. Log-capture pattern

`ParsekLog.TestSinkForTesting` is the canonical hook. It is a `static Action<string>` that receives every `[Parsek]` log line. The test installs its own sink, chains to the prior sink, captures into a `List<string>`, and restores the prior sink in `finally`.

Log assertions verify the `[GameStateRecorder] Game state: StrategyActivated 'X' ...` INFO line and the symmetric StrategyDeactivated line.

## 10. File-touch list (tests only)

- `Source/Parsek/InGameTests/RuntimeTests.cs` -- add one new `#region StrategyLifecycle` with 1-2 `[InGameTest]` methods inside the existing `RuntimeTests` class. Insert near the `#region ResourceReconciliation` block so related SPACECENTER-scoped assertions cluster.
- `CHANGELOG.md` -- under 0.8.2 "Tests" (or equivalent non-user-facing section), add 1 line: `Added runtime in-game test for strategy lifecycle Harmony patch capture (#439 Phase A follow-up).`
- `docs/dev/todo-and-known-bugs.md` -- under the `#439` entry, append a terminal "**Follow-up delivered:** in-game `[InGameTest]` for the strategy Harmony patch added in `test/strategy-lifecycle-in-game`." If the "in-game Harmony-patch coverage follow-up" bullet is a separate struck-through item elsewhere in the #439 entry, strike it.
- `.claude/CLAUDE.md` -- update the `InGameTests/` file-list line to bump the category count (21 -> 22) and test count by +1 or +2 depending on which variant ships.

No production code changes. No changes to `Source/Parsek/Patches/StrategyLifecyclePatch.cs`, `Source/Parsek/GameStateRecorder.cs`, or any ledger/module files.

## 11. Test name conventions

Existing in-game test names use `{ClassName}.{MethodName}`. Method names chosen:

- `ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents` -- happy path.
- `FailedActivation_DoesNotEmitEvent` -- optional filter pin.

Both live in the `RuntimeTests` class. Category is `StrategyLifecycle` in both.

## 12. Risks and open questions

- **R1 -- zero-cost strategy availability.** If no stock strategy at the current Admin tier has all-zero setup costs, the test skips. This is the right behavior (tests that cannot run cleanly should skip), but it means a minimal-progression save could skip the test. **Mitigation:** the section 2 candidate discovery accepts any zero-cost strategy; snapshot/restore Funds/Sci/Rep in teardown as a fallback for a non-zero-cost strategy if the skip rate is empirically high. Prefer skip over forced mutation.
- **R2 -- side effects on save state.** Activate/Deactivate persists to `saves/<save>/persistent.sfs`. Re-entering the test save after the test leaves one extra activate/deactivate pair in `StrategySystem`. Because the test deactivates in teardown, `IsActive` is false on disk, which is equivalent to never-activated. The `dateActivated` / `dateDeactivated` bookkeeping on the strategy is mutated, but that is user-visible only as a tiny log crumb -- acceptable.
- **R3 -- Sandbox / Science mode.** `StrategySystem.Instance` is null outside Career. The game-mode gate (step 1) handles this with `Skip`.
- **R4 -- shared test save vs. dedicated save.** The `InjectAllRecordings` workflow injects synthetic recordings into an arbitrary user save. The strategy test runs against whatever save is loaded. It does NOT require `InjectAllRecordings`-injected state, so it works on the default career test save. No new test save is required. Document in the test docstring: "Runs in any career-mode save with at least one zero-setup-cost strategy available."
- **R5 -- deferred tick timing.** `Strategies.Strategy.Activate` is synchronous (per the #439 plan section 3.1) and the Harmony postfix runs inside the same call stack. `GameStateStore.AddEvent` is also synchronous. `yield return null` after Activate is defensive, not strictly required. Keep the yield for safety against a future KSP patch that defers effect registration.
- **R6 -- `GameStateStore` assertion hook.** Tests assert against `GameStateStore.Events` (public `IReadOnlyList`). This hook exists already. No production seam change needed.
- **R7 -- stock-strategy config drift.** If a future KSP patch changes a stock strategy's setup costs to non-zero, the section 2 candidate discovery may return empty. Skip behavior absorbs this cleanly.

## 13. Acceptance checklist

- [ ] `RuntimeTests.ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents` (and optionally `RuntimeTests.FailedActivation_DoesNotEmitEvent`) present in the `StrategyLifecycle` category with `Scene = GameScenes.SPACECENTER`.
- [ ] Test is eligible under `RunAll` / `RunCategory` in SPACECENTER via Ctrl+Shift+T.
- [ ] Test captures both `StrategyActivated` and `StrategyDeactivated` events and asserts on `key`, `ut`, and key detail-string substrings.
- [ ] Log-capture asserts the `[GameStateRecorder] Game state: StrategyActivated` and `... StrategyDeactivated` INFO lines.
- [ ] Teardown guarantees `s.IsActive == false` and no Funds/Sci/Rep drift.
- [ ] Skip paths cover non-career, null `StrategySystem.Instance`, no eligible candidate.
- [ ] CHANGELOG entry (one line): `Added runtime in-game test for strategy lifecycle Harmony patch capture (#439 Phase A follow-up).`
- [ ] `docs/dev/todo-and-known-bugs.md`: append a "Follow-up delivered" line to the `#439` entry (or strike the relevant sub-bullet).
- [ ] `.claude/CLAUDE.md`: update `InGameTests/` line category count 21 -> 22 and test count (74 -> 75 or 76).
- [ ] No production code changes -- `git diff Source/Parsek/ ':!Source/Parsek/InGameTests/'` is empty.
