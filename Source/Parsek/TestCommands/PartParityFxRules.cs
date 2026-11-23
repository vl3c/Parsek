using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>What an FX case drives and how (design Appendix E.1, E.2): not part of the
    /// case identity; the case id reads the key's State and Context.</summary>
    internal sealed class PartParityFxOption
    {
        /// <summary>One of <see cref="PartParityFxRules.Families"/>.</summary>
        internal string Family;

        /// <summary>The <c>moduleName</c> of the module the case drives.</summary>
        internal string Module;

        /// <summary>That module's prefab index (the same-word tiebreak and the case id read it).</summary>
        internal int ModuleIndex;

        /// <summary>The module index the recorder keys the case's events by: for a plume or
        /// rcs case the module's position among the part's <c>as ModuleEngines</c> /
        /// <c>as ModuleRCS</c> modules (<c>FlightRecorder</c>'s engine and RCS caches,
        /// <c>EncodeEngineKey</c>'s <c>midx</c>); for a heat or light case the prefab index
        /// (<c>PollAnimateHeat</c> and the light check key on it).</summary>
        internal int RecorderKeyIndex;

        /// <summary>The state word, without the same-word tiebreak.</summary>
        internal string State;

        /// <summary>The case context: <c>alone</c>, <c>plate</c> or <c>air</c>.</summary>
        internal string Context;

        /// <summary>A plume case's requested throttle (1.0 or 0.3); NaN for every other family.</summary>
        internal double Throttle = double.NaN;

        /// <summary>A heat case's target scalar (0.5 or 0.999); NaN for every other family.</summary>
        internal double HeatScalar = double.NaN;

        /// <summary>The engine breathes air (a <c>PROPELLANT</c> named <c>IntakeAir</c>).</summary>
        internal bool AirBreathing;

        /// <summary>The engine's <c>throttleLocked</c> (every solid booster).</summary>
        internal bool ThrottleLocked;

        /// <summary>The engine's <c>useEngineResponseTime</c> (a spooling engine).</summary>
        internal bool UseEngineResponseTime;

        /// <summary>The engine's <c>engineID</c>; null when it names none.</summary>
        internal string EngineId;

        /// <summary>The part carries a <c>MultiModeEngine</c> (the press picks this module's mode).</summary>
        internal bool MultiMode;

        /// <summary><see cref="PartParityFxRules.ReferenceWaterfall"/> for a case judged
        /// reference-only (E.6), else null.</summary>
        internal string Reference;
    }

    /// <summary>A module (or part) an FX run leaves out, and why (design E.1).</summary>
    internal sealed class PartParityFxExclusion
    {
        internal string Part;

        /// <summary>The family the module would belong to; null for a family-less exclusion.</summary>
        internal string Family;

        internal string Module;
        internal int ModuleIndex = -1;

        /// <summary>One of <see cref="PartParityFxRules.ExclusionReasons"/>.</summary>
        internal string Reason;

        /// <summary>The context of the case the exclusion stands for (<c>air</c> for an air-breathing
        /// engine's cases), so a run lists only its own (<see cref="PartParityFxRules.ExclusionApplies"/>).</summary>
        internal string Context = PartParityStateRules.ContextAlone;
    }

    /// <summary>The FX cases of one part (or several) and what was left out.</summary>
    internal sealed class PartParityFxEnumeration
    {
        internal readonly List<PartParityCaseKey> Cases = new List<PartParityCaseKey>();
        internal readonly List<PartParityFxExclusion> Exclusions = new List<PartParityFxExclusion>();

        internal void AddRange(PartParityFxEnumeration other)
        {
            if (other == null) return;
            Cases.AddRange(other.Cases);
            Exclusions.AddRange(other.Exclusions);
        }
    }

    /// <summary>One RCS thruster transform as <see cref="PartParityFxRules.ChooseRcsInput"/>
    /// reads it: the axis stock dots with the input (the thruster's <c>up</c>, or
    /// <c>forward</c> with <c>useZaxis</c>), in the case vessel's <c>ReferenceTransform</c>
    /// frame, and whether stock fires it at all.</summary>
    internal sealed class PartParityRcsThruster
    {
        internal double[] Axis;

        /// <summary>The thruster's GameObject is active in the hierarchy.</summary>
        internal bool Active = true;

        /// <summary>The transform sits at the world origin (stock skips such a thruster).</summary>
        internal bool AtOrigin;
    }

    /// <summary>The RCS input a case presses (design E.2): the direction in the reference
    /// frame, each nozzle's predicted force and the nozzles stock fires.</summary>
    internal sealed class PartParityRcsChoice
    {
        internal double[] Direction;
        internal double[] Forces;

        /// <summary>Nozzles with a predicted force above <see cref="PartParityFxRules.RcsFireEpsilon"/>.</summary>
        internal readonly List<int> Fired = new List<int>();

        /// <summary>Nozzles at <see cref="PartParityFxRules.RcsEvenFraction"/> of the strongest or more.</summary>
        internal int Counted;
        internal double Spread;
        internal double Mean;
    }

    /// <summary>What one install says about a candidate control subject (design E.5).</summary>
    internal sealed class PartParityFxSubjectFacts
    {
        internal bool Present;

        /// <summary>The prefab carries a <c>ModuleWaterfallFX</c>.</summary>
        internal bool HasWaterfall;

        /// <summary>The <c>UnityEngine.LightType</c> names of the prefab's <c>Light</c> components.</summary>
        internal List<string> LightTypes = new List<string>();

        /// <summary>The prefab's model carries the <c>ModuleAnimateHeat</c> clip the module names.</summary>
        internal bool HasHeatClip;
    }

    /// <summary>The subject an FX control runs on, or why it has none.</summary>
    internal sealed class PartParityFxSubjectChoice
    {
        internal string ControlId;

        /// <summary>The chosen part; null when none is eligible.</summary>
        internal string Subject;

        /// <summary>Null when a subject was chosen, else <c>bad</c> (a required control) or
        /// <c>n/a</c> (<see cref="TestCommandPartParitySweep.OptionalControlIds"/>).</summary>
        internal string Outcome;
        internal string Detail;
    }

    /// <summary>What an FX control's judge reads (design E.5): the setup status, the
    /// measures that failed (names of <see cref="PartParityFxRules.Measures"/>) and the facts
    /// each control's required outcome names.</summary>
    internal sealed class PartParityFxControlEvidence
    {
        internal string SetupStatus = TestCommandPartParitySweep.SetupOk;
        internal readonly HashSet<string> FailingMeasures = new HashSet<string>(StringComparer.Ordinal);
        internal bool RealCorePresent;
        internal int EventsRecorded;
        internal bool AllCoverageZero;
        internal bool GhostCoverageZero;
        internal bool RealCoverageNonZero;
        internal bool RealPlateLit;
        internal bool PlateViewsFail;
    }

    /// <summary>Which wait an FX wave admits against (design E.2, E.8).</summary>
    internal enum PartParityFxBucket
    {
        /// <summary>A plume with no spool: the 10 s press timeout.</summary>
        Burn,

        /// <summary>A plume of an engine with <c>useEngineResponseTime</c>: 45 s.</summary>
        Spool,

        /// <summary>An RCS puff.</summary>
        Rcs,

        /// <summary>A heat level (deterministic, P6's rate).</summary>
        Heat,

        /// <summary>A lamp on the plate (deterministic, P6's rate).</summary>
        Plate,
    }

    /// <summary>
    /// The pure FX rules of the part parity sweep (design-part-ghost-parity.md Appendix E,
    /// piece F1): the four FX families, each matched with the SAME predicate as its recorder
    /// poller (<c>FlightRecorder</c>'s engine and RCS caches, <c>PartStatePoller.PollAnimateHeat</c>,
    /// the light check); the <c>fx=</c> words, the state words and the <c>plate</c> context;
    /// the exclusions; the RCS input rule; the control subjects; the renderer layer rule;
    /// the timeouts and the wave bound; every measure constant and threshold default of E.4;
    /// the FX control judges. P6's tables (<see cref="PartParityStateRules.Families"/>,
    /// <see cref="PartParityStateRules.OrbitContexts"/>, its exclusions) do not change: heat
    /// and <c>plate</c> live here only.
    /// </summary>
    internal static class PartParityFxRules
    {
        // ----- families (E.1 table order) -----

        internal const string FamilyPlume = "plume";
        internal const string FamilyRcs = "rcs";
        internal const string FamilyHeat = "heat";
        internal const string FamilyLight = PartParityStateRules.FamilyLight;

        /// <summary>Every FX family, in the E.1 table order (after the P6 families in the viewer).</summary>
        internal static readonly string[] Families = { FamilyPlume, FamilyRcs, FamilyHeat, FamilyLight };

        /// <summary>The stochastic families (the E.4 instrument); the others are P6 state cases.</summary>
        internal static readonly string[] StochasticFamilies = { FamilyPlume, FamilyRcs };

        // ----- the fx= words (E.5) -----

        internal const string ArgPlume = "plume";
        internal const string ArgRcs = "rcs";
        internal const string ArgHeat = "heat";
        internal const string ArgPlate = "plate";

        /// <summary><c>fx=all</c>: every FX word.</summary>
        internal const string ArgAllToken = "all";

        /// <summary>Every word an <c>fx=</c> csv may name, in canonical order.</summary>
        internal static readonly string[] ArgWords = { ArgPlume, ArgRcs, ArgHeat, ArgPlate };

        /// <summary>The family an <c>fx=</c> word selects (<c>plate</c> selects the lamps); null
        /// for an unknown word.</summary>
        internal static string FamilyForArg(string word)
        {
            switch (word)
            {
                case ArgPlume: return FamilyPlume;
                case ArgRcs: return FamilyRcs;
                case ArgHeat: return FamilyHeat;
                case ArgPlate: return FamilyLight;
                default: return null;
            }
        }

        // ----- state words and contexts (E.1, E.5) -----

        internal const string StateBurnFull = "burn-full";
        internal const string StateBurnLow = "burn-low";
        internal const string StateRcsFire = "rcs-fire";
        internal const string StateHeatMedium = "heat-medium";
        internal const string StateHeatHot = "heat-hot";
        internal const string StateLit = PartParityStateRules.StateLit;

        /// <summary>Every FX state word, in canonical order.</summary>
        internal static readonly string[] States =
            { StateBurnFull, StateBurnLow, StateRcsFire, StateHeatMedium, StateHeatHot, StateLit };

        /// <summary>A lamp's context: the light it casts on a matte plate.</summary>
        internal const string ContextPlate = "plate";

        /// <summary>The contexts an orbit FX run plans (<c>contexts=none</c>).</summary>
        internal static readonly string[] OrbitContexts = { PartParityStateRules.ContextAlone, ContextPlate };

        // ----- the type and module names the predicates match -----

        internal const string TypeEngines = PartParityStateRules.TypeEngines;
        internal const string TypeRcs = PartParityStateRules.TypeRcs;
        internal const string ModuleAnimateHeat = PartParityStateRules.ModuleAnimateHeat;
        internal const string TypeLight = PartParityStateRules.TypeLight;
        internal const string TypeAblator = PartParityStateRules.TypeAblator;
        internal const string ModuleMultiModeEngine = "MultiModeEngine";
        internal const string ModuleWaterfallFx = "ModuleWaterfallFX";

        // Config keys and their stock defaults (decompiled KSP 1.12.5 ModuleEngines field initializers).
        internal const string KeyThrottleLocked = "throttleLocked";              // default false
        internal const string KeyUseEngineResponseTime = "useEngineResponseTime"; // default false
        internal const string KeyEngineId = "engineID";
        internal const string NodePropellant = "PROPELLANT";
        internal const string KeyPropellantName = "name";

        /// <summary>The propellant that makes an engine air-breathing.</summary>
        internal const string AirPropellant = "IntakeAir";

        /// <summary>A case's <c>fx.reference</c> on a part with a <c>ModuleWaterfallFX</c> (E.6).</summary>
        internal const string ReferenceWaterfall = "waterfall";

        // ----- exclusions (E.1) -----

        internal const string ExcludeReentryCamera = "fx-reentry-camera";
        internal const string ExcludeBlackbody = "fx-blackbody";
        internal const string ExcludeAblatorChar = "fx-ablator-char";
        internal const string ExcludeNeedsAir = "fx-needs-air";
        internal const string ExcludeThrottleLocked = "fx-throttle-locked";

        /// <summary>Every FX exclusion reason. The enumeration writes the last three per
        /// module; reentry fire and blackbody glow are out of every part case (R1 covers
        /// them), so no part names them.</summary>
        internal static readonly string[] ExclusionReasons =
        {
            ExcludeReentryCamera, ExcludeBlackbody, ExcludeAblatorChar, ExcludeNeedsAir,
            ExcludeThrottleLocked,
        };

        // ----- drive (E.2) -----

        internal const double BurnFullThrottle = 1.0;
        internal const double BurnLowThrottle = 0.3;

        /// <summary>The <c>currentThrottle</c> a plume's done signal reads within this of the request.</summary>
        internal const double ThrottleDoneTolerance = 0.01;

        internal const double PlumeTimeoutSeconds = 10.0;

        /// <summary>A spooling engine (<c>useEngineResponseTime</c>): a jet's spool has a
        /// time constant of about 8 s, so it reaches 0.99 of the request after about 38 s (E0 U1).</summary>
        internal const double SpoolTimeoutSeconds = 45.0;

        internal const double RcsTimeoutSeconds = 10.0;
        internal const double HeatTimeoutSeconds = 5.0;
        internal const double PlateTimeoutSeconds = PartParityStateRules.QuickStateTimeoutSeconds;

        /// <summary>The heat scalars the ghost samples its heat clip at (<c>SampleHeatAnimation3State</c>):
        /// medium at 0.5, hot at 0.999 (1.0 would overheat the part).</summary>
        internal const double HeatMediumScalar = 0.5;
        internal const double HeatHotScalar = 0.999;
        internal const double HeatDoneTolerance = 0.01;

        /// <summary>The hold's render-time drift gate (E0 U3); the per-step displacement is logged only.</summary>
        internal const double HoldMaxDriftM = 0.001;

        // ----- RCS (E.2, E0 U2) -----

        /// <summary>A nozzle counts toward the even-firing score at this fraction of the strongest.</summary>
        internal const double RcsEvenFraction = 0.5;

        /// <summary>A nozzle fires when its force is above this.</summary>
        internal const double RcsFireEpsilon = 0.01;

        // ----- warm-up and plateau (E.2) -----

        internal const double PlateauWindowSeconds = 0.5;
        internal const double PlateauMaxChange = 0.10;
        internal const double PlateauCapSeconds = 8.0;

        /// <summary>The coverage signal of a side with no readable particles: one flame render
        /// of this many pixels per <see cref="PlateauCoveragePeriodSeconds"/>.</summary>
        internal const int PlateauCoverageTilePx = 128;
        internal const double PlateauCoveragePeriodSeconds = 0.25;

        internal const string PlateauSignalParticles = "particles";
        internal const string PlateauSignalCoverage = "coverage";

        // ----- capture (E.3) -----

        /// <summary>Samples per FX view (N); a tile's alpha encodes k of N exactly for N up to 255.</summary>
        internal const int Samples = 16;
        internal const int MaxSamples = 255;

        internal const string ViewFxSideA = "fx-side-a";
        internal const string ViewFxSideB = "fx-side-b";
        internal const string ViewFxBottom = "fx-bottom";
        internal const string ViewFxFar = "fx-far";

        /// <summary>The four FX views, the FX atlas's columns.</summary>
        internal static readonly string[] Views = { ViewFxSideA, ViewFxSideB, ViewFxBottom, ViewFxFar };

        internal const string LayerBody = "body";
        internal const string LayerFlame = "flame";
        internal const string LayerSmoke = "smoke";

        /// <summary>The renderer layers (<see cref="ClassifyFxRenderer"/>).</summary>
        internal static readonly string[] RendererLayers = { LayerBody, LayerFlame, LayerSmoke };

        /// <summary>The two FX layers the instrument measures.</summary>
        internal static readonly string[] FxLayers = { LayerFlame, LayerSmoke };

        internal const string SideReal = "real";
        internal const string SideGhost = "ghost";
        internal const string SideGhostStart = "ghostStart";
        internal const string SideGhostAll = "ghostAll";

        /// <summary>Every side an FX case may capture (<c>ghostAll</c> on an rcs case only).</summary>
        internal static readonly string[] Sides = { SideReal, SideGhost, SideGhostStart, SideGhostAll };

        /// <summary>The strip: frames of <c>fx-side-a</c> per side, one every second sample frame.</summary>
        internal const int StripFrames = 12;
        internal const int StripEverySampleFrames = 2;

        internal const double FramingNearParticleM = 200.0;
        internal const double FramingClampM = 80.0;
        internal const double FramingPercentile = 0.95;
        internal const double FramingBodyLengths = 3.0;
        internal const double FramingBottomWiden = 2.0;

        internal const double PlateSpotDistanceM = 3.0;
        internal const double PlatePointDistanceM = 1.0;
        internal const double PlateLitMinPct = 0.02;
        internal const int PlateLitMinLum = 8;

        internal const string LightTypeSpot = "Spot";
        internal const string LightTypePoint = "Point";

        // ----- the measures (E.4) -----

        /// <summary>CORE is k at least N times this; ENVELOPE is k at least 1.</summary>
        internal const double CoreFraction = 0.5;

        /// <summary>A smoke sample covers a pixel at this alpha byte or more.</summary>
        internal const int AlphaCovered = 128;

        /// <summary>A flame sample covers a pixel when its max channel is this or more.</summary>
        internal const int FlameValueMin = 16;

        /// <summary>A channel at this value is clipped (colour and brightness skip the pixel).</summary>
        internal const int SaturatedValue = 255;

        internal const int HueBins = 12;
        internal const double LuminanceR = 0.2126;
        internal const double LuminanceG = 0.7152;
        internal const double LuminanceB = 0.0722;

        internal const int PresenceMinPx = 64;
        internal const int SmokeMinPx = 256;
        internal const double SplitHalfPct = 0.10;

        // Fail defaults (starting guesses, re-pinned from the reading runs); warn is the stated fraction.
        internal const double OriginFailMinPx = 4.0;
        internal const double OriginFailLengthFraction = 0.10;
        internal const double LateralFailMinPx = 4.0;
        internal const double LateralFailLengthFraction = 0.05;
        internal const double OffsetWarnFraction = 0.5;
        internal const double LengthFailFraction = 0.25;
        internal const double LengthWarnFraction = 0.125;
        internal const double WidthFailFraction = 0.30;
        internal const double WidthWarnFraction = 0.15;
        internal const double DirectionFailDeg = 10.0;
        internal const double DirectionWarnDeg = 5.0;
        internal const double CrossSectionAreaFailMin = 0.6;
        internal const double CrossSectionAreaFailMax = 1.6;
        internal const double CrossSectionAreaWarnMin = 0.8;
        internal const double CrossSectionAreaWarnMax = 1.25;
        internal const double CrossSectionOffsetFail = 0.1;
        internal const double ChromaFail = 0.06;
        internal const double HueL1Fail = 0.5;
        internal const double ColourWarnFraction = 0.25;
        internal const double BrightnessWarnMin = 0.5;
        internal const double BrightnessWarnMax = 2.0;
        internal const double CoreIouWarnMin = 0.5;

        internal const string MeasurePresence = "presence";
        internal const string MeasureOrigin = "origin";
        internal const string MeasureLateral = "lateral";
        internal const string MeasureLength = "length";
        internal const string MeasureWidth = "width";
        internal const string MeasureDirection = "direction";
        internal const string MeasureCrossSection = "cross-section";
        internal const string MeasureColour = "colour";
        internal const string MeasureBrightness = "brightness";
        internal const string MeasureSmoke = "smoke";
        internal const string MeasureCoreIou = "core-iou";

        /// <summary>Every FX measure, in the E.4 table order.</summary>
        internal static readonly string[] Measures =
        {
            MeasurePresence, MeasureOrigin, MeasureLateral, MeasureLength, MeasureWidth,
            MeasureDirection, MeasureCrossSection, MeasureColour, MeasureBrightness, MeasureSmoke,
            MeasureCoreIou,
        };

        /// <summary>The measures an rcs case reports and never judges (R4: they depend on the
        /// power the ghost's all-nozzle firing sets).</summary>
        internal static readonly string[] RcsReportOnlyMeasures = { MeasureLength, MeasureWidth, MeasureBrightness };

        internal const string ReasonFxMissing = "fx-missing";
        internal const string ReasonFxExtra = "fx-extra";
        internal const string ReasonSmokeMissing = "smoke-missing";
        internal const string ReasonSmokeExtra = "smoke-extra";

        // ----- flags, tags and caps (E.2 to E.5) -----

        internal const string TagFxNotPlateauedPrefix = "fx-not-plateaued:";
        internal const string CapFxUnsettled = "fx-unsettled";
        internal const string FlagFxFramingClamped = "fx-framing-clamped";
        internal const string FlagRcsKnownDifferencePrefix = "rcs-known-difference:";

        // ----- waves (E.8) -----

        internal const int MaxFxWaveCases = 4;

        /// <summary>One plume or rcs case's accumulation: 16 sample frames on each of four
        /// views of about 35 ms each (E0 U6), plus the strip.</summary>
        internal const double FxCaptureSecondsPerCase = 3.0;

        /// <summary>One plate case's capture: four views, two renders.</summary>
        internal const double PlateCaptureSecondsPerCase = 0.5;

        /// <summary>The literal the per-step FX count line starts with.</summary>
        internal const string FxCountsLiteral = "fx cases:";

        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // =====================================================================================
        // enumeration (E.1)
        // =====================================================================================

        /// <summary>
        /// The FX cases of one part and what it leaves out (E.1), at <paramref name="baseKey"/>'s
        /// part, source and base skin (ruling R2). Plume: every <c>as ModuleEngines</c> module
        /// (<c>FlightRecorder.CheckEngineState</c>), <c>burn-full</c> and <c>burn-low</c> (a
        /// throttle-locked engine: <c>burn-full</c> only, <c>fx-throttle-locked</c>), in the
        /// <c>air</c> context when it breathes air (<c>fx-needs-air</c> for an orbit run). RCS:
        /// every <c>as ModuleRCS</c> module, <c>rcs-fire</c>. Heat: every module named
        /// <c>ModuleAnimateHeat</c> (<c>PollAnimateHeat</c>), <c>heat-medium</c> and
        /// <c>heat-hot</c>. A lamp: the first <c>ModuleLight</c> (the recorder's light check),
        /// <c>lit</c> in the <c>plate</c> context. A <c>ModuleAblator</c> is <c>fx-ablator-char</c>.
        /// </summary>
        internal static PartParityFxEnumeration Enumerate(PartParityStatePart part, PartParityCaseKey baseKey)
        {
            var r = new PartParityFxEnumeration();
            if (part == null || part.Modules == null) return r;
            string name = part.Name ?? baseKey?.Part;
            bool waterfall = false, multiMode = false;
            foreach (PartParityStateModule m in part.Modules)
            {
                if (m == null) continue;
                if (m.Named(ModuleWaterfallFx)) waterfall = true;
                if (m.Named(ModuleMultiModeEngine)) multiMode = true;
            }
            var cases = new List<PartParityFxOption>();
            int engineKey = 0, rcsKey = 0;
            foreach (PartParityStateModule m in part.Modules)
            {
                if (m == null) continue;
                if (m.Implements(TypeEngines)) AddPlume(name, m, engineKey++, multiMode, waterfall, cases, r);
                if (m.Implements(TypeRcs))
                {
                    PartParityFxOption rcs = Option(FamilyRcs, m, StateRcsFire, PartParityStateRules.ContextAlone, waterfall);
                    rcs.RecorderKeyIndex = rcsKey++;
                    cases.Add(rcs);
                }
                if (m.Named(ModuleAnimateHeat))
                {
                    cases.Add(HeatOption(m, StateHeatMedium, HeatMediumScalar));
                    cases.Add(HeatOption(m, StateHeatHot, HeatHotScalar));
                }
                if (m.Implements(TypeAblator))
                    r.Exclusions.Add(new PartParityFxExclusion
                        { Part = name, Module = m.Name, ModuleIndex = m.Index, Reason = ExcludeAblatorChar });
            }
            foreach (PartParityStateModule m in part.Modules)
            {
                if (m == null || !m.Implements(TypeLight)) continue;
                cases.Add(Option(FamilyLight, m, StateLit, ContextPlate, false));
                break;
            }
            BuildKeys(cases, baseKey, name, r);
            return r;
        }

        private static void AddPlume(string part, PartParityStateModule m, int keyIndex, bool multiMode, bool waterfall,
            List<PartParityFxOption> cases, PartParityFxEnumeration r)
        {
            bool air = HasPropellant(m, AirPropellant);
            bool locked = PartParityStateRules.ParseBool(m.Value(KeyThrottleLocked), false);
            string context = air ? PartParityStateRules.ContextAir : PartParityStateRules.ContextAlone;
            PartParityFxOption full = PlumeOption(m, StateBurnFull, BurnFullThrottle, context, air, locked, multiMode, waterfall);
            full.RecorderKeyIndex = keyIndex;
            cases.Add(full);
            if (locked)
                r.Exclusions.Add(new PartParityFxExclusion
                {
                    Part = part, Family = FamilyPlume, Module = m.Name, ModuleIndex = m.Index,
                    Reason = ExcludeThrottleLocked, Context = context,
                });
            else
            {
                PartParityFxOption low = PlumeOption(m, StateBurnLow, BurnLowThrottle, context, air, locked, multiMode, waterfall);
                low.RecorderKeyIndex = keyIndex;
                cases.Add(low);
            }
            if (air)
                r.Exclusions.Add(new PartParityFxExclusion
                {
                    Part = part, Family = FamilyPlume, Module = m.Name, ModuleIndex = m.Index,
                    Reason = ExcludeNeedsAir, Context = PartParityStateRules.ContextAlone,
                });
        }

        private static PartParityFxOption PlumeOption(PartParityStateModule m, string state, double throttle,
            string context, bool air, bool locked, bool multiMode, bool waterfall)
        {
            PartParityFxOption o = Option(FamilyPlume, m, state, context, waterfall);
            o.Throttle = throttle;
            o.AirBreathing = air;
            o.ThrottleLocked = locked;
            o.UseEngineResponseTime = PartParityStateRules.ParseBool(m.Value(KeyUseEngineResponseTime), false);
            o.EngineId = m.Value(KeyEngineId);
            o.MultiMode = multiMode;
            return o;
        }

        private static PartParityFxOption HeatOption(PartParityStateModule m, string state, double scalar)
        {
            PartParityFxOption o = Option(FamilyHeat, m, state, PartParityStateRules.ContextAlone, false);
            o.HeatScalar = scalar;
            return o;
        }

        private static PartParityFxOption Option(string family, PartParityStateModule m, string state,
            string context, bool waterfall)
            => new PartParityFxOption
            {
                Family = family, Module = m.Name, ModuleIndex = m.Index, RecorderKeyIndex = m.Index,
                State = state, Context = context,
                Reference = waterfall && (family == FamilyPlume || family == FamilyRcs) ? ReferenceWaterfall : null,
            };

        /// <summary>Whether the module's MODULE node has a <c>PROPELLANT</c> child of that name.</summary>
        internal static bool HasPropellant(PartParityStateModule m, string propellant)
        {
            if (m?.Config == null) return false;
            foreach (ConfigNode p in m.Config.GetNodes(NodePropellant))
                if (string.Equals(p.GetValue(KeyPropellantName), propellant, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The case keys, the same-word tiebreak of D.1 applied over (word, context).</summary>
        private static void BuildKeys(List<PartParityFxOption> cases, PartParityCaseKey baseKey, string name,
            PartParityFxEnumeration r)
        {
            string part = baseKey?.Part ?? name;
            string source = baseKey?.Source;
            string skin = baseKey?.Skin ?? TestCommandPartParitySweep.DefaultSkinName;
            foreach (PartParityFxOption o in cases)
            {
                int same = 0;
                foreach (PartParityFxOption other in cases)
                    if (other.State == o.State && other.Context == o.Context) same++;
                r.Cases.Add(new PartParityCaseKey
                {
                    Part = part, Source = source, Skin = skin, SkinOrdinal = 0,
                    State = same > 1 ? PartParityStateRules.TiebreakWord(o.State, o.ModuleIndex) : o.State,
                    Context = o.Context, FxOption = o,
                });
            }
        }

        /// <summary>Whether an exclusion belongs in a run's list: an air run lists only the
        /// exclusions of air cases, an orbit run every other one.</summary>
        internal static bool ExclusionApplies(PartParityFxExclusion e, bool airRun)
            => e != null && (string.Equals(e.Context, PartParityStateRules.ContextAir, StringComparison.Ordinal) == airRun);

        /// <summary>Whether an FX key belongs to a run: its family is one the run's <c>fx=</c>
        /// words select, and its context is the air context exactly when the run is an air run.</summary>
        internal static bool KeySelected(PartParityCaseKey key, IList<string> fxWords, bool airRun)
        {
            PartParityFxOption o = key?.FxOption;
            if (o == null || fxWords == null) return false;
            bool family = false;
            foreach (string w in fxWords)
                if (string.Equals(FamilyForArg(w), o.Family, StringComparison.Ordinal)) { family = true; break; }
            if (!family) return false;
            bool air = string.Equals(o.Context, PartParityStateRules.ContextAir, StringComparison.Ordinal);
            return air == airRun;
        }

        // =====================================================================================
        // counts (the per-step Info line)
        // =====================================================================================

        /// <summary>
        /// <c>fx cases: fx=&lt;..&gt; contexts=&lt;..&gt; install=&lt;n&gt; (&lt;family&gt;=&lt;n&gt; ...)
        /// planned=&lt;n&gt; excluded=&lt;n&gt; (&lt;reason&gt;=&lt;n&gt; ...)</c>, families and reasons in
        /// their table order, zero counts left out.
        /// </summary>
        internal static string FormatCounts(string fx, string contexts, IEnumerable<PartParityCaseKey> install,
            int planned, IEnumerable<PartParityFxExclusion> excluded)
        {
            var byFamily = new Dictionary<string, int>(StringComparer.Ordinal);
            int total = 0;
            if (install != null)
                foreach (var k in install)
                {
                    if (k?.FxOption == null) continue;
                    total++;
                    Bump(byFamily, k.FxOption.Family);
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
            var sb = new StringBuilder(FxCountsLiteral);
            sb.Append(" fx=").Append(fx ?? TestCommandPartParitySweep.ArgNoneToken)
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
        // drive (E.2)
        // =====================================================================================

        /// <summary>The <c>independentThrottlePercentage</c> a plume case authors: 100 or 30.</summary>
        internal static int IndependentThrottlePercentageFor(PartParityFxOption o)
            => o != null && o.State == StateBurnLow ? 30 : 100;

        /// <summary>The press timeout of a case before <c>state-timeout</c>.</summary>
        internal static double PressTimeoutSeconds(PartParityFxOption o)
        {
            switch (BucketFor(o))
            {
                case PartParityFxBucket.Spool: return SpoolTimeoutSeconds;
                case PartParityFxBucket.Rcs: return RcsTimeoutSeconds;
                case PartParityFxBucket.Heat: return HeatTimeoutSeconds;
                case PartParityFxBucket.Plate: return PlateTimeoutSeconds;
                default: return PlumeTimeoutSeconds;
            }
        }

        /// <summary>The wave bucket of a case.</summary>
        internal static PartParityFxBucket BucketFor(PartParityFxOption o)
        {
            switch (o?.Family)
            {
                case FamilyRcs: return PartParityFxBucket.Rcs;
                case FamilyHeat: return PartParityFxBucket.Heat;
                case FamilyLight: return PartParityFxBucket.Plate;
                default: return o != null && o.UseEngineResponseTime ? PartParityFxBucket.Spool : PartParityFxBucket.Burn;
            }
        }

        /// <summary>
        /// A plume's done signal (E.2): the recorder's ignition predicate holds
        /// (<c>FlightRecorder.ShouldRecordEngineAsIgnited</c>), <c>currentThrottle</c> is within
        /// <see cref="ThrottleDoneTolerance"/> of the request, and the event route has recorded
        /// <c>EngineIgnited</c>.
        /// </summary>
        internal static bool PlumeReached(bool recorderIgnited, double currentThrottle, double requested, bool ignitedRecorded)
            => recorderIgnited && ignitedRecorded && !double.IsNaN(currentThrottle) && !double.IsNaN(requested)
               && Math.Abs(currentThrottle - requested) <= ThrottleDoneTolerance;

        /// <summary>
        /// The temperature a heat case writes for <paramref name="scalar"/>, inverting stock's
        /// <c>(T + lerpOffset) * lerpDivRecip</c> over the module's LIVE lerp fields
        /// (decompiled <c>ModuleAnimateHeat.HeatEffectStartup</c> / <c>UpdateHeatEffect</c>);
        /// NaN when the reciprocal cannot be inverted.
        /// </summary>
        internal static double HeatTemperatureFor(double scalar, double lerpOffset, double lerpDivRecip)
        {
            if (double.IsNaN(lerpDivRecip) || double.IsInfinity(lerpDivRecip) || lerpDivRecip <= 0.0
                || double.IsNaN(scalar) || double.IsNaN(lerpOffset))
                return double.NaN;
            return scalar / lerpDivRecip - lerpOffset;
        }

        /// <summary>A heat case is done when the recorder's heat reader reads the target within
        /// <see cref="HeatDoneTolerance"/> and <c>PollAnimateHeat</c> emitted the level's event.</summary>
        internal static bool HeatReached(double readScalar, double target, bool levelEventEmitted)
            => levelEventEmitted && !double.IsNaN(readScalar) && Math.Abs(readScalar - target) <= HeatDoneTolerance;

        /// <summary>The hold kept the case still at render time (E0 U3).</summary>
        internal static bool HoldOk(double maxRenderDriftM)
            => !double.IsNaN(maxRenderDriftM) && maxRenderDriftM < HoldMaxDriftM;

        // =====================================================================================
        // RCS input (E.2, E0 U2)
        // =====================================================================================

        /// <summary>The 26 directions of the unit cube in their fixed tie order: 6 faces, 12
        /// edges, 8 corners, each normalised.</summary>
        internal static List<double[]> RcsCandidateDirections()
        {
            var dirs = new List<double[]>();
            for (int axis = 0; axis < 3; axis++)
                for (int s = 1; s >= -1; s -= 2)
                {
                    var d = new double[3];
                    d[axis] = s;
                    dirs.Add(d);
                }
            int[][] pairs = { new[] { 0, 1 }, new[] { 0, 2 }, new[] { 1, 2 } };
            double e = 1.0 / Math.Sqrt(2.0);
            foreach (int[] p in pairs)
                foreach (int sa in new[] { 1, -1 })
                    foreach (int sb in new[] { 1, -1 })
                    {
                        var d = new double[3];
                        d[p[0]] = sa * e;
                        d[p[1]] = sb * e;
                        dirs.Add(d);
                    }
            double c = 1.0 / Math.Sqrt(3.0);
            foreach (int sx in new[] { 1, -1 })
                foreach (int sy in new[] { 1, -1 })
                    foreach (int sz in new[] { 1, -1 })
                        dirs.Add(new[] { sx * c, sy * c, sz * c });
            return dirs;
        }

        /// <summary>
        /// The RCS input that fires the most nozzles evenly (E.2), over the prefab's thrusters
        /// in the reference frame, before the spawn (so no trial input fires). Stock fires nozzle
        /// i at <c>Max(Dot(axis_i, input), 0)</c> and skips an inactive thruster or one at the
        /// world origin (decompiled <c>ModuleRCS.FixedUpdate</c>, E0 U2). A direction with a
        /// component on an axis the module disables (<paramref name="enableX"/> gates the
        /// reference x, <paramref name="enableZ"/> y, <paramref name="enableY"/> z, as
        /// <c>inputLin = (X, Z, Y)</c>) is not a candidate. Ranked by the nozzles at
        /// <see cref="RcsEvenFraction"/> of the strongest or more, then the smallest spread
        /// between the forces of every nozzle that fires (above <see cref="RcsFireEpsilon"/>, so
        /// a weak extra nozzle counts against a direction), then the largest mean of those
        /// forces; a tie keeps the earlier direction. Null when no direction fires any nozzle.
        /// </summary>
        internal static PartParityRcsChoice ChooseRcsInput(IList<PartParityRcsThruster> thrusters,
            bool enableX = true, bool enableY = true, bool enableZ = true)
        {
            if (thrusters == null || thrusters.Count == 0) return null;
            PartParityRcsChoice best = null;
            foreach (double[] d in RcsCandidateDirections())
            {
                if ((!enableX && d[0] != 0.0) || (!enableZ && d[1] != 0.0) || (!enableY && d[2] != 0.0)) continue;
                PartParityRcsChoice c = ScoreRcsDirection(thrusters, d);
                if (c == null) continue;
                if (best == null || RcsBetter(c, best)) best = c;
            }
            return best;
        }

        /// <summary>One candidate direction's score (null when it fires no nozzle).</summary>
        internal static PartParityRcsChoice ScoreRcsDirection(IList<PartParityRcsThruster> thrusters, double[] d)
        {
            var forces = new double[thrusters.Count];
            double strongest = 0.0;
            for (int i = 0; i < thrusters.Count; i++)
            {
                forces[i] = RcsNozzleForce(thrusters[i], d);
                strongest = Math.Max(strongest, forces[i]);
            }
            if (strongest <= RcsFireEpsilon) return null;
            var c = new PartParityRcsChoice { Direction = (double[])d.Clone(), Forces = forces };
            double min = double.MaxValue, max = 0.0, sum = 0.0;
            for (int i = 0; i < forces.Length; i++)
            {
                if (forces[i] >= RcsEvenFraction * strongest) c.Counted++;
                if (forces[i] <= RcsFireEpsilon) continue;
                c.Fired.Add(i);
                sum += forces[i];
                min = Math.Min(min, forces[i]);
                max = Math.Max(max, forces[i]);
            }
            c.Spread = max - min;
            c.Mean = sum / c.Fired.Count;
            return c;
        }

        /// <summary>One nozzle's force for a unit input direction: 0 for a thruster stock skips.</summary>
        internal static double RcsNozzleForce(PartParityRcsThruster t, double[] d)
        {
            if (t == null || !t.Active || t.AtOrigin || t.Axis == null || t.Axis.Length != 3 || d == null) return 0.0;
            double n = Math.Sqrt(t.Axis[0] * t.Axis[0] + t.Axis[1] * t.Axis[1] + t.Axis[2] * t.Axis[2]);
            if (n <= 0.0 || double.IsNaN(n)) return 0.0;
            double dot = (t.Axis[0] * d[0] + t.Axis[1] * d[1] + t.Axis[2] * d[2]) / n;
            return Math.Max(dot, 0.0);
        }

        private const double RcsScoreEpsilon = 1e-9;

        private static bool RcsBetter(PartParityRcsChoice a, PartParityRcsChoice b)
        {
            if (a.Counted != b.Counted) return a.Counted > b.Counted;
            if (Math.Abs(a.Spread - b.Spread) > RcsScoreEpsilon) return a.Spread < b.Spread;
            return a.Mean > b.Mean + RcsScoreEpsilon;
        }

        /// <summary>The <c>ctrlState</c> the press feeds for a reference-frame direction:
        /// <c>{X, Y, Z}</c> with <c>X = d.x</c>, <c>Z = d.y</c>, <c>Y = d.z</c>, the inverse of
        /// <c>ModuleRCS.Update</c>'s <c>inputLin = (X, Z, Y)</c>.</summary>
        internal static double[] RcsCtrlStateFor(double[] direction)
            => direction == null || direction.Length != 3 ? null : new[] { direction[0], direction[2], direction[1] };

        /// <summary>
        /// The press check (E0 U2): the nozzles stock fired (force above
        /// <see cref="RcsFireEpsilon"/>) are exactly the predicted ones; else
        /// <paramref name="detail"/> names both sets and the case is <c>rcs-input-mismatch</c>.
        /// </summary>
        internal static bool RcsFiredMatches(IList<int> predicted, IList<double> observedForces, out string detail)
        {
            var fired = new List<int>();
            if (observedForces != null)
                for (int i = 0; i < observedForces.Count; i++)
                    if (observedForces[i] > RcsFireEpsilon) fired.Add(i);
            var want = predicted != null ? new List<int>(predicted) : new List<int>();
            want.Sort();
            bool same = want.Count == fired.Count;
            for (int i = 0; same && i < want.Count; i++) same = want[i] == fired[i];
            detail = same ? null : "predicted=" + Join(want) + " fired=" + Join(fired);
            return same;
        }

        private static string Join(List<int> v)
        {
            var parts = new string[v.Count];
            for (int i = 0; i < v.Count; i++) parts[i] = v[i].ToString(Ic);
            return "[" + string.Join(",", parts) + "]";
        }

        /// <summary><c>rcsPowerRatio</c>: the recorded mean power over the mean fired-nozzle
        /// force; NaN when nothing fired.</summary>
        internal static double RcsPowerRatio(double recordedPower, IList<double> firedForces)
        {
            if (firedForces == null || firedForces.Count == 0 || double.IsNaN(recordedPower)) return double.NaN;
            double sum = 0.0;
            foreach (double f in firedForces) sum += f;
            double mean = sum / firedForces.Count;
            return mean > 0.0 ? recordedPower / mean : double.NaN;
        }

        /// <summary>The R4 known-difference flag: <c>rcs-known-difference:nozzles=&lt;fired&gt;/&lt;total&gt;,power=&lt;ratio&gt;</c>.</summary>
        internal static string FormatRcsKnownDifference(int fired, int total, double powerRatio)
            => FlagRcsKnownDifferencePrefix + "nozzles=" + fired.ToString(Ic) + "/" + total.ToString(Ic)
               + ",power=" + (double.IsNaN(powerRatio) || double.IsInfinity(powerRatio) ? "null" : powerRatio.ToString("0.000", Ic));

        // =====================================================================================
        // plateau (E.2)
        // =====================================================================================

        /// <summary>The signal a side plateaus on: its live particle count, or its flame
        /// coverage when it has no particle system or its flame systems read 0 particles while
        /// they draw (E0 U5).</summary>
        internal static string PlateauSignalFor(bool hasParticleSystems, bool flameReadsZeroWhileDrawing)
            => hasParticleSystems && !flameReadsZeroWhileDrawing ? PlateauSignalParticles : PlateauSignalCoverage;

        /// <summary>
        /// A side has plateaued once it ran at least its longest particle lifetime since its
        /// prime and its signal moved less than <see cref="PlateauMaxChange"/> of its largest
        /// value over the last <see cref="PlateauWindowSeconds"/> (a signal at 0 throughout is
        /// settled: a side that draws nothing is judged as missing, not waited on).
        /// </summary>
        internal static bool PlateauSettled(double runSeconds, double longestLifetimeSeconds,
            double windowMin, double windowMax)
        {
            if (double.IsNaN(runSeconds) || double.IsNaN(windowMin) || double.IsNaN(windowMax)) return false;
            double life = double.IsNaN(longestLifetimeSeconds) || longestLifetimeSeconds < 0.0 ? 0.0 : longestLifetimeSeconds;
            if (runSeconds < life) return false;
            if (windowMax <= 0.0) return true;
            return windowMax - windowMin < PlateauMaxChange * windowMax;
        }

        /// <summary>The <c>fx-not-plateaued:&lt;side&gt;</c> tag.</summary>
        internal static string NotPlateauedTag(string side) => TagFxNotPlateauedPrefix + (side ?? string.Empty);

        // =====================================================================================
        // layers (E.3)
        // =====================================================================================

        /// <summary>
        /// The layer of one renderer (E.3), over its type name and shader name: a mesh or
        /// skinned mesh renderer is <c>body</c>, unless its shader is a <c>Waterfall/</c> one
        /// (<c>flame</c>; a <c>Waterfall/Distortion</c> renderer refracts the frame and emits
        /// nothing, so it is in no layer, E0 U7); a particle renderer is <c>flame</c> with an
        /// additive shader and <c>smoke</c> with any other; every other renderer type is in no
        /// layer (null). A disabled renderer keeps its layer and draws nothing (E0 U8).
        /// </summary>
        internal static string ClassifyFxRenderer(string rendererType, string shaderName)
        {
            string shader = shaderName ?? string.Empty;
            switch (rendererType)
            {
                case "MeshRenderer":
                case "SkinnedMeshRenderer":
                    if (shader.StartsWith("Waterfall/Distortion", StringComparison.Ordinal)) return null;
                    return shader.StartsWith("Waterfall/", StringComparison.Ordinal) ? LayerFlame : LayerBody;
                case "ParticleSystemRenderer":
                    return shader.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0 ? LayerFlame : LayerSmoke;
                default:
                    return null;
            }
        }

        // =====================================================================================
        // the 8-bit tile rule (E.4): every measure reads these, so Python recomputes exactly
        // =====================================================================================

        /// <summary>A pixel covered in <paramref name="k"/> of <paramref name="n"/> samples
        /// writes alpha <c>floor(255 k / n + 0.5)</c>, in integers.</summary>
        internal static int TileAlpha(int k, int n)
            => n <= 0 || k <= 0 ? 0 : (510 * k + n) / (2 * n);

        /// <summary>The covered-sample count a tile's alpha encodes: <c>round(alpha n / 255)</c>,
        /// exact for n up to 255.</summary>
        internal static int CoveredSamples(int alpha, int n)
            => n <= 0 || alpha <= 0 ? 0 : (2 * alpha * n + 255) / 510;

        /// <summary>A tile channel: the covered samples' mean, <c>floor(sum / k + 0.5)</c>, in integers.</summary>
        internal static int TileChannel(long sum, int k)
            => k <= 0 ? 0 : (int)((2 * sum + k) / (2L * k));

        /// <summary>CORE: covered in at least <see cref="CoreFraction"/> of the samples.</summary>
        internal static bool IsCore(int k, int n) => n > 0 && k > 0 && 2 * k >= n;

        /// <summary>ENVELOPE: covered in at least one sample.</summary>
        internal static bool IsEnvelope(int k) => k >= 1;

        /// <summary>Whether one sample covers a pixel: a flame sample by its max channel (an
        /// additive render leaves alpha 0, E0 U4), a smoke sample by its alpha byte.</summary>
        internal static bool SampleCovered(string layer, int r, int g, int b, int a)
            => string.Equals(layer, LayerFlame, StringComparison.Ordinal)
                ? Math.Max(r, Math.Max(g, b)) >= FlameValueMin
                : a >= AlphaCovered;

        /// <summary>A pixel with a clipped channel (colour and brightness skip it).</summary>
        internal static bool IsSaturated(int r, int g, int b)
            => r >= SaturatedValue || g >= SaturatedValue || b >= SaturatedValue;

        /// <summary>Rec. 709 luminance of an 8-bit colour.</summary>
        internal static double Luminance(int r, int g, int b)
            => LuminanceR * r + LuminanceG * g + LuminanceB * b;

        /// <summary><c>ctl-fx-plume-hue</c>'s rotation of a rendered flame pixel,
        /// (r, g, b) -> (b, r, g): max-channel coverage is unchanged exactly.</summary>
        internal static void RotateHueControl(ref byte r, ref byte g, ref byte b)
        {
            byte r0 = r, g0 = g;
            r = b;
            g = r0;
            b = g0;
        }

        /// <summary>A plate case's floor (E.2): the real side lit at least
        /// <see cref="PlateLitMinPct"/> of the plate.</summary>
        internal static bool PlateLit(double litFraction)
            => !double.IsNaN(litFraction) && litFraction >= PlateLitMinPct;

        /// <summary>The plate's distance along the light's forward: 3 m for a spot, 1 m for a point light.</summary>
        internal static double PlateDistanceM(string lightType)
            => string.Equals(lightType, LightTypePoint, StringComparison.Ordinal) ? PlatePointDistanceM : PlateSpotDistanceM;

        /// <summary>A judged reason's name: <c>&lt;route&gt;:&lt;view&gt;:&lt;layer&gt;:&lt;reason&gt;</c>.</summary>
        internal static string FormatReason(string route, string view, string layer, string reason)
            => (route ?? string.Empty) + ":" + (view ?? string.Empty) + ":" + (layer ?? string.Empty) + ":" + (reason ?? string.Empty);

        // =====================================================================================
        // waves (E.8)
        // =====================================================================================

        /// <summary>
        /// The worst-case wall time of one FX wave, the bound a wave is admitted against: the
        /// spawn cap, the slowest case's press timeout, then for a stochastic wave the plateau
        /// cap and <see cref="FxCaptureSecondsPerCase"/> per case, for a heat wave the minimum
        /// settle and P6's capture, for a plate wave the light settle cap and the plate capture;
        /// then the teardown.
        /// </summary>
        internal static double WaveWorstCaseSeconds(IList<PartParityFxOption> wave)
        {
            int n = wave?.Count ?? 0;
            double press = 0.0;
            bool stochastic = false, plate = false;
            if (wave != null)
                foreach (PartParityFxOption o in wave)
                {
                    press = Math.Max(press, PressTimeoutSeconds(o));
                    PartParityFxBucket b = BucketFor(o);
                    if (b == PartParityFxBucket.Burn || b == PartParityFxBucket.Spool || b == PartParityFxBucket.Rcs)
                        stochastic = true;
                    if (b == PartParityFxBucket.Plate) plate = true;
                }
            double tail;
            if (stochastic) tail = PlateauCapSeconds + FxCaptureSecondsPerCase * n;
            else if (plate) tail = PartParityStateRules.LightSettleCapSeconds + PlateCaptureSecondsPerCase * n;
            else tail = PartParitySweepPlan.MinSettleSeconds + PartParitySweepPlan.StateCaptureSecondsPerCase * n;
            return PartParitySweepPlan.SpawnTimeoutSeconds + press + tail + PartParitySweepPlan.StateTeardownSeconds;
        }

        // =====================================================================================
        // controls (E.5)
        // =====================================================================================

        /// <summary>The plume controls' preference (stock-minimal and modded-restock: the LV-T45;
        /// modded-compat, where the liquids carry Waterfall: the Flea).</summary>
        internal static readonly string[] PlumeControlParts = { "liquidEngine2.v2", "liquidEngine", "solidBooster.sm.v2" };

        /// <summary>The smoke control's and every air control's subject.</summary>
        internal static readonly string[] SmokeControlParts = { "solidBooster.sm.v2" };

        /// <summary>The heat control's preference. Stock's two nose cones name a clip
        /// (<c>Cone_Heat</c>) their models do not carry, so the real part never glows there
        /// (F3 flight); a candidate with no clip is skipped.</summary>
        internal static readonly string[] HeatControlParts =
            { "standardNoseCone", "pointyNoseConeA", "noseConeAdapter", "avionicsNoseCone" };

        /// <summary>The spot-light plate control's preference: the lamps first, then the parts
        /// whose light is a spot too (E0 U9: every light part checked is a spot light).</summary>
        internal static readonly string[] SpotPlateControlParts =
        {
            "spotLight1", "spotLight2", "spotLight3", "spotLight1.v2", "spotLight2.v2", "groundLight1",
            "groundLight2", "navLight1", "domeLight1", "stripLight1",
        };

        /// <summary>A control's preference list, or null for the point-light control (any part
        /// of the install whose prefab light is a point light, in the stable part order).</summary>
        internal static string[] ControlPreference(string controlId, bool airRun)
        {
            if (airRun) return SmokeControlParts;
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlFxSmokeDropped: return SmokeControlParts;
                case TestCommandPartParitySweep.ControlHeatEventsDropped: return HeatControlParts;
                case TestCommandPartParitySweep.ControlPlateLightOff: return SpotPlateControlParts;
                case TestCommandPartParitySweep.ControlPlateLightOffPoint: return null;
                default: return PlumeControlParts;
            }
        }

        /// <summary>The light type a control's subject must carry, or null for none.</summary>
        internal static string ControlLightType(string controlId)
        {
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlPlateLightOff: return LightTypeSpot;
                case TestCommandPartParitySweep.ControlPlateLightOffPoint: return LightTypePoint;
                default: return null;
            }
        }

        /// <summary>
        /// The subject of one FX control on this install (E.5): the first part of the
        /// control's preference (for the point-light control, of <paramref name="installParts"/>
        /// in ordinal order) that is present, carries no <c>ModuleWaterfallFX</c> and, for a
        /// plate control, has a prefab <c>Light</c> of the wanted type. None eligible: <c>bad</c>
        /// (INVALID(parity-controls)), or <c>n/a</c> for an optional control. Every candidate's
        /// verdict is in the detail, so the choice is logged in full.
        /// </summary>
        internal static PartParityFxSubjectChoice ChooseControlSubject(string controlId, bool airRun,
            IEnumerable<string> installParts, Func<string, PartParityFxSubjectFacts> facts)
        {
            var choice = new PartParityFxSubjectChoice { ControlId = controlId };
            string[] pref = ControlPreference(controlId, airRun);
            var candidates = new List<string>();
            if (pref != null) candidates.AddRange(pref);
            else if (installParts != null)
            {
                foreach (string p in installParts) if (p != null) candidates.Add(p);
                candidates.Sort(string.CompareOrdinal);
            }
            string light = airRun ? null : ControlLightType(controlId);
            var notes = new List<string>();
            foreach (string part in candidates)
            {
                string why = SubjectRefusal(facts?.Invoke(part), light, controlId == TestCommandPartParitySweep.ControlHeatEventsDropped && !airRun);
                if (why == null)
                {
                    choice.Subject = part;
                    break;
                }
                if (pref != null) notes.Add(part + ":" + why);
            }
            if (choice.Subject == null)
            {
                bool optional = Array.IndexOf(TestCommandPartParitySweep.OptionalControlIds, controlId) >= 0;
                choice.Outcome = optional ? TestCommandPartParitySweep.ControlsNotApplicable : TestCommandPartParitySweep.ControlsBad;
            }
            choice.Detail = "subject=" + (choice.Subject ?? "none")
                            + (notes.Count > 0 ? " skipped=" + string.Join(",", notes.ToArray()) : string.Empty)
                            + (pref == null ? " candidates=" + candidates.Count.ToString(Ic) : string.Empty);
            return choice;
        }

        private static string SubjectRefusal(PartParityFxSubjectFacts f, string lightType, bool needsHeatClip)
        {
            if (f == null || !f.Present) return "absent";
            if (f.HasWaterfall) return "waterfall";
            if (lightType != null && (f.LightTypes == null || !f.LightTypes.Contains(lightType))) return "no-" + lightType.ToLowerInvariant() + "-light";
            if (needsHeatClip && !f.HasHeatClip) return "no-heat-clip";
            return null;
        }

        /// <summary>A control's subject in words.</summary>
        internal static string ControlSubjectWords(string controlId, bool airRun)
        {
            string plume = string.Join(" / ", PlumeControlParts) + " (first with no ModuleWaterfallFX)";
            if (airRun && (controlId == TestCommandPartParitySweep.ControlEmpty || controlId == TestCommandPartParitySweep.ControlGhostAbsent))
                return SmokeControlParts[0] + " held burning in the air, " + (controlId == TestCommandPartParitySweep.ControlEmpty ? "both sides hidden" : "ghost not built");
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlEmpty: return plume + " inside a burning wave, both sides hidden";
                case TestCommandPartParitySweep.ControlGhostAbsent: return plume + " burning, ghost not built";
                case TestCommandPartParitySweep.ControlFxRealVsReal: return plume + " burning, real against itself in alternate samples";
                case TestCommandPartParitySweep.ControlFxPlumeOff: return plume + ", event-route ghost built without the polled events";
                case TestCommandPartParitySweep.ControlFxPlumeShort: return plume + ", intact ghost vs a ghost whose FX roots are scaled to 0.4 (Hierarchy scaling)";
                case TestCommandPartParitySweep.ControlFxPlumeHue: return plume + ", intact ghost vs a ghost whose rendered flame channels are rotated (r,g,b)->(b,r,g)";
                case TestCommandPartParitySweep.ControlFxPlumeShift: return plume + ", intact ghost vs a ghost whose FX roots are moved 10 % across and along the axis";
                case TestCommandPartParitySweep.ControlFxSmokeDropped: return SmokeControlParts[0] + ", intact ghost vs a ghost with its smoke renderers hidden";
                case TestCommandPartParitySweep.ControlHeatEventsDropped: return string.Join(" / ", HeatControlParts) + " at heat-hot, event-route ghost built without the polled event";
                case TestCommandPartParitySweep.ControlPlateLightOff: return "a spot light (" + SpotPlateControlParts[0] + ", ...), the ghost's Lights disabled";
                case TestCommandPartParitySweep.ControlPlateLightOffPoint: return "a part whose prefab Light is a point light, the ghost's Lights disabled";
                case TestCommandPartParitySweep.ControlFxRealVsRealAir: return SmokeControlParts[0] + " held burning in the air, real against itself in alternate samples";
                case TestCommandPartParitySweep.ControlFxPlumeOffAir: return SmokeControlParts[0] + " held burning in the air, event-route ghost built without the polled events";
                default: return null;
            }
        }

        /// <summary>A control's required outcome in words.</summary>
        internal static string ControlRequiredWords(string controlId)
        {
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlEmpty: return "zero coverage on every layer";
                case TestCommandPartParitySweep.ControlGhostAbsent: return "ghost layers zero, real covered";
                case TestCommandPartParitySweep.ControlFxRealVsReal:
                case TestCommandPartParitySweep.ControlFxRealVsRealAir: return "passes every FX measure (the noise floor)";
                case TestCommandPartParitySweep.ControlFxPlumeOff:
                case TestCommandPartParitySweep.ControlFxPlumeOffAir: return "fails presence, real core present, EngineIgnited recorded";
                case TestCommandPartParitySweep.ControlFxPlumeShort: return "fails length, passes presence and colour";
                case TestCommandPartParitySweep.ControlFxPlumeHue: return "fails colour, passes presence and length";
                case TestCommandPartParitySweep.ControlFxPlumeShift: return "fails origin and lateral offset, passes length and colour";
                case TestCommandPartParitySweep.ControlFxSmokeDropped: return "fails smoke, passes presence";
                case TestCommandPartParitySweep.ControlHeatEventsDropped: return "the press reached heat-hot, an event recorded, the event-route views fail on colour";
                case TestCommandPartParitySweep.ControlPlateLightOff:
                case TestCommandPartParitySweep.ControlPlateLightOffPoint: return "fails on the plate, the real side passes the plate floor";
                default: return null;
            }
        }

        /// <summary>
        /// Judges one stochastic or plate FX control (E.5) from its evidence: <c>ok</c> when the
        /// required outcome holds, else <c>bad</c> with the reason in <paramref name="detail"/>.
        /// A setup fault is <c>bad</c>. <c>ctl-heat-events-dropped</c> is judged by
        /// <see cref="JudgeHeatEventsDroppedControl"/> on its P6 views.
        /// </summary>
        internal static string JudgeFxControl(string controlId, PartParityFxControlEvidence e, out string detail)
        {
            detail = null;
            if (e == null) { detail = "no evidence"; return TestCommandPartParitySweep.ControlsBad; }
            if (TestCommandPartParitySweep.IsSetupFault(e.SetupStatus))
            {
                detail = "setup " + (e.SetupStatus ?? "<none>");
                return TestCommandPartParitySweep.ControlsBad;
            }
            switch (controlId)
            {
                case TestCommandPartParitySweep.ControlEmpty:
                    return Require(e.AllCoverageZero, "a layer is covered", out detail);
                case TestCommandPartParitySweep.ControlGhostAbsent:
                    return Require(e.GhostCoverageZero && e.RealCoverageNonZero,
                        e.GhostCoverageZero ? "the real side is not covered" : "a ghost layer is covered", out detail);
                case TestCommandPartParitySweep.ControlFxRealVsReal:
                case TestCommandPartParitySweep.ControlFxRealVsRealAir:
                    return Require(e.FailingMeasures.Count == 0, "fails " + Names(e.FailingMeasures), out detail);
                case TestCommandPartParitySweep.ControlFxPlumeOff:
                case TestCommandPartParitySweep.ControlFxPlumeOffAir:
                    if (!e.RealCorePresent) return Require(false, "no real core", out detail);
                    if (e.EventsRecorded < 1) return Require(false, "no EngineIgnited recorded", out detail);
                    return RequireMeasures(e, new[] { MeasurePresence }, null, out detail);
                case TestCommandPartParitySweep.ControlFxPlumeShort:
                    return RequireMeasures(e, new[] { MeasureLength }, new[] { MeasurePresence, MeasureColour }, out detail);
                case TestCommandPartParitySweep.ControlFxPlumeHue:
                    return RequireMeasures(e, new[] { MeasureColour }, new[] { MeasurePresence, MeasureLength }, out detail);
                case TestCommandPartParitySweep.ControlFxPlumeShift:
                    return RequireMeasures(e, new[] { MeasureOrigin, MeasureLateral }, new[] { MeasureLength, MeasureColour }, out detail);
                case TestCommandPartParitySweep.ControlFxSmokeDropped:
                    return RequireMeasures(e, new[] { MeasureSmoke }, new[] { MeasurePresence }, out detail);
                case TestCommandPartParitySweep.ControlPlateLightOff:
                case TestCommandPartParitySweep.ControlPlateLightOffPoint:
                    if (!e.RealPlateLit) return Require(false, "the real side is under the plate floor", out detail);
                    return Require(e.PlateViewsFail, "the plate views pass", out detail);
                default:
                    detail = "not an FX control: " + (controlId ?? "<none>");
                    return TestCommandPartParitySweep.ControlsBad;
            }
        }

        private static string Require(bool ok, string why, out string detail)
        {
            detail = ok ? null : why;
            return ok ? TestCommandPartParitySweep.ControlsOk : TestCommandPartParitySweep.ControlsBad;
        }

        private static string RequireMeasures(PartParityFxControlEvidence e, string[] mustFail, string[] mustPass, out string detail)
        {
            foreach (string m in mustFail)
                if (!e.FailingMeasures.Contains(m)) return Require(false, m + " passes", out detail);
            if (mustPass != null)
                foreach (string m in mustPass)
                    if (e.FailingMeasures.Contains(m)) return Require(false, m + " fails", out detail);
            detail = null;
            return TestCommandPartParitySweep.ControlsOk;
        }

        private static string Names(HashSet<string> set)
        {
            var list = new List<string>();
            foreach (string m in Measures) if (set.Contains(m)) list.Add(m);
            return string.Join(",", list.ToArray());
        }

        /// <summary>
        /// Judges <c>ctl-heat-events-dropped</c> (E.5): P6's state control at <c>heat-hot</c>
        /// with a colour requirement instead of shape: the press reached the level, an event was
        /// recorded, and at least one event-route view fails on colour.
        /// </summary>
        internal static string JudgeHeatEventsDroppedControl(bool reached, int eventsRecorded,
            IList<PartParityViewRecord> views, string setupStatus, out string detail)
        {
            detail = null;
            if (TestCommandPartParitySweep.IsSetupFault(setupStatus))
            {
                detail = "setup " + (setupStatus ?? "<none>");
                return TestCommandPartParitySweep.ControlsBad;
            }
            if (!reached) return Require(false, "the press never reached heat-hot", out detail);
            if (eventsRecorded < 1) return Require(false, "no event recorded", out detail);
            if (views == null || views.Count == 0) return Require(false, "no views", out detail);
            foreach (PartParityViewRecord v in views)
                if (v != null && PartParitySweepPlan.ColorFails(v.Measures)) return TestCommandPartParitySweep.ControlsOk;
            return Require(false, "the event-route views pass on colour", out detail);
        }
    }
}
