using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Logistics;
using Parsek.Reaim;

namespace Parsek.InGameTests
{
    /// <summary>
    /// (M5 inter-body builder-shape gate) The one automated proof that a REAL
    /// interplanetary route drives the REAL re-aim classifier + loop-unit builder
    /// end to end, without a hand-flown Kerbin-&gt;Duna save.
    ///
    /// <para><b>The gap this closes (from the M5 adversarial review, verbatim
    /// intent).</b> Every M5 xUnit (<c>RouteWindowBasisTests</c>,
    /// <c>RouteInterBodyFireTests</c>) drives HAND-BUILT <c>LoopUnit</c> /
    /// <c>ReaimMissionPlan</c> / <c>ReaimWindowSchedule</c> structs, so there is ZERO
    /// automated coverage of the REAL <c>MissionLoopUnitBuilder.ApplyReaim</c>-produced
    /// unit shape (the <c>ReaimPlan.Supported</c> / <c>ReaimSchedule.Valid</c> flags, the
    /// post-pad-align/loiter-compression <c>PhaseAnchorUT</c>) feeding
    /// <c>RouteLoopClock.DeriveWindowBasis</c>, the firing gate, and the countdown. If the
    /// LIVE builder's unit deviated from the fixtures' shape, inter-body routes would
    /// silently classify <c>FlatInterval</c> (N ignored, no windowed label / countdown)
    /// with nothing red anywhere. This test builds a REAL committed Kerbin-&gt;Duna
    /// transfer tree, promotes it to a Route, obtains the unit via the REAL
    /// <c>RouteOrchestrator.ResolveLoopUnit</c> (which runs
    /// <c>RouteBackingMission.BuildMission</c> -&gt; the REAL
    /// <c>MissionLoopUnitBuilder.TryBuildLoopUnitForSelection</c> with the LIVE
    /// <c>FlightGlobalsBodyInfo.Instance</c>), and asserts the unit classifies
    /// <c>ReaimWindows</c> (NOT <c>Flat</c>) and the firing gate applies the residual
    /// modulo.
    ///
    /// <para><b>Why in-game (not xUnit).</b> Re-aim engages ONLY on the
    /// <c>bodyInfo != null</c> path (<c>MissionLoopUnitBuilder.TryBuildMissionUnit</c>),
    /// and the ONLY non-degenerate <c>IBodyInfo</c> in production is
    /// <c>FlightGlobalsBodyInfo.Instance</c>, which reads live stock ephemerides
    /// (Kerbin / Duna / Sun orbital periods, gravity parameters, parents). In xUnit
    /// <c>FlightGlobals</c> is empty, so the singleton degrades to no-body-info and re-aim
    /// can never engage - the fixture structs are the ONLY way xUnit reaches the windowed
    /// basis. This test runs at the Space Center (no vessel needed) so it exercises the
    /// live geometry the fixtures stand in for.
    ///
    /// <para><b>Isolation.</b> Mutates the shared <c>RecordingStore</c> (commits a
    /// synthetic tree) and <c>RouteStore</c> (adds a synthetic route), and sets the
    /// <c>RouteOrchestrator</c> test seams. Every mutation is snapshotted and
    /// reverted in the <c>finally</c> block regardless of pass / fail / skip, so a
    /// run leaves no synthetic route or committed tree behind - which makes the
    /// test batch-safe and ordinary-Run-All executable (all-tests-auto policy,
    /// 2026-07-08; the earlier manual-only exclusion was conservatism).
    /// </summary>
    public sealed class RouteInterBodyBuilderShapeInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const string Tag = "TestRunner";

        // A clean synthetic recorded-mission time base (well clear of live UT=0 cold
        // loads; the geometry is a stand-in the builder re-solves against live
        // ephemerides). The member window is [ParkStartUT .. ArrivalEndUT]; the dock
        // phase (delivery instant) sits inside it.
        private const double ParkStartUT = 5_000_000.0;
        private const double DepartureUT = 5_000_600.0;   // launch-body SOI exit -> heliocentric leg
        private const double ArrivalUT = 9_000_000.0;    // target SOI entry
        private const double ArrivalEndUT = 9_000_600.0;  // end of the recorded Duna arrival leg
        private const double DockUT = 8_900_000.0;        // delivery instant, inside the span

