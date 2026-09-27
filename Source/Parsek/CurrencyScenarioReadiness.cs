using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>The three stock currency ScenarioModules, as a set.</summary>
    [Flags]
    internal enum CurrencySingletons
    {
        None = 0,
        Funding = 1,
        ResearchAndDevelopment = 2,
        Reputation = 4,
        All = Funding | ResearchAndDevelopment | Reputation
    }

    /// <summary>
    /// Which stock currency singletons a game mode creates, and whether the ones present
    /// have finished loading their save values.
    ///
    /// <para>
    /// <b>Which exist.</b> Decompiled KSP 1.12.5 <c>[KSPScenario]</c> creation options:
    /// <c>Funding</c> = 1120 (career + new mission games), <c>Reputation</c> = 96 (career
    /// only), <c>ResearchAndDevelopment</c> = 3112 (career + science sandbox + mission
    /// games). So CAREER has all three, SCIENCE_SANDBOX only R&amp;D, SANDBOX none. A wait
    /// keyed on the full set therefore always times out in Science and Sandbox.
    /// </para>
    ///
    /// <para>
    /// <b>When loaded.</b> The positive signal is the game's own scenario bookkeeping:
    /// <c>ProtoScenarioModule.Load</c> calls <c>ScenarioRunner.AddModule(ConfigNode)</c>,
    /// which constructs the module AND runs its <c>Load(node)</c> (OnLoad), and only then
    /// assigns <c>moduleRef</c>. A proto in <c>HighLogic.CurrentGame.scenarios</c> whose
    /// <c>moduleRef</c> is the live singleton therefore proves that singleton's OnLoad ran,
    /// independent of the VALUE it loaded - which is what lets a genuinely zero pool (a
    /// StartingFunds = 0 career, a Science game at 0 science) be trusted instead of waited
    /// out.
    /// </para>
    /// </summary>
    internal static class CurrencyScenarioReadiness
    {
        /// <summary>
        /// Pure: the currency singletons stock creates for <paramref name="mode"/>. An
        /// unknown mode (null, the SCENARIO family) keeps the legacy full set so its wait
        /// stays exactly as bounded as before.
        /// </summary>
        internal static CurrencySingletons ExpectedFor(Game.Modes? mode)
        {
            if (!mode.HasValue)
                return CurrencySingletons.All;

            switch (mode.Value)
            {
                case Game.Modes.CAREER:
                    return CurrencySingletons.All;
                case Game.Modes.SCIENCE_SANDBOX:
                    return CurrencySingletons.ResearchAndDevelopment;
                case Game.Modes.SANDBOX:
                    return CurrencySingletons.None;
                case Game.Modes.MISSION:
                case Game.Modes.MISSION_BUILDER:
                    return CurrencySingletons.Funding | CurrencySingletons.ResearchAndDevelopment;
                default:
                    return CurrencySingletons.All;
            }
        }

        /// <summary>Pure: true when every expected singleton is in <paramref name="satisfied"/>.</summary>
        internal static bool Covers(CurrencySingletons expected, CurrencySingletons satisfied)
        {
            return (expected & ~satisfied) == CurrencySingletons.None;
        }

        /// <summary>
        /// Pure core of <see cref="IsScenarioModuleLoaded"/>. <paramref name="protoNamesModule"/>
        /// false (no proto carries this class at all) or no proto list falls back to presence:
        /// the load is synchronous, so presence was the pre-existing readiness signal.
        /// </summary>
        internal static bool DecideModuleLoaded(
            bool present,
            bool protoListAvailable,
            bool protoNamesModule,
            bool protoRefIsModule)
        {
            if (!present)
                return false;
            if (!protoListAvailable || !protoNamesModule)
                return true;
            return protoRefIsModule;
        }

        /// <summary>Test seam: replaces the live expected / present / loaded reads.</summary>
        internal static Func<Game.Modes?> ModeProviderForTesting;
        internal static Func<CurrencySingletons> PresentProviderForTesting;
        internal static Func<CurrencySingletons> LoadedProviderForTesting;

        internal static void ResetForTesting()
        {
            ModeProviderForTesting = null;
            PresentProviderForTesting = null;
            LoadedProviderForTesting = null;
        }

        /// <summary>The current game's mode, or null with no live game.</summary>
        internal static Game.Modes? CurrentMode()
        {
            var provider = ModeProviderForTesting;
            if (provider != null)
                return provider();
            var game = HighLogic.CurrentGame;
            return game != null ? game.Mode : (Game.Modes?)null;
        }

        /// <summary>The currency singletons the current game mode creates.</summary>
        internal static CurrencySingletons ExpectedForCurrentGame()
        {
            return ExpectedFor(CurrentMode());
        }

        /// <summary>The currency singletons that exist right now.</summary>
        internal static CurrencySingletons PresentNow()
        {
            var provider = PresentProviderForTesting;
            if (provider != null)
                return provider();

            var result = CurrencySingletons.None;
            if (Funding.Instance != null) result |= CurrencySingletons.Funding;
            if (ResearchAndDevelopment.Instance != null) result |= CurrencySingletons.ResearchAndDevelopment;
            if (Reputation.Instance != null) result |= CurrencySingletons.Reputation;
            return result;
        }

        /// <summary>The currency singletons that exist AND have finished their OnLoad.</summary>
        internal static CurrencySingletons LoadedNow()
        {
            var provider = LoadedProviderForTesting;
            if (provider != null)
                return provider();

            var result = CurrencySingletons.None;
            if (IsScenarioModuleLoaded(Funding.Instance)) result |= CurrencySingletons.Funding;
            if (IsScenarioModuleLoaded(ResearchAndDevelopment.Instance)) result |= CurrencySingletons.ResearchAndDevelopment;
            if (IsScenarioModuleLoaded(Reputation.Instance)) result |= CurrencySingletons.Reputation;
            return result;
        }

        /// <summary>True once every singleton the current mode creates exists.</summary>
        internal static bool AllExpectedPresent()
        {
            return Covers(ExpectedForCurrentGame(), PresentNow());
        }

        /// <summary>True once every singleton that exists has finished its OnLoad.</summary>
        internal static bool AllPresentLoaded()
        {
            return Covers(PresentNow(), LoadedNow());
        }

        /// <summary>
        /// Live: true when <paramref name="module"/> exists and the game's scenario list
        /// holds a proto whose <c>moduleRef</c> is this very instance (set by stock only
        /// after the module's OnLoad returned). See the class summary.
        /// </summary>
        internal static bool IsScenarioModuleLoaded(ScenarioModule module)
        {
            if (module == null)
                return false;

            var game = HighLogic.CurrentGame;
            List<ProtoScenarioModule> protos = game != null ? game.scenarios : null;
            bool protoListAvailable = protos != null;
            bool protoNamesModule = false;
            bool protoRefIsModule = false;
            if (protoListAvailable)
            {
                string className = module.GetType().Name;
                for (int i = 0; i < protos.Count; i++)
                {
                    var proto = protos[i];
                    if (proto == null || !string.Equals(proto.moduleName, className, StringComparison.Ordinal))
                        continue;
                    protoNamesModule = true;
                    if (ReferenceEquals(proto.moduleRef, module))
                    {
                        protoRefIsModule = true;
                        break;
                    }
                }
            }

            return DecideModuleLoaded(true, protoListAvailable, protoNamesModule, protoRefIsModule);
        }

        /// <summary>Invariant rendering for logs and tests.</summary>
        internal static string Format(CurrencySingletons set)
        {
            if (set == CurrencySingletons.None)
                return "none";
            var parts = new List<string>(3);
            if ((set & CurrencySingletons.Funding) != 0) parts.Add("Funding");
            if ((set & CurrencySingletons.ResearchAndDevelopment) != 0) parts.Add("R&D");
            if ((set & CurrencySingletons.Reputation) != 0) parts.Add("Reputation");
            return string.Join("+", parts.ToArray());
        }
    }
}
