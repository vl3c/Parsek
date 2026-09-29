using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Stock labels and tooltips can hold a bare localization key (the facility menu's
    /// description is "#autoLOC_900122"). KSP localizes a text at render time only when the
    /// whole text is one tag, so Parsek text appended onto a raw key renders the key. Every
    /// composer localizes a '#'-prefixed stock text first (StockUiText.ResolveStockKey).
    /// </summary>
    [Collection("Sequential")]
    public class StockUiTextLocalizationTests : IDisposable
    {
        private const string Key = "#autoLOC_900122";
        private const string English =
            "At the Tracking Station, all ongoing missions can be viewed and focused. Landed craft can be recovered from here as well.";
        private const string Why = "Upgraded on Y1, D06, 14:05, blocked by timeline until then.";

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> localizerCalls = new List<string>();

        public StockUiTextLocalizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            StockUiText.LocalizerForTesting = key =>
            {
                localizerCalls.Add(key);
                return key == Key ? English : key;
            };
        }

        public void Dispose()
        {
            StockUiText.LocalizerForTesting = null;
            ParsekLog.ResetTestOverrides();
        }

        private static string Line(string why)
        {
            return "<color=" + StockUiRnDDecoration.ReasonColorHex + ">" + why + "</color>";
        }

        private int ResolvedLogCount(string site)
        {
            return logLines.Count(l => l.Contains("[StockUiOverlay]")
                && l.Contains(site + ": stock text was localization key '" + Key + "'"));
        }

        [Fact]
        public void ResolveStockKey_SendsAKeyThroughTheLocalizer_AndLeavesPlainTextAlone()
        {
            Assert.Equal(English, StockUiText.ResolveStockKey(Key, "site"));
            Assert.Equal(new[] { Key }, localizerCalls);

            localizerCalls.Clear();
            Assert.Equal("Plain stock text", StockUiText.ResolveStockKey("Plain stock text", "site"));
            Assert.Equal("<b>#1</b>", StockUiText.ResolveStockKey("<b>#1</b>", "site"));
            Assert.Null(StockUiText.ResolveStockKey(null, "site"));
            Assert.Equal("", StockUiText.ResolveStockKey("", "site"));
            Assert.Empty(localizerCalls);
        }

        [Fact]
        public void ResolveStockKey_UnknownKeyEmptyResultOrThrowingLocalizer_KeepTheRawText()
        {
            Assert.Equal("#autoLOC_unknown", StockUiText.ResolveStockKey("#autoLOC_unknown", "site"));

            StockUiText.LocalizerForTesting = key => "";
            Assert.Equal(Key, StockUiText.ResolveStockKey(Key, "site"));

            StockUiText.LocalizerForTesting = key => { throw new InvalidOperationException("no localizer"); };
            Assert.Equal(Key, StockUiText.ResolveStockKey(Key, "throwing site"));
            Assert.Contains(logLines, l => l.Contains("[StockUiOverlay]") && l.Contains("[WARN]")
                && l.Contains("throwing site: stock text key '" + Key + "' could not be localized"));
            Assert.DoesNotContain(logLines, l => l.Contains("stock text was localization key"));
        }

        [Fact]
        public void ResolveStockKey_Headless_WithoutASeam_ReturnsTheKeyAndDoesNotThrow()
        {
            StockUiText.LocalizerForTesting = null;
            Assert.Equal(Key, StockUiText.ResolveStockKey(Key, "headless"));
        }

        [Fact]
        public void AppendReason_OnAKey_AppendsToTheLocalizedText_AndRemoveReasonGivesItBack()
        {
            string composed = StockUiRnDDecoration.AppendReason(Key, Why, "facility menu description");

            Assert.Equal(English + "\n" + Line(Why), composed);
            Assert.DoesNotContain("#autoLOC", composed);
            Assert.Equal(composed, StockUiRnDDecoration.AppendReason(composed, Why, "facility menu description"));
            Assert.Equal(English, StockUiFacilityDecoration.RemoveReason(composed, Why));
        }

        [Fact]
        public void AppendReason_WithoutAReason_LeavesTheKeyForStockToLocalize()
        {
            Assert.Equal(Key, StockUiRnDDecoration.AppendReason(Key, null, "site"));
            Assert.Equal(Key, StockUiRnDDecoration.AppendReason(Key, "", "site"));
            Assert.Empty(localizerCalls);
        }

        [Fact]
        public void ResolvedKey_LogsOncePerSite()
        {
            StockUiRnDDecoration.AppendReason(Key, Why, "facility menu description");
            StockUiRnDDecoration.AppendReason(Key, Why, "facility menu description");
            StockUiRnDDecoration.AppendReason(Key, Why, "R&D node description");

            Assert.Equal(1, ResolvedLogCount("facility menu description"));
            Assert.Equal(1, ResolvedLogCount("R&D node description"));
        }

        [Fact]
        public void TooltipComposers_LocalizeAStockKey_AndAnOwnedTooltipIgnoresStockText()
        {
            string wrapped = StockUiFacilityDecoration.WrapTooltipText(Why);
            Assert.Equal(English + "\n" + Line(wrapped),
                StockUiFacilityDecoration.ComposeTooltipText(false, Key, wrapped, "facility menu button tooltip"));
            Assert.Equal(wrapped, StockUiFacilityDecoration.ComposeTooltipText(true, Key, wrapped));
            Assert.Equal(English + "\n" + Line(wrapped), StockUiReasonTooltip.Compose(false, Key, Why, "Administration tooltip"));
            Assert.Equal(English + "\n" + Line(Why), StockUiFlightCrewDecoration.PortraitTooltip(false, Key, Why));
            Assert.Equal(1, ResolvedLogCount("Administration tooltip"));
        }

        [Fact]
        public void AstronautComposers_LocalizeAStockKey()
        {
            Assert.Equal(English + "\n\n<b>Reserved</b>\n" + Why,
                StockUiAstronautDecoration.AppendTooltip(Key, "Reserved", Why));
            Assert.Equal(English + " (Reserved)", StockUiAstronautDecoration.ComposeAssignedLabel(Key, "Reserved"));
            Assert.Equal("Reserved", StockUiAstronautDecoration.ComposeAssignedLabel(null, "Reserved"));
        }

        [Fact]
        public void MissionControlComposers_LocalizeAStockKeyOnlyWhenAppending()
        {
            var blocked = new StockUiDecoration
            {
                Kind = StockUiDecorationKind.ContractResolution,
                Marked = true,
                Blocked = true,
                Why = Why,
                Title = "Reserved",
            };
            string detail = MissionControlStockAnnotation.ComposeDetailText(Key, blocked);
            Assert.StartsWith(English, detail);
            Assert.DoesNotContain("#autoLOC", detail);
            Assert.EndsWith("\n" + Why, detail);

            string row = MissionControlStockAnnotation.ComposeRowLabel(Key, "Contract", blocked);
            Assert.StartsWith(English, row);
            Assert.DoesNotContain("#autoLOC", row);

            var unmarked = default(StockUiDecoration);
            Assert.Equal(Key, MissionControlStockAnnotation.ComposeDetailText(Key, unmarked));
            Assert.Equal(Key, MissionControlStockAnnotation.ComposeRowLabel(Key, "Contract", unmarked));
        }
    }
}