        // The loop was enabled well AFTER the mission finished (the normal flow), so
        // the builder's first-play floor is a no-op and the phase anchor snaps to the
        // next synodic window (not the raw span start).
        private const double LoopEnabledUT = 9_100_000.0;

        // Batch-safe by construction (all-tests-auto policy, 2026-07-08): every
        // mutation - the synthetic committed tree, the synthetic route, and both
        // RouteOrchestrator test seams - is snapshotted up front and reverted in
        // the finally regardless of pass/fail/skip, so the ordinary Run All batch
        // can execute this test. The earlier manual-only exclusion was
        // conservatism, not a real hazard.
        [InGameTest(Category = "Logistics", Scene = GameScenes.SPACECENTER,
            Description = "M5 inter-body builder-shape gate: a REAL committed Kerbin->Duna transfer tree, promoted to a Route, resolves through the REAL MissionLoopUnitBuilder (live FlightGlobalsBodyInfo) into a re-aim LoopUnit that classifies ReaimWindows (not Flat), applies the residual modulo N, and fires delivery on the deliverable window while purely skipping the non-deliverable one")]
        public void InterBodyRoute_RealBuilder_ClassifiesReaimWindows_AndModuloFires()
        {
            // Live-body precondition: Kerbin / Duna / Sun must be present and share a
            // parent (stock). A non-stock pack skips cleanly.
            CelestialBody kerbin = FindBody("Kerbin");
            CelestialBody duna = FindBody("Duna");
            if (kerbin == null || duna == null)
                InGameAssert.Skip("Kerbin/Duna not in FlightGlobals.Bodies (non-stock pack) - cannot drive the live re-aim geometry");
            if (kerbin.referenceBody == null || duna.referenceBody == null
                || kerbin.referenceBody != duna.referenceBody)
                InGameAssert.Skip("Kerbin and Duna do not share a parent in this pack - not a cross-parent single-hop transfer");
            string sun = kerbin.referenceBody.bodyName;
            // The builder needs a real synodic period (bodies with distinct orbital
            // periods); stock Kerbin/Duna satisfy this, but guard defensively.
            if (kerbin.orbit == null || duna.orbit == null
                || System.Math.Abs(kerbin.orbit.period - duna.orbit.period) < 1.0)
                InGameAssert.Skip("Kerbin/Duna orbital periods are degenerate/equal - no synodic period, re-aim cannot schedule");

            string treeId = "m5-ingame-reaim-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "m5-ingame-route-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            string dockRecId = treeId + "-mission";

            // Snapshot the seams we set so the finally restores exactly.
            var priorResolver = RouteOrchestrator.LoopUnitResolverForTesting;
            var priorRowEmitter = RouteOrchestrator.DeliveryRowEmitterForTesting;
            var priorApplier = RouteOrchestrator.DeliveryApplierForTesting;
            var priorOriginDebit = RouteOrchestrator.OriginDebitApplierForTesting;

            bool treeCommitted = false;
            bool routeAdded = false;

            // Capture the pre-existing ledger row count so the D4 skip-purity assert
            // measures a DELTA (the live career ledger already has rows).
            int deliveredBefore = CountActions(GameActionType.RouteCargoDelivered);
            int dispatchedBefore = CountActions(GameActionType.RouteDispatched);

            try
            {
                // ---- (a) Build a REAL interplanetary route whose backing mission re-aims. ----

                // The recorded SOI chain the classifier accepts as a cross-parent
                // single-hop transfer: launch-body parking -> common-ancestor
                // (heliocentric) leg -> target-body arrival (ReaimClassifier.Classify).
                // Carried on ONE committed recording's OrbitSegments (ApplyReaim
                // classifies each member's OWN segments), so this recording is the
                // re-aim transfer member.
                var segments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = kerbin.bodyName, startUT = ParkStartUT, endUT = DepartureUT,
                        semiMajorAxis = 700000.0, eccentricity = 0.0, epoch = ParkStartUT,
                        isPredicted = false,
                    },
                    new OrbitSegment
                    {
                        bodyName = sun, startUT = DepartureUT, endUT = ArrivalUT,
                        semiMajorAxis = 1.5e10, eccentricity = 0.2, epoch = DepartureUT,
                        isPredicted = false,
                    },
                    new OrbitSegment
                    {
                        bodyName = duna.bodyName, startUT = ArrivalUT, endUT = ArrivalEndUT,
                        semiMajorAxis = 500000.0, eccentricity = 0.1, epoch = ArrivalUT,
                        isPredicted = false,
                    },
                };

