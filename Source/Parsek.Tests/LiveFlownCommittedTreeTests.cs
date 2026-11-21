using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner RULE 3 ("My live flight wins", finding H7): while a committed vessel is flown
    /// again (the live tree is a copy of a committed tree, also across a restart in the
    /// save's active or pending tree) the optimizer leaves the committed tree alone, and the
    /// commit keeps the new leg instead of dropping the copy as a duplicate.
    /// </summary>
    [Collection("Sequential")]
    public class LiveFlownCommittedTreeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public LiveFlownCommittedTreeTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            ParsekScenario.ResetInstanceForTesting();
            Parsek.Logistics.RouteStore.ResetForTesting();
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            tempDir = Path.Combine(Path.GetTempPath(), "parsek_lfct_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            RecordingPaths.SaveRootOverrideForTesting = tempDir;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = false;
            Parsek.Logistics.RouteStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }

        private static Recording Rec(string id, double startUT, double endUT, string chainId = null,
            int chainIndex = 0)
        {
            var rec = new Recording
            {
                RecordingId = id,
                TreeId = "tree_1",
                VesselName = "Station",
                ChainId = chainId,
                ChainIndex = chainIndex,
                SegmentPhase = "exo",
                SegmentBodyName = "Mun",
                MergeState = MergeState.Immutable,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, altitude = 50000, bodyName = "Mun" });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, altitude = 50000, bodyName = "Mun" });
            return rec;
        }

        private static RecordingTree Tree(string active, params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Station",
                RootRecordingId = recs[0].RecordingId,
                ActiveRecordingId = active,
                BranchPoints = new List<BranchPoint>(),
            };
            foreach (var r in recs)
                tree.AddOrReplaceRecording(r);
            return tree;
        }

        private static RecordingTree CommitExisting(RecordingTree tree)
        {
            foreach (var r in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(r);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        private static BranchPoint Bp(string id) => new BranchPoint
        {
            Id = id,
            Type = BranchPointType.Undock,
            ParentRecordingIds = new List<string>(),
            ChildRecordingIds = new List<string>(),
        };

        // ---------- Commit ----------

        // catches: the optimizer re-cut the committed tree while the copy lived (the committed
        // side holds a tail id the copy lacks), so the strict gate refused and the duplicate
        // skip dropped the player's new leg; and a union that kept that tail beside the copy's
        // whole recording played the tail twice and left it the chain tip with the old ending.
        [Fact]
        public void CommitPendingTree_FlownAgainAfterAReCut_KeepsTheNewLeg_ReabsorbsTheCoveredSplitTail()
        {
            var committedHead = Rec("rec_head", 0, 100, "chain_A", 0);
            committedHead.Hidden = true;
            committedHead.MergeState = MergeState.CommittedProvisional;
            // After the split the tail is the chain tip, where later writes land: the player
            // sealed the slot there, the spawn stamp moved there, a loop was set on its row.
            var committedTail = Rec("rec_tail", 100, 200, "chain_A", 1);
            committedTail.TerminalStateValue = TerminalState.Orbiting;
            committedTail.MergeState = MergeState.Immutable;
            committedTail.VesselSpawned = true;
            committedTail.SpawnedVesselPersistentId = 777u;
            committedTail.LoopPlayback = true;
            var anchored = Rec("rec_probe", 150, 180);
            anchored.ParentAnchorRecordingId = "rec_tail";
            var committedTree = Tree("rec_tail", committedHead, committedTail, anchored);
            committedTree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_probe",
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_tail" },
                ChildRecordingIds = new List<string> { "rec_probe" },
            });
            anchored.ParentBranchPointId = "bp_probe";
            CommitExisting(committedTree);
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_tail", Type = GameActionType.FundsEarning, RecordingId = "rec_tail",
                UT = 160, FundsAwarded = 10f, FundsSource = FundsEarningSource.Other,
            });

            // The live copy was taken before the re-cut: one whole recording, then flown on.
            var liveHead = Rec("rec_head", 0, 300);
            liveHead.TerminalStateValue = TerminalState.Landed;
            PendingTreeSlot.StashPendingTree(Tree("rec_head", liveHead));

            Assert.True(TreeCommitService.CommitPendingTree());

            Assert.False(PendingTreeSlot.HasPendingTree);
            var committed = RecordingStore.FindCommittedTreeById("tree_1");
            Assert.Same(liveHead, committed.Recordings["rec_head"]);
            Assert.Equal("rec_head", committed.ActiveRecordingId);
            Assert.Same(liveHead, RecordingStore.CommittedRecordings.Single(r => r.RecordingId == "rec_head"));
            // Nothing else of the vessel's chain overlaps the live recording, and the stale tail
            // (old ending) is gone, so the chain tip is the live recording with the new ending.
            Assert.False(committed.Recordings.ContainsKey("rec_tail"));
            Assert.DoesNotContain(RecordingStore.CommittedRecordings, r => r.RecordingId == "rec_tail");
            Assert.DoesNotContain(RecordingStore.CommittedRecordings, r =>
                !ReferenceEquals(r, liveHead) && r.ChainId == "chain_A"
                && r.StartUT < liveHead.EndUT && r.EndUT > liveHead.StartUT);
            Assert.Equal(TerminalState.Landed, liveHead.TerminalStateValue);
            // RULE 1: the shared id keeps the committed head's player settings, and what only the
            // tip owned is not lost with it: the seal (slot state is read from the tip) and the
            // spawn claim. A per-row setting only the tail had (its loop) covered only the tail,
            // so it does not spread over the whole survivor (BATCH-B4).
            Assert.True(liveHead.Hidden);
            Assert.Equal(MergeState.Immutable, liveHead.MergeState);
            Assert.True(liveHead.VesselSpawned);
            Assert.Equal(777u, liveHead.SpawnedVesselPersistentId);
            Assert.False(liveHead.LoopPlayback);
            // What named the tail now names the survivor.
            Assert.Equal("rec_head", anchored.ParentAnchorRecordingId);
            Assert.Contains("rec_head", committed.BranchPoints.Single(bp => bp.Id == "bp_probe").ParentRecordingIds);
            Assert.DoesNotContain("rec_tail", committed.BranchPoints.Single(bp => bp.Id == "bp_probe").ParentRecordingIds);
            Assert.Equal("rec_head", Ledger.Actions.Single(a => a.ActionId == "act_tail").RecordingId);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("CommitTree: committed vessel flown again")
                && l.Contains("recordingRunsPastCommitted rec=rec_head")
                && l.Contains("reabsorbedSplitPieces=1")
                && l.Contains("keptCommittedOnlyRecordings=1"));
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("re-absorbed split piece 'rec_tail' into 'rec_head'"));
        }

        // catches: a committed-only chain piece the live recording does not cover is a real
        // separate piece of the timeline and must stay.
        [Fact]
        public void CommitPendingTree_FlownAgain_KeepsACommittedOnlyPieceTheLiveRecordingDoesNotCover()
        {
            CommitExisting(Tree("rec_tail",
                Rec("rec_head", 0, 100, "chain_A", 0),
                Rec("rec_tail", 100, 200, "chain_A", 1)));
            var incoming = Tree("rec_head", Rec("rec_head", 0, 150));
            incoming.BranchPoints.Add(Bp("bp_new_leg"));
            PendingTreeSlot.StashPendingTree(incoming);

            Assert.True(TreeCommitService.CommitPendingTree());

            Assert.True(RecordingStore.FindCommittedTreeById("tree_1").Recordings.ContainsKey("rec_tail"));
            Assert.Contains(RecordingStore.CommittedRecordings, r => r.RecordingId == "rec_tail");
            Assert.Contains(logLines, l => l.Contains("CommitTree: committed vessel flown again")
                && l.Contains("reabsorbedSplitPieces=0")
                && l.Contains("keptCommittedOnlyRecordings=1"));
        }

        // catches: folding a piece a supersede row names (a Re-Fly's old or new side) or a
        // supply route sources from would leave the row or the route pointing at nothing.
        [Theory]
        [InlineData("supersede")]
        [InlineData("route")]
        public void CommitPendingTree_FlownAgain_NeverFoldsAPieceARowOrARouteNames(string holder)
        {
            CommitExisting(Tree("rec_tail",
                Rec("rec_head", 0, 100, "chain_A", 0),
                Rec("rec_tail", 100, 200, "chain_A", 1)));
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            if (holder == "supersede")
                scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation
                {
                    RelationId = "rel_1", OldRecordingId = "rec_tail", NewRecordingId = "rec_fork", UT = 150,
                });
            ParsekScenario.SetInstanceForTesting(scenario);
            if (holder == "route")
                Parsek.Logistics.RouteStore.AddRoute(new Parsek.Tests.Generators.RouteFixtureBuilder()
                    .WithId("route_1").WithName("route_1")
                    .WithStatus(Parsek.Logistics.RouteStatus.Active)
                    .WithRecordingId("rec_tail")
                    .WithSourceRef(new Parsek.Logistics.RouteSourceRef { RecordingId = "rec_tail", TreeId = "tree_1" })
                    .Build());
            PendingTreeSlot.StashPendingTree(Tree("rec_head", Rec("rec_head", 0, 300)));

            Assert.True(TreeCommitService.CommitPendingTree());

            Assert.True(RecordingStore.FindCommittedTreeById("tree_1").Recordings.ContainsKey("rec_tail"));
            Assert.Contains(RecordingStore.CommittedRecordings, r => r.RecordingId == "rec_tail");
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("kept split piece 'rec_tail' beside 'rec_head'")
                && l.Contains(holder == "supersede" ? "supersede row" : "route source"));
            Assert.Contains(logLines, l => l.Contains("reabsorbedSplitPieces=0"));
        }

        [Fact]
        public void CarryTipOwnedStateOnFold_TipStateWins_SettingsOnlyWhereTheHeadHasNone()
        {
            var head = Rec("h", 0, 100, "chain_A", 0);
            head.MergeState = MergeState.CommittedProvisional;
            head.LoopPlayback = true;
            head.LoopIntervalSeconds = 600;
            head.RecordingGroups = new List<string> { "Head group" };
            var tip = Rec("t", 100, 200, "chain_A", 1);
            tip.MergeState = MergeState.Immutable;
            tip.Hidden = true;
            tip.LoopPlayback = true;
            tip.LoopIntervalSeconds = 99;
            tip.RecordingGroups = new List<string> { "Tip group" };
            tip.CrewDeathRespawns = true;
            tip.CrewDeathRespawnSeconds = 42;
            tip.SpawnedVesselPersistentId = 777u;

            string carried = TreeCommitService.CarryTipOwnedStateOnFold(tip, head);

            Assert.Equal(MergeState.Immutable, head.MergeState);
            // Hidden was the tip row's own: it does not spread over the head (BATCH-B4).
            Assert.False(head.Hidden);
            Assert.Equal(600, head.LoopIntervalSeconds);
            Assert.Equal(new[] { "Head group" }, head.RecordingGroups);
            Assert.True(head.CrewDeathRespawns);
            Assert.Equal(42, head.CrewDeathRespawnSeconds);
            Assert.Equal(777u, head.SpawnedVesselPersistentId);
            Assert.Contains("MergeState", carried);
            Assert.DoesNotContain("Loop", carried);
            Assert.DoesNotContain("Hidden", carried);
            Assert.Equal("", TreeCommitService.CarryTipOwnedStateOnFold(null, head));
        }

        // BATCH-B4: a folded piece's per-row settings (hide, ghost off, loop) covered only that
        // piece. Carried onto the survivor they covered the whole flight, and every later split
        // of the survivor copied them onto every re-cut row. They stay with the piece; the
        // survivor keeps its own, and the dropped ones are logged.
        // MUTATION NOTE: restoring the Hidden / PlaybackEnabled / Loop carry in
        // CarryUnsetPlayerSettings reds this.
        [Fact]
        public void CarryTipOwnedStateOnFold_LoopOnTheFoldedPieceOnly_DoesNotSpread()
        {
            var head = Rec("h2", 0, 100, "chain_B", 0);
            var tip = Rec("t2", 100, 200, "chain_B", 1);
            tip.LoopPlayback = true;
            tip.LoopIntervalSeconds = 45;
            tip.Hidden = true;
            tip.PlaybackEnabled = false;
            tip.RecordingGroups = new List<string> { "Tip group" };

            string carried = TreeCommitService.CarryTipOwnedStateOnFold(tip, head);

            Assert.False(head.LoopPlayback);
            Assert.False(head.Hidden);
            Assert.True(head.PlaybackEnabled);
            Assert.DoesNotContain("Loop", carried);
            Assert.Contains("RecordingGroups", carried);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("fold of 't2' into 'h2'")
                && l.Contains("dropped=[Hidden,PlaybackEnabled,Loop]"));

            // A later split of the survivor copies only the survivor's own settings.
            var second = Rec("h2_tail", 50, 100, "chain_B", 1);
            RecordingOptimizer.CopyPlayerRowSettings(head, second);
            Assert.False(second.LoopPlayback);
            Assert.False(second.Hidden);

            // Mirror: a setting the survivor has itself stays, and nothing is logged as dropped.
            logLines.Clear();
            var loopingHead = Rec("h3", 0, 100, "chain_C", 0);
            loopingHead.LoopPlayback = true;
            var loopingTip = Rec("t3", 100, 200, "chain_C", 1);
            loopingTip.LoopPlayback = true;
            TreeCommitService.CarryTipOwnedStateOnFold(loopingTip, loopingHead);
            Assert.True(loopingHead.LoopPlayback);
            Assert.DoesNotContain(logLines, l => l.Contains("dropped=["));
        }

        [Fact]
        public void FindCoveredSplitPieces_Table()
        {
            var existing = Tree("h",
                Rec("h", 0, 100, "chain_A", 0),
                Rec("t1", 100, 200, "chain_A", 1),
                Rec("t2", 200, 400, "chain_A", 2),
                Rec("other", 100, 200, "chain_B", 0));
            var incoming = Tree("h", Rec("h", 0, 300));

            var pieces = TreeCommitService.FindCoveredCommittedOnlySplitPieces(existing, incoming);

            Assert.Single(pieces);
            Assert.Equal("t1", pieces[0].Key);
            Assert.Equal("h", pieces[0].Value);
            Assert.Empty(TreeCommitService.FindCoveredCommittedOnlySplitPieces(existing, existing));
        }

        // catches: a copy that was never flown (the same flight, re-cut on the committed side)
        // must still be skipped as a duplicate, not unioned back over the cut.
        [Fact]
        public void CommitPendingTree_UnflownCopyOfAReCutTree_IsStillADuplicate()
        {
            CommitExisting(Tree("rec_tail",
                Rec("rec_head", 0, 100, "chain_A", 0),
                Rec("rec_tail", 100, 200, "chain_A", 1)));
            PendingTreeSlot.StashPendingTree(Tree("rec_head", Rec("rec_head", 0, 200)));

            Assert.False(TreeCommitService.CommitPendingTree());

            var committed = RecordingStore.FindCommittedTreeById("tree_1");
            Assert.Equal(100, committed.Recordings["rec_head"].EndUT);
            Assert.Contains(logLines, l => l.Contains("CommitTree: duplicate tree id='tree_1' skipped")
                && l.Contains("flownAgain=false evidence=none"));
        }

        [Fact]
        public void IsFlownAgain_NewBranchPoint_IsNewFlight()
        {
            var existing = Tree("a", Rec("a", 0, 100));
            var incoming = Tree("a", Rec("a", 0, 100));
            incoming.BranchPoints.Add(Bp("bp_new"));

            Assert.True(TreeCommitService.IsCommittedTreeFlownAgain(existing, incoming, out string evidence));
            Assert.Equal("newBranchPoints=1", evidence);
        }

        [Fact]
        public void IsFlownAgain_NewRecordingPastTheCommittedTree_IsNewFlight_OneInsideItIsNot()
        {
            var existing = Tree("a", Rec("a", 0, 100), Rec("b", 0, 150));

            var inside = Tree("a", Rec("a", 0, 100), Rec("merged_away", 20, 140));
            Assert.False(TreeCommitService.IsCommittedTreeFlownAgain(existing, inside, out string e1));
            Assert.Equal("none", e1);

            var past = Tree("a", Rec("a", 0, 100), Rec("new_leg", 120, 260));
            Assert.True(TreeCommitService.IsCommittedTreeFlownAgain(existing, past, out string e2));
            Assert.StartsWith("newRecordingPastCommitted rec=new_leg", e2);
        }

        [Fact]
        public void IsFlownAgain_PredictedTailIsNotFlight()
        {
            var existing = Tree("a", Rec("a", 0, 100));
            var copy = Rec("a", 0, 100);
            copy.OrbitSegments.Add(new OrbitSegment { startUT = 100, endUT = 5000, isPredicted = true });

            Assert.False(TreeCommitService.IsCommittedTreeFlownAgain(existing, Tree("a", copy), out _));

            copy.OrbitSegments.Add(new OrbitSegment { startUT = 100, endUT = 400, isPredicted = false });
            Assert.True(TreeCommitService.IsCommittedTreeFlownAgain(existing, Tree("a", copy), out _));
        }

        [Fact]
        public void IsFlownAgain_SameObjectOrOtherId_IsNotACopy()
        {
            var tree = Tree("a", Rec("a", 0, 100));
            Assert.False(TreeCommitService.IsCommittedTreeFlownAgain(tree, tree, out string e));
            Assert.Equal("not-a-copy", e);
            var other = Tree("a", Rec("a", 0, 500));
            other.Id = "tree_2";
            Assert.False(TreeCommitService.IsCommittedTreeFlownAgain(tree, other, out e));
            Assert.Equal("not-a-copy", e);
            Assert.False(TreeCommitService.IsCommittedTreeFlownAgain(null, tree, out _));
        }

        // ---------- Optimizer hold ----------

        // catches: the cold load's optimizer pass re-cut (here: merged) the committed tree a
        // saved live copy belongs to, so the copy's commit then read as a duplicate.
        [Fact]
        public void OptimizerPass_LeavesATreeWithAPendingLiveCopyWhole()
        {
            CommitExisting(Tree("seg_tail",
                Rec("seg_head", 17000, 17030, "chain_A", 0),
                Rec("seg_tail", 17030, 17060, "chain_A", 1)));
            PendingTreeSlot.StashPendingTree(Tree("seg_tail",
                Rec("seg_head", 17000, 17030, "chain_A", 0),
                Rec("seg_tail", 17030, 17090, "chain_A", 1)), PendingTreeState.Limbo);
            logLines.Clear();

            CommittedListOptimizer.RunOptimizationPass();

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("Optimization pass: tree 'tree_1' held whole while its live copy is flown")
                && l.Contains("pendingCopy=True"));
        }

        [Fact]
        public void OptimizerPass_WithoutALiveCopy_StillMergesTheSameTree()
        {
            CommitExisting(Tree("seg_tail",
                Rec("seg_head", 17000, 17030, "chain_A", 0),
                Rec("seg_tail", 17030, 17060, "chain_A", 1)));

            CommittedListOptimizer.RunOptimizationPass();

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.DoesNotContain(logLines, l => l.Contains("held whole while its live copy is flown"));
        }

        [Fact]
        public void CollectLiveCopyTreeIds_DistinctCopiesAndTheRestoreAttempt()
        {
            var committedA = Tree("a", Rec("a", 0, 1));
            var committedB = Tree("b", Rec("b", 0, 1));
            committedB.Id = "tree_B";
            var committedC = Tree("c", Rec("c", 0, 1));
            committedC.Id = "tree_C";
            var trees = new List<RecordingTree> { committedA, committedB, committedC };
            var copyOfA = Tree("a", Rec("a", 0, 2));
            var fresh = Tree("x", Rec("x", 0, 1));
            fresh.Id = "tree_new";

            var ids = TreeCommitService.CollectLiveCopyTreeIds(
                trees, new[] { copyOfA, committedB, fresh, null }, "tree_C");

            Assert.Equal(new[] { "tree_1", "tree_C" }, ids.OrderBy(s => s, StringComparer.Ordinal).ToArray());

            var held = TreeCommitService.AddLiveCopyRecordingIds(
                new HashSet<string> { "route_src" }, trees, ids);
            Assert.Equal(new[] { "a", "c", "route_src" }, held.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        }
    }
}
