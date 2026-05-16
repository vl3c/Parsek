using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the per-dispatch funds-cost arithmetic. Costs are injected as
    /// deterministic dictionaries so we never touch PartLoader / PartResourceLibrary.
    /// </summary>
    [Collection("Sequential")]
    public class RouteFundsCalculatorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteFundsCalculatorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// Build a <c>PART</c> ConfigNode with optional <c>RESOURCE</c> children.
        /// </summary>
        private static ConfigNode MakePart(
            string name,
            params (string resName, double amount)[] resources)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", name);
            foreach (var (resName, amount) in resources)
            {
                var res = part.AddNode("RESOURCE");
                res.AddValue("name", resName);
                res.AddValue("amount", amount.ToString("R", CultureInfo.InvariantCulture));
            }
            return part;
        }

        private static ConfigNode MakeVessel(params ConfigNode[] parts)
        {
            var vessel = new ConfigNode("VESSEL");
            foreach (var p in parts)
                vessel.AddNode(p);
            return vessel;
        }

        // catches: an NRE if a route is created against a degraded recording with no snapshot.
        [Fact]
        public void NullSnapshot_ReturnsZero()
        {
            double cost = RouteFundsCalculator.ComputeDispatchFundsCost(
                vesselSnapshot: null,
                partCostLookup: _ => 100f,
                resourceUnitCostLookup: _ => 1f);

            Assert.Equal(0.0, cost);
        }

        // catches: routes built from snapshot-less recordings inflating costs from stale defaults.
        [Fact]
        public void EmptyPartList_ReturnsZero()
        {
            ConfigNode vessel = new ConfigNode("VESSEL"); // no PART nodes

            double cost = RouteFundsCalculator.ComputeDispatchFundsCost(
                vessel,
                _ => 100f,
                _ => 1f);

            Assert.Equal(0.0, cost);
        }

        // catches: a single-part snapshot returning the wrong stock cost.
        [Fact]
        public void SinglePart_NoResources_ReturnsPartCost()
        {
            ConfigNode vessel = MakeVessel(MakePart("probeCoreOcto2"));
            var partCosts = new Dictionary<string, float>
            {
                { "probeCoreOcto2", 450f },
            };

            double cost = RouteFundsCalculator.ComputeDispatchFundsCost(
                vessel,
                name => partCosts.TryGetValue(name, out float c) ? c : 0f,
                _ => 1f);

            Assert.Equal(450.0, cost, 3);
        }

        // catches: an off-by-one between part cost and resource cost (must sum, not pick one).
        [Fact]
        public void PartPlusResources_SumsCost()
        {
            ConfigNode vessel = MakeVessel(
                MakePart("fuelTank", ("LiquidFuel", 50.0)));
            var partCosts = new Dictionary<string, float> { { "fuelTank", 100f } };
            var resourceCosts = new Dictionary<string, float> { { "LiquidFuel", 0.5f } };

            double cost = RouteFundsCalculator.ComputeDispatchFundsCost(
                vessel,
                name => partCosts.TryGetValue(name, out float c) ? c : 0f,
                name => resourceCosts.TryGetValue(name, out float c) ? c : 0f);

            // 100 (part) + 50 * 0.5 (resource) = 125
            Assert.Equal(125.0, cost, 3);
        }

        // catches: an unknown part silently corrupting the total instead of emitting a warning.
        [Fact]
        public void UnknownPart_TreatedAsZero_WithWarn()
        {
            ConfigNode vessel = MakeVessel(MakePart("nonexistentPart"));

            double cost = RouteFundsCalculator.ComputeDispatchFundsCost(
                vessel,
                _ => 0f,
                _ => 0f);

            Assert.Equal(0.0, cost);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("Unknown part cost")
                && l.Contains("nonexistentPart"));
        }

        // catches: a crash on resources that lack a known unit cost (e.g., mod-added resource).
        [Fact]
        public void MissingResourceCost_TreatedAsZero()
        {
            ConfigNode vessel = MakeVessel(
                MakePart("tank", ("WeirdResource", 100.0)));
            var partCosts = new Dictionary<string, float> { { "tank", 200f } };

            double cost = RouteFundsCalculator.ComputeDispatchFundsCost(
                vessel,
                name => partCosts.TryGetValue(name, out float c) ? c : 0f,
                _ => 0f);

            // Part cost 200, resource cost 0 (unknown), total = 200.
            Assert.Equal(200.0, cost, 3);
        }
    }
}
