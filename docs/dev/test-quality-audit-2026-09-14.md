# Test Quality Audit - 2026-09-14

Baseline SHA `4aedb0a1a` (`4aedb0a1a018bd3420081778cffc1337bb6a58aa`, pinned; every finding cites this SHA).
Dates: Phase 0-1 opened 2026-09-14; Phases 1, 2, 3 and 4 closed 2026-09-15.
Scope: `Source/Parsek.Tests` xUnit unit tests only. `InGameTests` and the harness Python suites are
cross-referenced only where they change a verdict on a unit test.

Status: Phase A complete; Phase B go-ahead given by the operator on 2026-09-15 (first wave: the 7 High in `testfix-t1t2`, the six mechanical gate repairs in `testfix-t4-flaky`).
Phase B progress: the 7 High are FIXED on `testfix-t1t2` (2026-09-15) - see the "Phase B" column of the High table; every fix carries a re-run mutation proof under `research/.../mutations/` (`<id>-phaseB.patch`, `mutations.csv`).

Plan: `docs/dev/plans/test-quality-audit.md`. Raw and derived evidence:
`docs/dev/research/test-quality-audit-2026-09-14/`.

## What this supersedes, and what it does not

This is a dated SNAPSHOT of xUnit-internal quality, not a living status doc. It answers "are the
existing ~23.4k executed cases sound, and where is added coverage worth it".

- It supersedes `test-coverage-audit-2026-07-29.md` ONLY for that question. The July audit remains
  the authority for stack-level coverage across the three testing systems (xUnit, `InGameTests`,
  the flight harness), for in-game category execution, and for harness dimension coverage.
- It does not supersede `docs/dev/autotest-status.md`, which stays the SINGLE status authority for
  the automated-testing system. This audit is registered there as a snapshot row only.
- It does not change `design-testing-unified.md`'s contracts; that doc's derivation line names both
  audits.
- Where a finding here re-finds a July register item, the JULY ID stays primary and this audit adds
  new evidence; the mapping lives in `research/test-quality-audit-2026-09-14/july-crosswalk.csv` and
  in the "July crosswalk" section below.
- No production code and no test code was edited in Phase A. Nothing in this document had been
  fixed when it was written; Phase B fixes are recorded per row, not by rewriting the findings.

## Method

### Rubric

The review rubric was frozen before Phase 1 (`research/test-quality-audit-2026-09-14/rubric.md`) and
embedded verbatim in every agent prompt.

Verdict vocabulary, one per test method: `ok`, `weak`, `vacuous`, `duplicate`, `brittle`,
`misleading`, `organization`, `source-gate-ok`, `source-gate-misplaced`, `fixture-limited`,
`uncertain`.

Finding categories: T1 vacuous/cannot-fail, T2 redundant/duplicate, T3 weak/misleading,
T4 brittle/flaky, T5 coverage opportunity, T6 organization, T7 production bug.

Severity: `Critical` = a realistic regression in save/sidecar/ledger/rewind data-destruction or
career-corruption paths leaves the suite green. `High` = the same for recording
integrity/playback/chain/terminal-state correctness. `Medium` = wrong branch pinned, over-mocked,
misleading name, order-dependent flake risk, missing negative/mirror case. `Low` = organization,
naming, duplication cost only.

Every non-`ok` verdict and every finding carries a falsifiability string of the exact shape
`falsifiability: <SUT file:line>; <production change> -> red`, with three named exemptions
(`sut=test-infrastructure`, `sut=wiring-gate`, `sut=multi-line integration`). A finding without one
is dropped by the linter.

Two rubric rules kept the register honest and are worth restating:

- Weak vs vacuous boundary: if stubbing or removing the production line the test NAMES leaves the
  test green, the verdict is `vacuous`, not `weak`. `weak` means the line IS exercised but the
  assertion window is narrow.
- Source-text gate acceptance: a source-scan test is `source-gate-ok` unless it is the SOLE coverage
  for the behavior, a behavioral test was feasible and skipped, or it regexes source text where an
  AST walk is required. Only then `source-gate-misplaced` (T4). This kept roughly 100 wiring-gate
  files out of the register as bogus T4s; 240 methods landed `source-gate-ok` and 24
  `source-gate-misplaced`.

### Tiers and batching

All 1,010 test files got an agent pass; depth was tiered by file size.

| Tier | Size | Files | Methods | Batch budget | ok rate |
|---|---|---|---|---|---|
| A | > 32 KB | 220 | 10,377 | 1-2 files, or one method range for > 128 KB | 93.7% |
| B | 8-32 KB | 520 | 8,329 | 4-6 files | 93.4% |
| C | 2-8 KB | 246 | 1,471 | 8-12 files | 93.1% |
| D | < 2 KB | 24 | 48 | mechanical plus spot check | 95.8% |

The ok rate is flat across tiers (93.1-95.8%), which is evidence that the tiering did not trade
depth for a rosier verdict in the cheap tiers.

Clusters were ordered risk-first: `rewind-refly`, `recording-tree`, `ledger-career`,
`recorder-events`, `logistics-route`, `spawn-vessel`, `ghost-playback`, `map-render`,
`trajectory-orbit`, `mission-groups`, `analyzer`, `logging`, `harness-seam`, `wiring-gates`,
`ui-settings`, `io-serialization`, `legacy-bugfix`, `catchall`.

### Agent protocol

- Review agents ran on Opus 5 at High effort, 4 in parallel, 21 rounds
  (`research/.../work/metrics.md`). The session (Fable 5.1) supervised, adjudicated, and did the
  Phase 4 synthesis. Review agents never ran tests and never ran git.
- Each batch produced one `findings/<cluster>-<batch>.jsonl` fragment: one record per test method,
  plus finding and coverage records. Agents emitted JSONL only; no agent hand-edited a CSV.
- `tools/lint_fragments.py` validated every fragment against the batch manifest: every method in the
  slice present exactly once, enums valid, falsifiability present on every actionable verdict, IDs
  unique. Agents self-linted and pasted the PASS line before returning. No finding entered D2 except
  through a linted fragment.
- `tools/merge_fragments.py` is the only writer of `test-inventory.csv`.
- 427 fragments (426 manifest batches plus `supplement-001` for the 7 TRX-only methods the scanner
  missed); 0 linter rejections at return, one pre-existing failure repaired by the supervisor
  (`ledger-career-034`, two rows missing the `->` token).

### Calibration result

Two pilot batches were reviewed independently three times each before the fan-out
(`research/.../calibration-2026-09-14.md`).

| Pilot batch | Methods | r1 vs r2 | r1 vs r3 | r2 vs r3 |
|---|---|---|---|---|
| recording-tree-032 (`EffectiveStateTests.cs`) | 39 | 94.9% | 92.3% | 92.3% |
| ghost-playback-024 (6 files) | 51 | 86.3% | 84.3% | 96.1% |

Gate was >= 80% pairwise exact agreement: PASS. All three runs independently found the same core
defects in both batches; the divergences were label choices, not missed defects. Two protocol
repairs came out of it (the weak/vacuous boundary rule above, and three linter over-rejections).
Known residue: on 39-method batches the miss rate on MEDIUM findings is about 10-20%, so the Medium
register is a floor, not a census.

### Phase 2 mutation protocol

- Every High finding got a second agent trying to REFUTE it, plus a bounded mutation.
- Mutations ran only in a detached scratch worktree `../Parsek-testaudit-mut` at the pinned SHA;
  `git status --porcelain` empty before each run and after `git restore`; never in the audit
  worktree, never committed. Patches are committed as evidence under `mutations/`.
- One filtered class per run (`dotnet test ... --filter "FullyQualifiedName~<Class>"`), never the
  full suite. Per-mutation row in `mutations/mutations.csv`: finding id, sha, file, symbol,
  mutation kind, patch path, baseline result, exact command, observed result, red test, restore
  command, restored-clean flag.
- Per-category recipe: T1 = stub or negate the production line the test's name claims (GREEN means
  the finding holds). T2 = one mutation, record which of the pair reds. T3 = a realistic semantic
  change staying green. T4 = rerun evidence, no mutation.
- Cap was 30 mutations; 7 were used (the 7 High). The seeded 10% Medium sample (27 findings) was
  verified read-only.

### What was NOT done

- No production edits and no test edits. This audit changed no `.cs` file.
- No full-suite rerun after Phase 0. The Phase 0 baseline is the only full run:
  Failed 0 / Passed 23,420 / Skipped 1, wall 1m09s (`work/baseline.trx`); coverlet line 45.75%,
  branch 47.4%, method 60.68% (`work/coverage.cobertura.xml`). Coverage is a diagnostic here, never
  a target.
- No mutation coverage of Medium or Low findings beyond the 27-finding sample; the remaining 245
  Medium and 489 Low rows are `unverified`.
- No T7 verification. Production observations are recorded below as leads only.

## D1 - test register summary

`test-inventory.csv`, 20,225 rows, one per test method. Do not list it in a document; aggregate it.
The row count equals the TRX executed-method count exactly. 1,010 test files; 974 of them declare
test methods (the other 36 are generators, fixtures and helper classes with zero `[Fact]`/`[Theory]`,
13 of them under `Generators/`). 1,055 test classes. 19,436 `[Fact]` + 789 `[Theory]` rows
(23,421 executed cases after theory expansion). 680 of 1,010 files carry `[Collection("Sequential")]`;
100 files read production source text.

### Verdict totals

| Verdict | Methods | Share |
|---|---|---|
| ok | 18,922 | 93.6% |
| duplicate | 348 | 1.7% |
| vacuous | 258 | 1.3% |
| source-gate-ok | 240 | 1.2% |
| weak | 216 | 1.1% |
| misleading | 181 | 0.9% |
| source-gate-misplaced | 24 | 0.1% |
| fixture-limited | 18 | 0.1% |
| organization | 11 | 0.1% |
| brittle | 7 | < 0.1% |
| **Total** | **20,225** | |

`uncertain` was available to Tier D mechanical rows and was used zero times: every row got a real
verdict.

### Per cluster

| Cluster | Methods | ok | non-ok | of which source-gate-ok |
|---|---|---|---|---|
| catchall | 3,044 | 2,907 | 137 | 34 |
| recording-tree | 2,340 | 2,093 | 247 | 45 |
| logistics-route | 1,871 | 1,813 | 58 | 5 |
| ledger-career | 1,853 | 1,747 | 106 | 16 |
| recorder-events | 1,489 | 1,363 | 126 | 9 |
| ghost-playback | 1,449 | 1,356 | 93 | 22 |
| map-render | 1,357 | 1,246 | 111 | 39 |
| legacy-bugfix | 1,255 | 1,159 | 96 | 5 |
| harness-seam | 1,126 | 1,102 | 24 | 0 |
| spawn-vessel | 977 | 918 | 59 | 1 |
| rewind-refly | 919 | 857 | 62 | 5 |
| trajectory-orbit | 819 | 767 | 52 | 0 |
| mission-groups | 735 | 708 | 27 | 3 |
| ui-settings | 217 | 207 | 10 | 0 |
| analyzer | 213 | 206 | 7 | 0 |
| wiring-gates | 204 | 142 | 62 | 55 |
| logging | 186 | 170 | 16 | 1 |
| io-serialization | 164 | 156 | 8 | 0 |
| supplement | 7 | 5 | 2 | 0 |

`wiring-gates` reads as the worst cluster at 70% ok, but 55 of its 62 non-ok rows are
`source-gate-ok`, which is an accepted verdict and carries no action. Corrected for that, the
worst clusters by actionable non-ok rate are `recording-tree` (8.6%), `recorder-events` (7.9%) and
`legacy-bugfix` (7.3%).

## D2 - issue register

Full register: `research/test-quality-audit-2026-09-14/issue-register.csv` (768 rows; columns
`id, file, line, method, category, severity, confidence, falsifiability, what_it_asserts, why_weak,
proposed_action, effort, july_ref`). Confidence is `high` on 744 rows and `medium` on 24; none is
`low`. Effort is `S` on 691 rows and `M` on 77; none is `L`.

### Totals by severity and category

| | T1 vacuous | T2 duplicate | T3 weak/misleading | T4 brittle | T6 organization | Total |
|---|---|---|---|---|---|---|
| Critical | 0 | 0 | 0 | 0 | 0 | **0** |
| High | 7 | 0 | 0 | 0 | 0 | **7** |
| Medium | 114 | 1 | 138 | 19 | 0 | **272** |
| Low | 64 | 231 | 183 | 6 | 5 | **489** |
| **Total** | **185** | **232** | **321** | **25** | **5** | **768** |

There is no Critical finding: no save/sidecar/ledger/rewind data-destruction or career-corruption
path was found where a realistic regression leaves the suite green AND no other cell covers it.

Phase 1 reported Medium 273 / Low 488; the difference is the single Phase 2 downgrade
(F-recording-tree-003-02, below). The CSV carries the post-adjudication numbers.

### The 7 High findings, with Phase 2 mutation evidence

All seven are T1, all seven were supervisor-confirmed on reading, and all seven were then VERIFIED
by mutation: the mutation the finding names left the whole filtered class green at the baseline pass
count, so none of these tests can red on the production regression it is named for. Patches:
`mutations/<id>.patch`; run rows: `mutations/mutations.csv`.

