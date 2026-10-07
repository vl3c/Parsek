using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CHAIN-WALK-SUPERSEDE-BLIND-HOPS-ONTO-REFLY-TIP: <see cref="GhostChainWalker"/> read the
    /// committed trees as they are, supersede relations ignored. A Re-Fly splits the slot's
    /// recording into HEAD + TIP on one optimizer chain (<see cref="RecordingTreeSplitter"/>) and
    /// its fork supersedes TIP, so the walk's chain hop landed on TIP, the recording that no
    /// longer plays: the chain's tip and spawn UT came from TIP, and the fork (same pid, ending
    /// before TIP) read as an intermediate link, which suppressed its spawn. The same held for an
    /// unsplit superseded child picked at a branch point. These cells build the claimed station's
    /// dock / undock tree, the Re-Fly shape and the supersede rows, then run the walker.
    /// </summary>
    [Collection("Sequential")]
    public class GhostChainWalkerSupersedeTests : IDisposable
    {
        private const string TreeId = "tree-refly-station";
        private const string OtherTreeId = "tree-other";
        private const uint VisitorPid = 50;
        private const uint StationPid = 100;
        private const uint DepartingPid = 77;

        private readonly List<string> logLines = new List<string>();
        private readonly ParsekScenario scenario;

        public GhostChainWalkerSupersedeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();

            scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
        }

        // ------------------------------------------------------------ shapes

        private static ConfigNode Snapshot(params uint[] partPids)
        {
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("root", "0");
            for (int i = 0; i < partPids.Length; i++)
            {
                ConfigNode part = vessel.AddNode("PART");
                part.AddValue("persistentId", partPids[i].ToString(CultureInfo.InvariantCulture));
            }
            return vessel;
        }

        private static Recording Rec(string id, uint pid, double start, double end,
            string treeId = TreeId, string parentBp = null, string childBp = null,
            TerminalState? terminal = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                VesselPersistentId = pid,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                ParentBranchPointId = parentBp,
                ChildBranchPointId = childBp,
                TerminalStateValue = terminal,
                Points = new List<TrajectoryPoint>(),
                MergeState = MergeState.Immutable,
            };
        }

        private void Supersede(string oldId, string newId)
        {
            scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation
            {
                RelationId = "rsr_" + oldId + "_" + newId,
                OldRecordingId = oldId,
                NewRecordingId = newId,
                UT = 2100,
                CreatedRealTime = "2026-10-07T00:00:00Z",
            });
            scenario.BumpSupersedeStateVersion();
        }

        /// <summary>
        /// A visitor V (pid 50) docks to the foreign station (pid 100, the claim) at 1060; the
        /// station dominates, so the merged recording M keeps pid 100. At 1100 the visitor
        /// undocks (D, new pid 77) and the station flies on as S (pid 100), the station's own
        /// continuation slot. With <paramref name="splitAtRewind"/> S is the Re-Fly HEAD
        /// (chain index 0, [1100,1200]) and S-tip is the TIP (index 1, [1200,2000]); else S is
        /// one recording [1100,2000]. The Re-Fly fork F (pid 100, [1200,1500]) hangs off the same
        /// branch point the way <c>RewindInvoker.BuildProvisionalRecording</c> attaches it: its
        /// parent branch point is S's, and no branch point lists it as a child.
        /// </summary>
        private RecordingTree BuildStationTree(bool splitAtRewind, bool addFork = true)
        {
            var tree = new RecordingTree { Id = TreeId, TreeName = "Station Re-Fly", RootRecordingId = "V" };

            Recording v = Rec("V", VisitorPid, 1000, 1060, childBp: "bp-dock");
            v.VesselSnapshot = Snapshot(1, 2, 3);
            Recording m = Rec("M", StationPid, 1060, 1100, parentBp: "bp-dock", childBp: "bp-undock");
            m.VesselSnapshot = Snapshot(1, 2, 3, 11, 12);
            Recording d = Rec("D", DepartingPid, 1100, 1300, parentBp: "bp-undock",
                terminal: TerminalState.Orbiting);
            d.VesselSnapshot = Snapshot(1, 2, 3);

            tree.AddOrReplaceRecording(v);
            tree.AddOrReplaceRecording(m);
            tree.AddOrReplaceRecording(d);

            if (splitAtRewind)
            {
                Recording head = Rec("S", StationPid, 1100, 1200, parentBp: "bp-undock");
                head.ChainId = "chain-refly";
                head.ChainIndex = 0;
                Recording tip = Rec("S-tip", StationPid, 1200, 2000, terminal: TerminalState.Orbiting);
                tip.ChainId = "chain-refly";
                tip.ChainIndex = 1;
                tip.VesselSnapshot = Snapshot(11, 12);
                tree.AddOrReplaceRecording(head);
                tree.AddOrReplaceRecording(tip);
            }
            else
            {
                Recording s = Rec("S", StationPid, 1100, 2000, parentBp: "bp-undock",
                    terminal: TerminalState.Orbiting);
                s.VesselSnapshot = Snapshot(11, 12);
                tree.AddOrReplaceRecording(s);
            }

            if (addFork)
            {
                Recording fork = Rec("F", StationPid, 1200, 1500, parentBp: "bp-undock",
                    terminal: TerminalState.Orbiting);
                fork.VesselSnapshot = Snapshot(11, 12);
                tree.AddOrReplaceRecording(fork);
            }

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock", Type = BranchPointType.Dock, UT = 1060,
                TargetVesselPersistentId = StationPid,
                ParentRecordingIds = new List<string> { "V" },
                ChildRecordingIds = new List<string> { "M" },
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-undock", Type = BranchPointType.Undock, UT = 1100,
                ParentRecordingIds = new List<string> { "M" },
                ChildRecordingIds = new List<string> { "D", "S" },
            });

            RecordingStore.AddCommittedTreeForTesting(tree);
            foreach (Recording rec in tree.Recordings.Values)
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            return tree;
        }

        private static Dictionary<uint, GhostChain> Walk()
            => GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, 900);

        private void AssertForkIsTheSpawningTip(Dictionary<uint, GhostChain> chains, RecordingTree tree)
        {
            Assert.True(chains.ContainsKey(StationPid));
            GhostChain chain = chains[StationPid];
            Recording fork = tree.Recordings["F"];
            Assert.Equal("F", chain.TipRecordingId);
            Assert.Equal(TreeId, chain.TipTreeId);
            Assert.Equal(1500.0, chain.SpawnUT);
            Assert.False(chain.IsTerminated);

            Assert.Null(GhostChainWalker.FindIntermediateLinkChain(chains, fork));
            Assert.False(GhostPlaybackLogic.ShouldSuppressSpawnForChain(chains, fork).suppressed);
            Assert.Same(chain, ParsekFlight.FindChainTipForRecording(chains, fork));
        }

        // ------------------------------------------------------------ cells

        // catches: the defect (the PR #2037 reviewer's probe). The chain hop from HEAD lands on
        // TIP, which the fork supersedes; the walk must follow the supersede row to the fork, so
        // the fork is the chain's tip and spawns at its own end.
        [Fact]
        public void ReFlyHeadTip_TipSupersededByFork_WalkLandsOnTheFork()
        {
            RecordingTree tree = BuildStationTree(splitAtRewind: true);
            Supersede("S-tip", "F");

            var chains = Walk();

            AssertForkIsTheSpawningTip(chains, tree);
            // HEAD and TIP are not where the vessel ends: both read as intermediate links.
            Assert.NotNull(GhostChainWalker.FindIntermediateLinkChain(chains, tree.Recordings["S"]));
            Assert.Null(ParsekFlight.FindChainTipForRecording(chains, tree.Recordings["S-tip"]));
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("WalkToLeaf: step") && l.Contains("segment=S-tip")
                && l.Contains("superseded -> effective=F")
                && l.Contains("rule=" + GhostChainWalker.RuleSupersede)
                && l.Contains("identity=" + GhostChainWalker.SupersedeBasisClaimedParts));
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("WalkToLeaf: reached leaf=F"));
        }

        // catches: the older form of the same class. The branch point's child S is not split and
        // the fork supersedes it directly; the walk picked S and stopped there.
        [Fact]
        public void UnsplitChildAtBranchPoint_SupersededByFork_WalkLandsOnTheFork()
        {
            RecordingTree tree = BuildStationTree(splitAtRewind: false);
            Supersede("S", "F");

            var chains = Walk();

            AssertForkIsTheSpawningTip(chains, tree);
            Assert.Null(ParsekFlight.FindChainTipForRecording(chains, tree.Recordings["S"]));
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("WalkToLeaf: step") && l.Contains("child=S")
                && l.Contains("superseded -> effective=F")
                && l.Contains("rule=" + GhostChainWalker.RuleSupersede));
        }

        // catches: a walk that stops after one supersede hop. A nested Re-Fly superseded the
        // first fork too; the walk follows the whole supersede chain to the last fork.
        [Fact]
        public void NestedReFly_ForkSupersededAgain_WalkLandsOnTheLastFork()
        {
            RecordingTree tree = BuildStationTree(splitAtRewind: true);
            Recording fork2 = Rec("F2", StationPid, 1300, 1400, parentBp: "bp-undock",
                terminal: TerminalState.Landed);
            fork2.VesselSnapshot = Snapshot(11, 12);
            tree.AddOrReplaceRecording(fork2);
            RecordingStore.AddRecordingWithTreeForTesting(fork2);
            Supersede("S-tip", "F");
            Supersede("F", "F2");

            var chains = Walk();

            GhostChain chain = chains[StationPid];
            Assert.Equal("F2", chain.TipRecordingId);
            Assert.Equal(1400.0, chain.SpawnUT);
            Assert.Null(GhostChainWalker.FindIntermediateLinkChain(chains, fork2));
            Assert.Null(ParsekFlight.FindChainTipForRecording(chains, tree.Recordings["F"]));
        }

        // catches: the mirror. With no supersede row the Re-Fly HEAD / TIP chain (or any optimizer
        // chain) is walked as before: the tip is TIP and no supersede hop is logged.
        [Fact]
        public void ReFlyHeadTip_NoSupersedeRow_TipUnchanged()
        {
            RecordingTree tree = BuildStationTree(splitAtRewind: true, addFork: false);

            var chains = Walk();

            Assert.Equal("S-tip", chains[StationPid].TipRecordingId);
            Assert.Equal(2000.0, chains[StationPid].SpawnUT);
            Assert.DoesNotContain(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("rule=" + GhostChainWalker.RuleSupersede));
        }

        // catches: the mirror on the walk's other side. A supersede row on a recording the walk
        // never reaches (the departing visitor D) changes nothing: the station's tip is S.
        [Fact]
        public void SupersedeOnASiblingOffThePath_TipUnchanged()
        {
            RecordingTree tree = BuildStationTree(splitAtRewind: false);
            Supersede("D", "F");

            var chains = Walk();

            Assert.Equal("S", chains[StationPid].TipRecordingId);
            Assert.Equal(2000.0, chains[StationPid].SpawnUT);
            Assert.DoesNotContain(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("rule=" + GhostChainWalker.RuleSupersede));
        }

        // catches: a walk that follows a supersede row out of the tree. Supersede rows are scoped
        // to the Re-Fly session's tree (rewind design 5.7 / known limitations), and the chain's
        // tip tree is the walk's tree, so a row naming a recording of another tree is not
        // followed: the walk keeps the recording it reached and logs why.
        [Fact]
        public void SupersedeIntoAnotherTree_NotFollowed()
        {
            RecordingTree tree = BuildStationTree(splitAtRewind: true, addFork: false);
            var other = new RecordingTree { Id = OtherTreeId, TreeName = "Other", RootRecordingId = "X" };
            Recording x = Rec("X", 4242, 1200, 1500, treeId: OtherTreeId, terminal: TerminalState.Orbiting);
            other.AddOrReplaceRecording(x);
            RecordingStore.AddCommittedTreeForTesting(other);
            RecordingStore.AddRecordingWithTreeForTesting(x);
            Supersede("S-tip", "X");

            var chains = Walk();

            Assert.Equal("S-tip", chains[StationPid].TipRecordingId);
            Assert.Equal(TreeId, chains[StationPid].TipTreeId);
            Assert.Equal(2000.0, chains[StationPid].SpawnUT);
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("superseded by X") && l.Contains("not in tree " + TreeId));
        }

        // catches: an orphan row (its new recording gone) taking the walk nowhere. The walk keeps
        // the recording it reached, as before the fix.
        [Fact]
        public void SupersedeToAMissingRecording_NotFollowed()
        {
            BuildStationTree(splitAtRewind: true, addFork: false);
            Supersede("S-tip", "rec-missing");

            var chains = Walk();

            Assert.Equal("S-tip", chains[StationPid].TipRecordingId);
            Assert.Equal(2000.0, chains[StationPid].SpawnUT);
        }

        // catches: a fork that ended Destroyed. The walk lands on the fork, so the chain reads
        // the fork's terminal and is terminated (TIP's Orbiting no longer keeps it alive).
        [Fact]
        public void ForkDestroyed_ChainTerminatedOnTheFork()
        {
            RecordingTree tree = BuildStationTree(splitAtRewind: true);
            tree.Recordings["F"].TerminalStateValue = TerminalState.Destroyed;
            Supersede("S-tip", "F");

            var chains = Walk();

            Assert.Equal("F", chains[StationPid].TipRecordingId);
            Assert.True(chains[StationPid].IsTerminated);
        }

        /// <summary>
        /// The mirror shape: the RE-FLOWN vessel is a visitor X (pid 60, parts 1-3) whose old TIP
        /// docked with the station (pid 100, parts 11-12) at 1300; the fork F did not dock. The
        /// Re-Fly writes a row from every recording of TIP's subtree to F, so the merged
        /// recording M2 is superseded by F too, but F does not carry the station.
        /// </summary>
        private RecordingTree BuildVisitorReFlyTree(bool stationDominant)
        {
            const string visitorTree = "tree-refly-visitor";
            const uint xPid = 60;
            var tree = new RecordingTree { Id = visitorTree, TreeName = "Visitor Re-Fly", RootRecordingId = "X" };

            Recording head = Rec("X", xPid, 1000, 1100, treeId: visitorTree);
            head.ChainId = "chain-x";
            head.ChainIndex = 0;
            Recording tip = Rec("X-tip", xPid, 1100, 1300, treeId: visitorTree, childBp: "bp-dock2");
            tip.ChainId = "chain-x";
            tip.ChainIndex = 1;
            tip.VesselSnapshot = Snapshot(1, 2, 3);
            Recording merged = Rec("M2", stationDominant ? StationPid : xPid, 1300, 2000,
                treeId: visitorTree, parentBp: "bp-dock2", terminal: TerminalState.Orbiting);
            merged.VesselSnapshot = Snapshot(1, 2, 3, 11, 12);
            Recording fork = Rec("FX", xPid, 1100, 1500, treeId: visitorTree, terminal: TerminalState.Orbiting);
            fork.VesselSnapshot = Snapshot(1, 2, 3);

            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tip);
            tree.AddOrReplaceRecording(merged);
            tree.AddOrReplaceRecording(fork);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock2", Type = BranchPointType.Dock, UT = 1300,
                TargetVesselPersistentId = StationPid,
                ParentRecordingIds = new List<string> { "X-tip" },
                ChildRecordingIds = new List<string> { "M2" },
            });

            RecordingStore.AddCommittedTreeForTesting(tree);
            foreach (Recording rec in tree.Recordings.Values)
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            return tree;
        }

        // catches: a walk that follows every supersede row. The fork replaces the re-flown visitor
        // only; the merged recording's row names it too, but the fork holds none of the station's
        // parts, so the station's walk must not land on the visitor's fork (which would make the
        // visitor the station's chain tip). Whichever vessel kept its pid in the dock.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RowOnAnotherVesselOfTheSupersededSubtree_NotFollowed(bool stationDominant)
        {
            BuildVisitorReFlyTree(stationDominant);
            Supersede("X-tip", "FX");
            Supersede("M2", "FX");

            var chains = Walk();

            Assert.True(chains.ContainsKey(StationPid));
            Assert.Equal("M2", chains[StationPid].TipRecordingId);
            Assert.Equal(2000.0, chains[StationPid].SpawnUT);
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("child=M2 superseded by FX")
                && l.Contains("does not continue the walked vessel ("
                    + GhostChainWalker.SupersedeBasisClaimedParts + ")"));
        }

        // catches: the identity rule's two arms directly.
        [Fact]
        public void ForkContinuesWalkedVessel_PartsFirstThenPidAndGuid()
        {
            Recording candidate = Rec("c", StationPid, 0, 1);
            candidate.RecordedVesselGuid = "guid-a";
            Recording fork = Rec("f", StationPid, 0, 1);
            fork.RecordedVesselGuid = "guid-a";
            string basis;

            // No part data anywhere: same pid and launch.
            Assert.True(GhostChainWalker.ForkContinuesWalkedVessel(candidate, fork, null, out basis));
            Assert.Equal(GhostChainWalker.SupersedeBasisSamePid, basis);

            // Same pid, another launch.
            fork.RecordedVesselGuid = "guid-b";
            Assert.False(GhostChainWalker.ForkContinuesWalkedVessel(candidate, fork, null, out basis));
            fork.RecordedVesselGuid = "guid-a";

            // Another pid.
            fork.VesselPersistentId = DepartingPid;
            Assert.False(GhostChainWalker.ForkContinuesWalkedVessel(candidate, fork, null, out basis));

            // Part data decides when both sides have it, whatever the pid says.
            var claimedParts = new HashSet<uint> { 11, 12 };
            fork.VesselSnapshot = Snapshot(11, 40);
            Assert.True(GhostChainWalker.ForkContinuesWalkedVessel(candidate, fork, claimedParts, out basis));
            Assert.Equal(GhostChainWalker.SupersedeBasisClaimedParts, basis);
            fork.VesselPersistentId = StationPid;
            fork.VesselSnapshot = Snapshot(1, 2);
            Assert.False(GhostChainWalker.ForkContinuesWalkedVessel(candidate, fork, claimedParts, out basis));
            Assert.Equal(GhostChainWalker.SupersedeBasisClaimedParts, basis);

            // A fork with no part data falls back to pid and guid.
            fork.VesselSnapshot = null;
            Assert.True(GhostChainWalker.ForkContinuesWalkedVessel(candidate, fork, claimedParts, out basis));
            Assert.Equal(GhostChainWalker.SupersedeBasisSamePid, basis);
        }

        // catches: the explicit-list overload reading the rows it is given, not the scenario's.
        [Fact]
        public void ExplicitSupersedeList_IsTheOneRead()
        {
            RecordingTree tree = BuildStationTree(splitAtRewind: true);
            var rows = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation { RelationId = "rsr_x", OldRecordingId = "S-tip", NewRecordingId = "F" },
            };

            var withRows = GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, 900, rows);
            var withoutRows = GhostChainWalker.ComputeAllGhostChains(
                RecordingStore.CommittedTrees, 900, new List<RecordingSupersedeRelation>());

            Assert.Equal("F", withRows[StationPid].TipRecordingId);
            Assert.Equal("S-tip", withoutRows[StationPid].TipRecordingId);
        }
    }
}
