using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo UNDOCK-BRANCH-POINT-MISSING-SPLIT-CAUSE: the focused undock path wrote its Undock
    /// branch point with no SplitCause while the background recorder (and the coalescer for a
    /// pre-attached port) write "UNDOCK". The focused path now writes it too; these cells build
    /// the branch point through the production factory and carry it through the tree codec and
    /// the Missions label, the shape a saved flight takes.
    /// </summary>
    public class UndockSplitCauseTests
    {
        [Fact]
        public void FocusedUndockBranchPoint_CarriesUndockCause_ThroughTheTreeCodec()
        {
            var (bp, _, _) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent", treeId: "tree", branchUT: 250.5,
                branchType: BranchPointType.Undock,
                activeVesselPid: 1000u, activeVesselName: "Tug",
                backgroundVesselPid: 2000u, backgroundVesselName: "Depot");

            Assert.Equal("UNDOCK", bp.SplitCause);
            Assert.Equal(0u, bp.DecouplerPartId);

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);
            Assert.Equal("UNDOCK", node.GetValue("splitCause"));
            BranchPoint loaded = RecordingTree.LoadBranchPointFrom(node);
            Assert.Equal(BranchPointType.Undock, loaded.Type);
            Assert.Equal("UNDOCK", loaded.SplitCause);
            Assert.Equal("Undocked", MissionCompositionBuilder.BranchEventName(loaded.Type, loaded.SplitCause));
        }

        [Fact]
        public void FocusedEvaBranchPoint_KeepsNoCause()
        {
            var (bp, _, _) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent", treeId: "tree", branchUT: 10.0,
                branchType: BranchPointType.EVA,
                activeVesselPid: 1u, activeVesselName: "Jeb",
                backgroundVesselPid: 2u, backgroundVesselName: "Pod",
                evaCrewName: "Jebediah Kerman", evaVesselPid: 1u);

            Assert.Null(bp.SplitCause);
            Assert.Null(ParsekFlight.ForegroundSplitCauseFor(BranchPointType.EVA));
            Assert.Equal("UNDOCK", ParsekFlight.ForegroundSplitCauseFor(BranchPointType.Undock));
        }

        [Fact]
        public void UndockedPortPartId_ReachesTheBranchPoint()
        {
            // OnPartUndock names the port, OnVesselsUndocking carries it into the deferred
            // branch, and CreateSplitBranch stamps it on the Undock branch point.
            string brancher = Prepared("ParsekFlightSplitBrancher.cs");
            Assert.Contains("pendingUndockPartPid = undockedPartPid;", brancher);
            Assert.Contains("host.DeferredUndockBranch(oldPid, newPid, undockRootPartSeed, undockPartPid)", brancher);
            Assert.Contains("bp.DecouplerPartId = undockedPartPid;", brancher);
            Assert.Contains("undockedPartPid: undockedPartPid", Prepared("ParsekFlight.SplitBranching.cs"));
        }

        private static string Prepared(string file)
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(
                Path.Combine(repo, "Source", "Parsek", "Controllers", file)));
        }
    }
}
