using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Strategy module — tracks active strategies and transforms contract rewards.
    /// Strategies divert a percentage of one contract reward resource into another.
    ///
    /// Registered as the Strategy tier in <see cref="RecalculationEngine"/> — dispatched
    /// between first-tier modules (Science, Milestones, Contracts, Kerbals) and
    /// second-tier modules (Funds, Reputation).
    ///
    /// UT=0 reservation: once activated anywhere on the timeline, a strategy
    /// consumes its Administration slot from UT=0 until deactivated.
    /// <see cref="GetActiveStrategyCount"/> returns the count of all strategies
    /// that have been activated and not yet deactivated, regardless of current UT.
    ///
    /// Pure computation — no KSP state access.
    /// Design doc: section 11 (Strategies Module).
    /// </summary>
    internal class StrategiesModule : IResourceModule
    {
        private const string Tag = "Strategies";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Active strategies on the timeline. Key = strategyId, Value = activation state.
        /// A strategy is "active" from its StrategyActivate action until a matching
        /// StrategyDeactivate action. All active strategies are reserved from UT=0.
        /// </summary>
        private readonly Dictionary<string, StrategyState> activeStrategies
            = new Dictionary<string, StrategyState>();

        /// <summary>
        /// Administration building slot limit. Determined by building level:
        /// 1 at level 1, 2 at level 2, 3 at level 3. Default 1.
        /// </summary>
        private int maxSlots = 1;

        // ================================================================
        // Internal state type
        // ================================================================

        internal class StrategyState
        {
            public string StrategyId;
            public StrategyResource SourceResource;
            public StrategyResource TargetResource;
            public float Commitment;
            public double ActivateUT;
        }

        // ================================================================
        // IResourceModule
        // ================================================================

        /// <inheritdoc/>
        public void Reset()
        {
            int previousCount = activeStrategies.Count;
            activeStrategies.Clear();
            ParsekLog.Verbose(Tag, $"Reset: cleared {previousCount} active strategies");
        }

        /// <inheritdoc/>
        public void PrePass(List<GameAction> actions)
        {
            // No pre-pass needed for strategies
        }

        /// <summary>
        /// Processes a single game action. Handles StrategyActivate and StrategyDeactivate.
        /// For ContractComplete actions with Effective=true, applies the strategy transform
        /// to divert source resource rewards into the target resource.
        /// All other action types are ignored.
        /// </summary>
        public void ProcessAction(GameAction action)
        {
            switch (action.Type)
            {
                case GameActionType.StrategyActivate:
                    ProcessActivate(action);
                    break;
                case GameActionType.StrategyDeactivate:
                    ProcessDeactivate(action);
                    break;
                case GameActionType.ContractComplete:
                    TransformContractReward(action);
                    break;
            }
        }

        // ================================================================
        // Strategy lifecycle
        // ================================================================

        private void ProcessActivate(GameAction action)
        {
            string id = action.StrategyId ?? "";

            if (activeStrategies.ContainsKey(id))
            {
                ParsekLog.Warn(Tag,
                    $"Activate: strategyId='{id}' already active, overwriting previous activation");
            }

            var state = new StrategyState
            {
                StrategyId = id,
                SourceResource = action.SourceResource,
                TargetResource = action.TargetResource,
                Commitment = action.Commitment,
                ActivateUT = action.UT
            };

            activeStrategies[id] = state;

            ParsekLog.Info(Tag,
                $"Activate: strategyId='{id}' source={action.SourceResource} " +
                $"target={action.TargetResource} commitment={action.Commitment.ToString("F2", IC)} " +
                $"setupCost={action.SetupCost.ToString("F1", IC)} " +
                $"ut={action.UT.ToString("F1", IC)} activeCount={activeStrategies.Count}/{maxSlots}");
        }

        private void ProcessDeactivate(GameAction action)
        {
            string id = action.StrategyId ?? "";

            if (!activeStrategies.ContainsKey(id))
            {
                ParsekLog.Warn(Tag,
                    $"Deactivate: strategyId='{id}' not currently active, ignoring");
                return;
            }

            activeStrategies.Remove(id);

            ParsekLog.Info(Tag,
                $"Deactivate: strategyId='{id}' ut={action.UT.ToString("F1", IC)} " +
                $"activeCount={activeStrategies.Count}/{maxSlots}");
        }

        // ================================================================
        // Contract reward transform
        // ================================================================

        /// <summary>
        /// Transforms contract reward fields on a ContractComplete action based on
        /// active strategy commitments. Only transforms effective contract completions.
        ///
        /// For each active strategy whose source resource matches a reward field on
        /// the contract, diverts commitment% of that reward into the target resource:
        ///   diverted = rewardAmount * commitment
        ///   sourceReward -= diverted
        ///   targetReward += diverted * conversionRate
        ///
        /// conversionRate is 1.0 for Phase 4 (deferred item D2).
        /// Modifies the action in place (derived fields, not persisted).
        /// </summary>
        internal void TransformContractReward(GameAction action)
        {
            if (action.Type != GameActionType.ContractComplete)
                return;

            if (!action.Effective)
                return;

            if (activeStrategies.Count == 0)
                return;

            const float conversionRate = 1.0f;

            foreach (var kvp in activeStrategies)
            {
                var strategy = kvp.Value;

                // Only transform if the contract UT is within the strategy's active window.
                // The strategy is active from ActivateUT onward (until a Deactivate removes it).
                if (action.UT < strategy.ActivateUT)
                    continue;

                float diverted;
                switch (strategy.SourceResource)
                {
                    case StrategyResource.Funds:
                        diverted = action.TransformedFundsReward * strategy.Commitment;
                        action.TransformedFundsReward -= diverted;
                        AddToTargetResource(action, strategy.TargetResource, diverted * conversionRate);
                        LogTransform(strategy, action, "FundsReward", diverted, conversionRate);
                        break;

                    case StrategyResource.Science:
                        diverted = action.TransformedScienceReward * strategy.Commitment;
                        action.TransformedScienceReward -= diverted;
                        AddToTargetResource(action, strategy.TargetResource, diverted * conversionRate);
                        LogTransform(strategy, action, "ScienceReward", diverted, conversionRate);
                        break;

                    case StrategyResource.Reputation:
                        diverted = action.TransformedRepReward * strategy.Commitment;
                        action.TransformedRepReward -= diverted;
                        AddToTargetResource(action, strategy.TargetResource, diverted * conversionRate);
                        LogTransform(strategy, action, "RepReward", diverted, conversionRate);
                        break;
                }
            }
        }

        private static void AddToTargetResource(GameAction action, StrategyResource target, float amount)
        {
            switch (target)
            {
                case StrategyResource.Funds:
                    action.TransformedFundsReward += amount;
                    break;
                case StrategyResource.Science:
                    action.TransformedScienceReward += amount;
                    break;
                case StrategyResource.Reputation:
                    action.TransformedRepReward += amount;
                    break;
            }
        }

        private static void LogTransform(
            StrategyState strategy, GameAction action,
            string sourceField, float diverted, float conversionRate)
        {
            ParsekLog.Verbose(Tag,
                $"Transform: strategy='{strategy.StrategyId}' contractId='{action.ContractId}' " +
                $"source={sourceField} diverted={diverted.ToString("F2", IC)} " +
                $"target={strategy.TargetResource} added={( diverted * conversionRate ).ToString("F2", IC)} " +
                $"ut={action.UT.ToString("F1", IC)}");
        }

        // ================================================================
        // Query methods
        // ================================================================

        /// <summary>
        /// Returns the number of strategies currently active on the timeline.
        /// Active means activated and not yet deactivated — reserved from UT=0.
        /// </summary>
        internal int GetActiveStrategyCount()
        {
            return activeStrategies.Count;
        }

        /// <summary>
        /// Returns the number of available Administration building slots.
        /// </summary>
        internal int GetAvailableSlots()
        {
            return maxSlots - activeStrategies.Count;
        }

        /// <summary>
        /// Returns whether the given strategy is currently active on the timeline.
        /// </summary>
        internal bool IsStrategyActive(string strategyId)
        {
            return activeStrategies.ContainsKey(strategyId);
        }

        /// <summary>
        /// Sets the maximum number of strategy slots. For testing and facility level updates.
        /// </summary>
        internal void SetMaxSlots(int slots)
        {
            maxSlots = slots;
            ParsekLog.Verbose(Tag, $"SetMaxSlots: maxSlots={maxSlots}");
        }
    }
}
