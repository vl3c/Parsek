using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>Which stock part-menu button a <c>PartAction</c> presses.</summary>
    internal enum PartActionKind
    {
        Unknown = 0,
        /// <summary>A docking port's "Undock" (<c>ModuleDockingNode.Undock</c>).</summary>
        Undock,
        /// <summary>A decoupler's "Decouple" (<c>ModuleDecouplerBase.Decouple</c>).</summary>
        Decouple,
        /// <summary>A command part's or docking port's "Control From Here"
        /// (<c>ModuleCommand.MakeReference</c> / <c>ModuleDockingNode.MakeReferenceTransform</c>).</summary>
        ControlFrom,
    }

    /// <summary>
    /// Pure half of the automation-only <c>PartAction</c> verb (vessel names, design
    /// <c>design-vessel-and-mission-names.md</c> section 15.3): a player's click on one button
    /// of one part's stock right-click menu (PAW), in FLIGHT.
    ///
    /// <para><b>Why.</b> Three vessel-name claims need a stock part event no mission or verb
    /// drove: an undock after a dock (the dock-absorb row turning into an UndockReturn link), a
    /// decouple after a load (stock's <c>onVesselWasModified</c> re-reads the vessel's naming
    /// part, so a rename that did not update it reverts), and "Control From Here" on a part
    /// below a decoupler (the staging race where stock's <c>ModuleDecouple.WaitAndSwitchFocus</c>
    /// renames the OLD vessel one Update after the split). Each act invokes the part module's
    /// own <c>KSPEvent</c> through its <c>BaseEvent</c>, which is exactly what the PAW button
    /// does, so stock and every Parsek hook run as for a player.</para>
    ///
    /// <para><b>Grammar.</b> <c>cmd=PartAction button=undock|decouple|controlfrom
    /// [part=&lt;part name&gt;] [pid=&lt;persistentId&gt;]</c>. <c>button=</c> REQUIRED, closed.
    /// <c>part=</c> is the part's runtime name (dot form, <c>Decoupler.1</c>); REQUIRED for
    /// <c>decouple</c> and <c>controlfrom</c>, OPTIONAL for <c>undock</c> (absent: the vessel's
    /// one docked port pair). <c>pid=</c> names a loaded, non-ghost vessel by persistentId;
    /// absent, the ACTIVE vessel. SINGLE-PHASE, <c>RequiresFlight</c>, default budget: every
    /// event runs synchronously (the undock and the decouple create the new vessel inside the
    /// call).</para>
    ///
    /// <para><b>Answer.</b> OK payload <c>button= pid= part= partId= event= newPid= newName=
    /// vesselsBefore= vesselsAfter= activePid= activeName=</c>: <c>newPid</c> / <c>newName</c>
    /// are the one vessel the event created (0 / empty when none, as for <c>controlfrom</c>),
    /// read at once, so a stock rename that lands one Update later is NOT in it. Line:
    /// <c>partaction applied button= pid= part='..' partId= event= newPid= newName='..' vessels=N->M
    /// activePid= activeName='..'</c>.</para>
    /// </summary>
    internal static class TestCommandPartAction
    {
        internal const string Verb = "PartAction";
        internal const string ButtonKey = "button";
        internal const string PartKey = "part";
        internal const string PidKey = "pid";

        internal const string UndockToken = "undock";
        internal const string DecoupleToken = "decouple";
        internal const string ControlFromToken = "controlfrom";

        /// <summary>The closed <c>button=</c> vocabulary. hlib.PARTACTION_BUTTON_VALUES mirrors it
        /// (PartActionSourceSyncTests).</summary>
        internal static readonly string[] ButtonValues = { UndockToken, DecoupleToken, ControlFromToken };

        internal const string ButtonArgMissingReason = "partaction-button-arg-missing";
        internal const string ButtonArgInvalidReason = "partaction-button-arg-invalid";
        internal const string PartArgMissingReason = "partaction-part-arg-missing";
        internal const string PidArgInvalidReason = "partaction-pid-arg-invalid";
        internal const string NoActiveVesselReason = "partaction-no-active-vessel";
        internal const string VesselNotFoundReason = "partaction-vessel-not-found";
        internal const string TargetIsGhostReason = "partaction-target-is-ghost";
        internal const string VesselUnloadedReason = "partaction-vessel-unloaded";
        internal const string PartNotFoundReason = "partaction-part-not-found";
        internal const string PartAmbiguousReason = "partaction-part-ambiguous";
        internal const string EventUnavailableReason = "partaction-event-unavailable";

        // Post-call ERROR terminal: the event was invoked and threw.
        internal const string EventThrewReason = "partaction-event-threw";

        /// <summary>The REJECTED vocabulary, in gate order. hlib.PARTACTION_REASONS mirrors it
        /// (PartActionSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "partaction-button-arg-missing",
            "partaction-button-arg-invalid",
            "partaction-part-arg-missing",
            "partaction-pid-arg-invalid",
            "partaction-no-active-vessel",
            "partaction-vessel-not-found",
            "partaction-target-is-ghost",
            "partaction-vessel-unloaded",
            "partaction-part-not-found",
            "partaction-part-ambiguous",
            "partaction-event-unavailable",
        };

        /// <summary>The post-call ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons = { "partaction-event-threw" };

        /// <summary>Parses the REQUIRED closed <c>button=</c> (case-sensitive, like every closed
        /// seam vocabulary). Returns null on success or the refusal reason.</summary>
        internal static string ParseButton(string raw, out PartActionKind kind)
        {
            kind = PartActionKind.Unknown;
            if (string.IsNullOrEmpty(raw)) return ButtonArgMissingReason;
            switch (raw)
            {
                case UndockToken: kind = PartActionKind.Undock; return null;
                case DecoupleToken: kind = PartActionKind.Decouple; return null;
                case ControlFromToken: kind = PartActionKind.ControlFrom; return null;
                default: return ButtonArgInvalidReason;
            }
        }

        /// <summary>The wire token of <paramref name="kind"/>.</summary>
        internal static string Token(PartActionKind kind)
        {
            switch (kind)
            {
                case PartActionKind.Undock: return UndockToken;
                case PartActionKind.Decouple: return DecoupleToken;
                case PartActionKind.ControlFrom: return ControlFromToken;
                default: return "unknown";
            }
        }

        /// <summary>True when <paramref name="kind"/> needs a <c>part=</c>: a decoupler and a
        /// control point are chosen by the player on one named part; an undock has one obvious
        /// port pair on an ordinary docked vessel.</summary>
        internal static bool RequiresPart(PartActionKind kind)
            => kind == PartActionKind.Decouple || kind == PartActionKind.ControlFrom;

        /// <summary>Parses <c>part=</c> for <paramref name="kind"/>: null (success) with
        /// <paramref name="partName"/> null when absent and optional, or the refusal reason.
        /// The name is matched VERBATIM against <c>Part.partInfo.name</c> (dot form).</summary>
        internal static string ParsePart(string raw, PartActionKind kind, out string partName)
        {
            partName = string.IsNullOrEmpty(raw) ? null : raw;
            if (partName == null && RequiresPart(kind)) return PartArgMissingReason;
            return null;
        }

        /// <summary>Parses the OPTIONAL <c>pid=</c> (invariant decimal uint, nonzero; the
        /// Recover / RenameVessel grammar).</summary>
        internal static string ParsePid(string raw, out uint pid, out bool hasPid)
        {
            pid = 0;
            hasPid = false;
            if (string.IsNullOrEmpty(raw)) return null;
            if (!uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed)
                || parsed == 0)
                return PidArgInvalidReason;
            pid = parsed;
            hasPid = true;
            return null;
        }

        /// <summary>The vessel gate: found (by pid, else the active one), not a ghost map
        /// vessel, and loaded (a part menu exists only on a loaded vessel's parts). Null to
        /// proceed.</summary>
        internal static string DecideVessel(bool hasPid, bool found, bool isGhostMapVessel, bool loaded)
        {
            if (!found) return hasPid ? VesselNotFoundReason : NoActiveVesselReason;
            if (isGhostMapVessel) return TargetIsGhostReason;
            if (!loaded) return VesselUnloadedReason;
            return null;
        }

        /// <summary>The stock <c>KSPEvent</c> names an act presses, in lookup order: the
        /// part-menu buttons' method names in KSP 1.12.5 (decompiled).</summary>
        internal static string[] EventNamesFor(PartActionKind kind)
        {
            switch (kind)
            {
                case PartActionKind.Undock: return new[] { "Undock" };
                case PartActionKind.Decouple: return new[] { "Decouple" };
                case PartActionKind.ControlFrom: return new[] { "MakeReference", "MakeReferenceTransform" };
                default: return new string[0];
            }
        }

        /// <summary>
        /// The candidate gate, after the applier counted the vessel's parts: with a
        /// <c>part=</c>, how many parts carry that name (<paramref name="namedParts"/>); and how
        /// many distinct buttons the act could press (<paramref name="candidates"/>: a part
        /// module with the act's event active and shown in flight, one per docked port PAIR for
        /// an undock). Exactly one candidate proceeds; none is <c>part-not-found</c> (a named
        /// part the vessel does not carry) or <c>event-unavailable</c> (no live button); more
        /// than one is <c>part-ambiguous</c>, never a silent first pick.
        /// </summary>
        internal static string DecideCandidates(string partName, int namedParts, int candidates)
        {
            if (partName != null && namedParts == 0) return PartNotFoundReason;
            if (candidates == 0) return EventUnavailableReason;
            if (candidates > 1) return PartAmbiguousReason;
            return null;
        }

        /// <summary>
        /// Folds the docking ports whose Undock is live into distinct PAIRS: stock shows Undock
        /// on both ports of a docked pair, and either press undocks the same joint. Each entry is
        /// (port id, the id of the port it is docked to, 0 when unknown); a port whose partner is
        /// already listed is the same pair. Returns the kept ports' indexes, in order.
        /// </summary>
        internal static List<int> FoldUndockPairs(IList<KeyValuePair<uint, uint>> ports)
        {
            var kept = new List<int>();
            if (ports == null) return kept;
            var seen = new HashSet<uint>();
            for (int i = 0; i < ports.Count; i++)
            {
                uint self = ports[i].Key;
                uint other = ports[i].Value;
                if (seen.Contains(self) || (other != 0 && seen.Contains(other))) continue;
                seen.Add(self);
                if (other != 0) seen.Add(other);
                kept.Add(i);
            }
            return kept;
        }

        /// <summary>The pids present after the event and absent before it, in after-order:
        /// the vessel(s) an undock or a decouple created.</summary>
        internal static List<uint> NewVesselPids(IEnumerable<uint> before, IEnumerable<uint> after)
        {
            var created = new List<uint>();
            var old = new HashSet<uint>();
            if (before != null) foreach (uint p in before) old.Add(p);
            if (after != null)
                foreach (uint p in after)
                    if (!old.Contains(p) && !created.Contains(p)) created.Add(p);
            return created;
        }

        /// <summary>The OK payload, in wire order.</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            PartActionKind kind, uint pid, string partName, uint partId, string eventName,
            uint newPid, string newName, int vesselsBefore, int vesselsAfter,
            uint activePid, string activeName)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("button", Token(kind)),
                new KeyValuePair<string, string>("pid", pid.ToString(ic)),
                new KeyValuePair<string, string>("part", partName ?? string.Empty),
                new KeyValuePair<string, string>("partId", partId.ToString(ic)),
                new KeyValuePair<string, string>("event", eventName ?? string.Empty),
                new KeyValuePair<string, string>("newPid", newPid.ToString(ic)),
                new KeyValuePair<string, string>("newName", newName ?? string.Empty),
                new KeyValuePair<string, string>("vesselsBefore", vesselsBefore.ToString(ic)),
                new KeyValuePair<string, string>("vesselsAfter", vesselsAfter.ToString(ic)),
                new KeyValuePair<string, string>("activePid", activePid.ToString(ic)),
                new KeyValuePair<string, string>("activeName", activeName ?? string.Empty),
            };
        }

        /// <summary>The grep-stable success line. Names are single-quoted so a spec can anchor
        /// a name containing spaces.</summary>
        internal static string FormatAppliedLine(
            PartActionKind kind, uint pid, string partName, uint partId, string eventName,
            uint newPid, string newName, int vesselsBefore, int vesselsAfter,
            uint activePid, string activeName)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "partaction applied button=" + Token(kind)
                + " pid=" + pid.ToString(ic)
                + " part='" + (partName ?? string.Empty) + "'"
                + " partId=" + partId.ToString(ic)
                + " event=" + (eventName ?? string.Empty)
                + " newPid=" + newPid.ToString(ic)
                + " newName='" + (newName ?? string.Empty) + "'"
                + " vessels=" + vesselsBefore.ToString(ic) + "->" + vesselsAfter.ToString(ic)
                + " activePid=" + activePid.ToString(ic)
                + " activeName='" + (activeName ?? string.Empty) + "'";
        }
    }
}
