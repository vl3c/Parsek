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

        [Fact]
        public void PadAlignLaunch_SnapsLaunchToWholeSiderealDay()
        {
            // recordedLaunch (spanStart) = 1000; an arbitrary live anchor 0.6 days off a whole day.
            double recordedLaunch = 1000.0;
            double phaseAnchor = recordedLaunch + 4175.6 * KerbinSiderealDay; // 0.6-day misaligned
            var r = ReaimWindowPlanner.PadAlignLaunch(
                phaseAnchor, 19_645_697.0, phaseAnchor + 50_000.0, 19_645_697.0,
                recordedLaunch, KerbinSiderealDay);

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
                500_000.0, 19_645_697.0, 600_000.0, 19_645_697.0, 1000.0, KerbinSiderealDay);

            Assert.True(r.Applied);
            double cadenceDays = r.CadenceSeconds / KerbinSiderealDay;
            double synodicDays = r.SynodicPeriodSeconds / KerbinSiderealDay;
            Assert.Equal(System.Math.Round(cadenceDays), cadenceDays, 6);
            Assert.Equal(System.Math.Round(synodicDays), synodicDays, 6);
            // Quantized within half a day of the original synodic.
            Assert.True(System.Math.Abs(r.SynodicPeriodSeconds - 19_645_697.0) <= KerbinSiderealDay / 2.0 + 1e-6);
        }

        [Fact]
        public void PadAlignLaunch_DepartureMovesBySameDeltaAsLaunch()
        {
            double phaseAnchor = 500_000.0, firstDeparture = 600_000.0;
            var r = ReaimWindowPlanner.PadAlignLaunch(
                phaseAnchor, 19_645_697.0, firstDeparture, 19_645_697.0, 1000.0, KerbinSiderealDay);

            // The whole timeline shifts by one delta: launch and departure move together so the
            // window-index <-> launch mapping stays intact.
            Assert.Equal(r.PhaseAnchorUT - phaseAnchor, r.FirstDepartureUT - firstDeparture, 6);
            Assert.Equal(r.DeltaSeconds, r.PhaseAnchorUT - phaseAnchor, 6);
        }

        [Fact]
        public void PadAlignLaunch_NonRotatingBody_Identity()
        {
            var r = ReaimWindowPlanner.PadAlignLaunch(
                500_000.0, 19_645_697.0, 600_000.0, 19_645_697.0, 1000.0, 0.0);

            Assert.False(r.Applied);
            Assert.Equal(500_000.0, r.PhaseAnchorUT, 6);
            Assert.Equal(19_645_697.0, r.CadenceSeconds, 6);
            Assert.Equal(600_000.0, r.FirstDepartureUT, 6);
        }

        [Fact]
        public void PadAlignLaunch_AlreadyAligned_ZeroDelta()
        {
            double recordedLaunch = 1000.0;
            double phaseAnchor = recordedLaunch + 4176.0 * KerbinSiderealDay; // exact whole-day offset
            var r = ReaimWindowPlanner.PadAlignLaunch(
                phaseAnchor, 19_645_697.0, phaseAnchor + 50_000.0, 19_645_697.0,
                recordedLaunch, KerbinSiderealDay);

            Assert.True(r.Applied);
            Assert.Equal(0.0, r.DeltaSeconds, 3);
            Assert.Equal(phaseAnchor, r.PhaseAnchorUT, 3);
        }
    }
}
