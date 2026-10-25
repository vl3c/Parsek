using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// ROUTE-WINDOW-SAVE-LOAD-KEEPS-WINDOW-ACTIONS. A save written inside a go-back rewind's
    /// 15 s lead-time window carries the route state floor, so its load retired route rows only
    /// after the floor: a pause written after the save, or a route created after it, survived the
    /// load (the route could then run its window crossing a second time). The load now retires the
    /// rows in (save clock, floor] that the save's own route event log does not hold and sends the
    /// committed routes its route copy does not hold, created after its clock, dormant.
    /// Fixture (as <see cref="RouteGoBackWindowFloorTests"/>): span [1000, 1300], cadence 300,
    /// dock 1150, so crossings at 1150, 1450, 1750; the rewind save at 1460 (after the cycle-1
    /// crossing, fired at 1452), the clock wound back to 1445; an F5 at 1450 inside the window.
    /// </summary>
    [Collection("Sequential")]
    public class RouteWindowSaveLoadTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteWindowSaveLoadTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RewindContext.ResetForTesting();
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
            RecordingStore.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private sealed class EligibleEnv : IRouteRuntimeEnvironment
        {
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall) { shortfall = 0.0; lackingResource = string.Empty; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        private readonly EligibleEnv env = new EligibleEnv();

        private static Route BuildLoopRoute(string id = "route-loop", double createdUT = 900.0)
        {
            return new Route
            {
                Id = id, Name = id, Status = RouteStatus.Active, CreatedUT = createdUT,
                IsKscOrigin = true, BackingMissionTreeId = "tree-" + id, RecordedDockUT = 1150.0,
                DockMemberRecordingId = "rec", LoopAnchorUT = 1000.0, LastObservedLoopCycleIndex = -1,
                DispatchInterval = 300.0, TransitDuration = 300.0,
                CostManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec", TreeId = "tree-" + id, RouteProofHash = "deadbeef" },
                },
            };
        }

        private static Route Build2StopRoute()
        {
            return new Route
            {
                Id = "route-multi", Name = "route-multi", Status = RouteStatus.Active, CreatedUT = 900.0,
                IsKscOrigin = true, BackingMissionTreeId = "tree-multi", RecordedDockUT = 1300.0,
                DockMemberRecordingId = "rec-dock-b", LoopAnchorUT = 1000.0, LastObservedLoopCycleIndex = -1,
                DispatchInterval = 400.0, TransitDuration = 400.0,
                CostManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 }, { "Oxidizer", 120.0 } },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                        SegmentIndexBefore = 0, RecordedDockUT = 1150.0, LastFiredCycleIndex = -1,
                    },
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 43u },
                        DeliveryManifest = new Dictionary<string, double> { { "Oxidizer", 120.0 } },
                        SegmentIndexBefore = 1, RecordedDockUT = 1300.0, LastFiredCycleIndex = -1,
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock-b", TreeId = "tree-multi", RouteProofHash = "deadbeef" },
                },
            };
        }

        private static Route Snapshot(Route route)
        {
            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);
            return Route.DeserializeFrom(node);
        }

        private static ConfigNode SaveNow()
        {
            var node = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(node);
            return node;
        }

        private static Route Find(string id) => RouteStore.CommittedRoutes.SingleOrDefault(r => r.Id == id);

        private static double[] DispatchUTs(string routeId = "route-loop")
            => RouteEventLog.Events
                .Where(a => a.Type == RouteEventType.RouteDispatched && a.RouteId == routeId)
                .Select(a => a.UT).ToArray();

        private static List<RouteEvent> RowsOf(string routeId, RouteEventType t)
            => RouteEventLog.Events.Where(a => a.RouteId == routeId && a.Type == t).ToList();

        private static RouteEvent Marker(string routeId, RouteEventType type, double ut, string reason)
        {
            return new RouteEvent { Type = type, UT = ut, RouteId = routeId, RouteEndpointReason = reason };
        }

        private void InstallSingleStopLoop()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => r.Id == "route-multi"
                ? new GhostPlaybackLogic.LoopUnit(0, new[] { 0 }, 1000.0, 1400.0, 400.0, 1000.0)
                : new GhostPlaybackLogic.LoopUnit(0, new[] { 0 }, 1000.0, 1300.0, 300.0, 1000.0);
            RouteOrchestrator.DeliveryRowEmitterForTesting =
                (route, currentUT, e, cycleId, stopIndex, bump) =>
                {
                    RouteEventLog.Add(new RouteEvent
                    {
                        Type = RouteEventType.RouteCargoDelivered, UT = currentUT, RouteId = route.Id,
                        RouteCycleId = cycleId, RouteStopIndex = stopIndex,
                        Sequence = stopIndex * RouteOrchestrator.SeqStride + 3,
                    });
                };
        }

        /// <summary>
        /// The route-loop history: a player pause at 950 and resume at 960 (so a derived status is
        /// readable), crossings 0 (1150) and 1 (1452) fired, the rewind save at 1460, the
        /// abandoned future's crossing 2 at 1755, then the go-back to the clock 1445 with the floor
        /// 1460. <paramref name="extra"/> routes are installed with it.
        /// </summary>
        private void FlyThenGoBack(params Route[] extra)
        {
            InstallSingleStopLoop();
            RouteStore.AddRoute(BuildLoopRoute());
            foreach (Route r in extra)
                RouteStore.AddRoute(r);
            RouteEventLog.Add(Marker("route-loop", RouteEventType.RoutePaused, 950.0, "player-pause"));
            RouteEventLog.Add(Marker("route-loop", RouteEventType.RouteResumed, 960.0, "player-activate"));
            RouteOrchestrator.Tick(1150.0, env);
            RouteOrchestrator.Tick(1452.0, env);
            var savedAt1460 = RouteStore.CommittedRoutes.Select(Snapshot).ToList();
            RouteOrchestrator.Tick(1755.0, env);
            Assert.Equal(new[] { 1150.0, 1452.0, 1755.0 }, DispatchUTs());

            RouteLoadReconcile.ReconcileAtGoBackRewind(1445.0, savedAt1460, 1460.0);
            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
            Assert.Equal(1460.0, RouteStore.StateFloorUT);
        }

        // ------------------------------------------------------------------
        // The defect: a row written after an in-window save
        // ------------------------------------------------------------------

        // catches: the filed defect. F5 at 1450 inside the window, a player pause at 1455, then
        // a load back to the F5. The floor-raised cutoff (1460) kept the pause row and the route
        // stayed Paused; the save holds neither. The settled cycle-1 rows the save does hold stay,
        // and the crossing is not fired a second time.
        [Theory]
        [InlineData(true)]                           // F9 in flight: QuickloadFlight
        [InlineData(false)]                          // F9 at the Space Center: InSessionOther, needs the evidence
        public void LoadOfAnInWindowSave_RetiresAPauseWrittenAfterIt(bool inFlight)
        {
            LoadKind kind = inFlight ? LoadKind.QuickloadFlight : LoadKind.InSessionOther;
            FlyThenGoBack();
            ConfigNode f5At1450 = SaveNow();
            Assert.True(RouteOrchestrator.TryPause(Find("route-loop"), 1455.0));
            Assert.Single(RowsOf("route-loop", RouteEventType.RoutePaused).Where(r => r.UT == 1455.0));

            RouteLoadReconcileOutcome outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                kind, 1450.0, 1450.0, f5At1450);

            Assert.Equal(RouteLoadReconcileOutcome.Reconciled, outcome);
            Assert.DoesNotContain(RouteEventLog.Events, e => e.UT == 1455.0);
            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
            Route live = Find("route-loop");
            Assert.Equal(RouteStatus.Active, live.Status);
            Assert.Equal(1, live.LastObservedLoopCycleIndex);
            Assert.Equal(1460.0, RouteStore.StateFloorUT);
            Assert.Contains(logLines, l => l.Contains("[RouteEventLog]")
                && l.Contains("RetireUnheldWindowRows: removed 1 route event(s) in (1450, 1460]"));
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("save written inside a go-back window saveUT=1450 floorUT=1460")
                && l.Contains("unheldWindowRows=1") && l.Contains("routesCreatedAfterTheSave=0"));
            if (kind == LoadKind.InSessionOther)
                Assert.Contains(logLines, l => l.Contains("evidence=window-route-events:1"));

            foreach (double ut in new[] { 1451.0, 1453.0, 1460.0 })
                RouteOrchestrator.Tick(ut, env);
            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
            RouteOrchestrator.Tick(1760.0, env);
            Assert.Equal(new[] { 1150.0, 1452.0, 1760.0 }, DispatchUTs());
        }

        // catches: the Space Center F9 keeping a Send Once armed after the save. With a row the
        // save does not hold the reconcile now runs, so the one-shots armed after it are cleared.
        [Fact]
        public void SpaceCenterLoadOfAnInWindowSave_ClearsOneShotsArmedAfterIt()
        {
            FlyThenGoBack();
            ConfigNode f5At1450 = SaveNow();
            Assert.True(RouteOrchestrator.TryPause(Find("route-loop"), 1454.0));
            Assert.True(RouteOrchestrator.TrySendOneCycleNow(Find("route-loop"), 1455.0));

            RouteLoadReconcile.ReconcileAtInSessionLoad(LoadKind.InSessionOther, 1450.0, 1450.0, f5At1450);

            Assert.False(Find("route-loop").SendOnceArmed);
            Assert.False(Find("route-loop").PauseAfterCurrentCycle);
            Assert.Equal(RouteStatus.Active, Find("route-loop").Status);
        }

        // catches: a revert to a launch made inside the window after a pause row. The revert
        // node (the launch save at 1447) does not hold the pause at 1449.
        [Fact]
        public void RevertToALaunchInsideTheWindow_RetiresAPauseWrittenAfterTheLaunch()
        {
            FlyThenGoBack();
            ConfigNode launchSaveAt1447 = SaveNow();
            Assert.True(RouteOrchestrator.TryPause(Find("route-loop"), 1449.0));

            RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.StockRevert,
                RouteLoadReconcile.ResolveCutoffUT(LoadKind.StockRevert, 1447.0, 1447.0),
                1447.0, launchSaveAt1447);

            Assert.DoesNotContain(RouteEventLog.Events, e => e.UT == 1449.0);
            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
            Assert.Equal(RouteStatus.Active, Find("route-loop").Status);
        }

        // ------------------------------------------------------------------
        // A route created after an in-window save
        // ------------------------------------------------------------------

        // catches: the second half of the defect. A route created at 1455 (CreatedUT below the
        // floor) runs its first crossing at 1456; the save at 1450 does not carry it. Kept
        // committed with a reset cursor, it ran the same crossing again after the load. It now
        // goes dormant with its rows retired, and comes back Paused at its creation.
        [Fact]
        public void LoadOfAnInWindowSave_SendsARouteCreatedAfterItDormant()
        {
            FlyThenGoBack();
            ConfigNode f5At1450 = SaveNow();
            Route created = BuildLoopRoute("route-new", createdUT: 1455.0);
            RouteStore.AddRoute(created);
            RouteOrchestrator.Tick(1456.0, env);
            Assert.Single(DispatchUTs("route-new"));

            RouteLoadReconcile.ReconcileAtInSessionLoad(LoadKind.QuickloadFlight, 1450.0, 1450.0, f5At1450);

            Assert.Null(Find("route-new"));
            Assert.Contains(RouteStore.DormantRoutes, r => r.Id == "route-new");
            Assert.Empty(RouteEventLog.Events.Where(e => e.RouteId == "route-new"));
            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
            Assert.Contains(logLines, l => l.Contains("routesCreatedAfterTheSave=1"));
            Assert.Contains(logLines, l => l.Contains("windowRoutesDormant=1"));

            RouteOrchestrator.Tick(1456.0, env);
            Route back = Find("route-new");
            Assert.NotNull(back);
            // Materialized Paused; this headless store has no recordings, so it reads its sources
            // as missing. Either way it does not run.
            Assert.NotEqual(RouteStatus.Active, back.Status);
            RouteOrchestrator.Tick(1458.0, env);
            Assert.Empty(DispatchUTs("route-new"));
        }

        // The go-back's own routes are held by the in-window save and stay committed, even one
        // created inside the window before the save.
        [Fact]
        public void LoadOfAnInWindowSave_KeepsARouteTheSaveHolds()
        {
            FlyThenGoBack();
            Route early = BuildLoopRoute("route-early", createdUT: 1447.0);
            early.Status = RouteStatus.Paused;
            RouteStore.AddRoute(early);
            ConfigNode f5At1450 = SaveNow();

            RouteLoadReconcile.ReconcileAtInSessionLoad(LoadKind.QuickloadFlight, 1450.0, 1450.0, f5At1450);

            Assert.NotNull(Find("route-early"));
            Assert.NotNull(Find("route-loop"));
            Assert.Empty(RouteStore.DormantRoutes);
        }

        // ------------------------------------------------------------------
        // The multi-stop resume rule
        // ------------------------------------------------------------------

        // catches: keying the whole reconcile to the save's own clock instead. A multi-stop
        // cycle dispatched at 1150 with stop 0 delivered lies inside the window (clock 1145,
        // floor 1160) and the in-window save at 1148 holds both rows; a pause after the save is
        // retired, the held dispatch is kept, and stop 1's window resumes cycle-0 instead of
        // dispatching it again (a second origin debit).
        [Fact]
        public void MultiStopCycleHeldByTheInWindowSave_ResumesItsLoggedDispatch()
        {
            InstallSingleStopLoop();
            RouteStore.AddRoute(Build2StopRoute());
            RouteEventLog.Add(Marker("route-multi", RouteEventType.RoutePaused, 950.0, "player-pause"));
            RouteEventLog.Add(Marker("route-multi", RouteEventType.RouteResumed, 960.0, "player-activate"));
            RouteOrchestrator.Tick(1150.0, env);
            Route at1160 = Snapshot(Find("route-multi"));
            RouteOrchestrator.Tick(1350.0, env);
            RouteLoadReconcile.ReconcileAtGoBackRewind(1145.0, new[] { at1160 }, 1160.0);
            Assert.Equal(1160.0, RouteStore.StateFloorUT);

            ConfigNode f5At1148 = SaveNow();
            Assert.True(RouteOrchestrator.TryPause(Find("route-multi"), 1149.0));

            RouteLoadReconcile.ReconcileAtInSessionLoad(LoadKind.QuickloadFlight, 1148.0, 1148.0, f5At1148);

            Assert.DoesNotContain(RouteEventLog.Events, e => e.UT == 1149.0);
            Assert.Single(RowsOf("route-multi", RouteEventType.RouteDispatched));
            Assert.Equal(RouteStatus.Active, Find("route-multi").Status);
            Assert.Equal(0, Find("route-multi").CompletedCycles + Find("route-multi").SkippedCycles);

            RouteOrchestrator.Tick(1350.0, env);
            var dispatched = RowsOf("route-multi", RouteEventType.RouteDispatched);
            Assert.Single(dispatched);
            Assert.Equal("cycle-0", dispatched[0].RouteCycleId);
            var delivered = RowsOf("route-multi", RouteEventType.RouteCargoDelivered);
            Assert.Equal(new[] { 0, 1 }, delivered.Select(d => d.RouteStopIndex).OrderBy(x => x).ToArray());
            Assert.All(delivered, d => Assert.Equal("cycle-0", d.RouteCycleId));
        }

        // ------------------------------------------------------------------
        // Unchanged behaviour
        // ------------------------------------------------------------------

        // A save outside any window (no floor): the reconcile keys to the save's clock exactly as
        // before and builds no window.
        [Fact]
        public void LoadOfASaveOutsideAnyWindow_IsUnchanged()
        {
            InstallSingleStopLoop();
            RouteStore.AddRoute(BuildLoopRoute());
            RouteEventLog.Add(Marker("route-loop", RouteEventType.RoutePaused, 950.0, "player-pause"));
            RouteEventLog.Add(Marker("route-loop", RouteEventType.RouteResumed, 960.0, "player-activate"));
            RouteOrchestrator.Tick(1150.0, env);
            ConfigNode at1200 = SaveNow();
            Assert.True(double.IsNaN(RouteStore.ReadSavedRouteStateFloorUT(at1200)));
            Assert.True(RouteOrchestrator.TryPause(Find("route-loop"), 1210.0));

            RouteLoadReconcileOutcome outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.QuickloadFlight, 1200.0, 1200.0, at1200);

            Assert.Equal(RouteLoadReconcileOutcome.Reconciled, outcome);
            Assert.DoesNotContain(RouteEventLog.Events, e => e.UT == 1210.0);
            Assert.Equal(new[] { 1150.0 }, DispatchUTs());
            Assert.Equal(RouteStatus.Active, Find("route-loop").Status);
            Assert.DoesNotContain(logLines, l => l.Contains("save written inside a go-back window"));
            Assert.DoesNotContain(logLines, l => l.Contains("RetireUnheldWindowRows"));
            Assert.DoesNotContain(logLines, l => l.Contains("windowSaveUT="));
        }

        // A cold load is owned by RouteStore.LoadRoutesFrom: the in-session hook builds no window
        // and touches nothing; the cold path restores exactly the save's log.
        [Fact]
        public void ColdLoadOfAnInWindowSave_IsOwnedByTheColdPath()
        {
            FlyThenGoBack();
            ConfigNode f5At1450 = SaveNow();
            Assert.True(RouteOrchestrator.TryPause(Find("route-loop"), 1455.0));
            int before = RouteEventLog.Events.Count;

            RouteLoadReconcileOutcome outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.Cold, 1450.0, 1450.0, f5At1450);

            Assert.Equal(RouteLoadReconcileOutcome.SkippedOwnedElsewhere, outcome);
            Assert.Equal(before, RouteEventLog.Events.Count);
            Assert.DoesNotContain(logLines, l => l.Contains("save written inside a go-back window"));

            RouteStore.LoadRoutesFrom(f5At1450);
            Assert.DoesNotContain(RouteEventLog.Events, e => e.UT == 1455.0);
            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
            Assert.Equal(1460.0, RouteStore.StateFloorUT);
        }

        // A forward scene change inside the window: its node is the live state, so the save
        // holds every row and route, and the load still touches nothing.
        [Fact]
        public void ForwardSceneChangeInsideTheWindow_AfterAPause_TouchesNothing()
        {
            FlyThenGoBack();
            Assert.True(RouteOrchestrator.TryPause(Find("route-loop"), 1455.0));
            ConfigNode outgoing = SaveNow();

            RouteLoadReconcileOutcome outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.InSessionOther, 1456.0, 1456.0, outgoing);

            Assert.Equal(RouteLoadReconcileOutcome.SkippedNothingAfterCutoff, outcome);
            Assert.Contains(RouteEventLog.Events, e => e.UT == 1455.0);
            Assert.Equal(RouteStatus.Paused, Find("route-loop").Status);
            Assert.Contains(logLines, l => l.Contains("unheldWindowRows=0") && l.Contains("routesCreatedAfterTheSave=0"));
        }

        // ------------------------------------------------------------------
        // Pure rules
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(1450.0, 1460.0, true)]
        [InlineData(1460.0, 1460.0, false)]
        [InlineData(1470.0, 1460.0, false)]
        [InlineData(1450.0, double.NaN, false)]
        [InlineData(double.NaN, 1460.0, false)]
        [InlineData(0.0, 1460.0, false)]
        public void IsInSaveWindow_OnlyWhenTheFloorLiesAheadOfTheSaveClock(double save, double floor, bool expected)
        {
            Assert.Equal(expected, RouteEventRetire.IsInSaveWindow(save, floor));
            Assert.Equal(expected, RouteLoadReconcile.TryBuildSaveWindow(save, floor, null, null) != null);
        }

        private static RouteEvent Ev(RouteEventType t, double ut, string cycle = null, int stop = -1, int seq = 0)
            => new RouteEvent { Type = t, UT = ut, RouteId = "r", RouteCycleId = cycle, RouteStopIndex = stop, Sequence = seq };

        [Fact]
        public void SelectUnheldWindowRows_MatchesHeldRowsOneToOne_InsideTheWindowOnly()
        {
            var live = new List<RouteEvent>
            {
                Ev(RouteEventType.RouteDispatched, 1440.0, "cycle-0"),   // at/below the save clock: never
                Ev(RouteEventType.RouteDispatched, 1452.0, "cycle-1"),   // held
                Ev(RouteEventType.RouteCargoDelivered, 1452.0, "cycle-1", 0, 3), // held
                Ev(RouteEventType.RoutePaused, 1455.0),                  // not held
                Ev(RouteEventType.RouteHeld, 1456.0),                    // held once
                Ev(RouteEventType.RouteHeld, 1456.0),                    // its twin: not held
                Ev(RouteEventType.RouteDispatched, 1470.0, "cycle-2"),   // above the floor: never
            };
            var saved = new List<RouteEvent>
            {
                Ev(RouteEventType.RouteDispatched, 1440.0, "cycle-0"),
                Ev(RouteEventType.RouteDispatched, 1452.0, "cycle-1"),
                Ev(RouteEventType.RouteCargoDelivered, 1452.0, "cycle-1", 0, 3),
                Ev(RouteEventType.RouteHeld, 1456.0),
            };

            List<RouteEvent> picked = RouteEventRetire.SelectUnheldWindowRows(live, 1450.0, 1460.0, saved);

            Assert.Equal(2, picked.Count);
            Assert.Same(live[3], picked[0]);
            Assert.Same(live[5], picked[1]);
            Assert.Empty(RouteEventRetire.SelectUnheldWindowRows(live, 1450.0, double.NaN, saved));
            Assert.Equal(5, RouteEventRetire.SelectUnheldWindowRows(live, 1450.0, 1460.0, null).Count);
        }

        [Fact]
        public void WindowRowKey_SurvivesTheSaveRoundTrip()
        {
            var e = new RouteEvent
            {
                Type = RouteEventType.RouteCargoDelivered, UT = 1452.1234567891, Sequence = 11,
                RouteId = "r", RouteCycleId = "cycle-1", RouteStopIndex = 1,
            };
            var node = new ConfigNode("ROUTE_EVENT_LOG");
            e.SerializeInto(node);
            RouteEvent back = RouteEvent.DeserializeFrom(node.GetNodes(RouteEvent.NodeName)[0], out _);
            Assert.Equal(RouteEventRetire.WindowRowKey(e), RouteEventRetire.WindowRowKey(back));
            Assert.NotEqual(RouteEventRetire.WindowRowKey(e),
                RouteEventRetire.WindowRowKey(new RouteEvent { Type = e.Type, UT = e.UT, Sequence = 11, RouteId = "r", RouteCycleId = "cycle-1", RouteStopIndex = 0 }));
        }

        [Fact]
        public void SelectRoutesCreatedAfterTheSave_TakesOnlyRoutesTheCopyLacks()
        {
            var committed = new List<Route>
            {
                new Route { Id = "kept-before", CreatedUT = 1400.0 },
                new Route { Id = "held-in-window", CreatedUT = 1447.0 },
                new Route { Id = "created-after", CreatedUT = 1455.0 },
                new Route { Id = "legacy", CreatedUT = -1.0 },
                new Route { Id = "materialized-after", CreatedUT = 1452.0 },
            };
            var saved = new List<Route>
            {
                new Route { Id = "kept-before" },
                new Route { Id = "held-in-window" },
            };

            HashSet<string> ids = RouteLoadReconcile.SelectRoutesCreatedAfterTheSave(committed, saved, 1450.0);

            Assert.Equal(new[] { "created-after", "materialized-after" }, ids.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.Empty(RouteLoadReconcile.SelectRoutesCreatedAfterTheSave(committed, saved, double.NaN));
        }

        [Fact]
        public void HasWindowStateAfterSave_NamesTheFirstHit()
        {
            Assert.True(RouteLoadReconcile.HasWindowStateAfterSave(2, null, out string a));
            Assert.Equal("window-route-events:2", a);
            Assert.True(RouteLoadReconcile.HasWindowStateAfterSave(0, new HashSet<string> { "route-new" }, out string b));
            Assert.StartsWith("window-route-created:", b);
            Assert.False(RouteLoadReconcile.HasWindowStateAfterSave(0, new HashSet<string>(), out string c));
            Assert.Equal("none", c);
        }

        [Fact]
        public void Classify_ForcedDormantIds_GoDormantWhateverTheirCreation()
        {
            var a = new Route { Id = "a", CreatedUT = 1455.0 };
            var b = new Route { Id = "b", CreatedUT = 1400.0 };
            RouteRewindClassifier.Classify(new[] { a, b }, null, 1460.0,
                out List<Route> committed, out List<Route> dormant, new HashSet<string> { "a" });
            Assert.Equal(new[] { "b" }, committed.Select(r => r.Id).ToArray());
            Assert.Equal(new[] { "a" }, dormant.Select(r => r.Id).ToArray());

            RouteRewindClassifier.Classify(new[] { a, b }, null, 1460.0, out committed, out dormant);
            Assert.Equal(2, committed.Count);
            Assert.Empty(dormant);
        }

        [Fact]
        public void ReadSavedEvents_DecodesTheSavesLogWithoutTouchingMemory()
        {
            RouteEventLog.Add(Ev(RouteEventType.RouteDispatched, 1452.0, "cycle-1"));
            ConfigNode saved = SaveNow();
            RouteEventLog.Add(Ev(RouteEventType.RoutePaused, 1455.0));

            List<RouteEvent> read = RouteEventLog.ReadSavedEvents(saved);

            Assert.Single(read);
            Assert.Equal(1452.0, read[0].UT);
            Assert.Equal(2, RouteEventLog.Events.Count);
            Assert.Empty(RouteEventLog.ReadSavedEvents(new ConfigNode("SCENARIO")));
            Assert.Empty(RouteEventLog.ReadSavedEvents(null));
        }
    }
}
