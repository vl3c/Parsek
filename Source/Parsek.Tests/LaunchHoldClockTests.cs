using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Span-clock composition tests for the per-loop LAUNCH ALIGNMENT
    /// (docs/dev/design-reaim-launch-hold-seam.md, borrow-at-launch / repay-at-SOI-exit): the launch shift
    /// wired into <see cref="GhostPlaybackLogic.TryComputeSpanLoopUT"/> and surfaced through
    /// <see cref="GhostPlaybackLogic.DecideUnitMemberRender"/>. Covers:
    /// - in-SOI rotation alignment (currentUT - resolvedLoopUT is a whole multiple of T_sid through the
    ///   launch / parking / escape), via both the region-A (current cycle) and region-B (early next launch)
    ///   paths;
    /// - the SOI-exit repay coast hold (loopUT held at the SOI-exit recorded UT across the delta window);
    /// - the post-SOI-and-onward timeline byte-identical to BASELINE (the repay nets to zero with the
    ///   earlier launch, so targeting + the arrival hold are unchanged);
    /// - no pad absence (the early launch renders loopUT == spanStart, never a hidden/below-span sample);
    /// - cadence==synodic / not-engaged byte-identical;
    /// - the delta &gt; slack edge capped to slack.
    /// Pure inputs (no Unity).
    /// </summary>
    [Collection("Sequential")]
    public class LaunchHoldClockTests : IDisposable
    {
        private const double Tol = 1e-6;

        public LaunchHoldClockTests()
        {
            ParsekLog.SuppressLogging = false;
            GhostPlaybackLogic.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            GhostPlaybackLogic.ResetForTesting();
        }

        // Standard fixture: span [0,1000], cadence 2000 (so each cycle has a 1000 s idle tail => slack 1000),
        // phaseAnchor 300, T_sid 700, SOI exit at recorded UT 600. delta_N = (300 + N*2000) mod 700 steps
        // 300, 200, 100, ... (a true sawtooth, since 2000 mod 700 = 600 != 0), every delta well under slack.
        private const double Anchor = 300, S0 = 0, S1 = 1000, Cad = 2000, Tsid = 700, SoiExit = 600;

        private static double Loop(double currentUT, bool engaged, double tSid = Tsid, double soiExit = SoiExit)
        {
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                currentUT, Anchor, S0, S1, Cad, out double loopUT, out long _, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: tSid, launchHoldEngaged: engaged,
                soiExitAtUT: soiExit);
            return loopUT;
        }

        private static double Delta(long n, double cadence = Cad, double tSid = Tsid)
            => GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(Anchor, S0, n, cadence, tSid);

        // === No pad absence + in-SOI rotation alignment (region B: early next launch) =============

        [Fact]
        public void EarlyNextLaunch_RendersAtSpanStart_NotAbsent()
        {
            // Instance 1 launches early at L_1 - delta_1 = (300+2000) - 200 = 2100 (region B of cycle 0,
            // phaseInCycle = cadence - delta_1 = 1800). At that instant the in-SOI replay renders loopUT ==
            // spanStart (just launched, ON the pad) - NEVER below the span (no pad absence).
            double loopUT = Loop(2100.0, engaged: true);
            Assert.Equal(S0, loopUT, Tol);
            Assert.False(loopUT < S0, "the early launch renders at spanStart, never absent below the span");
        }

        [Fact]
        public void InSoiReplay_AlignedToRecordedRotation_AcrossLaunchAndEscape()
        {
            // The alignment proof (design §1.2): throughout the in-SOI replay (loopUT in [spanStart, SOI exit])
            // the live rotation equals the recorded rotation, i.e. (currentUT - resolvedLoopUT) is a whole
            // multiple of T_sid. Sweep the early launch of instance 1 (region B) from launch through the
            // SOI-exit boundary and assert congruence at every sample.
            for (double t = 2100.0; t <= 2100.0 + SoiExit; t += 7.0)
            {
                double loopUT = Loop(t, engaged: true);
                if (loopUT >= SoiExit - Tol)
                    break;   // at/past the SOI exit -> repay coast hold / post-SOI (covered separately)
                double rem = ((t - loopUT) % Tsid + Tsid) % Tsid;
                // Either ~0 or ~T_sid (float wrap): assert within tolerance of a whole multiple.
                double off = Math.Min(rem, Tsid - rem);
                Assert.True(off < 1e-3, $"t={t} loopUT={loopUT} not rotation-aligned (off={off})");
            }
        }

        [Fact]
        public void InSoiReplay_AlignedToRecordedRotation_RegionA()
        {
            // Region A (the current cycle's instance, launched delta ago in the prior window's tail): at the
            // cycle-1 boundary (currentUT 2300, phaseInCycle 0) the replay is delta_1 into flight (loopUT 200)
            // and stays rotation-aligned through the SOI exit.
            for (double t = 2300.0; t <= 2300.0 + SoiExit; t += 11.0)
            {
                double loopUT = Loop(t, engaged: true);
                // Stop once the replay reaches the SOI exit: at/after that boundary the loopUT enters the
                // delta coast HOLD (frozen at 600 while currentUT advances), which is intentionally NOT
                // rotation-aligned (it is the repay dwell). The in-SOI ascent/parking/escape BEFORE the exit
                // is what must be aligned.
                if (loopUT >= SoiExit - Tol)
                    break;
                double rem = ((t - loopUT) % Tsid + Tsid) % Tsid;
                double off = Math.Min(rem, Tsid - rem);
                Assert.True(off < 1e-3, $"t={t} loopUT={loopUT} not rotation-aligned (off={off})");
            }
        }

        // === Boundary continuity (region B of cycle N hands off to region A of cycle N+1) =========

        [Fact]
        public void BoundaryContinuity_RegionBHandsOffToRegionA()
        {
            // Just below the cycle 0->1 boundary (region B, instance 1) and just at it (region A, instance 1)
            // resolve nearly the same loopUT (continuous: no flicker, no rollback to the prior instance's
            // landed frame). The loopUT difference equals the currentUT probe step (the clock is locally linear
            // here), so a 1e-4 probe yields a ~1e-4 loopUT gap - the point is continuity, not exact equality.
            double justBelow = Loop(2300.0 - 1e-4, engaged: true);
            double atBoundary = Loop(2300.0, engaged: true);
            Assert.Equal(atBoundary, justBelow, 1e-3);
        }

        [Fact]
        public void BoundaryContinuity_SameInstanceAdvance_AcrossAllCycleBoundaries()
        {
            // The internal-consistency fix: region B (cycle N) caps instance N+1's advance to slack_N, and
            // region A (cycle N+1) caps the SAME instance N+1's advance to slack_{(N+1)-1} = slack_N - the SAME
            // value, so the loopUT is continuous at the cycle boundary (no discontinuity from the two regions
            // disagreeing on the instance's advance). Region B engages only when the PRIOR (launching) cycle has
            // a positive own advance (the outer `launchAdvance > 0` guard hosts the early launch in its tail);
            // when the launching cycle's delta == 0 there is no tail early-launch and the next instance appears
            // via region A at the boundary (a structural seam unrelated to this fix). Assert continuity only at
            // boundaries whose launching cycle (n-1) has a positive advance.
            for (long n = 1; n <= 6; n++)
            {
                if (!(Delta(n - 1) > 0.0))
                    continue; // launching cycle has no early-launch tail -> region B disabled there
                double boundary = Anchor + n * Cad; // cycle n-1 -> n
                double justBelow = Loop(boundary - 1e-3, engaged: true);
                double atBoundary = Loop(boundary, engaged: true);
                // Locally linear: a 1e-3 currentUT probe yields a ~1e-3 loopUT gap. Continuity, not a jump.
                Assert.Equal(atBoundary, justBelow, 1e-2);
            }
        }

        [Fact]
        public void BoundaryContinuity_SameInstanceAdvance_PerLoopVaryingSlack()
        {
            // With a per-loop arrival hold (W_N drifts per cycle) slack_N varies, so region A's cap (slack_{N-1})
            // and region B's cap (slack_N) are DIFFERENT slacks - but they cap DIFFERENT instances (region A
            // caps instance N to slack_{N-1}, region B of the PRIOR cycle caps that SAME instance N to
            // slack_{N-1} too). The shared helper guarantees both use slack_{N-1} for instance N, so the
            // boundary stays continuous even when slack varies per loop. Fixture: span [0,1000], cadence 2000,
            // arrival hold W_0=300 at recorded UT 800, T_align 250, SOI exit 600. As above, only boundaries
            // whose launching cycle has a positive own advance host a region-B early launch.
            const double holdAt = 800, w0 = 300, tAlign = 250;
            for (long n = 1; n <= 6; n++)
            {
                if (!(Delta(n - 1) > 0.0))
                    continue;
                double boundary = Anchor + n * Cad;
                double justBelow = LoopHold(boundary - 1e-3, w0, holdAt, tAlign);
                double atBoundary = LoopHold(boundary, w0, holdAt, tAlign);
                Assert.Equal(atBoundary, justBelow, 1e-2);
            }
        }

        private static double LoopHold(double currentUT, double w0, double holdAt, double tAlign)
        {
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                currentUT, Anchor, S0, S1, Cad, out double loopUT, out long _, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: w0, arrivalHoldAtUT: holdAt,
                arrivalHoldAlignPeriod: tAlign, launchBodyRotationPeriod: Tsid, launchHoldEngaged: true,
                soiExitAtUT: SoiExit);
            return loopUT;
        }

        // === SOI-exit repay coast hold (loopUT held at the SOI-exit recorded UT) ==================

        [Fact]
        public void SoiExitRepay_HoldsAtSoiExitUT()
        {
            // Region A of cycle 1, instance 1: phaseFromLaunch = phaseInCycle + delta_1 (=200). The SOI hold
            // inserts delta_1 at soiExitPhasePos = 600, so for phaseFromLaunch in (600, 600+200] the loopUT is
            // HELD at 600. phaseFromLaunch = 700 -> phaseInCycle 500 -> currentUT 2300 + 500 = 2800.
            double loopUT = Loop(2800.0, engaged: true);
            Assert.Equal(SoiExit, loopUT, 1e-3);
        }

        // === Post-SOI-and-onward timeline byte-identical to baseline (the repay nets to zero) =====

        [Fact]
        public void PostSoi_ByteIdenticalToBaseline()
        {
            // Region A, past the SOI-exit hold (phaseFromLaunch > soiExitPhasePos + delta): the engaged loopUT
            // equals the BASELINE (not-engaged) loopUT at the SAME currentUT - the SOI-exit repay nets to zero
            // with the earlier launch, so everything from the SOI exit onward is on the baseline schedule.
            // phaseInCycle 700 of cycle 1 -> currentUT 3000, phaseFromLaunch 900 > 800.
            double engaged = Loop(3000.0, engaged: true);
            double baseline = Loop(3000.0, engaged: false);
            Assert.Equal(baseline, engaged, 1e-3);
        }

        [Fact]
        public void PostSoi_ByteIdenticalToBaseline_AcrossSweep()
        {
            // The post-SOI baseline-equality holds across a swept range of post-SOI-hold currentUTs (region A
            // of several cycles). Only sample where the engaged loopUT is past the SOI-exit recorded UT.
            for (double t = 2300.0; t <= 2300.0 + 6.0 * Cad; t += 13.0)
            {
                double engaged = Loop(t, engaged: true);
                if (engaged <= SoiExit + Tol)
                    continue;   // in-SOI / repay window: covered by the alignment + repay tests
                double baseline = Loop(t, engaged: false);
                Assert.Equal(baseline, engaged, 1e-3);
            }
        }

        // === Not-engaged / degenerate T_sid byte-identical ========================================

        [Fact]
        public void NotEngaged_ByteIdenticalToBareClock_AcrossSweep()
        {
            // launchHoldEngaged false (every non-re-aim caller) -> the launch-alignment params are inert;
            // loopUT, cycleIndex, and the tail flag match the pre-alignment call across a swept range.
            for (double t = Anchor; t <= Anchor + 3.0 * Cad; t += 29.0)
            {
                bool okBase = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, Anchor, S0, S1, Cad, out double baseUT, out long baseCyc, out bool baseTail);
                bool okNew = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, Anchor, S0, S1, Cad, out double newUT, out long newCyc, out bool newTail,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                    arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: Tsid, launchHoldEngaged: false,
                    soiExitAtUT: SoiExit);
                Assert.Equal(okBase, okNew);
                Assert.Equal(baseUT, newUT, Tol);
                Assert.Equal(baseCyc, newCyc);
                Assert.Equal(baseTail, newTail);
            }
        }

        [Fact]
        public void DegenerateTsid_NoAdvance_ByteIdenticalAcrossSweep()
        {
            // launchHoldEngaged true but a degenerate (non-rotating) T_sid -> delta_N == 0 -> byte-identical to
            // the not-engaged clock across a swept currentUT and several loops.
            for (double t = Anchor; t <= Anchor + 3.0 * Cad; t += 31.0)
            {
                double engagedDeg = Loop(t, engaged: true, tSid: 0.0);
                double notEngaged = Loop(t, engaged: false);
                Assert.Equal(notEngaged, engagedDeg, Tol);
            }
        }

        // === Targeting invariants byte-identical (the resolver window read, schedule:null) ========

        [Fact]
        public void TargetingInvariant_CycleIndexUnchangedBySchedulelessResolverCall()
        {
            // The resolver derives windowIndex from TryComputeSpanLoopUT(..., schedule:null) with NO launch-hold
            // args; that call's cycleIndex is byte-identical to the bare clock (the launch-alignment defaults to
            // not engaged there). Sweep including pre-anchor + early-launch UTs.
            for (double t = Anchor; t <= Anchor + 4.0 * Cad; t += 17.0)
            {
                GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, Anchor, S0, S1, Cad, out double _, out long resolverCyc, out bool _, schedule: null);
                // The resolver call passes only the first 5 positional args (schedule:null), so the launch
                // alignment is dormant. This pins that the window read the resolver uses is unchanged.
                GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, Anchor, S0, S1, Cad, out double _, out long bareCyc, out bool _);
                Assert.Equal(bareCyc, resolverCyc);
            }
        }

        // === cadence == synodic untouched (the launch alignment never engages there) =============

        [Fact]
        public void CadenceEqualsSynodic_NotEngaged_ByteIdentical()
        {
            // For cadence == synodic PadAlignLaunch applies, so the builder sets launchHoldEngaged=false. With
            // the alignment not engaged the clock is byte-identical to today regardless of the carried T_sid.
            const double synodic = 19653076.0, span = 1000000.0;
            for (double t = 0.0; t <= 3.0 * synodic; t += synodic / 7.0)
            {
                bool okBase = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, 0.0, 0.0, span, synodic, out double baseUT, out long baseCyc, out bool baseTail);
                bool okNew = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, 0.0, 0.0, span, synodic, out double newUT, out long newCyc, out bool newTail,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                    arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: 21600.0, launchHoldEngaged: false,
                    soiExitAtUT: 500000.0);
                Assert.Equal(okBase, okNew);
                Assert.Equal(baseUT, newUT, 3);
                Assert.Equal(baseCyc, newCyc);
                Assert.Equal(baseTail, newTail);
            }
        }

        // === Arrival hold UNCHANGED by the launch alignment (no (W_N - H) subtraction) ===========

        [Fact]
        public void ArrivalHold_UnchangedByLaunchAlignment()
        {
            // The arrival hold is byte-identical whether or not the launch alignment is engaged, for a
            // post-SOI / post-arrival probe (the repay nets to zero, so the SOI entry + arrival occur at the
            // same live UT as baseline). Fixture: arrival hold at recorded UT 800 (after the SOI exit 600),
            // W_0 = 100, T_align = 250. Probe a post-arrival UT in region A of cycle 1.
            const double holdAt = 800, w0 = 100, tAlign = 250;
            // currentUT 3000: region A cycle 1, phaseFromLaunch 900, well past the arrival boundary.
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                3000.0, Anchor, S0, S1, Cad, out double engagedUT, out long _, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: w0, arrivalHoldAtUT: holdAt,
                arrivalHoldAlignPeriod: tAlign, launchBodyRotationPeriod: Tsid, launchHoldEngaged: true,
                soiExitAtUT: SoiExit);
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                3000.0, Anchor, S0, S1, Cad, out double baselineUT, out long _, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: w0, arrivalHoldAtUT: holdAt,
                arrivalHoldAlignPeriod: tAlign, launchBodyRotationPeriod: Tsid, launchHoldEngaged: false,
                soiExitAtUT: SoiExit);
            // The post-SOI portion (incl. the arrival hold) is byte-identical: the launch alignment does NOT
            // perturb the arrival hold (the PR #1174 (W_N - H_N) subtraction was removed).
            Assert.Equal(baselineUT, engagedUT, 1e-3);
        }

        // === delta > slack edge: capped to slack ==================================================

        [Fact]
        public void DeltaExceedsSlack_CappedToSlack()
        {
            // Pathological: span 1000, cadence 1100 (slack 100), arrivalHold 0, T_sid 700 so the raw delta can
            // reach ~700 >> slack 100. The clamp caps the launch advance to slack (100), so the in-SOI replay
            // and the SOI-exit-and-onward timeline are never truncated and the resolved loopUT stays a real
            // recorded UT inside [s0, s1]. (Capping only shortens the borrow; it cannot reopen the seam since
            // the advance is bounded BY slack.)
            const double cad = 1100, soi = 600;
            for (double t = Anchor; t <= Anchor + 3.0 * cad; t += 9.0)
            {
                GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, Anchor, S0, S1, cad, out double loopUT, out long _, out bool tail,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                    arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: Tsid, launchHoldEngaged: true,
                    soiExitAtUT: soi);
                if (!tail)
                    Assert.True(loopUT >= S0 - Tol && loopUT <= S1 + Tol, $"t={t} loopUT {loopUT} outside recorded span");
            }
        }

        // === Cross-surface parity: ResolveTrackingStationSampleUT reads the hold from the unit =====

        private static GhostPlaybackLogic.LoopUnitSet BuildLaunchHoldUnit()
        {
            // A launch-alignment unit (member 0, span [0,1000], cadence 2000, phaseAnchor 300, T_sid 700,
            // SOI exit 600). The reaimPlan / reaimSchedule are null here (the TS sampler reads only the
            // span-clock + launch-alignment fields).
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, S0, S1, Cad, Anchor, Cad, null, null, null, null,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: Tsid, launchHoldEngaged: true,
                recordedSoiExitUT: SoiExit);
            return new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { 0, unit } },
                new Dictionary<int, int> { { 0, 0 } });
        }

        [Fact]
        public void TsSampler_RendersEarlyLaunch_AndPostSoiBaseline()
        {
            var units = BuildLaunchHoldUnit();

            // Early launch of instance 1 (currentUT 2100): the TS sampler renders the ghost at spanStart (NOT
            // hidden - there is no pad absence under borrow-repay), reading the launch-alignment fields from
            // the unit with NO external-caller change.
            double launchUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                0, S0, S1, 2100.0, units, out bool launchHidden);
            Assert.False(launchHidden, "the early launch must render (no pad absence)");
            Assert.Equal(S0, launchUT, 1e-3);

            // Post-SOI (currentUT 3000): renders at the post-SOI loopUT, which equals the baseline.
            double postUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                0, S0, S1, 3000.0, units, out bool postHidden);
            Assert.False(postHidden);
            double baseline = Loop(3000.0, engaged: false);
            Assert.Equal(baseline, postUT, 1e-3);
        }
    }
}
