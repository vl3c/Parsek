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
    }
}
