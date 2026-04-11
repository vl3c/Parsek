using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class InventoryManifestSerializationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public InventoryManifestSerializationTests()
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

        [Fact]
        public void RoundTrip_BothStartAndEnd()
        {
            var rec = new Recording();
            rec.RecordingId = "test-inv-both";
            rec.StartInventory = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 4, slotsTaken = 4 },
                ["batteryPack"] = new InventoryItem { count = 2, slotsTaken = 2 }
            };
            rec.EndInventory = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 0, slotsTaken = 0 },
                ["batteryPack"] = new InventoryItem { count = 0, slotsTaken = 0 }
            };

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeInventoryManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-inv-both";
            RecordingStore.DeserializeInventoryManifest(node, loaded);

            Assert.NotNull(loaded.StartInventory);
            Assert.NotNull(loaded.EndInventory);
            Assert.Equal(2, loaded.StartInventory.Count);
            Assert.Equal(2, loaded.EndInventory.Count);

            Assert.Equal(4, loaded.StartInventory["solarPanels5"].count);
            Assert.Equal(4, loaded.StartInventory["solarPanels5"].slotsTaken);
            Assert.Equal(0, loaded.EndInventory["solarPanels5"].count);
            Assert.Equal(0, loaded.EndInventory["solarPanels5"].slotsTaken);

            Assert.Equal(2, loaded.StartInventory["batteryPack"].count);
            Assert.Equal(2, loaded.StartInventory["batteryPack"].slotsTaken);
            Assert.Equal(0, loaded.EndInventory["batteryPack"].count);
            Assert.Equal(0, loaded.EndInventory["batteryPack"].slotsTaken);

            Assert.Contains(logLines, l => l.Contains("[RecordingStore]") && l.Contains("wrote 2 item(s)"));
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]") && l.Contains("loaded=2") && l.Contains("skipped=0"));
        }

        [Fact]
        public void RoundTrip_StartOnly()
        {
            var rec = new Recording();
            rec.RecordingId = "test-inv-start";
            rec.StartInventory = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 4, slotsTaken = 4 }
            };
            rec.EndInventory = null;

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeInventoryManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-inv-start";
            RecordingStore.DeserializeInventoryManifest(node, loaded);

            Assert.NotNull(loaded.StartInventory);
            Assert.Null(loaded.EndInventory);
            Assert.Equal(4, loaded.StartInventory["solarPanels5"].count);
            Assert.Equal(4, loaded.StartInventory["solarPanels5"].slotsTaken);
        }

        [Fact]
        public void RoundTrip_NullBoth_NoNodeWritten()
        {
            var rec = new Recording();
            rec.RecordingId = "test-inv-null";
            rec.StartInventory = null;
            rec.EndInventory = null;

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeInventoryManifest(node, rec);

            Assert.Null(node.GetNode("INVENTORY_MANIFEST"));
        }

        [Fact]
        public void RoundTrip_ViaTree()
        {
            var rec = new Recording();
            rec.RecordingId = "test-inv-tree";
            rec.StartInventory = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 4, slotsTaken = 4 }
            };
            rec.EndInventory = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 0, slotsTaken = 0 }
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-inv-tree";
            RecordingTree.LoadRecordingResourceAndState(node, loaded);

            Assert.NotNull(loaded.StartInventory);
            Assert.NotNull(loaded.EndInventory);
            Assert.Equal(4, loaded.StartInventory["solarPanels5"].count);
            Assert.Equal(0, loaded.EndInventory["solarPanels5"].count);
        }

        [Fact]
        public void InventorySlots_RoundTrip()
        {
            var rec = new Recording();
            rec.RecordingId = "test-inv-slots";
            rec.StartInventorySlots = 8;
            rec.EndInventorySlots = 12;
            // Also set inventory dicts so both paths exercise
            rec.StartInventory = new Dictionary<string, InventoryItem>
            {
                ["batteryPack"] = new InventoryItem { count = 2, slotsTaken = 2 }
            };

            // Test via ParsekScenario path
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-inv-slots";
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(8, loaded.StartInventorySlots);
            Assert.Equal(12, loaded.EndInventorySlots);

            // Test via RecordingTree path
            var treeNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingResourceAndState(treeNode, rec);

            var treeLoaded = new Recording();
            treeLoaded.RecordingId = "test-inv-slots-tree";
            RecordingTree.LoadRecordingResourceAndState(treeNode, treeLoaded);

            Assert.Equal(8, treeLoaded.StartInventorySlots);
            Assert.Equal(12, treeLoaded.EndInventorySlots);
        }

        [Fact]
        public void RoundTrip_EndOnly()
        {
            var rec = new Recording();
            rec.RecordingId = "test-inv-end";
            rec.StartInventory = null;
            rec.EndInventory = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 2, slotsTaken = 2 }
            };

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeInventoryManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-inv-end";
            RecordingStore.DeserializeInventoryManifest(node, loaded);

            Assert.Null(loaded.StartInventory);
            Assert.NotNull(loaded.EndInventory);
            Assert.Equal(2, loaded.EndInventory["solarPanels5"].count);
        }

        [Fact]
        public void RoundTrip_EmptyDicts_NoNodeWritten()
        {
            var rec = new Recording();
            rec.RecordingId = "test-inv-empty";
            rec.StartInventory = new Dictionary<string, InventoryItem>();
            rec.EndInventory = new Dictionary<string, InventoryItem>();

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeInventoryManifest(node, rec);

            Assert.Null(node.GetNode("INVENTORY_MANIFEST"));
        }

        [Fact]
        public void RoundTrip_AsymmetricKeys()
        {
            var rec = new Recording();
            rec.RecordingId = "test-inv-asym";
            rec.StartInventory = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 4, slotsTaken = 4 },
                ["batteryPack"] = new InventoryItem { count = 2, slotsTaken = 2 }
            };
            rec.EndInventory = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 1, slotsTaken = 1 },
                ["sensorBarometer"] = new InventoryItem { count = 3, slotsTaken = 3 }
            };

            var node = new ConfigNode("RECORDING");
            RecordingStore.SerializeInventoryManifest(node, rec);

            var loaded = new Recording();
            loaded.RecordingId = "test-inv-asym";
            RecordingStore.DeserializeInventoryManifest(node, loaded);

            Assert.Equal(2, loaded.StartInventory.Count);
            Assert.Equal(2, loaded.EndInventory.Count);
            Assert.True(loaded.StartInventory.ContainsKey("solarPanels5"));
            Assert.True(loaded.StartInventory.ContainsKey("batteryPack"));
            Assert.True(loaded.EndInventory.ContainsKey("solarPanels5"));
            Assert.True(loaded.EndInventory.ContainsKey("sensorBarometer"));
            Assert.False(loaded.StartInventory.ContainsKey("sensorBarometer"));
            Assert.False(loaded.EndInventory.ContainsKey("batteryPack"));
        }

        [Fact]
        public void MalformedItem_Skipped()
        {
            var node = new ConfigNode("RECORDING");
            var manifest = node.AddNode("INVENTORY_MANIFEST");
            var good = manifest.AddNode("ITEM");
            good.AddValue("name", "solarPanels5");
            good.AddValue("startCount", "4");
            good.AddValue("startSlots", "4");
            var bad = manifest.AddNode("ITEM");
            bad.AddValue("name", "");
            bad.AddValue("startCount", "2");

            var loaded = new Recording();
            loaded.RecordingId = "test-inv-malformed";
            RecordingStore.DeserializeInventoryManifest(node, loaded);

            Assert.NotNull(loaded.StartInventory);
            Assert.Single(loaded.StartInventory);
            Assert.Equal(4, loaded.StartInventory["solarPanels5"].count);
            Assert.Contains(logLines, l => l.Contains("loaded=1") && l.Contains("skipped=1"));
        }

        [Fact]
        public void LegacyRecording_NoNode_NullFields()
        {
            var node = new ConfigNode("RECORDING");
            // No INVENTORY_MANIFEST node — simulates legacy recording

            var loaded = new Recording();
            loaded.RecordingId = "test-inv-legacy";
            RecordingStore.DeserializeInventoryManifest(node, loaded);

            Assert.Null(loaded.StartInventory);
            Assert.Null(loaded.EndInventory);
        }
    }
}
