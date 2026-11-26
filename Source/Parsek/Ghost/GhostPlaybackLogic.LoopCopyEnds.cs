using System;
using System.Collections.Generic;

namespace Parsek
{
    // Where a copy of an overlapping loop ends. Every copy of a self-overlapping mission's member (and of
    // its ride-along debris) ends at its own window's end, on the flight mesh, the Space Center, the
    // flight map and the Tracking Station alike (owner ruling 2026-11-24).
    internal static partial class GhostPlaybackLogic
    {
        /// <summary>
        /// How close to the recording's end a window end must be to BE that end: a window taken from a
        /// mission interval may sit a physics step off the recording's own last UT.
        /// </summary>
        internal const double WindowReachesEndSeconds = 0.05;

        /// <summary>
        /// How long (game seconds) a watched copy that ended waits for the same vessel's next row of its
        /// own launch to start, before the watch counts the launch as over. Bridges the sample-sized gaps
        /// between rows, never a trimmed-out stretch.
        /// </summary>
        internal const double NextRowLookAheadSeconds = 3.0;

        /// <summary>How the copies of one overlap schedule end. The default value is the standalone
        /// overlap loop, whose launch period is always shorter than its recording.</summary>
        internal readonly struct OverlapCopyEnds
        {
            /// <summary>Each copy ends at <see cref="WindowEndUT"/>; a period longer than the window
            /// leaves a gap with no copy before the next launch.</summary>
            internal readonly bool AtOwnEnd;

            /// <summary>The end is recorded (or trimmed), not a predicted fall's. Wording only.</summary>
            internal readonly bool Recorded;

            /// <summary>The recorded UT the window ends at; read only with <see cref="AtOwnEnd"/>.</summary>
            internal readonly double WindowEndUT;

            /// <summary>The cycle the watch camera follows on this index, or negative. Only a member of a
            /// self-overlapping mission names one; every other path keeps its own camera events.</summary>
            internal readonly long WatchedCycle;

            /// <summary>The watched cycle's launch has another row of the same vessel to follow, with a copy
            /// the camera can be handed (<see cref="IsFollowable"/>).</summary>
            internal readonly bool WatchedRowContinues;

            internal OverlapCopyEnds(
                double windowEndUT, bool recorded, long watchedCycle = -1, bool watchedRowContinues = false)
            {
                AtOwnEnd = true;
                Recorded = recorded;
                WindowEndUT = windowEndUT;
                WatchedCycle = watchedCycle;
                WatchedRowContinues = watchedRowContinues;
            }

            /// <summary>True when the watch camera follows this cycle's copy of a self-overlapping
            /// mission's member.</summary>
            internal bool IsWatched(long cycle) => AtOwnEnd && cycle >= 0 && cycle == WatchedCycle;
        }

        /// <summary>What the watch camera does when the copy it follows ends at its own end.</summary>
        internal enum WatchedCopyEnd
        {
            /// <summary>The camera is not on this copy.</summary>
            NotWatched,

            /// <summary>The same vessel's next row of the same launch carries on, with a copy to follow or
            /// one about to start (<see cref="NextRowCopy"/>): the ended copy stays as an invisible camera
            /// target until the handoff lands on that row.</summary>
            HoldForNextRow,

            /// <summary>Nothing to follow carries on (a landing, a trim, the end of the launch, a next row
            /// with no copy of it): the camera holds at the end for a moment, then picks up the mission's
            /// running launch.</summary>
            HoldThenRestart,

            /// <summary>The copy explodes: the explosion hold, then the mission's running launch.</summary>
            HoldAtExplosion
        }

        /// <summary>The single decision of <see cref="WatchedCopyEnd"/>. An explosion always wins over a
        /// next row, so the camera never leaves a crash for another row. Pure.</summary>
        internal static WatchedCopyEnd DecideWatchedCopyEnd(in OverlapCopyEnds ends, long cycle, bool explodes)
        {
            if (!ends.IsWatched(cycle))
                return WatchedCopyEnd.NotWatched;
            if (explodes)
                return WatchedCopyEnd.HoldAtExplosion;
            return ends.WatchedRowContinues ? WatchedCopyEnd.HoldForNextRow : WatchedCopyEnd.HoldThenRestart;
        }

        /// <summary>True when a window ends where the recording itself ends. Pure.</summary>
        internal static bool WindowReachesRecordingEnd(IPlaybackTrajectory traj, double windowEndUT)
        {
            return traj != null && windowEndUT >= traj.EndUT - WindowReachesEndSeconds;
        }

        /// <summary>
        /// True when a copy that ends at <paramref name="windowEndUT"/> ends with the recording's
        /// explosion: the recording ended Destroyed and the window reaches that end. A member trimmed
        /// before it ends quietly. Pure.
        /// </summary>
        internal static bool ExplodesAtWindowEnd(IPlaybackTrajectory traj, double windowEndUT)
        {
            if (traj == null)
                return false;
            return ShouldTriggerExplosionAtPlaybackUT(
                traj, WindowReachesRecordingEnd(traj, windowEndUT) ? traj.EndUT : windowEndUT);
        }

        /// <summary>The UT an expiring copy is placed at: its own window's end, or a standalone loop's
        /// effective loop end. Pure.</summary>
        internal static double ResolveOverlapCopyEndUT(in OverlapCopyEnds ends, IPlaybackTrajectory traj)
        {
            return ends.AtOwnEnd ? ends.WindowEndUT : GhostPlaybackEngine.ResolveLoopPlaybackEndpointUT(traj);
        }

