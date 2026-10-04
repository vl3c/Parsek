using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the seam's shared active-vessel-loss guard
    /// (<see cref="TestCommandActiveVesselLoss"/>): which pending verbs are watched, what
    /// counts as lost (a destroyed reference OR stock's DEAD state, which is all a dead
    /// ACTIVE vessel ever shows), and the one Warn line the guard writes.
    /// </summary>
    [Collection("Sequential")]
    public class TestCommandActiveVesselLossTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TestCommandActiveVesselLossTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void WatchedSet_IsTheTwoKerbalVerbs()
        {
            Assert.Equal(new[] { "EvaGroundScience", "PlantFlag" },
                TestCommandActiveVesselLoss.WatchedVerbList.OrderBy(v => v, StringComparer.Ordinal).ToArray());
        }

        [Theory]
        [InlineData("EvaGroundScience", true)]
        [InlineData("PlantFlag", true)]
        // Owns its own debounced eva-chute-kerbal-lost terminal.
        [InlineData("EvaChuteDeploy", false)]
        // Boarding removes the EVA vessel on its own success path.
        [InlineData("EvaBoard", false)]
        // Switches focus to the new kerbal.
        [InlineData("EvaExit", false)]
        // Scene-leaving / scene-reloading verbs.
        [InlineData("Recover", false)]
        [InlineData("ExitToSpaceCenter", false)]
        [InlineData("LoadGame", false)]
        [InlineData("InvokeRewind", false)]
        [InlineData("WarpToUT", false)]
        [InlineData("RunTests", false)]
        [InlineData("evagroundscience", false)]
        [InlineData(null, false)]
        public void WatchesActiveVessel_OnlyForWatchedVerbs(string verb, bool expected)
        {
            Assert.Equal(expected, TestCommandActiveVesselLoss.WatchesActiveVessel(verb));
        }

        [Theory]
        // A dead ACTIVE vessel keeps its GameObject (stock Vessel.Die), so DEAD alone is loss.
        [InlineData(true, true, true)]
        // A destroyed GameObject reads Unity-null.
        [InlineData(false, false, true)]
        [InlineData(true, false, false)]
        public void IsLost_DeadOrDestroyed(bool referenceAlive, bool stateDead, bool expected)
        {
            Assert.Equal(expected, TestCommandActiveVesselLoss.IsLost(referenceAlive, stateDead));
        }

        [Fact]
        public void ShouldFailFast_EvaStepMove_KerbalDead_Fails()
        {
            // The EVA-8 2026-09-29 shape: Jeb exploded mid step-move, the vessel object
            // stayed live as the dead active vessel.
            Assert.True(TestCommandActiveVesselLoss.ShouldFailFast(
                "EvaGroundScience", watchArmed: true, referenceAlive: true, stateDead: true));
        }

        [Fact]
        public void ShouldFailFast_AliveKerbal_KeepsWaiting()
        {
            Assert.False(TestCommandActiveVesselLoss.ShouldFailFast(
                "EvaGroundScience", watchArmed: true, referenceAlive: true, stateDead: false));
            Assert.False(TestCommandActiveVesselLoss.ShouldFailFast(
                "PlantFlag", watchArmed: true, referenceAlive: true, stateDead: false));
        }

        [Fact]
        public void ShouldFailFast_UnarmedWatch_NeverFails()
        {
            // No active vessel was captured at initiation: the guard has nothing to judge.
            Assert.False(TestCommandActiveVesselLoss.ShouldFailFast(
                "EvaGroundScience", watchArmed: false, referenceAlive: false, stateDead: false));
        }

        [Fact]
        public void ShouldFailFast_UnwatchedVerb_NeverFails()
        {
            Assert.False(TestCommandActiveVesselLoss.ShouldFailFast(
                "EvaBoard", watchArmed: true, referenceAlive: false, stateDead: false));
            Assert.False(TestCommandActiveVesselLoss.ShouldFailFast(
                "WarpToUT", watchArmed: true, referenceAlive: true, stateDead: true));
        }

        [Fact]
        public void LossKind_NamesTheShape()
        {
            Assert.Equal("destroyed", TestCommandActiveVesselLoss.LossKind(false, false));
            Assert.Equal("dead", TestCommandActiveVesselLoss.LossKind(true, true));
            Assert.Equal("alive", TestCommandActiveVesselLoss.LossKind(true, false));
        }

        [Fact]
        public void Reason_IsTheWireToken()
        {
            Assert.Equal("active-vessel-lost", TestCommandActiveVesselLoss.Reason);
        }

        [Fact]
        public void FormatLossLine_WritesOneGreppableWarn_InvariantCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                ParsekLog.Warn("TestCommands", TestCommandActiveVesselLoss.FormatLossLine(
                    "0022", "EvaGroundScience", "Jebediah Kerman", 3263810799u,
                    referenceAlive: true, stateDead: true, elapsedSeconds: 12.34));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }

            Assert.Single(logLines);
            string line = logLines[0];
            Assert.Contains("[WARN][TestCommands]", line);
            Assert.Contains("active-vessel lost id=0022 cmd=EvaGroundScience vessel=Jebediah Kerman "
                + "pid=3263810799 state=dead elapsed=12.3s reason=active-vessel-lost", line);
        }
    }
}
