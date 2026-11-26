using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    // Loop copies that end at their own end in flight: what a copy does on the frame it ends, the watched
    // copy that stays as an invisible camera target until its vessel's next row is ready, and where the
    // watch picks the mission up again after an end.
    internal partial class GhostPlaybackEngine
    {
        /// <summary>The watch camera's cycle value while it holds at an end (an explosion, or a quiet end
        /// with nothing to follow).</summary>
        internal const long WatchHoldCycleIndex = -2;

        // A copy found this far past its end (game seconds) did not reach it on a drawn frame: a warp or
        // a time jump skipped it, so its last part events replay with no puffs or sounds.
        internal const double EndEventEffectsMaxOvershootSeconds = 2.0;

        // How many frames an ended watched copy waits for a next row that has already started: its copy
        // is built for the handoff, so one frame is normal and a copy not ready after these never is.
        internal const int MaxEndedCopyWaitFrames = 120;

        // This frame's per-recording flags (UpdatePlayback's), for what a member decides about another
        // row. Null outside the frame: no flag is then read.
        internal TrajectoryPlaybackFlags[] frameFlags;

        /// <summary>
        /// Runs the part events up to the copy's end once, after the copy was placed there: the last drawn
        /// frame stops a step short of the end, and nothing draws the copy again. No mesh, no events.
        /// </summary>
        private void ApplyEndPartEvents(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double endUT,
            double overshootSeconds, bool suppressVisualFx)
        {
            if (state?.ghost == null || state.endedCopyHeld)
                return;
            ApplyPartStateForFrame(index, traj, state, endUT,
                allowTransientEffects: !suppressVisualFx
                    && overshootSeconds <= EndEventEffectsMaxOvershootSeconds);
        }

        /// <summary>
        /// The watched copy ended and its vessel's next row carries on: the copy disappears at its end and
        /// stays there only as the camera's target, re-placed every frame, until the watch is handed over
        /// or the row turns out to have no copy to follow. The frame after the handover it is no longer
        /// watched and goes. False when the copy cannot be placed at its end (its anchor is gone): it then
        /// ends as any copy there.
        /// </summary>
        private bool TryHoldEndedWatchedCopy(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags, FrameContext ctx,
            GhostPlaybackState state, double phase, double duration, double playbackStartUT, double endUT,
            bool suppressVisualFx, GhostPlaybackLogic.WatchedCopyEnd watchedEnd)
        {
            GhostRenderTrace.EmitGuardSkip(traj, index, ctx.currentUT, "loop-copy-held-for-watch");
            if (state.ghost != null)
            {
                state.anchorRetiredThisFrame = false;
                PositionGhostAtLoopEndUT(index, traj, state, endUT);
                if (RelativeAnchorResolution.ShouldSkipPostPositionPipeline(state.anchorRetiredThisFrame))
                    return false;
                ApplyEndPartEvents(index, traj, state, endUT, phase - duration, suppressVisualFx);
                HideHeldCopy(index, state);
            }
            if (state.endedCopyHeld)
                return true;
            state.endedCopyHeld = true;
            WriteRecordedCopyEnd(
                index, traj, state, phase, playbackStartUT, endUT, state.recordedEndCopy, watchedEnd);
            ParsekLog.Info("CameraFollow",
                "unit watch: copy ended, holding its end for the next row: #"
                    + index.ToString(CultureInfo.InvariantCulture)
                    + " \"" + (traj.VesselName ?? "?") + "\" cycle="
                    + state.loopCycleIndex.ToString(CultureInfo.InvariantCulture)
                    + " endUT=" + endUT.ToString("F2", CultureInfo.InvariantCulture));
            return true;
        }

        // Every part hidden, every emitter and sound stopped, the root left active: an inactive camera
        // target makes the stock camera snap back to the active vessel. Once per mesh: a copy held before
        // its mesh was built hides it when it comes.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void HideHeldCopy(int index, GhostPlaybackState state)
        {
            if (!state.ghost.activeSelf)
                state.ghost.SetActive(true);
            state.deferVisibilityUntilPlaybackSync = false;
            if (ReferenceEquals(state.endedCopyHiddenMesh, state.ghost))
                return;
            state.endedCopyHiddenMesh = state.ghost;
            StopVisualFxForFrame(state, index);
            state.visualFxSuppressed = true;
            GhostPlaybackLogic.HideAllGhostParts(state);
        }

        private void LogRecordedCopyEnd(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double phase,
            double playbackStartUT, double endUT, in GhostPlaybackLogic.OverlapCopyEnds ends,
            GhostPlaybackLogic.WatchedCopyEnd watchedEnd)
        {
            if (!state.endedCopyHeld)
                WriteRecordedCopyEnd(
                    index, traj, state, phase, playbackStartUT, endUT, ends.AtOwnEnd && ends.Recorded, watchedEnd);
        }

        /// <summary>The one line of a copy that ended at its recorded or trimmed end (a fall's copy has
        /// its own lines). Rate-limited per index and watch outcome: a short loop ends a copy every few
        /// seconds.</summary>
        private static void WriteRecordedCopyEnd(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double phase,
            double playbackStartUT, double endUT, bool recorded, GhostPlaybackLogic.WatchedCopyEnd watchedEnd)
        {
            if (!recorded)
                return;
            ParsekLog.VerboseRateLimited("Engine",
                "loop-copy-ended-" + index.ToString(CultureInfo.InvariantCulture) + "-" + watchedEnd,
                "Loop copy ended at its own end: #" + index.ToString(CultureInfo.InvariantCulture)
                    + " \"" + (traj?.VesselName ?? "?") + "\" cycle="
                    + state.loopCycleIndex.ToString(CultureInfo.InvariantCulture)
                    + " loopUT=" + (playbackStartUT + phase).ToString("F2", CultureInfo.InvariantCulture)
                    + " endUT=" + endUT.ToString("F2", CultureInfo.InvariantCulture)
                    + " reason=" + GhostPlaybackLogic.DescribeOverlapCopyEnd(traj, endUT)
                    + " watch=" + watchedEnd);
        }

        /// <summary>
        /// The row the watch moves to when the watched row of launch
        /// <see cref="FrameContext.protectedLoopCycleIndex"/> is over: another row of the same vessel
        /// (<see cref="GhostPlaybackLogic.IsSameWatchedVessel"/>) whose copy of that launch the camera can be
        /// handed (<see cref="GhostPlaybackLogic.DecideNextRowCopy"/>), the latest one to start first.
        /// <paramref name="liveRow"/> is that row once it started, else -1. The return value is the best of
        /// the vessel's rows; only a followable one is worth waiting for.
        /// </summary>
        private GhostPlaybackLogic.NextRowCopy FindWatchedNextRow(
            in GhostPlaybackLogic.LoopUnit unit, IReadOnlyList<IPlaybackTrajectory> trajectories,
            FrameContext ctx, bool suppressGhosts, out int liveRow)
        {
            liveRow = -1;
            int watchedIndex = ctx.protectedIndex;
            long cycle = ctx.protectedLoopCycleIndex;
            GhostPlaybackLogic.NextRowCopy best = GhostPlaybackLogic.NextRowCopy.OutOfReach;
            if (cycle < 0 || trajectories == null || unit.MemberIndices == null
                || watchedIndex < 0 || watchedIndex >= trajectories.Count || trajectories[watchedIndex] == null
                || GhostPlaybackLogic.UnitInstanceLoopUT(unit, cycle, ctx.currentUT)
                    > unit.SpanEndUT + LoopTiming.BoundaryEpsilon)
                return best;
            IPlaybackTrajectory watched = trajectories[watchedIndex];
            double liveStart = double.NegativeInfinity;
            foreach (int idx in unit.MemberIndices)
            {
                if (idx == watchedIndex || idx < 0 || idx >= trajectories.Count || trajectories[idx] == null)
                    continue;
                IPlaybackTrajectory member = trajectories[idx];
                if (!GhostPlaybackLogic.IsSameWatchedVessel(
                        watched.RecordedVesselGuid, watched.VesselName,
                        member.RecordedVesselGuid, member.VesselName))
                    continue;
                GhostPlaybackLogic.NextRowCopy next = DecideWatchedLaunchRowCopy(
                    unit, idx, member, cycle, ctx, suppressGhosts);
                if (next > best)
                    best = next;
                double start = unit.MemberStartUT(idx, member.StartUT);
                if (next == GhostPlaybackLogic.NextRowCopy.Live && start > liveStart)
                {
                    liveRow = idx;
                    liveStart = start;
                }
            }
            if (WaitedTooLongFor(best, watchedIndex, cycle))
            {
                liveRow = -1;
                return GhostPlaybackLogic.NextRowCopy.WaitedTooLong;
            }
            return best;
        }

        /// <summary>
        /// Where the watched vessel of launch <see cref="FrameContext.protectedLoopCycleIndex"/> went at a
        /// dock, boarding or undock: the host's branch answer (<see cref="ResolveWatchedBranchContinuation"/>,
        /// the end-of-recording follow rules), asked over the members whose copy of that launch is within
        /// reach, or -1 (no host, no such branch, the launch is over or its watched row is not near its end).
        /// <paramref name="copy"/> is whether the camera can be handed that launch's copy of it
        /// (<see cref="GhostPlaybackLogic.DecideNextRowCopy"/>, bounded by the held copy's wait): a branch
        /// answer that cannot be followed ends the watch of the launch, it never falls to the same-guid
        /// row, which at an undock is the station's.
        /// </summary>
        private int FindWatchedBranchRow(
            in GhostPlaybackLogic.LoopUnit unit, IReadOnlyList<IPlaybackTrajectory> trajectories,
            FrameContext ctx, bool suppressGhosts, out GhostPlaybackLogic.NextRowCopy copy)
        {
            copy = GhostPlaybackLogic.NextRowCopy.OutOfReach;
            int watchedIndex = ctx.protectedIndex;
            long cycle = ctx.protectedLoopCycleIndex;
            int[] members = unit.MemberIndices;
            if (ResolveWatchedBranchContinuation == null || cycle < 0 || trajectories == null || members == null
                || watchedIndex < 0 || watchedIndex >= trajectories.Count || trajectories[watchedIndex] == null
                || System.Array.IndexOf(members, watchedIndex) < 0)
                return -1;
            double instanceUT = GhostPlaybackLogic.UnitInstanceLoopUT(unit, cycle, ctx.currentUT);
            if (instanceUT > unit.SpanEndUT + LoopTiming.BoundaryEpsilon
                || instanceUT < unit.MemberEndUT(watchedIndex, trajectories[watchedIndex].EndUT)
                    - GhostPlaybackLogic.NextRowLookAheadSeconds)
                return -1;

            var unitCopy = unit;
            int branchRow = ResolveWatchedBranchContinuation(watchedIndex, idx =>
                idx != watchedIndex && idx >= 0 && idx < trajectories.Count && trajectories[idx] != null
                && System.Array.IndexOf(members, idx) >= 0
                && DecideWatchedLaunchRowCopy(unitCopy, idx, trajectories[idx], cycle, ctx, suppressGhosts)
                    != GhostPlaybackLogic.NextRowCopy.OutOfReach);
            if (branchRow < 0 || branchRow == watchedIndex || branchRow >= trajectories.Count
                || trajectories[branchRow] == null || System.Array.IndexOf(members, branchRow) < 0)
                return -1;
            copy = DecideWatchedLaunchRowCopy(unit, branchRow, trajectories[branchRow], cycle, ctx, suppressGhosts);
            if (WaitedTooLongFor(copy, watchedIndex, cycle))
                copy = GhostPlaybackLogic.NextRowCopy.WaitedTooLong;
            return branchRow;
        }

        // What the engine does with launch <paramref name="cycle"/>'s copy of one row, read from where that
        // copy is now (a held copy is the camera's, never a target).
        private GhostPlaybackLogic.NextRowCopy DecideWatchedLaunchRowCopy(
            in GhostPlaybackLogic.LoopUnit unit, int index, IPlaybackTrajectory member, long cycle,
            FrameContext ctx, bool suppressGhosts)
        {
            bool hasCopy = TryGetCopyOfCycle(index, cycle, out GhostPlaybackState copy) && !copy.endedCopyHeld;
            bool inSlot = hasCopy && ghostStates.TryGetValue(index, out GhostPlaybackState slot)
                && ReferenceEquals(slot, copy);
            return GhostPlaybackLogic.DecideNextRowCopy(
                unit, index, member, cycle, ctx.currentUT, IsRowDrawn(index, member, ctx),
                suppressGhosts, ctx.warpRate, inSlot, hasCopy && !inSlot);
        }

        // A started row whose copy the watched launch's held end waited the whole bound for never comes.
        private bool WaitedTooLongFor(GhostPlaybackLogic.NextRowCopy next, int watchedIndex, long cycle)
        {
            return next == GhostPlaybackLogic.NextRowCopy.Live
                && TryGetCopyOfCycle(watchedIndex, cycle, out GhostPlaybackState held)
                && held.endedCopyHeld && held.endedCopyWaitFrames >= MaxEndedCopyWaitFrames;
        }

        // The guards that stop UpdatePlaybackEntry / UpdateUnitMemberPlayback before they draw the row.
        private bool IsRowDrawn(int index, IPlaybackTrajectory member, FrameContext ctx)
        {
            if (!HasRenderableGhostData(member)
                || (member.LoopAnchorVesselId != 0 && !loadedAnchorVessels.Contains(member.LoopAnchorVesselId)))
                return false;
            if (frameFlags == null || index >= frameFlags.Length)
                return true;
            TrajectoryPlaybackFlags f = frameFlags[index];
            return !f.skipGhost && !f.anchorReFlyUnstable
                && !(ctx.activeReFlyMarker != null && f.sessionSuppressed);
        }

        /// <summary>Counts the frames a held copy waits for a started row, and says why a watched copy that
        /// ended has none to follow. Called for the watched member, once a frame.</summary>
        private void TrackEndedCopyWait(
            int index, IPlaybackTrajectory traj, long cycle, GhostPlaybackLogic.NextRowCopy next, bool rowOver)
        {
            if (!TryGetCopyOfCycle(index, cycle, out GhostPlaybackState copy))
                return;
            if (copy.endedCopyHeld && next == GhostPlaybackLogic.NextRowCopy.Live)
                copy.endedCopyWaitFrames++;
            else if (!GhostPlaybackLogic.IsFollowable(next)
                && (copy.endedCopyHeld || (rowOver && next != GhostPlaybackLogic.NextRowCopy.OutOfReach)))
                ParsekLog.InfoRateLimited("CameraFollow",
                    "unit-watch-no-next-row-" + index.ToString(CultureInfo.InvariantCulture) + "-" + next,
                    "unit watch: no copy of the next row to follow: #"
                        + index.ToString(CultureInfo.InvariantCulture)
                        + " \"" + (traj.VesselName ?? "?") + "\" cycle="
                        + cycle.ToString(CultureInfo.InvariantCulture)
                        + " reason=" + next
                        + " waitedFrames=" + copy.endedCopyWaitFrames.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>True when the index is a member of a mission that overlaps itself.</summary>
        internal bool IsSelfOverlapUnitMember(int recordingIndex)
        {
            return currentLoopUnits.TryGetUnitForMember(recordingIndex, out GhostPlaybackLogic.LoopUnit unit)
                && GhostPlaybackLogic.UnitMemberOverlaps(unit);
        }

        /// <summary>
        /// The copy of one loop cycle on one index: the slot's, else an overlap copy (the boundary
        /// secondary is never a camera target). Held copies count: the camera is on them.
        /// </summary>
        internal bool TryGetCopyOfCycle(int index, long cycle, out GhostPlaybackState copy)
        {
            if (ghostStates.TryGetValue(index, out copy) && copy != null && copy.loopCycleIndex == cycle)
                return true;
            copy = null;
            if (!overlapGhosts.TryGetValue(index, out List<GhostPlaybackState> overlaps) || overlaps == null)
                return false;
            for (int k = 0; k < overlaps.Count; k++)
                if (overlaps[k] != null && !overlaps[k].isBoundaryOverlapSecondary
                    && overlaps[k].loopCycleIndex == cycle)
                {
                    copy = overlaps[k];
                    return true;
                }
            return false;
        }

        /// <summary>
        /// Where the watch picks a self-overlapping mission up after an end: the through-line root's newest
        /// copy, else the newest launch's live row (the watched vessel's first, as in the stage handoff),
        /// else any row with a copy in its slot, the newest launch first. Only copies that are drawn now;
        /// false when the mission has none, and the camera then keeps holding. Reads slots and schedules,
        /// changes nothing.
        /// </summary>
        internal bool TryResolveSelfOverlapRestart(
            int watchedIndex, IReadOnlyList<IPlaybackTrajectory> trajectories, double currentUT,
            out int targetIndex, out long targetCycle)
        {
            targetIndex = -1;
            targetCycle = -1;
            if (!currentLoopUnits.TryGetUnitForMember(watchedIndex, out GhostPlaybackLogic.LoopUnit unit)
                || !GhostPlaybackLogic.UnitMemberOverlaps(unit)
                || trajectories == null || unit.MemberIndices == null)
                return false;

            if (TryGetLiveSlotCycle(unit.OwnerIndex, out targetCycle))
            {
                targetIndex = unit.OwnerIndex;
                return true;
            }

            GhostPlaybackLogic.ComputeNewestMissionInstanceSpanLoopUT(
                unit.PhaseAnchorUT, unit.SpanStartUT, unit.SpanEndUT - unit.SpanStartUT,
                unit.OverlapCadenceSeconds, currentUT, out double newestLoopUT, out long newestCycle);
            IPlaybackTrajectory watched = watchedIndex >= 0 && watchedIndex < trajectories.Count
                ? trajectories[watchedIndex] : null;
            bool bestMatches = false;
            double bestStart = double.NegativeInfinity;
            int anyIndex = -1;
            long anyCycle = -1;
            double anyStart = double.NegativeInfinity;
            foreach (int idx in unit.MemberIndices)
            {
                if (idx < 0 || idx >= trajectories.Count || trajectories[idx] == null
                    || !TryGetLiveSlotCycle(idx, out long cycle))
                    continue;
                IPlaybackTrajectory member = trajectories[idx];
                double start = unit.MemberStartUT(idx, member.StartUT);
                double end = unit.MemberEndUT(idx, member.EndUT);
                if (cycle > anyCycle || (cycle == anyCycle && start > anyStart))
                {
                    anyIndex = idx;
                    anyCycle = cycle;
                    anyStart = start;
                }
                if (cycle != newestCycle
                    || !GhostPlaybackLogic.IsLoopUTInMemberWindow(newestLoopUT, start, end))
                    continue;
                bool matches = watched != null && GhostPlaybackLogic.IsSameWatchedVessel(
                    watched.RecordedVesselGuid, watched.VesselName,
                    member.RecordedVesselGuid, member.VesselName);
                if (targetIndex >= 0 && !GhostPlaybackLogic.IsBetterUnitCameraLiveMember(
                        matches, start, bestMatches, bestStart))
                    continue;
                targetIndex = idx;
                targetCycle = cycle;
                bestMatches = matches;
                bestStart = start;
            }
            if (targetIndex >= 0)
                return true;
            targetIndex = anyIndex;
            targetCycle = anyCycle;
            return anyIndex >= 0;
        }

        // A slot with a live loop copy (a held one has ended). An ended copy never stays in a slot.
        private bool TryGetLiveSlotCycle(int index, out long cycle)
        {
            cycle = -1;
            if (!ghostStates.TryGetValue(index, out GhostPlaybackState state) || state == null
                || state.loopCycleIndex < 0 || state.endedCopyHeld)
                return false;
            cycle = state.loopCycleIndex;
            return true;
        }
    }
}
