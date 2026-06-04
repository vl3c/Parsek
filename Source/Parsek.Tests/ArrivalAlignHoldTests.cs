using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    // Re-aim Phase 4 (cross-parent destination-SOI arrival alignment), implementation Phase 3a:
    // the PURE loop-clock helpers for the arrival HOLD (the inverse of a loiter cut). These delay the
    // in-SOI replay so the destination's rotation phase at SOI entry recurs to its recorded value.
    // No engine wiring is exercised here (the helpers are additive and unwired this phase); the loop
    // clock integration is Phase 3b. GhostPlaybackLogic is internal in namespace Parsek; Parsek.Tests
    // sees it directly.
    public class ArrivalAlignHoldTests
    {
        private const double Tol = 1e-9;

        // === ComputeArrivalAlignHoldSeconds ===================================================

        [Theory]
        [InlineData(0.0)]
        [InlineData(-100.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Hold_DegenerateRotationPeriod_ReturnsZero(double rotationPeriod)
        {
            Assert.Equal(0.0, GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(1000.0, 350.0, rotationPeriod));
        }

        [Fact]
        public void Hold_NaNInputs_ReturnsZero()
        {
            Assert.Equal(0.0, GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(double.NaN, 350.0, 100.0));
            Assert.Equal(0.0, GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(1000.0, double.NaN, 100.0));
        }

        [Fact]
        public void Hold_AlreadyAligned_ReturnsZero()
        {
            // entryLive differs from recordedArrival by an exact multiple of T_rot -> already aligned.
            double w = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(1000.0, 1000.0 - 3.0 * 100.0, 100.0);
            Assert.Equal(0.0, w, 9);
        }

        [Fact]
        public void Hold_GeneralCase_AlignsForward()
        {
            // recordedArrival=1000, entryLive=350, T_rot=100 -> W=50; (entryLive+W) aligns to recorded mod T_rot.
            double tRot = 100.0;
            double w = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(1000.0, 350.0, tRot);
            Assert.Equal(50.0, w, 9);
            Assert.Equal((1000.0 % tRot + tRot) % tRot, ((350.0 + w) % tRot + tRot) % tRot, 9);
        }

        [Fact]
        public void Hold_NegativeDelta_NormalizedForwardIntoRange()
        {
            // entryLive AHEAD of recordedArrival: the minimal forward hold still aligns and stays in [0,T_rot).
            double tRot = 100.0;
            double w = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(1000.0, 1230.0, tRot);
            Assert.Equal(70.0, w, 9);
            Assert.True(w >= 0.0 && w < tRot);
            Assert.Equal((1000.0 % tRot + tRot) % tRot, ((1230.0 + w) % tRot + tRot) % tRot, 9);
        }

        [Theory]
        [InlineData(70898646.0, 70898646.0 - 19653076.0, 65518.0)]   // Duna-ish synodic shift, Duna rotation
        [InlineData(123456.0, 654321.0, 21549.425)]                  // Kerbin rotation
        [InlineData(0.0, 999999.0, 65518.0)]
        public void Hold_AlwaysInRange_AndAligns(double recArrival, double entryLive, double tRot)
        {
            double w = GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds(recArrival, entryLive, tRot);
            Assert.True(w >= 0.0 && w < tRot, $"W={w} out of [0,{tRot})");
            // The shifted entry is aligned to the recorded entry's rotation phase.
            double recPhase = (recArrival % tRot + tRot) % tRot;
            double livePhase = ((entryLive + w) % tRot + tRot) % tRot;
            Assert.Equal(recPhase, livePhase, 6);
        }

        // === ApplyArrivalHoldToPhase ==========================================================

        [Theory]
        [InlineData(0.0)]
        [InlineData(-5.0)]
        [InlineData(double.NaN)]
        public void Apply_ZeroOrNegativeHold_Identity(double holdSeconds)
        {
            Assert.Equal(400.0, GhostPlaybackLogic.ApplyArrivalHoldToPhase(400.0, 500.0, holdSeconds), Tol);
            Assert.Equal(900.0, GhostPlaybackLogic.ApplyArrivalHoldToPhase(900.0, 500.0, holdSeconds), Tol);
        }

        [Fact]
        public void Apply_BeforeBoundary_Identity()
        {
            Assert.Equal(400.0, GhostPlaybackLogic.ApplyArrivalHoldToPhase(400.0, 500.0, 70.0), Tol);
            Assert.Equal(500.0, GhostPlaybackLogic.ApplyArrivalHoldToPhase(500.0, 500.0, 70.0), Tol); // at boundary
        }

        [Fact]
        public void Apply_WithinHold_HeldAtBoundary()
        {
            // 500 < eff <= 570 maps to the boundary 500 (the ghost waits at SOI arrival).
            Assert.Equal(500.0, GhostPlaybackLogic.ApplyArrivalHoldToPhase(530.0, 500.0, 70.0), Tol);
            Assert.Equal(500.0, GhostPlaybackLogic.ApplyArrivalHoldToPhase(570.0, 500.0, 70.0), Tol); // at hold end
        }

        [Fact]
        public void Apply_AfterHold_DeferredByHold()
        {
            // eff > 570 resumes the recorded sequence, shifted earlier by the hold (70).
            Assert.Equal(530.0, GhostPlaybackLogic.ApplyArrivalHoldToPhase(600.0, 500.0, 70.0), Tol);
            Assert.Equal(501.0, GhostPlaybackLogic.ApplyArrivalHoldToPhase(571.0, 500.0, 70.0), Tol); // just past
        }

        [Fact]
        public void Apply_IsContinuousAcrossTheHold()
        {
            // No jump at either edge of the hold window: ..., 500 at the boundary, 500 held, 500 -> 500+eps after.
            double atEnd = GhostPlaybackLogic.ApplyArrivalHoldToPhase(570.0, 500.0, 70.0);
            double justAfter = GhostPlaybackLogic.ApplyArrivalHoldToPhase(570.0 + 1e-6, 500.0, 70.0);
            Assert.Equal(500.0, atEnd, Tol);
            Assert.True(justAfter - atEnd >= 0.0 && justAfter - atEnd < 1e-5);
        }

        [Fact]
        public void Apply_InsertsExactlyHoldSeconds_PostBoundaryRoundTrip()
        {
            // A recorded-span position past the boundary is reached from effective phase = recordedPos + hold,
            // i.e. the hold inserts exactly holdSeconds of dead time (the inverse of a cut's removal).
            double holdPhasePos = 500.0, hold = 70.0, recordedPos = 530.0;
            Assert.Equal(recordedPos,
                GhostPlaybackLogic.ApplyArrivalHoldToPhase(recordedPos + hold, holdPhasePos, hold), Tol);
        }

        // === TryComputeSpanLoopUT with the arrival hold (3b loop-clock wiring) ================

        [Fact]
        public void Clock_NoHold_NoCut_IsIdentityToTheRecordedSpan()
        {
            // No hold + no cuts: loopUT = spanStartUT + phaseInCycle (the pre-hold behavior). cadence > span.
            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                300.0, 0.0, 0.0, 1000.0, 2000.0,
                out double loopUT, out long cyc, out bool tail);
            Assert.True(ok);
            Assert.Equal(300.0, loopUT, Tol);
            Assert.Equal(0, cyc);
            Assert.False(tail);

            // Past the span: parked at spanEnd, tail set.
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                1500.0, 0.0, 0.0, 1000.0, 2000.0, out loopUT, out cyc, out tail);
            Assert.Equal(1000.0, loopUT, Tol);
            Assert.True(tail);
        }

        [Fact]
        public void Clock_WithHold_BeforeIdentity_WithinHeld_AfterDeferred()
        {
            // Hold of 200 s at recorded-span boundary 600 (span 0..1000, cadence 2000 > span+hold). Before the
            // boundary: identity (launch + transfer unchanged). Within the hold [600,800]: held at 600 (the
            // ghost waits at SOI arrival). After: the recorded in-SOI replay, deferred by the hold.
            const double anchor = 0, s0 = 0, s1 = 1000, cad = 2000, hold = 200, holdAt = 600;
            void Check(double currentUT, double expect)
            {
                GhostPlaybackLogic.TryComputeSpanLoopUT(
                    currentUT, anchor, s0, s1, cad, out double loopUT, out long _, out bool _,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: hold, arrivalHoldAtUT: holdAt);
                Assert.Equal(expect, loopUT, Tol);
            }
            Check(500.0, 500.0);   // before the boundary: identity
            Check(700.0, 600.0);   // within the hold: held at the boundary
            Check(900.0, 700.0);   // after the hold: recorded loopUT deferred by 200
            Check(1150.0, 950.0);  // still in the deferred in-SOI replay

            // Past effectiveSpan (1200): parked at spanEnd, tail set.
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                1300.0, anchor, s0, s1, cad, out double lp, out long _, out bool tail,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: hold, arrivalHoldAtUT: holdAt);
            Assert.Equal(1000.0, lp, Tol);
            Assert.True(tail);
        }

        [Fact]
        public void Clock_HoldComposesWithLoiterCut()
        {
            // A loiter cut [200,300] (excise 100 s, before the boundary) composes with a 200 s hold at the
            // recorded boundary 600. Compressed boundary = 600 - 100 = 500. The cut shifts recorded UTs up by
            // 100 after 200; the hold then holds at the recorded boundary 600 and defers the in-SOI replay.
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 200.0, LengthSeconds = 100.0 },
            };
            const double anchor = 0, s0 = 0, s1 = 1000, cad = 2000, hold = 200, holdAt = 600;
            void Check(double currentUT, double expect)
            {
                GhostPlaybackLogic.TryComputeSpanLoopUT(
                    currentUT, anchor, s0, s1, cad, out double loopUT, out long _, out bool _,
                    schedule: null, loiterCuts: cuts, arrivalHoldSeconds: hold, arrivalHoldAtUT: holdAt);
                Assert.Equal(expect, loopUT, Tol);
            }
            Check(400.0, 500.0);   // compressed phase 400 (< compressed boundary 500): skip the cut -> recorded 500
            Check(550.0, 600.0);   // within the hold: held at the recorded boundary 600
            Check(900.0, 800.0);   // after the hold: deferred; compressed 700 -> recorded 800 (cut skipped)
        }
    }
}
