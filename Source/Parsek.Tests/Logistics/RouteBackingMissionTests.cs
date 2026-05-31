using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Phase 1 (Checkpoint A): the route backing-mission derivation helper.
    /// <see cref="RouteBackingMission.ComputeExcludedIntervalKeys"/> must trim the
    /// rendered window to <c>[launch .. undock]</c> off the tree ROOT, and
    /// <see cref="RouteBackingMission.BuildMission"/> must produce a route-owned
    /// Mission that never touches <c>MissionStore</c>.
    /// </summary>
    [Collection("Sequential")]
    public class RouteBackingMissionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteBackingMissionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionStore.ResetForTesting();
        }

        // -----------------------------------------------------------------
        // Tree fixture helpers (mirror MissionStructureTests)
        // -----------------------------------------------------------------

        private static Recording Leg(string id, string chainId, int chainIndex,
            double start, double end, int chainBranch = 0, string vessel = "V")
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
                ExplicitEndUT = end
            };
        }

        private static BranchPoint BP(string id, BranchPointType type,
            string[] parents, string[] children, double ut)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                SplitCause = type == BranchPointType.Undock ? "UNDOCK" : null,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children)
            };
        }

        private static RecordingTree Tree(string id, Recording[] recs, BranchPoint[] bps = null)
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

        // Multi-leg flight: launch (root) -> dock (mid-flight) -> undock, with a
        // payload left behind at undock so the post-undock survivor renders as its
        // own composition interval.
        //   launch  C0/0  [1000..2000]  (the transport launches; ROOT)
        //   docked  C0/1  [2000..3000]  (Dock BP@2000 merges launch + station)
        //   survivor C0/2 [3000..4000]  (Undock BP@3000; the transport continues)
        //   payload C1/0  [3000..3500]  (a separate vessel left behind at undock)
        // ROOT launch UT = 1000 (NOT the mid-flight dock child's 2000).
        private const double RootLaunchUT = 1000.0;
        private const double UndockUT = 3000.0;

        private static RecordingTree BuildLaunchDockUndockTree()
        {
            return Tree("tree-dock", new[]
                {
                    Leg("launch", "C0", 0, 1000, 2000, vessel: "Transport"),
                    Leg("docked", "C0", 1, 2000, 3000, vessel: "Transport"),
                    Leg("survivor", "C0", 2, 3000, 4000, vessel: "Transport"),
                    Leg("payload", "C1", 0, 3000, 3500, vessel: "Payload")
                },
                new[]
                {
                    BP("dock-bp", BranchPointType.Dock,
                        new[] { "launch" }, new[] { "docked" }, ut: 2000),
                    // survivor first => recorder's continuation; payload is the peel.
                    BP("undock-bp", BranchPointType.Undock,
                        new[] { "docked" }, new[] { "survivor", "payload" }, ut: 3000)
                });
        }

        // -----------------------------------------------------------------
        // ComputeExcludedIntervalKeys
        // -----------------------------------------------------------------

        // catches: window START keyed off the mid-flight dock child instead of the
        // tree ROOT, and post-undock segments not trimmed.
        [Fact]
        public void Compute_MultiLegTree_KeepsLaunchToUndock_ExcludesPostUndock()
        {
            RecordingTree tree = BuildLaunchDockUndockTree();

            HashSet<string> excluded =
                RouteBackingMission.ComputeExcludedIntervalKeys(tree, UndockUT, RootLaunchUT);

            // Build the composition windows under this exclusion and assert the
            // transport renders [root.StartUT .. undockUT], not the dock-child start
            // and not past the undock.
            var structure = MissionStructureBuilder.Build(tree);
            var roots = MissionCompositionBuilder.Build(structure);
            var windows = MissionIntervalSelection.ComputeRenderWindows(roots, excluded);

            // The transport through-line is owned by the ROOT launch leg.
            Assert.True(windows.ContainsKey("launch"),
                "transport (root-owned) vessel must still render");
            MissionIntervalSelection.RenderWindow w = windows["launch"];
            Assert.Equal(RootLaunchUT, w.StartUT); // == 1000, the ROOT launch, NOT 2000
            Assert.Equal(UndockUT, w.EndUT);       // end-trimmed at the undock

            // The post-undock payload vessel is fully dropped.
            Assert.False(windows.ContainsKey("payload"),
                "post-undock payload must be excluded");

            // Excluded keys are non-empty; the kept launch interval-0 key
            // (the bare root leg id) is NOT excluded.
            Assert.NotEmpty(excluded);
            Assert.DoesNotContain("launch", excluded); // interval 0 stays in
            Assert.Contains("payload", excluded);      // post-undock offshoot dropped

            // Summary log fired with the tree id and undock UT.
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("ComputeExcludedIntervalKeys") &&
                l.Contains("tree=tree-dock"));
        }

        // catches: an interval STARTING exactly at the undock boundary being kept
        // (it is post-undock) or an interval ENDING at the undock being dropped.
        [Fact]
        public void Compute_ExactBoundary_StartAtUndockExcluded_EndAtUndockKept()
        {
            RecordingTree tree = BuildLaunchDockUndockTree();

            var structure = MissionStructureBuilder.Build(tree);
            var roots = MissionCompositionBuilder.Build(structure);

            HashSet<string> excluded =
                RouteBackingMission.ComputeExcludedIntervalKeys(tree, UndockUT, RootLaunchUT);
            var windows = MissionIntervalSelection.ComputeRenderWindows(roots, excluded);

            // The first interval ENDS at the undock (3000) and is KEPT.
            // The post-undock interval STARTS at the undock (3000) and is EXCLUDED.
            // Net: the kept window's EndUT is exactly the undock instant.
            Assert.True(windows.ContainsKey("launch"));
            Assert.Equal(UndockUT, windows["launch"].EndUT);
        }

        // catches: a single-interval tree (no post-undock structure) producing a
        // spurious exclusion. Undock at/after the only interval's end -> nothing to
        // trim, empty set, whole segment renders.
        [Fact]
        public void Compute_SingleInterval_NoPostUndockStructure_EmptySet()
        {
            // A plain launch-only tree; undockUT == the leg end.
            var tree = Tree("tree-solo", new[]
            {
                Leg("solo", "C0", 0, 1000, 2000, vessel: "Transport")
            });

            HashSet<string> excluded =
                RouteBackingMission.ComputeExcludedIntervalKeys(tree, undockUT: 2000.0, launchUT: 1000.0);

            Assert.Empty(excluded);
        }

        // catches: NaN inputs / inverted window producing exceptions or a non-empty
        // set instead of the honest whole-segment fallback.
        [Theory]
        [InlineData(double.NaN, 1000.0)]
        [InlineData(3000.0, double.NaN)]
        [InlineData(1000.0, 1000.0)]   // undock <= launch
        [InlineData(500.0, 1000.0)]    // undock < launch
        public void Compute_BadWindow_FallsBackToEmptySetAndLogs(double undockUT, double launchUT)
        {
            RecordingTree tree = BuildLaunchDockUndockTree();

            HashSet<string> excluded =
                RouteBackingMission.ComputeExcludedIntervalKeys(tree, undockUT, launchUT);

            Assert.Empty(excluded);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("ComputeExcludedIntervalKeys") &&
                l.Contains("whole segment renders"));
        }

        [Fact]
        public void Compute_NullTree_EmptySetAndLogs()
        {
            HashSet<string> excluded =
                RouteBackingMission.ComputeExcludedIntervalKeys(null, 3000.0, 1000.0);

            Assert.Empty(excluded);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("ComputeExcludedIntervalKeys") &&
                l.Contains("tree=<null>"));
        }

        // -----------------------------------------------------------------
        // BuildMission
        // -----------------------------------------------------------------

        // catches: the route-owned Mission leaking into MissionStore, the excluded
        // set not being copied, the coarse ExcludedThroughLineHeadIds field being
        // populated, or the loop schedule not coming from the route.
        [Fact]
        public void BuildMission_CopiesExcludedSet_SetsLoopFields_NeverTouchesStore()
        {
            int storeBefore = MissionStore.Missions.Count;

            Route route = new RouteFixtureBuilder()
                .WithId("route-abc12345")
                .WithName("Mun Fuel Run")
                .WithBackingMissionTreeId("tree-dock")
                .WithExcludedIntervalKey("launch/seg1")
                .WithExcludedIntervalKey("payload")
                .WithSchedule(2000.0, 43200.0)   // transit, dispatchInterval
                .WithLoopAnchorUT(123456.0)
                .Build();

            Mission mission = RouteBackingMission.BuildMission(route, currentUT: 200000.0);

            Assert.NotNull(mission);
            // Stable derived id keyed off the route id.
            Assert.Equal("route-abc12345-backing", mission.Id);
            Assert.Equal("tree-dock", mission.TreeId);
            Assert.Equal("Mun Fuel Run", mission.Name);

            // Excluded set copied into ExcludedIntervalKeys ONLY.
            Assert.Equal(2, mission.ExcludedIntervalKeys.Count);
            Assert.Contains("launch/seg1", mission.ExcludedIntervalKeys);
            Assert.Contains("payload", mission.ExcludedIntervalKeys);
            Assert.Empty(mission.ExcludedThroughLineHeadIds);

            // Loop fields from the route schedule.
            Assert.True(mission.LoopPlayback);
            Assert.Equal(route.DispatchInterval, mission.LoopIntervalSeconds);
            Assert.Equal(LoopTimeUnit.Sec, mission.LoopTimeUnit);
            Assert.Equal(123456.0, mission.LoopAnchorUT);

            // The route-owned Mission NEVER reaches MissionStore.
            Assert.Equal(storeBefore, MissionStore.Missions.Count);

            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("BuildMission") &&
                l.Contains("route-abc12345-backing"));
        }

        [Fact]
        public void BuildMission_NullRoute_ReturnsNull()
        {
            Mission mission = RouteBackingMission.BuildMission(null, 100.0);
            Assert.Null(mission);
        }
    }
}
