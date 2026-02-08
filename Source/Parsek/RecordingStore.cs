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
        // When true, suppresses Debug.Log calls (for unit testing outside Unity)
        internal static bool SuppressLogging;

        static void Log(string message)
        {
            if (!SuppressLogging)
                UnityEngine.Debug.Log(message);
        }

        /// <summary>
        /// Recommended merge action based on vessel state after recording.
        /// </summary>
        public enum MergeDefault
        {
            Recover,    // Vessel barely moved — recover for funds
            MergeOnly,  // Vessel destroyed or snapshot missing — merge recording only
            Persist     // Vessel intact and moved — respawn where it ended up
        }

        public class Recording
        {
            public List<ParsekSpike.TrajectoryPoint> Points = new List<ParsekSpike.TrajectoryPoint>();
            public List<ParsekSpike.OrbitSegment> OrbitSegments = new List<ParsekSpike.OrbitSegment>();
            public string VesselName = "";

            // Tracks which point's resource deltas have been applied during playback.
            // -1 means no resources applied yet (start from point 0's delta).
            public int LastAppliedResourceIndex = -1;

            // Vessel persistence fields (transient — only needed between revert and merge dialog)
            public ConfigNode VesselSnapshot;       // ProtoVessel as ConfigNode (null if destroyed)
            public double DistanceFromLaunch;       // Meters from launch position
            public bool VesselDestroyed;            // Vessel was destroyed before revert
            public string VesselSituation;          // "Orbiting Kerbin", "Landed on Mun", etc.
            public double MaxDistanceFromLaunch;     // Peak distance reached during recording
            public bool VesselSpawned;              // True after deferred RespawnVessel has fired
            public uint SpawnedVesselPersistentId;  // persistentId of spawned vessel (0 = not yet spawned)

            public double StartUT => Points.Count > 0 ? Points[0].ut : 0;
            public double EndUT => Points.Count > 0 ? Points[Points.Count - 1].ut : 0;
        }

        /// <summary>
        /// Determines the recommended merge action based on vessel state.
        /// </summary>
        public static MergeDefault GetRecommendedAction(
            double distance, bool destroyed, bool hasSnapshot,
            double duration = 0, double maxDistance = 0)
        {
            if (destroyed || !hasSnapshot)
            {
                if (distance < 100.0)
                    return MergeDefault.Recover;
                return MergeDefault.MergeOnly;
            }

            // Vessel intact with snapshot — did it actually go somewhere?
            if (distance < 100.0 && (duration <= 10.0 || maxDistance <= 100.0))
                return MergeDefault.Recover;

            return MergeDefault.Persist;
        }

        // Just-finished recording awaiting user decision (merge or discard)
        private static Recording pendingRecording;

        // Merged to timeline — these auto-playback during flight
        private static List<Recording> committedRecordings = new List<Recording>();

        public static bool HasPending => pendingRecording != null;
        public static Recording Pending => pendingRecording;
        public static List<Recording> CommittedRecordings => committedRecordings;

        public static void StashPending(List<ParsekSpike.TrajectoryPoint> points, string vesselName,
            List<ParsekSpike.OrbitSegment> orbitSegments = null)
        {
            if (points == null || points.Count < 2) return;

            pendingRecording = new Recording
            {
                Points = new List<ParsekSpike.TrajectoryPoint>(points),
                OrbitSegments = orbitSegments != null
                    ? new List<ParsekSpike.OrbitSegment>(orbitSegments)
                    : new List<ParsekSpike.OrbitSegment>(),
                VesselName = vesselName
            };

            Log($"[Parsek] Stashed pending recording: {points.Count} points, " +
                $"{pendingRecording.OrbitSegments.Count} orbit segments from {vesselName}");
        }

        public static void CommitPending()
        {
            if (pendingRecording == null) return;

            committedRecordings.Add(pendingRecording);
            Log($"[Parsek] Committed recording from {pendingRecording.VesselName} " +
                $"({pendingRecording.Points.Count} points). Total committed: {committedRecordings.Count}");
            pendingRecording = null;
        }

        public static void DiscardPending()
        {
            if (pendingRecording == null) return;

            Log($"[Parsek] Discarded pending recording from {pendingRecording.VesselName}");
            pendingRecording = null;
        }

        public static void Clear()
        {
            pendingRecording = null;
            committedRecordings.Clear();
            Log("[Parsek] All recordings cleared");
        }

        /// <summary>
        /// Resets state without Unity logging. For unit tests only.
        /// </summary>
        internal static void ResetForTesting()
        {
            pendingRecording = null;
            committedRecordings.Clear();
        }
    }
}
