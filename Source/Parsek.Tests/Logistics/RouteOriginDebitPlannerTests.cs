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

            var nullRoute = RouteOriginDebitPlanner.PrepareDebit(null, probe);
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
    }
}
