using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Parsek;
using Parsek.Logistics;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Characterizes the route tick with many routes at once (test-gap list 2026-11-18, "Many
    /// routes at once"): 10, 20 and 30 single-stop loop routes, half from the Space Center and
    /// half drawing from their own unloaded depot, each delivering into its own unloaded tank
    /// through the production delivery and origin-debit writers. Measures, per route count, the
    /// mean <see cref="RouteOrchestrator.Tick(double, IRouteRuntimeEnvironment)"/> at 1x (1 s
    /// steps, the common case: one tick per game second, or one per frame under warp) and under
    /// warp (1000 s steps, every route fires every tick), both on a fresh route event log and
    /// after a long career's worth of rows. The bounds are generous (two orders of magnitude
    /// above the measured numbers) so the cell only reds on a gross regression; the numbers go
    /// to the test output. The environment is the test fake, so the live endpoint resolution
    /// (<c>FlightGlobals</c> walks) is not in the measurement.
    /// </summary>
    [Collection("Sequential")]
    public class RouteTickScalingTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const double Anchor = 1000.0;
        private readonly ITestOutputHelper output;
        private int lineCount;
        private readonly Dictionary<uint, Vessel> vessels = new Dictionary<uint, Vessel>();

        public RouteTickScalingTests(ITestOutputHelper output)
        {
            this.output = output;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            // Verbose stays on (the shipping default); lines are counted, not kept.
            ParsekLog.TestSinkForTesting = line => lineCount++;
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
            RouteStore.ResetForTesting();
            RouteEventLog.ResetForTesting();
        }

        private sealed class ScaleEnv : IRouteRuntimeEnvironment
        {
            private readonly Dictionary<uint, Vessel> vessels;
            internal ScaleEnv(Dictionary<uint, Vessel> vessels) { this.vessels = vessels; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            {
                bool ok = vessels.ContainsKey(endpoint.VesselPersistentId);
                reason = ok ? string.Empty : "unknown-pid";
                return ok;
            }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            {
                bool ok = vessels.TryGetValue(endpoint.VesselPersistentId, out vessel);
                reason = ok ? string.Empty : "unknown-pid";
                return ok;
            }
            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall)
            {
                lackingResource = string.Empty;
                shortfall = 0.0;
                if (route.IsKscOrigin)
                    return true;
                Vessel origin = vessels[route.Origin.VesselPersistentId];
                var probe = new LiveOriginCargoProbe(origin, isLoaded: false);
                return RoutePickupSourceGate.OriginCoversNetOfOtherRoutes(
                    route.CostManifest, probe.ProbeResourceStored,
                    name => RouteStore.OtherRoutesReservedFor(route.Id, origin.persistentId, name),
                    out lackingResource, out shortfall, out bool _, out double _, out double _);
            }
            public bool DestinationHasCapacity(Route route, out string fullResource)
            {
                bool fits = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                    route,
                    stopIndex => new LiveDeliveryCapacityProbe(
                        vessels[route.Stops[stopIndex].Endpoint.VesselPersistentId], isLoaded: false),
                    out string fullToken, out int _);
                fullResource = fits ? string.Empty : fullToken;
                return fits;
            }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        private void AddVessel(uint pid, double amount)
        {
            var tank = (ProtoPartResourceSnapshot)FormatterServices.GetUninitializedObject(
                typeof(ProtoPartResourceSnapshot));
            tank.resourceName = "LiquidFuel";
            tank.amount = amount;
            tank.maxAmount = 1e12;
            tank.flowState = true;
            var part = (ProtoPartSnapshot)FormatterServices.GetUninitializedObject(typeof(ProtoPartSnapshot));
            part.resources = new List<ProtoPartResourceSnapshot> { tank };
            var pv = (ProtoVessel)FormatterServices.GetUninitializedObject(typeof(ProtoVessel));
            pv.protoPartSnapshots = new List<ProtoPartSnapshot> { part };
            var vessel = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(vessel, new IntPtr(1));
            vessel.persistentId = pid;
            vessel.vesselName = "Scale " + pid.ToString(IC);
            vessel.loaded = false;
            vessel.packed = true;
            vessel.protoVessel = pv;
            vessels[pid] = vessel;
        }

        /// <summary>N routes, cadences 300 + 7 i (so the docks spread out), even ones from the
        /// Space Center, odd ones from their own depot; every route has its own tank.</summary>
        private void InstallRoutes(int n)
        {
            var units = new Dictionary<string, GhostPlaybackLogic.LoopUnit>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                double cadence = 300.0 + 7.0 * i;
                string id = "route-scale-" + i.ToString("D2", IC);
                uint destPid = 20000u + (uint)i;
                uint originPid = 30000u + (uint)i;
                AddVessel(destPid, 0.0);
                bool ksc = i % 2 == 0;
                if (!ksc)
                    AddVessel(originPid, 1e11);
                RouteStore.AddRoute(new Route
                {
                    Id = id,
                    Name = id,
                    Status = RouteStatus.Active,
                    CreatedUT = 900.0,
                    IsKscOrigin = ksc,
                    Origin = ksc ? default(RouteEndpoint) : new RouteEndpoint { VesselPersistentId = originPid, RootPartUId = 1u },
                    BackingMissionTreeId = "tree-" + id,
                    RecordedDockUT = Anchor + cadence / 2.0,
                    DockMemberRecordingId = "rec-" + id,
                    LoopAnchorUT = Anchor,
                    LastObservedLoopCycleIndex = -1,
                    DispatchInterval = cadence,
                    TransitDuration = cadence,
                    CostManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
                    Stops = new List<RouteStop>
                    {
                        new RouteStop
                        {
                            Endpoint = new RouteEndpoint { VesselPersistentId = destPid, RootPartUId = 1u },
                            DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
                        },
                    },
                    SourceRefs = new List<RouteSourceRef>
                    {
                        new RouteSourceRef { RecordingId = "rec-" + id, TreeId = "tree-" + id, RouteProofHash = "deadbeef" },
                    },
                });
                units[id] = new GhostPlaybackLogic.LoopUnit(
                    ownerIndex: i, memberIndices: new[] { i },
                    spanStartUT: Anchor, spanEndUT: Anchor + cadence,
                    cadenceSeconds: cadence, phaseAnchorUT: Anchor);
            }
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => units[r.Id];
        }

        private struct Phase
        {
            internal double MeanMicros;
            internal double MaxMicros;
            internal long BytesPerTick;
            internal int Fires;
            internal int Lines;
        }

        private static long AllocatedBytes()
        {
            try
            {
                AppDomain.MonitoringIsEnabled = true;
                return AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            }
            catch (Exception)
            {
                return -1; // not supported on this runtime
            }
        }

        private Phase Run(IRouteRuntimeEnvironment env, ref double ut, double step, int ticks)
        {
            int rowsBefore = RouteEventLog.Events.Count;
            int linesBefore = lineCount;
            long totalTicks = 0;
            long maxTicks = 0;
            long alloc0 = AllocatedBytes();
            for (int i = 0; i < ticks; i++)
            {
                ut += step;
                long t0 = Stopwatch.GetTimestamp();
                RouteOrchestrator.Tick(ut, env);
                long dt = Stopwatch.GetTimestamp() - t0;
                if (dt > maxTicks) maxTicks = dt;
                totalTicks += dt;
            }
            long alloc1 = AllocatedBytes();
            double usPerTick = 1e6 / Stopwatch.Frequency;
            var phase = new Phase
            {
                MeanMicros = totalTicks * usPerTick / ticks,
                MaxMicros = maxTicks * usPerTick,
                BytesPerTick = alloc0 >= 0 && alloc1 >= 0 ? (alloc1 - alloc0) / ticks : -1,
                Fires = RouteEventLog.Events.Skip(rowsBefore).Count(e => e.Type == RouteEventType.RouteDispatched),
                Lines = lineCount - linesBefore,
            };
            return phase;
        }

        private string Describe(string label, int n, Phase p, int ticks) =>
            $"routes={n.ToString(IC),2} {label,-26} meanTick={p.MeanMicros.ToString("F1", IC),8} us " +
            $"maxTick={p.MaxMicros.ToString("F1", IC),8} us alloc/tick={(p.BytesPerTick < 0 ? "n/a" : p.BytesPerTick.ToString(IC)),8} B " +
            $"fires={p.Fires.ToString(IC),5}/{ticks.ToString(IC)} ticks logLines={p.Lines.ToString(IC)} " +
            $"eventRows={RouteEventLog.Events.Count.ToString(IC)}";

        // catches: a gross regression in the per-tick cost of many routes (a quadratic walk over
        // the routes turning a tick into milliseconds) at the 1x cadence and under warp, and the
        // no-crossing log line going back to formatting its message on every route every tick
        // (about 1.4 KB per route per tick of garbage, one tick per frame under warp).
        [Fact]
        public void ManyRoutes_TickCostStaysSmall()
        {
            var report = new List<string>();
            foreach (int n in new[] { 10, 20, 30 })
            {
                ResetStores();
                vessels.Clear();
                InstallRoutes(n);
                var env = new ScaleEnv(vessels);
                double ut = Anchor;
                RouteOrchestrator.Tick(ut, env);
                Run(env, ref ut, 1.0, 200); // warm-up: JIT out of the measurement

                Phase steady = Run(env, ref ut, 1.0, 2000);
                report.Add(Describe("1x", n, steady, 2000));
                Phase warp = Run(env, ref ut, 1000.0, 100);
                report.Add(Describe("warp 1000s", n, warp, 100));
                string all = string.Join(Environment.NewLine, report);

                Assert.True(warp.Fires == n * 100, "every route fires once per warp tick: " + all);
                Assert.True(steady.MeanMicros < 5000.0, all);
                Assert.True(warp.MeanMicros < 200000.0, all);
                if (steady.BytesPerTick >= 0)
                {
                    Assert.True(steady.BytesPerTick < 2048L * n,
                        "per-route garbage per 1x tick over 2 KB (the eager no-crossing message?): " + all);
                }
            }
            foreach (string line in report)
                output.WriteLine(line);
        }

        // Manual benchmark (about two minutes): the same routes before and after a long career's
        // event log (1300 warp ticks, every route firing each one). Set PARSEK_ROUTE_TICK_BENCH=1
        // to run it; the numbers go to the test output. The per-fire cost grows with the log
        // (todo ROUTE-TICK-EVENT-LOG-SCANS), so this reports instead of asserting.
        [Fact]
        [Trait("Category", "Manual")]
        public void ManyRoutes_LongEventLog_Benchmark()
        {
            if (Environment.GetEnvironmentVariable("PARSEK_ROUTE_TICK_BENCH") != "1")
            {
                output.WriteLine("set PARSEK_ROUTE_TICK_BENCH=1 to run the long-log route tick benchmark");
                return;
            }
            var report = new List<string>();
            foreach (int n in new[] { 10, 20, 30 })
            {
                ResetStores();
                vessels.Clear();
                InstallRoutes(n);
                var env = new ScaleEnv(vessels);
                double ut = Anchor;
                RouteOrchestrator.Tick(ut, env);
                Run(env, ref ut, 1.0, 200);
                report.Add(Describe("1x fresh log", n, Run(env, ref ut, 1.0, 2000), 2000));
                report.Add(Describe("warp 1000s fresh log", n, Run(env, ref ut, 1000.0, 200), 200));
                Run(env, ref ut, 1000.0, 1300);
                report.Add(Describe("1x long log", n, Run(env, ref ut, 1.0, 2000), 2000));
                report.Add(Describe("warp 1000s long log", n, Run(env, ref ut, 1000.0, 200), 200));
            }
            foreach (string line in report)
                output.WriteLine(line);
        }
    }
}
