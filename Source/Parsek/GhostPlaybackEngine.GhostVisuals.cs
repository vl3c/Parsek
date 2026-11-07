using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Ghost visual load / unload: lazy population, hidden-ghost state, priming, watch synchronization and deferred activation.
    internal partial class GhostPlaybackEngine
    {
        private GhostVisualLoadStatus BuildGhostVisualsWithMetrics(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            bool resetCompletedEventDedup, bool forceImmediateBuild,
            out HeaviestSpawnBuildType buildType)
        {
            GhostVisualLoadStatus status = GhostVisualLoadStatus.Failed;
            buildType = HeaviestSpawnBuildType.None;
            // Bug #414: record per-spawn cost so the breakdown WARN surfaces whether a single
            // heavy ghost is the culprit vs many light ones. Take the pre-call tick count
            // before Start() resumes the stopwatch so the delta is this call only.
            long preCallTicks = spawnStopwatch.ElapsedTicks;
            spawnStopwatch.Start();
            frameSpawnCount++;
            if (resetCompletedEventDedup)
                completedEventFired.Remove(index);

            try
            {
                status = TryPopulateGhostVisuals(index, traj, state, forceImmediateBuild, out buildType);
                return status;
            }
            finally
            {
                spawnStopwatch.Stop();
                long deltaTicks = spawnStopwatch.ElapsedTicks - preCallTicks;
                if (deltaTicks > frameMaxSpawnTicks)
                {
                    frameMaxSpawnTicks = deltaTicks;
                    // Bug #450: this call is the new frame-heaviest — latch its sub-phase
                    // breakdown so the one-shot WARN can attribute whichever single spawn
                    // dominates. Copy the per-call deltas set by TryPopulateGhostVisuals;
                    // "other" is the residual that preserves the sum+residual = delta
                    // invariant the tests assert. The enum arrives directly through the
                    // out-param — no string-classification coupling between engine and
                    // diagnostics layer (prior revision had that brittleness).
                    frameHeaviestSpawnSnapshotResolveTicks = lastSpawnSnapshotResolveTicks;
                    frameHeaviestSpawnTimelineTicks = lastSpawnTimelineTicks;
                    frameHeaviestSpawnDictionariesTicks = lastSpawnDictionariesTicks;
                    frameHeaviestSpawnReentryTicks = lastSpawnReentryTicks;
                    long attributedTicks =
                        lastSpawnSnapshotResolveTicks
                        + lastSpawnTimelineTicks
                        + lastSpawnDictionariesTicks
                        + lastSpawnReentryTicks;
                    long otherTicks = deltaTicks - attributedTicks;
                    frameHeaviestSpawnOtherTicks = otherTicks < 0 ? 0 : otherTicks;
                    frameHeaviestSpawnBuildType = buildType;
                }
                if (status == GhostVisualLoadStatus.CompletedThisCall)
                    DiagnosticsState.health.ghostBuildsThisSession++;
            }
        }

        private GhostVisualLoadStatus TryPopulateGhostVisuals(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            bool forceImmediateBuild, out HeaviestSpawnBuildType buildType)
        {
            buildType = HeaviestSpawnBuildType.None;
            // Bug #450: reset per-call sub-phase deltas so a prior (shorter-lived) spawn's
            // attribution cannot leak into this call's heaviest-spawn latch. Each delta is
            // captured via pre/post Start-Stop subtraction below, matching the #414
            // spawnStopwatch pattern.
            lastSpawnSnapshotResolveTicks = 0;
            lastSpawnTimelineTicks = 0;
            lastSpawnDictionariesTicks = 0;
            lastSpawnReentryTicks = 0;

            if (state == null)
                return GhostVisualLoadStatus.Failed;
            state.audioPaused = ghostAudioPaused;

            Color ghostColor = new Color(0.2f, 1f, 0.4f, 0.8f); // bright green-cyan
            GhostBuildResult buildResult = null;
            GameObject ghost = null;
            bool builtFromSnapshot = false;
            PendingGhostVisualBuild pendingBuild = state.pendingVisualBuild;
            // Retain the exact snapshot used to begin the split build so the final
            // dictionaries/logical-part reconstruction runs against the same node after
            // several yielded frames instead of re-resolving from traj mid-build.
            ConfigNode snapshot = pendingBuild?.snapshotNode;

            // Bug #450 sub-phase 1: snapshot resolve. Included even though it's trivial so
            // the breakdown reconciles — callers reading the log can sanity-check that
            // snapshot+timeline+dicts+reentry+other ≈ spawnMax.
            if (pendingBuild == null)
            {
                long snapshotResolvePre = buildSnapshotResolveStopwatch.ElapsedTicks;
                buildSnapshotResolveStopwatch.Start();
                snapshot = GhostVisualBuilder.GetGhostSnapshot(traj);
                buildSnapshotResolveStopwatch.Stop();
                lastSpawnSnapshotResolveTicks = buildSnapshotResolveStopwatch.ElapsedTicks - snapshotResolvePre;
            }

            // Bug #450 sub-phase 2: timeline build (the dominant suspect). Covers both the
            // snapshot path (BuildTimelineGhostFromSnapshot — part instantiation, engine FX
            // size-boost, audio wiring) and the sphere fallback (CreateGhostSphere). Bug
            // #450 B2 advances the snapshot path in bounded chunks across frames.
            long timelinePre = buildTimelineStopwatch.ElapsedTicks;
            buildTimelineStopwatch.Start();
            if (pendingBuild != null)
            {
                buildType = pendingBuild.buildType;
                long timelineBudgetTicks = forceImmediateBuild
                    ? long.MaxValue
                    : MaxSpawnTimelineBuildTicksPerAdvance;
                if (!GhostVisualBuilder.AdvanceTimelineGhostBuild(pendingBuild, timelineBudgetTicks))
                {
                    buildTimelineStopwatch.Stop();
                    lastSpawnTimelineTicks = buildTimelineStopwatch.ElapsedTicks - timelinePre;
                    return GhostVisualLoadStatus.Pending;
                }

                buildResult = GhostVisualBuilder.CompleteTimelineGhostBuild(pendingBuild, traj);
                state.pendingVisualBuild = null;
                if (buildResult != null)
                {
                    ghost = buildResult.root;
                    builtFromSnapshot = true;
                }
            }
            else if (snapshot != null)
            {
                HeaviestSpawnBuildType snapshotBuildType = traj.GhostVisualSnapshot != null
                    ? HeaviestSpawnBuildType.RecordingStartSnapshot
                    : HeaviestSpawnBuildType.VesselSnapshot;
                pendingBuild = GhostVisualBuilder.TryBeginTimelineGhostBuild(
                    traj, snapshot, $"Parsek_Timeline_{index}", snapshotBuildType);
                if (pendingBuild != null)
                {
                    state.pendingVisualBuild = pendingBuild;
                    buildType = snapshotBuildType;
                    long timelineBudgetTicks = forceImmediateBuild
                        ? long.MaxValue
                        : MaxSpawnTimelineBuildTicksPerAdvance;
                    if (!GhostVisualBuilder.AdvanceTimelineGhostBuild(pendingBuild, timelineBudgetTicks))
                    {
                        buildTimelineStopwatch.Stop();
                        lastSpawnTimelineTicks = buildTimelineStopwatch.ElapsedTicks - timelinePre;
                        return GhostVisualLoadStatus.Pending;
                    }

                    buildResult = GhostVisualBuilder.CompleteTimelineGhostBuild(pendingBuild, traj);
                    state.pendingVisualBuild = null;
                    if (buildResult != null)
                    {
                        ghost = buildResult.root;
                        builtFromSnapshot = true;
                    }
                }
            }

            if (ghost == null)
            {
                ghost = GhostVisualBuilder.CreateGhostSphere($"Parsek_Timeline_{index}", ghostColor);
                buildType = HeaviestSpawnBuildType.SphereFallback;
            }
            buildTimelineStopwatch.Stop();
            lastSpawnTimelineTicks = buildTimelineStopwatch.ElapsedTicks - timelinePre;

            if (ghost == null)
                return GhostVisualLoadStatus.Failed;

            var cameraPivotObj = new GameObject("cameraPivot");
            cameraPivotObj.transform.SetParent(ghost.transform, false);

            var horizonProxyObj = new GameObject("horizonProxy");
            horizonProxyObj.transform.SetParent(cameraPivotObj.transform, false);

            state.vesselName = traj?.VesselName ?? state.vesselName ?? "Unknown";
            state.ghost = ghost;
            state.cameraPivot = cameraPivotObj.transform;
            state.horizonProxy = horizonProxyObj.transform;

            // Bug #450 sub-phase 3: dictionaries. Subtree map + logical-id set +
            // PopulateGhostInfoDictionaries + inventory/compound visibility are all
            // snapshot-walk consumers; grouped as one bucket because splitting at this
            // granularity isn't actionable for a Phase B fix.
            long dictionariesPre = buildDictionariesStopwatch.ElapsedTicks;
            buildDictionariesStopwatch.Start();
            state.partTree = GhostVisualBuilder.BuildPartSubtreeMap(snapshot);
            state.logicalPartIds = GhostVisualBuilder.BuildSnapshotPartIdSet(snapshot);

            if (builtFromSnapshot)
            {
                state.materials = new List<Material>();
            }
            else
            {
                var m = ghost.GetComponent<Renderer>()?.material;
                state.materials = m != null ? new List<Material> { m } : new List<Material>();
            }

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, buildResult, traj);
            GhostPlaybackLogic.InitializeInventoryPlacementVisibility(traj, state);
            GhostPlaybackLogic.RefreshCompoundPartVisibility(state);
            GhostPlaybackLogic.RefreshFairingStructureVisibility(state);
            GhostPlaybackLogic.RecalculateCameraPivot(state);
            GhostVisualBuilder.AttachGhostAudioToWatchPivot(buildResult, state.cameraPivot);
            buildDictionariesStopwatch.Stop();
            lastSpawnDictionariesTicks = buildDictionariesStopwatch.ElapsedTicks - dictionariesPre;

            // Bug #450 sub-phase 4: reentry FX. Bracket the ENTIRE reentry decision
            // (classification + build or skip) in a single window so the O(n)
            // HasReentryPotential scan over trajectory points on non-orbital recordings
            // is attributed to the reentry bucket rather than leaking into "other". If
            // the scan dominated and we lumped it into "other", Phase B would see a big
            // residual and pick the wrong fix branch.
            // Gate reentry FX build: stationary part-showcase ghosts, EVA walks, and slow
            // suborbital hops below Mach 1.5 can never produce reentry visuals, so we skip
            // the mesh-combine + ParticleSystem + glow-material clone work entirely. With
            // hundreds of ghosts looping, rebuilding reentry FX on every loop-cycle
            // boundary was the dominant cost in the map-view perf tank (#406).
            long reentryPre = buildReentryFxStopwatch.ElapsedTicks;
            buildReentryFxStopwatch.Start();
            if (TrajectoryMath.HasReentryPotential(traj))
            {
                // Bug #450 B3: defer the ~7 ms TryBuildReentryFx work to the first
                // in-atmosphere frame. Ghosts that never enter atmosphere (orbital-only
                // fly-bys, pad ghosts that never launch, sub-400 m/s suborbital hops
                // that stay above the boundary) save the entire build cost. The lazy
                // build fires from inside UpdateReentryFx after its body lookup so
                // there is no duplicate FlightGlobals.Bodies.Find.
                state.reentryFxInfo = null;
                state.reentryFxPendingBuild = true;
                DiagnosticsState.health.reentryFxDeferredThisSession++;
            }
            else
            {
                state.reentryFxInfo = null;
                state.reentryFxPendingBuild = false;
                DiagnosticsState.health.reentryFxSkippedThisSession++;
                ParsekLog.VerboseRateLimited("ReentryFx", $"skip-{index}",
                    $"Skipped reentry FX build for ghost #{index} \"{traj.VesselName}\" " +
                    $"— trajectory peak speed below {TrajectoryMath.ReentryPotentialSpeedFloor:F0} m/s " +
                    $"and no orbit segments (cannot produce reentry visuals)", 5.0);
            }
            buildReentryFxStopwatch.Stop();
            lastSpawnReentryTicks = buildReentryFxStopwatch.ElapsedTicks - reentryPre;
            state.reentryMpb = new MaterialPropertyBlock();

            // Keep fresh builds hidden until the playback loop has positioned them at the
            // current UT. This prevents one-frame flashes at the recording-start snapshot.
            if (ghost.activeSelf)
                ghost.SetActive(false);
            state.deferVisibilityUntilPlaybackSync = true;
            ResetGhostAppearanceTracking(state);

            if (builtFromSnapshot && buildType == HeaviestSpawnBuildType.None)
            {
                buildType = traj.GhostVisualSnapshot != null
                    ? HeaviestSpawnBuildType.RecordingStartSnapshot
                    : HeaviestSpawnBuildType.VesselSnapshot;
            }
            return GhostVisualLoadStatus.CompletedThisCall;
        }

        private bool HandleHiddenGhostVisualState(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, float warpRate, double ghostDistance,
            bool overlapGhost, string hiddenReason)
        {
            if (state == null)
                return false;

            GhostPlaybackLogic.ApplyDistanceLodFidelity(state, shouldReduceFidelity: false);

            if (ShouldPrewarmHiddenGhost(traj, state, ghostDistance, playbackUT))
            {
                if (!HasLoadedGhostVisuals(state))
                {
                    // Bug #414: throttle prewarm spawns — the safest throttle victim since the
                    // ghost is hidden by distance LOD anyway. A 1-frame prewarm delay is invisible.
                    if (!TryReserveSpawnSlot(index, "hidden-tier-prewarm"))
                        return false;
                    GhostVisualLoadStatus prewarmStatus = EnsureGhostVisualsLoaded(
                        index, traj, state, playbackUT,
                        $"hidden-tier prewarm ({hiddenReason})");
                    if (prewarmStatus == GhostVisualLoadStatus.Failed)
                    {
                        CountFrameSkip(GhostPlaybackSkipReason.VisualLoadFailed);
                        return false;
                    }
                    if (prewarmStatus == GhostVisualLoadStatus.Pending)
                        return true;
                }

                if (HasLoadedGhostVisuals(state))
                {
                    PositionLoadedGhostAtPlaybackUT(index, traj, state, playbackUT);
                    if (state.ghost.activeSelf)
                        state.ghost.SetActive(false);
                    ResetGhostAppearanceTracking(state);
                    ApplyFrameVisuals(index, traj, state, playbackUT, warpRate,
                        skipPartEvents: false, suppressVisualFx: true, allowTransientEffects: false);
                    ParsekLog.VerboseRateLimited("Engine", $"prewarm-hidden-{index}",
                        $"Ghost #{index} \"{state.vesselName}\" hidden-tier prewarm active " +
                        $"({hiddenReason})", 1.0);
                    return true;
                }
            }

            if (HasLoadedGhostVisuals(state))
            {
                // Diagnostic (anchor-distance tracer): the ghost mesh is about to be torn down.
                // Log the ghost-to-anchor-vehicle distance (state.lastDistance), the render
                // distance that drove the hide, and both world positions so a "ghost vanished
                // too early / when a duplicate launched" report can be checked against the real
                // anchor distance and against a phantom-distance seam.
                ParsekLog.VerboseRateLimited("Zone", $"mesh-teardown-{index}",
                    $"Mesh torn down: ghost #{index} \"{state.vesselName}\" "
                    + (overlapGhost ? "overlap" : "primary")
                    + " cycle=" + state.loopCycleIndex.ToString(CultureInfo.InvariantCulture)
                    + " anchorDist=" + FormatPlaybackDistanceForLog(state.lastDistance)
                    + " renderDist=" + FormatPlaybackDistanceForLog(ghostDistance)
                    + " ghostPos=" + FormatGhostWorldPosForLog(state)
                    + " anchorPos=" + FormatActiveVesselWorldPosForLog()
                    + " reason=" + hiddenReason);
                if (overlapGhost)
                    UnloadOverlapGhostVisuals(index, state, hiddenReason);
                else
                    UnloadGhostVisuals(index, state, hiddenReason);
            }

            ResetGhostAppearanceTracking(state);

            return false;
        }

        private GhostVisualLoadStatus EnsureGhostVisualsLoaded(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, string reason, bool forceImmediateBuild = false,
            bool resetCompletedEventDedup = false)
        {
            visualLoadAttemptCountForTesting++;
            if (state == null)
                return GhostVisualLoadStatus.Failed;
            if (HasLoadedGhostVisuals(state))
                return GhostVisualLoadStatus.Ready;
            if (traj.IsDebris && GhostVisualBuilder.GetGhostSnapshot(traj) == null)
                return GhostVisualLoadStatus.Failed;

            GhostVisualLoadStatus status = BuildGhostVisualsWithMetrics(
                index, traj, state, resetCompletedEventDedup, forceImmediateBuild,
                out HeaviestSpawnBuildType buildType);
            if (status == GhostVisualLoadStatus.Pending)
            {
                if (state.pendingVisualBuild != null && !state.pendingVisualBuild.hasLoggedSplitYield)
                {
                    state.pendingVisualBuild.hasLoggedSplitYield = true;
                    ParsekLog.VerboseRateLimited("Engine", $"spawn-split-{index}",
                        $"Ghost #{index} \"{state.vesselName}\" build split across frames: " +
                        $"{state.pendingVisualBuild.nextPartIndex}/{state.pendingVisualBuild.partNodes.Length} " +
                        $"snapshot parts built ({reason}, budget={GhostPlayback.MaxSpawnBuildMillisecondsPerAdvance:F1}ms)",
                        1.0);
                }
                return status;
            }

            if (status != GhostVisualLoadStatus.CompletedThisCall)
                return status;

            PrimeLoadedGhostForPlaybackUT(index, traj, state, playbackUT);
            if (state.pendingSpawnLifecycle != PendingSpawnLifecycle.None)
                FinalizePendingSpawnLifecycle(index, traj, state, playbackUT, buildType);
            else
                ParsekLog.VerboseRateLimited("Engine", $"rebuild-{index}",
                    $"Ghost #{index} \"{state.vesselName}\" visuals rebuilt ({buildType.ToLogToken()}, {reason})", 1.0);

            return status;
        }

        private bool PrimeLoadedGhostForPlaybackUT(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (state?.ghost == null)
                return false;

            state.playbackIndex = 0;
            state.partEventIndex = 0;
            state.anchorRetiredThisFrame = false;
            double primePlaybackUT = ResolveVisiblePlaybackUT(traj, state, playbackUT);
            PositionLoadedGhostAtPlaybackUT(index, traj, state, primePlaybackUT);
            bool primeAnchorRetired = RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                state.anchorRetiredThisFrame);
            // Hidden priming is allowed to apply persistent part state after a clean
            // position. If any anchor-retire path fired, not only deterministic debris
            // coverage retirement, skip part events and camera retarget from this prime.
            var visualPolicy = HiddenPrimeVisualPolicy();
            ApplyFrameVisuals(index, traj, state, primePlaybackUT, TimeWarp.CurrentRate,
                primeAnchorRetired || visualPolicy.skipPartEvents,
                visualPolicy.suppressVisualFx,
                visualPolicy.allowTransientEffects);
            if (state.ghost.activeSelf)
                state.ghost.SetActive(false);
            ResetGhostAppearanceTracking(state);
            return primeAnchorRetired;
        }

        internal static (bool skipPartEvents, bool suppressVisualFx, bool allowTransientEffects)
            HiddenPrimeVisualPolicy()
        {
            return (false, true, false);
        }

        private void SynchronizeLoadedGhostForWatch(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT,
            double currentUT)
        {
            if (!HasLoadedGhostVisuals(state))
                return;

            state.playbackIndex = 0;
            state.partEventIndex = 0;
            // Bug #613 (PR #594 P1): clear retire signal before positioning;
            // gate visuals/activation/appearance on it after. Watch-sync runs
            // through the same relative-frame positioner as the per-frame
            // path, so it has the same vulnerability to a stale anchor pid.
            state.anchorRetiredThisFrame = false;
            PositionLoadedGhostAtPlaybackUT(index, traj, state, playbackUT);
            if (RelativeAnchorResolution.ShouldSkipPostPositionPipeline(
                    state.anchorRetiredThisFrame))
            {
                ApplyFrameVisuals(index, traj, state, playbackUT, TimeWarp.CurrentRate,
                    skipPartEvents: true, suppressVisualFx: true,
                    allowTransientEffects: false);
            }
            else
            {
                bool watchSyncHidden = ShouldHoldInitialActivationHiddenThisFrame(
                    traj, state, playbackUT, out string watchSyncHideReason);
                bool hasGhostTransform = state?.ghost != null;
                Vector3 ghostPosition = hasGhostTransform
                    ? state.ghost.transform.position
                    : Vector3.zero;
                // Watch-sync does not call ResolveVisiblePlaybackUT, so raw ==
                // visible == playbackUT and clampFired is invariant false. The
                // explicit pass-through here makes that guarantee visible to
                // post-hoc trace readers and lets investigators compare watch-
                // sync activation flow with the RenderInRangeGhost path on the
                // same fields. Watch-sync rows are activation-only (no
                // surrounding FrameStart / AfterUpdate context) — accepted as
                // v1 asymmetry, see plan §1a.
                //
                // currentUT (Planetarium UT) is threaded separately from
                // playbackUT because looped watch targets pass loop-mapped
                // recording time as playbackUT — using it as currentUT would
                // open the activation-transition detailed window in the wrong
                // time coordinate and miss subsequent AfterUpdate / LateUpdate
                // emits that key off real Planetarium UT.
                GhostRenderTrace.EmitActivationDecision(
                    trajectory: traj,
                    ghostIndex: index,
                    currentUT: currentUT,
                    rawPlaybackUT: playbackUT,
                    visiblePlaybackUT: playbackUT,
                    activationStartUT: ResolveGhostActivationStartUT(traj),
                    framesRemaining: state?.initialRelativeActivationHiddenFramesRemaining ?? 0,
                    hidden: watchSyncHidden,
                    hideReason: watchSyncHidden
                        ? (watchSyncHideReason ?? "unknown")
                        : null,
                    callSite: "SynchronizeLoadedGhostForWatch",
                    currentPosition: ghostPosition,
                    hasCurrentPosition: hasGhostTransform);
                if (watchSyncHidden)
                {
                    if (state.ghost != null && state.ghost.activeSelf)
                        state.ghost.SetActive(false);
                    ApplyFrameVisuals(index, traj, state, playbackUT, TimeWarp.CurrentRate,
                        skipPartEvents: false, suppressVisualFx: true, allowTransientEffects: false);
                    ResetGhostAppearanceTracking(state);
                }
                else
                {
                    bool activatedDeferredState = ActivateGhostVisualsIfNeeded(state);
                    ApplyFrameVisuals(index, traj, state, playbackUT, TimeWarp.CurrentRate,
                        skipPartEvents: false, suppressVisualFx: false, allowTransientEffects: false);
                    if (ShouldRestoreDeferredRuntimeFxState(
                            activatedDeferredState,
                            suppressVisualFx: false))
                        GhostPlaybackLogic.RestoreDeferredRuntimeFxState(state);
                    TrackGhostAppearance(index, traj, state, playbackUT, "watch-sync");
                }
            }
        }

        /// <summary>
        /// Test seam exposing the otherwise-private
        /// <see cref="SynchronizeLoadedGhostForWatch"/> for Phase 1 unit tests
        /// that need to drive the watch-resume activation flow directly.
        /// Caller is responsible for pre-populating the playback state and
        /// the trajectory just as the production path would.
        /// </summary>
        internal void SynchronizeLoadedGhostForWatchForTesting(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT,
            double currentUT)
        {
            SynchronizeLoadedGhostForWatch(index, traj, state, playbackUT, currentUT);
        }

        private void PositionLoadedGhostAtPlaybackUT(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            if (!HasLoadedGhostVisuals(state) || traj == null || positioner == null)
                return;

            if (TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                    index, traj, state, playbackUT,
                    "GhostPlaybackEngine.PositionLoadedGhostAtPlaybackUT"))
                return;

            bool authoredGapHasShadow =
                AuthoredFrameGapHasShadowCoverage(traj, playbackUT);
            if (authoredGapHasShadow
                && TryPositionBodyFixedPrimary(
                    index,
                    traj,
                    state,
                    playbackUT,
                    "loaded-direct-authored-frame-gap"))
            {
                return;
            }

            if (!authoredGapHasShadow && ShouldUseOrbitTailPlayback(traj, playbackUT))
            {
                positioner.PositionFromOrbit(index, traj, state, playbackUT);
                return;
            }

            if (TryPositionRelativeSectionAtPlaybackUT(
                    index, traj, state, playbackUT, suppressFx: true, out _))
                return;

            bool hasPoints = traj.Points != null && traj.Points.Count >= 2;
            bool hasSurfaceData = traj.SurfacePos.HasValue;
            bool hasOrbitData = traj.HasOrbitSegments;

            if (hasPoints)
            {
                positioner.InterpolateAndPosition(index, traj, state, playbackUT, suppressFx: true);
                return;
            }

            if (hasSurfaceData)
            {
                positioner.PositionAtSurface(index, traj, state);
                return;
            }

            if (traj.Points != null && traj.Points.Count > 0)
            {
                var firstPoint = traj.Points[0];

                positioner.PositionAtPoint(index, traj, state, firstPoint);
                return;
            }

            if (hasOrbitData)
                positioner.PositionFromOrbit(index, traj, state, playbackUT);
        }

        private static bool ActivateGhostVisualsIfNeeded(GhostPlaybackState state)
        {
            if (state?.ghost == null)
                return false;

            bool restoredDeferredState = state.deferVisibilityUntilPlaybackSync;

            if (!state.ghost.activeSelf)
                state.ghost.SetActive(true);

            state.deferVisibilityUntilPlaybackSync = false;
            return restoredDeferredState;
        }

        internal static bool ActivateGhostVisualsIfNeededForTesting(GhostPlaybackState state)
            => ActivateGhostVisualsIfNeeded(state);

        internal static bool ShouldRestoreDeferredRuntimeFxState(
            bool activatedDeferredState, bool suppressVisualFx)
        {
            return activatedDeferredState && !suppressVisualFx;
        }

        /// <summary>
        /// Whether engine plume FX must be explicitly restarted this frame. True only on the
        /// suppressed -> unsuppressed transition: engine FX are event-driven and
        /// <see cref="GhostPlaybackLogic.StopAllEngineFx"/> left them dark, so they need a
        /// one-shot restart from the last recorded throttle when suppression lifts. (RCS and
        /// audio restore themselves on every unsuppressed frame, so they need no transition
        /// gate.) Distinct from <see cref="ShouldRestoreDeferredRuntimeFxState"/>, which only
        /// fires on ghost re-activation and so misses an FX-suppress lift while the ghost
        /// stayed active in the reduced-fidelity Visual tier.
        /// </summary>
        internal static bool ShouldRestartEngineFxAfterSuppression(
            bool suppressVisualFx, bool wasVisualFxSuppressed)
        {
            return !suppressVisualFx && wasVisualFxSuppressed;
        }

        private void UnloadGhostVisuals(int index, GhostPlaybackState state, string reason)
        {
            if (!HasLoadedGhostVisuals(state))
                return;

            ParsekLog.VerboseRateLimited("Engine", $"unload-{index}",
                $"Ghost #{index} \"{state.vesselName}\" visuals unloaded ({reason})", 1.0);
            DestroyGhostResourcesWithMetrics(state, lingerParticleSystems: false);
            state.ClearLoadedVisualReferences();
        }

        private void UnloadOverlapGhostVisuals(int index, GhostPlaybackState state, string reason)
        {
            if (!HasLoadedGhostVisuals(state))
                return;

            ParsekLog.VerboseRateLimited("Engine", $"overlap-unload-{index}-{state.loopCycleIndex}",
                $"Ghost #{index} cycle={state.loopCycleIndex} visuals unloaded ({reason})", 1.0);
            DestroyGhostResourcesWithMetrics(state, lingerParticleSystems: false);
            state.ClearLoadedVisualReferences();
        }
    }
}
