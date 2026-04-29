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
    internal class StrategiesModule : IResourceModule, IProjectionCloneableModule
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
        public bool PrePass(List<GameAction> actions, double? walkNowUT = null)
        {
            // No pre-pass needed for strategies; walkNowUT is ignored.
            return false;
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
        /// #439 Phase A: documented identity no-op.
        ///
        /// Stock KSP strategies (Strategies.Effects.CurrencyConverter /
        /// CurrencyOperation) subscribe <c>GameEvents.Modifiers.OnCurrencyModifierQuery</c>
        /// and mutate the pending currency query BEFORE KSP's Funding /
        /// ReputationHandler / ResearchAndDevelopment code fires the final
        /// <c>FundsChanged</c> / <c>ReputationChanged</c> / <c>ScienceChanged</c>
        /// event. Our <see cref="GameStateRecorder.OnContractCompleted"/> captures
        /// <c>contract.FundsCompletion</c> / <c>RepCompletion</c> / <c>ScienceCompletion</c>
        /// which ARE the post-transform values — applying Commitment a second time here
        /// would double-divert.
        ///
        /// The method is retained (not deleted) so the action walk still dispatches
        /// ContractComplete through StrategiesModule for future extension (e.g. for
        /// mod strategies that bypass the modifier-query path, or for a UI overlay that
        /// needs to display "X% diverted" attribution). Phase A emits one VERBOSE line
        /// per call so the no-op is observable in logs.
        ///
        /// See <c>docs/dev/done/plans/fix-439-strategy-lifecycle-capture.md</c> section 3.5
        /// (option B) for the full rationale and option C deferral.
        /// </summary>
        internal void TransformContractReward(GameAction action)
        {
            if (action.Type != GameActionType.ContractComplete)
                return;

            ParsekLog.Verbose(Tag,
                $"StrategiesModule.TransformContractReward: identity no-op (KSP " +
                $"CurrencyModifierQuery already transformed contract reward pre-event) " +
                $"contractId='{action.ContractId ?? "(none)"}' " +
                $"activeStrategies={activeStrategies.Count}");
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

        public IResourceModule CreateProjectionClone()
        {
            var clone = new StrategiesModule();
            clone.maxSlots = maxSlots;
            return clone;
        }

        public void PostWalk() { }
    }
}