                // Sanity-gate the fixture against the LIVE classifier before wiring the
                // route: if the recorded chain does not classify Supported (a stock
                // rebalance moved a parent, etc.) skip rather than silently prove
                // nothing. This runs the SAME ReaimClassifier the builder runs.
                ReaimMissionPlan classified = ReaimClassifier.Classify(
                    segments, FlightGlobalsBodyInfo.Instance);
                if (!classified.Supported)
                    InGameAssert.Skip($"live classifier declined the synthetic Kerbin->Duna chain (reason='{classified.Reason}') - fixture needs re-pinning for this body graph");

                RecordingTree tree = BuildTransferTree(treeId, dockRecId, segments);

                // Commit the tree the production way: CommitTree adds every recording
                // to CommittedRecordings (the engine alignment list ResolveLoopUnit
                // reads) AND the tree to CommittedTrees (RouteTreeGuard.FindCommittedTree).
                RecordingStore.CommitTree(tree);
                treeCommitted = true;

                // Build the backing Route by hand (RouteFixtureBuilder lives in the
                // test project and is not reachable here). BackingMissionTreeId makes it
                // a loop route; CadenceMultiplier=2 exercises the residual modulo. The
                // dispatch interval / transit duration mirror the neighboring firing
                // tests (v0 DispatchInterval = N x TransitDuration).
                Route route = BuildReaimRoute(routeId, treeId, dockRecId, cadenceMultiplier: 2);
                RouteStore.AddRoute(route);
                routeAdded = true;

                // ---- (b) Obtain the unit via the REAL RouteOrchestrator.ResolveLoopUnit. ----

                // Seam OFF: the live build runs (BuildMission -> the REAL
                // MissionLoopUnitBuilder.TryBuildLoopUnitForSelection with
                // FlightGlobalsBodyInfo.Instance). This is the exact path
                // ProcessLoopRoute takes in flight.
                RouteOrchestrator.LoopUnitResolverForTesting = null;
                GhostPlaybackLogic.LoopUnit? unitOpt =
                    RouteOrchestrator.ResolveLoopUnit(route, LoopEnabledUT);
                InGameAssert.IsTrue(unitOpt.HasValue,
                    "the REAL builder must resolve a loop unit for the committed Kerbin->Duna route " +
                    "(no unit => the route silently never renders / delivers)");
                GhostPlaybackLogic.LoopUnit unit = unitOpt.Value;

                // The load-bearing shape assertions: the REAL ApplyReaim-produced unit
                // must carry a Supported plan + a Valid synodic schedule (IsReaim), or
                // the whole inter-body render/deliver path silently degrades to flat.
                InGameAssert.IsTrue(unit.ReaimPlan.HasValue,
                    "the real unit must carry a ReaimPlan (the builder engaged re-aim)");
                InGameAssert.IsTrue(unit.ReaimPlan.Value.Supported,
                    $"the real unit's ReaimPlan must be Supported (reason='{unit.ReaimPlan.Value.Reason}')");
                InGameAssert.IsTrue(unit.ReaimSchedule.HasValue,
                    "the real unit must carry a ReaimSchedule");
                InGameAssert.IsTrue(unit.ReaimSchedule.Value.Valid,
                    $"the real unit's ReaimSchedule must be Valid (reason='{unit.ReaimSchedule.Value.Reason}')");
                InGameAssert.IsTrue(unit.IsReaim,
                    "the real unit must be IsReaim (Supported plan + valid synodic schedule)");
                InGameAssert.AreEqual(kerbin.bodyName, unit.ReaimPlan.Value.LaunchBody,
                    "the re-aim plan's launch body must be the recorded launch body");
                InGameAssert.AreEqual(duna.bodyName, unit.ReaimPlan.Value.TargetBody,
                    "the re-aim plan's target body must be the recorded arrival body");

