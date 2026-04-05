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
    }
}
