using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostCommNetRelayTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostCommNetRelayTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region ComputeCombinedAntennaPower

        /// <summary>
        /// Single antenna returns its power directly.
        /// Guards: simplest case, no combinability logic needed.
        /// </summary>
        [Fact]
        public void ComputeCombinedPower_SingleAntenna_ReturnsPower()
        {
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec
                {
                    partName = "longAntenna",
                    antennaPower = 500000,
                    antennaCombinable = true,
                    antennaCombinableExponent = 0.75
                }
            };

            double result = GhostCommNetRelay.ComputeCombinedAntennaPower(specs);

            Assert.Equal(500000.0, result);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("single antenna") &&
                l.Contains("500000"));
        }

        /// <summary>
        /// Multiple combinable antennas combine using KSP's formula:
        /// strongest + sum(other * (other/strongest)^exponent).
        /// Guards: combinability math produces correct total.
        /// </summary>
        [Fact]
        public void ComputeCombinedPower_MultipleAntennas_CombinesCorrectly()
        {
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec
                {
                    partName = "longAntenna",
                    antennaPower = 500000,
                    antennaCombinable = true,
                    antennaCombinableExponent = 0.75
                },
                new AntennaSpec
                {
                    partName = "longAntenna",
                    antennaPower = 500000,
                    antennaCombinable = true,
                    antennaCombinableExponent = 0.75
                }
            };

            double result = GhostCommNetRelay.ComputeCombinedAntennaPower(specs);

            // strongest = 500000
            // other = 500000, ratio = 1.0, contribution = 500000 * 1.0^0.75 = 500000
            // total = 500000 + 500000 = 1000000
            Assert.Equal(1000000.0, result, 5);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("combined="));
        }

        /// <summary>
        /// Empty list returns 0.
        /// Guards: no antennas = no power.
        /// </summary>
        [Fact]
        public void ComputeCombinedPower_EmptyList_ReturnsZero()
        {
            var specs = new List<AntennaSpec>();

            double result = GhostCommNetRelay.ComputeCombinedAntennaPower(specs);

            Assert.Equal(0.0, result);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("null/empty"));
        }

        /// <summary>
        /// Null list returns 0.
        /// Guards: null safety.
        /// </summary>
        [Fact]
        public void ComputeCombinedPower_NullList_ReturnsZero()
        {
            double result = GhostCommNetRelay.ComputeCombinedAntennaPower(null);

            Assert.Equal(0.0, result);
        }

        /// <summary>
        /// Non-combinable antennas: only strongest power is returned.
        /// Guards: combinability flag correctly gates the combination formula.
        /// </summary>
        [Fact]
        public void ComputeCombinedPower_NonCombinable_ReturnsStrongest()
        {
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec
                {
                    partName = "antenna1",
                    antennaPower = 100000,
                    antennaCombinable = false,
                    antennaCombinableExponent = 0.75
                },
                new AntennaSpec
                {
                    partName = "antenna2",
                    antennaPower = 500000,
                    antennaCombinable = false,
                    antennaCombinableExponent = 0.75
                }
            };

            double result = GhostCommNetRelay.ComputeCombinedAntennaPower(specs);

            Assert.Equal(500000.0, result);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("no combinable"));
        }

        /// <summary>
        /// Mixed combinable/non-combinable: only combinable ones participate in sum.
        /// Guards: per-antenna combinability flag is checked individually.
        /// </summary>
        [Fact]
        public void ComputeCombinedPower_MixedCombinability_OnlyCombinableContribute()
        {
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec
                {
                    partName = "relay",
                    antennaPower = 1000000,
                    antennaCombinable = true,
                    antennaCombinableExponent = 0.75
                },
                new AntennaSpec
                {
                    partName = "internal",
                    antennaPower = 5000,
                    antennaCombinable = false,
                    antennaCombinableExponent = 0.75
                },
                new AntennaSpec
                {
                    partName = "relay2",
                    antennaPower = 500000,
                    antennaCombinable = true,
                    antennaCombinableExponent = 0.75
                }
            };

            double result = GhostCommNetRelay.ComputeCombinedAntennaPower(specs);

            // strongest = 1000000 (relay)
            // internal (5000): non-combinable, skipped
            // relay2 (500000): combinable, ratio = 0.5, contribution = 500000 * 0.5^0.75
            double expectedContribution = 500000.0 * Math.Pow(0.5, 0.75);
            double expected = 1000000.0 + expectedContribution;
            Assert.Equal(expected, result, 5);
        }

        /// <summary>
        /// Asymmetric antenna powers combine correctly with the formula.
        /// Guards: ratio computation (other/strongest) works for different power levels.
        /// </summary>
        [Fact]
        public void ComputeCombinedPower_AsymmetricPowers_CombinesCorrectly()
        {
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec
                {
                    partName = "bigRelay",
                    antennaPower = 2000000000, // 2G (RA-100)
                    antennaCombinable = true,
                    antennaCombinableExponent = 0.75
                },
                new AntennaSpec
                {
                    partName = "smallAntenna",
                    antennaPower = 500000, // 500k
                    antennaCombinable = true,
                    antennaCombinableExponent = 0.75
                }
            };

            double result = GhostCommNetRelay.ComputeCombinedAntennaPower(specs);

            // strongest = 2G
            // small: ratio = 500000/2000000000 = 0.00025
            // contribution = 500000 * 0.00025^0.75
            double ratio = 500000.0 / 2000000000.0;
            double contribution = 500000.0 * Math.Pow(ratio, 0.75);
            double expected = 2000000000.0 + contribution;
            Assert.Equal(expected, result, 1);
        }

        /// <summary>
        /// All antennas with zero power returns 0.
        /// Guards: zero-power edge case doesn't cause division by zero.
        /// </summary>
        [Fact]
        public void ComputeCombinedPower_AllZeroPower_ReturnsZero()
        {
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec { partName = "a1", antennaPower = 0, antennaCombinable = true },
                new AntennaSpec { partName = "a2", antennaPower = 0, antennaCombinable = true }
            };

            double result = GhostCommNetRelay.ComputeCombinedAntennaPower(specs);

            Assert.Equal(0.0, result);
        }

        #endregion

        #region ShouldRegisterCommNet

        /// <summary>
        /// Vessel with antennas should register for CommNet.
        /// Guards: basic CommNet eligibility check.
        /// </summary>
        [Fact]
        public void ShouldRegisterCommNet_HasAntennas_True()
        {
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec
                {
                    partName = "longAntenna",
                    antennaPower = 500000,
                    antennaCombinable = true,
                    antennaCombinableExponent = 0.75
                }
            };

            bool result = GhostCommNetRelay.ShouldRegisterCommNet(specs);

            Assert.True(result);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("ShouldRegisterCommNet") &&
                l.Contains("true"));
        }

        /// <summary>
        /// Vessel with no antennas should not register for CommNet.
        /// Guards: empty antenna list correctly returns false.
        /// </summary>
        [Fact]
        public void ShouldRegisterCommNet_NoAntennas_False()
        {
            var specs = new List<AntennaSpec>();

            bool result = GhostCommNetRelay.ShouldRegisterCommNet(specs);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("null/empty"));
        }

        /// <summary>
        /// Null antenna list returns false.
        /// Guards: null safety for recordings without extracted antenna data.
        /// </summary>
        [Fact]
        public void ShouldRegisterCommNet_NullList_False()
        {
            bool result = GhostCommNetRelay.ShouldRegisterCommNet(null);

            Assert.False(result);
        }

        /// <summary>
        /// Antennas with zero power should not register for CommNet.
        /// Guards: zero-power antennas are not useful for relay.
        /// </summary>
        [Fact]
        public void ShouldRegisterCommNet_ZeroPowerAntennas_False()
        {
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec { partName = "broken", antennaPower = 0 }
            };

            bool result = GhostCommNetRelay.ShouldRegisterCommNet(specs);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostCommNet]") && l.Contains("zero power"));
        }

        /// <summary>
        /// Mixed zero and nonzero power: should register if any has positive power.
        /// Guards: one working antenna is enough for CommNet registration.
        /// </summary>
        [Fact]
        public void ShouldRegisterCommNet_MixedPower_TrueIfAnyPositive()
        {
            var specs = new List<AntennaSpec>
            {
                new AntennaSpec { partName = "broken", antennaPower = 0 },
                new AntennaSpec { partName = "working", antennaPower = 500000 }
            };

            bool result = GhostCommNetRelay.ShouldRegisterCommNet(specs);

            Assert.True(result);
        }

        #endregion
    }
}
