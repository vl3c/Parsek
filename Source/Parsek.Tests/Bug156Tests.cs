using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #156: pad-failure threshold tests and CacheEngineModules null-safety.
    /// </summary>
    [Collection("Sequential")]
    public class Bug156_PadFailureThresholdTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug156_PadFailureThresholdTests()
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

        // ────────────────────────────────────────────────────────────
        //  IsPadFailure — pad-failure discard threshold
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void IsPadFailure_ShortDurationAndCloseDistance_ReturnsTrue()
        {
            // Bug #156: 5s duration, 20m from launch — both under thresholds (10s, 30m)
            Assert.True(ParsekFlight.IsPadFailure(5.0, 20.0));
        }

        [Fact]
        public void IsPadFailure_LongDuration_ReturnsFalse()
        {
            // Bug #156: 15s duration exceeds 10s threshold — not a pad failure
            Assert.False(ParsekFlight.IsPadFailure(15.0, 20.0));
        }

        [Fact]
        public void IsPadFailure_FarDistance_ReturnsFalse()
        {
            // Bug #156: 50m distance exceeds 30m threshold — not a pad failure
            Assert.False(ParsekFlight.IsPadFailure(5.0, 50.0));
        }

        [Fact]
        public void IsPadFailure_ExactThresholds_ReturnsFalse()
        {
            // Boundary: exactly 10s and 30m — strict less-than means not a pad failure
            Assert.False(ParsekFlight.IsPadFailure(10.0, 30.0));
        }

        [Fact]
        public void IsPadFailure_BothExceedThresholds_ReturnsFalse()
        {
            Assert.False(ParsekFlight.IsPadFailure(15.0, 50.0));
        }

        [Fact]
        public void IsPadFailure_ZeroDurationAndZeroDistance_ReturnsTrue()
        {
            // Edge case: immediate destruction at spawn point
            Assert.True(ParsekFlight.IsPadFailure(0.0, 0.0));
        }

        // ────────────────────────────────────────────────────────────
        //  CacheEngineModules — null-vessel guard
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void CacheEngineModules_NullVessel_ReturnsEmptyList()
        {
            // Bug #156: null vessel must not crash, must return empty list
            var result = FlightRecorder.CacheEngineModules(null);
            Assert.Empty(result);
        }
    }
}
