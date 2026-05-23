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
