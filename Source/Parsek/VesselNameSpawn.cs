using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Vessel names at a Parsek spawn (design-vessel-and-mission-names.md 7.5):
    /// <list type="bullet">
    /// <item>the spawn node is stamped only when a decision COVERS the spawning leg: its
    /// <c>VESSEL.name</c> takes that leg's post-apply name, and the priority PART's
    /// <c>VESSELNAMING</c> <c>name</c> takes it too (stock's tie rule,
    /// <see cref="VesselRenameWriter.FindPriorityNamingIndex"/>), so the spawned vessel does not
    /// revert to an old part naming on its first <c>onVesselWasModified</c>. Otherwise the node
    /// is left exactly as it was (raw snapshot name, possibly a <c>#autoLOC</c> tag);</item>
    /// <item>the regenerated part flightIDs are carried into the node's dock records
    /// (<c>DOCKEDVESSEL</c> / <c>DOCKEDVESSEL_Other</c> <c>rootUId</c>, and the docking port's
    /// <c>dockUId</c>), so a spawned docked stack still undocks with stock's pre-dock name restore
    /// and finds its partner port.</item>
    /// </list>
    /// Snapshot sidecars are never rewritten (a name-only rewrite would advance the <c>.prec</c>
    /// epoch, the F9 stale-sidecar trap). The decisions over a node are pure; the production
    /// wrapper resolves over the live recordings.
    /// </summary>
    internal static class VesselNameSpawn
    {
        internal const string Tag = "VesselNames";

        internal const string StampCovered = "covered";
        internal const string StampNoSource = "no-source";
        internal const string StampNoIdentity = "no-identity";
        internal const string StampNoDecision = "no-decision";
        internal const string StampUncovered = "uncovered";
        internal const string StampUnclassified = "unclassified";

        /// <summary>
        /// The name to stamp on the spawn node of <paramref name="source"/> (the spawning leg):
        /// its post-apply <see cref="Recording.VesselName"/> when its identity's decision covers
        /// it, else null with <paramref name="why"/> naming the reason. Run after the apply pass
        /// over <paramref name="res"/>. Pure over the resolution and the store.
        /// </summary>
        internal static string ResolveSpawnStampName(
            VesselIdentity.Resolution res, Recording source, out string why)
        {
            if (source == null || string.IsNullOrEmpty(source.RecordingId)) { why = StampNoSource; return null; }
            if (res == null || !res.TryGetIdentity(source.RecordingId, out VesselIdentity.Identity identity))
            {
                why = StampNoIdentity;
                return null;
            }
            VesselNameDecision d = VesselNameStore.ResolveDecision(identity);
            if (d == null) { why = StampNoDecision; return null; }
            if (d.UncoveredLegIds.Contains(source.RecordingId)) { why = StampUncovered; return null; }
            if (!d.CoveredLegIds.Contains(source.RecordingId)) { why = StampUnclassified; return null; }
            why = StampCovered;
            string name = source.VesselName;
            return VesselRenameWriter.IsWritableName(name) ? name : d.Name;
        }

        /// <summary>
        /// Sets <c>VESSEL.name</c> and the priority PART's <c>VESSELNAMING</c> <c>name</c> (only
        /// its name: type and priority untouched) of <paramref name="vesselNode"/>. Returns the
        /// PART index written, or -1 when no part names the vessel. Pure.
        /// </summary>
        internal static int StampSpawnNodeName(ConfigNode vesselNode, string name)
        {
            if (vesselNode == null || !VesselRenameWriter.IsWritableName(name)) return -1;
            vesselNode.SetValue("name", name, true);
            ConfigNode[] parts = vesselNode.GetNodes("PART");
            var namings = new List<VesselNaming>(parts.Length);
            var namingNodes = new List<ConfigNode>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                ConfigNode vn = parts[i].GetNode("VESSELNAMING");
                namingNodes.Add(vn);
                if (vn == null) { namings.Add(null); continue; }
                int priority = 0;
                string raw = vn.GetValue("priority");
                if (raw != null) int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out priority);
                namings.Add(new VesselNaming { vesselName = vn.GetValue("name"), namingPriority = priority });
            }
            int idx = VesselRenameWriter.FindPriorityNamingIndex(namings);
            if (idx >= 0) namingNodes[idx].SetValue("name", name, true);
            return idx;
        }

        /// <summary>
        /// Carries regenerated part flightIDs (<paramref name="uidMap"/>: old -> new) into every
        /// dock record of <paramref name="vesselNode"/>: each PART MODULE's <c>DOCKEDVESSEL</c> and
        /// <c>DOCKEDVESSEL_Other</c> <c>rootUId</c> (stock <c>DockedVesselInfo</c>, read back by
        /// <c>Part.Undock</c> to re-root the leaving half and restore its pre-dock name) and the
        /// module's <c>dockUId</c> (<c>ModuleDockingNode.dockedPartUId</c>, the partner part
        /// <c>FindOtherNode</c> looks up). A value the map does not hold is left alone. Returns the
        /// values rewritten. Pure.
        /// </summary>
        internal static int RemapDockRecordUids(ConfigNode vesselNode, IDictionary<uint, uint> uidMap)
        {
            if (vesselNode == null || uidMap == null || uidMap.Count == 0) return 0;
            int count = 0;
            foreach (ConfigNode part in vesselNode.GetNodes("PART"))
                foreach (ConfigNode module in part.GetNodes("MODULE"))
                {
                    count += RemapUidValue(module, "dockUId", uidMap);
                    foreach (ConfigNode info in module.GetNodes("DOCKEDVESSEL"))
                        count += RemapUidValue(info, "rootUId", uidMap);
                    foreach (ConfigNode info in module.GetNodes("DOCKEDVESSEL_Other"))
                        count += RemapUidValue(info, "rootUId", uidMap);
                }
            return count;
        }

        private static int RemapUidValue(ConfigNode node, string key, IDictionary<uint, uint> uidMap)
        {
            string raw = node.GetValue(key);
            if (raw == null || !uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint old)
                || old == 0 || !uidMap.TryGetValue(old, out uint now) || now == old)
                return 0;
            node.SetValue(key, now.ToString(CultureInfo.InvariantCulture), true);
            return 1;
        }

        /// <summary>
        /// Production: stamps <paramref name="spawnNode"/> for a spawn of <paramref name="source"/>
        /// (both the regenerate and the preserve-identity path). Resolves over the live
        /// recordings and the link rows, runs the apply pass (so an unclassified spawning leg is
        /// classified first), then stamps only a covered leg. Never throws; a null source or an
        /// empty store leaves the node untouched.
        /// </summary>
        internal static string ApplySpawnStamp(ConfigNode spawnNode, Recording source, string context)
        {
            if (spawnNode == null || source == null || VesselNameStore.DecisionCount == 0) return StampNoDecision;
            try
            {
                List<RecordingTree> trees = VesselNameStore.GatherTrees();
                List<Recording> loose = VesselNameStore.GatherLoose(trees);
                VesselIdentity.Resolution res = VesselNameStore.ResolveUniverse(trees, loose);
                if (!string.IsNullOrEmpty(source.RecordingId) && !res.ByRecordingId.ContainsKey(source.RecordingId)
                    && VesselIdentity.IsVesselLeg(source))
                {
                    loose.Add(source);
                    res = VesselNameStore.ResolveUniverse(trees, loose);
                }
                VesselNameStore.ApplyTo(res, "spawn-stamp");
                string name = ResolveSpawnStampName(res, source, out string why);
                if (name == null)
                {
                    ParsekLog.Verbose(Tag, "Spawn node left unstamped: rec=" + (source.RecordingId ?? "<null>") +
                        " reason=" + why + " context=" + (context ?? "<none>"));
                    return why;
                }
                string from = spawnNode.GetValue("name");
                int part = StampSpawnNodeName(spawnNode, name);
                ParsekLog.Info(Tag, "Spawn node stamped: rec=" + source.RecordingId + " '" + (from ?? "") + "' -> '" +
                    name + "' namingPart=" + (part >= 0 ? "part[" + part.ToString(CultureInfo.InvariantCulture) + "]" : "none") +
                    " context=" + (context ?? "<none>"));
                return why;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Spawn stamp threw for rec=" + (source.RecordingId ?? "<null>") + ": " +
                    ex.GetType().Name + ": " + ex.Message);
                return "threw";
            }
        }
    }
}
