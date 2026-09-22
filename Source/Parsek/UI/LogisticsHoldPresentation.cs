using System.Globalization;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// Pure presentation helper for live route hold reasons (M6 hold reasons):
    /// maps the persisted <c>Route.LastHold*</c> fields (the
    /// <see cref="RouteDispatchEvaluator.EligibilityFailureKind"/> plus the raw
    /// evaluator reason token) to plain-ASCII player language for the Logistics
    /// window's detail panel and status-cell tooltip. TOTAL over both token
    /// shapes - the loop path stores bare tokens ("LiquidFuel", "funds-short",
    /// "stop-0-no-live-vessels") while the legacy self-timer path stores
    /// prefixed decision tokens ("origin-lacks-X", "funds-shortfall-N",
    /// "destination-full-X") - and over unknown future kinds/tokens via the
    /// "route is blocked (kind: token)" fallback: never throws, never returns
    /// blank for a real hold. Unity-free and side-effect-free so it is unit
    /// tested directly off the IMGUI path (mirrors
    /// <see cref="LogisticsRejectPresentation"/> and the other
    /// Logistics*Presentation siblings). InvariantCulture for the shortfall.
    /// </summary>
    internal static class LogisticsHoldPresentation
    {
        /// <summary>
        /// One-clause player-language description of a hold, used verbatim as
        /// the status-cell tooltip augmentation and as the body of the detail
        /// line (<see cref="FormatHoldDetailLine"/>). Returns null ONLY for
        /// <see cref="RouteDispatchEvaluator.EligibilityFailureKind.None"/>
        /// (no hold recorded); every real hold maps to non-empty text.
        /// </summary>
        internal static string DescribeHold(
            RouteDispatchEvaluator.EligibilityFailureKind kind,
            string detail,
            double shortfall)
        {
            switch (kind)
            {
                case RouteDispatchEvaluator.EligibilityFailureKind.None:
                    return null;

                case RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo:
                    return DescribeOriginLacksCargo(detail, shortfall);

                case RouteDispatchEvaluator.EligibilityFailureKind.FundsShort:
                    // Both token shapes ("funds-short" / "funds-shortfall-N") land
                    // here; the number comes ONLY from the shortfall argument.
                    // The legacy capture stores shortfall 0 (the value lives only
                    // inside its token), so legacy holds render the generic text -
                    // accepted degradation, do NOT parse the token suffix.
                    return shortfall > 0.0
                        ? Fmt(LogisticsHoldClauses.FundsShortWithAmount, shortfall)
                        : LogisticsHoldClauses.FundsShortGeneric;

                case RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull:
                {
                    string resource = StripPrefix(detail, "destination-full-");
                    // Destination-capacity gate: an inventory-slot shortfall
                    // names the stored part ("stored-part:<partName>"); a bare
                    // token is a resource name.
                    string storedPart = TryStripPrefix(resource, RouteDestinationCapacityCheck.StoredPartTokenPrefix);
                    if (storedPart != null)
                    {
                        return storedPart.Length == 0
                            ? LogisticsHoldClauses.DestinationNoInventorySlot
                            : Fmt(LogisticsHoldClauses.DestinationNoInventorySlotForNamedPart, storedPart);
                    }
                    return string.IsNullOrEmpty(resource)
                        ? LogisticsHoldClauses.DestinationNoRoomForDelivery
                        : Fmt(LogisticsHoldClauses.DestinationNoRoomForResource, resource);
                }

                case RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost:
                    // "origin-*" names the origin resolver; everything else
                    // ("stop-N-*", "endpoint-destroyed-at-delivery:*", unknown)
                    // is a destination loss.
                    return detail != null
                        && detail.StartsWith("origin-", System.StringComparison.Ordinal)
                        ? LogisticsHoldClauses.OriginVesselNotFound
                        : LogisticsHoldClauses.DestinationVesselNotFound;

                case RouteDispatchEvaluator.EligibilityFailureKind.SourcesStale:
                    return LogisticsHoldClauses.SourceRecordingsUnavailable;

                case RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner:
                {
                    // Round-trip linking (M4c Phase C1): the gate token is
                    // "partner:<partnerName-or-id>". Name the linked route so the
                    // player knows which run this one is waiting on. The route keeps
                    // flying its loop (GhostDriving) while it waits.
                    string partner = StripPrefix(detail, "partner:");
                    return string.IsNullOrEmpty(partner)
                        ? LogisticsHoldClauses.WaitingForLinkedRoute
                        : Fmt(LogisticsHoldClauses.WaitingForNamedLinkedRoute, partner);
                }

                default:
                    return Fallback(kind, detail);
            }
        }

        /// <summary>
        /// The detail-panel line: "Last cycle blocked: {describe} (checked
        /// {age} ago)". The age suffix is mandatory display context (a reason
        /// held across a long warp reads as historical fact, not a live claim)
        /// and is omitted only when the age is unknown/invalid (negative, or a
        /// degenerate duration the formatter renders as "-"). Returns null when
        /// <paramref name="describe"/> is null/empty (no hold to render).
        /// </summary>
        internal static string FormatHoldDetailLine(string describe, double ageSeconds)
        {
            if (string.IsNullOrEmpty(describe))
                return null;
            if (ageSeconds < 0.0)
                return Fmt(LogisticsHoldClauses.HoldDetailLine, describe);
            string age = LogisticsWindowUI.FormatDuration(ageSeconds);
            if (age == "-")
                return Fmt(LogisticsHoldClauses.HoldDetailLine, describe);
            return Fmt(LogisticsHoldClauses.HoldDetailLineWithAge, describe, age);
        }

        /// <summary>
        /// Display gate for the hold text (plan-review MAJOR 2): no hold
        /// renders when none is recorded, and
        /// <see cref="RouteStatus.MissingSourceRecording"/> /
        /// <see cref="RouteStatus.SourceChanged"/> rows suppress the hold
        /// because those statuses already explain themselves and a persisted
        /// OLDER hold (e.g. an OriginLacksCargo from before the source changed)
        /// would actively mislead. Persistence stays unconditional - this gates
        /// DISPLAY only (mirrors the M4 CapacityContext status gate). Holds DO
        /// display for Active, the three wait states, EndpointLost, InTransit,
        /// and Paused (keep-on-Pause answers "why wasn't this delivering").
        /// </summary>
        internal static bool ShouldDisplayHold(
            RouteStatus status,
            RouteDispatchEvaluator.EligibilityFailureKind kind)
        {
            if (kind == RouteDispatchEvaluator.EligibilityFailureKind.None)
                return false;
            if (status == RouteStatus.MissingSourceRecording
                || status == RouteStatus.SourceChanged)
                return false;
            return true;
        }

        /// <summary>
        /// Status-cell tooltip: the raw enum name alone (the pre-M6 contract), or the
        /// enum name followed by the one-clause hold description on a SINGLE line.
        /// The tooltip always carries the FULL hold clause - the visible cell text
        /// is the compact (possibly truncated) <see cref="StatusCellText"/>, so the
        /// tooltip is where a truncated reason is read in full. Single-line since the
        /// Logistics help strip became one line tall: a hard newline spent one of the
        /// old two lines on the short enum name; now the strip's marquee scrolls the
        /// whole "Enum - clause" line into view instead.
        /// </summary>
        internal static string StatusCellTooltip(RouteStatus status, string holdShort)
        {
            if (string.IsNullOrEmpty(holdShort))
                return status.ToString();
            return status + " - " + holdShort;
        }

        // The OriginLacksCargo token family: the special markers first
        // (inventory-unsupported, origin-unresolved), then the resource name -
        // bare on the loop path, "origin-lacks-" prefixed on the legacy path.
        // <paramref name="shortfall"/> is the missing AMOUNT of the named
        // resource, threaded from the gate through the evaluator - the SAME
        // shortfall > 0.0 conditional FundsShort uses, because the token is a
        // legibility string and must never be parsed for a magnitude. 0 is
        // "unknown / not a resource shortfall" (inventory shorts, unresolved
        // endpoints, legacy persisted holds) and renders the pre-existing text.
        private static string DescribeOriginLacksCargo(string detail, double shortfall)
        {
            // Strip the legacy "origin-lacks-" wrapper FIRST: the legacy
            // WaitResources factory wraps whatever OriginHasCargo returned,
            // including the special markers below, so checking markers on the
            // wrapped token would render "origin is out of
            // origin-unresolved:..." (post-implementation review NIT 2).
            string token = StripPrefix(detail, "origin-lacks-");
            // M4b Phase B1 (plan D10 / OQ5): the per-PICKUP-SOURCE all-or-nothing
            // gate names the SHORT source vessel, not just the resource. Token shape:
            // "source:<pid>:<name>:<resource-or-inventory-token>". Render the source
            // vessel name so the hold reads "X cannot supply Y" rather than naming
            // only the resource (the player has several depots; which one is short
            // matters). The unresolved-source variant ("pickup-source-unresolved:*")
            // is a missing source vessel.
            if (token != null
                && token.StartsWith("pickup-source-unresolved:", System.StringComparison.Ordinal))
            {
                return Fmt(LogisticsHoldClauses.PickupSourceNotFound, token);
            }
            // M6 escrow-hold legibility: an ESCROW-caused pickup-source short -
            // the source physically holds the cargo but a competing route's
            // escrow reservation explains the shortfall. Token shape:
            // "source-reserved:<pid>:<name>:<resource>:<reservingRouteName>"
            // (both names sanitized of ':' at the emit site). Renders the
            // reserving route so the hold does not read as an empty depot.
            // Checked before the plain "source:" family for clarity (the
            // prefixes cannot collide - the char after "source" differs).
            if (token != null
                && token.StartsWith("source-reserved:", System.StringComparison.Ordinal))
            {
                return DescribeReservedPickupSource(token);
            }
            if (token != null
                && token.StartsWith("source:", System.StringComparison.Ordinal))
            {
                return DescribePickupSourceShort(token, shortfall);
            }
            // Inventory shortfalls: the emit sites now name the PART
            // ("inventory:<partName>"), with the raw identity hash only as a
            // fallback for unresolvable markers; pre-existing persisted holds
            // may still carry a hash, so a hash-shaped tail renders the generic
            // category text. The "inventory-state:<partName>" variant is the
            // near-miss: the origin physically holds the part but its state
            // (charge, fuel, contents) differs from the recorded cargo.
            string stateName = TryStripPrefix(token, "inventory-state:");
            if (stateName != null)
            {
                return stateName.Length == 0
                    ? LogisticsHoldClauses.OriginStoredPartStateChanged
                    : Fmt(LogisticsHoldClauses.OriginNamedStoredPartStateChanged, stateName);
            }
            string inventoryName = TryStripPrefix(token, "inventory:");
            if (inventoryName != null)
            {
                return inventoryName.Length == 0 || IsOpaqueInventoryTail(inventoryName)
                    ? LogisticsHoldClauses.OriginMissingStoredPart
                    : Fmt(LogisticsHoldClauses.OriginMissingNamedStoredPart, inventoryName);
            }
            if (token != null
                && token.StartsWith("origin-unresolved:", System.StringComparison.Ordinal))
            {
                // Keep the raw token in the tail so the log-grep handle survives
                // into the UI text.
                return Fmt(LogisticsHoldClauses.OriginVesselNotFoundWithToken, token);
            }
            if (string.IsNullOrEmpty(token))
                return Fallback(RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, detail);
            return shortfall > 0.0
                ? Fmt(LogisticsHoldClauses.OriginShortOfResource,
                    FormatShortfallAmount(shortfall), token)
                : Fmt(LogisticsHoldClauses.OriginOutOfResource, token);
        }

        // M4b Phase B1: parse the "source:<pid>:<name>:<short>" pickup-source token
        // and name the short source vessel. The name was sanitized of ':' at the
        // emit site (RoutePickupSourceGate.BuildHoldToken), so the first three ':'
        // delimit pid / name / short cleanly. Degrades to the generic origin text
        // if the shape is unexpected (never throws, never blank).
        private static string DescribePickupSourceShort(string token, double shortfall)
        {
            // token = "source:<pid>:<name>:<short...>"; split into at most 4 parts so
            // a short token that itself contains ':' (e.g. "inventory:<hash>") keeps
            // its colon in the tail.
            string body = token.Substring("source:".Length);
            string[] parts = body.Split(new[] { ':' }, 3);
            if (parts.Length < 3)
                return LogisticsHoldClauses.PickupSourceMissingCargo;
            string name = string.IsNullOrEmpty(parts[1]) ? "a pickup source" : parts[1];
            string shortToken = parts[2];
            string inventoryName = TryStripPrefix(shortToken, "inventory:");
            if (inventoryName != null)
            {
                return inventoryName.Length == 0 || IsOpaqueInventoryTail(inventoryName)
                    ? Fmt(LogisticsHoldClauses.NamedSourceMissingStoredPart, name)
                    : Fmt(LogisticsHoldClauses.NamedSourceMissingNamedStoredPart, name, inventoryName);
            }
            if (string.IsNullOrEmpty(shortToken))
                return Fmt(LogisticsHoldClauses.NamedSourceMissingCargo, name);
            // A PARTIALLY short source held with "is out of X" and no number, which
            // read as an empty depot when it held most of the manifest
            // (ROUTE-HOLD-SHORTFALL-DROPPED). Name the missing amount whenever the
            // gate measured one.
            return shortfall > 0.0
                ? Fmt(LogisticsHoldClauses.NamedSourceShortOfResource,
                    name, FormatShortfallAmount(shortfall), shortToken)
                : Fmt(LogisticsHoldClauses.NamedSourceOutOfResource, name, shortToken);
        }

        // M6 escrow-hold legibility: parse
        // "source-reserved:<pid>:<name>:<resource>:<reservingRouteName>" and name
        // both the reserved source vessel and the competing route holding the
        // reservation. Both names were sanitized of ':' at the emit site
        // (RoutePickupSourceGate.BuildReservedHoldToken) and the resource slot is
        // always a bare resource name (inventory shorts never take the escrow
        // path), so a 4-way split delimits pid / name / resource / route cleanly.
        // Degrades to a generic reserved-cargo clause on an unexpected shape
        // (never throws, never blank).
        private static string DescribeReservedPickupSource(string token)
        {
            string body = token.Substring("source-reserved:".Length);
            string[] parts = body.Split(new[] { ':' }, 4);
            if (parts.Length < 4)
                return LogisticsHoldClauses.ReservedPickupSourceCargo;
            string name = string.IsNullOrEmpty(parts[1]) ? "a pickup source" : parts[1];
            string resource = parts[2];
            string routeName = string.IsNullOrEmpty(parts[3]) ? "another route" : parts[3];
            if (string.IsNullOrEmpty(resource))
            {
                return Fmt(LogisticsHoldClauses.NamedSourceCargoReserved, name, routeName);
            }
            return Fmt(LogisticsHoldClauses.NamedSourceResourceReserved, name, resource, routeName);
        }

        // ------------------------------------------------------------------
        // M6 closeout: row-level hold treatment (Status cell)
        // ------------------------------------------------------------------

        /// <summary>
        /// Hard cap on the visible Status-cell hold text. The cell is 240 px
        /// and wraps, so this bounds the row to roughly two wrapped lines even
        /// with long vessel / route names; the FULL clause always survives in
        /// the tooltip (<see cref="StatusCellTooltip"/>).
        /// </summary>
        internal const int StatusCellMaxChars = 60;

        /// <summary>
        /// The visible Status-cell text for a held route (M6 closeout: the
        /// row-level treatment - the cell carries the compact SPECIFIC reason,
        /// not the generic per-status sentence). "Held: " marker plus
        /// <see cref="CompactHold"/>, truncated to
        /// <see cref="StatusCellMaxChars"/> via <see cref="TruncateForCell"/>.
        /// Returns null ONLY for kind None (no hold recorded) - the draw path
        /// then falls back to the generic StatusReason text. Callers gate on
        /// <see cref="ShouldDisplayHold"/> first, same as the tooltip/detail.
        /// </summary>
        internal static string StatusCellText(
            RouteDispatchEvaluator.EligibilityFailureKind kind,
            string detail,
            double shortfall)
        {
            string compact = CompactHold(kind, detail, shortfall);
            if (string.IsNullOrEmpty(compact))
                return null;
            return TruncateForCell(
                Fmt(LogisticsHoldClauses.StatusCellHeld, compact), StatusCellMaxChars);
        }

        /// <summary>
        /// One compact clause naming the specific blocker, for the Status cell
        /// (the row, not the tooltip). Same total kind+token table as
        /// <see cref="DescribeHold"/> - both token shapes, never throws, never
        /// blank for a real hold, unknown kinds/tokens degrade to
        /// "blocked ({kind})" - but drops the "delivers when ..." guidance
        /// suffixes so the cell stays short; the full clause lives in the
        /// tooltip. Returns null only for kind None.
        /// </summary>
        internal static string CompactHold(
            RouteDispatchEvaluator.EligibilityFailureKind kind,
            string detail,
            double shortfall)
        {
            switch (kind)
            {
                case RouteDispatchEvaluator.EligibilityFailureKind.None:
                    return null;

                case RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo:
                    return CompactOriginLacksCargo(detail, shortfall);

                case RouteDispatchEvaluator.EligibilityFailureKind.FundsShort:
                    // Same shortfall contract as DescribeHold: the number comes
                    // ONLY from the shortfall argument (legacy captures store 0
                    // and render the generic text).
                    return shortfall > 0.0
                        ? Fmt(LogisticsHoldClauses.CompactFundsShortWithAmount, shortfall)
                        : LogisticsHoldClauses.CompactFundsShortGeneric;

                case RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull:
                {
                    string resource = StripPrefix(detail, "destination-full-");
                    string storedPart = TryStripPrefix(resource, RouteDestinationCapacityCheck.StoredPartTokenPrefix);
                    if (storedPart != null)
                    {
                        return storedPart.Length == 0
                            ? LogisticsHoldClauses.CompactNoFreeInventorySlot
                            : Fmt(LogisticsHoldClauses.CompactNoSlotForNamedPart, storedPart);
                    }
                    return string.IsNullOrEmpty(resource)
                        ? LogisticsHoldClauses.CompactDestinationFull
                        : Fmt(LogisticsHoldClauses.CompactNoRoomForResource, resource);
                }

                case RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost:
                    return detail != null
                        && detail.StartsWith("origin-", System.StringComparison.Ordinal)
                        ? LogisticsHoldClauses.CompactOriginVesselLost
                        : LogisticsHoldClauses.CompactDestinationVesselLost;

                case RouteDispatchEvaluator.EligibilityFailureKind.SourcesStale:
                    return LogisticsHoldClauses.CompactSourceRecordingsUnavailable;

                case RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner:
                {
                    string partner = StripPrefix(detail, "partner:");
                    return string.IsNullOrEmpty(partner)
                        ? LogisticsHoldClauses.CompactWaitingForLinkedRoute
                        : Fmt(LogisticsHoldClauses.CompactWaitingForNamedRoute, partner);
                }

                default:
                    return Fmt(LogisticsHoldClauses.CompactBlockedUnknownKind, kind);
            }
        }

        // Compact variant of DescribeOriginLacksCargo: same token family, same
        // strip-the-legacy-wrapper-first order, same shortfall > 0.0 conditional,
        // short phrasing.
        private static string CompactOriginLacksCargo(string detail, double shortfall)
        {
            string token = StripPrefix(detail, "origin-lacks-");
            if (token != null
                && token.StartsWith("pickup-source-unresolved:", System.StringComparison.Ordinal))
            {
                return LogisticsHoldClauses.CompactPickupSourceVesselLost;
            }
            if (token != null
                && token.StartsWith("source-reserved:", System.StringComparison.Ordinal))
            {
                // "source-reserved:<pid>:<name>:<resource>:<reservingRouteName>"
                string body = token.Substring("source-reserved:".Length);
                string[] parts = body.Split(new[] { ':' }, 4);
                if (parts.Length < 4)
                    return LogisticsHoldClauses.CompactCargoReservedByAnotherRoute;
                string resource = parts[2];
                string routeName = string.IsNullOrEmpty(parts[3]) ? "another route" : parts[3];
                return string.IsNullOrEmpty(resource)
                    ? Fmt(LogisticsHoldClauses.CompactCargoReservedByNamedRoute, routeName)
                    : Fmt(LogisticsHoldClauses.CompactResourceReservedByNamedRoute, resource, routeName);
            }
            if (token != null
                && token.StartsWith("source:", System.StringComparison.Ordinal))
            {
                // "source:<pid>:<name>:<short...>" - same 3-way split as
                // DescribePickupSourceShort so an "inventory:<hash>" short
                // keeps its colon in the tail.
                string body = token.Substring("source:".Length);
                string[] parts = body.Split(new[] { ':' }, 3);
                if (parts.Length < 3)
                    return LogisticsHoldClauses.CompactPickupSourceShortOfCargo;
                string name = string.IsNullOrEmpty(parts[1]) ? "pickup source" : parts[1];
                string shortToken = parts[2];
                string sourceInventoryName = TryStripPrefix(shortToken, "inventory:");
                if (sourceInventoryName != null)
                {
                    return sourceInventoryName.Length == 0 || IsOpaqueInventoryTail(sourceInventoryName)
                        ? Fmt(LogisticsHoldClauses.CompactNamedSourceMissingStoredPart, name)
                        : Fmt(LogisticsHoldClauses.CompactNamedSourceMissingNamedPart,
                            name, sourceInventoryName);
                }
                if (string.IsNullOrEmpty(shortToken))
                    return Fmt(LogisticsHoldClauses.CompactNamedSourceShortOfCargo, name);
                return shortfall > 0.0
                    ? Fmt(LogisticsHoldClauses.CompactNamedSourceShortOfResource,
                        name, FormatShortfallAmount(shortfall), shortToken)
                    : Fmt(LogisticsHoldClauses.CompactNamedSourceOutOfResource, name, shortToken);
            }
            string stateName = TryStripPrefix(token, "inventory-state:");
            if (stateName != null)
            {
                return stateName.Length == 0
                    ? LogisticsHoldClauses.CompactOriginStoredPartStateDiffers
                    : Fmt(LogisticsHoldClauses.CompactOriginNamedStoredPartStateDiffers, stateName);
            }
            string inventoryName = TryStripPrefix(token, "inventory:");
            if (inventoryName != null)
            {
                return inventoryName.Length == 0 || IsOpaqueInventoryTail(inventoryName)
                    ? LogisticsHoldClauses.CompactOriginMissingStoredPart
                    : Fmt(LogisticsHoldClauses.CompactOriginMissingNamedStoredPart, inventoryName);
            }
            if (token != null
                && token.StartsWith("origin-unresolved:", System.StringComparison.Ordinal))
            {
                return LogisticsHoldClauses.CompactOriginVesselLost;
            }
            if (string.IsNullOrEmpty(token))
                return LogisticsHoldClauses.CompactOriginShortOfCargo;
            return shortfall > 0.0
                ? Fmt(LogisticsHoldClauses.CompactOriginShortOfResource,
                    FormatShortfallAmount(shortfall), token)
                : Fmt(LogisticsHoldClauses.CompactOriginOutOfResource, token);
        }

        /// <summary>
        /// One-decimal InvariantCulture rendering of a resource shortfall for the
        /// player-facing hold text. Invariant because the xUnit host runs under the
        /// OS culture and these strings are asserted directly; one decimal because
        /// the raw double is a float-accumulated tank total
        /// (108.79999999999706 reads as "108.8").
        /// </summary>
        internal static string FormatShortfallAmount(double shortfall)
        {
            return shortfall.ToString("F1", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Plain-ASCII hard truncation for the Status cell: text at or under
        /// <paramref name="maxChars"/> passes through unchanged; longer text is
        /// cut to <c>maxChars - 3</c> plus "...". Null/empty passes through.
        /// </summary>
        internal static string TruncateForCell(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || maxChars <= 3 || text.Length <= maxChars)
                return text;
            return text.Substring(0, maxChars - 3) + "...";
        }

        // Total fallback row: readable, never blank, never throws - new tokens
        // or future kinds degrade here instead of rendering nothing.
        private static string Fallback(
            RouteDispatchEvaluator.EligibilityFailureKind kind, string detail)
        {
            return Fmt(LogisticsHoldClauses.BlockedUnknownKind,
                kind, string.IsNullOrEmpty(detail) ? "<none>" : detail);
        }

        /// <summary>
        /// The one substitution site for every clause in
        /// <see cref="LogisticsHoldClauses"/>. InvariantCulture because two
        /// clauses carry numeric holes (the whole-unit funds shortfall, and the
        /// already-invariant <see cref="FormatShortfallAmount"/> string) and the
        /// xUnit host runs under the OS culture.
        /// </summary>
        private static string Fmt(string clause, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, clause, args);
        }

        private static string StripPrefix(string token, string prefix)
        {
            if (string.IsNullOrEmpty(token))
                return token;
            return token.StartsWith(prefix, System.StringComparison.Ordinal)
                ? token.Substring(prefix.Length)
                : token;
        }

        // Prefix-DETECTING sibling of StripPrefix: null when the token does not
        // carry the prefix (so callers can branch on the family), the stripped
        // tail (possibly empty) when it does.
        private static string TryStripPrefix(string token, string prefix)
        {
            if (string.IsNullOrEmpty(token))
                return null;
            return token.StartsWith(prefix, System.StringComparison.Ordinal)
                ? token.Substring(prefix.Length)
                : null;
        }

        /// <summary>
        /// True when <paramref name="value"/> is shaped like a canonical
        /// inventory payload kind key (64 lowercase-hex chars, the SHA256
        /// form <c>VesselSnapshotOps.ComputeInventoryPayloadKindKey</c> emits).
        /// Pre-legibility persisted holds carry the raw hash in their
        /// <c>inventory:</c> token; rendering a hash as a "part name" would be
        /// worse than the generic text, so the describe paths fall back on it.
        /// </summary>
        internal static bool LooksLikeIdentityHash(string value)
        {
            if (value == null || value.Length != 64)
                return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!hex)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// True when an <c>inventory:</c> token tail is NOT a part name and
        /// must render the generic category text instead of being quoted as
        /// one: a canonical identity hash (pre-legibility persisted holds) or
        /// an internal gate marker (<c>null-stored-counter</c>, the defensive
        /// null-reader branch of <c>RouteOriginCargoCheck.HasRequiredInventory</c>).
        /// Quoting either as a "part name" would show the player an internal
        /// code - the exact failure the legible tokens exist to remove.
        /// </summary>
        internal static bool IsOpaqueInventoryTail(string tail)
        {
            return LooksLikeIdentityHash(tail)
                || string.Equals(tail, "null-stored-counter", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// The detail-panel line for a partial delivery
        /// (<c>Route.LastPartialDeliverySummary</c>): "Last delivery was
        /// partial: {summary} (age ago)" - the destination-capacity gate makes
        /// partials rare (mid-transit capacity changes only), so when one DOES
        /// happen the player must see exactly what was lost. Same age-suffix
        /// contract as <see cref="FormatHoldDetailLine"/>. Returns null when
        /// no summary is recorded.
        /// </summary>
        internal static string FormatPartialDeliveryLine(string summary, double ageSeconds)
        {
            if (string.IsNullOrEmpty(summary))
                return null;
            if (ageSeconds < 0.0)
                return Fmt(LogisticsHoldClauses.PartialDeliveryLine, summary);
            string age = LogisticsWindowUI.FormatDuration(ageSeconds);
            if (age == "-")
                return Fmt(LogisticsHoldClauses.PartialDeliveryLine, summary);
            return Fmt(LogisticsHoldClauses.PartialDeliveryLineWithAge, summary, age);
        }
    }
}