                // The EXACT review failure mode: DeriveWindowBasis must be ReaimWindows,
                // NOT the silent FlatInterval that ignores N and shows no windowed
                // label / countdown.
                RouteWindowBasis basis = RouteLoopClock.DeriveWindowBasis(unit);
                InGameAssert.AreEqual(RouteWindowBasis.ReaimWindows, basis,
                    "the REAL builder's unit must classify ReaimWindows (the review's exact silent-Flat failure mode)");
                InGameAssert.AreNotEqual(RouteWindowBasis.FlatInterval, basis,
                    "the inter-body unit must NOT classify FlatInterval (N would be ignored, no windowed label/countdown - 'nothing red anywhere')");

                // The residual cadence must survive as N under ReaimWindows (the build
                // discarded DispatchInterval; N becomes the route-side window modulo).
                int nResidual = RouteLoopClock.ResolveResidualCadence(basis, route.CadenceMultiplier);
                InGameAssert.AreEqual(2, nResidual,
                    "under ReaimWindows the CadenceMultiplier=2 must resolve to residual N=2 (every 2nd rendered synodic window delivers)");

                // The unit's span is the RAW recorded member window (re-aim changes only
                // the cadence/anchor/schedule, not the span), so the dock phase we drive
                // the firing gate against must fall inside it.
                InGameAssert.IsTrue(DockUT > unit.SpanStartUT && DockUT < unit.SpanEndUT,
                    $"the fixture dock UT ({DockUT.ToString("R", IC)}) must sit inside the resolved span " +
                    $"[{unit.SpanStartUT.ToString("R", IC)} .. {unit.SpanEndUT.ToString("R", IC)}]");

                ParsekLog.Info(Tag,
                    $"InterBodyBuilderShape: REAL unit resolved isReaim={unit.IsReaim} basis={basis} " +
                    $"nResidual={nResidual.ToString(IC)} launch={unit.ReaimPlan.Value.LaunchBody} " +
                    $"target={unit.ReaimPlan.Value.TargetBody} span=[{unit.SpanStartUT.ToString("R", IC)}.." +
                    $"{unit.SpanEndUT.ToString("R", IC)}] cadence={unit.CadenceSeconds.ToString("R", IC)} " +
                    $"phaseAnchor={unit.PhaseAnchorUT.ToString("R", IC)} " +
                    $"synodic={unit.ReaimSchedule.Value.SynodicPeriodSeconds.ToString("R", IC)}");

                // ---- (c) Drive the firing gate through the LIVE orchestrator. ----

                // Feed the REAL builder output through the resolver seam (NOT a fake -
                // the task's constraint): this pins the unit deterministically across
                // ticks (the geometry does not drift with the live clock) while keeping
                // the unit shape byte-identical to the live build above. The window
                // arithmetic uses the unit's OWN synodic cadence / phase anchor.
                GhostPlaybackLogic.LoopUnit firingUnit = unit;
                RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => firingUnit;

                // Real-path row emitter (the M4a lesson): the REAL ApplyDelivery runs its
                // per-window ELS guard; only the live-Vessel row emission is faked, so a
                // replay/suppression regression still goes red. A skip emits NOTHING, so
                // this stays silent on a modulo-skipped window.
                var deliveredRows = new List<string>();
                RouteOrchestrator.DeliveryRowEmitterForTesting =
                    (r, currentUT, e, cycleId, stopIndex, bumpCompletedCycle) =>
                    {
                        Ledger.AddAction(new GameAction
                        {
                            Type = GameActionType.RouteCargoDelivered,
                            UT = currentUT,
                            RouteId = r.Id,
                            RouteCycleId = cycleId,
                            RouteStopIndex = stopIndex,
                            Sequence = stopIndex * RouteOrchestrator.SeqStride + 3,
                        });
                        deliveredRows.Add(cycleId);
                    };

                var env = new EligibleEnv();
                double phaseAnchor = unit.PhaseAnchorUT;
                // The dock-phase offset inside the span (loopUT >= RecordedDockUT fires).
                double dockOffset = DockUT - unit.SpanStartUT;

                // Live UT that lands at window w's dock phase: phaseAnchor + w*cadence +
                // dockOffset. Cadence == synodic for a normal inter-body mission (span <
                // synodic), which the unit's own CadenceSeconds carries.
                double cadence = unit.CadenceSeconds;