| Finding | Test | SUT the name claims | Mutation applied | Result | Phase B |
|---|---|---|---|---|---|
| F-recorder-events-003-01 | `PartEventTests.cs:2493` `RemoveDuplicateCrew_RemovesDuplicate_LogsWarning` | `VesselSpawner.RemoveDuplicateCrewFromSnapshot` (`VesselSpawner.cs:3265-3297`) | stub the method body to an immediate return | 164/164 green | FIXED (`testfix-t1t2`): `VesselSpawner.RemoveDuplicateCrewFromSnapshotCore` extracted; both cells call it |
| F-recorder-events-013-01 | `BackgroundSplitTests.cs:1033` `Bug285_ParentDead_NoContinuation_TreeHasOnlyChildren` (+3 sibling cells) | `BackgroundRecorder.HandleBackgroundVesselSplit` (`BackgroundRecorder.cs:602-760`) | make the bug-285 parent continuation unconditional | 58/58 green | FIXED (`testfix-t1t2`): `BackgroundRecorder.TryBuildAndAttachParentContinuation` extracted; all four cells call it |
| F-recorder-events-024-01 | `BackgroundPartEventAuditTests.cs:94` `PollPartEvents_CoversAllPolledEventTypes_MatchingFlightRecorder` | `BackgroundRecorder.PollPartEvents` (`BackgroundRecorder.PartEventPolling.cs:91-109`) | comment out all 19 `Check*State` calls | 17/17 green | FIXED (`testfix-t1t2`): IL call-set equality vs `FlightRecorder.PollPartStates` (`ILCallSet`) |
| F-recording-tree-050-04 | `CommittedRecordingImmutabilityTests.cs:90` `ContinuationVesselDestroyed_PreservesVesselSnapshot` | `ParsekFlight` destroy handler (`ParsekFlight.cs:3311-3324`) | reintroduce `contRec.VesselSnapshot = null` | 12/12 green | FIXED (`testfix-t1t2`): `ParsekFlight.MarkContinuationVesselDestroyed` extracted and BOTH mirrored destroy branches (chain continuation and undock continuation) route through it; one cell calls it, a second IL-gates both call sites against an inline flag write |
| F-recording-tree-050-07 | `CommittedRecordingImmutabilityTests.cs:151` `EvaBoardingContinuationStop_PreservesVesselSnapshot` | `ChainSegmentManager.CommitChainSegment` EVA-boarding branch (`ChainSegmentManager.cs:822-834`) | reintroduce `rec.VesselSnapshot = null` | 12/12 green | FIXED (`testfix-t1t2`): `ChainSegmentManager.ApplyBoardingContinuationStop` extracted; the cell calls it |
| F-spawn-vessel-004-01 | `IdentityLossClassifierTests.cs:379` `ActiveRootBackgrounded_FlushForwardsControllers_AllowingIdentityLossOverride` | `FlightRecorder.StartRecording` controllers backstop (`FlightRecorder.cs:6983`) | short-circuit the `AdoptControllersIfEmpty` backstop | 40/40 green | FIXED (`testfix-t1t2`): `FlightRecorder.ApplyStartRecordingTreeBackstop` extracted; the cell drives the backstop |
| F-trajectory-orbit-015-01 | `RelativeRecordingTests.cs:185` `RecorderContract_LiveAnchorPositionMustMatchPlaybackAnchorPosition` | `FlightRecorder` anchor-pose producer (`TryResolveLiveAnchorPose`, `FlightRecorder.cs:9512-9515`; `:9334-9338` is the consumer that computes the offset from it) | feed the anchor pose from `GetWorldPos3D` (CoM), i.e. the shipped drift bug | 6/6 green | FIXED (`testfix-t1t2`): new IL gate on the anchor-pose producer, covering the CoM FIELD reads (`Vessel.CoM`/`CoMD`, ldfld) as well as `GetWorldPos3D`; the math cell renamed |

Read them together and the shape is one shape: the test performs the production work itself. Five
replay a production branch inline (the dedup loop, the split tree mutations, the controllers
forward, the anchor-pose identity); two assert fixture state the test just wrote. Two of the seven
name a bug that actually shipped once - the ~10 m relative-debris drift (F-trajectory-orbit-015-01)
and the bug-95 snapshot-nulling that kills respawn after revert (F-recording-tree-050-04) - so the
re-regression these cells exist to prevent is exactly what would slip through today.

Proposed actions, in the register: three want a pure helper extracted so the decision becomes
callable (`RemoveDuplicateCrewFromSnapshot`'s PART-node loop, the split continuation decision, the
`StartRecording` forward); one wants an AST/IL call-set walk instead of a reflection existence check
(F-recorder-events-024-01); one wants the `AnchorPose` producer pinned through the existing seam;
two want deletion or a move to `InGameTests` because a fixture-only cell cannot witness the contract
(F-recording-tree-050-04 / -07).

### Top 20 Medium

Selection rule: risk class first (data-loss > career/ledger > recording > playback > other), risk
derived from the PRODUCTION file named in the finding's falsifiability string (falling back to the
test file), then category (T1 before T3 before T4 before T2), then confidence, then effort; capped
at 2 rows per test file so a single mega-file cannot fill the list. All 20 land in the data-loss
class, which has 27 Medium rows in total; the remaining risk classes are career 52, recording 67,
playback 52, other 74.

| # | id | Test (file:line, method) | What is wrong | Proposed action | Effort |
|---|---|---|---|---|---|
| 1 | F-rewind-refly-010-05 | `RewindTimelineTests.cs:1039` `MarkRewoundTreeRecordingsAsGhostOnly_StandaloneRecording_MarksByPidMatch` | `RewindReplayTargetRecordingId` equals the recording id, so `ShouldApplyRewindSpawnSuppression` returns at the id-equality branch (`ParsekScenario.cs:7011-7016`); the pid-match fallback the name claims is never reached | null the replay-target id so only the pid/tree-null fallback can mark | S |
| 2 | F-rewind-refly-018-01 | `ReFlyPreservedFleetGuardTests.cs:321` `ResolveDisabledSlotVesselsToStrip_SelectedSlotIsNeverACandidate` | the fixture cannot reach the kill path at all: the recording carries neither guid nor spawned pid, so both match arms at `RewindInvoker.cs:1831-1835` reject pid 9 with or without the skip | give the recording a guid and pass the same guid on the live entry | S |
| 3 | F-catchall-006-02 | `AtomicMarkerWriteTests.cs:526` `Phase1And2_NoOnSaveBetween` | the subscription is try/catch guarded and skipped when `GameEvents.onGameStateSave` is null, and `AtomicMarkerWrite` raises no save event, so the invariant is unfalsifiable; the rest duplicates line 404 | drop, or re-target at an observable seam (a `RecordingStore` save / `StateVersion` counter) | S |
| 4 | F-recording-tree-011-03 | `QuickloadResumeTests.cs:2394` `HasOrphanedLimboTree_LimboStashedButNotRestoredFromSave_IsTrue_Bug300` | the predicate is computed at `ParsekScenario.cs:3449` and read only by a debug log at :3471; the cells duplicate it rather than call it | extract `hasOrphanedLimboTree` into an internal static and assert that, or rename the cells | S |
| 5 | F-recording-tree-039-01 | `DiscardSidecarReapTests.cs:477` `Reap_AlsoUnlinksTheQuickloadResumeSave_ButNeverARewindPointQuicksave` | both file outcomes come from the test's staged deleter, not production; the eighth limb of `RecordingStore.DeleteRecordingFiles` has no behavioral coverage anywhere in the suite | drive the real `DeleteRecordingFiles` against a temp save root (add a save-root seam) | S |
| 6 | F-recording-tree-046-06 | `SwitchSegmentSuppressionNarrowingTests.cs:352` `ShouldDeferPendingEventMilestoneFlush_AllMarkerOwned_ReturnsFalse` | deleting the marker-owned skip at `ParsekScenario.cs:2402-2406` leaves the cell green: the segment id is not in the armed attempt set, so the un-narrowed loop also falls through to false | put the marker-owned segment id in the armed attempt tree and assert the `markerOwned` counter | S |
| 7 | F-rewind-refly-001-01 | `SupersedeCommitTests.cs:969` `HasReFlySessionStructuralMutation_PreExistingPostRpBranchPoint_ExcludedByBaseline` | the fixture branch point is parentless, so the lineage filter excludes it regardless of the baseline; a regression that stops honoring `PreSessionBranchPointIds` stays green | set `ParentRecordingIds` on the branch point so the baseline exclusion is the only live term | S |
| 8 | F-rewind-refly-001-03 | `SupersedeCommitTests.cs:1255` `HasReFlySessionStructuralMutation_PreRewindBreakup_NotDetected` | same fixture shape: the pre-rewind cutoff is never the deciding exclusion, so a wrong-source or inverted-epsilon cutoff stays green | set `ParentRecordingIds` so only the cutoff comparison at :1456 can exclude | S |
| 9 | F-rewind-refly-020-01 | `RewindCleanupClearTests.cs:37` `RewindStrip_ClearsPendingCleanup_PidsAndNames` | the cell sets both properties, nulls them itself, then asserts null; no xUnit cell drives `HandleRewindOnLoad`, so deleting `ParsekScenario.cs:4566-4567` leaves the suite green | extract the clears into an internal static and assert it, or drive the path behind a seam, else delete | S |
| 10 | F-catchall-062-01 | `BackwardCompatTests.cs:69` `RevertDetection_TreeRecordingsCounted_InTotalSavedRecCount` | no production symbol is called; the OnLoad revert-detection counter can be broken in any way | extract the counting into an internal static and assert it, or delete the cell | M |
| 11 | F-recording-tree-011-01 | `QuickloadResumeTests.cs:2225` `IsRevert_EpochDecreased_IsTrue` | the replayed decision mirrors a pre-#434 OnLoad shape; production now computes `isRevert` from `RevertDetector.Consume` (`ParsekScenario.cs:3457-3462`) and the epoch/count clauses no longer exist | extract the production revert decision into an internal static (the `ShouldRunQuickloadDiscard` pattern) and point the cells at it | M |
| 12 | F-recording-tree-042-03 | `SceneExitInterceptorTests.cs:414` `BuildPostChoice_ArmsTokenWithDestination` | the lines the name claims live inside the returned closure, which xUnit cannot invoke (`HighLogic.LoadScene`); deleting all three statements leaves both asserts green | extract the closure body behind an `Action<GameScenes> loadScene` seam and assert the token, destination and one load call | M |
| 13 | F-rewind-refly-014-04 | `RewindLoggingTests.cs:701` `ResourceCorrection_ResetsToBaseline_NotAbsoluteTarget` | `currentFunds + (baseline - currentFunds)` is `baseline` for every input, so no change to the real correction (`LedgerOrchestrator.RecalculateAndPatch`) can move it | assert the production correction seam (a ledger recalc at the adjusted UT), or delete if the ledger tests own it | M |
| 14 | F-recorder-events-015-01 | `LoopPhaseTests.cs:602` `BackwardCompat_MissingKey_ReturnsNull_RecordingTree` | it loads through `LoadRecordingMetadataForTests`, the same call as its non-tree twin; the `RecordingTreeRecordCodec` decode site it names is never executed | rebuild the node inside a `RECORDING_TREE` node and load via `RecordingTree.Load`, or rename | S |
| 15 | F-catchall-039-03 | `ScenarioAutoCommitResourcesAppliedTests.cs:325` `AutoCommit_CommitOrderingIsCommitThenMark` | the name promises a reorder guard but every assertion is end-state, and the end state is order-independent (the comment concedes it) | rename to an end-state test, or make the order observable (log order, or `HasPendingTree` seen by `MarkTreeAsApplied`) | M |
| 16 | F-harness-seam-015-02 | `InGameTestSidecarReaperTests.cs:325` `SuffixListMatches_RecordingPipeline` | the comment promises that a new `RecordingPaths` suffix reds this test, but the expected array is a hand-written literal; a new sidecar builder is invisible, so orphan sidecars can reappear green | derive the expected set by reflecting over `RecordingPaths` for static string methods taking a recordingId | M |
| 17 | F-recording-tree-042-04 | `SceneExitInterceptorTests.cs:454` `SafeWritePersistent_TestSeam_FailureOnMainMenu_ReturnsFalse` | the seam returns at `SceneExitInterceptor.cs:505-506` before the try, so the MAINMENU hard-block at :565-572 is unreachable; that hard-block has no test anywhere | add a save-failure seam inside the try (or seam `ShowSaveFailedPopup`), else rename to `_TestSeamPassthrough_` | M |
| 18 | F-catchall-006-01 | `AtomicMarkerWriteTests.cs:271` `AtomicMarkerWrite_CapturesRewindPointUTFromRp_PinnedBySourceInspection` | it is a source gate whose stated reason is false: the same file calls `RewindInvoker.AtomicMarkerWrite` directly in 10 cells and reads marker fields back | replace with `Assert.Equal(rp.UT, scenario.ActiveReFlySessionMarker.RewindPointUT)` in an existing cell | S |
| 19 | F-recording-tree-047-01 | `SwitchSegmentSaveLoadTests.cs:233` `F9_ToPreSwitchSave_ClearsMarker_DropsPendingAttempt` | both ordering `IndexOf` calls hit the clear-helper bodies near the top of the file, not `LoadRewindStagingState`, so the clear-before-read contract is unpinned | drive `LoadRewindStagingState` by reflection as `RecordingRewindRetirementTests.cs:112` does; keep a source gate only for the intent half | S |
| 20 | F-logistics-route-034-03 | `Logistics/RouteStoreScenarioIntegrationTests.cs:324` `Scenario_Update_InvokesRouteOrchestratorTick` | the needle also occurs at `ParsekScenario.cs:1009` inside the catch block's log string, so deleting the real `Update()` hook at :1004 keeps the gate green | strip comments and string literals before the scan (or walk the tree), or pin a literal with no log-string twin | M |

### Per-cluster finding counts

