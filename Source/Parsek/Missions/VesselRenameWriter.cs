using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Writes a vessel name decision through to a stock vessel the way the stock rename
    /// dialog does (design-vessel-and-mission-names.md 7.3), emulating the private
    /// <c>Vessel.onVesselRenameAccept(string, VesselType)</c> without changing the type:
    /// <list type="number">
    /// <item>refuse a blank name (stock <c>Vessel.IsValidVesselName</c> rejects null / empty;
    /// Parsek also trims);</item>
    /// <item>set <c>Vessel.vesselName</c>, and <c>ProtoVessel.vesselName</c> too when the vessel
    /// is unloaded (an unloaded vessel's proto is what the next save writes);</item>
    /// <item>set the priority naming part's <c>VesselNaming.vesselName</c> (stock's tie rule,
    /// <see cref="FindPriorityNamingIndex"/>), loaded parts or proto part snapshots, so the
    /// next <c>onVesselWasModified</c> re-derives the same name instead of reverting it
    /// (<c>Vessel.vesselNamedBy</c> is null after a load and stock's dialog then skips the
    /// part);</item>
    /// <item>fire <c>GameEvents.onVesselRename</c> once (contracts, the PAW, the info header).
    /// Parsek has no handler on it, so no re-entry guard is needed.</item>
    /// </list>
    /// A ProtoVessel-only entry (no Vessel object yet, e.g. during OnLoad) gets the name and the
    /// naming part with no event. Never used for a ghost map vessel (their names are
    /// Parsek-owned, design 7.8). The tie rule and the name check are pure.
    /// </summary>
    internal static class VesselRenameWriter
    {
        internal const string Tag = "VesselNames";

        /// <summary>A name stock accepts and Parsek keeps: non-blank after trimming. Pure.</summary>
        internal static bool IsWritableName(string name)
            => !string.IsNullOrEmpty(name) && name.Trim().Length > 0;

        /// <summary>
        /// Stock's priority naming part rule (decompiled <c>VesselNaming.FindPriorityNamePart</c>,
        /// KSP 1.12.5): the FIRST entry in order with the strictly highest
        /// <c>namingPriority</c> above 0; a null naming counts as priority 0. -1 when no entry
        /// qualifies. Pure.
        /// </summary>
        internal static int FindPriorityNamingIndex(IList<VesselNaming> namings)
        {
            if (namings == null) return -1;
            int best = -1;
            int bestPriority = 0;
            for (int i = 0; i < namings.Count; i++)
            {
                VesselNaming n = namings[i];
                if (n == null || n.namingPriority <= bestPriority) continue;
                bestPriority = n.namingPriority;
                best = i;
            }
            return best;
        }

        /// <summary>
        /// Sets the priority naming entry's name (only its name: type and priority untouched).
        /// Returns its index, or -1 when no entry names the vessel. Pure.
        /// </summary>
        internal static int ApplyNameToPriorityNaming(IList<VesselNaming> namings, string name)
        {
            int idx = FindPriorityNamingIndex(namings);
            if (idx >= 0) namings[idx].vesselName = name;
            return idx;
        }

        /// <summary>
        /// The naming part step alone (design 7.1 / 7.3 step 3): over the loaded parts, or over
        /// the proto part snapshots when the vessel has no loaded parts. Returns where it wrote
        /// ("part" / "proto-part" / "none").
        /// </summary>
        internal static string UpdatePriorityNamingPart(Vessel v, string name)
        {
            if (v == null) return "none";
            if (v.parts != null && v.parts.Count > 0)
            {
                var namings = new List<VesselNaming>(v.parts.Count);
                for (int i = 0; i < v.parts.Count; i++)
                    namings.Add(v.parts[i] != null ? v.parts[i].vesselNaming : null);
                return ApplyNameToPriorityNaming(namings, name) >= 0 ? "part" : "none";
            }
            return UpdateProtoPriorityNamingPart(v.protoVessel, name);
        }

        private static string UpdateProtoPriorityNamingPart(ProtoVessel pv, string name)
        {
            if (pv?.protoPartSnapshots == null || pv.protoPartSnapshots.Count == 0) return "none";
            var namings = new List<VesselNaming>(pv.protoPartSnapshots.Count);
            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
                namings.Add(pv.protoPartSnapshots[i] != null ? pv.protoPartSnapshots[i].vesselNaming : null);
            return ApplyNameToPriorityNaming(namings, name) >= 0 ? "proto-part" : "none";
        }

        /// <summary>
        /// The stock-equivalent rename of a live <see cref="Vessel"/> (loaded or not). False with a
        /// Warn when refused (blank name, ghost map vessel, no vessel); the decision is kept and
        /// the next pass retries (design 13).
        /// </summary>
        internal static bool ApplyStockName(Vessel v, string name, string reason)
        {
            if (v == null)
            {
                ParsekLog.Warn(Tag, "Write-through refused: no vessel name='" + (name ?? "") + "' reason=" + (reason ?? "<none>"));
                return false;
            }
            if (!IsWritableName(name))
            {
                ParsekLog.Warn(Tag, "Write-through refused: blank name for vessel pid=" +
                    v.persistentId.ToString(CultureInfo.InvariantCulture) + " reason=" + (reason ?? "<none>"));
                return false;
            }
            if (GhostMapPresence.IsGhostMapVessel(v.persistentId))
            {
                ParsekLog.Warn(Tag, "Write-through refused: ghost map vessel pid=" +
                    v.persistentId.ToString(CultureInfo.InvariantCulture) + " reason=" + (reason ?? "<none>"));
                return false;
            }
            string from = v.vesselName;
            v.vesselName = name;
            bool loaded = v.loaded;
            if (!loaded && v.protoVessel != null) v.protoVessel.vesselName = name;
            string naming = UpdatePriorityNamingPart(v, name);
            GameEvents.onVesselRename.Fire(new GameEvents.HostedFromToAction<Vessel, string>(v, from, name));
            ParsekLog.Info(Tag,
                "Write-through applied: vessel pid=" + v.persistentId.ToString(CultureInfo.InvariantCulture) +
                " guid=" + v.id.ToString("N") + " '" + (from ?? "") + "' -> '" + name + "' loaded=" +
                (loaded ? "true" : "false") + " namingPart=" + naming + " reason=" + (reason ?? "<none>"));
            return true;
        }

        /// <summary>
        /// The ProtoVessel-only variant (design 7.3 last paragraph): name plus the naming part over
        /// the part snapshots, no event (there is no Vessel object to host one).
        /// </summary>
        internal static bool ApplyProtoName(ProtoVessel pv, string name, string reason)
        {
            if (pv == null || !IsWritableName(name))
            {
                ParsekLog.Warn(Tag, "Write-through refused (proto): " + (pv == null ? "no proto" : "blank name") +
                    " reason=" + (reason ?? "<none>"));
                return false;
            }
            if (GhostMapPresence.IsGhostMapVessel(pv.persistentId))
            {
                ParsekLog.Warn(Tag, "Write-through refused (proto): ghost map vessel pid=" +
                    pv.persistentId.ToString(CultureInfo.InvariantCulture) + " reason=" + (reason ?? "<none>"));
                return false;
            }
            string from = pv.vesselName;
            pv.vesselName = name;
            string naming = UpdateProtoPriorityNamingPart(pv, name);
            ParsekLog.Info(Tag,
                "Write-through applied (proto): pid=" + pv.persistentId.ToString(CultureInfo.InvariantCulture) +
                " guid=" + pv.vesselID.ToString("N") + " '" + (from ?? "") + "' -> '" + name +
                "' namingPart=" + naming + " reason=" + (reason ?? "<none>"));
            return true;
        }
    }
}
