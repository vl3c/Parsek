using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// A committed recording the loaded save names but that does not load keeps its ledger
    /// rows (owner ruling O3, 2026-10-21; game-actions design sections 3.13 and 4.3). The
    /// load drops such a recording (a sidecar that fails to hydrate, a partial schema reject,
    /// a tree that loads empty) and the next save no longer names it, so its rows lose their
    /// recording for good: the cold ledger load keeps them as career rows with the tag cleared
    /// (<see cref="LedgerOrchestrator.KeepRowsOfRecordingsThatFailedToLoad"/>), the same shape
    /// a non-rewind discard leaves. A whole tree the schema gate rejects stays inert instead
    /// (GC-3) and keeps its tagged rows; a never-committed Re-Fly provisional is left to the
    /// load sweep.
    /// </summary>
    public partial class ParsekScenario
    {
        // The committed recording ids the last LoadRecordingTrees saw named but did not load.
        // Set by every LoadRecordingTrees, consumed by the cold ledger load that follows it.
        private static HashSet<string> lastLoadDroppedRecordingIds;

        /// <summary>
        /// Adds the id of every committed recording a <c>RECORDING_TREE</c> node names to
        /// <paramref name="into"/>. A recording stamped <c>NotCommitted</c> (a Re-Fly
        /// provisional the load sweep owns) is left out. Pure over the node.
        /// </summary>
        internal static void CollectNamedCommittedRecordingIds(ConfigNode treeNode, HashSet<string> into)
        {
            if (treeNode == null || into == null)
                return;
            ConfigNode[] recNodes = treeNode.GetNodes("RECORDING");
            for (int i = 0; i < recNodes.Length; i++)
            {
                string id = recNodes[i].GetValue("recordingId");
                if (string.IsNullOrEmpty(id))
                    continue;
                if (string.Equals(recNodes[i].GetValue("mergeState"),
                        MergeState.NotCommitted.ToString(), StringComparison.Ordinal))
                    continue;
                into.Add(id);
            }
        }

        /// <summary>
        /// The ids of <paramref name="namedIds"/> that no tree of <paramref name="loadedTrees"/>
        /// holds. Pure.
        /// </summary>
        internal static HashSet<string> ComputeLoadDroppedRecordingIds(
            HashSet<string> namedIds, IList<RecordingTree> loadedTrees)
        {
            var dropped = new HashSet<string>(StringComparer.Ordinal);
            if (namedIds == null || namedIds.Count == 0)
                return dropped;
            var loaded = new HashSet<string>(StringComparer.Ordinal);
            if (loadedTrees != null)
            {
                for (int t = 0; t < loadedTrees.Count; t++)
                {
                    var recordings = loadedTrees[t]?.Recordings;
                    if (recordings == null) continue;
                    foreach (var kvp in recordings)
                    {
                        if (!string.IsNullOrEmpty(kvp.Value?.RecordingId))
                            loaded.Add(kvp.Value.RecordingId);
                    }
                }
            }
            foreach (string id in namedIds)
            {
                if (!loaded.Contains(id))
                    dropped.Add(id);
            }
            return dropped;
        }

        private static void NoteLoadDroppedRecordings(
            HashSet<string> namedIds, IList<RecordingTree> loadedTrees)
        {
            lastLoadDroppedRecordingIds = ComputeLoadDroppedRecordingIds(namedIds, loadedTrees);
            ParsekLog.Verbose("Scenario",
                "LoadRecordingTrees: named committed recordings=" +
                (namedIds?.Count ?? 0).ToString(CultureInfo.InvariantCulture) +
                " not loaded=" + lastLoadDroppedRecordingIds.Count.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Hands over (and forgets) the ids the last <c>LoadRecordingTrees</c> did not load.
        /// Null when none.
        /// </summary>
        internal static HashSet<string> TakeLoadDroppedRecordingIds()
        {
            HashSet<string> ids = lastLoadDroppedRecordingIds;
            lastLoadDroppedRecordingIds = null;
            return ids != null && ids.Count > 0 ? ids : null;
        }
    }
}
