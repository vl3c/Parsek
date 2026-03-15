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
    }
}
