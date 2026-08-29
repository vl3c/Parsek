using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure success-payload builders for the four recorder/tree verbs (P5.4 / P5.5).
    /// The Unity side samples ParsekFlight state around the actual
    /// <c>StartRecording</c> / <c>StopRecording</c> / <c>CommitTreeFlight</c> /
    /// <c>AutoDiscardActiveTreeWithMessage</c> calls (which all return void) and feeds
    /// the sampled booleans here. The no-op flags (<c>already</c> / <c>idle</c> /
    /// <c>nothing</c>) are the idempotency signals the orchestrator reads, so their
    /// presence rules are pinned by xUnit without Unity.
    /// </summary>
    internal static class TestCommandRecordingVerbs
    {
        /// <summary>
        /// StartRecording payload: always <c>recordingId</c> (sampled after the call);
        /// <c>already=true</c> only when a recorder was already live (no second recorder
        /// was forced).
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildStartPayload(bool alreadyLive, string recordingId)
        {
            var p = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("recordingId", recordingId ?? string.Empty),
            };
            if (alreadyLive)
                p.Add(new KeyValuePair<string, string>("already", "true"));
            return p;
        }

        /// <summary>
        /// StopRecording payload: <c>stopped</c> reflects whether a live recorder was
        /// stopped; <c>idle=true</c> only when there was no recorder (idempotent no-op).
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildStopPayload(bool wasLive)
        {
            var p = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("stopped", wasLive ? "true" : "false"),
            };
            if (!wasLive)
                p.Add(new KeyValuePair<string, string>("idle", "true"));
            return p;
        }

        /// <summary>CommitTree success payload (only reached with an active tree).</summary>
        internal static List<KeyValuePair<string, string>> BuildCommitPayload()
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("committed", "true"),
            };

        /// <summary>
        /// DiscardTree payload: <c>discarded=true</c> when an active tree was torn down;
        /// <c>nothing=true</c> when there was no active tree (idempotent no-op).
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildDiscardPayload(bool hadTree)
            => new List<KeyValuePair<string, string>>
            {
                hadTree
                    ? new KeyValuePair<string, string>("discarded", "true")
                    : new KeyValuePair<string, string>("nothing", "true"),
            };

        /// <summary>
        /// Selects which recordings of a just-discarded active tree should have their
        /// on-disk sidecars reaped. StartRecording's quickload-resume OnSave writes
        /// ACTIVE-tree sidecars (.prec/.pann/_ghost.craft) to disk, but the shared
        /// discard core is in-memory-only by design, so a discard-to-empty strands
        /// those files forever (CleanOrphanFiles refuses to delete when the store has
        /// zero known ids). Reap ONLY ids absent from the post-discard known-id set:
        /// a committed-restore clone shares its committed original's id, which stays
        /// known after the discard, so the original's files are never touched. A null
        /// known set fails CLOSED (nothing reaped): this is a deletion guard, and the
        /// caller always passes a real set.
        ///
        /// <para>Thin alias over <see cref="DiscardSidecarReap.SelectReapRecordings"/>,
        /// which is the shared implementation the gameplay discard paths use too.</para>
        /// </summary>
        internal static List<Recording> SelectDiscardReapRecordings(
            List<Recording> discardedTreeRecordings, HashSet<string> knownIdsAfterDiscard)
        {
            return DiscardSidecarReap.SelectReapRecordings(
                discardedTreeRecordings, knownIdsAfterDiscard);
        }

        /// <summary>
        /// Gate for the discard sidecar reap. During a Re-Fly session or an open merge
        /// journal, the restored active tree holds the ONLY copy of the original
        /// mission's recordings (RewindInvoker's load shape removes the committed tree
        /// via RemoveCommittedTreeById before RestoreActiveTreeFromPending pops it to
        /// active), so those ids are absent from the known set while their files are
        /// still the durable committed data - reaping would permanently destroy them
        /// out from under the merge journal's rollback. Same for the transient
        /// restore-in-progress window. Returns the skip reason, or null to reap.
        ///
        /// <para>Thin alias over <see cref="DiscardSidecarReap.SkipReason"/>, which is
        /// the shared implementation the gameplay discard paths use too.</para>
        /// </summary>
        internal static string DiscardReapSkipReason(
            bool reFlyMarkerActive, bool mergeJournalActive, bool restoringActiveTree)
        {
            return DiscardSidecarReap.SkipReason(
                reFlyMarkerActive, mergeJournalActive, restoringActiveTree);
        }
    }
}
