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
        public void RelaunchUTForWindow_SpanExceedsSynodic_CadenceIsSynodicMultiple_ClocksCoincide()
        {
            // A mission whose recorded span (25e6 s) EXCEEDS the Kerbin->Duna synodic (~19.6e6 s): the loop
            // engine relaunches every CADENCE, which is now the smallest WHOLE synodic multiple >= span. For
            // this shape span/synodic ~1.27, so Ceiling(1.27) = 2 => cadence == 2*synodic. The departure clock
            // steps by the SAME cadence, so the cadence-clock relaunch and the departure-clock transfer aim
            // COINCIDE at every window (the F2 override fires every relaunch, the transfer reaches live Duna).
            const double bigSpanStart = 0.0, bigSpanEnd = 25_000_000.0;
            const double bigDeparture = 2_000_000.0, bigTof = 3_000_000.0;
            var s = ReaimWindowPlanner.Plan(
                KerbinPeriod, DunaPeriod, bigDeparture, bigTof, bigSpanStart, bigSpanEnd, referenceUT: 0.0);
            Assert.True(s.Valid, s.Reason);

            double spanDuration = bigSpanEnd - bigSpanStart;
            // cadence == the smallest whole synodic multiple >= span, >= span, and an integer*synodic.
            double expectedCadence =
                System.Math.Ceiling(spanDuration / s.SynodicPeriodSeconds - 1e-9) * s.SynodicPeriodSeconds;
            Assert.Equal(expectedCadence, s.CadenceSeconds, 3);
            Assert.True(s.CadenceSeconds >= spanDuration);          // fits one cadence => single instance
            Assert.True(s.CadenceSeconds > s.SynodicPeriodSeconds); // span > synodic => a multiple > 1
            double multiple = s.CadenceSeconds / s.SynodicPeriodSeconds;
            Assert.Equal(System.Math.Round(multiple), multiple, 6); // an integer number of synodic periods
            Assert.Equal(2.0, System.Math.Round(multiple), 0);      // 2*synodic for this shape

            // The two clocks COINCIDE at EVERY window now (both step by cadence) - the load-bearing new
            // invariant: DepartureUTForWindow(k) == RelaunchUTForWindow(k) for all k.
            for (long k = 0; k <= 4; k++)
                Assert.Equal(s.DepartureUTForWindow(k), s.RelaunchUTForWindow(k), 3);
        }

        [Fact]
        public void ParkRephase_SpanExceedsSynodic_CadenceMultiple_ParkSitsOnLiveLaunchBody()
        {
            // #1172 NON-REGRESSION at cadence = 2*synodic. The park re-phase places the recorded Sun-inertial
            // park next to the LIVE launch body at the relaunch UT by rotating its LAN by
            //   parkDeltaLon(k) = omega_Kerbin * (parkReplayUT - RecordedDepartureUT),  parkReplayUT = D0+k*cadence.
            // With the synodic-MULTIPLE cadence the relaunch clock (where the park sits) and the departure clock
            // (where the transfer is aimed) are the SAME UT, so the park re-phase angle evaluated on EITHER clock
            // is identical at every window - the park sits next to live Kerbin AND the transfer departs from that
            // same point. This re-proves the #1172 placement holds at cadence=2*synodic (it does NOT just delete
            // the coverage); the old span-cadence divergence (extra omega*(cadence-synodic) per window) is gone.
            const double bigSpanStart = 0.0, bigSpanEnd = 25_000_000.0;
            const double bigDeparture = 2_000_000.0, bigTof = 3_000_000.0;
            var s = ReaimWindowPlanner.Plan(
                KerbinPeriod, DunaPeriod, bigDeparture, bigTof, bigSpanStart, bigSpanEnd, referenceUT: 0.0);
            Assert.True(s.Valid, s.Reason);
            Assert.True(s.CadenceSeconds > s.SynodicPeriodSeconds); // span > synodic => cadence == 2*synodic

            for (long k = 0; k <= 3; k++)
            {
                double parkReplayUT = s.RelaunchUTForWindow(k);     // where the LAN-rotated park sits
                double departureUT = s.DepartureUTForWindow(k);     // where the transfer is aimed
                // The clocks coincide => the park re-phase angle is the SAME on both, so the park-end and the
                // transfer-start are the same point (no teleport seam, no loiter ~142 deg off live Kerbin).
                Assert.Equal(parkReplayUT, departureUT, 3);
                double cadenceAngle = ReaimSegmentAssembler.ComputeParkDeltaLonDegrees(
                    parkReplayUT, bigDeparture, KerbinPeriod);
                double departureAngle = ReaimSegmentAssembler.ComputeParkDeltaLonDegrees(
                    departureUT, bigDeparture, KerbinPeriod);
                Assert.Equal(cadenceAngle, departureAngle, 6);

                // And the park sits on the LIVE launch body: the re-phase rotates by exactly the launch body's
                // heliocentric advance since the recorded departure, omega_Kerbin*(parkReplayUT - recDeparture),
                // so the recorded park (captured next to Kerbin at recDeparture) lands next to Kerbin again.
                double expected = (360.0 / KerbinPeriod) * (parkReplayUT - bigDeparture);
                Assert.Equal(expected, cadenceAngle, 6);
            }
        }

        [Fact]
        public void DepartureAndRelaunchClocksCoincide_AtEveryWindow_SpanExceedsSynodic()
        {
            // The synodic-multiple-cadence fix makes the departure clock (DepartureUTForWindow, what the
            // transfer geometry is solved for) and the relaunch clock (RelaunchUTForWindow, what the
            // body-relative escape leg / park re-phase track) COINCIDE at EVERY window - both step by the
            // same cadence. This is the load-bearing new invariant: with the clocks coincident the F2 park-end
            // override fires every window, so the transfer reaches the destination's LIVE position at every
            // relaunch, not only window 0. Use the span>synodic case (Kerbal X #2 shape) where the OLD code
            // diverged by k*(cadence - synodic) and only window 0 was correct.
            const double bigSpanStart = 0.0, bigSpanEnd = 25_000_000.0;
            const double bigDeparture = 2_000_000.0, bigTof = 3_000_000.0;
            var s = ReaimWindowPlanner.Plan(
                KerbinPeriod, DunaPeriod, bigDeparture, bigTof, bigSpanStart, bigSpanEnd, referenceUT: 0.0);
            Assert.True(s.Valid, s.Reason);
            Assert.True(s.CadenceSeconds > s.SynodicPeriodSeconds); // span > synodic => cadence == 2*synodic

            // Window 0: D0 == R0 == FirstDepartureUT.
            Assert.Equal(s.FirstDepartureUT, s.DepartureUTForWindow(0), 3);
            Assert.Equal(s.FirstDepartureUT, s.RelaunchUTForWindow(0), 3);

            // EVERY window now coincides (diff 0) - no more k*(cadence - synodic) drift.
            for (long k = 0; k <= 4; k++)
                Assert.Equal(s.DepartureUTForWindow(k), s.RelaunchUTForWindow(k), 3);
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
        public void ParkEndOverrideClocksCoincide_AtEveryWindow_BothScheduleShapes()
        {
            // The F2 override gate is now true at EVERY window for BOTH schedule shapes: the normal
            // span<=synodic schedule (cadence == synodic) AND the span>synodic schedule (cadence ==
            // 2*synodic), because the departure clock and the relaunch clock both step by cadence and so
            // coincide. So the F2 override fires at every relaunch of either shape (the every-window fix).
            var normal = PlanKerbinDuna(referenceUT: 100_000.0);
            Assert.True(normal.Valid, normal.Reason);
            Assert.Equal(normal.SynodicPeriodSeconds, normal.CadenceSeconds, 3); // span <= synodic
            for (long k = 0; k <= 8; k++)
                Assert.True(ReaimWindowPlanner.ParkEndOverrideClocksCoincide(
                    normal.RelaunchUTForWindow(k), normal.DepartureUTForWindow(k)),
                    $"normal mission: clocks must coincide at window k={k}");

            const double bigSpanStart = 0.0, bigSpanEnd = 25_000_000.0;
            const double bigDeparture = 2_000_000.0, bigTof = 3_000_000.0;
            var big = ReaimWindowPlanner.Plan(
                KerbinPeriod, DunaPeriod, bigDeparture, bigTof, bigSpanStart, bigSpanEnd, referenceUT: 0.0);
            Assert.True(big.Valid, big.Reason);
            Assert.True(big.CadenceSeconds > big.SynodicPeriodSeconds); // span > synodic => cadence == 2*synodic
            for (long k = 0; k <= 8; k++)
                Assert.True(ReaimWindowPlanner.ParkEndOverrideClocksCoincide(
                    big.RelaunchUTForWindow(k), big.DepartureUTForWindow(k)),
                    $"span>synodic mission: clocks must now coincide at window k={k} (every-window fix)");
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

        [Fact]
        public void Plan_SubEpsilonPositiveSpan_CadenceFlooredToSynodic_NotZero()
        {
            // Defensive floor guard: a degenerate POSITIVE span small enough that
            // Ceiling(span/synodic - 1e-9) == 0 (here ~1 ms, far below any real interplanetary
            // recording) must NOT produce a 0 cadence (which would collapse the loop clock). The
            // Math.Max(1.0, ...) floors the multiple at one synodic period, mirroring
            // QuantizeCadenceToMultipleOfP's k>=1 guard.
            const double tinySpanStart = 0.0, tinySpanEnd = 0.001; // 1 ms span
            var s = ReaimWindowPlanner.Plan(KerbinPeriod, DunaPeriod, RecordedDeparture, RecordedTof,
                tinySpanStart, tinySpanEnd, referenceUT: 0.0);
            Assert.True(s.Valid, s.Reason);
            Assert.Equal(s.SynodicPeriodSeconds, s.CadenceSeconds, 3); // floored to 1*synodic, not 0
            Assert.True(s.CadenceSeconds > 0.0);
        }

        [Fact]
        public void Plan_S15Shape_CadenceIsTwoSynodic_ClocksCoincideEveryWindow()
        {
            // The s15 failing shape: a recorded span LONGER than the Kerbin->Duna transfer window. With
            // span/synodic ~= 1.185 the cadence rounds UP to the next whole synodic multiple = 2*synodic,
            // so each relaunch lands on a real transfer window AND the mission fits one cadence (single
            // instance). The departure clock and the relaunch clock then COINCIDE at every window, so the
            // F2 park-end override fires every relaunch and the transfer reaches Duna's live position.
            // Build the span as 1.185 * the planner-derived synodic so the shape matches regardless of the
            // exact synodic value (s15: span 23.285e6, synodic 19.646e6).
            double synodic = TransferWindowMath.SynodicPeriodSeconds(KerbinPeriod, DunaPeriod);
            double span = 1.185 * synodic; // ~23.285e6 for the stock synodic ~19.646e6
            var s = ReaimWindowPlanner.Plan(
                KerbinPeriod, DunaPeriod, RecordedDeparture, RecordedTof,
                0.0, span, referenceUT: 0.0);
            Assert.True(s.Valid, s.Reason);

            Assert.Equal(synodic, s.SynodicPeriodSeconds, 3);
            Assert.Equal(2.0 * synodic, s.CadenceSeconds, 3); // Ceiling(1.185) = 2 => 2*synodic
            Assert.True(s.CadenceSeconds >= span);            // mission fits one cadence
            double multiple = s.CadenceSeconds / s.SynodicPeriodSeconds;
            Assert.Equal(System.Math.Round(multiple), multiple, 6); // a whole synodic multiple

            // Clocks coincide at every window (k = 0..3) => the F2 override fires each window.
            for (long k = 0; k <= 3; k++)
                Assert.Equal(s.DepartureUTForWindow(k), s.RelaunchUTForWindow(k), 3);
        }

        [Fact]
        public void Plan_NormalCase_CadenceIsSynodic_DepartureByteIdenticalToSynodicStepping()
        {
            // The normal interplanetary case (synodic > span): cadence == 1*synodic (Ceiling(<1 - eps) = 1),
            // so the departure clock steps by exactly synodic - byte-identical to the pre-fix
            // FirstDepartureUT + k*synodic. Direct / normal missions are unaffected by the fix.
            var s = PlanKerbinDuna(referenceUT: 100_000.0);
            Assert.True(s.Valid, s.Reason);
            Assert.Equal(s.SynodicPeriodSeconds, s.CadenceSeconds, 3); // cadence == synodic
            for (long k = 0; k <= 5; k++)
            {
                // Old contract: FirstDepartureUT + k*synodic. New code returns FirstDepartureUT + k*cadence,
                // and cadence == synodic, so the two are bit-identical for the normal case.
                Assert.Equal(s.FirstDepartureUT + k * s.SynodicPeriodSeconds, s.DepartureUTForWindow(k), 3);
                Assert.Equal(s.DepartureUTForWindow(k), s.RelaunchUTForWindow(k), 3);
            }
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
