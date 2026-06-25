using System.Collections.Generic;

namespace Parsek
{
    internal static partial class GhostMapPresence
    {
        /// <summary>
        /// Pure: find recording IDs that are superseded by a later recording in the same chain.
        /// A recording is superseded if another recording's ParentRecordingId points to it.
        /// Chain-tip recordings are NOT in the returned set.
        /// </summary>
        internal static HashSet<string> FindSupersededRecordingIds(IReadOnlyList<Recording> recordings)
        {
            var superseded = new HashSet<string>();
            if (recordings == null) return superseded;
            for (int i = 0; i < recordings.Count; i++)
            {
                string parentId = recordings[i].ParentRecordingId;
                if (!string.IsNullOrEmpty(parentId))
                    superseded.Add(parentId);
            }
            return superseded;
        }

        /// <summary>
        /// Tracking Station visibility suppression is time-aware: a recording is hidden only
        /// after one of its child recordings has actually started by the current UT. This keeps
        /// the current atmospheric continuation visible even when a later future leg already
        /// exists in the committed chain.
        /// </summary>
        internal static HashSet<string> FindTrackingStationSuppressedRecordingIds(
            IReadOnlyList<Recording> recordings, double currentUT)
        {
            var scenario = ParsekScenario.Instance;
            var supersedes = object.ReferenceEquals(null, scenario)
                ? null
                : scenario.RecordingSupersedes;
            var retirements = object.ReferenceEquals(null, scenario)
                ? null
                : scenario.RecordingRewindRetirements;
            return FindTrackingStationSuppressedRecordingIds(recordings, currentUT, supersedes, retirements);
        }

        internal static HashSet<string> FindTrackingStationSuppressedRecordingIds(
            IReadOnlyList<Recording> recordings, double currentUT,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            return FindTrackingStationSuppressedRecordingIds(
                recordings,
                currentUT,
                supersedes,
                retirements: null);
        }

        internal static HashSet<string> FindTrackingStationSuppressedRecordingIds(
            IReadOnlyList<Recording> recordings, double currentUT,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            IReadOnlyList<RecordingRewindRetirement> retirements)
        {
            var suppressed = new HashSet<string>();
            if (recordings == null)
                return suppressed;

            for (int i = 0; i < recordings.Count; i++)
            {
                Recording child = recordings[i];
                string parentId = child?.ParentRecordingId;
                if (string.IsNullOrEmpty(parentId))
                    continue;

                if (HasTrackingStationChildStarted(child, currentUT))
                    suppressed.Add(parentId);
            }

            AddSupersedeRelationSuppressedRecordingIds(suppressed, recordings, supersedes);
            AddRewindRetiredSuppressedRecordingIds(suppressed, recordings, retirements);
            return suppressed;
        }

        private static void AddSupersedeRelationSuppressedRecordingIds(
            HashSet<string> suppressed,
            IReadOnlyList<Recording> recordings,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (suppressed == null || recordings == null || supersedes == null || supersedes.Count == 0)
                return;

            for (int i = 0; i < recordings.Count; i++)
            {
                Recording rec = recordings[i];
                if (!EffectiveState.IsSupersededByRelation(rec, supersedes))
                    continue;
                suppressed.Add(rec.RecordingId);
            }
        }

        private static void AddRewindRetiredSuppressedRecordingIds(
            HashSet<string> suppressed,
            IReadOnlyList<Recording> recordings,
            IReadOnlyList<RecordingRewindRetirement> retirements)
        {
            if (suppressed == null || recordings == null || retirements == null || retirements.Count == 0)
                return;

            // Cascade overload: parent-anchored debris of a retired recording
            // inherits the retirement so the orphan debris ghost does not
            // render at the tracking station alongside the restored parent's
            // own debris.
            var retiredIds = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording rec = recordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!retiredIds.Contains(rec.RecordingId))
                    continue;
                suppressed.Add(rec.RecordingId);
            }
        }

        private static void AddActiveSessionSuppressedRecordingIds(
            HashSet<string> suppressed, IReadOnlyList<Recording> recordings)
        {
            if (suppressed == null || recordings == null)
                return;

            for (int i = 0; i < recordings.Count; i++)
            {
                if (!IsSuppressedByActiveSession(i))
                    continue;

                string recordingId = recordings[i]?.RecordingId;
                if (!string.IsNullOrEmpty(recordingId))
                    suppressed.Add(recordingId);
            }
        }
    }
}
