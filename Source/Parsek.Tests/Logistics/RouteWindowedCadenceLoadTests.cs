using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// ROUTE-INTERACTION-SEAMS-TO-VERIFY, "the every-Nth-window anchor of an inter-body route
    /// resets on a rewind": the shared reconcile resets <see cref="Route.WindowAnchorCycleIndex"/>
    /// to -1 (whose rule is "the next crossing delivers and re-anchors"), and the loop-position
    /// restore puts the loaded save's anchor back when the clock definition matches. Pins that a
    /// load back to a save written between windows keeps the every-other-window cadence the save
    /// held, instead of delivering on the window the cadence skips.
    /// Fixture: synodic windows every 3000 s, dock phase at 1150 + 3000k, every 2nd window.
    /// </summary>
    [Collection("Sequential")]
    public class RouteWindowedCadenceLoadTests : IDisposable
    {
        private const double DockUT = 1150.0;
        private const double Synodic = 3000.0;
        private readonly List<string> logLines = new List<string>();

        public RouteWindowedCadenceLoadTests()
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
            RouteOrchestrator.ResetWindowBasisTransitionCountsForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.ResetWindowBasisTransitionCountsForTesting();
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

        private void InstallEveryOtherWindowRoute()
        {
            RouteStore.AddRoute(new Route
            {
                Id = "route-interbody", Name = "route-interbody", Status = RouteStatus.Active, CreatedUT = 900.0,
                IsKscOrigin = true, BackingMissionTreeId = "tree-1", RecordedDockUT = DockUT,
                DockMemberRecordingId = "rec-dock", LoopAnchorUT = 1000.0, LastObservedLoopCycleIndex = -1,
                WindowAnchorCycleIndex = -1, CadenceMultiplier = 2, DispatchInterval = 600.0, TransitDuration = 300.0,
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
                    new RouteSourceRef { RecordingId = "rec-dock", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            });
            GhostPlaybackLogic.LoopUnit unit = RouteWindowBasisTests.BuildReaimUnit(
                spanStartUT: 1000.0, spanEndUT: 1300.0, synodicCadence: Synodic, phaseAnchorUT: 1000.0);
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

        private static ConfigNode SaveNow()
        {
            var node = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(node);
            return node;
        }

        private static Route Snapshot(Route route)
        {
            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);
            return Route.DeserializeFrom(node);
        }

        private static Route Live() => RouteStore.CommittedRoutes.Single();

        private static double[] DeliveredUTs()
            => RouteEventLog.Events.Where(a => a.Type == RouteEventType.RouteCargoDelivered)
                .Select(a => a.UT).OrderBy(u => u).ToArray();

        // Flies window 0 (delivers, anchors at 0), window 1 (skipped by the cadence) and
        // window 2 (delivers) past a save written in the gap after window 0.
        private void FlyPastTheSave()
        {
            RouteOrchestrator.Tick(DockUT, env);
            Assert.Equal(0L, Live().WindowAnchorCycleIndex);
        }

        // catches: the F9 path. Without the anchor restore the reset anchor makes window 1, the
        // window the every-2nd cadence skips, deliver and re-anchor the cadence on odd windows.
        [Fact]
        public void QuickloadInTheGapAfterAWindow_KeepsTheEveryOtherWindowCadence()
        {
            InstallEveryOtherWindowRoute();
            FlyPastTheSave();
            ConfigNode at2000 = SaveNow();
            RouteOrchestrator.Tick(DockUT + Synodic, env);
            RouteOrchestrator.Tick(DockUT + 2 * Synodic, env);
            Assert.Equal(new[] { DockUT, DockUT + 2 * Synodic }, DeliveredUTs());

            RouteLoadReconcile.ReconcileAtInSessionLoad(LoadKind.QuickloadFlight, 2000.0, 2000.0, at2000);

            Assert.Equal(0L, Live().WindowAnchorCycleIndex);
            Assert.Equal(new[] { DockUT }, DeliveredUTs());
            RouteOrchestrator.Tick(DockUT + Synodic, env);
            Assert.Equal(new[] { DockUT }, DeliveredUTs());
            RouteOrchestrator.Tick(DockUT + 2 * Synodic, env);
            Assert.Equal(new[] { DockUT, DockUT + 2 * Synodic }, DeliveredUTs());
            Assert.Equal(0L, Live().WindowAnchorCycleIndex);
        }

        // catches: the go-back path (the rewind save's own route copy restores the anchor).
        [Fact]
        public void GoBackToARewindSaveInTheGap_KeepsTheEveryOtherWindowCadence()
        {
            InstallEveryOtherWindowRoute();
            FlyPastTheSave();
            Route at2000 = Snapshot(Live());
            RouteOrchestrator.Tick(DockUT + Synodic, env);
            RouteOrchestrator.Tick(DockUT + 2 * Synodic, env);

            RouteLoadReconcile.ReconcileAtGoBackRewind(1990.0, new[] { at2000 }, 2000.0);

            Assert.Equal(0L, Live().WindowAnchorCycleIndex);
            RouteOrchestrator.Tick(DockUT + Synodic, env);
            RouteOrchestrator.Tick(DockUT + 2 * Synodic, env);
            Assert.Equal(new[] { DockUT, DockUT + 2 * Synodic }, DeliveredUTs());
        }

        // The case where the reset stands: the clock changed after the save (a cadence change is
        // a rebase, design 19.4), so the next crossing delivers and re-anchors, as it would on
        // the live path.
        [Fact]
        public void CadenceChangedAfterTheSave_TheNextCrossingReanchors()
        {
            InstallEveryOtherWindowRoute();
            FlyPastTheSave();
            Route at2000 = Snapshot(Live());
            Live().CadenceMultiplier = 3;
            Live().DispatchInterval = 900.0;

            RouteLoadReconcile.ReconcileAtGoBackRewind(1990.0, new[] { at2000 }, 2000.0);

            Assert.Equal(-1L, Live().WindowAnchorCycleIndex);
            Assert.Contains(logLines, l => l.Contains("clock definition changed after the save"));
        }
    }
}
