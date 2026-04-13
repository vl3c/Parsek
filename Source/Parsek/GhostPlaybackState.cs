using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    internal class LightPlaybackState
    {
        public bool isOn;
        public bool blinkEnabled;
        public float blinkRateHz = 1f;
    }

    internal class GhostPlaybackState
    {
        public string vesselName;
        public GameObject ghost;
        public List<Material> materials;
        public int playbackIndex;
        public int partEventIndex;
        public long loopCycleIndex = -1;
        public Dictionary<uint, List<uint>> partTree;
        public HashSet<uint> logicalPartIds;
        public Dictionary<uint, ParachuteGhostInfo> parachuteInfos;
        public Dictionary<uint, JettisonGhostInfo> jettisonInfos;
        public Dictionary<ulong, EngineGhostInfo> engineInfos; // key = EncodeEngineKey(pid, moduleIndex)
        public Dictionary<ulong, RcsGhostInfo> rcsInfos;   // separate from engineInfos — keys can overlap for same part
        public Dictionary<ulong, AudioGhostInfo> audioInfos; // engine/RCS audio — keyed same as engineInfos/rcsInfos
        public OneShotAudioInfo oneShotAudio;                // shared one-shot source for decouple/explosion sounds
        public bool audioMuted;                              // true during high warp or when ghost hidden
        public float atmosphereFactor = 1f;                  // 0 in vacuum, 1 at sea level — updated per frame. Init 1 so first-frame events aren't swallowed.
        public CelestialBody cachedAudioBody;                // cached body for atmosphere lookup (avoid per-frame Find)
        public string cachedAudioBodyName;                   // body name the cache was built for
        public Dictionary<ulong, RoboticGhostInfo> roboticInfos; // key = EncodeEngineKey(pid, moduleIndex)
        public Dictionary<uint, DeployableGhostInfo> deployableInfos;
        public Dictionary<uint, HeatGhostInfo> heatInfos;
        public Dictionary<uint, LightGhostInfo> lightInfos;
        public Dictionary<uint, LightPlaybackState> lightPlaybackStates;
        public Dictionary<uint, List<ColorChangerGhostInfo>> colorChangerInfos;
        public Dictionary<uint, FairingGhostInfo> fairingInfos;
        public List<CompoundPartGhostInfo> compoundPartInfos;
        public Dictionary<uint, GameObject> fakeCanopies;
        public ReentryFxInfo reentryFxInfo;
        public MaterialPropertyBlock reentryMpb; // per-ghost to avoid shared-state bugs with overlapping ghosts
        public bool explosionFired;
        public bool pauseHidden;
        public bool rcsSuppressed;
        public bool fidelityReduced;     // true when ReduceFidelity soft cap disabled some renderers
        public bool distanceLodReduced;  // true when distance-based LOD applied ReduceFidelity
        public List<Renderer> fidelityDisabledRenderers; // renderers disabled by ReduceFidelity (for precise restore)
        public bool simplified;          // true when SimplifyToOrbitLine soft cap hid the ghost mesh
        public Transform cameraPivot; // child of ghost; centroid of active parts — camera targets this
        public Transform horizonProxy; // child of cameraPivot; horizon-aligned rotation for locked camera mode
        public Vector3 lastInterpolatedVelocity;
        public string lastInterpolatedBodyName;
        public double lastInterpolatedAltitude;
        public Vector3 lastValidHorizonForward; // fallback forward direction when velocity near zero
        public RenderingZone currentZone = RenderingZone.Physics; // distance-based rendering zone
        public double lastDistance; // meters from active vessel, updated per frame in ApplyZonePolicy
        public int flagEventIndex;               // tracks which flags have been spawned

        internal void ClearLoadedVisualReferences()
        {
            ghost = null;
            materials = null;
            parachuteInfos = null;
            jettisonInfos = null;
            engineInfos = null;
            rcsInfos = null;
            audioInfos = null;
            oneShotAudio = null;
            audioMuted = false;
            cachedAudioBody = null;
            cachedAudioBodyName = null;
            roboticInfos = null;
            deployableInfos = null;
            heatInfos = null;
            lightInfos = null;
            lightPlaybackStates = null;
            colorChangerInfos = null;
            fairingInfos = null;
            compoundPartInfos = null;
            fakeCanopies = null;
            reentryFxInfo = null;
            reentryMpb = null;
            pauseHidden = false;
            rcsSuppressed = false;
            fidelityReduced = false;
            distanceLodReduced = false;
            fidelityDisabledRenderers = null;
            simplified = false;
            cameraPivot = null;
            horizonProxy = null;
        }

        public void SetInterpolated(InterpolationResult r)
        {
            lastInterpolatedVelocity = r.velocity;
            lastInterpolatedBodyName = r.bodyName;
            lastInterpolatedAltitude = r.altitude;
        }
    }

    internal struct InterpolationResult
    {
        public Vector3 velocity;
        public string bodyName;
        public double altitude;

        public static readonly InterpolationResult Zero = new InterpolationResult
        {
            velocity = Vector3.zero,
            bodyName = null,
            altitude = 0
        };

        public InterpolationResult(Vector3 vel, string body, double alt)
        {
            velocity = vel;
            bodyName = body;
            altitude = alt;
        }
    }
}
