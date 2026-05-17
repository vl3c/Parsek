# Clean-context Opus review — fix-refly-abandon-and-fork-persist plan

**Review verdict: APPROVE-WITH-CHANGES**

The plan is largely correct: both root causes are accurately diagnosed and the cited evidence is real. The fix design for Bug 1 is sound. Bug 2's fix design is plausible but has crash-recovery and idempotency concerns that need to be addressed before implementation. Several open questions are resolved below; one test plan item is unbuildable as written.

## 1. Root-cause accuracy

- **Bug 1, primary claim verified.** `RewindInvoker.AtomicMarkerWrite` at `RewindInvoker.cs:1104-1338` does not enumerate `RecordingStore.CommittedRecordings` for prior-session NotCommitted provisionals before adding its own (verified directly). `RecordingStore.RemoveCommittedInternal` at `RecordingStore.cs:728-734` does remove only from the flat list (verified). `LoadTimeSweep.RemoveDiscardRecordings` at `LoadTimeSweep.cs:1133-1159` only calls `RemoveCommittedInternal` and does not touch tree dicts (verified). **The claim that the zombie is re-added by `FinalizeTreeCommit` walking `tree.Recordings.Values` is reasonable but not directly verified** — the plan should grep `FinalizeTreeCommit` in `RecordingStore.cs` to confirm the exact code path, since the trace at line 91072 shows `LedgerOrchestrator: Committed recording` re-committing it, not a direct `CommitTree` adding it. The mechanism named in the plan is plausible (trace 22:17:29 `Merger.MergeTree: id=rec_675a9193…` at line 90312 confirms the zombie is being merged), but the implementor must verify the exact re-add code site before writing the test.
- **Bug 2 cited code is verified.** `MergeJournalOrchestrator.RunMerge` at `:194-298` runs Begin → Split → Supersede → Tombstone → Finalize → Durable1Done → RpReap → MarkerCleared → Durable2Done and never migrates the active tree's fork. `RecordingTreeSplitter.cs:1007-1012` is verbose-only "id preservation kept the active recording pointer valid" — accurate. `RecordingStore.ShouldReplaceCommittedTree` at `:1678-1741` rejects with `incoming-missing-existing-ids` exactly as cited.
- **Minor inaccuracy: trace line 66464 reads `pend.tree=-`**, which means no pending tree at OnSave-pre, contradicting the plan's bullet "right before merge: 14 recs in activeTree, NO pending tree, **3 committed trees**". The 3-committed-trees claim is correct (trace line 66466: `OnSave: saving 3 committed tree(s)`), but the cited line 66464 itself doesn't say "3 committed trees" — that's at 66466. Cite both. Low impact.

## 2. Evidence reconciliation

- Spot-checked 10 trace citations: lines 53293, 53296, 54140, 66464, 66961, 66962, 67843, 68161, 68404, 68503, 68507, 70289, 70291, 80232, 90312, 91072, 91878 — **all verified accurate**.
- persistent.sfs spot-checks at lines 648, 1646, 1678–1681, 3023, 3031, 3046 — **all verified accurate**.
- `rec_12b7252f` appears zero times as a RECORDING node in persistent.sfs (only as `newRecordingId` and `retiringRecordingId`) — **verified**.

## 3. Fix design soundness

- **Bug 1 primary fix reaches all four collections** (flat list, every CommittedTree.Recordings, PendingTree.Recordings, ActiveTreeForSerialization.Recordings) — solid. `ParsekFlight.ActiveTreeForSerialization` at `ParsekFlight.cs:565` is real and accessible from `RewindInvoker`. The `ProvisionalForRpId` field is real at `Recording.cs:266`.
- **The Bug 1 plan misses one collection consideration**: `RecordingStore.committedRecordings` is one place, but `RecordingTreeSplitter` and `MergeJournalOrchestrator` recovery paths also read recordings via `FindCommittedRecordingByIdForCarveOut` and `FindRecordingById`. These resolve through the flat list, so the reap is sufficient — but the implementor should grep all `FindRecording*` helpers to confirm none cache a side-list.
- **Bug 2's `TryUnionActiveReFlyTreeIntoCommitted` has a subtle ordering bug.** The plan inserts the union BEFORE `ApplySessionMergeToRecordings` / `ApplyRewindProvisionalMergeStates` / `PromoteNormalStagingRewindPoints` / `AutoGroupTreeRecordings` / `AdoptOrphanedRecordingsIntoTreeGroup` / `MarkSupersededTerminalSpawnsForContinuedSources` (`RecordingStore.cs:909-916`). When the union is taken, those subsequent calls then run against `tree` (the **incoming** tree, which is partially-merged into existing but is still passed to the helpers). The existing committed tree has been mutated, but the parameter passed downstream is still `tree`. This is a correctness hazard — the plan needs to specify whether `tree` (incoming) or `committedTrees[i]` (existing/merged) is the canonical post-union object, and which one those 6 helpers run against. (Or the union helper should return the merged tree.)
- **Bug 2c (Step 2.12 active-id promotion to TIP, then later promotion to fork)** is sound and the snapshot/rollback hook is correctly noted.

