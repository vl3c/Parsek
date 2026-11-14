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

    /// <summary>
    /// One vessel a chain holds back: the chain's own key, or the key of a chain folded into
    /// it because its tip carries that pid (<c>GhostChainWalker.MergeCrossTreeLinks</c>).
    /// </summary>
    internal struct ChainClaimedVessel
    {
        public uint Pid;
        // The launch guid of the claim on that pid (the folded chain's LaunchGuid); null = unknown.
        public string LaunchGuid;
        // UT of the first claim on that pid (the folded chain's GhostStartUT): before it the
        // held-back vessel sits on rails in its pre-claim form.
        public double FirstClaimUT;
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
        // Every vessel this chain holds back, the key first: a tip that ends several claimed
        // vessels (a tug carries a depot to a station) holds back all of them until the tip,
        // which then spawns once as their recorded merged form.
        public List<ChainClaimedVessel> ClaimedVessels;

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
        // Real time before which the full-trajectory walkback rescan is skipped, set after a
        // walkback found a clear point but the spawn there failed (0 = no backoff).
        public double WalkbackRescanNotBeforeRealTime;
        public bool WalkbackBackoffLogged;    // the backoff decision is logged once per chain

        public GhostChain()
        {
            Links = new List<ChainLink>();
            ClaimedVessels = new List<ChainClaimedVessel>();
        }

        /// <summary>True when <paramref name="pid"/> is one of the vessels this chain holds back.</summary>
        public bool ClaimsPid(uint pid)
        {
            if (pid == 0)
                return false;
            if (pid == OriginalVesselPid)
                return true;
            for (int i = 0; i < ClaimedVessels.Count; i++)
            {
                if (ClaimedVessels[i].Pid == pid)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Adds a held-back vessel unless the chain already claims that pid. Returns true when added.
        /// </summary>
        public bool AddClaimedVessel(uint pid, string launchGuid, double firstClaimUT)
        {
            if (pid == 0)
                return false;
            for (int i = 0; i < ClaimedVessels.Count; i++)
            {
                if (ClaimedVessels[i].Pid == pid)
                    return false;
            }
            ClaimedVessels.Add(new ChainClaimedVessel
            {
                Pid = pid,
                LaunchGuid = launchGuid,
                FirstClaimUT = firstClaimUT
            });
            return true;
        }

        /// <summary>
        /// The vessels this chain holds back, the key first. A chain built without a claim
        /// list (hand-built in tests) holds back its key alone.
        /// </summary>
        public List<ChainClaimedVessel> HeldBackVessels()
        {
            var held = new List<ChainClaimedVessel>();
            if (OriginalVesselPid != 0)
                held.Add(new ChainClaimedVessel
                {
                    Pid = OriginalVesselPid,
                    LaunchGuid = LaunchGuid,
                    FirstClaimUT = GhostStartUT
                });
            for (int i = 0; i < ClaimedVessels.Count; i++)
            {
                if (ClaimedVessels[i].Pid != 0 && ClaimedVessels[i].Pid != OriginalVesselPid)
                    held.Add(ClaimedVessels[i]);
            }
            return held;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "GhostChain vessel={0} links={1} tip={2} spawnUT={3:F1} terminated={4}",
                OriginalVesselPid, Links.Count, TipRecordingId, SpawnUT, IsTerminated);
        }
    }
}
