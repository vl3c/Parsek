using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>LoadGame focusPid=</c>: fly the save's vessel with this persistentId (the Tracking
    /// Station Fly button's <c>StartAndFocusVessel(save, index)</c>), since a save written
    /// outside FLIGHT stores <c>activeVessel = 0</c>.
    /// </summary>
    public class TestCommandLoadGameFocusTests
    {
        [Theory]
        [InlineData(null, true, 0u, false)]
        [InlineData("3620499050", true, 3620499050u, true)]
        [InlineData("", false, 0u, false)]
        [InlineData("0", false, 0u, false)]
        [InlineData("abc", false, 0u, false)]
        [InlineData("+5", false, 0u, false)]
        [InlineData("4294967296", false, 0u, false)]
        public void TryParseFocusPid_IsTheRecoverGrammar_AbsentIsNoFocus(
            string raw, bool ok, uint pid, bool hasFocus)
        {
            Assert.Equal(ok, TestCommandLoadGame.TryParseFocusPid(raw, out uint got, out bool has));
            Assert.Equal(pid, got);
            Assert.Equal(hasFocus, has);
        }

        [Fact]
        public void ResolveFocusIndex_OneMatchIsItsIndex_NoneOrSeveralIsMinusOne()
        {
            var pids = new List<uint> { 3722070064, 3264961987, 3620499050, 788309716 };
            Assert.Equal(2, TestCommandLoadGame.ResolveFocusIndex(pids, 3620499050, out int m1));
            Assert.Equal(1, m1);
            Assert.Equal(-1, TestCommandLoadGame.ResolveFocusIndex(pids, 42, out int m0));
            Assert.Equal(0, m0);
            var dup = new List<uint> { 7, 9, 7 };
            Assert.Equal(-1, TestCommandLoadGame.ResolveFocusIndex(dup, 7, out int m2));
            Assert.Equal(2, m2);
            Assert.Equal(-1, TestCommandLoadGame.ResolveFocusIndex(null, 7, out int mn));
            Assert.Equal(0, mn);
        }

        [Fact]
        public void DecideFocus_InGateOrder()
        {
            Assert.Null(TestCommandLoadGame.DecideFocus(false, RequestedBootScene.TrackingStation, 0));
            Assert.Equal("focus-pid-scene-conflict",
                TestCommandLoadGame.DecideFocus(true, RequestedBootScene.TrackingStation, 1));
            Assert.Equal("focus-pid-scene-conflict",
                TestCommandLoadGame.DecideFocus(true, RequestedBootScene.SpaceCenter, 0));
            Assert.Equal("focus-pid-not-found",
                TestCommandLoadGame.DecideFocus(true, RequestedBootScene.Unspecified, 0));
            Assert.Equal("focus-pid-ambiguous",
                TestCommandLoadGame.DecideFocus(true, RequestedBootScene.Unspecified, 2));
            Assert.Null(TestCommandLoadGame.DecideFocus(true, RequestedBootScene.Unspecified, 1));
        }

        [Fact]
        public void TheKeyAndReasonsAreTheWireTokens()
        {
            Assert.Equal("focusPid", TestCommandLoadGame.FocusPidKey);
            Assert.Equal("focus-pid-arg-invalid", TestCommandLoadGame.FocusPidArgInvalidReason);
            Assert.Equal("focus-pid-scene-conflict", TestCommandLoadGame.FocusPidSceneConflictReason);
            Assert.Equal("focus-pid-not-found", TestCommandLoadGame.FocusPidNotFoundReason);
            Assert.Equal("focus-pid-ambiguous", TestCommandLoadGame.FocusPidAmbiguousReason);
        }
    }
}
