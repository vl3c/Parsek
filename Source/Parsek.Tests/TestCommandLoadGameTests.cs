using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P5.7 coverage for the LoadGame focusability decision + completion payload. A null
    /// / incompatible game or an out-of-range active-vessel index must fail with
    /// load-failed rather than sending FlightDriver.StartAndFocusVessel a bad index
    /// (design edge case 27). Fails if a bad load is treated as focusable, or if the
    /// completion payload keys drift.
    /// </summary>
    public class TestCommandLoadGameTests
    {
        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        [Fact]
        public void Focusable_ValidGame_InRangeIdx()
        {
            Assert.True(TestCommandLoadGame.IsLoadedGameFocusable(
                gamePresent: true, compatible: true, flightStatePresent: true, protoVesselsPresent: true,
                activeVesselIdx: 0, protoVesselCount: 3));
        }

        [Fact]
        public void NotFocusable_NullGame()
        {
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(false, false, false, false, 0, 0));
        }

        [Fact]
        public void NotFocusable_IncompatibleGame()
        {
            // A version-incompatible game (Game.compatible == false) is NOT focusable even
            // though it parsed and has an in-range active vessel.
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(
                gamePresent: true, compatible: false, flightStatePresent: true, protoVesselsPresent: true,
                activeVesselIdx: 0, protoVesselCount: 3));
        }

        [Fact]
        public void NotFocusable_NullFlightState_OrProtoVessels()
        {
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(true, true, false, false, 0, 1));
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(true, true, true, false, 0, 1));
        }

        [Fact]
        public void NotFocusable_IdxOutOfRange()
        {
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(true, true, true, true, -1, 2));
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(true, true, true, true, 2, 2));
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(true, true, true, true, 5, 2));
        }

        [Fact]
        public void CompletePayload_CarriesSceneAndSave()
        {
            var p = TestCommandLoadGame.BuildCompletePayload("FLIGHT", "DefaultCareer");
            Assert.Equal("FLIGHT", Val(p, "scene"));
            Assert.Equal("DefaultCareer", Val(p, "save"));
            Assert.Equal(new[] { "scene", "save" }, p.Select(kv => kv.Key).ToArray());
        }

        [Fact]
        public void CompletePayload_NullSave_EmptyString()
        {
            var p = TestCommandLoadGame.BuildCompletePayload("MAINMENU", null);
            Assert.Equal(string.Empty, Val(p, "save"));
        }

        // ----- F2: two-phase completion decision (StillWaiting / CompleteOk /
        // LoadTimeout / LoadFailedMenu). A failed load must resolve to a terminal
        // ERROR instead of hanging PENDING to the harness run budget. -----

        private const double Budget = 300.0;

        [Fact]
        public void DecideLoadCompletion_FlightWithGame_CompleteOk()
        {
            Assert.Equal(LoadCompletionDecision.CompleteOk,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.Flight, currentGameNonNull: true, Budget));
        }

        [Fact]
        public void DecideLoadCompletion_FlightNoGameYet_StillWaiting()
        {
            // A FLIGHT scene without a loaded game (transient) is not yet complete.
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.Flight, currentGameNonNull: false, Budget));
        }

        [Fact]
        public void DecideLoadCompletion_OtherSceneWithinBudget_StillWaiting()
        {
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.SpaceCenter, currentGameNonNull: true, Budget));
        }

        [Fact]
        public void DecideLoadCompletion_ReturnedToMenu_LoadFailedMenu()
        {
            // The scene settled back at MAINMENU (a failed load, e.g. an NRE in
            // FlightDriver.Start) -> fast terminal failure, even well within budget.
            Assert.Equal(LoadCompletionDecision.LoadFailedMenu,
                TestCommandLoadGame.DecideLoadCompletion(
                    2.0, TestCommandScene.MainMenu, currentGameNonNull: false, Budget));
        }

        [Fact]
        public void DecideLoadCompletion_MenuWithGameObject_StillFailedMenu()
        {
            // StartAndFocusVessel sets HighLogic.CurrentGame before the flight boot,
            // so the game object may be non-null even when the load bounced to the menu.
            // A MAINMENU observation is the failure regardless of the game object.
            Assert.Equal(LoadCompletionDecision.LoadFailedMenu,
                TestCommandLoadGame.DecideLoadCompletion(
                    2.0, TestCommandScene.MainMenu, currentGameNonNull: true, Budget));
        }

        [Fact]
        public void DecideLoadCompletion_MenuTakesPrecedenceOverTimeout()
        {
            // MAINMENU is the more actionable signal, so it is reported even past budget.
            Assert.Equal(LoadCompletionDecision.LoadFailedMenu,
                TestCommandLoadGame.DecideLoadCompletion(
                    Budget + 10.0, TestCommandScene.MainMenu, currentGameNonNull: false, Budget));
        }

        [Fact]
        public void DecideLoadCompletion_BudgetExpiredElsewhere_LoadTimeout()
        {
            // The load never settled at flight or menu and the budget expired -> timeout.
            Assert.Equal(LoadCompletionDecision.LoadTimeout,
                TestCommandLoadGame.DecideLoadCompletion(
                    Budget, TestCommandScene.Loading, currentGameNonNull: false, Budget));
        }

        [Fact]
        public void DecideLoadCompletion_FlightGameNullPastBudget_LoadTimeout()
        {
            // A FLIGHT scene that never got a loaded game, past budget -> timeout, not OK.
            Assert.Equal(LoadCompletionDecision.LoadTimeout,
                TestCommandLoadGame.DecideLoadCompletion(
                    Budget + 1.0, TestCommandScene.Flight, currentGameNonNull: false, Budget));
        }
    }
}
