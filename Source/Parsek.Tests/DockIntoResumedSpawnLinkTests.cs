using System;
using System.Collections.Generic;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    // A dock into a RESUMED Parsek-spawned vessel. The spawn gave the vessel a fresh launch
    // guid G1 and a KSP-unique pid; flying it again resumed its committed recording, which
    // keeps the original launch's guid G0 and is re-pidded to the spawn pid, and a link row
    // G1 -> G0 joins the two guids (VesselNameStore.Links). A later dock into that vessel
    // stamps the live guid G1 on its branch point, so every dock-claim reader that compares
    // guids with the bare GuidsConclusivelyDiffer reads the resumed recording as a different
    // launch. The player sees it after a rewind: the ghost chain that should hold the vessel
    // back until the docking flight's end does not, so the resumed recording spawns its
    // vessel and the docked stack spawns it a second time. The Missions dock rows lose the
    // partner too.
    [Collection("Sequential")]
    public class DockIntoResumedSpawnLinkTests : IDisposable
    {
        private const uint SpawnPid = 500;
        private const uint TankerPid = 60;
        private const uint StationPid = 700;
        // The original launch of the resumed vessel (its recordings keep this guid).
        private const string G0 = "00000000000000000000000000000a00";
        // The fresh guid the Parsek spawn gave the live vessel.
        private const string G1 = "00000000000000000000000000000a01";
        // A guid that continues G1 (for example an undock re-key of the spawned vessel).
        private const string G2 = "00000000000000000000000000000a02";
        // An unrelated launch of the same craft.
        private const string GOther = "00000000000000000000000000000b00";
        private const string GTanker = "000000000000000000000000000000c0";
        private const string GStation = "000000000000000000000000000000d0";

        private readonly List<string> logLines = new List<string>();

        public DockIntoResumedSpawnLinkTests()
        {
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            VesselNameStore.ResetForTesting();
            VesselNameStore.EnsureLaunchIdentityLinkRuleInstalled();
            DockEventGraph.SuppressLogging = false;
            MissionCrossTreeDock.SuppressLogging = false;
        }

        public void Dispose()
        {
            VesselNameStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            DockEventGraph.SuppressLogging = false;
            MissionCrossTreeDock.SuppressLogging = false;
        }

        private static void LinkResume()
            => Assert.True(VesselNameStore.AddLink(G1, G0, VesselIdentityLinkKind.Resume, "test resume"));

        // ---- the predicate ----

        [Fact]
        public void GuidsConclusivelyDifferAllowingLinks_LinkedEitherDirection_DoesNotDiffer()
        {
            LinkResume();
            Assert.True(VesselLaunchIdentity.GuidsConclusivelyDiffer(G1, G0));
            Assert.False(VesselLaunchIdentity.GuidsConclusivelyDifferAllowingLinks(G1, G0));
            Assert.False(VesselLaunchIdentity.GuidsConclusivelyDifferAllowingLinks(G0, G1));
        }

        [Fact]
        public void GuidsConclusivelyDifferAllowingLinks_FollowsALinkChain_BothDirections()
        {
            LinkResume();
            Assert.True(VesselNameStore.AddLink(G2, G1, VesselIdentityLinkKind.UndockReturn, "test undock"));
            Assert.False(VesselLaunchIdentity.GuidsConclusivelyDifferAllowingLinks(G2, G0));
            Assert.False(VesselLaunchIdentity.GuidsConclusivelyDifferAllowingLinks(G0, G2));
        }

        [Fact]
        public void GuidsConclusivelyDifferAllowingLinks_UnlinkedOrUnknown_MatchesTheBarePredicate()
        {
            LinkResume();
            // Another launch of the same craft: no link, still conclusively different.
            Assert.True(VesselLaunchIdentity.GuidsConclusivelyDifferAllowingLinks(GOther, G0));
            Assert.True(VesselLaunchIdentity.GuidsConclusivelyDifferAllowingLinks(G1, GOther));
            // Equal and unknown guids answer as before.
            Assert.False(VesselLaunchIdentity.GuidsConclusivelyDifferAllowingLinks(G0, G0));
            Assert.False(VesselLaunchIdentity.GuidsConclusivelyDifferAllowingLinks(null, G0));
            Assert.False(VesselLaunchIdentity.GuidsConclusivelyDifferAllowingLinks(G1, ""));
        }

        [Fact]
        public void GuidsConclusivelyDifferAllowingLinks_TwoContinuationsOfOneGuid_StillDiffer()
        {
            // Two fresh guids that both continue G0 (two spawns of one recording in two
            // timelines) are not joined to each other: neither continues the other.
            LinkResume();
            Assert.True(VesselNameStore.AddLink(G2, G0, VesselIdentityLinkKind.Spawn, "test respawn"));
            Assert.True(VesselLaunchIdentity.GuidsConclusivelyDifferAllowingLinks(G1, G2));
        }

        // ---- GhostChainWalker: the spawn after a rewind ----

        // Tree a: the resumed recording RA (spawn pid 500, original launch guid G0) ends at
        // UT 2000 in orbit. Tree k: tanker K docks into vessel 500 at UT 2200; the branch point
        // stamps the live guid. K absorbs the vessel (K-leaf carries K's pid) unless
        // partnerSurvives, where the merged stack carries pid 500 and the live guid.
        private static List<RecordingTree> ResumedDockTrees(string stamp, bool partnerSurvives = false)
        {
            var ra = Rec("RA", SpawnPid, 1000, 2000, G0);
            ra.TerminalStateValue = TerminalState.Orbiting;
            var treeA = WalkerTree("tree-a", new[] { ra }, new BranchPoint[0]);

            var k = Rec("K", TankerPid, 2100, 2200, GTanker, childBp: "bp-k");
            var kLeaf = partnerSurvives
                ? Rec("K-leaf", SpawnPid, 2200, 2300, stamp, parentBp: "bp-k")
                : Rec("K-leaf", TankerPid, 2200, 2300, GTanker, parentBp: "bp-k");
            var bp = ScenarioWriter.DockBranch("bp-k", new[] { "K" }, "K-leaf", 2200,
                targetVesselPid: SpawnPid, targetVesselGuid: stamp);
            var treeK = WalkerTree("tree-k", new[] { k, kLeaf }, new[] { bp });
            return new List<RecordingTree> { treeA, treeK };
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Walker_DockIntoResumedSpawn_WithLink_SuppressesTheResumedSpawn(bool partnerSurvives)
        {
            LinkResume();
            var trees = ResumedDockTrees(G1, partnerSurvives);
            var ra = trees[0].Recordings["RA"];

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, 900, null);

            Assert.Equal("K-leaf", chains[SpawnPid].TipRecordingId);
            var (suppressed, reason) = GhostPlaybackLogic.ShouldSuppressSpawnForChain(chains, ra);
            Assert.True(suppressed);
            Assert.Equal("intermediate ghost chain link", reason);
        }

        [Fact]
        public void Walker_DockIntoResumedSpawn_WithoutLink_KeepsTheRefusal()
        {
            // No link row: the stamped guid is another launch, so the resumed recording keeps
            // its own spawn (the #976 rule for two launches of one craft).
            var trees = ResumedDockTrees(G1);
            var ra = trees[0].Recordings["RA"];

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, 900, null);

            Assert.False(GhostPlaybackLogic.ShouldSuppressSpawnForChain(chains, ra).suppressed);
        }

        [Fact]
        public void Walker_DockIntoAnotherLaunchOfTheCraft_WithAnUnrelatedLink_KeepsTheRefusal()
        {
            // A link that joins G1 to G0 says nothing about an unrelated launch of the craft.
            LinkResume();
            var trees = ResumedDockTrees(GOther);
            var ra = trees[0].Recordings["RA"];

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, 900, null);

            Assert.False(GhostPlaybackLogic.ShouldSuppressSpawnForChain(chains, ra).suppressed);
        }

        [Fact]
        public void Walker_LatestLaunchFilter_KeepsLinkedClaimsTogether()
        {
            // Two claims on vessel 500: tree s claims it under the original guid (an unstamped
            // branch point whose pid resolves to the resumed recording), tree k under the live
            // guid. With the link they are one vessel, so neither claim is dropped as a stale
            // launch.
            LinkResume();
            var trees = ResumedDockTrees(G1);
            var s = Rec("S", StationPid, 1500, 1600, GStation, childBp: "bp-s");
            var sLeaf = Rec("S-leaf", StationPid, 1600, 1700, GStation, parentBp: "bp-s");
            var sBg = Rec("S-bg", SpawnPid, 1500, 1600, G0);
            var bpS = ScenarioWriter.DockBranch("bp-s", new[] { "S" }, "S-leaf", 1600,
                targetVesselPid: SpawnPid);
            trees.Add(WalkerTree("tree-s", new[] { s, sLeaf, sBg }, new[] { bpS }));

            var chains = GhostChainWalker.ComputeAllGhostChains(trees, 900, null);

            Assert.Equal(2, chains[SpawnPid].Links.Count);
            Assert.DoesNotContain(logLines, l => l.Contains("stale-launch claim"));
        }

        // Tree a: the resumed vessel RA0 (pid 500, guid G0) docks to station 700 at UT 1500
        // and survives (RA1 carries pid 500 / G0), so chain 700's tip carries pid 500. Tree k
        // then docks into vessel 500 at UT 2200 under the live guid.
        private static List<RecordingTree> CrossTreeResumedTrees()
        {
            var ra0 = Rec("RA0", SpawnPid, 1000, 1500, G0, childBp: "bp-a");
            var ra1 = Rec("RA1", SpawnPid, 1500, 2000, G0, parentBp: "bp-a");
            var bpA = ScenarioWriter.DockBranch("bp-a", new[] { "RA0" }, "RA1", 1500,
                targetVesselPid: StationPid, targetVesselGuid: GStation);
            var treeA = WalkerTree("tree-a", new[] { ra0, ra1 }, new[] { bpA });

            var k = Rec("K", TankerPid, 2100, 2200, GTanker, childBp: "bp-k");
            var kLeaf = Rec("K-leaf", TankerPid, 2200, 2300, GTanker, parentBp: "bp-k");
            var bpK = ScenarioWriter.DockBranch("bp-k", new[] { "K" }, "K-leaf", 2200,
                targetVesselPid: SpawnPid, targetVesselGuid: G1);
            var treeK = WalkerTree("tree-k", new[] { k, kLeaf }, new[] { bpK });
            return new List<RecordingTree> { treeA, treeK };
        }

        [Fact]
        public void Walker_CrossTreeMerge_ThroughALink_Merges()
        {
            LinkResume();
            var chains = GhostChainWalker.ComputeAllGhostChains(CrossTreeResumedTrees(), 900, null);

            Assert.Single(chains);
            Assert.Equal("K-leaf", chains[StationPid].TipRecordingId);
            Assert.Equal(2300.0, chains[StationPid].SpawnUT);
            Assert.DoesNotContain(logLines, l => l.Contains("Cross-tree link refused"));
        }

        [Fact]
        public void Walker_CrossTreeMerge_WithoutLink_StaysRefused()
        {
            var chains = GhostChainWalker.ComputeAllGhostChains(CrossTreeResumedTrees(), 900, null);

            Assert.Equal(2, chains.Count);
            Assert.Equal("RA1", chains[StationPid].TipRecordingId);
            Assert.Contains(logLines, l => l.Contains("[ChainWalker]") && l.Contains("Cross-tree link refused"));
        }

        [Fact]
        public void Walker_ForkContinuesWalkedVessel_AcceptsALinkedGuid()
        {
            // A fork carrying the live guid of the vessel the candidate recorded under its
            // original guid continues the walked vessel; an unlinked guid does not.
            var candidate = Rec("C", SpawnPid, 1000, 2000, G0);
            var fork = Rec("F", SpawnPid, 1500, 2500, G1);
            Assert.False(GhostChainWalker.ForkContinuesWalkedVessel(candidate, fork, null, out _));
            LinkResume();
            Assert.True(GhostChainWalker.ForkContinuesWalkedVessel(candidate, fork, null, out string basis));
            Assert.Equal(GhostChainWalker.SupersedeBasisSamePid, basis);
            fork.RecordedVesselGuid = GOther;
            Assert.False(GhostChainWalker.ForkContinuesWalkedVessel(candidate, fork, null, out _));
        }

        // ---- MissionCrossTreeDock / DockEventGraph ----

        [Fact]
        public void FindLinks_DockIntoResumedSpawn_LinksThroughTheIdentityLink()
        {
            var trees = ResumedDockTrees(G1);
            var treeA = trees[0];

            Assert.Empty(MissionCrossTreeDock.FindLinks(treeA, trees));
            Assert.Contains(logLines, l => l.Contains("CrossTreeDock: tree=tree-a links=0")
                && l.Contains("guidRejected=1"));

            LinkResume();
            var link = Assert.Single(MissionCrossTreeDock.FindLinks(treeA, trees));
            Assert.Equal("RA", link.ClaimedRecordingId);
            Assert.Equal("bp-k", link.LinkId);
            Assert.Equal(G0, link.PartnerLaunchGuid);
        }

        [Fact]
        public void FindLinks_DockIntoAnotherLaunchOfTheCraft_StaysRejected()
        {
            LinkResume();
            var trees = ResumedDockTrees(GOther);
            Assert.Empty(MissionCrossTreeDock.FindLinks(trees[0], trees));
        }

        [Fact]
        public void DockEventGraph_DockIntoResumedSpawn_ResolvesThePartnerThroughTheLink()
        {
            var trees = ResumedDockTrees(G1);

            Assert.Equal(DockPartnerStatus.GuidRejected,
                DockEventGraph.Build(trees, null).NodesByBranchPointId["bp-k"].Partner.Status);

            LinkResume();
            var partner = DockEventGraph.Build(trees, null).NodesByBranchPointId["bp-k"].Partner;
            Assert.Equal(DockPartnerStatus.CrossTree, partner.Status);
            Assert.Equal("RA", partner.PartnerRecordingId);
            Assert.Equal("tree-a", partner.PartnerTreeId);
        }

        [Fact]
        public void FindSameTreeDockClaims_DockIntoResumedSpawnInTheSameTree_ClaimsThroughTheLink()
        {
            // Inside the resumed tree: station W (the active vessel after a switch) docks into
            // the resumed vessel, a background member recorded under G0; the branch point
            // stamps the live guid G1.
            var ra = Rec("RA", SpawnPid, 1000, 1500, G0);
            var w = Rec("W", StationPid, 1000, 1500, GStation, childBp: "bp-w");
            var wLeaf = Rec("W-leaf", StationPid, 1500, 1600, GStation, parentBp: "bp-w");
            var bp = ScenarioWriter.DockBranch("bp-w", new[] { "W" }, "W-leaf", 1500,
                targetVesselPid: SpawnPid, targetVesselGuid: G1);
            var tree = WalkerTree("tree-a", new[] { w, wLeaf, ra }, new[] { bp });

            Assert.Empty(MissionCrossTreeDock.FindSameTreeDockClaims(tree));

            LinkResume();
            var claim = Assert.Single(MissionCrossTreeDock.FindSameTreeDockClaims(tree));
            Assert.Equal("RA", claim.ClaimedRecordingId);

            bp.TargetVesselGuid = GOther;
            Assert.Empty(MissionCrossTreeDock.FindSameTreeDockClaims(tree));
        }

        [Fact]
        public void PartnerJourney_FollowsThePartnerSurvivingAsTheStack_ThroughTheLink()
        {
            // The resumed vessel survives the dock (the merged stack carries pid 500 and the
            // live guid G1) and the stack then splits; the journey must follow the vessel's own
            // continuation, not the first controlled child.
            var ra = Rec("RA", SpawnPid, 1000, 2000, G0);
            ra.TerminalStateValue = TerminalState.Orbiting;
            var treeA = WalkerTree("tree-a", new[] { ra }, new BranchPoint[0]);

            var k = Rec("K", TankerPid, 2100, 2200, GTanker, childBp: "bp-k");
            var stack = Rec("STACK", SpawnPid, 2200, 2400, G1, parentBp: "bp-k", childBp: "bp-u");
            var kAway = Rec("K-away", TankerPid + 1, 2400, 2500, GTanker, parentBp: "bp-u");
            var vCont = Rec("V-cont", SpawnPid, 2400, 2600, G1, parentBp: "bp-u");
            var bpK = ScenarioWriter.DockBranch("bp-k", new[] { "K" }, "STACK", 2200,
                targetVesselPid: SpawnPid, targetVesselGuid: G1);
            var bpU = new BranchPoint
            {
                Id = "bp-u",
                Type = BranchPointType.Undock,
                UT = 2400,
                ParentRecordingIds = new List<string> { "STACK" },
                ChildRecordingIds = new List<string> { "K-away", "V-cont" },
            };
            var treeK = WalkerTree("tree-k", new[] { k, stack, kAway, vCont }, new[] { bpK, bpU });
            var trees = new List<RecordingTree> { treeA, treeK };

            LinkResume();
            var link = Assert.Single(MissionCrossTreeDock.FindLinks(treeA, trees));
            List<string> journey = MissionCrossTreeDock.ComputePartnerJourneyLegIds(treeK, link);
            Assert.Contains("STACK", journey);
            Assert.Contains("V-cont", journey);
            Assert.DoesNotContain("K-away", journey);
        }

        // ---- helpers ----

        private static Recording Rec(string id, uint pid, double start, double end,
            string guid, string parentBp = null, string childBp = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Vessel " + id,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                ParentBranchPointId = parentBp,
                ChildBranchPointId = childBp,
                Points = new List<TrajectoryPoint>()
            };
        }

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
