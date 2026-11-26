using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>The <c>op=</c> of a sweep step.</summary>
    internal enum PartParitySweepOp
    {
        /// <summary>Clear the label directory (the lane's first step).</summary>
        Reset,

        /// <summary>Process cases in the stable order until the time box ends (resumes).</summary>
        Run,
    }

    /// <summary>The <c>skins=</c> selection.</summary>
    internal enum PartParitySkinSelection
    {
        /// <summary>The base skin of each part only.</summary>
        Default,

        /// <summary>Every skin a part offers.</summary>
        All,
    }

    /// <summary>What the resume rule decides for one case at the start of its turn (design 5.3).</summary>
    internal enum PartParityCaseResume
    {
        /// <summary>No complete sidecar and no crash record: capture it.</summary>
        Run,

        /// <summary>A complete sidecar exists: skip it.</summary>
        SkipComplete,

        /// <summary>No complete sidecar and it is owed a solo run (<c>isolate.txt</c>): a
        /// previous process died while it shared a wave with other cases, so it runs in a
        /// wave of its own and a second crash names it alone.</summary>
        RunSolo,
    }

    /// <summary>A parsed <c>inflight.txt</c> (design 5.3): the case ids it names, in order,
    /// and whether the wave that wrote it was a solo rerun.</summary>
    internal sealed class PartParityInflight
    {
        internal readonly List<string> CaseIds = new List<string>();
        internal bool Solo;
    }

    /// <summary>What a stale <c>inflight.txt</c> decides at step start (design 5.3).</summary>
    internal sealed class PartParityInflightDecision
    {
        /// <summary>The one case recorded <c>crashed</c>; null when none is.</summary>
        internal string CrashedCaseId;

        /// <summary>The cases owed a solo run, in the file's order.</summary>
        internal readonly List<string> Isolate = new List<string>();

        /// <summary>Named cases that already have a complete sidecar: nothing to do.</summary>
        internal readonly List<string> Finished = new List<string>();
    }

    /// <summary>The parsed args of one <c>PartParitySweep</c> step.</summary>
    internal sealed class PartParitySweepArgs
    {
        internal string Label;
        internal PartParitySweepOp Op = PartParitySweepOp.Run;
        internal string Filter = TestCommandPartParitySweep.DefaultFilter;

        /// <summary>The <c>source=</c> csv, or null for every source.</summary>
        internal List<string> Sources;

        internal PartParitySkinSelection Skins = PartParitySkinSelection.Default;

        /// <summary>The <c>states=</c> words in canonical order (<c>all</c> resolves to every
        /// <see cref="PartParityStateRules.KnownStates"/> word), or null for <c>none</c>.</summary>
        internal List<string> States;

        /// <summary>True when <c>states=all</c> (the manifest records the token, not the list).</summary>
        internal bool StatesAll;

        /// <summary>The <c>contexts=</c> names in canonical order, or null for <c>none</c>.</summary>
        internal List<string> Contexts;

        /// <summary>The <c>fx=</c> words in canonical order (<c>all</c> resolves to every
        /// <see cref="PartParityFxRules.ArgWords"/> word), or null for <c>none</c> (design E.5).</summary>
        internal List<string> Fx;

        /// <summary>True when <c>fx=all</c> (the manifest records the token, not the list).</summary>
        internal bool FxAll;

        /// <summary>The views, in canonical order, deduplicated.</summary>
        internal List<string> Views = new List<string>(TestCommandPartParitySweep.RequiredViews);

        internal int Tile = TestCommandPartParitySweep.DefaultTile;
        internal int ShardK = 1;
        internal int ShardN = 1;

        /// <summary>The <c>limit=</c>, or 0 for no limit.</summary>
        internal int Limit;

        internal bool Controls = true;
        internal int MaxSeconds = TestCommandPartParitySweep.DefaultMaxSeconds;
    }

    /// <summary>The identity of one case before it has an id: part, skin, state, context
    /// (design section 3), plus what ordering and filtering read.</summary>
    internal sealed class PartParityCaseKey
    {
        internal string Part;

        /// <summary>The <c>source</c> attribution (first model url segment).</summary>
        internal string Source;

        internal string Skin = TestCommandPartParitySweep.DefaultSkinName;

        /// <summary>The skin's position among the part's skins, 0 for the base skin.</summary>
        internal int SkinOrdinal;

        /// <summary>What the skin case authors (design 3.2); null for the base skin. Not part
        /// of the identity: the case id and order read <see cref="Skin"/> and <see cref="SkinOrdinal"/>.</summary>
        internal PartParitySkinOption SkinOption;

        internal string State = TestCommandPartParitySweep.DefaultState;
        internal string Context = TestCommandPartParitySweep.DefaultContext;

        /// <summary>What a state or context case presses and how (design D.2); null for a
        /// stowed case. Not part of the identity: the case id reads <see cref="State"/> and
        /// <see cref="Context"/>.</summary>
        internal PartParityStateOption StateOption;

        /// <summary>What an FX case drives (design E.1); null for every other case. Not part of
        /// the identity, like <see cref="StateOption"/>.</summary>
        internal PartParityFxOption FxOption;
    }

    /// <summary>One case a step will consider, with its stable index and id.</summary>
    internal sealed class PartParityPlannedCase
    {
        /// <summary>The case's index in the stable order of the selected cases (the index the
        /// shard rule reads).</summary>
        internal int Index;

        internal PartParityCaseKey Key;
        internal string CaseId;
    }

    /// <summary>
    /// Pure half of the automation-only <c>PartParitySweep</c> seam verb
    /// (design-part-ghost-parity.md section 7): the arg grammar with its defaults and
    /// refusals, the stable case order, sharding, case ids, the resume decision, the time-box
    /// decision and the OK payload. The capture itself, the files and the wave coroutine
    /// belong to the applier.
    ///
    /// <para>Grammar: <c>PartParitySweep label=&lt;label&gt; [op=reset|run]
    /// [filter=&lt;glob&gt;] [source=&lt;csv&gt;] [skins=default|all]
    /// [states=none|all|&lt;csv&gt;] [contexts=none|&lt;csv&gt;] [fx=none|all|&lt;csv&gt;] [views=required|all|&lt;csv&gt;] [tile=256..1024] [shard=&lt;k&gt;/&lt;n&gt;]
    /// [limit=&lt;n&gt;] [controls=true|false] [maxSeconds=60..480]</c>. Every present arg
    /// that does not parse is REJECTED with its own reason, never clamped or defaulted: a
    /// silently widened sweep would run past its step budget, a silently narrowed one would
    /// read as complete.</para>
    /// </summary>
    internal static class TestCommandPartParitySweep
    {
        internal const string VerbName = "PartParitySweep";

        // ----- files -----

        /// <summary>The sweep root under the KSP root; each label is a directory below it.</summary>
        internal const string RootDirName = "parsek-partparity";
        internal const string ManifestFileName = "manifest.json";
        internal const string InflightFileName = "inflight.txt";

        /// <summary>The cases still owed a solo run, one id per line (design 5.3).</summary>
        internal const string IsolateFileName = "isolate.txt";
        internal const string SidecarSuffix = ".part.json";
        internal const string AtlasSuffix = ".png";
        internal const string ThumbSuffix = ".thumb.png";

        internal static string SidecarFileName(string caseId) => (caseId ?? string.Empty) + SidecarSuffix;
        internal static string AtlasFileName(string caseId) => (caseId ?? string.Empty) + AtlasSuffix;
        internal static string ThumbFileName(string caseId) => (caseId ?? string.Empty) + ThumbSuffix;

        /// <summary>An FX case's accumulated FX atlas and its strip (design E.5).</summary>
        internal const string FxAtlasSuffix = ".fx.png";
        internal const string StripSuffix = ".strip.png";

        internal static string FxAtlasFileName(string caseId) => (caseId ?? string.Empty) + FxAtlasSuffix;
        internal static string StripFileName(string caseId) => (caseId ?? string.Empty) + StripSuffix;

        /// <summary>A burst case's time-binned atlas and a dust case's atlas (design Appendix G).</summary>
        internal const string BurstAtlasSuffix = ".burst.png";
        internal const string DustAtlasSuffix = ".dust.png";

        internal static string BurstAtlasFileName(string caseId) => (caseId ?? string.Empty) + BurstAtlasSuffix;
        internal static string DustAtlasFileName(string caseId) => (caseId ?? string.Empty) + DustAtlasSuffix;

        /// <summary>The label directory relative to the KSP root, forward slashes.</summary>
        internal static string RelativeLabelDir(string label) => RootDirName + "/" + (label ?? string.Empty);

        // ----- views -----

        internal const string ViewSideA = "side-a";
        internal const string ViewSideB = "side-b";
        internal const string ViewTop = "top";
        internal const string ViewBottom = "bottom";
        internal const string ViewSideC = "side-c";
        internal const string ViewSideD = "side-d";
        internal const string ViewHero = "hero";

        internal const string ViewsRequiredToken = "required";
        internal const string ViewsAllToken = "all";

        /// <summary>The four orthographic views every sweep captures.</summary>
        internal static readonly string[] RequiredViews = { ViewSideA, ViewSideB, ViewTop, ViewBottom };

        /// <summary><c>views=all</c>: the required views plus the two remaining side faces.
        /// The perspective <c>hero</c> view is opt-in by name only.</summary>
        internal static readonly string[] AllViews =
            { ViewSideA, ViewSideB, ViewTop, ViewBottom, ViewSideC, ViewSideD };

        /// <summary>Every view name, in canonical order (a csv is re-ordered to this).</summary>
        internal static readonly string[] KnownViews =
            { ViewSideA, ViewSideB, ViewTop, ViewBottom, ViewSideC, ViewSideD, ViewHero };

        // ----- defaults and ranges -----

        internal const string DefaultFilter = "*";
        internal const int DefaultTile = 512;
        internal const int MinTile = 256;
        internal const int MaxTile = 1024;
        internal const int DefaultMaxSeconds = 480;
        internal const int MinMaxSeconds = 60;
        internal const int MaxMaxSeconds = 480;
        internal const int MaxShards = 64;
        internal const int MaxLimit = 100000;
        internal const int MaxFilterLength = 128;

        internal const string DefaultSkinName = "default";
        internal const string DefaultState = "stowed";
        internal const string DefaultContext = "alone";

        // ----- case ids -----

        internal const int MaxCaseIdLength = TestCommandCaptureScreenshot.MaxLabelLength;
        internal const string CaseIdSeparator = "--";
        internal const int CaseIdHashLength = 8;

        // ----- setup statuses (sidecar setup.status) -----

        internal const string SetupOk = "ok";
        internal const string SetupSkinNotApplied = "skin-not-applied";
        /// <summary>A real part's depth-mask module needed the repair of design 4.2 step 0 and did
        /// not get it (<c>PartParityDepthMaskRepair</c>): its real picture is not the part a
        /// player sees, so the case is a setup fault, not a ghost finding.</summary>
        internal const string SetupDepthMaskUnrepaired = "depth-mask-unrepaired";
        internal const string SetupSpawnTimeout = "spawn-timeout";
        internal const string SetupStillAnimating = "still-animating";
        internal const string SetupDiedSettling = "died-settling";
        internal const string SetupCrashed = "crashed";

        /// <summary>A state case's stock action was missing, threw, or left the module
        /// unchanged (design D.3; <c>stateProof.actionResult</c> says which).</summary>
        internal const string SetupActionFailed = "action-failed";

        /// <summary>A state case's done signal did not arrive within its timeout (design D.3).</summary>
        internal const string SetupStateTimeout = "state-timeout";

        /// <summary>A plate case whose REAL side lit less than <see cref="PartParityFxRules.PlateLitMinPct"/>
        /// of the plate (design E.2): unmeasurable, never a pass.</summary>
        internal const string SetupPlateNotLit = "plate-not-lit";

        /// <summary>An rcs case whose fired nozzles differ from <see cref="PartParityFxRules.ChooseRcsInput"/>'s
        /// prediction (design E.2, E0 U2).</summary>
        internal const string SetupRcsInputMismatch = "rcs-input-mismatch";

        /// <summary>A burst case whose REAL side drew nothing it was meant to: no blast tagged, no
        /// gas burst (design G.4). Unmeasurable, never a pass.</summary>
        internal const string SetupBurstNotTriggered = "burst-not-triggered";

        /// <summary>A dust case whose stock raycast hit no surface in any sample (design G.4): its
        /// slot height is under the part's reach by construction, so this is a setup fault.</summary>
        internal const string SetupDustNoStockHit = "dust-no-stock-hit";

        internal static readonly string[] SetupStatuses =
        {
            SetupOk, SetupSkinNotApplied, SetupSpawnTimeout, SetupStillAnimating,
            SetupDiedSettling, SetupCrashed, SetupDepthMaskUnrepaired, SetupActionFailed, SetupStateTimeout,
            SetupPlateNotLit, SetupRcsInputMismatch, SetupBurstNotTriggered, SetupDustNoStockHit,
        };

        /// <summary>True for a status that is a setup fault: anything but <c>ok</c> and
        /// <c>still-animating</c>. A still-animating case is captured and measured anyway
        /// (design 4.1), so it is a tag on a real measurement, not a missing one.</summary>
        internal static bool IsSetupFault(string status)
            => !string.Equals(status, SetupOk, StringComparison.Ordinal)
               && !string.Equals(status, SetupStillAnimating, StringComparison.Ordinal);

        // ----- controls (design 4.5) -----

        internal const string ControlEmpty = "ctl-empty";
        internal const string ControlGhostAbsent = "ctl-ghost-absent";
        internal const string ControlRealVsReal = "ctl-real-vs-real";
        internal const string ControlGhostScale90 = "ctl-ghost-scale90";
        internal const string ControlGhostRot90 = "ctl-ghost-rot90";
        internal const string ControlGhostFlip = "ctl-ghost-flip";
        internal const string ControlGhostMissingMesh = "ctl-ghost-missing-mesh";
        internal const string ControlWrongSkin = "ctl-wrong-skin";

        /// <summary>The control cases of a stowed run (no <c>states=</c> / <c>contexts=</c>),
        /// in the order they run (before every other case).</summary>
        internal static readonly string[] ControlIds =
        {
            ControlEmpty, ControlGhostAbsent, ControlRealVsReal, ControlGhostScale90,
            ControlGhostRot90, ControlGhostFlip, ControlGhostMissingMesh, ControlWrongSkin,
        };

        /// <summary>The orbit state control (design D.6): the full state case of
        /// <c>longAntenna</c> with the event-route ghost built WITHOUT the polled events.</summary>
        internal const string ControlStateEventsDropped = "ctl-state-events-dropped";

        /// <summary>The same on <c>parachuteSingle</c> at <c>semi</c> in the air.</summary>
        internal const string ControlStateEventsDroppedAir = "ctl-state-events-dropped-air";

        /// <summary>An orbit state run's controls: the stowed eight, then the state control.</summary>
        internal static readonly string[] OrbitStateControlIds =
        {
            ControlEmpty, ControlGhostAbsent, ControlRealVsReal, ControlGhostScale90,
            ControlGhostRot90, ControlGhostFlip, ControlGhostMissingMesh, ControlWrongSkin,
            ControlStateEventsDropped,
        };

        /// <summary>An air run's controls (design D.2): the two coverage controls, the
        /// real-vs-real control on a falling armed chute, and the air state control.</summary>
        internal static readonly string[] AirControlIds =
        {
            ControlEmpty, ControlGhostAbsent, ControlRealVsReal, ControlStateEventsDroppedAir,
        };

        // The FX controls (design E.5); their subjects are PartParityFxRules.ChooseControlSubject's.
        internal const string ControlFxRealVsReal = "ctl-fx-real-vs-real";
        internal const string ControlFxPlumeOff = "ctl-fx-plume-off";
        internal const string ControlFxPlumeShort = "ctl-fx-plume-short";
        internal const string ControlFxPlumeHue = "ctl-fx-plume-hue";
        internal const string ControlFxPlumeShift = "ctl-fx-plume-shift";
        internal const string ControlFxSmokeDropped = "ctl-fx-smoke-dropped";
        internal const string ControlHeatEventsDropped = "ctl-heat-events-dropped";
        internal const string ControlPlateLightOff = "ctl-plate-light-off";
        internal const string ControlPlateLightOffPoint = "ctl-plate-light-off-point";
        internal const string ControlFxRealVsRealAir = "ctl-fx-real-vs-real-air";
        internal const string ControlFxPlumeOffAir = "ctl-fx-plume-off-air";

        /// <summary>An orbit FX run's controls (<c>fx=</c> without <c>contexts=air</c>).</summary>
        internal static readonly string[] FxOrbitControlIds =
        {
            ControlEmpty, ControlGhostAbsent, ControlFxRealVsReal, ControlFxPlumeOff, ControlFxPlumeShort,
            ControlFxPlumeHue, ControlFxPlumeShift, ControlFxSmokeDropped, ControlHeatEventsDropped,
            ControlPlateLightOff, ControlPlateLightOffPoint,
        };

        /// <summary>An air FX run's controls: the solid booster held burning in the air.</summary>
        internal static readonly string[] FxAirControlIds =
        {
            ControlEmpty, ControlGhostAbsent, ControlFxRealVsRealAir, ControlFxPlumeOffAir,
        };

        // The burst and dust controls (design G.4); their subjects are PartParityFxRules.BuiltControlPreference's.
        internal const string ControlBurstEmpty = "ctl-burst-empty";
        internal const string ControlBurstRealVsReal = "ctl-burst-real-vs-real";
        internal const string ControlBurstOff = "ctl-burst-off";
        internal const string ControlBurstLate = "ctl-burst-late";
        internal const string ControlBurstPower = "ctl-burst-power";
        internal const string ControlBurstShift = "ctl-burst-shift";
        internal const string ControlBurstHue = "ctl-burst-hue";
        internal const string ControlDustRealVsReal = "ctl-dust-real-vs-real";
        internal const string ControlDustOff = "ctl-dust-off";
        internal const string ControlDustAboveReach = "ctl-dust-above-reach";

        /// <summary>A burst run's controls (<c>fx=</c> of burst words, <c>contexts=air</c>).</summary>
        internal static readonly string[] BurstControlIds =
        {
            ControlBurstEmpty, ControlBurstRealVsReal, ControlBurstOff, ControlBurstLate, ControlBurstPower,
            ControlBurstShift, ControlBurstHue,
        };

        /// <summary>A dust run's controls (<c>fx=dust</c>, <c>contexts=ground</c>).</summary>
        internal static readonly string[] DustControlIds =
        {
            ControlDustRealVsReal, ControlDustOff, ControlDustAboveReach,
        };

        /// <summary>The controls an install may lack a subject for: recorded <c>n/a</c>, never
        /// <c>bad</c> (E0 U9: no part on either install carries a point light).</summary>
        internal static readonly string[] OptionalControlIds = { ControlPlateLightOffPoint };

        /// <summary>The controls that record <c>n/a</c> while a known ghost difference stands
        /// (design G.4): <c>ctl-dust-above-reach</c> until fix Y3 (G.8 item 3,
        /// <see cref="PartParityFxRules.DustAboveReachGhostJudged"/>).</summary>
        internal static readonly string[] KnownDifferenceControlIds = { ControlDustAboveReach };

        /// <summary>Every control id any run may plan.</summary>
        internal static readonly string[] AllControlIds =
        {
            ControlEmpty, ControlGhostAbsent, ControlRealVsReal, ControlGhostScale90,
            ControlGhostRot90, ControlGhostFlip, ControlGhostMissingMesh, ControlWrongSkin,
            ControlStateEventsDropped, ControlStateEventsDroppedAir,
            ControlFxRealVsReal, ControlFxPlumeOff, ControlFxPlumeShort, ControlFxPlumeHue,
            ControlFxPlumeShift, ControlFxSmokeDropped, ControlHeatEventsDropped, ControlPlateLightOff,
            ControlPlateLightOffPoint, ControlFxRealVsRealAir, ControlFxPlumeOffAir,
            ControlBurstEmpty, ControlBurstRealVsReal, ControlBurstOff, ControlBurstLate, ControlBurstPower,
            ControlBurstShift, ControlBurstHue, ControlDustRealVsReal, ControlDustOff, ControlDustAboveReach,
        };

        internal const string ControlsOk = "ok";
        internal const string ControlsBad = "bad";
        internal const string ControlsPending = "pending";

        /// <summary><c>controls=false</c>: the step ran no controls (a dev run only; a lane
        /// whose controls never ran cannot pass).</summary>
        internal const string ControlsOff = "off";

        /// <summary>An optional control (<see cref="OptionalControlIds"/>) with no subject on the
        /// install: it counts as met.</summary>
        internal const string ControlsNotApplicable = "n/a";

        // ----- refusals -----

        internal const string LabelArgMissingReason = TestCommandCaptureScreenshot.LabelArgMissingReason;
        internal const string LabelArgInvalidReason = TestCommandCaptureScreenshot.LabelArgInvalidReason;
        internal const string OpArgInvalidReason = "op-arg-invalid";
        internal const string FilterArgInvalidReason = "filter-arg-invalid";
        internal const string SourceArgInvalidReason = "source-arg-invalid";
        internal const string SkinsArgInvalidReason = "skins-arg-invalid";
        internal const string StatesArgInvalidReason = "states-arg-invalid";
        internal const string ContextsArgInvalidReason = "contexts-arg-invalid";
        internal const string FxArgInvalidReason = "fx-arg-invalid";
        internal const string ViewsArgInvalidReason = "views-arg-invalid";
        internal const string TileArgInvalidReason = "tile-arg-invalid";
        internal const string ShardArgInvalidReason = "shard-arg-invalid";
        internal const string LimitArgInvalidReason = "limit-arg-invalid";
        internal const string ControlsArgInvalidReason = "controls-arg-invalid";
        internal const string MaxSecondsArgInvalidReason = "maxseconds-arg-invalid";

        /// <summary>REJECTED: a well-formed burst or dust run (design G.7) while the live burst
        /// and dust driver (piece X4) is not built; the grammar alone is <see cref="TryParseGrammar"/>.</summary>
        internal const string BurstDustRunsNotBuiltReason = "burst-dust-runs-not-built";

        /// <summary>False until the live burst and dust driver (design G.7 piece X4) lands.</summary>
        internal const bool BurstDustRunsBuilt = false;

        /// <summary>REJECTED: FLIGHT with no active vessel.</summary>
        internal const string NoActiveVesselReason = "no-active-vessel";

        /// <summary>REJECTED: the active vessel is not in a stable orbit (landed, splashed,
        /// suborbital, escaping): the co-orbital spawns need an orbiting host.</summary>
        internal const string HostNotOrbitingReason = "host-not-orbiting";

        /// <summary>REJECTED: an air run (<c>contexts=air</c>) whose active vessel is not
        /// PRELAUNCH or LANDED within <see cref="AirHostMaxPadDistanceM"/> of the KSC pad
        /// (design D.4): the chutes spawn over the showcase band beside the pad.</summary>
        internal const string HostNotAtPadReason = "host-not-at-pad";

        /// <summary>How far from the launch pad an air run's host may sit (design D.4).</summary>
        internal const double AirHostMaxPadDistanceM = 500.0;

        /// <summary>The REJECTED vocabulary, in parse-then-admission order.</summary>
        internal static readonly string[] Reasons =
        {
            LabelArgMissingReason, LabelArgInvalidReason, OpArgInvalidReason,
            FilterArgInvalidReason, SourceArgInvalidReason, SkinsArgInvalidReason,
            StatesArgInvalidReason, ContextsArgInvalidReason, FxArgInvalidReason,
            ViewsArgInvalidReason, TileArgInvalidReason, ShardArgInvalidReason,
            LimitArgInvalidReason, ControlsArgInvalidReason, MaxSecondsArgInvalidReason,
            BurstDustRunsNotBuiltReason, NoActiveVesselReason, HostNotOrbitingReason, HostNotAtPadReason,
        };

        /// <summary>Every arg key the verb reads.</summary>
        internal static readonly string[] ArgKeys =
        {
            "label", "op", "filter", "source", "skins", "states", "contexts", "fx", "views", "tile",
            "shard", "limit", "controls", "maxSeconds",
        };

        // ----- states= and contexts= (design D.2) -----

        /// <summary>The default of both <c>states=</c> and <c>contexts=</c>: no state cases.</summary>
        internal const string ArgNoneToken = "none";

        /// <summary><c>states=all</c>: every state word.</summary>
        internal const string StatesAllToken = "all";

        /// <summary>The manifest <c>settings</c> keys a run records its state selection and its
        /// control set under (design D.2).</summary>
        internal const string SettingStates = "states";
        internal const string SettingContexts = "contexts";
        internal const string SettingControlSet = "controlSet";

        /// <summary>The manifest setting an FX run records its <c>fx=</c> under (design E.5);
        /// written only by an FX run, so every other run's settings stay what they were.</summary>
        internal const string SettingFx = "fx";

        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // ----- arg parse -----

        private static string Get(IDictionary<string, string> args, string key)
            => args != null && args.TryGetValue(key, out string v) ? v : null;

        /// <summary>
        /// Parses every arg of one step (<see cref="TryParseGrammar"/>); an air run
        /// (<c>contexts=air</c>) parses like any other and meets its own host rule at the
        /// admission (<c>host-not-at-pad</c>), an <c>fx=</c> one included. A burst or dust run
        /// (design G.7) is refused <c>burst-dust-runs-not-built</c> (detail <c>fx=..</c>) while
        /// <see cref="BurstDustRunsBuilt"/> is false. On a refusal <paramref name="rejectReason"/> is the reason token and
        /// <paramref name="rejectDetail"/> the offending <c>key=value</c>.
        /// </summary>
        internal static bool TryParseArgs(
            IDictionary<string, string> args, out PartParitySweepArgs parsed,
            out string rejectReason, out string rejectDetail)
        {
            if (!TryParseGrammar(args, out parsed, out rejectReason, out rejectDetail)) return false;
            if (!BurstDustRunsBuilt && IsBurstDustRun(parsed))
            {
                rejectReason = BurstDustRunsNotBuiltReason;
                rejectDetail = "fx=" + FormatFx(parsed);
                parsed = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// The arg grammar alone: every arg of one step, each present one that does not parse
        /// refused with its own reason. The pure planning rules read its result directly.
        /// </summary>
        internal static bool TryParseGrammar(
            IDictionary<string, string> args, out PartParitySweepArgs parsed,
            out string rejectReason, out string rejectDetail)
        {
            parsed = null;
            rejectDetail = null;
            var a = new PartParitySweepArgs();

            string raw = Get(args, "label");
            if (!TestCommandCaptureScreenshot.TryParseLabel(raw, out string label, out rejectReason))
                return Reject("label", raw, out rejectDetail);
            a.Label = label;

            raw = Get(args, "op");
            if (raw != null)
            {
                if (raw == "reset") a.Op = PartParitySweepOp.Reset;
                else if (raw == "run") a.Op = PartParitySweepOp.Run;
                else { rejectReason = OpArgInvalidReason; return Reject("op", raw, out rejectDetail); }
            }

            raw = Get(args, "filter");
            if (raw != null)
            {
                if (!IsValidFilter(raw)) { rejectReason = FilterArgInvalidReason; return Reject("filter", raw, out rejectDetail); }
                a.Filter = raw;
            }

            raw = Get(args, "source");
            if (raw != null)
            {
                if (!TryParseSourceCsv(raw, out List<string> sources))
                { rejectReason = SourceArgInvalidReason; return Reject("source", raw, out rejectDetail); }
                a.Sources = sources;
            }

            raw = Get(args, "skins");
            if (raw != null)
            {
                if (raw == "default") a.Skins = PartParitySkinSelection.Default;
                else if (raw == "all") a.Skins = PartParitySkinSelection.All;
                else { rejectReason = SkinsArgInvalidReason; return Reject("skins", raw, out rejectDetail); }
            }

            raw = Get(args, "states");
            if (raw != null)
            {
                if (!TryParseStates(raw, out List<string> states, out bool statesAll))
                { rejectReason = StatesArgInvalidReason; return Reject("states", raw, out rejectDetail); }
                a.States = states;
                a.StatesAll = statesAll;
            }

            raw = Get(args, "contexts");
            if (raw != null)
            {
                if (!TryParseContexts(raw, out List<string> contexts))
                { rejectReason = ContextsArgInvalidReason; return Reject("contexts", raw, out rejectDetail); }
                a.Contexts = contexts;
            }

            raw = Get(args, "fx");
            if (raw != null)
            {
                if (!TryParseFx(raw, out List<string> fx, out bool fxAll)
                    || (fx != null && !FxCombinationValid(a, fx)))
                { rejectReason = FxArgInvalidReason; return Reject("fx", raw, out rejectDetail); }
                a.Fx = fx;
                a.FxAll = fxAll;
            }
            // The ground context is the dust run's alone: without fx= it would plan nothing.
            if (a.Fx == null && IsGroundRun(a))
            { rejectReason = ContextsArgInvalidReason; return Reject("contexts", FormatContexts(a), out rejectDetail); }

            raw = Get(args, "views");
            if (raw != null)
            {
                if (!TryParseViews(raw, out List<string> views))
                { rejectReason = ViewsArgInvalidReason; return Reject("views", raw, out rejectDetail); }
                a.Views = views;
            }

            raw = Get(args, "tile");
            if (raw != null)
            {
                if (!TryParseIntInRange(raw, MinTile, MaxTile, out int tile))
                { rejectReason = TileArgInvalidReason; return Reject("tile", raw, out rejectDetail); }
                a.Tile = tile;
            }

            raw = Get(args, "shard");
            if (raw != null)
            {
                if (!TryParseShard(raw, out int k, out int n))
                { rejectReason = ShardArgInvalidReason; return Reject("shard", raw, out rejectDetail); }
                a.ShardK = k;
                a.ShardN = n;
            }

            raw = Get(args, "limit");
            if (raw != null)
            {
                if (!TryParseIntInRange(raw, 1, MaxLimit, out int limit))
                { rejectReason = LimitArgInvalidReason; return Reject("limit", raw, out rejectDetail); }
                a.Limit = limit;
            }

            raw = Get(args, "controls");
            if (raw != null)
            {
                if (raw == "true") a.Controls = true;
                else if (raw == "false") a.Controls = false;
                else { rejectReason = ControlsArgInvalidReason; return Reject("controls", raw, out rejectDetail); }
            }

            raw = Get(args, "maxSeconds");
            if (raw != null)
            {
                if (!TryParseIntInRange(raw, MinMaxSeconds, MaxMaxSeconds, out int maxSeconds))
                { rejectReason = MaxSecondsArgInvalidReason; return Reject("maxSeconds", raw, out rejectDetail); }
                a.MaxSeconds = maxSeconds;
            }

            parsed = a;
            rejectReason = null;
            return true;
        }

        private static bool Reject(string key, string raw, out string detail)
        {
            detail = key + "=" + (raw ?? string.Empty);
            return false;
        }

        /// <summary>A decimal integer in [min, max]; no sign, no whitespace, no other culture.</summary>
        internal static bool TryParseIntInRange(string raw, int min, int max, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            if (!int.TryParse(raw, NumberStyles.None, Ic, out int v)) return false;
            if (v < min || v > max) return false;
            value = v;
            return true;
        }

        /// <summary><c>k/n</c> with 1 &lt;= n &lt;= <see cref="MaxShards"/> and 1 &lt;= k &lt;= n.</summary>
        internal static bool TryParseShard(string raw, out int k, out int n)
        {
            k = 1;
            n = 1;
            if (string.IsNullOrEmpty(raw)) return false;
            int slash = raw.IndexOf('/');
            if (slash <= 0 || slash != raw.LastIndexOf('/')) return false;
            if (!TryParseIntInRange(raw.Substring(slash + 1), 1, MaxShards, out int nn)) return false;
            if (!TryParseIntInRange(raw.Substring(0, slash), 1, nn, out int kk)) return false;
            k = kk;
            n = nn;
            return true;
        }

        /// <summary>A glob over part names: 1..<see cref="MaxFilterLength"/> chars of
        /// <c>[A-Za-z0-9._-]</c> plus the wildcards <c>*</c> and <c>?</c>.</summary>
        internal static bool IsValidFilter(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length > MaxFilterLength) return false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (!(IsIdChar(c) || c == '*' || c == '?')) return false;
            }
            return true;
        }

        /// <summary>A non-empty csv of source names (<c>[A-Za-z0-9._-]</c>, no empty item);
        /// duplicates are dropped, order kept.</summary>
        internal static bool TryParseSourceCsv(string raw, out List<string> sources)
        {
            sources = null;
            if (string.IsNullOrEmpty(raw)) return false;
            var list = new List<string>();
            foreach (string item in raw.Split(','))
            {
                if (item.Length == 0) return false;
                for (int i = 0; i < item.Length; i++)
                    if (!IsIdChar(item[i])) return false;
                bool dup = false;
                foreach (string s in list)
                    if (string.Equals(s, item, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup) list.Add(item);
            }
            sources = list;
            return true;
        }

        /// <summary><c>required</c>, <c>all</c>, or a csv of <see cref="KnownViews"/> names
        /// (re-ordered to the canonical order, duplicates dropped).</summary>
        internal static bool TryParseViews(string raw, out List<string> views)
        {
            views = null;
            if (raw == ViewsRequiredToken) { views = new List<string>(RequiredViews); return true; }
            if (raw == ViewsAllToken) { views = new List<string>(AllViews); return true; }
            if (string.IsNullOrEmpty(raw)) return false;
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string item in raw.Split(','))
            {
                if (Array.IndexOf(KnownViews, item) < 0) return false;
                wanted.Add(item);
            }
            views = new List<string>();
            foreach (string v in KnownViews)
                if (wanted.Contains(v)) views.Add(v);
            return true;
        }

        /// <summary>
        /// <c>none</c> (null, no state filter), <c>all</c> (every
        /// <see cref="PartParityStateRules.KnownStates"/> word), or a csv of known state words
        /// (re-ordered to the canonical order, duplicates dropped, no empty item).
        /// </summary>
        internal static bool TryParseStates(string raw, out List<string> states, out bool all)
        {
            states = null;
            all = false;
            if (raw == ArgNoneToken) return true;
            if (raw == StatesAllToken)
            {
                states = new List<string>(PartParityStateRules.KnownStates);
                all = true;
                return true;
            }
            return TryParseKnownCsv(raw, PartParityStateRules.KnownStates, out states);
        }

        /// <summary>Every context a <c>contexts=</c> csv may name, in canonical order: P6's
        /// (<see cref="PartParityStateRules.KnownContexts"/>), then the dust run's <c>ground</c>
        /// (design G.2.2), which lives in the FX rules so P6's tables do not change.</summary>
        internal static readonly string[] KnownRunContexts =
        {
            PartParityStateRules.ContextAlone, PartParityStateRules.ContextStacked, PartParityStateRules.ContextAir,
            PartParityStateRules.ContextFgv, PartParityFxRules.ContextGround,
        };

        /// <summary>
        /// <c>none</c> (null) or a csv of known contexts (canonical order, duplicates dropped).
        /// <c>air</c> stands alone: the air cases need a host at the pad and the orbit cases an
        /// orbiting one, so one step never plans both. <c>ground</c> stands alone too, and only
        /// an <c>fx=dust</c> run names it (the grammar refuses it otherwise).
        /// </summary>
        internal static bool TryParseContexts(string raw, out List<string> contexts)
        {
            contexts = null;
            if (raw == ArgNoneToken) return true;
            if (!TryParseKnownCsv(raw, KnownRunContexts, out List<string> list)) return false;
            if (list.Contains(PartParityStateRules.ContextAir) && list.Count > 1) return false;
            if (list.Contains(PartParityFxRules.ContextGround) && list.Count > 1) return false;
            contexts = list;
            return true;
        }

        private static bool TryParseKnownCsv(string raw, string[] known, out List<string> values)
        {
            values = null;
            if (string.IsNullOrEmpty(raw)) return false;
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string item in raw.Split(','))
            {
                if (Array.IndexOf(known, item) < 0) return false;
                wanted.Add(item);
            }
            values = new List<string>();
            foreach (string v in known)
                if (wanted.Contains(v)) values.Add(v);
            return true;
        }

        /// <summary>
        /// <c>none</c> (null), <c>all</c> (every <see cref="PartParityFxRules.ArgWords"/> word),
        /// or a csv of FX words (canonical order, duplicates dropped, no empty item).
        /// </summary>
        internal static bool TryParseFx(string raw, out List<string> fx, out bool all)
        {
            fx = null;
            all = false;
            if (raw == ArgNoneToken) return true;
            if (raw == PartParityFxRules.ArgAllToken)
            {
                fx = new List<string>(PartParityFxRules.ArgWords);
                all = true;
                return true;
            }
            return TryParseKnownCsv(raw, PartParityFxRules.KnownArgWords, out fx);
        }

        /// <summary>
        /// An <c>fx=</c> run plans FX cases only (design E.5), so it names no <c>states=</c>: a P6
        /// state word beside it would be silently ignored. Its words are of ONE kind (design G.7),
        /// since each kind has its own host, context and control set: P8 words with at most
        /// <c>contexts=air</c>; burst words with exactly <c>contexts=air</c> (a burst never runs in
        /// the orbit context, G.2.1); the dust word with exactly <c>contexts=ground</c>. The parse
        /// reads <c>fx=</c> after <c>states=</c> and <c>contexts=</c>.
        /// </summary>
        internal static bool FxCombinationValid(PartParitySweepArgs a, IList<string> fx)
        {
            if (a.States != null || fx == null) return false;
            bool burst = false, dust = false, p8 = false;
            foreach (string w in fx)
            {
                if (PartParityFxRules.IsBurstArg(w)) burst = true;
                else if (PartParityFxRules.IsDustArg(w)) dust = true;
                else p8 = true;
            }
            if ((burst ? 1 : 0) + (dust ? 1 : 0) + (p8 ? 1 : 0) != 1) return false;
            if (burst) return OnlyContext(a, PartParityStateRules.ContextAir);
            if (dust) return OnlyContext(a, PartParityFxRules.ContextGround);
            return a.Contexts == null || (a.Contexts.Count == 1 && a.Contexts[0] == PartParityStateRules.ContextAir);
        }

        private static bool OnlyContext(PartParitySweepArgs a, string context)
            => a.Contexts != null && a.Contexts.Count == 1 && a.Contexts[0] == context;

        /// <summary>The <c>fx=</c> setting token: <c>none</c>, <c>all</c>, or the csv.</summary>
        internal static string FormatFx(PartParitySweepArgs args)
        {
            if (args?.Fx == null) return ArgNoneToken;
            return args.FxAll ? PartParityFxRules.ArgAllToken : string.Join(",", args.Fx.ToArray());
        }

        /// <summary>True when the step names <c>fx=</c> other than <c>none</c>: it plans ONLY
        /// the FX cases of its families (design E.5).</summary>
        internal static bool IsFxRun(PartParitySweepArgs args) => args?.Fx != null;

        /// <summary>A burst run (design G.2.1): <c>fx=</c> of burst words (the grammar keeps a run to one kind).</summary>
        internal static bool IsBurstRun(PartParitySweepArgs args)
            => args?.Fx != null && args.Fx.Count > 0 && PartParityFxRules.IsBurstArg(args.Fx[0]);

        /// <summary>A dust run (design G.2.2): <c>fx=dust</c>.</summary>
        internal static bool IsDustRun(PartParitySweepArgs args)
            => args?.Fx != null && args.Fx.Count > 0 && PartParityFxRules.IsDustArg(args.Fx[0]);

        /// <summary>A burst or dust run (design G.7): refused until the live driver lands.</summary>
        internal static bool IsBurstDustRun(PartParitySweepArgs args) => IsBurstRun(args) || IsDustRun(args);

        /// <summary>True for a ground run: <c>contexts=ground</c> (the dust cases, design G.2.2).</summary>
        internal static bool IsGroundRun(PartParitySweepArgs args)
            => args?.Contexts != null && args.Contexts.Contains(PartParityFxRules.ContextGround);

        /// <summary>A run whose host is the pad host (design D.4, G.2): an air run or a ground run.</summary>
        internal static bool IsPadHostRun(PartParitySweepArgs args) => IsAirRun(args) || IsGroundRun(args);

        /// <summary>The <c>states=</c> setting token: <c>none</c>, <c>all</c>, or the csv.</summary>
        internal static string FormatStates(PartParitySweepArgs args)
        {
            if (args?.States == null) return ArgNoneToken;
            return args.StatesAll ? StatesAllToken : string.Join(",", args.States.ToArray());
        }

        /// <summary>The <c>contexts=</c> setting token: <c>none</c> or the csv.</summary>
        internal static string FormatContexts(PartParitySweepArgs args)
            => args?.Contexts == null ? ArgNoneToken : string.Join(",", args.Contexts.ToArray());

        /// <summary>True when the step names <c>states=</c> or <c>contexts=</c> other than
        /// <c>none</c>: it plans ONLY state and context cases (design D.2).</summary>
        internal static bool IsStateRun(PartParitySweepArgs args)
            => args != null && (args.States != null || args.Contexts != null);

        /// <summary>True for an air run: <c>contexts=air</c> (the parachute cases, D.4).</summary>
        internal static bool IsAirRun(PartParitySweepArgs args)
            => args?.Contexts != null && args.Contexts.Contains(PartParityStateRules.ContextAir);

        /// <summary>A state case (four atlas rows): any state but <c>stowed</c>. The stacked
        /// stowed shroud is a context case with the stowed case's two rows.</summary>
        internal static bool IsStateCase(PartParityCaseKey key)
            => key != null && !string.Equals(key.State ?? DefaultState, DefaultState, StringComparison.Ordinal);

        /// <summary>The manifest settings a run records its selection under, in order:
        /// <c>states</c>, <c>contexts</c>, <c>controlSet</c> (the ids, comma-joined), then
        /// <c>fx</c> for an FX run only (a manifest without it reads <c>none</c>).</summary>
        internal static List<KeyValuePair<string, string>> StateSettings(PartParitySweepArgs args)
        {
            var settings = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(SettingStates, FormatStates(args)),
                new KeyValuePair<string, string>(SettingContexts, FormatContexts(args)),
                new KeyValuePair<string, string>(SettingControlSet, string.Join(",", ControlSetFor(args))),
            };
            if (IsFxRun(args)) settings.Add(new KeyValuePair<string, string>(SettingFx, FormatFx(args)));
            return settings;
        }

        /// <summary>
        /// The control set of a run (design D.2), the same rule as
        /// <c>partparity.control_set(settings)</c>: no <c>states=</c> / <c>contexts=</c> runs
        /// the eight stowed controls (<see cref="ControlIds"/>, so PP-1 / PP-2 are unchanged);
        /// an air run <see cref="AirControlIds"/>; any other state run
        /// <see cref="OrbitStateControlIds"/>; an FX run (design E.5)
        /// <see cref="FxAirControlIds"/> or <see cref="FxOrbitControlIds"/>. <c>controls=false</c>
        /// runs none of them, but the set is still the run's (the manifest records it).
        /// </summary>
        internal static string[] ControlSetFor(PartParitySweepArgs args)
        {
            if (IsBurstRun(args)) return (string[])BurstControlIds.Clone();
            if (IsDustRun(args)) return (string[])DustControlIds.Clone();
            if (IsFxRun(args)) return (string[])(IsAirRun(args) ? FxAirControlIds : FxOrbitControlIds).Clone();
            if (!IsStateRun(args)) return (string[])ControlIds.Clone();
            return (string[])(IsAirRun(args) ? AirControlIds : OrbitStateControlIds).Clone();
        }

        private static bool IsIdChar(char c)
            => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
               || c == '.' || c == '_' || c == '-';

        /// <summary>Glob match, <c>*</c> any run and <c>?</c> one char, case-insensitive
        /// (ordinal, invariant upper-casing).</summary>
        internal static bool GlobMatch(string pattern, string text)
        {
            if (pattern == null || text == null) return false;
            int p = 0, t = 0, star = -1, mark = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || CharEq(pattern[p], text[t])))
                {
                    p++;
                    t++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    mark = t;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    t = ++mark;
                }
                else
                {
                    return false;
                }
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        private static bool CharEq(char a, char b)
            => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

        // ----- order, selection, sharding -----

        /// <summary>The stable case order: part name (ordinal), skin ordinal, skin name, state,
        /// context (all ordinal). Independent of enumeration order, so two runs of one install
        /// number the cases the same.</summary>
        internal static int CompareCases(PartParityCaseKey a, PartParityCaseKey b)
        {
            int c = string.CompareOrdinal(a.Part, b.Part);
            if (c != 0) return c;
            c = a.SkinOrdinal.CompareTo(b.SkinOrdinal);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Skin, b.Skin);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.State, b.State);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Context, b.Context);
        }

        /// <summary>
        /// The cases one step considers, in stable order: every case that passes
        /// <c>filter</c> (part name), <c>source</c> (case-insensitive) and <c>skins</c>, indexed
        /// 0.. in the stable order; then the shard (index mod n == k - 1); then the first
        /// <c>limit</c> of those. The index is taken BEFORE the shard, so shards of one sweep
        /// partition the same numbering.
        /// </summary>
        internal static List<PartParityPlannedCase> SelectCases(
            IEnumerable<PartParityCaseKey> cases, PartParitySweepArgs args)
        {
            if (args == null) args = new PartParitySweepArgs();
            var kept = new List<PartParityCaseKey>();
            if (cases != null)
            {
                foreach (var c in cases)
                {
                    if (c == null) continue;
                    if (!GlobMatch(args.Filter ?? DefaultFilter, c.Part ?? string.Empty)) continue;
                    if (args.Sources != null && !SourceSelected(args.Sources, c.Source)) continue;
                    if (args.Skins == PartParitySkinSelection.Default && c.SkinOrdinal != 0) continue;
                    kept.Add(c);
                }
            }
            // List.Sort is not stable, so the order must not lean on input order:
            // CompareCases is total over the tuple and exact duplicates are interchangeable.
            kept.Sort(CompareCases);

            var selected = new List<PartParityPlannedCase>();
            int n = args.ShardN < 1 ? 1 : args.ShardN;
            int k = args.ShardK < 1 ? 1 : args.ShardK;
            for (int i = 0; i < kept.Count; i++)
            {
                if (i % n != k - 1) continue;
                if (args.Limit > 0 && selected.Count >= args.Limit) break;
                selected.Add(new PartParityPlannedCase { Index = i, Key = kept[i], CaseId = CaseId(kept[i]) });
            }
            return selected;
        }

        /// <summary>
        /// The cases one step plans (design D.2). Without <c>states=</c> / <c>contexts=</c>:
        /// exactly <see cref="SelectCases"/> over the stowed keys, today's list in today's
        /// order. With either: ONLY the state and context keys whose state matches
        /// <c>states=</c> (any state when <c>none</c>) and whose context matches
        /// <c>contexts=</c> (<see cref="PartParityStateRules.OrbitContexts"/> when <c>none</c>;
        /// matched by <see cref="PartParityStateRules.ContextClass"/>, so <c>fgv</c> selects
        /// every FGV context), never a <c>stowed</c> / <c>alone</c> case, then the same filter / source / shard /
        /// limit selection. <c>skins=</c> governs stowed cases only (state cases are at the
        /// base skin, ruling R2). An FX run (design E.5) plans ONLY the FX keys of its
        /// <c>fx=</c> families, the air cases for <c>contexts=air</c> and the orbit ones
        /// otherwise (<see cref="PartParityFxRules.KeySelected"/>), then the same selection.
        /// </summary>
        internal static List<PartParityPlannedCase> PlanCases(
            IEnumerable<PartParityCaseKey> stowedKeys, IEnumerable<PartParityCaseKey> stateKeys,
            PartParitySweepArgs args, IEnumerable<PartParityCaseKey> fxKeys = null)
        {
            if (args == null) args = new PartParitySweepArgs();
            if (IsBurstDustRun(args))
            {
                var built = new List<PartParityCaseKey>();
                string context = IsBurstRun(args) ? PartParityFxRules.ContextBurst : PartParityFxRules.ContextGround;
                if (fxKeys != null)
                    foreach (var k in fxKeys)
                        if (PartParityFxRules.BuiltKeySelected(k, args.Fx, context)) built.Add(k);
                return SelectCases(built, args);
            }
            if (IsFxRun(args))
            {
                var fx = new List<PartParityCaseKey>();
                bool air = IsAirRun(args);
                if (fxKeys != null)
                    foreach (var k in fxKeys)
                        if (PartParityFxRules.KeySelected(k, args.Fx, air)) fx.Add(k);
                return SelectCases(fx, args);
            }
            if (!IsStateRun(args)) return SelectCases(stowedKeys, args);
            var kept = new List<PartParityCaseKey>();
            if (stateKeys != null)
            {
                foreach (var k in stateKeys)
                {
                    if (k == null) continue;
                    string state = k.StateOption?.State ?? k.State ?? DefaultState;
                    string context = k.Context ?? DefaultContext;
                    if (state == DefaultState && context == DefaultContext) continue;
                    if (args.States != null && !args.States.Contains(state)) continue;
                    string contextClass = PartParityStateRules.ContextClass(context);
                    if (args.Contexts != null ? !args.Contexts.Contains(contextClass)
                        : Array.IndexOf(PartParityStateRules.OrbitContexts, contextClass) < 0) continue;
                    kept.Add(k);
                }
            }
            return SelectCases(kept, args);
        }

        private static bool SourceSelected(List<string> sources, string source)
        {
            if (source == null) return false;
            foreach (string s in sources)
                if (string.Equals(s, source, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ----- case ids (design 5.2) -----

        /// <summary>Maps one id component: chars outside <c>[A-Za-z0-9._-]</c> become
        /// <c>-</c>, letters are lower-cased.</summary>
        internal static string MapIdComponent(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 'A' && c <= 'Z') sb.Append((char)(c + 32));
                else if (IsIdChar(c)) sb.Append(c);
                else sb.Append('-');
            }
            return sb.ToString();
        }

        /// <summary>FNV-1a 32-bit over the UTF-8 bytes of <paramref name="s"/>, as 8 lower
        /// hex digits.</summary>
        internal static string Fnv1a32Hex(string s)
        {
            uint h = 2166136261u;
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= 16777619u;
            }
            return h.ToString("x8", Ic);
        }

        /// <summary>The unmapped tuple the case-id hash is taken over: the four components
        /// joined by U+001F (a character no part, skin, state or context name holds).</summary>
        internal static string CaseTuple(PartParityCaseKey key)
            => (key.Part ?? string.Empty) + "\u001f" + (key.Skin ?? string.Empty) + "\u001f"
               + (key.State ?? string.Empty) + "\u001f" + (key.Context ?? string.Empty);

        /// <summary>
        /// The case id: <c>&lt;part&gt;--&lt;skin&gt;[--&lt;state&gt;][--&lt;context&gt;]--&lt;hash&gt;</c>,
        /// each component mapped by <see cref="MapIdComponent"/>. The state is written when it
        /// or the context is not the default (so a context never sits in the state's place),
        /// the context when it is not the default. The 8-hex hash of the UNMAPPED tuple keeps
        /// two names that map alike (case, punctuation) apart on a case-insensitive file
        /// system. The readable head is cut so the whole id fits
        /// <see cref="MaxCaseIdLength"/>, a head that does not start alphanumeric gains an
        /// <c>x</c>, and the result always passes <c>TestCommandCaptureScreenshot.IsValidLabel</c>.
        /// </summary>
        internal static string CaseId(PartParityCaseKey key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            bool contextNonDefault = !string.Equals(key.Context ?? DefaultContext, DefaultContext, StringComparison.Ordinal);
            bool stateNonDefault = !string.Equals(key.State ?? DefaultState, DefaultState, StringComparison.Ordinal);

            var head = new StringBuilder();
            head.Append(MapIdComponent(key.Part));
            head.Append(CaseIdSeparator).Append(MapIdComponent(key.Skin));
            if (stateNonDefault || contextNonDefault)
                head.Append(CaseIdSeparator).Append(MapIdComponent(key.State));
            if (contextNonDefault)
                head.Append(CaseIdSeparator).Append(MapIdComponent(key.Context));

            string h = head.ToString();
            if (h.Length == 0 || !IsAlnum(h[0])) h = "x" + h;
            string tail = CaseIdSeparator + Fnv1a32Hex(CaseTuple(key));
            int maxHead = MaxCaseIdLength - tail.Length;
            if (h.Length > maxHead) h = h.Substring(0, maxHead);
            return h + tail;
        }

        private static bool IsAlnum(char c)
            => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

        // ----- resume (design 5.3) -----

        /// <summary>The <c>inflight.txt</c> line that marks a solo rerun wave. The leading
        /// <c>#</c> is outside the case-id alphabet, so it never reads as a case.</summary>
        internal const string InflightSoloMarker = "#solo";

        /// <summary>The valid case ids of a one-id-per-line text, trimmed, in order, each
        /// once; blank and invalid lines (a torn or foreign line, the solo marker) are
        /// skipped.</summary>
        internal static List<string> ParseCaseIdLines(string text)
        {
            var ids = new List<string>();
            if (string.IsNullOrEmpty(text)) return ids;
            foreach (string raw in text.Split(new[] { '\r', '\n' }))
            {
                string line = raw.Trim();
                if (line.Length == 0 || !TestCommandCaptureScreenshot.IsValidLabel(line)) continue;
                if (!ids.Contains(line)) ids.Add(line);
            }
            return ids;
        }

        /// <summary>One id per line, each line ending in <c>\n</c>; null / empty ids skipped.</summary>
        internal static string FormatCaseIdLines(IEnumerable<string> caseIds)
        {
            var sb = new StringBuilder();
            if (caseIds != null)
                foreach (string id in caseIds)
                    if (!string.IsNullOrEmpty(id)) sb.Append(id).Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// An <c>inflight.txt</c>: every case id it names (one per line; a one-line file of
        /// the first format reads as before) and the solo marker. Null when it names no valid
        /// case id (an empty or torn file names nothing).
        /// </summary>
        internal static PartParityInflight ParseInflight(string text)
        {
            List<string> ids = ParseCaseIdLines(text);
            if (ids.Count == 0) return null;
            var r = new PartParityInflight();
            r.CaseIds.AddRange(ids);
            foreach (string raw in text.Split(new[] { '\r', '\n' }))
                if (string.Equals(raw.Trim(), InflightSoloMarker, StringComparison.Ordinal)) r.Solo = true;
            return r;
        }

        /// <summary>The <c>inflight.txt</c> text: the case ids one per line, then the solo
        /// marker line when the wave is a solo rerun. During a wave's spawn and settle it
        /// names every case of the wave; during a capture only the case being captured.</summary>
        internal static string FormatInflight(IList<string> caseIds, bool solo)
            => FormatCaseIdLines(caseIds) + (solo ? InflightSoloMarker + "\n" : string.Empty);

        /// <summary>The one-case form (the first format, unchanged).</summary>
        internal static string FormatInflight(string caseId) => FormatInflight(new[] { caseId }, false);

        /// <summary>
        /// The stale-inflight rule (design 5.3). The applier deletes <c>inflight.txt</c> after
        /// each case, so one found at step start is a previous process that died while the
        /// named cases were in flight. Exactly one name: that case is recorded <c>crashed</c>
        /// (a capture, a single-case wave, or the solo rerun of an isolated case). Several
        /// names (a multi-case wave died while spawning or settling): no case is blamed; each
        /// is owed a solo run, so the next crash isolates the culprit. A named case with a
        /// complete sidecar wins over either: a process that died between the sidecar write
        /// and the inflight delete finished it.
        /// </summary>
        internal static PartParityInflightDecision DecideInflight(
            PartParityInflight inflight, ICollection<string> completeCaseIds)
        {
            var d = new PartParityInflightDecision();
            if (inflight == null || inflight.CaseIds.Count == 0) return d;
            var pending = new List<string>();
            foreach (string id in inflight.CaseIds)
            {
                if (completeCaseIds != null && completeCaseIds.Contains(id)) d.Finished.Add(id);
                else pending.Add(id);
            }
            if (inflight.CaseIds.Count == 1)
            {
                if (pending.Count == 1) d.CrashedCaseId = pending[0];
                return d;
            }
            d.Isolate.AddRange(pending);
            return d;
        }

        /// <summary>The isolation list after a stale-inflight decision: the cases already
        /// owed a solo run, then the newly isolated ones (each id once, order kept), minus the
        /// case just recorded crashed and the named cases found complete.</summary>
        internal static List<string> MergeIsolate(IList<string> existing, PartParityInflightDecision decision)
        {
            var merged = new List<string>();
            if (existing != null)
                foreach (string id in existing)
                    if (!string.IsNullOrEmpty(id) && !merged.Contains(id)) merged.Add(id);
            if (decision != null)
            {
                foreach (string id in decision.Isolate)
                    if (!merged.Contains(id)) merged.Add(id);
                if (decision.CrashedCaseId != null) merged.Remove(decision.CrashedCaseId);
                foreach (string id in decision.Finished) merged.Remove(id);
            }
            return merged;
        }

        /// <summary>
        /// The resume rule for one case at the start of its turn: a complete sidecar skips it
        /// (a crash record IS a complete sidecar, written at step start by the stale-inflight
        /// rule); a case owed a solo run runs alone; any other case runs in a normal wave.
        /// </summary>
        internal static PartParityCaseResume DecideCaseResume(
            bool completeSidecarExists, bool owedSoloRun)
        {
            if (completeSidecarExists) return PartParityCaseResume.SkipComplete;
            return owedSoloRun ? PartParityCaseResume.RunSolo : PartParityCaseResume.Run;
        }

        /// <summary>
        /// Whether a step may start another wave: only when the time spent so far plus the
        /// previous wave's wall time stays inside the box, so a step ends before its budget
        /// rather than after. The first wave of a step always starts.
        /// </summary>
        internal static bool ShouldStartWave(double elapsedSeconds, int maxSeconds, double lastWaveSeconds)
        {
            if (elapsedSeconds <= 0.0) return true;
            double estimate = lastWaveSeconds > 0.0 ? lastWaveSeconds : 0.0;
            return elapsedSeconds + estimate <= maxSeconds;
        }

        /// <summary>The <c>controls=</c> payload token from the outcomes so far: <c>bad</c>
        /// when any control failed, else <c>pending</c> while any has not run, else <c>ok</c>;
        /// <c>off</c> when the step ran without controls.</summary>
        internal static string ControlsState(bool controlsEnabled, IList<string> outcomes)
            => ControlsState(controlsEnabled, outcomes, ControlIds);

        /// <summary>As above, against a run's own control set (<see cref="ControlSetFor"/>).</summary>
        internal static string ControlsState(bool controlsEnabled, IList<string> outcomes, IList<string> controlSet)
        {
            if (!controlsEnabled) return ControlsOff;
            int ok = 0;
            if (outcomes != null)
            {
                foreach (string o in outcomes)
                {
                    if (o == ControlsBad) return ControlsBad;
                    if (o == ControlsOk || o == ControlsNotApplicable) ok++;
                }
            }
            int need = controlSet != null ? controlSet.Count : ControlIds.Length;
            return ok >= need ? ControlsOk : ControlsPending;
        }

        // ----- admission and payload -----

        /// <summary>Null when the host may run a sweep, else the refusal reason.</summary>
        internal static string DecideHostAdmission(bool hasActiveVessel, bool activeVesselOrbiting)
        {
            if (!hasActiveVessel) return NoActiveVesselReason;
            if (!activeVesselOrbiting) return HostNotOrbitingReason;
            return null;
        }

        /// <summary>
        /// The admission of a run by its kind: an orbit run (stowed or state) needs an orbiting
        /// host, as above; an air run (design D.4) or a ground run (design G.2.2) a host PRELAUNCH or LANDED within
        /// <see cref="AirHostMaxPadDistanceM"/> of the KSC pad, else <c>host-not-at-pad</c>.
        /// A non-finite distance is not at the pad.
        /// </summary>
        internal static string DecideHostAdmission(PartParitySweepArgs args, bool hasActiveVessel,
            bool activeVesselOrbiting, bool activeVesselLandedOrPrelaunch, double distanceToPadM)
        {
            if (!IsPadHostRun(args)) return DecideHostAdmission(hasActiveVessel, activeVesselOrbiting);
            if (!hasActiveVessel) return NoActiveVesselReason;
            bool near = !double.IsNaN(distanceToPadM) && !double.IsInfinity(distanceToPadM)
                        && distanceToPadM >= 0.0 && distanceToPadM <= AirHostMaxPadDistanceM;
            return activeVesselLandedOrPrelaunch && near ? null : HostNotAtPadReason;
        }

        /// <summary>
        /// OK payload (design 7): <c>label done next cases complete crashed setupFaults
        /// controls wall hostDamaged hostDamage</c>, in that order. <c>next</c> is the
        /// position in the step's selection the next step resumes at (= <c>cases</c> when
        /// done); <c>wall</c> is the step's wall time in seconds, one decimal;
        /// <c>hostDamaged</c> is <c>true</c> once the host-integrity guard stopped the sweep
        /// (<see cref="PartParityHostIntegrity"/>), and <c>hostDamage</c> names why
        /// (<c>gone|parts|situation</c>, else <c>none</c>). The two host keys come last so a
        /// pin written for the first nine keys still matches.
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            string label, bool done, int next, int cases, int complete, int crashed,
            int setupFaults, string controls, double wallSeconds, string hostDamage = null)
        {
            bool damaged = !string.IsNullOrEmpty(hostDamage)
                           && !string.Equals(hostDamage, PartParityHostIntegrity.DamageNone, StringComparison.Ordinal);
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("label", label ?? string.Empty),
                new KeyValuePair<string, string>("done", done ? "true" : "false"),
                new KeyValuePair<string, string>("next", next.ToString(Ic)),
                new KeyValuePair<string, string>("cases", cases.ToString(Ic)),
                new KeyValuePair<string, string>("complete", complete.ToString(Ic)),
                new KeyValuePair<string, string>("crashed", crashed.ToString(Ic)),
                new KeyValuePair<string, string>("setupFaults", setupFaults.ToString(Ic)),
                new KeyValuePair<string, string>("controls", controls ?? ControlsPending),
                new KeyValuePair<string, string>("wall",
                    (double.IsNaN(wallSeconds) || double.IsInfinity(wallSeconds) ? 0.0 : wallSeconds)
                        .ToString("0.0", Ic)),
                new KeyValuePair<string, string>("hostDamaged", damaged ? "true" : "false"),
                new KeyValuePair<string, string>("hostDamage", damaged ? hostDamage : PartParityHostIntegrity.DamageNone),
            };
        }

        /// <summary>The payload as one <c>key=value</c> line, space-separated (the form a log
        /// line and a spec regex read).</summary>
        internal static string FormatPayload(List<KeyValuePair<string, string>> payload)
        {
            var sb = new StringBuilder();
            if (payload == null) return string.Empty;
            for (int i = 0; i < payload.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(payload[i].Key).Append('=').Append(payload[i].Value);
            }
            return sb.ToString();
        }
    }
}
