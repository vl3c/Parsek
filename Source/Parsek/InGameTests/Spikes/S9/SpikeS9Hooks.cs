using System;
using System.Collections.Generic;
using Cheats = KSP.UI.Screens.DebugToolbar.Screens.Cheats;
using ContractScreens = KSP.UI.Screens.DebugToolbar.Screens.Contract;
using GamePlayScreens = KSP.UI.Screens.DebugToolbar.Screens.GamePlay;
using PhysicsScreens = KSP.UI.Screens.DebugToolbar.Screens.Physics;
using ResourceScreens = KSP.UI.Screens.DebugToolbar.Screens.Resources;

namespace Parsek.InGameTests.Spikes
{
    /// <summary>One hand-applied S9 hook: the stock method, its layer and the tool it stands for.</summary>
    internal sealed class SpikeS9Hook
    {
        /// <summary>The debug action as a player names it ("max-tech", "console-funds").</summary>
        internal readonly string Tool;
        /// <summary>ui, toggle, console, legacy, effect or currency.</summary>
        internal readonly string Layer;
        internal readonly Type Type;
        internal readonly string Method;
        /// <summary>Exact argument types; null means "the one declared method of that name".</summary>
        internal readonly Type[] Args;

        internal SpikeS9Hook(string tool, string layer, Type type, string method, Type[] args)
        {
            Tool = tool;
            Layer = layer;
            Type = type;
            Method = method;
            Args = args;
        }

        /// <summary>A currency hook logs (and opens a scope) only for the stock cheat reason.</summary>
        internal bool CheatReasonOnly => Layer == SpikeS9Hooks.LayerCurrency;

        internal string Entry => SpikeS9Format.EntryLabel(Type?.FullName, Method);
    }

    /// <summary>
    /// Every S9 hook. Each one is a DECLARED method of its type (the override, not the base),
    /// checked against KSP 1.12.5 Assembly-CSharp with ikdasm and resolved by xUnit
    /// (SpikeS9HooksTests) against the reference assembly. Private UI handlers are the
    /// "once per press" layer; the effect layer is what the console, the legacy window and
    /// the UI all reach.
    /// </summary>
    internal static class SpikeS9Hooks
    {
        internal const string LayerUi = "ui";
        internal const string LayerToggle = "toggle";
        internal const string LayerConsole = "console";
        internal const string LayerLegacy = "legacy";
        internal const string LayerEffect = "effect";
        internal const string LayerCurrency = "currency";

        private static readonly Type[] None = Type.EmptyTypes;
        private static readonly Type[] OneBool = { typeof(bool) };
        private static readonly Type[] OneString = { typeof(string) };

