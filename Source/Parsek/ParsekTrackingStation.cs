using System.Collections.Generic;
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

        /// <summary>Tracks the last known committed recording count for live-update detection.</summary>
        private int lastKnownCommittedCount;

        /// <summary>
        /// Tracks the last-known value of <c>ParsekSettings.Current.showGhostsInTrackingStation</c>.
        /// When the flag flips we force a lifecycle tick so ghosts appear/disappear
        /// immediately without waiting for the 2-second interval.
        /// </summary>
        private bool lastKnownShowGhosts = true;

        void Start()
        {
            // Read through the persistence store so the startup tick uses the
            // recorded user preference even when ParsekSettings.Current isn't
            // resolved yet (early-scene-load case, see ParsekScenario.cs:546).
            lastKnownShowGhosts = ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation();
            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();
            int renderersFixed = GhostMapPresence.EnsureGhostOrbitRenderers();

            nextLifecycleCheckTime = Time.time + LifecycleCheckIntervalSec;
            atmosCachedIndices.Clear();
            lastKnownCommittedCount = RecordingStore.CommittedRecordings?.Count ?? 0;

            ParsekLog.Info(Tag,
                $"ParsekTrackingStation initialized: created {created} ghost vessel(s), " +
                $"fixed {renderersFixed} orbit renderer(s), " +
                $"showGhostsInTrackingStation={lastKnownShowGhosts}");
        }

        void Update()
        {
            // Detect live recording commits (merge dialog, approval dialog) and force
            // an immediate lifecycle tick so proto-vessel ghosts appear without waiting
            // for the normal 2-second interval.
            // NOTE: count-based detection has a blind spot if a recording is removed and
            // another added in the same frame (net zero change). This can't happen in TS
            // today — removals only occur via clear-all which resets the entire session.
            int currentCount = RecordingStore.CommittedRecordings?.Count ?? 0;
            if (currentCount != lastKnownCommittedCount)
            {
                ParsekLog.Info(Tag,
                    $"Committed recording count changed ({lastKnownCommittedCount} → {currentCount}) " +
                    "— forcing immediate lifecycle tick");
                lastKnownCommittedCount = currentCount;
                nextLifecycleCheckTime = 0f; // force tick this frame
            }

            // #388: detect the ghost visibility flag flipping and react immediately.
            // On off-flip, remove every ghost ProtoVessel so the vessel list empties
            // without waiting for a committed-count change. On on-flip, force a tick
            // so the Phase-2 loop in UpdateTrackingStationGhostLifecycle recreates
            // ghosts for every eligible recording.
            bool currentShowGhosts = ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation();
            if (currentShowGhosts != lastKnownShowGhosts)
            {
                ParsekLog.Info(Tag,
                    $"showGhostsInTrackingStation flipped {lastKnownShowGhosts} → {currentShowGhosts} " +
                    "— forcing immediate lifecycle tick");
                lastKnownShowGhosts = currentShowGhosts;
                if (!currentShowGhosts)
                    GhostMapPresence.RemoveAllGhostVessels("ghost-filter-disabled");
                nextLifecycleCheckTime = 0f;
            }

            if (Time.time < nextLifecycleCheckTime) return;
            nextLifecycleCheckTime = Time.time + LifecycleCheckIntervalSec;

            GhostMapPresence.UpdateTrackingStationGhostLifecycle();
        }

        void OnGUI()
        {
            // Draw icons for recordings in atmospheric phases (no ProtoVessel).
            // Position comes directly from trajectory point interpolation —
            // same approach as ParsekUI.DrawMapMarkers in the flight scene.
            if (Event.current.type != EventType.Repaint) return;
            if (PlanetariumCamera.Camera == null) return;

            // #388: skip the whole atmospheric-marker pass when the user has
            // hidden ghosts in the tracking station.
            if (!ParsekSettingsPersistence.EffectiveShowGhostsInTrackingStation()) return;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || committed.Count == 0) return;

            double currentUT = Planetarium.GetUniversalTime();

            var superseded = GhostMapPresence.CachedSupersededIds;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (!ShouldDrawAtmosphericMarker(rec, i, currentUT, superseded)) continue;

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

                VesselType vtype = ResolveVesselTypeWithFallback(committed, rec);
                Color markerColor = MapMarkerRenderer.GetColorForType(vtype);
                MapMarkerRenderer.DrawMarker(worldPos, rec.VesselName ?? "(unknown)", markerColor, vtype);

                ParsekLog.VerboseRateLimited(Tag, $"atmosMarker-{i}",
                    $"Drawing atmospheric marker #{i} \"{rec.VesselName}\" " +
                    $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                    $"lat={pt.Value.latitude:F2} lon={pt.Value.longitude:F2} alt={pt.Value.altitude:F0}");
            }
        }

        /// <summary>
        /// Resolve VesselType for a recording. If the recording has no VesselSnapshot,
        /// searches other recordings of the same vessel (by VesselPersistentId) for a snapshot.
        /// Ensures consistent icon type across chain recordings of the same vessel.
        /// O(n) scan per call — acceptable for small committed recording counts (typically under 30).
        /// </summary>
        private static VesselType ResolveVesselTypeWithFallback(IReadOnlyList<Recording> committed, Recording rec)
        {
            if (rec.VesselSnapshot != null)
                return GhostMapPresence.ResolveVesselType(rec.VesselSnapshot);

            // No snapshot — search for a sibling recording of the same vessel
            uint vpid = rec.VesselPersistentId;
            if (vpid != 0)
            {
                for (int j = 0; j < committed.Count; j++)
                {
                    if (committed[j].VesselPersistentId == vpid && committed[j].VesselSnapshot != null)
                        return GhostMapPresence.ResolveVesselType(committed[j].VesselSnapshot);
                }
            }

            return VesselType.Ship;
        }

        /// <summary>
        /// Pure: should an atmospheric trajectory marker be drawn for this recording?
        /// Returns true if the recording is eligible for trajectory-interpolated icon rendering
        /// (no ProtoVessel ghost, has trajectory data at currentUT, not superseded, not in orbit segment).
        /// Deliberately does NOT filter by terminal state — atmospheric markers show the ghost's
        /// flight path during its time window regardless of how the recording ended.
        /// </summary>
        internal static bool ShouldDrawAtmosphericMarker(
            Recording rec, int recordingIndex, double currentUT,
            HashSet<string> supersededIds)
        {
            // A ProtoVessel exists but its icon may be suppressed (below atmosphere).
            // When suppressed, the atmospheric marker should still draw.
            if (GhostMapPresence.HasGhostVesselForRecording(recordingIndex))
            {
                uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(recordingIndex);
                if (ghostPid == 0 || !GhostMapPresence.IsIconSuppressed(ghostPid))
                    return false;
            }
            if (rec == null) return false;
            if (rec.IsDebris) return false;
            if (rec.Points == null || rec.Points.Count == 0) return false;
            if (currentUT < rec.Points[0].ut || currentUT > rec.Points[rec.Points.Count - 1].ut) return false;
            if (supersededIds != null && supersededIds.Contains(rec.RecordingId)) return false;
            if (rec.HasOrbitSegments
                && TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, currentUT).HasValue)
                return false;
            return true;
        }

        void OnDestroy()
        {
            GhostMapPresence.RemoveAllGhostVessels("tracking-station-cleanup");
            ParsekLog.Info(Tag, "ParsekTrackingStation destroyed");
        }
    }
}
