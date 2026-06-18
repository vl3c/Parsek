using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Phase 3c of re-aim (docs/dev/plans/reaim-interplanetary-transfers.md): the pure synodic
    // relaunch planner (congruent-window, recorded-tof model). Validates the schedule fields against
    // stock Kerbin->Duna numbers and guards the degenerate-input fail-closed paths.
    public class ReaimWindowPlannerTests
    {
        private const double KerbinPeriod = 9203545.0, DunaPeriod = 17315400.0;

        // A recorded Kerbin->Duna mission: span [1000, 5000], departed (SOI exit) at 2000, recorded
        // transfer tof = 3000 s, so it arrived at 5000 = spanEnd.
        private const double SpanStart = 1000.0, SpanEnd = 5000.0;
        private const double RecordedDeparture = 2000.0, RecordedTof = 3000.0;

        private static ReaimWindowPlanner.ReaimWindowSchedule PlanKerbinDuna(double referenceUT)
        {
            return ReaimWindowPlanner.Plan(
                KerbinPeriod, DunaPeriod, RecordedDeparture, RecordedTof, SpanStart, SpanEnd, referenceUT);
        }

        [Fact]
        public void Plan_KerbinToDuna_ProducesSaneSynodicSchedule()
        {
            var s = PlanKerbinDuna(referenceUT: 100_000.0);
            Assert.True(s.Valid, s.Reason);

            // Synodic ~2.1 Kerbin years; cadence == synodic (it dwarfs the 4000 s span).
            double kerbinYears = s.SynodicPeriodSeconds / KerbinPeriod;
            Assert.InRange(kerbinYears, 2.0, 2.3);
            Assert.Equal(s.SynodicPeriodSeconds, s.CadenceSeconds, 3);

            // The schedule replays the RECORDED tof (not an idealized Hohmann time).
            Assert.Equal(RecordedTof, s.TofSeconds, 6);

            // First window is congruent to the recorded departure (RecordedDeparture + k*synodic) and
            // at/after the reference UT.
            Assert.True(s.FirstDepartureUT >= 100_000.0);
            double offset = s.FirstDepartureUT - RecordedDeparture;
            double remainder = offset - System.Math.Round(offset / s.SynodicPeriodSeconds) * s.SynodicPeriodSeconds;
            Assert.InRange(System.Math.Abs(remainder), 0.0, 1e-3); // a whole number of synodic periods
            Assert.True(s.Prograde);

            // Anchor maps to the recorded span start for window 0, and is far in the future (> spanEnd)
            // -> preserves the loop first-play floor invariant.
            Assert.Equal(s.FirstDepartureUT, s.PhaseAnchorUT + (RecordedDeparture - SpanStart), 3);
            Assert.True(s.PhaseAnchorUT > SpanEnd);
        }

        [Fact]
        public void DepartureUTForWindow_StepsBySynodicPeriod()
        {
            var s = PlanKerbinDuna(referenceUT: 100_000.0);
            Assert.True(s.Valid, s.Reason);
            Assert.Equal(s.FirstDepartureUT, s.DepartureUTForWindow(0), 3);
            Assert.Equal(s.FirstDepartureUT + s.SynodicPeriodSeconds, s.DepartureUTForWindow(1), 3);
            Assert.Equal(s.FirstDepartureUT + 5.0 * s.SynodicPeriodSeconds, s.DepartureUTForWindow(5), 3);
        }

        [Fact]
        public void RelaunchUTForWindow_StepsByCadence()
        {
            var s = PlanKerbinDuna(referenceUT: 100_000.0);
            Assert.True(s.Valid, s.Reason);
            Assert.Equal(s.FirstDepartureUT, s.RelaunchUTForWindow(0), 3);
            Assert.Equal(s.FirstDepartureUT + s.CadenceSeconds, s.RelaunchUTForWindow(1), 3);
            Assert.Equal(s.FirstDepartureUT + 5.0 * s.CadenceSeconds, s.RelaunchUTForWindow(5), 3);
        }

        [Fact]
        public void RelaunchUTForWindow_NormalCase_CoincidesWithSynodicDeparture()
        {
            // synodic > span => cadence == synodic, so the cadence-clock relaunch time and the synodic-clock
            // departure are identical at every window (the park re-phase is byte-identical to pre-fix).
            var s = PlanKerbinDuna(referenceUT: 100_000.0);
            Assert.True(s.Valid, s.Reason);
            Assert.Equal(s.SynodicPeriodSeconds, s.CadenceSeconds, 3);
            for (long k = 0; k <= 5; k++)
                Assert.Equal(s.DepartureUTForWindow(k), s.RelaunchUTForWindow(k), 3);
        }

        [Fact]
        public void RelaunchUTForWindow_SpanExceedsSynodic_TracksEngineRelaunchNotSynodicWindow()
        {
            // A mission whose recorded span (25e6 s) EXCEEDS the Kerbin->Duna synodic (~19.6e6 s): the loop
            // engine relaunches every CADENCE = span, but the synodic-clock departure steps by synodic. The
            // body-relative escape leg follows the live launch body at the cadence relaunch time, so the park
            // must re-phase to RelaunchUTForWindow (cadence), not DepartureUTForWindow (synodic). This is the
            // span>synodic case (e.g. Kerbal X #2) where the two clocks DIVERGE and the old code put the
            // loiter ~142 deg*window off live Kerbin.
            const double bigSpanStart = 0.0, bigSpanEnd = 25_000_000.0;
            const double bigDeparture = 2_000_000.0, bigTof = 3_000_000.0;
            var s = ReaimWindowPlanner.Plan(
                KerbinPeriod, DunaPeriod, bigDeparture, bigTof, bigSpanStart, bigSpanEnd, referenceUT: 0.0);
            Assert.True(s.Valid, s.Reason);

            double spanDuration = bigSpanEnd - bigSpanStart;
            Assert.Equal(spanDuration, s.CadenceSeconds, 3);        // cadence == span (span > synodic)
            Assert.True(s.CadenceSeconds > s.SynodicPeriodSeconds); // the divergent case

            // Window 0 coincides; later windows diverge by exactly window*(cadence - synodic).
            Assert.Equal(s.DepartureUTForWindow(0), s.RelaunchUTForWindow(0), 3);
            for (long k = 1; k <= 4; k++)
            {
                double diverge = k * (s.CadenceSeconds - s.SynodicPeriodSeconds);
                Assert.True(diverge > 0.0);
                Assert.Equal(s.DepartureUTForWindow(k) + diverge, s.RelaunchUTForWindow(k), 3);
            }
        }

        [Fact]
        public void ParkRephase_SpanExceedsSynodic_CadenceClockMovesParkOntoLiveLaunchBody()
        {
            // End-to-end on the consumer: the park re-phase angle (ComputeParkDeltaLonDegrees) fed the
            // CADENCE-clock relaunch UT differs from the old SYNODIC-clock departure by the launch body's
            // heliocentric advance across the span-vs-synodic gap = omega_Kerbin * (cadence - synodic) per
            // window (~142 deg/window for stock Kerbin->Duna). That advance is exactly how far the live
            // launch body moved past the synodic window, i.e. the loiter teleport the fix removes.
            const double bigSpanStart = 0.0, bigSpanEnd = 25_000_000.0;
            const double bigDeparture = 2_000_000.0, bigTof = 3_000_000.0;
            var s = ReaimWindowPlanner.Plan(
                KerbinPeriod, DunaPeriod, bigDeparture, bigTof, bigSpanStart, bigSpanEnd, referenceUT: 0.0);
            Assert.True(s.Valid, s.Reason);

            for (long k = 1; k <= 3; k++)
            {
                double cadenceAngle = ReaimSegmentAssembler.ComputeParkDeltaLonDegrees(
                    s.RelaunchUTForWindow(k), bigDeparture, KerbinPeriod);
                double synodicAngle = ReaimSegmentAssembler.ComputeParkDeltaLonDegrees(
                    s.DepartureUTForWindow(k), bigDeparture, KerbinPeriod);
                double expectedExtra = (360.0 / KerbinPeriod) * k * (s.CadenceSeconds - s.SynodicPeriodSeconds);
                Assert.True(expectedExtra > 0.0);
                Assert.Equal(expectedExtra, cadenceAngle - synodicAngle, 6);
            }
        }

        [Fact]
        public void Window0_DepartureAndRelaunchClocksCoincide_DivergeForKGreaterThanZero()
        {
            // The departure-seam fix closes the seam at WINDOW 0 (k=0), where the synodic-departure clock
            // (DepartureUTForWindow, what the transfer geometry is solved for) and the cadence-relaunch clock
            // (RelaunchUTForWindow, what the body-relative escape leg / park re-phase track) COINCIDE. This
            // documents the in-scope boundary: window 0 is clock-consistent (the transfer's r1 and the park's
            // re-phase share one UT), and the divergence for k >= 1 is the SEPARATE windows-1+ arrival
            // clock-drift (self-overlap Increment 2), explicitly out of scope for this fix.
            //
            // Use the span>synodic case (Kerbal X #2 shape) so cadence != synodic and the divergence is real.
            const double bigSpanStart = 0.0, bigSpanEnd = 25_000_000.0;
            const double bigDeparture = 2_000_000.0, bigTof = 3_000_000.0;
            var s = ReaimWindowPlanner.Plan(
                KerbinPeriod, DunaPeriod, bigDeparture, bigTof, bigSpanStart, bigSpanEnd, referenceUT: 0.0);
            Assert.True(s.Valid, s.Reason);
            Assert.True(s.CadenceSeconds > s.SynodicPeriodSeconds); // the divergent (span>synodic) case

            // Window 0: D0 == R0 == FirstDepartureUT (the seam-fix window; the two clocks coincide here).
            Assert.Equal(s.FirstDepartureUT, s.DepartureUTForWindow(0), 3);
            Assert.Equal(s.FirstDepartureUT, s.RelaunchUTForWindow(0), 3);
            Assert.Equal(s.DepartureUTForWindow(0), s.RelaunchUTForWindow(0), 3);

            // k >= 1: the clocks diverge by exactly k*(cadence - synodic) - the OUT-OF-SCOPE windows-1+
            // arrival clock-drift, not this fix's concern.
            for (long k = 1; k <= 4; k++)
            {
                double diverge = k * (s.CadenceSeconds - s.SynodicPeriodSeconds);
                Assert.True(diverge > 0.0);
                Assert.Equal(s.DepartureUTForWindow(k) + diverge, s.RelaunchUTForWindow(k), 3);
            }
        }

        [Fact]
        public void ParkEndOverrideClocksCoincide_CoincideTrue_DivergeFalse()
        {
            // The pure gate the resolver uses to decide whether the F2 park-end r1 override is geometrically
            // valid this window. The two clocks coincide (diff 0) => true; diverge by the stock span>synodic
            // gap (~3.6M s) => false. The 1.0 s tolerance is the resolver's UT-equality epsilon.
            Assert.True(ReaimWindowPlanner.ParkEndOverrideClocksCoincide(1_000_000.0, 1_000_000.0)); // diff 0
            Assert.True(ReaimWindowPlanner.ParkEndOverrideClocksCoincide(1_000_000.5, 1_000_000.0)); // diff 0.5 < 1
            Assert.False(ReaimWindowPlanner.ParkEndOverrideClocksCoincide(1_003_600_000.0, 1_000_000_000.0)); // diff 3.6M
            Assert.False(ReaimWindowPlanner.ParkEndOverrideClocksCoincide(1_000_002.0, 1_000_000.0)); // diff 2 > 1
            // Just over the epsilon is gated off; just under stays on.
            Assert.False(ReaimWindowPlanner.ParkEndOverrideClocksCoincide(1_000_001.5, 1_000_000.0));
            Assert.True(ReaimWindowPlanner.ParkEndOverrideClocksCoincide(1_000_000.9, 1_000_000.0));
        }

        [Fact]
        public void ParkEndOverrideClocksCoincide_Window0_TrueViaScheduleClocks()
        {
            // Window 0 always coincides (parkReplayUT == departureUT == D0), for BOTH the normal span<=synodic
            // schedule and the divergent span>synodic schedule - so the F2 override applies at window 0 in
            // every case (the seam-fix window).
            var normal = PlanKerbinDuna(referenceUT: 100_000.0);
            Assert.True(normal.Valid, normal.Reason);
            Assert.True(ReaimWindowPlanner.ParkEndOverrideClocksCoincide(
                normal.RelaunchUTForWindow(0), normal.DepartureUTForWindow(0)));

            const double bigSpanStart = 0.0, bigSpanEnd = 25_000_000.0;
            const double bigDeparture = 2_000_000.0, bigTof = 3_000_000.0;
            var big = ReaimWindowPlanner.Plan(
                KerbinPeriod, DunaPeriod, bigDeparture, bigTof, bigSpanStart, bigSpanEnd, referenceUT: 0.0);
            Assert.True(big.Valid, big.Reason);
            Assert.True(big.CadenceSeconds > big.SynodicPeriodSeconds); // divergent schedule
            Assert.True(ReaimWindowPlanner.ParkEndOverrideClocksCoincide(
                big.RelaunchUTForWindow(0), big.DepartureUTForWindow(0))); // still coincides at k=0
            // But k>=1 of the divergent schedule gates the override OFF (the bounded-fix fallback to Increment-1).
            Assert.False(ReaimWindowPlanner.ParkEndOverrideClocksCoincide(
                big.RelaunchUTForWindow(1), big.DepartureUTForWindow(1)));
        }

        [Fact]
        public void ParkEndOverrideClocksCoincide_SpanLessThanOrEqualSynodic_TrueAtEveryWindow()
        {
            // For a normal span<=synodic mission cadence == synodic, so RelaunchUTForWindow(k) ==
            // DepartureUTForWindow(k) at EVERY k => the gate is always true => F2 is UNAFFECTED for normal
            // interplanetary missions (the bounded fix only changes the span>synodic k>=1 case). This reuses
            // the existing 4000 s span (<< the ~19.6M s synodic) so cadence == synodic.
            var s = PlanKerbinDuna(referenceUT: 100_000.0);
            Assert.True(s.Valid, s.Reason);
            Assert.Equal(s.SynodicPeriodSeconds, s.CadenceSeconds, 3); // span <= synodic => cadence == synodic
            for (long k = 0; k <= 8; k++)
                Assert.True(ReaimWindowPlanner.ParkEndOverrideClocksCoincide(
                    s.RelaunchUTForWindow(k), s.DepartureUTForWindow(k)),
                    $"clocks must coincide at every window k={k} for a span<=synodic mission (F2 unaffected)");
        }

        [Fact]
        public void Plan_ReferenceBeforeRecordedDeparture_FirstWindowIsTheRecordedDeparture()
        {
            // A recording dated in the future (e.g. after a career rewind): the first window is the
            // recorded departure itself (no negative k).
            var s = PlanKerbinDuna(referenceUT: 0.0);
            Assert.True(s.Valid, s.Reason);
            Assert.Equal(RecordedDeparture, s.FirstDepartureUT, 3);
        }

        [Fact]
        public void Plan_DegenerateInputs_InvalidWithReason()
        {
            // Equal periods -> no synodic.
            var eq = ReaimWindowPlanner.Plan(KerbinPeriod, KerbinPeriod, RecordedDeparture, RecordedTof,
                SpanStart, SpanEnd, 100_000.0);
            Assert.False(eq.Valid);
            Assert.Contains("synodic", eq.Reason);
            // Non-positive tof.
            Assert.False(ReaimWindowPlanner.Plan(KerbinPeriod, DunaPeriod, RecordedDeparture, 0.0,
                SpanStart, SpanEnd, 100_000.0).Valid);
            // Non-positive period.
            Assert.False(ReaimWindowPlanner.Plan(0.0, DunaPeriod, RecordedDeparture, RecordedTof,
                SpanStart, SpanEnd, 100_000.0).Valid);
            // NaN UT.
            Assert.False(ReaimWindowPlanner.Plan(KerbinPeriod, DunaPeriod, double.NaN, RecordedTof,
                SpanStart, SpanEnd, 100_000.0).Valid);
        }

        [Fact]
        public void Plan_ZeroSpanDuration_CadenceIsSynodic()
        {
            var s = ReaimWindowPlanner.Plan(KerbinPeriod, DunaPeriod, RecordedDeparture, RecordedTof,
                SpanStart, SpanStart /* spanEnd == spanStart */, 100_000.0);
            Assert.True(s.Valid, s.Reason);
            Assert.Equal(s.SynodicPeriodSeconds, s.CadenceSeconds, 3);
        }

        // ------------------------------------------------------------------
        // PadAlignLaunch: launch-pad alignment for the cross-parent ascent->parking seam fix.
        // ------------------------------------------------------------------

        private const double KerbinSiderealDay = 21549.425; // stock Kerbin rotation period (s)

        // NaN floor = "no floor" for the snap tests that are not exercising the referenceUT guard.
        private const double NoFloor = double.NaN;
        private const double Synodic = 19_645_697.0;

        [Fact]
        public void PadAlignLaunch_SnapsLaunchToWholeSiderealDay()
        {
            // recordedLaunch (spanStart) = 1000; an arbitrary live anchor 0.6 days off a whole day.
            double recordedLaunch = 1000.0;
            double phaseAnchor = recordedLaunch + 4175.6 * KerbinSiderealDay; // 0.6-day misaligned
            var r = ReaimWindowPlanner.PadAlignLaunch(
                phaseAnchor, Synodic, phaseAnchor + 50_000.0, Synodic,
                recordedLaunch, KerbinSiderealDay, NoFloor);

            Assert.True(r.Applied);
            // (alignedLaunch - recordedLaunch) is now a whole number of sidereal days.
            double daysAfter = (r.PhaseAnchorUT - recordedLaunch) / KerbinSiderealDay;
            Assert.Equal(System.Math.Round(daysAfter), daysAfter, 6);
            // The nudge is at most half a sidereal day.
            Assert.True(System.Math.Abs(r.DeltaSeconds) <= KerbinSiderealDay / 2.0 + 1e-6);
        }

        [Fact]
        public void PadAlignLaunch_QuantizesCadenceAndSpacingToWholeDay()
        {
            var r = ReaimWindowPlanner.PadAlignLaunch(
                500_000.0, Synodic, 600_000.0, Synodic, 1000.0, KerbinSiderealDay, NoFloor);

            Assert.True(r.Applied);
            double cadenceDays = r.CadenceSeconds / KerbinSiderealDay;
            double synodicDays = r.SynodicPeriodSeconds / KerbinSiderealDay;
            Assert.Equal(System.Math.Round(cadenceDays), cadenceDays, 6);
            Assert.Equal(System.Math.Round(synodicDays), synodicDays, 6);
            // Quantized within half a day of the original synodic.
            Assert.True(System.Math.Abs(r.SynodicPeriodSeconds - Synodic) <= KerbinSiderealDay / 2.0 + 1e-6);
            // Cadence == synodic so the window-index <-> departure map stays 1:1.
            Assert.Equal(r.SynodicPeriodSeconds, r.CadenceSeconds, 6);
        }

        [Fact]
        public void PadAlignLaunch_DepartureMovesBySameDeltaAsLaunch()
        {
            double phaseAnchor = 500_000.0, firstDeparture = 600_000.0;
            var r = ReaimWindowPlanner.PadAlignLaunch(
                phaseAnchor, Synodic, firstDeparture, Synodic, 1000.0, KerbinSiderealDay, NoFloor);

            // The whole timeline shifts by one delta: launch and departure move together so the
            // window-index <-> launch mapping stays intact.
            Assert.Equal(r.PhaseAnchorUT - phaseAnchor, r.FirstDepartureUT - firstDeparture, 6);
            Assert.Equal(r.DeltaSeconds, r.PhaseAnchorUT - phaseAnchor, 6);
        }

        [Fact]
        public void PadAlignLaunch_NonRotatingBody_Identity()
        {
            var r = ReaimWindowPlanner.PadAlignLaunch(
                500_000.0, Synodic, 600_000.0, Synodic, 1000.0, 0.0, NoFloor);

            Assert.False(r.Applied);
            Assert.Equal(500_000.0, r.PhaseAnchorUT, 6);
            Assert.Equal(Synodic, r.CadenceSeconds, 6);
            Assert.Equal(600_000.0, r.FirstDepartureUT, 6);
        }

        [Fact]
        public void PadAlignLaunch_AlreadyAligned_ZeroDelta()
        {
            double recordedLaunch = 1000.0;
            double phaseAnchor = recordedLaunch + 4176.0 * KerbinSiderealDay; // exact whole-day offset
            var r = ReaimWindowPlanner.PadAlignLaunch(
                phaseAnchor, Synodic, phaseAnchor + 50_000.0, Synodic,
                recordedLaunch, KerbinSiderealDay, NoFloor);

            Assert.True(r.Applied);
            Assert.Equal(0.0, r.DeltaSeconds, 3);
            Assert.Equal(phaseAnchor, r.PhaseAnchorUT, 3);
        }

        [Fact]
        public void PadAlignLaunch_CadenceDiffersFromSynodic_NotApplied()
        {
            // Case B (span >= synodic): cadence = span != synodic. Pad-align must NOT apply, because the
            // resolver maps the window index off the cadence but the departure off the synodic spacing,
            // and quantizing them independently would diverge. Keep the unaligned schedule unchanged.
            double cadence = 25_000_000.0; // span-dominated cadence, != synodic
            var r = ReaimWindowPlanner.PadAlignLaunch(
                500_000.0, cadence, 600_000.0, Synodic, 1000.0, KerbinSiderealDay, NoFloor);

            Assert.False(r.Applied);
            Assert.Equal(500_000.0, r.PhaseAnchorUT, 6);
            Assert.Equal(cadence, r.CadenceSeconds, 6);
            Assert.Equal(Synodic, r.SynodicPeriodSeconds, 6);
        }

        [Fact]
        public void PadAlignLaunch_FloorGuard_SnapsUpAboveFloor()
        {
            // A live anchor 0.3 days ABOVE a whole-day offset would round DOWN, but that down-snap lands
            // below the floor, so the guard rounds UP one day instead and the result stays above it.
            double recordedLaunch = 1000.0;
            double phaseAnchor = recordedLaunch + 10.3 * KerbinSiderealDay;
            double floor = recordedLaunch + 10.1 * KerbinSiderealDay; // between the down-snap (10) and the anchor
            var r = ReaimWindowPlanner.PadAlignLaunch(
                phaseAnchor, Synodic, phaseAnchor + 50_000.0, Synodic,
                recordedLaunch, KerbinSiderealDay, floor);

            Assert.True(r.Applied);
            Assert.True(r.PhaseAnchorUT >= floor);
            // Snapped UP to whole day 11 (not the nearest, 10), so still a whole-day offset.
            double daysAfter = (r.PhaseAnchorUT - recordedLaunch) / KerbinSiderealDay;
            Assert.Equal(System.Math.Round(daysAfter), daysAfter, 6);
            Assert.Equal(11.0, daysAfter, 6);
        }

        [Fact]
        public void PadAlignLaunch_RetrogradeBody_AlignsToRotationMagnitude()
        {
            // A retrograde launch body carries a negative rotation period in some representations; the
            // ascent's inertial track still recurs every |rotationPeriod|, so align to the magnitude.
            double recordedLaunch = 1000.0;
            double phaseAnchor = recordedLaunch + 4175.6 * KerbinSiderealDay;
            var r = ReaimWindowPlanner.PadAlignLaunch(
                phaseAnchor, Synodic, phaseAnchor + 50_000.0, Synodic,
                recordedLaunch, -KerbinSiderealDay, NoFloor);

            Assert.True(r.Applied);
            double daysAfter = (r.PhaseAnchorUT - recordedLaunch) / KerbinSiderealDay;
            Assert.Equal(System.Math.Round(daysAfter), daysAfter, 6);
        }
    }
}
