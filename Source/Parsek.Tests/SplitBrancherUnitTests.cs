using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization and unit tests for ParsekFlightSplitBrancher: the headless-reachable
    /// instance paths (the dock merge branch up to its recorder start, the breakup guards, the
    /// dedup guard, the dock / undock state reset, the coalescer and boarding guards) and the
    /// pure statics (asteroid / comet detection, route endpoint parsing, merge cause, the
    /// standalone fallback leg name, the breakup child seed helpers).
    /// </summary>
    [Collection("Sequential")]
    public class SplitBrancherUnitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SplitBrancherUnitTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // --- builders ---

        private static ParsekFlight Flight(RecordingTree tree)
        {
            var flight = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            flight.activeTree = tree;
            // Field initializers do not run on an uninitialized object.
            flight.lastBranchUT = -1;
            flight.lastBranchVesselPids = new HashSet<uint>();
            flight.dockingInProgress = new HashSet<uint>();
            flight.pendingDockRouteEndpointSituation = -1;
            return flight;
        }

        private static RecordingTree Tree(params Recording[] recs)
        {
            var tree = new RecordingTree { Id = "tree-1", TreeName = "Tree One" };
            foreach (var r in recs)
            {
                r.TreeId = tree.Id;
                tree.Recordings[r.RecordingId] = r;
            }
            if (recs.Length > 0)
            {
                tree.RootRecordingId = recs[0].RecordingId;
                tree.ActiveRecordingId = recs[0].RecordingId;
            }
            return tree;
        }

        private static ConfigNode VesselNode(uint pid, string sit, int root, params (uint pid, uint uid)[] parts)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("persistentId", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (sit != null) node.AddValue("sit", sit);
            node.AddValue("lat", "1.5");
            node.AddValue("lon", "2.5");
            node.AddValue("alt", "3.5");
            if (root >= 0) node.AddValue("root", root.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var p in parts)
            {
                var part = node.AddNode("PART");
                part.AddValue("persistentId", p.pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (p.uid != 0) part.AddValue("uid", p.uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return node;
        }

        // ================= characterization: instance paths =================

        [Fact]
        public void BranchDedup_SameUtSamePid_SecondIsSkipped()
        {
            var flight = Flight(null);
            Assert.True(flight.SplitBrancher.CheckBranchDeduplication(100.0, 7));
            Assert.False(flight.SplitBrancher.CheckBranchDeduplication(100.05, 7));
            Assert.Contains(logLines, l => l.Contains("Skipping duplicate branch for pid=7 at same UT"));
            Assert.True(flight.SplitBrancher.CheckBranchDeduplication(100.05, 8));
            Assert.Equal(100.0, flight.lastBranchUT);
        }

        [Fact]
        public void BranchDedup_NewUtWindowClearsTheSet()
        {
            var flight = Flight(null);
            Assert.True(flight.SplitBrancher.CheckBranchDeduplication(100.0, 7));
            Assert.True(flight.SplitBrancher.CheckBranchDeduplication(100.2, 7));
            Assert.Equal(100.2, flight.lastBranchUT);
            Assert.Single(flight.lastBranchVesselPids);
            // The window is 0.1 either way (absolute difference).
            Assert.True(flight.SplitBrancher.CheckBranchDeduplication(100.0, 7));
        }

        [Fact]
        public void ClearDockUndockState_ResetsEveryPendingDockField()
        {
            var flight = Flight(null);
            flight.pendingDockMergedPid = 1;
            flight.dockConfirmFrames = 2;
            flight.pendingDockAbsorbedPid = 3;
            flight.pendingDockRouteTargetPid = 4;
            flight.pendingDockPartnerPid = 5;
            flight.pendingDockPartnerGuid = "g";
            flight.pendingDockTransferKind = RouteConnectionKind.DockingPort;
            flight.pendingDockPartnerSnapshot = new ConfigNode("VESSEL");
            flight.pendingDockPartnerSnapshotPid = 6;
            flight.pendingDockSelfSnapshot = new ConfigNode("VESSEL");
            flight.pendingDockSelfSnapshotPid = 7;
            flight.pendingDockRouteEndpointAtDock = new RouteEndpoint { VesselPersistentId = 8 };
            flight.pendingDockRouteEndpointSituation = 9;

            flight.SplitBrancher.ClearDockUndockState();

            Assert.Equal(0u, flight.pendingDockMergedPid);
            Assert.Equal(0, flight.dockConfirmFrames);
            Assert.Equal(0u, flight.pendingDockAbsorbedPid);
            Assert.Equal(0u, flight.pendingDockRouteTargetPid);
            Assert.Equal(0u, flight.pendingDockPartnerPid);
            Assert.Null(flight.pendingDockPartnerGuid);
            Assert.Equal(RouteConnectionKind.None, flight.pendingDockTransferKind);
            Assert.Null(flight.pendingDockPartnerSnapshot);
            Assert.Equal(0u, flight.pendingDockPartnerSnapshotPid);
            Assert.Null(flight.pendingDockSelfSnapshot);
            Assert.Equal(0u, flight.pendingDockSelfSnapshotPid);
            Assert.False(flight.pendingDockRouteEndpointAtDock.HasValue);
            Assert.Equal(-1, flight.pendingDockRouteEndpointSituation);
        }

        [Fact]
        public void CoalescerTicks_NoCoalescer_DoNothing()
        {
            var flight = Flight(Tree(new Recording { RecordingId = "r1" }));
            flight.SplitBrancher.TickCrashCoalescer();
            flight.SplitBrancher.EmitPendingSplitBeforeFocusLeaves("unit");
            Assert.DoesNotContain(logLines, l => l.Contains("[Coalescer]"));
        }

        [Fact]
        public void UnconfirmedBoardingStop_NoRecorder_LeavesBoardingStateAlone()
        {
            var flight = Flight(null);
            flight.pendingBoardingTargetPid = 5;
            flight.boardingConfirmFrames = 3;
            flight.SplitBrancher.HandleUnconfirmedBoardingStop();
            Assert.Equal(5u, flight.pendingBoardingTargetPid);
            Assert.Equal(3, flight.boardingConfirmFrames);
        }

        [Fact]
        public void JointBreakDeferredCheck_SplitAlreadyPending_DoesNothing()
        {
            var flight = Flight(null);
            flight.pendingSplitInProgress = true;
            flight.SplitBrancher.HandleJointBreakDeferredCheck();
            Assert.True(flight.pendingSplitInProgress);
            Assert.Null(flight.pendingSplitRecorder);
        }

        [Fact]
        public void FallbackDecoupleEvents_NoRecorderOrNoPids_AddNothing()
        {
            var flight = Flight(null);
            Assert.Equal(0, flight.SplitBrancher.EmitFallbackDecoupleEventsForNewVessels(null, new List<uint> { 1 }, 5));
            Assert.Equal(0, flight.SplitBrancher.EmitFallbackDecoupleEventsForNewVessels(new FlightRecorder(), null, 5));
            Assert.Equal(0, flight.SplitBrancher.EmitFallbackDecoupleEventsForNewVessels(new FlightRecorder(), new List<uint>(), 5));
        }

        [Fact]
        public void ProcessBreakup_NoActiveTree_IsNotRecorded()
        {
            var flight = Flight(null);
            flight.SplitBrancher.ProcessBreakupEvent(new BranchPoint { BreakupCause = "CRASH", DebrisCount = 3 });
            Assert.Contains(logLines, l => l.Contains("[Coalescer]")
                && l.Contains("ProcessBreakupEvent: no active tree") && l.Contains("cause=CRASH, debris=3"));
        }

        [Fact]
        public void ProcessBreakup_NoActiveRecordingId_Warns()
        {
            var tree = Tree(new Recording { RecordingId = "r1" });
            tree.ActiveRecordingId = null;
            Flight(tree).SplitBrancher.ProcessBreakupEvent(new BranchPoint());
            Assert.Contains(logLines, l => l.Contains("[WARN][Coalescer]")
                && l.Contains("ProcessBreakupEvent: active tree has no active recording"));
            Assert.Empty(tree.BranchPoints);
        }

        [Fact]
        public void ProcessBreakup_ActiveRecordingMissing_Warns()
        {
            var tree = Tree(new Recording { RecordingId = "r1" });
            tree.ActiveRecordingId = "gone";
            Flight(tree).SplitBrancher.ProcessBreakupEvent(new BranchPoint());
            Assert.Contains(logLines, l => l.Contains("[WARN][Coalescer]")
                && l.Contains("ProcessBreakupEvent: active recording id=gone not found in tree"));
            Assert.Empty(tree.BranchPoints);
        }

        [Fact]
        public void PartnerPartPids_ZeroPid_IsNull()
        {
            Assert.Null(Flight(null).SplitBrancher.TryDeriveEndpointPartPidsFromPartner(0));
        }

        [Fact]
        public void PartnerPartPids_ActiveTreeThenCommitted()
        {
            var partner = new Recording
            {
                RecordingId = "p1", VesselPersistentId = 77,
                VesselSnapshot = VesselNode(77, "LANDED", 0, (901, 1), (902, 2)),
            };
            var flight = Flight(Tree(new Recording { RecordingId = "act", VesselPersistentId = 10 }, partner));
            var pids = flight.SplitBrancher.TryDeriveEndpointPartPidsFromPartner(77);
            Assert.Equal(new List<uint> { 901, 902 }, pids);
            Assert.Contains(logLines, l => l.Contains("TryDeriveEndpointPartPidsFromPartner: active-tree recording partnerPid=77 recId=p1 parts=2"));

            Assert.Null(flight.SplitBrancher.TryDeriveEndpointPartPidsFromPartner(78));
            Assert.Contains(logLines, l => l.Contains("TryDeriveEndpointPartPidsFromPartner: no snapshot available partnerPid=78"));
        }

        // CreateMergeBranch runs headless up to step 10, where FlightRecorder.StartRecording
        // reads Time.timeScale (a Unity ECall that throws outside the game). Everything this
        // characterizes - the background parent end, the route proof endpoint resolution and
        // the branch point / merged child wiring - happens before that step.
        private static void RunMergeBranchUntilRecorderStart(ParsekFlight flight, uint routeTargetPid,
            string backgroundParentId = "bg")
        {
            Assert.ThrowsAny<Exception>(() => flight.SplitBrancher.CreateMergeBranch(
                BranchPointType.Dock, mergedVesselPid: 10, activeParentRecordingId: "act",
                backgroundParentRecordingId: backgroundParentId, mergeUT: 500, stoppedRecorder: null,
                routeTargetVesselPid: routeTargetPid, transferKind: RouteConnectionKind.DockingPort,
                branchPartnerPid: routeTargetPid, branchPartnerGuid: null));
        }

        [Fact]
        public void MergeBranch_EndsTheBackgroundParentAndWiresTheChild()
        {
            var act = new Recording { RecordingId = "act", VesselPersistentId = 10 };
            var bg = new Recording { RecordingId = "bg", VesselPersistentId = 77 };
            var tree = Tree(act, bg);
            tree.BackgroundMap[77] = "bg";
            var flight = Flight(tree);

            RunMergeBranchUntilRecorderStart(flight, routeTargetPid: 0);

            Assert.Equal(TerminalState.Docked, bg.TerminalStateValue);
            Assert.Equal(500, bg.ExplicitEndUT);
            Assert.False(tree.BackgroundMap.ContainsKey(77));
            Assert.Equal(TerminalState.Docked, act.TerminalStateValue);
            var bp = Assert.Single(tree.BranchPoints);
            Assert.Equal(BranchPointType.Dock, bp.Type);
            Assert.Equal(new List<string> { "act", "bg" }, bp.ParentRecordingIds);
            Assert.Equal(bp.Id, act.ChildBranchPointId);
            Assert.Equal(bp.Id, bg.ChildBranchPointId);
            Assert.Equal(3, tree.Recordings.Count);
            Assert.NotEqual("act", tree.ActiveRecordingId);
            Assert.Equal("Merged Vessel", tree.Recordings[tree.ActiveRecordingId].VesselName);
            Assert.DoesNotContain(logLines, l => l.Contains("Route proof"));
        }

        [Fact]
        public void MergeBranch_RouteEndpointRestoredFromTheBackgroundSnapshot()
        {
            var act = new Recording { RecordingId = "act", VesselPersistentId = 10 };
            var bg = new Recording
            {
                RecordingId = "bg", VesselPersistentId = 77,
                VesselSnapshot = VesselNode(77, "LANDED", 0, (901, 555), (902, 0)),
            };
            var tree = Tree(act, bg);
            tree.BackgroundMap[77] = "bg";
            var flight = Flight(tree);

            RunMergeBranchUntilRecorderStart(flight, routeTargetPid: 77);

            Assert.Contains(logLines, l => l.Contains("Route proof endpoint restored from background snapshot targetPid=77 situation="));
            Assert.DoesNotContain(logLines, l => l.Contains("Route proof endpoint unavailable"));
            Assert.DoesNotContain(logLines, l => l.Contains("root backfilled"));
            // No live merged vessel, so no merged snapshot and no window; the skip line names
            // the endpoint part set the background snapshot supplied.
            Assert.Contains(logLines, l => l.Contains("Route proof dock window capture skipped")
                && l.Contains("targetPid=77 snapshot=False transportParts=0 endpointParts=2"));
        }

        [Fact]
        public void MergeBranch_PreCoupleSnapshotBackfillsTheEndpointRoot()
        {
            var act = new Recording { RecordingId = "act", VesselPersistentId = 10 };
            var tree = Tree(act);
            var flight = Flight(tree);
            flight.pendingDockRouteEndpointAtDock = new RouteEndpoint { VesselPersistentId = 77, RootPartUId = 0 };
            flight.pendingDockRouteEndpointSituation = (int)Vessel.Situations.LANDED;
            flight.pendingDockPartnerSnapshot = VesselNode(77, "LANDED", 1, (901, 111), (902, 444), (903, 0));
            flight.pendingDockPartnerSnapshotPid = 77;

            RunMergeBranchUntilRecorderStart(flight, routeTargetPid: 77, backgroundParentId: null);

            Assert.DoesNotContain(logLines, l => l.Contains("restored from background snapshot"));
            Assert.Contains(logLines, l => l.Contains("Route proof endpoint root backfilled from the pre-couple snapshot: targetPid=77 rootPartUId=444"));
            Assert.Contains(logLines, l => l.Contains("Route proof dock window capture skipped")
                && l.Contains("endpointParts=3"));
        }

        [Fact]
        public void MergeBranch_BackgroundSnapshotBackfillsTheRootWhenNoPreCoupleSnapshot()
        {
            var act = new Recording { RecordingId = "act", VesselPersistentId = 10 };
            var bg = new Recording
            {
                RecordingId = "bg", VesselPersistentId = 77,
                VesselSnapshot = VesselNode(77, "LANDED", 0, (901, 555)),
            };
            var flight = Flight(Tree(act, bg));
            flight.pendingDockRouteEndpointAtDock = new RouteEndpoint { VesselPersistentId = 77, RootPartUId = 0 };
            flight.pendingDockRouteEndpointSituation = (int)Vessel.Situations.LANDED;

            RunMergeBranchUntilRecorderStart(flight, routeTargetPid: 77);

            Assert.DoesNotContain(logLines, l => l.Contains("restored from background snapshot"));
            Assert.Contains(logLines, l => l.Contains("Route proof endpoint root backfilled from the pre-couple snapshot: targetPid=77 rootPartUId=555"));
        }

        [Fact]
        public void MergeBranch_BackgroundSnapshotOfAnotherPid_DoesNotBackfillTheRoot()
        {
            var act = new Recording { RecordingId = "act", VesselPersistentId = 10 };
            var bg = new Recording
            {
                RecordingId = "bg", VesselPersistentId = 66,
                VesselSnapshot = VesselNode(66, "LANDED", 0, (901, 555)),
            };
            var flight = Flight(Tree(act, bg));
            flight.pendingDockRouteEndpointAtDock = new RouteEndpoint { VesselPersistentId = 77, RootPartUId = 0 };
            flight.pendingDockRouteEndpointSituation = (int)Vessel.Situations.LANDED;

            RunMergeBranchUntilRecorderStart(flight, routeTargetPid: 77);

            Assert.DoesNotContain(logLines, l => l.Contains("root backfilled"));
            // The background parent's parts still serve as the endpoint part set (no pid check there).
            Assert.Contains(logLines, l => l.Contains("Route proof dock window capture skipped")
                && l.Contains("endpointParts=1"));
        }

        [Fact]
        public void MergeBranch_EndpointWithARootIsNotBackfilled()
        {
            var act = new Recording { RecordingId = "act", VesselPersistentId = 10 };
            var flight = Flight(Tree(act));
            flight.pendingDockRouteEndpointAtDock = new RouteEndpoint { VesselPersistentId = 77, RootPartUId = 9 };
            flight.pendingDockRouteEndpointSituation = (int)Vessel.Situations.LANDED;
            flight.pendingDockPartnerSnapshot = VesselNode(77, "LANDED", 0, (901, 444));
            flight.pendingDockPartnerSnapshotPid = 77;

            RunMergeBranchUntilRecorderStart(flight, routeTargetPid: 77, backgroundParentId: null);

            Assert.DoesNotContain(logLines, l => l.Contains("root backfilled"));
        }

        [Fact]
        public void MergeBranch_PreCoupleSnapshotOfAnotherPid_IsIgnored()
        {
            var act = new Recording { RecordingId = "act", VesselPersistentId = 10 };
            var flight = Flight(Tree(act));
            flight.pendingDockRouteEndpointAtDock = new RouteEndpoint { VesselPersistentId = 77, RootPartUId = 0 };
            flight.pendingDockRouteEndpointSituation = (int)Vessel.Situations.LANDED;
            flight.pendingDockPartnerSnapshot = VesselNode(88, "LANDED", 0, (901, 111));
            flight.pendingDockPartnerSnapshotPid = 88;

            RunMergeBranchUntilRecorderStart(flight, routeTargetPid: 77, backgroundParentId: null);

            Assert.DoesNotContain(logLines, l => l.Contains("root backfilled"));
            Assert.Contains(logLines, l => l.Contains("TryDeriveEndpointPartPidsFromPartner: no snapshot available partnerPid=77"));
            Assert.Contains(logLines, l => l.Contains("Route proof dock window capture skipped")
                && l.Contains("endpointParts=0"));
        }

        [Fact]
        public void MergeBranch_NoEndpointAnywhere_LogsUnavailable()
        {
            var act = new Recording { RecordingId = "act", VesselPersistentId = 10 };
            var bg = new Recording
            {
                RecordingId = "bg", VesselPersistentId = 77,
                // No situation: the snapshot endpoint cannot be built.
                VesselSnapshot = VesselNode(77, null, 0, (901, 555)),
            };
            var tree = Tree(act, bg);
            var flight = Flight(tree);

            RunMergeBranchUntilRecorderStart(flight, routeTargetPid: 77);

            Assert.DoesNotContain(logLines, l => l.Contains("restored from background snapshot"));
            Assert.Contains(logLines, l => l.Contains("Route proof endpoint unavailable at dock targetPid=77"));
            Assert.Contains(logLines, l => l.Contains("Route proof dock window capture skipped")
                && l.Contains("endpointParts=1"));
        }

        // ================= statics =================

        [Theory]
        [InlineData("PotatoRoid", true)]
        [InlineData("potatoroid.v2", true)]
        [InlineData("xPotatoComet", true)]
        [InlineData("mk1pod.v2", false)]
        [InlineData("", false)]
        public void AsteroidOrCometPart(string name, bool expected)
        {
            Assert.Equal(expected, ParsekFlightSplitBrancher.ContainsAsteroidOrCometPart(new[] { "fuelTank", name }));
        }

        [Fact]
        public void AsteroidOrCometPart_NullList_False()
        {
            Assert.False(ParsekFlightSplitBrancher.ContainsAsteroidOrCometPart(null));
            Assert.False(ParsekFlightSplitBrancher.ContainsAsteroidOrCometPart(new string[] { null }));
        }

        [Theory]
        [InlineData("ModuleAsteroid", true)]
        [InlineData("ModuleComet", true)]
        [InlineData("moduleasteroid", false)]
        [InlineData("ModuleAsteroidInfo", false)]
        [InlineData(null, false)]
        public void AsteroidOrCometModule(string name, bool expected)
        {
            Assert.Equal(expected, ParsekFlightSplitBrancher.ContainsAsteroidOrCometModule(new[] { "ModuleCommand", name }));
        }

        [Fact]
        public void AsteroidOrCometModule_NullList_False()
        {
            Assert.False(ParsekFlightSplitBrancher.ContainsAsteroidOrCometModule(null));
        }

        [Theory]
        [InlineData("LANDED", true, Vessel.Situations.LANDED)]
        [InlineData("orbiting", true, Vessel.Situations.ORBITING)]
        [InlineData("1", true, Vessel.Situations.LANDED)]
        // Enum.TryParse accepts any numeric string, defined or not, so the IsDefined
        // fallback below it never sees a numeric value: "3" parses to (Situations)3.
        [InlineData("3", true, (Vessel.Situations)3)]
        [InlineData("nonsense", false, default(Vessel.Situations))]
        [InlineData("", false, default(Vessel.Situations))]
        public void RouteEndpointSituationParse(string raw, bool ok, Vessel.Situations expected)
        {
            var node = new ConfigNode("VESSEL");
            if (raw != null) node.AddValue("sit", raw);
            Assert.Equal(ok, ParsekFlightSplitBrancher.TryParseRouteEndpointSituation(node, out var sit));
            Assert.Equal(expected, sit);
        }

        [Fact]
        public void RouteEndpointSituationParse_NullSnapshot_False()
        {
            Assert.False(ParsekFlightSplitBrancher.TryParseRouteEndpointSituation(null, out _));
        }

        [Fact]
        public void SnapshotRouteCoordinate_PrimaryThenFallback()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("lat", "1.25");
            node.AddValue("longitude", "-2.5");
            Assert.True(ParsekFlightSplitBrancher.TryGetSnapshotRouteCoordinate(node, "lat", "latitude", out double lat));
            Assert.Equal(1.25, lat);
            Assert.True(ParsekFlightSplitBrancher.TryGetSnapshotRouteCoordinate(node, "lon", "longitude", out double lon));
            Assert.Equal(-2.5, lon);
            Assert.False(ParsekFlightSplitBrancher.TryGetSnapshotRouteCoordinate(node, "alt", "altitude", out _));
        }

        [Theory]
        [InlineData(Vessel.Situations.LANDED, true)]
        [InlineData(Vessel.Situations.SPLASHED, true)]
        [InlineData(Vessel.Situations.PRELAUNCH, true)]
        [InlineData(Vessel.Situations.FLYING, false)]
        [InlineData(Vessel.Situations.ORBITING, false)]
        [InlineData(Vessel.Situations.SUB_ORBITAL, false)]
        public void RouteEndpointSurfaceSituation(Vessel.Situations sit, bool expected)
        {
            Assert.Equal(expected, ParsekFlightSplitBrancher.IsRouteEndpointSurfaceSituation(sit));
        }

        [Theory]
        [InlineData(BranchPointType.Dock, "DOCK")]
        [InlineData(BranchPointType.Board, "BOARD")]
        [InlineData(BranchPointType.Undock, null)]
        [InlineData(BranchPointType.EVA, null)]
        [InlineData(BranchPointType.JointBreak, null)]
        public void MergeCause(BranchPointType type, string expected)
        {
            Assert.Equal(expected, ParsekFlightSplitBrancher.GetMergeCauseForBranchType(type));
        }

        [Fact]
        public void EvaKerbalName_NoTargetPart_Null()
        {
            Assert.Null(ParsekFlightSplitBrancher.ExtractEvaKerbalName(
                new GameEvents.FromToAction<Part, Part>(null, null)));
        }

        [Fact]
        public void StandaloneFallbackLegName_StartSnapshotNameElseUnknown()
        {
            var ghost = new ConfigNode("VESSEL");
            ghost.AddValue("name", "Start Name");
            var captured = new Recording { VesselName = "Stop Name", GhostVisualSnapshot = ghost };
            Assert.Equal("Start Name", ParsekFlightSplitBrancher.ResolveStandaloneFallbackLegName(null, captured));
            Assert.Contains(logLines, l => l.Contains("FallbackCommitSplitRecorder: leg name 'Start Name' source=kept-existing pid=0")
                && l.Contains("stop-time active-vessel name 'Stop Name' is not used"));

            Assert.Equal("Unknown Vessel", ParsekFlightSplitBrancher.ResolveStandaloneFallbackLegName(null, null));
            Assert.Contains(logLines, l => l.Contains("leg name 'Unknown Vessel' source=unknown pid=0")
                && l.Contains("name '(null)' is not used"));
        }

        [Fact]
        public void BreakupSeedPose_NoSeed_AndNoLiveVessel()
        {
            Assert.Equal("seedPoint=none",
                ParsekFlightSplitBrancher.DescribeBreakupChildSeedPose(null, null, 1));
            var point = new TrajectoryPoint
            {
                ut = 12.345, bodyName = "Kerbin", latitude = 1, longitude = 2, altitude = 3,
                velocity = new UnityEngine.Vector3(1, 2, 3),
            };
            Assert.Equal(
                "seedPoint@12.35 body=Kerbin flags=0 lla=(1.00,2.00,3.00) vel=(1.00,2.00,3.00)",
                ParsekFlightSplitBrancher.DescribeBreakupChildSeedPose(point, null, 1));
        }

        [Fact]
        public void ControlledChildSeed_NoLiveVessel_KeepsTheCapturedSeed()
        {
            Assert.Null(ParsekFlightSplitBrancher.ResolveControlledChildInitialTrajectoryPoint(5, null, null, 10));
            Assert.DoesNotContain(logLines, l => l.Contains("[Coalescer]"));

            var captured = new TrajectoryPoint { ut = 9.5, bodyName = "Kerbin" };
            var result = ParsekFlightSplitBrancher.ResolveControlledChildInitialTrajectoryPoint(5, captured, null, 10.25);
            Assert.True(result.HasValue);
            Assert.Equal(9.5, result.Value.ut);
            Assert.Contains(logLines, l => l.Contains("Controlled child initial seed: pid=5 source=decouple-callback")
                && l.Contains("capturedUT=9.50 liveUT=10.25 reason=live-vessel-missing"));
        }

        [Fact]
        public void BreakupSeedLiveRootDistance_NoVessel_FalseAndNaN()
        {
            Assert.False(ParsekFlightSplitBrancher.TryComputeBreakupSeedLiveRootDistance(
                new TrajectoryPoint(), null, 1, out double d, out double r));
            Assert.True(double.IsNaN(d));
            Assert.True(double.IsNaN(r));
        }
    }
}