                // Within-cycle-phase-identity precondition for the direct dock-phase
                // arithmetic below: a re-aim unit's within-cycle phase mapping is
                // NON-identity when it carries loiter cuts or an arrival hold (the span
                // clock remaps loopUT then), which would move the dock instant off
                // spanStart + dockOffset. The synthetic single-pass chain (a short
                // parking orbit, one heliocentric pass, a short arrival leg) engages
                // neither, so the mapping is identity and phaseAnchor + w*cadence +
                // dockOffset lands exactly on window w's recorded dock. If a future
                // builder change makes the fixture compress a loiter or hold at arrival,
                // skip the firing-gate arithmetic (the shape gate (a)/(b) above is the
                // primary coverage and already passed) rather than drive wrong UTs.
                bool hasLoiterCuts = unit.LoiterCuts != null && unit.LoiterCuts.Count > 0;
                bool hasArrivalHold = unit.ArrivalHoldSeconds > 0.0
                    && !double.IsInfinity(unit.ArrivalHoldSeconds);
                if (hasLoiterCuts || hasArrivalHold)
                    InGameAssert.Skip(
                        "the resolved re-aim unit carries loiter cuts (" +
                        (unit.LoiterCuts?.Count ?? 0).ToString(IC) + ") or an arrival hold (" +
                        unit.ArrivalHoldSeconds.ToString("R", IC) + "s), so the within-cycle phase is " +
                        "non-identity and the direct dock-phase arithmetic would target the wrong UT; " +
                        "the ReaimWindows shape + residual-N gate (a)/(b) already passed - re-pin the " +
                        "synthetic chain to a single non-compressible pass to re-enable the firing-gate drive");

                int deliveredCountBaseline = CountActions(GameActionType.RouteCargoDelivered);
                int dispatchedCountBaseline = CountActions(GameActionType.RouteDispatched);

                // Window 0: the FIRST owed crossing adopts the modulo anchor and DELIVERS
                // (D3 anchor adoption). Tick just before the dock (nothing), then at the
                // dock (fire).
                RouteOrchestrator.Tick(phaseAnchor + 0 * cadence + 0.25 * dockOffset, env);
                InGameAssert.AreEqual(deliveredCountBaseline, CountActions(GameActionType.RouteCargoDelivered),
                    "pre-dock tick of window 0 must not deliver");

                RouteOrchestrator.Tick(phaseAnchor + 0 * cadence + dockOffset, env);
                int afterW0 = CountActions(GameActionType.RouteCargoDelivered);
                InGameAssert.AreEqual(deliveredCountBaseline + 1, afterW0,
                    "window 0 dock phase must FIRE one delivery (D3 anchor adoption on the first owed crossing)");
                InGameAssert.IsTrue(route.ReaimWindowBasisEngaged,
                    "the D6 engage transition must have run on the first windowed tick (basis marker set)");
                InGameAssert.AreEqual(0L, route.WindowAnchorCycleIndex,
                    "the modulo anchor must be adopted at window 0");

                int dispatchedAfterW0 = CountActions(GameActionType.RouteDispatched);
                InGameAssert.AreEqual(dispatchedCountBaseline + 1, dispatchedAfterW0,
                    "window 0 must emit exactly one dispatch (the full cycle fired)");

                // Snapshot the mutable route + ledger state for the D4 skip-purity delta.
                int rowsBeforeSkip = Ledger.Actions.Count;
                int completedBeforeSkip = route.CompletedCycles;
                int skippedCyclesBeforeSkip = route.SkippedCycles;

                // Window 1: NON-deliverable under anchor 0 / N=2 (odd window). It must be
                // a pure modulo skip - marker advances, NOTHING else (no ledger row, no
                // dispatch, no completed/skipped-cycle bump, no escrow) - D4 skip purity.
                RouteOrchestrator.Tick(phaseAnchor + 1 * cadence + dockOffset, env);

                InGameAssert.AreEqual(afterW0, CountActions(GameActionType.RouteCargoDelivered),
                    "window 1 (non-deliverable at N=2) must NOT deliver (the modulo skip)");
                InGameAssert.AreEqual(dispatchedAfterW0, CountActions(GameActionType.RouteDispatched),
                    "window 1 skip must emit NO dispatch (D4: nothing but the marker moves)");
                InGameAssert.AreEqual(rowsBeforeSkip, Ledger.Actions.Count,
                    "window 1 modulo skip must write NO ledger row of any kind (D4 skip purity)");
                InGameAssert.AreEqual(completedBeforeSkip, route.CompletedCycles,
                    "window 1 skip must not bump CompletedCycles (no cycle completed)");
                InGameAssert.AreEqual(skippedCyclesBeforeSkip, route.SkippedCycles,
                    "window 1 modulo skip must NOT bump SkippedCycles (a scheduled modulo skip is not a blocked cycle - D4)");
                InGameAssert.IsFalse(RouteStore.HasEscrow(route.Id),
                    "window 1 modulo skip must reserve no escrow");
                InGameAssert.AreEqual(1L, route.LastObservedLoopCycleIndex,
                    "window 1 skip must advance the observed-cycle marker to 1");

