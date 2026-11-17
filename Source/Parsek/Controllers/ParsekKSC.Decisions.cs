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
            return CareerLedgerClockCursor.FormatReason("ksc", currentUT, lastAppliedUT, epsilon);
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

        /// <summary>
        /// Per-Update tally of <see cref="ClassifyKscStructuralRoute"/> over the recordings
        /// that reached the structural check (timeline-inactive ones never do).
        /// </summary>
        internal struct KscStructuralPassCounts : IEquatable<KscStructuralPassCounts>
        {
            internal int Render;
            internal int SpawnOnly;
            internal int Skip;

            internal int Total => Render + SpawnOnly + Skip;

            internal void Count(KscStructuralRoute route)
            {
                switch (route)
                {
                    case KscStructuralRoute.Render: Render++; break;
                    case KscStructuralRoute.SpawnOnly: SpawnOnly++; break;
                    default: Skip++; break;
                }
            }

            public bool Equals(KscStructuralPassCounts other)
            {
                return Render == other.Render && SpawnOnly == other.SpawnOnly && Skip == other.Skip;
            }
        }

        /// <summary>
        /// The structural pass summary is written only when the split changed since the last
        /// line and some recording reached the check.
        /// </summary>
        internal static bool ShouldLogKscStructuralPass(
            KscStructuralPassCounts current, KscStructuralPassCounts lastLogged)
        {
            return current.Total > 0 && !current.Equals(lastLogged);
        }

        internal static string FormatKscStructuralPassSummary(KscStructuralPassCounts counts)
        {
            return "KSC structural pass: drawable=" + counts.Render.ToString(CultureInfo.InvariantCulture)
                + " spawnOnly=" + counts.SpawnOnly.ToString(CultureInfo.InvariantCulture)
                + " (off Kerbin: no KSC ghost, end spawn still decided)"
                + " skipped=" + counts.Skip.ToString(CultureInfo.InvariantCulture)
                + " (fewer than 2 points: no ghost, no spawn)";
        }

        /// <summary>
        /// Once per recording per scene: the Space Center is about to run the past-end spawn
        /// for a recording it cannot draw. Returns true when the line was written.
        /// </summary>
        internal static bool LogKscSpawnOnlyAttemptOnce(
            Recording rec,
            int recIdx,
            string drawRejectReason,
            ISet<string> loggedKeys)
        {
            string reason = string.IsNullOrEmpty(drawRejectReason)
                ? KscStructuralReasonOffKerbin
                : drawRejectReason;
            string id = !string.IsNullOrEmpty(rec?.RecordingId)
                ? rec.RecordingId
                : "idx:" + recIdx.ToString(CultureInfo.InvariantCulture);
            string key = "spawn-only|" + reason + "|" + id;
            if (loggedKeys != null && !loggedKeys.Add(key))
                return false;

            string firstBody = rec?.Points != null && rec.Points.Count > 0
                ? rec.Points[0].bodyName ?? "(null)"
                : "(none)";
            ParsekLog.Verbose("KSCSpawn",
                "Spawn-only past-end: attempting spawn for #" + recIdx.ToString(CultureInfo.InvariantCulture)
                + " \"" + (rec?.VesselName ?? "(null)") + "\" id=" + (rec?.RecordingId ?? "(null)")
                + " reason=" + reason + " firstBody=" + firstBody
                + " (no KSC ghost; the end spawn is decided as in any scene)");
            return true;
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
