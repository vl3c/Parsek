using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Log-assertion tests for the BOUNDARY-OVERLAP launch render
    /// (docs/dev/plan-launch-boundary-overlap.md 2.3, section 9 "Logging"): the per-loop Verbose line carries
    /// secondaryActive=true + both loopUTs and residualDeg ~ 0 on a zero-slack loop; the one-shot
    /// `boundary-overlap engaged` line fires; the `launch-advance-capped` WARN no longer fires on the engaged loop
    /// (the seam closes). Drives the actual <see cref="GhostPlaybackLogic.ComputeSpanLoopFrame"/> so the lines are
    /// proven to propagate, not merely returned by the pure helper.
    /// </summary>
    [Collection("Sequential")]
    public class BoundaryOverlapLoggingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public BoundaryOverlapLoggingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GhostPlaybackLogic.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ParsekLog.ResetRateLimitsForTesting();
            GhostPlaybackLogic.ResetForTesting();
        }

        // ZERO-SLACK fixture (boundary overlap always engages): span [0,1000], cadence == span == 1000, no hold,
        // phaseAnchor 300, T_sid 700, SOI exit 600. delta_N steps 300, 600, 200, ... all > slack(0).
        private const double Anchor = 300, S0 = 0, S1 = 1000, Cad = 1000, Tsid = 700, SoiExit = 600;

        private void Sample(double currentUT)
        {
            GhostPlaybackLogic.ComputeSpanLoopFrame(
                currentUT, Anchor, S0, S1, Cad,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: Tsid, launchHoldEngaged: true,
                soiExitAtUT: SoiExit);
        }

        private static double RawDelta(long n)
            => GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(Anchor, S0, n, Cad, Tsid);

        [Fact]
        public void BorrowWindow_PerLoopLine_CarriesSecondaryActiveAndResidualZero()
        {
            // Probe inside cycle 1's borrow window (secondary = instance 2): the per-loop line reports
            // secondaryActive=true with a secondaryLoopUT, and residualDeg ~ 0 (the boundary overlap uses the full
            // raw delta for the rendered instance, so the seam closes).
            double advNext = RawDelta(2);
            Sample(Anchor + 1 * Cad + (Cad - advNext) + 50.0);

            var launchLines = logLines
                .Where(l => l.Contains("[Reaim]") && l.Contains("per-loop launch advance:"))
                .ToList();
            Assert.NotEmpty(launchLines);
            Assert.Contains(launchLines, l => l.Contains("secondaryActive=True"));
            Assert.Contains(launchLines, l => l.Contains("secondaryLoopUT=") && !l.Contains("secondaryLoopUT=(none)"));
            // residualDeg ~ 0 (the seam closed). Parse the value and assert near 0.
            var residualLine = launchLines.First(l => l.Contains("residualDeg="));
            double residual = ParseField(residualLine, "residualDeg=");
            Assert.True(Math.Abs(residual) < 1e-3 || Math.Abs(residual - 360.0) < 1e-3,
                $"expected residualDeg ~ 0 on the engaged loop, got {residual}\n{residualLine}");
        }

        [Fact]
        public void EngagedLoop_BoundaryOverlapEngagedLine_Fires()
        {
            // The one-shot per-mission `boundary-overlap engaged` line fires on an engaged loop (region A or the
            // borrow window). Probe region A of cycle 1 (engaged because slack 0).
            Sample(Anchor + 1 * Cad + 200.0);
            Assert.Contains(logLines, l => l.Contains("[Reaim]") && l.Contains("boundary-overlap engaged:"));
        }

        [Fact]
        public void EngagedLoop_NoLaunchAdvanceCappedWarn()
        {
            // The launch-advance-capped WARN no longer fires on an engaged loop: the boundary overlap uses the
            // full raw delta, so the seam closes (renderedCapped is false). Sweep several cycles.
            for (double t = Anchor; t <= Anchor + 5.0 * Cad; t += 37.0)
            {
                Sample(t);
                ParsekLog.ResetRateLimitsForTesting();
            }
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN]") && l.Contains("launch-advance"));
            Assert.DoesNotContain(logLines, l => l.Contains("CAPPED"));
        }

        [Fact]
        public void AlignedLoop_NoSecondaryActive_NoBoundaryOverlapLine()
        {
            // On the ALIGNED fixture (slack 1000, raw delta <= 300) the boundary overlap never engages: no
            // secondaryActive=True, no `boundary-overlap engaged` line.
            const double aAnchor = 300, aCad = 2000;
            for (double t = aAnchor; t <= aAnchor + 4.0 * aCad; t += 41.0)
            {
                GhostPlaybackLogic.ComputeSpanLoopFrame(
                    t, aAnchor, S0, S1, aCad,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                    arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: Tsid, launchHoldEngaged: true,
                    soiExitAtUT: SoiExit);
                ParsekLog.ResetRateLimitsForTesting();
            }
            Assert.DoesNotContain(logLines, l => l.Contains("secondaryActive=True"));
            Assert.DoesNotContain(logLines, l => l.Contains("boundary-overlap engaged:"));
        }

        private static double ParseField(string line, string key)
        {
            int idx = line.IndexOf(key, StringComparison.Ordinal);
            Assert.True(idx >= 0, $"field {key} not in line: {line}");
            int start = idx + key.Length;
            int end = start;
            while (end < line.Length && !char.IsWhiteSpace(line[end]))
                end++;
            string token = line.Substring(start, end - start);
            return double.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
