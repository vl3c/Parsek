using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Readers of <c>ChildBranchPointId</c> that must not read a breakup-continuous
    /// recording as "the flight ended here". A focused-vessel breakup or decouple
    /// (<c>ParsekFlight.WireBreakupIntoTree</c>) stamps <c>ChildBranchPointId</c> on a
    /// recording that keeps sampling past the split to its own terminal; its branch point
    /// children are only the different-PID stages and debris it dropped. A split that
    /// closes its parent leaves a same-PID child and trims the parent to the branch UT.
    /// </summary>
    [Collection("Sequential")]
    public class BreakupContinuousLeafTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public BreakupContinuousLeafTests()
        {
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        #region Helpers

        internal static RecordingTree MakeTree(string treeId, Recording[] recordings,
            BranchPoint[] branchPoints)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                RootRecordingId = recordings.Length > 0 ? recordings[0].RecordingId : ""
            };
            for (int i = 0; i < recordings.Length; i++)
            {
                recordings[i].TreeId = treeId;
                tree.Recordings[recordings[i].RecordingId] = recordings[i];
            }
            if (branchPoints != null)
                tree.BranchPoints.AddRange(branchPoints);
            return tree;
        }

        internal static Recording MakeRecording(string id, uint vesselPid,
            double startUT, double endUT,
            TerminalState? terminal = null,
            string parentBpId = null, string childBpId = null,
            bool isDebris = false)
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBpId,
                ChildBranchPointId = childBpId,
                IsDebris = isDebris,
                Points = new List<TrajectoryPoint>()
            };
        }

        internal static BranchPoint MakeBranchPoint(string id, BranchPointType type,
            double ut, uint targetVesselPid, string[] parentRecIds, string[] childRecIds)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                TargetVesselPersistentId = targetVesselPid,
                ParentRecordingIds = new List<string>(parentRecIds),
                ChildRecordingIds = new List<string>(childRecIds)
            };
        }

        /// <summary>
        /// A (pid 50) docks to the pre-existing station S (pid 100); the merged craft M
        /// (pid 100) then decouples a spent stage D (debris, pid 200) through the crash
        /// coalescer, so M carries ChildBranchPointId = the decouple and keeps flying to
        /// <paramref name="mergedEndUT"/> with <paramref name="mergedTerminal"/>.
        /// </summary>
        static RecordingTree DockThenDecoupleTree(
            TerminalState mergedTerminal, double mergedEndUT,
            TerminalState debrisTerminal, double debrisEndUT)
        {
            var a = MakeRecording("A", 50, 1000, 1060, childBpId: "bp-dock");
            var m = MakeRecording("M", 100, 1060, mergedEndUT, terminal: mergedTerminal,
                parentBpId: "bp-dock", childBpId: "bp-decouple");
            var d = MakeRecording("D", 200, 1100, debrisEndUT, terminal: debrisTerminal,
                parentBpId: "bp-decouple", isDebris: true);
            var dock = MakeBranchPoint("bp-dock", BranchPointType.Dock, 1060, 100,
                new[] { "A" }, new[] { "M" });
            var decouple = MakeBranchPoint("bp-decouple", BranchPointType.JointBreak, 1100, 0,
                new[] { "M" }, new[] { "D" });
            decouple.SplitCause = "DECOUPLE";
            decouple.CoalesceWindow = 0.5;
            return MakeTree("tree-dock", new[] { a, m, d }, new[] { dock, decouple });
        }

        #endregion

        #region GhostChainWalker.IsTreeFullyTerminated

        [Fact]
        public void IsTreeFullyTerminated_ContinuedParentOrbiting_OnlyDebrisDestroyed_False()
        {
            var tree = DockThenDecoupleTree(TerminalState.Orbiting, 1500, TerminalState.Destroyed, 1200);

            Assert.False(GhostChainWalker.IsTreeFullyTerminated(tree));
        }

        [Fact]
        public void IsTreeFullyTerminated_ContinuedParentDestroyedLater_AllTerminated_True()
        {
            var tree = DockThenDecoupleTree(TerminalState.Destroyed, 1500, TerminalState.Destroyed, 1200);

            Assert.True(GhostChainWalker.IsTreeFullyTerminated(tree));
        }

        [Fact]
        public void IsTreeFullyTerminated_ClosedSplitParent_SamePidChildDecides()
        {
            // An undock closes its parent: the parent has no terminal of its own and the
            // same-PID child is the vessel's leaf.
            var p = MakeRecording("P", 50, 1000, 1100, childBpId: "bp-undock");
            var c = MakeRecording("C", 50, 1100, 1300, terminal: TerminalState.Destroyed,
                parentBpId: "bp-undock");
            var o = MakeRecording("O", 60, 1100, 1300, terminal: TerminalState.Recovered,
                parentBpId: "bp-undock");
            var undock = MakeBranchPoint("bp-undock", BranchPointType.Undock, 1100, 0,
                new[] { "P" }, new[] { "C", "O" });
            var tree = MakeTree("tree-undock", new[] { p, c, o }, new[] { undock });

            Assert.True(GhostChainWalker.IsTreeFullyTerminated(tree));
        }

        #endregion

        #region GhostChainWalker.ComputeAllGhostChains

        [Fact]
        public void ComputeAllGhostChains_DockThenDecouple_DebrisDestroyed_StationStillClaimed()
        {
            var tree = DockThenDecoupleTree(TerminalState.Orbiting, 1500, TerminalState.Destroyed, 1200);

            var chains = GhostChainWalker.ComputeAllGhostChains(new List<RecordingTree> { tree }, 900);

            Assert.True(chains.ContainsKey(100));
            Assert.Equal("M", chains[100].TipRecordingId);
            Assert.Equal(1500.0, chains[100].SpawnUT);
            Assert.False(chains[100].IsTerminated);
        }

        [Fact]
        public void ComputeAllGhostChains_DockThenDecouple_TipIsMergedCraftNotDebris()
        {
            // The debris outlives nothing here but is still flying at commit: the tree is
            // not fully terminated, and the tip walk must stay on the merged craft.
            var tree = DockThenDecoupleTree(TerminalState.Orbiting, 1500, TerminalState.SubOrbital, 1600);

            var chains = GhostChainWalker.ComputeAllGhostChains(new List<RecordingTree> { tree }, 900);

            Assert.True(chains.ContainsKey(100));
            Assert.Equal("M", chains[100].TipRecordingId);
            Assert.Equal(1500.0, chains[100].SpawnUT);
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("WalkToLeaf") && l.Contains("continued past") && l.Contains("rec=M"));
        }

        [Fact]
        public void ComputeAllGhostChains_DockThenDecouple_MergedCraftDestroyedLater_ChainTerminated()
        {
            var tree = DockThenDecoupleTree(TerminalState.Destroyed, 1500, TerminalState.SubOrbital, 1600);

            var chains = GhostChainWalker.ComputeAllGhostChains(new List<RecordingTree> { tree }, 900);

            Assert.True(chains.ContainsKey(100));
            Assert.Equal("M", chains[100].TipRecordingId);
            Assert.True(chains[100].IsTerminated);
        }

        [Fact]
        public void ComputeAllGhostChains_MergedCraftEndedAtBreakup_StillFollowsSurvivingChild()
        {
            // Mirror: the merged craft broke up and ended AT the split; the surviving
            // controlled piece is still followed as before.
            var a = MakeRecording("A", 50, 1000, 1060, childBpId: "bp-dock");
            var m = MakeRecording("M", 100, 1060, 1100, terminal: TerminalState.Destroyed,
                parentBpId: "bp-dock", childBpId: "bp-breakup");
            var p = MakeRecording("P", 300, 1100, 1500, terminal: TerminalState.Orbiting,
                parentBpId: "bp-breakup");
            var dock = MakeBranchPoint("bp-dock", BranchPointType.Dock, 1060, 100,
                new[] { "A" }, new[] { "M" });
            var breakup = MakeBranchPoint("bp-breakup", BranchPointType.Breakup, 1100, 0,
                new[] { "M" }, new[] { "P" });
            breakup.CoalesceWindow = 0.5;
            var tree = MakeTree("tree-breakup", new[] { a, m, p }, new[] { dock, breakup });

            var chains = GhostChainWalker.ComputeAllGhostChains(new List<RecordingTree> { tree }, 900);

            Assert.True(chains.ContainsKey(100));
            Assert.Equal("P", chains[100].TipRecordingId);
            Assert.False(chains[100].IsTerminated);
        }

        #endregion

        #region Live-tree fixtures

        /// <summary>
        /// A live tree: RA (pid 100) staged a probe core through the crash coalescer
        /// (JointBreak bp-stage, children RB pid 200 and debris D pid 300) and kept flying;
        /// the player then switched to RB, so RA records in the background with no terminal.
        /// </summary>
        static RecordingTree StagedThenSwitchedTree(TerminalState? raTerminal = null)
        {
            var ra = MakeRecording("RA", 100, 1000, 1200, terminal: raTerminal, childBpId: "bp-stage");
            ra.VesselName = "Kerbal X";
            ra.VesselSnapshot = new ConfigNode("VESSEL");
            var rb = MakeRecording("RB", 200, 1100, 1300, parentBpId: "bp-stage");
            rb.VesselName = "Kerbal X Probe";
            var d = MakeRecording("D", 300, 1100, 1150, terminal: TerminalState.Destroyed,
                parentBpId: "bp-stage", isDebris: true);
            var stage = MakeBranchPoint("bp-stage", BranchPointType.JointBreak, 1100, 0,
                new[] { "RA" }, new[] { "RB", "D" });
            stage.SplitCause = "DECOUPLE";
            var tree = MakeTree("tree-live", new[] { ra, rb, d }, new[] { stage });
            tree.ActiveRecordingId = "RB";
            return tree;
        }

        /// <summary>An undock closed P (pid 100) into its same-PID child C and the other half O.</summary>
        static RecordingTree ClosedUndockTree()
        {
            var p = MakeRecording("P", 100, 1000, 1100, childBpId: "bp-undock");
            var c = MakeRecording("C", 100, 1100, 1300, parentBpId: "bp-undock");
            var o = MakeRecording("O", 150, 1100, 1300, parentBpId: "bp-undock");
            var undock = MakeBranchPoint("bp-undock", BranchPointType.Undock, 1100, 0,
                new[] { "P" }, new[] { "C", "O" });
            return MakeTree("tree-undock", new[] { p, c, o }, new[] { undock });
        }

        /// <summary>
        /// BackgroundRecorder's decouple with the parent destroyed in the same frame:
        /// CloseParentRecording set ExplicitEndUT = branch UT, no same-PID continuation was
        /// created and nothing stamped a terminal. P (pid 500) ended at bp-j.
        /// </summary>
        static RecordingTree ClosedAtSplitNoContinuationTree()
        {
            var p = MakeRecording("P", 500, 1000, 1100, childBpId: "bp-j");
            p.VesselSnapshot = new ConfigNode("VESSEL");
            var f1 = MakeRecording("F1", 501, 1100, 1150, terminal: TerminalState.Destroyed, parentBpId: "bp-j");
            var f2 = MakeRecording("F2", 502, 1100, 1150, terminal: TerminalState.Destroyed, parentBpId: "bp-j");
            var r = MakeRecording("R", 100, 1000, 1300);
            var j = MakeBranchPoint("bp-j", BranchPointType.JointBreak, 1100, 0,
                new[] { "P" }, new[] { "F1", "F2" });
            var tree = MakeTree("tree-closed", new[] { r, p, f1, f2 }, new[] { j });
            tree.ActiveRecordingId = "R";
            return tree;
        }

        #endregion

        #region RecordingTree.IsVesselLineEnd

        [Fact]
        public void IsVesselLineEnd_ClosedAtSplitWithoutContinuation_False()
        {
            var tree = ClosedAtSplitNoContinuationTree();
            var p = tree.Recordings["P"];

            Assert.False(tree.IsVesselLineEnd(p));
            // A committed reader that already holds the recording's terminal may opt in.
            Assert.True(RecordingTree.IsVesselLineEnd(p, tree.Recordings, tree.BranchPoints,
                allowClosedAtBranch: true));
        }

        [Fact]
        public void LiveReaders_ClosedAtSplitWithoutContinuation_StayClosed()
        {
            var tree = ClosedAtSplitNoContinuationTree();

            tree.RebuildBackgroundMap();
            Assert.False(tree.BackgroundMap.ContainsKey(500));

            Assert.True(RecordingTree.AreAllLeavesTerminal(tree, activeVesselDestroyed: true));
            Assert.DoesNotContain(tree.GetSpawnableLeaves(), r => r.RecordingId == "P");
            Assert.DoesNotContain(tree.GetAllLeaves(), r => r.RecordingId == "P");
            Assert.True(ParsekFlight.BackgroundParentAlreadySplit(tree.Recordings["P"], tree));
        }

        [Fact]
        public void IsVesselLineEnd_BreakupContinuousParent_True()
        {
            var tree = StagedThenSwitchedTree();
            Assert.True(tree.IsVesselLineEnd(tree.Recordings["RA"]));
            Assert.True(tree.IsVesselLineEnd(tree.Recordings["RB"]));
        }

        [Fact]
        public void IsVesselLineEnd_ClosedSplitParent_False()
        {
            var tree = ClosedUndockTree();
            Assert.False(tree.IsVesselLineEnd(tree.Recordings["P"]));
            Assert.True(tree.IsVesselLineEnd(tree.Recordings["C"]));
        }

        [Fact]
        public void IsVesselLineEnd_BackgroundDecoupleWithSamePidChild_False()
        {
            // BackgroundRecorder.CloseParentRecording: a JointBreak that closed its parent.
            var tree = StagedThenSwitchedTree();
            var cont = MakeRecording("RA2", 100, 1100, 1400, parentBpId: "bp-stage");
            cont.TreeId = tree.Id;
            tree.Recordings["RA2"] = cont;
            tree.BranchPoints[0].ChildRecordingIds.Add("RA2");
            Assert.False(tree.IsVesselLineEnd(tree.Recordings["RA"]));
        }

        [Fact]
        public void IsVesselLineEnd_DockIntoOtherPid_False()
        {
            // A docked into S and the merged child took S's pid: the line continues in M.
            var a = MakeRecording("A", 50, 1000, 1060, terminal: TerminalState.Docked, childBpId: "bp-dock");
            var m = MakeRecording("M", 100, 1060, 1500, parentBpId: "bp-dock");
            var dock = MakeBranchPoint("bp-dock", BranchPointType.Dock, 1060, 100,
                new[] { "A" }, new[] { "M" });
            var tree = MakeTree("tree-dock-other", new[] { a, m }, new[] { dock });
            Assert.False(tree.IsVesselLineEnd(a));
        }

        [Fact]
        public void IsVesselLineEnd_PostSwitchLaunchParent_True()
        {
            var parent = MakeRecording("P", 100, 1000, 1500, childBpId: "bp-launch");
            var fresh = MakeRecording("F", 600, 1200, 1500, parentBpId: "bp-launch");
            var launch = MakeBranchPoint("bp-launch", BranchPointType.Launch, 1200, 0,
                new[] { "P" }, new[] { "F" });
            var tree = MakeTree("tree-launch", new[] { parent, fresh }, new[] { launch });
            Assert.True(tree.IsVesselLineEnd(parent));
        }

        [Fact]
        public void IsVesselLineEnd_ChainSegmentWithSuccessor_False()
        {
            var tree = StagedThenSwitchedTree();
            var ra = tree.Recordings["RA"];
            ra.ChainId = "chain-ra";
            ra.ChainIndex = 0;
            var next = MakeRecording("RA-1", 100, 1200, 1400);
            next.ChainId = "chain-ra";
            next.ChainIndex = 1;
            next.TreeId = tree.Id;
            tree.Recordings["RA-1"] = next;
            Assert.False(tree.IsVesselLineEnd(ra));
            Assert.True(tree.IsVesselLineEnd(next));
        }

        [Fact]
        public void IsVesselLineEnd_DanglingBranchPoint_False()
        {
            var tree = StagedThenSwitchedTree();
            tree.Recordings["RA"].ChildBranchPointId = "bp-missing";
            Assert.False(tree.IsVesselLineEnd(tree.Recordings["RA"]));
        }

        #endregion

        #region RecordingTree live-tree readers

        [Fact]
        public void RebuildBackgroundMap_KeepsBreakupContinuousParent()
        {
            var tree = StagedThenSwitchedTree();

            tree.RebuildBackgroundMap();

            Assert.True(tree.BackgroundMap.TryGetValue(100, out string recId));
            Assert.Equal("RA", recId);
            Assert.False(tree.BackgroundMap.ContainsKey(200)); // the active recording
        }

        [Fact]
        public void RebuildBackgroundMap_EndedBreakupParent_NotMapped()
        {
            var tree = StagedThenSwitchedTree(raTerminal: TerminalState.Destroyed);

            tree.RebuildBackgroundMap();

            Assert.False(tree.BackgroundMap.ContainsKey(100));
        }

        [Fact]
        public void RebuildBackgroundMap_ClosedSplitParent_NotMapped()
        {
            var tree = ClosedUndockTree();
            tree.ActiveRecordingId = "O";

            tree.RebuildBackgroundMap();

            Assert.True(tree.BackgroundMap.TryGetValue(100, out string recId));
            Assert.Equal("C", recId);
        }

        [Fact]
        public void AreAllLeavesTerminal_ActiveCrashed_BreakupContinuousVesselStillFlying_False()
        {
            var tree = StagedThenSwitchedTree();
            tree.Recordings["RB"].TerminalStateValue = TerminalState.Destroyed;

            Assert.False(RecordingTree.AreAllLeavesTerminal(tree, activeVesselDestroyed: true));
            Assert.Contains(logLines, l => l.Contains("[TreeDestruction]")
                && l.Contains("leaves=3") && l.Contains("alive=1"));
        }

        [Fact]
        public void AreAllLeavesTerminal_BreakupContinuousVesselAlsoEnded_True()
        {
            var tree = StagedThenSwitchedTree(raTerminal: TerminalState.Destroyed);
            tree.Recordings["RB"].TerminalStateValue = TerminalState.Destroyed;

            Assert.True(RecordingTree.AreAllLeavesTerminal(tree, activeVesselDestroyed: true));
        }

        [Fact]
        public void AreAllActiveCrashBlockersDebris_BreakupContinuousVesselStillFlying_False()
        {
            var tree = StagedThenSwitchedTree();

            Assert.False(RecordingTree.AreAllActiveCrashBlockersDebris(tree));
        }

        [Fact]
        public void AreAllActiveCrashBlockersDebris_OnlyLiveDebrisLeft_True()
        {
            var tree = StagedThenSwitchedTree(raTerminal: TerminalState.Destroyed);
            tree.Recordings["D"].TerminalStateValue = null;

            Assert.True(RecordingTree.AreAllActiveCrashBlockersDebris(tree));
        }

        [Fact]
        public void GetSpawnableAndAllLeaves_IncludeBreakupContinuousParent()
        {
            var tree = StagedThenSwitchedTree(raTerminal: TerminalState.Orbiting);

            Assert.Contains(tree.GetSpawnableLeaves(), r => r.RecordingId == "RA");
            Assert.Contains(tree.GetAllLeaves(), r => r.RecordingId == "RA");
        }

        [Fact]
        public void GetAllLeaves_ClosedSplitParent_Excluded()
        {
            var tree = ClosedUndockTree();

            Assert.DoesNotContain(tree.GetAllLeaves(), r => r.RecordingId == "P");
        }

        [Fact]
        public void BuildDefaultVesselDecisions_NonActiveBreakupContinuousParent_GetsDecision()
        {
            var tree = StagedThenSwitchedTree(raTerminal: TerminalState.Orbiting);

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);

            Assert.True(decisions.ContainsKey("RA"));
        }

        #endregion

        #region ParsekFlight live-tree gates

        [Fact]
        public void ShouldAllowFinalEndpointSegmentPhase_StagedActiveFlight_Allowed()
        {
            var tree = StagedThenSwitchedTree();
            tree.ActiveRecordingId = "RA";
            var ra = tree.Recordings["RA"];

            Assert.True(ParsekFlight.ShouldAllowFinalEndpointSegmentPhase(ra, "RA", tree));
            // Without the tree the branch cannot be read and the old refusal stands.
            Assert.False(ParsekFlight.ShouldAllowFinalEndpointSegmentPhase(ra, "RA"));
        }

        [Fact]
        public void ShouldAllowFinalEndpointSegmentPhase_ClosedSplitParent_Refused()
        {
            var tree = ClosedUndockTree();
            tree.ActiveRecordingId = "P";

            Assert.False(ParsekFlight.ShouldAllowFinalEndpointSegmentPhase(
                tree.Recordings["P"], "P", tree));
        }

        [Fact]
        public void BackgroundParentAlreadySplit_BreakupContinuous_False_ClosedSplit_True()
        {
            var staged = StagedThenSwitchedTree();
            Assert.False(ParsekFlight.BackgroundParentAlreadySplit(staged.Recordings["RA"], staged));
            Assert.False(ParsekFlight.BackgroundParentAlreadySplit(staged.Recordings["RB"], staged));

            var closed = ClosedUndockTree();
            Assert.True(ParsekFlight.BackgroundParentAlreadySplit(closed.Recordings["P"], closed));
        }

        #endregion

        #region SwitchSegmentBuilder

        static TrajectoryPoint Boundary(double ut)
        {
            return new TrajectoryPoint { ut = ut, rotation = UnityEngine.Quaternion.identity };
        }

        [Fact]
        public void ResolveSwitchContinuationParent_BreakupContinuousParent_IsTheLeaf()
        {
            var tree = StagedThenSwitchedTree();

            var res = SwitchSegmentBuilder.ResolveSwitchContinuationParent(tree, 100);

            Assert.Equal(SwitchContinuationParentStatus.UniqueTerminalLeafFound, res.Status);
            Assert.Equal("RA", res.TerminalLeafRecordingId);
            Assert.Contains(logLines, l => l.Contains("[SwitchSegmentResolver]")
                && l.Contains("breakup-continuous leaf") && l.Contains("recordingId=RA"));
        }

        [Fact]
        public void ResolveSwitchContinuationParent_ClosedSplit_FollowsSamePidChild()
        {
            var tree = ClosedUndockTree();

            var res = SwitchSegmentBuilder.ResolveSwitchContinuationParent(tree, 100);

            Assert.Equal(SwitchContinuationParentStatus.UniqueTerminalLeafFound, res.Status);
            Assert.Equal("C", res.TerminalLeafRecordingId);
        }

        [Fact]
        public void CreateSwitchContinuationSegment_UnderBreakupContinuousParent_Created()
        {
            var tree = StagedThenSwitchedTree();

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, "RA", focusedVesselPersistentId: 100, focusedVesselName: "Kerbal X",
                switchUT: 1250, entryReason: SwitchSegmentEntryReason.MapSwitchTo,
                intentId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
                newRecordingId: "RA-switch", newBranchPointId: "bp-switch",
                initialBoundaryPointFactory: Boundary);

            Assert.True(result.Created, result.FailureReason);
            Assert.Equal("RA", result.ParentRecordingId);
            Assert.Equal("bp-switch", tree.Recordings["RA"].ChildBranchPointId);
            // The staging branch still lists RA as its parent.
            Assert.Contains("RA", tree.BranchPoints[0].ParentRecordingIds);
        }

        [Fact]
        public void CreateSwitchContinuationSegment_UnderClosedSplitParent_Refused()
        {
            var tree = ClosedUndockTree();

            var result = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                tree, "P", focusedVesselPersistentId: 100, focusedVesselName: "P",
                switchUT: 1250, entryReason: SwitchSegmentEntryReason.MapSwitchTo,
                intentId: Guid.NewGuid(), sessionId: Guid.NewGuid(),
                newRecordingId: "P-switch", newBranchPointId: "bp-switch",
                initialBoundaryPointFactory: Boundary);

            Assert.False(result.Created);
            Assert.Equal("parent-not-terminal-leaf", result.FailureReason);
        }

        #endregion

        #region RecordingStore.CollectSwitchSegmentSubtreeRecordingIds

        [Fact]
        public void CollectSwitchSegmentSubtree_OverwrittenEarlierStaging_StillCollected()
        {
            // Segment S staged twice: bp1 (probe P) and then bp2 (stage Q), which overwrote
            // S.ChildBranchPointId. bp1 still lists S as its parent.
            var s = MakeRecording("S", 100, 1000, 1500, childBpId: "bp2");
            var p = MakeRecording("P", 200, 1100, 1500, parentBpId: "bp1");
            var q = MakeRecording("Q", 300, 1200, 1300, parentBpId: "bp2");
            var bp1 = MakeBranchPoint("bp1", BranchPointType.JointBreak, 1100, 0,
                new[] { "S" }, new[] { "P" });
            var bp2 = MakeBranchPoint("bp2", BranchPointType.JointBreak, 1200, 0,
                new[] { "S" }, new[] { "Q" });
            var tree = MakeTree("tree-seg", new[] { s, p, q }, new[] { bp1, bp2 });
            var session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                TreeId = tree.Id,
                ActiveSegmentRecordingId = "S",
            };

            var ids = RecordingStore.CollectSwitchSegmentSubtreeRecordingIds(tree, session);

            Assert.Contains("S", ids);
            Assert.Contains("Q", ids);
            Assert.Contains("P", ids);
        }

        [Fact]
        public void FindOverwrittenContinuingBranchPointId_RestoresLatestStaging_IgnoresOthers()
        {
            // RA staged twice (bp1 at 1100, bp2 at 1150), then a switch continuation overwrote
            // its link. A GroundPartPlaced branch also lists RA but never carried the link.
            var tree = StagedThenSwitchedTree();
            var bp2 = MakeBranchPoint("bp-stage2", BranchPointType.JointBreak, 1150, 0,
                new[] { "RA" }, new string[0]);
            var ground = MakeBranchPoint("bp-ground", BranchPointType.GroundPartPlaced, 1180, 0,
                new[] { "RA" }, new string[0]);
            tree.BranchPoints.Add(bp2);
            tree.BranchPoints.Add(ground);

            Assert.Equal("bp-stage2",
                RecordingStore.FindOverwrittenContinuingBranchPointId(tree, tree.Recordings["RA"]));
            Assert.Null(RecordingStore.FindOverwrittenContinuingBranchPointId(tree, tree.Recordings["RB"]));
        }

        [Fact]
        public void CollectSwitchSegmentSubtree_DoesNotClimbToParent()
        {
            var tree = ClosedUndockTree();
            var session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                TreeId = tree.Id,
                ActiveSegmentRecordingId = "C",
            };

            var ids = RecordingStore.CollectSwitchSegmentSubtreeRecordingIds(tree, session);

            Assert.Contains("C", ids);
            Assert.DoesNotContain("P", ids);
            Assert.DoesNotContain("O", ids);
        }

        #endregion

        #region MissionEventDigest

        [Fact]
        public void Digest_StagedVesselThatKeptFlying_GetsItsTerminalRow()
        {
            var tree = StagedThenSwitchedTree(raTerminal: TerminalState.Landed);

            var rows = MissionEventDigest.Build(null, tree, null, null, null);

            Assert.Contains(rows, r => r.SubjectName == "Kerbal X"
                && r.Verb == MissionCompositionBuilder.TerminalName(TerminalState.Landed));
        }

        [Fact]
        public void Digest_VesselDestroyedAtItsBreakup_GetsItsTerminalRow()
        {
            // A crash: the breakup and the end share a UT. The line still ends here.
            var tree = StagedThenSwitchedTree(raTerminal: TerminalState.Destroyed);
            tree.Recordings["RA"].ExplicitEndUT = 1100;
            tree.BranchPoints[0].Type = BranchPointType.Breakup;

            var rows = MissionEventDigest.Build(null, tree, null, null, null);

            Assert.Contains(rows, r => r.SubjectName == "Kerbal X"
                && r.Verb == MissionCompositionBuilder.TerminalName(TerminalState.Destroyed));
        }

        [Fact]
        public void Digest_ClosedSplitParent_NoRowOfItsOwn()
        {
            var tree = ClosedUndockTree();
            tree.Recordings["P"].VesselName = "Parent";
            tree.Recordings["P"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["C"].VesselName = "Continuation";
            tree.Recordings["C"].TerminalStateValue = TerminalState.Orbiting;

            var rows = MissionEventDigest.Build(null, tree, null, null, null);

            Assert.DoesNotContain(rows, r => r.SubjectName == "Parent"
                && r.Verb == MissionCompositionBuilder.TerminalName(TerminalState.Orbiting));
            Assert.Contains(rows, r => r.SubjectName == "Continuation"
                && r.Verb == MissionCompositionBuilder.TerminalName(TerminalState.Orbiting));
        }

        #endregion

        #region TimelineBuilder

        static List<Recording> StagedTreeChildRecordings(TerminalState childTerminal)
        {
            // Root R undocks (closing) into C (same pid) and O; C then stages debris D
            // through the coalescer and keeps flying to its own terminal.
            var r = MakeRecording("R", 100, 1000, 1100, childBpId: "bp-undock");
            r.VesselName = "Root";
            var c = MakeRecording("C", 100, 1100, 1500, terminal: childTerminal,
                parentBpId: "bp-undock", childBpId: "bp-stage");
            c.VesselName = "Core";
            var o = MakeRecording("O", 150, 1100, 1300, terminal: TerminalState.Destroyed,
                parentBpId: "bp-undock");
            o.VesselName = "Other";
            var d = MakeRecording("D", 300, 1200, 1250, terminal: TerminalState.Destroyed,
                parentBpId: "bp-stage", isDebris: true);
            foreach (var rec in new[] { r, c, o, d })
                rec.TreeId = "tree-tl";
            return new List<Recording> { r, c, o, d };
        }

        static List<TimelineEntry> BuildTimeline(List<Recording> recs)
        {
            return TimelineBuilder.Build(recs, new List<GameAction>(), new List<Milestone>(), _ => true);
        }

        [Fact]
        public void Timeline_TreeChildThatStagedAndKeptFlying_HasSpawnRow()
        {
            var entries = BuildTimeline(StagedTreeChildRecordings(TerminalState.Orbiting));

            Assert.Contains(entries, e => e.Type == TimelineEntryType.VesselSpawn && e.RecordingId == "C");
        }

        [Fact]
        public void Timeline_TreeChildAbsorbedByDock_NoSpawnRow()
        {
            var entries = BuildTimeline(StagedTreeChildRecordings(TerminalState.Docked));

            Assert.DoesNotContain(entries, e => e.Type == TimelineEntryType.VesselSpawn && e.RecordingId == "C");
        }

        [Fact]
        public void Timeline_IsBreakupContinuousSpawnLeaf_Gates()
        {
            var rec = MakeRecording("X", 100, 0, 10, terminal: TerminalState.Landed, childBpId: "bp");
            Assert.True(TimelineBuilder.IsBreakupContinuousSpawnLeaf(rec));
            rec.IsDebris = true;
            Assert.False(TimelineBuilder.IsBreakupContinuousSpawnLeaf(rec));
            rec.IsDebris = false;
            rec.TerminalStateValue = TerminalState.Recovered;
            Assert.False(TimelineBuilder.IsBreakupContinuousSpawnLeaf(rec));
            rec.TerminalStateValue = null;
            Assert.False(TimelineBuilder.IsBreakupContinuousSpawnLeaf(rec));
        }

        #endregion

        #region UnfinishedFlightClassifier.IsNonBlockingDownstreamBranch edges

        [Fact]
        public void IsNonBlockingDownstreamBranch_Edges()
        {
            var tree = StagedThenSwitchedTree();
            var ra = tree.Recordings["RA"];
            var bp = tree.BranchPoints[0];

            // RB is a controllable child: blocks.
            Assert.False(UnfinishedFlightClassifier.IsNonBlockingDownstreamBranch(bp, ra, tree));

            // Only the debris child: non-blocking, for Breakup as for JointBreak.
            bp.ChildRecordingIds.Remove("RB");
            Assert.True(UnfinishedFlightClassifier.IsNonBlockingDownstreamBranch(bp, ra, tree));
            bp.Type = BranchPointType.Breakup;
            Assert.True(UnfinishedFlightClassifier.IsNonBlockingDownstreamBranch(bp, ra, tree));

            // A debris child sharing the parent's pid is a continuation: blocks.
            tree.Recordings["D"].VesselPersistentId = 100;
            Assert.False(UnfinishedFlightClassifier.IsNonBlockingDownstreamBranch(bp, ra, tree));
            tree.Recordings["D"].VesselPersistentId = 300;

            // A child missing from the tree cannot be shown to be debris: blocks.
            bp.ChildRecordingIds.Add("missing");
            Assert.False(UnfinishedFlightClassifier.IsNonBlockingDownstreamBranch(bp, ra, tree));

            // No recorded child at all: blocks, as before.
            bp.ChildRecordingIds.Clear();
            Assert.False(UnfinishedFlightClassifier.IsNonBlockingDownstreamBranch(bp, ra, tree));

            // Other types block; Launch never does.
            bp.Type = BranchPointType.Undock;
            Assert.False(UnfinishedFlightClassifier.IsNonBlockingDownstreamBranch(bp, ra, tree));
            bp.Type = BranchPointType.Launch;
            Assert.True(UnfinishedFlightClassifier.IsNonBlockingDownstreamBranch(bp, ra, tree));
        }

        [Fact]
        public void HasBlockingDownstreamBranch_NoTree_FallsBackToTheChildLink()
        {
            var rec = MakeRecording("X", 100, 0, 10, childBpId: "bp-any");
            Assert.True(UnfinishedFlightClassifier.HasBlockingDownstreamBranch(
                rec, rec, "bp-rp", null, out string blocking));
            Assert.Equal("bp-any", blocking);
        }

        [Fact]
        public void HasBlockingDownstreamBranch_RewindBranchMissing_EveryParentedBranchCounts()
        {
            // The rewind point's branch is not in the tree: fail closed, every branch the
            // tip parents is treated as downstream.
            var tree = StagedThenSwitchedTree();
            var ra = tree.Recordings["RA"];
            Assert.True(UnfinishedFlightClassifier.HasBlockingDownstreamBranch(
                ra, ra, "bp-not-in-tree", tree, out string blocking));
            Assert.Equal("bp-stage", blocking);
        }

        #endregion

        #region MissionCrossTreeDock journey        #region MissionCrossTreeDock journey

        static RecordingTree ControllerTreeWithLaunchBranch(double stackEndUT)
        {
            // The merged stack AB never undocks; the player switches to an unrelated
            // outsider X (pid 600), which stamps a post-switch Launch branch on AB.
            var ta = CrossTreeDockFixture.ControllerTree(withUndock: false);
            var ab = ta.Recordings["AB"];
            ab.ExplicitEndUT = stackEndUT;
            ab.ChildBranchPointId = "launchbp";
            var x = CrossTreeDockFixture.Rec("X", 600, "dddddddddddddddddddddddddddddddd", "CX", 0,
                250, 700, parentBp: "launchbp");
            x.TreeId = ta.Id;
            ta.Recordings["X"] = x;
            ta.BranchPoints.Add(CrossTreeDockFixture.BP("launchbp", BranchPointType.Launch, 250,
                new[] { "AB" }, new[] { "X" }));
            return ta;
        }

        [Fact]
        public void Journey_StackContinuedPastLaunchBranch_StaysOnStack()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = ControllerTreeWithLaunchBranch(stackEndUT: 500);
            var link = Assert.Single(MissionCrossTreeDock.FindLinks(tb, new List<RecordingTree> { tb, ta }));

            bool savedSuppress = MissionCrossTreeDock.SuppressLogging;
            MissionCrossTreeDock.SuppressLogging = false;
            List<string> journey;
            try
            {
                journey = MissionCrossTreeDock.ComputePartnerJourneyLegIds(ta, link);
            }
            finally
            {
                MissionCrossTreeDock.SuppressLogging = savedSuppress;
            }

            Assert.Equal(new[] { "AB" }, journey);
            Assert.Contains(logLines, l => l.Contains("[Mission]")
                && l.Contains("journey stays on rec=AB") && l.Contains("type=Launch"));
        }

        [Fact]
        public void Journey_StackEndedAtBranch_StillFollowsControlledChild()
        {
            // Mirror: a stack that ended AT the branch point keeps the old fallback.
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = ControllerTreeWithLaunchBranch(stackEndUT: 250);
            var link = Assert.Single(MissionCrossTreeDock.FindLinks(tb, new List<RecordingTree> { tb, ta }));

            var journey = MissionCrossTreeDock.ComputePartnerJourneyLegIds(ta, link);

            Assert.Equal(new[] { "AB", "X" }, journey);
        }

        #endregion
    }
}
