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

        // Global "Archive" toggle for the Missions window: when true, archived missions are
        // hidden from the list (mirrors the recordings window's GroupHierarchyStore.HideActive).
        // Persisted alongside the missions so the view state survives a reload.
        internal static bool HideArchived;

        internal static IReadOnlyList<Mission> Missions => missions;

        internal static void ResetForTesting()
        {
            missions.Clear();
            HideArchived = false;
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
        /// Enables or disables loop playback on a Mission. Multiple Missions may loop at once, but
        /// at most ONE per recording tree: enabling <paramref name="target"/> turns off any other
        /// looping Mission that shares its <see cref="Mission.TreeId"/>, while leaving looping
        /// Missions on OTHER trees untouched. (Two Missions on the same tree are variant selections
        /// that share trunk legs before any fork, so their committed-recording indices overlap and
        /// cannot each own a span clock; Missions on different trees have disjoint indices and loop
        /// concurrently with no conflict.) Enabling also stamps <see cref="Mission.LoopAnchorUT"/>
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
                int clearedSameTree = 0;
                for (int i = 0; i < missions.Count; i++)
                {
                    Mission m = missions[i];
                    if (m == null || ReferenceEquals(m, target))
                        continue;
                    // Only clear looping siblings on the SAME tree; concurrent loops on other
                    // trees are allowed.
                    if (m.LoopPlayback && string.Equals(m.TreeId, target.TreeId, StringComparison.Ordinal))
                    {
                        m.LoopPlayback = false;
                        clearedSameTree++;
                    }
                }
                target.LoopPlayback = true;
                target.LoopAnchorUT = currentUT;
                if (!SuppressLogging)
                    ParsekLog.Info("Mission",
                        $"SetLoopEnabled: loop ON for '{target.Name}' (tree={target.TreeId}); " +
                        $"anchorUT={currentUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}; " +
                        $"cleared {clearedSameTree} other looping mission(s) on the same tree (one loop per tree)");
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
        /// Enforces the one-loop-per-tree invariant after load. SetLoopEnabled keeps at most one
        /// Mission looping per tree during normal use, but a hand-edited save could carry several
        /// looping Missions that share a tree. Keeps the first in list order for each tree and
        /// clears the rest, so the adapter never builds two conflicting span clocks over the same
        /// (overlapping) committed indices. Looping Missions on distinct trees are all kept.
        /// Returns the number cleared.
        /// </summary>
        internal static int NormalizeOneLoopPerTree()
        {
            var seenTrees = new HashSet<string>(StringComparer.Ordinal);
            int cleared = 0;
            for (int i = 0; i < missions.Count; i++)
            {
                Mission m = missions[i];
                if (m == null || !m.LoopPlayback)
                    continue;
                string treeId = m.TreeId ?? string.Empty;
                if (seenTrees.Add(treeId))
                    continue; // first looping mission for this tree — keep it
                m.LoopPlayback = false;
                cleared++;
            }
            if (cleared > 0 && !SuppressLogging)
                ParsekLog.Warn("Mission",
                    $"NormalizeOneLoopPerTree: cleared {cleared} extra looping mission(s) " +
                    "(at most one Mission may loop per tree; kept the first of each)");
            return cleared;
        }

        internal static Mission Clone(Mission source)
        {
            if (source == null)
                return null;
            Mission copy = source.Clone(Guid.NewGuid().ToString("N"));
            // Insert the copy directly after its source so a clone sits next to the
            // original it was made from (and shares its tree index in the UI). Falls
            // back to append if the source is somehow not in the list.
            int srcIdx = missions.IndexOf(source);
            if (srcIdx >= 0)
                missions.Insert(srcIdx + 1, copy);
            else
                missions.Add(copy);
            if (!SuppressLogging)
                ParsekLog.Info("Mission",
                    $"Cloned mission '{source.Name}' -> '{copy.Name}' (tree={source.TreeId})");
            return copy;
        }

        // Only a COPY can be deleted; the original mission of a tree is never deletable. The
        // original is the first mission in list order for the tree (the auto-created default;
        // clones are inserted after it). So a mission is deletable iff it is NOT the first mission
        // of its tree. This also keeps every tree with at least its original.
        internal static bool CanDelete(Mission m)
        {
            if (m == null)
                return false;
            for (int i = 0; i < missions.Count; i++)
            {
                if (missions[i] == null || missions[i].TreeId != m.TreeId)
                    continue;
                // First same-tree mission found: it is the original. Deletable only if it is not m.
                return !ReferenceEquals(missions[i], m);
            }
            return false;
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
            node.RemoveValues("missionHideArchived");
            node.AddValue("missionHideArchived", HideArchived);
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
            HideArchived = bool.TryParse(node.GetValue("missionHideArchived"), out bool hide) && hide;
            ConfigNode[] mNodes = node.GetNodes("MISSION");
            for (int i = 0; i < mNodes.Length; i++)
                missions.Add(Mission.Load(mNodes[i]));
            if (!SuppressLogging)
                ParsekLog.Info("Mission", $"Loaded {missions.Count} mission(s)");
        }
    }
}
