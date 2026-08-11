using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    internal class JettisonGhostInfo
    {
        public uint partPersistentId;
        public List<Transform> jettisonTransforms;
    }

    internal class ParachuteGhostInfo
    {
        public uint partPersistentId;
        public Transform canopyTransform;
        public Transform capTransform;
        public Vector3 deployedCanopyScale;
        public Vector3 deployedCanopyPos;
        public Quaternion deployedCanopyRot;
        public Vector3 semiDeployedCanopyScale;
        public Vector3 semiDeployedCanopyPos;
        public Quaternion semiDeployedCanopyRot;
        public bool semiDeployedSampled;
        // The canopy's build-time (stowed) pose, captured by TryBuildParachuteInfo AFTER every
        // reparent/override branch has run, so it is the true spawn pose for EVA chutes and for
        // canopies that live outside modelRoot as well as for the ordinary case. Restored by
        // ParachuteRepacked and by the loop-cycle baseline reset. Scale is Vector3.zero for stock
        // prefabs (a stowed canopy mesh is scaled away rather than deactivated), but read it from
        // here rather than hardcoding zero so there is one source of truth.
        public Vector3 stowedCanopyScale;
        public Vector3 stowedCanopyPos;
        public Quaternion stowedCanopyRot;
    }

    internal struct KspEmitterRef
    {
        public MonoBehaviour emitter;
        public System.Reflection.FieldInfo emitField;
    }

    internal class EngineGhostInfo
    {
        public uint partPersistentId;
        public int moduleIndex;
        public List<ParticleSystem> particleSystems = new List<ParticleSystem>();
        public List<KspEmitterRef> kspEmitters = new List<KspEmitterRef>();
        public FloatCurve emissionCurve;
        public FloatCurve speedCurve;
        public float currentPower;
    }

    internal struct DeployableTransformState
    {
        public Transform t;
        public Vector3 stowedPos;
        public Quaternion stowedRot;
        public Vector3 stowedScale;
        public Vector3 deployedPos;
        public Quaternion deployedRot;
        public Vector3 deployedScale;
    }

    internal class DeployableGhostInfo
    {
        public uint partPersistentId;
        public List<DeployableTransformState> transforms;
    }

    internal struct HeatTransformState
    {
        public Transform t;
        public Vector3 coldPos, mediumPos, hotPos;
        public Quaternion coldRot, mediumRot, hotRot;
        public Vector3 coldScale, mediumScale, hotScale;
    }

    internal struct HeatMaterialState
    {
        public Material material;
        public string colorProperty;
        public Color coldColor;
        public Color mediumColor;
        public Color hotColor;
        public string emissiveProperty;
        public Color coldEmission;
        public Color mediumEmission;
        public Color hotEmission;
    }

    internal class HeatGhostInfo
    {
        public uint partPersistentId;
        public List<HeatTransformState> transforms;
        public List<HeatMaterialState> materialStates;
    }

    internal class LightGhostInfo
    {
        public uint partPersistentId;
        public List<Light> lights;
    }

    internal struct ColorChangerMaterialState
    {
        public Material material;
        public Color offColor;
        public Color onColor;
    }

    internal class ColorChangerGhostInfo
    {
        public uint partPersistentId;
        public string shaderProperty;      // "_EmissiveColor" or "_BurnColor"
        public bool isCabinLight;          // true = Pattern A (toggle), false = Pattern B (reentry)
        // Pattern B: highest char fraction reached (permanent, never decreases).
        // NOTE: Rewind past reentry won't reset this value — a reset mechanism
        // will be needed when rewind/scrub support is added.
        public float peakCharIntensity;
        public List<ColorChangerMaterialState> materials;
    }

    internal class RcsGhostInfo
    {
        public uint partPersistentId;
        public int moduleIndex;
        public List<ParticleSystem> particleSystems = new List<ParticleSystem>();
        public List<KspEmitterRef> kspEmitters = new List<KspEmitterRef>();
        public FloatCurve emissionCurve;
        public FloatCurve speedCurve;
        public float emissionScale = 1f;
        public float speedScale = 1f;
        public float currentPower;
    }

    internal enum GhostAudioPriorityClass
    {
        Explosion,
        RocketEngine,
        QuietEngine,
        JetEngine
    }

    internal class AudioGhostInfo
    {
        public uint partPersistentId;
        public int moduleIndex;
        public int selectionOrder; // stable build-order tie-break for runtime playback cap
        public GhostAudioPriorityClass priorityClass;
        public AudioSource audioSource;
        public AudioClip clip;
        public FloatCurve volumeCurve;
        public FloatCurve pitchCurve;
        public float currentPower;
    }

    internal enum RoboticVisualMode
    {
        Rotational,
        Linear,
        RotorRpm,
        // Wheel motor spin: continuous rotation whose rate is DERIVED from the ghost's own
        // horizontal ground speed each frame, not read from a recorded event. Recorded wheel-motor
        // events (old recordings still carry them) are ignored in this mode. See
        // FlightRecorder.IsWheelMotorSpinModuleName and GhostPlaybackLogic.UpdateActiveRobotics.
        WheelGroundSpeed
    }

    internal class RoboticGhostInfo
    {
        public uint partPersistentId;
        public int moduleIndex;
        public string moduleName;
        public Transform servoTransform;
        public Vector3 axisLocal = Vector3.up;
        public Vector3 stowedPos;
        public Quaternion stowedRot;
        public RoboticVisualMode visualMode;
        public float currentValue;
        public bool active;
        public double lastUpdateUT = double.NaN;
        // WheelGroundSpeed mode only: the wheel's rolling radius in metres, read at build time from
        // ModuleWheelBase.radius (a [KSPField]) times the part's rescaleFactor, matching how stock
        // sizes the wheel collider. Falls back to
        // GhostPlaybackLogic.DefaultWheelRadiusMeters when the module or field is unreachable.
        public float wheelRadius;
    }

    internal struct FxModelDefinition
    {
        public string transformName;
        public string modelName;
        public Vector3 localOffset;
        public Quaternion localRotation;
        public Vector3 localScale;
    }

    internal class FairingGhostInfo
    {
        public uint partPersistentId;
        public GameObject fairingMeshObject;
    }

    internal struct FireShellMesh
    {
        public Mesh mesh;
        public Transform transform;
    }

    internal class ReentryFxInfo
    {
        public ParticleSystem fireParticles;
        public Mesh combinedEmissionMesh; // combined ghost meshes for surface emission, needs Destroy
        public Texture2D generatedTexture; // runtime soft-circle, needs Destroy on cleanup
        public List<FireShellMesh> fireShellMeshes; // mesh+transform pairs for DrawMesh flame overlay
        public Material fireShellMaterial; // additive material for flame shell passes
        public List<HeatMaterialState> glowMaterials = new List<HeatMaterialState>();
        public List<Material> allClonedMaterials = new List<Material>();
        public float lastIntensity;
        public float vesselLength;
    }

    internal enum VariantPropertyType { Texture, Color, Float, Skip }

    internal struct VariantTextureRule
    {
        public string materialName;
        public string shaderName;
        public string transformName;
        public List<(string key, string value)> properties;
    }

    internal struct CompoundPartData
    {
        public Vector3 targetPos;
        public Quaternion targetRot;
        public uint targetPersistentId;
        public string lineObjName;
        public string targetAnchorName;
        public string targetCapName;
    }

    internal class CompoundPartGhostInfo
    {
        public uint partPersistentId;
        public uint targetPersistentId;
        public Transform partTransform;
    }

    internal enum PendingSpawnLifecycle : byte
    {
        None = 0,
        StandardEnter = 1,
        LoopEnter = 2,
        OverlapPrimaryEnter = 3
    }

    /// <summary>
    /// Incremental timeline-ghost build state for bug #450 B2. The expensive
    /// part-instantiation loop advances across multiple UpdatePlayback ticks
    /// instead of monopolizing one frame.
    /// </summary>
    internal class PendingGhostVisualBuild
    {
        public string rootName;
        public GameObject root;
        public Transform visualsRoot;
        public ConfigNode snapshotNode;
        public ConfigNode[] partNodes;
        public int nextPartIndex;
        public bool addedAnyVisual;
        public int visualCount;
        public int skippedName;
        public int skippedPrefab;
        public int skippedMesh;
        public bool raiseLightVisualOnly;
        public bool raiseRcsVisualOnly;
        public bool hasLoggedSplitYield;
        public HeaviestSpawnBuildType buildType;
        public List<ParachuteGhostInfo> parachuteInfos = new List<ParachuteGhostInfo>();
        public List<JettisonGhostInfo> jettisonInfos = new List<JettisonGhostInfo>();
        public List<EngineGhostInfo> engineInfos = new List<EngineGhostInfo>();
        public List<DeployableGhostInfo> deployableInfos = new List<DeployableGhostInfo>();
        public List<HeatGhostInfo> heatInfos = new List<HeatGhostInfo>();
        public List<LightGhostInfo> lightInfos = new List<LightGhostInfo>();
        public List<FairingGhostInfo> fairingInfos = new List<FairingGhostInfo>();
        public List<RcsGhostInfo> rcsInfos = new List<RcsGhostInfo>();
        public List<RoboticGhostInfo> roboticInfos = new List<RoboticGhostInfo>();
        public List<ColorChangerGhostInfo> colorChangerInfos = new List<ColorChangerGhostInfo>();
        public List<CompoundPartGhostInfo> compoundPartInfos = new List<CompoundPartGhostInfo>();
        public List<AudioGhostInfo> audioInfos = new List<AudioGhostInfo>();
    }

    /// <summary>
    /// Bundles all output from BuildTimelineGhostFromSnapshot: the root GameObject
    /// plus per-module-type ghost info lists. Replaces the previous 10 out-parameters.
    /// Null list fields mean that module type had no matching parts in the snapshot.
    /// </summary>
    internal class GhostBuildResult
    {
        public GameObject root;
        public List<ParachuteGhostInfo> parachuteInfos;
        public List<JettisonGhostInfo> jettisonInfos;
        public List<EngineGhostInfo> engineInfos;
        public List<DeployableGhostInfo> deployableInfos;
        public List<HeatGhostInfo> heatInfos;
        public List<LightGhostInfo> lightInfos;
        public List<FairingGhostInfo> fairingInfos;
        public List<RcsGhostInfo> rcsInfos;
        public List<RoboticGhostInfo> roboticInfos;
        public List<ColorChangerGhostInfo> colorChangerInfos;
        public List<CompoundPartGhostInfo> compoundPartInfos;
        public List<AudioGhostInfo> audioInfos;
    }
}
