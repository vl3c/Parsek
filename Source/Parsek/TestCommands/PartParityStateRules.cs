using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>One prefab module of a part, as the state rules read it: its
    /// <c>moduleName</c>, its type chain and its MODULE node.</summary>
    internal sealed class PartParityStateModule
    {
        /// <summary>The module's index in the prefab's module list (the index a poller keys
        /// on and a <c>DeployModuleIndex</c> names).</summary>
        internal int Index;

        /// <summary>The module's <c>moduleName</c> (an exact-name poller compares this).</summary>
        internal string Name;

        /// <summary>The module's own type name, then each base type name, up to and including
        /// <c>PartModule</c>. A <c>FindModuleImplementing&lt;T&gt;</c> or an <c>as T</c> poller
        /// matches when <c>T</c>'s name is in it. Empty reads as the name alone.</summary>
        internal List<string> TypeChain = new List<string>();

        /// <summary>The module's MODULE node of the part config; null when it has none.</summary>
        internal ConfigNode Config;

        internal bool Implements(string typeName)
            => string.Equals(Name, typeName, StringComparison.Ordinal)
               || (TypeChain != null && TypeChain.Contains(typeName));

        internal bool Named(string moduleName) => string.Equals(Name, moduleName, StringComparison.Ordinal);

        internal string Value(string key) => Config?.GetValue(key);
    }

    /// <summary>One part as the state rules read it: its prefab modules in order and the
    /// names of its stack attach nodes.</summary>
    internal sealed class PartParityStatePart
    {
        internal string Name;
        internal List<PartParityStateModule> Modules = new List<PartParityStateModule>();

        /// <summary>The prefab's stack node names (<c>node_stack_&lt;name&gt;</c> without the prefix).</summary>
        internal List<string> StackNodes = new List<string>();

        /// <summary>The prefab's <c>Light</c> components; null when nothing read them (the part
        /// lists read from configs), which leaves every lamp in.</summary>
        internal int? LightCount;
    }

    /// <summary>What a state case presses and how (design D.2): not part of the case
    /// identity; the case id reads the key's State and Context.</summary>
    internal sealed class PartParityStateOption
    {
        /// <summary>One of <see cref="PartParityStateRules.Families"/>.</summary>
        internal string Family;

        /// <summary>The <c>moduleName</c> of the module the press acts on.</summary>
        internal string Module;

        /// <summary>That module's prefab index (the first jettison module for the jettison family).</summary>
        internal int ModuleIndex;

        /// <summary>Every module the press acts on, in prefab order (one entry except for jettison).</summary>
        internal List<int> ModuleIndices = new List<int>();

        /// <summary>The state word, without the same-word tiebreak.</summary>
        internal string State;

        /// <summary>The stock KSPEvent (or KSPAction, see <see cref="ActionIsKspAction"/>) pressed;
        /// null for a context case that presses nothing (the stacked stowed shroud).</summary>
        internal string Action;

        internal bool ActionIsKspAction;

        /// <summary>The attach nodes the context puts a filler part on (stacked context); empty otherwise.</summary>
        internal List<string> NodeNames = new List<string>();

        /// <summary>A stock KSPEvent of the module pressed in the same frame before
        /// <see cref="Action"/> (an FGV context's separation after the fairing is deployed);
        /// null for none.</summary>
        internal string PreAction;

        /// <summary>The FGV context the case authors (<see cref="PartParityStateRules.FgvContextTable"/>);
        /// null for every other case.</summary>
        internal PartParityFgvContext Fgv;
    }

    /// <summary>
    /// One of FGV-1's six stock configurations as a context of the part parity sweep (design
    /// D.10 S8): its context id <c>fgv-&lt;n&gt;-&lt;name&gt;</c>, the one part it is the
    /// subject of (the sweep's case part, its vessel's root), the family whose state it
    /// shows and FGV's states in it. The live authoring is
    /// <c>Parsek.InGameTests.FairingGhostAuthoring</c>, FGV's own.
    /// </summary>
    internal sealed class PartParityFgvContext
    {
        /// <summary>FGV's configuration number (1..6).</summary>
        internal int Number;
        internal string Id;
        internal string Subject;
        internal string Family;

        /// <summary>FGV's phases as state words: <c>stowed</c> (FGV's intact), <c>deployed</c>
        /// (jettisoned: the fairing deployed), <c>separated</c> (the part FGV separates has
        /// left; the event route is FGV's <c>separated</c> image, the start route its
        /// <c>separated-branch</c>).</summary>
        internal string[] States;

        /// <summary>The separation is the subject's own <c>ModuleDecouple</c> (the engine
        /// plate); otherwise the leaving part's <c>Part.decouple</c>.</summary>
        internal bool SeparateWithDecoupler;

        /// <summary>The separation runs after the fairing is deployed (FGV's order on the
        /// interstage: jettison, then separate).</summary>
        internal bool DeployBeforeSeparating;
    }

    /// <summary>A module (or part) a state run leaves out, and why (design D.1).</summary>
    internal sealed class PartParityStateExclusion
    {
        internal string Part;

        /// <summary>The family the module would belong to; null for a part-wide or family-less exclusion.</summary>
        internal string Family;

        internal string Module;

        /// <summary>The module's prefab index; -1 for a part-wide exclusion.</summary>
        internal int ModuleIndex = -1;

        /// <summary>One of <see cref="PartParityStateRules.ExclusionReasons"/>.</summary>
        internal string Reason;
    }

    /// <summary>The state and context cases of one part (or several) and what was left out.</summary>
    internal sealed class PartParityStateEnumeration
    {
        internal readonly List<PartParityCaseKey> Cases = new List<PartParityCaseKey>();
        internal readonly List<PartParityStateExclusion> Exclusions = new List<PartParityStateExclusion>();

        internal void AddRange(PartParityStateEnumeration other)
        {
            if (other == null) return;
            Cases.AddRange(other.Cases);
            Exclusions.AddRange(other.Exclusions);
        }
    }

    /// <summary>The stock values a family's done signal reads (design D.1), sampled by the
    /// live driver from the case's real part. A field the family does not read stays null.</summary>
    internal sealed class PartParityStateObservation
    {
        /// <summary><c>ModuleDeployablePart.deployState</c>, as its enum name.</summary>
        internal string DeployState;

        /// <summary><c>ModuleWheelDeployment.stateString</c>.</summary>
        internal string GearState;

        /// <summary>The recorder's own classifier verdict for the module (ladder, animation
        /// group, standalone animate generic, airbrake): <c>isDeployed</c>, null when the
        /// classifier could not classify it.</summary>
        internal bool? ClassifierDeployed;

        /// <summary>The cargo bay's linked <c>ModuleAnimateGeneric.animTime</c>, the bay's
        /// <c>closedPosition</c> and its deploy-limit stop
        /// (<c>FlightRecorder.ResolveCargoBayDeployLimitStop</c>).</summary>
        internal float? CargoAnimTime;
        internal float? CargoClosedPosition;
        internal float? CargoDeployLimitStop;

        /// <summary><c>ModuleLight.isOn</c>, or the colour changer's <c>animState</c>.</summary>
        internal bool? LightOn;

        /// <summary><c>ModuleParachute.deploymentState</c>, as its enum name.</summary>
        internal string ParachuteState;

        /// <summary>Whether any Animation of the part still plays (the parachute's deploy clip).</summary>
        internal bool? AnimationPlaying;

        /// <summary>How many of the part's jettison modules there are, and how many read
        /// <c>isJettisoned</c>.</summary>
        internal int JettisonModules;
        internal int JettisonedModules;

        /// <summary><c>ModuleProceduralFairing.GetScalar</c>.</summary>
        internal float? FairingScalar;

        /// <summary>An FGV separation: the leaving part is no longer on the case vessel.</summary>
        internal bool? SeparatedPartGone;

        /// <summary>An animation group's own deploy clip has finished (not playing, or
        /// clamped at its end); null when the module has no deploy clip.</summary>
        internal bool? DeployClipDone;
    }

    /// <summary>A ghost's own playback state for one state case after its prime (design D.5),
    /// read from the engine's info dictionaries. Plain values only.</summary>
    internal sealed class PartParityGhostStateInput
    {
        /// <summary>The engine holds an info entry of the family for this part.</summary>
        internal bool HasInfo;

        /// <summary>The info carries the sampled pose of the target state (a deploy clip
        /// sampled at both ends, a parachute semi / full canopy pose).</summary>
        internal bool PoseSampled = true;

        /// <summary>The deployable info: <c>deployFraction</c>, <c>currentDeployed</c>,
        /// <c>transitionActive</c>.</summary>
        internal float? DeployFraction;
        internal bool? CurrentDeployed;
        internal bool? TransitionActive;

        /// <summary>A light (or cabin light) the ghost shows lit; a fairing it shows deployed.</summary>
        internal bool? On;

        /// <summary>The parachute pose the ghost applied, as a state word (stowed, semi, full).</summary>
        internal string ParachuteApplied;

        /// <summary>The ghost's jettison transforms, and how many of them are hidden.</summary>
        internal int JettisonTransforms;
        internal int JettisonHidden;
    }

    /// <summary>Which wait a state wave admits against (design D.3).</summary>
    internal enum PartParityStateBucket
    {
        /// <summary>An animated family: the long state timeout.</summary>
        Long,

        /// <summary>Lights, the airbrake, jettisons, fairings: the quick timeout.</summary>
        Quick,

        /// <summary>Parachutes in the air: semi then full.</summary>
        Air,
    }

    /// <summary>
    /// The pure state rules of the part parity sweep (design-part-ghost-parity.md Appendix D,
    /// D.1 to D.5): the twelve transformation families, each matched with the SAME predicate
    /// its recorder poller uses (<c>PartStatePoller</c> and the active recorder's light
    /// check), so the sweep never finds a family the recorder would not poll and a subclass
    /// lands in one family only; the exclusions with their reasons; the state words, the
    /// same-word tiebreak and the stock actions; the done-signal readers over plain values;
    /// the timeouts and settle rules; and the ghost-side state read. Every rule reads plain
    /// objects and ConfigNodes, so xUnit drives it; the live driver builds the inputs.
    /// </summary>
    internal static class PartParityStateRules
    {
        // ----- families (D.1 table order) -----

        internal const string FamilyDeployable = "deployable";
        internal const string FamilyGear = "gear";
        internal const string FamilyLadder = "ladder";
        internal const string FamilyAnimationGroup = "animation-group";
        internal const string FamilyCargoBay = "cargo-bay";
        internal const string FamilyAnimateGeneric = "animate-generic";
        internal const string FamilyAirbrake = "airbrake";
        internal const string FamilyLight = "light";
        internal const string FamilyCabinLight = "cabin-light";
        internal const string FamilyParachute = "parachute";
        internal const string FamilyJettison = "jettison";
        internal const string FamilyFairing = "fairing";

        /// <summary>Every family, in the D.1 table order (the viewer's state order and the
        /// press gate's family table read this order).</summary>
        internal static readonly string[] Families =
        {
            FamilyDeployable, FamilyGear, FamilyLadder, FamilyAnimationGroup, FamilyCargoBay,
            FamilyAnimateGeneric, FamilyAirbrake, FamilyLight, FamilyCabinLight, FamilyParachute,
            FamilyJettison, FamilyFairing,
        };

        // ----- state words and contexts -----

        internal const string StateStowed = "stowed";
        internal const string StateDeployed = "deployed";
        internal const string StateOpen = "open";
        internal const string StateLit = "lit";
        internal const string StateSemi = "semi";
        internal const string StateFull = "full";
        internal const string StateJettisoned = "jettisoned";
        internal const string StateSeparated = "separated";

        /// <summary>Every state word a <c>states=</c> csv may name, in canonical order
        /// (<c>stowed</c> selects the stacked stowed context case; a state run never plans
        /// the alone stowed case).</summary>
        internal static readonly string[] KnownStates =
        {
            StateStowed, StateDeployed, StateOpen, StateLit, StateSemi, StateFull, StateJettisoned,
            StateSeparated,
        };

        internal const string ContextAlone = "alone";
        internal const string ContextStacked = "stacked";
        internal const string ContextAir = "air";

        /// <summary>The class word of every FGV context (<c>fgv-&lt;n&gt;-&lt;name&gt;</c>): a
        /// <c>contexts=</c> csv names it to select all six.</summary>
        internal const string ContextFgv = "fgv";
        internal const string ContextFgvPrefix = "fgv-";

        /// <summary>Every context a <c>contexts=</c> csv may name, in canonical order.</summary>
        internal static readonly string[] KnownContexts = { ContextAlone, ContextStacked, ContextAir, ContextFgv };

        /// <summary>The contexts an orbit run plans when <c>contexts=</c> is not given.</summary>
        internal static readonly string[] OrbitContexts = { ContextAlone, ContextStacked, ContextFgv };

        /// <summary>The word a <c>contexts=</c> csv selects a case context by: <c>fgv</c> for
        /// every <c>fgv-&lt;n&gt;-&lt;name&gt;</c> context, the context itself otherwise.</summary>
        internal static string ContextClass(string context)
            => context != null && context.StartsWith(ContextFgvPrefix, StringComparison.Ordinal) ? ContextFgv : context;

        /// <summary>The same-word tiebreak (D.1): when two or more cases of one part share a
        /// state word and a context, EVERY one of them writes <c>&lt;word&gt;-m&lt;moduleIndex&gt;</c>.</summary>
        internal const string ModuleTiebreakPrefix = "-m";

        // ----- the stock actions pressed (D.1) -----

        internal const string ActionExtend = "Extend";
        internal const string ActionEventToggle = "EventToggle";
        internal const string ActionDeployModule = "DeployModule";
        internal const string ActionToggle = "Toggle";
        internal const string ActionToggleBrakes = "ActionToggleBrakes";
        internal const string ActionLightsOn = "LightsOn";
        internal const string ActionToggleEvent = "ToggleEvent";
        internal const string ActionDeploy = "Deploy";
        internal const string ActionJettison = "Jettison";
        internal const string ActionDeployFairing = "DeployFairing";

        /// <summary>An FGV context's separation (not a stock event name): the subject's
        /// <c>ModuleDecouple.Decouple</c> or the leaving part's <c>Part.decouple</c>.</summary>
        internal const string ActionSeparate = "Separate";

        /// <summary>The <c>stateProof.actionResult</c> vocabulary.</summary>
        internal const string ActionInvoked = "invoked";
        internal const string ActionMissing = "missing";
        internal const string ActionThrew = "threw";
        internal const string ActionRefused = "refused";
        internal static readonly string[] ActionResults = { ActionInvoked, ActionMissing, ActionThrew, ActionRefused };

        // ----- the type and module names the pollers match -----

        internal const string TypeDeployablePart = "ModuleDeployablePart";
        internal const string TypeWheelDeployment = "ModuleWheelDeployment";
        internal const string ModuleRetractableLadder = "RetractableLadder";
        internal const string ModuleAnimationGroup = "ModuleAnimationGroup";
        internal const string TypeCargoBay = "ModuleCargoBay";
        internal const string TypeAnimateGeneric = "ModuleAnimateGeneric";
        internal const string ModuleAeroSurface = "ModuleAeroSurface";
        internal const string ModuleControlSurface = "ModuleControlSurface";
        internal const string ModuleRobotArmScanner = "ModuleRobotArmScanner";
        internal const string ModuleAnimateHeat = "ModuleAnimateHeat";
        internal const string TypeLight = "ModuleLight";
        internal const string TypeColorChanger = "ModuleColorChanger";
        internal const string TypeParachute = "ModuleParachute";
        internal const string TypeJettison = "ModuleJettison";
        internal const string TypeProceduralFairing = "ModuleProceduralFairing";
        internal const string TypeGroundPart = "ModuleGroundPart";
        internal const string TypeBaseServo = "BaseServo";
        internal const string TypeEngines = "ModuleEngines";
        internal const string TypeRcs = "ModuleRCS";
        internal const string TypeAblator = "ModuleAblator";

        // Config keys and their stock defaults (decompiled KSP 1.12.5 field initializers).
        internal const string KeyAnimationName = "animationName";
        internal const string AnimateGenericDefaultAnimationName = "default"; // ModuleAnimateGeneric
        internal const string KeyDeployModuleIndex = "DeployModuleIndex";        // ModuleCargoBay, default 0
        internal const string KeyToggleInFlight = "toggleInFlight";              // ModuleColorChanger, default true
        internal const string KeyBottomNodeName = "bottomNodeName";              // ModuleJettison, default "bottom"
        internal const string JettisonDefaultBottomNode = "bottom";
        internal const string KeyIgnoreNodes = "ignoreNodes";                    // ModuleJettison, default false
        internal const string KeyCheckBottomNode = "checkBottomNode";            // ModuleJettison, default false
        internal const string KeyAutoDeploy = "autoDeploy";                      // ModuleAnimationGroup, default false

        // ----- exclusions (D.1) -----

        internal const string ExcludeGroundDeploy = "ground-deploy";
        internal const string ExcludeNoPose = "no-pose";
        internal const string ExcludeFairingBay = "fairing-bay";
        internal const string ExcludeBayDeployNotAnimated = "bay-deploy-not-animated";
        internal const string ExcludeCsDeploySign = "cs-deploy-sign";
        internal const string ExcludeJettisonBlockedByNode = "jettison-blocked-by-node";
        internal const string ExcludeJettisonNodeMissing = "jettison-node-missing";
        internal const string ExcludeP7Robotics = "p7-robotics";
        internal const string ExcludeP8Heat = "p8-heat";
        internal const string ExcludeP8Fx = "p8-fx";
        internal const string ExcludeNotPolled = "not-polled";
        internal const string ExcludeNoAnimation = "no-animation";
        internal const string ExcludeFairingNoContext = "fairing-no-context";
        internal const string ExcludeAutoDeploy = "auto-deploy";

        /// <summary>Every exclusion reason a state run writes.</summary>
        internal static readonly string[] ExclusionReasons =
        {
            ExcludeGroundDeploy, ExcludeNoPose, ExcludeFairingBay, ExcludeBayDeployNotAnimated,
            ExcludeCsDeploySign, ExcludeJettisonBlockedByNode, ExcludeJettisonNodeMissing,
            ExcludeP7Robotics, ExcludeP8Heat, ExcludeP8Fx, ExcludeNotPolled, ExcludeNoAnimation,
            ExcludeFairingNoContext, ExcludeAutoDeploy,
        };

        internal const string ContextFgv1 = "fgv-1-plain-capped";
        internal const string ContextFgv2 = "fgv-2-interstage-truss";
        internal const string ContextFgv3 = "fgv-3-open-top";
        internal const string ContextFgv4 = "fgv-4-colour-variant";
        internal const string ContextFgv5 = "fgv-5-engine-plate";
        internal const string ContextFgv6 = "fgv-6-engine-shroud";

        /// <summary>The FGV contexts (FGV-1's six configurations, design D.10 S8), in FGV's
        /// order. A fairing part none of them names is excluded <c>fairing-no-context</c>
        /// (its stowed case still runs in PP-1 / PP-2).</summary>
        internal static readonly string[] FairingContexts =
        {
            ContextFgv1, ContextFgv2, ContextFgv3, ContextFgv4, ContextFgv5, ContextFgv6,
        };

        /// <summary>Each FGV context's subject, family and states (FGV's phases as state
        /// words). The fairings show the fairing family (intact, deployed, the interstage also
        /// separated after its jettison); the two skirts the jettison family (intact, and the
        /// shroud gone once the part below leaves). 13 cases, every FGV-1 image among them:
        /// the three separated cases each carry FGV's <c>separated</c> (event route) and
        /// <c>separated-branch</c> (start route) pair.</summary>
        internal static readonly PartParityFgvContext[] FgvContextTable =
        {
            new PartParityFgvContext { Number = 1, Id = ContextFgv1, Subject = "fairingSize1", Family = FamilyFairing,
                States = new[] { StateStowed, StateDeployed } },
            new PartParityFgvContext { Number = 2, Id = ContextFgv2, Subject = "fairingSize2", Family = FamilyFairing,
                States = new[] { StateStowed, StateDeployed, StateSeparated }, DeployBeforeSeparating = true },
            new PartParityFgvContext { Number = 3, Id = ContextFgv3, Subject = "fairingSize2", Family = FamilyFairing,
                States = new[] { StateStowed, StateDeployed } },
            new PartParityFgvContext { Number = 4, Id = ContextFgv4, Subject = "fairingSize1", Family = FamilyFairing,
                States = new[] { StateStowed, StateDeployed } },
            new PartParityFgvContext { Number = 5, Id = ContextFgv5, Subject = "EnginePlate2", Family = FamilyJettison,
                States = new[] { StateStowed, StateSeparated }, SeparateWithDecoupler = true },
            new PartParityFgvContext { Number = 6, Id = ContextFgv6, Subject = "liquidEngine2.v2", Family = FamilyJettison,
                States = new[] { StateStowed, StateSeparated } },
        };

        /// <summary>The FGV context of a context id; null for any other context.</summary>
        internal static PartParityFgvContext FgvContext(string contextId)
        {
            foreach (PartParityFgvContext c in FgvContextTable)
                if (string.Equals(c.Id, contextId, StringComparison.Ordinal)) return c;
            return null;
        }

        /// <summary>Whether a case's state leaves its jettison transforms (a shroud, the
        /// fairing's panels) off the part: jettisoned, separated (the shroud left with the
        /// part below; the interstage's panels were deployed first) or a deployed fairing.
        /// The real side then hides them as thrown debris.</summary>
        internal static bool ThrowsJettisonTransforms(PartParityStateOption option)
            => option != null
               && (option.State == StateJettisoned || option.State == StateSeparated
                   || (option.Family == FamilyFairing && option.State == StateDeployed));

        // ----- flags and tags a state case writes (D.3 to D.6) -----

        internal const string FlagStateNotModelledPrefix = "state-not-modelled:";
        internal const string FlagStateNotRecorded = "state-not-recorded";
        internal const string FlagStartPoseNotFirstFrame = "start-pose-not-first-frame";
        internal const string FlagStateInvisible = "state-invisible";
        internal const string FlagSunTrackerFirstFramePrefix = "sun-tracker-first-frame:";
        internal const string TagStillTracking = "still-tracking";
        internal const string TagCanopyWobblePrefix = "canopy-wobble:";
        internal const string TagContextHiddenPrefix = "context-hidden:";
        internal const string TagJettisonDebrisHiddenPrefix = "jettison-debris-hidden:";

        /// <summary>The two judged ghost routes (D.5), as a flag's route token.</summary>
        internal const string RouteEvent = "event";
        internal const string RouteStart = "start";

        // ----- the ghost-side state read (D.5) -----

        internal const string GhostApplied = "applied";
        internal const string GhostNoInfo = "no-info";
        internal const string GhostPoseNotSampled = "pose-not-sampled";
        internal const string GhostNotReached = "not-reached";
        internal static readonly string[] GhostOutcomes = { GhostApplied, GhostNoInfo, GhostPoseNotSampled, GhostNotReached };

        // ----- the press gate (D.6) -----

        /// <summary>The setup statuses the press gate counts: a press, wait or poll path
        /// that never reached its state.</summary>
        internal static readonly string[] PressGateStatuses =
        {
            TestCommandPartParitySweep.SetupActionFailed, TestCommandPartParitySweep.SetupStateTimeout,
        };

        // ----- waits (D.3, D.4) -----

        internal const double LongStateTimeoutSeconds = 20.0;
        internal const double QuickStateTimeoutSeconds = 5.0;
        internal const double TimeoutClipFactor = 2.0;
        internal const double TimeoutClipPadSeconds = 2.0;
        internal const double ChuteSemiTimeoutSeconds = 10.0;
        internal const double ChuteFullTimeoutSeconds = 15.0;

        internal const double LightSettleSeconds = 1.0;
        // Stock ModuleLight fades its lens in over lightBrightenSpeed GAME seconds (decompiled
        // FixedUpdate: _EmissiveColor = Lerp(black, lightColor, currentTime / lightBrightenSpeed)),
        // 2.5 s on every stock and ReStock light cfg, so a light settles for that long plus this
        // pad (never under LightSettleSeconds, never over LightSettleCapSeconds).
        internal const double LightBrightenPadSeconds = 0.5;
        internal const double LightSettleCapSeconds = 5.0;
        internal const double CabinLightPadSeconds = 0.5;
        internal const double CabinLightSettleCapSeconds = 5.0;

        internal const double SunTrackerStillDeg = 0.1;

        /// <summary>The angle a tracker may still be short of its aim when it counts as
        /// settled. Over a window W the exponential slew turns a remaining angle r by
        /// <c>r (1 - exp(-trackingSpeed W))</c>, so the still rule for one tracker is that
        /// turn at r = 0.85 deg (<see cref="SunTrackerStillDegFor"/>): 0.1 deg over 0.5 s at
        /// 0.25 (the panels' rule), 0.04 deg at a radiator's 0.1.</summary>
        internal const double SunTrackerRemainingDeg = 0.85;
        internal const double SunTrackerWindowSeconds = 0.5;
        // Stock slews a tracking pivot by Quaternion.Lerp(current, aim, deltaTime * trackingSpeed)
        // (decompiled ModuleDeployablePart, trackingSpeed 0.25 on every stock tracking panel): an
        // exponential approach with a 4 s time constant. The still rule (0.1 deg over 0.5 s) holds
        // once under 0.85 deg remain, which from the worst 180 deg takes ln(180 / 0.85) / 0.25 =
        // 21.4 s; 10 s left the panels 1 to 14 deg short (PP-3 2026-11-22_1634).
        internal const double SunTrackerCapSeconds = 25.0;

        /// <summary>The <c>trackingSpeed</c> the 25 s cap was derived at (every stock tracking
        /// solar panel).</summary>
        internal const double SunTrackerReferenceSpeed = 0.25;

        /// <summary>No tracker's cap exceeds this (a mod's near-zero speed would hold a wave
        /// for minutes); a tracker still moving there is tagged still-tracking.</summary>
        internal const double SunTrackerMaxCapSeconds = 90.0;

        internal const double AirbrakeStillDeg = 0.05;
        internal const double AirbrakeWindowSeconds = 0.5;
        internal const double AirbrakeCapSeconds = 5.0;

        internal const double CanopyWindowDeg = 3.0;
        internal const double CanopyCapSeconds = 4.0;

        /// <summary>The air case's chute config overrides (D.4): <c>deployAltitude</c> at the
        /// spawn, then raised for the full case; the spawn altitude and descent rate. Stock
        /// also opens fully when a raycast of <c>deployAltitude</c> metres down hits the
        /// terrain (decompiled <c>ModuleParachute.ShouldDeploy</c>), so the semi value is 500 m:
        /// a semi case falling from about 1530 m above the KSC terrain keeps about 1 km of
        /// margin before stock would open it fully.</summary>
        internal const double ChuteDeployAltitudeSemi = 500.0;
        internal const double ChuteDeployAltitudeFull = 5000.0;
        internal const double AirSpawnAltitudeM = 1600.0;
        internal const double AirSpawnDescentMps = 10.0;

        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // =====================================================================================
        // enumeration
        // =====================================================================================

        /// <summary>
        /// The state and context cases of one part and what it leaves out (D.1). The cases
        /// carry <paramref name="baseKey"/>'s part, source and base skin (state cases run at
        /// the base skin only, ruling R2) and a <see cref="PartParityStateOption"/>.
        /// </summary>
        internal static PartParityStateEnumeration Enumerate(PartParityStatePart part, PartParityCaseKey baseKey)
        {
            var r = new PartParityStateEnumeration();
            if (part == null || part.Modules == null) return r;
            var acc = new Accumulator
            {
                Name = part.Name ?? baseKey?.Part, Mods = part.Modules, Result = r,
            };
            ExcludeFamilyLess(acc);
            EnumerateDeployFamilies(acc);
            EnumerateBaysAndDoors(acc);
            EnumerateLightsAndChutes(acc);
            EnumerateJettison(acc.Name, part, acc.Cases, acc.Contexts, r);

            EnumerateFgvContexts(acc);

            BuildKeys(acc, baseKey);
            return r;
        }

        /// <summary>One part's enumeration in progress: its options and contexts, in order.</summary>
        private sealed class Accumulator
        {
            internal string Name;
            internal List<PartParityStateModule> Mods;
            internal PartParityStateEnumeration Result;
            internal readonly List<PartParityStateOption> Cases = new List<PartParityStateOption>();
            internal readonly List<string> Contexts = new List<string>();

            internal void Exclude(string family, PartParityStateModule m, string reason)
                => Result.Exclusions.Add(Ex(Name, family, m.Name, m.Index, reason));

            internal void Case(string family, PartParityStateModule m, string state, string action,
                bool isKspAction = false, string context = ContextAlone)
                => Add(Cases, Contexts, family, m, state, action, isKspAction, context);
        }

        /// <summary>Family-less exclusions: transforming modules the state run does not cover.</summary>
        private static void ExcludeFamilyLess(Accumulator acc)
        {
            foreach (PartParityStateModule m in acc.Mods)
            {
                if (m == null) continue;
                if (m.Implements(TypeBaseServo) || m.Named(ModuleRobotArmScanner))
                    acc.Exclude(null, m, ExcludeP7Robotics);
                else if (m.Named(ModuleControlSurface))
                    acc.Exclude(null, m, ExcludeCsDeploySign);
                else if (m.Named(ModuleAnimateHeat) || m.Implements(TypeAblator))
                    acc.Exclude(null, m, ExcludeP8Heat);
                else if (m.Implements(TypeEngines) || m.Implements(TypeRcs))
                    acc.Exclude(null, m, ExcludeP8Fx);
            }
        }

        /// <summary>The deployable, gear, ladder and animation group families.</summary>
        private static void EnumerateDeployFamilies(Accumulator acc)
        {
            List<PartParityStateModule> mods = acc.Mods;

            // Deployable panel / antenna / radiator: PollDeployables (FindModuleImplementing).
            int dep = FirstImplementing(mods, TypeDeployablePart);
            if (dep >= 0 && !mods[dep].Named(ModuleRobotArmScanner))
            {
                if (string.IsNullOrEmpty(mods[dep].Value(KeyAnimationName)))
                    acc.Exclude(FamilyDeployable, mods[dep], ExcludeNoPose);
                else
                    acc.Case(FamilyDeployable, mods[dep], StateDeployed, ActionExtend);
            }

            // Landing gear, legs, deploying wheels: PollGear (as ModuleWheelDeployment, first).
            int gear = FirstImplementing(mods, TypeWheelDeployment);
            if (gear >= 0) acc.Case(FamilyGear, mods[gear], StateDeployed, ActionEventToggle);

            // Ladder: PollLadders (exact name, first per part).
            int ladder = FirstNamed(mods, ModuleRetractableLadder);
            if (ladder >= 0) acc.Case(FamilyLadder, mods[ladder], StateDeployed, ActionExtend);

            // Animation group: PollAnimationGroups (exact name, every module). On a Breaking
            // Ground part (a ModuleGroundPart) it deploys only once placed on a surface. One
            // with autoDeploy deploys itself and hides Deploy / Retract (decompiled
            // ModuleAnimationGroup.OnLoad: isDeployed = true, both events and the three
            // actions inactive), so the player never sees it stowed and there is nothing to
            // press: the stowed case judges the only pose it has (the stock ISRU).
            bool groundPart = FirstImplementing(mods, TypeGroundPart) >= 0;
            foreach (PartParityStateModule m in mods)
            {
                if (m == null || !m.Named(ModuleAnimationGroup)) continue;
                if (groundPart) acc.Exclude(FamilyAnimationGroup, m, ExcludeGroundDeploy);
                else if (ParseBool(m.Value(KeyAutoDeploy), false)) acc.Exclude(FamilyAnimationGroup, m, ExcludeAutoDeploy);
                else acc.Case(FamilyAnimationGroup, m, StateDeployed, ActionDeployModule);
            }
        }

        /// <summary>The cargo bay, standalone animate generic and airbrake families.</summary>
        private static void EnumerateBaysAndDoors(Accumulator acc)
        {
            List<PartParityStateModule> mods = acc.Mods;

            // Cargo and service bay: PollCargoBays (FindModuleImplementing, DeployModuleIndex).
            int bay = FirstImplementing(mods, TypeCargoBay);
            int bayDeploy = -1;
            if (bay >= 0)
            {
                bayDeploy = ParseInt(mods[bay].Value(KeyDeployModuleIndex), 0);
                PartParityStateModule target = bayDeploy >= 0 && bayDeploy < mods.Count ? mods[bayDeploy] : null;
                if (target != null && target.Implements(TypeProceduralFairing))
                    acc.Exclude(FamilyCargoBay, mods[bay], ExcludeFairingBay);
                else if (target == null || !target.Implements(TypeAnimateGeneric))
                    acc.Exclude(FamilyCargoBay, mods[bay], ExcludeBayDeployNotAnimated);
                else
                    acc.Case(FamilyCargoBay, target, StateOpen, ActionToggle);
            }

            // Standalone animate generic: PollAnimateGeneric, only on a part with no dedicated
            // handler (FlightRecorder.HasDedicatedAnimateHandler). On a part that has one, every
            // animate generic but the cargo bay's own deploy module is never polled.
            bool dedicated = HasDedicatedAnimateHandler(mods);
            foreach (PartParityStateModule m in mods)
            {
                if (m == null || !m.Implements(TypeAnimateGeneric)) continue;
                if (dedicated)
                {
                    if (bay < 0 || m.Index != bayDeploy) acc.Exclude(FamilyAnimateGeneric, m, ExcludeNotPolled);
                    continue;
                }
                string anim = m.Config != null && m.Config.HasValue(KeyAnimationName)
                    ? m.Value(KeyAnimationName) : AnimateGenericDefaultAnimationName;
                if (string.IsNullOrEmpty(anim)) acc.Exclude(FamilyAnimateGeneric, m, ExcludeNoAnimation);
                else acc.Case(FamilyAnimateGeneric, m, StateDeployed, ActionToggle);
            }

            // Airbrake: PollAeroSurfaces (exact name, every module).
            foreach (PartParityStateModule m in mods)
                if (m != null && m.Named(ModuleAeroSurface))
                    acc.Case(FamilyAirbrake, m, StateDeployed, ActionToggleBrakes, true);
        }

        /// <summary>The light, cabin light and parachute families.</summary>
        private static void EnumerateLightsAndChutes(Accumulator acc)
        {
            List<PartParityStateModule> mods = acc.Mods;

            // Light, then the cabin light on a part with no ModuleLight (the active recorder's
            // light check: FindModuleImplementing<ModuleLight>, else the first ModuleColorChanger
            // with toggleInFlight).
            int light = FirstImplementing(mods, TypeLight);
            if (light >= 0)
                acc.Case(FamilyLight, mods[light], StateLit, ActionLightsOn);
            else
            {
                foreach (PartParityStateModule m in mods)
                {
                    if (m == null || !m.Implements(TypeColorChanger)) continue;
                    if (!ParseBool(m.Value(KeyToggleInFlight), true)) continue;
                    acc.Case(FamilyCabinLight, m, StateLit, ActionToggleEvent);
                    break;
                }
            }

            // Parachute: PollParachutes (FindModuleImplementing); semi and full, in the air.
            int chute = FirstImplementing(mods, TypeParachute);
            if (chute >= 0)
            {
                acc.Case(FamilyParachute, mods[chute], StateSemi, ActionDeploy, false, ContextAir);
                acc.Case(FamilyParachute, mods[chute], StateFull, ActionDeploy, false, ContextAir);
            }
        }

        /// <summary>
        /// The FGV contexts whose subject is this part (D.10 S8), one case per state: a
        /// fairing context reads the part's <c>ModuleProceduralFairing</c> (PollFairings,
        /// FindModuleImplementing), a skirt context its jettison modules (PollJettisons). A
        /// <c>stowed</c> case presses nothing (FGV's intact), <c>deployed</c> presses
        /// <c>DeployFairing</c>, <c>separated</c> is FGV's separation (after
        /// <c>DeployFairing</c> on the interstage). A fairing part no context names is
        /// excluded <c>fairing-no-context</c>.
        /// </summary>
        private static void EnumerateFgvContexts(Accumulator acc)
        {
            List<PartParityStateModule> mods = acc.Mods;
            int fairing = FirstImplementing(mods, TypeProceduralFairing);
            var jettison = new List<int>();
            foreach (PartParityStateModule m in mods)
                if (m != null && m.Implements(TypeJettison)) jettison.Add(m.Index);
            bool named = false;
            foreach (PartParityFgvContext c in FgvContextTable)
            {
                if (!string.Equals(c.Subject, acc.Name, StringComparison.Ordinal)) continue;
                List<int> indices;
                if (c.Family == FamilyFairing)
                {
                    if (fairing < 0) continue;
                    indices = new List<int> { fairing };
                }
                else
                {
                    if (jettison.Count == 0) continue;
                    indices = new List<int>(jettison);
                }
                named = true;
                PartParityStateModule first = mods[indices[0]];
                foreach (string state in c.States)
                {
                    var opt = new PartParityStateOption
                    {
                        Family = c.Family, Module = first.Name, ModuleIndex = first.Index,
                        ModuleIndices = new List<int>(indices), State = state, Fgv = c,
                    };
                    if (state == StateDeployed) opt.Action = ActionDeployFairing;
                    else if (state == StateSeparated)
                    {
                        opt.Action = ActionSeparate;
                        if (c.DeployBeforeSeparating) opt.PreAction = ActionDeployFairing;
                    }
                    acc.Cases.Add(opt);
                    acc.Contexts.Add(c.Id);
                }
            }
            if (fairing >= 0 && !named)
                acc.Exclude(FamilyFairing, mods[fairing], ExcludeFairingNoContext);
        }

        /// <summary>The case keys of the part's options, the same-word tiebreak applied.</summary>
        private static void BuildKeys(Accumulator acc, PartParityCaseKey baseKey)
        {
            ApplyTiebreak(acc.Cases, acc.Contexts, out List<string> words);
            string part = baseKey?.Part ?? acc.Name;
            string source = baseKey?.Source;
            string skin = baseKey?.Skin ?? TestCommandPartParitySweep.DefaultSkinName;
            for (int i = 0; i < acc.Cases.Count; i++)
            {
                acc.Result.Cases.Add(new PartParityCaseKey
                {
                    Part = part, Source = source, Skin = skin, SkinOrdinal = 0,
                    State = words[i], Context = acc.Contexts[i], StateOption = acc.Cases[i],
                });
            }
        }

        /// <summary>
        /// The jettison family (PollJettisons: every <c>as ModuleJettison</c> module), per part
        /// and by each module's own config (decompiled <c>ModuleJettison.SetupJettisonState</c>
        /// and <c>Jettison</c>): when every jettison module has <c>ignoreNodes</c> the shroud
        /// shows alone, so the part's one case is <c>jettisoned</c> / <c>alone</c>. Otherwise
        /// the context <c>stacked</c> puts a filler on each distinct <c>bottomNodeName</c> the
        /// non-ignoring modules name: a <c>stowed</c> / <c>stacked</c> case, and a
        /// <c>jettisoned</c> / <c>stacked</c> one unless a module has <c>checkBottomNode</c>
        /// (stock refuses to jettison with that node occupied). A node the prefab lacks
        /// excludes the part's stacked cases.
        /// </summary>
        private static void EnumerateJettison(string name, PartParityStatePart part,
            List<PartParityStateOption> cases, List<string> contexts, PartParityStateEnumeration r)
        {
            var all = new List<PartParityStateModule>();
            foreach (PartParityStateModule m in part.Modules)
                if (m != null && m.Implements(TypeJettison)) all.Add(m);
            if (all.Count == 0) return;

            var indices = new List<int>();
            foreach (var m in all) indices.Add(m.Index);
            var nodes = new List<string>();
            bool blocked = false;
            foreach (var m in all)
            {
                if (ParseBool(m.Value(KeyIgnoreNodes), false)) continue;
                string node = m.Value(KeyBottomNodeName);
                if (string.IsNullOrEmpty(node)) node = JettisonDefaultBottomNode;
                if (!nodes.Contains(node)) nodes.Add(node);
                if (ParseBool(m.Value(KeyCheckBottomNode), false)) blocked = true;
            }
            PartParityStateModule first = all[0];

            if (nodes.Count == 0)
            {
                cases.Add(JettisonOption(first, indices, StateJettisoned, ActionJettison, nodes));
                contexts.Add(ContextAlone);
                return;
            }
            foreach (string node in nodes)
            {
                if (part.StackNodes != null && part.StackNodes.Contains(node)) continue;
                r.Exclusions.Add(Ex(name, FamilyJettison, first.Name, first.Index, ExcludeJettisonNodeMissing));
                return;
            }
            cases.Add(JettisonOption(first, indices, StateStowed, null, nodes));
            contexts.Add(ContextStacked);
            if (blocked)
            {
                r.Exclusions.Add(Ex(name, FamilyJettison, first.Name, first.Index, ExcludeJettisonBlockedByNode));
                return;
            }
            cases.Add(JettisonOption(first, indices, StateJettisoned, ActionJettison, nodes));
            contexts.Add(ContextStacked);
        }

        private static PartParityStateOption JettisonOption(PartParityStateModule first, List<int> indices,
            string state, string action, List<string> nodes)
            => new PartParityStateOption
            {
                Family = FamilyJettison, Module = first.Name, ModuleIndex = first.Index,
                ModuleIndices = new List<int>(indices), State = state, Action = action,
                NodeNames = new List<string>(nodes),
            };

        private static void Add(List<PartParityStateOption> cases, List<string> contexts, string family,
            PartParityStateModule m, string state, string action, bool isKspAction, string context)
        {
            cases.Add(new PartParityStateOption
            {
                Family = family, Module = m.Name, ModuleIndex = m.Index,
                ModuleIndices = new List<int> { m.Index }, State = state, Action = action,
                ActionIsKspAction = isKspAction,
            });
            contexts.Add(context);
        }

        /// <summary>The same-word tiebreak (D.1): the case words, where every case of a
        /// (word, context) pair shared by two or more cases reads <c>&lt;word&gt;-m&lt;index&gt;</c>.</summary>
        private static void ApplyTiebreak(List<PartParityStateOption> cases, List<string> contexts, out List<string> words)
        {
            words = new List<string>(cases.Count);
            for (int i = 0; i < cases.Count; i++)
            {
                int same = 0;
                for (int j = 0; j < cases.Count; j++)
                    if (cases[j].State == cases[i].State && contexts[j] == contexts[i]) same++;
                words.Add(same > 1 ? TiebreakWord(cases[i].State, cases[i].ModuleIndex) : cases[i].State);
            }
        }

        internal static string TiebreakWord(string word, int moduleIndex)
            => (word ?? string.Empty) + ModuleTiebreakPrefix + moduleIndex.ToString(Ic);

        /// <summary>Mirror of <c>FlightRecorder.HasDedicatedAnimateHandler</c> over module names
        /// and type chains (its FindModuleImplementing checks and its exact names).</summary>
        internal static bool HasDedicatedAnimateHandler(IList<PartParityStateModule> mods)
        {
            if (mods == null) return false;
            foreach (string type in DedicatedHandlerTypes)
                if (FirstImplementing(mods, type) >= 0) return true;
            foreach (PartParityStateModule m in mods)
            {
                if (m == null) continue;
                if (Array.IndexOf(DedicatedHandlerModuleNames, m.Name) >= 0) return true;
            }
            return false;
        }

        /// <summary>The types <c>FlightRecorder.HasDedicatedAnimateHandler</c> finds with
        /// <c>FindModuleImplementing</c>, in its order (an xUnit cell reads them out of its body).</summary>
        internal static readonly string[] DedicatedHandlerTypes = { TypeDeployablePart, TypeWheelDeployment, TypeCargoBay };

        /// <summary>The <c>moduleName</c>s it compares exactly, in its order.</summary>
        internal static readonly string[] DedicatedHandlerModuleNames =
        {
            ModuleRetractableLadder, ModuleAnimationGroup, ModuleAeroSurface, ModuleControlSurface,
            ModuleRobotArmScanner,
        };

        private static int FirstImplementing(IList<PartParityStateModule> mods, string typeName)
        {
            for (int i = 0; i < mods.Count; i++)
                if (mods[i] != null && mods[i].Implements(typeName)) return i;
            return -1;
        }

        private static int FirstNamed(IList<PartParityStateModule> mods, string moduleName)
        {
            for (int i = 0; i < mods.Count; i++)
                if (mods[i] != null && mods[i].Named(moduleName)) return i;
            return -1;
        }

        private static PartParityStateExclusion Ex(string part, string family, string module, int index, string reason)
            => new PartParityStateExclusion { Part = part, Family = family, Module = module, ModuleIndex = index, Reason = reason };

        /// <summary>A KSP config bool (<c>bool.TryParse</c>, case-insensitive), else the default.</summary>
        internal static bool ParseBool(string raw, bool fallback)
            => raw != null && bool.TryParse(raw.Trim(), out bool b) ? b : fallback;

        internal static int ParseInt(string raw, int fallback)
            => raw != null && int.TryParse(raw.Trim(), NumberStyles.Integer, Ic, out int v) ? v : fallback;

        // =====================================================================================
        // counts (the per-step Info line, D.2)
        // =====================================================================================

        /// <summary>The literal the per-step state count line starts with.</summary>
        internal const string StateCountsLiteral = "state cases:";

        /// <summary>
        /// <c>state cases: states=&lt;..&gt; contexts=&lt;..&gt; install=&lt;n&gt; (&lt;family&gt;=&lt;n&gt; ...)
        /// planned=&lt;n&gt; excluded=&lt;n&gt; (&lt;reason&gt;=&lt;n&gt; ...)</c>: every state and context
        /// case the install offers by family (D.1 order, zero counts left out), the cases the
        /// step planned, and the exclusions by reason (D.1 order).
        /// </summary>
        internal static string FormatCounts(string states, string contexts,
            IEnumerable<PartParityCaseKey> install, int planned, IEnumerable<PartParityStateExclusion> excluded)
        {
            var byFamily = new Dictionary<string, int>(StringComparer.Ordinal);
            int total = 0;
            if (install != null)
                foreach (var k in install)
                {
                    if (k?.StateOption == null) continue;
                    total++;
                    byFamily.TryGetValue(k.StateOption.Family ?? string.Empty, out int n);
                    byFamily[k.StateOption.Family ?? string.Empty] = n + 1;
                }
            var byReason = new Dictionary<string, int>(StringComparer.Ordinal);
            int ex = 0;
            if (excluded != null)
                foreach (var e in excluded)
                {
                    if (e == null) continue;
                    ex++;
                    byReason.TryGetValue(e.Reason ?? string.Empty, out int n);
                    byReason[e.Reason ?? string.Empty] = n + 1;
                }
            var sb = new StringBuilder(StateCountsLiteral);
            sb.Append(" states=").Append(states ?? TestCommandPartParitySweep.ArgNoneToken)
                .Append(" contexts=").Append(contexts ?? TestCommandPartParitySweep.ArgNoneToken)
                .Append(" install=").Append(total.ToString(Ic)).Append(" (");
            AppendCounts(sb, Families, byFamily);
            sb.Append(") planned=").Append(planned.ToString(Ic))
                .Append(" excluded=").Append(ex.ToString(Ic)).Append(" (");
            AppendCounts(sb, ExclusionReasons, byReason);
            return sb.Append(')').ToString();
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
        // done signals (D.1, D.3, D.4)
        // =====================================================================================

        /// <summary>
        /// Whether the real part reached the case's state, by the stock field the recorder
        /// classifies (the D.1 table's done signal column; the settle that follows is
        /// <see cref="PostDoneSettleSeconds"/>). A context case with no press (the stacked
        /// stowed shroud) has reached its state at once.
        /// </summary>
        internal static bool StateReached(PartParityStateOption option, PartParityStateObservation o)
        {
            if (option == null) return false;
            if (option.Action == null) return true;
            if (o == null) return false;
            if (option.State == StateSeparated) return o.SeparatedPartGone == true;
            switch (option.Family)
            {
                case FamilyDeployable:
                    return string.Equals(o.DeployState, "EXTENDED", StringComparison.Ordinal);
                case FamilyGear:
                    FlightRecorder.ClassifyGearState(o.GearState, out bool gearDeployed, out _);
                    return gearDeployed;
                case FamilyAnimationGroup:
                    // The classifier reads isDeployed, which DeployModule sets before its clip
                    // plays (a ReStock drill's 10.7 s clip outlasted the settle cap): done once
                    // the module's own deploy clip has finished too.
                    return o.ClassifierDeployed == true && o.DeployClipDone != false;
                case FamilyLadder:
                case FamilyAnimateGeneric:
                case FamilyAirbrake:
                    return o.ClassifierDeployed == true;
                case FamilyCargoBay:
                    if (!o.CargoAnimTime.HasValue || !o.CargoClosedPosition.HasValue) return false;
                    FlightRecorder.ClassifyCargoBayState(o.CargoAnimTime.Value, o.CargoClosedPosition.Value,
                        o.CargoDeployLimitStop, out bool open, out _);
                    return open;
                case FamilyLight:
                case FamilyCabinLight:
                    return o.LightOn == true;
                case FamilyParachute:
                    string want = option.State == StateFull ? "DEPLOYED" : "SEMIDEPLOYED";
                    return string.Equals(o.ParachuteState, want, StringComparison.Ordinal) && o.AnimationPlaying == false;
                case FamilyJettison:
                    return o.JettisonModules > 0 && o.JettisonedModules >= o.JettisonModules;
                case FamilyFairing:
                    return o.FairingScalar.HasValue && o.FairingScalar.Value >= 0.5f;
                default:
                    return false;
            }
        }

        /// <summary>The wait bucket of a family (D.3).</summary>
        internal static PartParityStateBucket BucketFor(string family)
        {
            switch (family)
            {
                case FamilyLight:
                case FamilyCabinLight:
                case FamilyAirbrake:
                case FamilyJettison:
                case FamilyFairing:
                    return PartParityStateBucket.Quick;
                case FamilyParachute:
                    return PartParityStateBucket.Air;
                default:
                    return PartParityStateBucket.Long;
            }
        }

        /// <summary>
        /// The real-time wait for a case's done signal before <c>state-timeout</c> (D.3, D.4):
        /// an animated family waits <c>min(20 s, 2 x clip / |speed| + 2 s)</c> from the part's
        /// own Animation (20 s when the clip is unknown); the quick families 5 s; a parachute
        /// 10 s for semi and 15 s for full (measured from the semi done).
        /// </summary>
        internal static double StateTimeoutSeconds(PartParityStateOption option, double clipSeconds, double speed)
        {
            if (option == null) return LongStateTimeoutSeconds;
            switch (BucketFor(option.Family))
            {
                case PartParityStateBucket.Quick:
                    return QuickStateTimeoutSeconds;
                case PartParityStateBucket.Air:
                    return option.State == StateFull ? ChuteFullTimeoutSeconds : ChuteSemiTimeoutSeconds;
                default:
                    double s = Math.Abs(speed);
                    if (double.IsNaN(clipSeconds) || double.IsInfinity(clipSeconds) || clipSeconds <= 0.0
                        || double.IsNaN(s) || double.IsInfinity(s) || s <= 0.0)
                        return LongStateTimeoutSeconds;
                    return Math.Min(LongStateTimeoutSeconds, TimeoutClipFactor * clipSeconds / s + TimeoutClipPadSeconds);
            }
        }

        /// <summary>The settle after the done signal (D.1): a light its lens fade
        /// (<see cref="LightSettleFor"/>, 3 s at stock's 2.5 s); a cabin light
        /// <c>1 / animRate + 0.5 s</c>, at most 5 s; every other family the existing settle
        /// rule's minimum (no Animation playing, 0.5 s).</summary>
        internal static double PostDoneSettleSeconds(string family, double animRate,
            double lightBrightenSeconds = double.NaN)
        {
            switch (family)
            {
                case FamilyLight:
                    return LightSettleFor(lightBrightenSeconds);
                case FamilyCabinLight:
                    if (double.IsNaN(animRate) || double.IsInfinity(animRate) || animRate <= 0.0)
                        return CabinLightSettleCapSeconds;
                    return Math.Min(CabinLightSettleCapSeconds, 1.0 / animRate + CabinLightPadSeconds);
                default:
                    return PartParitySweepPlan.MinSettleSeconds;
            }
        }

        /// <summary>The longest settle a family can add after its done signal, for the wave
        /// bound: a cabin light its 5 s cap, an airbrake its 5 s stillness cap, a light its
        /// 5 s fade cap,
        /// every other family the 0.5 s minimum settle.</summary>
        internal static double SettleCapSeconds(string family)
        {
            switch (family)
            {
                case FamilyCabinLight: return CabinLightSettleCapSeconds;
                case FamilyAirbrake: return AirbrakeCapSeconds;
                case FamilyLight: return LightSettleCapSeconds;
                default: return PartParitySweepPlan.MinSettleSeconds;
            }
        }

        /// <summary>
        /// One tracker's cap (D.3). Stock slews by <c>Lerp(current, aim, deltaTime *
        /// trackingSpeed)</c> (decompiled <c>ModuleDeployablePart.CalculateTracking</c>), an
        /// exponential approach with time constant <c>1 / trackingSpeed</c>, so the 25 s cap
        /// derived at 0.25 scales by <c>0.25 / trackingSpeed</c>: 62.5 s for a folding radiator
        /// at 0.1. At most <see cref="SunTrackerMaxCapSeconds"/>; an unreadable or
        /// non-positive speed reads as 0.25.
        /// </summary>
        internal static double SunTrackerCapSecondsFor(double trackingSpeed)
        {
            if (double.IsNaN(trackingSpeed) || double.IsInfinity(trackingSpeed) || trackingSpeed <= 0.0)
                trackingSpeed = SunTrackerReferenceSpeed;
            return Math.Min(SunTrackerMaxCapSeconds, SunTrackerCapSeconds * SunTrackerReferenceSpeed / trackingSpeed);
        }

        /// <summary>
        /// A light's settle after its done signal (D.1): its own <c>lightBrightenSpeed</c> plus
        /// <see cref="LightBrightenPadSeconds"/>, at least <see cref="LightSettleSeconds"/> and
        /// at most <see cref="LightSettleCapSeconds"/>; the cap when the speed is unreadable.
        /// </summary>
        internal static double LightSettleFor(double lightBrightenSeconds)
        {
            if (double.IsNaN(lightBrightenSeconds) || double.IsInfinity(lightBrightenSeconds) || lightBrightenSeconds < 0.0)
                return LightSettleCapSeconds;
            return Math.Min(LightSettleCapSeconds, Math.Max(LightSettleSeconds, lightBrightenSeconds + LightBrightenPadSeconds));
        }

        /// <summary>A sun tracker has settled when its pivot turned less than 0.1 deg over the
        /// last 0.5 s (D.3).</summary>
        internal static bool SunTrackerSettled(double pivotDeltaDegOverWindow)
            => !double.IsNaN(pivotDeltaDegOverWindow) && pivotDeltaDegOverWindow < SunTrackerStillDeg;

        /// <summary>One tracker's still threshold over <see cref="SunTrackerWindowSeconds"/>:
        /// the turn its own slew makes there with <see cref="SunTrackerRemainingDeg"/> left
        /// (an unreadable or non-positive speed reads as 0.25).</summary>
        internal static double SunTrackerStillDegFor(double trackingSpeed)
        {
            if (double.IsNaN(trackingSpeed) || double.IsInfinity(trackingSpeed) || trackingSpeed <= 0.0)
                trackingSpeed = SunTrackerReferenceSpeed;
            return SunTrackerRemainingDeg * (1.0 - Math.Exp(-trackingSpeed * SunTrackerWindowSeconds));
        }

        /// <summary>A tracker of <paramref name="trackingSpeed"/> has settled when its pivot
        /// turned less than <see cref="SunTrackerStillDegFor"/> over the window.</summary>
        internal static bool SunTrackerSettled(double pivotDeltaDegOverWindow, double trackingSpeed)
            => !double.IsNaN(pivotDeltaDegOverWindow) && pivotDeltaDegOverWindow < SunTrackerStillDegFor(trackingSpeed);

        /// <summary>
        /// The angle in degrees between two unit quaternions [x, y, z, w], exact for small
        /// angles: <c>2 atan2(|v|, |w|)</c> of <c>conj(a) * b</c> in double. Unity's
        /// <c>Quaternion.Angle</c> returns 0 below about 0.16 deg (its <c>IsEqualUsingDot</c>
        /// test, dot > 1 - 1e-6), which made the still rule read 0 with a radiator still
        /// 3 deg short of its aim.
        /// </summary>
        internal static double PreciseAngleDeg(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != 4 || b.Length != 4) return double.NaN;
            double ax = -a[0], ay = -a[1], az = -a[2], aw = a[3];
            double bx = b[0], by = b[1], bz = b[2], bw = b[3];
            double w = aw * bw - ax * bx - ay * by - az * bz;
            double x = aw * bx + ax * bw + ay * bz - az * by;
            double y = aw * by - ax * bz + ay * bw + az * bx;
            double z = aw * bz + ax * by - ay * bx + az * bw;
            double v = Math.Sqrt(x * x + y * y + z * z);
            if (double.IsNaN(v) || double.IsNaN(w)) return double.NaN;
            return 2.0 * Math.Atan2(v, Math.Abs(w)) * 180.0 / Math.PI;
        }

        /// <summary>An airbrake has settled when its surface transform turned less than 0.05 deg
        /// over the last 0.5 s (D.3).</summary>
        internal static bool AirbrakeSettled(double surfaceDeltaDegOverWindow)
            => !double.IsNaN(surfaceDeltaDegOverWindow) && surfaceDeltaDegOverWindow < AirbrakeStillDeg;

        /// <summary>An air case captures at the first frame whose canopy airflow tilt plus
        /// wobble is under 3 deg (D.4); after 4 s it captures anyway, tagged.</summary>
        internal static bool CanopyCaptureReady(double tiltDeg, double wobbleDeg)
            => !double.IsNaN(tiltDeg) && !double.IsNaN(wobbleDeg) && tiltDeg + wobbleDeg < CanopyWindowDeg;

        // =====================================================================================
        // the ghost side (D.5)
        // =====================================================================================

        /// <summary>
        /// Whether a ghost represents the case's state after its prime, read-only from its own
        /// playback state: <c>no-info</c> (the engine built no info of the family for the part),
        /// <c>pose-not-sampled</c> (the info has no sampled pose for the state), else
        /// <c>applied</c> when the state shows (a deploy family at fraction 1, deployed and not
        /// mid-transition; a light lit; the parachute's semi / full pose; every jettison transform
        /// hidden; a fairing deployed) and <c>not-reached</c> otherwise.
        /// </summary>
        internal static string GhostStateReached(PartParityStateOption option, PartParityGhostStateInput g)
        {
            if (option == null || g == null || !g.HasInfo) return GhostNoInfo;
            if (!g.PoseSampled) return GhostPoseNotSampled;
            if (option.Action == null && option.State == StateStowed) return GhostApplied;
            bool reached;
            switch (option.Family)
            {
                case FamilyLight:
                case FamilyCabinLight:
                case FamilyFairing:
                    reached = g.On == true;
                    break;
                case FamilyParachute:
                    reached = string.Equals(g.ParachuteApplied, option.State, StringComparison.Ordinal);
                    break;
                case FamilyJettison:
                    reached = g.JettisonTransforms > 0 && g.JettisonHidden >= g.JettisonTransforms;
                    break;
                default:
                    reached = g.DeployFraction.HasValue && g.DeployFraction.Value >= 1f - 1e-4f
                              && g.CurrentDeployed == true && g.TransitionActive != true;
                    break;
            }
            return reached ? GhostApplied : GhostNotReached;
        }

        /// <summary>The flag a route that is not <c>applied</c> raises:
        /// <c>state-not-modelled:&lt;route&gt;:&lt;outcome&gt;</c>; null when applied.</summary>
        internal static string StateNotModelledFlag(string route, string outcome)
            => string.Equals(outcome, GhostApplied, StringComparison.Ordinal) || string.IsNullOrEmpty(outcome)
                ? null
                : FlagStateNotModelledPrefix + (route ?? string.Empty) + ":" + outcome;
    }
}
