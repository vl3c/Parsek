using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Looping and overlap playback, loop-synced debris, the auto-loop launch schedule and the loop / distance utilities.
    internal partial class GhostPlaybackEngine
    {
        private readonly struct AutoLoopQueueCandidate
        {
            internal AutoLoopQueueCandidate(
                int recordingIndex,
                double playbackStartUT,
                double playbackEndUT,
                string recordingId)
            {
                RecordingIndex = recordingIndex;
                PlaybackStartUT = playbackStartUT;
                PlaybackEndUT = playbackEndUT;
                RecordingId = recordingId ?? string.Empty;
            }

            internal int RecordingIndex { get; }
            internal double PlaybackStartUT { get; }
            internal double PlaybackEndUT { get; }
            internal string RecordingId { get; }
        }

        /// <summary>
        /// Loop-synced-debris dispatch (B10) of UpdatePlayback: when this slot is debris
        /// synced to a looping parent, render it on the parent's loop clock. Returns true when
        /// the caller should `continue` — i.e. the parent-loop branch was taken (every internal
        /// `continue` in the original block, including the trailing one). Returns false in the
        /// exactly two cases that fell through to warp-suppression in the original:
        /// (a) LoopSyncParentIdx out of range, (b) parent not looping. <paramref name="state"/>
        /// and <paramref name="ghostActive"/> are by ref because the cycle-change path nulls
        /// state / clears ghostActive and the inner RenderInRangeGhost reassigns both; on a
        /// false return the block wrote neither, so the contract matches inline either way.
        /// Verbatim extraction; the syncCtx struct copy stays a stack local.
        /// </summary>
        private bool TryUpdateLoopSyncedDebris(
            int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx,
            IReadOnlyList<IPlaybackTrajectory> trajectories, bool suppressGhosts, bool suppressVisualFx,
            double activationStartUT, bool hasPointData, bool hasInterpolatedPoints,
            bool hasSurfaceData, bool hasOrbitData, ref GhostPlaybackState state, ref bool ghostActive)
        {
            // Widen the seam (Phase D2): reach the parent-loop path when the parent is a Mission
            // loop unit member even if it is not per-recording looping (unit members carry no own
            // LoopPlayback flag, so ShouldLoopPlayback(parent) is false for them).
            if (traj.LoopSyncParentIdx >= 0 && traj.LoopSyncParentIdx < trajectories.Count)
            {
                var parent = trajectories[traj.LoopSyncParentIdx];
                if (ShouldLoopPlayback(parent) || currentLoopUnits.OwnerByIndex.ContainsKey(traj.LoopSyncParentIdx))
                {
                    int parentIdx = traj.LoopSyncParentIdx;
                    double parentLoopUT;
                    long parentCycle;
                    bool parentPaused;

                    // Edge 9: when the loop-sync parent is itself a Mission loop unit member, its timing
                    // is governed by the unit's SHARED span clock, not its own per-recording loop
                    // clock. Sourcing TryComputeLoopPlaybackUT here would resolve the parent's
                    // standalone phase (wrong: a unit member's loop clock never sweeps past its own
                    // EndUT into a sibling's window). Instead source the span clock for the parent's
                    // owner so the debris rides the same clock the live parent member does. The span
                    // clock has no inter-cycle pause (cadence == span), so parentPaused is always
                    // false on this branch. The rest of the block (cycle-change rebuild, debrisInRange
                    // test, RenderInRangeGhost) is unchanged - it keys off parentLoopUT / parentCycle.
                    if (GhostPlaybackLogic.ShouldSourceDebrisFromUnitSpan(
                            parentIdx, currentLoopUnits, out GhostPlaybackLogic.LoopUnit parentUnit))
                    {
                        // Interval-level trim: a ride-along debris belongs to the slice of its
                        // parent's flight when it was jettisoned. If the parent member is start/end-
                        // trimmed (a composition interval unchecked) and this debris was jettisoned
                        // OUTSIDE the parent's trimmed render window, it is debris of a trimmed-out
                        // segment - suppress it, so watching only a late segment (e.g. the pod after
                        // the decouple) shows only that segment's own child debris, not the earlier
                        // launch-stage jettisons. Untrimmed parents keep their full [StartUT,EndUT]
                        // window (MemberStartUT/EndUT fall back to it), so this is a no-op for an
                        // untrimmed mission.
                        double parentMemberStart = parentUnit.MemberStartUT(parentIdx, parent.StartUT);
                        double parentMemberEnd = parentUnit.MemberEndUT(parentIdx, parent.EndUT);
                        if (traj.StartUT < parentMemberStart - LoopTiming.BoundaryEpsilon
                            || traj.StartUT > parentMemberEnd + LoopTiming.BoundaryEpsilon)
                        {
                            GhostRenderTrace.EmitGuardSkip(
                                traj, i, ctx.currentUT, "debris-outside-parent-trim-window");
                            if (ghostActive)
                            {
                                DestroyGhost(i, traj, f,
                                    reason: "debris jettisoned outside parent member's trimmed window");
                                DestroyAllOverlapGhosts(i);
                            }
                            CountFrameSkip(GhostPlaybackSkipReason.MissionLoopUnitInactive);
                            return true;
                        }

                        // MISSION SELF-OVERLAP: when the parent's unit overlaps (overlap cadence
                        // shorter than the span), the debris must overlap on the SAME cadence so its
                        // instances ride alongside the parent member's instances. The debris is NOT a
                        // unit member, but its absolute window [debrisStart, debrisEnd] sits inside the
                        // span, so the SAME anchor-offset reduction the member path uses applies with
                        // the debris's own window:
                        //   scheduleStartUT = PhaseAnchorUT + (debrisStart - spanStart)
                        //   playbackStartUT = debrisStart
                        //   duration        = debrisEnd - debrisStart
                        //   intervalSeconds = OverlapCadenceSeconds
                        // Route it through UpdateOverlapPlayback (its own overlapGhosts[i] slot) so the
                        // debris gets the same staggered instances as its parent. When the unit does
                        // NOT overlap, fall through to the single span-clock instance below (unchanged).
                        double parentUnitSpan = parentUnit.SpanEndUT - parentUnit.SpanStartUT;
                        if (parentUnitSpan > 0 && parentUnit.OverlapCadenceSeconds < parentUnitSpan)
                        {
                            double debrisDuration = traj.EndUT - traj.StartUT;
                            if (debrisDuration <= 0)
                            {
                                GhostRenderTrace.EmitGuardSkip(
                                    traj, i, ctx.currentUT, "debris-unit-overlap-zero-duration");
                                if (ghostActive)
                                    DestroyGhost(i, traj, f, reason: "debris unit overlap zero duration");
                                CountFrameSkip(GhostPlaybackSkipReason.LoopSyncFailed);
                                return true;
                            }

                            double debrisScheduleStartUT =
                                parentUnit.PhaseAnchorUT + (traj.StartUT - parentUnit.SpanStartUT);
                            double debrisPlaybackStartUT = traj.StartUT;

                            bool suppressDebrisOverlapGhosts = false;
                            if (suppressGhosts)
                            {
                                if (!GhostPlaybackLogic.TryComputeNewestOverlapPlaybackUT(
                                        ctx.currentUT, parentUnit.OverlapCadenceSeconds, debrisDuration,
                                        debrisPlaybackStartUT, debrisScheduleStartUT,
                                        out double debrisNewestUT, out _)
                                    || GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                                        ctx.warpRate, traj, debrisNewestUT))
                                {
                                    GhostRenderTrace.EmitGuardSkip(
                                        traj, i, ctx.currentUT, "debris-unit-overlap-warp-hidden");
                                    if (ghostActive && state != null && state.ghost != null && state.ghost.activeSelf)
                                    {
                                        state.ghost.SetActive(false);
                                        ResetGhostAppearanceTracking(state);
                                    }
                                    DestroyAllOverlapGhosts(i);
                                    CountFrameSkip(GhostPlaybackSkipReason.WarpHidden);
                                    return true;
                                }
                                suppressDebrisOverlapGhosts = true;
                            }

                            ParsekLog.VerboseRateLimited(
                                "Engine", "debris-self-overlap-" + i.ToString(CultureInfo.InvariantCulture),
                                "Mission self-overlap debris #" + i.ToString(CultureInfo.InvariantCulture)
                                    + " parent #" + parentIdx.ToString(CultureInfo.InvariantCulture)
                                    + " owner=" + parentUnit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                                    + " overlapCadence=" + parentUnit.OverlapCadenceSeconds.ToString("F2", CultureInfo.InvariantCulture)
                                    + " debrisDur=" + debrisDuration.ToString("F2", CultureInfo.InvariantCulture),
                                5.0);

                            UpdateOverlapPlayback(
                                i, traj, f, ctx, state,
                                parentUnit.OverlapCadenceSeconds, debrisDuration,
                                debrisPlaybackStartUT, debrisScheduleStartUT,
                                suppressVisualFx, suppressDebrisOverlapGhosts);
                            LogLoopSyncedDebrisOverlapVisibility(
                                i, traj, ctx.currentUT, parentUnit.OverlapCadenceSeconds,
                                debrisDuration, debrisPlaybackStartUT, debrisScheduleStartUT);
                            return true;
                        }

                        // Pass parentUnit.RelaunchSchedule so ride-along debris rides its scheduled
                        // parent's non-uniform launches (NOT the debris's own); null for a non-scheduled
                        // parent keeps the uniform path. With a scheduled parent (or any unit whose
                        // CadenceSeconds > span) the parked inter-cycle tail DOES engage between
                        // launches, so we MUST consume isInInterCycleTail and hide the debris during the
                        // gap (otherwise debris whose own end is at/after the parent's spanEnd would
                        // stay "in range" at the clamped spanEnd frame and render a frozen stale ghost
                        // between launches). Route it through parentPaused so it reuses the existing
                        // hide-and-skip path, exactly as the parent member is hidden by HiddenInterCycleTail.
                        if (!GhostPlaybackLogic.TryComputeSpanLoopUT(
                                ctx.currentUT, parentUnit.PhaseAnchorUT, parentUnit.SpanStartUT,
                                parentUnit.SpanEndUT, parentUnit.CadenceSeconds, out parentLoopUT,
                                out parentCycle, out bool parentInInterCycleTail, parentUnit.RelaunchSchedule,
                                parentUnit.LoiterCuts, parentUnit.ArrivalHoldSeconds, parentUnit.ArrivalHoldAtUT,
                                parentUnit.ArrivalAlignPeriodSeconds,
                                parentUnit.LaunchBodyRotationPeriodSeconds, parentUnit.LaunchHoldEngaged,
                                parentUnit.RecordedSoiExitUT,
                                parentUnit.ArrivalJointSecondaryPeriodSeconds,
                                parentUnit.ArrivalJointSecondaryToleranceSeconds,
                                parentUnit.ArrivalJointMaxWholeHoldPeriods))
                        {
                            GhostRenderTrace.EmitGuardSkip(
                                traj, i, ctx.currentUT, "parent-unit-span-clock-unresolved");
                            if (ghostActive)
                                DestroyGhost(i, traj, f, reason: "parent unit span clock unresolved");
                            CountFrameSkip(GhostPlaybackSkipReason.LoopSyncFailed);
                            return true;
                        }
                        parentPaused = parentInInterCycleTail; // parked between launches -> hide debris
                        ParsekLog.VerboseOnChange(
                            "Engine",
                            "loop-sync-debris-unit-" + i.ToString(CultureInfo.InvariantCulture),
                            parentUnit.OwnerIndex.ToString(CultureInfo.InvariantCulture),
                            "loop-sync debris #" + i.ToString(CultureInfo.InvariantCulture)
                                + " parent #" + parentIdx.ToString(CultureInfo.InvariantCulture)
                                + " is unit member (owner=" + parentUnit.OwnerIndex.ToString(CultureInfo.InvariantCulture)
                                + ") - sourcing span clock");
                    }
                    else if (!TryComputeLoopPlaybackUT(parent, ctx.currentUT, ctx.autoLoopIntervalSeconds,
                            out parentLoopUT, out parentCycle, out parentPaused, parentIdx))
                    {
                        GhostRenderTrace.EmitGuardSkip(
                            traj, i, ctx.currentUT, "parent-loop-sync-failed");
                        if (ghostActive)
                            DestroyGhost(i, traj, f, reason: "parent loop sync failed");
                        CountFrameSkip(GhostPlaybackSkipReason.LoopSyncFailed);
                        return true;
                    }

                    bool suppressLoopSyncGhost = suppressGhosts
                        && GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                            ctx.warpRate, traj, parentLoopUT);
                    if (parentPaused || suppressLoopSyncGhost)
                    {
                        GhostRenderTrace.EmitGuardSkip(
                            traj, i, ctx.currentUT,
                            parentPaused ? "parent-loop-paused" : "parent-loop-warp-hidden");
                        if (state != null)
                            DestroyGhost(i, traj, f, reason: "parent loop paused/warp");
                        CountFrameSkip(parentPaused
                            ? GhostPlaybackSkipReason.ParentLoopPaused
                            : GhostPlaybackSkipReason.WarpHidden);
                        return true;
                    }

                    // Cycle change: rebuild ghost for clean visual state
                    if (state != null && state.loopCycleIndex != parentCycle)
                    {
                        DestroyGhost(i, traj, f, reason: "parent loop cycle change");
                        ghostActive = false;
                        state = null;
                        // Clear completed-event flag so the debris can play again
                        completedEventFired.Remove(i);
                    }

                    bool debrisInRange = parentLoopUT >= activationStartUT && parentLoopUT <= traj.EndUT;
                    if (debrisInRange)
                    {
                        // Override UT for positioning — use parent's loop clock
                        var syncCtx = ctx;
                        syncCtx.currentUT = parentLoopUT;
                        if (RenderInRangeGhost(i, traj, f, syncCtx, suppressVisualFx,
                                hasPointData, hasInterpolatedPoints, hasSurfaceData, hasOrbitData,
                                allowEarlyDestroyedDebrisCompletion: false,
                                ref state, ref ghostActive))
                        {
                            if (state != null)
                                state.loopCycleIndex = parentCycle;
                        }
                    }
                    else if (state != null)
                    {
                        DestroyGhost(i, traj, f, reason: "outside debris UT range in parent loop");
                    }
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Non-gated per-frame visibility diagnostic for a ride-along debris rendered through the
        /// mission self-overlap path. Unlike <see cref="GhostRenderTrace"/> (which only logs an
        /// ~4 s window after each spawn, then goes silent in steady state), this reports the
        /// debris's ACTUAL rendered-instance count every frame (rate-limited 2 s), so a playtest
        /// log shows how long the debris is really visible across overlap cycles and whether the
        /// staggered background instances (which should collectively cover its whole flight) render
        /// at all. <c>totalVisible</c> dropping to 0 between cycles, or <c>overlapActive</c> staying
        /// 0 while the primary resets every cadence, is the "debris disappears too quickly" signal.
        /// </summary>
        private void LogLoopSyncedDebrisOverlapVisibility(
            int index, IPlaybackTrajectory traj, double currentUT,
            double cadenceSeconds, double duration, double playbackStartUT, double scheduleStartUT)
        {
            int primaryActive = 0;
            long primaryCycle = -1;
            if (ghostStates.TryGetValue(index, out GhostPlaybackState primary) && primary != null)
            {
                primaryCycle = primary.loopCycleIndex;
                if (primary.ghost != null && primary.ghost.activeSelf)
                    primaryActive = 1;
            }

            int overlapActive = 0;
            int overlapTotal = 0;
            if (overlapGhosts.TryGetValue(index, out List<GhostPlaybackState> overlaps) && overlaps != null)
            {
                overlapTotal = overlaps.Count;
                for (int k = 0; k < overlaps.Count; k++)
                {
                    if (overlaps[k]?.ghost != null && overlaps[k].ghost.activeSelf)
                        overlapActive++;
                }
            }

            GhostPlaybackLogic.TryComputeNewestOverlapPlaybackUT(
                currentUT, cadenceSeconds, duration, playbackStartUT, scheduleStartUT,
                out double newestLoopUT, out long newestCycle);

            ParsekLog.VerboseRateLimited(
                "Engine", "debris-vis-" + index.ToString(CultureInfo.InvariantCulture),
                "Loop-synced debris #" + index.ToString(CultureInfo.InvariantCulture)
                    + " \"" + (traj?.VesselName ?? "?") + "\" visibility:"
                    + " totalVisible=" + (primaryActive + overlapActive).ToString(CultureInfo.InvariantCulture)
                    + " (primaryActive=" + primaryActive.ToString(CultureInfo.InvariantCulture)
                    + " cycle=" + primaryCycle.ToString(CultureInfo.InvariantCulture)
                    + ", overlapActive=" + overlapActive.ToString(CultureInfo.InvariantCulture)
                    + "/" + overlapTotal.ToString(CultureInfo.InvariantCulture) + ")"
                    + " newestCycle=" + newestCycle.ToString(CultureInfo.InvariantCulture)
                    + " newestLoopUT=" + newestLoopUT.ToString("F2", CultureInfo.InvariantCulture)
                    + " cadence=" + cadenceSeconds.ToString("F2", CultureInfo.InvariantCulture)
                    + " window=[" + playbackStartUT.ToString("F2", CultureInfo.InvariantCulture)
                    + "," + (playbackStartUT + duration).ToString("F2", CultureInfo.InvariantCulture) + "]"
                    + " at UT=" + currentUT.ToString("F2", CultureInfo.InvariantCulture),
                2.0);
        }

        /// <summary>
        /// Handles looping ghost playback for a single trajectory.
        /// Manages cycle changes, ghost spawn/destroy, pause windows.
        /// Fires CameraActionEvents for watch mode interactions (engine does not
        /// know about watch mode — ParsekFlight handles camera in event handlers).
        /// </summary>
        private void UpdateLoopingPlayback(int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, FrameContext ctx,
            bool suppressGhosts, bool suppressVisualFx)
        {
            GhostPlaybackState state;
            ghostStates.TryGetValue(index, out state);
            bool ghostActive = HasLoadedGhostVisuals(state);

            if (!TryResolveLoopSchedule(
                    traj, ctx.autoLoopIntervalSeconds, index,
                    out double playbackStartUT,
                    out double scheduleStartUT,
                    out double duration,
                    out double intervalSeconds))
            {
                GhostRenderTrace.EmitGuardSkip(
                    traj, index, ctx.currentUT, "loop-schedule-resolution-failed");
                if (ghostActive)
                    DestroyGhost(index, traj, flags, reason: "loop schedule resolution failed");
                CountFrameSkip(GhostPlaybackSkipReason.LoopSyncFailed);
                return;
            }

            // #381: If the period is shorter than the recording duration, successive
            // launches overlap — use multi-cycle path.
            if (GhostPlaybackLogic.IsOverlapLoop(intervalSeconds, duration))
            {
                UpdateOverlapLoopDispatch(
                    index, traj, flags, ctx, state, ghostActive, suppressGhosts, suppressVisualFx,
                    intervalSeconds, duration, playbackStartUT, scheduleStartUT);
                return;
            }

            // --- Period >= duration: single ghost path (pause window may apply) ---
            DestroyAllOverlapGhosts(index);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;
            if (!TryComputeLoopPlaybackUT(traj, ctx.currentUT, ctx.autoLoopIntervalSeconds,
                    out loopUT, out cycleIndex, out inPauseWindow, index))
            {
                GhostRenderTrace.EmitGuardSkip(
                    traj, index, ctx.currentUT, "loop-ut-computation-failed");
                if (ghostActive)
                    DestroyGhost(index, traj, flags, reason: "loop UT computation failed");
                CountFrameSkip(GhostPlaybackSkipReason.LoopSyncFailed);
                return;
            }

            // High time warp: hide moving loop ghosts, but keep stationary surface
            // segments visible because their mesh is not chasing a high-rate trajectory.
            if (suppressGhosts && GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                    ctx.warpRate, traj, loopUT))
            {
                GhostRenderTrace.EmitGuardSkip(
                    traj, index, ctx.currentUT, "loop-warp-hidden");
                if (ghostActive && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ParsekLog.Info("Engine",
                        $"Ghost #{index} \"{traj.VesselName}\" (loop) hidden: warp > {WarpThresholds.GhostHide}x");
                    // Fire camera event so host can exit watch mode
                    OnLoopCameraAction?.Invoke(new CameraActionEvent
                    {
                        Index = index, Action = CameraActionType.ExitWatch,
                        Trajectory = traj, Flags = flags
                    });
                }
                DestroyAllOverlapGhosts(index);
                CountFrameSkip(GhostPlaybackSkipReason.WarpHidden);
                return;
            }

            // Rebuild once per loop cycle to guarantee clean visual state and event indices.
            bool cycleChanged = HasLoopCycleChanged(state, cycleIndex);
            if (cycleChanged && state != null)
            {
                if (!HasLoadedGhostVisuals(state))
                {
                    // Bug #450 B2: split first-spawn builds can survive across a loop
                    // boundary before any ghost GameObject exists. Advance the cycle index
                    // so the pending spawn eventually finalizes against the current cycle,
                    // but do NOT emit restart / explosion-hold events for a ghost that
                    // never actually spawned.
                    ReusePrimaryGhostAcrossCycle(index, traj, flags, state, loopUT, cycleIndex);
                }
                else
                {
                // Position at the loop endpoint so explosion appears at the real loop end.
                    // Bug #613 (PR #594 P1 round 2): clear retire signal so we
                    // can detect a fresh retirement raised by the loop-endpoint
                    // positioner (PositionGhostAtLoopEndpoint -> PositionLoop ->
                    // relative-frame retire branch). Without this, the side
                    // effects below (explosion FX, ExplosionHold camera event,
                    // LoopRestarted with ExplosionPosition) all fire from a
                    // stale (0,0,0) transform when the LoopAnchor pid is
                    // unresolvable post-rewind.
                    state.anchorRetiredThisFrame = false;
                    PositionGhostAtLoopEndpoint(
                        index, traj, flags, state, ctx.currentUT, ctx.warpRate);

                    bool loopEndpointRetired = RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                        state.anchorRetiredThisFrame);

                    bool needsExplosion = !loopEndpointRetired
                    && !state.explosionFired
                    && GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(
                        traj, ResolveLoopPlaybackEndpointUT(traj));

                    if (loopEndpointRetired)
                    {
                        ParsekLog.VerboseRateLimited("Engine", $"loop-endpoint-suppressed-{index}",
                            $"loop endpoint side effects suppressed: anchor retired ghost #{index} \"{traj.VesselName}\" cycle={cycleIndex}");
                    }

                    if (needsExplosion)
                        TriggerExplosionIfDestroyed(state, traj, index, ctx.warpRate);

                    // Fire camera event for cycle change (host handles camera anchor/hold/retarget)
                    if (!loopEndpointRetired)
                    {
                        OnLoopCameraAction?.Invoke(new CameraActionEvent
                        {
                            Index = index,
                            Action = needsExplosion ? CameraActionType.ExplosionHoldStart : CameraActionType.ExplosionHoldEnd,
                            AnchorPosition = state.ghost.transform.position,
                            HoldUntilUT = ctx.currentUT + GhostPlayback.OverlapExplosionHoldSeconds,
                            Trajectory = traj, Flags = flags
                        });

                        // Fire loop restarted event
                        OnLoopRestarted?.Invoke(new LoopRestartedEvent
                        {
                            Index = index, Trajectory = traj, State = state, Flags = flags,
                            PreviousCycleIndex = state.loopCycleIndex,
                            NewCycleIndex = cycleIndex,
                            ExplosionFired = needsExplosion,
                            ExplosionPosition = state.ghost.transform.position
                        });
                    }

                    GhostPlaybackLogic.ResetReentryFx(state, index);

                    // #406 follow-up: reuse the ghost GameObject across the loop
                    // cycle boundary instead of destroy+spawn. The reuse preserves
                    // the ghost hierarchy, reentryFxInfo, reentryFxPendingBuild
                    // (#450 B3), all info dictionaries, cameraPivot identity,
                    // and horizonProxy; it resets playback iterators, explosionFired,
                    // pauseHidden, rcsSuppressed, lightPlaybackStates, fakeCanopies,
                    // logicalPartIds (re-materialised from snapshot), and part
                    // visibility (previous-cycle decoupled parts get reactivated).
                    // This path does NOT consume a #414 spawn slot and does NOT
                    // increment frameLazyReentryBuildCount. See
                    // docs/dev/plan-406-ghost-reuse-loop-cycles.md for the full
                    // preservation table.
                    //
                    // Suppress the retarget camera event when an explosion was just
                    // emitted: ExplosionHoldStart already set the watch handler into
                    // hold (watchedOverlapCycleIndex == -2) and a follow-up
                    // RetargetToNewGhost would be ignored anyway. The contract is
                    // "one OnLoopCameraAction per cycle boundary" — see
                    // RuntimeTests.ExplosionAnchorPosition_BelowTerrain_ClampsBeforeWatchHold.
                    // Non-destroyed boundaries (ExplosionHoldEnd) still need the
                    // retarget so the watch handler swaps the bridge anchor for
                    // the new cycle's pivot.
                    ReusePrimaryGhostAcrossCycle(
                        index, traj, flags, state, loopUT, cycleIndex,
                        // #613 P1 round 2: also suppress retarget when retired —
                        // the ghost was just hidden and there is no real
                        // (post-positioning) world position to anchor a camera
                        // retarget on this cycle boundary.
                        emitRetargetEvent: !needsExplosion && !loopEndpointRetired);
                    // state is the same object; loaded-ghost path keeps visuals live, and
                    // pending-build path above just advances the cycle index without
                    // emitting restart side effects.
                }
            }

            if (TryHandleParentAnchoredDebrisCoverageRetired(
                    index, traj, state, loopUT, ctx.currentUT, ctx.warpRate,
                    "GhostPlaybackEngine.UpdateLoopingPlayback.pre-spawn",
                    ShouldExitWatchForCoverageRetiredCycle(index, cycleIndex, ctx),
                    out ghostActive))
                return;

            // Looped ghost distance gating
            double loopGhostDistance = ResolvePlaybackDistance(
                index, traj, state, loopUT, ctx.activeVesselPos);

            var (_, loopSimplified) =
                GhostPlaybackLogic.EvaluateLoopedGhostSpawn(loopGhostDistance);

            if (state == null)
            {
                // True first-spawn path: no ghost has ever existed this session
                // for this recording. Safe to throttle via the #414 spawn slot
                // cap because nothing visible is being replaced. Post-#406 the
                // cycle-rebuild branch above never falls through here — it
                // always reuses the existing ghost — so the throttle is always
                // safe on this line.
                if (!TryReserveSpawnSlot(index, "loop-first-spawn"))
                    return;
                state = CreatePendingSpawnState(
                    traj, loopUT, PendingSpawnLifecycle.LoopEnter, flags);
                state.loopCycleIndex = cycleIndex;
                ghostStates[index] = state;
                GhostVisualLoadStatus loopSpawnStatus = EnsureGhostVisualsLoaded(
                    index, traj, state, loopUT, "loop first spawn",
                    resetCompletedEventDedup: true);
                if (loopSpawnStatus == GhostVisualLoadStatus.Failed)
                {
                    CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                    ghostStates.Remove(index);
                    return;
                }
                QueueOrEmitGhostSpawnPending(index, traj, state, flags);
                if (loopSpawnStatus == GhostVisualLoadStatus.Pending)
                {
                    ghostActive = false;
                    return;
                }
                ghostActive = true;
            }

            if (state == null)
                return;

            // Zone-based rendering
            double loopZoneDistance = ResolvePlaybackDistance(
                index, traj, state, loopUT, ctx.activeVesselPos);
            double loopActiveVesselDistance = ResolvePlaybackActiveVesselDistance(
                index, traj, state, loopUT, ctx.activeVesselPos);
            CachePlaybackDistances(state, loopActiveVesselDistance, loopZoneDistance);
            var zoneResult = positioner.ApplyZoneRendering(
                index, state, traj, loopZoneDistance, loopUT, ctx.protectedIndex);
            if (zoneResult.hiddenByZone)
            {
                GhostRenderTrace.EmitGuardSkip(
                    traj, index, ctx.currentUT,
                    "loop-hidden-by-zone distance=" + FormatPlaybackDistanceForLog(loopZoneDistance));
                ghostActive = HandleHiddenGhostVisualState(
                    index, traj, state, loopUT, ctx.warpRate, loopZoneDistance, overlapGhost: false,
                    hiddenReason: $"loop hidden by distance LOD at {FormatPlaybackDistanceForLog(loopZoneDistance)}");
                return;
            }

            if (!HasLoadedGhostVisuals(state))
            {
                // Bug #414: throttle loop distance-tier rehydration.
                string loopReloadSite = state.pendingSpawnLifecycle != PendingSpawnLifecycle.None
                    ? "loop-first-spawn-continue"
                    : "loop-distance-tier-rehydrate";
                if (!TryReserveSpawnSlot(index, loopReloadSite))
                    return;
                GhostVisualLoadStatus loopLoadStatus = EnsureGhostVisualsLoaded(
                    index, traj, state, loopUT,
                    state.pendingSpawnLifecycle != PendingSpawnLifecycle.None
                        ? "continuing loop first spawn"
                        : "loop re-entered visible distance tier");
                if (loopLoadStatus == GhostVisualLoadStatus.Failed)
                {
                    CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                    return;
                }
                if (loopLoadStatus == GhostVisualLoadStatus.Pending)
                    return;
            }

            GhostPlaybackLogic.ApplyDistanceLodFidelity(state, zoneResult.reduceFidelity);
            bool forceWatchedFullFidelity = IsWatchedGhostState(index, state, ctx);
            bool skipLoopPartEvents = zoneResult.skipPartEvents || (loopSimplified && !forceWatchedFullFidelity);
            bool effectiveSuppressVisualFx = suppressVisualFx || zoneResult.suppressVisualFx;

            // Pause window: position at end, hide parts, zero velocity for reentry decay
            if (inPauseWindow)
            {
                HandleLoopPauseWindow(index, traj, flags, state, ctx.currentUT, ctx.warpRate);
                return;
            }

            // Bug #613 (PR #594 P1): clear the per-frame retire signal before
            // positioning. The relative loop positioner sets it back to true
            // if the recorded anchor is unresolvable.
            state.anchorRetiredThisFrame = false;
            GhostRenderTrace.BeginFrame(
                traj, index, ctx.currentUT, loopUT, "loop-primary");

            // Position the loop ghost
            bool primaryLoopUsedBodyFixed = PositionLoopAtPlaybackUT(
                index, traj, state, loopUT,
                effectiveSuppressVisualFx,
                "GhostPlaybackEngine.UpdateLoopPlayback");

            // Apply visual events
            bool loopRetired = RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                state.anchorRetiredThisFrame);
            GhostRenderTrace.EmitPostUpdate(
                traj, index, ctx.currentUT, loopUT, state, "loop-primary", loopRetired,
                ResolveRenderSurface(primaryLoopUsedBodyFixed, loopRetired),
                rawPlaybackUT: loopUT,
                activationStartUT: ResolveGhostActivationStartUT(traj));
            if (loopRetired)
            {
                // Retired loop ghost: stop FX cleanly, do NOT re-activate, do
                // NOT log appearance. See the matching gate in RenderInRangeGhost.
                ApplyFrameVisuals(index, traj, state, loopUT, ctx.warpRate,
                    skipPartEvents: true, suppressVisualFx: true,
                    allowTransientEffects: false);
            }
            else
            {
                bool activatedDeferredState = ActivateGhostVisualsIfNeeded(state);
                if (!skipLoopPartEvents)
                    ApplyFrameVisuals(index, traj, state, loopUT, ctx.warpRate,
                        false, effectiveSuppressVisualFx);
                if (ShouldRestoreDeferredRuntimeFxState(
                        activatedDeferredState,
                        effectiveSuppressVisualFx))
                    GhostPlaybackLogic.RestoreDeferredRuntimeFxState(state);
            }
        }

        /// <summary>
        /// The overlap branch of <see cref="UpdateLoopingPlayback"/> (#381: the period is shorter than the
        /// recording, so launches overlap): at warp, either hide everything (the newest overlap instance cannot be
        /// resolved or its mesh is warp-suppressed) or keep the newest primary and cull the clones; then run the
        /// multi-cycle overlap playback. The caller returns after it.
        /// </summary>
        internal void UpdateOverlapLoopDispatch(
            int index,
            IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags,
            FrameContext ctx,
            GhostPlaybackState state,
            bool ghostActive,
            bool suppressGhosts,
            bool suppressVisualFx,
            double intervalSeconds,
            double duration,
            double playbackStartUT,
            double scheduleStartUT)
        {
            bool suppressOverlapGhosts = false;
            if (suppressGhosts
                && (!GhostPlaybackLogic.TryComputeNewestOverlapPlaybackUT(
                        ctx.currentUT,
                        intervalSeconds,
                        duration,
                        playbackStartUT,
                        scheduleStartUT,
                        out double overlapLoopUT,
                        out _)
                    || GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                        ctx.warpRate, traj, overlapLoopUT)))
            {
                GhostRenderTrace.EmitGuardSkip(
                    traj, index, ctx.currentUT, "overlap-loop-warp-hidden");
                if (ghostActive && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ParsekLog.Info("Engine",
                        $"Ghost #{index} \"{traj.VesselName}\" (loop) hidden: warp > {WarpThresholds.GhostHide}x");
                    OnLoopCameraAction?.Invoke(new CameraActionEvent
                    {
                        Index = index, Action = CameraActionType.ExitWatch,
                        Trajectory = traj, Flags = flags
                    });
                }
                DestroyAllOverlapGhosts(index);
                CountFrameSkip(GhostPlaybackSkipReason.WarpHidden);
                return;
            }
            else if (suppressGhosts)
            {
                // Keep the newest stationary primary mesh visible, but continue
                // culling overlap clones at high warp for the original perf reason.
                suppressOverlapGhosts = true;
            }

            UpdateOverlapPlayback(index, traj, flags, ctx, state,
                intervalSeconds, duration, playbackStartUT, scheduleStartUT,
                suppressVisualFx, suppressOverlapGhosts);
        }

        /// <summary>
        /// Multi-cycle overlap path for negative intervals. Multiple ghosts from
        /// different cycles may be visible simultaneously.
        /// </summary>
        private void UpdateOverlapPlayback(int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, FrameContext ctx,
            GhostPlaybackState primaryState,
            double intervalSeconds, double duration,
            double playbackStartUT, double scheduleStartUT,
            bool suppressVisualFx, bool suppressOverlapGhosts = false,
            bool stopAfterSuppressOverlapGhostsForTesting = false)
        {
            if (ctx.currentUT < scheduleStartUT)
            {
                GhostRenderTrace.EmitGuardSkip(
                    traj, index, ctx.currentUT, "overlap-before-activation-start-ut");
                if (primaryState != null) DestroyGhost(index, traj, flags, reason: "before activation start UT");
                DestroyAllOverlapGhosts(index);
                CountFrameSkip(GhostPlaybackSkipReason.BeforeActivation);
                return;
            }

            // #443: Compute the effective launch cadence. If the user-configured
            // period would produce more simultaneously-live cycles than the cap,
            // cadence is raised to the minimum value that makes
            // ceil(duration/cadence) <= cap. The user's stored period is
            // unchanged — only the runtime spawn rate adjusts. This replaces
            // the pre-fix behaviour of silently culling older cycles via
            // GetActiveCycles's newest-cycle clamp (which stacked ghosts near
            // launch under short user periods).
            double effectiveCadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                intervalSeconds, duration, GhostPlayback.MaxOverlapGhostsPerRecording);
            LogOverlapCadenceIfChanged(index, traj, intervalSeconds, effectiveCadence, duration);

            double cycleDuration = Math.Max(effectiveCadence, LoopTiming.MinCycleDuration);

            long firstCycle, lastCycle;
            GhostPlaybackLogic.GetActiveCycles(
                ctx.currentUT,
                scheduleStartUT,
                scheduleStartUT + duration,
                effectiveCadence,
                GhostPlayback.MaxOverlapGhostsPerRecording, out firstCycle, out lastCycle);

            List<GhostPlaybackState> overlaps;
            if (!overlapGhosts.TryGetValue(index, out overlaps))
            {
                overlaps = new List<GhostPlaybackState>();
                overlapGhosts[index] = overlaps;
            }
            else if (suppressOverlapGhosts)
            {
                DestroyAllOverlapGhosts(index);
            }
            if (suppressOverlapGhosts && stopAfterSuppressOverlapGhostsForTesting)
                return;

            // Primary ghost represents the newest (lastCycle)
            bool primaryCycleChanged = HasLoopCycleChanged(primaryState, lastCycle);
            double primaryLoopUT = GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT(
                ctx.currentUT,
                scheduleStartUT,
                playbackStartUT,
                duration,
                cycleDuration,
                lastCycle);
            bool primaryAdvanceDeferredThisFrame = false;

            if (primaryCycleChanged)
            {
                // Move old primary to overlap list if still alive
                if (primaryState != null)
                {
                    if (suppressOverlapGhosts)
                    {
                        DestroyGhost(index, traj, flags,
                            reason: "stationary high-warp overlap primary advanced");
                    }
                    else
                    {
                        ghostStates.Remove(index);
                        if (primaryState.pendingSpawnLifecycle != PendingSpawnLifecycle.None)
                        {
                            // Bug #450 B2: a primary that gets demoted to the overlap list before
                            // its split build completes must NOT later finalize as a brand-new
                            // overlap-primary enter. The old cycle should quietly finish as an
                            // overlap shell with no ghost-created / camera-retarget side effects.
                            primaryState.pendingSpawnLifecycle = PendingSpawnLifecycle.None;
                            primaryState.pendingSpawnFlags = default(TrajectoryPlaybackFlags);
                        }
                        if (primaryState.ghost != null)
                            GhostPlaybackLogic.MuteAllAudio(primaryState); // overlap ghosts get no audio
                        overlaps.Add(primaryState);
                        EmitLoopCycleTrace(index, traj, primaryState,
                            primaryState.loopCycleIndex, lastCycle, "overlap-demote");
                        ParsekLog.VerboseRateLimited("Engine", "overlap-move",
                            $"Ghost #{index} cycle={primaryState.loopCycleIndex} moved to overlap list (audio muted)");
                    }
                    primaryState = null;
                }

                bool retiredBeforePrimarySpawn = TryHandleParentAnchoredDebrisCoverageRetired(
                    index, traj, primaryState, primaryLoopUT, ctx.currentUT, ctx.warpRate,
                    "GhostPlaybackEngine.UpdateOverlapPlayback.primary-pre-spawn",
                    ShouldExitWatchForCoverageRetiredCycle(index, lastCycle, ctx),
                    out _);

                if (!retiredBeforePrimarySpawn)
                {
                    // Spawn new primary for lastCycle
                    primaryState = CreatePendingSpawnState(
                        traj, primaryLoopUT, PendingSpawnLifecycle.OverlapPrimaryEnter, flags);
                    primaryState.loopCycleIndex = lastCycle;
                    ghostStates[index] = primaryState;
                    GhostVisualLoadStatus overlapPrimarySpawnStatus = EnsureGhostVisualsLoaded(
                        index, traj, primaryState, primaryLoopUT, "overlap primary first spawn",
                        resetCompletedEventDedup: true);
                    if (overlapPrimarySpawnStatus == GhostVisualLoadStatus.Failed)
                    {
                        CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                        ghostStates.Remove(index);
                        ParsekLog.Warn("Engine",
                            $"Overlap: SpawnGhost failed for #{index} cycle={lastCycle}");
                        return;
                    }
                    QueueOrEmitGhostSpawnPending(index, traj, primaryState, flags);
                    primaryAdvanceDeferredThisFrame =
                        overlapPrimarySpawnStatus == GhostVisualLoadStatus.Pending;
                }
            }

            // Position primary ghost
            // Note: anchor-relative positioning is handled internally by positioner.PositionLoop,
            // which calls ShouldUseLoopAnchor itself. No need to pre-compute here.
            if (primaryState != null)
            {
                if (TryHandleParentAnchoredDebrisCoverageRetired(
                        index, traj, primaryState, primaryLoopUT, ctx.currentUT, ctx.warpRate,
                        "GhostPlaybackEngine.UpdateOverlapPlayback.primary-pre-zone",
                        ShouldExitWatchForCoverageRetiredState(index, primaryState, ctx),
                        out _))
                {
                    primaryState = null;
                }
            }

            if (primaryState != null)
            {
                bool primaryReady = true;
                double primaryDistance = ResolvePlaybackDistance(
                    index, traj, primaryState, primaryLoopUT, ctx.activeVesselPos);
                double primaryActiveVesselDistance = ResolvePlaybackActiveVesselDistance(
                    index, traj, primaryState, primaryLoopUT, ctx.activeVesselPos);
                CachePlaybackDistances(primaryState, primaryActiveVesselDistance, primaryDistance);
                var zoneResult = positioner.ApplyZoneRendering(
                    index, primaryState, traj, primaryDistance, primaryLoopUT, ctx.protectedIndex);
                if (zoneResult.hiddenByZone)
                {
                    GhostRenderTrace.EmitGuardSkip(
                        traj, index, ctx.currentUT,
                        "overlap-primary-hidden-by-zone distance=" + FormatPlaybackDistanceForLog(primaryDistance));
                    // Bug #450 B2: a newly-started overlap-primary build may already have
                    // consumed its one allowed timeline advance this frame above. Do not let
                    // hidden-tier prewarm immediately take a second advance through
                    // HandleHiddenGhostVisualState's EnsureGhostVisualsLoaded path.
                    if (!(primaryAdvanceDeferredThisFrame && !HasLoadedGhostVisuals(primaryState)))
                    {
                        HandleHiddenGhostVisualState(
                            index, traj, primaryState, primaryLoopUT, ctx.warpRate, primaryDistance, overlapGhost: false,
                            hiddenReason: $"overlap primary hidden by distance LOD at {FormatPlaybackDistanceForLog(primaryDistance)}");
                    }
                }
                else
                {
                    if (!HasLoadedGhostVisuals(primaryState))
                    {
                        if (primaryAdvanceDeferredThisFrame)
                            primaryReady = false;
                        else
                        {
                            // Bug #414: throttle overlap-primary distance-tier rehydration.
                            string overlapPrimaryReloadSite = primaryState.pendingSpawnLifecycle != PendingSpawnLifecycle.None
                                ? "overlap-primary-first-spawn-continue"
                                : "overlap-primary-rehydrate";
                            if (!TryReserveSpawnSlot(index, overlapPrimaryReloadSite))
                                primaryReady = false;
                            else
                            {
                                GhostVisualLoadStatus overlapPrimaryLoadStatus = EnsureGhostVisualsLoaded(
                                    index, traj, primaryState, primaryLoopUT,
                                    primaryState.pendingSpawnLifecycle != PendingSpawnLifecycle.None
                                        ? "continuing overlap primary first spawn"
                                        : "overlap primary re-entered visible distance tier");
                                if (overlapPrimaryLoadStatus != GhostVisualLoadStatus.CompletedThisCall
                                    && overlapPrimaryLoadStatus != GhostVisualLoadStatus.Ready)
                                {
                                    if (overlapPrimaryLoadStatus == GhostVisualLoadStatus.Failed)
                                        CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                                    primaryReady = false;
                                }
                            }
                        }
                    }

                    if (primaryReady)
                    {
                        GhostPlaybackLogic.ApplyDistanceLodFidelity(primaryState, zoneResult.reduceFidelity);
                        bool effectiveSuppressVisualFx = suppressVisualFx || zoneResult.suppressVisualFx;
                        // Bug #613 (PR #594 P1): clear retire signal before
                        // positioning; gate visuals/activation/appearance on
                        // it after.
                        primaryState.anchorRetiredThisFrame = false;
                        GhostRenderTrace.BeginFrame(
                            traj, index, ctx.currentUT, primaryLoopUT, "overlap-primary");
                        bool overlapPrimaryUsedBodyFixed = PositionLoopAtPlaybackUT(
                            index, traj, primaryState, primaryLoopUT, effectiveSuppressVisualFx,
                            "GhostPlaybackEngine.UpdateOverlapPlayback.primary");
                        bool primaryRetired = EmitPostPositionUpdate(
                            traj, index, ctx.currentUT, primaryLoopUT, primaryState, "overlap-primary",
                            overlapPrimaryUsedBodyFixed, rawPlaybackUT: primaryLoopUT);
                        if (primaryRetired)
                        {
                            ApplyFrameVisuals(index, traj, primaryState, primaryLoopUT, ctx.warpRate,
                                skipPartEvents: true, suppressVisualFx: true,
                                allowTransientEffects: false);
                        }
                        else
                        {
                            bool activatedDeferredState = ActivateGhostVisualsIfNeeded(primaryState);
                            ApplyFrameVisuals(index, traj, primaryState, primaryLoopUT, ctx.warpRate,
                                zoneResult.skipPartEvents, effectiveSuppressVisualFx);
                            if (ShouldRestoreDeferredRuntimeFxState(
                                    activatedDeferredState,
                                    effectiveSuppressVisualFx))
                                GhostPlaybackLogic.RestoreDeferredRuntimeFxState(primaryState);
                            TrackGhostAppearance(index, traj, primaryState, primaryLoopUT, "loop-primary");
                        }
                    }
                }
            }

            if (suppressOverlapGhosts)
                return;

            // Update overlap ghosts (older cycles)
            UpdateExpireAndPositionOverlaps(index, traj, flags, ctx, overlaps,
                duration, cycleDuration, playbackStartUT, scheduleStartUT, suppressVisualFx);
        }

        /// <summary>
        /// Iterates overlap ghosts (older cycles) in reverse. Expires cycles whose phase
        /// exceeds duration (triggers explosion + camera event), removes null entries,
        /// and positions remaining overlaps at their current loop UT.
        /// </summary>
        private void UpdateExpireAndPositionOverlaps(int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, FrameContext ctx,
            List<GhostPlaybackState> overlaps,
            double duration, double cycleDuration,
            double playbackStartUT, double scheduleStartUT,
            bool suppressVisualFx)
        {
            for (int i = overlaps.Count - 1; i >= 0; i--)
            {
                // Bug #460: count every overlap-ghost iteration (including null-entry
                // cleanup) before any continue / remove, so the mainLoop breakdown WARN's
                // `overlapGhostIterationCount` reflects the full overlap dispatch work
                // that `mainLoopMicroseconds` covers. Matters for distinguishing
                // "slow per-trajectory dispatch" from "many overlap ghosts per trajectory"
                // on the next post-#460 playtest spike.
                frameOverlapGhostIterationCount++;

                var ovState = overlaps[i];
                if (ovState == null)
                {
                    overlaps.RemoveAt(i);
                    continue;
                }

                long cycle = ovState.loopCycleIndex;
                double phase = ctx.currentUT - (scheduleStartUT + cycle * cycleDuration);

                // Expired cycle
                if (phase > duration)
                {
                    // Bug #613 (PR #594 P1 round 2): clear retire signal so
                    // PositionGhostAtLoopEndpoint -> PositionLoop -> relative
                    // retire branch can flag this overlap expiry. Without the
                    // gate, an unresolvable LoopAnchor pid would let the
                    // explosion + ExplosionHold camera event + OverlapExpired
                    // payload fire from a stale (0,0,0) transform.
                    bool overlapExpiryRetired = false;
                    if (ovState.ghost != null)
                    {
                        ovState.anchorRetiredThisFrame = false;
                        PositionGhostAtLoopEndpoint(
                            index, traj, flags, ovState, ctx.currentUT, ctx.warpRate);
                        overlapExpiryRetired = RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                            ovState.anchorRetiredThisFrame);
                    }
                    bool triggerExplosionAtExpiry = !overlapExpiryRetired
                        && !ovState.explosionFired
                        && GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(
                            traj, ResolveLoopPlaybackEndpointUT(traj));
                    if (overlapExpiryRetired)
                    {
                        ParsekLog.VerboseRateLimited("Engine", $"overlap-expiry-suppressed-{index}",
                            $"loop endpoint side effects suppressed: anchor retired ghost #{index} \"{traj.VesselName}\" overlap-cycle={cycle}");
                    }
                    if (triggerExplosionAtExpiry)
                    {
                        TriggerExplosionIfDestroyed(ovState, traj, index, ctx.warpRate);
                    }

                    // Fire camera event for overlap expiry
                    if (!overlapExpiryRetired)
                    {
                        OnOverlapCameraAction?.Invoke(new CameraActionEvent
                        {
                            Index = index,
                            Action = triggerExplosionAtExpiry
                                ? CameraActionType.ExplosionHoldStart
                                : CameraActionType.ExplosionHoldEnd,
                            NewCycleIndex = cycle,
                            AnchorPosition = ovState.ghost != null ? ovState.ghost.transform.position : Vector3.zero,
                            HoldUntilUT = ctx.currentUT + GhostPlayback.OverlapExplosionHoldSeconds,
                            Trajectory = traj, Flags = flags
                        });

                        // Fire overlap expired event
                        OnOverlapExpired?.Invoke(new OverlapExpiredEvent
                        {
                            Index = index, Trajectory = traj, State = ovState, Flags = flags,
                            CycleIndex = cycle,
                            ExplosionFired = triggerExplosionAtExpiry,
                            ExplosionPosition = ovState.ghost != null ? ovState.ghost.transform.position : Vector3.zero
                        });
                    }

                    ParsekLog.VerboseRateLimited("Engine", "overlap-expired",
                        $"Ghost EXITED range: #{index} \"{traj.VesselName}\" cycle={cycle} (overlap expired)");
                    DestroyOverlapGhostState(ovState, index, traj, "overlap expired");
                    overlaps.RemoveAt(i);
                    continue;
                }

                phase = Math.Max(0, Math.Min(phase, duration));
                double loopUT = playbackStartUT + phase;
                if (TryHandleParentAnchoredDebrisCoverageRetired(
                        index, traj, ovState, loopUT, ctx.currentUT, ctx.warpRate,
                        "GhostPlaybackEngine.UpdateExpireAndPositionOverlaps.pre-zone",
                        ShouldExitWatchForCoverageRetiredState(index, ovState, ctx),
                        out _))
                    continue;

                double overlapDistance = ResolvePlaybackDistance(
                    index, traj, ovState, loopUT, ctx.activeVesselPos);
                double overlapActiveVesselDistance = ResolvePlaybackActiveVesselDistance(
                    index, traj, ovState, loopUT, ctx.activeVesselPos);
                CachePlaybackDistances(ovState, overlapActiveVesselDistance, overlapDistance);
                var zoneResult = positioner.ApplyZoneRendering(
                    index, ovState, traj, overlapDistance, loopUT, ctx.protectedIndex);
                if (zoneResult.hiddenByZone)
                {
                    GhostRenderTrace.EmitGuardSkip(
                        traj, index, ctx.currentUT,
                        "overlap-hidden-by-zone distance=" + FormatPlaybackDistanceForLog(overlapDistance)
                            + " cycle=" + cycle.ToString(CultureInfo.InvariantCulture));
                    HandleHiddenGhostVisualState(
                        index, traj, ovState, loopUT, ctx.warpRate, overlapDistance, overlapGhost: true,
                        hiddenReason: $"overlap hidden by distance LOD at {FormatPlaybackDistanceForLog(overlapDistance)}");
                    continue;
                }

                if (!HasLoadedGhostVisuals(ovState))
                {
                    // Bug #414: throttle loop-overlap distance-tier rehydration.
                    if (!TryReserveSpawnSlot(index, "loop-overlap-rehydrate"))
                        continue;
                    GhostVisualLoadStatus overlapLoadStatus = EnsureGhostVisualsLoaded(
                        index, traj, ovState, loopUT, "overlap re-entered visible distance tier");
                    if (overlapLoadStatus == GhostVisualLoadStatus.Failed
                        || overlapLoadStatus == GhostVisualLoadStatus.Pending)
                        continue;
                }

                GhostPlaybackLogic.ApplyDistanceLodFidelity(ovState, zoneResult.reduceFidelity);
                bool effectiveSuppressVisualFx = suppressVisualFx || zoneResult.suppressVisualFx;
                // Bug #613 (PR #594 P1): clear retire signal before
                // positioning; gate visuals/activation/appearance on it after.
                ovState.anchorRetiredThisFrame = false;
                GhostRenderTrace.BeginFrame(
                    traj, index, ctx.currentUT, loopUT,
                    "loop-overlap cycle=" + cycle.ToString(CultureInfo.InvariantCulture));
                bool overlapLoopUsedBodyFixed = PositionLoopAtPlaybackUT(
                    index, traj, ovState, loopUT,
                    effectiveSuppressVisualFx,
                    "GhostPlaybackEngine.UpdateExpireAndPositionOverlaps");
                bool overlapRetired = EmitPostPositionUpdate(
                    traj, index, ctx.currentUT, loopUT, ovState,
                    "loop-overlap cycle=" + cycle.ToString(CultureInfo.InvariantCulture),
                    overlapLoopUsedBodyFixed, rawPlaybackUT: loopUT);
                if (overlapRetired)
                {
                    ApplyFrameVisuals(index, traj, ovState, loopUT, ctx.warpRate,
                        skipPartEvents: true, suppressVisualFx: true,
                        allowTransientEffects: false);
                }
                else
                {
                    bool activatedDeferredState = ActivateGhostVisualsIfNeeded(ovState);
                    ApplyFrameVisuals(index, traj, ovState, loopUT, ctx.warpRate,
                        zoneResult.skipPartEvents, effectiveSuppressVisualFx);
                    if (ShouldRestoreDeferredRuntimeFxState(
                            activatedDeferredState,
                            effectiveSuppressVisualFx))
                        GhostPlaybackLogic.RestoreDeferredRuntimeFxState(ovState);
                    TrackGhostAppearance(index, traj, ovState, loopUT, "loop-overlap");
                }
            }
        }

        /// <summary>
        /// Handles the loop pause window: positions ghost at the final loop point,
        /// hides all parts (crash-site hold), zeroes velocity for reentry FX decay,
        /// and triggers explosion if the recording ended in destruction.
        /// </summary>
        private void HandleLoopPauseWindow(int index, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags, GhostPlaybackState state, double frameUT, float warpRate)
        {
            // Bug #613 (PR #594 P1): clear retire signal before positioning;
            // gate visuals/activation/appearance on it after. The loop-pause
            // window calls PositionGhostAtLoopEndpoint -> positioner.PositionLoop,
            // which routes through the relative-frame retire branch when
            // traj.LoopAnchorVesselId is unresolvable (same Re-Fly rewind
            // failure mode covered by the per-frame gate above).
            state.anchorRetiredThisFrame = false;
            PositionGhostAtLoopEndpoint(index, traj, flags, state, frameUT, warpRate);
            bool loopPauseRetired = RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                state.anchorRetiredThisFrame);
            // PositionAtPoint now sets state.lastInterpolatedAltitude to the
            // clamped altitude (#282). Don't overwrite it here with the raw
            // recording-end altitude on the first pause-window frame, or the
            // watch overlay reports the buried recorded value for that one frame.
            // Still seed the body name (PositionAtPoint doesn't touch it).
            if (string.IsNullOrEmpty(state.lastInterpolatedBodyName))
            {
                state.lastInterpolatedBodyName = traj?.Points != null && traj.Points.Count > 0
                    ? traj.Points[traj.Points.Count - 1].bodyName
                    : null;
            }
            // Bug #613 (PR #594 P1 round 2): when retired, the explosion side
            // effect would fire from the stale (0,0,0) loop-endpoint transform.
            // Skip TriggerExplosionIfDestroyed; if the recording's loop endpoint
            // really was a destruction, the next replay with a resolvable
            // anchor will produce the explosion at the real position.
            if (!loopPauseRetired
                && GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(
                    traj, ResolveLoopPlaybackEndpointUT(traj)))
            {
                TriggerExplosionIfDestroyed(state, traj, index, warpRate);
            }
            else if (loopPauseRetired)
            {
                ParsekLog.VerboseRateLimited("Engine", $"loop-pause-suppressed-{index}",
                    $"loop endpoint side effects suppressed: anchor retired ghost #{index} \"{traj?.VesselName}\" loop-pause");
            }
            if (!state.pauseHidden)
            {
                state.pauseHidden = true;
                GhostPlaybackLogic.HideAllGhostParts(state);
            }
            // Bug #450 B3: widened guard — pending-but-not-yet-built ghosts also need
            // UpdateReentryFx to run so the lazy build can fire if the loop-pause frame
            // happens to be the first one in atmosphere.
            if (state.reentryFxInfo != null || state.reentryFxPendingBuild)
            {
                state.ClearInterpolatedVelocities();
                UpdateReentryFx(index, state, traj.VesselName, warpRate);
            }
            // Bug #613 (PR #594 P1): if the loop-endpoint position landed in a
            // relative section whose anchor pid is unresolvable, the retire
            // branch already called SetActive(false). Skip the
            // ActivateGhostVisualsIfNeeded + TrackGhostAppearance pair here so
            // the same-frame reactivation race can't undo the hide.
            //
            // P3 (round 2): HideAllGhostParts above only calls MuteAllAudio —
            // any previously-emitting engine plumes / RCS / reentry FX
            // continue rendering at the (0,0,0) retired position. The other
            // five PR #594 visibility gates already call
            // ApplyFrameVisuals(suppressVisualFx:true) for FX teardown; do
            // the same here so the loop-pause window matches that contract.
            if (loopPauseRetired)
            {
                ApplyFrameVisuals(index, traj, state, ResolveLoopPlaybackEndpointUT(traj), warpRate,
                    skipPartEvents: true, suppressVisualFx: true,
                    allowTransientEffects: false);
                return;
            }
            ActivateGhostVisualsIfNeeded(state);
            double appearanceUT = ResolveLoopPlaybackEndpointUT(traj);
            TrackGhostAppearance(index, traj, state, appearanceUT, "loop-pause");
        }

        private void PositionGhostAtLoopEndpoint(
            int index,
            IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags flags,
            GhostPlaybackState state,
            double frameUT,
            float warpRate)
        {
            if (state?.ghost == null || traj == null || positioner == null)
                return;

            PositionLoopAtPlaybackUT(
                index, traj, state, ResolveLoopPlaybackEndpointUT(traj), true,
                "GhostPlaybackEngine.PositionGhostAtLoopEndpoint");
        }

        private bool PositionLoopAtPlaybackUT(
            int index,
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double loopUT,
            bool suppressFx,
            string callsite)
        {
            // Parent-anchored gate is ParentAnchorRecordingId != null ALONE (shared with the
            // non-loop path via IsParentAnchoredBodyFixedPrimaryCandidate). Both genuine debris and
            // controlled-decoupled children (the continuing upper stage of a staged rocket, etc.)
            // render through their own bodyFixedFrames first. ShouldUseLoopAnchoredDebrisChain still
            // requires IsDebris, so a controlled child is NOT a loop-anchored chain: it takes the
            // body-fixed-primary-first branch below, exactly like the non-loop path.
            bool parentAnchored = IsParentAnchoredBodyFixedPrimaryCandidate(traj);
            bool loopAnchoredDebrisChain = parentAnchored
                && ShouldUseLoopAnchoredDebrisChain(traj, loopUT);
            if (!loopAnchoredDebrisChain
                && TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                    index, traj, state, loopUT, callsite))
            {
                return false;
            }

            // The body-fixed-primary route only applies while the ACTIVE section at loopUT is
            // Relative (its bodyFixedFrames are the recorded fallback for the relative anchor). A
            // parent-anchored controlled-decoupled child (probe, lander, separated stage) also
            // records ABSOLUTE tail sections after it leaves its parent's bubble; those have no
            // bodyFixedFrames and must play through the normal absolute loop path below. Without this
            // gate such a child whose CURRENT section is Absolute (e.g. a probe that decoupled and
            // flew off on its own) is forced into TryPositionBodyFixedPrimary, which fails for a
            // non-Relative section, and is retired every frame -> never visible / not watchable. The
            // non-loop path already bails out the same way via TryGetRelativeSectionAtUT.
            bool activeSectionIsRelative =
                TryGetRelativeSectionAtUT(traj, loopUT, out RelativeSectionPlaybackTarget _);
            if (parentAnchored
                && !loopAnchoredDebrisChain
                && activeSectionIsRelative
                && TryPositionBodyFixedPrimary(index, traj, state, loopUT, callsite))
            {
                return true;
            }
            if (parentAnchored && !loopAnchoredDebrisChain && activeSectionIsRelative)
            {
                var diagnostic =
                    DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic.Create(
                        "body-fixed-primary-position-failed");
                MarkParentAnchoredDebrisCoverageRetired(
                    index,
                    traj,
                    state,
                    loopUT,
                    callsite,
                    diagnostic);
                return false;
            }

            if (loopAnchoredDebrisChain
                && TryGetRelativeSectionAtUT(traj, loopUT, out RelativeSectionPlaybackTarget target))
            {
                if (!DebrisRelativePlaybackPolicy.RelativeFramesCoverUT(
                        traj,
                        target.Section,
                        loopUT,
                        DebrisRelativeCoverageMode.RecorderPersistable))
                {
                    if (TryPositionBodyFixedPrimary(
                            index,
                            traj,
                            state,
                            loopUT,
                            callsite + ".loop-chain-relative-uncovered"))
                    {
                        return true;
                    }

                    MarkParentAnchoredDebrisCoverageRetired(
                        index,
                        traj,
                        state,
                        loopUT,
                        callsite,
                        DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic.Create(
                            "loop-chain-relative-frames-unavailable"));
                    return false;
                }

                positioner.InterpolateAndPositionRelative(
                    index, traj, state, loopUT, suppressFx, target);
            }
            else
            {
                positioner.PositionLoop(index, traj, state, loopUT, suppressFx);
            }
            // Body-fixed fallback when the relative anchor could not resolve this frame
            // (anchorRetiredThisFrame). This covers BOTH parent-anchored loop-chain debris AND a
            // top-level vessel whose ACTIVE section is Relative against a SIBLING that ended early:
            // e.g. the continuing upper stage ("Kerbal X") recorded a stretch relative to the probe
            // that decoupled and crashed ~0.1 s later, so its recorded section anchor runs out long
            // before the section does and PositionLoop retires it. TryPositionBodyFixedPrimary is
            // self-gating (it only succeeds when the active section is Relative AND its
            // bodyFixedFrames cover the UT), so this never fires for absolute sections or sections
            // without a body-fixed surface, and the ghost plays its own recorded body-fixed track
            // instead of vanishing for most of its window.
            if (state != null
                && state.anchorRetiredThisFrame
                && TryPositionBodyFixedPrimary(index, traj, state, loopUT, callsite + ".fallback"))
            {
                return true;
            }

            return false;
        }

        #region Loop utilities

        private void RebuildAutoLoopLaunchScheduleCache(
            IReadOnlyList<IPlaybackTrajectory> trajectories,
            double autoLoopIntervalSeconds)
        {
            autoLoopLaunchSchedules.Clear();
            autoLoopQueueScratch.Clear();
            if (trajectories == null || trajectories.Count == 0)
                return;

            for (int i = 0; i < trajectories.Count; i++)
            {
                var traj = trajectories[i];
                if (!GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue(traj))
                    continue;

                // Mission loop unit members are scheduled by their unit's span clock, not the flat
                // global stagger, so they must never receive a staggered global slot. Excluding
                // them here is the single global-queue exclusion point; the span-clock render
                // dispatch (Phase D2) takes over their playback. Keyed on unit membership,
                // independent of the LoopPlayback flag.
                if (currentLoopUnits.OwnerByIndex.TryGetValue(i, out int unitOwner))
                {
                    ParsekLog.VerboseOnChange(
                        "Loop",
                        "unit-loop-exclude-" + i.ToString(CultureInfo.InvariantCulture),
                        unitOwner.ToString(CultureInfo.InvariantCulture),
                        "unit-loop member recIdx=" + i.ToString(CultureInfo.InvariantCulture)
                            + " excluded from global auto queue (unit owner="
                            + unitOwner.ToString(CultureInfo.InvariantCulture) + ")");
                    continue;
                }

                autoLoopQueueScratch.Add(new AutoLoopQueueCandidate(
                    i,
                    EffectiveLoopStartUT(traj),
                    EffectiveLoopEndUT(traj),
                    traj.RecordingId));
            }

            if (autoLoopQueueScratch.Count == 0)
                return;

            autoLoopQueueScratch.Sort(CompareAutoLoopQueueCandidates);
            double launchGapSeconds = GhostPlaybackLogic.ResolveLoopInterval(
                trajectories[autoLoopQueueScratch[0].RecordingIndex],
                autoLoopIntervalSeconds,
                LoopTiming.DefaultLoopIntervalSeconds,
                LoopTiming.MinCycleDuration);
            double anchorUT = autoLoopQueueScratch[0].PlaybackStartUT;
            double cadenceSeconds = launchGapSeconds * autoLoopQueueScratch.Count;
            ParsekLog.Verbose("Engine",
                $"Auto loop queue rebuilt: count={autoLoopQueueScratch.Count} " +
                $"anchorUT={anchorUT.ToString("R", CultureInfo.InvariantCulture)} " +
                $"cadence={cadenceSeconds.ToString("R", CultureInfo.InvariantCulture)}s");
            for (int slot = 0; slot < autoLoopQueueScratch.Count; slot++)
            {
                var candidate = autoLoopQueueScratch[slot];
                autoLoopLaunchSchedules[candidate.RecordingIndex] =
                    new GhostPlaybackLogic.AutoLoopLaunchSchedule(
                        anchorUT + (slot * launchGapSeconds),
                        cadenceSeconds,
                        slot,
                        autoLoopQueueScratch.Count);
            }
        }

        private bool TryResolveLoopSchedule(
            IPlaybackTrajectory traj,
            double autoLoopIntervalSeconds,
            int recIdx,
            out double playbackStartUT,
            out double scheduleStartUT,
            out double duration,
            out double intervalSeconds)
        {
            playbackStartUT = 0.0;
            scheduleStartUT = 0.0;
            duration = 0.0;
            intervalSeconds = LoopTiming.DefaultLoopIntervalSeconds;
            if (traj == null || traj.Points == null || traj.Points.Count < 2)
                return false;

            playbackStartUT = EffectiveLoopStartUT(traj);
            double playbackEndUT = EffectiveLoopEndUT(traj);
            duration = playbackEndUT - playbackStartUT;
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            double baseIntervalSeconds = GhostPlaybackLogic.ResolveLoopInterval(
                traj, autoLoopIntervalSeconds,
                LoopTiming.DefaultLoopIntervalSeconds,
                LoopTiming.MinCycleDuration);
            if (recIdx >= 0 && autoLoopLaunchSchedules.TryGetValue(recIdx, out var autoSchedule))
            {
                scheduleStartUT = autoSchedule.LaunchStartUT;
                intervalSeconds = autoSchedule.LaunchCadenceSeconds;
                return true;
            }

            scheduleStartUT = playbackStartUT;
            intervalSeconds = baseIntervalSeconds;
            return true;
        }

        /// <summary>Resolve the effective loop interval for a trajectory.</summary>
        internal double GetLoopIntervalSeconds(IPlaybackTrajectory traj, double autoLoopIntervalSeconds)
        {
            return GhostPlaybackLogic.ResolveLoopInterval(
                traj, autoLoopIntervalSeconds,
                LoopTiming.DefaultLoopIntervalSeconds,
                LoopTiming.MinCycleDuration);
        }

        internal double GetLoopIntervalSeconds(
            IPlaybackTrajectory traj,
            double autoLoopIntervalSeconds,
            int recIdx)
        {
            if (recIdx >= 0 && autoLoopLaunchSchedules.TryGetValue(recIdx, out var autoSchedule))
                return autoSchedule.LaunchCadenceSeconds;

            return GetLoopIntervalSeconds(traj, autoLoopIntervalSeconds);
        }

        /// <summary>
        /// Compute the effective UT within a looping trajectory, accounting for cycle index,
        /// pause windows, and loop phase offsets. Returns false if the trajectory is not loopable.
        /// </summary>
        internal bool TryComputeLoopPlaybackUT(
            IPlaybackTrajectory traj,
            double currentUT,
            double autoLoopIntervalSeconds,
            out double loopUT,
            out long cycleIndex,
            out bool inPauseWindow,
            int recIdx = -1)
        {
            cycleIndex = 0;
            inPauseWindow = false;
            loopUT = 0;
            if (!TryResolveLoopSchedule(
                    traj, autoLoopIntervalSeconds, recIdx,
                    out double playbackStartUT,
                    out double scheduleStartUT,
                    out double duration,
                    out double intervalSeconds))
                return false;

            // Apply loop phase offset (set by Watch mode to reset ghost to recording start)
            double phaseOffset;
            if (recIdx >= 0 && loopPhaseOffsets.TryGetValue(recIdx, out phaseOffset))
            {
                ParsekLog.Verbose("Engine", "TryComputeLoopPlaybackUT: applying phase offset " +
                    phaseOffset.ToString("F2", CultureInfo.InvariantCulture) + "s for recIdx=" +
                    recIdx.ToString(CultureInfo.InvariantCulture));
                scheduleStartUT -= phaseOffset;
            }

            if (!GhostPlaybackLogic.TryComputeLoopPlaybackPhase(
                    currentUT, scheduleStartUT, duration, intervalSeconds,
                    out double playbackPhase, out cycleIndex, out inPauseWindow))
            {
                return false;
            }

            loopUT = playbackStartUT + playbackPhase;
            if (inPauseWindow && ParsekLog.IsVerboseEnabled)
            {
                ParsekLog.VerboseRateLimited("Engine", "loop_pause_" + recIdx,
                    $"TryComputeLoopPlaybackUT: in pause window for recIdx={recIdx}, cycle={cycleIndex}");
            }
            return true;
        }

        /// <summary>Whether any time warp is active (using FrameContext values, no KSP globals).</summary>
        internal static bool IsAnyWarpActive(FrameContext ctx)
        {
            return GhostPlaybackLogic.IsAnyWarpActive(ctx.warpRateIndex, ctx.warpRate);
        }

        private bool IsWatchedGhostState(int recordingIndex, GhostPlaybackState state, FrameContext ctx)
        {
            if (state == null)
                return false;

            if (IsWatchedGhostStateResolver != null)
                return IsWatchedGhostStateResolver(recordingIndex, state);

            return GhostPlaybackLogic.IsProtectedGhost(
                ctx.protectedIndex, ctx.protectedLoopCycleIndex,
                recordingIndex, state.loopCycleIndex);
        }

        private double ResolvePlaybackDistance(
            int recordingIndex, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, Vector3d activeVesselPos)
        {
            if (ResolvePlaybackDistanceOverride != null)
            {
                double resolved = ResolvePlaybackDistanceOverride(
                    recordingIndex, traj, state, playbackUT);
                if (!double.IsNaN(resolved) && !double.IsInfinity(resolved) && resolved >= 0)
                    return resolved;
            }

            if (state != null && state.ghost != null)
                return Vector3d.Distance((Vector3d)state.ghost.transform.position, activeVesselPos);

            return double.MaxValue;
        }

        private double ResolvePlaybackActiveVesselDistance(
            int recordingIndex, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, Vector3d activeVesselPos)
        {
            if (ResolvePlaybackActiveVesselDistanceOverride != null)
            {
                double resolved = ResolvePlaybackActiveVesselDistanceOverride(
                    recordingIndex, traj, state, playbackUT);
                if (!double.IsNaN(resolved) && !double.IsInfinity(resolved) && resolved >= 0)
                    return resolved;
            }

            if (state != null && state.ghost != null)
                return Vector3d.Distance((Vector3d)state.ghost.transform.position, activeVesselPos);

            return double.MaxValue;
        }

        internal static void CachePlaybackDistances(
            GhostPlaybackState state, double activeVesselDistance, double renderDistance)
        {
            if (state == null)
                return;

            state.lastDistance = activeVesselDistance;
            state.lastRenderDistance = renderDistance;
        }

        internal static string FormatPlaybackDistanceForLog(double distanceMeters)
        {
            return RenderingZoneManager.FormatDistanceForLog(distanceMeters);
        }

        // Diagnostic helpers for the anchor-distance teardown tracer. Compact world-position
        // strings so a teardown log can be cross-checked for a phantom-distance seam (ghost and
        // anchor look close but the distance metric reads huge).
        private static string FormatGhostWorldPosForLog(GhostPlaybackState state)
        {
            return state != null && state.ghost != null
                ? GhostPlaybackLogic.FormatVector3Invariant(state.ghost.transform.position)
                : "(none)";
        }

        private static string FormatActiveVesselWorldPosForLog()
        {
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            return activeVessel != null && activeVessel.transform != null
                ? GhostPlaybackLogic.FormatVector3Invariant(activeVessel.transform.position)
                : "(none)";
        }

        /// <summary>Whether any time warp is active (reads KSP globals directly — host convenience wrapper).</summary>
        internal static bool IsAnyWarpActiveFromGlobals()
        {
            return GhostPlaybackLogic.IsAnyWarpActive(TimeWarp.CurrentRateIndex, TimeWarp.CurrentRate);
        }

        #endregion
    }
}
