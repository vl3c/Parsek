using System;
using System.Collections.Generic;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    // The dock branch point's stamped partner launch guid (BranchPoint.TargetVesselGuid):
    // capture, codec, and the three claim readers (GhostChainWalker, MissionCrossTreeDock,
    // DockEventGraph). The gap it closes: when the claimer absorbs the partner, no recording
    // in the claiming tree carries the partner's craft-baked pid, so the claim resolved no
    // launch guid and pooled with a relaunch of the same craft (which reuses the freed pid).
    [Collection("Sequential")]
    public class DockPartnerLaunchGuidTests : IDisposable
    {
        private const string GuidHub1 = "11111111111111111111111111111111";
        private const string GuidHub2 = "22222222222222222222222222222222";
        private const string GuidK1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1";
        private const string GuidK2 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2";

        private readonly List<string> logLines = new List<string>();

        public DockPartnerLaunchGuidTests()
        {
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            DockEventGraph.SuppressLogging = false;
            MissionCrossTreeDock.SuppressLogging = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            DockEventGraph.SuppressLogging = false;
            MissionCrossTreeDock.SuppressLogging = false;
        }

        // ---- codec ----

        [Fact]
        public void Codec_TargetVesselGuid_RoundTrips()
        {
            var bp = ScenarioWriter.DockBranch("bp-dock", new[] { "R1" }, "R1-leaf", 1060.0,
                targetVesselPid: 100, targetVesselGuid: GuidHub1);

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);
            var restored = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal(GuidHub1, node.GetValue("targetVesselGuid"));
            Assert.Equal(100u, restored.TargetVesselPersistentId);
            Assert.Equal(GuidHub1, restored.TargetVesselGuid);
        }

        [Fact]
        public void Codec_TargetVesselGuid_AbsentKey_ReadsNull_AndIsNotWritten()
        {
            // An older save (or a couple with no readable partner id) carries no key: it must
            // load as null so every consumer keeps the pid-only fallback, and a null must not
            // write an empty key back.
            var bp = ScenarioWriter.DockBranch("bp-dock", new[] { "R1" }, "R1-leaf", 1060.0,
                targetVesselPid: 100);

            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);
            Assert.Null(node.GetValue("targetVesselGuid"));
            Assert.Equal("100", node.GetValue("targetVesselPid"));

            var restored = RecordingTree.LoadBranchPointFrom(node);
            Assert.Equal(100u, restored.TargetVesselPersistentId);
            Assert.Null(restored.TargetVesselGuid);

            node.AddValue("targetVesselGuid", "");
            Assert.Null(RecordingTree.LoadBranchPointFrom(node).TargetVesselGuid);
        }

        [Fact]
        public void Codec_TreeSaveLoad_CarriesTargetVesselGuid()
        {
            var tree = ScenarioWriter.MaterializeTree(
                new[]
                {
                    new RecordingBuilder("Tanker").WithRecordingId("K1"),
                    new RecordingBuilder("Tanker").WithRecordingId("K1-leaf"),
                },
                branchPoints: new[]
                {
                    ScenarioWriter.DockBranch("bp-dock", new[] { "K1" }, "K1-leaf", 50.0,
                        targetVesselPid: 100, targetVesselGuid: GuidHub1),
                });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            var loaded = RecordingTree.Load(node);

            var bp = Assert.Single(loaded.BranchPoints);
            Assert.Equal(GuidHub1, bp.TargetVesselGuid);
        }

        // ---- capture ----

        [Fact]
        public void ResolveDockPartnerGuidFromEvent_PicksTheSideCarryingThePartnerPid()
        {
            Assert.Equal(GuidHub1, ParsekFlight.ResolveDockPartnerGuidFromEvent(
                100, fromVesselPid: 100, fromVesselGuid: GuidHub1,
                toVesselPid: 50, toVesselGuid: GuidK1));
            Assert.Equal(GuidHub1, ParsekFlight.ResolveDockPartnerGuidFromEvent(
                100, fromVesselPid: 50, fromVesselGuid: GuidK1,
                toVesselPid: 100, toVesselGuid: GuidHub1));
        }

        [Fact]
        public void ResolveDockPartnerGuidFromEvent_NoStampOrNoMatch_ReturnsNull()
        {
            // A zero stamp (EVA grab, self couple) never carries a guid.
            Assert.Null(ParsekFlight.ResolveDockPartnerGuidFromEvent(
                0, 100, GuidHub1, 50, GuidK1));
            // Neither side carries the stamped pid.
            Assert.Null(ParsekFlight.ResolveDockPartnerGuidFromEvent(
                100, 60, GuidK2, 50, GuidK1));
            // The matching side has no readable id.
            Assert.Null(ParsekFlight.ResolveDockPartnerGuidFromEvent(
                100, 100, null, 50, GuidK1));
        }

        [Fact]
        public void BuildMergeBranchData_StampsTheGuidOnlyBesideANonZeroPid()
        {
            var (bp, _) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "K1" }, "tree-k1", 50.0, BranchPointType.Dock,
                mergedVesselPid: 50, mergedVesselName: "Tanker",
                targetVesselPersistentId: 0, branchPartnerPid: 100, branchPartnerGuid: GuidHub1);
            Assert.Equal(100u, bp.TargetVesselPersistentId);
            Assert.Equal(GuidHub1, bp.TargetVesselGuid);

            var (bpNoPid, _) = ParsekFlight.BuildMergeBranchData(
                new List<string> { "K1" }, "tree-k1", 50.0, BranchPointType.Dock,
                mergedVesselPid: 50, mergedVesselName: "Tanker",
                branchPartnerGuid: GuidHub1);
            Assert.Equal(0u, bpNoPid.TargetVesselPersistentId);
            Assert.Null(bpNoPid.TargetVesselGuid);
        }

        // ---- shared claim guid resolution ----

        [Fact]
        public void ResolveClaimLaunchGuid_PrefersStamp_FallsBackToTreePid()
        {
            var tree = WalkerTree("tree-x",
                new[] { Rec("M", 100, 0, 10, guid: GuidHub2) }, new BranchPoint[0]);

            Assert.Equal(GuidHub1,
                MissionCrossTreeDock.ResolveClaimLaunchGuid(tree, 100, GuidHub1));
            Assert.Equal(GuidHub2,
                MissionCrossTreeDock.ResolveClaimLaunchGuid(tree, 100, null));
            Assert.Null(MissionCrossTreeDock.ResolveClaimLaunchGuid(tree, 200, null));
            Assert.Null(MissionCrossTreeDock.ResolveClaimLaunchGuid(tree, 0, GuidHub1));
        }

        // ---- GhostChainWalker: claim guid + latest-launch filter ----

        // Two launches of one hub craft share baked pid 100. Tanker K2 docks to hub launch 2
        // at UT 1100 and the hub survives (merged child carries pid 100 / guid hub2). Tanker K1
        // docks to hub launch 1 at UT 1300 and K1 survives, absorbing the hub (merged child
        // carries K1's pid 50): no recording in K1's tree carries pid 100.
        private static List<RecordingTree> TwoHubLaunchTrees(bool stampGuids)
        {
            var k2 = Rec("K2", 60, 1000, 1100, guid: GuidK2, childBp: "bp-k2");
            var k2Leaf = Rec("K2-leaf", 100, 1100, 1200, guid: GuidHub2, parentBp: "bp-k2");
            var bpK2 = Dock("bp-k2", 1100, 100, "K2", "K2-leaf", stampGuids ? GuidHub2 : null);
            var treeK2 = WalkerTree("tree-k2", new[] { k2, k2Leaf }, new[] { bpK2 });

            var k1 = Rec("K1", 50, 1200, 1300, guid: GuidK1, childBp: "bp-k1");
            var k1Leaf = Rec("K1-leaf", 50, 1300, 1400, guid: GuidK1, parentBp: "bp-k1");
            var bpK1 = Dock("bp-k1", 1300, 100, "K1", "K1-leaf", stampGuids ? GuidHub1 : null);
            var treeK1 = WalkerTree("tree-k1", new[] { k1, k1Leaf }, new[] { bpK1 });

            return new List<RecordingTree> { treeK2, treeK1 };
        }

        [Fact]
        public void Walker_UnstampedAbsorbingClaim_IsPidOnly_AndSuppressesTheOtherLaunchesTip()
        {
            // The pre-fix shape (an older save): K1's claim resolves no guid, so it pools with
            // K2's claim on hub launch 2 and K2-leaf - hub launch 2's real continuation - is
            // suppressed as an intermediate link of a chain that ends on K1's stack.
            var trees = TwoHubLaunchTrees(stampGuids: false);
            var k2Leaf = trees[0].Recordings["K2-leaf"];

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, 900, null);

            Assert.Equal(2, chains[100].Links.Count);
            Assert.Null(chains[100].LaunchGuid);
            Assert.True(GhostChainWalker.IsIntermediateChainLink(chains, k2Leaf));
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("Claim PID=100 tree=tree-k1 bp=bp-k1 resolved no launch guid"));
        }

        [Fact]
        public void Walker_StampedAbsorbingClaim_CarriesTheHubLaunch_AndDoesNotPool()
        {
            var trees = TwoHubLaunchTrees(stampGuids: true);
            var k2Leaf = trees[0].Recordings["K2-leaf"];

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, 900, null);

            var chain = chains[100];
            Assert.Equal(GuidHub1, chain.LaunchGuid);
            var link = Assert.Single(chain.Links);
            Assert.Equal("K1", link.recordingId);
            Assert.Equal(GuidHub1, link.recordedVesselGuid);
            // Hub launch 2's continuation is no longer suppressed by hub launch 1's chain.
            Assert.False(GhostChainWalker.IsIntermediateChainLink(chains, k2Leaf));
            Assert.DoesNotContain(logLines, l => l.Contains("resolved no launch guid"));
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("Vessel PID=100 claimed by")
                && l.Contains("kept latest launch=" + GuidHub1));
        }

        // ---- GhostChainWalker: MergeCrossTreeLinks guid guard ----

        // Tree a: A (pid 200) docks to vessel 100 and survives, so chain 100's tip is A-leaf
        // carrying pid 200 / launch guidA. Tree b: B docks to "vessel 200" and survives
        // (absorbing it), stamping the partner's launch guid on its branch point.
        private static List<RecordingTree> CrossTreeTrees(string guidA, string treeBStamp)
        {
            var a = Rec("A", 200, 1000, 1060, guid: guidA, childBp: "bp-a");
            var aLeaf = Rec("A-leaf", 200, 1060, 1120, guid: guidA, parentBp: "bp-a");
            var bpA = Dock("bp-a", 1060, 100, "A", "A-leaf", GuidHub1);
            var treeA = WalkerTree("tree-a", new[] { a, aLeaf }, new[] { bpA });

            var b = Rec("B", 300, 1200, 1260, guid: GuidK2, childBp: "bp-b");
            var bLeaf = Rec("B-leaf", 300, 1260, 1320, guid: GuidK2, parentBp: "bp-b");
            var bpB = Dock("bp-b", 1260, 200, "B", "B-leaf", treeBStamp);
            var treeB = WalkerTree("tree-b", new[] { b, bLeaf }, new[] { bpB });

            return new List<RecordingTree> { treeA, treeB };
        }

        [Fact]
        public void MergeCrossTreeLinks_SameLaunch_Merges()
        {
            const string guidA = "cccccccccccccccccccccccccccccccc";
            var trees = CrossTreeTrees(guidA, treeBStamp: guidA);

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, 900, null);

            Assert.Single(chains);
            Assert.Equal("B-leaf", chains[100].TipRecordingId);
            Assert.Equal(1320.0, chains[100].SpawnUT);
            Assert.Equal(2, chains[100].Links.Count);
            Assert.DoesNotContain(logLines, l => l.Contains("Cross-tree link refused"));
        }

        [Fact]
        public void MergeCrossTreeLinks_DifferentLaunch_Refused_ChainsStaySeparate()
        {
            // Chain 200 claims a DIFFERENT launch of the craft that reused pid 200. Merging it
            // would move chain 100's tip (and its spawn UT) onto B's stack, a different launch.
            const string guidA = "cccccccccccccccccccccccccccccccc";
            const string guidOtherLaunch = "dddddddddddddddddddddddddddddddd";
            var trees = CrossTreeTrees(guidA, treeBStamp: guidOtherLaunch);

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, 900, null);

            Assert.Equal(2, chains.Count);
            Assert.Equal("A-leaf", chains[100].TipRecordingId);
            Assert.Equal(1120.0, chains[100].SpawnUT);
            Assert.Single(chains[100].Links);
            Assert.Equal("B-leaf", chains[200].TipRecordingId);
            Assert.Equal(guidOtherLaunch, chains[200].LaunchGuid);
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]")
                && l.Contains("Cross-tree link refused: vessel=100 tip=A-leaf (pid=200 launch=" + guidA + ")")
                && l.Contains("(launch=" + guidOtherLaunch + ")"));
        }

        [Fact]
        public void MergeCrossTreeLinks_UnknownChainGuid_KeepsPidOnlyMerge()
        {
            // No stamp and no recording in tree b carries pid 200: the linked chain's launch is
            // unknown, so the merge keeps today's pid-only behavior.
            const string guidA = "cccccccccccccccccccccccccccccccc";
            var trees = CrossTreeTrees(guidA, treeBStamp: null);

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, 900, null);

            Assert.Single(chains);
            Assert.Equal("B-leaf", chains[100].TipRecordingId);
            Assert.DoesNotContain(logLines, l => l.Contains("Cross-tree link refused"));
        }

        [Fact]
        public void MergeCrossTreeLinks_RepeatedCalls_DoNotRespamRefusal()
        {
            const string guidA = "cccccccccccccccccccccccccccccccc";
            const string guidOtherLaunch = "dddddddddddddddddddddddddddddddd";
            var trees = CrossTreeTrees(guidA, treeBStamp: guidOtherLaunch);

            GhostChainWalker.ComputeAllGhostChains(trees, 900, null);
            GhostChainWalker.ComputeAllGhostChains(trees, 900, null);
            GhostChainWalker.ComputeAllGhostChains(trees, 900, null);

            int refusals = 0;
            foreach (var l in logLines)
                if (l.Contains("Cross-tree link refused")) refusals++;
            Assert.Equal(1, refusals);
        }

        // ---- MissionCrossTreeDock / DockEventGraph ----

        [Fact]
        public void FindLinks_AbsorbedPartner_StampedGuidRejectsADifferentLaunch()
        {
            // Controller tree ta: A absorbs B (no undock), so no recording in ta carries B's
            // pid. Partner tree tb holds a DIFFERENT launch of B's craft.
            var tb = CrossTreeDockFixture.PartnerTree(guidB: "cccccccccccccccccccccccccccccccc");
            var ta = CrossTreeDockFixture.ControllerTree(withUndock: false);

            // Unstamped (older save): pid-only, the wrong launch is claimed.
            Assert.Single(MissionCrossTreeDock.FindLinks(tb, new List<RecordingTree> { tb, ta }));

            ta.BranchPoints[0].TargetVesselGuid = CrossTreeDockFixture.GuidB;
            Assert.Empty(MissionCrossTreeDock.FindLinks(tb, new List<RecordingTree> { tb, ta }));
            Assert.Contains(logLines, l => l.Contains("CrossTreeDock: tree=tb links=0")
                && l.Contains("guidRejected=1"));
        }

        [Fact]
        public void FindLinks_AbsorbedPartner_StampedGuidOfTheSameLaunch_StillLinks()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree(withUndock: false);
            ta.BranchPoints[0].TargetVesselGuid = CrossTreeDockFixture.GuidB;

            var link = Assert.Single(MissionCrossTreeDock.FindLinks(tb, new List<RecordingTree> { tb, ta }));
            Assert.Equal("B0", link.ClaimedRecordingId);
        }

        [Fact]
        public void DockEventGraph_AbsorbedPartner_StampedGuidRejectsADifferentLaunch()
        {
            var tb = CrossTreeDockFixture.PartnerTree(guidB: "cccccccccccccccccccccccccccccccc");
            var ta = CrossTreeDockFixture.ControllerTree(withUndock: false);
            var trees = new List<RecordingTree> { tb, ta };

            Assert.Equal(DockPartnerStatus.CrossTree,
                DockEventGraph.Build(trees, null).NodesByBranchPointId["dockbp"].Partner.Status);

            ta.BranchPoints[0].TargetVesselGuid = CrossTreeDockFixture.GuidB;
            var node = DockEventGraph.Build(trees, null).NodesByBranchPointId["dockbp"];
            Assert.Equal(CrossTreeDockFixture.GuidB, node.PartnerStampedGuid);
            Assert.Equal(DockPartnerStatus.GuidRejected, node.Partner.Status);
        }

        // ---- helpers ----

        private static Recording Rec(string id, uint pid, double start, double end,
            string guid = null, string parentBp = null, string childBp = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                ParentBranchPointId = parentBp,
                ChildBranchPointId = childBp,
                Points = new List<TrajectoryPoint>()
            };
        }

        private static BranchPoint Dock(string id, double ut, uint targetPid,
            string parentId, string childId, string targetGuid)
            => ScenarioWriter.DockBranch(id, new[] { parentId }, childId, ut,
                targetVesselPid: targetPid, targetVesselGuid: targetGuid);

        private static RecordingTree WalkerTree(string treeId, Recording[] recs, BranchPoint[] bps)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = recs[0].RecordingId };
            foreach (var r in recs)
            {
                r.TreeId = treeId;
                tree.Recordings[r.RecordingId] = r;
            }
            tree.BranchPoints.AddRange(bps);
            return tree;
        }
    }
}
