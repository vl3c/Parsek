using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// SEAL-LEAVES-SPLIT-CHAIN-HEAD-PROVISIONAL: the route candidacy gate
    /// (<see cref="RouteCandidateFinder.IsTreeFullySealed(RecordingTree, IReadOnlyList{RecordingSupersedeRelation})"/>),
    /// its re-flyable count and the SealSlot seam read each member through
    /// <see cref="EffectiveState.ResolveMemberSealState"/>. Every case names what the
    /// member read BEFORE (raw MergeState) and what it reads now; the cases that must
    /// stay OPEN are the ones a wrong "sealed" would turn into a route offered from an
    /// unfinished flight.
    /// </summary>
    [Collection("Sequential")]
    public class TreeSealedThroughTipTests : IDisposable
    {
        private const string TreeId = "tst-tree";
        private readonly List<string> logLines = new List<string>();

        public TreeSealedThroughTipTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static readonly List<RecordingSupersedeRelation> NoRows =
            new List<RecordingSupersedeRelation>();

        private static Recording Rec(string id, MergeState state,
            string chainId = null, int chainIndex = 0, string treeId = TreeId)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
                ChainId = chainId,
                ChainIndex = chainIndex,
            };
        }

        private static RecordingTree Tree(params Recording[] recs)
        {
            var tree = new RecordingTree { Id = TreeId, RootRecordingId = "root" };
            tree.AddOrReplaceRecording(Rec("root", MergeState.Immutable));
            foreach (var r in recs)
                tree.AddOrReplaceRecording(r);
            return tree;
        }

        private static List<RecordingSupersedeRelation> Row(string oldId, string newId)
        {
            return new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    RelationId = "rsr_" + oldId,
                    OldRecordingId = oldId,
                    NewRecordingId = newId,
                    UT = 100.0,
                },
            };
        }

        private static bool Sealed(RecordingTree tree, List<RecordingSupersedeRelation> rows)
            => RouteCandidateFinder.IsTreeFullySealed(tree, rows);

        // ---- the defect: a split chain head of a closed slot ----

        [Fact]
        public void SplitChainHead_TipSealed_ReadsSealed()
        {
            // Before: the head's raw CommittedProvisional kept the tree unsealed forever.
            var tree = Tree(
                Rec("head", MergeState.CommittedProvisional, "c1", 0),
                Rec("tip", MergeState.Immutable, "c1", 1));

            Assert.Equal(MergeState.Immutable, EffectiveState.ResolveMemberSealState(
                tree.Recordings["head"], "head", tree, NoRows));
            Assert.True(Sealed(tree, NoRows));
            Assert.Equal(0, RouteCandidateFinder.CountReflyableRecordings(tree, NoRows));
            Assert.Empty(TestCommandSealSlot.RecordingIdsNeedingSeal(tree, NoRows));
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("IsTreeFullySealed: tree=" + TreeId)
                && l.Contains("1 member(s) not Immutable themselves"));
        }

        [Fact]
        public void SplitChainHead_TipOpen_ReadsOpen()
        {
            // Before and after: open. The slot is still re-flyable.
            var tree = Tree(
                Rec("head", MergeState.CommittedProvisional, "c1", 0),
                Rec("tip", MergeState.CommittedProvisional, "c1", 1));

            Assert.False(Sealed(tree, NoRows));
            Assert.Equal(2, RouteCandidateFinder.CountReflyableRecordings(tree, NoRows));
            Assert.Equal(new[] { "head", "tip" },
                TestCommandSealSlot.RecordingIdsNeedingSeal(tree, NoRows).ToArray());
        }

        // ---- superseded Re-Fly leftovers ----

        [Fact]
        public void SupersededOldRecording_ForkSealed_ReadsSealed()
        {
            // An old slot recording superseded by its Re-Fly fork, the fork concluded.
            // Before: the old recording's CommittedProvisional kept the tree unsealed.
            var tree = Tree(
                Rec("old", MergeState.CommittedProvisional),
                Rec("fork", MergeState.Immutable));
            var rows = Row("old", "fork");

            Assert.True(Sealed(tree, rows));
            // Without the row the same tree is open: the walk, not the shape, decides.
            Assert.False(Sealed(tree, NoRows));
        }

        [Fact]
        public void SupersededOldRecording_ForkOpen_ReadsOpen()
        {
            // The Re-Fly merge left the slot open (a crashed re-fly): still open.
            var tree = Tree(
                Rec("old", MergeState.CommittedProvisional),
                Rec("fork", MergeState.CommittedProvisional));
            var rows = Row("old", "fork");

            Assert.False(Sealed(tree, rows));
            Assert.Equal(2, RouteCandidateFinder.CountReflyableRecordings(tree, rows));
        }

        [Fact]
        public void SplitterHeadKeptVisible_TipSuperseded_ReadsTheFork()
        {
            // RecordingTreeSplitter's shape: HEAD (pre-rewind, kept visible) and TIP
            // (post-rewind) are chain siblings, the supersede row sits on TIP.
            // HEAD -> chain -> TIP -> supersede -> fork.
            var sealedFork = Tree(
                Rec("HEAD", MergeState.CommittedProvisional, "cs", 0),
                Rec("TIP", MergeState.CommittedProvisional, "cs", 1),
                Rec("fork", MergeState.Immutable));
            Assert.True(Sealed(sealedFork, Row("TIP", "fork")));

            var openFork = Tree(
                Rec("HEAD", MergeState.CommittedProvisional, "cs", 0),
                Rec("TIP", MergeState.CommittedProvisional, "cs", 1),
                Rec("fork", MergeState.CommittedProvisional));
            Assert.False(Sealed(openFork, Row("TIP", "fork")));
        }

        [Fact]
        public void RetiredEmptyProvisional_OriginStaysOpen()
        {
            // The leaf prune removed the empty provisional from the tree and no
            // supersede row was written: the origin's slot is still open.
            var tree = Tree(Rec("origin", MergeState.CommittedProvisional));
            Assert.False(Sealed(tree, NoRows));
            Assert.Equal(new[] { "origin" },
                TestCommandSealSlot.RecordingIdsNeedingSeal(tree, NoRows).ToArray());
        }

        // ---- members that must never read sealed ----

        [Fact]
        public void NotCommittedMember_NeverReadsSealed_EvenWithASealedTip()
        {
            var tree = Tree(
                Rec("head", MergeState.NotCommitted, "c1", 0),
                Rec("tip", MergeState.Immutable, "c1", 1));
            Assert.Equal(MergeState.NotCommitted, EffectiveState.ResolveMemberSealState(
                tree.Recordings["head"], "head", tree, NoRows));
            Assert.False(Sealed(tree, NoRows));
        }

        [Fact]
        public void NotCommittedTip_KeepsTheChainOpen()
        {
            var tree = Tree(
                Rec("head", MergeState.CommittedProvisional, "c1", 0),
                Rec("tip", MergeState.NotCommitted, "c1", 1));
            Assert.Equal(MergeState.NotCommitted, EffectiveState.ResolveMemberSealState(
                tree.Recordings["head"], "head", tree, NoRows));
            Assert.False(Sealed(tree, NoRows));
        }

        [Fact]
        public void StashReopenedSlot_ReadsOpen_HeadIncluded()
        {
            // Stash demotes only the tip of a stable leaf (born Immutable). Before: the
            // tip alone kept the tree open. Now the head reads through the open tip too.
            var tree = Tree(
                Rec("head", MergeState.Immutable, "c1", 0),
                Rec("tip", MergeState.CommittedProvisional, "c1", 1));
            Assert.False(Sealed(tree, NoRows));
            Assert.Equal(new[] { "head", "tip" },
                TestCommandSealSlot.RecordingIdsNeedingSeal(tree, NoRows).ToArray());
        }

        [Fact]
        public void TipInAnotherCommittedTree_Open_KeepsThisTreeOpen()
        {
            // The walk leaves the tree (a fork committed elsewhere): the committed-store
            // lookup still finds it, and its open state wins over the member's own.
            var other = new RecordingTree { Id = "other-tree", RootRecordingId = "fork" };
            other.AddOrReplaceRecording(Rec("fork", MergeState.CommittedProvisional,
                treeId: "other-tree"));
            RecordingStore.AddCommittedTreeForTesting(other);

            var tree = Tree(Rec("old", MergeState.Immutable));
            Assert.False(Sealed(tree, Row("old", "fork")));

            other.Recordings["fork"].MergeState = MergeState.Immutable;
            Assert.True(Sealed(tree, Row("old", "fork")));
        }

        [Fact]
        public void DanglingSupersedeRow_ReadsTheMembersOwnState()
        {
            var open = Tree(Rec("old", MergeState.CommittedProvisional));
            Assert.False(Sealed(open, Row("old", "missing")));

            var closed = Tree(Rec("old", MergeState.Immutable));
            Assert.True(Sealed(closed, Row("old", "missing")));
        }

        [Fact]
        public void PlainImmutableTree_StaysSealed_AndLogsNoThroughTipLine()
        {
            var tree = Tree(
                Rec("a", MergeState.Immutable, "c1", 0),
                Rec("b", MergeState.Immutable, "c1", 1));
            Assert.True(Sealed(tree, NoRows));
            Assert.DoesNotContain(logLines, l => l.Contains("member(s) not Immutable themselves"));
        }

        [Fact]
        public void ProductionOverload_ReadsTheLoadedScenarioRows()
        {
            var tree = Tree(
                Rec("old", MergeState.CommittedProvisional),
                Rec("fork", MergeState.Immutable));
            Assert.False(RouteCandidateFinder.IsTreeFullySealed(tree));

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = Row("old", "fork"),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            Assert.True(RouteCandidateFinder.IsTreeFullySealed(tree));
            Assert.Empty(TestCommandSealSlot.RecordingIdsNeedingSeal(tree));
        }
    }
}
