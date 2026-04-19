# Test Coverage Audit

Date: 2026-04-19
Repo worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-audit-test-coverage`
Branch: `audit-test-coverage-2026-04-19`

## Scope

This audit is a current-state snapshot of Parsek's testing surface across:

- headless xUnit tests in `Source/Parsek.Tests`
- in-game runtime tests in `Source/Parsek/InGameTests`
- log assertion and `KSP.log` contract validation
- manual playtest checklists in `docs/dev/manual-testing`

It is not a true line/branch coverage report because the repo does not currently have coverage instrumentation or exported coverage artifacts.

## Baseline Snapshot

### Automated suite status

- `dotnet test Source\Parsek.Tests\Parsek.Tests.csproj` passed locally in this worktree
- Result: `Passed: 7358, Skipped: 1, Total: 7359`
- The single skip is `GhostPlaybackEngineTests.SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT`, which is intentionally deferred to the in-game runtime suite because Unity `GameObject` construction cannot be exercised correctly in the headless xUnit runner
- The test run surfaced two xUnit analyzer warnings in `KerbalsWindowUITests.cs` (`xUnit2009`, substring assertions using `Assert.True`)

### Test surface size

- Production C# files: 148
- Production C# lines: 105121
- Test-project C# files: 306
- Test-project C# lines: 128473
- In-game runtime test methods discovered from `[InGameTest]`: 161
- Manual checklist docs currently present: `test-general.md`, `test-auto-record.md`

### Test harness structure

- Headless tests use xUnit on `net472`
- The test project references the real local KSP install (`Assembly-CSharp`, Unity modules) so it can exercise extracted logic that still depends on KSP data structures
- In-game runtime coverage lives in the production assembly under `Source/Parsek/InGameTests`
- `scripts/validate-ksp-log.ps1` turns the latest `KSP.log` into a gating xUnit check by setting `PARSEK_LIVE_VALIDATE_REQUIRED=1` and running `LiveKspLogValidationTests.ValidateLatestSession`

### Important limitation

There is no `coverlet`, collector, Cobertura/OpenCover export, or any other coverage-reporting pipeline in the repo today. That means we can say a lot about breadth, intent, and risk concentration, but we cannot answer "what percent of the code is covered?" in a mechanically defensible way yet.

## Historical Context

There are already several older audit artifacts in the repo, and they are helpful for interpreting the current state:

- `docs/dev/done/plans/task-32-test-audit.md` (`2026-03-25`) was a deep whole-suite audit of 110 test files. Its focus was mostly test hygiene: tautologies, duplicates, missing isolation, tests not calling production code, and weak log assertions. Importantly, the same doc says its P0-P2 cleanup pass was completed afterward.
- `docs/dev/done/plans/test-audit-v06.md` (`2026-04-03`) was a narrower follow-up audit on the `game-action-recording-redesign` test additions. It still found a smaller set of tautologies, duplicates, and isolation issues, but it explicitly described overall quality as good and said most of the ~2100 new tests were meaningful.
- `docs/dev/done/plans/task-13-tree-test-coverage.md` documents a focused push to add 18 non-vacuous tree-coverage tests plus synthetic tree builders. This helps explain why tree coverage is relatively mature today.
- `docs/dev/done/log-audit-2026-03-25.md` is not a test audit, but it explains why log validation became such a strong part of the repo's testing culture: logging volume, subsystem tagging quality, and observability were audited directly.

What that means for the current audit:

- older audits were primarily about suite quality cleanup
- the current audit is more about strategic coverage shape and missing instrumentation
- some classes of issues that were major in March and early April appear to have already been addressed by later cleanup passes

## What Is Strong Today

### 1. Headless coverage breadth is already unusually deep

The xUnit suite is not small or token. It is large and appears to be the default engineering safety net:

- broad regression coverage (`Bug*`, `Issue*`, `*Regression*`, `*Followup*`)
- core timeline/playback coverage (`Recording*`, `Ghost*`, `Spawn*`, `Rewind*`, `Chain*`, `Tree*`, `Watch*`)
- career-state coverage (`Ledger*`, `GameState*`, `Funds*`, `Science*`, `Reputation*`, `Facility*`, `Contract*`, `Milestone*`, `Kerbal*`, `Crew*`)
- UI logic coverage for several windows/tables (`CareerStateWindowUITests`, `KerbalsWindowUITests`, `RecordingsTableUITests`, `TimelineWindowUITests`, `SpawnWarningUITests`, `SelectiveSpawnUITests`)

Large, high-risk production files such as `ParsekFlight`, `GhostPlaybackLogic`, `RecordingStore`, `BackgroundRecorder`, `LedgerOrchestrator`, and `WatchModeController` are referenced by many tests. Coverage here is fragmented across extracted helpers and regressions rather than one-test-file-per-class, but it is real.

### 2. Log assertion testing is a first-class testing style in this repo

This codebase already uses log assertions aggressively:

- many tests install `ParsekLog.TestSinkForTesting`
- dedicated suites exist for logging and diagnostics (`ParsekLogTests`, `DiagnosticLoggingTests`, `ObservabilityLoggingTests`, `TreeLogVerificationTests`, `FxDiagnosticsTests`, `RecorderStateObservabilityTests`)
- runtime log contracts are validated both against fixtures and against the latest live `KSP.log`

This is a real strength. For a KSP/Unity mod where many bugs are timing-sensitive or scene-sensitive, log assertions are often the only cheap way to pin behavior without requiring a full playthrough.

### 3. There is meaningful in-game automation, not just manual testing

The runtime suite is substantial:

- 161 discovered `[InGameTest]` methods
- coverage categories include `GhostPlayback`, `GhostLifecycle`, `PartEventFX`, `CrewReservation`, `QuickloadResume`, `StrategyLifecycle`, `TerrainClearance`, `SaveLoad`, `LogContracts`, `Diagnostics`, `MapPresence`, `ResourceReconciliation`, `SpawnCollision`, and more
- destructive single-run tests are explicitly separated from batch-safe tests via `AllowBatchExecution = false`
- the in-game runner preserves per-scene history and exports `parsek-test-results.txt`, which is a solid foundation for repeatable playtest evidence

This is the right direction for a Unity/KSP mod. Some failure classes simply cannot be trusted in headless tests.

### 4. Manual checklists already focus on the correct user flows

The manual docs are not random smoke notes. They cover the real product surface:

- record -> revert -> merge -> ghost playback
- vessel persistence
- crew replacement and cleanup
- quicksave/quickload safety
- scene transitions
- record manager UI
- part event playback
- auto-record settings and behavior
- post-run log validation

That means the missing piece is not "what should we test?" so much as "which of these flows should be promoted into stronger automation?"

## Current Gaps And Risks

### 1. No mechanical coverage reporting

This is the biggest audit finding.

Without line/branch coverage output:

- there is no trustworthy percentage baseline
- there is no diff coverage for new changes
- there is no easy way to find dead zones except by heuristics
- regressions in coverage quality are hard to detect because the suite can grow while overall risk concentration gets worse

The repo currently has strong test volume but weak coverage observability.

### 2. Runtime-heavy UI surfaces still look under-automated

The thinnest direct automated coverage appears to be around IMGUI/UI wrapper surfaces and window plumbing, especially:

- `SettingsWindowUI`
- `SpawnControlUI`
- `GroupPickerUI`
- `TestRunnerUI`
- `CommittedActionDialog`
- `ParsekToolbarRegistration`

Important nuance: this does not mean these areas are completely untested. Some behavior is probably covered indirectly through broader tests or manual flows. But compared to the rest of the repo, these files have much weaker obvious ownership in automated tests.

Risk: UI regressions often survive because the business logic underneath is tested while the draw/input/state-sync layer is not.

### 3. Some large Unity/KSP-heavy files rely more on indirect coverage than direct seams

The files that look most expensive to break, but harder to cover precisely, include:

- `GhostVisualBuilder`
- `EngineFxBuilder`
- `TrajectorySidecarBinary`
- `SnapshotSidecarCodec`
- `ParsekPlaybackPolicy`
- `ParsekFlight`

Again, these are not uncovered. The issue is different:

- coverage is spread across many regressions rather than centered around explicit ownership
- some assertions are behavior-level only, not structure-level
- runtime-only paths still depend on in-game confidence and manual validation

That makes root-cause localization slower when one of these systems breaks.

### 4. In-game tests are strong on invariants, lighter on scripted end-to-end scenarios

The runtime suite does a good job checking state health and runtime invariants:

- ghost counts
- null/destroyed object leaks
- scene eligibility
- spawned vessel invariants
- data-health checks
- log contracts

What it still has less of is fully scripted scenario automation for the highest-value player flows:

- launch -> record -> revert -> merge -> playback -> spawn
- multi-scene round-trips with save/load and quickload
- crew replacement lifecycle across real mission flows
- watch-mode and map-presence flows during active playback
- full part-event playback scenarios with explicit before/after assertions

The manual checklist covers these flows already. They just are not encoded as aggressively as the lower-level invariant checks.

### 5. The test architecture docs are stale

`Source/README.md` still says "1342 tests total", while the current local run reports 7359 total tests. That is not a functional bug, but it is a process smell:

- the docs understate the current suite size
- readers will get the wrong picture of current confidence
- stale test docs usually mean stale test strategy docs too

### 6. Parallel/static-state risk is actively managed, but it is still a maintenance hazard

This suite leans heavily on static stores and global test overrides:

- `RecordingStore`
- `MilestoneStore`
- `GameStateStore`
- `ParsekLog`
- diagnostics singletons and clock overrides

The repo already uses many `[Collection("Sequential")]` test classes to contain this. That is the correct move, but it remains a quality risk because any new test that forgets the sequential guard or fails cleanup can create order-dependent failures.

This is also one place where the historical audits matter: both T32 and the v0.6 follow-up spent real effort calling out missing `Dispose()` and missing sequential collection guards. The current suite shape suggests that earlier cleanup work paid off, but the underlying risk remains structural because the architecture still relies heavily on shared static state.

## Coverage Assessment By Test Type

### Unit and extracted-logic tests

Assessment: strong

Reasons:

- broad coverage on pure or mostly-pure logic (`TrajectoryMath`, tree/chain logic, serialization, reconciliation, resource math, policy helpers, event classifiers)
- many historical bugs are permanently pinned as regressions
- the suite is fast enough to run locally and passed cleanly in this audit

Primary improvement need:

- make ownership more explicit for the highest-risk large files

### Log assertion tests

Assessment: strong and strategically valuable

Reasons:

- log assertions are already embedded in many tests
- dedicated logging/observability suites exist
- live `KSP.log` validation is scriptable
- manual docs already instruct developers to validate logs after scenarios

Primary improvement need:

- expand contract checks around the newest high-risk subsystems as they are added
- standardize which playtest logs are "must-pass" validation artifacts for releases

### In-game tests

Assessment: good foundation, not yet complete enough to replace manual scenario coverage

Reasons:

- 161 runtime tests is substantial
- categories cover many runtime-only bug classes
- batch-safe vs destructive-test separation is already designed in

Primary improvement need:

- promote the most critical manual flows into scripted, reproducible scenario tests

### Manual/in-game exploratory testing

Assessment: strong checklist quality, medium enforcement quality

Reasons:

- the checklists are specific and actionable
- they align with the actual product risks
- but they still depend on human discipline and do not generate coverage metrics

Primary improvement need:

- pair each high-value checklist area with an automated counterpart where feasible

## Highest-Value Next Steps

### Priority 1: Add real coverage instrumentation

Goal:

- produce machine-readable line/branch coverage for `Source/Parsek.Tests`
- establish a baseline report committed to CI or at least generated locally on demand

Why first:

- until this exists, every future audit will still be partly heuristic
- it becomes much easier to identify weak zones after this lands

Notes:

- because this is `net472` with KSP assembly references, coverage tooling should be validated carefully against the current build/test environment
- even a local-only first step is worth it if CI integration is not immediately convenient

### Priority 2: Add scenario-driven in-game tests for the core player loop

Suggested targets:

- record/revert/merge/playback happy path
- spawn/persist path for intact vessel
- destroyed-vessel merge-only path
- quicksave/quickload mid-playback
- crew replacement and cleanup end-to-end
- watch mode entry/exit and map-view presence around playback windows

Why:

- these are the highest-value behaviors in the product
- they are partially covered today, but split across unit tests, runtime invariants, logs, and manual checks

### Priority 3: Refactor UI windows toward testable presenters/helpers

Suggested first targets:

- `SettingsWindowUI`
- `SpawnControlUI`
- `GroupPickerUI`
- `TestRunnerUI`

Approach:

- keep the IMGUI draw layer thin
- extract text generation, selection state, sorting/filtering, button enable/disable rules, and status formatting into pure helpers
- cover those helpers headlessly

Why:

- this is the cleanest path to raising automated confidence on UI without pretending IMGUI itself can be fully unit-tested

### Priority 4: Give the runtime-heavy builders more explicit ownership tests

Suggested first targets:

- `EngineFxBuilder`
- `GhostVisualBuilder`
- `TrajectorySidecarBinary`
- `SnapshotSidecarCodec`

Approach:

- extract more pure transforms/selection logic into helper methods
- add fixture-driven round-trip tests
- keep the Unity object construction itself in the in-game suite

Why:

- these systems are costly when they fail and hard to debug from symptom-only regressions

### Priority 5: Tighten the release gate around logs and runtime evidence

Suggested changes:

- treat `scripts/validate-ksp-log.ps1` as part of the normal playtest closeout
- standardize a small set of named playtest scenarios whose logs must validate before release
- keep the exported `parsek-test-results.txt` with the relevant playtest bundle

Why:

- the repo already has the pieces, but the process signal can be stronger

### Priority 6: Clean up test-quality nits while touching the suite

Immediate low-cost items:

- fix the two current `xUnit2009` warnings in `KerbalsWindowUITests`
- keep enforcing sequential collections around tests that mutate shared static state
- update `Source/README.md` so the documented test strategy matches reality

## Concrete Improvement Backlog

If the goal is to raise testing quality and coverage without boiling the ocean, this is the sequence I would use:

1. Add coverage reporting for the existing xUnit suite.
2. Build a subsystem coverage matrix that maps production areas to headless tests, in-game tests, log validation, and manual scenarios.
3. Promote the core auto-record plus merge/revert player flows into scripted in-game tests.
4. Extract testable helpers from the thinly covered UI windows.
5. Add targeted ownership tests for `EngineFxBuilder`, `GhostVisualBuilder`, and sidecar codecs.
6. Make `validate-ksp-log.ps1` plus selected in-game result exports part of the normal release evidence bundle.

## Historical Follow-Up Triage

Quick source verification against the current tree shows that some of the older audit findings are now stale, while others still look worth carrying forward.

### Resolved or superseded since the older audits

- `ActionReplayTests` no longer appears to exist in the current test tree, so the old null-list gap is no longer a live target in its original form.
- `GetRecommendedAction` / `MergeDefault` were removed; `VesselPersistenceTests.cs` now explicitly says the old coverage moved to `ShouldSpawnAtRecordingEnd`, and the current suite has broad coverage for that logic across `RewindTimelineTests`, `SpawnSafetyNetTests`, `CommitFlowTests`, `GhostOnlyRecordingTests`, and related files.
- `DeserializeExtractedTests` now does assert deserialized rotation and velocity fields on trajectory points, so that specific T32 gap appears closed.
- `ScienceModuleTests` now implements `IDisposable`, and the old `RecalculationEngine.ClearModules()` cleanup concern appears to have been addressed.
- The tree-coverage push documented in `task-13-tree-test-coverage.md` appears to have landed; tree-specific pure, edge-case, log, and synthetic-builder coverage is much stronger than the March baseline.

### Closed in this audit worktree

- `GameActionSerializationTests` now has `[Collection("Sequential")]`, so that old parallel-state hygiene issue is closed locally.
- `BugFixTests.FindNextWatchTargetTests` now has explicit teardown instead of constructor-only cleanup.
- `RewindLoggingTests` now round-trip through `RecordingTree.SaveRecordingResourceAndState` / `LoadRecordingResourceAndState` instead of duplicated test-only helpers.
- `ParsekLogTests` now cover `Warn`, safe fallback formatting, and rate-limit reset behavior directly.
- `FlagEventTests` now cover the extracted happy-path crew-membership check.
- `GhostCommNetRelayTests` now cover the `antennaCombinableExponent = 0` edge case.
- `CompoundPartDataTests` now assert the `TryParseCompoundPartData(...)` return value in the two cases that previously ignored it.
- The leftover duplicate `InferCrewEndState_*` overlap in `KerbalReservationTests` has been removed.

### Still-live carryovers worth backlog time

- `KerbalsWindowUITests` still leaves two `xUnit2009` warnings in the suite; they are low severity but should be cleaned up the next time that file is touched.
- Mechanical coverage reporting is still not validated end-to-end. A local `coverlet.msbuild` + `scripts/test-coverage.ps1` scaffold now exists in this worktree, but the current machine cannot complete restore/test execution reliably enough to trust the numbers yet.
- The auto-record player flow is now materially better covered in this worktree: this audit adds helper-level xUnit coverage for launch gating and deferred EVA gating plus real single-run in-game launch and EVA auto-record scenarios. The remaining weakness is not the basic auto-start path anymore, but broader multi-step player flows like merge/revert and playback control.
- The real merge-dialog / revert-to-launch / discard player flows are covered strongly at the seam level (`MergeDialog`, `RecordingStore`, `ParsekScenario` helpers). This worktree now also has manual-only runtime coverage for both the synthetic pending-tree discard popup and the deferred `Merge to Timeline` commit path; the remaining gap is the full stock revert-to-launch transition with fresh live log evidence, not the dialog button branch itself.
- Playback control is no longer "missing automation" in the abstract: this worktree now includes a manual-only `Keep Vessel` canary that commits a synthetic timeline recording, fast-forwards into its window, asserts ghost activation, and asserts that the end-of-recording vessel spawn happens exactly once. The remaining gap is broader stock-behavior validation such as natural warp-stop and cross-scene duplicate-prevention evidence.
- Part-event playback is stronger than the older audits suggested: xUnit has broad structural/event coverage and in-game tests already verify live FX/buildability for engines, parachutes, lights, RCS, fairings, and deployables. The remaining gap is narrower: player-visible timing/assertion scenarios, not basic subsystem absence.

The remaining concerns are mostly strategic now, not cleanup nits: validated coverage reporting, a few real player-flow scenario tests, and tighter release evidence around logs/runtime exports.

## Continuation Plan

### Progress update

The first hygiene pass from this audit has now been completed in the audit worktree:

- added `[Collection("Sequential")]` to `GameActionSerializationTests`
- added explicit teardown to `BugFixTests.FindNextWatchTargetTests`
- removed the leftover duplicate `InferCrewEndState` coverage from `KerbalReservationTests`
- tightened the two `CompoundPartDataTests` cases that previously ignored the `TryParseCompoundPartData(...)` return value
- verified the full suite still passes locally after the cleanup

The next assertion-gap pass is now completed locally:

- extracted the `ShouldRecordFlagEvent` crew-membership check into a pure helper and added direct happy-path / null-entry / missing-name coverage for it
- added the `GhostCommNetRelay` exponent-zero edge-case test
- added direct `ParsekLog` coverage for `Warn`, safe fallback formatting, and rate-limit reset behavior
- verified those new tests directly against the built test assembly in-process

The production-path follow-up is also now completed locally:

- replaced the hand-written rewind-field serialization helpers in `RewindLoggingTests` with calls to the actual `RecordingTree.SaveRecordingResourceAndState` / `LoadRecordingResourceAndState` production helpers
- smoke-verified the rewritten rewind round-trip tests directly against the built test assembly in-process

The first coverage-instrumentation follow-up has also been prepared locally:

- added a local `scripts/test-coverage.ps1` runner and a `coverlet.msbuild` reference in the xUnit test project so the suite has a straightforward coverage path once restore/test execution is healthy again
- confirmed the changed test project still builds with `--no-restore`
- removed an experimental repo-local `NuGet.Config` after it failed to improve the restore blocker; it added noise without increasing confidence

The next-sequence auto-record follow-up has now started locally:

- extracted pure auto-record helpers from `ParsekFlight` for launch-transition gating and deferred EVA start gating
- added xUnit coverage for prelaunch starts, settled-LANDED starts, bounce suppression, inactive-vessel suppression, setting-disable gating, and deferred EVA start preconditions
- added a real single-run in-game test that stages a PRELAUNCH vessel, waits for launch auto-record to start, and asserts the launch auto-record log fires exactly once
- added a second real single-run in-game test that forces a stock EVA from a crewed vessel, waits for the deferred active-vessel switch, and asserts the EVA auto-record log fires exactly once
- added a live merge-dialog runtime smoke test that opens the real tree merge popup for a synthetic pending tree, verifies the button surface, and drives the actual `Discard` button callback through the UI object
- verified the modified test project still builds with `--no-restore`
- the remaining auto-record work is now mostly evidence quality: these runtime scenarios should be executed in a healthy KSP session and captured in `parsek-test-results.txt` / `KSP.log`

One environment caveat also appeared during this pass:

- the current machine's socket provider is broken for `dotnet test` / `TcpListener`, so I could still build the test project but I could not rerun the full xUnit suite from this session; the blocker is environmental, not a compile or assertion failure in the changed tests
- the current machine also fails `dotnet restore` inside NuGet initialization (`NuGet.Configuration.ConfigurationDefaults`, `path1` null), so the new coverage scaffold is staged but not yet validated end-to-end from this environment

Live runtime evidence from the `logs/2026-04-19_2126` bundle is now available too:

- `log-validation.txt` passed, so the session produced a clean `KSP.log` validation run
- the real `TreeMergeDialog_DiscardButton_ClearsPendingTree` runtime smoke test passed in both `KSP.log` and `Player.log`, with the expected popup creation and real `Discard` callback execution
- the two new auto-record runtime tests were still not exercised in that bundle because they are intentionally marked `AllowBatchExecution = false`; the exported `parsek-test-results.txt` shows them as `(never run)`, which is expected for `Run All`
- the broader runtime export still had unrelated pre-existing failures in that session (`TerminalOrbitBackfill_AlreadyPopulated_NoOverwrite`, two stock-strategy lifecycle tests, and `RuntimeTests.TimeScalePositive` in `SPACECENTER`), so that bundle is useful branch evidence for the new merge-dialog smoke path but not a globally clean in-game baseline yet

The later `logs/2026-04-19_2228` bundle closes the auto-record validation gap:

- `parsek-test-results.txt` captured exactly the two single-run `AutoRecord` tests in `FLIGHT`, and both passed: `AutoRecordOnLaunch_StartsExactlyOnce` and `AutoRecordOnEvaFromPad_StartsExactlyOnce`
- both `KSP.log` and `Player.log` show the real row-play execution and `PASSED` lines for those two tests, so the launch-from-pad and deferred EVA-from-pad paths now have live runtime evidence rather than only xUnit seam coverage
- that same bundle also exposed a separate observability issue: `log-validation.txt` failed `REC-001` because the latest-session validator did not see any Recorder line beginning with `Recording started`
- the root cause was not the recorder contract itself but the in-game assertion hook: those runtime tests were installing `ParsekLog.TestSinkForTesting`, which short-circuits `Debug.Log` and therefore swallowed the live recorder start line out of `KSP.log` / `Player.log` while still letting the tests assert on the captured line in-process
- this worktree now fixes that by adding a tee-style `ParsekLog.TestObserverForTesting` hook and moving the in-game log-capture sites to it, so live KSP logging should remain intact while runtime tests still capture lines for assertions; compile validation passed, but a fresh KSP run is still needed to confirm `REC-001` is gone in practice

The follow-up `logs/2026-04-19_2302_bridge-hang` bundle confirms that observer fix:

- `log-validation.txt` passed, so `REC-001` is no longer failing on the same single-run `AutoRecord` scenarios
- `parsek-test-results.txt` again captured exactly the two `AutoRecord` tests in `FLIGHT`, and both passed
- both `KSP.log` and `Player.log` now contain the real Recorder `Recording started: vessel=...` line for the launch and EVA auto-record runs, which confirms the live log file is no longer being muted by the in-game assertion hook
- that same session also captured a separate runtime problem after the auto-record rerun: `FlightIntegrationTests.BridgeSurvivesSceneTransition` started, but no pass/fail/export line followed for that test, and the tail of `Player.log` shows repeated stock `PauseMenu` `NullReferenceException` failures during revert-option button handling before the session tears down

The later `logs/2026-04-19_2332_fix-488-build` bundle closes that quickload/runtime blocker:

- `parsek-test-results.txt` captured all three `QuickloadResume` checks in `FLIGHT`, and all three passed: `BridgeSurvivesSceneTransition`, `Quickload_MidRecording_ResumesSameActiveRecordingId`, and `ReentrancyGuard_ClearedAfterRestore`
- both `KSP.log` and `Player.log` show the bridge canary and the mid-recording quickload-resume test reaching a real `PASSED` line after the scene transition
- the previous stock `FlightCamera` / `PauseMenu` `NullReferenceException` storm from `logs/2026-04-19_2302_bridge-hang` does not recur in the validated rerun, so the helper-side `FlightDriver.StartAndFocusVessel(...)` fix is now backed by live KSP evidence instead of only code review

On top of that, this worktree now has live runtime evidence for the two newer manual-only in-game canaries as well:

- the deferred merge-dialog test that invokes `ParsekScenario.ShowDeferredMergeDialog()`, presses `Merge to Timeline`, and asserts the pending tree is committed into `RecordingStore.CommittedTrees` / `CommittedRecordings`
- the synthetic `Keep Vessel` playback-control test that commits a one-recording tree, fast-forwards into its playback window, waits for a live ghost, and asserts the end-of-recording spawn materializes exactly once before cleanup

The direct live `Kerbal Space Program/KSP.log` + `parsek-test-results.txt` rerun at `2026-04-20 00:32` closes the second half of that gap:

- `parsek-test-results.txt` now records `FlightIntegrationTests.KeepVessel_FastForwardIntoPlayback_SpawnsExactlyOnce` as `FLIGHT PASSED (6132.6ms)`
- `KSP.log` shows two earlier `SKIPPED` rows because the session still had an active recording, then a clean single-run replay after the session returned to idle flight
- the validated run chose an off-pad synthetic endpoint (`padDist≈240m`), reached ghost playback end, executed the deferred landed spawn, and logged `Vessel spawn for #2 ... sit=LANDED`
- the final `Keep-vessel runtime: ... spawnedPid=...` line and `PASSED:` row confirm the single-spawn assertion held after the cleanup wait, so the manual-only playback-control canary is now backed by live KSP evidence rather than only compile validation

