using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for ParsekFlight.FindBackgroundRecordingForVessel — the pure static method
    /// that locates a committed recording covering a given UT for a vessel PID.
    /// Used by chain ghost trajectory playback (Task 6b-4).
    /// </summary>
    [Collection("Sequential")]
    public class ChainGhostTrajectoryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainGhostTrajectoryTests()
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

        /// <summary>
        /// Creates a recording with trajectory points spanning the given UT range.
        /// Points are spaced 10s apart from startUT to endUT.
        /// </summary>
        static Recording MakeRecordingWithPoints(string id, uint vesselPid,
            double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                Points = new List<TrajectoryPoint>()
            };

            // Generate points every 10 seconds from startUT to endUT
            for (double ut = startUT; ut <= endUT; ut += 10.0)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = ut,
                    latitude = 0.0,
                    longitude = 0.0,
                    altitude = 75.0,
                    bodyName = "Kerbin"
                });
            }

            // Ensure endUT is included as the last point
            if (rec.Points.Count > 0 && rec.Points[rec.Points.Count - 1].ut < endUT)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = endUT,
                    latitude = 0.0,
                    longitude = 0.0,
                    altitude = 75.0,
                    bodyName = "Kerbin"
                });
            }

            return rec;
        }

        /// <summary>
        /// Creates a recording with no trajectory points (empty Points list).
        /// </summary>
        static Recording MakeRecordingNoPoints(string id, uint vesselPid,
            double startUT, double endUT)
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                Points = new List<TrajectoryPoint>()
            };
        }

        static Recording MakeRecordingOrbitOnly(string id, uint vesselPid,
            double startUT, double endUT)
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = startUT,
                        endUT = endUT,
                        bodyName = "Kerbin",
                        semiMajorAxis = 700000
                    }
                }
            };
        }

        static RecordingTree MakeTree(string treeId, Recording[] recordings, BranchPoint[] branchPoints)
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

        static BranchPoint MakeBranchPoint(string id, BranchPointType type,
            double ut, uint targetVesselPid,
            string[] parentRecIds, string[] childRecIds)
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

        #endregion

        #region FindBackgroundRecordingForVessel — core behavior

        /// <summary>
        /// UT falls within recording range. Returns the recording.
        /// Guards: basic in-range lookup.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_InRange_ReturnsRecording()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1050);

            Assert.NotNull(result);
            Assert.Equal("bg-1", result.RecordingId);
        }

        /// <summary>
        /// UT is outside recording range. Returns null.
        /// Guards: out-of-range rejection.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_OutOfRange_ReturnsNull()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1200);

            Assert.Null(result);
        }

        /// <summary>
        /// Recording matches UT but wrong vessel PID. Returns null.
        /// Guards: PID filtering.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_WrongVesselPid_ReturnsNull()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 200, 1050);

            Assert.Null(result);
        }

        /// <summary>
        /// Recording matches PID and UT but has empty Points list. Returns null.
        /// Guards: trajectory data required.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_EmptyPointsList_ReturnsNull()
        {
            var rec = MakeRecordingNoPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1050);

            Assert.Null(result);
        }

        /// <summary>
        /// Null recordings list. Returns null without NullReferenceException.
        /// Guards: null safety.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_NullRecordings_ReturnsNull()
        {
            var result = ParsekFlight.FindBackgroundRecordingForVessel(null, 100, 1050);

            Assert.Null(result);
        }

        /// <summary>
        /// Multiple recordings cover the same UT for the same vessel.
        /// Returns the first one found (list order).
        /// Guards: deterministic selection.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_MultipleCandidates_ReturnsFirst()
        {
            var rec1 = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var rec2 = MakeRecordingWithPoints("bg-2", 100, 1020, 1120);
            var recordings = new List<Recording> { rec1, rec2 };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1050);

            Assert.NotNull(result);
            Assert.Equal("bg-1", result.RecordingId);
        }

        /// <summary>
        /// Chain-scoped lookup must prefer the tree that actually owns the chain link,
        /// not an overlapping alternate-history recording with the same PID.
        /// Guards: same-PID historical reuse does not steal chain trajectory playback.
        /// </summary>
        [Fact]
        public void FindBackgroundRecordingForChain_PrefersClaimingTreeOverAlternateHistory()
        {
            var altRec = MakeRecordingWithPoints("bg-alt", 100, 1000, 1100);
            var altTree = MakeTree("tree-alt", new[] { altRec }, null);

            var claimRec = MakeRecordingNoPoints("R1", 50, 1000, 1060);
            claimRec.ChildBranchPointId = "bp-dock";
            var chainRec = MakeRecordingWithPoints("bg-chain", 100, 1060, 1120);
            chainRec.ParentBranchPointId = "bp-dock";
            var chainTree = MakeTree("tree-1", new[] { claimRec, chainRec },
                new[]
                {
                    MakeBranchPoint("bp-dock", BranchPointType.Dock, 1060, 100,
                        new[] { "R1" }, new[] { "bg-chain" })
                });

            var recordings = new List<Recording> { altRec, claimRec, chainRec };
            var trees = new List<RecordingTree> { altTree, chainTree };
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipTreeId = "tree-1"
            };
            chain.Links.Add(new ChainLink
            {
                recordingId = "R1",
                treeId = "tree-1",
                ut = 1060,
                interactionType = "MERGE"
            });

            var result = ParsekFlight.FindBackgroundRecordingForChain(recordings, trees, chain, 1070);

            Assert.NotNull(result);
            Assert.Equal("bg-chain", result.RecordingId);
        }

        /// <summary>
        /// When a later tree extends the same chain, lookup should advance to the
        /// most recent claim tree once its claim UT has been reached.
        /// Guards: cross-tree continuation picks the newest link's trajectory.
        /// </summary>
        [Fact]
        public void FindBackgroundRecordingForChain_PrefersMostRecentClaimTreeAtCurrentUT()
        {
            var claim1 = MakeRecordingNoPoints("R1", 50, 1000, 1060);
            claim1.ChildBranchPointId = "bp-dock-1";
            var tree1Rec = MakeRecordingWithPoints("bg-tree-1", 100, 1060, 1300);
            tree1Rec.ParentBranchPointId = "bp-dock-1";
            var tree1 = MakeTree("tree-1", new[] { claim1, tree1Rec },
                new[]
                {
                    MakeBranchPoint("bp-dock-1", BranchPointType.Dock, 1060, 100,
                        new[] { "R1" }, new[] { "bg-tree-1" })
                });

            var claim2 = MakeRecordingNoPoints("R2", 60, 1200, 1260);
            claim2.ChildBranchPointId = "bp-dock-2";
            var tree2Rec = MakeRecordingWithPoints("bg-tree-2", 100, 1260, 1320);
            tree2Rec.ParentBranchPointId = "bp-dock-2";
            var tree2 = MakeTree("tree-2", new[] { claim2, tree2Rec },
                new[]
                {
                    MakeBranchPoint("bp-dock-2", BranchPointType.Dock, 1260, 100,
                        new[] { "R2" }, new[] { "bg-tree-2" })
                });

            var recordings = new List<Recording> { claim1, tree1Rec, claim2, tree2Rec };
            var trees = new List<RecordingTree> { tree1, tree2 };
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipTreeId = "tree-2"
            };
            chain.Links.Add(new ChainLink
            {
                recordingId = "R1",
                treeId = "tree-1",
                ut = 1060,
                interactionType = "MERGE"
            });
            chain.Links.Add(new ChainLink
            {
                recordingId = "R2",
                treeId = "tree-2",
                ut = 1260,
                interactionType = "MERGE"
            });

            var result = ParsekFlight.FindBackgroundRecordingForChain(recordings, trees, chain, 1270);

            Assert.NotNull(result);
            Assert.Equal("bg-tree-2", result.RecordingId);
        }

        /// <summary>
        /// Chain ghosts are active from rewind, not from first claim UT. If the
        /// claiming tree has no pre-claim trajectory for the claimed vessel, lookup
        /// must fall back to any committed recording that actually covers the UT.
        /// Guards: foreign-merge chains still have a trajectory source before claim.
        /// </summary>
        [Fact]
        public void FindBackgroundRecordingForChain_FallsBackToGlobalHistoryBeforeFirstClaim()
        {
            var preClaimRec = MakeRecordingWithPoints("bg-alt", 100, 1000, 1100);
            var altTree = MakeTree("tree-alt", new[] { preClaimRec }, null);

            var claimRec = MakeRecordingNoPoints("R1", 50, 1000, 1060);
            claimRec.ChildBranchPointId = "bp-dock";
            var postClaimRec = MakeRecordingWithPoints("bg-chain", 100, 1060, 1120);
            postClaimRec.ParentBranchPointId = "bp-dock";
            var chainTree = MakeTree("tree-1", new[] { claimRec, postClaimRec },
                new[]
                {
                    MakeBranchPoint("bp-dock", BranchPointType.Dock, 1060, 100,
                        new[] { "R1" }, new[] { "bg-chain" })
                });

            var recordings = new List<Recording> { preClaimRec, claimRec, postClaimRec };
            var trees = new List<RecordingTree> { altTree, chainTree };
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipTreeId = "tree-1"
            };
            chain.Links.Add(new ChainLink
            {
                recordingId = "R1",
                treeId = "tree-1",
                ut = 1060,
                interactionType = "MERGE"
            });

            var result = ParsekFlight.FindBackgroundRecordingForChain(recordings, trees, chain, 1030);

            Assert.NotNull(result);
            Assert.Equal("bg-alt", result.RecordingId);
        }

        /// <summary>
        /// If the first claiming tree already contains a unique recording for the claimed
        /// vessel before the claim UT, chain lookup should stay in that tree instead of
        /// falling back to another committed tree with the same PID.
        /// Guards: pre-claim chain-local coverage wins over global PID-only lookup.
        /// </summary>
        [Fact]
        public void FindBackgroundRecordingForChain_PrefersUniquePreClaimCoverageInClaimTree()
        {
            var altRec = MakeRecordingWithPoints("bg-alt", 100, 1000, 1100);
            var altTree = MakeTree("tree-alt", new[] { altRec }, null);

            var claimRec = MakeRecordingNoPoints("R1", 50, 1000, 1060);
            claimRec.ChildBranchPointId = "bp-dock";
            var preClaimChainRec = MakeRecordingWithPoints("bg-chain-pre", 100, 1000, 1050);
            var postClaimChainRec = MakeRecordingWithPoints("bg-chain-post", 100, 1060, 1120);
            postClaimChainRec.ParentBranchPointId = "bp-dock";
            var chainTree = MakeTree("tree-1", new[] { claimRec, preClaimChainRec, postClaimChainRec },
                new[]
                {
                    MakeBranchPoint("bp-dock", BranchPointType.Dock, 1060, 100,
                        new[] { "R1" }, new[] { "bg-chain-post" })
                });

            var recordings = new List<Recording> { altRec, claimRec, preClaimChainRec, postClaimChainRec };
            var trees = new List<RecordingTree> { altTree, chainTree };
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipTreeId = "tree-1"
            };
            chain.Links.Add(new ChainLink
            {
                recordingId = "R1",
                treeId = "tree-1",
                ut = 1060,
                interactionType = "MERGE"
            });

            var result = ParsekFlight.FindBackgroundRecordingForChain(recordings, trees, chain, 1030);

            Assert.NotNull(result);
            Assert.Equal("bg-chain-pre", result.RecordingId);
        }

        /// <summary>
        /// Pre-claim chain-local coverage can be orbit-only/surface-only. The claim-tree
        /// helper must still return that recording so fallback positioning stays on the
        /// intended branch instead of reopening a global PID-only scan.
        /// Guards: pre-claim orbit-only chain-local coverage is preserved.
        /// </summary>
        [Fact]
        public void FindPreClaimChainRecordingAtUT_ReturnsOrbitOnlyCoverageInClaimTree()
        {
            var claimRec = MakeRecordingNoPoints("R1", 50, 1000, 1060);
            claimRec.ChildBranchPointId = "bp-dock";
            var preClaimOrbitRec = MakeRecordingOrbitOnly("bg-chain-pre", 100, 1000, 1050);
            var postClaimChainRec = MakeRecordingWithPoints("bg-chain-post", 100, 1060, 1120);
            postClaimChainRec.ParentBranchPointId = "bp-dock";
            var chainTree = MakeTree("tree-1", new[] { claimRec, preClaimOrbitRec, postClaimChainRec },
                new[]
                {
                    MakeBranchPoint("bp-dock", BranchPointType.Dock, 1060, 100,
                        new[] { "R1" }, new[] { "bg-chain-post" })
                });

            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipTreeId = "tree-1"
            };
            chain.Links.Add(new ChainLink
            {
                recordingId = "R1",
                treeId = "tree-1",
                ut = 1060,
                interactionType = "MERGE"
            });

            var result = ParsekFlight.FindPreClaimChainRecordingAtUT(
                new List<RecordingTree> { chainTree }, chain, 1030, out bool hasCoverage);

            Assert.True(hasCoverage);
            Assert.NotNull(result);
            Assert.Equal("bg-chain-pre", result.RecordingId);
        }

        /// <summary>
        /// A pre-claim placeholder in the first claim tree should not suppress the old
        /// global fallback unless it actually has usable playback payload.
        /// Guards: empty/degraded same-tree placeholders do not hide valid global history.
        /// </summary>
        [Fact]
        public void FindBackgroundRecordingForChain_IgnoresPreClaimPlaceholderWithoutPlaybackData()
        {
            var altRec = MakeRecordingWithPoints("bg-alt", 100, 1000, 1100);
            var altTree = MakeTree("tree-alt", new[] { altRec }, null);

            var claimRec = MakeRecordingNoPoints("R1", 50, 1000, 1060);
            claimRec.ChildBranchPointId = "bp-dock";
            var placeholderRec = MakeRecordingNoPoints("bg-chain-pre", 100, 1000, 1050);
            var postClaimChainRec = MakeRecordingWithPoints("bg-chain-post", 100, 1060, 1120);
            postClaimChainRec.ParentBranchPointId = "bp-dock";
            var chainTree = MakeTree("tree-1", new[] { claimRec, placeholderRec, postClaimChainRec },
                new[]
                {
                    MakeBranchPoint("bp-dock", BranchPointType.Dock, 1060, 100,
                        new[] { "R1" }, new[] { "bg-chain-post" })
                });

            var recordings = new List<Recording> { altRec, claimRec, placeholderRec, postClaimChainRec };
            var trees = new List<RecordingTree> { altTree, chainTree };
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipTreeId = "tree-1"
            };
            chain.Links.Add(new ChainLink
            {
                recordingId = "R1",
                treeId = "tree-1",
                ut = 1060,
                interactionType = "MERGE"
            });

            var result = ParsekFlight.FindBackgroundRecordingForChain(recordings, trees, chain, 1030);

            Assert.NotNull(result);
            Assert.Equal("bg-alt", result.RecordingId);
        }

        /// <summary>
        /// Once a chain-participating tree covers the UT, a point-backed alternate history
        /// must not override that tree's orbit-only/surface-only playback source.
        /// Returning null here is intentional: PositionChainGhostFallback should then use
        /// the chain-local orbit/surface path instead of a global PID-only point lookup.
        /// Guards: post-claim orbit-only chain segments keep priority over alternate history.
        /// </summary>
        [Fact]
        public void FindBackgroundRecordingForChain_DoesNotOverrideChainLocalOrbitOnlyCoverage()
        {
            var altRec = MakeRecordingWithPoints("bg-alt", 100, 1200, 1300);
            var altTree = MakeTree("tree-alt", new[] { altRec }, null);

            var claimRec = MakeRecordingNoPoints("R1", 50, 1140, 1200);
            claimRec.ChildBranchPointId = "bp-dock";
            var chainOrbitRec = MakeRecordingOrbitOnly("bg-chain-orbit", 100, 1200, 1300);
            chainOrbitRec.ParentBranchPointId = "bp-dock";
            var chainTree = MakeTree("tree-1", new[] { claimRec, chainOrbitRec },
                new[]
                {
                    MakeBranchPoint("bp-dock", BranchPointType.Dock, 1200, 100,
                        new[] { "R1" }, new[] { "bg-chain-orbit" })
                });

            var recordings = new List<Recording> { altRec, claimRec, chainOrbitRec };
            var trees = new List<RecordingTree> { altTree, chainTree };
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipTreeId = "tree-1"
            };
            chain.Links.Add(new ChainLink
            {
                recordingId = "R1",
                treeId = "tree-1",
                ut = 1200,
                interactionType = "MERGE"
            });

            var result = ParsekFlight.FindBackgroundRecordingForChain(recordings, trees, chain, 1250);

            Assert.Null(result);
        }

        /// <summary>
        /// After a chain has started, a missing exact chain-path recording must not reopen
        /// the old global PID-only lookup, or an unrelated alternate history can take over.
        /// Guards: post-claim gaps stay unresolved instead of selecting another tree's data.
        /// </summary>
        [Fact]
        public void FindBackgroundRecordingForChain_PostClaimGap_DoesNotFallbackGlobal()
        {
            var altRec = MakeRecordingWithPoints("bg-alt", 100, 1000, 1300);
            var altTree = MakeTree("tree-alt", new[] { altRec }, null);

            var claimRec = MakeRecordingNoPoints("R1", 50, 1000, 1060);
            claimRec.ChildBranchPointId = "bp-dock";
            var chainRec = MakeRecordingWithPoints("bg-chain", 100, 1060, 1100);
            chainRec.ParentBranchPointId = "bp-dock";
            var chainTree = MakeTree("tree-1", new[] { claimRec, chainRec },
                new[]
                {
                    MakeBranchPoint("bp-dock", BranchPointType.Dock, 1060, 100,
                        new[] { "R1" }, new[] { "bg-chain" })
                });

            var recordings = new List<Recording> { altRec, claimRec, chainRec };
            var trees = new List<RecordingTree> { altTree, chainTree };
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipTreeId = "tree-1"
            };
            chain.Links.Add(new ChainLink
            {
                recordingId = "R1",
                treeId = "tree-1",
                ut = 1060,
                interactionType = "MERGE"
            });

            var result = ParsekFlight.FindBackgroundRecordingForChain(recordings, trees, chain, 1150);

            Assert.Null(result);
        }

        /// <summary>
        /// Once a newer claim has started, older link history must not revive just because
        /// the newer branch has no exact coverage at the current UT.
        /// Guards: superseded chain links never take over after a later claim begins.
        /// </summary>
        [Fact]
        public void FindBackgroundRecordingForChain_NewerClaimGap_DoesNotReviveOlderLink()
        {
            var claim1 = MakeRecordingNoPoints("R1", 50, 1000, 1060);
            claim1.ChildBranchPointId = "bp-dock-1";
            var tree1Rec = MakeRecordingWithPoints("bg-tree-1", 100, 1060, 1300);
            tree1Rec.ParentBranchPointId = "bp-dock-1";
            var tree1 = MakeTree("tree-1", new[] { claim1, tree1Rec },
                new[]
                {
                    MakeBranchPoint("bp-dock-1", BranchPointType.Dock, 1060, 100,
                        new[] { "R1" }, new[] { "bg-tree-1" })
                });

            var claim2 = MakeRecordingNoPoints("R2", 60, 1200, 1260);
            claim2.ChildBranchPointId = "bp-dock-2";
            var tree2Rec = MakeRecordingWithPoints("bg-tree-2", 100, 1260, 1270);
            tree2Rec.ParentBranchPointId = "bp-dock-2";
            var tree2 = MakeTree("tree-2", new[] { claim2, tree2Rec },
                new[]
                {
                    MakeBranchPoint("bp-dock-2", BranchPointType.Dock, 1260, 100,
                        new[] { "R2" }, new[] { "bg-tree-2" })
                });

            var recordings = new List<Recording> { claim1, tree1Rec, claim2, tree2Rec };
            var trees = new List<RecordingTree> { tree1, tree2 };
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                TipTreeId = "tree-2"
            };
            chain.Links.Add(new ChainLink
            {
                recordingId = "R1",
                treeId = "tree-1",
                ut = 1060,
                interactionType = "MERGE"
            });
            chain.Links.Add(new ChainLink
            {
                recordingId = "R2",
                treeId = "tree-2",
                ut = 1260,
                interactionType = "MERGE"
            });

            var result = ParsekFlight.FindBackgroundRecordingForChain(recordings, trees, chain, 1280);

            Assert.Null(result);
        }

        /// <summary>
        /// When two branches in the same tree both claim the same vessel PID, chain
        /// trajectory resolution must follow the active branch's link path instead of
        /// picking an arbitrary same-tree recording by PID.
        /// Guards: same-tree alternate histories stay branch-specific.
        /// </summary>
        [Fact]
        public void FindExactChainRecordingAtUT_SameTreeOverlap_PicksCurrentBranch()
        {
            var root = MakeRecordingNoPoints("root", 50, 1000, 1020);
            root.ChildBranchPointId = "bp-split";

            var branchA = MakeRecordingNoPoints("branch-a", 60, 1020, 1060);
            branchA.ParentBranchPointId = "bp-split";
            branchA.ChildBranchPointId = "bp-dock-a";

            var branchB = MakeRecordingNoPoints("branch-b", 70, 1020, 1080);
            branchB.ParentBranchPointId = "bp-split";
            branchB.ChildBranchPointId = "bp-dock-b";

            var branchALeaf = MakeRecordingWithPoints("branch-a-leaf", 100, 1060, 1120);
            branchALeaf.ParentBranchPointId = "bp-dock-a";

            var branchBLeaf = MakeRecordingWithPoints("branch-b-leaf", 100, 1080, 1160);
            branchBLeaf.ParentBranchPointId = "bp-dock-b";

            var tree = MakeTree("tree-1",
                new[] { root, branchA, branchB, branchALeaf, branchBLeaf },
                new[]
                {
                    MakeBranchPoint("bp-split", BranchPointType.Breakup, 1020, 0,
                        new[] { "root" }, new[] { "branch-a", "branch-b" }),
                    MakeBranchPoint("bp-dock-a", BranchPointType.Dock, 1060, 100,
                        new[] { "branch-a" }, new[] { "branch-a-leaf" }),
                    MakeBranchPoint("bp-dock-b", BranchPointType.Dock, 1080, 100,
                        new[] { "branch-b" }, new[] { "branch-b-leaf" })
                });

            var chains = GhostChainWalker.ComputeAllGhostChains(new List<RecordingTree> { tree }, 900);
            var chain = chains[100];

            Assert.Null(ParsekFlight.FindExactChainRecordingAtUT(
                new List<RecordingTree> { tree }, chain, 1030));
            Assert.Equal("branch-a-leaf", ParsekFlight.FindExactChainRecordingAtUT(
                new List<RecordingTree> { tree }, chain, 1070).RecordingId);
            Assert.Equal("branch-b-leaf", ParsekFlight.FindExactChainRecordingAtUT(
                new List<RecordingTree> { tree }, chain, 1090).RecordingId);
        }

        /// <summary>
        /// UT equals exactly the recording's StartUT. Returns the recording.
        /// Guards: inclusive start boundary.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_ExactStartUT_ReturnsRecording()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1000);

            Assert.NotNull(result);
            Assert.Equal("bg-1", result.RecordingId);
        }

        /// <summary>
        /// UT equals exactly the recording's EndUT. Returns the recording.
        /// Guards: inclusive end boundary.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_ExactEndUT_ReturnsRecording()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1100);

            Assert.NotNull(result);
            Assert.Equal("bg-1", result.RecordingId);
        }

        #endregion

        #region FindBackgroundRecordingForVessel — edge cases

        /// <summary>
        /// UT is just before StartUT (one epsilon below). Returns null.
        /// Guards: strict lower bound.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_JustBeforeStart_ReturnsNull()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 999.999);

            Assert.Null(result);
        }

        /// <summary>
        /// UT is just after EndUT (one epsilon above). Returns null.
        /// Guards: strict upper bound.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_JustAfterEnd_ReturnsNull()
        {
            var rec = MakeRecordingWithPoints("bg-1", 100, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1100.001);

            Assert.Null(result);
        }

        /// <summary>
        /// Empty recordings list. Returns null.
        /// Guards: empty list safety.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_EmptyList_ReturnsNull()
        {
            var result = ParsekFlight.FindBackgroundRecordingForVessel(
                new List<Recording>(), 100, 1050);

            Assert.Null(result);
        }

        /// <summary>
        /// Multiple recordings for different vessels — only the matching PID is returned.
        /// Guards: vessel isolation.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_MixedVessels_ReturnsCorrectPid()
        {
            var rec1 = MakeRecordingWithPoints("bg-ship", 100, 1000, 1100);
            var rec2 = MakeRecordingWithPoints("bg-station", 200, 1000, 1100);
            var recordings = new List<Recording> { rec1, rec2 };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 200, 1050);

            Assert.NotNull(result);
            Assert.Equal("bg-station", result.RecordingId);
        }

        /// <summary>
        /// Recording has null Points list. Returns null.
        /// Guards: null Points safety.
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_NullPointsList_ReturnsNull()
        {
            var rec = new Recording
            {
                RecordingId = "bg-null-pts",
                VesselPersistentId = 100,
                ExplicitStartUT = 1000,
                ExplicitEndUT = 1100,
                Points = null
            };
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 100, 1050);

            Assert.Null(result);
        }

        /// <summary>
        /// Vessel PID = 0 matches recording with PID = 0.
        /// Guards: zero PID is valid for FindBackgroundRecordingForVessel
        /// (filtering out zero is the caller's responsibility).
        /// </summary>
        [Fact]
        public void FindBackgroundRecording_ZeroPid_MatchesZeroPidRecording()
        {
            var rec = MakeRecordingWithPoints("bg-zero", 0, 1000, 1100);
            var recordings = new List<Recording> { rec };

            var result = ParsekFlight.FindBackgroundRecordingForVessel(recordings, 0, 1050);

            Assert.NotNull(result);
            Assert.Equal("bg-zero", result.RecordingId);
        }

        #endregion
    }
}
