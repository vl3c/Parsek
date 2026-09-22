# Test Quality Audit - 2026-09-14

Baseline SHA `4aedb0a1a` (`4aedb0a1a018bd3420081778cffc1337bb6a58aa`, pinned; every finding cites this SHA).
Dates: Phase 0-1 opened 2026-09-14; Phases 1, 2, 3 and 4 closed 2026-09-15.
Scope: `Source/Parsek.Tests` xUnit unit tests only. `InGameTests` and the harness Python suites are
cross-referenced only where they change a verdict on a unit test.

Status: CLOSED 2026-09-23 (historical). Phase B landed 36 PRs (#1681 through #1765). Of the 768 findings, 589 are closed: all 7 High and 272 Medium, and Low T1 (64), T2 (231), T4 (6) and T6 (5). Each closed row carries a `mutations/mutations.csv` proof row (444 rows), a Phase B status adjudication, or a line in `work/phase-b-low-t2-dedupe.tsv`. Coverage priorities 1 and 2 are closed (80 done, 20 already covered, 2 deferred). CUT by operator decision on 2026-09-23 under this plan's opportunistic clause: the remaining 179 Low T3 rows (weak asserts, severity Low) and the 144 priority-3-to-5 coverage `keep` rows (including C-io-serialization-005-01). Both stay listed in `issue-register.csv` and `coverage-opportunities.csv` for anyone who takes them up. Follow-ups filed outside the audit: TQ-1, TQ-2 and LOOP-TIME-UNIT-NOT-PERSISTED in `docs/dev/todo-and-known-bugs.md`.

Earlier status: Phase A complete; Phase B go-ahead given by the operator on 2026-09-15 (first wave: the 7 High in `testfix-t1t2`, the six mechanical gate repairs in `testfix-t4-flaky`).
Phase B progress: the 7 High are FIXED on `testfix-t1t2` (2026-09-15) - see the "Phase B" column of the High table; every fix carries a re-run mutation proof under `research/.../mutations/` (`<id>-phaseB.patch`, `mutations.csv`).

Plan: `docs/dev/done/plans/test-quality-audit.md`. Raw and derived evidence:
`docs/dev/done/research/test-quality-audit-2026-09-14/`.

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
  new evidence; the mapping lives in `done/research/test-quality-audit-2026-09-14/july-crosswalk.csv` and
  in the "July crosswalk" section below.
- No production code and no test code was edited in Phase A. Nothing in this document had been
  fixed when it was written; Phase B fixes are recorded per row, not by rewriting the findings.

## Method

### Rubric

The review rubric was frozen before Phase 1 (`done/research/test-quality-audit-2026-09-14/rubric.md`) and
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

Full register: `done/research/test-quality-audit-2026-09-14/issue-register.csv` (768 rows; columns
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

Full table: `done/research/test-quality-audit-2026-09-14/coverage-opportunities.csv` (249 rows);
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
(patches under `done/research/test-quality-audit-2026-09-14/mutations/<cand_id>-phaseB.patch`):
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

**Phase B status, sixth PR (2026-09-15).** Twelve more priority-2 `recording` rows, all
`direct` and all `S`: ten are new coverage and two are `already-covered` under the
cross-class rule (C-legacy-bugfix-002-01 reds
`TreeCommitTests.CommitTree_AddsRecordingsToCommittedList` and ten further CommitTree
cells; C-mission-groups-014-01 reds `GroupTreeDataTests.DuplicateGroupMembership_NoDuplicateIndices`,
and the row's own proposed distinct-group assertion could not red because a full index
permutation visits every recording either way), so no duplicate cell was written for
either and none of the twelve is obsolete. The ten landed cells are mutation-proved with
no production change: C-legacy-bugfix-011-01, C-legacy-bugfix-015-01,
C-logistics-route-002-01, C-recorder-events-003-01, C-recorder-events-004-01,
C-recorder-events-021-01, C-recording-tree-001-01, C-recording-tree-001-02,
C-recording-tree-008-01, C-recording-tree-009-01. Their `status` in the CSV is now `done`.

**Phase B status, seventh PR (2026-09-16).** Twelve more priority-2 `recording` rows
(recording-tree and rewind-refly), all `direct` and all `S`: eleven are new coverage and one
is `already-covered` under the cross-class rule (C-recording-tree-050-01's isDebris-default
mutant reds `RecordingFieldExtensionTests.BackwardCompat_NoIsDebris_DefaultsFalse` plus four
fixture cells, so no duplicate cell was written), and none of the twelve is obsolete. The
eleven landed cells are mutation-proved with no production change: C-recording-tree-010-01,
C-recording-tree-011-02, C-recording-tree-013-01, C-recording-tree-022-02,
C-recording-tree-026-01, C-recording-tree-026-02, C-recording-tree-040-01,
C-recording-tree-040-02, C-rewind-refly-003-01, C-rewind-refly-003-02,
C-rewind-refly-004-01. Their `status` in the CSV is now `done`. Two rows needed a fixture
wider than the proposal sketch to discriminate at all: the touching-checkpoint split only
reaches the straddle partition when the recording carries NO mirrored top-level
`OrbitSegments` (seeding them makes the Ensure pass cut the section at the split UT first),
and the child-PID filter is only load-bearing when a `focusedVesselRecordingIdHint` pins the
walk, because otherwise every PID-matching terminal leaf is already a walk root. The
cycle-guard mutant does not produce a failed assertion but a stack overflow that takes the
test host down, so its evidence is the aborted run plus a green re-run of the same set with
only the new cell excluded.

**Phase B status, eighth PR (2026-09-16).** Twelve more priority-2 `recording` rows, all
`direct` and all `M`: six are new coverage and six are `already-covered` under the
cross-class rule, and none is obsolete. The six landed rows are mutation-proved with no
production change: C-recording-tree-005-01, C-recording-tree-006-01, C-recording-tree-023-02,
C-recording-tree-029-01, C-recording-tree-029-02, C-rewind-refly-005-01 (seven cells: the
sweep row landed with its mirror). Their `status` in the CSV is now `done`. The six
already-covered rows each red a cell that landed after the audit snapshot was taken:
C-catchall-011-01 reds `EnsurePassIntegrityTests.SplitAtUT_CommittedSplit_DropsTheHeadsSectionAnnotations`,
C-legacy-bugfix-007-01 reds both twins
(`EnvironmentTrackingIntegrationTests.CloseCurrentTrackSection_ComputesCorrectSampleRate`
and `BackgroundTrackSectionTests.ClosedSection_ComputesSampleRateHz`), C-recording-tree-031-01
reds `SwitchSegmentDiscardScopeTests.CollectSubtree_PastTheIterationCap_BreaksWithWarn_AndPartialList`,
C-recording-tree-046-01 and -046-02 red the shared-id narrowing cells in
`SwitchSegmentSuppressionNarrowingTests`, and C-recording-tree-047-01 reds
`SwitchSegmentSaveLoadTests.F9_ToPreSwitchSave_ClearsMarker_DropsPendingAttempt`. Two notes
for the next slice. The reseed anchor search accepts a gap of at most 5 s, so the proposal
sketch's ut=100 anchor against a segment starting at 112 could never be admitted; the cell
uses ut=110. And `ParsekScenario` inherits Unity's overloaded `==`, so a mutant written as
`if (scenario != null) return 0;` inside `LoadTimeSweep` is inert against a test-constructed
scenario (Unity reports the fake-null as null, which is why `Run` itself uses
`ReferenceEquals`); the recorded mutant stubs the body unconditionally instead.

**Phase B status, ninth PR (2026-09-16).** The last fourteen priority-2 rows, a mixed
`direct` / `seam` slice: eight are new coverage (thirteen cells, every mirror included),
five are `already-covered` and one is `deferred`, and none is obsolete. **The priority-2
register is now closed** - no `keep` row of priority 2 remains in
`coverage-opportunities.csv`, leaving two `deferred` priority-2 rows and 144 `keep` rows at
priority 3 and below (the count includes the new C-io-serialization-005-01 filed below). The eight landed rows are
C-rewind-refly-005-02, C-ghost-playback-012-01, C-recording-tree-034-02,
C-recording-tree-051-01, C-recording-tree-042-01, C-spawn-vessel-021-01,
C-recorder-events-008-01 and C-recorder-events-015-02; four of them needed a production
seam and the other four did not. The seams, all smallest-hook and live-path-neutral:
`SceneExitInterceptor.PersistentSaveStepForTesting` (an `Action<GameScenes>` invoked
INSIDE the try, because the existing whole-method seam returns before it and a
test-constructed `Game` reads as null to Unity's overloaded `==`, so the catch was
unreachable); `ParsekScenario.ArmRevertCleanupData(collector)` (the revert cleanup arming
step lifted out of `OnLoad` with the spawned-vessel collector as a delegate);
`GhostMapPresence.ShouldRemoveStateVectorOrbitForFrame` (one frame-aware gate now shared by
BOTH state-vector removal call sites, with the `GetAtmosphereDepth` read kept behind the
frame test so a Relative-frame point still never touches `FlightGlobals`); and
`WatchModeController.ComputeWatchOverlapPlaybackUT` (the overlap branch of the private
instance method, as a pure static over the cadence inputs). The five already-covered rows:
C-ghost-playback-008-01, C-spawn-vessel-020-01, C-io-serialization-004-01,
C-recorder-events-026-01 and C-recording-tree-039-01. The deferred row is
C-catchall-049-01.

Four notes for whoever reads these rows again. First, C-ghost-playback-008-01's proposed
mutant is EQUIVALENT: `Landed` is refused by three independent gates
(`IsTerminalStateEligibleForMapPresence`, `IsTerminalStateEligibleForTerminalOrbitMapPresence`
and `TryResolveTerminalOrbitGhostSeed`), each producing the same `terminal-<state>` reason,
so admitting it at any one site changes nothing observable - and the proposed cell name
`ShouldCreate_Landed_Skipped` is a cell that already exists and survives every single-gate
mutant. The reachable form of the same site (admit `SubOrbital`) reds a committed
`GhostMapEndpointTailTests` cell, which is what the recorded patch holds. Second,
C-catchall-049-01 is DEFERRED rather than closed: its mutant reds only the source-scrape cell
`PlaytestFollowupTests.Bug273_MethodBody_ContainsMarkFilesDirtyCall`, and the behavioural
witness the row wants needs the append-plus-dirty pair moved out of
`SampleContinuationVessel` - which that same scrape cell would itself red, so the seam is
forbidden here and the row stays open. Third, C-io-serialization-004-01 keeps its
`already-covered` verdict for the recorded mutant only. The `ForceReplaceFailureForTesting`
seam buys no cell for THAT mutant (a forced replace failure with nothing locked lets the
fallback SUCCEED, exactly as the drop-the-destination mutant does), but the `File.Replace`
catch itself is executed by no cell at all: the four replace-failure cells reach it only
through `FileShare.None`, which the Linux CI host does not enforce, and the forced-fallback
cell that reds the destination-preserving mutant skips the catch entirely. That edge is filed
as the new priority-3 row **C-io-serialization-005-01** (mutant: return inside the
`File.Replace` catch, swallowing the failure and orphaning the `.tmp`). Fourth, two of the landed rows guard walks that do not terminate under
their mutant: `GhostChainWalker.MergeCrossTreeLinks` and `ParsekFlight`'s preferred-child
path walk would both spin forever rather than fail an assertion, so both cells run the walk
on a worker task and assert a 10 s completion bound - the mutant then reds in ten seconds
instead of stalling the run (the chain-walker cell also installs its own log sink inside the
task, since the sink is `[ThreadStatic]`). A two-chain cycle is not enough for the first one (the `tipVesselPid == originPid` short-circuit already catches it),
so that fixture is a 100 -> 200 -> 300 -> 200 loop that excludes the walk origin.

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
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`.
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
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`
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
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`.
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
  a proof row in `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a
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
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`
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
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a
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
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`.
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
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a
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
    one on the `recording-tree` half (the other two recording-tree method additions are renames), each carrying the mirror or positive direction the
    original cell could not reach.

- `testfix-t3-d`, slice 5 (2026-09-16): the FIFTH slice of Medium T3 rows
  (`work/phase-b-slice-medium-t3-05.txt`, 20 ids: 12 `ghost-playback`, 7 `catchall`,
  1 `analyzer`). Counted the slice-4 way (every kept row is strengthened; a rename is a
  SUBSET of that, not a separate bucket): 19 strengthened, of which 9 renamed; 1 deleted;
  0 deferred; 0 premise-wrong. Nine sibling cells were added. Per commit, derived from
  `git diff origin/main...HEAD -- Source/Parsek.Tests`: e29d96489 3 renames + 1 new + the
  deletion, e2ee3d67b 1 + 1, 41e91527e 0 + 0, 6338adcfd 1 + 2, 30419f2b6 1 + 0,
  1a0d21bf9 0 + 0, eae1f2f75 0 + 2, 69d9af157 2 + 1, 8f2e4eef5 1 + 2. Each row has a proof
  row in `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a
  `*-phaseB.patch` that `git apply --check`s against a clean tree. The three source gates
  this slice adds share `SourceScanText.BraceMatchedBlock` (moved there from the first
  copy rather than pasted three times); every class that reads `SourceScanText` was re-run
  after the move, and all three gates were re-proved RED under their recorded patches.
  - Given the production term the name claims (8): F-catchall-010-02 is the DELETION - its
    second rollout emission sat 200 s outside the 60 s duplicate window, so the window
    check alone kept the write and the cell was a copy of
    `OnVesselRolloutSpending_OutsideDuplicateWindow_BothKept` with an inert adoption; the
    twin `OnVesselRolloutSpending_AdoptedRowDoesNotBlockRelaunchWrite` (relaunch INSIDE the
    window) is the only cell that reds when both adopted-row exclusions are deleted, across
    all four classes that reach the rollout path. Note which exclusion decides: adoption
    NULLS the row's `DedupKey`, so the `IsNullOrEmpty(a.DedupKey)` skip is load-bearing and
    the `RecordingId` skip alone is equivalent.
    F-analyzer-003-01 (renamed
    `CorePurity_AllRules_NullSaveDirectory_DoNotThrow_AndProbeNoSaveFiles`: the model now
    carries `RewindSaveFileName` with a NULL `SaveDirectory` and the report must be EMPTY,
    since `Record.Exception == null` is also satisfied by a rule that reads a real file and
    succeeds. The name says no SAVE files because `Inv10CodecRoundtrip` round-trips through
    a temp file by design);
    F-catchall-018-01 (renamed
    `AlignedLoop_NeverHasSecondary_AndRegionBStillFlipsThePrimaryToNextInstance`: the sweep
    read only `HasSecondary`, which the region-B cycle bump leaves untouched, so the cell
    now probes inside the borrow window and pins `CycleIndex == N+1` and the fresh-launch
    `LoopUT`. Cycle 3's advance is 0 on that fixture, so the window loop stops at N=1);
    F-catchall-044-01 (the repro's own 2.42 m clearance and 285.52 m floor as literals - the
    old expectation was `283.1 + clearance`, computed from the very call under test. The ramp
    itself stays with `Bug156Tests.ComputeTerrainClearance_*`);
    F-catchall-046-01 (the landing-only control's rotation assertions moved out of
    `if (unit.ArrivalHoldSeconds > 0.0)` and the hold is asserted unconditionally - the one
    regression the control exists to catch used to skip them);
    F-ghost-playback-008-01 (all seven `TerminalFilter_` / `DebrisFilter_` cells now call
    `ShouldCreateTrackingStationGhost` and assert the `(shouldCreate, skipReason)` pair. The
    recorded mutant opens BOTH `IsTerminalStateEligibleForMapPresence` and
    `IsTerminalStateEligibleForTerminalOrbitMapPresence` so all three refusals move at once,
    but the two filters are NOT symmetric. Destroyed and Landed are refused by the first and
    then again by the second, so opening the first ALONE leaves their `skipReason` unchanged
    and the suite green. SubOrbital passes the first and is refused only by the second, so
    opening the second alone already reds `SubOrbital_HasOrbitData_ReturnsTrue` on its own);
    F-ghost-playback-011-02 (the ready arm plus the three remaining deferral inputs - only
    the false arm was pinned and nothing else calls `CanRestoreMapFocus`);
    F-ghost-playback-015-02 (the trace cursors and completed-event set are seeded, asserted
    present, and asserted gone after `Reset()`);
    F-ghost-playback-018-02 (the debris mirror: `IPlaybackTrajectory.LoopPlayback` is
    `!IsDebris && LoopPlayback`, and no cell cast a debris recording to the interface).
  - Re-aimed at a production caller through a behaviour-identical extraction (5 rows, 4
    helpers): F-catchall-040-01 (`ParsekFlight.NeedsPostSwitchModuleCacheRefresh` - two of
    the Theory's five parameters never reached production because the cell recomputed the
    invalidation itself);
    F-ghost-playback-009-01 (`GhostMapPresence.ShouldRemoveStateVectorOrbitForFrame` - the
    cell was a two-fact tripwire whose facts were never joined. This row was originally
    written with a branch-only `ShouldRemoveStateVectorOrbitInRefreshPass`; merging
    `origin/main` brought PR #1724's `ShouldRemoveStateVectorOrbitForFrame`, which is the
    SINGLE gate for both removal call sites (refresh pass and flight policy) with the
    atmosphere read kept lazy behind the frame test, so the branch helper was deleted and
    the cell retargeted onto main's. It derives the frame flag from the recording's own
    `TrackSection`, the tracking-station shape; `RuntimePolicyTests` pins the same gate with
    the flag passed literally, and the re-generated mutant reds both);
    F-ghost-playback-013-01 (`GhostPlaybackLogic.ComputeTargetWheelSteeringDegrees` - the
    cell was handed the post-negation value, so the caller-side minus never ran AND the
    `-10` it passed was the opposite sign of a real heading rate; the mirror direction is
    asserted so dropping both negations cannot pass);
    F-ghost-playback-016-01 and -016-02
    (`GhostPlaybackLogic.ShouldEnforceLoopedAudioPlaybackCap` - no xUnit `AudioGhostInfo`
    can carry a Unity `AudioSource`, so the null-source early return fired first and both
    `enforceCount` assertions were satisfied without the cap flag ever being read. That
    early return did nothing but skip the cap, so folding it into the gate is
    byte-identical. -016-01 is renamed to the paused-tracking claim with its inert override
    injection dropped; -016-02 is renamed to the power batch driving the post-batch
    selection, with the seam-routing assertion kept and labelled as one).
    `GhostPlaybackEngine.CountFxForObservability` / `CountModulesAndParticleSystems` were
    widened private -> internal for F-ghost-playback-003-01, whose cell asserted twelve
    zeros that the headless `HasLoadedGhostVisuals` gate produces on its own; it is renamed
    to that gate, and two new cells drive the counting arithmetic, accumulation across
    ghosts, and the null / empty maps.
  - Moved to a source gate because no runtime cell can see the claim (3):
    F-catchall-024-01 (`ComputeForwardStopUT` is pure and takes its list as a parameter, so
    no argument of it can witness WHICH list the renderer supplies; renamed to the geometry
    claim, and a gate over the method hosting the `ComputeForwardWindow` call asserts
    `windowSegs` is assembled only from the coalesced effective scratch lists fed by
    `ResolveEffectiveMapOrbitSegments`);
    F-catchall-039-03 (renamed `AutoCommit_EndState_TreeCommittedAndResourceIndexAdvanced`;
    the commit-then-mark ORDER is a gate over `CommitPendingTreeAsApplied`'s body, because
    `MarkTreeAsApplied` advances the indexes on the tree OBJECT the caller passes and the
    end state is identical under the swap);
    F-ghost-playback-025-01 (renamed
    `ExitWatchModeBeforeTimelineGhostCleanup_WhenWatching_DetachesThenExitsSkippingCameraRestore`;
    the ordering half compared the helper's log against a destroy line the TEST wrote right
    after the call, so the real call-site order is now a gate over
    `DestroyAllTimelineGhosts`).
  - F-ghost-playback-007-01 is renamed
    `SeamBridge_AngleBandRouting_MeetsConicChordAndMergeSlice`, with every measured playtest
    geometry routed through `ClassifySeamBridgeByAngle`. Its messages had called the 4.59 deg
    near-meet a SKIP, which production does not do - (0.5 deg, 5 deg] routes to `Chord`. The
    mutant is aimed at `SeamBridgeAngleRad` rather than the classifier, because
    `ClassifySeamBridgeByAngle_FourBands` already pins all four bands and all three
    boundaries from raw radians; what this cell uniquely owns is the geometry-to-band
    pipeline.
  - F-ghost-playback-004-01 closes the slice: the launch-alignment sweep's in-span bound sat
    inside `if (resolved)`, so a resolver that stopped resolving ran the body zero times. It
    now counts swept and resolved steps and requires every step to resolve. The mutant is
    deliberately PARTIAL (inert only past the third cycle): a resolver inert everywhere also
    reds the two sibling cells that assert `resolved` at currentUT 2100 and 3000, so only the
    swept-count assertion can see a regression confined to later cycles.


- `testfix-t1t2`, eighth PR (2026-09-16): the THIRD slice of Medium T3 rows
  (`work/phase-b-slice-medium-t3-03.txt`, 20 ids, all `ledger-career`). Each fixed row
  has a proof row in `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv`
  and a `*-phaseB.patch` that `git apply --check`s against a clean tree.
  - Strengthened (15): F-ledger-career-001-06
    (`ReconcileKsc_PartPurchase_EntryCostMatched_NoWarn`, with the inert bypass provider
    dropped - nothing on the reconcile path reads it - and a new mirror cell
    `ReconcileKsc_PartPurchase_PartCostAgainstEntryCostEvent_WarnsDeltaMismatch` driving
    the PRE-#451 shape, since the silence alone is satisfied by a reconciler that never
    compares deltas. The #451 pin proper stays at the recorder seam,
    `GameStateRecorderLedgerTests.ComputePartPurchaseFundsSpent_BypassOff_ReturnsEntryCost`);
    F-ledger-career-003-02 (drives the real `OnRecordingCommitted` with an installed
    crewed recording and asserts the commit summary at `LedgerOrchestrator.cs:407`
    instead of the recalc line `RecalculateAndPatch_RunsWithoutError` already pins. The
    `startUT=`/`endUT=` values are matched on the KEY only: that line formats `F1` under
    the OS culture and the ro-RO dev host prints `50,0`);
    F-ledger-career-004-01 (the wrong-reason fixture is now a wrong-reason DEBIT keyed
    `TechResearchScienceReasonKey`, so the sign filter can no longer stand in for the
    reason filter);
    F-ledger-career-004-02 (the discriminator comes from
    `KspStatePatcher.ComputePendingAdjustedRunningScience` over a staged uncommitted
    exchange, not a typed-in constant plus an inline re-issue of the fold. Kept rather
    than deleted against `StrategyPrefixHoldbackTests`: that twin pins the fold and the
    basis, never the clamp-direction symptom);
    F-ledger-career-008-02 (`TryGetOriginalScience` values, not just the entry count);
    F-ledger-career-013-03 (serializes under `de-DE`, asserts the raw `ut` /
    `scienceAwarded` / `transmitScalar` / `subjectMaxValue` node text carries no comma,
    then reads back - the shape of `ResourceManifestSerializationTests.LocaleSafety`);
    F-ledger-career-013-05 (captures the sink and asserts the `Unknown action type id
    '9999'` Warn, plus `Type == default(GameActionType)` rather than `NotEqual(9999)`);
    F-ledger-career-014-01 (both source scans start at
    `internal static void OnStrategyCurrencyConversion`; the file-wide `IndexOf` was
    being satisfied by `OnKscSpending`'s earlier, unrelated `Ledger.AddAction(action);`);
    F-ledger-career-016-01 (each `GameActionType` must appear at least once carrying a
    payload beyond the five skeleton fields, checked by reflection over `GameAction`'s
    fields so a new field is covered the day it lands. This immediately red'd on
    `KerbalExperience` and `StrategyScienceCredit`, neither of which had a `CreateAction`
    arm - the corpus had been fuzzing all-zero rows of both, which is exactly what the
    old comment falsely promised to catch - and both arms were added);
    F-ledger-career-017-02 (the CROSS-ORDER comparison the design-doc property is about:
    `NotEqual` on the milestone and contract effectives between the two walks. Flat
    multipliers used to pass);
    F-ledger-career-024-01 (the "ignored" milestone fixture is replaced with
    `FacilityUpgrade`, which `ScienceModule.ProcessAction` genuinely has no arm for, and
    two mirror cells pin the milestone arm it does have - effective-with-science credits
    the pool, not-effective credits nothing);
    F-ledger-career-025-02 (guards the production channel tag
    `KSC reconciliation (funds)`; the literal `KSC reconciliation: Funds mismatch` it
    used to guard is emitted nowhere);
    F-ledger-career-027-01 (new sibling
    `TerminalContractMaps_SecondTerminalActionWithNoReAccept_Overwrites`: a fail then a
    cancel on one id with no intervening Accept is the only shape that separates
    latest-wins from first-wins, since an Accept clears both maps);
    F-ledger-career-031-03 (the dispatch is witnessed by the module's own `[Strategies]`
    Activate / Deactivate lines and by `IsStrategyActive` after the walk; the
    `Transformed*` fields are prefilled by the test helper and prove nothing about it);
    F-ledger-career-031-04 (the stored `StrategyState` is read back, so the overwrite is
    distinguishable from an ignore for the first time).
  - Renamed to what they prove (3): F-ledger-career-018-01
    (`PatchTechTree_NullTargetWithRewindContext_SkipsBeforeTheAvailableLog`; the
    applied-node Info log carrying `utCutoff` / `baselineUt` is unreachable headlessly
    because the null-target return fires first, so the cell now asserts the skip AND the
    absence of that log's own tokens. The log's fields stay in-game work);
    F-ledger-career-018-03 (`PatchAll_RestoresSuppressionFlags`, with the unproven "Sets"
    half moved to a new direct cell
    `SuppressionGuard_ResourcesAndReplay_SetsBothFlagsInsideTheScope` - PatchAll opens
    exactly that guard, and nothing inside its scope was observable from the old cell);
    F-ledger-career-037-01 (`IsManaged_TwoReservedCrew_BothManagedAsReservedActive`,
    plus `GetReservationKind == ReservedActive` so the reservation term, not chain
    membership, has to answer. Genuine retirement needs a displaced, unreserved chain
    entry that this fixture never builds; the cell's own comment had said as much since
    it was written).
  - Deleted as duplicates (2), each leaving a comment naming the twin:
    F-ledger-career-001-04 (`ClassifyAction_PartPurchase_BypassOff_StaysUntransformed` -
    `KscActionExpectationClassifier` never reads the bypass provider, so the setup was
    inert and the four asserts were a copy of
    `ClassifyAction_PartPurchase_UntransformedWithRnDPartPurchaseKey`; the bypass branch
    is pinned in `GameStateRecorderLedgerTests`);
    F-ledger-career-032-01 (`InferCrewEndState_UndockedPodCrewInChildSnapshot_ReturnsAboard`
    built no recording, no undock and no child snapshot - it drove the same branch with
    the same assertions as `InferCrewEndState_OrbitingInSnapshot_ReturnsAboard`. The
    undock/child-recording path it was named for still needs a recording-level case).
  - One production change, a read-only accessor:
    `StrategiesModule.TryGetActiveStrategy(string, out StrategyState)`, named by the
    register for F-ledger-career-031-04 and added because no existing surface can tell a
    re-activation's overwrite from an ignore. No behavior and no log text changed;
    `GrepAuditTests` and `AgentInstructionMirrorTests` re-run green.
  - Slice total: 20 of 20 addressed - 15 strengthened, 3 renamed and strengthened, 2
    deleted (twin), 0 deferred. Five sibling cells were added (the delta-mismatch mirror,
    the terminal-map overwrite, the suppression-guard scope, and the two milestone-arm
    science cells), each carrying the mirror or positive direction the original cell
    could not reach.
- `testfix-t1t2`, sixth PR (2026-09-15): the final slice of Medium T1 rows
  (`work/phase-b-slice-medium-t1-06.txt`, 14 ids: 10 catchall, 4 legacy-bugfix), first
  commit. Every fixed row has a proof row in
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`
  that `git apply --check`s against a clean tree.
  - Fixed: F-catchall-006-02 (the `onGameStateSave` watch is unfalsifiable headlessly, so
    the cell now watches the two observable counters instead: `RecordingStore.StateVersion`
    must NOT move between CheckpointA:AfterProvisional and CheckpointB:AfterMarker, and
    `ParsekScenario.SupersedeStateVersion` - the reader wake-up - must bump exactly once and
    only after the marker field is readable; the mutant moves the bump into phase 1);
    F-catchall-010-01 (the idempotency cell seeded two legitimately distinct rollouts, so
    nothing collapsed; it now seeds the production duplicate cluster directly, asserts pass
    one returns 1 and pass two returns 0, and pins EXACTLY ONE collapse summary across both
    passes - the mutant emits that summary unconditionally); F-catchall-021-01 (the
    dock-chain branch-1 sibling now carries an EVA kerbal PAST the branch-0 tip, a branch-0
    kerbal boards back before it, and a positive control adds a branch-0 EVA past the tip
    that IS excluded, so the null is a decision rather than a dead walk);
    F-catchall-035-01 (renamed and re-pointed: the control-surface probe takes a live
    `PartModule`, so the delegation is pinned as a brace-matched body gate over
    comment-stripped source - every `return` in `TryClassifyControlSurfaceState` is the
    aero-probe call; the mutant leaves the delegation standing as a COMMENT, so the strip is
    proved at the same time); F-catchall-050-01 (the root's `RewindSaveFileName` starts null,
    so the no-op cell now asserts the BUDGET the empty-save early return protects -
    reserved and pre-launch figures stay 0 while the capture carries non-zero ones);
    F-catchall-053-02 (the `CheckSpawnCollisions` recovery latch is extracted as
    `VesselSpawner.ShouldEnterDuplicateBlockerRecovery` - the only production change in this
    commit - and the cell calls it, with a positive control that clears the latch and an
    unloaded-blocker case; the call site passes `blockerVesselLoaded` false for a null
    blocker so no member of a missing blocker is read).
  - Deleted (twins named in the register): F-catchall-036-01's three inline hide-policy
    replays (`Hide_UnfinishedFlight_WarnsAndDoesNotFlip`,
    `Hide_NonUnfinishedRecording_FlipsNormally`,
    `Hide_NormalListUnfinishedFlight_RefusesWithoutVirtualGroup`). All three emitted the Warn
    and the ScreenMessage themselves and modelled a depth-AND-classifier expression the
    shipped code does not use; the shipped predicate is covered behaviourally by
    `ArchiveRefusal_AppliesToHidingOnly` (all four direction / classification combinations)
    and at wiring level by `Hide_PolicyGate_IsClassifierOnly_NoDepthCheck` and
    `TheGroupHideAllScanRedsWhenTheRoutingExistsOnlyInAComment` in the same file. A comment
    at the site names the twins.

- `testfix-t1t2`, sixth PR (2026-09-15), second commit: the rest of
  `work/phase-b-slice-medium-t1-06.txt`. Slice total 14 ids: 11 fixed, 3 deleted (twins
  named), 0 deferred. This CLOSES the Medium T1 register - no Medium T1 row is left
  unhandled. Every fixed row has a proof row in
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`.
  - Fixed: F-catchall-059-01 (renamed `FreshRecorder_AltitudeFlagsDefaultFalse` - the
    confirm log it claimed needs a live Vessel, so the cell states the field-default claim
    it can make, notes that ParsekFlight's altitude-phase split early-returns on
    `AltitudeBoundaryCrossed`, and also drives the not-recording guard; the mutant
    initialises the field to true); F-catchall-060-01 (drives the real load path -
    `ParsekScenario.LoadRecordingTrees` over a SCENARIO node with no `RECORDING_TREE`
    child - instead of calling `CommittedTrees.Clear()` in the test body, and asserts the
    `OnLoad initial: cleared CommittedTrees, loading 0 tree(s)` line);
    F-catchall-062-01 (the OnLoad counting loop is extracted as
    `ParsekScenario.CountSavedCommittedRecordingNodes`, and the cell feeds it a node
    carrying an in-flight marker tree AND a pending marker tree whose recordings must NOT
    count - the rule the old in-test replay got wrong on top of being detached);
    F-legacy-bugfix-009-02 / -009-03 (both fixtures now seed the fork into
    `RecordingStore.CommittedRecordings`, so the guard-removed path would ATTACH it rather
    than land on the missing-from-BOTH branch that also returns false; the mutants delete
    the `InPlaceContinuation` guard and the marker-TreeId-vs-tree-Id guard respectively);
    F-legacy-bugfix-025-01 (the map-view carve-out is extracted as
    `GhostPlaybackLogic.ShouldSuppressGhostsInView(mapViewEnabled, warpRate)` - the
    composition `UpdatePlayback` now calls - and all three cells assert the helper, so a
    dropped `mapViewEnabled` term reds instead of leaving the warp threshold restated).
  - Replaced (stale claim, no twin to delete into): F-legacy-bugfix-026-01. The cell
    quoted a gate expression the engine no longer carries; the shipped line is a bare
    `if (!TryReserveSpawnSlot(index, "loop-first-spawn"))` inside the `state == null`
    first-spawn block, and the cycle-rebuild branch runs only when `state != null`, so a
    rebuild cannot reach it and no bypass term is needed. Renamed
    `LoopFirstSpawn_IsThrottledWithNoCycleChangedBypass` and re-pointed at the real
    reservation primitive under the loop site tag (budget available -> reserved; budget
    exhausted -> deferred, counted and named in the throttle log). The mutant exempts the
    `loop-first-spawn` site from the frame cap.
  - Two production helpers were extracted in this slice
    (`VesselSpawner.ShouldEnterDuplicateBlockerRecovery` in the first commit,
    `ParsekScenario.CountSavedCommittedRecordingNodes` and
    `GhostPlaybackLogic.ShouldSuppressGhostsInView` here); each call site keeps the same
    inputs, order and log lines. After each production edit the test tree was grepped for
    the moved identifiers and every source-scanning class over the touched file was re-run
    (`ProgrammaticRecoveryCrewSuppressionGateTests`, `SpawnWalkbackFallbackTests`,
    `CareerSeedReadinessTests`, `ChainSaveLoadTests`, `CheckpointDoubleCoverRetireTests`,
    `ObservabilityPersistencePhase3Tests`, `QuickloadResumeTests`,
    `SaveActiveTreeSidecarBothOrNeitherTests`, `ScenarioGameEventHandlerContractTests`,
    `SceneChangeTerminalStateWiringGateTests`, `SwitchSegmentSaveLoadTests`,
    `SwitchSegmentSuppressionNarrowingTests`, `TestBatchIsolationTests`,
    `GhostRenderTraceTests`, `GhostSpawnPendingNotifyTests`,
    `GrepAuditNonLoopLivePidTests`, `LoopUnitSetCoherenceTests`,
    `OverlapPerInstanceTests`, `WatchEntryAcceptanceWiringGateTests`,
    `RuntimePolicyTests`), plus `GrepAuditTests` each time. No gate needed re-anchoring.

- `testfix-t3-e` (2026-09-16): the SIXTH slice of Medium T3 rows
  (`work/phase-b-slice-medium-t3-06.txt`, 20 ids: 8 `map-render`, 7 `legacy-bugfix`,
  3 `harness-seam`, 2 `io-serialization`). Counting rule: every row is strengthened, and
  a rename is a SUBSET of that, never a separate bucket. Slice total: 19 strengthened,
  6 of those also renamed, 1 deleted, 0 premise-wrong, 2 behaviour-identical production
  edits. Each row has a proof row in
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a
  `*-phaseB.patch` that `git apply --check`s against a clean tree.
  - Vocabulary asserted against itself (3): F-harness-seam-001-01 re-split
    `ValidCtrlNames`, which is the comma-join of `CtrlValues`, so a kind rename on either
    side stayed green; it compares against `GuiTreeAssembler.KindName` over every
    `GuiNodeKind` now. F-harness-seam-007-01's reason set was a test-local array, so the
    stated purpose (a capture reason added without a bucket is visible here) went unmet;
    the set is read from `ComputeSeamEndpointGeometry`'s IL `ldstr` operands, with the
    three seam-seed constants excluded BY the production constants and the oracle-owned
    `no-usable-ratio` appended by name. F-harness-seam-015-02's comment promised a new
    `RecordingPaths` builder would red it while listing seven by hand; the builder set is
    discovered by reflection (static, string-returning, one `recordingId` parameter).
  - Culture pins that pinned nothing (2): F-io-serialization-004-01 / -004-02 set no
    culture, and a symmetric codec round-trips on every host, so both now pin de-DE around
    the round trip and read the RAW written POINT / ORBIT_SEGMENT values. The mutant is
    ambient culture on BOTH sides, which is green before and red after.
  - Assertion windows too wide to see a wrong answer (3): F-map-render-006-01's
    `Assert.Equal(3f, t.x, 4f)` binds xUnit's float TOLERANCE overload (a float literal
    cannot bind the int precision overload), accepting x in [-1,7]; it is 1e-4f now, with
    z and a non-zero-origin pair. F-map-render-014-02 value-checked only `frames[0].ut`
    against a whole-payload read-only invariant; every frame's ut/lat/lon/alt/velocity/
    body/rotation/flags is snapshotted now. F-map-render-014-01, renamed
    `OutlierClassifier_Cluster_FlagsSection_WhenRejectionRateIsOverTheGate`: its Cluster
    assertion sat inside a test-side recompute of the production rate gate, and the
    measurement shows the gate was NEVER true - four consecutive kraken velocities reject
    2 of 16 samples (0.125, under 0.20), because the interior samples of a run share their
    neighbours' velocity. The fixture spikes alternating indices (8 of 16), pins the
    counts first, and a new mirror cell pins the bit CLEAR under the gate.
  - Never entered the named branch (5): F-map-render-001-03 / -001-04 hand-passed the
    effective loop bounds, so a KSC dispatcher reverted to the raw span stayed green; both
    read them from `ParsekKSC.TryGetLoopSchedule` now and the cycle-bounds cell feeds
    `GetActiveCycles` the resolver's `(scheduleStartUT, scheduleStartUT + duration)`
    exactly as `UpdateOverlapKsc` does. F-legacy-bugfix-005-01 asserted the SubOrbital
    DEFAULT under a name promising Orbiting, because `FlightGlobals.GetBodyByName` is null
    headless; the radius comes through `TerminalInferenceBodyRadiusResolverForTesting` now,
    with the unresolved-body and sub-surface-periapsis mirrors as their own cells.
    F-legacy-bugfix-004-01 and -018-01 both named a cycle guard over fixtures with no
    reachable revisit (the first's victim was the first BP parent; the second's duplicate
    `ChainIndex = 0` has no predecessor, as its own comment said). Each keeps its old
    fixture under an honest name
    (`IsRecordingInParentChainOfActiveReFly_DirectParentInCyclicBp_ReturnsTrue`,
    `DuplicateChainIndexZero_TerminatesAtTheFirstMatch`) beside a new absent-victim case
    where the walk must exhaust and the trace's `parents=[...]` list decides. The mutant
    is the `visitedRecs` short-circuit alone: both fixtures still TERMINATE without it
    (the BP-level visited set bounds them), so the evidence is a duplicate in the trace
    rather than a hang that would take the test host down.
  - Asserted no value at all (4): F-map-render-004-01's three Info-routing cells asserted
    no level token under a fixture that forces verbose on, so a Verbose-routed line matched
    every predicate; each asserts `[INFO]` now (the `EmitRaw_NonImportant` twin already
    pinned `[VERBOSE]`, so the mirror direction was covered). F-map-render-009-03,
    renamed `Probe_AlgorithmStampField_RoundTrips`, constructed neither the mismatch nor
    the discard its name promised; a new sibling patches the stamp int in the header bytes
    and asserts the probe reports the drifted value, which is what a probe echoing the
    compiled-in constant would fail. F-legacy-bugfix-015-01, renamed
    `CreateTimeJumpEvent_HeaderAndDetailKeyGrammar`: the values are already owned by the
    post-audit `CreateTimeJumpEvent_DetailsValuesRoundTrip`, whose dictionary parse loses
    ORDER, so this cell pins the eight keys in their declared order plus the header.
    F-legacy-bugfix-002-01 put every assertion inside a `foreach` over
    `CommittedRecordings`, so a `CommitTree` that committed nothing passed in silence; the
    count comes first and the group is compared against the tree's own
    `AutoGeneratedRootGroupName`.
  - Production decision re-implemented in the test (1): F-legacy-bugfix-001-03 ran its own
    copy of OnLoad's revert guard, so the production branch never executed. That decision
    is extracted as the pure `ParsekScenario.ClassifyRevertPendingTreeDisposition` (fresh
    stash wins, then no pending tree, then Limbo / LimboVesselSwitch, else orphan); the
    call site is a straight dispatch over the result with the same log lines and the same
    `PendingStashedThisTransition` clear, and a sibling covers the three arms.
  - The one delete: F-legacy-bugfix-010-01's `ImmutableDestroyedUnderRP_IsMember` said
    Immutable in its name, doc comment and claimed regression while its fixture passed
    `MergeState.CommittedProvisional`, making it byte-identical to
    `CommittedProvisionalDestroyedUnderRP_IsMember` below it. Under the cross-class rule
    the sealed-tip mutant (disable `IsSlotEffectiveTipOpen`) reds six cells in four classes
    - `ImmutableDestroyedUnderRP_NotMember_SealedTipClosed`, `SealedSlot_NotMember`,
    `StashedThenSealedSlot_NotMember`,
    `UnfinishedFlightClassifierTests.OpenClosedFilter_ImmutableTip_HidesShapeQualifyingSlotFromUf`,
    `CollapseSealMergeStateRegressionTests.StashThenSeal_NotReStashable_AndHiddenFromUf`
    and `RewindB9FixtureTests.Inject_CrashedBoosterClassifiesAsOpenUnfinishedFlight` - so
    the Immutable case is covered and the duplicate was removed with a comment in its place.
  - Two production edits, both behaviour-identical and both re-verified against the
    source-scanning gates over the touched files: `ParsekKSC.TryGetLoopSchedule` widened
    from `private static` to `internal static` (visibility only), and the revert-branch
    classifier extracted above. After the `ParsekScenario.cs` edit every class that reads
    that file was re-run (`Bug585InPlaceContinuationRestoreTests`,
    `CareerSeedReadinessTests`, `ChainSaveLoadTests`, `CheckpointDoubleCoverRetireTests`,
    `ObservabilityPersistencePhase3Tests`, `QuickloadResumeTests`, `RevertDiscardTests`,
    `RewindB9FixtureTests`, `SaveActiveTreeSidecarBothOrNeitherTests`,
    `ScenarioAutoCommitResourcesAppliedTests`, `ScenarioGameEventHandlerContractTests`,
    `SceneChangeTerminalStateWiringGateTests`, `SwitchSegmentSaveLoadTests`,
    `SwitchSegmentSuppressionNarrowingTests`, `TestBatchIsolationTests`), plus
    `GrepAuditTests`, `GrepAuditNonLoopLivePidTests` and `LoopUnitSetCoherenceTests` after
    the `ParsekKSC` one. No gate needed re-anchoring.
  - One doc reference updated for a rename: `docs/dev/done/plans/phase8-outlier-rejection.md`
    named the Cluster cell and its "2 of 16" claim.

- `testfix-t3-c` (2026-09-16): the FOURTH slice of Medium T3 rows
  (`work/phase-b-slice-medium-t3-04.txt`, 20 ids: 12 `logistics-route`,
  7 `recorder-events`, 1 `ledger-career`). Counting rule: every row is strengthened, and
  a rename is a SUBSET of that, never a separate bucket. Slice total: 20 strengthened,
  11 of those also renamed, 0 deleted, 0 deferred, no production change. The first
  commit covers the 8 non-logistics ids: 8 strengthened, 5 of those also renamed
  (`ZeroTimeDelta_IdenticalVelocity_NoRecord`,
  `ShouldForceWatchProtectedFullFidelity_AllInputPairs`,
  `ShouldAllowWarpZoneHideExemption_AllInputPairs`,
  `ShouldTriggerExplosion_AllGuardsPass_WarpGateDecidesFxSuppression`,
  `ResolveMapPresenceGhostSource_CrossBodyLoopMember_PredicateComputedFlag_StillRejects`).
  Each has a proof row in
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a
  `*-phaseB.patch` that `git apply --check`s against a clean tree.
  - F-recorder-events-012-01 and -012-02, both renamed `..._AllInputPairs`: the two
    warp / watch-protection predicates take the same two booleans, so one asserted
    pair - and a FALSE-returning pair at that - could not tell the conjunction from a
    constant. Both are theories over all four pairs now; the arm that had no assertion
    anywhere in `Source/Parsek.Tests` or `Source/Parsek/InGameTests` is (true, false).
  - F-recorder-events-023-01, renamed
    `ShouldTriggerExplosion_AllGuardsPass_WarpGateDecidesFxSuppression`: the old name
    promised a suppression LOG assertion neither helper can make. Rows at 10x and
    10.01x were added so the strict `>` and the `FxSuppress` constant decide rather
    than the rate range, which is what made the old cell a duplicate of
    `ShouldSuppressVisualFx_Above10x_Suppresses`.
  - F-recorder-events-023-04: `rec.VesselDestroyed = true` is set UNCONDITIONALLY ahead
    of the already-Destroyed early return and is what `SwitchSegmentNoOpClassifier`
    reads, so the already-Destroyed cell now asserts it and the two not-destroyed cells
    assert its absence.
  - F-recorder-events-011-02, renamed `ZeroTimeDelta_IdenticalVelocity_NoRecord`: there
    is no zero-delta guard in `ShouldRecordPoint`, and with identical velocities no gate
    could fire whatever the elapsed handling did. Two siblings pin what production
    actually does - a velocity change at the SAME UT DOES record a duplicate-UT sample
    (the mutant is inserting the guard the old name implied), and the negative-elapsed
    mirror is refused by the min-interval floor, not by a backward-time guard. The
    register's optional production change (add a non-positive-elapsed guard) was NOT
    taken: it is a behaviour change, not a test fix. Scope, as T7-3 above already records:
    the duplicate-UT regime needs a ZERO min-interval floor, which is this test class's
    legacy constant - `ParsekSettings.GetMinSampleInterval` returns 0.5 / 0.2 / 0.05 for
    Low / Medium / High, so no shipped density reaches it. The cells pin the pure
    function's contract, not an in-game duplicate sample, and both say so.
  - F-recorder-events-015-01: the `_RecordingTree` half decoded through
    `ParsekScenario.LoadRecordingMetadataForTests`, the same call as its own
    `_ParsekScenario` twin. It now saves a real `RECORDING_TREE` and reads it back
    through `RecordingTree.Load` -> `RecordingTreeRecordCodec`. A missing key alone
    still cannot discriminate (the field defaults null either way), so the node also
    carries a recording WITH the key and the codec's assignment is the deciding term.
  - F-recorder-events-019-02, renamed
    `ResolveMapPresenceGhostSource_CrossBodyLoopMember_PredicateComputedFlag_StillRejects`:
    the cell hand-passed `acceptTerminalOrbitForLoopSynthesis:false`, which made it the
    same branch as the non-loop cell beside it and left the cross-body guard its name
    claimed unwitnessed. The flag is now COMPUTED as `GhostMapPresence.cs:7067` computes
    it (loop member AND `IsTerminalOrbitSynthesisSafeForLoopMember`), with a same-body
    control that must reach `EndpointTail`. The register's first option - drive the real
    caller - is not reachable headlessly: both call sites sit inside the TS lifecycle and
    the flight pending-create pass, which need live KSP.
  - F-ledger-career-038-01: the cell built a local `KerbalsModule` the static call never
    saw, so only the `?? false` fallback ran and `IsManaged` was never invoked. It now
    injects a module through `LedgerOrchestrator.SetKerbalsForTesting` that manages a
    DIFFERENT kerbal, and the null-module fallback is kept as its own cell
    (`ShouldSuppress_NoKerbalsModule_ReturnsFalse`).
  - Second commit, the 12 `logistics-route` ids: 12 strengthened, 6 of those also
    renamed (`FormatRejectMessage_AllEnumValuesProduceDedicatedText`,
    `SumRecoveredCredits_HonoursThePassedScopeSet_NotRouteMembers`,
    `Delivery_ProbeAndWriter_StoreInjectedLoadedGate_AndTheWriterDispatchesOnIt`,
    `EmitPendingRecoveryCreditCall_WithCareerKscPendingMarker_EmitsOwedCreditOnce`,
    `PaintMembership_IsClearedOnlyByAHideOrAFlush`,
    `UnloadedStoredPartNode_IsAFreshCopyCarryingTheOverriddenSlotAndUnits`),
    0 deleted, 0 deferred, still no production change.
    F-logistics-route-003-01 (the name says FRESH guid but 32-characters-and-non-empty
    is satisfied by one constant `DefaultIdFactory` return; the cell builds twice from
    the same analysis and pins distinctness).
    F-logistics-route-019-01, renamed `FormatRejectMessage_AllEnumValuesProduceDedicatedText`
    (the switch DEFAULT returns non-empty text, so a status with no branch passed the
    old sweep; each status must now produce copy that is not the fallback, with a
    control on an out-of-range status proving the default is still reachable).
    F-logistics-route-032-02 (`Latitude` / `Longitude` / `Altitude` pinned to the
    fixture values - the name said LOCATION and swapping the two assignments, which
    feed the M2 Phase 5 harvest-origin endpoint, left every other assertion green).
    F-logistics-route-020-02, renamed
    `SumRecoveredCredits_HonoursThePassedScopeSet_NotRouteMembers` plus a new
    `RecoveryInTreeButNotRouteMembers_StillCounted_ThroughResolvedScope`
    (`SumRecoveredCredits` takes the scope set as a PARAMETER, so the G1 rescoping
    regression cannot be introduced at that level; the new cell composes
    `ResolveTreeRecordingIds` over a committed tree with the sum, which is where the
    regression lives - the mutant reds it and leaves the retitled cell green).
    F-logistics-route-038-01 (the cell round-tripped a HAND-BUILT endpoint through
    `RouteNodeCodec` while its name and FAILS-IF comment claimed the BUILDER stamps the
    root id; it now drives `RouteBuilder.BuildRoute` over a bound docked-origin proof
    and asserts `Route.Origin.RootPartUId` before the codec half).
  - The four that needed a real drive:
    F-logistics-route-007-01, renamed
    `Delivery_ProbeAndWriter_StoreInjectedLoadedGate_AndTheWriterDispatchesOnIt`
    (no probe or writer METHOD was called, so the divergence the comment describes - a
    writer re-evaluating `vessel.loaded` per call - was uncovered; `WriteResource` is now
    driven on both writers and the `path=` token read off the delivery Info line. The
    PROBE stays construction-pinned and the cell says why: every probe entry point
    returns before its branch when `vessel == null`).
    F-logistics-route-008-02 (the installed fake applier itself clears `PendingDeliveryUT`
    and transitions back to Active, so two of the three post-tick reads could not see a
    production path that armed them first; the cell now captures all three INSIDE the
    applier, ahead of its bookkeeping, and keeps the post-tick reads).
    F-logistics-route-011-01, renamed
    `EmitPendingRecoveryCreditCall_WithCareerKscPendingMarker_EmitsOwedCreditOnce` plus a
    new `EndpointLostAtDelivery_WiringFlushesTheOwedRecoveryCredit` (the old cell called
    the shared helper directly and its comment conceded the wiring was "verified by
    reading"; the new one reaches `RouteOrchestrator.cs:4246` through a tick against an
    env whose delivery-time endpoint re-resolution fails. Note what the drive showed: TWO
    credits land on that tick - cycle-0's ordinary deferral flush and cycle-1's, armed by
    that same crossing and payable only by the endpoint-lost tail, since the route goes
    quiet immediately after. The cycle-1 row is the one the mutant removes).
    F-logistics-route-013-02 (the headline "competing route sees it" was test-side
    arithmetic, `250.0 - otherReserved`; the net is now taken through the production
    `RoutePickupSourceGate.NettedAvailable` - the one expression
    `LiveRouteRuntimeEnvironment`'s netted reader calls - and the competitor's gate is
    actually evaluated, asserting the exact `source-reserved:200:Depot B:Ore:Ore Run`
    hold token assembled the way the live env assembles it).
  - F-logistics-route-013-01 was NOT deleted into its twin. The register offered that
    (the body duplicated the first tick of
    `Shuttle_DebitsRefineryAtItsWindow_EscrowEmptyAfterCycle`) or "give it real content";
    the second is worth more, because 19.2.5 is a standing prohibition rather than a
    one-off. The route's backing recording now CARRIES the crashed disposition
    (`TerminalState.Destroyed` + `VesselDestroyed`) in a committed tree, so a
    disposition gate added later reds THIS cell and leaves the shuttle twin green - which
    the mutant demonstrates. The class now clears `RecordingStore` committed trees on
    both ends so the Sequential collection cannot inherit the fixture.
  - Two renamed because the claim in the old name needs Unity and no seam can carry it
    headlessly: F-logistics-route-016-01
    (`PaintMembership_IsClearedOnlyByAHideOrAFlush` - the frame-survival claim was two
    identical reads with nothing between them; no headless seam advances the draw pass's
    frame, and the staleness half is already pinned by
    `ResolveLegPaintFromMesh_IsMembershipAndTheDeadRendererGuard`. The cell gains an
    unrelated-recording paint in the middle, so membership is shown to be per recording
    and per leg rather than rebuilt wholesale) and F-logistics-route-033-02
    (`UnloadedStoredPartNode_IsAFreshCopyCarryingTheOverriddenSlotAndUnits` - the APPEND
    the old name claimed is done by the test's own `ConfigNode.AddNode`;
    `WriteInventoryUnloaded` is private and needs a live `ProtoVessel`, so the append
    stays in-game coverage. The cell now also pins that the built node is a COPY and the
    recorded payload keeps its origin slot).
- `testfix-t3-b` (2026-09-16): the second slice of Medium T3 rows
  (`work/phase-b-slice-medium-t3-02.txt`, 20 ids, all `recording-tree`). 17 strengthened,
  3 renamed, 0 deleted, 0 deferred. Every strengthened row has a proof row in
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`
  that `git apply --check`s against a clean tree. No production file changed.
  - Strengthened by making the guard the DECIDING term (the fixture was previously
    rejected by an earlier gate, or was a lone record that every implementation answers
    the same way): F-recording-tree-030-05 (a committed null / empty / same-ChainId peer
    at a HIGHER `ChainIndex`, in all three degenerate `IsChainMidSegment` cells, via a new
    `CommitChainPeer` helper); F-recording-tree-030-06 (an unrelated committed recording
    with a LATER `EndUT`, so the chain scoping in `GetChainEndUT` decides);
    F-recording-tree-030-07 (a committed null-ChainId peer at `ChainIndex -1`, which is
    exactly the expected predecessor index); F-recording-tree-030-08 (a committed EVA child
    whose `ParentRecordingId` is also empty, so the legacy parent-child loop would match
    `"" == ""`); F-recording-tree-029-03 (the cross-tree debris now carries a resolvable
    `tree_b` Breakup branch point naming `rec_origin`, so every other gate in
    `EnqueueDebrisChildren` passes and only the `TreeId` fence rejects);
    F-recording-tree-032-02 and -032-03 (both RPs carry a slot for the subject, so the
    `NotCommitted` merge-state guard / the chain tip's Landed `stableTerminal` reject
    decide instead of `noMatchingRpSlot`; -032-03 also pins the reject REASON, since the
    boolean is false for a Destroyed tip too); F-recording-tree-032-05 (a real supersede
    relation plus both recordings registered, with the ERS exclusion asserted, so the
    pass-through claim is witnessed and `skippedTombstoned=1` is pinned);
    F-recording-tree-052-01 (the orbit segment now ends PAST the last point, so the
    exact-boundary heuristic would say "use orbit" and only the persisted
    `TrajectoryPoint` phase rejects); F-recording-tree-030-09 (a pending tree is stashed
    before `ClearCommitted`, so the ONLY half of the name is actually pinned).
  - Strengthened by pinning the value instead of a bool: F-recording-tree-027-04 (both
    parser theories now assert the parsed components, so a y/z or x/w swap reds - the
    rejecting rows also pin the untouched `Vector3.zero` / `Quaternion.identity` out
    value); F-recording-tree-028-03 (the two written `ENTRY` nodes are read back and the
    original/replacement pairs compared order-independently, so a Save-side key/value swap
    reds).
  - Strengthened by adding the missing arm: F-recording-tree-025-01 (an engine-only
    positive and an all-zero-throttle negative, so both arms of `HasMeaningfulThrust`
    discriminate - the original cell was decided solely by the RCS arm);
    F-recording-tree-044-01 (mirror case: branch-point children ordered
    [different-PID debris, same-PID continuation], so a first-child-only scan reds - the
    all-different-PID cell cannot tell "every child" from "first child").
  - Repurposed: F-recording-tree-013-04, whose count of 50 was rejected by the bound gate
    before the sparse header was ever read (the same observable as the count-999 sibling).
    It now uses a count of 2 with one COMPLETE sparse point plus a truncated second, and
    asserts the `EndOfStreamException` together with the surviving first point's
    DEFAULTED body name - the sparse path the name claims. Renamed
    `TrajectorySidecarBinary_Read_SparsePointList_TruncatedSecondPoint_ThrowsEndOfStreamAfterDefaultedFirstPoint`.
    Its mutation clears the decoded points when the sparse loop hits end of stream, which
    reds this cell alone (207 passed, 1 failed) - the earlier mutant forced the dense
    branch and red the whole sparse family, so it could not show what this cell adds.
  - Source gates bounded / added: F-recording-tree-035-02 (gate 3 now runs inside the
    `BindLiveRecorderToSwitchSegment` body, sliced from its declaration to the
    end-of-Phase-C marker; run file-wide the canonical-bind regex matched the
    `CreateSplitBranch` undock recorder, so mutating the helper to `isPromotion: false`
    stayed green. The `recorder-bound` / `new-recording-id=` literals are now asserted
    inside the same body); F-recording-tree-033-02 (renamed
    `DirectForwardingPredicate_StampedIdAndReResolvedTag_Disagree`, which is all the old
    body proved, plus a new wiring gate
    `DirectForwardingCallSites_AllPassStampedEventRecordingId` that walks every
    `Source/Parsek` file with line comments stripped and requires each call site of
    `ShouldForwardDirectLedgerEvent` AND of the two wrappers that delegate to it
    (`ShouldForwardFacilityLedgerEvent`, `ShouldForwardDirectScienceSubject`) to pass
    a `<expr>.recordingId` form, never a re-resolved tag. A bare `recordingId` /
    `recordingTag` identifier is accepted only inside the two wrappers' own
    brace-matched bodies, where it is the parameter already carrying the stamped id;
    anywhere else that spelling can be a local alias re-resolved at decision time,
    which is the #431 defect class itself. 21-call-site floor against a collapsed
    scan. Proved by two mutants that the first draft of the gate survived: a local
    `string recordingId = ResolveCurrentRecordingTag();` alias at
    `GameStateRecorder.Handlers.cs:144`, and a re-resolved tag passed through the
    facility wrapper at `GameStateFacilityRecorder.cs:113`).
  - Renamed to what the cell proves (the claimed contract is unreachable from xUnit and is
    named in the body): F-recording-tree-042-04 ->
    `SafeWritePersistent_TestSeamPassthrough_MainMenuDestination_ReturnsSeamValue` (the
    test seam short-circuits before the try, so the MAINMENU hard-block catch is never
    reached; the destination reaching the seam is now asserted);
    F-recording-tree-050-08 ->
    `ChainSegmentManagerWithContinuationFields_LogsCreation_AndLeavesCommittedSnapshot`
    (the asserted `[Chain]` line is the CONSTRUCTOR log, not a boarding-preservation
    message - the boarding path needs a live `FlightRecorder`; the ctor fields are now
    asserted alongside); F-recording-tree-046-05 ->
    `ShouldSuppressEventPersistence_MarkerOwnedIdOutsideAttemptSet_LogsMarkerOwnedBypass`
    (its `Assert.False` cannot discriminate because the id is in neither the attempt set
    nor the cutoff map; the shared-id shape the register proposed already exists as the
    sibling `..._MarkerOwnedIdAlsoInAttemptSet_NotSuppressed`, added by the priority-2
    coverage commit `45dda34ee`, so the boolean half is covered and this cell keeps the
    log-line half under an honest name).
  - Filtered suite after the slice: 530 passed / 0 failed across `ChainTests`,
    `CommittedRecordingImmutabilityTests`, `DiscardFateTests`,
    `EffectiveLeafFinalizationTests`, `EffectiveStateTests`,
    `RecordingEndpointPersistenceTests`, `RecordingFinalizationCacheProducerTests`,
    `RecordingStorageRoundTripTests`, `RecordingStoreTests`, `CrewReplacementTests`,
    `SceneExitInterceptorTests`, `SessionSuppressedSubtreeTests`,
    `SwitchSegmentConsumeTests`, `SwitchSegmentSuppressionNarrowingTests` and
    `GrepAuditTests`.
- `testfix-t3-f` (2026-09-16): the SEVENTH and last slice of Medium T3 rows
  (`work/phase-b-slice-medium-t3-07.txt`, 17 ids: 3 `map-render`, 6 `mission-groups`,
  2 `spawn-vessel`, 5 `trajectory-orbit`, 1 `ui-settings`). Counting rule: every kept row
  is strengthened, and a rename is a SUBSET of that, never a separate bucket. Slice total:
  16 strengthened, 7 of those also renamed, 1 deleted, 0 premise-wrong, no production
  change. Per-commit split, derived from the diff: 3 rows (`2cef68a22`), 6 rows
  (`be908585b`), 4 rows (`8de257ffa`, one of them the deletion), 4 rows (`f4b2dc0e1`).
  Each row has a proof row in
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`
  that `git apply --check`s against a clean tree. The EIGHTEENTH row of the slice,
  F-spawn-vessel-021-01 (`SpawnCleanupGuardTests`), was deliberately HELD BACK: another
  open PR rewrites that file, so the fix would conflict; it stays untouched in the
  register.
  - Renamed off a claim the body does not make (each also strengthened):
    F-map-render-020-01 ->
    `NaNClearance_EveryRestoredPoint_RoutesThroughRendererToRecordedAltitude` (no v8 file
    is ever written or read - `TrajectorySidecarBinary.Write` always stamps the current
    version and older generations are rejected outright - and the all-NaN fixture let a
    helper stubbed to `return recordedAltitude` pass, so a finite-clearance control point
    was added); F-map-render-023-01 ->
    `AllowPointHermiteInterpolation_AllOutcomes_GuardOrderDecidesReason` (a six-row theory
    over the four outcomes, including the two rows where two guards would fire at once);
    F-mission-groups-006-01 ->
    `UnitMemberOverlaps_ScheduleCarryingUnitWithSpanFlooredCadence_False` (the cell floors
    the cadence itself, so it says nothing about the builder; it now states that scope and
    gains an overlapping mirror); F-spawn-vessel-007-01 ->
    `ResolveActions_ModuleLightOffPlusColorChangerOn_IsAnAllFalseOpinion_Skipped` (the old
    `NotEqual(true, ...)` was satisfied by a null from the all-false skip, not by the `??`
    order); F-trajectory-orbit-006-01 -> `PureAngleAxisSpin_*` for the two surviving
    SpinForward cells (there is no production SpinForward; the composition lives in
    `ParsekFlight.ComputeOrbitalRotation`); F-trajectory-orbit-014-01 ->
    `Classify_FlattenedGatherWithInterloper_CollapsesTheTransfer`;
    F-trajectory-orbit-014-02 -> `Classify_IkeAfterDunaArrival_TargetsDuna_Supported` (the
    old name said NotSupported / Deferred while the assertions proved the opposite).
  - Strengthened by adding the arm or the polarity that was missing:
    F-mission-groups-003-02 (a populated `LoopUnitSet` positive that resolves the owning
    unit, plus the negative-parent guard re-asserted against that same populated set - the
    empty set had masked it); F-mission-groups-003-04 (the false polarity, the only input
    where the preserved value and the real value differ); F-ui-settings-002-02 (both
    negatives of `isFuture && rec != null`, mirroring its `ShouldShowRewindButton`
    sibling); and F-trajectory-orbit-014-02's new Kerbin -> Sun -> Ike cell, the
    direct-child decline nothing in the suite reached - its mutant was green across every
    Reaim test class.
  - Strengthened by making the named term the deciding one:
    F-map-render-021-01 (the byte edit was landing on the anchor-candidate count at the end
    of the file, not the bitmap length nine bytes back, so a guard several steps past the
    named one was doing the rejecting; the failure text is read now);
    F-mission-groups-003-03 (`newLiveMemberIndex` equalled `watchedIndex`, so the later
    guard answered on its own); F-mission-groups-007-01 (the `InRange` bounds were the scan
    window and the shift range, which hold for every possible return value; k=1, d=+1 and
    the 15 s residual are hand-derived now, with a later-k mirror); F-mission-groups-013-02
    (`statusOrder` is 1 for either active candidate, so the chosen recording's countdown is
    pinned instead); F-spawn-vessel-002-01 (the expectation was the helper's own delegate,
    so a change inside the shift moved both sides; the hand-computed value is pinned);
    F-trajectory-orbit-017-03 (lat=0 lon=0 zeroes both y and z, so an axis swap, a lat/lon
    transposition and a degree/radian error all survived; a lat=30 lon=60 point pins all
    three components).
  - Pinned at the source because the site cannot run headless: F-trajectory-orbit-006-01
    gains `ComputeOrbitalRotation_SpinForwardComposition_UsesOmegaTimesDtInDegrees`, a gate
    over the brace-matched body of `ParsekFlight.ComputeOrbitalRotation` (comment-stripped,
    literal-masked) pinning the axis, the `|omega| * dt` angle in DEGREES, the
    `AngleAxis * boundary` order and the non-spinning arm's orbital-frame decode. The cells
    above can only drive COPIES of that arithmetic: the site needs a live `Orbit` for
    `getOrbitalVelocityAtUT` / `getPositionAtUT`. It is a SOURCE gate, not a behavioural
    proof - it pins how the composition is SPELLED, so an equivalent rewrite (`Mathf.Rad2Deg`
    replaced by `180.0 / Math.PI`) would red it although nothing moved; the mutations.csv row
    says so too. It normalizes line endings, anchors on the DECLARATION (which occurs once,
    so no call site can match) and confirms the brace it found opens a method body, then runs
    its assertions against both an LF and a CRLF rendering: `ParsekFlight.cs` is stored with
    LF and checked out CRLF on a `core.autocrlf=true` worktree, and the first version of this
    gate carried a CRLF needle that passed locally and would have red on CI.
  - Reached through the real builder: F-trajectory-orbit-014-01 gains
    `MissionLoopUnitBuilderTests.ReaimClassification_IsPerMember_AParkedStationDoesNotCollapseTheTransfer`,
    which adds a station parked in Kerbin orbit for the whole heliocentric coast to the
    flown Duna-direct fixture and asserts the unit still engages re-aim on the transfer
    member with the same departure UT and tof. The mutant is the flattened gather; it is
    invisible to every other builder fixture, all of which are single-member.
  - Deleted: F-trajectory-orbit-006-02
    (`SpinForward_ZeroAngVel_FallsBackToOrbitalFrame`). No fallback code ran and both
    assertions were verbatim duplicates of `IsSpinning_DefaultSegment_ReturnsFalse` and
    `HasOrbitalFrameRotation_IdentityQuaternion_ReturnsTrue`, which red under the same
    mutants (the inverted `IsSpinning` reds five OrbitSegmentTests cells with the deleted
    cell gone; the constant-false `HasOrbitalFrameRotation` reds its twin plus two
    `BallisticExtrapolatorTests` cells). The branch its NAME claimed is the 3-way branch in
    `ComputeOrbitalRotation`, now pinned by the composition gate above.
  - Already covered, so no duplicate cell was written: the register's builder-level
    proposal for F-mission-groups-006-01 (the `minSpacing = 0.0` mutant reds the
    pre-existing
    `MissionPeriodicityTests.Build_DriftingLongSpan_ThrottleFlooredAtSpan_NonOverlappingByConstruction`,
    which IS that proposal), and the ON direction of the light precedence for
    F-spawn-vessel-007-01 (the `??` swap reds the pre-existing
    `ResolveActions_ModuleLightOnWinsOverColorChangerOff`; the OFF direction was the
    unguarded half, and it is what the new blinking cell covers).
  - Doc references updated for the renames: `docs/dev/done/design-orbital-rotation.md`
    (items 13, 14, 21) and `docs/dev/done/todo-and-known-bugs-v5.md` (the P2-2 entry).
  - Filtered suite after the slice: 0 failed across `TrajectorySidecarBinaryTerrainTests`,
    `OutlierFlagsSidecarRoundTripTests`, `AnchorCorrectionConsumerHookTests`,
    `MissionSpanClockTests`, `MissionZeroDriftScheduleTests`, `MissionLoiterKnobTests`,
    `GroupAggregateTests`, `SpawnSafetyNetTests`, `SnapshotBaselineApplicationTests`,
    `OrbitSegmentTests`, `ReaimClassifierTests`, `MissionLoopUnitBuilderTests`,
    `MissionPeriodicityTests`, `TrajectoryWalkbackTests` and `TimelineWindowUITests`.
- `testfix-t3-g` (2026-09-16): slice 8 of the Medium T3 rows
  (`work/phase-b-slice-medium-t3-08.txt`), a single row held back from slice 7 because
  PR #1724 rewrote its file underneath it. 1 strengthened, 1 of those also renamed,
  0 deleted, 0 deferred, no production change. With this row the Medium T3 register's
  remaining work is slices 6 and 7, each on its own branch.
  - F-spawn-vessel-021-01, renamed
    `RevertCleanupArming_WithProductionCollector_ArmsOnlySpawnedVessels`: the cell called
    `RecordingStore.CollectSpawnedVesselInfo` and then assigned `PendingCleanupPids` /
    `PendingCleanupNames` itself, so the revert arming its old name claimed
    (`RevertPath_SetsCleanupData_WhenNotAlreadySet`) never ran. PR #1724 lifted that step
    out as `ParsekScenario.ArmRevertCleanupData(collector)` and added both guard arms
    around it, which made the ALREADY-COVERED question worth asking first: it is not.
    Both #1724 cells inject a FAKE collector, so the register's named mutant
    (`CollectSpawnedVesselInfo` skipping nonzero `SpawnedVesselPersistentId`) leaves both
    of them green - it reds only the three hand-rolled cells. The re-aimed cell closes
    that gap by passing the production collector itself, exactly as `OnLoad` does, and
    asserting the armed sets ARE its return value; a second recording with no spawned pid
    is the discriminator between the spawned-vessel collector and the wider all-names one.
    It is therefore not a duplicate of either #1724 cell, and it reds under both mutants:
    the register's, and a stub of `ArmRevertCleanupData` that never arms (the recorded
    patch), which the old cell survived.
  - Filtered suite after the slice: `SpawnCleanupGuardTests` 6 passed / 0 failed, the
    same count as before (a rename, not an added cell).

- `testfix-t2-a` (2026-09-16): the Medium register's single T2 (duplicate) row
  (`work/phase-b-slice-medium-t2-01.txt`). 0 strengthened, 1 deleted, 0 deferred, no
  production change. With this row and Medium T3 slice 7 the Medium register is closed.
  - F-ghost-playback-012-01, DELETED: `GhostChainWalkerTests.CrossTreeCycle_DetectedAndHandled`
    built the same fixture as `CrossTree_TwoLinks_ChainsExtend` - identical recordings, pids,
    branch points and UTs - and asserted a strict subset of its claims (non-null, key 100,
    `Links.Count >= 1`, non-empty tip, against the twin's `Single`, `Links.Count == 2`,
    tip `R2-leaf`, `SpawnUT == 1320`). Nothing in it was distinct, so nothing was kept.
  - The cycle its name claimed is unreachable from that fixture: the tip vessel pid equals
    the walk origin, so `MergeCrossTreeLinks` breaks at the `tipVesselPid == originPid`
    test and never reaches the `chainVisited` guard. That guard is covered by
    `MergeCrossTreeLinks_TwoChainsPointingAtEachOther_BreaksCycleAndWarns` (PR #1724).
  - The register's own falsifiability line is WRONG and is recorded as such in
    `mutations.csv`: its mutant (stop absorbing the linked chain in `MergeCrossTreeLinks`,
    `mutations/F-ghost-playback-012-01-phaseB.patch`) leaves BOTH the deleted cell and the
    twin green - 148 passed / 1 failed, the one red being the #1724 cycle cell. The #1724
    mutant (`mutations/C-ghost-playback-012-01-phaseB.patch`) gives the identical picture.
    Neither discriminates, because neither cell's fixture reaches the absorption at all.
  - Subsumption was proved instead with a probe mutant (`chain.SpawnUT = leaf.EndUT` ->
    `leaf.StartUT`): the twin reds, the deleted cell stays green. Same input, strictly
    weaker asserts, so the deleted cell could only fail where the twin already fails.
  - Filtered suite (`GhostChainWalkerTests` plus every other class reaching
    `MergeCrossTreeLinks` through `ComputeAllGhostChains`: `ChainEvalOnLoadTests`,
    `ChainGhostTrajectoryTests`, `ChainSaveLoadTests`, `Bug171To174Tests`,
    `DisassembledTerminalStateTests`, `SessionSuppressionWiringTests`): 149 passed / 0
    failed before, 148 passed / 0 failed after - exactly the one removed cell.

- `testfix-low-02` (2026-09-22): Low T1 slice 2 (`work/phase-b-slice-low-t1-02.txt`, 16
  ids: 3 `recording-tree`, 2 each `catchall`, `legacy-bugfix`, `recorder-events`,
  `rewind-refly`, 1 each `map-render`, `mission-groups`, `spawn-vessel`,
  `trajectory-orbit`, `wiring-gates`). Counted the slice-4 way: 11 strengthened, of which 5
  renamed; 3 deleted; 0 deferred; 2 premise-wrong. Three cells were added (one mirror, two
  call-site source gates, both "source-gated, fixture-limited"), two siblings were re-aimed
  alongside their row, and four siblings were deleted as subsumed (each green under every
  mutant tried, its claim carried by the re-aimed row). Two behaviour-identical extractions:
  `GhostPlaybackEngine.ResolveDestroyedGhostName` and `ParsekFlight.WireBreakupIntoTree`;
  every class that reads either production file re-ran green (427 and 857 cells). Per
  commit, derived from `git diff origin/main...HEAD -- Source/Parsek.Tests`: 8f27c01a8 4
  renames + 2 new + 2 deletions, 943e05977 2 renames + 2 deletions, 9cba48eca 2 deletions,
  23d453c2f 1 deletion, ee289af56 1 new. Each row has a proof row in `mutations.csv` and a
  `*-phaseB.patch` that `git apply --check`s against the branch tip.
  - Strengthened: F-catchall-050-02 / -03 (renamed `CodecLoad_*`: the cells loaded through
    `ParsekScenario.LoadRecordingMetadataForTests`, whose Save/Load pair has NO production
    caller; they now load through `RecordingTreeRecordCodec.LoadRecordingFrom` into a
    pre-seeded target and with orphan orbit keys, plus the mirror
    `CodecRoundTrip_LocationFields_Survive`), F-legacy-bugfix-001-01 (renamed; the
    `FallsToDefault` sibling re-aimed with it), F-legacy-bugfix-025-03 (save through
    `SaveRecordingFilesToPathsForTesting`; the out-of-band sibling re-aimed as the
    incrementEpoch=false mirror), F-map-render-027-01 (renamed; raw 0x81 / 0x80 through the
    binary codec - 73 flag / sidecar cells were green under a reserved-bit-masking reader),
    F-mission-groups-013-03 (renamed; the register's "no negative duration is
    constructible" is wrong: ExplicitStartUT alone reads StartUT=500, EndUT=0),
    F-recorder-events-024-03 (GameEvents Add/Remove run headless, contrary to the cell's own
    comment), F-recording-tree-027-02, F-rewind-refly-009-02, F-trajectory-orbit-016-01 (a
    consistent order flip on both encode and decode was green across 1,511 relative /
    anchor / debris cells) and F-wiring-gates-003-03.
  - Deleted, twin reds under the row's mutant across every class reaching the method:
    F-recording-tree-027-01 (`RecordingFieldExtensionTests.MaxDistanceFromLaunch_RoundTrip_PreservedAcrossReload`),
    F-recording-tree-028-01 (`CrewReplacementTests.SaveCrewReplacements_WithData_RoundTrips`),
    F-spawn-vessel-013-03 (`SeedUT_RecordingStartUTInFuture_ReturnsCurrentUT`; at equality
    both branches return the same value, and the `>=` -> `>` mutant is green class-wide).
  - Premise-wrong, kept: F-recorder-events-007-02 (the before-start return in
    `GetActiveCycles` is an equivalent mutant next to the `lastActiveCycle` clamp; the cell
    pins the output and reds only when both go; the proposed TryCompute pin already exists
    as the currentUT=99 theory row; a comment now says so) and F-rewind-refly-020-04
    (already re-aimed by 8d0c07363, after the audit snapshot; reds under the row's mutant
    with no edit).
  - Follow-up, not done here: `ParsekScenario.SaveRecordingMetadata` /
    `LoadRecordingMetadataForTests` are test-only (11 test files use them), so every other
    cell built on that pair pins a copy of the codec rather than the codec. CLOSED by
    `retarget-recording-metadata-tests` (2026-09-22): every such cell now drives
    `RecordingTree.SaveRecordingInto` / `LoadRecordingFrom`, the pair is deleted, and the one
    key the codec lacks is filed as LOOP-TIME-UNIT-NOT-PERSISTED.
- `testfix-low-03` (2026-09-22): Low T1 slice 3, the remainder of slice 1
  (`work/phase-b-slice-low-t1-03.txt`, 14 ids; slice 1's other two rows,
  F-analyzer-002-02 and F-rewind-refly-019-02, are on `testfix-low-01`). Counting rule:
  every kept row is strengthened, and a rename is a SUBSET of that. Slice total: 11
  strengthened, 5 of those also renamed (one split into two cells), 3 deleted, 0
  premise-wrong. Per-commit split, derived from the diff: 4 rows (`429e2e205`), 4 rows
  (`6de00c759`, one deletion), 4 rows (`bcadedb4d`, two deletions), 2 rows (`b1f13d313`).
  Each row has a proof row in
  `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and a `*-phaseB.patch`
  that `git apply --check`s against this branch. Two behaviour-identical extractions:
  `ParsekUI.ComposeScrollbarGutterWidth` and `FlightRecorder.FormatGrowthRateAtStop`; the
  two patches that mutate them apply on top of those extractions.
  - Renamed (each also strengthened): F-catchall-040-02 ->
    `OnRailsSoiTransition_ProducerClosesAndOpensAtTheOneBoundaryUT` (drives
    `TransitionTrackSectionAtSoiBoundary`, which picks both boundary UTs from one input);
    F-ghost-playback-015-01 ->
    `TheNumbersAreInvariantFormatted_UnderACultureWithANonAsciiMinusSign` (every token is
    an int, so only the negative sign can separate invariant from culture formatting, and
    recIdx -1 is a real input); F-harness-seam-008-01 ->
    `AutorunEnv_IsReadOnlyInParseAutorunConfigOnce_WhichOnlyAwakeCalls` (source-gated,
    fixture-limited: the read-once caller is a MonoBehaviour); F-recording-tree-052-08 ->
    `CommitScienceSubjects_EqualValue_RetainsValueAndIsNotCountedAsAnUpdate` (the register
    proposed DELETE; declined, because the equal value is the only input separating `>`
    from `>=`, and a `>=` mutant was green across `CommittedScienceDictTests` and
    `GameStateEventTests` - the `updated` counter in the commit summary is the witness);
    F-rewind-refly-019-01 -> split into
    `NullProvisional_WithInPlaceMarker_ReturnsWithoutTouchingTheCommittedTree` and
    `NullMarker_WithProvisional_ReturnsWithoutTouchingTheCommittedTree`, each red under its
    own single-guard deletion.
  - Strengthened in place: F-catchall-023-03 (header padding through the shipped gutter
    rule), F-ledger-career-015-05 / -015-06 (drive `ApplyToRoster` over the class's fake
    facade, which now records `TryRecreateStandIn` requests; the two rows need mirror
    mutants - deleting the call-site guard, and skipping every displaced entry - because
    each leaves the other cell green), F-logging-004-01 (the production formatter's exact
    line under de-DE), F-recorder-events-001-02 (event-free roundtrip through the
    production codec), F-recorder-events-001-04 (`RecordingTree.LoadRecordingFrom` over a
    `BuildV3Metadata` node: a legacy `Build()` node is rejected by the schema gate before
    the linkage keys are read, which kept the first attempt green under the mutant).
  - Deleted, each after its named twin red under the row's mutant across every class
    reaching the method while the deleted cell stayed green:
    F-legacy-bugfix-020-01 (twin `HybridSpike_TotalBelowBudget_DoesNotFireBreakdown`;
    the hybrid `totalMs > 0 ? ... : "n/a"` fraction sentinels are unreachable behind the
    8 ms budget guard - a mutant rewriting one leaves all 70 cells of the five breakdown
    classes green - and the proposed mainLoop replacement already exists as
    `Bug460MainLoopBreakdownTests.ZeroTrajectoriesAndOverlap_RendersMeanAsNa`),
    F-logistics-route-035-01 (twin
    `TheRelayIsEligibleWithRoverBAsTheSourceAndLanderAAsTheDestination`; the dump's
    `ITestOutputHelper` constructor and `FormatDouble` helper went with it),
    F-recorder-events-001-05 (twin `PartEvents_SerializationRoundtrip_LightOn`).
  - Filtered classes after the slice: `TableRowInsetAlignmentTests` 9,
    `ReferenceFrameTrackingTests` 20, `GhostPartEventApplyLogTests` 28,
    `AutorunHooksTests` 73, `KerbalReservationTests` 64, `Bug581HybridBreakdownTests` 14
    (15 before), `ObservabilityLoggingTests` 18, `RoverRelayCOracleTests` 3 (4 before),
    `PartEventTests` 164 (165 before), `CommittedScienceDictTests` 7,
    `MergeJournalForkMigrationTests` 6 (5 before), all passed / 0 failed.
- `testfix-low-01` (2026-09-16, OPEN, do not merge until reviewed): the Low T1 sweep's
  first slice is PARTIAL - 2 of 16 rows proven and landed; the remaining 14 wait on
  dedicated implementer dispatches (`work/phase-b-slice-low-t1-01.txt`).
  - F-analyzer-002-02, strengthened + renamed
    `Key_SameFindingTwice_IsStable` -> `Key_SeparateInstancesSameShape_EqualKeys_RuleStillDistinguishes`:
    the cell compared `KeyOf(f)` with itself. It now keys two separate findings with
    numerically drifted messages (equal keys) and a different RuleId (different key).
    RED under both mutants (17 passed / 3 failed each: the named cell plus two pre-existing
    Gate / Apply / MultiMatch cells that also key findings): the register's default-key stub
    (`mutations/F-analyzer-002-02-default-phaseB.patch`) and a digest-mask drop
    (`mutations/F-analyzer-002-02-phaseB.patch`). The old cell stays GREEN under the
    default-key stub.
  - F-rewind-refly-019-02, strengthened: `EmptyRpId_ReturnsZero` became a Theory over
    null and empty rpId, each with an orphan whose own `ProvisionalForRpId` matches the
    call, plus a no-reap log assertion. RED 0 passed / 2 failed under the register's
    deleted-early-return mutant (`mutations/F-rewind-refly-019-02-phaseB.patch`);
    restored class 8 passed / 0 failed. Earlier GREEN mutant runs came from a stale
    testhost and are superseded; the trace-confirmed RED run is recorded.
  - No production change; both patches revert to the base tree. Serialized full suite:
    23,820 passed / 0 failed / 1 skipped.

- `testfix-low-05` (2026-09-22): Low T1 slice 5 (`work/phase-b-slice-low-t1-05.txt`),
  16 rows. 10 strengthened (9 also renamed), 5 deleted (7 cells), 1 premise wrong, 0
  deferred. Three production helpers were extracted with no behavior change; nothing else
  in production changed. Slices 2-4 are on their own branches, so with this slice the Low
  T1 register is closed. Each row was proved with its mutant (patches under
  `mutations/<id>-phaseB.patch`, one row each in `mutations.csv`). Every old cell was GREEN
  under its mutant in one combined run on the base tree (14 passed, the OQ1 cell skipped).
  The one exception is PreLaunchFields_DefaultToZero, which the initializer mutant does
  red; it is deleted as subsumed, not as vacuous.
  - F-mission-groups-012-01, renamed `BuildForDisplay_SilencesLineAndRestoresPriorFlags`:
    the cell set and restored the suppress flags itself and never reached the Missions
    window. The `GetMissionView` wrap moved, unchanged, into
    `MissionStructureBuilder.BuildForDisplay`. A Theory over mixed prior flags now reds all
    three rows when the finally-restore is dropped.
  - F-recorder-events-005-01, renamed `CheckpointAllVessels_EmptyBackgroundMap_LogsAllZeroSummary`:
    it asserts the all-zero summary line. The register's whole-body stub reds 8 cells in
    the class. An empty-map early return reds this cell alone, and that is the recorded
    patch.
  - F-recorder-events-013-03, renamed `SetDebrisExpiry_StoresExpiryPerPid_LaterCallOverwrites`:
    it drives the production TTL writer `ParsekFlight.ProcessBreakupEvent` calls, where
    before it only used the test-only inject and read accessors.
  - F-recorder-events-013-04, renamed `ProcessPendingSplitChecks_OnePending_DrainsAndDispatchesOnce`:
    a test-only `InjectPendingSplitCheckForTesting` (in `BackgroundRecorder.Testing.cs`)
    seeds a check whose parent recording is absent. The drain then dispatches into
    `HandleBackgroundVesselSplit`'s not-found guard with no live vessel.
  - F-recording-tree-026-01, renamed `TerminalState_AllValues_RoundTripThroughRecordCodecAsInts`:
    every member goes through `RecordingTreeRecordCodec`. The enum-name write also reds
    the three per-kind cells. A loader that rejects `Disassembled` reds only this one.
  - F-recording-tree-026-02, renamed `..._DefaultsZeroAndKeepsTree`: it has a
    current-generation child, asserts that the tree survives, and parses a sentinel `7`.
    The register's literal mutant is EQUIVALENT (a fresh tree's field is already 0). The
    recorded mutant, which drops a tree whose version key is missing, and a
    parse-ignored mutant each red this cell alone.
  - F-recording-tree-050-10, renamed `ContinuationVesselDestroyed_HandlerLogsSnapshotPreservedThroughFormatter`:
    the line moved, unchanged, into `ParsekFlight.FormatContinuationVesselDestroyedMessage`.
    The cell drives it after the real mark, with a mirror for a nulled snapshot, and reads
    the `OnVesselWillDestroy` call count from IL.
  - F-rewind-refly-014-03, renamed `UTFlow_AdjustedUTCapturedBeforeYield_SetUniversalTimeAfterIt`:
    it is source-gated and fixture-limited. The comment-stripped
    `ApplyRewindResourceAdjustment` body must capture before the first yield, set UT after
    it, and never re-read the global. The runtime half is `EndRewind` clearing the value.
  - F-spawn-vessel-020-03, renamed `MarkSpawnBlocked_StampsBlockedSinceUT_WalkbackTimeoutMeasuresFromIt`:
    the stamp moved, unchanged, into `VesselGhoster.MarkSpawnBlocked`. The cell drives it
    into `ShouldTriggerWalkback` and reads the `SpawnAtChainTip` call from IL.
  - F-wiring-gates-005-01, strengthened: the cell now requires the probe to be consulted
    exactly once. A fall-through mutant (probe consulted, then the live lookup) is
    equivalent under xUnit and stays green; the log assertion tried against it was
    vacuous and was removed.
  - DELETED, each twin red under the row's mutant across the classes that reach the SUT:
    - F-spawn-vessel-015-01: `PreLaunchFields_MissingKeysDefaultToZero`, plus 6 more.
    - F-spawn-vessel-020-01, both inline copies: `DeferredSpawnTests.ShouldCheckForSpawnDeath_*`.
      The mirror cell asserted the check ENTERS with pid 0, which is the opposite of
      production.
    - F-trajectory-orbit-015-02, both cells: the codec round trip of `anchorPid`. The
      Absolute default is a struct default, so no mutant exists for it.
    - F-trajectory-orbit-018-02: `RoundTrip_AtmosphericAbsolute_5Frames`, under a codec
      funds-drop mutant. The register's type-change mutant is a compile break.
    - F-ui-settings-003-01: `CurrentRecordingFormatVersion_IsV1`.
  - F-supplement-001-02, PREMISE WRONG, cell kept: the skipped cell is the inverse
    assertion that a second recovery clock must satisfy. It is not a duplicate, and
    `done/research/logistics-recovery-clock-memo.md` names it the acceptance fixture. The
    flush-UT mutant reds both active twins. Only the stale memo path was fixed.
  - Renamed-cell references updated in `done/plans/task-1-data-model.md` and
    `done/todo-and-known-bugs-v7.md`.
  - Filtered suite after the slice (the 14 touched classes): 400 passed / 0 failed / 1
    skipped. That is 5 fewer than before: 7 cells were deleted and one Fact became a
    three-row Theory.
- `testfix-low-04`, Low T1 slice 4 (2026-09-22): all 16 rows of
  `work/phase-b-slice-low-t1-04.txt` (5 `catchall`, 3 `legacy-bugfix`, 3 `map-render`, 2 `ledger-career`, 1 `ghost-playback`,
  1 `harness-seam`, 1 `logistics-route`). Counted the slice-4 way: 12 strengthened, of
  which 5 renamed; 4 deleted; 0 deferred; 0 premise-wrong. One sibling cell was added.
  Per commit, derived from `git diff origin/main...HEAD -- Source/Parsek.Tests`:
  d01a4c7d1 six Facts folded into two Theories + 1 new cell, adae178a2 1 rename + 1
  deletion, c560ecb80 1 rename + 1 deletion, 4a08cc759 2 renames + 1 deletion. Each row
  has a proof row in `done/research/test-quality-audit-2026-09-14/mutations/mutations.csv` and
  a `*-phaseB.patch` that `git apply --check`s against a clean tree.
  - Given the production term the name claims (7): F-catchall-022-01 (the three Patch*
    call sites now select their session toast latch through the behaviour-identical
    `KspStatePatcher.DrawdownGuardSessionToastLatch`; the cell emits through all six
    statics and requires the reset to re-arm each, where it used to re-arm its own local);
    F-catchall-023-01 (a NON-zero pid after the reset, with a call-counting override that
    must not be consulted, plus the guid resolver half); F-catchall-031-02 (Assert.Equal,
    and a null-only mutant reds the named cell alone); F-harness-seam-002-02 (a real
    `InGameTestRunner(null)` - discovery needs no host - so `ClearAllSceneHistory`'s own
    flag decides; half the gap had already closed on main in ecb1ec1d9);
    F-ledger-career-008-01 (a bare CREW node, so only `DeserializeFrom`'s defaults can
    answer); F-map-render-011-01 (two candidates at distinct UTs, emitted in reverse UT
    order); F-legacy-bugfix-024-01 (source-gated, fixture-limited: the three
    `PersistFinalizedRecording` context literals are read from their own brace-matched
    method bodies, one call per body, declaration anchored once).
  - Renamed to what they prove (5): F-ledger-career-007-04
    (`AddEvent_NonResourceEvents_AppendInArrivalOrder`; the "most recent facility" scan
    was test-local); F-legacy-bugfix-008-01
    (`ConvertMilestoneAchieved_UnparsableDetail_ZeroRewardsWithoutThrowing`; a
    well-formed zero cannot tell parsed from never-parsed, so the Theory feeds details the
    parse must reject); F-logistics-route-025-01
    (`FormatSourceRecordingDisplay_LargePosition_NoThousandsSeparator`; dropping
    InvariantCulture is value-equivalent for a positive int, recorded GREEN before and
    after); F-map-render-025-01 (`AnchorSourceAndSide_AreByteBacked`); F-catchall-031-01
    (the three `ShouldRecordFlagEvent` Facts fold into
    `ShouldRecordFlagEvent_NullVessel_ReturnsFalse` over three placedBy values: no headless
    Vessel passes the null check, and the register's mutant is value-equivalent because
    `CrewContainsKerbalNamed` rejects an empty name on its own - the new
    `CrewContainsKerbalNamed_NullOrEmptyName_ReturnsFalse` pins that guard and is the proof).
  - Deleted in favour of a named twin (3): F-ghost-playback-018-01 (twin
    `GhostPlaybackEngineTests.ClearLoadedVisualReferences_ResetsPendingSplitBuildState`) and
    F-legacy-bugfix-020-02 (twin `HealthCounters_Reset_ZerosReentryFxDeferred`): under a
    mutant where the default value matters, each twin is the only red across every class
    that reaches the SUT. F-map-render-025-02 (twins
    `AllowAnchorCorrection_NoAnchorInStore_ReturnsFalse` / `_WrongSection_ReturnsFalse`):
    the twins are NOT the only reds. Under its patch (TryLookup reports a miss as found)
    17 cells fail, 439 passed / 17 failed: the two named twins, three
    `RenderSessionStateTests.TryLookup_*` cells, one `EnsurePassIntegrityTests` cell and
    eleven `AnchorPropagationTests`; the deleted cell was green among the passes.
  - Deleted with the coverage gap still OPEN (1): F-catchall-060-02. The two redundant
    wheel-damage cells fold into the null-transform cell
    (`IsRendererOnDamagedTransform_NullTransform_ReturnsFalseForAnyNames`), but no cell reds
    under any mutant of the guard: deleting the names clause, the transform check, or the
    whole guard all stay green, because the ancestor walk's own null test answers false for
    a null start. The register's "red by NullReferenceException" is wrong. The guard's names
    half and the parent walk need a live Transform; the in-game cell the register proposed
    is filed as `TQ-2-wheel-damage-guard-needs-live-transform` in `todo-and-known-bugs.md`.

- `testfix-low-t2-01` (2026-09-23): the Low T2 (duplicate) register, all 231 rows, plus
  the five Low T6 (organization) rows. With this PR the Low T2 and Low T6 registers are
  closed. The per-row outcome of every T2 row is in `work/phase-b-low-t2-dedupe.tsv`.
  Method: a read-only Sonnet classifier paired each cell with its twin; a mechanical
  statement-subset check proved the cells whose every statement also appears in the twin;
  an Opus approver compared the rest side by side, checked each production claim in source
  and rejected when in doubt; and a guard refused any removal whose twin was itself being
  removed. Outcomes: 175 removed (35 `removed-mechanical`, 140 `removed-opus-approved`),
  21 `kept-reviewer-found-unique-coverage`, 2 `kept-theory-duplicate` (data-driven rows),
  1 `kept-held`, 24 `declined-cosmetic-fold` (Theory folds that are organization only and
  gain no failure mode), and the 8 "B" rows below. No production change. Each B row has a
  `mutations/<id>-phaseB.patch` that `git apply --check`s against the branch and one row in
  `mutations.csv`. One combined run of the old test files under all eight patches read
  408 passed / 8 failed: each old cell red, each old twin green, except where noted.
  - Assertion moved into the twin, cell removed (6), each twin the only red in its class
    under its patch: F-catchall-006-03 (`Assert.Same` on the origin instance; mutant: the
    rollback swaps in a `DeepClone`), F-recorder-events-024-05 (the `OnBackgroundPartDie`
    prefix; mutant drops it), F-rewind-refly-004-04 (no defensive Immutable Warn on the
    self-rewind path; mutant emits it and still retires), F-catchall-003-01 (the absolute
    first-event UT of every RCS showcase entry; test-side generator mutant, offset 0 -> 3),
    F-ghost-playback-008-04 (`result=True`, where the twin matched a bare `True`; mutant
    renames the token), F-catchall-040-03 (the WARNING marker on the unknown-type line;
    mutant drops it).
  - F-ghost-playback-017-02, PREMISE WRONG, cell removed: the twin
    `ChooseStrategy_OrbitalTerminal_ReturnsOrbital` already asserts the log line and the
    return value, and the cell was a strict subset. Under a body-dropping probe mutant
    both the cell and the twin red.
  - F-rewind-refly-005-01, KEPT AND RENAMED
    `RetirementPointingAtImmutable_NoSourceRelationId_RestoresRelationUnderLegacyRestoreId`:
    only its arrange (no `SourceSupersedeRelationId`) reaches the `rsr_legacyrestore_`
    fallback, so the assertion cannot move. It now asserts the Old/New ids and the prefixed
    id. The old cell was green under the prefix-dropping mutant; the renamed cell is the
    only red.
  - T6, no mutation proof (organization rows):
    - F-analyzer-003-02: `RecordingSectionDump.Manual_DumpRecordingSections` now reports
      SKIPPED when `PARSEK_DUMP_SAVE` is unset (a `DumpSaveFact` attribute sets `Skip`),
      where it used to report a pass with no assertion. With the variable set it still
      runs (checked against `bdock-recorded`).
    - F-catchall-005-01: the seven injectors (`InjectPendingLimboTree` ...
      `InjectAllRecordings`) now report SKIPPED when their target save is absent (an
      `InjectTargetFact` attribute resolves the same KSP root and save / target env vars
      as the bodies), where they used to report seven passes that ran nothing. The
      register's proposal (throw `SkipException`) is wrong for this suite: under xUnit
      2.4.2 a thrown `Xunit.Sdk.SkipException` reports as FAILED (measured with a
      throwaway probe), so the KSP.log lock refusal inside the bodies already fails red
      rather than skipping as the register assumed. Checked: with
      `PARSEK_INJECT_SAVE_NAME` pointing at no save all seven skip; with a staged save
      under a scratch `KSPDIR` the injector runs.
    - F-legacy-bugfix-018-02: the uncalled `MakeActiveProbeTipMergeThenSplit` helper in
      `Bug618ReFlyMergeParentChainTipTests` is deleted.
    - F-recording-tree-013-05: the dead `v1SectionAuthoritative` ternary arm and its stale
      comment are deleted, and the theory is renamed
      `CodecRoundTrip_BoundaryFixture_SectionAuthoritativeBinaryPreservesSemantics` to
      name the one write path its two rows cover.
    - F-trajectory-orbit-005-02: the `OrbitSegment Serialization` region in
      `OrbitSegmentTests` is renamed `FindOrbitSegment boundaries, Recording UT derivation
      and ToString`.
  - Filtered classes after the branch, all 0 failed: the eight B-row classes 409 passed
    (the seven removed cells gone; `SyntheticRecordingTests` without its injectors); the
    T6 classes (`Bug618ReFlyMergeParentChainTipTests`, `RecordingStorageRoundTripTests`,
    `OrbitSegmentTests`, `RecordingSectionDump`, `SyntheticRecordingTests`) 259 passed / 8
    skipped with the injectors pointed at no save.

## July crosswalk

`done/research/test-quality-audit-2026-09-14/july-crosswalk.csv` maps every July register ID (42 rows: A1-A7, B1-B8, C1-C6, D1-D5, and Tier E numbered E1-E16 in source order) to the SUT or file it names and to the D2/D3 rows here that touch the same SUT. `status_now` is judged from the xUnit tree only and says `unknown` for harness and in-game items this audit cannot decide (closed 15, unknown 19, open 7, superseded 1). 49 findings and 14 coverage proposals carry a `july_ref` / `dupe_of_july_id`; for those the July ID stays primary and this audit adds evidence.

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

`docs/dev/done/research/test-quality-audit-2026-09-14/`:

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
| `work/` | committed except the large generated inputs (see `done/research/test-quality-audit-2026-09-14/README.md`): `baseline.trx`, `coverage.cobertura.xml`, `durations.csv`, `metrics.md`, `supervisor-notes.md`, `inventory-completeness.txt`, the Phase 2/3/4 scratch |

### How to regenerate

From the audit worktree root, after editing or re-emitting a fragment under `findings/`:

```bash
python docs/dev/done/research/test-quality-audit-2026-09-14/tools/lint_fragments.py \
  --manifests docs/dev/done/research/test-quality-audit-2026-09-14/work/manifests \
  --fragments docs/dev/done/research/test-quality-audit-2026-09-14/findings \
  --all --report docs/dev/done/research/test-quality-audit-2026-09-14/work/lint-report.txt

python docs/dev/done/research/test-quality-audit-2026-09-14/tools/merge_fragments.py \
  --inventory docs/dev/done/research/test-quality-audit-2026-09-14/test-inventory.csv \
  --july-refs docs/dev/done/research/test-quality-audit-2026-09-14/work/phase4/july-refs.jsonl \
  --fragments docs/dev/done/research/test-quality-audit-2026-09-14/findings \
  --out docs/dev/done/research/test-quality-audit-2026-09-14
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
