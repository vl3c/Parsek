using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // In-range primary ghost rendering: first spawn, positioning, frame visuals and the hidden-ghost prewarm predicates.
    internal partial class GhostPlaybackEngine
    {
        internal static bool HasRenderableGhostData(IPlaybackTrajectory traj)
        {
            return traj != null
                && ((traj.Points != null && traj.Points.Count > 0)
                    || traj.HasOrbitSegments
                    || traj.SurfacePos.HasValue);
        }

        // KEEP debris-only: the `!traj.IsDebris` gate here is intentional. This
        // predicate scopes a Re-Fly render carve-out specifically to PRE-REWIND
        // sibling debris of the re-fly origin (parent-id == marker.OriginChildRecordingId,
        // StartUT < marker.RewindPointUT). Controlled-decoupled children of the
        // origin must NOT be hidden during re-fly playback - they are not
        // "companion debris of the origin"; widening this gate to all parent-
        // anchored children would land a visible regression.
        internal static bool ShouldRenderSuppressedCompanionDebris(
            IPlaybackTrajectory traj,
            ReFlySessionMarker marker,
            TrajectoryPlaybackFlags flags)
        {
            if (!flags.sessionSuppressedRenderCarveOutEligible
                || traj == null
                || marker == null
                || !traj.IsDebris)
            {
                return false;
            }

            string parentRecordingId = traj.ParentAnchorRecordingId;
            string originRecordingId = marker.OriginChildRecordingId;
            if (string.IsNullOrWhiteSpace(parentRecordingId)
                || string.IsNullOrWhiteSpace(originRecordingId)
                || !string.Equals(parentRecordingId, originRecordingId, StringComparison.Ordinal))
            {
                return false;
            }

            string recordingId = traj.RecordingId;
            // Defend against future trajectory/flag pairing drift.
            if (string.IsNullOrWhiteSpace(recordingId)
                || !string.Equals(recordingId, flags.recordingId, StringComparison.Ordinal)
                || string.Equals(recordingId, originRecordingId, StringComparison.Ordinal)
                || string.Equals(recordingId, marker.ActiveReFlyRecordingId, StringComparison.Ordinal))
            {
                return false;
            }

            // Pre-rewind-moment companions only. The gate is strict `<`
            // against marker.RewindPointUT — captured directly from rp.UT
            // in RewindInvoker.AtomicMarkerWrite, so it is decoupled from
            // SafeNow() / onFlightReady-deferred dispatch and tracks the
            // exact rewind point UT rather than the drifted post-load
            // Planetarium UT (marker.InvokedUT). Debris timestamped before
            // RP.UT is treated as kept history (e.g. side-booster debris
            // shed pre-rewind); debris at-or-after belongs to the original
            // timeline being replaced (e.g. the upper stage's own
            // post-probe-separation break-up). At-exactly-RP debris is
            // hidden as a conservative choice: if a Breakup BP itself sits
            // at the rewind point, the new flight is the canonical author
            // of that moment's events.
            //
            // NaN or non-positive RewindPointUT (legacy marker without the
            // persisted field, or any other unset sentinel) collapses to
            // the pre-PR-858 default of "hide the suppressed debris",
            // since we have no trustworthy reference UT. Both branches are
            // spelled out explicitly so the gate reads at a glance without
            // relying on IEEE 754 NaN-comparison trivia.
            return !double.IsNaN(marker.RewindPointUT)
                && marker.RewindPointUT > 0.0
                && traj.StartUT < marker.RewindPointUT;
        }

        internal static void LogSessionSuppressedCompanionDebrisRenderAllowed(
            int index,
            IPlaybackTrajectory traj,
            ReFlySessionMarker marker)
        {
            string recordingId = traj?.RecordingId;
            string parentRecordingId = traj?.ParentAnchorRecordingId;
            double trajStartUT = traj?.StartUT ?? 0.0;
            double rewindPointUT = marker?.RewindPointUT ?? double.NaN;
            string identity = "session-suppressed-companion-debris|"
                + (!string.IsNullOrEmpty(recordingId)
                    ? recordingId
                    : index.ToString(CultureInfo.InvariantCulture));
            string stateKey =
                (marker?.SessionId ?? string.Empty) + "|" +
                (recordingId ?? string.Empty) + "|" +
                (parentRecordingId ?? string.Empty) + "|" +
                (marker?.OriginChildRecordingId ?? string.Empty) + "|" +
                (marker?.ActiveReFlyRecordingId ?? string.Empty);

            ParsekLog.VerboseOnChange(
                "Engine",
                identity,
                stateKey,
                "session-suppressed-companion-debris: render allowed "
                + "recording=#" + index.ToString(CultureInfo.InvariantCulture)
                + " recId=" + FormatRecordingIdShort(recordingId)
                + " parentRecId=" + FormatRecordingIdShort(parentRecordingId)
                + " originRecId=" + FormatRecordingIdShort(marker?.OriginChildRecordingId)
                + " activeReFlyRecId=" + FormatRecordingIdShort(marker?.ActiveReFlyRecordingId)
                + " startUT=" + trajStartUT.ToString("R", CultureInfo.InvariantCulture)
                + " rewindPointUT=" + (double.IsNaN(rewindPointUT)
                    ? "<nan>"
                    : rewindPointUT.ToString("R", CultureInfo.InvariantCulture))
                + " sess=" + (marker?.SessionId ?? "<no-id>"));
        }

        internal static bool ShouldPrewarmHiddenGhostForPartEvent(PartEventType type)
        {
            switch (type)
            {
                case PartEventType.Decoupled:
                case PartEventType.Destroyed:
                case PartEventType.ParachuteDeployed:
                case PartEventType.ParachuteSemiDeployed:
                case PartEventType.ParachuteCut:
                case PartEventType.ParachuteRepacked:
                case PartEventType.ParachuteDestroyed:
                case PartEventType.ShroudJettisoned:
                case PartEventType.DeployableExtended:
                case PartEventType.DeployableRetracted:
                case PartEventType.GearDeployed:
                case PartEventType.GearRetracted:
                case PartEventType.CargoBayOpened:
                case PartEventType.CargoBayClosed:
                case PartEventType.FairingJettisoned:
                case PartEventType.Docked:
                case PartEventType.Undocked:
                case PartEventType.InventoryPartPlaced:
                case PartEventType.InventoryPartRemoved:
                // P8: DeployableBroken is the ONLY new member that earns a prewarm — it hides a
                // mesh subtree, so a hidden ghost that skipped it would render an intact panel
                // the moment it becomes visible. The other eight are self-correcting overlays:
                // the converter running loop is a pure function of (UT - activeSinceUT) and
                // re-derives its phase on the first visible frame, and the EVA jetpack pose /
                // plume / ragdoll flags are re-established by the prefix replay when the ghost
                // is actually built. Prewarming a ghost build for those would buy nothing.
                case PartEventType.DeployableBroken:
                // Wheel damage swaps two meshes, so a hidden ghost that skipped the event would
                // show the wrong wheel the moment it became visible. Same argument as the break.
                case PartEventType.WheelBroken:
                case PartEventType.WheelRepaired:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool ShouldPrewarmHiddenGhost(
            IPlaybackTrajectory traj, GhostPlaybackState state, double ghostDistance, double currentUT)
        {
            if (traj == null || state == null)
                return false;

            if (ghostDistance <= DistanceThresholds.GhostFlight.LoopSimplifiedMeters
                + GhostPlayback.HiddenGhostVisibleTierPrewarmBufferMeters)
            {
                return true;
            }

            if (traj.PartEvents == null || traj.PartEvents.Count == 0)
                return false;

            double lookaheadDeadline = currentUT + GhostPlayback.HiddenGhostEventPrewarmLookaheadSeconds;
            int startIndex = Math.Max(0, state.partEventIndex);
            for (int i = startIndex; i < traj.PartEvents.Count; i++)
            {
                var evt = traj.PartEvents[i];
                if (evt.ut > lookaheadDeadline)
                    break;
                if (evt.ut < currentUT)
                    continue;
                if (ShouldPrewarmHiddenGhostForPartEvent(evt.eventType))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// #688 follow-up: chain-segment dead-on-arrival spawn-suppression predicate. A chain
        /// successor whose section is already past its effective end at first-spawn time, and
        /// which is not held by the policy, should never build its mesh (it would flash for a
        /// few frames then be torn down by the stale-past-end cleanup). True => suppress the
        /// first spawn. Pure; the <paramref name="ghostHeld"/> bool is resolved from the
        /// IsGhostHeld delegate at the call site (no delegate-as-parameter). The original
        /// condition was `(currentUT > endUT || currentUT > chainEndUT) && (IsGhostHeld == null
        /// || !IsGhostHeld(i))`; with ghostHeld = `IsGhostHeld != null &amp;&amp; IsGhostHeld(i)`,
        /// `!ghostHeld` is the exact De Morgan equivalent of the held-not clause.
        /// </summary>
        internal static bool IsSpawnSuppressedDeadOnArrival(
            double currentUT, double endUT, double chainEndUT, bool ghostHeld)
        {
            bool deadOnArrivalPastEnd = currentUT > endUT || currentUT > chainEndUT;
            return deadOnArrivalPastEnd && !ghostHeld;
        }

        /// <summary>
        /// First-spawn block (R3) of RenderInRangeGhost, run only when state == null:
        /// #688 dead-on-arrival suppression, the #414 first-spawn slot throttle, pending-state
        /// creation + store, and EnsureGhostVisualsLoaded with Failed/Pending handling.
        /// Returns an outcome enum so every original `return` stays at the caller's statement
        /// level (item 4: no buried return). <paramref name="state"/> and
        /// <paramref name="ghostActive"/> are by ref because this assigns state =
        /// CreatePendingSpawnState(...), writes ghostStates[i] = state, and sets ghostActive
        /// = false on the Failed/Pending paths. Verbatim extraction; the dead-on-arrival
        /// VerboseRateLimited message and the Failed-path ghostStates.Remove(i) are preserved.
        /// </summary>
        private FirstSpawnOutcome TryFirstSpawn(
            int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx,
            ref GhostPlaybackState state, ref bool ghostActive)
        {
            // #688 follow-up: chain-segment dead-on-arrival suppression.
            // A chain successor whose section is already past its
            // effective end at the moment of first-spawn would build
            // its mesh at the recorded-start coords, render for a few
            // frames at a position discontinuous from the previous
            // chain segment's last frame, then be destroyed by the
            // stale-past-end cleanup at the bottom of the loop.
            // Players see this as a one-frame Probe ghost flash on
            // the booster's destruction segment. Mirror the same
            // predicate the cleanup uses so the spawn is skipped
            // entirely; the fall-through past-end handler below this
            // branch still fires the PlaybackCompleted event so
            // consumers (camera transfer, debris spawn, milestone
            // logging) see the lifecycle they expect.
            // traj is non-null per the loop's earlier guard at the
            // entry to UpdatePlayback's per-trajectory iteration; the
            // surface area below uses it directly.
            bool ghostHeld = IsGhostHeld != null && IsGhostHeld(i);
            if (IsSpawnSuppressedDeadOnArrival(ctx.currentUT, traj.EndUT, f.chainEndUT, ghostHeld))
            {
                CountFrameSkip(GhostPlaybackSkipReason.SpawnSuppressedDeadOnArrival);
                ParsekLog.VerboseRateLimited(
                    "Engine",
                    "spawn-suppressed-dead-on-arrival-" + i.ToString(CultureInfo.InvariantCulture),
                    "Ghost #" + i.ToString(CultureInfo.InvariantCulture)
                    + " \"" + (traj.VesselName ?? "?")
                    + "\" spawn suppressed: past-effective-end at first-spawn time and not held — "
                    + "ut=" + ctx.currentUT.ToString("F2", CultureInfo.InvariantCulture)
                    + " endUT=" + traj.EndUT.ToString("F2", CultureInfo.InvariantCulture)
                    + " chainEndUT=" + f.chainEndUT.ToString("F2", CultureInfo.InvariantCulture)
                    + " (past-end handler will still fire completion)",
                    5.0);
                return FirstSpawnOutcome.ReturnFalse;
            }

            // Bug #414: throttle first-ever spawns so scene-load warm-up bursts don't
            // land every eligible ghost's visual build on a single frame.
            if (!TryReserveSpawnSlot(i, "first-spawn"))
                return FirstSpawnOutcome.ReturnFalse;

            state = CreatePendingSpawnState(
                traj, ctx.currentUT, PendingSpawnLifecycle.StandardEnter, f);
            ghostStates[i] = state;
            // Chain seam: skip time-slicing the build so gs.ghost is set the same frame the
            // successor enters range. The mid-chain Watch transfer then succeeds on its first
            // attempt instead of defer-retrying for 3-5 frames while the launch ghost's
            // terminal frame remains glued to the camera.
            GhostVisualLoadStatus firstSpawnStatus = EnsureGhostVisualsLoaded(
                i, traj, state, ctx.currentUT, "first spawn",
                forceImmediateBuild: state.spawnedAtChainSeam,
                resetCompletedEventDedup: true);
            if (firstSpawnStatus == GhostVisualLoadStatus.Failed)
            {
                CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                ghostStates.Remove(i);
                ghostActive = false;
                return FirstSpawnOutcome.ReturnFalse;
            }
            QueueOrEmitGhostSpawnPending(i, traj, state, f);
            if (firstSpawnStatus == GhostVisualLoadStatus.Pending)
            {
                ghostActive = false;
                return FirstSpawnOutcome.ReturnTrue;
            }

            return FirstSpawnOutcome.Proceed;
        }

        /// <summary>
        /// Handles in-range ghost rendering: spawn if needed, position, apply visual events.
        /// Returns true if the ghost was processed (caller should continue to next iteration).
        /// </summary>
        private bool RenderInRangeGhost(int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f,
            FrameContext ctx, bool suppressVisualFx,
            bool hasPointData, bool hasInterpolatedPoints, bool hasSurfaceData, bool hasOrbitData,
            bool allowEarlyDestroyedDebrisCompletion,
            ref GhostPlaybackState state, ref bool ghostActive)
        {
            if (allowEarlyDestroyedDebrisCompletion && earlyDestroyedDebrisCompleted.Contains(i))
                return true;

            double initialCoveragePlaybackUT = ResolveVisiblePlaybackUT(traj, state, ctx.currentUT);
            if (TryHandleParentAnchoredDebrisCoverageRetired(
                    i, traj, state, initialCoveragePlaybackUT, ctx.currentUT,
                    ctx.warpRate, "GhostPlaybackEngine.RenderInRangeGhost.pre-spawn",
                    ShouldExitWatchForCoverageRetiredState(i, state, ctx),
                    out ghostActive))
                return true;

            // This intentionally precedes permanent flag-event playback. Parent-anchored
            // debris recordings are structural fragments, not flag-planting vessels, so an
            // out-of-coverage debris frame has no standalone flag world state to preserve.
            // Flag events spawn permanent world vessels — apply regardless of ghost state,
            // zone distance, or spawn-throttle (#249, #414). Unlike visual part events (mesh
            // toggles), flags are independent entities that must exist whether or not the
            // ghost is visible AND whether or not the ghost's visual build has happened yet.
            // When `state` is null (first-spawn throttled or not yet run this frame),
            // ApplyFlagEvents falls back to a state-less walk with FlagExistsAtPosition dedup.
            GhostPlaybackLogic.ApplyFlagEvents(state, traj, ctx.currentUT);

            if (state == null)
            {
                switch (TryFirstSpawn(i, traj, f, ctx, ref state, ref ghostActive))
                {
                    case FirstSpawnOutcome.ReturnTrue: return true;
                    case FirstSpawnOutcome.ReturnFalse: return false;
                    // FirstSpawnOutcome.Proceed: fall through to the defensive
                    // null-check below and the zone-rendering pipeline.
                }
            }

            if (state == null) return false;

            // Zone-based rendering
            double renderDistance = ResolvePlaybackDistance(i, traj, state, ctx.currentUT, ctx.activeVesselPos);
            double activeVesselDistance = ResolvePlaybackActiveVesselDistance(
                i, traj, state, ctx.currentUT, ctx.activeVesselPos);
            CachePlaybackDistances(state, activeVesselDistance, renderDistance);
            var zoneResult = positioner.ApplyZoneRendering(
                i, state, traj, renderDistance, ctx.currentUT, ctx.protectedIndex);

            if (zoneResult.hiddenByZone)
            {
                GhostRenderTrace.EmitGuardSkip(
                    traj, i, ctx.currentUT,
                    "hidden-by-zone distance=" + FormatPlaybackDistanceForLog(renderDistance));
                ghostActive = HandleHiddenGhostVisualState(
                    i, traj, state, ctx.currentUT, ctx.warpRate, renderDistance, overlapGhost: false,
                    hiddenReason: $"hidden by distance LOD at {FormatPlaybackDistanceForLog(renderDistance)}");
                return true;
            }

            if (!HasLoadedGhostVisuals(state))
            {
                // Bug #414: throttle distance-tier rehydration — a 1-frame delay is invisible
                // when a ghost comes back into LOD range.
                string reloadSite = state.pendingSpawnLifecycle != PendingSpawnLifecycle.None
                    ? "first-spawn-continue"
                    : "distance-tier-rehydrate";
                if (!TryReserveSpawnSlot(i, reloadSite))
                {
                    ghostActive = false;
                    return false;
                }
                GhostVisualLoadStatus loadStatus = EnsureGhostVisualsLoaded(
                    i, traj, state, ctx.currentUT,
                    state.pendingSpawnLifecycle != PendingSpawnLifecycle.None
                        ? "continuing first spawn"
                        : "entered visible distance tier");
                if (loadStatus == GhostVisualLoadStatus.Failed)
                {
                    CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                    ghostActive = false;
                    return false;
                }
                if (loadStatus == GhostVisualLoadStatus.Pending)
                {
                    ghostActive = false;
                    return true;
                }
            }

            ghostActive = HasLoadedGhostVisuals(state);
            if (!ghostActive) return false;

            GhostPlaybackLogic.ApplyDistanceLodFidelity(state, zoneResult.reduceFidelity);

            double visiblePlaybackUT = ResolveVisiblePlaybackUT(traj, state, ctx.currentUT);
            GhostRenderTrace.BeginFrame(
                traj, i, ctx.currentUT, visiblePlaybackUT, "non-loop");

            // Bug #613 (PR #594 P1): clear the per-frame retire signal before
            // positioning so a stale value from a previous frame's relative
            // section can't leak into the current frame's pipeline. The
            // relative positioner sets it back to true if the recorded anchor
            // is unresolvable.
            state.anchorRetiredThisFrame = false;
            bool usedBodyFixedPrimary = false;

            // Position the ghost. Parent-anchored debris is only valid
            // while a recorded Relative section covers the playback UT; outside
            // that coverage, hide instead of falling through to flat points,
            // surface, or orbit-tail playback. The ShadowPositioned route has
            // already positioned the ghost via the bodyFixedFrames lerp, so the
            // normal positioning chain is also skipped for that frame.
            if (!TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                    i, traj, state, visiblePlaybackUT, "GhostPlaybackEngine.RenderInRangeGhost"))
            {
                bool authoredGapHasShadow =
                    AuthoredFrameGapHasShadowCoverage(traj, visiblePlaybackUT);
                bool orbitTailPlayback =
                    !authoredGapHasShadow && ShouldUseOrbitTailPlayback(traj, visiblePlaybackUT);

                if (hasInterpolatedPoints)
                {
                    if (orbitTailPlayback)
                    {
                        positioner.PositionFromOrbit(i, traj, state, visiblePlaybackUT);
                    }
                    else if (!TryPositionRelativeSectionAtPlaybackUT(
                                 i, traj, state, visiblePlaybackUT, suppressVisualFx,
                                 out usedBodyFixedPrimary))
                    {
                        positioner.InterpolateAndPosition(i, traj, state, visiblePlaybackUT, suppressVisualFx);
                    }
                }
                else if (hasPointData)
                {
                    // Relative single-point sections store metre offsets in
                    // latitude/longitude/altitude fields; never fall through to
                    // PositionAtPoint for them.
                    if (orbitTailPlayback)
                    {
                        positioner.PositionFromOrbit(i, traj, state, visiblePlaybackUT);
                    }
                    else if (!TryPositionRelativeSectionAtPlaybackUT(
                                 i, traj, state, visiblePlaybackUT, suppressVisualFx,
                                 out usedBodyFixedPrimary))
                    {
                        positioner.PositionAtPoint(i, traj, state, traj.Points[0]);
                    }
                }
                else if (hasSurfaceData)
                {
                    positioner.PositionAtSurface(i, traj, state);
                }
                else if (hasOrbitData)
                {
                    positioner.PositionFromOrbit(i, traj, state, visiblePlaybackUT);
                }
            }

            // Bug #613 (PR #594 P1): when the relative positioner retired the
            // ghost (anchor unresolvable post-rewind), call ApplyFrameVisuals
            // with skipPartEvents=true and suppressVisualFx=true so any
            // previously-emitting plumes/audio get cleanly stopped, but no
            // new transient events fire from the stale (0,0,0) transform.
            // Skip ActivateGhostVisualsIfNeeded -- otherwise it would
            // unconditionally re-show the ghost the same frame we hid it.
            // Skip TrackGhostAppearance -- logging a root=(0,0,0) appearance
            // for a retired ghost was the original misleading symptom.
            bool retired = EmitPostPositionUpdate(
                traj, i, ctx.currentUT, visiblePlaybackUT, state, "non-loop",
                usedBodyFixedPrimary, rawPlaybackUT: ctx.currentUT);
            if (!retired)
                MarkPrimaryGhostPositionedThisFrame(state);
            if (retired)
            {
                ApplyFrameVisuals(i, traj, state, visiblePlaybackUT, ctx.warpRate,
                    skipPartEvents: true, suppressVisualFx: true,
                    allowTransientEffects: false);
            }
            else
            {
                ApplyNonRetiredPostPosition(
                    i, traj, ctx, state, visiblePlaybackUT, suppressVisualFx, zoneResult,
                    ref ghostActive);
            }

            // Targeted post-separation observability: emits one
            // [PlaybackTrace] line per frame for ghosts whose trajectory
            // has a structural-event flag within the past 5 seconds, so
            // we can correlate the rendered ghost world position with the
            // active TrackSection during the visually critical separation
            // window. No-op outside the gate window. The retired branch
            // skips the trace because its (0,0,0) transform would pollute
            // the delta computation; the next non-retired frame establishes
            // a fresh cursor.
            if (!retired)
            {
                PlaybackTrace.MaybeEmitFrame(
                    traj, i, ctx.currentUT, state.ghost.transform.position);
            }

            // Run early-destroyed-debris completion for its side effects (event
            // emission, explosion FX). The helper's return value doesn't gate
            // the outer result: the ghost has already been rendered above this
            // line, so RenderInRangeGhost must report true regardless of
            // whether the completion fired or was skipped. #369 cosmetic.
            //
            // Bug #613 (PR #594 P1 round 2): when the relative-frame retire
            // gate fired, the ghost was just hidden at (0,0,0) — running
            // TryHandleEarlyDestroyedDebrisCompletion here would still see
            // ghostActive==true (computed from HasLoadedGhostVisuals BEFORE
            // positioning) and would TriggerExplosionIfDestroyed at the stale
            // transform plus mark explosionFired and queue the completion
            // event. Skip the helper entirely on retired frames; if the anchor
            // resolves on a later frame, the normal completion path will
            // handle it from a real position.
            if (allowEarlyDestroyedDebrisCompletion && !retired)
                TryHandleEarlyDestroyedDebrisCompletion(i, traj, f, ctx, state, ghostActive, hasPointData);
            else if (allowEarlyDestroyedDebrisCompletion && retired)
                ParsekLog.VerboseRateLimited("Engine", $"early-completion-suppressed-{i}",
                    $"early-completion suppressed: anchor retired ghost #{i} \"{traj.VesselName}\"");

            return true;
        }

        /// <summary>
        /// R10 non-retired post-position arm of RenderInRangeGhost: resolve the effective
        /// skip-part-events / suppress-visual-fx flags, decide the initial-activation-hidden
        /// hold, emit the activation-decision trace, and run the two-way hide-vs-activate
        /// split. Extracted verbatim (straight-line, no control-flow exits — both inner arms
        /// fall out the bottom). <paramref name="ghostActive"/> is by ref because the hidden
        /// arm sets it to false; the FrameContext / ZoneRenderingResult structs pass by value
        /// (no boxing). The state?.ghost null-checks keep their exact short-circuit form.
        /// </summary>
        private void ApplyNonRetiredPostPosition(
            int i, IPlaybackTrajectory traj, FrameContext ctx, GhostPlaybackState state,
            double visiblePlaybackUT, bool suppressVisualFx, ZoneRenderingResult zoneResult,
            ref bool ghostActive)
        {
            bool effectiveSkipPartEvents = zoneResult.skipPartEvents;
            bool effectiveSuppressVisualFx = suppressVisualFx || zoneResult.suppressVisualFx;
            string initialActivationHiddenReason;
            bool initialActivationHidden = ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, visiblePlaybackUT, out initialActivationHiddenReason);
            bool hasGhostTransform = state?.ghost != null;
            Vector3 ghostPosition = hasGhostTransform
                ? state.ghost.transform.position
                : Vector3.zero;
            GhostRenderTrace.EmitActivationDecision(
                trajectory: traj,
                ghostIndex: i,
                currentUT: ctx.currentUT,
                rawPlaybackUT: ctx.currentUT,
                visiblePlaybackUT: visiblePlaybackUT,
                activationStartUT: ResolveGhostActivationStartUT(traj),
                framesRemaining: state?.initialRelativeActivationHiddenFramesRemaining ?? 0,
                hidden: initialActivationHidden,
                hideReason: initialActivationHidden
                    ? (initialActivationHiddenReason ?? "unknown")
                    : null,
                callSite: "RenderInRangeGhost",
                currentPosition: ghostPosition,
                hasCurrentPosition: hasGhostTransform);
            if (initialActivationHidden)
            {
                if (state.ghost != null && state.ghost.activeSelf)
                    state.ghost.SetActive(false);
                ghostActive = false;
                ApplyFrameVisuals(i, traj, state, visiblePlaybackUT, ctx.warpRate,
                    effectiveSkipPartEvents, suppressVisualFx: true,
                    allowTransientEffects: false);
                ResetGhostAppearanceTracking(state);
                ParsekLog.VerboseRateLimited(
                    "Engine",
                    "initial-activation-hidden-" + i.ToString(CultureInfo.InvariantCulture),
                    "Ghost #" + i.ToString(CultureInfo.InvariantCulture)
                    + " \"" + (traj.VesselName ?? "?") + "\" initial activation hidden: "
                    + "reason=" + (initialActivationHiddenReason ?? "unknown") + " "
                    + "ut=" + visiblePlaybackUT.ToString("F3", CultureInfo.InvariantCulture)
                    + " activationStart="
                    + ResolveGhostActivationStartUT(traj).ToString("F3", CultureInfo.InvariantCulture)
                    + " relativeWindow="
                    + GhostPlayback.InitialRelativeActivationHiddenSeconds.ToString("F3", CultureInfo.InvariantCulture)
                    + "s absoluteBridgeMax="
                    + GhostPlayback.InitialAbsoluteBridgeActivationHiddenMaxSeconds.ToString("F3", CultureInfo.InvariantCulture)
                    + "s debrisSeedBridgeMax="
                    + GhostPlayback.InitialDebrisSeedBridgeActivationHiddenMaxSeconds.ToString("F3", CultureInfo.InvariantCulture)
                    + "s minFrames="
                    + GhostPlayback.InitialActivationHiddenMinimumFrames.ToString(CultureInfo.InvariantCulture),
                    5.0);
            }
            else
            {
                bool activatedDeferredState = ActivateGhostVisualsIfNeeded(state);
                ApplyFrameVisuals(i, traj, state, visiblePlaybackUT, ctx.warpRate,
                    effectiveSkipPartEvents, effectiveSuppressVisualFx);
                if (ShouldRestoreDeferredRuntimeFxState(
                        activatedDeferredState,
                        effectiveSuppressVisualFx))
                    GhostPlaybackLogic.RestoreDeferredRuntimeFxState(state);
                TrackGhostAppearance(index: i, traj: traj, state: state, playbackUT: visiblePlaybackUT,
                    reason: "playback", requestedPlaybackUT: ctx.currentUT);
            }
        }

        /// <summary>
        /// Applies per-frame visual events to a ghost: part events, flag events,
        /// reentry FX, and RCS emission state. Called after positioning.
        /// When skipPartEvents is true, only reentry FX and RCS are applied.
        /// </summary>
        private void ApplyFrameVisuals(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, float warpRate,
            bool skipPartEvents, bool suppressVisualFx, bool allowTransientEffects = true)
        {
            state.audioPaused = ghostAudioPaused;

            if (!skipPartEvents)
            {
                GhostPlaybackLogic.ApplyPartEvents(index, traj, ut, state, allowTransientEffects);
                // Flag events are applied earlier in RenderInRangeGhost, before zone check (#249).

                // Continuous robotic motion (rotor RPM, and wheel spin derived from ground speed)
                // is driven from HERE rather than from inside ApplyPartEvents, which early-returns
                // when a recording has no PartEvents at all. Wheel spin no longer depends on any
                // recorded event, so a rover recording that happens to carry zero part events must
                // still roll its wheels — under the old call site it silently would not. Rotors are
                // unaffected: with no events their `active` flag is false and this is a no-op.
                //
                // The sections are the wheel-spin ground-contact gate's only input; a null / empty
                // list holds the wheels still (see GhostPlaybackLogic.ResolveWheelGroundContact).
                GhostPlaybackLogic.UpdateActiveRobotics(state, ut, traj?.TrackSections);

                // S2: advance any deployable stow<->deploy clip in flight. Progress is a pure
                // function of the RECORDED event UT, so this call is idempotent within a frame and
                // correct after a scrub, a warp change or a prefix catch-up with no extra state.
                GhostPlaybackLogic.UpdateActiveDeployables(state, ut);

                // S3: gimbal / control-surface / sun-tracking synthesis, driven off the ghost's own
                // APPLIED world rotation. Placed here, after ApplyPartEvents, for the same reason
                // UpdateActiveRobotics is: it must run on every visible frame, including for a
                // recording that carries no part events at all.
                GhostPlaybackLogic.UpdateSynthesizedMotion(state, ut);
            }

            bool priorVisualFxSuppressed = state.visualFxSuppressed;
            state.visualFxSuppressed = suppressVisualFx;

            if (suppressVisualFx)
            {
                // Diagnostic (anchor-distance tracer): log the ghost-to-anchor-vehicle distance
                // on the on->off FX transition so a "FX vanished too early" report can be checked
                // against the actual anchor distance (not camera distance). ghostPos/anchorPos
                // expose a phantom-distance seam (positions look close but the metric is huge).
                if (!priorVisualFxSuppressed)
                    ParsekLog.VerboseRateLimited("Zone", $"fx-teardown-{index}",
                        $"FX suppressed: ghost #{index} \"{state.vesselName}\" cycle="
                        + state.loopCycleIndex.ToString(CultureInfo.InvariantCulture)
                        + " anchorDist=" + FormatPlaybackDistanceForLog(state.lastDistance)
                        + " renderDist=" + FormatPlaybackDistanceForLog(state.lastRenderDistance)
                        + " ghostPos=" + FormatGhostWorldPosForLog(state)
                        + " anchorPos=" + FormatActiveVesselWorldPosForLog());
                GhostPlaybackLogic.StopAllEngineFx(state);
                GhostPlaybackLogic.StopAllRcsFx(state);
                GhostPlaybackLogic.StopAllRcsEmissions(state);
                GhostPlaybackLogic.ResetReentryFx(state, index);
                DriveLaunchDustToZero(state);
                GhostPlaybackLogic.MuteAllAudio(state);
            }
            else
            {
                UpdateReentryFx(index, state, traj.VesselName, warpRate);
                UpdateLaunchDust(index, state, traj, warpRate);

                // S4 SELF-HEAL, and it belongs beside launch dust for the same reason dust is here:
                // a ParticleSystem.Play() only takes on an ACTIVE hierarchy, and a ghost is inactive
                // for the whole of its spawn-time prefix replay (BuildTimelineGhostFromSnapshot ends
                // with root.SetActive(false); ActivateGhostVisualsIfNeeded runs later). Dust gets the
                // retry for free by re-calling Play every frame; the plume is EVENT-driven, so
                // without this a ghost spawning mid-burst stayed dark for the entire burst. Gated on
                // the one flag that can want a plume, so every non-EVA ghost pays one bool read.
                GhostPlaybackLogic.UpdateEvaJetpackPlumeForFrame(state);
                GhostPlaybackLogic.RestoreAllRcsEmissions(state);
                // Boundary-overlap secondary gets NO audio (plan invariant 3): it borrows the overlap
                // STORAGE but must never be audible. The spawn-time MuteAllAudio is a one-shot flag, and
                // this unsuppressed branch runs every visible (within-LOD) frame -- calling UnmuteAllAudio
                // here would clear audioMuted and let UpdateAudioAtmosphere play the in-SOI ascending
                // ghost's engine audio. Keep the secondary muted persistently; MuteAllAudio is idempotent
                // and also covers the deferred-build case (spawn-time mute is guarded by sec.ghost != null).
                if (state.isBoundaryOverlapSecondary)
                    GhostPlaybackLogic.MuteAllAudio(state);
                else
                    GhostPlaybackLogic.UnmuteAllAudio(state);

                // Engine FX are event-driven and StopAllEngineFx left them dark; nothing
                // re-applies them per frame. On the suppressed -> unsuppressed transition,
                // restart them from the last recorded throttle so a ghost re-entering FX
                // range (canonical case: a looping aircraft that repeatedly crosses the
                // anchor distance) keeps its plume. ApplyPartEvents ran above and caught the
                // throttle cursor up to this UT, so a throttle-down during the suppressed
                // window has already cleared currentPower and is not re-ignited here. When the
                // transition also coincides with a ghost re-activation, RestoreDeferredRuntimeFxState
                // (in ApplyNonRetiredPostPosition) re-applies the same engines this frame too;
                // both route through SetEngineEmission and are idempotent.
                if (ShouldRestartEngineFxAfterSuppression(suppressVisualFx, priorVisualFxSuppressed))
                    GhostPlaybackLogic.RestoreActiveEngineFx(state);
            }

            // Per-frame atmosphere attenuation — smoothly fade audio as ghost ascends/descends.
            // Runs after part events (which may start/stop audio) and after mute/unmute.
            GhostPlaybackLogic.UpdateAudioAtmosphere(state);
        }

        private static GhostRenderTrace.RenderSurface ResolveRenderSurface(
            bool usedBodyFixedPrimary,
            bool retired)
        {
            if (usedBodyFixedPrimary)
                return GhostRenderTrace.RenderSurface.BodyFixedPrimary;
            if (retired)
                return GhostRenderTrace.RenderSurface.Hidden;
            return GhostRenderTrace.RenderSurface.Legacy;
        }

        /// <summary>
        /// Computes the post-position retired flag and emits the post-update
        /// render trace for one positioned ghost. Shared by the non-loop,
        /// overlap-primary, and loop-overlap per-frame paths, which differ only
        /// in the ghost index, playback UT, state, path label, body-fixed flag,
        /// and rawPlaybackUT. Returns retired so callers can gate their divergent
        /// post-position bodies. Hot per-frame path: all arguments are primitives
        /// or reference copies, no allocations.
        /// </summary>
        private static bool EmitPostPositionUpdate(
            IPlaybackTrajectory traj,
            int ghostIndex,
            double currentUT,
            double playbackUT,
            GhostPlaybackState state,
            string path,
            bool usedBodyFixedPrimary,
            double rawPlaybackUT)
        {
            bool retired = RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                state.anchorRetiredThisFrame);
            GhostRenderTrace.EmitPostUpdate(
                traj, ghostIndex, currentUT, playbackUT, state, path, retired,
                ResolveRenderSurface(usedBodyFixedPrimary, retired),
                rawPlaybackUT: rawPlaybackUT,
                activationStartUT: ResolveGhostActivationStartUT(traj));
            return retired;
        }
    }
}
