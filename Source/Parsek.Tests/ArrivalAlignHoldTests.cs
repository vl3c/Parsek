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

        [Fact]
        public void Clock_HoldClampedSoEffectiveSpanNeverExceedsCadence()
        {
            // Pathological: a 200 s hold with cadence 1100 and span 1000 would make effectiveSpan 1200 > the
            // cadence, wrapping a cycle mid-span. The clamp caps the hold at cadence - span = 100, so
            // effectiveSpan == cadence and the in-SOI replay is never truncated. (No real caller trips this;
            // the re-aim cadence is synodic.) Verify the hold defers by only the clamped 100 s, not 200.
            void Loop(double currentUT, out double loopUT)
            {
                GhostPlaybackLogic.TryComputeSpanLoopUT(
                    currentUT, 0.0, 0.0, 1000.0, 1100.0, out loopUT, out long _, out bool _,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: 200.0, arrivalHoldAtUT: 600.0);
            }
            Loop(650.0, out double a); Assert.Equal(600.0, a, Tol);   // within the clamped hold: held at 600
            Loop(1050.0, out double b); Assert.Equal(950.0, b, Tol);  // after: deferred by the clamped 100 (not 200)
        }

        // === ComputePerLoopArrivalHoldSeconds (13c dynamic per-loop hold) =====================

        [Theory]
        [InlineData(0L)]
        [InlineData(1L)]
        [InlineData(7L)]
        [InlineData(1000L)]
        public void PerLoop_ZeroBaseHold_ReturnsZeroForEveryN(long cycleIndex)
        {
            // W_0 = 0 (alignment Off / Drop) must stay 0 for every loop - the 13b regression fence. The bare
            // formula is a nonzero per-loop sawtooth at W_0 = 0, so the gate is what keeps Off byte-identical.
            double wn = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(0.0, cycleIndex, 19653076.0, 65518.0);
            Assert.Equal(0.0, wn, Tol);
        }

        [Theory]
        [InlineData(-5.0)]
        [InlineData(double.NaN)]
        public void PerLoop_NonPositiveBaseHold_ReturnsBaseUnchanged(double w0)
        {
            // The gate is strictly W_0 > 0; any non-positive (or NaN) base hold passes through unchanged.
            double wn = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(w0, 3L, 19653076.0, 65518.0);
            Assert.Equal(w0, wn, Tol);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-100.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void PerLoop_DegenerateRotationPeriod_ReturnsBaseHoldUnchanged(double rotationPeriod)
        {
            // No rotation constraint (NaN sentinel / non-positive / infinite T_rot): no per-loop adjustment,
            // the constant base hold is preserved (a re-aim unit with no destination rotation constraint).
            const double w0 = 12345.0;
            double wn = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(w0, 4L, 19653076.0, rotationPeriod);
            Assert.Equal(w0, wn, Tol);
        }

        [Fact]
        public void PerLoop_NEquals0_ReturnsBaseHold()
        {
            // At N=0 the subtracted drift is 0, so W_0 is returned unchanged (already in [0,T_rot) by
            // construction from ComputeArrivalAlignHoldSeconds).
            const double w0 = 46450.59, cadence = 19653076.0, tRot = 65518.0;
            double wn = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(w0, 0L, cadence, tRot);
            Assert.Equal(w0, wn, 6);
        }

        [Fact]
        public void PerLoop_NEquals1_SubtractsCadenceModTrot_Wrapped()
        {
            // N=1 subtracts (cadence mod T_rot), mod-wrapped into [0,T_rot). Hand-computed reference.
            const double w0 = 46450.59, cadence = 19653076.0, tRot = 65518.0;
            double drift = cadence % tRot;                       // the per-loop rotation-phase step
            double expect = ((w0 - drift) % tRot + tRot) % tRot; // mod-wrapped
            double wn = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(w0, 1L, cadence, tRot);
            Assert.Equal(expect, wn, 6);
            Assert.True(wn >= 0.0 && wn < tRot, $"WN={wn} out of [0,{tRot})");
        }

        [Fact]
        public void PerLoop_WrapAround_NegativeInnerBroughtBackIntoRange()
        {
            // A loop where (W_0 - N*drift) goes negative: the +T_rot in the double-mod brings it back into
            // [0,T_rot). Pick small W_0 and a drift that overshoots it at N=1.
            const double w0 = 10.0, tRot = 100.0;
            // cadence mod T_rot = 30, so W_1 = ((10 - 30) mod 100 + 100) mod 100 = 80.
            double wn = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(w0, 1L, 130.0, tRot);
            Assert.Equal(80.0, wn, Tol);
            Assert.True(wn >= 0.0 && wn < tRot);
        }

        [Theory]
        [InlineData(46450.59, 19653076.0, 65518.0)]   // Duna One: synodic cadence, Duna rotation
        [InlineData(900.0, 130.0, 100.0)]             // small overshooting drift
        [InlineData(50.0, 19653076.0, 21549.425)]     // Kerbin-ish rotation
        public void PerLoop_AlwaysInRange_AcrossManyLoops(double w0, double cadence, double tRot)
        {
            for (long n = 0; n < 64; n++)
            {
                double wn = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(w0, n, cadence, tRot);
                Assert.True(wn >= 0.0 && wn < tRot, $"N={n} WN={wn} out of [0,{tRot})");
            }
        }

        [Fact]
        public void PerLoop_ReAlignsDeorbitRotationPhaseEveryLoop()
        {
            // The alignment property: with the per-loop hold applied, the live deorbit lands at the SAME
            // destination rotation phase on every loop. The unshifted live deorbit on loop N sits at
            // (base + N*cadence) mod T_rot; adding W_N must drive it to the recorded phase (base + W_0) mod
            // T_rot for every N. (This is the recurrence's whole point.)
            const double w0 = 46450.59, cadence = 19653076.0, tRot = 65518.0, baseDeorbit = 123456.0;
            double recordedPhase = ((baseDeorbit + w0) % tRot + tRot) % tRot;
            for (long n = 0; n < 50; n++)
            {
                double wn = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(w0, n, cadence, tRot);
                double liveDeorbit = baseDeorbit + n * cadence + wn;
                double livePhase = (liveDeorbit % tRot + tRot) % tRot;
                Assert.Equal(recordedPhase, livePhase, 4);
            }
        }

        [Fact]
        public void PerLoop_FirstLoopMatchesConstantHold()
        {
            // On loop 0 the per-loop hold equals the constant W_0, so the reference loop is unchanged from the
            // pre-13c constant-hold behavior. (Cross-checks PerLoop_NEquals0 against the clock's W_0.)
            const double w0 = 46450.59, cadence = 19653076.0, tRot = 65518.0;
            Assert.Equal(w0, GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(w0, 0L, cadence, tRot), 6);
        }

        // === TryComputeSpanLoopUT byte-identical when Off / invalid T_rot (13c regression fence) ==

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(0.0)]
        [InlineData(-100.0)]
        [InlineData(65518.0)]
        public void Clock_NoHold_ByteIdenticalRegardlessOfRotationPeriod(double rotationPeriod)
        {
            // With arrivalHoldSeconds = 0 (alignment Off) the per-loop branch is gated out, so any
            // arrivalHoldAlignPeriod (including a valid one) leaves loopUT / cycleIndex byte-identical to
            // the no-period call, across a swept range of currentUT and several loops.
            const double anchor = 0, s0 = 0, s1 = 1000, cad = 1000; // cadence == span -> multiple loops
            for (double t = 0.0; t <= 5200.0; t += 37.0)
            {
                bool okBase = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, anchor, s0, s1, cad, out double baseUT, out long baseCyc, out bool baseTail);
                bool okNew = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, anchor, s0, s1, cad, out double newUT, out long newCyc, out bool newTail,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                    arrivalHoldAlignPeriod: rotationPeriod);
                Assert.Equal(okBase, okNew);
                Assert.Equal(baseUT, newUT, Tol);
                Assert.Equal(baseCyc, newCyc);
                Assert.Equal(baseTail, newTail);
            }
        }

        [Fact]
        public void Clock_WithHold_InvalidRotationPeriod_KeepsConstantHoldBehavior()
        {
            // arrivalHoldSeconds > 0 but T_rot invalid (NaN, the default): the per-loop branch returns the base
            // hold unchanged, so the clock matches the pre-13c constant-hold path on every loop.
            const double anchor = 0, s0 = 0, s1 = 1000, cad = 2000, hold = 200, holdAt = 600;
            void Check(double currentUT, double expect)
            {
                GhostPlaybackLogic.TryComputeSpanLoopUT(
                    currentUT, anchor, s0, s1, cad, out double loopUT, out long _, out bool _,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: hold, arrivalHoldAtUT: holdAt,
                    arrivalHoldAlignPeriod: double.NaN);
                Assert.Equal(expect, loopUT, Tol);
            }
            // Same expectations as Clock_WithHold_BeforeIdentity_WithinHeld_AfterDeferred (constant hold).
            Check(500.0, 500.0);
            Check(700.0, 600.0);
            Check(900.0, 700.0);
        }

        [Fact]
        public void Clock_WithHold_ValidRotationPeriod_AppliesPerLoopHoldOnLaterLoop()
        {
            // arrivalHoldSeconds > 0 and a valid T_rot: loop 0 uses W_0 (constant-hold behavior), a later loop
            // uses a DIFFERENT per-loop W_N, so the in-SOI replay defers by W_N (not the constant W_0). Cadence
            // > span+hold so the defense clamp never trips; a T_rot that yields a clear per-loop step.
            const double anchor = 0, s0 = 0, s1 = 1000, cad = 2000, hold = 200, holdAt = 600, tRot = 700;

            // Loop 0: W_0 = 200. elapsed 900 -> cyc 0, phase 900. holdPhasePos = 600; phase 900 > 600+200 ->
            // after the hold -> loopUT = 900 - 200 = 700 (the constant-hold reference loop).
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                900.0, anchor, s0, s1, cad, out double loop0, out long cyc0, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: hold, arrivalHoldAtUT: holdAt,
                arrivalHoldAlignPeriod: tRot);
            Assert.Equal(0, cyc0);
            Assert.Equal(700.0, loop0, Tol);

            // Loop 3: drift = cad mod tRot = 2000 mod 700 = 600; W_3 = ((200 - 3*600) mod 700 + 700) mod 700
            //        = ((200 - 1800) mod 700 + 700) mod 700 = 500.
            double w3 = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(hold, 3L, cad, tRot);
            Assert.Equal(500.0, w3, Tol);
            // currentUT inside loop 3 with phaseInCycle 900: elapsed 6900 -> cyc 3, phase 900. holdPhasePos =
            // 600; phase 900 <= 600 + W_3(500) = 1100 -> WITHIN the (larger) per-loop hold -> held at the
            // boundary 600 (NOT 700, which is what the constant W_0=200 would have produced).
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                6900.0, anchor, s0, s1, cad, out double loop3, out long cyc3, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: hold, arrivalHoldAtUT: holdAt,
                arrivalHoldAlignPeriod: tRot);
            Assert.Equal(3, cyc3);
            Assert.Equal(600.0, loop3, Tol);
        }

        [Fact]
        public void Clock_StationHold_TStationDrivesPerLoopDrift_EndToEnd()
        {
            // M4c (plan test 13): the clock's per-loop drift correction is period-agnostic - with
            // the unit carrying T_STATION as arrivalHoldAlignPeriod (the station-hold
            // substitution), W_N is computed against the station period, not any rotation value.
            // Same geometry as the T_rot test above but with a station-scale period: tStation =
            // 300; drift = cad mod tStation = 2000 mod 300 = 200; W_2 = ((200 - 2*200) mod 300 +
            // 300) mod 300 = 100. currentUT in loop 2 with phaseInCycle 900: holdPhasePos 600;
            // phase 900 > 600 + W_2(100) -> after the hold -> loopUT = 900 - 100 = 800.
            const double anchor = 0, s0 = 0, s1 = 1000, cad = 2000, hold = 200, holdAt = 600, tStation = 300;

            double w2 = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(hold, 2L, cad, tStation);
            Assert.Equal(100.0, w2, Tol);

            GhostPlaybackLogic.TryComputeSpanLoopUT(
                4900.0, anchor, s0, s1, cad, out double loop2, out long cyc2, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: hold, arrivalHoldAtUT: holdAt,
                arrivalHoldAlignPeriod: tStation);
            Assert.Equal(2, cyc2);
            Assert.Equal(800.0, loop2, Tol);
        }

        // === ComputePerLoopJointArrivalHoldSeconds (D8 landing+station, post-M4c wiring) ======

        private static double CircErr(double delta, double period)
            => MissionPeriodicity.CircularPhaseError(delta, period);

        [Fact]
        public void JointHold_StationExactAndRotationWithinTol_EveryCycle_NeverAccumulates()
        {
            // The zero-drift property of the joint per-loop hold: at EVERY cycle the total offset
            // from the recorded arrival is a whole number of station periods (station phase exact)
            // AND within the rotation tolerance - re-solved absolutely per cycle, so the check
            // holds at cycle 300 exactly as at cycle 0 (a fixed cadence with two incommensurate
            // periods would otherwise drift out within a loop or two). Geometry: T_sta 100 /
            // T_rot 360 / tol 5 (worst lattice miss-run 17 << the 64 budget).
            const double tSta = 100.0, tRot = 360.0, tol = 5.0;
            const double cadence = 12345.67;
            const double entryOffset0 = 1234.5;              // liveEntry_0 - recordedArrivalUT
            double w0 = ((-entryOffset0) % tSta + tSta) % tSta; // the station-lattice base (65.5)

            for (long n = 0; n < 300; n++)
            {
                double w = GhostPlaybackLogic.ComputePerLoopJointArrivalHoldSeconds(
                    w0, n, cadence, tSta, tRot, tol, entryOffset0, 64);
                double delta = entryOffset0 + n * cadence + w;
                Assert.True(CircErr(delta, tSta) < 1e-6,
                    $"cycle {n}: station phase error {CircErr(delta, tSta)} not exact");
                Assert.True(CircErr(delta, tRot) <= tol + 1e-9,
                    $"cycle {n}: rotation phase error {CircErr(delta, tRot)} > {tol}");
                Assert.True(w >= 0.0 && w < 65 * tSta,
                    $"cycle {n}: hold {w} outside the budget bound");
            }
        }

        [Fact]
        public void JointHold_DegenerateJointInputs_EqualSinglePeriodHold()
        {
            // NaN secondary period / zero budget / NaN base offset all degrade to the shipped
            // single-period per-loop hold byte-identically (the non-joint unit fence).
            const double w0 = 65.5, cadence = 12345.67, tSta = 100.0;
            for (long n = 0; n < 5; n++)
            {
                double single = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(w0, n, cadence, tSta);
                Assert.Equal(single, GhostPlaybackLogic.ComputePerLoopJointArrivalHoldSeconds(
                    w0, n, cadence, tSta, double.NaN, 5.0, 1234.5, 64), 12);
                Assert.Equal(single, GhostPlaybackLogic.ComputePerLoopJointArrivalHoldSeconds(
                    w0, n, cadence, tSta, 360.0, 5.0, 1234.5, 0), 12);
                Assert.Equal(single, GhostPlaybackLogic.ComputePerLoopJointArrivalHoldSeconds(
                    w0, n, cadence, tSta, 360.0, 5.0, double.NaN, 64), 12);
            }
        }

        [Fact]
        public void JointHold_ZeroW0_OffFence_Unchanged()
        {
            // The 13b regression fence carries over: w0 <= 0 (alignment Off / Drop) never turns
            // a zero hold nonzero, joint params present or not.
            Assert.Equal(0.0, GhostPlaybackLogic.ComputePerLoopJointArrivalHoldSeconds(
                0.0, 7, 12345.67, 100.0, 360.0, 5.0, 1234.5, 64));
        }

        [Fact]
        public void SpanClock_JointHold_HoldsAtBoundaryThroughWholePeriodExtension()
        {
            // Integration through TryComputeSpanLoopUT: a joint unit whose cycle-0 station-exact
            // base (45s) leaves the rotation misaligned extends by 17 whole station periods
            // (base 45 + offset 55 -> delta 100; (100 + 100i) % 360 == 0 first at i=17), so at a
            // probe 100s past the boundary the clock is still HELD at the boundary (loopUT ==
            // holdAtUT) while the single-period clock has already resumed 55s downstream.
            const double s0 = 0.0, s1 = 1000.0, holdAt = 800.0, tSta = 100.0, tRot = 360.0;
            const double anchor = 55.0;      // liveEntry_0 = 55 + 800 = 855; entryOffset0 = 55
            const double cad = 12345.0;
            const double w0 = 45.0;          // (800 - 855) mod 100
            double probe = anchor + 900.0;   // phase 900: 100s past the boundary at phase 800

            Assert.True(GhostPlaybackLogic.TryComputeSpanLoopUT(
                probe, anchor, s0, s1, cad, out double jointLoop, out _, out _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: w0, arrivalHoldAtUT: holdAt,
                arrivalHoldAlignPeriod: tSta,
                arrivalJointSecondaryPeriod: tRot, arrivalJointSecondaryTolerance: 5.0,
                arrivalJointMaxWholeHoldPeriods: 64));
            Assert.Equal(holdAt, jointLoop, Tol); // held at the boundary through the extension

            Assert.True(GhostPlaybackLogic.TryComputeSpanLoopUT(
                probe, anchor, s0, s1, cad, out double singleLoop, out _, out _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: w0, arrivalHoldAtUT: holdAt,
                arrivalHoldAlignPeriod: tSta));
            Assert.Equal(855.0, singleLoop, Tol); // 900 - 45: the single-period hold has resumed
        }
    }
}
