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
        // S1 (plume magnitude): the emitter's own particle-rate / velocity fields at BUILD time,
        // captured once by GhostVisualBuilder.CaptureFxMagnitudeBaselines AFTER every build-time
        // mutation has run (the #383 size boost, the world-space velocity floor, the per-part
        // overrides). Runtime throttle scaling writes `baseline * ratio` into these same fields,
        // so it composes with those fixes instead of fighting them and never drifts across
        // repeated events. FieldInfos are resolved once at capture: KSPParticleEmitter is
        // compile-time reachable but these particular fields are read reflectively so a KSP
        // version that renames one degrades to "no scaling" rather than failing the build.
        // magnitudeBaselineCaptured == false means every scaling write is skipped for this
        // emitter, which is exactly the pre-S1 boolean behaviour.
        public System.Reflection.FieldInfo minEmissionField;
        public System.Reflection.FieldInfo maxEmissionField;
        public System.Reflection.FieldInfo localVelocityField;
        public float baselineMinEmission;
        public float baselineMaxEmission;
        public Vector3 baselineLocalVelocity;
        public bool magnitudeBaselineCaptured;
    }

    /// <summary>
    /// S1 per-<see cref="ParticleSystem"/> magnitude baseline, captured in the same pass as
    /// <see cref="KspEmitterRef"/>'s and stored INDEX-PARALLEL to
    /// <c>EngineGhostInfo.particleSystems</c> / <c>RcsGhostInfo.particleSystems</c>. Both lists are
    /// appended to only during the build and only through the same two sinks, so the indices stay
    /// aligned for the ghost's whole lifetime; the applier still bounds-checks.
    /// </summary>
    internal struct GhostFxMagnitudeBaseline
    {
        public float startSpeedMultiplier;
        public float startSizeMultiplier;
        public bool captured;
    }

    internal class EngineGhostInfo
    {
        public uint partPersistentId;
        public int moduleIndex;
        public List<ParticleSystem> particleSystems = new List<ParticleSystem>();
        public List<KspEmitterRef> kspEmitters = new List<KspEmitterRef>();
        /// <summary>S1 baselines, index-parallel to <see cref="particleSystems"/>. Empty until captured.</summary>
        public List<GhostFxMagnitudeBaseline> particleBaselines = new List<GhostFxMagnitudeBaseline>();
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
        // S2 (deployable interpolation). The prefab animation clip's own length, read at build time
        // by GhostVisualBuilder.SampleAnimationStates and clamped by
        // GhostPlaybackLogic.ClampDeployableClipSeconds. Unreadable clip -> the 3 s default, which
        // still animates rather than snapping.
        public float clipLengthSeconds = 3f;
        /// <summary>Where the transforms are RIGHT NOW along stowed(0) -&gt; deployed(1).</summary>
        public float deployFraction;
        /// <summary>True once the family has reached / is heading to the deployed end. The sun-tracking gate reads this.</summary>
        public bool currentDeployed;
        /// <summary>True while an interpolated stow&lt;-&gt;deploy transition is in flight.</summary>
        public bool transitionActive;
        /// <summary>The RECORDED EVENT UT the in-flight transition started at — never wall time.</summary>
        public double transitionStartUT;
        /// <summary>The fraction the in-flight transition started FROM. Non-0/1 after a mid-clip reversal.</summary>
        public float transitionStartFraction;
        /// <summary>The fraction the in-flight transition is heading to (0 or 1).</summary>
        public float transitionTargetFraction;
    }

    /// <summary>
    /// S3 gimbal synthesis: one engine gimbal ring driven from the ghost's own applied world-rotation
    /// derivative. Neutral is the ghost transform's build-time localRotation, so a part whose prefab
    /// ships a pre-canted gimbal deflects around ITS pose, not around identity.
    /// </summary>
    internal class GimbalGhostInfo
    {
        public uint partPersistentId;
        public List<Transform> gimbalTransforms = new List<Transform>();
        public List<Quaternion> neutralRotations = new List<Quaternion>();
        public float gimbalRangeDegrees;
        /// <summary>Smoothed applied deflection, degrees, about the two gimbal axes. Eased, not snapped.</summary>
        public Vector2 currentDeflection;
    }

    /// <summary>
    /// S3 control-surface synthesis: an aero surface deflected from the same attitude derivative,
    /// gated on atmosphere. Suppressed while the part's deployable pose is DEPLOYED (an extended
    /// airbrake is a brake pose, and S2 owns that transform then — risk 4's explicit precedence).
    /// </summary>
    internal class ControlSurfaceGhostInfo
    {
        public uint partPersistentId;
        public List<Transform> surfaceTransforms = new List<Transform>();
        public List<Quaternion> neutralRotations = new List<Quaternion>();
        public float rangeDegrees;
        public bool ignorePitch;
        public bool ignoreYaw;
        public bool ignoreRoll;
        public float currentDeflection;
    }

    /// <summary>
    /// S3 sun tracking: a deployed tracking solar panel / antenna pivot slewed toward the Sun.
    /// Only ever driven while the owning part's deployable pose is DEPLOYED and no S2 transition is
    /// running (transition &gt; tracking, risk 4).
    /// </summary>
    internal class SunTrackingGhostInfo
    {
        public uint partPersistentId;
        public Transform pivotTransform;
        public Quaternion neutralRotation;
        /// <summary>The pivot's rotation axis, in the pivot's own local frame.</summary>
        public Vector3 axisLocal = Vector3.up;
        /// <summary>Current slewed angle in degrees about <see cref="axisLocal"/> from neutral.</summary>
        public float currentAngleDegrees;
        public bool hasAimed;
    }

    /// <summary>
    /// S3 launch dust: ONE Parsek-owned particle system per ghost (the reentry <c>fireParticles</c>
    /// template — Parsek authored it, so its emission/size multipliers are driven directly).
    /// </summary>
    internal class LaunchDustInfo
    {
        public GameObject dustObject;
        public ParticleSystem particles;
        public Material material;
        public Texture2D generatedTexture;
        public float lastIntensity;
    }

    /// <summary>
    /// The three S3 synthesis families a single part can contribute. One container so the ghost
    /// build's already-14-wide out-parameter list grows by one rather than by three.
    /// </summary>
    internal class SynthesizedMotionGhostInfos
    {
        public List<GimbalGhostInfo> gimbals;
        public List<ControlSurfaceGhostInfo> controlSurfaces;
        public List<SunTrackingGhostInfo> sunTrackers;

        internal bool IsEmpty =>
            (gimbals == null || gimbals.Count == 0)
            && (controlSurfaces == null || controlSurfaces.Count == 0)
            && (sunTrackers == null || sunTrackers.Count == 0);
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
        /// <summary>S1 baselines, index-parallel to <see cref="particleSystems"/>. Empty until captured.</summary>
        public List<GhostFxMagnitudeBaseline> particleBaselines = new List<GhostFxMagnitudeBaseline>();
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
        WheelGroundSpeed,
        // S3 wheel steering: caliper heading DERIVED from the rate of change of the ghost's own
        // ground-track heading, not read from a recorded event. Recorded ModuleWheelSteering
        // scalars (old recordings carry them, and they were an unsigned steering INPUT rather than
        // an angle) are ignored in this mode, the same contract WheelGroundSpeed has.
        WheelSteeringHeading
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
        /// <summary>
        /// The pose this servo starts every playback cycle from: the M1 snapshot value
        /// when one was read, else 0f — which by construction IS the prefab pose, because
        /// <c>stowedPos</c>/<c>stowedRot</c> are captured from the prefab-mirrored ghost
        /// transform and <c>ApplyRoboticPose(info, 0f)</c> is the identity offset. Never
        /// seeded from the prefab's own scalar field: the transform already embodies it,
        /// so applying it again would double-count.
        /// </summary>
        public float spawnValue;
        /// <summary>True when <see cref="spawnValue"/> came from the snapshot rather than defaulting to the prefab pose.</summary>
        public bool hasSnapshotBaseline;
        // WheelGroundSpeed mode only: the wheel's rolling radius in metres, read at build time from
        // ModuleWheelBase.radius (a [KSPField]) times the part's rescaleFactor, matching how stock
        // sizes the wheel collider. Falls back to
        // GhostPlaybackLogic.DefaultWheelRadiusMeters when the module or field is unreachable.
        public float wheelRadius;
        // WheelSteeringHeading mode only: the eased steering angle currently APPLIED, in degrees
        // about axisLocal from stowedRot. Eased rather than snapped so a noisy heading derivative
        // cannot make the calipers judder. Reset to 0 on a loop cycle alongside currentValue.
        public float steeringAngleDegrees;
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
        /// <summary>S3: accumulated across parts, merged into one container at CompleteTimelineGhostBuild.</summary>
        public SynthesizedMotionGhostInfos synthesizedMotionInfos = new SynthesizedMotionGhostInfos();
        public List<ColorChangerGhostInfo> colorChangerInfos = new List<ColorChangerGhostInfo>();
        public List<CompoundPartGhostInfo> compoundPartInfos = new List<CompoundPartGhostInfo>();
        public List<AudioGhostInfo> audioInfos = new List<AudioGhostInfo>();
        /// <summary>
        /// Per-part module state read out of the snapshot PART nodes as the build walks
        /// them (M1). Keyed by persistentId; only parts with something readable get an
        /// entry. Consumed once by <c>GhostPlaybackLogic.ApplySnapshotBaselines</c> and
        /// kept on the playback state so a loop cycle can restore the same baseline.
        /// </summary>
        public Dictionary<uint, SnapshotPartBaseline> snapshotBaselines =
            new Dictionary<uint, SnapshotPartBaseline>();
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
        /// <summary>S3: the per-part gimbal / control-surface / sun-tracking containers, flattened.</summary>
        public SynthesizedMotionGhostInfos synthesizedMotionInfos;
        public List<ColorChangerGhostInfo> colorChangerInfos;
        public List<CompoundPartGhostInfo> compoundPartInfos;
        public List<AudioGhostInfo> audioInfos;
        /// <summary>
        /// Per-part snapshot module baselines (M1), keyed by persistentId. Null when the
        /// snapshot carried nothing readable — which is also the pre-M1 behaviour.
        /// </summary>
        public Dictionary<uint, SnapshotPartBaseline> snapshotBaselines;
    }
}
