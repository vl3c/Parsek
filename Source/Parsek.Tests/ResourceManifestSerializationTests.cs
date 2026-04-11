using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ResourceManifestSerializationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ResourceManifestSerializationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        #region Resource Manifest Round-Trip

        [Fact]
        public void RoundTrip_BothStartAndEnd()
        {
            var rec = new Recording();
            rec.RecordingId = "test-both";
            rec.StartResources = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 3600, maxAmount = 3600 },
                ["Oxidizer"] = new ResourceAmount { amount = 4400, maxAmount = 4400 }
            };
            rec.EndResources = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 200, maxAmount = 3600 },
                ["Oxidizer"] = new ResourceAmount { amount = 244, maxAmount = 4400 }
            };

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeResourceManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-both";
            RecordingStore.DeserializeResourceManifest(node, loaded);

            Assert.NotNull(loaded.StartResources);
            Assert.NotNull(loaded.EndResources);
            Assert.Equal(2, loaded.StartResources.Count);
            Assert.Equal(2, loaded.EndResources.Count);

            Assert.Equal(3600.0, loaded.StartResources["LiquidFuel"].amount);
            Assert.Equal(3600.0, loaded.StartResources["LiquidFuel"].maxAmount);
            Assert.Equal(200.0, loaded.EndResources["LiquidFuel"].amount);
            Assert.Equal(3600.0, loaded.EndResources["LiquidFuel"].maxAmount);

            Assert.Equal(4400.0, loaded.StartResources["Oxidizer"].amount);
            Assert.Equal(4400.0, loaded.StartResources["Oxidizer"].maxAmount);
            Assert.Equal(244.0, loaded.EndResources["Oxidizer"].amount);
            Assert.Equal(4400.0, loaded.EndResources["Oxidizer"].maxAmount);

            Assert.Contains(logLines, l => l.Contains("[RecordingStore]") && l.Contains("wrote 2 resource(s)"));
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]") && l.Contains("loaded=2") && l.Contains("skipped=0"));
        }

        [Fact]
        public void RoundTrip_StartOnly()
        {
            var rec = new Recording();
            rec.RecordingId = "test-start";
            rec.StartResources = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 3600, maxAmount = 3600 }
            };
            rec.EndResources = null;

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeResourceManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-start";
            RecordingStore.DeserializeResourceManifest(node, loaded);

            Assert.NotNull(loaded.StartResources);
            Assert.Null(loaded.EndResources);
            Assert.Equal(3600.0, loaded.StartResources["LiquidFuel"].amount);
        }

        [Fact]
        public void RoundTrip_EndOnly()
        {
            var rec = new Recording();
            rec.RecordingId = "test-end";
            rec.StartResources = null;
            rec.EndResources = new Dictionary<string, ResourceAmount>
            {
                ["Ore"] = new ResourceAmount { amount = 1500, maxAmount = 1500 }
            };

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeResourceManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-end";
            RecordingStore.DeserializeResourceManifest(node, loaded);

            Assert.Null(loaded.StartResources);
            Assert.NotNull(loaded.EndResources);
            Assert.Equal(1500.0, loaded.EndResources["Ore"].amount);
        }

        [Fact]
        public void RoundTrip_NullBoth_NoNodeWritten()
        {
            var rec = new Recording();
            rec.RecordingId = "test-null";
            rec.StartResources = null;
            rec.EndResources = null;

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeResourceManifest(node, rec);

            Assert.Null(node.GetNode("RESOURCE_MANIFEST"));
        }

        [Fact]
        public void RoundTrip_EmptyDicts_NoNodeWritten()
        {
            var rec = new Recording();
            rec.RecordingId = "test-empty";
            rec.StartResources = new Dictionary<string, ResourceAmount>();
            rec.EndResources = new Dictionary<string, ResourceAmount>();

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeResourceManifest(node, rec);

            Assert.Null(node.GetNode("RESOURCE_MANIFEST"));
        }

        [Fact]
        public void RoundTrip_AsymmetricKeys()
        {
            var rec = new Recording();
            rec.RecordingId = "test-asym";
            rec.StartResources = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 3600, maxAmount = 3600 },
                ["Oxidizer"] = new ResourceAmount { amount = 4400, maxAmount = 4400 }
            };
            rec.EndResources = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 200, maxAmount = 3600 },
                ["Ore"] = new ResourceAmount { amount = 1500, maxAmount = 1500 }
            };

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeResourceManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-asym";
            RecordingStore.DeserializeResourceManifest(node, loaded);

            Assert.NotNull(loaded.StartResources);
            Assert.NotNull(loaded.EndResources);

            // LiquidFuel: in both start and end
            Assert.True(loaded.StartResources.ContainsKey("LiquidFuel"));
            Assert.True(loaded.EndResources.ContainsKey("LiquidFuel"));

            // Oxidizer: only in start
            Assert.True(loaded.StartResources.ContainsKey("Oxidizer"));
            Assert.False(loaded.EndResources.ContainsKey("Oxidizer"));

            // Ore: only in end
            Assert.False(loaded.StartResources.ContainsKey("Ore"));
            Assert.True(loaded.EndResources.ContainsKey("Ore"));

            // Merged key set = 3 resources
            Assert.Contains(logLines, l => l.Contains("wrote 3 resource(s)"));
        }

        [Fact]
        public void RoundTrip_Precision()
        {
            double precise = 3600.123456789;
            var rec = new Recording();
            rec.RecordingId = "test-precision";
            rec.StartResources = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = precise, maxAmount = precise }
            };
            rec.EndResources = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = precise / 2, maxAmount = precise }
            };

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeResourceManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-precision";
            RecordingStore.DeserializeResourceManifest(node, loaded);

            Assert.Equal(precise, loaded.StartResources["LiquidFuel"].amount);
            Assert.Equal(precise, loaded.StartResources["LiquidFuel"].maxAmount);
            Assert.Equal(precise / 2, loaded.EndResources["LiquidFuel"].amount);
            Assert.Equal(precise, loaded.EndResources["LiquidFuel"].maxAmount);
        }

        [Fact]
        public void LocaleSafety()
        {
            // Save under a comma-decimal locale, load under invariant
            var rec = new Recording();
            rec.RecordingId = "test-locale";
            rec.StartResources = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 1234.5678, maxAmount = 9999.1234 }
            };

            var node = new ConfigNode("RECORDING");

            var savedCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                // Simulate comma-decimal locale for serialization
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                RecordingStore.SerializeResourceManifest(node, rec);

                // Verify the node values use dots, not commas
                var manifest = node.GetNode("RESOURCE_MANIFEST");
                Assert.NotNull(manifest);
                var resources = manifest.GetNodes("RESOURCE");
                Assert.Single(resources);
                string startAmountStr = resources[0].GetValue("startAmount");
                Assert.DoesNotContain(",", startAmountStr);

                // Load under invariant culture
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                var loaded = new Recording();
                loaded.RecordingId = "test-locale";
                RecordingStore.DeserializeResourceManifest(node, loaded);

                Assert.Equal(1234.5678, loaded.StartResources["LiquidFuel"].amount);
                Assert.Equal(9999.1234, loaded.StartResources["LiquidFuel"].maxAmount);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = savedCulture;
            }
        }

        [Fact]
        public void LegacyRecording_NoNode_NullFields()
        {
            var node = new ConfigNode("RECORDING");
            // No RESOURCE_MANIFEST node — simulates legacy recording

            var loaded = new Recording();
            loaded.RecordingId = "test-legacy";
            RecordingStore.DeserializeResourceManifest(node, loaded);

            Assert.Null(loaded.StartResources);
            Assert.Null(loaded.EndResources);
        }

        [Fact]
        public void MalformedResource_Skipped()
        {
            var node = new ConfigNode("RECORDING");
            var manifest = node.AddNode("RESOURCE_MANIFEST");

            // Valid resource
            var validRes = manifest.AddNode("RESOURCE");
            validRes.AddValue("name", "LiquidFuel");
            validRes.AddValue("startAmount", "3600");
            validRes.AddValue("startMax", "3600");

            // Malformed: empty name
            var badRes = manifest.AddNode("RESOURCE");
            badRes.AddValue("name", "");
            badRes.AddValue("startAmount", "100");
            badRes.AddValue("startMax", "100");

            // Malformed: missing name
            var noNameRes = manifest.AddNode("RESOURCE");
            noNameRes.AddValue("startAmount", "200");

            var loaded = new Recording();
            loaded.RecordingId = "test-malformed";
            RecordingStore.DeserializeResourceManifest(node, loaded);

            Assert.NotNull(loaded.StartResources);
            Assert.Single(loaded.StartResources);
            Assert.Equal(3600.0, loaded.StartResources["LiquidFuel"].amount);

            Assert.Contains(logLines, l => l.Contains("loaded=1") && l.Contains("skipped=2"));
        }

        #endregion

        #region DockTargetVesselPid Round-Trip

        [Fact]
        public void DockTargetVesselPid_RoundTrip_ViaScenario()
        {
            var rec = new Recording();
            rec.RecordingId = "test-dock";
            rec.DockTargetVesselPid = 12345;

            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-dock";
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(12345u, loaded.DockTargetVesselPid);
        }

        [Fact]
        public void DockTargetVesselPid_LegacyRecording_DefaultsZero()
        {
            var node = new ConfigNode("RECORDING");
            // No dockTargetPid value — simulates legacy recording

            var loaded = new Recording();
            loaded.RecordingId = "test-legacy-dock";
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(0u, loaded.DockTargetVesselPid);
        }

        [Fact]
        public void DockTargetVesselPid_ZeroNotWritten()
        {
            var rec = new Recording();
            rec.RecordingId = "test-dock-zero";
            rec.DockTargetVesselPid = 0;

            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, rec);

            Assert.Null(node.GetValue("dockTargetPid"));
        }

        [Fact]
        public void DockTargetVesselPid_RoundTrip_ViaTree()
        {
            var rec = new Recording();
            rec.RecordingId = "test-dock-tree";
            rec.DockTargetVesselPid = 67890;

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-dock-tree";
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.Equal(67890u, loaded.DockTargetVesselPid);
        }

        #endregion

        #region Resource Manifest via Tree path

        [Fact]
        public void ResourceManifest_RoundTrip_ViaTree()
        {
            var rec = new Recording();
            rec.RecordingId = "test-tree-res";
            rec.StartResources = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 3600, maxAmount = 3600 }
            };
            rec.EndResources = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 200, maxAmount = 3600 }
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-tree-res";
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.NotNull(loaded.StartResources);
            Assert.NotNull(loaded.EndResources);
            Assert.Equal(3600.0, loaded.StartResources["LiquidFuel"].amount);
            Assert.Equal(200.0, loaded.EndResources["LiquidFuel"].amount);
        }

        #endregion
    }
}
