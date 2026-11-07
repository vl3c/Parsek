using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct unit tests for the TreeCommitService decision units: the committed-tree replace
    /// gate and its diff helpers, the per-cluster runtime-field preservation of a replace
    /// commit, the first-commit / staging-promotion predicates and the terminal-spawn
    /// supersede identity tests.
    /// </summary>
    [Collection("Sequential")]
    public class TreeCommitServiceUnitTests : IDisposable
    {
        private const string GuidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string GuidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private readonly List<string> logLines = new List<string>();

        public TreeCommitServiceUnitTests()
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
            return new Recording
            {
                RecordingId = id,
                ParentBranchPointId = parentBp,
                ChildBranchPointId = childBp,
            };
        }

        private static BranchPoint Bp(string id, string[] parents = null, string[] children = null)
        {
            return new BranchPoint
            {
                Id = id,
                ParentRecordingIds = new List<string>(parents ?? new string[0]),
                ChildRecordingIds = new List<string>(children ?? new string[0]),
            };
        }

        private static RecordingTree Tree(string root, string active, Recording[] recs, BranchPoint[] bps = null)
        {
            var tree = new RecordingTree
            {
                Id = "tree-1",
                RootRecordingId = root,
                ActiveRecordingId = active,
            };
            foreach (var r in recs)
                tree.Recordings[r.RecordingId] = r;
            if (bps != null)
                tree.BranchPoints.AddRange(bps);
            return tree;
        }

        /// <summary>Two structurally identical trees (separate instances): r1 -> bp1 -> r2.</summary>
        private static void TwinTrees(out RecordingTree existing, out RecordingTree incoming)
        {
            existing = Tree("r1", "r2",
                new[] { Rec("r1", childBp: "bp1"), Rec("r2", parentBp: "bp1") },
                new[] { Bp("bp1", new[] { "r1" }, new[] { "r2" }) });
            incoming = Tree("r1", "r2",
                new[] { Rec("r1", childBp: "bp1"), Rec("r2", parentBp: "bp1") },
                new[] { Bp("bp1", new[] { "r1" }, new[] { "r2" }) });
        }

        // =====================================================================
        // ShouldReplaceCommittedTree
        // =====================================================================

        [Fact]
        public void ShouldReplace_NullExisting_FalseWithNoTopologyChange()
        {
            Assert.False(TreeCommitService.ShouldReplaceCommittedTree(
                null, Tree("r1", null, new[] { Rec("r1") }), out string reason));
            Assert.Equal("no-topology-change", reason);
        }

        [Fact]
        public void ShouldReplace_NullIncoming_FalseWithNoTopologyChange()
        {
            Assert.False(TreeCommitService.ShouldReplaceCommittedTree(
                Tree("r1", null, new[] { Rec("r1") }), null, out string reason));
            Assert.Equal("no-topology-change", reason);
        }

        [Fact]
        public void ShouldReplace_IdenticalTrees_FalseWithAllFlagsClear()
        {
            TwinTrees(out var existing, out var incoming);
            Assert.False(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.Equal(
                "newRecordingIds=0 newBranchPointIds=0 rootChanged=False activeChanged=False " +
                "recordingTopologyChanged=False branchTopologyChanged=False recordingPayloadChanged=False",
                reason);
        }

        [Fact]
        public void ShouldReplace_IncomingHasFewerRecordings_RejectedAsNotRicher()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.Recordings.Remove("r2");
            Assert.False(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.Equal(
                "incoming-not-richer oldRecordings=2 newRecordings=1 oldBranchPoints=1 newBranchPoints=1",
                reason);
        }

        [Fact]
        public void ShouldReplace_IncomingHasFewerBranchPoints_RejectedAsNotRicher()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.BranchPoints.Clear();
            Assert.False(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.StartsWith("incoming-not-richer ", reason);
            Assert.Contains("oldBranchPoints=1 newBranchPoints=0", reason);
        }

        [Fact]
        public void ShouldReplace_SameCountButExistingRecordingIdMissing_Rejected()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.Recordings.Remove("r2");
            incoming.Recordings["r3"] = Rec("r3", parentBp: "bp1");
            Assert.False(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.Equal("incoming-missing-existing-ids missingRecordingIds=1 missingBranchPointIds=0", reason);
        }

        [Fact]
        public void ShouldReplace_SameCountButExistingBranchPointIdMissing_Rejected()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.BranchPoints[0] = Bp("bp9", new[] { "r1" }, new[] { "r2" });
            Assert.False(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.Equal("incoming-missing-existing-ids missingRecordingIds=0 missingBranchPointIds=1", reason);
        }

        [Fact]
        public void ShouldReplace_NewRecordingId_Replaces()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.Recordings["r3"] = Rec("r3");
            Assert.True(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.StartsWith("newRecordingIds=1 newBranchPointIds=0 ", reason);
        }

        [Fact]
        public void ShouldReplace_NewBranchPointId_Replaces()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.BranchPoints.Add(Bp("bp2"));
            Assert.True(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.StartsWith("newRecordingIds=0 newBranchPointIds=1 ", reason);
        }

        [Fact]
        public void ShouldReplace_RootChanged_Replaces()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.RootRecordingId = "r2";
            Assert.True(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.Contains("rootChanged=True activeChanged=False", reason);
        }

        [Fact]
        public void ShouldReplace_ActiveChanged_Replaces()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.ActiveRecordingId = null;
            Assert.True(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.Contains("rootChanged=False activeChanged=True", reason);
        }

        [Fact]
        public void ShouldReplace_RecordingTopologyChanged_Replaces()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.Recordings["r2"].ChildBranchPointId = "bp1";
            Assert.True(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.Contains("recordingTopologyChanged=True branchTopologyChanged=False", reason);
        }

        [Fact]
        public void ShouldReplace_BranchTopologyChanged_Replaces()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.BranchPoints[0].ChildRecordingIds.Add("r1");
            Assert.True(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.Contains("branchTopologyChanged=True recordingPayloadChanged=False", reason);
        }

        [Fact]
        public void ShouldReplace_PayloadChanged_Replaces()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.Recordings["r1"].VesselSpawned = true;
            Assert.True(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.EndsWith("recordingPayloadChanged=True", reason);
        }

        [Fact]
        public void ShouldReplace_NullCollectionsOnBothSides_NoChange()
        {
            var existing = new RecordingTree { Id = "t", Recordings = null, BranchPoints = null };
            var incoming = new RecordingTree { Id = "t", Recordings = null, BranchPoints = null };
            Assert.False(TreeCommitService.ShouldReplaceCommittedTree(existing, incoming, out string reason));
            Assert.StartsWith("newRecordingIds=0 newBranchPointIds=0 rootChanged=False", reason);
        }

        // =====================================================================
        // Count* id diffs
        // =====================================================================

        [Fact]
        public void CountRecordingIds_NewAndMissing()
        {
            var existing = Tree("a", null, new[] { Rec("a"), Rec("b") });
            var incoming = Tree("a", null, new[] { Rec("a"), Rec("c"), Rec("d") });
            Assert.Equal(2, TreeCommitService.CountNewRecordingIds(existing, incoming));
            Assert.Equal(1, TreeCommitService.CountMissingRecordingIds(existing, incoming));
        }

        [Fact]
        public void CountRecordingIds_NullSidesCountZero()
        {
            var tree = Tree("a", null, new[] { Rec("a") });
            var bare = new RecordingTree { Recordings = null };
            Assert.Equal(0, TreeCommitService.CountNewRecordingIds(null, tree));
            Assert.Equal(0, TreeCommitService.CountNewRecordingIds(tree, null));
            Assert.Equal(0, TreeCommitService.CountMissingRecordingIds(bare, tree));
            Assert.Equal(0, TreeCommitService.CountMissingRecordingIds(tree, bare));
        }

        [Fact]
        public void CountBranchPointIds_NewAndMissing_SkipNullAndEmptyIds()
        {
            var existing = Tree("a", null, new[] { Rec("a") },
                new[] { Bp("bp1"), Bp("bp2"), null, Bp("") });
            var incoming = Tree("a", null, new[] { Rec("a") },
                new[] { Bp("bp1"), Bp("bp3"), Bp("bp4"), null, Bp(null) });
            Assert.Equal(2, TreeCommitService.CountNewBranchPointIds(existing, incoming));
            Assert.Equal(1, TreeCommitService.CountMissingBranchPointIds(existing, incoming));
        }

        [Fact]
        public void CountBranchPointIds_DuplicateIncomingIdsCountEachEntry()
        {
            var existing = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1") });
            var incoming = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1"), Bp("bp2"), Bp("bp2") });
            Assert.Equal(2, TreeCommitService.CountNewBranchPointIds(existing, incoming));
        }

        [Fact]
        public void CountBranchPointIds_IdsCompareOrdinal()
        {
            var existing = Tree("a", null, new[] { Rec("a") }, new[] { Bp("BP1") });
            var incoming = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1") });
            Assert.Equal(1, TreeCommitService.CountNewBranchPointIds(existing, incoming));
            Assert.Equal(1, TreeCommitService.CountMissingBranchPointIds(existing, incoming));
        }

        [Fact]
        public void CountBranchPointIds_NullListsCountZero()
        {
            var tree = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1") });
            var bare = new RecordingTree { BranchPoints = null };
            Assert.Equal(0, TreeCommitService.CountNewBranchPointIds(bare, tree));
            Assert.Equal(0, TreeCommitService.CountMissingBranchPointIds(tree, bare));
            Assert.Equal(0, TreeCommitService.CountNewBranchPointIds(null, tree));
            Assert.Equal(0, TreeCommitService.CountMissingBranchPointIds(tree, null));
        }

        // =====================================================================
        // HasRecordingTopologyDifference / HasBranchPointTopologyDifference
        // =====================================================================

        [Fact]
        public void RecordingTopology_ParentChange_Differs()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.Recordings["r2"].ParentBranchPointId = "bpX";
            Assert.True(TreeCommitService.HasRecordingTopologyDifference(existing, incoming));
        }

        [Fact]
        public void RecordingTopology_CaseOnlyChange_Differs()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.Recordings["r1"].ChildBranchPointId = "BP1";
            Assert.True(TreeCommitService.HasRecordingTopologyDifference(existing, incoming));
        }

        [Fact]
        public void RecordingTopology_RecordingOnlyInIncomingOrNull_Ignored()
        {
            TwinTrees(out var existing, out var incoming);
            incoming.Recordings["r3"] = Rec("r3", parentBp: "anything");
            incoming.Recordings["r2"] = null;
            Assert.False(TreeCommitService.HasRecordingTopologyDifference(existing, incoming));
        }

        [Fact]
        public void RecordingTopology_NullExistingEntry_Ignored()
        {
            TwinTrees(out var existing, out var incoming);
            existing.Recordings["r2"] = null;
            incoming.Recordings["r2"].ParentBranchPointId = "changed";
            Assert.False(TreeCommitService.HasRecordingTopologyDifference(existing, incoming));
        }

        [Fact]
        public void RecordingTopology_NullTrees_NoDifference()
        {
            TwinTrees(out var existing, out _);
            Assert.False(TreeCommitService.HasRecordingTopologyDifference(existing, null));
            Assert.False(TreeCommitService.HasRecordingTopologyDifference(null, existing));
        }

        [Fact]
        public void BranchTopology_ReorderedEndpoints_NoDifference()
        {
            var existing = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1", new[] { "a", "b" }, new[] { "c", "d" }) });
            var incoming = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1", new[] { "b", "a" }, new[] { "d", "c" }) });
            Assert.False(TreeCommitService.HasBranchPointTopologyDifference(existing, incoming));
        }

        [Fact]
        public void BranchTopology_ChangedParents_Differs()
        {
            var existing = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1", new[] { "a" }, new[] { "c" }) });
            var incoming = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1", new[] { "b" }, new[] { "c" }) });
            Assert.True(TreeCommitService.HasBranchPointTopologyDifference(existing, incoming));
        }

        [Fact]
        public void BranchTopology_NewBranchPointInIncoming_Ignored()
        {
            var existing = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1", new[] { "a" }) });
            var incoming = Tree("a", null, new[] { Rec("a") },
                new[] { Bp("bp1", new[] { "a" }), Bp("bp2", new[] { "z" }), null, Bp("") });
            Assert.False(TreeCommitService.HasBranchPointTopologyDifference(existing, incoming));
        }

        [Fact]
        public void BranchTopology_DuplicateExistingId_FirstEntryIsCompared()
        {
            var existing = Tree("a", null, new[] { Rec("a") },
                new[] { Bp("bp1", new[] { "a" }), Bp("bp1", new[] { "other" }) });
            var sameAsFirst = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1", new[] { "a" }) });
            var sameAsSecond = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1", new[] { "other" }) });
            Assert.False(TreeCommitService.HasBranchPointTopologyDifference(existing, sameAsFirst));
            Assert.True(TreeCommitService.HasBranchPointTopologyDifference(existing, sameAsSecond));
        }

        [Fact]
        public void BranchTopology_NullLists_NoDifference()
        {
            var tree = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1") });
            Assert.False(TreeCommitService.HasBranchPointTopologyDifference(new RecordingTree { BranchPoints = null }, tree));
            Assert.False(TreeCommitService.HasBranchPointTopologyDifference(tree, null));
        }

        // =====================================================================
        // SameStringSet / SameDouble
        // =====================================================================

        public static IEnumerable<object[]> StringSetCases()
        {
            yield return new object[] { null, null, true };
            yield return new object[] { null, new string[0], true };
            yield return new object[] { new string[0], null, true };
            yield return new object[] { null, new[] { "a" }, false };
            yield return new object[] { new[] { "a" }, null, false };
            yield return new object[] { new[] { "a", "b" }, new[] { "b", "a" }, true };
            yield return new object[] { new[] { "a" }, new[] { "a", "b" }, false };
            yield return new object[] { new[] { "a", "a" }, new[] { "a", "b" }, false };
            yield return new object[] { new[] { "a", "b" }, new[] { "a", "a" }, false };
            yield return new object[] { new[] { "a", "a" }, new[] { "a", "a" }, true };
            yield return new object[] { new[] { "a" }, new[] { "A" }, false };
        }

        [Theory]
        [MemberData(nameof(StringSetCases))]
        public void SameStringSet_Cases(string[] left, string[] right, bool expected)
        {
            Assert.Equal(expected, TreeCommitService.SameStringSet(
                left == null ? null : new List<string>(left),
                right == null ? null : new List<string>(right)));
        }

        [Theory]
        [InlineData(double.NaN, double.NaN, true)]
        [InlineData(double.NaN, 0.0, false)]
        [InlineData(1.5, 1.5, true)]
        [InlineData(1.5, 1.5000001, false)]
        [InlineData(0.0, -0.0, true)]
        [InlineData(double.PositiveInfinity, double.PositiveInfinity, true)]
        public void SameDouble_Cases(double left, double right, bool expected)
        {
            Assert.Equal(expected, TreeCommitService.SameDouble(left, right));
        }

        // =====================================================================
        // HasRecordingPayloadDifference (per recording)
        // =====================================================================

        public static IEnumerable<object[]> PayloadMutations()
        {
            yield return new object[] { "Points", (Action<Recording>)(r => r.Points.Add(new TrajectoryPoint())) };
            yield return new object[] { "OrbitSegments", (Action<Recording>)(r => r.OrbitSegments.Add(new OrbitSegment())) };
            yield return new object[] { "PartEvents", (Action<Recording>)(r => r.PartEvents.Add(new PartEvent())) };
            yield return new object[] { "FlagEvents", (Action<Recording>)(r => r.FlagEvents.Add(new FlagEvent())) };
            yield return new object[] { "SegmentEvents", (Action<Recording>)(r => r.SegmentEvents.Add(new SegmentEvent())) };
            yield return new object[] { "TrackSections", (Action<Recording>)(r => r.TrackSections.Add(new TrackSection())) };
            yield return new object[] { "StartUT", (Action<Recording>)(r => r.ExplicitStartUT = 12.0) };
            yield return new object[] { "EndUT", (Action<Recording>)(r => r.ExplicitEndUT = 34.0) };
            yield return new object[] { "VesselPersistentId", (Action<Recording>)(r => r.VesselPersistentId = 7u) };
            yield return new object[] { "SpawnedVesselPersistentId", (Action<Recording>)(r => r.SpawnedVesselPersistentId = 8u) };
            yield return new object[] { "VesselSpawned", (Action<Recording>)(r => r.VesselSpawned = true) };
            yield return new object[] { "TerminalStateValue", (Action<Recording>)(r => r.TerminalStateValue = TerminalState.Landed) };
            yield return new object[] { "EndpointPhase", (Action<Recording>)(r => r.EndpointPhase = RecordingEndpointPhase.OrbitSegment) };
            yield return new object[] { "EndpointBodyName", (Action<Recording>)(r => r.EndpointBodyName = "Mun") };
            yield return new object[] { "TerminalOrbitBody", (Action<Recording>)(r => r.TerminalOrbitBody = "Minmus") };
        }

        [Theory]
        [MemberData(nameof(PayloadMutations))]
        public void RecordingPayload_EachComparedFieldDiffers(string field, Action<Recording> mutate)
        {
            var existing = Rec("r");
            var incoming = Rec("r");
            Assert.False(TreeCommitService.HasRecordingPayloadDifference(existing, incoming));
            mutate(incoming);
            Assert.True(TreeCommitService.HasRecordingPayloadDifference(existing, incoming), field);
        }

        [Fact]
        public void RecordingPayload_NullListEqualsEmptyList()
        {
            var existing = Rec("r");
            var incoming = Rec("r");
            incoming.Points = null;
            incoming.PartEvents = null;
            incoming.TrackSections = null;
            Assert.False(TreeCommitService.HasRecordingPayloadDifference(existing, incoming));
        }

        [Fact]
        public void RecordingPayload_UncomparedFieldsDoNotDiffer()
        {
            var existing = Rec("r");
            var incoming = Rec("r");
            incoming.VesselName = "Renamed";
            incoming.SpawnAttempts = 3;
            incoming.RewindSaveFileName = "save";
            Assert.False(TreeCommitService.HasRecordingPayloadDifference(existing, incoming));
        }

        [Fact]
        public void TreePayload_OnlySharedNonNullRecordingsCompared()
        {
            TwinTrees(out var existing, out var incoming);
            var extra = Rec("r3");
            extra.VesselSpawned = true;
            incoming.Recordings["r3"] = extra;
            Assert.False(TreeCommitService.HasRecordingPayloadDifference(existing, incoming));

            existing.Recordings["r1"] = null;
            incoming.Recordings["r1"].VesselSpawned = true;
            Assert.False(TreeCommitService.HasRecordingPayloadDifference(existing, incoming));

            incoming.Recordings["r2"].EndpointBodyName = "Duna";
            Assert.True(TreeCommitService.HasRecordingPayloadDifference(existing, incoming));
        }

        // =====================================================================
        // FindBranchPointById / FindCommittedRecordingIndex / GetTreeStartUT
        // =====================================================================

        [Fact]
        public void FindBranchPointById_MatchesOrdinalAndSkipsNulls()
        {
            var bp = Bp("bp1");
            var tree = Tree("a", null, new[] { Rec("a") }, new[] { null, Bp("BP1"), bp });
            Assert.Same(bp, TreeCommitService.FindBranchPointById(tree, "bp1"));
            Assert.Null(TreeCommitService.FindBranchPointById(tree, "bp2"));
            Assert.Null(TreeCommitService.FindBranchPointById(tree, ""));
            Assert.Null(TreeCommitService.FindBranchPointById(tree, null));
            Assert.Null(TreeCommitService.FindBranchPointById(null, "bp1"));
            Assert.Null(TreeCommitService.FindBranchPointById(new RecordingTree { BranchPoints = null }, "bp1"));
        }

        [Fact]
        public void FindCommittedRecordingIndex_IdMatchWinsOverReference()
        {
            var a = Rec("a");
            var b = Rec("b");
            RecordingStore.AddCommittedInternal(a);
            RecordingStore.AddCommittedInternal(b);

            // A different instance carrying b's id resolves to b's slot by id.
            Assert.Equal(1, TreeCommitService.FindCommittedRecordingIndex(Rec("b")));
            Assert.Equal(0, TreeCommitService.FindCommittedRecordingIndex(a));
            Assert.Equal(-1, TreeCommitService.FindCommittedRecordingIndex(Rec("zzz")));
            Assert.Equal(-1, TreeCommitService.FindCommittedRecordingIndex(null));
        }

        [Fact]
        public void FindCommittedRecordingIndex_NoIdFallsBackToReference()
        {
            var a = Rec("a");
            var anonymous = Rec(null);
            RecordingStore.AddCommittedInternal(a);
            RecordingStore.AddCommittedInternal(anonymous);
            Assert.Equal(1, TreeCommitService.FindCommittedRecordingIndex(anonymous));
            Assert.Equal(-1, TreeCommitService.FindCommittedRecordingIndex(Rec(null)));
        }

        [Fact]
        public void GetTreeStartUT_MinimumOverRecordingsSkippingNulls()
        {
            var early = Rec("a");
            early.ExplicitStartUT = 50.0;
            var late = Rec("b");
            late.ExplicitStartUT = 80.0;
            var tree = Tree("a", null, new[] { late, early });
            tree.Recordings["c"] = null;
            Assert.Equal(50.0, TreeCommitService.GetTreeStartUT(tree));
        }

        [Fact]
        public void GetTreeStartUT_NoUsableRecordings_NaN()
        {
            Assert.True(double.IsNaN(TreeCommitService.GetTreeStartUT(null)));
            Assert.True(double.IsNaN(TreeCommitService.GetTreeStartUT(new RecordingTree())));
            var onlyNull = new RecordingTree();
            onlyNull.Recordings["x"] = null;
            Assert.True(double.IsNaN(TreeCommitService.GetTreeStartUT(onlyNull)));
        }

        // =====================================================================
        // CollectCommittedTreeRecordingIds / CopyAutoGeneratedGroupNames
        // =====================================================================

        [Fact]
        public void CollectCommittedTreeRecordingIds_UnionSkippingNullTreesAndEmptyIds()
        {
            var t1 = Tree("a", null, new[] { Rec("a"), Rec("b") });
            var t2 = Tree("c", null, new[] { Rec("c") });
            t2.Recordings[""] = Rec("");
            var t3 = new RecordingTree { Recordings = null };
            var ids = TreeCommitService.CollectCommittedTreeRecordingIds(
                new List<RecordingTree> { t1, null, t2, t3 });
            Assert.Equal(new[] { "a", "b", "c" }, Sorted(ids));
        }

        [Fact]
        public void CollectCommittedTreeRecordingIds_EmptyList_EmptySet()
        {
            Assert.Empty(TreeCommitService.CollectCommittedTreeRecordingIds(new List<RecordingTree>()));
        }

        [Fact]
        public void CopyAutoGeneratedGroupNames_CopiesAllThreeIncludingNull()
        {
            var existing = new RecordingTree
            {
                AutoGeneratedRootGroupName = "Kerbal X",
                AutoGeneratedDebrisGroupName = "Kerbal X / Debris",
                AutoGeneratedCrewGroupName = null,
            };
            var incoming = new RecordingTree
            {
                AutoGeneratedRootGroupName = "other",
                AutoGeneratedDebrisGroupName = "other / Debris",
                AutoGeneratedCrewGroupName = "other / Crew",
            };
            TreeCommitService.CopyAutoGeneratedGroupNames(existing, incoming);
            Assert.Equal("Kerbal X", incoming.AutoGeneratedRootGroupName);
            Assert.Equal("Kerbal X / Debris", incoming.AutoGeneratedDebrisGroupName);
            Assert.Null(incoming.AutoGeneratedCrewGroupName);
        }

        // =====================================================================
        // CollectSupersedeForkIds / IsFirstCommitForPromotion
        // =====================================================================

        [Fact]
        public void CollectSupersedeForkIds_TakesNewIdsSkippingNullRowsAndEmptyIds()
        {
            var rows = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation { OldRecordingId = "old1", NewRecordingId = "fork1" },
                null,
                new RecordingSupersedeRelation { OldRecordingId = "old2", NewRecordingId = "" },
                new RecordingSupersedeRelation { OldRecordingId = "old3", NewRecordingId = "fork3" },
            };
            var forks = TreeCommitService.CollectSupersedeForkIds(rows);
            Assert.Equal(new[] { "fork1", "fork3" }, Sorted(forks));
            Assert.DoesNotContain("old1", forks);
        }

        [Theory]
        [InlineData("fresh", true)]
        [InlineData("committed", false)]
        [InlineData("fork", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("Committed", true)]
        public void IsFirstCommitForPromotion_Cases(string id, bool expected)
        {
            var committed = new HashSet<string>(StringComparer.Ordinal) { "committed" };
            var forks = new HashSet<string>(StringComparer.Ordinal) { "fork" };
            Assert.Equal(expected, TreeCommitService.IsFirstCommitForPromotion(id, committed, forks));
        }

        // =====================================================================
        // CollectBranchPointRewindPointIds / IsNormalStagingRewindPointOfTree
        // =====================================================================

        [Fact]
        public void CollectBranchPointRewindPointIds_NoneNamed_Null()
        {
            var tree = Tree("a", null, new[] { Rec("a") }, new[] { Bp("bp1"), null });
            Assert.Null(TreeCommitService.CollectBranchPointRewindPointIds(tree));
        }

        [Fact]
        public void CollectBranchPointRewindPointIds_DistinctNamedIds()
        {
            var b1 = Bp("bp1"); b1.RewindPointId = "rp1";
            var b2 = Bp("bp2"); b2.RewindPointId = "rp2";
            var b3 = Bp("bp3"); b3.RewindPointId = "rp1";
            var b4 = Bp("bp4"); b4.RewindPointId = "";
            var tree = Tree("a", null, new[] { Rec("a") }, new[] { b1, null, b2, b3, b4 });
            Assert.Equal(new[] { "rp1", "rp2" }, Sorted(TreeCommitService.CollectBranchPointRewindPointIds(tree)));
        }

        [Fact]
        public void IsNormalStagingRewindPointOfTree_PromotesOnlyOwnedSessionlessProvisional()
        {
            var treeRpIds = new HashSet<string>(StringComparer.Ordinal) { "rp1" };
            Assert.True(TreeCommitService.IsNormalStagingRewindPointOfTree(
                new RewindPoint { RewindPointId = "rp1", SessionProvisional = true }, treeRpIds));

            Assert.False(TreeCommitService.IsNormalStagingRewindPointOfTree(null, treeRpIds));
            Assert.False(TreeCommitService.IsNormalStagingRewindPointOfTree(
                new RewindPoint { RewindPointId = "rp1", SessionProvisional = false }, treeRpIds));
            Assert.False(TreeCommitService.IsNormalStagingRewindPointOfTree(
                new RewindPoint { RewindPointId = "rp1", SessionProvisional = true, CreatingSessionId = "sess" },
                treeRpIds));
            Assert.False(TreeCommitService.IsNormalStagingRewindPointOfTree(
                new RewindPoint { RewindPointId = "", SessionProvisional = true }, treeRpIds));
            Assert.False(TreeCommitService.IsNormalStagingRewindPointOfTree(
                new RewindPoint { RewindPointId = "rp2", SessionProvisional = true }, treeRpIds));
        }

        // =====================================================================
        // PreserveLiveRuntimeFieldsOnReplace clusters
        // =====================================================================

        [Fact]
        public void PreserveRewindLaunchFields_CopiesDefaultsAndCountsLedgerFields()
        {
            var existing = new Recording
            {
                RewindSaveFileName = "parsek_rw_1",
                RewindReservedFunds = 100.0,
                RewindReservedScience = 5.0,
                RewindReservedRep = 2.5f,
            };
            var incoming = new Recording();
            int other = 0;
            Assert.True(TreeCommitService.PreserveRewindLaunchFieldsOnReplace(existing, incoming, ref other));
            Assert.Equal(3, other);
            Assert.Equal("parsek_rw_1", incoming.RewindSaveFileName);
            Assert.Equal(100.0, incoming.RewindReservedFunds);
            Assert.Equal(5.0, incoming.RewindReservedScience);
            Assert.Equal(2.5f, incoming.RewindReservedRep);
        }

        [Fact]
        public void PreserveRewindLaunchFields_IncomingValuesWin()
        {
            var existing = new Recording
            {
                RewindSaveFileName = "old",
                RewindReservedFunds = 100.0,
                RewindReservedScience = 5.0,
                RewindReservedRep = 2.5f,
            };
            var incoming = new Recording
            {
                RewindSaveFileName = "new",
                RewindReservedFunds = -1.0,
                RewindReservedScience = 9.0,
                RewindReservedRep = -3f,
            };
            int other = 4;
            Assert.False(TreeCommitService.PreserveRewindLaunchFieldsOnReplace(existing, incoming, ref other));
            Assert.Equal(4, other);
            Assert.Equal("new", incoming.RewindSaveFileName);
            Assert.Equal(-1.0, incoming.RewindReservedFunds);
            Assert.Equal(9.0, incoming.RewindReservedScience);
            Assert.Equal(-3f, incoming.RewindReservedRep);
        }

        [Fact]
        public void PreserveRewindLaunchFields_NothingToCopy_NoChange()
        {
            var incoming = new Recording();
            int other = 0;
            Assert.False(TreeCommitService.PreserveRewindLaunchFieldsOnReplace(new Recording(), incoming, ref other));
            Assert.Equal(0, other);
            Assert.Null(incoming.RewindSaveFileName);
        }

        public static IEnumerable<object[]> SpawnCounterFields()
        {
            yield return new object[] { "TerminalSpawnSupersededByRecordingId",
                (Action<Recording>)(r => r.TerminalSpawnSupersededByRecordingId = "cont"),
                (Func<Recording, object>)(r => r.TerminalSpawnSupersededByRecordingId) };
            yield return new object[] { "SpawnAttempts",
                (Action<Recording>)(r => r.SpawnAttempts = 2), (Func<Recording, object>)(r => r.SpawnAttempts) };
            yield return new object[] { "CollisionBlockCount",
                (Action<Recording>)(r => r.CollisionBlockCount = 7), (Func<Recording, object>)(r => r.CollisionBlockCount) };
            yield return new object[] { "SpawnAbandoned",
                (Action<Recording>)(r => r.SpawnAbandoned = true), (Func<Recording, object>)(r => r.SpawnAbandoned) };
            yield return new object[] { "WalkbackExhausted",
                (Action<Recording>)(r => r.WalkbackExhausted = true), (Func<Recording, object>)(r => r.WalkbackExhausted) };
            yield return new object[] { "DuplicateBlockerRecovered",
                (Action<Recording>)(r => r.DuplicateBlockerRecovered = true),
                (Func<Recording, object>)(r => r.DuplicateBlockerRecovered) };
            yield return new object[] { "SpawnDeathCount",
                (Action<Recording>)(r => r.SpawnDeathCount = 1), (Func<Recording, object>)(r => r.SpawnDeathCount) };
        }

        [Theory]
        [MemberData(nameof(SpawnCounterFields))]
        public void PreserveSpawnCounters_CopiesEachFieldOnlyAtDefault(
            string field, Action<Recording> set, Func<Recording, object> get)
        {
            var existing = new Recording();
            set(existing);
            var incoming = new Recording();
            int other = 0;
            TreeCommitService.PreserveSpawnCountersOnReplace(existing, incoming, ref other);
            Assert.Equal(1, other);
            Assert.Equal(get(existing), get(incoming));

            // A second pass finds the field already set and copies nothing.
            TreeCommitService.PreserveSpawnCountersOnReplace(existing, incoming, ref other);
            Assert.True(other == 1, field);
        }

        [Fact]
        public void PreserveSpawnCounters_NonDefaultIncomingIsKept()
        {
            var existing = new Recording { SpawnAttempts = 2, TerminalSpawnSupersededByRecordingId = "old" };
            var incoming = new Recording { SpawnAttempts = 5, TerminalSpawnSupersededByRecordingId = "new" };
            int other = 0;
            TreeCommitService.PreserveSpawnCountersOnReplace(existing, incoming, ref other);
            Assert.Equal(0, other);
            Assert.Equal(5, incoming.SpawnAttempts);
            Assert.Equal("new", incoming.TerminalSpawnSupersededByRecordingId);
        }

        public static IEnumerable<object[]> TerminalSafetyFields()
        {
            yield return new object[] { "TerminalSpawnSafetyDeferred",
                (Action<Recording>)(r => r.TerminalSpawnSafetyDeferred = true),
                (Func<Recording, object>)(r => r.TerminalSpawnSafetyDeferred) };
            yield return new object[] { "TerminalSpawnCannotSpawnSafely",
                (Action<Recording>)(r => r.TerminalSpawnCannotSpawnSafely = true),
                (Func<Recording, object>)(r => r.TerminalSpawnCannotSpawnSafely) };
            yield return new object[] { "TerminalSpawnSafetyReasonCode",
                (Action<Recording>)(r => r.TerminalSpawnSafetyReasonCode = "code"),
                (Func<Recording, object>)(r => r.TerminalSpawnSafetyReasonCode) };
            yield return new object[] { "TerminalSpawnSafetyReason",
                (Action<Recording>)(r => r.TerminalSpawnSafetyReason = "why"),
                (Func<Recording, object>)(r => r.TerminalSpawnSafetyReason) };
            yield return new object[] { "TerminalSpawnSafetyDecisionUT",
                (Action<Recording>)(r => r.TerminalSpawnSafetyDecisionUT = 0.0),
                (Func<Recording, object>)(r => r.TerminalSpawnSafetyDecisionUT) };
            yield return new object[] { "TerminalSpawnNextAttemptUT",
                (Action<Recording>)(r => r.TerminalSpawnNextAttemptUT = 42.0),
                (Func<Recording, object>)(r => r.TerminalSpawnNextAttemptUT) };
        }

        [Theory]
        [MemberData(nameof(TerminalSafetyFields))]
        public void PreserveTerminalSpawnSafety_CopiesEachFieldOnlyAtDefault(
            string field, Action<Recording> set, Func<Recording, object> get)
        {
            var existing = new Recording();
            set(existing);
            var incoming = new Recording();
            int other = 0;
            TreeCommitService.PreserveTerminalSpawnSafetyOnReplace(existing, incoming, ref other);
            Assert.Equal(1, other);
            Assert.Equal(get(existing), get(incoming));
            TreeCommitService.PreserveTerminalSpawnSafetyOnReplace(existing, incoming, ref other);
            Assert.True(other == 1, field);
        }

        [Fact]
        public void PreserveTerminalSpawnSafety_ZeroUtIsAValueNotADefault()
        {
            // NaN is the unset marker for the UT fields; 0 on the replacement is kept.
            var existing = new Recording { TerminalSpawnSafetyDecisionUT = 10.0, TerminalSpawnNextAttemptUT = 20.0 };
            var incoming = new Recording { TerminalSpawnSafetyDecisionUT = 0.0, TerminalSpawnNextAttemptUT = 0.0 };
            int other = 0;
            TreeCommitService.PreserveTerminalSpawnSafetyOnReplace(existing, incoming, ref other);
            Assert.Equal(0, other);
            Assert.Equal(0.0, incoming.TerminalSpawnSafetyDecisionUT);
            Assert.Equal(0.0, incoming.TerminalSpawnNextAttemptUT);
        }

        [Fact]
        public void PreserveRewindSuppression_CopiesAllThreeFields()
        {
            var existing = new Recording
            {
                SpawnSuppressedByRewind = true,
                SpawnSuppressedByRewindReason = "rewind-to-launch",
                SpawnSuppressedByRewindUT = 17.0,
            };
            var incoming = new Recording();
            int other = 0;
            TreeCommitService.PreserveRewindSuppressionOnReplace(existing, incoming, ref other);
            Assert.Equal(3, other);
            Assert.True(incoming.SpawnSuppressedByRewind);
            Assert.Equal("rewind-to-launch", incoming.SpawnSuppressedByRewindReason);
            Assert.Equal(17.0, incoming.SpawnSuppressedByRewindUT);
        }

        [Fact]
        public void PreserveRewindSuppression_NaNExistingUtIsNotCopied()
        {
            var existing = new Recording { SpawnSuppressedByRewindUT = double.NaN };
            var incoming = new Recording();
            int other = 0;
            TreeCommitService.PreserveRewindSuppressionOnReplace(existing, incoming, ref other);
            Assert.Equal(0, other);
            Assert.True(double.IsNaN(incoming.SpawnSuppressedByRewindUT));
        }

        [Fact]
        public void PreserveFinalStateDiagnostics_LiveIncomingTakesEverything()
        {
            var existing = new Recording
            {
                SceneExitSituation = 2,
                MaxDistanceFromLaunch = 9000.0,
                DistanceFromLaunch = 1200.0,
                VesselDestroyed = true,
                VesselSituation = "Landed on Mun",
            };
            var incoming = new Recording();
            int other = 0;
            TreeCommitService.PreserveFinalStateDiagnosticsOnReplace(existing, incoming, ref other);

            // Pins today's field order: the destroyed flag copies after the situation int and
            // before the situation string, so the int carries over and the string does not.
            // Unreachable in play (MarkDestroyedAtTerminal clears both on the destroyed side).
            Assert.Equal(4, other);
            Assert.Equal(2, incoming.SceneExitSituation);
            Assert.Equal(9000.0, incoming.MaxDistanceFromLaunch);
            Assert.Equal(1200.0, incoming.DistanceFromLaunch);
            Assert.True(incoming.VesselDestroyed);
            Assert.Null(incoming.VesselSituation);
        }

        [Fact]
        public void PreserveFinalStateDiagnostics_DestroyedIncomingKeepsClearedSituations()
        {
            var existing = new Recording { SceneExitSituation = 2, VesselSituation = "Orbiting Kerbin" };
            var incoming = new Recording { VesselDestroyed = true };
            int other = 0;
            TreeCommitService.PreserveFinalStateDiagnosticsOnReplace(existing, incoming, ref other);
            Assert.Equal(0, other);
            Assert.Equal(-1, incoming.SceneExitSituation);
            Assert.Null(incoming.VesselSituation);
        }

        [Fact]
        public void PreserveFinalStateDiagnostics_AliveOnBothSidesCopiesSituationString()
        {
            var existing = new Recording { VesselSituation = "Orbiting Kerbin" };
            var incoming = new Recording();
            int other = 0;
            TreeCommitService.PreserveFinalStateDiagnosticsOnReplace(existing, incoming, ref other);
            Assert.Equal(1, other);
            Assert.Equal("Orbiting Kerbin", incoming.VesselSituation);
        }

        // --- spawn claim ---

        private static Recording SpawnedExisting(uint spawnPid = 500u)
        {
            return new Recording
            {
                RecordingId = "old",
                VesselName = "Rover",
                VesselPersistentId = 100u,
                VesselSpawned = true,
                SpawnedVesselPersistentId = spawnPid,
            };
        }

        [Fact]
        public void PreserveSpawnClaim_LiveVessel_CopiesBothFields()
        {
            var existing = SpawnedExisting();
            var incoming = new Recording { RecordingId = "new", VesselName = "Rover" };
            int other = 0;
            TreeCommitService.PreserveSpawnClaimOnReplace(
                existing, incoming, new HashSet<uint> { 500u }, null, ref other);
            Assert.Equal(2, other);
            Assert.True(incoming.VesselSpawned);
            Assert.Equal(500u, incoming.SpawnedVesselPersistentId);
            Assert.DoesNotContain(logLines, l => l.Contains("PreserveLiveRuntimeFieldsOnReplace"));
        }

        [Fact]
        public void PreserveSpawnClaim_UnknownLiveness_CopiesAsBefore()
        {
            var existing = SpawnedExisting();
            var incoming = new Recording();
            int other = 0;
            TreeCommitService.PreserveSpawnClaimOnReplace(existing, incoming, null, null, ref other);
            Assert.Equal(2, other);
            Assert.Equal(500u, incoming.SpawnedVesselPersistentId);
        }

        [Fact]
        public void PreserveSpawnClaim_VesselGone_DropsAndLogs()
        {
            var existing = SpawnedExisting();
            var incoming = new Recording { RecordingId = "new", VesselName = "Rover" };
            int other = 0;
            TreeCommitService.PreserveSpawnClaimOnReplace(
                existing, incoming, new HashSet<uint> { 999u }, null, ref other);
            Assert.Equal(0, other);
            Assert.False(incoming.VesselSpawned);
            Assert.Equal(0u, incoming.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("PreserveLiveRuntimeFieldsOnReplace: dropped stale spawn stamp pid=500")
                && l.Contains("(vessel no longer live)"));
        }

        [Theory]
        [InlineData(TerminalState.Recovered)]
        [InlineData(TerminalState.Destroyed)]
        [InlineData(TerminalState.Disassembled)]
        public void PreserveSpawnClaim_VessellessTerminal_NeverInheritsAndLogs(TerminalState terminal)
        {
            var existing = SpawnedExisting();
            var incoming = new Recording { RecordingId = "new", VesselName = "Rover", TerminalStateValue = terminal };
            int other = 0;
            TreeCommitService.PreserveSpawnClaimOnReplace(
                existing, incoming, new HashSet<uint> { 500u }, null, ref other);
            Assert.Equal(0, other);
            Assert.False(incoming.VesselSpawned);
            Assert.Equal(0u, incoming.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l => l.Contains("PreserveLiveRuntimeFieldsOnReplace: spawn stamp pid=500 not re-installed")
                && l.Contains("leaves no vessel"));
        }

        [Fact]
        public void PreserveSpawnClaim_IncomingAlreadyClaimed_NoLogNoCopy()
        {
            var existing = SpawnedExisting();
            var incoming = new Recording { VesselSpawned = true, SpawnedVesselPersistentId = 777u };
            int other = 0;
            TreeCommitService.PreserveSpawnClaimOnReplace(
                existing, incoming, new HashSet<uint> { 999u }, null, ref other);
            Assert.Equal(0, other);
            Assert.Equal(777u, incoming.SpawnedVesselPersistentId);
            Assert.DoesNotContain(logLines, l => l.Contains("dropped stale spawn stamp"));
        }

        [Fact]
        public void PreserveSpawnClaim_OnlyFlagMissing_CopiesFlagOnly()
        {
            var existing = SpawnedExisting();
            var incoming = new Recording { SpawnedVesselPersistentId = 500u };
            int other = 0;
            TreeCommitService.PreserveSpawnClaimOnReplace(
                existing, incoming, new HashSet<uint> { 500u }, null, ref other);
            Assert.Equal(1, other);
            Assert.True(incoming.VesselSpawned);
        }

        // --- the composed method ---

        [Fact]
        public void PreserveLiveRuntimeFields_AllClustersCompose()
        {
            var existing = SpawnedExisting();
            existing.RewindSaveFileName = "rw";
            existing.RewindReservedFunds = 1.0;
            existing.SidecarEpochAdvancePending = true;
            existing.LastAppliedResourceIndex = 4;
            existing.SpawnAttempts = 1;
            existing.TerminalSpawnSafetyReason = "why";
            existing.SpawnSuppressedByRewind = true;
            existing.MaxDistanceFromLaunch = 10.0;
            var incoming = new Recording { RecordingId = "new" };

            TreeCommitService.PreserveLiveRuntimeFieldsOnReplace(
                existing, incoming, out bool savePreserved, out int otherPreserved,
                new HashSet<uint> { 500u });

            Assert.True(savePreserved);
            // funds, cursor, spawned flag, spawned pid, attempts, reason, suppression, distance;
            // the epoch flag carries over without counting.
            Assert.Equal(8, otherPreserved);
            Assert.True(incoming.SidecarEpochAdvancePending);
            Assert.Equal(4, incoming.LastAppliedResourceIndex);
            Assert.Equal(500u, incoming.SpawnedVesselPersistentId);
            Assert.Equal(10.0, incoming.MaxDistanceFromLaunch);
        }

        [Fact]
        public void PreserveLiveRuntimeFields_NullSides_NothingPreserved()
        {
            TreeCommitService.PreserveLiveRuntimeFieldsOnReplace(
                null, new Recording(), out bool save1, out int other1);
            TreeCommitService.PreserveLiveRuntimeFieldsOnReplace(
                SpawnedExisting(), null, out bool save2, out int other2);
            Assert.False(save1);
            Assert.False(save2);
            Assert.Equal(0, other1);
            Assert.Equal(0, other2);
        }

        [Fact]
        public void PreserveLiveRuntimeFields_IncomingCursorKept()
        {
            var existing = new Recording { LastAppliedResourceIndex = 4 };
            var incoming = new Recording { LastAppliedResourceIndex = 0 };
            TreeCommitService.PreserveLiveRuntimeFieldsOnReplace(existing, incoming, out _, out int other);
            Assert.Equal(0, other);
            Assert.Equal(0, incoming.LastAppliedResourceIndex);
        }

        // =====================================================================
        // IsDockMergeAbsorbedLeaf
        // =====================================================================

        private static Recording Leaf(string id, uint recordedPid, uint spawnPid, string guid = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = recordedPid,
                SpawnedVesselPersistentId = spawnPid,
                RecordedVesselGuid = guid,
            };
        }

        [Fact]
        public void DockMergeLeaf_UniqueSpawnPid_MatchesPidOnly()
        {
            var prior = Leaf("p", recordedPid: 100u, spawnPid: 555u, guid: GuidA);
            Assert.True(TreeCommitService.IsDockMergeAbsorbedLeaf(prior, 555u, GuidB, "merged", out bool unique));
            Assert.True(unique);
        }

        [Fact]
        public void DockMergeLeaf_BakedPidWithUnknownGuid_Matches()
        {
            var prior = Leaf("p", recordedPid: 100u, spawnPid: 0u, guid: GuidA);
            Assert.True(TreeCommitService.IsDockMergeAbsorbedLeaf(prior, 100u, null, "merged", out bool unique));
            Assert.False(unique);
        }

        [Fact]
        public void DockMergeLeaf_BakedPidWithSameGuid_Matches()
        {
            var prior = Leaf("p", recordedPid: 100u, spawnPid: 0u, guid: GuidA);
            Assert.True(TreeCommitService.IsDockMergeAbsorbedLeaf(
                prior, 100u, GuidA.ToUpperInvariant(), "merged", out _));
        }

        [Fact]
        public void DockMergeLeaf_BakedPidWithDifferentGuid_Rejected()
        {
            var prior = Leaf("p", recordedPid: 100u, spawnPid: 0u, guid: GuidA);
            Assert.False(TreeCommitService.IsDockMergeAbsorbedLeaf(prior, 100u, GuidB, "merged", out _));
        }

        [Fact]
        public void DockMergeLeaf_AdoptionStampIsBakedNotUnique_GuidGated()
        {
            // spawn pid == recorded pid: not a unique spawn, so the guid gate applies.
            var prior = Leaf("p", recordedPid: 100u, spawnPid: 100u, guid: GuidA);
            Assert.False(TreeCommitService.IsDockMergeAbsorbedLeaf(prior, 100u, GuidB, "merged", out bool unique));
            Assert.False(unique);
            Assert.True(TreeCommitService.IsDockMergeAbsorbedLeaf(prior, 100u, GuidA, "merged", out unique));
            Assert.False(unique);
        }

        [Fact]
        public void DockMergeLeaf_SkipsIneligiblePriors()
        {
            Assert.False(TreeCommitService.IsDockMergeAbsorbedLeaf(null, 100u, null, "merged", out _));
            Assert.False(TreeCommitService.IsDockMergeAbsorbedLeaf(Leaf("", 100u, 0u), 100u, null, "merged", out _));
            Assert.False(TreeCommitService.IsDockMergeAbsorbedLeaf(Leaf("merged", 100u, 0u), 100u, null, "merged", out _));
            var superseded = Leaf("p", 100u, 0u);
            superseded.TerminalSpawnSupersededByRecordingId = "someone";
            Assert.False(TreeCommitService.IsDockMergeAbsorbedLeaf(superseded, 100u, null, "merged", out _));
        }

        [Fact]
        public void DockMergeLeaf_NoPidMatch_Rejected()
        {
            Assert.False(TreeCommitService.IsDockMergeAbsorbedLeaf(Leaf("p", 100u, 555u), 200u, null, "merged", out bool unique));
            Assert.False(unique);
            Assert.False(TreeCommitService.IsDockMergeAbsorbedLeaf(Leaf("p", 0u, 0u), 0u, null, "merged", out _));
        }

        [Fact]
        public void MarkTerminalSpawnSupersededByDockMerge_MarksAndClearsSpawnState()
        {
            var prior = Leaf("p", recordedPid: 100u, spawnPid: 555u);
            prior.VesselSpawned = true;
            prior.VesselName = "Rover";
            var other = Leaf("q", recordedPid: 300u, spawnPid: 0u);
            RecordingStore.AddCommittedInternal(prior);
            RecordingStore.AddCommittedInternal(other);

            int marked = TreeCommitService.MarkTerminalSpawnSupersededByDockMerge(555u, null, 900u, "merged");

            Assert.Equal(1, marked);
            Assert.Equal("merged", prior.TerminalSpawnSupersededByRecordingId);
            Assert.False(prior.VesselSpawned);
            Assert.Equal(0u, prior.SpawnedVesselPersistentId);
            Assert.Null(other.TerminalSpawnSupersededByRecordingId);
            Assert.Contains(logLines, l => l.Contains("DockMerge: terminal spawn for recording 'p'")
                && l.Contains("match=spawn-pid"));
        }

        // =====================================================================
        // ShouldMarkSupersededTerminalSpawn
        // =====================================================================

        private static Recording Prior(uint spawnPid, double endUT, string tree = "t-old")
        {
            return new Recording
            {
                RecordingId = "prior",
                TreeId = tree,
                VesselName = "Rover",
                VesselPersistentId = 100u,
                SpawnedVesselPersistentId = spawnPid,
                ExplicitStartUT = 0.0,
                ExplicitEndUT = endUT,
            };
        }

        private static Recording Continued(uint pid, double startUT, double endUT, string tree = "t-new", int order = 1)
        {
            return new Recording
            {
                RecordingId = "cont",
                TreeId = tree,
                VesselName = "Rover",
                VesselPersistentId = pid,
                TreeOrder = order,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
            };
        }

        [Fact]
        public void ShouldMarkSuperseded_SpawnedPidMatch()
        {
            Assert.True(TreeCommitService.ShouldMarkSupersededTerminalSpawn(
                Prior(555u, 100.0), Continued(555u, 100.0, 200.0), 100.0, out string reason));
            Assert.Equal("spawned-pid-match", reason);
        }

        [Fact]
        public void ShouldMarkSuperseded_GuardsRejectEarly()
        {
            Assert.False(TreeCommitService.ShouldMarkSupersededTerminalSpawn(null, Continued(5u, 0, 1), 0, out _));
            Assert.False(TreeCommitService.ShouldMarkSupersededTerminalSpawn(Prior(5u, 1), null, 0, out _));
            Assert.False(TreeCommitService.ShouldMarkSupersededTerminalSpawn(
                Prior(5u, 100.0), Continued(0u, 100.0, 200.0), 100.0, out _));
            Assert.False(TreeCommitService.ShouldMarkSupersededTerminalSpawn(
                Prior(0u, 100.0), Continued(5u, 100.0, 200.0), 100.0, out _));
        }

        [Fact]
        public void ShouldMarkSuperseded_PriorEndingAfterContinuation_Rejected()
        {
            Assert.False(TreeCommitService.ShouldMarkSupersededTerminalSpawn(
                Prior(555u, 300.0), Continued(555u, 100.0, 200.0), 100.0, out _));
            // Inside the 1 ms tolerance it still matches.
            Assert.True(TreeCommitService.ShouldMarkSupersededTerminalSpawn(
                Prior(555u, 200.0005), Continued(555u, 100.0, 200.0), 100.0, out _));
        }

        [Fact]
        public void ShouldMarkSuperseded_AdoptionRelaunchCollision_FallsThroughToContiguity()
        {
            var prior = Prior(100u, 100.0);
            prior.RecordedVesselGuid = GuidA;
            var relaunch = Continued(100u, 500.0, 600.0);
            relaunch.RecordedVesselGuid = GuidB;
            // Later tree start (500) is past prior end (100): the contiguity branch rejects.
            Assert.False(TreeCommitService.ShouldMarkSupersededTerminalSpawn(prior, relaunch, 500.0, out _));
        }

        [Fact]
        public void ShouldMarkSuperseded_AdoptionStampSameLaunch_Matches()
        {
            var prior = Prior(100u, 100.0);
            prior.RecordedVesselGuid = GuidA;
            var cont = Continued(100u, 100.0, 200.0);
            cont.RecordedVesselGuid = GuidA;
            Assert.True(TreeCommitService.ShouldMarkSupersededTerminalSpawn(prior, cont, 100.0, out string reason));
            Assert.Equal("spawned-pid-match", reason);
        }

        [Fact]
        public void ShouldMarkSuperseded_ContiguousSameNameContinuation_Matches()
        {
            // The prior was re-spawned (pid 777) and the continuation flew pid 555.
            Assert.True(TreeCommitService.ShouldMarkSupersededTerminalSpawn(
                Prior(777u, 100.0), Continued(555u, 100.0, 200.0), 90.0, out string reason));
            Assert.Equal("same-name-overlapping-continuation-tree", reason);
        }

        [Fact]
        public void ShouldMarkSuperseded_ContiguityBranchRejections()
        {
            Assert.False(TreeCommitService.ShouldMarkSupersededTerminalSpawn(
                Prior(777u, 100.0, tree: "same"), Continued(555u, 100.0, 200.0, tree: "same"), 90.0, out _));
            Assert.False(TreeCommitService.ShouldMarkSupersededTerminalSpawn(
                Prior(777u, 100.0), Continued(555u, 100.0, 200.0, order: 0), 90.0, out _));
            Assert.False(TreeCommitService.ShouldMarkSupersededTerminalSpawn(
                Prior(777u, 100.0), Continued(555u, 100.0, 200.0), double.NaN, out _));
            Assert.False(TreeCommitService.ShouldMarkSupersededTerminalSpawn(
                Prior(777u, 100.0), Continued(555u, 100.0, 200.0), 150.0, out _));
            Assert.False(TreeCommitService.ShouldMarkSupersededTerminalSpawn(
                Prior(777u, 100.0), Continued(555u, 99.0, 200.0), 90.0, out _));

            var renamed = Continued(555u, 100.0, 200.0);
            renamed.VesselName = "Other";
            Assert.False(TreeCommitService.ShouldMarkSupersededTerminalSpawn(Prior(777u, 100.0), renamed, 90.0, out _));
        }

        private static string[] Sorted(IEnumerable<string> values)
        {
            var list = new List<string>(values);
            list.Sort(StringComparer.Ordinal);
            return list.ToArray();
        }
    }
}