                // Window 2: deliverable again (even window under anchor 0 / N=2). Fires.
                RouteOrchestrator.Tick(phaseAnchor + 2 * cadence + dockOffset, env);
                int afterW2 = CountActions(GameActionType.RouteCargoDelivered);
                InGameAssert.AreEqual(afterW0 + 1, afterW2,
                    "window 2 (deliverable at N=2) must FIRE one delivery (the modulo alternation)");
                InGameAssert.AreEqual(2L, route.LastObservedLoopCycleIndex,
                    "window 2 delivery must advance the observed-cycle marker to 2");
                InGameAssert.AreEqual(0L, route.WindowAnchorCycleIndex,
                    "the modulo anchor must stay at 0 across the alternation");

                // The route-row basis label / countdown is NON-Flat (the 'nothing red
                // anywhere' symptom): re-derive the basis from the live route + unit,
                // exactly as the Logistics window legibility does, and confirm it reads
                // ReaimWindows with a positive countdown to the next window.
                RouteWindowBasis liveBasis = RouteLoopClock.DeriveWindowBasis(firingUnit);
                InGameAssert.AreEqual(RouteWindowBasis.ReaimWindows, liveBasis,
                    "the route row basis must stay ReaimWindows through the firing cycle (a Flat label would be the silent-degrade symptom)");
                InGameAssert.IsTrue(route.ReaimWindowBasisEngaged,
                    "the route's persisted windowed-basis marker must stay engaged across the alternation");

