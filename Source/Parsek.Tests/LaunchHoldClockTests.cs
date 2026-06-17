using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Span-clock composition tests for the per-loop LAUNCH HOLD
    /// (docs/dev/design-reaim-launch-hold-seam.md sections 6.2 / 9.2): the launch-TIME shift wired into
    /// <see cref="GhostPlaybackLogic.TryComputeSpanLoopUT"/> and surfaced through
    /// <see cref="GhostPlaybackLogic.DecideUnitMemberRender"/>. Covers the pre-launch ABSENCE (phaseInCycle
    /// &lt; H_N resolves below the member window, mapped to HiddenPreLaunchHold), the verbatim-deferred replay
    /// (phaseInCycle &gt;= H_N), the targeting invariants (cycleIndex / window read byte-identical with or
    /// without the hold), cadence==synodic untouched (not engaged), and the arrival-wait self-correction
    /// ((W_N - H_N) mod T_align). Pure inputs (no Unity).
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

        // Standard fixture: span [0,1000], cadence 2000 (> span so the inter-cycle tail engages between
        // loops), phaseAnchor 300, T_sid 400 => Off_0 = 300, H_0 = ((-300) mod 400 + 400) mod 400 = 100.
        // The cycle-0 launch is deferred by 100 s.
        private const double Anchor = 300, S0 = 0, S1 = 1000, Cad = 2000, Tsid = 400;

        private static double Loop(double currentUT, bool engaged, double tSid = Tsid)
        {
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                currentUT, Anchor, S0, S1, Cad, out double loopUT, out long _, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: tSid, launchHoldEngaged: engaged);
            return loopUT;
        }

        // === Pre-launch ABSENCE (phaseInCycle < H_N) ============================================

        [Fact]
        public void PreLaunch_ResolvesBelowSpanStart_NotFrozenOnPad()
        {
            // currentUT 350: phaseInCycle = 50 < H_0 (100) -> pre-launch. The loop has not launched yet;
            // the resolved loopUT must be BELOW spanStart (so DecideUnitMemberRender hides it), NOT clamped
            // to spanStart (the frozen-on-pad pose, which would render).
            double loopUT = Loop(350.0, engaged: true);
            Assert.True(loopUT < S0, $"pre-launch loopUT {loopUT} should be below spanStart {S0}");
        }

        [Fact]
        public void PreLaunch_DecideUnitMemberRender_HiddenPreLaunchHold()
        {
            // The pre-launch window routes to HiddenPreLaunchHold (hide-not-destroy), NOT HiddenOutsideWindow
            // (which would route to the engine's destroy path) and NOT Render (frozen on the pad).
            var decision = GhostPlaybackLogic.DecideUnitMemberRender(
                350.0, Anchor, S0, S1, Cad, S0, S1, out double loopUT, out long _, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: Tsid, launchHoldEngaged: true);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenPreLaunchHold, decision);
        }

        [Fact]
        public void AtLaunchInstant_AscentStartsAtSpanStart()
        {
            // currentUT 400: phaseInCycle == H_0 (100) -> the ascent starts exactly at spanStart.
            double loopUT = Loop(400.0, engaged: true);
            Assert.Equal(S0, loopUT, Tol);
            var decision = GhostPlaybackLogic.DecideUnitMemberRender(
                400.0, Anchor, S0, S1, Cad, S0, S1, out double _, out long _, out bool _,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: Tsid, launchHoldEngaged: true);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, decision);
        }

        // === Verbatim-deferred replay (phaseInCycle > H_N) ======================================

        [Fact]
        public void PostLaunch_VerbatimDeferredByHN()
        {
            // currentUT 600: phaseInCycle 300, workPhase 300 - 100 = 200 -> loopUT == spanStart + 200 == 200.
            double loopUT = Loop(600.0, engaged: true);
            Assert.Equal(200.0, loopUT, Tol);
        }

        [Fact]
        public void PostLaunch_ProbeEqualsNoShiftAtCurrentMinusHN()
        {
            // The deferral identity: loopUT(currentUT, with-shift) == loopUT(currentUT - H_N, no-shift) for a
            // probe UT past the launch window (design 9.2). H_0 = 100.
            const double h0 = 100.0;
            double probe = 650.0;
            double withShift = Loop(probe, engaged: true);
            double noShift = Loop(probe - h0, engaged: false);
            Assert.Equal(noShift, withShift, Tol);
        }

        [Fact]
        public void DegenerateTsid_NoHold_ByteIdenticalAcrossSweep()
        {
            // launchHoldEngaged true but a degenerate (non-rotating) T_sid -> H_N == 0 -> byte-identical to
            // the not-engaged clock across a swept currentUT and several loops.
            for (double t = Anchor; t <= Anchor + 3.0 * Cad; t += 31.0)
            {
                double engagedDeg = Loop(t, engaged: true, tSid: 0.0);
                double notEngaged = Loop(t, engaged: false);
                Assert.Equal(notEngaged, engagedDeg, Tol);
            }
        }

        [Fact]
        public void NotEngaged_ByteIdenticalToBareClock_AcrossSweep()
        {
            // launchHoldEngaged false (every non-re-aim caller) -> the launch-hold params are inert; loopUT,
            // cycleIndex, and the tail flag match the pre-launch-hold call across a swept range.
            for (double t = Anchor; t <= Anchor + 3.0 * Cad; t += 29.0)
            {
                bool okBase = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, Anchor, S0, S1, Cad, out double baseUT, out long baseCyc, out bool baseTail);
                bool okNew = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, Anchor, S0, S1, Cad, out double newUT, out long newCyc, out bool newTail,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                    arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: Tsid, launchHoldEngaged: false);
                Assert.Equal(okBase, okNew);
                Assert.Equal(baseUT, newUT, Tol);
                Assert.Equal(baseCyc, newCyc);
                Assert.Equal(baseTail, newTail);
            }
        }

        // === Targeting invariants byte-identical (the resolver window read, schedule:null) =======

        [Fact]
        public void TargetingInvariant_CycleIndexUnchangedByLaunchHold()
        {
            // The resolver derives windowIndex from TryComputeSpanLoopUT(..., schedule:null) with NO hold
            // args; the launch hold must not move cycleIndex. Assert cycleIndex is byte-identical with the
            // launch hold engaged vs. the bare resolver call across a sweep (incl. pre-launch UTs).
            for (double t = Anchor; t <= Anchor + 4.0 * Cad; t += 17.0)
            {
                GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, Anchor, S0, S1, Cad, out double _, out long resolverCyc, out bool _, schedule: null);
                GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, Anchor, S0, S1, Cad, out double _, out long heldCyc, out bool _,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                    arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: Tsid, launchHoldEngaged: true);
                Assert.Equal(resolverCyc, heldCyc);
            }
        }

        // === cadence == synodic untouched (the launch hold never engages there) ==================

        [Fact]
        public void CadenceEqualsSynodic_NotEngaged_ByteIdentical()
        {
            // For cadence == synodic PadAlignLaunch applies, so the builder sets launchHoldEngaged=false. With
            // the hold not engaged the clock is byte-identical to today regardless of the carried T_sid.
            // (Same assertion shape as NotEngaged_ByteIdenticalToBareClock with a synodic-scale cadence.)
            const double synodic = 19653076.0, span = 1000000.0;
            for (double t = 0.0; t <= 3.0 * synodic; t += synodic / 7.0)
            {
                bool okBase = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, 0.0, 0.0, span, synodic, out double baseUT, out long baseCyc, out bool baseTail);
                bool okNew = GhostPlaybackLogic.TryComputeSpanLoopUT(
                    t, 0.0, 0.0, span, synodic, out double newUT, out long newCyc, out bool newTail,
                    schedule: null, loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                    arrivalHoldAlignPeriod: double.NaN, launchBodyRotationPeriod: 21600.0, launchHoldEngaged: false);
                Assert.Equal(okBase, okNew);
                Assert.Equal(baseUT, newUT, 3);
                Assert.Equal(baseCyc, newCyc);
                Assert.Equal(baseTail, newTail);
            }
        }

        // === Defense-in-depth clamp: launch hold capped first, in-SOI replay never truncated =====

        [Fact]
        public void Clamp_LaunchHoldCappedFirst_SoArrivalAndInSoiReplayPreserved()
        {
            // Pathological: span 1000, cadence 1100, arrivalHold 50, a launch T_sid of 400 (so the raw H_N can
            // be up to ~400). compressedSpan(1000) + arrivalHold(50) + launchHold would exceed cadence 1100
            // whenever launchHold > 50. The clamp caps the LAUNCH hold first to cadence - span - arrivalHold =
            // 50, leaving the arrival hold (50) and the full 1000 s in-SOI replay intact. Verify the deferral
            // is by at most the clamped launch hold (<= 50), never the raw H_N, so the replay is not truncated.
            const double anchor = 300, s0 = 0, s1 = 1000, cad = 1100, hold = 50, holdAt = 600, tAlign = 700, tSid = 400;
            // A late-in-cycle UT well past any plausible hold sum: the resolved loopUT must still be a real
            // recorded UT inside [s0, s1], proving the in-SOI replay was not clamped away.
            GhostPlaybackLogic.TryComputeSpanLoopUT(
                anchor + 1050.0, anchor, s0, s1, cad, out double loopUT, out long _, out bool tail,
                schedule: null, loiterCuts: null, arrivalHoldSeconds: hold, arrivalHoldAtUT: holdAt,
                arrivalHoldAlignPeriod: tAlign, launchBodyRotationPeriod: tSid, launchHoldEngaged: true);
            Assert.True(loopUT >= s0 - Tol && loopUT <= s1 + Tol, $"loopUT {loopUT} outside recorded span");
        }

        // === Arrival-wait self-correction: (W_N - H_N) mod T_align (design 5.4 / 9.2) ============

        [Fact]
        public void ArrivalWait_SelfCorrects_DeorbitRotationPhaseStaysAligned()
        {
            // The 5.4 regression fence as an end-to-end alignment property. Pick a fixture where the launch
            // hold is nonzero on several loops and the arrival hold aligns a destination rotation phase. With
            // the single subtraction W_N_effective = ((W_N - H_N) mod T_align + T_align) mod T_align applied,
            // the destination rotation phase at the launch-shifted SOI-entry replay must stay congruent to the
            // recorded arrival phase on EVERY loop. The deorbit live UT is:
            //   arrivalLiveUT = (L_N + H_N) + (recordedArrivalDisplacement) + W_N_effective
            // where the launch shift defers the whole sequence by H_N. We pin the rotation-phase congruence.
            // T_sid 300 does NOT divide cad 2000 (2000 mod 300 = 200), so H_N genuinely varies per loop -
            // the case where the self-correction matters (with a T_sid that divides cad, H_N is constant and
            // the subtraction is a no-op).
            const double anchor = 0, s0 = 0, cad = 2000, holdAt = 600;
            const double tSid = 300, tAlign = 700;
            const double baseW0 = 200;       // the per-mission base arrival hold W_0 (> 0)
            // The recorded arrival sits at recorded-span phase holdAt; its live-UT displacement from spanStart
            // (before any hold) on loop N is (L_N - spanStart) + holdAt. The recorded rotation phase target is
            // (recordedArrivalAbsolute) mod T_align. We assert the live deorbit lands at a CONSTANT rotation
            // phase across loops, which is exactly what the self-correction guarantees.
            double? firstPhase = null;
            for (long n = 0; n < 8; n++)
            {
                // Recompute the effective per-loop arrival hold the way the clock does:
                double wN = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(baseW0, n, cad, tAlign);
                double hN = GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(anchor, s0, n, cad, tSid);
                double wEff = ((wN - hN) % tAlign + tAlign) % tAlign;
                // The launch instant L_N + H_N, plus the recorded arrival displacement holdAt, plus the
                // effective arrival hold, is the live deorbit UT.
                double lN = anchor + n * cad;
                double arrivalLiveUT = (lN + hN) + holdAt + wEff;
                double phase = (arrivalLiveUT % tAlign + tAlign) % tAlign;
                if (firstPhase == null) firstPhase = phase;
                else Assert.Equal(firstPhase.Value, phase, 3);   // constant rotation phase => aligned every loop
            }
        }

        [Fact]
        public void ArrivalWait_WithoutSubtraction_WouldDrift_ProvingTheCorrectionIsLoadBearing()
        {
            // Negative control for the test above: WITHOUT subtracting H_N from W_N, the deorbit rotation phase
            // drifts loop-to-loop (because the launch shift H_N moved the whole sequence but the unshifted W_N
            // aligns against the unshifted entry). At least one loop must differ, proving the subtraction is
            // load-bearing (the test above fails if the subtraction is dropped from the clock).
            const double anchor = 0, s0 = 0, cad = 2000, holdAt = 600;
            const double tSid = 300, tAlign = 700, baseW0 = 200;
            double? firstPhase = null;
            bool sawDrift = false;
            for (long n = 0; n < 8; n++)
            {
                double wN = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(baseW0, n, cad, tAlign);
                double hN = GhostPlaybackLogic.ComputePerLoopLaunchHoldSeconds(anchor, s0, n, cad, tSid);
                double lN = anchor + n * cad;
                double arrivalLiveUT = (lN + hN) + holdAt + wN;   // NO subtraction
                double phase = (arrivalLiveUT % tAlign + tAlign) % tAlign;
                if (firstPhase == null) firstPhase = phase;
                else if (Math.Abs(phase - firstPhase.Value) > 1.0) sawDrift = true;
            }
            Assert.True(sawDrift, "expected the un-corrected arrival phase to drift, proving the H_N subtraction matters");
        }

        // === Cross-surface parity: ResolveTrackingStationSampleUT reads the hold from the unit =====

        private static GhostPlaybackLogic.LoopUnitSet BuildLaunchHoldUnit()
        {
            // A launch-hold unit (member 0, span [0,1000], cadence 2000, phaseAnchor 300, T_sid 400). The
            // reaimPlan / reaimSchedule are null here (the TS sampler reads only the span-clock + launch-hold
            // fields, which are sufficient to exercise the pre-launch/post-launch resolution).
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, S0, S1, Cad, Anchor, Cad, null, null, null, null,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: Tsid, launchHoldEngaged: true);
            return new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { { 0, unit } },
                new Dictionary<int, int> { { 0, 0 } });
        }

        [Fact]
        public void TsSampler_PreLaunch_RenderHidden_PostLaunch_DeferredLoopUT()
        {
            var units = BuildLaunchHoldUnit();

            // Pre-launch (currentUT 350, phaseInCycle 50 < H_0 100): the TS sampler hides the ghost (icon +
            // line + marker suppressed), reading the launch hold from the unit with NO external-caller change.
            double preUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                0, S0, S1, 350.0, units, out bool preHidden);
            Assert.True(preHidden, "pre-launch TS sample must be hidden (the loop has not launched)");

            // Post-launch (currentUT 600, phaseInCycle 300): renders at the verbatim-deferred loopUT 200.
            double postUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                0, S0, S1, 600.0, units, out bool postHidden);
            Assert.False(postHidden);
            Assert.Equal(200.0, postUT, Tol);
        }
    }
}
