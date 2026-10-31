using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins QW2: the route delete X-button routes through a confirm dialog, and the
    /// dialog body text is built by the pure
    /// <see cref="LogisticsWindowUI.BuildDeleteConfirmBody"/> helper. The IMGUI
    /// dialog spawn itself is not unit-testable, but the body-text + name-fallback
    /// decision is the pure surface this test pins. Unity-free.
    /// </summary>
    public class LogisticsWindowUIDeleteConfirmTests
    {
        [Fact]
        public void NamedRoute_BodyUsesRouteName()
        {
            Route route = new RouteFixtureBuilder()
                .WithId("del-test")
                .WithName("Mun Fuel Run")
                .Build();

            string body = LogisticsWindowUI.BuildDeleteConfirmBody(route);

            Assert.Equal("Delete route 'Mun Fuel Run'?\n\nThis cannot be undone.", body);
        }

        [Fact]
        public void DefaultRouteNameArrow_IsAsciiInTheStockDialogBody()
        {
            // The PopupDialog body renders through the stock TMP font, which has no
            // U+2192 glyph (todo GUI-CENSUS-WAVE6-RESIDUE item 8): the arrow becomes "->".
            Route route = new RouteFixtureBuilder()
                .WithId("arrow-test")
                .WithName("Route: KSC \u2192 Mun")
                .Build();

            string body = LogisticsWindowUI.BuildDeleteConfirmBody(route);

            Assert.Equal("Delete route 'Route: KSC -> Mun'?\n\nThis cannot be undone.", body);
            Assert.Equal(
                "Delete dormant route 'Route: KSC -> Mun'?",
                LogisticsDormantPresentation.BuildDeleteDormantConfirmBody("Route: KSC \u2192 Mun")
                    .Split('\n')[0]);
        }

        [Fact]
        public void StockFontText_ReplacesOnlyTheArrow_AndKeepsNull()
        {
            Assert.Null(StockFontText.ForStockUi(null));
            Assert.Equal("", StockFontText.ForStockUi(""));
            Assert.Equal("Mun Fuel Run", StockFontText.ForStockUi("Mun Fuel Run"));
            Assert.Equal("A -> B -> C", StockFontText.ForStockUi("A \u2192 B \u2192 C"));
        }

        [Fact]
        public void EmptyName_FallsBackToShortId_NotLiteralNull()
        {
            // A route with no display name must still show an identifier (its short
            // id), never the literal text "null".
            Route route = new RouteFixtureBuilder()
                .WithId("abcdef1234567890")
                .WithName(string.Empty)
                .Build();

            string body = LogisticsWindowUI.BuildDeleteConfirmBody(route);

            Assert.Contains("abcdef12", body);
            Assert.DoesNotContain("null", body);
            Assert.Contains("This cannot be undone.", body);
        }

        [Fact]
        public void NullName_FallsBackToShortId()
        {
            Route route = new RouteFixtureBuilder()
                .WithId("zyxwvu9876543210")
                .WithName(null)
                .Build();

            string body = LogisticsWindowUI.BuildDeleteConfirmBody(route);

            Assert.Contains("zyxwvu98", body);
            Assert.DoesNotContain("'null'", body);
        }

        [Fact]
        public void NullRoute_DoesNotThrow_AndProducesBody()
        {
            string body = LogisticsWindowUI.BuildDeleteConfirmBody(null);

            // ShortId(null) yields "<none>"; the helper must not throw and must
            // still produce a well-formed confirmation body.
            Assert.Contains("This cannot be undone.", body);
            Assert.StartsWith("Delete route '", body);
        }
    }
}
