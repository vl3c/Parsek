using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Tracking station scene host for ghost map presence.
    /// Creates ghost ProtoVessels from committed recordings so ghosts appear
    /// in the tracking station vessel list with orbit lines and targeting.
    /// Per-frame lifecycle: removes/creates ghosts when UT crosses segment bounds.
    /// OnGUI draws icons for atmospheric phases (no ProtoVessel — direct rendering
    /// from trajectory data, same approach as ParsekUI.DrawMapMarkers in flight).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public class ParsekTrackingStation : MonoBehaviour
    {
        private const string Tag = "TrackingStation";
        private const float LifecycleCheckIntervalSec = 2.0f;
        private float nextLifecycleCheckTime;

        /// <summary>Cached interpolation indices for atmospheric ghost icon rendering (per recording index).</summary>
        private readonly Dictionary<int, int> atmosCachedIndices = new Dictionary<int, int>();

        /// <summary>Superseded recording IDs, rebuilt each lifecycle tick.</summary>
        private HashSet<string> cachedSuperseded;

        void Start()
        {
            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();
            int renderersFixed = GhostMapPresence.EnsureGhostOrbitRenderers();

            nextLifecycleCheckTime = Time.time + LifecycleCheckIntervalSec;

            ParsekLog.Info(Tag,
                $"ParsekTrackingStation initialized: created {created} ghost vessel(s), " +
                $"fixed {renderersFixed} orbit renderer(s)");
        }

        void Update()
        {
            if (Time.time < nextLifecycleCheckTime) return;
            nextLifecycleCheckTime = Time.time + LifecycleCheckIntervalSec;

            GhostMapPresence.UpdateTrackingStationGhostLifecycle();

            // Rebuild superseded set for OnGUI (lightweight — just scans ParentRecordingId)
            var committed = RecordingStore.CommittedRecordings;
            cachedSuperseded = committed != null
                ? GhostMapPresence.FindSupersededRecordingIds(committed)
                : null;
        }

        void OnGUI()
        {
            // Draw icons for recordings in atmospheric phases (no ProtoVessel).
            // Position comes directly from trajectory point interpolation —
            // same approach as ParsekUI.DrawMapMarkers in the flight scene.
            if (Event.current.type != EventType.Repaint) return;
            if (PlanetariumCamera.Camera == null) return;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0) return;

            double currentUT = Planetarium.GetUniversalTime();

            for (int i = 0; i < committed.Count; i++)
            {
                // Skip if a ProtoVessel ghost already handles this recording
                if (GhostMapPresence.HasGhostVesselForRecording(i)) continue;

                var rec = committed[i];
                if (rec.IsDebris) continue;
                if (rec.Points == null || rec.Points.Count == 0) continue;
                if (currentUT < rec.Points[0].ut || currentUT > rec.Points[rec.Points.Count - 1].ut) continue;

                // Skip superseded recordings (intermediate chain segments)
                if (cachedSuperseded != null && cachedSuperseded.Contains(rec.RecordingId)) continue;

                // Skip non-orbital terminal states
                var terminal = rec.TerminalStateValue;
                if (terminal.HasValue
                    && terminal.Value != TerminalState.Orbiting
                    && terminal.Value != TerminalState.Docked)
                    continue;

                // Skip if currently in an orbit segment (ProtoVessel handles that)
                if (rec.OrbitSegments != null
                    && TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, currentUT).HasValue)
                    continue;

                // Interpolate trajectory position at current UT
                if (!atmosCachedIndices.ContainsKey(i))
                    atmosCachedIndices[i] = -1;
                int cached = atmosCachedIndices[i];
                TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(rec.Points, currentUT, ref cached);
                atmosCachedIndices[i] = cached;

                if (!pt.HasValue) continue;

                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == pt.Value.bodyName);
                if (body == null) continue;

                Vector3d worldPos = body.GetWorldSurfacePosition(
                    pt.Value.latitude, pt.Value.longitude, pt.Value.altitude);

                VesselType vtype = GhostMapPresence.ResolveVesselType(rec.VesselSnapshot);
                MapMarkerRenderer.DrawMarker(worldPos, rec.VesselName ?? "(unknown)", Color.green, vtype);
            }
        }

        void OnDestroy()
        {
            GhostMapPresence.RemoveAllGhostVessels("tracking-station-cleanup");
            ParsekLog.Info(Tag, "ParsekTrackingStation destroyed");
        }
    }
}
