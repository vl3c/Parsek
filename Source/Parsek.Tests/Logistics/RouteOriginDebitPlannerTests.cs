using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pin the pure origin-debit planner's clamp + short + deterministic-order
    /// invariants (M1; mirror of <see cref="RouteDeliveryPlannerTests"/>).
    /// Uses a hand-rolled <see cref="IOriginCargoProbe"/> fake so the planner
    /// never touches KSP statics, vessels, or parts.
    /// </summary>
    public class RouteOriginDebitPlannerTests
    {
        /// <summary>
        /// Hand-rolled probe fake. Stored amounts are read from a settable
        /// dictionary; absent resources read as 0 (origin holds none).
        /// </summary>
        private sealed class FakeOriginCargoProbe : IOriginCargoProbe
        {
            public Dictionary<string, double> Stored = new Dictionary<string, double>();

            public double ProbeResourceStored(string resourceName)
            {
                if (resourceName == null) return 0.0;
                double v;
                return Stored.TryGetValue(resourceName, out v) ? v : 0.0;
            }
        }

        private static Route MakeRoute(Dictionary<string, double> costManifest)
        {
            return new Route
            {
                Id = "route-1",
                IsKscOrigin = false,
                CostManifest = costManifest,
            };
        }

        // catches: clamp logic returning Available < Required when the origin
        // holds enough (false short).
        [Fact]
        public void FullyStocked_PlansFullAmounts_NotShort()
        {
            var route = MakeRoute(new Dictionary<string, double>
            {
                { "LiquidFuel", 100.0 },
                { "Oxidizer", 120.0 },
            });
            var probe = new FakeOriginCargoProbe
            {
                Stored = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 }, // exact-equal boundary plans full
                    { "Oxidizer", 500.0 },
                },
            };

            var plan = RouteOriginDebitPlanner.PrepareDebit(route, probe);

            Assert.Equal(2, plan.Resources.Count);
            foreach (var line in plan.Resources)
            {
                Assert.Equal(line.Required, line.Available);
            }
            Assert.False(plan.IsShort);
        }

        // catches: a short origin not clamping to stored or losing the short
        // marker (the row's requested-on-shortfall manifest keys off it).
        [Fact]
        public void ShortResource_ClampsToStored_SetsIsShort()
        {
            var route = MakeRoute(new Dictionary<string, double>
            {
                { "LiquidFuel", 100.0 },
                { "Oxidizer", 120.0 },
            });
            var probe = new FakeOriginCargoProbe
            {
                Stored = new Dictionary<string, double>
                {
                    { "LiquidFuel", 40.0 }, // short: clamps to 40
                    { "Oxidizer", 500.0 },  // covered
                },
            };

            var plan = RouteOriginDebitPlanner.PrepareDebit(route, probe);

            Assert.Equal(2, plan.Resources.Count);
            Assert.True(plan.IsShort);
            Assert.Equal("LiquidFuel", plan.Resources[0].Name);
            Assert.Equal(100.0, plan.Resources[0].Required);
            Assert.Equal(40.0, plan.Resources[0].Available);
            Assert.Equal("Oxidizer", plan.Resources[1].Name);
            Assert.Equal(120.0, plan.Resources[1].Available);
        }

        // catches: plan line order leaking dictionary insertion order into
        // ledger rows / logs (must be ordinal name order, same rule as the
        // delivery planner).
        [Fact]
        public void PlanLines_DeterministicOrdinalOrder()
        {
            var route = MakeRoute(new Dictionary<string, double>
            {
                { "Oxidizer", 10.0 },
                { "MonoPropellant", 10.0 },
                { "LiquidFuel", 10.0 },
            });
            var probe = new FakeOriginCargoProbe
            {
                Stored = new Dictionary<string, double>
                {
                    { "LiquidFuel", 10.0 },
                    { "MonoPropellant", 10.0 },
                    { "Oxidizer", 10.0 },
                },
            };

            var plan = RouteOriginDebitPlanner.PrepareDebit(route, probe);

            Assert.Equal(3, plan.Resources.Count);
            Assert.Equal("LiquidFuel", plan.Resources[0].Name);
            Assert.Equal("MonoPropellant", plan.Resources[1].Name);
            Assert.Equal("Oxidizer", plan.Resources[2].Name);
        }

        // catches: zero / negative manifest entries producing plan lines (and
        // falsely flagging short when stored is 0).
        [Fact]
        public void SkipsNonPositiveEntries()
        {
            var route = MakeRoute(new Dictionary<string, double>
            {
                { "Ablator", 0.0 },
                { "XenonGas", -5.0 },
                { "LiquidFuel", 50.0 },
            });
            var probe = new FakeOriginCargoProbe
            {
                Stored = new Dictionary<string, double> { { "LiquidFuel", 60.0 } },
            };

            var plan = RouteOriginDebitPlanner.PrepareDebit(route, probe);

            Assert.Single(plan.Resources);
            Assert.Equal("LiquidFuel", plan.Resources[0].Name);
            Assert.False(plan.IsShort);
        }

        // catches: a negative stored read (defensive probe failure shape)
        // planning a negative removal instead of clamping at zero.
        [Fact]
        public void NegativeStored_TreatedAsZero()
        {
            var route = MakeRoute(new Dictionary<string, double> { { "LiquidFuel", 50.0 } });
            var probe = new FakeOriginCargoProbe
            {
                Stored = new Dictionary<string, double> { { "LiquidFuel", -10.0 } },
            };

            var plan = RouteOriginDebitPlanner.PrepareDebit(route, probe);

            Assert.Single(plan.Resources);
            Assert.Equal(0.0, plan.Resources[0].Available);
            Assert.True(plan.IsShort);
        }

        // catches: degenerate inputs (null route / probe / empty manifest)
        // throwing instead of returning the empty plan.
        [Fact]
        public void NullOrEmptyInputs_EmptyPlan()
        {
            var probe = new FakeOriginCargoProbe();

            var nullRoute = RouteOriginDebitPlanner.PrepareDebit((Route)null, probe);
            Assert.Empty(nullRoute.Resources);
            Assert.False(nullRoute.IsShort);

            var nullProbe = RouteOriginDebitPlanner.PrepareDebit(
                MakeRoute(new Dictionary<string, double> { { "LiquidFuel", 1.0 } }), null);
            Assert.Empty(nullProbe.Resources);

            var nullManifest = RouteOriginDebitPlanner.PrepareDebit(MakeRoute(null), probe);
            Assert.Empty(nullManifest.Resources);

            var emptyManifest = RouteOriginDebitPlanner.PrepareDebit(
                MakeRoute(new Dictionary<string, double>()), probe);
            Assert.Empty(emptyManifest.Resources);
        }

        // ==============================================================
        // M3 Phase 3 (design D5): manifest-agnostic overload. The planner
        // plans a debit from an ARBITRARY required-amounts manifest (the
        // per-window PICKUP manifest), not only route.CostManifest. The
        // clamp / short / order rules must be identical to the M1 path.
        // ==============================================================

        // catches: the manifest overload diverging from the CostManifest path -
        // a pickup manifest must clamp available=min(required,stored) with the
        // same short flag the row's requested-on-shortfall keys off.
        [Fact]
        public void ManifestOverload_PlansFromArbitraryManifest_ClampsAndShorts()
        {
            var pickupManifest = new Dictionary<string, double>
            {
                { "Ore", 200.0 },
                { "LiquidFuel", 50.0 },
            };
            var probe = new FakeOriginCargoProbe
            {
                Stored = new Dictionary<string, double>
                {
                    { "Ore", 80.0 },        // short: clamps to 80
                    { "LiquidFuel", 999.0 }, // covered
                },
            };

            var plan = RouteOriginDebitPlanner.PrepareDebit(pickupManifest, probe);

            Assert.Equal(2, plan.Resources.Count);
            Assert.True(plan.IsShort);
            // Ordinal order: LiquidFuel before Ore.
            Assert.Equal("LiquidFuel", plan.Resources[0].Name);
            Assert.Equal(50.0, plan.Resources[0].Available);
            Assert.False(plan.Resources[0].Required > plan.Resources[0].Available);
            Assert.Equal("Ore", plan.Resources[1].Name);
            Assert.Equal(200.0, plan.Resources[1].Required);
            Assert.Equal(80.0, plan.Resources[1].Available); // clamped to stored
        }

        // catches: the manifest overload not sharing the non-positive skip /
        // empty-plan guards with the route path.
        [Fact]
        public void ManifestOverload_SkipsNonPositive_EmptyOnNullOrEmpty()
        {
            var probe = new FakeOriginCargoProbe
            {
                Stored = new Dictionary<string, double> { { "Ore", 100.0 } },
            };

            var withZero = RouteOriginDebitPlanner.PrepareDebit(
                new Dictionary<string, double> { { "Ablator", 0.0 }, { "Ore", 30.0 } }, probe);
            Assert.Single(withZero.Resources);
            Assert.Equal("Ore", withZero.Resources[0].Name);
            Assert.False(withZero.IsShort);

            var nullManifest = RouteOriginDebitPlanner.PrepareDebit((Dictionary<string, double>)null, probe);
            Assert.Empty(nullManifest.Resources);

            var emptyManifest = RouteOriginDebitPlanner.PrepareDebit(new Dictionary<string, double>(), probe);
            Assert.Empty(emptyManifest.Resources);

            var nullProbe = RouteOriginDebitPlanner.PrepareDebit(
                new Dictionary<string, double> { { "Ore", 1.0 } }, null);
            Assert.Empty(nullProbe.Resources);
        }

        // catches: the M1 Route overload no longer delegating to the manifest
        // overload byte-behaviour-identically (the headline Phase 3 invariant -
        // the M1 origin-debit path must be UNCHANGED). Plans the SAME plan from
        // the route as from its CostManifest directly.
        [Fact]
        public void RouteOverload_DelegatesToManifestOverload_ByteIdentical()
        {
            var costManifest = new Dictionary<string, double>
            {
                { "LiquidFuel", 100.0 },
                { "Oxidizer", 120.0 },
            };
            var route = MakeRoute(costManifest);
            var probe = new FakeOriginCargoProbe
            {
                Stored = new Dictionary<string, double>
                {
                    { "LiquidFuel", 40.0 }, // short
                    { "Oxidizer", 500.0 },  // covered
                },
            };
            var probe2 = new FakeOriginCargoProbe
            {
                Stored = new Dictionary<string, double>
                {
                    { "LiquidFuel", 40.0 },
                    { "Oxidizer", 500.0 },
                },
            };

            var viaRoute = RouteOriginDebitPlanner.PrepareDebit(route, probe);
            var viaManifest = RouteOriginDebitPlanner.PrepareDebit(costManifest, probe2);

            Assert.Equal(viaManifest.Resources.Count, viaRoute.Resources.Count);
            Assert.Equal(viaManifest.IsShort, viaRoute.IsShort);
            for (int i = 0; i < viaRoute.Resources.Count; i++)
            {
                Assert.Equal(viaManifest.Resources[i].Name, viaRoute.Resources[i].Name);
                Assert.Equal(viaManifest.Resources[i].Required, viaRoute.Resources[i].Required);
                Assert.Equal(viaManifest.Resources[i].Available, viaRoute.Resources[i].Available);
            }
        }
    }
}
