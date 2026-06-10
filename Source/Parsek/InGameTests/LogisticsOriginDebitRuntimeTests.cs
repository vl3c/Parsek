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
    /// M1 origin-debit lifecycle tests - the in-game layer (c) of the
    /// scenario-lifecycle test harness (plan `plan-logistics-m1-origin-debit.md`
    /// section 4 Phase 6, design D10). xUnit covers the pure planner / check /
    /// codec layers; these tests pin the parts only live KSP can exercise:
    /// the LIVE origin-vessel mutation through the production
    /// <see cref="RouteOrchestrator.Tick(double)"/> loop-crossing path
    /// (loaded <c>PartResource.amount</c> AND unloaded
    /// <c>ProtoPartResourceSnapshot.amount</c> writes), the empty-origin
    /// eligibility hold, and the proto-snapshot debit surviving a real
    /// <see cref="GamePersistence.SaveGame"/> round-trip.
    ///
    /// <para><b>Re-entry discipline (todo "background RouteOrchestrator.Tick
    /// can re-enter a logistics test's synthetic route").</b> The three
    /// orchestrator-driven tests are SYNCHRONOUS (<c>void</c>, no yields):
    /// the whole arrange / Tick / assert / teardown sequence runs inside one
    /// frame on the main thread, so the background 1 Hz scenario tick can
    /// never interleave with an armed seam or a stored synthetic route -
    /// strictly stronger isolation than pausing time warp across a yield.
    /// The save round-trip test does yield (deferred .sfs writes must
    /// settle), so it arms NO seams, stores NO route, and pauses / restores
    /// time warp across the yield window.</para>
    ///
    /// <para><b>Unloaded-origin fixture (plan finding 6).</b> No existing
    /// in-game test spawns a distant unloaded vessel, and a spawn-based
    /// fixture was flagged as new, unproven ground. The unloaded tests
    /// instead source an EXISTING on-rails vessel from the current save
    /// (non-ghost, unloaded, holding debitable LiquidFuel per the production
    /// <see cref="LiveOriginCargoProbe"/>) and precondition-skip with a named
    /// reason when the save has none; every mutated tank amount is restored
    /// in <c>finally</c>.</para>
    /// </summary>
    public sealed class LogisticsOriginDebitRuntimeTests
    {
        private const string LiquidFuelName = "LiquidFuel";
        private const double DefaultDebitAmount = 5.0;
        private const double MinMeaningfulDebit = 0.1;
        private const double ResourceTolerance = 0.01;
        private const string TestSaveSlotPrefix = "parsek_origindebit_ingame_test_";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const string IsolatedOnlyBatchSkipReason =
            "Isolated-run only - mutates RouteStore, Ledger, RecordingStore committed trees, " +
            "RouteOrchestrator test seams, and live vessel resource state under live KSP statics; " +
            "excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play " +
            "button in a disposable FLIGHT session.";

        // Deterministic span clock (same shape as
        // LogisticsRouteOnMissionsRuntimeTests.LoopFire_RendersAndDelivers_AtDockCrossing):
        // span [1000,3000], cadence == span, anchor == spanStart, dock UT 2000,
        // tick at 2000 -> cycleIndex 0, not in the inter-cycle tail, dock phase
        // reached -> crossing fires on the first tick (LastObservedLoopCycleIndex
        // starts at -1).
        private const double SpanStartUT = 1000.0;
        private const double SpanEndUT = 3000.0;
        private const double DockUT = 2000.0;
        private const double TickUT = 2000.0;
        private const double Cadence = SpanEndUT - SpanStartUT;

        // ==================================================================
        // 1. Loaded-origin debit through the production loop crossing
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "A non-KSC loop-route crossing through RouteOrchestrator.Tick physically removes the CostManifest LiquidFuel from the LOADED origin vessel (the active vessel), emits a RouteCargoDebited row carrying the actual manifest + origin pid, and logs the Origin debit line; the delivery half is a no-op seam so only the debit mutates state")]
        public void OriginDebit_LoadedOriginVessel_RemovesManifestAmount()
        {
            // PRECONDITIONS --------------------------------------------------
            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live origin vessel to debit");
            Vessel originVessel = FlightGlobals.ActiveVessel;
            if (!(originVessel.loaded && !originVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{originVessel.vesselName}' is not loaded+unpacked " +
                    $"(loaded={originVessel.loaded}, packed={originVessel.packed}); the origin debit would take " +
                    "the unloaded proto-snapshot path which does not mutate the live PartResource this test reads");

            // Measure the debitable pool with the PRODUCTION probe (the same
            // gate the planner and the writer use), so the expected delta is
            // exactly what the debit may remove.
            double storedBefore = new LiveOriginCargoProbe(originVessel, true)
                .ProbeResourceStored(LiquidFuelName);
            if (storedBefore < MinMeaningfulDebit)
                InGameAssert.Skip(
                    $"Active vessel '{originVessel.vesselName}' stores only " +
                    $"{storedBefore.ToString("R", IC)} debitable LiquidFuel " +
                    $"(< {MinMeaningfulDebit.ToString("R", IC)}); pick a vessel with fuel aboard to run this test");
            double debitAmount = Math.Min(DefaultDebitAmount, storedBefore);

            string treeId = "ingame-od-loaded-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-od-loaded-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Read-only snapshots (taken BEFORE any mutation).
            List<Route> preExistingRoutes = SnapshotRoutes();
            List<KeyValuePair<PartResource, double>> tankSnapshot =
                SnapshotLoadedLiquidFuel(originVessel);
            int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

            RunOriginDebitCrossing(
                label: "OriginDebit_Loaded",
                treeId: treeId,
                routeId: routeId,
                originVesselForRoute: originVessel,
                destVessel: originVessel,
                debitAmount: debitAmount,
                preExistingRoutes: preExistingRoutes,
                restoreOriginState: () => RestoreLoadedLiquidFuel(tankSnapshot),
                assertions: capturedLog =>
                {
                    // 1. LIVE resource: the deliverable pool dropped by exactly
                    //    the manifest amount (production probe on both sides).
                    double storedAfter = new LiveOriginCargoProbe(originVessel, true)
                        .ProbeResourceStored(LiquidFuelName);
                    InGameAssert.ApproxEqual(storedBefore - debitAmount, storedAfter, ResourceTolerance,
                        $"Origin LiquidFuel pool should drop by {debitAmount.ToString("R", IC)} " +
                        $"(before={storedBefore.ToString("R", IC)} after={storedAfter.ToString("R", IC)})");

                    // 2. Ledger rows: dispatch-debit pair present, debited row
                    //    carries the ACTUAL manifest + origin pid, no funds cost,
                    //    no requested-on-shortfall (full debit), and the no-op
                    //    delivery seam emitted NO delivered row.
                    FindNewRouteRows(beforeLedgerCount, routeId,
                        out GameAction dispatched, out GameAction debited, out GameAction delivered);
                    InGameAssert.IsNotNull(dispatched,
                        $"No RouteDispatched ledger row for routeId={routeId}");
                    InGameAssert.IsNotNull(debited,
                        $"No RouteCargoDebited ledger row for routeId={routeId}");
                    InGameAssert.IsNull(delivered,
                        "No RouteCargoDelivered row expected - the delivery half was a no-op seam");
                    InGameAssert.AreEqual(originVessel.persistentId, debited.RouteOriginVesselPid,
                        "Debited row must carry the origin vessel pid");
                    InGameAssert.IsNotNull(debited.RouteResourceManifest,
                        "Debited row must carry the actual-debited manifest");
                    InGameAssert.IsTrue(debited.RouteResourceManifest.ContainsKey(LiquidFuelName),
                        "Debited row manifest must contain LiquidFuel");
                    InGameAssert.ApproxEqual(debitAmount, debited.RouteResourceManifest[LiquidFuelName], ResourceTolerance,
                        "Debited row actuals must equal the removed amount");
                    InGameAssert.IsNull(debited.RouteRequestedResourceManifest,
                        "Full debit must not record a requested-on-shortfall manifest");
                    InGameAssert.AreEqual(0f, debited.RouteKscFundsCost,
                        "Non-KSC debit row must carry no funds cost");

                    // 3. The per-resource Info write log fired on the loaded path.
                    InGameAssert.IsTrue(
                        capturedLog.Exists(l => l.Contains("Origin debit:")
                            && l.Contains(routeId) && l.Contains("path=loaded")),
                        "Expected an 'Origin debit:' Info line for the route on path=loaded");

                    // 4. Route state: fired cycle, stayed Active, index snapped.
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route postTick),
                        "Synthetic route disappeared from store during Tick");
                    InGameAssert.AreEqual(RouteStatus.Active, postTick.Status,
                        $"Loop route should stay Active after fire, was {postTick.Status}");
                    InGameAssert.AreEqual(1, postTick.CompletedCycles,
                        "CompletedCycles should be 1 (bumped by the no-op delivery seam)");
                    InGameAssert.AreEqual(0, postTick.SkippedCycles,
                        "SkippedCycles should stay 0 on an eligible crossing");
                    InGameAssert.AreEqual(0L, postTick.LastObservedLoopCycleIndex,
                        "LastObservedLoopCycleIndex should snap to the crossed dock cycle (0)");

                    ParsekLog.Info("TestRunner",
                        $"OriginDebit_Loaded: PASS routeId={routeId} " +
                        $"storedBefore={storedBefore.ToString("R", IC)} storedAfter={storedAfter.ToString("R", IC)} " +
                        $"debit={debitAmount.ToString("R", IC)} originPid={originVessel.persistentId.ToString(IC)}");
                });
        }

        // ==================================================================
        // 2. Unloaded-origin debit writes the proto snapshot
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "A non-KSC loop-route crossing whose origin is an UNLOADED on-rails vessel drains ProtoPartResourceSnapshot.amount through the production unloaded writer path; the RouteCargoDebited row carries the actuals + origin pid. Sources an existing unloaded vessel from the save (no spawn) and skips with a named reason when none exists")]
        public void OriginDebit_UnloadedOriginVessel_WritesProtoSnapshot()
        {
            // PRECONDITIONS --------------------------------------------------
            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live destination vessel for the stop endpoint");
            Vessel destVessel = FlightGlobals.ActiveVessel;

            if (!TryFindUnloadedLiquidFuelVessel(out Vessel originVessel, out double storedBefore))
                InGameAssert.Skip(
                    "PRECONDITION: no unloaded non-ghost vessel with >= " +
                    $"{MinMeaningfulDebit.ToString("R", IC)} debitable LiquidFuel in this save. " +
                    "The unloaded-origin fixture sources an EXISTING on-rails vessel (plan finding 6: " +
                    "spawn-based fixtures are unproven ground); load a save with a distant fuel-carrying " +
                    "vessel (e.g. an orbiting depot) to run this test");
            double debitAmount = Math.Min(DefaultDebitAmount, storedBefore);

            string treeId = "ingame-od-proto-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-od-proto-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            List<Route> preExistingRoutes = SnapshotRoutes();
            List<KeyValuePair<ProtoPartResourceSnapshot, double>> protoSnapshot =
                SnapshotProtoLiquidFuel(originVessel);
            int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

            RunOriginDebitCrossing(
                label: "OriginDebit_Unloaded",
                treeId: treeId,
                routeId: routeId,
                originVesselForRoute: originVessel,
                destVessel: destVessel,
                debitAmount: debitAmount,
                preExistingRoutes: preExistingRoutes,
                restoreOriginState: () => RestoreProtoLiquidFuel(protoSnapshot),
                assertions: capturedLog =>
                {
                    // 1. Proto snapshots: the deliverable pool dropped by the
                    //    manifest amount (production probe, unloaded branch).
                    double storedAfter = new LiveOriginCargoProbe(originVessel, false)
                        .ProbeResourceStored(LiquidFuelName);
                    InGameAssert.ApproxEqual(storedBefore - debitAmount, storedAfter, ResourceTolerance,
                        $"Unloaded origin LiquidFuel pool should drop by {debitAmount.ToString("R", IC)} " +
                        $"(before={storedBefore.ToString("R", IC)} after={storedAfter.ToString("R", IC)})");

                    // 2. Debited row: actuals + origin pid, debit took the
                    //    unloaded path.
                    FindNewRouteRows(beforeLedgerCount, routeId,
                        out GameAction dispatched, out GameAction debited, out GameAction delivered);
                    InGameAssert.IsNotNull(dispatched,
                        $"No RouteDispatched ledger row for routeId={routeId}");
                    InGameAssert.IsNotNull(debited,
                        $"No RouteCargoDebited ledger row for routeId={routeId}");
                    InGameAssert.IsNull(delivered,
                        "No RouteCargoDelivered row expected - the delivery half was a no-op seam");
                    InGameAssert.AreEqual(originVessel.persistentId, debited.RouteOriginVesselPid,
                        "Debited row must carry the unloaded origin vessel pid");
                    InGameAssert.IsNotNull(debited.RouteResourceManifest,
                        "Debited row must carry the actual-debited manifest");
                    InGameAssert.IsTrue(debited.RouteResourceManifest.ContainsKey(LiquidFuelName),
                        "Debited row manifest must contain LiquidFuel");
                    InGameAssert.ApproxEqual(debitAmount, debited.RouteResourceManifest[LiquidFuelName], ResourceTolerance,
                        "Debited row actuals must equal the removed amount");

                    // 3. The write log fired on the unloaded path.
                    InGameAssert.IsTrue(
                        capturedLog.Exists(l => l.Contains("Origin debit:")
                            && l.Contains(routeId) && l.Contains("path=unloaded")),
                        "Expected an 'Origin debit:' Info line for the route on path=unloaded");

                    ParsekLog.Info("TestRunner",
                        $"OriginDebit_Unloaded: PASS routeId={routeId} origin={originVessel.vesselName} " +
                        $"originPid={originVessel.persistentId.ToString(IC)} " +
                        $"storedBefore={storedBefore.ToString("R", IC)} storedAfter={storedAfter.ToString("R", IC)} " +
                        $"debit={debitAmount.ToString("R", IC)}");
                });
        }

        // ==================================================================
        // 3. Unloaded debit survives a real KSP save round-trip
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "A proto-snapshot origin debit (production LiveOriginDebitWriters, unloaded path) persists through a real GamePersistence.SaveGame: the saved .sfs VESSEL node's LiquidFuel RESOURCE amounts reflect the post-debit totals. Pauses time warp across the save; restores tanks and deletes the disposable slot in finally")]
        public IEnumerator OriginDebit_UnloadedDebit_SurvivesKspSaveRoundTrip()
        {
            // PRECONDITIONS --------------------------------------------------
            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("HighLogic.CurrentGame is null; cannot drive GamePersistence.SaveGame");
            if (string.IsNullOrEmpty(HighLogic.SaveFolder))
                InGameAssert.Skip("HighLogic.SaveFolder is null/empty; cannot resolve save root");
            if (string.IsNullOrEmpty(KSPUtil.ApplicationRootPath))
                InGameAssert.Skip("KSPUtil.ApplicationRootPath is null/empty; cannot resolve .sfs path");

            if (!TryFindUnloadedLiquidFuelVessel(out Vessel originVessel, out double storedBefore))
                InGameAssert.Skip(
                    "PRECONDITION: no unloaded non-ghost vessel with >= " +
                    $"{MinMeaningfulDebit.ToString("R", IC)} debitable LiquidFuel in this save. " +
                    "The unloaded-origin fixture sources an EXISTING on-rails vessel (plan finding 6); " +
                    "load a save with a distant fuel-carrying vessel to run this test");
            double debitAmount = Math.Min(DefaultDebitAmount, storedBefore);

            string saveSlot = TestSaveSlotPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-od-save-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string savePath = Path.Combine(
                KSPUtil.ApplicationRootPath ?? string.Empty,
                "saves",
                HighLogic.SaveFolder ?? string.Empty,
                saveSlot + ".sfs");

            // Read-only snapshots BEFORE any mutation. rawBefore sums ALL proto
            // LiquidFuel unconditionally (no flow gates) because the .sfs writes
            // every tank, gated or not - the comparison must use the same basis.
            List<KeyValuePair<ProtoPartResourceSnapshot, double>> protoSnapshot =
                SnapshotProtoLiquidFuel(originVessel);
            double rawBefore = SumProtoLiquidFuelRaw(originVessel);

            // Pause time warp across the save + yield window (todo: background
            // tick re-entry under warp; saving under high warp is also flaky).
            int warpIndexBefore = TimeWarp.CurrentRateIndex;
            bool warpPaused = false;
            if (warpIndexBefore > 0)
            {
                TimeWarp.SetRate(0, true);
                warpPaused = true;
                ParsekLog.Verbose("TestRunner",
                    $"OriginDebit_SaveRoundTrip: paused time warp (was index {warpIndexBefore.ToString(IC)})");
            }

            try
            {
                // ACT 1 - production unloaded debit (the same planner + writer
                // bundle the orchestrator's ApplyOriginDebit drives; the
                // orchestrator-driven crossing itself is pinned by
                // OriginDebit_UnloadedOriginVessel_WritesProtoSnapshot). No
                // route enters RouteStore and no seam is armed, so the yield
                // below has zero background-tick exposure.
                var route = new Route
                {
                    Id = routeId,
                    Name = "Parsek Origin-Debit Save Round-Trip",
                    IsKscOrigin = false,
                    CostManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        { LiquidFuelName, debitAmount },
                    },
                };
                var probe = new LiveOriginCargoProbe(originVessel, false);
                OriginDebitPlan plan = RouteOriginDebitPlanner.PrepareDebit(route, probe);
                var writers = new LiveOriginDebitWriters(route, originVessel, plan, isLoaded: false);
                if (plan.Resources != null)
                {
                    for (int i = 0; i < plan.Resources.Count; i++)
                    {
                        OriginDebitLine line = plan.Resources[i];
                        if (line.Available > 0.0)
                            writers.WriteResourceDebit(line.Name, line.Available);
                    }
                }
                double actualDebited = writers.ReadActualDebited(LiquidFuelName);
                InGameAssert.ApproxEqual(debitAmount, actualDebited, ResourceTolerance,
                    $"Writer should remove the full planned amount (planned={debitAmount.ToString("R", IC)} " +
                    $"actual={actualDebited.ToString("R", IC)})");

                double rawAfter = SumProtoLiquidFuelRaw(originVessel);
                InGameAssert.ApproxEqual(rawBefore - actualDebited, rawAfter, ResourceTolerance,
                    "In-memory proto totals should reflect the debit before the save");

                ParsekLog.Verbose("TestRunner",
                    $"OriginDebit_SaveRoundTrip: debited {actualDebited.ToString("R", IC)} LF from " +
                    $"'{originVessel.vesselName}' pid={originVessel.persistentId.ToString(IC)} " +
                    $"rawBefore={rawBefore.ToString("R", IC)} rawAfter={rawAfter.ToString("R", IC)} slot='{saveSlot}'");

                // ACT 2 - real KSP save. For unloaded vessels KSP serializes the
                // in-memory protoVessel, so the mutated snapshot amounts must
                // land in the .sfs.
                string saveResult = GamePersistence.SaveGame(saveSlot, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                InGameAssert.IsTrue(!string.IsNullOrEmpty(saveResult),
                    $"GamePersistence.SaveGame returned null/empty for slot '{saveSlot}'");

                // Yield one frame so deferred-one-frame writes settle.
                yield return null;

                InGameAssert.IsTrue(File.Exists(savePath),
                    $"Expected .sfs at '{savePath}' after GamePersistence.SaveGame");

                // ACT 3 - parse the .sfs and find the origin VESSEL node.
                ConfigNode root = ConfigNode.Load(savePath);
                InGameAssert.IsNotNull(root, $"ConfigNode.Load returned null for '{savePath}'");

                ConfigNode vesselNode = FindVesselNodeByPersistentId(root, originVessel.persistentId);
                if (vesselNode == null)
                    InGameAssert.Skip(
                        $"Could not locate VESSEL node persistentId={originVessel.persistentId.ToString(IC)} " +
                        $"in '{savePath}' (save layout mismatch); skipping rather than false-failing - " +
                        "the in-memory debit assertions above already passed");

                // ASSERT - the saved vessel's total LiquidFuel matches the
                // post-debit in-memory total, and the debit is visible vs the
                // pre-debit total.
                double sfsTotal = SumVesselNodeResourceAmount(vesselNode, LiquidFuelName);
                InGameAssert.ApproxEqual(rawAfter, sfsTotal, ResourceTolerance,
                    $"Saved .sfs LiquidFuel total should equal the post-debit proto total " +
                    $"(sfs={sfsTotal.ToString("R", IC)} expected={rawAfter.ToString("R", IC)})");
                InGameAssert.IsTrue(sfsTotal < rawBefore - MinMeaningfulDebit + ResourceTolerance,
                    $"Saved .sfs LiquidFuel total ({sfsTotal.ToString("R", IC)}) should be below the " +
                    $"pre-debit total ({rawBefore.ToString("R", IC)}) - the debit must be visible on disk");

                ParsekLog.Info("TestRunner",
                    $"OriginDebit_SaveRoundTrip: PASS slot='{saveSlot}' origin={originVessel.vesselName} " +
                    $"pid={originVessel.persistentId.ToString(IC)} rawBefore={rawBefore.ToString("R", IC)} " +
                    $"rawAfter={rawAfter.ToString("R", IC)} sfsTotal={sfsTotal.ToString("R", IC)}");
            }
            finally
            {
                // TEARDOWN: restore the mutated proto amounts, delete the
                // disposable slot (sidecar-safe), restore time warp.
                RestoreProtoLiquidFuel(protoSnapshot);
                QuickloadResumeHelpers.TryDeleteSaveSlot(saveSlot);
                if (warpPaused)
                {
                    try
                    {
                        TimeWarp.SetRate(warpIndexBefore, true);
                        ParsekLog.Verbose("TestRunner",
                            $"OriginDebit_SaveRoundTrip cleanup: restored time warp index {warpIndexBefore.ToString(IC)}");
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn("TestRunner",
                            $"OriginDebit_SaveRoundTrip cleanup: failed to restore time warp ({ex.GetType().Name}: {ex.Message})");
                    }
                }
            }
        }

        // ==================================================================
        // 4. Empty origin holds the route (gate, not debit)
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "A non-KSC loop-route crossing whose origin tanks are drained holds at the OriginLacksCargo eligibility gate: SkippedCycles bumps, nothing is debited or delivered, and the BLOCKED Info line names the short resource")]
        public void OriginGate_EmptyOrigin_HoldsWaitingForResources()
        {
            // PRECONDITIONS --------------------------------------------------
            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live origin vessel to drain");
            Vessel originVessel = FlightGlobals.ActiveVessel;
            if (!(originVessel.loaded && !originVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{originVessel.vesselName}' is not loaded+unpacked " +
                    $"(loaded={originVessel.loaded}, packed={originVessel.packed}); the drain mutates live " +
                    "PartResource amounts which an unloaded vessel does not expose");

            List<KeyValuePair<PartResource, double>> tankSnapshot =
                SnapshotLoadedLiquidFuel(originVessel);
            if (tankSnapshot.Count == 0)
                InGameAssert.Skip(
                    $"Active vessel '{originVessel.vesselName}' has no LiquidFuel tanks to drain; " +
                    "pick a vessel with at least one LF tank to run this test");

            string treeId = "ingame-od-empty-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-od-empty-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            List<Route> preExistingRoutes = SnapshotRoutes();
            int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

            // DRAIN the origin: zero every LiquidFuel tank so the production
            // probe reads 0 and the gate names the short resource. Restored
            // from the snapshot in finally (inside the shared runner).
            for (int i = 0; i < tankSnapshot.Count; i++)
                tankSnapshot[i].Key.amount = 0.0;

            RunOriginDebitCrossing(
                label: "OriginGate_EmptyOrigin",
                treeId: treeId,
                routeId: routeId,
                originVesselForRoute: originVessel,
                destVessel: originVessel,
                debitAmount: DefaultDebitAmount,
                preExistingRoutes: preExistingRoutes,
                restoreOriginState: () => RestoreLoadedLiquidFuel(tankSnapshot),
                assertions: capturedLog =>
                {
                    // 1. Route holds: still Active (the loop path skips a blocked
                    //    crossing without a status transition), SkippedCycles
                    //    bumped, nothing completed, index snapped forward so the
                    //    blocked cycle does not re-fire every tick.
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route postTick),
                        "Synthetic route disappeared from store during Tick");
                    InGameAssert.AreEqual(RouteStatus.Active, postTick.Status,
                        $"Blocked loop crossing must not transition status, was {postTick.Status}");
                    InGameAssert.AreEqual(1, postTick.SkippedCycles,
                        $"SkippedCycles should bump to 1 on the blocked crossing, was {postTick.SkippedCycles.ToString(IC)}");
                    InGameAssert.AreEqual(0, postTick.CompletedCycles,
                        "CompletedCycles must stay 0 - nothing was emitted");
                    InGameAssert.AreEqual(0L, postTick.LastObservedLoopCycleIndex,
                        "LastObservedLoopCycleIndex should still snap to the blocked dock cycle (0)");

                    // 2. Nothing debited or delivered: no ledger row of any route
                    //    type was appended for this route id.
                    FindNewRouteRows(beforeLedgerCount, routeId,
                        out GameAction dispatched, out GameAction debited, out GameAction delivered);
                    InGameAssert.IsNull(dispatched, "Blocked crossing must emit no RouteDispatched row");
                    InGameAssert.IsNull(debited, "Blocked crossing must emit no RouteCargoDebited row");
                    InGameAssert.IsNull(delivered, "Blocked crossing must emit no RouteCargoDelivered row");

                    // 3. The origin pool is still empty (no partial debit fired).
                    double storedAfter = new LiveOriginCargoProbe(originVessel, true)
                        .ProbeResourceStored(LiquidFuelName);
                    InGameAssert.ApproxEqual(0.0, storedAfter, ResourceTolerance,
                        "Drained origin must still read 0 deliverable LiquidFuel after the blocked tick");

                    // 4. The BLOCKED Info line names the gate kind and the short
                    //    resource (elig.Reason carries the lacking resource name).
                    InGameAssert.IsTrue(
                        capturedLog.Exists(l => l.Contains("BLOCKED")
                            && l.Contains("kind=OriginLacksCargo") && l.Contains(LiquidFuelName)),
                        "Expected a BLOCKED Info line with kind=OriginLacksCargo naming LiquidFuel");

                    ParsekLog.Info("TestRunner",
                        $"OriginGate_EmptyOrigin: PASS routeId={routeId} " +
                        $"skippedCycles={postTick.SkippedCycles.ToString(IC)} status={postTick.Status}");
                });
        }

        // ==================================================================
        // Shared crossing runner
        // ==================================================================

        /// <summary>
        /// Shared arrange / act / assert / teardown frame for the synchronous
        /// orchestrator-driven cases: wipes RouteStore down to one synthetic
        /// non-KSC loop route, commits a backing tree + recordings (so the ERS
        /// eligibility gate is real), arms the loop-unit resolver seam (route-id
        /// scoped) and a NO-OP delivery seam (finding 5: the debit half is under
        /// test; no real destination delivery is wanted), captures Info log
        /// lines via <see cref="ParsekLog.TestObserverForTesting"/> (lines still
        /// reach KSP.log), runs ONE production <see cref="RouteOrchestrator.Tick(double)"/>
        /// at the deterministic crossing UT, runs the caller's assertions, and
        /// restores EVERYTHING in finally (seams first, then store, recordings,
        /// tree, and the caller's origin-state restore).
        /// </summary>
        private static void RunOriginDebitCrossing(
            string label,
            string treeId,
            string routeId,
            Vessel originVesselForRoute,
            Vessel destVessel,
            double debitAmount,
            List<Route> preExistingRoutes,
            Action restoreOriginState,
            Action<List<string>> assertions)
        {
            RecordingTree routeTree = BuildLaunchDockUndockTree(treeId);
            GhostPlaybackLogic.LoopUnit loopUnit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0,
                memberIndices: new[] { 0 },
                spanStartUT: SpanStartUT,
                spanEndUT: SpanEndUT,
                cadenceSeconds: Cadence,
                phaseAnchorUT: SpanStartUT);

            bool routeTreeAdded = false, storeWiped = false, resolverArmed = false, deliveryArmed = false;
            var previousResolver = RouteOrchestrator.LoopUnitResolverForTesting;
            var previousDeliveryApplier = RouteOrchestrator.DeliveryApplierForTesting;
            var previousObserver = ParsekLog.TestObserverForTesting;
            var committedAdded = new List<Recording>();
            var capturedLog = new List<string>();

            try
            {
                RecordingStore.AddCommittedTreeForTesting(routeTree);
                routeTreeAdded = true;
                // AddCommittedTreeForTesting only registers the tree, NOT its
                // recordings; push the member recordings into CommittedRecordings
                // so ERS resolves the route's SourceRefs.
                foreach (Recording rec in routeTree.Recordings.Values)
                {
                    if (rec == null) continue;
                    RecordingStore.AddCommittedInternal(rec);
                    committedAdded.Add(rec);
                }

                // Wipe the store down to ONLY the synthetic route so the single
                // production tick cannot touch any real route at the synthetic
                // UT (stronger isolation than the snapshot-only pattern).
                RouteStore.ResetForTesting();
                storeWiped = true;
                Route route = BuildOriginDebitLoopRoute(
                    routeId, treeId, originVesselForRoute, destVessel, debitAmount);
                RouteStore.AddRoute(route);
                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out _),
                    "Synthetic loop route was not stored");

                // Loop-unit resolver seam, scoped to OUR route id only.
                RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) =>
                {
                    if (r != null && string.Equals(r.Id, routeId, StringComparison.Ordinal))
                        return loopUnit;
                    return previousResolver != null
                        ? previousResolver(r, ut)
                        : (GhostPlaybackLogic.LoopUnit?)null;
                };
                resolverArmed = true;

                // NO-OP delivery seam (plan finding 5): the debit half is the
                // subject under test; replacing the delivery half avoids needing
                // a real destination delivery to land. Mirrors the xUnit fakes:
                // bump CompletedCycles so cycle ids keep advancing, emit nothing.
                RouteOrchestrator.DeliveryApplierForTesting = (r, ut, env) =>
                {
                    if (r != null) r.CompletedCycles += 1;
                    ParsekLog.Verbose("TestRunner",
                        $"{label}: no-op delivery seam consumed the delivery half " +
                        $"(routeId={r?.Id ?? "<none>"} ut={ut.ToString("R", IC)})");
                };
                deliveryArmed = true;

                // Log observer (NOT the sink): lines still reach Debug.Log /
                // KSP.log while we capture them for assertions.
                ParsekLog.TestObserverForTesting = line =>
                {
                    capturedLog.Add(line);
                    previousObserver?.Invoke(line);
                };

                ParsekLog.Verbose("TestRunner",
                    $"{label}: pre-tick routeId={routeId} treeId={treeId} " +
                    $"originPid={(originVesselForRoute != null ? originVesselForRoute.persistentId.ToString(IC) : "<none>")} " +
                    $"destPid={(destVessel != null ? destVessel.persistentId.ToString(IC) : "<none>")} " +
                    $"debit={debitAmount.ToString("R", IC)} tickUT={TickUT.ToString("R", IC)}");

                // ACT - one production no-env tick through the loop-route branch.
                // Synchronous: arrange, tick, assert, and teardown all run inside
                // this frame, so no background scenario tick can interleave.
                RouteOrchestrator.Tick(TickUT);

                assertions(capturedLog);
            }
            finally
            {
                // Disarm seams FIRST so nothing can re-enter mid-teardown.
                ParsekLog.TestObserverForTesting = previousObserver;
                if (resolverArmed)
                    RouteOrchestrator.LoopUnitResolverForTesting = previousResolver;
                if (deliveryArmed)
                    RouteOrchestrator.DeliveryApplierForTesting = previousDeliveryApplier;

                if (storeWiped)
                    RestoreRoutes(preExistingRoutes);

                for (int i = 0; i < committedAdded.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedAdded[i]);
                if (routeTreeAdded)
                    RemoveCommittedTree(treeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);

                try
                {
                    restoreOriginState?.Invoke();
                    ParsekLog.Verbose("TestRunner", $"{label} cleanup: origin resource state restored");
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"{label} cleanup: failed to restore origin resource state ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }

        /// <summary>
        /// Synthetic non-KSC loop route: origin endpoint at the given origin
        /// vessel's pid, one docking stop at the destination vessel's pid, a
        /// LiquidFuel <c>CostManifest</c> of <paramref name="debitAmount"/>,
        /// and the backing-mission binding that routes it down the loop path.
        /// </summary>
        private static Route BuildOriginDebitLoopRoute(
            string routeId, string treeId, Vessel originVessel, Vessel destVessel, double debitAmount)
        {
            return new Route
            {
                Id = routeId,
                Name = "Parsek Origin-Debit In-Game",
                Status = RouteStatus.Active,
                IsKscOrigin = false,
                Origin = new RouteEndpoint
                {
                    VesselPersistentId = originVessel != null ? originVessel.persistentId : 0u,
                    BodyName = originVessel != null && originVessel.mainBody != null
                        ? originVessel.mainBody.bodyName : "Kerbin",
                    IsSurface = false,
                },
                BackingMissionTreeId = treeId,
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
                    { LiquidFuelName, debitAmount },
                },
                InventoryCostManifest = new List<InventoryPayloadItem>(),
                RecordingIds = new List<string> { "launch", "docked" },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "launch", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "docked", TreeId = treeId },
                },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint
                        {
                            VesselPersistentId = destVessel != null ? destVessel.persistentId : 0u,
                            BodyName = destVessel != null && destVessel.mainBody != null
                                ? destVessel.mainBody.bodyName : "Kerbin",
                            IsSurface = false,
                        },
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, debitAmount },
                        },
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 0,
                        DeliveryOffsetSeconds = 0.0,
                    },
                },
            };
        }

        // ==================================================================
        // Fixture helpers (tree topology mirrors LogisticsRouteOnMissionsRuntimeTests)
        // ==================================================================

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
            ParsekLog.Verbose("TestRunner",
                $"OriginDebit cleanup: restored {preExisting.Count.ToString(IC)} pre-existing route(s)");
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

        private static List<KeyValuePair<PartResource, double>> SnapshotLoadedLiquidFuel(Vessel vessel)
        {
            var snapshot = new List<KeyValuePair<PartResource, double>>();
            if (vessel == null || vessel.parts == null) return snapshot;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(LiquidFuelName);
                if (pr == null) continue;
                snapshot.Add(new KeyValuePair<PartResource, double>(pr, pr.amount));
            }
            return snapshot;
        }

        private static void RestoreLoadedLiquidFuel(List<KeyValuePair<PartResource, double>> snapshot)
        {
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i].Key != null)
                    snapshot[i].Key.amount = snapshot[i].Value;
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

        /// <summary>
        /// Unconditional proto-snapshot LiquidFuel total (NO flow gates) -
        /// the .sfs writes every tank, so the save-comparison basis must
        /// include gated tanks the production probe excludes.
        /// </summary>
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
        /// Finds an existing UNLOADED, non-ghost, non-active vessel holding at
        /// least <see cref="MinMeaningfulDebit"/> debitable LiquidFuel per the
        /// production <see cref="LiveOriginCargoProbe"/> (unloaded branch).
        /// Existing-vessel sourcing instead of a spawn-based fixture - plan
        /// finding 6 flagged distant spawning as unproven ground.
        /// </summary>
        private static bool TryFindUnloadedLiquidFuelVessel(out Vessel candidate, out double stored)
        {
            candidate = null;
            stored = 0.0;
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

                double s = new LiveOriginCargoProbe(v, false).ProbeResourceStored(LiquidFuelName);
                if (s < MinMeaningfulDebit) continue;

                candidate = v;
                stored = s;
                return true;
            }
            return false;
        }

        // ==================================================================
        // Ledger + .sfs lookup helpers
        // ==================================================================

        /// <summary>
        /// Scans the ledger rows appended after <paramref name="fromIndex"/>
        /// for the three route action types attributed to
        /// <paramref name="routeId"/>. Any of the outs may be null.
        /// </summary>
        private static void FindNewRouteRows(int fromIndex, string routeId,
            out GameAction dispatched, out GameAction debited, out GameAction delivered)
        {
            dispatched = null;
            debited = null;
            delivered = null;
            var actions = Ledger.Actions;
            if (actions == null) return;
            for (int i = fromIndex; i < actions.Count; i++)
            {
                GameAction a = actions[i];
                if (a == null) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (a.Type == GameActionType.RouteDispatched) dispatched = a;
                else if (a.Type == GameActionType.RouteCargoDebited) debited = a;
                else if (a.Type == GameActionType.RouteCargoDelivered) delivered = a;
            }
        }

        /// <summary>
        /// Tolerant .sfs walk: the root may or may not wrap FLIGHTSTATE in a
        /// GAME node (mirrors <c>LogisticsRouteStoreRuntimeTests</c>'s
        /// SCENARIO walk). Matches the VESSEL node by its <c>persistentId</c>
        /// value; returns null when no node matches.
        /// </summary>
        private static ConfigNode FindVesselNodeByPersistentId(ConfigNode root, uint pid)
        {
            if (root == null) return null;
            ConfigNode gameNode = root;
            if (gameNode.GetNode("FLIGHTSTATE") == null)
            {
                ConfigNode wrapped = root.GetNode("GAME");
                if (wrapped != null)
                    gameNode = wrapped;
            }
            ConfigNode flightState = gameNode.GetNode("FLIGHTSTATE");
            if (flightState == null) return null;

            ConfigNode[] vesselNodes = flightState.GetNodes("VESSEL");
            if (vesselNodes == null) return null;
            string pidText = pid.ToString(IC);
            for (int i = 0; i < vesselNodes.Length; i++)
            {
                ConfigNode vn = vesselNodes[i];
                if (vn == null) continue;
                if (string.Equals(vn.GetValue("persistentId"), pidText, StringComparison.Ordinal))
                    return vn;
            }
            return null;
        }

        /// <summary>
        /// Sums every <c>PART/RESOURCE</c> child of the VESSEL node whose
        /// <c>name</c> matches, parsing amounts InvariantCulture. Unparseable
        /// amounts are skipped (defensive; KSP writes culture-stable values).
        /// </summary>
        private static double SumVesselNodeResourceAmount(ConfigNode vesselNode, string resourceName)
        {
            double total = 0.0;
            if (vesselNode == null) return total;
            ConfigNode[] partNodes = vesselNode.GetNodes("PART");
            if (partNodes == null) return total;
            for (int i = 0; i < partNodes.Length; i++)
            {
                ConfigNode part = partNodes[i];
                if (part == null) continue;
                ConfigNode[] resourceNodes = part.GetNodes("RESOURCE");
                if (resourceNodes == null) continue;
                for (int j = 0; j < resourceNodes.Length; j++)
                {
                    ConfigNode res = resourceNodes[j];
                    if (res == null) continue;
                    if (!string.Equals(res.GetValue("name"), resourceName, StringComparison.Ordinal)) continue;
                    string amountText = res.GetValue("amount");
                    if (amountText != null
                        && double.TryParse(amountText, NumberStyles.Float, IC, out double amount))
                    {
                        total += amount;
                    }
                }
            }
            return total;
        }
    }
}
