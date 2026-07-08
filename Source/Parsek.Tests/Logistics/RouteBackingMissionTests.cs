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
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionStore.ResetForTesting();
            RecordingStore.ResetForTesting();
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
        // Since M-MIS-5 the dock at 2000 is itself an interval boundary (the docked
        // stretch keys as "launch@dock1"), and production passes the DOCK UT as
        // segmentEndUT (RouteBuilder passes ConnectionWindow.DockUT), so the
        // fixtures below trim at DockUT = 2000 - passing the undock 3000 would keep
        // the docked stretch rendered and no longer model production.
        private const double RootLaunchUT = 1000.0;
        private const double DockUT = 2000.0;
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

        // catches (M-MIS-5 D4, supersedes the pre-M-MIS-5 undock-end pin): with the dock
        // edge emitted, production's segmentEndUT = the DOCK UT excludes the docked
        // combined stretch ("launch@dock1", which STARTS at the dock), so the rendered
        // window ends AT THE DOCK - what the RouteBuilder comments claimed all along.
        // Also catches: window START keyed off the mid-flight dock child instead of the
        // tree ROOT.
        [Fact]
        public void Compute_DockEdge_WindowEndsAtDock()
        {
            RecordingTree tree = BuildLaunchDockUndockTree();

            HashSet<string> excluded =
                RouteBackingMission.ComputeExcludedIntervalKeys(tree, DockUT, RootLaunchUT);

            // Build the composition windows under this exclusion and assert the
            // transport renders [root.StartUT .. dockUT]: not the dock-child start,
            // not the docked stretch, not past the undock.
            var structure = MissionStructureBuilder.Build(tree);
            var roots = MissionCompositionBuilder.Build(structure);
            var windows = MissionIntervalSelection.ComputeRenderWindows(roots, excluded);

            // The transport through-line is owned by the ROOT launch leg.
            Assert.True(windows.ContainsKey("launch"),
                "transport (root-owned) vessel must still render");
            MissionIntervalSelection.RenderWindow w = windows["launch"];
            Assert.Equal(RootLaunchUT, w.StartUT); // == 1000, the ROOT launch, NOT 2000
            Assert.Equal(DockUT, w.EndUT);         // end-trimmed at the DOCK (M-MIS-5 flip)

            // The post-undock payload vessel is fully dropped.
            Assert.False(windows.ContainsKey("payload"),
                "post-undock payload must be excluded");

            // Excluded keys: the docked combined sub-interval, the post-undock
            // survivor interval, and the peel; the kept launch interval-0 key stays.
            Assert.DoesNotContain("launch", excluded);      // interval 0 stays in
            Assert.Contains("launch@dock1", excluded);      // the docked combined stretch
            Assert.Contains("launch/seg1", excluded);       // post-undock survivor interval
            Assert.Contains("payload", excluded);           // post-undock offshoot dropped
            Assert.Equal(3, excluded.Count);

            // Summary log fired with the tree id.
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("ComputeExcludedIntervalKeys") &&
                l.Contains("tree=tree-dock"));
        }

        // catches: an interval STARTING exactly at the dock boundary being kept
        // (it is the docked combined stretch) or an interval ENDING at the dock
        // being dropped.
        [Fact]
        public void Compute_ExactBoundary_StartAtDockExcluded_EndAtDockKept()
        {
            RecordingTree tree = BuildLaunchDockUndockTree();

            var structure = MissionStructureBuilder.Build(tree);
            var roots = MissionCompositionBuilder.Build(structure);

            HashSet<string> excluded =
                RouteBackingMission.ComputeExcludedIntervalKeys(tree, DockUT, RootLaunchUT);
            var windows = MissionIntervalSelection.ComputeRenderWindows(roots, excluded);

            // The first interval ENDS at the dock (2000) and is KEPT.
            // The docked sub-interval STARTS at the dock (2000) and is EXCLUDED.
            // Net: the kept window's EndUT is exactly the dock instant.
            Assert.True(windows.ContainsKey("launch"));
            Assert.Equal(DockUT, windows["launch"].EndUT);
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
                RouteBackingMission.ComputeExcludedIntervalKeys(tree, segmentEndUT: 2000.0, launchUT: 1000.0);

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
        // ComputeMemberRecordingIds (must-fix #3)
        // -----------------------------------------------------------------

        // catches: the member set not covering the kept [root..dock] path, or the
        // docked stretch / post-undock survivor / payload leaking into the member
        // set. NOTE: the transport's legs are ONE composition through-line owned by
        // the root "launch" leg (its intervals key as "launch" / "launch@dockM" /
        // "launch/segN", all stripping to "launch"), so the transport surfaces as
        // the single member "launch". The dock-child leaf id is added separately by
        // RouteBuilder (the delivery-binding carrier), not by this helper.
        [Fact]
        public void ComputeMembers_MultiLegTree_CoversTransportThroughLine_ExcludesPostDock()
        {
            RecordingTree tree = BuildLaunchDockUndockTree();

            HashSet<string> members =
                RouteBackingMission.ComputeMemberRecordingIds(tree, DockUT, RootLaunchUT);

            // The transport through-line renders up to the dock and surfaces as the
            // root-owned "launch" member.
            Assert.Contains("launch", members);
            // The post-undock survivor and the peeled payload are NOT members.
            Assert.DoesNotContain("survivor", members);
            Assert.DoesNotContain("payload", members);
        }

        // catches: a malformed window not falling back to the whole-segment member
        // set (or NaN inputs throwing).
        [Theory]
        [InlineData(double.NaN, 1000.0)]
        [InlineData(3000.0, double.NaN)]
        [InlineData(1000.0, 1000.0)]
        public void ComputeMembers_BadWindow_FallsBackToWholeSegment(double undockUT, double launchUT)
        {
            RecordingTree tree = BuildLaunchDockUndockTree();

            HashSet<string> members =
                RouteBackingMission.ComputeMemberRecordingIds(tree, undockUT, launchUT);

            // Whole-segment fallback: at minimum the launch root is a member.
            Assert.NotEmpty(members);
            Assert.Contains("launch", members);
        }

        // catches: a null tree throwing instead of returning empty.
        [Fact]
        public void ComputeMembers_NullTree_Empty()
        {
            HashSet<string> members =
                RouteBackingMission.ComputeMemberRecordingIds(null, 3000.0, 1000.0);
            Assert.Empty(members);
        }

        // catches: an earlier in-flight undock (before the route's dock cycle)
        // wrongly trimming the survivor that continues on to the dock. The
        // terminal-undock scoping must key the trim on the undock AT/nearest
        // undockUT only.
        [Fact]
        public void ComputeMembers_EarlierUndock_DoesNotTrimContinuingSurvivor()
        {
            // launch C0/0 [1000..1500]
            // (an EARLY undock at 1500 peels a probe; the transport continues)
            // mid    C0/1 [1500..2500]  (transport continues past the early undock)
            // probe  C1/0 [1500..1800]  (peeled at the early undock; unrelated)
            // docked C0/2 [2500..3000]  (Dock BP@2500 merges; this is pre-route-undock)
            // (route undock at 3000 peels payload)
            // survivor C0/3 [3000..3500]
            // payload  C2/0 [3000..3300]
            var tree = Tree("tree-early", new[]
                {
                    Leg("launch", "C0", 0, 1000, 1500, vessel: "Transport"),
                    Leg("mid", "C0", 1, 1500, 2500, vessel: "Transport"),
                    Leg("probe", "C1", 0, 1500, 1800, vessel: "Probe"),
                    Leg("docked", "C0", 2, 2500, 3000, vessel: "Transport"),
                    Leg("survivor", "C0", 3, 3000, 3500, vessel: "Transport"),
                    Leg("payload", "C2", 0, 3000, 3300, vessel: "Payload")
                },
                new[]
                {
                    BP("early-undock", BranchPointType.Undock,
                        new[] { "launch" }, new[] { "mid", "probe" }, ut: 1500),
                    BP("dock-bp", BranchPointType.Dock,
                        new[] { "mid" }, new[] { "docked" }, ut: 2500),
                    BP("route-undock", BranchPointType.Undock,
                        new[] { "docked" }, new[] { "survivor", "payload" }, ut: 3000)
                });

            // Production passes the route's DOCK UT (2500, M-MIS-5) as segmentEndUT.
            HashSet<string> members =
                RouteBackingMission.ComputeMemberRecordingIds(tree, segmentEndUT: 2500.0, launchUT: 1000.0);

            // The transport's full pre-dock through-line is kept; the early
            // undock at 1500 does NOT trim the continuing transport.
            Assert.Contains("launch", members);
            // The docked stretch and post-route-undock survivor / payload are excluded.
            Assert.DoesNotContain("survivor", members);
            Assert.DoesNotContain("payload", members);
        }

        // -----------------------------------------------------------------
        // ComputeAutoExcludedNewIntervalKeys (M-MIS-9 branch freeze)
        // -----------------------------------------------------------------

        // A route captured at creation time over BuildLaunchDockUndockTree:
        // SourceRefs cover the rendered members (the "launch" through-line head
        // plus the dock-child leaf "docked"), the dock binding carries the
        // recorded DOCK UT (2000 - production's RecordedDockUT is genuinely the
        // dock, M-MIS-5), and the creation-time trim is the production
        // ComputeExcludedIntervalKeys output
        // ({launch@dock1, launch/seg1, payload}).
        private static Route FrozenRouteOverDockTree(
            RecordingTree creationTree, string id, bool bindDock = true)
        {
            var builder = new RouteFixtureBuilder()
                .WithId(id)
                .WithName("Frozen Route")
                .WithBackingMissionTreeId(creationTree.Id)
                .WithSourceRef(new RouteSourceRef { RecordingId = "launch", TreeId = creationTree.Id })
                .WithSourceRef(new RouteSourceRef { RecordingId = "docked", TreeId = creationTree.Id })
                .WithSchedule(2000.0, 2000.0)
                .WithLoopAnchorUT(1000.0);
            if (bindDock)
                builder.WithDockBinding(DockUT, "docked");
            Route route = builder.Build();
            foreach (string key in RouteBackingMission.ComputeExcludedIntervalKeys(
                         creationTree, DockUT, RootLaunchUT))
                route.ExcludedIntervalKeys.Add(key);
            return route;
        }

        // catches: the positional-key hole (review finding 1) in the pure
        // method - a post-creation peel on the KNOWN member through-line
        // RENUMBERS its intervals and mints a base-known post-dock tail key
        // ("launch/seg2") that the base-id rule alone would include; only the
        // UT end-trim prong catches it. Pre-dock intervals of the known member
        // must stay included, and the new recording's key must be excluded.
        //
        // CAVEAT (re-review finding 1): this pins the PURE derivation only. The
        // same grown-tree shape also renumbers what the route's stale
        // creation-time excluded key STRINGS denote (a creation key can come to
        // name a pre-dock member window), which the BuildMission union would
        // over-exclude - that inverse direction is unreachable in production
        // because every pre-dock member-path mutation changes a SourceRef
        // fingerprint and RevalidateSources flips the route off ghost-driving
        // first. Do not read this test as full-pipeline proof for pre-dock
        // renumbering.
        [Fact]
        public void ComputeAutoExcluded_NewSegPeelOfKnownMember_PreDockKept_PostDockTailTrimmed()
        {
            RecordingTree creationTree = BuildLaunchDockUndockTree();
            Route route = FrozenRouteOverDockTree(creationTree, "route-baseid01");

            // The tree GROWS after creation: a probe peels off the KNOWN member
            // through-line "launch" mid-ascent. Intervals renumber to
            // launch [1000..1500], launch/seg1 [1500..2000], launch/seg1@dock1
            // [2000..3000], launch/seg2 [3000..4000] - all base "launch" (known) -
            // and the probe surfaces as a NEW recording's key.
            RecordingTree grown = BuildLaunchDockUndockTree();
            grown.Recordings["newprobe"] = Leg("newprobe", "C2", 0, 1500, 1800, vessel: "Probe");
            grown.BranchPoints.Add(BP("probe-bp", BranchPointType.Undock,
                new[] { "launch" }, new[] { "newprobe" }, ut: 1500));

            HashSet<string> auto =
                RouteBackingMission.ComputeAutoExcludedNewIntervalKeys(grown, route);

            // Prong 1 (base-id rule): the NEW recording's key is auto-excluded.
            Assert.Contains("newprobe", auto);
            // Prong 2 (UT end-trim): the renumbered base-KNOWN docked stretch
            // ("launch/seg1@dock1", starts at the dock 2000) and post-undock tail
            // ("launch/seg2") are auto-excluded too.
            Assert.Contains("launch/seg1@dock1", auto);
            Assert.Contains("launch/seg2", auto);
            Assert.Equal(3, auto.Count);
            // Pre-dock intervals of the known member stay included.
            Assert.DoesNotContain("launch", auto);
            Assert.DoesNotContain("launch/seg1", auto);
            Assert.DoesNotContain("payload", auto);

            // Batch summary log fired with the per-prong counts.
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("ComputeAutoExcludedNewIntervalKeys") &&
                l.Contains("autoExcluded=1") &&
                l.Contains("utTrimAdded=2") &&
                l.Contains("total=3"));
        }

        // catches: the base-id rule in isolation (UT trim gated off: the route
        // carries no dock binding, so RecordedDockUT is unset) - a new "/segN"
        // re-peel of a known member recording is NOT auto-excluded; only the
        // new recording is, and the skipped trim is logged.
        [Fact]
        public void ComputeAutoExcluded_BaseIdRuleAlone_NewSegPeelKept_NewBranchExcluded()
        {
            RecordingTree creationTree = BuildLaunchDockUndockTree();
            Route route = FrozenRouteOverDockTree(creationTree, "route-baseid02", bindDock: false);

            RecordingTree grown = BuildLaunchDockUndockTree();
            grown.Recordings["newprobe"] = Leg("newprobe", "C2", 0, 1500, 1800, vessel: "Probe");
            grown.BranchPoints.Add(BP("probe-bp", BranchPointType.Undock,
                new[] { "launch" }, new[] { "newprobe" }, ut: 1500));

            HashSet<string> auto =
                RouteBackingMission.ComputeAutoExcludedNewIntervalKeys(grown, route);

            Assert.Contains("newprobe", auto);
            Assert.Single(auto);
            Assert.DoesNotContain(auto, k =>
                k == "launch" || k.StartsWith("launch/seg", StringComparison.Ordinal)
                || k.StartsWith("launch@dock", StringComparison.Ordinal));

            // The skipped UT trim is logged (RecordedDockUT unset).
            Assert.Contains(logLines, l =>
                l.Contains("ComputeAutoExcludedNewIntervalKeys") &&
                l.Contains("ut-trim skipped"));
        }

        // catches: the fail-open guard missing (review finding 3) - a route
        // with creation-time excluded keys but NO SourceRefs would otherwise
        // auto-exclude the whole member path (the known-base set would hold
        // only excluded bases).
        [Fact]
        public void ComputeAutoExcluded_EmptySourceRefs_FailsOpenToEmpty()
        {
            Route route = new RouteFixtureBuilder()
                .WithId("route-norefs01")
                .WithBackingMissionTreeId("tree-dock")
                .WithExcludedIntervalKey("launch@dock1")
                .WithExcludedIntervalKey("launch/seg1")
                .WithExcludedIntervalKey("payload")
                .WithDockBinding(DockUT, "docked")
                .Build();   // NO SourceRefs

            RecordingTree grown = BuildLaunchDockUndockTree();
            grown.Recordings["fork"] = Leg("fork", "C2", 0, 3000, 6000, vessel: "Fork");
            grown.BranchPoints.Find(b => b.Id == "undock-bp").ChildRecordingIds.Add("fork");

            HashSet<string> auto =
                RouteBackingMission.ComputeAutoExcludedNewIntervalKeys(grown, route);

            Assert.Empty(auto);
            Assert.Contains(logLines, l =>
                l.Contains("ComputeAutoExcludedNewIntervalKeys") &&
                l.Contains("sourceRefs=empty") &&
                l.Contains("fail-open"));
        }

        // catches: the freeze overriding the honest whole-segment-fallback
        // contract. A route whose creation-time excluded set is EMPTY (creation
        // could not trim) must keep rendering the whole segment - the derivation
        // returns empty even when a new branch exists.
        [Fact]
        public void ComputeAutoExcluded_EmptyCreationExcludedSet_HonestFallbackPreserved()
        {
            Route route = new RouteFixtureBuilder()
                .WithId("route-fallback01")
                .WithBackingMissionTreeId("tree-dock")
                .WithSourceRef(new RouteSourceRef { RecordingId = "launch", TreeId = "tree-dock" })
                .Build();   // NO excluded interval keys: honest whole-segment fallback

            RecordingTree grown = BuildLaunchDockUndockTree();
            grown.Recordings["fork"] = Leg("fork", "C2", 0, 3000, 6000, vessel: "Fork");
            grown.BranchPoints.Find(b => b.Id == "undock-bp").ChildRecordingIds.Add("fork");

            HashSet<string> auto =
                RouteBackingMission.ComputeAutoExcludedNewIntervalKeys(grown, route);

            Assert.Empty(auto);
            Assert.Contains(logLines, l =>
                l.Contains("ComputeAutoExcludedNewIntervalKeys") &&
                l.Contains("honest whole-segment fallback preserved"));
        }

        // catches: null tree / null route throwing instead of the empty guard.
        [Fact]
        public void ComputeAutoExcluded_NullTreeOrRoute_ReturnsEmptyAndLogs()
        {
            Assert.Empty(RouteBackingMission.ComputeAutoExcludedNewIntervalKeys(
                null, new RouteFixtureBuilder().Build()));
            Assert.Empty(RouteBackingMission.ComputeAutoExcludedNewIntervalKeys(
                BuildLaunchDockUndockTree(), null));
            Assert.Contains(logLines, l =>
                l.Contains("ComputeAutoExcludedNewIntervalKeys") &&
                l.Contains("-> empty (no derivation)"));
        }

        // catches: the signature gate re-running the composition walk on an
        // UNCHANGED tree (per-frame cost + log spam), failing to re-derive when
        // the tree grows, the Info line missing/duplicated, or the cached set
        // not landing in subsequent missions.
        [Fact]
        public void BuildMission_SignatureGate_DerivesOncePerTopology_RederivesOnGrowth()
        {
            double clock = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => clock;
            ParsekLog.ResetRateLimitsForTesting();

            RecordingTree tree = BuildLaunchDockUndockTree();
            Route route = FrozenRouteOverDockTree(tree, "route-siggate01");
            RecordingStore.CommittedTrees.Add(tree);

            // Two same-topology builds: exactly ONE derivation (the cache hit
            // skips the walk and logs nothing) and nothing auto-excluded.
            Mission m1 = RouteBackingMission.BuildMission(route, clock);
            RouteBackingMission.BuildMission(route, clock);
            Assert.Single(logLines.FindAll(l =>
                l.Contains("ComputeAutoExcludedNewIntervalKeys")));
            Assert.NotNull(route.AutoExcludedNewIntervalKeys);
            Assert.Empty(route.AutoExcludedNewIntervalKeys);
            Assert.Equal(3, m1.ExcludedIntervalKeys.Count); // creation-time keys only
            Assert.DoesNotContain(logLines, l => l.Contains("after route creation"));

            // The tree GROWS (a post-creation fork at the undock): the counts
            // move, the gate re-derives, the Info line fires once, and the new
            // key lands in the next synthesized mission (and the folded
            // rate-limited BuildMission line counts it).
            clock += 6.0; // past the 5s rate-limit window so the next build line emits
            tree.Recordings["fork"] = Leg("fork", "C2", 0, 3000, 6000, vessel: "Fork");
            tree.BranchPoints.Find(b => b.Id == "undock-bp").ChildRecordingIds.Add("fork");
            Mission m3 = RouteBackingMission.BuildMission(route, clock);
            Assert.Equal(2, logLines.FindAll(l =>
                l.Contains("ComputeAutoExcludedNewIntervalKeys")).Count);
            Assert.Contains("fork", route.AutoExcludedNewIntervalKeys);
            Assert.Contains("fork", m3.ExcludedIntervalKeys);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("auto-excluded") &&
                l.Contains("tree-dock") && l.Contains("after route creation"));
            Assert.Contains(logLines, l =>
                l.Contains("BuildMission: id=route-siggate01-backing") &&
                l.Contains("autoExcludedKeys=1"));

            // Stable again: the fourth build reuses the cache (still 2
            // derivations), still excludes the fork, and does NOT repeat Info.
            Mission m4 = RouteBackingMission.BuildMission(route, clock);
            Assert.Equal(2, logLines.FindAll(l =>
                l.Contains("ComputeAutoExcludedNewIntervalKeys")).Count);
            Assert.Contains("fork", m4.ExcludedIntervalKeys);
            Assert.Single(logLines.FindAll(l =>
                l.Contains("after route creation")));

            // Shrink (review finding 6): the fork is discarded again. The id
            // hashes move, the gate re-derives to EMPTY, and the change still
            // Info-logs (the freeze releasing is news too).
            tree.Recordings.Remove("fork");
            tree.BranchPoints.Find(b => b.Id == "undock-bp").ChildRecordingIds.Remove("fork");
            Mission m5 = RouteBackingMission.BuildMission(route, clock);
            Assert.Equal(3, logLines.FindAll(l =>
                l.Contains("ComputeAutoExcludedNewIntervalKeys")).Count);
            Assert.Empty(route.AutoExcludedNewIntervalKeys);
            Assert.Equal(3, m5.ExcludedIntervalKeys.Count); // creation-time keys only
            Assert.Equal(2, logLines.FindAll(l =>
                l.Contains("after route creation")).Count);
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

        // catches: a regression where BuildMission logs UNCONDITIONALLY (the cause of
        // the ~21k-line route log flood). It is called every render frame by the
        // ghost-driving selector (and every delivery-clock tick), so its per-build line
        // must be rate-limited per route - one line, then quiet until the window passes.
        [Fact]
        public void BuildMission_RateLimitsItsPerFrameLog()
        {
            double clock = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => clock;
            ParsekLog.ResetRateLimitsForTesting();

            Route route = new RouteFixtureBuilder()
                .WithId("route-flood01")
                .WithName("Flood Test")
                .WithBackingMissionTreeId("tree-1")
                .WithSchedule(2000.0, 100.0)
                .Build();

            // Many per-frame builds at the SAME instant: exactly one log line.
            for (int i = 0; i < 50; i++)
                RouteBackingMission.BuildMission(route, clock);

            Assert.Single(logLines, l =>
                l.Contains("BuildMission: id=route-flood01-backing"));

            // After the 5s real-time window, the next build logs again (a heartbeat,
            // not silenced forever).
            clock += 6.0;
            RouteBackingMission.BuildMission(route, clock);
            Assert.Equal(2, logLines.FindAll(l =>
                l.Contains("BuildMission: id=route-flood01-backing")).Count);
        }

        // -----------------------------------------------------------------
        // StripSegMarker (M-MIS-5 R3: the "@dock" strip is LOAD-BEARING)
        // -----------------------------------------------------------------

        // catches (R3): a bare-head "@dockM" key not stripping to its base recording id -
        // M-MIS-9 prong 1 routes every persisted key through StripSegMarker, so without the
        // "@dock" strip every bare-head @dock key would misclassify as an unknown base and be
        // wrongly auto-excluded (the route no-flag self-heal breaks).
        [Theory]
        [InlineData("rec-a", "rec-a")]                       // bare recording id, untouched
        [InlineData("rec-a/seg2", "rec-a")]                  // structural sub-segment
        [InlineData("rec-a@dock1", "rec-a")]                 // dock sub-interval of the BARE head
        [InlineData("rec-a/seg2@dock1", "rec-a")]            // dock sub-interval of a /segN interval
        [InlineData("rec-a/seg10@dock3", "rec-a")]           // multi-digit ordinals
        [InlineData("", "")]                                 // empty passthrough
        public void StripSegMarker_StripsSegAndDockMarkers(string key, string expectedBase)
        {
            Assert.Equal(expectedBase, RouteBackingMission.StripSegMarker(key));
        }

        // -----------------------------------------------------------------
        // M-MIS-5 P2b: start-side trim (ComputeStartExcludedIntervalKeys),
        // selection-span verifier, and the M-MIS-9 start-side freeze prong
        // -----------------------------------------------------------------

        // Shuttle tree: the transport ROOTS MID-FLIGHT (no launch), docks at
        // depot A (loads), undocks - the ORIGIN UNDOCK, the lifted span start -
        // transits, docks at depot B (delivers), undocks and continues.
        //   root     C0/0 [0..1000]     (mid-orbit start; ROOT)
        //   dockedA  C0/1 [1000..1500]  (Dock BP@1000: depot-A merge)
        //   transit  C0/2 [1500..2500]  (Undock BP@1500: transit is the survivor)
        //   depotA   C1/0 [1500..2600]  (depot-A offshoot; OUTLIVES the origin undock)
        //   dockedB  C0/3 [2500..3000]  (Dock BP@2500: depot-B merge)
        //   survivor C0/4 [3000..3600]  (Undock BP@3000)
        //   payload  C2/0 [3000..3300]
        // Composition keys on the transport's through-line (owned by "root"):
        //   root [0..1000), root@dock1 [1000..1500), root/seg1 [1500..2500),
        //   root/seg1@dock1 [2500..3000), root/seg2 [3000..3600); the offshoots
        //   key as their own line heads ("depotA", "payload").
        private const double ShuttleOriginUndockUT = 1500.0;
        private const double ShuttleDockBUT = 2500.0;

        private static RecordingTree BuildShuttleOriginTree()
        {
            return Tree("tree-shuttle", new[]
                {
                    Leg("root", "C0", 0, 0, 1000, vessel: "Transport"),
                    Leg("dockedA", "C0", 1, 1000, 1500, vessel: "Transport"),
                    Leg("transit", "C0", 2, 1500, 2500, vessel: "Transport"),
                    Leg("depotA", "C1", 0, 1500, 2600, vessel: "DepotA"),
                    Leg("dockedB", "C0", 3, 2500, 3000, vessel: "Transport"),
                    Leg("survivor", "C0", 4, 3000, 3600, vessel: "Transport"),
                    Leg("payload", "C2", 0, 3000, 3300, vessel: "Payload")
                },
                new[]
                {
                    BP("dockA-bp", BranchPointType.Dock,
                        new[] { "root" }, new[] { "dockedA" }, ut: 1000),
                    // transit first => the transport is the continuation; the
                    // depot-A side peels at the ORIGIN undock.
                    BP("origin-undock-bp", BranchPointType.Undock,
                        new[] { "dockedA" }, new[] { "transit", "depotA" }, ut: 1500),
                    BP("dockB-bp", BranchPointType.Dock,
                        new[] { "transit" }, new[] { "dockedB" }, ut: 2500),
                    BP("undockB-bp", BranchPointType.Undock,
                        new[] { "dockedB" }, new[] { "survivor", "payload" }, ut: 3000)
                });
        }

        // The creation-time exclusion union a mid-tree-origin route carries:
        // end-trim at depot B's dock + start-trim at the origin undock.
        private static HashSet<string> ShuttleCreationExclusions(RecordingTree tree)
        {
            HashSet<string> excluded = RouteBackingMission.ComputeExcludedIntervalKeys(
                tree, ShuttleDockBUT, 0.0);
            excluded.UnionWith(RouteBackingMission.ComputeStartExcludedIntervalKeys(
                tree, ShuttleOriginUndockUT));
            return excluded;
        }

        // catches (plan-named headline): the start trim not excluding the
        // pre-origin lead + docked-origin stretch, or excluding the transit leg
        // that STARTS at the origin undock. The rendered window must start at
        // the origin undock and end at depot B's dock.
        [Fact]
        public void ComputeExcluded_StartTrim_ExcludesPreOriginDock()
        {
            RecordingTree tree = BuildShuttleOriginTree();

            HashSet<string> startExcluded =
                RouteBackingMission.ComputeStartExcludedIntervalKeys(tree, ShuttleOriginUndockUT);

            Assert.Contains("root", startExcluded);         // pre-dock lead
            Assert.Contains("root@dock1", startExcluded);   // docked-at-A stretch
            Assert.Contains("depotA", startExcluded);       // origin-undock offshoot
            Assert.DoesNotContain("root/seg1", startExcluded); // the transit leg stays
            Assert.Equal(3, startExcluded.Count);

            // Union with the end trim and assert the rendered window.
            HashSet<string> excluded = ShuttleCreationExclusions(tree);
            var structure = MissionStructureBuilder.Build(tree);
            var roots = MissionCompositionBuilder.Build(structure);
            var windows = MissionIntervalSelection.ComputeRenderWindows(roots, excluded);

            Assert.True(windows.ContainsKey("root"),
                "transport (root-owned) vessel must still render");
            Assert.Equal(ShuttleOriginUndockUT, windows["root"].StartUT);
            Assert.Equal(ShuttleDockBUT, windows["root"].EndUT);
            Assert.False(windows.ContainsKey("depotA"), "depot-A offshoot must not render");
            Assert.False(windows.ContainsKey("payload"), "post-undock payload must not render");

            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("ComputeStartExcludedIntervalKeys") &&
                l.Contains("tree=tree-shuttle"));
        }

        // catches: the symmetric epsilon tie inverted - an interval ENDING
        // exactly at the origin undock (the docked-origin stretch) must be
        // excluded; the interval STARTING there (the transit leg) must be kept.
        [Fact]
        public void ComputeStartExcluded_ExactBoundary_EndAtOriginExcluded_StartAtOriginKept()
        {
            RecordingTree tree = BuildShuttleOriginTree();

            HashSet<string> startExcluded =
                RouteBackingMission.ComputeStartExcludedIntervalKeys(tree, ShuttleOriginUndockUT);

            Assert.Contains("root@dock1", startExcluded);   // ends exactly at 1500
            Assert.DoesNotContain("root/seg1", startExcluded); // starts exactly at 1500
        }

        // catches: the origin-undock-child scoping missing - the depot-A
        // offshoot starts AT the origin undock and outlives it (EndUT 2600 past
        // the boundary), so the EndUT rule alone would keep it rendered as a
        // ghost depot on top of the live one.
        [Fact]
        public void ComputeStartExcluded_OriginUndockChildBranch_Excluded()
        {
            RecordingTree tree = BuildShuttleOriginTree();

            HashSet<string> startExcluded =
                RouteBackingMission.ComputeStartExcludedIntervalKeys(tree, ShuttleOriginUndockUT);

            Assert.Contains("depotA", startExcluded);
            Assert.Contains(logLines, l =>
                l.Contains("ComputeStartExcludedIntervalKeys") &&
                l.Contains("originUndockChildren=2"));
        }

        // catches: bad inputs throwing or producing a non-empty set instead of
        // the honest no-start-trim fallback.
        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(0.0)]
        [InlineData(-5.0)]
        public void ComputeStartExcluded_BadInputs_EmptySetAndLogs(double segmentStartUT)
        {
            RecordingTree tree = BuildShuttleOriginTree();

            HashSet<string> startExcluded =
                RouteBackingMission.ComputeStartExcludedIntervalKeys(tree, segmentStartUT);

            Assert.Empty(startExcluded);
            Assert.Contains(logLines, l =>
                l.Contains("ComputeStartExcludedIntervalKeys") &&
                l.Contains("no start trim"));
        }

        [Fact]
        public void ComputeStartExcluded_NullTree_EmptySetAndLogs()
        {
            Assert.Empty(RouteBackingMission.ComputeStartExcludedIntervalKeys(null, 1500.0));
            Assert.Contains(logLines, l =>
                l.Contains("ComputeStartExcludedIntervalKeys") &&
                l.Contains("tree=<null>"));
        }

        // catches: the selection-span verifier disagreeing with the render
        // windows the loop builder derives - the RouteBuilder fail-closed span
        // check keys on these values.
        [Fact]
        public void TryComputeSelectionSpan_ShuttleExclusions_SpanIsOriginUndockToDock()
        {
            RecordingTree tree = BuildShuttleOriginTree();
            HashSet<string> excluded = ShuttleCreationExclusions(tree);

            bool resolved = RouteBackingMission.TryComputeSelectionSpan(
                tree, excluded, out double spanStartUT, out double spanEndUT);

            Assert.True(resolved);
            Assert.Equal(ShuttleOriginUndockUT, spanStartUT);
            Assert.Equal(ShuttleDockBUT, spanEndUT);
        }

        // catches: a pre-origin offshoot that outlives the origin undock (NOT an
        // origin-undock child - it peeled earlier) silently dragging the span
        // start early instead of surfacing through the verifier so the builder
        // can reject origin-span-mismatch.
        [Fact]
        public void TryComputeSelectionSpan_PreOriginOffshootOutlivesUndock_SpanStartDisagrees()
        {
            RecordingTree tree = BuildShuttleOriginTree();
            // A relay peels off the root line at 700 and keeps flying past the
            // origin undock: its interval [700..2200] survives BOTH trims.
            tree.Recordings["relay"] = Leg("relay", "C3", 0, 700, 2200, vessel: "Relay");
            tree.BranchPoints.Add(BP("relay-bp", BranchPointType.Undock,
                new[] { "root" }, new[] { "relay" }, ut: 700));

            HashSet<string> excluded = ShuttleCreationExclusions(tree);
            bool resolved = RouteBackingMission.TryComputeSelectionSpan(
                tree, excluded, out double spanStartUT, out _);

            Assert.True(resolved);
            Assert.NotEqual(ShuttleOriginUndockUT, spanStartUT);
            Assert.Equal(700.0, spanStartUT);
        }

        [Fact]
        public void TryComputeSelectionSpan_NullTreeOrAllExcluded_False()
        {
            Assert.False(RouteBackingMission.TryComputeSelectionSpan(
                null, new HashSet<string>(), out _, out _));

            RecordingTree tree = BuildShuttleOriginTree();
            var everything = new HashSet<string>
            {
                "root", "root@dock1", "root/seg1", "root/seg1@dock1", "root/seg2",
                "depotA", "payload"
            };
            Assert.False(RouteBackingMission.TryComputeSelectionSpan(
                tree, everything, out _, out _));
        }

        // Frozen mid-tree-origin route over the shuttle tree: creation-time
        // exclusion union + the persisted origin-undock UT the start prong
        // re-derives from.
        private static Route FrozenShuttleRoute(RecordingTree creationTree, string id,
            bool bindOriginUndock = true)
        {
            Route route = new RouteFixtureBuilder()
                .WithId(id)
                .WithName("Frozen Shuttle")
                .WithBackingMissionTreeId(creationTree.Id)
                .WithSourceRef(new RouteSourceRef { RecordingId = "root", TreeId = creationTree.Id })
                .WithSourceRef(new RouteSourceRef { RecordingId = "dockedA", TreeId = creationTree.Id })
                .WithSourceRef(new RouteSourceRef { RecordingId = "dockedB", TreeId = creationTree.Id })
                .WithDockBinding(ShuttleDockBUT, "dockedB")
                .Build();
            if (bindOriginUndock)
                route.RecordedOriginUndockUT = ShuttleOriginUndockUT;
            foreach (string key in ShuttleCreationExclusions(creationTree))
                route.ExcludedIntervalKeys.Add(key);
            return route;
        }

        // catches (M-MIS-9 start prong): a post-creation peel on the known
        // member through-line renumbering the PRE-ORIGIN intervals into
        // base-known keys the base-id rule + the END trim both miss - only the
        // start-side UT prong re-derived from the persisted RecordedOriginUndockUT
        // catches them.
        [Fact]
        public void ComputeAutoExcluded_StartProng_NewPreOriginKey_AutoExcluded()
        {
            RecordingTree creationTree = BuildShuttleOriginTree();
            Route route = FrozenShuttleRoute(creationTree, "route-startprong01");

            // Growth: a probe peels off the root line at 700 (pre-origin).
            // Intervals renumber: root [0..700), root/seg1 [700..1000),
            // root/seg1@dock1 [1000..1500), root/seg2 [1500..2500),
            // root/seg2@dock1 [2500..3000), root/seg3 [3000..3600).
            RecordingTree grown = BuildShuttleOriginTree();
            grown.Recordings["newprobe"] = Leg("newprobe", "C4", 0, 700, 900, vessel: "Probe");
            grown.BranchPoints.Add(BP("probe-bp", BranchPointType.Undock,
                new[] { "root" }, new[] { "newprobe" }, ut: 700));

            HashSet<string> auto =
                RouteBackingMission.ComputeAutoExcludedNewIntervalKeys(grown, route);

            // Prong 1: the new recording's key.
            Assert.Contains("newprobe", auto);
            // Prong 2 (end trim at 2500): the renumbered post-dock tail.
            Assert.Contains("root/seg2@dock1", auto);
            Assert.Contains("root/seg3", auto);
            // Prong 2b (START trim at 1500): the renumbered PRE-ORIGIN lead.
            Assert.Contains("root/seg1", auto);
            // The renumbered docked-origin stretch [1000..1500) now carries the
            // key "root/seg1@dock1" - the SAME STRING the creation-time END trim
            // persisted for the depot-B docked stretch [2500..3000). The plan's
            // (d) rule ("creation-time keys filtered out": the derived set is
            // genuinely NEW exclusions only) makes the prong skip it, and
            // BuildMission's persisted-UNION-auto exclusion still covers the
            // interval via the persisted string - so it must NOT re-surface in
            // the auto set.
            Assert.DoesNotContain("root/seg1@dock1", auto);
            Assert.Contains("root/seg1@dock1", route.ExcludedIntervalKeys);
            Assert.Equal(4, auto.Count);
            // The in-span transit interval ("root/seg2" post-renumbering) stays.
            Assert.DoesNotContain("root/seg2", auto);

            Assert.Contains(logLines, l =>
                l.Contains("ComputeAutoExcludedNewIntervalKeys") &&
                l.Contains("startTrimAdded=1") &&
                l.Contains("total=4"));
        }

        // catches (byte-identity pin): a route WITHOUT a persisted origin-undock
        // UT (every launch-rooted route) gaining start-prong exclusions - the
        // freeze must stay end-prong-only for them.
        [Fact]
        public void ComputeAutoExcluded_NoOriginUndockUT_EndProngOnly()
        {
            RecordingTree creationTree = BuildShuttleOriginTree();
            Route route = FrozenShuttleRoute(creationTree, "route-startprong02",
                bindOriginUndock: false);

            RecordingTree grown = BuildShuttleOriginTree();
            grown.Recordings["newprobe"] = Leg("newprobe", "C4", 0, 700, 900, vessel: "Probe");
            grown.BranchPoints.Add(BP("probe-bp", BranchPointType.Undock,
                new[] { "root" }, new[] { "newprobe" }, ut: 700));

            HashSet<string> auto =
                RouteBackingMission.ComputeAutoExcludedNewIntervalKeys(grown, route);

            Assert.Contains("newprobe", auto);
            Assert.Contains("root/seg2@dock1", auto);
            Assert.Contains("root/seg3", auto);
            Assert.Equal(3, auto.Count);
            Assert.DoesNotContain("root/seg1", auto);
            Assert.DoesNotContain("root/seg1@dock1", auto);
            Assert.Contains(logLines, l =>
                l.Contains("ComputeAutoExcludedNewIntervalKeys") &&
                l.Contains("startTrimAdded=0"));
        }
    }
}
