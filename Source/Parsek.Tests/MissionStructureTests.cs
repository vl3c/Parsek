using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class MissionStructureTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissionStructureTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // --- Helpers ---

        private static Recording Leg(string id, string chainId, int chainIndex,
            double start, double end, int chainBranch = 0, string vessel = "V",
            string eva = null, string parentAnchor = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = chainBranch,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                EvaCrewName = eva,
                ParentAnchorRecordingId = parentAnchor
            };
        }

        private static Recording Debris(string id, double start, double end)
        {
            return new Recording
            {
                RecordingId = id,
                IsDebris = true,
                ExplicitStartUT = start,
                ExplicitEndUT = end
            };
        }

        private static BranchPoint BP(string id, BranchPointType type,
            string[] parents, string[] children, double ut = 0)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children)
            };
        }

        private static RecordingTree Tree(string id, Recording[] recs,
            BranchPoint[] bps = null)
        {
            var tree = new RecordingTree
            {
                Id = id,
                RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null
            };
            foreach (var r in recs)
                tree.Recordings[r.RecordingId] = r;
            if (bps != null)
                tree.BranchPoints.AddRange(bps);
            return tree;
        }

        // --- Tests ---

        [Fact]
        public void SingleVesselRun_LinksEnvSplitLegsInChainIndexOrder()
        {
            var tree = Tree("t1", new[]
            {
                Leg("a", "C", 0, 100, 200),
                Leg("b", "C", 1, 200, 300),
                Leg("c", "C", 2, 300, 400)
            });

            var s = MissionStructureBuilder.Build(tree);

            Assert.Equal(3, s.LegsById.Count);
            Assert.Equal("b", s.LegsById["a"].SequenceNextId);
            Assert.Equal("c", s.LegsById["b"].SequenceNextId);
            Assert.Null(s.LegsById["c"].SequenceNextId);
            Assert.Null(s.LegsById["a"].SequencePrevId);
            Assert.Equal("a", s.LegsById["b"].SequencePrevId);
            Assert.Equal(new[] { "a" }, s.RootLegIds.ToArray());
        }

        [Fact]
        public void Run_OrdersByChainIndex_NotInsertionOrder()
        {
            // Inserted out of order (2,0,1) to prove ordering keys on ChainIndex.
            var tree = Tree("t1", new[]
            {
                Leg("c", "C", 2, 300, 400),
                Leg("a", "C", 0, 100, 200),
                Leg("b", "C", 1, 200, 300)
            });

            var s = MissionStructureBuilder.Build(tree);

            Assert.Equal("b", s.LegsById["a"].SequenceNextId);
            Assert.Equal("c", s.LegsById["b"].SequenceNextId);
            Assert.Null(s.LegsById["c"].SequenceNextId);
            Assert.Single(s.RootLegIds);
            Assert.Equal("a", s.RootLegIds[0]);
        }

        [Fact]
        public void Fork_ControlledSeparation_ProducesTwoBranchChildren()
        {
            var tree = Tree("t1",
                new[]
                {
                    Leg("trunk", "C0", 0, 100, 200),
                    Leg("aA", "C1", 0, 200, 300, vessel: "A"),
                    Leg("bB", "C2", 0, 200, 350, vessel: "B")
                },
                new[]
                {
                    BP("bp1", BranchPointType.JointBreak,
                        new[] { "trunk" }, new[] { "aA", "bB" }, ut: 200)
                });

            var s = MissionStructureBuilder.Build(tree);

            Assert.Equal(2, s.LegsById["trunk"].BranchChildIds.Count);
            Assert.Contains("aA", s.LegsById["trunk"].BranchChildIds);
            Assert.Contains("bB", s.LegsById["trunk"].BranchChildIds);
            Assert.Equal(BranchPointType.JointBreak, s.LegsById["trunk"].EndBranchPointType);
            Assert.Equal(new[] { "trunk" }, s.LegsById["aA"].BranchParentIds.ToArray());
            Assert.Equal(new[] { "trunk" }, s.LegsById["bB"].BranchParentIds.ToArray());
            Assert.Equal(BranchPointType.JointBreak, s.LegsById["aA"].OriginBranchPointType);
            // Only the trunk is a root; the two forks have a branch-parent.
            Assert.Equal(new[] { "trunk" }, s.RootLegIds.ToArray());
        }

        [Fact]
        public void Debris_IsExcludedFromLegs_AndDoesNotBecomeAChild()
        {
            var tree = Tree("t1",
                new[]
                {
                    Leg("trunk", "C0", 0, 100, 200),
                    Leg("child", "C1", 0, 200, 300)
                },
                new[]
                {
                    BP("bp1", BranchPointType.JointBreak,
                        new[] { "trunk" }, new[] { "child", "junk" }, ut: 200)
                });
            // Debris booster shed at the same separation.
            tree.Recordings["junk"] = Debris("junk", 200, 260);

            var s = MissionStructureBuilder.Build(tree);

            Assert.False(s.LegsById.ContainsKey("junk"));
            Assert.Equal(new[] { "child" }, s.LegsById["trunk"].BranchChildIds.ToArray());
            Assert.Equal(2, s.LegsById.Count);
        }

        [Fact]
        public void SameTreeMerge_DockBack_GivesChildTwoBranchParents()
        {
            var tree = Tree("t1",
                new[]
                {
                    Leg("m", "CM", 0, 100, 300, vessel: "M"),
                    Leg("d", "CD", 0, 150, 300, vessel: "D"),
                    Leg("x", "CX", 0, 300, 400, vessel: "M+D")
                },
                new[]
                {
                    BP("bp1", BranchPointType.Dock,
                        new[] { "m", "d" }, new[] { "x" }, ut: 300)
                });

            var s = MissionStructureBuilder.Build(tree);

            Assert.Equal(2, s.LegsById["x"].BranchParentIds.Count);
            Assert.Contains("m", s.LegsById["x"].BranchParentIds);
            Assert.Contains("d", s.LegsById["x"].BranchParentIds);
            Assert.Contains("x", s.LegsById["m"].BranchChildIds);
            Assert.Contains("x", s.LegsById["d"].BranchChildIds);
            // Both incoming lines are roots; the merged child is not.
            Assert.Equal(2, s.RootLegIds.Count);
            Assert.Contains("m", s.RootLegIds);
            Assert.Contains("d", s.RootLegIds);
            Assert.DoesNotContain("x", s.RootLegIds);
        }

        [Fact]
        public void ForeignDock_IsSingleParent()
        {
            // Co-parent (the foreign station) is not in this tree, so the dock
            // branch point lists only the in-tree parent.
            var tree = Tree("t1",
                new[]
                {
                    Leg("d", "CD", 0, 100, 300, vessel: "D"),
                    Leg("x", "CX", 0, 300, 400, vessel: "D@S")
                },
                new[]
                {
                    BP("bp1", BranchPointType.Dock,
                        new[] { "d" }, new[] { "x" }, ut: 300)
                });

            var s = MissionStructureBuilder.Build(tree);

            Assert.Single(s.LegsById["x"].BranchParentIds);
            Assert.Equal("d", s.LegsById["x"].BranchParentIds[0]);
            Assert.Equal(new[] { "d" }, s.RootLegIds.ToArray());
        }

        [Fact]
        public void DisconnectedContinuation_IsASeparateRoot()
        {
            // Two unconnected runs in one tree (e.g. a restore-completed switch
            // continuation with ParentBranchPointId == null and no branch point).
            var tree = Tree("t1", new[]
            {
                Leg("r1a", "C0", 0, 100, 200),
                Leg("r1b", "C0", 1, 200, 300),
                Leg("r2", "C9", 0, 500, 600, vessel: "W")
            });

            var s = MissionStructureBuilder.Build(tree);

            Assert.Equal(2, s.RootLegIds.Count);
            Assert.Contains("r1a", s.RootLegIds);
            Assert.Contains("r2", s.RootLegIds);
            Assert.DoesNotContain("r1b", s.RootLegIds);
        }

        [Fact]
        public void ParallelBranch_IsSeparateRun_NotLinkedIntoPrimary()
        {
            // ChainBranch>0 shares ChainId/ChainIndex with the primary but is a
            // distinct ghost-only continuation run.
            var tree = Tree("t1", new[]
            {
                Leg("a", "C", 0, 100, 200, chainBranch: 0),
                Leg("b", "C", 1, 200, 300, chainBranch: 0),
                Leg("p", "C", 0, 150, 250, chainBranch: 1, vessel: "P")
            });

            var s = MissionStructureBuilder.Build(tree);

            Assert.Equal("b", s.LegsById["a"].SequenceNextId);
            Assert.Null(s.LegsById["p"].SequenceNextId);
            Assert.Null(s.LegsById["p"].SequencePrevId);
            // Primary run head "a" and the parallel branch head "p" are both roots.
            Assert.Equal(2, s.RootLegIds.Count);
            Assert.Contains("a", s.RootLegIds);
            Assert.Contains("p", s.RootLegIds);
        }

        [Fact]
        public void Fork_ChildBranchContinuesAsItsOwnRun()
        {
            var tree = Tree("t1",
                new[]
                {
                    Leg("trunk", "C0", 0, 100, 200),
                    Leg("a0", "CA", 0, 200, 300, vessel: "A"),
                    Leg("a1", "CA", 1, 300, 400, vessel: "A"),
                    Leg("b", "CB", 0, 200, 350, vessel: "B")
                },
                new[]
                {
                    BP("bp1", BranchPointType.JointBreak,
                        new[] { "trunk" }, new[] { "a0", "b" }, ut: 200)
                });

            var s = MissionStructureBuilder.Build(tree);

            // Sorted by (StartUT, RecordingId): a0 and b both start at 200, so id order.
            Assert.Equal(new[] { "a0", "b" }, s.LegsById["trunk"].BranchChildIds.ToArray());
            Assert.Equal("a1", s.LegsById["a0"].SequenceNextId);
            Assert.Equal("a0", s.LegsById["a1"].SequencePrevId);
            Assert.Null(s.LegsById["a1"].SequenceNextId);
            Assert.Equal(new[] { "trunk" }, s.RootLegIds.ToArray());
        }

        [Fact]
        public void Output_IsDeterministic_RegardlessOfInsertionOrder()
        {
            var forward = Tree("t1",
                new[]
                {
                    Leg("trunk", "C0", 0, 100, 200),
                    Leg("a", "CA", 0, 200, 300, vessel: "A"),
                    Leg("b", "CB", 0, 200, 350, vessel: "B"),
                    Leg("c", "CC", 0, 200, 360, vessel: "C")
                },
                new[]
                {
                    BP("bp1", BranchPointType.JointBreak,
                        new[] { "trunk" }, new[] { "a", "b", "c" }, ut: 200)
                });
            var reverse = Tree("t1",
                new[]
                {
                    Leg("c", "CC", 0, 200, 360, vessel: "C"),
                    Leg("b", "CB", 0, 200, 350, vessel: "B"),
                    Leg("a", "CA", 0, 200, 300, vessel: "A"),
                    Leg("trunk", "C0", 0, 100, 200)
                },
                new[]
                {
                    BP("bp1", BranchPointType.JointBreak,
                        new[] { "trunk" }, new[] { "c", "b", "a" }, ut: 200)
                });

            var sf = MissionStructureBuilder.Build(forward);
            var sr = MissionStructureBuilder.Build(reverse);

            Assert.Equal(sf.RootLegIds.ToArray(), sr.RootLegIds.ToArray());
            Assert.Equal(
                sf.LegsById["trunk"].BranchChildIds.ToArray(),
                sr.LegsById["trunk"].BranchChildIds.ToArray());
            // Documented (StartUT, RecordingId) order; all three children start at 200.
            Assert.Equal(new[] { "a", "b", "c" }, sf.LegsById["trunk"].BranchChildIds.ToArray());
        }

        [Fact]
        public void ContinuesAsVessel_And_OffshootFlags_AreSetCorrectly()
        {
            // Root branches into the continuing vessel (no crew, no anchor) plus two
            // offshoots: an EVA kerbal and an anchored probe.
            var tree = Tree("t1",
                new[]
                {
                    Leg("root", "C0", 0, 0, 100),
                    Leg("cont", "C1", 0, 100, 200),
                    Leg("kerb", "C2", 0, 100, 150, eva: "Bob Kerman"),
                    Leg("probe", "C3", 0, 100, 140, parentAnchor: "root")
                },
                new[]
                {
                    BP("bp1", BranchPointType.EVA, new[] { "root" }, new[] { "cont", "kerb", "probe" })
                });
            tree.Recordings["cont"].TerminalStateValue = TerminalState.Landed;

            var s = MissionStructureBuilder.Build(tree);

            // The root continues as the vessel ("cont"), so it is not a terminal leg.
            Assert.True(s.LegsById["root"].ContinuesAsVessel);
            // A leaf continuation with a terminal does not itself continue.
            Assert.False(s.LegsById["cont"].ContinuesAsVessel);
            Assert.False(s.LegsById["cont"].IsAnchoredOffshoot);
            Assert.True(string.IsNullOrEmpty(s.LegsById["cont"].EvaCrewName));
            // Offshoots are flagged so they get a start event, not "Continues".
            Assert.Equal("Bob Kerman", s.LegsById["kerb"].EvaCrewName);
            Assert.True(s.LegsById["probe"].IsAnchoredOffshoot);
        }

        [Fact]
        public void EmptyOrNullTree_ReturnsEmptyStructure()
        {
            var sNull = MissionStructureBuilder.Build(null);
            Assert.Empty(sNull.LegsById);
            Assert.Empty(sNull.RootLegIds);

            var sEmpty = MissionStructureBuilder.Build(new RecordingTree { Id = "t" });
            Assert.Equal("t", sEmpty.TreeId);
            Assert.Empty(sEmpty.LegsById);
            Assert.Empty(sEmpty.RootLegIds);
        }
    }
}
