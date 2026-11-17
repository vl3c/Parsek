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
    /// its re-flyable flight count and the SealSlot seam read each member through
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
            string chainId = null, int chainIndex = 0, string treeId = TreeId,
            uint pid = 0, string parentBp = null, string childBp = null,
            string eva = null, int chainBranch = 0)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = chainBranch,
                VesselPersistentId = pid,
                ParentBranchPointId = parentBp,
                ChildBranchPointId = childBp,
                EvaCrewName = eva,
            };
        }

        private static BranchPoint Bp(string id, BranchPointType type,
            string[] parents, string[] children)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = 100.0,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
            };
        }

        private static RecordingTree TreeWithBranch(BranchPoint bp, params Recording[] recs)
        {
            RecordingTree tree = Tree(recs);
            tree.BranchPoints.Add(bp);
            return tree;
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

        private static List<RecordingSupersedeRelation> Rows(params string[] pairs)
        {
            var rows = new List<RecordingSupersedeRelation>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                rows.Add(new RecordingSupersedeRelation
                {
                    RelationId = "rsr_" + pairs[i],
                    OldRecordingId = pairs[i],
                    NewRecordingId = pairs[i + 1],
                    UT = 100.0,
                });
            return rows;
        }

        private static int Flights(RecordingTree tree, List<RecordingSupersedeRelation> rows)
            => RouteCandidateFinder.CountReflyableFlights(tree, rows);

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
            Assert.Equal(0, Flights(tree, NoRows));
            Assert.Empty(TestCommandSealSlot.RecordingIdsNeedingSeal(tree, NoRows));
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("IsTreeFullySealed: tree=" + TreeId)
                && l.Contains("1 member(s) not Immutable themselves"));
        }

        [Fact]
        public void SplitChainHead_TipOpen_ReadsOpen()
        {
            // Before and after: open. The slot is still re-flyable, and it is one flight
            // (head and tip resolve to the same open tip), not two.
            var tree = Tree(
                Rec("head", MergeState.CommittedProvisional, "c1", 0),
                Rec("tip", MergeState.CommittedProvisional, "c1", 1));

            Assert.False(Sealed(tree, NoRows));
            Assert.Equal(1, Flights(tree, NoRows));
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
            // One open flight: the old recording reads through to the fork.
            Assert.Equal(1, Flights(tree, rows));
            // Without the row they are two separate open recordings.
            Assert.Equal(2, Flights(tree, NoRows));
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

        // ---- the re-flyable flight count ----

        [Fact]
        public void FlightCount_TwoOpenSlots_CountsTwo_SealedOnesNotAtAll()
        {
            // Two independent open slots (one a split chain) and a sealed split chain.
            var tree = Tree(
                Rec("a0", MergeState.CommittedProvisional, "ca", 0),
                Rec("a1", MergeState.CommittedProvisional, "ca", 1),
                Rec("b", MergeState.CommittedProvisional),
                Rec("s0", MergeState.CommittedProvisional, "cs", 0),
                Rec("s1", MergeState.Immutable, "cs", 1));
            Assert.Equal(2, Flights(tree, NoRows));
        }

        [Fact]
        public void FlightCount_SupersededSubtreeOnOneOpenFork_CountsOne()
        {
            // A superseded split chain whose tip points at one open fork: one flight.
            var tree = Tree(
                Rec("h", MergeState.CommittedProvisional, "c", 0),
                Rec("t", MergeState.CommittedProvisional, "c", 1),
                Rec("fork", MergeState.CommittedProvisional));
            Assert.Equal(1, Flights(tree, Row("t", "fork")));
        }

        [Fact]
        public void FlightCount_NotCommittedMember_CountsAsItsOwnFlight()
        {
            var tree = Tree(
                Rec("head", MergeState.NotCommitted, "c1", 0),
                Rec("tip", MergeState.CommittedProvisional, "c1", 1));
            Assert.Equal(2, Flights(tree, NoRows));
        }

        // ---- walk boundaries: the chain hop must stop where the vessel changes ----

        [Fact]
        public void Undock_StopsTheWalk_OpenParentStaysOpen()
        {
            var tree = TreeWithBranch(
                Bp("bpU", BranchPointType.Undock, new[] { "V" }, new[] { "V2", "U" }),
                Rec("V", MergeState.CommittedProvisional, pid: 1, childBp: "bpU"),
                Rec("V2", MergeState.Immutable, pid: 1, parentBp: "bpU"),
                Rec("U", MergeState.Immutable, pid: 2, parentBp: "bpU"));
            Assert.False(Sealed(tree, NoRows));
        }

        [Fact]
        public void ForeignBoard_StopsTheWalk_OpenVesselStaysOpen()
        {
            // A kerbal of another vessel boards A: the merged child is not A's own.
            var tree = TreeWithBranch(
                Bp("bpB", BranchPointType.Board, new[] { "A", "K" }, new[] { "M" }),
                Rec("A", MergeState.CommittedProvisional, pid: 1, childBp: "bpB"),
                Rec("K", MergeState.Immutable, pid: 77, childBp: "bpB", eva: "Bob"),
                Rec("M", MergeState.Immutable, pid: 1, parentBp: "bpB"));
            Assert.Equal(MergeState.CommittedProvisional, EffectiveState.ResolveMemberSealState(
                tree.Recordings["A"], "A", tree, NoRows));
            Assert.False(Sealed(tree, NoRows));
        }

        [Fact]
        public void OwnEva_VesselFollowsItsContinuation_OpenKerbalReadsItsOwnState()
        {
            var tree = TreeWithBranch(
                Bp("bpE", BranchPointType.EVA, new[] { "V" }, new[] { "V1", "K" }),
                Rec("V", MergeState.CommittedProvisional, pid: 1, childBp: "bpE"),
                Rec("V1", MergeState.Immutable, pid: 1, parentBp: "bpE"),
                Rec("K", MergeState.CommittedProvisional, pid: 9, parentBp: "bpE", eva: "Jeb"));
            Assert.Equal(MergeState.Immutable, EffectiveState.ResolveMemberSealState(
                tree.Recordings["V"], "V", tree, NoRows));
            Assert.Equal(MergeState.CommittedProvisional, EffectiveState.ResolveMemberSealState(
                tree.Recordings["K"], "K", tree, NoRows));
            Assert.False(Sealed(tree, NoRows));
        }

        [Fact]
        public void OpenKerbal_BoardingAnotherVessel_StaysOpen()
        {
            var tree = TreeWithBranch(
                Bp("bpB", BranchPointType.Board, new[] { "W", "K" }, new[] { "W1" }),
                Rec("K", MergeState.CommittedProvisional, pid: 9, childBp: "bpB", eva: "Jeb"),
                Rec("W", MergeState.Immutable, pid: 2, childBp: "bpB"),
                Rec("W1", MergeState.Immutable, pid: 2, parentBp: "bpB"));
            Assert.False(Sealed(tree, NoRows));
        }

        [Fact]
        public void SwitchContinuation_SealedSegmentBeforeAnOpenOne_ReadsOpen_OneFlight()
        {
            var tree = TreeWithBranch(
                Bp("bpS", BranchPointType.VesselSwitchContinuation, new[] { "A" }, new[] { "C" }),
                Rec("A", MergeState.Immutable, pid: 1, childBp: "bpS"),
                Rec("C", MergeState.CommittedProvisional, pid: 1, parentBp: "bpS"));
            Assert.False(Sealed(tree, NoRows));
            Assert.Equal(1, Flights(tree, NoRows));
        }

        [Fact]
        public void DifferentChainBranch_DoesNotCarryTheTip()
        {
            var tree = Tree(
                Rec("h", MergeState.CommittedProvisional, "c", 0, chainBranch: 0),
                Rec("g", MergeState.Immutable, "c", 1, chainBranch: 1));
            Assert.False(Sealed(tree, NoRows));
        }

        [Fact]
        public void SameChainIdInAnotherTree_DoesNotCarryTheTip()
        {
            var other = new RecordingTree { Id = "other-tree", RootRecordingId = "x" };
            other.AddOrReplaceRecording(Rec("x", MergeState.Immutable, "c", 5, treeId: "other-tree"));
            RecordingStore.AddCommittedTreeForTesting(other);

            var tree = Tree(Rec("h", MergeState.CommittedProvisional, "c", 0));
            Assert.False(Sealed(tree, NoRows));
        }

        [Fact]
        public void NotCommittedInPlaceFork_KeepsTheOldRecordingOpen()
        {
            var open = Tree(
                Rec("O", MergeState.CommittedProvisional),
                Rec("F", MergeState.NotCommitted));
            Assert.False(Sealed(open, Row("O", "F")));

            var sealedOld = Tree(
                Rec("O", MergeState.Immutable),
                Rec("F", MergeState.NotCommitted));
            Assert.Equal(MergeState.NotCommitted, EffectiveState.ResolveMemberSealState(
                sealedOld.Recordings["O"], "O", sealedOld, Row("O", "F")));
        }

        [Fact]
        public void ThreeMembersOfOneChain_ReadTheSameTip()
        {
            var closed = Tree(
                Rec("a0", MergeState.CommittedProvisional, "c", 0),
                Rec("a1", MergeState.CommittedProvisional, "c", 1),
                Rec("a2", MergeState.Immutable, "c", 2));
            Assert.True(Sealed(closed, NoRows));

            var open = Tree(
                Rec("a0", MergeState.Immutable, "c", 0),
                Rec("a1", MergeState.Immutable, "c", 1),
                Rec("a2", MergeState.CommittedProvisional, "c", 2));
            Assert.False(Sealed(open, NoRows));
            Assert.Equal(1, Flights(open, NoRows));
        }

        [Fact]
        public void SupersedeCycleThroughAnOutsideRecording_ReadsWhatTheSlotWalkReads()
        {
            // Corrupt rows (no writer produces X -> Z -> X): the cycle guard stops on Z,
            // the same tip every slot consumer reads, so X reads Z's state. Kept as a
            // named residue in todo SEAL-LEAVES-SPLIT-CHAIN-HEAD-PROVISIONAL.
            var other = new RecordingTree { Id = "other-tree", RootRecordingId = "Z" };
            other.AddOrReplaceRecording(Rec("Z", MergeState.Immutable, treeId: "other-tree"));
            RecordingStore.AddCommittedTreeForTesting(other);

            var tree = Tree(Rec("X", MergeState.CommittedProvisional));
            List<RecordingSupersedeRelation> rows = Rows("X", "Z", "Z", "X");
            Assert.Equal("Z", EffectiveState.EffectiveTipRecordingId("X", rows, tree.Recordings, tree));
            Assert.True(Sealed(tree, rows));
        }

        // ---- the 1 Hz Logistics derivations read the loaded scenario's rows ----

        [Fact]
        public void DeriveCandidates_ReadsTheLoadedScenarioRows()
        {
            var tree = Tree(
                Rec("old", MergeState.CommittedProvisional),
                Rec("fork", MergeState.Immutable));
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RecordingSupersedes = Row("old", "fork"),
            });

            RouteCandidateFinder.DeriveCandidates(new List<RecordingTree> { tree }, new List<Route>());

            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("DeriveCandidates: trees=1") && l.Contains("notSealed=0"));
        }

        [Fact]
        public void DeriveNearMisses_ReadsTheLoadedScenarioRows()
        {
            var tree = Tree(
                Rec("old", MergeState.CommittedProvisional),
                Rec("fork", MergeState.Immutable));
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RecordingSupersedes = Row("old", "fork"),
            });

            List<RouteNearMiss> nearMisses =
                RouteCandidateFinder.DeriveNearMisses(new List<RecordingTree> { tree });

            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("DeriveNearMisses: trees=1") && l.Contains("notSealed=0"));
            Assert.All(nearMisses, nm => Assert.False(nm.NotSealed));
        }

        [Fact]
        public void DeriveNearMisses_ReflyableCount_ReadsTheLoadedScenarioRows()
        {
            // One genuinely open recording plus one superseded by a concluded fork: one
            // flight can still be re-flown, not two.
            var tree = Tree(
                Rec("open", MergeState.CommittedProvisional),
                Rec("old", MergeState.CommittedProvisional),
                Rec("fork", MergeState.Immutable));
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RecordingSupersedes = Row("old", "fork"),
            });

            List<RouteNearMiss> nearMisses =
                RouteCandidateFinder.DeriveNearMisses(new List<RecordingTree> { tree });

            RouteNearMiss nm = Assert.Single(nearMisses);
            Assert.True(nm.NotSealed);
            Assert.Equal(1, nm.ReflyableCount);
        }
    }
}
