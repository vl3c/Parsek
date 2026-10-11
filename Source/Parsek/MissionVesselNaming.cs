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
    /// identity; nothing is written except by the rename, which records a vessel name
    /// decision):</para>
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
    /// legs that share a ChainId or a launch identity, so chain segments share one name, plus
    /// a RE-PIDDED CONTINUATION: KSP gives a ship a fresh persistent id (and launch guid) when
    /// it undocks from another vessel, so a leg that continues one of this mission's own
    /// vessels after that vessel's own legs have ended, and shares part pids with it, is the
    /// same physical ship and joins it, the best-matching such leg per hop
    /// (<see cref="VesselIdentity.MergeRepiddedContinuations"/>). Which legs are one vessel
    /// is <see cref="VesselIdentity"/>'s in-tree union, the same resolver the vessel name
    /// store uses. Legs are grouped by vessel FIRST and then numbered per name, so a vessel
    /// carrying two captured names is one vessel in both. Order: the vessel's first
    /// appearance UT; on a tie the vessel whose first leg shares part pids with its parent
    /// lineage keeps the plain name; then the RecordingId. EVA legs keep the kerbal's name
    /// (they are not in the map) and debris is never a leg.</item>
    /// </list>
    ///
    /// <para>Numbering is per TREE, not per Mission: clones of a mission share the tree, and
    /// a vessel keeps the same name in every one of them whatever each clone includes. No
    /// Unity calls; the only shared state read is the mission names (the partner phrase's
    /// fallback when the caller passes no mission-name resolver).</para>
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

        /// <summary>
        /// A Parsek row rename (the Recordings table's rename commit and the seam's op=edit
        /// both land here): an identity-wide vessel name decision
        /// (<see cref="VesselNameStore.RecordParsekRename"/>), so every leg of the row's
        /// vessel takes the name. False (nothing written) for a blank or unchanged name, an
        /// EVA row (its name is the kerbal's) and a vessel with no known launch guid; each
        /// refusal is logged. <paramref name="universeForTesting"/> replaces the live trees the
        /// identity is resolved over.
        /// </summary>
        internal static bool ApplyRecordingRename(
            Recording rec, string newName, IEnumerable<RecordingTree> universeForTesting = null)
        {
            return VesselNameStore.RecordParsekRename(rec, newName, universeForTesting, out _);
        }

        /// <summary>
        /// The Missions tab's cross-frame cache of the naming pass: per tree names, the launch
        /// index and every recording's part pids, all keyed on
        /// <see cref="RecordingStore.StateVersion"/>, <see cref="DisplayNames.Version"/> and a signature
        /// of the missions' names (a partner phrase names another mission). A frame whose key matches reads the
        /// dictionaries and computes nothing; a moved key drops everything at once. Pure apart
        /// from what the caller passes in, so the reuse and the invalidation are unit-testable.
        /// </summary>
        internal sealed class Cache
        {
            private int stateVersion;
            private int missionSignature;
            private int nameVersion;
            private bool primed;
            private LaunchIndex index;
            private readonly Dictionary<string, Dictionary<string, string>> namesByTree =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            private readonly Dictionary<string, HashSet<uint>> pidsByRecording =
                new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
            private readonly Dictionary<string, HashSet<string>> partnersByTree =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            /// <summary>Naming passes run since construction (a cache miss each).</summary>
            internal int Rebuilds { get; private set; }

            /// <summary>Lookups answered from the cache.</summary>
            internal int Hits { get; private set; }

            /// <summary>The last miss's tally, for the caller's rate-limited log line.</summary>
            internal Tally LastTally { get; private set; }

            /// <summary>
            /// The names of <paramref name="tree"/>'s legs. <paramref name="structure"/> is
            /// only invoked on a miss; <paramref name="allTrees"/> feeds the launch index,
            /// built once per key.
            /// </summary>
            internal Dictionary<string, string> GetOrBuild(
                RecordingTree tree, Func<RecordingTree, MissionStructure> structure,
                IEnumerable<RecordingTree> allTrees, int currentStateVersion,
                int currentMissionSignature, Func<string, string> missionNameOfTree)
            {
                if (tree == null || string.IsNullOrEmpty(tree.Id))
                    return new Dictionary<string, string>(StringComparer.Ordinal);
                int currentNameVersion = DisplayNames.Version;
                if (!primed || currentStateVersion != stateVersion
                    || currentMissionSignature != missionSignature
                    || currentNameVersion != nameVersion)
                {
                    namesByTree.Clear();
                    partnersByTree.Clear();
                    pidsByRecording.Clear();
                    index = null;
                    stateVersion = currentStateVersion;
                    missionSignature = currentMissionSignature;
                    nameVersion = currentNameVersion;
                    primed = true;
                }
                if (namesByTree.TryGetValue(tree.Id, out Dictionary<string, string> cached))
                {
                    Hits++;
                    return cached;
                }
                if (index == null) index = LaunchIndex.Build(allTrees);
                var partners = new HashSet<string>(StringComparer.Ordinal);
                Dictionary<string, string> names = Build(tree, structure != null ? structure(tree) : null,
                    index, missionNameOfTree, out Tally tally, CachedPartPids, partners);
                namesByTree[tree.Id] = names;
                partnersByTree[tree.Id] = partners;
                Rebuilds++;
                LastTally = tally;
                return names;
            }

            /// <summary>
            /// The legs the last <see cref="GetOrBuild"/> of <paramref name="treeId"/> named as
            /// another mission's vessel (same key as its names); null before that call.
            /// </summary>
            internal HashSet<string> PartnerLegIds(string treeId)
                => treeId != null && partnersByTree.TryGetValue(treeId, out HashSet<string> p) ? p : null;

            private HashSet<uint> CachedPartPids(Recording rec)
            {
                string key = rec?.RecordingId;
                if (key == null) return PartPids(rec);
                if (!pidsByRecording.TryGetValue(key, out HashSet<uint> pids))
                {
                    pids = PartPids(rec);
                    pidsByRecording[key] = pids;
                }
                return pids;
            }

            /// <summary>An order-free signature of every mission's tree and name. Pure.</summary>
            internal static int MissionSignature(IEnumerable<Mission> missions)
            {
                unchecked
                {
                    int sum = 0, count = 0;
                    if (missions != null)
                        foreach (Mission m in missions)
                        {
                            if (m == null) continue;
                            int h = 17;
                            h = h * 31 + (m.TreeId != null ? StringComparer.Ordinal.GetHashCode(m.TreeId) : 0);
                            h = h * 31 + (m.Name != null ? StringComparer.Ordinal.GetHashCode(m.Name) : 0);
                            sum += h;
                            count++;
                        }
                    return sum * 31 + count;
                }
            }
        }

        /// <summary>What one naming pass decided, for the caller's log line.</summary>
        internal struct Tally
        {
            internal int Legs;
            internal int Partners;
            internal int Numbered;
            internal int Continuations;
        }

        // Absorbs serialization noise only: a partner's launch must be strictly earlier.
        private const double EarlierEpsilonSeconds = 1e-3;

        /// <summary>
        /// The display name of every controlled, non-EVA leg of <paramref name="tree"/>, keyed
        /// by RecordingId: the partner phrase, a numbered name, or the plain vessel name.
        /// <paramref name="missionNameOfTree"/> names the mission that owns another tree
        /// (the window passes the tree's original mission's name); null or an empty answer
        /// falls back to <see cref="MissionStore.DisplayNameForTree(string, string)"/> (the
        /// original mission's name, else the tree's TreeName). <paramref name="partnerLegIds"/>,
        /// when given, receives the id of every leg named as another mission's vessel (the
        /// mission Log's Dock / Undock rows read it to put this mission's own ship in the
        /// Vessel column).
        /// </summary>
        internal static Dictionary<string, string> Build(
            RecordingTree tree, MissionStructure structure, LaunchIndex index,
            Func<string, string> missionNameOfTree, out Tally tally,
            Func<Recording, HashSet<uint>> partPids = null,
            ICollection<string> partnerLegIds = null)
        {
            if (partPids == null) partPids = PartPids;
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
                    if (string.IsNullOrEmpty(mission))
                        mission = MissionStore.DisplayNameForTree(owner.TreeId, owner.TreeName);
                    names[leg.RecordingId] = MissionChapters.FormatPartnerWithMission(
                        LegName(leg), mission) ?? LegName(leg);
                    tally.Partners++;
                    partnerLegIds?.Add(leg.RecordingId);
                    continue;
                }
                own.Add(leg);
            }

            // 2. One vessel = the own legs the shared resolver joins in-tree: ChainId, launch
            //    identity, switch continuation, and a ship KSP re-pidded at an undock.
            own.Sort((a, b) => string.CompareOrdinal(a.RecordingId, b.RecordingId));
            var parent = new int[own.Count];
            for (int i = 0; i < own.Count; i++) parent[i] = i;
            tally.Continuations = VesselIdentity.UnionInTreeLegs(tree, structure, own, parent, partPids);

            // 3. Vessel first: group the legs by vessel, then number per name. A vessel's place
            //    among the vessels carrying a name is its FIRST appearance overall, so one vessel
            //    with two captured names (a rename the store never saw) is one vessel in both
            //    buckets and never splits into an early and a late ordinal.
            var vesselLegs = new Dictionary<int, List<MissionLeg>>();
            for (int i = 0; i < own.Count; i++)
            {
                int root = VesselIdentity.Find(parent, i);
                if (!vesselLegs.TryGetValue(root, out List<MissionLeg> legs))
                {
                    legs = new List<MissionLeg>();
                    vesselLegs[root] = legs;
                }
                legs.Add(own[i]);
            }
            var orderByRoot = new Dictionary<int, VesselOrder>();
            var rootsByName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (KeyValuePair<int, List<MissionLeg>> v in vesselLegs)
            {
                List<MissionLeg> legs = v.Value;
                MissionLeg first = legs[0];
                for (int k = 1; k < legs.Count; k++)
                {
                    int c = legs[k].StartUT.CompareTo(first.StartUT);
                    if (c < 0 || (c == 0 && string.CompareOrdinal(legs[k].RecordingId, first.RecordingId) < 0))
                        first = legs[k];
                }
                orderByRoot[v.Key] = new VesselOrder
                {
                    Root = v.Key,
                    FirstUT = first.StartUT,
                    SharesParentParts = SharesPartsWithParent(tree, first, partPids),
                    FirstId = first.RecordingId,
                };
                var namesOfVessel = new HashSet<string>(StringComparer.Ordinal);
                for (int k = 0; k < legs.Count; k++)
                {
                    string name = LegName(legs[k]);
                    if (!namesOfVessel.Add(name)) continue;
                    if (!rootsByName.TryGetValue(name, out List<int> roots2))
                    {
                        roots2 = new List<int>();
                        rootsByName[name] = roots2;
                    }
                    roots2.Add(v.Key);
                }
            }

            var ordinal = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<int>> byName in rootsByName)
            {
                var orders = new List<VesselOrder>(byName.Value.Count);
                foreach (int root in byName.Value) orders.Add(orderByRoot[root]);
                orders.Sort(CompareVessel);
                var ranks = new Dictionary<int, int>();
                for (int v = 0; v < orders.Count; v++) ranks[orders[v].Root] = v + 1;
                ordinal[byName.Key] = ranks;
            }
            foreach (KeyValuePair<int, List<MissionLeg>> v in vesselLegs)
            {
                foreach (MissionLeg leg in v.Value)
                {
                    string name = LegName(leg);
                    int rank = ordinal[name][v.Key];
                    if (rank > 1) tally.Numbered++;
                    names[leg.RecordingId] = FormatNumbered(name, rank);
                }
            }
            return names;
        }

        private struct VesselOrder
        {
            internal int Root;
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
        private static bool SharesPartsWithParent(
            RecordingTree tree, MissionLeg leg, Func<Recording, HashSet<uint>> partPids)
        {
            Recording rec = Rec(tree, leg.RecordingId);
            if (rec == null) return false;
            HashSet<uint> mine = partPids(rec);
            if (mine.Count == 0) return false;
            var parents = new List<string>(leg.BranchParentIds);
            if (!string.IsNullOrEmpty(leg.SequencePrevId)) parents.Add(leg.SequencePrevId);
            for (int i = 0; i < parents.Count; i++)
            {
                Recording p = Rec(tree, parents[i]);
                if (p == null) continue;
                foreach (uint pid in partPids(p))
                    if (mine.Contains(pid)) return true;
            }
            return false;
        }

        /// <summary>A recording's part pids (<see cref="VesselIdentity.PartPids"/>). Parses the
        /// snapshot, so a per-frame caller goes through <see cref="Cache"/>.</summary>
        internal static HashSet<uint> PartPids(Recording rec) => VesselIdentity.PartPids(rec);

        private static string LegName(MissionLeg leg)
            => string.IsNullOrEmpty(leg.VesselName) ? "(vessel)" : leg.VesselName;

        private static Recording Rec(RecordingTree tree, string id)
            => id != null && tree?.Recordings != null
               && tree.Recordings.TryGetValue(id, out Recording r) ? r : null;
    }
}
