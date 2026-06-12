using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Logistics;
using Parsek.Tests.Generators;
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

        // ---------------------------------------------------------------
        // M2 resource generality (plan D11): CRP-style mod resources priced
        // at their own unit cost through the injected lookup
        // ---------------------------------------------------------------

        // catches: a modded resource's unit cost not flowing into the
        // per-dispatch funds charge (e.g. a stock-name assumption in the walk).
        [Fact]
        public void ComputeDispatchFundsCost_ModdedUnitCost_Charged()
        {
            ConfigNode vessel = MakeVessel(
                MakePart("karboniteTank",
                    (CrpFixtures.Karbonite, 500.0),
                    (CrpFixtures.Uraninite, 10.0)));
            var partCosts = new Dictionary<string, float> { { "karboniteTank", 1000f } };

            double cost = RouteFundsCalculator.ComputeDispatchFundsCost(
                vessel,
                name => partCosts.TryGetValue(name, out float c) ? c : 0f,
                CrpFixtures.UnitCostLookup);

            // 1000 (part) + 500 * 0.04 + 10 * 8 = 1100
            Assert.Equal(1100.0, cost, 3);
        }

        // catches: a zero-cost DEFINED resource (routes normally per the
        // transferability rule) contributing a spurious charge.
        [Fact]
        public void ComputeDispatchFundsCost_ZeroCostResource_AddsNothing()
        {
            ConfigNode vessel = MakeVessel(
                MakePart("oreHold", (CrpFixtures.MetallicOre, 2500.0)));
            var partCosts = new Dictionary<string, float> { { "oreHold", 300f } };

            double cost = RouteFundsCalculator.ComputeDispatchFundsCost(
                vessel,
                name => partCosts.TryGetValue(name, out float c) ? c : 0f,
                CrpFixtures.UnitCostLookup);

            // Part cost only: 2500 units at unit cost 0 add nothing.
            Assert.Equal(300.0, cost, 3);
        }

        // catches: an UNDEFINED resource name (uninstalled mod) charging
        // funds - the production lookup returns 0 for undefined names, so the
        // fixture lookup mirrors that and the charge must stay part-only.
        [Fact]
        public void ComputeDispatchFundsCost_UndefinedResource_CostZero()
        {
            ConfigNode vessel = MakeVessel(
                MakePart("mysteryTank", (CrpFixtures.UninstalledModResource, 750.0)));
            var partCosts = new Dictionary<string, float> { { "mysteryTank", 120f } };

            double cost = RouteFundsCalculator.ComputeDispatchFundsCost(
                vessel,
                name => partCosts.TryGetValue(name, out float c) ? c : 0f,
                CrpFixtures.UnitCostLookup);

            Assert.Equal(120.0, cost, 3);
        }

        // ---------------------------------------------------------------
        // M2 funds basis (plan D9 / OQ1): launch-manifest resource term
        // ---------------------------------------------------------------

        private static Dictionary<string, ResourceAmount> StartManifest(
            params (string name, double amount)[] resources)
        {
            var manifest = new Dictionary<string, ResourceAmount>();
            foreach (var (name, amount) in resources)
                manifest[name] = new ResourceAmount { amount = amount, maxAmount = 1000.0 };
            return manifest;
        }

        // catches: the manifest-basis overload still pricing resources from
        // the stop snapshot's RESOURCE nodes (the dock-arrival leftovers)
        // instead of the launch manifest - burned transit fuel must be
        // charged at its launch amount.
        [Fact]
        public void ComputeDispatchFundsCost_ManifestBasis_UsesStartAmounts()
        {
            // Stop snapshot: 30 LF remaining at dock; launch manifest: 50 LF.
            ConfigNode vessel = MakeVessel(
                MakePart("fuelTank", ("LiquidFuel", 30.0)));
            var partCosts = new Dictionary<string, float> { { "fuelTank", 100f } };
            var resourceCosts = new Dictionary<string, float> { { "LiquidFuel", 0.5f } };

            double cost = RouteFundsCalculator.ComputeDispatchFundsCost(
                vessel,
                StartManifest(("LiquidFuel", 50.0)),
                name => partCosts.TryGetValue(name, out float c) ? c : 0f,
                name => resourceCosts.TryGetValue(name, out float c) ? c : 0f);

            // 100 (part) + 50 * 0.5 (LAUNCH amount, not the 30 left at dock) = 125.
            Assert.Equal(125.0, cost, 3);
        }

        // catches: the null-manifest fallback drifting from the legacy
        // three-argument overload - every pre-M2 recording (and every
        // degraded leg) must keep its exact cost (plan risk 7 containment).
        [Fact]
        public void ComputeDispatchFundsCost_NullManifest_FallsBackToSnapshotWalk()
        {
            ConfigNode vessel = MakeVessel(
                MakePart("fuelTank", ("LiquidFuel", 50.0), ("Oxidizer", 60.0)),
                MakePart("nonexistentPart"));
            var partCosts = new Dictionary<string, float> { { "fuelTank", 100f } };
            var resourceCosts = new Dictionary<string, float>
            {
                { "LiquidFuel", 0.5f },
                { "Oxidizer", 0.2f },
            };
            Func<string, float> partLookup =
                name => partCosts.TryGetValue(name, out float c) ? c : 0f;
            Func<string, float> resourceLookup =
                name => resourceCosts.TryGetValue(name, out float c) ? c : 0f;

            double legacy = RouteFundsCalculator.ComputeDispatchFundsCost(
                vessel, partLookup, resourceLookup);
            int legacyWarnCount = logLines.Count(l => l.Contains("Unknown part cost"));

            double viaNullManifest = RouteFundsCalculator.ComputeDispatchFundsCost(
                vessel, startResourceManifest: null, partLookup, resourceLookup);
            int totalWarnCount = logLines.Count(l => l.Contains("Unknown part cost"));

            // Identical value AND identical log behavior (one unknown-part
            // warn per call, none added or dropped by the fallback hop).
            Assert.Equal(legacy, viaNullManifest, 9);
            Assert.Equal(legacyWarnCount * 2, totalWarnCount);
            // 100 (part) + 50*0.5 + 60*0.2 = 137 on both paths.
            Assert.Equal(137.0, legacy, 3);
        }

        // catches: harvested cargo aboard at dock being billed as
        // KSC-supplied launch cargo - the launch basis prices Ore at its
        // launch amount (zero), violating "Harvested - Debit: none" otherwise.
        [Fact]
        public void ComputeDispatchFundsCost_HarvestedOreNotCharged_OnLaunchBasis()
        {
            // Stop snapshot at the dock boundary: 120 harvested Ore aboard
            // plus 10 LF left in the tank. Launch manifest: 0 Ore, 50 LF.
            ConfigNode vessel = MakeVessel(
                MakePart("oreRig", ("Ore", 120.0), ("LiquidFuel", 10.0)));
            var partCosts = new Dictionary<string, float> { { "oreRig", 300f } };
            var resourceCosts = new Dictionary<string, float>
            {
                { "Ore", 0.02f },
                { "LiquidFuel", 0.5f },
            };

            double cost = RouteFundsCalculator.ComputeDispatchFundsCost(
                vessel,
                StartManifest(("Ore", 0.0), ("LiquidFuel", 50.0)),
                name => partCosts.TryGetValue(name, out float c) ? c : 0f,
                name => resourceCosts.TryGetValue(name, out float c) ? c : 0f);

            // 300 (part) + 0 * 0.02 (harvested Ore free) + 50 * 0.5 (full
            // launch fuel, including the burned 40) = 325.
            Assert.Equal(325.0, cost, 3);
        }
    }
}
