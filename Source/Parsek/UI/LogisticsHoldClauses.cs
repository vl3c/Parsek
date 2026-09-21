using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Every player-facing HOLD clause the Logistics window can render, as
    /// named composite format strings. One constant per rendered sentence;
    /// <see cref="LogisticsHoldPresentation"/> holds the branching that picks
    /// one and supplies its arguments, and nothing else in the program writes
    /// a hold sentence.
    ///
    /// Three groups, in the order they appear here:
    /// LONG FORM - the detail panel's blocked line and the Status-cell tooltip
    /// (<see cref="LogisticsHoldPresentation.DescribeHold"/>), which carry the
    /// "delivers when ..." guidance; COMPACT FORM - the Status cell itself
    /// (<see cref="LogisticsHoldPresentation.CompactHold"/>), which drops the
    /// guidance so the 240 px cell stays about two wrapped lines; FRAMES - the
    /// lines a clause is rendered INSIDE.
    ///
    /// NOT here, deliberately: the argument VALUES a clause substitutes when
    /// the token cannot name a thing ("a pickup source", "pickup source",
    /// "another route", "&lt;none&gt;"). Those are fallback nouns dropped into a
    /// hole, not sentences the vocabulary needs to claim; the sentence around
    /// them is what a catalogue state has to be able to produce.
    ///
    /// Extracted with NO behavior change (the clause text, argument order,
    /// spacing, punctuation and number formatting are byte-identical to what
    /// shipped) so the GUI state gallery's completeness guard can enumerate the
    /// vocabulary instead of scraping literals - see
    /// <c>docs/dev/design-gui-state-gallery.md</c> section 11.
    /// </summary>
    internal static class LogisticsHoldClauses
    {
        // ==================================================================
        // LONG FORM - detail panel + status-cell tooltip (DescribeHold)
        // ==================================================================

        /// <summary>FundsShort with a measured shortfall (Career, KSC-origin dispatch).</summary>
        internal const string FundsShortWithAmount =
            "not enough funds at KSC - short {0:F0} funds for this dispatch";

        /// <summary>FundsShort with no measured shortfall (legacy holds persist 0).</summary>
        internal const string FundsShortGeneric =
            "not enough funds at KSC for this dispatch";

        /// <summary>DestinationFull, inventory-slot shortfall whose stored part cannot be named.</summary>
        internal const string DestinationNoInventorySlot =
            "destination has no free inventory slot for a stored part"
            + " - delivers when it has room for the full manifest";

        /// <summary>DestinationFull, inventory-slot shortfall naming the stored part.</summary>
        internal const string DestinationNoInventorySlotForNamedPart =
            "destination has no free inventory slot for stored part '{0}'"
            + " - delivers when it has room for the full manifest";

        /// <summary>DestinationFull with no resource named in the token.</summary>
        internal const string DestinationNoRoomForDelivery =
            "destination has no room for the delivery"
            + " - delivers when it has room for the full manifest";

        /// <summary>DestinationFull naming the resource the destination cannot take.</summary>
        internal const string DestinationNoRoomForResource =
            "destination has no room for {0}"
            + " - delivers when it has room for the full manifest";

        /// <summary>EndpointLost on an "origin-*" token (the origin resolver failed).</summary>
        internal const string OriginVesselNotFound =
            "origin vessel could not be found";

        /// <summary>EndpointLost on any other token (a destination loss).</summary>
        internal const string DestinationVesselNotFound =
            "destination vessel could not be found - re-target or recreate the route";

        /// <summary>SourcesStale: the route's source recordings did not resolve this cycle.</summary>
        internal const string SourceRecordingsUnavailable =
            "route source recordings are unavailable right now";

        /// <summary>WaitingForPartner with no partner name in the token.</summary>
        internal const string WaitingForLinkedRoute =
            "waiting for the linked route to complete its run";

        /// <summary>WaitingForPartner naming the linked round-trip route.</summary>
        internal const string WaitingForNamedLinkedRoute =
            "waiting for the linked route '{0}' to complete its run";

        /// <summary>Total fallback: an unknown failure kind, or a token no arm recognized.</summary>
        internal const string BlockedUnknownKind =
            "route is blocked ({0}: {1})";

        /// <summary>OriginLacksCargo, "pickup-source-unresolved:*": the source vessel is gone.</summary>
        internal const string PickupSourceNotFound =
            "a pickup source vessel could not be found - it may have moved,"
            + " been recovered, or been destroyed ({0})";

        /// <summary>OriginLacksCargo, "inventory-state:" with no part name.</summary>
        internal const string OriginStoredPartStateChanged =
            "a stored part at the origin does not match the recorded cargo"
            + " - its charge, fuel, or contents changed";

        /// <summary>OriginLacksCargo, "inventory-state:&lt;partName&gt;".</summary>
        internal const string OriginNamedStoredPartStateChanged =
            "stored part '{0}' at the origin does not match the recorded cargo"
            + " - its charge, fuel, or contents changed";

        /// <summary>OriginLacksCargo, "inventory:" with an empty or opaque tail (hash / gate marker).</summary>
        internal const string OriginMissingStoredPart =
            "origin is missing a required stored part - delivers when the origin holds it";

        /// <summary>OriginLacksCargo, "inventory:&lt;partName&gt;".</summary>
        internal const string OriginMissingNamedStoredPart =
            "origin is missing stored part '{0}' - delivers when the origin holds it";

        /// <summary>OriginLacksCargo, "origin-unresolved:*": keeps the raw token as a log-grep handle.</summary>
        internal const string OriginVesselNotFoundWithToken =
            "origin vessel could not be found - it may have moved,"
            + " been recovered, or been destroyed ({0})";

        /// <summary>OriginLacksCargo, resource short by a measured amount.</summary>
        internal const string OriginShortOfResource =
            "origin is short {0} {1} - delivers when the origin has the full amount";

        /// <summary>OriginLacksCargo, resource short with no measured amount.</summary>
        internal const string OriginOutOfResource =
            "origin is out of {0} - delivers when the origin has the full amount";

        /// <summary>OriginLacksCargo, "source:*" whose shape did not parse into pid / name / short.</summary>
        internal const string PickupSourceMissingCargo =
            "a pickup source is missing required cargo"
            + " - delivers when the source has the full amount";

        /// <summary>Per-pickup-source inventory short with an empty or opaque part tail.</summary>
        internal const string NamedSourceMissingStoredPart =
            "{0} is missing a required stored part - delivers when it holds it";

        /// <summary>Per-pickup-source inventory short naming the part.</summary>
        internal const string NamedSourceMissingNamedStoredPart =
            "{0} is missing stored part '{1}' - delivers when it holds it";

        /// <summary>Per-pickup-source short whose token named no resource.</summary>
        internal const string NamedSourceMissingCargo =
            "{0} is missing required cargo - delivers when it has the full amount";

        /// <summary>Per-pickup-source resource short by a measured amount.</summary>
        internal const string NamedSourceShortOfResource =
            "{0} is short {1} {2} - delivers when it has the full amount";

        /// <summary>Per-pickup-source resource short with no measured amount.</summary>
        internal const string NamedSourceOutOfResource =
            "{0} is out of {1} - delivers when it has the full amount";

        /// <summary>Escrow hold, "source-reserved:*" whose shape did not parse into four fields.</summary>
        internal const string ReservedPickupSourceCargo =
            "a pickup source has cargo reserved by another route"
            + " - delivers when the reservation clears";

        /// <summary>Escrow hold naming the source and the reserving route, but no resource.</summary>
        internal const string NamedSourceCargoReserved =
            "{0} has cargo reserved by route '{1}' - delivers when the reservation clears";

        /// <summary>Escrow hold naming the source, the reserved resource and the reserving route.</summary>
        internal const string NamedSourceResourceReserved =
            "{0} has {1} reserved by route '{2}' - delivers when the reservation clears";

        // ==================================================================
        // COMPACT FORM - the Status cell (CompactHold). Same kind+token table
        // as the long form, guidance suffixes dropped.
        // ==================================================================

        /// <summary>Compact FundsShort with a measured shortfall.</summary>
        internal const string CompactFundsShortWithAmount = "short {0:F0} funds";

        /// <summary>Compact FundsShort with no measured shortfall.</summary>
        internal const string CompactFundsShortGeneric = "insufficient funds";

        /// <summary>Compact DestinationFull inventory-slot short, part unnamed.</summary>
        internal const string CompactNoFreeInventorySlot = "no free inventory slot";

        /// <summary>Compact DestinationFull inventory-slot short, part named.</summary>
        internal const string CompactNoSlotForNamedPart = "no slot for '{0}'";

        /// <summary>Compact DestinationFull with no resource named.</summary>
        internal const string CompactDestinationFull = "destination full";

        /// <summary>Compact DestinationFull naming the resource.</summary>
        internal const string CompactNoRoomForResource = "no room for {0}";

        /// <summary>
        /// Compact "the origin vessel is gone". TWO arms render it: EndpointLost
        /// on an "origin-*" token, and OriginLacksCargo on "origin-unresolved:*"
        /// (whose long form keeps the raw token, which the cell has no room for).
        /// </summary>
        internal const string CompactOriginVesselLost = "origin vessel lost";

        /// <summary>Compact EndpointLost on a destination token.</summary>
        internal const string CompactDestinationVesselLost = "destination vessel lost";

        /// <summary>Compact SourcesStale.</summary>
        internal const string CompactSourceRecordingsUnavailable = "source recordings unavailable";

        /// <summary>Compact WaitingForPartner with no partner name.</summary>
        internal const string CompactWaitingForLinkedRoute = "waiting for linked route";

        /// <summary>Compact WaitingForPartner naming the linked route.</summary>
        internal const string CompactWaitingForNamedRoute = "waiting for '{0}'";

        /// <summary>Compact total fallback: an unknown failure kind.</summary>
        internal const string CompactBlockedUnknownKind = "blocked ({0})";

        /// <summary>Compact "pickup-source-unresolved:*".</summary>
        internal const string CompactPickupSourceVesselLost = "pickup source vessel lost";

        /// <summary>Compact escrow hold whose token did not parse into four fields.</summary>
        internal const string CompactCargoReservedByAnotherRoute = "cargo reserved by another route";

        /// <summary>Compact escrow hold naming the reserving route but no resource.</summary>
        internal const string CompactCargoReservedByNamedRoute = "cargo reserved by '{0}'";

        /// <summary>Compact escrow hold naming the resource and the reserving route.</summary>
        internal const string CompactResourceReservedByNamedRoute = "{0} reserved by '{1}'";

        /// <summary>Compact "source:*" whose shape did not parse.</summary>
        internal const string CompactPickupSourceShortOfCargo = "pickup source short of cargo";

        /// <summary>Compact per-pickup-source inventory short, part unnamed.</summary>
        internal const string CompactNamedSourceMissingStoredPart = "{0} missing a stored part";

        /// <summary>Compact per-pickup-source inventory short, part named.</summary>
        internal const string CompactNamedSourceMissingNamedPart = "{0} missing '{1}'";

        /// <summary>Compact per-pickup-source short whose token named no resource.</summary>
        internal const string CompactNamedSourceShortOfCargo = "{0} short of cargo";

        /// <summary>Compact per-pickup-source resource short by a measured amount.</summary>
        internal const string CompactNamedSourceShortOfResource = "{0} short {1} {2}";

        /// <summary>Compact per-pickup-source resource short with no measured amount.</summary>
        internal const string CompactNamedSourceOutOfResource = "{0} out of {1}";

        /// <summary>Compact "inventory-state:" with no part name.</summary>
        internal const string CompactOriginStoredPartStateDiffers = "stored part state differs at origin";

        /// <summary>Compact "inventory-state:&lt;partName&gt;".</summary>
        internal const string CompactOriginNamedStoredPartStateDiffers = "'{0}' state differs at origin";

        /// <summary>Compact "inventory:" with an empty or opaque tail.</summary>
        internal const string CompactOriginMissingStoredPart = "origin missing a stored part";

        /// <summary>Compact "inventory:&lt;partName&gt;".</summary>
        internal const string CompactOriginMissingNamedStoredPart = "origin missing '{0}'";

        /// <summary>Compact OriginLacksCargo whose token was empty (no resource to name).</summary>
        internal const string CompactOriginShortOfCargo = "origin short of cargo";

        /// <summary>Compact origin resource short by a measured amount.</summary>
        internal const string CompactOriginShortOfResource = "origin short {0} {1}";

        /// <summary>Compact origin resource short with no measured amount.</summary>
        internal const string CompactOriginOutOfResource = "origin out of {0}";

        // ==================================================================
        // FRAMES - the lines a clause is rendered INSIDE
        // ==================================================================

        /// <summary>Detail-panel blocked line when the hold's age is unknown or invalid.</summary>
        internal const string HoldDetailLine = "Last cycle blocked: {0}";

        /// <summary>
        /// Detail-panel blocked line with the mandatory age suffix: a reason held
        /// across a long warp reads as historical fact, not a live claim.
        /// </summary>
        internal const string HoldDetailLineWithAge = "Last cycle blocked: {0} (checked {1} ago)";

        /// <summary>Detail-panel partial-delivery report when the age is unknown or invalid.</summary>
        internal const string PartialDeliveryLine = "Last delivery was partial: {0}";

        /// <summary>Detail-panel partial-delivery report with its age suffix.</summary>
        internal const string PartialDeliveryLineWithAge = "Last delivery was partial: {0} ({1} ago)";

        /// <summary>The Status cell's held marker, wrapped around a compact clause before truncation.</summary>
        internal const string StatusCellHeld = "Held: {0}";

        /// <summary>
        /// The Send Once toast's belt-and-braces clause when a blocked cycle
        /// somehow carried no failure kind (<c>RouteSendOncePresentation</c>).
        /// </summary>
        internal const string SendOnceNotEligible = "the route was not eligible to dispatch";

        // ==================================================================

        private static readonly IReadOnlyList<LogisticsClause> all = BuildAll();

        /// <summary>Every hold clause above, in declaration order.</summary>
        internal static IReadOnlyList<LogisticsClause> All
        {
            get { return all; }
        }

        private static IReadOnlyList<LogisticsClause> BuildAll()
        {
            var list = new List<LogisticsClause>
            {
                Hold("FundsShortWithAmount", FundsShortWithAmount),
                Hold("FundsShortGeneric", FundsShortGeneric),
                Hold("DestinationNoInventorySlot", DestinationNoInventorySlot),
                Hold("DestinationNoInventorySlotForNamedPart", DestinationNoInventorySlotForNamedPart),
                Hold("DestinationNoRoomForDelivery", DestinationNoRoomForDelivery),
                Hold("DestinationNoRoomForResource", DestinationNoRoomForResource),
                Hold("OriginVesselNotFound", OriginVesselNotFound),
                Hold("DestinationVesselNotFound", DestinationVesselNotFound),
                Hold("SourceRecordingsUnavailable", SourceRecordingsUnavailable),
                Hold("WaitingForLinkedRoute", WaitingForLinkedRoute),
                Hold("WaitingForNamedLinkedRoute", WaitingForNamedLinkedRoute),
                Hold("BlockedUnknownKind", BlockedUnknownKind),
                Hold("PickupSourceNotFound", PickupSourceNotFound),
                Hold("OriginStoredPartStateChanged", OriginStoredPartStateChanged),
                Hold("OriginNamedStoredPartStateChanged", OriginNamedStoredPartStateChanged),
                Hold("OriginMissingStoredPart", OriginMissingStoredPart),
                Hold("OriginMissingNamedStoredPart", OriginMissingNamedStoredPart),
                Hold("OriginVesselNotFoundWithToken", OriginVesselNotFoundWithToken),
                Hold("OriginShortOfResource", OriginShortOfResource),
                Hold("OriginOutOfResource", OriginOutOfResource),
                Hold("PickupSourceMissingCargo", PickupSourceMissingCargo),
                Hold("NamedSourceMissingStoredPart", NamedSourceMissingStoredPart),
                Hold("NamedSourceMissingNamedStoredPart", NamedSourceMissingNamedStoredPart),
                Hold("NamedSourceMissingCargo", NamedSourceMissingCargo),
                Hold("NamedSourceShortOfResource", NamedSourceShortOfResource),
                Hold("NamedSourceOutOfResource", NamedSourceOutOfResource),
                Hold("ReservedPickupSourceCargo", ReservedPickupSourceCargo),
                Hold("NamedSourceCargoReserved", NamedSourceCargoReserved),
                Hold("NamedSourceResourceReserved", NamedSourceResourceReserved),

                Hold("CompactFundsShortWithAmount", CompactFundsShortWithAmount),
                Hold("CompactFundsShortGeneric", CompactFundsShortGeneric),
                Hold("CompactNoFreeInventorySlot", CompactNoFreeInventorySlot),
                Hold("CompactNoSlotForNamedPart", CompactNoSlotForNamedPart),
                Hold("CompactDestinationFull", CompactDestinationFull),
                Hold("CompactNoRoomForResource", CompactNoRoomForResource),
                Hold("CompactOriginVesselLost", CompactOriginVesselLost),
                Hold("CompactDestinationVesselLost", CompactDestinationVesselLost),
                Hold("CompactSourceRecordingsUnavailable", CompactSourceRecordingsUnavailable),
                Hold("CompactWaitingForLinkedRoute", CompactWaitingForLinkedRoute),
                Hold("CompactWaitingForNamedRoute", CompactWaitingForNamedRoute),
                Hold("CompactBlockedUnknownKind", CompactBlockedUnknownKind),
                Hold("CompactPickupSourceVesselLost", CompactPickupSourceVesselLost),
                Hold("CompactCargoReservedByAnotherRoute", CompactCargoReservedByAnotherRoute),
                Hold("CompactCargoReservedByNamedRoute", CompactCargoReservedByNamedRoute),
                Hold("CompactResourceReservedByNamedRoute", CompactResourceReservedByNamedRoute),
                Hold("CompactPickupSourceShortOfCargo", CompactPickupSourceShortOfCargo),
                Hold("CompactNamedSourceMissingStoredPart", CompactNamedSourceMissingStoredPart),
                Hold("CompactNamedSourceMissingNamedPart", CompactNamedSourceMissingNamedPart),
                Hold("CompactNamedSourceShortOfCargo", CompactNamedSourceShortOfCargo),
                Hold("CompactNamedSourceShortOfResource", CompactNamedSourceShortOfResource),
                Hold("CompactNamedSourceOutOfResource", CompactNamedSourceOutOfResource),
                Hold("CompactOriginStoredPartStateDiffers", CompactOriginStoredPartStateDiffers),
                Hold("CompactOriginNamedStoredPartStateDiffers", CompactOriginNamedStoredPartStateDiffers),
                Hold("CompactOriginMissingStoredPart", CompactOriginMissingStoredPart),
                Hold("CompactOriginMissingNamedStoredPart", CompactOriginMissingNamedStoredPart),
                Hold("CompactOriginShortOfCargo", CompactOriginShortOfCargo),
                Hold("CompactOriginShortOfResource", CompactOriginShortOfResource),
                Hold("CompactOriginOutOfResource", CompactOriginOutOfResource),

                Hold("HoldDetailLine", HoldDetailLine),
                Hold("HoldDetailLineWithAge", HoldDetailLineWithAge),
                Hold("PartialDeliveryLine", PartialDeliveryLine),
                Hold("PartialDeliveryLineWithAge", PartialDeliveryLineWithAge),
                Hold("StatusCellHeld", StatusCellHeld),
                Hold("SendOnceNotEligible", SendOnceNotEligible)
            };
            return list.AsReadOnly();
        }

        private static LogisticsClause Hold(string name, string format)
        {
            return new LogisticsClause(name, format, LogisticsClauseKind.Hold);
        }
    }
}
