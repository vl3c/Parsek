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
        /// Actions are dispatched in sorted order (UT ascending, earnings before spendings, sequence).
        /// </summary>
        void ProcessAction(GameAction action);
    }
}
