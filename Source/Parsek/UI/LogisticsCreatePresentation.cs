namespace Parsek
{
    /// <summary>
    /// Pure presentation helper for the Logistics window "Create Route" confirm
    /// (H6). The candidate "Create Route" button opens a three-button summary
    /// dialog ("Create Paused" / "Create and Activate" / "Cancel"); this helper
    /// owns the pure decision for what each button outcome means, so the
    /// build-vs-activate branch logic is unit testable off the IMGUI path
    /// (mirrors <see cref="LogisticsButtonState"/> and
    /// <see cref="LogisticsCountdownPresentation"/>). Unity-free and
    /// side-effect-free. The window owns the actual dialog spawn and the
    /// in-callback build (see LogisticsWindowUI.SpawnCreateRouteConfirmation);
    /// this helper only classifies the chosen button.
    /// </summary>
    internal static class LogisticsCreatePresentation
    {
        /// <summary>Which button the player chose in the Create Route confirm.</summary>
        internal enum CreateRouteChoice
        {
            /// <summary>"Cancel": no route is built; the dialog only logs.</summary>
            Cancel = 0,

            /// <summary>"Create Paused": build the route and leave it Paused
            /// (the existing window-create behavior).</summary>
            CreatePaused = 1,

            /// <summary>"Create and Activate": build the route, then activate it so
            /// it begins auto-dispatching immediately.</summary>
            CreateAndActivate = 2,
        }

        /// <summary>
        /// True when the chosen outcome should run <c>RouteBuilder.BuildRoute</c>
        /// at all. Both create branches build; only Cancel does not. Drives whether
        /// the callback touches the route store.
        /// </summary>
        internal static bool ShouldBuild(CreateRouteChoice choice)
            => choice != CreateRouteChoice.Cancel;

        /// <summary>
        /// True only for "Create and Activate": after building, the callback must
        /// additionally call <c>RouteOrchestrator.TryActivate</c> on the freshly
        /// built (Paused) route. False for "Create Paused" (leave it Paused) and
        /// for "Cancel" (nothing was built).
        /// </summary>
        internal static bool ShouldActivate(CreateRouteChoice choice)
            => choice == CreateRouteChoice.CreateAndActivate;

        // ------------------------------------------------------------------
        // M5: manual-loop-turned-off toast + the always-visible ownership note.
        // ------------------------------------------------------------------

        /// <summary>
        /// True when creating the route actually disabled at least one manual loop on
        /// its source tree (<paramref name="clearedCount"/> &gt; 0), so the
        /// "manual loop turned off" toast should fire. A route created on a tree that
        /// had no manual loop clears nothing and produces no toast.
        /// </summary>
        internal static bool ShouldToastManualLoopCleared(int clearedCount)
            => clearedCount > 0;

        /// <summary>
        /// The one-shot screen toast posted when a Create Route disables an existing
        /// manual loop on the route's source tree: "Manual loop on '&lt;tree&gt;'
        /// turned off: a route now owns this tree". Pure for unit testing; the window
        /// posts it via <c>ParsekLog.ScreenMessage</c>.
        /// </summary>
        internal static string FormatManualLoopTurnedOffToast(string treeName)
            => $"Manual loop on '{treeName}' turned off: a route now owns this tree";

        /// <summary>
        /// The always-visible detail-panel ownership note (M5): "This route owns tree
        /// '&lt;tree&gt;'; manual looping is disabled while it exists." Pure for unit
        /// testing; the window renders it as a detail line whenever the route binds a
        /// tree.
        /// </summary>
        internal static string FormatRouteOwnsTreeNote(string treeName)
            => $"This route owns tree '{treeName}'; manual looping is disabled while it exists.";
    }
}
