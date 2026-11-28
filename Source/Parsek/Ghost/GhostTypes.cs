using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    internal class JettisonGhostInfo
    {
        public uint partPersistentId;
        public List<Transform> jettisonTransforms;
        // The subset stock hides when the part starts in flight (ModuleJettison.SetupJettisonState:
        // an empty bottom node with ignoreNodes off, or a jettisoned / hide-override shroud), read
        // from the snapshot. The build hides them, and a loop cycle restarts with them hidden, so
        // playback events layer over the same look the real part had. Null or empty when none.
        public List<Transform> spawnHiddenTransforms;
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
        // Animated bones BELOW the canopy transform (a rigged canopy such as ReStock's, whose
        // deploy clips move child bones, not just the canopy root). Null when the deploy clips
        // animate nothing under the canopy (stock canopies), so those chutes keep the
        // root-only pose. Each entry carries the bone's own local pose per stage.
        public List<CanopyBonePose> canopyBones;

        // Stock aims an open canopy along the airflow every physics tick
        // (ModuleParachute.FixedUpdate: canopy.rotation = LookRotation(+-dragVectorDir, up),
        // then a spread tilt for a symmetry group), so the sampled clip rotation is never what
        // the player sees. These are that rule's inputs, read from the prefab module at build.
        // partTransform is the ghost stand-in for the part's own transform (stock base.transform).
        // aimCanopy is false for an EVA chute (ModuleEvaChute poses its canopy kerbal-relative)
        // and for a module that sets dontRotateParachute.
        public Transform partTransform;
        public bool aimCanopy;
        public bool invertCanopy = true;
        public float rotationSpeedDegPerSec = 90f;
        public float spreadAngleDeg;
        public int symmetryCount;
        // Runtime aim state: the stage being aimed (Stowed = not aiming), whether the world
        // rotation below holds a real aim, and the UT of that aim (NaN = snap on the next aim).
        public CanopyPoseStage canopyAimStage;
        public bool canopyAimSeeded;
        public Quaternion canopyAimWorldRot;
        public double canopyAimUT = double.NaN;
        public CanopyAimSource canopyAimLastSource;
    }

    /// <summary>Where a ghost canopy's aim direction came from (see GhostPlaybackLogic.ResolveCanopyAimForward).</summary>
    internal enum CanopyAimSource
    {
        /// <summary>The ghost's own surface-relative velocity (stock's dragVectorDir).</summary>
        Airflow,
        /// <summary>No usable velocity: the part is taken to fall straight down the body's gravity.</summary>
        Gravity,
        /// <summary>No velocity and no body: the part is taken to fall along its own -Y.</summary>
        PartUp
    }

    internal enum CanopyPoseStage
    {
        Stowed,
        SemiDeployed,
        Deployed
    }

    /// <summary>
    /// One animated canopy bone's local pose (relative to its parent) at each parachute stage.
    /// Stowed is the ghost bone's build-time pose; the two deploy stages are sampled from the
    /// part's deploy animations at their end.
    /// </summary>
    internal class CanopyBonePose
    {
        public Transform bone;
        public Vector3 stowedPos;
        public Quaternion stowedRot;
        public Vector3 stowedScale;
        public bool semiDeployedSampled;
        public Vector3 semiPos;
        public Quaternion semiRot;
        public Vector3 semiScale;
        public Vector3 deployedPos;
        public Quaternion deployedRot;
        public Vector3 deployedScale;
    }

    /// <summary>
    /// A canopy bone sampled from a prefab clone: the child-index path from the canopy
    /// transform to the bone, and its local pose at the end of each deploy animation.
    /// </summary>
    internal class CanopyBoneSample
    {
        public int[] childIndexPath;
        public bool semiDeployedSampled;
        public Vector3 semiPos;
        public Quaternion semiRot;
        public Vector3 semiScale;
        public Vector3 deployedPos;
        public Quaternion deployedRot;
        public Vector3 deployedScale;
    }

    /// <summary>
    /// Per-part-type result of sampling a parachute's canopy deploy animations.
    /// </summary>
    internal class CanopySample
    {
        public Vector3 sdScale;
        public Vector3 sdPos;
        public Quaternion sdRot;
        public bool semiDeployedSampled;
        public Vector3 dScale;
        public Vector3 dPos;
        public Quaternion dRot;
        // True when dPos / dRot are the clip's end pose read in the sampled model's frame (the
        // sdPos / sdRot pair is in that frame whenever semiDeployedSampled). The builder moves
        // them into the ghost canopy's parent frame, which is what localPosition reads.
        public bool deployedInModelFrame;
        public List<CanopyBoneSample> bones;
    }

    internal struct KspEmitterRef
    {
        public MonoBehaviour emitter;
        public System.Reflection.FieldInfo emitField;
        // S1 (plume magnitude): the emitter's own particle-rate / velocity fields at BUILD time,
        // captured once by GhostVisualBuilder.CaptureFxMagnitudeBaselines AFTER every build-time
        // mutation has run (the #383 size boost, the per-part
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
        // A GhostEngineFxRule writes this emitter; the S1 ratio leaves it alone.
        public bool stockRuled;
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
        // A GhostEngineFxRule writes this system; the S1 ratio leaves it alone.
        public bool stockRuled;
    }

    /// <summary>
    /// Where a ghost engine's particle systems came from, decided once at build time.
    /// <see cref="EffectsNode"/> means at least one system was cloned from the part's
    /// EFFECTS node (on-disk pristine EFFECTS included); <see cref="Supplement"/> means
    /// every system is a Parsek-injected fallback prefab; <see cref="Legacy"/> means the
    /// PART-level <c>fx_*</c> children path. Read only by the ignition witness line.
    /// </summary>
    internal enum EngineFxSource
    {
        None,
        Legacy,
        EffectsNode,
        Supplement,
    }

    internal class EngineGhostInfo
    {
        public uint partPersistentId;
        public int moduleIndex;
        /// <summary>Runtime part name (dot form), carried for the ignition witness line.</summary>
        public string partName;
        public EngineFxSource fxSource;
        /// <summary>
        /// Systems cloned from EFFECTS-node entries. They are appended FIRST, so they are the
        /// leading <c>effectsNodeSystemCount</c> entries of <see cref="particleSystems"/>.
        /// </summary>
        public int effectsNodeSystemCount;
        public int supplementSystemCount;
        public int legacySystemCount;
        public List<ParticleSystem> particleSystems = new List<ParticleSystem>();
        public List<KspEmitterRef> kspEmitters = new List<KspEmitterRef>();
        /// <summary>S1 baselines, index-parallel to <see cref="particleSystems"/>. Empty until captured.</summary>
        public List<GhostFxMagnitudeBaseline> particleBaselines = new List<GhostFxMagnitudeBaseline>();
        // S1 fallback curves, of sources with no entry in fxRules.
        public FloatCurve emissionCurve;
        public FloatCurve speedCurve;
        public float currentPower;
        /// <summary>The sources stock's own power rules size (<see cref="GhostPlaybackLogic.ApplyEngineFxRules"/>).</summary>
        public List<GhostEngineFxRule> fxRules = new List<GhostEngineFxRule>();
        /// <summary>The part PREFAB's engine module, read for its thrust curves only.</summary>
        public ModuleEngines engine;
        public bool usesThrustFraction;
        /// <summary><c>finalThrust / maxThrust</c> at full throttle in the ghost's air; 1 until known.</summary>
        public float thrustFraction = 1f;
        // The inputs thrustFraction was last computed from.
        public int thrustAirSerial;
        public float thrustThrottle = -1f;
        /// <summary>The stock <c>SmokeTrailControl</c> and <c>FXFadeByPressure</c> fades of this
        /// engine's cloned FX, captured before the component is stripped (todo
        /// GHOST-SMOKE-IN-VACUUM). Empty for an engine whose FX carry none.</summary>
        public List<GhostSmokeTrailFade> smokeTrailFades = new List<GhostSmokeTrailFade>();
        /// <summary>The stock <c>ModuleSurfaceFX</c> this engine drives (launch dust); null when none.</summary>
        public GhostSurfaceFxSource surfaceFx;
    }

    /// <summary>
    /// One stock <c>SmokeTrailControl</c> transcribed onto a ghost renderer. Stock fades a smoke
    /// trail by air density every frame (decompiled <c>SmokeTrailControl.Update</c>, KSP 1.12.5):
    /// <c>airDensity = InverseLerp(fadeEndDns, fadeStartDns, ship_dns)</c>; above 0.001 it tints
    /// the material and enables the renderer, otherwise it disables the renderer, which is why no
    /// smoke draws in vacuum. The ghost keeps the component's own serialized values and drives the
    /// same rule from its playback altitude (<see cref="GhostPlaybackLogic.UpdateEngineSmokeTrailFades"/>).
    /// </summary>
    internal sealed class GhostSmokeTrailFade
    {
        public Renderer renderer;
        public float maxAlpha;
        public float fadeStartDns;
        public float fadeEndDns;
        public string shaderColorName;
        public Color fadedColor;
        public bool fadeToColor;
        /// <summary>The renderer's own material instance, made on the first tint write.</summary>
        public Material material;
        /// <summary>The quantized fade level last written (<see cref="GhostPlaybackLogic.QuantizeSmokeTrailAirDensity"/>):
        /// -1 never written, 0 hidden, 1..N drawn.</summary>
        public int appliedLevel = -1;
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

    /// <summary>One material colour a deploy clip animates, at its stowed and deployed ends.</summary>
    internal struct DeployableColorState
    {
        public Material material;
        public string property;
        public Color stowed;
        public Color deployed;
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
        /// <summary>The part's FXModuleLookAtConstraint pairs, re-aimed after every pose write.</summary>
        public List<LookAtConstraintGhostInfo> lookAtConstraints;
        /// <summary>
        /// Material colours the deploy clip animates (ReStock's SurfaceScanner 'screenon' turns
        /// the screen on and moves nothing), written at the same fraction as the transforms.
        /// Null when the clip animates no colour.
        /// </summary>
        public List<DeployableColorState> colors;

        /// <summary>
        /// S6: the ghost-side clone of the part's <c>panelBreakTransform</c> subtree — what stock
        /// hides when a ModuleDeployablePart goes BROKEN. Resolved at build time from
        /// <c>breakName</c>, falling back to <c>pivotName</c> exactly as stock's own OnStart does
        /// (the prefab has not run OnStart, so the empty-breakName default has not been applied
        /// yet and we must apply it ourselves). Null when the part is not breakable or the
        /// transform could not be resolved onto the ghost — in which case a DeployableBroken
        /// event degrades to no visual rather than to a wrong one.
        /// </summary>
        public Transform breakSubtreeRoot;

        /// <summary>
        /// True while <see cref="breakSubtreeRoot"/> is hidden. Tracked rather than read back off
        /// the GameObject so the loop-cycle re-show and the sun-tracking gate are pure state
        /// reads, and so a null transform still carries a truthful "this part is broken" answer.
        /// </summary>
        public bool breakSubtreeHidden;

        /// <summary>
        /// Stock's full stowed -&gt; deployed motion time in seconds, set only for a family whose
        /// recorder poller emits its event when stock's motion ENDS (ModuleDeployablePart, gear,
        /// RetractableLadder, cargo bay, standalone ModuleAnimateGeneric): the clip at the
        /// module's own speed over the travel it plays. NaN for every other family, whose event
        /// marks the START and which keeps the clip-from-the-event transition.
        /// </summary>
        public double extendMotionSeconds = double.NaN;
        /// <summary>The same for deployed -&gt; stowed (gear retracts at its own factor).</summary>
        public double retractMotionSeconds = double.NaN;
        /// <summary>
        /// The in-flight transition's own length when it ENDS at its event UT (an end-marked
        /// family); NaN for a transition that starts at its event and runs the clip.
        /// </summary>
        public double transitionDurationSeconds = double.NaN;
        /// <summary>The recorded event UT an end-marked transition ends at; NaN otherwise.</summary>
        public double transitionEventUT = double.NaN;
    }


    /// <summary>
    /// The ghost side of a stock <c>ModuleWheelDamage</c> wheel: the cloned transforms stock
    /// toggles on a break (<c>damagedTransformName</c> shown, <c>undamagedTransformName</c>
    /// hidden) and on a repair (the reverse). Built only when the part's busted mesh actually
    /// cloned onto the ghost (or the part names no busted mesh at all, which is stock's own
    /// "hide the wheel" shape), so a WheelBroken event can never hide the intact wheel without
    /// something to show in its place. The busted transforms start HIDDEN at build time.
    /// </summary>
    internal class WheelDamageGhostInfo
    {
        public uint partPersistentId;
        /// <summary>Cloned transforms named by <c>damagedTransformName</c>: shown while broken.</summary>
        public List<Transform> damagedTransforms = new List<Transform>();
        /// <summary>Cloned transforms named by <c>undamagedTransformName</c>: shown while intact.</summary>
        public List<Transform> undamagedTransforms = new List<Transform>();
        /// <summary>True while the ghost shows the busted wheel. Tracked rather than read back off a GameObject.</summary>
        public bool damaged;
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
        /// <summary>The part's FXModuleLookAtConstraint pairs, re-aimed when the deflection changes.</summary>
        public List<LookAtConstraintGhostInfo> lookAtConstraints;
        /// <summary>The deflection the constraints were last aimed at; NaN = never.</summary>
        public Vector2 lookAtDeflection = new Vector2(float.NaN, float.NaN);
    }

    /// <summary>
    /// One FXModuleLookAtConstraint CONSTRAINLOOKFX pair on a ghost: every mover (the clones of
    /// stock's <c>FindModelTransforms(rotatorsName)</c>) LookAt the target clone with the ghost
    /// part root's up, as stock <c>Track</c> does every frame on the live part.
    /// </summary>
    internal class LookAtConstraintGhostInfo
    {
        public List<Transform> movers = new List<Transform>();
        public Transform target;
        public Transform upReference;
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
    /// What stock's <c>ModuleSurfaceFX</c> raycast reports, in its own <c>SurfaceType</c> order
    /// (decompiled KSP 1.12.5): nothing, terrain, water, the launch pad.
    /// </summary>
    internal enum LaunchDustSurface
    {
        None = 0,
        Terrain = 1,
        Water = 2,
        Launchpad = 3,
    }

    /// <summary>
    /// Part parity P9 Y3: one engine's stock <c>ModuleSurfaceFX</c>, read off the part prefab at
    /// ghost build: the ghost's copy of the thrust transform it raycasts along and the module's
    /// reach, falloff and <c>fxMax</c> (stock defaults 50 m, 2 and 1).
    /// </summary>
    internal sealed class GhostSurfaceFxSource
    {
        public Transform thrustTransform;
        public string thrustTransformName;
        public float fxMax = 1f;
        public float maxDistance = 50f;
        public float falloff = 2f;
    }

    /// <summary>
    /// One built launch dust emitter: either a copy of stock's own <c>SurfaceFX</c> / <c>WaterFX</c>
    /// prefab with the stock component removed (its cloud, dust and wake systems kept), or the
    /// Parsek-built faint fallback disc in <see cref="dust"/> alone. Parsek owns every system, so
    /// the driver writes their colour and emission directly.
    /// </summary>
    internal sealed class LaunchDustEmitter
    {
        public GameObject root;
        public ParticleSystem cloud;
        public ParticleSystem dust;
        public ParticleSystem wake;
        /// <summary>The systems' own start colours, whose alpha the driver replaces (stock keeps the RGB).</summary>
        public Color cloudColor = Color.white;
        public Color dustColor = Color.white;
        public Color wakeColor = Color.white;
        /// <summary>Scratch for the per-particle push, grown on demand.</summary>
        public ParticleSystem.Particle[] buffer;
        /// <summary>True for a copy of stock's prefab, false for the Parsek-built fallback.</summary>
        public bool stockPrefab;
        /// <summary>Where it came from, for the logs ("SurfaceFX", "WaterFX", "built").</summary>
        public string source;
        public Material material;
        public Texture2D generatedTexture;
        /// <summary>The strength last driven (stock's clamped sum of ScaledFX); 0 while stopped.</summary>
        public float fxScale;
        /// <summary>Whether the systems are emitting, so a stopped emitter is not stopped again every frame.</summary>
        public bool emitting;
    }

    /// <summary>
    /// S3 launch dust: at most one terrain and one water emitter per ghost, built lazily on the
    /// first frame that draws that surface. A build that fails is never retried.
    /// </summary>
    internal class LaunchDustInfo
    {
        public LaunchDustEmitter terrain;
        public LaunchDustEmitter water;
        public bool terrainUnavailable;
        public bool waterUnavailable;
        public float lastIntensity;
        /// <summary>The terrain emitter's root, the object the dust cases and the visibility pass name.</summary>
        public GameObject dustObject => terrain?.root;
    }

    /// <summary>
    /// S4: the Parsek-owned puff a ghost kerbal's jetpack emits while thrusting.
    ///
    /// WHY PARSEK OWNS IT rather than cloning a stock system, verified by decompile: KerbalEVA's
    /// jetpack FX are <c>FXGroup</c>s resolved from <c>part.findFxGroup("X+")</c> and friends, and
    /// <c>kerbalEVA.cfg</c>'s EFFECTS node is AUDIO ONLY — there are no stock particles to clone,
    /// and no ModuleRCS to route through the engine/RCS FX path. Same situation as launch dust, so
    /// the same answer: author one system outright, which also means the driver may write emission
    /// and size multipliers directly rather than through the captured-baseline machinery a cloned
    /// KSP asset needs.
    /// </summary>
    internal class EvaJetpackPlumeInfo
    {
        public GameObject plumeObject;
        public ParticleSystem particles;
        public Material material;
        public Texture2D generatedTexture;
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
        /// <summary>
        /// S7: the ModuleAnimationGroup RUNNING loops (drill spinning, ISRU churning). Rides this
        /// container rather than a fifteenth <c>AddPartVisuals</c> out-parameter, and it belongs
        /// here on merit: like the other three it is DRIVEN per frame from recorded state rather
        /// than applied once by an event, so it shares the same per-frame walk.
        /// </summary>
        public List<ConverterLoopGhostInfo> converterLoops;

        internal bool IsEmpty =>
            (gimbals == null || gimbals.Count == 0)
            && (controlSurfaces == null || controlSurfaces.Count == 0)
            && (sunTrackers == null || sunTrackers.Count == 0)
            && (converterLoops == null || converterLoops.Count == 0);
    }

    /// <summary>One transform's pose at one sampled phase of a looping animation clip.</summary>
    internal struct ConverterLoopPose
    {
        public Vector3 pos;
        public Quaternion rot;
        public Vector3 scale;
    }

    /// <summary>
    /// One ghost transform animated by a running loop, with its pose sampled at each phase.
    /// <c>phases</c> has the same length for every transform in a
    /// <see cref="ConverterLoopGhostInfo"/> (the sampler fills all of them or drops the transform).
    /// </summary>
    internal class ConverterLoopTransformState
    {
        public Transform t;
        public ConverterLoopPose[] phases;
    }

    /// <summary>
    /// S7: one part's ModuleAnimationGroup RUNNING animation, replayed as a continuous loop while
    /// the part's converter is active.
    ///
    /// WHY N SAMPLED PHASES rather than the two-pose interpolation the deployable family uses: a
    /// running loop is CYCLIC, so its two endpoints are the SAME pose. Sampling only the ends would
    /// produce a delta of zero and no motion at all — a drill that "runs" by standing perfectly
    /// still. N phases around the cycle give a piecewise-linear traversal that reads correctly at
    /// playback speed, which is all the design principle asks for.
    ///
    /// The phase is a pure function of <c>(playbackUT - activeSinceUT) / clipLengthSeconds</c>,
    /// exactly the S2 argument: no wall-clock time anywhere, so scrubbing, time warp and looping
    /// replays all land on the same pose for the same recorded moment.
    ///
    /// Built INDEPENDENTLY of <c>TryGetAnimationGroupDeployAnimation</c>, which requires a non-empty
    /// deployAnimationName — the large ISRU's ModuleAnimationGroup has an EMPTY deploy name and only
    /// a running one (<c>ProcessorLarge_running</c>), so anything gated on the deploy name would
    /// silently skip the single biggest ISRU part in the game.
    /// </summary>
    internal class ConverterLoopGhostInfo
    {
        public uint partPersistentId;
        public List<ConverterLoopTransformState> transforms;
        /// <summary>The running clip's own length, clamped by ClampDeployableClipSeconds.</summary>
        public float clipLengthSeconds = 3f;
        /// <summary>
        /// True when the loop runs while the ModuleAnimationGroup is DEPLOYED rather than while a
        /// converter runs: stock's <c>CheckForActivity</c> plays the active clip whenever the
        /// group is deployed and any <c>IAnimatedModule</c> on the part reports active, and every
        /// non-converter one (ModuleResourceScanner, ModuleGPS, ModuleOrbitalSurveyor) reports
        /// active as soon as the deploy enables it; <c>alwaysActive</c> does the same. Driven by
        /// the DeployableExtended / Retracted events and the snapshot's isDeployed instead of the
        /// converter edges (the M4435 orbital scanner's 'miniscanner' / ReStock 'Spin').
        /// </summary>
        public bool followsDeploy;
        /// <summary>True while the part's converter is running and the loop should advance.</summary>
        public bool active;
        /// <summary>
        /// The RECORDED UT the loop started at — never wall time. Phase is measured from here, so
        /// the same recorded moment always renders the same pose.
        /// </summary>
        public double activeSinceUT;
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
        // Engine / RCS nozzle glow clips of the part (driven by recorded power, not by heat events).
        public List<NozzleGlowInfo> nozzleGlows;
        // Created for glows on materials of their own (NozzleGlowInfo.isColorChanger) or clips that
        // only move transforms: no heat visual, so the part keeps the reentry glow (which leaves those materials' property to them).
        public bool throttleGlowsOnly;
        // The part's ModuleAnimateHeat names a clip its model lacks: stock drives nothing, so a
        // heat event draws nothing here and the part counts as any other for the reentry glow.
        public bool heatClipMissing;
    }

    internal class LightGhostInfo
    {
        public uint partPersistentId;
        public List<Light> lights;
        // Stock ModuleLight drives its lens renderer's _EmissiveColor (lightColor on, black off)
        // and its flare renderer's _TintColor (white on, black off); per-ghost material clones.
        public List<Material> lensMaterials;
        public Color lensOnColor;
        public List<Material> flareMaterials;
        // Last lens / flare write, so the every-frame light driver skips unchanged writes.
        public bool lensFlareWritten;
        public bool lensFlareWrittenOn;
        // Stock ModuleLight's fade (decompiled FixedUpdate): its own lights (stockDriven) move
        // toward their on intensity at brightenSpeed and toward 0 at dimSpeed, and an
        // animationDim lamp stays lit while it dims. onIntensities is each light's level-1 value.
        public List<bool> stockDriven;
        public List<float> onIntensities;
        public float brightenSpeed;
        public float dimSpeed;
        public bool animationDim;
        // The fade level last written to the module's lights (0 off, 1 on).
        public float level;
    }

    /// <summary>One frame of a ghost lamp's stock fade (<see cref="GhostPlaybackLogic.ResolveLightFade"/>).</summary>
    internal struct LightFadeFrame
    {
        // Fraction of each module light's on intensity.
        public float level;
        // Whether the module's lights draw.
        public bool lightsEnabled;
        // How far the lens / flare colour has gone from the old state to the new, 0 to 1.
        public float lensFraction;
        public bool settled;
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
        // True only for a colour changer a ModuleAblator drives (the reentry char); a
        // rest-state colour changer (isCabinLight and isAblationChar both false) is written
        // once at build and never again.
        public bool isAblationChar;
        // Pattern B: highest char fraction reached (permanent, never decreases).
        // NOTE: Rewind past reentry won't reset this value — a reset mechanism
        // will be needed when rewind/scrub support is added.
        public float peakCharIntensity;
        public List<ColorChangerMaterialState> materials;
        // Set on a colour changer an FXModuleThrottleEffects drives (ReStock's nozzle glow): the
        // colours BuildNozzleGlows plays by the recorded engine power.
        public ThrottleGlowCurve throttleGlow;
        // The throttle-driven colour changers that write the same property of these materials
        // before throttleGlow in a frame, in frame order; null when it is the only one.
        public List<ThrottleGlowCurve> throttleGlowUnder;
    }

    /// <summary>What BuildNozzleGlows needs of a throttle-driven ModuleColorChanger.</summary>
    internal class ThrottleGlowCurve
    {
        public string moduleId;
        public float animRate;
        public bool useRate;
        public bool animState;
        // The module's colour curves, evenly sampled from 0 to 1.
        public Color[] samples;
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
        /// <summary>The emitters stock's <c>ModelMultiParticleFX</c> rule sizes
        /// (<see cref="GhostPlaybackLogic.ApplyRcsFxRules"/>).</summary>
        public List<GhostEngineFxRule> fxRules = new List<GhostEngineFxRule>();
        /// <summary>The thruster transforms the recorded power is the mean over.</summary>
        public int recordedNozzles = 1;
        /// <summary>Those of them the recorded part variant leaves active: the ones that can fire.</summary>
        public int activeNozzles = 1;
        /// <summary>
        /// Read-only record, index-parallel to <see cref="particleSystems"/> as built: the FX
        /// transform each system was built under. Nothing in the build or playback reads it
        /// (the part parity FX sweep hides the systems of the nozzles stock did not fire).
        /// Teardown clears <see cref="particleSystems"/> but not this list, so read it only
        /// through <see cref="TryGetParticleThruster"/>.
        /// </summary>
        public List<RcsParticleThruster> particleThrusters = new List<RcsParticleThruster>();

        /// <summary>
        /// The FX transform particle system <paramref name="particleIndex"/> was built under;
        /// false when the index is out of range or the two lists no longer line up (teardown).
        /// </summary>
        internal bool TryGetParticleThruster(int particleIndex, out RcsParticleThruster thruster)
        {
            thruster = default(RcsParticleThruster);
            if (particleSystems == null || particleThrusters == null) return false;
            if (particleThrusters.Count != particleSystems.Count) return false;
            if (particleIndex < 0 || particleIndex >= particleThrusters.Count) return false;
            thruster = particleThrusters[particleIndex];
            return true;
        }
    }

    /// <summary>
    /// Where one ghost RCS particle system came from: the EFFECTS model node's transform name
    /// and the transform's position among every transform of that name under the part prefab,
    /// in depth-first pre-order and before the variant filter. Stock builds
    /// <c>ModuleRCS.thrusterTransforms</c> with the same walk (<c>Part.FindHeirarchyTransforms</c>
    /// from the part's <c>model</c> child), so when the FX transform name is the module's
    /// <c>thrusterTransformName</c> and every match sits under <c>model</c>, the index is stock's
    /// thruster index i (the one <c>thrustForces[i]</c> is read for).
    /// </summary>
    internal struct RcsParticleThruster
    {
        public string transformName;
        public int thrusterIndex;
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
        // scalars (LEGACY recordings only — the recorder stopped emitting them, see
        // FlightRecorder.IsDerivedWheelVisualModuleName; and they were an unsigned steering INPUT
        // rather than an angle) are ignored in this mode, the same contract WheelGroundSpeed has.
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
        /// Rotational servos: the displayed angle of the prefab rest (the hinge's
        /// <c>modelInitialAngle</c>, else 0), taken off every recorded angle before posing
        /// (<see cref="GhostPlaybackLogic.ComputeRoboticPoseAngleDegrees"/>).
        /// </summary>
        public float poseAngleOffsetDegrees;
        /// <summary>
        /// The pose this servo starts every playback cycle from: the M1 snapshot value
        /// when one was read, else the prefab pose, which the builder sets to
        /// <see cref="poseAngleOffsetDegrees"/> for a Rotational servo and 0 otherwise
        /// (<c>stowedPos</c>/<c>stowedRot</c> are captured from the prefab-mirrored ghost
        /// transform, so that value poses the identity offset). Never seeded from the
        /// prefab's own scalar field: the transform already embodies it, so applying it
        /// again would double-count.
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
        /// <summary>
        /// Breaking Ground servos only: the moving-side attach config read from the prefab
        /// module (null for every other robotic family). Feeds ServoFollowerPlanner.
        /// </summary>
        public ServoAttachConfig servoAttachConfig;
        /// <summary>
        /// The moving transform's local pose the snapshot was saved at (stock persists
        /// BaseServo.servoTransformPosition / servoTransformRotation in the same OnSave that
        /// refreshes the attached parts' positions), so the followers' snapshot poses are
        /// captured against it. False when the snapshot carries no usable pose.
        /// </summary>
        public bool hasSnapshotGeometryPose;
        public Vector3 snapshotGeometryPos;
        public Quaternion snapshotGeometryRot;
        /// <summary>
        /// Ghost parts riding this servo's moving half; re-posed by
        /// GhostPlaybackLogic.PropagateServoFollowers whenever the servo transform is written.
        /// Null when nothing rides it.
        /// </summary>
        public List<ServoFollower> followers;
        /// <summary>
        /// Servos whose part rides this servo (they are among <see cref="followers"/>); their
        /// own followers are re-posed after this servo's followers move. Null when none.
        /// </summary>
        public List<RoboticGhostInfo> nestedServos;
        /// <summary>
        /// False from each bind until the first time playback re-poses this servo's followers;
        /// that first re-pose writes one Verbose line (GhostPlaybackLogic.ConsumeFirstFollowerRepose).
        /// </summary>
        public bool followerReposeLogged;
        /// <summary>
        /// RotorRpm mode only: false from each ghost build until playback first turns this
        /// rotor; that first spin writes one Verbose line (GhostPlaybackLogic.ConsumeFirstRotorSpin).
        /// </summary>
        public bool rotorSpinLogged;
    }

    /// <summary>
    /// One ghost part that follows a servo's moving transform. The part stays a direct child
    /// of the ghost part container (every pid lookup, visibility walk and pivot pass keeps
    /// working); only its local pose is recomputed from the servo chain.
    /// </summary>
    internal class ServoFollower
    {
        public uint partPersistentId;
        public Transform part;
        /// <summary>The part's pose in the servo transform's frame, taken at the snapshot geometry pose.</summary>
        public Vector3 relPos;
        public Quaternion relRot;
    }

    internal struct FxModelDefinition
    {
        public string transformName;
        public string modelName;
        public Vector3 localOffset;
        public Quaternion localRotation;
        public Vector3 localScale;
        // A stock MODEL_MULTI_PARTICLE node, whose instances take stock's nested scale.
        public bool stockNested;
        // The stock node's emission / energy / speed curves; null for another node type.
        public GhostEngineFxCurves curves;
    }

    internal class FairingGhostInfo
    {
        public uint partPersistentId;
        // The fairing shell. In FLIGHT it is a container holding the panels stock's own
        // ProceduralFairings.FairingPanel builder made from the snapshot's XSECTIONs; in
        // other scenes (or when that builder declines) it is the legacy generated cone.
        // Null when the snapshot already shows the fairing deployed.
        public GameObject fairingMeshObject;
        public bool shellBuiltByStockPanels;
        // Meshes the shell created (stock panels or the generated cone); destroyed with the ghost.
        public List<Mesh> shellMeshes;
        // Stock ModuleStructuralNode objects of this part (the interstage truss and cap
        // plates), driven by the stock visibility rule. Null when the part has none.
        public List<FairingStructureNodeGhostInfo> structureNodes;
    }

    /// <summary>
    /// One stock ModuleStructuralNode of a fairing part on a ghost. Mirrors the inputs of
    /// ModuleStructuralNode.CheckDisplay: the structure is shown when visibilityState is on
    /// and (spawnManually ? spawnState : a part is attached at one of attachNodeNames),
    /// inverted by reverseVisibility, with attachment read from the snapshot's attN rows.
    /// Stock evaluates that rule only at load and from DecoupleAction, which a separating
    /// stage fires before it clears the attach node, so a live truss stays shown after its
    /// stage leaves until the craft reloads. The snapshot then records the live state
    /// (recordedShown, from GhostFairingStructureStamp) and the ghost shows that instead;
    /// the ghost never re-evaluates during playback, as stock does not in flight.
    /// </summary>
    internal class FairingStructureNodeGhostInfo
    {
        public string rootObject;
        public GameObject structureObject;
        public bool visibilityState;
        public bool spawnManually;
        public bool spawnState;
        public bool reverseVisibility;
        public int[] attachedPartIndices;
        public bool? recordedShown;
    }

    /// <summary>One ghost mesh the reentry sheath redraws through stock's FX shaders.</summary>
    internal struct FireShellMesh
    {
        public Mesh mesh;
        public Transform transform;
        // The mesh's renderer: its materials pick the replacement subshader, as stock's do.
        public Renderer renderer;
    }

    internal class ReentryFxInfo
    {
        // Never built since part parity P9 Y1 (stock's sheath replaced the particles); kept null
        // until the destroy path that still names them drops them.
        public Mesh combinedEmissionMesh;
        public Texture2D generatedTexture;
        public List<FireShellMesh> fireShellMeshes; // the ghost meshes GhostAeroFxRenderer redraws
        public List<HeatMaterialState> glowMaterials = new List<HeatMaterialState>();
        public List<Material> allClonedMaterials = new List<Material>();
        // Smoothed stock FxScalar, clamped to 0..1 (0 = no effect).
        public float lastIntensity;
        // Stock's thermal blend for the drive: 0 = condensation, 1 = reentry heat.
        public float aeroState;
        // What the last drive computed the scalar from (logged on the rig's shot frames).
        public AeroFxInputs lastInputs;
        public float vesselLength;
        // The last sheath decision GhostAeroFxRenderer logged for this ghost.
        public GhostAeroSheathDecision? lastSheathDecision;
        // GhostAeroFxRenderer logged this build's sheath materials (once per info, so the
        // renderer holds no reference that would keep a destroyed ghost's info alive).
        public bool sheathMaterialsLogged;
        // Per-ghost rate-limit keys of the two per-frame log lines, composed once per recording
        // index (GhostAeroFxLogKeys.CachedLogKey), so a suppressed line builds no string.
        public string sheathLogKey;
        public int sheathLogKeyIdx = -1;
        public string intensityLogKey;
        public int intensityLogKeyIdx = -1;
        // Test-only isolation frame (GhostPlaybackEngine.ArmReentryIsolationForOneFrame): the
        // mode the next DriveReentryLayers of this ghost applies, consumed by that drive; None
        // unless a test or the parity rig armed it.
        public ReentryIsolation isolationArmed;
        // True while an isolation frame keeps the sheath off; the next drive that does not
        // isolate offers it again.
        public bool fireHiddenForIsolation;
    }

    /// <summary>
    /// What a test-only reentry isolation frame leaves out of one ghost's reentry drive: the sheath
    /// (and its light; the member names keep the earlier fire-and-shell wording the rig arms), or
    /// that and Layer A's glow (the char stays: it is the body).
    /// </summary>
    internal enum ReentryIsolation
    {
        None = 0,
        FireAndShell = 1,
        FireShellAndGlow = 2,
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
        // ReStock's ModuleRestockLinkedMesh texture stretch: the shader texture properties it
        // rescales (null when the module is not ReStock's) and the stretched axis (0 = x, 1 = y).
        public string[] stretchTextures;
        public int stretchAxisIndex;
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
        public List<WheelDamageGhostInfo> wheelDamageInfos = new List<WheelDamageGhostInfo>();
        public List<HeatGhostInfo> heatInfos = new List<HeatGhostInfo>();
        public List<LightGhostInfo> lightInfos = new List<LightGhostInfo>();
        public List<FairingGhostInfo> fairingInfos = new List<FairingGhostInfo>();
        public List<RcsGhostInfo> rcsInfos = new List<RcsGhostInfo>();
        public List<RoboticGhostInfo> roboticInfos = new List<RoboticGhostInfo>();
        /// <summary>Breaking Ground servo parts by PART index, for ServoFollowerPlanner.</summary>
        public Dictionary<int, ServoAttachConfig> servoAttachConfigsByPartIndex =
            new Dictionary<int, ServoAttachConfig>();
        /// <summary>S3: accumulated across parts, merged into one container at CompleteTimelineGhostBuild.</summary>
        public SynthesizedMotionGhostInfos synthesizedMotionInfos = new SynthesizedMotionGhostInfos();
        public List<ColorChangerGhostInfo> colorChangerInfos = new List<ColorChangerGhostInfo>();
        public List<CompoundPartGhostInfo> compoundPartInfos = new List<CompoundPartGhostInfo>();
        public List<AudioGhostInfo> audioInfos = new List<AudioGhostInfo>();
        /// <summary>The skin each variant part's visuals were built with, keyed by persistentId.</summary>
        public Dictionary<uint, GhostAppliedSkin> appliedSkins = new Dictionary<uint, GhostAppliedSkin>();
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
        public List<WheelDamageGhostInfo> wheelDamageInfos;
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
        /// <summary>Which ghost parts ride which servo's moving half. Null when the ghost has no servo.</summary>
        public ServoFollowerPlan servoFollowerPlan;
        /// <summary>
        /// The skin the builder applied to each part that has a <c>ModulePartVariants</c> or a
        /// <c>ModuleB9PartSwitch</c>, keyed by persistentId. A read-only record of what
        /// <c>AddPartVisuals</c> resolved; nothing in the build or in playback reads it. Null when
        /// no part of the snapshot has a variant or B9 module.
        /// Read it through <see cref="TryGetAppliedSkin"/>.
        /// </summary>
        public Dictionary<uint, GhostAppliedSkin> appliedSkins;

        /// <summary>The skin the builder applied to part <paramref name="persistentId"/>; false
        /// when the part has neither a variant module nor a B9PartSwitch module, or the build drew
        /// no visuals for it. The builder models no other part switcher, so a false here for such
        /// a part is the answer, not a missing record.</summary>
        internal bool TryGetAppliedSkin(uint persistentId, out GhostAppliedSkin skin)
        {
            skin = null;
            return appliedSkins != null && appliedSkins.TryGetValue(persistentId, out skin) && skin != null;
        }
    }

    /// <summary>
    /// What <c>GhostVisualBuilder.AddPartVisuals</c> resolved for one part's
    /// <c>ModulePartVariants</c> skin: the VARIANT whose GAMEOBJECTS and TEXTURE rules it used and
    /// how it found it (<c>GhostVisualBuilder.MatchVariantNode</c>'s resolution path).
    /// </summary>
    internal sealed class GhostAppliedSkin
    {
        /// <summary>The variant name; on the <c>base-implicit</c> path the snapshot's name, for
        /// which the builder keeps the prefab's default look. Null when the part config has no
        /// variant module node or no VARIANT.</summary>
        public string variantName;

        /// <summary><c>snapshot</c>, <c>runtime</c>, <c>base</c>, <c>first-fallback</c>,
        /// <c>base-implicit</c>, or null when no variant was resolved.</summary>
        public string variantResolutionPath;

        /// <summary>The subtype the builder applied for each <c>ModuleB9PartSwitch</c> module of the
        /// part, in config order; null when the part has no B9 module.</summary>
        public List<GhostAppliedB9Subtype> b9Subtypes;
    }

    /// <summary>One <c>ModuleB9PartSwitch</c> module's subtype as the ghost builder resolved it
    /// (<see cref="GhostB9Subtypes.Resolve"/>).</summary>
    internal sealed class GhostAppliedB9Subtype
    {
        /// <summary>The module's <c>moduleID</c>; null for a lone unnamed module.</summary>
        public string moduleId;
        /// <summary>The subtype shown; null when the module has no SUBTYPE.</summary>
        public string subtype;
        /// <summary><c>snapshot</c>, <c>default</c> or <c>snapshot-unknown</c>.</summary>
        public string resolutionPath;
        /// <summary>False when the subtype also carries texture, material or module rules the
        /// ghost does not model, so the ghost shows it only in part.</summary>
        public bool modelled;
    }
}
