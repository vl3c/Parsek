using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// F9 into the quicksave of a flight that was committed after the quicksave, when an OnSave
    /// ran in between (todo QUICKLOAD-INTO-COMMITTED-FLIGHT-AFTER-A-SAVE-KEEPS-ABANDONED-FUTURE).
    /// The OnSave advanced the sidecar epochs past the ones the quicksave names, so every member
    /// that changed since the quicksave reads `stale-sidecar-epoch` on the load. Owner ruling
    /// OQ-2: the F9 resumes the flight and retires its recording-tagged future; D2: a tree the
    /// quicksave already holds as committed history keeps today's behaviour.
    ///
    /// Shapes, all driven through <c>ParsekScenario.TryRestoreActiveTreeNode</c> and the
    /// production resume entry <c>FlightRecorder.PrepareQuickloadResumeStateIfNeeded</c>:
    /// the Space Center route (no pending tree: the exit committed the flight), the in-flight
    /// route (the same-id copy-on-write clone the recorder resumed after an in-flight commit,
    /// stashed as Limbo by the F9), the D2 shape, and the plain stale-epoch keep with no
    /// committed copy.
    /// </summary>
    [Collection("Sequential")]
    public class QuickloadCommittedAfterQuicksaveTests : IDisposable
    {
        private const double QuicksaveUT = 200.0;
        private const uint RootPid = 111u;
        private const uint BoosterPid = 222u;

        private readonly List<string> logLines = new List<string>();
        private readonly string saveRoot;

        public QuickloadCommittedAfterQuicksaveTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResetAll();
            RecordingStore.SuppressLogging = false;
            GameStateStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;

            saveRoot = Path.Combine(Path.GetTempPath(), "parsek-ql-after-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(saveRoot);
            RecordingPaths.SaveRootOverrideForTesting = saveRoot;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            try { Directory.Delete(saveRoot, true); } catch { }
            ResetAll();
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.ClearPendingQuickloadResumeContext();
            ParsekScenario.ClearRestoredQuicksaveTreeFactsForTesting();
            ParsekScenario.pendingActiveTreeResumeRewindSave = null;
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            RewindContext.ResetForTesting();
            RevertDetector.ResetForTesting();
            FlightRecorder.QuickloadResumeUTProviderForTesting = null;
        }

        // ============================================================
        // Space Center route: F5, Esc to the Space Center (auto-merge, exit save), F9.
        // ============================================================

        [Fact]
        public void SpaceCenterRoute_StaleMembersSalvagedFromCommittedCopy_ResumedAndFutureRetired()
        {
            var committed = CommitFuture("ql_sc");
            string rootId = committed.RootRecordingId;
            string boosterId = BoosterId(committed);
            SeedFutureEventsAndRows(rootId, boosterId);
            ConfigNode node = QuicksaveNode(MakeQuicksaveTree("ql_sc"), committedTree: null);

            logLines.Clear();
            bool restored = ParsekScenario.TryRestoreActiveTreeNode(node);

            // Today: the stale root drops the whole saved tree and the flight resumes unrecorded.
            AssertNoLine("dropped entire tree");
            Assert.True(restored);
            Assert.Contains(logLines, l => l.Contains("Sidecar epoch mismatch"));
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == committed.Id);
            Assert.DoesNotContain(RecordingStore.CommittedRecordings, r => r.TreeId == committed.Id);
            Assert.Contains(logLines, l =>
                l.Contains("TryRestoreActiveTreeNode: removed committed tree 'ql_sc'"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("Quickload committed-copy restore:")
                && l.Contains("tree='ql_sc'")
                && l.Contains("route=space-center")
                && l.Contains("action=ResumeFromQuicksave")
                && l.Contains("reason=committed-after-quicksave")
                && l.Contains("staleMembers=2")
                && l.Contains("salvagedFromCommitted=2")
                && l.Contains("unsalvaged=0"));
            // The wording lane QL-4b pins (harness/scenarios/QL-4b-quickload-from-space-center.toml).
            Assert.Contains(logLines, l => Regex.IsMatch(l,
                @"\[Scenario\] Quickload committed-copy restore: tree='ql_sc' id=\S+ route=space-center "
                + @"action=ResumeFromQuicksave reason=committed-after-quicksave loadKind=InSession "
                + @"staleMembers=[1-9][0-9]* salvagedFromCommitted=[1-9][0-9]* unsalvaged=[0-9]+ replacesStaleKeep=false"));
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            RecordingTree resumed = RecordingStore.PopPendingTree();
            Assert.NotSame(committed, resumed);
            Recording booster = resumed.Recordings[boosterId];
            Assert.False(booster.SidecarLoadFailed);
            Assert.True(booster.FilesDirty);

            logLines.Clear();
            RunResumePrep(resumed, QuicksaveUT, LoadKind.QuickloadFlight);

            AssertAbandonedFutureRetired(resumed, rootId, boosterId, expectedEventsPurged: 2);
        }

        // ============================================================
        // In-flight route: F5, fly on, commit in flight (the recorder resumes on a same-id
        // copy-on-write clone), any OnSave, F9 (the scene change stashes the clone as Limbo).
        // ============================================================

        [Fact]
        public void InFlightRoute_SameIdLimboCloneNotKept_CommittedCopyDetachedAndFutureRetired()
        {
            var committed = CommitFuture("ql_if");
            string rootId = committed.RootRecordingId;
            string boosterId = BoosterId(committed);
            SeedFutureEventsAndRows(rootId, boosterId);
            RecordingTree clone = StashResumedClone(committed);
            AddEvent(rootId, 335.0, GameStateEventType.ContractCompleted, "clone-335", "fundsReward=1");
            ConfigNode node = QuicksaveNode(MakeQuicksaveTree("ql_if"), committedTree: null);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));
            RecordingTree resumed = RecordingStore.PopPendingTree();
            RunResumePrep(resumed, QuicksaveUT, LoadKind.QuickloadFlight);

            // Today: the stale-epoch keep holds the clone, nothing is detached, and every
            // member reads as still committed.
            AssertNoLine("reason=empty-plan");
            AssertNoLine("keeping in-memory pending tree");
            Assert.NotSame(clone, resumed);
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == committed.Id);
            Assert.DoesNotContain(RecordingStore.CommittedRecordings, r => r.TreeId == committed.Id);
            Assert.False(RecordingStore.HasCommittedTreeRestoreAttempt);
            Assert.Contains(logLines, l =>
                l.Contains("Quickload committed-copy restore:")
                && l.Contains("tree='ql_if'")
                && l.Contains("route=in-flight")
                && l.Contains("action=ResumeFromQuicksave")
                && l.Contains("staleMembers=2")
                && l.Contains("salvagedFromCommitted=2"));
            Assert.Contains(logLines, l =>
                l.Contains("Quickload committed-copy restore: cleared the committed-tree restore attempt")
                && l.Contains("tree='ql_if'"));
            // The wording lane QL-4c pins (harness/scenarios/QL-4c-quickload-after-in-flight-save.toml).
            Assert.Contains(logLines, l => Regex.IsMatch(l,
                @"\[Scenario\] Quickload committed-copy restore: tree='ql_if' id=\S+ route=in-flight "
                + @"action=ResumeFromQuicksave reason=committed-after-quicksave loadKind=InSession "
                + @"staleMembers=[1-9][0-9]* salvagedFromCommitted=[1-9][0-9]* unsalvaged=[0-9]+ replacesStaleKeep=true"));
            Assert.Contains(logLines, l => Regex.IsMatch(l,
                @"\[Scenario\] Quickload committed-copy restore: cleared the committed-tree restore attempt for tree='ql_if'"));
            // The resumed tree is the quicksave's, not the clone's post-commit tail.
            Assert.Equal(QuicksaveUT, resumed.Recordings[rootId].EndUT);

            AssertAbandonedFutureRetired(resumed, rootId, boosterId, expectedEventsPurged: 3);
        }

        [Fact]
        public void InFlightRoute_StaleMemberTheCommittedCopyNoLongerHolds_DroppedAndCloneStillNotKept()
        {
            // The quicksave names a member the committed copy no longer holds (merged away by
            // the commit's optimizer pass) whose sidecar was rewritten after the quicksave: it
            // stays stale after the salvage, and the same-id clone must still not be kept.
            var committed = CommitFuture("ql_unsalv");
            RecordingTree clone = StashResumedClone(committed);
            var quicksaved = MakeQuicksaveTree("ql_unsalv");
            var merged = new Recording
            {
                RecordingId = "merged_ql_unsalv", TreeId = quicksaved.Id, VesselName = "merged",
                VesselPersistentId = 333u,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                SidecarEpoch = 1,
            };
            SetPoints(merged, 130.0, 190.0);
            quicksaved.AddOrReplaceRecording(merged);
            var mergedOnDisk = new RecordingTree { Id = quicksaved.Id, TreeName = quicksaved.Id };
            mergedOnDisk.AddOrReplaceRecording(Recording.DeepClone(merged));
            WriteSidecarsAtEpoch(mergedOnDisk, 2);
            ConfigNode node = QuicksaveNode(quicksaved, committedTree: null);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));

            AssertNoLine("keeping in-memory pending tree");
            Assert.NotSame(clone, RecordingStore.PendingTree);
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == committed.Id);
            Assert.False(RecordingStore.PendingTree.Recordings.ContainsKey("merged_ql_unsalv"));
            Assert.Contains(logLines, l =>
                l.Contains("Quickload committed-copy restore:")
                && l.Contains("route=in-flight")
                && l.Contains("action=ResumeFromQuicksave")
                && l.Contains("staleMembers=3")
                && l.Contains("salvagedFromCommitted=2")
                && l.Contains("unsalvaged=1")
                && l.Contains("replacesStaleKeep=true"));
            Assert.Contains(logLines, l =>
                l.Contains("TryRestoreActiveTreeNode: dropped 1 recording(s) with incompatible sidecars"));
        }

        // ============================================================
        // D2: the quicksave already holds the tree as committed history (a copy-on-write
        // clone of a tree committed BEFORE the quicksave): today's behaviour, logged.
        // ============================================================

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void CommittedBeforeTheQuicksave_LaterSaveThenF9_KeepsTodaysBehaviour(bool withPendingClone)
        {
            var committed = CommitFuture("ql_d2");
            RecordingTree clone = withPendingClone ? StashResumedClone(committed) : null;
            // The quicksave was taken while the clone flew: OnSave wrote the committed tree
            // first, then the clone as the active tree.
            var quicksavedActive = MakeQuicksaveTree("ql_d2");
            var quicksavedCommitted = MakeQuicksaveTree("ql_d2");
            ConfigNode node = QuicksaveNode(quicksavedActive, quicksavedCommitted);

            logLines.Clear();
            bool restored = ParsekScenario.TryRestoreActiveTreeNode(node);

            Assert.Contains(RecordingStore.CommittedTrees, t => ReferenceEquals(t, committed));
            Assert.Contains(RecordingStore.CommittedRecordings, r => r.TreeId == committed.Id);
            Assert.DoesNotContain(logLines, l => l.Contains("removed committed tree 'ql_d2'"));
            if (withPendingClone)
            {
                Assert.True(restored);
                Assert.Contains(logLines, l => l.Contains("keeping in-memory pending tree"));
                Assert.Same(clone, RecordingStore.PendingTree);
                Assert.True(RecordingStore.HasCommittedTreeRestoreAttempt);
            }
            else
            {
                Assert.False(restored);
                Assert.Contains(logLines, l => l.Contains("dropped entire tree 'ql_d2'"));
            }
            Assert.Contains(logLines, l =>
                l.Contains("Quickload committed-copy restore:")
                && l.Contains("tree='ql_d2'")
                && l.Contains("route=" + (withPendingClone ? "in-flight" : "space-center"))
                && l.Contains("action=Unchanged")
                && l.Contains("reason=tree-committed-in-quicksave")
                && l.Contains("staleMembers=2")
                && l.Contains("salvagedFromCommitted=0"));
        }

        // ============================================================
        // The plain stale-epoch keep (F5, fly on, autosave, F9; nothing committed): unchanged.
        // ============================================================

        [Fact]
        public void NoCommittedCopy_StaleEpochKeepOfThePendingTree_Unchanged()
        {
            var future = MakeFutureTree("ql_plain");
            WriteSidecarsAtEpoch(future, 2);
            RecordingStore.StashPendingTree(future, PendingTreeState.Limbo);
            ConfigNode node = QuicksaveNode(MakeQuicksaveTree("ql_plain"), committedTree: null);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));

            Assert.Same(future, RecordingStore.PendingTree);
            Assert.Contains(logLines, l => l.Contains("keeping in-memory pending tree"));
            Assert.DoesNotContain(logLines, l => l.Contains("removed committed tree"));
            Assert.DoesNotContain(logLines, l => l.Contains("Quickload committed-copy restore:"));
            Assert.Contains(logLines, l =>
                l.Contains("Quickload committed-copy restore skipped:")
                && l.Contains("tree='ql_plain'")
                && l.Contains("reason=no-committed-copy")
                && l.Contains("staleMembers=2"));
        }

        // ============================================================
        // In flight with no save between the commit and the F9 (QL-4's route): nothing reads
        // stale, the existing refresh and detach already ran; the rule adds the restore-attempt
        // clear so the resumed tree is written by the next OnSave.
        // ============================================================

        [Fact]
        public void InFlightNoSaveInBetween_NothingStale_DetachAsBeforeAndRestoreAttemptCleared()
        {
            var committed = MakeFutureTree("ql_nosave");
            foreach (var rec in committed.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(committed);
            StashResumedClone(committed);
            ConfigNode node = QuicksaveNode(MakeQuicksaveTree("ql_nosave"), committedTree: null);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));

            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == committed.Id);
            Assert.False(RecordingStore.HasCommittedTreeRestoreAttempt);
            Assert.Contains(logLines, l =>
                l.Contains("Quickload committed-copy restore:")
                && l.Contains("route=in-flight")
                && l.Contains("action=ResumeFromQuicksave")
                && l.Contains("staleMembers=0")
                && l.Contains("salvagedFromCommitted=0")
                && l.Contains("replacesStaleKeep=false"));
            Assert.Contains(logLines, l =>
                l.Contains("cleared the committed-tree restore attempt") && l.Contains("tree='ql_nosave'"));
            RecordingTree resumed = RecordingStore.PopPendingTree();
            // The refresh brought the committed future in; the resume trim owns it.
            Assert.Equal(TerminalState.Destroyed, resumed.Recordings[BoosterId(resumed)].TerminalStateValue);
        }

        // ============================================================
        // Exclusions through the real entry: a revert and a Re-Fly start keep today's restore.
        // ============================================================

        [Fact]
        public void RevertPending_SpaceCenterShape_Unchanged()
        {
            var committed = CommitFuture("ql_revert");
            ConfigNode node = QuicksaveNode(MakeQuicksaveTree("ql_revert"), committedTree: null);
            RevertDetector.SetPendingForTesting(RevertKind.Launch);

            logLines.Clear();
            Assert.False(ParsekScenario.TryRestoreActiveTreeNode(node));

            Assert.Contains(RecordingStore.CommittedTrees, t => ReferenceEquals(t, committed));
            Assert.Contains(logLines, l => l.Contains("dropped entire tree 'ql_revert'"));
            Assert.Contains(logLines, l =>
                l.Contains("Quickload committed-copy restore:")
                && l.Contains("action=Unchanged")
                && l.Contains("reason=revert-pending"));
        }

        [Fact]
        public void LoadLandingOutsideFlight_SpaceCenterShape_Unchanged()
        {
            // A flight quicksave loaded straight to the Space Center (the automation-only
            // `LoadGame scene=spacecenter`): no recorder resumes, so nothing would trim the
            // salvaged payload before the outside-flight auto-commit re-committed it.
            var committed = CommitFuture("ql_ksc_scene");
            ConfigNode node = QuicksaveNode(MakeQuicksaveTree("ql_ksc_scene"), committedTree: null);

            logLines.Clear();
            Assert.False(ParsekScenario.TryRestoreActiveTreeNode(
                node, EarlyLoadKind.InSession, loadedSceneIsFlight: false));

            Assert.Contains(RecordingStore.CommittedTrees, t => ReferenceEquals(t, committed));
            Assert.Contains(logLines, l => l.Contains("dropped entire tree 'ql_ksc_scene'"));
            Assert.Contains(logLines, l =>
                l.Contains("Quickload committed-copy restore:")
                && l.Contains("action=Unchanged")
                && l.Contains("reason=scene-not-flight"));
        }

        [Fact]
        public void FinalizedSameIdPendingTree_RestoreAttemptNotClearedBeforeTheFinalizedKeep()
        {
            // A same-id pending tree already Finalized (bug #290d: the restore keeps it as
            // authoritative) is not the quicksave's flight resuming: the rule stays out, so the
            // restore attempt armed for its committed original is not cleared.
            var committed = MakeFutureTree("ql_finalized");
            foreach (var rec in committed.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(committed);
            RecordingStore.ArmCommittedTreeRestoreAttempt(committed, "test copy-on-write resume");
            var finalizedClone = RecordingTree.DeepClone(committed);
            RecordingStore.StashPendingTree(finalizedClone, PendingTreeState.Finalized);
            ConfigNode node = QuicksaveNode(MakeQuicksaveTree("ql_finalized"), committedTree: null);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));

            Assert.Same(finalizedClone, RecordingStore.PendingTree);
            Assert.Contains(logLines, l => l.Contains("keeping in-memory Finalized tree"));
            Assert.True(RecordingStore.HasCommittedTreeRestoreAttempt);
            AssertNoLine("cleared the committed-tree restore attempt");
            Assert.Contains(logLines, l =>
                l.Contains("Quickload committed-copy restore:")
                && l.Contains("tree='ql_finalized'")
                && l.Contains("action=Unchanged")
                && l.Contains("reason=finalized-pending-tree"));
        }

        [Fact]
        public void ReFlyStartLoad_SpaceCenterShape_Unchanged()
        {
            var committed = CommitFuture("ql_refly");
            ConfigNode node = QuicksaveNode(MakeQuicksaveTree("ql_refly"), committedTree: null);

            logLines.Clear();
            Assert.False(ParsekScenario.TryRestoreActiveTreeNode(node, EarlyLoadKind.ReFlyStart));

            Assert.Contains(RecordingStore.CommittedTrees, t => ReferenceEquals(t, committed));
            Assert.Contains(logLines, l =>
                l.Contains("Quickload committed-copy restore:")
                && l.Contains("action=Unchanged")
                && l.Contains("reason=load-kind-ReFlyStart"));
        }

        // ============================================================
        // The merge state the abandoned commit gave a member (todo
        // QUICKLOAD-RESUMED-MEMBER-KEEPS-ABANDONED-FUTURE-MERGE-STATE): the commit after the
        // quicksave promoted the booster to CommittedProvisional (an Unfinished Flight from its
        // abandoned end), the same-id refresh or the stale-epoch salvage copies that onto the
        // quicksave's booster (which the quicksave holds with no `mergeState` key, so Immutable),
        // and the reconcile must hand back the quicksave's state with the end state it clears.
        // ============================================================

        [Theory]
        [InlineData("nosave")]
        [InlineData("spacecenter")]
        [InlineData("inflight-save")]
        public void PromotedAfterTheQuicksave_ResumedMemberTakesBackTheQuicksaveMergeState(string route)
        {
            string id = "ql_ms_" + route.Replace("-", "_");
            RecordingTree committed;
            if (route == "nosave")
            {
                // QL-4: commit in flight, no save, F9 (nothing reads stale; the same-id refresh).
                committed = MakeFutureTree(id);
                committed.Recordings[BoosterId(committed)].MergeState = MergeState.CommittedProvisional;
                foreach (var rec in committed.Recordings.Values)
                    RecordingStore.AddCommittedInternal(rec);
                RecordingStore.AddCommittedTreeForTesting(committed);
                StashResumedClone(committed);
            }
            else
            {
                // QL-4b (exit to the Space Center, F9) and QL-4c (commit in flight, a save, F9):
                // the stale-epoch salvage.
                committed = CommitFuture(id, promoteBooster: true);
                if (route == "inflight-save")
                    StashResumedClone(committed);
            }
            string boosterId = BoosterId(committed);
            RecordingTree quicksaved = MakeQuicksaveTree(id);
            Assert.Equal(MergeState.Immutable, quicksaved.Recordings[boosterId].MergeState);
            ConfigNode node = QuicksaveNode(quicksaved, committedTree: null);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));
            RecordingTree resumed = RecordingStore.PopPendingTree();
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == committed.Id);
            // The leak's first half: the restore hands the booster the abandoned commit's state.
            Assert.Equal(MergeState.CommittedProvisional, resumed.Recordings[boosterId].MergeState);

            RunResumePrep(resumed, QuicksaveUT, LoadKind.QuickloadFlight);

            Recording booster = resumed.Recordings[boosterId];
            Assert.Null(booster.TerminalStateValue);
            Assert.Equal(MergeState.Immutable, booster.MergeState);
            Assert.True(booster.FilesDirty);
            Assert.Equal(MergeState.Immutable, resumed.Recordings[resumed.RootRecordingId].MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("Quickload abandoned-future merge state reset: rec=" + boosterId
                    + " previousMergeState=CommittedProvisional mergeState=Immutable reason=abandoned-future-merge-state"));
            // The new token is the line's LAST field, so every lane regex over the line still matches.
            Assert.Contains(logLines, l => Regex.IsMatch(l,
                @"\[Scenario\] Quickload abandoned-future reconcile: tree='" + id + @"' "
                + @"(?=.*\bkind=QuickloadFlight\b)(?=.*\bscope=TreeWide\b)(?=.*\bendStatesCleared=[1-9][0-9]*\b)"
                + @"(?=.*\bskippedCommitted=0\b).* quicksaveFacts=present mergeStatesReset=1$"));
        }

        [Fact]
        public void CommittedBeforeTheQuicksave_PromotedMemberKeepsItsCommittedMergeState()
        {
            // D2: the quicksave holds the tree as committed history, the restore keeps the
            // copy-on-write clone, and the reconcile reads every member as still committed.
            var committed = CommitFuture("ql_ms_d2", promoteBooster: true);
            RecordingTree clone = StashResumedClone(committed);
            string boosterId = BoosterId(committed);
            var quicksavedCommitted = MakeQuicksaveTree("ql_ms_d2");
            quicksavedCommitted.Recordings[boosterId].MergeState = MergeState.CommittedProvisional;
            var quicksavedActive = MakeQuicksaveTree("ql_ms_d2");
            quicksavedActive.Recordings[boosterId].MergeState = MergeState.CommittedProvisional;
            ConfigNode node = QuicksaveNode(quicksavedActive, quicksavedCommitted);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));
            Assert.Same(clone, RecordingStore.PendingTree);
            RecordingTree resumed = RecordingStore.PopPendingTree();
            RunResumePrep(resumed, QuicksaveUT, LoadKind.QuickloadFlight);

            Assert.Equal(MergeState.CommittedProvisional, resumed.Recordings[boosterId].MergeState);
            Assert.Equal(MergeState.CommittedProvisional, committed.Recordings[boosterId].MergeState);
            AssertNoLine("Quickload abandoned-future merge state reset:");
        }

        // ============================================================
        // Pure pieces
        // ============================================================

        [Fact]
        public void DecideCommittedCopyRestore_Table()
        {
            var notCommitted = new ParsekScenario.QuicksaveTreeFacts { TreeId = "t" };
            notCommitted.Members["a"] = new ParsekScenario.QuicksaveMemberFacts();
            var treeCommitted = new ParsekScenario.QuicksaveTreeFacts { TreeId = "t", TreeCommittedInQuicksave = true };
            var memberCommitted = new ParsekScenario.QuicksaveTreeFacts { TreeId = "t" };
            memberCommitted.Members["a"] = new ParsekScenario.QuicksaveMemberFacts { CommittedInQuicksave = true };

            string reason;
            Assert.Equal(ParsekScenario.CommittedCopyRestoreAction.None,
                ParsekScenario.DecideCommittedCopyRestore(false, EarlyLoadKind.InSession, true, false, false, notCommitted, out reason));
            Assert.Equal("no-committed-copy", reason);
            Assert.Equal(ParsekScenario.CommittedCopyRestoreAction.ResumeFromQuicksave,
                ParsekScenario.DecideCommittedCopyRestore(true, EarlyLoadKind.InSession, true, false, false, notCommitted, out reason));
            Assert.Equal("committed-after-quicksave", reason);
            Assert.Equal(ParsekScenario.CommittedCopyRestoreAction.Unchanged,
                ParsekScenario.DecideCommittedCopyRestore(true, EarlyLoadKind.InSession, true, false, false, treeCommitted, out reason));
            Assert.Equal("tree-committed-in-quicksave", reason);
            Assert.Equal(ParsekScenario.CommittedCopyRestoreAction.Unchanged,
                ParsekScenario.DecideCommittedCopyRestore(true, EarlyLoadKind.InSession, true, false, false, memberCommitted, out reason));
            Assert.Equal("member-committed-in-quicksave", reason);
            Assert.Equal(ParsekScenario.CommittedCopyRestoreAction.Unchanged,
                ParsekScenario.DecideCommittedCopyRestore(true, EarlyLoadKind.InSession, true, false, false, null, out reason));
            Assert.Equal("no-quicksave-facts", reason);
            Assert.Equal(ParsekScenario.CommittedCopyRestoreAction.Unchanged,
                ParsekScenario.DecideCommittedCopyRestore(true, EarlyLoadKind.InSession, true, false, true, notCommitted, out reason));
            Assert.Equal("revert-pending", reason);
            Assert.Equal(ParsekScenario.CommittedCopyRestoreAction.Unchanged,
                ParsekScenario.DecideCommittedCopyRestore(true, EarlyLoadKind.InSession, false, false, false, notCommitted, out reason));
            Assert.Equal("scene-not-flight", reason);
            Assert.Equal(ParsekScenario.CommittedCopyRestoreAction.Unchanged,
                ParsekScenario.DecideCommittedCopyRestore(true, EarlyLoadKind.InSession, true, true, false, notCommitted, out reason));
            Assert.Equal("finalized-pending-tree", reason);
            foreach (EarlyLoadKind kind in new[]
                { EarlyLoadKind.Cold, EarlyLoadKind.PlainRewind, EarlyLoadKind.ReFlyStart, EarlyLoadKind.DiscardReFly })
            {
                Assert.Equal(ParsekScenario.CommittedCopyRestoreAction.Unchanged,
                    ParsekScenario.DecideCommittedCopyRestore(true, kind, true, false, false, notCommitted, out reason));
                Assert.Equal("load-kind-" + kind, reason);
            }
        }

        [Fact]
        public void SalvageStaleEpochMembersFromCommittedTree_OnlyStaleMembersTheCopyHolds()
        {
            var committed = MakeFutureTree("ql_salvage");
            var loaded = MakeQuicksaveTree("ql_salvage");
            var root = loaded.Recordings[loaded.RootRecordingId];
            var booster = loaded.Recordings[BoosterId(loaded)];
            root.SidecarLoadFailed = true;
            root.SidecarLoadFailureReason = ParsekScenario.StaleSidecarEpochReason;
            root.MergeState = MergeState.NotCommitted;
            booster.SidecarLoadFailed = true;
            booster.SidecarLoadFailureReason = "trajectory-missing";
            var orphan = new Recording
            {
                RecordingId = "orphan_ql_salvage", TreeId = loaded.Id,
                SidecarLoadFailed = true, SidecarLoadFailureReason = ParsekScenario.StaleSidecarEpochReason,
            };
            loaded.AddOrReplaceRecording(orphan);

            int salvaged = ParsekScenario.SalvageStaleEpochMembersFromCommittedTree(loaded, committed, out int unsalvaged);

            Assert.Equal(1, salvaged);
            Assert.Equal(1, unsalvaged);
            Assert.Same(root, loaded.Recordings[loaded.RootRecordingId]);
            Assert.False(root.SidecarLoadFailed);
            Assert.Null(root.SidecarLoadFailureReason);
            Assert.True(root.FilesDirty);
            Assert.Equal(320.0, root.EndUT);
            Assert.Equal(2, root.SidecarEpoch);
            Assert.Equal(MergeState.NotCommitted, root.MergeState);
            Assert.Equal("root_ql_salvage", root.RecordingId);
            // A failure that is not a stale epoch is the pending salvage's, not this one's.
            Assert.True(booster.SidecarLoadFailed);
            Assert.Equal("trajectory-missing", booster.SidecarLoadFailureReason);
            Assert.True(orphan.SidecarLoadFailed);
            Assert.Equal(0, ParsekScenario.SalvageStaleEpochMembersFromCommittedTree(loaded, null, out unsalvaged));
        }

        [Fact]
        public void CaptureQuicksaveTreeFacts_TreeCommittedInQuicksaveFromCommittedTreeNodesOnly()
        {
            var active = MakeQuicksaveTree("ql_facts");
            var other = MakeQuicksaveTree("ql_facts_other");
            var pending = MakeQuicksaveTree("ql_facts_pending");

            var node = new ConfigNode("PARSEK_SCENARIO");
            other.Save(node.AddNode("RECORDING_TREE"));
            var pendingNode = node.AddNode("RECORDING_TREE");
            pending.Save(pendingNode);
            pendingNode.AddValue("isPending", "True");
            var activeNode = node.AddNode("RECORDING_TREE");
            active.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            HashSet<string> treeIds = ParsekScenario.CollectQuicksaveCommittedTreeIds(node);
            Assert.Equal(new[] { "ql_facts_other" }, treeIds.ToArray());
            var facts = ParsekScenario.CaptureQuicksaveTreeFacts(
                RecordingTree.Load(activeNode), ParsekScenario.CollectQuicksaveCommittedRecordingIds(node), treeIds);
            Assert.False(facts.TreeCommittedInQuicksave);
            Assert.False(facts.AnyMemberCommittedInQuicksave);

            active.Save(node.AddNode("RECORDING_TREE"));
            treeIds = ParsekScenario.CollectQuicksaveCommittedTreeIds(node);
            facts = ParsekScenario.CaptureQuicksaveTreeFacts(
                RecordingTree.Load(activeNode), ParsekScenario.CollectQuicksaveCommittedRecordingIds(node), treeIds);
            Assert.True(facts.TreeCommittedInQuicksave);
            Assert.True(facts.AnyMemberCommittedInQuicksave);
            Assert.False(ParsekScenario.CaptureQuicksaveTreeFacts(
                RecordingTree.Load(activeNode), null).TreeCommittedInQuicksave);
        }

        [Fact]
        public void TrimRecordingPastUT_CutsBodyFixedFramesAtTheCutoff()
        {
            // A parent-anchored Relative section the salvage or the refresh brings in from the
            // committed copy carries its body-fixed surface past the cutoff too.
            var rec = new Recording { RecordingId = "ql_bodyfixed" };
            SetPoints(rec, 100.0, 150.0, 300.0);
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 300.0,
                frames = new List<TrajectoryPoint>(),
                bodyFixedFrames = new List<TrajectoryPoint>(),
            };
            foreach (double ut in new[] { 100.0, 200.0, 250.0, 300.0 })
            {
                section.frames.Add(new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });
                section.bodyFixedFrames.Add(new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });
            }
            rec.TrackSections.Add(section);

            Assert.True(ParsekScenario.TrimRecordingPastUT(rec, 200.0));

            TrackSection trimmed = rec.TrackSections[0];
            Assert.Equal(200.0, trimmed.endUT);
            Assert.Equal(new[] { 100.0, 200.0 }, trimmed.frames.Select(f => f.ut).ToArray());
            Assert.Equal(new[] { 100.0, 200.0 }, trimmed.bodyFixedFrames.Select(f => f.ut).ToArray());
        }

        // ============================================================
        // Helpers
        // ============================================================

        private void AssertNoLine(string fragment)
        {
            string found = logLines.FirstOrDefault(l => l.Contains(fragment));
            Assert.True(found == null, "unexpected log line: " + found);
        }

        private void AssertAbandonedFutureRetired(
            RecordingTree resumed, string rootId, string boosterId, int expectedEventsPurged)
        {
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("Quickload abandoned-future reconcile:")
                && l.Contains("kind=QuickloadFlight")
                && l.Contains("trimmed=2")
                && l.Contains("pruned=0")
                && l.Contains("endStatesCleared=1")
                && l.Contains("eventsPurged=" + expectedEventsPurged)
                && l.Contains("ledgerRowsRetired=3")
                && l.Contains("skippedCommitted=0")
                && l.Contains("skippedQuicksaveHistory=0"));

            Recording booster = resumed.Recordings[boosterId];
            Assert.Null(booster.TerminalStateValue);
            Assert.False(booster.VesselDestroyed);
            Assert.Null(booster.CrewEndStates);
            Assert.False(booster.CrewEndStatesResolved);
            Assert.Equal(QuicksaveUT, booster.EndUT);
            Assert.True(resumed.BackgroundMap.TryGetValue(BoosterPid, out string bgId));
            Assert.Equal(boosterId, bgId);
            Assert.Equal(QuicksaveUT, resumed.Recordings[rootId].EndUT);

            Assert.Equal(new[] { "kept-150" },
                GameStateStore.Events.Select(e => e.key).ToArray());
            Assert.Equal(new[] { "ContractComplete:" + rootId },
                Ledger.Actions
                    .Where(a => !RecalculationEngine.IsSeedType(a.Type))
                    .Select(a => a.Type + ":" + a.RecordingId)
                    .ToArray());
        }

        private static string BoosterId(RecordingTree tree) => "child_" + tree.Id + "_1";

        // The flight as the quicksave at 200 holds it: the pod (root, active) and a booster,
        // both still recording, both named at sidecar epoch 1.
        private static RecordingTree MakeQuicksaveTree(string id)
        {
            var tree = new RecordingTree
            {
                Id = id,
                TreeName = id,
                RootRecordingId = "root_" + id,
                ActiveRecordingId = "root_" + id,
            };
            var root = new Recording { RecordingId = "root_" + id, TreeId = id, VesselName = id, VesselPersistentId = RootPid };
            SetPoints(root, 100.0, 150.0, QuicksaveUT);
            tree.AddOrReplaceRecording(root);
            var booster = new Recording
            {
                RecordingId = BoosterId(tree), TreeId = id, VesselName = id + " booster", VesselPersistentId = BoosterPid,
            };
            SetPoints(booster, 120.0, 180.0, QuicksaveUT);
            tree.AddOrReplaceRecording(booster);
            foreach (var rec in tree.Recordings.Values)
            {
                rec.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
                rec.RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration;
                rec.SidecarEpoch = 1;
            }
            tree.RebuildBackgroundMap();
            return tree;
        }

        // The flight after the quicksave: the pod flew on to 320, the booster was destroyed at
        // 300 with Bob aboard. The OnSave after the commit wrote both at epoch 2.
        private static RecordingTree MakeFutureTree(string id)
        {
            var tree = MakeQuicksaveTree(id);
            SetPoints(tree.Recordings[tree.RootRecordingId], 100.0, 150.0, 320.0);
            var booster = tree.Recordings[BoosterId(tree)];
            SetPoints(booster, 120.0, 180.0, 300.0);
            booster.TerminalStateValue = TerminalState.Destroyed;
            booster.VesselDestroyed = true;
            booster.GhostVisualSnapshot = CrewSnapshot("Bob Kerman");
            booster.CrewEndStates = new Dictionary<string, KerbalEndState> { { "Bob Kerman", KerbalEndState.Dead } };
            booster.CrewEndStatesResolved = true;
            foreach (var rec in tree.Recordings.Values)
                rec.SidecarEpoch = 2;
            tree.RebuildBackgroundMap();
            return tree;
        }

        private static RecordingTree CommitFuture(string id, bool promoteBooster = false)
        {
            var tree = MakeFutureTree(id);
            // The commit's promotion pass made the crashed booster an Unfinished Flight.
            if (promoteBooster)
                tree.Recordings[BoosterId(tree)].MergeState = MergeState.CommittedProvisional;
            WriteSidecarsAtEpoch(tree, 2);
            foreach (var rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        // The copy-on-write clone the recorder resumed on the still-active pod after the
        // in-flight commit, carrying a post-commit tail; the F9's scene change stashed it.
        private static RecordingTree StashResumedClone(RecordingTree committed)
        {
            RecordingStore.ArmCommittedTreeRestoreAttempt(committed, "test copy-on-write resume");
            var clone = RecordingTree.DeepClone(committed);
            SetPoints(clone.Recordings[clone.RootRecordingId], 100.0, 150.0, 320.0, 340.0);
            RecordingStore.StashPendingTree(clone, PendingTreeState.Limbo);
            return clone;
        }

        private static void WriteSidecarsAtEpoch(RecordingTree tree, int epoch)
        {
            foreach (var rec in tree.Recordings.Values)
            {
                var onDisk = Recording.DeepClone(rec);
                onDisk.SidecarEpoch = epoch - 1;
                Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                    onDisk,
                    RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildTrajectoryRelativePath(onDisk.RecordingId)),
                    RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildVesselSnapshotRelativePath(onDisk.RecordingId)),
                    RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildGhostSnapshotRelativePath(onDisk.RecordingId)),
                    incrementEpoch: true));
                Assert.Equal(epoch, onDisk.SidecarEpoch);
            }
        }

        // A save node holding the active tree, and, when given, a committed tree node written
        // before it (OnSave writes committed trees first).
        private static ConfigNode QuicksaveNode(RecordingTree activeTree, RecordingTree committedTree)
        {
            var node = new ConfigNode("PARSEK_SCENARIO");
            if (committedTree != null)
                committedTree.Save(node.AddNode("RECORDING_TREE"));
            var treeNode = node.AddNode("RECORDING_TREE");
            activeTree.Save(treeNode);
            treeNode.AddValue("isActive", "True");
            return node;
        }

        private static void SeedFutureEventsAndRows(string rootId, string boosterId)
        {
            AddEvent(rootId, 150.0, GameStateEventType.ContractCompleted, "kept-150", "fundsReward=1");
            AddEvent(rootId, 300.0, GameStateEventType.ContractCompleted, "abandoned-300", "fundsReward=1");
            AddEvent(boosterId, 290.0, GameStateEventType.MilestoneAchieved, "abandoned-booster-290", "");
            AddRow(rootId, 150.0, GameActionType.ContractComplete, contractId: "kept-150");
            AddRow(rootId, 300.0, GameActionType.FundsEarning);
            AddRow(boosterId, 120.0, GameActionType.KerbalAssignment, kerbal: "Bob Kerman");
            AddRow(boosterId, 300.0, GameActionType.ReputationPenalty);
        }

        private static ConfigNode CrewSnapshot(string kerbal)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", kerbal);
            return snapshot;
        }

        private static void SetPoints(Recording rec, params double[] uts)
        {
            rec.Points.Clear();
            rec.TrackSections.Clear();
            foreach (double ut in uts)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = ut, altitude = 1000.0, bodyName = "Kerbin",
                    rotation = Quaternion.identity, velocity = Vector3.zero,
                });
            }
            rec.ExplicitStartUT = uts[0];
            rec.ExplicitEndUT = uts[uts.Length - 1];
        }

        private static void AddEvent(string recordingId, double ut, GameStateEventType type, string key, string detail)
        {
            var e = new GameStateEvent
            {
                ut = ut, eventType = type, key = key, detail = detail, recordingId = recordingId,
            };
            GameStateStore.AddEvent(ref e);
        }

        private static void AddRow(string recordingId, double ut, GameActionType type,
            string contractId = null, string kerbal = null)
        {
            Ledger.AddAction(new GameAction
            {
                UT = ut, Type = type, RecordingId = recordingId, ContractId = contractId, KerbalName = kerbal,
                StartUT = (float)ut,
            });
        }

        private static void RunResumePrep(RecordingTree tree, double resumeUT, LoadKind kind)
        {
            var recorder = new FlightRecorder { ActiveTree = tree };
            FlightRecorder.QuickloadResumeUTProviderForTesting = () => resumeUT;
            ParsekScenario.ConfigurePendingQuickloadResumeContext(tree, kind);
            MethodInfo method = typeof(FlightRecorder).GetMethod(
                "PrepareQuickloadResumeStateIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(recorder, null);
        }
    }
}
