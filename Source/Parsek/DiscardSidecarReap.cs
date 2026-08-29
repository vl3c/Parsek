using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Shared explicit-id sidecar reap for ACTIVE-TREE discards.
    ///
    /// <para><b>Why this exists.</b> <c>StartRecording</c>'s quickload-resume OnSave
    /// writes ACTIVE-tree sidecars to <c>saves/&lt;save&gt;/Parsek/Recordings/</c> - the
    /// seven files <c>RecordingStore.DeleteRecordingFiles</c> unlinks (<c>.prec</c>,
    /// <c>.pann</c>, <c>_vessel.craft</c>, <c>_ghost.craft</c> and the three readable
    /// mirrors), plus, for a recording that carries one, its per-recording
    /// quickload-resume save at <c>Parsek/Saves/&lt;name&gt;.sfs</c> (NOT the
    /// Rewind-to-Separation quicksaves under <c>Parsek/RewindPoints/</c>, which have a
    /// separate builder and their own reaper) - but
    /// the shared discard core <c>ParsekFlight.AutoDiscardActiveTreeCore</c> is
    /// in-memory-only BY DESIGN. A discard that empties the store therefore strands
    /// those files PERMANENTLY: once their ids are absent from
    /// <c>RecordingStore.BuildKnownRecordingIds()</c>, <c>CleanOrphanFiles</c>' zero-known
    /// safety guard (correctly) refuses to touch the directory at all, so nothing ever
    /// reclaims them. The harness-side twin of this leak was fixed for the S0.5
    /// <c>DiscardTree</c> test-command verb; this class is the same fix, promoted to a
    /// shared surface so the normal-gameplay discard paths get it too.</para>
    ///
    /// <para><b>Explicit ids only.</b> This is NEVER a directory sweep. It deletes only
    /// the sidecar set of recordings the caller captured from the tree it is tearing
    /// down, and only after the teardown, so the store's own known-id set is the
    /// authority on what survives. The sweeper (<c>CleanOrphanFiles</c>) and its
    /// zero-known guard are untouched.</para>
    ///
    /// <para><b>Two guards, both mandatory.</b>
    /// <list type="number">
    ///   <item><description><see cref="SelectReapRecordings"/> - the known-ids guard.
    ///   Any id the store still owns after the discard (committed recording, committed
    ///   tree, pending tree) is PRESERVED: those files are real mission data. A
    ///   committed-restore clone shares its committed original's id, so the original's
    ///   files are never touched. A null known set fails CLOSED (nothing reaped).</description></item>
    ///   <item><description><see cref="SkipReason"/> - the load-shape gate. During a
    ///   Re-Fly session, an open merge journal, or an active-tree restore, the restored
    ///   active tree holds the ONLY copy of the original mission's recordings
    ///   (<c>RewindInvoker</c> removes the committed tree before the restore pops it to
    ///   active), so their ids are legitimately absent from the known set while their
    ///   files are still the durable committed data. Reaping there would permanently
    ///   destroy committed mission data (Fable review of PR #1328, finding 1).</description></item>
    /// </list></para>
    ///
    /// <para><b>Failure safety.</b> A reap failure (a file locked by KSP or an
    /// antivirus) must never abort the discard. <c>DeleteRecordingFiles</c> swallows
    /// its own per-file failures and reports them through its return value, which this
    /// class counts into <see cref="ReapOutcome.Failed"/>; the call is additionally
    /// wrapped so a future throw could not escape either. A failed recording leaves its
    /// orphan on disk until a future discard-with-known-ids - i.e. it degrades to the
    /// pre-fix status quo - and the batch continues with the next recording.</para>
    /// </summary>
    internal static class DiscardSidecarReap
    {
        /// <summary>
        /// Test seam for the per-recording sidecar delete. Null in production, where the
        /// reap calls <c>RecordingStore.DeleteRecordingFiles</c> (which resolves paths
        /// through <c>KSPUtil.ApplicationRootPath</c> and therefore cannot run headless).
        /// Carries the same contract as the production delete: true when nothing was
        /// left behind, false when at least one file could not be unlinked.
        /// </summary>
        internal static Func<Recording, bool> DeleteRecordingFilesForTesting;

        /// <summary>
        /// Test seam for the post-discard known-id set. Null in production, where the
        /// reap calls <c>RecordingStore.BuildKnownRecordingIds()</c>.
        /// </summary>
        internal static Func<HashSet<string>> KnownRecordingIdsForTesting;

        /// <summary>Clears both test seams. Call from a test class's Dispose.</summary>
        internal static void ResetForTesting()
        {
            DeleteRecordingFilesForTesting = null;
            KnownRecordingIdsForTesting = null;
        }

        /// <summary>Per-call counters for the one-line batch summary and for tests.</summary>
        internal struct ReapOutcome
        {
            /// <summary>Recordings the caller captured from the discarded tree.</summary>
            internal int TreeRecordings;
            /// <summary>Ids in the post-discard known set (0 = discard-to-empty).</summary>
            internal int KnownAfterDiscard;
            /// <summary>Recordings whose whole on-disk footprint was unlinked.</summary>
            internal int Reaped;
            /// <summary>
            /// Recordings that left at least one file behind - a locked file reported by
            /// <c>DeleteRecordingFiles</c>, or a throw out of the delete. The orphan
            /// persists until a future discard-with-known-ids (the pre-fix status quo).
            /// </summary>
            internal int Failed;
            /// <summary>Recordings preserved because the store still owns the id.</summary>
            internal int PreservedKnown;
            /// <summary>Recordings skipped because the id failed path validation.</summary>
            internal int InvalidId;
            /// <summary>Non-null when the load-shape gate suppressed the whole reap.</summary>
            internal string Skipped;
        }

        /// <summary>
        /// Load-shape gate for the discard sidecar reap. Returns the skip reason, or
        /// null to reap. See the class remarks for why each shape must not reap.
        /// </summary>
        internal static string SkipReason(
            bool reFlyMarkerActive, bool mergeJournalActive, bool restoringActiveTree)
        {
            if (reFlyMarkerActive) return "refly-marker-active";
            if (mergeJournalActive) return "merge-journal-active";
            if (restoringActiveTree) return "restoring-active-tree";
            return null;
        }

        /// <summary>
        /// Known-ids guard: selects which recordings of a just-discarded tree may have
        /// their on-disk sidecars reaped. Reaps ONLY ids absent from the POST-discard
        /// known-id set. A null known set fails CLOSED (nothing reaped): this is a
        /// deletion guard, and every production caller passes a real set.
        /// </summary>
        internal static List<Recording> SelectReapRecordings(
            IEnumerable<Recording> discardedTreeRecordings,
            ICollection<string> knownIdsAfterDiscard)
        {
            var reap = new List<Recording>();
            if (discardedTreeRecordings == null || knownIdsAfterDiscard == null)
                return reap;
            foreach (var rec in discardedTreeRecordings)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (knownIdsAfterDiscard.Contains(rec.RecordingId))
                    continue;
                reap.Add(rec);
            }
            return reap;
        }

        /// <summary>
        /// Snapshots the recordings of a tree that is about to be torn down. MUST be
        /// called BEFORE the teardown nulls the tree - the reap itself must run AFTER,
        /// so the store's known-id set no longer owns the discarded ids.
        /// </summary>
        internal static List<Recording> CaptureTreeRecordings(RecordingTree tree)
        {
            var captured = new List<Recording>();
            if (tree?.Recordings == null)
                return captured;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec != null)
                    captured.Add(rec);
            }
            return captured;
        }

        /// <summary>
        /// Production entry point. Reaps the sidecar sets of
        /// <paramref name="capturedRecordings"/> whose ids the store no longer owns.
        /// Call AFTER the in-memory teardown. Never throws: every delete is wrapped and
        /// a failure degrades to the pre-fix status quo (the orphan persists).
        /// </summary>
        /// <param name="capturedRecordings">
        /// The discarded tree's recordings, captured BEFORE teardown via
        /// <see cref="CaptureTreeRecordings"/>.
        /// </param>
        /// <param name="skipReason">
        /// The <see cref="SkipReason"/> verdict sampled BEFORE teardown. Non-null
        /// suppresses the whole reap (logged, never silent).
        /// </param>
        /// <param name="context">
        /// Short caller name for the log line (e.g. the discard reason). Makes the reap
        /// greppable per discard path.
        /// </param>
        internal static ReapOutcome ReapDiscardedTreeSidecars(
            IEnumerable<Recording> capturedRecordings,
            string skipReason,
            string context)
        {
            var outcome = new ReapOutcome();
            string tag = string.IsNullOrEmpty(context) ? "<none>" : context;

            var captured = new List<Recording>();
            if (capturedRecordings != null)
            {
                foreach (var rec in capturedRecordings)
                    captured.Add(rec);
            }
            outcome.TreeRecordings = captured.Count;

            if (!string.IsNullOrEmpty(skipReason))
            {
                outcome.Skipped = skipReason;
                ParsekLog.Info("DiscardReap",
                    $"discard sidecar-reap: skipped context='{tag}' reason={skipReason} " +
                    $"treeRecordings={outcome.TreeRecordings}");
                return outcome;
            }

            HashSet<string> knownAfterDiscard;
            try
            {
                knownAfterDiscard = KnownRecordingIdsForTesting != null
                    ? KnownRecordingIdsForTesting()
                    : RecordingStore.BuildKnownRecordingIds();
            }
            catch (Exception ex)
            {
                // Fail CLOSED: without a trustworthy known-id set we cannot prove a
                // candidate is not live mission data, so delete nothing.
                ParsekLog.Warn("DiscardReap",
                    $"discard sidecar-reap: skipped context='{tag}' reason=known-ids-unavailable " +
                    $"{ex.GetType().Name}: {ex.Message}");
                outcome.Skipped = "known-ids-unavailable";
                return outcome;
            }
            outcome.KnownAfterDiscard = knownAfterDiscard?.Count ?? 0;

            var reapList = SelectReapRecordings(captured, knownAfterDiscard);
            outcome.PreservedKnown = captured.Count - reapList.Count;

            var delete = DeleteRecordingFilesForTesting;
            foreach (var rec in reapList)
            {
                // Same path-safety contract as every other delete site: an id that
                // fails validation never reaches the filesystem.
                if (!RecordingPaths.ValidateRecordingId(rec.RecordingId))
                {
                    outcome.InvalidId++;
                    continue;
                }
                try
                {
                    // DeleteRecordingFiles swallows its own per-file failures (and logs
                    // its own Warn for each) but REPORTS them: false means at least one
                    // file is still on disk. Count that as a failure rather than
                    // assuming success, so `failed=` in the summary is truthful.
                    bool allDeleted = delete != null
                        ? delete(rec)
                        : RecordingStore.DeleteRecordingFiles(rec);
                    if (allDeleted)
                    {
                        outcome.Reaped++;
                    }
                    else
                    {
                        outcome.Failed++;
                        ParsekLog.Warn("DiscardReap",
                            $"discard sidecar-reap failed context='{tag}' id={rec.RecordingId} " +
                            "reason=file-not-unlinked - orphan retained, discard continues");
                    }
                }
                catch (Exception ex)
                {
                    // Defence in depth: the production delete does not throw today, but
                    // a locked file must never abort the discard if that ever changes.
                    outcome.Failed++;
                    ParsekLog.Warn("DiscardReap",
                        $"discard sidecar-reap failed context='{tag}' id={rec.RecordingId} " +
                        $"{ex.GetType().Name}: {ex.Message} - orphan retained, discard continues");
                }
            }

            ParsekLog.Info("DiscardReap",
                $"discard sidecar-reap: context='{tag}' " +
                $"treeRecordings={outcome.TreeRecordings} " +
                $"knownAfterDiscard={outcome.KnownAfterDiscard} " +
                $"reaped={outcome.Reaped} failed={outcome.Failed} " +
                $"preservedKnown={outcome.PreservedKnown} invalidId={outcome.InvalidId}");
            return outcome;
        }
    }
}