## 4. Crash-recovery story

- **The TreeMerge crash-recovery story has a contradiction.** TreeMerge happens AFTER Begin's DurableSave fires and BEFORE Split's DurableSave. That makes the journal phase on disk = `Begin` during TreeMerge execution, so RunFinisher dispatches to `RollBack(Begin)`. The plan's table conflates the two cases: "Mid-TreeMerge crash" with journal phase = `Begin` rolls back (correct), while "Just after TreeMerge DurableSave" with journal phase = `TreeMerge` drives forward. The plan's table row says "Mid-TreeMerge: Journal phase on disk = `Begin`" — correct — but then asserts the migration is "idempotent if it ran partially" which is irrelevant because the on-disk state is unchanged and RollBack is what runs. **Clarify** that the in-memory migration partial state has no consequence because RollBack on phase=Begin doesn't try to undo the migration (there's nothing on disk to roll back).
- **The proposed `goto case MergeJournal.Phases.Split;`** in C# switch syntax doesn't compose with the existing `if (journal.Phase == ...)` cascade structure in `CompleteFromPostDurable` (`MergeJournalOrchestrator.cs:457-593`). The existing code is **not a switch** — it's a sequence of `if` blocks each advancing `journal.Phase`. Rewrite the snippet to add a new `if (journal.Phase == MergeJournal.Phases.TreeMerge)` block that runs `MigrateActiveReFlyForkIntoCommittedTree` then `AdvancePhase(Split)` and falls through (matching the existing pattern). **Critical correction — the proposed switch syntax would not compile.**
- **`MergeJournal.IsKnownPostBeginPhase`** at `MergeJournal.cs:131-142` and `IsPostDurablePhase` at `:106-117` must both learn `TreeMerge`. Plan only mentions adding the constant but doesn't call out these predicates. Add both.
- **`RunFinisher`'s dispatch** at `MergeJournalOrchestrator.cs:342-379` branches on `phase == Phases.Begin` → RollBack, `IsKnownPostBeginPhase` → CompleteFromPostDurable, else → RollBack. TreeMerge needs to be in `IsKnownPostBeginPhase` to drive forward.

## 5. Logging compliance

- All proposed log strings use the right `[Parsek][LEVEL][Subsystem] msg` format via `ParsekLog.Info` / `Warn` / `Verbose` — compliant.
- Log levels look right: `Info` for state transitions ("reaped N prior provisionals"), `Warn` for invariant violations (NotCommitted in closure walk), `Verbose` only for fine-grained diagnostic detail.
- **Existing `LoadTimeSweep` log site at `LoadTimeSweep.cs:1144-1148`** is being changed by the plan from "Zombie discarded rec=… sess=… supersedeTarget=…" to the new 4-flag form. The 4-flag log is richer, but the planner should verify no log-contract test in `LogContractTests` is asserting the existing 3-field shape, and update that test if so. Grep `Zombie discarded` in the tests dir before changing the format.
- **No proposed change loses an existing log site** based on my read.

## 6. Test plan soundness

- **`S11EvidenceBundle` reading persistent.sfs via `ConfigNode.Load` cleanly** — `ConfigNode.Load` returns a node with the file's contents (per CLAUDE.md gotcha). The s11 `persistent.sfs` is a valid KSP save and should load. But xUnit tests run in net472 without Unity's `HighLogic.CurrentGame` — `ConfigNode` itself is in Assembly-CSharp, accessible from tests (other tests use `ConfigNode.Load` per `LedgerTests.cs:1081`). **The fixture is buildable**, but it cannot drive `ParsekScenario.OnLoad` to deserialize — per `reference_parsek_scenario_xunit.md`, that path touches Planetarium + Unity GameEvents and crashes outside the runtime. The plan's `S11Bug2Regression_ForkPersistTests` "run the merge journal orchestrator, write a fresh persistent.sfs to memory, parse it back" is **not buildable as written** — `DurableSave` calls `GamePersistence.SaveGame` which requires `HighLogic.CurrentGame`. The test must use `DurableSaveForTesting` and `SaveGameForTesting` hooks (per `MergeJournalOrchestrator.cs:629`) to intercept the save call. Update the plan to use those hooks explicitly.
- **`AppendRelations_RefusesNotCommittedRowAtWriteSite_LogsWarn`** would also need to call internal `BuildCommittedRecordingIndex` or manually seed `committedRecordings` — buildable, but tedious. Plan should note that this test requires `[Collection("Sequential")]`.
- **`CommitTreeUnionsActiveReFlyTreeTests` requires `ParsekScenario.Instance` to expose a live `ActiveReFlySessionMarker`**. The plan's `TryUnionActiveReFlyTreeIntoCommitted` reads `ParsekScenario.Instance?.ActiveReFlySessionMarker`. Tests need a way to install a scenario instance — confirm via existing tests like `RewindLoggingTests.cs` what fixture pattern works.

