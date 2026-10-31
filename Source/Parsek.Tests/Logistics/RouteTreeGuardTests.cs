using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Phase 2 (mutual-exclusion guard): <see cref="RouteTreeGuard"/> decides
    /// whether a tree is bound to a committed supply route (and therefore must
    /// have its manual loop surfaces greyed/blocked), and clears both surfaces
    /// when a route is activated or on load.
    /// </summary>
    /// <remarks>
    /// Touches shared static state (<see cref="RouteStore"/>,
    /// <see cref="MissionStore"/>, <see cref="RecordingStore"/> committed trees,
    /// <see cref="ParsekLog"/>), so the class is <c>[Collection("Sequential")]</c>
    /// and resets all of it in the ctor + Dispose.
    /// </remarks>
    [Collection("Sequential")]
    public class RouteTreeGuardTests : System.IDisposable
    {
        private const string GuardTag = "[RouteGuard]";
        private readonly List<string> logLines = new List<string>();

        public RouteTreeGuardTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            MissionStore.ResetForTesting();
            RecordingStore.ClearCommittedTreesInternal();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            MissionStore.ResetForTesting();
            RecordingStore.ClearCommittedTreesInternal();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // Fixtures
        // -----------------------------------------------------------------

        private static RouteSourceRef SourceRef(string recId, string treeId)
        {
            return new RouteSourceRef { RecordingId = recId, TreeId = treeId };
        }

        // A route bound to one tree via a single source ref.
        private static Route RouteOnTree(string id, string treeId,
            RouteStatus status = RouteStatus.Active)
        {
            return new RouteFixtureBuilder()
                .WithId(id)
                .WithName(id)
                .WithStatus(status)
                .WithBackingMissionTreeId(treeId)
                .WithRecordingId("rec-" + treeId)
                .WithSourceRef(SourceRef("rec-" + treeId, treeId))
                .Build();
        }

        // A committed tree carrying recordings the per-recording loop clear can flip.
        private static RecordingTree TreeWithRecordings(string treeId, params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null
            };
            foreach (var r in recs)
                tree.Recordings[r.RecordingId] = r;
            return tree;
        }

        private static Recording Rec(string id, string treeId, bool loop)
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = "V-" + id,
                LoopPlayback = loop
            };
        }

        // -----------------------------------------------------------------
        // IsTreeBoundToActiveRoute / RouteBindingFor
        // -----------------------------------------------------------------

        [Fact]
        public void IsBound_TrueWhenRouteBindsTreeViaSourceRef_AndVerboseLogged()
        {
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X"));

            Assert.True(RouteTreeGuard.IsTreeBoundToActiveRoute("tree-X"));
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains(GuardTag)
                && l.Contains("tree-X")
                && l.Contains("BOUND by route")
                && l.Contains("route-A"));
        }

        // catches: a regression where the BOUND line logs UNCONDITIONALLY. RouteBindingFor
        // is called many times per frame (UI table + per-tree visibility checks), so a
        // successful bind floods the log (~2k lines in a few-second window in one playtest).
        // The BOUND line must be rate-limited per tree: one line, then quiet until the
        // window passes.
        [Fact]
        public void IsBound_BoundLog_IsRateLimitedPerTree()
        {
            double clock = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => clock;
            ParsekLog.ResetRateLimitsForTesting();
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X"));

            // Many same-instant bind checks: exactly one BOUND line.
            for (int i = 0; i < 20; i++)
                Assert.True(RouteTreeGuard.IsTreeBoundToActiveRoute("tree-X"));
            Assert.Single(logLines, l =>
                l.Contains("BOUND by route") && l.Contains("tree-X"));

            // After the 5s window the next bind check logs again.
            clock += 6.0;
            Assert.True(RouteTreeGuard.IsTreeBoundToActiveRoute("tree-X"));
            Assert.Equal(2, logLines.FindAll(l =>
                l.Contains("BOUND by route") && l.Contains("tree-X")).Count);
        }

        [Fact]
        public void IsBound_FalseForUnboundTree()
        {
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X"));

            Assert.False(RouteTreeGuard.IsTreeBoundToActiveRoute("tree-OTHER"));
        }

        [Fact]
        public void IsBound_FalseWhenNoRoutes()
        {
            Assert.False(RouteTreeGuard.IsTreeBoundToActiveRoute("tree-X"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void IsBound_NullOrEmptyTreeId_FalseNoThrow(string treeId)
        {
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X"));

            Assert.False(RouteTreeGuard.IsTreeBoundToActiveRoute(treeId));
        }

        // Every RouteStatus BINDS its tree (confirmed decision: a broken route
        // keeps owning its tree until explicitly removed). The guard must agree
        // with RouteStatusPolicy.BindsTree for all 9 values. Ordinals are passed
        // as int (RouteStatus is internal; an xUnit test method must be public,
        // which forbids an internal parameter type, CS0051).
        [Theory]
        [InlineData((int)RouteStatus.Active)]
        [InlineData((int)RouteStatus.InTransit)]
        [InlineData((int)RouteStatus.WaitingForResources)]
        [InlineData((int)RouteStatus.DestinationFull)]
        [InlineData((int)RouteStatus.EndpointLost)]
        [InlineData((int)RouteStatus.MissingSourceRecording)]
        [InlineData((int)RouteStatus.SourceChanged)]
        [InlineData((int)RouteStatus.Paused)]
        public void IsBound_TrueForEveryStatus(int statusOrdinal)
        {
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X", (RouteStatus)statusOrdinal));

            Assert.True(RouteTreeGuard.IsTreeBoundToActiveRoute("tree-X"));
        }

        [Fact]
        public void RouteBindingFor_ReturnsBindingRoute()
        {
            Route route = RouteOnTree("route-A", "tree-X");
            route.Name = "Fuel Run";
            RouteStore.AddRoute(route);

            bool found = RouteTreeGuard.RouteBindingFor("tree-X", out Route bound);

            Assert.True(found);
            Assert.Same(route, bound);
        }

        [Fact]
        public void RouteBindingFor_MissReturnsFalseNull()
        {
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X"));

            bool found = RouteTreeGuard.RouteBindingFor("tree-OTHER", out Route bound);

            Assert.False(found);
            Assert.Null(bound);
        }

        // -----------------------------------------------------------------
        // BoundTreeIds
        // -----------------------------------------------------------------

        [Fact]
        public void BoundTreeIds_CollectsDistinctTrees_AcrossRoutesAndMultiSourceRefRoute()
        {
            // route-A binds tree-X; route-B binds two distinct trees (tree-Y, tree-Z)
            // via two source refs. tree-X appears once; duplicates collapse.
            RouteStore.AddRoute(RouteOnTree("route-A", "tree-X"));

            Route multi = new RouteFixtureBuilder()
                .WithId("route-B")
                .WithName("route-B")
                .WithBackingMissionTreeId("tree-Y")
                .WithRecordingId("rec-Y")
                .WithRecordingId("rec-Z")
                .WithSourceRef(SourceRef("rec-Y", "tree-Y"))
                .WithSourceRef(SourceRef("rec-Z", "tree-Z"))
                // a second ref on tree-X proves de-dup
                .WithSourceRef(SourceRef("rec-X2", "tree-X"))
                .Build();
            RouteStore.AddRoute(multi);

            var bound = RouteTreeGuard.BoundTreeIds();

            Assert.Equal(3, bound.Count);
            Assert.Contains("tree-X", bound);
            Assert.Contains("tree-Y", bound);
            Assert.Contains("tree-Z", bound);
        }

        [Fact]
        public void BoundTreeIds_EmptyWhenNoRoutes()
        {
            Assert.Empty(RouteTreeGuard.BoundTreeIds());
        }

        // -----------------------------------------------------------------
        // ForceClearManualLoopForRouteTree
        // -----------------------------------------------------------------

        [Fact]
        public void ForceClear_ClearsMissionLoopAndPerRecordingLoop_OnBoundTree()
        {
            // Seed a committed tree with a looping recording, and a looping mission.
            Recording looping = Rec("rec-1", "tree-X", loop: true);
            RecordingStore.AddCommittedTreeForTesting(
                TreeWithRecordings("tree-X", looping));

            MissionStore.EnsureDefaultsForTrees(
                new List<RecordingTree> { TreeWithRecordings("tree-X", looping) });
            Mission mission = MissionStore.FindOriginalMission("tree-X");
            Assert.NotNull(mission);
            MissionStore.SetLoopEnabled(mission, true, currentUT: 100.0);
            Assert.True(mission.LoopPlayback);
            Assert.True(looping.LoopPlayback);

            RouteTreeGuard.ForceClearManualLoopForRouteTree("tree-X", currentUT: 200.0);

            Assert.False(mission.LoopPlayback);
            Assert.False(looping.LoopPlayback);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains(GuardTag)
                && l.Contains("tree=tree-X")
                && l.Contains("cleared 1 mission loop(s)")
                && l.Contains("1 per-recording loop(s)"));
        }

        [Fact]
        public void ForceClear_LeavesOtherTreeLoopsUntouched()
        {
            Recording loopX = Rec("rec-x", "tree-X", loop: true);
            Recording loopY = Rec("rec-y", "tree-Y", loop: true);
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-X", loopX));
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-Y", loopY));

            MissionStore.EnsureDefaultsForTrees(new List<RecordingTree>
            {
                TreeWithRecordings("tree-X", loopX),
                TreeWithRecordings("tree-Y", loopY)
            });
            Mission mX = MissionStore.FindOriginalMission("tree-X");
            Mission mY = MissionStore.FindOriginalMission("tree-Y");
            MissionStore.SetLoopEnabled(mX, true, 100.0);
            MissionStore.SetLoopEnabled(mY, true, 100.0);

            RouteTreeGuard.ForceClearManualLoopForRouteTree("tree-X", 200.0);

            // tree-X cleared, tree-Y survives.
            Assert.False(mX.LoopPlayback);
            Assert.False(loopX.LoopPlayback);
            Assert.True(mY.LoopPlayback);
            Assert.True(loopY.LoopPlayback);
        }

        [Fact]
        public void ForceClear_Idempotent_SecondCallClearsNothing()
        {
            Recording looping = Rec("rec-1", "tree-X", loop: true);
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-X", looping));

            RouteTreeGuard.ForceClearManualLoopForRouteTree("tree-X", 100.0);
            logLines.Clear();
            RouteTreeGuard.ForceClearManualLoopForRouteTree("tree-X", 200.0);

            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains(GuardTag)
                && l.Contains("cleared 0 mission loop(s)")
                && l.Contains("0 per-recording loop(s)"));
        }

        [Fact]
        public void ForceClear_NullTreeId_SkipsWithLog()
        {
            RouteTreeGuard.ForceClearManualLoopForRouteTree(null, 100.0);

            Assert.Contains(logLines, l =>
                l.Contains(GuardTag) && l.Contains("null/empty treeId"));
        }

        // -----------------------------------------------------------------
        // M5: ForceClear* return the cleared count so the create path can fire the
        // toast ONLY on a real clear.
        // -----------------------------------------------------------------

        [Fact]
        public void ForceClear_ReturnsClearedCount_MissionPlusRecording()
        {
            Recording looping = Rec("rec-1", "tree-X", loop: true);
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-X", looping));

            MissionStore.EnsureDefaultsForTrees(
                new List<RecordingTree> { TreeWithRecordings("tree-X", looping) });
            Mission mission = MissionStore.FindOriginalMission("tree-X");
            MissionStore.SetLoopEnabled(mission, true, 100.0);

            int cleared = RouteTreeGuard.ForceClearManualLoopForRouteTree("tree-X", 200.0);

            // 1 mission loop + 1 per-recording loop.
            Assert.Equal(2, cleared);
        }

        [Fact]
        public void ForceClear_ReturnsZero_WhenNoManualLoopOnTree()
        {
            // A sealed tree with no looping recording and no looping mission.
            Recording notLooping = Rec("rec-1", "tree-X", loop: false);
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-X", notLooping));

            int cleared = RouteTreeGuard.ForceClearManualLoopForRouteTree("tree-X", 200.0);

            Assert.Equal(0, cleared);
        }

        [Fact]
        public void ForceClear_SecondCallReturnsZero_Idempotent()
        {
            Recording looping = Rec("rec-1", "tree-X", loop: true);
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-X", looping));

            int first = RouteTreeGuard.ForceClearManualLoopForRouteTree("tree-X", 100.0);
            int second = RouteTreeGuard.ForceClearManualLoopForRouteTree("tree-X", 200.0);

            Assert.Equal(1, first);
            Assert.Equal(0, second);
        }

        [Fact]
        public void ForceClear_NullTreeId_ReturnsZero()
        {
            Assert.Equal(0, RouteTreeGuard.ForceClearManualLoopForRouteTree(null, 100.0));
        }

        // -----------------------------------------------------------------
        // ForceClearManualLoopForRoute (AddRoute-site convenience)
        // -----------------------------------------------------------------

        [Fact]
        public void ForceClearForRoute_ClearsEverySourceTree()
        {
            Recording loopY = Rec("rec-y", "tree-Y", loop: true);
            Recording loopZ = Rec("rec-z", "tree-Z", loop: true);
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-Y", loopY));
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-Z", loopZ));

            Route multi = new RouteFixtureBuilder()
                .WithId("route-B")
                .WithName("route-B")
                .WithBackingMissionTreeId("tree-Y")
                .WithSourceRef(SourceRef("rec-y", "tree-Y"))
                .WithSourceRef(SourceRef("rec-z", "tree-Z"))
                .Build();

            RouteTreeGuard.ForceClearManualLoopForRoute(multi, 100.0);

            Assert.False(loopY.LoopPlayback);
            Assert.False(loopZ.LoopPlayback);
        }

        [Fact]
        public void ForceClearForRoute_NullRoute_SkipsWithLog()
        {
            RouteTreeGuard.ForceClearManualLoopForRoute(null, 100.0);

            Assert.Contains(logLines, l =>
                l.Contains(GuardTag) && l.Contains("null route"));
        }

        [Fact]
        public void ForceClearForRoute_ReturnsSummedCountAcrossTrees()
        {
            Recording loopY = Rec("rec-y", "tree-Y", loop: true);
            Recording loopZ = Rec("rec-z", "tree-Z", loop: true);
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-Y", loopY));
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-Z", loopZ));

            Route multi = new RouteFixtureBuilder()
                .WithId("route-B")
                .WithName("route-B")
                .WithBackingMissionTreeId("tree-Y")
                .WithSourceRef(SourceRef("rec-y", "tree-Y"))
                .WithSourceRef(SourceRef("rec-z", "tree-Z"))
                .Build();

            int cleared = RouteTreeGuard.ForceClearManualLoopForRoute(multi, 100.0);

            // One per-recording loop on each of the two distinct trees.
            Assert.Equal(2, cleared);
        }

        [Fact]
        public void ForceClearForRoute_ReturnsZero_WhenNoManualLoops()
        {
            Recording notLooping = Rec("rec-y", "tree-Y", loop: false);
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-Y", notLooping));

            Route route = RouteOnTree("route-A", "tree-Y");

            Assert.Equal(0, RouteTreeGuard.ForceClearManualLoopForRoute(route, 100.0));
        }

        [Fact]
        public void ForceClearForRoute_NullRoute_ReturnsZero()
        {
            Assert.Equal(0, RouteTreeGuard.ForceClearManualLoopForRoute(null, 100.0));
        }

        // -----------------------------------------------------------------
        // M5: the toast fires (via ParsekLog.ScreenMessage) ONLY when a manual
        // loop was actually cleared. Pins the count-plumbing -> ShouldToast ->
        // FormatMissionNowRepeatsOnRouteToast -> ScreenMessage seam (the IMGUI create
        // wiring in CreateRouteFromCandidate is not unit-testable, so we drive the
        // same pure surface it calls and assert the screen-message sink).
        // -----------------------------------------------------------------

        [Fact]
        public void Toast_PostedWhenLoopActuallyCleared()
        {
            var toasts = new List<string>();
            ParsekLog.ScreenMessageSinkForTesting = (msg, dur) => toasts.Add(msg);

            Recording looping = Rec("rec-1", "tree-X", loop: true);
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-X", looping));
            Route route = RouteOnTree("route-A", "tree-X");

            int cleared = RouteTreeGuard.ForceClearManualLoopForRoute(route, 100.0);
            // Drive the same pure decision + format the create path runs.
            if (LogisticsCreatePresentation.ShouldToastManualLoopCleared(cleared))
                ParsekLog.ScreenMessage(
                    LogisticsCreatePresentation.FormatMissionNowRepeatsOnRouteToast("tree-X"), 5f);

            Assert.True(cleared > 0);
            Assert.Contains(toasts, t => t.Contains("now repeats only on this route's schedule"));
        }

        [Fact]
        public void Toast_NotPostedWhenNothingCleared()
        {
            var toasts = new List<string>();
            ParsekLog.ScreenMessageSinkForTesting = (msg, dur) => toasts.Add(msg);

            // A sealed tree with no looping recording / mission: nothing to clear.
            Recording notLooping = Rec("rec-1", "tree-X", loop: false);
            RecordingStore.AddCommittedTreeForTesting(TreeWithRecordings("tree-X", notLooping));
            Route route = RouteOnTree("route-A", "tree-X");

            int cleared = RouteTreeGuard.ForceClearManualLoopForRoute(route, 100.0);
            if (LogisticsCreatePresentation.ShouldToastManualLoopCleared(cleared))
                ParsekLog.ScreenMessage(
                    LogisticsCreatePresentation.FormatMissionNowRepeatsOnRouteToast("tree-X"), 5f);

            Assert.Equal(0, cleared);
            Assert.Empty(toasts);
        }
            // -----------------------------------------------------------------
        // A looping mission whose partner journey reaches into a route's tree
        // -----------------------------------------------------------------

        // Mission "mb" on partner tree "tb" includes the dock link into controller tree "ta".
        private static Mission LoadPartnerMission(bool includeLink, bool loop)
        {
            var node = new ConfigNode("PARSEK");
            var mB = new Mission("mb", "tb", "B mission");
            if (includeLink)
                mB.IncludedForeignDockLinkIds.Add(CrossTreeDockFixture.DockBpId);
            mB.Save(node.AddNode("MISSION"));
            MissionStore.Load(node);
            Mission b = System.Linq.Enumerable.FirstOrDefault(MissionStore.Missions, x => x.Id == "mb");
            Assert.NotNull(b);
            if (loop)
                MissionStore.SetLoopEnabled(b, true, 100.0);
            return b;
        }

        private static List<RecordingTree> SeedPartnerTrees()
        {
            var tb = CrossTreeDockFixture.PartnerTree();
            var ta = CrossTreeDockFixture.ControllerTree();
            RecordingStore.AddCommittedTreeForTesting(tb);
            RecordingStore.AddCommittedTreeForTesting(ta);
            return new List<RecordingTree> { tb, ta };
        }

        [Fact]
        public void MissionSpansTree_OwnTree_True_ForeignTreeOnlyThroughAnIncludedLink()
        {
            var trees = SeedPartnerTrees();
            Mission linked = LoadPartnerMission(includeLink: true, loop: false);

            Assert.True(RouteTreeGuard.MissionSpansTree(linked, "tb", trees));
            Assert.True(RouteTreeGuard.MissionSpansTree(linked, "ta", trees));
            Assert.False(RouteTreeGuard.MissionSpansTree(linked, "tree-other", trees));
            Assert.False(RouteTreeGuard.MissionSpansTree(null, "ta", trees));
            Assert.False(RouteTreeGuard.MissionSpansTree(linked, null, trees));

            linked.IncludedForeignDockLinkIds.Clear();
            Assert.True(RouteTreeGuard.MissionSpansTree(linked, "tb", trees));
            Assert.False(RouteTreeGuard.MissionSpansTree(linked, "ta", trees));
        }

        [Fact]
        public void ForceClear_ClearsALoopingMission_WhosePartnerJourneyReachesTheRouteTree()
        {
            SeedPartnerTrees();
            Mission b = LoadPartnerMission(includeLink: true, loop: true);
            Assert.True(b.LoopPlayback);

            int cleared = RouteTreeGuard.ForceClearManualLoopForRouteTree("ta", currentUT: 200.0);

            Assert.False(b.LoopPlayback);
            Assert.True(cleared >= 1);
            Assert.Contains(logLines, l =>
                l.Contains(GuardTag) && l.Contains("tree=ta")
                && l.Contains("cleared 1 mission loop(s)"));
        }

        [Fact]
        public void ForceClear_KeepsALoopingMission_WithoutThePartnerJourney()
        {
            SeedPartnerTrees();
            Mission b = LoadPartnerMission(includeLink: false, loop: true);

            RouteTreeGuard.ForceClearManualLoopForRouteTree("ta", currentUT: 200.0);

            Assert.True(b.LoopPlayback);
        }

        [Fact]
        public void RouteBindingForMission_FindsTheRouteOnThePartnerJourneysTree()
        {
            var trees = SeedPartnerTrees();
            RouteStore.AddRoute(RouteOnTree("route-T", "ta"));
            Mission b = LoadPartnerMission(includeLink: true, loop: false);

            Assert.True(RouteTreeGuard.RouteBindingForMission(b, trees, out Route route));
            Assert.Equal("route-T", route.Id);

            b.IncludedForeignDockLinkIds.Clear();
            Assert.False(RouteTreeGuard.RouteBindingForMission(b, trees, out Route none));
            Assert.Null(none);
            Assert.False(RouteTreeGuard.RouteBindingForMission(null, trees, out _));
        }

        [Fact]
        public void RouteBindingForMission_OwnTreeRouteWins()
        {
            var trees = SeedPartnerTrees();
            RouteStore.AddRoute(RouteOnTree("route-own", "tb"));
            RouteStore.AddRoute(RouteOnTree("route-T", "ta"));
            Mission b = LoadPartnerMission(includeLink: true, loop: false);

            Assert.True(RouteTreeGuard.RouteBindingForMission(b, trees, out Route route));
            Assert.Equal("route-own", route.Id);
        }

        [Fact]
        public void MissionsWindow_ReadsTheMissionLevelBinding_AndClearsTheLoopOnAPartnerInclude()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                RepoRoot(), "Source", "Parsek", "UI", "MissionsWindowUI.cs"));
            int count = 0, at = 0;
            while ((at = src.IndexOf("RouteTreeGuard.RouteBindingForMission(", at, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                at++;
            }
            Assert.Equal(2, count);
            Assert.Contains("mission.LoopPlayback && !YieldLoopToPartnerRoute(mission, link)", src);
            Assert.Contains("RouteTreeGuard.IsTreeBoundToActiveRoute(link.ForeignTreeId)", src);
        }

        private static string RepoRoot()
        {
            string dir = System.AppDomain.CurrentDomain.BaseDirectory;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, "..", "..", "..", "..", ".."));
        }
    }
}
