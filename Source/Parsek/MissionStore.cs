using System;
using System.Collections.Generic;

namespace Parsek
{
    // Static store of player-defined Missions, surviving scene changes and persisted
    // through ParsekScenario OnSave/OnLoad. Every committed tree always has at least
    // one Mission (a default, all-included), and Delete is blocked on a tree's last
    // Mission, so a tree is never left without one. See Mission / MissionsWindowUI.
    internal static class MissionStore
    {
        private static readonly List<Mission> missions = new List<Mission>();

        internal static bool SuppressLogging;

        internal static IReadOnlyList<Mission> Missions => missions;

        internal static void ResetForTesting()
        {
            missions.Clear();
        }

        internal static int CountForTree(string treeId)
        {
            int n = 0;
            for (int i = 0; i < missions.Count; i++)
                if (missions[i].TreeId == treeId)
                    n++;
            return n;
        }

        /// <summary>
        /// Creates an all-included default Mission for any tree that has none. Returns
        /// the number created. Idempotent: a no-op once every tree has a Mission.
        /// </summary>
        internal static int EnsureDefaultsForTrees(IEnumerable<RecordingTree> trees)
        {
            if (trees == null)
                return 0;
            int created = 0;
            foreach (var tree in trees)
            {
                if (tree == null || string.IsNullOrEmpty(tree.Id))
                    continue;
                if (CountForTree(tree.Id) > 0)
                    continue;
                string name = string.IsNullOrEmpty(tree.TreeName) ? "Mission" : tree.TreeName;
                missions.Add(new Mission(Guid.NewGuid().ToString("N"), tree.Id, name));
                created++;
            }
            if (created > 0 && !SuppressLogging)
                ParsekLog.Verbose("Mission", $"EnsureDefaultsForTrees: created {created} default mission(s)");
            return created;
        }

        /// <summary>Removes Missions whose tree no longer exists. Returns count removed.</summary>
        internal static int PruneOrphans(IEnumerable<RecordingTree> trees)
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            if (trees != null)
                foreach (var tree in trees)
                    if (tree != null && !string.IsNullOrEmpty(tree.Id))
                        live.Add(tree.Id);

            int removed = missions.RemoveAll(
                m => m == null || string.IsNullOrEmpty(m.TreeId) || !live.Contains(m.TreeId));
            if (removed > 0 && !SuppressLogging)
                ParsekLog.Verbose("Mission", $"PruneOrphans: removed {removed} mission(s) for missing trees");
            return removed;
        }

        /// <summary>
        /// Drops stale through-line head ids from every Mission's excluded set: ids that
        /// are no longer a current through-line head for the Mission's tree (the branch
        /// they referred to was edited away, re-flown, or merged out from under them). A
        /// stale exclusion would otherwise silently re-include a branch the player had
        /// dropped once it stopped being a head, so this fails loudly with a warn. Builds
        /// each tree's through-line view at most once and reuses it across Missions that
        /// share the tree. Returns the total number of ids removed across all Missions.
        /// </summary>
        internal static int ReconcileSelections(IEnumerable<RecordingTree> trees)
        {
            var byId = new Dictionary<string, RecordingTree>(StringComparer.Ordinal);
            if (trees != null)
                foreach (var tree in trees)
                    if (tree != null && !string.IsNullOrEmpty(tree.Id))
                        byId[tree.Id] = tree;

            var viewCache = new Dictionary<string, MissionThroughLineView>(StringComparer.Ordinal);
            int removed = 0;
            for (int i = 0; i < missions.Count; i++)
            {
                Mission m = missions[i];
                if (m == null || string.IsNullOrEmpty(m.TreeId)
                    || m.ExcludedThroughLineHeadIds.Count == 0)
                    continue;
                if (!byId.TryGetValue(m.TreeId, out RecordingTree tree))
                    continue;

                if (!viewCache.TryGetValue(m.TreeId, out MissionThroughLineView view))
                {
                    view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(tree));
                    viewCache[m.TreeId] = view;
                }

                var stale = new List<string>();
                foreach (string headId in m.ExcludedThroughLineHeadIds)
                    if (!view.ByHeadId.ContainsKey(headId))
                        stale.Add(headId);
                for (int s = 0; s < stale.Count; s++)
                    m.ExcludedThroughLineHeadIds.Remove(stale[s]);
                removed += stale.Count;
            }