## 7. Order of operations

- Test files for Bug 1's `ReapPriorProvisionalsForRp` cannot be written before Phase 2 because the helper method itself is implemented in Phase 2 (step 2). The plan's Phase 1 lists `RewindAbandonRetryProvisionalReapTests.cs` calling the helper — that test is unbuildable before Phase 2 ships the helper. **The dependency ordering is broken.** Either:
  - rewrite Phase 1 as "test files that fail to compile until Phase 2 lands" (compiler-failure-as-failing-test); or
  - move test infrastructure (the `S11EvidenceBundle` and skeleton assertions) into Phase 1 and the helper-call sites into Phase 2.
- The Phase 1 / Phase 2 split as written is more aspirational than practical. Simpler fix: collapse to a single iteration order where each test is committed together with its helper-under-test.

## 8. Scope creep / out-of-scope honesty

- **The orphan-adopter carve-out is honest** — the plan correctly notes that `ReapPriorProvisionalsForRp` is keyed on `provisionalForRpId == rpId`, so an orphan-adopter that crosses trees with a different RP id is not subsumed. Open question 7 (whether the fix subsumes `rec_3cdedee5` too) is resolved correct: the evidence summary confirms identical shape for `rec_3cdedee5` (same `provisionalForRpId = rp_0d8cc5…`), so the single fix covers both.
- **PR #872's split logic carve-out is honest** — splitter is unchanged except for the active-id promotion in Step 2.12.

## 9. Resolution of the 7 open questions

1. **`provisionalForRpId` field name** — **resolved (correct)**: `Recording.cs:266` declares `public string ProvisionalForRpId;` (PascalCase, public). Plan can use `ProvisionalForRpId` directly.
2. **`ParsekFlight.Instance?.ActiveTreeForSerialization`** — **resolved (correct)**: real internal property at `ParsekFlight.cs:565`. Returns `activeTree` directly.
3. **`CommitTreeSceneExit (autoMerge off)` path** — **resolved**: trace line 66961 confirms `CommitTreeSceneExit` runs the offending `CommitTree` call. The live marker reference check in the union helper is correct, but verify by grepping `CommitTree(` in the codebase to enumerate all callers.
4. **`MigrateActiveReFlyForkIntoCommittedTree` for the non-in-place case** — **still open**: the plan correctly flags this. The non-in-place path's fork attachment uses `EnsureForkAttachedToTree` only when `pendingTreeForFork != null`, and the placeholder branch doesn't run `EnsureForkAttachedToTree` at all. Verify by reading lines 1230-1258 of `RewindInvoker.cs`: the eager tree-attach is only inside the `if (inPlaceContinuation)` block. Non-in-place forks may not have a tree home at marker-write time. The migrate helper needs a non-in-place branch or a clear "fork must already be in some tree" precondition.
5. **Relaxing `ShouldReplaceCommittedTree`'s `incoming-not-richer` gate** — **resolved (correct)**: the union helper bypasses both gates atomically when the live marker matches, so both rejection reasons are handled uniformly. Plan is right.
6. **`activeRecordingId` semantics for chained in-place forks** — **still open**: the s11 evidence has no nested-Re-Fly case, so this is genuinely speculative. The plan correctly identifies it as untested. Defer to a follow-up unless the implementor encounters it.
7. **The "Bug 1" name covers both `rec_675a9193` and `rec_3cdedee5`** — **resolved (correct)**: both are NotCommitted, both have `provisionalForRpId` set, both have invalid supersede rows. Single fix covers both.

## 10. Anything missed

