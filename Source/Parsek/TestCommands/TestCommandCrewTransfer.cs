using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>What one completion poll of a started <c>CrewTransfer</c> concludes.</summary>
    internal enum CrewTransferCompletionDecision
    {
        StillWaiting,
        Moved,
        Timeout,
    }

    /// <summary>
    /// Pure half of the automation-only <c>CrewTransfer</c> seam verb: a player's stock crew
    /// transfer between two crewed parts of the ACTIVE vessel (two pods of a docked pair, for
    /// one), the move a player makes from a crewed part's right-click menu.
    ///
    /// <para><b>Why the stock transfer and not a roster write.</b> The applier opens stock's own
    /// transfer (<c>CrewTransfer.Create(srcPart, crew, onDismiss)</c>, which lists the vessel's
    /// valid destination parts exactly as the highlighted-part prompt does) and then selects the
    /// destination through the transfer's own <c>OnPartSelect(Part)</c>, the method a click on a
    /// highlighted part calls. Stock fires <c>onCrewTransferSelected</c> (a mod may veto there),
    /// moves the kerbal (<c>RemoveCrewmember</c> / <c>AddCrewmember</c>), fires
    /// <c>onCrewTransferred</c> and <c>Vessel.CrewWasModified</c>, and one frame later respawns
    /// the IVA crew and dismisses itself with <c>ItemMoved</c> (decompiled KSP 1.12.5).
    /// Parsek's crew bookkeeping sees exactly what a player's transfer gives it.</para>
    ///
    /// <para><b>Grammar.</b> <c>cmd=CrewTransfer kerbal=&lt;full name&gt; part=&lt;runtime part
    /// name&gt;</c>, both REQUIRED. <c>kerbal=</c> must sit in a part of the active vessel.
    /// <c>part=</c> (dot form, PartAction's rule) names the destination: exactly one of stock's
    /// valid destinations for that kerbal (a crewed part of the same vessel, not the kerbal's
    /// own, with a free seat and transfer allowed) must carry the name, so two identical pods of
    /// a docked pair are told apart by the source being one of them. FLIGHT only; TWO-PHASE on
    /// the default budget: OK once stock dismissed the transfer with <c>ItemMoved</c> and the
    /// kerbal sits in the destination.</para>
    ///
    /// <para><b>Answer.</b> OK payload <c>kerbal= pid= from= fromId= to= toId= crewFrom=
    /// crewTo=</c>: the vessel's persistentId, both parts' runtime names and flight ids, and the
    /// crew counts of both parts after the move. Line: <c>crewtransfer applied kerbal='..'
    /// pid= from= fromId= to= toId= crewFrom= crewTo=</c>.</para>
    /// </summary>
    internal static class TestCommandCrewTransfer
    {
        internal const string Verb = "CrewTransfer";
        internal const string KerbalKey = "kerbal";
        internal const string PartKey = "part";

        internal const string KerbalArgMissingReason = "crewtransfer-kerbal-arg-missing";
        internal const string PartArgMissingReason = "crewtransfer-part-arg-missing";
        internal const string NoActiveVesselReason = "crewtransfer-no-active-vessel";
        internal const string VesselNotReadyReason = "crewtransfer-vessel-not-ready";
        internal const string KerbalNotAboardReason = "crewtransfer-kerbal-not-aboard";
        internal const string TransferRunningReason = "crewtransfer-transfer-running";
        internal const string PartNotFoundReason = "crewtransfer-part-not-found";
        internal const string PartAmbiguousReason = "crewtransfer-part-ambiguous";
        internal const string TransferUnavailableReason = "crewtransfer-transfer-unavailable";

        // Post-call ERROR terminals: the transfer was opened and the kerbal did not move.
        internal const string TransferThrewReason = "crewtransfer-threw";
        internal const string VetoedReason = "crewtransfer-vetoed";
        internal const string TimeoutReason = "crewtransfer-timeout";

        /// <summary>The REJECTED vocabulary, in gate order. hlib.CREWTRANSFER_REASONS mirrors
        /// it (CrewTransferSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "crewtransfer-kerbal-arg-missing",
            "crewtransfer-part-arg-missing",
            "crewtransfer-no-active-vessel",
            "crewtransfer-vessel-not-ready",
            "crewtransfer-kerbal-not-aboard",
            "crewtransfer-transfer-running",
            "crewtransfer-part-not-found",
            "crewtransfer-part-ambiguous",
            "crewtransfer-transfer-unavailable",
        };

        /// <summary>The post-call ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "crewtransfer-threw",
            "crewtransfer-vetoed",
            "crewtransfer-timeout",
        };

        /// <summary>Parses the two REQUIRED args, kept verbatim. Returns null on success or the
        /// refusal reason (kerbal first).</summary>
        internal static string ParseArgs(string rawKerbal, string rawPart, out string kerbal, out string part)
        {
            kerbal = null;
            part = null;
            if (string.IsNullOrWhiteSpace(rawKerbal))
                return KerbalArgMissingReason;
            if (string.IsNullOrWhiteSpace(rawPart))
                return PartArgMissingReason;
            kerbal = rawKerbal;
            part = rawPart;
            return null;
        }

        /// <summary>
        /// The vessel gate, in the order the applier meets it: an active vessel, loaded and
        /// unpacked (stock lists destinations only among a live vessel's parts), the kerbal in
        /// one of its parts, and no other stock item transfer open (stock would dismiss the new
        /// one as already running). Returns null to proceed.
        /// </summary>
        internal static string DecideGate(bool hasVessel, bool loadedUnpacked, bool kerbalAboard,
            bool transferRunning)
        {
            if (!hasVessel) return NoActiveVesselReason;
            if (!loadedUnpacked) return VesselNotReadyReason;
            if (!kerbalAboard) return KerbalNotAboardReason;
            if (transferRunning) return TransferRunningReason;
            return null;
        }

        /// <summary>
        /// The destination pick. <paramref name="validNames"/> holds the runtime names of stock's
        /// VALID destinations (its own <c>validParts</c> list), <paramref name="allNames"/> the
        /// runtime names of every part of the vessel other than the kerbal's own. Exactly one
        /// valid destination must carry <paramref name="partName"/> (ordinal), its index into
        /// <paramref name="validNames"/> returned through <paramref name="index"/>. None valid
        /// but present on the vessel: the part is full or refuses transfers
        /// (transfer-unavailable); absent: not found; several valid: ambiguous, never a silent
        /// first pick. Returns null on success or the refusal reason.
        /// </summary>
        internal static string PickDestination(IList<string> validNames, IList<string> allNames,
            string partName, out int index)
        {
            index = -1;
            int matches = 0;
            if (validNames != null)
            {
                for (int i = 0; i < validNames.Count; i++)
                {
                    if (!string.Equals(validNames[i], partName, StringComparison.Ordinal)) continue;
                    matches++;
                    index = i;
                }
            }
            if (matches == 1) return null;
            index = -1;
            if (matches > 1) return PartAmbiguousReason;
            if (allNames != null)
            {
                for (int i = 0; i < allNames.Count; i++)
                    if (string.Equals(allNames[i], partName, StringComparison.Ordinal))
                        return TransferUnavailableReason;
            }
            return PartNotFoundReason;
        }

        /// <summary>
        /// One completion poll: OK once stock dismissed the transfer with <c>ItemMoved</c> AND the
        /// kerbal sits in the destination part; a timeout once <paramref name="elapsed"/> reaches
        /// <paramref name="budget"/>.
        /// </summary>
        internal static CrewTransferCompletionDecision DecideCompletion(bool dismissedMoved,
            bool kerbalInDestination, double elapsed, double budget)
        {
            if (dismissedMoved && kerbalInDestination) return CrewTransferCompletionDecision.Moved;
            if (elapsed >= budget) return CrewTransferCompletionDecision.Timeout;
            return CrewTransferCompletionDecision.StillWaiting;
        }

        /// <summary>The OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            string kerbal, uint pid, string fromPart, uint fromId, string toPart, uint toId,
            int crewFrom, int crewTo)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("kerbal", kerbal ?? string.Empty),
                new KeyValuePair<string, string>("pid", pid.ToString(ic)),
                new KeyValuePair<string, string>("from", fromPart ?? string.Empty),
                new KeyValuePair<string, string>("fromId", fromId.ToString(ic)),
                new KeyValuePair<string, string>("to", toPart ?? string.Empty),
                new KeyValuePair<string, string>("toId", toId.ToString(ic)),
                new KeyValuePair<string, string>("crewFrom", crewFrom.ToString(ic)),
                new KeyValuePair<string, string>("crewTo", crewTo.ToString(ic)),
            };
        }

        /// <summary>The grep-stable line logged on OK.</summary>
        internal static string FormatAppliedLine(
            string kerbal, uint pid, string fromPart, uint fromId, string toPart, uint toId,
            int crewFrom, int crewTo)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "crewtransfer applied kerbal='" + (kerbal ?? string.Empty) + "'"
                + " pid=" + pid.ToString(ic)
                + " from=" + (fromPart ?? string.Empty) + " fromId=" + fromId.ToString(ic)
                + " to=" + (toPart ?? string.Empty) + " toId=" + toId.ToString(ic)
                + " crewFrom=" + crewFrom.ToString(ic) + " crewTo=" + crewTo.ToString(ic);
        }
    }
}