            if (removed > 0 && !SuppressLogging)
                ParsekLog.Warn("Mission",
                    $"ReconcileSelections: removed {removed} stale excluded through-line head id(s) " +
                    "(branches no longer current heads; re-included to avoid silently dropping them)");
            return removed;
        }

        /// <summary>
        /// Enables or disables loop playback on a Mission. Enabling enforces single
        /// selection: at most one Mission loops at a time, so turning <paramref name="target"/>
        /// on turns every other Mission off. Enabling also stamps <see cref="Mission.LoopAnchorUT"/>
        /// to <paramref name="currentUT"/> so the span clock phases from this moment: every enable
        /// (including the first, and every re-enable after a disable) restarts the looped mission
        /// from the recording's start instead of resuming mid-phase. Disabling only clears the
        /// target's flag and leaves the stale anchor in place (the next enable overwrites it).
        /// </summary>
        internal static void SetLoopEnabled(Mission target, bool on, double currentUT)
        {
            if (target == null)
                return;
            if (on)
            {
                int clearedOthers = 0;
                for (int i = 0; i < missions.Count; i++)
                {
                    Mission m = missions[i];
                    if (m == null || ReferenceEquals(m, target))
                        continue;
                    if (m.LoopPlayback)
                    {
                        m.LoopPlayback = false;
                        clearedOthers++;
                    }
                }
                target.LoopPlayback = true;
                target.LoopAnchorUT = currentUT;
                if (!SuppressLogging)
                    ParsekLog.Info("Mission",
                        $"SetLoopEnabled: loop ON for '{target.Name}' (tree={target.TreeId}); " +
                        $"anchorUT={currentUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}; " +
                        $"cleared {clearedOthers} other looping mission(s) (single-selection)");
            }
            else
            {
                target.LoopPlayback = false;
                if (!SuppressLogging)
                    ParsekLog.Info("Mission",
                        $"SetLoopEnabled: loop OFF for '{target.Name}' (tree={target.TreeId})");
            }
        }

        /// <summary>
        /// Enforces the single-loop invariant after load. SetLoopEnabled keeps at most one
        /// Mission looping during normal use, but a hand-edited save could carry several
        /// with LoopPlayback on. Keeps the first in list order and clears the rest, so the
        /// adapter's "first looping mission wins" never silently hides extra enabled loops.
        /// Returns the number cleared.
        /// </summary>
        internal static int NormalizeSingleLoop()
        {
            bool keptOne = false;
            int cleared = 0;
            for (int i = 0; i < missions.Count; i++)
            {
                Mission m = missions[i];
                if (m == null || !m.LoopPlayback)
                    continue;
                if (!keptOne)
                {
                    keptOne = true;
                    continue;
                }
                m.LoopPlayback = false;
                cleared++;
            }
            if (cleared > 0 && !SuppressLogging)
                ParsekLog.Warn("Mission",
                    $"NormalizeSingleLoop: cleared {cleared} extra looping mission(s) " +
                    "(only one Mission may loop at a time; kept the first)");
            return cleared;
        }

        internal static Mission Clone(Mission source)
        {
            if (source == null)
                return null;
            Mission copy = source.Clone(Guid.NewGuid().ToString("N"));
            missions.Add(copy);
            if (!SuppressLogging)
                ParsekLog.Info("Mission",
                    $"Cloned mission '{source.Name}' -> '{copy.Name}' (tree={source.TreeId})");
            return copy;
        }

        // A Mission can be deleted only when it is not the last one for its tree.
        internal static bool CanDelete(Mission m)
        {
            return m != null && CountForTree(m.TreeId) > 1;
        }

        internal static bool Delete(Mission m)
        {
            if (!CanDelete(m))
                return false;
            missions.Remove(m);
            if (!SuppressLogging)
                ParsekLog.Info("Mission", $"Deleted mission '{m.Name}' (tree={m.TreeId})");
            return true;
        }

        // --- Persistence (driven by ParsekScenario) ---

        internal static void Save(ConfigNode node)
        {
            node.RemoveNodes("MISSION");
            for (int i = 0; i < missions.Count; i++)
            {
                ConfigNode mNode = node.AddNode("MISSION");
                missions[i].Save(mNode);
            }
            if (!SuppressLogging)
                ParsekLog.Verbose("Mission", $"Saved {missions.Count} mission(s)");
        }

        internal static void Load(ConfigNode node)
        {
            missions.Clear();
            ConfigNode[] mNodes = node.GetNodes("MISSION");
            for (int i = 0; i < mNodes.Length; i++)
                missions.Add(Mission.Load(mNodes[i]));
            if (!SuppressLogging)
                ParsekLog.Info("Mission", $"Loaded {missions.Count} mission(s)");
        }
    }
}
