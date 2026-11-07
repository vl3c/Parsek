using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // The IGhostPositioner implementation the playback engine calls, and zone rendering shared by
    // normal, background and looped ghosts.
    public partial class ParsekFlight
    {
        /// <summary>
        // ZoneRenderingResult struct is now in IGhostPositioner.cs (namespace-level, shared with engine)

        /// <summary>
        /// Evaluates zone-based rendering for a ghost: computes distance, classifies zone,
        /// detects transitions, hides/shows mesh, exits watch mode if needed.
        /// Shared by normal, background, and looped ghost update paths.
        /// </summary>
        ZoneRenderingResult ApplyZoneRenderingImpl(int recIdx, GhostPlaybackState state, IPlaybackTrajectory rec,
            double renderDistance, double playbackUT, int protectedIndex)
        {
            double activeVesselDistance = state != null ? state.lastDistance : renderDistance;
            bool orbitTailPlayback = GhostPlaybackEngine.ShouldUseOrbitTailPlayback(rec, playbackUT);
            var zone = state != null
                ? RenderingZoneManager.ClassifyDistance(renderDistance, state.currentZone)
                : RenderingZoneManager.ClassifyDistance(renderDistance);
            var (isWatchedGhost, _, isWatchProtectedRecording) =
                ResolveZoneWatchState(recIdx, state, protectedIndex);

            // Cache render distance separately from active-vessel safety distance.
            if (state != null)
                state.lastRenderDistance = renderDistance;

            // Detect zone transition
            if (state != null)
            {
                string transDesc;
                if (GhostPlaybackLogic.DetectZoneTransition(state.currentZone, zone, out transDesc))
                {
                    RenderingZoneManager.LogZoneTransition(
                        $"#{recIdx} \"{rec.VesselName}\"", state.currentZone, zone, renderDistance);
                    state.currentZone = zone;
                }
            }

            var (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning) =
                GhostPlaybackLogic.GetZoneRenderingPolicy(zone);

            // Ghost camera cutoff: exit watch mode when the watched ghost reaches or exceeds
            // the fixed watch cutoff distance. The cutoff applies uniformly to all ghosts.
            //
            // Debounce: requires WatchExitCutoffDebounceFrames consecutive
            // frames over the cutoff before exiting. Suppresses single-frame
            // false positives where state.lastDistance was computed across a
            // FloatingOrigin / Krakensbane frame seam during time warp (the
            // freshly-resolved ghost world position and the active vessel's
            // stale pre-shift transform.position end up in different
            // floating-origin frames and Vector3d.Distance picks up a phantom
            // hundreds-of-km gap). The counter is driven on every watched-
            // ghost frame regardless of zone hide/positioning order, so it
            // also can't get stuck rejecting in a "renderDistance is Beyond"
            // hidden-without-positioning loop. Real cutoff crossings exit
            // after the debounce window (~50 ms at 60 fps).
            bool forceWatchedFullFidelity = false;
            // Drive the cutoff debounce counter only when we're processing
            // the watched ghost. Every other ghost would register a
            // within-range sample and reset the counter, so a real cutoff
            // crossing on the watched ghost could never accumulate enough
            // consecutive samples to fire whenever any non-watched ghost
            // ran later in the same frame.
            bool cutoffTriggered;
            if (isWatchedGhost && watchMode != null)
            {
                double watchCutoffDistance = WatchModeController.ResolveWatchCutoffDistance(
                    activeVesselDistance,
                    renderDistance,
                    includeRenderDistance: orbitTailPlayback);
                bool cachedCutoffTripped =
                    WatchModeController.ShouldExitWatchForDistance(watchCutoffDistance);
                cutoffTriggered = watchMode.RegisterWatchCutoffSampleAndShouldExit(cachedCutoffTripped);
                if (cachedCutoffTripped && !cutoffTriggered)
                {
                    ParsekLog.VerboseRateLimited("Zone", $"watch-cutoff-debounce-{recIdx}",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" cached cutoff tripped " +
                        $"({watchCutoffDistance.ToString("F0", CultureInfo.InvariantCulture)}m >= " +
                        $"{WatchModeController.WatchExitCutoffMeters.ToString("F0", CultureInfo.InvariantCulture)}m), " +
                        $"debounce={watchMode.WatchCutoffConsecutiveFramesForDiagnostics}/" +
                        $"{WatchModeController.WatchExitCutoffDebounceFrames} — staying in watch mode");
                }
            }
            else if (isWatchedGhost)
            {
                // No watch controller available (e.g. test stubs): fall
                // back to immediate exit on cached cutoff trip so we do
                // not silently drop real exits.
                double watchCutoffDistance = WatchModeController.ResolveWatchCutoffDistance(
                    activeVesselDistance,
                    renderDistance,
                    includeRenderDistance: orbitTailPlayback);
                cutoffTriggered =
                    WatchModeController.ShouldExitWatchForDistance(watchCutoffDistance);
            }
            else
            {
                cutoffTriggered = false;
            }
            if (isWatchedGhost || isWatchProtectedRecording)
            {
                if (cutoffTriggered)
                {
                    // Append ghost-world + active-section + anchor context so a
                    // "ghost flew off into space" cutoff is diagnosable from
                    // KSP.log alone. Common root cause is a Relative-anchor
                    // section whose live anchor is in a totally different
                    // physical location than at recording time (e.g.
                    // previously re-flown booster now in stable orbit).
                    // Without this context the user has to cross-reference
                    // prec.txt + flight state to figure out why the distance
                    // went absurd.
                    string cutoffContext = DescribeWatchCutoffContext(rec, state);
                    double watchCutoffDistance = WatchModeController.ResolveWatchCutoffDistance(
                        activeVesselDistance,
                        renderDistance,
                        includeRenderDistance: orbitTailPlayback);
                    ParsekLog.Info("Zone",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" exceeded ghost camera cutoff " +
                        $"({watchCutoffDistance.ToString("F0", CultureInfo.InvariantCulture)}m cutoff distance >= " +
                        $"{WatchModeController.WatchExitCutoffMeters.ToString("F0", CultureInfo.InvariantCulture)}m; " +
                        $"active={activeVesselDistance.ToString("F0", CultureInfo.InvariantCulture)}m " +
                        $"render={renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m) " +
                        $"after {WatchModeController.WatchExitCutoffDebounceFrames}-frame debounce — exiting watch mode" +
                        (string.IsNullOrEmpty(cutoffContext) ? string.Empty : " | " + cutoffContext));
                    ExitWatchModePreservingLineage();
                    // Don't return — let zone rendering continue (ghost will be hidden if Beyond)
                }
                else
                {
                    forceWatchedFullFidelity =
                        GhostPlaybackLogic.ShouldForceWatchProtectedFullFidelity(
                            isWatchProtectedRecording, orbitTailPlayback)
                        || WatchModeController.ShouldForceWatchedFullFidelityAtDistance(
                            isWatchedGhost, activeVesselDistance);
                    (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning) =
                        GhostPlaybackLogic.ApplyWatchedFullFidelityOverride(
                            shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning,
                            forceWatchedFullFidelity);

                    if (forceWatchedFullFidelity)
                    {
                        GhostPlaybackLogic.RestoreWatchedFullFidelityState(state);
                        if (ShouldAutoActivateGhost(state)
                            && state != null && state.ghost != null && !state.ghost.activeSelf)
                            state.ghost.SetActive(true);
                        if (zone == RenderingZone.Beyond)
                        {
                            string reason = isWatchedGhost
                                ? (rec.HasOrbitSegments ? "watched orbital ghost" : "watched")
                                : "watch-protected debris";
                            ParsekLog.VerboseRateLimited("Zone", $"watch-protected-zone-exempt-{recIdx}",
                                $"Ghost #{recIdx} \"{rec.VesselName}\" beyond visual range " +
                                $"({renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m render distance) but {reason} — exempt from full-fidelity LOD suppression",
                                5.0);
                        }
                    }
                }
            }

            var (distanceHideMesh, distanceSkipPartEvents, distanceSkipPositioning,
                distanceSuppressVisualFx, distanceReduceFidelity) =
                GhostPlaybackLogic.ApplyDistanceLodPolicy(
                    shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning,
                    renderDistance, forceWatchedFullFidelity, zone);
            shouldHideMesh = distanceHideMesh;
            shouldSkipPartEvents = distanceSkipPartEvents;
            shouldSkipPositioning = distanceSkipPositioning;

            // #171: During warp, exempt orbital ghosts from zone hiding — they travel far
            // from the player and would complete playback while invisible.
            if (GhostPlaybackLogic.ShouldApplyWarpZoneHideExemption(
                    shouldHideMesh,
                    zone,
                    GetCurrentWarpRateSafe(),
                    rec.HasOrbitSegments
                        && GhostPlaybackLogic.ShouldAllowWarpZoneHideExemption(
                            isWatchProtectedRecording, orbitTailPlayback)))
            {
                shouldHideMesh = false;
                ParsekLog.VerboseRateLimited("Zone", $"warp-zone-exempt-{recIdx}",
                    $"Ghost #{recIdx} \"{rec.VesselName}\" beyond visual range " +
                    $"({renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m render distance) " +
                    $"but exempt during warp (orbital ghost)", 5.0);
            }

            if (shouldHideMesh)
            {
                if (state != null && state.ghost != null && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ParsekLog.VerboseRateLimited("Zone", "zone-hide",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" hidden by distance LOD " +
                        $"({renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m render distance)");
                }
                return new ZoneRenderingResult
                {
                    hiddenByZone = true,
                    skipPartEvents = true,
                    suppressVisualFx = true,
                    reduceFidelity = false
                };
            }

            // Zone 1 or 2: ensure ghost is visible
            if (ShouldAutoActivateGhost(state)
                && state != null && state.ghost != null && !state.ghost.activeSelf)
            {
                state.ghost.SetActive(true);
                ParsekLog.VerboseRateLimited("Zone", "zone-show",
                    $"Ghost #{recIdx} \"{rec.VesselName}\" re-shown: entered visible distance tier " +
                    $"({renderDistance.ToString("F0", CultureInfo.InvariantCulture)}m render distance)");
            }

            return new ZoneRenderingResult
            {
                hiddenByZone = false,
                skipPartEvents = shouldSkipPartEvents,
                suppressVisualFx = distanceSuppressVisualFx,
                reduceFidelity = distanceReduceFidelity
            };
        }

        /// <summary>
        /// Contract for the `allowActivation` parameter threaded through
        /// IGhostPositioner / ParsekFlight positioning methods (bug #258 / #375):
        ///
        /// - `true` = positioner may call <c>ghost.SetActive(true)</c> after
        ///   world-space placement. Default for routine playback frames.
        /// - `false` = positioner leaves visibility untouched. Only use when a
        ///   downstream step (e.g. <c>ActivateGhostVisualsIfNeeded</c> during loop
        ///   sync or deferred-FF handoff) owns the SetActive decision and must
        ///   run AFTER world-space positioning so the first visible frame is
        ///   already aligned with playback.
        ///
        /// Every new positioning path must decide explicitly. Forgetting to thread
        /// the flag (and defaulting to <c>true</c> blind) can re-introduce the
        /// "first-visible-frame shows stale initial pose" regression that #258
        /// fixed. <c>ShouldAutoActivateGhost</c> is the canonical source for the
        /// value.
        ///
        /// </summary>
        private static bool ShouldAutoActivateGhost(GhostPlaybackState state)
        {
            if (state == null) return true;
            if (state.deferVisibilityUntilPlaybackSync) return false;
            return true;
        }

        /// <summary>
        /// Diagnostic context appended to the watch-cutoff log line: ghost
        /// world position plus the active section under the current game UT.
        /// Recorded Relative sections are identified by anchor recording ID;
        /// non-loop playback must not enrich this diagnostic from live vessel
        /// PID lookups.
        /// </summary>
        internal static string DescribeWatchCutoffContext(
            IPlaybackTrajectory rec, GhostPlaybackState state)
        {
            if (state == null || state.ghost == null)
                return string.Empty;

            var ic = CultureInfo.InvariantCulture;
            Vector3d ghostWorld = state.ghost.transform.position;
            string ghostWorldStr = $"ghostWorld={FormatVector3d(ghostWorld)}";

            if (rec?.TrackSections == null || rec.TrackSections.Count == 0)
                return ghostWorldStr;

            // Probe the section under the current game UT for relative-anchor
            // context. If the trajectory is using a different playback UT
            // (loops, overlap shift), this lookup may pick a neighbouring
            // section, which is fine — the goal is "give the user a
            // starting point for diagnosing", not perfect attribution.
            double currentUT;
            try
            {
                currentUT = Planetarium.GetUniversalTime();
            }
            catch (System.Exception ex)
            {
                // Defensive — Planetarium is alive in flight scenes but the
                // catch keeps the cutoff log path noise-free if we ever get
                // called from an off-nominal scene. Surface the skip so the
                // next surprise is debuggable.
                ParsekLog.VerboseRateLimited("Zone", "watch-cutoff-context-skip",
                    $"DescribeWatchCutoffContext: Planetarium.GetUniversalTime threw {ex.GetType().Name} — skipping section/anchor context");
                return ghostWorldStr;
            }

            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, currentUT);
            if (sectionIdx < 0 || sectionIdx >= rec.TrackSections.Count)
                return ghostWorldStr + " section=none";

            TrackSection section = rec.TrackSections[sectionIdx];
            string sectionStr =
                $"section={section.referenceFrame} " +
                $"sectionUT=[{section.startUT.ToString("F1", ic)}," +
                $"{section.endUT.ToString("F1", ic)}]";

            if (section.referenceFrame != ReferenceFrame.Relative)
                return $"{ghostWorldStr} {sectionStr}";

            string anchorRec = string.IsNullOrWhiteSpace(section.anchorRecordingId)
                ? "anchorRec=missing"
                : $"anchorRec={section.anchorRecordingId.Trim()}";
            return $"{ghostWorldStr} {sectionStr} {anchorRec}";
        }

        internal (bool isWatchedGhost, int watchProtectionIndex, bool isWatchProtectedRecording)
            ResolveZoneWatchState(int recIdx, GhostPlaybackState state, int protectedIndex)
        {
            bool isWatchedGhost = watchMode != null
                ? watchMode.IsWatchedGhostState(recIdx, state)
                : GhostPlaybackLogic.IsProtectedGhost(protectedIndex, recIdx);
            int watchProtectionIndex = watchMode != null
                ? watchMode.WatchProtectionRecordingIndex
                : protectedIndex;
            bool isWatchProtectedRecording = GhostPlaybackLogic.IsWatchProtectedRecording(
                RecordingStore.CommittedRecordings, RecordingStore.CommittedTrees, watchProtectionIndex, recIdx);
            return (isWatchedGhost, watchProtectionIndex, isWatchProtectedRecording);
        }

        void IGhostPositioner.InterpolateAndPosition(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx)
        {
            if (state?.ghost == null || traj?.Points == null) return;
            activeBodyFixedShiftSeconds = state.bodyFixedShiftSeconds;
            try
            {
                InterpolateAndPositionEntryCore(index, traj, state, ut);
            }
            finally
            {
                activeBodyFixedShiftSeconds = 0.0;
            }
        }

        private void InterpolateAndPositionEntryCore(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut)
        {
            int playbackIdx = state.playbackIndex;
            InterpolationResult interpResult;

            if (TryInterpolateAndPositionCheckpointSection(
                index,
                traj,
                state.ghost,
                ref playbackIdx,
                ut,
                ShouldAutoActivateGhost(state),
                out interpResult))
            {
                state.SetInterpolated(interpResult);
                state.playbackIndex = playbackIdx;
                return;
            }

            int splineSectionIdx = traj.TrackSections != null && traj.TrackSections.Count > 0
                ? TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, ut)
                : -1;

            if (splineSectionIdx >= 0
                && TryGetAbsoluteSectionPlaybackFrames(
                    traj.TrackSections[splineSectionIdx],
                    out List<TrajectoryPoint> bodyFixedFrames))
            {
                string pointFrameSource = "section-frames";
                if (TryGetAbsoluteSectionPlaybackFramesForPlayback(
                        traj.TrackSections,
                        splineSectionIdx,
                        out List<TrajectoryPoint> bridgedFrames,
                        out string bridgedFrameSource))
                {
                    bodyFixedFrames = bridgedFrames;
                    pointFrameSource = bridgedFrameSource;
                }

                int absolutePlaybackIdx = playbackIdx;
                InterpolateAndPosition(
                    state.ghost,
                    bodyFixedFrames,
                    null,
                    ref absolutePlaybackIdx,
                    ut,
                    index * 10000,
                    out interpResult,
                    allowActivation: ShouldAutoActivateGhost(state),
                    skipOrbitSegments: true,
                    recordingId: traj.RecordingId,
                    sectionIndex: splineSectionIdx,
                    pointFrameSource: pointFrameSource);
                state.SetInterpolated(interpResult);
                state.playbackIndex = absolutePlaybackIdx;
                return;
            }

            bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(traj.TrackSections, ut);
            // Phase 1: pass section index so the body-fixed spline lookup can
            // gate per-section. Falls through to legacy lerp when sections
            // are absent (-1) - older flat-point recordings remain bit-exact.
            InterpolateAndPosition(state.ghost, traj.Points, traj.OrbitSegments,
                ref playbackIdx, ut, index * 10000, out interpResult,
                allowActivation: ShouldAutoActivateGhost(state), skipOrbitSegments: surfaceSkip,
                recordingId: traj.RecordingId, sectionIndex: splineSectionIdx,
                pointFrameSource: "flat-points",
                flatPointSections: traj.TrackSections);
            state.SetInterpolated(interpResult);
            state.playbackIndex = playbackIdx;
        }

        void IGhostPositioner.InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx,
            RelativeSectionPlaybackTarget target)
        {
            if (state?.ghost == null || traj?.Points == null) return;
            int sectionIdx = target.SectionIndex >= 0
                ? target.SectionIndex
                : TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, ut);
            TrackSection section = target.SectionIndex >= 0
                ? target.Section
                : sectionIdx >= 0 ? traj.TrackSections[sectionIdx] : default(TrackSection);
            var sectionFrames = (sectionIdx >= 0 && section.frames != null)
                ? section.frames
                : traj.Points;

            string relKey = string.Concat(
                index.ToString(CultureInfo.InvariantCulture),
                "|",
                target.AnchorRecordingId ?? "(missing)",
                "|",
                sectionIdx.ToString(CultureInfo.InvariantCulture));
            if (loggedRecordedRelativeStart.Add(relKey))
            {
                string sectionUt = sectionIdx >= 0
                    ? $"[{traj.TrackSections[sectionIdx].startUT:F1},{traj.TrackSections[sectionIdx].endUT:F1}]"
                    : "[fallback]";
                ParsekLog.Info("Playback",
                    $"RELATIVE playback started: recording #{index} \"{traj.VesselName}\" " +
                    $"anchorRec={target.AnchorRecordingId ?? "(missing)"} " +
                    $"contract=anchor-local " +
                    $"version={traj.RecordingFormatVersion} sectionUT={sectionUt}");
            }

            int playbackIdx = state.playbackIndex;
            InterpolationResult interpResult;
            // PR #594 P1 review fix: pass `state` so the relative-frame
            // retirement branch can set state.anchorRetiredThisFrame, which
            // the engine's per-frame loop reads to skip ApplyFrameVisuals'
            // transient events, ActivateGhostVisualsIfNeeded, and
            // TrackGhostAppearance. Without this signal, the engine's
            // unconditional SetActive(true) at the end of the frame would
            // re-show the just-hidden ghost at its stale (0,0,0) transform.
            InterpolateAndPositionRecordedRelative(state.ghost, traj, sectionFrames, ref playbackIdx,
                ut, target, traj.RecordingFormatVersion,
                index, traj.VesselName, state,
                ShouldAutoActivateGhost(state), out interpResult);
            state.SetInterpolated(interpResult);
            state.playbackIndex = playbackIdx;
        }

        /// <summary>
        /// Position the ghost from the recording's `bodyFixedFrames` primary
        /// track for parent-anchored debris. Routes through the same
        /// `InterpolateAndPosition` path as absolute sections, so body /
        /// altitude / GhostPosEntry FloatingOrigin reapply / InterpolationResult
        /// population are reused.
        /// </summary>
        /// <remarks>
        /// Parent-anchored contract: this is the primary rendering surface for
        /// parent-anchored debris whenever the active Relative section's
        /// `bodyFixedFrames` covers the playback UT. Loop-anchored chains still
        /// try relative replay first and use this as the recorded fallback when
        /// the loop anchor cannot be resolved.
        ///
        /// This path is recorded-data-only, never a substitute for live anchors.
        /// </remarks>
        bool IGhostPositioner.TryPositionFromBodyFixedPrimary(
            int index,
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            RelativeSectionPlaybackTarget target,
            out double bracketBeforeUT,
            out double bracketAfterUT)
        {
            bracketBeforeUT = double.NaN;
            bracketAfterUT = double.NaN;

            double firstBodyFixedUT;
            double lastBodyFixedUT;
            if (state?.ghost == null
                || !BodyFixedPrimaryCoversPlaybackUT(
                    target.Section,
                    playbackUT,
                    out firstBodyFixedUT,
                    out lastBodyFixedUT))
            {
                // Single-sample body-fixed primary data is rejected: InterpolateAndPosition
                // would snap to that one point and we'd render a stale pose
                // for the rest of the window.
                return false;
            }

            // Bracket UTs are reported back to the caller for the
            // [Anchor] anchor-rotation-shadow-route log line.
            ResolveBodyFixedPrimaryBracketUTs(
                target.Section.bodyFixedFrames, playbackUT,
                out bracketBeforeUT, out bracketAfterUT);

            int playbackIdx = state.playbackIndex;
            int absolutePlaybackIdx = playbackIdx;
            // Snapshot the ghost's pre-call active state so we can detect
            // InterpolateAndPosition's body-lookup-miss / empty-points
            // failure paths, which call ghost.SetActive(false) and write
            // InterpolationResult.Zero. See ParsekFlight.cs:17226-17256.
            bool ghostWasActive = state.ghost.activeSelf;
            // ---- Trace-Sep: open playback window on first parent-anchored debris render ----
            bool traceEnabled = ParsekSettings.Current?.ghostRenderTracing == true;
            int bodyFixedCount = target.Section.bodyFixedFrames != null
                ? target.Section.bodyFixedFrames.Count : 0;
            bool firstPlaybackRender = state.appearanceCount == 0;
            // Pre-call ghost world snapshot so the playback trace can log the
            // per-frame world step (pre → post) that the lerp produces. This
            // captures the user-visible "slide" instant: on the first
            // playback frame the ghost transform.position is whatever the
            // spawner / previous frame left it; after InterpolateAndPosition
            // runs it jumps to the lerp output. Distance between the two is
            // exactly the slide. Gated on traceEnabled so production runs
            // pay nothing for the snapshot.
            Vector3d ghostWorldBefore = Vector3d.zero;
            if (traceEnabled && state.ghost?.transform != null)
                ghostWorldBefore = (Vector3d)state.ghost.transform.position;
            if (traceEnabled
                && firstPlaybackRender && traj != null && traj.IsDebris
                && !string.IsNullOrWhiteSpace(traj.ParentAnchorRecordingId))
            {
                TraceSeparation.OpenPlaybackWindow(
                    "first debris ghost render recId=" + (target.RecordingId ?? "<null>"));
            }
            // ---- /Trace-Sep ----
            InterpolateAndPosition(
                state.ghost,
                target.Section.bodyFixedFrames,
                null,
                ref absolutePlaybackIdx,
                playbackUT,
                index * 10000,
                out InterpolationResult interpResult,
                allowActivation: ShouldAutoActivateGhost(state),
                skipOrbitSegments: true,
                recordingId: target.RecordingId,
                sectionIndex: target.SectionIndex);
            state.playbackIndex = absolutePlaybackIdx;
            // ---- Trace-Sep: log debris playback positioning + bracketing samples ----
            if (traceEnabled
                && TraceSeparation.PlaybackWindowActive
                && traj != null && traj.IsDebris
                && !string.IsNullOrWhiteSpace(traj.ParentAnchorRecordingId))
            {
                Vector3d ghostWorld = state.ghost.transform != null
                    ? (Vector3d)state.ghost.transform.position : Vector3d.zero;
                TrajectoryPoint? beforeFrame = null;
                TrajectoryPoint? afterFrame = null;
                if (target.Section.bodyFixedFrames != null && target.Section.bodyFixedFrames.Count > 0)
                {
                    int beforeIdx = -1;
                    int afterIdx = -1;
                    for (int j = 0; j < target.Section.bodyFixedFrames.Count; j++)
                    {
                        if (target.Section.bodyFixedFrames[j].ut <= playbackUT)
                            beforeIdx = j;
                        if (target.Section.bodyFixedFrames[j].ut >= playbackUT && afterIdx < 0)
                            afterIdx = j;
                    }
                    if (beforeIdx >= 0) beforeFrame = target.Section.bodyFixedFrames[beforeIdx];
                    if (afterIdx >= 0) afterFrame = target.Section.bodyFixedFrames[afterIdx];
                }
                string beforeStr = beforeFrame.HasValue
                    ? ("ut=" + beforeFrame.Value.ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + " lla=(" + beforeFrame.Value.latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + "," + beforeFrame.Value.longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + "," + beforeFrame.Value.altitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")")
                    : "<none>";
                string afterStr = afterFrame.HasValue
                    ? ("ut=" + afterFrame.Value.ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + " lla=(" + afterFrame.Value.latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + "," + afterFrame.Value.longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + "," + afterFrame.Value.altitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")")
                    : "<none>";
                // Lerp alpha + manually-predicted ghost world position. The
                // lerp alpha is what InterpolateAndPosition computed
                // implicitly from playbackUT / bracket UTs; we reconstruct it
                // here so the log row carries the interpolation factor
                // explicitly. The predicted world position is a body-fixed
                // linear blend of the bracketing LLAs (each resolved via
                // body.GetWorldSurfacePosition independently then linearly
                // blended in world-space). If `ghostWorld` differs from
                // `predictedWorld` by more than rounding, the discrepancy is
                // playback-math; if they match but the ghost is still
                // slipping forward relative to truth, the discrepancy lives
                // in the recorded LLAs themselves (recorder-side phase
                // offset).
                double lerpAlpha = double.NaN;
                Vector3d predictedWorld = Vector3d.zero;
                if (beforeFrame.HasValue && afterFrame.HasValue)
                {
                    lerpAlpha = TraceSeparation.ComputeLerpAlpha(
                        beforeFrame.Value.ut, afterFrame.Value.ut, playbackUT);
                    CelestialBody body = !string.IsNullOrEmpty(beforeFrame.Value.bodyName)
                        ? FlightGlobals.Bodies?.Find(b => b.name == beforeFrame.Value.bodyName)
                        : null;
                    if (body != null && !double.IsNaN(lerpAlpha))
                    {
                        Vector3d worldBefore = body.GetWorldSurfacePosition(
                            beforeFrame.Value.latitude,
                            beforeFrame.Value.longitude,
                            beforeFrame.Value.altitude);
                        Vector3d worldAfter = body.GetWorldSurfacePosition(
                            afterFrame.Value.latitude,
                            afterFrame.Value.longitude,
                            afterFrame.Value.altitude);
                        predictedWorld = worldBefore + (worldAfter - worldBefore) * lerpAlpha;
                    }
                }
                Vector3d worldStep = ghostWorld - ghostWorldBefore;
                Vector3d predictedVsActual = ghostWorld - predictedWorld;
                // Parent ghost world position (rendered) so we can log the
                // RENDERED parent-vs-debris distance on screen. Looked up via
                // the engine's recordingId mapping; returns false when the
                // parent ghost hasn't materialized yet (in which case the
                // distance fields stay NaN in the log).
                Vector3d parentGhostWorld = Vector3d.zero;
                bool parentGhostFound = engine != null
                    && engine.TryGetGhostWorldByRecordingId(
                        traj.ParentAnchorRecordingId, out parentGhostWorld);
                double renderedParentDist = parentGhostFound
                    ? (ghostWorld - parentGhostWorld).magnitude
                    : double.NaN;
                // Recorded anchor-local distance at the bracketing
                // before-frame: in Relative sections the `frames[]` entries
                // store anchor-local Cartesian metres in `latitude/longitude/altitude`,
                // so the offset magnitude IS the parent-vs-debris distance
                // the recorder captured at the bracket's left edge.
                //
                // Do NOT compare this directly to renderedParentDist as a
                // fidelity check — this field is the seed-only value and
                // stays constant across the entire bracket (e.g. the 600 ms
                // seed→first-sample gap on a fresh debris recording),
                // while the recorded relative motion may genuinely evolve
                // across that bracket and the rendered distance follows
                // the bracket's linear interpolation. Use
                // `interpolatedAnchorLocalDist` (below) for the
                // fidelity check; this field is retained for the
                // "bracket-left-edge snapshot" view and for direct
                // comparison against the section's `bodyFixedFrames[]`
                // bracketing-before sample via `recordedBodyFixedDist`.
                double recordedAnchorLocalDist = double.NaN;
                // Interpolated anchor-local distance: same `frames[]` surface
                // as recordedAnchorLocalDist, but linearly interpolated at the
                // current playbackUT instead of clamped to the bracketing-
                // before entry. Use this to ask "is the rendered distance
                // tracking the recorded relative motion, or actually
                // diverging?" — the bracketing-before-only field is stable
                // across an entire bracket (e.g. the 0.6 s seed→first-sample
                // gap on fresh debris recordings) while the interpolated
                // value evolves with the recorded geometry.
                double interpolatedAnchorLocalDist = double.NaN;
                if (target.Section.referenceFrame == ReferenceFrame.Relative
                    && target.Section.frames != null
                    && target.Section.frames.Count > 0)
                {
                    int beforeIdx = -1;
                    for (int j = 0; j < target.Section.frames.Count; j++)
                    {
                        if (target.Section.frames[j].ut <= playbackUT)
                            beforeIdx = j;
                        else break;
                    }
                    if (beforeIdx >= 0)
                    {
                        TrajectoryPoint relFrame = target.Section.frames[beforeIdx];
                        Vector3d localOffset = new Vector3d(
                            relFrame.latitude, relFrame.longitude, relFrame.altitude);
                        recordedAnchorLocalDist = localOffset.magnitude;
                    }
                    interpolatedAnchorLocalDist = TraceSeparation
                        .InterpolateAnchorLocalOffsetMagnitude(
                            target.Section.frames, playbackUT);
                }
                // Recorded body-fixed distance: subtract the parent's
                // body-fixed primary world position at the bracketing UT from
                // the debris's body-fixed primary world. Computed
                // independently of playback math — ghost transforms drop out
                // entirely. Reflects what the recorder wrote into the two
                // independent body-fixed streams. If this doesn't match the
                // recorded anchor-local distance, the two recording surfaces
                // disagree about parent-vs-debris geometry.
                double recordedBodyFixedDist = double.NaN;
                if (beforeFrame.HasValue && !string.IsNullOrEmpty(beforeFrame.Value.bodyName)
                    && !string.IsNullOrWhiteSpace(traj.ParentAnchorRecordingId))
                {
                    CelestialBody body = FlightGlobals.Bodies?.Find(
                        b => b.name == beforeFrame.Value.bodyName);
                    Recording parentRec = RecordingStore.TryFindCommittedRecordingById(
                        traj.ParentAnchorRecordingId);
                    if (body != null
                        && parentRec != null
                        && parentRec.TrackSections != null)
                    {
                        // Find the parent's body-fixed primary sample bracketing playbackUT.
                        TrajectoryPoint? parentBefore = null;
                        for (int s = 0; s < parentRec.TrackSections.Count; s++)
                        {
                            var ps = parentRec.TrackSections[s];
                            if (ps.bodyFixedFrames == null || ps.bodyFixedFrames.Count == 0)
                                continue;
                            for (int j = 0; j < ps.bodyFixedFrames.Count; j++)
                            {
                                if (ps.bodyFixedFrames[j].ut <= playbackUT)
                                    parentBefore = ps.bodyFixedFrames[j];
                                else break;
                            }
                        }
                        if (parentBefore.HasValue)
                        {
                            Vector3d parentBodyFixedWorld = body.GetWorldSurfacePosition(
                                parentBefore.Value.latitude,
                                parentBefore.Value.longitude,
                                parentBefore.Value.altitude);
                            Vector3d debrisBodyFixedWorld = body.GetWorldSurfacePosition(
                                beforeFrame.Value.latitude,
                                beforeFrame.Value.longitude,
                                beforeFrame.Value.altitude);
                            recordedBodyFixedDist =
                                (debrisBodyFixedWorld - parentBodyFixedWorld).magnitude;
                        }
                    }
                }
                TraceSeparation.PlaybackLog("PositionDebris",
                    "recId=" + (target.RecordingId ?? "<null>") +
                    " parentRecId=" + (traj.ParentAnchorRecordingId ?? "<null>") +
                    " playbackUT=" + playbackUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    " bodyFixedCount=" + bodyFixedCount +
                    " first=" + firstPlaybackRender +
                    " bracketBefore=[" + beforeStr + "]" +
                    " bracketAfter=[" + afterStr + "]" +
                    " lerpAlpha=" + (double.IsNaN(lerpAlpha) ? "NaN" : lerpAlpha.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)) +
                    " ghostWorldBefore=" + TraceSeparation.FormatVector3d(ghostWorldBefore) +
                    " ghostWorld=" + TraceSeparation.FormatVector3d(ghostWorld) +
                    " worldStep=" + TraceSeparation.FormatVector3d(worldStep) +
                    " |worldStep|=" + worldStep.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    " predictedWorld=" + TraceSeparation.FormatVector3d(predictedWorld) +
                    " predictedVsActual=" + TraceSeparation.FormatVector3d(predictedVsActual) +
                    " |predDelta|=" + predictedVsActual.magnitude.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                    " parentGhostFound=" + parentGhostFound +
                    " parentGhostWorld=" + (parentGhostFound ? TraceSeparation.FormatVector3d(parentGhostWorld) : "<unresolved>") +
                    " renderedParentDist=" + (double.IsNaN(renderedParentDist) ? "NaN" : renderedParentDist.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)) +
                    " recordedAnchorLocalDist=" + (double.IsNaN(recordedAnchorLocalDist) ? "NaN" : recordedAnchorLocalDist.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)) +
                    " interpolatedAnchorLocalDist=" + (double.IsNaN(interpolatedAnchorLocalDist) ? "NaN" : interpolatedAnchorLocalDist.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)) +
                    " recordedBodyFixedDist=" + (double.IsNaN(recordedBodyFixedDist) ? "NaN" : recordedBodyFixedDist.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)));
            }
            // ---- /Trace-Sep ----
            // Fail closed when InterpolateAndPosition could not produce a
            // valid pose. See GhostPlaybackEngine.IsInterpolationResultValid
            // for the bodyName-null sentinel rationale. Without this guard
            // the engine would treat the route as ShadowPositioned and the
            // post-position pipeline's ActivateGhostVisualsIfNeeded would
            // re-show the ghost at its stale transform, defeating the
            // body-miss SetActive(false). The router falls back to Hidden
            // when this returns false.
            if (!GhostPlaybackEngine.IsInterpolationResultValid(interpResult))
            {
                // Restore the pre-call active state so the legacy hide path
                // (which sets the mesh inactive itself) starts from a known
                // baseline, and so a future SetActive(false) on the same
                // GameObject from the legacy path is observable in tests.
                if (state.ghost != null && state.ghost.activeSelf != ghostWasActive)
                    state.ghost.SetActive(ghostWasActive);
                bracketBeforeUT = double.NaN;
                bracketAfterUT = double.NaN;
                return false;
            }
            // Match the canonical positioner contract at :16653-16657:
            // positioner-with-state owns the SetInterpolated call so watch-mode
            // camera and FX paths read fresh lastInterpolatedBodyName / Altitude
            // / Velocity. Callers do NOT need to invoke SetInterpolated separately.
            state.SetInterpolated(interpResult);
            return true;
        }

        internal static bool BodyFixedPrimaryCoversPlaybackUT(
            TrackSection section,
            double playbackUT,
            out double firstBodyFixedUT,
            out double lastBodyFixedUT)
        {
            firstBodyFixedUT = double.NaN;
            lastBodyFixedUT = double.NaN;
            if (section.referenceFrame != ReferenceFrame.Relative
                || section.bodyFixedFrames == null
                || section.bodyFixedFrames.Count < 2)
            {
                return false;
            }

            int lastIdx = section.bodyFixedFrames.Count - 1;
            firstBodyFixedUT = section.bodyFixedFrames[0].ut;
            lastBodyFixedUT = section.bodyFixedFrames[lastIdx].ut;

            // Reject playback UTs outside the body-fixed primary's covered range.
            // Without this guard InterpolateAndPosition / TrajectoryMath.InterpolatePoints
            // would clamp at the nearest endpoint and silently freeze stale debris.
            const double coverageEpsilon = 1e-6;
            return playbackUT + coverageEpsilon >= firstBodyFixedUT
                && playbackUT - coverageEpsilon <= lastBodyFixedUT;
        }

        /// <summary>
        /// Find the bodyFixedFrames bracket [before, after] that brackets the
        /// playback UT. Endpoints clamp at the first / last sample. NaN is
        /// returned when the section has zero or one frames (the caller's
        /// log line will report no usable bracket).
        /// </summary>
        private static void ResolveBodyFixedPrimaryBracketUTs(
            System.Collections.Generic.List<TrajectoryPoint> bodyFixedFrames,
            double playbackUT,
            out double bracketBeforeUT,
            out double bracketAfterUT)
        {
            bracketBeforeUT = double.NaN;
            bracketAfterUT = double.NaN;
            if (bodyFixedFrames == null || bodyFixedFrames.Count < 2)
                return;
            // Linear scan is fine -- shadow lists are typically <200 frames per
            // section and this is only called on gate-fired frames.
            for (int i = 0; i < bodyFixedFrames.Count - 1; i++)
            {
                if (bodyFixedFrames[i].ut <= playbackUT
                    && bodyFixedFrames[i + 1].ut >= playbackUT)
                {
                    bracketBeforeUT = bodyFixedFrames[i].ut;
                    bracketAfterUT = bodyFixedFrames[i + 1].ut;
                    return;
                }
            }
            // Out of coverage: endpoints. Surface the closest pair so the log
            // still records what coverage we tried against.
            if (playbackUT < bodyFixedFrames[0].ut)
            {
                bracketBeforeUT = bodyFixedFrames[0].ut;
                bracketAfterUT = bodyFixedFrames[1].ut;
            }
            else
            {
                int last = bodyFixedFrames.Count - 1;
                bracketBeforeUT = bodyFixedFrames[last - 1].ut;
                bracketAfterUT = bodyFixedFrames[last].ut;
            }
        }

        void IGhostPositioner.PositionAtPoint(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, TrajectoryPoint point)
        {
            if (state?.ghost == null) return;

            // Bug #282: for Landed/Splashed recordings, clamp the rendered
            // altitude up so the ghost mesh sits above terrain. The recording's
            // raw altitude is the root-part origin — for a Mk1-3 pod the lowest
            // mesh vertex is ~1.77 m below the root, so any recorded clearance
            // under ~2 m buries the pod. Applies only to the PositionAtPoint
            // call sites (past-end hold, loop cycle boundary, loop pause hold,
            // overlap expired): these are the "stuck at final point" paths.
            // Normal in-flight playback already gets the distance-aware terrain
            // floor from ClampGhostsToTerrain in LateUpdate; mirror that same
            // minimum here so long-range watched landed ghosts do not sink below
            // the visual terrain right as playback reaches its final point.
            TrajectoryPoint positioned = point;
            // P1 review (PR #706): when ApplyLandedGhostClearance rewrites the
            // altitude to currentTerrain + clearance, mark the altitude as
            // authoritative so PositionGhostAt skips tail-lift. Otherwise the
            // wrapper would add Δ on top of the already-clamped altitude and
            // produce currentTerrain + clearance + Δ — the same end-of-descent
            // pop the tail-lift was meant to remove, in the opposite direction.
            bool altitudeAuthoritative = false;
            if (traj != null)
            {
                if (ShouldApplyImmediatePointSurfaceClearance(
                        traj.TerminalStateValue,
                        traj.SurfacePos.HasValue))
                {
                    positioned = ApplyLandedGhostClearance(
                        point, index, traj.VesselName, traj.TerrainHeightAtEnd,
                        ResolveImmediateLandedGhostClearanceMeters(
                            index, traj.VesselName,
                            point.bodyName, point.latitude, point.longitude, point.altitude));
                    altitudeAuthoritative = true;
                }
            }

            activeBodyFixedShiftSeconds = state.bodyFixedShiftSeconds;
            try
            {
                PositionGhostAt(state.ghost, positioned, traj?.RecordingId, altitudeAuthoritative);
            }
            finally
            {
                activeBodyFixedShiftSeconds = 0.0;
            }

            // Keep the watch-mode overlay + diagnostics consistent with the
            // clamped position. WatchModeController reads state.lastInterpolatedAltitude
            // for its "ghost at alt N m on Kerbin" line — if we don't update it,
            // the log reports the buried raw altitude even after the visual is fixed.
            state.SetInterpolated(new InterpolationResult(
                positioned.velocity,
                positioned.bodyName,
                positioned.altitude));
        }

        bool IGhostPositioner.TryResolveExplosionAnchorPosition(int index,
            IPlaybackTrajectory traj, GhostPlaybackState state, out Vector3 worldPosition)
        {
            if (state?.ghost == null)
            {
                worldPosition = Vector3.zero;
                string missingGhostVesselName = traj != null && !string.IsNullOrEmpty(traj.VesselName)
                    ? traj.VesselName
                    : state?.vesselName ?? "?";
                ParsekLog.Warn("TerrainCorrect",
                    $"Explosion anchor resolve #{index} (\"{missingGhostVesselName}\") skipped: ghost root is null");
                return false;
            }

            Vector3 rawWorldPos = state.ghost.transform.position;
            worldPosition = rawWorldPos;
            string safeVesselName = traj != null && !string.IsNullOrEmpty(traj.VesselName)
                ? traj.VesselName
                : state.vesselName ?? "?";
            string bodyName = ResolveExplosionAnchorBodyName(
                state.lastInterpolatedBodyName, traj);
            if (string.IsNullOrEmpty(bodyName))
                return true;

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            if (body == null || body.pqsController == null)
                return true;

            double latitude = body.GetLatitude(rawWorldPos);
            double longitude = body.GetLongitude(rawWorldPos);
            double rawAltitude = body.GetAltitude(rawWorldPos);
            double terrainHeight = body.TerrainAltitude(latitude, longitude, true);

            double minClearance = ImmediateLandedGhostClearanceFallbackMeters;
            if (FlightGlobals.ActiveVessel != null)
            {
                double distanceToVessel = Vector3d.Distance(
                    rawWorldPos, FlightGlobals.ActiveVessel.GetWorldPos3D());
                minClearance = ComputeTerrainClearance(distanceToVessel);
            }
            else
            {
                ParsekLog.Verbose("TerrainCorrect",
                    $"Explosion anchor clearance fallback #{index} (\"{safeVesselName}\"): " +
                    $"active vessel unavailable — using legacy floor " +
                    $"{ImmediateLandedGhostClearanceFallbackMeters.ToString("F1", CultureInfo.InvariantCulture)}m");
            }

            double clampedAltitude = TerrainCorrector.ClampAltitude(
                rawAltitude, terrainHeight, minClearance);
            double resolvedAltitude = clampedAltitude > rawAltitude ? clampedAltitude : rawAltitude;
            worldPosition = clampedAltitude > rawAltitude
                ? body.GetWorldSurfacePosition(latitude, longitude, clampedAltitude)
                : rawWorldPos;

            // Keep the watch overlay / diagnostics aligned with the anchor we
            // actually returned, not just the "had to clamp" branch.
            state.lastInterpolatedBodyName = body.name;
            state.lastInterpolatedAltitude = resolvedAltitude;

            if (clampedAltitude <= rawAltitude)
                return true;

            string cycleText = state.loopCycleIndex >= 0
                ? $" cycle={state.loopCycleIndex}"
                : "";
            ParsekLog.Verbose("TerrainCorrect",
                $"Explosion anchor clamp #{index} (\"{safeVesselName}\"): " +
                $"alt={rawAltitude.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"terrain={terrainHeight.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"-> {clampedAltitude.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"(clearance={minClearance.ToString("F1", CultureInfo.InvariantCulture)}m){cycleText}");
            return true;
        }

        /// <summary>
        /// Pure decision helper: given whether a <see cref="CelestialBody"/>
        /// resolved for the ghost's body name and whether an active vessel is
        /// present, returns the reason the fallback fires (for logging), or
        /// <c>null</c> when both inputs are live and the distance-aware path
        /// can run. Extracted for unit testing without KSP singletons (#373).
        /// </summary>
        internal static string ResolveImmediateLandedGhostClearanceFallbackReason(
            bool hasBody, bool hasActiveVessel)
        {
            if (!hasBody && !hasActiveVessel) return "no-body-and-no-active-vessel";
            if (!hasBody) return "no-body";
            if (!hasActiveVessel) return "no-active-vessel";
            return null;
        }

        internal static string ResolveExplosionAnchorBodyName(
            string lastInterpolatedBodyName, IPlaybackTrajectory traj)
        {
            if (!string.IsNullOrEmpty(lastInterpolatedBodyName))
                return lastInterpolatedBodyName;
            return RecordingEndpointResolver.TryGetPreferredEndpointBodyName(
                traj, out string bodyName)
                ? bodyName
                : null;
        }

        private static double ResolveImmediateLandedGhostClearanceMeters(
            int index, string vesselName,
            string bodyName, double latitude, double longitude, double altitude)
        {
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            bool hasBody = body != null;
            bool hasActiveVessel = FlightGlobals.ActiveVessel != null;
            string fallbackReason = ResolveImmediateLandedGhostClearanceFallbackReason(
                hasBody, hasActiveVessel);
            if (fallbackReason != null)
            {
                // #373: Silent regression to the legacy 0.5 m floor when the
                // distance-aware resolver cannot run. Rate-limit per
                // ghost+reason so scene-transition cold-start paths surface
                // without spamming the log on every frame.
                string safeVessel = string.IsNullOrEmpty(vesselName) ? "?" : vesselName;
                string safeBody = string.IsNullOrEmpty(bodyName) ? "?" : bodyName;
                ParsekLog.WarnRateLimited("TerrainCorrect",
                    $"landed-ghost-clearance-fallback-{index}-{fallbackReason}",
                    $"Landed ghost clearance fallback #{index} (\"{safeVessel}\") on body='{safeBody}': " +
                    $"{fallbackReason} — using legacy floor " +
                    $"{ImmediateLandedGhostClearanceFallbackMeters.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}m " +
                    "(distance-aware #262 path skipped)");
                return ImmediateLandedGhostClearanceFallbackMeters;
            }

            Vector3d pointWorld = body.GetWorldSurfacePosition(latitude, longitude, altitude);
            double distToVessel = Vector3d.Distance(pointWorld, FlightGlobals.ActiveVessel.GetWorldPos3D());
            return ComputeTerrainClearance(distToVessel);
        }

        internal static double ResolveNaNFallbackLandedGhostClearanceMeters(double minClearanceMeters)
        {
            return System.Math.Max(
                VesselSpawner.LandedGhostClearanceMeters,
                System.Math.Max(0.5, minClearanceMeters));
        }

        internal static bool ShouldApplyImmediateSurfacePositionClearance(
            TerminalState? terminalState,
            double recordedTerrainHeight,
            bool hasSurfacePosition)
        {
            return ShouldApplyImmediatePointSurfaceClearance(terminalState, hasSurfacePosition)
                || !double.IsNaN(recordedTerrainHeight);
        }

        internal static bool ShouldApplyImmediatePointSurfaceClearance(
            TerminalState? terminalState,
            bool hasSurfacePosition)
        {
            if (terminalState == TerminalState.Landed
                || terminalState == TerminalState.Splashed)
                return true;

            // A Recovered recording can still be a surface terminal recording
            // when Parsek captured the landed/splashed terminal pose before KSP
            // removed the vessel. Treat only those recovered recordings as
            // surface ghosts; orbit/flight recoveries do not get a terrain clamp.
            return terminalState == TerminalState.Recovered && hasSurfacePosition;
        }

        /// <summary>
        /// Position a surface-terminal ghost. The recorded altitude is the TRUTH —
        /// the vessel was literally at that world-space position when the terminal
        /// state was determined. For vessels recorded on mesh objects (Island
        /// Airfield, launchpad, KSC buildings), the altitude encodes the airfield
        /// surface height, which <c>body.TerrainAltitude()</c> cannot see (PQS-only).
        /// The old code applied terrain-relative correction via
        /// <c>ComputeCorrectedAltitude</c> which pulled mesh-object ghosts down to
        /// raw PQS terrain, burying them under the runway.
        ///
        /// <para>New behavior: preserve the recorded altitude. The only correction
        /// is an underground safety floor — push up if the recorded altitude is
        /// below the current PQS terrain floor. The caller supplies the minimum
        /// clearance to use for the current playback context; long-range watched
        /// ghosts use the same distance-aware floor as <c>ClampGhostsToTerrain</c>.
        /// For NaN-recorded-terrain legacy recordings we keep the historical 4 m
        /// minimum clearance above PQS, but allow callers to request a larger
        /// distance-aware floor at extreme watch ranges.</para>
        ///
        /// <para>Ghosts are kinematic — no physics damage concern — so the safety
        /// floor defaults to 0.5 m when the caller has no better context.
        /// <c>internal static</c> so in-game tests can exercise it without
        /// needing a full <c>IGhostPositioner</c> chain.</para>
        /// </summary>
        internal static TrajectoryPoint ApplyLandedGhostClearance(
            TrajectoryPoint p, int index, string vesselName, double recordedTerrainHeight,
            double minClearanceMeters = 0.5)
        {
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == p.bodyName);
            if (body == null)
                return p;
            if (body.pqsController == null)
            {
                ParsekLog.WarnRateLimited("TerrainCorrect",
                    $"pqs-unready-{body.name}",
                    $"Landed ghost clamp #{index} (\"{vesselName}\"): PQS not spun up " +
                    $"for body '{body.name}' — leaving recorded alt unchanged");
                return p;
            }

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            double pqsTerrain = body.TerrainAltitude(p.latitude, p.longitude, true);

            double floorClearance = System.Math.Max(0.5, minClearanceMeters);

            // Legacy (NaN) path: no recorded surface height — keep the historical
            // 4 m fallback floor, but honor a larger caller-requested clearance so
            // long-range watched ghosts do not clip near the edge of visual range.
            if (double.IsNaN(recordedTerrainHeight))
            {
                double legacyClearance = ResolveNaNFallbackLandedGhostClearanceMeters(minClearanceMeters);
                double legacyTarget = pqsTerrain + legacyClearance;
                if (p.altitude >= legacyTarget)
                    return p;
                // delta is always > 0 here because we already verified p.altitude < legacyTarget.
                double delta = legacyTarget - p.altitude;
                ParsekLog.VerboseRateLimited("TerrainCorrect", $"landed-ghost-{index}",
                    $"Landed ghost clamp #{index} (\"{vesselName}\"): " +
                    $"alt={p.altitude.ToString("F1", ic)} pqsTerrain={pqsTerrain.ToString("F1", ic)} " +
                    $"-> {legacyTarget.ToString("F1", ic)} (delta=+{delta.ToString("F2", ic)}m, " +
                    $"body={body.name}, NaN fallback, clearance={legacyClearance.ToString("F1", ic)}m)");
                p.altitude = legacyTarget;
                return p;
            }

            // Primary path: trust the recorded altitude. Only push up if the
            // recorded altitude is below the current PQS terrain floor — which
            // only happens when PQS terrain has shifted UP since recording.
            double safetyFloor = pqsTerrain + floorClearance;
            if (p.altitude >= safetyFloor)
                return p;

            double upDelta = safetyFloor - p.altitude;
            ParsekLog.VerboseRateLimited("TerrainCorrect", $"landed-ghost-{index}",
                $"Landed ghost clamp #{index} (\"{vesselName}\"): " +
                $"alt={p.altitude.ToString("F1", ic)} pqsTerrain={pqsTerrain.ToString("F1", ic)} " +
                $"-> {safetyFloor.ToString("F1", ic)} (delta=+{upDelta.ToString("F2", ic)}m, " +
                $"body={body.name}, below-pqs-floor, clearance={floorClearance.ToString("F1", ic)}m, " +
                $"recSurface={recordedTerrainHeight.ToString("F1", ic)})");
            p.altitude = safetyFloor;
            return p;
        }

        void IGhostPositioner.PositionAtSurface(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state)
        {
            if (state?.ghost == null || traj?.SurfacePos == null) return;

            SurfacePosition positioned = traj.SurfacePos.Value;
            if (ShouldApplyImmediateSurfacePositionClearance(
                    traj.TerminalStateValue,
                    traj.TerrainHeightAtEnd,
                    traj.SurfacePos.HasValue))
            {
                var syntheticPoint = new TrajectoryPoint
                {
                    latitude = positioned.latitude,
                    longitude = positioned.longitude,
                    altitude = positioned.altitude,
                    rotation = positioned.rotation,
                    bodyName = positioned.body,
                    // Phase 7: synthetic terminal point for landed ghost clamp;
                    // playback path uses recorded altitude directly, no clearance.
                    recordedGroundClearance = double.NaN,
                };
                syntheticPoint = ApplyLandedGhostClearance(
                    syntheticPoint, index, traj.VesselName, traj.TerrainHeightAtEnd,
                    ResolveImmediateLandedGhostClearanceMeters(
                        index, traj.VesselName,
                        positioned.body, positioned.latitude, positioned.longitude, positioned.altitude));
                positioned.altitude = syntheticPoint.altitude;
            }

            PositionGhostAtSurface(state.ghost, positioned, ShouldAutoActivateGhost(state),
                traj?.RecordingId);
            // Surface holds are stationary. Keep watch-mode diagnostics and
            // downstream state consumers from inheriting stale in-flight velocity.
            state.SetInterpolated(new InterpolationResult(
                Vector3.zero,
                positioned.body,
                positioned.altitude).WithSurfaceVelocity(
                    Vector3.zero, GhostSurfaceVelocitySource.SurfaceHold));
        }

        void IGhostPositioner.PositionFromOrbit(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut)
        {
            if (state?.ghost == null || traj?.OrbitSegments == null) return;
            bool foundSegment = GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment(
                traj, ut, out OrbitSegment seg, out int segmentIndex);
            if (!foundSegment)
            {
                for (int i = 0; i < traj.OrbitSegments.Count; i++)
                {
                    OrbitSegment candidate = traj.OrbitSegments[i];
                    bool inRange = i == traj.OrbitSegments.Count - 1
                        ? ut >= candidate.startUT && ut <= candidate.endUT
                        : ut >= candidate.startUT && ut < candidate.endUT;
                    if (!inRange)
                        continue;

                    seg = candidate;
                    segmentIndex = i;
                    foundSegment = true;
                    break;
                }
            }

            if (foundSegment)
            {
                if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg))
                    return;

                if (PlaybackOrbitDiagnostics.TryBuildPlaybackPredictedTailLog(
                    index, traj, seg, ut, out string logKey, out string logMessage))
                {
                    ParsekLog.VerboseRateLimited("Playback", logKey, logMessage, 1.0);
                }

                long orbitTailCacheKey = index * 10000 + Math.Max(0, segmentIndex);
                PositionGhostFromOrbit(
                    state.ghost,
                    seg,
                    ut,
                    orbitTailCacheKey,
                    traj.RecordingId,
                    traj);
                SetOrbitTailInterpolated(state, seg, ut, orbitTailCacheKey);

                // Re-aim seam/arrival diagnostic: trace the orbit-only re-aim ghost vs the live bodies so a
                // teleport at a segment seam or an arrival where the target is NOT is visible in the log.
                if (state.ghost != null)
                {
                    // The transform read stays inside the gated arms so a
                    // tracing-off faithful ghost costs zero extra ECalls.
                    bool isReaim = traj is Parsek.Reaim.ReaimedTrajectory;
                    bool seamTracing = ParsekSettings.Current != null
                        && ParsekSettings.Current.ghostRenderTracing;
                    if (isReaim || seamTracing)
                    {
                        Vector3 ghostWorldPos = state.ghost.transform.position;
                        if (isReaim)
                            LogReaimGhostTrace(index, seg, segmentIndex, ut, ghostWorldPos);
                        // Mode-AGNOSTIC Tier-C seam-teleport instrument: unlike the
                        // [ReaimSeam] verbose trace above, this samples FAITHFUL
                        // members too, so a faithful-vs-re-aim A/B measures both
                        // modes' seam behavior with the same predicate.
                        if (seamTracing)
                            TrackLoopSeamTeleport(index, traj, seg, segmentIndex, ut, ghostWorldPos);
                    }
                }
            }
        }

        /// <summary>
        /// Orbit-tail playback positions the ghost from an orbit segment and writes no
        /// interpolation result of its own, so without this the wheel, reentry FX and watch
        /// readers would keep the last point frame's velocities. Mirrors the orbit-segment
        /// branch of the point playback: the segment's orbital velocity, and the surface
        /// velocity derived from it (source Orbit).
        /// </summary>
        private void SetOrbitTailInterpolated(
            GhostPlaybackState state, OrbitSegment seg, double ut, long cacheKey)
        {
            if (state?.ghost == null || orbitCache == null
                || !orbitCache.TryGetValue(cacheKey, out Orbit orbit) || orbit == null)
                return;
            CelestialBody body = orbit.referenceBody;
            if (body == null)
                return;
            Vector3d zupVelocity = orbit.getOrbitalVelocityAtUT(ut);
            Vector3d ghostWorldPos = state.ghost.transform.position;
            var interp = new InterpolationResult(
                (Vector3)zupVelocity, seg.bodyName, body.GetAltitude(ghostWorldPos))
                .WithSurfaceVelocity(
                    GhostSurfaceVelocity.FromOrbitVelocity(zupVelocity, body.getRFrmVel(ghostWorldPos)),
                    GhostSurfaceVelocitySource.Orbit);
            state.SetInterpolated(interp);
        }

        /// <summary>
        /// Loop-seam teleport tracker: on an orbit-segment change, measure the
        /// ghost's world-position discontinuity and raise the Tier-C
        /// <c>loop-seam-teleport</c> anomaly through
        /// <see cref="GhostRenderTrace.EmitAnomaly"/> when
        /// <see cref="GhostRenderTrace.IsLoopSeamTeleport"/> says the jump is
        /// defect-class (floor 1,000 km vs the ~7-10 km healthy seams and the
        /// ~1-SOI-radius defect population). Cheap early-out when
        /// ghostRenderTracing is off; never throws.
        /// </summary>
        private void TrackLoopSeamTeleport(int memberIndex, IPlaybackTrajectory traj,
            OrbitSegment seg, int segmentIndex, double loopUT, Vector3 ghostPos)
        {
            try
            {
                if (ParsekSettings.Current == null || !ParsekSettings.Current.ghostRenderTracing)
                    return;
                var ic = CultureInfo.InvariantCulture;
                double nowUT = Planetarium.GetUniversalTime();
                int frame = Time.frameCount;
                if (loopSeamLastGhostFrame.TryGetValue(memberIndex, out var last)
                    && last.segIdx != segmentIndex)
                {
                    double jump = (ghostPos - last.pos).magnitude;
                    CelestialBody segBody = FlightGlobals.Bodies?.Find(
                        b => b != null && b.bodyName == seg.bodyName);
                    double bodySpeed = segBody != null && segBody.orbit != null
                        ? segBody.orbit.orbitalSpeed
                        : 0.0;
                    if (GhostRenderTrace.IsLoopSeamTeleport(
                            jump,
                            bodySpeed + LoopSeamGhostSpeedAllowanceMps,
                            nowUT - last.currentUT,
                            frame,
                            last.frame,
                            ReFlySettleStabilityTracker.LastFloatingOriginShiftFrame,
                            loopUT - last.loopUT))
                    {
                        double soi = segBody != null ? segBody.sphereOfInfluence : double.NaN;
                        GhostRenderTrace.EmitAnomaly(
                            traj.RecordingId, memberIndex, nowUT, loopUT,
                            "loop-seam-teleport",
                            "member=" + memberIndex.ToString(ic)
                            + " seg=" + last.segIdx.ToString(ic) + "->" + segmentIndex.ToString(ic)
                            + " body=" + (seg.bodyName ?? "<none>")
                            + " jump=" + jump.ToString("F0", ic) + "m"
                            + " soi=" + soi.ToString("F0", ic) + "m"
                            + " reaim=" + (traj is Parsek.Reaim.ReaimedTrajectory ? "true" : "false"));
                    }
                }
                loopSeamLastGhostFrame[memberIndex] = (segmentIndex, ghostPos, loopUT, nowUT, frame);
            }
            catch { /* diagnostic only */ }
        }

        /// <summary>
        /// Diagnostic trace of a re-aimed orbit ghost vs the live bodies. Logs (rate-limited) the ghost's
        /// world position and its distance to the segment's own body, the launch body, and the target, so
        /// "arrives where the target is not" is measurable. On a SEGMENT CHANGE (a trajectory seam) it logs
        /// the world-position discontinuity between the prior frame and this one - a large jump is a
        /// teleport (no trajectory continuity across the seam). Verbose; never throws.
        /// </summary>
        private void LogReaimGhostTrace(int memberIndex, OrbitSegment seg, int segmentIndex, double loopUT, Vector3 ghostPos)
        {
            try
            {
                var ic = CultureInfo.InvariantCulture;
                double currentUT = Planetarium.GetUniversalTime();
                CelestialBody segBody = FlightGlobals.Bodies?.Find(b => b != null && b.bodyName == seg.bodyName);
                CelestialBody target = null, launch = null;
                if (cachedLoopUnits != null && cachedLoopUnits.TryGetUnitForMember(memberIndex, out GhostPlaybackLogic.LoopUnit unit)
                    && unit.ReaimPlan.HasValue)
                {
                    target = FlightGlobals.Bodies?.Find(b => b != null && b.bodyName == unit.ReaimPlan.Value.TargetBody);
                    launch = FlightGlobals.Bodies?.Find(b => b != null && b.bodyName == unit.ReaimPlan.Value.LaunchBody);
                }
                double dSeg = segBody != null ? (ghostPos - (Vector3)segBody.position).magnitude : double.NaN;
                double dLaunch = launch != null ? (ghostPos - (Vector3)launch.position).magnitude : double.NaN;
                double dTarget = target != null ? (ghostPos - (Vector3)target.position).magnitude : double.NaN;

                // Seam-teleport detection: if the segment changed since the last frame, log the jump.
                if (reaimLastGhostFrame.TryGetValue(memberIndex, out var last) && last.segIdx != segmentIndex)
                {
                    double jump = (ghostPos - last.pos).magnitude;
                    ParsekLog.Verbose("ReaimSeam",
                        $"SEAM member={memberIndex} seg#{last.segIdx}->{segmentIndex} body={seg.bodyName} " +
                        $"loopUT {last.loopUT.ToString("F0", ic)}->{loopUT.ToString("F0", ic)} " +
                        $"jump={jump.ToString("F0", ic)}m (prevPos->thisPos discontinuity)");
                }
                reaimLastGhostFrame[memberIndex] = (segmentIndex, ghostPos, loopUT);

                ParsekLog.VerboseRateLimited("ReaimSeam", "trace-" + memberIndex.ToString(ic),
                    $"member={memberIndex} seg#{segmentIndex} body={seg.bodyName} loopUT={loopUT.ToString("F0", ic)} " +
                    $"currentUT={currentUT.ToString("F0", ic)} dist[{seg.bodyName}]={dSeg.ToString("F0", ic)}m " +
                    $"dist[{(launch != null ? launch.bodyName : "launch")}]={dLaunch.ToString("F0", ic)}m " +
                    $"dist[{(target != null ? target.bodyName : "target")}]={dTarget.ToString("F0", ic)}m" +
                    (target != null ? $"(SOI={target.sphereOfInfluence.ToString("F0", ic)})" : ""), 2.0);
            }
            catch { /* diagnostic only */ }
        }

        void IGhostPositioner.PositionLoop(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state, double ut, bool suppressFx)
        {
            if (state?.ghost == null || traj == null) return;
            int playbackIdx = state.playbackIndex;
            bool useAnchor = GhostPlaybackLogic.ShouldUseLoopAnchor(traj);
            InterpolationResult interpResult;
            // PR #594 P1 review fix: pass `state` so the relative-frame
            // retirement branch can set state.anchorRetiredThisFrame for
            // the loop-playback path. Same rationale as in
            // IGhostPositioner.InterpolateAndPositionRelative above.
            activeBodyFixedShiftSeconds = state.bodyFixedShiftSeconds;
            try
            {
                PositionLoopGhost(state.ghost, traj, ref playbackIdx, ut,
                    index, index * 10000, useAnchor, state,
                    ShouldAutoActivateGhost(state), out interpResult);
            }
            finally
            {
                activeBodyFixedShiftSeconds = 0.0;
            }
            state.SetInterpolated(interpResult);
            state.playbackIndex = playbackIdx;
        }

        ZoneRenderingResult IGhostPositioner.ApplyZoneRendering(int index, GhostPlaybackState state,
            IPlaybackTrajectory traj, double distance, double playbackUT, int protectedIndex)
        {
            // Delegates to the private ApplyZoneRendering which has the same signature
            return ApplyZoneRenderingImpl(index, state, traj, distance, playbackUT, protectedIndex);
        }

        void IGhostPositioner.ClearOrbitCache()
        {
            ClearOrbitPlaybackCaches();
        }

        // CreateGhostSphere moved to GhostVisualBuilder.CreateGhostSphere (T25 Phase 4 prep)

        void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points, ref int cachedIndex,
            double targetUT, bool allowActivation, out InterpolationResult interpResult,
            string recordingId = null, int sectionIndex = -1,
            string pointFrameSource = "input",
            List<TrackSection> flatPointSections = null)
        {
            TrajectoryPoint before, after;
            float t;
            bool hasSegment = TrajectoryMath.InterpolatePoints(
                points, ref cachedIndex, targetUT, out before, out after, out t);

            // A flat Points read can land on a Relative section's anchor-local metre
            // offsets (a section gap, or a span no section covers). Those are not
            // lat/lon/alt: route them through the section's body-fixed shadow, or refuse.
            // Without a segment only `before` is meaningful (`after` is default).
            if (!hasSegment)
                after = before;
            if (flatPointSections != null
                && points != null
                && points.Count > 0
                && !TryDispatchFlatPointEndpoints(
                    flatPointSections, ref before, ref after, targetUT, recordingId, pointFrameSource))
            {
                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
                return;
            }

            if (!hasSegment)
            {
                // Either empty list or before recording start
                if (points == null || points.Count == 0)
                {
                    interpResult = InterpolationResult.Zero;
                    ghost.SetActive(false);
                    return;
                }
                PositionGhostAt(ghost, before, recordingId);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, before.altitude);
                return;
            }

            // Degenerate segment (t == 0 from zero-duration segment)
            if (t == 0f && before.ut == after.ut)
            {
                PositionGhostAt(ghost, before, recordingId);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, before.altitude);
                return;
            }

            CelestialBody bodyBefore = FlightGlobals.Bodies.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies.Find(b => b.name == after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
            {
                Log($"Could not find body: {(bodyBefore == null ? before.bodyName : after.bodyName)}");
                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
                return;
            }
            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            // Phase 7 (design doc §13.1, §18 Phase 7): when both endpoints
            // carry a recorded ground clearance (surface section in a v9+
            // recording), substitute the interpolated altitude with
            // current_terrain + clearance so the rendered ghost stays at
            // constant clearance even when KSP regenerates terrain mesh
            // between sessions. NaN sentinel on either endpoint ⇒ silent
            // fall-through to the legacy altitude path (HR-9).
            //
            // referenceFrame=Absolute: this method's callers route RELATIVE
            // sections through IGhostPositioner.InterpolateAndPositionRelative,
            // and OrbitalCheckpoint sections through
            // TryInterpolateAndPositionCheckpointSection (orbit propagation),
            // so the points reaching here are always Absolute-frame body-
            // fixed lat/lon/alt. The explicit argument is the P2-1 safety
            // gate at the helper.
            double effectiveAltBefore = ResolveEffectiveAltitudeWithTailLift(
                bodyBefore, before.latitude, before.longitude, before.altitude,
                before.recordedGroundClearance, ReferenceFrame.Absolute,
                before.ut, recordingId);
            double effectiveAltAfter = ResolveEffectiveAltitudeWithTailLift(
                bodyAfter, after.latitude, after.longitude, after.altitude,
                after.recordedGroundClearance, ReferenceFrame.Absolute,
                after.ut, recordingId);

            // M4b body-fixed derotation (identity when activeBodyFixedShiftSeconds == 0, every
            // non-knob path): resolve at the shifted longitude so a vacuum point replayed off the
            // rotation-aligned grid lands at its recorded INERTIAL position instead of riding the
            // planet's extra rotation.
            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude,
                TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(
                    before.longitude, activeBodyFixedShiftSeconds, bodyBefore),
                effectiveAltBefore);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude,
                TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(
                    after.longitude, activeBodyFixedShiftSeconds, bodyAfter),
                effectiveAltAfter);

            Vector3d rawLerpPos = Vector3d.Lerp(posBefore, posAfter, t);
            Vector3d interpolatedPos = rawLerpPos;
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);

            interpolatedRot = TrajectoryMath.SanitizeQuaternion(interpolatedRot);

            if (double.IsNaN(interpolatedPos.x) || double.IsNaN(interpolatedPos.y) || double.IsNaN(interpolatedPos.z))
            {
                Log("Warning: NaN in interpolated position, using 'before' position");
                interpolatedPos = posBefore;
            }

            bool allowSplineForPlayback = allowSplinePositioningForPlayback(
                false,
                allowNormalPlaybackSplinePositioning: NormalPlaybackSplinePositioningEnabled,
                out string splineReason);

            // Phase 1 smoothing splines (design doc §6.1, §17.3.1, §18 Phase 1).
            // Replace the lerped lat/lon/alt-derived world position with a
            // Catmull-Rom spline evaluation only when the playback-mode gate
            // allows it, the section has a valid spline, and the rollout flag
            // is on. Normal Watch/loop playback stays on the historical
            // section-frame lerp unless a future product gate explicitly
            // enables spline-positioned playback. Rotation continues to come
            // from the existing slerp — Phase 1 is position-only. Any
            // precondition miss (no recordingId, no sectionIndex, flag off,
            // store miss, or !IsValid) silently falls through to the legacy
            // lerp result — that is acceptable per HR-9 because "no spline
            // yet" is a normal state, not a failure.
            bool splineApplied = false;
            if (allowSplineForPlayback
                && allowSplinePositioning(recordingId, sectionIndex, out Parsek.Rendering.SmoothingSpline spline))
            {
                RecordSplineEvalForLogging();
                Vector3d splineLatLonAlt = TrajectoryMath.CatmullRomFit.Evaluate(spline, targetUT);
                if (!double.IsNaN(splineLatLonAlt.x) && !double.IsNaN(splineLatLonAlt.y) && !double.IsNaN(splineLatLonAlt.z))
                {
                    // Phase 4 frame-aware dispatch (design doc §6.2 Stage 2,
                    // §18 Phase 4, §26.1 HR-9). FrameTag pins the coordinate
                    // contract of the spline's controls — body-fixed (0) hands
                    // straight to GetWorldSurfacePosition; inertial-longitude
                    // (1) re-lowers via FrameTransform.LowerFromInertialToWorld
                    // at the playback UT so the body's rotation between
                    // recording and playback is correctly cancelled out. An
                    // unrecognised tag emits Warn (per HR-9) gated by
                    // unknownFrameTagWarned so a degenerate recording can't
                    // flood the log — see DispatchSplineWorldByFrameTag.
                    Vector3d splineWorld = TrajectoryMath.FrameTransform.DispatchSplineWorldByFrameTag(
                        spline.FrameTag,
                        splineLatLonAlt.x, splineLatLonAlt.y, splineLatLonAlt.z,
                        bodyBefore, targetUT,
                        recordingId, sectionIndex,
                        unknownFrameTagWarned);
                    if (!double.IsNaN(splineWorld.x) && !double.IsNaN(splineWorld.y) && !double.IsNaN(splineWorld.z))
                    {
                        interpolatedPos = splineWorld;
                        splineApplied = true;
                        splineReason = "applied";
                    }
                }
            }

            // Velocity-aware PointInterp was introduced during Re-Fly work, but it
            // changes the visible path between recorded samples. Keep normal Watch
            // and loop playback on the historical section-frame lerp unless a
            // future product gate explicitly enables velocity-curved playback.
            // Active Re-Fly recorded-coordinate playback also stays linear.
            bool hermiteApplied = false;
            Vector3d rawHermitePos = Vector3d.zero;
            double hermiteDeviationMeters = double.NaN;
            double hermiteMaxDeviationMeters = ComputePointHermiteMaxDeviationMeters(posBefore, posAfter);
            bool allowHermite = allowPointHermiteInterpolation(
                false,
                splineApplied,
                allowNormalPlaybackHermite: NormalPlaybackPointHermiteEnabled,
                out string hermiteReason);
            if (allowHermite)
            {
                if (TrajectoryMath.TryInterpolateWorldHermite(
                        posBefore,
                        before.velocity,
                        posAfter,
                        after.velocity,
                        after.ut - before.ut,
                        t,
                        hermiteMaxDeviationMeters,
                        out rawHermitePos,
                        out hermiteDeviationMeters,
                        out hermiteReason))
                {
                    interpolatedPos = rawHermitePos;
                    hermiteApplied = true;
                }
            }

            // Phase 2 + Phase 3 anchor correction (design doc §6.3 Stage 3,
            // §6.4 multi-anchor lerp, §7.1, §8, §18 Phase 2-3 / HR-15).
            // Additive world-space ε translation pinned once at session entry;
            // the lookup reads the cached AnchorCorrection.Epsilon values and
            // never touches live vessel state. Phase 3 evaluates ε at the
            // current playback UT — a both-end interval lerps linearly per
            // §6.4, single-side intervals return constant ε. Any miss (no
            // recordingId, no section, flag off, no anchor in the store) is
            // a silent fall-through — re-fly sessions with no marker yet are
            // normal state, not failure.
            bool hasAnchorEps = allowRenderAnchorCorrectionInterval(
                recordingId, sectionIndex, targetUT, false,
                out Vector3d anchorEps, out string anchorCorrectionReason);
            if (hasAnchorEps)
                interpolatedPos += anchorEps;

            ghost.transform.position = interpolatedPos;
            ghost.transform.rotation = bodyBefore.bodyTransform.rotation
                * TrajectoryMath.FrameTransform.ShiftSurfaceRelativeRotation(
                    interpolatedRot, activeBodyFixedShiftSeconds, bodyBefore);

            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT;
            if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    recordingId,
                    -1,
                    traceCurrentUT,
                    targetUT,
                    "UpdatePath",
                    "mode=PointInterp"
                    + " pointFrameSource=" + (pointFrameSource ?? "input")
                    + " sectionIndex=" + sectionIndex.ToString(CultureInfo.InvariantCulture)
                    + " beforeUT=" + before.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " afterUT=" + after.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " t=" + t.ToString("F4", CultureInfo.InvariantCulture)
                    + " rawBefore=" + GhostRenderTrace.FormatVector3d(posBefore)
                    + " rawAfter=" + GhostRenderTrace.FormatVector3d(posAfter)
                    + " rawLerp=" + GhostRenderTrace.FormatVector3d(rawLerpPos)
                    + " rawHermite=" + GhostRenderTrace.FormatVector3d(hermiteApplied ? rawHermitePos : Vector3d.zero)
                    + " hermiteHit=" + (hermiteApplied ? "true" : "false")
                    + " hermiteReason=" + (hermiteReason ?? "<none>")
                    + " hermiteDeviationMeters=" + hermiteDeviationMeters.ToString("F2", CultureInfo.InvariantCulture)
                    + " hermiteMaxDeviationMeters=" + hermiteMaxDeviationMeters.ToString("F2", CultureInfo.InvariantCulture)
                    + " interpMode=" + (splineApplied ? "Spline" : hermiteApplied ? "Hermite" : "Lerp")
                    + " splineHit=" + (splineApplied ? "true" : "false")
                    + " splineReason=" + (splineReason ?? "<none>")
                    + " anchorCorrectionHit=" + (hasAnchorEps ? "true" : "false")
                    + " anchorCorrectionReason=" + anchorCorrectionReason
                    + " anchorEps=" + GhostRenderTrace.FormatVector3d(hasAnchorEps ? anchorEps : Vector3d.zero)
                    + " final=" + GhostRenderTrace.FormatVector3d(interpolatedPos)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }

            // Register for LateUpdate re-positioning after FloatingOrigin shift.
            // Phase 3 / Phase 4: store the lookup key (recordingId + sectionIndex)
            // so LateUpdate can re-evaluate BOTH the spline (Phase 1+4) and the
            // anchor interval (Phase 2+3) at the same playback UT. Storing the
            // key only when an anchor exists (the prior contract) discarded the
            // spline result on every LateUpdate — the raw lerp on lines below
            // would overwrite the spline-positioned transform. The key is
            // always-stored when (recordingId, sectionIndex) is valid; the gates
            // (settings flag, store presence) are re-checked at LateUpdate.
            bool hasLookupKey = !string.IsNullOrEmpty(recordingId) && sectionIndex >= 0;
            AddOrReplaceGhostPosEntry(new GhostPosEntry
            {
                ghost = ghost,
                mode = GhostPosMode.PointInterp,
                recordingId = recordingId,
                bodyBefore = bodyBefore,
                bodyAfter = bodyAfter,
                latBefore = before.latitude, lonBefore = before.longitude, altBefore = effectiveAltBefore,
                latAfter = after.latitude, lonAfter = after.longitude, altAfter = effectiveAltAfter,
                t = t,
                pointUT = targetUT,
                pointDeltaTimeSeconds = after.ut - before.ut,
                interpolatedRot = interpolatedRot,
                velocityBefore = before.velocity,
                velocityAfter = after.velocity,
                anchorRecordingId = hasLookupKey ? recordingId : null,
                anchorSectionIndex = hasLookupKey ? sectionIndex : -1,
            });

            interpResult = new InterpolationResult(
                Vector3.Lerp(before.velocity, after.velocity, t),
                after.bodyName,
                TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t));
            // Surface velocity from the same body-fixed bracket the position used (both
            // endpoints resolved now, so the delta is motion over the surface in today's
            // axes). A body change inside the bracket has no single surface to measure on.
            if (ReferenceEquals(bodyBefore, bodyAfter)
                && GhostSurfaceVelocity.TryFromBracketPositions(
                    posBefore, posAfter, after.ut - before.ut, out Vector3 surfaceVelocity))
            {
                interpResult = interpResult.WithSurfaceVelocity(
                    surfaceVelocity, GhostSurfaceVelocitySource.Positions);
            }
        }

        void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points,
            List<OrbitSegment> segments, ref int cachedIndex, double targetUT, int orbitCacheBase,
            out InterpolationResult interpResult, bool allowActivation = true, bool skipOrbitSegments = false,
            string recordingId = null, int sectionIndex = -1,
            string pointFrameSource = "input",
            List<TrackSection> flatPointSections = null)
        {
            // Check orbit segments first (unless suppressed for surface vehicles)
            if (!skipOrbitSegments && segments != null && segments.Count > 0)
            {
                OrbitSegment? seg = FindOrbitSegment(segments, targetUT);
                if (seg.HasValue)
                {
                    if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg.Value))
                    {
                        int smaKey = ~orbitCacheBase; // bitwise complement — guaranteed no collision with positive cache keys
                        if (loggedOrbitSegments.Add(smaKey))
                            Log($"Orbit segment rejected (unusable elements): sma={seg.Value.semiMajorAxis:F0}, " +
                                "falling through to point interpolation");
                    }
                    else
                    {
                        CelestialBody segBody = FlightGlobals.Bodies?.Find(b => b.name == seg.Value.bodyName);
                        // Use segment index as cache key offset
                        int segIdx = segments.IndexOf(seg.Value);
                        int cacheKey = orbitCacheBase + segIdx;
                        if (loggedOrbitSegments.Add(cacheKey))
                            Log($"Orbit segment activated: cache={cacheKey}, body={seg.Value.bodyName}, " +
                                $"sma={seg.Value.semiMajorAxis:F0}, UT {seg.Value.startUT:F0}-{seg.Value.endUT:F0}");
                        PositionGhostFromOrbit(ghost, seg.Value, targetUT, cacheKey, recordingId);
                        Vector3 vel = Vector3.zero;
                        Orbit orbit;
                        bool hasOrbit = orbitCache.TryGetValue(cacheKey, out orbit);
                        Vector3d zupVelocity = hasOrbit ? orbit.getOrbitalVelocityAtUT(targetUT) : Vector3d.zero;
                        if (hasOrbit)
                            vel = zupVelocity;
                        Vector3d ghostWorldPos = ghost.transform.position;
                        double segAlt = segBody != null ? segBody.GetAltitude(ghostWorldPos) : 0;
                        interpResult = new InterpolationResult(vel, seg.Value.bodyName, segAlt);
                        if (hasOrbit && segBody != null)
                        {
                            interpResult = interpResult.WithSurfaceVelocity(
                                GhostSurfaceVelocity.FromOrbitVelocity(
                                    zupVelocity, segBody.getRFrmVel(ghostWorldPos)),
                                GhostSurfaceVelocitySource.Orbit);
                        }
                        return;
                    }
                }
            }

            // Fall through to point-based interpolation
            InterpolateAndPosition(ghost, points, ref cachedIndex, targetUT, allowActivation, out interpResult,
                recordingId, sectionIndex, pointFrameSource, flatPointSections);
        }
    }
}
