using Xunit;

namespace Parsek.Tests
{
    // The Basic-mode gate over the Missions tab's manual-loop authoring controls (design
    // docs/dev/design-ui-basic-advanced.md section 4.5): the per-mission "Loop" toggle, the
    // loop-period cell beside it, and the include checkboxes that pick which intervals /
    // partner journeys the loop replays.
    //
    // The draw sites themselves are IMGUI callbacks with no headless seam, so what is
    // testable is the decision they all read: MissionsWindowUI.ShowsLoopAuthoringControls.
    // Each test names the regression it catches.
    public class MissionsWindowLoopGateTests
    {
        // The feature itself. Basic drops the three controls; Advanced is unchanged
        // (philosophy 6 - Advanced stays behaviorally identical to today).
        [Fact]
        public void BasicHidesTheLoopAuthoringControlsAndAdvancedKeepsThem()
        {
            Assert.False(
                MissionsWindowUI.ShowsLoopAuthoringControls(UiComplexityMode.Basic),
                "Basic must not draw the Missions tab's manual-loop authoring controls");
            Assert.True(
                MissionsWindowUI.ShowsLoopAuthoringControls(UiComplexityMode.Advanced),
                "Advanced must keep every loop control it draws today");
        }

        // The gate is the shared decision point, not a private second opinion (philosophy 4).
        // Fails if someone re-implements the rule inline instead of re-keying this helper.
        [Fact]
        public void TheGateIsDerivedFromTheSharedSurfaceDecision()
        {
            foreach (UiComplexityMode mode in new[] { UiComplexityMode.Basic, UiComplexityMode.Advanced })
            {
                Assert.Equal(
                    UiSurfaceVisibility.IsVisible(UiSurface.MissionsLoopControls, mode),
                    MissionsWindowUI.ShowsLoopAuthoringControls(mode));
            }
        }

        // Scope guard. The loop controls live INSIDE the Missions tab, so a mis-keyed gate
        // (pointing at the tab instead of the control group) would take the whole tab dark in
        // Basic - the surface design section 4 pins as a core-loop keeper.
        [Fact]
        public void HidingTheLoopControlsDoesNotHideTheMissionsTab()
        {
            Assert.True(
                UiSurfaceVisibility.IsVisible(UiSurface.TabMissions, UiComplexityMode.Basic),
                "the Missions tab must stay visible in Basic; only its loop controls are gated");
            Assert.True(
                UiSurfaceVisibility.IsVisible(UiSurface.MainButtonRecordings, UiComplexityMode.Basic),
                "the Missions launcher must stay visible in Basic");
        }
    }
}
