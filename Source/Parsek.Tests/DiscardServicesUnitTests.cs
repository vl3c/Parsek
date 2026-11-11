using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct unit tests for the discard-side RecordingStore services: the switch-segment
    /// scoped discard units (SwitchSegmentAttempts), the committed-tree prune and pending-only
    /// id collection of TreeDiscardService, and the restore-attempt id / cutoff builders of
    /// CommittedTreeRestoreAttempt.
    /// </summary>
    [Collection("Sequential")]
    public class DiscardServicesUnitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DiscardServicesUnitTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // --- builders ---

        private static Recording Rec(string id, string parentBp = null, string childBp = null)
        {
            return new Recording { RecordingId = id, ParentBranchPointId = parentBp, ChildBranchPointId = childBp };
        }

        private static BranchPoint Bp(string id, string[] parents, string[] children,
            BranchPointType type = BranchPointType.Undock, double ut = 0.0)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                ParentRecordingIds = parents == null ? null : new List<string>(parents),
                ChildRecordingIds = children == null ? null : new List<string>(children),
            };
        }

        private static RecordingTree Tree(string id, Recording[] recs, BranchPoint[] bps = null)
        {
            var tree = new RecordingTree { Id = id };
            foreach (var r in recs)
                tree.Recordings[r.RecordingId] = r;
            if (bps != null)
                tree.BranchPoints.AddRange(bps);
            return tree;
        }

        private static HashSet<string> Ids(params string[] ids)
        {
            return new HashSet<string>(ids, StringComparer.Ordinal);
        }

        private static string[] Sorted(IEnumerable<string> values)
        {
            var list = new List<string>(values);
            list.Sort(StringComparer.Ordinal);
            return list.ToArray();
        }

        /// <summary>
        /// root -> bp1 -> seg (the segment) -> bp2 -> {debrisA, debrisB}; a ground part placed by
        /// seg hangs off gp (parented by seg, not referenced by seg's child link).
        /// </summary>
        private static RecordingTree SegmentTree()
        {
            return Tree("t1",
                new[]
                {
                    Rec("root", childBp: "bp1"),
                    Rec("seg", parentBp: "bp1", childBp: "bp2"),
                    Rec("debrisA", parentBp: "bp2"),
                    Rec("debrisB", parentBp: "bp2"),
                    Rec("part", parentBp: "gp"),
                    Rec("other"),
                },
                new[]
                {
                    Bp("bp1", new[] { "root" }, new[] { "seg" }, BranchPointType.VesselSwitchContinuation),
                    Bp("bp2", new[] { "seg" }, new[] { "debrisA", "debrisB" }, BranchPointType.Breakup),
                    Bp("gp", new[] { "seg" }, new[] { "part" }, BranchPointType.GroundPartPlaced),
                });
        }

        // =====================================================================
        // SwitchSegmentAttempts: committed-restore clone / disposition
        // =====================================================================

        [Theory]
        [InlineData(null, "t1", "c1", false)]
        [InlineData("", "t1", "c1", false)]
        [InlineData("t1", "t1", "c1", true)]
        [InlineData("c1", "t1", "c1", true)]
        [InlineData("T1", "t1", "c1", false)]
        [InlineData("x", "t1", null, false)]
        [InlineData("x", null, "x", true)]
        public void IsCommittedRestoreCloneSession_Cases(
            string attemptTreeId, string sessionTreeId, string sessionCommittedTreeId, bool expected)
        {
            var session = new SwitchSegmentSession { TreeId = sessionTreeId, CommittedTreeId = sessionCommittedTreeId };
            Assert.Equal(expected, SwitchSegmentAttempts.IsCommittedRestoreCloneSession(attemptTreeId, session));
        }

        [Theory]
        [InlineData(true, 3, 3, (int)SwitchSegmentDisposition.CommittedRestoreClone)]
        [InlineData(true, 5, 1, (int)SwitchSegmentDisposition.CommittedRestoreClone)]
        [InlineData(false, 3, 3, (int)SwitchSegmentDisposition.Standalone)]
        [InlineData(false, 4, 3, (int)SwitchSegmentDisposition.BgMemberOrMixed)]
        [InlineData(false, 1, 1, (int)SwitchSegmentDisposition.Standalone)]
        public void ClassifySwitchSegmentDisposition_Cases(
            bool isClone, int treeCount, int subtreeCount, int expected)
        {
            Assert.Equal((SwitchSegmentDisposition)expected,
                SwitchSegmentAttempts.ClassifySwitchSegmentDisposition(isClone, treeCount, subtreeCount));
        }

        // =====================================================================
        // SwitchSegmentAttempts: subtree walk pieces
        // =====================================================================

        [Fact]
        public void CollectTreeRecordingsForIds_SkipsMissingNullAndEmpty()
        {
            var tree = SegmentTree();
            tree.Recordings["nullRec"] = null;
            var recs = SwitchSegmentAttempts.CollectTreeRecordingsForIds(
                tree, Ids("seg", "debrisA", "missing", "nullRec", ""));
            var ids = new List<string>();
            foreach (var r in recs) ids.Add(r.RecordingId);
            Assert.Equal(new[] { "debrisA", "seg" }, Sorted(ids));
        }

        [Fact]
        public void CollectTreeRecordingsForIds_NullRecordings_Empty()
        {
            var tree = new RecordingTree { Recordings = null };
            Assert.Empty(SwitchSegmentAttempts.CollectTreeRecordingsForIds(tree, Ids("a")));
        }

        [Fact]
        public void CollectSubtree_WalksChildLinksAndParentedBranches()
        {
            var tree = SegmentTree();
            var session = new SwitchSegmentSession { TreeId = "t1", ActiveSegmentRecordingId = "seg" };
            var ids = SwitchSegmentAttempts.CollectSwitchSegmentSubtreeRecordingIds(tree, session);
            Assert.Equal(new[] { "debrisA", "debrisB", "part", "seg" }, Sorted(ids));
        }

        [Fact]
        public void CollectSubtree_SegmentNotInTree_Empty()
        {
            var tree = SegmentTree();
            Assert.Empty(SwitchSegmentAttempts.CollectSwitchSegmentSubtreeRecordingIds(
                tree, new SwitchSegmentSession { ActiveSegmentRecordingId = "ghost" }));
            Assert.Empty(SwitchSegmentAttempts.CollectSwitchSegmentSubtreeRecordingIds(
                tree, new SwitchSegmentSession { ActiveSegmentRecordingId = null }));
            Assert.Empty(SwitchSegmentAttempts.CollectSwitchSegmentSubtreeRecordingIds(tree, null));
        }

        [Fact]
        public void CollectSubtree_CycleTerminates()
        {
            var tree = Tree("t",
                new[] { Rec("a", childBp: "x"), Rec("b", childBp: "y") },
                new[]
                {
                    Bp("x", new[] { "a" }, new[] { "b" }),
                    Bp("y", new[] { "b" }, new[] { "a" }),
                });
            var ids = SwitchSegmentAttempts.CollectSwitchSegmentSubtreeRecordingIds(
                tree, new SwitchSegmentSession { ActiveSegmentRecordingId = "a" });
            Assert.Equal(new[] { "a", "b" }, Sorted(ids));
        }

        [Fact]
        public void EnqueueParentedBranchChildren_QueuesUnseenChildrenOfEveryParentedBranch()
        {
            var tree = SegmentTree();
            var queue = new Queue<string>();
            SwitchSegmentAttempts.EnqueueParentedBranchChildren(tree, "seg", Ids("debrisA"), queue);
            Assert.Equal(new[] { "debrisB", "part" }, queue.ToArray());
        }

        [Fact]
        public void EnqueueParentedBranchChildren_SkipsNullListsAndNullBranches()
        {
            var tree = Tree("t", new[] { Rec("p") },
                new[] { null, Bp("a", null, new[] { "c" }), Bp("b", new[] { "p" }, null), Bp("c", new[] { "p" }, new[] { "", "k" }) });
            var queue = new Queue<string>();
            SwitchSegmentAttempts.EnqueueParentedBranchChildren(tree, "p", Ids(), queue);
            Assert.Equal(new[] { "k" }, queue.ToArray());

            var bare = new RecordingTree { BranchPoints = null };
            SwitchSegmentAttempts.EnqueueParentedBranchChildren(bare, "p", Ids(), queue);
            Assert.Single(queue);
        }

        [Fact]
        public void FindSwitchSegmentBranchPointById_OrdinalMatch()
        {
            var tree = SegmentTree();
            Assert.Same(tree.BranchPoints[1], SwitchSegmentAttempts.FindSwitchSegmentBranchPointById(tree, "bp2"));
            Assert.Null(SwitchSegmentAttempts.FindSwitchSegmentBranchPointById(tree, "BP2"));
            Assert.Null(SwitchSegmentAttempts.FindSwitchSegmentBranchPointById(tree, ""));
            Assert.Null(SwitchSegmentAttempts.FindSwitchSegmentBranchPointById(null, "bp2"));
        }

        [Fact]
        public void FindOverwrittenContinuingBranchPointId_LatestContinuingTypeWins()
        {
            var tree = Tree("t", new[] { Rec("r") },
                new[]
                {
                    Bp("early", new[] { "r" }, new[] { "x" }, BranchPointType.Breakup, ut: 10),
                    Bp("late", new[] { "r" }, new[] { "y" }, BranchPointType.JointBreak, ut: 20),
                    Bp("undock", new[] { "r" }, new[] { "z" }, BranchPointType.Undock, ut: 30),
                    Bp("otherParent", new[] { "q" }, new[] { "w" }, BranchPointType.Launch, ut: 40),
                });
            Assert.Equal("late", SwitchSegmentAttempts.FindOverwrittenContinuingBranchPointId(tree, tree.Recordings["r"]));
        }

        [Fact]
        public void FindOverwrittenContinuingBranchPointId_NoneOrBadInput_Null()
        {
            var tree = Tree("t", new[] { Rec("r") },
                new[] { Bp("undock", new[] { "r" }, new[] { "z" }, BranchPointType.Undock) });
            Assert.Null(SwitchSegmentAttempts.FindOverwrittenContinuingBranchPointId(tree, tree.Recordings["r"]));
            Assert.Null(SwitchSegmentAttempts.FindOverwrittenContinuingBranchPointId(tree, null));
            Assert.Null(SwitchSegmentAttempts.FindOverwrittenContinuingBranchPointId(tree, Rec(null)));
            Assert.Null(SwitchSegmentAttempts.FindOverwrittenContinuingBranchPointId(null, Rec("r")));
        }

        // =====================================================================
        // SwitchSegmentAttempts: session-authored branch points and removal
        // =====================================================================

        [Fact]
        public void CollectSessionAuthoredBranchPointIds_CurrentMinusBaseline()
        {
            var tree = SegmentTree();
            var session = new SwitchSegmentSession { PreSessionBranchPointIds = new List<string> { "bp1" } };
            Assert.Equal(new[] { "bp2", "gp" },
                Sorted(SwitchSegmentAttempts.CollectSessionAuthoredBranchPointIds(tree, session)));
        }

        [Fact]
        public void CollectSessionAuthoredBranchPointIds_NullBaselineIsUnknown_EmptyBaselineIsAll()
        {
            var tree = SegmentTree();
            Assert.Empty(SwitchSegmentAttempts.CollectSessionAuthoredBranchPointIds(
                tree, new SwitchSegmentSession { PreSessionBranchPointIds = null }));
            Assert.Equal(3, SwitchSegmentAttempts.CollectSessionAuthoredBranchPointIds(
                tree, new SwitchSegmentSession { PreSessionBranchPointIds = new List<string>() }).Count);
            Assert.Empty(SwitchSegmentAttempts.CollectSessionAuthoredBranchPointIds(tree, null));
        }

        [Fact]
        public void CollectSessionAuthoredBranchPointIds_SkipsNullAndEmptyIds()
        {
            var tree = Tree("t", new[] { Rec("a") }, new[] { null, Bp("", new[] { "a" }, new[] { "b" }), Bp("n", null, null) });
            Assert.Equal(new[] { "n" }, Sorted(SwitchSegmentAttempts.CollectSessionAuthoredBranchPointIds(
                tree, new SwitchSegmentSession { PreSessionBranchPointIds = new List<string>() })));
        }

        [Fact]
        public void RemoveRecordingIdsFromTree_RemovesAndScrubsEndpointLists()
        {
            var tree = SegmentTree();
            int removed = SwitchSegmentAttempts.RemoveRecordingIdsFromTree(tree, Ids("debrisA", "seg", "absent", ""));
            Assert.Equal(2, removed);
            Assert.False(tree.Recordings.ContainsKey("seg"));
            Assert.Equal(new string[0], tree.BranchPoints[0].ChildRecordingIds.ToArray());
            Assert.Equal(new[] { "debrisB" }, tree.BranchPoints[1].ChildRecordingIds.ToArray());
            Assert.Empty(tree.BranchPoints[1].ParentRecordingIds);
            // Branch points themselves are left in place.
            Assert.Equal(3, tree.BranchPoints.Count);
        }

        [Fact]
        public void RemoveRecordingIdsFromTree_EmptyOrNullIds_NoChange()
        {
            var tree = SegmentTree();
            Assert.Equal(0, SwitchSegmentAttempts.RemoveRecordingIdsFromTree(tree, Ids()));
            Assert.Equal(0, SwitchSegmentAttempts.RemoveRecordingIdsFromTree(tree, null));
            Assert.Equal(0, SwitchSegmentAttempts.RemoveRecordingIdsFromTree(null, Ids("seg")));
            Assert.Equal(6, tree.Recordings.Count);
        }

        [Fact]
        public void ScrubRecordingIdsFromList_RemovesEveryOccurrenceInOrder()
        {
            var list = new List<string> { "a", "b", "a", "c" };
            SwitchSegmentAttempts.ScrubRecordingIdsFromList(list, Ids("a"));
            Assert.Equal(new[] { "b", "c" }, list.ToArray());
            SwitchSegmentAttempts.ScrubRecordingIdsFromList(null, Ids("a"));
            SwitchSegmentAttempts.ScrubRecordingIdsFromList(list, null);
            Assert.Equal(2, list.Count);
        }

        [Fact]
        public void RemoveSessionAuthoredBranchPointsFromTree_RemovesOnlyNamed()
        {
            var tree = SegmentTree();
            tree.BranchPoints.Add(null);
            int removed = SwitchSegmentAttempts.RemoveSessionAuthoredBranchPointsFromTree(tree, Ids("bp2", "gp", "zz"));
            Assert.Equal(2, removed);
            Assert.Equal(2, tree.BranchPoints.Count);
            Assert.Equal("bp1", tree.BranchPoints[0].Id);
            Assert.Null(tree.BranchPoints[1]);
            Assert.Equal(0, SwitchSegmentAttempts.RemoveSessionAuthoredBranchPointsFromTree(tree, Ids()));
            Assert.Equal(0, SwitchSegmentAttempts.RemoveSessionAuthoredBranchPointsFromTree(tree, null));
        }

        // =====================================================================
        // SwitchSegmentAttempts: post-discard repair
        // =====================================================================

        [Fact]
        public void RepairBranchPointLinks_ClearsParentLinkAndMarksDirty()
        {
            var tree = Tree("t", new[] { Rec("child", parentBp: "sess") });
            tree.Recordings["child"].FilesDirty = false;
            SwitchSegmentAttempts.RepairBranchPointLinksAfterSegmentDiscard(tree, Ids("sess"));
            Assert.Null(tree.Recordings["child"].ParentBranchPointId);
            Assert.True(tree.Recordings["child"].FilesDirty);
        }

        [Fact]
        public void RepairBranchPointLinks_RestoresOverwrittenContinuingBranchAndLogs()
        {
            // The session's switch continuation overwrote the breakup link on "parent".
            var tree = Tree("t", new[] { Rec("parent", childBp: "sess") },
                new[] { Bp("breakup", new[] { "parent" }, new[] { "debris" }, BranchPointType.Breakup, ut: 5) });
            SwitchSegmentAttempts.RepairBranchPointLinksAfterSegmentDiscard(tree, Ids("sess"));
            Assert.Equal("breakup", tree.Recordings["parent"].ChildBranchPointId);
            Assert.True(tree.Recordings["parent"].FilesDirty);
            Assert.Contains(logLines, l => l.Contains("[SwitchSegment]")
                && l.Contains("Discard: restored rec=parent childBranchPointId=breakup")
                && l.Contains("the removed session branch sess had overwritten it"));
        }

        [Fact]
        public void RepairBranchPointLinks_NoContinuingBranch_ClearsChildLinkSilently()
        {
            var tree = Tree("t", new[] { Rec("parent", childBp: "sess") });
            SwitchSegmentAttempts.RepairBranchPointLinksAfterSegmentDiscard(tree, Ids("sess"));
            Assert.Null(tree.Recordings["parent"].ChildBranchPointId);
            Assert.True(tree.Recordings["parent"].FilesDirty);
            Assert.DoesNotContain(logLines, l => l.Contains("Discard: restored rec="));
        }

        [Fact]
        public void RepairBranchPointLinks_UnrelatedLinksUntouched()
        {
            var tree = Tree("t", new[] { Rec("r", parentBp: "keepP", childBp: "keepC") });
            tree.Recordings["r"].FilesDirty = false;
            tree.Recordings["n"] = null;
            SwitchSegmentAttempts.RepairBranchPointLinksAfterSegmentDiscard(tree, Ids("sess"));
            Assert.Equal("keepP", tree.Recordings["r"].ParentBranchPointId);
            Assert.Equal("keepC", tree.Recordings["r"].ChildBranchPointId);
            Assert.False(tree.Recordings["r"].FilesDirty);
        }

        [Fact]
        public void RepairBranchPointLinks_EmptyAuthoredSet_NoOp()
        {
            var tree = Tree("t", new[] { Rec("r", parentBp: "sess") });
            SwitchSegmentAttempts.RepairBranchPointLinksAfterSegmentDiscard(tree, Ids());
            Assert.Equal("sess", tree.Recordings["r"].ParentBranchPointId);
        }

        [Theory]
        [InlineData("root", "root")]
        [InlineData("gone", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void ResolveActiveRecordingIdAfterSegmentDiscard_ParentIfSurviving(string parentId, string expected)
        {
            var tree = Tree("t", new[] { Rec("root") });
            Assert.Equal(expected, SwitchSegmentAttempts.ResolveActiveRecordingIdAfterSegmentDiscard(tree, parentId));
        }

        [Fact]
        public void ResolveActiveRecordingIdAfterSegmentDiscard_NullRecordings_Null()
        {
            Assert.Null(SwitchSegmentAttempts.ResolveActiveRecordingIdAfterSegmentDiscard(
                new RecordingTree { Recordings = null }, "root"));
        }

        // =====================================================================
        // TreeDiscardService: committed-tree prune
        // =====================================================================

        /// <summary>a -> bp1 -> b -> bp2 -> {c, d}; active = c, root = a.</summary>
        private static RecordingTree PruneTree()
        {
            var tree = Tree("pt",
                new[] { Rec("a", childBp: "bp1"), Rec("b", parentBp: "bp1", childBp: "bp2"), Rec("c", parentBp: "bp2"), Rec("d", parentBp: "bp2") },
                new[]
                {
                    Bp("bp1", new[] { "a" }, new[] { "b" }),
                    Bp("bp2", new[] { "b" }, new[] { "c", "d" }),
                });
            tree.RootRecordingId = "a";
            tree.ActiveRecordingId = "c";
            return tree;
        }

        [Fact]
        public void ScrubBranchPointEndpoints_DropsBranchWithEmptySide()
        {
            var tree = PruneTree();
            int scrubbed = TreeDiscardService.ScrubBranchPointEndpointsAndDropEmpty(tree, Ids("b"), out var dropped);
            // b is the only child of bp1 and the only parent of bp2: both go.
            Assert.Equal(2, scrubbed);
            Assert.Equal(new[] { "bp1", "bp2" }, Sorted(dropped));
            Assert.Empty(tree.BranchPoints);
        }

        [Fact]
        public void ScrubBranchPointEndpoints_PartialScrubKeepsBranch()
        {
            var tree = PruneTree();
            int scrubbed = TreeDiscardService.ScrubBranchPointEndpointsAndDropEmpty(tree, Ids("c"), out var dropped);
            Assert.Equal(1, scrubbed);
            Assert.Null(dropped);
            Assert.Equal(new[] { "d" }, tree.BranchPoints[1].ChildRecordingIds.ToArray());
        }

        [Fact]
        public void ScrubBranchPointEndpoints_AlreadyEmptySideIsDroppedToo()
        {
            // Defensive invariant: a branch point with no parent or no child connects nothing,
            // so the scrub drops it whether or not this call emptied it.
            var tree = Tree("t", new[] { Rec("a") },
                new[] { Bp("hollow", new[] { "a" }, new string[0]), Bp("nullKids", new[] { "a" }, null), Bp("", new string[0], new string[0]) });
            int scrubbed = TreeDiscardService.ScrubBranchPointEndpointsAndDropEmpty(tree, Ids("zzz"), out var dropped);
            Assert.Equal(0, scrubbed);
            Assert.Empty(tree.BranchPoints);
            Assert.Equal(new[] { "hollow", "nullKids" }, Sorted(dropped));
        }

        [Fact]
        public void ScrubBranchPointEndpoints_NoIdsOrBranches_NoChange()
        {
            var tree = PruneTree();
            Assert.Equal(0, TreeDiscardService.ScrubBranchPointEndpointsAndDropEmpty(tree, Ids(), out var d1));
            Assert.Null(d1);
            Assert.Equal(0, TreeDiscardService.ScrubBranchPointEndpointsAndDropEmpty(tree, null, out var d2));
            Assert.Null(d2);
            Assert.Equal(0, TreeDiscardService.ScrubBranchPointEndpointsAndDropEmpty(
                new RecordingTree { BranchPoints = null }, Ids("a"), out _));
            Assert.Equal(2, tree.BranchPoints.Count);
        }

        [Fact]
        public void ClearRecordingBranchPointRefs_ClearsBothSidesAndCounts()
        {
            var tree = PruneTree();
            tree.Recordings["n"] = null;
            int cleared = TreeDiscardService.ClearRecordingBranchPointRefs(tree, Ids("bp2"));
            Assert.Equal(3, cleared);
            Assert.Null(tree.Recordings["b"].ChildBranchPointId);
            Assert.Equal("bp1", tree.Recordings["b"].ParentBranchPointId);
            Assert.Null(tree.Recordings["c"].ParentBranchPointId);
            Assert.Null(tree.Recordings["d"].ParentBranchPointId);
            Assert.Equal("bp1", tree.Recordings["a"].ChildBranchPointId);
        }

        [Fact]
        public void PruneFromTree_NothingPresent_NoChangeAndZeroCounters()
        {
            var tree = PruneTree();
            int pruned = TreeDiscardService.PruneTaggedRecordingsFromTree(
                tree, Ids("absent", ""), null, out int scrubbed, out int dropped, out int cleared);
            Assert.Equal(0, pruned);
            Assert.Equal(0, scrubbed + dropped + cleared);
            Assert.Equal(4, tree.Recordings.Count);
            Assert.Equal("c", tree.ActiveRecordingId);
        }

        [Fact]
        public void PruneFromTree_LeafRemoved_ScrubsWithoutDroppingBranches()
        {
            var tree = PruneTree();
            int pruned = TreeDiscardService.PruneTaggedRecordingsFromTree(
                tree, Ids("d"), null, out int scrubbed, out int dropped, out int cleared);
            Assert.Equal(1, pruned);
            Assert.Equal(1, scrubbed);
            Assert.Equal(0, dropped);
            Assert.Equal(0, cleared);
            Assert.Equal("c", tree.ActiveRecordingId);
            Assert.Equal("a", tree.RootRecordingId);
        }

        [Fact]
        public void PruneFromTree_ActiveRemoved_ResetsToSurvivingFallback()
        {
            var tree = PruneTree();
            int pruned = TreeDiscardService.PruneTaggedRecordingsFromTree(
                tree, Ids("c"), "b", out _, out _, out _);
            Assert.Equal(1, pruned);
            Assert.Equal("b", tree.ActiveRecordingId);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("reset stale tree.ActiveRecordingId from 'c' to 'b' on tree=pt"));
        }

        [Fact]
        public void PruneFromTree_FallbackNotInTree_ResetsToNull()
        {
            var tree = PruneTree();
            TreeDiscardService.PruneTaggedRecordingsFromTree(tree, Ids("c"), "c", out _, out _, out _);
            Assert.Null(tree.ActiveRecordingId);
            Assert.Contains(logLines, l => l.Contains("reset stale tree.ActiveRecordingId from 'c' to '<null>'"));
        }

        [Fact]
        public void PruneFromTree_RootRemoved_WarnsAndClearsRecordingLinks()
        {
            var tree = PruneTree();
            int pruned = TreeDiscardService.PruneTaggedRecordingsFromTree(
                tree, Ids("a"), "b", out int scrubbed, out int dropped, out int cleared);
            Assert.Equal(1, pruned);
            Assert.Equal("b", tree.RootRecordingId);
            Assert.Equal("c", tree.ActiveRecordingId);
            // bp1 lost its only parent and goes; b's parent link to it is cleared.
            Assert.Equal(1, scrubbed);
            Assert.Equal(1, dropped);
            Assert.Equal(1, cleared);
            Assert.Null(tree.Recordings["b"].ParentBranchPointId);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("reset stale tree.RootRecordingId from 'a' to 'b'"));
            Assert.Contains(logLines, l => l.Contains("dropped 1 branch point(s) with empty endpoint sets") && l.Contains("ids=[bp1]"));
        }

        [Fact]
        public void PruneFromTree_RebuildsBackgroundMap()
        {
            var tree = PruneTree();
            tree.Recordings["d"].VesselPersistentId = 42u;
            tree.RebuildBackgroundMap();
            Assert.Contains(42u, tree.RecordedVesselPids);
            TreeDiscardService.PruneTaggedRecordingsFromTree(tree, Ids("d"), null, out _, out _, out _);
            Assert.DoesNotContain(42u, tree.RecordedVesselPids);
        }

        // =====================================================================
        // TreeDiscardService: pending-only ids
        // =====================================================================

        [Fact]
        public void CollectPendingOnlyRecordingIds_KeepsCommittedOverlapAndCountsHint()
        {
            RecordingStore.AddCommittedInternal(Rec("inMemory"));
            RecordingStore.SetDurableCommittedRecordingIdHint(Ids("hinted", "inMemory"));
            var tree = Tree("p", new[] { Rec("fresh"), Rec("inMemory"), Rec("hinted"), Rec("other") });
            tree.Recordings["nullRec"] = null;
            tree.Recordings["emptyId"] = Rec("");

            var ids = TreeDiscardService.CollectPendingOnlyRecordingIds(tree, out int skipped, out int byHint);

            Assert.Equal(new[] { "fresh", "other" }, Sorted(ids));
            Assert.Equal(2, skipped);
            Assert.Equal(1, byHint);
        }

        [Fact]
        public void CollectPendingOnlyRecordingIds_NoCommittedState_AllIds()
        {
            var tree = Tree("p", new[] { Rec("x"), Rec("y") });
            var ids = TreeDiscardService.CollectPendingOnlyRecordingIds(tree, out int skipped, out int byHint);
            Assert.Equal(new[] { "x", "y" }, Sorted(ids));
            Assert.Equal(0, skipped);
            Assert.Equal(0, byHint);
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, true)]
        public void ShouldPreserveCommittedOverlapOnPendingDiscard_Cases(bool inMemory, bool hint, bool expected)
        {
            Assert.Equal(expected, TreeDiscardService.ShouldPreserveCommittedOverlapOnPendingDiscard(inMemory, hint));
        }

        // =====================================================================
        // CommittedTreeRestoreAttempt builders
        // =====================================================================

        [Fact]
        public void TreeContainsRecordingId_RequiresMatchingTreeIdAndMember()
        {
            var tree = Tree("t1", new[] { Rec("r") });
            Assert.True(CommittedTreeRestoreAttempt.TreeContainsRecordingId(tree, "t1", "r"));
            Assert.False(CommittedTreeRestoreAttempt.TreeContainsRecordingId(tree, "t2", "r"));
            Assert.False(CommittedTreeRestoreAttempt.TreeContainsRecordingId(tree, "T1", "r"));
            Assert.False(CommittedTreeRestoreAttempt.TreeContainsRecordingId(tree, "t1", "zz"));
            Assert.False(CommittedTreeRestoreAttempt.TreeContainsRecordingId(tree, "", "r"));
            Assert.False(CommittedTreeRestoreAttempt.TreeContainsRecordingId(null, "t1", "r"));
            Assert.False(CommittedTreeRestoreAttempt.TreeContainsRecordingId(
                new RecordingTree { Id = "t1", Recordings = null }, "t1", "r"));
        }

        [Fact]
        public void BuildCommittedTreeRestoreRecordingIds_RecordingOwnIdsSkippingNullAndEmpty()
        {
            var tree = Tree("t", new[] { Rec("a"), Rec("b") });
            tree.Recordings["nullRec"] = null;
            tree.Recordings["keyOnly"] = Rec("");
            Assert.Equal(new[] { "a", "b" }, Sorted(CommittedTreeRestoreAttempt.BuildCommittedTreeRestoreRecordingIds(tree)));
            Assert.Empty(CommittedTreeRestoreAttempt.BuildCommittedTreeRestoreRecordingIds(null));
        }

        [Fact]
        public void BuildCommittedTreeRestoreEventCutoffs_UsesEndUtOfBoundedRecordings()
        {
            var withPoints = Rec("pts");
            withPoints.Points.Add(new TrajectoryPoint { ut = 100.0 });
            withPoints.Points.Add(new TrajectoryPoint { ut = 250.0 });
            var explicitEnd = Rec("explicit");
            explicitEnd.ExplicitEndUT = 300.0;
            var unbounded = Rec("unbounded");
            var tree = Tree("t", new[] { withPoints, explicitEnd, unbounded });
            tree.Recordings["nullRec"] = null;

            var cutoffs = CommittedTreeRestoreAttempt.BuildCommittedTreeRestoreEventCutoffs(tree);

            Assert.Equal(2, cutoffs.Count);
            Assert.Equal(250.0, cutoffs["pts"]);
            Assert.Equal(300.0, cutoffs["explicit"]);
            Assert.False(cutoffs.ContainsKey("unbounded"));
        }

        [Fact]
        public void BuildCommittedTreeRestoreEventCutoffs_InfiniteEndSkipped()
        {
            var inf = Rec("inf");
            inf.ExplicitEndUT = double.PositiveInfinity;
            var tree = Tree("t", new[] { inf });
            Assert.Empty(CommittedTreeRestoreAttempt.BuildCommittedTreeRestoreEventCutoffs(tree));
            Assert.Empty(CommittedTreeRestoreAttempt.BuildCommittedTreeRestoreEventCutoffs(null));
        }
    }
}
