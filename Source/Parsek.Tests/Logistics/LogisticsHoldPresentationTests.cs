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

        // M3 Phase 5 (D7 carve-out lift): the inventory-origin debit is now
        // supported, so a non-KSC origin short of a stored part holds with an
        // "inventory:<identityHash>" short token. The presentation renders a
        // missing-stored-part hold (the opaque hash is not surfaced), NOT the
        // retired "unsupported" deferral text and NOT the raw resource text.
        [Fact]
        public void DescribeHold_OriginMissingStoredPart()
        {
            Assert.Equal(
                "origin is missing a required stored part - delivers when the origin holds it",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "inventory:abc123def456", 0.0));
            // The legacy "origin-lacks-" wrapper strips first, same as the
            // resource and origin-unresolved markers.
            Assert.Equal(
                "origin is missing a required stored part - delivers when the origin holds it",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "origin-lacks-inventory:abc123def456", 0.0));
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

        // M4b Phase B1 (plan D10 / OQ5): the per-PICKUP-SOURCE all-or-nothing gate
        // names the short SOURCE vessel via a "source:<pid>:<name>:<short>" token.
        // catches: the source token rendering only the resource (the player needs
        // to know WHICH depot is short), or a parse that drops the name.
        [Fact]
        public void DescribeHold_PickupSourceShort_NamesSourceVesselAndResource()
        {
            string token = RoutePickupSourceGate.BuildHoldToken(40u, "Depot A", "Ore");
            Assert.Equal(
                "Depot A is out of Ore - delivers when it has the full amount",
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, token, 0.0));
        }

        // catches: an inventory-short pickup source rendering the opaque hash, or
        // not naming the source.
        [Fact]
        public void DescribeHold_PickupSourceInventoryShort_NamesSource()
        {
            string token = RoutePickupSourceGate.BuildHoldToken(50u, "Cargo Bay", "inventory:abc123");
            Assert.Equal(
                "Cargo Bay is missing a required stored part - delivers when it holds it",
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, token, 0.0));
        }

        // M6 escrow-hold legibility: an escrow-caused pickup-source short renders
        // the reserving route by name instead of the lying "X is out of Y" text.
        // catches: the "source-reserved:" token falling through to the plain
        // "source:" branch (or the fallback), or the parse dropping the vessel /
        // resource / route name. Round-trips the REAL emit-site token builder.
        [Fact]
        public void DescribeHold_PickupSourceReserved_NamesVesselResourceAndRoute()
        {
            string token = RoutePickupSourceGate.BuildReservedHoldToken(
                40u, "Depot A", "Ore", "Fuel Run Alpha");
            Assert.Equal(
                "Depot A has Ore reserved by route 'Fuel Run Alpha' - delivers when the reservation clears",
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, token, 0.0));
            // The legacy "origin-lacks-" wrapper strips first, same as every
            // other OriginLacksCargo token.
            Assert.Equal(
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, token, 0.0),
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "origin-lacks-" + token, 0.0));
        }

        // catches: a malformed / truncated "source-reserved:" token throwing or
        // rendering blank instead of the generic reserved-cargo clause, and the
        // sanitized-empty slots rendering broken sentences.
        [Fact]
        public void DescribeHold_PickupSourceReserved_DegradedShapes()
        {
            // Too few parts (no resource / route slots): generic clause.
            Assert.Equal(
                "a pickup source has cargo reserved by another route - delivers when the reservation clears",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source-reserved:12:OnlyName", 0.0));
            // Empty vessel-name slot: generic source noun, named route kept.
            Assert.Equal(
                "a pickup source has Ore reserved by route 'Fuel Run' - delivers when the reservation clears",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source-reserved:12::Ore:Fuel Run", 0.0));
            // Empty route-name slot: generic route noun.
            Assert.Equal(
                "Depot A has Ore reserved by route 'another route' - delivers when the reservation clears",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source-reserved:12:Depot A:Ore:", 0.0));
            // Empty resource slot: reserved-cargo phrasing, both names kept.
            Assert.Equal(
                "Depot A has cargo reserved by route 'Fuel Run' - delivers when the reservation clears",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source-reserved:12:Depot A::Fuel Run", 0.0));
        }

        // catches: an unresolved pickup source losing its raw reason tail.
        [Fact]
        public void DescribeHold_PickupSourceUnresolved()
        {
            string text = LogisticsHoldPresentation.DescribeHold(
                OriginLacksCargo, "pickup-source-unresolved:pid-miss", 0.0);
            Assert.StartsWith(
                "a pickup source vessel could not be found - it may have moved, been recovered, or been destroyed",
                text);
            Assert.Contains("pickup-source-unresolved:pid-miss", text);
        }

        // catches: a source token with an empty name (sanitized "<unnamed>")
        // rendering a broken sentence.
        [Fact]
        public void DescribeHold_PickupSourceShort_UnnamedSource()
        {
            string token = RoutePickupSourceGate.BuildHoldToken(60u, null, "MonoPropellant");
            Assert.Equal(
                "<unnamed> is out of MonoPropellant - delivers when it has the full amount",
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, token, 0.0));
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
            // Doubly-wrapped legacy markers: the legacy WaitResources factory
            // wraps whatever OriginHasCargo returned, INCLUDING the special
            // markers - the prefix strip must run before the marker checks so
            // these render the marker text, not "origin is out of origin-..."
            // (post-implementation review NIT 2).
            Assert.Equal(
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "origin-unresolved:no-live-vessels", 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "origin-lacks-origin-unresolved:no-live-vessels", 0.0));
            Assert.Equal(
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "inventory:abc123def456", 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "origin-lacks-inventory:abc123def456", 0.0));
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

        private const RouteDispatchEvaluator.EligibilityFailureKind WaitingForPartner =
            RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner;

        // M4c Phase C1 (plan D12 / OQ8): the round-trip linking hold names the
        // linked partner. catches: the WaitingForPartner kind rendering the generic
        // fallback instead of a partner-named "waiting for the linked route" clause.
        [Fact]
        public void DescribeHold_WaitingForPartner_NamesPartner()
        {
            Assert.Equal(
                "waiting for the linked route 'Return Run' to complete its run",
                LogisticsHoldPresentation.DescribeHold(WaitingForPartner, "partner:Return Run", 0.0));
        }

        // catches: a WaitingForPartner hold with a missing / malformed partner token
        // rendering blank instead of the generic linked-route clause.
        [Fact]
        public void DescribeHold_WaitingForPartner_NoToken_GenericClause()
        {
            Assert.Equal(
                "waiting for the linked route to complete its run",
                LogisticsHoldPresentation.DescribeHold(WaitingForPartner, "partner:", 0.0));
            Assert.Equal(
                "waiting for the linked route to complete its run",
                LogisticsHoldPresentation.DescribeHold(WaitingForPartner, null, 0.0));
        }

        // catches: a WaitingForPartner hold being suppressed from display on an
        // Active route (the route stays GhostDriving while waiting, so the hold MUST
        // show to answer "why isn't this delivering").
        [Fact]
        public void ShouldDisplayHold_WaitingForPartner_ShowsOnActive()
        {
            Assert.True(LogisticsHoldPresentation.ShouldDisplayHold(
                RouteStatus.Active, WaitingForPartner));
        }

        // ------------------------------------------------------------------
        // StatusCellText / CompactHold (M6 closeout row-level treatment):
        // the compact truncated "Held: <specific reason>" the Status cell
        // shows in place of the generic per-status sentence. One test per
        // token family, mirroring the DescribeHold table above.
        // ------------------------------------------------------------------

        // catches: a "no hold" kind rendering cell text (the draw path keys on
        // null to fall back to the generic StatusReason).
        [Fact]
        public void StatusCellText_None_ReturnsNull()
        {
            Assert.Null(LogisticsHoldPresentation.StatusCellText(None, null, 0.0));
            Assert.Null(LogisticsHoldPresentation.StatusCellText(None, "LiquidFuel", 5.0));
        }

        // catches: the funds cell losing the shortfall number (loop path) or
        // rendering blank on the legacy zero-shortfall capture.
        [Fact]
        public void StatusCellText_Funds_BothShapes()
        {
            Assert.Equal("Held: short 500 funds",
                LogisticsHoldPresentation.StatusCellText(FundsShort, "funds-short", 500.0));
            Assert.Equal("Held: insufficient funds",
                LogisticsHoldPresentation.StatusCellText(FundsShort, "funds-shortfall-99", 0.0));
        }

        // catches: the bare loop-path resource token or the legacy
        // "origin-lacks-" wrapped token not naming the resource in the cell.
        [Fact]
        public void StatusCellText_OriginShort_BothTokenShapes()
        {
            Assert.Equal("Held: origin out of LiquidFuel",
                LogisticsHoldPresentation.StatusCellText(OriginLacksCargo, "LiquidFuel", 0.0));
            Assert.Equal("Held: origin out of LiquidFuel",
                LogisticsHoldPresentation.StatusCellText(OriginLacksCargo, "origin-lacks-LiquidFuel", 0.0));
        }

        // catches: the "source:" pickup-source token losing the SHORT source
        // vessel's name in the cell (which depot matters), or an inventory
        // short rendering the opaque hash.
        [Fact]
        public void StatusCellText_PickupSourceShort_NamesVessel()
        {
            Assert.Equal("Held: Depot A out of Ore",
                LogisticsHoldPresentation.StatusCellText(OriginLacksCargo,
                    RoutePickupSourceGate.BuildHoldToken(40u, "Depot A", "Ore"), 0.0));
            Assert.Equal("Held: Cargo Bay missing a stored part",
                LogisticsHoldPresentation.StatusCellText(OriginLacksCargo,
                    RoutePickupSourceGate.BuildHoldToken(50u, "Cargo Bay", "inventory:abc123"), 0.0));
        }

        // catches: the "source-reserved:" escrow token reading as an empty
        // depot in the cell instead of naming the reserving route.
        [Fact]
        public void StatusCellText_ReservedPickupSource_NamesRoute()
        {
            Assert.Equal("Held: Ore reserved by 'Fuel Run Alpha'",
                LogisticsHoldPresentation.StatusCellText(OriginLacksCargo,
                    RoutePickupSourceGate.BuildReservedHoldToken(40u, "Depot A", "Ore", "Fuel Run Alpha"), 0.0));
            // Malformed token degrades to a generic reserved clause, never blank.
            Assert.Equal("Held: cargo reserved by another route",
                LogisticsHoldPresentation.StatusCellText(OriginLacksCargo,
                    "source-reserved:12:OnlyName", 0.0));
        }

        // catches: the origin inventory short rendering the opaque hash, and
        // the unresolved-origin / unresolved-pickup-source tokens rendering the
        // long clause (the cell names the category, the tooltip carries detail).
        [Fact]
        public void StatusCellText_InventoryAndUnresolvedTokens()
        {
            Assert.Equal("Held: origin missing a stored part",
                LogisticsHoldPresentation.StatusCellText(OriginLacksCargo, "inventory:abc123def456", 0.0));
            Assert.Equal("Held: origin vessel lost",
                LogisticsHoldPresentation.StatusCellText(OriginLacksCargo, "origin-unresolved:pid-miss", 0.0));
            Assert.Equal("Held: pickup source vessel lost",
                LogisticsHoldPresentation.StatusCellText(OriginLacksCargo, "pickup-source-unresolved:pid-miss", 0.0));
        }

        // catches: DestinationFull losing the resource name in the cell.
        [Fact]
        public void StatusCellText_DestinationFull_BothShapes()
        {
            Assert.Equal("Held: no room for Ore",
                LogisticsHoldPresentation.StatusCellText(DestinationFull, "destination-full-Ore", 0.0));
            Assert.Equal("Held: destination full",
                LogisticsHoldPresentation.StatusCellText(DestinationFull, null, 0.0));
        }

        // catches: the endpoint-lost cell not distinguishing origin loss from
        // destination loss ("origin-*" names the origin resolver).
        [Fact]
        public void StatusCellText_EndpointLost_OriginVsDestination()
        {
            Assert.Equal("Held: origin vessel lost",
                LogisticsHoldPresentation.StatusCellText(EndpointLost, "origin-body-unresolved", 0.0));
            Assert.Equal("Held: destination vessel lost",
                LogisticsHoldPresentation.StatusCellText(EndpointLost, "stop-0-no-surface-candidate", 0.0));
        }

        // catches: SourcesStale / WaitingForPartner cells rendering blank.
        [Fact]
        public void StatusCellText_SourcesStaleAndPartner()
        {
            Assert.Equal("Held: source recordings unavailable",
                LogisticsHoldPresentation.StatusCellText(SourcesStale, "sources-stale", 0.0));
            Assert.Equal("Held: waiting for 'Return Run'",
                LogisticsHoldPresentation.StatusCellText(WaitingForPartner, "partner:Return Run", 0.0));
            Assert.Equal("Held: waiting for linked route",
                LogisticsHoldPresentation.StatusCellText(WaitingForPartner, null, 0.0));
        }

        // catches: an unknown future kind rendering blank in the cell (total
        // fallback, mirrors DescribeHold's never-blank contract).
        [Fact]
        public void StatusCellText_UnknownKind_FallbackNeverBlank()
        {
            string cell = LogisticsHoldPresentation.StatusCellText(
                (RouteDispatchEvaluator.EligibilityFailureKind)999, "tok", 0.0);
            Assert.False(string.IsNullOrEmpty(cell));
            Assert.StartsWith("Held: blocked (", cell);
        }

        // catches: a long vessel/route name blowing the cell width - the
        // visible text is hard-capped with "..." while the FULL clause
        // survives in the tooltip built from DescribeHold.
        [Fact]
        public void StatusCellText_LongName_TruncatedWithFullTooltip()
        {
            string longName = new string('X', 80);
            string token = RoutePickupSourceGate.BuildHoldToken(40u, longName, "Ore");

            string cell = LogisticsHoldPresentation.StatusCellText(OriginLacksCargo, token, 0.0);

            Assert.Equal(LogisticsHoldPresentation.StatusCellMaxChars, cell.Length);
            Assert.EndsWith("...", cell);
            Assert.StartsWith("Held: " + longName.Substring(0, 10), cell);

            // The tooltip clause (DescribeHold) keeps the full name.
            string full = LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, token, 0.0);
            Assert.Contains(longName, full);
        }

        // catches: the truncation helper corrupting short strings or
        // mishandling null / degenerate caps.
        [Fact]
        public void TruncateForCell_PassthroughAndCap()
        {
            Assert.Null(LogisticsHoldPresentation.TruncateForCell(null, 60));
            Assert.Equal("", LogisticsHoldPresentation.TruncateForCell("", 60));
            Assert.Equal("short", LogisticsHoldPresentation.TruncateForCell("short", 60));
            Assert.Equal("abcdef", LogisticsHoldPresentation.TruncateForCell("abcdef", 6));
            Assert.Equal("abc...", LogisticsHoldPresentation.TruncateForCell("abcdefg", 6));
            // Degenerate cap (<= 3): passthrough rather than a negative substring.
            Assert.Equal("abcdefg", LogisticsHoldPresentation.TruncateForCell("abcdefg", 3));
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
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "inventory:abc123def456", 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "origin-unresolved:pid-miss-no-surface-fallback", 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, RoutePickupSourceGate.BuildHoldToken(40u, "Depot A", "Ore"), 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, RoutePickupSourceGate.BuildHoldToken(50u, "Cargo Bay", "inventory:abc123"), 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, RoutePickupSourceGate.BuildReservedHoldToken(40u, "Depot A", "Ore", "Fuel Run Alpha"), 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "source-reserved:12:OnlyName", 0.0),
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "pickup-source-unresolved:pid-miss", 0.0),
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
                LogisticsHoldPresentation.DescribeHold(WaitingForPartner, "partner:Return Run", 0.0),
                LogisticsHoldPresentation.DescribeHold(WaitingForPartner, "partner:", 0.0),
                LogisticsHoldPresentation.DescribeHold(WaitingForPartner, null, 0.0),
                LogisticsHoldPresentation.DescribeHold(
                    (RouteDispatchEvaluator.EligibilityFailureKind)999, "tok", 0.0),
                LogisticsHoldPresentation.FormatHoldDetailLine("origin is out of LiquidFuel", 120.0),
                LogisticsHoldPresentation.FormatHoldDetailLine("origin is out of LiquidFuel", -1.0),
                LogisticsHoldPresentation.StatusCellTooltip(
                    RouteStatus.WaitingForFunds, "not enough funds at KSC for this dispatch"),
                // M6 closeout: the compact Status-cell strings ride the same guard.
                LogisticsHoldPresentation.StatusCellText(OriginLacksCargo, "LiquidFuel", 0.0),
                LogisticsHoldPresentation.StatusCellText(OriginLacksCargo,
                    RoutePickupSourceGate.BuildReservedHoldToken(40u, "Depot A", "Ore", "Fuel Run Alpha"), 0.0),
                LogisticsHoldPresentation.StatusCellText(FundsShort, "funds-short", 1234.5),
                LogisticsHoldPresentation.StatusCellText(WaitingForPartner, "partner:Return Run", 0.0),
                LogisticsHoldPresentation.StatusCellText(EndpointLost, "stop-0-no-surface-candidate", 0.0),
                LogisticsHoldPresentation.StatusCellText(
                    (RouteDispatchEvaluator.EligibilityFailureKind)999, "tok", 0.0),
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
