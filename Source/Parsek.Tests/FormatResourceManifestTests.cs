using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    public class FormatResourceManifestTests
    {
        [Fact]
        public void BothNull_ReturnsNull()
        {
            var result = RecordingsTableUI.FormatResourceManifest(null, null);

            Assert.Null(result);
        }

        [Fact]
        public void StartOnly_ShowsStartFormat()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 3600, maxAmount = 3600 },
                ["Oxidizer"] = new ResourceAmount { amount = 4400, maxAmount = 4400 }
            };

            var result = RecordingsTableUI.FormatResourceManifest(start, null);

            Assert.NotNull(result);
            Assert.StartsWith("Resources at start:", result);
            Assert.Contains("LiquidFuel: 3600.0 / 3600.0", result);
            Assert.Contains("Oxidizer: 4400.0 / 4400.0", result);
        }

        [Fact]
        public void BothStartAndEnd_ShowsDeltaFormat()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 3600, maxAmount = 3600 },
                ["Oxidizer"] = new ResourceAmount { amount = 4400, maxAmount = 4400 }
            };
            var end = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 200, maxAmount = 3600 },
                ["Oxidizer"] = new ResourceAmount { amount = 244, maxAmount = 4400 }
            };

            var result = RecordingsTableUI.FormatResourceManifest(start, end);

            Assert.NotNull(result);
            Assert.StartsWith("Resources:", result);
            Assert.Contains("LiquidFuel: 3600.0 \u2192 200.0 (-3400.0)", result);
            Assert.Contains("Oxidizer: 4400.0 \u2192 244.0 (-4156.0)", result);
        }

        [Fact]
        public void SingleResource()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["MonoPropellant"] = new ResourceAmount { amount = 30, maxAmount = 50 }
            };

            var result = RecordingsTableUI.FormatResourceManifest(start, null);

            Assert.NotNull(result);
            Assert.Contains("MonoPropellant: 30.0 / 50.0", result);
            // Only one resource line + header
            var lines = result.Split('\n');
            Assert.Equal(2, lines.Length);
        }

        [Fact]
        public void ManyResources_SortedAlphabetically()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["Oxidizer"] = new ResourceAmount { amount = 488, maxAmount = 488 },
                ["Ablator"] = new ResourceAmount { amount = 200, maxAmount = 200 },
                ["MonoPropellant"] = new ResourceAmount { amount = 30, maxAmount = 50 },
                ["LiquidFuel"] = new ResourceAmount { amount = 400, maxAmount = 400 }
            };

            var result = RecordingsTableUI.FormatResourceManifest(start, null);

            Assert.NotNull(result);
            var lines = result.Split('\n');
            Assert.Equal(5, lines.Length); // header + 4 resources
            Assert.Contains("Ablator", lines[1]);
            Assert.Contains("LiquidFuel", lines[2]);
            Assert.Contains("MonoPropellant", lines[3]);
            Assert.Contains("Oxidizer", lines[4]);
        }

        [Fact]
        public void ResourceGained_PositiveDelta()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["Ore"] = new ResourceAmount { amount = 0, maxAmount = 1500 }
            };
            var end = new Dictionary<string, ResourceAmount>
            {
                ["Ore"] = new ResourceAmount { amount = 1500, maxAmount = 1500 }
            };

            var result = RecordingsTableUI.FormatResourceManifest(start, end);

            Assert.NotNull(result);
            Assert.Contains("Ore: 0.0 \u2192 1500.0 (+1500.0)", result);
        }

        [Fact]
        public void ResourceConsumed_NegativeDelta()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 3600, maxAmount = 3600 }
            };
            var end = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 200, maxAmount = 3600 }
            };

            var result = RecordingsTableUI.FormatResourceManifest(start, end);

            Assert.NotNull(result);
            Assert.Contains("LiquidFuel: 3600.0 \u2192 200.0 (-3400.0)", result);
        }

        [Fact]
        public void Unchanged_ZeroDelta()
        {
            var start = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 400, maxAmount = 400 }
            };
            var end = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 400, maxAmount = 400 }
            };

            var result = RecordingsTableUI.FormatResourceManifest(start, end);

            Assert.NotNull(result);
            Assert.Contains("LiquidFuel: 400.0 \u2192 400.0 (+0.0)", result);
        }

        [Fact]
        public void EndOnly_ShowsDeltaWithZeroStart()
        {
            var end = new Dictionary<string, ResourceAmount>
            {
                ["Ore"] = new ResourceAmount { amount = 1500, maxAmount = 1500 }
            };

            var result = RecordingsTableUI.FormatResourceManifest(null, end);

            Assert.NotNull(result);
            Assert.StartsWith("Resources:", result);
            Assert.Contains("Ore: 0.0 \u2192 1500.0 (+1500.0)", result);
        }
    }
}
