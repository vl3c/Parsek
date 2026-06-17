using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Log-assertion + wiring tests for the per-loop LAUNCH ALIGNMENT
    /// (docs/dev/design-reaim-launch-hold-seam.md, borrow-at-launch / repay-at-SOI-exit). Two halves:
    ///
    /// 1. The per-loop Verbose Reaim launch-advance line is PROVEN to propagate delta_N through
    ///    <see cref="GhostPlaybackLogic.TryComputeSpanLoopUT"/> (not merely returned by the pure helper in
    ///    isolation - the PR #885/#890 "prove the patch mutated something" lesson): it fires for at least two
    ///    cycles with a NON-ZERO delta_N, so delta_N is seen to step.
    /// 2. A source-text gate (the established <see cref="DestinationLoiterTrimWiringTests"/> pattern) asserts
    ///    MissionLoopUnitBuilder emits the launch-alignment Info line under the correct gate and threads the
    ///    launch-alignment args (T_sid + engaged flag + SOI-exit UT) into the LoopUnit constructor. The full
    ///    re-aim ENGAGE path needs a live KSP / Unity body model, so the builder Info line is gated
    ///    structurally here rather than by driving Build.
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

        // Fixture: span [0,1000], cadence 2000 (so each cycle has a 1000 s idle tail => slack 1000),
        // phaseAnchor 300, T_sid 700, SOI exit 600 => delta_N = (300 + N*2000) mod 700 steps 300, 200, 100,
        // ... a true sawtooth (2000 mod 700 = 600 != 0), every delta non-zero and under slack.
        private const double Anchor = 300, S0 = 0, S1 = 1000, Cad = 2000, Tsid = 700, SoiExit = 600;

        private void SampleClock(double currentUT)
        {
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                currentUT, Anchor, S0, S1, Cad, out double _, out long _, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: Tsid, launchHoldEngaged: true,
                soiExitAtUT: SoiExit);
        }

        [Fact]
        public void PerLoopVerboseLine_FiresWithNonZeroDelta_AcrossTwoCycles()
        {
            // Drive a UT mid-cycle in cycle 0 (region A, instance 0) so the launch-advance line logs.
            SampleClock(Anchor + 500.0);   // cycle 0: elapsed 500 -> phaseInCycle 500, region A
            // The rate-limiter keys on mission identity (not cycleIndex), so a second sample within the
            // window is suppressed; reset the rate-limit cache to model real time passing between loops.
            ParsekLog.ResetRateLimitsForTesting();
            SampleClock(Anchor + Cad + 500.0);   // cycle 1: elapsed 2500 -> cyc 1, phaseInCycle 500, region A

            var launchLines = logLines
                .Where(l => l.Contains("[Reaim]") && l.Contains("per-loop launch advance:"))
                .ToList();
            Assert.True(launchLines.Count >= 2,
                $"expected >= 2 per-loop launch-advance lines, got {launchLines.Count}:\n{string.Join("\n", logLines)}");

            // Distinct cycleIndex on the two lines (cycle 0 and cycle 1), proving delta_N stepped.
            Assert.Contains(launchLines, l => l.Contains("cycleIndex=0"));
            Assert.Contains(launchLines, l => l.Contains("cycleIndex=1"));

            // Each line carries Tsid + slack and a NON-ZERO delta_N (delta_0 = 300, delta_1 = 200, both > 0).
            Assert.All(launchLines, l => Assert.Contains("Tsid=", l));
            Assert.All(launchLines, l => Assert.Contains("slack=", l));
            Assert.Contains(launchLines, l => l.Contains("deltaN=") && !l.Contains("deltaN=0s") && !l.Contains("deltaN=0 "));
        }

        [Fact]
        public void NotEngaged_NoLaunchAdvanceLine()
        {
            // launchHoldEngaged false (every non-re-aim caller): no launch-advance Verbose line is emitted, so
            // pad-aligned / non-rotating units never spam a per-mission line.
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                Anchor + 500.0, Anchor, S0, S1, Cad, out double _, out long _, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: Tsid, launchHoldEngaged: false,
                soiExitAtUT: SoiExit);
            Assert.DoesNotContain(logLines, l => l.Contains("per-loop launch advance:"));
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
        public void Builder_GatesLaunchAlignmentOnPadDeclinedSupportedAndSoiExit_AndEmitsInfoLine()
        {
            string src = ReadBuilderSource();
            // The gate: re-aim engaged (inside the plan.Supported block) AND PadAlignLaunch did not apply AND
            // a valid SOI-exit boundary (where the delta_N repay coast hold is inserted).
            Assert.Contains("if (!pad.Applied && plan.Supported && soiExitValid)", src);
            Assert.Contains("launchHoldEngaged = true", src);
            Assert.Contains("launchHoldRotationPeriod = launchRotationPeriod", src);
            Assert.Contains("launchHoldSoiExitUT = plan.RecordedSoiExitUT", src);
            // The Info line mirrors the PAD-ALIGN / ARRIVAL HOLD lines (tag Reaim, sidereal day, SOI exit,
            // the reason).
            Assert.Contains("LAUNCH HOLD engaged", src);
            Assert.Contains("per-loop launch advance / SOI-exit repay", src);
            Assert.Contains("siderealDay=", src);
            Assert.Contains("soiExit=", src);
        }

        [Fact]
        public void Builder_ThreadsLaunchAlignmentArgsIntoLoopUnitCtor()
        {
            string src = ReadBuilderSource();
            // The three launch-alignment args (T_sid + engaged flag + SOI-exit UT) are passed to the LoopUnit
            // constructor alongside the arrival-hold args, after arrivalHold.AmberReason.
            Assert.Contains("launchHoldRotationPeriod, launchHoldEngaged, launchHoldSoiExitUT", src);
        }
    }
}
