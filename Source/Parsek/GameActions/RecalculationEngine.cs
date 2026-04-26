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

            List<GameAction> projectedTimeline = null;
            if (utCutoff.HasValue)
            {
                // A cutoff walk must leave visible game state at the current UT, but funds
                // and science availability also need full-timeline cashflow to reserve
                // already-committed future spendings. This isolated shadow walk uses cloned
                // modules and suppressed logging to derive future Effective/Transformed/
                // EffectiveScience fields without dispatching future actions through the
                // registered live modules. Projection uses the latest explicit action UT or
                // contract deadline so deadline-only future penalties are synthesized too.
                projectedTimeline = RunProjectionWalk(
                    CopyNonNullActions(actions),
                    ComputeProjectionHorizon(actions)).Sorted;
            }

            WalkResult walk = RunWalk(effective, utCutoff);

            if (utCutoff.HasValue)
                ApplyProjectedAvailability(projectedTimeline, utCutoff.Value);

            // 4. Log summary
            string cutoffLabel = utCutoff.HasValue
                ? utCutoff.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : "null";
            string stateKey = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "total={0}|after={1}|cutoff={2}|filtered={3}|sorted={4}|first={5}:{6}|strategy={7}|second={8}:{9}|facilities={10}",
                actions.Count,
                effective.Count,
                cutoffLabel,
                filteredOut,
                walk.Sorted.Count,
                firstTierModules.Count,
                walk.FirstTierDispatches,
                walk.StrategyDispatches,
                secondTierModules.Count,
                walk.SecondTierDispatches,
                walk.FacilitiesDispatches);
            string message =
                $"Recalculate complete: actionsTotal={actions.Count}, " +
                $"actionsAfterCutoff={effective.Count}, cutoffUT={cutoffLabel}, " +
                $"filteredOut={filteredOut}, walkedSorted={walk.Sorted.Count}, " +
                $"firstTier={firstTierModules.Count} modules ({walk.FirstTierDispatches} dispatches), " +
                $"strategy={walk.StrategyDispatches} dispatches, " +
                $"secondTier={secondTierModules.Count} modules ({walk.SecondTierDispatches} dispatches), " +
                $"facilities={walk.FacilitiesDispatches} dispatches";
            if (utCutoff.HasValue)
            {
                ParsekLog.Verbose("RecalcEngine", message);
            }
            else
            {
                ParsekLog.VerboseOnChange("RecalcEngine",
                    "recalculate-summary",
                    stateKey,
                    message);
            }
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

        private sealed class WalkResult
        {
            internal List<GameAction> Sorted;
            internal int FirstTierDispatches;
            internal int StrategyDispatches;
            internal int SecondTierDispatches;
            internal int FacilitiesDispatches;
        }

        private struct ProjectionResult
        {
            internal double Available;
            internal double MinBalance;
            internal double FinalBalance;
            internal int FutureActions;
            internal int DeltaActions;
        }

        private static WalkResult RunWalk(List<GameAction> actions, double? walkNowUT)
        {
            return RunWalk(
                actions,
                walkNowUT,
                firstTierModules,
                strategyTransform,
                secondTierModules,
                facilitiesModule);
        }

        private static WalkResult RunProjectionWalk(List<GameAction> actions, double projectionNowUT)
        {
            var projectionFirstTier = CreateProjectionModules(firstTierModules);
            var projectionStrategy = CreateProjectionModule(strategyTransform);
            var projectionSecondTier = CreateProjectionModules(secondTierModules);
            var projectionFacilities = CreateProjectionModule(facilitiesModule);

            using (ParsekLog.SuppressScope())
            {
                return RunWalk(
                    actions,
                    projectionNowUT,
                    projectionFirstTier,
                    projectionStrategy,
                    projectionSecondTier,
                    projectionFacilities);
            }
        }

        private static WalkResult RunWalk(
            List<GameAction> actions,
            double? walkNowUT,
            List<IResourceModule> firstTier,
            IResourceModule strategy,
            List<IResourceModule> secondTier,
            IResourceModule facilities)
        {
            // 1. Sort — stable sort preserving insertion order for equal keys
            var sorted = SortActions(actions);

            // 2. Reset all modules and action-derived fields.
            ResetAllModules(firstTier, strategy, secondTier, facilities);
            ResetDerivedFields(sorted);

            // 2b. Pre-pass: let modules compute aggregate data before the walk.
            // Modules may inject synthetic actions (e.g., ContractsModule injects
            // ContractFail for expired deadlines), so we re-sort afterward.
            //
            // Pass the cutoff as the walk's effective "now" so ContractsModule can
            // detect deadlines that expired between the last pre-cutoff action and
            // the cutoff itself. Without this, a filtered action list could have its
            // "last UT" land before a deadline, letting deadline-expired contracts
            // slip through the rewind without the synthetic ContractFail.
            PrePassAllModules(sorted, walkNowUT, firstTier, strategy, secondTier, facilities);
            sorted = SortActions(sorted);

            // 3. Walk sorted actions.
            int firstTierDispatches = 0;
            int strategyDispatches = 0;
            int secondTierDispatches = 0;
            int facilitiesDispatches = 0;

            for (int i = 0; i < sorted.Count; i++)
            {
                var action = sorted[i];

                // Dispatch to first-tier modules.
                for (int m = 0; m < firstTier.Count; m++)
                {
                    firstTier[m].ProcessAction(action);
                    firstTierDispatches++;
                }

                // Strategy transform tier.
                if (strategy != null)
                {
                    strategy.ProcessAction(action);
                    strategyDispatches++;
                }

                // Dispatch to second-tier modules.
                for (int m = 0; m < secondTier.Count; m++)
                {
                    secondTier[m].ProcessAction(action);
                    secondTierDispatches++;
                }

                // Dispatch to facilities module.
                if (facilities != null)
                {
                    facilities.ProcessAction(action);
                    facilitiesDispatches++;
                }
            }

            // 4. Post-walk: let modules finalize derived state.
            PostWalkAllModules(firstTier, strategy, secondTier, facilities);

            return new WalkResult
            {
                Sorted = sorted,
                FirstTierDispatches = firstTierDispatches,
                StrategyDispatches = strategyDispatches,
                SecondTierDispatches = secondTierDispatches,
                FacilitiesDispatches = facilitiesDispatches
            };
        }

        private static void ResetDerivedFields(List<GameAction> sorted)
        {
            // These fields are written by modules during the walk and read by downstream
            // modules within the same walk (e.g., FundsModule reads Effective set by
            // MilestonesModule, ReputationModule reads TransformedRepReward set by
            // StrategiesModule). This reset ensures idempotency: calling Recalculate
            // twice on the same action list produces identical results, with no stale
            // Effective=false / EffectiveScience values surviving after a new walk
            // begins. Transformed reward fields are seeded from their immutable source
            // fields so the strategy transform can subtract from the correct base.
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
        }

        private static List<GameAction> CopyNonNullActions(List<GameAction> actions)
        {
            var copy = new List<GameAction>(actions.Count);
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i] != null)
                    copy.Add(actions[i]);
            }
            return copy;
        }

        private static double ComputeProjectionHorizon(List<GameAction> actions)
        {
            double horizon = 0.0;
            bool hasValue = false;

            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null)
                    continue;

                if (!hasValue || action.UT > horizon)
                {
                    horizon = action.UT;
                    hasValue = true;
                }

                if (action.Type != GameActionType.ContractAccept || float.IsNaN(action.DeadlineUT))
                    continue;

                if (action.DeadlineUT > horizon)
                    horizon = action.DeadlineUT;
            }

            return horizon;
        }

        private static List<IResourceModule> CreateProjectionModules(List<IResourceModule> modules)
        {
            var clones = new List<IResourceModule>(modules.Count);
            for (int i = 0; i < modules.Count; i++)
            {
                var clone = CreateProjectionModule(modules[i]);
                if (clone != null)
                    clones.Add(clone);
            }
            return clones;
        }

        private static IResourceModule CreateProjectionModule(IResourceModule module)
        {
            if (module == null)
                return null;

            try
            {
                var cloneable = module as IProjectionCloneableModule;
                if (cloneable != null)
                {
                    var clone = cloneable.CreateProjectionClone();
                    if (clone != null)
                        return clone;
                }

                return Activator.CreateInstance(module.GetType(), true) as IResourceModule;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecalcEngine",
                    $"Projection clone failed for module {module.GetType().Name}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static void ApplyProjectedAvailability(List<GameAction> projectedTimeline, double cutoff)
        {
            if (projectedTimeline == null)
                return;

            ApplyProjectedAvailability(firstTierModules, projectedTimeline, cutoff);

            if (strategyTransform != null)
                ApplyProjectedAvailability(strategyTransform, projectedTimeline, cutoff);

            ApplyProjectedAvailability(secondTierModules, projectedTimeline, cutoff);

            if (facilitiesModule != null)
                ApplyProjectedAvailability(facilitiesModule, projectedTimeline, cutoff);
        }

        private static void ApplyProjectedAvailability(
            List<IResourceModule> modules,
            List<GameAction> projectedTimeline,
            double cutoff)
        {
            for (int i = 0; i < modules.Count; i++)
                ApplyProjectedAvailability(modules[i], projectedTimeline, cutoff);
        }

        private static void ApplyProjectedAvailability(
            IResourceModule module,
            List<GameAction> projectedTimeline,
            double cutoff)
        {
            var projectionModule = module as ICashflowProjectionModule;
            if (projectionModule == null)
                return;

            double currentBalance = projectionModule.GetProjectionCurrentBalance();
            var projection = ProjectAvailability(
                projectedTimeline,
                cutoff,
                currentBalance,
                projectionModule);
            projectionModule.SetProjectedAvailable(
                projection.Available,
                currentBalance,
                projection.MinBalance,
                projection.FinalBalance,
                projection.FutureActions,
                projection.DeltaActions);
        }

        private static ProjectionResult ProjectAvailability(
            List<GameAction> projectedTimeline,
            double cutoff,
            double currentBalance,
            ICashflowProjectionModule projectionModule)
        {
            double projected = currentBalance;
            double minProjected = currentBalance;
            int futureActions = 0;
            int deltaActions = 0;

            for (int i = 0; i < projectedTimeline.Count; i++)
            {
                var action = projectedTimeline[i];
                if (action == null || IsSeedType(action.Type) || action.UT <= cutoff)
                    continue;

                futureActions++;

                double delta;
                if (!projectionModule.TryGetProjectionDelta(action, out delta))
                    continue;

                projected += delta;
                deltaActions++;

                if (projected < minProjected)
                    minProjected = projected;
            }

            return new ProjectionResult
            {
                Available = minProjected > 0.0 ? minProjected : 0.0,
                MinBalance = minProjected,
                FinalBalance = projected,
                FutureActions = futureActions,
                DeltaActions = deltaActions
            };
        }

        private static void PrePassAllModules(
            List<GameAction> sorted,
            double? walkNowUT,
            List<IResourceModule> firstTier,
            IResourceModule strategy,
            List<IResourceModule> secondTier,
            IResourceModule facilities)
        {
            for (int i = 0; i < firstTier.Count; i++)
                firstTier[i].PrePass(sorted, walkNowUT);

            if (strategy != null)
                strategy.PrePass(sorted, walkNowUT);

            for (int i = 0; i < secondTier.Count; i++)
                secondTier[i].PrePass(sorted, walkNowUT);

            if (facilities != null)
                facilities.PrePass(sorted, walkNowUT);
        }

        private static void PostWalkAllModules(
            List<IResourceModule> firstTier,
            IResourceModule strategy,
            List<IResourceModule> secondTier,
            IResourceModule facilities)
        {
            for (int i = 0; i < firstTier.Count; i++)
                firstTier[i].PostWalk();

            if (strategy != null)
                strategy.PostWalk();

            for (int i = 0; i < secondTier.Count; i++)
                secondTier[i].PostWalk();

            if (facilities != null)
                facilities.PostWalk();
        }

        private static void ResetAllModules(
            List<IResourceModule> firstTier,
            IResourceModule strategy,
            List<IResourceModule> secondTier,
            IResourceModule facilities)
        {
            for (int i = 0; i < firstTier.Count; i++)
                firstTier[i].Reset();

            if (strategy != null)
                strategy.Reset();

            for (int i = 0; i < secondTier.Count; i++)
                secondTier[i].Reset();

            if (facilities != null)
                facilities.Reset();
        }
    }
}
