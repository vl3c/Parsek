using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Pure decisions behind the optimization pass that <see cref="ParsekFlight.CommitTreeFlight"/>
    /// runs after committing a tree IN FLIGHT (todo IN-FLIGHT-COMMIT-SKIPS-OPTIMIZATION-PASS).
    ///
    /// <para>The in-flight commit differs from <see cref="MergeDialog.MergeCommit"/> in one way
    /// that matters to the optimizer: it stamps the live active vessel's recording as spawned
    /// (<c>VesselSpawned</c> + <c>SpawnedVesselPersistentId</c>) at the commit, while the merge
    /// dialog lets playback spawn the chain tip later. An optimizer split moves the terminal
    /// state and the <c>VesselSnapshot</c> to a NEW second half with a fresh id and does not
    /// move the spawn stamp, and a merge can absorb the recording into an earlier chain segment.
    /// So after the pass the stamp must follow the vessel onto its chain tip, or the tip reads
    /// as an unspawned spawnable leaf of a vessel that is already live.</para>
    /// </summary>
    internal static class InFlightCommitOptimization
    {
        internal const string SkipReasonReFlySession = "re-fly-session-active";
        internal const string SkipReasonMergeJournal = "merge-journal-active";

        /// <summary>
        /// Returns null when the in-flight commit may run the optimization pass, else the
        /// skip reason. A live Re-Fly session is skipped because the merge dialog's Re-Fly
        /// handling (survivor hint, supersede rows, retained-tip adoption) is not part of the
        /// in-flight commit, and CommitTree's Re-Fly union branch commits a DIFFERENT tree
        /// object than the one the flight controller holds. An active merge journal is
        /// skipped because the journal owns the committed list until it finishes. Either way
        /// the next cold load's pass optimizes the tree, which is the behavior before this pass
        /// existed.
        /// </summary>
        internal static string DecideSkipReason(bool reFlySessionActive, bool mergeJournalActive)
        {
            if (reFlySessionActive) return SkipReasonReFlySession;
            if (mergeJournalActive) return SkipReasonMergeJournal;
            return null;
        }

        /// <summary>
        /// Resolves the recording that ends <paramref name="rec"/>'s vessel line after an
        /// optimization pass: the branch-0 member of the same chain and tree with the highest
        /// <c>ChainIndex</c> (the pass reindexes a chain by StartUT after every split and
        /// merge). A recording with no chain, or on a parallel branch (ghost-only, never
        /// spawned), resolves to itself. Returns null only for a null input.
        /// </summary>
        internal static Recording ResolveChainTip(Recording rec, IReadOnlyList<Recording> committed)
        {
            if (rec == null) return null;
            if (string.IsNullOrEmpty(rec.ChainId) || rec.ChainBranch != 0 || committed == null)
                return rec;

            Recording tip = null;
            for (int i = 0; i < committed.Count; i++)
            {
                var c = committed[i];
                if (c == null || c.ChainBranch != 0) continue;
                if (!string.Equals(c.ChainId, rec.ChainId, StringComparison.Ordinal)) continue;
                if (!string.Equals(c.TreeId, rec.TreeId, StringComparison.Ordinal)) continue;
                if (tip == null || c.ChainIndex > tip.ChainIndex)
                    tip = c;
            }
            return tip ?? rec;
        }

        /// <summary>
        /// Moves the active vessel's spawn stamp from <paramref name="original"/> onto
        /// <paramref name="tip"/> when the pass gave the vessel line a different tip. The
        /// original is cleared so the committed shape matches a scene-exit commit (an
        /// unstamped head with no snapshot, a stamped tip) and no two recordings claim the
        /// same live pid. Returns true when a stamp moved.
        /// </summary>
        internal static bool CarrySpawnStampToTip(Recording original, Recording tip)
        {
            if (original == null || tip == null || ReferenceEquals(original, tip))
                return false;
            if (!original.VesselSpawned && original.SpawnedVesselPersistentId == 0)
                return false;

            tip.VesselSpawned = original.VesselSpawned;
            tip.SpawnedVesselPersistentId = original.SpawnedVesselPersistentId;
            tip.LastAppliedResourceIndex = tip.Points.Count - 1;
            original.VesselSpawned = false;
            original.SpawnedVesselPersistentId = 0;
            return true;
        }

        /// <summary>
        /// Counts the distinct post-pass tips of <paramref name="leaves"/> (captured before the
        /// commit) that are spawned, excluding the active vessel's tip.
        /// </summary>
        internal static int CountSpawnedLeafTips(
            IReadOnlyList<Recording> leaves, IReadOnlyList<Recording> committed, string activeTipId)
        {
            if (leaves == null) return 0;
            var seen = new HashSet<Recording>();
            int count = 0;
            for (int i = 0; i < leaves.Count; i++)
            {
                var tip = ResolveChainTip(leaves[i], committed);
                if (tip == null || !seen.Add(tip)) continue;
                if (tip.RecordingId != activeTipId && tip.VesselSpawned)
                    count++;
            }
            return count;
        }
    }
}
