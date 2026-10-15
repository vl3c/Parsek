using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>Why a re-keyed launch guid continues another guid's identity. Persisted by name.</summary>
    internal enum VesselIdentityLinkKind
    {
        /// <summary>A Parsek spawn: <c>VesselSpawner.RegenerateVesselIdentity</c> gave the spawned
        /// vessel a fresh guid (or the load-time backfill found a pre-upgrade genuine spawn).</summary>
        Spawn = 0,
        /// <summary>A spawned vessel the player flew again: the resumed recording keeps its old
        /// guid while the live vessel carries the spawn's.</summary>
        Resume = 1,
        /// <summary>An undock returned a docked (absorbed) ship under a fresh guid.</summary>
        UndockReturn = 2,
    }

    /// <summary>One link row (design 6.1): <see cref="Guid"/> continues <see cref="ToGuid"/>'s identity.</summary>
    internal struct VesselIdentityLink
    {
        internal string Guid;
        internal string ToGuid;
        internal VesselIdentityLinkKind Kind;
    }

    /// <summary>One dock-absorb row (design 6.1 / 7.7): the root part flightID an absorbed
    /// vessel had at dock time, and its launch guid.</summary>
    internal struct DockAbsorbRow
    {
        internal uint RootPartUId;
        internal string Guid;
    }

    /// <summary>
    /// Link rows and dock-absorb rows (design-vessel-and-mission-names.md 6.1, 6.2 edge 4, 7.5,
    /// 7.7): KSP re-keys a ship that stays the same physical vessel (a Parsek spawn, a resumed
    /// spawned vessel, the ship that leaves a dock), and a link row joins the fresh guid to the
    /// identity it continues. Dock-absorb rows remember which guid an absorbed vessel had, keyed
    /// by its root part flightID, so the undock that returns it can be linked. Same lifecycle
    /// as the decisions: static memory, persisted in <c>VESSEL_NAMES</c> (LINK / DOCK_ABSORB),
    /// read on the cold path only. Adding a link schedules an apply pass (next host tick), so the
    /// live write rule names the re-keyed vessel.
    /// </summary>
    internal static partial class VesselNameStore
    {
        internal const string LinkNodeName = "LINK";
        internal const string DockAbsorbNodeName = "DOCK_ABSORB";

        private static readonly string EmptyGuidN = System.Guid.Empty.ToString("N", CultureInfo.InvariantCulture);

        private static readonly Dictionary<string, VesselIdentityLink> links =
            new Dictionary<string, VesselIdentityLink>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<uint, DockAbsorbRow> dockAbsorbs = new Dictionary<uint, DockAbsorbRow>();
        private static bool linkApplyDue;

        internal static int LinkCount => links.Count;
        internal static int DockAbsorbCount => dockAbsorbs.Count;

        /// <summary>True when a link was added since the last apply pass.</summary>
        internal static bool LinkApplyDue => linkApplyDue;

        /// <summary>The link row of <paramref name="guid"/>, if any.</summary>
        internal static bool TryGetLink(string guid, out VesselIdentityLink link)
        {
            link = default;
            string g = VesselLaunchIdentity.NormalizeGuid(guid);
            return g != null && links.TryGetValue(g, out link);
        }

        /// <summary>
        /// True when <paramref name="liveGuid"/> continues <paramref name="targetGuid"/> through
        /// the link rows (a Parsek spawn, a resumed spawn or an undock re-keys a ship; each link
        /// points from the fresh guid to the identity it continues). Follows the chain from the
        /// live guid, bounded and cycle-safe. False for an unknown guid on either side, and for
        /// equal guids (no link is needed there).
        /// </summary>
        internal static bool GuidContinuesIdentity(string liveGuid, string targetGuid)
        {
            string current = NormalizeKnownGuid(liveGuid);
            string target = NormalizeKnownGuid(targetGuid);
            if (current == null || target == null || links.Count == 0) return false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int hop = 0; hop < 32; hop++)
            {
                if (!seen.Add(current)) return false;
                if (!links.TryGetValue(current, out VesselIdentityLink link)) return false;
                string next = NormalizeKnownGuid(link.ToGuid);
                if (next == null) return false;
                if (string.Equals(next, target, StringComparison.OrdinalIgnoreCase)) return true;
                current = next;
            }
            return false;
        }

        /// <summary>The dock-absorb row keyed by <paramref name="rootPartUId"/>, if any.</summary>
        internal static bool TryGetDockAbsorb(uint rootPartUId, out DockAbsorbRow row)
            => dockAbsorbs.TryGetValue(rootPartUId, out row);

        /// <summary>Every link as (re-keyed guid, guid it continues): the edge-4 input.</summary>
        internal static List<KeyValuePair<string, string>> GuidLinks()
        {
            var list = new List<KeyValuePair<string, string>>(links.Count);
            foreach (VesselIdentityLink l in links.Values)
                list.Add(new KeyValuePair<string, string>(l.Guid, l.ToGuid));
            return list;
        }

        /// <summary>The identity resolution every store pass uses: the recordings plus the link rows.</summary>
        internal static VesselIdentity.Resolution ResolveUniverse(
            IEnumerable<RecordingTree> trees, IEnumerable<Recording> loose)
            => VesselIdentity.Resolve(trees, loose, links.Count > 0 ? GuidLinks() : null);

        /// <summary>The normalized guid, or null when unknown or the empty guid. Pure.</summary>
        internal static string NormalizeKnownGuid(string guid)
        {
            string g = VesselLaunchIdentity.NormalizeGuid(guid);
            return g == null || string.Equals(g, EmptyGuidN, StringComparison.OrdinalIgnoreCase) ? null : g;
        }

        /// <summary>
        /// Adds the link row <paramref name="guid"/> -> <paramref name="toGuid"/>. Refused (false)
        /// for an unknown or empty guid on either side and for a self link. Idempotent: a guid
        /// already linked keeps its first row (a second, different target is logged and
        /// ignored). An added link schedules an apply pass (<see cref="SelfHealTick"/>).
        /// </summary>
        internal static bool AddLink(string guid, string toGuid, VesselIdentityLinkKind kind, string reason)
        {
            string from = NormalizeKnownGuid(guid);
            string to = NormalizeKnownGuid(toGuid);
            if (from == null || to == null || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                ParsekLog.Verbose(Tag, "Link not added: kind=" + kind + " guid=" + (from ?? "<none>") +
                    " to=" + (to ?? "<none>") + " reason=" + (reason ?? "<none>") +
                    (from != null && to != null ? " (same guid)" : " (unknown guid)"));
                return false;
            }
            if (links.TryGetValue(from, out VesselIdentityLink existing))
            {
                if (string.Equals(existing.ToGuid, to, StringComparison.OrdinalIgnoreCase))
                    ParsekLog.Verbose(Tag, "Link already present: kind=" + existing.Kind + " guid=" + from +
                        " to=" + to + " (requested " + kind + ", reason=" + (reason ?? "<none>") + ")");
                else
                    ParsekLog.Warn(Tag, "Link kept: guid=" + from + " already continues " + existing.ToGuid +
                        " (" + existing.Kind + "); ignored " + kind + " to " + to + " reason=" + (reason ?? "<none>"));
                return false;
            }
            links[from] = new VesselIdentityLink { Guid = from, ToGuid = to, Kind = kind };
            linkApplyDue = true;
            Version++;
            ParsekLog.Info(Tag, "Link added: kind=" + kind + " guid=" + from + " -> " + to +
                " reason=" + (reason ?? "<none>") + " links=" + links.Count.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>
        /// Records the dock-absorb row of a vessel a dock is absorbing (design 7.7): its root part
        /// flightID at dock time and its launch guid. A re-dock of the same root overwrites it.
        /// </summary>
        internal static bool RecordDockAbsorb(uint rootPartUId, string guid, string reason)
        {
            string g = NormalizeKnownGuid(guid);
            if (rootPartUId == 0 || g == null)
            {
                ParsekLog.Verbose(Tag, "Dock absorb row not recorded: rootUid=" +
                    rootPartUId.ToString(CultureInfo.InvariantCulture) + " guid=" + (g ?? "<none>") +
                    " reason=" + (reason ?? "<none>"));
                return false;
            }
            if (dockAbsorbs.TryGetValue(rootPartUId, out DockAbsorbRow old)
                && string.Equals(old.Guid, g, StringComparison.OrdinalIgnoreCase))
                return false;
            dockAbsorbs[rootPartUId] = new DockAbsorbRow { RootPartUId = rootPartUId, Guid = g };
            Version++;
            ParsekLog.Info(Tag, "Dock absorb row recorded: rootUid=" + rootPartUId.ToString(CultureInfo.InvariantCulture) +
                " guid=" + g + " reason=" + (reason ?? "<none>") +
                " rows=" + dockAbsorbs.Count.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>
        /// The undock hook (design 7.7): the vessel that left a dock has root part
        /// <paramref name="leavingRootPartUId"/> (stock re-roots it on the pre-dock root) and a
        /// fresh guid. A dock-absorb row for that root adds an UndockReturn link to the absorbed
        /// guid and is consumed (a later dock records a new one). No row (a dock recorded before
        /// the rows existed, a ship built docked in the editor): nothing; the in-tree re-pidded
        /// continuation edge stays the fallback.
        /// </summary>
        internal static bool TryLinkUndockReturn(uint leavingRootPartUId, string leavingGuid, string reason)
        {
            if (leavingRootPartUId == 0 || !dockAbsorbs.TryGetValue(leavingRootPartUId, out DockAbsorbRow row))
            {
                ParsekLog.Verbose(Tag, "Undock: no dock absorb row for rootUid=" +
                    leavingRootPartUId.ToString(CultureInfo.InvariantCulture) + " guid=" +
                    (NormalizeKnownGuid(leavingGuid) ?? "<none>") + " reason=" + (reason ?? "<none>") +
                    " (new identity unless the in-tree continuation joins it)");
                return false;
            }
            dockAbsorbs.Remove(leavingRootPartUId);
            Version++;
            bool added = AddLink(leavingGuid, row.Guid, VesselIdentityLinkKind.UndockReturn, reason);
            ParsekLog.Info(Tag, "Undock: dock absorb row hit rootUid=" +
                leavingRootPartUId.ToString(CultureInfo.InvariantCulture) + " absorbedGuid=" + row.Guid +
                " leavingGuid=" + (NormalizeKnownGuid(leavingGuid) ?? "<none>") + " linkAdded=" +
                (added ? "true" : "false") + " reason=" + (reason ?? "<none>"));
            return true;
        }

        /// <summary>
        /// A spawn regenerated part flightIDs (<paramref name="uidMap"/>: old -> new): every
        /// dock-absorb row keyed by an old uid gets a copy under the new one. The old row stays:
        /// the recording's snapshot keeps the old uids, so a later respawn of it maps them again.
        /// Returns the rows copied.
        /// </summary>
        internal static int CopyDockAbsorbsForUidMap(IDictionary<uint, uint> uidMap, string reason)
        {
            if (uidMap == null || uidMap.Count == 0 || dockAbsorbs.Count == 0) return 0;
            var copies = new List<DockAbsorbRow>();
            foreach (DockAbsorbRow row in dockAbsorbs.Values)
                if (uidMap.TryGetValue(row.RootPartUId, out uint newUid) && newUid != 0 && newUid != row.RootPartUId)
                    copies.Add(new DockAbsorbRow { RootPartUId = newUid, Guid = row.Guid });
            foreach (DockAbsorbRow c in copies) dockAbsorbs[c.RootPartUId] = c;
            if (copies.Count > 0)
            {
                Version++;
                ParsekLog.Info(Tag, "Dock absorb rows re-keyed for regenerated part uids: copied=" +
                    copies.Count.ToString(CultureInfo.InvariantCulture) + " reason=" + (reason ?? "<none>") +
                    " rows=" + dockAbsorbs.Count.ToString(CultureInfo.InvariantCulture));
            }
            return copies.Count;
        }

        // ------------------------------------------------------------------ spawn backfill

        /// <summary>Counts of one spawn-link backfill.</summary>
        internal struct BackfillResult
        {
            internal int Vessels;
            internal int GenuineSpawnMatches;
            internal int AdoptionStampSkipped;
            internal int Added;
            internal int AlreadyLinked;
        }

        /// <summary>
        /// Spawn-link backfill (design 6.2): a live vessel or proto whose pid is a recording's
        /// <see cref="Recording.SpawnedVesselPersistentId"/> through the GENUINE-spawn branch
        /// (<see cref="VesselLaunchIdentity.IsGenuineSpawnPidMatch"/>: the spawn pid is KSP-unique)
        /// gets a Spawn link from its guid to the recording's guid. The adoption-stamp branch
        /// (spawn pid equals the recorded pid: an in-flight commit adopted the live vessel, or a
        /// relaunch of the same craft) never links. Ghost map vessels are skipped. Idempotent;
        /// one summary line. Pure over its inputs and the store.
        /// </summary>
        internal static BackfillResult BackfillSpawnLinks(
            IEnumerable<Recording> recordings, IList<LiveVesselEntry> vessels, string reason)
        {
            var r = new BackfillResult();
            if (recordings == null || vessels == null) return r;
            var bySpawnPid = new Dictionary<uint, List<Recording>>();
            foreach (Recording rec in recordings)
            {
                if (rec == null || rec.SpawnedVesselPersistentId == 0) continue;
                if (!bySpawnPid.TryGetValue(rec.SpawnedVesselPersistentId, out List<Recording> list))
                    bySpawnPid[rec.SpawnedVesselPersistentId] = list = new List<Recording>(1);
                if (!list.Contains(rec)) list.Add(rec);
            }
            foreach (LiveVesselEntry v in vessels)
            {
                if (v == null || v.IsGhostMap || v.Pid == 0) continue;
                r.Vessels++;
                string g = NormalizeKnownGuid(v.Guid);
                if (g == null || !bySpawnPid.TryGetValue(v.Pid, out List<Recording> recs)) continue;
                foreach (Recording rec in recs)
                {
                    if (!VesselLaunchIdentity.IsGenuineSpawnPidMatch(rec, v.Pid))
                    {
                        r.AdoptionStampSkipped++;
                        continue;
                    }
                    r.GenuineSpawnMatches++;
                    string to = NormalizeKnownGuid(rec.RecordedVesselGuid);
                    if (to == null || string.Equals(to, g, StringComparison.OrdinalIgnoreCase)) continue;
                    if (links.ContainsKey(g)) { r.AlreadyLinked++; continue; }
                    if (AddLink(g, to, VesselIdentityLinkKind.Spawn, "backfill:" + (reason ?? "<none>")
                            + " rec=" + rec.RecordingId))
                        r.Added++;
                }
            }
            string line = "Spawn link backfill (" + (reason ?? "<none>") + "): vessels=" +
                r.Vessels.ToString(CultureInfo.InvariantCulture) +
                " genuineSpawnMatches=" + r.GenuineSpawnMatches.ToString(CultureInfo.InvariantCulture) +
                " adoptionStampSkipped=" + r.AdoptionStampSkipped.ToString(CultureInfo.InvariantCulture) +
                " added=" + r.Added.ToString(CultureInfo.InvariantCulture) +
                " alreadyLinked=" + r.AlreadyLinked.ToString(CultureInfo.InvariantCulture) +
                " links=" + links.Count.ToString(CultureInfo.InvariantCulture);
            if (r.Added > 0) ParsekLog.Info(Tag, line);
            else ParsekLog.Verbose(Tag, line);
            return r;
        }

        /// <summary>
        /// The OnLoad-tail backfill over every recording the store sees and the loaded game's
        /// proto vessels (<see cref="VesselNameLiveSync.LiveVesselsProviderForTesting"/> with
        /// <see cref="VesselNameLiveScope.ProtosOnly"/> in tests). Runs before the tail's apply
        /// pass, which the added links then feed. Never throws.
        /// </summary>
        internal static BackfillResult RunSpawnLinkBackfill(string reason)
        {
            try
            {
                List<RecordingTree> trees = GatherTrees();
                var recordings = new List<Recording>();
                foreach (RecordingTree t in trees)
                    if (t?.Recordings != null)
                        foreach (Recording r in t.Recordings.Values)
                            if (r != null) recordings.Add(r);
                recordings.AddRange(GatherLoose(trees));
                List<LiveVesselEntry> vessels = VesselNameLiveSync.GatherProtoVesselsForBackfill(reason);
                if (vessels == null)
                {
                    ParsekLog.Verbose(Tag, "Spawn link backfill (" + (reason ?? "<none>") + ") skipped: no proto vessels");
                    return default;
                }
                return BackfillSpawnLinks(recordings, vessels, reason);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Spawn link backfill (" + (reason ?? "<none>") + ") threw: " +
                    ex.GetType().Name + ": " + ex.Message);
                return default;
            }
        }

        // ------------------------------------------------------------------ identity guids

        /// <summary>
        /// Every guid of <paramref name="rec"/>'s vessel identity over the live recordings and the
        /// link rows (recording guids plus link guids), normalized. Empty for an EVA or guid-less
        /// identity; the recording's own guid when no tree holds it. Never throws.
        /// </summary>
        internal static HashSet<string> IdentityGuidsFor(Recording rec)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rec == null) return set;
            string own = NormalizeKnownGuid(rec.RecordedVesselGuid);
            if (own != null) set.Add(own);
            if (!VesselIdentity.IsVesselLeg(rec)) return set;
            try
            {
                List<RecordingTree> trees = GatherTrees();
                List<Recording> loose = GatherLoose(trees);
                VesselIdentity.Resolution res = ResolveUniverse(trees, loose);
                if (!res.ByRecordingId.ContainsKey(rec.RecordingId))
                {
                    loose.Add(rec);
                    res = ResolveUniverse(trees, loose);
                }
                if (res.TryGetIdentity(rec.RecordingId, out VesselIdentity.Identity identity))
                    foreach (string g in identity.Guids)
                    {
                        string n = NormalizeKnownGuid(g);
                        if (n != null) set.Add(n);
                    }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Identity guid set for rec=" + (rec.RecordingId ?? "<null>") + " threw: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
            return set;
        }

        // ------------------------------------------------------------------ codec

        private static void EncodeLinks(ConfigNode node)
        {
            var ordered = new List<VesselIdentityLink>(links.Values);
            ordered.Sort((a, b) => string.CompareOrdinal(a.Guid, b.Guid));
            foreach (VesselIdentityLink l in ordered)
            {
                ConfigNode ln = node.AddNode(LinkNodeName);
                ln.AddValue("guid", l.Guid);
                ln.AddValue("to", l.ToGuid);
                ln.AddValue("kind", l.Kind.ToString());
            }
            var rows = new List<DockAbsorbRow>(dockAbsorbs.Values);
            rows.Sort((a, b) => a.RootPartUId.CompareTo(b.RootPartUId));
            foreach (DockAbsorbRow row in rows)
            {
                ConfigNode dn = node.AddNode(DockAbsorbNodeName);
                dn.AddValue("rootUid", row.RootPartUId.ToString(CultureInfo.InvariantCulture));
                dn.AddValue("guid", row.Guid);
            }
        }

        private static void DecodeLinks(ConfigNode node, ref int skipped)
        {
            foreach (ConfigNode ln in node.GetNodes(LinkNodeName))
            {
                string from = NormalizeKnownGuid(ln.GetValue("guid"));
                string to = NormalizeKnownGuid(ln.GetValue("to"));
                string rawKind = ln.GetValue("kind");
                if (from == null || to == null || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)
                    || rawKind == null || !Enum.TryParse(rawKind, false, out VesselIdentityLinkKind kind)
                    || !Enum.IsDefined(typeof(VesselIdentityLinkKind), kind))
                {
                    skipped++;
                    ParsekLog.Warn(Tag, "Load: skipped a malformed LINK (guid='" + (ln.GetValue("guid") ?? "<null>") +
                        "' to='" + (ln.GetValue("to") ?? "<null>") + "' kind='" + (rawKind ?? "<null>") + "')");
                    continue;
                }
                if (links.ContainsKey(from)) { skipped++; continue; }
                links[from] = new VesselIdentityLink { Guid = from, ToGuid = to, Kind = kind };
            }
            foreach (ConfigNode dn in node.GetNodes(DockAbsorbNodeName))
            {
                string rawUid = dn.GetValue("rootUid");
                string g = NormalizeKnownGuid(dn.GetValue("guid"));
                if (rawUid == null || !uint.TryParse(rawUid, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint uid)
                    || uid == 0 || g == null)
                {
                    skipped++;
                    ParsekLog.Warn(Tag, "Load: skipped a malformed DOCK_ABSORB (rootUid='" + (rawUid ?? "<null>") +
                        "' guid='" + (dn.GetValue("guid") ?? "<null>") + "')");
                    continue;
                }
                dockAbsorbs[uid] = new DockAbsorbRow { RootPartUId = uid, Guid = g };
            }
        }

        private static void ClearLinkState()
        {
            links.Clear();
            dockAbsorbs.Clear();
            linkApplyDue = false;
        }
    }
}
