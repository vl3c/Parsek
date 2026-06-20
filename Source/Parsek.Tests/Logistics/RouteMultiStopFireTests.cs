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

        // ==================================================================
        // (BLOCKER-1) Cross-cycle gap-tick desync — the headline regression
        // ==================================================================

        // THE BLOCKER-1 regression (critical). A tick lands in the (dockA, dockB)
        // gap of cycle 1 while cycle 0's dockB is STILL OWED (cycle 0 fired only its
        // window 0). On the pre-fix code the per-stop due pass derived a DIFFERENT
        // sDockCycle per stop (stop 0 -> 1, stop 1 -> 0) but fired BOTH under the
        // SAME frozen cycleId = cycle-{C+S}; the deferred bump never fired (loopUT
        // in the gap), so the cycleId never advanced. A later tick past dock B then
        // found the gap-tick's row under the frozen cycleId and SILENTLY DROPPED
        // cycle 1's dispatch + one delivery, and CompletedCycles under-counted
        // (observed pre-fix: del0=1 del1=1 disp=1 completed=1). The fix processes
        // ONE dock-cycle per pass (lowest owed cMin first) with the catch-up loop
        // re-invoking after each C+S bump, so each cycle gets a UNIQUE cycleId.
        //
        // TRUTH after the three ticks: 2 complete cycles -> dock A fires twice, dock
        // B fires twice, 2 dispatches under cycle-0 / cycle-1, CompletedCycles == 2.
        [Fact]
        public void Blocker1_GapTickWithPriorOwedWindow_NoDroppedCycleOrDelivery()
        {
            var route = Build2StopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit()); // span [1000,1400], docks 1150 / 1300
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            // Tick 1: dock A of cycle 0 (loopUT 1150). Only window 0 fires; cycle 0's
            // dock B (1300) is NOT reached, so it stays OWED. Cycle NOT complete.
            RouteOrchestrator.Tick(1150.0, env);
            Assert.Equal(0, route.CompletedCycles);

            // Tick 2: the (dockA, dockB) GAP of cycle 1. ut = 1600 ->
            // loopUT = 1000 + (600 - 400) = 1200, between dock A(1150) and dock B(1300).
            // Cycle 0's dock B (cMin=0) is owed AND its dock phase IS reached this
            // tick (1200 >= ... no: 1200 < 1300). So cycle 0's dock B is NOT yet
            // reachable at loopUT 1200 either. cMin=0 dock B stays owed; cycle 1's
            // dock A (cMin would be 1 for stop 0) is reached. The fix must NOT fire
            // cycle 1's dock A under cycle 0's id, and must NOT lose anything.
            RouteOrchestrator.Tick(1600.0, env);

            // Tick 3: past dock B of cycle 1. ut = 1750 ->
            // loopUT = 1000 + (750 - 400) = 1350 (>= dock B 1300). Both remaining
            // owed windows (cycle 0 dock B, cycle 1 dock B) become reachable; the
            // catch-up loop processes cycle 0 then cycle 1 in ascending order.
            RouteOrchestrator.Tick(1750.0, env);

            var delivered = Delivered();
            var dispatched = Dispatched();

            // No dropped cycle, no dropped delivery, no under-count.
            Assert.Equal(2, delivered.Count(d => d.RouteStopIndex == 0)); // dock A x2
            Assert.Equal(2, delivered.Count(d => d.RouteStopIndex == 1)); // dock B x2
            Assert.Equal(2, dispatched.Count);                            // one per cycle
            Assert.Equal(2, route.CompletedCycles);

            // Each cycle fires EXACTLY ONCE per dock, under a UNIQUE cycleId.
            Assert.Contains(dispatched, d => d.RouteCycleId == "cycle-0");
            Assert.Contains(dispatched, d => d.RouteCycleId == "cycle-1");
            // cycle 0's two deliveries are under cycle-0, cycle 1's under cycle-1.
            Assert.Single(delivered.Where(d => d.RouteStopIndex == 0 && d.RouteCycleId == "cycle-0"));
            Assert.Single(delivered.Where(d => d.RouteStopIndex == 1 && d.RouteCycleId == "cycle-0"));
            Assert.Single(delivered.Where(d => d.RouteStopIndex == 0 && d.RouteCycleId == "cycle-1"));
            Assert.Single(delivered.Where(d => d.RouteStopIndex == 1 && d.RouteCycleId == "cycle-1"));
        }

        // Variant: a SINGLE gap tick where TWO cycles' windows are simultaneously
        // owed at DISTINCT dock-cycles - the catch-up loop must process cMin=0
        // (cycle 0's owed dock B) then cMin=1 (cycle 1's dock A) within ONE Tick,
        // each under its own UNIQUE cycleId. This is the precise within-tick
        // multi-owed-cycle path the catch-up loop exists for: at loopUT in cycle 1's
        // (dockA, dockB) gap, stop 1's recorded dock phase resolves to cycle 0 (still
        // owed, cursor -1) while stop 0's resolves to cycle 1 (owed, cursor 0).
        [Fact]
        public void Blocker1_SingleGapTickTwoOwedCycles_CatchUpProcessesBoth()
        {
            // Persisted post-cycle-0-window-0 state: stop 0 fired cycle 0 (dock A),
            // stop 1 never fired, route marker still -1 (cycle 0 not complete). The
            // dispatch + window-0 delivery rows for cycle-0 are in the ledger.
            var route = Build2StopRoute(stop0LastFired: 0, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteDispatched, UT = 1150.0, RouteId = route.Id,
                RouteCycleId = "cycle-0", RouteStopIndex = 0, Sequence = 0,
            });
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteCargoDelivered, UT = 1150.0, RouteId = route.Id,
                RouteCycleId = "cycle-0", RouteStopIndex = 0,
                Sequence = 0 * RouteOrchestrator.SeqStride + 3,
            });

            // One gap tick in cycle 1: ut = 1600 -> loopUT = 1000 + (600 - 400) = 1200,
            // between dock A(1150) and dock B(1300). stop 1's dock phase (1300) is NOT
            // yet reached at 1200, so it resolves to cycle 0 (cMin=0, still owed);
            // stop 0's dock A IS reached, resolving to cycle 1 (owed, cursor 0).
            RouteOrchestrator.Tick(1600.0, env);

            var delivered = Delivered();
            // cMin=0 pass: cycle 0's owed dock B fires + completes cycle 0.
            Assert.Single(delivered.Where(d => d.RouteStopIndex == 1 && d.RouteCycleId == "cycle-0"));
            // cMin=1 pass: cycle 1's dock A fires under a FRESH cycleId (NOT cycle-0).
            Assert.Single(delivered.Where(d => d.RouteStopIndex == 0 && d.RouteCycleId == "cycle-1"));
            // Exactly one NEW dispatch (cycle 1, under cycle-1); cycle 0's was pre-seeded.
            Assert.Equal(2, Dispatched().Count);
            Assert.Contains(Dispatched(), d => d.RouteCycleId == "cycle-1");
            // cycle 0 completed (its last dock reached this gap tick); cycle 1 is
            // PARTIAL (its dock B not reached at loopUT 1200) -> CompletedCycles 0 -> 1.
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
            // cycle 1's dock B is NOT yet fired (loopUT 1200 < dock B 1300).
            Assert.Empty(delivered.Where(d => d.RouteStopIndex == 1 && d.RouteCycleId == "cycle-1"));
        }

        // catches (A3-fix re-review BLOCKER): a multi-stop cycle BLOCKED while the
        // tick lands in its OWN (dockA, dockB) gap must be ATOMICALLY skipped (OQ4
        // all-or-nothing-at-dispatch / OQ3 snap-EVERY-stop). The fix's first cut
        // narrowed the blocked-branch snap to only the windows dock-crossed this
        // tick, so dock B (not yet dock-crossed in the gap) was left un-snapped;
        // if the block then CLEARED before the ghost reached dock B, dock B fired
        // next tick as a SEPARATELY-dispatched delivery under a FRESH cycleId,
        // splitting one logically-skipped cycle into "dock A skipped" + "dock B
        // dispatched (funds + origin debit) + delivered" - an unaffordable cycle
        // the player's resources never supported. The unconditional `< cMin` snap
        // skips the whole blocked cycle atomically.
        [Fact]
        public void InGapBlockedThenUnblocked_WholeCycleSkipped_NoSplitDispatch()
        {
            var route = Build2StopRoute(isKscOrigin: false); // non-KSC -> origin gate runs
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit()); // span [1000,1400], docks 1150 / 1300
            InstallRealPathRowEmitter();

            // Tick 1: in cycle 0's (dockA, dockB) gap (ut 1200 -> loopUT 1200, between
            // dock A 1150 and dock B 1300), origin BLOCKED. The whole cycle 0 must be
            // skipped: BOTH stops snapped to 0 (not just dock A), nothing emitted.
            RouteOrchestrator.Tick(1200.0, new BlockedEnv());

            Assert.Empty(Delivered());
            Assert.Empty(Dispatched());
            Assert.Equal(1, route.SkippedCycles);
            Assert.Equal(0, route.CompletedCycles);
            // The fix: EVERY stop snapped to the blocked cycle 0, including dock B
            // whose dock phase was NOT reached this tick (the pre-fix bug left it -1).
            Assert.Equal(0, route.Stops[0].LastFiredCycleIndex);
            Assert.Equal(0, route.Stops[1].LastFiredCycleIndex);
            Assert.Equal(0, route.LastObservedLoopCycleIndex);

            // Tick 2: the block has CLEARED and the ghost has reached dock B of cycle
            // 0 (ut 1350 -> loopUT 1350 >= dock B 1300, still cycle 0). cycle 0 was
            // already atomically skipped, so NOTHING fires - no spurious split
            // dispatch, no orphan dock-B delivery. (cycle 1 would dispatch fresh on a
            // future cycle-1 tick; this tick is still cycle 0.)
            RouteOrchestrator.Tick(1350.0, new EligibleEnv());

            Assert.Empty(Delivered());
            Assert.Empty(Dispatched());
            Assert.Equal(0, route.CompletedCycles);
            Assert.Equal(1, route.SkippedCycles); // no NEW skip; cycle 0 already counted
        }

        // ==================================================================
        // (C-2) Pickup replay backstop — no double physical debit
        // ==================================================================

        // C-2 (defense-in-depth): a multi-stop pickup window must NOT double-debit
        // its physical source across a simulated reload. Pre-seed the ledger with
        // the window's RouteCargoPickedUp row (as if it fired + saved), then
        // re-present the window: the C-2 backstop (IsPickupAlreadyInLedger) must
        // suppress the SECOND physical debit (PickupDebitApplierForTesting NOT
        // re-invoked) and emit no second pickup row.
        [Fact]
        public void PickupBackstop_NoDoubleDebitAcrossReload()
        {
            // A 1-stop PICKUP route (stop 0 picks up at dock A). Single-stop keeps
            // the byte-identical scalar path; the backstop is path-agnostic.
            var route = new Route
            {
                Id = "route-pickup",
                Status = RouteStatus.Active,
                IsKscOrigin = true,
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = 1150.0,
                DockMemberRecordingId = "rec-dock-a",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = 400.0,
                TransitDuration = 400.0,
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 99u },
                        PickupManifest = new Dictionary<string, double> { { "Ore", 50.0 } },
                        SegmentIndexBefore = 0,
                        RecordedDockUT = 1150.0,
                        LastFiredCycleIndex = -1,
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock-a", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit(spanStartUT: 1000.0, spanEndUT: 1200.0,
                cadenceSeconds: 200.0, phaseAnchorUT: 1000.0));

            int physicalDebits = 0;
            RouteOrchestrator.PickupDebitApplierForTesting = (endpoint, manifest, env2) =>
            {
                physicalDebits++;
                return new RouteOrchestrator.OriginDebitOutcome
                {
                    ActualDebited = new Dictionary<string, double> { { "Ore", 50.0 } },
                    RequestedOnShortfall = null,
                    OriginVesselPid = 99u,
                    Short = false,
                    Unresolved = false,
                };
            };
            var env = new EligibleEnv();

            // Pre-seed the cycle-0 pickup row + dispatch row as if the window fired
            // and was saved (the reload state). stopIndex 0.
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteDispatched, UT = 1150.0, RouteId = route.Id,
                RouteCycleId = "cycle-0", RouteStopIndex = -1, Sequence = 0,
            });
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteCargoPickedUp, UT = 1150.0, RouteId = route.Id,
                RouteCycleId = "cycle-0", RouteStopIndex = 0,
                Sequence = 0 * RouteOrchestrator.SeqStride + 2,
            });
            int pickupRowsBefore = Ledger.Actions.Count(a => a.Type == GameActionType.RouteCargoPickedUp);

            // Re-present the window directly (the C-2 guard lives in EmitPickupHalf,
            // exercised here through the real per-window emit). PendingStopIndex 0.
            route.PendingStopIndex = 0;
            RouteOrchestrator.EmitPickupHalf(route, 1150.0, env, "cycle-0");

            // The physical debit was NOT re-applied (no double-debit of the source).
            Assert.Equal(0, physicalDebits);
            // No second pickup row emitted.
            Assert.Equal(pickupRowsBefore,
                Ledger.Actions.Count(a => a.Type == GameActionType.RouteCargoPickedUp));
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("C-2 backstop"));
        }

        // Sanity: the C-2 backstop is genuinely per-window (RouteId, cycleId,
        // stopIndex). A window-0 pickup row does NOT mark window 1 as already in
        // the ledger.
        [Fact]
        public void PickupBackstop_IsPerWindow_NotPerCycle()
        {
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteCargoPickedUp, UT = 1150.0, RouteId = "route-y",
                RouteCycleId = "cycle-0", RouteStopIndex = 0,
            });
            Assert.True(RouteOrchestrator.IsPickupAlreadyInLedger("route-y", "cycle-0", 0));
            Assert.False(RouteOrchestrator.IsPickupAlreadyInLedger("route-y", "cycle-0", 1));
        }
    }
}
