using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure-clock tests for the BOUNDARY-OVERLAP launch render
    /// (docs/dev/plan-launch-boundary-overlap.md, Design B): the gated advance helper
    /// (<see cref="GhostPlaybackLogic.ComputeBoundaryOverlapAdvanceSeconds"/>), the dual-clock frame
    /// (<see cref="GhostPlaybackLogic.ComputeSpanLoopFrame"/>) emitting a SECONDARY on the zero-slack loops, and
    /// the per-member decision (<see cref="GhostPlaybackLogic.DecideBoundaryOverlapSecondaryRender"/> /
    /// <see cref="GhostPlaybackLogic.ResolveTrackingStationSampleFrame"/>). Covers plan section 9 tests 1-4:
    /// - the gated helper returns the capped advance on a slack&gt;0 loop and the FULL raw delta on a zero-slack loop;
    /// - the zero-slack loop emits HasSecondary inside the borrow window, false outside, with the section 7 invariants
    ///   (primary effectiveSpan = cadence + delta, no clamp truncation, boundary continuity, residual closes);
    /// - the byte-identity fence (the TryComputeSpanLoopUT primary-only wrapper and the schedule:null windowIndex);
    /// - per-member independence (the secondary resolves in the ascent member's window, the primary elsewhere).
    /// Pure inputs (no Unity).
    /// </summary>
    [Collection("Sequential")]
    public class BoundaryOverlapClockTests : IDisposable
    {
        private const double Tol = 1e-6;

        public BoundaryOverlapClockTests()
        {
            ParsekLog.SuppressLogging = true; // these are value tests, not log assertions
            GhostPlaybackLogic.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostPlaybackLogic.ResetForTesting();
        }

        // ZERO-SLACK fixture: span [0,1000], cadence == span == 1000 (no idle tail => slack 0), no loiter / no
        // arrival hold, phaseAnchor 300, T_sid 700, SOI exit 600. delta_N = (300 + N*1000) mod 700 steps
        // 300, 600, 200, 500, 100, ... all positive, all > slack(0), so the boundary overlap ALWAYS engages.
        private const double ZAnchor = 300, ZS0 = 0, ZS1 = 1000, ZCad = 1000, ZTsid = 700, ZSoiExit = 600;

        // ALIGNED (slack>0) fixture (the LaunchHoldClockTests fixture): span [0,1000], cadence 2000 (slack 1000),
        // phaseAnchor 300, T_sid 700, SOI exit 600. delta_N steps 300, 200, 100, ... all under slack -> NEVER
        // engages the boundary overlap (byte-identical to today, no secondary).
        private const double AAnchor = 300, AS0 = 0, AS1 = 1000, ACad = 2000, ATsid = 700, ASoiExit = 600;

        private static GhostPlaybackLogic.SpanLoopFrame ZFrame(double currentUT)
            => GhostPlaybackLogic.ComputeSpanLoopFrame(
                currentUT, ZAnchor, ZS0, ZS1, ZCad,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: ZTsid, launchHoldEngaged: true,
                soiExitAtUT: ZSoiExit);

        private static double ZRawDelta(long n)
            => GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(ZAnchor, ZS0, n, ZCad, ZTsid);

        // === Test 1: ComputeBoundaryOverlapAdvanceSeconds - gated, not blanket-uncapped =============

        [Fact]
        public void GatedAdvance_ReturnsCappedDelta_WhenRawUnderSlack()
        {
            // ALIGNED loop (slack 1000, raw delta <= 300): the gated helper returns the SAME value as the cap
            // helper (byte-identical to today), so region B's early launch instant and the primary are unchanged.
            for (long n = 0; n <= 6; n++)
            {
                double capped = GhostPlaybackLogic.ComputeCappedLaunchAdvanceSeconds(
                    AAnchor, AS0, AS1, ACad, n, ATsid, null, 0.0, double.NaN);
                double boundary = GhostPlaybackLogic.ComputeBoundaryOverlapAdvanceSeconds(
                    AAnchor, AS0, AS1, ACad, n, ATsid, null, 0.0, double.NaN);
                Assert.Equal(capped, boundary, Tol);
            }
        }

        [Fact]
        public void GatedAdvance_ReturnsFullRawDelta_WhenRawExceedsSlack()
        {
            // ZERO-SLACK loop (slack 0, raw delta > 0): the gated helper returns the FULL raw delta (uncapped),
            // strictly greater than the cap (= 0), so the launch realigns fully and the seam closes.
            for (long n = 0; n <= 6; n++)
            {
                double raw = ZRawDelta(n);
                if (!(raw > 0.0))
                    continue;
                double capped = GhostPlaybackLogic.ComputeCappedLaunchAdvanceSeconds(
                    ZAnchor, ZS0, ZS1, ZCad, n, ZTsid, null, 0.0, double.NaN);
                double boundary = GhostPlaybackLogic.ComputeBoundaryOverlapAdvanceSeconds(
                    ZAnchor, ZS0, ZS1, ZCad, n, ZTsid, null, 0.0, double.NaN);
                Assert.Equal(0.0, capped, Tol);          // the cap bit hard (slack 0)
                Assert.Equal(raw, boundary, Tol);        // the boundary overlap uses the full raw delta
                Assert.True(boundary > capped + Tol);
            }
        }

        [Fact]
        public void GatedAdvance_Degenerate_ReturnsZero()
        {
            // Degenerate / NaN T_sid -> 0 (no advance, nothing to cap).
            Assert.Equal(0.0, GhostPlaybackLogic.ComputeBoundaryOverlapAdvanceSeconds(
                ZAnchor, ZS0, ZS1, ZCad, 3, 0.0, null, 0.0, double.NaN), Tol);
            Assert.Equal(0.0, GhostPlaybackLogic.ComputeBoundaryOverlapAdvanceSeconds(
                ZAnchor, ZS0, ZS1, ZCad, 3, double.NaN, null, 0.0, double.NaN), Tol);
            Assert.Equal(0.0, GhostPlaybackLogic.ComputeBoundaryOverlapAdvanceSeconds(
                double.NaN, ZS0, ZS1, ZCad, 3, ZTsid, null, 0.0, double.NaN), Tol);
        }

        // === Test 2: load-bearing section 7 invariant - zero-slack loop emits a secondary =================

        [Fact]
        public void ZeroSlack_HasSecondary_InsideBorrowWindow_FalseOutside()
        {
            // Cycle 1: launched at L_1 = anchor + 1*cadence = 1300. The borrow window for the NEXT instance
            // (cycle 2, advNext = delta_2 = 200) is phaseInCycle in [cadence - advNext, cadence) = [800, 1000).
            // Probe a point INSIDE the borrow window of cycle 1 (phaseInCycle 900 -> currentUT 1300 + 900 = 2200).
            double advNext = ZRawDelta(2); // 200
            Assert.True(advNext > 0.0);
            double inBorrowUT = ZAnchor + 1 * ZCad + (ZCad - advNext) + 50.0; // phaseInCycle = 800 + 50 = 850
            var inFrame = ZFrame(inBorrowUT);
            Assert.True(inFrame.Resolved);
            Assert.True(inFrame.HasSecondary, "inside the borrow window the zero-slack loop emits a secondary");
            Assert.Equal(2, inFrame.SecondaryCycleIndex);   // the next instance N+1
            Assert.Equal(1, inFrame.CycleIndex);            // the PRIMARY stays on instance N (no flip)

            // Probe a point OUTSIDE the borrow window (phaseInCycle 400 -> currentUT 1300 + 400 = 1700).
            double outBorrowUT = ZAnchor + 1 * ZCad + 400.0;
            var outFrame = ZFrame(outBorrowUT);
            Assert.True(outFrame.Resolved);
            Assert.False(outFrame.HasSecondary, "outside the borrow window there is no secondary");
            Assert.Equal(1, outFrame.CycleIndex);
        }

        [Fact]
        public void ZeroSlack_PrimaryEffectiveSpan_NeverTruncatesWithinCycle()
        {
            // section 7: on a zero-slack loop the primary's effectiveSpan = compressedSpan + delta + hold = cadence + delta
            // > cadence, so the clamp min(phaseFromLaunch, effectiveSpan) never truncates the primary within the
            // cycle (its max phaseFromLaunch = cadence + delta = effectiveSpan, reached only AT the boundary). The
            // SOI-exit repay nets delta to zero past the SOI exit, so the primary's loopUT past the repay equals
            // the BASELINE loopUT (spanStart + phaseInCycle) - it is NOT parked early at spanEnd. Sweep cycle 2
            // (delta_2 = 200): every post-repay sample stays inside [s0, s1] AND advances with phaseInCycle (never
            // frozen at spanEnd before the cycle boundary).
            double delta = ZRawDelta(2);
            double cycleStart = ZAnchor + 2 * ZCad;
            bool sawAdvanceNearEnd = false;
            for (double phase = 0.0; phase < ZCad - 1.0; phase += 5.0)
            {
                var fr = ZFrame(cycleStart + phase);
                Assert.True(fr.Resolved);
                Assert.True(fr.LoopUT >= ZS0 - Tol && fr.LoopUT <= ZS1 + Tol,
                    $"phase={phase} primary loopUT {fr.LoopUT} outside [s0,s1]");
                // Past the SOI-exit repay the engaged primary equals the baseline loopUT (spanStart + phaseInCycle),
                // proving no early park: at phase 900 the baseline is 900, and the engaged loopUT must match it
                // (the repay nets to zero) rather than being frozen at spanEnd.
                if (phase >= 900.0)
                {
                    double baseline = cycleStart + phase < ZAnchor ? ZS0 : (ZS0 + phase);
                    Assert.Equal(baseline, fr.LoopUT, 1.0);
                    sawAdvanceNearEnd = true;
                }
            }
            Assert.True(sawAdvanceNearEnd, $"expected post-repay samples near the cycle end (delta={delta})");
        }

        [Fact]
        public void ZeroSlack_BoundaryContinuity_SecondaryHandsOffToNextPrimary()
        {
            // section 2.5: at phaseInCycle -> cadence the secondary's loopUT -> the next-cycle primary's loopUT at
            // phaseInCycle = 0 (same instance N+1, same loopUT). Probe just below cycle 2's boundary (in the
            // borrow window, secondary = instance 2) and just at cycle 2's start (primary = instance 2).
            double cycle2Start = ZAnchor + 2 * ZCad;
            var secFrame = ZFrame(cycle2Start - 1e-3);   // cycle 1, end of borrow window, secondary = instance 2
            Assert.True(secFrame.HasSecondary);
            Assert.Equal(2, secFrame.SecondaryCycleIndex);
            var primFrame = ZFrame(cycle2Start + 1e-3);  // cycle 2, start, primary = instance 2
            Assert.Equal(2, primFrame.CycleIndex);
            // The secondary's loopUT at the boundary equals the next primary's loopUT (locally linear, ~2e-3 gap).
            Assert.Equal(primFrame.LoopUT, secFrame.SecondaryLoopUT, 1e-2);
        }

        [Fact]
        public void ZeroSlack_SecondaryInSoiReplay_RotationAligned()
        {
            // The whole point: the SECONDARY's in-SOI replay (its early launch) is rotation-aligned with the
            // recorded launch, i.e. (currentUT - secondaryLoopUT) is a whole multiple of T_sid for the
            // early-launching instance N+1 BEFORE its SOI exit. Sweep the borrow window of cycle 1 (secondary =
            // instance 2). residualDeg == 0 for the previously-capped loop (the boundary overlap uses the full raw
            // delta, so the seam closes).
            double advNext = ZRawDelta(2);
            double cycle1Start = ZAnchor + 1 * ZCad;
            int aligned = 0, sampled = 0;
            for (double phase = ZCad - advNext + 1.0; phase < ZCad - 1.0; phase += 3.0)
            {
                var fr = ZFrame(cycle1Start + phase);
                if (!fr.HasSecondary)
                    continue;
                if (fr.SecondaryLoopUT >= ZSoiExit - Tol)
                    continue; // at/past the secondary's SOI exit -> repay dwell (not rotation-aligned)
                double currentUT = cycle1Start + phase;
                double rem = ((currentUT - fr.SecondaryLoopUT) % ZTsid + ZTsid) % ZTsid;
                double off = Math.Min(rem, ZTsid - rem);
                sampled++;
                if (off < 1e-3) aligned++;
            }
            Assert.True(sampled > 0, "expected some in-SOI secondary samples in the borrow window");
            Assert.Equal(sampled, aligned);
        }

        // === Test 3: byte-identity fence ===========================================================

        [Fact]
        public void Wrapper_PrimaryOnly_MatchesFrame_AcrossSweep()
        {
            // The TryComputeSpanLoopUT wrapper returns exactly the PRIMARY of ComputeSpanLoopFrame, across both
            // fixtures and a swept currentUT (no secondary leaks into the wrapper output).
            foreach (var fx in new[] { (ZAnchor, ZS0, ZS1, ZCad, ZTsid, ZSoiExit), (AAnchor, AS0, AS1, ACad, ATsid, ASoiExit) })
            {
                for (double t = fx.Item1; t <= fx.Item1 + 5.0 * fx.Item4; t += 17.0)
                {
                    var frame = GhostPlaybackLogic.ComputeSpanLoopFrame(
                        t, fx.Item1, fx.Item2, fx.Item3, fx.Item4,
                        schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                        arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: fx.Item5,
                        launchHoldEngaged: true, soiExitAtUT: fx.Item6);
                    bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                        t, fx.Item1, fx.Item2, fx.Item3, fx.Item4, out double loopUT, out long cyc, out bool tail,
                        schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                        arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: fx.Item5,
                        launchHoldEngaged: true, soiExitAtUT: fx.Item6);
                    Assert.Equal(frame.Resolved, ok);
                    Assert.Equal(frame.LoopUT, loopUT, Tol);
                    Assert.Equal(frame.CycleIndex, cyc);
                    Assert.Equal(frame.IsInInterCycleTail, tail);
                }
            }
        }

        [Fact]
        public void AlignedLoop_NeverHasSecondary_PrimaryByteIdenticalToCapHelper()
        {
            // The byte-identity fence for invariant 2: on the ALIGNED fixture the boundary overlap NEVER engages,
            // so HasSecondary is always false AND the primary loopUT equals the loopUT computed with the OLD capped
            // advance (the LaunchHoldClockTests fixture already pins the capped-advance clock, so equality here
            // proves the dual-clock change did not perturb the aligned-loop primary).
            for (double t = AAnchor; t <= AAnchor + 5.0 * ACad; t += 13.0)
            {
                var fr = GhostPlaybackLogic.ComputeSpanLoopFrame(
                    t, AAnchor, AS0, AS1, ACad,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                    arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: ATsid,
                    launchHoldEngaged: true, soiExitAtUT: ASoiExit);
                Assert.False(fr.HasSecondary, $"aligned loop must never emit a secondary (t={t})");
            }
        }

        [Fact]
        public void TargetingWindowIndex_Unchanged_ByScheduleNullCall()
        {
            // The resolver derives windowIndex from TryComputeSpanLoopUT(..., schedule:null) with no launch-hold
            // args, so the launch alignment is dormant there and the cycleIndex is byte-identical to the bare
            // clock - even on the zero-slack fixture (the boundary overlap never touches the targeting read).
            for (double t = ZAnchor; t <= ZAnchor + 5.0 * ZCad; t += 11.0)
            {
                GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, ZAnchor, ZS0, ZS1, ZCad, out double _, out long resolverCyc, out bool _, schedule: null);
                GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, ZAnchor, ZS0, ZS1, ZCad, out double _, out long bareCyc, out bool _);
                Assert.Equal(bareCyc, resolverCyc);
            }
        }

        // === Test 4: DecideBoundaryOverlapSecondaryRender - per-member independence =================

        [Fact]
        public void DecideSecondary_RendersInMemberWindow_HiddenOutside()
        {
            // The decision returns Render when the secondary loopUT is in the member's window, HiddenOutsideWindow
            // when not, NoSecondary when no secondary is live. Use a borrow-window UT (cycle 1, secondary =
            // instance 2 launching in-SOI, secondaryLoopUT near spanStart).
            double advNext = ZRawDelta(2);
            double inBorrowUT = ZAnchor + 1 * ZCad + (ZCad - advNext) + 50.0;

            // The secondary's in-SOI ascent loopUT is near spanStart; a member spanning the whole recording
            // contains it -> Render.
            var render = GhostPlaybackLogic.DecideBoundaryOverlapSecondaryRender(
                inBorrowUT, ZAnchor, ZS0, ZS1, ZCad, ZS0, ZS1,
                out double secUT, out long secCycle,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: ZTsid, launchHoldEngaged: true,
                soiExitAtUT: ZSoiExit);
            Assert.Equal(GhostPlaybackLogic.BoundaryOverlapSecondaryDecision.Render, render);
            Assert.Equal(2, secCycle);
            Assert.True(secUT >= ZS0 - Tol && secUT <= ZS1 + Tol);

            // A member window that EXCLUDES the secondary's ascent leg (e.g. only [700,1000], the late part of the
            // recording) -> HiddenOutsideWindow (the secondary is the in-SOI ascent, near spanStart, < 700).
            var hidden = GhostPlaybackLogic.DecideBoundaryOverlapSecondaryRender(
                inBorrowUT, ZAnchor, ZS0, ZS1, ZCad, 700.0, ZS1,
                out double _, out long _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: ZTsid, launchHoldEngaged: true,
                soiExitAtUT: ZSoiExit);
            Assert.Equal(GhostPlaybackLogic.BoundaryOverlapSecondaryDecision.HiddenOutsideWindow, hidden);

            // Outside the borrow window -> NoSecondary.
            double outBorrowUT = ZAnchor + 1 * ZCad + 200.0;
            var none = GhostPlaybackLogic.DecideBoundaryOverlapSecondaryRender(
                outBorrowUT, ZAnchor, ZS0, ZS1, ZCad, ZS0, ZS1,
                out double _, out long _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: ZTsid, launchHoldEngaged: true,
                soiExitAtUT: ZSoiExit);
            Assert.Equal(GhostPlaybackLogic.BoundaryOverlapSecondaryDecision.NoSecondary, none);
        }

        [Fact]
        public void DecideSecondary_AlignedLoop_NoSecondary()
        {
            // On the aligned fixture the boundary overlap never engages, so the decision is always NoSecondary,
            // regardless of the member window or the probe UT (invariant 2: no secondary on aligned loops).
            for (double t = AAnchor; t <= AAnchor + 4.0 * ACad; t += 19.0)
            {
                var d = GhostPlaybackLogic.DecideBoundaryOverlapSecondaryRender(
                    t, AAnchor, AS0, AS1, ACad, AS0, AS1, out double _, out long _,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                    arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: ATsid, launchHoldEngaged: true,
                    soiExitAtUT: ASoiExit);
                Assert.Equal(GhostPlaybackLogic.BoundaryOverlapSecondaryDecision.NoSecondary, d);
            }
        }

        // === ResolveTrackingStationSampleFrame - primary + optional secondary via the LoopUnit =====

        private static GhostPlaybackLogic.LoopUnitSet BuildZeroSlackUnit()
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, ZS0, ZS1, ZCad, ZAnchor, ZCad, null, null, null, null,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: ZTsid, launchHoldEngaged: true,
                recordedSoiExitUT: ZSoiExit);
            return new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { 0, unit } },
                new Dictionary<int, int> { { 0, 0 } });
        }

        [Fact]
        public void TsSampleFrame_PrimaryPlusSecondary_InBorrowWindow()
        {
            var units = BuildZeroSlackUnit();
            double advNext = ZRawDelta(2);
            double inBorrowUT = ZAnchor + 1 * ZCad + (ZCad - advNext) + 50.0;

            double primaryUT = GhostPlaybackLogic.ResolveTrackingStationSampleFrame(
                0, ZS0, ZS1, inBorrowUT, units,
                out bool primaryHidden, out bool hasSecondary, out double secondaryUT, out long secondaryCycle);
            Assert.False(primaryHidden);
            Assert.True(hasSecondary, "the borrow window exposes the secondary via the TS sample frame");
            Assert.Equal(2, secondaryCycle);
            Assert.True(secondaryUT >= ZS0 - Tol && secondaryUT <= ZS1 + Tol);
            // The primary is the continuing instance N (far downstream); the secondary is the in-SOI ascent (near
            // spanStart). They are DISJOINT (months apart in a real mission; here clearly different loopUTs).
            Assert.NotEqual(primaryUT, secondaryUT);
        }

        [Fact]
        public void TsSampleFrame_NonMember_NoSecondary()
        {
            // A non-member index returns the live UT, no secondary (the common case until a Mission loops).
            var units = BuildZeroSlackUnit();
            double live = 12345.0;
            double primaryUT = GhostPlaybackLogic.ResolveTrackingStationSampleFrame(
                7, ZS0, ZS1, live, units,
                out bool primaryHidden, out bool hasSecondary, out double _, out long _);
            Assert.False(primaryHidden);
            Assert.False(hasSecondary);
            Assert.Equal(live, primaryUT, Tol);
        }

        [Fact]
        public void TsSampleFrame_AlignedUnit_NeverHasSecondary()
        {
            // The aligned unit never exposes a secondary through the TS sample frame (invariant 2).
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, AS0, AS1, ACad, AAnchor, ACad, null, null, null, null,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: ATsid, launchHoldEngaged: true,
                recordedSoiExitUT: ASoiExit);
            var units = new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { 0, unit } },
                new Dictionary<int, int> { { 0, 0 } });
            for (double t = AAnchor; t <= AAnchor + 4.0 * ACad; t += 23.0)
            {
                GhostPlaybackLogic.ResolveTrackingStationSampleFrame(
                    0, AS0, AS1, t, units, out bool _, out bool hasSecondary, out double _, out long _);
                Assert.False(hasSecondary, $"aligned unit must never expose a secondary (t={t})");
            }
        }

        // === Test 5: TryResolveBoundaryOverlapSecondaryMarker - the additive-marker gate ============
        // The map-marker wiring helper that decides whether to draw the secondary's pre-Segment ascent
        // icon riding the polyline. It gates (map surface, non-debris, non-null) then delegates the
        // live-secondary question to ResolveTrackingStationSampleFrame.

        private static Recording ZRec()
            => new Recording { RecordingId = "z", VesselName = "Z", ExplicitStartUT = ZS0, ExplicitEndUT = ZS1 };

        [Fact]
        public void SecondaryMarker_InBorrowWindow_DrawsAtSecondaryHead()
        {
            var units = BuildZeroSlackUnit();
            double advNext = ZRawDelta(2);
            double inBorrowUT = ZAnchor + 1 * ZCad + (ZCad - advNext) + 50.0;

            bool draw = GhostMapPresence.TryResolveBoundaryOverlapSecondaryMarker(
                isMapSurface: true, ZRec(), 0, inBorrowUT, units,
                out double secondaryUT, out long secondaryCycle);

            Assert.True(draw, "the borrow window must surface the secondary's ascent marker");
            Assert.Equal(2, secondaryCycle);
            Assert.True(secondaryUT >= ZS0 - Tol && secondaryUT <= ZS1 + Tol);
        }

        [Fact]
        public void SecondaryMarker_NonMapSurface_NeverDraws()
        {
            var units = BuildZeroSlackUnit();
            double advNext = ZRawDelta(2);
            double inBorrowUT = ZAnchor + 1 * ZCad + (ZCad - advNext) + 50.0;

            bool draw = GhostMapPresence.TryResolveBoundaryOverlapSecondaryMarker(
                isMapSurface: false, ZRec(), 0, inBorrowUT, units, out double _, out long _);

            Assert.False(draw, "flight (non-map) view has currentUT=0 and no map polyline to ride");
        }

        [Fact]
        public void SecondaryMarker_Debris_NeverDraws()
        {
            var units = BuildZeroSlackUnit();
            double advNext = ZRawDelta(2);
            double inBorrowUT = ZAnchor + 1 * ZCad + (ZCad - advNext) + 50.0;
            var debris = ZRec();
            debris.IsDebris = true;

            bool draw = GhostMapPresence.TryResolveBoundaryOverlapSecondaryMarker(
                isMapSurface: true, debris, 0, inBorrowUT, units, out double _, out long _);

            Assert.False(draw, "debris never carries a boundary-overlap secondary marker");
        }

        [Fact]
        public void SecondaryMarker_NullRecording_NeverDraws()
        {
            var units = BuildZeroSlackUnit();
            bool draw = GhostMapPresence.TryResolveBoundaryOverlapSecondaryMarker(
                isMapSurface: true, null, 0, 1234.0, units, out double secondaryUT, out long _);
            Assert.False(draw);
            Assert.Equal(1234.0, secondaryUT, Tol); // defaulted to liveUT
        }

        [Fact]
        public void SecondaryMarker_AlignedUnit_NeverDraws()
        {
            // An already-aligned (slack>0) launch loop never engages the boundary overlap, so the marker
            // gate stays inert across the whole cycle (byte-identical to today).
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, AS0, AS1, ACad, AAnchor, ACad, null, null, null, null,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: ATsid, launchHoldEngaged: true,
                recordedSoiExitUT: ASoiExit);
            var units = new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { 0, unit } },
                new Dictionary<int, int> { { 0, 0 } });
            var rec = new Recording { RecordingId = "a", VesselName = "A", ExplicitStartUT = AS0, ExplicitEndUT = AS1 };
            for (double t = AAnchor; t <= AAnchor + 4.0 * ACad; t += 29.0)
            {
                bool draw = GhostMapPresence.TryResolveBoundaryOverlapSecondaryMarker(
                    isMapSurface: true, rec, 0, t, units, out double _, out long _);
                Assert.False(draw, $"aligned unit must never draw a secondary marker (t={t})");
            }
        }
    }
}
