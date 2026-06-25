using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    internal static partial class GhostMapPresence
    {
        private static ReFlySuppressionSearchTreeCache cachedReFlySuppressionSearchTrees;

        private sealed class ReFlySuppressionSearchTreeCache
        {
            internal readonly IReadOnlyList<RecordingTree> CommittedTrees;
            internal readonly RecordingTree PendingTree;
            internal readonly RecordingTree[] CommittedSnapshot;
            internal readonly IReadOnlyList<RecordingTree> ComposedTrees;

            internal ReFlySuppressionSearchTreeCache(
                IReadOnlyList<RecordingTree> committedTrees,
                RecordingTree pendingTree,
                RecordingTree[] committedSnapshot,
                IReadOnlyList<RecordingTree> composedTrees)
            {
                CommittedTrees = committedTrees;
                PendingTree = pendingTree;
                CommittedSnapshot = committedSnapshot;
                ComposedTrees = composedTrees;
            }
        }

        /// <summary>
        /// #611: composes the search-tree list for the Re-Fly suppression
        /// predicate. Production callers pass <see cref="RecordingStore.CommittedTrees"/>
        /// and (when present) <see cref="RecordingStore.PendingTree"/>; tests
        /// pass an explicit list. Pure-static so unit tests can construct
        /// arbitrary topologies without touching <c>RecordingStore</c>.
        /// <para>
        /// The list MUST include the pending tree because at Re-Fly load
        /// time <see cref="ParsekScenario.TryRestoreActiveTreeNode"/> calls
        /// <see cref="RecordingStore.RemoveCommittedTreeById"/> after the
        /// splice runs, leaving the freshly-loaded tree only as PendingTree.
        /// A predicate that searched only committed trees would silently
        /// fail the active-recording lookup during the load window — which
        /// is exactly when the doubled-vessel ProtoVessel get created (#611).
        /// </para>
        /// <para>
        /// The pending tree is appended (not prepended) so the existing
        /// tie-break behaviour for in-memory committedTrees still wins on
        /// id collisions; the BFS walk's first-match-wins loop then
        /// terminates at the same tree it would have found pre-#611 in the
        /// steady state, and falls through to the pending tree only when
        /// committed-trees lookup misses — matching the diagnosed bug
        /// shape.
        /// </para>
        /// </summary>
        internal static IReadOnlyList<RecordingTree> ComposeSearchTreesForReFlySuppression(
            IReadOnlyList<RecordingTree> committedTrees,
            RecordingTree pendingTree)
        {
            int committedCount = committedTrees?.Count ?? 0;
            bool hasPending = pendingTree != null;
            if (!hasPending)
            {
                ClearCachedReFlySuppressionSearchTrees();
                return committedTrees ?? Array.Empty<RecordingTree>();
            }

            if (TryGetCachedReFlySuppressionSearchTrees(
                    committedTrees,
                    committedCount,
                    pendingTree,
                    out IReadOnlyList<RecordingTree> cached))
            {
                return cached;
            }

            var result = new List<RecordingTree>(committedCount + 1);
            var snapshot = new RecordingTree[committedCount];
            for (int i = 0; i < committedCount; i++)
            {
                RecordingTree t = committedTrees[i];
                snapshot[i] = t;
                if (t == null) continue;
                if (string.Equals(t.Id, pendingTree.Id, StringComparison.Ordinal))
                {
                    // Same tree id in both — drop the committed entry and
                    // keep only the pending copy. At the moment both are
                    // present (load-time transient), pending carries the
                    // post-splice + post-refresh shape that the predicate
                    // needs to walk; committed is the pre-load snapshot.
                    // Skipping committed here avoids double-walk +
                    // visited-set churn; the pending append below makes
                    // the tree visible to the search.
                    continue;
                }
                result.Add(t);
            }
            result.Add(pendingTree);
            cachedReFlySuppressionSearchTrees = new ReFlySuppressionSearchTreeCache(
                committedTrees,
                pendingTree,
                snapshot,
                result);
            return result;
        }

        private static void ClearCachedReFlySuppressionSearchTrees()
        {
            cachedReFlySuppressionSearchTrees = null;
        }

        private static bool TryGetCachedReFlySuppressionSearchTrees(
            IReadOnlyList<RecordingTree> committedTrees,
            int committedCount,
            RecordingTree pendingTree,
            out IReadOnlyList<RecordingTree> cached)
        {
            cached = null;
            ReFlySuppressionSearchTreeCache cache = cachedReFlySuppressionSearchTrees;
            if (cache == null
                || cache.ComposedTrees == null
                || !ReferenceEquals(cache.CommittedTrees, committedTrees)
                || !ReferenceEquals(cache.PendingTree, pendingTree)
                || cache.CommittedSnapshot == null
                || cache.CommittedSnapshot.Length != committedCount)
            {
                return false;
            }

            // RecordingStore keeps the list instance stable while mutating its
            // contents in a few load/merge paths. Validate the source refs so
            // the cache removes hot-path allocations without serving stale tree
            // entries after same-count replacement.
            for (int i = 0; i < committedCount; i++)
            {
                if (!ReferenceEquals(cache.CommittedSnapshot[i], committedTrees[i]))
                    return false;
            }

            cached = cache.ComposedTrees;
            return true;
        }
    }
}
