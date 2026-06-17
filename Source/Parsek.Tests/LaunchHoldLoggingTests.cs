using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Log-assertion + wiring tests for the per-loop LAUNCH HOLD
    /// (docs/dev/design-reaim-launch-hold-seam.md section 9.3). Two halves:
    ///
    /// 1. The per-loop Verbose Reaim launch-hold line is PROVEN to propagate H_N through
    ///    <see cref="GhostPlaybackLogic.TryComputeSpanLoopUT"/> (not merely returned by the pure helper in
    ///    isolation - the PR #885/#890 "prove the patch mutated something" lesson): it fires for at least two
    ///    cycles with a NON-ZERO H_N, so H_N is seen to step.
    /// 2. A source-text gate (the established <see cref="DestinationLoiterTrimWiringTests"/> pattern) asserts
    ///    MissionLoopUnitBuilder emits the launch-hold Info line under the correct gate and threads the two
    ///    new launch-hold args into the LoopUnit constructor. The full re-aim ENGAGE path needs a live KSP /
    ///    Unity body model, so the builder Info line is gated structurally here rather than by driving Build.
    /// </summary>
    [Collection("Sequential")]
    public class LaunchHoldLoggingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LaunchHoldLoggingTests()
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

        // Fixture: span [0,1000], cadence 2000 (> span), phaseAnchor 300, T_sid 400 => the launch hold is
        // non-zero on consecutive cycles and steps as a sawtooth (Off_N = 300 + N*2000, T_sid 400).
        private const double Anchor = 300, S0 = 0, S1 = 1000, Cad = 2000, Tsid = 400;

        private void SampleClock(double currentUT)
        {
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                currentUT, Anchor, S0, S1, Cad, out double _, out long _, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: Tsid, launchHoldEngaged: true);
        }

        [Fact]
        public void PerLoopVerboseLine_FiresWithNonZeroHN_AcrossTwoCycles()
        {
            // Drive a UT well into cycle 0's launch (phaseInCycle > H_0 so the play branch runs and logs).
            SampleClock(Anchor + 500.0);   // cycle 0: elapsed 500 -> phaseInCycle 500 > H_0(100)
            // The rate-limiter keys on mission identity (not cycleIndex), so a second sample within the
            // window is suppressed; reset the rate-limit cache to model real time passing between loops.
            ParsekLog.ResetRateLimitsForTesting();
            SampleClock(Anchor + Cad + 500.0);   // cycle 1: elapsed 2500 -> cyc 1, phaseInCycle 500 > H_1

            var launchLines = logLines
                .Where(l => l.Contains("[Reaim]") && l.Contains("per-loop launch hold:"))
                .ToList();
            Assert.True(launchLines.Count >= 2,
                $"expected >= 2 per-loop launch-hold lines, got {launchLines.Count}:\n{string.Join("\n", logLines)}");

            // Distinct cycleIndex on the two lines (cycle 0 and cycle 1), proving H_N stepped.
            Assert.Contains(launchLines, l => l.Contains("cycleIndex=0"));
            Assert.Contains(launchLines, l => l.Contains("cycleIndex=1"));

            // Each line carries Tsid and a NON-ZERO H_N on at least one cycle (Off_0 = 300, T_sid = 400 ->
            // H_0 = 100 > 0). The line never reports HN=0 here.
            Assert.All(launchLines, l => Assert.Contains("Tsid=", l));
            Assert.Contains(launchLines, l => l.Contains("HN=") && !l.Contains("HN=0s") && !l.Contains("HN=0 "));
        }

        [Fact]
        public void NotEngaged_NoLaunchHoldLine()
        {
            // launchHoldEngaged false (every non-re-aim caller): no launch-hold Verbose line is emitted, so
            // pad-aligned / non-rotating units never spam a per-mission line.
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                Anchor + 500.0, Anchor, S0, S1, Cad, out double _, out long _, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: Tsid, launchHoldEngaged: false);
            Assert.DoesNotContain(logLines, l => l.Contains("per-loop launch hold:"));
        }

        // === Builder Info-line + ctor wiring source-text gate (DestinationLoiterTrimWiringTests pattern) ===

        private static string ReadBuilderSource()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", "MissionLoopUnitBuilder.cs");
            if (!File.Exists(path))
                path = Path.Combine(projectRoot, "Parsek", "MissionLoopUnitBuilder.cs");
            Assert.True(File.Exists(path), $"MissionLoopUnitBuilder.cs not found at {path}");
            return File.ReadAllText(path);
        }

        [Fact]
        public void Builder_GatesLaunchHoldOnPadDeclinedAndSupported_AndEmitsInfoLine()
        {
            string src = ReadBuilderSource();
            // The gate: re-aim engaged (inside the plan.Supported block) AND PadAlignLaunch did not apply.
            Assert.Contains("if (!pad.Applied && plan.Supported)", src);
            Assert.Contains("launchHoldEngaged = true", src);
            Assert.Contains("launchHoldRotationPeriod = launchRotationPeriod", src);
            // The Info line mirrors the PAD-ALIGN / ARRIVAL HOLD lines (tag Reaim, sidereal day, the reason).
            Assert.Contains("LAUNCH HOLD engaged", src);
            Assert.Contains("PadAlignLaunch declined -> per-loop launch hold", src);
            Assert.Contains("siderealDay=", src);
        }

        [Fact]
        public void Builder_ThreadsLaunchHoldArgsIntoLoopUnitCtor()
        {
            string src = ReadBuilderSource();
            // The two new launch-hold args are passed to the LoopUnit constructor alongside the arrival-hold
            // args, after arrivalHold.AmberReason.
            Assert.Contains("launchHoldRotationPeriod, launchHoldEngaged", src);
        }
    }
}
