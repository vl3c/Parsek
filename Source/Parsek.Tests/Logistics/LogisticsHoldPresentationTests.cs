using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the M6 hold-reason presentation: the pure
    /// <see cref="LogisticsHoldPresentation.DescribeHold"/> kind+token table
    /// (total over BOTH token shapes - loop-path bare tokens and legacy-path
    /// prefixed tokens - plus the never-blank fallback), the
    /// <see cref="LogisticsHoldPresentation.FormatHoldDetailLine"/> age suffix,
    /// the <see cref="LogisticsHoldPresentation.ShouldDisplayHold"/> status
    /// display gate (plan-review MAJOR 2), the status-cell tooltip augmentation,
    /// and the plain-ASCII guard over every produced string.
    /// </summary>
    public class LogisticsHoldPresentationTests
    {
        private const RouteDispatchEvaluator.EligibilityFailureKind None =
            RouteDispatchEvaluator.EligibilityFailureKind.None;
        private const RouteDispatchEvaluator.EligibilityFailureKind SourcesStale =
            RouteDispatchEvaluator.EligibilityFailureKind.SourcesStale;
        private const RouteDispatchEvaluator.EligibilityFailureKind EndpointLost =
            RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost;
        private const RouteDispatchEvaluator.EligibilityFailureKind OriginLacksCargo =
            RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo;
        private const RouteDispatchEvaluator.EligibilityFailureKind FundsShort =
            RouteDispatchEvaluator.EligibilityFailureKind.FundsShort;
        private const RouteDispatchEvaluator.EligibilityFailureKind DestinationFull =
            RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull;

        // ------------------------------------------------------------------
        // DescribeHold: one test per mapping-table row
        // ------------------------------------------------------------------

        // catches: a "no hold" kind rendering text (the draw path keys on null).
        [Fact]
        public void DescribeHold_None_ReturnsNull()
        {
            Assert.Null(LogisticsHoldPresentation.DescribeHold(None, null, 0.0));
            Assert.Null(LogisticsHoldPresentation.DescribeHold(None, "LiquidFuel", 5.0));
        }

        // catches: the loop path's bare resource token not naming the resource.
        [Fact]
        public void DescribeHold_OriginShortResource()
        {
            Assert.Equal(
                "origin is out of LiquidFuel - delivers when the origin has the full amount",
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "LiquidFuel", 0.0));
        }

        // catches: the inventory-unsupported marker degrading to the resource text.
        [Fact]
        public void DescribeHold_InventoryUnsupported()
        {
            Assert.Equal(
                "this route carries stored inventory parts, which docked-origin routes cannot debit yet",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "inventory-origin-debit-unsupported", 0.0));
        }

        // catches: the origin-unresolved marker losing its raw token tail (the
        // log-grep handle must survive into the UI text).
        [Fact]
        public void DescribeHold_OriginUnresolved()
        {
            string text = LogisticsHoldPresentation.DescribeHold(
                OriginLacksCargo, "origin-unresolved:no-live-vessels", 0.0);
            Assert.StartsWith(
                "origin vessel could not be found - it may have moved, been recovered, or been destroyed",
                text);
            Assert.Contains("origin-unresolved:no-live-vessels", text);
        }

        // catches: the funds shortfall number not surfacing (loop path carries it
        // through EligibilityResult.Shortfall), or a comma-locale render.
        [Fact]
        public void DescribeHold_FundsShort_NamesShortfall()
        {
            Assert.Equal(
                "not enough funds at KSC - short 750 funds for this dispatch",
                LogisticsHoldPresentation.DescribeHold(FundsShort, "funds-short", 750.0));
            // Shortfall 0 (legacy capture, or a degenerate result): generic text.
            Assert.Equal(
                "not enough funds at KSC for this dispatch",
                LogisticsHoldPresentation.DescribeHold(FundsShort, "funds-short", 0.0));
        }

        // catches: a named full resource not appearing in the text.
        [Fact]
        public void DescribeHold_DestinationFull_Named()
        {
            Assert.Equal(
                "destination has no room for Ore",
                LogisticsHoldPresentation.DescribeHold(DestinationFull, "Ore", 0.0));
        }

        // catches: an empty token rendering a blank/broken sentence.
        [Fact]
        public void DescribeHold_DestinationFull_Unnamed()
        {
            Assert.Equal(
                "destination has no room for the delivery",
                LogisticsHoldPresentation.DescribeHold(DestinationFull, "", 0.0));
            Assert.Equal(
                "destination has no room for the delivery",
                LogisticsHoldPresentation.DescribeHold(DestinationFull, null, 0.0));
        }

        // catches: the origin-vs-destination split on EndpointLost tokens.
        [Fact]
        public void DescribeHold_EndpointLost_OriginVsStop()
        {
            Assert.Equal(
                "origin vessel could not be found",
                LogisticsHoldPresentation.DescribeHold(
                    EndpointLost, "origin-no-vessel-within-radius", 0.0));
            Assert.Equal(
                "destination vessel could not be found - re-target or recreate the route",
                LogisticsHoldPresentation.DescribeHold(
                    EndpointLost, "stop-0-no-live-vessels", 0.0));
            Assert.Equal(
                "destination vessel could not be found - re-target or recreate the route",
                LogisticsHoldPresentation.DescribeHold(
                    EndpointLost, "endpoint-destroyed-at-delivery:no-live-vessels", 0.0));
            // Unknown/empty token: destination is the safe default.
            Assert.Equal(
                "destination vessel could not be found - re-target or recreate the route",
                LogisticsHoldPresentation.DescribeHold(EndpointLost, null, 0.0));
        }

        // catches: token-shape drift between the loop and legacy paths (risk 1).
        // The legacy path stores PREFIXED decision tokens; both shapes must land
        // on the same player text.
        [Fact]
        public void DescribeHold_LegacyPrefixedTokens()
        {
            // "origin-lacks-X" (legacy) == bare "X" (loop).
            Assert.Equal(
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "LiquidFuel", 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "origin-lacks-LiquidFuel", 0.0));
            // "destination-full-X" (legacy) == bare "X" (loop).
            Assert.Equal(
                LogisticsHoldPresentation.DescribeHold(DestinationFull, "Ore", 0.0),
                LogisticsHoldPresentation.DescribeHold(DestinationFull, "destination-full-Ore", 0.0));
            // "funds-shortfall-N" (legacy, shortfall stored as 0): the generic
            // funds text - the token suffix is deliberately NOT parsed.
            Assert.Equal(
                "not enough funds at KSC for this dispatch",
                LogisticsHoldPresentation.DescribeHold(FundsShort, "funds-shortfall-750", 0.0));
        }

        // catches: the SourcesStale row going blank.
        [Fact]
        public void DescribeHold_SourcesStale()
        {
            Assert.Equal(
                "route source recordings are unavailable right now",
                LogisticsHoldPresentation.DescribeHold(SourcesStale, "sources-stale", 0.0));
            Assert.Equal(
                "route source recordings are unavailable right now",
                LogisticsHoldPresentation.DescribeHold(SourcesStale, "null-env", 0.0));
        }

        // catches: an unknown future kind or a token-less OriginLacksCargo
        // throwing or rendering blank instead of the readable fallback.
        [Fact]
        public void DescribeHold_UnknownKindOrToken_FallsBack()
        {
            var futureKind = (RouteDispatchEvaluator.EligibilityFailureKind)999;
            Assert.Equal(
                "route is blocked (999: some-future-token)",
                LogisticsHoldPresentation.DescribeHold(futureKind, "some-future-token", 0.0));
            Assert.Equal(
                "route is blocked (999: <none>)",
                LogisticsHoldPresentation.DescribeHold(futureKind, null, 0.0));
            // OriginLacksCargo with an empty token cannot name a resource:
            // readable fallback, never a broken "origin is out of " sentence.
            Assert.Equal(
                "route is blocked (OriginLacksCargo: <none>)",
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "", 0.0));
        }

        // ------------------------------------------------------------------
        // Status display gate (plan-review MAJOR 2)
        // ------------------------------------------------------------------

        // catches: a persisted older hold rendering on a MissingSourceRecording /
        // SourceChanged row (those statuses already explain themselves), or the
        // gate suppressing a hold on a status that SHOULD display it.
        [Fact]
        public void ShouldDisplayHold_StatusGate()
        {
            // Source-problem statuses suppress display even with a hold recorded.
            Assert.False(LogisticsHoldPresentation.ShouldDisplayHold(
                RouteStatus.MissingSourceRecording, OriginLacksCargo));
            Assert.False(LogisticsHoldPresentation.ShouldDisplayHold(
                RouteStatus.SourceChanged, OriginLacksCargo));

            // Holds DO display for Active, the wait states, EndpointLost,
            // InTransit, and Paused (keep-on-Pause).
            Assert.True(LogisticsHoldPresentation.ShouldDisplayHold(
                RouteStatus.Active, OriginLacksCargo));
            Assert.True(LogisticsHoldPresentation.ShouldDisplayHold(
                RouteStatus.WaitingForResources, OriginLacksCargo));
            Assert.True(LogisticsHoldPresentation.ShouldDisplayHold(
                RouteStatus.WaitingForFunds, FundsShort));
            Assert.True(LogisticsHoldPresentation.ShouldDisplayHold(
                RouteStatus.DestinationFull, DestinationFull));
            Assert.True(LogisticsHoldPresentation.ShouldDisplayHold(
                RouteStatus.EndpointLost, EndpointLost));
            Assert.True(LogisticsHoldPresentation.ShouldDisplayHold(
                RouteStatus.InTransit, OriginLacksCargo));
            Assert.True(LogisticsHoldPresentation.ShouldDisplayHold(
                RouteStatus.Paused, OriginLacksCargo));

            // No hold recorded: nothing displays on any status.
            Assert.False(LogisticsHoldPresentation.ShouldDisplayHold(
                RouteStatus.Active, None));
            Assert.False(LogisticsHoldPresentation.ShouldDisplayHold(
                RouteStatus.Paused, None));
        }

        // ------------------------------------------------------------------
        // FormatHoldDetailLine
        // ------------------------------------------------------------------

        // catches: the mandatory age suffix being dropped for a known age.
        [Fact]
        public void FormatHoldDetailLine_AppendsAge()
        {
            Assert.Equal(
                "Last cycle blocked: origin is out of LiquidFuel (checked 2.0m ago)",
                LogisticsHoldPresentation.FormatHoldDetailLine(
                    "origin is out of LiquidFuel", 120.0));
        }

        // catches: a negative (unknown) age rendering a bogus "checked - ago".
        [Fact]
        public void FormatHoldDetailLine_OmitsNegativeAge()
        {
            Assert.Equal(
                "Last cycle blocked: origin is out of LiquidFuel",
                LogisticsHoldPresentation.FormatHoldDetailLine(
                    "origin is out of LiquidFuel", -1.0));
            // Age 0 renders "-" through FormatDuration: also omitted.
            Assert.Equal(
                "Last cycle blocked: origin is out of LiquidFuel",
                LogisticsHoldPresentation.FormatHoldDetailLine(
                    "origin is out of LiquidFuel", 0.0));
            // No describe -> no line at all.
            Assert.Null(LogisticsHoldPresentation.FormatHoldDetailLine(null, 120.0));
            Assert.Null(LogisticsHoldPresentation.FormatHoldDetailLine("", 120.0));
        }

        // ------------------------------------------------------------------
        // Status-cell tooltip augmentation
        // ------------------------------------------------------------------

        // catches: the tooltip losing the raw enum name (the pre-M6 contract)
        // or not appending the hold clause on its own line.
        [Fact]
        public void StatusCellTooltip_AppendsHoldOnSecondLine()
        {
            Assert.Equal("Active",
                LogisticsHoldPresentation.StatusCellTooltip(RouteStatus.Active, null));
            Assert.Equal("Active",
                LogisticsHoldPresentation.StatusCellTooltip(RouteStatus.Active, ""));
            Assert.Equal("Active\nnot enough funds at KSC for this dispatch",
                LogisticsHoldPresentation.StatusCellTooltip(
                    RouteStatus.Active, "not enough funds at KSC for this dispatch"));
        }

        // ------------------------------------------------------------------
        // Plain-ASCII guard (no em dashes, no special Unicode)
        // ------------------------------------------------------------------

        // catches: a non-ASCII character (em dash, smart quote) creeping into
        // any player-facing hold string.
        [Fact]
        public void AllHoldStrings_ArePlainAscii()
        {
            var samples = new List<string>
            {
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "LiquidFuel", 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "origin-lacks-LiquidFuel", 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "inventory-origin-debit-unsupported", 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "origin-unresolved:pid-miss-no-surface-fallback", 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "", 0.0),
                LogisticsHoldPresentation.DescribeHold(FundsShort, "funds-short", 1234.5),
                LogisticsHoldPresentation.DescribeHold(FundsShort, "funds-shortfall-99", 0.0),
                LogisticsHoldPresentation.DescribeHold(DestinationFull, "Ore", 0.0),
                LogisticsHoldPresentation.DescribeHold(DestinationFull, "destination-full-Ore", 0.0),
                LogisticsHoldPresentation.DescribeHold(DestinationFull, null, 0.0),
                LogisticsHoldPresentation.DescribeHold(EndpointLost, "origin-body-unresolved", 0.0),
                LogisticsHoldPresentation.DescribeHold(EndpointLost, "stop-0-no-surface-candidate", 0.0),
                LogisticsHoldPresentation.DescribeHold(EndpointLost, "endpoint-destroyed-at-delivery:unknown", 0.0),
                LogisticsHoldPresentation.DescribeHold(SourcesStale, "sources-stale", 0.0),
                LogisticsHoldPresentation.DescribeHold(
                    (RouteDispatchEvaluator.EligibilityFailureKind)999, "tok", 0.0),
                LogisticsHoldPresentation.FormatHoldDetailLine("origin is out of LiquidFuel", 120.0),
                LogisticsHoldPresentation.FormatHoldDetailLine("origin is out of LiquidFuel", -1.0),
                LogisticsHoldPresentation.StatusCellTooltip(
                    RouteStatus.WaitingForFunds, "not enough funds at KSC for this dispatch"),
            };

            foreach (string s in samples)
            {
                Assert.False(string.IsNullOrEmpty(s), "no hold string may be blank");
                foreach (char c in s)
                {
                    Assert.True(c <= 127,
                        $"non-ASCII char U+{(int)c:X4} in: {s}");
                }
            }
        }
    }
}
