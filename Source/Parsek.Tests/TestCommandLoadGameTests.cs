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

        // ----- LoadRoute (the ledger-lane no-vessel extension): a vessel-less
        // clean-slate career must resume to SPACECENTER, never load-fail. The
        // first live L-track run (2026-07-23) proved every career fixture is
        // NECESSARILY vessel-less; the old focusable-or-fail contract blocked
        // the whole ledger lane deterministically. -----

        [Fact]
        public void Route_FocusableGame_Flight()
        {
            Assert.Equal(LoadRoute.Focusable, TestCommandLoadGame.DecideLoadRoute(
                true, true, true, true, activeVesselIdx: 0, protoVesselCount: 3));
        }

        [Fact]
        public void Route_VesselLessCleanSlate_SpaceCenter()
        {
            // The fresh-career/science/sandbox fixtures: zero vessels, idx -1.
            Assert.Equal(LoadRoute.NoVesselSpaceCenter, TestCommandLoadGame.DecideLoadRoute(
                true, true, true, true, activeVesselIdx: -1, protoVesselCount: 0));
        }

        [Fact]
        public void Route_ParkedVesselsNoActive_SpaceCenter()
        {
            // activeVessel = -1 with parked vessels: a valid KSC-resume save.
            Assert.Equal(LoadRoute.NoVesselSpaceCenter, TestCommandLoadGame.DecideLoadRoute(
                true, true, true, true, activeVesselIdx: -1, protoVesselCount: 2));
        }

        [Fact]
        public void Route_NullProtoVesselList_SpaceCenterNotFailed()
        {
            // A null proto-vessel LIST is tolerated on the no-vessel route (KSP
            // normalizes it at scene start); game validity still gates.
            Assert.Equal(LoadRoute.NoVesselSpaceCenter, TestCommandLoadGame.DecideLoadRoute(
                true, true, true, false, activeVesselIdx: -1, protoVesselCount: 0));
        }

        [Fact]
        public void Route_InvalidGame_Failed()
        {
            Assert.Equal(LoadRoute.Failed, TestCommandLoadGame.DecideLoadRoute(
                false, false, false, false, 0, 0));
            Assert.Equal(LoadRoute.Failed, TestCommandLoadGame.DecideLoadRoute(
                true, false, true, true, 0, 1));   // incompatible
            Assert.Equal(LoadRoute.Failed, TestCommandLoadGame.DecideLoadRoute(
                true, true, false, false, -1, 0)); // no flight state
        }

        [Fact]
        public void Completion_SpaceCenterRoute_CompletesOnSettledKsc()
        {
            Assert.Equal(LoadCompletionDecision.CompleteOk,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.SpaceCenter, currentGameNonNull: true,
                    budgetSeconds: 600.0, expectedScene: TestCommandScene.SpaceCenter));
            // A FLIGHT settle does NOT complete the KSC route (and vice versa:
            // the default route still requires FLIGHT).
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.Flight, true, 600.0, TestCommandScene.SpaceCenter));
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.SpaceCenter, true, 600.0));
        }

        [Fact]
        public void Completion_SpaceCenterRoute_MenuBounceAndTimeoutKeepMeanings()
        {
            Assert.Equal(LoadCompletionDecision.LoadFailedMenu,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.MainMenu, false, 600.0, TestCommandScene.SpaceCenter));
            Assert.Equal(LoadCompletionDecision.LoadTimeout,
                TestCommandLoadGame.DecideLoadCompletion(
                    600.0, TestCommandScene.Loading, false, 600.0, TestCommandScene.SpaceCenter));
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

        // ----- R12: the optional `scene=` boot-route override. The TRACKSTATION route is
        // the only way into that scene at all (nothing in Parsek ever entered it
        // programmatically), and the parse is FAIL-CLOSED because a silently-ignored
        // scene arg is the B10-shaped fail-open: a wrong-scene boot reads GREEN through a
        // batch whose tests all scene-skip. -----

        [Fact]
        public void ParseRequestedScene_Absent_Unspecified()
        {
            Assert.True(TestCommandLoadGame.TryParseRequestedScene(null, out RequestedBootScene scene));
            Assert.Equal(RequestedBootScene.Unspecified, scene);
        }

        [Theory]
        [InlineData("trackstation")]
        [InlineData("spacecenter")]
        public void ParseRequestedScene_AcceptedWireValues(string raw)
        {
            Assert.True(TestCommandLoadGame.TryParseRequestedScene(raw, out RequestedBootScene scene));
            Assert.Equal(raw == "trackstation"
                ? RequestedBootScene.TrackingStation
                : RequestedBootScene.SpaceCenter, scene);
        }

        [Theory]
        [InlineData("")]              // present but empty: a typo, not an omission
        [InlineData(" ")]
        [InlineData("TRACKSTATION")]  // case-sensitive, like RunTests' isolated=
        [InlineData("TrackStation")]
        [InlineData("SpaceCenter")]
        [InlineData("tracking-station")]
        [InlineData("trackingstation")]
        [InlineData("ts")]
        [InlineData("ksc")]
        [InlineData("flight")]        // deliberately NOT accepted (see TryParseRequestedScene)
        [InlineData("mainmenu")]
        [InlineData("editor")]
        public void ParseRequestedScene_RejectsEverythingElse(string raw)
        {
            Assert.False(TestCommandLoadGame.TryParseRequestedScene(raw, out RequestedBootScene scene));
            // The out value stays at the inert default so a caller that ignores the
            // bool cannot silently boot somewhere it did not ask for.
            Assert.Equal(RequestedBootScene.Unspecified, scene);
        }

        [Fact]
        public void Route_RequestedTrackStation_TakesTrackStation_EvenWithFocusableVessel()
        {
            // The whole point: no property of a save says "boot me to the tracking
            // station", so the route is caller-requested and outranks focusability.
            Assert.Equal(LoadRoute.TrackingStation, TestCommandLoadGame.DecideLoadRoute(
                true, true, true, true, activeVesselIdx: 0, protoVesselCount: 3,
                requestedScene: RequestedBootScene.TrackingStation));
        }

        [Fact]
        public void Route_RequestedTrackStation_TakesTrackStation_OnVesselLessSave()
        {
            Assert.Equal(LoadRoute.TrackingStation, TestCommandLoadGame.DecideLoadRoute(
                true, true, true, true, activeVesselIdx: -1, protoVesselCount: 0,
                requestedScene: RequestedBootScene.TrackingStation));
        }

        [Fact]
        public void Route_RequestedSpaceCenter_ForcesKsc_OverFocusableVessel()
        {
            // Without the request this exact shape is LoadRoute.Focusable.
            Assert.Equal(LoadRoute.Focusable, TestCommandLoadGame.DecideLoadRoute(
                true, true, true, true, activeVesselIdx: 0, protoVesselCount: 3));
            Assert.Equal(LoadRoute.NoVesselSpaceCenter, TestCommandLoadGame.DecideLoadRoute(
                true, true, true, true, activeVesselIdx: 0, protoVesselCount: 3,
                requestedScene: RequestedBootScene.SpaceCenter));
        }

        [Fact]
        public void Route_UnspecifiedScene_IsPreR12BehaviorVerbatim()
        {
            // Passing Unspecified explicitly must equal omitting the parameter, on every
            // shape the pre-R12 cells above cover.
            Assert.Equal(LoadRoute.Focusable, TestCommandLoadGame.DecideLoadRoute(
                true, true, true, true, 0, 3, RequestedBootScene.Unspecified));
            Assert.Equal(LoadRoute.NoVesselSpaceCenter, TestCommandLoadGame.DecideLoadRoute(
                true, true, true, true, -1, 0, RequestedBootScene.Unspecified));
            Assert.Equal(LoadRoute.NoVesselSpaceCenter, TestCommandLoadGame.DecideLoadRoute(
                true, true, true, false, -1, 0, RequestedBootScene.Unspecified));
            Assert.Equal(LoadRoute.Failed, TestCommandLoadGame.DecideLoadRoute(
                true, false, true, true, 0, 1, RequestedBootScene.Unspecified));
        }

        [Theory]
        [InlineData("trackstation")]
        [InlineData("spacecenter")]
        public void Route_InvalidGame_FailsRegardlessOfRequestedScene(string raw)
        {
            // PRECEDENCE: validity outranks the request. A requested scene cannot rescue
            // a save that did not parse, so these must be load-failed, never a boot into
            // TRACKSTATION/SPACECENTER on a null or incompatible game.
            Assert.True(TestCommandLoadGame.TryParseRequestedScene(raw, out RequestedBootScene scene));
            Assert.Equal(LoadRoute.Failed, TestCommandLoadGame.DecideLoadRoute(
                false, false, false, false, 0, 0, scene));                 // null game
            Assert.Equal(LoadRoute.Failed, TestCommandLoadGame.DecideLoadRoute(
                true, false, true, true, 0, 1, scene));                    // incompatible
            Assert.Equal(LoadRoute.Failed, TestCommandLoadGame.DecideLoadRoute(
                true, true, false, false, -1, 0, scene));                  // no flight state
        }

        [Fact]
        public void ExpectedSceneFor_MapsEveryRoute()
        {
            Assert.Equal(TestCommandScene.Flight, TestCommandLoadGame.ExpectedSceneFor(LoadRoute.Focusable));
            Assert.Equal(TestCommandScene.SpaceCenter, TestCommandLoadGame.ExpectedSceneFor(LoadRoute.NoVesselSpaceCenter));
            Assert.Equal(TestCommandScene.TrackingStation, TestCommandLoadGame.ExpectedSceneFor(LoadRoute.TrackingStation));
            // Failed never reaches two-phase; it must still map to something inert
            // rather than throwing inside a completion poll.
            Assert.Equal(TestCommandScene.Flight, TestCommandLoadGame.ExpectedSceneFor(LoadRoute.Failed));
        }

        [Fact]
        public void Completion_TrackStationRoute_CompletesOnlyOnSettledTrackStation()
        {
            Assert.Equal(LoadCompletionDecision.CompleteOk,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.TrackingStation, currentGameNonNull: true,
                    budgetSeconds: 600.0, expectedScene: TestCommandScene.TrackingStation));
            // Neither of the other two routes' landing scenes completes it. This is the
            // gate that keeps a silently-wrong-scene boot from reading OK.
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.Flight, true, 600.0, TestCommandScene.TrackingStation));
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.SpaceCenter, true, 600.0, TestCommandScene.TrackingStation));
        }

        [Fact]
        public void Completion_TrackStationSettle_DoesNotCompleteTheOtherTwoRoutes()
        {
            // The converse: arriving at TRACKSTATION must not satisfy a FLIGHT or KSC
            // route either.
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.TrackingStation, true, 600.0));
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.TrackingStation, true, 600.0, TestCommandScene.SpaceCenter));
        }

        [Fact]
        public void Completion_TrackStationRoute_NoGameYet_StillWaiting()
        {
            Assert.Equal(LoadCompletionDecision.StillWaiting,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.TrackingStation, currentGameNonNull: false,
                    600.0, TestCommandScene.TrackingStation));
        }

        [Fact]
        public void Completion_TrackStationRoute_MenuBounceAndTimeoutKeepMeanings()
        {
            Assert.Equal(LoadCompletionDecision.LoadFailedMenu,
                TestCommandLoadGame.DecideLoadCompletion(
                    5.0, TestCommandScene.MainMenu, false, 600.0, TestCommandScene.TrackingStation));
            Assert.Equal(LoadCompletionDecision.LoadTimeout,
                TestCommandLoadGame.DecideLoadCompletion(
                    600.0, TestCommandScene.SpaceCenter, true, 600.0, TestCommandScene.TrackingStation));
        }
    }
}
