using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Hub and spoke: two CHAINED supply routes (test-gap list 2026-11-18, Batch 2 item 6).
    /// Route A runs from the Space Center into an unloaded depot; route B draws its cargo from
    /// that same depot (a vessel origin) and delivers it into an unloaded outpost tank. Both
    /// physical halves the depot sees are production code: A's delivery is the real
    /// <c>ApplyDelivery</c> (no delivery seam: <c>LiveDeliveryCapacityProbe</c> and
    /// <c>LiveDeliveryWriters</c> over the depot's proto snapshot) and B's dispatch is the real
    /// unloaded origin debit; the environment's two gates use the same production probes.
    ///
    /// <para>An independent model runs both loop clocks (closed form, as the single-route
    /// fuzzer) against one shared depot in the as-built tick order (lower
    /// <c>DispatchPriority</c> first, design D8). After every operation: each route's counters
    /// and cursor equal the model's; the depot and the tank equal the model's and stay inside
    /// [0, capacity]; cargo is conserved across the chain (depot + tank above their start fill
    /// is exactly what A delivered; what B debited is what B delivered); and, walking the route
    /// event log in order, the depot's running balance never goes negative, so B never draws
    /// cargo A has not delivered by that UT. A replayed seed reproduces the same event log and
    /// the same world byte for byte.</para>
    /// </summary>
    public partial class RouteTimelineFuzzerTests
    {
        // Route A: dock of cycle k at 1150 + 300 k, 100 units per cycle.
        // Route B: dock of cycle k at 1200 + 450 k, 150 units per cycle.
        // 300 a - 450 b = 50 has no integer solution, so no two docks share an instant; a tick
        // crossing both docks is the same-UT case the tick order decides.
        private const double ChainCadenceA = 300.0;
        private const double ChainDockOffsetA = 150.0;
        private const double ChainManifestA = 100.0;
        private const double ChainCadenceB = 450.0;
        private const double ChainDockOffsetB = 200.0;
        private const double ChainManifestB = 150.0;
        private const double DepotCapacity = 600.0;
        private const double OutpostCapacity = 1500.0;

        private const string ChainRouteA = "route-chain-a";
        private const string ChainRouteB = "route-chain-b";
        private const uint DepotPid = 9200u;
        private const uint OutpostPid = 9300u;

        private Vessel depotVessel;
        private ProtoPartResourceSnapshot depotTank;
        private Vessel outpostVessel;
        private ProtoPartResourceSnapshot outpostTank;

        // ------------------------------------------------------------------
        // Fixture
        // ------------------------------------------------------------------

        /// <summary>The chain's environment: endpoints resolve to the two unloaded vessels;
        /// the origin gate and the destination gate run the production probes and the
        /// production all-or-nothing capacity check over them.</summary>
        private sealed class ChainEnv : IRouteRuntimeEnvironment
        {
            private readonly RouteTimelineFuzzerTests owner;
            internal ChainEnv(RouteTimelineFuzzerTests owner) { this.owner = owner; }

            private Vessel Resolve(RouteEndpoint endpoint)
            {
                if (endpoint.VesselPersistentId == DepotPid) return owner.depotVessel;
                if (endpoint.VesselPersistentId == OutpostPid) return owner.outpostVessel;
                return null;
            }

            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            {
                bool ok = Resolve(endpoint) != null;
                reason = ok ? string.Empty : "unknown-pid";
                return ok;
            }

            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            {
                vessel = Resolve(endpoint);
                reason = vessel != null ? string.Empty : "unknown-pid";
                return vessel != null;
            }

            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall)
            {
                lackingResource = string.Empty;
                shortfall = 0.0;
                if (route.IsKscOrigin)
                    return true;
                Vessel origin = Resolve(route.Origin);
                var probe = new LiveOriginCargoProbe(origin, isLoaded: false);
                bool covered = RoutePickupSourceGate.OriginCoversNetOfOtherRoutes(
                    route.CostManifest, probe.ProbeResourceStored,
                    name => RouteStore.OtherRoutesReservedFor(route.Id, origin.persistentId, name),
                    out string shortResource, out double resourceShortfall,
                    out bool _, out double _, out double _);
                if (!covered)
                {
                    lackingResource = shortResource;
                    shortfall = resourceShortfall;
                }
                return covered;
            }

            public bool DestinationHasCapacity(Route route, out string fullResource)
            {
                bool fits = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                    route,
                    stopIndex => new LiveDeliveryCapacityProbe(
                        Resolve(route.Stops[stopIndex].Endpoint), isLoaded: false),
                    out string fullToken, out int _);
                fullResource = fits ? string.Empty : fullToken;
                return fits;
            }

            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        /// <summary>An unloaded vessel with one open LiquidFuel tank, alive to Unity's ==
        /// without native code (the same construction as the single-route depot).</summary>
        private static Vessel BuildUnloadedTankVessel(
            uint pid, string name, double amount, double capacity, out ProtoPartResourceSnapshot tankSnapshot)
        {
            tankSnapshot = (ProtoPartResourceSnapshot)FormatterServices.GetUninitializedObject(
                typeof(ProtoPartResourceSnapshot));
            tankSnapshot.resourceName = "LiquidFuel";
            tankSnapshot.amount = amount;
            tankSnapshot.maxAmount = capacity;
            tankSnapshot.flowState = true;
            var part = (ProtoPartSnapshot)FormatterServices.GetUninitializedObject(typeof(ProtoPartSnapshot));
            part.resources = new List<ProtoPartResourceSnapshot> { tankSnapshot };
            var pv = (ProtoVessel)FormatterServices.GetUninitializedObject(typeof(ProtoVessel));
            pv.protoPartSnapshots = new List<ProtoPartSnapshot> { part };
            var vessel = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(vessel, new IntPtr(1));
            vessel.persistentId = pid;
            vessel.vesselName = name;
            vessel.loaded = false;
            vessel.packed = true;
            vessel.protoVessel = pv;
            return vessel;
        }

        private void InstallChainWorld(double depotFill, double outpostFill)
        {
            depotVessel = BuildUnloadedTankVessel(DepotPid, "Chain Depot", depotFill, DepotCapacity, out depotTank);
            outpostVessel = BuildUnloadedTankVessel(OutpostPid, "Chain Outpost", outpostFill, OutpostCapacity, out outpostTank);
        }

        private static Route BuildChainRoute(
            string id, double cadence, double dockOffset, double manifest,
            RouteEndpoint? origin, uint destinationPid, int priority)
        {
            return new Route
            {
                Id = id,
                Name = id,
                Status = RouteStatus.Active,
                CreatedUT = 900.0,
                IsKscOrigin = !origin.HasValue,
                Origin = origin ?? default(RouteEndpoint),
                BackingMissionTreeId = "tree-" + id,
                RecordedDockUT = Anchor + dockOffset,
                DockMemberRecordingId = "rec-" + id,
                LoopAnchorUT = Anchor,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = cadence,
                TransitDuration = cadence,
                DispatchPriority = priority,
                CostManifest = new Dictionary<string, double> { { "LiquidFuel", manifest } },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = destinationPid, RootPartUId = 1u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", manifest } },
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-" + id, TreeId = "tree-" + id, RouteProofHash = "deadbeef" },
                },
            };
        }

        /// <summary>Installs both routes and the loop clocks. <paramref name="aFirst"/> picks
        /// which route the tick runs first (the lower DispatchPriority).</summary>
        private void InstallChain(bool aFirst)
        {
            RouteStore.AddRoute(BuildChainRoute(
                ChainRouteA, ChainCadenceA, ChainDockOffsetA, ChainManifestA,
                origin: null, destinationPid: DepotPid, priority: aFirst ? 0 : 1));
            RouteStore.AddRoute(BuildChainRoute(
                ChainRouteB, ChainCadenceB, ChainDockOffsetB, ChainManifestB,
                origin: new RouteEndpoint { VesselPersistentId = DepotPid, RootPartUId = 1u },
                destinationPid: OutpostPid, priority: aFirst ? 1 : 0));

            var unitA = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: Anchor, spanEndUT: Anchor + ChainCadenceA,
                cadenceSeconds: ChainCadenceA, phaseAnchorUT: Anchor);
            var unitB = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 1, memberIndices: new[] { 1 },
                spanStartUT: Anchor, spanEndUT: Anchor + ChainCadenceB,
                cadenceSeconds: ChainCadenceB, phaseAnchorUT: Anchor);
            RouteOrchestrator.LoopUnitResolverForTesting =
                (r, ut) => r.Id == ChainRouteA ? unitA : unitB;
            // No delivery or debit seam: both halves run the production writers.
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
        }

        private static Route LiveRoute(string id) => RouteStore.CommittedRoutes.Single(r => r.Id == id);

        // ------------------------------------------------------------------
        // The independent model
        // ------------------------------------------------------------------

        private static long ChainDockCycle(double ut, double dockOffset, double cadence)
        {
            double t = ut - (Anchor + dockOffset);
            if (t < 0.0)
                return -1;
            return (long)Math.Floor(t / cadence);
        }

        private sealed class ChainModel
        {
            internal double UT;
            internal bool AFirst;
            internal long LastA = -1;
            internal long LastB = -1;
            internal int DeliveredA;
            internal int BlockedA;
            internal int DeliveredB;
            internal int BlockedB;
            internal double Depot;
            internal double Outpost;
            internal ChainModel Clone() => (ChainModel)MemberwiseClone();
        }

        private sealed class ChainSave
        {
            internal ChainModel Model;
            internal double Depot;
            internal double Outpost;
            internal List<Route> Routes;
        }

        private static void ChainModelTickA(ChainModel m, double ut)
        {
            long dock = ChainDockCycle(ut, ChainDockOffsetA, ChainCadenceA);
            if (dock <= m.LastA)
                return;
            m.LastA = dock;
            if (m.Depot + ChainManifestA <= DepotCapacity + 1e-9)
            {
                m.Depot += ChainManifestA;
                m.DeliveredA++;
            }
            else
            {
                m.BlockedA++;
            }
        }

        private static void ChainModelTickB(ChainModel m, double ut)
        {
            long dock = ChainDockCycle(ut, ChainDockOffsetB, ChainCadenceB);
            if (dock <= m.LastB)
                return;
            m.LastB = dock;
            if (m.Depot >= ChainManifestB - 1e-9 && m.Outpost + ChainManifestB <= OutpostCapacity + 1e-9)
            {
                m.Depot -= ChainManifestB;
                m.Outpost += ChainManifestB;
                m.DeliveredB++;
            }
            else
            {
                m.BlockedB++;
            }
        }

        private static void ChainModelTick(ChainModel m, double ut)
        {
            m.UT = ut;
            if (m.AFirst)
            {
                ChainModelTickA(m, ut);
                ChainModelTickB(m, ut);
            }
            else
            {
                ChainModelTickB(m, ut);
                ChainModelTickA(m, ut);
            }
        }

        // ------------------------------------------------------------------
        // The invariants
        // ------------------------------------------------------------------

        private static double LiquidFuelOf(RouteEvent e) =>
            e.RouteResourceManifest != null && e.RouteResourceManifest.TryGetValue("LiquidFuel", out double v) ? v : 0.0;

        private void AssertChainInvariants(ChainModel m, double depot0, double outpost0, string trace)
        {
            Route a = LiveRoute(ChainRouteA);
            Route b = LiveRoute(ChainRouteB);
            Assert.True(a.CompletedCycles == m.DeliveredA && a.SkippedCycles == m.BlockedA,
                $"A completed/skipped {a.CompletedCycles}/{a.SkippedCycles} != model {m.DeliveredA}/{m.BlockedA}\n{trace}");
            Assert.True(b.CompletedCycles == m.DeliveredB && b.SkippedCycles == m.BlockedB,
                $"B completed/skipped {b.CompletedCycles}/{b.SkippedCycles} != model {m.DeliveredB}/{m.BlockedB}\n{trace}");
            Assert.True(a.LastObservedLoopCycleIndex == m.LastA && b.LastObservedLoopCycleIndex == m.LastB,
                $"cursors A={a.LastObservedLoopCycleIndex} B={b.LastObservedLoopCycleIndex} != model {m.LastA}/{m.LastB}\n{trace}");
            Assert.True(Math.Abs(depotTank.amount - m.Depot) < 1e-6,
                $"depot {depotTank.amount} != model {m.Depot}\n{trace}");
            Assert.True(Math.Abs(outpostTank.amount - m.Outpost) < 1e-6,
                $"outpost {outpostTank.amount} != model {m.Outpost}\n{trace}");
            Assert.True(depotTank.amount >= -1e-9 && depotTank.amount <= DepotCapacity + 1e-6,
                $"depot {depotTank.amount} outside [0, {DepotCapacity}]\n{trace}");
            Assert.True(outpostTank.amount <= OutpostCapacity + 1e-6,
                $"outpost {outpostTank.amount} over capacity\n{trace}");

            var events = RouteEventLog.Events;
            List<RouteEvent> Rows(string routeId, RouteEventType type) =>
                events.Where(e => e != null && e.RouteId == routeId && e.Type == type).ToList();
            var deliveredA = Rows(ChainRouteA, RouteEventType.RouteCargoDelivered);
            var deliveredB = Rows(ChainRouteB, RouteEventType.RouteCargoDelivered);
            var debitedB = Rows(ChainRouteB, RouteEventType.RouteCargoDebited);
            var dispatchedA = Rows(ChainRouteA, RouteEventType.RouteDispatched);
            var dispatchedB = Rows(ChainRouteB, RouteEventType.RouteDispatched);

            Assert.True(deliveredA.Count == a.CompletedCycles && dispatchedA.Count == a.CompletedCycles,
                $"A rows delivered={deliveredA.Count} dispatched={dispatchedA.Count} != completed {a.CompletedCycles}\n{trace}");
            Assert.True(deliveredB.Count == b.CompletedCycles && dispatchedB.Count == b.CompletedCycles
                && debitedB.Count == b.CompletedCycles,
                $"B rows delivered={deliveredB.Count} dispatched={dispatchedB.Count} debited={debitedB.Count} " +
                $"!= completed {b.CompletedCycles}\n{trace}");
            Assert.True(Rows(ChainRouteA, RouteEventType.RouteCargoDebited).Count == 0,
                $"the Space Center leg wrote a debit row\n{trace}");
            foreach (var rows in new[] { deliveredA, deliveredB, debitedB, dispatchedA, dispatchedB })
            {
                Assert.True(rows.Select(e => e.RouteCycleId).Distinct().Count() == rows.Count,
                    $"a cycle id written twice on one route\n{trace}");
            }
            Assert.True(debitedB.All(e => e.RouteRequestedResourceManifest == null && e.RouteOriginVesselPid == DepotPid),
                $"a B debit recorded a shortfall or the wrong origin\n{trace}");

            // Conservation across the chain: the Space Center is the only source.
            double inA = deliveredA.Sum(LiquidFuelOf);
            double outB = debitedB.Sum(LiquidFuelOf);
            double inB = deliveredB.Sum(LiquidFuelOf);
            Assert.True(Math.Abs(inA - ChainManifestA * deliveredA.Count) < 1e-6,
                $"A delivered {inA} != {deliveredA.Count} full manifests\n{trace}");
            Assert.True(Math.Abs(outB - inB) < 1e-6, $"B debited {outB} != B delivered {inB}\n{trace}");
            Assert.True(Math.Abs(depot0 + inA - outB - depotTank.amount) < 1e-6,
                $"depot {depotTank.amount} != start {depot0} + in {inA} - out {outB}\n{trace}");
            Assert.True(Math.Abs(outpost0 + inB - outpostTank.amount) < 1e-6,
                $"outpost {outpostTank.amount} != start {outpost0} + in {inB}\n{trace}");
            Assert.True(Math.Abs(depotTank.amount + outpostTank.amount - (depot0 + outpost0 + inA)) < 1e-6,
                $"chain created or lost cargo: depot+outpost {depotTank.amount + outpostTank.amount} " +
                $"!= start {depot0 + outpost0} + supplied {inA}\n{trace}");

            // Ordering: in log order (which is UT order) the depot's running balance never goes
            // negative and never passes capacity, so every B debit at UT t is covered by the
            // start fill plus A deliveries logged at or before t.
            double balance = depot0;
            double lastUT = double.NegativeInfinity;
            for (int i = 0; i < events.Count; i++)
            {
                RouteEvent e = events[i];
                if (e == null) continue;
                Assert.True(e.UT >= lastUT, $"route event log out of UT order at row {i} ut={e.UT} after {lastUT}\n{trace}");
                lastUT = e.UT;
                if (e.RouteId == ChainRouteA && e.Type == RouteEventType.RouteCargoDelivered)
                    balance += LiquidFuelOf(e);
                else if (e.RouteId == ChainRouteB && e.Type == RouteEventType.RouteCargoDebited)
                    balance -= LiquidFuelOf(e);
                Assert.True(balance >= -1e-9,
                    $"B drew cargo A had not delivered: depot balance {balance} after row {i} " +
                    $"({e.Type} {e.RouteId} {e.RouteCycleId} ut={e.UT})\n{trace}");
                Assert.True(balance <= DepotCapacity + 1e-6,
                    $"depot balance {balance} over capacity after row {i}\n{trace}");
            }
        }

        // ------------------------------------------------------------------
        // Deterministic cells: the same-tick order rule
        // ------------------------------------------------------------------

        // catches: the tick order deciding the wrong way when the depot holds 50: A first lets B
        // draw the 150 (50 + A's 100 delivered earlier in this tick); B first must block, because
        // at B's turn the depot still holds 50.
        [Theory]
        [InlineData(true, 1, 0.0, 150.0)]
        [InlineData(false, 0, 150.0, 0.0)]
        public void Chained_SameTickDraw_SeesOnlyWhatAlreadyArrived(
            bool aFirst, int expectedB, double expectedDepot, double expectedOutpost)
        {
            InstallChainWorld(depotFill: 50.0, outpostFill: 0.0);
            InstallChain(aFirst);
            var env = new ChainEnv(this);

            RouteOrchestrator.Tick(Anchor, env);
            RouteOrchestrator.Tick(1250.0, env);

            Assert.Equal(1, LiveRoute(ChainRouteA).CompletedCycles);
            Assert.Equal(expectedB, LiveRoute(ChainRouteB).CompletedCycles);
            Assert.Equal(1 - expectedB, LiveRoute(ChainRouteB).SkippedCycles);
            Assert.Equal(expectedDepot, depotTank.amount, 6);
            Assert.Equal(expectedOutpost, outpostTank.amount, 6);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("Delivery endpoint resolved:")
                && l.Contains("pid=" + DepotPid) && l.Contains("path=unloaded"));
            if (expectedB == 1)
            {
                Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("OriginDebit plan:")
                    && l.Contains("path=unloaded"));
            }
        }

        // catches: a hub that drains below what its spoke needs never recovering, or a spoke
        // that keeps dispatching from an empty hub: over 3000 s at 1x, A supplies 1000 and B
        // takes 150 per dock while the hub holds it.
        [Fact]
        public void Chained_SteadyRun_HubFeedsSpokeAndConserves()
        {
            InstallChainWorld(depotFill: 0.0, outpostFill: 0.0);
            InstallChain(aFirst: true);
            var env = new ChainEnv(this);
            var model = new ChainModel { UT = Anchor, AFirst = true };

            foreach (double ut in Ticks(Anchor, 4000.0, 1.0))
            {
                RouteOrchestrator.Tick(ut, env);
                ChainModelTick(model, ut);
            }

            AssertChainInvariants(model, 0.0, 0.0, "steady-run");
            Assert.True(LiveRoute(ChainRouteB).CompletedCycles > 0, "the spoke never delivered");
            Assert.True(LiveRoute(ChainRouteB).SkippedCycles > 0, "the spoke never waited on the hub");
        }

        // ------------------------------------------------------------------
        // The chained fuzzer
        // ------------------------------------------------------------------

        // catches: any sequence of warps, saves, Re-Fly starts and Discard Re-fly loads that lets
        // the spoke draw cargo the hub has not received yet, creates or loses cargo anywhere in
        // the chain, delivers or debits a cycle twice, or leaves either route's counters out of
        // step with its rows.
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
        public void RandomTimeline_ChainedRoutes_ConserveAndOrderCargo(int seed)
        {
            RunChainedTimeline(seed);
        }

        // catches: hidden nondeterminism in the two-route tick (dictionary or hash order, a
        // clock read, an unsorted walk): the same seed replayed from a clean state writes the
        // same route event log and leaves the same world.
        [Theory]
        [InlineData(3)]
        [InlineData(8)]
        public void RandomTimeline_ChainedRoutes_SameSeedReplaysIdentically(int seed)
        {
            string first = RunChainedTimeline(seed);
            ResetStores();
            RouteEventLog.ResetForTesting();
            logLines.Clear();
            string second = RunChainedTimeline(seed);
            Assert.Equal(first, second);
        }

        /// <summary>Runs one seeded chained timeline, asserting the invariants after every
        /// operation, and returns a fingerprint of the final route event log and world.</summary>
        private string RunChainedTimeline(int seed)
        {
            var rng = new Random(seed);
            bool aFirst = rng.Next(2) == 0;
            double depot0 = 50.0 * rng.Next(0, 13);
            double outpost0 = 150.0 * rng.Next(0, 6);
            InstallChainWorld(depot0, outpost0);
            InstallChain(aFirst);
            var env = new ChainEnv(this);

            var model = new ChainModel { UT = Anchor, AFirst = aFirst, Depot = depot0, Outpost = outpost0 };
            RouteOrchestrator.Tick(Anchor, env);
            ChainModelTick(model, Anchor);

            var saves = new List<ChainSave>();
            int loads = 0;
            int deliveredB = 0;
            var trace = new StringBuilder("seed=" + seed.ToString(IC) + " aFirst=" + (aFirst ? "1" : "0")
                + " depot0=" + depot0.ToString(IC) + " outpost0=" + outpost0.ToString(IC));
            double[] steps = { 1.0, 1.0, 5.0, 20.0, 50.0, 200.0, 299.0, 450.0, 900.0, 2000.0 };

            for (int op = 0; op < 120; op++)
            {
                int pick = rng.Next(100);
                if (pick < 70)
                {
                    double step = steps[rng.Next(steps.Length)];
                    int count = 1 + rng.Next(40);
                    trace.Append(" warp(").Append(step.ToString(IC)).Append('x')
                        .Append(count.ToString(IC)).Append(')');
                    for (int i = 0; i < count; i++)
                    {
                        double ut = model.UT + step;
                        RouteOrchestrator.Tick(ut, env);
                        int before = model.DeliveredB;
                        ChainModelTick(model, ut);
                        deliveredB += model.DeliveredB - before;
                    }
                }
                else if (pick < 82 || saves.Count == 0)
                {
                    trace.Append(" save@").Append(model.UT.ToString("R", IC));
                    saves.Add(new ChainSave
                    {
                        Model = model.Clone(),
                        Depot = depotTank.amount,
                        Outpost = outpostTank.amount,
                        Routes = new List<Route>
                        {
                            SnapshotAsSaved(LiveRoute(ChainRouteA)),
                            SnapshotAsSaved(LiveRoute(ChainRouteB)),
                        },
                    });
                }
                else
                {
                    ChainSave s = saves[rng.Next(saves.Count)];
                    bool reFly = pick < 91;
                    loads++;
                    trace.Append(reFly ? " reflyStart@" : " discardReFly@")
                        .Append(s.Model.UT.ToString("R", IC));
                    var savedRoutes = s.Routes.Select(SnapshotAsSaved).ToList();
                    if (reFly)
                    {
                        ReconciliationBundle bundle = ReconciliationBundle.Capture();
                        ReconciliationBundle.Restore(bundle, s.Model.UT, savedRoutes, s.Model.UT);
                    }
                    else
                    {
                        RouteLoadReconcile.ReconcileAtInSessionLoad(
                            LoadKind.DiscardReFly, s.Model.UT, s.Model.UT, savedRoutes);
                    }
                    // The loaded world: both vessels' tanks and the clock as saved.
                    depotTank.amount = s.Depot;
                    outpostTank.amount = s.Outpost;
                    model = s.Model.Clone();
                    saves.RemoveAll(x => x.Model.UT > s.Model.UT);
                    RouteOrchestrator.Tick(model.UT, env);
                }

                AssertChainInvariants(model, depot0, outpost0, trace.ToString());
            }

            Assert.True(deliveredB > 0, "the spoke never delivered: " + trace);
            Assert.True(loads > 0, "no load-back drawn: " + trace);

            var fingerprint = new StringBuilder();
            foreach (RouteEvent e in RouteEventLog.Events)
            {
                if (e == null) continue;
                fingerprint.Append(e.Type).Append('|').Append(e.RouteId).Append('|')
                    .Append(e.RouteCycleId).Append('|').Append(e.UT.ToString("R", IC)).Append('|')
                    .Append(e.Sequence.ToString(IC)).Append('|')
                    .Append(LiquidFuelOf(e).ToString("R", IC)).Append('\n');
            }
            fingerprint.Append("depot=").Append(depotTank.amount.ToString("R", IC))
                .Append(" outpost=").Append(outpostTank.amount.ToString("R", IC));
            return fingerprint.ToString();
        }
    }
}