- **Existing `RemoveCommittedById` already exists at `RecordingStore.cs:655`** — the plan claims "already exists, reuse" in step 1 (Phase 2). Verified. Good catch.
- **Missing defense: re-running `ReapPriorProvisionalsForRp` from `LoadTimeSweep`.** The plan's Bug 1 crash-recovery argument relies on `LoadTimeSweep` reaping orphans on the next OnLoad. But `LoadTimeSweep` reaps NotCommitted recordings whose `creatingSessionId` no longer has a live marker — that's already covered by existing `LoadTimeSweep` logic; the plan's extension only fixes the structural tree-dict leak. Confirm the existing `LoadTimeSweep` already catches mid-AtomicMarkerWrite crash orphans, or add a defense.
- **No `ReapPriorProvisionalsForRp` for the `RP-Reap` reaper path.** `RewindPointReaper.ReapOrphanedRPs()` at `MergeJournalOrchestrator.cs:269` reaps RPs but doesn't touch their provisional recordings. After Bug 1's fix, an RP being reaped from inside a merge could leave NotCommitted provisionals tagged to that RP id behind. Consider adding a complementary reap inside the RP reaper path, or document that this isn't a concern because by-merge-time the provisional has already flipped to non-NotCommitted.
- **Missing test: idempotent re-run of TreeMerge.** The plan doesn't include a test for "re-run `MigrateActiveReFlyForkIntoCommittedTree` twice → second run is a no-op". Crash-recovery soundness depends on this.
- **The `Begin → TreeMerge → Split` reordering needs to update `docs/parsek-rewind-to-separation-design.md` AND `MergeJournal.IsPostDurablePhase` / `IsKnownPostBeginPhase`** — only the design doc is mentioned.

## Prioritized must-fix list

**H (block implementation):**
1. **Rewrite the `case MergeJournal.Phases.TreeMerge: goto case ...` snippet** as an `if (journal.Phase == ...)` block matching the existing cascade in `CompleteFromPostDurable` (`MergeJournalOrchestrator.cs:457-593`). The proposed switch syntax does not compile.
2. **Update `MergeJournal.IsPostDurablePhase` (`MergeJournal.cs:106-117`) and `IsKnownPostBeginPhase` (`:131-142`)** to include `TreeMerge`. Plan only mentions adding the constant.
3. **Specify the post-union "which tree object is canonical"** for the 6 helper calls (`ApplySessionMergeToRecordings` through `MarkSupersededTerminalSpawnsForContinuedSources`) at `RecordingStore.cs:909-915`. The union mutates `committedTrees[i]`, but the helpers all take `tree` (incoming). Decide and document.
4. **Reorder Phase 1 / Phase 2** so each test ships in the same commit as the helper it exercises. Phase 1 as written has tests that cannot compile without Phase 2's helpers.
5. **Specify `DurableSaveForTesting` / `SaveGameForTesting` hooks** for the `S11Bug2Regression_ForkPersistTests` end-to-end test. Without them the test cannot run outside Unity.

**M (clarify before/during implementation):**

6. **Verify `FinalizeTreeCommit` re-add mechanism** for the zombie before writing the regression test. Trace line 91072 (`LedgerOrchestrator: Committed recording`) is the symptom — find the exact call site that re-adds the zombie to `committedRecordings`.
7. **Clarify the non-in-place fork case for `MigrateActiveReFlyForkIntoCommittedTree`** (open question 4). If the fork has no tree home at marker-write time, the helper needs a precondition or branch.
8. **Add an idempotency test** for `MigrateActiveReFlyForkIntoCommittedTree` (re-run produces no change).
9. **Update the `LoadTimeSweep` log format change** with awareness of any `LogContractTests` asserting the existing "Zombie discarded rec=… sess=… supersedeTarget=…" shape.
10. **Replace the plan's reference to "5 durable journal barriers"** with the actual 10-phase vocabulary (`Begin / TreeMerge (new) / Split / Supersede / Tombstone / Finalize / Durable1Done / RpReap / MarkerCleared / Durable2Done`) since `MergeJournal.Phases` at `MergeJournal.cs:66-78` has 10 names.

**L (nits):**

11. Cite trace line **66466** (`OnSave: saving 3 committed tree(s)`) instead of/alongside 66464 for the "3 committed trees" claim.
12. The Bug 1 fix design says "Bug 1's tertiary defense ... is defense-in-depth only" — fine, but explicitly mark the row-write guard as `#if DEBUG throw, else Warn-and-skip` for symmetry with the existing `ValidateSupersedeTarget` pattern at `SupersedeCommit.cs:225-236`.
13. The plan's barrier-count claim "from 5 to 6" is wrong if you count durable saves — currently `begin / split / durable1 / durable2 / final` = 5; new `treemerge` makes 6. But the **phase** vocabulary has 10 entries. Be consistent in terminology.

## What to keep

The diagnosis is excellent and matches reality precisely. The three-layer Bug 1 fix (primary reap, secondary closure-walk guard, tertiary row-write guard) is well-reasoned and the layers are correctly described as "primary is load-bearing, others defense-in-depth." The Bug 2 split into 2a/2b/2c is the right decomposition. The frozen-evidence approach with `S11EvidenceBundle` is the right test pattern.
