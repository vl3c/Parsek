using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;

namespace Parsek.InGameTests
{
    /// <summary>
    /// M-MIS-5 P2b end-to-end: the mid-tree docked-origin (undock -> undock
    /// shuttle) lift, exercised through the REAL production pipeline under live
    /// KSP statics. A synthetic shuttle tree - the transport roots MID-FLIGHT
    /// (no KSC launch, no start-docked proof), docks at depot A (the recorded
    /// origin window, dock 1000 / undock 1500), transits, docks at depot B (the
    /// delivery window, dock 2500 / undock 3000) - is committed and driven
    /// through <see cref="RouteAnalysisEngine.AnalyzeTree"/> (must ACCEPT as a
    /// mid-tree docked origin, the shape P2a rejected as status 9),
    /// <see cref="RouteBuilder.BuildRoute"/> (span = [origin undock .. depot-B
    /// dock], origin endpoint from the origin window's dock-time proof, the
    /// start-trim excluded keys, the persisted origin-undock UT), and the REAL
    /// <see cref="RouteOrchestrator.ResolveLoopUnit"/> (the built unit's span
    /// starts at the ORIGIN UNDOCK - a non-launch span start - with only the
    /// transit leg a member and the dock inside the lifted span).
    ///
    /// <para>The live two-delivery cadence firing stays with the manual shuttle
    /// playtest (the PR merge gate) and the sibling
    /// <see cref="LogisticsDockBoundaryDeliveryCadenceRuntimeTests"/> pattern;
    /// the loop-clock phase for a raised span start is xUnit-pinned
    /// (RouteLoopClockTests.DockPhase_RaisedSpanStart_CrossingFiresOncePerCycle).</para>
    /// </summary>
    public sealed class LogisticsShuttleRuntimeTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // Shuttle fixture UTs: root [0..1000] mid-orbit, depot-A window
        // [1000..1500] (the ORIGIN), transit [1500..2500], depot-B window
        // [2500..3000] (the delivery), post-undock structure after 3000.
        private const double OriginDockUT = 1000.0;
        private const double OriginUndockUT = 1500.0;
        private const double DeliveryDockUT = 2500.0;
        private const double DeliveryUndockUT = 3000.0;
        private const double TransitSpan = DeliveryDockUT - OriginUndockUT; // 1000

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            Description = "M-MIS-5 P2b: an undock->undock shuttle tree (mid-flight root, recorded depot-A origin window) is ACCEPTED by the real analysis as a mid-tree docked origin, builds a route spanning [origin undock .. depot-B dock] with the depot-A endpoint as origin, and resolves a real loop unit whose span starts at the ORIGIN UNDOCK with only the transit leg a member")]
        public void Shuttle_MidTreeOrigin_AnalyzesBuildsAndResolvesUnit()
        {
            if (Planetarium.fetch == null)
                InGameAssert.Skip("Planetarium.fetch is null; cannot resolve current UT");

            string treeId = "ingame-mmis5-p2b-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            RecordingTree tree = BuildShuttleTree(treeId);

            bool treeAdded = false;
            var committedAdded = new List<Recording>();
            try
            {
                // ---- REAL analysis: the P2b acceptance -----------------------
                RouteAnalysisResult analysis = RouteAnalysisEngine.AnalyzeTree(tree);
                InGameAssert.IsTrue(analysis.IsEligible,
                    $"P2b LIFT FAILED: the supported shuttle shape must analyze Eligible, got {analysis.Status} " +
                    $"(detail={analysis.RejectDetail ?? "<none>"})");
                InGameAssert.IsTrue(analysis.IsMidTreeDockedOrigin,
                    "P2b: the accepted shuttle run must classify as a mid-tree docked origin");
                InGameAssert.IsTrue(analysis.OriginConnectionWindow != null
                        && Math.Abs(analysis.OriginConnectionWindow.UndockUT - OriginUndockUT) < 1e-6,
                    "P2b: the FIRST window (depot A) must be the origin binding");
                InGameAssert.IsTrue(analysis.Stops != null && analysis.Stops.Count == 1
                        && Math.Abs(analysis.Stops[0].DockUT - DeliveryDockUT) < 1e-6,
                    "P2b: the origin window must NOT be a stop; only the depot-B delivery remains");

                // ---- REAL builder: origin-start plumbing ---------------------
                RouteBuilder.RouteBuildOutcome outcome = RouteBuilder.BuildRoute(
                    analysis, tree,
                    new RouteBuilder.RouteCreationInputs
                    {
                        Name = "Parsek M-MIS-5 P2b In-Game",
                        DispatchIntervalSeconds = TransitSpan
                    },
                    HighLogic.CurrentGame != null ? HighLogic.CurrentGame.Mode : Game.Modes.SANDBOX,
                    initialStatus: RouteStatus.Paused);
                InGameAssert.IsTrue(outcome.Route != null,
                    $"P2b: BuildRoute rejected the accepted shuttle analysis (reason={outcome.RejectReason ?? "<none>"})");
                Route route = outcome.Route;
                InGameAssert.IsTrue(Math.Abs(route.TransitDuration - TransitSpan) < 1e-6,
                    $"P2b: route span must be the transit leg [origin undock .. dock] = {TransitSpan.ToString("R", IC)}, " +
                    $"was {route.TransitDuration.ToString("R", IC)} (a launch-rooted span means the start trim did not land)");
                InGameAssert.IsTrue(Math.Abs(route.RecordedOriginUndockUT - OriginUndockUT) < 1e-6,
                    "P2b: the origin-undock UT must persist on the route (the M-MIS-9 start-prong anchor)");
                InGameAssert.IsTrue(!route.IsKscOrigin && route.Origin.VesselPersistentId == 777,
                    $"P2b: the origin must resolve to the depot-A endpoint (pid 777), " +
                    $"was pid={route.Origin.VesselPersistentId.ToString(IC)} ksc={route.IsKscOrigin}");

                // ---- REAL loop unit: non-launch span start -------------------
                // Commit the tree + recordings so ResolveLoopUnit's production
                // derivation (backing mission -> Missions pipeline -> committed
                // index alignment) runs against the real stores.
                RecordingStore.AddCommittedTreeForTesting(tree);
                treeAdded = true;
                foreach (Recording rec in tree.Recordings.Values)
                {
                    if (rec == null) continue;
                    RecordingStore.AddCommittedInternal(rec);
                    committedAdded.Add(rec);
                }

                double liveUT = Planetarium.GetUniversalTime();
                GhostPlaybackLogic.LoopUnit? unitOpt = RouteOrchestrator.ResolveLoopUnit(route, liveUT);
                InGameAssert.IsTrue(unitOpt.HasValue,
                    "P2b: ResolveLoopUnit returned no unit for the shuttle route (backing-mission derivation failed)");
                GhostPlaybackLogic.LoopUnit unit = unitOpt.Value;

                InGameAssert.IsTrue(Math.Abs(unit.SpanStartUT - OriginUndockUT) < 1.0,
                    $"P2b SPAN START FAILED: the unit span must start at the ORIGIN UNDOCK {OriginUndockUT.ToString("R", IC)}, " +
                    $"was {unit.SpanStartUT.ToString("R", IC)} (a start at the tree root 0 means the start trim did not land)");
                InGameAssert.IsTrue(Math.Abs(unit.SpanEndUT - DeliveryDockUT) < 1.0,
                    $"P2b: the unit span must end at the delivery dock {DeliveryDockUT.ToString("R", IC)}, " +
                    $"was {unit.SpanEndUT.ToString("R", IC)}");
                InGameAssert.IsTrue(RouteLoopClock.IsDockUTInSpan(unit, route.RecordedDockUT),
                    $"P2b: the recorded dock UT {route.RecordedDockUT.ToString("R", IC)} must sit inside the lifted span " +
                    $"[{unit.SpanStartUT.ToString("R", IC)}, {unit.SpanEndUT.ToString("R", IC)}] (no crossing could fire)");

                var committed = RecordingStore.CommittedRecordings;
                int idxTransit = FindCommittedIndex(committed, "transit");
                int idxRoot = FindCommittedIndex(committed, "root");
                int idxDockedA = FindCommittedIndex(committed, "dockedA");
                int idxDepotA = FindCommittedIndex(committed, "depotA");
                InGameAssert.IsTrue(idxTransit >= 0 && idxRoot >= 0,
                    "fixture push failed: shuttle legs not found in CommittedRecordings");
                InGameAssert.IsTrue(IsUnitMember(unit, idxTransit),
                    "P2b: the transit leg must be a loop-unit member");
                InGameAssert.IsFalse(IsUnitMember(unit, idxRoot),
                    "P2b: the pre-origin root leg must be a NON-member (start-trimmed)");
                InGameAssert.IsFalse(IsUnitMember(unit, idxDockedA),
                    "P2b: the docked-at-A origin stretch must be a NON-member");
                if (idxDepotA >= 0)
                    InGameAssert.IsFalse(IsUnitMember(unit, idxDepotA),
                        "P2b: the depot-A offshoot must be a NON-member (origin-undock scoping)");

                ParsekLog.Info("TestRunner",
                    $"Shuttle_P2b_InGame: PASS status={analysis.Status} spanStart={unit.SpanStartUT.ToString("R", IC)} " +
                    $"spanEnd={unit.SpanEndUT.ToString("R", IC)} cadence={unit.CadenceSeconds.ToString("R", IC)} " +
                    $"originPid={route.Origin.VesselPersistentId.ToString(IC)} " +
                    $"originUndockUT={route.RecordedOriginUndockUT.ToString("R", IC)} " +
                    $"excludedKeys={route.ExcludedIntervalKeys.Count.ToString(IC)}");
            }
            finally
            {
                for (int i = 0; i < committedAdded.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedAdded[i]);
                if (treeAdded)
                    RemoveCommittedTree(treeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);
            }
        }

        // ==================================================================
        // Fixture
        // ==================================================================

        // Shuttle tree (mirrors RouteBackingMissionTests.BuildShuttleOriginTree,
        // with the two recorded connection windows the analysis consumes):
        //   root     C0/0 [0..1000]     mid-orbit start (ROOT; no launch site)
        //   dockedA  C0/1 [1000..1500]  Dock BP@1000; carries the ORIGIN window
        //   transit  C0/2 [1500..2500]  Undock BP@1500 (depotA peels)
        //   depotA   C1/0 [1500..2600]  the depot-A offshoot
        //   dockedB  C0/3 [2500..3000]  Dock BP@2500; carries the DELIVERY window
        //   survivor C0/4 [3000..3600]  Undock BP@3000 (payload peels)
        //   payload  C2/0 [3000..3300]
        private static RecordingTree BuildShuttleTree(string treeId)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = "root" };
            tree.Recordings["root"] = Leg("root", "C0", 0, 0, OriginDockUT, "Transport");
            tree.Recordings["dockedA"] = Leg("dockedA", "C0", 1, OriginDockUT, OriginUndockUT, "Transport");
            tree.Recordings["transit"] = Leg("transit", "C0", 2, OriginUndockUT, DeliveryDockUT, "Transport");
            tree.Recordings["depotA"] = Leg("depotA", "C1", 0, OriginUndockUT, 2600, "DepotA");
            tree.Recordings["dockedB"] = Leg("dockedB", "C0", 3, DeliveryDockUT, DeliveryUndockUT, "Transport");
            tree.Recordings["survivor"] = Leg("survivor", "C0", 4, DeliveryUndockUT, 3600, "Transport");
            tree.Recordings["payload"] = Leg("payload", "C2", 0, DeliveryUndockUT, 3300, "Payload");

            // The ORIGIN window on the depot-A dock-merged child: the transport
            // loads 40 Ore while docked (endpoint 40->0, transport 0->40).
            tree.Recordings["dockedA"].RouteConnectionWindows = new List<RouteConnectionWindow>
            {
                new RouteConnectionWindow
                {
                    WindowId = "w-origin",
                    DockUT = OriginDockUT,
                    UndockUT = OriginUndockUT,
                    TransferTargetVesselPid = 777,
                    TransferKind = RouteConnectionKind.DockingPort,
                    DockEndpointResources = Ore(40.0),
                    UndockEndpointResources = Ore(0.0),
                    DockTransportResources = Ore(0.0),
                    UndockTransportResources = Ore(40.0),
                    EndpointAtDock = new RouteEndpoint
                    {
                        VesselPersistentId = 777,
                        BodyName = "Mun",
                        Latitude = 10.0,
                        Longitude = 20.0,
                        Altitude = 100000.0,
                        IsSurface = false
                    },
                    TransferEndpointSituation = 3
                }
            };
            // The DELIVERY window on the depot-B dock-merged child: the transport
            // delivers the 40 Ore (transport 40->0, endpoint 0->40).
            tree.Recordings["dockedB"].RouteConnectionWindows = new List<RouteConnectionWindow>
            {
                new RouteConnectionWindow
                {
                    WindowId = "w-delivery",
                    DockUT = DeliveryDockUT,
                    UndockUT = DeliveryUndockUT,
                    TransferTargetVesselPid = 888,
                    TransferKind = RouteConnectionKind.DockingPort,
                    DockEndpointResources = Ore(0.0),
                    UndockEndpointResources = Ore(40.0),
                    DockTransportResources = Ore(40.0),
                    UndockTransportResources = Ore(0.0),
                    EndpointAtDock = new RouteEndpoint
                    {
                        VesselPersistentId = 888,
                        BodyName = "Mun",
                        Latitude = -15.0,
                        Longitude = 60.0,
                        Altitude = 120000.0,
                        IsSurface = false
                    },
                    TransferEndpointSituation = 3
                }
            };

            tree.BranchPoints.Add(BP("dockA-bp", BranchPointType.Dock,
                new[] { "root" }, new[] { "dockedA" }, OriginDockUT));
            tree.BranchPoints.Add(BP("origin-undock-bp", BranchPointType.Undock,
                new[] { "dockedA" }, new[] { "transit", "depotA" }, OriginUndockUT));
            tree.BranchPoints.Add(BP("dockB-bp", BranchPointType.Dock,
                new[] { "transit" }, new[] { "dockedB" }, DeliveryDockUT));
            tree.BranchPoints.Add(BP("undockB-bp", BranchPointType.Undock,
                new[] { "dockedB" }, new[] { "survivor", "payload" }, DeliveryUndockUT));
            return tree;
        }

        private static Dictionary<string, ResourceAmount> Ore(double amount)
        {
            return new Dictionary<string, ResourceAmount>
            {
                ["Ore"] = new ResourceAmount { amount = amount, maxAmount = 80.0 }
            };
        }

        private static Recording Leg(string id, string chainId, int chainIndex,
            double start, double end, string vessel)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
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

        private static int FindCommittedIndex(IReadOnlyList<Recording> committed, string recordingId)
        {
            if (committed == null) return -1;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i] != null && string.Equals(committed[i].RecordingId, recordingId, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        private static bool IsUnitMember(GhostPlaybackLogic.LoopUnit unit, int committedIndex)
        {
            int[] members = unit.MemberIndices;
            if (members == null) return false;
            for (int i = 0; i < members.Length; i++)
                if (members[i] == committedIndex)
                    return true;
            return false;
        }

        private static void RemoveCommittedTree(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees == null)
                return;
            var survivors = new List<RecordingTree>(trees.Count);
            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree t = trees[i];
                if (t != null && string.Equals(t.Id, treeId, StringComparison.Ordinal))
                    continue;
                survivors.Add(t);
            }
            RecordingStore.ClearCommittedTreesInternal();
            for (int i = 0; i < survivors.Count; i++)
                RecordingStore.AddCommittedTreeForTesting(survivors[i]);
        }
    }
}
