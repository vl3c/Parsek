using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Tracking station scene host for ghost map presence.
    /// Creates ghost ProtoVessels from committed recordings so ghosts appear
    /// in the tracking station vessel list with orbit lines and targeting.
    /// Per-frame lifecycle: removes ghosts when UT passes their orbit segment bounds.
    /// Cleaned up on scene exit.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public class ParsekTrackingStation : MonoBehaviour
    {
        private const string Tag = "TrackingStation";
        private const float LifecycleCheckIntervalSec = 2.0f;
        private float nextLifecycleCheckTime;

        void Start()
        {
            // Ghost vessels are pre-created in GhostTrackingStationInitPatch (Harmony prefix
            // on SpaceTracking.Awake). This call is a safety net if Harmony didn't run.
            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();

            // Ensure orbit renderers exist — MapView.fetch may have been null during Awake
            // prefix, causing AddOrbitRenderer to bail. By Start, all Awakes are complete. (#195)
            // Intentionally redundant with the buildVesselsList Prefix (which covers all
            // callers); this catches the edge case where buildVesselsList isn't called.
            int renderersFixed = GhostMapPresence.EnsureGhostOrbitRenderers();

            nextLifecycleCheckTime = Time.time + LifecycleCheckIntervalSec;

            ParsekLog.Info(Tag,
                $"ParsekTrackingStation initialized: created {created} ghost vessel(s), " +
                $"fixed {renderersFixed} orbit renderer(s)");
        }

        void Update()
        {
            // State-vector ghost orbit updates run every frame for accurate positioning.
            // Keplerian propagation diverges immediately for atmospheric trajectories.
            GhostMapPresence.UpdateStateVectorGhostOrbits();

            // Creation/removal lifecycle is rate-limited (heavier: scans all recordings)
            if (Time.time < nextLifecycleCheckTime) return;
            nextLifecycleCheckTime = Time.time + LifecycleCheckIntervalSec;

            GhostMapPresence.UpdateTrackingStationGhostLifecycle();
        }

        void OnDestroy()
        {
            GhostMapPresence.RemoveAllGhostVessels("tracking-station-cleanup");
            ParsekLog.Info(Tag, "ParsekTrackingStation destroyed");
        }
    }
}
