using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class InventoryManifestTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public InventoryManifestTests()
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

        #region Helper — build inventory ConfigNodes

        private static ConfigNode MakeStoredPart(string partName, int? quantity = null)
        {
            var sp = new ConfigNode("STOREDPART");
            if (partName != null)
                sp.AddValue("partName", partName);
            if (quantity.HasValue)
                sp.AddValue("quantity", quantity.Value.ToString());
            return sp;
        }

        private static ConfigNode MakeInventoryModule(int inventorySlots, params ConfigNode[] storedParts)
        {
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleInventoryPart");
            module.AddValue("InventorySlots", inventorySlots.ToString());
            var storedPartsNode = module.AddNode("STOREDPARTS");
            foreach (var sp in storedParts)
                storedPartsNode.AddNode(sp);
            return module;
        }

        private static ConfigNode MakePart(params ConfigNode[] modules)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "testPart");
            foreach (var m in modules)
                part.AddNode(m);
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

        #region T11-INV.2 — ExtractInventoryManifest

        [Fact]
        public void ExtractInventoryManifest_NullInput_ReturnsNull()
        {
            var result = VesselSpawner.ExtractInventoryManifest(null, out int totalSlots);

            Assert.Null(result);
            Assert.Equal(0, totalSlots);
        }

        [Fact]
        public void ExtractInventoryManifest_NoInventoryModules_ReturnsNull()
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "fuelTank");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleFuelTank");
            var vessel = MakeVessel(part);

            var result = VesselSpawner.ExtractInventoryManifest(vessel, out int totalSlots);

            Assert.Null(result);
            Assert.Equal(0, totalSlots);
        }

        [Fact]
        public void ExtractInventoryManifest_SingleItem()
        {
            var vessel = MakeVessel(
                MakePart(
                    MakeInventoryModule(9, MakeStoredPart("solarPanels5", 1))));

            var result = VesselSpawner.ExtractInventoryManifest(vessel, out int totalSlots);

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal(1, result["solarPanels5"].count);
            Assert.Equal(1, result["solarPanels5"].slotsTaken);
            Assert.Equal(9, totalSlots);
            Assert.Contains(logLines, l => l.Contains("[Spawner]") && l.Contains("1 item type(s)"));
        }

        [Fact]
        public void ExtractInventoryManifest_MultipleItems()
        {
            var vessel = MakeVessel(
                MakePart(
                    MakeInventoryModule(9,
                        MakeStoredPart("solarPanels5", 1),
                        MakeStoredPart("batteryPack", 1))));

            var result = VesselSpawner.ExtractInventoryManifest(vessel, out int totalSlots);

            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Equal(1, result["solarPanels5"].count);
            Assert.Equal(1, result["solarPanels5"].slotsTaken);
            Assert.Equal(1, result["batteryPack"].count);
            Assert.Equal(1, result["batteryPack"].slotsTaken);
        }

        [Fact]
        public void ExtractInventoryManifest_SameItem_MultipleInventories_Summed()
        {
            var vessel = MakeVessel(
                MakePart(
                    MakeInventoryModule(4, MakeStoredPart("solarPanels5", 1))),
                MakePart(
                    MakeInventoryModule(4, MakeStoredPart("solarPanels5", 1))));

            var result = VesselSpawner.ExtractInventoryManifest(vessel, out int totalSlots);

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal(2, result["solarPanels5"].count);
            Assert.Equal(2, result["solarPanels5"].slotsTaken);
            Assert.Equal(8, totalSlots);
        }

        [Fact]
        public void ExtractInventoryManifest_StackableItem_QuantityRespected()
        {
            var vessel = MakeVessel(
                MakePart(
                    MakeInventoryModule(9, MakeStoredPart("evaRepairKit", 3))));

            var result = VesselSpawner.ExtractInventoryManifest(vessel, out int totalSlots);

            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal(3, result["evaRepairKit"].count);
            Assert.Equal(1, result["evaRepairKit"].slotsTaken);
        }

        [Fact]
        public void ExtractInventoryManifest_EmptyStoredParts_ReturnsNull()
        {
            // Module with empty STOREDPARTS node (no STOREDPART children)
            var vessel = MakeVessel(
                MakePart(
                    MakeInventoryModule(9)));

            var result = VesselSpawner.ExtractInventoryManifest(vessel, out int totalSlots);

            Assert.Null(result);
            Assert.Equal(0, totalSlots);
        }

        [Fact]
        public void ExtractInventoryManifest_MissingPartName_Skipped()
        {
            var badItem = new ConfigNode("STOREDPART");
            // No partName value
            badItem.AddValue("quantity", "1");

            var vessel = MakeVessel(
                MakePart(
                    MakeInventoryModule(9, badItem)));

            var result = VesselSpawner.ExtractInventoryManifest(vessel, out int totalSlots);

            Assert.Null(result);
        }

        [Fact]
        public void ExtractInventoryManifest_MissingQuantity_DefaultsOne()
        {
            var itemNoQty = MakeStoredPart("solarPanels5");

            var vessel = MakeVessel(
                MakePart(
                    MakeInventoryModule(9, itemNoQty)));

            var result = VesselSpawner.ExtractInventoryManifest(vessel, out int totalSlots);

            Assert.NotNull(result);
            Assert.Equal(1, result["solarPanels5"].count);
            Assert.Equal(1, result["solarPanels5"].slotsTaken);
        }

        [Fact]
        public void ExtractInventoryManifest_MultipleInventoryModulesOnOnePart()
        {
            var module1 = MakeInventoryModule(4, MakeStoredPart("solarPanels5", 1));
            var module2 = MakeInventoryModule(4, MakeStoredPart("batteryPack", 2));
            var vessel = MakeVessel(MakePart(module1, module2));

            var result = VesselSpawner.ExtractInventoryManifest(vessel, out int totalSlots);

            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Equal(1, result["solarPanels5"].count);
            Assert.Equal(2, result["batteryPack"].count);
            Assert.Equal(8, totalSlots);
            Assert.Contains(logLines, l => l.Contains("2 inventory module(s)"));
        }

        [Fact]
        public void ExtractInventoryManifest_TotalInventorySlots_Summed()
        {
            var vessel = MakeVessel(
                MakePart(
                    MakeInventoryModule(9, MakeStoredPart("solarPanels5", 1))),
                MakePart(
                    MakeInventoryModule(6, MakeStoredPart("batteryPack", 1))));

            var result = VesselSpawner.ExtractInventoryManifest(vessel, out int totalSlots);

            Assert.NotNull(result);
            Assert.Equal(15, totalSlots);
            Assert.Contains(logLines, l => l.Contains("15 total slot(s)"));
        }

        #endregion

        #region T11-INV.1 — ComputeInventoryDelta

        [Fact]
        public void ComputeInventoryDelta_BothNull_ReturnsNull()
        {
            var delta = InventoryManifest.ComputeInventoryDelta(null, null);

            Assert.Null(delta);
        }

        [Fact]
        public void ComputeInventoryDelta_NormalDelivery()
        {
            var start = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 4, slotsTaken = 4 }
            };
            var end = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 0, slotsTaken = 0 }
            };

            var delta = InventoryManifest.ComputeInventoryDelta(start, end);

            Assert.NotNull(delta);
            Assert.Equal(-4, delta["solarPanels5"].count);
            Assert.Equal(-4, delta["solarPanels5"].slotsTaken);
        }

        [Fact]
        public void ComputeInventoryDelta_ItemGained()
        {
            var start = new Dictionary<string, InventoryItem>();
            var end = new Dictionary<string, InventoryItem>
            {
                ["batteryPack"] = new InventoryItem { count = 3, slotsTaken = 3 }
            };

            var delta = InventoryManifest.ComputeInventoryDelta(start, end);

            Assert.NotNull(delta);
            Assert.Equal(3, delta["batteryPack"].count);
            Assert.Equal(3, delta["batteryPack"].slotsTaken);
        }

        [Fact]
        public void ComputeInventoryDelta_Unchanged()
        {
            var start = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 2, slotsTaken = 2 }
            };
            var end = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 2, slotsTaken = 2 }
            };

            var delta = InventoryManifest.ComputeInventoryDelta(start, end);

            Assert.NotNull(delta);
            Assert.Equal(0, delta["solarPanels5"].count);
            Assert.Equal(0, delta["solarPanels5"].slotsTaken);
        }

        [Fact]
        public void ComputeInventoryDelta_StartNull()
        {
            var end = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 4, slotsTaken = 4 }
            };

            var delta = InventoryManifest.ComputeInventoryDelta(null, end);

            Assert.NotNull(delta);
            Assert.Equal(4, delta["solarPanels5"].count);
            Assert.Equal(4, delta["solarPanels5"].slotsTaken);
        }

        [Fact]
        public void ComputeInventoryDelta_EndNull()
        {
            var start = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 4, slotsTaken = 4 }
            };

            var delta = InventoryManifest.ComputeInventoryDelta(start, null);

            Assert.NotNull(delta);
            Assert.Equal(-4, delta["solarPanels5"].count);
            Assert.Equal(-4, delta["solarPanels5"].slotsTaken);
        }

        [Fact]
        public void ComputeInventoryDelta_SlotDelta_Tracked()
        {
            // Stackable items: count changes but slotsTaken stays the same
            var start = new Dictionary<string, InventoryItem>
            {
                ["evaRepairKit"] = new InventoryItem { count = 5, slotsTaken = 2 }
            };
            var end = new Dictionary<string, InventoryItem>
            {
                ["evaRepairKit"] = new InventoryItem { count = 2, slotsTaken = 1 }
            };

            var delta = InventoryManifest.ComputeInventoryDelta(start, end);

            Assert.NotNull(delta);
            Assert.Equal(-3, delta["evaRepairKit"].count);
            Assert.Equal(-1, delta["evaRepairKit"].slotsTaken);
        }

        #endregion
    }
}
