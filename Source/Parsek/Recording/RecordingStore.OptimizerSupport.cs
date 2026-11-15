using System;
using System.Collections.Generic;

namespace Parsek
{
    public static partial class RecordingStore
    {
        // --- Optimizer support ----------------------------------------------------
        //
        // Store-side helpers the optimization pass (CommittedListOptimizer) shares with the
        // commit paths (the dirty-file flush) and the Re-Fly tree splitter (the branch-point
        // placement rule over the committed trees).

        /// <summary>
        /// Epoch rule for <see cref="FlushDirtyFiles"/>, an OUT-OF-BAND write: the .sfs
        /// on disk still carries the epoch of the last OnSave, and every .sfs that names
        /// a recording holds an epoch no greater than the in-memory one. A positive epoch
        /// is therefore preserved (advancing it would leave .prec = .sfs + 1 until the
        /// next save, and a load followed by a quit or crash would then reject the
        /// sidecar as stale and drop the recording). An epoch of 0 means no .sfs has ever
        /// carried one, and 0 disables the stale check on load, so the flush seeds 1:
        /// that strands nothing and keeps bug #270's detector armed for the recording.
        /// A preserved epoch sets <see cref="Recording.SidecarEpochAdvancePending"/>: the
        /// content changed under an epoch an older quicksave may also carry, so the next
        /// OnSave rewrites the recording with the epoch advanced and .prec and .sfs move
        /// to N+1 together. Between the flush and that save (and for good if the process
        /// ends first, since the flag is not persisted) a pre-flush quicksave reads the
        /// flushed content at a matching epoch; that is the window bug #290 already
        /// accepted for the BG-recorder and scene-exit force-writes.
        /// </summary>
        internal static bool ShouldAdvanceSidecarEpochOnFlush(Recording rec)
        {
            return rec != null && rec.SidecarEpoch <= 0;
        }

        /// <summary>
        /// Saves all dirty recordings to disk immediately. Called after commit and
        /// after the optimization pass to close the crash window where data exists
        /// only in RAM. Failures are logged but non-fatal - OnSave will retry.
        /// Never advances an existing epoch (it defers the advance to the next OnSave);
        /// see <see cref="ShouldAdvanceSidecarEpochOnFlush"/>.
        /// </summary>
        private static void FlushDirtyFiles(List<Recording> recordings)
        {
            int saved = 0, failed = 0, epochSeeded = 0, epochPreserved = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (!recordings[i].FilesDirty) continue;
                bool advanceEpoch = ShouldAdvanceSidecarEpochOnFlush(recordings[i]);
                if (RecordingSidecarStore.SaveRecordingFiles(recordings[i], incrementEpoch: advanceEpoch))
                {
                    saved++;
                    if (advanceEpoch)
                    {
                        epochSeeded++;
                    }
                    else
                    {
                        epochPreserved++;
                        recordings[i].SidecarEpochAdvancePending = true;
                    }
                }
                else
                    failed++;
            }
            if (saved > 0 || failed > 0)
                ParsekLog.Info("RecordingStore",
                    $"FlushDirtyFiles: saved {saved}, failed {failed} " +
                    $"(epochPreserved={epochPreserved} epochSeeded={epochSeeded}; " +
                    "preserved epochs advance at the next OnSave)");
        }

        internal static bool ShouldMoveChildBranchPointToSplitSecondHalf(
            string treeId,
            string childBranchPointId,
            double secondStartUT)
        {
            // Optimizer-only helper: RunOptimizationPass operates on committed
            // trees, so this intentionally does not inspect PendingTree. If a
            // future pending-tree split path appears, add an explicit tree
            // parameter rather than broadening this committed-tree contract.
            if (string.IsNullOrEmpty(treeId) || string.IsNullOrEmpty(childBranchPointId))
                return false;
            if (double.IsNaN(secondStartUT) || double.IsInfinity(secondStartUT))
                return false;

            const double eps = 0.0001;
            for (int t = 0; t < committedTrees.Count; t++)
            {
                var tree = committedTrees[t];
                if (tree == null || !string.Equals(tree.Id, treeId, StringComparison.Ordinal))
                    continue;
                if (tree.BranchPoints == null)
                    return false;
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    var bp = tree.BranchPoints[b];
                    if (bp == null || !string.Equals(bp.Id, childBranchPointId, StringComparison.Ordinal))
                        continue;
                    return bp.UT >= secondStartUT - eps;
                }
                return false;
            }

            return false;
        }

        /// <summary>
        /// Flushes the dirty recordings of the committed list (<see cref="FlushDirtyFiles"/>);
        /// the optimization pass calls it after it restructured the list.
        /// </summary>
        internal static void FlushDirtyCommittedFiles()
        {
            FlushDirtyFiles(committedRecordings);
        }
    }
}
