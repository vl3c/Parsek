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
            int created = GhostMapPresence.CreateGhostVesselsFromCommittedRecordings();
            ParsekLog.Info("TrackingStation",
                $"ParsekTrackingStation initialized, created {created} ghost map vessel(s)");
        }

        void OnDestroy()
        {
            GhostMapPresence.RemoveAllGhostVessels("tracking-station-cleanup");
            ParsekLog.Info("TrackingStation", "ParsekTrackingStation destroyed");
        }
    }
}
