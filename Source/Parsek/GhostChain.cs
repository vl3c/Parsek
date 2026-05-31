using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal struct ChainLink
    {
        public string recordingId;
        public string treeId;
        public string branchPointId;
        public double ut;
        public string interactionType;
        // Launch-unique Guid of the claimed vessel in this link's tree. Lets the walker keep
        // chain claims for two distinct launches of the same craft (same baked pid) separate
        // instead of pooling them into one chain. Null/empty = unknown (legacy -> pid-only pooling).
        public string recordedVesselGuid;

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "ChainLink rec={0} tree={1} type={2} ut={3:F1}",
                recordingId, treeId, interactionType, ut);
        }
    }

    internal class GhostChain
    {
        public uint OriginalVesselPid;
        // Launch-unique Guid this chain represents (the latest launch present for OriginalVesselPid).
        // Used to keep a relaunch of the same craft from suppressing a prior launch's spawn.
        public string LaunchGuid;
        public List<ChainLink> Links;
        public double GhostStartUT;
        public double SpawnUT;
        public string TipRecordingId;
        public string TipTreeId;
        public bool IsTerminated;

        // Runtime state: cached trajectory index for O(1) amortized lookup (not serialized)
        public int CachedTrajectoryIndex;

        // Runtime state: last orbit segment used for ghost map ProtoVessel (not serialized)
        // Used to detect segment changes and update the ProtoVessel's orbit.
        // null = no segment tracked yet (first frame, or no orbit data)
        public string LastMapOrbitBodyName;
        public double LastMapOrbitSma;
        public double LastMapOrbitEcc;

        // Runtime state: spawn blocked by collision (not serialized)
        public bool SpawnBlocked;
        public double BlockedSinceUT;
        public float BlockedInitialDistance;  // distance to blocker when first blocked
        public bool WalkbackExhausted;        // true after walkback scanned entire trajectory with no valid position

        public GhostChain()
        {
            Links = new List<ChainLink>();
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "GhostChain vessel={0} links={1} tip={2} spawnUT={3:F1} terminated={4}",
                OriginalVesselPid, Links.Count, TipRecordingId, SpawnUT, IsTerminated);
        }
    }
}
