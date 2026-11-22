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

        /// <summary>Every state word a <c>states=</c> csv may name, in canonical order
        /// (<c>stowed</c> selects the stacked stowed context case; a state run never plans
        /// the alone stowed case).</summary>
        internal static readonly string[] KnownStates =
        {
            StateStowed, StateDeployed, StateOpen, StateLit, StateSemi, StateFull, StateJettisoned,
        };

        internal const string ContextAlone = "alone";
        internal const string ContextStacked = "stacked";
        internal const string ContextAir = "air";

        /// <summary>Every context a <c>contexts=</c> csv may name, in canonical order.</summary>
        internal static readonly string[] KnownContexts = { ContextAlone, ContextStacked, ContextAir };

        /// <summary>The contexts an orbit run plans when <c>contexts=</c> is not given.</summary>
        internal static readonly string[] OrbitContexts = { ContextAlone, ContextStacked };

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
        internal const string ExcludeFairingContextsPending = "fairing-contexts-pending";

        /// <summary>Every exclusion reason a state run writes.</summary>
        internal static readonly string[] ExclusionReasons =
        {
            ExcludeGroundDeploy, ExcludeNoPose, ExcludeFairingBay, ExcludeBayDeployNotAnimated,
            ExcludeCsDeploySign, ExcludeJettisonBlockedByNode, ExcludeJettisonNodeMissing,
            ExcludeP7Robotics, ExcludeP8Heat, ExcludeP8Fx, ExcludeNotPolled, ExcludeNoAnimation,
            ExcludeFairingContextsPending,
        };

        /// <summary>The fairing family's contexts (FGV's six configurations); filled with
        /// their states by the FGV migration (D.10 S8). While empty, each fairing part is
        /// excluded <c>fairing-contexts-pending</c>.</summary>
        internal static readonly string[] FairingContexts = { };

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
        internal const double CabinLightPadSeconds = 0.5;
        internal const double CabinLightSettleCapSeconds = 5.0;

        internal const double SunTrackerStillDeg = 0.1;
        internal const double SunTrackerWindowSeconds = 0.5;
        internal const double SunTrackerCapSeconds = 10.0;

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

            // Procedural fairing: PollFairings (FindModuleImplementing). FGV's contexts land with S8.
            int fairing = FirstImplementing(acc.Mods, TypeProceduralFairing);
            if (fairing >= 0 && FairingContexts.Length == 0)
                r.Exclusions.Add(Ex(acc.Name, FamilyFairing, acc.Mods[fairing].Name, fairing, ExcludeFairingContextsPending));

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
            // Ground part (a ModuleGroundPart) it deploys only once placed on a surface.
            bool groundPart = FirstImplementing(mods, TypeGroundPart) >= 0;
            foreach (PartParityStateModule m in mods)
            {
                if (m == null || !m.Named(ModuleAnimationGroup)) continue;
                if (groundPart) acc.Exclude(FamilyAnimationGroup, m, ExcludeGroundDeploy);
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
            ModuleRobotArmScanner, ModuleAnimateHeat,
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
            switch (option.Family)
            {
                case FamilyDeployable:
                    return string.Equals(o.DeployState, "EXTENDED", StringComparison.Ordinal);
                case FamilyGear:
                    FlightRecorder.ClassifyGearState(o.GearState, out bool gearDeployed, out _);
                    return gearDeployed;
                case FamilyLadder:
                case FamilyAnimationGroup:
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

        /// <summary>The settle after the done signal (D.1): a light 1 s; a cabin light
        /// <c>1 / animRate + 0.5 s</c>, at most 5 s; every other family the existing settle
        /// rule's minimum (no Animation playing, 0.5 s).</summary>
        internal static double PostDoneSettleSeconds(string family, double animRate)
        {
            switch (family)
            {
                case FamilyLight:
                    return LightSettleSeconds;
                case FamilyCabinLight:
                    if (double.IsNaN(animRate) || double.IsInfinity(animRate) || animRate <= 0.0)
                        return CabinLightSettleCapSeconds;
                    return Math.Min(CabinLightSettleCapSeconds, 1.0 / animRate + CabinLightPadSeconds);
                default:
                    return PartParitySweepPlan.MinSettleSeconds;
            }
        }

        /// <summary>The longest settle a family can add after its done signal, for the wave
        /// bound: a cabin light its 5 s cap, an airbrake its 5 s stillness cap, a light 1 s,
        /// every other family the 0.5 s minimum settle.</summary>
        internal static double SettleCapSeconds(string family)
        {
            switch (family)
            {
                case FamilyCabinLight: return CabinLightSettleCapSeconds;
                case FamilyAirbrake: return AirbrakeCapSeconds;
                case FamilyLight: return LightSettleSeconds;
                default: return PartParitySweepPlan.MinSettleSeconds;
            }
        }

        /// <summary>A sun tracker has settled when its pivot turned less than 0.1 deg over the
        /// last 0.5 s (D.3).</summary>
        internal static bool SunTrackerSettled(double pivotDeltaDegOverWindow)
            => !double.IsNaN(pivotDeltaDegOverWindow) && pivotDeltaDegOverWindow < SunTrackerStillDeg;

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
