# TODO & Known Bugs

Older entries archived alongside this file:

- `done/todo-and-known-bugs-v1.md` — 225 bugs, 51 TODOs (mostly resolved, pre-#272).
- `done/todo-and-known-bugs-v2.md` — entries #272-#303 (78 bugs, 6 TODOs).
- `done/todo-and-known-bugs-v3.md` — everything through the v0.8.2 bugfix cascade up to #461. Archived 2026-04-18. Closed during archival: PR #307 career-earnings-bundle post-review follow-ups (all four fixes confirmed in code — `PickScienceOwnerRecordingId`, `DedupKey` serialization, `FundsAdvance` in ContractAccepted, `MilestoneScienceAwarded` field), #337 (same-tree EVA LOD culling — fix shipped in PR #260, stale), #368 / #367 / #364 (PR #240 / #242 / #229 follow-ups — done).

When referencing prior item numbers from source comments or plans, consult the relevant archive file.

---

## Priority queue — deterministic-timeline correctness

The four top-of-queue correctness fixes (#431, #432, #433, #434) shipped in the v0.8.2 cycle. Remaining follow-up: retire `MilestoneStore.CurrentEpoch` as the legacy work-around (now redundant with purge-on-discard + ghost-only event filtering). See #431's notes in `done/todo-and-known-bugs-v3.md`.

---

# Known Bugs

## ~~487. Test Runner transparent background on scene change / Settings-hosted reopen path~~

**Source:** follow-up on the transparent `TestRunner` window after scene transitions. The original fix hardened the global Ctrl+Shift+T shortcut path, but the shared `ParsekUI` cache used by the Settings-hosted Test Runner and other Parsek windows could still cache a transparent or unreadable window style after scene changes / skin-lag frames.

**Fix:** opaque-window rebuilds are now gated on a ready normal background, lagging hover/focus/active states fall back to the ready normal texture instead of freezing a transparent cache, shared `ParsekUI` windows invalidate stale opaque styles across scene changes, and focused/active title-bar text colors are normalized so the opaque title bar stays readable after focus changes. The in-game regressions cover the missing-skin / lagging-state path, and the xUnit coverage now checks the readable title-text states too.

**Files:** `Source/Parsek/InGameTests/TestRunnerShortcut.cs`, `Source/Parsek/InGameTests/RuntimeTests.cs`, `Source/Parsek/ParsekUI.cs`, `Source/Parsek/UI/TestRunnerUI.cs`, `Source/Parsek.Tests/ParsekUITests.cs`.

**Resolution:** shortcut path fixed on 2026-04-19 in `bug/487-test-runner-transparent`; the shared `ParsekUI` follow-up landed on 2026-04-20 so the Settings-hosted Test Runner and the rest of the Parsek subwindows now use the same guarded opaque-style rebuild instead of bypassing the original fix. A later live repro confirmed the transparent background was gone, and the final follow-up normalized title-bar text colors when window focus changed.

**Status:** CLOSED. Fixed for v0.8.3.

---

## ~~489. Manual-only runtime coverage for deferred merge commit and `Keep Vessel` playback existed locally; both now have live KSP validation~~

**Source:** local audit work on `audit-test-coverage-2026-04-19` after `#488` closed. New tests now exist in `Source/Parsek/InGameTests/RuntimeTests.cs`:

- `RuntimeTests.TreeMergeDialog_DeferredMergeButton_CommitsPendingTree`
- `FlightIntegrationTests.KeepVessel_FastForwardIntoPlayback_SpawnsExactlyOnce`

**What landed already:** the first test drives `ParsekScenario.ShowDeferredMergeDialog()` in `FLIGHT`, presses the real `Merge to Timeline` button, and asserts the synthetic pending tree moves into `RecordingStore.CommittedTrees` / `CommittedRecordings`. That path has now passed in a live KSP run. The second test commits a synthetic one-recording tree, calls `ParsekFlight.FastForwardToRecording(...)`, waits for a live ghost, and asserts the end-of-recording vessel spawn happens exactly once before cleanup/recovery.

**Resolution:** the deferred-merge canary passed live earlier, and the later direct `Kerbal Space Program/KSP.log` + `parsek-test-results.txt` rerun at `2026-04-20 00:32` closed the `Keep Vessel` side too. The first attempt hit the expected idle-flight guard and logged `SKIPPED`, but the actual row-play rerun passed once the session was idle. `KSP.log` shows the patched synthetic endpoint outside the KSC exclusion zone (`padDist≈240m`), a landed deferred spawn (`Vessel spawn for #2 ... sit=LANDED`), the runtime assertion log `Keep-vessel runtime: ... spawnedPid=...`, and the final `PASSED: FlightIntegrationTests.KeepVessel_FastForwardIntoPlayback_SpawnsExactlyOnce (6132.6ms)` line. That means both manual-only audit canaries are now live-validated.

**Remaining gap after closure:**

The next runtime scenario worth adding is no longer these synthetic canaries. It is the stock `record -> revert -> soft-unstash / no merge` flow, followed by the real non-revert scene-exit deferred merge path.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`, `docs/dev/test-coverage-audit-2026-04-19.md`, `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md`.

**Status:** CLOSED. Fixed for the audit branch; the next open player-flow gaps are the stock revert soft-unstash transition and the non-revert deferred merge path.

---

## ~~490. Manual-only stock `Revert to Launch` runtime coverage existed locally; it now has live KSP validation~~

**Source:** follow-up audit work after closing `#489`, aligned with shipped #434 behavior and the current user guide text.

**What landed already:** `FlightIntegrationTests.RevertToLaunch_SoftUnstashesPendingTree_WithoutMergeDialog` now exists in `Source/Parsek/InGameTests/RuntimeTests.cs`. It starts a real recording on a prelaunch vessel, stages the active vessel, drives stock `FlightDriver.RevertToLaunch`, waits for the fresh FLIGHT scene, and asserts that:

- the reverted mission did not commit into `RecordingStore.CommittedRecordings` / `CommittedTrees`
- no Parsek `ParsekMerge` popup appears after the revert
- the pending tree was soft-unstashed rather than committed or hard-discarded
- the log stream contains the expected fresh-pending keep + soft-unstash lines from the #434 path

**Why this matters:** the audit roadmap used to describe the next gap as `record -> revert -> merge`, but that is no longer the shipped product contract. The current documented behavior is: revert soft-unstashes; if the player wants the merge dialog they take a non-revert exit such as `Space Center`. This runtime canary is the missing end-to-end proof for the actual shipped revert path.

**Resolution:** the direct `Kerbal Space Program/KSP.log` + `parsek-test-results.txt` rerun at `2026-04-20 00:57` now closes this. `parsek-test-results.txt` records `FlightIntegrationTests.RevertToLaunch_SoftUnstashesPendingTree_WithoutMergeDialog` as `FLIGHT PASSED (7318.7ms)`. `KSP.log` contains the full shipped #434 revert sequence in one live pass: `Revert: keeping freshly-stashed pending`, then `Unstashed pending tree 'Kerbal X' on revert ... sidecar files preserved`, then `Revert flow runtime: ... committedBefore=2 committedAfter=2`, and finally the `PASSED:` row. That means the canary now has real evidence for the current product contract: stock revert soft-unstashes, does not open the Parsek merge dialog, and does not commit the reverted mission into the timeline.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`, `Source/Parsek/RecordingStore.cs`, `Source/Parsek/ParsekScenario.cs`, `docs/dev/test-coverage-audit-2026-04-19.md`, `docs/dev/todo-and-known-bugs.md`, `CHANGELOG.md`.

**Status:** CLOSED. Fixed for the audit branch; the next open runtime player-flow gap is the non-revert scene-exit deferred merge path.

---

## 491. No live end-to-end runtime canary yet for the real non-revert scene-exit deferred merge path

**Source:** audit follow-up after `#489` and `#490` both gained live KSP validation.

**Current state:** the branch now has strong coverage for the synthetic FLIGHT deferred-merge popup (`RuntimeTests.TreeMergeDialog_DeferredMergeButton_CommitsPendingTree`) and for stock revert semantics (`FlightIntegrationTests.RevertToLaunch_SoftUnstashesPendingTree_WithoutMergeDialog`). The next step is partially landed: `FlightIntegrationTests.ExitToSpaceCenter_DeferredMergeButton_CommitsPendingTree` and `FlightIntegrationTests.ExitToSpaceCenter_DeferredDiscardButton_ClearsPendingTree` now exist locally, build cleanly, and drive the real `record -> launch -> stock save-and-exit to Space Center -> deferred merge dialog -> merge/discard` flow end-to-end. What is still missing is live KSP evidence for those two manual-only tests.

**Why this matters:** after #434, revert is no longer the merge entry point. The remaining live confidence gap is not “revert then merge”; it is the non-revert exit path that still owns merge UI in production.

**Proposed next step:** from `Parsek-audit-test-coverage`, build `Source/Parsek/Parsek.csproj`, then run both `SceneExitMerge` tests individually from a disposable prelaunch flight:

- `FlightIntegrationTests.ExitToSpaceCenter_DeferredMergeButton_CommitsPendingTree`
- `FlightIntegrationTests.ExitToSpaceCenter_DeferredDiscardButton_ClearsPendingTree`

Collect `KSP.log`, `Player.log`, and `parsek-test-results.txt` afterward and close this item once both branches have clean live evidence.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`, `CHANGELOG.md`, `docs/dev/test-coverage-audit-2026-04-19.md`, `docs/dev/todo-and-known-bugs.md`.

**Status:** OPEN - IMPLEMENTED LOCALLY, LIVE VALIDATION PENDING.

---

## 492. First timing-sensitive part-event runtime canaries are local-only; they still need live KSP evidence

**Source:** audit follow-up after implementing the first `PartEventTiming` tests in the audit worktree.

**Current state:** the branch now contains `RuntimeTests.PartEventTiming_LightToggle_AppliesAtEventUt` and `RuntimeTests.PartEventTiming_DeployableTransition_AppliesAtEventUt`. These are deterministic `FLIGHT` runtime tests that build synthetic ghost light / deployable states and assert `GhostPlaybackLogic.ApplyPartEvents(...)` flips them exactly at the authored UT boundaries. They build cleanly, but they have not been run in a live KSP session yet.

**Why this matters:** the audit's remaining part-event gap was no longer "can ghost FX build at all?" Existing `PartEventFX` checks already cover that. The narrower missing confidence was timing: do visible state changes happen at the right moment? These two tests are the first concrete attempt to pin that down.

**Proposed next step:** from a normal `FLIGHT` session in the audit build, run:

- `RuntimeTests.PartEventTiming_LightToggle_AppliesAtEventUt`
- `RuntimeTests.PartEventTiming_DeployableTransition_AppliesAtEventUt`

Capture `KSP.log` and `parsek-test-results.txt`, then close this item if both pass cleanly.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`, `CHANGELOG.md`, `docs/dev/test-coverage-audit-2026-04-19.md`, `docs/dev/todo-and-known-bugs.md`.

**Status:** OPEN - IMPLEMENTED LOCALLY, LIVE VALIDATION PENDING.

---

## 493. Destructive FLIGHT runtime tests now have an isolated batch mode locally, but it still needs live KSP validation

**Source:** follow-up from the test-coverage audit after repeated manual single-run passes for the destructive FLIGHT canaries became the main workflow bottleneck.

**Current state:** the branch now has an explicit `Run All + Isolated` / `Run+` path in the in-game runner. That mode captures a temporary uniquely-named baseline save in `FLIGHT`, then quickloads that baseline between selected destructive tests. A live run from `logs/2026-04-21_1750_validate-batch-ui-terminalorbit-isolated` passed with `FLIGHT captured=170 Passed=132 Failed=0 Skipped=38` plus the two `SPACECENTER` scene-exit tests passing separately. The widened isolated-batch cohort is:

- `RuntimeTests.AutoRecordOnLaunch_StartsExactlyOnce`
- `RuntimeTests.AutoRecordOnEvaFromPad_StartsExactlyOnce`
- `RuntimeTests.TreeMergeDialog_DiscardButton_ClearsPendingTree`
- `RuntimeTests.TreeMergeDialog_DeferredMergeButton_CommitsPendingTree`
- `GhostPlaybackTests.RunAllDuringWatch_DoesNotLeakSunLateUpdateNREs`
- `FlightIntegrationTests.KeepVessel_FastForwardIntoPlayback_SpawnsExactlyOnce`
- `FlightIntegrationTests.BridgeSurvivesSceneTransition`
- `FlightIntegrationTests.Quickload_MidRecording_ResumesSameActiveRecordingId`
- `FlightIntegrationTests.RevertToLaunch_SoftUnstashesPendingTree_WithoutMergeDialog`

`SceneExitMerge` intentionally remains manual-only. The latest live logs show that although both scene-exit tests passed, the save still picked up a real post-run vessel crash (`Kerbal X crashed through terrain on Kerbin`), so the exit-to-KSC path is still too state-dirty to trust inside the isolated FLIGHT batch.

Local CLI verification for this runner change is still blocked on this machine's `.NETFramework,Version=v4.7.2` targeting-pack / restore issues, so the next real confidence step is live KSP evidence plus code review rather than a healthy local `dotnet build`.

**Why this matters:** this is the first attempt to reduce the repeated game-load churn without weakening the safety of ordinary `Run All`. If it holds up live, most destructive FLIGHT canaries stop being "one test per game session" work and become practical batch coverage.

**Proposed next step:** from a disposable prelaunch `FLIGHT` session in this worktree build, use `Run All + Isolated` or per-category `Run+`, then collect `KSP.log`, `Player.log`, and `parsek-test-results.txt`. The validation bar is:

- the `[isolated]` tests above run in one session without manual reloads
- the runner quickloads the baseline back between destructive tests
- the widened isolated FLIGHT canaries (`QuickloadResume` bridge/mid-recording and `RevertFlow`) survive batch restore cleanly enough to keep the session usable
- `SceneExitMerge` remains manual-only until the live post-run vessel/crash contamination is eliminated

**Files:** `Source/Parsek/InGameTests/InGameTestAttribute.cs`, `Source/Parsek/InGameTests/Helpers/QuickloadResumeHelpers.cs`, `Source/Parsek/InGameTests/InGameTestRunner.cs`, `Source/Parsek/InGameTests/TestRunnerShortcut.cs`, `Source/Parsek/UI/TestRunnerUI.cs`, `Source/Parsek/InGameTests/RuntimeTests.cs`, `Source/Parsek.Tests/InGameTestRunnerTests.cs`, `CHANGELOG.md`, `docs/dev/test-coverage-audit-2026-04-19.md`, `docs/dev/todo-and-known-bugs.md`.

**Status:** OPEN - IMPLEMENTED LOCALLY, LIVE VALIDATION PENDING.

---

## ~~480. `FlightIntegrationTests.ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents` / `FailedActivation_DoesNotEmitEvent` NRE ~2ms into SPACECENTER run on a career save with an activatable stock strategy~~

**Source:** `logs/2026-04-19_0123_test-report/parsek-test-results.txt` + `KSP.log:9471-9474`.

```
[01:20:32.161] [VERBOSE][TestRunner] Running: FlightIntegrationTests.ActivateAndDeactivate_StockStrategy_EmitsLifecycleEvents
[01:20:32.163] [WARN][TestRunner]    FAILED: ... - Object reference not set to an instance of an object
[01:20:32.169] [VERBOSE][TestRunner] Running: FlightIntegrationTests.FailedActivation_DoesNotEmitEvent
[01:20:32.170] [WARN][TestRunner]    FAILED: ... - Object reference not set to an instance of an object
```

~2ms from `Running:` → `FAILED:` on both, so the NRE fires very early in the test body. Save is career (`saves/c1/persistent.sfs` shows `Mode = CAREER`), so the career-mode and `StrategySystem.Instance != null` guards at `RuntimeTests.cs:3915-3932` both pass. The NRE happens further in — likely around `FindActivatableStockStrategy()` (`:3891-3907`) reading `strategy.Config.Name` when `Config` is momentarily null, `SnapshotFinancials()` (`:3847-3855`, defensive — probably not it), or `strategy.Activate()` call path (`:3975`) throwing in stock code for some reason.

**Concern:** these are both `#439` Phase A regression tests for `StrategyLifecyclePatch`. A failure here means one of: (a) the patch is throwing and bypassing the expected StrategyActivated emission, (b) the test helpers are fragile against the particular career-save state, (c) stock's `Strategy.Activate()` itself NREs on this save shape. Without a stack trace the three can't be distinguished from the log alone — the test runner reports `ex.Message` only.

**Fix:** first widen the test runner's failure capture so we get the stack trace. Add one line to whatever catches the test exception (grep for `FAILED:` emit site in `InGameTestRunner.cs`) to log `ex.ToString()` at WARN instead of just `ex.Message` — a stack trace turns this into a 5-minute fix instead of a week of guessing. Once the stack lands, root-cause the NRE:

- If it's `strategy.Config.Name` → tighten the null guard in `FindActivatableStockStrategy` to also require `s.Config.Name != null`.
- If it's inside `StrategyLifecyclePatch` postfix → the patch is throwing in a stock code path it didn't previously handle; fix the patch.
- If it's inside stock's `Activate()` → log a skip with the offending strategy's configName so future investigation has the signal, and move on.

Separately: the same save-state shape may make #439 Phase A behaviour unreliable in production, not just in the test harness. If the post-fix investigation reveals `StrategyLifecyclePatch` is the thrower, that's a shipped bug, not just a test fail.

**Files:** `Source/Parsek/InGameTests/InGameTestRunner.cs` (add `ex.ToString()` to the FAIL log), `Source/Parsek/InGameTests/RuntimeTests.cs:3891-3907` (possibly harden `FindActivatableStockStrategy`), `Source/Parsek/Patches/StrategyLifecyclePatch.cs` (if the postfix is implicated).

**Scope:** Small after the stack trace lands. Investigate first — don't patch blindly.

**Dependencies:** none (the other StrategyLifecycle work is on main already).

**Resolution:** fixed in PR #409 (`issue-480-stock-strategy-lifecycle`). Root cause landed in the test harness: probing stock strategies on the first SPACECENTER frames could catch the strategy system mid-hydration, and the tests also lacked targeted diagnostics around stock `Activate()` itself. The fix adds a bounded readiness/stability probe, rejects nameless configs, and fails loudly if readiness never settles or activation still throws after stabilization.

**Status:** CLOSED. Fixed for v0.8.3.

---

## ~~479. `FlightIntegrationTests.FinalizeReSnapshot_StableTerminal_LiveVessel_UpdatesSnapshotAndMarksDirty` fails in FLIGHT — `sit` field not refreshed from the live vessel after stable-terminal re-snapshot~~

**Source:** `logs/2026-04-19_0123_test-report/parsek-test-results.txt:18, 41`.

```
FAIL  FlightIntegrationTests.FinalizeReSnapshot_StableTerminal_LiveVessel_UpdatesSnapshotAndMarksDirty (1.0ms)
      Snapshot sit field must be refreshed from the live vessel, not preserved from the stale source
```

**Concern:** the #289 re-snapshot invariant is that when `FinalizeIndividualRecording` runs on a stable-terminal recording (`TerminalStateValue` set) with a live active vessel, the recording's `VesselSnapshot` gets replaced by a fresh snapshot from that vessel, and `sit` should reflect the vessel's actual situation (LANDED/SPLASHED/etc.), not the stale "FLYING" from the original snapshot. The test at `RuntimeTests.cs:3219-3286` builds a recording with `TerminalStateValue = Landed` and a stale `sit=FLYING` snapshot, invokes `FinalizeIndividualRecording(rec, ..., isSceneExit: true)`, then asserts `sit != "FLYING"` — and that assertion fails (`:3276-3277`). So the current code either (a) doesn't replace the snapshot at all, (b) replaces it with a fresh snapshot whose `sit` was also written as FLYING (bug in `BackupVessel()` or equivalent), or (c) replaces it but doesn't persist the new `sit` value.

Corresponding post-#289 re-snapshot path in `ParsekFlight.cs` is around `:6917-6928` (the `backfilled TerminalOrbitBody=` logs visible in earlier collected logs confirm this path fires). Check whether the path calls `vessel.BackupVessel()` and writes the returned ConfigNode to `rec.VesselSnapshot`, or whether it only updates specific fields and skips `sit`.

**Concern (downstream):** if the re-snapshot keeps the stale FLYING sit, the spawn path at `VesselSpawner` (`ShouldUseRecordedTerminalOrbitSpawnState`, `:707`) or `SpawnAtPosition`'s situation override (`:317-320`) will receive a recording whose snapshot sit contradicts the terminal state — the spawner already has defensive overrides for this shape (`#176 / #264` per code comments), but the re-snapshot path fighting them is a separate source of drift and may silently persist the wrong sit to the sidecar (next load sees FLYING).

**Fix:** trace the re-snapshot invocation site and confirm it calls `vessel.BackupVessel()` fully, then writes the result to `rec.VesselSnapshot` (the full ConfigNode, not field-by-field). If it already does that, check whether `BackupVessel()` for a LANDED-situation vessel actually emits `sit = LANDED` (some stock KSP snapshot paths capture from a cached state that may still read FLYING for one frame after situation transition — a `yield return null` / physics-frame wait before the re-snapshot closes that). Add an explicit `sit` override on the fresh snapshot derived from `rec.TerminalStateValue` so the stored value always matches the declared terminal, regardless of when the snapshot capture fires relative to KSP's situation-update tick.

Test should keep passing once the path writes a consistent `sit`; no other assertion in the test needs changes.

**Files:** `Source/Parsek/ParsekFlight.cs` (re-snapshot path near `:6917`), possibly `Source/Parsek/VesselSpawner.cs` (`BackupVessel` usage), `Source/Parsek/InGameTests/RuntimeTests.cs:3219-3286` (no changes — the test is correct as-is).

**Scope:** Small. Likely a 5-line fix to force-set `sit` from the terminal state after `BackupVessel()`.

**Dependencies:** #289 original fix (shipped). This is the regression test catching a hole the original fix left.

**Status:** CLOSED 2026-04-19. Fixed in PR #407 — stable-terminal re-snapshots now normalize unsafe `BackupVessel()` `sit` values before persisting the fresh snapshot, so the FLIGHT regression and stale-sidecar drift are resolved.

---

## ~~478. `RuntimeTests.MapMarkerIconsMatchStockAtlas` runs in EDITOR / MAINMENU / SPACECENTER where `MapView.fetch` doesn't exist — should be scene-gated to FLIGHT + TRACKSTATION only~~

**Closed:** 2026-04-19 in PR #406.

**Source:** `logs/2026-04-19_0123_test-report/parsek-test-results.txt:15, 21, 24, 434-438`.

```
[MapView]
  RuntimeTests.MapMarkerIconsMatchStockAtlas
    EDITOR         FAILED  (0.1ms) — MapView.fetch should exist — test requires flight or tracking station scene
    FLIGHT         PASSED  (0.5ms)
    MAINMENU       FAILED  (1.5ms) — MapView.fetch should exist — test requires flight or tracking station scene
    SPACECENTER    FAILED  (0.2ms) — MapView.fetch should exist — test requires flight or tracking station scene
    TRACKSTATION   PASSED  (3.4ms)
```

**Concern:** the `[InGameTest(Category = "MapView", ...)]` attribute at `RuntimeTests.cs:511-512` has no `Scene =` property, which defaults to `InGameTestAttribute.AnyScene = (GameScenes)(-1)` (`InGameTestAttribute.cs:18,21`). The test body requires `MapView.fetch` (available only in FLIGHT and TRACKSTATION per KSP's scene model) and correctly asserts its existence, but surfaces that assertion as a FAIL rather than a skip. Net effect: 3 of 5 scenes report FAIL for a test that is *expected* to only run in 2 scenes.

The `InGameTestAttribute` only supports a single `GameScenes` value; it can't express "FLIGHT OR TRACKSTATION" directly. Two valid fixes:

1. **Extend the attribute to accept a scene set.** Add a `GameScenes[] Scenes` property (or convert `Scene` to a `[Flags]`-like mask) and update `InGameTestRunner` scene-filter logic to match if any listed scene equals the current scene. More invasive; future-proofs other tests.
2. **Skip at the top of the test body** (`RuntimeTests.cs:513-…`) when `HighLogic.LoadedScene` is not FLIGHT or TRACKSTATION: `if (HighLogic.LoadedScene != GameScenes.FLIGHT && HighLogic.LoadedScene != GameScenes.TRACKSTATION) { InGameAssert.Skip("requires MapView scene"); return; }`. One-method change, keeps other callers of the attribute unaffected.

Option 2 is the cheapest and matches what several other tests already do internally (see `StrategyLifecycle` tests at `:3915-3932` for the skip pattern). Option 1 is worth doing only if a batch of other tests would benefit.

**Fix:** implemented option 2 — added the scene skip at the top of `MapMarkerIconsMatchStockAtlas`. Audited other `Category = "MapView"` / `Category = "TrackingStation"` tests; no other exposed `AnyScene` cases found.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs:513` (add skip), optionally `Source/Parsek/InGameTests/InGameTestAttribute.cs` if option 1 is chosen.

**Scope:** Trivial. 3-line skip + audit of adjacent tests.

**Dependencies:** none.

**Status:** CLOSED. Priority: low — fixed in PR #406. Unsupported scenes now skip instead of failing, so the per-scene report no longer shows three false FAILs for this test.

---

## 477. ~~Ledger walk over-counts milestone rewards — post-walk reconciliation `expected` sum is a 2× / 3× multiple of the actual stock enrichment~~

**Resolution (2026-04-19):** CLOSED. Re-investigation showed the ledger walk was not duplicate-crediting milestone rewards into career state. The real bug sat in `LedgerOrchestrator.ReconcilePostWalk`: it intentionally aggregated same-window `Progression` legs so one coalesced stock delta could match multiple milestone actions at the same UT, but it logged that aggregate under each individual `MilestoneAchievement id=...`. That made `expected=` look 2× / 3× too large whenever two milestones shared the window (for example `Mun/Flyby` + `Kerbin/Escape`, or the `RecordsAltitude` / `RecordsSpeed` bursts), and the missing-event path emitted one WARN per action instead of one WARN per coalesced window. The fix now compares/logs once per coalesced window, reports grouped ids/counts for shared funds/rep legs, and keeps single-contributor legs (such as `Kerbin/Escape` science) attributed to the single action. Regression tests cover both the aggregate-match path and the missing-event path.

**Source:** `logs/2026-04-19_0117_thorough-check/KSP.log`. Worked example for one Mun/Flyby milestone:

```
19799: [INFO][GameStateRecorder] Milestone enriched: 'Mun/Flyby' funds=13000 rep=1.0 sci=0.0
22085: [WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, funds): MilestoneAchievement id=Mun/Flyby expected=26200.0  (← 2× actual)
22086: [WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, rep):   MilestoneAchievement id=Mun/Flyby expected=3.0      (← 3× actual)
22087: [WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, sci):   MilestoneAchievement id=Mun/Flyby expected=1.0      (← actual is 0.0)
```

Same shape across `RecordsSpeed`, `RecordsAltitude`, `RecordsDistance` (hundreds of WARNs with expected 14400 / 9600 while stock gives 4800 per trigger — the Post-walk reconcile summary reports `actions=24, matches=0, mismatches(funds/rep/sci)=24/14/6` meaning ZERO of 24 actions matched; every single one had funds over-counted, 14 had rep over-counted, and 6 had sci incorrectly expected when the enrichment shows `sci=0.0`).

**Concern:** the reconciliation WARN phrasing ("no matching event") was misleading me earlier — on re-read, `CompareLeg` (`LedgerOrchestrator.cs:4248-4310`) sums `summedExpected` from `SumExpectedPostWalkWindow` which collects *every matching leg* across actions within the coalesce window. If the ledger holds multiple actions for the same milestone-at-same-UT (e.g. one per recording finalize pass, or one per recalc that re-replayed the same enrichment), `summedExpected` becomes N × the actual stock reward even though stock only fired it once. The reconciliation then correctly flags the mismatch — but the real bug is upstream: **the ledger is emitting a `MilestoneAchievement` action more than once per stock milestone fire**.

The duplicate emissions correlate with `actionsTotal` growing across recalcs even when no new stock events happened (`RecalculateAndPatch: actionsTotal=32 → 32 → 32 → 39` over a ~20ms span near `:01:07:54`). Each bump is a recording's commit path replaying its enrichment into the ledger without dedup.

This supersedes / refines #462 (prior observation was "double-count for a single milestone"; #477 is the general case across every milestone). #469's old "zero-match" shape turned out to be a separate stale-history live-store-coverage bug and is now closed independently; it did not resolve the duplicate-action emission tracked here.

**Fix:** trace the emission path. Two hypotheses, in priority order:

1. **Duplicate action insertion.** Every `LedgerOrchestrator.NotifyLedgerTreeCommitted` (or wherever `MilestoneAchievement` actions are created from recording state) re-inserts the action without checking whether an equivalent action already sits in the ledger. Expected fix: dedup by `(MilestoneId, UT, RecordingId)` — if the same triple is already present, skip. Watch out for the repeatable-record semantics (`RecordsSpeed` can legitimately fire multiple times per session at different UTs; dedup must be per-UT, not per-id).
2. **Recalc replay re-walks committed recordings without clearing prior action copies.** Between `Post-walk reconcile: actions=32` and `actions=39`, seven actions were added without corresponding new stock events. If `RecalculateAndPatch` clears the ledger and re-walks, it should land on the same 32 actions; if it doesn't clear first, each recalc adds another copy. Verify the clear path in `RecalcEngine.cs` / `LedgerOrchestrator.RecalculateAndPatch` entry.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs` (`NotifyLedgerTreeCommitted`, `RecalculateAndPatch`), `Source/Parsek/GameActions/MilestonesModule.cs` (or whichever module emits `MilestoneAchievement` actions). Test: xUnit seeding a recording with one `MilestoneAchieved` event, calling `NotifyLedgerTreeCommitted` twice (simulating double-commit), asserts the ledger action count does not double and post-walk reconcile reports `matches=1, mismatches=0`.

**Scope:** Medium. Finding the exact emit site is the work; once identified, the dedup or clear-first fix is ~5 lines.

**Dependencies:** read `#307 / #439 / #440 / #448` notes in `done/todo-and-known-bugs-v3.md` first — those touched the earnings-reconciliation path and clarify which side of the dedup is the correct place to land the fix.

**Status:** CLOSED. Was high priority because the false-positive WARN volume obscured real reconciliation signals.

---

## ~~476. Post-walk reconciliation runs in sandbox mode (where KSP does not track funds/science/rep) and floods the log with "store delta=0.0" and "no matching event" false positives~~

**Source:** `logs/2026-04-19_0117_thorough-check/KSP.log`. The session was sandbox mode (`Funding.Instance is null (sandbox mode) — skipping`, `ResearchAndDevelopment.Instance is null (sandbox mode) — skipping`, `Reputation.Instance is null (sandbox mode) — skipping`, all repeating throughout) yet:

```
[WARN][LedgerOrchestrator] Earnings reconciliation (funds): store delta=0.0 vs ledger emitted delta=72800.0 — missing earning channel? window=[15.1,79.9]
[INFO][LedgerOrchestrator] Post-walk reconcile: actions=24, matches=0, mismatches(funds/rep/sci)=24/14/6, cutoffUT=null
```

`actions=24, matches=0` every single reconcile sweep — because stock KSP doesn't fire the Funds/Science/Reputation changed events in sandbox, so the store has nothing to compare against. The reconciliation is doing work and producing noise that has no actionable meaning on this save.

**Concern:** `LedgerOrchestrator.RecalculateAndPatch` unconditionally runs `ReconcilePostWalkActions` (and the window-level variant that emits the `store delta=0.0 vs ledger emitted delta=N — missing earning channel?` lines) regardless of whether KSP's tracked state is available. In sandbox every reconcile fires the full set of "mismatch" WARNs because the comparison baseline is zero. This compounds with #477 (duplicate emissions) to produce 700+ WARNs per session on a sandbox save that should have no WARNs at all.

Same concern applies to any save where the relevant `*.Instance` accessor is null for a legitimate game-mode reason (sandbox, tutorial, scenario that disables the currency).

**Fix:** at the entry of `ReconcilePostWalkActions` and `ReconcileEarningsWindow` (`LedgerOrchestrator.cs` around `:430-451` and `:4230` onward), gate the reconciliation per-resource on the KSP singleton availability. Pseudocode:

```csharp
bool fundsTracked = Funding.Instance != null;
bool sciTracked   = ResearchAndDevelopment.Instance != null;
bool repTracked   = Reputation.Instance != null;
// ... skip fund/sci/rep legs individually when their tracker is null
if (!fundsTracked && !sciTracked && !repTracked) return;
```

Log a single one-shot VERBOSE `[LedgerOrchestrator] Post-walk reconcile skipped: sandbox / tracker unavailable (funds={f} sci={s} rep={r})` so the skip is observable without being repeated every recalc. Existing `PatchFunds: Funding.Instance is null (sandbox mode) — skipping` pattern at `KspStatePatcher.cs` is the template.

Per-leg gating (not whole-sweep) is the correct granularity — a save that disables only one currency should still reconcile the other two.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs` (`ReconcilePostWalkActions`, `ReconcileEarningsWindow`, `CompareLeg` entry). Test: xUnit seeding a `Funding.Instance = null` state (if the test rig can stub it) or verifying via the log sink that no WARN fires when `RecalculateAndPatch` is called with the trackers disabled.

**Scope:** Small. ~15 lines of gate + one log line.

**Dependencies:** none. Independent of #477 — fixing this one also reduces the reproducibility noise around #477 and #469 since a sandbox session after this fix would emit zero reconciliation WARNs.

**Resolution (2026-04-19):** Fixed in `fix-476-sandbox-postwalk`. `LedgerOrchestrator.ReconcileEarningsWindow` and `ReconcilePostWalk` now gate funds / science / reputation legs on live tracker availability, skip the whole sweep when all three trackers are unavailable, and emit a one-shot VERBOSE skip line instead of repeating WARNs every recalc. Added unit + integration coverage for both the all-trackers-missing sandbox shape and partial per-resource gating.

**Status:** CLOSED. Priority was medium — pure log-hygiene, but the hygiene payoff was large (a sandbox session goes from ~700 WARNs to 0).

---

## ~~475. Ghost whose recording terminates in Mun orbit spawns on a Kerbin-SOI-eject trajectory instead of in Mun orbit (post-rewind, map-view watch)~~

**Source:** user playtest report — "when recording a trip that ends in Mun orbit, after rewind when watching the ghost in map view, the ghost gets to the Mun encounter but then instead of spawning in Mun orbit, it spawns in a Kerbin SOI eject trajectory."

**Fix shipped:** terminal-orbit capture no longer falls back to `"Kerbin"` when `orbit.referenceBody` is null, finalization/load-time backfill only trusts a last `OrbitSegment` when that segment agrees with the recording endpoint body, and spawn-at-end now resolves the body from the actual endpoint before attempting any recorded-orbit propagation. If no endpoint-aligned orbital seed exists, spawn falls back to the endpoint state instead of constructing a wrong Kerbin-frame orbit.

**Tests:** added xUnit coverage for endpoint-aligned terminal-orbit backfill and endpoint-aligned orbital spawn-seed selection across Kerbin → Mun end-state shapes.

**Status:** done/closed on this branch. Priority was high because the bad cached body could throw the spawned vessel onto a solar-escape path after rewind and effectively destroy the mission outcome.

---

## ~~474. Ghost audio sometimes plays in a single stereo channel instead of centered when the Watch button snaps the camera to the ghost~~

**Status:** DONE / closed 2026-04-19.

**Fix shipped:** fresh ghost builds now recalculate `cameraPivot` immediately instead of leaving the initial watch target at the raw root origin; ghost loop + one-shot `AudioSource`s are then re-anchored to that watch pivot, forced back to `panStereo = 0`, and run with `spatialBlend = 0.75f` instead of fully-3D `1.0f`. `HideAllGhostParts()` also mutes those detached ghost audio sources when the ghost is hidden. That keeps Watch-mode framing and the dominant ghost audio source aligned, so engine/explosion playback no longer hard-pans into one ear when the camera snaps to the ghost.

**Verification added:** runtime coverage now checks both invariants directly: `cameraPivot` recenters to the active-part midpoint on a fresh ghost, and re-anchored ghost audio sources land on `cameraPivot` with centered stereo defaults.

---

## ~~473. Gloops group in the Recordings window should be treated as a permanent root group — no `X` disband button, and pinned to the top of the list~~

**Source:** user playtest request.

**Concern:** the Gloops group is created by `RecordingStore.CommitGloopsRecording` at `Source/Parsek/RecordingStore.cs:394-409` and uses the constant `GloopsGroupName = "Gloops - Ghosts Only"` (`:63`). In `UI/RecordingsTableUI.cs:1725`, the disband-eligibility gate reads `bool canDisbandGroup = !RecordingStore.IsAutoGeneratedTreeGroup(groupName);` — so auto-generated tree groups get a single `G` button, but the Gloops group falls through to `DrawBodyCenteredTwoButtons("G", "X", …)` at `:1734`, exposing an `X` that invokes `ShowDisbandGroupConfirmation`. Disbanding the Gloops group would leave new Gloops commits either re-creating it on the next commit (`:408-409` re-adds the name when missing) or reverting to standalone, and there is no user story for disbanding a system-owned group.

Separately, root-group ordering is decided by `GetGroupSortKey` + the column's sort predicate in `RecordingsTableUI.cs:1077-1079`. The Gloops group ends up wherever its sort key lands among the user's trees/chains, which is inconsistent frame-to-frame as the user sorts by different columns.

**Fix:** DONE. Added `RecordingStore.IsPermanentGroup` / `IsPermanentRootGroup`, switched the Recordings-table disband gate to the permanent-group predicate, and moved root-item sorting behind a dedicated comparator that pins the Gloops group above every other root item regardless of sort column or ascending/descending state. Also hardened `GroupHierarchyStore.SetGroupParent` so `Gloops - Ghosts Only` cannot be nested under another group, and `BuildGroupTreeData` now self-heals any stale permanent-root hierarchy mapping back to root on first draw rather than only papering over one saved-parent case.

**Edge case checked:** the legacy rename path still runs in `RecordingTree.LoadRecordingFrom` before UI grouping/sorting sees the loaded recording groups, so pre-rename saves normalize to the modern `Gloops - Ghosts Only` name before the permanent-group rules apply.

**Files:** `Source/Parsek/UI/RecordingsTableUI.cs`, `Source/Parsek/RecordingStore.cs`, `Source/Parsek/GroupHierarchyStore.cs`, plus xUnit coverage in `Source/Parsek.Tests/GroupManagementTests.cs`, `Source/Parsek.Tests/GroupTreeDataTests.cs`, and `Source/Parsek.Tests/RecordingsTableUITests.cs`.

**Scope:** Small. Slightly larger than the original estimate because the final fix also closes the parent-assignment side door and auto-heals old hierarchy state.

**Dependencies:** none.

**Status:** ~~DONE~~. Priority was low-medium — UI polish, but now shipped independently of #471 because the root-group semantics were self-contained and low-risk.

---

## ~~472. Watch-mode camera pitch/heading jumps when playback hands off to the next segment within a recording tree (e.g. flying → landed)~~

**Source:** user playtest report — "when watching a recording, maintain the camera watch angle exactly the same when transitioning to another recording segment (right now it moves when vessel is going from flying to landed for example)."

**Concern:** inside a single tree (chain/branch) the active playback ghost changes at each segment boundary (flying recording ends, landed recording's ghost becomes the new camera target). Investigation on `origin/main` confirmed the explicit tree-segment transfer path (`TransferWatchToNextSegment`) already captured and replayed `WatchCameraTransitionState`; the remaining snap lived in adjacent watch retarget paths that still did raw `FlightCamera.SetTargetTransform(...)` calls. The exposed offenders were the loop/overlap `RetargetToNewGhost` handlers plus the quiet-expiry / primary-cycle fallback, overlap-hold rebind, and stock vessel-switch re-target path. Those branches swapped the target transform without replaying the current pitch/heading, so the camera yanked to whatever framing the new target basis implied.

The existing loop-cycle-boundary code path (`CameraActionType.RetargetToNewGhost` inside `HandleLoopCameraAction`) has the same shape — if the bug reproduces at loop boundaries too, the fix covers both. Confirm during fix.

**Fix landed (2026-04-19):** centralized the retarget angle replay around `TryResolveRetargetedWatchAngles` inside `WatchModeController`'s watch-camera rebind path. Every watch-mode ghost rebind that should preserve framing now captures the current watch camera state, primes the replacement ghost's `horizonProxy` before target selection when `HorizonLocked` is active, then re-targets and replays compensated pitch/heading in the new target basis instead of doing a raw `SetTargetTransform(...)`. Applied to:

- loop `RetargetToNewGhost`
- overlap `RetargetToNewGhost`
- watched-cycle fallback to primary
- overlap-hold completion rebind
- quiet-expiry bridge retarget
- stock `OnVesselSwitchComplete` re-target

Edge cases to cover in the test matrix:
- `HorizonLocked` mode (default on entry) — pitch/heading are relative to the horizon and must survive the target swap
- `Free` mode — same requirement, but the relative frame is the ghost's local frame
- Overlap retarget vs non-overlap — both code paths at `:711` and `:731`
- Loop cycle boundary — verify same issue/fix

**Files:** `Source/Parsek/WatchModeController.cs` (retarget sites + helper plumbing), `Source/Parsek.Tests/WatchModeControllerTests.cs` (pure retarget-angle regression coverage). Verification in this environment used `dotnet build --no-restore` plus a direct reflection harness over the compiled `WatchModeControllerTests` methods because the standard `dotnet test` runner aborts here on local socket initialization (`SocketException 10106` before test execution starts).

**Scope:** Closed in a small controller-only patch. The explicit tree-transfer path needed no change; the missing coverage was the remaining raw re-target sites around loop/overlap and stock camera rebinds.

**Dependencies:** none.

**Status:** Closed — shipped for v0.8.3 as a targeted watch-camera retarget preservation pass. Priority was medium.

---

## ~~471. Gloops recordings should not loop by default; commit path should set `LoopPlayback=false` and `LoopIntervalSeconds=0` (auto)~~

**Source:** user request — "gloops recordings should no longer be looped by default and their loop period should be set to auto when they are created."

**Status:** ~~Fixed~~ in this PR. `ParsekFlight.CommitGloopsRecorderData` now writes `LoopPlayback=false`, `LoopIntervalSeconds=0`, and `LoopTimeUnit=LoopTimeUnit.Auto` before `RecordingStore.CommitGloopsRecording`, so fresh Gloops captures no longer start looping immediately and the stored "auto" period behaves correctly if the player later turns looping back on.

Comments/docs updated in `ParsekFlight`, `GloopsRecorderUI`, and `docs/user-guide.md` so the user-facing text matches the shipped behavior.

Regression coverage now invokes `CommitGloopsRecorderData` with a minimal `ParsekFlight` + `FlightRecorder` harness and asserts the committed recording is ghost-only, non-looping, and stored with the auto period settings. The commit path now resolves the active-vessel name through a small defensive helper so the unit test can run without KSP's Unity-backed `FlightGlobals` static initializer.

No schema change: existing recordings that already have `LoopPlayback=true` are preserved as-is.

---

## 470. ~~`Funds` subsystem logs `FundsSpending: -0, source=Other` hundreds of times per session (134 lines in one 15-minute career run)~~ CLOSED 2026-04-19

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log`. Top-of-list pattern in the deduplicated WARN/VERBOSE counts:

```
134 [Parsek][VERBOSE][Funds] FundsSpending: -0, source=Other, affordable=true, runningBalance=N, recordingId=(none)
```

**Concern:** every `RecalculateAndPatch` sweep (33 of them in this session) fans out to the per-module replay, and each module emits a `FundsSpending: -0` line for zero-delta entries inside the "Other" source bucket. Zero-delta spendings convey nothing a reader would ever act on, and at 4 per recalc × 33 recalcs = 132 lines, they bury the real entries. Adjacent modules already early-return on zero-delta (see the verbose threshold filters in `GameStateRecorder.cs`), so this one is the odd one out.

**Fix shipped (2026-04-19):** `Source/Parsek/GameActions/FundsModule.cs` now suppresses the success VERBOSE log when `FundsSpent == 0`, which is enough to eliminate the `FundsSpending: -0` replay spam without hiding any real low-value spendings. The action still flows through affordability and running-balance updates unchanged.

**Files:** `Source/Parsek/GameActions/FundsModule.cs`, `Source/Parsek.Tests/FundsModuleTests.cs`. Added `FundsSpending_ZeroCost_DoesNotLogVerboseSpend`, which submits a zero-cost `FundsSpending(Other)` action, asserts the action remains affordable with no balance change, and confirms the log sink stays silent.

**Scope:** Trivial. One-line guard + one test.

**Dependencies:** none.

**Status:** CLOSED 2026-04-19. Priority: low — pure log-hygiene. Ready to ship with the targeted regression coverage above.

---

## ~~469. Post-walk reconciliation fails to find same-UT FundsChanged events that are demonstrably in the store — "no matching event keyed 'Progression'" warns fire on events that exist~~

**Source:** `logs/2026-04-19_0014_investigate/KSP.log` and `logs/2026-04-19_0123_test-report/Player.log`.

**Root cause (confirmed):** `CompareLeg` itself was not losing a same-UT same-key live event. The false WARNs happened later, after `GameStateStore.PruneProcessedEvents()` had already removed the relevant `FundsChanged(Progression)` rows from the live store. `ReconcilePostWalk` walks the full ledger history on every recalc, but the store only retains the current live tail: resource events at or below the latest committed milestone `EndUT` are pruned, and after a rewind/load the current epoch may also have no live-event coverage for older-epoch ledger actions. That is why the logs could show an earlier `AddEvent: FundsChanged key='Progression' ... ut=57.2` line and then later emit `no matching event` WARNs for the same milestone: the event existed when recorded, but no longer existed in the live store by the time the later recalc pass ran.

**Fix (2026-04-19):** gate post-walk reconciliation to the portion of ledger history the live store can still represent. `ReconcilePostWalk` now skips:

1. actions at or below the current epoch's prune threshold (`MilestoneStore.GetLatestCommittedEndUT()`), because their paired resource events have already been consumed and removed; and
2. pre-live-tail history after an epoch bump, where the current epoch has neither a live source anchor nor any live observed reward leg for the historical action being revisited.

If a live observed reward still exists but there is no live source anchor, the reconcile only continues when the same-UT window is unambiguous; otherwise it emits a one-shot VERBOSE coverage-skip and leaves the ambiguous stale-history action alone.

Live-tail mismatches still WARN normally, so this is not a blanket suppression of `Progression` reconciliation.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs`; targeted regressions in `Source/Parsek.Tests/EarningsReconciliationTests.cs`.

**Verification:** build succeeded in the worktree. Targeted regression methods executed directly from the built test assembly:

- `PostWalk_MilestoneAchievement_EffectiveTrue_AllLegsMatch_NoWarn`
- `PostWalk_MilestoneAchievement_EffectiveFalseDuplicate_Skipped_NoWarn`
- `PostWalk_MilestoneAchievement_CoalescedWindow_MatchesOnce_NoWarn`
- `PostWalk_MilestoneAchievement_CoalescedWindow_MissingEvent_WarnsOncePerLeg`
- `PostWalk_MilestoneAchievement_CoalescedTinyScienceLegs_AggregateWarnsOnce`
- `PostWalk_MilestoneAchievement_PrunedByCommittedThreshold_Skipped_NoWarn`
- `PostWalk_MilestoneAchievement_WithoutLiveSourceAnchorInNewEpoch_Skipped_NoWarn`
- `PostWalk_MilestoneAchievement_WithLiveSourceAnchorInNewEpoch_MissingFundsEvent_Warns`
- `PostWalk_MilestoneAchievement_WithLiveFundsButNoSourceAnchorInNewEpoch_DoesNotSkip`
- `PostWalk_MilestoneAchievement_StaleNeighborInsideCoalesceWindow_DoesNotInflateLiveExpected`
- `PostWalk_MilestoneAchievement_StaleObservedEventIgnored_InLiveWindow`
- `PostWalk_MilestoneAchievement_ThresholdStraddlingStaleNeighbor_DoesNotSuppressLiveFallback`
- `PostWalk_MilestoneAchievement_LiveNoSourceOverlap_SkipsAmbiguousFallback`

All passed.

**Dependencies / follow-up:** independent of #462 / #477. Those entries are about duplicate milestone-action emission and over-counted expected deltas; this fix only closes the stale-history zero-match WARN path.

**Status:** ~~TODO~~ CLOSED for v0.8.3.

---

## ~~468. `ScienceEarning` reconcile anchor UT is vessel-recovery-time, but `ScienceChanged 'ScienceTransmission'` events are emitted at transmission-time earlier in the flight — the 0.1s window can never match~~

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log:10410-10415`.

```
[WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, sci): ScienceEarning id=mysteryGoo@KerbinSrfLandedLaunchPad expected=11.0 but no matching ScienceChanged event keyed 'ScienceTransmission' within 0.1s of ut=204.4
```

Paired with the actual capture sequence earlier in the same session:

```
9272: [GameStateRecorder] Emit: ScienceChanged key='ScienceTransmission' at ut=39.8
9273: [GameStateStore] AddEvent: ScienceChanged key='ScienceTransmission' ut=39.8
9488: [GameStateStore] AddEvent: ScienceChanged key='ScienceTransmission' ut=66.3
```

**Concern:** the `ScienceEarning` ledger actions created from a committed recording are timestamped to the vessel-recovery UT (here 204.4 — the recovery event), but the KSP `ScienceChanged` events fire whenever stock transmits/completes a science subject, which for an in-flight launch is typically 20-100 seconds into the flight, long before recovery. `CompareLeg`'s `Math.Abs(e.ut - action.UT) > PostWalkReconcileEpsilonSeconds (0.1s)` gate then rejects the only events that could possibly match, and every recovered science subject produces a post-walk WARN.

Independent of #469 (where the event IS at the right UT and the reconcile still fails): this is the case where the event is at the wrong UT *for this particular leg*. Both show up in the same session; fixing one does not fix the other.

**Fix:** two options, pick based on the semantic of `ScienceEarning`:

1. **Anchor the action to transmission UT**, not recovery UT. If the ledger action is meant to reconcile with the per-subject transmission event, the action's UT should track the event's UT. This may require `ScienceModule.cs` (the emit site) to carry the per-subject transmission timestamp forward into the action instead of collapsing to recovery UT.
2. **Broaden the reconcile window for `ScienceEarning`** to cover the entire recording's UT span (e.g. accept any matching ScienceChanged event between recording start and recovery). Keep the 0.1s window for `MilestoneAchievement` where the instantaneous match is correct.

Option 1 is cleaner but touches the emit path; option 2 is localised to `CompareLeg` / `ReconcilePostWalkActions` in `LedgerOrchestrator.cs`.

**Files:** `Source/Parsek/GameActions/LedgerOrchestrator.cs` (+ possibly `ScienceModule.cs`). Test: xUnit seeding a ScienceEarning at recovery UT and a ScienceChanged at an earlier UT within the same recording span, asserts no post-walk WARN.

**Scope:** Small-to-medium depending on option. Option 2 is ~20 lines; option 1 requires an action-schema nudge.

**Dependencies:** surface with #469 during the same investigation — root-cause signal will tell which option is right.

**Status:** DONE/CLOSED (2026-04-19). `CompareLeg` now widens the observed-side `ScienceTransmission` match window to the owning recording span only for end-anchored `ScienceEarning` actions, and new science actions persist `StartUT`/`EndUT` so reloads keep the same reconcile context. `#469` remains separate: it is the same-UT false-negative path, not this earlier-transmission window mismatch.

---

## ~~467. `ReputationChanged` threshold filter rejects stock +1 rep awards — `Math.Abs(delta) < 1.0f` drops `0.9999995` rewards, breaking all records-milestone rep reconciliation~~

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log`.

```
9473: Added 0.9999995 (1) reputation: 'Progression'.
9476: [Parsek][VERBOSE][GameStateRecorder] Ignored ReputationChanged delta=+1.0 below threshold=1.0
```

**Concern:** stock KSP awards `0.9999995` reputation for Records* milestones (the `(1)` in the log is the rounded display value; the actual delta is `~1 − 5e-7`). `GameStateRecorder.cs:910` drops the event with `if (Math.Abs(delta) < ReputationThreshold)` where `ReputationThreshold = 1.0f` (`:222`). `0.9999995 < 1.0` is true, so the event never makes it into the store. The post-walk reconcile for the paired `MilestoneAchievement` rep leg then reports "no matching ReputationChanged event keyed 'Progression' within 0.1s" — in this session that produced all 44 rep-mismatch WARNs (`RecordsSpeed`, `RecordsAltitude`, `RecordsDistance` each firing two per recalc pass).

**Update (2026-04-19):** Fixed in the `#467` worktree. `OnReputationChanged` now keeps a small `0.001f` epsilon under the `1.0f` threshold so stock-rounded `0.9999995` awards still survive after cumulative-float subtraction (`old + reward - old` can land slightly below `0.9999995`, e.g. `0.99999x`). Added regression coverage in `Source/Parsek.Tests/GameStateRecorderResourceThresholdTests.cs` for both raw `+/-0.9999995`, the cumulative-float subtraction shape, and clear sub-threshold control cases.

**Original fix sketch:** one-line change in `Source/Parsek/GameStateRecorder.cs:910`:

```csharp
// Before:  if (Math.Abs(delta) < ReputationThreshold)
// After:   if (Math.Abs(delta) < ReputationThreshold - 0.001f)
```

Or lower `ReputationThreshold` to `0.5f` (any value strictly below stock's 0.9999995 — `0.5f` leaves headroom for other rounding cases while still filtering sub-integer noise). Pick the second form if you want one named constant doing the semantic work; the epsilon form is narrower but preserves the visible `1.0` threshold.

Similar care needed for `FundsThreshold = 100.0` and `ScienceThreshold = 1.0` — confirm stock never rewards *exactly* threshold values; if it does, apply the same epsilon trim.

**Files:** `Source/Parsek/GameStateRecorder.cs:910` (rep), possibly `:821` (funds) and the ScienceChanged analogue. Test: xUnit calling the onReputationChanged handler with delta `0.9999995f`, asserts the event is captured in the store (not dropped).

**Scope:** Trivial. One-line fix + one test + verify the twin thresholds.

**Dependencies:** none. Fixes the rep-mismatch tail of #469 specifically, though the underlying #469 investigation may also surface non-rep mismatches unrelated to this threshold.

**Status:** ~~TODO~~ Fixed for v0.8.3. Priority was high — shipped as a small recorder-side threshold hardening plus targeted unit coverage.

---

## ~~466. `RecalculateAndPatch` runs mid-flight with an incomplete ledger, patches funds DOWN to the pre-milestone target and destroys in-progress earnings~~

**Source:** `logs/2026-04-19_0049_career-ledger/KSP.log:9993`.

```
9993: [WARN][KspStatePatcher] PatchFunds: suspicious drawdown delta=-36800.0 from current=57795.0 (>10% of pool, target=20995.0) — earning channel may be missing. HasSeed=True
9995: [INFO][KspStatePatcher] PatchFunds: 57795.0 -> 20995.0 (delta=-36800.0, target=20995.0)
```

Two more occurrences at `:12839` (-9300) and `:13581` (-41546.7) within the same 10-minute session, all with identical shape: the live KSP funds are higher than the ledger's computed target because stock KSP has credited milestones the Parsek ledger does not yet know about.

**Concern:** `KspStatePatcher.PatchFunds` logs the `suspicious drawdown` WARN (`KspStatePatcher.cs:160-167`) but deliberately still applies the drawdown — the comment at `:156-159` says "log-only (never aborts the patch) — but a >10% drop alongside a small pool (>1000F) is the shape of missing-earnings bugs". In this session that design is **destructive**: the recalc was triggered mid-launch by an OnLoad at 00:35:27 (just after revert subscribe on `:9957`), at which point the `r0` recording's tree had not yet committed (`Committed tree 'r0'` is at `:10083`, ~4s later). `actionsTotal=4` at that recalc — rollout + initial seed only, no milestones. So the ledger's target of `25000 - 4005 = 20995` ignores the `+800` `+4800` `+4800` `+4800` `+4800` milestone credits stock had already awarded, and `Funding.Instance.AddFunds(delta=-36800, TransactionReasons.None)` silently deletes 36,800F of the player's money.

A subsequent recalc at `:10134` with `actionsTotal=12` (post-commit) computes the full target, but by then the funds have been re-patched several times and the reconcile is in the broken state described in #469. The three drawdowns are not three separate events — they are three recalc passes, each landing before a different tree's commit.

**User-visible:** player earns milestones in flight (visible in game UI), then on scene transition / quickload the funds snap back to a lower value. This is the "ledger/resource recalculation did not really work correctly" the reporter is describing.

**Fix:** shipped. `LedgerOrchestrator.RecalculateAndPatch` now still walks the committed ledger but defers the KSP write-back step whenever there is a live recorder or pending tree and the walk is a normal non-cutoff pass. That keeps in-flight / pending-tree earnings live in KSP until the tree is either committed or discarded, while rewind-style cutoff walks remain authoritative. `MergeDialog.MergeDiscard` and the deferred merge-dialog idle-on-pad auto-discard path now trigger an immediate recalculation after the pending tree is removed so a discard still cleanly tears those live effects back out.

Add a cross-reference: `#439`, `#440`, `#448` and the already-archived post-#307 reconciliation work all touched adjacent logic. Re-read `done/todo-and-known-bugs-v3.md` entries for those before writing the fix — the reason several drawdown WARNs were *kept* log-only was a prior bug where aborting the patch masked a different class of problem. Don't regress that.

**Files:** `Source/Parsek/GameActions/KspStatePatcher.cs` (patch gate), `Source/Parsek/GameActions/LedgerOrchestrator.cs` (`RecalculateAndPatch` entry check), `Source/Parsek/FlightRecorder.cs` or `ParsekFlight.cs` (uncommitted-tree predicate). Tests: xUnit for the gate (seed a mid-flight state, trigger recalc, assert no `Funding.AddFunds` call); integration-style log-assertion test covering the revert-mid-flight path.

**Scope:** Medium. Touches patch gating + recalc entry + a new predicate. Several test cases to cover revert/rewind/OnLoad/quickload interactions.

**Dependencies:** read the #307/#439/#440 history first. Fix should land before / alongside #469 since the reconcile warnings mostly disappear once the patch gate prevents the stale-target state from ever being written.

**Status:** Fixed in `0.8.3`. Priority was **critical** — now closed. The suspicious-drawdown WARN stays log-only for genuine rewind/reset paths, but the destructive "uncommitted tree patched back to committed-ledger funds" case is blocked at the orchestrator layer before `PatchFunds` runs.

---

## 465. ~~Ghost engine/RCS audio keeps playing while the KSP pause menu is open outside the flight scene~~

**Source:** user playtest report. "When paused (game menu open) in KSC view and probably other views, the sound from the rocket ghost is still audible."

**Concern:** ghost `AudioSource` components (engine loops, RCS, ambient clips) don't respond to KSP's global pause like stock vessels' audio does. In the flight scene this is already handled: `ParsekFlight.cs:657` subscribes to `GameEvents.onGamePause`/`onGameUnpause` and the handlers at `ParsekFlight.cs:4302-4315` delegate to `engine.PauseAllGhostAudio()` / `engine.UnpauseAllGhostAudio()`, which loop over active ghost states and call `AudioSource.Pause()`/`UnPause()` (see `GhostPlaybackLogic.cs:2358` for the helpers). KSC playback had no equivalent pause subscription, so ESC at KSC muted stock audio while ghost engine loops kept playing.

**Fix / Resolution (2026-04-19):** `ParsekKSC.cs` now subscribes to `GameEvents.onGamePause` / `onGameUnpause` in `Start`, unsubscribes in `OnDestroy`, and applies pause/unpause across its own `kscGhosts` + `kscOverlapGhosts` dictionaries via a shared helper that reuses `GhostPlaybackLogic.PauseAllAudio()` / `UnpauseAllAudio()`. The earlier "forward to the flight engine" idea was incorrect because KSC playback does not own a `GhostPlaybackEngine`. Tracking Station was checked separately: it publishes map/ProtoVessel ghosts only and does not instantiate `AudioSource`s, so no extra scene-side fix was needed there. Added xUnit coverage for the KSC audio-action helper and its logging/counting seam.

**Files:** `Source/Parsek/ParsekKSC.cs` (pause subscriptions + KSC-local audio-action helper), `Source/Parsek.Tests/KscGhostPlaybackTests.cs` (helper coverage). No Tracking Station code change required after verification.

**Scope:** Small. KSC-only fix plus unit coverage.

**Dependencies:** none.

**Status:** DONE/CLOSED (2026-04-19). Priority: medium — fixed in `fix-465-pause-menu-audio`.

---

## 464. ~~Timeline Details tab duplicates milestone / strategy entries — gray `GameStateEvent` line shadows the green `GameAction` reward line~~

**Source:** user playtest report. "From the Timeline Details tab list, remove the 'Milestone … achieved' messages and leave only the green ones, they're kind of duplicates; same for Strategy: activate / deactivate, duplicates."

**Concern:** for each milestone or strategy lifecycle event, the Timeline Details list renders two rows:

- the green `GameAction` row — rendered by `TimelineEntryDisplay.cs:296-308` for `GameActionType.MilestoneAchievement` and carries the user-meaningful data (milestone name + `+960 funds` / `+0.5 rep`). The strategy-activation variant is rendered in the same file for `GameActionType.StrategyActivate` / `StrategyDeactivate` (setup cost legs).
- the gray `GameStateEvent` row — rendered by `GameStateEvent.GetDisplayDescription` at `GameStateEvent.cs:398-399` (`"{key}" achieved`) and `:405-413` (`"{title}" activated` / `"{title}" deactivated`). These are emitted by the `GameStateRecorder` path for audit completeness but add no information beyond what the green GameAction row already shows.

Net effect: every milestone / strategy event shows up twice in the Timeline Details tab — first as the green reward summary, then as the plain gray confirmation. Players read this as redundant.

**Fix:** filter the duplicate `GameStateEventType.MilestoneAchieved`, `StrategyActivated`, and `StrategyDeactivated` rows out of the Timeline Details rendering when a matching green `GameActionType.MilestoneAchievement` / `StrategyActivate` / `StrategyDeactivate` already exists for the same UT + key. Two equally valid places to apply the filter:

1. In the timeline-details collator (wherever `GameStateEvent`s are merged into the per-recording display list — likely `ParsekUI` / `RecordingsTableUI` or a shared `TimelineBuilder` helper). Preferred — drops them at assembly time so the display path stays simple.
2. In `TimelineEntryDisplay` via a post-hoc "if a preceding entry for this UT already carries the milestone id / strategy title, skip this one" dedup. Works but leaks the dedup logic into the display layer.

Keep the gray rows emitted at the data layer — they're still useful for the raw event log / debugging. Only filter at the Timeline Details renderer level. Add a setting/toggle only if users actually want the duplicates back (unlikely given the report).

**Files:** `Source/Parsek/Timeline/TimelineEntryDisplay.cs` (or upstream of it — grep for whatever builds the Details list); `Source/Parsek/GameStateEvent.cs` only if the "achieved"/"activated" format strings themselves need to change (they don't for this bug — the fix is filtering, not rewording). Test: xUnit building a timeline with both a MilestoneAchievement GameAction and a matching MilestoneAchieved GameStateEvent at the same UT, asserts the rendered list contains exactly one row for that milestone (the green one).

**Scope:** Small. Single collator/filter site + one test. No schema or recording-format change.

**Dependencies:** none.

**Status:** ~~TODO~~ Fixed. `TimelineBuilder` now drops only duplicate legacy `MilestoneAchieved` / `StrategyActivated` / `StrategyDeactivated` rows when the timeline already contains the matching `GameAction` at the same UT + key, so the Details tab keeps the richer action row while leaving raw event capture untouched. Targeted verification: `Source/Parsek` + `Source/Parsek.Tests` build clean with `dotnet build --no-restore`; focused `TimelineBuilderTests` coverage was re-run in-process for `LegacyEvents_AppearAtT2`, `LegacyEvents_ResourceEventsFiltered`, and `LegacyMilestoneAndStrategyDuplicates_AreFilteredWhenMatchingGameActionsExist`.

---

## ~~463. Deferred-spawn flush skips FlagEvents — flags planted mid-recording never materialise when warp carries the active vessel past a non-watched recording's end~~

**Source:** user playtest `logs/2026-04-19_0014_investigate/KSP.log`. Reproducer:

1. Record an "Untitled Space Craft" flight; EVA Bob Kerman and plant a flag (`[Flight] Flag planted: 'a' by 'Bob Kerman'` at UT 17126).
2. Watch an unrelated recording (Learstar A1) and time-warp through the flag's UT.
3. At warp-end the capsule (#290) and kerbal (#291) materialise as real vessels via the deferred-spawn queue — but the flag 'a' does NOT spawn.
4. Stop watching Learstar; watch the actual Bob Kerman recording (#291) instead. Its ghost runs through UT 17126 normally, `[GhostVisual] Spawned flag vessel: 'a' by 'Bob Kerman'` fires, and the flag appears.

Specific log lines in the snapshot (all from the same session):

- `00:10:40.581 [Policy] Deferred spawn during warp: #291 "Bob Kerman"` — warp active, spawn queued.
- `00:10:57.316 [Policy] Deferred spawn executing: #291 "Bob Kerman" id=d631f348fde24b6f8fbeb00228d8e057` — warp ended, queue flushed; `host.SpawnVesselOrChainTipFromPolicy(rec, i)` runs and spawns the EVA vessel. Nothing touches `rec.FlagEvents`.
- `00:12:56.614 [GhostVisual] Spawned flag vessel: 'a' by 'Bob Kerman'` — only emitted when the actual Bob Kerman recording is watched (session 2), via the normal `GhostPlaybackLogic.ApplyFlagEvents` cursor path.

**Root cause:** flag vessel spawns are driven by `GhostPlaybackLogic.ApplyFlagEvents` (`Source/Parsek/GhostPlaybackLogic.cs:1892`), which walks `state.flagEventIndex` forward over `rec.FlagEvents` every frame a ghost is in range. Callers are `GhostPlaybackEngine.UpdateNonLoopingPlayback:744`, `ParsekKSC.cs:341/476/528`, and the preview path in `ParsekFlight.cs:8177`. The deferred-spawn-at-warp-end path in `ParsekPlaybackPolicy.ExecuteDeferredSpawns` (≈ `ParsekPlaybackPolicy.cs:143-179`) goes straight from `host.SpawnVesselOrChainTipFromPolicy(rec, i)` to `continue` without ever stepping the flag-event cursor, because the ghost for that recording never entered range (you were watching Learstar). Flags in the recording interval — which are "in the past" by the time deferred spawn runs — are silently dropped.

User-visible symptom: a flag planted during an EVA disappears from the world whenever the player time-warps past its recording while watching anything else. "Capsule and kerbal spawned but the flag didn't" is the exact report shape.

**Fix:** in `ParsekPlaybackPolicy.ExecuteDeferredSpawns`, after a successful `SpawnVesselOrChainTipFromPolicy` call, walk `rec.FlagEvents` and invoke `GhostVisualBuilder.SpawnFlagVessel(evt)` for every event with `evt.ut <= currentUT`, guarded by the existing `GhostPlaybackLogic.FlagExistsAtPosition` dedup. This mirrors the state-less fallback branch inside `ApplyFlagEvents` (`GhostPlaybackLogic.cs:1918-1924`) so no new invariant is added — the dedup helper already handles idempotent replays. Consider extracting a small `GhostPlaybackLogic.SpawnFlagVesselsForRecording(rec, currentUT)` helper so both paths share one implementation. Log a `[Verbose][Policy] Deferred flag flush: #N "rec" spawned K/N flags` summary so the fix is observable in playtest logs.

**Also verify during fix:** earlier in the same session, `00:09:08.031 [Scenario] Stripping future vessel 'a' (pid=1009931614, sit=LANDED) — not in quicksave whitelist` fires from `ParsekScenario.StripFuturePrelaunchVessels`. This is the rewind/quickload strip path (`Source/Parsek/ParsekScenario.cs:1490`). Confirm that flags planted during a committed recording are NOT treated as future-prelaunch vessels on quicksave round-trip — the whitelist-based strip predates flag support, so a fresh look at whether the planted-flag PID should be added to the whitelist (or filtered by type) would close a related observation. If a quickload can strip the flag before the deferred-spawn replay even runs, the main fix above does not cover that path.

**Files:** `Source/Parsek/ParsekPlaybackPolicy.cs` (deferred spawn flush + policy log), `Source/Parsek/GhostPlaybackLogic.cs` (shared flag replay helper), `Source/Parsek.Tests/DeferredSpawnTests.cs` (helper + policy-path regressions). Verification in this environment used `dotnet test --no-restore` for compile/build plus a direct reflection harness over the compiled `DeferredSpawnTests` methods because the standard `dotnet test` runner aborts here on local socket initialization (`SocketException 10106` before test execution starts).

**Scope:** Small-to-medium. Core fix is a 5-10 line loop in one method + one helper + one unit test. Strip-path verification is separate and may be a no-op if flags are already on the whitelist.

**Dependencies:** none (flag event capture + `SpawnFlagVessel` both already work).

**Resolution:** fixed in branch `fix-463-flagevents-deferred-spawn` on 2026-04-19. Deferred warp-end spawn flushes now call a shared `GhostPlaybackLogic.SpawnFlagVesselsUpToUT(...)` helper immediately after the real vessel/chain-tip spawn, reusing the existing dedup check and emitting a `[Verbose][Policy] Deferred flag flush ... spawned K/N flag(s)` line. The quickload-strip observation was reviewed separately: `StripFuturePrelaunchVessels` already strips any vessel PID that was not present in the rewind quicksave, so this patch intentionally leaves `ParsekScenario` unchanged; the main missing behaviour was the post-spawn replay of already-due flag events.

**Status:** CLOSED 2026-04-19. Priority was medium-high — fixed for v0.8.3.

---

## 462. ~~LedgerOrchestrator earnings reconciliation: MilestoneAchievement double-count vs FundsChanged~~

**Source:** `logs/2026-04-19_0014_investigate/KSP.log` (48 WARN lines across one session). Representative pair:

```
[WARN][LedgerOrchestrator] Earnings reconciliation (post-walk, funds): MilestoneAchievement id=Kerbin/SurfaceEVA expected=960.0, observed=1440.0 across 2 event(s) keyed 'Progression' at ut=17110.6 -- post-walk delta mismatch
[WARN][LedgerOrchestrator] Earnings reconciliation (funds): store delta=13920.0 vs ledger emitted delta=13440.0 — missing earning channel? window=[17076.5,17110.6]
```

**Concern:** post-walk funds reconciliation detects a systematic mismatch between the expected milestone award and the observed FundsChanged events for several stock milestones — `MilestoneAchievement id=` values hitting 1.5× the expected payout across 2 events (so every recalc is double-writing one of them), plus store-vs-ledger window mismatches where the full-window delta diverges by a stable offset. Seen for: `RecordsSpeed` (12×), `RecordsDistance` (12×), `Kerbin/SurfaceEVA` (6+3), `Kerbin/Landing` (6×), `Kerbin/FlagPlant` (6×), `FirstLaunch` (6×). All on the same test-career session at UT≈17110. Because the observed delta is higher than expected, funds accounting for these milestones is likely over-paying — the kind of bug that silently inflates funds over long play sessions and is very hard to spot without the reconciliation WARNs.

**Fix:** investigate `LedgerOrchestrator.RecalculateAndPatch` + `GameActions/KerbalsModule`-style earnings paths for milestone events. Two plausible causes: (1) milestone event being emitted twice into the ledger (once from the live progress event, once during recalc replay); (2) `Progression` channel key matching two distinct events in the reconciliation window (ambient FundsChanged from another source collapsed in). Add a test generator that reproduces the double-count for `RecordsSpeed` in `Source/Parsek.Tests/` (the milestone most obviously reproducible — it fires on every takeoff/landing in the test save). Cross-reference with the existing PR #307 follow-ups in `done/todo-and-known-bugs-v3.md` — that bundle already touched the `Progression` dedup key.

**Files:** likely `Source/Parsek/GameActions/LedgerOrchestrator.cs`, the earnings emit path for MilestoneAchievement, and whichever module owns MilestoneStore→FundsChanged conversion. Log snapshot saved under `logs/2026-04-19_0014_investigate/` for reproduction context.

**Scope:** Medium. Funds reconciliation is safety-critical (double-counted earnings invalidate career economies), but the WARN mechanism is already catching it — so the fix is localised to one emit path, not a schema redesign.

**Dependencies:** none.

**Status:** CLOSED. The apparent `MilestoneAchievement` "double-count" shape was the same post-walk attribution bug family as #477, not duplicate milestone credit landing in the ledger. The main fix shipped in #477; a follow-up hardening pass now also pins the mixed null-tagged/tagged ordering edge so legacy siblings cannot reclaim ownership of a tagged `Progression` burst just by appearing earlier in the ledger.

**Update (superseded by #477):** re-investigation in `logs/2026-04-19_0117_thorough-check/` showed the 2× / 3× / spurious-sci pattern is general across every milestone, not specific to `Kerbin/SurfaceEVA`. Final fix: not duplicate `MilestoneAchievement` action emission, but per-action attribution of a coalesced post-walk reward window. `ReconcilePostWalk` now compares/logs once per window and reports grouped ids, which closes the `Kerbin/SurfaceEVA` / `Records*` false-positive shape as well.

**Update (PR #405):** partial fix shipped — cross-recording `Progression` (and other keyed) events are now filtered out of both `ReconcileEarningsWindow` (commit path) and `CompareLeg` / `AggregatePostWalkWindow` (post-walk) by `recordingId`. That closed the original "2 events keyed 'Progression'" sibling-recording shape, but not the broader same-window attribution bug that #477 later fixed.

**Update (2026-04-19 follow-up):** `AggregatePostWalkWindow` now prefers tagged recording-scoped actions over null-tagged legacy siblings when choosing the primary owner of a mixed-scope window. That makes the `#405` partial fix order-independent: a null-tagged legacy row can still match tagged store events when it is alone, but it can no longer re-aggregate a tagged sibling's `Progression` delta if the ledger happens to enumerate the legacy row first. Added xUnit coverage for the `Kerbin/SurfaceEVA`-style mixed-scope repro.

---

## ~~461. Pin the #406 reuse post-frame visibility invariant with an in-game test~~

**Source:** clean-context Opus review of PR #394 (#406 ghost GameObject reuse across loop-cycle boundaries), finding #4.

**Concern:** the reuse orchestrator (`GhostPlaybackEngine.ReusePrimaryGhostAcrossCycle`) exits with `state.deferVisibilityUntilPlaybackSync == true` and `state.ghost.activeSelf == false` (set by `PrimeLoadedGhostForPlaybackUT.SetActive(false)`). Control then falls through to `UpdateLoopingPlayback:1161-1166`, where `ActivateGhostVisualsIfNeeded` clears both on the same frame before any render pass. A post-investigation trace confirmed this is invariant-equivalent to the pre-#406 destroy+spawn path, so no visual regression exists today — but NO test pins this control-flow ordering. A future refactor that adds an early `return` between `:1068` (the reuse call) and `:1166` (the activation) would silently hide the ghost for a frame on every cycle boundary.

**Fix:** shipped in `Source/Parsek/InGameTests/RuntimeTests.cs` as `Bug406_ReuseClearsDeferVisOnSameFrame` plus `Bug406_ReuseHiddenByZone_DoesNotActivateGhostOnSameFrame`. The coverage now drives the real `UpdatePlayback -> UpdateLoopingPlayback` cycle-boundary path through the live FLIGHT positioner, asserts that the same ghost GameObject instance survives the full frame, and pins the two post-frame outcomes that matter: visible branch clears `deferVisibilityUntilPlaybackSync` and re-activates the reused ghost on the same frame; hidden-by-zone branch keeps the reused ghost deferred/inactive until a later visible frame. It also asserts that zone rendering does NOT emit the `re-shown: entered visible distance tier` path while the ghost is still deferred, so the same-frame activation remains owned by `ActivateGhostVisualsIfNeeded`. xUnit still cannot observe `GameObject.activeSelf`, so this remains in-game-only coverage.

**Files:** `Source/Parsek/InGameTests/RuntimeTests.cs`.

**Scope:** Small. Regression-test coverage only; no production behaviour change was required after the invariant re-check.

**Dependencies:** #394 (#406 follow-up) merged.

**Status:** CLOSED. Priority: low. Landed on 2026-04-19 with the two runtime regressions above. User-visible impact remains none today; this is a guard against future refactors that would insert an early return between reuse and same-frame activation.

---

## ~~450. Per-spawn time budgeting / coroutine split — #414 follow-up for bimodal single-spawn cost~~

**Resolution (2026-04-19):** CLOSED. Phase B2 shipped for v0.8.3. `GhostVisualBuilder.BuildTimelineGhostFromSnapshot` now advances the expensive snapshot-part instantiation loop across multiple `UpdatePlayback` ticks via persisted `PendingGhostVisualBuild` state instead of monopolizing one frame. `GhostPlaybackEngine` gives each pending ghost a bounded per-frame timeline budget, preserves the correct first-spawn / loop / overlap lifecycle event until the build actually completes, and still forces immediate completion for explicit watch-mode loads. Unload / destroy paths now clear pending split-build state, and the overlap-primary path no longer allows hidden-tier prewarm to consume a second advance in the same frame. Coverage added: xUnit guard for pending-state cleanup plus an in-game regression that the incremental builder yields mid-build, resumes, and completes cleanly.

**Source:** smoke-test bundle `logs/2026-04-18_0221_v0.8.2-smoke/KSP.log:11489`. One-shot #414 breakdown line (first exceeded frame in the session):

```
Playback budget breakdown (one-shot, first exceeded frame):
total=40.1ms mainLoop=11.34ms
spawn=28.11ms (built=1 throttled=0 max=28.11ms)
destroy=0.00ms explosionCleanup=0.00ms deferredCreated=0.24ms (1 evts)
deferredCompleted=0.00ms observabilityCapture=0.43ms
trajectories=1 ghosts=0 warp=1x
```

**Concern:** #414's fix caps ghost spawns per frame at 2 via `GhostPlaybackEngine.MaxSpawnsPerFrame`, but this frame built exactly 1 ghost and that single spawn cost 28.11 ms — throttled=0, max=28.11 ms. This is the **bimodal cost distribution** #414 explicitly flagged as requiring a follow-up: "if max > ~10 ms we have a bimodal cost distribution that a count cap alone cannot cover, in which case the follow-up is per-spawn time budgeting or a coroutine split" (see #414 **Fix** section). The smoke test confirms the bimodal case is real on this save.

Breakdown of the exceeded frame: 70% of the budget (28.11 / 40.1 ms) lived inside a single `SpawnGhost` invocation. Candidates for the dominant per-spawn cost:
- `GhostVisualBuilder.BuildGhostVisuals` — part instantiation + engine FX size-boost pass (PR #316) + reentry material pre-warm.
- `PartLoader.getPartInfoByName` resolution for every unique part name in the ghost snapshot (cold PartLoader cache on first spawn of a given vessel type).
- Ghost rigidbody freeze + collider disable walk (`GhostVisualBuilder.ConfigureGhostPart`).

`mainLoop=11.34 ms` with `trajectories=1 ghosts=0` is also on the high side (expected ≤1 ms per trajectory on an established session), worth subtracting from the spawn cost attribution when a follow-up breakdown lands.

**Phase A (shipped):** diagnostic first. `PlaybackBudgetPhases` now carries an aggregate-and-heaviest-spawn breakdown of every `BuildGhostVisualsWithMetrics` call across four sub-phases (snapshot resolve, timeline-from-snapshot, dictionaries, reentry FX) plus a residual "other" bucket so `sum + other = spawnMax` reconciles. See `docs/dev/plan-450-build-breakdown.md`.

**Phase B branch decision — data from the 2026-04-18 playtest:**

```
heaviestSpawn[type=recording-start-snapshot
              snapshot=0.00ms timeline=15.90ms dicts=1.28ms reentry=6.94ms
              other=0.08ms total=24.20ms]
```

Timeline dominates (65.7 %) and reentry is a significant secondary contributor (28.7 %). Both B2 and B3 apply; B3 ships first (smaller blast radius), then B2 takes on the remaining `timeline` cost.

**Phase B3 (shipped):** lazy reentry FX pre-warm. Defers `GhostVisualBuilder.TryBuildReentryFx` from spawn time to the first frame the ghost is actually inside a body's atmosphere. `MaxLazyReentryBuildsPerFrame = 2` per-frame cap mirrors `MaxSpawnsPerFrame`. See `docs/dev/plan-450-b3-lazy-reentry.md`.

**Phase B2 (shipped):** coroutine split of `BuildTimelineGhostFromSnapshot`. Targets the dominant 15.90 ms timeline bucket that remained after B3. The snapshot-part loop now resumes from a persisted `PendingGhostVisualBuild` on later frames, with the dominant timeline work budgeted per advance instead of landing as one 15-18 ms single-spawn spike. Direct watch-mode loads still bypass the budget and complete synchronously on demand.

**Phase B1 (not planned):** the 15 ms latch threshold means #450's diagnostic only fires on bimodal cases, so the "spread across many spawns" case B1 targets is structurally out of scope of the evidence. #414's count cap already covers that pattern.

**Scope:** Phase B2 shipped as Medium (coroutine split, new invariants).

**Dependencies:** #414 shipped, Phase A shipped, Phase B3 shipped.

**Status:** CLOSED. Phase A, Phase B3, and Phase B2 shipped for v0.8.3. Priority was medium. The remaining bimodal single-spawn timeline cost is now spread across multiple frames instead of monopolizing one `UpdatePlayback` tick.

**Follow-up note:** B2 intentionally changes the diagnostics shape: the old `spawnMax >= 15 ms` one-shot WARN now sees several smaller per-advance samples instead of one large single-spawn spike. Re-validate that gate on the next post-B2 playtest so future heavy snapshot builds do not disappear from the WARN signal purely because the work is now chunked.

---

## 435. Multi-recording Gloops trees (main + debris + crew children, no vessel spawn)

**Source:** world-model conversation on #432 (2026-04-17). The aspirational design for Gloops: when the player records a Gloops flight that stages or EVAs, the capture produces a **tree of ghost-only recordings** — main + debris children + crew children — all flagged `IsGhostOnly`, all grouped under a per-flight Gloops parent in the Recordings Manager, and none of them spawning a real vessel at ghost-end. Structurally the same as the normal Parsek recording tree (decouple → debris background recording, EVA → linked crew child), with the ghost-only flag applied uniformly and the vessel-spawn-at-end path skipped.

**Guiding architectural principle:** per `docs/dev/gloops-recorder-design.md`, Gloops is on track to be extracted as a standalone mod on which Parsek will depend. Parsek's recorder and tree infrastructure will become the base that both Gloops and Parsek share — Gloops exposes the trajectory recorder + playback engine, Parsek layers the career-state / tree / DAG / world-presence envelope on top via the `IPlaybackTrajectory` boundary. Multi-recording Gloops must therefore **reuse Parsek's existing recorder, tree, and BackgroundRecorder infrastructure** rather than growing a parallel Gloops-flavored implementation. The ghost-only distinction is a per-recording flag on top of shared machinery, not a separate code path.

**Current state (audited 2026-04-17):**

- `gloopsRecorder` is a **parallel** `FlightRecorder` instance with no `ActiveTree` (`ParsekFlight.cs:7460`) — a temporary workaround that the extraction direction wants to retire.
- `BackgroundRecorder` is never initialized in the Gloops path — only alongside `activeTree` for normal recordings. Staging during a Gloops flight does not produce a debris child.
- `FlightRecorder.HandleVesselSwitchDuringRecording` auto-stops Gloops on any vessel switch (`FlightRecorder.cs:5143-5151`), so EVA does not produce a linked crew child either.
- `RecordingStore.CommitGloopsRecording` accepts a single `Recording`, adds it to the flat `"Gloops - Ghosts Only"` group (`RecordingStore.cs:394-418`). No `CommitGloopsTree`, no nested group structure.
- No conditional `IsGloopsMode` branch inside `RecordingTree`, no half-finished Gloops tree scaffolding.

**Net: Gloops is strictly single-recording by design today**, implemented as a parallel workaround. Multi-recording Gloops is a separate, sizable feature that should also consolidate Gloops onto the shared Parsek recorder (retire the parallel `gloopsRecorder` path).

**Desired behavior:**

- Gloops uses Parsek's main `FlightRecorder` + `RecordingTree` + `BackgroundRecorder` path, with a tree-level `IsGhostOnly` flag propagated to every leaf at commit. No parallel `gloopsRecorder`.
- Starting a Gloops recording creates a `RecordingTree` with the ghost-only flag; normal recording continues alongside on the same machinery if already active, or the tree operates solo if not. How the two modes interleave in the UI (explicit toggle, implicit based on UI state, etc.) is for the implementing PR to decide — possibly in coordination with a UI gate preventing concurrent career + Gloops capture.
- Staging during a Gloops flight → debris gets its own ghost-only recording via the normal `BackgroundRecorder` split path, with `IsGhostOnly = true` inherited from the tree.
- EVA during a Gloops flight → linked child ghost-only recording via the normal EVA split path.
- Commit: the whole Gloops tree flushes as a nested group under `"Gloops - Ghosts Only"` — e.g. `"Gloops - Ghosts Only / Mk3 Airshow Flight"` with child debris / crew recordings under it. Every leaf is `IsGhostOnly`.
- No vessel-spawn-at-end for any recording in a Gloops tree. `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` already gates on `!rec.IsGhostOnly` (see `GhostPlaybackLogic.cs:3001`); the tree case reuses this.
- Per-recording delete / regroup / rename in the Recordings Manager works the same as normal trees.
- Apply-side: #432's filter reads `rec.IsGhostOnly` per-recording, so every leaf in a Gloops tree is already excluded from the ledger with no extra work.

**Files likely to touch (sketch, not exhaustive):**

- `Source/Parsek/ParsekFlight.cs` — retire `gloopsRecorder` in favor of the main `recorder`/`activeTree` path; the "Start Gloops" action creates a tree flagged ghost-only. `CheckGloopsAutoStoppedByVesselSwitch` goes away or is folded into normal tree commit.
- `Source/Parsek/FlightRecorder.cs` — remove `IsGloopsMode` branches once the parallel recorder is retired; the recorder becomes agnostic to career semantics (aligning with the extraction boundary in `gloops-recorder-design.md`).
- `Source/Parsek/BackgroundRecorder.cs` — carry a tree-level ghost-only flag so debris children inherit it.
- `Source/Parsek/RecordingStore.cs` — collapse `CommitGloopsRecording` into the normal tree commit path; the ghost-only distinction is per-tree (or per-leaf, if partial-Gloops trees ever become a thing, which they shouldn't).
- `Source/Parsek/UI/GloopsRecorderUI.cs` — controls now drive the main recorder with a ghost-only flag rather than spinning up a parallel instance.
- `Source/Parsek.Tests/` — tree-structural tests for multi-recording Gloops capture and commit.

**Dependencies / sequencing:**

- Ships after #432 (which closes the existing single-recording leak and establishes the per-recording `IsGhostOnly` apply-side filter that multi-recording Gloops will rely on).
- Coordinates loosely with the Gloops extraction work (`docs/dev/gloops-recorder-design.md` Section 11 — the extraction sequence); ideally this consolidation happens before extraction so the extraction moves a single unified recorder, not two.
- Not tied to the deterministic-timeline correctness cluster — this is a feature extension, not a correctness bug.

**Out of scope:**

- Making Gloops spawn real vessels at ghost-end (explicitly not wanted — Gloops is visual-only).
- Turning the existing single-recording Gloops path into a tree retroactively for existing saves (beta, restart the save if you want the new behavior).
- Actually extracting Gloops into its own mod. That's covered by `docs/dev/gloops-recorder-design.md`'s extraction plan. #435 is a preparatory consolidation step on the Parsek side.

**Priority:** Medium. Feature extension + architectural cleanup. Worth scoping after #432 lands.

**Status:** TODO. Size: L. New feature — not a follow-up to anything shipped today.

---

## 430. "Why is this blocked?" explainer for the committed-action dialog

**Source:** follow-up on the "paradox communication" thread — currently when the player tries to re-research a tech or re-upgrade a facility that's already committed to a future timeline event, `CommittedActionDialog` pops up with a short "Blocked action: X — reason" message. The reason is generic and the player has no way to see *which* committed action is causing the block, or *when* it will play out.

**Desired behavior:**

- Replace the one-line reason with a structured block:
  - The action the player tried (e.g. "Research node: Heavier Rocketry").
  - The committed action that blocks it, including the source recording and its UT (e.g. "Already scheduled at UT 183420 in recording 'Mun Lander 3'").
  - A `Go to Timeline` button that opens the Timeline window and scrolls to the offending entry (reuses `TimelineWindowUI.ScrollToRecording`).
  - A `Revert to launch` shortcut if the player actually wants to undo it (routes to the existing rewind dialog pre-filled with the blocking recording).
- Keep the OK/close path unchanged so existing muscle memory still works.

**Why it matters:**

The mental model of "you can't do this because the timeline already did" is counter-intuitive for a first-time player. Showing the *which* and *when* turns a mysterious block into a debuggable constraint, reinforcing the ledger-as-truth principle every time a block fires.

**Files to touch:**

- `Source/Parsek/CommittedActionDialog.cs` — extend the dialog body; accept an optional `blockingRecordingId` + `blockingUT` + `blockingAction` tuple.
- `Source/Parsek/Patches/*Patch.cs` (where blocks are triggered for tech research / facility upgrade / part purchase) — pass the conflict context into the dialog instead of just the short reason string.
- `Source/Parsek/UI/TimelineWindowUI.cs` — already has `ScrollToRecording`; no changes beyond what's there.

**Out of scope for v1:**

- Auto-resolving the block by rewinding silently; this stays an informational dialog, not a one-click rewind.
- Collapsing multiple overlapping blocks into a summary (each block fires its own dialog as today).

**Status:** TODO. Size: S-M. Best quality-per-effort of the paradox-comms work.

---

## 428. Preview-rewind pane

**Source:** follow-up on the "cost-of-rewind is hard to intuit" thread. Rewind is the most consequential single action in Parsek — it moves the player back to a chosen launch point and replays forward with existing ghosts. But right now the rewind confirmation dialog shows a single summary line ("Rewind to 'Mun Lander 3' at Y1 D23?") and a raw count of "how many future recordings exist". A player can't tell before confirming: which exact recordings will be preserved, which will be replayed, which resources / contracts / milestones will be re-rolled, whether crew reservations will shift.

**Desired behavior:**

- Replace the existing one-line confirmation with a two-pane preview dialog anchored on the rewind button.
- Left pane: **"Before rewind point"** — committed recordings whose `EndUT <= rewindTargetUT` (stay intact on the ledger and their ledger effects remain applied); game-action milestones that already fired before the target; crew reservations that complete before the target.
- Right pane: **"Re-rolled forward"** — committed recordings whose `StartUT > rewindTargetUT` (they stay committed; their resource deltas + events re-apply from the target UT forward as the player plays); milestones pending at UT > target (they'll re-fire); crew reservations spanning the target (stand-in chain resets).
- Each pane shows a count + a preview list of the first ~5 items with `...and N more` if longer.
- Confirm / Cancel buttons unchanged.

**Why it matters:**

Rewind currently feels like a commitment to the unknown — the player isn't sure what they'll lose. Making the consequences legible before the dialog closes reduces regret and teaches the two buckets (before / re-rolled), which is the honest mental model: rewind is deterministic replay, nothing is thrown away.

**Files to touch:**

- `Source/Parsek/UI/RewindConfirmationUI.cs` (new or extension of the existing confirmation helper — current code is inlined in `RecordingsTableUI.ShowRewindConfirmation`).
- A `RewindPreview.Build(recordings, ledgerActions, milestones, rewindTargetUT, liveUT)` pure helper that classifies each item as "before rewind point" or "re-rolled forward". Lives next to `TimelineBuilder` since both walk similar data.
- Tests: classification helper fully covered (happy path + each bucket's edge cases + an item spanning the target UT).

**Out of scope for v1:**

- Previewing the new resource balance after rewind. Just show counts + first few items.
- Undo for rewind. One-way operation stays one-way.

**Status:** TODO. Size: M-L. Biggest UX win per dollar on the rewind mechanic.

---

## 427. Proactive paradox warnings surface

**Source:** follow-up on the conversation after shipping the Career State window. Today the mod prevents paradoxes mostly via blocks (action-blocked dialog) and a single red over-committed warning in the Timeline's resource footer. There's no centralized surface that says "your committed timeline has these N potential issues" — so a player can build up a career with, e.g., a contract that expires before its committed completion, or a facility upgrade requiring a level that won't be reached in time, and only discover the contradiction when it fires (or silently zeroes out).

**Desired behavior:**

- A **Warnings** badge on the main ParsekUI button row — hidden when count is 0, shown as `Warnings (N)` when any warning rules fire.
- Clicking opens a small scrollable window listing each warning as a row:
  - Category tag (`Contract`, `Facility`, `Strategy`, `Resource`, `Crew`).
  - One-line description (`Contract "Rescue Kerbal" deadline UT 240000 is before committed completion at UT 250000`).
  - `Go to ...` button linking to the relevant other window (Timeline scroll, Career State tab, etc.).
- Warnings are computed once per `OnTimelineDataChanged` fan-out (same cache-invalidation channel everything else uses).
- Starter rule set, each as a pure static helper in `WarningRules.cs`:
  - **ContractDeadlineMissed** — active contract's `DeadlineUT < terminal-UT of its committed completion recording`.
  - **FacilityLevelRequirement** — an action requires facility level N but the facility doesn't reach N until after that action's UT.
  - **StrategySlotOverflow** — projected active strategies > projected max slots (currently only warned in log, not UI).
  - **ContractSlotOverflow** — same for contracts.
  - **CrewDoubleBooking** — a stand-in appears in two chains at overlapping UT ranges.
  - **ResourceOverCommit** — already shown in Timeline budget footer, but also listed here for one-stop-shop.

**Why it matters:**

Action blocking catches paradoxes at the moment the player tries to violate them. Warnings catch *latent* contradictions that the ledger can detect but won't error on — the subtle ones where the ledger silently picks a resolution the player didn't intend (e.g. contract gets zeroed out because its deadline passed unexpectedly). Surfacing these early turns the mod's "structural paradox prevention" into a communicated design contract rather than a hidden invariant.

**Files to touch:**

- `Source/Parsek/UI/WarningsWindowUI.cs` — new scrollable list window.
- `Source/Parsek/WarningRules.cs` — new pure-static rule evaluators, one method per rule, each returning `List<Warning>` given `(ledger, recordings, modules)`. Heavy unit-test coverage.
- `Source/Parsek/ParsekUI.cs` — add the badge button + open toggle; integrate with `OnTimelineDataChanged` cache invalidation.
- `Source/Parsek.Tests/WarningRulesTests.cs` — one test per rule (happy + each flag condition).

**Out of scope for v1:**

- Auto-fix for any warning. Pure read-only surface.
- Severity levels / color-coding. All warnings are equal in v1; add severity in a follow-up if there are too many of one kind.
- Per-rule disable toggles. Playtesting can decide which rules feel noisy before we add knobs.

**Status:** TODO. Size: M. Complements the help popup (#426) — where help explains the system, warnings explain *your career's* specific issues. Together they turn the mod from "learn by experimenting" to "learn by seeing the model."

---

## 426. In-window help popups explaining each Parsek system

**Source:** follow-up conversation during the #416 UI polish pass. A player unfamiliar with the mod has to read `docs/user-guide.md` (out of the game) to understand what each window's sections and columns mean. The mechanics are specific enough (slots vs. stand-ins vs. reservations, per-recording fates, timeline tiers, resource budget semantics, etc.) that even tooltips-on-hover don't carry the full picture. An in-game help surface keeps the explanation next to the thing it explains.

**Desired behavior:**

- A small `?` icon button rendered in the title bar (or as the last button in the main toolbar row) of each Parsek window: Recordings, Timeline, Kerbals, Career State, Real Spawn Control, Gloops Flight Recorder, Settings.
- Clicking the `?` opens a small modal-ish popup window titled `Parsek - {Window} Help` anchored next to the parent window.
- The popup body is static help text tailored to that window. For tabbed windows (Kerbals, Career State), the help content should also cover each tab, either as one scrolling document or as a small tab-match sub-structure inside the popup. Keep each section brief (5-15 sentences) — the goal is orientation, not exhaustive docs.
- A "Close" button and `GUI.DragWindow()` so the popup can be moved.
- Help text can be hard-coded string constants in `Source/Parsek/UI/HelpContent/` (one file per window). No runtime load, no localization for v1.
- Suggested starter content:
  - **Recordings** — column-by-column walkthrough, L/R/FF/W/Hide button meanings, group vs chain vs ghost-only distinction.
  - **Timeline** — Overview vs Details tiers, Recordings/Actions/Events source toggles, time-range filter, resource-budget footer, loop toggle semantics on entry rows, GoTo cross-link.
  - **Kerbals** — slots vs stand-ins vs reservations (Roster State tab), chronological outcomes per kerbal (Mission Outcomes tab), outcome-click-scrolls-Timeline.
  - **Career State** — contracts / strategies / facilities / milestones tabs, current-vs-projected columns when the timeline holds pending recordings, Mission Control / Administration slot math.
  - **Real Spawn Control** — what it does (warp-to-vessel-spawn), State column, 500m proximity trigger.
  - **Gloops** — ghost-only manual recording, loop-by-default commit, X delete button in Recordings.
  - **Settings** — group-by-group overview (Recording, Looping, Ghosts, Diagnostics, Recorder Sample Density, Data Management); call out Auto-merge, Auto-launch, Camera cutoff, Show-ghosts-in-Tracking-Station.

**Out of scope for v1:**

- Inline tooltips on every sub-control (hover-tooltips already exist for a few buttons; expanding them is a separate follow-up).
- Localization / translation.
- Interactive tutorials.
- Search within help content.
- External hyperlinks (no browser launch from KSP IMGUI reliably).

**Files to touch:**

- New: `Source/Parsek/UI/HelpWindowUI.cs` (shared small popup window; takes a `windowKey` + body-text source).
- New: `Source/Parsek/UI/HelpContent/*.cs` (one static class per window, each exposes `public const string Body` or a `BuildBody()` method if dynamic content is needed later).
- Each existing window UI file (RecordingsTableUI, TimelineWindowUI, KerbalsWindowUI, CareerStateWindowUI, SpawnControlUI, GloopsRecorderUI, SettingsWindowUI): add a small `?` button and an `IsHelpOpen` toggle that feeds HelpWindowUI.
- `ParsekUI.cs`: add a single shared `HelpWindowUI` field + accessor so every window delegates to the same instance (only one popup open at a time).
- `CHANGELOG.md` entry under Unreleased.
- `docs/user-guide.md` can mention the new `?` buttons briefly but stays as the authoritative long-form reference.

**Status:** TODO. Size: M. Style it the same way as the rest of the mod (shared section headers, dark list box for paragraph groups, pressed toggle idiom if any sub-tabs appear).

---

## 160. Log spam: remaining sources after ComputeTotal removal

After removing ResourceBudget.ComputeTotal logging (52% of output), remaining spam sources:
- GhostVisual HIERARCHY/DIAG dumps (~344 lines per session, rate-limited per-key but burst on build)
- GhostVisual per-part cloning details (~370 lines)
- Flight "applied heat level Cold" (46 lines, logs no-change steady state)
- RecordingStore SerializeTrackSections per-recording verbose (184 lines)
- KSCSpawn "Spawn not needed" at INFO level (54 lines)
- BgRecorder CheckpointAllVessels checkpointed=0 at INFO (15 lines)

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

**Status:** Open

---

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

---

## TODO — Performance & Optimization

### T61. Continue Phase 11.5 recording storage shrink work

The first five storage slices are in place: representative fixture coverage, `v1` section-authoritative `.prec` sidecars, alias-mode ghost snapshot dedupe, header-dispatched binary `v2` `.prec` sidecars, exact sparse `v3` defaults for stable per-point body/career fields, and lossless header-dispatched `Deflate` compression for `_vessel.craft` / `_ghost.craft` snapshot sidecars with legacy-text fallback. Current builds also keep a default-on readable `.txt` mirror path for `.prec` / `_vessel.craft` / `_ghost.craft` so binary-comparison debugging can happen without unpacking the authoritative files first.

Remaining high-value work should stay measurement-gated and follow `docs/dev/done/plans/phase-11-5-recording-storage-optimization.md`:

- any further snapshot-side work now has to clear a higher bar: `.prec` and `_ghost.craft` are already roughly equal buckets after compression, and `_vessel.craft` is small, so "focus on snapshots next" only applies if a future corpus shifts the split back toward snapshots
- keep the readable mirror path strictly diagnostic: authoritative load/save stays on `.prec` / `.craft`, mirror failures stay non-fatal, and stale mirrors should continue to reconcile cleanly on flag changes
- only pursue intra-save snapshot dedupe or any custom binary snapshot schema if a future rebaseline against a larger / more vessel-heavy corpus shows a meaningful measured win
- additional sparse payload work only where exact reconstruction and real byte wins are proven
- post-commit, error-bounded trajectory thinning only after the format wins are re-measured
- snapshot-only hydration salvage must keep the loaded disk trajectory authoritative; if pending-tree data is used to heal bad snapshot sidecars, it should restore only snapshot state, not overwrite trajectory/timing with future in-memory data
- out-of-band `incrementEpoch=false` sidecar writes still rely on the existing `.sfs` epoch and staged per-file replacement; if we ever need crash-proof mixed-generation detection there, add a sidecar-set commit marker/manifest instead of pretending the current epoch gate can prove it
- any further snapshot-side work should preserve current alias semantics, keep the missing-only ghost fallback contract, keep partial-write rollback safety intact, and stay covered by sidecar/load diagnostics

**Priority:** Current Phase 11.5 follow-on work — measurement-gated guidance for future shrink work rather than active tasks

---

## TODO — Ghost Visuals

### T25. Fairing internal truss structure after jettison

After fairing jettison, the ghost currently shows just the payload and base adapter. KSP's real vessel can show an internal truss structure (Cap/Truss meshes controlled by `ModuleStructuralNodeToggle.showMesh`). The prefab meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can set correctly. A procedural truss mesh was attempted but removed due to insufficient visual quality.

Latest investigation: a second procedural-truss attempt was tested against fresh collected logs in `logs/2026-04-13_1529_fairing-truss-artifact`. The run correctly detected `FairingJettisoned` and rebuilt the ghost with `showMesh=True`, but the generated truss still looked bad in game: visible dark bars with transparent gaps following the fairing outline from base to tip. This confirms the simplified procedural replacement is still not shippable.

Important constraint: the current ghost snapshot is just a normal `ProtoVessel`/`ConfigNode` capture (`BackupVessel` output copied into `GhostVisualSnapshot`). That preserves fairing state such as `fsm`, `ModuleStructuralNodeToggle.showMesh`, and `XSECTION`, but it does not preserve the live runtime-generated stock Cap/Truss mesh deformation/material state from `ModuleProceduralFairing`. So the ghost cannot reproduce the exact stock truss visual from snapshot data alone.

To implement properly: prefer a stock-authoritative approach instead of another simplified procedural mesh. Most likely options are either capturing the live stock fairing truss render/mesh state at record time, or spawning/regenerating a hidden stock fairing from the snapshot and cloning the resulting stock truss renderers for the ghost. Only fall back to custom geometry if it can genuinely match stock quality.

**Status:** Open — do not revive the current simplified procedural-strip truss

**Priority:** Low — cosmetic, only visible briefly after fairing jettison

---

## TODO — Compatibility

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Last phase of roadmap — v1 targets stock only, mod compat is best-effort
