using System;
using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Pins the L4 per-frame spline-eval summary line (design doc §19.2 Stage 1
    /// row 4): every successful spline evaluation increments a process-wide
    /// counter; once per second the counter flushes to a Verbose
    /// Pipeline-Smoothing line and resets. Distinct from VerboseRateLimited
    /// (which suppresses repeated lines and loses the count).
    /// </summary>
    [Collection("Sequential")]
    public class SplineEvalSummaryLogTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SplineEvalSummaryLogTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;  // L4 emits at Verbose
            ParsekFlight.ResetSplineEvalLoggingForTesting();
        }

        public void Dispose()
        {
            ParsekFlight.ResetSplineEvalLoggingForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void RecordSplineEvalForLogging_BelowOneSecond_NoEmit()
        {
            // What makes it fail: the L4 gate fires too often (every eval) and
            // floods KSP.log with per-frame chatter.
            DateTime baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            int call = 0;
            ParsekFlight.NowProviderForTesting = () => baseTime.AddMilliseconds(call++ * 10);

            for (int i = 0; i < 50; i++)
                ParsekFlight.RecordSplineEvalForLogging();

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Pipeline-Smoothing]") && l.Contains("frame summary: splineEvals="));
        }

        [Fact]
        public void RecordSplineEvalForLogging_OneSecondElapsed_EmitsOnce()
        {
            // What makes it fail: the gate never fires, so L4 silently never
            // emits and the per-frame budget visibility is lost.
            DateTime baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            int call = 0;
            // First call registers the start time. Subsequent calls advance
            // by 50 ms; after ~21 calls (1.05 s) the gate should fire.
            ParsekFlight.NowProviderForTesting = () => baseTime.AddMilliseconds(call++ * 50);

            for (int i = 0; i < 25; i++)
                ParsekFlight.RecordSplineEvalForLogging();

            int summaryLines = 0;
            foreach (string l in logLines)
            {
                if (l.Contains("[Pipeline-Smoothing]") && l.Contains("frame summary: splineEvals="))
                    summaryLines++;
            }
            Assert.Equal(1, summaryLines);
        }

        [Fact]
        public void RecordSplineEvalForLogging_CounterResetsAfterEmit()
        {
            // What makes it fail: counter doesn't reset, so subsequent emits
            // accumulate the entire process history instead of per-window
            // counts.
            DateTime baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            int call = 0;
            ParsekFlight.NowProviderForTesting = () => baseTime.AddMilliseconds(call++ * 50);

            // Drive 25 evals to fire the first emit (around the 21st call), then
            // continue another 25 evals to fire a second emit. Each emit's
            // splineEvals=N should reflect only the count since the previous
            // emit, not the cumulative total.
            for (int i = 0; i < 50; i++)
                ParsekFlight.RecordSplineEvalForLogging();

            int summaryLines = 0;
            foreach (string l in logLines)
            {
                if (l.Contains("[Pipeline-Smoothing]") && l.Contains("frame summary: splineEvals="))
                    summaryLines++;
            }
            Assert.Equal(2, summaryLines);
            // Cumulative-style emits would produce something like splineEvals=50
            // on the second flush. With reset, the second flush should report
            // ~half as many. Assert that no emitted line claims more than 50
            // (a loose bound — exact counts depend on the gate's timing).
            foreach (string l in logLines)
            {
                if (l.Contains("[Pipeline-Smoothing]") && l.Contains("frame summary: splineEvals="))
                {
                    int idx = l.IndexOf("splineEvals=", StringComparison.Ordinal) + "splineEvals=".Length;
                    int end = l.IndexOf(' ', idx);
                    if (end < 0) end = l.Length;
                    int count = int.Parse(l.Substring(idx, end - idx));
                    Assert.InRange(count, 1, 30);
                }
            }
        }
    }
}
