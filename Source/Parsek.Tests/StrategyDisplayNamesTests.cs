using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The shared strategy-name resolver behind the Career window's Strategies rows and the
    /// Timeline's strategy rows: stock's title when the live lookup answers, the humanized
    /// config name otherwise, and both windows reading the same answer.
    /// </summary>
    [Collection("Sequential")]
    public class StrategyDisplayNamesTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public StrategyDisplayNamesTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            StrategyDisplayNames.ResetForTesting();
        }

        public void Dispose()
        {
            StrategyDisplayNames.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Theory]
        [InlineData("OutsourcedResearchCfg", "Outsourced Research")]
        [InlineData("AppreciationCampaignCfg", "Appreciation Campaign")]
        [InlineData("RecoveryTransponders", "Recovery Transponders")]
        [InlineData("Cfg", "Cfg")]
        [InlineData("lowercaseModCfg", "Lowercase Mod")]
        [InlineData(null, "unknown")]
        [InlineData("", "unknown")]
        public void HumanizeStrategyId_DropsCfgAndSplitsPascalCase(string id, string expected)
        {
            Assert.Equal(expected, StrategyDisplayNames.HumanizeStrategyId(id));
        }

        [Fact]
        public void Resolve_PrefersTheStockTitle()
        {
            StrategyDisplayNames.TitleLookupForTesting = id => id == "OutsourcedResearchCfg" ? "Outsourced R&D" : null;
            Assert.Equal("Outsourced R&D", StrategyDisplayNames.Resolve("OutsourcedResearchCfg"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("#autoLOC_501158")]
        public void Resolve_FallsBackWhenStockCannotAnswer(string stockAnswer)
        {
            StrategyDisplayNames.TitleLookupForTesting = _ => stockAnswer;
            Assert.Equal("Outsourced Research", StrategyDisplayNames.Resolve("OutsourcedResearchCfg"));
            Assert.Contains(logLines, l => l.Contains("[UI]")
                && l.Contains("strategy title fallback id=OutsourcedResearchCfg"));
        }

        [Fact]
        public void Resolve_ALookupThatThrows_FallsBack()
        {
            StrategyDisplayNames.TitleLookupForTesting = _ => throw new System.InvalidOperationException("boom");
            Assert.Equal("Outsourced Research", StrategyDisplayNames.Resolve("OutsourcedResearchCfg"));
            Assert.Contains(logLines, l => l.Contains("strategy title lookup threw id=OutsourcedResearchCfg"));
        }

        [Fact]
        public void Resolve_Headless_TakesTheFallbackWithoutThrowing()
        {
            // No test lookup: the live StrategySystem read runs and finds no system.
            Assert.Equal("Patents Licensing", StrategyDisplayNames.Resolve("PatentsLicensingCfg"));
        }

        [Fact]
        public void CareerWindowAndTimeline_NameAStrategyIdentically()
        {
            CareerStateWindowUI.StrategyTitleLookupForTesting = id => id == "OutsourcedResearchCfg" ? "Outsourced R&D" : null;
            Assert.Same(StrategyDisplayNames.TitleLookupForTesting, CareerStateWindowUI.StrategyTitleLookupForTesting);
            Assert.Equal("Outsourced R&D", TimelineEntryDisplay.HumanizeStrategyId("OutsourcedResearchCfg"));
            Assert.Equal("Unknown Mod", TimelineEntryDisplay.HumanizeStrategyId("UnknownModCfg"));
        }
    }
}
