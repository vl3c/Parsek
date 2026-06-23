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
    /// M4c Phase C2 in-game close-out for ROUND-TRIP LINKING (plan
    /// `docs/dev/plan-logistics-m4-shape-generality.md` Phase C2, gameplay
    /// grounding 5.4 the Routine-Mission-Manager profile: two linked one-way
    /// routes - an outbound run and a return run - alternate so only one
    /// transport's worth of effects is in flight at a time, modeling a single
    /// reused vehicle). The pure xUnit suite (<c>RouteRoundTripLinkTests</c>)
    /// covers the alternation gate math, the deadlock seed, the paused / missing
    /// bypass, the gate-ordered-last rule, and the codec sparse round-trip; these
    /// tests pin the part only live KSP can exercise: the chain constraint driving
    /// the REAL production <see cref="RouteOrchestrator.Tick(double)"/> ->
    /// <see cref="RouteOrchestrator.ProcessLoopRoute"/> -> <c>EmitLoopCycle</c> ->
    /// <c>ApplyDelivery</c> path against a LIVE delivery endpoint vessel, the
    /// dispatch-time alternation advance (<c>AdvancePartnerAlternationOnDispatch</c>)
    /// + the <see cref="RouteStore.TryGetRoute"/> partner resolution, and a real
    /// <see cref="GamePersistence.SaveGame"/> round-trip of the alternation cursor.
    ///
    /// <para><b>CROSS-TICK alternation (the C1-review coverage nit).</b> The C1
    /// xUnit alternation test ticks BOTH routes in ONE tick, where the seed (A)
    /// processes first, bumps <c>CompletedCycles</c>, and B then sees A complete
    /// WITHIN the same tick and co-dispatches - a same-tick co-dispatch artifact
    /// that does not prove the cross-tick wait. These tests use two linked routes
    /// with DIFFERENT recorded dock phases (A's dock at loop UT 1300, B's at 1700,
    /// in span [1000,2000]) so a tick at 1300 crosses ONLY A's dock and a LATER
    /// tick at 1700 crosses ONLY B's dock. A dispatches + completes a cycle on the
    /// FIRST tick; B holds <c>WaitingForPartner</c> on that first tick (its dock
    /// phase is not yet reached AND, at that instant, A had not yet completed - the
    /// hold is asserted at the evaluator level so it is order-independent); then on
    /// the SECOND tick B becomes eligible (A completed a cycle) and dispatches. The
    /// "one transport's worth of effects" guarantee (design 5.4) is asserted via
    /// the <c>CompletedCycles</c> progression: never more than one of the pair
    /// advances per tick, and the second only after the first completed.</para>
    ///
    /// <para><b>Harvest-origin delivery routes.</b> Both routes are
    /// <c>IsHarvestOrigin=true</c> single-stop DELIVERY routes (no KSC funds, no
    /// origin vessel to resolve, empty <c>CostManifest</c>) crediting a tiny
    /// LiquidFuel amount onto the LIVE active vessel. This isolates the chain
    /// constraint (the focus of M4c) from the funds / origin-debit machinery
    /// already covered by M1/M2/M3/M4b; the live delivery endpoint is
    /// snapshotted + restored in finally so no resource litter survives.</para>
    ///
    /// <para><b>Re-entry discipline + post-restore unpack wait</b> (todo
    /// "background RouteOrchestrator.Tick can re-enter a logistics test's synthetic
    /// route", same contract as <see cref="LogisticsMultiOriginRuntimeTests"/> /
    /// <see cref="LogisticsMultiStopRuntimeTests"/>): the cases yield ONLY in the
    /// precondition unpack wait BEFORE any seam is armed or any store mutated; the
    /// whole arrange / Tick / assert / teardown sequence then runs yield-free on
    /// the main thread, so the background 1 Hz scenario tick can never interleave
    /// with an armed seam or a stored synthetic route. The save round-trip test
    /// pauses time warp + disarms the resolver seam BEFORE the save.
    /// AllowBatchExecution=false + RestoreBatchFlightBaselineAfterExecution=true.</para>
    ///
    /// <para><b>Deterministic span clock.</b> Span [1000,2000], cadence == span,
    /// anchor == spanStart. Route A's single delivery dock sits at loop UT 1300,
    /// route B's at 1700. A tick at 1300 reaches only A's dock phase for cycle 0; a
    /// tick at 1700 reaches both dock phases but A already fired cycle 0 (its
    /// LastObservedLoopCycleIndex snapped forward), so only B fires.</para>
    /// </summary>
    public sealed class LogisticsRoundTripRuntimeTests
    {
        private const string LiquidFuelName = "LiquidFuel";
        private const double DeliveryAmountA = 3.0;
        private const double DeliveryAmountB = 2.0;
        private const double ResourceTolerance = 0.01;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const string TestSaveSlotPrefix = "parsek_roundtrip_ingame_test_";

        private const string IsolatedOnlyBatchSkipReason =
            "Isolated-run only - mutates RouteStore, Ledger, RecordingStore committed trees, " +
            "RouteOrchestrator test seams, and live vessel resource state under live KSP statics; " +
            "excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play " +
            "button in a disposable FLIGHT session.";

        // Deterministic span clock. Span [1000,2000], cadence == span, anchor ==
        // spanStart; route A's delivery dock at loop UT 1300, route B's at 1700.
        private const double SpanStartUT = 1000.0;
        private const double SpanEndUT = 2000.0;
        private const double DockUtA = 1300.0;
        private const double DockUtB = 1700.0;
        private const double Cadence = SpanEndUT - SpanStartUT;
        // First tick reaches ONLY route A's dock; second tick reaches route B's
        // dock (A already fired cycle 0 so it does not re-cross).
        private const double TickUtDockA = 1300.0;
        private const double TickUtDockB = 1700.0;

        // ==================================================================
        // 1. STAGGERED-dock cross-tick alternation (the headline, 5.4)
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Round-trip linking (5.4): two linked one-way DELIVERY routes A (dock phase 1300) and B (dock phase 1700, linked to A) with DIFFERENT recorded dock phases alternate across SEPARATE ticks through the production RouteOrchestrator.Tick -> ProcessLoopRoute -> EmitLoopCycle path. Tick 1 (UT 1300) reaches ONLY A's dock: A (the seed) dispatches + completes its cycle (CompletedCycles 0->1) while B is NOT due (its dock phase is not reached) and, at the evaluator level, B would HOLD WaitingForPartner naming A. Tick 2 (UT 1700) reaches B's dock: B becomes eligible (A completed a cycle), dispatches + completes, and consumes A's cycle (LastConsumedPartnerCycle -> 1). At most one of the pair advances per tick (the one-transport-in-flight guarantee). Asserts CompletedCycles progression + the WaitingForPartner hold; this is the REAL cross-tick alternation, not a same-tick co-dispatch artifact")]
        public IEnumerator RoundTrip_StaggeredDocks_AlternatesAcrossTicks()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live delivery endpoint vessel");
            Vessel endpoint = FlightGlobals.ActiveVessel;

            string treeIdA = "ingame-rt-tA-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string treeIdB = "ingame-rt-tB-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeAId = "ingame-rt-A-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeBId = "ingame-rt-B-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var endpointSnap = SnapshotLoadedLiquidFuel(endpoint);
            List<Route> preExistingRoutes = SnapshotRoutes();

            RunLinkedPairScenario(
                label: "RoundTrip_Alternation",
                treeIds: new[] { treeIdA, treeIdB },
                routeIds: new[] { routeAId, routeBId },
                preExistingRoutes: preExistingRoutes,
                restoreState: () => RestoreLoadedLiquidFuel(endpointSnap),
                buildRoutes: () => new[]
                {
                    // A: seed (priority 0), dock at 1300, linked to B.
                    BuildLinkedDeliveryRoute(routeAId, treeIdA, endpoint,
                        linkedRouteId: routeBId, dispatchPriority: 0,
                        dockUT: DockUtA, deliveryAmount: DeliveryAmountA),
                    // B: non-seed (priority 1), dock at 1700, linked to A.
                    BuildLinkedDeliveryRoute(routeBId, treeIdB, endpoint,
                        linkedRouteId: routeAId, dispatchPriority: 1,
                        dockUT: DockUtB, deliveryAmount: DeliveryAmountB),
                },
                run: () =>
                {
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeAId, out Route a),
                        "Route A not stored");
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeBId, out Route b),
                        "Route B not stored");

                    // --- Cross-tick evaluator-level pre-check: BEFORE any tick, A
                    //     (the seed) is eligible via the deadlock break; B (non-seed)
                    //     HOLDS WaitingForPartner naming A. Order-independent. ---
                    var aElig0 = RouteDispatchEvaluator.CheckEligibility(a, TickUtDockA, new LiveRouteRuntimeEnvironment());
                    InGameAssert.IsTrue(aElig0.Eligible,
                        "Route A (the chain seed) must be eligible on a fresh mutual chain (deadlock break)");
                    var bElig0 = RouteDispatchEvaluator.CheckEligibility(b, TickUtDockA, new LiveRouteRuntimeEnvironment());
                    InGameAssert.IsFalse(bElig0.Eligible,
                        "Route B (non-seed) must HOLD on a fresh mutual chain until A completes a cycle");
                    InGameAssert.AreEqual(
                        RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner, bElig0.Kind,
                        "Route B's fresh-chain hold must be WaitingForPartner (not a cargo/funds/endpoint blocker)");
                    InGameAssert.IsNotNull(bElig0.Reason, "The WaitingForPartner hold must carry a partner: token");
                    InGameAssert.IsTrue(
                        bElig0.Reason.IndexOf("partner:", StringComparison.Ordinal) >= 0,
                        $"Route B's hold reason must name the partner (partner:<name>); reason was '{bElig0.Reason}'");

                    // --- TICK 1 (UT 1300): reaches ONLY route A's dock phase. A
                    //     dispatches + completes; B is NOT due (its dock at 1700 is
                    //     not reached). At most ONE of the pair advances. ---
                    RouteOrchestrator.Tick(TickUtDockA);

                    InGameAssert.AreEqual(1, a.CompletedCycles,
                        "Route A (seed) should complete its first cycle on tick 1 (its dock phase reached)");
                    InGameAssert.AreEqual(0, b.CompletedCycles,
                        "Route B must NOT complete on tick 1 (its dock phase 1700 was not reached AND it was holding for A) " +
                        "- only one transport's worth of effects is in flight at a time (5.4)");
                    // A consumed B's cycle 0 (B had completed 0); A's cursor stays 0.
                    InGameAssert.AreEqual(0, a.LastConsumedPartnerCycle,
                        "Route A consumed partner B's cycle 0 (B had completed 0)");

                    // After tick 1, B's gate must now CLEAR (A completed a cycle):
                    // partner(A).CompletedCycles(1) > B.LastConsumed(0).
                    var bElig1 = RouteDispatchEvaluator.CheckEligibility(b, TickUtDockB, new LiveRouteRuntimeEnvironment());
                    InGameAssert.IsTrue(bElig1.Eligible,
                        "Route B must become eligible after A completes a cycle (A->B alternation hand-off)");

                    // --- TICK 2 (UT 1700): reaches route B's dock phase. B
                    //     dispatches + completes + consumes A's cycle. A does NOT
                    //     re-fire (its cycle 0 already fired; LastObserved snapped). ---
                    RouteOrchestrator.Tick(TickUtDockB);

                    InGameAssert.AreEqual(1, b.CompletedCycles,
                        "Route B should complete its first cycle on tick 2 (A completed -> B alternates in)");
                    InGameAssert.AreEqual(1, a.CompletedCycles,
                        "Route A must NOT advance again on tick 2 (its cycle 0 already fired; only B fires)");
                    InGameAssert.AreEqual(1, b.LastConsumedPartnerCycle,
                        "Route B consumed partner A's cycle 1 on dispatch (alternation cursor advanced)");

                    ParsekLog.Info("TestRunner",
                        $"RoundTrip_Alternation: PASS routeA={routeAId} routeB={routeBId} " +
                        $"tick1=A(completed {a.CompletedCycles.ToString(IC)}) tick2=B(completed {b.CompletedCycles.ToString(IC)}) " +
                        $"bConsumed={b.LastConsumedPartnerCycle.ToString(IC)} - cross-tick alternation");
                });
            yield break;
        }

        // ==================================================================
        // 2. Paused-partner bypass (design 10.14)
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Paused-partner bypass (design 10.14): route B is linked to a PAUSED partner A and has already consumed A's baseline, so the alternation gate WOULD hold it - but a Paused (non-ghost-driving) partner can never complete a cycle, so the constraint is bypassed and B dispatches on its OWN schedule through the production Tick. B completes its cycle (CompletedCycles 0->1) while the Paused A dispatches nothing")]
        public IEnumerator RoundTrip_PausedPartner_BypassesAndDispatches()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live delivery endpoint vessel");
            Vessel endpoint = FlightGlobals.ActiveVessel;

            string treeIdA = "ingame-rt-paused-tA-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string treeIdB = "ingame-rt-paused-tB-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeAId = "ingame-rt-paused-A-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeBId = "ingame-rt-paused-B-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var endpointSnap = SnapshotLoadedLiquidFuel(endpoint);
            List<Route> preExistingRoutes = SnapshotRoutes();

            RunLinkedPairScenario(
                label: "RoundTrip_PausedPartner",
                treeIds: new[] { treeIdA, treeIdB },
                routeIds: new[] { routeAId, routeBId },
                preExistingRoutes: preExistingRoutes,
                restoreState: () => RestoreLoadedLiquidFuel(endpointSnap),
                buildRoutes: () =>
                {
                    // A: PAUSED (not ghost-driving). B linked to A, having already
                    // consumed A's baseline (cursor 0 == partner.CompletedCycles 0),
                    // so the alternation gate WOULD hold B - but the Paused bypass
                    // overrides. B's dock at 1300 (reached on the single tick).
                    Route a = BuildLinkedDeliveryRoute(routeAId, treeIdA, endpoint,
                        linkedRouteId: routeBId, dispatchPriority: 0,
                        dockUT: DockUtA, deliveryAmount: DeliveryAmountA);
                    a.Status = RouteStatus.Paused;
                    Route b = BuildLinkedDeliveryRoute(routeBId, treeIdB, endpoint,
                        linkedRouteId: routeAId, dispatchPriority: 1,
                        dockUT: DockUtA, deliveryAmount: DeliveryAmountB);
                    return new[] { a, b };
                },
                run: () =>
                {
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeAId, out Route a), "Route A not stored");
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeBId, out Route b), "Route B not stored");

                    // One tick at B's dock (1300). B bypasses the Paused partner and
                    // dispatches; A (Paused) dispatches nothing.
                    RouteOrchestrator.Tick(TickUtDockA);

                    InGameAssert.AreEqual(1, b.CompletedCycles,
                        "Route B must dispatch on its own schedule when its partner is Paused (10.14 bypass)");
                    InGameAssert.AreEqual(0, a.CompletedCycles,
                        "The Paused partner A must dispatch nothing");

                    ParsekLog.Info("TestRunner",
                        $"RoundTrip_PausedPartner: PASS routeB={routeBId} bypassed Paused partner routeA={routeAId} " +
                        $"bCompleted={b.CompletedCycles.ToString(IC)} aCompleted={a.CompletedCycles.ToString(IC)}");
                });
            yield break;
        }

        // ==================================================================
        // 3. Deadlock seed: mutual A<->B link, both fresh
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Deadlock seed (mutual A<->B link, neither completed): on a fresh mutual chain both routes compute partner.CompletedCycles(0) <= LastConsumed(0) = held, which without a seed deadlocks forever. The SEED (lower DispatchPriority, then ordinal id) dispatches its FIRST cycle while the non-seed HOLDS WaitingForPartner; after the seed completes once the non-seed alternates in. Drives the production Tick with both docks at 1300: the seed completes (CompletedCycles 1) and the non-seed then becomes eligible (asserted at the evaluator level for order-independence)")]
        public IEnumerator RoundTrip_DeadlockSeed_SeedDispatchesFirst()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live delivery endpoint vessel");
            Vessel endpoint = FlightGlobals.ActiveVessel;

            string treeIdA = "ingame-rt-seed-tA-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string treeIdB = "ingame-rt-seed-tB-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeAId = "ingame-rt-seed-A-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeBId = "ingame-rt-seed-B-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var endpointSnap = SnapshotLoadedLiquidFuel(endpoint);
            List<Route> preExistingRoutes = SnapshotRoutes();

            RunLinkedPairScenario(
                label: "RoundTrip_DeadlockSeed",
                treeIds: new[] { treeIdA, treeIdB },
                routeIds: new[] { routeAId, routeBId },
                preExistingRoutes: preExistingRoutes,
                restoreState: () => RestoreLoadedLiquidFuel(endpointSnap),
                buildRoutes: () => new[]
                {
                    // A seed (priority 0), B non-seed (priority 1). Both fresh,
                    // mutual link, both docks at 1300 so a single tick reaches both
                    // dock phases (proving the seed dispatches and the non-seed holds
                    // even when both could physically cross this tick).
                    BuildLinkedDeliveryRoute(routeAId, treeIdA, endpoint,
                        linkedRouteId: routeBId, dispatchPriority: 0,
                        dockUT: DockUtA, deliveryAmount: DeliveryAmountA),
                    BuildLinkedDeliveryRoute(routeBId, treeIdB, endpoint,
                        linkedRouteId: routeAId, dispatchPriority: 1,
                        dockUT: DockUtA, deliveryAmount: DeliveryAmountB),
                },
                run: () =>
                {
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeAId, out Route a), "Route A not stored");
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(routeBId, out Route b), "Route B not stored");

                    // Seed predicate is deterministic + structural (priority then id).
                    InGameAssert.IsTrue(RouteDispatchEvaluator.IsChainSeed(a, b),
                        "Route A (lower priority) must be the chain seed");
                    InGameAssert.IsFalse(RouteDispatchEvaluator.IsChainSeed(b, a),
                        "Route B (higher priority) must NOT be the chain seed");

                    // Fresh-chain verdict (evaluator level, order-independent): the
                    // SEED (A) is eligible via the deadlock break; the NON-seed (B)
                    // HOLDS WaitingForPartner.
                    InGameAssert.IsTrue(
                        RouteDispatchEvaluator.CheckEligibility(a, TickUtDockA, new LiveRouteRuntimeEnvironment()).Eligible,
                        "The chain seed (A) must dispatch first on a fresh mutual chain (deadlock break)");
                    var bElig0 = RouteDispatchEvaluator.CheckEligibility(b, TickUtDockA, new LiveRouteRuntimeEnvironment());
                    InGameAssert.IsFalse(bElig0.Eligible,
                        "The non-seed (B) must HOLD on a fresh mutual chain (no double-seed)");
                    InGameAssert.AreEqual(
                        RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner, bElig0.Kind,
                        "The non-seed's fresh-chain hold must be WaitingForPartner");

                    // Drive the tick: the seed (A) processes first and completes; the
                    // non-seed (B) then sees A completed within the tick and may also
                    // co-dispatch (the same-tick hand-off the cross-tick test #1
                    // separates). The seed must have completed exactly one cycle.
                    RouteOrchestrator.Tick(TickUtDockA);

                    InGameAssert.AreEqual(1, a.CompletedCycles,
                        "The seed (A) must complete its first cycle (deadlock broken)");
                    // The non-seed alternates in once A completed; after the tick B
                    // has consumed A's cycle 1 (A processed first this tick).
                    InGameAssert.AreEqual(1, b.CompletedCycles,
                        "The non-seed (B) alternates in once the seed completed (A->B hand-off within the tick)");
                    InGameAssert.AreEqual(1, b.LastConsumedPartnerCycle,
                        "B consumed A's cycle 1 (A completed before B processed this tick)");

                    ParsekLog.Info("TestRunner",
                        $"RoundTrip_DeadlockSeed: PASS seed={routeAId} nonSeed={routeBId} " +
                        $"seedCompleted={a.CompletedCycles.ToString(IC)} nonSeedCompleted={b.CompletedCycles.ToString(IC)}");
                });
            yield break;
        }

        // ==================================================================
        // 4. SaveGame round-trip: alternation cursor persists + resumes
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Save/reload preserves alternation: a linked pair runs A (completed 1, B consumed it) so B holds WaitingForPartner; a real GamePersistence.SaveGame persists Route.LastConsumedPartnerCycle into the .sfs (sparse, omitted at 0); the routes reload from the persisted node via the production RouteStore.LoadRoutesFrom codec with the cursor intact; and after the partner completes ANOTHER cycle the reloaded B becomes eligible again (alternation resumes correctly across the reload). Pauses time warp across the save; restores state + deletes the disposable slot in finally")]
        public IEnumerator RoundTrip_SaveReload_PreservesAlternation()
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
            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live delivery endpoint vessel");
            Vessel endpoint = FlightGlobals.ActiveVessel;

            string treeIdA = "ingame-rt-save-tA-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string treeIdB = "ingame-rt-save-tB-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeAId = "ingame-rt-save-A-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeBId = "ingame-rt-save-B-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string saveSlot = TestSaveSlotPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);

            bool warpPaused = false;
            int warpIndexBefore = TimeWarp.CurrentRateIndex;
            var endpointSnap = SnapshotLoadedLiquidFuel(endpoint);
            List<Route> preExistingRoutes = SnapshotRoutes();

            RecordingTree treeA = BuildBackingTree(treeIdA);
            RecordingTree treeB = BuildBackingTree(treeIdB);
            var committedAdded = new List<Recording>();
            bool treesAdded = false, storeWiped = false;

            try
            {
                if (warpIndexBefore > 0)
                {
                    TimeWarp.SetRate(0, true);
                    warpPaused = true;
                }

                string savePath = Path.Combine(
                    KSPUtil.ApplicationRootPath ?? string.Empty,
                    "saves",
                    HighLogic.SaveFolder ?? string.Empty,
                    saveSlot + ".sfs");

                RecordingStore.AddCommittedTreeForTesting(treeA);
                RecordingStore.AddCommittedTreeForTesting(treeB);
                treesAdded = true;
                foreach (RecordingTree t in new[] { treeA, treeB })
                    foreach (Recording rec in t.Recordings.Values)
                    {
                        if (rec == null) continue;
                        RecordingStore.AddCommittedInternal(rec);
                        committedAdded.Add(rec);
                    }

                RouteStore.ResetForTesting();
                storeWiped = true;

                // A: completed 1 cycle already; B: linked to A, already consumed A's
                // single completion (cursor 1, completed 1). So B holds
                // WaitingForPartner (partner.CompletedCycles 1 <= B.cursor 1).
                Route a = BuildLinkedDeliveryRoute(routeAId, treeIdA, endpoint,
                    linkedRouteId: routeBId, dispatchPriority: 0,
                    dockUT: DockUtA, deliveryAmount: DeliveryAmountA);
                a.Name = "Outbound Supply";
                a.CompletedCycles = 1;
                Route b = BuildLinkedDeliveryRoute(routeBId, treeIdB, endpoint,
                    linkedRouteId: routeAId, dispatchPriority: 1,
                    dockUT: DockUtB, deliveryAmount: DeliveryAmountB);
                b.CompletedCycles = 1;
                b.LastConsumedPartnerCycle = 1;
                RouteStore.AddRoute(a);
                RouteStore.AddRoute(b);

                // B holds WaitingForPartner BEFORE the save (partner A has not
                // completed a NEW cycle since B last consumed one).
                var bEligBefore = RouteDispatchEvaluator.CheckEligibility(b, DockUtB, new LiveRouteRuntimeEnvironment());
                InGameAssert.IsFalse(bEligBefore.Eligible,
                    "Route B must HOLD WaitingForPartner before the save (A has not completed a new cycle)");
                InGameAssert.AreEqual(
                    RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner, bEligBefore.Kind,
                    "Route B's pre-save hold must be WaitingForPartner");

                // --- SAVE: persist the alternation cursor into the .sfs. ---
                string saveResult = GamePersistence.SaveGame(saveSlot, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                InGameAssert.IsTrue(!string.IsNullOrEmpty(saveResult),
                    $"GamePersistence.SaveGame returned null/empty for slot '{saveSlot}'");

                yield return null; // let deferred-one-frame writes settle

                InGameAssert.IsTrue(File.Exists(savePath), $"Expected .sfs at '{savePath}' after save");
                ConfigNode root = ConfigNode.Load(savePath);
                InGameAssert.IsNotNull(root, $"ConfigNode.Load returned null for '{savePath}'");

                if (!TryFindRouteNode(root, routeBId, out ConfigNode routeBNode))
                    InGameAssert.Skip(
                        $"Could not locate the ROUTE node id={routeBId} in '{savePath}' (save layout mismatch); " +
                        "skipping rather than false-failing - the pre-save WaitingForPartner hold above passed");
                // B's cursor is 1 (non-default) so it must be WRITTEN.
                InGameAssert.AreEqual("1", routeBNode.GetValue("lastConsumedPartnerCycle"),
                    "Saved route B must carry lastConsumedPartnerCycle=1 (the consumed alternation cursor)");
                InGameAssert.AreEqual(routeAId, routeBNode.GetValue("linkedRouteId"),
                    "Saved route B must carry its linkedRouteId");
                // A's cursor is 0 (default) so it must be OMITTED (sparse byte-identity).
                if (TryFindRouteNode(root, routeAId, out ConfigNode routeANode))
                    InGameAssert.IsNull(routeANode.GetValue("lastConsumedPartnerCycle"),
                        "Saved route A must OMIT lastConsumedPartnerCycle (cursor 0 default, sparse)");

                // --- RELOAD via the production codec. ---
                RouteStore.ResetForTesting();
                ConfigNode scenarioWrap = FindParsekScenarioNode(root) ?? root;
                int loaded = RouteStore.LoadRoutesFrom(scenarioWrap);
                InGameAssert.IsTrue(loaded >= 2, "Reload should restore both synthetic linked routes from the .sfs");
                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeAId, out Route reloadedA),
                    "Route A did not survive the save/reload round-trip");
                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeBId, out Route reloadedB),
                    "Route B did not survive the save/reload round-trip");
                InGameAssert.AreEqual(1, reloadedB.LastConsumedPartnerCycle,
                    "Reloaded route B must carry the persisted alternation cursor (1)");
                InGameAssert.AreEqual(0, reloadedA.LastConsumedPartnerCycle,
                    "Reloaded route A must carry the default cursor (0, was omitted in the save)");
                InGameAssert.AreEqual(routeAId, reloadedB.LinkedRouteId,
                    "Reloaded route B must keep its partner link");

                // Reloaded B still HOLDS (partner A at completed 1, cursor 1).
                var bEligAfterReload = RouteDispatchEvaluator.CheckEligibility(reloadedB, DockUtB, new LiveRouteRuntimeEnvironment());
                InGameAssert.IsFalse(bEligAfterReload.Eligible,
                    "Reloaded route B must STILL hold WaitingForPartner (cursor restored; A has not completed a new cycle)");

                // The partner completes ANOTHER cycle -> the reloaded B alternates in.
                reloadedA.CompletedCycles = 2;
                var bEligAfterPartnerAdvance = RouteDispatchEvaluator.CheckEligibility(reloadedB, DockUtB, new LiveRouteRuntimeEnvironment());
                InGameAssert.IsTrue(bEligAfterPartnerAdvance.Eligible,
                    "After the reload, route B must become eligible once A completes ANOTHER cycle " +
                    "(alternation resumes from the persisted cursor)");

                ParsekLog.Info("TestRunner",
                    $"RoundTrip_SaveReload: PASS slot='{saveSlot}' routeB={routeBId} " +
                    $"persistedCursor={reloadedB.LastConsumedPartnerCycle.ToString(IC)} alternation resumed");
            }
            finally
            {
                if (storeWiped)
                    RestoreRoutes(preExistingRoutes);
                for (int i = 0; i < committedAdded.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedAdded[i]);
                if (treesAdded)
                {
                    RemoveCommittedTree(treeIdA);
                    RemoveCommittedTree(treeIdB);
                }
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);
                RestoreLoadedLiquidFuel(endpointSnap);
                QuickloadResumeHelpers.TryDeleteSaveSlot(saveSlot);
                if (warpPaused)
                {
                    try { TimeWarp.SetRate(warpIndexBefore, true); }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn("TestRunner",
                            $"RoundTrip_SaveReload cleanup: failed to restore time warp ({ex.GetType().Name}: {ex.Message})");
                    }
                }
            }
        }

        // ==================================================================
        // Shared scenario runner (synchronous arrange / act / assert / teardown)
        // ==================================================================

        /// <summary>
        /// Shared arrange / act / assert / teardown frame for the synchronous
        /// linked-pair cases: commits both backing trees + their member recordings
        /// (so the ERS eligibility gate is real), wipes RouteStore down to the
        /// caller-built linked pair, arms the loop-unit resolver seam (route-id
        /// scoped) with the span unit for both routes, runs the caller's
        /// <paramref name="run"/> body (which drives one or more production
        /// <see cref="RouteOrchestrator.Tick(double)"/> calls + the assertions), and
        /// restores everything (store, committed trees, endpoint resource state) in
        /// finally. Both delivery halves run on the LIVE production path (no
        /// DeliveryRowEmitterForTesting seam) so the real per-cycle delivery + the
        /// dispatch-time alternation advance are exercised. Yield-free (all yields
        /// happen in the caller's precondition unpack wait BEFORE this runs).
        /// </summary>
        private static void RunLinkedPairScenario(
            string label,
            string[] treeIds,
            string[] routeIds,
            List<Route> preExistingRoutes,
            Func<Route[]> buildRoutes,
            Action restoreState,
            Action run)
        {
            GhostPlaybackLogic.LoopUnit loopUnit = BuildSpanLoopUnit();
            var routeIdSet = new HashSet<string>(routeIds, StringComparer.Ordinal);

            bool treesAdded = false, storeWiped = false, resolverArmed = false;
            var previousResolver = RouteOrchestrator.LoopUnitResolverForTesting;
            var committedAdded = new List<Recording>();

            try
            {
                for (int t = 0; t < treeIds.Length; t++)
                {
                    RecordingTree tree = BuildBackingTree(treeIds[t]);
                    RecordingStore.AddCommittedTreeForTesting(tree);
                    foreach (Recording rec in tree.Recordings.Values)
                    {
                        if (rec == null) continue;
                        RecordingStore.AddCommittedInternal(rec);
                        committedAdded.Add(rec);
                    }
                }
                treesAdded = true;

                RouteStore.ResetForTesting();
                storeWiped = true;
                Route[] routes = buildRoutes();
                for (int i = 0; i < routes.Length; i++)
                    RouteStore.AddRoute(routes[i]);

                RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) =>
                {
                    if (r != null && routeIdSet.Contains(r.Id))
                        return loopUnit;
                    return previousResolver != null
                        ? previousResolver(r, ut)
                        : (GhostPlaybackLogic.LoopUnit?)null;
                };
                resolverArmed = true;

                ParsekLog.Verbose("TestRunner",
                    $"{label}: pre-run routes=[{string.Join(",", routeIds)}] trees=[{string.Join(",", treeIds)}]");

                run();
            }
            finally
            {
                if (resolverArmed)
                    RouteOrchestrator.LoopUnitResolverForTesting = previousResolver;
                if (storeWiped)
                    RestoreRoutes(preExistingRoutes);
                for (int i = 0; i < committedAdded.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedAdded[i]);
                if (treesAdded)
                    for (int t = 0; t < treeIds.Length; t++)
                        RemoveCommittedTree(treeIds[t]);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);

                try
                {
                    restoreState?.Invoke();
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
        /// Synthetic single-stop DELIVERY loop route for round-trip linking: a
        /// harvest-origin route (no KSC funds, no origin vessel to resolve, empty
        /// CostManifest) delivering <paramref name="deliveryAmount"/> LiquidFuel
        /// onto <paramref name="endpoint"/> at its recorded dock phase
        /// <paramref name="dockUT"/>, linked to <paramref name="linkedRouteId"/>.
        /// Single-stop, so it fires through the scalar ProcessLoopRoute ->
        /// EmitLoopCycle -> ApplyDelivery path (the same path the C1 xUnit suite
        /// drives), where AdvancePartnerAlternationOnDispatch runs on a genuine
        /// fire. Harvest origin isolates the chain constraint from funds /
        /// origin-debit (covered by M1/M2/M3/M4b).
        /// </summary>
        private static Route BuildLinkedDeliveryRoute(
            string routeId, string treeId, Vessel endpoint,
            string linkedRouteId, int dispatchPriority, double dockUT, double deliveryAmount)
        {
            return new Route
            {
                Id = routeId,
                Name = "Parsek Round-Trip Link In-Game (" + routeId.Substring(0, Math.Min(12, routeId.Length)) + ")",
                Status = RouteStatus.Active,
                IsKscOrigin = false,
                IsHarvestOrigin = true,
                BackingMissionTreeId = treeId,
                ExcludedIntervalKeys = new HashSet<string>(),
                RecordedDockUT = dockUT,
                DockMemberRecordingId = "dockedStation",
                LoopAnchorUT = SpanStartUT,
                LastObservedLoopCycleIndex = -1,
                TransitDuration = Cadence,
                DispatchInterval = Cadence,
                NextDispatchUT = SpanStartUT, // due immediately (well before the first tick)
                DispatchPriority = dispatchPriority,
                LinkedRouteId = linkedRouteId,
                LastConsumedPartnerCycle = 0,
                CompletedCycles = 0,
                SkippedCycles = 0,
                KscDispatchFundsCost = 0.0,
                // Harvest origin: nothing launched, nothing loaded en route - the
                // cargo is delivered at the dock. CostManifest stays EMPTY.
                CostManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                InventoryCostManifest = new List<InventoryPayloadItem>(),
                Origin = EndpointForVessel(endpoint),
                RecordingIds = new List<string> { "launch", "midToDock", "dockedStation" },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "launch", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "midToDock", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "dockedStation", TreeId = treeId },
                },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = EndpointForVessel(endpoint),
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        PickupManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                        InventoryPickupManifest = new List<InventoryPayloadItem>(),
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, deliveryAmount },
                        },
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 0,
                        RecordedDockUT = dockUT,
                        LastFiredCycleIndex = -1,
                    },
                },
            };
        }

        // launch -> dock -> terminal undock. Mirrors the verified single-window
        // topology so the ERS membership gate resolves the member recordings; the
        // fire path resolves the loop unit through the seam, so the exact
        // composition is not load-bearing (the member recordings just need to be
        // committed).
        private static RecordingTree BuildBackingTree(string treeId)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = "launch" };
            tree.Recordings["launch"] = Leg("launch", "C0", 0, 1000, 1200, "Transport");
            tree.Recordings["midToDock"] = Leg("midToDock", "C0", 1, 1200, 1700, "Transport");
            tree.Recordings["dockedStation"] = Leg("dockedStation", "C0", 2, 1700, 2000, "Transport");
            tree.Recordings["tail"] = Leg("tail", "C0", 3, 2000, 2300, "Transport");
            tree.BranchPoints.Add(BP("dock-bp", BranchPointType.Dock,
                new[] { "midToDock" }, new[] { "dockedStation" }, 1700));
            tree.BranchPoints.Add(BP("undock-bp", BranchPointType.Undock,
                new[] { "dockedStation" }, new[] { "tail" }, 2000));
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

        // ==================================================================
        // .sfs lookup helpers (mirror the multi-origin suite)
        // ==================================================================

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
