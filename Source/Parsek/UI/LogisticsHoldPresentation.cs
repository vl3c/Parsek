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
                    return DescribeOriginLacksCargo(detail);

                case RouteDispatchEvaluator.EligibilityFailureKind.FundsShort:
                    // Both token shapes ("funds-short" / "funds-shortfall-N") land
                    // here; the number comes ONLY from the shortfall argument.
                    // The legacy capture stores shortfall 0 (the value lives only
                    // inside its token), so legacy holds render the generic text -
                    // accepted degradation, do NOT parse the token suffix.
                    return shortfall > 0.0
                        ? string.Format(CultureInfo.InvariantCulture,
                            "not enough funds at KSC - short {0:F0} funds for this dispatch",
                            shortfall)
                        : "not enough funds at KSC for this dispatch";

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
                            ? "destination has no free inventory slot for a stored part - delivers when it has room for the full manifest"
                            : "destination has no free inventory slot for stored part '" + storedPart
                                + "' - delivers when it has room for the full manifest";
                    }
                    return string.IsNullOrEmpty(resource)
                        ? "destination has no room for the delivery - delivers when it has room for the full manifest"
                        : "destination has no room for " + resource
                            + " - delivers when it has room for the full manifest";
                }

                case RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost:
                    // "origin-*" names the origin resolver; everything else
                    // ("stop-N-*", "endpoint-destroyed-at-delivery:*", unknown)
                    // is a destination loss.
                    return detail != null
                        && detail.StartsWith("origin-", System.StringComparison.Ordinal)
                        ? "origin vessel could not be found"
                        : "destination vessel could not be found - re-target or recreate the route";

                case RouteDispatchEvaluator.EligibilityFailureKind.SourcesStale:
                    return "route source recordings are unavailable right now";

                case RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner:
                {
                    // Round-trip linking (M4c Phase C1): the gate token is
                    // "partner:<partnerName-or-id>". Name the linked route so the
                    // player knows which run this one is waiting on. The route keeps
                    // flying its loop (GhostDriving) while it waits.
                    string partner = StripPrefix(detail, "partner:");
                    return string.IsNullOrEmpty(partner)
                        ? "waiting for the linked route to complete its run"
                        : "waiting for the linked route '" + partner + "' to complete its run";
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
                return "Last cycle blocked: " + describe;
            string age = LogisticsWindowUI.FormatDuration(ageSeconds);
            if (age == "-")
                return "Last cycle blocked: " + describe;
            return "Last cycle blocked: " + describe + " (checked " + age + " ago)";
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
        /// Status-cell tooltip: the raw enum name alone (the pre-M6 contract),
        /// or the enum name plus the one-clause hold description on a second
        /// line. The tooltip always carries the FULL hold clause - the visible
        /// cell text is the compact (possibly truncated)
        /// <see cref="StatusCellText"/>, so the tooltip is where a truncated
        /// reason is read in full (M6 closeout row-level treatment).
        /// </summary>
        internal static string StatusCellTooltip(RouteStatus status, string holdShort)
        {
            if (string.IsNullOrEmpty(holdShort))
                return status.ToString();
            return status + "\n" + holdShort;
        }

        // The OriginLacksCargo token family: the special markers first
        // (inventory-unsupported, origin-unresolved), then the resource name -
        // bare on the loop path, "origin-lacks-" prefixed on the legacy path.
        private static string DescribeOriginLacksCargo(string detail)
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
                return "a pickup source vessel could not be found - it may have moved, been recovered, or been destroyed ("
                    + token + ")";
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
                return DescribePickupSourceShort(token);
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
                    ? "a stored part at the origin does not match the recorded cargo - its charge, fuel, or contents changed"
                    : "stored part '" + stateName + "' at the origin does not match the recorded cargo - its charge, fuel, or contents changed";
            }
            string inventoryName = TryStripPrefix(token, "inventory:");
            if (inventoryName != null)
            {
                return inventoryName.Length == 0 || IsOpaqueInventoryTail(inventoryName)
                    ? "origin is missing a required stored part - delivers when the origin holds it"
                    : "origin is missing stored part '" + inventoryName
                        + "' - delivers when the origin holds it";
            }
            if (token != null
                && token.StartsWith("origin-unresolved:", System.StringComparison.Ordinal))
            {
                // Keep the raw token in the tail so the log-grep handle survives
                // into the UI text.
                return "origin vessel could not be found - it may have moved, been recovered, or been destroyed ("
                    + token + ")";
            }
            if (string.IsNullOrEmpty(token))
                return Fallback(RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, detail);
            return "origin is out of " + token + " - delivers when the origin has the full amount";
        }

        // M4b Phase B1: parse the "source:<pid>:<name>:<short>" pickup-source token
        // and name the short source vessel. The name was sanitized of ':' at the
        // emit site (RoutePickupSourceGate.BuildHoldToken), so the first three ':'
        // delimit pid / name / short cleanly. Degrades to the generic origin text
        // if the shape is unexpected (never throws, never blank).
        private static string DescribePickupSourceShort(string token)
        {
            // token = "source:<pid>:<name>:<short...>"; split into at most 4 parts so
            // a short token that itself contains ':' (e.g. "inventory:<hash>") keeps
            // its colon in the tail.
            string body = token.Substring("source:".Length);
            string[] parts = body.Split(new[] { ':' }, 3);
            if (parts.Length < 3)
                return "a pickup source is missing required cargo - delivers when the source has the full amount";
            string name = string.IsNullOrEmpty(parts[1]) ? "a pickup source" : parts[1];
            string shortToken = parts[2];
            string inventoryName = TryStripPrefix(shortToken, "inventory:");
            if (inventoryName != null)
            {
                return inventoryName.Length == 0 || IsOpaqueInventoryTail(inventoryName)
                    ? name + " is missing a required stored part - delivers when it holds it"
                    : name + " is missing stored part '" + inventoryName
                        + "' - delivers when it holds it";
            }
            if (string.IsNullOrEmpty(shortToken))
                return name + " is missing required cargo - delivers when it has the full amount";
            return name + " is out of " + shortToken + " - delivers when it has the full amount";
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
                return "a pickup source has cargo reserved by another route - delivers when the reservation clears";
            string name = string.IsNullOrEmpty(parts[1]) ? "a pickup source" : parts[1];
            string resource = parts[2];
            string routeName = string.IsNullOrEmpty(parts[3]) ? "another route" : parts[3];
            if (string.IsNullOrEmpty(resource))
            {
                return name + " has cargo reserved by route '" + routeName
                    + "' - delivers when the reservation clears";
            }
            return name + " has " + resource + " reserved by route '" + routeName
                + "' - delivers when the reservation clears";
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
            return TruncateForCell("Held: " + compact, StatusCellMaxChars);
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
                    return CompactOriginLacksCargo(detail);

                case RouteDispatchEvaluator.EligibilityFailureKind.FundsShort:
                    // Same shortfall contract as DescribeHold: the number comes
                    // ONLY from the shortfall argument (legacy captures store 0
                    // and render the generic text).
                    return shortfall > 0.0
                        ? string.Format(CultureInfo.InvariantCulture,
                            "short {0:F0} funds", shortfall)
                        : "insufficient funds";

                case RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull:
                {
                    string resource = StripPrefix(detail, "destination-full-");
                    string storedPart = TryStripPrefix(resource, RouteDestinationCapacityCheck.StoredPartTokenPrefix);
                    if (storedPart != null)
                    {
                        return storedPart.Length == 0
                            ? "no free inventory slot"
                            : "no slot for '" + storedPart + "'";
                    }
                    return string.IsNullOrEmpty(resource)
                        ? "destination full"
                        : "no room for " + resource;
                }

                case RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost:
                    return detail != null
                        && detail.StartsWith("origin-", System.StringComparison.Ordinal)
                        ? "origin vessel lost"
                        : "destination vessel lost";

                case RouteDispatchEvaluator.EligibilityFailureKind.SourcesStale:
                    return "source recordings unavailable";

                case RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner:
                {
                    string partner = StripPrefix(detail, "partner:");
                    return string.IsNullOrEmpty(partner)
                        ? "waiting for linked route"
                        : "waiting for '" + partner + "'";
                }

                default:
                    return string.Format(CultureInfo.InvariantCulture,
                        "blocked ({0})", kind);
            }
        }

        // Compact variant of DescribeOriginLacksCargo: same token family, same
        // strip-the-legacy-wrapper-first order, short phrasing.
        private static string CompactOriginLacksCargo(string detail)
        {
            string token = StripPrefix(detail, "origin-lacks-");
            if (token != null
                && token.StartsWith("pickup-source-unresolved:", System.StringComparison.Ordinal))
            {
                return "pickup source vessel lost";
            }
            if (token != null
                && token.StartsWith("source-reserved:", System.StringComparison.Ordinal))
            {
                // "source-reserved:<pid>:<name>:<resource>:<reservingRouteName>"
                string body = token.Substring("source-reserved:".Length);
                string[] parts = body.Split(new[] { ':' }, 4);
                if (parts.Length < 4)
                    return "cargo reserved by another route";
                string resource = parts[2];
                string routeName = string.IsNullOrEmpty(parts[3]) ? "another route" : parts[3];
                return string.IsNullOrEmpty(resource)
                    ? "cargo reserved by '" + routeName + "'"
                    : resource + " reserved by '" + routeName + "'";
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
                    return "pickup source short of cargo";
                string name = string.IsNullOrEmpty(parts[1]) ? "pickup source" : parts[1];
                string shortToken = parts[2];
                string sourceInventoryName = TryStripPrefix(shortToken, "inventory:");
                if (sourceInventoryName != null)
                {
                    return sourceInventoryName.Length == 0 || IsOpaqueInventoryTail(sourceInventoryName)
                        ? name + " missing a stored part"
                        : name + " missing '" + sourceInventoryName + "'";
                }
                return string.IsNullOrEmpty(shortToken)
                    ? name + " short of cargo"
                    : name + " out of " + shortToken;
            }
            string stateName = TryStripPrefix(token, "inventory-state:");
            if (stateName != null)
            {
                return stateName.Length == 0
                    ? "stored part state differs at origin"
                    : "'" + stateName + "' state differs at origin";
            }
            string inventoryName = TryStripPrefix(token, "inventory:");
            if (inventoryName != null)
            {
                return inventoryName.Length == 0 || IsOpaqueInventoryTail(inventoryName)
                    ? "origin missing a stored part"
                    : "origin missing '" + inventoryName + "'";
            }
            if (token != null
                && token.StartsWith("origin-unresolved:", System.StringComparison.Ordinal))
            {
                return "origin vessel lost";
            }
            if (string.IsNullOrEmpty(token))
                return "origin short of cargo";
            return "origin out of " + token;
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
            return string.Format(CultureInfo.InvariantCulture,
                "route is blocked ({0}: {1})",
                kind, string.IsNullOrEmpty(detail) ? "<none>" : detail);
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
        /// inventory payload identity hash (64 lowercase-hex chars, the SHA256
        /// form <c>VesselSpawner.ComputeInventoryPayloadIdentityHash</c> emits).
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
                return "Last delivery was partial: " + summary;
            string age = LogisticsWindowUI.FormatDuration(ageSeconds);
            if (age == "-")
                return "Last delivery was partial: " + summary;
            return "Last delivery was partial: " + summary + " (" + age + " ago)";
        }
    }
}
