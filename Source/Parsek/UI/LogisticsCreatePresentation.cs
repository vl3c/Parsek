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
    }
}
