using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Per-loop LAUNCH HOLD (docs/dev/design-reaim-launch-hold-seam.md): the PURE residual helper
    /// (<see cref="GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds"/>) that defers replayed loop N's
    /// launch instant by H_N (in [0, T_sid)) so the launch body rotates back to the recorded inertial
    /// orientation and the body-fixed launch ascent coincides with the frozen escape conic (the
    /// launch->escape render seam closes), launch on the real pad. The span-clock composition (pre-launch
    /// ABSENCE, verbatim-deferred replay, arrival-wait self-correction, targeting invariants) is covered in
    /// LaunchHoldClockTests; the log-assertion test is in LaunchHoldLoggingTests. The helper is pure (no
    /// Unity), so Parsek.Tests drives it directly.
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
            // A degenerate T_sid (non-rotating launch body): no pad realignment possible, no hold.
            double h = GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(1000.0, 100.0, 5L, 200000.0, rotationPeriod);
            Assert.Equal(0.0, h, Tol);
        }

        [Fact]
        public void NaNDisplacementInputs_ReturnZero()
        {
            Assert.Equal(0.0, GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(double.NaN, 100.0, 3L, 200000.0, 21600.0), Tol);
            Assert.Equal(0.0, GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(1000.0, double.NaN, 3L, 200000.0, 21600.0), Tol);
        }

        // === Realignment property: (Off_N + H_N) is a whole multiple of T_sid =====================

        [Theory]
        [InlineData(1000.0, 100.0, 200000.0, 0L, 21600.0)]
        [InlineData(1000.0, 100.0, 200000.0, 1L, 21600.0)]
        [InlineData(1000.0, 100.0, 200000.0, 7L, 21600.0)]
        [InlineData(123456.0, 654.0, 19653076.0, 13L, 21549.425)]
        [InlineData(-5000.0, 1000.0, 88775.0, 4L, 88775.0)]   // retrograde-ish period via Math.Abs covered below
        public void RealignsModTsid(double phaseAnchor, double spanStart, double cadence, long n, double tSid)
        {
            double h = GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(phaseAnchor, spanStart, n, cadence, tSid);
            double offN = (phaseAnchor - spanStart) + n * cadence;
            double aligned = ((offN + h) % tSid + tSid) % tSid;
            Assert.Equal(0.0, aligned, 4);   // the shifted launch displacement is a whole number of T_sid
        }

        [Fact]
        public void RetrogradePeriod_UsesAbsoluteValue()
        {
            // The Math.Abs matches PadAlignLaunch's retrograde handling: a negative period aligns the same
            // way as its absolute value.
            double tSid = 21600.0;
            double hPos = GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(1000.0, 100.0, 3L, 200000.0, tSid);
            double hNeg = GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(1000.0, 100.0, 3L, 200000.0, -tSid);
            Assert.Equal(hPos, hNeg, Tol);
        }

        // === Range: H_N in [0, T_sid) for any N (incl. large N, negative inner term) =============

        [Theory]
        [InlineData(1000.0, 100.0, 200000.0, 21600.0)]
        [InlineData(123456.0, 654.0, 19653076.0, 21549.425)]
        [InlineData(0.0, 99999.0, 17.0, 100.0)]               // tiny cadence vs period
        public void AlwaysInRange_AcrossManyLoops(double phaseAnchor, double spanStart, double cadence, double tSid)
        {
            for (long n = 0; n < 200; n++)
            {
                double h = GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(phaseAnchor, spanStart, n, cadence, tSid);
                Assert.True(h >= 0.0 && h < tSid, $"N={n} H={h} out of [0,{tSid})");
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
            // cadence = 3*T_sid, so Off_N = (2 + 3N)*T_sid is always aligned -> H_N == 0.
            const double tSid = 21600.0;
            double spanStart = 100.0;
            double phaseAnchor = spanStart + 2.0 * tSid;
            double cadence = 3.0 * tSid;
            double h = GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(phaseAnchor, spanStart, n, cadence, tSid);
            Assert.Equal(0.0, h, 3);
        }

        // === Sawtooth in N: H_{N+1} - H_N == (-cadence) mod T_sid (the per-loop drift) ============

        [Fact]
        public void SawtoothInN_StepIsNegativeCadenceModTsid()
        {
            const double phaseAnchor = 1000.0, spanStart = 100.0, cadence = 200000.0, tSid = 21600.0;
            // The per-loop step is the residual of (-cadence) mod T_sid, wrapped into [0, T_sid).
            double expectStep = ((-cadence) % tSid + tSid) % tSid;
            for (long n = 0; n < 50; n++)
            {
                double hN = GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(phaseAnchor, spanStart, n, cadence, tSid);
                double hNext = GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(phaseAnchor, spanStart, n + 1, cadence, tSid);
                double step = ((hNext - hN) % tSid + tSid) % tSid;
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
                double h = GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(phaseAnchor, spanStart, n, cadence, tSid);
                Assert.True(h >= 0.0 && h < tSid, $"N={n} H={h} grew past [0,{tSid})");
            }
        }
    }
}
