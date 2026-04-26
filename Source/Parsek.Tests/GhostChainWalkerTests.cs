using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostChainWalkerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostChainWalkerTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
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

        static RecordingTree MakeTree(string treeId, Recording[] recordings,
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
            {
                for (int i = 0; i < branchPoints.Length; i++)
                    tree.BranchPoints.Add(branchPoints[i]);
            }

            return tree;
        }

        static Recording MakeRecording(string id, uint vesselPid,
            double startUT, double endUT,
            TerminalState? terminal = null,
            string parentBpId = null, string childBpId = null,
            string treeId = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBpId,
                ChildBranchPointId = childBpId,
                TreeId = treeId,
                // Clear default Points list to avoid confusing StartUT/EndUT
                Points = new List<TrajectoryPoint>()
            };
            return rec;
        }

        static BranchPoint MakeBranchPoint(string id, BranchPointType type,
            double ut, uint targetVesselPid,
            string[] parentRecIds, string[] childRecIds)
        {
            var bp = new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                TargetVesselPersistentId = targetVesselPid,
                ParentRecordingIds = new List<string>(parentRecIds),
                ChildRecordingIds = new List<string>(childRecIds)
            };
            return bp;
        }

        #endregion

        #region ComputeAllGhostChains — basic chain construction

        /// <summary>
        /// R1 docks A to S(PID=100). Tree has Dock BP with target=100.
        /// Chain: vessel=100, 1 link.
        /// Guards: basic chain construction.
        /// </summary>
        [Fact]
        public void SingleTree_SingleMerge_ClaimsVessel()
        {
            // R1 records vessel A (PID=50) docking to station S (PID=100)
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock");

            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });

            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Single(chains);
            Assert.True(chains.ContainsKey(100));
            var chain = chains[100];
            Assert.Equal((uint)100, chain.OriginalVesselPid);
            Assert.Single(chain.Links);
            Assert.Equal("MERGE", chain.Links[0].interactionType);
            Assert.Equal("tree-1", chain.Links[0].treeId);
            Assert.Equal(1060.0, chain.GhostStartUT); // earliest link UT, not rewindUT
            Assert.False(chain.IsTerminated);
        }

        /// <summary>
        /// R1 leaf has TerminalState.Destroyed. Chain terminated=true.
        /// Guards: terminated chains don't spawn.
        /// </summary>
        [Fact]
        public void SingleTree_DestructionTerminatesChain()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                terminal: TerminalState.Destroyed, parentBpId: "bp-dock");
            // Non-terminated leaf so tree isn't fully-terminated (#174 skip)
            var r1LeafB = MakeRecording("R1-leafB", 200, 1060, 1120,
                terminal: TerminalState.Landed, parentBpId: "bp-dock");

            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf", "R1-leafB" });

            var tree = MakeTree("tree-1", new[] { r1, r1Leaf, r1LeafB },
                new[] { dockBp });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Single(chains);
            Assert.True(chains[100].IsTerminated);
        }

        /// <summary>
        /// Tree1: R1 merges with S(100), R1 leaf has VesselPersistentId=100.
        /// Tree2: R2 merges with target=100.
        /// Chain extends to R2's leaf.
        /// Guards: PID-based cross-tree linking (THE critical feature).
        /// </summary>
        [Fact]
        public void CrossTree_TwoLinks_ChainsExtend()
        {
            // Tree1: R1 docks to S(100), resulting vessel keeps PID 100
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock1");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock1");

            var dock1 = MakeBranchPoint("bp-dock1", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });

            var tree1 = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dock1 });

            // Tree2: R2 docks to the same vessel (now S+A, PID=100)
            var r2 = MakeRecording("R2", 60, 1200, 1260, childBpId: "bp-dock2");
            var r2Leaf = MakeRecording("R2-leaf", 100, 1260, 1320,
                parentBpId: "bp-dock2");

            var dock2 = MakeBranchPoint("bp-dock2", BranchPointType.Dock,
                1260, 100, new[] { "R2" }, new[] { "R2-leaf" });

            var tree2 = MakeTree("tree-2", new[] { r2, r2Leaf },
                new[] { dock2 });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree1, tree2 }, 900);

            // Should have a single chain for PID=100 (both claims merged)
            Assert.Single(chains);
            Assert.True(chains.ContainsKey(100));
            var chain = chains[100];
            Assert.Equal(2, chain.Links.Count);
            Assert.Equal("R2-leaf", chain.TipRecordingId);
            Assert.Equal(1320.0, chain.SpawnUT);
        }

        /// <summary>
        /// Two committed trees can both continue the same claimed vessel across an
        /// overlapping rewind window. The later claim must still extend the chain.
        /// Guards: overlapping continuations are not dropped as "ambiguous."
        /// </summary>
        [Fact]
        public void CrossTree_OverlappingHistoricalReuse_StillExtendsChain()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock1");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock1");
            var dock1 = MakeBranchPoint("bp-dock1", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree1 = MakeTree("tree-1", new[] { r1, r1Leaf }, new[] { dock1 });

            var r2 = MakeRecording("R2", 60, 1020, 1080, childBpId: "bp-dock2");
            var r2Leaf = MakeRecording("R2-leaf", 100, 1080, 1160,
                parentBpId: "bp-dock2");
            var dock2 = MakeBranchPoint("bp-dock2", BranchPointType.Dock,
                1080, 100, new[] { "R2" }, new[] { "R2-leaf" });
            var tree2 = MakeTree("tree-2", new[] { r2, r2Leaf }, new[] { dock2 });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree1, tree2 }, 900);

            Assert.Single(chains);
            Assert.True(chains.ContainsKey(100));
            var chain = chains[100];
            Assert.Equal(2, chain.Links.Count);
            Assert.Equal("R2-leaf", chain.TipRecordingId);
            Assert.Equal(1160.0, chain.SpawnUT);
        }

        /// <summary>
        /// Two rewind branches inside the same tree can both continue the same claimed
        /// vessel PID. The later branch must still extend the chain.
        /// Guards: branch-local overlap is not treated as self-owned history.
        /// </summary>
        [Fact]
        public void SameTree_OverlappingHistoricalReuse_StillExtendsChain()
        {
            var root = MakeRecording("root", 50, 1000, 1020, childBpId: "bp-split");

            var branchA = MakeRecording("branch-a", 60, 1020, 1060,
                parentBpId: "bp-split", childBpId: "bp-dock-a");
            var branchALeaf = MakeRecording("branch-a-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock-a");

            var branchB = MakeRecording("branch-b", 70, 1020, 1080,
                parentBpId: "bp-split", childBpId: "bp-dock-b");
            var branchBLeaf = MakeRecording("branch-b-leaf", 100, 1080, 1160,
                parentBpId: "bp-dock-b");

            var splitBp = MakeBranchPoint("bp-split", BranchPointType.Breakup,
                1020, 0, new[] { "root" }, new[] { "branch-a", "branch-b" });
            var dockA = MakeBranchPoint("bp-dock-a", BranchPointType.Dock,
                1060, 100, new[] { "branch-a" }, new[] { "branch-a-leaf" });
            var dockB = MakeBranchPoint("bp-dock-b", BranchPointType.Dock,
                1080, 100, new[] { "branch-b" }, new[] { "branch-b-leaf" });

            var tree = MakeTree("tree-1",
                new[] { root, branchA, branchALeaf, branchB, branchBLeaf },
                new[] { splitBp, dockA, dockB });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Single(chains);
            Assert.True(chains.ContainsKey(100));
            var chain = chains[100];
            Assert.Equal(2, chain.Links.Count);
            Assert.Equal("branch-b-leaf", chain.TipRecordingId);
            Assert.Equal(1160.0, chain.SpawnUT);
        }

        /// <summary>
        /// Equal-time claims must still sort deterministically so later chain lookup does
        /// not depend on unstable List.Sort tie handling.
        /// Guards: same-UT links use a stable secondary key order.
        /// </summary>
        [Fact]
        public void EqualTimeClaims_SortDeterministically()
        {
            var aRec = MakeRecording("A-rec", 50, 1000, 1060, childBpId: "bp-a");
            var aLeaf = MakeRecording("A-leaf", 100, 1060, 1120, parentBpId: "bp-a");
            var aTree = MakeTree("tree-a", new[] { aRec, aLeaf },
                new[] { MakeBranchPoint("bp-a", BranchPointType.Dock, 1060, 100, new[] { "A-rec" }, new[] { "A-leaf" }) });

            var bRec = MakeRecording("B-rec", 60, 1000, 1060, childBpId: "bp-b");
            var bLeaf = MakeRecording("B-leaf", 100, 1060, 1140, parentBpId: "bp-b");
            var bTree = MakeTree("tree-b", new[] { bRec, bLeaf },
                new[] { MakeBranchPoint("bp-b", BranchPointType.Dock, 1060, 100, new[] { "B-rec" }, new[] { "B-leaf" }) });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { bTree, aTree }, 900);

            var chain = chains[100];
            Assert.Equal(2, chain.Links.Count);
            Assert.Equal("tree-a", chain.Links[0].treeId);
            Assert.Equal("tree-b", chain.Links[1].treeId);
        }

        /// <summary>
        /// R1 targets S1(100), R2 targets S2(200). Two independent chains.
        /// Guards: chains don't bleed.
        /// </summary>
        [Fact]
        public void IndependentChains_TwoVessels_SeparateChains()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock1");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock1");
            var dock1 = MakeBranchPoint("bp-dock1", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });

            var r2 = MakeRecording("R2", 70, 1000, 1060, childBpId: "bp-dock2");
            var r2Leaf = MakeRecording("R2-leaf", 200, 1060, 1120,
                parentBpId: "bp-dock2");
            var dock2 = MakeBranchPoint("bp-dock2", BranchPointType.Dock,
                1060, 200, new[] { "R2" }, new[] { "R2-leaf" });

            var tree = MakeTree("tree-1",
                new[] { r1, r1Leaf, r2, r2Leaf },
                new[] { dock1, dock2 });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Equal(2, chains.Count);
            Assert.True(chains.ContainsKey(100));
            Assert.True(chains.ContainsKey(200));
            Assert.Single(chains[100].Links);
            Assert.Single(chains[200].Links);
        }

        /// <summary>
        /// R1 records combined vessel S+A(PID=100) undocking. Undock BP has target=100.
        /// SPLIT path uses ChildRecordingIds[0] as the claiming recording.
        /// Guards: SPLIT branch point chain construction.
        /// </summary>
        [Fact]
        public void SingleTree_UndockSplit_ClaimsVessel()
        {
            // R1 is the parent recording (vessel combined S+A, PID=50)
            // After undock, S (PID=100) splits off as a separate vessel
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-undock");
            var r1Child = MakeRecording("R1-child", 100, 1060, 1120,
                parentBpId: "bp-undock");

            var undockBp = MakeBranchPoint("bp-undock", BranchPointType.Undock,
                1060, 100, new[] { "R1" }, new[] { "R1-child" });

            var tree = MakeTree("tree-1", new[] { r1, r1Child },
                new[] { undockBp });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Single(chains);
            Assert.True(chains.ContainsKey(100));
            var chain = chains[100];
            Assert.Equal((uint)100, chain.OriginalVesselPid);
            Assert.Single(chain.Links);
            Assert.Equal("SPLIT", chain.Links[0].interactionType);
            Assert.Equal("R1-child", chain.Links[0].recordingId);
            Assert.Equal("tree-1", chain.Links[0].treeId);
            Assert.False(chain.IsTerminated);
        }

        /// <summary>
        /// S's background recording has a Destroyed PartEvent. Chain via BACKGROUND_EVENT.
        /// Guards: non-BranchPoint claiming.
        /// </summary>
        [Fact]
        public void BackgroundEventClaim_PartDestroyed()
        {
            // Root recording (vessel A, PID=50)
            var r1 = MakeRecording("R1", 50, 1000, 1120);

            // Background recording for station S (PID=100), has a part destroyed event
            var bgRec = MakeRecording("BG-S", 100, 1000, 1120);
            bgRec.PartEvents = new List<PartEvent>
            {
                new PartEvent
                {
                    ut = 1050,
                    partPersistentId = 999,
                    eventType = PartEventType.Destroyed,
                    partName = "solarPanel"
                }
            };

            var tree = MakeTree("tree-1", new[] { r1, bgRec }, null);

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Single(chains);
            Assert.True(chains.ContainsKey(100));
            var chain = chains[100];
            Assert.Single(chain.Links);
            Assert.Equal("BACKGROUND_EVENT", chain.Links[0].interactionType);
            Assert.Equal("BG-S", chain.Links[0].recordingId);
        }

        /// <summary>
        /// A root-lineage recording can contain ghost-trigger events, but that does not
        /// make the tree claim its own vessel via BACKGROUND_EVENT.
        /// Guards: self/root recordings are never treated as background claims.
        /// </summary>
        [Fact]
        public void RootLineageRecording_WithTriggerEvents_DoesNotCreateBackgroundClaim()
        {
            var root = MakeRecording("R1", 50, 1000, 1120);
            root.PartEvents = new List<PartEvent>
            {
                new PartEvent
                {
                    ut = 1050,
                    partPersistentId = 123,
                    eventType = PartEventType.Destroyed,
                    partName = "panel"
                }
            };

            var tree = MakeTree("tree-1", new[] { root }, null);

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Empty(chains);
        }

        /// <summary>
        /// Background recording with only cosmetic events (LightOn/LightOff) does NOT
        /// create a chain claim. Only ghosting-trigger events qualify.
        /// Guards: cosmetic-only background recordings don't ghost.
        /// </summary>
        [Fact]
        public void BackgroundRecording_CosmeticOnlyEvents_NoClaim()
        {
            // Root recording (vessel A, PID=50)
            var r1 = MakeRecording("R1", 50, 1000, 1120);

            // Background recording for station S (PID=100), only cosmetic events
            var bgRec = MakeRecording("BG-S", 100, 1000, 1120);
            bgRec.PartEvents = new List<PartEvent>
            {
                new PartEvent
                {
                    ut = 1020,
                    partPersistentId = 999,
                    eventType = PartEventType.LightOn,
                    partName = "spotLight"
                },
                new PartEvent
                {
                    ut = 1080,
                    partPersistentId = 999,
                    eventType = PartEventType.LightOff,
                    partName = "spotLight"
                }
            };

            var tree = MakeTree("tree-1", new[] { r1, bgRec }, null);

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Empty(chains);
        }

        /// <summary>
        /// Tree with Launch BP only, no target PIDs. Empty dict.
        /// Guards: normal recordings don't ghost.
        /// </summary>
        [Fact]
        public void NoClaimsInTrees_ReturnsEmptyDict()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1120);

            var launchBp = MakeBranchPoint("bp-launch", BranchPointType.Launch,
                1000, 0, new string[0], new[] { "R1" });

            var tree = MakeTree("tree-1", new[] { r1 }, new[] { launchBp });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Empty(chains);
        }

        /// <summary>
        /// Tip has TerminalState.Docked. NOT terminated — only Destroyed/Recovered terminate.
        /// Guards: Docked state does not terminate chain.
        /// </summary>
        [Fact]
        public void DockedTerminalState_DoesNotTerminateChain()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                terminal: TerminalState.Docked, parentBpId: "bp-dock");

            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });

            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Single(chains);
            Assert.False(chains[100].IsTerminated);
        }

        /// <summary>
        /// Tip has TerminalState.Recovered. Terminated.
        /// Guards: recovery handled.
        /// </summary>
        [Fact]
        public void RecoveryTerminatesChain()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                terminal: TerminalState.Recovered, parentBpId: "bp-dock");
            // Non-terminated leaf so tree isn't fully-terminated (#174 skip)
            var r1LeafB = MakeRecording("R1-leafB", 200, 1060, 1120,
                terminal: TerminalState.Orbiting, parentBpId: "bp-dock");

            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf", "R1-leafB" });

            var tree = MakeTree("tree-1", new[] { r1, r1Leaf, r1LeafB },
                new[] { dockBp });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.True(chains[100].IsTerminated);
        }

        #endregion

        #region IsIntermediateChainLink

        /// <summary>
        /// Two-link chain. IsIntermediateChainLink(R1's rec) = true.
        /// Guards: intermediate detection.
        /// </summary>
        [Fact]
        public void IntermediateLink_SpawnSuppressed()
        {
            // Build a two-link chain via two trees
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock1");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock1");
            var dock1 = MakeBranchPoint("bp-dock1", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree1 = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dock1 });

            var r2 = MakeRecording("R2", 60, 1200, 1260, childBpId: "bp-dock2");
            var r2Leaf = MakeRecording("R2-leaf", 100, 1260, 1320,
                parentBpId: "bp-dock2");
            var dock2 = MakeBranchPoint("bp-dock2", BranchPointType.Dock,
                1260, 100, new[] { "R2" }, new[] { "R2-leaf" });
            var tree2 = MakeTree("tree-2", new[] { r2, r2Leaf },
                new[] { dock2 });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree1, tree2 }, 900);

            // R1 (the first link's recording) should be intermediate
            Assert.True(GhostChainWalker.IsIntermediateChainLink(chains, r1));
        }

        /// <summary>
        /// Same chain. IsIntermediateChainLink(R2's leaf) = false.
        /// Guards: tips are NOT suppressed.
        /// </summary>
        [Fact]
        public void ChainTip_SpawnNotSuppressed()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock1");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock1");
            var dock1 = MakeBranchPoint("bp-dock1", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree1 = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dock1 });

            var r2 = MakeRecording("R2", 60, 1200, 1260, childBpId: "bp-dock2");
            var r2Leaf = MakeRecording("R2-leaf", 100, 1260, 1320,
                parentBpId: "bp-dock2");
            var dock2 = MakeBranchPoint("bp-dock2", BranchPointType.Dock,
                1260, 100, new[] { "R2" }, new[] { "R2-leaf" });
            var tree2 = MakeTree("tree-2", new[] { r2, r2Leaf },
                new[] { dock2 });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree1, tree2 }, 900);

            // R2-leaf (the tip) should NOT be suppressed
            Assert.False(GhostChainWalker.IsIntermediateChainLink(chains, r2Leaf));
        }

        /// <summary>
        /// Recording not in any chain. IsIntermediateChainLink = false.
        /// THE MOST CRITICAL TEST — if this fails, all existing spawn-at-end breaks.
        /// </summary>
        [Fact]
        public void StandaloneRecording_NotInChain_NotSuppressed()
        {
            // Build a chain for PID=100
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock");
            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            // Standalone recording with PID=300 not in any chain
            var standalone = MakeRecording("standalone", 300, 2000, 2120);

            Assert.False(GhostChainWalker.IsIntermediateChainLink(chains, standalone));
        }

        #endregion

        #region FindChainForVessel

        /// <summary>
        /// Basic lookup.
        /// </summary>
        [Fact]
        public void FindChain_ExistingVessel_ReturnsChain()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock");
            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            var found = GhostChainWalker.FindChainForVessel(chains, 100);

            Assert.NotNull(found);
            Assert.Equal((uint)100, found.OriginalVesselPid);
        }

        /// <summary>
        /// Not claimed.
        /// </summary>
        [Fact]
        public void FindChain_UnclaimedVessel_ReturnsNull()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock");
            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            var found = GhostChainWalker.FindChainForVessel(chains, 999);

            Assert.Null(found);
        }

        #endregion

        #region Edge Cases

        /// <summary>
        /// No NPE on empty list.
        /// </summary>
        [Fact]
        public void EmptyCommittedTrees_ReturnsEmptyDict()
        {
            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree>(), 900);

            Assert.NotNull(chains);
            Assert.Empty(chains);
        }

        /// <summary>
        /// Null safety.
        /// </summary>
        [Fact]
        public void NullCommittedTrees_ReturnsEmptyDict()
        {
            var chains = GhostChainWalker.ComputeAllGhostChains(null, 900);

            Assert.NotNull(chains);
            Assert.Empty(chains);
        }

        /// <summary>
        /// Target=0 means internal, not external.
        /// </summary>
        [Fact]
        public void BranchPointWithZeroTargetPid_Ignored()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 50, 1060, 1120,
                parentBpId: "bp-dock");

            // Dock BP with target PID = 0 (internal dock, not external vessel)
            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 0, new[] { "R1" }, new[] { "R1-leaf" });

            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Empty(chains);
        }

        #endregion

        #region Log Assertion Tests

        /// <summary>
        /// Verify log contains "claimed by tree" for each merge.
        /// </summary>
        [Fact]
        public void LogsClaim_ForEachMerge()
        {
            logLines.Clear();

            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock");
            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("claimed by tree=tree-1")
                && l.Contains("PID=100"));
        }

        /// <summary>
        /// Verify log contains "Chain built" with correct fields.
        /// </summary>
        [Fact]
        public void LogsChainBuilt_WithCorrectFields()
        {
            logLines.Clear();

            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock");
            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("Chain built")
                && l.Contains("vessel=100") && l.Contains("links=1"));
        }

        /// <summary>
        /// Verify "No claims found" when empty.
        /// </summary>
        [Fact]
        public void LogsNoClaims_WhenEmpty()
        {
            logLines.Clear();

            var r1 = MakeRecording("R1", 50, 1000, 1120);
            var tree = MakeTree("tree-1", new[] { r1 }, null);

            GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("No claims found"));
        }

        /// <summary>
        /// Regression: per-frame callers (e.g. ParsekTrackingStation.RefreshGhostActionCache)
        /// invoke ComputeAllGhostChains every Update tick. Every diagnostic in the
        /// chain-walk path — claim, chain-built, walk-step, walk-reached-leaf,
        /// terminate, claims summary, has-ghosting-trigger — must coalesce silently
        /// when the input is stable. Counts every flavor of ChainWalker line, not
        /// just the leaf summary, so per-step walk diagnostics can't silently slip
        /// back into the per-frame path.
        /// </summary>
        [Fact]
        public void RepeatedCalls_StableInput_DoNotRespamDiagnostics()
        {
            // Multi-step chain: R1 → R1-mid → R1-leaf so WalkToLeaf takes >1 step.
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Mid = MakeRecording("R1-mid", 100, 1060, 1090,
                parentBpId: "bp-dock", childBpId: "bp-split");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1090, 1120,
                terminal: TerminalState.Destroyed, parentBpId: "bp-split");
            var r1LeafB = MakeRecording("R1-leafB", 200, 1090, 1120,
                terminal: TerminalState.Landed, parentBpId: "bp-split");
            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-mid" });
            var splitBp = MakeBranchPoint("bp-split", BranchPointType.Breakup,
                1090, 0, new[] { "R1-mid" }, new[] { "R1-leaf", "R1-leafB" });
            var tree = MakeTree("tree-1", new[] { r1, r1Mid, r1Leaf, r1LeafB },
                new[] { dockBp, splitBp });
            var trees = new List<RecordingTree> { tree };

            // Warm-up call seeds VerboseOnChange state and emits each diagnostic once.
            GhostChainWalker.ComputeAllGhostChains(trees, 900);
            logLines.Clear();

            // Subsequent calls with identical input must emit zero new lines for any
            // of the per-frame-spammy diagnostics. Suppressed counters absorb the
            // repeats; only the next genuine state flip would re-emit.
            for (int i = 0; i < 8; i++)
                GhostChainWalker.ComputeAllGhostChains(trees, 900);

            int spamCount = 0;
            foreach (var l in logLines)
            {
                if (!l.Contains("[ChainWalker]")) continue;
                if (l.Contains("claimed by tree=")) spamCount++;
                else if (l.Contains("Chain built:")) spamCount++;
                else if (l.Contains("WalkToLeaf: reached leaf=")) spamCount++;
                else if (l.Contains("WalkToLeaf: step ")) spamCount++;
                else if (l.Contains("ResolveTermination:") && l.Contains("marked terminated")) spamCount++;
                else if (l.Contains("Found claims for")) spamCount++;
                else if (l.Contains("HasGhostingTriggerEvents:")) spamCount++;
            }

            Assert.Equal(0, spamCount);
        }

        /// <summary>
        /// Regression: per-frame callers also hit the no-claim path in saves with
        /// no chain claims yet (e.g. fresh sandbox with one selected ghost). The
        /// "No claims found" and "No committed trees" summaries must coalesce.
        /// </summary>
        [Fact]
        public void RepeatedCalls_NoClaims_DoNotRespamSummary()
        {
            // Tree with no ghosting-trigger events and no claiming branch points.
            var r1 = MakeRecording("R1", 50, 1000, 1120);
            var tree = MakeTree("tree-1", new[] { r1 }, null);
            var trees = new List<RecordingTree> { tree };

            GhostChainWalker.ComputeAllGhostChains(trees, 900);
            logLines.Clear();

            for (int i = 0; i < 8; i++)
                GhostChainWalker.ComputeAllGhostChains(trees, 900);

            int spamCount = 0;
            foreach (var l in logLines)
            {
                if (!l.Contains("[ChainWalker]")) continue;
                if (l.Contains("No claims found in")) spamCount++;
                else if (l.Contains("No committed trees")) spamCount++;
                else if (l.Contains("HasGhostingTriggerEvents:")) spamCount++;
            }

            Assert.Equal(0, spamCount);

            // And the empty-input path is also coalesced.
            var empty = new List<RecordingTree>();
            GhostChainWalker.ComputeAllGhostChains(empty, 900);
            logLines.Clear();

            for (int i = 0; i < 8; i++)
                GhostChainWalker.ComputeAllGhostChains(empty, 900);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("No committed trees"));
        }

        /// <summary>
        /// Regression: when multiple branch points claim the same vessel PID in
        /// the same tree, the per-claim VerboseOnChange identity must scope to
        /// the individual claim (bp.Id / rec.RecordingId), not just the
        /// (pid, treeId) pair. Otherwise the two claims share an identity slot
        /// and their differing state keys ping-pong, re-emitting on every frame.
        /// </summary>
        [Fact]
        public void RepeatedCalls_MultipleClaimsSamePidSameTree_DoNotRespam()
        {
            // Same tree, two Dock branch points, both claiming PID=100.
            var root = MakeRecording("root", 50, 1000, 1020, childBpId: "bp-split");
            var branchA = MakeRecording("branch-a", 60, 1020, 1060,
                parentBpId: "bp-split", childBpId: "bp-dock-a");
            var branchALeaf = MakeRecording("branch-a-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock-a");
            var branchB = MakeRecording("branch-b", 70, 1020, 1080,
                parentBpId: "bp-split", childBpId: "bp-dock-b");
            var branchBLeaf = MakeRecording("branch-b-leaf", 100, 1080, 1160,
                parentBpId: "bp-dock-b");

            var splitBp = MakeBranchPoint("bp-split", BranchPointType.Breakup,
                1020, 0, new[] { "root" }, new[] { "branch-a", "branch-b" });
            var dockA = MakeBranchPoint("bp-dock-a", BranchPointType.Dock,
                1060, 100, new[] { "branch-a" }, new[] { "branch-a-leaf" });
            var dockB = MakeBranchPoint("bp-dock-b", BranchPointType.Dock,
                1080, 100, new[] { "branch-b" }, new[] { "branch-b-leaf" });

            var tree = MakeTree("tree-1",
                new[] { root, branchA, branchALeaf, branchB, branchBLeaf },
                new[] { splitBp, dockA, dockB });
            var trees = new List<RecordingTree> { tree };

            GhostChainWalker.ComputeAllGhostChains(trees, 900);
            logLines.Clear();

            for (int i = 0; i < 8; i++)
                GhostChainWalker.ComputeAllGhostChains(trees, 900);

            int respam = 0;
            foreach (var l in logLines)
            {
                if (l.Contains("[ChainWalker]") && l.Contains("claimed by tree="))
                    respam++;
            }

            Assert.Equal(0, respam);
        }

        #endregion

        #region IsIntermediateChainLink — null/empty safety

        [Fact]
        public void IsIntermediateChainLink_NullChains_ReturnsFalse()
        {
            var rec = MakeRecording("R1", 50, 1000, 1060);
            Assert.False(GhostChainWalker.IsIntermediateChainLink(null, rec));
        }

        [Fact]
        public void IsIntermediateChainLink_EmptyChains_ReturnsFalse()
        {
            var rec = MakeRecording("R1", 50, 1000, 1060);
            Assert.False(GhostChainWalker.IsIntermediateChainLink(
                new Dictionary<uint, GhostChain>(), rec));
        }

        [Fact]
        public void IsIntermediateChainLink_NullRecording_ReturnsFalse()
        {
            var chains = new Dictionary<uint, GhostChain>();
            chains[100] = new GhostChain { OriginalVesselPid = 100 };
            Assert.False(GhostChainWalker.IsIntermediateChainLink(chains, null));
        }

        #endregion

        #region Cross-tree cycle detection

        /// <summary>
        /// Tree1 has a chain tip with VesselPersistentId=100. Tree2 has a MERGE targeting PID=100,
        /// and Tree2's tip also has VesselPersistentId=100 (creating a potential cycle).
        /// ComputeAllGhostChains should not infinite-loop and should produce a valid chain
        /// (the visited HashSet cycle guard breaks the cycle).
        /// </summary>
        [Fact]
        public void CrossTreeCycle_DetectedAndHandled()
        {
            // Tree1: R1 docks to S(100), resulting vessel keeps PID 100
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock1");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock1");

            var dock1 = MakeBranchPoint("bp-dock1", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });

            var tree1 = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dock1 });

            // Tree2: R2 docks to the same vessel PID=100, AND its resulting
            // vessel also has PID=100 (creating a self-referencing cycle)
            var r2 = MakeRecording("R2", 60, 1200, 1260, childBpId: "bp-dock2");
            var r2Leaf = MakeRecording("R2-leaf", 100, 1260, 1320,
                parentBpId: "bp-dock2");

            var dock2 = MakeBranchPoint("bp-dock2", BranchPointType.Dock,
                1260, 100, new[] { "R2" }, new[] { "R2-leaf" });

            var tree2 = MakeTree("tree-2", new[] { r2, r2Leaf },
                new[] { dock2 });

            // Should not infinite-loop
            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree1, tree2 }, 900);

            // Should produce a valid result (single chain for PID=100)
            Assert.NotNull(chains);
            Assert.True(chains.ContainsKey(100));

            var chain = chains[100];
            Assert.True(chain.Links.Count >= 1);
            Assert.False(string.IsNullOrEmpty(chain.TipRecordingId));
        }

        #endregion

        #region FindChainForVessel — null safety

        [Fact]
        public void FindChainForVessel_NullChains_ReturnsNull()
        {
            Assert.Null(GhostChainWalker.FindChainForVessel(null, 100));
        }

        #endregion

        #region GetRootLineageVesselPids — chain following

        [Fact]
        public void GetRootLineageVesselPids_FollowsChainLinks()
        {
            // Root recording was split by the optimizer into 2 chain segments.
            // The ChildBranchPointId is on the second segment.
            // GetRootLineageVesselPids must follow chain links to reach the BP
            // and include child vessel PIDs in the lineage.
            var rootFirst = MakeRecording("root_first", 1000, 17000, 17030);
            rootFirst.ChainId = "chain_001";
            rootFirst.ChainIndex = 0;
            rootFirst.ChainBranch = 0;
            // No ChildBranchPointId on first half (moved to second)

            var rootSecond = MakeRecording("root_second", 1000, 17030, 17060,
                childBpId: "bp_staging");
            rootSecond.ChainId = "chain_001";
            rootSecond.ChainIndex = 1;
            rootSecond.ChainBranch = 0;

            var debris = MakeRecording("debris", 2000, 17060, 17100,
                parentBpId: "bp_staging",
                terminal: TerminalState.Destroyed);

            var bp = MakeBranchPoint("bp_staging", BranchPointType.JointBreak,
                17060, 0,
                new[] { "root_second" }, new[] { "debris" });

            var tree = MakeTree("tree_chain", new[] { rootFirst, rootSecond, debris },
                new[] { bp });

            var pids = GhostChainWalker.GetRootLineageVesselPids(tree);

            // Should include both root PID and debris PID (reached via chain + BP)
            Assert.Contains((uint)1000, pids);
            Assert.Contains((uint)2000, pids);
        }

        [Fact]
        public void GetRootLineageVesselPids_NoChain_StillFollowsBP()
        {
            // Root recording with direct ChildBranchPointId (no chain split).
            // Should still work as before.
            var root = MakeRecording("root", 1000, 17000, 17060,
                childBpId: "bp_staging");

            var stage = MakeRecording("stage", 3000, 17060, 17120,
                parentBpId: "bp_staging",
                terminal: TerminalState.Destroyed);

            var bp = MakeBranchPoint("bp_staging", BranchPointType.JointBreak,
                17060, 0,
                new[] { "root" }, new[] { "stage" });

            var tree = MakeTree("tree_nobp", new[] { root, stage }, new[] { bp });

            var pids = GhostChainWalker.GetRootLineageVesselPids(tree);

            Assert.Contains((uint)1000, pids);
            Assert.Contains((uint)3000, pids);
        }

        #endregion
    }
}
