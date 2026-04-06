using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for methods extracted from VesselSpawner:
    /// DetermineSituation (pure decision logic).
    /// Also verifies logging added to spawn paths.
    /// </summary>
    [Collection("Sequential")]
    public class VesselSpawnerExtractedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public VesselSpawnerExtractedTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        #region DetermineSituation

        [Fact]
        public void DetermineSituation_NegativeAlt_OverWater_ReturnsSplashed()
        {
            string result = VesselSpawner.DetermineSituation(
                alt: -5.0, overWater: true, speed: 0, orbitalSpeed: 2200);
            Assert.Equal("SPLASHED", result);
        }

        [Fact]
        public void DetermineSituation_ZeroAlt_OverWater_ReturnsSplashed()
        {
            string result = VesselSpawner.DetermineSituation(
                alt: 0, overWater: true, speed: 0, orbitalSpeed: 2200);
            Assert.Equal("SPLASHED", result);
        }

        [Fact]
        public void DetermineSituation_NegativeAlt_NotOverWater_ReturnsLanded()
        {
            string result = VesselSpawner.DetermineSituation(
                alt: -2.0, overWater: false, speed: 0, orbitalSpeed: 2200);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void DetermineSituation_ZeroAlt_NotOverWater_ReturnsLanded()
        {
            string result = VesselSpawner.DetermineSituation(
                alt: 0, overWater: false, speed: 10, orbitalSpeed: 2200);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void DetermineSituation_HighAlt_HighSpeed_ReturnsOrbiting()
        {
            // speed > orbitalSpeed * 0.9 -> ORBITING
            double orbitalSpeed = 2200;
            double speed = orbitalSpeed * 0.95;
            string result = VesselSpawner.DetermineSituation(
                alt: 70000, overWater: false, speed: speed, orbitalSpeed: orbitalSpeed);
            Assert.Equal("ORBITING", result);
        }

        [Fact]
        public void DetermineSituation_HighAlt_ExactThreshold_ReturnsOrbiting()
        {
            // speed == orbitalSpeed * 0.9 + epsilon -> ORBITING
            double orbitalSpeed = 2200;
            double speed = orbitalSpeed * 0.9 + 0.001;
            string result = VesselSpawner.DetermineSituation(
                alt: 70000, overWater: false, speed: speed, orbitalSpeed: orbitalSpeed);
            Assert.Equal("ORBITING", result);
        }

        [Fact]
        public void DetermineSituation_HighAlt_LowSpeed_ReturnsFlying()
        {
            // speed < orbitalSpeed * 0.9 -> FLYING
            double orbitalSpeed = 2200;
            double speed = orbitalSpeed * 0.5;
            string result = VesselSpawner.DetermineSituation(
                alt: 5000, overWater: false, speed: speed, orbitalSpeed: orbitalSpeed);
            Assert.Equal("FLYING", result);
        }

        [Fact]
        public void DetermineSituation_HighAlt_ZeroSpeed_ReturnsFlying()
        {
            string result = VesselSpawner.DetermineSituation(
                alt: 100, overWater: false, speed: 0, orbitalSpeed: 2200);
            Assert.Equal("FLYING", result);
        }

        [Fact]
        public void DetermineSituation_SplashedTakesPriorityOverLanded()
        {
            // When alt <= 0 AND overWater, SPLASHED wins over LANDED
            string result = VesselSpawner.DetermineSituation(
                alt: 0, overWater: true, speed: 100, orbitalSpeed: 2200);
            Assert.Equal("SPLASHED", result);
        }

        [Fact]
        public void DetermineSituation_BoundaryAlt_PositiveSmall_Flying()
        {
            // alt > 0, slow speed -> FLYING (not LANDED)
            string result = VesselSpawner.DetermineSituation(
                alt: 0.1, overWater: false, speed: 5, orbitalSpeed: 2200);
            Assert.Equal("FLYING", result);
        }

        #endregion

        #region ComputeCorrectedSituation

        [Fact]
        public void ComputeCorrectedSituation_FlyingToLanded()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Landed);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_FlyingToSplashed()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Splashed);
            Assert.Equal("SPLASHED", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_FlyingToOrbiting()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Orbiting);
            Assert.Equal("ORBITING", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_SubOrbitalToOrbiting()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("SUB_ORBITAL", TerminalState.Orbiting);
            Assert.Equal("ORBITING", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_SubOrbitalToLanded()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("SUB_ORBITAL", TerminalState.Landed);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_NoCorrection_SafeSituation()
        {
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("LANDED", TerminalState.Landed));
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("ORBITING", TerminalState.Orbiting));
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("SPLASHED", TerminalState.Splashed));
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("PRELAUNCH", TerminalState.Landed));
        }

        [Fact]
        public void ComputeCorrectedSituation_NoCorrection_DestroyedTerminal()
        {
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Destroyed));
        }

        [Fact]
        public void ComputeCorrectedSituation_NoCorrection_NullTerminal()
        {
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("FLYING", null));
        }

        [Fact]
        public void ComputeCorrectedSituation_NoCorrection_NullOrEmptySit()
        {
            Assert.Null(VesselSpawner.ComputeCorrectedSituation(null, TerminalState.Orbiting));
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("", TerminalState.Orbiting));
        }

        [Fact]
        public void ComputeCorrectedSituation_CaseInsensitive()
        {
            Assert.Equal("ORBITING", VesselSpawner.ComputeCorrectedSituation("flying", TerminalState.Orbiting));
            Assert.Equal("LANDED", VesselSpawner.ComputeCorrectedSituation("sub_orbital", TerminalState.Landed));
        }

        #endregion

        #region ShouldZeroVelocityAfterSpawn (#239)

        [Theory]
        [InlineData("LANDED", true)]
        [InlineData("SPLASHED", true)]
        [InlineData("PRELAUNCH", true)]
        [InlineData("ORBITING", false)]
        [InlineData("FLYING", false)]
        [InlineData("SUB_ORBITAL", false)]
        [InlineData("ESCAPING", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void ShouldZeroVelocityAfterSpawn_CorrectForSituation(string sit, bool expected)
        {
            Assert.Equal(expected, VesselSpawner.ShouldZeroVelocityAfterSpawn(sit));
        }

        [Fact]
        public void ShouldZeroVelocityAfterSpawn_CaseInsensitive()
        {
            Assert.True(VesselSpawner.ShouldZeroVelocityAfterSpawn("landed"));
            Assert.True(VesselSpawner.ShouldZeroVelocityAfterSpawn("Splashed"));
            Assert.True(VesselSpawner.ShouldZeroVelocityAfterSpawn("prelaunch"));
        }

        #endregion
    }
}
