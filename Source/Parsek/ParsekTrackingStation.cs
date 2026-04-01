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
            // on SpaceTracking.Awake) to ensure map icon click handlers are registered.
            // This call is a no-op if they already exist (CreateGhostVesselForRecording skips
            // duplicates), but acts as a safety net if the Harmony patch didn't run.
            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();
            ParsekLog.Info("TrackingStation",
                $"ParsekTrackingStation initialized, created {created} ghost map vessel(s)" +
                (created == 0 ? " (already pre-created by Harmony prefix)" : ""));
        }

        void OnDestroy()
        {
            GhostMapPresence.RemoveAllGhostVessels("tracking-station-cleanup");
            ParsekLog.Info("TrackingStation", "ParsekTrackingStation destroyed");
        }
    }
}
