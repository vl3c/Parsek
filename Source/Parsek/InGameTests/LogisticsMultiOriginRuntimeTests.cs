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
    /// M4b Phase B4 in-game close-out for MULTI-ORIGIN runs + cargo escrow (plan
    /// `docs/dev/plan-logistics-m4-shape-generality.md` Phase B4, gameplay
    /// grounding 5.2 the shuttle: refinery-load + station-deliver in one
    /// recording, and 5.3 multi-origin consolidation: load at depot A + load at
    /// depot B then deliver 300 at the station). The pure xUnit suites cover the
    /// N-source gate math (group-by-pid + sum + first-short), the escrow
    /// reserve/release/drop/net, and the per-window firing decision; these tests
    /// pin the part only live KSP can exercise: a multi-window route firing the
    /// per-window SOURCE debits through the production
    /// <see cref="RouteOrchestrator.Tick(double)"/> -&gt;
    /// <see cref="RouteOrchestrator.ProcessMultiStopCrossings"/> -&gt;
    /// <see cref="RouteOrchestrator.EmitPickupHalf"/> -&gt;
    /// <see cref="RouteOrchestrator.ApplyPickupDebit"/> path against LIVE source
    /// vessels (the auto-spawned depots), the all-or-nothing source hold naming a
    /// dry depot, the cargo-escrow competing-route hold, a real
    /// <see cref="GamePersistence.SaveGame"/> round-trip of an in-flight cycle, and
    /// a B1-review regression that the new gate did not break an M3a single-window
    /// pickup route.
    ///
    /// <para><b>Source provisioning.</b> The two consolidation depots and the
    /// single shuttle refinery are auto-spawned unloaded LiquidFuel vessels via
    /// <see cref="UnloadedFuelVesselFixture.EnsureUnloadedLiquidFuelVessel"/>
    /// (a fresh-identity pad-rocket copy in a parking orbit, validated live in the
    /// M4a / pickup playtests) so the player only needs a fueled pad rocket; the
    /// fixture is torn down in finally. When a depot cannot be provided the test
    /// <see cref="InGameAssert.Skip"/>s with a named reason - never a hard fail on
    /// missing setup. The escrow-hold + M3a-regression cases that drive only the
    /// pure gate / a single source reuse the active vessel.</para>
    ///
    /// <para><b>Re-entry discipline + post-restore unpack wait</b> (todo
    /// "background RouteOrchestrator.Tick can re-enter a logistics test's synthetic
    /// route", same contract as <see cref="LogisticsMultiStopRuntimeTests"/> /
    /// <see cref="LogisticsPickupRuntimeTests"/>): the orchestrator-driven cases
    /// yield ONLY in the precondition unpack + source-spawn waits BEFORE any seam
    /// is armed or any store mutated; the whole arrange / Tick / assert / teardown
    /// sequence then runs yield-free on the main thread, so the background 1 Hz
    /// scenario tick can never interleave with an armed seam or a stored synthetic
    /// route. The save round-trip test pauses time warp + disarms the resolver
    /// seam BEFORE the save. AllowBatchExecution=false +
    /// RestoreBatchFlightBaselineAfterExecution=true.</para>
    ///
    /// <para><b>Deterministic span clock.</b> Span [1000,3000], cadence == span,
    /// anchor == spanStart. The consolidation route's two pickup docks sit at loop
    /// UT 1500 (depot A) and 2000 (depot B), with the delivery dock at 2500 (the
    /// LAST dock). A tick at 2500 reaches every dock phase for cycle 0, so
    /// ProcessMultiStopCrossings fires both source debits + the delivery under ONE
    /// cycleId and bumps CompletedCycles exactly once. The shuttle uses two docks
    /// (refinery load at 1500, station deliver at 2500).</para>
    /// </summary>
    public sealed class LogisticsMultiOriginRuntimeTests
    {
        private const string LiquidFuelName = "LiquidFuel";
        private const double PickupAmountA = 4.0;
        private const double PickupAmountB = 6.0;
        private const double ShuttleLoadAmount = 5.0;
        private const double ResourceTolerance = 0.01;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // Auto-spawn fixture floors: a depot must hold enough debitable LiquidFuel
        // to cover its pickup window with margin; free capacity is irrelevant for a
        // pickup SOURCE (we drain it), so the floor is small but positive so the
        // fixture's headroom guard passes.
        private const double DepotAMinStored = PickupAmountA + 5.0;
        private const double DepotBMinStored = PickupAmountB + 5.0;
        private const double ShuttleSourceMinStored = ShuttleLoadAmount + 5.0;
        private const double FixtureMinFreeCapacity = 1.0;

        private const string TestSaveSlotPrefix = "parsek_multiorigin_ingame_test_";

        private const string IsolatedOnlyBatchSkipReason =
            "Isolated-run only - mutates RouteStore, Ledger, RecordingStore committed trees, " +
            "RouteOrchestrator test seams, the RouteStore cargo escrow, and live vessel resource " +
            "state under live KSP statics; excluded from ordinary Run All / Run category. Use Run " +
            "All + Isolated or the row play button in a disposable FLIGHT session.";

        // Deterministic span clock (same shape as the M4a multi-stop suite).
        private const double SpanStartUT = 1000.0;
        private const double SpanEndUT = 3000.0;
        private const double DockUtA = 1500.0;   // pickup depot A
        private const double DockUtB = 2000.0;   // pickup depot B
        private const double DockUtStation = 2500.0; // delivery station (the LAST dock)
        private const double Cadence = SpanEndUT - SpanStartUT;
        private const double TickUtAllDocks = 2500.0; // one tick reaches every dock phase

        // ==================================================================
        // 1. Multi-origin consolidation (5.3): A + B -> station
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "A multi-ORIGIN route with TWO pickup sources (depot A + depot B, each an auto-spawned unloaded vessel) and a delivery stop, crossing through RouteOrchestrator.Tick (ProcessMultiStopCrossings -> EmitPickupHalf -> ApplyPickupDebit), debits BOTH depots at their window phases (depot A's LiquidFuel drops by its window manifest, depot B's by its own), credits the consolidated cargo at the station window, and lands TWO RouteCargoPickedUp rows (one per source stop) plus the RouteDispatched row under ONE cycle with CompletedCycles == 1. Auto-spawns the two depots; skips with a named reason when either cannot be provided")]
        public IEnumerator MultiOrigin_TwoSources_BothDebitedAndConsolidated()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            // Provision depot A then depot B as separate auto-spawned unloaded
            // vessels (distinct pids so the gate groups them as two sources).
            var fixtureA = new UnloadedFuelVesselFixture.EnsureResult();
            var fixtureB = new UnloadedFuelVesselFixture.EnsureResult();
            try
            {
                IEnumerator ensureA = UnloadedFuelVesselFixture.EnsureUnloadedLiquidFuelVessel(
                    DepotAMinStored, FixtureMinFreeCapacity, fixtureA);
                while (ensureA.MoveNext())
                    yield return ensureA.Current;
                if (fixtureA.Vessel == null)
                    InGameAssert.Skip(
                        "PRECONDITION: could not provide unloaded depot A (>= " +
                        $"{DepotAMinStored.ToString("R", IC)} LF). Provide a fueled PRELAUNCH pad rocket");

                // Provision depot B excluding depot A's pid so the reuse fast-path
                // cannot hand back the just-spawned depot A; that forces a fresh
                // distinct-pid spawn (preserveIdentity:false mints a new identity).
                HashSet<uint> excludeForB = BuildExcludeSet(fixtureA);
                IEnumerator ensureB = UnloadedFuelVesselFixture.EnsureUnloadedLiquidFuelVessel(
                    DepotBMinStored, FixtureMinFreeCapacity, fixtureB, excludeForB);
                while (ensureB.MoveNext())
                    yield return ensureB.Current;
                if (fixtureB.Vessel == null)
                    InGameAssert.Skip(
                        "PRECONDITION: could not provide unloaded depot B (>= " +
                        $"{DepotBMinStored.ToString("R", IC)} LF). Provide a fueled PRELAUNCH pad rocket");

                Vessel depotA = fixtureA.Vessel;
                Vessel depotB = fixtureB.Vessel;
                if (depotA.persistentId == depotB.persistentId)
                    InGameAssert.Skip(
                        "Depot A and depot B resolved to the SAME pid; the two-source gate needs distinct " +
                        "source vessels (re-run; the spawn should mint fresh identities)");

                // A delivery endpoint: reuse depot A's body so the route is same-body
                // (the delivery stop just needs to resolve; we assert the SOURCE debits).
                Vessel deliveryTarget = FlightGlobals.ActiveVessel ?? depotA;

                string treeId = "ingame-mo-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string routeId = "ingame-mo-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

                double depotAStoredBefore = new LiveOriginCargoProbe(depotA, false).ProbeResourceStored(LiquidFuelName);
                double depotBStoredBefore = new LiveOriginCargoProbe(depotB, false).ProbeResourceStored(LiquidFuelName);
                var protoSnapA = SnapshotProtoLiquidFuel(depotA);
                var protoSnapB = SnapshotProtoLiquidFuel(depotB);
                // The delivery half credits the (loaded) delivery target; snapshot it
                // so finally leaves no resource litter on the player's active vessel.
                var deliveryTankSnap = SnapshotLoadedLiquidFuel(deliveryTarget);

                List<Route> preExistingRoutes = SnapshotRoutes();
                int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

                RunMultiStopCrossing(
                    label: "MultiOrigin_Consolidation",
                    treeId: treeId,
                    routeId: routeId,
                    tickUT: TickUtAllDocks,
                    preExistingRoutes: preExistingRoutes,
                    buildRoute: () => BuildConsolidationRoute(routeId, treeId, depotA, depotB, deliveryTarget),
                    restoreState: () =>
                    {
                        RestoreProtoLiquidFuel(protoSnapA);
                        RestoreProtoLiquidFuel(protoSnapB);
                        RestoreLoadedLiquidFuel(deliveryTankSnap);
                    },
                    assertions: capturedLog =>
                    {
                        InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route postTick),
                            "Multi-origin route disappeared from store during Tick");
                        InGameAssert.AreEqual(1, postTick.CompletedCycles,
                            "CompletedCycles should be exactly 1 after a single multi-origin cycle");
                        InGameAssert.AreEqual(0, postTick.SkippedCycles,
                            "SkippedCycles should stay 0 when both depots are stocked");

                        // Both depots physically debited their window manifests.
                        double depotAStoredAfter = new LiveOriginCargoProbe(depotA, false).ProbeResourceStored(LiquidFuelName);
                        double depotBStoredAfter = new LiveOriginCargoProbe(depotB, false).ProbeResourceStored(LiquidFuelName);
                        InGameAssert.ApproxEqual(depotAStoredBefore - PickupAmountA, depotAStoredAfter, ResourceTolerance,
                            $"Depot A LiquidFuel should drop by {PickupAmountA.ToString("R", IC)} " +
                            $"(before={depotAStoredBefore.ToString("R", IC)} after={depotAStoredAfter.ToString("R", IC)})");
                        InGameAssert.ApproxEqual(depotBStoredBefore - PickupAmountB, depotBStoredAfter, ResourceTolerance,
                            $"Depot B LiquidFuel should drop by {PickupAmountB.ToString("R", IC)} " +
                            $"(before={depotBStoredBefore.ToString("R", IC)} after={depotBStoredAfter.ToString("R", IC)})");

                        // Ledger: one dispatch, two RouteCargoPickedUp rows (one per
                        // source stop, distinct stop indices) - the per-window replay
                        // key (RANK-1) would otherwise suppress the second source.
                        CountNewRouteRows(beforeLedgerCount, routeId,
                            out int dispatchedCount, out int pickedUpCount, out var pickedUpStopIndices,
                            out int deliveredCount);
                        InGameAssert.AreEqual(1, dispatchedCount,
                            "Expected exactly one RouteDispatched row (dispatch fires once per multi-origin cycle)");
                        InGameAssert.AreEqual(2, pickedUpCount,
                            "Expected TWO RouteCargoPickedUp rows (one per source window); fewer means a source " +
                            "was suppressed by the per-window replay key");
                        InGameAssert.IsTrue(pickedUpStopIndices.Contains(0) && pickedUpStopIndices.Contains(1),
                            "The two pickup rows must carry DISTINCT source stop indices 0 and 1");

                        ParsekLog.Info("TestRunner",
                            $"MultiOrigin_Consolidation: PASS routeId={routeId} " +
                            $"depotA={depotA.vesselName} pid={depotA.persistentId.ToString(IC)} " +
                            $"depotADrop={(depotAStoredBefore - depotAStoredAfter).ToString("R", IC)} " +
                            $"depotB={depotB.vesselName} pid={depotB.persistentId.ToString(IC)} " +
                            $"depotBDrop={(depotBStoredBefore - depotBStoredAfter).ToString("R", IC)} " +
                            $"pickedUpRows={pickedUpCount.ToString(IC)} deliveredRows={deliveredCount.ToString(IC)} " +
                            $"completedCycles={postTick.CompletedCycles.ToString(IC)}");
                    });
            }
            finally
            {
                UnloadedFuelVesselFixture.Cleanup(fixtureB);
                UnloadedFuelVesselFixture.Cleanup(fixtureA);
            }
            yield break;
        }

        // ==================================================================
        // 2. All-or-nothing source gate: one short depot HOLDS naming it
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "A multi-origin route whose depot B is drained dry below its pickup window holds the WHOLE cycle (all-or-nothing at the source, 19.2.5): the cycle dispatches nothing, debits NEITHER depot (depot A is NOT drained because the cycle never committed), SkippedCycles bumps once, and the route records a hold whose token names the short source. Auto-spawns depot A stocked + depot B dry-able; skips when either cannot be provided")]
        public IEnumerator MultiOrigin_OneSourceShort_HoldsNamingSource()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            var fixtureA = new UnloadedFuelVesselFixture.EnsureResult();
            var fixtureB = new UnloadedFuelVesselFixture.EnsureResult();
            try
            {
                IEnumerator ensureA = UnloadedFuelVesselFixture.EnsureUnloadedLiquidFuelVessel(
                    DepotAMinStored, FixtureMinFreeCapacity, fixtureA);
                while (ensureA.MoveNext())
                    yield return ensureA.Current;
                if (fixtureA.Vessel == null)
                    InGameAssert.Skip("PRECONDITION: could not provide stocked depot A. Provide a fueled PRELAUNCH pad rocket");

                // Exclude depot A's pid so depot B spawns distinct (see test 1).
                HashSet<uint> excludeForB = BuildExcludeSet(fixtureA);
                IEnumerator ensureB = UnloadedFuelVesselFixture.EnsureUnloadedLiquidFuelVessel(
                    DepotBMinStored, FixtureMinFreeCapacity, fixtureB, excludeForB);
                while (ensureB.MoveNext())
                    yield return ensureB.Current;
                if (fixtureB.Vessel == null)
                    InGameAssert.Skip("PRECONDITION: could not provide depot B. Provide a fueled PRELAUNCH pad rocket");

                Vessel depotA = fixtureA.Vessel;
                Vessel depotB = fixtureB.Vessel;
                if (depotA.persistentId == depotB.persistentId)
                    InGameAssert.Skip("Depots resolved to the same pid; need two distinct sources");

                Vessel deliveryTarget = FlightGlobals.ActiveVessel ?? depotA;

                // Drain depot B below its pickup window so the all-or-nothing gate
                // holds. Snapshot first so finally restores it.
                var protoSnapA = SnapshotProtoLiquidFuel(depotA);
                var protoSnapB = SnapshotProtoLiquidFuel(depotB);
                DrainProtoLiquidFuel(depotB);
                double depotBStoredAfterDrain = new LiveOriginCargoProbe(depotB, false).ProbeResourceStored(LiquidFuelName);
                if (depotBStoredAfterDrain >= PickupAmountB)
                    InGameAssert.Skip(
                        $"Could not drain depot B below its pickup window (still {depotBStoredAfterDrain.ToString("R", IC)} LF " +
                        $">= {PickupAmountB.ToString("R", IC)}); cannot exercise the short-source hold");
                double depotAStoredBefore = new LiveOriginCargoProbe(depotA, false).ProbeResourceStored(LiquidFuelName);

                string treeId = "ingame-mo-short-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string routeId = "ingame-mo-short-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

                List<Route> preExistingRoutes = SnapshotRoutes();
                int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

                RunMultiStopCrossing(
                    label: "MultiOrigin_ShortSource",
                    treeId: treeId,
                    routeId: routeId,
                    tickUT: TickUtAllDocks,
                    preExistingRoutes: preExistingRoutes,
                    buildRoute: () => BuildConsolidationRoute(routeId, treeId, depotA, depotB, deliveryTarget),
                    restoreState: () =>
                    {
                        RestoreProtoLiquidFuel(protoSnapA);
                        RestoreProtoLiquidFuel(protoSnapB);
                    },
                    assertions: capturedLog =>
                    {
                        InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route postTick),
                            "Multi-origin route disappeared during Tick");
                        InGameAssert.AreEqual(0, postTick.CompletedCycles,
                            "A short-source cycle must NOT complete (all-or-nothing holds at dispatch)");
                        InGameAssert.IsTrue(postTick.SkippedCycles >= 1,
                            "A blocked cycle must bump SkippedCycles at least once");

                        // Depot A must NOT be drained - the cycle never committed.
                        double depotAStoredAfter = new LiveOriginCargoProbe(depotA, false).ProbeResourceStored(LiquidFuelName);
                        InGameAssert.ApproxEqual(depotAStoredBefore, depotAStoredAfter, ResourceTolerance,
                            "Depot A must NOT be debited when the cycle is held all-or-nothing on depot B " +
                            $"(before={depotAStoredBefore.ToString("R", IC)} after={depotAStoredAfter.ToString("R", IC)})");

                        // No pickup / dispatch rows landed for the held cycle.
                        CountNewRouteRows(beforeLedgerCount, routeId,
                            out int dispatchedCount, out int pickedUpCount, out _, out int deliveredCount);
                        InGameAssert.AreEqual(0, dispatchedCount, "A held cycle emits no RouteDispatched row");
                        InGameAssert.AreEqual(0, pickedUpCount, "A held cycle emits no RouteCargoPickedUp row");
                        InGameAssert.AreEqual(0, deliveredCount, "A held cycle emits no RouteCargoDelivered row");

                        // The hold names the short SOURCE (the source:<pid> token built
                        // by RoutePickupSourceGate flows into the eligibility Reason ->
                        // LastHoldDetail; the dry depot B's pid appears in it).
                        string holdToken = postTick.LastHoldDetail;
                        InGameAssert.IsNotNull(holdToken, "A held cycle must record a hold reason token");
                        InGameAssert.IsTrue(
                            holdToken.IndexOf("source:", StringComparison.Ordinal) >= 0
                            && holdToken.IndexOf(depotB.persistentId.ToString(IC), StringComparison.Ordinal) >= 0,
                            $"The hold token must name the SHORT source depot B (pid {depotB.persistentId.ToString(IC)}); " +
                            $"token was '{holdToken}'");

                        ParsekLog.Info("TestRunner",
                            $"MultiOrigin_ShortSource: PASS routeId={routeId} " +
                            $"depotBDryStored={depotBStoredAfterDrain.ToString("R", IC)} " +
                            $"skippedCycles={postTick.SkippedCycles.ToString(IC)} holdToken='{holdToken}'");
                    });
            }
            finally
            {
                UnloadedFuelVesselFixture.Cleanup(fixtureB);
                UnloadedFuelVesselFixture.Cleanup(fixtureA);
            }
            yield break;
        }

        // ==================================================================
        // 3. The shuttle (5.2): load at a refinery, deliver at a station
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "The shuttle (5.2): a 2-window non-KSC route loads LiquidFuel from a refinery source at its window phase, then delivers it at the station window, through the production ProcessMultiStopCrossings -> EmitPickupHalf / ApplyDelivery path. The refinery's LiquidFuel drops by the loaded amount at its window, one RouteCargoPickedUp row + one RouteDispatched + one RouteCargoDelivered land, and CompletedCycles == 1. Auto-spawns the unloaded refinery source; skips when it cannot be provided")]
        public IEnumerator Shuttle_RefineryLoadThenStationDeliver_DebitsRefinery()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live delivery-station vessel");
            Vessel station = FlightGlobals.ActiveVessel;

            var refineryFixture = new UnloadedFuelVesselFixture.EnsureResult();
            try
            {
                IEnumerator ensure = UnloadedFuelVesselFixture.EnsureUnloadedLiquidFuelVessel(
                    ShuttleSourceMinStored, FixtureMinFreeCapacity, refineryFixture);
                while (ensure.MoveNext())
                    yield return ensure.Current;
                if (refineryFixture.Vessel == null)
                    InGameAssert.Skip(
                        "PRECONDITION: could not provide the unloaded refinery source (>= " +
                        $"{ShuttleSourceMinStored.ToString("R", IC)} LF). Provide a fueled PRELAUNCH pad rocket");
                Vessel refinery = refineryFixture.Vessel;

                string treeId = "ingame-shuttle-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string routeId = "ingame-shuttle-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

                double refineryStoredBefore = new LiveOriginCargoProbe(refinery, false).ProbeResourceStored(LiquidFuelName);
                var protoSnapRefinery = SnapshotProtoLiquidFuel(refinery);
                // The station delivery credits the (loaded) active vessel; snapshot it
                // so finally leaves no resource litter.
                var stationTankSnap = SnapshotLoadedLiquidFuel(station);

                List<Route> preExistingRoutes = SnapshotRoutes();
                int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

                RunMultiStopCrossing(
                    label: "Shuttle_LoadDeliver",
                    treeId: treeId,
                    routeId: routeId,
                    tickUT: TickUtAllDocks,
                    preExistingRoutes: preExistingRoutes,
                    buildRoute: () => BuildShuttleRoute(routeId, treeId, refinery, station),
                    restoreState: () =>
                    {
                        RestoreProtoLiquidFuel(protoSnapRefinery);
                        RestoreLoadedLiquidFuel(stationTankSnap);
                    },
                    assertions: capturedLog =>
                    {
                        InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route postTick),
                            "Shuttle route disappeared during Tick");
                        InGameAssert.AreEqual(1, postTick.CompletedCycles,
                            "CompletedCycles should be exactly 1 after a single shuttle cycle");

                        double refineryStoredAfter = new LiveOriginCargoProbe(refinery, false).ProbeResourceStored(LiquidFuelName);
                        InGameAssert.ApproxEqual(refineryStoredBefore - ShuttleLoadAmount, refineryStoredAfter, ResourceTolerance,
                            $"Refinery LiquidFuel should drop by the loaded {ShuttleLoadAmount.ToString("R", IC)} " +
                            $"(before={refineryStoredBefore.ToString("R", IC)} after={refineryStoredAfter.ToString("R", IC)})");

                        CountNewRouteRows(beforeLedgerCount, routeId,
                            out int dispatchedCount, out int pickedUpCount, out var pickedUpStopIndices, out int deliveredCount);
                        InGameAssert.AreEqual(1, dispatchedCount, "Expected exactly one RouteDispatched row");
                        InGameAssert.AreEqual(1, pickedUpCount, "Expected exactly one RouteCargoPickedUp row (the refinery load window)");
                        InGameAssert.IsTrue(pickedUpStopIndices.Contains(0), "The pickup row must be the refinery stop (stop 0)");
                        InGameAssert.AreEqual(1, deliveredCount, "Expected exactly one RouteCargoDelivered row (the station window)");

                        ParsekLog.Info("TestRunner",
                            $"Shuttle_LoadDeliver: PASS routeId={routeId} refinery={refinery.vesselName} " +
                            $"refineryDrop={(refineryStoredBefore - refineryStoredAfter).ToString("R", IC)} " +
                            $"pickedUpRows={pickedUpCount.ToString(IC)} deliveredRows={deliveredCount.ToString(IC)} " +
                            $"completedCycles={postTick.CompletedCycles.ToString(IC)}");
                    });
            }
            finally
            {
                UnloadedFuelVesselFixture.Cleanup(refineryFixture);
            }
            yield break;
        }

        // ==================================================================
        // 4. Escrow competing-route hold: route A reserves, route B sees it held
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Cargo escrow (B2/B3, 19.2.5): route A reserves a shared source's LiquidFuel via RouteStore.ReserveCargo at dispatch; a competing route B gating the SAME source in the gap sees its available amount netted down by A's reservation (OtherRoutesReservedFor) and HOLDS naming the source, instead of double-claiming it. Drives the live LiveRouteRuntimeEnvironment.OriginHasCargo gate against an auto-spawned shared source vessel; skips when it cannot be provided")]
        public IEnumerator Escrow_CompetingRouteSeesReservation_Holds()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            var sharedFixture = new UnloadedFuelVesselFixture.EnsureResult();
            try
            {
                // The shared source must hold enough for exactly ONE route's pickup but
                // NOT two, so reserving for A nets B below its requirement. Cap the
                // spawned source's stored LiquidFuel to PickupAmountA + epsilon (covers
                // one pickup, well under 2x) so a single donor pad rocket - whose tank
                // is far larger than 2x the tiny pickup - can still demonstrate the
                // competing-route hold. The cap clamps the spawned tank exactly; it does
                // NOT apply to a reused pre-existing vessel (see the >= 2x guard below).
                double sourceFloor = PickupAmountA + 1.0;
                double sourceCap = PickupAmountA + 1.0; // < 2*PickupAmountA
                IEnumerator ensure = UnloadedFuelVesselFixture.EnsureUnloadedLiquidFuelVessel(
                    sourceFloor, FixtureMinFreeCapacity, sharedFixture,
                    excludeReusePids: null, capStoredLf: sourceCap);
                while (ensure.MoveNext())
                    yield return ensure.Current;
                if (sharedFixture.Vessel == null)
                    InGameAssert.Skip(
                        "PRECONDITION: could not provide the shared source vessel (>= " +
                        $"{sourceFloor.ToString("R", IC)} LF). Provide a fueled PRELAUNCH pad rocket");
                Vessel sharedSource = sharedFixture.Vessel;

                double sourceStored = new LiveOriginCargoProbe(sharedSource, false).ProbeResourceStored(LiquidFuelName);
                // Each route wants PickupAmountA; with sourceStored < 2*PickupAmountA the
                // second route cannot also be covered once the first reserves.
                if (sourceStored >= 2.0 * PickupAmountA)
                    InGameAssert.Skip(
                        $"Shared source holds {sourceStored.ToString("R", IC)} LF >= 2x the pickup; cannot demonstrate " +
                        "the competing-route net (it could cover both). Re-run with a smaller source");

                string routeAId = "ingame-escrow-A-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string routeBId = "ingame-escrow-B-" + Guid.NewGuid().ToString("N").Substring(0, 8);

                var env = new LiveRouteRuntimeEnvironment();
                Route routeA = BuildSingleSourcePickupRoute(routeAId, sharedSource, PickupAmountA);
                Route routeB = BuildSingleSourcePickupRoute(routeBId, sharedSource, PickupAmountA);

                bool reserved = false;
                try
                {
                    // Baseline: with NO reservation, route B's gate passes (source covers it).
                    bool bEligibleBefore = env.OriginHasCargo(routeB, out string lackBefore);
                    InGameAssert.IsTrue(bEligibleBefore,
                        $"Route B should pass the source gate BEFORE any reservation (source has " +
                        $"{sourceStored.ToString("R", IC)} LF, needs {PickupAmountA.ToString("R", IC)}); lack='{lackBefore}'");

                    // Route A reserves its pickup against the shared source (the
                    // dispatch-time reserve, exercised here directly via the store API
                    // the orchestrator's ReserveCycleEscrow calls).
                    RouteStore.ReserveCargo(routeAId, sharedSource.persistentId, LiquidFuelName, PickupAmountA);
                    reserved = true;
                    InGameAssert.ApproxEqual(PickupAmountA,
                        RouteStore.OtherRoutesReservedFor(routeBId, sharedSource.persistentId, LiquidFuelName),
                        ResourceTolerance,
                        "Route B must see route A's reservation as OTHER-route reserved");

                    // Now route B's gate must HOLD: live stored minus A's reservation
                    // is below B's requirement.
                    bool bEligibleAfter = env.OriginHasCargo(routeB, out string lackAfter);
                    InGameAssert.IsFalse(bEligibleAfter,
                        "Route B must HOLD once route A has reserved the shared source (escrow net): " +
                        $"source={sourceStored.ToString("R", IC)} reservedByA={PickupAmountA.ToString("R", IC)} " +
                        $"needB={PickupAmountA.ToString("R", IC)}");
                    InGameAssert.IsNotNull(lackAfter, "The held gate must report a lacking-resource token");
                    InGameAssert.IsTrue(
                        lackAfter.IndexOf("source:", StringComparison.Ordinal) >= 0
                        && lackAfter.IndexOf(sharedSource.persistentId.ToString(IC), StringComparison.Ordinal) >= 0,
                        $"The hold token must name the shared source (pid {sharedSource.persistentId.ToString(IC)}); " +
                        $"token was '{lackAfter}'");

                    ParsekLog.Info("TestRunner",
                        $"Escrow_CompetingHold: PASS sharedSource={sharedSource.vesselName} " +
                        $"pid={sharedSource.persistentId.ToString(IC)} stored={sourceStored.ToString("R", IC)} " +
                        $"reservedByA={PickupAmountA.ToString("R", IC)} bHeldToken='{lackAfter}'");
                }
                finally
                {
                    if (reserved)
                        RouteStore.DropRouteEscrow(routeAId);
                }
            }
            finally
            {
                UnloadedFuelVesselFixture.Cleanup(sharedFixture);
            }
            yield break;
        }

        // ==================================================================
        // 5. SaveGame round-trip: escrow re-establishes + the cycle resumes
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "A multi-origin cycle ticks to debit source A only (the tick reaches depot A's dock, not depot B's), a real GamePersistence.SaveGame persists the partial fire state (stop 0 fired, stops 1+ unfired) into the .sfs, the route reloads from the persisted node, and the next tick reaching depot B + the station fires source B + the delivery and completes the SAME cycle once. The escrow is RAM-only (not persisted) and re-establishes for the un-fired window on the dispatchAlready resume (B3 C1). Pauses time warp across the save; restores state + deletes the disposable slot in finally")]
        public IEnumerator MultiOrigin_SaveRoundTrip_ResumesAndReEstablishesEscrow()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("HighLogic.CurrentGame is null; cannot drive GamePersistence.SaveGame");
            if (string.IsNullOrEmpty(HighLogic.SaveFolder))
                InGameAssert.Skip("HighLogic.SaveFolder is null/empty; cannot resolve save root");
            if (string.IsNullOrEmpty(KSPUtil.ApplicationRootPath))
                InGameAssert.Skip("KSPUtil.ApplicationRootPath is null/empty; cannot resolve .sfs path");

            var fixtureA = new UnloadedFuelVesselFixture.EnsureResult();
            var fixtureB = new UnloadedFuelVesselFixture.EnsureResult();
            bool warpPaused = false;
            int warpIndexBefore = TimeWarp.CurrentRateIndex;
            string saveSlot = TestSaveSlotPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);

            try
            {
                IEnumerator ensureA = UnloadedFuelVesselFixture.EnsureUnloadedLiquidFuelVessel(
                    DepotAMinStored, FixtureMinFreeCapacity, fixtureA);
                while (ensureA.MoveNext())
                    yield return ensureA.Current;
                if (fixtureA.Vessel == null)
                    InGameAssert.Skip("PRECONDITION: could not provide depot A. Provide a fueled PRELAUNCH pad rocket");

                // Exclude depot A's pid so depot B spawns distinct (see test 1).
                HashSet<uint> excludeForB = BuildExcludeSet(fixtureA);
                IEnumerator ensureB = UnloadedFuelVesselFixture.EnsureUnloadedLiquidFuelVessel(
                    DepotBMinStored, FixtureMinFreeCapacity, fixtureB, excludeForB);
                while (ensureB.MoveNext())
                    yield return ensureB.Current;
                if (fixtureB.Vessel == null)
                    InGameAssert.Skip("PRECONDITION: could not provide depot B. Provide a fueled PRELAUNCH pad rocket");

                Vessel depotA = fixtureA.Vessel;
                Vessel depotB = fixtureB.Vessel;
                if (depotA.persistentId == depotB.persistentId)
                    InGameAssert.Skip("Depots resolved to the same pid; need two distinct sources");
                Vessel deliveryTarget = FlightGlobals.ActiveVessel ?? depotA;

                if (warpIndexBefore > 0)
                {
                    TimeWarp.SetRate(0, true);
                    warpPaused = true;
                }

                string treeId = "ingame-mo-resume-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string routeId = "ingame-mo-resume-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string savePath = Path.Combine(
                    KSPUtil.ApplicationRootPath ?? string.Empty,
                    "saves",
                    HighLogic.SaveFolder ?? string.Empty,
                    saveSlot + ".sfs");

                double depotAStoredBefore = new LiveOriginCargoProbe(depotA, false).ProbeResourceStored(LiquidFuelName);
                double depotBStoredBefore = new LiveOriginCargoProbe(depotB, false).ProbeResourceStored(LiquidFuelName);
                var protoSnapA = SnapshotProtoLiquidFuel(depotA);
                var protoSnapB = SnapshotProtoLiquidFuel(depotB);
                // The resume tick's delivery half credits the (loaded) delivery target;
                // snapshot it so finally leaves no resource litter.
                var deliveryTankSnap = SnapshotLoadedLiquidFuel(deliveryTarget);

                List<Route> preExistingRoutes = SnapshotRoutes();
                RecordingTree routeTree = BuildConsolidationBackingTree(treeId);
                GhostPlaybackLogic.LoopUnit loopUnit = BuildSpanLoopUnit();
                var previousResolver = RouteOrchestrator.LoopUnitResolverForTesting;
                var committedAdded = new List<Recording>();
                bool treeAdded = false, storeWiped = false, seamArmed = false;

                try
                {
                    RecordingStore.AddCommittedTreeForTesting(routeTree);
                    treeAdded = true;
                    foreach (Recording rec in routeTree.Recordings.Values)
                    {
                        if (rec == null) continue;
                        RecordingStore.AddCommittedInternal(rec);
                        committedAdded.Add(rec);
                    }

                    RouteStore.ResetForTesting();
                    storeWiped = true;
                    Route route = BuildConsolidationRoute(routeId, treeId, depotA, depotB, deliveryTarget);
                    RouteStore.AddRoute(route);

                    RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) =>
                        r != null && string.Equals(r.Id, routeId, StringComparison.Ordinal)
                            ? loopUnit
                            : (previousResolver != null ? previousResolver(r, ut) : (GhostPlaybackLogic.LoopUnit?)null);
                    seamArmed = true;

                    // ---- TICK 1: reach depot A only (UT 1500). Source A debited;
                    //      the cycle is NOT complete (station not reached). ----
                    RouteOrchestrator.Tick(DockUtA);

                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route afterTick1),
                        "Route disappeared after tick 1");
                    InGameAssert.AreEqual(0, afterTick1.CompletedCycles,
                        "Cycle must NOT complete after only depot A's window fired");
                    InGameAssert.AreEqual(0L, afterTick1.Stops[0].LastFiredCycleIndex,
                        "Stop 0 (depot A) should have fired cycle 0 on tick 1");
                    InGameAssert.AreEqual(-1L, afterTick1.Stops[1].LastFiredCycleIndex,
                        "Stop 1 (depot B) must NOT have fired (its dock phase was not reached on tick 1)");
                    double depotAAfterTick1 = new LiveOriginCargoProbe(depotA, false).ProbeResourceStored(LiquidFuelName);
                    InGameAssert.ApproxEqual(depotAStoredBefore - PickupAmountA, depotAAfterTick1, ResourceTolerance,
                        "Depot A should be debited after tick 1");

                    // Disarm the resolver seam before the save + settle yield.
                    RouteOrchestrator.LoopUnitResolverForTesting = previousResolver;
                    seamArmed = false;

                    string saveResult = GamePersistence.SaveGame(saveSlot, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                    InGameAssert.IsTrue(!string.IsNullOrEmpty(saveResult),
                        $"GamePersistence.SaveGame returned null/empty for slot '{saveSlot}'");

                    yield return null; // let deferred-one-frame writes settle

                    InGameAssert.IsTrue(File.Exists(savePath), $"Expected .sfs at '{savePath}' after save");
                    ConfigNode root = ConfigNode.Load(savePath);
                    InGameAssert.IsNotNull(root, $"ConfigNode.Load returned null for '{savePath}'");

                    if (!TryFindRouteNode(root, routeId, out ConfigNode routeNode))
                        InGameAssert.Skip(
                            $"Could not locate the ROUTE node id={routeId} in '{savePath}' (save layout mismatch); " +
                            "skipping rather than false-failing - the tick-1 partial-fire assertions above passed");
                    InGameAssert.AreEqual("0", routeNode.GetValue("completedCycles") ?? "0",
                        "Saved route completedCycles must be 0 (the cycle is mid-flight)");
                    ConfigNode[] stopNodes = routeNode.GetNodes("STOP");
                    InGameAssert.AreEqual(3, stopNodes != null ? stopNodes.Length : 0,
                        "Saved multi-origin route must carry three STOP nodes");
                    InGameAssert.AreEqual("0", stopNodes[0].GetValue("lastFiredCycleIndex"),
                        "Saved STOP 0 (depot A) must carry lastFiredCycleIndex=0");
                    InGameAssert.IsNull(stopNodes[1].GetValue("lastFiredCycleIndex"),
                        "Saved STOP 1 (depot B) must OMIT lastFiredCycleIndex (unfired, -1 default)");

                    // ---- RELOAD the route via the production codec; the escrow is
                    //      RAM-only so it is NOT persisted (re-established on resume). ----
                    RouteStore.ResetForTesting();
                    ConfigNode scenarioWrap = FindParsekScenarioNode(root) ?? root;
                    int loaded = RouteStore.LoadRoutesFrom(scenarioWrap);
                    InGameAssert.IsTrue(loaded >= 1, "Reload should restore the synthetic route from the .sfs");
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route reloaded),
                        "The route did not survive the save/reload round-trip");
                    InGameAssert.AreEqual(0L, reloaded.Stops[0].LastFiredCycleIndex,
                        "Reloaded stop 0 must carry lastFiredCycleIndex=0");
                    InGameAssert.AreEqual(-1L, reloaded.Stops[1].LastFiredCycleIndex,
                        "Reloaded stop 1 must carry lastFiredCycleIndex=-1");
                    InGameAssert.IsFalse(RouteStore.HasEscrow(routeId),
                        "Escrow is RAM-only and not persisted; the reloaded route should hold none before resume");

                    // Re-arm the resolver for the resumed route and tick to depot B +
                    // the station (UT 2500). Source B fires + the delivery completes the
                    // SAME cycle once; source A must NOT re-debit.
                    RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) =>
                        r != null && string.Equals(r.Id, routeId, StringComparison.Ordinal)
                            ? loopUnit
                            : (previousResolver != null ? previousResolver(r, ut) : (GhostPlaybackLogic.LoopUnit?)null);
                    seamArmed = true;

                    int beforeResumeLedger = Ledger.Actions != null ? Ledger.Actions.Count : 0;
                    double depotAStoredAtResume = new LiveOriginCargoProbe(depotA, false).ProbeResourceStored(LiquidFuelName);

                    RouteOrchestrator.Tick(TickUtAllDocks);

                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out Route afterTick2),
                        "Route disappeared after the resume tick");
                    InGameAssert.AreEqual(1, afterTick2.CompletedCycles,
                        "The resume tick reaching depot B + the station must complete the cycle exactly once");
                    InGameAssert.AreEqual(0L, afterTick2.Stops[1].LastFiredCycleIndex,
                        "Stop 1 (depot B) should now have fired cycle 0");

                    // Depot B debited on resume; depot A NOT re-debited.
                    double depotBStoredAfter = new LiveOriginCargoProbe(depotB, false).ProbeResourceStored(LiquidFuelName);
                    InGameAssert.ApproxEqual(depotBStoredBefore - PickupAmountB, depotBStoredAfter, ResourceTolerance,
                        "Depot B should be debited on the resume tick");
                    double depotAStoredAfterResume = new LiveOriginCargoProbe(depotA, false).ProbeResourceStored(LiquidFuelName);
                    InGameAssert.ApproxEqual(depotAStoredAtResume, depotAStoredAfterResume, ResourceTolerance,
                        "Depot A must NOT re-debit on the resume tick (window 0 already fired + persisted)");

                    // Escrow nets to zero after the cycle completes (B3 C2 drop).
                    InGameAssert.IsFalse(RouteStore.HasEscrow(routeId),
                        "After the cycle completes the route's escrow must be dropped (nets to zero)");

                    CountNewRouteRows(beforeResumeLedger, routeId,
                        out int dispatched2, out int pickedUp2, out var pickedUpStops2, out _);
                    InGameAssert.AreEqual(0, dispatched2,
                        "No new RouteDispatched row on the resume tick (dispatch already persisted for this cycle)");
                    InGameAssert.AreEqual(1, pickedUp2,
                        "Exactly one new RouteCargoPickedUp row on resume (depot B); depot A must NOT re-pick-up");
                    InGameAssert.IsTrue(pickedUpStops2.Contains(1) && !pickedUpStops2.Contains(0),
                        "The resume pickup row must be stop 1 (depot B) only");

                    ParsekLog.Info("TestRunner",
                        $"MultiOrigin_SaveRoundTrip: PASS routeId={routeId} slot='{saveSlot}' " +
                        $"tick1=depotA tick2=depotB+station completedCycles={afterTick2.CompletedCycles.ToString(IC)}");
                }
                finally
                {
                    if (seamArmed)
                        RouteOrchestrator.LoopUnitResolverForTesting = previousResolver;
                    if (storeWiped)
                        RestoreRoutes(preExistingRoutes);
                    for (int i = 0; i < committedAdded.Count; i++)
                        RecordingStore.RemoveCommittedInternal(committedAdded[i]);
                    if (treeAdded)
                        RemoveCommittedTree(treeId);
                    MissionStore.PruneOrphans(RecordingStore.CommittedTrees);
                    RestoreProtoLiquidFuel(protoSnapA);
                    RestoreProtoLiquidFuel(protoSnapB);
                    RestoreLoadedLiquidFuel(deliveryTankSnap);
                    QuickloadResumeHelpers.TryDeleteSaveSlot(saveSlot);
                }
            }
            finally
            {
                UnloadedFuelVesselFixture.Cleanup(fixtureB);
                UnloadedFuelVesselFixture.Cleanup(fixtureA);
                if (warpPaused)
                {
                    try { TimeWarp.SetRate(warpIndexBefore, true); }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn("TestRunner",
                            $"MultiOrigin_SaveRoundTrip cleanup: failed to restore time warp ({ex.GetType().Name}: {ex.Message})");
                    }
                }
            }
        }

        // ==================================================================
        // 6. B1-review nit A regression: M3a single-window pickup unaffected
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "B1-review nit A regression: the new N-source gate (shared OriginHasCargo pickup gating) does not regress the M3a single-window PICKUP path. A single-window pickup route whose source is stocked PASSES the live source gate (dispatches), and the SAME route with the source drained dry HOLDS naming the source. Confirms the M4b derive-sources-from-pickup-stops gate is a superset of the M3a single-source behavior")]
        public IEnumerator M3aSingleWindowPickup_NotRegressedByNewGate()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live source vessel");
            Vessel source = FlightGlobals.ActiveVessel;
            if (!(source.loaded && !source.packed))
                InGameAssert.Skip($"Active vessel '{source.vesselName}' is not loaded+unpacked");

            double sourceStored = new LiveOriginCargoProbe(source, true).ProbeResourceStored(LiquidFuelName);
            if (sourceStored < PickupAmountA + 0.1)
                InGameAssert.Skip(
                    $"Active vessel '{source.vesselName}' stores only {sourceStored.ToString("R", IC)} debitable " +
                    $"LiquidFuel (< {(PickupAmountA + 0.1).ToString("R", IC)}); pick a vessel with fuel aboard");

            var env = new LiveRouteRuntimeEnvironment();
            string routeId = "ingame-m3a-pickup-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Route route = BuildSingleSourcePickupRoute(routeId, source, PickupAmountA);

            // 1. Stocked source -> the gate PASSES (the route would dispatch).
            bool stockedEligible = env.OriginHasCargo(route, out string lackStocked);
            InGameAssert.IsTrue(stockedEligible,
                $"A single-window pickup route with a stocked source must PASS the gate " +
                $"(source={sourceStored.ToString("R", IC)} LF, needs {PickupAmountA.ToString("R", IC)}); lack='{lackStocked}'");

            // 2. Dry source (request more than stored) -> the gate HOLDS naming the source.
            Route dryRoute = BuildSingleSourcePickupRoute(routeId + "-dry", source, sourceStored + 1000.0);
            bool dryEligible = env.OriginHasCargo(dryRoute, out string lackDry);
            InGameAssert.IsFalse(dryEligible,
                "A single-window pickup route whose source cannot cover the window must HOLD");
            InGameAssert.IsNotNull(lackDry, "The held single-window pickup gate must report a lacking-resource token");
            InGameAssert.IsTrue(
                lackDry.IndexOf("source:", StringComparison.Ordinal) >= 0
                && lackDry.IndexOf(source.persistentId.ToString(IC), StringComparison.Ordinal) >= 0,
                $"The hold token must name the source (pid {source.persistentId.ToString(IC)}); token was '{lackDry}'");

            ParsekLog.Info("TestRunner",
                $"M3aSingleWindowPickup_NotRegressed: PASS source={source.vesselName} " +
                $"pid={source.persistentId.ToString(IC)} stored={sourceStored.ToString("R", IC)} " +
                $"stockedEligible=1 dryEligible=0 dryToken='{lackDry}'");
            yield break;
        }

        // ==================================================================
        // Shared crossing runner (synchronous, single-tick cases)
        // ==================================================================

        /// <summary>
        /// Shared arrange / act / assert / teardown frame for the synchronous
        /// single-tick multi-stop cases: commits the backing tree + member
        /// recordings (so the ERS eligibility gate is real), wipes RouteStore down
        /// to ONE caller-built synthetic route, arms the loop-unit resolver seam
        /// (route-id scoped) with the span unit, captures Info log lines, runs ONE
        /// production <see cref="RouteOrchestrator.Tick(double)"/> at
        /// <paramref name="tickUT"/>, runs the caller's assertions, and restores
        /// everything (store, committed trees, source/depot resource state) in
        /// finally. Leaves BOTH the pickup AND delivery halves on the LIVE
        /// production path so the real per-window source debits + ledger rows are
        /// exercised (no PickupDebitApplierForTesting / DeliveryRowEmitterForTesting
        /// seam).
        /// </summary>
        private static void RunMultiStopCrossing(
            string label,
            string treeId,
            string routeId,
            double tickUT,
            List<Route> preExistingRoutes,
            Func<Route> buildRoute,
            Action restoreState,
            Action<List<string>> assertions)
        {
            RecordingTree routeTree = BuildConsolidationBackingTree(treeId);
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
                Route route = buildRoute();
                RouteStore.AddRoute(route);
                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out _),
                    "Synthetic multi-origin route was not stored");

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
                    $"{label}: pre-tick routeId={routeId} treeId={treeId} tickUT={tickUT.ToString("R", IC)}");

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
                    restoreState?.Invoke();
                    ParsekLog.Verbose("TestRunner", $"{label} cleanup: source/depot resource state restored");
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"{label} cleanup: failed to restore source/depot resource state ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }

        // ==================================================================
        // Route + loop-unit + tree fixtures
        // ==================================================================

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

        private static RouteEndpoint EndpointForVessel(Vessel v)
        {
            return new RouteEndpoint
            {
                VesselPersistentId = v != null ? v.persistentId : 0u,
                BodyName = v != null && v.mainBody != null ? v.mainBody.bodyName : "Kerbin",
                IsSurface = false,
            };
        }

        /// <summary>
        /// Synthetic non-KSC multi-ORIGIN route (5.3): two PICKUP source stops
        /// (depot A at <see cref="DockUtA"/>, depot B at <see cref="DockUtB"/>, each
        /// resolving to a distinct live source vessel with its own LiquidFuel pickup
        /// manifest) + a DELIVERY stop at the station (<see cref="DockUtStation"/>,
        /// the LAST dock). NOT KSC origin and NOT harvest origin: the source gate
        /// (PickupSourcesHaveCargo) gates the two depots all-or-nothing, the per-window
        /// debit drains each depot, the delivery credits the consolidated cargo. The
        /// route-level scalar dock fields key on the LAST (max-DockUT) stop, as
        /// RouteBuilder derives them (A2).
        /// </summary>
        private static Route BuildConsolidationRoute(
            string routeId, string treeId, Vessel depotA, Vessel depotB, Vessel station)
        {
            return new Route
            {
                Id = routeId,
                Name = "Parsek Multi-Origin Consolidation In-Game",
                Status = RouteStatus.Active,
                IsKscOrigin = false,
                IsHarvestOrigin = false,
                BackingMissionTreeId = treeId,
                ExcludedIntervalKeys = new HashSet<string>(),
                RecordedDockUT = DockUtStation,
                DockMemberRecordingId = "dockedStation",
                LoopAnchorUT = SpanStartUT,
                LastObservedLoopCycleIndex = -1,
                TransitDuration = Cadence,
                DispatchInterval = Cadence,
                NextDispatchUT = TickUtAllDocks + Cadence,
                CompletedCycles = 0,
                SkippedCycles = 0,
                KscDispatchFundsCost = 0.0,
                // Multi-origin: cargo is loaded en route from the depots, not launched.
                // The single docked-origin provenance is EMPTY (no Route.Origin debit);
                // the pickup-source provenance gates + debits each depot.
                CostManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                InventoryCostManifest = new List<InventoryPayloadItem>(),
                // A pure-pickup-then-deliver route has no docked Route.Origin; the
                // origin endpoint is the pickup display origin (depot A). IsKscOrigin /
                // IsHarvestOrigin false routes through PickupSourcesHaveCargo only.
                Origin = EndpointForVessel(depotA),
                RecordingIds = new List<string> { "launch", "midA2B", "midB2S", "dockedStation" },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "launch", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "midA2B", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "midB2S", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "dockedStation", TreeId = treeId },
                },
                Stops = new List<RouteStop>
                {
                    // Stop 0: load PickupAmountA from depot A.
                    new RouteStop
                    {
                        Endpoint = EndpointForVessel(depotA),
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        PickupManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, PickupAmountA },
                        },
                        InventoryPickupManifest = new List<InventoryPayloadItem>(),
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 0,
                        RecordedDockUT = DockUtA,
                        LastFiredCycleIndex = -1,
                    },
                    // Stop 1: load PickupAmountB from depot B.
                    new RouteStop
                    {
                        Endpoint = EndpointForVessel(depotB),
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        PickupManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, PickupAmountB },
                        },
                        InventoryPickupManifest = new List<InventoryPayloadItem>(),
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 1,
                        RecordedDockUT = DockUtB,
                        LastFiredCycleIndex = -1,
                    },
                    // Stop 2: deliver the consolidated cargo at the station (last dock).
                    new RouteStop
                    {
                        Endpoint = EndpointForVessel(station),
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        PickupManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                        InventoryPickupManifest = new List<InventoryPayloadItem>(),
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, PickupAmountA + PickupAmountB },
                        },
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 2,
                        RecordedDockUT = DockUtStation,
                        LastFiredCycleIndex = -1,
                    },
                },
            };
        }

        /// <summary>
        /// Synthetic non-KSC SHUTTLE route (5.2): a refinery PICKUP stop at
        /// <see cref="DockUtA"/> + a station DELIVERY stop at
        /// <see cref="DockUtStation"/> (the LAST dock). Two windows, so the route
        /// drives ProcessMultiStopCrossings; the refinery is physically debited at
        /// its window, the station credited at its window.
        /// </summary>
        private static Route BuildShuttleRoute(string routeId, string treeId, Vessel refinery, Vessel station)
        {
            return new Route
            {
                Id = routeId,
                Name = "Parsek Shuttle Load+Deliver In-Game",
                Status = RouteStatus.Active,
                IsKscOrigin = false,
                IsHarvestOrigin = false,
                BackingMissionTreeId = treeId,
                ExcludedIntervalKeys = new HashSet<string>(),
                RecordedDockUT = DockUtStation,
                DockMemberRecordingId = "dockedStation",
                LoopAnchorUT = SpanStartUT,
                LastObservedLoopCycleIndex = -1,
                TransitDuration = Cadence,
                DispatchInterval = Cadence,
                NextDispatchUT = TickUtAllDocks + Cadence,
                CompletedCycles = 0,
                SkippedCycles = 0,
                KscDispatchFundsCost = 0.0,
                CostManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                InventoryCostManifest = new List<InventoryPayloadItem>(),
                Origin = EndpointForVessel(refinery),
                RecordingIds = new List<string> { "launch", "midB2S", "dockedStation" },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "launch", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "midB2S", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "dockedStation", TreeId = treeId },
                },
                Stops = new List<RouteStop>
                {
                    // Stop 0: load from the refinery.
                    new RouteStop
                    {
                        Endpoint = EndpointForVessel(refinery),
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        PickupManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, ShuttleLoadAmount },
                        },
                        InventoryPickupManifest = new List<InventoryPayloadItem>(),
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 0,
                        RecordedDockUT = DockUtA,
                        LastFiredCycleIndex = -1,
                    },
                    // Stop 1: deliver at the station (last dock).
                    new RouteStop
                    {
                        Endpoint = EndpointForVessel(station),
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        PickupManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                        InventoryPickupManifest = new List<InventoryPayloadItem>(),
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, ShuttleLoadAmount },
                        },
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 1,
                        RecordedDockUT = DockUtStation,
                        LastFiredCycleIndex = -1,
                    },
                },
            };
        }

        /// <summary>
        /// Synthetic non-KSC SINGLE-window PICKUP route (M3a shape): one pickup stop
        /// resolving to <paramref name="source"/> with a <paramref name="amount"/>
        /// LiquidFuel pickup manifest. Used by the escrow-hold + the M3a-regression
        /// tests to drive the live OriginHasCargo source gate (which routes through
        /// PickupSourcesHaveCargo for a non-KSC / non-harvest route). Single-stop, so
        /// it never enters ProcessMultiStopCrossings - these tests call the GATE only.
        /// </summary>
        private static Route BuildSingleSourcePickupRoute(string routeId, Vessel source, double amount)
        {
            return new Route
            {
                Id = routeId,
                Name = "Parsek Single-Source Pickup In-Game",
                Status = RouteStatus.Active,
                IsKscOrigin = false,
                IsHarvestOrigin = false,
                CostManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                InventoryCostManifest = new List<InventoryPayloadItem>(),
                Origin = EndpointForVessel(source),
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = EndpointForVessel(source),
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        PickupManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, amount },
                        },
                        InventoryPickupManifest = new List<InventoryPayloadItem>(),
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 0,
                        RecordedDockUT = DockUtA,
                        LastFiredCycleIndex = -1,
                    },
                },
            };
        }

        // launch -> dock A (pickup) -> mid -> dock B (pickup) -> mid -> dock station
        // (deliver) -> terminal undock. Mirrors the multi-stop topology so the ERS
        // membership gate resolves the member recordings; the fire path resolves the
        // loop unit through the seam, so the exact composition is not load-bearing
        // (the member recordings just need to be committed).
        private static RecordingTree BuildConsolidationBackingTree(string treeId)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = "launch" };
            tree.Recordings["launch"] = Leg("launch", "C0", 0, 1000, 1500, "Transport");
            tree.Recordings["dockedA"] = Leg("dockedA", "C0", 1, 1500, 1600, "Transport");
            tree.Recordings["midA2B"] = Leg("midA2B", "C0", 2, 1600, 2000, "Transport");
            tree.Recordings["dockedB"] = Leg("dockedB", "C0", 3, 2000, 2100, "Transport");
            tree.Recordings["midB2S"] = Leg("midB2S", "C0", 4, 2100, 2500, "Transport");
            tree.Recordings["dockedStation"] = Leg("dockedStation", "C0", 5, 2500, 3000, "Transport");
            tree.Recordings["tail"] = Leg("tail", "C0", 6, 3000, 3500, "Transport");
            tree.BranchPoints.Add(BP("dockA-bp", BranchPointType.Dock,
                new[] { "launch" }, new[] { "dockedA" }, 1500));
            tree.BranchPoints.Add(BP("undockA-bp", BranchPointType.Undock,
                new[] { "dockedA" }, new[] { "midA2B" }, 1600));
            tree.BranchPoints.Add(BP("dockB-bp", BranchPointType.Dock,
                new[] { "midA2B" }, new[] { "dockedB" }, 2000));
            tree.BranchPoints.Add(BP("undockB-bp", BranchPointType.Undock,
                new[] { "dockedB" }, new[] { "midB2S" }, 2100));
            tree.BranchPoints.Add(BP("dockStation-bp", BranchPointType.Dock,
                new[] { "midB2S" }, new[] { "dockedStation" }, 2500));
            tree.BranchPoints.Add(BP("undockStation-bp", BranchPointType.Undock,
                new[] { "dockedStation" }, new[] { "tail" }, 3000));
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
                MergeCause = type == BranchPointType.Dock ? "DOCK" : null,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children)
            };
        }

        // ==================================================================
        // State snapshot / restore helpers
        // ==================================================================

        /// <summary>
        /// The reuse-exclusion set for provisioning a SECOND distinct depot: the
        /// already-provisioned fixture's resolved persistentId (the just-spawned
        /// depot is now itself an existing unloaded vessel, so without excluding it
        /// the reuse fast-path would hand it back for the second call), plus the
        /// active vessel's pid as belt-and-suspenders. Prefers the live resolved
        /// vessel pid; falls back to the recorded SpawnedPid.
        /// </summary>
        private static HashSet<uint> BuildExcludeSet(UnloadedFuelVesselFixture.EnsureResult provisioned)
        {
            var set = new HashSet<uint>();
            if (provisioned != null)
            {
                if (provisioned.Vessel != null)
                    set.Add(provisioned.Vessel.persistentId);
                if (provisioned.SpawnedPid != 0u)
                    set.Add(provisioned.SpawnedPid);
            }
            Vessel active = FlightGlobals.ActiveVessel;
            if (active != null)
                set.Add(active.persistentId);
            return set;
        }

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

        private static List<KeyValuePair<PartResource, double>> SnapshotLoadedLiquidFuel(Vessel vessel)
        {
            var snapshot = new List<KeyValuePair<PartResource, double>>();
            if (vessel == null || !vessel.loaded || vessel.parts == null) return snapshot;
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
            if (snapshot == null) return;
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
            if (snapshot == null) return;
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i].Key != null)
                    snapshot[i].Key.amount = snapshot[i].Value;
        }

        private static void DrainProtoLiquidFuel(Vessel vessel)
        {
            ProtoVessel pv = vessel != null ? vessel.protoVessel : null;
            if (pv == null || pv.protoPartSnapshots == null) return;
            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null || pps.resources == null) continue;
                for (int j = 0; j < pps.resources.Count; j++)
                {
                    ProtoPartResourceSnapshot prs = pps.resources[j];
                    if (prs == null) continue;
                    if (!string.Equals(prs.resourceName, LiquidFuelName, StringComparison.Ordinal)) continue;
                    prs.amount = 0.0;
                }
            }
        }

        // ==================================================================
        // Ledger + .sfs lookup helpers
        // ==================================================================

        /// <summary>
        /// Counts the RouteDispatched / RouteCargoPickedUp / RouteCargoDelivered rows
        /// appended after <paramref name="fromIndex"/> for <paramref name="routeId"/>,
        /// collecting the DISTINCT stop indices the pickup rows carry (so the
        /// per-window replay-key fix is visible: a 2-source cycle emits two pickup
        /// rows with stop indices {0,1}).
        /// </summary>
        private static void CountNewRouteRows(int fromIndex, string routeId,
            out int dispatchedCount, out int pickedUpCount, out HashSet<int> pickedUpStopIndices,
            out int deliveredCount)
        {
            dispatchedCount = 0;
            pickedUpCount = 0;
            pickedUpStopIndices = new HashSet<int>();
            deliveredCount = 0;
            var actions = Ledger.Actions;
            if (actions == null) return;
            for (int i = fromIndex; i < actions.Count; i++)
            {
                GameAction a = actions[i];
                if (a == null) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (a.Type == GameActionType.RouteDispatched) dispatchedCount++;
                else if (a.Type == GameActionType.RouteCargoPickedUp)
                {
                    pickedUpCount++;
                    pickedUpStopIndices.Add(a.RouteStopIndex);
                }
                else if (a.Type == GameActionType.RouteCargoDelivered) deliveredCount++;
            }
        }

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
