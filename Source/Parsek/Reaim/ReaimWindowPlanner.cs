using System.Globalization;

namespace Parsek.Reaim
{
    // Plans the synodic relaunch schedule for a re-aimed interplanetary loop (docs/dev/plans/
    // reaim-interplanetary-transfers.md, Phase 3c). PURE: takes plain doubles (the live caller
    // reads the SMAs / mu / periods / current heliocentric phase through IBodyInfo and hands them
    // in), so the whole window-scheduling decision is unit-testable against textbook Kerbin->Duna
    // numbers.
    //
    // What it produces, and why those fields:
    //   - FirstDepartureUT (D0): the next real transfer window at/after the reference UT (the live
    //     phase drifting to the Hohmann phase-angle target). Subsequent windows are D0 + k*synodic
    //     because the synodic period IS the phase-angle recurrence period - so every window k stays
    //     phase-aligned and the per-window Lambert solve (aimed at the target's ACTUAL position at
    //     D_k) yields a sane transfer (verified in-game: the C2 canary found a sane elliptic +
    //     target ENCOUNTER at every phase-window across a synodic period).
    //   - CadenceSeconds: the loop clock's relaunch cadence = max(spanDuration, synodic). For an
    //     interplanetary transfer synodic (~2 Kerbin years) dwarfs the recorded span, so the ghost
    //     replays its mission once, hides through the long inter-window gap, then relaunches at the
    //     next window.
    //   - PhaseAnchorUT: the absolute UT that maps to the recorded span START for window 0, i.e.
    //     D0 - (recordedDepartureUT - spanStartUT). The span clock then resolves
    //     loopUT = spanStartUT + (currentUT - phaseAnchorUT) mod cadence, so at currentUT = D_k the
    //     ghost is exactly at its recorded departure phase. This is always far in the future (>>
    //     spanEndUT), preserving the MissionLoopUnitBuilder first-play floor invariant that a loop
    //     member's effUT never equals liveUT (see GhostMapPresence loop-shift note).
    //   - HohmannTofSeconds: the (constant across windows) transfer time fed to every per-window
    //     Lambert solve; depends only on the SMAs + parent mu, so the recorded span is fixed.
    internal static class ReaimWindowPlanner
    {
        /// <summary>The planned synodic relaunch schedule for a re-aim loop member. Invalid (with a
        /// reason) when the geometry is degenerate - the caller then leaves the mission on the
        /// faithful path.</summary>
        internal struct ReaimWindowSchedule
        {
            public bool Valid;
            public string Reason;             // why invalid (diagnostic), or null when Valid
            public double FirstDepartureUT;   // D0: absolute UT of window 0's heliocentric departure
            public double SynodicPeriodSeconds;
            public double HohmannTofSeconds;
            public double PhaseAnchorUT;       // absolute UT mapping to spanStartUT for window 0
            public double CadenceSeconds;      // loop-clock relaunch cadence = max(span, synodic)
            public bool Prograde;              // Lambert short-way direction (prograde planets => true)

            internal static ReaimWindowSchedule Invalid_(string reason)
            {
                return new ReaimWindowSchedule { Valid = false, Reason = reason };
            }

            /// <summary>The absolute heliocentric-departure UT for window index <paramref name="k"/>
            /// (k &gt;= 0): D0 + k * synodic. Pure.</summary>
            internal double DepartureUTForWindow(long k)
            {
                return FirstDepartureUT + k * SynodicPeriodSeconds;
            }
        }

        /// <summary>
        /// Builds the synodic relaunch schedule for a re-aim loop. <paramref name="currentPhaseDegrees"/>
        /// is the target's heliocentric longitude minus the launch body's at <paramref name="referenceUT"/>
        /// (how far the target currently LEADS the launch body), in [0,360). Pure. Returns Valid=false
        /// when any input is degenerate (non-positive SMA/period/mu, no relative drift, NaN), so the
        /// caller keeps the faithful path.
        /// </summary>
        internal static ReaimWindowSchedule Plan(
            double aOriginMeters, double aTargetMeters, double muParent,
            double originPeriodSeconds, double targetPeriodSeconds,
            double currentPhaseDegrees,
            double recordedDepartureUT, double spanStartUT, double spanEndUT,
            double referenceUT)
        {
            if (double.IsNaN(aOriginMeters) || double.IsNaN(aTargetMeters) || double.IsNaN(muParent)
                || aOriginMeters <= 0.0 || aTargetMeters <= 0.0 || muParent <= 0.0)
                return ReaimWindowSchedule.Invalid_("degenerate SMA/mu");
            if (originPeriodSeconds <= 0.0 || targetPeriodSeconds <= 0.0
                || double.IsNaN(originPeriodSeconds) || double.IsNaN(targetPeriodSeconds))
                return ReaimWindowSchedule.Invalid_("degenerate orbital periods");
            if (double.IsNaN(currentPhaseDegrees) || double.IsNaN(referenceUT)
                || double.IsNaN(recordedDepartureUT) || double.IsNaN(spanStartUT) || double.IsNaN(spanEndUT))
                return ReaimWindowSchedule.Invalid_("NaN phase/UT input");

            double synodic = TransferWindowMath.SynodicPeriodSeconds(originPeriodSeconds, targetPeriodSeconds);
            if (double.IsInfinity(synodic) || double.IsNaN(synodic) || synodic <= 0.0)
                return ReaimWindowSchedule.Invalid_("no synodic period (bodies never realign)");

            double phaseTarget = TransferWindowMath.HohmannPhaseAngleTargetDegrees(aOriginMeters, aTargetMeters);
            if (double.IsNaN(phaseTarget))
                return ReaimWindowSchedule.Invalid_("bad Hohmann phase-angle target");

            double tof = TransferWindowMath.HohmannTransferTimeSeconds(aOriginMeters, aTargetMeters, muParent);
            if (double.IsNaN(tof) || double.IsInfinity(tof) || tof <= 0.0)
                return ReaimWindowSchedule.Invalid_("bad Hohmann transfer time");

            double d0 = TransferWindowMath.NextDepartureUT(
                referenceUT, currentPhaseDegrees, phaseTarget, originPeriodSeconds, targetPeriodSeconds);
            if (double.IsNaN(d0) || double.IsInfinity(d0))
                return ReaimWindowSchedule.Invalid_("no next departure window");

            double spanDuration = spanEndUT - spanStartUT;
            if (spanDuration < 0.0 || double.IsNaN(spanDuration))
                spanDuration = 0.0;
            double cadence = synodic > spanDuration ? synodic : spanDuration;
            double phaseAnchor = d0 - (recordedDepartureUT - spanStartUT);

            return new ReaimWindowSchedule
            {
                Valid = true,
                Reason = null,
                FirstDepartureUT = d0,
                SynodicPeriodSeconds = synodic,
                HohmannTofSeconds = tof,
                PhaseAnchorUT = phaseAnchor,
                CadenceSeconds = cadence,
                Prograde = true // launch + target both orbit the parent prograde => short-way prograde transfer
            };
        }

        internal static string Describe(ReaimWindowSchedule s)
        {
            var ic = CultureInfo.InvariantCulture;
            if (!s.Valid)
                return $"invalid({s.Reason})";
            return $"D0={s.FirstDepartureUT.ToString("R", ic)} synodic={s.SynodicPeriodSeconds.ToString("R", ic)} " +
                   $"tof={s.HohmannTofSeconds.ToString("R", ic)} anchor={s.PhaseAnchorUT.ToString("R", ic)} " +
                   $"cadence={s.CadenceSeconds.ToString("R", ic)}";
        }
    }
}
