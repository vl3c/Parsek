using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    public partial class ParsekKSC
    {
        // Renders a TerminalState->count dictionary as `Key1=N, Key2=M` ordered
        // by descending count with an ordinal name tie-break, so the breakdown
        // line is stable across log diffs. Returns "(none)" for empty dicts so
        // the surrounding `terminal(...)` parens never collapse to `terminal()`.
        private static string FormatTerminalCounts(Dictionary<string, int> counts)
        {
            if (counts == null || counts.Count == 0)
                return "(none)";
            var ordered = new List<KeyValuePair<string, int>>(counts);
            ordered.Sort((a, b) =>
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            var parts = new List<string>(ordered.Count);
            foreach (var kv in ordered) parts.Add($"{kv.Key}={kv.Value}");
            return string.Join(", ", parts);
        }

        private static int CompareAutoLoopQueueCandidates(AutoLoopQueueCandidate a, AutoLoopQueueCandidate b)
        {
            int cmp = a.PlaybackStartUT.CompareTo(b.PlaybackStartUT);
            if (cmp != 0)
                return cmp;

            cmp = a.PlaybackEndUT.CompareTo(b.PlaybackEndUT);
            if (cmp != 0)
                return cmp;

            cmp = string.CompareOrdinal(a.RecordingId, b.RecordingId);
            if (cmp != 0)
                return cmp;

            return a.RecordingIndex.CompareTo(b.RecordingIndex);
        }

        internal static bool ShouldAdvanceCareerLedgerForKscUT(
            double currentUT,
            double lastAppliedUT,
            double nextActionUT,
            double epsilon)
        {
            if (double.IsNaN(currentUT) || double.IsInfinity(currentUT))
                return false;
            if (double.IsNaN(lastAppliedUT) || double.IsInfinity(lastAppliedUT))
                return false;
            if (currentUT < lastAppliedUT - epsilon)
                return true;
            if (double.IsNaN(nextActionUT) || double.IsInfinity(nextActionUT))
                return false;
            return currentUT >= nextActionUT;
        }

        internal static bool ShouldSeedCareerLedgerForKscUT(
            double currentUT,
            double lastAppliedUT)
        {
            if (double.IsNaN(currentUT) || double.IsInfinity(currentUT))
                return false;
            return double.IsNaN(lastAppliedUT);
        }

        internal static string GetCareerLedgerAdvanceReasonForKscUT(
            double currentUT,
            double lastAppliedUT,
            double epsilon)
        {
            return currentUT < lastAppliedUT - epsilon
                ? "ksc-clock-backward"
                : "ksc-clock";
        }

        internal static bool IsKscLedgerNextActionCacheValid(
            int cachedLedgerVersion,
            double cachedAfterUT,
            int currentLedgerVersion,
            double afterUT)
        {
            return cachedLedgerVersion == currentLedgerVersion
                && !double.IsNaN(cachedAfterUT)
                && cachedAfterUT == afterUT;
        }

        private static string BuildAutoLoopQueueFingerprint(
            IReadOnlyList<AutoLoopQueueCandidate> queue,
            double anchorUT,
            double cadenceSeconds)
        {
            int count = queue?.Count ?? 0;
            return string.Format(
                CultureInfo.InvariantCulture,
                "count={0}|anchor={1}|cadence={2}|ids={3}",
                count,
                anchorUT.ToString("R", CultureInfo.InvariantCulture),
                cadenceSeconds.ToString("R", CultureInfo.InvariantCulture),
                BuildAutoLoopQueueOrderedIds(queue));
        }

        private static string BuildAutoLoopQueueOrderedIds(IReadOnlyList<AutoLoopQueueCandidate> queue)
        {
            if (queue == null || queue.Count == 0)
                return "(empty)";

            var ids = new List<string>(queue.Count);
            for (int i = 0; i < queue.Count; i++)
            {
                AutoLoopQueueCandidate candidate = queue[i];
                string id = string.IsNullOrEmpty(candidate.RecordingId)
                    ? "(no-id)"
                    : candidate.RecordingId;
                ids.Add(candidate.RecordingIndex.ToString(CultureInfo.InvariantCulture) + ":" + id);
            }

            return string.Join(",", ids.ToArray());
        }

        internal static bool LogPlaybackDisabledPastEndSpawnAttemptOnce(
            Recording rec,
            int recIdx,
            string reason,
            ISet<string> loggedKeys)
        {
            string safeReason = string.IsNullOrEmpty(reason)
                ? "playback-disabled-past-end"
                : reason;
            string id = !string.IsNullOrEmpty(rec?.RecordingId)
                ? rec.RecordingId
                : "idx:" + recIdx.ToString(CultureInfo.InvariantCulture);
            string key = safeReason + "|" + id;
            if (loggedKeys != null && !loggedKeys.Add(key))
                return false;

            ParsekLog.Verbose("KSCSpawn",
                $"Playback-disabled past-end: attempting spawn for #{recIdx} " +
                $"\"{rec?.VesselName ?? "(null)"}\" id={rec?.RecordingId ?? "(null)"} " +
                $"reason={safeReason}");
            return true;
        }

        /// <summary>
        /// Whether a ghost-dictionary index key is orphaned for a committed list of
        /// <paramref name="committedCount"/> entries: any key outside the half-open range
        /// [0, committedCount). Pure; <see cref="ReapOrphanedKscGhosts"/> applies it per
        /// key to know which index-keyed ghosts to destroy after CommittedRecordings shrinks.
        /// </summary>
        internal static bool IsOrphanedGhostIndex(int key, int committedCount)
            => key < 0 || key >= committedCount;
    }
}
