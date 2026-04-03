using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Tracking station scene host for ghost map presence.
    /// Creates ghost ProtoVessels from committed recordings so ghosts appear
    /// in the tracking station vessel list with orbit lines and targeting.
    /// Cleaned up on scene exit.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public class ParsekTrackingStation : MonoBehaviour
    {
        void Start()
        {
            // Ghost vessels are pre-created in GhostTrackingStationInitPatch (Harmony prefix
            // on SpaceTracking.Awake). This call is a safety net if Harmony didn't run.
            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();

            // Ensure orbit renderers exist — MapView.fetch may have been null during Awake
            // prefix, causing AddOrbitRenderer to bail. By Start, all Awakes are complete. (#195)
            int fixed2 = GhostMapPresence.EnsureGhostOrbitRenderers();

            ParsekLog.Info("TrackingStation",
                $"ParsekTrackingStation initialized: created {created} ghost vessel(s), " +
                $"fixed {fixed2} orbit renderer(s)");
        }

        void OnDestroy()
        {
            GhostMapPresence.RemoveAllGhostVessels("tracking-station-cleanup");
            ParsekLog.Info("TrackingStation", "ParsekTrackingStation destroyed");
        }
    }
}
