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

            /// <summary>The watched cycle's launch has another row of the same vessel to follow.</summary>
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

            /// <summary>The same vessel's next row of the same launch carries on: the ended copy stays as
            /// an invisible camera target until the handoff lands on that row.</summary>
            HoldForNextRow,

            /// <summary>Nothing carries on (a landing, a trim, the end of the launch): the camera holds at
            /// the end for a moment, then picks up the mission's running launch.</summary>
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

        /// <summary>
        /// True when the watched vessel carries on in launch <paramref name="cycle"/> past the member the
        /// camera follows: another row of the same vessel (<see cref="IsSameWatchedVessel"/>) is drawn now
        /// or starts within <see cref="NextRowLookAheadSeconds"/>. Reads the same trimmed windows the
        /// handoff picks its target from, so a held copy always waits for a row the handoff can choose.
        /// Pure.
        /// </summary>
        internal static bool HasContinuingRow(
            in LoopUnit unit, IReadOnlyList<IPlaybackTrajectory> trajectories,
            int watchedIndex, long cycle, double currentUT)
        {
            if (cycle < 0 || trajectories == null || unit.MemberIndices == null
                || watchedIndex < 0 || watchedIndex >= trajectories.Count || trajectories[watchedIndex] == null)
                return false;
            IPlaybackTrajectory watched = trajectories[watchedIndex];
            double loopUT = UnitInstanceLoopUT(unit, cycle, currentUT);
            if (loopUT > unit.SpanEndUT + LoopTiming.BoundaryEpsilon)
                return false;
            foreach (int idx in unit.MemberIndices)
            {
                if (idx == watchedIndex || idx < 0 || idx >= trajectories.Count || trajectories[idx] == null)
                    continue;
                IPlaybackTrajectory member = trajectories[idx];
                double start = unit.MemberStartUT(idx, member.StartUT);
                double end = unit.MemberEndUT(idx, member.EndUT);
                if (end > start && start <= loopUT + NextRowLookAheadSeconds
                    && end >= loopUT - LoopTiming.BoundaryEpsilon
                    && IsSameWatchedVessel(
                        watched.RecordedVesselGuid, watched.VesselName,
                        member.RecordedVesselGuid, member.VesselName))
                    return true;
            }
            return false;
        }
    }
}
