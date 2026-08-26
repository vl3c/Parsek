using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the map-view seam pair (<see cref="TestCommandMapViewVerbs"/>): the
    /// four-branch toggle decision, the per-direction refusal tokens, and the two OK payload
    /// shapes. These verbs exist to open the map so the render pipeline's DRAW half actually
    /// runs (RC-OWN-DRAW-HALF-IS-MAP-GATED), so the property that matters most here is that a
    /// refusal is never dressed as a success: with the map closed, a "visible" TracedPath
    /// dwell is intent and not a draw, and an OK this verb did not earn would put that
    /// mistake back into a lane's arming pass.
    /// </summary>
    public class TestCommandMapViewVerbsTests
    {
        // ----- The decision, both directions -----

        [Theory]
        [InlineData(true)]   // EnterMapView
        [InlineData(false)]  // ExitMapView
        public void NoMapViewInstance_IsUnavailable_RegardlessOfEverythingElse(bool wantOpen)
        {
            // Availability is checked FIRST because with no MapView instance every other
            // sampled boolean is meaningless (openBefore/openAfter are forced false by the
            // applier, which would otherwise read Unavailable as "already closed" and answer
            // OK to an ExitMapView the game never heard).
            Assert.Equal(MapViewToggleOutcome.Unavailable,
                TestCommandMapViewVerbs.DecideToggleOutcome(
                    mapViewPresent: false, wantOpen: wantOpen,
                    openBefore: false, openAfter: false));
        }

        [Fact]
        public void Enter_WithMapAlreadyOpen_IsAlreadyInState()
        {
            Assert.Equal(MapViewToggleOutcome.AlreadyInState,
                TestCommandMapViewVerbs.DecideToggleOutcome(
                    mapViewPresent: true, wantOpen: true,
                    openBefore: true, openAfter: true));
        }

        [Fact]
        public void Exit_WithMapAlreadyClosed_IsAlreadyInState()
        {
            Assert.Equal(MapViewToggleOutcome.AlreadyInState,
                TestCommandMapViewVerbs.DecideToggleOutcome(
                    mapViewPresent: true, wantOpen: false,
                    openBefore: false, openAfter: false));
        }

        [Fact]
        public void Enter_ClosedThenOpen_IsChanged()
        {
            Assert.Equal(MapViewToggleOutcome.Changed,
                TestCommandMapViewVerbs.DecideToggleOutcome(
                    mapViewPresent: true, wantOpen: true,
                    openBefore: false, openAfter: true));
        }

        [Fact]
        public void Exit_OpenThenClosed_IsChanged()
        {
            Assert.Equal(MapViewToggleOutcome.Changed,
                TestCommandMapViewVerbs.DecideToggleOutcome(
                    mapViewPresent: true, wantOpen: false,
                    openBefore: true, openAfter: false));
        }

        [Fact]
        public void Enter_ReadBackStillClosed_IsRefused()
        {
            // The stock decline paths (ConstantMode, MissionSystem.AllowCameraSwitch,
            // Flight.CanUseMap) are all void returns, so this read-back is the ONLY verdict
            // source. It must not collapse into Changed.
            Assert.Equal(MapViewToggleOutcome.Refused,
                TestCommandMapViewVerbs.DecideToggleOutcome(
                    mapViewPresent: true, wantOpen: true,
                    openBefore: false, openAfter: false));
        }

        [Fact]
        public void Exit_ReadBackStillOpen_IsRefused()
        {
            Assert.Equal(MapViewToggleOutcome.Refused,
                TestCommandMapViewVerbs.DecideToggleOutcome(
                    mapViewPresent: true, wantOpen: false,
                    openBefore: true, openAfter: true));
        }

        // ----- Refusal tokens + verdicts -----

        [Fact]
        public void RefusalReasonAndVerdict_AreNullForBothSuccessOutcomes()
        {
            foreach (bool wantOpen in new[] { true, false })
            {
                Assert.Null(TestCommandMapViewVerbs.RefusalReason(
                    MapViewToggleOutcome.Changed, wantOpen));
                Assert.Null(TestCommandMapViewVerbs.RefusalReason(
                    MapViewToggleOutcome.AlreadyInState, wantOpen));
            }
            Assert.Null(TestCommandMapViewVerbs.RefusalVerdict(MapViewToggleOutcome.Changed));
            Assert.Null(TestCommandMapViewVerbs.RefusalVerdict(
                MapViewToggleOutcome.AlreadyInState));
        }

        [Fact]
        public void RefusalReason_UnavailableIsSharedByBothDirections()
        {
            // One token, deliberately: "there is no MapView here" is the same fact whichever
            // way the spec asked, and a per-direction spelling would be two tokens for a
            // single state.
            Assert.Equal(TestCommandMapViewVerbs.MapViewUnavailableReason,
                TestCommandMapViewVerbs.RefusalReason(MapViewToggleOutcome.Unavailable, true));
            Assert.Equal(TestCommandMapViewVerbs.MapViewUnavailableReason,
                TestCommandMapViewVerbs.RefusalReason(MapViewToggleOutcome.Unavailable, false));
        }

        [Fact]
        public void RefusalReason_DeclineTokensAreDirectionSpecificAndDistinct()
        {
            string enter = TestCommandMapViewVerbs.RefusalReason(
                MapViewToggleOutcome.Refused, wantOpen: true);
            string exit = TestCommandMapViewVerbs.RefusalReason(
                MapViewToggleOutcome.Refused, wantOpen: false);
            Assert.Equal(TestCommandMapViewVerbs.MapNotEnteredReason, enter);
            Assert.Equal(TestCommandMapViewVerbs.MapNotExitedReason, exit);
            // A spec pinning `map-not-entered` must never match an exit that declined.
            Assert.NotEqual(enter, exit);
            Assert.NotEqual(TestCommandMapViewVerbs.MapViewUnavailableReason, enter);
            Assert.NotEqual(TestCommandMapViewVerbs.MapViewUnavailableReason, exit);
        }

        [Fact]
        public void AllFourRefusalTokensAreDistinct()
        {
            // The throw token is kept apart from the silent-decline pair for the reason
            // SimulateStockSwitchClick keeps `switch-threw` apart from
            // `switch-refused-by-stock`: a throw is a different investigation.
            var tokens = new[]
            {
                TestCommandMapViewVerbs.MapViewUnavailableReason,
                TestCommandMapViewVerbs.MapNotEnteredReason,
                TestCommandMapViewVerbs.MapNotExitedReason,
                TestCommandMapViewVerbs.MapViewThrewReason,
            };
            Assert.Equal(tokens.Length, tokens.Distinct().Count());
            Assert.All(tokens, t => Assert.False(string.IsNullOrEmpty(t)));
        }

        [Fact]
        public void RefusalVerdict_IsRejectedOnlyBeforeStockIsCalled()
        {
            // THE CONTRACT A SPEC'S `expect` IS WRITTEN AGAINST, and the line is the one
            // SimulateStockSwitchClick draws: REJECTED for every gate evaluated BEFORE the
            // stock call, ERROR for `switch-refused-by-stock` / `switch-threw` AFTER it
            // (EnterWatchMode's post-call `watch-not-entered` is ERROR too). A REJECTED on
            // the Refused branch would claim we declined to act; we acted and the game
            // declined.
            Assert.Equal("REJECTED",
                TestCommandMapViewVerbs.RefusalVerdict(MapViewToggleOutcome.Unavailable));
            Assert.Equal("ERROR",
                TestCommandMapViewVerbs.RefusalVerdict(MapViewToggleOutcome.Refused));
        }

        // ----- Payloads -----

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EnterPayload_AlwaysClaimsMapOpenAndCarriesTheIdempotencyFlag(bool already)
        {
            List<KeyValuePair<string, string>> p =
                TestCommandMapViewVerbs.BuildEnterPayload(already);
            Assert.Equal("true", Value(p, "mapOpen"));
            // Present in BOTH cases (not present-only-when-true): a lane reading this verb is
            // deciding whether ITS step opened the map, and an absent key would make "it was
            // already open" indistinguishable from an older seam build.
            Assert.Equal(already ? "true" : "false", Value(p, "alreadyOpen"));
            Assert.Equal(2, p.Count);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ExitPayload_MirrorsEnter(bool already)
        {
            List<KeyValuePair<string, string>> p =
                TestCommandMapViewVerbs.BuildExitPayload(already);
            Assert.Equal("false", Value(p, "mapOpen"));
            Assert.Equal(already ? "true" : "false", Value(p, "alreadyClosed"));
            Assert.Equal(2, p.Count);
        }

        [Fact]
        public void Payloads_UseTheSameMapOpenKeyWithOppositeValues()
        {
            // The one key a lane can read without knowing which verb produced the response.
            Assert.Equal("true",
                Value(TestCommandMapViewVerbs.BuildEnterPayload(false), "mapOpen"));
            Assert.Equal("false",
                Value(TestCommandMapViewVerbs.BuildExitPayload(false), "mapOpen"));
        }

        private static string Value(List<KeyValuePair<string, string>> payload, string key)
        {
            Assert.Contains(payload, kv => kv.Key == key);
            return payload.First(kv => kv.Key == key).Value;
        }
    }
}