| Cluster | Findings | High | Medium | Low | T1 | T2 | T3 | T4 | T6 |
|---|---|---|---|---|---|---|---|---|---|
| recording-tree | 146 | 2 | 54 | 90 | 32 | 48 | 62 | 3 | 1 |
| ledger-career | 88 | 0 | 37 | 51 | 20 | 19 | 47 | 2 | 0 |
| catchall | 79 | 0 | 23 | 56 | 19 | 26 | 26 | 7 | 1 |
| recorder-events | 73 | 3 | 17 | 53 | 21 | 25 | 27 | 0 | 0 |
| rewind-refly | 56 | 0 | 29 | 27 | 22 | 11 | 23 | 0 | 0 |
| legacy-bugfix | 55 | 0 | 14 | 41 | 12 | 23 | 17 | 2 | 1 |
| logistics-route | 46 | 0 | 16 | 30 | 4 | 14 | 26 | 2 | 0 |
| map-render | 46 | 0 | 18 | 28 | 9 | 10 | 25 | 2 | 0 |
| trajectory-orbit | 37 | 1 | 10 | 26 | 9 | 14 | 13 | 0 | 1 |
| ghost-playback | 37 | 0 | 21 | 16 | 8 | 9 | 18 | 2 | 0 |
| spawn-vessel | 35 | 1 | 8 | 26 | 10 | 14 | 11 | 0 | 0 |
| harness-seam | 20 | 0 | 6 | 14 | 5 | 8 | 7 | 0 | 0 |
| mission-groups | 16 | 0 | 10 | 6 | 4 | 1 | 9 | 2 | 0 |
| logging | 9 | 0 | 1 | 8 | 2 | 1 | 4 | 2 | 0 |
| ui-settings | 8 | 0 | 2 | 6 | 1 | 5 | 1 | 1 | 0 |
| wiring-gates | 7 | 0 | 2 | 5 | 4 | 3 | 0 | 0 | 0 |
| analyzer | 4 | 0 | 2 | 2 | 2 | 0 | 1 | 0 | 1 |
| io-serialization | 4 | 0 | 2 | 2 | 0 | 0 | 4 | 0 | 0 |
| supplement | 2 | 0 | 0 | 2 | 1 | 1 | 0 | 0 | 0 |


### Recurring patterns

Five shapes account for most of the register.

