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
        /// The one-shot screen toast posted when a Create Route took over a mission that was
        /// repeating on its own: "Mission '&lt;mission&gt;' now repeats only on this route's
        /// schedule." It can fire in Basic, which has no loop control, so it names the
        /// effect without the word. <paramref name="missionName"/> is the source tree's
        /// ORIGINAL mission name (the window falls back to the tree's name). Pure for unit
        /// testing; the window posts it via <c>ParsekLog.ScreenMessage</c>.
        /// </summary>
        internal static string FormatMissionNowRepeatsOnRouteToast(string missionName)
            => $"Mission '{missionName}' now repeats only on this route's schedule.";
    }
}
