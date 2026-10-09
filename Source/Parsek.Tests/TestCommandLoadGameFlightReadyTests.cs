using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// LOADGAME-COMPLETES-BEFORE-ONFLIGHTREADY: a FLIGHT-route LoadGame must not answer OK
    /// until Parsek's onFlightReady has run for THIS load and the active vessel has
    /// unpacked. Before the fix the verb completed on the settled scene alone, so the next
    /// step raced <c>ParsekFlight.ResetFlightReadyState</c> (EVA-6) or the post-load unpack
    /// (CI-1 recorded on rails). Every other route, and every existing failure cell, keeps
    /// its meaning. Fails if a FLIGHT load completes before either condition, if a
    /// non-FLIGHT route starts waiting on flight readiness, or if a never-ready flight
    /// reads OK instead of the distinct flight-ready timeout.
    /// </summary>
    public class TestCommandLoadGameFlightReadyTests
    {
        private const double Budget = 300.0;

        private static LoadCompletionDecision Flight(
            bool flightReady, bool unpacked, double waited = 0.0, double elapsed = 5.0)
            => TestCommandLoadGame.DecideLoadCompletion(
                elapsed, TestCommandScene.Flight, currentGameNonNull: true, Budget,
                TestCommandScene.Flight, flightReady, unpacked, waited);

        [Fact]
        public void Flight_BeforeFlightReady_Waits()
        {
            Assert.Equal(LoadCompletionDecision.AwaitingFlightReady, Flight(false, false));
            // Unpacked alone is not enough: the reset has not run yet.
            Assert.Equal(LoadCompletionDecision.AwaitingFlightReady, Flight(false, true));
        }

        [Fact]
        public void Flight_FlightReadyButPacked_Waits()
        {
            Assert.Equal(LoadCompletionDecision.AwaitingFlightReady, Flight(true, false));
        }

        [Fact]
        public void Flight_FlightReadyAndUnpacked_CompletesOk()
        {
            Assert.Equal(LoadCompletionDecision.CompleteOk, Flight(true, true));
        }

        [Fact]
        public void Flight_WaitPastCap_IsFlightReadyTimeout_NotOk()
        {
            double cap = TestCommandLoadGame.FlightReadyWaitMaxSeconds;
            Assert.Equal(LoadCompletionDecision.AwaitingFlightReady, Flight(false, false, waited: cap - 0.1));
            Assert.Equal(LoadCompletionDecision.FlightReadyTimeout, Flight(false, false, waited: cap));
            Assert.Equal(LoadCompletionDecision.FlightReadyTimeout, Flight(true, false, waited: cap + 5.0));
            // Ready on the very frame the cap is reached still completes: readiness is checked first.
            Assert.Equal(LoadCompletionDecision.CompleteOk, Flight(true, true, waited: cap + 5.0));
        }

        [Fact]
        public void Flight_LoadBudgetExpiresWhileWaiting_IsFlightReadyTimeout_NotLoadTimeout()
        {
            // The scene DID arrive, so the generic load-timeout would misname the failure.
            Assert.Equal(LoadCompletionDecision.FlightReadyTimeout,
                Flight(false, true, waited: 1.0, elapsed: Budget));
        }

        [Fact]
        public void NonFlightRoute_IgnoresFlightReadiness()
        {
            // Mirror direction: a KSC / TS boot has no flight to get ready, so the new inputs
            // must not hold it (they read false there in production).
            foreach (TestCommandScene route in new[] { TestCommandScene.SpaceCenter, TestCommandScene.TrackingStation })
            {
                Assert.Equal(LoadCompletionDecision.CompleteOk,
                    TestCommandLoadGame.DecideLoadCompletion(
                        5.0, route, true, Budget, route,
                        flightReadyObserved: false, activeVesselUnpacked: false,
                        flightReadyWaitSeconds: 0.0));
            }
        }

        [Fact]
        public void Flight_NotArrived_KeepsTheOldCells()
        {
            // No game yet, a menu bounce and a load that never settles keep their meanings.
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.Flight, false, Budget, TestCommandScene.Flight, false, false, 0.0));
            Assert.Equal(LoadCompletionDecision.LoadFailedMenu,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.MainMenu, true, Budget, TestCommandScene.Flight, false, false, 0.0));
            Assert.Equal(LoadCompletionDecision.LoadTimeout,
                TestCommandLoadGame.DecideLoadCompletion(
                    Budget, TestCommandScene.Loading, false, Budget, TestCommandScene.Flight, false, false, 0.0));
        }

        [Fact]
        public void FlightReadyForThisLoad_RequiresANewInstanceThatSawTheEvent()
        {
            // A FLIGHT -> FLIGHT reload: the previous scene's instance had seen its own event.
            Assert.False(TestCommandLoadGame.IsFlightReadyForThisLoad(
                currentFlightInstanceId: 42, previousFlightInstanceId: 42, flightReadyObserved: true));
            // The new instance before its event.
            Assert.False(TestCommandLoadGame.IsFlightReadyForThisLoad(43, 42, false));
            // No instance at all.
            Assert.False(TestCommandLoadGame.IsFlightReadyForThisLoad(0, 42, true));
            // The new instance after its event; and a cold boot with no previous instance.
            Assert.True(TestCommandLoadGame.IsFlightReadyForThisLoad(43, 42, true));
            Assert.True(TestCommandLoadGame.IsFlightReadyForThisLoad(43, 0, true));
        }

        [Fact]
        public void FlightReadyWaitLine_NamesFramesAndSeconds()
        {
            string line = TestCommandLoadGame.FormatFlightReadyWaitLine(
                frames: 57, seconds: 1.234, save: "run1", vesselName: "Kerbal X");
            Assert.Equal(
                "loadgame flight-ready wait: waited frames=57 seconds=1.23 after the FLIGHT scene settled (onFlightReady observed, active vessel unpacked) save=run1 vessel='Kerbal X'",
                line);
        }
    }
}
