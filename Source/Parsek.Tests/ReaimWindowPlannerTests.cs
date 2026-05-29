using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Phase 3c of re-aim (docs/dev/plans/reaim-interplanetary-transfers.md): the pure synodic
    // relaunch planner. Validates the schedule fields against stock Kerbin->Duna numbers and guards
    // the degenerate-input fail-closed paths (so a bad geometry leaves the mission on the faithful
    // path rather than producing a garbage window).
    public class ReaimWindowPlannerTests
    {
        private const double KerbinSma = 13599840256.0, DunaSma = 20726155264.0, MuKerbol = 1.1723328e18;
        private const double KerbinPeriod = 9203545.0, DunaPeriod = 17315400.0;

        // A recorded Kerbin->Duna mission: span [1000, 5000], departed (SOI exit) at 2000.
        private const double SpanStart = 1000.0, SpanEnd = 5000.0, RecordedDeparture = 2000.0;
        private const double ReferenceUT = 100_000.0;

        private static ReaimWindowPlanner.ReaimWindowSchedule PlanKerbinDuna(double currentPhaseDeg)
        {
            return ReaimWindowPlanner.Plan(
                KerbinSma, DunaSma, MuKerbol, KerbinPeriod, DunaPeriod,
                currentPhaseDeg, RecordedDeparture, SpanStart, SpanEnd, ReferenceUT);
        }

        [Fact]
        public void Plan_KerbinToDuna_ProducesSaneSynodicSchedule()
        {
            var s = PlanKerbinDuna(currentPhaseDeg: 0.0);
            Assert.True(s.Valid, s.Reason);

            // Synodic ~2.1 Kerbin years; cadence == synodic (it dwarfs the 4000 s span).
            double kerbinYears = s.SynodicPeriodSeconds / KerbinPeriod;
            Assert.InRange(kerbinYears, 2.0, 2.3);
            Assert.Equal(s.SynodicPeriodSeconds, s.CadenceSeconds, 3);

            // Hohmann tof is positive and well under the synodic period.
            Assert.True(s.HohmannTofSeconds > 0.0);
            Assert.True(s.HohmannTofSeconds < s.SynodicPeriodSeconds);

            // First window is in the future (>= reference), prograde transfer.
            Assert.True(s.FirstDepartureUT >= ReferenceUT);
            Assert.True(s.Prograde);

            // Anchor maps to the recorded span start for window 0:
            // anchor + (recordedDeparture - spanStart) == D0.
            Assert.Equal(s.FirstDepartureUT, s.PhaseAnchorUT + (RecordedDeparture - SpanStart), 3);
            // Anchor is far in the future (> spanEnd) -> preserves the loop first-play floor invariant.
            Assert.True(s.PhaseAnchorUT > SpanEnd);
        }

        [Fact]
        public void DepartureUTForWindow_StepsBySynodicPeriod()
        {
            var s = PlanKerbinDuna(currentPhaseDeg: 0.0);
            Assert.True(s.Valid, s.Reason);
            Assert.Equal(s.FirstDepartureUT, s.DepartureUTForWindow(0), 3);
            Assert.Equal(s.FirstDepartureUT + s.SynodicPeriodSeconds, s.DepartureUTForWindow(1), 3);
            Assert.Equal(s.FirstDepartureUT + 5.0 * s.SynodicPeriodSeconds, s.DepartureUTForWindow(5), 3);
        }

        [Fact]
        public void Plan_PhaseAlreadyAtTarget_FirstWindowIsImmediate()
        {
            // When the current phase already equals the Hohmann target, D0 is ~the reference UT.
            double target = TransferWindowMath.HohmannPhaseAngleTargetDegrees(KerbinSma, DunaSma);
            var s = PlanKerbinDuna(currentPhaseDeg: target);
            Assert.True(s.Valid, s.Reason);
            Assert.InRange(s.FirstDepartureUT - ReferenceUT, 0.0, KerbinPeriod); // within a year, basically now
        }

        [Fact]
        public void Plan_DegenerateInputs_InvalidWithReason()
        {
            // Non-positive SMA / mu.
            Assert.False(ReaimWindowPlanner.Plan(0.0, DunaSma, MuKerbol, KerbinPeriod, DunaPeriod,
                0.0, RecordedDeparture, SpanStart, SpanEnd, ReferenceUT).Valid);
            Assert.False(ReaimWindowPlanner.Plan(KerbinSma, DunaSma, 0.0, KerbinPeriod, DunaPeriod,
                0.0, RecordedDeparture, SpanStart, SpanEnd, ReferenceUT).Valid);
            // Equal periods -> no synodic.
            var eq = ReaimWindowPlanner.Plan(KerbinSma, KerbinSma, MuKerbol, KerbinPeriod, KerbinPeriod,
                0.0, RecordedDeparture, SpanStart, SpanEnd, ReferenceUT);
            Assert.False(eq.Valid);
            Assert.Contains("synodic", eq.Reason);
            // NaN phase.
            Assert.False(ReaimWindowPlanner.Plan(KerbinSma, DunaSma, MuKerbol, KerbinPeriod, DunaPeriod,
                double.NaN, RecordedDeparture, SpanStart, SpanEnd, ReferenceUT).Valid);
        }

        [Fact]
        public void Plan_ZeroSpanDuration_CadenceIsSynodic()
        {
            // A degenerate (zero-length) span must still pick synodic as the cadence, never 0.
            var s = ReaimWindowPlanner.Plan(KerbinSma, DunaSma, MuKerbol, KerbinPeriod, DunaPeriod,
                0.0, RecordedDeparture, SpanStart, SpanStart /* spanEnd == spanStart */, ReferenceUT);
            Assert.True(s.Valid, s.Reason);
            Assert.Equal(s.SynodicPeriodSeconds, s.CadenceSeconds, 3);
        }
    }
}
