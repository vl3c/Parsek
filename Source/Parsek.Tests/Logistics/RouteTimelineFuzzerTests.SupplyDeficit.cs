using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// A route timeline that runs SHORT of supply (todo ROUTE-TICK-EVENT-LOG-SCANS review
    /// follow-up: no behaviour test reached the supply-deficit read of the real delivery, so
    /// reading the deficit from the wrong cycle's rows only redded a source check).
    ///
    /// <para>A vessel-origin loop route whose origin debit comes up short on some cycles (the
    /// debit seam returns a per-dock-cycle fraction of the cost manifest with the full manifest
    /// as requested-on-shortfall, the docked-origin / mid-tick-drift shape) delivers through the
    /// PRODUCTION <c>ApplyDelivery</c> into an unloaded outpost tank, so the delivery's cap is
    /// <c>ComputeCycleSupplyDeficit</c> over <c>RouteEventLog.RowsForCycle</c> of its own cycle.
    /// The timeline mixes warps, saves, Re-Fly starts and Discard Re-fly loads.</para>
    ///
    /// <para>An independent model (the loop clock in closed form, the outpost's all-or-nothing
    /// capacity gate, one fraction per dock cycle) predicts every cycle. After every operation:
    /// the outpost and the counters equal the model's, and every delivered row equals (a) its
    /// own cycle's debited amount (a full-scan join on the cycle id), (b) the recorded manifest
    /// less the deficit a full scan of the whole log computes for that cycle, and (c) the
    /// model's fraction for the dock cycle its UT falls in.</para>
    /// </summary>
    public partial class RouteTimelineFuzzerTests
    {
        private const string DeficitRouteId = "route-deficit";
        private const double DeficitOutpostCapacity = 1000.0;

        // Per dock cycle, the fraction of the cost manifest the origin debit actually takes.
        // Neighbouring cycles differ, so a delivery capped by another cycle's rows disagrees.
        private static readonly double[] DeficitFractions = { 1.0, 0.25, 1.0, 0.5, 0.9, 0.75, 1.0, 0.4 };

        private int deficitSeed;

        private double DeficitFractionFor(long dockCycle)
        {
            long k = Math.Max(0L, dockCycle);
            return DeficitFractions[(int)((k * 3 + deficitSeed) % DeficitFractions.Length)];
        }

        private void InstallDeficitRoute(int seed, double outpostFill)
        {
            deficitSeed = seed;
            depotVessel = BuildUnloadedTankVessel(DepotPid, "Deficit Depot", 1.0e6, 1.0e7, out depotTank);
            outpostVessel = BuildUnloadedTankVessel(
                OutpostPid, "Deficit Outpost", outpostFill, DeficitOutpostCapacity, out outpostTank);
            RouteStore.AddRoute(BuildChainRoute(
                DeficitRouteId, Cadence, DockOffset, Manifest,
                origin: new RouteEndpoint { VesselPersistentId = DepotPid, RootPartUId = 1u },
                destinationPid: OutpostPid, priority: 0));
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: Anchor, spanEndUT: Anchor + Cadence,
                cadenceSeconds: Cadence, phaseAnchorUT: Anchor);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            // The origin debit comes up short by the dock cycle's fraction (the depot itself is
            // not touched: the gate saw enough, the apply did not get it, as a docked origin can).
            RouteOrchestrator.OriginDebitApplierForTesting = (route, ut, env) =>
            {
                double actual = Manifest * DeficitFractionFor(ModelDockCycle(ut));
                bool isShort = actual < Manifest - 1e-9;
                return new RouteOrchestrator.OriginDebitOutcome
                {
                    ActualDebited = new Dictionary<string, double>(StringComparer.Ordinal) { { "LiquidFuel", actual } },
                    RequestedOnShortfall = isShort
                        ? new Dictionary<string, double>(StringComparer.Ordinal) { { "LiquidFuel", Manifest } }
                        : null,
                    OriginVesselPid = DepotPid,
                    Short = isShort,
                    Unresolved = false,
                };
            };
        }

        private sealed class DeficitModel
        {
            internal double UT;
            internal long LastFired = -1;
            internal int Delivered;
            internal int Blocked;
            internal int ShortDelivered;
            internal double Outpost;
            internal DeficitModel Clone() => (DeficitModel)MemberwiseClone();
        }

        private sealed class DeficitSave
        {
            internal DeficitModel Model;
            internal double Outpost;
            internal List<Route> Routes;
        }

        private void DeficitModelTick(DeficitModel m, double ut)
        {
            m.UT = ut;
            long dock = ModelDockCycle(ut);
            if (dock <= m.LastFired)
                return;
            m.LastFired = dock;
            // The destination gate is all-or-nothing on the RECORDED manifest.
            if (m.Outpost + Manifest > DeficitOutpostCapacity + 1e-9)
            {
                m.Blocked++;
                return;
            }
            double got = Manifest * DeficitFractionFor(dock);
            m.Outpost += got;
            m.Delivered++;
            if (got < Manifest - 1e-9)
                m.ShortDelivered++;
        }

        private static double LiquidFuelIn(Dictionary<string, double> manifest) =>
            manifest != null && manifest.TryGetValue("LiquidFuel", out double v) ? v : 0.0;

        private void AssertDeficitInvariants(DeficitModel m, double outpost0, string trace)
        {
            Assert.True(RouteEventLog.IndexMatchesFullScanForTesting(out string indexMismatch),
                $"route event log index out of step: {indexMismatch}\n{trace}");
            Route r = LiveRoute(DeficitRouteId);
            Assert.True(r.CompletedCycles == m.Delivered && r.SkippedCycles == m.Blocked,
                $"completed/skipped {r.CompletedCycles}/{r.SkippedCycles} != model {m.Delivered}/{m.Blocked}\n{trace}");
            Assert.True(r.LastObservedLoopCycleIndex == m.LastFired,
                $"cursor {r.LastObservedLoopCycleIndex} != model {m.LastFired}\n{trace}");
            Assert.True(Math.Abs(outpostTank.amount - m.Outpost) < 1e-6,
                $"outpost {outpostTank.amount} != model {m.Outpost}\n{trace}");
            Assert.True(outpostTank.amount <= DeficitOutpostCapacity + 1e-6,
                $"outpost {outpostTank.amount} over capacity\n{trace}");

            var events = RouteEventLog.Events;
            var delivered = events.Where(e => e != null && e.RouteId == DeficitRouteId
                && e.Type == RouteEventType.RouteCargoDelivered).ToList();
            var debited = events.Where(e => e != null && e.RouteId == DeficitRouteId
                && e.Type == RouteEventType.RouteCargoDebited).ToList();
            Assert.True(delivered.Count == r.CompletedCycles && debited.Count == r.CompletedCycles,
                $"rows delivered={delivered.Count} debited={debited.Count} != completed {r.CompletedCycles}\n{trace}");

            double deliveredSum = 0.0;
            foreach (RouteEvent d in delivered)
            {
                double got = LiquidFuelIn(d.RouteResourceManifest);
                deliveredSum += got;
                // (a) its own cycle's debit, joined on the cycle id over the whole log.
                var ownDebit = debited.Where(e => e.RouteCycleId == d.RouteCycleId).ToList();
                Assert.True(ownDebit.Count == 1,
                    $"cycle {d.RouteCycleId} has {ownDebit.Count} debit rows\n{trace}");
                double debit = LiquidFuelIn(ownDebit[0].RouteResourceManifest);
                Assert.True(Math.Abs(got - debit) < 1e-6,
                    $"cycle {d.RouteCycleId} delivered {got} but its own debit took {debit}\n{trace}");
                // (b) the recorded manifest less the deficit a FULL scan computes for that cycle.
                var fullDeficit = RouteOrchestrator.ComputeCycleSupplyDeficit(
                    LiveRoute(DeficitRouteId), d.RouteCycleId, d.Sequence, events);
                double expected = Manifest - (fullDeficit.TryGetValue("LiquidFuel", out double def) ? def : 0.0);
                Assert.True(Math.Abs(got - expected) < 1e-6,
                    $"cycle {d.RouteCycleId} delivered {got}, full-scan deficit says {expected}\n{trace}");
                // (c) the model's fraction for the dock cycle its fire UT falls in.
                double model = Manifest * DeficitFractionFor(ModelDockCycle(d.UT));
                Assert.True(Math.Abs(got - model) < 1e-6,
                    $"cycle {d.RouteCycleId} at ut={d.UT} delivered {got}, model {model}\n{trace}");
            }
            Assert.True(Math.Abs(outpost0 + deliveredSum - outpostTank.amount) < 1e-6,
                $"outpost {outpostTank.amount} != start {outpost0} + delivered {deliveredSum}\n{trace}");
        }

        // catches: the real delivery reading the supply deficit from the wrong rows (another
        // cycle's, none, or a later window's) over warps, saves, Re-Fly starts and Discard Re-fly
        // loads: a short cycle delivering the full recorded manifest (cargo created), or a full
        // cycle capped by a neighbour's shortfall (cargo lost), against a full-scan model.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void RandomTimeline_ShortSupply_DeliveryCapMatchesTheFullScan(int seed)
        {
            RunDeficitTimeline(seed);
        }

        private void RunDeficitTimeline(int seed)
        {
            var rng = new Random(seed);
            double outpost0 = 100.0 * rng.Next(0, 4);
            InstallDeficitRoute(seed, outpost0);
            var env = new ChainEnv(this);

            var model = new DeficitModel { UT = Anchor, Outpost = outpost0 };
            RouteOrchestrator.Tick(Anchor, env);
            DeficitModelTick(model, Anchor);

            var saves = new List<DeficitSave>();
            int loads = 0;
            int shortDelivered = 0;
            var trace = new StringBuilder("seed=" + seed.ToString(IC) + " outpost0=" + outpost0.ToString(IC));
            // Steps under the cadence dominate so most cycles fire on their own tick.
            double[] steps = { 1.0, 5.0, 20.0, 50.0, 97.0, 200.0, 299.0, 450.0 };

            for (int op = 0; op < 80; op++)
            {
                int pick = rng.Next(100);
                if (pick < 72)
                {
                    double step = steps[rng.Next(steps.Length)];
                    int count = 1 + rng.Next(30);
                    trace.Append(" warp(").Append(step.ToString(IC)).Append('x')
                        .Append(count.ToString(IC)).Append(')');
                    for (int i = 0; i < count; i++)
                    {
                        double ut = model.UT + step;
                        RouteOrchestrator.Tick(ut, env);
                        int before = model.ShortDelivered;
                        DeficitModelTick(model, ut);
                        shortDelivered += model.ShortDelivered - before;
                    }
                }
                else if (pick < 84 || saves.Count == 0)
                {
                    trace.Append(" save@").Append(model.UT.ToString("R", IC));
                    saves.Add(new DeficitSave
                    {
                        Model = model.Clone(),
                        Outpost = outpostTank.amount,
                        Routes = new List<Route> { SnapshotAsSaved(LiveRoute(DeficitRouteId)) },
                    });
                }
                else
                {
                    DeficitSave s = saves[rng.Next(saves.Count)];
                    bool reFly = pick < 92;
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
                    outpostTank.amount = s.Outpost;
                    model = s.Model.Clone();
                    saves.RemoveAll(x => x.Model.UT > s.Model.UT);
                    RouteOrchestrator.Tick(model.UT, env);
                }

                AssertDeficitInvariants(model, outpost0, trace.ToString());
            }

            Assert.True(shortDelivered > 0, "no short cycle delivered: " + trace);
            Assert.True(loads > 0, "no load-back drawn: " + trace);
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("supply short this cycle - capping delivery to what was picked up"));
        }
    }
}
