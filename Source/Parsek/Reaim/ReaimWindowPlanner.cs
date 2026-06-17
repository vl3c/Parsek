using System;
using System.Globalization;

namespace Parsek.Reaim
{
    // Plans the synodic relaunch schedule for a re-aimed interplanetary loop (docs/dev/plans/
    // reaim-interplanetary-transfers.md, Phase 3c). PURE: takes plain doubles, so the whole
    // window-scheduling decision is unit-testable against textbook Kerbin->Duna numbers.
    //
    // MODEL (congruent-window, recorded tof). The recorded mission departed at RecordedDepartureUT
    // with the launch + target bodies in some relative configuration, and flew the transfer in
    // RecordedTransferTofSeconds. After exactly ONE synodic period the two bodies return to that SAME
    // relative configuration, so the re-aim windows are simply RecordedDepartureUT + k*synodic. At
    // each window the live caller re-solves Lambert for the target's ACTUAL position using the
    // RECORDED tof, producing a transfer CONGRUENT to the recorded one (identical shape, rotated in
    // inertial space to point where the target now is) that arrives at the recorded arrival UT. This
    // is both faithful ("replay my transfer, shifted forward by whole synodic periods") and span-
    // coherent: because the tof is the recorded tof, the assembled arrival lands at the recorded
    // arrival UT and fits the fixed recorded loop span exactly (no clipping). Congruent geometry =>
    // a sane transfer at every window (unlike an arbitrary departure, which usually has none).
    //
    // What it produces:
    //   - FirstDepartureUT (D0): the first window RecordedDepartureUT + k*synodic at/after the
    //     reference UT (the loop-enable / first-play floor).
    //   - SynodicPeriodSeconds: the window spacing (and the loop relaunch cadence basis).
    //   - TofSeconds: the recorded transfer time, fed to every per-window Lambert solve AND used to
    //     place the transfer segment - so the recorded span is preserved.
    //   - CadenceSeconds: the loop clock's relaunch cadence = max(spanDuration, synodic). Synodic
    //     (~2 Kerbin years) dwarfs the span, so the ghost replays its mission once, hides through the
    //     long inter-window gap, then relaunches at the next window.
    //   - PhaseAnchorUT: the absolute UT that maps to the recorded span START for window 0, i.e.
    //     D0 - (RecordedDepartureUT - spanStartUT). The span clock then resolves loopUT = spanStartUT
    //     + (currentUT - phaseAnchorUT) mod cadence, so at currentUT = D_k the ghost is exactly at its
    //     recorded departure phase. Always far in the future (>> spanEndUT), preserving the
    //     MissionLoopUnitBuilder first-play floor invariant.
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
            public double TofSeconds;          // the RECORDED transfer time (solve + placement)
            public double PhaseAnchorUT;       // absolute UT mapping to spanStartUT for window 0
            public double CadenceSeconds;      // loop-clock relaunch cadence = max(span, synodic)
            public bool Prograde;              // Lambert short-way direction (prograde planets => true)

            internal static ReaimWindowSchedule Invalid_(string reason)
            {
                return new ReaimWindowSchedule { Valid = false, Reason = reason };
            }

            /// <summary>The absolute heliocentric-departure UT for window index <paramref name="k"/>
            /// (k &gt;= 0): D0 + k * synodic. This is the SYNODIC-clock departure - the time the transfer
            /// geometry is solved for (the launch + target bodies return to the recorded relative
            /// configuration every synodic period, so a congruent transfer exists here). Pure.</summary>
            internal double DepartureUTForWindow(long k)
            {
                return FirstDepartureUT + k * SynodicPeriodSeconds;
            }

