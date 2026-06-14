using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the loop-route delivery fire (plan Phase 4 tasks 2-5; must-fix #1).
    /// Drives the full <see cref="RouteOrchestrator.Tick(double, IRouteRuntimeEnvironment)"/>
    /// loop-route path with two test seams: <see cref="RouteOrchestrator.LoopUnitResolverForTesting"/>
    /// (returns a directly-built <see cref="GhostPlaybackLogic.LoopUnit"/>) and
    /// <see cref="RouteOrchestrator.DeliveryApplierForTesting"/> (emits the
    /// <c>RouteCargoDelivered</c> row + bumps CompletedCycles without a live
    /// Vessel). The dispatch-debit half (<see cref="RouteOrchestrator.EmitDispatchDebit"/>)
    /// runs for real, so the three-row fire + funds debit + ELS idempotency are
    /// all exercised in xUnit.
    /// </summary>
    [Collection("Sequential")]
    public class RouteLoopDeliveryFireTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();

        public RouteLoopDeliveryFireTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Seam helpers
        // ==================================================================

        // span [1000, 1300] (300s); cadence == span -> one crossing == one cycle.
        // dock UT 1150 inside the span. The resolver returns a unit whose clock,
        // at the given tick UT, reports the desired cycle / tail state.
        private static GhostPlaybackLogic.LoopUnit BuildUnit(
            double spanStartUT = 1000.0, double spanEndUT = 1300.0,
            double cadenceSeconds = 300.0, double phaseAnchorUT = 1000.0)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: spanStartUT, spanEndUT: spanEndUT,
                cadenceSeconds: cadenceSeconds, phaseAnchorUT: phaseAnchorUT);
        }

        private void InstallUnitResolver(GhostPlaybackLogic.LoopUnit unit)
        {
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
        }

        // A fake delivery half that mirrors ApplyDelivery's observable contract
        // (emit RouteCargoDelivered + bump CompletedCycles + clear pending) WITHOUT
        // a live Vessel. The dispatch-debit half runs for real, so the three-row
        // fire is genuine.
        private void InstallFakeDeliveryApplier(double fundsCostIfCareerKsc = 0.0)
        {
            RouteOrchestrator.DeliveryApplierForTesting = (route, currentUT, env) =>
            {
                string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);
                bool isCareerKsc = env.IsCareer && route.IsKscOrigin;
                Ledger.AddAction(new GameAction
                {
                    Type = GameActionType.RouteCargoDelivered,
                    UT = currentUT,
                    RouteId = route.Id,
                    RouteCycleId = cycleId,
                    RouteStopIndex = 0,
                    Sequence = 0,
                    RouteKscFundsCost = isCareerKsc ? (float)fundsCostIfCareerKsc : 0f,
                });
                route.CompletedCycles += 1;
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                route.TransitionTo(RouteStatus.Active, "delivered-loop-fake");
            };
        }

        private static Route BuildLoopRoute(
            string id = "route-loop",
            RouteStatus status = RouteStatus.Active,
            bool isKscOrigin = true,
            double recordedDockUT = 1150.0,
            long lastObservedLoopCycleIndex = -1,
            double dispatchInterval = 300.0)
        {
            return new Route
            {
                Id = id,
                Status = status,
                IsKscOrigin = isKscOrigin,
                BackingMissionTreeId = "tree-1", // makes IsLoopRoute true
                RecordedDockUT = recordedDockUT,
                DockMemberRecordingId = "rec-dock",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = lastObservedLoopCycleIndex,
                DispatchInterval = dispatchInterval,
                TransitDuration = 300.0,
                CostManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                    { "Oxidizer", 120.0 },
                },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double>
                        {
                            { "LiquidFuel", 100.0 },
                            { "Oxidizer", 120.0 },
                        },
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
        }

        // Eligible fake env (all gates pass; no live Vessel needed because the
        // delivery half is the injected fake).
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

        // Env that fails a chosen eligibility gate.
        private sealed class BlockedEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public bool OriginHasCargoResult { get; set; } = true;
            public string OriginLackingResource { get; set; } = "LiquidFuel";
            public bool KscFundsAvailableResult { get; set; } = true;
            public double KscFundsShortfall { get; set; }
            public bool DestinationHasCapacityResult { get; set; } = true;
            public string DestinationFullResource { get; set; } = "Ore";
            public bool EndpointResolvable { get; set; } = true;

            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            { reason = EndpointResolvable ? string.Empty : "pid-miss"; return EndpointResolvable; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource)
            { lackingResource = OriginHasCargoResult ? string.Empty : OriginLackingResource; return OriginHasCargoResult; }
            public bool KscFundsAvailable(Route route, out double shortfall)
            { shortfall = KscFundsAvailableResult ? 0.0 : KscFundsShortfall; return KscFundsAvailableResult; }
            public bool DestinationHasCapacity(Route route, out string fullResource)
            { fullResource = DestinationHasCapacityResult ? string.Empty : DestinationFullResource; return DestinationHasCapacityResult; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // Eligible at crossing time but the DESTINATION fails to resolve at
        // DELIVERY time (M6 hold reasons, plan-review BLOCKER 1 pin):
        // TryResolveEndpoint passes (eligibility gate), TryResolveEndpointVessel
        // fails (ApplyDelivery STEP 2 re-resolution).
        private sealed class EligibleButDeliveryLostEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            { vessel = null; reason = "no-live-vessels"; return false; }
            public bool OriginHasCargo(Route route, out string lackingResource) { lackingResource = string.Empty; return true; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // ==================================================================
        // must-fix #1: the FULL three-row cycle under ONE cycleId
        // ==================================================================

        // catches: must-fix #1 regression — a crossing emitting delivery ALONE
        // (no dispatch/debit) would never debit origin / charge funds and would
        // trip RouteModule's out-of-order guard. The full cycle MUST emit all
        // three row types under one cycleId.
        [Fact]
        public void Crossing_EmitsAllThreeRows_UnderOneCycleId()
        {
            var route = BuildLoopRoute(lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit()); // span [1000,1300], cadence 300
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            // ut 1150 -> cycle 0, mid-span, dock 1150 in span -> crossing.
            RouteOrchestrator.Tick(1150.0, env);

            var dispatched = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteDispatched);
            var debited = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoDebited);
            var delivered = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoDelivered);

            Assert.NotNull(dispatched);
            Assert.NotNull(debited);
            Assert.NotNull(delivered);

            // ALL THREE under the same cycleId (cycle-0).
            Assert.Equal("cycle-0", dispatched.RouteCycleId);
            Assert.Equal("cycle-0", debited.RouteCycleId);
            Assert.Equal("cycle-0", delivered.RouteCycleId);

            // Dispatch emitted BEFORE delivery (RouteModule walks in order; the
            // out-of-order guard requires DispatchedCycles > 0 at delivery time).
            int dispatchedIdx = Ledger.Actions.ToList().FindIndex(a => a.Type == GameActionType.RouteDispatched);
            int deliveredIdx = Ledger.Actions.ToList().FindIndex(a => a.Type == GameActionType.RouteCargoDelivered);
            Assert.True(dispatchedIdx < deliveredIdx);

            // The dispatch-debit half ran (cost manifest cloned onto the debit row).
            Assert.NotNull(debited.RouteResourceManifest);
            Assert.Equal(2, debited.RouteResourceManifest.Count);

            // Cycle index snapped forward; CompletedCycles bumped by the delivery.
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
            Assert.Equal(1, route.CompletedCycles);
        }

        // catches (M2, plan D7): the live env's OriginHasCargo gating a
        // harvest-origin route against a non-existent origin vessel. The
        // harvest branch runs BEFORE any KSP-static touch (route flag check +
        // log only), so it is directly assertable here: early-true with the
        // rate-limited breadcrumb.
        [Fact]
        public void OriginHasCargo_HarvestOrigin_EarlyTrueWithBreadcrumb()
        {
            ParsekLog.VerboseOverrideForTesting = true;
            var route = BuildLoopRoute(isKscOrigin: false);
            route.IsHarvestOrigin = true;
            route.CostManifest = new Dictionary<string, double>();
            route.InventoryCostManifest = null;
            var env = new LiveRouteRuntimeEnvironment();

            bool hasCargo = env.OriginHasCargo(route, out string lacking);

            Assert.True(hasCargo);
            Assert.Equal(string.Empty, lacking);
            Assert.Contains(logLines, l =>
                l.Contains("OriginHasCargo") &&
                l.Contains("harvest origin - no physical source to gate"));
        }

        // catches (M2, plan D7 / risk 8): a harvest-origin crossing either
        // attempting a physical origin debit (there is no origin vessel -
        // the seam must never be consulted) or dropping the
        // RouteDispatched/RouteCargoDebited row pair (row-shape stability:
        // the pair still emits as a structural no-op - empty manifest, zero
        // funds, pid 0).
        [Fact]
        public void Crossing_HarvestOrigin_NoPhysicalDebit_RowsStillEmitted()
        {
            ParsekLog.VerboseOverrideForTesting = true;
            var route = BuildLoopRoute(isKscOrigin: false);
            route.IsHarvestOrigin = true;
            route.CostManifest = new Dictionary<string, double>(); // empty by construction (D7)
            route.Origin = new RouteEndpoint
            {
                VesselPersistentId = 0,
                BodyName = "Minmus",
                IsSurface = true
            };
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit()); // span [1000,1300], cadence 300
            InstallFakeDeliveryApplier();
            bool originDebitSeamCalled = false;
            RouteOrchestrator.OriginDebitApplierForTesting = (r, ut, e) =>
            {
                originDebitSeamCalled = true;
                return default(RouteOrchestrator.OriginDebitOutcome);
            };

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            // The full three-row cycle still emitted under one cycleId.
            var dispatched = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteDispatched);
            var debited = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoDebited);
            var delivered = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoDelivered);
            Assert.NotNull(dispatched);
            Assert.NotNull(debited);
            Assert.NotNull(delivered);
            Assert.Equal("cycle-0", dispatched.RouteCycleId);
            Assert.Equal("cycle-0", debited.RouteCycleId);
            Assert.Equal("cycle-0", delivered.RouteCycleId);

            // Structural no-op debit: empty manifest, zero funds, pid 0, no
            // requested-on-shortfall - and NO physical write attempted.
            Assert.False(originDebitSeamCalled,
                "harvest origin must never attempt a physical origin debit");
            Assert.Equal(0, debited.RouteResourceManifest?.Count ?? 0);
            Assert.Null(debited.RouteRequestedResourceManifest);
            Assert.Equal(0u, debited.RouteOriginVesselPid);
            Assert.Equal(0f, debited.RouteKscFundsCost);

            Assert.Contains(logLines, l =>
                l.Contains("harvest origin: physical origin debit skipped"));
        }

        // catches: a loop-route reaching the legacy InTransit state machine (it
        // must NEVER set InTransit / PendingDeliveryUT / CurrentCycleStartUT).
        [Fact]
        public void Crossing_NeverEntersInTransitState()
        {
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            // Status stays Active (ghost-driving); no InTransit hand-off state.
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Null(route.PendingDeliveryUT);
            Assert.Null(route.CurrentCycleStartUT);
        }

        // catches: Career KSC funds debit not flowing through the delivery row
        // on a loop-route cycle.
        [Fact]
        public void Crossing_CareerKsc_DebitedRowCarriesFundsCost()
        {
            var route = BuildLoopRoute(isKscOrigin: true);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier(fundsCostIfCareerKsc: 555.0);
            var env = new EligibleEnv { IsCareer = true };

            RouteOrchestrator.Tick(1150.0, env);

            var delivered = Ledger.Actions.First(a => a.Type == GameActionType.RouteCargoDelivered);
            Assert.Equal(555f, delivered.RouteKscFundsCost);
        }

        // ==================================================================
        // Fire-once + snap discipline
        // ==================================================================

        // catches: re-firing the SAME cycle on a second tick within the same
        // cycle index (would double-charge). DEL-2: the FIRST fire is at the dock
        // phase (ut 1150 == dock), NOT at span start.
        [Fact]
        public void SameCycle_TwoTicks_FiresOnce()
        {
            var route = BuildLoopRoute(lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env); // cycle 0 dock crossing -> fires
            int afterFirst = Ledger.Actions.Count;
            Assert.Equal(3, afterFirst); // dispatched + debited + delivered

            RouteOrchestrator.Tick(1200.0, env); // still cycle 0, dock already crossed -> no new fire
            Assert.Equal(afterFirst, Ledger.Actions.Count);
            Assert.Equal(1, route.CompletedCycles);
        }

        // catches DEL-2: the delivery firing at SPAN START (ghost just launching,
        // loopUT < dock) instead of waiting for the dock phase. A pre-dock tick
        // must emit NOTHING; the next tick once loopUT >= dock fires the cycle.
        [Fact]
        public void PreDockTick_DoesNotFire_ThenFiresAtDockPhase()
        {
            var route = BuildLoopRoute(lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit()); // span [1000,1300], dock 1150
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            // ut 1050 -> cycle 0, loopUT 1050 < dock 1150 -> NOT yet at the dock phase.
            RouteOrchestrator.Tick(1050.0, env);
            Assert.Empty(Ledger.Actions);
            Assert.Equal(0, route.CompletedCycles);
            Assert.Equal(-1, route.LastObservedLoopCycleIndex); // not consumed
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("no dock crossing"));

            // ut 1200 -> cycle 0, loopUT 1200 >= dock 1150 -> NOW fires.
            RouteOrchestrator.Tick(1200.0, env);
            Assert.Equal(3, Ledger.Actions.Count);
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("DOCK CROSSING confirmed"));
        }

        // catches: a multi-cycle warp jump replaying each skipped cycle instead
        // of firing once and snapping forward. DEL-2: the tick lands in cycle 5
        // BEFORE that cycle's dock (loopUT 1100 < dock 1150), so the highest cycle
        // whose dock instant HAS passed is cycle 4. The route fires once for cycle
        // 4 and snaps to 4 (cycle 5's delivery fires once its own dock is reached).
        [Fact]
        public void WarpJump_FiresOnce_SnapsForward()
        {
            // lastObserved = 0; next tick lands in cycle 5 (fast warp). The clock
            // built from anchor 1000 cadence 300 at ut = 1000 + 5*300 + 100 = 2600
            // is cycle 5 with loopUT 1100 (phase 100, still before dock 1150).
            var route = BuildLoopRoute(lastObservedLoopCycleIndex: 0);
            route.CompletedCycles = 1; // one already done
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(2600.0, env);

            // Fired exactly ONCE (3 rows), snapped to the highest passed-dock cycle
            // (4), NOT the raw span-clock cycle 5 whose dock is still ahead.
            Assert.Equal(3, Ledger.Actions.Count);
            Assert.Equal(4, route.LastObservedLoopCycleIndex);
            Assert.Equal(2, route.CompletedCycles); // 1 + this one
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("warp jump"));
        }

        // catches DEL-2 warp: a single warp frame that jumps PAST the recorded
        // dock of the landing cycle fires once for THAT cycle (loopUT >= dock).
        [Fact]
        public void WarpJump_PastDock_FiresLandingCycleOnce()
        {
            // lastObserved 0; tick at ut = 1000 + 5*300 + 200 = 2700 -> cycle 5,
            // phase 200, loopUT 1200 (>= dock 1150) -> dock of cycle 5 has passed.
            var route = BuildLoopRoute(lastObservedLoopCycleIndex: 0);
            route.CompletedCycles = 1;
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(2700.0, env);

            Assert.Equal(3, Ledger.Actions.Count); // fired ONCE
            Assert.Equal(5, route.LastObservedLoopCycleIndex); // snapped to the landing cycle
            Assert.Equal(2, route.CompletedCycles);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("warp jump"));
        }

        // catches DEL-2: a cold start parked in the cadence>span tail whose cycle
        // was NEVER delivered must still fire that cycle's delivery once (its dock
        // was passed), then NOT re-fire while still parked. The pre-DEL-2 behavior
        // (parked tail = never fire) silently dropped an owed delivery.
        [Fact]
        public void ParkedTail_ColdStart_FiresOnce_ThenIdempotent()
        {
            var route = BuildLoopRoute(lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            // cadence 600 > span 300 -> at ut 1450 the clock is parked at spanEnd
            // (loopUT 1300 >= dock 1150, so cycle 0's dock has been passed).
            InstallUnitResolver(BuildUnit(cadenceSeconds: 600.0));
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1450.0, env);

            // Dock of cycle 0 already passed -> fire once.
            Assert.Equal(3, Ledger.Actions.Count);
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
            Assert.Equal(1, route.CompletedCycles);

            // Still parked in cycle 0's tail -> no re-fire.
            RouteOrchestrator.Tick(1500.0, env);
            Assert.Equal(3, Ledger.Actions.Count);
            Assert.Equal(1, route.CompletedCycles);
        }

        // ==================================================================
        // Status gate
        // ==================================================================

        // catches: a non-ghost-driving (Paused) loop-route firing a delivery.
        [Fact]
        public void NonGhostDrivingStatus_DoesNotFire()
        {
            var route = BuildLoopRoute(status: RouteStatus.Paused);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            Assert.Empty(Ledger.Actions);
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.Equal(0, route.CompletedCycles);
        }

        // ==================================================================
        // must-fix #3: eligibility WITHOUT EvaluateRoute
        // ==================================================================

        // catches: a blocked cycle (eligibility fails) STILL emitting a debit /
        // delivery. The blocked cycle must emit NOTHING, bump SkippedCycles, and
        // STILL snap the cycle index forward so it does not re-fire every tick.
        [Fact]
        public void FailedEligibility_EmitsNothing_BumpsSkipped_SnapsForward()
        {
            var route = BuildLoopRoute(isKscOrigin: false, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            // Non-KSC + origin lacks cargo -> blocked.
            var env = new BlockedEnv { OriginHasCargoResult = false, OriginLackingResource = "LiquidFuel" };

            RouteOrchestrator.Tick(1150.0, env);

            // NOTHING emitted.
            Assert.Empty(Ledger.Actions);
            // SkippedCycles bumped, CompletedCycles untouched.
            Assert.Equal(1, route.SkippedCycles);
            Assert.Equal(0, route.CompletedCycles);
            // Snapped forward so the blocked cycle does not re-fire every tick.
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("BLOCKED") && l.Contains("LiquidFuel"));
        }

        // catches: a blocked cycle re-firing on the next same-cycle tick (the
        // snap must hold).
        [Fact]
        public void FailedEligibility_DoesNotReFireSameCycle()
        {
            var route = BuildLoopRoute(isKscOrigin: false, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var env = new BlockedEnv { OriginHasCargoResult = false };

            RouteOrchestrator.Tick(1150.0, env); // cycle 0 dock crossing, blocked -> snap
            RouteOrchestrator.Tick(1200.0, env); // still cycle 0, dock already crossed -> no re-fire

            Assert.Empty(Ledger.Actions);
            Assert.Equal(1, route.SkippedCycles); // bumped exactly once
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
        }

        // catches: the blocked-then-eligible sequence not advancing the cycleId
        // (a skip must advance the sequence so the next FIRE uses a fresh id).
        [Fact]
        public void SkipThenFire_YieldsUniqueCycleIds_NoDoubleCharge()
        {
            var route = BuildLoopRoute(isKscOrigin: false, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallFakeDeliveryApplier();

            // Cycle 0: blocked (skip). DEL-2: the crossing is detected at the dock
            // phase (ut 1150 == dock), not at span start.
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => BuildUnit();
            var blockedEnv = new BlockedEnv { OriginHasCargoResult = false };
            RouteOrchestrator.Tick(1150.0, blockedEnv); // cycle 0 dock crossing, blocked -> SkippedCycles=1, snap to 0
            Assert.Equal(1, route.SkippedCycles);
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
            Assert.Empty(Ledger.Actions);

            // Cycle 1: eligible (fire). cycleId = cycle-{0 completed + 1 skipped} = cycle-1.
            var eligibleEnv = new EligibleEnv();
            // ut 1450 -> cycle 1 (one full 300s period + 150), dock 1150 in span.
            RouteOrchestrator.Tick(1450.0, eligibleEnv);

            var dispatched = Ledger.Actions.First(a => a.Type == GameActionType.RouteDispatched);
            var delivered = Ledger.Actions.First(a => a.Type == GameActionType.RouteCargoDelivered);
            // The fired cycle uses cycle-1 (NOT cycle-0 — the skip advanced the id).
            Assert.Equal("cycle-1", dispatched.RouteCycleId);
            Assert.Equal("cycle-1", delivered.RouteCycleId);
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(1, route.SkippedCycles);
            Assert.Equal(1, route.LastObservedLoopCycleIndex);

            // Exactly one dispatched + one delivered (no double-charge).
            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.RouteDispatched));
            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.RouteCargoDelivered));
        }

        // ==================================================================
        // M6 hold reasons: capture on the blocked branch, clear on an
        // eligible crossing, survive the endpoint-lost-at-delivery case
        // ==================================================================

        // catches (M6): a blocked crossing not persisting the eligibility
        // verdict onto the route's LastHold* fields.
        [Fact]
        public void BlockedCrossing_RecordsHoldReason()
        {
            var route = BuildLoopRoute(isKscOrigin: false, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var env = new BlockedEnv { OriginHasCargoResult = false, OriginLackingResource = "LiquidFuel" };

            RouteOrchestrator.Tick(1150.0, env);

            Assert.Equal(1, route.SkippedCycles); // the blocked branch actually ran
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                route.LastHoldKind);
            Assert.Equal("LiquidFuel", route.LastHoldDetail); // raw token, verbatim
            Assert.Equal(0.0, route.LastHoldShortfall);
            Assert.Equal(1150.0, route.LastHoldUT); // the tick UT, for the age suffix
        }

        // catches (M6 + plan-review finding 3): the funds gate silently skipping
        // because IsCareer was false (vacuous pass), or the shortfall not landing
        // on the hold. The gate only runs when env.IsCareer && route.IsKscOrigin.
        [Fact]
        public void BlockedCrossing_FundsShort_RecordsShortfall()
        {
            var route = BuildLoopRoute(isKscOrigin: true, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var env = new BlockedEnv
            {
                IsCareer = true,
                KscFundsAvailableResult = false,
                KscFundsShortfall = 750.0,
            };

            RouteOrchestrator.Tick(1150.0, env);

            Assert.Equal(1, route.SkippedCycles); // proves the funds gate blocked, not a pass
            Assert.Empty(Ledger.Actions);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.FundsShort,
                route.LastHoldKind);
            Assert.Equal("funds-short", route.LastHoldDetail);
            Assert.Equal(750.0, route.LastHoldShortfall);
            Assert.Equal(1150.0, route.LastHoldUT);
        }

        // catches (M6): a FIRED crossing leaving a stale hold behind (the
        // eligible-crossing clear must wipe a prior blocked cycle's reason).
        [Fact]
        public void FiredCrossing_ClearsHold()
        {
            var route = BuildLoopRoute(lastObservedLoopCycleIndex: -1);
            route.RecordHold(
                RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                "LiquidFuel", 0.0, 500.0);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            Assert.Equal(3, Ledger.Actions.Count); // genuinely fired
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None, route.LastHoldKind);
            Assert.Null(route.LastHoldDetail);
            Assert.Equal(0.0, route.LastHoldShortfall);
            Assert.Equal(-1.0, route.LastHoldUT);
        }

        // catches (M6): the replay-backstop branch keeping a stale hold. The
        // crossing was ELIGIBLE either way, so the single pre-emit clear must
        // cover the backstop branch too.
        [Fact]
        public void ReplayBackstop_ClearsHold()
        {
            var route = BuildLoopRoute(lastObservedLoopCycleIndex: -1);
            route.RecordHold(
                RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                "LiquidFuel", 0.0, 500.0);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();

            // Pre-seed a delivered row for cycle-0 so the crossing replay-skips.
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = 150.0,
                RouteId = route.Id,
                RouteCycleId = "cycle-0",
                RouteStopIndex = 0,
                Sequence = 0,
            });
            int before = Ledger.Actions.Count;

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            Assert.Equal(before, Ledger.Actions.Count); // backstop emitted nothing
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None, route.LastHoldKind);
            Assert.Null(route.LastHoldDetail);
            Assert.Equal(-1.0, route.LastHoldUT);
        }

        // catches (M6, plan-review BLOCKER 1): a clear placed AFTER EmitLoopCycle
        // would erase the EndpointLost hold that ApplyDelivery records INSIDE the
        // call when the destination fails to resolve at delivery time
        // (EmitLoopCycle returns true even on that branch). The delivery seam is
        // left NULL so the PRODUCTION ApplyDelivery runs.
        [Fact]
        public void EndpointLostAtDelivery_HoldSurvivesEligibleCrossing()
        {
            // KSC origin: the dispatch-debit half never reaches the physical
            // origin debit, so the production path stays xUnit-safe end-to-end.
            var route = BuildLoopRoute(isKscOrigin: true, lastObservedLoopCycleIndex: -1);
            // Pre-seed a stale hold so a misplaced post-emit clear would also
            // visibly wipe the fresh EndpointLost hold below.
            route.RecordHold(
                RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                "LiquidFuel", 0.0, 500.0);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            // DeliveryApplierForTesting intentionally NOT installed.

            RouteOrchestrator.Tick(1150.0, new EligibleButDeliveryLostEnv());

            // The crossing was eligible (dispatch+debit emitted), the delivery
            // half lost the endpoint, and the hold recorded inside EmitLoopCycle
            // SURVIVED the post-crossing bookkeeping.
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.RouteDispatched);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.RouteEndpointLost);
            Assert.Equal(RouteStatus.EndpointLost, route.Status);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost,
                route.LastHoldKind);
            Assert.Equal("endpoint-destroyed-at-delivery:no-live-vessels", route.LastHoldDetail);
            Assert.Equal(1150.0, route.LastHoldUT);
        }

        // ==================================================================
        // ELS idempotency backstop
        // ==================================================================

        // catches: a replayed cycleId (already in the ledger) re-emitting the
        // dispatch/debit/delivery rows (save-reload double-fire). The ELS
        // (routeId, cycleId) backstop must emit NOTHING and advance the cycle id
        // so the route progresses.
        [Fact]
        public void ReplayedCycleId_EmitsNothing_NoDoubleCharge()
        {
            var route = BuildLoopRoute(lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            // Pre-seed a delivered row for cycle-0 (simulates a save written after
            // the cycle fired, then reloaded with LastObservedLoopCycleIndex still
            // at -1 in the in-memory route).
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = 150.0,
                RouteId = route.Id,
                RouteCycleId = "cycle-0",
                RouteStopIndex = 0,
                Sequence = 0,
            });
            int before = Ledger.Actions.Count;

            RouteOrchestrator.Tick(1150.0, env); // would be cycle 0 again

            // No NEW rows (the backstop suppressed the duplicate dispatch+debit+
            // delivery).
            Assert.Equal(before, Ledger.Actions.Count);
            // CompletedCycles bumped so the NEXT cycle id advances past cycle-0.
            Assert.Equal(1, route.CompletedCycles);
            // Snapped forward.
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("already in ledger") && l.Contains("replay"));
        }

        // ==================================================================
        // No resolvable unit
        // ==================================================================

        // catches: a null loop-unit (no committed members this tick) crashing the
        // tick instead of skipping the route.
        [Fact]
        public void NoResolvableUnit_SkipsRoute_NoThrow()
        {
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => null;
            InstallFakeDeliveryApplier();

            var ex = Record.Exception(() => RouteOrchestrator.Tick(1150.0, new EligibleEnv()));
            Assert.Null(ex);
            Assert.Empty(Ledger.Actions);
            Assert.Equal(-1, route.LastObservedLoopCycleIndex);
        }

        // ==================================================================
        // Reset discipline (Phase 4 task 5)
        // ==================================================================

        // catches: a re-activated loop-route NOT resetting its cycle-observation
        // cursor, so the first post-activate crossing would not fire until the
        // clock's cycleIndex climbed past the stale value.
        [Fact]
        public void Activate_LoopRoute_ResetsLastObservedToMinusOne()
        {
            var route = BuildLoopRoute(status: RouteStatus.Paused, lastObservedLoopCycleIndex: 7);

            bool ok = RouteOrchestrator.TryActivate(route, 5000.0);

            Assert.True(ok);
            Assert.Equal(RouteStatus.Active, route.Status);
            // Loop-route: cycle observation restarts so the first crossing fires.
            Assert.Equal(-1, route.LastObservedLoopCycleIndex);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("TryActivate") && l.Contains("loopRoute=1"));
        }

        // catches: a NON-loop route (no backing-mission tree) having its cycle
        // cursor stomped by the loop-route reset.
        [Fact]
        public void Activate_NonLoopRoute_LeavesLastObservedUntouched()
        {
            var route = BuildLoopRoute(status: RouteStatus.Paused, lastObservedLoopCycleIndex: 7);
            route.BackingMissionTreeId = null; // makes IsLoopRoute false

            bool ok = RouteOrchestrator.TryActivate(route, 5000.0);

            Assert.True(ok);
            // Non-loop route: the loop cursor is left alone (the self-timer path
            // never reads it).
            Assert.Equal(7, route.LastObservedLoopCycleIndex);
        }

        // ==================================================================
        // dock UT outside span
        // ==================================================================

        // catches: a dock UT outside the rendered span firing a delivery.
        [Fact]
        public void DockUTOutsideSpan_DoesNotFire()
        {
            var route = BuildLoopRoute(recordedDockUT: 9999.0); // outside [1000,1300]
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            Assert.Empty(Ledger.Actions);
            Assert.Equal(0, route.CompletedCycles);
        }

        // ==================================================================
        // M1 physical origin debit (design D11/D12). The loop path is the
        // ONLY caller that applies the physical debit; the debited row's
        // population (actuals / requested-on-shortfall / origin pid) is
        // driven through the OriginDebitApplierForTesting seam, except the
        // D12 null-vessel rule which runs the PRODUCTION applier.
        // ==================================================================

        // catches: a non-KSC loop crossing emitting the v0 CostManifest clone
        // instead of the physical debit's ACTUAL removed amounts + origin pid.
        [Fact]
        public void Crossing_NonKscOrigin_DebitedRowCarriesActualManifestAndOriginPid()
        {
            var route = BuildLoopRoute(isKscOrigin: false);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            int seamCalls = 0;
            RouteOrchestrator.OriginDebitApplierForTesting = (r, ut, env) =>
            {
                seamCalls++;
                return new RouteOrchestrator.OriginDebitOutcome
                {
                    ActualDebited = new Dictionary<string, double>
                    {
                        { "LiquidFuel", 100.0 },
                        { "Oxidizer", 120.0 },
                    },
                    RequestedOnShortfall = null,
                    OriginVesselPid = 777u,
                    Short = false,
                    Unresolved = false,
                };
            };

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            Assert.Equal(1, seamCalls);
            var debited = Ledger.Actions.First(a => a.Type == GameActionType.RouteCargoDebited);
            Assert.NotNull(debited.RouteResourceManifest);
            Assert.Equal(2, debited.RouteResourceManifest.Count);
            Assert.Equal(100.0, debited.RouteResourceManifest["LiquidFuel"]);
            Assert.Equal(120.0, debited.RouteResourceManifest["Oxidizer"]);
            Assert.Null(debited.RouteRequestedResourceManifest); // full debit
            Assert.Equal(777u, debited.RouteOriginVesselPid);
            // The extended DispatchDebit line carries the attribution fields.
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("DispatchDebit:")
                && l.Contains("originPid=777") && l.Contains("debitedResources=2")
                && l.Contains("short=0"));
        }

        // catches: a non-KSC physical debit leaking a funds cost onto the
        // debited row (funds are KSC-origin-only; the physical manifest IS
        // the non-KSC cost).
        [Fact]
        public void Crossing_NonKscOrigin_NoFundsCostOnDebitedRow()
        {
            var route = BuildLoopRoute(isKscOrigin: false);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            RouteOrchestrator.OriginDebitApplierForTesting = (r, ut, env) =>
                new RouteOrchestrator.OriginDebitOutcome
                {
                    ActualDebited = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                    OriginVesselPid = 777u,
                };

            RouteOrchestrator.Tick(1150.0, new EligibleEnv { IsCareer = true });

            var debited = Ledger.Actions.First(a => a.Type == GameActionType.RouteCargoDebited);
            Assert.Equal(0f, debited.RouteKscFundsCost);
        }

        // catches (risk 1): the M1 row change touching the KSC branch. A KSC
        // loop crossing must keep the v0 row shape BYTE-IDENTICAL: the
        // unconditional CostManifest clone, no requested manifest, pid 0 -
        // and the physical-debit seam must never be consulted.
        [Fact]
        public void Crossing_KscOrigin_DebitedRowByteIdenticalToV0Shape()
        {
            var route = BuildLoopRoute(isKscOrigin: true);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            int seamCalls = 0;
            RouteOrchestrator.OriginDebitApplierForTesting = (r, ut, env) =>
            {
                seamCalls++;
                return new RouteOrchestrator.OriginDebitOutcome { OriginVesselPid = 999u };
            };

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            Assert.Equal(0, seamCalls); // KSC origins never reach the physical debit
            var debited = Ledger.Actions.First(a => a.Type == GameActionType.RouteCargoDebited);
            // v0 shape: exact CostManifest clone (copied, not referenced).
            Assert.NotNull(debited.RouteResourceManifest);
            Assert.NotSame(route.CostManifest, debited.RouteResourceManifest);
            Assert.Equal(route.CostManifest.Count, debited.RouteResourceManifest.Count);
            foreach (var kv in route.CostManifest)
            {
                Assert.True(debited.RouteResourceManifest.TryGetValue(kv.Key, out double v));
                Assert.Equal(kv.Value, v);
            }
            Assert.Null(debited.RouteRequestedResourceManifest);
            Assert.Equal(0u, debited.RouteOriginVesselPid);
        }

        // catches (D3): a short apply not recording the requested manifest on
        // the row, or the clamp landing silently (no Warn). Driven through
        // the seam so the emit-site warn is pinned independently of the
        // production applier.
        [Fact]
        public void OriginDebit_ShortAtApply_RecordsRequestedManifest_Warns()
        {
            var route = BuildLoopRoute(isKscOrigin: false);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            RouteOrchestrator.OriginDebitApplierForTesting = (r, ut, env) =>
                new RouteOrchestrator.OriginDebitOutcome
                {
                    ActualDebited = new Dictionary<string, double> { { "LiquidFuel", 40.0 } },
                    RequestedOnShortfall = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                    OriginVesselPid = 777u,
                    Short = true,
                    Unresolved = false,
                };

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            var debited = Ledger.Actions.First(a => a.Type == GameActionType.RouteCargoDebited);
            Assert.Equal(40.0, debited.RouteResourceManifest["LiquidFuel"]);
            Assert.NotNull(debited.RouteRequestedResourceManifest);
            Assert.Equal(100.0, debited.RouteRequestedResourceManifest["LiquidFuel"]);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Route]")
                && l.Contains("SHORT at apply"));
        }

        // catches (D12 rule pin): an env resolution that returns TRUE with a
        // null vessel being treated as resolved. Runs the PRODUCTION applier
        // (seam left null) against EligibleEnv, whose
        // TryResolveEndpointVessel returns (true, vessel: null) - the xUnit
        // fake-env shape. Must count as UNRESOLVED: Warn + zero actuals +
        // FULL requested manifest + pid 0.
        [Fact]
        public void OriginDebit_ResolvedNullVessel_TreatedAsUnresolved()
        {
            var route = BuildLoopRoute(isKscOrigin: false);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            // OriginDebitApplierForTesting stays null -> production ApplyOriginDebit.

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            var debited = Ledger.Actions.First(a => a.Type == GameActionType.RouteCargoDebited);
            // Zero actuals serialize as no manifest; full requested manifest.
            Assert.Null(debited.RouteResourceManifest);
            Assert.NotNull(debited.RouteRequestedResourceManifest);
            Assert.Equal(route.CostManifest.Count, debited.RouteRequestedResourceManifest.Count);
            Assert.Equal(100.0, debited.RouteRequestedResourceManifest["LiquidFuel"]);
            Assert.Equal(120.0, debited.RouteRequestedResourceManifest["Oxidizer"]);
            Assert.Equal(0u, debited.RouteOriginVesselPid);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Route]")
                && l.Contains("origin unresolved at debit")
                && l.Contains("resolved-null-vessel"));
            // The extended DispatchDebit line flags the unresolved outcome.
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("DispatchDebit:")
                && l.Contains("unresolved=1"));
        }

        // catches: the ELS replay backstop letting the physical debit run
        // (a replayed cycleId must emit NOTHING, including no origin-tank
        // mutation - extends ReplayedCycleId_EmitsNothing_NoDoubleCharge to
        // the origin-debit seam).
        [Fact]
        public void ReplayedCycleId_EmitsNoDebit()
        {
            var route = BuildLoopRoute(isKscOrigin: false, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            int seamCalls = 0;
            RouteOrchestrator.OriginDebitApplierForTesting = (r, ut, env) =>
            {
                seamCalls++;
                return new RouteOrchestrator.OriginDebitOutcome();
            };

            // Pre-seed a delivered row for cycle-0 (save written after the
            // cycle fired, reloaded with the stale in-memory cursor).
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = 150.0,
                RouteId = route.Id,
                RouteCycleId = "cycle-0",
                RouteStopIndex = 0,
                Sequence = 0,
            });
            int before = Ledger.Actions.Count;

            RouteOrchestrator.Tick(1150.0, new EligibleEnv()); // would be cycle 0 again

            Assert.Equal(before, Ledger.Actions.Count); // nothing emitted
            Assert.Equal(0, seamCalls); // physical debit never invoked on replay
        }
    }
}
