using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    public class FormatInventoryManifestTests
    {
        [Fact]
        public void BothNull_ReturnsNull()
        {
            var result = RecordingsTableUI.FormatInventoryManifest(null, null);

            Assert.Null(result);
        }

        [Fact]
        public void StartOnly_ShowsStartFormat()
        {
            var start = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 4, slotsTaken = 4 },
                ["batteryPack"] = new InventoryItem { count = 2, slotsTaken = 2 }
            };

            var result = RecordingsTableUI.FormatInventoryManifest(start, null);

            Assert.NotNull(result);
            Assert.StartsWith("Inventory at start:", result);
            Assert.Contains("solarPanels5: 4", result);
            Assert.Contains("batteryPack: 2", result);
        }

        [Fact]
        public void BothStartAndEnd_ShowsDeltaFormat()
        {
            var start = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 4, slotsTaken = 4 },
                ["batteryPack"] = new InventoryItem { count = 2, slotsTaken = 2 }
            };
            var end = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 0, slotsTaken = 0 },
                ["batteryPack"] = new InventoryItem { count = 0, slotsTaken = 0 }
            };

            var result = RecordingsTableUI.FormatInventoryManifest(start, end);

            Assert.NotNull(result);
            Assert.StartsWith("Inventory:", result);
            Assert.Contains("solarPanels5: 4 -> 0 (-4)", result);
            Assert.Contains("batteryPack: 2 -> 0 (-2)", result);
        }

        [Fact]
        public void SingleItem()
        {
            var start = new Dictionary<string, InventoryItem>
            {
                ["solarPanels5"] = new InventoryItem { count = 4, slotsTaken = 4 }
            };

            var result = RecordingsTableUI.FormatInventoryManifest(start, null);

            Assert.NotNull(result);
            Assert.Contains("solarPanels5: 4", result);
            // Only one item line + header
            var lines = result.Split('\n');
            Assert.Equal(2, lines.Length);
        }

        [Fact]
        public void Unchanged_ZeroDelta()
        {
            var start = new Dictionary<string, InventoryItem>
            {
                ["batteryPack"] = new InventoryItem { count = 2, slotsTaken = 2 }
            };
            var end = new Dictionary<string, InventoryItem>
            {
                ["batteryPack"] = new InventoryItem { count = 2, slotsTaken = 2 }
            };

            var result = RecordingsTableUI.FormatInventoryManifest(start, end);

            Assert.NotNull(result);
            Assert.Contains("batteryPack: 2 -> 2 (+0)", result);
        }
    }
}
