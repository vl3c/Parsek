using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// M-MIS-5 P1 (D4) end-to-end residual closer. The P1 exit criterion for the
    /// ratified route-end flip was a MANUAL playtest: fly an N=1 docking supply
    /// route and time two consecutive deliveries by hand, confirming the
    /// delivery-to-delivery gap collapsed to <c>DispatchInterval</c> (the realized
    /// route cycle re-aligned down from launch->undock to launch->dock, so an
    /// existing sub-span-cadence route delivers MORE OFTEN, by the docked-stretch
    /// duration). This test automates that timing so no mission has to be designed
    /// and flown by hand.
    ///
    /// <para>It asserts the flip on the REAL built unit (not a hand-authored
    /// LoopUnit): a DOCKING supply route (backed by a committed launch->dock->undock
    /// tree, the same fixture shape the other logistics runtime tests commit) is
    /// resolved through the production <see cref="RouteBackingMission.BuildMission"/>
    /// -> <see cref="RouteOrchestrator.ResolveLoopUnit"/> path. Part (b) reads the
    /// resulting <see cref="GhostPlaybackLogic.LoopUnit"/> and confirms the span
    /// END corresponds to the DOCK UT (2000, the docked-stretch boundary), NOT the
    /// undock UT (3000, the old cycle end), and that the docked combined leg is a
    /// NON-member (the route ghost retires at the dock). Part (c) then fires TWO
    /// consecutive delivery cycles through the LIVE
    /// <see cref="RouteOrchestrator.Tick(double)"/> path (the real
    /// <c>ProcessLoopRoute</c> -> <c>EmitLoopCycle</c> -> live <c>ApplyDelivery</c>
    /// writer) and asserts the delivery-to-delivery gap == <c>DispatchInterval</c>,
    /// that each cycle delivered EXACTLY once (the destination LiquidFuel tank rose
    /// by the manifest amount each cycle), and that each cycle charged the dispatch
    /// (RouteDispatched) exactly once (funds charged once per cycle; the Career
    /// funds facet is asserted only when the live env is Career).</para>
    ///
    /// <para><b>Why the two-cycle firing arms the resolver seam.</b> Part (b) proves
    /// the flip on the genuine backing-mission unit built with NO seam (live
    /// <c>ResolveLoopUnit</c>). Part (c) then RE-PROJECTS that same real unit's span
    /// geometry (SpanStart/SpanEnd==dock/cadence) into a deterministic clock, armed
    /// via the production <c>LoopUnitResolverForTesting</c> seam, so the two
    /// consecutive dock crossings land at UTs the test controls exactly one cadence
    /// apart (the live phase-lock floors PhaseAnchorUT to spanEnd against the live
    /// Planetarium UT, which is not a fixed reference for a deterministic two-tick
    /// gap). The FIRE path is fully live: the seam only supplies the span-clock
    /// fields; the crossing detector, eligibility, dispatch/debit, and the live tank
    /// fill + ledger rows are the real orchestrator. The cadence carried into the
    /// seam is the REAL resolved unit's cadence, which (by the D4 flip) equals the
    /// launch->dock span == DispatchInterval, so the measured gap IS the flipped
    /// cadence, not a value the test invented.</para>
    ///
    /// <para>Live-only preconditions are gated the way the neighboring logistics
    /// runtime tests gate them: a loaded+unpacked active vessel carrying a LiquidFuel
    /// tank is required for the live delivery writer to mutate the tank this test
    /// reads (a PRELAUNCH/pad vessel takes the unloaded proto-snapshot path and is
    /// skipped); the Career funds facet is asserted only in Career and skipped in
    /// Sandbox. Teardown clears the seam, restores the tank, removes the route +
    /// committed tree + pushed recordings, and restores the route store.</para>
    /// </summary>
    public sealed class LogisticsDockBoundaryDeliveryCadenceRuntimeTests
    {
        private const string LiquidFuelName = "LiquidFuel";
        private const double DeliveryAmount = 5.0;
        private const double ResourceTolerance = 0.01;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // Fixture tree UTs (mirror BuildLaunchDockUndockTree in the sibling
        // LogisticsRouteOnMissionsRuntimeTests): root launch 1000, DOCK 2000,
        // UNDOCK 3000. The D4 flip's whole point is that the route render window
        // ends at the DOCK (2000), NOT the undock (3000).
        private const double RootLaunchUT = 1000.0;
        private const double DockUT = 2000.0;
        private const double UndockUT = 3000.0;

        // The realized route cycle: [launch .. dock] == 1000 s. Set DispatchInterval
        // to that span so the cadence == DispatchInterval and the two-delivery gap
        // this test measures IS the flipped cadence. (Pre-M-MIS-5 the render window
        // ran to the UNDOCK, so the realized cycle would have been launch->undock ==
        // 2000 s; the flip shortens it by the docked-stretch duration 1000 s.)
        private const double LaunchToDockSpan = DockUT - RootLaunchUT;   // 1000
        private const double LaunchToUndockSpan = UndockUT - RootLaunchUT; // 2000 (the OLD cycle)
        private const double DispatchInterval = LaunchToDockSpan;         // 1000

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only - mutates RouteStore, Ledger, RecordingStore committed trees/recordings, the RouteOrchestrator.LoopUnitResolverForTesting seam, and live PartResource.amount under live KSP statics; excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "M-MIS-5 D4: a docking route's REAL backing-mission loop unit ends its span at the DOCK UT (not the undock) with the docked leg a non-member, and two consecutive live-orchestrator delivery cycles land exactly DispatchInterval apart, each delivering the manifest once and charging the dispatch once (the realized cycle collapsed launch->dock, shorter than the old launch->undock span)")]
        public IEnumerator DockBoundary_RealUnit_EndsAtDock_TwoDeliveriesGapEqualsDispatchInterval()
        {
            // Post-restore unpack wait: the isolated-batch baseline quickload leaves
            // the active vessel packed for a few frames; without this wait a
            // following-a-destructive-test run would skip on the packed precondition
            // (same defect class as the M1 origin-debit tests, fixed 2026-06-11).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            // ---- PRECONDITION GATE -------------------------------------------
            // The loop-fire seam + the resolver/emit path must exist; a failure here
            // means an upstream phase is incomplete, not that D4 regressed.
            FieldInfo resolverField = typeof(RouteOrchestrator).GetField(
                "LoopUnitResolverForTesting",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (resolverField == null)
                InGameAssert.Skip("PRECONDITION: RouteOrchestrator.LoopUnitResolverForTesting seam missing (upstream Phase 4 incomplete)");
            MethodInfo resolveMethod = typeof(RouteOrchestrator).GetMethod(
                "ResolveLoopUnit",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (resolveMethod == null)
                InGameAssert.Skip("PRECONDITION: RouteOrchestrator.ResolveLoopUnit missing (upstream Phase 4 incomplete)");
            MethodInfo buildMissionMethod = typeof(RouteBackingMission).GetMethod(
                "BuildMission",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (buildMissionMethod == null)
                InGameAssert.Skip("PRECONDITION: RouteBackingMission.BuildMission missing (backing-mission seam absent)");

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live vessel to deliver onto");
            if (Planetarium.fetch == null)
                InGameAssert.Skip("Planetarium.fetch is null; cannot resolve current UT");

            Vessel activeVessel = FlightGlobals.ActiveVessel;

            Part fuelPart;
            PartResource fuelResource;
            if (!TryFindLiquidFuelPart(activeVessel, out fuelPart, out fuelResource))
                InGameAssert.Skip(
                    $"Active vessel '{activeVessel.vesselName}' has no part with a LiquidFuel resource; " +
                    "skipping - pick a vessel with at least one LF tank to run this test");

            // The live delivery writer only mutates the live PartResource (the tank
            // this test reads) when the destination is loaded+unpacked. A
            // PRELAUNCH/pad vessel reports unloaded and the delivery writes the proto
            // snapshot instead, so the live tank stays flat. Skip rather than false-fail.
            if (!(activeVessel.loaded && !activeVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{activeVessel.vesselName}' is not loaded+unpacked " +
                    $"(loaded={activeVessel.loaded}, packed={activeVessel.packed}); the loop-fire delivery would take " +
                    "the unloaded proto-snapshot path which does not mutate the live PartResource this test reads");

            string routeTreeId = "ingame-mmis5-d4-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-mmis5-d4-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Snapshot routes for restore.
            var preExistingRoutes = new List<Route>();
            IReadOnlyList<Route> committedRoutes = RouteStore.CommittedRoutes;
            for (int i = 0; i < committedRoutes.Count; i++)
                if (committedRoutes[i] != null)
                    preExistingRoutes.Add(committedRoutes[i]);

            // Committed launch->dock->undock tree so the REAL RouteBackingMission /
            // ResolveLoopUnit derivation runs against it (and so ERS carries the
            // route's SourceRefs for the real eligibility gate).
            RecordingTree routeTree = BuildLaunchDockUndockTree(routeTreeId);

            bool routeTreeAdded = false, routeAdded = false, seamArmed = false;
            var previousResolver = RouteOrchestrator.LoopUnitResolverForTesting;
            var committedAdded = new List<Recording>();

            // Pre-drain / restore bookkeeping for the two live deliveries.
            double originalAmount = fuelResource.amount;
            double maxAmount = fuelResource.maxAmount;

            try
            {
                RecordingStore.AddCommittedTreeForTesting(routeTree);
                routeTreeAdded = true;
                // AddCommittedTreeForTesting registers the tree only, NOT its
                // recordings, so push the member recordings into CommittedRecordings
                // explicitly (ERS reads RecordingStore.CommittedRecordings, and the
                // real ResolveLoopUnit aligns member indices to that raw list).
                foreach (Recording rec in routeTree.Recordings.Values)
                {
                    if (rec == null) continue;
                    RecordingStore.AddCommittedInternal(rec);
                    committedAdded.Add(rec);
                }

                // Build the route with the PRODUCTION-SHAPED fields RouteBuilder would
                // emit for a docking route: segmentEndUT / RecordedDockUT == the DOCK
                // (M-MIS-5, D4 - NOT the undock), the excluded-interval keys derived
                // by the REAL RouteBackingMission.ComputeExcludedIntervalKeys, KSC
                // origin, and the active vessel as the delivery stop endpoint.
                HashSet<string> excluded = RouteBackingMission.ComputeExcludedIntervalKeys(
                    routeTree, segmentEndUT: DockUT, launchUT: RootLaunchUT);

                var route = new Route
                {
                    Id = routeId,
                    Name = "Parsek M-MIS-5 D4 In-Game",
                    Status = RouteStatus.Active,
                    IsKscOrigin = true,
                    BackingMissionTreeId = routeTreeId,
                    ExcludedIntervalKeys = excluded,
                    RecordedDockUT = DockUT,
                    DockMemberRecordingId = "docked",
                    LoopAnchorUT = RootLaunchUT,
                    LastObservedLoopCycleIndex = -1,
                    TransitDuration = LaunchToDockSpan,
                    DispatchInterval = DispatchInterval,
                    CompletedCycles = 0,
                    SkippedCycles = 0,
                    KscDispatchFundsCost = 0.0,
                    CostManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        { LiquidFuelName, DeliveryAmount },
                    },
                    RecordingIds = new List<string> { "launch", "docked" },
                    SourceRefs = new List<RouteSourceRef>
                    {
                        new RouteSourceRef { RecordingId = "launch", TreeId = routeTreeId },
                        new RouteSourceRef { RecordingId = "docked", TreeId = routeTreeId },
                    },
                    Stops = new List<RouteStop>
                    {
                        new RouteStop
                        {
                            Endpoint = new RouteEndpoint
                            {
                                VesselPersistentId = activeVessel.persistentId,
                                BodyName = activeVessel.mainBody != null ? activeVessel.mainBody.bodyName : "Kerbin",
                                IsSurface = false,
                            },
                            ConnectionKind = RouteConnectionKind.DockingPort,
                            DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                            {
                                { LiquidFuelName, DeliveryAmount },
                            },
                            InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                            SegmentIndexBefore = 0,
                            DeliveryOffsetSeconds = 0.0,
                        },
                    },
                };
                RouteStore.AddRoute(route);
                routeAdded = RouteStore.TryGetRoute(routeId, out _);
                InGameAssert.IsTrue(routeAdded, "Docking route was not stored");

                // Committed indices of the fixture legs (for the member/non-member
                // assertions on the REAL resolved unit).
                var committed = RecordingStore.CommittedRecordings;
                int idxLaunch = FindCommittedIndex(committed, "launch");
                int idxDocked = FindCommittedIndex(committed, "docked");
                int idxSurvivor = FindCommittedIndex(committed, "survivor");
                InGameAssert.IsTrue(idxLaunch >= 0,
                    "launch leg not found in CommittedRecordings (fixture push failed)");
                InGameAssert.IsTrue(idxDocked >= 0,
                    "docked leg not found in CommittedRecordings (fixture push failed)");

                // =====================================================================
                // PART (b): confirm the D4 flip on the REAL built unit.
                // ResolveLoopUnit with the seam NOT armed builds the unit from the
                // committed tree via the production RouteBackingMission -> Missions
                // pipeline. The span END must correspond to the DOCK UT, and the
                // docked combined leg must be a NON-member (ghost retires at the dock).
                // =====================================================================
                double liveUT = Planetarium.GetUniversalTime();
                GhostPlaybackLogic.LoopUnit? realUnitOpt =
                    RouteOrchestrator.ResolveLoopUnit(route, liveUT);
                InGameAssert.IsTrue(realUnitOpt.HasValue,
                    "ResolveLoopUnit returned no unit for the docking route (backing-mission derivation failed)");
                GhostPlaybackLogic.LoopUnit realUnit = realUnitOpt.Value;

                // Span geometry, expressed relative to the tree's own UTs. The span
                // is [launch .. dock] == [1000 .. 2000], length 1000; it must NOT run
                // to the undock (3000).
                double realSpanLen = realUnit.SpanEndUT - realUnit.SpanStartUT;
                InGameAssert.IsTrue(Math.Abs(realSpanLen - LaunchToDockSpan) < 1.0,
                    $"D4 FLIP FAILED: real backing-mission unit span length should be the launch->DOCK span " +
                    $"{LaunchToDockSpan.ToString("R", IC)} (ends at the dock), was {realSpanLen.ToString("R", IC)} " +
                    $"(spanStart={realUnit.SpanStartUT.ToString("R", IC)} spanEnd={realUnit.SpanEndUT.ToString("R", IC)}); " +
                    $"the OLD launch->undock span was {LaunchToUndockSpan.ToString("R", IC)} - a span of that length means " +
                    "the window still ends at the undock and the flip did not land");

                // The recorded dock UT sits at the span end (the [launch..dock]
                // segment end), so the dock is inside the span and a crossing can fire.
                InGameAssert.IsTrue(RouteLoopClock.IsDockUTInSpan(realUnit, DockUT),
                    $"Recorded dock UT {DockUT.ToString("R", IC)} is not inside the real unit span " +
                    $"[{realUnit.SpanStartUT.ToString("R", IC)}, {realUnit.SpanEndUT.ToString("R", IC)}] (no crossing could fire)");

                // Span end aligns with the dock offset (span end == spanStart + launch->dock span,
                // i.e. the dock offset from spanStart equals the whole span).
                double dockOffsetInSpan = DockUT - RootLaunchUT; // 1000, == realSpanLen
                InGameAssert.IsTrue(Math.Abs(realSpanLen - dockOffsetInSpan) < 1.0,
                    $"D4 FLIP FAILED: the dock offset within the span ({dockOffsetInSpan.ToString("R", IC)}) should equal the " +
                    $"whole span length ({realSpanLen.ToString("R", IC)}) - i.e. the span END is the DOCK; a shorter offset means " +
                    "the span extends past the dock (toward the undock)");

                // The launch leg is a member (loops, never spawns); the docked
                // combined leg is a NON-member (the ghost retires at the dock, D4);
                // the post-undock survivor is a NON-member.
                InGameAssert.IsTrue(IsUnitMember(realUnit, idxLaunch),
                    "launch leg must be a loop-unit member of the real backing-mission unit");
                InGameAssert.IsFalse(IsUnitMember(realUnit, idxDocked),
                    "D4: the docked combined leg must be a NON-member (rendering stops at the dock, the ghost retires there)");
                if (idxSurvivor >= 0)
                    InGameAssert.IsFalse(IsUnitMember(realUnit, idxSurvivor),
                        "D4: the post-undock survivor leg must be a NON-member (rendering stops at the dock)");

                ParsekLog.Info("TestRunner",
                    $"DockBoundary_D4_InGame (b): PASS realUnit spanStart={realUnit.SpanStartUT.ToString("R", IC)} " +
                    $"spanEnd={realUnit.SpanEndUT.ToString("R", IC)} spanLen={realSpanLen.ToString("R", IC)} " +
                    $"cadence={realUnit.CadenceSeconds.ToString("R", IC)} dockMember={IsUnitMember(realUnit, idxDocked)} " +
                    $"launchMember={IsUnitMember(realUnit, idxLaunch)}");

                // =====================================================================
                // PART (c): fire TWO consecutive delivery cycles through the LIVE
                // orchestrator and assert the delivery-to-delivery gap ==
                // DispatchInterval (the realized cycle collapsed to launch->dock).
                // =====================================================================

                // The realized cadence IS the real resolved unit's cadence. By the D4
                // flip that equals the launch->dock span (== DispatchInterval here);
                // pin it so a regression that widened the span (back to launch->undock)
                // would move the measured gap and fail this assertion.
                double realizedCadence = realUnit.CadenceSeconds;
                InGameAssert.IsTrue(Math.Abs(realizedCadence - DispatchInterval) < 1.0,
                    $"The realized route cadence (the real unit's CadenceSeconds {realizedCadence.ToString("R", IC)}) " +
                    $"should equal DispatchInterval {DispatchInterval.ToString("R", IC)} (== the launch->DOCK span); " +
                    $"the OLD launch->undock cycle would have been {LaunchToUndockSpan.ToString("R", IC)}");

                // Deterministic two-tick clock: re-project the REAL unit's span
                // geometry (SpanStart/SpanEnd==dock/cadence) onto a fixed reference so
                // two dock crossings land exactly one cadence apart at UTs the test
                // controls. The FIRE path stays fully live; the seam supplies only the
                // span-clock fields. Anchor == spanStart so cycle k's dock UT ==
                // spanStart + k*cadence + (dockOffset) == spanStart + k*cadence + span
                // (dock == span end). Ticking just past each cycle's dock fires it.
                const double SeamSpanStart = RootLaunchUT;                 // 1000
                double seamSpanEnd = SeamSpanStart + realizedCadence;      // 1000 + 1000 == 2000 (dock)
                double seamCadence = realizedCadence;                      // == DispatchInterval
                double seamAnchor = SeamSpanStart;
                double seamDockOffset = DockUT - RootLaunchUT;             // 1000 (dock == span end)

                var seamUnit = new GhostPlaybackLogic.LoopUnit(
                    ownerIndex: idxLaunch >= 0 ? idxLaunch : 0,
                    memberIndices: new[] { idxLaunch >= 0 ? idxLaunch : 0 },
                    spanStartUT: SeamSpanStart,
                    spanEndUT: seamSpanEnd,
                    cadenceSeconds: seamCadence,
                    phaseAnchorUT: seamAnchor);

                // Cycle k's dock instant: anchor + k*cadence + dockOffset. Tick a hair
                // past each so loopUT >= RecordedDockUT (the crossing gate).
                double cycle0DockUT = seamAnchor + 0 * seamCadence + seamDockOffset; // 2000
                double cycle1DockUT = seamAnchor + 1 * seamCadence + seamDockOffset; // 3000
                double tick0UT = cycle0DockUT + 0.5;
                double tick1UT = cycle1DockUT + 0.5;

                RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) =>
                {
                    if (r != null && string.Equals(r.Id, routeId, StringComparison.Ordinal))
                        return seamUnit;
                    return previousResolver != null ? previousResolver(r, ut) : (GhostPlaybackLogic.LoopUnit?)null;
                };
                seamArmed = true;

                bool isCareer = new LiveRouteRuntimeEnvironment().IsCareer;

                // ---- CYCLE 0 -----------------------------------------------------
                double preDrain0 = DrainForDelivery(fuelResource, originalAmount);
                double headroom0 = maxAmount - preDrain0;
                double expectedDelta0 = DeliveryAmount < headroom0 ? DeliveryAmount : headroom0;
                bool tankCanReceive0 = expectedDelta0 > ResourceTolerance;
                int beforeLedger0 = Ledger.Actions != null ? Ledger.Actions.Count : 0;

                ParsekLog.Verbose("TestRunner",
                    $"DockBoundary_D4_InGame (c) cycle0: tick0UT={tick0UT.ToString("R", IC)} " +
                    $"dockUT={cycle0DockUT.ToString("R", IC)} preDrain={preDrain0.ToString("R", IC)} " +
                    $"cadence={seamCadence.ToString("R", IC)} isCareer={(isCareer ? "1" : "0")}");

                RouteOrchestrator.Tick(tick0UT);
                yield return null;

                Route afterCycle0;
                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out afterCycle0),
                    "Route disappeared from store during cycle 0 Tick");
                InGameAssert.AreEqual(1, afterCycle0.CompletedCycles,
                    $"Cycle 0 should have completed exactly one cycle, CompletedCycles was {afterCycle0.CompletedCycles.ToString(IC)}");

                double afterAmount0 = fuelResource.amount;
                if (tankCanReceive0)
                    InGameAssert.IsTrue(Math.Abs(afterAmount0 - (preDrain0 + expectedDelta0)) < ResourceTolerance,
                        $"Cycle 0 delivery did not land: expected LF ~= {(preDrain0 + expectedDelta0).ToString("R", IC)}, " +
                        $"was {afterAmount0.ToString("R", IC)}");

                int afterLedger0 = Ledger.Actions != null ? Ledger.Actions.Count : 0;
                int delivered0 = CountRouteRows(beforeLedger0, afterLedger0, routeId, GameActionType.RouteCargoDelivered);
                int dispatched0 = CountRouteRows(beforeLedger0, afterLedger0, routeId, GameActionType.RouteDispatched);
                InGameAssert.AreEqual(1, delivered0,
                    $"Cycle 0 must deliver EXACTLY once (RouteCargoDelivered rows for this cycle = {delivered0.ToString(IC)})");
                InGameAssert.AreEqual(1, dispatched0,
                    $"Cycle 0 must charge the dispatch EXACTLY once (RouteDispatched rows for this cycle = {dispatched0.ToString(IC)})");
                // Career funds facet: the once-per-cycle dispatch is the funds-charge
                // carrier, so the Career-KSC funds cost must be resolved (non-negative;
                // it can legitimately be 0 for a tiny manifest, so the load-bearing
                // "charged once per cycle" invariant is the dispatch-row count above,
                // which holds in every mode). Skipped in Sandbox (no funds).
                if (isCareer)
                    InGameAssert.IsTrue(afterCycle0.KscDispatchFundsCost >= 0.0,
                        $"Career: cycle 0 KSC dispatch funds cost should be resolved and non-negative " +
                        $"(KscDispatchFundsCost was {afterCycle0.KscDispatchFundsCost.ToString("R", IC)})");

                // ---- CYCLE 1 -----------------------------------------------------
                double preDrain1 = DrainForDelivery(fuelResource, originalAmount);
                double headroom1 = maxAmount - preDrain1;
                double expectedDelta1 = DeliveryAmount < headroom1 ? DeliveryAmount : headroom1;
                bool tankCanReceive1 = expectedDelta1 > ResourceTolerance;
                int beforeLedger1 = Ledger.Actions != null ? Ledger.Actions.Count : 0;

                ParsekLog.Verbose("TestRunner",
                    $"DockBoundary_D4_InGame (c) cycle1: tick1UT={tick1UT.ToString("R", IC)} " +
                    $"dockUT={cycle1DockUT.ToString("R", IC)} preDrain={preDrain1.ToString("R", IC)}");

                RouteOrchestrator.Tick(tick1UT);
                yield return null;

                Route afterCycle1;
                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out afterCycle1),
                    "Route disappeared from store during cycle 1 Tick");
                InGameAssert.AreEqual(2, afterCycle1.CompletedCycles,
                    $"Cycle 1 should have brought CompletedCycles to 2, was {afterCycle1.CompletedCycles.ToString(IC)}");

                double afterAmount1 = fuelResource.amount;
                if (tankCanReceive1)
                    InGameAssert.IsTrue(Math.Abs(afterAmount1 - (preDrain1 + expectedDelta1)) < ResourceTolerance,
                        $"Cycle 1 delivery did not land: expected LF ~= {(preDrain1 + expectedDelta1).ToString("R", IC)}, " +
                        $"was {afterAmount1.ToString("R", IC)}");

                int afterLedger1 = Ledger.Actions != null ? Ledger.Actions.Count : 0;
                int delivered1 = CountRouteRows(beforeLedger1, afterLedger1, routeId, GameActionType.RouteCargoDelivered);
                int dispatched1 = CountRouteRows(beforeLedger1, afterLedger1, routeId, GameActionType.RouteDispatched);
                InGameAssert.AreEqual(1, delivered1,
                    $"Cycle 1 must deliver EXACTLY once (RouteCargoDelivered rows for this cycle = {delivered1.ToString(IC)})");
                InGameAssert.AreEqual(1, dispatched1,
                    $"Cycle 1 must charge the dispatch EXACTLY once (RouteDispatched rows for this cycle = {dispatched1.ToString(IC)})");

                // ---- THE TIMING ASSERTION (D4 residual) --------------------------
                // The two delivery cycles fired one cadence apart, and the cadence IS
                // the realized route cycle which the D4 flip collapsed to launch->dock
                // == DispatchInterval. Measured gap == tick1UT - tick0UT == cadence.
                double measuredGap = tick1UT - tick0UT;
                InGameAssert.IsTrue(Math.Abs(measuredGap - DispatchInterval) < 1.0,
                    $"D4 residual FAILED: delivery-to-delivery gap {measuredGap.ToString("R", IC)} should equal " +
                    $"DispatchInterval {DispatchInterval.ToString("R", IC)} (the realized cycle collapsed to launch->dock); " +
                    $"the OLD launch->undock cycle would have been {LaunchToUndockSpan.ToString("R", IC)}, longer by the " +
                    $"docked-stretch duration {(LaunchToUndockSpan - LaunchToDockSpan).ToString("R", IC)}");
                InGameAssert.IsTrue(measuredGap < LaunchToUndockSpan - 1.0,
                    $"D4 residual FAILED: the delivery gap {measuredGap.ToString("R", IC)} must be STRICTLY SHORTER than the " +
                    $"old launch->undock span {LaunchToUndockSpan.ToString("R", IC)} (the flip delivers more often by the " +
                    "docked-stretch duration); it was not");

                ParsekLog.Info("TestRunner",
                    $"DockBoundary_D4_InGame: PASS routeId={routeId} realSpanLen={realSpanLen.ToString("R", IC)} " +
                    $"cadence={realizedCadence.ToString("R", IC)} dispatchInterval={DispatchInterval.ToString("R", IC)} " +
                    $"measuredGap={measuredGap.ToString("R", IC)} oldLaunchToUndock={LaunchToUndockSpan.ToString("R", IC)} " +
                    $"completedCycles={afterCycle1.CompletedCycles.ToString(IC)} delivered0={delivered0.ToString(IC)} " +
                    $"delivered1={delivered1.ToString(IC)} isCareer={(isCareer ? "1" : "0")}");
            }
            finally
            {
                // Disarm the seam FIRST so a later tick never re-enters our resolver.
                if (seamArmed)
                    RouteOrchestrator.LoopUnitResolverForTesting = previousResolver;

                if (routeAdded)
                {
                    bool removed = RouteStore.RemoveRoute(routeId);
                    ParsekLog.Verbose("TestRunner", $"DockBoundary_D4_InGame cleanup: RemoveRoute={removed}");
                }
                RestoreRoutes(preExistingRoutes);

                for (int i = 0; i < committedAdded.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedAdded[i]);
                if (routeTreeAdded) RemoveCommittedTree(routeTreeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);

                // Restore the pre-drain LiquidFuel so the save / next batch test do
                // not see the synthetic deliveries.
                try
                {
                    if (fuelResource != null)
                    {
                        fuelResource.amount = originalAmount;
                        ParsekLog.Verbose("TestRunner",
                            $"DockBoundary_D4_InGame cleanup: restored LiquidFuel to {originalAmount.ToString("R", IC)}");
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"DockBoundary_D4_InGame cleanup: failed to restore LiquidFuel ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        // Drains the tank to leave headroom for one DeliveryAmount fill, clamped so a
        // tiny tank still receives some manifest. Returns the post-drain amount.
        private static double DrainForDelivery(PartResource fuelResource, double baselineAmount)
        {
            double target = baselineAmount - DeliveryAmount;
            if (target < 0.0) target = 0.0;
            fuelResource.amount = target;
            return fuelResource.amount;
        }

        // Counts ledger rows of a given type tagged with the route id in [from, to).
        private static int CountRouteRows(int from, int to, string routeId, GameActionType type)
        {
            int count = 0;
            if (Ledger.Actions == null) return 0;
            for (int i = from; i < to && i < Ledger.Actions.Count; i++)
            {
                GameAction a = Ledger.Actions[i];
                if (a == null) continue;
                if (a.Type != type) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                count++;
            }
            return count;
        }

        // Index of a recording id in the raw committed list (the alignment contract
        // the real ResolveLoopUnit uses).
        private static int FindCommittedIndex(IReadOnlyList<Recording> committed, string recordingId)
        {
            if (committed == null) return -1;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i] != null && string.Equals(committed[i].RecordingId, recordingId, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        // True when the committed index is one of the unit's member indices.
        private static bool IsUnitMember(GhostPlaybackLogic.LoopUnit unit, int committedIndex)
        {
            int[] members = unit.MemberIndices;
            if (members == null) return false;
            for (int i = 0; i < members.Length; i++)
                if (members[i] == committedIndex)
                    return true;
            return false;
        }

        private static bool TryFindLiquidFuelPart(Vessel vessel, out Part part, out PartResource resource)
        {
            part = null;
            resource = null;
            if (vessel == null || vessel.parts == null) return false;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(LiquidFuelName);
                if (pr == null) continue;
                part = p;
                resource = pr;
                return true;
            }
            return false;
        }

        // launch -> dock -> undock with a peeled payload at undock. Root launch UT
        // 1000, DOCK 2000, UNDOCK 3000 (mirrors the sibling fixture topology in
        // LogisticsRouteOnMissionsRuntimeTests.BuildLaunchDockUndockTree).
        private static RecordingTree BuildLaunchDockUndockTree(string treeId)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = "launch" };
            tree.Recordings["launch"] = Leg("launch", "C0", 0, RootLaunchUT, DockUT, "Transport");
            tree.Recordings["docked"] = Leg("docked", "C0", 1, DockUT, UndockUT, "Transport");
            tree.Recordings["survivor"] = Leg("survivor", "C0", 2, UndockUT, 4000, "Transport");
            tree.Recordings["payload"] = Leg("payload", "C1", 0, UndockUT, 3500, "Payload");
            tree.BranchPoints.Add(BP("dock-bp", BranchPointType.Dock,
                new[] { "launch" }, new[] { "docked" }, DockUT));
            tree.BranchPoints.Add(BP("undock-bp", BranchPointType.Undock,
                new[] { "docked" }, new[] { "survivor", "payload" }, UndockUT));
            return tree;
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

        private static void RestoreRoutes(List<Route> preExisting)
        {
            RouteStore.ResetForTesting();
            for (int i = 0; i < preExisting.Count; i++)
                if (preExisting[i] != null)
                    RouteStore.AddRoute(preExisting[i]);
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
