using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// What the burst and dust enumeration reads of one part (design Appendix G.2, piece X1):
    /// its module list (<see cref="PartParityStatePart"/>), its base key, and two part-level
    /// config facts the module list does not carry: the part's <c>explosionPotential</c> and
    /// the FX group names its legacy <c>fx_*</c> keys name (the last csv item of each).
    /// </summary>
    internal sealed class PartParityBurstPart
    {
        internal PartParityStatePart Part;

        /// <summary>The part's default case key (part, source, base skin).</summary>
        internal PartParityCaseKey BaseKey;

        /// <summary>The part config's <c>explosionPotential</c>, or stock's field default
        /// (<see cref="PartParityFxRules.DefaultExplosionPotential"/>) when it names none.</summary>
        internal double ExplosionPotential = PartParityFxRules.DefaultExplosionPotential;

        /// <summary>The FX group names of the part's <c>fx_*</c> keys, in config order.</summary>
        internal List<string> FxGroups = new List<string>();

        internal string Name => Part?.Name ?? BaseKey?.Part;
    }

    /// <summary>What a burst control's judge reads (design G.4): the setup status, the burst
    /// measures that failed (names of <see cref="PartParityFxRules.BurstMeasures"/>), the
    /// schedule counts and whether every bin and layer drew nothing.</summary>
    internal sealed class PartParityBurstControlEvidence
    {
        internal string SetupStatus = TestCommandPartParitySweep.SetupOk;
        internal readonly HashSet<string> FailingMeasures = new HashSet<string>(StringComparer.Ordinal);
        internal bool AllCoverageZero;
        internal int RealBlasts;
        internal int GhostBlasts;

        /// <summary>The schedule failed on the prefab index (not only on count or position).</summary>
        internal bool ScheduleIndexDiffers;
    }

    /// <summary>What a dust control's judge reads (design G.4).</summary>
    internal sealed class PartParityDustControlEvidence
    {
        internal string SetupStatus = TestCommandPartParitySweep.SetupOk;

        /// <summary>The dust-layer measures that failed (names of <see cref="PartParityFxRules.DustMeasures"/>).</summary>
        internal readonly HashSet<string> FailingDustMeasures = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The flame-layer (the engine's own plume) measures that failed.</summary>
        internal readonly HashSet<string> FailingFlameMeasures = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Stock's raycast hit a surface in at least one sample.</summary>
        internal bool StockRaycastHit;

        internal bool RealDustCovered;
        internal bool GhostDustCovered;
    }

    /// <summary>
    /// P9 (design-part-ghost-parity.md Appendix G, piece X1): the contract of the burst and
    /// dust cases, the effects Parsek builds or triggers itself. Families, words, states, the
    /// <c>ground</c> context and its slots, the burst window and time bins, every G.3 constant
    /// and fail / warn line, the enumeration, the control subjects and the control judges. Every
    /// literal here has its mirror in <c>harness/lib/partparity.py</c>. The live driver is piece
    /// X4: until it lands a burst or dust run is refused
    /// <see cref="TestCommandPartParitySweep.BurstDustRunsNotBuiltReason"/>.
    /// </summary>
    internal static partial class PartParityFxRules
    {
        // ----- families (G.7 order) -----

        internal const string FamilyExplode = "explode";
        internal const string FamilyVesselExplode = "vessel-explode";
        internal const string FamilyBurstCustom = "burst-custom";
        internal const string FamilyDecouple = "decouple";
        internal const string FamilyDust = "dust";

        /// <summary>The P9 families, in the G.7 order.</summary>
        internal static readonly string[] BuiltFamilies =
            { FamilyExplode, FamilyVesselExplode, FamilyBurstCustom, FamilyDecouple, FamilyDust };

        /// <summary>The families sampled in time bins over trials (G.2.1).</summary>
        internal static readonly string[] BurstFamilies = { FamilyExplode, FamilyVesselExplode, FamilyBurstCustom, FamilyDecouple };

        /// <summary>The families judged by E.4's steady measures over the <c>dust</c> layer (G.2.2).</summary>
        internal static readonly string[] DustFamilies = { FamilyDust };

        // ----- the fx= words (each selects its own family) -----

        internal const string ArgExplode = "explode";
        internal const string ArgVesselExplode = "vessel-explode";
        internal const string ArgBurstCustom = "burst-custom";
        internal const string ArgDecouple = "decouple";
        internal const string ArgDust = "dust";

        /// <summary>The burst words: a run of them is an air run (<c>contexts=air</c>).</summary>
        internal static readonly string[] BurstArgWords = { ArgExplode, ArgVesselExplode, ArgBurstCustom, ArgDecouple };

        /// <summary>The dust words: a run of them is a ground run (<c>contexts=ground</c>).</summary>
        internal static readonly string[] DustArgWords = { ArgDust };

        /// <summary>Every word an <c>fx=</c> csv may name, in canonical order: the P8 words
        /// (<see cref="ArgKnownWords"/>), then the burst words, then the dust words. <c>fx=all</c> is <see cref="ArgWords"/> (P8) only,
        /// so every run named before P9 plans what it planned.</summary>
        internal static readonly string[] KnownArgWords =
        {
            ArgPlume, ArgRcs, ArgHeat, ArgPlate, ArgPlumeMain,
            ArgExplode, ArgVesselExplode, ArgBurstCustom, ArgDecouple,
            ArgDust,
        };

        /// <summary>Whether the word is a burst word.</summary>
        internal static bool IsBurstArg(string word) => Array.IndexOf(BurstArgWords, word) >= 0;

        /// <summary>Whether the word is a dust word.</summary>
        internal static bool IsDustArg(string word) => Array.IndexOf(DustArgWords, word) >= 0;

        /// <summary>Whether the family is a burst family.</summary>
        internal static bool IsBurstFamily(string family) => Array.IndexOf(BurstFamilies, family) >= 0;

        /// <summary>Whether the family is a dust family.</summary>
        internal static bool IsDustFamily(string family) => Array.IndexOf(DustFamilies, family) >= 0;

        /// <summary>Whether the family is a P9 family.</summary>
        internal static bool IsBuiltFamily(string family) => Array.IndexOf(BuiltFamilies, family) >= 0;

        // ----- states and contexts (G.2) -----

        internal const string StateExplode = "explode";

        /// <summary>A vessel stack of n parts: <c>vessel-explode-x&lt;n&gt;</c>.</summary>
        internal const string StateVesselExplodePrefix = "vessel-explode-x";

        /// <summary>The custom explosion of a vessel of L metres: <c>custom-l&lt;L&gt;</c>.</summary>
        internal const string StateCustomExplosionPrefix = "custom-l";

        /// <summary>The part puff fallback against the prefab of the part's potential.</summary>
        internal const string StatePuff = "puff";

        internal const string StateDecouple = "decouple";
        internal const string StateJointBreak = "joint-break";
        internal const string StateDustLow = "dust-low";
        internal const string StateDustHigh = "dust-high";
        internal const string StateDustWater = "dust-water";

        /// <summary>Every fixed P9 state word, in canonical order; the stack and custom words are
        /// <see cref="VesselExplodeState"/> and <see cref="CustomExplosionState"/>.</summary>
        internal static readonly string[] BuiltStates =
            { StateExplode, StatePuff, StateDecouple, StateJointBreak, StateDustLow, StateDustHigh, StateDustWater };

        internal static string VesselExplodeState(int parts) => StateVesselExplodePrefix + parts.ToString(Ic);

        internal static string CustomExplosionState(double lengthM)
            => StateCustomExplosionPrefix + lengthM.ToString("0.###", Ic);

        /// <summary>The dust cases' context: F7's held surface slots near KSC, at a height over
        /// the terrain (or the ocean) under the slot.</summary>
        internal const string ContextGround = "ground";

        /// <summary>The burst cases' context: F7's <c>air</c> context (the pad host, held slots
        /// 1600 m above sea level), never the orbit context (G.2.1).</summary>
        internal const string ContextBurst = PartParityStateRules.ContextAir;

        // ----- the ground context's slots (G.2.2) -----

        internal const string SurfaceSlotTerrain = "terrain";
        internal const string SurfaceSlotWater = "water";

        /// <summary>The slot heights as fractions of each part's own <c>ModuleSurfaceFX.maxDistance</c>:
        /// 0.2 and 0.7 over terrain, 0.4 over the ocean east of KSC, so stock's raycast always
        /// reaches the surface.</summary>
        internal const double DustLowFraction = 0.2;
        internal const double DustHighFraction = 0.7;
        internal const double DustWaterFraction = 0.4;

        /// <summary><c>ctl-dust-above-reach</c>'s height: past the raycast's reach.</summary>
        internal const double DustAboveReachFraction = 1.2;

        /// <summary>The dust slots, in case order: (state, surface, fraction).</summary>
        internal static readonly string[] DustSlotStates = { StateDustLow, StateDustHigh, StateDustWater };
        internal static readonly string[] DustSlotSurfaces = { SurfaceSlotTerrain, SurfaceSlotTerrain, SurfaceSlotWater };
        internal static readonly double[] DustSlotFractions = { DustLowFraction, DustHighFraction, DustWaterFraction };

        /// <summary>A slot's height above the surface under it, metres.</summary>
        internal static double DustSlotHeightM(double fraction, double maxDistanceM)
            => double.IsNaN(fraction) || double.IsNaN(maxDistanceM) ? double.NaN : fraction * maxDistanceM;

        // ----- ModuleSurfaceFX (decompiled KSP 1.12.5 field initializers) -----

        internal const string ModuleSurfaceFx = "ModuleSurfaceFX";
        internal const string KeyThrustProviderModuleIndex = "thrustProviderModuleIndex";
        internal const string KeyFxMax = "fxMax";
        internal const string KeyMaxDistance = "maxDistance";
        internal const string KeyFalloff = "falloff";
        internal const string KeyThrustTransformName = "thrustTransformName";
        internal const int SurfaceFxDefaultThrustProviderModuleIndex = -1;
        internal const double SurfaceFxDefaultFxMax = 1.0;
        internal const double SurfaceFxDefaultMaxDistanceM = 50.0;
        internal const double SurfaceFxDefaultFalloff = 2.0;

        /// <summary>The collider tag on which stock draws the pad's own FX and no dust.</summary>
        internal const string LaunchpadFxTag = "LaunchpadFX";

        /// <summary>
        /// Stock's dust scale for one raycast (decompiled <c>ModuleSurfaceFX</c>):
        /// <c>thrust / maxThrust * fxMax * (1 - d / maxDistance)^falloff</c>, 0 at or past the
        /// reach or for a senseless input.
        /// </summary>
        internal static double StockSurfaceFxScale(double thrustFraction, double fxMax, double distanceM,
            double maxDistanceM, double falloff)
        {
            if (double.IsNaN(thrustFraction) || double.IsNaN(fxMax) || double.IsNaN(distanceM)
                || double.IsNaN(maxDistanceM) || double.IsNaN(falloff) || maxDistanceM <= 0.0
                || distanceM < 0.0 || distanceM >= maxDistanceM)
                return 0.0;
            return thrustFraction * fxMax * Math.Pow(1.0 - distanceM / maxDistanceM, falloff);
        }

        // ----- the surfaces stock's raycast reports (G.2.2, G.3) -----

        internal const string DustSurfaceTerrain = "terrain";
        internal const string DustSurfaceWater = "water";
        internal const string DustSurfaceLaunchpad = "launchpad";
        internal const string DustSurfaceNone = "none";

        internal static readonly string[] DustSurfaces =
            { DustSurfaceTerrain, DustSurfaceWater, DustSurfaceLaunchpad, DustSurfaceNone };

        // ----- part facts (decompiled Part / ModuleDecouplerBase field initializers) -----

        internal const string KeyExplosionPotential = "explosionPotential";
        internal const double DefaultExplosionPotential = 0.5;
        internal const string TypeDecouplerBase = "ModuleDecouplerBase";
        internal const string KeyFxGroupName = "fxGroupName";
        internal const string DefaultDecoupleFxGroup = "decouple";
        internal const string FxKeyPrefix = "fx_";

        /// <summary>The FX group an <c>fx_*</c> value names: its last csv item, trimmed; null for none.</summary>
        internal static string FxGroupOfValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string[] items = value.Split(',');
            string g = items[items.Length - 1].Trim();
            return g.Length == 0 ? null : g;
        }

        // ----- the burst subjects (G.2.1, G.5) -----

        /// <summary>The vessel stacks: <see cref="VesselExplodeStackSizes"/> parts of the first
        /// present part of this list, held still and exploded in one frame (they merge as in a
        /// crash). 1 and 2 bracket FXMonger's 10 m merge of two default parts (G.8 item 5).</summary>
        internal static readonly string[] VesselExplodeStackParts = { "fuelTank", "fuelTankSmallFlat" };
        internal static readonly int[] VesselExplodeStackSizes = { 1, 2, 3, 6 };

        /// <summary>The custom explosion's vessel lengths, metres: Parsek's power
        /// <c>Clamp01(L / 20)</c> at 0.05, 0.25, 0.5, 0.75 and 1.</summary>
        internal static readonly double[] CustomExplosionLengthsM = { 1.0, 5.0, 10.0, 15.0, 20.0 };

        /// <summary>The divisor of Parsek's end-explosion power, <c>Clamp01(length / 20)</c>
        /// (<c>GhostAudioPresets.VesselLengthPowerDivisor</c>, pinned by a test).</summary>
        internal const double TerminalExplosionLengthDivisorM = 20.0;

        /// <summary>The custom explosion stand-in is no part: its cases carry this name.</summary>
        internal const string BurstCustomPart = "parsek.customExplosion";

        /// <summary>The plain joint breaks: two parts held as a 2-part case, the joint broken
        /// directly; stock draws nothing.</summary>
        internal static readonly string[] JointBreakParts = { "fuelTank", "trussPiece1x" };

        internal const string StandInCustom = "custom";
        internal const string StandInPuff = "puff";

        /// <summary>Parsek's end-explosion power for a vessel of this length.</summary>
        internal static double TerminalExplosionPower(double lengthM)
        {
            if (double.IsNaN(lengthM)) return 0.0;
            double p = lengthM / TerminalExplosionLengthDivisorM;
            return p < 0.0 ? 0.0 : (p > 1.0 ? 1.0 : p);
        }

        /// <summary>The FXMonger prefab a blast of this power draws: <c>explosions[(int)(Clamp01(power) * (n - 1))]</c>
        /// (decompiled <c>FXMonger.LateUpdate</c>); -1 with no prefabs.</summary>
        internal static int FxMongerPrefabIndex(double power, int prefabCount)
        {
            if (prefabCount <= 0 || double.IsNaN(power)) return -1;
            double p = power < 0.0 ? 0.0 : (power > 1.0 ? 1.0 : power);
            return (int)(p * (prefabCount - 1));
        }

        // ----- the burst window, bins and trials (G.2.1) -----

        internal const double BurstWindowSeconds = 4.0;
        internal const double DecoupleBurstWindowSeconds = 2.0;
        internal const double BurstBinSeconds = 0.25;

        /// <summary>Trials per case: the four slots of one wave, each fires one trial.</summary>
        internal const int BurstTrials = 4;

        /// <summary>Each trial's slot pair from the next one.</summary>
        internal const double BurstTrialSpacingM = 40.0;

        /// <summary>A ghost's slot from every real source (FXMonger merges blasts within 10 m).</summary>
        internal const double BurstGhostSeparationM = 20.0;

        /// <summary>FXMonger's <c>minDistanceFromOtherBlasts</c>: blasts closer than this merge.</summary>
        internal const double FxMongerMergeDistanceM = 10.0;

        /// <summary>A new explosion object is tagged to the nearest source within this.</summary>
        internal const double BurstTagRadiusM = 10.0;

        /// <summary>A case's burst window: 2 s for a decouple case, 4 s for every other burst.</summary>
        internal static double BurstWindowFor(string family)
            => string.Equals(family, FamilyDecouple, StringComparison.Ordinal) ? DecoupleBurstWindowSeconds : BurstWindowSeconds;

        /// <summary>The bins of a window: 16 for 4 s, 8 for 2 s.</summary>
        internal static int BurstBinCount(double windowSeconds)
            => double.IsNaN(windowSeconds) || windowSeconds <= 0.0 ? 0 : (int)Math.Round(windowSeconds / BurstBinSeconds);

        /// <summary>The bin of a capture frame at <paramref name="t"/> seconds after the trigger
        /// frame (t = 0 is that frame): <c>floor(t / 0.25)</c>, or -1 before the trigger or past
        /// the window.</summary>
        internal static int BurstBinIndex(double t, double windowSeconds)
        {
            int bins = BurstBinCount(windowSeconds);
            if (double.IsNaN(t) || t < 0.0 || bins == 0) return -1;
            int b = (int)Math.Floor(t / BurstBinSeconds);
            return b < bins ? b : -1;
        }

        /// <summary>The split half a 0-based trial belongs to: trials 1 and 3 (0, 2) are the even
        /// half, 2 and 4 (1, 3) the odd one; independent draws, not alternate frames.</summary>
        internal static bool IsEvenTrial(int trial) => trial % 2 == 0;

        // ----- views and layers (G.2.1, G.2.2) -----

        internal const string ViewBurstSide = "burst-side";
        internal const string ViewBurstTop = "burst-top";
        internal const string ViewBurstFar = "burst-far";

        /// <summary>A burst case's views: across the slot line, straight down, and far.</summary>
        internal static readonly string[] BurstViews = { ViewBurstSide, ViewBurstTop, ViewBurstFar };

        internal const string ViewDustSide = "dust-side";
        internal const string ViewDustTop = "dust-top";
        internal const string ViewDustFar = "dust-far";

        internal static readonly string[] DustViews = { ViewDustSide, ViewDustTop, ViewDustFar };

        /// <summary>The cross-section view of a dust case (the axis, the surface normal, projects to a point).</summary>
        internal const string DustAxisView = ViewDustTop;

        /// <summary>The layer holding the <c>SurfaceFX</c> prefab's renderers and the ghost's
        /// <c>ParsekLaunchDust</c>; the engine's plume stays in flame / smoke, unjudged here.</summary>
        internal const string LayerDust = "dust";

        /// <summary>A burst case's layers: the explosion's additive fire and its smoke (<see cref="ClassifyFxRenderer"/>).</summary>
        internal static readonly string[] BurstLayers = { LayerFlame, LayerSmoke };

        // ----- the burst measures (G.3) -----

        internal const string MeasureOnset = "onset";
        internal const string MeasurePeakTime = "peak-time";
        internal const string MeasureDuration = "duration";
        internal const string MeasurePeakArea = "peak-area";
        internal const string MeasureRadius = "radius";
        internal const string MeasureCentroidOffset = "centroid-offset";
        internal const string MeasureSchedule = "schedule";

        /// <summary>Every burst measure, in the G.3 table order.</summary>
        internal static readonly string[] BurstMeasures =
        {
            MeasurePresence, MeasureOnset, MeasurePeakTime, MeasureDuration, MeasurePeakArea, MeasureRadius,
            MeasureCentroidOffset, MeasureColour, MeasureBrightness, MeasureSmoke, MeasureSchedule,
        };

        /// <summary>The dust measures: E.4's over the <c>dust</c> layer with the surface-normal axis (G.3).</summary>
        internal static readonly string[] DustMeasures =
        {
            MeasurePresence, MeasureOrigin, MeasureLateral, MeasureLength, MeasureWidth, MeasureCrossSection,
            MeasureColour, MeasureBrightness,
        };

        internal const string ReasonBurstMissing = "burst-missing";
        internal const string ReasonBurstExtra = "burst-extra";
        internal const string ReasonDustMissing = "dust-missing";

        /// <summary><c>dust-extra:&lt;surface&gt;</c>: the ghost draws dust where stock drew none,
        /// named by stock's raycast surface (none, or the pad, when stock draws no dust there).</summary>
        internal const string ReasonDustExtraPrefix = "dust-extra:";

        internal static string DustExtraReason(string surface) => ReasonDustExtraPrefix + (surface ?? DustSurfaceNone);

        // Fail and warn lines (starting guesses, re-pinned from the reading runs and the controls).
        // Bin deltas are ghost minus real in whole bins; a delta FAILS when its magnitude is over
        // the fail line and WARNS when it is at or over the warn line ("over 2 bins" / "1 bin").
        internal const int BurstOnsetFailBins = 1;
        internal const int BurstPeakTimeFailBins = 2;
        internal const int BurstPeakTimeWarnBins = 1;
        internal const double BurstDurationFailFraction = 0.40;
        internal const double BurstDurationWarnFraction = 0.20;
        internal const double BurstPeakAreaFailMin = 0.6;
        internal const double BurstPeakAreaFailMax = 1.6;
        internal const double BurstPeakAreaWarnMin = 0.8;
        internal const double BurstPeakAreaWarnMax = 1.25;
        internal const double BurstRadiusPercentile = 0.95;
        internal const double BurstRadiusFailMin = 0.7;
        internal const double BurstRadiusFailMax = 1.4;
        internal const double BurstRadiusWarnMin = 0.85;
        internal const double BurstRadiusWarnMax = 1.18;
        internal const double BurstCentroidOffsetFail = 0.3;
        internal const double BurstCentroidOffsetWarn = 0.15;

        /// <summary>The schedule's position match (rig): a blast more than this from its
        /// counterpart, relative to each side's own source, fails.</summary>
        internal const double BurstSchedulePositionToleranceM = 1.0;

        /// <summary>Two blast powers inside one prefab index differ when further apart than this (a warn).</summary>
        internal const double BurstSchedulePowerEpsilon = 0.0001;

        internal const string LevelOk = "ok";
        internal const string LevelWarn = "warn";
        internal const string LevelFail = "fail";

        /// <summary>
        /// The G.3 line of one numeric burst measure: <c>ok</c>, <c>warn</c> or <c>fail</c>, null
        /// for a measure with no numeric line or a NaN value (a null measure never fails).
        /// <c>onset</c> / <c>peak-time</c> take a bin delta, <c>duration</c> the delta over the
        /// real duration, <c>peak-area</c> / <c>radius</c> / <c>brightness</c> a ratio,
        /// <c>centroid-offset</c> the offset over the source part's box diameter. Colour is
        /// <see cref="BurstColourLevel"/>.
        /// </summary>
        internal static string BurstMeasureLevel(string measure, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return null;
            double a = Math.Abs(value);
            switch (measure)
            {
                case MeasureOnset: return a > BurstOnsetFailBins ? LevelFail : LevelOk;
                case MeasurePeakTime: return a > BurstPeakTimeFailBins ? LevelFail : (a >= BurstPeakTimeWarnBins ? LevelWarn : LevelOk);
                case MeasureDuration: return a > BurstDurationFailFraction ? LevelFail : (a > BurstDurationWarnFraction ? LevelWarn : LevelOk);
                case MeasurePeakArea: return Band(value, BurstPeakAreaFailMin, BurstPeakAreaFailMax, BurstPeakAreaWarnMin, BurstPeakAreaWarnMax);
                case MeasureRadius: return Band(value, BurstRadiusFailMin, BurstRadiusFailMax, BurstRadiusWarnMin, BurstRadiusWarnMax);
                case MeasureCentroidOffset: return a > BurstCentroidOffsetFail ? LevelFail : (a > BurstCentroidOffsetWarn ? LevelWarn : LevelOk);
                case MeasureBrightness: return value < BrightnessWarnMin || value > BrightnessWarnMax ? LevelWarn : LevelOk;
                default: return null;
            }
        }

        /// <summary>E.4's colour lines over a burst's chromaticity distance and hue L1 (either
        /// NaN reads as unmeasured): fail over <see cref="ChromaFail"/> or <see cref="HueL1Fail"/>,
        /// warn over half of either.</summary>
        internal static string BurstColourLevel(double chromaDistance, double hueL1)
        {
            bool c = !double.IsNaN(chromaDistance), h = !double.IsNaN(hueL1);
            if (!c && !h) return null;
            if ((c && chromaDistance > ChromaFail) || (h && hueL1 > HueL1Fail)) return LevelFail;
            if ((c && chromaDistance > ColourWarnFraction * ChromaFail) || (h && hueL1 > ColourWarnFraction * HueL1Fail))
                return LevelWarn;
            return LevelOk;
        }

        private static string Band(double v, double failMin, double failMax, double warnMin, double warnMax)
        {
            if (v < failMin || v > failMax) return LevelFail;
            if (v < warnMin || v > warnMax) return LevelWarn;
            return LevelOk;
        }

        // ----- exclusions -----

        /// <summary>A <c>ModuleDecouplerBase</c> whose part names no FX group of its
        /// <c>fxGroupName</c>: stock bursts nothing (the two stock heat shields).</summary>
        internal const string ExcludeDecoupleNoFxGroup = "decouple-no-fx-group";

        /// <summary>A <c>ModuleSurfaceFX</c> whose <c>thrustProviderModuleIndex</c> names no engine module.</summary>
        internal const string ExcludeDustNoThrustProvider = "dust-no-thrust-provider";

        /// <summary>A fixed burst subject (a stack part, a joint-break part) the install lacks.</summary>
        internal const string ExcludeBurstSubjectAbsent = "burst-subject-absent";

        internal static readonly string[] BuiltExclusionReasons =
            { ExcludeDecoupleNoFxGroup, ExcludeDustNoThrustProvider, ExcludeBurstSubjectAbsent };

        // =====================================================================================
        // enumeration (G.2, G.5): over the whole install, since the power buckets, the stacks and
        // the stand-ins are install-level choices
        // =====================================================================================

        /// <summary>
        /// Every burst and dust case of an install and what was left out (design G.2, G.5):
        /// <list type="bullet">
        /// <item><c>explode</c>: one part per <c>explosionPotential</c> value present (ascending),
        /// the ordinal-first part of that value, <c>explode</c> in the air context.</item>
        /// <item><c>vessel-explode</c>: stacks of <see cref="VesselExplodeStackSizes"/> parts of the
        /// first present <see cref="VesselExplodeStackParts"/> part.</item>
        /// <item><c>burst-custom</c>: the custom explosion at each <see cref="CustomExplosionLengthsM"/>
        /// (part <see cref="BurstCustomPart"/>), then the part puff on each explode bucket's part.</item>
        /// <item><c>decouple</c>: every module implementing <c>ModuleDecouplerBase</c> whose part names an
        /// FX group of its <c>fxGroupName</c> (<c>decouple</c> by default), <c>decouple</c>; else
        /// <c>decouple-no-fx-group</c>; then a <c>joint-break</c> on each present <see cref="JointBreakParts"/> part.</item>
        /// <item><c>dust</c>: every module named <c>ModuleSurfaceFX</c> whose thrust provider is an
        /// engine, at the three slots of the <c>ground</c> context.</item>
        /// </list>
        /// The same-word tiebreak of D.1 applies per part over (state, context).
        /// </summary>
        internal static PartParityFxEnumeration EnumerateBurstDust(IEnumerable<PartParityBurstPart> install)
        {
            var r = new PartParityFxEnumeration();
            var parts = new List<PartParityBurstPart>();
            if (install != null)
                foreach (PartParityBurstPart p in install)
                    if (p != null && p.Name != null) parts.Add(p);
            parts.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            var byName = new Dictionary<string, PartParityBurstPart>(StringComparer.Ordinal);
            foreach (PartParityBurstPart p in parts)
                if (!byName.ContainsKey(p.Name)) byName[p.Name] = p;

            // explode: one representative per potential value, ascending.
            var buckets = new SortedDictionary<double, PartParityBurstPart>();
            foreach (PartParityBurstPart p in parts)
                if (!double.IsNaN(p.ExplosionPotential) && !buckets.ContainsKey(p.ExplosionPotential))
                    buckets[p.ExplosionPotential] = p;
            foreach (KeyValuePair<double, PartParityBurstPart> b in buckets)
                AddBurst(r, b.Value, FamilyExplode, StateExplode, o => o.ExplosionPotential = b.Key);

            // vessel stacks.
            PartParityBurstPart stack = FirstPresent(VesselExplodeStackParts, byName);
            if (stack == null)
                r.Exclusions.Add(Excluded(VesselExplodeStackParts[0], FamilyVesselExplode, ExcludeBurstSubjectAbsent, ContextBurst));
            else
                foreach (int n in VesselExplodeStackSizes)
                    AddBurst(r, stack, FamilyVesselExplode, VesselExplodeState(n), o =>
                    {
                        o.StackSize = n;
                        o.ExplosionPotential = stack.ExplosionPotential;
                    });

            // stand-ins: the custom explosion (no part), then the puff on each bucket's part.
            var custom = new PartParityBurstPart { BaseKey = new PartParityCaseKey { Part = BurstCustomPart } };
            foreach (double l in CustomExplosionLengthsM)
                AddBurst(r, custom, FamilyBurstCustom, CustomExplosionState(l), o =>
                {
                    o.StandIn = StandInCustom;
                    o.StandInLengthM = l;
                    o.ExplosionPotential = TerminalExplosionPower(l);
                });
            foreach (KeyValuePair<double, PartParityBurstPart> b in buckets)
                AddBurst(r, b.Value, FamilyBurstCustom, StatePuff, o =>
                {
                    o.StandIn = StandInPuff;
                    o.ExplosionPotential = b.Key;
                });

            // decouplers, then the plain joint breaks.
            foreach (PartParityBurstPart p in parts)
            {
                var cases = new List<PartParityFxOption>();
                if (p.Part?.Modules != null)
                    foreach (PartParityStateModule m in p.Part.Modules)
                    {
                        if (m == null || !m.Implements(TypeDecouplerBase)) continue;
                        string group = m.Value(KeyFxGroupName);
                        if (string.IsNullOrEmpty(group)) group = DefaultDecoupleFxGroup;
                        if (p.FxGroups == null || !p.FxGroups.Contains(group))
                        {
                            r.Exclusions.Add(new PartParityFxExclusion
                            {
                                Part = p.Name, Family = FamilyDecouple, Module = m.Name, ModuleIndex = m.Index,
                                Reason = ExcludeDecoupleNoFxGroup, Context = ContextBurst,
                            });
                            continue;
                        }
                        PartParityFxOption o = BurstOption(FamilyDecouple, StateDecouple, m.Name, m.Index);
                        o.FxGroup = group;
                        o.ExplosionPotential = p.ExplosionPotential;
                        cases.Add(o);
                    }
                BuildBurstKeys(cases, p, r);
            }
            foreach (string name in JointBreakParts)
            {
                if (!byName.TryGetValue(name, out PartParityBurstPart p))
                {
                    r.Exclusions.Add(Excluded(name, FamilyDecouple, ExcludeBurstSubjectAbsent, ContextBurst));
                    continue;
                }
                AddBurst(r, p, FamilyDecouple, StateJointBreak, o => o.ExplosionPotential = p.ExplosionPotential);
            }

            // dust: every ModuleSurfaceFX with an engine behind it, three slots each.
            foreach (PartParityBurstPart p in parts) EnumerateDust(p, r);
            return r;
        }

        private static void EnumerateDust(PartParityBurstPart p, PartParityFxEnumeration r)
        {
            if (p.Part?.Modules == null) return;
            var cases = new List<PartParityFxOption>();
            bool multiMode = false;
            foreach (PartParityStateModule m in p.Part.Modules)
                if (m != null && m.Named(ModuleMultiModeEngine)) multiMode = true;
            foreach (PartParityStateModule m in p.Part.Modules)
            {
                if (m == null || !m.Named(ModuleSurfaceFx)) continue;
                int provider = ParseInt(m.Value(KeyThrustProviderModuleIndex), SurfaceFxDefaultThrustProviderModuleIndex);
                PartParityStateModule engine = ModuleAt(p.Part, provider);
                if (engine == null || !engine.Implements(TypeEngines))
                {
                    r.Exclusions.Add(new PartParityFxExclusion
                    {
                        Part = p.Name, Family = FamilyDust, Module = m.Name, ModuleIndex = m.Index,
                        Reason = ExcludeDustNoThrustProvider, Context = ContextGround,
                    });
                    continue;
                }
                // The recorder keys an engine by its position among the part's as-ModuleEngines modules.
                int engineKey = 0;
                foreach (PartParityStateModule e in p.Part.Modules)
                    if (e != null && e.Index < provider && e.Implements(TypeEngines)) engineKey++;
                for (int s = 0; s < DustSlotStates.Length; s++)
                {
                    PartParityFxOption o = PlumeOption(engine, StateBurnFull, BurnFullThrottle, ContextGround,
                        HasPropellant(engine, AirPropellant), PartParityStateRules.ParseBool(engine.Value(KeyThrottleLocked), false),
                        multiMode, false);
                    o.Family = FamilyDust;
                    o.State = DustSlotStates[s];
                    o.Module = m.Name;
                    o.ModuleIndex = m.Index;
                    o.RecorderKeyIndex = engineKey;
                    o.ThrustProviderModuleIndex = provider;
                    o.EngineModule = engine.Name;
                    o.Surface = DustSlotSurfaces[s];
                    o.SlotFraction = DustSlotFractions[s];
                    o.SurfaceFxMaxDistanceM = ParseDouble(m.Value(KeyMaxDistance), SurfaceFxDefaultMaxDistanceM);
                    o.SurfaceFxFalloff = ParseDouble(m.Value(KeyFalloff), SurfaceFxDefaultFalloff);
                    o.SurfaceFxFxMax = ParseDouble(m.Value(KeyFxMax), SurfaceFxDefaultFxMax);
                    o.ThrustTransformName = m.Value(KeyThrustTransformName);
                    o.SlotHeightM = DustSlotHeightM(o.SlotFraction, o.SurfaceFxMaxDistanceM);
                    cases.Add(o);
                }
            }
            BuildBurstKeys(cases, p, r);
        }

        /// <summary>The module whose prefab index is <paramref name="index"/>, or null.</summary>
        private static PartParityStateModule ModuleAt(PartParityStatePart part, int index)
        {
            if (index < 0 || part?.Modules == null) return null;
            foreach (PartParityStateModule m in part.Modules)
                if (m != null && m.Index == index) return m;
            return null;
        }

        private static void AddBurst(PartParityFxEnumeration r, PartParityBurstPart p, string family, string state,
            Action<PartParityFxOption> fill)
        {
            PartParityFxOption o = BurstOption(family, state, null, -1);
            fill?.Invoke(o);
            BuildBurstKeys(new List<PartParityFxOption> { o }, p, r);
        }

        private static PartParityFxOption BurstOption(string family, string state, string module, int moduleIndex)
            => new PartParityFxOption
            {
                Family = family, State = state, Context = ContextBurst, Module = module,
                ModuleIndex = moduleIndex, RecorderKeyIndex = moduleIndex,
                BurstWindowSeconds = BurstWindowFor(family),
            };

        private static void BuildBurstKeys(List<PartParityFxOption> cases, PartParityBurstPart p, PartParityFxEnumeration r)
        {
            if (cases.Count == 0) return;
            var tmp = new PartParityFxEnumeration();
            BuildKeys(cases, p.BaseKey ?? new PartParityCaseKey { Part = p.Name }, p.Name, tmp);
            r.Cases.AddRange(tmp.Cases);
        }

        private static PartParityBurstPart FirstPresent(string[] names, Dictionary<string, PartParityBurstPart> byName)
        {
            foreach (string n in names)
                if (byName.TryGetValue(n, out PartParityBurstPart p)) return p;
            return null;
        }

        private static PartParityFxExclusion Excluded(string part, string family, string reason, string context)
            => new PartParityFxExclusion { Part = part, Family = family, Reason = reason, Context = context };

        private static int ParseInt(string s, int fallback)
            => int.TryParse(s, NumberStyles.Integer, Ic, out int v) ? v : fallback;

        private static double ParseDouble(string s, double fallback)
            => double.TryParse(s, NumberStyles.Float, Ic, out double v) ? v : fallback;

        /// <summary>Whether a P9 key belongs to a run: its family is one the run's words select
        /// and its context is the run's (<c>air</c> for a burst run, <c>ground</c> for a dust run).</summary>
        internal static bool BuiltKeySelected(PartParityCaseKey key, IList<string> fxWords, string runContext)
        {
            PartParityFxOption o = key?.FxOption;
            if (o == null || fxWords == null || !IsBuiltFamily(o.Family)) return false;
            bool family = false;
            foreach (string w in fxWords)
                if (string.Equals(FamilyForArg(w), o.Family, StringComparison.Ordinal)) { family = true; break; }
            return family && string.Equals(o.Context, runContext, StringComparison.Ordinal);
        }

        // =====================================================================================
        // controls (G.4)
        // =====================================================================================

        /// <summary>The burst controls' subject: one default-potential part, held alone.</summary>
        internal static readonly string[] BurstControlParts = { "fuelTank", "fuelTankSmallFlat" };

        /// <summary>The dust controls' subject: the LV-T45 (<c>maxDistance</c> 30, <c>falloff</c> 1.7).</summary>
        internal static readonly string[] DustControlParts = { "liquidEngine2.v2" };

        /// <summary>
        /// <c>ctl-dust-above-reach</c> while G.8 item 3 stands: the ghost's dust is not judged
        /// against stock's raycast miss, so a ghost that draws dust there records <c>n/a</c>
        /// (<see cref="TestCommandPartParitySweep.KnownDifferenceControlIds"/>). Fix Y3 sets it true.
        /// </summary>
        internal const bool DustAboveReachGhostJudged = false;

        /// <summary><c>ctl-burst-late</c>'s delay, <c>ctl-burst-shift</c>'s offset and
        /// <c>ctl-burst-power</c>'s step (G.4).</summary>
        internal const double BurstLateControlSeconds = 0.5;
        internal const double BurstShiftControlM = 3.0;
        internal const int BurstPowerControlIndexStep = 1;

        /// <summary>A burst or dust control's preference list; null for any other control.</summary>
        internal static string[] BuiltControlPreference(string controlId)
        {
            if (Array.IndexOf(TestCommandPartParitySweep.BurstControlIds, controlId) >= 0) return BurstControlParts;
            if (Array.IndexOf(TestCommandPartParitySweep.DustControlIds, controlId) >= 0) return DustControlParts;
            return null;
        }

        /// <summary>A burst or dust control's subject in words; null for any other control.</summary>
        internal static string BuiltControlSubjectWords(string controlId)
        {
            string burst = string.Join(" / ", BurstControlParts) + " (first present), held alone";
            string dust = DustControlParts[0] + " burning nozzle down in the ground context";
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlBurstEmpty: return burst + ", a burst wave with nothing triggered";
                case TestCommandPartParitySweep.ControlBurstRealVsReal: return burst + ", real trials 1 and 3 against 2 and 4";
                case TestCommandPartParitySweep.ControlBurstOff: return burst + ", intact ghost vs one whose event is dropped";
                case TestCommandPartParitySweep.ControlBurstLate: return burst + ", intact ghost vs one triggered 0.5 s late";
                case TestCommandPartParitySweep.ControlBurstPower: return burst + ", intact ghost vs one exploded one prefab index lower";
                case TestCommandPartParitySweep.ControlBurstShift: return burst + ", intact ghost vs one triggered 3 m off its part";
                case TestCommandPartParitySweep.ControlBurstHue: return burst + ", intact ghost vs one whose readback channels are rotated (r,g,b)->(b,r,g)";
                case TestCommandPartParitySweep.ControlDustRealVsReal: return dust + " at 0.2 of its reach, real against itself in split halves";
                case TestCommandPartParitySweep.ControlDustOff: return dust + ", intact ghost vs one with its dust hidden";
                case TestCommandPartParitySweep.ControlDustAboveReach: return DustControlParts[0] + " held at 1.2 of its maxDistance, past stock's raycast";
                default: return null;
            }
        }

        /// <summary>A burst or dust control's required outcome in words; null for any other control.</summary>
        internal static string BuiltControlRequiredWords(string controlId)
        {
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlBurstEmpty: return "zero coverage in every bin and layer";
                case TestCommandPartParitySweep.ControlBurstRealVsReal: return "passes every burst measure (the noise floor)";
                case TestCommandPartParitySweep.ControlBurstOff: return "fails presence, the schedule reads one blast against none";
                case TestCommandPartParitySweep.ControlBurstLate: return "fails onset, passes peak area and colour";
                case TestCommandPartParitySweep.ControlBurstPower: return "fails the schedule on the prefab index, and peak area or radius";
                case TestCommandPartParitySweep.ControlBurstShift: return "fails centroid offset, passes peak area and colour";
                case TestCommandPartParitySweep.ControlBurstHue: return "fails colour only";
                case TestCommandPartParitySweep.ControlDustRealVsReal: return "passes every dust measure";
                case TestCommandPartParitySweep.ControlDustOff: return "fails dust presence, the flame layer passes";
                case TestCommandPartParitySweep.ControlDustAboveReach: return "stock draws no dust (raycast miss) and the ghost none either; n/a while G.8 item 3 stands";
                default: return null;
            }
        }

        /// <summary>
        /// Judges one burst control (G.4) from its evidence: <c>ok</c> when the required outcome
        /// holds, else <c>bad</c> with the reason in <paramref name="detail"/>; a setup fault is <c>bad</c>.
        /// </summary>
        internal static string JudgeBurstControl(string controlId, PartParityBurstControlEvidence e, out string detail)
        {
            detail = null;
            if (e == null) { detail = "no evidence"; return TestCommandPartParitySweep.ControlsBad; }
            if (TestCommandPartParitySweep.IsSetupFault(e.SetupStatus))
            {
                detail = "setup " + (e.SetupStatus ?? "<none>");
                return TestCommandPartParitySweep.ControlsBad;
            }
            HashSet<string> f = e.FailingMeasures;
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlBurstEmpty:
                    return Require(e.AllCoverageZero, "a bin is covered", out detail);
                case TestCommandPartParitySweep.ControlBurstRealVsReal:
                    return Require(f.Count == 0, "fails " + BurstNames(f), out detail);
                case TestCommandPartParitySweep.ControlBurstOff:
                    if (!f.Contains(MeasurePresence)) return Require(false, "presence passes", out detail);
                    return Require(e.RealBlasts == 1 && e.GhostBlasts == 0,
                        "the schedule reads " + e.RealBlasts.ToString(Ic) + " against " + e.GhostBlasts.ToString(Ic), out detail);
                case TestCommandPartParitySweep.ControlBurstLate:
                    return RequireMeasures(f, new[] { MeasureOnset }, new[] { MeasurePeakArea, MeasureColour }, out detail);
                case TestCommandPartParitySweep.ControlBurstPower:
                    if (!f.Contains(MeasureSchedule) || !e.ScheduleIndexDiffers) return Require(false, "the schedule index matches", out detail);
                    return Require(f.Contains(MeasurePeakArea) || f.Contains(MeasureRadius), "peak area and radius pass", out detail);
                case TestCommandPartParitySweep.ControlBurstShift:
                    return RequireMeasures(f, new[] { MeasureCentroidOffset }, new[] { MeasurePeakArea, MeasureColour }, out detail);
                case TestCommandPartParitySweep.ControlBurstHue:
                    if (!f.Contains(MeasureColour)) return Require(false, "colour passes", out detail);
                    return Require(f.Count == 1, "fails " + BurstNames(f), out detail);
                default:
                    detail = "not a burst control: " + (controlId ?? "<none>");
                    return TestCommandPartParitySweep.ControlsBad;
            }
        }

        /// <summary>
        /// Judges one dust control (G.4): <c>ok</c>, <c>bad</c>, or for
        /// <c>ctl-dust-above-reach</c> <c>n/a</c> when stock drew nothing and the ghost drew dust
        /// while <see cref="DustAboveReachGhostJudged"/> is false (G.8 item 3).
        /// </summary>
        internal static string JudgeDustControl(string controlId, PartParityDustControlEvidence e, out string detail)
        {
            detail = null;
            if (e == null) { detail = "no evidence"; return TestCommandPartParitySweep.ControlsBad; }
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlDustRealVsReal:
                    if (TestCommandPartParitySweep.IsSetupFault(e.SetupStatus)) return SetupBad(e.SetupStatus, out detail);
                    return Require(e.FailingDustMeasures.Count == 0, "fails " + DustNames(e.FailingDustMeasures), out detail);
                case TestCommandPartParitySweep.ControlDustOff:
                    if (TestCommandPartParitySweep.IsSetupFault(e.SetupStatus)) return SetupBad(e.SetupStatus, out detail);
                    if (!e.FailingDustMeasures.Contains(MeasurePresence)) return Require(false, "dust presence passes", out detail);
                    return Require(e.FailingFlameMeasures.Count == 0, "the flame layer fails " + DustNames(e.FailingFlameMeasures), out detail);
                case TestCommandPartParitySweep.ControlDustAboveReach:
                    // Above the reach the stock side is MEANT to miss: dust-no-stock-hit is its
                    // pass condition here, never a setup fault.
                    if (TestCommandPartParitySweep.IsSetupFault(e.SetupStatus)
                        && !string.Equals(e.SetupStatus, TestCommandPartParitySweep.SetupDustNoStockHit, StringComparison.Ordinal))
                        return SetupBad(e.SetupStatus, out detail);
                    if (e.StockRaycastHit || e.RealDustCovered) return Require(false, "stock drew dust past its reach", out detail);
                    if (!e.GhostDustCovered) return TestCommandPartParitySweep.ControlsOk;
                    if (!DustAboveReachGhostJudged)
                    {
                        detail = "the ghost draws dust past stock's reach (G.8 item 3)";
                        return TestCommandPartParitySweep.ControlsNotApplicable;
                    }
                    return Require(false, "the ghost draws dust past stock's reach", out detail);
                default:
                    detail = "not a dust control: " + (controlId ?? "<none>");
                    return TestCommandPartParitySweep.ControlsBad;
            }
        }

        private static string SetupBad(string status, out string detail)
        {
            detail = "setup " + (status ?? "<none>");
            return TestCommandPartParitySweep.ControlsBad;
        }

        private static string RequireMeasures(HashSet<string> failing, string[] mustFail, string[] mustPass, out string detail)
        {
            foreach (string m in mustFail)
                if (!failing.Contains(m)) return Require(false, m + " passes", out detail);
            foreach (string m in mustPass)
                if (failing.Contains(m)) return Require(false, m + " fails", out detail);
            detail = null;
            return TestCommandPartParitySweep.ControlsOk;
        }

        private static string BurstNames(HashSet<string> set) => OrderedNames(BurstMeasures, set);

        private static string DustNames(HashSet<string> set) => OrderedNames(DustMeasures, set);

        private static string OrderedNames(string[] order, HashSet<string> set)
        {
            var list = new List<string>();
            foreach (string m in order) if (set.Contains(m)) list.Add(m);
            return string.Join(",", list.ToArray());
        }
    }
}
