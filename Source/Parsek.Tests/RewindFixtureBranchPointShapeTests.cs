using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Every injected rewind corpus is production-shaped at its rewind point (todo
    /// REWIND-CREW-LOSS-FIXTURE-RP-HAS-NO-BRANCH-POINT): the committed tree carries the split
    /// BranchPoint that names the point (<c>RewindPointAuthor.Begin</c> stamps it), and the
    /// point is persistent, the state <c>RecordingStore.PromoteNormalStagingRewindPoints</c>
    /// leaves a committed tree's staging point in. Without the link the in-session owner
    /// partition read the point as the save's (QL-3 `2026-10-07_1920`
    /// `rp_cl_root:FollowSave:kept-loaded`, QL-5 `2026-10-07_2035`
    /// `rp_b9_root:FollowSave:kept-loaded`), which no production tree does.
    /// </summary>
    [Collection("Sequential")]
    public class RewindFixtureBranchPointShapeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RewindFixtureBranchPointShapeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        public static IEnumerable<object[]> Corpora()
        {
            yield return new object[] { "rewind-b9" };
            yield return new object[] { "rewind-crew-loss" };
            yield return new object[] { "refly-world-preservation" };
            yield return new object[] { "rewind-readback" };
        }

        private sealed class Corpus
        {
            internal string RewindPointId;
            internal string BranchPointId;
            internal string RootId;
            internal string[] ChildIds;
            internal RecordingTree Tree;
            internal RewindPoint Point;
        }

        private static Corpus Build(string preset)
        {
            var writer = new ScenarioWriter();
            var c = new Corpus();
            switch (preset)
            {
                case "rewind-b9":
                    RewindB9Fixture.PopulateWriter(writer, 1000.0);
                    c.RewindPointId = RewindB9Fixture.RewindPointId;
                    c.BranchPointId = RewindB9Fixture.BranchPointId;
                    c.RootId = RewindB9Fixture.RootRecordingId;
                    c.ChildIds = new[] { RewindB9Fixture.UpperRecordingId, RewindB9Fixture.BoosterRecordingId };
                    break;
                case "rewind-crew-loss":
                    RewindCrewLossFixture.PopulateWriter(writer, 1000.0);
                    c.RewindPointId = RewindCrewLossFixture.RewindPointId;
                    c.BranchPointId = RewindCrewLossFixture.BranchPointId;
                    c.RootId = RewindCrewLossFixture.RootRecordingId;
                    c.ChildIds = new[] { RewindCrewLossFixture.ProbeRecordingId, RewindCrewLossFixture.PodRecordingId };
                    break;
                case "refly-world-preservation":
                    ReFlyWorldPreservationFixture.PopulateWriter(writer, 1000.0);
                    c.RewindPointId = ReFlyWorldPreservationFixture.RewindPointId;
                    c.BranchPointId = ReFlyWorldPreservationFixture.BranchPointId;
                    c.RootId = ReFlyWorldPreservationFixture.RootRecordingId;
                    c.ChildIds = new[] { ReFlyWorldPreservationFixture.UpperRecordingId, ReFlyWorldPreservationFixture.BoosterRecordingId };
                    break;
                case "rewind-readback":
                    RewindReadbackFixture.PopulateWriter(writer, RewindReadbackFixture.HostSaveUT);
                    c.RewindPointId = RewindReadbackFixture.RewindPointId;
                    c.BranchPointId = RewindReadbackFixture.BranchPointId;
                    c.RootId = RewindReadbackFixture.RootRecordingId;
                    c.ChildIds = new[] { RewindReadbackFixture.UpperRecordingId, RewindReadbackFixture.BoosterRecordingId };
                    break;
                default:
                    throw new ArgumentException(preset);
            }

            ConfigNode scenario = writer.BuildScenarioNode();
            ConfigNode[] trees = scenario.GetNodes("RECORDING_TREE");
            Assert.Single(trees);
            c.Tree = RecordingTree.Load(trees[0]);
            ConfigNode[] points = scenario.GetNode("REWIND_POINTS").GetNodes("POINT");
            Assert.Single(points);
            c.Point = RewindPoint.LoadFrom(points[0]);
            return c;
        }

        [Theory]
        [MemberData(nameof(Corpora))]
        public void TheTreeCarriesTheSplitBranchPointNamingThePoint(string preset)
        {
            Corpus c = Build(preset);

            BranchPoint bp = Assert.Single(c.Tree.BranchPoints);
            Assert.Equal(c.BranchPointId, bp.Id);
            Assert.Equal(BranchPointType.JointBreak, bp.Type);
            Assert.True(RewindPointAuthor.IsReFlySplitType(bp.Type));
            Assert.Equal(c.RewindPointId, bp.RewindPointId);
            Assert.Equal(c.Point.UT, bp.UT);
            Assert.Equal(new[] { c.RootId }, bp.ParentRecordingIds);
            Assert.Equal(c.ChildIds, bp.ChildRecordingIds);
            // The point's slots are exactly the split's children.
            Assert.Equal(
                c.ChildIds.OrderBy(x => x, StringComparer.Ordinal),
                c.Point.ChildSlots.Select(s => s.OriginChildRecordingId).OrderBy(x => x, StringComparer.Ordinal));

            Assert.Equal(c.BranchPointId, c.Tree.Recordings[c.RootId].ChildBranchPointId);
            foreach (string child in c.ChildIds)
                Assert.Equal(c.BranchPointId, c.Tree.Recordings[child].ParentBranchPointId);

            // The pre-split stack ends at the split, so it is not a leaf.
            Assert.DoesNotContain(c.RootId, c.Tree.GetAllLeaves().Select(r => r.RecordingId));
        }

        [Theory]
        [MemberData(nameof(Corpora))]
        public void ThePointIsPersistentAndADurableSplitPoint(string preset)
        {
            Corpus c = Build(preset);

            Assert.Equal(c.BranchPointId, c.Point.BranchPointId);
            Assert.False(c.Point.SessionProvisional);
            Assert.Null(c.Point.CreatingSessionId);
            Assert.False(c.Point.Corrupted);
            // At least one slot is open (CommittedProvisional), so the reaper keeps the
            // persistent point until a re-fly closes it.
            Assert.Contains(c.ChildIds, id =>
                c.Tree.Recordings[id].MergeState == MergeState.CommittedProvisional);
        }

        [Theory]
        [MemberData(nameof(Corpora))]
        public void TheInSessionOwnerPartitionClaimsThePointForItsCommittedTree(string preset)
        {
            Corpus c = Build(preset);

            InSessionStagedStateHandoff.CollectCommittedTreeRewindLinks(
                new[] { c.Tree }, out HashSet<string> bpIds, out HashSet<string> rpIds);
            Func<RewindPoint, bool> owned = p =>
                bpIds.Contains(p.BranchPointId ?? "") || rpIds.Contains(p.RewindPointId ?? "");
            Assert.Equal(RewindPointOwnerClass.CommittedOwner,
                InSessionStagedStateHandoff.Classify(c.Point, owned));

            // Memory and save both hold it (a Discard Re-fly load): memory's instance kept.
            var decisions = new List<string>();
            InSessionStagedStateHandoff.MergeRewindPointsByOwner(
                new List<RewindPoint> { c.Point }, new List<RewindPoint> { c.Point }, owned,
                out RewindPointPartitionCounts _, decisions);
            Assert.Equal(new[] { c.RewindPointId + ":CommittedOwner:kept-memory" }, decisions);

            // A merge reaped it and an earlier save still lists it: not resurrected.
            decisions.Clear();
            List<RewindPoint> merged = InSessionStagedStateHandoff.MergeRewindPointsByOwner(
                new List<RewindPoint>(), new List<RewindPoint> { c.Point }, owned,
                out RewindPointPartitionCounts _, decisions);
            Assert.Empty(merged);
            Assert.Equal(new[] { c.RewindPointId + ":CommittedOwner:dropped-save-only" }, decisions);
        }

        [Fact]
        public void TheThroughEvaCorpusSplitNamesItsPoint()
        {
            foreach (bool upperIsParent in new[] { false, true })
            {
                RecordingTree tree = ReFlyThroughEvaFixture.MaterializeTree(
                    ReFlyThroughEvaVariant.TwoEvasOneLeavesForForeign, upperIsParent);
                BranchPoint split = tree.BranchPoints.Single(
                    b => b.Id == ReFlyThroughEvaFixture.SplitBranchPointId);
                Assert.Equal(ReFlyThroughEvaFixture.RewindPointId, split.RewindPointId);
                // Only the split names the point; the EVA / board points do not.
                Assert.Single(tree.BranchPoints, b => b.RewindPointId != null);
            }
        }
    }
}
