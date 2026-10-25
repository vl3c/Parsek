using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// ROUTE-LOAD-MID-CYCLE-MULTISTOP-REDISPATCH: a load back to a save written between a
    /// multi-stop loop route's windows (an F9, a go-back rewind, a Re-Fly start) rebuilt the
    /// cycle counters from the kept rows, which count the part-delivered cycle as completed, so
    /// the next window fired under a fresh cycle id with a second dispatch (a second origin
    /// debit for a vessel origin) instead of resuming the logged one. The loop-position restore
    /// now puts the save's own counters back with the cursors.
    /// Fixture: span [1000, 1400], cadence 400, stop 0 docks at 1150, stop 1 at 1300.
    /// </summary>
    [Collection("Sequential")]
    public class RouteMultiStopLoadResumeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteMultiStopLoadResumeTests()
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

        private static Route Build2StopRoute()
        {
            return new Route
            {
                Id = "route-multi", Name = "route-multi", Status = RouteStatus.Active, CreatedUT = 900.0,
                IsKscOrigin = true, BackingMissionTreeId = "tree-1", RecordedDockUT = 1300.0,
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
                    new RouteSourceRef { RecordingId = "rec-dock-b", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
        }

        private void InstallMulti()
        {
            RouteStore.AddRoute(Build2StopRoute());
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

        private static List<RouteEvent> Rows(RouteEventType t)
            => RouteEventLog.Events.Where(a => a.Type == t).ToList();

        private static Route Live() => RouteStore.CommittedRoutes.Single();

        // catches: the F9 half of the defect. The quicksave at 1200 holds cycle-0's dispatch
        // and stop 0's delivery; the second window must resume cycle-0 (no second dispatch).
        [Fact]
        public void QuickloadBetweenTheWindows_TheSecondWindowResumesTheLoggedDispatch()
        {
            InstallMulti();
            RouteOrchestrator.Tick(1150.0, env);
            ConfigNode at1200 = SaveNow();
            RouteOrchestrator.Tick(1350.0, env);
            Assert.Single(Rows(RouteEventType.RouteDispatched));

            RouteLoadReconcile.ReconcileAtInSessionLoad(LoadKind.QuickloadFlight, 1200.0, 1200.0, at1200);

            Assert.Equal(0, Live().CompletedCycles);
            Assert.Equal(0, Live().SkippedCycles);
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("cycle counters completed 1->0 skipped 0->0 (saved; next cycle id cycle-0)"));
            RouteOrchestrator.Tick(1350.0, env);

            var dispatched = Rows(RouteEventType.RouteDispatched);
            Assert.Single(dispatched);
            Assert.Equal("cycle-0", dispatched[0].RouteCycleId);
            var delivered = Rows(RouteEventType.RouteCargoDelivered);
            Assert.Equal(2, delivered.Count);
            Assert.All(delivered, d => Assert.Equal("cycle-0", d.RouteCycleId));
            Assert.Equal(1, Live().CompletedCycles);
        }

        // catches: the go-back half. The rewind save at 1160 lies between the windows; the
        // cutoff keeps cycle-0's dispatch and stop 0's row and retires stop 1's.
        [Fact]
        public void GoBackToARewindSaveBetweenTheWindows_TheSecondWindowResumes()
        {
            InstallMulti();
            RouteOrchestrator.Tick(1150.0, env);
            Route at1160 = Snapshot(Live());
            RouteOrchestrator.Tick(1350.0, env);

            RouteLoadReconcile.ReconcileAtGoBackRewind(1145.0, new[] { at1160 }, 1160.0);
            RouteOrchestrator.Tick(1350.0, env);

            var dispatched = Rows(RouteEventType.RouteDispatched);
            Assert.Single(dispatched);
            Assert.Equal("cycle-0", dispatched[0].RouteCycleId);
            Assert.Equal(new[] { 0, 1 },
                Rows(RouteEventType.RouteCargoDelivered).Select(d => d.RouteStopIndex).OrderBy(x => x).ToArray());
            Assert.All(Rows(RouteEventType.RouteCargoDelivered), d => Assert.Equal("cycle-0", d.RouteCycleId));
        }

        // catches: the Re-Fly start half (the bundle restore reads the RP quicksave's copy).
        [Fact]
        public void ReFlyStartFromAnRpQuicksaveBetweenTheWindows_TheSecondWindowResumes()
        {
            InstallMulti();
            RouteOrchestrator.Tick(1150.0, env);
            ConfigNode rpAt1200 = SaveNow();
            RouteOrchestrator.Tick(1350.0, env);
            ReconciliationBundle bundle = ReconciliationBundle.Capture();

            ReconciliationBundle.Restore(bundle, 1200.0, RouteStore.ReadSavedCommittedRoutes(rpAt1200), 1200.0);
            RouteOrchestrator.Tick(1350.0, env);

            var dispatched = Rows(RouteEventType.RouteDispatched);
            Assert.Single(dispatched);
            Assert.Equal("cycle-0", dispatched[0].RouteCycleId);
            Assert.All(Rows(RouteEventType.RouteCargoDelivered), d => Assert.Equal("cycle-0", d.RouteCycleId));
        }

        // The control: a load back past the whole cycle restores counters 0 and the cycle
        // fires once, under cycle-0.
        [Fact]
        public void QuickloadBeforeTheCycle_TheCycleFiresOnceUnderItsOwnId()
        {
            InstallMulti();
            ConfigNode at1100 = SaveNow();
            RouteOrchestrator.Tick(1150.0, env);
            RouteOrchestrator.Tick(1350.0, env);

            RouteLoadReconcile.ReconcileAtInSessionLoad(LoadKind.QuickloadFlight, 1100.0, 1100.0, at1100);
            Assert.Empty(RouteEventLog.Events);
            RouteOrchestrator.Tick(1150.0, env);
            RouteOrchestrator.Tick(1350.0, env);

            Assert.Single(Rows(RouteEventType.RouteDispatched));
            Assert.Equal(2, Rows(RouteEventType.RouteCargoDelivered).Count);
            Assert.Equal(1, Live().CompletedCycles);
        }

        // catches: the counters restored while the cursors stay reset. A clock definition
        // change after the save keeps the rebuild, the cursors' index space being gone.
        [Fact]
        public void ClockChangedAfterTheSave_CountersStayRebuilt()
        {
            InstallMulti();
            RouteOrchestrator.Tick(1150.0, env);
            ConfigNode at1200 = SaveNow();
            RouteOrchestrator.Tick(1350.0, env);
            Live().CadenceMultiplier = 2;

            RouteLoadReconcile.ReconcileAtInSessionLoad(LoadKind.QuickloadFlight, 1200.0, 1200.0, at1200);

            Assert.Equal(1, Live().CompletedCycles);
            Assert.Equal(-1, Live().Stops[0].LastFiredCycleIndex);
        }

        [Fact]
        public void RestoreCycleCountersFromSave_TakesTheSavedPair()
        {
            var kept = new Route { Id = "r", CompletedCycles = 3, SkippedCycles = 2 };
            var saved = new Route { Id = "r", CompletedCycles = 2, SkippedCycles = 2 };
            Assert.True(RouteLoadReconcile.RestoreCycleCountersFromSave(kept, saved));
            Assert.Equal(2, kept.CompletedCycles);
            Assert.Equal(2, kept.SkippedCycles);
            Assert.False(RouteLoadReconcile.RestoreCycleCountersFromSave(kept, saved));
            Assert.False(RouteLoadReconcile.RestoreCycleCountersFromSave(null, saved));
            Assert.False(RouteLoadReconcile.RestoreCycleCountersFromSave(kept, null));
        }
    }
}