        /// <summary>True when a copy of this schedule explodes where it ends. A standalone loop keeps its
        /// exact loop end. Pure.</summary>
        internal static bool OverlapCopyExplodes(in OverlapCopyEnds ends, IPlaybackTrajectory traj)
        {
            return ends.AtOwnEnd
                ? ExplodesAtWindowEnd(traj, ends.WindowEndUT)
                : ShouldTriggerExplosionAtPlaybackUT(
                    traj, GhostPlaybackEngine.ResolveLoopPlaybackEndpointUT(traj));
        }

        /// <summary>Why a copy ended at its own end, for the log. Pure.</summary>
        internal static string DescribeOverlapCopyEnd(IPlaybackTrajectory traj, double windowEndUT)
        {
            if (ExplodesAtWindowEnd(traj, windowEndUT))
                return IsQuietDestroyedEnd(traj) ? "destroyed-quietly" : "destroyed";
            return WindowReachesRecordingEnd(traj, windowEndUT) ? "recorded-end" : "trimmed-end";
        }

        /// <summary>
        /// The spacing of a unit member's copies, for every reader of them (the flight engine, the Space
        /// Center, the watch clock, the map schedule). Pure.
        /// </summary>
        internal static double MemberOverlapCycleSeconds(
            in LoopUnit unit, double memberDuration, bool memberHasFall)
        {
            return Math.Max(
                ResolveOverlapCadence(UnitLaunchCadenceSeconds(unit), memberDuration, memberHasFall),
                LoopTiming.MinCycleDuration);
        }

        /// <summary>The recorded UT launch <paramref name="cycle"/> of a uniform unit has reached, past
        /// the span end included. Pure.</summary>
        internal static double UnitInstanceLoopUT(in LoopUnit unit, long cycle, double currentUT)
        {
            return unit.SpanStartUT
                + (currentUT - (unit.PhaseAnchorUT + cycle * UnitLaunchCadenceSeconds(unit)));
        }

        /// <summary>Whether the camera can be handed the watched launch's own copy of one row. The last
        /// two can; the others never will be in this launch, so nothing waits for them.</summary>
        internal enum NextRowCopy
        {
            /// <summary>The row is over for that launch, or starts later than the look-ahead.</summary>
            OutOfReach,

            /// <summary>The row draws no ghost: unticked, suppressed, no data, its anchor not loaded.</summary>
            NotDrawn,

            /// <summary>High warp hides the row now, and nothing says when that ends.</summary>
            WarpHidden,

            /// <summary>The copy cap drops that launch's copy.</summary>
            PastCopyCap,

            /// <summary>The row started without it (a warp went over its start): the engine builds only
            /// the newest launch's copy, so this one is never built.</summary>
            SkippedAtStart,

            /// <summary>Its ended copy waited long enough for a copy that does not become ready.</summary>
            WaitedTooLong,

            /// <summary>The row starts within the look-ahead.</summary>
            NotStarted,

            /// <summary>The copy exists, or is the newest one and is built this frame.</summary>
            Live
        }

        /// <summary>True when an ended watched copy may wait for this one. Pure.</summary>
        internal static bool IsFollowable(NextRowCopy copy)
        {
            return copy == NextRowCopy.NotStarted || copy == NextRowCopy.Live;
        }

        /// <summary>
        /// The single decision of <see cref="NextRowCopy"/> for one row of a self-overlapping mission: what
        /// the engine does with launch <paramref name="cycle"/>'s copy of it, in the engine's own terms (the
        /// trimmed window, the spacing the row is drawn with, only the newest launch spawned).
        /// <paramref name="inSlot"/> / <paramref name="inOverlapList"/> say where that copy is now, if
        /// anywhere. Pure.
        /// </summary>
        internal static NextRowCopy DecideNextRowCopy(
            in LoopUnit unit, int memberIndex, IPlaybackTrajectory member, long cycle, double currentUT,
            bool rowDrawn, bool suppressGhosts, float warpRate, bool inSlot, bool inOverlapList)
        {
            double start = unit.MemberStartUT(memberIndex, member.StartUT);
            double end = unit.MemberEndUT(memberIndex, member.EndUT);
            double duration = end - start;
            if (cycle < 0 || duration <= 0)
                return NextRowCopy.OutOfReach;
            bool hasFall = MemberHasLoopFall(unit, end);
            double spacing = MemberOverlapCycleSeconds(unit, duration, hasFall);
            double scheduleStartUT = ComputeMemberOverlapScheduleStartUT(unit.PhaseAnchorUT, unit.SpanStartUT, start);
            double phase = OverlapCopyPhaseSeconds(currentUT, cycle, scheduleStartUT, spacing, start);
            if (phase > duration || phase < -NextRowLookAheadSeconds)
                return NextRowCopy.OutOfReach;
            if (!rowDrawn)
                return NextRowCopy.NotDrawn;
            if (!TryComputeNewestOverlapPlaybackUT(
                    currentUT, UnitLaunchCadenceSeconds(unit), duration, start, scheduleStartUT,
                    out double newestUT, out long newest, hasFall))
            {
                newest = -1;
                newestUT = start;
            }
            if (suppressGhosts && ShouldSuppressGhostMeshAtWarp(warpRate, member, newestUT))
                return NextRowCopy.WarpHidden;
            if (cycle > newest)
                return NextRowCopy.NotStarted;
            if (hasFall && IsFallCopyPastCap(cycle, OldestDrawnFallCycle(newest)))
                return NextRowCopy.PastCopyCap;
            // At high warp a row keeps the newest copy alone.
            return cycle == newest || inSlot || (inOverlapList && !suppressGhosts)
                ? NextRowCopy.Live
                : NextRowCopy.SkippedAtStart;
        }
    }
}
