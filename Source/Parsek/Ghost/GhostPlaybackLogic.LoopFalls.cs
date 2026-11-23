using System;
using System.Collections.Generic;

namespace Parsek
{
    // Loop falls: a loop-unit member (or ride-along debris) whose predicted tail outlasts the unit's
    // recorded span. The span clock, the cadences and the first-play floor read the recorded span
    // (LoopUnit.SpanEndUT); the member's window keeps its predicted tail, so the fall is drawn past
    // the span end and never changes the loop's timing (owner ruling 2026-11-24).
    internal static partial class GhostPlaybackLogic
    {
        /// <summary>
        /// The cycle index of the FIRST RUN's fall kept beside the loop copies: the ordinary ghost of
        /// a member still falling when its unit goes live. It replays on the real clock, never on a
        /// loop clock. Distinct from -1 (no cycle) and from the watch camera's -2 (explosion hold), so
        /// neither a fresh state nor a hold is read as one.
        /// </summary>
        internal const long FirstRunFallCycleIndex = -3;

        /// <summary>
        /// True when a member's drawn window outlasts its unit's timing span, which only a predicted
        /// tail can do (<see cref="LoopUnit.SpanEndUT"/> is the latest RECORDED member end). False on
        /// every unit with no such member, whose handling is then unchanged. Pure.
        /// </summary>
        internal static bool MemberHasLoopFall(in LoopUnit unit, double memberEndUT)
        {
            return unit.HasLoopFalls && memberEndUT > unit.SpanEndUT + LoopTiming.BoundaryEpsilon;
        }

        /// <summary>
        /// True when ride-along debris carries a predicted tail past its parent unit's timing span.
        /// Recorded debris data past the span end does not count: it keeps the span-clock cut. Pure.
        /// </summary>
        internal static bool DebrisHasLoopFall(in LoopUnit unit, IPlaybackTrajectory debris)
        {
            return unit.SpanEndUT > unit.SpanStartUT
                && HasPredictedDataPast(debris, unit.SpanEndUT + LoopTiming.BoundaryEpsilon);
        }

        /// <summary>
        /// True when the trajectory holds predicted data (a predicted orbit segment, or a predicted
        /// descent section) ending after <paramref name="ut"/>. Pure.
        /// </summary>
        internal static bool HasPredictedDataPast(IPlaybackTrajectory traj, double ut)
        {
            if (traj == null)
                return false;
            List<OrbitSegment> segments = traj.OrbitSegments;
            if (segments != null)
                for (int i = segments.Count - 1; i >= 0; i--)
                    if (segments[i].isPredicted && segments[i].endUT > ut)
                        return true;
            List<TrackSection> sections = traj.TrackSections;
            if (sections != null)
                for (int i = sections.Count - 1; i >= 0; i--)
                    if (sections[i].source == TrackSectionSource.Predicted && sections[i].endUT > ut)
                        return true;
            return false;
        }

        /// <summary>
        /// True when the unit's cycles are all the same length and shape: launches at
        /// <c>PhaseAnchorUT + n * cadence</c> and recorded time 1:1 inside the span. A zero-drift
        /// schedule, loiter cuts, an arrival hold, the launch alignment and the descent trigger each
        /// break one of the two. Pure.
        /// </summary>
        internal static bool UnitHasUniformCycles(in LoopUnit unit)
        {
            return unit.RelaunchSchedule == null
                && (unit.LoiterCuts == null || unit.LoiterCuts.Count == 0)
                && !(unit.ArrivalHoldSeconds > 0.0)
                && !unit.LaunchHoldEngaged
                && !unit.HasDescentTrigger;
        }

        /// <summary>
        /// The launch-to-launch period the unit flies: the overlap cadence when the mission overlaps
        /// itself, else the span-clock cadence. Pure.
        /// </summary>
        internal static double UnitLaunchCadenceSeconds(in LoopUnit unit)
        {
            return Math.Max(
                UnitMemberOverlaps(unit) ? unit.OverlapCadenceSeconds : unit.CadenceSeconds,
                LoopTiming.MinCycleDuration);
        }

        /// <summary>
        /// True when a member of a unit that does NOT overlap itself is still drawn as overlapping
        /// copies, because its fall runs on past the span: one copy per launch, each to its own
        /// impact. Uniform units only (<see cref="UnitHasUniformCycles"/>). Pure.
        /// </summary>
        internal static bool MemberFallUsesOverlapSchedule(in LoopUnit unit, double memberEndUT)
        {
            return MemberHasLoopFall(unit, memberEndUT)
                && !UnitMemberOverlaps(unit)
                && UnitHasUniformCycles(unit);
        }

        /// <summary>
        /// The oldest cycle drawn while <paramref name="newestCycle"/> is the newest: at most
        /// <see cref="GhostPlayback.MaxOverlapGhostsPerRecording"/> copies of one recording. Older
        /// copies end quietly; a fall never raises the cadence. Pure.
        /// </summary>
        internal static long OldestDrawnFallCycle(long newestCycle)
        {
            return newestCycle - GhostPlayback.MaxOverlapGhostsPerRecording + 1;
        }

