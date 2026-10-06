using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// F9 back into the quicksave of a flight that was Discarded (todo
    /// QUICKLOAD-INTO-DISCARDED-FLIGHT-RECORDS-AGAIN, owner ruling 2026-10-06: the resumed
    /// flight starts a fresh recording from the loaded state, as if it had never been
    /// discarded). Discard deletes the tree's sidecars, so the quicksave's active tree loads
    /// with every member `trajectory-missing`; the shape is the synthetic-fixture marker, so
    /// nothing drops it and the restore used to stash it and resume the recorder INTO the
    /// discarded ids, whose trajectory and snapshots were gone.
    /// </summary>
    [Collection("Sequential")]
    public class QuickloadIntoDiscardedFlightTests : IDisposable
    {
        private const uint RootPid = 111u;
        private const uint BoosterPid = 222u;

        private readonly List<string> logLines = new List<string>();
        private readonly string saveRoot;

        public QuickloadIntoDiscardedFlightTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResetAll();
            RecordingStore.SuppressLogging = false;
            GameStateStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;

            saveRoot = Path.Combine(Path.GetTempPath(), "parsek-ql-discarded-" + Guid.NewGuid().ToString("N"));
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
            TreeDiscardPurge.ResetTestOverrides();
            GameStateRecorder.PendingScienceSubjects.Clear();
        }

        // ============================================================
        // End to end: F5, fly on, Discard, F9.
        // ============================================================

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DiscardThenF9_TheDiscardedTreeIsNotRestored_AFreshRecordingIsArmed(bool coldLoad)
        {
            ConfigNode quicksave = F5AndDiscard("ql_disc");

            logLines.Clear();
            bool restored = ParsekScenario.TryRestoreActiveTreeNode(
                quicksave, coldLoad ? EarlyLoadKind.Cold : EarlyLoadKind.InSession, loadedSceneIsFlight: true);

            // Nothing holds the discarded ids: no pending stash, no committed copy, no resume.
            Assert.False(restored);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == "ql_disc");
            Assert.DoesNotContain(RecordingStore.CommittedRecordings, r => r.TreeId == "ql_disc");
            Assert.False(ParsekScenario.MatchesPendingQuickloadResumeContext("ql_disc"));
            Assert.DoesNotContain(logLines, l => l.Contains("TryRestoreActiveTreeNode: stashed active tree 'ql_disc'"));

            Assert.True(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
            Assert.Equal("ql_disc", ParsekScenario.FreshRecordingAfterDiscardTreeIdForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("TryRestoreActiveTreeNode: saved active tree 'ql_disc' id=ql_disc was discarded this session")
                && l.Contains("members=2")
                && l.Contains("action=StartFreshRecording reason=discarded-this-session")
                && l.Contains("activeRecId=root_ql_disc")
                && l.Contains("poppedLimbo=none")
                && l.Contains("a fresh recording starts for the active vessel at flight ready"));
        }

        [Fact]
        public void DiscardThenF9_TheLiveTreeTheLoadAbandonsIsPoppedAsTheRestoreWould()
        {
            ConfigNode quicksave = F5AndDiscard("ql_disc");
            // After the Discard the player flew another vessel; the F9's scene change stashed
            // that live tree as Limbo. A restore pops it for the loaded tree; so does the decline,
            // or the Limbo dispatch would try to resume it onto the loaded vessel.
            RecordingTree live = MakeTree("ql_live");
            RecordingStore.StashPendingTree(live, PendingTreeState.Limbo);

            logLines.Clear();
            Assert.False(ParsekScenario.TryRestoreActiveTreeNode(quicksave));

            Assert.False(RecordingStore.HasPendingTree);
            Assert.True(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
            Assert.Contains(logLines, l =>
                l.Contains("was discarded this session")
                && l.Contains("poppedLimbo='ql_live' id=ql_live state=Limbo"));
        }

        [Fact]
        public void DiscardThenLoadOutsideFlight_DeclinedButNothingArmed()
        {
            ConfigNode quicksave = F5AndDiscard("ql_disc");

            logLines.Clear();
            Assert.False(ParsekScenario.TryRestoreActiveTreeNode(
                quicksave, EarlyLoadKind.InSession, loadedSceneIsFlight: false));

            Assert.False(RecordingStore.HasPendingTree);
            Assert.False(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
            Assert.Contains(logLines, l =>
                l.Contains("was discarded this session")
                && l.Contains("the load lands outside FLIGHT, nothing starts"));
        }

        [Fact]
        public void DiscardedTreeWithNoActiveRecording_DeclinedButNothingArmed()
        {
            RecordingTree tree = MakeTree("ql_outsider");
            tree.ActiveRecordingId = null;
            tree.RebuildBackgroundMap();
            ConfigNode quicksave = F5(tree);
            Discard(tree);

            Assert.False(ParsekScenario.TryRestoreActiveTreeNode(quicksave));

            Assert.False(RecordingStore.HasPendingTree);
            Assert.False(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
            Assert.Contains(logLines, l =>
                l.Contains("action=DropWithoutRecording")
                && l.Contains("the save's active vessel was not being recorded, nothing starts"));
        }

        // ============================================================
        // Not a discard: today's behaviour holds.
        // ============================================================

        [Fact]
        public void SidecarsMissingWithoutADiscard_RestoresAsBefore()
        {
            // A copied save folder without its Parsek files, or files deleted by hand: the same
            // on-disk shape as a discard, but nothing this session discarded the tree.
            RecordingTree tree = MakeTree("ql_missing");
            ConfigNode quicksave = F5(tree);
            foreach (Recording rec in tree.Recordings.Values)
                File.Delete(TrajectoryPath(rec.RecordingId));

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(quicksave));

            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            Assert.Equal("ql_missing", RecordingStore.PendingTree.Id);
            Assert.False(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
            Assert.DoesNotContain(logLines, l => l.Contains("was discarded this session"));
            Assert.Contains(logLines, l =>
                l.Contains("TryRestoreActiveTreeNode: stashed active tree 'ql_missing'")
                && l.Contains("with 2 sidecar hydration failure(s)"));
        }

        [Fact]
        public void DiscardedTreeWithACorruptSidecar_BehavesAsBefore()
        {
            ConfigNode quicksave = F5AndDiscard("ql_corrupt");
            // Something wrote an unreadable trajectory under the root's id: not the discard's
            // shape, so the corrupt-save handling stays (the root is rejected, the tree dropped).
            File.WriteAllText(TrajectoryPath("root_ql_corrupt"), "not a trajectory");

            logLines.Clear();
            Assert.False(ParsekScenario.TryRestoreActiveTreeNode(quicksave));

            Assert.False(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
            Assert.Contains(logLines, l =>
                l.Contains("was discarded this session but is restored as before")
                && l.Contains("reason=member-failed id=root_ql_corrupt reason=text-sidecar-unsupported"));
            Assert.Contains(logLines, l => l.Contains("TryRestoreActiveTreeNode: dropped entire tree 'ql_corrupt'"));
        }

        [Fact]
        public void DiscardedCloneOfACommittedTree_RestoresAsBefore()
        {
            // The copy-on-write shape: tree ql_cow is committed, the recorder resumed its vessel
            // on a same-id clone that gained a segment, F5 saved the clone, and the Discard
            // deleted the segment only (committed ids are preserved), so the tree id is noted.
            // The committed members' files are gone too here (a damaged folder), so every member
            // reads trajectory-missing and only the committed-copy guard keeps the decline off.
            RecordingTree committed = MakeTree("ql_cow");
            WriteSidecars(committed);
            foreach (Recording rec in committed.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(committed);

            RecordingTree clone = RecordingTree.DeepClone(committed);
            var segment = new Recording
            {
                RecordingId = "seg_ql_cow", TreeId = "ql_cow", VesselName = "ql_cow", VesselPersistentId = RootPid,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                SidecarEpoch = 0,
            };
            SetPoints(segment, 200.0, 250.0);
            WriteSidecar(segment);
            clone.AddOrReplaceRecording(segment);
            clone.ActiveRecordingId = "seg_ql_cow";
            clone.RebuildBackgroundMap();
            ConfigNode quicksave = QuicksaveNode(clone);

            RecordingStore.StashPendingTree(RecordingTree.DeepClone(clone), PendingTreeState.Finalized);
            RecordingStore.DiscardPendingTree();
            Assert.True(RecordingStore.WasTreeDiscardedThisSession("ql_cow"));
            Assert.False(File.Exists(TrajectoryPath("seg_ql_cow")));
            Assert.True(File.Exists(TrajectoryPath("root_ql_cow")));
            foreach (Recording rec in committed.Recordings.Values)
                File.Delete(TrajectoryPath(rec.RecordingId));

            logLines.Clear();
            ParsekScenario.TryRestoreActiveTreeNode(quicksave);

            Assert.False(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
            Assert.DoesNotContain(logLines, l => l.Contains("- not restoring it"));
            Assert.Contains(logLines, l =>
                l.Contains("TryRestoreActiveTreeNode: tree 'ql_cow' id=ql_cow was discarded this session "
                    + "but is restored as before (reason=committed-copy-in-memory)"));
        }

        [Fact]
        public void DiscardedTreeWithASameIdPendingTree_RestoresAsBeforeFromThePendingTree()
        {
            // A pending tree carrying the discarded tree's id is live state the restore salvages
            // from, not a discard to honour: the decline must neither fire nor pop it.
            RecordingTree tree = MakeTree("ql_same");
            ConfigNode quicksave = F5(tree);
            Discard(tree);
            RecordingTree sameId = RecordingTree.DeepClone(tree);
            RecordingStore.StashPendingTree(sameId, PendingTreeState.Limbo);

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(quicksave));

            Assert.False(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            Assert.Equal("ql_same", RecordingStore.PendingTree.Id);
            Assert.Contains(logLines, l =>
                l.Contains("TryRestoreActiveTreeNode: tree 'ql_same' id=ql_same was discarded this session "
                    + "but is restored as before (reason=pending-same-id-in-memory)"));
            Assert.Contains(logLines, l =>
                l.Contains("TryRestoreActiveTreeNode: restored 2 hydration-failed recording(s) from matching pending tree 'ql_same'"));
        }

        // ============================================================
        // Mirror: the save holds the discarded tree as its PENDING tree.
        // ============================================================

        [Fact]
        public void DiscardedTreeSavedAsPending_NotRestoredAsAnEmptyShell()
        {
            RecordingTree tree = MakeTree("ql_pending");
            WriteSidecars(tree);
            ConfigNode save = PendingNode(tree);
            Discard(tree);

            logLines.Clear();
            Assert.False(ParsekScenario.TryRestorePendingTreeNode(save));

            Assert.False(RecordingStore.HasPendingTree);
            Assert.False(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("TryRestorePendingTreeNode: saved pending tree 'ql_pending' id=ql_pending was discarded this session")
                && l.Contains("not restoring it"));
        }

        [Fact]
        public void PendingTreeWithSidecarsMissingWithoutADiscard_RestoresAsBefore()
        {
            RecordingTree tree = MakeTree("ql_pending_missing");
            WriteSidecars(tree);
            ConfigNode save = PendingNode(tree);
            foreach (Recording rec in tree.Recordings.Values)
                File.Delete(TrajectoryPath(rec.RecordingId));

            logLines.Clear();
            Assert.True(ParsekScenario.TryRestorePendingTreeNode(save));

            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal("ql_pending_missing", RecordingStore.PendingTree.Id);
            Assert.DoesNotContain(logLines, l => l.Contains("was discarded this session"));
        }

        [Fact]
        public void DiscardPendingTree_NotesTheTreeOnlyWhenItDeletedSidecars()
        {
            RecordingTree discarded = MakeTree("ql_noted");
            F5(discarded);
            Discard(discarded);
            Assert.True(RecordingStore.WasTreeDiscardedThisSession("ql_noted"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("Noted tree 'ql_noted' id=ql_noted as discarded this session (removedSidecarSets=2"));

            // Every member committed history: the discard deletes nothing and notes nothing.
            RecordingTree overlap = MakeTree("ql_overlap");
            F5(overlap);
            foreach (Recording rec in overlap.Recordings.Values)
                RecordingStore.AddCommittedInternal(Recording.DeepClone(rec));
            Discard(overlap);
            Assert.False(RecordingStore.WasTreeDiscardedThisSession("ql_overlap"));
            Assert.True(File.Exists(TrajectoryPath("root_ql_overlap")));
        }

        // ============================================================
        // Pure decision
        // ============================================================

        [Fact]
        public void DecideDiscardedActiveTreeRestore_Table()
        {
            Assert.Equal(DiscardedActiveTreeRestoreAction.StartFreshRecording,
                Decide(Hydrated(MakeTree("t"), missing: true), discarded: true, out string reason));
            Assert.Equal("discarded-this-session", reason);

            Assert.Equal(DiscardedActiveTreeRestoreAction.Restore,
                Decide(Hydrated(MakeTree("t"), missing: true), discarded: false, out reason));
            Assert.Equal("not-discarded-this-session", reason);

            Assert.Equal(DiscardedActiveTreeRestoreAction.Restore,
                Decide(Hydrated(MakeTree("t"), missing: true), discarded: true, out reason, committedCopy: true));
            Assert.Equal("committed-copy-in-memory", reason);

            Assert.Equal(DiscardedActiveTreeRestoreAction.Restore,
                Decide(Hydrated(MakeTree("t"), missing: true), discarded: true, out reason, pendingSameId: true));
            Assert.Equal("pending-same-id-in-memory", reason);

            RecordingTree oneHydrated = Hydrated(MakeTree("t"), missing: true);
            RecordingStore.ClearSidecarLoadFailure(oneHydrated.Recordings["booster_t"]);
            Assert.Equal(DiscardedActiveTreeRestoreAction.Restore,
                Decide(oneHydrated, discarded: true, out reason));
            Assert.Equal("member-hydrated id=booster_t", reason);

            foreach (string other in new[] { "stale-sidecar-epoch", "trajectory-invalid", "generation-older",
                "snapshot-vessel-missing", "exception:IOException" })
            {
                RecordingTree failed = Hydrated(MakeTree("t"), missing: true);
                failed.Recordings["root_t"].SidecarLoadFailureReason = other;
                Assert.Equal(DiscardedActiveTreeRestoreAction.Restore,
                    Decide(failed, discarded: true, out reason));
                Assert.Equal("member-failed id=root_t reason=" + other, reason);
            }

            RecordingTree outsider = Hydrated(MakeTree("t"), missing: true);
            outsider.ActiveRecordingId = null;
            Assert.Equal(DiscardedActiveTreeRestoreAction.DropWithoutRecording,
                Decide(outsider, discarded: true, out reason));

            Assert.Equal(DiscardedActiveTreeRestoreAction.Restore,
                Decide(new RecordingTree { Id = "empty" }, discarded: true, out reason));
            Assert.Equal("no-members", reason);
        }

        // ============================================================
        // Flight-ready start through the recorder entry seam
        // ============================================================

        [Fact]
        public void FlightReady_StartsOnceThroughTheRecorderEntry_ThenTheRequestIsGone()
        {
            ArmByDiscardThenF9();
            int starts = 0;

            logLines.Clear();
            bool started = Consume(restoreScheduled: false, start: () => { starts++; return true; });

            Assert.True(started);
            Assert.Equal(1, starts);
            Assert.False(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("Fresh recording after discarded restore started")
                && l.Contains("discarded tree 'ql_disc' id=ql_disc activeRecId=root_ql_disc stays discarded"));

            // A second flight-ready (double fire) or a later scene finds nothing armed.
            Assert.False(Consume(restoreScheduled: false, start: () => { starts++; return true; }));
            Assert.Equal(1, starts);
        }

        [Theory]
        [InlineData(true, false, false, true, "SkipRestoreScheduled")]
        [InlineData(false, true, false, true, "SkipRecorderLive")]
        [InlineData(false, false, true, true, "SkipActiveTree")]
        [InlineData(false, false, false, false, "SkipNoActiveVessel")]
        public void FlightReady_AnotherOwnerOfTheFlight_NoStartAndTheRequestIsCleared(
            bool restoreScheduled, bool recorderLive, bool hasActiveTree, bool hasActiveVessel, string decision)
        {
            ArmByDiscardThenF9();
            int starts = 0;

            logLines.Clear();
            bool started = ParsekScenario.ConsumeFreshRecordingAfterDiscard(
                restoreScheduled, recorderLive, hasActiveTree, hasActiveVessel,
                () => { starts++; return true; });

            Assert.False(started);
            Assert.Equal(0, starts);
            Assert.False(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
            Assert.Contains(logLines, l =>
                l.Contains("Fresh recording after discarded restore skipped: decision=" + decision));
        }

        [Fact]
        public void FlightReady_StartLeavesNoRecorder_WarnsAndReportsFalse()
        {
            ArmByDiscardThenF9();

            logLines.Clear();
            Assert.False(Consume(restoreScheduled: false, start: () => false));

            Assert.False(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN][Scenario]")
                && l.Contains("Fresh recording after discarded restore: StartRecording left no recorder running"));
        }

        [Fact]
        public void FlightReady_NothingArmed_NoStartNoLog()
        {
            int starts = 0;
            logLines.Clear();
            Assert.False(Consume(restoreScheduled: false, start: () => { starts++; return true; }));
            Assert.Equal(0, starts);
            Assert.DoesNotContain(logLines, l => l.Contains("Fresh recording after discarded restore"));
        }

        [Fact]
        public void TheNextLoadClearsAnUnconsumedRequest()
        {
            ArmByDiscardThenF9();

            Assert.False(ParsekScenario.TryRestoreActiveTreeNode(new ConfigNode("PARSEK_SCENARIO")));

            Assert.False(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
        }

        [Fact]
        public void DiscardThenF9_TheLoadRecalculatesAtTheLoadedUT_NoFutureRowReachesKsp()
        {
            // A resume defers the load's KSP patch (its Limbo stash sits in the pending slot);
            // the decline leaves no pending tree, recorder or active tree, so an uncut walk
            // would patch every row after the loaded UT into KSP. A committed-future Space
            // Center spending at 400 must stay out of the state at 150.
            Ledger.AddAction(new GameAction
            {
                UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 100000f,
            });
            Ledger.AddAction(new GameAction
            {
                UT = 400.0, Type = GameActionType.FacilityUpgrade, FacilityId = "LaunchPad",
                ToLevel = 2, FacilityCost = 50000f,
            });
            ArmByDiscardThenF9();
            Assert.False(RecordingStore.HasPendingTree);

            logLines.Clear();
            bool cutoffUsed = ParsekScenario.RecalculateLedgerForInSessionLoad(
                isRevert: false, loadedScene: GameScenes.FLIGHT, planetariumReady: true, loadedUT: 150.0);

            Assert.True(cutoffUsed);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("OnLoad: post-rewind current-UT cutoff decision useCurrentUtCutoff=True"));
            Assert.Contains(logLines, l =>
                l.Contains("Current-UT ledger recalculation: reason=post-rewind-load cutoffUT=150"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("RecalculateAndPatch: ") && l.Contains("cutoffUT=null"));
            Assert.Equal(100000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.Equal(0.0, LedgerOrchestrator.Funds.GetTotalCommittedSpendings(), 1);
        }

        // ============================================================
        // Helpers
        // ============================================================

        // The flight at the quicksave: the pod (root, active) and a booster, both recording,
        // their sidecars written by the F5's OnSave at epoch 1. Returns the quicksave's node.
        private static ConfigNode F5(RecordingTree tree)
        {
            WriteSidecars(tree);
            return QuicksaveNode(tree);
        }

        // F5, fly on, Discard: the flown tree is the pending tree the merge dialog discards.
        private static ConfigNode F5AndDiscard(string id)
        {
            RecordingTree tree = MakeTree(id);
            ConfigNode quicksave = F5(tree);
            Discard(tree);
            foreach (Recording rec in tree.Recordings.Values)
                Assert.False(File.Exists(TrajectoryPath(rec.RecordingId)));
            return quicksave;
        }

        private static void Discard(RecordingTree tree)
        {
            RecordingTree flown = RecordingTree.DeepClone(tree);
            SetPoints(flown.Recordings[flown.RootRecordingId], 100.0, 200.0, 320.0);
            RecordingStore.StashPendingTree(flown, PendingTreeState.Finalized);
            RecordingStore.DiscardPendingTree();
            Assert.False(RecordingStore.HasPendingTree);
        }

        private static void ArmByDiscardThenF9()
        {
            Assert.False(ParsekScenario.TryRestoreActiveTreeNode(F5AndDiscard("ql_disc")));
            Assert.True(ParsekScenario.IsFreshRecordingAfterDiscardArmed);
        }

        private static bool Consume(bool restoreScheduled, Func<bool> start)
        {
            return ParsekScenario.ConsumeFreshRecordingAfterDiscard(
                restoreScheduled, recorderLive: false, hasActiveTree: false, hasActiveVessel: true,
                startRecording: start);
        }

        // Every member as a discarded tree hydrates (missing=true): trajectory-missing, no points.
        private static RecordingTree Hydrated(RecordingTree tree, bool missing)
        {
            foreach (Recording rec in tree.Recordings.Values)
            {
                rec.Points.Clear();
                if (missing)
                {
                    rec.SidecarLoadFailed = true;
                    rec.SidecarLoadFailureReason = "trajectory-missing";
                }
            }
            return tree;
        }

        private static DiscardedActiveTreeRestoreAction Decide(
            RecordingTree tree, bool discarded, out string reason,
            bool committedCopy = false, bool pendingSameId = false)
        {
            return ParsekScenario.DecideDiscardedActiveTreeRestore(
                tree, discarded, committedCopy, pendingSameId, out reason);
        }

        private static RecordingTree MakeTree(string id)
        {
            var tree = new RecordingTree
            {
                Id = id,
                TreeName = id,
                RootRecordingId = "root_" + id,
                ActiveRecordingId = "root_" + id,
            };
            var root = new Recording
            {
                RecordingId = "root_" + id, TreeId = id, VesselName = id, VesselPersistentId = RootPid,
            };
            SetPoints(root, 100.0, 150.0, 200.0);
            tree.AddOrReplaceRecording(root);
            var booster = new Recording
            {
                RecordingId = "booster_" + id, TreeId = id, VesselName = id + " booster",
                VesselPersistentId = BoosterPid,
            };
            SetPoints(booster, 120.0, 180.0, 200.0);
            tree.AddOrReplaceRecording(booster);
            foreach (Recording rec in tree.Recordings.Values)
            {
                rec.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
                rec.RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration;
                rec.SidecarEpoch = 0;
            }
            tree.RebuildBackgroundMap();
            return tree;
        }

        private static void WriteSidecars(RecordingTree tree)
        {
            foreach (Recording rec in tree.Recordings.Values)
                WriteSidecar(rec);
        }

        private static void WriteSidecar(Recording rec)
        {
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec,
                TrajectoryPath(rec.RecordingId),
                RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId)),
                RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId)),
                incrementEpoch: true));
            Assert.Equal(1, rec.SidecarEpoch);
            Assert.True(File.Exists(TrajectoryPath(rec.RecordingId)));
        }

        private static string TrajectoryPath(string recordingId)
        {
            return RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildTrajectoryRelativePath(recordingId));
        }

        private static ConfigNode QuicksaveNode(RecordingTree activeTree)
        {
            var node = new ConfigNode("PARSEK_SCENARIO");
            var treeNode = node.AddNode("RECORDING_TREE");
            activeTree.Save(treeNode);
            treeNode.AddValue("isActive", "True");
            return node;
        }

        private static ConfigNode PendingNode(RecordingTree pendingTree)
        {
            var node = new ConfigNode("PARSEK_SCENARIO");
            var treeNode = node.AddNode("RECORDING_TREE");
            pendingTree.Save(treeNode);
            treeNode.AddValue("isPending", "True");
            return node;
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
    }
}