1. **Inline replay of a production branch, then assertions on the test's own writes.** The dominant
   T1 shape; concentrated in legacy bug files and "simulated integration" cells. All 7 High findings
   are instances. Examples: F-recorder-events-013-01 (hand-mutates the split tree),
   F-catchall-062-01 (no production symbol called at all), F-catchall-060-01
   (`SaveContaminationTests.cs:86`, the cross-save list clearing is simulated inline),
   F-ghost-playback-008-02 (the comment says "Simulating the check in
   `UpdateChainGhostOrbitIfNeeded`").
2. **Source-text gates over unstripped source.** `IndexOf` on raw file text or a fixed character
   window, where `SourceScanText.StripCommentsAndMaskLiterals` already exists in the test project and
   the project rule reserves a derived set for an AST walk. Comments read as code and the gate fails
   GREEN. Examples: F-logistics-route-034-03 (the needle has a log-string twin 5 lines away),
   F-logistics-route-040-01 (a decl+4000-char slice that under-reads `ParsekFlight` and overruns into
   the next method for `ParsekKSC` and `ParsekTrackingStation`), F-ghost-playback-023-02
   (`StripComments` cuts at the first `//` inside string literals too), F-mission-groups-015-02.
3. **"Culture-invariant" cells that install no culture.** The check is symmetric (same provider on
   write and read) so a codec switched to `CurrentCulture` round-trips green, and the test only reds
   on a comma-decimal host that nothing in the suite establishes. Examples: F-io-serialization-004-01
   and -004-02 (`SerializationEdgeCaseTests.cs:50` and :570). Two near-misses in the same family
   (F-harness-seam-014-02, F-harness-seam-017-01) swap the culture over integer-only payloads, where
   no .NET culture changes the formatting.
4. **Single-branch predicate pins with no mirror case.** The Medium T3 bulk: the named conjunct is
   never the deciding term because an earlier filter already rejects the fixture, so removing it
   stays green. Examples: F-rewind-refly-001-01 / -001-03 / -001-04 / -001-05 (four
   `SupersedeCommitTests` cells all excluded by lineage before the guard under test),
   F-mission-groups-003-04 (`SpanClock.cs:3198-3200` returns true either way for the cell's inputs),
   F-ghost-playback-018-02.
5. **Byte-identical twins distinguished only by name or comment.** The Low T2 bulk (231 of 232 T2
   rows are Low). Examples: F-rewind-refly-010-03 / F-rewind-refly-010-04 (`RewindTimelineTests.cs:594` and :613 duplicate
   cells 200 lines above), F-catchall-018-02 (`ArrivalAlignHoldTests.cs:327`, the comment itself says
   so), F-catchall-024-02 (`RevertDiscardTests.cs:377`, near-identical to the cell at :67, differing only by one extra field assignment).

Two smaller shapes worth naming because they are mechanical to fix:

- **Static state left installed by a passing test.** F-map-render-021-02
  (`RotationPeriodForTesting` installed, never restored), F-ledger-career-015-07
  (`LedgerOrchestrator.Kerbals` module installed for the rest of the assembly run),
  F-catchall-002-01 and F-catchall-059-02 (a suppression flag / `IsRewinding` left set for every
  later cell in the `Sequential` collection).
- **Live runtime counters read as equality.** F-logging-001-03 and F-logging-002-02 both assert
  `GC.CollectionCount(0)` after a reset, which fails spuriously whenever a gen0 collection lands in
  between.

### Phase 2 verification summary

| Population | Size | Verified | Refuted | Downgraded | Method |
|---|---|---|---|---|---|
| High | 7 | 7 | 0 | 0 | bounded mutation in the scratch worktree |
| Medium (seeded 10% sample) | 27 | 26 | 0 | 1 | read-only refutation against the SUT |
| Medium (remainder) | 245 | 0 | 0 | 0 | unverified |
| Low | 489 | 0 | 0 | 0 | unverified |

Adjudications applied to the register:

- F-recording-tree-003-02 DOWNGRADED Medium -> Low: the negative case it asks for already exists in
  the same file (`RecordingOptimizerTests.cs:3241-3308` supplies same-tree non-debris candidates and
  reds when the UT-range conjunct is dropped). Suite-wide the guard is covered; the residue is a
  limited-window duplicate.
- F-logistics-route-020-02: falsifiability string CORRECTED (the named mutation reds a sibling cell,
  `RecoveryFromDifferentTree_Excluded:149`, not this one). The finding stands.
- F-ghost-playback-008-02: wording over-reach noted (only two of the three named cells replay the
  predicate; the third is a plain property getter/setter cell). Verdict unchanged.
- F-ledger-career-007-03: a reviewer argued the severity could go UP (no other cell pins the
  resource-type gate); left at Medium under the "wrong branch pinned" rule.

Estimated Medium false-positive rate from the sample: 0 of 27 refuted, 1 of 27 severity drift. Read
with the calibration's other direction (a 10-20% Medium MISS rate on 39-method batches), the Medium
register is accurate in what it says and incomplete in what it covers.

## T7 - production-bug triage register

Production observations recorded by reviewers as NOT test defects. Phase 2 verified TEST findings
only; every row entered Phase B **unverified**. Verification, severity assignment and todo filing
happen in Phase B's `testfix-t7-prodbugs` workstream, never in this document, and nothing here is
fixed inside this audit. Phase B verdicts are in the Status column and the reasoning is under the
table: T7-1, T7-2 and T7-3 are `not-a-bug`; T7-4 is `verified` as a documentation-only defect and is
filed as `TQ-1-mapview-refused-doc-says-rejected` in `docs/dev/todo-and-known-bugs.md`.

| # | Observation | Evidence at `4aedb0a1a` | Status | Source |
|---|---|---|---|---|
| T7-1 | `FlightRecorder.ResolveEffectiveMinSampleInterval(bool highFidelityActive, float configuredMin)` ignores its `highFidelityActive` argument and returns `configuredMin` unchanged. Whether high fidelity is meant to tighten the foreground floor here, or is folded in upstream by design, is an open question. | `Source/Parsek/FlightRecorder.cs:990-993` | not-a-bug | reviewer note, `recorder-events` cluster |
| T7-2 | `BackgroundRecorder.ResolveBackgroundAttitudeMinSampleInterval` explicitly discards the same flag (`_ = highFidelityActive;`) and returns `Math.Min(effectiveMotionMinSampleInterval, foregroundAttitudeMin)`. The in-source comment says the caller already folded fidelity into the motion interval; if that premise ever stops holding, background attitude cadence silently ignores fidelity. Paired with T7-1 this is the same question in the mirror direction. | `Source/Parsek/BackgroundRecorder.cs:4869-4885` (discard at :4877, `Math.Min` at :4884) | not-a-bug | F-recorder-events-028-01 (Low T3, test-side); C-recorder-events-028-01 (D3 keep, contract pin) |
| T7-3 | `TrajectoryMath.ShouldRecordPoint` zero-delta question: with `minInterval = 0` and `elapsed <= 0`, the min floor at `TrajectoryMath.cs:59-61` does not fire and the speed gate returns TRUE, so a duplicate-UT sample is admitted. With any production `minInterval > 0` the floor already blocks it, so this may be unreachable in shipped configuration. | `Source/Parsek/TrajectoryMath.cs:42-62` | not-a-bug | C-recorder-events-011-01, refiled from the D3 backlog (`status = invalid` there, explicitly "refile as a T7 question") |
| T7-4 | Documentation-vs-behavior mismatch on the map-view command seam: the XML doc on `MapViewToggleOutcome.Refused` says the outcome is "REJECTED with the per-direction reason", but `RefusalVerdict` returns `"ERROR"` for `Refused` (and `"REJECTED"` only for `Unavailable`). Harness `expect` strings are written against the verdict split, so the doc is the thing that is wrong - but which of the two is authoritative is an operator call. | `Source/Parsek/TestCommands/TestCommandMapViewVerbs.cs:21-24` (doc) vs `:171-177` (`RefusalVerdict`) | verified | reviewer note, `harness-seam` cluster |

Phase B verdicts (`testfix-t7-prodbugs`, verified against the tree at `e866d44b`, line numbers
re-grepped there):

- **T7-1 not-a-bug.** High fidelity is folded into the MAX resolver, never the MIN floor:
  `FlightRecorder.ResolveEffectiveMaxSampleInterval` (`Source/Parsek/FlightRecorder.cs:1015-1023`)
  returns `Math.Min(configuredMax, Math.Max(0f, configuredMin))` when the flag is set, so a
  high-fidelity window collapses the backstop onto the player's configured minimum and samples at
  exactly that density. Tightening the MIN there would sample BELOW the density preset the player
  chose, which is the one thing the floor exists to prevent. The intent is pinned by name in
  `AdaptiveSamplingTests.HighFidelityWindow_UsesConfiguredMinIntervalBackstop`
  (`Source/Parsek.Tests/AdaptiveSamplingTests.cs:431-455`). Full production caller set of the
  two-arg helper is three sites - the five-arg overload's tail (`FlightRecorder.cs:1012`; the overload is
  also called from `BackgroundRecorder.cs:2157`, but that call is gated on a debris tier and never
  reaches the tail, so the tail is reached only from `FlightRecorder.cs:9088`), `BackgroundRecorder.cs:2164`, and
  `BackgroundRecorder.cs:4916` - and none of them wants a sub-preset floor. The identity body is a
  named seam, not a dropped branch.
- **T7-2 not-a-bug, and the mirror premise holds at the single caller.** The only production caller
  is `BackgroundRecorder.cs:2183`, and the value it passes as `effectiveMotionMinSampleInterval` is
  computed at `:2156-2165` by a branch on the same flag: high fidelity substitutes the configured
  foreground minimum for the coarser proximity interval, exactly as the comment at `:2145-2148`
  says. So fidelity IS folded in by the caller and a second fold would be a double count. Walked in
  both directions: on the debris-tier path the motion floor is `ProximitySamplingCadence.ResolveSampleInterval`,
  which floors at `MinimumSampleIntervalSeconds = 0.02f` and clamps to `configuredMin`, so the `Math.Min`
  at `:4919` resolves to the foreground floor there. On the non-fidelity, non-debris branch the raw
  `proximityInterval` is passed (`:2165`, `ProximityRateSelector.DockingInterval = 0.2`, no clamp against
  `configuredMin`), so at the Low preset (`configuredMin = 0.5`) the attitude floor is the proximity
  floor 0.2, not the foreground floor. That is the designed non-fidelity cadence, not a fidelity
  question: the flag is honoured on the branch that carries it, and the discard is a no-op on the other. If the caller's branch is ever removed the discard becomes
  a defect, so the contract pin (C-recorder-events-028-01) stays worth having.
- **T7-3 not-a-bug: `minInterval = 0` is unreachable in any shipped configuration.** It is a
  documented opt-out (`TrajectoryMath.cs:40`, "Set minInterval = 0 to disable the floor") exercised
  only by tests. Every production feed is positive: `ParsekSettings.GetMinSampleInterval` returns
  0.5 / 0.2 / 0.05 for Low / Medium / High (`Source/Parsek/ParsekSettings.cs:216-219`) and
  `SamplingDensityLevel` (`:202-207`) clamps any out-of-range serialized `samplingDensity` to
  Medium, so no corrupted save reaches a fourth value; the three production `ShouldRecordPoint`
  call sites (`FlightRecorder.cs:9100`, `BackgroundRecorder.cs:2179`,
  `ChainSegmentManager.cs:409`) all derive their floor from that table, through
  `ProximitySamplingCadence` / `ProximityRateSelector` / `ContinuationMinInterval`
  (`ChainSegmentManager.cs:261-262`), and none of those paths can produce 0. With any positive
  floor a duplicate-UT or backwards-UT sample fails `elapsed < minInterval` and is rejected, so the
  admitted-duplicate reading has no shipped reachability.
- **T7-4 verified as a DOCUMENTATION defect (doc-only; the wire contract is correct).** The
  `RefusalVerdict` split at `TestCommandMapViewVerbs.cs:171-177` is deliberate and reasoned in its
  own doc block (`:163-170`: REJECTED for gates evaluated before the stock call, ERROR once stock
  was called and declined), is the seam-wide convention (`harness/lib/hlib.py:1080-1090`,
  `SEAM_VERDICT_OUTCOME_TERMINAL = "ERROR"`, mirrored by `EnterWatchMode`'s post-call
  `watch-not-entered`), and is pinned by
  `TestCommandMapViewVerbsTests.RefusalVerdict_IsRejectedOnlyBeforeStockIsCalled`
  (`Source/Parsek.Tests/TestCommandMapViewVerbsTests.cs:155-167`). The enum member doc at
  `:21-24` contradicts it with the word REJECTED. No committed spec currently pins the refusal
  branch - every `EnterMapView` / `ExitMapView` step in `harness/scenarios/*.toml` is
  `expect = "OK"` (all eleven specs with an uncommented step for either verb: B32, GUI-6, GUI-9, H59, RF-7M, RF-8, V26M, V27M, V3C, V6M, W1;
  grep for a non-OK expect within three lines of either verb returns nothing) - so the consequence is authorship, not a red
  lane: a spec author reading the enum doc writes `expect = "REJECTED"` for a stock-declined toggle
  and the lane mismatches against the ERROR the seam actually emits. Filed as TQ-1.

Deliberately NOT a T7: the `RouteRenderUnionWiringTests` slicing overrun. It is a TEST defect, not a
production one - `ExtractDriveMissionLoopUnitsBody` takes a fixed decl + 4000-char slice of raw
source, so the negative arms miss the last third of `ParsekFlight.DriveMissionLoopUnits` (5,988
chars) and overrun into the following method for `ParsekKSC` (2,993) and `ParsekTrackingStation`
(3,053). It is filed as F-logistics-route-040-01 (Medium T4) and belongs to the `testfix-t4-flaky`
workstream.

When a T7 is VERIFIED in Phase B, it gets an entry in `docs/dev/todo-and-known-bugs.md` in the same
commit, headed
`## TQ-<n>-<slug>: <prose> [FILED <date> off test-quality-audit, evidence <file:line@4aedb0a>]`.

## D3 - coverage-opportunity backlog

Full table: `research/test-quality-audit-2026-09-14/coverage-opportunities.csv` (249 rows);
reviewed narrative with per-cluster tables: `coverage-opportunities.md`.

252 Phase 1 candidates entered Phase 3 triage (SUT guard confirmed at the pinned SHA, headless
feasibility classified, existing coverage grepped, one mutant named per proposal). Outcome: 245
kept, 1 already covered, 3 invalid, 3 duplicates folded. Every kept row carries a `mutant`: the
one-line production change the proposed test reds on. A row without one was dropped.

| Axis | Split |
|---|---|
| Risk | recording 100, career 54, playback 53, ui 26, data-loss 12 |
| Feasibility | direct 193, seam 36, in-game 9, generator 7 |
| Case kind | negative 86, boundary 52, state-transition 44, integration 29, mirror-direction 24, serializer-key 6, roundtrip 4 |
| Effort | S 157, M 75, L 13 |
| Priority (1 = highest) | 1: 16, 2: 86, 3: 97, 4: 38, 5: 8 |
| Names an existing finding | 43 of 245 |

Priority is risk order (data-loss > career > recording > playback > ui), then feasibility (direct >
seam > generator > in-game), then whether a Medium/High finding already names the gap. 193 of 245
are `direct`: no new seam, no generator change, no live KSP. That is the reason this backlog is
cheap.

### Top 20

| # | cand_id | Risk | SUT | Kind | Feas | P | Mutant the proposed test reds on |
|---|---|---|---|---|---|---|---|
| 1 | C-legacy-bugfix-023-01 | data-loss | `RecordingStore.OrphanCleanup.cs:278` | negative | direct | 1 | delete the `savedPendingTreeDuringActiveRestore` contribution at :276-277 |
| 2 | C-legacy-bugfix-024-01 | data-loss | `RecordingSidecarStore.cs:63` | negative | direct | 1 | re-add `File.Delete(vesselPath)` when `rec.VesselSnapshot` is null |
| 3 | C-recording-tree-034-01 | data-loss | `RecordingStore.cs:4606` | negative | direct | 1 | delete the `hasSavedPendingDuringActiveRestore` term from the play-mode guard at :4608-4611 |
| 4 | C-rewind-refly-011-01 | data-loss | `RewindPointReaper.cs:284` | state-transition | direct | 1 | delete the `File.Delete(absolute)` call at :286 (log only) |
| 5 | C-rewind-refly-020-01 | data-loss | `ParsekScenario.cs:6733` | integration | direct | 1 | walk `protoVessels` forward with `RemoveAt` at :6740 so consecutive strips are skipped |
| 6 | C-legacy-bugfix-005-01 | recording | `ParsekFlight.cs:16405` | boundary | direct | 1 | delete `return TerminalState.Orbiting;` at :16406 (falls through to the SubOrbital default) |
| 7 | C-recorder-events-023-01 | recording | `ParsekFlight.TerminalEvents.cs:326` | state-transition | direct | 1 | move `rec.VesselDestroyed = true;` below the `TerminalState.Destroyed` early return |
| 8 | C-recording-tree-011-01 | recording | `ParsekScenario.HydrationRepair.cs:25` | negative | direct | 1 | delete the `PendingTree.Id != loadedTree.Id` disjunct |
| 9 | C-rewind-refly-004-02 | recording | `RecordingStore.RewindSupersedeRollback.cs:99` | boundary | direct | 1 | delete the `string.IsNullOrEmpty(rel.OldRecordingId)` skip |
| 10 | C-rewind-refly-019-01 | recording | `RewindInvoker.cs:2433` | state-transition | direct | 1 | delete the `PendingTree` removal block at :2433-2438 |
| 11 | C-legacy-bugfix-010-01 | recording | `EffectiveState.cs:1065` | state-transition | direct | 1 | invert `IsSlotEffectiveTipOpen`'s use at :1064 so an Immutable tip is admitted |
| 12 | C-recorder-events-024-01 | recording | `BackgroundRecorder.PartEventPolling.cs:92` | integration | direct | 1 | delete the `CheckFairingState` call from `PollPartEvents` |
| 13 | C-recording-tree-022-01 | recording | `RecordingOptimizer.cs:1849` | mirror-direction | direct | 1 | change `headSection.bodyFixedFrames.Count < 2` to `< 1` |
| 14 | C-recording-tree-030-01 | recording | `RecordingStore.cs:539` | boundary | direct | 1 | force `firstMoving = 0` so the trim / orbit-drop / event-retime block never runs |
| 15 | C-rewind-refly-015-01 | recording | `RecordingStore.cs:4216` | mirror-direction | direct | 1 | narrow the `emptyParents or emptyChildren` disjunct at :4220 to `emptyChildren` only |
| 16 | C-recording-tree-023-01 | recording | `RecordingTreeSplitter.cs:785` | state-transition | direct | 1 | delete the `RollBackInMemory(scenario, freshSnapshot)` call in the catch |
| 17 | C-recording-tree-047-02 | data-loss | `RecordingStore.cs:2495` | negative | direct | 2 | move the marker-owned bypass below the attempt-set / cutoff checks |
| 18 | C-catchall-049-01 | data-loss | `ChainSegmentManager.cs:429` | state-transition | seam | 2 | delete `rec.MarkFilesDirty()` after the continuation point append |
| 19 | C-io-serialization-004-01 | data-loss | `FileIOUtils.cs:302` | negative | seam | 2 | delete the destination-preserving fallback in the `File.Replace` catch |
| 20 | C-recording-tree-039-01 | data-loss | `RecordingStore.OrphanCleanup.cs:56` | integration | seam | 2 | delete the `RewindSaveFileName` limb at :56-57 |

**Phase B status (2026-09-15).** The five priority-1 data-loss rows have landed as xUnit cells
on `testfix-t5-coverage`, each mutation-proved in that worktree with the mutant the CSV names (C-legacy-bugfix-024-01 in the shared save body both entry points route through, not the public wrapper; the wrapper stays guarded by the source-text pin)
(patches under `research/test-quality-audit-2026-09-14/mutations/<cand_id>-phaseB.patch`):
C-legacy-bugfix-023-01, C-legacy-bugfix-024-01, C-recording-tree-034-01, C-rewind-refly-011-01,
C-rewind-refly-020-01. Their `status` in the CSV is now `done`.

**Phase B status, second PR (2026-09-15).** Ten of the eleven remaining priority-1 `recording` rows
landed as xUnit cells on the same branch, each mutation-proved with the mutant the CSV names
(C-recording-tree-023-01 needed only a throwing seam - a one-shot
`LedgerOrchestrator.OnTimelineDataChanged`, which the forward path fires exactly once at step 9b -
so no production change): C-legacy-bugfix-005-01, C-legacy-bugfix-010-01, C-recorder-events-023-01,
C-recording-tree-011-01, C-recording-tree-022-01, C-recording-tree-023-01, C-recording-tree-030-01,
C-rewind-refly-004-02, C-rewind-refly-015-01, C-rewind-refly-019-01. C-recorder-events-024-01 is
`already-covered`: PR #1698 replaced the reflection existence check with an IL call-set comparison
in `BackgroundPartEventAuditTests.PollPartEvents_CoversAllPolledEventTypes_MatchingFlightRecorder`,
which is the proposed cell, so no duplicate was written.

**Phase B status, third PR (2026-09-15).** Twelve priority-2 data-loss / career rows landed as
xUnit cells on the same branch: eleven are new coverage; C-ghost-playback-023-01 is already covered
(its mutant reds `DiscardFateTests.FlushThenDiscard_EventsAndMilestonePurged` and two siblings, found
by the review's cross-class re-run, so the duplicate cell was dropped). Each landed cell is
mutation-proved with the mutant the CSV names, with no production change. Method note for the
remaining coverage rows: a class-scoped RED proves the new cell guards the line, not that the row
was uncovered; an `already-covered` verdict needs the mutant run against every test class that
reaches the same method. C-recording-tree-047-02, C-ghost-playback-023-01, C-ledger-career-001-01,
C-ledger-career-003-01, C-ledger-career-004-01, C-ledger-career-006-01, C-ledger-career-007-01,
C-ledger-career-007-02, C-ledger-career-008-01, C-ledger-career-009-01, C-ledger-career-009-02,
C-ledger-career-011-01. Their `status` in the CSV is now `done`.

**Phase B status, fourth PR (2026-09-15).** Twelve more priority-2 career-risk rows, all
`direct` / S: ten are new coverage, two are `already-covered` under the cross-class rule
(C-ledger-career-017-01 reds `StrategyCaptureTests.RecalculateAndPatch_StrategyActivateSetupCostsAffectScienceAndRepBalances`,
C-recording-tree-032-01 reds `SupersedeCommitTombstoneTests.CommitTombstones_PreRewindPayoutAttributedToOriginChild_NotTombstoned`),
so no duplicate cell was written for either. The ten landed cells are mutation-proved with
the mutant the CSV names, with no production change: C-ledger-career-014-01,
C-ledger-career-014-02, C-ledger-career-015-02, C-ledger-career-021-01, C-ledger-career-025-01,
C-ledger-career-031-01, C-legacy-bugfix-016-01, C-logistics-route-014-01,
C-recording-tree-021-02, C-recording-tree-051-02. Their `status` in the CSV is now `done`.

**Phase B status, fifth PR (2026-09-15).** Twelve more priority-2 career / recording rows,
all `direct`: nine are new coverage, two are `already-covered` under the cross-class rule
(C-rewind-refly-016-01 reds `TombstoneEligibilityTests.RepPenalty_PairedWithDeathAtExactUTBoundary_Eligible`,
C-ledger-career-003-02 reds `LedgerOrchestratorTests.CreateVesselCostActions_PairedRecoveryEventPreferredOverPointDelta`),
so no duplicate cell was written for either, and one is `deferred` as obsolete
(C-rewind-refly-017-01: commit 5d7568c88, the audit day, retired
`RewindReadbackGuard.AbortRewindPatchOnDivergence`, so the abort OR-gate has no production
operand left to delete). C-ledger-career-004-02's named single-conjunct mutant proved
EQUIVALENT - `AdjustStartUtForChainGap` re-derives the gap key from `rec.ChainIndex - 1`
while the map is keyed by `predecessor.ChainIndex`, so a mismatched predecessor misses the
lookup anyway - and its cell is proved against the refactor-shaped mutant that also unifies
the two key sources. The nine landed cells are mutation-proved with no production change:
C-rewind-refly-006-01, C-rewind-refly-016-02, C-ledger-career-004-02, C-ledger-career-013-01,
C-ledger-career-018-01, C-ledger-career-027-01, C-ledger-career-034-01, C-catchall-021-01,
C-ledger-career-036-01. Their `status` in the CSV is now `done`.

Sixteen of the twenty are `direct` and `S` or `M` effort. Numbers 12 and 20 pair with High and
Medium findings respectively (F-recorder-events-024-01 and F-recording-tree-039-01), which is the
expected shape: where a test cannot fail, the guard also has no coverage.

Per-cluster tables capped at 10 rows each are in `coverage-opportunities.md`; the full 245 are in
the CSV. Kept rows per cluster: recording-tree 71, ledger-career 39, recorder-events 28,
rewind-refly 21, legacy-bugfix 17, ghost-playback 15, trajectory-orbit 10, map-render 9, catchall 6,
harness-seam 6, logistics-route 6, mission-groups 6, spawn-vessel 5, logging 3, io-serialization 1,
wiring-gates 1, ui-settings 1.

### Triage notes carried forward

- C-recorder-events-011-01 (`ShouldRecordPoint` zero-delta) is refiled as T7-3 above: the
  candidate's expected value contradicts current production, so it is a question about the code, not
  a missing cell.
- C-logistics-route-020-01 and C-recording-tree-032-01 CONFLICT on whether ELS excludes a superseded
  recovery row; the route-side exclusion likely comes from `treeRecordingIds`. Confirm before
  writing either cell.
- Two chain-walk mutants (`GhostChainWalker.cs:535`, `ParsekFlight.cs:17805`) HANG rather than red;
  those cells need a bounded-time assertion, not a plain mutation proof.
- C-ghost-playback-013-02 needs a real AST walk and `Parsek.Tests` has no `Microsoft.CodeAnalysis`
  reference today; regex over source is barred by the rubric, so it waits on a package decision.
- C-map-render-012-01 needs a small production observability addition (four resolver guards return
  bare `false`) before any test can discriminate guard removal. That is a production change and
  belongs to a separate decision, not to a test PR.
- The 9 in-game-only rows (they need a live `Vessel` / `Part` / `Transform`) sit at priority 4 (5 rows) and 5 (4 rows)
  as the `InGameTests` backlog, not xUnit work.
- `dupe_of_july_id` is filled in the July crosswalk, not in the CSV.

## D4 - summary

The suite is sound. 18,922 of 20,225 reviewed test methods (93.6%) can red on a realistic regression
in the code they exercise, the rate is flat across all four size tiers, and after a full-file pass
over 1,010 files not one Critical finding exists: no save, sidecar, ledger or rewind
data-destruction path was found where a realistic regression ships green with no other cell catching
it. The 768 findings are 6.4% of the methods reviewed, and 489 of them are Low - duplicate twins,
misnamed cells, organization debt - that cost reading time and suite wall, not safety.

The real risk is small, sharp and concentrated. Seven High findings, all T1, all
mutation-verified: in each, the test performs the production work itself, so the guard it is named
for can be deleted and the suite stays green. Two of the seven guard bugs that ACTUALLY SHIPPED
once - the relative-debris anchor drift (`FlightRecorder.cs:9512`) and the bug-95 snapshot nulling
that kills respawn after revert (`ParsekFlight.cs:3311`) - which means the exact re-regression these
cells exist to prevent would slip through today. That is the whole High set, and it is seven test
fixes, not a rewrite.

Three anti-patterns dominate everything below High. Inline replay of a production branch followed by
assertions on the test's own writes (185 T1 rows, and all 7 High). Source-text gates scanning
unstripped source or fixed character windows, where a comment reads as code and the gate fails GREEN
(the T4 population, and the reason the project rule already says derived sets need an AST walk).
And single-branch predicate pins where an earlier filter, not the named conjunct, decides the
outcome - the Medium T3 bulk, and the cheapest class to fix, because most need only one fixture
field set.

The coverage backlog is unusually cheap for what it buys: 245 proposals, each with a named mutant,
193 of them writable directly against existing pure surfaces with no new seam and no live KSP. The
top of it is the same code the High findings sit on top of.

Phase B should do three things in order. First the seven High T1 fixes, because they are the only
findings where a shipped-bug class currently has zero guarding test; they are seven cells and three
small helper extractions. Second the T4 source-gate repairs, because a gate that fails green is
worse than no gate and the fix is mechanical (route through the existing
`SourceScanText.StripCommentsAndMaskLiterals`, or delete the gate in favor of the behavioral cell
that already exists). Third the priority-1 coverage rows, which are 16 direct cells over data-loss
and recording guards. The 489 Low rows should be swept opportunistically, never as a project.

## Phase B workstream assignment

Four workstreams, four worktrees, four PRs. Path is always `../Parsek-<branch>`; base is
`origin/main` after this audit PR merges (a workstream may stack on `test-quality-audit` before
that). One PR per workstream, never a local merge. Only the assigned workstream edits its own rows;
audit-doc conflicts resolve entry-level keep-both.

**Rule: each D2/D3 id belongs to exactly one workstream.** Assignment is by category, mechanically:

| Workstream | Worktree | Gets | Count |
|---|---|---|---|
| `testfix-t1t2` | `../Parsek-testfix-t1t2` | every D2 row with category T1, T2, T3 or T6 | 743 |
| `testfix-t4-flaky` | `../Parsek-testfix-t4-flaky` | every D2 row with category T4 | 25 |
| `testfix-t5-coverage` | `../Parsek-testfix-t5-coverage` | every D3 row with `status = keep` | 245 |
| `testfix-t7-prodbugs` | `../Parsek-testfix-t7-prodbugs` | every T7 row in this document | 4 |

T3 belongs with T1/T2 rather than in a workstream of its own: a T3 fix is assertion strengthening on
a cell that already exists, which is the same edit shape and usually the same file as the T1/T2 work.
T6 (5 rows) rides along because every one of them is a file-level move or a deletion inside a file
the T1/T2 stream is already touching. 743 + 25 = 768 = the whole D2 register; no row is unassigned
and no row appears twice.

`testfix-t1t2` is large enough to need internal waves; run it as High first (7), then Medium T1
(114), then Medium T3 (138) plus the one Medium T2, then the Low sweep (483) as capacity allows. The Low sweep is
opportunistic and may be cut without reopening the audit.

Wave order across workstreams: (1) T1/T2 fixes, (2) T4 flakiness, (3) T5 coverage additions,
(4) T7 production bugs as separate PRs.

**Per-PR mutation-proof rule.** Every added or strengthened test carries a mutation proof: stub or
invert the guarded production line, or invert the expected value, or drop a serialized key, or
mutate the generator. Where no mutant exists, the PR body states why. Mutations follow the Phase 2
rules exactly: detached scratch worktree at the pinned SHA, `git status --porcelain` empty before
and after, one filtered class per run, patch committed under `mutations/`, row appended to
`mutations.csv`. A fix with no mutation proof and no stated reason does not land. Full suite green
before each PR, run alone in the machine-wide suite slot, and the PR body says it ran alone.

### Suggested first PR per workstream

- `testfix-t1t2`: the 7 High as one PR. F-recording-tree-050-04 and -050-07 are deletions or moves
  to `InGameTests` (no helper needed); F-recorder-events-003-01, F-recorder-events-013-01 and
  F-spawn-vessel-004-01 each extract one pure helper and re-point the cell;
  F-trajectory-orbit-015-01 pins the `AnchorPose` producer through the existing seam;
  F-recorder-events-024-01 replaces a reflection existence check with a call-set comparison. Each
  carries its Phase 2 patch, re-run to confirm it now REDS.
- `testfix-t4-flaky`: the four unstripped-source gates that have a log-string or comment twin, plus
  the two unrestored-static leaks: F-logistics-route-034-03, F-logistics-route-040-01,
  F-ghost-playback-023-02, F-mission-groups-015-02, F-map-render-021-02, F-ledger-career-015-07. All
  six are mechanical and independently verifiable.
- `testfix-t5-coverage`: the five priority-1 data-loss rows: C-legacy-bugfix-023-01,
  C-legacy-bugfix-024-01, C-recording-tree-034-01, C-rewind-refly-011-01, C-rewind-refly-020-01. All
  `direct`, four at `S` effort, each with a one-line mutant already named.
- `testfix-t7-prodbugs`: verify T7-1 and T7-2 together (they are the same question in the two
  directions, and the mirror-direction rule says neither is decided alone), then T7-4, which is a
  one-line doc-vs-behavior call. Verified rows get a `TQ-<n>-<slug>` entry in
  `docs/dev/todo-and-known-bugs.md` in the same commit; unverified rows stay in this document.

### Phase B status

- `testfix-t4-flaky`, first PR (2026-09-15): all six rows of the suggested first PR are FIXED on
  branch `testfix-t4-flaky`, each with a proof row in
  `research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`.
  - F-logistics-route-034-03 fixed: the tick gate scans `SourceScanText.StripCommentsAndMaskLiterals`
    output, so the catch-block log twin no longer stands in for the deleted hook.
  - F-logistics-route-040-01 fixed: `ExtractDriveMissionLoopUnitsBody` slices the SANITIZED,
    brace-matched method body instead of a fixed 4000-character window.
  - F-ghost-playback-023-02 fixed: the consumer sweep's hand-rolled line-split strip now delegates to
    `SourceScanText.StripCSharpComments` (literal-aware, `/* */` aware); literals stay readable
    because two cells pin production log text.
  - F-mission-groups-015-02 fixed: all three scans run over stripped + literal-masked source, each
    proximity window is anchored to the enclosing method body, and one comment-only decoy cell pins
    the stripping.
  - F-map-render-021-02 fixed: `TrajectoryMath.FrameTransform.ResetForTesting()` on both ends of the
    class, so the rotation-period seam cannot cross the Sequential collection.
  - F-ledger-career-015-07 fixed: `LedgerOrchestrator.Kerbals` is saved in the ctor and restored in
    Dispose, matching `CrewReservationRecomputeTests`.
  - The two leak rows were proved with a temporary in-process probe
    (`mutations/phaseB-leak-probe.cs`) that drives ctor -> cell -> Dispose and asserts the static is
    the value it found; RED without the restore, GREEN with it. The probe is scaffolding, not a
    committed test.

- `testfix-t4-flaky`, second PR (2026-09-15): the remaining 19 T4 rows
  (`work/phase-b-slice-t4-02.txt`). 18 FIXED, 1 DELETED in favour of a named twin, 0
  deferred. Each fixed row has a proof row in
  `research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`
  that `git apply --check`s against a clean tree.
  - Comment-stripping (the raw-scan half): F-catchall-026-01 (Update ordering, via
    `StripCommentsAndMaskLiterals`), F-catchall-036-02 (hide policy gate, via
    `StripCSharpComments` with indexes still addressing the raw file),
    F-catchall-049-01 (bug-273 dirty pins, plus a whole-identifier match and a
    literal-masked body walk), F-mission-groups-015-01 (selection stamp; also
    enclosing-method anchoring plus a comment-only decoy cell), F-legacy-bugfix-015-02
    (time-jump recalc), F-ui-settings-002-01 (both tooltip scanners, so a commented-out
    control no longer counts toward the margin-0 anti-vacuity floors).
  - Wrong instrument, replaced: F-map-render-015-02 (the intent COMMENT and the
    `5.0); return; }` shape swapped for a brace-matched defer block that refuses a
    `PruneStaleState` call - control proof: the 5.0 -> 10.0 edit is now GREEN),
    F-ghost-playback-024-02 (900-character window -> nearest preceding Failed check in the
    call's OWN method body, with no other notify between check and call),
    F-ledger-career-035-02 (body extraction now ends at the method's closing brace -
    control proof: a `||` written into the NEXT member's doc comment is now GREEN).
  - Source pin -> behaviour: F-catchall-006-01 (`AtomicMarkerWrite` driven; the marker's
    `RewindPointUT` must equal `rp.UT` and differ from `InvokedUT`), F-recording-tree-031-01
    (a corrupted branch-point graph past the cap: partial list + the cap Warn),
    F-recording-tree-031-02 (`MergeCommit` driven with an armed session + restore attempt;
    both markers cleared), F-recording-tree-047-01 (`LoadRewindStagingState` driven by
    reflection with a node carrying neither marker), F-catchall-011-01 (`SplitAtUT` driven:
    the committed split drops the head's section annotations, a guarded return keeps them -
    the mirror direction, which is why the opt-out exists).
  - DELETED in favour of a twin: F-legacy-bugfix-024-02's five-step cross-file grep chain,
    superseded by `Bug278SnapshotPersistenceTests.SaveRecordingFiles_NullVesselSnapshot_`
    `LeavesExistingVesselCraftOnDisk` (landed with C-legacy-bugfix-024-01). Only the negative
    pin naming the removed `File.Delete(vesselPath)` is kept, now read from stripped source.
    The wrapper layer `SaveRecordingFiles` is therefore spelling-pinned only: the twin drives the
    shared body through the explicit-paths seam, so a differently spelled delete in the wrapper is
    caught by nothing until a wrapper-level cell exists (see C-legacy-bugfix-024-01's note).
  - Flakes: F-logging-001-03 and F-logging-002-02 bracket `gcGen0Baseline` between reads taken
    either side of Reset instead of comparing it to a post-hoc `GC.CollectionCount(0)`;
    both still red when a counter is omitted from Reset.
  - Leaks: F-catchall-002-01 restores `GameStateStore.SuppressLogging` in both finally blocks
    (probe RED without it). F-catchall-059-02's premise is WRONG and is recorded as such: the
    class's `Dispose` already calls `RecordingStore.ResetForTesting()`, which itself resets
    `RewindContext`, so `IsRewinding` never leaked. The explicit `RewindContext.ResetForTesting()`
    is kept in ctor and Dispose so the guard no longer rides on a foreign type's reset, and the
    probe reds only when both resets are removed.
  - Both leak rows were proved with the same temporary in-process probe PR #1696 used
    (`PhaseBLeakProbe`, ctor -> cell -> Dispose, asserting the static). Scaffolding, not
    committed.

- `testfix-t1t2`, second PR (2026-09-15): the first slice of Medium T1 rows
  (`work/phase-b-slice-medium-t1-01.txt`, 20 ids, rewind / Re-Fly + recording-tree).
  Each fixed row has a proof row in
  `research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`.
  - Fixed: F-rewind-refly-001-01 / -001-03 / -001-04 / -001-05 (branch-point fixtures given
    `ParentRecordingIds` in the Re-Fly target's lineage, plus a real `tree_b` for the tree-id
    guard, so the baseline / cutoff / type / tree-id term each becomes the sole discriminator);
    F-rewind-refly-004-01 / -004-02 (the no-op re-apply cells now arm committed owner, replay
    scope and a droppable supersede row, and the empty-owner-id cell asserts the ABSENCE of the
    owner-not-found log line that the fallback branch would emit); F-rewind-refly-006-01
    (a milestone inside the cutoff window, so a read of the cleared global moves the balance);
    F-rewind-refly-009-01 (fixture given a recorded terminal orbit plus a control assertion that
    it retains WITHOUT the marker); F-rewind-refly-010-05 (armed replay-target id points outside
    the list, so only the pid / null-tree fallback can mark); F-rewind-refly-011-02 (origin made
    `CommittedProvisional`, so only the supersede walk reaches an Immutable endpoint);
    F-rewind-refly-014-02 (`RecordingStore.BeginRewindForOwner` made `internal` - visibility only -
    and the cell drives it instead of re-issuing `RewindContext.BeginRewind` inline).
  - Fixed (second half): F-rewind-refly-016-01 (the death row is now IN the list handed to
    `TryPairBundledRepPenalty`, as the production caller does, so the `RecordingId` equality check is
    the discriminator); F-rewind-refly-016-02 (`ReputationPenaltySource.Other` at exactly
    `BundledRepUtWindow`, plus a just-outside sibling, so the inclusive boundary is actually
    evaluated - a `KerbalDeath` source short-circuits on the source arm and never reaches it);
    F-rewind-refly-018-01 (the selected slot's recording and live vessel now agree on a conclusive
    launch guid, so the source arm WOULD name the pid and only the `SlotIndex` skip stops it);
    F-rewind-refly-020-01 / -020-02 (the bug #134 clear and the `OnFlightReady` cleanup gate are
    extracted as `RecordingStore.ClearPendingCleanupAfterRewindStrip` /
    `ShouldRunPendingCleanupOnFlightReady` - behavior-identical, called from
    `ParsekScenario.HandleRewindOnLoad` and `ParsekFlight.OnFlightReady` - and both cells drive them
    instead of re-implementing the clear and the gate expression in the test body; the log-format
    cell in the same class now asserts the line the production clear emits);
    F-recording-tree-010-01 (a real `Limbo` -> `CommitPendingTree` -> `Finalized` round trip, where
    the old body reset the store first so only the null-pending guard ran);
    F-recording-tree-011-01 and -011-02 (the two test-local mirrors `ComputeIsRevert` /
    `ComputeLimboDispatch` are deleted; production grew `ParsekScenario.ComputeIsRevertOnLoad` and
    `ParsekScenario.ClassifyLimboDispatch` + `LimboDispatchOutcome`, both called from `OnLoad`, and
    the cells drive those. The truth tables shrink to the decisions that exist: the pre-#434
    epoch / count / orphaned-limbo clauses are gone from the revert decision, and the dispatch has
    no revert outcome because the branch above `OnLoad`'s Limbo block has already discarded the
    pending tree. `hasOrphanedLimboTree` keeps its own coverage in the `HasOrphanedLimboTree_*`
    cells, which drive the real `TryRestoreActiveTreeNode`).
  - Deleted: F-rewind-refly-014-04, whose `currentFunds + (baseline - currentFunds)` is `baseline`
    for every input. Twin: `RewindUtCutoffTests.FundsSpending_CutoffFiltersLaterSpending`, which
    pins that a spend after the cutoff is not deducted - the same contract, on the ledger recalc
    that now performs the correction. Its one unique assertion (the positive arm of the
    committed-cost sign convention) survives as
    `RewindLoggingTests.FullCommittedCost_SignConvention_PositiveMeansSpent`.

- `testfix-t1t2`, third PR (2026-09-15): the second slice of Medium T1 rows
  (`work/phase-b-slice-medium-t1-02.txt`, 20 ids, all recording-tree). Each fixed row has
  a proof row in `research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a
  `*-phaseB.patch`.
  - Fixed: F-recording-tree-050-01 / -050-02 / -050-03 (the three `BackwardCompat_*`
    nodes are stamped with `RecordingStore.CurrentRecordingFormatVersion` /
    `CurrentRecordingSchemaGeneration`, so `LoadRecordingFrom` passes the schema gate and
    the asserted null / false / zero is the loader's own default; each cell also asserts
    the recording was not rejected); F-recording-tree-035-04 / -035-05 (both
    session-priority cells now pass the session's own focused pid as the target, the one
    input where Case A answers `SkipDialogSameTarget` and the no-session arm answers
    `OpenDialog`, so reordering the arms reds them); F-recording-tree-048-01 / -048-02 /
    -048-03 (renamed to the guards their inputs actually reach -
    `IsLiveReFlyCrew_NullKerbal_False`,
    `ActiveVesselMatchesReFlyRecording_NullVessel_False`,
    `ActiveVesselMatchesReFlyRecording_NullVessel_UnknownMarkerId_False` - with the false
    comment about an `IsSuppressed` lookup dropped; the marker-null and unknown-id cases
    need a live `Vessel` and stay with the in-game `KerbalDualResidenceCarveOutTest`).
  - Deleted: F-recording-tree-050-06 (`ContinuationVesselDestroyed_SnapshotPreservedForRevertSpawn`
    replayed the revert inline; twin
    `ResetAllPlaybackState_ClearsVesselDestroyed_SpawnEligibleAfter` in the same class
    drives `RecordingStore.ResetAllPlaybackState` and now carries the deleted cell's
    snapshot assertion); F-recording-tree-052-04 (`ZeroPid_NoEffect`; the `pid == 0`
    short-circuit returns what every path below it returns, so no unit test can pin it -
    twin `CommitFlowTests.ShouldSkip_ZeroPid_ReturnsFalse` keeps the documentation).
  - Fixed (second half): F-recording-tree-011-03 (production grew
    `ParsekScenario.ComputeHasOrphanedLimboTree`, called from `OnLoad`'s revert-detection
    log line, and the three `HasOrphanedLimboTree_*` cells drive it instead of re-deriving
    the expression); F-recording-tree-020-01 (the re-commit is now
    `RecordingStore.ApplyRewindProvisionalMergeStatesForTesting`, since a second
    `CommitTree` returns at the reference-equal duplicate skip before the promotion pass
    the cell is named for); F-recording-tree-021-01 (the mapping half of
    `CleanUpReplacement` is extracted as
    `CrewReservationManager.TryRemoveReplacementMappingPreservingRescueMarker`, called by
    production and by the xUnit seam, so the marker-preservation contract is pinned on the
    code the game runs; the roster half still needs a live `KerbalRoster`);
    F-recording-tree-036-01 (a real `KerbalsModule` is installed and the cell asserts
    `CrewReservationManager`'s own "Recomputed after tombstones" line, not the caller's);
    F-recording-tree-039-01 (`RecordingPaths.SaveRootOverrideForTesting` lets the cell drive
    the REAL `RecordingStore.DeleteRecordingFiles` against the staged temp save, so the
    eighth limb - the quickload-resume save - is behaviourally covered for the first time,
    with the RewindPoints quicksave as the blast-radius witness);
    F-recording-tree-042-03 (the postChoice closure body is extracted as
    `SceneExitInterceptor.RunPostChoice(destination, loadScene)` - same guard, same order of
    side effects - and two cells drive it: the token is read INSIDE the injected load, and a
    failed persist arms nothing and loads nothing; the original cell keeps the
    not-armed-until-invoked claim under that name);
    F-recording-tree-046-06 (the marker-owned segment is now IN the armed attempt tree, the
    shared-id case, plus a `markerOwned=2` assertion and a fixture precondition);
    F-recording-tree-051-01 (production grew `GhostChainWalker.ShouldGhostChainAtUT`, called
    by `ParsekFlight.FilterAndGhostChains`; the test-local mirror is deleted and all three
    cells drive the production rule. The terminated-chain arm stays at the call site because
    it logs its own reason).
  - Renamed, behavioural coverage deferred: F-recording-tree-034-01 and -034-02, the two
    batch-flight-baseline SIMULATION cells. The runner's step order lives inside
    `InGameTestRunner.RestoreBatchFlightBaselineCore`, which needs a live KSP batch
    (quicksave + `GamePersistence` load), so no xUnit mutant of that sequence can red them;
    they are now `Simulation_ValidationFailureBeforeWipe_DocumentsIntendedSequence_RecordingStoreOnly`
    and `Simulation_PostTestRollback_RestoresFromTheBatchStartSnapshot_NotTestMutations`,
    each saying in the body what it cannot witness and naming the in-game batch as the
    detector. -034-02 carries a mutation proof against the store primitive it does pin
    (`RecordingStore.RestoreFromSnapshotForTesting`); -034-01 has no mutant by construction
    and is the one deferred row of the twenty.

- `testfix-t1t2`, sixth PR (2026-09-15): the fifth slice of Medium T1 rows
  (`work/phase-b-slice-medium-t1-05.txt`, 20 ids: 5 trajectory-orbit, 4 map-render,
  3 harness-seam, 2 mission-groups, 2 wiring-gates, 2 legacy-bugfix, 1 analyzer,
  1 logging). 19 fixed, 1 deferred, 0 deleted. Each fixed row has a proof row in
  `research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`
  that `git apply --check`s against a clean tree.
  - Fixed, assertion re-pointed at the branch the name claims: F-map-render-009-02 (the
    reason must carry `exceeds cap 10000` and NOT `would need`, the only wording the
    stream-length branch below the cap cannot produce); F-trajectory-orbit-011-01 (both
    candidates given the same `ghostIndex` so the source comparison is the sole
    discriminator, plus the mirror list order); F-trajectory-orbit-017-01 (the Sun
    segment given the SAME `semiMajorAxis` as the Kerbin loiter and a span of many of its
    OWN periods, so the 5% a-step guard cannot pre-empt the `bodyName` guard; the merged
    shape is one cut, the split shape two); F-trajectory-orbit-018-01 (the adapter's
    `StartUT` / `EndUT` are read against a non-zero recorded span, over the null AND the
    empty assembled list); F-mission-groups-014-01 (a FILTERED, reordered view over three
    single-group recordings - over a full permutation `ri = row` visits the same pairs and
    is a no-op); F-analyzer-002-01 (renamed
    `Apply_EmitsMetaFindingsInSortedKeyOrder`: entries inserted in reverse ordinal order
    and the emitted MULTI-MATCH / STALE-ENTRY sequences pinned, since two in-process runs
    share one insertion order); F-wiring-gates-003-01 / -003-02 (the two tautologies -
    a returned field equal to its own argument, and an untouched coalescer - replaced by
    the child wiring `BuildSplitBranchData` derives: ids, generations, branch-point
    parentage, start UT, and the EVA kerbal-by-pid branch in BOTH directions; renamed
    `UndockSplit_DerivesChildWiringFromTheSplit` and
    `EvaSplit_KerbalChildIsPickedByPid_BothDirections`); F-legacy-bugfix-009-01 (every
    exit but the attach returns false, so the guard is pinned by the ABSENCE of the
    fall-through hydration Verbose / missing-from-BOTH Warn, with the committed store
    reset so the fixture state is known).
  - Fixed through a production change, both behaviour-identical: F-logging-001-01 (the
    growth-rate update with its zero-elapsed division guard is extracted as
    `FlightRecorder.ComputeGrowthRate`, called verbatim by BOTH commit paths - the block
    was duplicated - and the cell drives it at `elapsedSeconds == 0` plus a dividing
    control arm, instead of hand-filling the struct with the answer); F-map-render-015-01
    (`ShadowRenderDriver.WarnSpineAssemblerFallback` widened private -> internal, so the
    cell drives the set's REAL and only writer: the per-pid one-shot dedupe, a second
    pid, the scene-switch clear, and a re-warn after it. Asserting an empty count made
    both the dedupe and the clear unfalsifiable). No log text changed.

  - Fixed through a production change (second half), all behaviour-identical and all
    called by the original site: F-harness-seam-002-01
    (`InGameTestRunner.ResetLiveStatus(test, clearSceneHistory)` extracted from
    `ResetResults` / `ResetCategory` / `ClearAllSceneHistory` - the flag IS the
    difference between the implicit pre-run reset and the explicit wipe - and both
    reset cells drive it instead of replaying three assignments inline);
    F-mission-groups-011-01 (`MissionChapters.ApplyChapterToggle(chapterKeys,
    excludedKeys, include)` extracted from the chapter checkbox handler, returning the
    changed count the handler logs; the cell no longer performs the
    UnionWith / ExceptWith itself and now also pins the re-exclude no-op);
    F-trajectory-orbit-002-01 (`MissionLoopUnitBuilder.ComputeDescentParkingConicEndUT`
    carries BOTH the `descentRun.EndUT + captureShift` shift and the frame-mismatch
    invariant that fires when the shift is dropped; the old cell asserted
    `ParkingConicEnd == RECORDED_PARKING_END + CapShift` where the right-hand side is
    DEFINED as the left, an identity the 0ba10f594 bug left green).
  - Fixed by moving from a self-performed simulation to a source wiring gate:
    F-trajectory-orbit-013-01, renamed
    `RetireBranch_ProductionCallSites_SetTheFlagAndDedupeTheWarn_SourceGate`. The old body
    added to its own dedupe set, called `ParsekLog.Warn` itself and assigned
    `anchorRetiredThisFrame` itself, so none of the three failure modes it names could red
    it. The pure decisions keep their own cells; the WIRING lives inside `ParsekFlight`
    positioning methods that need a live `GameObject`, so the gate reads comment-stripped
    source and asserts all three `RelativeAnchorResolution.DedupeKey` sites set the flag
    BEFORE the warn is considered and route the warn through `loggedAnchorNotFound.Add`
    ahead of `FormatRetiredMessage`.
  - Fixed, culture cells given teeth: F-harness-seam-014-02 and F-harness-seam-017-01.
    Every payload value is an int or a long, and integer default formatting is identical
    in every .NET culture, so the de-DE swap exercised nothing and both cells would pass
    against a `CurrentCulture` site. Each now also reads the production `BuildPayload`
    body through the new `TestCommandSourceGate` (comments blanked via
    `SourceScanText.StripCSharpComments`, body taken by brace matching, not a character
    window) and pins the provider: `CultureInfo ic = CultureInfo.InvariantCulture;`
    present, `CultureInfo.CurrentCulture` absent, no bare `.ToString()`. The culture swap
    is kept as the behavioural half. `harness/lib` re-run green afterwards.
  - Fixed, frames actually seeded: F-legacy-bugfix-007-01. The section is filled through
    `CurrentTrackSectionForTesting.frames` (TrackSection is a struct but `frames` is a
    shared List reference, so no production change was needed), so the
    `frames.Count > 1` branch runs: 11 frames over 10 s is 1.1 Hz, with the single-frame
    shape kept as the guarded-default control. The old body closed an EMPTY section and
    asserted the struct default.
  - Deferred: F-map-render-012-01, renamed
    `Loop_AnchorPidPositive_DoesNotThrowUnhandled_NoBehaviourWitnessed`. Past the pid != 0
    guard `TryResolveLoopAnchorWorldPos` calls `TryFindVesselByPid` -> `FlightGlobals` and
    there is no injected vessel-lookup seam, so headless xUnit can only observe "false, or
    one of three Unity exceptions" - every possible outcome. The body now says so and
    names the in-game categories as the detector; making it falsifiable needs an injected
    lookup seam, a production change beyond this row. Its sibling F-map-render-012-02 is
    fixed (above) and is the family's discriminator template.

- `testfix-t1t2`, fifth PR (2026-09-15): the fourth slice of Medium T1 rows
  (`work/phase-b-slice-medium-t1-04.txt`, 20 ids: 6 recorder-events, 6 ghost-playback,
  5 spawn-vessel, 2 logistics-route, 1 map-render). 16 fixed, 3 deleted in favour of a
  named twin, 1 deferred. Each fixed row has a proof row in
  `research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a
  `*-phaseB.patch`.
  - Production replay replaced by a call (five files touched, all behaviour-identical):
    F-recorder-events-020-01 (`GhostVisualBuilder.ParseVariantTextureRules` extracted;
    the test-local `ParseTextureRule` copy deleted and all five `TextureNodeParsing_*`
    cells pointed at it); F-ghost-playback-008-02
    (`ParsekFlight.TryStampChainMapOrbit` - same three-term comparison, same field
    writes, same early-out); F-ghost-playback-008-03
    (`GhostMapPresence.EnsureDefensiveVesselNodes`); F-spawn-vessel-020-02
    (`ParsekPlaybackPolicy.ApplySpawnDeathDisposition` + `SpawnDeathDisposition`, with
    the counters and both log lines kept at the call site);
    F-spawn-vessel-004-02 (`BackgroundRecorder.RetireDestroyedBackgroundEntry` widened
    to internal - visibility only - and the cell seeds the pid INTO `BackgroundMap`
    first, so the retirement drains a real entry); F-recorder-events-019-01
    (`GhostMapPresence.IsTerminalMapPresenceRegion` widened to internal and asserted
    both inside and outside the activation region); F-ghost-playback-024-01 (60 real
    `HandleFlightGhostCreatedMapPresence` events instead of a retyped rate key - the
    other two cells in that class keep their inline mirrors, the overlap sites they
    name being Unity-only).
  - Fixture over-determination removed: F-recorder-events-010-01 (three frames over a
    ten-second section pin the 0.3 Hz formula; the zero-frame degenerate case is its
    own cell); F-spawn-vessel-007-02 (asserts the baseline was REACHED -
    `hasSnapshotBaseline` / `spawnValue` / `currentValue` / the `servos=1` summary -
    before asserting the park); F-spawn-vessel-007-03 (arms the rotor first and asserts
    the restored count and the parked value); F-ghost-playback-005-01 (reads
    `BuildInvocationCountForTesting`; the build log is rate-limited and matches on both
    paths); F-ghost-playback-002-01 (renamed: a negative phase offset DEFERS the
    schedule, so the cell pins `false` + no cycle rather than the unreachable
    `cycleIndex < 0` clamp); F-recorder-events-022-01 (renamed: asserts the flag VALUE
    and the caller's UT, so a pass-through stub fails; the two-vessel half of the section-12
    contract needs live `Vessel`s and stays with the in-game batch);
    F-map-render-009-01 (a stale `<path>.tmp` is pre-created and the safe-write must
    consume it); F-ghost-playback-011-01 (the hold cell keeps its claim - its transfer
    declines on the ghost-null guard one step earlier - and a new sibling
    `FindNextWatchTarget_UndockBranchDebrisOnlyChild_ReturnsNoTarget` pins the debris
    exclusion with a non-debris control arm, the breakup branch being blocked by the
    #321 rule before `IsDebris` is read); F-logistics-route-008-01 (the delivered row's
    funds field sits after the live-Vessel resolution both loop seams stand in for, and
    the cost is PartLoader-priced, so the cell pins the production career-KSC arm
    through a `KscDispatchFundsCost` sentinel the arm must overwrite - the mutant is
    that write, not the row field the register named).
  - Deleted in favour of a twin: F-recorder-events-021-01
    (`TreeCreation_BackgroundMap_PopulatedCorrectly`; twin
    `TreeCreation_RebuildBackgroundMap_MatchesManualSetup` drives the real
    `RebuildBackgroundMap`), F-logistics-route-020-01
    (`SupersededRecovery_ExcludedByElsInput`; twin `RecoveryFromDifferentTree_Excluded`
    passes the unwanted row in instead of pre-filtering it), F-spawn-vessel-019-01
    (`RegeneratePartIdentities_MultipleParts_LogNotEmpty`; twin
    `RegeneratePartIdentities_MultipleParts_EachGetsUniqueIds`).
  - Deferred: F-recorder-events-024-02. `OnBackgroundPartJointBreak`'s dedup guard needs
    a live `PartJoint` carrying a Child `Part` with a `Vessel` and an `attachJoint`; the
    cell is renamed to the set-level claim it can make and names the in-game
    BackgroundRecording category as the detector.

- `testfix-t1t2`, fourth PR (2026-09-15): the third slice of Medium T1 rows
  (`work/phase-b-slice-medium-t1-03.txt`, 20 ids: 16 ledger-career, 4 recorder-events).
  18 fixed, 2 deleted (twin named), 0 deferred. Every fixed row has a proof row in
  `research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`.
  No production file changed in this slice.
  - Fixed: F-ledger-career-001-07 (the duplicate milestone moved onto the live event's own
    UT with a divergent award, and it is now the ONLY action in the list - a second,
    effective milestone in the same window makes both rows ambiguous-coverage skips and
    hides the Effective gate again, which is why the register's two-action shape does not
    red); F-ledger-career-002-01 (a divergent `FundsChanged(Progression)` at the action's
    UT plus an assertion on the emitted `ut is at/below live prune threshold=650.0` skip
    line); F-ledger-career-002-02 (the absence assertion now matches the prefix the
    per-action path really writes, `KSC reconciliation (`, and the silent path is
    positively witnessed by its `KSC reconciliation: ContractComplete skipped` verbose
    line - the mutant has to give the Transformed classification a funds leg, since the
    skip arm it names carries no legs at all); F-ledger-career-002-03 (non-zero
    `TransformedFundsReward` / `EffectiveRep` / `TransformedScienceReward` behind the
    `Effective=false` gate); F-ledger-career-006-01 (asserts
    `PendingRecoveryFundsCountForTesting == 0` and the guard's own verbose line, so a
    silent defer no longer reads as a skip); F-ledger-career-007-01 / -007-02 / -007-03
    (the coalescing fixtures now vary ONLY the subject under test - same key across the
    window boundary, the same key on both sides of the recovery barrier, the same key on
    two non-resource rows); F-ledger-career-008-03 (renamed
    `OnScienceReceived_SuppressResourceEvents_ReturnsBeforeAnyCapture` and reflection-drives
    the real private handler, whose suppression early-return precedes every KSP read; the
    max-wins half it duplicated stays in `CommitScienceSubjects_MaxWins` /
    `CommitScienceSubjects_LowerValueIgnored`); F-ledger-career-010-01 (a fresh milestone
    id, so the AlreadyCredited arm cannot stand in for the Effective arm);
    F-ledger-career-010-02 (MissionControl 2 vs Administration 3, so a crossed key read
    moves both the echoed level and the slot count); F-ledger-career-017-01 (a standing rep
    balance plus every rep-bearing field set non-zero, `HasSeed` asserted false, and
    `StrategyActivate` dropped from the ignored list because `ProcessAction` routes it to
    `ProcessStrategySetupReputation`); F-ledger-career-029-01 (the roster facade records
    every append / create / recreate CALL, refused or not, so a dropped skip is an
    attempt rather than a refusal the fixture configured); F-ledger-career-030-01 (reads
    `KerbalLoadRepairDiagnostics.CurrentForTesting.RetiredStandInsRecreated` / `Kept`
    before `EmitAndReset`, so a false record reds without depending on the summary gate);
    F-recorder-events-001-01 (a real `SerializeTrajectoryInto` / `DeserializeTrajectoryFrom`
    round trip instead of a hand-rolled PART_EVENT parse); F-recorder-events-003-02 (drives
    `VesselSpawner.RemoveDuplicateCrewFromSnapshotCore` over a snapshot with no live
    holder; its mutant is a fail-open `FindDuplicateCrew`, because a no-op stub of the
    removal cannot red a no-removal cell); F-recorder-events-007-01 (inputs UNDER the
    visibility floor, `Assert.Equal(60f, rate)`); F-recorder-events-008-01
    (`hasActiveTree: true`, so `isActiveVessel=false` is the only reason `None` returns).
  - Deleted: F-ledger-career-028-01
    (`ResolveFundsPatch_LeakClamp_FeedsTheGuardWarnLogWithClampedValue`; `PatchFunds`
    early-returns on the null KSP singletons headlessly, so the cell re-issued
    resolve-then-emit and supplied `clampedTo` itself. Twins:
    `ResolveFundsPatch_MissingEarningLeak_ClampsToLiveAndCollapsesToNoOp` in the same class
    and `DrawdownGuardTests.EmitDrawdownGuardClamp_Funds_WarnsWithNumbersAndToastsOnce`);
    F-ledger-career-028-02
    (`ResolveMissingSubjectCreation_CreatedSubjectFeedsTheNormalPatchDecision`; the
    composition lives in `PatchPerSubjectScience`, which needs a live R&D table, and the
    cell performed the feed inline with a hardcoded `currentScience: 0`. Twins:
    `ResolveMissingSubjectCreation_LedgerCreditsSubjectRnDLacks_Creates` and
    `ResolveSubjectSciencePatch_ScientificValueFromCap`. The composition itself is in-game
    work). Both deletions leave a comment at the site naming the twins.

- `testfix-t1t2`, seventh PR (2026-09-16): the FIRST slice of Medium T3 rows opens the
  T3 (weak / misleading) wave (`work/phase-b-slice-medium-t3-01.txt`, 20 ids: 12
  `rewind-refly`, 8 `recording-tree`). Every T3 row already runs the production line;
  the work is making the named term the DECIDING one. Each fixed row has a proof row in
  `research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a
  `*-phaseB.patch` that `git apply --check`s against a clean tree.
  - The twelve `rewind-refly` ids, all strengthened, no production change:
    F-rewind-refly-001-02 (the legacy marker's BP now carries `ParentRecordingIds` in
    the Re-Fly target's lineage, so the null baseline is the only term that can keep the
    gate shut - previously lineage excluded it either way);
    F-rewind-refly-002-02 (new mirror cell
    `InPlaceContinuationSlotLookupFailure_OrbitingTerminal_ExemptFromSlotAwareAbort`:
    Landed never sets `RequiresSlotAwareMergeClassification`, so the original cell could
    not witness the in-place exemption from the abort; Orbiting is the one terminal that
    does);
    F-rewind-refly-003-01 (the competing chain-head gate is ARMED - committed TIP,
    `SupersedeTargetId`, shared `ChainId`/`ChainBranch` at a lower `ChainIndex` - and a
    new mirror cell drives the identical shape with `IsDebris=false` and must get
    `PreRewindChainHead`. The comment's block-ORDER rationale was false: the two
    branches gate on `IsDebris` and `!IsDebris` and are mutually exclusive by type, so
    the comment now says that instead);
    F-rewind-refly-004-03 (the supersede relation is re-staged between passes, as the
    cross-`LoadScene` `.sfs` restore does, so the third pass actually reaches the
    `seenRetiredIds` duplicate guard the comment named instead of stopping at the
    empty-list early-out);
    F-rewind-refly-007-03 (`IsUnfinishedFlight` admits a recording whose slot anchor
    cannot resolve, so it stays true for a pruned origin; the cell now asserts store
    membership and RP survival, which IS the row's visibility gate).
  - The spawn and dialog half: F-rewind-refly-010-01 and -010-02 (both cells left
    `TerminalStateValue` null, so `hasSpawnableTerminal` collapsed `effectiveLeaf` and
    the plain `ChildBranchPointId` gate answered before `IsEffectiveLeafForVessel` or
    the passed tree context was consulted; both now carry a spawnable terminal, and
    -010-02 gains the positive sibling
    `ShouldSpawn_DifferentPidChildInPendingTreeContextOnly_SpawnsAsEffectiveLeaf` - the
    only direction in which the passed context can change the verdict, since the
    same-PID shape answers False whether or not the context resolves);
    F-rewind-refly-012-01 (the whole headline, formatted duration and closing tag
    included, instead of the `MyShip - ` prefix an empty duration also satisfies);
    F-rewind-refly-012-02 (the same throwing-classifier route is driven a second time
    with a SEALING preview, so the fallback's return value is discriminated rather than
    matching a hardcoded `false`);
    F-rewind-refly-017-01 (the before-dispatch half is now witnessed by
    `Assert.DoesNotContain` over the first three dispatch targets' own log lines; the
    trailing `PatchAll complete` line is absent for a relocated guard too).
  - The two `sut=test-infrastructure` fixture rows, strengthened against the OTHER side
    of the derivation: F-rewind-refly-016-03 (the RP map key is compared against the
    `vesselPersistentId` the INJECTED recording carries, since
    `ScenarioWriter.BuildRecording` has its own `StableHashToUint` call site) and
    F-rewind-refly-016-04 (the cell loads `Parsek/RewindPoints/rp_cl_root.sfs` and
    asserts the pod slot's VESSEL `pid` equals the recording's `recordedVesselGuid` -
    the pair `QuickloadResumeMatchGuard` compares - mirroring
    `RewindB9FixtureTests.Inject_RpSidecarVesselGuidsAgreeWithRecordedVesselGuid`).
    Their mutation patches are over `Source/Parsek.Tests/Generators/ScenarioWriter.cs`,
    the code under test for these two rows.
  - The eight `recording-tree` ids close the slice: 6 strengthened, 2 renamed AND
    strengthened, 0 deferred, 0 deleted. Still no production change.
    F-recording-tree-002-01 and -002-02 are the two renames
    (`CanAutoSplitIgnoringGhostTriggers_TreeIdIsNotAGate_SameVerdictAsCanAutoSplit`
    and `FindSplitCandidatesForOptimizer_ExoToAtmoBoundary_TreeIdIsNotAGate`):
    `RecordingOptimizer` reads `Recording.TreeId` NOWHERE, so "allows tree recordings"
    / "finds tree recordings" named a property no fixture in that file can witness.
    Each cell keeps its original assertions, gains an identical no-`TreeId` control and
    (for -002-01) the base `CanAutoSplit` agreement, so a newly added tree skip reds the
    tree arm and leaves the control green. The pass-level tree-split contract stays with
    `RunOptimizationPass_SplitsMultiEnvRecording` as the register noted.
    F-recording-tree-003-01 and -003-03 (the two loop-sync cells asserted
    `LoopSyncParentIdx == -1` against a one-element list, which is also the field's
    default; each now holds the partner the candidate scan WOULD accept - non-debris,
    different pid, covering the subject's `StartUT`, and for -003-03 carrying the SAME
    null `TreeId`, since the scan compares the two ids for EQUALITY: a partner with a
    real tree id could never link and would leave the guard unwitnessed, which is where
    the register's sketch would have gone wrong).
    F-recording-tree-003-04 (exact `EndUT == 17060` - the previous `>= 17050 &&
    <= 17060` window is satisfied by a ZERO buffer too).
    F-recording-tree-007-01 (the reason half of the `(recordingId, failureReason)`
    rate-limit key is now driven with TWO further non-`NullSolver` reasons on the SAME
    recording inside the window. Note the register's sketch of one `MissingPatchBody`
    hit would NOT have red'd: `WarnRateLimited` keeps its own key store behind a `W|`
    prefix, so the `NullSolver` verbose floor can never suppress a WARN - it takes two
    WARN-arm reasons sharing the recording id to make the key's reason segment load-bearing).
    F-recording-tree-008-01 (renamed
    `MergeTree_SameAnchorRelativeBoundary_MeasuresAnchorLocalDiscontinuity`, since
    `MergeTree` only forwards the format version to `MeasureRelativeAwareBoundary` /
    `TryGetBodyFixedBoundaryPoint` and neither body reads it. The register's suggested
    `Assert.Equal(CurrentRecordingFormatVersion, merged.RecordingFormatVersion)` is a
    TAUTOLOGY - `Recording.RecordingFormatVersion` is initialised to that constant, so
    deleting the copy in `SessionMerger` leaves it green - so the copy is pinned by a new
    sibling, `MergeTree_CopiesTheSourceRecordingFormatVersion_NotTheFieldDefault`, which
    stamps `CurrentRecordingFormatVersion + 7`).
    F-recording-tree-013-01 (the cell now reads `WriteBinaryTrajectoryFile`'s own
    accounting line and pins `sparsePointLists=2`, `sparsePoints=6` and the four
    `omitted*` counts; the probe asserts encoding and version only, which is why
    disabling `BuildSparsePointListPlan` used to leave it green).
  - Slice total: 20 of 20 addressed - 18 strengthened, 2 renamed and strengthened, 0
    deferred, 0 deleted. Three sibling cells were added on the `rewind-refly` half and
    two on the `recording-tree` half, each carrying the mirror or positive direction the
    original cell could not reach.

## July crosswalk

`research/test-quality-audit-2026-09-14/july-crosswalk.csv` maps every July register ID (42 rows: A1-A7, B1-B8, C1-C6, D1-D5, and Tier E numbered E1-E16 in source order) to the SUT or file it names and to the D2/D3 rows here that touch the same SUT. `status_now` is judged from the xUnit tree only and says `unknown` for harness and in-game items this audit cannot decide (closed 15, unknown 19, open 7, superseded 1). 49 findings and 14 coverage proposals carry a `july_ref` / `dupe_of_july_id`; for those the July ID stays primary and this audit adds evidence.

Still open at the baseline (grep-verified, zero or self-only test references): E5 `ReFlyCanonicalization`, E11 career-spend-blocking patches (`TechResearchPatch` / `FacilityUpgradePatch`), E12 `RouteDispatchDecision` factory, E13 `ReaimOrbitSegmentConverter`, D3/E16 `MergeCrashRecoveryMatrixTests` (Facts only, no Theory matrix), C5 `WithSpawnedPid`. A2 (crew-death chain) is closed on the unit leg only; D5 (mutation adequacy) is superseded by this audit's Phase 2 protocol.

| July ID | Title | Now | Findings here | Proposals here |
|---|---|---|---|---|
| A1 | FileIOUtils.SafeWriteConfigNode destroys the original on a failed ConfigNode.Save; no test | closed | F-map-render-009-01 | C-io-serialization-004-01 |
| A2 | Crew-death -> ledger row -> tombstone -> rep-penalty chain has zero automated proof at any | closed | F-rewind-refly-016-01, F-rewind-refly-016-02, F-ledger-career-001-07, F-ledger-c | C-rewind-refly-016-01, C-rewind-refly-016-02 |
| A3 | Schema-reject -> PruneRejectedRecordingReferences -> save -> quarantine chain untested end | closed | F-ui-settings-003-02 |  |
| A4 | RecordingTreeSplitter (HEAD/TIP split) never executed live; D9 head-tip-split unowned | closed |  | C-rewind-refly-008-01, C-recording-tree-023-01, C-recording- |
| A5 | S4.1-IDLE-DISCARD: live re-fly session tree destroyed by idle auto-discard, no regression  | closed |  |  |
| A6 | Rewind across an SOI boundary: zero tests anywhere | unknown |  |  |
| A7 | Foreign/unmodelled currency delta through a rewind (drawdown guard bypassed under IsAuthor | unknown | F-ledger-career-004-02, F-catchall-022-01 | C-catchall-022-01 |
| B1 | No spec forbids any raw Unity exception pattern | unknown |  |  |
| B2 | STOCK_AWARD_PATTERNS dead against real KSP logs | unknown |  |  |
| B3 | icon-jump dead token; icon-teleport/icon-off-orbit ungated; no count budgets | unknown | F-map-render-004-01, F-legacy-bugfix-006-01, F-map-render-008-01, F-map-render-0 | C-map-render-004-01, C-map-render-004-02 |
| B4 | 52/55 specs tracer-off; ghostRenderTracing armed by zero specs; GhostRenderTrace emits no  | unknown | F-ghost-playback-017-01 |  |
| B5 | B4 chuteDeployed commanded latch | unknown |  |  |
| B6 | ~19 silent early-return PASS sites in in-game tests | unknown |  |  |
| B7 | ERS/ELS grep-gate holes (CommittedTrees unpoliced, partial-class escapes, silent pwsh skip | unknown |  |  |
| B8 | Log expectations cannot express counts/ordering; recording assertions are one integer wind | unknown |  |  |
| C1 | ~68 reachable-but-undriven in-game categories | unknown |  |  |
| C2 | S1.5 + S4.1 unattended | unknown |  |  |
| C3 | modded-compat instance provision + one spec | unknown |  |  |
| C4 | TS scene route -> 9 stranded TrackingStation tests + 8 untested TS patches | unknown |  |  |
| C5 | WithSpawnedPid corpus fixture change -> CrewReservationLive + SpawnHealth cells | open |  |  |
| C6 | Marginal tokens on already-flying scenarios | unknown |  |  |
| D1 | Property/fuzz testing for the 9 ledger modules and the supersede/chain/closure walkers | closed | F-ledger-career-016-01, F-ledger-career-016-02 |  |
| D2 | Perf/scale: no recording-length budget, ComputeERS cost unmeasured, no soak/memory test | unknown |  |  |
| D3 | Crash-matrix completeness: hand-picked journal fault Facts, Split has no fault test, one-p | open | F-rewind-refly-019-01, F-rewind-refly-009-02 |  |
| D4 | Visual validation: zero screenshot capability, 14/15 windows undrawn, playback/watch verbs | unknown |  |  |
| D5 | Mutation adequacy: no mutation tooling anywhere; every cell-bites claim is prose | superseded |  |  |
| E1 | FlightRecorder.ProcessRcsDebounce (pure, zero tests) | closed |  |  |
| E2 | Reflection-classifier interpretation split | closed | F-catchall-035-01 |  |
| E3 | BallisticExtrapolator Kepler core vs closed form | closed | F-trajectory-orbit-008-01 |  |
| E4 | RecordedRelativeAnchorPoseResolver with injected body-pose provider | closed |  |  |
| E5 | ReFlyCanonicalization round-trip under 1 mm | open |  |  |
| E6 | Spawn-rotation family vs the two-rotation contract | closed |  |  |
| E7 | TryPassTerminalOrbitSpawnSafety vs the BUG-C geometry | closed |  |  |
| E8 | RP-slot ambiguity detection | unknown | F-recording-tree-032-02 |  |
| E9 | KerbalsModule end-state suite | closed | F-ledger-career-029-01, F-ledger-career-030-01, F-ledger-career-037-01, F-ledger | C-ledger-career-015-01, C-ledger-career-015-02, C-ledger-car |
| E10 | PostWalkActionReconciler per-case suite (rep-source mapping) | closed | F-ledger-career-001-07, F-ledger-career-002-01 |  |
| E11 | Career-spend-blocking patch decision extraction | open |  |  |
| E12 | RouteDispatchDecision hold-reason vocabulary | open |  |  |
| E13 | ReaimOrbitSegmentConverter deg/rad round-trip | open |  |  |
| E14 | Sidecar-codec heal family | closed | F-recorder-events-001-01, F-io-serialization-004-01, F-io-serialization-004-02,  | C-recording-tree-013-01 |
| E15 | RouteProofMetadata clone helpers | unknown |  |  |
| E16 | MergeCrashRecoveryMatrixTests to a Theory over the phase enum | open |  |  |

## Appendix

### File map of the research directory

`docs/dev/research/test-quality-audit-2026-09-14/`:

| Path | Contents |
|---|---|
| `rubric.md` | the frozen review rubric, embedded verbatim in every Phase 1 agent prompt |
| `calibration-2026-09-14.md` | the three-way pilot agreement measurement and the two protocol repairs |
| `phase1-summary.md` | Phase 1 totals, per-cluster table, stop-condition check, known gaps |
| `phase2-summary.md` | mutation results for the 7 High, the 27-finding Medium sample outcome |
| `phase2-verdicts.csv` | one row per verified finding: status, evidence, note, patch path |
| `test-inventory.csv` | **D1**, 20,225 rows, one per test method; written only by `merge_fragments.py` |
| `issue-register.csv` | **D2**, 768 findings |
| `coverage-opportunities.csv` / `.md` | **D3**, 249 rows / the reviewed narrative with per-cluster tables |
| `coverage-proposals.csv` | the raw Phase 1 coverage candidates before Phase 3 triage |
| `file-summary.csv` | per-file stats for all 1,010 files (size, tier, cluster, counts, smell flags) |
| `file-batches.csv` | the 426 batch manifests with file lists and method ranges |
| `coverage-by-class.csv` | Phase 0 coverlet output joined per class |
| `findings/` | 427 committed per-batch JSONL fragments (the only source D2/D3 were merged from) |
| `findings.csv` | the flat merge of every finding record in `findings/` |
| `mutations/` | 7 committed patches plus `mutations.csv` (the Phase 2 run log) |
| `july-crosswalk.csv` | July register ID -> SUT/file mapping, built in Phase 4 |
| `tools/` | `inventory_scan.py`, `parse_results.py`, `lint_fragments.py`, `build_batches.py`, `merge_fragments.py`, `make_workorders.py`, `agent-protocol.md`. The plan's `smell_sweep.py` / `redundancy.py` (P0.5 hash-tier redundancy) were NOT built: redundancy was agent-confirmed per method under the rubric's twin rule instead, so no hash-only T2 exists and `work/redundancy-calibration.csv` does not exist |
| `work/` | committed except the large generated inputs (see `research/test-quality-audit-2026-09-14/README.md`): `baseline.trx`, `coverage.cobertura.xml`, `durations.csv`, `metrics.md`, `supervisor-notes.md`, `inventory-completeness.txt`, the Phase 2/3/4 scratch |

### How to regenerate

From the audit worktree root, after editing or re-emitting a fragment under `findings/`:

```bash
python docs/dev/research/test-quality-audit-2026-09-14/tools/lint_fragments.py \
  --manifests docs/dev/research/test-quality-audit-2026-09-14/work/manifests \
  --fragments docs/dev/research/test-quality-audit-2026-09-14/findings \
  --all --report docs/dev/research/test-quality-audit-2026-09-14/work/lint-report.txt

python docs/dev/research/test-quality-audit-2026-09-14/tools/merge_fragments.py \
  --inventory docs/dev/research/test-quality-audit-2026-09-14/test-inventory.csv \
  --july-refs docs/dev/research/test-quality-audit-2026-09-14/work/phase4/july-refs.jsonl \
  --fragments docs/dev/research/test-quality-audit-2026-09-14/findings \
  --out docs/dev/research/test-quality-audit-2026-09-14
```

`lint_fragments.py` also takes `--batch <id>` (repeatable) to lint one batch and `--suffix` for
pilot runs. `merge_fragments.py` is the ONLY writer of `test-inventory.csv`, `findings.csv`, `issue-register.csv`
and `coverage-proposals.csv` (the committed `test-inventory.csv` is a valid input to itself; `--july-refs` restores the July
cross-references, which live in `work/phase4/july-refs.jsonl`, not in the fragments); never hand-edit those files - edit the fragment, re-lint, re-merge.

### The seeded Medium sample (Phase 2, 27 ids)

F-catchall-011-01, F-catchall-021-01, F-catchall-053-02, F-ghost-playback-008-01,
F-ghost-playback-008-02, F-ghost-playback-023-02, F-io-serialization-004-01,
F-io-serialization-004-02, F-ledger-career-002-02, F-ledger-career-007-03, F-ledger-career-018-03,
F-ledger-career-027-01, F-legacy-bugfix-010-01, F-logging-001-01, F-logistics-route-008-01,
F-logistics-route-020-02, F-map-render-006-01, F-map-render-006-02, F-map-render-009-02,
F-mission-groups-003-04, F-recorder-events-001-01, F-recorder-events-024-02,
F-recording-tree-003-02, F-recording-tree-027-04, F-recording-tree-030-05,
F-recording-tree-042-04, F-rewind-refly-004-01.

### Known data caveats

- F-legacy-bugfix-018-02 (Low T6, dead test-fixture code at
  `Bug618ReFlyMergeParentChainTipTests.cs:597`) is the one finding with no matching D1 row: it is
  filed against a code region, not a test method. 767 of 768 findings join `test-inventory.csv` on
  `register_id`.
- The scanner initially missed 7 methods that the TRX executes; they were reviewed under
  `supplement-001` and the D1 row count now equals the TRX count exactly
  (`work/inventory-completeness.txt`).
- `file-summary.csv` names a `generators` cluster (13 files) that never appears in
  `test-inventory.csv`, because those files declare no test methods. 36 of the 1,010 files are in
  that shape.
