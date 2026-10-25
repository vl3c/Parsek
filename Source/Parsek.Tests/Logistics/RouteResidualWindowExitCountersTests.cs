using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The two exits that still key their route cutoff to the floor alone (a go-back to a rewind
    /// save written inside an earlier go-back's window, a Re-Fly start from an RP quicksave
    /// written inside one) keep the rows in (save clock, floor], including rows written after
    /// the save. A route with such a row keeps the cycle counters rebuilt from the kept rows:
    /// restoring the save's counters would reissue that row's cycle id, so a multi-stop route's
    /// later stop would resume a dispatch the loaded world never paid for (cargo from nothing)
    /// and a single-stop route would lose the crossing to the dedup. A row the save holds (the
    /// earlier go-back's settled rows) does not count, so the multi-stop resume rule stands.
    /// Fixture: span [1000, 1400], cadence 400; multi-stop docks at 1150 + 400c (stop 0) and
    /// 1300 + 400c (stop 1); single-stop docks at 1150 + 400c. The earlier go-back's floor is
    /// 1560 and the in-window save is written at 1545.
    /// </summary>
    [Collection("Sequential")]
    public class RouteResidualWindowExitCountersTests : IDisposable
    {
        private const double Floor = 1560.0;
        private const double SaveClock = 1545.0;
        private readonly List<string> logLines = new List<string>();

        public RouteResidualWindowExitCountersTests()
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

        private sealed class EligibleEnv : IRouteRuntimeEnvironment
        {
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall) { shortfall = 0.0; lackingResource = string.Empty; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        private readonly EligibleEnv env = new EligibleEnv();

        private static RouteStop Stop(uint pid, string resource, double amount, int segment, double dockUT)
        {
            return new RouteStop
            {
                Endpoint = new RouteEndpoint { VesselPersistentId = pid },
                DeliveryManifest = new Dictionary<string, double> { { resource, amount } },
                SegmentIndexBefore = segment, RecordedDockUT = dockUT, LastFiredCycleIndex = -1,
            };
        }

        private void Install(bool multiStop)
        {
            var stops = new List<RouteStop> { Stop(42u, "LiquidFuel", 100.0, 0, 1150.0) };
            if (multiStop)
                stops.Add(Stop(43u, "Oxidizer", 120.0, 1, 1300.0));
            RouteStore.AddRoute(new Route
            {
                Id = "route-r", Name = "route-r", Status = RouteStatus.Active, CreatedUT = 900.0,
                IsKscOrigin = true, BackingMissionTreeId = "tree-1",
                RecordedDockUT = multiStop ? 1300.0 : 1150.0,
                DockMemberRecordingId = "rec-dock", LoopAnchorUT = 1000.0, LastObservedLoopCycleIndex = -1,
                DispatchInterval = 400.0, TransitDuration = 400.0,
                CostManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                Stops = stops,
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            });
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1400.0, cadenceSeconds: 400.0, phaseAnchorUT: 1000.0);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
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

        private static Route Snapshot(Route route)
        {
            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);
            return Route.DeserializeFrom(node);
        }

        private static Route Live() => RouteStore.CommittedRoutes.Single();

        private static List<RouteEvent> Rows(RouteEventType t)
            => RouteEventLog.Events.Where(a => a.Type == t).ToList();

        private static bool Delivered(string cycleId, int stop, double ut)
            => Rows(RouteEventType.RouteCargoDelivered).Any(
                d => d.RouteCycleId == cycleId && d.RouteStopIndex == stop && d.UT == ut);

        private sealed class InWindowSave
        {
            public Route Route;
            public List<RouteEvent> Events;
        }

        // Cycle 0 delivered whole before the earlier go-back; the in-window save at 1545 holds
        // it. Then cycle 1 dispatches at 1550 (at or before the floor), after the save.
        private InWindowSave FlyCycleOneAfterTheInWindowSave(bool multiStop)
        {
            Install(multiStop);
            RouteOrchestrator.Tick(1150.0, env);
            if (multiStop)
                RouteOrchestrator.Tick(1300.0, env);
            Assert.Equal(1, Live().CompletedCycles);
            RouteStore.StateFloorUT = Floor;
            var save = new InWindowSave { Route = Snapshot(Live()), Events = RouteEventLog.Events.ToList() };
            RouteOrchestrator.Tick(1550.0, env);
            Assert.Single(Rows(RouteEventType.RouteDispatched).Where(d => d.RouteCycleId == "cycle-1"));
            if (multiStop)
                RouteOrchestrator.Tick(1700.0, env);
            return save;
        }

        private void ReFlyStartFrom(InWindowSave save)
        {
            ReconciliationBundle bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(
                bundle, SaveClock, new[] { save.Route }, Floor, SaveClock, save.Events);
        }

        private void GoBackTo(InWindowSave save)
        {
            RouteLoadReconcile.ReconcileAtGoBackRewind(
                SaveClock - 15.0, new[] { save.Route }, Floor, SaveClock, save.Events);
        }

        // catches: the Re-Fly start restoring the quicksave's counters (next id cycle-1) while
        // keeping cycle-1's dispatch row written after it: stop 1 then delivers under cycle-1,
        // resuming a dispatch the quicksave's world never paid for.
        [Fact]
        public void ReFlyStart_MultiStop_RowAfterTheSaveClock_CountersStayRebuilt()
        {
            InWindowSave save = FlyCycleOneAfterTheInWindowSave(multiStop: true);
            ReFlyStartFrom(save);

            Assert.Equal(2, Live().CompletedCycles);
            Assert.Contains(logLines, l => l.Contains("[ReconciliationBundle]")
                && l.Contains("RP quicksave written inside a go-back window")
                && l.Contains("1 route(s) keep rebuilt cycle counters"));
            Assert.Contains(logLines, l => l.Contains("cycle counters stay rebuilt"));

            RouteOrchestrator.Tick(1550.0, env);
            RouteOrchestrator.Tick(1700.0, env);
            Assert.False(Delivered("cycle-1", 1, 1700.0));
            Assert.True(Delivered("cycle-2", 0, 1550.0));
            Assert.True(Delivered("cycle-2", 1, 1700.0));
            Assert.Single(Rows(RouteEventType.RouteDispatched).Where(d => d.RouteCycleId == "cycle-2"));
        }

        // catches: the single-stop half: the restored counters reissue cycle-1, whose kept row
        // makes the dedup swallow the 1550 crossing, so the loaded world loses that delivery.
        [Fact]
        public void ReFlyStart_SingleStop_RowAfterTheSaveClock_TheCrossingStillDelivers()
        {
            InWindowSave save = FlyCycleOneAfterTheInWindowSave(multiStop: false);
            ReFlyStartFrom(save);

            Assert.Equal(2, Live().CompletedCycles);
            RouteOrchestrator.Tick(1550.0, env);
            Assert.True(Delivered("cycle-2", 0, 1550.0));
        }

        // catches: the go-back exit restoring the rewind save's counters over a row it does not hold.
        [Fact]
        public void GoBack_MultiStop_RowAfterTheSaveClock_CountersStayRebuilt()
        {
            InWindowSave save = FlyCycleOneAfterTheInWindowSave(multiStop: true);
            GoBackTo(save);

            Assert.Equal(2, Live().CompletedCycles);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("rewind save written inside an earlier go-back window")
                && l.Contains("1 route(s) keep rebuilt cycle counters"));

            RouteOrchestrator.Tick(1550.0, env);
            RouteOrchestrator.Tick(1700.0, env);
            Assert.False(Delivered("cycle-1", 1, 1700.0));
            Assert.True(Delivered("cycle-2", 1, 1700.0));
        }

        [Fact]
        public void GoBack_SingleStop_RowAfterTheSaveClock_TheCrossingStillDelivers()
        {
            InWindowSave save = FlyCycleOneAfterTheInWindowSave(multiStop: false);
            GoBackTo(save);

            Assert.Equal(2, Live().CompletedCycles);
            RouteOrchestrator.Tick(1550.0, env);
            Assert.True(Delivered("cycle-2", 0, 1550.0));
        }

        // catches: the guard ignoring the save's own log. Cycle 1's dispatch at 1550 lies after
        // the save clock but was written before the save (the earlier go-back kept it ahead of
        // the wound-back clock), so the save's counters count it and the multi-stop resume rule
        // stands: stop 1 resumes cycle-1's logged dispatch.
        [Fact]
        public void ReFlyStart_MultiStop_RowTheSaveHolds_ResumesItsLoggedDispatch()
        {
            Install(multiStop: true);
            RouteOrchestrator.Tick(1150.0, env);
            RouteOrchestrator.Tick(1300.0, env);
            RouteOrchestrator.Tick(1550.0, env);
            RouteStore.StateFloorUT = Floor;
            var save = new InWindowSave { Route = Snapshot(Live()), Events = RouteEventLog.Events.ToList() };
            RouteOrchestrator.Tick(1700.0, env);

            ReFlyStartFrom(save);

            Assert.Equal(1, Live().CompletedCycles);
            Assert.DoesNotContain(logLines, l => l.Contains("keep rebuilt cycle counters"));
            RouteOrchestrator.Tick(1700.0, env);
            Assert.True(Delivered("cycle-1", 1, 1700.0));
            Assert.Single(Rows(RouteEventType.RouteDispatched).Where(d => d.RouteCycleId == "cycle-1"));
            Assert.DoesNotContain(Rows(RouteEventType.RouteDispatched), d => d.RouteCycleId == "cycle-2");
        }

        // ------------------------------------------------------------------
        // The pure selector
        // ------------------------------------------------------------------

        private static RouteEvent Row(string route, string cycle, double ut)
            => new RouteEvent { Type = RouteEventType.RouteDispatched, UT = ut, RouteId = route, RouteCycleId = cycle };

        [Fact]
        public void Select_NamesOnlyRoutesWithARowTheSaveDoesNotHold()
        {
            RouteEvent held = Row("a", "cycle-1", 1550.0);
            var kept = new List<RouteEvent>
            {
                Row("a", "cycle-0", 1150.0),   // at or below the save clock
                held,                          // after it, held by the save
                Row("b", "cycle-3", 1555.0),   // after it, not held
            };
            HashSet<string> ids = RouteLoadReconcile.SelectRoutesWithUnheldRowsAfterSaveClock(
                kept, SaveClock, Floor, new List<RouteEvent> { Row("a", "cycle-1", 1550.0) });
            Assert.Equal(new[] { "b" }, ids.ToArray());
        }

        [Fact]
        public void Select_UnknownSavedLog_CountsEveryRowAfterTheClock()
        {
            var kept = new List<RouteEvent> { Row("a", "cycle-1", 1550.0) };
            Assert.Equal(new[] { "a" },
                RouteLoadReconcile.SelectRoutesWithUnheldRowsAfterSaveClock(kept, SaveClock, Floor, null).ToArray());
        }

        [Theory]
        [InlineData(1560.0, 1560.0)]     // cutoff at the save clock (no window)
        [InlineData(1560.0, 1500.0)]     // cutoff below it
        [InlineData(double.NaN, 1560.0)] // unknown save clock
        public void Select_NoWindow_NamesNothing(double saveClock, double cutoff)
        {
            var kept = new List<RouteEvent> { Row("a", "cycle-1", 1550.0) };
            Assert.Empty(RouteLoadReconcile.SelectRoutesWithUnheldRowsAfterSaveClock(kept, saveClock, cutoff, null));
        }
    }
}
