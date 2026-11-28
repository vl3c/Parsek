using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>What a motion case presses and how it reads its motion (design Appendix H.1,
    /// H.2): not part of the case identity; the case id reads the key's State and Context.</summary>
    internal sealed class PartParityMotionOption
    {
        /// <summary>One of <see cref="PartParityMotionRules.Families"/>.</summary>
        internal string Family;

        /// <summary>One of <see cref="PartParityMotionRules.Kinds"/>: how the case reads its
        /// predicted motion time and its stock clock.</summary>
        internal string Kind;

        /// <summary>The <c>moduleName</c> of the module the case presses.</summary>
        internal string Module;

        /// <summary>That module's prefab index (the same-word tiebreak reads it).</summary>
        internal int ModuleIndex;

        /// <summary>The motion word, <c>motion-&lt;state&gt;</c> (before the tiebreak).</summary>
        internal string State;

        internal string Context;

        /// <summary>The stock press: a KSPEvent or KSPAction name (P6's for its families,
        /// <c>ActionExtend</c> for a control surface); null for a servo, which is driven
        /// through <see cref="DriveField"/>.</summary>
        internal string Action;

        internal bool ActionIsKspAction;

        /// <summary>A servo's target field (<c>targetAngle</c> / <c>targetExtension</c>),
        /// set through its <c>BaseField</c> the way the PAW slider does; null otherwise.</summary>
        internal string DriveField;

        /// <summary>The P6 option of a P6 family (its press, done signal and timeout); null
        /// for robotics and control surfaces.</summary>
        internal PartParityStateOption StateOption;

        /// <summary>A light or cabin light: planned only when <c>motion=</c> names
        /// <c>light</c> by word (a reading run), never by <c>motion=all</c> (H.1).</summary>
        internal bool ReadingOnly;
    }

    /// <summary>One transforming module P7 does not judge, and why (H.1).</summary>
    internal sealed class PartParityMotionExclusion
    {
        internal string Part;

        /// <summary>The family whose case is missing, or null for a motion with no family.</summary>
        internal string Family;

        internal string Module;
        internal int ModuleIndex = -1;
        internal string Reason;

        /// <summary>The context the case would run in (an air run lists only air ones).</summary>
        internal string Context = PartParityStateRules.ContextAlone;
    }

    /// <summary>One part's motion cases and what it leaves out.</summary>
    internal sealed class PartParityMotionEnumeration
    {
        internal readonly List<PartParityCaseKey> Cases = new List<PartParityCaseKey>();
        internal readonly List<PartParityMotionExclusion> Exclusions = new List<PartParityMotionExclusion>();

        internal void AddRange(PartParityMotionEnumeration other)
        {
            if (other == null) return;
            Cases.AddRange(other.Cases);
            Exclusions.AddRange(other.Exclusions);
        }
    }

    /// <summary>One transform's local pose on one frame: position (m), rotation (a
    /// quaternion x, y, z, w) and scale.</summary>
    internal sealed class PartParityMotionPose
    {
        internal double[] Position = new double[3];
        internal double[] Rotation = { 0.0, 0.0, 0.0, 1.0 };
        internal double[] Scale = { 1.0, 1.0, 1.0 };
    }

    /// <summary>One progress transform's poses on one side, one per frame (or per scout point).</summary>
    internal sealed class PartParityMotionTrack
    {
        internal string Path;
        internal readonly List<PartParityMotionPose> Poses = new List<PartParityMotionPose>();
    }

    /// <summary>One transform's travel on one side (H.1): its accumulated travel per channel,
    /// the channel that moved most, and its fraction of that travel on every frame.</summary>
    internal sealed class PartParityMotionTransformProgress
    {
        internal string Path;

        /// <summary>Accumulated travel: metres, degrees, scale units.</summary>
        internal double TravelPositionM;
        internal double TravelRotationDeg;
        internal double TravelScale;

        /// <summary>The channel that moved most against its still threshold, null when none
        /// moved past it (the transform is left out).</summary>
        internal string Channel;

        /// <summary>The travel in <see cref="Channel"/> over its still threshold: the weight.</summary>
        internal double Weight;

        /// <summary>Its travel since frame 0 over its total, on every frame.</summary>
        internal double[] Fraction;

        internal double TravelIn(string channel)
        {
            switch (channel)
            {
                case PartParityMotionRules.ChannelPosition: return TravelPositionM;
                case PartParityMotionRules.ChannelRotation: return TravelRotationDeg;
                case PartParityMotionRules.ChannelScale: return TravelScale;
                default: return double.NaN;
            }
        }
    }

    /// <summary>One side's pose progress (H.1): the travel-weighted mean and every transform.</summary>
    internal sealed class PartParityMotionSideProgress
    {
        internal double[] Progress;
        internal readonly List<PartParityMotionTransformProgress> Transforms = new List<PartParityMotionTransformProgress>();
    }

    /// <summary>One transform's two per-frame arrays, as the sidecar carries them (H.2): the
    /// real fractions at the real frames, the ghost fractions on the scout series.</summary>
    internal sealed class PartParityMotionTransformSeries
    {
        internal string Path;
        internal double TravelReal = double.NaN;
        internal double TravelGhost = double.NaN;
        internal double[] Real;
        internal double[] Ghost;
    }

    /// <summary>The per-frame arrays the timing measures are a pure function of (H.1, H.2):
    /// the real frames' offsets and progress, the ghost's scout offsets and progress, the
    /// ghost's same-time progress at the real frames, the transforms, and for a servo both
    /// sides' positions at the real frames. Offsets are seconds since the press.</summary>
    internal sealed class PartParityMotionSeries
    {
        internal double[] RealS;
        internal double[] RealProgress;
        internal double[] ScoutS;
        internal double[] ScoutProgress;
        internal double[] GhostSameProgress;
        internal readonly List<PartParityMotionTransformSeries> Transforms = new List<PartParityMotionTransformSeries>();

        /// <summary>A servo's positions (deg or m) at the real frames; null for any other kind.</summary>
        internal double[] RealClock;
        internal double[] GhostClock;
    }

    /// <summary>The timing measures and flags of one case (H.1). Not measured is NaN.</summary>
    internal sealed class PartParityMotionTiming
    {
        /// <summary>t02, t10, t50, t90, t98 (<see cref="PartParityMotionRules.CrossingLevels"/>).</summary>
        internal double[] RealCrossings;
        internal double[] GhostCrossings;
        internal double LagS = double.NaN;
        internal double OnsetLagS = double.NaN;
        internal double EndLagS = double.NaN;
        internal double DurationRatio = double.NaN;
        internal double CurveShapeDev = double.NaN;
        internal double StaggerDev = double.NaN;

        /// <summary>The transform travel ratio farthest from 1 (ghost over real).</summary>
        internal double TravelRatio = double.NaN;
        internal double AngleMaxDevDeg = double.NaN;
        internal readonly List<string> Flags = new List<string>();

        /// <summary>The real motion's length, t98 - t02 (the lag rule's scale).</summary>
        internal double RealDurationS
            => RealCrossings == null ? double.NaN : RealCrossings[4] - RealCrossings[0];
    }

    /// <summary>A motion window (H.2): its frame targets, its end and whether the cap ended it.</summary>
    internal sealed class PartParityMotionWindow
    {
        internal readonly List<double> TargetsS = new List<double>();
        internal double EndS;
        internal double SpacingS;
        internal bool Capped;
    }

    /// <summary>
    /// The pure contract of the P7 motion cases (design-part-ghost-parity.md Appendix H, piece
    /// M1), mirrored by <c>harness/lib/partparity.py</c>: the families (P6's predicates through
    /// <see cref="PartParityStateRules.Enumerate"/>, plus the robotic servos and control
    /// surfaces by the module names their pollers read), the exclusions, the <c>motion=</c>
    /// words, the frame schedule, the progress and timing math, the control judges and every
    /// constant they read. Nothing here touches a live game object.
    /// </summary>
    internal static class PartParityMotionRules
    {
        // ----- families (H.1 table order) -----

        internal const string FamilyDeployable = PartParityStateRules.FamilyDeployable;
        internal const string FamilyGear = PartParityStateRules.FamilyGear;
        internal const string FamilyLadder = PartParityStateRules.FamilyLadder;
        internal const string FamilyAnimationGroup = PartParityStateRules.FamilyAnimationGroup;
        internal const string FamilyCargoBay = PartParityStateRules.FamilyCargoBay;
        internal const string FamilyAnimateGeneric = PartParityStateRules.FamilyAnimateGeneric;
        internal const string FamilyAirbrake = PartParityStateRules.FamilyAirbrake;
        internal const string FamilyParachute = PartParityStateRules.FamilyParachute;
        internal const string FamilyRobotics = "robotics";
        internal const string FamilyControlSurface = "control-surface";
        internal const string FamilyLight = PartParityStateRules.FamilyLight;
        internal const string FamilyCabinLight = PartParityStateRules.FamilyCabinLight;

        /// <summary>Every motion family, H.1 order; the last two are reading-only.</summary>
        internal static readonly string[] Families =
        {
            FamilyDeployable, FamilyGear, FamilyLadder, FamilyAnimationGroup, FamilyCargoBay,
            FamilyAnimateGeneric, FamilyAirbrake, FamilyParachute, FamilyRobotics, FamilyControlSurface,
            FamilyLight, FamilyCabinLight,
        };

        /// <summary>The P6 families whose state P7 reaches by motion (their P6 cases map to
        /// motion cases one to one).</summary>
        internal static readonly string[] P6MotionFamilies =
        {
            FamilyDeployable, FamilyGear, FamilyLadder, FamilyAnimationGroup, FamilyCargoBay,
            FamilyAnimateGeneric, FamilyAirbrake, FamilyParachute,
        };

        // ----- the motion= words (H.1) -----

        internal const string ArgLight = "light";
        internal const string ArgAllToken = "all";

        /// <summary><c>motion=all</c>: every judged family (never the reading-only lights).</summary>
        internal static readonly string[] ArgWords =
        {
            FamilyDeployable, FamilyGear, FamilyLadder, FamilyAnimationGroup, FamilyCargoBay,
            FamilyAnimateGeneric, FamilyAirbrake, FamilyParachute, FamilyRobotics, FamilyControlSurface,
        };

        /// <summary>Every word a <c>motion=</c> csv may name: the families and <c>light</c>
        /// (both light families, a reading run).</summary>
        internal static readonly string[] ArgKnownWords =
        {
            FamilyDeployable, FamilyGear, FamilyLadder, FamilyAnimationGroup, FamilyCargoBay,
            FamilyAnimateGeneric, FamilyAirbrake, FamilyParachute, FamilyRobotics, FamilyControlSurface,
            ArgLight,
        };

        /// <summary>The <c>motion=</c> word that selects a family.</summary>
        internal static string ArgForFamily(string family)
            => family == FamilyLight || family == FamilyCabinLight ? ArgLight : family;

        // ----- kinds, states and contexts (H.1, H.2) -----

        internal const string KindClip = "clip";
        internal const string KindServo = "servo";
        internal const string KindSurface = "surface";
        internal const string KindCanopy = "canopy";

        /// <summary>A light's brightness ramp (a reading run only).</summary>
        internal const string KindFade = "fade";

        internal static readonly string[] Kinds = { KindClip, KindServo, KindSurface, KindCanopy, KindFade };

        internal const string StatePrefix = "motion-";
        internal const string StateDeployed = "motion-deployed";
        internal const string StateOpen = "motion-open";
        internal const string StateSemi = "motion-semi";
        internal const string StateFull = "motion-full";
        internal const string StateSwept = "motion-swept";
        internal const string StateLit = "motion-lit";

        internal static readonly string[] States = { StateDeployed, StateOpen, StateSemi, StateFull, StateSwept, StateLit };

        /// <summary>A control surface on a hidden filler's radial node (H.2).</summary>
        internal const string ContextRadial = "radial";

        internal static readonly string[] OrbitContexts = { PartParityStateRules.ContextAlone, ContextRadial };

        /// <summary>The filler a control surface sits on, radially (H.2).</summary>
        internal const string RadialFillerPart = "fuelTankSmallFlat";

        /// <summary>The motion word of a P6 state word.</summary>
        internal static string StateFor(string p6State) => StatePrefix + (p6State ?? string.Empty);

        /// <summary>A motion case's state (any tiebreak suffix included).</summary>
        internal static bool IsMotionState(string state)
            => state != null && state.StartsWith(StatePrefix, StringComparison.Ordinal);

        // ----- the module names the two new families read -----

        internal const string TypeBaseServo = PartParityStateRules.TypeBaseServo;
        internal const string ModuleServoHinge = "ModuleRoboticServoHinge";
        internal const string ModuleServoPiston = "ModuleRoboticServoPiston";
        internal const string ModuleRotationServo = "ModuleRoboticRotationServo";
        internal const string ModuleServoRotor = "ModuleRoboticServoRotor";
        internal const string ModuleControlSurface = PartParityStateRules.ModuleControlSurface;
        internal const string ModuleRobotArmScanner = PartParityStateRules.ModuleRobotArmScanner;
        internal const string ModuleAnimationGroup = PartParityStateRules.ModuleAnimationGroup;
        internal const string ModuleGimbal = "ModuleGimbal";
        internal const string TypeGroundPart = PartParityStateRules.TypeGroundPart;

        /// <summary>The servo modules the recorder's robotic poll reads (exact names,
        /// <c>FlightRecorder.IsRoboticModuleName</c> less the rotor and the wheel modules).</summary>
        internal static readonly string[] ServoModules = { ModuleServoHinge, ModuleRotationServo, ModuleServoPiston };

        internal const string DriveTargetAngle = "targetAngle";
        internal const string DriveTargetExtension = "targetExtension";

        /// <summary>The control surface press (decompiled <c>ModuleControlSurface</c>: the
        /// KSPAction that sets <c>deploy</c>).</summary>
        internal const string ActionControlSurfaceDeploy = "ActionExtend";

        // ----- exclusions (H.1) -----

        internal const string ExcludeMotionPhysics = "motion-physics";
        internal const string ExcludeLightFade = "light-fade";
        internal const string ExcludeSynthesizedFromTrajectory = "synthesized-from-trajectory";
        internal const string ExcludeLoopPhase = "loop-phase";
        internal const string ExcludeGroundScan = "ground-scan";
        internal const string ExcludeRotorSpinNotPrimed = "rotor-spin-not-primed";

        /// <summary>A <c>BaseServo</c> the robotic poll does not read (a mod servo): P6's word.</summary>
        internal const string ExcludeNotPolled = PartParityStateRules.ExcludeNotPolled;

        /// <summary>Every exclusion reason a motion run writes, in order: P7's own, then the P6
        /// reasons a P6 motion family carries over (its case is missing for the P6 reason).</summary>
        internal static readonly string[] ExclusionReasons =
        {
            ExcludeMotionPhysics, ExcludeLightFade, ExcludeSynthesizedFromTrajectory, ExcludeLoopPhase,
            ExcludeGroundScan, ExcludeRotorSpinNotPrimed, ExcludeNotPolled,
            PartParityStateRules.ExcludeGroundDeploy, PartParityStateRules.ExcludeNoPose,
            PartParityStateRules.ExcludeFairingBay, PartParityStateRules.ExcludeBayDeployNotAnimated,
            PartParityStateRules.ExcludeNoAnimation, PartParityStateRules.ExcludeAutoDeploy,
        };

        // ----- the frame schedule (H.2) -----

        internal const double MinPredictedSeconds = 0.25;
        internal const double MaxPredictedSeconds = 30.0;

        /// <summary>Frames per predicted motion time (<c>frames=</c>, default 8: t_k = k T / 8).</summary>
        internal const int DefaultFramesPerMotion = 8;
        internal const int MinFramesPerMotion = 4;
        internal const int MaxFramesPerMotion = 16;

        /// <summary>The spacing doubles after every block of this many motion times of frames
        /// (32 intervals at the default 8, so the doubling starts past frame 33).</summary>
        internal const int DoublingBlockMotions = 4;

        internal const double WindowPadSeconds = 0.5;
        internal const double WindowCapSeconds = 40.0;

        /// <summary>A servo's own stop hold, its "expected run" after its last sample.</summary>
        internal const double ServoStopHoldSeconds = 0.25;

        /// <summary>The ghost's scout grid (H.2 step 1).</summary>
        internal const double ScoutStepSeconds = 0.05;

        /// <summary>Two scout offsets closer than this are the same point.</summary>
        internal const double ScoutMergeEpsilonSeconds = 1e-9;

        /// <summary>The framing box's growth over the union of the three boxes.</summary>
        internal const double FramingGrowFraction = 0.2;

        internal const int MotionTilePx = 256;

        /// <summary>The motion views when the step names no <c>views=</c> (H.2).</summary>
        internal static readonly string[] DefaultViews = { TestCommandPartParitySweep.ViewSideA, TestCommandPartParitySweep.ViewSideB };

        // ----- servos and surfaces (H.2) -----

        internal const double ServoSweepDeg = 45.0;
        internal const double PistonSweepFraction = 0.5;
        internal const double SurfaceStillDeg = PartParityStateRules.AirbrakeStillDeg;
        internal const double SurfaceStillWindowSeconds = PartParityStateRules.AirbrakeWindowSeconds;

        // ----- progress (H.1) -----

        /// <summary>A transform moving less than all three on a side is left out of its progress.</summary>
        internal const double TransformStillDeg = 0.5;
        internal const double TransformStillM = 0.001;
        internal const double TransformStillScale = 0.001;

        internal const string ChannelPosition = "position";
        internal const string ChannelRotation = "rotation";
        internal const string ChannelScale = "scale";
        internal static readonly string[] Channels = { ChannelPosition, ChannelRotation, ChannelScale };

        /// <summary>t02 (start), t10, t50, t90, t98 (end).</summary>
        internal static readonly double[] CrossingLevels = { 0.02, 0.10, 0.50, 0.90, 0.98 };

        /// <summary>The sidecar keys of the five crossings, in <see cref="CrossingLevels"/> order.</summary>
        internal static readonly string[] CrossingKeys = { "t02", "t10", "t50", "t90", "t98" };

        /// <summary><c>curveShapeDev</c> reads the real frames strictly inside this band.</summary>
        internal const double ShapeBandLow = 0.05;
        internal const double ShapeBandHigh = 0.95;

        /// <summary>A matched UT is refused across a scout interval that jumps more than this.</summary>
        internal const double MatchJumpMax = 0.25;

        /// <summary>A render's progress against the scout's at the same UT.</summary>
        internal const double ScoutRenderTolerance = 1e-4;

        // ----- flags (H.1, H.2) -----

        internal const double SnapLow = 0.1;
        internal const double SnapHigh = 0.9;
        internal const int SnapRealMinFrames = 3;
        internal const double StepRealRise = 0.05;
        internal const double StepStillEpsilon = 1e-4;
        internal const int StepMinHolds = 2;

        /// <summary>A hold counts only strictly inside this band (a late start or a snap's two
        /// flat ends are not steps).</summary>
        internal const double StepHoldLow = 0.02;
        internal const double StepHoldHigh = 0.98;
        internal const double NotModelledReal = 0.9;
        internal const double NotModelledGhost = 0.1;

        internal const string FlagMotionSnap = "motion-snap";
        internal const string FlagMotionStepped = "motion-stepped";
        internal const string FlagMotionNotModelled = "motion-not-modelled";
        internal const string FlagWindowCapped = "motion-window-capped";
        internal const string FlagClippedAtBorder = "motion-clipped-at-border";
        internal const string FlagFrameJitterPrefix = "frame-jitter:";
        internal const string FlagNoMatch = "no-match";
        internal const string FlagScoutRenderDisagree = "scout-render-disagree";
        internal const string FlagTravelDiffers = "travel-differs";
        internal const string FlagProgressDisagree = "progress-disagree";

        // ----- verdict thresholds (H.3; starting guesses, mirrored by partparity.py) -----

        internal const double LagFailMinSeconds = 0.5;
        internal const double LagFailRealFraction = 0.25;
        internal const double DurationRatioFailMin = 0.6;
        internal const double DurationRatioFailMax = 1.67;
        internal const double CurveShapeFail = 0.30;
        internal const double ServoAngleFailMinDeg = 5.0;
        internal const double ServoAngleFailSweepFraction = 0.15;
        internal const double DurationRatioWarnMin = 0.8;
        internal const double DurationRatioWarnMax = 1.25;

        /// <summary>A numeric line warns at this fraction of its distance from the ideal.</summary>
        internal const double WarnFraction = 0.5;

        internal const int MinFrames = 5;
        internal const double RealReachedProgress = 0.9;
        internal const double TravelDiffersMin = 0.9;
        internal const double TravelDiffersMax = 1.1;
        internal const int ProgressDisagreeFrames = 2;

        // ----- the motion controls (H.3) -----

        /// <summary>The real-vs-real and late controls' subject (P6's state control part).</summary>
        internal const string ControlPart = PartParitySweepPlan.ControlStatePart;

        /// <summary>The slow control's subject: a start-marked family (H.3).</summary>
        internal const string ControlSlowPart = "airbrake1";

        internal const string ControlAirPart = PartParitySweepPlan.ControlAirPart;

        /// <summary>The slow and late controls write two sidecars over the same real frames:
        /// the altered route under the control id, the normal route under the id plus this
        /// suffix, each with its own motion atlas (H.3; partparity.py MOTION_CONTROL_NORMAL_SUFFIX).</summary>
        internal const string NormalRouteSuffix = "--normal";

        /// <summary>Whether a control writes a normal-route sidecar beside its own.</summary>
        internal static bool HasNormalRoute(string controlId)
            => controlId == TestCommandPartParitySweep.ControlMotionGhostSlow
               || controlId == TestCommandPartParitySweep.ControlMotionGhostLate;

        internal static string NormalRouteCaseId(string controlId) => (controlId ?? string.Empty) + NormalRouteSuffix;

        internal const double LateShiftSeconds = 1.0;
        internal const double SlowClipFactor = 2.0;
        internal const double RealVsRealLagMotionFraction = 0.02;
        internal const double RealVsRealDurationTolerance = 0.05;
        internal const double RealVsRealCurveShapeMax = 0.05;
        internal const double SlowRatioMin = 1.9;
        internal const double SlowRatioMax = 2.1;
        internal const double LateLagMin = 0.98;
        internal const double LateLagMax = 1.02;
        internal const double LateDurationTolerance = 0.02;

        // ----- waves and counts -----

        internal const int MaxMotionWaveCases = PartParitySweepPlan.MaxStateWaveCases;

        /// <summary>The literal the per-step motion count line starts with.</summary>
        internal const string MotionCountsLiteral = "motion cases:";

        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // =====================================================================================
        // enumeration (H.1)
        // =====================================================================================

        /// <summary>
        /// The motion cases of one part and what it leaves out (H.1), at <paramref name="baseKey"/>'s
        /// part, source and base skin (ruling R2). Every P6 case of a P6 motion family maps to
        /// one motion case (its P6 option kept: press, done signal, timeout), the P6 exclusions
        /// of those families carry over with their reason; P6's lights are <c>light-fade</c>
        /// (and reading-only cases). Then: every module implementing <c>ModuleJettison</c> or
        /// <c>ModuleProceduralFairing</c> (P6's two physics families' predicates) is
        /// <c>motion-physics</c>;
        /// every servo module the robotic poll reads (exact name) is one <c>motion-swept</c>
        /// case, a rotor <c>rotor-spin-not-primed</c>, any other <c>BaseServo</c>
        /// <c>not-polled</c>; every <c>ModuleControlSurface</c> (exact name, PollControlSurfaces)
        /// one <c>motion-deployed</c> case in the radial context, and also
        /// <c>synthesized-from-trajectory</c> for its flight deflection, as every
        /// <c>ModuleGimbal</c> is; every <c>ModuleRobotArmScanner</c> <c>ground-scan</c>; every
        /// in-orbit <c>ModuleAnimationGroup</c> <c>loop-phase</c> for its running loop.
        /// </summary>
        internal static PartParityMotionEnumeration Enumerate(PartParityStatePart part, PartParityCaseKey baseKey)
        {
            var r = new PartParityMotionEnumeration();
            if (part == null || part.Modules == null) return r;
            string name = part.Name ?? baseKey?.Part;
            PartParityStateEnumeration p6 = PartParityStateRules.Enumerate(part, baseKey);
            var cases = new List<PartParityMotionOption>();
            foreach (PartParityCaseKey k in p6.Cases)
            {
                PartParityStateOption s = k.StateOption;
                if (s == null) continue;
                if (s.Family == PartParityStateRules.FamilyJettison || s.Family == PartParityStateRules.FamilyFairing) continue;
                if (s.Family == FamilyLight || s.Family == FamilyCabinLight)
                {
                    r.Exclusions.Add(Ex(name, s.Family, s.Module, s.ModuleIndex, ExcludeLightFade, PartParityStateRules.ContextAlone));
                    cases.Add(FromP6(s, KindFade, PartParityStateRules.ContextAlone, true));
                    continue;
                }
                if (Array.IndexOf(P6MotionFamilies, s.Family) < 0) continue;
                if (s.Family == FamilyAnimationGroup && HasNoDeployAnimation(ModuleAt(part, s.ModuleIndex)))
                {
                    // Its deploy plays no clip (stock flips isDeployed at once and only starts the
                    // running loop, which loop-phase excludes): there is no deploy motion to time.
                    r.Exclusions.Add(Ex(name, s.Family, s.Module, s.ModuleIndex, PartParityStateRules.ExcludeNoAnimation,
                        PartParityStateRules.ContextAlone));
                    continue;
                }
                string kind = s.Family == FamilyAirbrake ? KindSurface
                    : s.Family == FamilyParachute ? KindCanopy : KindClip;
                cases.Add(FromP6(s, kind, k.Context, false));
            }
            foreach (PartParityStateExclusion e in p6.Exclusions)
            {
                if (e == null || Array.IndexOf(P6MotionFamilies, e.Family) < 0) continue;
                if (Array.IndexOf(ExclusionReasons, e.Reason) < 0) continue;
                r.Exclusions.Add(Ex(name, e.Family, e.Module, e.ModuleIndex, e.Reason,
                    e.Family == FamilyParachute ? PartParityStateRules.ContextAir : PartParityStateRules.ContextAlone));
            }

            bool groundPart = false;
            foreach (PartParityStateModule m in part.Modules)
                if (m != null && m.Implements(TypeGroundPart)) groundPart = true;
            foreach (PartParityStateModule m in part.Modules)
            {
                if (m == null) continue;
                if (m.Implements(PartParityStateRules.TypeJettison) || m.Implements(PartParityStateRules.TypeProceduralFairing))
                    r.Exclusions.Add(Ex(name,
                        m.Implements(PartParityStateRules.TypeProceduralFairing) ? PartParityStateRules.FamilyFairing : PartParityStateRules.FamilyJettison,
                        m.Name, m.Index, ExcludeMotionPhysics, PartParityStateRules.ContextAlone));
                if (Array.IndexOf(ServoModules, m.Name) >= 0)
                    cases.Add(new PartParityMotionOption
                    {
                        Family = FamilyRobotics, Kind = KindServo, Module = m.Name, ModuleIndex = m.Index,
                        State = StateSwept, Context = PartParityStateRules.ContextAlone,
                        DriveField = m.Named(ModuleServoPiston) ? DriveTargetExtension : DriveTargetAngle,
                    });
                else if (m.Named(ModuleServoRotor))
                    r.Exclusions.Add(Ex(name, FamilyRobotics, m.Name, m.Index, ExcludeRotorSpinNotPrimed, PartParityStateRules.ContextAlone));
                else if (m.Implements(TypeBaseServo))
                    r.Exclusions.Add(Ex(name, FamilyRobotics, m.Name, m.Index, ExcludeNotPolled, PartParityStateRules.ContextAlone));

                if (m.Named(ModuleControlSurface))
                {
                    cases.Add(new PartParityMotionOption
                    {
                        Family = FamilyControlSurface, Kind = KindSurface, Module = m.Name, ModuleIndex = m.Index,
                        State = StateDeployed, Context = ContextRadial,
                        Action = ActionControlSurfaceDeploy, ActionIsKspAction = true,
                    });
                    r.Exclusions.Add(Ex(name, null, m.Name, m.Index, ExcludeSynthesizedFromTrajectory, PartParityStateRules.ContextAlone));
                }
                else if (m.Named(ModuleGimbal))
                    r.Exclusions.Add(Ex(name, null, m.Name, m.Index, ExcludeSynthesizedFromTrajectory, PartParityStateRules.ContextAlone));
                else if (m.Named(ModuleRobotArmScanner))
                    r.Exclusions.Add(Ex(name, null, m.Name, m.Index, ExcludeGroundScan, PartParityStateRules.ContextAlone));
                else if (m.Named(ModuleAnimationGroup) && !groundPart)
                    r.Exclusions.Add(Ex(name, null, m.Name, m.Index, ExcludeLoopPhase, PartParityStateRules.ContextAlone));
            }
            BuildKeys(cases, baseKey, name, r);
            return r;
        }

        /// <summary>The config key of a <c>ModuleAnimationGroup</c>'s deploy clip (stock
        /// default empty: no deploy clip).</summary>
        internal const string KeyDeployAnimationName = "deployAnimationName";

        private static PartParityStateModule ModuleAt(PartParityStatePart part, int index)
        {
            if (part?.Modules == null) return null;
            foreach (PartParityStateModule m in part.Modules)
                if (m != null && m.Index == index) return m;
            return null;
        }

        /// <summary>
        /// Whether a <c>ModuleAnimationGroup</c> plays no deploy clip: its config node names an
        /// empty or no <c>deployAnimationName</c> (stock's default is empty; the orbital scanner
        /// names only its running <c>activeAnimationName</c>). A module with no config node read
        /// (a committed fixture line that carries no key) keeps its case.
        /// </summary>
        internal static bool HasNoDeployAnimation(PartParityStateModule m)
            => m?.Config != null && string.IsNullOrEmpty(m.Value(KeyDeployAnimationName));

        private static PartParityMotionOption FromP6(PartParityStateOption s, string kind, string context, bool readingOnly)
            => new PartParityMotionOption
            {
                Family = s.Family, Kind = kind, Module = s.Module, ModuleIndex = s.ModuleIndex,
                State = StateFor(s.State), Context = context, Action = s.Action,
                ActionIsKspAction = s.ActionIsKspAction, StateOption = s, ReadingOnly = readingOnly,
            };

        private static PartParityMotionExclusion Ex(string part, string family, string module, int index,
            string reason, string context)
            => new PartParityMotionExclusion
            {
                Part = part, Family = family, Module = module, ModuleIndex = index, Reason = reason, Context = context,
            };

        /// <summary>The case keys, the same-word tiebreak of D.1 applied over (word, context).</summary>
        private static void BuildKeys(List<PartParityMotionOption> cases, PartParityCaseKey baseKey, string name,
            PartParityMotionEnumeration r)
        {
            string part = baseKey?.Part ?? name;
            string source = baseKey?.Source;
            string skin = baseKey?.Skin ?? TestCommandPartParitySweep.DefaultSkinName;
            foreach (PartParityMotionOption o in cases)
            {
                int same = 0;
                foreach (PartParityMotionOption other in cases)
                    if (other.State == o.State && other.Context == o.Context) same++;
                r.Cases.Add(new PartParityCaseKey
                {
                    Part = part, Source = source, Skin = skin, SkinOrdinal = 0,
                    State = same > 1 ? PartParityStateRules.TiebreakWord(o.State, o.ModuleIndex) : o.State,
                    Context = o.Context, MotionOption = o,
                });
            }
        }

        /// <summary>Whether a motion key belongs to a run: its family's word is one the run's
        /// <c>motion=</c> words name (a reading-only light by <c>light</c> only, which
        /// <c>all</c> never holds), and its context is the air context exactly when the run is
        /// an air run.</summary>
        internal static bool KeySelected(PartParityCaseKey key, IList<string> words, bool airRun)
        {
            PartParityMotionOption o = key?.MotionOption;
            if (o == null || words == null) return false;
            if (!words.Contains(ArgForFamily(o.Family))) return false;
            bool air = string.Equals(o.Context, PartParityStateRules.ContextAir, StringComparison.Ordinal);
            return air == airRun;
        }

        /// <summary>Whether an exclusion belongs in a run's list: an air run lists only the
        /// exclusions of air cases, an orbit run every other one; a <c>light-fade</c> is not
        /// listed on a run that names <c>light</c> (it plans those cases).</summary>
        internal static bool ExclusionApplies(PartParityMotionExclusion e, IList<string> words, bool airRun)
        {
            if (e == null) return false;
            if (string.Equals(e.Context, PartParityStateRules.ContextAir, StringComparison.Ordinal) != airRun) return false;
            return !(e.Reason == ExcludeLightFade && words != null && words.Contains(ArgLight));
        }

        /// <summary>
        /// <c>motion cases: motion=&lt;..&gt; contexts=&lt;..&gt; install=&lt;n&gt; (&lt;family&gt;=&lt;n&gt; ...)
        /// planned=&lt;n&gt; excluded=&lt;n&gt; (&lt;reason&gt;=&lt;n&gt; ...)</c>, families and reasons in
        /// their table order, zero counts left out.
        /// </summary>
        internal static string FormatCounts(string motion, string contexts, IEnumerable<PartParityCaseKey> install,
            int planned, IEnumerable<PartParityMotionExclusion> excluded)
        {
            var byFamily = new Dictionary<string, int>(StringComparer.Ordinal);
            int total = 0;
            if (install != null)
                foreach (var k in install)
                {
                    if (k?.MotionOption == null) continue;
                    total++;
                    Bump(byFamily, k.MotionOption.Family);
                }
            var byReason = new Dictionary<string, int>(StringComparer.Ordinal);
            int ex = 0;
            if (excluded != null)
                foreach (var e in excluded)
                {
                    if (e == null) continue;
                    ex++;
                    Bump(byReason, e.Reason);
                }
            var sb = new StringBuilder(MotionCountsLiteral);
            sb.Append(" motion=").Append(motion ?? TestCommandPartParitySweep.ArgNoneToken)
                .Append(" contexts=").Append(contexts ?? TestCommandPartParitySweep.ArgNoneToken)
                .Append(" install=").Append(total.ToString(Ic)).Append(" (");
            AppendCounts(sb, Families, byFamily);
            sb.Append(") planned=").Append(planned.ToString(Ic))
                .Append(" excluded=").Append(ex.ToString(Ic)).Append(" (");
            AppendCounts(sb, ExclusionReasons, byReason);
            return sb.Append(')').ToString();
        }

        private static void Bump(Dictionary<string, int> d, string key)
        {
            d.TryGetValue(key ?? string.Empty, out int n);
            d[key ?? string.Empty] = n + 1;
        }

        private static void AppendCounts(StringBuilder sb, string[] order, Dictionary<string, int> counts)
        {
            bool first = true;
            foreach (string k in order)
            {
                if (!counts.TryGetValue(k, out int n) || n == 0) continue;
                if (!first) sb.Append(' ');
                sb.Append(k).Append('=').Append(n.ToString(Ic));
                first = false;
            }
        }

        // =====================================================================================
        // the frame schedule (H.2)
        // =====================================================================================

        /// <summary>T clamped to [<see cref="MinPredictedSeconds"/>, <see cref="MaxPredictedSeconds"/>];
        /// a reading that is not a finite positive time is the maximum (the widest window).</summary>
        internal static double ClampPredictedSeconds(double t)
        {
            if (double.IsNaN(t) || double.IsInfinity(t) || t <= 0.0) return MaxPredictedSeconds;
            return t < MinPredictedSeconds ? MinPredictedSeconds : (t > MaxPredictedSeconds ? MaxPredictedSeconds : t);
        }

        /// <summary>A clip's predicted time, its <c>AnimationState</c> <c>length / |speed|</c>.</summary>
        internal static double ClipSeconds(double length, double speed)
            => speed == 0.0 ? double.NaN : length / Math.Abs(speed);

        /// <summary>A servo's, <c>|target - start| / traverseVelocity</c>.</summary>
        internal static double ServoSeconds(double start, double target, double traverseVelocity)
            => traverseVelocity <= 0.0 ? double.NaN : Math.Abs(target - start) / traverseVelocity;

        /// <summary>An airbrake's or control surface's, <c>deployAngle / actuatorSpeed</c>.</summary>
        internal static double SurfaceSeconds(double deployAngleDeg, double actuatorSpeedDegPerS)
            => actuatorSpeedDegPerS <= 0.0 ? double.NaN : Math.Abs(deployAngleDeg) / actuatorSpeedDegPerS;

        /// <summary>A servo's target (H.2): toward its upper limit by <see cref="ServoSweepDeg"/>
        /// (a piston by <see cref="PistonSweepFraction"/> of its travel), stopping at the
        /// limit; from a rest at the upper limit, the same toward the lower one.</summary>
        internal static double ServoTarget(bool piston, double rest, double min, double max)
        {
            double step = piston ? PistonSweepFraction * Math.Abs(max - min) : ServoSweepDeg;
            if (max - rest > 1e-9) return Math.Min(rest + step, max);
            return Math.Max(rest - step, min);
        }

        /// <summary>A servo is done when it stopped moving within its deadband of the target.</summary>
        internal static bool ServoDone(bool moving, double position, double target, double deadband)
            => !moving && Math.Abs(position - target) <= Math.Abs(deadband);

        /// <summary>The frame spacing at the start of the window, T / frames.</summary>
        internal static double SpacingSeconds(double predictedS, int framesPerMotion)
            => ClampPredictedSeconds(predictedS) / Math.Max(1, framesPerMotion);

        /// <summary>
        /// Frame k's target offset from the press: <c>k T / n</c> for the first block of
        /// <c>DoublingBlockMotions x n</c> intervals (frame 0 is the press frame), then each
        /// further block at twice the previous block's spacing.
        /// </summary>
        internal static double FrameTargetSeconds(int k, double predictedS, int framesPerMotion)
        {
            if (k <= 0) return 0.0;
            double s = SpacingSeconds(predictedS, framesPerMotion);
            int block = DoublingBlockMotions * Math.Max(1, framesPerMotion);
            int m = k <= block ? 0 : (k - 1) / block;
            double blockStart = block * s * (Math.Pow(2.0, m) - 1.0);
            return blockStart + (k - m * block) * s * Math.Pow(2.0, m);
        }

        /// <summary>The window's end (H.2): the real done signal plus the ghost's own expected
        /// run plus <see cref="WindowPadSeconds"/> plus a control route's own change, capped at
        /// <see cref="WindowCapSeconds"/>.</summary>
        internal static double WindowEndSeconds(double realDoneS, double ghostExpectedRunS, double controlExtraS, out bool capped)
        {
            double end = Finite(realDoneS) + Finite(ghostExpectedRunS) + WindowPadSeconds + Finite(controlExtraS);
            capped = end > WindowCapSeconds;
            return capped ? WindowCapSeconds : end;
        }

        private static double Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) || v < 0.0 ? 0.0 : v;

        /// <summary>
        /// The frame targets of a window (H.2): <see cref="FrameTargetSeconds"/> for k = 0, 1, ...
        /// while the target is at or before the window's end.
        /// </summary>
        internal static PartParityMotionWindow PlanFrameTargets(double predictedS, double realDoneS,
            double ghostExpectedRunS, double controlExtraS, int framesPerMotion)
        {
            var w = new PartParityMotionWindow
            {
                EndS = WindowEndSeconds(realDoneS, ghostExpectedRunS, controlExtraS, out bool capped),
                SpacingS = SpacingSeconds(predictedS, framesPerMotion),
                Capped = capped,
            };
            for (int k = 0; ; k++)
            {
                double t = FrameTargetSeconds(k, predictedS, framesPerMotion);
                if (t > w.EndS + 1e-9) break;
                w.TargetsS.Add(t);
            }
            return w;
        }

        /// <summary>The <c>frame-jitter:&lt;ms&gt;</c> tag of a frame taken later than half a
        /// frame interval after its target (T / 16 at the default 8), else null.</summary>
        internal static string FrameJitterTag(double targetS, double actualS, double predictedS, int framesPerMotion)
        {
            double late = actualS - targetS;
            double allowed = SpacingSeconds(predictedS, framesPerMotion) / 2.0;
            if (!(late > allowed)) return null;
            return FlagFrameJitterPrefix + ((int)Math.Round(late * 1000.0, MidpointRounding.AwayFromZero)).ToString(Ic);
        }

        /// <summary>
        /// The scout offsets (H.2 step 1): a <see cref="ScoutStepSeconds"/> grid from the press
        /// to the window's end, merged with every real frame's own offset, increasing, each once.
        /// </summary>
        internal static List<double> PlanScoutOffsets(double windowEndS, IList<double> realOffsetsS)
        {
            var all = new List<double>();
            int n = (int)Math.Floor(windowEndS / ScoutStepSeconds + 1e-9);
            for (int i = 0; i <= n; i++) all.Add(i * ScoutStepSeconds);
            if (realOffsetsS != null)
                foreach (double s in realOffsetsS)
                    if (!double.IsNaN(s) && !double.IsInfinity(s)) all.Add(s);
            all.Sort();
            var merged = new List<double>(all.Count);
            foreach (double s in all)
                if (merged.Count == 0 || s - merged[merged.Count - 1] > ScoutMergeEpsilonSeconds) merged.Add(s);
            return merged;
        }

        /// <summary>
        /// The matched offset of a real frame's progress on the scout series (H.2 step 2): the
        /// first scout point that reaches <paramref name="target"/>, interpolated inside the
        /// interval that brackets it; at 0 the scout's first point, at 1 the first point that
        /// reaches 1. NaN when the ghost never reaches it, or when the bracketing interval jumps
        /// by more than <see cref="MatchJumpMax"/> (a snap or a servo step).
        /// </summary>
        internal static double MatchedOffset(IList<double> scoutS, IList<double> scoutP, double target)
        {
            if (scoutS == null || scoutP == null || scoutS.Count == 0 || scoutS.Count != scoutP.Count) return double.NaN;
            if (target <= 0.0) return scoutS[0];
            for (int k = 0; k < scoutP.Count; k++)
            {
                if (scoutP[k] < target) continue;
                if (k == 0) return scoutS[0];
                double dp = scoutP[k] - scoutP[k - 1];
                if (dp > MatchJumpMax) return double.NaN;
                if (target >= 1.0) return scoutS[k];
                return scoutS[k - 1] + (target - scoutP[k - 1]) * (scoutS[k] - scoutS[k - 1]) / dp;
            }
            return double.NaN;
        }

        /// <summary>A render agrees with its scout (H.2 step 3): a same-time render within
        /// <see cref="ScoutRenderTolerance"/> of the scout at that UT, a matched one within the
        /// scout's own change across the interval it was interpolated in.</summary>
        internal static bool RenderAgrees(bool sameTime, double renderProgress, double expected, double intervalChange)
        {
            if (double.IsNaN(renderProgress) || double.IsNaN(expected)) return false;
            double allowed = sameTime ? ScoutRenderTolerance : Math.Max(ScoutRenderTolerance, Math.Abs(intervalChange));
            return Math.Abs(renderProgress - expected) <= allowed;
        }

        // =====================================================================================
        // progress (H.1)
        // =====================================================================================

        /// <summary>The angle between two rotations, degrees, the short way (a frame-to-frame
        /// step is far under 180 degrees, so the accumulated travel is not folded back).</summary>
        internal static double RotationStepDeg(double[] a, double[] b)
        {
            double dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
            double sign = dot < 0.0 ? -1.0 : 1.0;
            double dx = 0.0, sx = 0.0;
            for (int i = 0; i < 4; i++)
            {
                double d = a[i] - sign * b[i];
                double s = a[i] + sign * b[i];
                dx += d * d;
                sx += s * s;
            }
            return 4.0 * Math.Atan2(Math.Sqrt(dx), Math.Sqrt(sx)) * 180.0 / Math.PI;
        }

        private static double Distance(double[] a, double[] b)
        {
            double d = 0.0;
            for (int i = 0; i < a.Length; i++) d += (a[i] - b[i]) * (a[i] - b[i]);
            return Math.Sqrt(d);
        }

        /// <summary>
        /// One transform's travel (H.1): position, rotation and scale accumulated frame to
        /// frame; the channel that moved most against its still threshold; its fraction of that
        /// travel on every frame (0 at frame 0, 1 at the last). Null channel when it moved less
        /// than every threshold.
        /// </summary>
        internal static PartParityMotionTransformProgress TransformProgress(PartParityMotionTrack track)
        {
            var t = new PartParityMotionTransformProgress { Path = track?.Path };
            int n = track?.Poses?.Count ?? 0;
            var cum = new double[3][];
            for (int c = 0; c < 3; c++) cum[c] = new double[n];
            for (int k = 1; k < n; k++)
            {
                PartParityMotionPose a = track.Poses[k - 1], b = track.Poses[k];
                cum[0][k] = cum[0][k - 1] + Distance(a.Position, b.Position);
                cum[1][k] = cum[1][k - 1] + RotationStepDeg(a.Rotation, b.Rotation);
                cum[2][k] = cum[2][k - 1] + Distance(a.Scale, b.Scale);
            }
            if (n > 0)
            {
                t.TravelPositionM = cum[0][n - 1];
                t.TravelRotationDeg = cum[1][n - 1];
                t.TravelScale = cum[2][n - 1];
            }
            double[] still = { TransformStillM, TransformStillDeg, TransformStillScale };
            double[] total = { t.TravelPositionM, t.TravelRotationDeg, t.TravelScale };
            int best = -1;
            double bestNorm = 1.0;
            for (int c = 0; c < 3; c++)
            {
                double norm = total[c] / still[c];
                if (norm >= bestNorm && (best < 0 || norm > total[best] / still[best])) { best = c; bestNorm = norm; }
            }
            t.Fraction = new double[n];
            if (best < 0) return t;
            t.Channel = Channels[best];
            t.Weight = total[best] / still[best];
            for (int k = 0; k < n; k++) t.Fraction[k] = cum[best][k] / total[best];
            return t;
        }

        /// <summary>
        /// One side's pose progress (H.1): every transform's fraction, the side's progress the
        /// weighted mean of the transforms that moved (weight: travel over the still
        /// threshold), each side normalised by its own frame 0 and last pose. All zero when no
        /// transform moved.
        /// </summary>
        internal static PartParityMotionSideProgress SideProgress(IList<PartParityMotionTrack> tracks, int frames)
        {
            var side = new PartParityMotionSideProgress { Progress = new double[Math.Max(0, frames)] };
            if (tracks == null) return side;
            double weights = 0.0;
            foreach (PartParityMotionTrack track in tracks)
            {
                PartParityMotionTransformProgress t = TransformProgress(track);
                side.Transforms.Add(t);
                if (t.Channel == null || t.Fraction.Length != side.Progress.Length) continue;
                weights += t.Weight;
                for (int k = 0; k < side.Progress.Length; k++) side.Progress[k] += t.Weight * t.Fraction[k];
            }
            if (weights > 0.0)
                for (int k = 0; k < side.Progress.Length; k++) side.Progress[k] /= weights;
            return side;
        }

        /// <summary>Picture progress (H.1): <c>a / (a + b)</c>, 0 when both are 0.</summary>
        internal static double PictureProgress(double mismatchToFirst, double mismatchToLast)
        {
            double sum = mismatchToFirst + mismatchToLast;
            return sum > 0.0 ? mismatchToFirst / sum : 0.0;
        }

        // =====================================================================================
        // timing (H.1): pure over the per-frame arrays, recomputed by partparity.py
        // =====================================================================================

        /// <summary>The first time <paramref name="p"/> reaches <paramref name="level"/>,
        /// linearly interpolated between the two points that bracket it; NaN when never.</summary>
        internal static double Crossing(IList<double> s, IList<double> p, double level)
        {
            if (s == null || p == null || s.Count == 0 || s.Count != p.Count) return double.NaN;
            for (int k = 0; k < p.Count; k++)
            {
                if (!(p[k] >= level)) continue;
                if (k == 0) return s[0];
                return s[k - 1] + (level - p[k - 1]) * (s[k] - s[k - 1]) / (p[k] - p[k - 1]);
            }
            return double.NaN;
        }

        internal static double[] Crossings(IList<double> s, IList<double> p)
        {
            var c = new double[CrossingLevels.Length];
            for (int i = 0; i < c.Length; i++) c[i] = Crossing(s, p, CrossingLevels[i]);
            return c;
        }

        /// <summary>A series' value at <paramref name="at"/>, linear between its points, its
        /// first and last value outside them.</summary>
        internal static double Interpolate(IList<double> s, IList<double> p, double at)
        {
            if (s == null || p == null || s.Count == 0 || s.Count != p.Count) return double.NaN;
            if (at <= s[0]) return p[0];
            for (int k = 1; k < s.Count; k++)
            {
                if (at > s[k]) continue;
                double span = s[k] - s[k - 1];
                return span > 0.0 ? p[k - 1] + (at - s[k - 1]) * (p[k] - p[k - 1]) / span : p[k];
            }
            return p[p.Count - 1];
        }

        /// <summary>The largest <c>|ghost(t + lag) - real(t)|</c> over the real points strictly
        /// inside the shape band; NaN when the lag is not measured or no point is inside.</summary>
        internal static double ShapeDeviation(IList<double> realS, IList<double> realP,
            IList<double> ghostS, IList<double> ghostP, double lagS)
        {
            if (double.IsNaN(lagS) || realS == null || realP == null || realS.Count != realP.Count) return double.NaN;
            double worst = double.NaN;
            for (int k = 0; k < realS.Count; k++)
            {
                if (!(realP[k] > ShapeBandLow && realP[k] < ShapeBandHigh)) continue;
                double d = Math.Abs(Interpolate(ghostS, ghostP, realS[k] + lagS) - realP[k]);
                if (double.IsNaN(worst) || d > worst) worst = d;
            }
            return worst;
        }

        private static double Diff(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : a - b;

        private static double Ratio(double num, double den)
            => double.IsNaN(num) || double.IsNaN(den) || den <= 0.0 ? double.NaN : num / den;

        /// <summary>
        /// The timing measures and flags of one case (H.1), a pure function of
        /// <paramref name="s"/>: both sides' crossings, <c>lagS</c>, <c>onsetLagS</c>,
        /// <c>endLagS</c>, <c>durationRatio</c>, <c>curveShapeDev</c>, <c>staggerDev</c>,
        /// <c>travelRatio</c>, <c>angleMaxDevDeg</c> (servos), and the flags
        /// <c>motion-not-modelled</c>, <c>motion-snap</c>, <c>motion-stepped</c> and the
        /// report-only <c>travel-differs</c>.
        /// </summary>
        internal static PartParityMotionTiming Timing(PartParityMotionSeries s)
        {
            var t = new PartParityMotionTiming();
            if (s == null) return t;
            t.RealCrossings = Crossings(s.RealS, s.RealProgress);
            t.GhostCrossings = Crossings(s.ScoutS, s.ScoutProgress);
            t.LagS = Diff(t.GhostCrossings[2], t.RealCrossings[2]);
            t.OnsetLagS = Diff(t.GhostCrossings[0], t.RealCrossings[0]);
            t.EndLagS = Diff(t.GhostCrossings[4], t.RealCrossings[4]);
            t.DurationRatio = Ratio(Diff(t.GhostCrossings[3], t.GhostCrossings[1]), Diff(t.RealCrossings[3], t.RealCrossings[1]));
            t.CurveShapeDev = ShapeDeviation(s.RealS, s.RealProgress, s.ScoutS, s.ScoutProgress, t.LagS);

            double travelRatio = double.NaN;
            foreach (PartParityMotionTransformSeries tr in s.Transforms)
            {
                if (tr == null) continue;
                double d = ShapeDeviation(s.RealS, tr.Real, s.ScoutS, tr.Ghost, t.LagS);
                if (!double.IsNaN(d) && (double.IsNaN(t.StaggerDev) || d > t.StaggerDev)) t.StaggerDev = d;
                double r = Ratio(tr.TravelGhost, tr.TravelReal);
                if (!double.IsNaN(r) && (double.IsNaN(travelRatio) || Math.Abs(r - 1.0) > Math.Abs(travelRatio - 1.0))) travelRatio = r;
            }
            t.TravelRatio = travelRatio;
            if (!double.IsNaN(travelRatio) && (travelRatio < TravelDiffersMin || travelRatio > TravelDiffersMax))
                t.Flags.Add(FlagTravelDiffers);
            t.AngleMaxDevDeg = MaxAbsDifference(s.RealClock, s.GhostClock);

            if (NotModelled(s.RealProgress, s.ScoutProgress)) t.Flags.Add(FlagMotionNotModelled);
            if (Snapped(s.RealProgress, s.GhostSameProgress)) t.Flags.Add(FlagMotionSnap);
            if (Stepped(s.RealProgress, s.GhostSameProgress)) t.Flags.Add(FlagMotionStepped);
            return t;
        }

        private static double Max(IList<double> v)
        {
            double m = double.NaN;
            if (v == null) return m;
            foreach (double x in v) if (!double.IsNaN(x) && (double.IsNaN(m) || x > m)) m = x;
            return m;
        }

        /// <summary><c>motion-not-modelled</c>: the real reaches 0.9 and the ghost never 0.1.</summary>
        internal static bool NotModelled(IList<double> realP, IList<double> ghostP)
        {
            double r = Max(realP), g = Max(ghostP);
            return r >= NotModelledReal && (double.IsNaN(g) || g < NotModelledGhost);
        }

        /// <summary><c>motion-snap</c>: the ghost rises from under 0.1 to over 0.9 between two
        /// adjacent frames while the real takes <see cref="SnapRealMinFrames"/> frames or more
        /// (from its last frame under 0.1 to its first over 0.9).</summary>
        internal static bool Snapped(IList<double> realP, IList<double> ghostP)
        {
            if (realP == null || ghostP == null || realP.Count != ghostP.Count) return false;
            int high = -1;
            for (int k = 0; k < realP.Count; k++) if (realP[k] > SnapHigh) { high = k; break; }
            if (high < 0) return false;
            int low = -1;
            for (int k = high - 1; k >= 0; k--) if (realP[k] < SnapLow) { low = k; break; }
            if (low < 0 || high - low < SnapRealMinFrames) return false;
            for (int k = 0; k + 1 < ghostP.Count; k++)
                if (ghostP[k] < SnapLow && ghostP[k + 1] > SnapHigh) return true;
            return false;
        }

        /// <summary><c>motion-stepped</c>: <see cref="StepMinHolds"/> or more separate holds,
        /// each a run of adjacent frames over which the ghost stays still (within
        /// <see cref="StepStillEpsilon"/>, strictly inside the hold band) while the real rises
        /// more than <see cref="StepRealRise"/> per frame.</summary>
        internal static bool Stepped(IList<double> realP, IList<double> ghostP)
        {
            if (realP == null || ghostP == null || realP.Count != ghostP.Count) return false;
            int holds = 0;
            bool inHold = false;
            for (int k = 0; k + 1 < ghostP.Count; k++)
            {
                bool still = Math.Abs(ghostP[k + 1] - ghostP[k]) <= StepStillEpsilon
                             && ghostP[k] > StepHoldLow && ghostP[k] < StepHoldHigh
                             && realP[k + 1] - realP[k] > StepRealRise;
                if (still && !inHold) holds++;
                inHold = still;
            }
            return holds >= StepMinHolds;
        }

        /// <summary>The largest <c>|a - b|</c> over the points both carry; NaN when none.</summary>
        internal static double MaxAbsDifference(IList<double> a, IList<double> b)
        {
            if (a == null || b == null) return double.NaN;
            double m = double.NaN;
            for (int k = 0; k < Math.Min(a.Count, b.Count); k++)
            {
                double d = Math.Abs(a[k] - b[k]);
                if (!double.IsNaN(d) && (double.IsNaN(m) || d > m)) m = d;
            }
            return m;
        }

        /// <summary><c>progress-disagree</c> (an instrument hint): the pose and the picture
        /// progress t50 differ by more than <see cref="ProgressDisagreeFrames"/> frame intervals.</summary>
        internal static bool ProgressDisagrees(double poseT50, double pictureT50, double frameIntervalS)
            => !double.IsNaN(poseT50) && !double.IsNaN(pictureT50)
               && Math.Abs(poseT50 - pictureT50) > ProgressDisagreeFrames * frameIntervalS;

        // ----- the fail lines the control judges read (H.3) -----

        /// <summary>A lag the verdict fails: <c>|lagS| &gt; max(0.5 s, 0.25 T_real)</c>.</summary>
        internal static bool LagFails(double lagS, double realDurationS)
            => !double.IsNaN(lagS)
               && Math.Abs(lagS) > Math.Max(LagFailMinSeconds, LagFailRealFraction * (double.IsNaN(realDurationS) ? 0.0 : realDurationS));

        /// <summary>A duration ratio the verdict fails: outside [0.6, 1.67].</summary>
        internal static bool DurationFails(double ratio)
            => !double.IsNaN(ratio) && (ratio < DurationRatioFailMin || ratio > DurationRatioFailMax);

        // =====================================================================================
        // controls (H.3)
        // =====================================================================================

        internal static string ControlSubjectWords(string controlId)
        {
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlMotionRealVsReal:
                    return ControlPart + " x2 in one wave, pressed in the same frame, copy B's live frames as the ghost rows";
                case TestCommandPartParitySweep.ControlMotionGhostSlow:
                    return ControlSlowPart + " at motion-deployed, a second event route with its built clip length doubled";
                case TestCommandPartParitySweep.ControlMotionGhostLate:
                    return ControlPart + " at motion-deployed, a second event route with every polled event 1.0 s later";
                case TestCommandPartParitySweep.ControlMotionRealVsRealAir:
                    return ControlAirPart + " x2 in the air, pressed in the same frame, the canopy-scale curves only";
                default: return null;
            }
        }

        internal static string ControlRequiredWords(string controlId)
        {
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlMotionRealVsReal:
                case TestCommandPartParitySweep.ControlMotionRealVsRealAir:
                    return "|lagS| <= max(one frame interval, 0.02 T), |durationRatio - 1| <= 0.05, curveShapeDev <= 0.05, "
                           + "frame 0 and the last frame within the real-vs-real allowance";
                case TestCommandPartParitySweep.ControlMotionGhostSlow:
                    return "durationRatio(slow) / durationRatio(normal) in [1.9, 2.1], the onset within one frame interval, "
                           + "the slow route failing on duration while its last frame passes the end-state rules";
                case TestCommandPartParitySweep.ControlMotionGhostLate:
                    return "lagS(late) - lagS(normal) in [0.98, 1.02] s, durationRatio within 0.02, the late route failing on lag";
                default: return null;
            }
        }

        private static string BadSetup(string setupStatus, out string detail)
        {
            detail = "setup " + (setupStatus ?? "<none>");
            return TestCommandPartParitySweep.ControlsBad;
        }

        /// <summary>
        /// <c>ctl-motion-real-vs-real</c> and its air twin (H.3): copy B's curve against copy
        /// A's, within the timing noise floor of the live capture.
        /// </summary>
        internal static string JudgeRealVsReal(PartParityMotionTiming t, double frameIntervalS, double predictedS,
            bool endFramesWithinAllowance, string setupStatus, out string detail)
        {
            if (TestCommandPartParitySweep.IsSetupFault(setupStatus)) return BadSetup(setupStatus, out detail);
            detail = null;
            if (t == null) { detail = "no timing"; return TestCommandPartParitySweep.ControlsBad; }
            double lagAllowed = Math.Max(frameIntervalS, RealVsRealLagMotionFraction * predictedS);
            var why = new List<string>();
            if (double.IsNaN(t.LagS) || Math.Abs(t.LagS) > lagAllowed) why.Add("lagS=" + Num(t.LagS));
            if (double.IsNaN(t.DurationRatio) || Math.Abs(t.DurationRatio - 1.0) > RealVsRealDurationTolerance)
                why.Add("durationRatio=" + Num(t.DurationRatio));
            if (!double.IsNaN(t.CurveShapeDev) && t.CurveShapeDev > RealVsRealCurveShapeMax) why.Add("curveShapeDev=" + Num(t.CurveShapeDev));
            if (!endFramesWithinAllowance) why.Add("end frames differ");
            return Verdict(why, out detail);
        }

        /// <summary>
        /// <c>ctl-motion-ghost-slow</c> (H.3), judged against the same subject's normal route:
        /// the duration ratio doubles, the onset stays, the slow route fails on duration and its
        /// last frame passes the P6 end-state rules.
        /// </summary>
        internal static string JudgeGhostSlow(PartParityMotionTiming normal, PartParityMotionTiming slow,
            double frameIntervalS, bool slowLastFramePasses, string setupStatus, out string detail)
        {
            if (TestCommandPartParitySweep.IsSetupFault(setupStatus)) return BadSetup(setupStatus, out detail);
            detail = null;
            if (normal == null || slow == null) { detail = "no timing"; return TestCommandPartParitySweep.ControlsBad; }
            var why = new List<string>();
            double ratio = Ratio(slow.DurationRatio, normal.DurationRatio);
            if (double.IsNaN(ratio) || ratio < SlowRatioMin || ratio > SlowRatioMax) why.Add("ratio=" + Num(ratio));
            double onset = Diff(slow.OnsetLagS, normal.OnsetLagS);
            if (double.IsNaN(onset) || Math.Abs(onset) > frameIntervalS) why.Add("onset=" + Num(onset));
            if (!DurationFails(slow.DurationRatio)) why.Add("slow route does not fail on duration");
            if (!slowLastFramePasses) why.Add("slow last frame fails the end-state rules");
            return Verdict(why, out detail);
        }

        /// <summary>
        /// <c>ctl-motion-ghost-late</c> (H.3), judged against the same subject's normal route:
        /// the lag grows by the shift, the duration ratio stays, the late route fails on lag.
        /// </summary>
        internal static string JudgeGhostLate(PartParityMotionTiming normal, PartParityMotionTiming late,
            string setupStatus, out string detail)
        {
            if (TestCommandPartParitySweep.IsSetupFault(setupStatus)) return BadSetup(setupStatus, out detail);
            detail = null;
            if (normal == null || late == null) { detail = "no timing"; return TestCommandPartParitySweep.ControlsBad; }
            var why = new List<string>();
            double shift = Diff(late.LagS, normal.LagS);
            if (double.IsNaN(shift) || shift < LateLagMin || shift > LateLagMax) why.Add("lag shift=" + Num(shift));
            double dr = Diff(late.DurationRatio, normal.DurationRatio);
            if (double.IsNaN(dr) || Math.Abs(dr) > LateDurationTolerance) why.Add("durationRatio change=" + Num(dr));
            if (LateMustFailOnLag(late.RealDurationS) && !LagFails(late.LagS, late.RealDurationS))
                why.Add("late route does not fail on lag");
            return Verdict(why, out detail);
        }

        /// <summary>Whether the late route must fail on lag: only when the 1.0 s shift is past
        /// that real motion's lag fail line, <c>max(0.5 s, 0.25 T_real)</c> (T_real under 4 s).
        /// A longer real motion holds the shift inside its line, so the relative requirement
        /// is then the whole test (H.3).</summary>
        internal static bool LateMustFailOnLag(double realDurationS)
            => LateShiftSeconds > Math.Max(LagFailMinSeconds,
                LagFailRealFraction * (double.IsNaN(realDurationS) ? 0.0 : realDurationS));

        private static string Verdict(List<string> why, out string detail)
        {
            detail = why.Count == 0 ? null : string.Join("; ", why.ToArray());
            return why.Count == 0 ? TestCommandPartParitySweep.ControlsOk : TestCommandPartParitySweep.ControlsBad;
        }

        private static string Num(double v) => double.IsNaN(v) ? "nan" : v.ToString("0.###", Ic);
    }
}
