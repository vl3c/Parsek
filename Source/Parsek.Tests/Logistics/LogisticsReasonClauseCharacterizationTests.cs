using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Characterization pin over EVERY player-facing Logistics HOLD and REJECT
    /// clause: one case per rendered sentence, comparing the live producer
    /// against a literal written out here by hand.
    ///
    /// Written BEFORE the clause-constant extraction and deliberately NOT
    /// referencing the constants classes: the expected strings are spelled out
    /// in full so the file is an independent record of what the player saw
    /// before the refactor. If an extraction changes one byte of spacing,
    /// punctuation or number formatting, exactly one case here fails and names
    /// the clause.
    ///
    /// Every case runs twice - once under the ambient (host) culture and once
    /// under de-DE - because the numeric clauses (the FundsShort whole-unit
    /// amount, the one-decimal resource shortfall, the re-flyable count) are
    /// contractually InvariantCulture and a comma-locale host is exactly how a
    /// regression would ship unnoticed.
    /// </summary>
    public class LogisticsReasonClauseCharacterizationTests
    {
        private const RouteDispatchEvaluator.EligibilityFailureKind OriginLacksCargo =
            RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo;
        private const RouteDispatchEvaluator.EligibilityFailureKind FundsShort =
            RouteDispatchEvaluator.EligibilityFailureKind.FundsShort;
        private const RouteDispatchEvaluator.EligibilityFailureKind DestinationFull =
            RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull;
        private const RouteDispatchEvaluator.EligibilityFailureKind EndpointLost =
            RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost;
        private const RouteDispatchEvaluator.EligibilityFailureKind SourcesStale =
            RouteDispatchEvaluator.EligibilityFailureKind.SourcesStale;
        private const RouteDispatchEvaluator.EligibilityFailureKind WaitingForPartner =
            RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner;

        // A float-accumulated tank total, so the F1 rendering is the interesting
        // one ("108.8", never "108,8" and never the raw double).
        private const double MeasuredShortfall = 108.79999999999706;

        // ------------------------------------------------------------------
        // The two entry points. Everything below is shared.
        // ------------------------------------------------------------------

        // catches: any byte of a hold/reject clause moving on the host culture.
        [Fact]
        public void EveryClauseRendersItsPinnedText()
        {
            foreach (Case c in AllCases())
                Assert.Equal(c.Expected, c.Actual);
        }

        // catches: a culture-sensitive number or count leaking into a clause
        // (a de-DE host prints "108,8" / "1.235" unless the site is invariant).
        [Fact]
        public void EveryClauseRendersItsPinnedTextUnderDeDe()
        {
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                foreach (Case c in AllCases())
                    Assert.Equal(c.Expected, c.Actual);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        // catches: a clause growing a non-ASCII character (the house style is
        // plain ASCII, and the IMGUI fallback font renders the rest as boxes).
        [Fact]
        public void EveryClauseIsPlainAscii()
        {
            foreach (Case c in AllCases())
            {
                foreach (char ch in c.Actual ?? string.Empty)
                {
                    Assert.True(ch >= 0x20 && ch <= 0x7e,
                        c.Label + " carries a non-ASCII character: " + c.Actual);
                }
            }
        }

        private struct Case
        {
            internal string Label;
            internal string Expected;
            internal string Actual;
        }

        private static Case C(string label, string expected, string actual)
        {
            return new Case { Label = label, Expected = expected, Actual = actual };
        }

        private static IEnumerable<Case> AllCases()
        {
            var cases = new List<Case>();
            AddLongHoldCases(cases);
            AddCompactHoldCases(cases);
            AddFrameCases(cases);
            AddRejectCases(cases);
            return cases;
        }

        // ------------------------------------------------------------------
        // Hold clauses, long form (detail panel + status-cell tooltip):
        // LogisticsHoldPresentation.DescribeHold and its private helpers.
        // ------------------------------------------------------------------

        private static void AddLongHoldCases(List<Case> c)
        {
            // -- FundsShort --
            c.Add(C("FundsShortWithAmount",
                "not enough funds at KSC - short 1235 funds for this dispatch",
                LogisticsHoldPresentation.DescribeHold(FundsShort, "funds-short", 1234.6)));
            c.Add(C("FundsShortGeneric",
                "not enough funds at KSC for this dispatch",
                LogisticsHoldPresentation.DescribeHold(FundsShort, "funds-short", 0.0)));

            // -- DestinationFull --
            c.Add(C("DestinationNoInventorySlotGeneric",
                "destination has no free inventory slot for a stored part"
                    + " - delivers when it has room for the full manifest",
                LogisticsHoldPresentation.DescribeHold(
                    DestinationFull, "destination-full-stored-part:", 0.0)));
            c.Add(C("DestinationNoInventorySlotNamed",
                "destination has no free inventory slot for stored part 'radialDrill'"
                    + " - delivers when it has room for the full manifest",
                LogisticsHoldPresentation.DescribeHold(
                    DestinationFull, "destination-full-stored-part:radialDrill", 0.0)));
            c.Add(C("DestinationNoRoomGeneric",
                "destination has no room for the delivery"
                    + " - delivers when it has room for the full manifest",
                LogisticsHoldPresentation.DescribeHold(DestinationFull, "destination-full-", 0.0)));
            c.Add(C("DestinationNoRoomForResource",
                "destination has no room for LiquidFuel"
                    + " - delivers when it has room for the full manifest",
                LogisticsHoldPresentation.DescribeHold(
                    DestinationFull, "destination-full-LiquidFuel", 0.0)));

            // -- EndpointLost --
            c.Add(C("EndpointLostOrigin",
                "origin vessel could not be found",
                LogisticsHoldPresentation.DescribeHold(EndpointLost, "origin-no-live-vessel", 0.0)));
            c.Add(C("EndpointLostDestination",
                "destination vessel could not be found - re-target or recreate the route",
                LogisticsHoldPresentation.DescribeHold(EndpointLost, "stop-0-no-live-vessels", 0.0)));

            // -- SourcesStale --
            c.Add(C("SourcesStale",
                "route source recordings are unavailable right now",
                LogisticsHoldPresentation.DescribeHold(SourcesStale, "sources-stale", 0.0)));

            // -- WaitingForPartner --
            c.Add(C("WaitingForPartnerGeneric",
                "waiting for the linked route to complete its run",
                LogisticsHoldPresentation.DescribeHold(WaitingForPartner, "partner:", 0.0)));
            c.Add(C("WaitingForPartnerNamed",
                "waiting for the linked route 'Return Leg' to complete its run",
                LogisticsHoldPresentation.DescribeHold(
                    WaitingForPartner, "partner:Return Leg", 0.0)));

            // -- unknown kind / unparsable token --
            c.Add(C("UnknownKindFallback",
                "route is blocked (9999: weird-token)",
                LogisticsHoldPresentation.DescribeHold(
                    (RouteDispatchEvaluator.EligibilityFailureKind)9999, "weird-token", 0.0)));
            c.Add(C("UnknownKindFallbackNoDetail",
                "route is blocked (9999: <none>)",
                LogisticsHoldPresentation.DescribeHold(
                    (RouteDispatchEvaluator.EligibilityFailureKind)9999, null, 0.0)));
            c.Add(C("OriginLacksCargoEmptyTokenFallback",
                "route is blocked (OriginLacksCargo: <none>)",
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, null, 0.0)));

            // -- OriginLacksCargo: origin side --
            c.Add(C("OriginPickupSourceUnresolved",
                "a pickup source vessel could not be found - it may have moved,"
                    + " been recovered, or been destroyed (pickup-source-unresolved:42)",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "pickup-source-unresolved:42", 0.0)));
            c.Add(C("OriginStoredPartStateChangedGeneric",
                "a stored part at the origin does not match the recorded cargo"
                    + " - its charge, fuel, or contents changed",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "inventory-state:", 0.0)));
            c.Add(C("OriginStoredPartStateChangedNamed",
                "stored part 'scienceBox' at the origin does not match the recorded cargo"
                    + " - its charge, fuel, or contents changed",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "inventory-state:scienceBox", 0.0)));
            c.Add(C("OriginMissingStoredPartGeneric",
                "origin is missing a required stored part - delivers when the origin holds it",
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "inventory:", 0.0)));
            c.Add(C("OriginMissingStoredPartGenericOpaqueTail",
                "origin is missing a required stored part - delivers when the origin holds it",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "inventory:null-stored-counter", 0.0)));
            c.Add(C("OriginMissingStoredPartNamed",
                "origin is missing stored part 'radialDrill' - delivers when the origin holds it",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "inventory:radialDrill", 0.0)));
            c.Add(C("OriginUnresolved",
                "origin vessel could not be found - it may have moved,"
                    + " been recovered, or been destroyed (origin-unresolved:7)",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "origin-unresolved:7", 0.0)));
            c.Add(C("OriginShortOfResource",
                "origin is short 108.8 LiquidFuel - delivers when the origin has the full amount",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "LiquidFuel", MeasuredShortfall)));
            c.Add(C("OriginOutOfResource",
                "origin is out of LiquidFuel - delivers when the origin has the full amount",
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "LiquidFuel", 0.0)));
            // The legacy self-timer token shape wraps the same tail.
            c.Add(C("OriginOutOfResourceLegacyToken",
                "origin is out of LiquidFuel - delivers when the origin has the full amount",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "origin-lacks-LiquidFuel", 0.0)));

            // -- OriginLacksCargo: pickup-source side --
            c.Add(C("PickupSourceUnparsed",
                "a pickup source is missing required cargo"
                    + " - delivers when the source has the full amount",
                LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "source:42", 0.0)));
            c.Add(C("PickupSourceMissingStoredPartGeneric",
                "Depot B is missing a required stored part - delivers when it holds it",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source:42:Depot B:inventory:", 0.0)));
            c.Add(C("PickupSourceMissingStoredPartNamed",
                "Depot B is missing stored part 'radialDrill' - delivers when it holds it",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source:42:Depot B:inventory:radialDrill", 0.0)));
            c.Add(C("PickupSourceMissingCargo",
                "Depot B is missing required cargo - delivers when it has the full amount",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source:42:Depot B:", 0.0)));
            c.Add(C("PickupSourceShortOfResource",
                "Depot B is short 108.8 LiquidFuel - delivers when it has the full amount",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source:42:Depot B:LiquidFuel", MeasuredShortfall)));
            c.Add(C("PickupSourceOutOfResource",
                "Depot B is out of LiquidFuel - delivers when it has the full amount",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source:42:Depot B:LiquidFuel", 0.0)));
            // Unnamed source: the placeholder name is substituted INTO the clause.
            c.Add(C("PickupSourceOutOfResourceUnnamed",
                "a pickup source is out of LiquidFuel - delivers when it has the full amount",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source:42::LiquidFuel", 0.0)));

            // -- OriginLacksCargo: escrow-reserved pickup source --
            c.Add(C("ReservedPickupSourceUnparsed",
                "a pickup source has cargo reserved by another route"
                    + " - delivers when the reservation clears",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source-reserved:42:Depot B", 0.0)));
            c.Add(C("ReservedPickupSourceCargo",
                "Depot B has cargo reserved by route 'Return Leg'"
                    + " - delivers when the reservation clears",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source-reserved:42:Depot B::Return Leg", 0.0)));
            c.Add(C("ReservedPickupSourceResource",
                "Depot B has LiquidFuel reserved by route 'Return Leg'"
                    + " - delivers when the reservation clears",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source-reserved:42:Depot B:LiquidFuel:Return Leg", 0.0)));
            c.Add(C("ReservedPickupSourceResourceUnnamedRoute",
                "Depot B has LiquidFuel reserved by route 'another route'"
                    + " - delivers when the reservation clears",
                LogisticsHoldPresentation.DescribeHold(
                    OriginLacksCargo, "source-reserved:42:Depot B:LiquidFuel:", 0.0)));
        }

        // ------------------------------------------------------------------
        // Hold clauses, compact form (the Status cell):
        // LogisticsHoldPresentation.CompactHold and its private helper.
        // ------------------------------------------------------------------

        private static void AddCompactHoldCases(List<Case> c)
        {
            c.Add(C("CompactFundsShortWithAmount", "short 1235 funds",
                LogisticsHoldPresentation.CompactHold(FundsShort, "funds-short", 1234.6)));
            c.Add(C("CompactFundsShortGeneric", "insufficient funds",
                LogisticsHoldPresentation.CompactHold(FundsShort, "funds-short", 0.0)));

            c.Add(C("CompactDestinationNoInventorySlotGeneric", "no free inventory slot",
                LogisticsHoldPresentation.CompactHold(
                    DestinationFull, "destination-full-stored-part:", 0.0)));
            c.Add(C("CompactDestinationNoInventorySlotNamed", "no slot for 'radialDrill'",
                LogisticsHoldPresentation.CompactHold(
                    DestinationFull, "destination-full-stored-part:radialDrill", 0.0)));
            c.Add(C("CompactDestinationFull", "destination full",
                LogisticsHoldPresentation.CompactHold(DestinationFull, "destination-full-", 0.0)));
            c.Add(C("CompactDestinationNoRoomForResource", "no room for LiquidFuel",
                LogisticsHoldPresentation.CompactHold(
                    DestinationFull, "destination-full-LiquidFuel", 0.0)));

            c.Add(C("CompactEndpointLostOrigin", "origin vessel lost",
                LogisticsHoldPresentation.CompactHold(EndpointLost, "origin-no-live-vessel", 0.0)));
            c.Add(C("CompactEndpointLostDestination", "destination vessel lost",
                LogisticsHoldPresentation.CompactHold(EndpointLost, "stop-0-no-live-vessels", 0.0)));

            c.Add(C("CompactSourcesStale", "source recordings unavailable",
                LogisticsHoldPresentation.CompactHold(SourcesStale, "sources-stale", 0.0)));

            c.Add(C("CompactWaitingForPartnerGeneric", "waiting for linked route",
                LogisticsHoldPresentation.CompactHold(WaitingForPartner, "partner:", 0.0)));
            c.Add(C("CompactWaitingForPartnerNamed", "waiting for 'Return Leg'",
                LogisticsHoldPresentation.CompactHold(
                    WaitingForPartner, "partner:Return Leg", 0.0)));

            c.Add(C("CompactUnknownKindFallback", "blocked (9999)",
                LogisticsHoldPresentation.CompactHold(
                    (RouteDispatchEvaluator.EligibilityFailureKind)9999, "weird-token", 0.0)));

            c.Add(C("CompactPickupSourceUnresolved", "pickup source vessel lost",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "pickup-source-unresolved:42", 0.0)));

            c.Add(C("CompactReservedCargoUnparsed", "cargo reserved by another route",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "source-reserved:42:Depot B", 0.0)));
            c.Add(C("CompactReservedCargoNamedRoute", "cargo reserved by 'Return Leg'",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "source-reserved:42:Depot B::Return Leg", 0.0)));
            c.Add(C("CompactReservedResourceNamedRoute", "LiquidFuel reserved by 'Return Leg'",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "source-reserved:42:Depot B:LiquidFuel:Return Leg", 0.0)));

            c.Add(C("CompactPickupSourceUnparsed", "pickup source short of cargo",
                LogisticsHoldPresentation.CompactHold(OriginLacksCargo, "source:42", 0.0)));
            c.Add(C("CompactPickupSourceMissingStoredPartGeneric", "Depot B missing a stored part",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "source:42:Depot B:inventory:", 0.0)));
            c.Add(C("CompactPickupSourceMissingStoredPartNamed", "Depot B missing 'radialDrill'",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "source:42:Depot B:inventory:radialDrill", 0.0)));
            c.Add(C("CompactPickupSourceShortOfCargo", "Depot B short of cargo",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "source:42:Depot B:", 0.0)));
            c.Add(C("CompactPickupSourceShortOfResource", "Depot B short 108.8 LiquidFuel",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "source:42:Depot B:LiquidFuel", MeasuredShortfall)));
            c.Add(C("CompactPickupSourceOutOfResource", "Depot B out of LiquidFuel",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "source:42:Depot B:LiquidFuel", 0.0)));
            c.Add(C("CompactPickupSourceOutOfResourceUnnamed", "pickup source out of LiquidFuel",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "source:42::LiquidFuel", 0.0)));

            c.Add(C("CompactOriginStoredPartStateChangedGeneric", "stored part state differs at origin",
                LogisticsHoldPresentation.CompactHold(OriginLacksCargo, "inventory-state:", 0.0)));
            c.Add(C("CompactOriginStoredPartStateChangedNamed", "'scienceBox' state differs at origin",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "inventory-state:scienceBox", 0.0)));
            c.Add(C("CompactOriginMissingStoredPartGeneric", "origin missing a stored part",
                LogisticsHoldPresentation.CompactHold(OriginLacksCargo, "inventory:", 0.0)));
            c.Add(C("CompactOriginMissingStoredPartNamed", "origin missing 'radialDrill'",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "inventory:radialDrill", 0.0)));
            // The origin-unresolved arm shares EndpointLost-origin's compact words.
            c.Add(C("CompactOriginUnresolved", "origin vessel lost",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "origin-unresolved:7", 0.0)));
            c.Add(C("CompactOriginShortOfCargo", "origin short of cargo",
                LogisticsHoldPresentation.CompactHold(OriginLacksCargo, null, 0.0)));
            c.Add(C("CompactOriginShortOfResource", "origin short 108.8 LiquidFuel",
                LogisticsHoldPresentation.CompactHold(
                    OriginLacksCargo, "LiquidFuel", MeasuredShortfall)));
            c.Add(C("CompactOriginOutOfResource", "origin out of LiquidFuel",
                LogisticsHoldPresentation.CompactHold(OriginLacksCargo, "LiquidFuel", 0.0)));
        }

        // ------------------------------------------------------------------
        // The frames a clause is rendered INSIDE (detail-panel lines, the
        // Status-cell marker, the Send Once toast fallback).
        // ------------------------------------------------------------------

        private static void AddFrameCases(List<Case> c)
        {
            c.Add(C("HoldDetailLineWithAge",
                "Last cycle blocked: origin is out of LiquidFuel (checked 10.0m ago)",
                LogisticsHoldPresentation.FormatHoldDetailLine(
                    "origin is out of LiquidFuel", 600.0)));
            c.Add(C("HoldDetailLineNegativeAge",
                "Last cycle blocked: origin is out of LiquidFuel",
                LogisticsHoldPresentation.FormatHoldDetailLine(
                    "origin is out of LiquidFuel", -1.0)));
            c.Add(C("HoldDetailLineUnknownAge",
                "Last cycle blocked: origin is out of LiquidFuel",
                LogisticsHoldPresentation.FormatHoldDetailLine(
                    "origin is out of LiquidFuel", 0.0)));

            c.Add(C("PartialDeliveryLineWithAge",
                "Last delivery was partial: 40.0 of 60.0 LiquidFuel (10.0m ago)",
                LogisticsHoldPresentation.FormatPartialDeliveryLine(
                    "40.0 of 60.0 LiquidFuel", 600.0)));
            c.Add(C("PartialDeliveryLineNegativeAge",
                "Last delivery was partial: 40.0 of 60.0 LiquidFuel",
                LogisticsHoldPresentation.FormatPartialDeliveryLine(
                    "40.0 of 60.0 LiquidFuel", -1.0)));

            c.Add(C("StatusCellHeldPrefix",
                "Held: origin out of LiquidFuel",
                LogisticsHoldPresentation.StatusCellText(OriginLacksCargo, "LiquidFuel", 0.0)));
            // The cell truncates at 60 chars; the tooltip keeps the full clause.
            c.Add(C("StatusCellHeldPrefixTruncated",
                "Held: A Very Long Depot Name That Keeps On Going out of L...",
                LogisticsHoldPresentation.StatusCellText(
                    OriginLacksCargo,
                    "source:42:A Very Long Depot Name That Keeps On Going:LiquidFuel",
                    0.0)));
            c.Add(C("StatusCellTooltipWithHold",
                "Paused - origin is out of LiquidFuel - delivers when the origin has the full amount",
                LogisticsHoldPresentation.StatusCellTooltip(
                    RouteStatus.Paused,
                    LogisticsHoldPresentation.DescribeHold(OriginLacksCargo, "LiquidFuel", 0.0))));

            c.Add(C("SendOnceBlockedNamesTheHold",
                "Send Once: route 'Relay Run' did not run - origin is out of LiquidFuel"
                    + " - delivers when the origin has the full amount - route is now Paused",
                RouteSendOncePresentation.BuildBlockedMessage(
                    "Relay Run", "route-1", OriginLacksCargo, "LiquidFuel", 0.0)));
            c.Add(C("SendOnceBlockedNotEligibleFallback",
                "Send Once: route 'Relay Run' did not run"
                    + " - the route was not eligible to dispatch - route is now Paused",
                RouteSendOncePresentation.BuildBlockedMessage(
                    "Relay Run", "route-1",
                    RouteDispatchEvaluator.EligibilityFailureKind.None, null, 0.0)));
        }

        // ------------------------------------------------------------------
        // Near-miss / candidate REJECT clauses:
        // LogisticsRejectPresentation.DescribeNearMiss ->
        // RouteCreationFormatters.FormatRejectMessage, plus the not-sealed gate.
        // ------------------------------------------------------------------

        private static void AddRejectCases(List<Case> c)
        {
            c.Add(C("RejectNotFullySealedSingular",
                "not fully sealed (1 recording still re-flyable)",
                LogisticsRejectPresentation.DescribeNearMiss(
                    RouteAnalysisStatus.Eligible, true, 1)));
            c.Add(C("RejectNotFullySealedPlural",
                "not fully sealed (1234 recordings still re-flyable)",
                LogisticsRejectPresentation.DescribeNearMiss(
                    RouteAnalysisStatus.Eligible, true, 1234)));

            c.Add(C("RejectEligibleIsBlank",
                string.Empty,
                LogisticsRejectPresentation.DescribeNearMiss(
                    RouteAnalysisStatus.Eligible, false, 0)));

            c.Add(C("RejectMissingRouteProof",
                "Recording has no route proof - log the dock event to enable a Supply Route.",
                Reject(RouteAnalysisStatus.MissingRouteProof)));
            c.Add(C("RejectMultipleConnectionWindows",
                "Two transfers happened at the same recorded time and cannot be ordered."
                    + " Re-record so each dock happens at a distinct moment.",
                Reject(RouteAnalysisStatus.MultipleConnectionWindows)));
            c.Add(C("RejectNoDeliveryManifest",
                "No delivery payload detected - check that cargo actually moved"
                    + " from transport to destination.",
                Reject(RouteAnalysisStatus.NoDeliveryManifest)));
            c.Add(C("RejectMixedPickupDelivery",
                "Unwitnessed inventory gain detected - the transport gained a stored part"
                    + " that the destination did not give it. Stored cargo is counted by kind"
                    + " (part, variant and how full it is), so only a kind the destination"
                    + " visibly gave up can be picked up. Re-record so the picked-up part"
                    + " comes from the destination.",
                Reject(RouteAnalysisStatus.MixedPickupDelivery)));
            c.Add(C("RejectMissingEndpointProof",
                "Endpoint vessel could not be identified at dock time.",
                Reject(RouteAnalysisStatus.MissingEndpointProof)));
            c.Add(C("RejectUndockedStartOrigin",
                "This run starts undocked with cargo already aboard, so the cargo's source"
                    + " was never witnessed. Start the supply run docked to the origin depot,"
                    + " record the mining that produced the cargo, or launch it from KSC.",
                Reject(RouteAnalysisStatus.UndockedStartOrigin)));

            // The four detail-carrying clauses, each in both forms.
            c.Add(C("RejectUntrackedCargoGainNoDetail",
                "The transport gained cargo during this run with no recorded source."
                    + " Only witnessed gains can route: record the mining with the drill"
                    + " or converter running, or re-record without the unexplained gain.",
                Reject(RouteAnalysisStatus.UntrackedCargoGain)));
            c.Add(C("RejectUntrackedCargoGainWithDetail",
                "The transport gained cargo during this run with no recorded source"
                    + " (Ore: 120.0 gained, 100.0 harvested)."
                    + " Only witnessed gains can route: record the mining with the drill"
                    + " or converter running, or re-record without the unexplained gain.",
                Reject(RouteAnalysisStatus.UntrackedCargoGain, "Ore: 120.0 gained, 100.0 harvested")));

            c.Add(C("RejectFlowDoesNotCloseNoDetail",
                "This run's cargo does not add up: the transport ended with more of a"
                    + " resource than ever arrived. The recorded loads, harvest, and"
                    + " deliveries cannot account for what was left aboard. Re-record so"
                    + " every resource that leaves the transport is matched by a recorded"
                    + " load, harvest, or delivery.",
                Reject(RouteAnalysisStatus.FlowDoesNotClose)));
            c.Add(C("RejectFlowDoesNotCloseWithDetail",
                "This run's cargo does not add up: the transport ended with more of a"
                    + " resource than ever arrived (Ore: 30.0 over-delivered). The recorded"
                    + " loads, harvest, and deliveries cannot account for what was left"
                    + " aboard. Re-record so every resource that leaves the transport is"
                    + " matched by a recorded load, harvest, or delivery.",
                Reject(RouteAnalysisStatus.FlowDoesNotClose, "Ore: 30.0 over-delivered")));

            c.Add(C("RejectMidRecordingStartTrimUnsupportedNoDetail",
                "This run starts between two docks: an earlier docked stretch was recorded"
                    + " before the cargo run, but this shape is not supported yet. A mid-flight"
                    + " start works when the run begins at a fully recorded docked-origin window"
                    + " - dock at the origin depot, then undock, both recorded, before the first"
                    + " delivery dock. Otherwise start the supply run docked at the origin depot,"
                    + " or launch it from KSC.",
                Reject(RouteAnalysisStatus.MidRecordingStartTrimUnsupported)));
            c.Add(C("RejectMidRecordingStartTrimUnsupportedWithDetail",
                "This run starts between two docks: an earlier docked stretch"
                    + " (docked origin recorded at UT 100) was recorded before the cargo run,"
                    + " but this shape is not supported yet. A mid-flight start works when the"
                    + " run begins at a fully recorded docked-origin window - dock at the origin"
                    + " depot, then undock, both recorded, before the first delivery dock."
                    + " Otherwise start the supply run docked at the origin depot, or launch it"
                    + " from KSC.",
                Reject(RouteAnalysisStatus.MidRecordingStartTrimUnsupported,
                    "docked origin recorded at UT 100")));

            c.Add(C("RejectUnsupportedConnectionKindNoDetail",
                "This run's transfer used a connection type Parsek does not support for"
                    + " routes. Docked and claw-grappled transfers are supported.",
                Reject(RouteAnalysisStatus.UnsupportedConnectionKind)));
            c.Add(C("RejectUnsupportedConnectionKindWithDetail",
                "This run's transfer used a connection type Parsek does not support for"
                    + " routes (Weld). Docked and claw-grappled transfers are supported.",
                Reject(RouteAnalysisStatus.UnsupportedConnectionKind, "Weld")));

            c.Add(C("RejectUnknownStatusFallback",
                "Route source is not eligible (9999).",
                Reject((RouteAnalysisStatus)9999)));
        }

        private static string Reject(RouteAnalysisStatus status, string detail = null)
        {
            return LogisticsRejectPresentation.DescribeNearMiss(status, false, 0, detail);
        }
    }
}
