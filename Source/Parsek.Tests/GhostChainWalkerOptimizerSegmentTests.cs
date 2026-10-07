using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CHAIN-WALK-STOPS-ON-FIRST-OPTIMIZER-SEGMENT: <see cref="RecordingStore.RunOptimizationPass"/>
    /// (every load and commit) splits a recording at environment / body boundaries into chain
    /// segments; the first keeps the recording id (so it stays the branch point's child) and the
    /// LAST one takes the end state (vessel snapshot, terminal state). The chain walker followed
    /// branch points only, so a claimed vessel whose tip recording was split got the first
    /// segment as its chain tip: no snapshot to spawn from (flight's chain-tip spawn, the
    /// out-of-flight stale-vessel replacement), the wrong spawn UT, and the wrong terminal.
    /// These cells drive the real optimizer pass over a committed dock / undock tree and then
    /// the walker.
    /// </summary>
    [Collection("Sequential")]
    public class GhostChainWalkerOptimizerSegmentTests : IDisposable
    {
        private const string TreeId = "tree-dock-split";
        private const uint TransportPid = 50;
        private const uint StationPid = 100;
        private const uint DepartingPid = 77;
        private const uint DebrisPid = 999;

        private static readonly uint[] TransportParts = { 1, 2, 3 };
        private static readonly uint[] StationParts = { 11, 12 };

        private readonly List<string> logLines = new List<string>();

        public GhostChainWalkerOptimizerSegmentTests()
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

        private static Recording Plain(string id, uint pid, double start, double end,
            string parentBp = null, string childBp = null, TerminalState? terminal = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = TreeId,
                VesselPersistentId = pid,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                ParentBranchPointId = parentBp,
                ChildBranchPointId = childBp,
                TerminalStateValue = terminal,
                Points = new List<TrajectoryPoint>(),
            };
        }

        private static void AddSection(Recording rec, SegmentEnvironment env, double start, double end,
            double altStart, double altEnd)
        {
            rec.Points.Add(new TrajectoryPoint { ut = start, altitude = altStart, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = (start + end) / 2, altitude = (altStart + altEnd) / 2, bodyName = "Kerbin"
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = env, startUT = start, endUT = end,
                frames = new List<TrajectoryPoint>(),
            });
        }

        /// <summary>
        /// A transport F (pid 50) docks to the foreign station C (pid 100, the claim) and later
        /// undocks. When <paramref name="transportDominant"/> the merged vessel keeps the
        /// transport's pid and the station half departs with a new pid (77), else the station
        /// keeps its pid. The station half then flies exo [1100,1300] -> atmo [1300,1500] ->
        /// surface [1500,1700], which the optimizer cuts at 1300 and 1500. With
        /// <paramref name="stageBeforeCut"/> the station drops a stage at 1150 and flies on past
        /// it (a breakup-continuous JointBreak), so its first segment keeps that child branch
        /// point.
        /// </summary>
        private static RecordingTree BuildAndCommit(
            bool transportDominant, TerminalState stationTerminal, bool stageBeforeCut,
            out Recording stationHalf)
        {
            uint mergedPid = transportDominant ? TransportPid : StationPid;
            uint stationHalfPid = transportDominant ? DepartingPid : StationPid;
            uint transportHalfPid = transportDominant ? TransportPid : DepartingPid;

            Recording f = Plain("F", TransportPid, 1000, 1060, childBp: "bp-dock");
            f.VesselSnapshot = Snapshot(TransportParts);
            Recording m = Plain("M", mergedPid, 1060, 1100, parentBp: "bp-dock", childBp: "bp-undock");
            m.VesselSnapshot = Snapshot(TransportParts.Concat(StationParts).ToArray());
            Recording fHalf = Plain("F-half", transportHalfPid, 1100, 1200,
                parentBp: "bp-undock", terminal: TerminalState.Orbiting);
            fHalf.VesselSnapshot = Snapshot(TransportParts);

            stationHalf = new Recording
            {
                RecordingId = "C-half",
                VesselName = "Station",
                TreeId = TreeId,
                VesselPersistentId = stationHalfPid,
                ParentBranchPointId = "bp-undock",
                TerminalStateValue = stationTerminal,
                VesselSnapshot = Snapshot(StationParts),
            };
            AddSection(stationHalf, SegmentEnvironment.ExoBallistic, 1100, 1300, 90000, 70000);
            AddSection(stationHalf, SegmentEnvironment.Atmospheric, 1300, 1500, 69000, 100);
            AddSection(stationHalf, SegmentEnvironment.SurfaceStationary, 1500, 1700, 80, 80);
            stationHalf.Points.Add(new TrajectoryPoint { ut = 1700, altitude = 80, bodyName = "Kerbin" });

            var tree = new RecordingTree { Id = TreeId, TreeName = "Dock split", RootRecordingId = "F" };
            tree.AddOrReplaceRecording(f);
            tree.AddOrReplaceRecording(m);
            tree.AddOrReplaceRecording(fHalf);
            tree.AddOrReplaceRecording(stationHalf);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock", Type = BranchPointType.Dock, UT = 1060,
                TargetVesselPersistentId = StationPid,
                ParentRecordingIds = new List<string> { "F" },
                ChildRecordingIds = new List<string> { "M" },
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-undock", Type = BranchPointType.Undock, UT = 1100,
                ParentRecordingIds = new List<string> { "M" },
                ChildRecordingIds = new List<string> { "F-half", "C-half" },
            });

            Recording debris = null;
            if (stageBeforeCut)
            {
                debris = Plain("stage", DebrisPid, 1150, 1180,
                    parentBp: "bp-stage", terminal: TerminalState.Destroyed);
                tree.AddOrReplaceRecording(debris);
                tree.BranchPoints.Add(new BranchPoint
                {
                    Id = "bp-stage", Type = BranchPointType.JointBreak, UT = 1150,
                    ParentRecordingIds = new List<string> { "C-half" },
                    ChildRecordingIds = new List<string> { "stage" },
                });
                stationHalf.ChildBranchPointId = "bp-stage";
            }

            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddRecordingWithTreeForTesting(f);
            RecordingStore.AddRecordingWithTreeForTesting(m);
            RecordingStore.AddRecordingWithTreeForTesting(fHalf);
            RecordingStore.AddRecordingWithTreeForTesting(stationHalf);
            if (debris != null)
                RecordingStore.AddRecordingWithTreeForTesting(debris);
            return tree;
        }

        /// <summary>
        /// The same dock / undock, but the MERGED recording M is the one the optimizer cuts:
        /// docked, the pair flies exo [1060,1260] -> atmo [1260,1460] and undocks at 1460, so
        /// the undock branch point moves to M's last segment. The station half [1460,1660] is
        /// not cut.
        /// </summary>
        private static RecordingTree BuildMergedSplitAndCommit(bool transportDominant, out Recording merged)
        {
            uint mergedPid = transportDominant ? TransportPid : StationPid;
            uint stationHalfPid = transportDominant ? DepartingPid : StationPid;
            uint transportHalfPid = transportDominant ? TransportPid : DepartingPid;

            Recording f = Plain("F", TransportPid, 1000, 1060, childBp: "bp-dock");
            f.VesselSnapshot = Snapshot(TransportParts);

            merged = new Recording
            {
                RecordingId = "M",
                VesselName = "Docked pair",
                TreeId = TreeId,
                VesselPersistentId = mergedPid,
                ParentBranchPointId = "bp-dock",
                ChildBranchPointId = "bp-undock",
                VesselSnapshot = Snapshot(TransportParts.Concat(StationParts).ToArray()),
            };
            AddSection(merged, SegmentEnvironment.ExoBallistic, 1060, 1260, 90000, 70000);
            AddSection(merged, SegmentEnvironment.Atmospheric, 1260, 1460, 69000, 30000);
            merged.Points.Add(new TrajectoryPoint { ut = 1460, altitude = 30000, bodyName = "Kerbin" });

            Recording fHalf = Plain("F-half", transportHalfPid, 1460, 1560,
                parentBp: "bp-undock", terminal: TerminalState.Orbiting);
            fHalf.VesselSnapshot = Snapshot(TransportParts);
            Recording cHalf = Plain("C-half", stationHalfPid, 1460, 1660,
                parentBp: "bp-undock", terminal: TerminalState.Orbiting);
            cHalf.VesselSnapshot = Snapshot(StationParts);

            var tree = new RecordingTree { Id = TreeId, TreeName = "Merged split", RootRecordingId = "F" };
            tree.AddOrReplaceRecording(f);
            tree.AddOrReplaceRecording(merged);
            tree.AddOrReplaceRecording(fHalf);
            tree.AddOrReplaceRecording(cHalf);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock", Type = BranchPointType.Dock, UT = 1060,
                TargetVesselPersistentId = StationPid,
                ParentRecordingIds = new List<string> { "F" },
                ChildRecordingIds = new List<string> { "M" },
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-undock", Type = BranchPointType.Undock, UT = 1460,
                ParentRecordingIds = new List<string> { "M" },
                ChildRecordingIds = new List<string> { "F-half", "C-half" },
            });

            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddRecordingWithTreeForTesting(f);
            RecordingStore.AddRecordingWithTreeForTesting(merged);
            RecordingStore.AddRecordingWithTreeForTesting(fHalf);
            RecordingStore.AddRecordingWithTreeForTesting(cHalf);
            return tree;
        }

        private static List<Recording> Segments(RecordingTree tree, Recording head)
        {
            return tree.Recordings.Values
                .Where(r => !string.IsNullOrEmpty(head.ChainId) && r.ChainId == head.ChainId)
                .OrderBy(r => r.ChainIndex)
                .ToList();
        }

        // ------------------------------------------------------------ cells

        // catches: the defect. The station half's recording is split three ways; the walk must
        // land on the LAST segment, the one holding the snapshot, so flight's chain-tip spawn
        // and the out-of-flight replacement find the tip by the recording that spawns.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OptimizerSplitStationHalf_TipIsTheSnapshotHoldingLastSegment(bool transportDominant)
        {
            RecordingTree tree = BuildAndCommit(transportDominant, TerminalState.Landed,
                stageBeforeCut: false, stationHalf: out Recording stationHalf);

            RecordingStore.RunOptimizationPass();

            List<Recording> segs = Segments(tree, stationHalf);
            Assert.Equal(3, segs.Count);
            Assert.Same(stationHalf, segs[0]);
            Assert.Null(segs[0].VesselSnapshot);
            Assert.NotNull(segs[2].VesselSnapshot);
            Assert.Equal(TerminalState.Landed, segs[2].TerminalStateValue);

            var chains = GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, 900);

            Assert.True(chains.ContainsKey(StationPid));
            GhostChain chain = chains[StationPid];
            Assert.Equal(segs[2].RecordingId, chain.TipRecordingId);
            Assert.Equal(segs[2].EndUT, chain.SpawnUT);
            Assert.False(chain.IsTerminated);

            // The flight chain-tip spawn and the out-of-flight replacement look the chain up by
            // the recording whose end spawns, which is the snapshot-holding segment.
            Assert.Same(chain, ParsekFlight.FindChainTipForRecording(chains, segs[2]));
            Assert.Null(ParsekFlight.FindChainTipForRecording(chains, segs[0]));
            Assert.Single(ChainTipStaleVessel.FindChainsForTip(chains, segs[2].RecordingId));
            Assert.False(GhostChainWalker.IsIntermediateChainLink(chains, segs[2]));

            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("WalkToLeaf: step") && l.Contains("rec=C-half")
                && l.Contains("segment=" + segs[2].RecordingId)
                && l.Contains("rule=" + GhostChainWalker.RuleOptimizerChainSegment));
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("WalkToLeaf: reached leaf=" + segs[2].RecordingId));
        }

        // catches: the mirror. The station half ends Destroyed; the split moves the terminal to
        // the last segment, so the chain must terminate on it (reading the first segment, which
        // has no terminal, left the chain alive and its vessel ghosted with nothing to spawn).
        [Fact]
        public void OptimizerSplitStationHalf_LastSegmentDestroyed_ChainTerminated()
        {
            RecordingTree tree = BuildAndCommit(transportDominant: true, TerminalState.Destroyed,
                stageBeforeCut: false, stationHalf: out Recording stationHalf);

            RecordingStore.RunOptimizationPass();

            List<Recording> segs = Segments(tree, stationHalf);
            Assert.Equal(3, segs.Count);
            Assert.Null(segs[0].TerminalStateValue);
            Assert.Equal(TerminalState.Destroyed, segs[2].TerminalStateValue);

            var chains = GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, 900);

            Assert.Equal(segs[2].RecordingId, chains[StationPid].TipRecordingId);
            Assert.True(chains[StationPid].IsTerminated);
            Assert.Null(ParsekFlight.FindChainTipForRecording(chains, segs[2]));
        }

        // catches: a first segment that keeps a child branch point the vessel flew past (a stage
        // dropped before the cut): the walk stopped there as "continued past" instead of going
        // on through the chain to the segment the vessel ends in.
        [Fact]
        public void OptimizerSplitAfterAStageDrop_WalkContinuesPastTheStageToTheLastSegment()
        {
            RecordingTree tree = BuildAndCommit(transportDominant: true, TerminalState.Landed,
                stageBeforeCut: true, stationHalf: out Recording stationHalf);

            RecordingStore.RunOptimizationPass();

            List<Recording> segs = Segments(tree, stationHalf);
            Assert.Equal(3, segs.Count);
            Assert.Equal("bp-stage", segs[0].ChildBranchPointId);
            Assert.True(GhostPlaybackLogic.RecordingContinuesPastChildBranch(segs[0], tree));

            var chains = GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, 900);

            Assert.Equal(segs[2].RecordingId, chains[StationPid].TipRecordingId);
            Assert.Equal(segs[2].EndUT, chains[StationPid].SpawnUT);
            Assert.False(chains[StationPid].IsTerminated);
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("continued past") && l.Contains("rec=C-half"));
        }

        // catches: a walk that stops on the segment it hopped to. The merged recording between
        // the dock and the undock is cut, so the undock branch point sits on its LAST segment:
        // the walk must hop there and then carry on through the undock to the station half.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OptimizerSplitMergedRecording_WalkHopsThenContinuesThroughTheUndock(bool transportDominant)
        {
            RecordingTree tree = BuildMergedSplitAndCommit(transportDominant, out Recording merged);

            RecordingStore.RunOptimizationPass();

            List<Recording> segs = Segments(tree, merged);
            Assert.Equal(2, segs.Count);
            Assert.Same(merged, segs[0]);
            Assert.Null(segs[0].ChildBranchPointId);
            Assert.Equal("bp-undock", segs[1].ChildBranchPointId);

            var chains = GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, 900);

            Assert.True(chains.ContainsKey(StationPid));
            Assert.Equal("C-half", chains[StationPid].TipRecordingId);
            Assert.Equal(1660.0, chains[StationPid].SpawnUT);
            Assert.False(chains[StationPid].IsTerminated);
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("WalkToLeaf: step") && l.Contains("rec=M")
                && l.Contains("segment=" + segs[1].RecordingId)
                && l.Contains("rule=" + GhostChainWalker.RuleOptimizerChainSegment));
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("WalkToLeaf: step") && l.Contains("rec=" + segs[1].RecordingId)
                && l.Contains("child=C-half") && l.Contains("via bp=bp-undock"));
        }

        // catches: the unsplit mirror. A tip the optimizer did not cut is unchanged: no chain
        // hop, the tip is the recording itself.
        [Fact]
        public void UnsplitStationHalf_TipUnchanged()
        {
            RecordingTree tree = BuildAndCommit(transportDominant: true, TerminalState.Orbiting,
                stageBeforeCut: false, stationHalf: out Recording stationHalf);
            // One environment only: nothing to cut.
            stationHalf.TrackSections.RemoveRange(1, 2);
            stationHalf.Points.RemoveRange(2, stationHalf.Points.Count - 2);

            RecordingStore.RunOptimizationPass();

            Assert.True(string.IsNullOrEmpty(stationHalf.ChainId));
            var chains = GhostChainWalker.ComputeAllGhostChains(RecordingStore.CommittedTrees, 900);

            Assert.Equal("C-half", chains[StationPid].TipRecordingId);
            Assert.Equal(stationHalf.EndUT, chains[StationPid].SpawnUT);
            Assert.DoesNotContain(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("rule=" + GhostChainWalker.RuleOptimizerChainSegment));
        }

        // catches: the tree-level mirror (#174's skip). Every vessel of the tree ended Destroyed
        // but one recording was cut by the optimizer: its earlier segments carry no terminal and
        // are not leaves, so the tree still counts as fully terminated.
        [Fact]
        public void FullyDestroyedTree_WithASplitRecording_IsFullyTerminated()
        {
            RecordingTree tree = BuildAndCommit(transportDominant: true, TerminalState.Destroyed,
                stageBeforeCut: false, stationHalf: out Recording stationHalf);
            tree.Recordings["F-half"].TerminalStateValue = TerminalState.Destroyed;

            RecordingStore.RunOptimizationPass();

            Assert.Equal(3, Segments(tree, stationHalf).Count);
            Assert.True(GhostChainWalker.IsTreeFullyTerminated(tree));
        }
    }
}
