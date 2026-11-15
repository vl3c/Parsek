using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    public partial class ParsekFlight
    {
        /// <summary>
        /// The ghost of one held-back vessel before its first claim (design 12.5): built from
        /// the last Rewind-to-Launch's own copy of the vessel when it has one (the landing world
        /// holds the vessel in its post-claim form), else from the snapshot <c>VesselGhoster</c>
        /// took before the despawn, and posed where that copy says. Resolved once when shown.
        /// </summary>
        internal sealed class ClaimedVesselHoldGhostState
        {
            public uint pid;
            public uint chainPid;
            public string vesselName;
            public ConfigNode snapshot;
            public ConfigNode visual;
            public bool fromRewindSave;
            public GameObject ghost;
            public bool buildFailed;
            public CelestialBody body;
            public Orbit orbit;
            public ClaimedVesselHoldPose pose;
            public Quaternion surfaceRelativeRotation;
            public double shownSinceUT;
            public bool inRange;
        }

        // Design section 2.12 Rule 3: survivors of a Re-Fly load that a committed recording
        // flies or recovers after the rewind UT (ReFlyInvokeHold), held without a ghost chain.
        // Keyed by pid; FirstClaimUT is the first committed use, after which the hold ghost
        // yields to the committed recording.
        internal readonly Dictionary<uint, ChainClaimedVessel> reFlyInvokeHolds =
            new Dictionary<uint, ChainClaimedVessel>();
    }
}
