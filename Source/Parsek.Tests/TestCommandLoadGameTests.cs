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
                gamePresent: true, flightStatePresent: true, protoVesselsPresent: true,
                activeVesselIdx: 0, protoVesselCount: 3));
        }

        [Fact]
        public void NotFocusable_NullGame()
        {
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(false, false, false, 0, 0));
        }

        [Fact]
        public void NotFocusable_NullFlightState_OrProtoVessels()
        {
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(true, false, false, 0, 1));
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(true, true, false, 0, 1));
        }

        [Fact]
        public void NotFocusable_IdxOutOfRange()
        {
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(true, true, true, -1, 2));
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(true, true, true, 2, 2));
            Assert.False(TestCommandLoadGame.IsLoadedGameFocusable(true, true, true, 5, 2));
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
    }
}
