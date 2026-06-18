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
    /// Pins the M4a Phase A3 per-window FIRING for a MULTI-STOP DELIVERY loop
    /// route (Horn A): N delivery windows fire each at its OWN recorded dock phase
    /// under ONE cycleId, idempotent across save/reload, with CompletedCycles
    /// bumping exactly once per completed cycle.
    ///
    /// <para><b>Real path, not the false-green fake.</b> The plain
    /// <see cref="RouteOrchestrator.DeliveryApplierForTesting"/> fake short-circuits
    /// BEFORE the per-window idempotency guard
    /// (<see cref="RouteOrchestrator.IsDeliveryAlreadyInLedger"/>) and would let a
    /// "window 2 not suppressed" assertion pass even if the real guard suppressed
    /// it. These tests instead leave that fake null and assign
    /// <see cref="RouteOrchestrator.DeliveryRowEmitterForTesting"/>, which the REAL
    /// <see cref="RouteOrchestrator.ApplyDelivery"/> consults AFTER its STEP-1
    /// guard - so a window-2-suppressed or stopIndex-collision regression goes RED.
    /// The dispatch-debit half (EmitDispatchDebit) + both replay guards run for
    /// real.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteMultiStopFireTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();

        public RouteMultiStopFireTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Seam helpers
        // ==================================================================

        // span [1000, 1400] (400s), cadence == span -> one crossing window per
        // cycle. Dock A (stop 0) at 1150, Dock B (stop 1) at 1300 inside the span.
        private static GhostPlaybackLogic.LoopUnit BuildUnit(
            double spanStartUT = 1000.0, double spanEndUT = 1400.0,
            double cadenceSeconds = 400.0, double phaseAnchorUT = 1000.0)
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

        // The REAL ApplyDelivery path runs (DeliveryApplierForTesting null), and the
        // row-emitter seam emits a genuine RouteCargoDelivered row AFTER the real
        // per-window guard. The fake does NOT bump CompletedCycles (the caller owns
        // it). Records every (cycleId, stopIndex) it emitted for assertions.
        private void InstallRealPathRowEmitter()
        {
            RouteOrchestrator.DeliveryRowEmitterForTesting =
                (route, currentUT, env, cycleId, stopIndex, bumpCompletedCycle) =>
                {
                    Ledger.AddAction(new GameAction
                    {
                        Type = GameActionType.RouteCargoDelivered,
                        UT = currentUT,
                        RouteId = route.Id,
                        RouteCycleId = cycleId,
                        RouteStopIndex = stopIndex,
                        // Mirror the production delivery Sequence stride (stop*8 + 3).
                        Sequence = stopIndex * RouteOrchestrator.SeqStride + 3,
                    });
                };
        }

        // A 2-stop DELIVERY route. Stop 0 docks at 1150 (LF), stop 1 docks at 1300
        // (Ox). KSC origin (no physical origin debit needed for the headless path).
        private static Route Build2StopRoute(
            string id = "route-multi",
            RouteStatus status = RouteStatus.Active,
            bool isKscOrigin = true,
            long lastObservedLoopCycleIndex = -1,
            long stop0LastFired = -1,
            long stop1LastFired = -1)
        {
            return new Route
            {
                Id = id,
                Status = status,
                IsKscOrigin = isKscOrigin,
                BackingMissionTreeId = "tree-1", // IsLoopRoute true
                // Route-level span pair keys on the run-end (last) dock (A2 fold).
                RecordedDockUT = 1300.0,
                DockMemberRecordingId = "rec-dock-b",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = lastObservedLoopCycleIndex,
                DispatchInterval = 400.0,
                TransitDuration = 400.0,
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
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                        SegmentIndexBefore = 0,
                        RecordedDockUT = 1150.0,
                        LastFiredCycleIndex = stop0LastFired,
                    },
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 43u },
                        DeliveryManifest = new Dictionary<string, double> { { "Oxidizer", 120.0 } },
                        SegmentIndexBefore = 1,
                        RecordedDockUT = 1300.0,
                        LastFiredCycleIndex = stop1LastFired,
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock-b", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
        }

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

        private sealed class BlockedEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource) { lackingResource = "LiquidFuel"; return false; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        private static List<GameAction> Delivered() =>
            Ledger.Actions.Where(a => a.Type == GameActionType.RouteCargoDelivered).ToList();

        private static List<GameAction> Dispatched() =>
            Ledger.Actions.Where(a => a.Type == GameActionType.RouteDispatched).ToList();

        // ==================================================================
        // (1) Both windows fire at their phases; window 2 NOT suppressed
        // ==================================================================

        // catches the RANK-1 hole: window 0's RouteCargoDelivered row suppressing
        // window 1's delivery forever (the single-key replay guard). Drives the
        // ghost from dock A's phase to dock B's phase across two ticks; both
        // deliveries must emit, each carrying its OWN stopIndex, under ONE cycleId.
        [Fact]
        public void TwoWindowCycle_BothDeliveriesFire_Window2NotSuppressed()
        {
            var route = Build2StopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit()); // span [1000,1400], docks 1150 / 1300
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            // Tick at loopUT 1150 (== dock A): window 0 due, window 1's dock (1300)
            // not yet reached. Only window 0 fires; cycle NOT complete.
            RouteOrchestrator.Tick(1150.0, env);

            var afterA = Delivered();
            Assert.Single(afterA);
            Assert.Equal(0, afterA[0].RouteStopIndex);
            Assert.Equal("cycle-0", afterA[0].RouteCycleId);
            // Dispatch fired once (cycle open) under the same cycleId.
            Assert.Single(Dispatched());
            Assert.Equal("cycle-0", Dispatched()[0].RouteCycleId);
            // Cycle NOT complete (window B's dock not reached): no CompletedCycles
            // bump, route marker NOT snapped.
            Assert.Equal(0, route.CompletedCycles);
            Assert.Equal(-1, route.LastObservedLoopCycleIndex);
            Assert.Equal(0, route.Stops[0].LastFiredCycleIndex); // stop 0 fired this cycle
            Assert.Equal(-1, route.Stops[1].LastFiredCycleIndex); // stop 1 not yet

            // Tick at loopUT 1350 (>= dock B 1300): window 1 due, window 0 already
            // fired. Window 1's delivery MUST emit (NOT suppressed by window 0's row).
            RouteOrchestrator.Tick(1350.0, env);

            var afterB = Delivered();
            Assert.Equal(2, afterB.Count);
            Assert.Contains(afterB, d => d.RouteStopIndex == 0 && d.RouteCycleId == "cycle-0");
            Assert.Contains(afterB, d => d.RouteStopIndex == 1 && d.RouteCycleId == "cycle-0");
            // Still exactly ONE dispatch for the cycle (no re-dispatch on tick 2).
            Assert.Single(Dispatched());
            // Cycle COMPLETE after the last window: CompletedCycles bumped ONCE.
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
            Assert.Equal(0, route.Stops[1].LastFiredCycleIndex);
        }

        // ==================================================================
        // (2) Idempotent across save/reload — partial-cycle resume (OQ3)
        // ==================================================================

        // catches: a reload mid-cycle re-firing window 0 (already delivered) or
        // failing to fire window 1. Simulate the persisted state after window 0
        // fired (its delivered row in the ledger + stop0.LastFiredCycleIndex=0 +
        // the dispatch row), then re-present the cycle past dock B. Window 0 must
        // SKIP (per-stop fire index + the per-window ELS guard) and window 1 fires.
        [Fact]
        public void PartialCycleResume_Window0Skips_Window1Fires()
        {
            // Persisted post-window-0 state: stop0.LastFiredCycleIndex=0, route
            // marker still -1 (deferred snap — cycle not yet complete at save).
            var route = Build2StopRoute(stop0LastFired: 0, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            // Pre-seed the ledger as it would be after window 0 fired + saved:
            // the RouteDispatched row (cycle-0, carrier stop 0) and window 0's
            // RouteCargoDelivered row (cycle-0, stop 0).
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteDispatched,
                UT = 1150.0,
                RouteId = route.Id,
                RouteCycleId = "cycle-0",
                RouteStopIndex = 0,
                Sequence = 0,
            });
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = 1150.0,
                RouteId = route.Id,
                RouteCycleId = "cycle-0",
                RouteStopIndex = 0,
                Sequence = 0 * RouteOrchestrator.SeqStride + 3,
            });
            int before = Ledger.Actions.Count;

            // Re-present the cycle past dock B (loopUT 1350).
            RouteOrchestrator.Tick(1350.0, env);

            // Window 0 did NOT re-fire: still exactly ONE stop-0 delivered row.
            Assert.Single(Delivered().Where(d => d.RouteStopIndex == 0));
            // Window 1 fired (the resume window).
            Assert.Single(Delivered().Where(d => d.RouteStopIndex == 1));
            // Dispatch NOT re-emitted (the carrier-stop-0 guard suppressed it; the
            // carrier index is cycle-stable, so a resume tick with only window 1 due
            // does NOT re-dispatch under stop 1).
            Assert.Single(Dispatched());
            // Cycle completed once on the resume tick.
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
            // Net new rows: exactly the window-1 delivered row (no re-dispatch, no
            // re-debit, no window-0 re-delivery).
            Assert.Equal(before + 1, Ledger.Actions.Count);
        }

        // catches: the per-window delivery guard not distinguishing stopIndex. The
        // REAL IsDeliveryAlreadyInLedger is asserted directly: window 0's row must
        // NOT register as "window 1 already delivered".
        [Fact]
        public void DeliveryGuard_IsPerWindow_NotPerCycle()
        {
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = 1150.0,
                RouteId = "route-x",
                RouteCycleId = "cycle-0",
                RouteStopIndex = 0,
            });

            // Window 0 IS in the ledger.
            Assert.True(RouteOrchestrator.IsDeliveryAlreadyInLedger("route-x", "cycle-0", 0));
            // Window 1 is NOT (the RANK-1 hole would return true here).
            Assert.False(RouteOrchestrator.IsDeliveryAlreadyInLedger("route-x", "cycle-0", 1));
        }

        // ==================================================================
        // (3) (UT, Sequence) total order across the windows' rows
        // ==================================================================

        // catches the RANK-3 collision: fixed Sequence 0/1/2/3 colliding across
        // windows. After a full 2-window cycle, the dispatch/debit/delivery rows
        // must have a TOTAL order — no two rows share the same (UT, Sequence) when
        // their UTs coincide, and the per-window stride keeps stop 1's rows distinct
        // from stop 0's.
        [Fact]
        public void TwoWindowCycle_UtSequence_IsTotalOrder()
        {
            var route = Build2StopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env); // window 0 + dispatch
            RouteOrchestrator.Tick(1350.0, env); // window 1 + cycle complete

            // Collect every route row for the cycle and assert (UT, Sequence) is a
            // total order (no duplicate key).
            var rows = Ledger.Actions
                .Where(a => a.RouteId == route.Id && a.RouteCycleId == "cycle-0")
                .Select(a => (a.UT, a.Sequence))
                .ToList();
            Assert.True(rows.Count >= 3); // dispatch + debit + 2 deliveries (at least)
            var distinct = rows.Distinct().ToList();
            Assert.Equal(rows.Count, distinct.Count); // no (UT, Sequence) collision

            // Dispatch (seq 0) + debit (seq 1) carry the carrier stop index 0; the
            // stop-1 delivery carries stride+3 = 11, distinct from stop-0's +3 = 3.
            var dispatched = Dispatched().Single();
            Assert.Equal(0, dispatched.Sequence);
            var del1 = Delivered().Single(d => d.RouteStopIndex == 1);
            Assert.Equal(1 * RouteOrchestrator.SeqStride + 3, del1.Sequence);
            var del0 = Delivered().Single(d => d.RouteStopIndex == 0);
            Assert.Equal(0 * RouteOrchestrator.SeqStride + 3, del0.Sequence);
        }

        // ==================================================================
        // (4) CompletedCycles bumps EXACTLY ONCE per multi-window cycle
        // ==================================================================

        // catches a per-window CompletedCycles bump (which would shift the cycleId
        // the later windows compute and break Horn A). Two full cycles of a 2-window
        // route must leave CompletedCycles == 2 (NOT 4).
        [Fact]
        public void CompletedCycles_BumpsOncePerCycle_NotPerWindow()
        {
            var route = Build2StopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            // Cycle 0: window 0 (1150) then window 1 (1350).
            RouteOrchestrator.Tick(1150.0, env);
            Assert.Equal(0, route.CompletedCycles); // not yet (window 1 pending)
            RouteOrchestrator.Tick(1350.0, env);
            Assert.Equal(1, route.CompletedCycles); // cycle 0 complete

            // Cycle 1: anchor 1000, cadence 400 -> cycle 1 dock A at 1000+400+150=1550,
            // dock B at 1000+400+300=1700.
            RouteOrchestrator.Tick(1550.0, env); // window 0 of cycle 1
            Assert.Equal(1, route.CompletedCycles); // still 1 (window 1 pending)
            RouteOrchestrator.Tick(1700.0, env); // window 1 of cycle 1
            Assert.Equal(2, route.CompletedCycles); // cycle 1 complete

            // Every cycle's deliveries are under a unique cycleId; the cycleId is
            // stable across each cycle's windows.
            Assert.Equal(2, Delivered().Count(d => d.RouteStopIndex == 0));
            Assert.Equal(2, Delivered().Count(d => d.RouteStopIndex == 1));
            Assert.Equal(2, Dispatched().Count);
            Assert.Contains(Dispatched(), d => d.RouteCycleId == "cycle-0");
            Assert.Contains(Dispatched(), d => d.RouteCycleId == "cycle-1");
        }

        // ==================================================================
        // (5) Cadence rebase resets the per-stop fire indices (C1)
        // ==================================================================

        // catches: a cadence-multiplier change leaving stale per-stop
        // LastFiredCycleIndex values that stall (N raised) or jump (N lowered) the
        // later windows. RouteCadence.ApplyMultiplier must reset EVERY stop to -1
        // alongside the route-level cursor.
        [Fact]
        public void CadenceRebase_ResetsPerStopFireIndices()
        {
            var route = Build2StopRoute(
                lastObservedLoopCycleIndex: 5, stop0LastFired: 5, stop1LastFired: 5);

            bool changed = RouteCadence.ApplyMultiplier(route, 3);

            Assert.True(changed);
            // Route-level cursor + BOTH per-stop indices reset to -1 so the next
            // crossing of each window fires exactly once after the rebase.
            Assert.Equal(-1, route.LastObservedLoopCycleIndex);
            Assert.Equal(-1, route.Stops[0].LastFiredCycleIndex);
            Assert.Equal(-1, route.Stops[1].LastFiredCycleIndex);
        }

        // catches: TryActivate (Paused -> Active) NOT resetting the per-stop fire
        // indices on a multi-stop route.
        [Fact]
        public void Activate_ResetsPerStopFireIndices()
        {
            var route = Build2StopRoute(
                status: RouteStatus.Paused,
                lastObservedLoopCycleIndex: 7, stop0LastFired: 7, stop1LastFired: 7);

            bool ok = RouteOrchestrator.TryActivate(route, 5000.0);

            Assert.True(ok);
            Assert.Equal(-1, route.LastObservedLoopCycleIndex);
            Assert.Equal(-1, route.Stops[0].LastFiredCycleIndex);
            Assert.Equal(-1, route.Stops[1].LastFiredCycleIndex);
        }

        // ==================================================================
        // (6) Warp past multiple cycles AND windows in one tick
        // ==================================================================

        // catches: a warp tick that jumps past several cycles AND both windows
        // replaying each skipped cycle instead of firing each due window once and
        // bumping ONCE for the landing cycle. lastObserved=0, stops already fired
        // cycle 0; a tick lands deep in cycle 3 PAST dock B -> both windows fire
        // once for cycle 3, CompletedCycles bumps once.
        [Fact]
        public void WarpPastCyclesAndWindows_FiresEachDueWindowOnce_BumpsOnce()
        {
            // After cycle 0: lastObserved 0, both stops fired cycle 0. CompletedCycles 1.
            var route = Build2StopRoute(
                lastObservedLoopCycleIndex: 0, stop0LastFired: 0, stop1LastFired: 0);
            route.CompletedCycles = 1;
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit()); // span 400, anchor 1000
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            // Tick deep in cycle 3, past dock B: ut = 1000 + 3*400 + 350 = 2550,
            // loopUT = 1350 (>= dock B 1300). Both windows due for cycle 3.
            RouteOrchestrator.Tick(2550.0, env);

            // Each window fired exactly ONCE for the landing cycle (3), NOT replayed
            // for cycles 1 and 2.
            Assert.Single(Delivered().Where(d => d.RouteStopIndex == 0));
            Assert.Single(Delivered().Where(d => d.RouteStopIndex == 1));
            Assert.Single(Dispatched());
            // The fired cycle's id is cycle-{Completed(1)+Skipped(0)} = cycle-1.
            Assert.Equal("cycle-1", Dispatched()[0].RouteCycleId);
            Assert.All(Delivered(), d => Assert.Equal("cycle-1", d.RouteCycleId));
            // CompletedCycles bumped ONCE (1 -> 2) for the single landing cycle.
            Assert.Equal(2, route.CompletedCycles);
            // Both stops + the route marker snapped forward to the landing cycle (3).
            Assert.Equal(3, route.LastObservedLoopCycleIndex);
            Assert.Equal(3, route.Stops[0].LastFiredCycleIndex);
            Assert.Equal(3, route.Stops[1].LastFiredCycleIndex);
        }

        // ==================================================================
        // Blocked multi-stop cycle
        // ==================================================================

        // catches: a blocked multi-stop cycle emitting any window, or not snapping
        // every stop forward (which would re-fire the block every tick).
        [Fact]
        public void BlockedMultiStopCycle_EmitsNothing_SnapsAllStops()
        {
            var route = Build2StopRoute(isKscOrigin: false); // non-KSC -> origin gate runs
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallRealPathRowEmitter();
            var env = new BlockedEnv();

            // Tick past dock B so BOTH windows would be due if eligible.
            RouteOrchestrator.Tick(1350.0, env);

            Assert.Empty(Delivered());
            Assert.Empty(Dispatched());
            Assert.Equal(1, route.SkippedCycles);
            Assert.Equal(0, route.CompletedCycles);
            // Every stop + the route marker snapped to the blocked cycle (0).
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
            Assert.Equal(0, route.Stops[0].LastFiredCycleIndex);
            Assert.Equal(0, route.Stops[1].LastFiredCycleIndex);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, route.LastHoldKind);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("LoopRoute(multi)") && l.Contains("BLOCKED"));
        }

        // catches: the multi-stop path not detecting a pre-dock tick (loopUT before
        // dock A) — no window should fire, nothing consumed.
        [Fact]
        public void PreDockA_Tick_FiresNothing()
        {
            var route = Build2StopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            // loopUT 1050 < dock A 1150 -> no window due.
            RouteOrchestrator.Tick(1050.0, env);

            Assert.Empty(Ledger.Actions);
            Assert.Equal(0, route.CompletedCycles);
            Assert.Equal(-1, route.LastObservedLoopCycleIndex);
            Assert.Equal(-1, route.Stops[0].LastFiredCycleIndex);
            Assert.Equal(-1, route.Stops[1].LastFiredCycleIndex);
        }
    }
}
