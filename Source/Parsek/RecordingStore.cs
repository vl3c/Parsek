using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Static holder for recording data that survives scene changes.
    /// Static fields persist across scene loads within a KSP session.
    /// Save/load persistence is handled separately by ParsekScenario.
    /// </summary>
    public static class RecordingStore
    {
        public class Recording
        {
            public List<ParsekSpike.TrajectoryPoint> Points = new List<ParsekSpike.TrajectoryPoint>();
            public string VesselName = "";

            // Tracks which point's resource deltas have been applied during playback.
            // -1 means no resources applied yet (start from point 0's delta).
            public int LastAppliedResourceIndex = -1;

            public double StartUT => Points.Count > 0 ? Points[0].ut : 0;
            public double EndUT => Points.Count > 0 ? Points[Points.Count - 1].ut : 0;
        }

        // Just-finished recording awaiting user decision (merge or discard)
        private static Recording pendingRecording;

        // Merged to timeline — these auto-playback during flight
        private static List<Recording> committedRecordings = new List<Recording>();

        public static bool HasPending => pendingRecording != null;
        public static Recording Pending => pendingRecording;
        public static List<Recording> CommittedRecordings => committedRecordings;

        public static void StashPending(List<ParsekSpike.TrajectoryPoint> points, string vesselName)
        {
            if (points == null || points.Count == 0) return;

            pendingRecording = new Recording
            {
                Points = new List<ParsekSpike.TrajectoryPoint>(points),
                VesselName = vesselName
            };

            UnityEngine.Debug.Log($"[Parsek] Stashed pending recording: {points.Count} points from {vesselName}");
        }

        public static void CommitPending()
        {
            if (pendingRecording == null) return;

            committedRecordings.Add(pendingRecording);
            UnityEngine.Debug.Log($"[Parsek] Committed recording from {pendingRecording.VesselName} " +
                $"({pendingRecording.Points.Count} points). Total committed: {committedRecordings.Count}");
            pendingRecording = null;
        }

        public static void DiscardPending()
        {
            if (pendingRecording == null) return;

            UnityEngine.Debug.Log($"[Parsek] Discarded pending recording from {pendingRecording.VesselName}");
            pendingRecording = null;
        }

        public static void Clear()
        {
            pendingRecording = null;
            committedRecordings.Clear();
            UnityEngine.Debug.Log("[Parsek] All recordings cleared");
        }
    }
}
