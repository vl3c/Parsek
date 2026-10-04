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
    /// legs that share a ChainId or a launch identity, so chain segments share one name, plus
    /// a RE-PIDDED CONTINUATION: KSP gives a ship a fresh persistent id (and launch guid) when
    /// it undocks from another vessel, so a leg that continues one of this mission's own
    /// vessels after that vessel's own legs have ended, and shares part pids with it, is the
    /// same physical ship and joins it, the best-matching such leg per hop
    /// (<see cref="MergeRepiddedContinuations"/>). Order:
    /// first appearance UT; on a tie the vessel whose first leg shares part pids with its
    /// parent lineage keeps the plain name; then the RecordingId. EVA legs keep the kerbal's
    /// name (they are not in the map) and debris is never a leg.</item>
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

        /// <summary>
        /// Moves whenever a recording's name or part data changes in place, without a
        /// committed-list mutation (a player rename, a hydration repair), so
        /// <see cref="Cache"/> drops its names: <see cref="RecordingStore.StateVersion"/> does
        /// not move on those and its listeners need not.
        /// </summary>
        internal static int NameVersion { get; private set; }

        /// <summary>Bumps <see cref="NameVersion"/> and logs why.</summary>
        internal static void NoteRecordingNameOrPartsChanged(string reason, string recordingId)
        {
            NameVersion++;
            ParsekLog.Verbose("Mission",
                $"Vessel names invalidated: reason={reason ?? "<none>"} rec={recordingId ?? "<null>"} " +
                $"nameVersion={NameVersion}");
        }

        /// <summary>
        /// A recording rename: sets <see cref="Recording.VesselName"/> to the trimmed name and
        /// invalidates the shared vessel names. False (nothing written) for a blank or unchanged
        /// name. The Recordings table's rename commit and the seam's op=edit both land here.
        /// </summary>
        internal static bool ApplyRecordingRename(Recording rec, string newName)
        {
            string trimmed = newName?.Trim();
            if (rec == null || string.IsNullOrEmpty(trimmed) || trimmed == rec.VesselName)
                return false;
            ParsekLog.Info("UI", $"Recording '{rec.VesselName}' renamed to '{trimmed}'");
            rec.VesselName = trimmed;
            NoteRecordingNameOrPartsChanged("rename", rec.RecordingId);
            return true;
        }

        /// <summary>
        /// The Missions tab's cross-frame cache of the naming pass: per tree names, the launch
        /// index and every recording's part pids, all keyed on
        /// <see cref="RecordingStore.StateVersion"/>, <see cref="NameVersion"/> and a signature
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
                int currentNameVersion = NameVersion;
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
        /// (the window passes the tree's original mission's name); null falls back to the
        /// tree's own name. <paramref name="partnerLegIds"/>, when given, receives the id of
        /// every leg named as another mission's vessel (the mission Log's Dock / Undock rows
        /// read it to put this mission's own ship in the Vessel column).
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
                    if (string.IsNullOrEmpty(mission)) mission = owner.TreeName;
                    names[leg.RecordingId] = MissionChapters.FormatPartnerWithMission(
                        LegName(leg), mission) ?? LegName(leg);
                    tally.Partners++;
                    partnerLegIds?.Add(leg.RecordingId);
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

            // 2b. A ship KSP re-pidded at an undock is the vessel it continues.
            tally.Continuations = MergeRepiddedContinuations(tree, structure, own, parent, partPids);

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
                        SharesParentParts = SharesPartsWithParent(tree, first, partPids),
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

        /// <summary>
        /// Joins each re-pidded continuation to the own vessel it continues, one hop at a time.
        /// A candidate is a pair (own vessel V, leg L) where V holds L's NEAREST own lineage
        /// ancestor (the walk up L's branch parents and chain predecessors passes through
        /// partner legs and stops at the first own legs: the own ship docks into the partner's
        /// stack and leaves it again), no leg of V runs past L's start, and L shares at least
        /// one part pid with V. Each round every vessel's candidates are weighed together and
        /// the best pair overall merges - most shared pids, then the later-ending vessel, then
        /// the ids - so the ship's real half wins over a smaller piece that split off the
        /// partner's stack a moment earlier. The round re-reads the current union-find roots,
        /// so a ship re-pidded twice (A -> A' -> A'') chains: A'' only becomes a candidate of A
        /// once A' has joined it, whatever the recording ids. The running test is the only
        /// guard: a vessel that carries on past a split is never continued by the piece that
        /// left it (a genuine same-named twin stays a second vessel), and once V absorbs one
        /// half of an undock that half is running, so the other half cannot join V too. Each
        /// leg joins at most one vessel. Returns the merges made.
        /// </summary>
        private static int MergeRepiddedContinuations(
            RecordingTree tree, MissionStructure structure, List<MissionLeg> own, int[] parent,
            Func<Recording, HashSet<uint>> partPids)
        {
            if (own.Count < 2) return 0;
            var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < own.Count; i++) indexById[own[i].RecordingId] = i;
            var pidsByLeg = new HashSet<uint>[own.Count];
            for (int i = 0; i < own.Count; i++)
            {
                Recording r = Rec(tree, own[i].RecordingId);
                pidsByLeg[i] = r != null ? partPids(r) : new HashSet<uint>();
            }
            // The nearest own ancestors of each leg never change; only their roots do.
            var nearest = new List<int>[own.Count];
            for (int i = 0; i < own.Count; i++)
                nearest[i] = NearestOwnAncestors(structure, own[i], indexById);

            var joined = new HashSet<int>();
            int merges = 0;
            while (true)
            {
                Continuation best = default;
                bool found = false;
                for (int li = 0; li < own.Count; li++)
                {
                    if (joined.Contains(li) || pidsByLeg[li].Count == 0) continue;
                    int legRoot = Find(parent, li);
                    var tried = new HashSet<int>();
                    foreach (int ai in nearest[li])
                    {
                        int vRoot = Find(parent, ai);
                        if (vRoot == legRoot || !tried.Add(vRoot)) continue;
                        if (!TryScore(own, pidsByLeg, parent, vRoot, li,
                                out int shared, out double vesselEnd))
                            continue;
                        var c = new Continuation
                        {
                            Leg = li, Vessel = vRoot, Shared = shared, VesselEnd = vesselEnd,
                        };
                        if (!found || CompareContinuation(own, c, best) < 0)
                        {
                            best = c;
                            found = true;
                        }
                    }
                }
                if (!found) break;
                Union(parent, best.Leg, best.Vessel);
                joined.Add(best.Leg);
                merges++;
            }
            return merges;
        }

        // The own legs a leg L continues from directly: up its branch parents and chain
        // predecessors, through partner legs, stopping at the first own leg on each path.
        private static List<int> NearestOwnAncestors(
            MissionStructure structure, MissionLeg leg, Dictionary<string, int> indexById)
        {
            var result = new List<int>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { leg.RecordingId };
            var stack = new Stack<MissionLeg>();
            stack.Push(leg);
            while (stack.Count > 0)
            {
                MissionLeg cur = stack.Pop();
                var ups = new List<string>(cur.BranchParentIds);
                if (!string.IsNullOrEmpty(cur.SequencePrevId)) ups.Add(cur.SequencePrevId);
                foreach (string up in ups)
                {
                    if (string.IsNullOrEmpty(up) || !seen.Add(up)) continue;
                    if (indexById.TryGetValue(up, out int ai))
                    {
                        result.Add(ai);
                        continue;
                    }
                    if (structure.LegsById.TryGetValue(up, out MissionLeg upLeg) && upLeg != null)
                        stack.Push(upLeg);
                }
            }
            return result;
        }

        // Vessel vRoot can take leg li: none of its legs runs past li's start, and they share
        // at least one part pid with it.
        private static bool TryScore(List<MissionLeg> own, HashSet<uint>[] pidsByLeg, int[] parent,
            int vRoot, int li, out int shared, out double vesselEnd)
        {
            shared = 0;
            vesselEnd = double.MinValue;
            for (int k = 0; k < own.Count; k++)
            {
                if (Find(parent, k) != vRoot) continue;
                if (own[k].EndUT > vesselEnd) vesselEnd = own[k].EndUT;
                foreach (uint pid in pidsByLeg[k])
                    if (pidsByLeg[li].Contains(pid)) shared++;
            }
            return shared > 0 && vesselEnd <= own[li].StartUT + EarlierEpsilonSeconds;
        }

        private static int CompareContinuation(List<MissionLeg> own, Continuation a, Continuation b)
        {
            int c = b.Shared.CompareTo(a.Shared);
            if (c != 0) return c;
            c = b.VesselEnd.CompareTo(a.VesselEnd);
            if (c != 0) return c;
            c = string.CompareOrdinal(own[a.Vessel].RecordingId, own[b.Vessel].RecordingId);
            return c != 0 ? c : string.CompareOrdinal(own[a.Leg].RecordingId, own[b.Leg].RecordingId);
        }

        private struct Continuation
        {
            internal int Leg;
            internal int Vessel;
            internal int Shared;
            internal double VesselEnd;
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

        /// <summary>A recording's part pids: its snapshot's PART nodes and its part events.
        /// Parses the snapshot, so a per-frame caller goes through <see cref="Cache"/>.</summary>
        internal static HashSet<uint> PartPids(Recording rec)
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