This worktree now also has a new manual-only stock revert canary in code: it starts a real recording, stages the active vessel, calls stock `FlightDriver.RevertToLaunch`, and asserts the shipped #434 behavior (`pending tree soft-unstashed, no Parsek merge dialog, no timeline commit`). That scenario still needs live KSP validation.

### Recommended next sequence

From here, I would continue with one structural pass, but with a tighter order than the earlier draft:

1. **Finish validating the local coverage path**
   Keep the new local coverage scaffold, but do not treat it as done until `dotnet restore` and `dotnet test` are healthy on a non-broken machine/account. The next useful output is a real baseline report, not more tooling churn.
2. **Live-validate the stock revert soft-unstash flow**
   The next runtime canary in this worktree no longer assumes a revert-time merge dialog. It starts a real recording, crosses the actual stock `Revert to Launch` boundary, and asserts the shipped `soft-unstash / no merge / no commit` semantics in live KSP.
3. **Then add the non-revert scene-exit merge flow**
   The remaining merge-dialog gap is now the real non-revert exit path: `record -> leave flight without revert -> deferred merge dialog -> commit/discard`. That is the user-facing route that still owns merge UI after #434.
4. **After that, add part-event timing scenarios**
   These are still valuable, but the repo already has stronger structural coverage here than it does for auto-record and merge/revert.