        internal static IReadOnlyList<SpikeS9Hook> All()
        {
            var h = new List<SpikeS9Hook>();

            // Alt+F12 > Cheats > Career (CheatsCareer, private click handlers).
            Type career = typeof(Cheats.CheatsCareer);
            h.Add(new SpikeS9Hook("funds-plus-small", LayerUi, career, "OnFundsPosClick", None));
            h.Add(new SpikeS9Hook("funds-plus-big", LayerUi, career, "OnFundsPosBigClick", None));
            h.Add(new SpikeS9Hook("funds-minus-small", LayerUi, career, "OnFundsNegClick", None));
            h.Add(new SpikeS9Hook("funds-minus-big", LayerUi, career, "OnFundsNegBigClick", None));
            h.Add(new SpikeS9Hook("science-plus-small", LayerUi, career, "OnSciencePosClick", None));
            h.Add(new SpikeS9Hook("science-plus-big", LayerUi, career, "OnSciencePosBigClick", None));
            h.Add(new SpikeS9Hook("science-minus-small", LayerUi, career, "OnScienceNegClick", None));
            h.Add(new SpikeS9Hook("science-minus-big", LayerUi, career, "OnScienceNegBigClick", None));
            h.Add(new SpikeS9Hook("rep-plus-small", LayerUi, career, "OnRepPosClick", None));
            h.Add(new SpikeS9Hook("rep-plus-big", LayerUi, career, "OnRepPosBigClick", None));
            h.Add(new SpikeS9Hook("rep-minus-small", LayerUi, career, "OnRepNegClick", None));
            h.Add(new SpikeS9Hook("rep-minus-big", LayerUi, career, "OnRepNegBigClick", None));
            h.Add(new SpikeS9Hook("max-tech", LayerUi, career, "OnMaxTechClick", None));
            h.Add(new SpikeS9Hook("max-facilities", LayerUi, career, "OnMaxFacilityClick", None));
            h.Add(new SpikeS9Hook("max-xp", LayerUi, career, "OnMaxXPClick", None));
            h.Add(new SpikeS9Hook("repair", LayerUi, career, "FixRepairableParts", None));
            // Max Progression: right click -> CheatEarlyProgression, any other -> CheatProgression.
            h.Add(new SpikeS9Hook("max-progress", LayerUi, typeof(Cheats.MaxProgression), "OnPointerClick", null));

            // Cheat switches (DebugScreenToggle overrides; each writes one CheatOptions static).
            h.Add(new SpikeS9Hook("allow-part-clipping", LayerToggle, typeof(Cheats.AllowPartClipping), "OnToggleChanged", OneBool));
            h.Add(new SpikeS9Hook("biomes-visible", LayerToggle, typeof(Cheats.BiomesVisibleInMap), "OnToggleChanged", OneBool));
            h.Add(new SpikeS9Hook("ignore-eva-construction-mass", LayerToggle, typeof(Cheats.IgnoreEVAConstructionMassLimit), "OnToggleChanged", OneBool));
            h.Add(new SpikeS9Hook("ignore-kerbal-inventory", LayerToggle, typeof(Cheats.IgnoreKerbalInventoryLimits), "OnToggleChanged", OneBool));
            h.Add(new SpikeS9Hook("ignore-max-temperature", LayerToggle, typeof(Cheats.IgnoreMaxTemperature), "OnToggleChanged", OneBool));
            h.Add(new SpikeS9Hook("infinite-electricity", LayerToggle, typeof(Cheats.InfiniteElectricity), "OnToggleChanged", OneBool));
            h.Add(new SpikeS9Hook("infinite-propellant", LayerToggle, typeof(Cheats.InfinitePropellant), "OnToggleChanged", OneBool));
            h.Add(new SpikeS9Hook("middle-click-set-position", LayerToggle, typeof(Cheats.MiddleMouseClickSetPosition), "OnToggleChanged", OneBool));
            h.Add(new SpikeS9Hook("no-crash-damage", LayerToggle, typeof(Cheats.NoCrashDamage), "OnToggleChanged", OneBool));
            h.Add(new SpikeS9Hook("non-strict-attachment", LayerToggle, typeof(Cheats.NonStrictPartAttachment), "OnToggleChanged", OneBool));
            h.Add(new SpikeS9Hook("pause-on-vessel-unpack", LayerToggle, typeof(Cheats.PauseOnVesselUnpack), "OnToggleChanged", OneBool));
            h.Add(new SpikeS9Hook("unbreakable-joints", LayerToggle, typeof(Cheats.UnbreakableJoints), "OnToggleChanged", OneBool));
            Type gravity = typeof(Cheats.HackGravity);
            h.Add(new SpikeS9Hook("hack-gravity-toggle", LayerToggle, gravity, "OnToggleChanged", OneBool));
            h.Add(new SpikeS9Hook("hack-gravity-slider", LayerToggle, gravity, "OnSliderChanged", new[] { typeof(float) }));
            h.Add(new SpikeS9Hook("hack-gravity-reset", LayerToggle, gravity, "OnResetClick", None));
            h.Add(new SpikeS9Hook("hack-gravity-set", LayerEffect, gravity, "SetGravityFactor", new[] { typeof(double) }));

            // Teleports (Alt+F12 > Cheats > Set Orbit / Set Position).
            h.Add(new SpikeS9Hook("set-orbit", LayerUi, typeof(Cheats.SetOrbit), "OnSetOrbitClick", None));
            h.Add(new SpikeS9Hook("rendezvous", LayerUi, typeof(Cheats.SetOrbit), "OnRendezvousClick", None));
            h.Add(new SpikeS9Hook("set-position", LayerUi, typeof(Cheats.SetPosition), "OnSetPositionClick", None));
            h.Add(new SpikeS9Hook("post-orbit-set", LayerEffect, typeof(FlightGlobals), "PostOrbitSet", new[] { typeof(CelestialBody) }));

            // Comets and the object thrower.
            Type comets = typeof(Cheats.CheatsComets);
            h.Add(new SpikeS9Hook("comet-kill", LayerUi, comets, "OnKillClick", None));
            h.Add(new SpikeS9Hook("comet-random", LayerUi, comets, "OnRandomClick", None));
            h.Add(new SpikeS9Hook("comet-short", LayerUi, comets, "OnShortClick", None));
            h.Add(new SpikeS9Hook("comet-mid", LayerUi, comets, "OnMidClick", None));
            h.Add(new SpikeS9Hook("comet-long", LayerUi, comets, "OnLongClick", None));
            h.Add(new SpikeS9Hook("comet-interstellar", LayerUi, comets, "OnInterstellarClick", None));
            h.Add(new SpikeS9Hook("comet-sentinel", LayerUi, comets, "OnSentinelClick", None));
            h.Add(new SpikeS9Hook("comet-sentinel-short", LayerUi, comets, "OnSentinelShortClick", None));
            h.Add(new SpikeS9Hook("comet-sentinel-mid", LayerUi, comets, "OnSentinelMidClick", None));
            h.Add(new SpikeS9Hook("comet-sentinel-long", LayerUi, comets, "OnSentinelLongClick", None));
            h.Add(new SpikeS9Hook("comet-sentinel-interstellar", LayerUi, comets, "OnSentinelInterstellarClick", None));
            h.Add(new SpikeS9Hook("comet-colliders", LayerUi, comets, "OnCollidersClick", OneBool));
            h.Add(new SpikeS9Hook("object-thrower-arm", LayerUi, typeof(Cheats.CheatsObjectThrower), "OnArmedToggle", OneBool));

            // Other debug screens: contracts, kerbal create, physics database, heat.
            Type existing = typeof(ContractScreens.ScreenContractExistingItem);
            h.Add(new SpikeS9Hook("contract-complete-or-accept", LayerUi, existing, "OnLeftButtonClicked", None));
            h.Add(new SpikeS9Hook("contract-cancel-or-decline", LayerUi, existing, "OnRightButtonClicked", None));
            h.Add(new SpikeS9Hook("contract-generate", LayerUi, typeof(ContractScreens.ScreenContractNewItem), "GenerateContract",
                new[] { typeof(Type), typeof(Contracts.Contract.ContractPrestige) }));
            Type tools = typeof(ContractScreens.ScreenContractTools);
            h.Add(new SpikeS9Hook("contract-reset-weights", LayerUi, tools, "OnResetWeightsClicked", None));
            h.Add(new SpikeS9Hook("contract-regenerate", LayerUi, tools, "OnRegenerateCurrentClicked", None));
            h.Add(new SpikeS9Hook("contract-clear-current", LayerUi, tools, "OnClearCurrentClicked", None));
            h.Add(new SpikeS9Hook("contract-clear-finished", LayerUi, tools, "OnClearFinishedClicked", None));
            h.Add(new SpikeS9Hook("kerbal-create", LayerUi, typeof(GamePlayScreens.ScreenKerbalCreate), "OnSubmitClicked", None));
            h.Add(new SpikeS9Hook("physics-load", LayerUi, typeof(PhysicsScreens.ScreenPhysics), "OnLoadClicked", None));
            h.Add(new SpikeS9Hook("physics-save", LayerUi, typeof(PhysicsScreens.ScreenPhysics), "OnSaveClicked", None));
            h.Add(new SpikeS9Hook("heat-generation", LayerUi, typeof(ResourceScreens.ScreenResources), "OnHeatGenerationToggle", OneBool));

            // The debug console (Alt+F12 > Console).
            Type console = typeof(ConsoleCommands);
            h.Add(new SpikeS9Hook("console-funds", LayerConsole, console, "OnFundsCommand", OneString));
            h.Add(new SpikeS9Hook("console-science", LayerConsole, console, "OnScienceCommand", OneString));
            h.Add(new SpikeS9Hook("console-reputation", LayerConsole, console, "OnReputationCommand", OneString));
            h.Add(new SpikeS9Hook("console-rendezvous", LayerConsole, console, "OnRendezvousCommand", OneString));
            h.Add(new SpikeS9Hook("console-targetv", LayerConsole, console, "OnTargetvCommand", OneString));
            h.Add(new SpikeS9Hook("console-b4d455", LayerConsole, console, "OnBadassCommand", OneString));

            // The legacy cheat window (class DebugToolbar); whether any scene builds it is measured.
            Type legacy = typeof(DebugToolbar);
            h.Add(new SpikeS9Hook("legacy-science", LayerLegacy, legacy, "CheatInScience", new[] { typeof(float) }));
            h.Add(new SpikeS9Hook("legacy-funds", LayerLegacy, legacy, "CheatInFunds", new[] { typeof(double) }));
            h.Add(new SpikeS9Hook("legacy-reputation", LayerLegacy, legacy, "CheatInReputation", new[] { typeof(float) }));
            h.Add(new SpikeS9Hook("legacy-experience", LayerLegacy, legacy, "CheatInExperience", None));

            // Effect entry points (every caller of these is a debug path, but see the report:
            // CheatTechnology is also called by the Mission Builder editor).
            h.Add(new SpikeS9Hook("CheatTechnology", LayerEffect, typeof(ResearchAndDevelopment), "CheatTechnology", None));
            h.Add(new SpikeS9Hook("CheatAddScience", LayerEffect, typeof(ResearchAndDevelopment), "CheatAddScience", new[] { typeof(float) }));
            h.Add(new SpikeS9Hook("CheatFacilities", LayerEffect, typeof(ScenarioUpgradeableFacilities), "CheatFacilities", None));
            h.Add(new SpikeS9Hook("CheatExperience", LayerEffect, typeof(KerbalRoster), "CheatExperience", None));
            h.Add(new SpikeS9Hook("CheatProgression", LayerEffect, typeof(ProgressTracking), "CheatProgression", None));
            h.Add(new SpikeS9Hook("CheatEarlyProgression", LayerEffect, typeof(ProgressTracking), "CheatEarlyProgression", None));
            h.Add(new SpikeS9Hook("CheatComplete", LayerEffect, typeof(ProgressNode), "CheatComplete", None));
            h.Add(new SpikeS9Hook("CheatRepair", LayerEffect, typeof(ModuleDeployablePart), "CheatRepair", None));

            // Currency setters: logged only when the reason is TransactionReasons.Cheating.
            Type[] dReason = { typeof(double), typeof(TransactionReasons) };
            Type[] fReason = { typeof(float), typeof(TransactionReasons) };
            h.Add(new SpikeS9Hook("funds-add", LayerCurrency, typeof(Funding), "AddFunds", dReason));
            h.Add(new SpikeS9Hook("funds-set", LayerCurrency, typeof(Funding), "SetFunds", dReason));
            h.Add(new SpikeS9Hook("science-add", LayerCurrency, typeof(ResearchAndDevelopment), "AddScience", fReason));
            h.Add(new SpikeS9Hook("science-set", LayerCurrency, typeof(ResearchAndDevelopment), "SetScience", fReason));
            h.Add(new SpikeS9Hook("rep-add", LayerCurrency, typeof(Reputation), "AddReputation", fReason));
            h.Add(new SpikeS9Hook("rep-set", LayerCurrency, typeof(Reputation), "SetReputation", fReason));

            return h;
        }

        /// <summary>The argument index of the TransactionReasons parameter of a currency hook, or -1.</summary>
        internal static int ReasonArgIndex(SpikeS9Hook hook)
        {
            if (hook?.Args == null) return -1;
            for (int i = 0; i < hook.Args.Length; i++)
                if (hook.Args[i] == typeof(TransactionReasons)) return i;
            return -1;
        }
    }
}
