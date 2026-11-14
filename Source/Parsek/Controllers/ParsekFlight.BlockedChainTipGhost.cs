using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // [ERS-exempt] Reads RecordingStore.CommittedRecordings by index only to pair a
    // policy-held slot with its engine ghost (the engine's index-keyed contract). A
    // superseded or rewind-retired tip is released by the policy's own hold checks.
    public partial class ParsekFlight
    {
        /// <summary>
        /// One spawn-blocked chain tip whose playback ghost the policy is holding past its end
        /// UT (phase 6b-4, design 12.9.2 / 13.5). Resolved once when the hold is first seen, so
        /// the per-frame positioning reads cached fields and allocates nothing.
        /// </summary>
        internal sealed class BlockedChainTipGhostState
        {
            public GhostChain chain;
            public uint chainPid;
            public int index;
            public string recordingId;
            public string vesselName;
            public GameObject ghost;
            public BlockedChainTipPoseSource source;
            public CelestialBody body;
            public Orbit orbit;
            public double holdLat;
            public double holdLon;
            public double holdAlt;
            public Quaternion surfaceRelativeRotation;
            public double heldSinceUT;
            public float lastPositionLogRealTime;
        }

        /// <summary>
        /// True when <paramref name="rec"/> is the tip of an active ghost chain whose spawn is
        /// collision-blocked. The policy exempts such a held ghost from the hold timeout.
        /// </summary>
        internal bool IsSpawnBlockedChainTipFromPolicy(Recording rec)
        {
            if (rec == null)
                return false;
            GhostChain chain = FindChainTipForRecording(activeGhostChains, rec);
            return chain != null && chain.SpawnBlocked;
        }
    }
}
