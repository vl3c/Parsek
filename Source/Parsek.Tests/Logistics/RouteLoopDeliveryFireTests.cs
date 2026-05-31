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
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
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
            public bool DestinationHasCapacityResult { get; set; } = true;
            public string DestinationFullResource { get; set; } = "Ore";
            public bool EndpointResolvable { get; set; } = true;

            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            { reason = EndpointResolvable ? string.Empty : "pid-miss"; return EndpointResolvable; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource)
            { lackingResource = OriginHasCargoResult ? string.Empty : OriginLackingResource; return OriginHasCargoResult; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource)
            { fullResource = DestinationHasCapacityResult ? string.Empty : DestinationFullResource; return DestinationHasCapacityResult; }
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
    }
}
