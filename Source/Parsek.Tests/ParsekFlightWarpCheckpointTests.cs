using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ParsekFlightWarpCheckpointTests
    {
        [Fact]
        public void RecalculateLedgerAfterWarpExit_InvokesCutoffPathOnly()
        {
            var logLines = new List<string>();
            double capturedCutoff = double.NaN;
            int cutoffCalls = 0;

            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                ParsekFlight.RecalculateLedgerAfterWarpExit(
                    1234.5,
                    cutoff =>
                    {
                        capturedCutoff = cutoff;
                        cutoffCalls++;
                    });
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }

            Assert.Equal(1, cutoffCalls);
            Assert.Equal(1234.5, capturedCutoff);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][LedgerOrchestrator]")
                && l.Contains("Warp exit ledger recalculation started")
                && l.Contains("cutoffUT=1234.5"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][LedgerOrchestrator]")
                && l.Contains("Warp exit ledger recalculation complete")
                && l.Contains("cutoffUT=1234.5"));
        }

        [Fact]
        public void RecalculateLedgerAfterWarpExit_LogsAndSwallowsRecalculationFailure()
        {
            var logLines = new List<string>();

            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                ParsekFlight.RecalculateLedgerAfterWarpExit(
                    1234.5,
                    _ => throw new InvalidOperationException("boom"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }

            Assert.Contains(logLines, l =>
                l.Contains("[WARN][LedgerOrchestrator]")
                && l.Contains("Warp exit ledger recalculation failed")
                && l.Contains("cutoffUT=1234.5")
                && l.Contains("InvalidOperationException"));
        }

        [Fact]
        public void ShouldSkipDuplicateWarpCheckpointEvent_FirstEvent_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldSkipDuplicateWarpCheckpointEvent(
                currentWarpRate: 1.0f,
                currentUT: 100.0,
                hasLastEvent: false,
                lastWarpRate: 1.0f,
                lastUT: 100.0));
        }

        [Fact]
        public void ShouldSkipDuplicateWarpCheckpointEvent_SameRateSameUt_ReturnsTrue()
        {
            Assert.True(ParsekFlight.ShouldSkipDuplicateWarpCheckpointEvent(
                currentWarpRate: 1.0f,
                currentUT: 100.0,
                hasLastEvent: true,
                lastWarpRate: 1.0f,
                lastUT: 100.0));
        }

        [Fact]
        public void ShouldSkipDuplicateWarpCheckpointEvent_SameRateAdvancedUt_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldSkipDuplicateWarpCheckpointEvent(
                currentWarpRate: 1.0f,
                currentUT: 101.0,
                hasLastEvent: true,
                lastWarpRate: 1.0f,
                lastUT: 100.0));
        }

        [Fact]
        public void ShouldSkipDuplicateWarpCheckpointEvent_ChangedRateSameUt_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldSkipDuplicateWarpCheckpointEvent(
                currentWarpRate: 2.0f,
                currentUT: 100.0,
                hasLastEvent: true,
                lastWarpRate: 1.0f,
                lastUT: 100.0));
        }

        [Fact]
        public void ShouldSkipDuplicateWarpCheckpointEvent_NonFiniteInputs_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldSkipDuplicateWarpCheckpointEvent(
                currentWarpRate: float.NaN,
                currentUT: 100.0,
                hasLastEvent: true,
                lastWarpRate: 1.0f,
                lastUT: 100.0));
        }
    }
}
