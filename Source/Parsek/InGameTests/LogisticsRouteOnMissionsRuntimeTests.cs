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
    /// Phase 5 (Checkpoint C) end-to-end stock-runtime check for the
    /// route-on-Missions render + fire path. Under live KSP statics it asserts the
    /// whole seam composes:
    /// <list type="number">
    ///   <item>a ghost-driving route is SELECTED by
    ///   <see cref="RouteGhostDriverSelector"/>;</item>
    ///   <item>its backing Mission, fed through the UNCHANGED
    ///   <c>MissionLoopUnitBuilder.Build</c>, yields exactly one loop unit whose
    ///   member window is <c>[launch .. undock]</c> (the post-undock tail trimmed);</item>
    ///   <item>the <see cref="RouteLoopClock"/> reports a crossing whose loopUT
    ///   sweeps across the recorded dock UT (delivery fires there);</item>
    ///   <item>the mutual-exclusion guard reports the route's tree bound (so both
    ///   manual-loop surfaces grey);</item>
    ///   <item>a manual loop on a DIFFERENT tree renders in parallel (disjoint
    ///   owners).</item>
    /// </list>
    /// A precondition gate (cheap reflection check that the union seam + the
    /// loop-clock fire switch exist) runs first so a red result is attributable to
    /// THIS workstream, not an incomplete upstream phase. The xUnit suite covers
    /// the pure math; this test pins that the production statics + the locked
    /// builder compose correctly at runtime.
    /// </summary>
    public sealed class LogisticsRouteOnMissionsRuntimeTests
    {
        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            BatchSkipReason = "Mutates RouteStore + the committed-tree list under live KSP statics; runs out of band so a parallel batch test cannot observe partial state.",
            Description = "A ghost-driving route is selected, its backing mission yields one loop unit trimmed to [launch..undock], the loop clock crosses the recorded dock UT, mutual exclusion binds the route tree, and a manual loop on another tree renders in parallel (disjoint owners)")]
        public void RouteOnMissions_RendersTrimmedLoop_FiresAtDock_AndMutuallyExcludes()
        {
            // ---- PRECONDITION GATE -------------------------------------------
            // The union seam (RouteGhostDriverSelector + MissionLoopUnitBuilder.Build)
            // and the loop-clock fire switch (RouteLoopClock.TryGetRouteLoopState)
            // must exist. A failure here means an upstream phase is incomplete, not
            // that this end-to-end behavior regressed.
            MethodInfo selectMethod = typeof(RouteGhostDriverSelector).GetMethod(
                "SelectGhostDrivingBackingMissions",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (selectMethod == null)
                InGameAssert.Skip("PRECONDITION: RouteGhostDriverSelector.SelectGhostDrivingBackingMissions missing (upstream Phase 3 incomplete)");
            MethodInfo clockMethod = typeof(RouteLoopClock).GetMethod(
                "TryGetRouteLoopState",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (clockMethod == null)
                InGameAssert.Skip("PRECONDITION: RouteLoopClock.TryGetRouteLoopState missing (upstream Phase 4 incomplete)");
            MethodInfo buildMethod = typeof(MissionLoopUnitBuilder).GetMethod(
                "Build",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (buildMethod == null)
                InGameAssert.Skip("PRECONDITION: MissionLoopUnitBuilder.Build missing (locked Missions seam absent)");

            string routeTreeId = "ingame-rom-route-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string manualTreeId = "ingame-rom-manual-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-rom-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Snapshot routes for restore.
            var preExistingRoutes = new List<Route>();
            IReadOnlyList<Route> committedRoutes = RouteStore.CommittedRoutes;
            for (int i = 0; i < committedRoutes.Count; i++)
                if (committedRoutes[i] != null)
                    preExistingRoutes.Add(committedRoutes[i]);

            // Build a launch -> dock -> undock tree (root launch UT 1000, undock 3000).
            RecordingTree routeTree = BuildLaunchDockUndockTree(routeTreeId);
            // A simple 2-leg manual tree on a different id.
            RecordingTree manualTree = BuildLinearTree(manualTreeId);

            bool routeTreeAdded = false, manualTreeAdded = false, routeAdded = false;
            var committed = new List<Recording>();

            try
            {
                RecordingStore.AddCommittedTreeForTesting(routeTree);
                routeTreeAdded = true;
                RecordingStore.AddCommittedTreeForTesting(manualTree);
                manualTreeAdded = true;

                committed.AddRange(routeTree.Recordings.Values);
                committed.AddRange(manualTree.Recordings.Values);

                int idxLaunch = committed.FindIndex(r => r.RecordingId == "launch");
                int idxSurvivor = committed.FindIndex(r => r.RecordingId == "survivor");
                int idxManualA = committed.FindIndex(r => r.RecordingId == "m-a");

                // Derive the route's backing-mission definition exactly as RouteBuilder
                // would, and commit an Active route bound to the tree.
                HashSet<string> excluded = RouteBackingMission.ComputeExcludedIntervalKeys(
                    routeTree, undockUT: 3000.0, launchUT: 1000.0);
                var route = new Route
                {
                    Id = routeId,
                    Name = "Parsek Route-on-Missions In-Game",
                    BackingMissionTreeId = routeTreeId,
                    ExcludedIntervalKeys = excluded,
                    RecordedDockUT = 2500.0,            // within [1000,3000]
                    DockMemberRecordingId = "docked",
                    LoopAnchorUT = 1000.0,
                    LastObservedLoopCycleIndex = -1,
                    TransitDuration = 2000.0,
                    DispatchInterval = 2000.0,          // == span -> cadence == interval
                    Status = RouteStatus.Active,
                    RecordingIds = new List<string> { "launch", "docked" },
                    SourceRefs = new List<RouteSourceRef>
                    {
                        new RouteSourceRef { RecordingId = "launch", TreeId = routeTreeId },
                        new RouteSourceRef { RecordingId = "docked", TreeId = routeTreeId }
                    },
                    Stops = new List<RouteStop> { new RouteStop() }
                };
                RouteStore.AddRoute(route);
                routeAdded = RouteStore.TryGetRoute(routeId, out _);
                InGameAssert.IsTrue(routeAdded, "Route was not stored");

                // (1) The route is SELECTED as ghost-driving.
                double ut = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 100000.0;
                IReadOnlyList<Mission> routeMissions =
                    RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(
                        RouteStore.CommittedRoutes, ut);
                Mission routeMission = null;
                for (int i = 0; i < routeMissions.Count; i++)
                    if (routeMissions[i] != null && routeMissions[i].TreeId == routeTreeId)
                        routeMission = routeMissions[i];
                InGameAssert.IsNotNull(routeMission,
                    "Active route was not selected as a ghost-driving backing mission");

                // A parallel manual loop on the OTHER tree.
                var manualMission = new Mission("ingame-rom-manual-m", manualTreeId, "Manual Loop")
                {
                    LoopPlayback = true,
                    LoopTimeUnit = LoopTimeUnit.Sec,
                    LoopIntervalSeconds = 600.0
                };

                // (2) Union through the UNCHANGED builder -> two units, route trimmed.
                var unioned = new List<Mission> { manualMission, routeMission };
                GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                    unioned, new[] { routeTree, manualTree }, committed, 30.0);

                InGameAssert.IsTrue(set.Count == 2,
                    "Expected exactly two loop units (route + manual), got " + set.Count);
                InGameAssert.IsTrue(set.IsMember(idxLaunch),
                    "Route launch leg should be a loop-unit member");
                InGameAssert.IsFalse(set.IsMember(idxSurvivor),
                    "Post-undock survivor must be trimmed from the route loop unit");

                // (5) Disjoint owners: the route's member and the manual loop's
                // member map to different owners (single-owner contract preserved).
                int routeOwner, manualOwner;
                InGameAssert.IsTrue(set.OwnerByIndex.TryGetValue(idxLaunch, out routeOwner),
                    "Route launch index has no owner");
                InGameAssert.IsTrue(set.OwnerByIndex.TryGetValue(idxManualA, out manualOwner),
                    "Manual loop index has no owner");
                InGameAssert.IsTrue(routeOwner != manualOwner,
                    "Route and manual loop must have disjoint owners (cross-tree parallel render)");

                // (3) The loop clock crosses the recorded dock UT during the span.
                // Resolve the route's unit and sweep loopUT across [spanStart, dock].
                GhostPlaybackLogic.LoopUnit routeUnit;
                InGameAssert.IsTrue(set.TryGetUnitForMember(idxLaunch, out routeUnit),
                    "Could not resolve the route's loop unit");

                bool sweptDock = false;
                // Sample two UTs straddling the dock phase (anchor floored to spanEnd
                // -> first cycle wraps spanStart). Just before and after the dock.
                double dockPhase = 2500.0; // RecordedDockUT
                double spanStart = routeUnit.SpanStartUT;
                double anchor = routeUnit.PhaseAnchorUT;
                // loopUT == spanStart + ((ut - anchor) mod cadence). Choose ut so the
                // phase lands just past the dock to prove the dock UT is reachable.
                double cadence = routeUnit.SpanEndUT - routeUnit.SpanStartUT;
                double utAtDock = anchor + (dockPhase - spanStart);
                double loopUT; long cycleIdx; bool tail;
                if (RouteLoopClock.TryGetRouteLoopState(routeUnit, utAtDock,
                        out loopUT, out cycleIdx, out tail))
                {
                    // The resolved loopUT should land at/near the dock phase.
                    if (!tail && Math.Abs(loopUT - dockPhase) < 1.0)
                        sweptDock = true;
                }
                InGameAssert.IsTrue(sweptDock,
                    "Loop clock did not resolve a loopUT at the recorded dock UT (delivery would never fire)");

                // (4) Mutual exclusion: the route's tree is reported bound.
                InGameAssert.IsTrue(RouteTreeGuard.IsTreeBoundToActiveRoute(routeTreeId),
                    "Route-bound tree was not reported bound (manual-loop surfaces would not grey)");
                InGameAssert.IsFalse(RouteTreeGuard.IsTreeBoundToActiveRoute(manualTreeId),
                    "Non-route tree was incorrectly reported bound");

                ParsekLog.Info("TestRunner",
                    $"RouteOnMissions_InGame: PASS routeTree={routeTreeId} manualTree={manualTreeId} " +
                    $"units={set.Count} cadence={cadence.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            finally
            {
                if (routeAdded)
                    RouteStore.RemoveRoute(routeId);
                RestoreRoutes(preExistingRoutes);
                if (routeTreeAdded) RemoveCommittedTree(routeTreeId);
                if (manualTreeAdded) RemoveCommittedTree(manualTreeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);
            }
        }

        /// <summary>
        /// LST-4 — the missing INTEGRATED end-to-end check for the loop-clock fire
        /// delivery path. Drives the production <see cref="RouteOrchestrator.Tick"/>
        /// through the LOOP-ROUTE branch (<c>IsLoopRoute</c> true) -> a confirmed
        /// <see cref="RouteLoopClock"/> crossing -> <see cref="RouteOrchestrator.EmitLoopCycle"/>
        /// -> the LIVE <c>ApplyDelivery</c> writer, and asserts that the destination
        /// vessel's <c>LiquidFuel</c> tank actually INCREASED, a
        /// <see cref="GameActionType.RouteCargoDelivered"/> ledger row was appended
        /// for the route, and the dispatch-debit pair (<see cref="GameActionType.RouteDispatched"/>)
        /// landed in the same fire.
        ///
        /// <para>This is distinct from <see cref="LogisticsDeliveryRuntimeTests"/>,
        /// which drives the LEGACY <c>PendingDeliveryUT</c> pre-evaluator hook on a
        /// non-loop route. Here the route has a backing-mission tree (so
        /// <c>IsLoopRoute</c> is true and the legacy state machine is NEVER reached);
        /// the fire is owned by the span-clock crossing detector. To make the crossing
        /// deterministic under live statics we inject a real
        /// <see cref="GhostPlaybackLogic.LoopUnit"/> via the production
        /// <c>LoopUnitResolverForTesting</c> seam (span <c>[1000,3000]</c>, cadence ==
        /// span, anchor == spanStart, dock UT 2000 inside the span) and tick at
        /// <c>currentUT = 2000</c> so the clock reports cycleIndex 0, not parked in the
        /// inter-cycle tail, with the dock UT in span -> crossing. The DELIVERY half is
        /// left on the LIVE path (<c>DeliveryApplierForTesting</c> stays null) so the
        /// real tank fill + ledger rows are exercised, NOT a fake.</para>
        ///
        /// <para>Eligibility is real: the source tree is committed so ERS carries the
        /// route's <c>SourceRefs</c>, the stop endpoint is the active vessel's pid, and
        /// the KSC origin means origin-cargo passes (funds gate is Career-only and
        /// skipped in Sandbox). Teardown clears the resolver seam, restores the tank,
        /// removes the route + committed tree, and restores the route store.</para>
        /// </summary>
        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            BatchSkipReason = "Mutates RouteStore + Ledger + RecordingStore committed trees + the RouteOrchestrator.LoopUnitResolverForTesting seam + live PartResource.amount under live KSP statics; runs out of band so a parallel batch test cannot observe partial state or the seam window.",
            Description = "A loop-route crossing through RouteOrchestrator.Tick (IsLoopRoute true) fires EmitLoopCycle -> live ApplyDelivery: the destination LiquidFuel tank increases by the manifest amount and a RouteCargoDelivered (+ RouteDispatched) ledger row is emitted")]
        public IEnumerator LoopFire_RendersAndDelivers_AtDockCrossing()
        {
            // PRECONDITION GATE -------------------------------------------------
            // The loop-fire path + its test seams must exist; a failure here means
            // an upstream phase is incomplete, not that this behavior regressed.
            FieldInfo resolverField = typeof(RouteOrchestrator).GetField(
                "LoopUnitResolverForTesting",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (resolverField == null)
                InGameAssert.Skip("PRECONDITION: RouteOrchestrator.LoopUnitResolverForTesting seam missing (upstream Phase 4 incomplete)");
            MethodInfo emitMethod = typeof(RouteOrchestrator).GetMethod(
                "EmitLoopCycle",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (emitMethod == null)
                InGameAssert.Skip("PRECONDITION: RouteOrchestrator.EmitLoopCycle missing (upstream Phase 4 incomplete)");

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live vessel to deliver onto");

            Vessel activeVessel = FlightGlobals.ActiveVessel;

            Part fuelPart;
            PartResource fuelResource;
            if (!TryFindLiquidFuelPart(activeVessel, out fuelPart, out fuelResource))
                InGameAssert.Skip(
                    $"Active vessel '{activeVessel.vesselName}' has no part with a LiquidFuel resource; " +
                    "skipping — pick a vessel with at least one LF tank to run this test");

            // PRE-DRAIN so there is headroom for the synthetic delivery. Same shape
            // as LogisticsDeliveryRuntimeTests: cap the drain so a tiny tank still
            // receives at least some of the manifest, and clamp the expected fill to
            // the actual headroom.
            double originalAmount = fuelResource.amount;
            double maxAmount = fuelResource.maxAmount;
            double preDrainTarget = originalAmount - LoopDeliveryAmount;
            if (preDrainTarget < 0.0) preDrainTarget = 0.0;
            fuelResource.amount = preDrainTarget;
            double postDrainAmount = fuelResource.amount;
            double headroom = maxAmount - postDrainAmount;
            double expectedDelta = LoopDeliveryAmount < headroom ? LoopDeliveryAmount : headroom;
            double expectedAmount = postDrainAmount + expectedDelta;

            // Skip the live-tank assertion if the tank physically cannot hold the
            // manifest delta (degenerate capacity); the route-state + ledger
            // assertions still run.
            bool tankCanReceiveDelta = expectedDelta > LoopResourceTolerance;

            string routeTreeId = "ingame-loopfire-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-loopfire-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Deterministic span clock (independent of live UT): span [1000,3000],
            // cadence == span, anchor == spanStart, dock UT 2000, tick at 2000 ->
            // cycleIndex 0, !tail, dock in span -> crossing on the first tick
            // (lastObservedLoopCycleIndex starts at -1).
            const double SpanStartUT = 1000.0;
            const double SpanEndUT = 3000.0;
            const double DockUT = 2000.0;
            const double Cadence = SpanEndUT - SpanStartUT;
            const double TickUT = 2000.0;

            // Snapshot routes for restore.
            var preExistingRoutes = new List<Route>();
            IReadOnlyList<Route> committedRoutes = RouteStore.CommittedRoutes;
            for (int i = 0; i < committedRoutes.Count; i++)
                if (committedRoutes[i] != null)
                    preExistingRoutes.Add(committedRoutes[i]);

            int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

            // Source tree committed so ERS carries the route's SourceRefs (the
            // RouteHasValidSourcesInErs eligibility gate is real). The leaf member
            // ("docked") carries the delivery binding; the route SourceRefs point at
            // committed member ids so the ERS membership check passes.
            RecordingTree routeTree = BuildLaunchDockUndockTree(routeTreeId);

            // Build the loop unit directly (owner index / member indices are not read
            // by the fire path — only the span-clock fields are). The committed-index
            // values are arbitrary here because production resolves the unit through
            // the seam, not through the live builder.
            GhostPlaybackLogic.LoopUnit loopUnit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0,
                memberIndices: new[] { 0 },
                spanStartUT: SpanStartUT,
                spanEndUT: SpanEndUT,
                cadenceSeconds: Cadence,
                phaseAnchorUT: SpanStartUT);

            bool routeTreeAdded = false, routeAdded = false, seamArmed = false;
            var previousResolver = RouteOrchestrator.LoopUnitResolverForTesting;
            // Recordings we push into CommittedRecordings so ERS resolves the route's
            // SourceRefs (the RouteHasValidSourcesInErs eligibility gate is real, and
            // ComputeERS reads RecordingStore.CommittedRecordings). Removed in finally.
            var committedAdded = new List<Recording>();

            try
            {
                RecordingStore.AddCommittedTreeForTesting(routeTree);
                routeTreeAdded = true;
                // AddCommittedTreeForTesting only registers the tree, NOT its
                // recordings, so push the route's member recordings into
                // CommittedRecordings explicitly so ERS sees "launch" + "docked".
                foreach (Recording rec in routeTree.Recordings.Values)
                {
                    if (rec == null) continue;
                    RecordingStore.AddCommittedInternal(rec);
                    committedAdded.Add(rec);
                }

                // Active KSC-origin loop route: IsLoopRoute is true (backing tree set),
                // status Active (ghost-driving), dock UT inside the span, delivery
                // manifest onto the active vessel.
                var route = new Route
                {
                    Id = routeId,
                    Name = "Parsek Loop-Fire In-Game",
                    Status = RouteStatus.Active,
                    IsKscOrigin = true,
                    BackingMissionTreeId = routeTreeId,
                    ExcludedIntervalKeys = new HashSet<string>(),
                    RecordedDockUT = DockUT,
                    DockMemberRecordingId = "docked",
                    LoopAnchorUT = SpanStartUT,
                    LastObservedLoopCycleIndex = -1,
                    TransitDuration = Cadence,
                    DispatchInterval = Cadence,
                    NextDispatchUT = TickUT + Cadence,
                    CompletedCycles = 0,
                    SkippedCycles = 0,
                    KscDispatchFundsCost = 0.0,
                    CostManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        { LiquidFuelName, LoopDeliveryAmount },
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
                                { LiquidFuelName, LoopDeliveryAmount },
                            },
                            InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                            SegmentIndexBefore = 0,
                            DeliveryOffsetSeconds = 0.0,
                        },
                    },
                };
                RouteStore.AddRoute(route);
                routeAdded = RouteStore.TryGetRoute(routeId, out _);
                InGameAssert.IsTrue(routeAdded, "Loop route was not stored");

                // Arm the loop-unit resolver seam so the production ResolveLoopUnit
                // returns our deterministic span-clock unit ONLY for this route id.
                // Leaving DeliveryApplierForTesting null keeps the LIVE ApplyDelivery
                // (real tank fill + ledger rows) on the delivery half.
                RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) =>
                {
                    if (r != null && string.Equals(r.Id, routeId, StringComparison.Ordinal))
                        return loopUnit;
                    return previousResolver != null ? previousResolver(r, ut) : (GhostPlaybackLogic.LoopUnit?)null;
                };
                seamArmed = true;

                ParsekLog.Verbose("TestRunner",
                    $"LoopFire_InGame: pre-tick routeId={routeId} treeId={routeTreeId} " +
                    $"vessel={activeVessel.vesselName} pid={activeVessel.persistentId.ToString(IC)} " +
                    $"postDrain={postDrainAmount.ToString("R", IC)} max={maxAmount.ToString("R", IC)} " +
                    $"expected={expectedAmount.ToString("R", IC)} tickUT={TickUT.ToString("R", IC)} " +
                    $"dockUT={DockUT.ToString("R", IC)} beforeLedger={beforeLedgerCount.ToString(IC)}");

                // ACT — production no-env tick through the loop-route branch. The
                // crossing detector fires EmitLoopCycle -> EmitDispatchDebit +
                // live ApplyDelivery in this single tick.
                RouteOrchestrator.Tick(TickUT);

                // Settle a frame in case any deferred behavior lands next FixedUpdate.
                yield return null;

                // ASSERTIONS ---------------------------------------------------

                // 1. Route state: a fired loop cycle bumps CompletedCycles and snaps
                //    LastObservedLoopCycleIndex forward to the crossed cycle (0),
                //    while staying Active (a ghost-driving loop route self-transitions).
                Route postTick;
                InGameAssert.IsTrue(
                    RouteStore.TryGetRoute(routeId, out postTick),
                    "Loop route disappeared from store during Tick");
                InGameAssert.IsNotNull(postTick, "TryGetRoute true but post-tick route null");
                InGameAssert.AreEqual(RouteStatus.Active, postTick.Status,
                    $"Expected loop route to stay Active after fire, but was {postTick.Status}");
                InGameAssert.AreEqual(1, postTick.CompletedCycles,
                    $"Expected CompletedCycles=1 after loop fire, but was {postTick.CompletedCycles.ToString(IC)}");
                InGameAssert.AreEqual(0L, postTick.LastObservedLoopCycleIndex,
                    $"Expected LastObservedLoopCycleIndex snapped to 0 after crossing, but was {postTick.LastObservedLoopCycleIndex.ToString(IC)}");

                // 2. LIVE resource: the destination tank's LiquidFuel INCREASED by the
                //    manifest amount (within tolerance). The applier writes pr.amount
                //    directly on the loaded path, so re-reading the same PartResource
                //    is the correct probe. Skip the live-tank assertions only when the
                //    tank physically cannot hold the manifest delta (degenerate full /
                //    tiny tank) — the route-state + ledger assertions still ran above.
                double actualAmount = fuelResource.amount;
                if (tankCanReceiveDelta)
                {
                    InGameAssert.IsTrue(actualAmount > postDrainAmount + LoopResourceTolerance,
                        $"LiquidFuel did not INCREASE after loop fire: postDrain={postDrainAmount.ToString("R", IC)} " +
                        $"actual={actualAmount.ToString("R", IC)} (the live delivery did not land)");
                    double diff = Math.Abs(actualAmount - expectedAmount);
                    InGameAssert.IsTrue(diff < LoopResourceTolerance,
                        $"Expected LiquidFuel ~= {expectedAmount.ToString("R", IC)} on part " +
                        $"'{(fuelPart.partInfo != null ? fuelPart.partInfo.name : "<unknown>")}' after loop fire, " +
                        $"but was {actualAmount.ToString("R", IC)} (diff={diff.ToString("R", IC)} tol={LoopResourceTolerance.ToString("R", IC)})");
                }
                else
                {
                    ParsekLog.Info("TestRunner",
                        $"LoopFire_InGame: tank cannot hold delta (expectedDelta={expectedDelta.ToString("R", IC)} " +
                        $"<= tol); skipping live-tank assertion, route-state + ledger checks still ran");
                }

                // 3. Ledger rows: the full cycle emitted RouteDispatched (dispatch-debit
                //    half) AND RouteCargoDelivered (delivery half) for our route id.
                int afterLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;
                bool dispatchedFound = false, deliveredFound = false;
                if (Ledger.Actions != null)
                {
                    for (int i = beforeLedgerCount; i < afterLedgerCount; i++)
                    {
                        GameAction a = Ledger.Actions[i];
                        if (a == null) continue;
                        if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                        if (a.Type == GameActionType.RouteDispatched) dispatchedFound = true;
                        else if (a.Type == GameActionType.RouteCargoDelivered) deliveredFound = true;
                    }
                }
                InGameAssert.IsTrue(deliveredFound,
                    $"No RouteCargoDelivered ledger row for loop routeId={routeId} " +
                    $"(beforeCount={beforeLedgerCount.ToString(IC)} afterCount={afterLedgerCount.ToString(IC)}); " +
                    "EmitLoopCycle -> ApplyDelivery should have emitted one.");
                InGameAssert.IsTrue(dispatchedFound,
                    $"No RouteDispatched ledger row for loop routeId={routeId}; " +
                    "EmitLoopCycle -> EmitDispatchDebit should have emitted the dispatch-debit half.");

                ParsekLog.Info("TestRunner",
                    $"LoopFire_InGame: PASS routeId={routeId} status={postTick.Status} " +
                    $"completedCycles={postTick.CompletedCycles.ToString(IC)} " +
                    $"lastObserved={postTick.LastObservedLoopCycleIndex.ToString(IC)} " +
                    $"fuelBefore={postDrainAmount.ToString("R", IC)} fuelAfter={fuelResource.amount.ToString("R", IC)} " +
                    $"newLedgerRows={(afterLedgerCount - beforeLedgerCount).ToString(IC)}");
            }
            finally
            {
                // Disarm the seam FIRST so a later tick never re-enters our resolver.
                if (seamArmed)
                    RouteOrchestrator.LoopUnitResolverForTesting = previousResolver;

                if (routeAdded)
                {
                    bool removed = RouteStore.RemoveRoute(routeId);
                    ParsekLog.Verbose("TestRunner", $"LoopFire_InGame cleanup: RemoveRoute={removed}");
                }
                RestoreRoutes(preExistingRoutes);
                // Remove the recordings we pushed into CommittedRecordings, then the tree.
                for (int i = 0; i < committedAdded.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedAdded[i]);
                if (routeTreeAdded) RemoveCommittedTree(routeTreeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);

                // Restore the pre-drain LiquidFuel so the save / next batch test do
                // not see the synthetic delivery.
                try
                {
                    if (fuelResource != null)
                    {
                        fuelResource.amount = originalAmount;
                        ParsekLog.Verbose("TestRunner",
                            $"LoopFire_InGame cleanup: restored LiquidFuel to {originalAmount.ToString("R", IC)}");
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"LoopFire_InGame cleanup: failed to restore LiquidFuel ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }

        private const string LiquidFuelName = "LiquidFuel";
        private const double LoopDeliveryAmount = 5.0;
        private const double LoopResourceTolerance = 0.01;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Finds the first <see cref="Part"/> on <paramref name="vessel"/> that
        /// carries a <c>LiquidFuel</c> resource entry. Mirrors the order the
        /// delivery applier walks (vessel.parts ascending) so the pre-drain / restore
        /// + the applier's fill all land on the same tank. Local to this file (the
        /// shared test generators are off-limits to this workstream).
        /// </summary>
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
        // 1000, undock 3000 (mirrors the xUnit topology).
        private static RecordingTree BuildLaunchDockUndockTree(string treeId)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = "launch" };
            tree.Recordings["launch"] = Leg("launch", "C0", 0, 1000, 2000, "Transport");
            tree.Recordings["docked"] = Leg("docked", "C0", 1, 2000, 3000, "Transport");
            tree.Recordings["survivor"] = Leg("survivor", "C0", 2, 3000, 4000, "Transport");
            tree.Recordings["payload"] = Leg("payload", "C1", 0, 3000, 3500, "Payload");
            tree.BranchPoints.Add(BP("dock-bp", BranchPointType.Dock,
                new[] { "launch" }, new[] { "docked" }, 2000));
            tree.BranchPoints.Add(BP("undock-bp", BranchPointType.Undock,
                new[] { "docked" }, new[] { "survivor", "payload" }, 3000));
            return tree;
        }

        private static RecordingTree BuildLinearTree(string treeId)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = "m-a" };
            tree.Recordings["m-a"] = Leg("m-a", "M", 0, 5000, 5100, "Manual");
            tree.Recordings["m-b"] = Leg("m-b", "M", 1, 5100, 5200, "Manual");
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
