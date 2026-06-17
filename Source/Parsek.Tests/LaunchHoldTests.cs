using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Per-loop LAUNCH ALIGNMENT (docs/dev/design-reaim-launch-hold-seam.md, borrow-at-launch /
    /// repay-at-SOI-exit): the PURE residual helper
    /// (<see cref="GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds"/>) that returns the per-loop
    /// BACKWARD advance delta_N (in [0, T_sid)) to SUBTRACT from replayed loop N's nominal launch L_N so
    /// the in-Kerbin-SOI replay runs at the recorded launch-body rotation (the launch->escape render seam
    /// closes), launch on the real pad delta_N EARLIER. The borrowed delta_N is repaid as a coast hold at
    /// the SOI exit (covered by the span-clock composition in LaunchHoldClockTests; the log-assertion test
    /// is in LaunchHoldLoggingTests). The helper is pure (no Unity), so Parsek.Tests drives it directly.
    /// </summary>
    public class LaunchHoldTests
    {
        private const double Tol = 1e-6;

        // === Degenerate / NaN guards (mirror ComputeArrivalAlignHoldSeconds) =====================

        [Theory]
        [InlineData(0.0)]
        [InlineData(-100.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void NonRotatingLaunchBody_ReturnsZero(double rotationPeriod)
        {
            // A degenerate T_sid (non-rotating launch body): no pad realignment possible, no advance.
            double d = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(1000.0, 100.0, 5L, 200000.0, rotationPeriod);
            Assert.Equal(0.0, d, Tol);
        }

        [Fact]
        public void NaNDisplacementInputs_ReturnZero()
        {
            Assert.Equal(0.0, GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(double.NaN, 100.0, 3L, 200000.0, 21600.0), Tol);
            Assert.Equal(0.0, GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(1000.0, double.NaN, 3L, 200000.0, 21600.0), Tol);
        }

        // === Realignment property: (Off_N - delta_N) is a whole multiple of T_sid ==================

        [Theory]
        [InlineData(1000.0, 100.0, 200000.0, 0L, 21600.0)]
        [InlineData(1000.0, 100.0, 200000.0, 1L, 21600.0)]
        [InlineData(1000.0, 100.0, 200000.0, 7L, 21600.0)]
        [InlineData(123456.0, 654.0, 19653076.0, 13L, 21549.425)]
        [InlineData(-5000.0, 1000.0, 88775.0, 4L, 88775.0)]   // retrograde-ish period via Math.Abs covered below
        public void RealignsModTsid_BackwardSubtraction(double phaseAnchor, double spanStart, double cadence, long n, double tSid)
        {
            double d = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(phaseAnchor, spanStart, n, cadence, tSid);
            double offN = (phaseAnchor - spanStart) + n * cadence;
            // Launching at L_N - delta_N makes the in-SOI displacement (Off_N - delta_N) a whole number of
            // T_sid, so the live launch-body rotation equals the recorded rotation throughout the in-SOI replay.
            // The residual can land just below T_sid (float wrap of an exact-zero), so measure the distance to
            // the NEAREST whole multiple of T_sid.
            double rem = ((offN - d) % tSid + tSid) % tSid;
            double aligned = Math.Min(rem, tSid - rem);
            Assert.True(aligned < 1e-3, $"residual {aligned} not aligned mod {tSid}");
        }

        [Fact]
        public void RetrogradePeriod_UsesAbsoluteValue()
        {
            // The Math.Abs matches PadAlignLaunch's retrograde handling: a negative period aligns the same
            // way as its absolute value.
            double tSid = 21600.0;
            double dPos = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(1000.0, 100.0, 3L, 200000.0, tSid);
            double dNeg = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(1000.0, 100.0, 3L, 200000.0, -tSid);
            Assert.Equal(dPos, dNeg, Tol);
        }

        // === Range: delta_N in [0, T_sid) for any N (incl. large N, negative inner term) ==========

        [Theory]
        [InlineData(1000.0, 100.0, 200000.0, 21600.0)]
        [InlineData(123456.0, 654.0, 19653076.0, 21549.425)]
        [InlineData(0.0, 99999.0, 17.0, 100.0)]               // tiny cadence vs period
        public void AlwaysInRange_AcrossManyLoops(double phaseAnchor, double spanStart, double cadence, double tSid)
        {
            for (long n = 0; n < 200; n++)
            {
                double d = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(phaseAnchor, spanStart, n, cadence, tSid);
                Assert.True(d >= 0.0 && d < tSid, $"N={n} delta={d} out of [0,{tSid})");
            }
        }

        // === Already aligned -> 0 =================================================================

        [Theory]
        [InlineData(0L)]
        [InlineData(1L)]
        [InlineData(9L)]
        public void AlreadyAligned_ReturnsZero(long n)
        {
            // Off_N an exact whole multiple of T_sid for every N: phaseAnchor-spanStart = 2*T_sid and
            // cadence = 3*T_sid, so Off_N = (2 + 3N)*T_sid is always aligned -> delta_N == 0.
            const double tSid = 21600.0;
            double spanStart = 100.0;
            double phaseAnchor = spanStart + 2.0 * tSid;
            double cadence = 3.0 * tSid;
            double d = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(phaseAnchor, spanStart, n, cadence, tSid);
            Assert.Equal(0.0, d, 3);
        }

        // === Sawtooth in N: delta_{N+1} - delta_N == cadence mod T_sid (the per-loop drift) ========

        [Fact]
        public void SawtoothInN_StepIsCadenceModTsid()
        {
            const double phaseAnchor = 1000.0, spanStart = 100.0, cadence = 200000.0, tSid = 21600.0;
            // The per-loop step is the residual of (+cadence) mod T_sid, wrapped into [0, T_sid) (delta_N is
            // Off_N mod T_sid, so adding one more cadence advances delta by cadence mod T_sid).
            double expectStep = (cadence % tSid + tSid) % tSid;
            for (long n = 0; n < 50; n++)
            {
                double dN = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(phaseAnchor, spanStart, n, cadence, tSid);
                double dNext = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(phaseAnchor, spanStart, n + 1, cadence, tSid);
                double step = ((dNext - dN) % tSid + tSid) % tSid;
                Assert.Equal(expectStep, step, 3);
            }
        }

        [Fact]
        public void NeverGrowsWithLoopIndex_BoundedByTsid()
        {
            // A sawtooth, not a ramp: the value never exceeds T_sid no matter how large N gets.
            const double phaseAnchor = 1000.0, spanStart = 100.0, cadence = 200000.0, tSid = 21600.0;
            for (long n = 0; n < 100000; n += 997)
            {
                double d = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(phaseAnchor, spanStart, n, cadence, tSid);
                Assert.True(d >= 0.0 && d < tSid, $"N={n} delta={d} grew past [0,{tSid})");
            }
        }

        // === Common-case parity with the worked example (N=13: delta=5058 <= slack=5419) ==========

        [Fact]
        public void WorkedExample_N13_DeltaInRange()
        {
            // The design's worked common case (s15 / Kerbal X #2): N=13 delta ~ 5058 s, well inside the
            // Kerbin sidereal day. We only assert it is in range here (the slack comparison is a clock-level
            // concern covered in LaunchHoldClockTests); the exact value depends on the real phaseAnchor.
            const double tSid = 21549.425;   // Kerbin sidereal day
            double d = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(123456.0, 654.0, 13L, 19653076.0, tSid);
            Assert.True(d >= 0.0 && d < tSid);
        }

        // === ComputeCappedLaunchAdvanceSeconds: min(delta_win, slack_{win-1}) =====================
        //
        // Shared helper that bounds the per-loop launch advance to the LAUNCHING cycle's idle gap so the
        // span clock (region A/B) and the Missions-window navigable launch time agree on the SAME capped
        // value. slack_{win-1} = cadence - compressedSpan - W_{win-1}.

        [Fact]
        public void Capped_NoHoldNoCut_ReturnsDeltaWhenUnderSlack()
        {
            // span 1000, cadence 2000 -> compressedSpan 1000, no hold so W=0, slack = 2000-1000 = 1000.
            // delta_N for N>=0 is (anchor-spanStart + N*cadence) mod 700 (300, 200, 100, ...), all < 1000,
            // so the capped advance equals the raw delta.
            const double anchor = 300, s0 = 0, s1 = 1000, cad = 2000, tSid = 700;
            for (long n = 0; n < 6; n++)
            {
                double raw = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(anchor, s0, n, cad, tSid);
                double capped = GhostPlaybackLogic.ComputeCappedLaunchAdvanceSeconds(
                    anchor, s0, s1, cad, n, tSid, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalAlignPeriod: double.NaN);
                Assert.Equal(raw, capped, Tol);
            }
        }

        [Fact]
        public void Capped_DeltaExceedsSlack_ClampedToSlack()
        {
            // span 1000, cadence 1100 -> compressedSpan 1000, W=0, slack = 100. With T_sid 700 the raw delta
            // can reach ~700 >> 100, so the capped advance is bounded at slack (100).
            const double anchor = 300, s0 = 0, s1 = 1000, cad = 1100, tSid = 700;
            for (long n = 0; n < 8; n++)
            {
                double raw = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(anchor, s0, n, cad, tSid);
                double capped = GhostPlaybackLogic.ComputeCappedLaunchAdvanceSeconds(
                    anchor, s0, s1, cad, n, tSid, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalAlignPeriod: double.NaN);
                double slack = cad - (s1 - s0); // 100
                Assert.True(capped <= slack + Tol, $"N={n} capped {capped} > slack {slack}");
                Assert.Equal(System.Math.Min(raw, slack), capped, 1e-3);
            }
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-100.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Capped_DegenerateOrNaNPeriod_ReturnsZero(double rotationPeriod)
        {
            double capped = GhostPlaybackLogic.ComputeCappedLaunchAdvanceSeconds(
                300.0, 0.0, 1000.0, 2000.0, 3L, rotationPeriod, loiterCuts: null,
                arrivalHoldSeconds: 0.0, arrivalAlignPeriod: double.NaN);
            Assert.Equal(0.0, capped, Tol);
        }

        [Fact]
        public void Capped_NaNDisplacement_ReturnsZero()
        {
            Assert.Equal(0.0, GhostPlaybackLogic.ComputeCappedLaunchAdvanceSeconds(
                double.NaN, 0.0, 1000.0, 2000.0, 3L, 700.0, null, 0.0, double.NaN), Tol);
            Assert.Equal(0.0, GhostPlaybackLogic.ComputeCappedLaunchAdvanceSeconds(
                300.0, double.NaN, 1000.0, 2000.0, 3L, 700.0, null, 0.0, double.NaN), Tol);
        }

        [Fact]
        public void Capped_HoldReducesSlack_UsesLaunchingCycleHold()
        {
            // The cap uses W_{win-1} (the LAUNCHING cycle's per-loop arrival hold), not W_win. Build a fixture
            // where the per-loop hold varies per cycle so slack_{win-1} != slack_win, and assert the helper's
            // slack matches a hand recomputation using cycle win-1.
            const double anchor = 300, s0 = 0, s1 = 1000, cad = 2000, tSid = 700;
            const double w0 = 400, tAlign = 250;  // per-loop hold drifts each cycle
            long win = 5;
            double wPrev = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(w0, win - 1, cad, tAlign);
            double compressedSpan = s1 - s0; // no cut
            // mirror the clock's hold clamp
            if (wPrev > 0.0 && compressedSpan + wPrev > cad)
                wPrev = System.Math.Max(0.0, cad - compressedSpan);
            double slackPrev = System.Math.Max(0.0, cad - compressedSpan - wPrev);
            double rawDelta = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(anchor, s0, win, cad, tSid);
            double expected = System.Math.Min(rawDelta, slackPrev);
            double capped = GhostPlaybackLogic.ComputeCappedLaunchAdvanceSeconds(
                anchor, s0, s1, cad, win, tSid, loiterCuts: null, arrivalHoldSeconds: w0, arrivalAlignPeriod: tAlign);
            Assert.Equal(expected, capped, 1e-3);
        }

        [Fact]
        public void Capped_WithLoiterCut_UsesCompressedSpan()
        {
            // A whole-period loiter cut compresses the span; slack uses the COMPRESSED span. span 1000, cut 600
            // -> compressedSpan 400, cadence 1100, W=0 -> slack = 1100-400 = 700. Raw delta (T_sid 700) maxes
            // just under 700, so the cap rarely bites; assert the helper's value matches min(delta, 700).
            const double anchor = 300, s0 = 0, s1 = 1000, cad = 1100, tSid = 700;
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 200, LengthSeconds = 600 }
            };
            for (long n = 0; n < 6; n++)
            {
                double raw = GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(anchor, s0, n, cad, tSid);
                double capped = GhostPlaybackLogic.ComputeCappedLaunchAdvanceSeconds(
                    anchor, s0, s1, cad, n, tSid, cuts, arrivalHoldSeconds: 0.0, arrivalAlignPeriod: double.NaN);
                double slack = cad - 400.0; // compressedSpan = 400
                Assert.Equal(System.Math.Min(raw, slack), capped, 1e-3);
            }
        }
    }
}
