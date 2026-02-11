using System;
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
        public const int CurrentRecordingFormatVersion = 2;
        public const int CurrentGhostGeometryVersion = 1;

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
            public string RecordingId = Guid.NewGuid().ToString("N");
            public int RecordingFormatVersion = CurrentRecordingFormatVersion;
            public int GhostGeometryVersion = CurrentGhostGeometryVersion;
            public List<TrajectoryPoint> Points = new List<TrajectoryPoint>();
            public List<OrbitSegment> OrbitSegments = new List<OrbitSegment>();
            public string VesselName = "";
            public string GhostGeometryRelativePath;
            public bool GhostGeometryAvailable;
            public string GhostGeometryCaptureError;
            public string GhostGeometryCaptureStrategy = "stub_v1";
            public string GhostGeometryProbeStatus = "uninitialized";

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
            public int SpawnAttempts;               // Number of failed spawn attempts (give up after 3)

            public double StartUT => Points.Count > 0 ? Points[0].ut : 0;
            public double EndUT => Points.Count > 0 ? Points[Points.Count - 1].ut : 0;

            /// <summary>
            /// Copies persistence/capture artifacts from a stop-time captured recording.
            /// Intentionally does NOT copy Points/OrbitSegments/VesselName, which are
            /// set by StashPending from the current recorder buffers.
            /// </summary>
            public void ApplyPersistenceArtifactsFrom(Recording source)
            {
                if (source == null) return;

                VesselSnapshot = source.VesselSnapshot;
                RecordingId = source.RecordingId;
                DistanceFromLaunch = source.DistanceFromLaunch;
                VesselDestroyed = source.VesselDestroyed;
                VesselSituation = source.VesselSituation;
                MaxDistanceFromLaunch = source.MaxDistanceFromLaunch;
                GhostGeometryRelativePath = source.GhostGeometryRelativePath;
                GhostGeometryAvailable = source.GhostGeometryAvailable;
                GhostGeometryCaptureError = source.GhostGeometryCaptureError;
                GhostGeometryCaptureStrategy = source.GhostGeometryCaptureStrategy;
                GhostGeometryProbeStatus = source.GhostGeometryProbeStatus;
                RecordingFormatVersion = source.RecordingFormatVersion;
                GhostGeometryVersion = source.GhostGeometryVersion;
            }
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

        public static void StashPending(List<TrajectoryPoint> points, string vesselName,
            List<OrbitSegment> orbitSegments = null,
            string recordingId = null,
            int? recordingFormatVersion = null,
            int? ghostGeometryVersion = null)
        {
            if (points == null || points.Count < 2)
            {
                Log("[Parsek] Recording too short (< 2 points) — discarded");
                return;
            }

            pendingRecording = new Recording
            {
                RecordingId = string.IsNullOrEmpty(recordingId) ? Guid.NewGuid().ToString("N") : recordingId,
                RecordingFormatVersion = recordingFormatVersion ?? CurrentRecordingFormatVersion,
                GhostGeometryVersion = ghostGeometryVersion ?? CurrentGhostGeometryVersion,
                Points = new List<TrajectoryPoint>(points),
                OrbitSegments = orbitSegments != null
                    ? new List<OrbitSegment>(orbitSegments)
                    : new List<OrbitSegment>(),
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

            DeleteGhostGeometryArtifact(pendingRecording);
            Log($"[Parsek] Discarded pending recording from {pendingRecording.VesselName}");
            pendingRecording = null;
        }

        public static void ClearCommitted()
        {
            for (int i = 0; i < committedRecordings.Count; i++)
                DeleteGhostGeometryArtifact(committedRecordings[i]);
            committedRecordings.Clear();
        }

        public static void Clear()
        {
            if (pendingRecording != null)
                DeleteGhostGeometryArtifact(pendingRecording);
            pendingRecording = null;
            ClearCommitted();
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

        internal static bool DeleteGhostGeometryArtifact(Recording rec)
        {
            if (rec == null) return false;
            if (string.IsNullOrEmpty(rec.GhostGeometryRelativePath)) return false;

            try
            {
                string absolutePath = RecordingPaths.ResolveSaveScopedPath(rec.GhostGeometryRelativePath);
                if (string.IsNullOrEmpty(absolutePath)) return false;
                if (!System.IO.File.Exists(absolutePath)) return false;
                System.IO.File.Delete(absolutePath);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
