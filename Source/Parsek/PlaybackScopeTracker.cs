using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Decides which committed recordings are in legitimate "replay scope" versus
    /// purely historical, so the ghost-playback machinery stays dormant during a
    /// normal forward playthrough (BUG-B).
    ///
    /// Parsek's design (README "How It Works") replays a committed recording — and
    /// spawns its terminal vessel — only after the player REWINDS to before that
    /// recording, so its UT window lies in the player's forward path again. During
    /// normal forward play KSP's universal time only ever advances, so every
    /// committed recording's window is already historical the moment it is committed.
    /// Without this gate the engine treats "live UT is past the recorded end" as
    /// "playback finished" and spawns each recording's terminal vessel, and renders a
    /// ghost for any recording whose orbital-extrapolated window still straddles the
    /// live UT — even though the player never replayed anything.
    ///
    /// A recording enters replay scope the first time the live playhead is observed
    /// at or before its activation-start UT (the post-rewind case; a rewind drops the
    /// playhead onto the target recording's launch). A recording NOT in scope, whose
    /// window the playhead has only ever moved forward through/past, is "historical"
    /// and must produce no ghost, no map icon, and no spawned vessel.
    ///
    /// The discriminant keys on the recording's activation START (its real launch),
    /// not its end: an orbital recording's recorded/extrapolated END can run ahead of
    /// the live UT, but its START always lies behind the playhead during forward play.
    ///
    /// Keyed by recordingId (a stable per-recording GUID), so the latch is shared
    /// across the flight, Space Center, and Tracking Station scenes and cannot collide
    /// across saves. Cleared on return to the main menu (game unload).
    /// </summary>
    internal static class PlaybackScopeTracker
    {
        private static readonly HashSet<string> recordingsInReplayScope = new HashSet<string>();

        /// <summary>
        /// A rewind lands the playhead on (or a few physics frames past) the target
        /// recording's launch UT, which equals that recording's activation-start to
        /// within timing jitter. Any real, spawnable mission recording is far longer
        /// than this, so a normal forward-play commit (currentUT already well past the
        /// recording's start) never falls inside the tolerance window.
        /// </summary>
        internal const double ActivationToleranceSeconds = 2.0;

        /// <summary>
        /// Records the live playhead position relative to a recording's activation
        /// start. If the playhead is at or before that start (within tolerance), the
        /// recording's window lies in the player's forward path — mark it in scope.
        /// Cheap; safe to call every frame for every committed recording.
        /// </summary>
        internal static void NotePlayhead(string recordingId, double currentUT, double activationStartUT)
        {
            if (string.IsNullOrEmpty(recordingId)) return;
            if (currentUT <= activationStartUT + ActivationToleranceSeconds)
                recordingsInReplayScope.Add(recordingId);
        }

        /// <summary>
        /// True iff the recording is historical: the playhead is past its activation
        /// start (outside tolerance) and the recording was never observed in replay
        /// scope. Such a recording must stay dormant during normal forward play.
        /// Returns false for a null/empty id (caller cannot identify the recording, so
        /// fall back to the pre-existing behaviour rather than suppress blindly).
        /// </summary>
        internal static bool IsHistoricalNeverReplayed(
            string recordingId, double currentUT, double activationStartUT)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            if (currentUT <= activationStartUT + ActivationToleranceSeconds) return false;
            return !recordingsInReplayScope.Contains(recordingId);
        }

        /// <summary>
        /// Notes the playhead for every recording in <paramref name="recordings"/> (the
        /// per-frame sweep FLIGHT and the Space Center run inline over the committed list, and
        /// the Tracking Station runs through this). Returns how many were newly latched. A
        /// recording whose activation start is already behind the playhead (outside tolerance)
        /// is never latched here, so a genuinely historical recording stays historical no
        /// matter which scene sweeps it. A clock that is not ready (UT not positive, or NaN:
        /// a scene that has not loaded the save's time yet) sweeps nothing, because UT 0 would
        /// sit before every recording and latch the whole history.
        /// </summary>
        internal static int NotePlayheadSweep(IReadOnlyList<Recording> recordings, double currentUT)
        {
            if (!IsSweepClockReady(currentUT))
                return 0;
            int newlyLatched = 0;
            int count = recordings != null ? recordings.Count : 0;
            for (int i = 0; i < count; i++)
            {
                Recording rec = recordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (recordingsInReplayScope.Contains(rec.RecordingId))
                    continue;
                NotePlayhead(rec.RecordingId, currentUT,
                    PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(rec));
                if (recordingsInReplayScope.Contains(rec.RecordingId))
                    newlyLatched++;
            }
            return newlyLatched;
        }

        /// <summary>True when <paramref name="currentUT"/> is a loaded game clock a sweep may trust.</summary>
        internal static bool IsSweepClockReady(double currentUT)
        {
            return !double.IsNaN(currentUT) && !double.IsInfinity(currentUT) && currentUT > 0.0;
        }

        /// <summary>True iff the recording has been latched into replay scope.</summary>
        internal static bool IsInReplayScope(string recordingId)
        {
            return !string.IsNullOrEmpty(recordingId)
                && recordingsInReplayScope.Contains(recordingId);
        }

        /// <summary>Clears all scope state. Call on return to the main menu / game unload.</summary>
        internal static void Reset()
        {
            recordingsInReplayScope.Clear();
        }

        internal static void ResetForTesting()
        {
            recordingsInReplayScope.Clear();
        }
    }
}
