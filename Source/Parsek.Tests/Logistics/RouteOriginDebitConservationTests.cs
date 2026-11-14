using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Route conservation on the origin side (design 19.2.4 / 19.2.5): the
    /// production origin debit (<c>RouteOrchestrator.ApplyOriginDebit</c>, reached
    /// through <see cref="RouteOrchestrator.EmitDispatchDebit"/> with the seam left
    /// null) driven against a headless UNLOADED origin vessel whose tanks are real
    /// <see cref="ProtoPartResourceSnapshot"/> objects. Per resource, what leaves
    /// the origin's open tanks must equal what the RouteCargoDebited row records,
    /// which must equal min(cost, open stored); a resource the origin cannot cover
    /// is recorded at its full cost on the requested manifest; nothing outside the
    /// cost manifest, and no closed tank, is touched.
    /// </summary>
    [Collection("Sequential")]
    public class RouteOriginDebitConservationTests : IDisposable
    {
        private const double Tol = 1e-9;
        private const uint OriginPid = 77001u;

        private static readonly FieldInfo CachedPtr = typeof(UnityEngine.Object).GetField(
            "m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly List<string> logLines = new List<string>();

        public RouteOriginDebitConservationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RouteStore.ResetForTesting();
            RouteEventLog.ResetForTesting();
            Ledger.ResetForTesting();
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.LoopUnitResolverForTesting = null;
        }

        public void Dispose()
        {
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteEventLog.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private sealed class Tank
        {
            internal readonly ProtoPartResourceSnapshot Snapshot;
            internal readonly double Before;

            internal Tank(ProtoPartResourceSnapshot snapshot)
            {
                Snapshot = snapshot;
                Before = snapshot.amount;
            }

            internal string Name => Snapshot.resourceName;
            internal bool Open => Snapshot.flowState;
            internal double Drained => Before - Snapshot.amount;
        }

        private static ProtoPartResourceSnapshot NewTank(string name, double amount, bool open = true)
        {
            var prs = (ProtoPartResourceSnapshot)FormatterServices.GetUninitializedObject(
                typeof(ProtoPartResourceSnapshot));
            prs.resourceName = name;
            prs.amount = amount;
            prs.maxAmount = Math.Max(amount, 1.0) * 2.0;
            prs.flowState = open;
            return prs;
        }

        private static ProtoPartSnapshot NewPart(params ProtoPartResourceSnapshot[] tanks)
        {
            var pps = (ProtoPartSnapshot)FormatterServices.GetUninitializedObject(typeof(ProtoPartSnapshot));
            pps.resources = new List<ProtoPartResourceSnapshot>(tanks);
            return pps;
        }

        /// <summary>An unloaded vessel Unity's == reads as alive (or as destroyed when
        /// <paramref name="alive"/> is false), carrying <paramref name="parts"/>.</summary>
        private static Vessel UnloadedOrigin(bool alive, params ProtoPartSnapshot[] parts)
        {
            var v = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            Assert.NotNull(CachedPtr);
            CachedPtr.SetValue(v, alive ? new IntPtr(1) : IntPtr.Zero);
            v.persistentId = OriginPid;
            v.vesselName = "Depot";
            v.loaded = false;
            v.packed = true;
            var pv = (ProtoVessel)FormatterServices.GetUninitializedObject(typeof(ProtoVessel));
            pv.protoPartSnapshots = new List<ProtoPartSnapshot>(parts);
            v.protoVessel = pv;
            return v;
        }

        private static List<Tank> TanksOf(Vessel v)
        {
            var tanks = new List<Tank>();
            foreach (ProtoPartSnapshot pps in v.protoVessel.protoPartSnapshots)
                foreach (ProtoPartResourceSnapshot prs in pps.resources)
                    tanks.Add(new Tank(prs));
            return tanks;
        }

        private static Route VesselOriginRoute(Dictionary<string, double> cost, string id = "route-origin-debit")
        {
            Route route = new RouteFixtureBuilder()
                .WithId(id)
                .WithKscOrigin(false)
                .WithOrigin(new RouteEndpoint { VesselPersistentId = OriginPid, RootPartUId = 1u })
                .WithCostManifest(cost)
                .Build();
            route.IsKscOrigin = false;
            return route;
        }

        public enum ResolveMode { Vessel, ReturnsFalse, Throws, ReturnsTrueWithNull }

        private sealed class OriginEnv : IRouteRuntimeEnvironment
        {
            private readonly Vessel origin;
            private readonly ResolveMode mode;
            internal int VesselResolveCalls;

            internal OriginEnv(Vessel origin, ResolveMode mode = ResolveMode.Vessel)
            {
                this.origin = origin;
                this.mode = mode;
            }

            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            {
                reason = string.Empty;
                return true;
            }

            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            {
                VesselResolveCalls++;
                switch (mode)
                {
                    case ResolveMode.ReturnsFalse:
                        vessel = null;
                        reason = "test-not-found";
                        return false;
                    case ResolveMode.Throws:
                        throw new InvalidOperationException("test resolver failure");
                    case ResolveMode.ReturnsTrueWithNull:
                        vessel = null;
                        reason = string.Empty;
                        return true;
                    default:
                        vessel = origin;
                        reason = string.Empty;
                        return true;
                }
            }

            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall)
            {
                lackingResource = string.Empty;
                shortfall = 0.0;
                return true;
            }

            public bool DestinationHasCapacity(Route route, out string fullResource)
            {
                fullResource = string.Empty;
                return true;
            }

            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        private static RouteEvent RowOf(RouteEventType type, string cycleId)
        {
            return RouteEventLog.Events.Single(e => e.Type == type && e.RouteCycleId == cycleId);
        }

        private static double ValueOrZero(Dictionary<string, double> manifest, string name)
        {
            return manifest != null && manifest.TryGetValue(name, out double v) ? v : 0.0;
        }

        private bool HasLog(params string[] fragments)
        {
            return logLines.Any(l => fragments.All(f => l.Contains(f)));
        }

        // ------------------------------------------------------------------
        // Resolved origin, unloaded tanks
        // ------------------------------------------------------------------

        // catches: a debit that removes the wrong amount from a multi-tank,
        // multi-resource origin, or a row whose manifest disagrees with what left
        // the tanks (origin debit != recorded debit).
        [Fact]
        public void FullOrigin_MultiResourceMultiTank_DebitsExactlyTheCost_RowMatchesTanks()
        {
            Vessel origin = UnloadedOrigin(true,
                NewPart(NewTank("LiquidFuel", 30.0), NewTank("Oxidizer", 40.0)),
                NewPart(NewTank("LiquidFuel", 50.0), NewTank("Oxidizer", 60.0), NewTank("Ore", 500.0)));
            List<Tank> tanks = TanksOf(origin);
            Route route = VesselOriginRoute(new Dictionary<string, double>
            {
                { "LiquidFuel", 72.0 },
                { "Oxidizer", 88.0 },
            });

            RouteOrchestrator.EmitDispatchDebit(route, 1000.0, new OriginEnv(origin), "cycle-0",
                applyPhysicalOriginDebit: true);

            RouteEvent debit = RowOf(RouteEventType.RouteCargoDebited, "cycle-0");
            Assert.Equal(72.0, ValueOrZero(debit.RouteResourceManifest, "LiquidFuel"), 9);
            Assert.Equal(88.0, ValueOrZero(debit.RouteResourceManifest, "Oxidizer"), 9);
            Assert.Equal(2, debit.RouteResourceManifest.Count);
            Assert.Null(debit.RouteRequestedResourceManifest);
            Assert.Equal(OriginPid, debit.RouteOriginVesselPid);

            Assert.Equal(72.0, tanks.Where(t => t.Name == "LiquidFuel").Sum(t => t.Drained), 9);
            Assert.Equal(88.0, tanks.Where(t => t.Name == "Oxidizer").Sum(t => t.Drained), 9);
            Assert.Equal(0.0, tanks.Single(t => t.Name == "Ore").Drained);
            // Drained in part order: the first part's tanks empty before the second's.
            Assert.Equal(0.0, tanks[0].Snapshot.amount, 9);
            Assert.Equal(8.0, tanks[2].Snapshot.amount, 9);
            Assert.All(tanks, t => Assert.True(t.Snapshot.amount >= 0.0));

            Assert.True(HasLog("[Route]", "OriginDebit plan:", "short=0", "path=unloaded"));
            Assert.False(HasLog("SHORT at apply"));
        }

        // catches: a partial origin recorded as fully debited (cargo created on
        // the row), a debit that overdraws a tank, or a requested manifest that
        // names a resource the origin covered.
        [Fact]
        public void PartialOrigin_DebitsWhatIsStored_RequestedNamesOnlyTheShortResource()
        {
            Vessel origin = UnloadedOrigin(true,
                NewPart(NewTank("LiquidFuel", 10.0), NewTank("Oxidizer", 100.0)),
                NewPart(NewTank("LiquidFuel", 15.0)));
            List<Tank> tanks = TanksOf(origin);
            Route route = VesselOriginRoute(new Dictionary<string, double>
            {
                { "LiquidFuel", 40.0 },
                { "Oxidizer", 50.0 },
            });

            RouteOrchestrator.EmitDispatchDebit(route, 1000.0, new OriginEnv(origin), "cycle-0",
                applyPhysicalOriginDebit: true);

            RouteEvent debit = RowOf(RouteEventType.RouteCargoDebited, "cycle-0");
            Assert.Equal(25.0, ValueOrZero(debit.RouteResourceManifest, "LiquidFuel"), 9);
            Assert.Equal(50.0, ValueOrZero(debit.RouteResourceManifest, "Oxidizer"), 9);
            Assert.NotNull(debit.RouteRequestedResourceManifest);
            Assert.Equal(new[] { "LiquidFuel" }, debit.RouteRequestedResourceManifest.Keys.ToArray());
            Assert.Equal(40.0, debit.RouteRequestedResourceManifest["LiquidFuel"]);
            Assert.Equal(25.0, tanks.Where(t => t.Name == "LiquidFuel").Sum(t => t.Drained), 9);
            Assert.All(tanks.Where(t => t.Name == "LiquidFuel"), t => Assert.Equal(0.0, t.Snapshot.amount, 9));
            Assert.Equal(50.0, tanks.Single(t => t.Name == "Oxidizer").Snapshot.amount, 9);

            Assert.True(HasLog("[Route]", "SHORT at apply", "debitedResources=2", "requestedResources=1"));
        }

        // catches: an empty origin (no tank of the resource, or a tank at zero)
        // that writes a non-empty debited manifest, or a requested manifest that
        // drops the uncovered resources.
        [Fact]
        public void EmptyOrigin_RowHasNoActuals_RequestedIsTheWholeCost()
        {
            Vessel origin = UnloadedOrigin(true,
                NewPart(NewTank("LiquidFuel", 0.0)),
                NewPart(NewTank("Ore", 80.0)));
            List<Tank> tanks = TanksOf(origin);
            Route route = VesselOriginRoute(new Dictionary<string, double>
            {
                { "LiquidFuel", 12.5 },
                { "MonoPropellant", 4.0 },
            });

            RouteOrchestrator.EmitDispatchDebit(route, 1000.0, new OriginEnv(origin), "cycle-0",
                applyPhysicalOriginDebit: true);

            RouteEvent debit = RowOf(RouteEventType.RouteCargoDebited, "cycle-0");
            Assert.Null(debit.RouteResourceManifest);
            Assert.Equal(2, debit.RouteRequestedResourceManifest.Count);
            Assert.Equal(12.5, debit.RouteRequestedResourceManifest["LiquidFuel"]);
            Assert.Equal(4.0, debit.RouteRequestedResourceManifest["MonoPropellant"]);
            Assert.Equal(OriginPid, debit.RouteOriginVesselPid);
            Assert.All(tanks, t => Assert.Equal(0.0, t.Drained));
        }

        // catches: a closed tank (flowState false, the player's lock) drained by
        // the debit, or counted as available so the row claims cargo that never
        // left the origin.
        [Fact]
        public void ClosedTank_IsNeitherCountedNorDrained()
        {
            Vessel origin = UnloadedOrigin(true,
                NewPart(NewTank("LiquidFuel", 100.0, open: false)),
                NewPart(NewTank("LiquidFuel", 20.0)));
            List<Tank> tanks = TanksOf(origin);
            Route route = VesselOriginRoute(new Dictionary<string, double> { { "LiquidFuel", 50.0 } });

            RouteOrchestrator.EmitDispatchDebit(route, 1000.0, new OriginEnv(origin), "cycle-0",
                applyPhysicalOriginDebit: true);

            RouteEvent debit = RowOf(RouteEventType.RouteCargoDebited, "cycle-0");
            Assert.Equal(100.0, tanks[0].Snapshot.amount);
            Assert.Equal(0.0, tanks[1].Snapshot.amount, 9);
            Assert.Equal(20.0, ValueOrZero(debit.RouteResourceManifest, "LiquidFuel"), 9);
            Assert.Equal(50.0, debit.RouteRequestedResourceManifest["LiquidFuel"]);
        }

        // catches: a zero or negative cost entry turned into a credit (a negative
        // debit adds cargo to the origin) or recorded on either manifest.
        [Fact]
        public void NonPositiveCostEntries_AreSkipped_NoCreditToTheOrigin()
        {
            Vessel origin = UnloadedOrigin(true,
                NewPart(NewTank("LiquidFuel", 10.0), NewTank("Oxidizer", 10.0), NewTank("Ore", 10.0)));
            List<Tank> tanks = TanksOf(origin);
            Route route = VesselOriginRoute(new Dictionary<string, double>
            {
                { "LiquidFuel", 0.0 },
                { "Oxidizer", -25.0 },
                { "Ore", 4.0 },
            });

            RouteOrchestrator.EmitDispatchDebit(route, 1000.0, new OriginEnv(origin), "cycle-0",
                applyPhysicalOriginDebit: true);

            RouteEvent debit = RowOf(RouteEventType.RouteCargoDebited, "cycle-0");
            Assert.Equal(new[] { "Ore" }, debit.RouteResourceManifest.Keys.ToArray());
            Assert.Equal(4.0, debit.RouteResourceManifest["Ore"], 9);
            Assert.Null(debit.RouteRequestedResourceManifest);
            Assert.Equal(10.0, tanks.Single(t => t.Name == "LiquidFuel").Snapshot.amount);
            Assert.Equal(10.0, tanks.Single(t => t.Name == "Oxidizer").Snapshot.amount);
            Assert.Equal(6.0, tanks.Single(t => t.Name == "Ore").Snapshot.amount, 9);
        }

        // catches: repeated cycles against one depot drifting from conservation:
        // the sum of every cycle's debited row must equal what left the depot, the
        // cycle that runs it dry records the shortfall, and a dry depot's cycles
        // record zero actuals rather than the cost.
        [Fact]
        public void RepeatedCycles_DrainTheDepot_RowsSumToTheDrawdown()
        {
            Vessel origin = UnloadedOrigin(true,
                NewPart(NewTank("LiquidFuel", 33.0)),
                NewPart(NewTank("LiquidFuel", 17.0)));
            List<Tank> tanks = TanksOf(origin);
            Route route = VesselOriginRoute(new Dictionary<string, double> { { "LiquidFuel", 15.0 } });
            var env = new OriginEnv(origin);

            for (int c = 0; c < 5; c++)
            {
                RouteOrchestrator.EmitDispatchDebit(route, 1000.0 + c * 100.0, env,
                    "cycle-" + c.ToString(CultureInfo.InvariantCulture), applyPhysicalOriginDebit: true);
            }

            List<RouteEvent> debits = RouteEventLog.Events
                .Where(e => e.Type == RouteEventType.RouteCargoDebited).ToList();
            Assert.Equal(5, debits.Count);
            double[] perCycle = debits.Select(d => ValueOrZero(d.RouteResourceManifest, "LiquidFuel")).ToArray();
            Assert.Equal(new[] { 15.0, 15.0, 15.0, 5.0, 0.0 }, perCycle.Select(x => Math.Round(x, 9)).ToArray());
            Assert.Equal(50.0, tanks.Sum(t => t.Drained), 9);
            Assert.Equal(perCycle.Sum(), tanks.Sum(t => t.Drained), 9);
            Assert.Null(debits[2].RouteRequestedResourceManifest);
            Assert.Equal(15.0, debits[3].RouteRequestedResourceManifest["LiquidFuel"]);
            Assert.Equal(15.0, debits[4].RouteRequestedResourceManifest["LiquidFuel"]);
            Assert.Null(debits[4].RouteResourceManifest);
        }

        // catches: the multi-stop carrier index lost from the debit row (the
        // per-window replay guard keys on it) or the debit applied more than once
        // for one dispatch.
        [Fact]
        public void MultiStopCarrierIndex_StampsBothRows_SingleDebit()
        {
            Vessel origin = UnloadedOrigin(true, NewPart(NewTank("LiquidFuel", 100.0)));
            List<Tank> tanks = TanksOf(origin);
            Route route = VesselOriginRoute(new Dictionary<string, double> { { "LiquidFuel", 30.0 } });

            RouteOrchestrator.EmitDispatchDebit(route, 1000.0, new OriginEnv(origin), "cycle-0",
                applyPhysicalOriginDebit: true, dispatchStopIndex: 2);

            RouteEvent dispatched = RowOf(RouteEventType.RouteDispatched, "cycle-0");
            RouteEvent debit = RowOf(RouteEventType.RouteCargoDebited, "cycle-0");
            Assert.Equal(2, dispatched.RouteStopIndex);
            Assert.Equal(2, debit.RouteStopIndex);
            Assert.Equal(2 * RouteOrchestrator.SeqStride, dispatched.Sequence);
            Assert.Equal(2 * RouteOrchestrator.SeqStride + 1, debit.Sequence);
            Assert.Equal(30.0, tanks[0].Drained, 9);
            Assert.Equal(30.0, debit.RouteResourceManifest["LiquidFuel"], 9);
        }

        // ------------------------------------------------------------------
        // Unresolved origin
        // ------------------------------------------------------------------

        // catches: an origin that cannot be resolved at debit time (resolver
        // refuses, throws, answers true with no vessel, or hands back a destroyed
        // vessel) recorded as if the cost had been taken, or touching tanks.
        [Theory]
        [InlineData(ResolveMode.ReturnsFalse, "reason=test-not-found")]
        [InlineData(ResolveMode.Throws, "reason=resolver-threw-InvalidOperationException")]
        [InlineData(ResolveMode.ReturnsTrueWithNull, "reason=resolved-null-vessel")]
        public void UnresolvedOrigin_ZeroActuals_RequestedIsThePositiveCost(ResolveMode mode, string reasonToken)
        {
            Vessel origin = UnloadedOrigin(true, NewPart(NewTank("LiquidFuel", 100.0)));
            List<Tank> tanks = TanksOf(origin);
            Route route = VesselOriginRoute(new Dictionary<string, double>
            {
                { "LiquidFuel", 30.0 },
                { "Oxidizer", 0.0 },
            });

            RouteOrchestrator.EmitDispatchDebit(route, 1000.0, new OriginEnv(origin, mode), "cycle-0",
                applyPhysicalOriginDebit: true);

            AssertUnresolvedRow(tanks, reasonToken);
        }

        [Fact]
        public void DestroyedOriginVessel_IsUnresolved_NotDebited()
        {
            Vessel origin = UnloadedOrigin(false, NewPart(NewTank("LiquidFuel", 100.0)));
            List<Tank> tanks = TanksOf(origin);
            Route route = VesselOriginRoute(new Dictionary<string, double>
            {
                { "LiquidFuel", 30.0 },
                { "Oxidizer", 0.0 },
            });

            RouteOrchestrator.EmitDispatchDebit(route, 1000.0, new OriginEnv(origin), "cycle-0",
                applyPhysicalOriginDebit: true);

            AssertUnresolvedRow(tanks, "reason=resolved-null-vessel");
        }

        private void AssertUnresolvedRow(List<Tank> tanks, string reasonToken)
        {
            RouteEvent debit = RowOf(RouteEventType.RouteCargoDebited, "cycle-0");
            Assert.Null(debit.RouteResourceManifest);
            Assert.Equal(new[] { "LiquidFuel" }, debit.RouteRequestedResourceManifest.Keys.ToArray());
            Assert.Equal(30.0, debit.RouteRequestedResourceManifest["LiquidFuel"]);
            Assert.Equal(0u, debit.RouteOriginVesselPid);
            Assert.All(tanks, t => Assert.Equal(0.0, t.Drained));
            Assert.True(HasLog("[WARN]", "[Route]", "origin unresolved at debit", reasonToken));
            Assert.False(HasLog("SHORT at apply"));
            Assert.True(HasLog("[Route]", "DispatchDebit:", "unresolved=1"));
        }

        // ------------------------------------------------------------------
        // Origin kinds that never debit
        // ------------------------------------------------------------------

        // catches: a Space Center origin consulting the origin vessel or writing a
        // debit row (it is free and moves nothing out of any vessel).
        [Fact]
        public void KscOrigin_WritesDispatchOnly_NeverResolvesOrDrainsAVessel()
        {
            Vessel origin = UnloadedOrigin(true, NewPart(NewTank("LiquidFuel", 100.0)));
            List<Tank> tanks = TanksOf(origin);
            Route route = VesselOriginRoute(new Dictionary<string, double> { { "LiquidFuel", 30.0 } });
            route.IsKscOrigin = true;
            var env = new OriginEnv(origin);

            RouteOrchestrator.EmitDispatchDebit(route, 1000.0, env, "cycle-0", applyPhysicalOriginDebit: true);

            Assert.Single(RouteEventLog.Events);
            Assert.Equal(RouteEventType.RouteDispatched, RouteEventLog.Events[0].Type);
            Assert.Equal(0, env.VesselResolveCalls);
            Assert.Equal(0.0, tanks[0].Drained);
        }

        // ------------------------------------------------------------------
        // Seeded conservation property
        // ------------------------------------------------------------------

        // catches: any per-resource conservation break over random origins: a
        // resource whose drawdown differs from its debited row, a row that exceeds
        // min(cost, open stored), a tank driven negative, a closed tank or an
        // uncosted resource touched, or a requested manifest present when the
        // origin covered the cost (or absent when it did not).
        [Fact]
        public void SeededOrigins_PerResourceDrawdownEqualsDebitedRow()
        {
            string[] names = { "LiquidFuel", "Oxidizer", "MonoPropellant", "Ore", "XenonGas" };
            int shortCases = 0, fullCases = 0;
            for (int seed = 1; seed <= 300; seed++)
            {
                RouteEventLog.ResetForTesting();
                logLines.Clear();
                var rng = new Random(seed);

                int partCount = 1 + rng.Next(5);
                var parts = new ProtoPartSnapshot[partCount];
                for (int p = 0; p < partCount; p++)
                {
                    int tankCount = rng.Next(4);
                    var partTanks = new ProtoPartResourceSnapshot[tankCount];
                    for (int t = 0; t < tankCount; t++)
                    {
                        double amount = rng.Next(5) == 0 ? 0.0 : Math.Round(rng.NextDouble() * 200.0, rng.Next(4));
                        partTanks[t] = NewTank(names[rng.Next(names.Length)], amount, open: rng.Next(6) != 0);
                    }
                    parts[p] = NewPart(partTanks);
                }
                Vessel origin = UnloadedOrigin(true, parts);
                List<Tank> tanks = TanksOf(origin);

                var cost = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (string name in names)
                {
                    int roll = rng.Next(5);
                    if (roll == 0) continue;
                    cost[name] = roll == 1 ? -rng.NextDouble() * 10.0
                        : roll == 2 ? 0.0
                        : Math.Round(rng.NextDouble() * 250.0, rng.Next(5));
                }
                Route route = VesselOriginRoute(cost, "route-seed-" + seed.ToString(CultureInfo.InvariantCulture));

                RouteOrchestrator.EmitDispatchDebit(route, 1000.0, new OriginEnv(origin), "cycle-0",
                    applyPhysicalOriginDebit: true);

                string ctx = "seed=" + seed.ToString(CultureInfo.InvariantCulture);
                RouteEvent debit = RowOf(RouteEventType.RouteCargoDebited, "cycle-0");
                Assert.Equal(OriginPid, debit.RouteOriginVesselPid);
                Assert.All(tanks, t => Assert.True(t.Snapshot.amount >= -Tol, ctx + " tank went negative"));
                Assert.All(tanks.Where(t => !t.Open), t => Assert.True(t.Drained == 0.0, ctx + " closed tank drained"));

                bool anyShort = false;
                foreach (string name in names)
                {
                    double drained = tanks.Where(t => t.Name == name).Sum(t => t.Drained);
                    double recorded = ValueOrZero(debit.RouteResourceManifest, name);
                    double requested = ValueOrZero(debit.RouteRequestedResourceManifest, name);
                    cost.TryGetValue(name, out double required);
                    double openStored = tanks.Where(t => t.Name == name && t.Open && t.Before > 0.0).Sum(t => t.Before);

                    Assert.True(Math.Abs(drained - recorded) <= Tol,
                        $"{ctx} {name}: drained={drained.ToString("R", CultureInfo.InvariantCulture)} " +
                        $"recorded={recorded.ToString("R", CultureInfo.InvariantCulture)}");
                    if (required <= 0.0)
                    {
                        Assert.True(drained == 0.0, $"{ctx} {name}: uncosted resource drained");
                        Assert.True(requested == 0.0, $"{ctx} {name}: uncosted resource requested");
                        continue;
                    }

                    double expected = Math.Min(required, openStored);
                    Assert.True(Math.Abs(recorded - expected) <= Tol,
                        $"{ctx} {name}: recorded={recorded.ToString("R", CultureInfo.InvariantCulture)} " +
                        $"expected={expected.ToString("R", CultureInfo.InvariantCulture)}");
                    bool isShort = openStored < required - Tol;
                    anyShort |= isShort;
                    Assert.True(isShort ? requested == required : requested == 0.0,
                        $"{ctx} {name}: requested={requested.ToString("R", CultureInfo.InvariantCulture)} " +
                        $"short={isShort}");
                }

                if (anyShort) shortCases++; else fullCases++;
                Assert.Equal(anyShort, HasLog("SHORT at apply"));
            }

            // The seeds must exercise both branches, or the property proves little.
            Assert.True(shortCases > 30, "short cases=" + shortCases.ToString(CultureInfo.InvariantCulture));
            Assert.True(fullCases > 30, "full cases=" + fullCases.ToString(CultureInfo.InvariantCulture));
        }
    }
}