        /// <summary>
        /// The live UT at which cycle <paramref name="cycle"/> of the unit reaches the timing span's
        /// end, the instant every fall past it starts. Mirrors the span clock
        /// (<see cref="ComputeSpanLoopFrame"/>): the per-launch effective span of a schedule, the
        /// compressed span plus that cycle's arrival hold otherwise; the launch alignment borrows and
        /// repays inside the cycle, so it does not move this instant. False when the cycle has no
        /// launch. Pure apart from the arrival hold's rate-limited diagnostics.
        /// </summary>
        internal static bool TryComputeCycleSpanEndLiveUT(in LoopUnit unit, long cycle, out double liveUT)
        {
            liveUT = double.NaN;
            double span = unit.SpanEndUT - unit.SpanStartUT;
            if (span <= 0.0 || cycle < 0)
                return false;

            MissionRelaunchSchedule schedule = unit.RelaunchSchedule;
            if (schedule != null)
            {
                if (!schedule.TryGetLaunchUT(cycle, out double launchUT))
                    return false;
                double effSpan = span;
                if (schedule.TryGetLaunchTiming(cycle, out LaunchTimingEntry timing))
                {
                    effSpan = span - TotalCutLength(timing.Cuts) + timing.ExtensionSeconds;
                    if (effSpan <= 0.0)
                        effSpan = span;
                }
                liveUT = launchUT + effSpan;
                return true;
            }

            double cadence = UnitLaunchCadenceSeconds(unit);
            ResolveSpanLoopHoldsAndSlack(
                span, cadence, cycle, unit.PhaseAnchorUT, unit.SpanStartUT, unit.LoiterCuts,
                unit.ArrivalHoldSeconds, unit.ArrivalHoldAtUT, unit.ArrivalAlignPeriodSeconds,
                unit.ArrivalJointSecondaryPeriodSeconds, unit.ArrivalJointSecondaryToleranceSeconds,
                unit.ArrivalJointMaxWholeHoldPeriods, out double compressedSpan, out double hold);
            liveUT = unit.PhaseAnchorUT + cycle * cadence + compressedSpan + hold;
            return !double.IsNaN(liveUT) && !double.IsInfinity(liveUT);
        }

        /// <summary>
        /// Where cycle <paramref name="cycle"/>'s fall stands: the recorded UT past the span end that
        /// plays at <paramref name="currentUT"/> (1:1 from the instant that cycle reached the span
        /// end). False before that instant. Pure.
        /// </summary>
        internal static bool TryComputeFallLoopUT(
            in LoopUnit unit, long cycle, double currentUT, out double fallLoopUT)
        {
            fallLoopUT = double.NaN;
            if (!TryComputeCycleSpanEndLiveUT(unit, cycle, out double spanEndLiveUT)
                || currentUT < spanEndLiveUT)
                return false;
            fallLoopUT = unit.SpanEndUT + (currentUT - spanEndLiveUT);
            return true;
        }

        /// <summary>
        /// The single-copy reading of a fall (KSC, the Tracking Station, the map outside flight, and
        /// the flight engine's non-uniform units): in the wait between cycles, a member still falling
        /// is rendered at its fall UT instead of hidden, until its own end. The next relaunch takes
        /// the clock back, so a longer fall is cut there. Changes <paramref name="decision"/> only
        /// from <see cref="UnitMemberRenderDecision.HiddenInterCycleTail"/> to
        /// <see cref="UnitMemberRenderDecision.Render"/>. A descent-set member and the descent's
        /// transfer member are driven by the descent trigger and left alone. Pure.
        /// </summary>
        internal static bool TryExtendRenderIntoFall(
            in LoopUnit unit, int memberIndex, double currentUT,
            double memberStartUT, double memberEndUT, long unitCycle,
            ref UnitMemberRenderDecision decision, ref double spanLoopUT)
        {
            if (decision != UnitMemberRenderDecision.HiddenInterCycleTail
                || !MemberHasLoopFall(unit, memberEndUT)
                || IsDescentDrivenMember(unit, memberIndex)
                || !TryComputeFallLoopUT(unit, unitCycle, currentUT, out double fallLoopUT)
                || !IsLoopUTInMemberWindow(fallLoopUT, memberStartUT, memberEndUT))
                return false;
            spanLoopUT = fallLoopUT;
            decision = UnitMemberRenderDecision.Render;
            return true;
        }

        /// <summary>True for a member the descent trigger positions (its set, or the transfer member
        /// that carries the icon to it). Pure.</summary>
        internal static bool IsDescentDrivenMember(in LoopUnit unit, int memberIndex)
        {
            return unit.HasDescentTrigger
                && (unit.IsDescentMember(memberIndex) || memberIndex == unit.TransferMemberIndex);
        }

        /// <summary>
        /// How far into its window (seconds) cycle <paramref name="cycle"/>'s copy of one recording has
        /// played at <paramref name="currentUT"/>: from its launch on the uniform schedule every overlap
        /// loop uses, or, with <paramref name="fallUnit"/> (a unit whose cycles are not uniform), from
        /// the instant the cycle reached the span end; negative infinity before it. The first run's kept
        /// fall replays on the real clock. Pure.
        /// </summary>
        internal static double OverlapCopyPhaseSeconds(
            double currentUT, long cycle, double scheduleStartUT, double cycleDurationSeconds,
            double playbackStartUT, LoopUnit? fallUnit = null)
        {
            if (cycle == FirstRunFallCycleIndex)
                return currentUT - playbackStartUT;
            if (!fallUnit.HasValue)
                return currentUT - (scheduleStartUT + cycle * cycleDurationSeconds);
            return TryComputeFallLoopUT(fallUnit.Value, cycle, currentUT, out double fallLoopUT)
                ? fallLoopUT - playbackStartUT
                : double.NegativeInfinity;
        }

        /// <summary>
        /// True when the copy cap retires cycle <paramref name="cycle"/>: older than
        /// <paramref name="oldestDrawnCycle"/> (long.MinValue = no cap). The first run's fall precedes
        /// cycle 0, so it is the oldest copy of all. Pure.
        /// </summary>
        internal static bool IsFallCopyPastCap(long cycle, long oldestDrawnCycle)
        {
            return (cycle == FirstRunFallCycleIndex ? -1 : cycle) < oldestDrawnCycle;
        }
    }
}
