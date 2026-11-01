using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Past-end completion: completed events, early-destroyed debris, stale past-end cleanup and the recording-endpoint hold.
    internal partial class GhostPlaybackEngine
    {
        private bool TryHandleEarlyDestroyedDebrisCompletion(
            int index, IPlaybackTrajectory traj, TrajectoryPlaybackFlags flags,
            FrameContext ctx, GhostPlaybackState state, bool ghostActive, bool hasPointData)
        {
            if (completedEventFired.Contains(index))
                return true;

            if (!GhostPlaybackLogic.TryGetEarlyDestroyedDebrisExplosionUT(traj, out double explosionUT))
                return false;

            if (ctx.currentUT + 1e-6 < explosionUT)
                return false;

            completedEventFired.Add(index);
            earlyDestroyedDebrisCompleted.Add(index);

            if (ghostActive)
                TriggerExplosionIfDestroyed(state, traj, index, ctx.warpRate);

            deferredCompletedEvents.Add(new PlaybackCompletedEvent
            {
                Index = index,
                Trajectory = traj,
                State = state,
                Flags = flags,
                GhostWasActive = ghostActive,
                PastEffectiveEnd = ctx.currentUT > flags.chainEndUT,
                LastPoint = hasPointData ? traj.Points[traj.Points.Count - 1] : default,
                CurrentUT = ctx.currentUT
            });

            ParsekLog.Info("Engine",
                $"Early debris completion: ghost #{index} \"{traj.VesselName}\" " +
                $"explosionUT={explosionUT:F2} currentUT={ctx.currentUT:F2} endUT={traj.EndUT:F2}");
            return true;
        }

        /// <summary>
        /// Loop first-run spawn: when a looping index's real playhead crosses the recording's
        /// own end (and its chain's effective end, where the policy's spawn is gated) and the
        /// ordinary spawn gate allows it (<c>needsSpawn</c>, which already folds VesselSpawned,
        /// the replay-scope history gate and the chain rules), queue ONE spawn-only completion.
        /// The event carries no ghost state (GhostWasActive=false, the hidden past-end
        /// completion's shape) and is marked <c>LoopFirstRun</c>, so the policy spawns the
        /// vessel and leaves the cycling loop ghost and any watch session alone. A mid-chain
        /// segment is left to its chain tip. The one-shot latch re-arms while the playhead is
        /// before the recording (a rewind). Runs for every index; a non-looping index returns
        /// false and keeps the ordinary past-end completion. See
        /// <see cref="GhostPlaybackLogic.ShouldAttemptLoopFirstRunSpawn"/>.
        /// </summary>
        internal bool TryQueueLoopFirstRunSpawn(int i, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags f, FrameContext ctx, double activationStartUT, bool hasPointData)
        {
            if (ctx.currentUT < activationStartUT)
            {
                loopFirstRunSpawnFired.Remove(i);
                return false;
            }

            bool loopDriven = currentLoopUnits.IsMember(i) || ShouldLoopPlayback(traj);
            if (!GhostPlaybackLogic.ShouldAttemptLoopFirstRunSpawn(
                    loopDriven: loopDriven,
                    spawnEligible: f.needsSpawn && !f.isMidChain,
                    currentUT: ctx.currentUT,
                    recordingEndUT: Math.Max(traj.EndUT, f.chainEndUT),
                    alreadyAttempted: loopFirstRunSpawnFired.Contains(i)))
                return false;

            loopFirstRunSpawnFired.Add(i);
            ParsekLog.Info("Engine",
                "Loop first-run spawn: #" + i.ToString(CultureInfo.InvariantCulture)
                + " \"" + (traj.VesselName ?? "?") + "\" id=" + (f.recordingId ?? "(none)")
                + " UT=" + ctx.currentUT.ToString("F2", CultureInfo.InvariantCulture)
                + " endUT=" + traj.EndUT.ToString("F2", CultureInfo.InvariantCulture)
                + " (the first run of a looping recording is real; later cycles stay ghost-only)");
            deferredCompletedEvents.Add(new PlaybackCompletedEvent
            {
                Index = i,
                Trajectory = traj,
                State = null,
                Flags = f,
                GhostWasActive = false,
                PastEffectiveEnd = ctx.currentUT > f.chainEndUT,
                LastPoint = hasPointData ? traj.Points[traj.Points.Count - 1] : default,
                CurrentUT = ctx.currentUT,
                LoopFirstRun = true
            });
            return true;
        }

        /// <summary>
        /// Handles past-end ghost: positions at final point, triggers explosion if destroyed,
        /// fires completed event. Works for both active and inactive ghost cases.
        /// </summary>
        private void HandlePastEndGhost(int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f,
            FrameContext ctx, GhostPlaybackState state, bool ghostActive, bool hasPointData)
        {
            if (ghostActive)
            {
                System.Diagnostics.Debug.Assert(
                    state != null,
                    "ghostActive implies an existing GhostPlaybackState");

                // Bug #613 follow-up: most endpoint retirement is frame-local.
                // A RELATIVE anchor can resolve again through the recorded
                // fallback path on a later frame, so keep the ghost alive and
                // retry instead of marking completion or destroying state.
                // Parent-anchored debris outside endpoint Relative coverage is
                // deterministic; that branch completes below after suppressing
                // stale endpoint side effects.
                state.anchorRetiredThisFrame = false;

                // Position ghost at the true recording endpoint.
                PositionGhostAtRecordingEndpoint(i, traj, state);

                bool endpointRetired = RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                    state.anchorRetiredThisFrame);
                if (endpointRetired)
                {
                    double endpointCoverageUT = ResolveRecordingEndpointCoverageUT(traj);
                    // endpointCoverageUT may be after the visible endpoint sample for
                    // orbit/checkpoint tails; suppressing events/FX makes this a
                    // teardown-only visual pass.
                    ApplyFrameVisuals(i, traj, state, endpointCoverageUT,
                        ctx.warpRate, skipPartEvents: true, suppressVisualFx: true,
                        allowTransientEffects: false);
                    if (ShouldCompleteParentAnchoredDebrisEndpointCoverageMiss(
                            traj, endpointCoverageUT))
                    {
                        ParsekLog.VerboseRateLimited("Engine", $"past-end-coverage-retired-{i}",
                            $"past-end completion finalized: parent-anchored debris outside relative coverage #{i} \"{traj?.VesselName}\"");
                        DestroyGhost(i, traj, f,
                            reason: "parent-anchored debris outside relative coverage at endpoint");
                        completedEventFired.Add(i);
                        deferredCompletedEvents.Add(new PlaybackCompletedEvent
                        {
                            Index = i,
                            Trajectory = traj,
                            State = state,
                            Flags = f,
                            GhostWasActive = false,
                            PastEffectiveEnd = ctx.currentUT > f.chainEndUT,
                            LastPoint = hasPointData ? traj.Points[traj.Points.Count - 1] : default,
                            CurrentUT = ctx.currentUT
                        });
                        return;
                    }

                    ParsekLog.VerboseRateLimited("Engine", $"past-end-suppressed-{i}",
                        $"past-end completion suppressed: anchor retired ghost #{i} \"{traj?.VesselName}\"");
                    return;
                }

                // Trigger explosion if destroyed
                TriggerExplosionIfDestroyed(state, traj, i, ctx.warpRate);
            }

            QueuePastEndCompletedEvent(i, traj, f, ctx.currentUT, state, ghostActive, hasPointData);
        }

        /// <summary>
        /// Marks the slot completed and queues its completed event (policy handles
        /// spawn/resources/camera). The ghost stays alive; the policy decides when to destroy
        /// it (may hold for a blocked spawn or the watch camera, or destroy immediately).
        /// </summary>
        private void QueuePastEndCompletedEvent(int i, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags f, double currentUT, GhostPlaybackState state,
            bool ghostActive, bool hasPointData)
        {
            completedEventFired.Add(i);
            deferredCompletedEvents.Add(new PlaybackCompletedEvent
            {
                Index = i,
                Trajectory = traj,
                State = state,
                Flags = f,
                GhostWasActive = ghostActive,
                PastEffectiveEnd = currentUT > f.chainEndUT,
                LastPoint = hasPointData ? traj.Points[traj.Points.Count - 1] : default,
                CurrentUT = currentUT
            });
        }

        /// <summary>
        /// Outcome of the per-slot stale past-end cleanup check. See
        /// <see cref="DecideStalePastEndCleanup"/>.
        /// </summary>
        internal enum StalePastEndCleanupDecision
        {
            /// <summary>No ghost state, or the slot has not completed: nothing to clean.</summary>
            NotApplicable,
            /// <summary>The policy is holding the ghost (pending spawn, watched): keep it.</summary>
            KeepHeld,
            /// <summary>The completion event has not reached the policy yet: decide after delivery.</summary>
            DeferUntilCompletionDelivered,
            /// <summary>Completion delivered and the policy chose not to hold: clean up.</summary>
            Destroy,
        }

        /// <summary>
        /// Pure stale past-end cleanup decision. The held check comes first so a slot the
        /// policy already holds keeps its existing behaviour; a non-held slot whose completion
        /// event is still queued is deferred, because the policy (which may hold the ghost for a
        /// blocked spawn, design 13.5) has not received that event yet.
        /// </summary>
        internal static StalePastEndCleanupDecision DecideStalePastEndCleanup(
            bool hasState, bool completionFired, bool ghostHeld, bool completionPendingDelivery)
        {
            if (!hasState || !completionFired)
                return StalePastEndCleanupDecision.NotApplicable;
            if (ghostHeld)
                return StalePastEndCleanupDecision.KeepHeld;
            if (completionPendingDelivery)
                return StalePastEndCleanupDecision.DeferUntilCompletionDelivered;
            return StalePastEndCleanupDecision.Destroy;
        }

        /// <summary>
        /// The per-trajectory loop's stale past-end cleanup step for one slot: keep a held
        /// ghost, postpone a slot whose completion is still queued (see
        /// <see cref="RunStalePastEndCleanupsAfterCompletionDelivery"/>), otherwise clean up.
        /// </summary>
        private void ApplyStalePastEndCleanupStep(int i, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags f, double currentUT, GhostPlaybackState state,
            int chainNextIndex, bool continuationHasActiveGhost)
        {
            if (state == null || !completedEventFired.Contains(i))
                return;

            PlaybackCompletedEvent pendingCompletion = FindPendingCompletedEvent(i);
            StalePastEndCleanupDecision staleDecision = DecideStalePastEndCleanup(
                hasState: true,
                completionFired: true,
                ghostHeld: IsGhostHeld != null && IsGhostHeld(i),
                completionPendingDelivery: pendingCompletion != null);
            if (staleDecision == StalePastEndCleanupDecision.DeferUntilCompletionDelivered)
            {
                staleCleanupsAwaitingCompletionDelivery.Add(new DeferredStaleCleanup
                {
                    Completion = pendingCompletion,
                    ChainNextIndex = chainNextIndex,
                    ContinuationHasActiveGhost = continuationHasActiveGhost,
                });
            }
            else if (staleDecision == StalePastEndCleanupDecision.Destroy)
            {
                RunStalePastEndCleanup(i, traj, f, currentUT,
                    chainNextIndex, continuationHasActiveGhost);
            }
        }

        /// <summary>
        /// Returns the completion event queued for <paramref name="index"/> this frame, or null.
        /// The list holds at most a few entries per frame, so a linear scan is cheaper than a
        /// parallel set that would also need index shifting.
        /// </summary>
        private PlaybackCompletedEvent FindPendingCompletedEvent(int index)
        {
            for (int k = deferredCompletedEvents.Count - 1; k >= 0; k--)
            {
                if (deferredCompletedEvents[k].Index == index)
                    return deferredCompletedEvents[k];
            }
            return null;
        }

        /// <summary>
        /// Runs the stale past-end cleanups the per-trajectory loop postponed because the
        /// slot's completion event had not been delivered yet. Called right after
        /// FireDeferredFrameEvents, so the policy has already held, destroyed or left each
        /// ghost. A slot whose ghost state is gone or was replaced (the policy destroyed it, or
        /// a committed-list shift moved the index during delivery) is left alone.
        /// </summary>
        internal void RunStalePastEndCleanupsAfterCompletionDelivery(double currentUT)
        {
            int count = staleCleanupsAwaitingCompletionDelivery.Count;
            if (count == 0)
                return;

            int kept = 0, destroyed = 0, gone = 0;
            for (int k = 0; k < count; k++)
            {
                DeferredStaleCleanup deferred = staleCleanupsAwaitingCompletionDelivery[k];
                PlaybackCompletedEvent evt = deferred.Completion;
                int index = evt.Index;
                GhostPlaybackState current;
                bool sameState = ghostStates.TryGetValue(index, out current)
                    && current != null
                    && object.ReferenceEquals(current, evt.State);
                StalePastEndCleanupDecision decision = DecideStalePastEndCleanup(
                    hasState: sameState,
                    completionFired: completedEventFired.Contains(index),
                    ghostHeld: IsGhostHeld != null && IsGhostHeld(index),
                    completionPendingDelivery: false);
                string name = evt.Trajectory?.VesselName ?? "?";
                if (decision == StalePastEndCleanupDecision.Destroy)
                {
                    destroyed++;
                    ParsekLog.Verbose("Engine",
                        "Stale past-end cleanup after completion delivery: ghost #"
                        + index.ToString(CultureInfo.InvariantCulture)
                        + " \"" + name + "\" not held by the policy, cleaning up");
                    RunStalePastEndCleanup(index, evt.Trajectory, evt.Flags, currentUT,
                        deferred.ChainNextIndex, deferred.ContinuationHasActiveGhost);
                }
                else if (decision == StalePastEndCleanupDecision.KeepHeld)
                {
                    kept++;
                    ParsekLog.Verbose("Engine",
                        "Stale past-end cleanup after completion delivery: ghost #"
                        + index.ToString(CultureInfo.InvariantCulture)
                        + " \"" + name + "\" kept (held by the policy)");
                }
                else
                {
                    gone++;
                }
            }
            staleCleanupsAwaitingCompletionDelivery.Clear();

            ParsekLog.Verbose("Engine",
                "Stale past-end cleanups after completion delivery: deferred="
                + count.ToString(CultureInfo.InvariantCulture)
                + " keptHeld=" + kept.ToString(CultureInfo.InvariantCulture)
                + " destroyed=" + destroyed.ToString(CultureInfo.InvariantCulture)
                + " alreadyGone=" + gone.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Stale past-end cleanup for one slot the policy is not holding: bridge-hold a chain
        /// head whose continuation has not activated yet, otherwise destroy the ghost.
        /// </summary>
        private void RunStalePastEndCleanup(int i, IPlaybackTrajectory traj,
            TrajectoryPlaybackFlags f, double currentUT,
            int chainNextIndex, bool continuationHasActiveGhost)
        {
            // === Chain-seam handoff: bridge-hold the head when its
            // chain continuation has not yet activated. Collapses the
            // section-gap case onto a single visible ghost by keeping
            // the head alive briefly while the continuation spawns.
            // Bounded by ChainHandoffLogic.DefaultBridgeMaxSeconds so
            // a continuation that genuinely never activates still
            // tears down. ===
            double bridgeOpenedUT;
            if (!chainBridgeOpenedUT.TryGetValue(i, out bridgeOpenedUT))
                bridgeOpenedUT = double.NaN;
            ChainBridgeAction bridge = ChainHandoffLogic.DecideBridgeHold(
                chainNextIndex,
                continuationHasActiveGhost,
                currentUT,
                bridgeOpenedUT,
                ChainHandoffLogic.DefaultBridgeMaxSeconds);
            if (bridge == ChainBridgeAction.Hold)
            {
                if (double.IsNaN(bridgeOpenedUT))
                    chainBridgeOpenedUT[i] = currentUT;
                CountFrameSkip(GhostPlaybackSkipReason.ChainBridgeHeld);
                double openedUTForLog = double.IsNaN(bridgeOpenedUT)
                    ? currentUT : bridgeOpenedUT;
                ParsekLog.VerboseRateLimited(
                    "Engine",
                    "chain-bridge-hold-" + i.ToString(CultureInfo.InvariantCulture),
                    "Ghost #" + i.ToString(CultureInfo.InvariantCulture)
                        + " \"" + (traj.VesselName ?? "?")
                        + "\" chain-bridge-hold: waiting for continuation slot #"
                        + chainNextIndex.ToString(CultureInfo.InvariantCulture)
                        + " at UT="
                        + currentUT.ToString("F2", CultureInfo.InvariantCulture)
                        + " openedUT="
                        + openedUTForLog.ToString("F2", CultureInfo.InvariantCulture)
                        + " maxSeconds="
                        + ChainHandoffLogic.DefaultBridgeMaxSeconds.ToString(
                            "F1", CultureInfo.InvariantCulture),
                    5.0);
                // Mirror the shadow path's trace emit so a "why
                // wasn't this ghost destroyed at past-end?"
                // investigation can correlate against the same
                // anomaly-window stream the rest of the engine
                // writes into.
                GhostRenderTrace.EmitGuardSkip(
                    traj, i, currentUT, "chain-bridge-held");
            }
            else
            {
                if (bridge == ChainBridgeAction.Expired)
                {
                    ParsekLog.Verbose("Engine",
                        "Ghost #" + i.ToString(CultureInfo.InvariantCulture)
                        + " \"" + (traj.VesselName ?? "?")
                        + "\" chain-bridge-expired: continuation slot #"
                        + chainNextIndex.ToString(CultureInfo.InvariantCulture)
                        + " did not activate within "
                        + ChainHandoffLogic.DefaultBridgeMaxSeconds.ToString(
                            "F1", CultureInfo.InvariantCulture)
                        + "s; destroying head at UT="
                        + currentUT.ToString("F2", CultureInfo.InvariantCulture));
                }
                chainBridgeOpenedUT.Remove(i);
                DestroyGhost(i, traj, f, reason: "stale past-end ghost (no longer held)");
            }
        }

        private void PositionGhostAtRecordingEndpoint(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state)
        {
            if (state?.ghost == null || traj == null || positioner == null)
                return;

            double endpointCoverageUT = ResolveRecordingEndpointCoverageUT(traj);
            if (TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                    index, traj, state, endpointCoverageUT,
                    "GhostPlaybackEngine.PositionGhostAtRecordingEndpoint"))
                return;

            if (RecordingEndpointResolver.TryGetOrbitEndpointUT(traj, out double orbitEndpointUT))
            {
                positioner.PositionFromOrbit(index, traj, state, orbitEndpointUT);
                if (RecordingEndpointResolver.TryGetOrbitEndpointCoordinates(
                    traj, out string bodyName, out _, out _, out double altitude))
                {
                    state.lastInterpolatedBodyName = bodyName;
                    state.lastInterpolatedAltitude = altitude;
                    state.ClearInterpolatedVelocities();
                }
                return;
            }

            double endpointUT = ResolveRecordingEndpointPlaybackUT(traj);
            if (TryGetCheckpointBackedOrbitEndpointUT(
                    traj, endpointUT, out double checkpointOrbitEndpointUT, out int checkpointSectionIndex))
            {
                ParsekLog.VerboseRateLimited(
                    "Engine",
                    "checkpoint-backed-orbit-endpoint-" + (traj.RecordingId ?? index.ToString(CultureInfo.InvariantCulture)),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Endpoint positioned through checkpoint-backed orbit path: recordingId={0} index={1} sectionIndex={2} endpointUT={3:R}",
                        traj.RecordingId ?? "<none>",
                        index,
                        checkpointSectionIndex,
                        checkpointOrbitEndpointUT),
                    5.0);
                positioner.PositionFromOrbit(index, traj, state, checkpointOrbitEndpointUT);
                return;
            }

            if (TryPositionRelativeSectionAtPlaybackUT(
                    index, traj, state, endpointUT, suppressFx: true, out _))
                return;

            if (traj.Points != null && traj.Points.Count > 0)
            {
                positioner.PositionAtPoint(index, traj, state, traj.Points[traj.Points.Count - 1]);
                return;
            }

            if (traj.SurfacePos.HasValue)
            {
                positioner.PositionAtSurface(index, traj, state);
                return;
            }

            if (traj.HasOrbitSegments)
                positioner.PositionFromOrbit(index, traj, state, traj.OrbitSegments[traj.OrbitSegments.Count - 1].endUT);
        }

        /// <summary>
        /// Mirrors endpoint positioning order for relative-coverage checks.
        /// Orbit tails and checkpoint-backed endpoints are positioned at their
        /// selected orbit UT, not necessarily the last sampled point UT.
        /// </summary>
        internal static double ResolveRecordingEndpointCoverageUT(IPlaybackTrajectory traj)
        {
            double endpointUT = ResolveRecordingEndpointPlaybackUT(traj);
            if (RecordingEndpointResolver.TryGetOrbitEndpointUT(traj, out double orbitEndpointUT))
                return orbitEndpointUT;
            if (TryGetCheckpointBackedOrbitEndpointUT(
                    traj, endpointUT, out double checkpointOrbitEndpointUT, out _))
                return checkpointOrbitEndpointUT;
            return endpointUT;
        }

        internal static bool ShouldCompleteParentAnchoredDebrisEndpointCoverageMiss(
            IPlaybackTrajectory traj)
        {
            return ShouldCompleteParentAnchoredDebrisEndpointCoverageMiss(
                traj, ResolveRecordingEndpointCoverageUT(traj));
        }

        internal static bool ShouldCompleteParentAnchoredDebrisEndpointCoverageMiss(
            IPlaybackTrajectory traj,
            double endpointCoverageUT)
        {
            return ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj, endpointCoverageUT);
        }

        internal static double ResolveRecordingEndpointPlaybackUT(IPlaybackTrajectory traj)
        {
            // Preserve endpoint-hold semantics: point-backed recordings hold the
            // last sampled pose even when ExplicitEndUT extends beyond it.
            if (traj?.Points != null && traj.Points.Count > 0)
                return traj.Points[traj.Points.Count - 1].ut;
            if (traj != null && traj.HasOrbitSegments)
                return traj.OrbitSegments[traj.OrbitSegments.Count - 1].endUT;
            return traj?.EndUT ?? 0.0;
        }

        internal static bool TryGetCheckpointBackedOrbitEndpointUT(
            IPlaybackTrajectory traj,
            double endpointUT,
            out double orbitEndpointUT,
            out int sectionIndex)
        {
            orbitEndpointUT = 0.0;
            sectionIndex = -1;
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return false;
            if (!traj.HasOrbitSegments || traj.OrbitSegments == null || traj.OrbitSegments.Count == 0)
                return false;

            sectionIndex = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, endpointUT);
            if (sectionIndex < 0 || sectionIndex >= traj.TrackSections.Count)
                return false;

            TrackSection section = traj.TrackSections[sectionIndex];
            if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint)
                return false;

            double sectionEndpointUT = section.endUT;
            if (!double.IsNaN(traj.EndUT)
                && traj.EndUT >= section.startUT
                && traj.EndUT <= section.endUT)
            {
                sectionEndpointUT = traj.EndUT;
            }

            if (TryResolveCheckpointBackedOrbitEndpointUT(
                    traj.OrbitSegments,
                    section,
                    sectionEndpointUT,
                    out orbitEndpointUT))
            {
                return true;
            }

            return false;
        }

        internal static bool TryResolveCheckpointBackedOrbitEndpointUT(
            List<OrbitSegment> segments,
            TrackSection section,
            double desiredEndpointUT,
            out double orbitEndpointUT)
        {
            orbitEndpointUT = 0.0;
            if (segments == null || segments.Count == 0)
                return false;

            if (TrajectoryMath.FindOrbitSegment(segments, desiredEndpointUT).HasValue)
            {
                orbitEndpointUT = desiredEndpointUT;
                return true;
            }

            bool found = false;
            double bestUT = 0.0;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment segment = segments[i];
                if (segment.endUT < section.startUT || segment.startUT > section.endUT)
                    continue;

                double candidateUT = desiredEndpointUT;
                if (candidateUT < segment.startUT)
                    candidateUT = segment.startUT;
                else if (candidateUT > segment.endUT)
                    candidateUT = segment.endUT;

                double distance = System.Math.Abs(candidateUT - desiredEndpointUT);
                if (!found
                    || distance < bestDistance
                    || (System.Math.Abs(distance - bestDistance) <= 1e-9 && candidateUT > bestUT))
                {
                    bestUT = candidateUT;
                    bestDistance = distance;
                    found = true;
                }
            }

            if (!found)
                return false;

            orbitEndpointUT = bestUT;
            return true;
        }
    }
}
