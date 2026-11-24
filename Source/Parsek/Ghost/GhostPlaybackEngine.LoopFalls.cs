using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Loop falls in flight: a unit member or ride-along debris whose predicted tail outlasts the
    // unit's recorded span plays to its own end beside the next launches, without changing their
    // cadence (owner ruling 2026-11-24).
    internal partial class GhostPlaybackEngine
    {
        /// <summary>
        /// Handover from the first run while it is still falling: the slot's ORDINARY ghost (no loop
        /// path created it) keeps falling on the real clock as an overlap copy of
        /// <see cref="GhostPlaybackLogic.FirstRunFallCycleIndex"/>, so the loop's first copy never cuts
        /// it. False, and nothing changes, when the slot holds a loop ghost, no ghost, or one past its
        /// recording's end. Muted like every overlap copy.
        /// </summary>
        internal bool TryKeepFirstRunFall(
            int i, IPlaybackTrajectory traj, FrameContext ctx, double anchorUT,
            ref GhostPlaybackState state, ref bool ghostActive)
        {
            if (!ShouldRetireOrdinaryStateAtUnitHandover(state) || ctx.currentUT > traj.EndUT)
                return false;

            ParsekLog.Info("Engine",
                "Loop first-run handover: #" + i.ToString(CultureInfo.InvariantCulture)
                + " \"" + (traj.VesselName ?? "?") + "\" UT="
                + ctx.currentUT.ToString("F2", CultureInfo.InvariantCulture)
                + " anchor=" + anchorUT.ToString("F2", CultureInfo.InvariantCulture)
                + " endUT=" + traj.EndUT.ToString("F2", CultureInfo.InvariantCulture)
                + " - the first run's fall keeps playing beside the loop copies");
            MoveToOverlapCopies(i, state, GhostPlaybackLogic.FirstRunFallCycleIndex);
            state = null;
            ghostActive = false;
            return true;
        }

        /// <summary>Moves a slot's primary ghost into the overlap list as the copy of
        /// <paramref name="cycle"/>: muted, with no pending spawn event left to fire.</summary>
        private void MoveToOverlapCopies(int i, GhostPlaybackState state, long cycle)
        {
            ghostStates.Remove(i);
            state.pendingSpawnLifecycle = PendingSpawnLifecycle.None;
            state.pendingSpawnFlags = default(TrajectoryPlaybackFlags);
            state.loopCycleIndex = cycle;
            if (state.ghost != null)
                GhostPlaybackLogic.MuteAllAudio(state);
            if (!overlapGhosts.TryGetValue(i, out List<GhostPlaybackState> overlaps))
            {
                overlaps = new List<GhostPlaybackState>();
                overlapGhosts[i] = overlaps;
            }
            overlaps.Add(state);
        }

        /// <summary>
        /// Before a falling member's first loop copy starts, its only copy can be the first run's kept
        /// fall: drives it. False when there is none, so the caller clears the overlap list as before.
        /// </summary>
        private bool UpdateFirstRunFallBeforeFirstCopy(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags, FrameContext ctx,
            double duration, double playbackStartUT, double scheduleStartUT, bool suppressVisualFx)
        {
            if (!overlapGhosts.TryGetValue(index, out List<GhostPlaybackState> overlaps) || overlaps == null)
                return false;
            for (int k = overlaps.Count - 1; k >= 0; k--)
            {
                GhostPlaybackState copy = overlaps[k];
                if (copy == null || copy.loopCycleIndex == GhostPlaybackLogic.FirstRunFallCycleIndex)
                    continue;
                DestroyOverlapGhostState(copy, index, traj, "before activation start UT");
                overlaps.RemoveAt(k);
            }
            if (overlaps.Count == 0)
                return false;
            UpdateExpireAndPositionOverlaps(index, traj, flags, ctx, overlaps,
                duration, 0.0, playbackStartUT, scheduleStartUT, suppressVisualFx);
            return true;
        }

        /// <summary>
        /// Ends a fall's newest copy once it has played past its own end, while its period leaves the
        /// next launch still ahead: the primary joins the overlap list, where the expiry places it at
        /// its end and gives it its ending (the explosion, the watch hold), and the slot stays empty
        /// until that launch. Older copies keep playing. A primary from no loop cycle just goes.
        /// </summary>
        private void EndNewestFallCopy(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags, FrameContext ctx,
            GhostPlaybackState primaryState, long newestCycle,
            double duration, double cycleDuration, double playbackStartUT, double scheduleStartUT,
            bool suppressVisualFx, long oldestDrawnCycle)
        {
            GhostRenderTrace.EmitGuardSkip(traj, index, ctx.currentUT, "fall-newest-copy-ended");
            CountFrameSkip(GhostPlaybackSkipReason.MissionLoopUnitInactive);
            if (primaryState != null)
            {
                ParsekLog.VerboseRateLimited("Engine",
                    "fall-ended-" + index.ToString(CultureInfo.InvariantCulture),
                    "Fall copy reached its end before the next launch: #"
                        + index.ToString(CultureInfo.InvariantCulture)
                        + " \"" + (traj.VesselName ?? "?") + "\" cycle="
                        + primaryState.loopCycleIndex.ToString(CultureInfo.InvariantCulture)
                        + " newestCycle=" + newestCycle.ToString(CultureInfo.InvariantCulture)
                        + " UT=" + ctx.currentUT.ToString("F2", CultureInfo.InvariantCulture)
                        + " duration=" + duration.ToString("F2", CultureInfo.InvariantCulture));
                if (primaryState.loopCycleIndex < 0)
                    DestroyGhost(index, traj, flags, reason: "fall copy past its end");
                else
                    MoveToOverlapCopies(index, primaryState, primaryState.loopCycleIndex);
            }
            if (overlapGhosts.TryGetValue(index, out List<GhostPlaybackState> overlaps) && overlaps.Count > 0)
                UpdateExpireAndPositionOverlaps(index, traj, flags, ctx, overlaps,
                    duration, cycleDuration, playbackStartUT, scheduleStartUT, suppressVisualFx,
                    oldestDrawnCycle);
        }

        /// <summary>Ends a non-uniform unit's debris fall that played to its end in the wait, with the
        /// overlap expiry's ending. The copy's window is taken as already over.</summary>
        private void ExpireEndedFallCopy(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags, FrameContext ctx,
            GhostPlaybackState state, bool suppressVisualFx)
        {
            MoveToOverlapCopies(index, state, state.loopCycleIndex);
            List<GhostPlaybackState> overlaps = overlapGhosts[index];
            UpdateExpireAndPositionOverlaps(index, traj, flags, ctx, overlaps,
                -1.0, 0.0, traj.StartUT, ctx.currentUT, suppressVisualFx);
            if (overlaps.Count == 0)
                overlapGhosts.Remove(index);
        }

        /// <summary>
        /// While high warp hides a fall's mesh, its primary goes as soon as its own copy has ended:
        /// kept, it would be given its ending after the warp, long after the impact. The watched ghost
        /// is kept hidden, so the camera never loses its target here.
        /// </summary>
        private void DropEndedFallAtWarp(
            int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx,
            double intervalSeconds, double duration, double scheduleStartUT, bool keepCadence,
            ref GhostPlaybackState state, ref bool ghostActive)
        {
            double cycleDuration = GhostPlaybackLogic.ResolveOverlapCadence(intervalSeconds, duration, keepCadence);
            if (state == null || i == ctx.protectedIndex
                || !GhostPlaybackLogic.HasOverlapCopyEnded(
                    ctx.currentUT, state.loopCycleIndex, scheduleStartUT, cycleDuration, duration))
                return;
            DestroyGhost(i, traj, f, reason: "fall copy ended under high warp");
            state = null;
            ghostActive = false;
        }

        /// <summary>
        /// Ends a fall copy the per-recording cap retires: no explosion, and the watch camera (when it
        /// follows this copy) bridges as at any quiet expiry.
        /// </summary>
        private void EndFallCopyQuietly(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags, FrameContext ctx,
            GhostPlaybackState copy, long cycle)
        {
            if (cycle != GhostPlaybackLogic.FirstRunFallCycleIndex)
            {
                Vector3 position = copy.ghost != null ? ReadGhostWorldPosition(copy) : Vector3.zero;
                OnOverlapCameraAction?.Invoke(new CameraActionEvent
                {
                    Index = index,
                    Action = CameraActionType.ExplosionHoldEnd,
                    NewCycleIndex = cycle,
                    AnchorPosition = position,
                    HoldUntilUT = ctx.currentUT,
                    Trajectory = traj, Flags = flags
                });
                OnOverlapExpired?.Invoke(new OverlapExpiredEvent
                {
                    Index = index, Trajectory = traj, State = copy, Flags = flags,
                    CycleIndex = cycle,
                    ExplosionFired = false,
                    ExplosionPosition = position
                });
            }
            ParsekLog.VerboseRateLimited("Engine",
                "fall-copy-cap-" + index.ToString(CultureInfo.InvariantCulture),
                "Fall copy ended quietly at the copy cap: #" + index.ToString(CultureInfo.InvariantCulture)
                    + " \"" + (traj.VesselName ?? "?") + "\" cycle=" + cycle.ToString(CultureInfo.InvariantCulture)
                    + " cap=" + GhostPlayback.MaxOverlapGhostsPerRecording.ToString(CultureInfo.InvariantCulture));
            DestroyOverlapGhostState(copy, index, traj, "fall copy cap");
        }

        /// <summary>
        /// Step 2c of <see cref="UpdateUnitMemberPlayback"/> for a uniform unit that does not overlap
        /// itself: the falling member is a per-recording overlap loop on the unit's launch cadence, so
        /// each launch's copy plays to its own end while the rest of the mission keeps its single
        /// span-clock instance. The cadence is never raised; copies past the cap end quietly. The
        /// caller returns after it.
        /// </summary>
        internal void UpdateUnitMemberFallSchedule(
            int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx,
            in GhostPlaybackLogic.LoopUnit unit, string unitKey, double memberStartUT, double memberEndUT,
            bool suppressGhosts, bool suppressVisualFx,
            ref GhostPlaybackState state, ref bool ghostActive)
        {
            TryKeepFirstRunFall(i, traj, ctx, unit.PhaseAnchorUT, ref state, ref ghostActive);
            if (ShouldRetireOrdinaryStateAtUnitHandover(state))
            {
                DestroyGhost(i, traj, f, reason: "first run handed to the loop unit");
                state = null;
                ghostActive = false;
            }

            double cadence = GhostPlaybackLogic.UnitLaunchCadenceSeconds(unit);
            double memberDuration = memberEndUT - memberStartUT;
            double scheduleStartUT = GhostPlaybackLogic.ComputeMemberOverlapScheduleStartUT(
                unit.PhaseAnchorUT, unit.SpanStartUT, memberStartUT);

            bool suppressOverlapGhosts = false;
            if (suppressGhosts)
            {
                if (!GhostPlaybackLogic.TryComputeNewestOverlapPlaybackUT(
                        ctx.currentUT, cadence, memberDuration, memberStartUT, scheduleStartUT,
                        out double newestUT, out _, keepCadence: true)
                    || GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(ctx.warpRate, traj, newestUT))
                {
                    DropEndedFallAtWarp(
                        i, traj, f, ctx, cadence, memberDuration, scheduleStartUT, true,
                        ref state, ref ghostActive);
                    HideFallAtWarp(i, traj, ctx, state, ghostActive);
                    return;
                }
                suppressOverlapGhosts = true;
            }

            ParsekLog.VerboseRateLimited(
                "Engine", unitKey + "-fall-" + i.ToString(CultureInfo.InvariantCulture),
                "Mission fall unit owner=" + unit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                    + " member #" + i.ToString(CultureInfo.InvariantCulture)
                    + " launchCadence=" + cadence.ToString("F2", CultureInfo.InvariantCulture)
                    + " span=" + (unit.SpanEndUT - unit.SpanStartUT).ToString("F2", CultureInfo.InvariantCulture)
                    + " memberDur=" + memberDuration.ToString("F2", CultureInfo.InvariantCulture)
                    + " schedStart=" + scheduleStartUT.ToString("F2", CultureInfo.InvariantCulture),
                5.0);

            UpdateOverlapPlayback(
                i, traj, f, ctx, state, cadence, memberDuration, memberStartUT, scheduleStartUT,
                suppressVisualFx, suppressOverlapGhosts, keepCadence: true, endAtOwnEnd: true);
        }

        /// <summary>The fall's high-warp hide: the mesh is hidden and its copies are dropped, as in every
        /// overlap branch.</summary>
        private void HideFallAtWarp(
            int i, IPlaybackTrajectory traj, FrameContext ctx, GhostPlaybackState state, bool ghostActive)
        {
            GhostRenderTrace.EmitGuardSkip(traj, i, ctx.currentUT, "unit-fall-warp-hidden");
            if (ghostActive && state != null && state.ghost != null && IsGhostMeshActive(state))
            {
                DeactivateGhostMesh(state);
                ResetGhostAppearanceTracking(state);
            }
            DestroyAllOverlapGhosts(i);
            CountFrameSkip(GhostPlaybackSkipReason.WarpHidden);
        }

        /// <summary>
        /// Step 2c of <see cref="UpdateUnitMemberPlayback"/> for a unit whose cycles are not uniform
        /// (a relaunch schedule, loiter cuts, holds, the descent trigger): the span-clock primary plays
        /// on into its fall through the wait between cycles, and a copy still falling when the next
        /// cycle takes the slot is kept as an overlap copy on the fall clock until its own end. With
        /// the launch alignment engaged the overlap list belongs to the boundary secondary, so the
        /// primary alone is drawn and the next relaunch cuts it. Returns true while fall copies live in
        /// the overlap list (the caller must not clear it).
        /// </summary>
        internal bool UpdateNonUniformFall(
            int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx,
            in GhostPlaybackLogic.LoopUnit unit, long unitCycle, double memberStartUT, double memberEndUT,
            bool suppressGhosts, bool suppressVisualFx,
            ref GhostPlaybackLogic.UnitMemberRenderDecision decision, ref double spanLoopUT,
            ref GhostPlaybackState state, ref bool ghostActive)
        {
            if (GhostPlaybackLogic.IsDescentDrivenMember(unit, i))
                return false;
            bool inWait = decision == GhostPlaybackLogic.UnitMemberRenderDecision.HiddenInterCycleTail;
            GhostPlaybackLogic.TryExtendRenderIntoFall(
                unit, i, ctx.currentUT, memberStartUT, memberEndUT, unitCycle, ref decision, ref spanLoopUT);
            if (unit.LaunchHoldEngaged)
                return false;
            if (suppressGhosts)
            {
                // High warp hides every fall: the copies go, and so does a primary of an older cycle,
                // which would be given its ending after the warp, long after its impact.
                if (overlapGhosts.ContainsKey(i))
                {
                    DestroyAllOverlapGhosts(i);
                    overlapGhosts.Remove(i);
                }
                if (state != null && i != ctx.protectedIndex
                    && state.loopCycleIndex >= 0 && state.loopCycleIndex < unitCycle)
                {
                    DestroyGhost(i, traj, f, reason: "fall copy ended under high warp");
                    state = null;
                    ghostActive = false;
                }
                return false;
            }

            if (!TryKeepFirstRunFall(i, traj, ctx, unit.PhaseAnchorUT, ref state, ref ghostActive)
                && state != null && state.loopCycleIndex >= 0
                && (state.loopCycleIndex < unitCycle
                    || (inWait && decision != GhostPlaybackLogic.UnitMemberRenderDecision.Render)))
            {
                // An older cycle's fall, or this cycle's fall past its end: the overlap list plays the
                // one out and expires the other with its ending.
                MoveToOverlapCopies(i, state, state.loopCycleIndex);
                state = null;
                ghostActive = false;
            }

            if (!overlapGhosts.TryGetValue(i, out List<GhostPlaybackState> overlaps)
                || overlaps == null || overlaps.Count == 0)
                return false;
            UpdateExpireAndPositionOverlaps(i, traj, f, ctx, overlaps,
                memberEndUT - memberStartUT, 0.0, memberStartUT, double.NaN, suppressVisualFx,
                GhostPlaybackLogic.OldestDrawnFallCycle(unitCycle), unit);
            return overlaps.Count > 0;
        }
    }
}
