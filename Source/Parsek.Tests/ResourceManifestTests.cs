using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ResourceManifestTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ResourceManifestTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region Helper — build RESOURCE node

        private static ConfigNode MakeResource(string name, double amount, double maxAmount)
        {
            var res = new ConfigNode("RESOURCE");
            res.AddValue("name", name);
            res.AddValue("amount", amount.ToString("R", CultureInfo.InvariantCulture));
            res.AddValue("maxAmount", maxAmount.ToString("R", CultureInfo.InvariantCulture));
            return res;
        }

        private static ConfigNode MakePart(params ConfigNode[] resources)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "testPart");
            foreach (var r in resources)
                part.AddNode(r);
            return part;
        }

        private static ConfigNode MakeVessel(params ConfigNode[] parts)
        {
            var vessel = new ConfigNode("VESSEL");
            foreach (var p in parts)
                vessel.AddNode(p);
            return vessel;
        }

        #endregion

        #region T11.1 — ExtractResourceManifest

        [Fact]
        public void ExtractResourceManifest_NullInput_ReturnsNull()
        {
            var result = VesselSpawner.ExtractResourceManifest(null);

            Assert.Null(result);
        }

        [Fact]
        public void ExtractResourceManifest_EmptyVessel_ReturnsNull()
        {
            var vessel = new ConfigNode("VESSEL");

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.Null(result);
        }

        [Fact]
        public void ExtractResourceManifest_SinglePart_SingleResource()
        {
            var vessel = MakeVessel(
                MakePart(MakeResource("LiquidFuel", 400, 400)));

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal(400.0, result["LiquidFuel"].amount);
            Assert.Equal(400.0, result["LiquidFuel"].maxAmount);
            Assert.Contains(logLines, l => l.Contains("[Spawner]") && l.Contains("1 resource type(s)") && l.Contains("1 part(s)"));
        }

        [Fact]
        public void ExtractResourceManifest_SinglePart_MultipleResources()
        {
            var vessel = MakeVessel(
                MakePart(
                    MakeResource("LiquidFuel", 400, 400),
                    MakeResource("Oxidizer", 488, 488)));

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Equal(400.0, result["LiquidFuel"].amount);
            Assert.Equal(488.0, result["Oxidizer"].amount);
        }

        [Fact]
        public void ExtractResourceManifest_MultipleParts_SameResource_Summed()
        {
            var vessel = MakeVessel(
                MakePart(MakeResource("LiquidFuel", 400, 400)),
                MakePart(MakeResource("LiquidFuel", 400, 400)),
                MakePart(MakeResource("LiquidFuel", 400, 400)));

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal(1200.0, result["LiquidFuel"].amount);
            Assert.Equal(1200.0, result["LiquidFuel"].maxAmount);
            Assert.Contains(logLines, l => l.Contains("1 resource type(s)") && l.Contains("3 part(s)"));
        }

        [Fact]
        public void ExtractResourceManifest_MultipleParts_MixedResources()
        {
            var vessel = MakeVessel(
                MakePart(
                    MakeResource("LiquidFuel", 400, 400),
                    MakeResource("Oxidizer", 488, 488)),
                MakePart(
                    MakeResource("LiquidFuel", 200, 400),
                    MakeResource("MonoPropellant", 30, 50)));

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.NotNull(result);
            Assert.Equal(3, result.Count);
            Assert.Equal(600.0, result["LiquidFuel"].amount);
            Assert.Equal(800.0, result["LiquidFuel"].maxAmount);
            Assert.Equal(488.0, result["Oxidizer"].amount);
            Assert.Equal(30.0, result["MonoPropellant"].amount);
        }

        [Fact]
        public void ExtractResourceManifest_ZeroAmountResource_Included()
        {
            var vessel = MakeVessel(
                MakePart(MakeResource("Ore", 0, 1500)));

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.NotNull(result);
            Assert.True(result.ContainsKey("Ore"));
            Assert.Equal(0.0, result["Ore"].amount);
            Assert.Equal(1500.0, result["Ore"].maxAmount);
        }

        [Fact]
        public void ExtractResourceManifest_PartWithNoResources_Skipped()
        {
            var structuralPart = new ConfigNode("PART");
            structuralPart.AddValue("name", "structuralPanel");

            var vessel = MakeVessel(
                structuralPart,
                MakePart(MakeResource("LiquidFuel", 100, 100)));

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal(100.0, result["LiquidFuel"].amount);
        }

        [Fact]
        public void ExtractResourceManifest_MissingAmountField_DefaultsZero()
        {
            var res = new ConfigNode("RESOURCE");
            res.AddValue("name", "LiquidFuel");
            // No amount or maxAmount values
            var vessel = MakeVessel(MakePart(res));

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.NotNull(result);
            Assert.Equal(0.0, result["LiquidFuel"].amount);
            Assert.Equal(0.0, result["LiquidFuel"].maxAmount);
        }

        [Fact]
        public void ExtractResourceManifest_MalformedAmount_DefaultsZero()
        {
            var res = new ConfigNode("RESOURCE");
            res.AddValue("name", "LiquidFuel");
            res.AddValue("amount", "abc");
            res.AddValue("maxAmount", "xyz");
            var vessel = MakeVessel(MakePart(res));

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.NotNull(result);
            Assert.Equal(0.0, result["LiquidFuel"].amount);
            Assert.Equal(0.0, result["LiquidFuel"].maxAmount);
        }

        [Fact]
        public void ExtractResourceManifest_ElectricCharge_Excluded()
        {
            var vessel = MakeVessel(
                MakePart(
                    MakeResource("ElectricCharge", 150, 150),
                    MakeResource("LiquidFuel", 400, 400)));

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.NotNull(result);
            Assert.False(result.ContainsKey("ElectricCharge"));
            Assert.True(result.ContainsKey("LiquidFuel"));
        }

        [Fact]
        public void ExtractResourceManifest_IntakeAir_Excluded()
        {
            var vessel = MakeVessel(
                MakePart(
                    MakeResource("IntakeAir", 1, 5),
                    MakeResource("LiquidFuel", 400, 400)));

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.NotNull(result);
            Assert.False(result.ContainsKey("IntakeAir"));
            Assert.True(result.ContainsKey("LiquidFuel"));
        }

        [Fact]
        public void ExtractResourceManifest_Ablator_Included()
        {
            var vessel = MakeVessel(
                MakePart(MakeResource("Ablator", 200, 200)));

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.NotNull(result);
            Assert.True(result.ContainsKey("Ablator"));
            Assert.Equal(200.0, result["Ablator"].amount);
        }

        [Fact]
        public void ExtractResourceManifest_RoundTrip_Precision()
        {
            double precise = 3600.123456789;
            var vessel = MakeVessel(
                MakePart(MakeResource("LiquidFuel", precise, precise)));

            var result = VesselSpawner.ExtractResourceManifest(vessel);

            Assert.NotNull(result);
            Assert.Equal(precise, result["LiquidFuel"].amount);
            Assert.Equal(precise, result["LiquidFuel"].maxAmount);
        }

        [Fact]
        public void ExtractResourceManifest_VesselSnapshotBuilder_Integration()
        {
            // Build a vessel snapshot via VesselSnapshotBuilder, then add RESOURCE nodes
            // to verify extraction works against a realistic snapshot structure.
            var builder = Parsek.Tests.Generators.VesselSnapshotBuilder.ProbeShip("TestProbe");
            var snapshot = builder.Build();

            // Manually add RESOURCE nodes to the first PART
            var parts = snapshot.GetNodes("PART");
            Assert.True(parts.Length > 0, "VesselSnapshotBuilder should produce at least one PART");

            var lfRes = MakeResource("LiquidFuel", 200, 400);
            var oxRes = MakeResource("Oxidizer", 244, 488);
            parts[0].AddNode(lfRes);
            parts[0].AddNode(oxRes);

            var result = VesselSpawner.ExtractResourceManifest(snapshot);

            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Equal(200.0, result["LiquidFuel"].amount);
            Assert.Equal(400.0, result["LiquidFuel"].maxAmount);
            Assert.Equal(244.0, result["Oxidizer"].amount);
            Assert.Equal(488.0, result["Oxidizer"].maxAmount);
        }

        #endregion

        #region T11.5 — ComputeResourceDelta

        [Fact]
        public void ComputeResourceDelta_NormalConsumption()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 3600, maxAmount = 3600 }
            };
            var end = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 200, maxAmount = 3600 }
            };

            var delta = ResourceManifest.ComputeResourceDelta(start, end);

            Assert.NotNull(delta);
            Assert.Equal(-3400.0, delta["LiquidFuel"]);
        }

        [Fact]
        public void ComputeResourceDelta_ResourceGained()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["Ore"] = new ResourceAmount { amount = 0, maxAmount = 1500 }
            };
            var end = new Dictionary<string, ResourceAmount>
            {
                ["Ore"] = new ResourceAmount { amount = 1500, maxAmount = 1500 }
            };

            var delta = ResourceManifest.ComputeResourceDelta(start, end);

            Assert.NotNull(delta);
            Assert.Equal(1500.0, delta["Ore"]);
        }

        [Fact]
        public void ComputeResourceDelta_MixedGainsAndLosses()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 3600, maxAmount = 3600 },
                ["Ore"] = new ResourceAmount { amount = 0, maxAmount = 1500 }
            };
            var end = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 200, maxAmount = 3600 },
                ["Ore"] = new ResourceAmount { amount = 1500, maxAmount = 1500 }
            };

            var delta = ResourceManifest.ComputeResourceDelta(start, end);

            Assert.NotNull(delta);
            Assert.Equal(-3400.0, delta["LiquidFuel"]);
            Assert.Equal(1500.0, delta["Ore"]);
        }

        [Fact]
        public void ComputeResourceDelta_ResourceOnlyInStart()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 3600, maxAmount = 3600 }
            };
            var end = new Dictionary<string, ResourceAmount>();

            var delta = ResourceManifest.ComputeResourceDelta(start, end);

            Assert.NotNull(delta);
            Assert.Equal(-3600.0, delta["LiquidFuel"]);
        }

        [Fact]
        public void ComputeResourceDelta_ResourceOnlyInEnd()
        {
            var start = new Dictionary<string, ResourceAmount>();
            var end = new Dictionary<string, ResourceAmount>
            {
                ["Ore"] = new ResourceAmount { amount = 1500, maxAmount = 1500 }
            };

            var delta = ResourceManifest.ComputeResourceDelta(start, end);

            Assert.NotNull(delta);
            Assert.Equal(1500.0, delta["Ore"]);
        }

        [Fact]
        public void ComputeResourceDelta_Unchanged()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 400, maxAmount = 400 }
            };
            var end = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 400, maxAmount = 400 }
            };

            var delta = ResourceManifest.ComputeResourceDelta(start, end);

            Assert.NotNull(delta);
            Assert.Equal(0.0, delta["LiquidFuel"]);
        }

        [Fact]
        public void ComputeResourceDelta_BothNull()
        {
            var delta = ResourceManifest.ComputeResourceDelta(null, null);

            Assert.Null(delta);
        }

        [Fact]
        public void ComputeResourceDelta_StartNull()
        {
            var end = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 200, maxAmount = 3600 }
            };

            var delta = ResourceManifest.ComputeResourceDelta(null, end);

            Assert.NotNull(delta);
            Assert.Equal(200.0, delta["LiquidFuel"]);
        }

        [Fact]
        public void ComputeResourceDelta_EndNull()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 3600, maxAmount = 3600 }
            };

            var delta = ResourceManifest.ComputeResourceDelta(start, null);

            Assert.NotNull(delta);
            Assert.Equal(-3600.0, delta["LiquidFuel"]);
        }

        #endregion
    }
}