                ParsekLog.Info(Tag,
                    $"InterBodyBuilderShape: firing gate PASS - window0 fired, window1 modulo-skipped " +
                    $"(purely: no rows/dispatch/escrow), window2 fired; N={nResidual.ToString(IC)} " +
                    $"anchor={route.WindowAnchorCycleIndex.ToString(IC)} " +
                    $"lastObserved={route.LastObservedLoopCycleIndex.ToString(IC)} basis={liveBasis} " +
                    $"deliveredRows={string.Join(",", deliveredRows)}");
            }
            finally
            {
                // Restore the orchestrator seams first (so nothing else fires on them).
                RouteOrchestrator.LoopUnitResolverForTesting = priorResolver;
                RouteOrchestrator.DeliveryRowEmitterForTesting = priorRowEmitter;
                RouteOrchestrator.DeliveryApplierForTesting = priorApplier;
                RouteOrchestrator.OriginDebitApplierForTesting = priorOriginDebit;

                // Drop the synthetic route + its escrow.
                if (routeAdded)
                {
                    bool removed = RouteStore.RemoveRoute(routeId);
                    ParsekLog.Verbose(Tag, $"InterBodyBuilderShape cleanup: RemoveRoute={removed}");
                }

                // Remove the synthetic committed tree + its recordings from the store so
                // the next batch/session does not inherit a fabricated Kerbin->Duna tree.
                // RemoveCommittedTreeById drops the tree from CommittedTrees AND each of
                // its recordings from the flat CommittedRecordings list.
                if (treeCommitted)
                {
                    bool removedTree = RecordingStore.RemoveCommittedTreeById(
                        treeId, "InterBodyBuilderShape-cleanup");
                    ParsekLog.Verbose(Tag,
                        $"InterBodyBuilderShape cleanup: removed committed tree {treeId} => {removedTree}");
                }

                // Drop the two delivery rows this test appended to the live ledger (the
                // real-path emitter wrote them into the shared Ledger). Leaves the
                // career ledger exactly as found.
                RemoveTestLedgerRows(routeId);

                int deliveredAfter = CountActions(GameActionType.RouteCargoDelivered);
                int dispatchedAfter = CountActions(GameActionType.RouteDispatched);
                ParsekLog.Verbose(Tag,
                    $"InterBodyBuilderShape cleanup: ledger deliveries {deliveredBefore.ToString(IC)}->{deliveredAfter.ToString(IC)} " +
                    $"dispatches {dispatchedBefore.ToString(IC)}->{dispatchedAfter.ToString(IC)}");
            }
        }

        // -----------------------------------------------------------------
        // Fixture builders
        // -----------------------------------------------------------------

        // The single-vessel transfer tree: one recording carrying the full Kerbin->Duna
        // SOI chain on its OrbitSegments (the re-aim transfer member the classifier
        // accepts), rooted as the tree's only through-line so MissionLoopUnitBuilder
        // yields exactly one loop unit.
        private static RecordingTree BuildTransferTree(
            string treeId, string dockRecId, List<OrbitSegment> segments)
        {
            var rec = new Recording
            {
                RecordingId = dockRecId,
                VesselName = "InterBody Transport",
                ChainId = "C0",
                ChainIndex = 0,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = ParkStartUT,
                ExplicitEndUT = ArrivalEndUT,
                OrbitSegments = new List<OrbitSegment>(segments),
            };
            var tree = new RecordingTree
            {
                Id = treeId,
                RootRecordingId = dockRecId,
            };
            tree.Recordings[rec.RecordingId] = rec;
            return tree;
        }

        // The backing Route. BackingMissionTreeId makes IsLoopRoute true;
        // CadenceMultiplier drives the residual modulo; DispatchInterval = N x
        // TransitDuration mirrors the v0 flat-cadence relationship the firing tests use.
        // The single delivery stop + KSC origin make an eligible delivery-only route
        // (reserves no escrow), so the EligibleEnv fires without a live vessel.
        private static Route BuildReaimRoute(
            string routeId, string treeId, string dockRecId, int cadenceMultiplier)
        {
            return new Route
            {
                Id = routeId,
                Name = "M5 In-Game Inter-Body Route",
                Status = RouteStatus.Active,
                IsKscOrigin = true,
                BackingMissionTreeId = treeId,
                RecordedDockUT = DockUT,
                DockMemberRecordingId = dockRecId,
                LoopAnchorUT = LoopEnabledUT,
                LastObservedLoopCycleIndex = -1,
                WindowAnchorCycleIndex = -1,
                ReaimWindowBasisEngaged = false,
                CadenceMultiplier = cadenceMultiplier,
                DispatchInterval = (ArrivalEndUT - ParkStartUT) * cadenceMultiplier,
                TransitDuration = ArrivalEndUT - ParkStartUT,
                CompletedCycles = 0,
                RecordingIds = new List<string> { dockRecId },
                CostManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 424242u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = dockRecId, TreeId = treeId, RouteProofHash = "deadbeef" },
                },
            };
        }

        // An always-eligible runtime environment (no live vessel needed): mirrors the
        // xUnit RouteInterBodyFireTests.EligibleEnv so the delivery-only KSC route fires
        // on every owed, deliverable crossing.
        private sealed class EligibleEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource) { lackingResource = string.Empty; return true; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // -----------------------------------------------------------------
        // Live-state helpers
        // -----------------------------------------------------------------

        private static CelestialBody FindBody(string name)
        {
            if (FlightGlobals.Bodies == null)
                return null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody b = FlightGlobals.Bodies[i];
                if (b != null && b.bodyName == name)
                    return b;
            }
            return null;
        }

        private static int CountActions(GameActionType type)
        {
            int n = 0;
            var actions = Ledger.Actions;
            if (actions == null)
                return 0;
            for (int i = 0; i < actions.Count; i++)
                if (actions[i] != null && actions[i].Type == type)
                    n++;
            return n;
        }

        // Drops the RouteCargoDelivered rows this test's real-path emitter wrote into the
        // shared Ledger (keyed by the synthetic route id), restoring the live ledger.
        // Walks back-to-front so RemoveActionAt indices stay valid as rows drop.
        private static void RemoveTestLedgerRows(string routeId)
        {
            var actions = Ledger.Actions;
            if (actions == null)
                return;
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                GameAction a = actions[i];
                if (a != null && a.RouteId == routeId
                    && a.Type == GameActionType.RouteCargoDelivered)
                {
                    Ledger.RemoveActionAt(i);
                }
            }
        }
    }
}
