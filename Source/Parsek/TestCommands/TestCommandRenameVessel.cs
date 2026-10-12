using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure half of the automation-only <c>RenameVessel</c> verb (vessel names, design
    /// <c>design-vessel-and-mission-names.md</c> section 15.3): a player's accept in the stock
    /// rename dialog, for one live vessel in FLIGHT or the Tracking Station.
    ///
    /// <para><b>Why the stock accept and not a name write.</b> Parsek captures a stock rename
    /// ONLY through its Harmony Prefix / Postfix pair on the private
    /// <c>Vessel.onVesselRenameAccept(string, VesselType)</c> (the dialog's accept callback;
    /// <c>VesselRenameAcceptCapturePatch</c>), never through <c>onVesselRename</c>. So the
    /// applier invokes that method by reflection with the vessel's CURRENT type: stock renames
    /// the vessel, updates <c>vesselNamedBy</c>, fires <c>onVesselRename</c>, and the capture
    /// pair records the decision exactly as for a player. Writing <c>vesselName</c> directly
    /// would rename the vessel and record nothing.</para>
    ///
    /// <para><b>Grammar.</b> <c>cmd=RenameVessel name=&lt;new name&gt; [pid=&lt;persistentId&gt;]</c>.
    /// <c>name=</c> is REQUIRED and must not be blank (the stock dialog accepts any non-empty
    /// name; Parsek refuses a blank one). <c>pid=</c> names a live non-ghost vessel by its
    /// persistentId; absent, the ACTIVE vessel. Issued in FLIGHT or the Tracking Station
    /// (<c>RequiresVesselScene</c>; the TS vessel info header renames an UNLOADED vessel, whose
    /// naming part the capture writes on its proto part snapshots, and has no active vessel, so
    /// a TS rename names its <c>pid=</c>).
    /// SINGLE-PHASE on the default budget: the accept runs synchronously and the capture runs
    /// inside it.</para>
    ///
    /// <para><b>Answer.</b> OK payload <c>pid= old= new= type= active= changed= legs= decision=
    /// decisionName=</c>: <c>old</c> is the name the vessel carried just before the accept (so
    /// a rename doubles as a read of the live stock name), <c>legs</c> the recorded legs of the
    /// vessel's identity (0 for a vessel Parsek never recorded), and <c>decision</c> /
    /// <c>decisionName</c> the source and name of the decision the store holds for the identity
    /// after the call (<c>none</c> / empty when it holds none). Line:
    /// <c>renamevessel applied pid= old='..' new='..' type= active= changed= legs= decision=
    /// decisionName='..'</c>.</para>
    /// </summary>
    internal static class TestCommandRenameVessel
    {
        internal const string Verb = "RenameVessel";
        internal const string NameKey = "name";
        internal const string PidKey = "pid";

        internal const string NameArgMissingReason = "renamevessel-name-arg-missing";
        internal const string NameArgInvalidReason = "renamevessel-name-arg-invalid";
        internal const string PidArgInvalidReason = "renamevessel-pid-arg-invalid";
        internal const string NoActiveVesselReason = "renamevessel-no-active-vessel";
        internal const string VesselNotFoundReason = "renamevessel-vessel-not-found";
        internal const string TargetIsGhostReason = "renamevessel-target-is-ghost";
        internal const string AcceptUnavailableReason = "renamevessel-accept-unavailable";

        // Post-call ERROR terminals: the accept ran (or was attempted) and did not rename.
        internal const string AcceptThrewReason = "renamevessel-accept-threw";
        internal const string NotAppliedReason = "renamevessel-not-applied";

        /// <summary>The REJECTED vocabulary, in gate order. hlib.RENAMEVESSEL_REASONS mirrors
        /// it (RenameVesselSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "renamevessel-name-arg-missing",
            "renamevessel-name-arg-invalid",
            "renamevessel-pid-arg-invalid",
            "renamevessel-no-active-vessel",
            "renamevessel-vessel-not-found",
            "renamevessel-target-is-ghost",
            "renamevessel-accept-unavailable",
        };

        /// <summary>The post-call ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "renamevessel-accept-threw",
            "renamevessel-not-applied",
        };

        /// <summary>The <c>decision=</c> value when the store holds no decision for the
        /// vessel's identity (or the vessel has no identity).</summary>
        internal const string NoDecisionToken = "none";

        /// <summary>Parses <c>name=</c>; returns null on success or the refusal reason.
        /// Absent or empty is missing; whitespace-only is invalid (Parsek's own rename refuses
        /// a blank name). The name is passed to stock VERBATIM, never trimmed: the stock
        /// dialog does not trim either.</summary>
        internal static string ParseName(string raw, out string name)
        {
            name = null;
            if (string.IsNullOrEmpty(raw))
                return NameArgMissingReason;
            if (string.IsNullOrWhiteSpace(raw))
                return NameArgInvalidReason;
            name = raw;
            return null;
        }

        /// <summary>Parses the OPTIONAL <c>pid=</c> (invariant, decimal uint, nonzero, the
        /// Recover grammar). Returns null on success (with <paramref name="hasPid"/> false when
        /// the arg is absent or empty) or the refusal reason.</summary>
        internal static string ParsePid(string raw, out uint pid, out bool hasPid)
        {
            pid = 0;
            hasPid = false;
            if (string.IsNullOrEmpty(raw))
                return null;
            if (!uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed)
                || parsed == 0)
                return PidArgInvalidReason;
            pid = parsed;
            hasPid = true;
            return null;
        }

        /// <summary>
        /// The target gate, in the order the applier meets it: the vessel (named by pid, else
        /// the active one) must exist, must not be a ghost map vessel (its name is Parsek's,
        /// and the capture ignores it), and the stock accept method must resolve. Returns null
        /// to proceed.
        /// </summary>
        internal static string DecideTarget(bool hasPid, bool vesselFound, bool isGhostMapVessel,
            bool acceptAvailable)
        {
            if (!vesselFound) return hasPid ? VesselNotFoundReason : NoActiveVesselReason;
            if (isGhostMapVessel) return TargetIsGhostReason;
            if (!acceptAvailable) return AcceptUnavailableReason;
            return null;
        }

        /// <summary>True when the accept did what it was asked: the vessel now carries the
        /// requested name (ordinal).</summary>
        internal static bool WasApplied(string requested, string after)
            => string.Equals(requested, after, StringComparison.Ordinal);

        /// <summary>
        /// The decision the store holds for an identity, read without side effects: the
        /// highest-seq decision indexed under any of <paramref name="guids"/> (the rule
        /// <c>VesselNameStore.ResolveDecision</c> applies, minus its fold). Null when none.
        /// </summary>
        internal static VesselNameDecision PickDecision(IEnumerable<string> guids,
            Func<string, VesselNameDecision> findByGuid)
        {
            if (guids == null || findByGuid == null) return null;
            VesselNameDecision best = null;
            foreach (string g in guids)
            {
                VesselNameDecision d = findByGuid(g);
                if (d != null && (best == null || d.Seq > best.Seq)) best = d;
            }
            return best;
        }

        /// <summary>The OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            uint pid, string oldName, string newName, string vesselType, bool active,
            int legs, VesselNameDecision decision)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("pid", pid.ToString(ic)),
                new KeyValuePair<string, string>("old", oldName ?? string.Empty),
                new KeyValuePair<string, string>("new", newName ?? string.Empty),
                new KeyValuePair<string, string>("type", vesselType ?? string.Empty),
                new KeyValuePair<string, string>("active", active ? "true" : "false"),
                new KeyValuePair<string, string>("changed",
                    !string.Equals(oldName, newName, StringComparison.Ordinal) ? "true" : "false"),
                new KeyValuePair<string, string>("legs", legs.ToString(ic)),
                new KeyValuePair<string, string>("decision",
                    decision != null ? decision.Source.ToString() : NoDecisionToken),
                new KeyValuePair<string, string>("decisionName",
                    decision != null ? decision.Name ?? string.Empty : string.Empty),
            };
        }

        /// <summary>The grep-stable success line. Names are single-quoted so a spec can anchor
        /// a name containing spaces.</summary>
        internal static string FormatAppliedLine(
            uint pid, string oldName, string newName, string vesselType, bool active,
            int legs, VesselNameDecision decision)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "renamevessel applied pid=" + pid.ToString(ic)
                + " old='" + (oldName ?? string.Empty) + "'"
                + " new='" + (newName ?? string.Empty) + "'"
                + " type=" + (vesselType ?? string.Empty)
                + " active=" + (active ? "true" : "false")
                + " changed=" + (!string.Equals(oldName, newName, StringComparison.Ordinal) ? "true" : "false")
                + " legs=" + legs.ToString(ic)
                + " decision=" + (decision != null ? decision.Source.ToString() : NoDecisionToken)
                + " decisionName='" + (decision != null ? decision.Name ?? string.Empty : string.Empty) + "'";
        }
    }
}
