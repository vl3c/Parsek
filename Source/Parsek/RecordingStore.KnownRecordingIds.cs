using System;
using System.Collections.Generic;

namespace Parsek
{
    public static partial class RecordingStore
    {
        // The ids of every recording memory holds (committed list, committed trees, the pending
        // slots and the inert trees). RecordingFileJanitor and ParsekFileCollector gate their
        // deletions on these sets; they stay on the store because they read its private slots
        // and InertRecordingTrees, which on the janitor would put it back on a cycle.

        internal static HashSet<string> BuildKnownRecordingIdsForCleanup()
        {
            var knownIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < committedRecordings.Count; i++)
                AddKnownRecordingId(knownIds, committedRecordings[i]);

            AddKnownTreeRecordingIds(knownIds, pendingTree);
            AddKnownTreeRecordingIds(knownIds, savedPendingTreeDuringActiveRestore);
            // GC-3: recordings of committed trees kept inert are known (not orphans).
            InertRecordingTrees.AddRecordingIdsTo(knownIds);
            return knownIds;
        }

        private static void AddKnownRecordingId(HashSet<string> knownIds, Recording rec)
        {
            if (!string.IsNullOrEmpty(rec?.RecordingId))
                knownIds.Add(rec.RecordingId);
        }

        private static int AddKnownTreeRecordingIds(HashSet<string> knownIds, RecordingTree tree)
        {
            int count = 0;
            if (tree == null || tree.Recordings == null)
                return 0;

            foreach (var kvp in tree.Recordings)
            {
                if (!string.IsNullOrEmpty(kvp.Value?.RecordingId))
                {
                    AddKnownRecordingId(knownIds, kvp.Value);
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Builds the set of all recording IDs that are currently known to the store
        /// (committed recordings, committed trees, the pending tree, and the recordings of
        /// committed trees kept inert by <see cref="InertRecordingTrees"/>). Used by
        /// the OnSave stranded-sidecar warn and the load-fault preserve guard.
        /// Extracted as <c>internal static</c> for direct testability (#290).
        /// </summary>
        internal static HashSet<string> BuildKnownRecordingIds()
        {
            return BuildKnownRecordingIds(out _);
        }

        /// <summary>
        /// Overload that also reports how many IDs came from the pending tree
        /// (for diagnostic logging).
        /// </summary>
        internal static HashSet<string> BuildKnownRecordingIds(out int pendingTreeIdCount)
        {
            var knownIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < committedRecordings.Count; i++)
                AddKnownRecordingId(knownIds, committedRecordings[i]);

            for (int t = 0; t < committedTrees.Count; t++)
            {
                var treeRecordings = committedTrees[t]?.Recordings;
                if (treeRecordings == null) continue;
                foreach (var kvp in treeRecordings)
                    AddKnownRecordingId(knownIds, kvp.Value);
            }

            pendingTreeIdCount = 0;
            pendingTreeIdCount += AddKnownTreeRecordingIds(knownIds, pendingTree);
            pendingTreeIdCount += AddKnownTreeRecordingIds(
                knownIds, savedPendingTreeDuringActiveRestore);
            // GC-3: recordings of committed trees kept inert are known, so the ledger
            // reconcile keeps their rows and the sweeps do not treat them as orphans.
            InertRecordingTrees.AddRecordingIdsTo(knownIds);
            return knownIds;
        }
    }
}
