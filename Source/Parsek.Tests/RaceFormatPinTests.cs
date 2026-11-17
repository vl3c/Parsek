using System;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The one pin class for the race formats (multiplayer R1-03; R1-12 and R3E-04 add cells for
    /// the <c>race-link</c> / <c>race-exchange</c> rows and their golden folders). RaceCore may not
    /// name <c>SaveFormat</c>, so its constants and classifier are pinned to it here.
    /// </summary>
    public class RaceFormatPinTests
    {
        [Fact]
        public void ScenarioModuleName_IsTheModuleTypeName()
        {
            string expected = nameof(ParsekRaceScenario);
            Assert.Equal(expected, RaceFormat.ScenarioModuleName);
            Assert.Equal(typeof(ParsekRaceScenario).Name, expected);
        }

        [Fact]
        public void Constants_AreTheFirstBuildsValues()
        {
            Assert.Equal(1, RaceFormat.Baseline);
            Assert.Equal(1, RaceFormat.LinkCurrent);
            Assert.Equal(1, RaceFormat.Current);
            Assert.Equal("SECOND", RaceFormat.DateUnitSecond);
            Assert.Equal("kerbin-equator-1", RaceFormat.SiteTableKerbinEquator1);
            Assert.Equal("standard-1", RaceFormat.LadderStandard1);
        }

        [Fact]
        public void ClassEnum_HasSaveFormatsMembers_InTheSameOrder()
        {
            Assert.Equal(Enum.GetNames(typeof(SaveFormatClass)), Enum.GetNames(typeof(RaceFormatClass)));
            foreach (SaveFormatClass c in Enum.GetValues(typeof(SaveFormatClass)))
                Assert.Equal((int)c, (int)Enum.Parse(typeof(RaceFormatClass), c.ToString()));
        }

        [Theory]
        [InlineData(null, 1)]
        [InlineData(-5, 1)]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(2, 1)]
        [InlineData(int.MaxValue, 1)]
        [InlineData(null, 3)]
        [InlineData(0, 3)]
        [InlineData(1, 3)]
        [InlineData(2, 3)]
        [InlineData(3, 3)]
        [InlineData(4, 3)]
        public void Classify_AgreesWithSaveFormatClassify(int? stored, int current)
        {
            SaveFormatClass expected = SaveFormat.Classify(stored, RaceFormat.Baseline, current);
            RaceFormatClass actual = RaceFormat.Classify(stored, current);
            Assert.Equal(expected.ToString(), actual.ToString());
        }

        [Theory]
        [InlineData(null, "Missing")]
        [InlineData(0, "PreBaseline")]
        [InlineData(1, "Current")]
        [InlineData(2, "Newer")]
        public void Classify_LinkFormat_Table(int? stored, string expected)
        {
            Assert.Equal(expected, RaceFormat.Classify(stored, RaceFormat.LinkCurrent).ToString());
        }

        [Fact]
        public void Classify_Upgradable_BetweenBaselineAndCurrent()
        {
            Assert.Equal(RaceFormatClass.Upgradable, RaceFormat.Classify(2, 3));
        }
    }
}
