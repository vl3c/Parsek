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
    /// Seeded property test for a supply route against time warp and the Re-Fly load exits
    /// (todo ROUTE-TIMELINE-FUZZER; test-gap audit 2026-10-31 item 5, C1). A single-stop loop
    /// route delivering into a capped tank is driven through
    /// <see cref="RouteOrchestrator.Tick(double, IRouteRuntimeEnvironment)"/> by a random mix of
    /// frame steps (1x through very high warp), saves, Re-Fly starts
    /// (<see cref="ReconciliationBundle.Capture"/> / <c>Restore</c> at the save's UT, the world's
    /// tank handed back from that save) and Discard Re-fly loads
    /// (<see cref="RouteLoadReconcile.ReconcileAtInSessionLoad(LoadKind, double, double, IReadOnlyList{Route})"/>).
    ///
    /// <para>An independent model of the loop clock (closed form: dock instant of cycle k at
    /// anchor + k * cadence + (dock - spanStart)) predicts every fire. After every operation:
    /// the route's counters equal the model's (delivered and blocked), the tank equals the
    /// model's and never passes its capacity, the tank equals the start fill plus one manifest
    /// per delivered row in the route event log (cargo conserved), and no cycle id carries two
    /// dispatches or two deliveries. The model is the AS-BUILT rule of logistics design 10.7:
    /// one fire per tick for the newest owed dock cycle, so a tick that spans several dock
    /// instants fires once (collapse) and a tick shorter than the cadence never loses one.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteTimelineFuzzerTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // span [1000, 1300], cadence == span (300 s), dock 1150: the dock of cycle k is at
        // 1150 + 300 k.
        private const double Anchor = 1000.0;
        private const double Cadence = 300.0;
        private const double DockOffset = 150.0;
        private const double Manifest = 100.0;
        private const double Capacity = 1000.0;

        private readonly List<string> logLines = new List<string>();
        private double tank;

        public RouteTimelineFuzzerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResetStores();
        }

        public void Dispose()
        {
            ResetStores();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetStores()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RewindContext.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GroupHierarchyStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            MilestoneStore.ResetForTesting();
        }

        // ------------------------------------------------------------------
        // Fixture
        // ------------------------------------------------------------------

        private sealed class TankEnv : IRouteRuntimeEnvironment
        {
            private readonly RouteTimelineFuzzerTests owner;
            internal TankEnv(RouteTimelineFuzzerTests owner) { this.owner = owner; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall) { shortfall = 0.0; lackingResource = string.Empty; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource)
            {
                bool room = owner.tank + Manifest <= Capacity + 1e-9;
                fullResource = room ? string.Empty : "LiquidFuel";
                return room;
            }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        private void Install()
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: Anchor, spanEndUT: Anchor + Cadence,
                cadenceSeconds: Cadence, phaseAnchorUT: Anchor);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
            // ApplyDelivery's observable contract over the test's tank: the row, the counter,
            // and the cargo written into the destination.
            RouteOrchestrator.DeliveryApplierForTesting = (route, currentUT, env) =>
            {
                string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);
                RouteEventLog.Add(new RouteEvent
                {
                    Type = RouteEventType.RouteCargoDelivered,
                    UT = currentUT,
                    RouteId = route.Id,
                    RouteCycleId = cycleId,
                    RouteStopIndex = 0,
                    Sequence = 0,
                });
                tank += Manifest;
                route.CompletedCycles += 1;
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                route.TransitionTo(RouteStatus.Active, "delivered-fuzz");
            };
        }

        private static Route BuildRoute()
        {
            return new Route
            {
                Id = "route-fuzz",
                Name = "Fuzz Route",
                Status = RouteStatus.Active,
                CreatedUT = 900.0,
                IsKscOrigin = true,
                BackingMissionTreeId = "tree-fuzz",
                RecordedDockUT = Anchor + DockOffset,
                DockMemberRecordingId = "rec-fuzz",
                LoopAnchorUT = Anchor,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = Cadence,
                TransitDuration = Cadence,
                CostManifest = new Dictionary<string, double> { { "LiquidFuel", Manifest } },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", Manifest } },
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-fuzz", TreeId = "tree-fuzz", RouteProofHash = "deadbeef" },
                },
            };
        }

        private static Route Live() => RouteStore.CommittedRoutes.Single();

        private static Route SnapshotAsSaved(Route route)
        {
            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);
            Route copy = Route.DeserializeFrom(node);
            Assert.NotNull(copy);
            return copy;
        }

        // ------------------------------------------------------------------
        // The independent model
        // ------------------------------------------------------------------

        /// <summary>The newest cycle whose dock instant is at or before <paramref name="ut"/>,
        /// -1 before the first. Closed form of the loop clock, not a call into it.</summary>
        internal static long ModelDockCycle(double ut)
        {
            double t = ut - (Anchor + DockOffset);
            if (t < 0.0)
                return -1;
            return (long)Math.Floor(t / Cadence);
        }

        private sealed class ModelState
        {
            internal double UT;
            internal long LastFired = -1;
            internal int Delivered;
            internal int Blocked;
            internal double Tank;
            internal ModelState Clone() => (ModelState)MemberwiseClone();
        }

        private sealed class Save
        {
            internal ModelState Model;
            internal double Tank;
            internal List<Route> Routes;
        }

        private static void ModelTick(ModelState m, double ut)
        {
            m.UT = ut;
            long dock = ModelDockCycle(ut);
            if (dock <= m.LastFired)
                return;
            m.LastFired = dock;
            if (m.Tank + Manifest <= Capacity + 1e-9)
            {
                m.Tank += Manifest;
                m.Delivered++;
            }
            else
            {
                m.Blocked++;
            }
        }

        // ------------------------------------------------------------------
        // The invariants
        // ------------------------------------------------------------------

        private void AssertInvariants(ModelState m, double startTank, string trace)
        {
            Route r = Live();
            Assert.True(m.Delivered == r.CompletedCycles,
                $"completed {r.CompletedCycles} != model {m.Delivered}\n{trace}");
            Assert.True(m.Blocked == r.SkippedCycles,
                $"skipped {r.SkippedCycles} != model {m.Blocked}\n{trace}");
            Assert.True(m.LastFired == r.LastObservedLoopCycleIndex,
                $"cursor {r.LastObservedLoopCycleIndex} != model {m.LastFired}\n{trace}");
            Assert.True(Math.Abs(m.Tank - tank) < 1e-6, $"tank {tank} != model {m.Tank}\n{trace}");
            Assert.True(tank <= Capacity + 1e-6, $"tank {tank} over capacity\n{trace}");

            var delivered = RouteEventLog.Events
                .Where(e => e.Type == RouteEventType.RouteCargoDelivered).ToList();
            var dispatched = RouteEventLog.Events
                .Where(e => e.Type == RouteEventType.RouteDispatched).ToList();
            // Cargo conserved: every unit in the tank above its start fill is a delivered row.
            Assert.True(Math.Abs(startTank + Manifest * delivered.Count - tank) < 1e-6,
                $"tank {tank} != start {startTank} + {delivered.Count} deliveries\n{trace}");
            Assert.True(delivered.Count == r.CompletedCycles,
                $"{delivered.Count} delivered rows != completed {r.CompletedCycles}\n{trace}");
            Assert.True(dispatched.Count == delivered.Count,
                $"{dispatched.Count} dispatched rows != {delivered.Count} delivered\n{trace}");
            Assert.True(delivered.Select(e => e.RouteCycleId).Distinct().Count() == delivered.Count,
                $"a cycle id delivered twice\n{trace}");
            Assert.True(dispatched.Select(e => e.RouteCycleId).Distinct().Count() == dispatched.Count,
                $"a cycle id dispatched twice\n{trace}");
        }

        // ------------------------------------------------------------------
        // Deterministic cells: the warp rule itself
        // ------------------------------------------------------------------

        private List<double> Ticks(double from, double to, double step)
        {
            var list = new List<double>();
            for (double ut = from; ut < to; ut += step)
                list.Add(ut);
            list.Add(to);
            return list;
        }

        // catches: a cycle lost (or doubled) under warp that keeps each tick shorter than the
        // cadence, at 1x (1 s ticks), at 50x / 1000x frame steps, and at a step just under the
        // cadence. Ten dock instants (1150 .. 3850) crossed, ten deliveries, one per cycle id.
        [Theory]
        [InlineData(1.0)]
        [InlineData(20.0)]
        [InlineData(97.0)]
        [InlineData(299.0)]
        public void WarpShorterThanCadence_DeliversEveryCycleOnce(double step)
        {
            tank = 0.0;
            RouteStore.AddRoute(BuildRoute());
            Install();
            var env = new TankEnv(this);

            foreach (double ut in Ticks(Anchor, 3900.0, step))
                RouteOrchestrator.Tick(ut, env);

            Assert.Equal(10, Live().CompletedCycles);
            Assert.Equal(0, Live().SkippedCycles);
            Assert.Equal(1000.0, tank, 6);
            Assert.Equal(9, Live().LastObservedLoopCycleIndex);
            Assert.DoesNotContain(logLines, l => l.Contains("warp jump="));
        }

        // catches: the AS-BUILT collapse moving silently. A tick spanning several dock
        // instants fires ONCE for the newest (logistics design 10.7); 3000 s ticks over ten
        // dock instants fire on each tick that crossed at least one.
        [Fact]
        public void WarpLongerThanCadence_CollapsesToOneFirePerTick()
        {
            tank = 0.0;
            RouteStore.AddRoute(BuildRoute());
            Install();
            var env = new TankEnv(this);

            // Ticks at 1000, 4000, 7000: dock cycles crossed 0..9 at 4000 (one fire), 10..19 at
            // 7000 (one fire).
            foreach (double ut in new[] { 1000.0, 4000.0, 7000.0 })
                RouteOrchestrator.Tick(ut, env);

            Assert.Equal(2, Live().CompletedCycles);
            Assert.Equal(19, Live().LastObservedLoopCycleIndex);
            Assert.Equal(200.0, tank, 6);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("warp jump=10 "));
        }

        // catches: a full destination double-counting or losing its place: deliveries stop at
        // the capacity, every later crossing blocks once, and the cursor still advances.
        [Fact]
        public void FullDestination_BlocksEachLaterCrossingOnce()
        {
            tank = 700.0;
            RouteStore.AddRoute(BuildRoute());
            Install();
            var env = new TankEnv(this);

            foreach (double ut in Ticks(Anchor, 3900.0, 10.0))
                RouteOrchestrator.Tick(ut, env);

            Assert.Equal(3, Live().CompletedCycles);
            Assert.Equal(7, Live().SkippedCycles);
            Assert.Equal(1000.0, tank, 6);
            Assert.Equal(RouteStatus.Active, Live().Status);
        }

        // ------------------------------------------------------------------
        // The fuzzer
        // ------------------------------------------------------------------

        // catches: any sequence of warp steps, saves, Re-Fly starts and Discard Re-fly loads that
        // delivers a cycle twice, loses an owed cycle, creates or loses cargo, or leaves the
        // route's counters out of step with its rows.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        [InlineData(11)]
        [InlineData(12)]
        public void RandomTimeline_KeepsCargoAndCyclesInStep(int seed)
        {
            var rng = new Random(seed);
            double startTank = 100.0 * rng.Next(0, 4);
            tank = startTank;
            RouteStore.AddRoute(BuildRoute());
            Install();
            var env = new TankEnv(this);

            var model = new ModelState { UT = Anchor, Tank = startTank };
            RouteOrchestrator.Tick(Anchor, env);
            ModelTick(model, Anchor);

            var saves = new List<Save>();
            int loads = 0;
            int decided = 0;
            var trace = new System.Text.StringBuilder("seed=" + seed.ToString(IC));
            double[] steps = { 1.0, 1.0, 5.0, 20.0, 50.0, 200.0, 299.0, 450.0, 900.0, 2000.0 };

            for (int op = 0; op < 120; op++)
            {
                int pick = rng.Next(100);
                if (pick < 70)
                {
                    // A warp of 1..40 ticks at one step size (one WarpToUT at one rate).
                    double step = steps[rng.Next(steps.Length)];
                    int count = 1 + rng.Next(40);
                    trace.Append(" warp(").Append(step.ToString(IC)).Append('x')
                        .Append(count.ToString(IC)).Append(')');
                    for (int i = 0; i < count; i++)
                    {
                        double ut = model.UT + step;
                        RouteOrchestrator.Tick(ut, env);
                        int before = model.Delivered + model.Blocked;
                        ModelTick(model, ut);
                        decided += model.Delivered + model.Blocked - before;
                    }
                }
                else if (pick < 82 || saves.Count == 0)
                {
                    trace.Append(" save@").Append(model.UT.ToString("R", IC));
                    saves.Add(new Save
                    {
                        Model = model.Clone(),
                        Tank = tank,
                        Routes = new List<Route> { SnapshotAsSaved(Live()) },
                    });
                }
                else
                {
                    Save s = saves[rng.Next(saves.Count)];
                    bool reFly = pick < 91;
                    loads++;
                    trace.Append(reFly ? " reflyStart@" : " discardReFly@")
                        .Append(s.Model.UT.ToString("R", IC));
                    if (reFly)
                    {
                        ReconciliationBundle bundle = ReconciliationBundle.Capture();
                        ReconciliationBundle.Restore(bundle, s.Model.UT, s.Routes, s.Model.UT);
                    }
                    else
                    {
                        RouteLoadReconcile.ReconcileAtInSessionLoad(
                            LoadKind.DiscardReFly, s.Model.UT, s.Model.UT, s.Routes);
                    }
                    // The loaded world: the save's tank and clock.
                    tank = s.Tank;
                    model = s.Model.Clone();
                    // Saves written after the load target belong to the abandoned future.
                    saves.RemoveAll(x => x.Model.UT > s.Model.UT);
                    // The first tick after the load lands where the save stood.
                    RouteOrchestrator.Tick(model.UT, env);
                }

                AssertInvariants(model, startTank, trace.ToString());
            }

            // The run did something: deliveries were decided and load-backs were drawn (the
            // final state may sit on an early save, so count over the whole run).
            Assert.True(decided > 0, "no delivery decided: " + trace);
            Assert.True(loads > 0, "no load-back drawn: " + trace);
        }
    }
}
