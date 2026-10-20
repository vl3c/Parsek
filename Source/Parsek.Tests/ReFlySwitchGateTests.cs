using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ReFlySwitchGateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ReFlySwitchGateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ReFlySwitchGate.ResetForTesting();
        }

        public void Dispose()
        {
            ReFlySwitchGate.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static Recording Rec(string id, uint pid, string eva = null, string chainId = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = pid,
                EvaCrewName = eva,
                ChainId = chainId,
                TreeId = "tree-1",
            };
        }

        private static BranchPoint Bp(string id, BranchPointType type, string[] parents, string[] children)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
            };
        }

        // Session tree: the provisional P (vessel 100) decouples a probe D (200) and keeps
        // flying as P2 (100); P2 puts Jeb (300) out on EVA and continues as V (100); Jeb
        // places a ground part G (600) and boards V again as B (100). A chain segment C (100)
        // continues P. U (400) is an unrelated vessel of the tree, and its own EVA kerbal
        // Bill (500) is foreign crew.
        private static RecordingTree SessionTree()
        {
            var tree = new RecordingTree { Id = "tree-1" };
            foreach (var r in new[]
            {
                Rec("P", 100u, chainId: "chain-p"), Rec("C", 100u, chainId: "chain-p"),
                Rec("P2", 100u), Rec("D", 200u), Rec("V", 100u), Rec("K", 300u, eva: "Jeb"),
                Rec("G", 600u), Rec("B", 100u), Rec("U", 400u), Rec("UK", 500u, eva: "Bill"),
            })
                tree.Recordings[r.RecordingId] = r;
            tree.BranchPoints.Add(Bp("bp-split", BranchPointType.JointBreak, new[] { "P" }, new[] { "P2", "D" }));
            tree.BranchPoints.Add(Bp("bp-eva", BranchPointType.EVA, new[] { "P2" }, new[] { "V", "K" }));
            tree.BranchPoints.Add(Bp("bp-ground", BranchPointType.GroundPartPlaced, new[] { "K" }, new[] { "G" }));
            tree.BranchPoints.Add(Bp("bp-board", BranchPointType.Board, new[] { "V", "K" }, new[] { "B" }));
            tree.BranchPoints.Add(Bp("bp-u-eva", BranchPointType.EVA, new[] { "U" }, new[] { "UK" }));
            return tree;
        }

        [Theory]
        [InlineData(false, 200u, true, "noSession")]
        [InlineData(false, 0u, false, "noSession")]
        [InlineData(true, 0u, true, "targetUnknown")]
        [InlineData(true, 100u, true, "sessionVessel")]
        [InlineData(true, 300u, true, "sessionVessel")]
        [InlineData(true, 200u, true, "notSessionVessel")]
        [InlineData(true, 200u, false, "sessionVesselUnresolved")]
        public void Decide_TruthTable(bool markerLive, uint target, bool haveAllowedSet, string reason)
        {
            ICollection<uint> allowed = haveAllowedSet ? new HashSet<uint> { 100u, 300u } : new HashSet<uint>();

            ReFlySwitchDecision d = ReFlySwitchGate.Decide(markerLive, target, allowed, ReFlySwitchGate.PathVesselCycle);

            Assert.Equal(reason, d.Reason);
            Assert.Equal(reason == "noSession" || reason == "sessionVessel", d.Allowed);
        }

        [Fact]
        public void ComputeAllowedPids_ReFlownVesselAndOwnEvaCrewOnly()
        {
            HashSet<uint> allowed = ReFlySwitchGate.ComputeAllowedPids(SessionTree(), "P");

            Assert.Equal(new HashSet<uint> { 100u, 300u }, allowed);
        }

        [Fact]
        public void ComputeAllowedPids_VesselSplitOffByTheReFly_IsNotAllowed()
        {
            HashSet<uint> allowed = ReFlySwitchGate.ComputeAllowedPids(SessionTree(), "P");

            Assert.DoesNotContain(200u, allowed);
            Assert.DoesNotContain(600u, allowed);
            Assert.DoesNotContain(400u, allowed);
            Assert.DoesNotContain(500u, allowed);
        }

        [Fact]
        public void ComputeAllowedPids_GrowsAsTheSessionAddsAnEva()
        {
            var tree = new RecordingTree { Id = "tree-1" };
            tree.Recordings["P"] = Rec("P", 100u);
            Assert.Equal(new HashSet<uint> { 100u }, ReFlySwitchGate.ComputeAllowedPids(tree, "P"));

            tree.Recordings["V"] = Rec("V", 100u);
            tree.Recordings["K"] = Rec("K", 300u, eva: "Jeb");
            tree.BranchPoints.Add(Bp("bp-eva", BranchPointType.EVA, new[] { "P" }, new[] { "V", "K" }));

            Assert.Equal(new HashSet<uint> { 100u, 300u }, ReFlySwitchGate.ComputeAllowedPids(tree, "P"));
        }

        [Fact]
        public void ComputeAllowedPids_SwitchContinuationAndDockChildrenContinueTheVessel()
        {
            var tree = new RecordingTree { Id = "tree-1" };
            tree.Recordings["P"] = Rec("P", 100u);
            tree.Recordings["S"] = Rec("S", 100u);
            tree.Recordings["M"] = Rec("M", 700u);
            tree.BranchPoints.Add(Bp("bp-sw", BranchPointType.VesselSwitchContinuation, new[] { "P" }, new[] { "S" }));
            tree.BranchPoints.Add(Bp("bp-dock", BranchPointType.Dock, new[] { "S" }, new[] { "M" }));

            Assert.Equal(new HashSet<uint> { 100u, 700u }, ReFlySwitchGate.ComputeAllowedPids(tree, "P"));
        }

        [Fact]
        public void ComputeAllowedPids_MissingStartOrTree_IsEmpty()
        {
            Assert.Empty(ReFlySwitchGate.ComputeAllowedPids(null, "P"));
            Assert.Empty(ReFlySwitchGate.ComputeAllowedPids(SessionTree(), "missing"));
            Assert.Empty(ReFlySwitchGate.ComputeAllowedPids(SessionTree(), null));
        }

        [Fact]
        public void FirstAllowedIndex_SkipsRefusedCandidates()
        {
            var allowed = new HashSet<uint> { 100u, 300u };

            Assert.Equal(1, ReFlySwitchGate.FirstAllowedIndex(new List<uint> { 200u, 300u, 100u }, allowed));
            Assert.Equal(-1, ReFlySwitchGate.FirstAllowedIndex(new List<uint> { 200u, 400u }, allowed));
            Assert.Equal(-1, ReFlySwitchGate.FirstAllowedIndex(new List<uint>(), allowed));
        }

        private void InstallSession()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-1",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = "P",
            };
            RecordingTree tree = SessionTree();
            ReFlySwitchGate.LiveSessionProviderForTesting = () =>
                new ReFlySwitchGate.LiveSession { Marker = marker, Tree = tree };
        }

        [Fact]
        public void IsRefused_SplitOffProbe_LogsDesignRefusalLine()
        {
            InstallSession();

            string reason;
            bool refused = ReFlySwitchGate.IsRefused(200u, ReFlySwitchGate.PathMapSwitchTo, true, out reason);

            Assert.True(refused);
            Assert.Equal("notSessionVessel", reason);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("Switch refused sess=sess-1 path=mapSwitchTo target=200 reason=notSessionVessel"));
        }

        [Fact]
        public void IsRefused_OwnEvaKerbal_IsAllowedAndNotLoggedAsRefusal()
        {
            InstallSession();

            Assert.False(ReFlySwitchGate.IsRefused(300u, ReFlySwitchGate.PathVesselCycle));
            Assert.False(ReFlySwitchGate.IsRefused(100u, ReFlySwitchGate.PathTrackingStationFly));
            Assert.DoesNotContain(logLines, l => l.Contains("Switch refused"));
        }

        [Fact]
        public void IsRefused_NoSession_AllowsAndLogsNothing()
        {
            ReFlySwitchGate.LiveSessionProviderForTesting = () => new ReFlySwitchGate.LiveSession();

            Assert.False(ReFlySwitchGate.IsRefused(200u, ReFlySwitchGate.PathKscMarkerFly));
            Assert.DoesNotContain(logLines, l => l.Contains("[ReFlySession]"));
        }

        [Fact]
        public void IsRefused_SilentMirror_RefusesWithoutALine()
        {
            InstallSession();

            Assert.True(ReFlySwitchGate.IsRefused(400u, ReFlySwitchGate.PathTrackingStationFly, log: false));
            Assert.DoesNotContain(logLines, l => l.Contains("Switch refused"));
        }

        [Fact]
        public void VesselCyclePatch_TargetsStockVesselSwitchingUpdate()
        {
            var attrs = typeof(VesselCycleReFlySwitchPatch).GetCustomAttributes(typeof(HarmonyPatch), inherit: false);
            Assert.NotEmpty(attrs);

            MethodInfo update = typeof(VesselSwitching).GetMethod(
                "Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(update);

            MethodInfo prefix = typeof(VesselCycleReFlySwitchPatch).GetMethod(
                "Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(prefix);
            Assert.Equal(typeof(bool), prefix.ReturnType);
        }

        [Fact]
        public void KscMarkerAndTrackingStationFlyPrefixes_CanSkipTheOriginal()
        {
            MethodInfo ksc = typeof(KscVesselMarkerFlyPatch).GetMethod(
                "Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo ts = typeof(GhostTrackingFlyPatch).GetMethod(
                "Prefix", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.Equal(typeof(bool), ksc.ReturnType);
            Assert.Equal(typeof(bool), ts.ReturnType);
        }
    }
}