### Scenario promotion shortlist

The first scripted runtime scenarios I would add are:

1. **Stock revert soft-unstash / no-merge flow**
   Add or validate one runtime scenario that starts from a real recording, crosses the actual `Revert to Launch` scene boundary, and proves the pending tree is soft-unstashed without a merge popup or timeline commit.
2. **Non-revert scene-exit deferred merge flow**
   Add one runtime scenario that starts from a real recording, exits flight without revert, waits for the deferred merge dialog in the destination scene, and then commits or discards through the real UI.
3. **Part-event timing showcase**
   Reuse the existing synthetic showcase content to assert at least one live timing-sensitive transition end-to-end: e.g. lights toggle, gear deploys, fairing disappears, or RCS FX emits near the authored timestamps.

That sequence matches the actual current gap profile better than the older "quickload/revert first" assumption. Quickload, scene-exit finalize, crew replacement placement, ghost visual buildability, and part-event FX presence already have materially more automated coverage than the historical audits implied, and `#488` now has live validation rather than being an open blocker.

## Bottom Line

Parsek does not have a testing problem in the usual sense of "there are not enough tests." The repo already has a large and serious test suite.

The real gaps are:

- missing mechanical coverage reporting
- weaker direct automation around UI shells and some Unity-heavy builders
- not enough scripted end-to-end in-game scenarios for the top user flows

If we fix those three areas, overall confidence should rise materially without needing a wholesale rewrite of the existing test strategy.