            /// <summary>The absolute UT at which the loop ENGINE actually relaunches the ghost for window
            /// <paramref name="k"/> (k &gt;= 0): D0 + k * cadence. This is the CADENCE-clock replay time -
            /// the live instant the ghost replays its recorded departure (the span clock resolves
            /// loopUT = spanStart at currentUT = D0 + k*cadence). It coincides with
            /// <see cref="DepartureUTForWindow"/> when cadence == synodic (the normal case, synodic &gt;
            /// span), and DIVERGES by k*(span - synodic) when the recorded span exceeds the synodic (a
            /// mission longer than its own transfer window). The body-relative escape leg renders at the
            /// launch body's LIVE position (KSP Orbit.getPositionAtUT adds referenceBody.position), so a
            /// launch-body-co-orbital heliocentric PARK must be re-phased to THIS time - not the synodic
            /// departure - to sit next to the live launch body. Pure.</summary>
            internal double RelaunchUTForWindow(long k)
            {
                return FirstDepartureUT + k * CadenceSeconds;
            }
        }

        /// <summary>
        /// Builds the synodic relaunch schedule for a re-aim loop from the two bodies' orbital periods
        /// (about their shared parent) and the recorded transfer. Pure. Returns Valid=false when any
        /// input is degenerate (non-positive period/tof, no relative drift, NaN), so the caller keeps
        /// the faithful path.
        /// </summary>
        internal static ReaimWindowSchedule Plan(
            double originPeriodSeconds, double targetPeriodSeconds,
            double recordedDepartureUT, double recordedTofSeconds,
            double spanStartUT, double spanEndUT,
            double referenceUT)
        {
            if (originPeriodSeconds <= 0.0 || targetPeriodSeconds <= 0.0
                || double.IsNaN(originPeriodSeconds) || double.IsNaN(targetPeriodSeconds))
                return ReaimWindowSchedule.Invalid_("degenerate orbital periods");
            if (double.IsNaN(recordedTofSeconds) || double.IsInfinity(recordedTofSeconds) || recordedTofSeconds <= 0.0)
                return ReaimWindowSchedule.Invalid_("degenerate recorded tof");
            if (double.IsNaN(recordedDepartureUT) || double.IsNaN(referenceUT)
                || double.IsNaN(spanStartUT) || double.IsNaN(spanEndUT))
                return ReaimWindowSchedule.Invalid_("NaN UT input");

            double synodic = TransferWindowMath.SynodicPeriodSeconds(originPeriodSeconds, targetPeriodSeconds);
            if (double.IsInfinity(synodic) || double.IsNaN(synodic) || synodic <= 0.0)
                return ReaimWindowSchedule.Invalid_("no synodic period (bodies never realign)");

            // Windows are congruent to the recorded departure: RecordedDepartureUT + k*synodic. Pick
            // the first at/after the reference UT (already floored to the first-play end by the caller).
            double d0 = recordedDepartureUT;
            if (referenceUT > recordedDepartureUT)
            {
                double k = Math.Ceiling((referenceUT - recordedDepartureUT) / synodic);
                if (k < 0.0)
                    k = 0.0;
                d0 = recordedDepartureUT + k * synodic;
            }
            if (d0 < referenceUT) // floating-point guard: never schedule before the reference
                d0 += synodic;

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
                TofSeconds = recordedTofSeconds,
                PhaseAnchorUT = phaseAnchor,
                CadenceSeconds = cadence,
                Prograde = true // launch + target both orbit the parent prograde => short-way prograde transfer
            };
        }

        /// <summary>Result of <see cref="PadAlignLaunch"/>: the launch-pad-aligned phase anchor, the
        /// quantized relaunch cadence, and the matching adjusted window departure spacing.</summary>
        internal struct PadAlignResult
        {
            public double PhaseAnchorUT;
            public double CadenceSeconds;
            public double FirstDepartureUT;
            public double SynodicPeriodSeconds;
            public double DeltaSeconds;   // how far the launch moved (diagnostic; |delta| <= half a day)
            public bool Applied;
        }

        /// <summary>
        /// Launch-pad alignment for a re-aim loop (the cross-parent fix for the recorded body-fixed ascent
        /// not connecting to the recorded inertial parking orbit). The body-fixed ascent replays from the
        /// pad, so its inertial track follows the launch body's rotation at the LIVE launch time. The
        /// recorded parking orbit + escape are replayed at their recorded INERTIAL orientation. They only
        /// connect when the launch body's rotation at the live launch matches the recorded launch, i.e.
        /// when (livePhaseAnchorUT - recordedLaunchUT) is a whole number of the body's sidereal days.
        ///
        /// Re-aim schedules the launch off the synodic window, NOT the pad rotation, so generally they are
        /// out of phase. This snaps the phase anchor to the nearest whole sidereal day (a sub-half-day
        /// nudge, negligible inside a multi-year transfer window: the per-window Lambert re-solves to the
        /// target's actual position regardless) so the ascent replays at the recorded rotation phase and
        /// feeds the recorded parking orbit/escape exactly. The relaunch cadence + window departure spacing
        /// are quantized to a whole sidereal day too, so EVERY relaunch stays pad-aligned (not just window
        /// 0). The departure offset is moved by the SAME delta as the launch, so the window index <-> launch
        /// mapping the resolver relies on stays intact.
        ///
        /// Pure (no Unity); <paramref name="launchBodyRotationPeriodSeconds"/> is the launch body's sidereal
        /// rotation period (CelestialBody.rotationPeriod via IBodyInfo). <paramref name="notBeforeUT"/> is
        /// the schedule floor (the loop-anchor / first-play UT Plan enforces): a down-snap that would cross
        /// it rounds up a day instead (pass NaN to skip). Returns Applied=false (identity, unaligned
        /// schedule) for a degenerate / non-rotating body OR when cadence != synodic (a mission longer than
        /// its window, where the window-index<->departure mapping would otherwise diverge).
        /// </summary>
        internal static PadAlignResult PadAlignLaunch(
            double phaseAnchorUT, double cadenceSeconds,
            double firstDepartureUT, double synodicPeriodSeconds,
            double spanStartUT, double launchBodyRotationPeriodSeconds,
            double notBeforeUT)
        {
            var r = new PadAlignResult
            {
                PhaseAnchorUT = phaseAnchorUT,
                CadenceSeconds = cadenceSeconds,
                FirstDepartureUT = firstDepartureUT,
                SynodicPeriodSeconds = synodicPeriodSeconds,
                DeltaSeconds = 0.0,
                Applied = false
            };

            // Sidereal day. Math.Abs so a retrograde launch body (negative rotationPeriod in some
            // representations) aligns to its rotation magnitude instead of silently no-op'ing - matching
            // the faithful phase-lock path, which also negates a retrograde period.
            double day = Math.Abs(launchBodyRotationPeriodSeconds);
            if (double.IsNaN(day) || double.IsInfinity(day) || day <= 0.0
                || double.IsNaN(phaseAnchorUT) || double.IsNaN(spanStartUT)
                || double.IsNaN(cadenceSeconds) || double.IsNaN(synodicPeriodSeconds))
                return r; // non-rotating / degenerate => keep the unaligned schedule

            // Window-index <-> departure consistency: the resolver derives the loop window index from the
            // CADENCE clock but reads the transfer departure from SynodicPeriodSeconds, so the two MUST
            // share one period. That holds only when cadence == synodic (the normal interplanetary case,
            // where synodic dwarfs the span so cadence = max(span, synodic) = synodic). If they differ
            // (span >= synodic, a mission longer than its own transfer window), pad-aligning would bake a
            // divergent quantization into the schedule, so skip it and keep the unaligned schedule - that
            // degenerate case then stays exactly as it was before pad-align (no NEW inconsistency).
            if (Math.Abs(cadenceSeconds - synodicPeriodSeconds) > 1.0)
                return r;

            // Snap the live launch to the nearest whole sidereal day from the recorded launch.
            double offset = phaseAnchorUT - spanStartUT;
            double snappedOffset = Math.Round(offset / day) * day;
            // Floor guard: never let the snap push the launch (and so the loop's first render) before
            // notBeforeUT (the first-play / loop-anchor floor that Plan enforces on the window). A
            // down-snap that would cross it rounds up one sidereal day instead.
            if (!double.IsNaN(notBeforeUT) && spanStartUT + snappedOffset < notBeforeUT)
                snappedOffset += day;
            double delta = snappedOffset - offset; // |delta| <= half a day (<= 1.5 days when floor-bumped)

            // Quantize the window spacing to a whole sidereal day so every relaunch stays aligned, and set
            // the cadence to the SAME value (guarded equal above) so the window-index<->departure map can
            // never diverge.
            double quantizedSynodic = Math.Round(synodicPeriodSeconds / day) * day;
            if (quantizedSynodic <= 0.0)
                quantizedSynodic = synodicPeriodSeconds; // guard: synodic shorter than a day (not a real re-aim case)

            r.PhaseAnchorUT = phaseAnchorUT + delta;
            r.FirstDepartureUT = firstDepartureUT + delta; // departure tracks the launch (same delta)
            r.SynodicPeriodSeconds = quantizedSynodic;
            r.CadenceSeconds = quantizedSynodic; // == cadence; kept identical so window-index<->departure stays 1:1
            r.DeltaSeconds = delta;
            r.Applied = true;
            return r;
        }

        internal static string Describe(ReaimWindowSchedule s)
        {
            var ic = CultureInfo.InvariantCulture;
            if (!s.Valid)
                return $"invalid({s.Reason})";
            return $"D0={s.FirstDepartureUT.ToString("R", ic)} synodic={s.SynodicPeriodSeconds.ToString("R", ic)} " +
                   $"tof={s.TofSeconds.ToString("R", ic)} anchor={s.PhaseAnchorUT.ToString("R", ic)} " +
                   $"cadence={s.CadenceSeconds.ToString("R", ic)}";
        }
    }
}
