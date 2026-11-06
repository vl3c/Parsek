using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The used / carried clause of a recording row's Status hover
    /// (<see cref="RecordingsTableFormatters.FormatManifestChangeSummary"/>): one line, only
    /// what changed, null when nothing did.
    /// </summary>
    public class ManifestChangeSummaryTests
    {
        private static Dictionary<string, ResourceAmount> Res(params (string name, double amount)[] items)
        {
            var d = new Dictionary<string, ResourceAmount>();
            foreach (var (name, amount) in items)
                d[name] = new ResourceAmount { amount = amount, maxAmount = 10000 };
            return d;
        }

        private static Dictionary<string, InventoryItem> Inv(params (string name, int count)[] items)
        {
            var d = new Dictionary<string, InventoryItem>();
            foreach (var (name, count) in items)
                d[name] = new InventoryItem { count = count, slotsTaken = count };
            return d;
        }

        private static Dictionary<string, int> Crew(params (string trait, int count)[] items)
        {
            var d = new Dictionary<string, int>();
            foreach (var (trait, count) in items)
                d[trait] = count;
            return d;
        }

        private static string Summary(
            Dictionary<string, ResourceAmount> sr = null, Dictionary<string, ResourceAmount> er = null,
            Dictionary<string, InventoryItem> si = null, Dictionary<string, InventoryItem> ei = null,
            Dictionary<string, int> sc = null, Dictionary<string, int> ec = null,
            System.Func<string, string> resourceTitle = null, System.Func<string, string> partTitle = null)
            => RecordingsTableFormatters.FormatManifestChangeSummary(sr, er, si, ei, sc, ec, resourceTitle, partTitle);

        [Fact]
        public void NoManifests_ReturnsNull()
        {
            Assert.Null(Summary());
        }

        [Fact]
        public void StartOnly_ReturnsNull_NothingToCompare()
        {
            // A still-open recording has a start manifest and no end yet.
            Assert.Null(Summary(sr: Res(("LiquidFuel", 3600)), si: Inv(("solarPanels5", 4)),
                sc: Crew(("Pilot", 1))));
        }

        [Fact]
        public void NothingChanged_ReturnsNull()
        {
            Assert.Null(Summary(
                Res(("LiquidFuel", 3600)), Res(("LiquidFuel", 3600)),
                Inv(("solarPanels5", 4)), Inv(("solarPanels5", 4)),
                Crew(("Pilot", 1)), Crew(("Pilot", 1))));
        }

        [Fact]
        public void ResourcesUsedAndGained_NamesOnlyChangedOnes_Sorted()
        {
            string s = Summary(
                Res(("Oxidizer", 4400), ("LiquidFuel", 3600), ("MonoPropellant", 50), ("Ore", 0)),
                Res(("Oxidizer", 250), ("LiquidFuel", 200), ("MonoPropellant", 50), ("Ore", 200)));
            Assert.Equal("Used 3,400 LiquidFuel, 4,150 Oxidizer; gained 200 Ore", s);
        }

        [Fact]
        public void ResourceOnlyOnOneSide_CountsAsZeroOnTheOther()
        {
            string s = Summary(Res(("LiquidFuel", 100)), Res(("Ore", 12.5)));
            Assert.Equal("Used 100 LiquidFuel; gained 12.5 Ore", s);
        }

        [Fact]
        public void FloatNoise_IsNotReportedAsAChange()
        {
            Assert.Null(Summary(Res(("ElectricCharge", 150.0)), Res(("ElectricCharge", 150.004))));
        }

        [Theory]
        [InlineData(3400.4, "3,400")]
        [InlineData(100, "100")]
        [InlineData(12.46, "12.5")]
        [InlineData(1, "1")]
        [InlineData(0.456, "0.46")]
        [InlineData(0.004, null)]
        [InlineData(0, null)]
        public void FormatManifestAmount_Precision(double amount, string expected)
        {
            Assert.Equal(expected, RecordingsTableFormatters.FormatManifestAmount(amount));
        }

        [Fact]
        public void Inventory_UnloadedAndLoaded()
        {
            string s = Summary(
                si: Inv(("solarPanels5", 4), ("evaRepairKit", 1)),
                ei: Inv(("evaRepairKit", 3)));
            Assert.Equal("Unloaded 4 solarPanels5; loaded 2 evaRepairKit", s);
        }

        [Fact]
        public void Crew_SignedPerTrait()
        {
            string s = Summary(
                sc: Crew(("Pilot", 1), ("Engineer", 2)),
                ec: Crew(("Pilot", 1), ("Scientist", 1)));
            Assert.Equal("Crew -2 Engineer, +1 Scientist", s);
        }

        [Fact]
        public void AllKinds_JoinOnOneLine_NoNewline()
        {
            string s = Summary(
                Res(("LiquidFuel", 3600)), Res(("LiquidFuel", 200)),
                Inv(("solarPanels5", 4)), Inv(),
                Crew(("Engineer", 2)), Crew());
            Assert.Equal("Used 3,400 LiquidFuel; unloaded 4 solarPanels5; crew -2 Engineer", s);
            Assert.DoesNotContain("\n", s);
        }

        [Fact]
        public void TitleResolvers_NameThingsForThePlayer_FallBackOnNullOrEmpty()
        {
            string s = Summary(
                Res(("LiquidFuel", 3600), ("Ablator", 50)), Res(("LiquidFuel", 200), ("Ablator", 10)),
                Inv(("solarPanels5", 4)), Inv(),
                resourceTitle: n => n == "LiquidFuel" ? "Liquid Fuel" : "",
                partTitle: n => n == "solarPanels5" ? "OX-STAT Photovoltaic Panels" : null);
            Assert.Equal("Used 40 Ablator, 3,400 Liquid Fuel; unloaded 4 OX-STAT Photovoltaic Panels", s);
        }

        [Fact]
        public void OneSideMissing_SkipsThatKindOnly()
        {
            // Resources have both ends; inventory has no end manifest - only resources report.
            string s = Summary(
                Res(("LiquidFuel", 10)), Res(("LiquidFuel", 4)),
                Inv(("solarPanels5", 4)), null);
            Assert.Equal("Used 6 LiquidFuel", s);
        }
    }
}
