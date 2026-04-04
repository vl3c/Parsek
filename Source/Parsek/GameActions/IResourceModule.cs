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
        /// Called once at the start of each <see cref="RecalculationEngine.Recalculate"/> invocation.
        /// </summary>
        void Reset();

        /// <summary>
        /// Pre-pass over the full sorted action list before the walk begins.
        /// Modules that need aggregate information (e.g. total committed spendings
        /// for the reservation system) compute it here. Called after Reset and
        /// before the first ProcessAction dispatch.
        /// </summary>
        void PrePass(List<GameAction> actions);

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
}
