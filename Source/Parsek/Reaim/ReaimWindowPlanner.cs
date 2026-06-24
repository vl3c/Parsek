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
    //   - CadenceSeconds: the loop clock's relaunch cadence = the smallest synodic MULTIPLE >= span
    //     (ceil(span / synodic) * synodic). This makes the relaunch always land on a real transfer
    //     window (a synodic multiple of the recorded departure) AND keeps the recorded mission inside
    //     one cadence (=> a single instance, no self-overlap). For a normal mission (synodic dwarfs the
    //     span) this is exactly one synodic period, so the ghost replays its mission once, hides
    //     through the long inter-window gap, then relaunches at the next window. The departure clock
    //     steps by this same cadence, so the two clocks coincide at every window (the F2 override fires
    //     every relaunch).
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
            public double CadenceSeconds;      // relaunch cadence = smallest synodic multiple >= span
            public bool Prograde;              // Lambert short-way direction (prograde planets => true)

            internal static ReaimWindowSchedule Invalid_(string reason)
            {
                return new ReaimWindowSchedule { Valid = false, Reason = reason };
            }

            /// <summary>The absolute heliocentric-departure UT for window index <paramref name="k"/>
            /// (k &gt;= 0): D0 + k * cadence - the time the transfer geometry is solved for. Cadence is a
            /// whole synodic MULTIPLE (see <see cref="Plan"/>), so each window's departure is still
            /// congruent to the recorded one (the launch + target bodies return to the recorded relative
            /// configuration every synodic period, hence at every synodic multiple too). This now uses the
            /// SAME expression as <see cref="RelaunchUTForWindow"/>, so for the SAME k the two are
            /// bit-identical and the departure clock and the relaunch clock COINCIDE at EVERY window - the
            /// F2 park-end override (which fires only when they coincide) is therefore valid at every
            /// relaunch, not just window 0. For the normal case (synodic &gt; span) cadence == synodic, so
            /// this is byte-identical to the old D0 + k*synodic. Pure.</summary>
            internal double DepartureUTForWindow(long k)
            {
                return FirstDepartureUT + k * CadenceSeconds;
            }

            /// <summary>The absolute UT at which the loop ENGINE actually relaunches the ghost for window
            /// <paramref name="k"/> (k &gt;= 0): D0 + k * cadence. This is the CADENCE-clock replay time -
            /// the live instant the ghost replays its recorded departure (the span clock resolves
            /// loopUT = spanStart at currentUT = D0 + k*cadence). Because cadence is a whole synodic
            /// MULTIPLE, it COINCIDES with <see cref="DepartureUTForWindow"/> at EVERY window for every
            /// mission (both step by cadence now), including a recorded span longer than its own transfer
            /// window (span &gt; synodic), where they used to diverge by k*(span - synodic). The
            /// body-relative escape leg renders at the launch body's LIVE position (KSP
            /// Orbit.getPositionAtUT adds referenceBody.position), so a launch-body-co-orbital heliocentric
            /// PARK re-phased to THIS time sits next to the live launch body - and because the transfer is
            /// aimed at the SAME UT, it also reaches the target's live position. Pure.</summary>
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
            // Cadence = the smallest synodic MULTIPLE >= span, so every relaunch lands on a real
            // transfer window (a synodic multiple of the recorded departure) AND the recorded mission
            // fits inside one cadence (=> the loop is single-instance, never self-overlapping). The
            // - 1e-9 epsilon mirrors QuantizeCadenceToMultipleOfP: it stops a sub-ulp-above-synodic span
            // from spuriously doubling a NORMAL mission's cadence. For synodic > span (the normal case)
            // Ceiling(<1 - eps) == 1 => cadence == synodic, byte-identical to the old max(span, synodic).
            // For span > synodic (e.g. the s15 ratio ~1.185) Ceiling rounds up to 2 => cadence == 2*synodic.
            // The Max(1.0, ...) floors the multiple at 1 (mirroring QuantizeCadenceToMultipleOfP's k>=1
            // guard): a degenerate sub-epsilon span (Ceiling(tiny - 1e-9) == 0) cannot yield a 0 cadence.
            double cadence = (spanDuration > 0.0)
                ? System.Math.Max(1.0, System.Math.Ceiling(spanDuration / synodic - 1e-9)) * synodic
                : synodic;
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

        /// <summary>
        /// True when the F2 park-end-anchor BUNDLE is geometrically valid for a window: the cadence-clock
        /// relaunch time (<see cref="ReaimWindowSchedule.RelaunchUTForWindow"/>, where the body-relative
        /// escape leg and the LAN-rotated park sit, per PR #1172) and the transfer departure
        /// (<see cref="ReaimWindowSchedule.DepartureUTForWindow"/>, where the Lambert transfer is aimed)
        /// COINCIDE. With the synodic-multiple cadence both clocks step by the SAME cadence, so they are
        /// bit-identical for the same window k and this is true at EVERY window for EVERY mission (normal
        /// synodic&gt;span AND the former span&gt;synodic case): the F2 override therefore fires at every
        /// relaunch, not just window 0, and the transfer reaches the target's live position each window.
        /// (Kept as an explicit gate so a degenerate / future schedule whose clocks somehow disagree still
        /// fails closed to the tested Increment-1 launch-body-center path - a sane conic, not wild.)
        /// #1172's park LAN re-phase is independent of this gate and applies at every window. Pure (1.0 s
        /// tolerance, the same UT-equality epsilon used across the resolver).
        /// </summary>
        internal static bool ParkEndOverrideClocksCoincide(double parkReplayUT, double departureUT)
        {
            return Math.Abs(parkReplayUT - departureUT) < 1.0;
        }
    }
}
