using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsek
{
    /// <summary>
    /// The core recalculation engine. Sorts all game actions by (UT, type, sequence),
    /// resets all registered modules, then walks actions forward from UT=0 dispatching
    /// each action to the appropriate tier of modules.
    ///
    /// Pure computation — no KSP state access, no file I/O.
    /// Modules are registered, not hardcoded — the engine does not know about specific module types.
    ///
    /// Tier ordering (design doc 1.8):
    ///   First-tier (independent): Science, Milestones, Contracts, Kerbals
    ///   Transform: Strategies (between tiers — future, placeholder for now)
    ///   Second-tier (dependent): Funds, Reputation
    ///   Parallel: Facilities
    /// </summary>
    internal static class RecalculationEngine
    {
        private static readonly List<IResourceModule> firstTierModules = new List<IResourceModule>();
        private static readonly List<IResourceModule> secondTierModules = new List<IResourceModule>();
        private static IResourceModule strategyTransform;
        private static IResourceModule facilitiesModule;

        // ================================================================
        // Module registration
        // ================================================================

        internal enum ModuleTier
        {
            FirstTier,
            SecondTier,
            Strategy,
            Facilities
        }

        /// <summary>
        /// Registers a resource module for the recalculation walk at the specified tier.
        /// Modules are dispatched in tier order: first-tier, strategy transform, second-tier, facilities.
        /// </summary>
        internal static void RegisterModule(IResourceModule module, ModuleTier tier)
        {
            if (module == null)
            {
                ParsekLog.Warn("RecalcEngine", "RegisterModule called with null module, skipping");
                return;
            }

            switch (tier)
            {
                case ModuleTier.FirstTier:
                    if (firstTierModules.Contains(module))
                    {
                        ParsekLog.Verbose("RecalcEngine",
                            $"First-tier module already registered: {module.GetType().Name}, skipping");
                        return;
                    }
                    firstTierModules.Add(module);
                    ParsekLog.Verbose("RecalcEngine",
                        $"Registered first-tier module: {module.GetType().Name}, total={firstTierModules.Count}");
                    break;

                case ModuleTier.SecondTier:
                    if (secondTierModules.Contains(module))
                    {
                        ParsekLog.Verbose("RecalcEngine",
                            $"Second-tier module already registered: {module.GetType().Name}, skipping");
                        return;
                    }
                    secondTierModules.Add(module);
                    ParsekLog.Verbose("RecalcEngine",
                        $"Registered second-tier module: {module.GetType().Name}, total={secondTierModules.Count}");
                    break;

                case ModuleTier.Strategy:
                    if (strategyTransform == module)
                    {
                        ParsekLog.Verbose("RecalcEngine",
                            $"Strategy transform module already registered: {module.GetType().Name}, skipping");
                        return;
                    }
                    strategyTransform = module;
                    ParsekLog.Verbose("RecalcEngine",
                        $"Registered strategy transform module: {module.GetType().Name}");
                    break;

                case ModuleTier.Facilities:
                    if (facilitiesModule == module)
                    {
                        ParsekLog.Verbose("RecalcEngine",
                            $"Facilities module already registered: {module.GetType().Name}, skipping");
                        return;
                    }
                    facilitiesModule = module;
                    ParsekLog.Verbose("RecalcEngine",
                        $"Registered facilities module: {module.GetType().Name}");
                    break;

                default:
                    ParsekLog.Warn("RecalcEngine", $"Unknown tier '{tier}' for module {module.GetType().Name}");
                    break;
            }
        }

        /// <summary>
        /// Clears all registered modules. Used for test isolation.
        /// </summary>
        internal static void ClearModules()
        {
            int total = firstTierModules.Count + secondTierModules.Count
                + (strategyTransform != null ? 1 : 0)
                + (facilitiesModule != null ? 1 : 0);

            firstTierModules.Clear();
            secondTierModules.Clear();
            strategyTransform = null;
            facilitiesModule = null;

            ParsekLog.Verbose("RecalcEngine", $"ClearModules: removed={total}");
        }

        // ================================================================
        // Core recalculation
        // ================================================================

        /// <summary>
        /// Runs the full recalculation walk from UT=0 forward:
        ///   1. Sort actions by (UT ascending, earnings before spendings, sequence)
        ///   2. Reset all modules
        ///   3. Walk sorted actions, dispatching each to all tiers
        ///   4. Log summary
        /// </summary>
        /// <param name="actions">All game actions to consider. The list is not mutated.</param>
        /// <param name="utCutoff">
        /// Optional UT cutoff. When non-null, only actions with <c>UT &lt;= utCutoff</c> are
        /// fed to the pre-pass and dispatch walk. Seed actions (FundsInitial / ScienceInitial /
        /// ReputationInitial) are always included regardless of cutoff — they are the baseline
        /// by definition. Used by the rewind path to prevent post-rewind re-credit of events
        /// that occurred after the rewind target. When null, no filtering is applied.
        /// </param>
        internal static void Recalculate(List<GameAction> actions, double? utCutoff = null)
        {
            if (actions == null)
            {
                ParsekLog.Warn("RecalcEngine", "Recalculate called with null actions list");
                return;
            }

            // UT-cutoff filter (Phase D): drop actions whose UT strictly exceeds the cutoff.
            // Seed actions always survive because they define the session baseline. When no
            // cutoff is supplied we reuse the caller's list directly to avoid an allocation.
            int filteredOut = 0;
            List<GameAction> effective;
            if (utCutoff.HasValue)
            {
                double cutoff = utCutoff.Value;
                effective = new List<GameAction>(actions.Count);
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a == null) continue;
                    if (IsSeedType(a.Type) || a.UT <= cutoff)
                        effective.Add(a);
                    else
                        filteredOut++;
                }
            }
            else
            {
                effective = actions;
            }

            // 1. Sort — stable sort preserving insertion order for equal keys
            var sorted = SortActions(effective);

            // 2. Reset all modules
            ResetAllModules();

            // 2b. Reset derived fields to defaults before walk.
            // These fields are written by modules during the walk and read by downstream
            // modules within the same walk (e.g., FundsModule reads Effective set by
            // MilestonesModule, ReputationModule reads TransformedRepReward set by
            // StrategiesModule). They are NOT read outside the recalculation walk —
            // no UI code or other consumers access them. This reset ensures idempotency:
            // calling Recalculate twice on the same action list produces identical results,
            // because stale derived values from a previous walk are cleared before the
            // new walk begins. Transformed reward fields are seeded from their immutable
            // source fields so the strategy transform can subtract from the correct base.
            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].Effective = true;
                sorted[i].EffectiveScience = 0f;
                sorted[i].Affordable = false;
                sorted[i].EffectiveRep = 0f;
                sorted[i].TransformedFundsReward = sorted[i].FundsReward;
                sorted[i].TransformedScienceReward = sorted[i].ScienceReward;
                sorted[i].TransformedRepReward = sorted[i].RepReward;
            }

            // 2c. Pre-pass: let modules compute aggregate data before the walk.
            // Modules may inject synthetic actions (e.g., ContractsModule injects
            // ContractFail for expired deadlines), so we re-sort afterward.
            PrePassAllModules(sorted);
            sorted = SortActions(sorted);

            // 3. Walk sorted actions
            int firstTierDispatches = 0;
            int strategyDispatches = 0;
            int secondTierDispatches = 0;
            int facilitiesDispatches = 0;

            for (int i = 0; i < sorted.Count; i++)
            {
                var action = sorted[i];

                // Dispatch to first-tier modules
                for (int m = 0; m < firstTierModules.Count; m++)
                {
                    firstTierModules[m].ProcessAction(action);
                    firstTierDispatches++;
                }

                // Strategy transform (between tiers — future)
                if (strategyTransform != null)
                {
                    strategyTransform.ProcessAction(action);
                    strategyDispatches++;
                }

                // Dispatch to second-tier modules
                for (int m = 0; m < secondTierModules.Count; m++)
                {
                    secondTierModules[m].ProcessAction(action);
                    secondTierDispatches++;
                }

                // Dispatch to facilities module
                if (facilitiesModule != null)
                {
                    facilitiesModule.ProcessAction(action);
                    facilitiesDispatches++;
                }
            }

            // 4. Post-walk: let modules finalize derived state
            PostWalkAllModules();

            // 5. Log summary
            string cutoffLabel = utCutoff.HasValue
                ? utCutoff.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : "null";
            ParsekLog.Info("RecalcEngine",
                $"Recalculate complete: actionsTotal={actions.Count}, " +
                $"actionsAfterCutoff={effective.Count}, cutoffUT={cutoffLabel}, " +
                $"filteredOut={filteredOut}, walkedSorted={sorted.Count}, " +
                $"firstTier={firstTierModules.Count} modules ({firstTierDispatches} dispatches), " +
                $"strategy={strategyDispatches} dispatches, " +
                $"secondTier={secondTierModules.Count} modules ({secondTierDispatches} dispatches), " +
                $"facilities={facilitiesDispatches} dispatches");
        }

        /// <summary>
        /// Returns true if the action type is a session-baseline seed (FundsInitial,
        /// ScienceInitial, ReputationInitial). Seeds are always included in recalculation
        /// regardless of any UT cutoff — they define the starting balance for the walk.
        /// </summary>
        internal static bool IsSeedType(GameActionType type)
        {
            return type == GameActionType.FundsInitial
                || type == GameActionType.ScienceInitial
                || type == GameActionType.ReputationInitial;
        }

        // ================================================================
        // Sort
        // ================================================================

        /// <summary>
        /// Sorts actions by the three-level key defined in design doc 1.7:
        ///   Primary: UT ascending
        ///   Secondary: earning types before spending types
        ///   Tertiary: sequence field (ascending)
        ///
        /// Uses LINQ OrderBy/ThenBy for a guaranteed stable sort.
        /// Returns a new sorted list — the input is not modified.
        /// </summary>
        internal static List<GameAction> SortActions(List<GameAction> actions)
        {
            if (actions == null || actions.Count == 0)
                return new List<GameAction>();

            return actions
                .OrderBy(a => a.UT)
                .ThenBy(a => IsEarningType(a.Type) ? 0 : 1)
                .ThenBy(a => a.Sequence)
                .ToList();
        }

        // ================================================================
        // Type classification
        // ================================================================

        /// <summary>
        /// Returns true if the action type represents an earning (credit).
        /// Earnings are processed before spendings at the same UT.
        /// </summary>
        internal static bool IsEarningType(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.ScienceEarning:
                case GameActionType.FundsEarning:
                case GameActionType.MilestoneAchievement:
                case GameActionType.ContractComplete:
                case GameActionType.ReputationEarning:
                case GameActionType.KerbalRescue:
                case GameActionType.FundsInitial:
                case GameActionType.ScienceInitial:
                case GameActionType.ReputationInitial:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns true if the action type represents a spending (debit).
        /// Spendings are processed after earnings at the same UT.
        /// </summary>
        internal static bool IsSpendingType(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.ScienceSpending:
                case GameActionType.FundsSpending:
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityRepair:
                case GameActionType.KerbalHire:
                case GameActionType.StrategyActivate:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    return true;
                default:
                    return false;
            }
        }

        // ================================================================
        // Internal helpers
        // ================================================================

        private static void PrePassAllModules(List<GameAction> sorted)
        {
            for (int i = 0; i < firstTierModules.Count; i++)
                firstTierModules[i].PrePass(sorted);

            if (strategyTransform != null)
                strategyTransform.PrePass(sorted);

            for (int i = 0; i < secondTierModules.Count; i++)
                secondTierModules[i].PrePass(sorted);

            if (facilitiesModule != null)
                facilitiesModule.PrePass(sorted);
        }

        private static void PostWalkAllModules()
        {
            for (int i = 0; i < firstTierModules.Count; i++)
                firstTierModules[i].PostWalk();

            if (strategyTransform != null)
                strategyTransform.PostWalk();

            for (int i = 0; i < secondTierModules.Count; i++)
                secondTierModules[i].PostWalk();

            if (facilitiesModule != null)
                facilitiesModule.PostWalk();
        }

        private static void ResetAllModules()
        {
            for (int i = 0; i < firstTierModules.Count; i++)
                firstTierModules[i].Reset();

            if (strategyTransform != null)
                strategyTransform.Reset();

            for (int i = 0; i < secondTierModules.Count; i++)
                secondTierModules[i].Reset();

            if (facilitiesModule != null)
                facilitiesModule.Reset();
        }
    }
}
