using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    // Phase C: the pure adapter that turns a looping Mission's selection into a span-clock
    // LoopUnitSet, plus the shared MissionSelection include cascade it depends on. No engine
    // wiring (Phase D); these drive the pure logic with directly constructed inputs.
    [Collection("Sequential")]
    public class MissionLoopUnitBuilderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissionLoopUnitBuilderTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionLoopUnitBuilder.SuppressLogging = false;
        }

        // --- Helpers (mirror MissionStructureTests) ---

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

        private static Mission LoopMission(string treeId, LoopTimeUnit unit = LoopTimeUnit.Auto,
            double interval = 0.0, string[] excluded = null)
        {
            var m = new Mission("m1", treeId, "Loopy")
            {
                LoopPlayback = true,
                LoopTimeUnit = unit,
                LoopIntervalSeconds = interval
            };
            if (excluded != null)
                foreach (var e in excluded)
                    m.ExcludedThroughLineHeadIds.Add(e);
            return m;
        }

        // A simple 3-leg env-split linear vessel tree, committed in StartUT order.
        private static RecordingTree LinearTree(string id = "t1")
        {
            return Tree(id, new[]
            {
                Leg("a", "C", 0, 100, 200),
                Leg("b", "C", 1, 200, 300),
                Leg("c", "C", 2, 300, 410)
            });
        }

        // --- MissionLoopUnitBuilder.Build ---

        [Fact]
        public void Build_NoLoopingMission_ReturnsEmpty()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission>
            {
                new Mission("m0", "t1", "NotLooping") { LoopPlayback = false }
            };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.Equal(0, set.Count);
            Assert.Same(GhostPlaybackLogic.LoopUnitSet.Empty, set);
        }

        [Fact]
        public void Build_NullOrEmptyMissions_ReturnsEmpty()
        {
            Assert.Equal(0, MissionLoopUnitBuilder.Build(null, null, null).Count);
            Assert.Equal(0, MissionLoopUnitBuilder.Build(
                new List<Mission>(), new List<RecordingTree>(), new List<Recording>()).Count);
        }

        [Fact]
        public void Build_TreeNotFound_ReturnsEmpty()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("nope") };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.Equal(0, set.Count);
            Assert.Contains(logLines, l => l.Contains("[Mission]") && l.Contains("tree not found"));
        }

        [Fact]
        public void Build_LinearMission_AllIncluded_OneUnit_AutoCadenceIsSpan()
        {
            var tree = LinearTree();
            // Committed in StartUT order: a(100..200), b(200..300), c(300..410).
            var committed = new List<Recording>
            {
                tree.Recordings["a"], tree.Recordings["b"], tree.Recordings["c"]
            };
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.Equal(1, set.Count);
            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(new[] { 0, 1, 2 }, unit.MemberIndices); // StartUT order
            Assert.Equal(0, unit.OwnerIndex);                    // earliest start
            Assert.Equal(100.0, unit.SpanStartUT);               // min start
            Assert.Equal(410.0, unit.SpanEndUT);                 // max end
            Assert.Equal(310.0, unit.CadenceSeconds);            // Auto: span (310 >= MinCycle)
            // Every member resolves to the same owner.
            Assert.True(set.IsMember(1) && set.IsMember(2));
            Assert.Equal(0, set.OwnerByIndex[2]);
        }

        [Fact]
        public void Build_OwnerAndOrder_FollowStartUT_NotCommittedOrder()
        {
            var tree = LinearTree();
            // Committed out of StartUT order: c first, then a, then b.
            var committed = new List<Recording>
            {
                tree.Recordings["c"], // index 0, start 300
                tree.Recordings["a"], // index 1, start 100
                tree.Recordings["b"]  // index 2, start 200
            };
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(1, out var unit));
            // Members sorted by StartUT: a(idx1,100), b(idx2,200), c(idx0,300).
            Assert.Equal(new[] { 1, 2, 0 }, unit.MemberIndices);
            Assert.Equal(1, unit.OwnerIndex); // a is earliest start
            Assert.Equal(100.0, unit.SpanStartUT);
            Assert.Equal(410.0, unit.SpanEndUT);
        }

        [Fact]
        public void Build_ExplicitPeriod_AboveSpan_UsesPeriod()
        {
            var tree = LinearTree(); // span = 310
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Sec, interval: 600.0) };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(600.0, unit.CadenceSeconds); // period >= span -> period
        }

        [Fact]
        public void Build_ExplicitPeriod_BelowSpan_RaisedToSpan()
        {
            var tree = LinearTree(); // span = 310
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Sec, interval: 50.0) };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(310.0, unit.CadenceSeconds); // period < span -> raised to span
        }

        [Fact]
        public void Build_TinyPeriodAndTinySpan_ClampedToMinCycleDuration()
        {
            // Single tiny leg so span < MinCycleDuration, and a zero period.
            var tree = Tree("t1", new[] { Leg("a", "C", 0, 100.0, 101.0) }); // span = 1
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Sec, interval: 0.0) };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(LoopTiming.MinCycleDuration, unit.CadenceSeconds); // 5.0
        }

        [Fact]
        public void Build_AutoTinySpan_ClampedToMinCycleDuration()
        {
            var tree = Tree("t1", new[] { Leg("a", "C", 0, 100.0, 102.0) }); // span = 2
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(LoopTiming.MinCycleDuration, unit.CadenceSeconds);
        }

        [Fact]
        public void Build_ExcludingForkHead_DropsThatBranchesMemberLegs()
        {
            // Trunk "root" (env-split root->c1->c2) with two forks at bp1: vessel-continuation
            // "c1" stays in the trunk; a separate craft "fork" branches off. Excluding the
            // fork head must drop the fork's member legs from the unit.
            var tree = Tree("t1",
                new[]
                {
                    Leg("root", "CR", 0, 0, 100),
                    Leg("c1", "CC", 0, 100, 200),
                    Leg("c2", "CC", 1, 200, 300),
                    Leg("fork", "CF", 0, 100, 260, parentAnchor: "root") // offshoot craft
                },
                new[]
                {
                    BP("bp1", BranchPointType.Breakup, new[] { "root" }, new[] { "c1", "fork" })
                });
            var committed = new List<Recording>
            {
                tree.Recordings["root"], // 0
                tree.Recordings["c1"],   // 1
                tree.Recordings["c2"],   // 2
                tree.Recordings["fork"]  // 3
            };

            // Sanity: the through-line view should expose "fork" as an offshoot head.
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(tree));
            Assert.Contains("fork", view.ByHeadId.Keys);

            // Exclude the fork head.
            var missions = new List<Mission>
            {
                LoopMission("t1", LoopTimeUnit.Auto, excluded: new[] { "fork" })
            };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            // root + c1 + c2 remain; fork (index 3) is gone.
            Assert.Equal(new[] { 0, 1, 2 }, unit.MemberIndices);
            Assert.False(set.IsMember(3));
            Assert.Equal(0.0, unit.SpanStartUT);
            Assert.Equal(300.0, unit.SpanEndUT); // fork's 260 end no longer extends the span
        }

        [Fact]
        public void Build_IncludedIdNotInCommitted_IsSkipped_NoOutOfRangeIndex()
        {
            var tree = LinearTree(); // legs a, b, c
            // "b" is missing from committed; only a and c are present.
            var committed = new List<Recording>
            {
                tree.Recordings["a"], // 0
                tree.Recordings["c"]  // 1
            };
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            // Only valid indices 0 (a) and 1 (c); never an out-of-range 2.
            Assert.Equal(new[] { 0, 1 }, unit.MemberIndices);
            Assert.All(unit.MemberIndices, idx => Assert.InRange(idx, 0, committed.Count - 1));
            Assert.Equal(100.0, unit.SpanStartUT);
            Assert.Equal(410.0, unit.SpanEndUT);
        }

        [Fact]
        public void Build_AllMembersMissingFromCommitted_ReturnsEmpty()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(); // nothing committed
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.Equal(0, set.Count);
        }

        [Fact]
        public void Build_FirstLoopingMissionWins()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission>
            {
                new Mission("m0", "t1", "Off") { LoopPlayback = false },
                LoopMission("t1", LoopTimeUnit.Auto), // first looping
                LoopMission("t1", LoopTimeUnit.Sec, interval: 9999.0) // ignored
            };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(310.0, unit.CadenceSeconds); // Auto from the FIRST looping mission
        }

        [Fact]
        public void Build_LogsSummary_OnSuccess()
        {
            var tree = LinearTree();
            var committed = new List<Recording>(tree.Recordings.Values);
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.Contains(logLines, l =>
                l.Contains("[Mission]") && l.Contains("MissionLoopUnit") &&
                l.Contains("members=3") && l.Contains("Loopy"));
        }

        [Fact]
        public void Build_DuplicateCommittedIds_FirstWins()
        {
            var tree = Tree("t1", new[] { Leg("a", "C", 0, 100, 200) });
            var dup = Leg("a", "C", 0, 100, 200); // same id, second position
            var committed = new List<Recording>
            {
                tree.Recordings["a"], // index 0 (first wins)
                dup                   // index 1
            };
            var missions = new List<Mission> { LoopMission("t1", LoopTimeUnit.Auto) };

            var set = MissionLoopUnitBuilder.Build(missions, new[] { tree }, committed);

            Assert.True(set.TryGetUnitForMember(0, out var unit));
            Assert.Equal(new[] { 0 }, unit.MemberIndices); // index 0, not 1
        }

        // --- MissionSelection.ComputeIncludedHeadIds ---

        [Fact]
        public void Selection_NullView_ReturnsEmpty()
        {
            Assert.Empty(MissionSelection.ComputeIncludedHeadIds(null, new HashSet<string>()));
        }

        [Fact]
        public void Selection_NullExcluded_IncludesEverything()
        {
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(ForkView()));
            var included = MissionSelection.ComputeIncludedHeadIds(view, null);
            Assert.Equal(view.ByHeadId.Keys.OrderBy(x => x),
                included.OrderBy(x => x));
        }

        [Fact]
        public void Selection_NoExclusions_IncludesAllRootsAndOffshoots()
        {
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(ForkView()));
            var included = MissionSelection.ComputeIncludedHeadIds(view, new HashSet<string>());

            Assert.Contains("root", included);
            Assert.Contains("fork", included);
            Assert.Equal(view.ByHeadId.Count, included.Count);
        }

        [Fact]
        public void Selection_ExcludingRoot_DropsItAndItsOffshoots()
        {
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(ForkView()));
            var excluded = new HashSet<string> { "root" };
            var included = MissionSelection.ComputeIncludedHeadIds(view, excluded);

            // root excluded -> root gone AND its offshoot "fork" cascades out.
            Assert.DoesNotContain("root", included);
            Assert.DoesNotContain("fork", included);
            Assert.Empty(included);
        }

        [Fact]
        public void Selection_ExcludingOffshoot_DropsOnlyThatBranch()
        {
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(ForkView()));
            var excluded = new HashSet<string> { "fork" };
            var included = MissionSelection.ComputeIncludedHeadIds(view, excluded);

            Assert.Contains("root", included);     // trunk stays
            Assert.DoesNotContain("fork", included); // only the fork drops
        }

        [Fact]
        public void Selection_DeepOffshootChain_CascadesFromTheExcludedAncestor()
        {
            // root -> a -> b -> c (each a separate craft branching off the previous).
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(DeepChain()));

            // No exclusions: all four heads included.
            var all = MissionSelection.ComputeIncludedHeadIds(view, new HashSet<string>());
            Assert.Equal(new[] { "a", "b", "c", "root" }, all.OrderBy(x => x).ToArray());

            // Exclude "a": a, b, c all cascade out; only root remains.
            var fromA = MissionSelection.ComputeIncludedHeadIds(view, new HashSet<string> { "a" });
            Assert.Equal(new[] { "root" }, fromA.ToArray());

            // Exclude "b": b and c cascade out; root and a remain.
            var fromB = MissionSelection.ComputeIncludedHeadIds(view, new HashSet<string> { "b" });
            Assert.Equal(new[] { "a", "root" }, fromB.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void Selection_MergeReachedTwice_DoesNotInfiniteLoop_OrDoubleCount()
        {
            // Same-tree merge: two trunks p1 and p2 both fork-parent the same child "m".
            // "m" is an offshoot of both; the visited set must stop it being traversed twice.
            var tree = Tree("t1",
                new[]
                {
                    Leg("p1", "C1", 0, 0, 100),
                    Leg("p2", "C2", 0, 0, 100),
                    Leg("m", "C3", 0, 100, 200, parentAnchor: "p1")
                },
                new[]
                {
                    BP("bp1", BranchPointType.Breakup, new[] { "p1", "p2" }, new[] { "m" })
                });
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(tree));

            // Should terminate and include each head exactly once.
            var included = MissionSelection.ComputeIncludedHeadIds(view, new HashSet<string>());
            Assert.Equal(included.Count, included.Distinct().Count());
            Assert.Contains("m", included);
        }

        [Fact]
        public void Selection_Cycle_DoesNotInfiniteLoop()
        {
            // Hand-build a deliberately cyclic view (A offshoots B, B offshoots A).
            var view = new MissionThroughLineView { TreeId = "t1" };
            var tlA = new MissionThroughLine { HeadLegId = "A", StartUT = 0 };
            var tlB = new MissionThroughLine { HeadLegId = "B", StartUT = 1 };
            tlA.OffshootHeadIds.Add("B");
            tlB.OffshootHeadIds.Add("A");
            view.ByHeadId["A"] = tlA;
            view.ByHeadId["B"] = tlB;
            view.RootHeadIds.Add("A");

            // Must terminate (the visited guard breaks the cycle).
            var included = MissionSelection.ComputeIncludedHeadIds(view, new HashSet<string>());
            Assert.Contains("A", included);
            Assert.Contains("B", included);
            Assert.Equal(2, included.Count);
        }

        // A trunk that forks off one offshoot craft "fork".
        private static RecordingTree ForkView()
        {
            return Tree("t1",
                new[]
                {
                    Leg("root", "CR", 0, 0, 200),
                    Leg("fork", "CF", 0, 100, 150, parentAnchor: "root")
                },
                new[]
                {
                    BP("bp1", BranchPointType.Breakup, new[] { "root" }, new[] { "fork" })
                });
        }

        // root -> a -> b -> c, each offshoot anchored to the previous craft.
        private static RecordingTree DeepChain()
        {
            return Tree("t1",
                new[]
                {
                    Leg("root", "CR", 0, 0, 400),
                    Leg("a", "CA", 0, 50, 350, parentAnchor: "root"),
                    Leg("b", "CB", 0, 100, 300, parentAnchor: "a"),
                    Leg("c", "CC", 0, 150, 250, parentAnchor: "b")
                },
                new[]
                {
                    BP("bpa", BranchPointType.Breakup, new[] { "root" }, new[] { "a" }),
                    BP("bpb", BranchPointType.Breakup, new[] { "a" }, new[] { "b" }),
                    BP("bpc", BranchPointType.Breakup, new[] { "b" }, new[] { "c" })
                });
        }
    }
}
