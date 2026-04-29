using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Interface for resource modules that participate in the recalculation walk.
    /// Each module processes game actions relevant to its resource type and maintains
    /// derived state that is recomputed from scratch on every recalculation.
    /// </summary>
    internal interface IResourceModule
    {
        /// <summary>
        /// Resets all derived state to zero/default before a recalculation walk.
        /// Called once on registered live modules during each visible
        /// <see cref="RecalculationEngine.Recalculate"/> walk. Isolated projection clones
        /// may also be reset while computing future cashflow reservations.
        /// </summary>
        void Reset();

        /// <summary>
        /// Pre-pass over the full sorted action list before the walk begins.
        /// Modules that need aggregate information (e.g. total committed spendings
        /// for the reservation system) compute it here. Called after Reset and
        /// before the first ProcessAction dispatch. Return <c>true</c> only when
        /// the action list was mutated and must be sorted again.
        /// </summary>
        /// <param name="actions">The action list (already UT-cutoff-filtered by the engine).</param>
        /// <param name="walkNowUT">
        /// The effective "now" of the walk used for deadline-style comparisons.
        /// When the engine was called with a UT cutoff (rewind path), this is the
        /// cutoff value — "now" is the rewind target, not the last surviving action's UT.
        /// When there is no cutoff, this is <c>null</c> and modules that care about
        /// "now" fall back to their existing heuristic (typically the last action's UT).
        /// Added in Phase D round 2 (#436) so <see cref="ContractsModule.PrePass"/> can
        /// correctly detect deadlines that expired between the last pre-cutoff action
        /// and the cutoff UT itself — otherwise deadline-expired contracts would leak
        /// past a rewind without firing their synthetic <c>ContractFail</c>.
        /// </param>
        bool PrePass(List<GameAction> actions, double? walkNowUT = null);

        /// <summary>
        /// Processes a single game action during the recalculation walk.
        /// The module should update its derived state based on the action's type and fields.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Dispatch order contract (guaranteed by <see cref="RecalculationEngine"/>):</b>
        /// Actions are dispatched in sorted order with a three-level key:
        ///   1. UT ascending (primary)
        ///   2. Earnings before spendings at the same UT (secondary)
        ///   3. Sequence number ascending (tertiary, preserves insertion order within a batch)
        /// </para>
        /// <para>
        /// This ordering ensures that earnings are always applied before spendings at the
        /// same UT, so affordability checks see the correct balance. Within each category,
        /// the sequence number preserves the original event order from
        /// <see cref="GameStateEventConverter.ConvertEvents"/>.
        /// </para>
        /// </remarks>
        void ProcessAction(GameAction action);

        /// <summary>
        /// Called once after all actions have been dispatched via ProcessAction.
        /// Modules that need cross-action post-processing (e.g. building derived
        /// structures from the complete accumulated state) implement their logic here.
        /// Most modules leave this as a no-op.
        /// </summary>
        void PostWalk();
    }

    /// <summary>
    /// Optional extension for modules that need to carry static configuration into
    /// the isolated projection walk.
    /// </summary>
    internal interface IProjectionCloneableModule
    {
        IResourceModule CreateProjectionClone();
    }

    /// <summary>
    /// Optional extension for modules whose top-bar availability must reserve future
    /// committed cashflow after a UT-cutoff walk.
    /// </summary>
    internal interface ICashflowProjectionModule
    {
        /// <summary>Returns the current running balance after the visible cutoff walk.</summary>
        double GetProjectionCurrentBalance();

        /// <summary>
        /// Returns the resource delta for a future action after a full shadow walk has
        /// populated derived action fields.
        /// </summary>
        bool TryGetProjectionDelta(GameAction action, out double delta);

        /// <summary>Installs the projected spendable amount for subsequent query/patch calls.</summary>
        void SetProjectedAvailable(
            double available,
            double currentBalance,
            double minProjectedBalance,
            double finalProjectedBalance,
            int futureActions,
            int deltaActions);
    }
}
