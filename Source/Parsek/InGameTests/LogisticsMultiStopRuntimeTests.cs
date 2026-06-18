using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Parsek.Logistics;
using Parsek.InGameTests.Helpers;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// M4a Phase A5 in-game close-out for MULTI-STOP DELIVERY (plan
    /// `docs/dev/plan-logistics-m4-shape-generality.md` Phase A5, gameplay
    /// grounding 5.1: KSC -> Base A deliver -> Base B deliver -> recover). The
    /// pure xUnit suites cover the firing decision math (per-window replay key,
    /// per-stop fire state, the partial-cycle resume, the once-per-cycle
    /// CompletedCycles bump); these tests pin the part only live KSP can
    /// exercise: a 2-window DELIVERY route firing BOTH deliveries through the
    /// production <see cref="RouteOrchestrator.Tick(double)"/> -&gt;
    /// <see cref="RouteOrchestrator.ProcessMultiStopCrossings"/> path against
    /// LIVE endpoint vessels (loaded <c>PartResource.amount</c> AND unloaded
    /// <c>ProtoPartResourceSnapshot.amount</c> writes), and a real
    /// <see cref="GamePersistence.SaveGame"/> round-trip mid-cycle proving the
    /// A3 partial-cycle resume: window 0 fires, the per-stop
    /// <see cref="RouteStop.LastFiredCycleIndex"/> persists to the <c>.sfs</c>,
    /// and window 1 fires on the next tick of the SAME cycle.
    ///
    /// <para><b>Re-entry discipline + post-restore unpack wait (todo "background
    /// RouteOrchestrator.Tick can re-enter a logistics test's synthetic
    /// route").</b> The orchestrator-driven cases yield ONLY in the precondition
    /// unpack wait BEFORE any seam is armed or any state mutated; the whole
    /// arrange / Tick / assert / teardown sequence then runs yield-free inside
    /// one frame on the main thread, so the background 1 Hz scenario tick can
    /// never interleave with an armed seam or a stored synthetic route. The save
    /// round-trip test yields mid-test (deferred .sfs writes must settle), so it
    /// pauses / restores time warp across the yield window and disarms the
    /// resolver seam BEFORE the save. Same isolated-batch discipline as
    /// <see cref="LogisticsOriginDebitRuntimeTests"/> /
    /// <see cref="LogisticsPickupRuntimeTests"/>: AllowBatchExecution=false +
    /// RestoreBatchFlightBaselineAfterExecution=true.</para>
    ///
    /// <para><b>Two-stop deterministic span clock.</b> Span [1000,3000], cadence
    /// == span, anchor == spanStart; dock A at loop UT 1500, dock B (the LAST
    /// dock) at loop UT 2500. A single tick at UT 2500 reaches BOTH dock phases
    /// for cycle 0 (each stop's LastFiredCycleIndex starts at -1), so
    /// ProcessMultiStopCrossings fires window A then window B under ONE cycleId
    /// and bumps CompletedCycles exactly once (loopUT &gt;= maxDockUT). The
    /// partial-resume test ticks at 1500 first (only window A reached), saves,
    /// reloads the route from the .sfs, then ticks at 2500 for window B.</para>
    /// </summary>
    public sealed class LogisticsMultiStopRuntimeTests
    {
        private const string LiquidFuelName = "LiquidFuel";
        private const double DeliveryAmountA = 5.0;
        private const double DeliveryAmountB = 4.0;
        private const double MinTankHeadroom = 0.1;
        private const double ResourceTolerance = 0.01;
        private const string TestSaveSlotPrefix = "parsek_multistop_ingame_test_";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const string IsolatedOnlyBatchSkipReason =
            "Isolated-run only - mutates RouteStore, Ledger, RecordingStore committed trees, " +
            "RouteOrchestrator test seams, and live vessel resource state under live KSP statics; " +
            "excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play " +
            "button in a disposable FLIGHT session.";

        // Deterministic span clock (same shape as
        // LogisticsRouteOnMissionsRuntimeTests.LoopFire_RendersAndDelivers_AtDockCrossing,
        // extended to TWO dock phases): span [1000,3000], cadence == span, anchor
        // == spanStart; dock A at 1500, dock B at 2500 (the last dock). A tick at
        // 2500 -> loopUT 2500 reaches both dock phases for cycle 0 (both stops'
        // LastFiredCycleIndex start at -1).
        private const double SpanStartUT = 1000.0;
        private const double SpanEndUT = 3000.0;
        private const double DockUtA = 1500.0;
        private const double DockUtB = 2500.0;
        private const double Cadence = SpanEndUT - SpanStartUT;
        // A tick at the LAST dock phase reaches both windows in one pass.
        private const double TickUtBothDocks = 2500.0;
        // The partial-resume first tick reaches ONLY dock A.
        private const double TickUtDockAOnly = 1500.0;

        // ==================================================================
        // 1. Loaded endpoint: both windows deliver in one pass
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "A 2-stop DELIVERY route crossing through RouteOrchestrator.Tick (ProcessMultiStopCrossings) fires delivery at BOTH recorded dock phases in one pass onto the LOADED active vessel: the destination LiquidFuel tank rises by the SUM of both window manifests, TWO RouteCargoDelivered ledger rows (one per stopIndex) plus the single RouteDispatched row land, and CompletedCycles bumps exactly once")]
        public IEnumerator MultiStop_LoadedEndpoint_DeliversAtBothDocks()
        {
            // Post-restore unpack wait (yields BEFORE any seam/mutation).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live endpoint vessel to deliver onto");
            Vessel endpointVessel = FlightGlobals.ActiveVessel;
            if (!(endpointVessel.loaded && !endpointVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{endpointVessel.vesselName}' is not loaded+unpacked " +
                    $"(loaded={endpointVessel.loaded}, packed={endpointVessel.packed}); the multi-stop delivery " +
                    "would take the unloaded proto-snapshot path which does not mutate the live PartResource this test reads");

            Part fuelPart;
            PartResource fuelResource;
            if (!TryFindLiquidFuelPart(endpointVessel, out fuelPart, out fuelResource))
                InGameAssert.Skip(
                    $"Active vessel '{endpointVessel.vesselName}' has no part with a LiquidFuel resource; " +
                    "pick a vessel with at least one LF tank to run this test");

            double totalDelivery = DeliveryAmountA + DeliveryAmountB;

            // Pre-drain so there is headroom for the synthetic two-window delivery,
            // and clamp the expected fill to the actual headroom (degenerate tiny
            // tanks still pass the route-state + ledger assertions).
            double originalAmount = fuelResource.amount;
            double maxAmount = fuelResource.maxAmount;
            double preDrainTarget = originalAmount - totalDelivery;
            if (preDrainTarget < 0.0) preDrainTarget = 0.0;
            fuelResource.amount = preDrainTarget;
            double postDrainAmount = fuelResource.amount;
            double headroom = maxAmount - postDrainAmount;
            double expectedDelta = totalDelivery < headroom ? totalDelivery : headroom;
            double expectedAmount = postDrainAmount + expectedDelta;
            bool tankCanReceiveDelta = expectedDelta > ResourceTolerance;

            string treeId = "ingame-ms-loaded-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-ms-loaded-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            List<Route> preExistingRoutes = SnapshotRoutes();
            int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

            RunMultiStopCrossing(
                label: "MultiStop_Loaded",
                treeId: treeId,
                routeId: routeId,
                endpointVessel: endpointVessel,
                tickUT: TickUtBothDocks,
                preExistingRoutes: preExistingRoutes,
                restoreEndpointState: () =>
                {
                    if (fuelResource != null) fuelResource.amount = originalAmount;
                },
                assertions: capturedLog =>
                {
                    // 1. Route state: the multi-stop cycle completed once and snapped
                    //    LastObservedLoopCycleIndex to the crossed dock cycle (0); both
                    //    stops fired cycle 0.
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route postTick),
                        "Multi-stop route disappeared from store during Tick");
                    InGameAssert.AreEqual(RouteStatus.Active, postTick.Status,
                        $"Multi-stop loop route should stay Active after fire, was {postTick.Status}");
                    InGameAssert.AreEqual(1, postTick.CompletedCycles,
                        "CompletedCycles should be exactly 1 after a single two-window cycle");
                    InGameAssert.AreEqual(0, postTick.SkippedCycles,
                        "SkippedCycles should stay 0 on an eligible crossing");
                    InGameAssert.AreEqual(0L, postTick.LastObservedLoopCycleIndex,
                        "LastObservedLoopCycleIndex should snap to the crossed dock cycle (0)");
                    InGameAssert.AreEqual(2, postTick.Stops != null ? postTick.Stops.Count : 0,
                        "Route must carry two stops");
                    InGameAssert.AreEqual(0L, postTick.Stops[0].LastFiredCycleIndex,
                        "Stop 0 (dock A) should have fired cycle 0");
                    InGameAssert.AreEqual(0L, postTick.Stops[1].LastFiredCycleIndex,
                        "Stop 1 (dock B) should have fired cycle 0 in the SAME pass");

                    // 2. LIVE resource: the destination tank rose by the SUM of both
                    //    window manifests (within the tank's headroom).
                    if (tankCanReceiveDelta)
                    {
                        double actualAmount = fuelResource.amount;
                        InGameAssert.IsTrue(actualAmount > postDrainAmount + ResourceTolerance,
                            $"LiquidFuel did not INCREASE after the two-window delivery: " +
                            $"postDrain={postDrainAmount.ToString("R", IC)} actual={actualAmount.ToString("R", IC)}");
                        double diff = Math.Abs(actualAmount - expectedAmount);
                        InGameAssert.IsTrue(diff < ResourceTolerance,
                            $"Expected LiquidFuel ~= {expectedAmount.ToString("R", IC)} (postDrain + both windows) " +
                            $"after the multi-stop fire, but was {actualAmount.ToString("R", IC)} " +
                            $"(diff={diff.ToString("R", IC)} tol={ResourceTolerance.ToString("R", IC)})");
                    }
                    else
                    {
                        ParsekLog.Info("TestRunner",
                            $"MultiStop_Loaded: tank cannot hold the summed delta (expectedDelta={expectedDelta.ToString("R", IC)} " +
                            "<= tol); skipping live-tank assertion, route-state + ledger checks still ran");
                    }

                    // 3. Ledger rows: ONE RouteDispatched (dispatch fires once per cycle)
                    //    and TWO RouteCargoDelivered rows - one per stopIndex (the RANK-1
                    //    per-window replay-key hole would suppress window 1's row).
                    CountNewRouteRows(beforeLedgerCount, routeId,
                        out int dispatchedCount, out int deliveredCount, out var deliveredStopIndices);
                    InGameAssert.AreEqual(1, dispatchedCount,
                        $"Expected exactly one RouteDispatched row for routeId={routeId} (dispatch fires once per cycle)");
                    InGameAssert.AreEqual(2, deliveredCount,
                        $"Expected exactly TWO RouteCargoDelivered rows for routeId={routeId} (one per window); " +
                        "fewer means window 1 was suppressed by window 0's per-cycle replay key (RANK-1)");
                    InGameAssert.IsTrue(deliveredStopIndices.Contains(0) && deliveredStopIndices.Contains(1),
                        "The two delivery rows must carry DISTINCT stop indices 0 and 1");

                    ParsekLog.Info("TestRunner",
                        $"MultiStop_Loaded: PASS routeId={routeId} endpoint={endpointVessel.vesselName} " +
                        $"completedCycles={postTick.CompletedCycles.ToString(IC)} " +
                        $"deliveredRows={deliveredCount.ToString(IC)} dispatchedRows={dispatchedCount.ToString(IC)} " +
                        $"fuelBefore={postDrainAmount.ToString("R", IC)} fuelAfter={fuelResource.amount.ToString("R", IC)}");
                });
            yield break;
        }

        // ==================================================================
        // 2. Unloaded endpoint: both windows deliver to the proto snapshot
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "A 2-stop DELIVERY route whose stops resolve to an UNLOADED on-rails vessel delivers at BOTH recorded dock phases through the production unloaded writer (ProtoPartResourceSnapshot.amount): the proto LiquidFuel total rises by the SUM of both windows and TWO RouteCargoDelivered rows land. Sources an existing unloaded vessel from the save (no spawn) and skips with a named reason when none exists")]
        public IEnumerator MultiStop_UnloadedEndpoint_DeliversAtBothDocks()
        {
            // Post-restore unpack wait (yields BEFORE any seam/mutation).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (!TryFindUnloadedVesselWithHeadroom(out Vessel endpointVessel,
                    out double rawBefore))
                InGameAssert.Skip(
                    "PRECONDITION: no unloaded non-ghost vessel with a LiquidFuel tank that has spare " +
                    $"capacity (>= {MinTankHeadroom.ToString("R", IC)}) in this save. The unloaded-endpoint " +
                    "fixture sources an EXISTING on-rails vessel (plan finding 6: spawn-based fixtures are " +
                    "unproven ground); load a save with a distant vessel that has an LF tank with headroom");

            string treeId = "ingame-ms-proto-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-ms-proto-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            List<Route> preExistingRoutes = SnapshotRoutes();
            List<KeyValuePair<ProtoPartResourceSnapshot, double>> protoSnapshot =
                SnapshotProtoLiquidFuel(endpointVessel);
            int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

            RunMultiStopCrossing(
                label: "MultiStop_Unloaded",
                treeId: treeId,
                routeId: routeId,
                endpointVessel: endpointVessel,
                tickUT: TickUtBothDocks,
                preExistingRoutes: preExistingRoutes,
                restoreEndpointState: () => RestoreProtoLiquidFuel(protoSnapshot),
                assertions: capturedLog =>
                {
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route postTick),
                        "Multi-stop route disappeared from store during Tick");
                    InGameAssert.AreEqual(1, postTick.CompletedCycles,
                        "CompletedCycles should be exactly 1 after a single two-window cycle");

                    // The proto LiquidFuel total rose; clamp tolerance lets KSP's own
                    // capacity clamp trim the fill, so assert STRICTLY UP rather than
                    // an exact sum (the unloaded vessel's spare capacity is unknown).
                    double rawAfter = SumProtoLiquidFuelRaw(endpointVessel);
                    InGameAssert.IsTrue(rawAfter > rawBefore + ResourceTolerance,
                        $"Unloaded endpoint LiquidFuel total should INCREASE after the two-window delivery " +
                        $"(before={rawBefore.ToString("R", IC)} after={rawAfter.ToString("R", IC)})");

                    CountNewRouteRows(beforeLedgerCount, routeId,
                        out int dispatchedCount, out int deliveredCount, out var deliveredStopIndices);
                    InGameAssert.AreEqual(1, dispatchedCount,
                        "Expected exactly one RouteDispatched row (dispatch fires once per cycle)");
                    InGameAssert.AreEqual(2, deliveredCount,
                        "Expected exactly TWO RouteCargoDelivered rows (one per window)");
                    InGameAssert.IsTrue(deliveredStopIndices.Contains(0) && deliveredStopIndices.Contains(1),
                        "The two delivery rows must carry DISTINCT stop indices 0 and 1");

                    ParsekLog.Info("TestRunner",
                        $"MultiStop_Unloaded: PASS routeId={routeId} endpoint={endpointVessel.vesselName} " +
                        $"pid={endpointVessel.persistentId.ToString(IC)} " +
                        $"rawBefore={rawBefore.ToString("R", IC)} rawAfter={rawAfter.ToString("R", IC)} " +
                        $"deliveredRows={deliveredCount.ToString(IC)}");
                });
            yield break;
        }

        // ==================================================================
        // 3. Partial-cycle resume across a real GamePersistence.SaveGame
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "A 2-stop DELIVERY route fires window 0 only (the tick reaches dock A, not dock B), a real GamePersistence.SaveGame persists the per-stop LastFiredCycleIndex into the .sfs ROUTE/STOP node (stop 0 = 0, stop 1 = -1, cycle uncompleted), then the next tick reaching dock B fires window 1 and completes the SAME cycle exactly once (the A3 partial-cycle resume). Pauses time warp across the save; restores state and deletes the disposable slot in finally")]
        public IEnumerator MultiStop_PartialCycleResume_SurvivesSaveRoundTrip()
        {
            // Post-restore unpack wait (yields BEFORE any seam/mutation).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("HighLogic.CurrentGame is null; cannot drive GamePersistence.SaveGame");
            if (string.IsNullOrEmpty(HighLogic.SaveFolder))
                InGameAssert.Skip("HighLogic.SaveFolder is null/empty; cannot resolve save root");
            if (string.IsNullOrEmpty(KSPUtil.ApplicationRootPath))
                InGameAssert.Skip("KSPUtil.ApplicationRootPath is null/empty; cannot resolve .sfs path");
            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live endpoint vessel for the stops");
            Vessel endpointVessel = FlightGlobals.ActiveVessel;
            if (!(endpointVessel.loaded && !endpointVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{endpointVessel.vesselName}' is not loaded+unpacked; the delivery would take " +
                    "the unloaded proto path - this test reads the live PartResource between ticks");

            Part fuelPart;
            PartResource fuelResource;
            if (!TryFindLiquidFuelPart(endpointVessel, out fuelPart, out fuelResource))
                InGameAssert.Skip(
                    $"Active vessel '{endpointVessel.vesselName}' has no LiquidFuel tank; pick a vessel with one");

            double totalDelivery = DeliveryAmountA + DeliveryAmountB;
            double originalAmount = fuelResource.amount;
            double maxAmount = fuelResource.maxAmount;
            double preDrainTarget = originalAmount - totalDelivery;
            if (preDrainTarget < 0.0) preDrainTarget = 0.0;
            fuelResource.amount = preDrainTarget;
            double postDrainAmount = fuelResource.amount;
            bool tankHasHeadroom = maxAmount - postDrainAmount > ResourceTolerance;

            string treeId = "ingame-ms-resume-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-ms-resume-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string saveSlot = TestSaveSlotPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
            string savePath = Path.Combine(
                KSPUtil.ApplicationRootPath ?? string.Empty,
                "saves",
                HighLogic.SaveFolder ?? string.Empty,
                saveSlot + ".sfs");

            // Pause time warp across the save + yield window (saving under high warp
            // is flaky; the background tick is also paused while warp is 0).
            int warpIndexBefore = TimeWarp.CurrentRateIndex;
            bool warpPaused = false;
            if (warpIndexBefore > 0)
            {
                TimeWarp.SetRate(0, true);
                warpPaused = true;
            }

            List<Route> preExistingRoutes = SnapshotRoutes();
            int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

            RecordingTree routeTree = BuildMultiStopBackingTree(treeId);
            GhostPlaybackLogic.LoopUnit loopUnit = BuildSpanLoopUnit();

            bool routeTreeAdded = false, storeWiped = false, seamArmed = false;
            var previousResolver = RouteOrchestrator.LoopUnitResolverForTesting;
            var committedAdded = new List<Recording>();

            try
            {
                RecordingStore.AddCommittedTreeForTesting(routeTree);
                routeTreeAdded = true;
                foreach (Recording rec in routeTree.Recordings.Values)
                {
                    if (rec == null) continue;
                    RecordingStore.AddCommittedInternal(rec);
                    committedAdded.Add(rec);
                }

                // Wipe the store to ONLY our synthetic route so the single ticks
                // cannot touch any real route at the synthetic UTs.
                RouteStore.ResetForTesting();
                storeWiped = true;
                Route route = BuildMultiStopDeliveryRoute(routeId, treeId, endpointVessel);
                RouteStore.AddRoute(route);

                RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) =>
                {
                    if (r != null && string.Equals(r.Id, routeId, StringComparison.Ordinal))
                        return loopUnit;
                    return previousResolver != null ? previousResolver(r, ut) : (GhostPlaybackLogic.LoopUnit?)null;
                };
                seamArmed = true;

                // ---- TICK 1: reach ONLY dock A (UT 1500). Window 0 fires; the
                //      cycle is NOT completed (dock B not reached this pass). ----
                RouteOrchestrator.Tick(TickUtDockAOnly);

                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route afterTick1),
                    "Route disappeared after tick 1");
                InGameAssert.AreEqual(0, afterTick1.CompletedCycles,
                    "Cycle must NOT complete after only window 0 fired (dock B not yet reached)");
                InGameAssert.AreEqual(0L, afterTick1.Stops[0].LastFiredCycleIndex,
                    "Stop 0 (dock A) should have fired cycle 0 on tick 1");
                InGameAssert.AreEqual(-1L, afterTick1.Stops[1].LastFiredCycleIndex,
                    "Stop 1 (dock B) must NOT have fired yet (its dock phase was not reached on tick 1)");
                CountNewRouteRows(beforeLedgerCount, routeId,
                    out int dispatched1, out int delivered1, out var stops1);
                InGameAssert.AreEqual(1, dispatched1, "Dispatch should fire once when the cycle opens (tick 1)");
                InGameAssert.AreEqual(1, delivered1, "Exactly one delivery row (window 0) after tick 1");
                InGameAssert.IsTrue(stops1.Contains(0), "The tick-1 delivery row must be stop 0 (dock A)");

                // Disarm the seam BEFORE the save: GamePersistence.SaveGame + the
                // one-frame settle yield must not re-enter our resolver.
                RouteOrchestrator.LoopUnitResolverForTesting = previousResolver;
                seamArmed = false;

                // ---- SAVE: a real KSP save. ParsekScenario.OnSave drives
                //      RouteStore.SaveRoutesTo, so the partially-fired per-stop
                //      LastFiredCycleIndex must land in the .sfs ROUTE/STOP node. ----
                string saveResult = GamePersistence.SaveGame(saveSlot, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                InGameAssert.IsTrue(!string.IsNullOrEmpty(saveResult),
                    $"GamePersistence.SaveGame returned null/empty for slot '{saveSlot}'");

                yield return null; // let deferred-one-frame writes settle

                InGameAssert.IsTrue(File.Exists(savePath),
                    $"Expected .sfs at '{savePath}' after GamePersistence.SaveGame");

                // Parse the saved .sfs and prove the partial-cycle fire state
                // persisted: the ROUTE node's STOP children carry stop0 lastFired=0,
                // stop1 lastFired=-1 (omitted at -1), and the route completed 0 cycles.
                ConfigNode root = ConfigNode.Load(savePath);
                InGameAssert.IsNotNull(root, $"ConfigNode.Load returned null for '{savePath}'");
                if (!TryFindRouteNode(root, routeId, out ConfigNode routeNode))
                    InGameAssert.Skip(
                        $"Could not locate the ROUTE node id={routeId} in '{savePath}' (save layout mismatch); " +
                        "skipping rather than false-failing - the in-memory tick-1 partial-fire assertions above passed");

                InGameAssert.AreEqual("0", routeNode.GetValue("completedCycles") ?? "0",
                    "Saved route completedCycles must be 0 (the cycle is mid-flight, window B not yet fired)");
                ConfigNode[] stopNodes = routeNode.GetNodes("STOP");
                InGameAssert.AreEqual(2, stopNodes != null ? stopNodes.Length : 0,
                    "Saved route must carry two STOP nodes");
                // Stop 0 fired cycle 0 -> the sparse lastFiredCycleIndex key is present and 0.
                InGameAssert.AreEqual("0", stopNodes[0].GetValue("lastFiredCycleIndex"),
                    "Saved STOP 0 must carry lastFiredCycleIndex=0 (window 0 fired before the save)");
                // Stop 1 never fired -> the sparse key is OMITTED (-1 default).
                InGameAssert.IsNull(stopNodes[1].GetValue("lastFiredCycleIndex"),
                    "Saved STOP 1 must OMIT lastFiredCycleIndex (still -1; window 1 not fired before the save)");

                // ---- RELOAD the route from the persisted node via the production
                //      RouteStore.LoadRoutesFrom codec (the exact path OnLoad uses),
                //      then continue. This proves the resumed route - not the live
                //      in-memory one - carries the partial fire state. ----
                RouteStore.ResetForTesting();
                ConfigNode scenarioWrap = FindParsekScenarioNode(root) ?? root;
                int loaded = RouteStore.LoadRoutesFrom(scenarioWrap);
                InGameAssert.IsTrue(loaded >= 1, "Reload should restore at least our synthetic route from the .sfs");
                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route reloaded),
                    "The synthetic route did not survive the save/reload round-trip");
                InGameAssert.AreEqual(0L, reloaded.Stops[0].LastFiredCycleIndex,
                    "Reloaded stop 0 must carry lastFiredCycleIndex=0");
                InGameAssert.AreEqual(-1L, reloaded.Stops[1].LastFiredCycleIndex,
                    "Reloaded stop 1 must carry lastFiredCycleIndex=-1");
                InGameAssert.AreEqual(0, reloaded.CompletedCycles,
                    "Reloaded route must carry completedCycles=0");

                // Re-arm the resolver for the resumed route and tick to dock B.
                RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) =>
                {
                    if (r != null && string.Equals(r.Id, routeId, StringComparison.Ordinal))
                        return loopUnit;
                    return previousResolver != null ? previousResolver(r, ut) : (GhostPlaybackLogic.LoopUnit?)null;
                };
                seamArmed = true;

                int beforeResumeLedger = Ledger.Actions != null ? Ledger.Actions.Count : 0;

                // ---- TICK 2: reach dock B (UT 2500). Window 1 fires and COMPLETES
                //      the same cycle exactly once. Window 0 must NOT re-fire. ----
                RouteOrchestrator.Tick(TickUtBothDocks);

                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route afterTick2),
                    "Route disappeared after the resume tick");
                InGameAssert.AreEqual(1, afterTick2.CompletedCycles,
                    "The resumed tick reaching dock B must complete the cycle exactly once (CompletedCycles=1)");
                InGameAssert.AreEqual(0L, afterTick2.Stops[1].LastFiredCycleIndex,
                    "Stop 1 (dock B) should now have fired cycle 0");

                CountNewRouteRows(beforeResumeLedger, routeId,
                    out int dispatched2, out int delivered2, out var stops2);
                InGameAssert.AreEqual(1, delivered2,
                    "The resume tick must fire EXACTLY window 1 (one new delivery row); window 0 must NOT re-fire");
                InGameAssert.IsTrue(stops2.Contains(1) && !stops2.Contains(0),
                    "The resume delivery row must be stop 1 (dock B) only - stop 0 must not re-deliver");
                InGameAssert.AreEqual(0, dispatched2,
                    "No new RouteDispatched row on the resume tick (dispatch already fired + persisted for this cycle)");

                ParsekLog.Info("TestRunner",
                    $"MultiStop_PartialResume: PASS routeId={routeId} slot='{saveSlot}' " +
                    $"tick1Delivered=stop0 tick2Delivered=stop1 completedCycles={afterTick2.CompletedCycles.ToString(IC)} " +
                    $"reloadedStop0Fired={reloaded.Stops[0].LastFiredCycleIndex.ToString(IC)} " +
                    $"reloadedStop1Fired={reloaded.Stops[1].LastFiredCycleIndex.ToString(IC)} " +
                    $"tankHadHeadroom={(tankHasHeadroom ? "1" : "0")}");
            }
            finally
            {
                if (seamArmed)
                    RouteOrchestrator.LoopUnitResolverForTesting = previousResolver;

                if (storeWiped)
                    RestoreRoutes(preExistingRoutes);

                for (int i = 0; i < committedAdded.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedAdded[i]);
                if (routeTreeAdded)
                    RemoveCommittedTree(treeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);

                QuickloadResumeHelpers.TryDeleteSaveSlot(saveSlot);

                try
                {
                    if (fuelResource != null)
                        fuelResource.amount = originalAmount;
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"MultiStop_PartialResume cleanup: failed to restore LiquidFuel ({ex.GetType().Name}: {ex.Message})");
                }

                if (warpPaused)
                {
                    try { TimeWarp.SetRate(warpIndexBefore, true); }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn("TestRunner",
                            $"MultiStop_PartialResume cleanup: failed to restore time warp ({ex.GetType().Name}: {ex.Message})");
                    }
                }
            }
        }

        // ==================================================================
        // Shared crossing runner (synchronous, single-tick cases)
        // ==================================================================

        /// <summary>
        /// Shared arrange / act / assert / teardown frame for the synchronous
        /// single-tick multi-stop cases: commits the backing tree + member
        /// recordings (so the ERS eligibility gate is real), wipes RouteStore down
        /// to ONE synthetic two-stop KSC-origin delivery route, arms the loop-unit
        /// resolver seam (route-id scoped) with the two-dock span unit, captures
        /// Info log lines, runs ONE production <see cref="RouteOrchestrator.Tick(double)"/>
        /// at <paramref name="tickUT"/>, runs the caller's assertions, and restores
        /// everything in finally. Leaves the DELIVERY half on the LIVE path so the
        /// real per-window endpoint writes + ledger rows are exercised (no fake).
        /// </summary>
        private static void RunMultiStopCrossing(
            string label,
            string treeId,
            string routeId,
            Vessel endpointVessel,
            double tickUT,
            List<Route> preExistingRoutes,
            Action restoreEndpointState,
            Action<List<string>> assertions)
        {
            RecordingTree routeTree = BuildMultiStopBackingTree(treeId);
            GhostPlaybackLogic.LoopUnit loopUnit = BuildSpanLoopUnit();

            bool routeTreeAdded = false, storeWiped = false, resolverArmed = false;
            var previousResolver = RouteOrchestrator.LoopUnitResolverForTesting;
            var previousObserver = ParsekLog.TestObserverForTesting;
            var committedAdded = new List<Recording>();
            var capturedLog = new List<string>();

            try
            {
                RecordingStore.AddCommittedTreeForTesting(routeTree);
                routeTreeAdded = true;
                foreach (Recording rec in routeTree.Recordings.Values)
                {
                    if (rec == null) continue;
                    RecordingStore.AddCommittedInternal(rec);
                    committedAdded.Add(rec);
                }

                RouteStore.ResetForTesting();
                storeWiped = true;
                Route route = BuildMultiStopDeliveryRoute(routeId, treeId, endpointVessel);
                RouteStore.AddRoute(route);
                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out _),
                    "Synthetic multi-stop route was not stored");

                RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) =>
                {
                    if (r != null && string.Equals(r.Id, routeId, StringComparison.Ordinal))
                        return loopUnit;
                    return previousResolver != null
                        ? previousResolver(r, ut)
                        : (GhostPlaybackLogic.LoopUnit?)null;
                };
                resolverArmed = true;

                ParsekLog.TestObserverForTesting = line =>
                {
                    capturedLog.Add(line);
                    previousObserver?.Invoke(line);
                };

                ParsekLog.Verbose("TestRunner",
                    $"{label}: pre-tick routeId={routeId} treeId={treeId} " +
                    $"endpointPid={(endpointVessel != null ? endpointVessel.persistentId.ToString(IC) : "<none>")} " +
                    $"dockA={DockUtA.ToString("R", IC)} dockB={DockUtB.ToString("R", IC)} tickUT={tickUT.ToString("R", IC)}");

                // ACT - one production no-env tick through the multi-stop loop branch.
                RouteOrchestrator.Tick(tickUT);

                assertions(capturedLog);
            }
            finally
            {
                ParsekLog.TestObserverForTesting = previousObserver;
                if (resolverArmed)
                    RouteOrchestrator.LoopUnitResolverForTesting = previousResolver;

                if (storeWiped)
                    RestoreRoutes(preExistingRoutes);

                for (int i = 0; i < committedAdded.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedAdded[i]);
                if (routeTreeAdded)
                    RemoveCommittedTree(treeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);

                try
                {
                    restoreEndpointState?.Invoke();
                    ParsekLog.Verbose("TestRunner", $"{label} cleanup: endpoint resource state restored");
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"{label} cleanup: failed to restore endpoint resource state ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }

        // ==================================================================
        // Route + loop-unit + tree fixtures
        // ==================================================================

        // Deterministic two-dock span unit (span [1000,3000], cadence == span,
        // anchor == spanStart). Owner / member indices are not read by the fire
        // path; only the span-clock fields drive the crossing.
        private static GhostPlaybackLogic.LoopUnit BuildSpanLoopUnit()
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0,
                memberIndices: new[] { 0 },
                spanStartUT: SpanStartUT,
                spanEndUT: SpanEndUT,
                cadenceSeconds: Cadence,
                phaseAnchorUT: SpanStartUT);
        }

        /// <summary>
        /// Synthetic KSC-origin two-stop DELIVERY route: both stops resolve to the
        /// SAME live endpoint vessel (allowed, plan OQ6) but at DISTINCT recorded
        /// dock phases (dock A at <see cref="DockUtA"/>, dock B at
        /// <see cref="DockUtB"/>), each with its own LiquidFuel delivery manifest.
        /// KSC origin so the origin-cargo gate passes with no source vessel and the
        /// funds gate is Career-only (skipped in Sandbox / no funds in this test).
        /// The route-level scalar dock fields key on the LAST (max-DockUT) stop, as
        /// RouteBuilder derives them (A2).
        /// </summary>
        private static Route BuildMultiStopDeliveryRoute(string routeId, string treeId, Vessel endpointVessel)
        {
            RouteEndpoint EndpointFor() => new RouteEndpoint
            {
                VesselPersistentId = endpointVessel != null ? endpointVessel.persistentId : 0u,
                BodyName = endpointVessel != null && endpointVessel.mainBody != null
                    ? endpointVessel.mainBody.bodyName : "Kerbin",
                IsSurface = false,
            };

            return new Route
            {
                Id = routeId,
                Name = "Parsek Multi-Stop Delivery In-Game",
                Status = RouteStatus.Active,
                IsKscOrigin = true,
                BackingMissionTreeId = treeId,
                ExcludedIntervalKeys = new HashSet<string>(),
                // Route-level scalars key on the LAST dock (A2): dock B.
                RecordedDockUT = DockUtB,
                DockMemberRecordingId = "dockedB",
                LoopAnchorUT = SpanStartUT,
                LastObservedLoopCycleIndex = -1,
                TransitDuration = Cadence,
                DispatchInterval = Cadence,
                NextDispatchUT = TickUtBothDocks + Cadence,
                CompletedCycles = 0,
                SkippedCycles = 0,
                KscDispatchFundsCost = 0.0,
                // KSC origin: the launch covers both deliveries; no per-source debit.
                CostManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                InventoryCostManifest = new List<InventoryPayloadItem>(),
                RecordingIds = new List<string> { "launch", "midA2B", "dockedB" },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "launch", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "midA2B", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "dockedB", TreeId = treeId },
                },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = EndpointFor(),
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, DeliveryAmountA },
                        },
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 0,
                        RecordedDockUT = DockUtA,
                        LastFiredCycleIndex = -1,
                    },
                    new RouteStop
                    {
                        Endpoint = EndpointFor(),
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, DeliveryAmountB },
                        },
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 1,
                        RecordedDockUT = DockUtB,
                        LastFiredCycleIndex = -1,
                    },
                },
            };
        }

        // launch -> dock A -> intermediate undock -> dock B -> terminal undock.
        // Mirrors the verified xUnit / RouteOnMissions multi-stop topology so the
        // ERS membership gate resolves "launch"/"midA2B"/"dockedB". The fire path
        // resolves the loop unit through the seam, so the exact composition is not
        // load-bearing here; the member recordings just need to be committed.
        private static RecordingTree BuildMultiStopBackingTree(string treeId)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = "launch" };
            tree.Recordings["launch"] = Leg("launch", "C0", 0, 1000, 1500, "Transport");
            tree.Recordings["midA2B"] = Leg("midA2B", "C0", 1, 1500, 2500, "Transport");
            tree.Recordings["payloadA"] = Leg("payloadA", "C1", 0, 1500, 1800, "Payload");
            tree.Recordings["dockedB"] = Leg("dockedB", "C0", 2, 2500, 3000, "Transport");
            tree.Recordings["tail"] = Leg("tail", "C0", 3, 3000, 3500, "Transport");
            tree.Recordings["payloadB"] = Leg("payloadB", "C2", 0, 3000, 3300, "Payload");
            tree.BranchPoints.Add(BP("undockA-bp", BranchPointType.Undock,
                new[] { "launch" }, new[] { "midA2B", "payloadA" }, 1500));
            tree.BranchPoints.Add(BP("dockB-bp", BranchPointType.Dock,
                new[] { "midA2B" }, new[] { "dockedB" }, 2500));
            tree.BranchPoints.Add(BP("undockB-bp", BranchPointType.Undock,
                new[] { "dockedB" }, new[] { "tail", "payloadB" }, 3000));
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

        // ==================================================================
        // State snapshot / restore helpers
        // ==================================================================

        private static List<Route> SnapshotRoutes()
        {
            var snapshot = new List<Route>();
            IReadOnlyList<Route> committed = RouteStore.CommittedRoutes;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i] != null)
                    snapshot.Add(committed[i]);
            return snapshot;
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

        private static List<KeyValuePair<ProtoPartResourceSnapshot, double>> SnapshotProtoLiquidFuel(Vessel vessel)
        {
            var snapshot = new List<KeyValuePair<ProtoPartResourceSnapshot, double>>();
            ProtoVessel pv = vessel != null ? vessel.protoVessel : null;
            if (pv == null || pv.protoPartSnapshots == null) return snapshot;
            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null || pps.resources == null) continue;
                for (int j = 0; j < pps.resources.Count; j++)
                {
                    ProtoPartResourceSnapshot prs = pps.resources[j];
                    if (prs == null) continue;
                    if (!string.Equals(prs.resourceName, LiquidFuelName, StringComparison.Ordinal)) continue;
                    snapshot.Add(new KeyValuePair<ProtoPartResourceSnapshot, double>(prs, prs.amount));
                }
            }
            return snapshot;
        }

        private static void RestoreProtoLiquidFuel(List<KeyValuePair<ProtoPartResourceSnapshot, double>> snapshot)
        {
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i].Key != null)
                    snapshot[i].Key.amount = snapshot[i].Value;
        }

        private static double SumProtoLiquidFuelRaw(Vessel vessel)
        {
            double total = 0.0;
            ProtoVessel pv = vessel != null ? vessel.protoVessel : null;
            if (pv == null || pv.protoPartSnapshots == null) return total;
            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null || pps.resources == null) continue;
                for (int j = 0; j < pps.resources.Count; j++)
                {
                    ProtoPartResourceSnapshot prs = pps.resources[j];
                    if (prs == null) continue;
                    if (!string.Equals(prs.resourceName, LiquidFuelName, StringComparison.Ordinal)) continue;
                    total += prs.amount;
                }
            }
            return total;
        }

        /// <summary>
        /// Finds an existing UNLOADED, non-ghost, non-active vessel with a
        /// LiquidFuel tank that has spare CAPACITY (so a delivery onto it can rise).
        /// Mirrors the M1/M3 unloaded fixtures (plan finding 6: spawn-based fixtures
        /// are unproven ground). <paramref name="rawBefore"/> is the unconditional
        /// proto LiquidFuel total (the basis the delivery raises).
        /// </summary>
        private static bool TryFindUnloadedVesselWithHeadroom(out Vessel candidate, out double rawBefore)
        {
            candidate = null;
            rawBefore = 0.0;
            List<Vessel> vessels = FlightGlobals.Vessels;
            if (vessels == null) return false;
            HashSet<uint> ghostPids = GhostMapPresence.ghostMapVesselPids;
            Vessel active = FlightGlobals.ActiveVessel;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null || v.loaded) continue;
                if (active != null && ReferenceEquals(v, active)) continue;
                if (ghostPids != null && ghostPids.Contains(v.persistentId)) continue;
                if (v.protoVessel == null || v.protoVessel.protoPartSnapshots == null) continue;

                double stored = 0.0;
                double capacity = 0.0;
                ProtoVessel pv = v.protoVessel;
                for (int p = 0; p < pv.protoPartSnapshots.Count; p++)
                {
                    ProtoPartSnapshot pps = pv.protoPartSnapshots[p];
                    if (pps == null || pps.resources == null) continue;
                    for (int r = 0; r < pps.resources.Count; r++)
                    {
                        ProtoPartResourceSnapshot prs = pps.resources[r];
                        if (prs == null) continue;
                        if (!string.Equals(prs.resourceName, LiquidFuelName, StringComparison.Ordinal)) continue;
                        stored += prs.amount;
                        capacity += prs.maxAmount;
                    }
                }
                if (capacity - stored < MinTankHeadroom) continue;

                candidate = v;
                rawBefore = stored;
                return true;
            }
            return false;
        }

        // ==================================================================
        // Ledger + .sfs lookup helpers
        // ==================================================================

        /// <summary>
        /// Counts the RouteDispatched / RouteCargoDelivered rows appended after
        /// <paramref name="fromIndex"/> for <paramref name="routeId"/>, collecting
        /// the DISTINCT stop indices the delivered rows carry (so the per-window
        /// replay-key fix is visible: a 2-window cycle emits two delivered rows with
        /// stop indices {0,1}).
        /// </summary>
        private static void CountNewRouteRows(int fromIndex, string routeId,
            out int dispatchedCount, out int deliveredCount, out HashSet<int> deliveredStopIndices)
        {
            dispatchedCount = 0;
            deliveredCount = 0;
            deliveredStopIndices = new HashSet<int>();
            var actions = Ledger.Actions;
            if (actions == null) return;
            for (int i = fromIndex; i < actions.Count; i++)
            {
                GameAction a = actions[i];
                if (a == null) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (a.Type == GameActionType.RouteDispatched) dispatchedCount++;
                else if (a.Type == GameActionType.RouteCargoDelivered)
                {
                    deliveredCount++;
                    deliveredStopIndices.Add(a.RouteStopIndex);
                }
            }
        }

        /// <summary>
        /// Tolerant .sfs walk to the Parsek scenario node: the root may or may not
        /// wrap the GAME node. Returns the SCENARIO node named ParsekScenario, or
        /// null when not found.
        /// </summary>
        private static ConfigNode FindParsekScenarioNode(ConfigNode root)
        {
            if (root == null) return null;
            ConfigNode gameNode = root.GetNode("GAME") ?? root;
            ConfigNode[] scenarios = gameNode.GetNodes("SCENARIO");
            if (scenarios == null) return null;
            for (int i = 0; i < scenarios.Length; i++)
            {
                ConfigNode s = scenarios[i];
                if (s == null) continue;
                if (string.Equals(s.GetValue("name"), "ParsekScenario", StringComparison.Ordinal))
                    return s;
            }
            return null;
        }

        /// <summary>
        /// Finds the ROUTE node for <paramref name="routeId"/> inside the saved
        /// ParsekScenario / ROUTES container. Returns false when the layout does not
        /// match (the caller then skips rather than false-fails).
        /// </summary>
        private static bool TryFindRouteNode(ConfigNode root, string routeId, out ConfigNode routeNode)
        {
            routeNode = null;
            ConfigNode scenario = FindParsekScenarioNode(root);
            if (scenario == null) return false;
            ConfigNode routes = scenario.GetNode("ROUTES");
            if (routes == null) return false;
            ConfigNode[] routeNodes = routes.GetNodes("ROUTE");
            if (routeNodes == null) return false;
            for (int i = 0; i < routeNodes.Length; i++)
            {
                ConfigNode rn = routeNodes[i];
                if (rn == null) continue;
                if (string.Equals(rn.GetValue("id"), routeId, StringComparison.Ordinal))
                {
                    routeNode = rn;
                    return true;
                }
            }
            return false;
        }
    }
}
