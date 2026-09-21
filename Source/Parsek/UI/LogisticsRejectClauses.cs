using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Every player-facing REJECT clause a committed recording tree can be
    /// refused with, as named composite format strings: the eleven
    /// <see cref="Parsek.Logistics.RouteAnalysisStatus"/> arms of
    /// <see cref="Parsek.Logistics.RouteCreationFormatters.FormatRejectMessage"/>
    /// (ten named statuses plus the unknown-status fallback) and the separate
    /// not-fully-sealed gate that
    /// <see cref="LogisticsRejectPresentation.DescribeNearMiss"/> answers before
    /// the status is consulted at all.
    ///
    /// Four of the status clauses can quantify themselves. Their <c>{0}</c> is
    /// the whole parenthesised detail SEGMENT, empty when the analysis recorded
    /// no detail - the shape the shipped concatenation had, kept exactly so the
    /// sentence reads the same with and without one.
    ///
    /// <see cref="Parsek.Logistics.RouteAnalysisStatus.Eligible"/> has no clause
    /// by design: it is the happy path and formats to the empty string, which is
    /// a caller contract rather than something the player reads.
    ///
    /// Extracted with NO behavior change, for the GUI state gallery's
    /// completeness guard - see <c>docs/dev/design-gui-state-gallery.md</c>
    /// section 11.
    /// </summary>
    internal static class LogisticsRejectClauses
    {
        /// <summary>The tree has at least one non-Immutable recording, so the route proof can still change.</summary>
        internal const string NotFullySealed =
            "not fully sealed ({0} {1} still re-flyable)";

        /// <summary>MissingRouteProof: no dock event was logged on the source recording.</summary>
        internal const string MissingRouteProof =
            "Recording has no route proof - log the dock event to enable a Supply Route.";

        /// <summary>MultipleConnectionWindows: two transfers share a recorded moment and cannot be ordered.</summary>
        internal const string MultipleConnectionWindows =
            "Two transfers happened at the same recorded time and cannot be ordered."
            + " Re-record so each dock happens at a distinct moment.";

        /// <summary>NoDeliveryManifest: nothing measurably moved from transport to destination.</summary>
        internal const string NoDeliveryManifest =
            "No delivery payload detected - check that cargo actually moved"
            + " from transport to destination.";

        /// <summary>MixedPickupDelivery: the transport gained a stored part the destination never gave it.</summary>
        internal const string MixedPickupDelivery =
            "Unwitnessed inventory gain detected - the transport gained a stored part"
            + " that the destination did not give it. Stored cargo is counted by kind"
            + " (part, variant and how full it is), so only a kind the destination"
            + " visibly gave up can be picked up. Re-record so the picked-up part"
            + " comes from the destination.";

        /// <summary>MissingEndpointProof: no endpoint vessel identity was captured at dock time.</summary>
        internal const string MissingEndpointProof =
            "Endpoint vessel could not be identified at dock time.";

        /// <summary>UndockedStartOrigin: the run began undocked with cargo aboard, so its source was never witnessed.</summary>
        internal const string UndockedStartOrigin =
            "This run starts undocked with cargo already aboard, so the cargo's source"
            + " was never witnessed. Start the supply run docked to the origin depot,"
            + " record the mining that produced the cargo, or launch it from KSC.";

        /// <summary>UntrackedCargoGain: a gain with no witnessed source. {0} is the detail segment.</summary>
        internal const string UntrackedCargoGain =
            "The transport gained cargo during this run with no recorded source{0}."
            + " Only witnessed gains can route: record the mining with the drill"
            + " or converter running, or re-record without the unexplained gain.";

        /// <summary>FlowDoesNotClose: the run ended with more of a resource than ever arrived. {0} is the detail segment.</summary>
        internal const string FlowDoesNotClose =
            "This run's cargo does not add up: the transport ended with more of a"
            + " resource than ever arrived{0}. The recorded loads, harvest, and"
            + " deliveries cannot account for what was left aboard. Re-record so"
            + " every resource that leaves the transport is matched by a recorded"
            + " load, harvest, or delivery.";

        /// <summary>MidRecordingStartTrimUnsupported: the remaining unsupported mid-flight start shapes. {0} is the detail segment.</summary>
        internal const string MidRecordingStartTrimUnsupported =
            "This run starts between two docks: an earlier docked stretch{0} was"
            + " recorded before the cargo run, but this shape is not supported yet."
            + " A mid-flight start works when the run begins at a fully recorded"
            + " docked-origin window - dock at the origin depot, then undock, both"
            + " recorded, before the first delivery dock. Otherwise start the supply"
            + " run docked at the origin depot, or launch it from KSC.";

        /// <summary>UnsupportedConnectionKind: the transfer used a connection routes do not support. {0} is the detail segment.</summary>
        internal const string UnsupportedConnectionKind =
            "This run's transfer used a connection type Parsek does not support for"
            + " routes{0}. Docked and claw-grappled transfers are supported.";

        /// <summary>The total fallback for a status with no dedicated clause.</summary>
        internal const string UnknownStatus =
            "Route source is not eligible ({0}).";

        // ==================================================================

        private static readonly IReadOnlyList<LogisticsClause> all = BuildAll();

        /// <summary>Every reject clause above, in declaration order.</summary>
        internal static IReadOnlyList<LogisticsClause> All
        {
            get { return all; }
        }

        private static IReadOnlyList<LogisticsClause> BuildAll()
        {
            var list = new List<LogisticsClause>
            {
                Reject("NotFullySealed", NotFullySealed),
                Reject("MissingRouteProof", MissingRouteProof),
                Reject("MultipleConnectionWindows", MultipleConnectionWindows),
                Reject("NoDeliveryManifest", NoDeliveryManifest),
                Reject("MixedPickupDelivery", MixedPickupDelivery),
                Reject("MissingEndpointProof", MissingEndpointProof),
                Reject("UndockedStartOrigin", UndockedStartOrigin),
                Reject("UntrackedCargoGain", UntrackedCargoGain),
                Reject("FlowDoesNotClose", FlowDoesNotClose),
                Reject("MidRecordingStartTrimUnsupported", MidRecordingStartTrimUnsupported),
                Reject("UnsupportedConnectionKind", UnsupportedConnectionKind),
                Reject("UnknownStatus", UnknownStatus)
            };
            return list.AsReadOnly();
        }

        private static LogisticsClause Reject(string name, string format)
        {
            return new LogisticsClause(name, format, LogisticsClauseKind.Reject);
        }
    }
}
