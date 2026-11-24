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
        /// stays there only as the camera's target, re-placed every frame, until the watch is handed over.
        /// The frame after that it is no longer watched and goes. False when the copy cannot be placed at
        /// its end (its anchor is gone): it then ends as any copy there.
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
                {
                    state.endedCopyHeld = false;
                    return false;
                }
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
        // target makes the stock camera snap back to the active vessel.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void HideHeldCopy(int index, GhostPlaybackState state)
        {
            if (!state.ghost.activeSelf)
                state.ghost.SetActive(true);
            state.deferVisibilityUntilPlaybackSync = false;
            if (state.endedCopyHeld)
                return;
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
        /// its own lines).</summary>
        private static void WriteRecordedCopyEnd(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double phase,
            double playbackStartUT, double endUT, bool recorded, GhostPlaybackLogic.WatchedCopyEnd watchedEnd)
        {
            if (!recorded)
                return;
            ParsekLog.Verbose("Engine",
                "Loop copy ended at its own end: #" + index.ToString(CultureInfo.InvariantCulture)
                    + " \"" + (traj?.VesselName ?? "?") + "\" cycle="
                    + state.loopCycleIndex.ToString(CultureInfo.InvariantCulture)
                    + " loopUT=" + (playbackStartUT + phase).ToString("F2", CultureInfo.InvariantCulture)
                    + " endUT=" + endUT.ToString("F2", CultureInfo.InvariantCulture)
                    + " reason=" + GhostPlaybackLogic.DescribeOverlapCopyEnd(traj, endUT)
                    + " watch=" + watchedEnd);
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
