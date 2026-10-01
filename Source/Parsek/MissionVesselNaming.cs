using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The one place a mission's vessels get their display names, shared by the mission Log
    /// and the Missions tab's vessel rows, so the two surfaces name a vessel the same way.
    ///
    /// <para>Two rules, both presentation only (no recording data is read for anything but
    /// identity, nothing is written):</para>
    /// <list type="bullet">
    /// <item>A leg whose launch identity (<see cref="VesselLaunchIdentity.RecordingsShareLaunch"/>)
    /// matches a vessel ANOTHER mission recorded earlier is that mission's vessel - a dock
    /// partner, or the half of a docked stack that keeps the partner's identity after an
    /// undock. It reads <c>"Kerbal X (mission 'Kerbal X')"</c>
    /// (<see cref="MissionChapters.FormatPartnerWithMission"/>) and is never numbered. A leg
    /// sharing launch identity with one of this mission's own root legs is never a partner:
    /// a root is the mission's own subject.</item>
    /// <item>This mission's OWN same-named vessels are numbered <c>"Kerbal X [2]"</c>: square
    /// brackets, because round brackets already mean counts (<c>"(pod x1, crew x2)"</c>,
    /// <c>"Debris (3)"</c>) and <c>"#2"</c> is the mission / group dedup. One vessel is the
    /// legs that share a ChainId or a launch identity, so chain segments share one name.
    /// Order: first appearance UT; on a tie the vessel whose first leg shares part pids with
    /// its parent lineage keeps the plain name; then the RecordingId. EVA legs keep the
    /// kerbal's name (they are not in the map) and debris is never a leg.</item>
    /// </list>
    ///
    /// <para>Numbering is per TREE, not per Mission: clones of a mission share the tree, and
    /// a vessel keeps the same name in every one of them whatever each clone includes. Pure:
    /// no Unity calls, no shared mutable state.</para>
    /// </summary>
    internal static class MissionVesselNaming
    {
        /// <summary>"Kerbal X" for ordinal 1, "Kerbal X [2]" after. InvariantCulture.</summary>
        internal static string FormatNumbered(string name, int ordinal)
        {
            if (ordinal <= 1) return name ?? "";
            return (name ?? "") + " [" + ordinal.ToString(CultureInfo.InvariantCulture) + "]";
        }

        /// <summary>
        /// Every committed non-debris recording with a persistent id, by pid, with the tree it
        /// lives in. Built once per derivation pass (a frame of the Missions tab, a Log
        /// rebuild) and shared across every tree named in it.
        /// </summary>
        internal sealed class LaunchIndex
        {
            internal struct Entry
            {
                internal string TreeId;
                internal string TreeName;
                internal Recording Rec;
            }

            internal readonly Dictionary<uint, List<Entry>> ByPid =
                new Dictionary<uint, List<Entry>>();

            internal static LaunchIndex Build(IEnumerable<RecordingTree> trees)
            {
                var index = new LaunchIndex();
                if (trees == null) return index;
                foreach (RecordingTree t in trees)
                {
                    if (t?.Recordings == null) continue;
                    foreach (Recording r in t.Recordings.Values)
                    {
                        if (r == null || r.IsDebris || r.VesselPersistentId == 0) continue;
                        if (!index.ByPid.TryGetValue(r.VesselPersistentId, out List<Entry> list))
                        {
                            list = new List<Entry>(1);
                            index.ByPid[r.VesselPersistentId] = list;
                        }
                        list.Add(new Entry { TreeId = t.Id, TreeName = t.TreeName, Rec = r });
                    }
                }
                return index;
            }
        }

        /// <summary>What one naming pass decided, for the caller's log line.</summary>
        internal struct Tally
        {
            internal int Legs;
            internal int Partners;
            internal int Numbered;
        }

        // Absorbs serialization noise only: a partner's launch must be strictly earlier.
        private const double EarlierEpsilonSeconds = 1e-3;

        /// <summary>
        /// The display name of every controlled, non-EVA leg of <paramref name="tree"/>, keyed
        /// by RecordingId: the partner phrase, a numbered name, or the plain vessel name.
        /// <paramref name="missionNameOfTree"/> names the mission that owns another tree
        /// (the window passes the tree's original mission's name); null falls back to the
        /// tree's own name.
        /// </summary>
        internal static Dictionary<string, string> Build(
            RecordingTree tree, MissionStructure structure, LaunchIndex index,
            Func<string, string> missionNameOfTree, out Tally tally)
        {
            tally = default;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (tree == null || structure == null || structure.LegsById.Count == 0)
                return names;

            var roots = new List<Recording>();
            for (int i = 0; i < structure.RootLegIds.Count; i++)
            {
                Recording r = Rec(tree, structure.RootLegIds[i]);
                if (r != null) roots.Add(r);
            }

            // 1. Partners, and the own legs that are left to name.
            var own = new List<MissionLeg>();
            foreach (MissionLeg leg in structure.LegsById.Values)
            {
                if (leg == null || !string.IsNullOrEmpty(leg.EvaCrewName)) continue;
                tally.Legs++;
                Recording rec = Rec(tree, leg.RecordingId);
                if (TryResolveForeignOwner(tree, rec, roots, index, out LaunchIndex.Entry owner))
                {
                    string mission = missionNameOfTree != null ? missionNameOfTree(owner.TreeId) : null;
                    if (string.IsNullOrEmpty(mission)) mission = owner.TreeName;
                    names[leg.RecordingId] = MissionChapters.FormatPartnerWithMission(
                        LegName(leg), mission) ?? LegName(leg);
                    tally.Partners++;
                    continue;
                }
                own.Add(leg);
            }

            // 2. One vessel = the own legs sharing a ChainId or a launch identity.
            own.Sort((a, b) => string.CompareOrdinal(a.RecordingId, b.RecordingId));
            var parent = new int[own.Count];
            for (int i = 0; i < own.Count; i++) parent[i] = i;
            for (int i = 0; i < own.Count; i++)
            {
                Recording ri = Rec(tree, own[i].RecordingId);
                for (int j = i + 1; j < own.Count; j++)
                {
                    bool sameChain = !string.IsNullOrEmpty(own[i].ChainId)
                        && string.Equals(own[i].ChainId, own[j].ChainId, StringComparison.Ordinal);
                    if (sameChain
                        || VesselLaunchIdentity.RecordingsShareLaunch(ri, Rec(tree, own[j].RecordingId)))
                        Union(parent, i, j);
                }
            }

            // 3. Per name, the vessels carrying it; two or more get numbers.
            var vesselsByName = new Dictionary<string, Dictionary<int, List<MissionLeg>>>(StringComparer.Ordinal);
            for (int i = 0; i < own.Count; i++)
            {
                string name = LegName(own[i]);
                int root = Find(parent, i);
                if (!vesselsByName.TryGetValue(name, out Dictionary<int, List<MissionLeg>> byVessel))
                {
                    byVessel = new Dictionary<int, List<MissionLeg>>();
                    vesselsByName[name] = byVessel;
                }
                if (!byVessel.TryGetValue(root, out List<MissionLeg> legs))
                {
                    legs = new List<MissionLeg>();
                    byVessel[root] = legs;
                }
                legs.Add(own[i]);
            }

            foreach (KeyValuePair<string, Dictionary<int, List<MissionLeg>>> byName in vesselsByName)
            {
                var vessels = new List<VesselOrder>();
                foreach (List<MissionLeg> legs in byName.Value.Values)
                {
                    MissionLeg first = legs[0];
                    for (int k = 1; k < legs.Count; k++)
                    {
                        int c = legs[k].StartUT.CompareTo(first.StartUT);
                        if (c < 0 || (c == 0 && string.CompareOrdinal(legs[k].RecordingId, first.RecordingId) < 0))
                            first = legs[k];
                    }
                    vessels.Add(new VesselOrder
                    {
                        Legs = legs,
                        FirstUT = first.StartUT,
                        SharesParentParts = SharesPartsWithParent(tree, first),
                        FirstId = first.RecordingId,
                    });
                }
                vessels.Sort(CompareVessel);
                for (int v = 0; v < vessels.Count; v++)
                {
                    string display = FormatNumbered(byName.Key, v + 1);
                    if (v > 0) tally.Numbered += vessels[v].Legs.Count;
                    foreach (MissionLeg leg in vessels[v].Legs)
                        names[leg.RecordingId] = display;
                }
            }
            return names;
        }

        private struct VesselOrder
        {
            internal List<MissionLeg> Legs;
            internal double FirstUT;
            internal bool SharesParentParts;
            internal string FirstId;
        }

        private static int CompareVessel(VesselOrder a, VesselOrder b)
        {
            if (Math.Abs(a.FirstUT - b.FirstUT) > EarlierEpsilonSeconds)
                return a.FirstUT.CompareTo(b.FirstUT);
            if (a.SharesParentParts != b.SharesParentParts)
                return a.SharesParentParts ? -1 : 1;
            return string.CompareOrdinal(a.FirstId, b.FirstId);
        }

        /// <summary>
        /// The other tree whose earlier recording shares this leg's launch, when there is one
        /// and the leg is not one of this mission's own roots (or a continuation of one).
        /// The earliest such recording wins; ties go to the tree id.
        /// </summary>
        internal static bool TryResolveForeignOwner(
            RecordingTree tree, Recording rec, List<Recording> roots, LaunchIndex index,
            out LaunchIndex.Entry owner)
        {
            owner = default;
            if (tree == null || rec == null || rec.IsDebris || rec.VesselPersistentId == 0
                || index == null
                || !index.ByPid.TryGetValue(rec.VesselPersistentId, out List<LaunchIndex.Entry> list))
                return false;
            if (roots != null)
                for (int i = 0; i < roots.Count; i++)
                    if (VesselLaunchIdentity.RecordingsShareLaunch(rec, roots[i]))
                        return false;
            bool found = false;
            for (int i = 0; i < list.Count; i++)
            {
                LaunchIndex.Entry e = list[i];
                if (string.Equals(e.TreeId, tree.Id, StringComparison.Ordinal)) continue;
                if (!(e.Rec.StartUT < rec.StartUT - EarlierEpsilonSeconds)) continue;
                if (!VesselLaunchIdentity.RecordingsShareLaunch(rec, e.Rec)) continue;
                if (!found
                    || e.Rec.StartUT < owner.Rec.StartUT
                    || (e.Rec.StartUT == owner.Rec.StartUT
                        && string.CompareOrdinal(e.TreeId, owner.TreeId) < 0))
                {
                    owner = e;
                    found = true;
                }
            }
            return found;
        }

        // The tie-break: the vessel that kept the parent's parts is the one that continues it.
        private static bool SharesPartsWithParent(RecordingTree tree, MissionLeg leg)
        {
            Recording rec = Rec(tree, leg.RecordingId);
            if (rec == null) return false;
            HashSet<uint> mine = PartPids(rec);
            if (mine.Count == 0) return false;
            var parents = new List<string>(leg.BranchParentIds);
            if (!string.IsNullOrEmpty(leg.SequencePrevId)) parents.Add(leg.SequencePrevId);
            for (int i = 0; i < parents.Count; i++)
            {
                Recording p = Rec(tree, parents[i]);
                if (p == null) continue;
                foreach (uint pid in PartPids(p))
                    if (mine.Contains(pid)) return true;
            }
            return false;
        }

        private static HashSet<uint> PartPids(Recording rec)
        {
            var pids = new HashSet<uint>();
            ConfigNode snapshot = rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
            if (snapshot != null)
                foreach (uint pid in VesselSnapshotOps.CollectPartPersistentIds(snapshot))
                    if (pid != 0) pids.Add(pid);
            if (rec.PartEvents != null)
                for (int i = 0; i < rec.PartEvents.Count; i++)
                    if (rec.PartEvents[i].partPersistentId != 0)
                        pids.Add(rec.PartEvents[i].partPersistentId);
            return pids;
        }

        private static string LegName(MissionLeg leg)
            => string.IsNullOrEmpty(leg.VesselName) ? "(vessel)" : leg.VesselName;

        private static Recording Rec(RecordingTree tree, string id)
            => id != null && tree?.Recordings != null
               && tree.Recordings.TryGetValue(id, out Recording r) ? r : null;

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }
    }
}
