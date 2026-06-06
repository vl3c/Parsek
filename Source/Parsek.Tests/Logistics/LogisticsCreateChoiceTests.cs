using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

using CreateRouteChoice = Parsek.LogisticsCreatePresentation.CreateRouteChoice;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins H6: the three-button Create Route confirm ("Create Paused" /
    /// "Create and Activate" / "Cancel") maps to the pure
    /// <see cref="LogisticsCreatePresentation"/> build-vs-activate decision. The
    /// IMGUI dialog spawn (SpawnCreateRouteConfirmation) is not unit-testable, so
    /// this test pins the pure choice surface the callbacks branch on; the
    /// "Create and Activate" effect (TryActivate flips a Paused route to Active) is
    /// covered separately here against a real Paused Route, matching the
    /// RouteOrchestratorTests precedent. Unity-free.
    /// </summary>
    public class LogisticsCreateChoiceTests
    {
        // ---- ShouldBuild: both create branches build; Cancel does not. ----

        [Fact]
        public void ShouldBuild_CreatePaused_True()
        {
            Assert.True(LogisticsCreatePresentation.ShouldBuild(CreateRouteChoice.CreatePaused));
        }

        [Fact]
        public void ShouldBuild_CreateAndActivate_True()
        {
            Assert.True(LogisticsCreatePresentation.ShouldBuild(CreateRouteChoice.CreateAndActivate));
        }

        [Fact]
        public void ShouldBuild_Cancel_False()
        {
            Assert.False(LogisticsCreatePresentation.ShouldBuild(CreateRouteChoice.Cancel));
        }

        // ---- ShouldActivate: true ONLY for "Create and Activate". ----

        [Fact]
        public void ShouldActivate_CreateAndActivate_True()
        {
            Assert.True(LogisticsCreatePresentation.ShouldActivate(CreateRouteChoice.CreateAndActivate));
        }

        [Fact]
        public void ShouldActivate_CreatePaused_False()
        {
            // "Create Paused" must NOT activate: it lands Paused (current behavior).
            Assert.False(LogisticsCreatePresentation.ShouldActivate(CreateRouteChoice.CreatePaused));
        }

        [Fact]
        public void ShouldActivate_Cancel_False()
        {
            Assert.False(LogisticsCreatePresentation.ShouldActivate(CreateRouteChoice.Cancel));
        }

        // ---- The "Create and Activate" effect: a built (Paused) route, when the
        // activate path runs, flips to Active via RouteOrchestrator.TryActivate
        // (mirrors RouteOrchestratorTests.TryActivate_PausedRoute_TransitionsToActive).

        [Fact]
        public void ActivatePath_OnFreshlyBuiltPausedRoute_TransitionsToActive()
        {
            // A window-created route is built Paused (initialStatus: RouteStatus.Paused),
            // so the "Create and Activate" branch can hand it straight to TryActivate.
            Route route = new RouteFixtureBuilder().WithId("h6-activate").Build();
            route.Status = RouteStatus.Paused;

            // ShouldActivate gates the call; the activate branch then runs TryActivate.
            Assert.True(LogisticsCreatePresentation.ShouldActivate(CreateRouteChoice.CreateAndActivate));
            bool ok = RouteOrchestrator.TryActivate(route, 100.0);

            Assert.True(ok);
            Assert.Equal(RouteStatus.Active, route.Status);
        }

        [Fact]
        public void PausedPath_DoesNotActivate_RouteStaysPaused()
        {
            // "Create Paused" must not run the activate call, so the route the build
            // produced (Paused) stays Paused. Pinning the branch gate is enough; we
            // assert ShouldActivate is false so the callback never calls TryActivate.
            Route route = new RouteFixtureBuilder().WithId("h6-paused").Build();
            route.Status = RouteStatus.Paused;

            Assert.False(LogisticsCreatePresentation.ShouldActivate(CreateRouteChoice.CreatePaused));
            Assert.Equal(RouteStatus.Paused, route.Status);
        }

        // ------------------------------------------------------------------
        // M5: the toast-should-fire decision + the two pure strings.
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(0, false)]
        [InlineData(-1, false)]
        public void ShouldToastManualLoopCleared_OnlyPositiveCount(int cleared, bool expected)
        {
            // The toast must fire ONLY when a manual loop was actually turned off.
            Assert.Equal(expected, LogisticsCreatePresentation.ShouldToastManualLoopCleared(cleared));
        }

        [Fact]
        public void FormatManualLoopTurnedOffToast_NamesTreeAndExplainsOwnership()
        {
            string toast = LogisticsCreatePresentation.FormatManualLoopTurnedOffToast("Munar Logistics");

            Assert.Equal(
                "Manual loop on 'Munar Logistics' turned off: a route now owns this tree",
                toast);
        }

        [Fact]
        public void FormatRouteOwnsTreeNote_NamesTreeAndStatesLoopingDisabled()
        {
            string note = LogisticsCreatePresentation.FormatRouteOwnsTreeNote("Munar Logistics");

            Assert.Equal(
                "This route owns tree 'Munar Logistics'; manual looping is disabled while it exists.",
                note);
        }
    }
}
