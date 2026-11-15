using System.Collections.Generic;

namespace Parsek
{
    internal static partial class GhostMapPresence
    {
        /// <summary>
        /// Pure: is every map / Tracking-Station render surface of this recording hidden by
        /// the player's per-recording playback tick box (Recordings tab, column 0)?
        ///
        /// <para>GUI-P1 (operator ruling 2026-09-14): unticked means "no ghost appears
        /// ANYWHERE" - the flight-scene ghost (already honoured by
        /// <c>GhostPlaybackEngine</c> / <c>ParsekKSC.ShouldShowInKSC</c>), the map icon, the
        /// orbit line, the Tracking Station row, the non-proto atmospheric marker and the
        /// trajectory polyline. Before this gate existed, `GhostMapPresence*` and
        /// `ParsekTrackingStation` never read the flag, so an unticked recording kept three
        /// of its four surfaces.</para>
        ///
        /// <para>This is a VISIBILITY predicate only. The recording's career effect is
        /// deliberately untouched: the Space Center terminal-vessel spawn still fires for a
        /// hidden recording (Bug #433, <c>ParsekKSC.cs</c>'s visibility-only reject) and
        /// <c>GhostPlaybackLogic.ShouldFireHiddenPastEndCompletion</c> still completes it. Do
        /// NOT fold this predicate into the <c>suppressedIds</c> set that
        /// <see cref="FindTrackingStationSuppressedRecordingIds(IReadOnlyList{Recording},double)"/>
        /// builds: that set also gates the Tracking Station spawn handoff, so folding it in
        /// would silently suppress the career effect.</para>
        /// </summary>
        internal static bool IsMapPresenceHiddenByPlaybackToggle(IPlaybackTrajectory traj)
        {
            return traj != null && !traj.PlaybackEnabled;
        }

        /// <summary>
        /// One log line per (surface, recording) whenever the playback-toggle suppression
        /// STARTS applying, not once per frame: every producer below runs on a per-frame or
        /// per-lifecycle-tick poll. Restoration is witnessed by the producer's own existing
        /// create lines (<c>create-dispatch</c> / "Deferred ghost creation for #N"), which
        /// reappear on the very next tick after the box is re-ticked.
        /// </summary>
        internal static void LogMapPresencePlaybackSuppressedOnChange(
            string surface, string recordingId, string vesselName)
        {
            ParsekLog.VerboseOnChange(
                Tag,
                "map-presence-playback-disabled-" + (surface ?? "(none)")
                    + "-" + (recordingId ?? "(null)"),
                "hidden",
                "Map presence hidden by the playback tick box: surface=" + (surface ?? "(none)")
                    + " rec=" + (recordingId ?? "(null)")
                    + " vessel=\"" + (vesselName ?? "(null)") + "\""
                    + " reason=" + TrackingStationGhostSkipPlaybackDisabled);
        }

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
