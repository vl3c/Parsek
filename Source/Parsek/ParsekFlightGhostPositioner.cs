using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // The flight scene's IGhostPositioner: places ghosts from recorded trajectories (points,
    // orbits, surface, relative and body-fixed sections, loops) and owns the positioning caches
    // nothing else reads. ParsekFlight builds the playback engine with it and keeps the state its
    // other paths share (orbit cache, log-once sets, the Re-Fly dense-frame cache).
    internal sealed class ParsekFlightGhostPositioner : IGhostPositioner
    {
        private readonly ParsekFlight host;

        internal ParsekFlightGhostPositioner(ParsekFlight host)
        {
            this.host = host;
        }

        // M4b phasing-knob body-fixed derotation: the time shift (seconds) of the positioning call
        // currently on the stack, read from GhostPlaybackState.bodyFixedShiftSeconds at the
        // IGhostPositioner entries (InterpolateAndPosition / PositionAtPoint / PositionLoop) and
        // consumed by the private InterpolateAndPosition / PositionGhostAt when resolving Absolute
        // body-fixed points (vacuum burn arcs replayed off the rotation-aligned grid render rotated
        // with the planet otherwise; the 2026-06-11 playtest's 46-degree teleports). Per-call
        // ambient with try/finally reset rather than threading a parameter through the six-deep
        // positioning chain; single-threaded Unity main loop, and 0 (the identity) for every
        // non-knob caller.
        private double activeBodyFixedShiftSeconds;

        // Per-member last re-aim ghost frame, for seam-teleport detection (segment change -> log the jump).
        private readonly Dictionary<int, (int segIdx, Vector3 pos, double loopUT)> reaimLastGhostFrame
            = new Dictionary<int, (int, Vector3, double)>();

        // Per-member last orbit-positioned ghost frame for the mode-agnostic
        // loop-seam teleport instrument (Tier-C, ghostRenderTracing-gated;
        // faithful AND re-aimed members). Unity frame number rides along so a
        // hide/show gap suppresses the stale-prev comparison.
        private readonly Dictionary<int, (int segIdx, Vector3 pos, double loopUT, double currentUT, int frame)>
            loopSeamLastGhostFrame
                = new Dictionary<int, (int, Vector3, double, double, int)>();

        // Generous upper bound on a loop ghost's own speed (m/s), added to the
        // segment body's orbital speed for the warp-scaled expected-motion term
        // of the teleport threshold. Interplanetary coast speeds run ~3-12 km/s.
        internal const double LoopSeamGhostSpeedAllowanceMps = 15_000.0;

        private readonly Dictionary<string, ParsekFlight.PredictedOrbitTailContinuityCacheEntry> predictedOrbitTailContinuityCache =
            new Dictionary<string, ParsekFlight.PredictedOrbitTailContinuityCacheEntry>();

        private readonly HashSet<string> recordedAnchorSeenRecordingIds =
            new HashSet<string>(StringComparer.Ordinal);

        private readonly HashSet<string> recordedRelativeAnchorVisited =
            new HashSet<string>(StringComparer.Ordinal);

        // ===== Recorded-coordinate playback helpers =====
        //
        private const double PointHermiteMaxDeviationCapMeters = 250.0;

        private const double PointHermiteMinDeviationMeters = 10.0;

        private const double PointHermiteDeviationFraction = 0.05;

        private const bool NormalPlaybackSplinePositioningEnabled = false;

        private const long CheckpointOrbitCacheBase = long.MinValue;

        private const long CheckpointOrbitCacheRecordingStride = 1000000000000L;

        private const long CheckpointOrbitCacheSectionStride = 1000000L;

        private const long CheckpointOrbitCacheMaxRecordingIndex =
            (long.MaxValue - CheckpointOrbitCacheRecordingStride + 1) / CheckpointOrbitCacheRecordingStride;

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
                ParsekFlight.ShouldAutoActivateGhost(state),
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
                && ParsekFlight.TryGetAbsoluteSectionPlaybackFrames(
                    traj.TrackSections[splineSectionIdx],
                    out List<TrajectoryPoint> bodyFixedFrames))
            {
                string pointFrameSource = "section-frames";
                if (ParsekFlight.TryGetAbsoluteSectionPlaybackFramesForPlayback(
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
                    allowActivation: ParsekFlight.ShouldAutoActivateGhost(state),
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
                allowActivation: ParsekFlight.ShouldAutoActivateGhost(state), skipOrbitSegments: surfaceSkip,
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
            if (host.loggedRecordedRelativeStart.Add(relKey))
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
                ParsekFlight.ShouldAutoActivateGhost(state), out interpResult);
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
                || !ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
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
                allowActivation: ParsekFlight.ShouldAutoActivateGhost(state),
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
                TraceDebrisBodyFixedPrimaryPlayback(
                    traj, state, playbackUT, target, bodyFixedCount, firstPlaybackRender, ghostWorldBefore);
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

        /// <summary>
        /// Find the bodyFixedFrames bracket [before, after] that brackets the
        /// playback UT. Endpoints clamp at the first / last sample. NaN is
        /// returned when the section has zero or one frames (the caller's
        /// log line will report no usable bracket).
        /// </summary>
        internal static void ResolveBodyFixedPrimaryBracketUTs(
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

        /// <summary>
        /// Trace-Sep playback row for a parent-anchored debris ghost positioned from its
        /// body-fixed primary: bracketing samples, lerp alpha, predicted vs rendered world
        /// position and the rendered / recorded parent distances. Called only while ghost
        /// render tracing is on and the playback window is open.
        /// </summary>
        private void TraceDebrisBodyFixedPrimaryPlayback(
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            RelativeSectionPlaybackTarget target,
            int bodyFixedCount,
            bool firstPlaybackRender,
            Vector3d ghostWorldBefore)
        {
            Vector3d ghostWorld = state.ghost.transform != null
                ? (Vector3d)state.ghost.transform.position : Vector3d.zero;
            FindTraceBracketFrames(
                target.Section.bodyFixedFrames, playbackUT,
                out TrajectoryPoint? beforeFrame, out TrajectoryPoint? afterFrame);
            string beforeStr = FormatTraceBracketFrame(beforeFrame);
            string afterStr = FormatTraceBracketFrame(afterFrame);
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
            bool parentGhostFound = host.engine != null
                && host.engine.TryGetGhostWorldByRecordingId(
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
            // fidelity check - this field is the seed-only value and
            // stays constant across the entire bracket (e.g. the 600 ms
            // seed->first-sample gap on a fresh debris recording),
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
            // diverging?" - the bracketing-before-only field is stable
            // across an entire bracket (e.g. the 0.6 s seed->first-sample
            // gap on fresh debris recordings) while the interpolated
            // value evolves with the recorded geometry.
            double interpolatedAnchorLocalDist = double.NaN;
            if (target.Section.referenceFrame == ReferenceFrame.Relative
                && target.Section.frames != null
                && target.Section.frames.Count > 0)
            {
                recordedAnchorLocalDist = ResolveRecordedAnchorLocalDistanceBefore(
                    target.Section.frames, playbackUT);
                interpolatedAnchorLocalDist = TraceSeparation
                    .InterpolateAnchorLocalOffsetMagnitude(
                        target.Section.frames, playbackUT);
            }
            // Recorded body-fixed distance: subtract the parent's
            // body-fixed primary world position at the bracketing UT from
            // the debris's body-fixed primary world. Computed
            // independently of playback math - ghost transforms drop out
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
                    TrajectoryPoint? parentBefore = FindLastBodyFixedFrameAtOrBefore(
                        parentRec.TrackSections, playbackUT);
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
                " renderedParentDist=" + FormatTraceDistance(renderedParentDist) +
                " recordedAnchorLocalDist=" + FormatTraceDistance(recordedAnchorLocalDist) +
                " interpolatedAnchorLocalDist=" + FormatTraceDistance(interpolatedAnchorLocalDist) +
                " recordedBodyFixedDist=" + FormatTraceDistance(recordedBodyFixedDist));
        }

        /// <summary>
        /// The last sample at or before the playback UT and the first sample at or after it
        /// (null when none qualifies or the list is empty).
        /// </summary>
        internal static void FindTraceBracketFrames(
            List<TrajectoryPoint> frames,
            double playbackUT,
            out TrajectoryPoint? beforeFrame,
            out TrajectoryPoint? afterFrame)
        {
            beforeFrame = null;
            afterFrame = null;
            if (frames != null && frames.Count > 0)
            {
                int beforeIdx = -1;
                int afterIdx = -1;
                for (int j = 0; j < frames.Count; j++)
                {
                    if (frames[j].ut <= playbackUT)
                        beforeIdx = j;
                    if (frames[j].ut >= playbackUT && afterIdx < 0)
                        afterIdx = j;
                }
                if (beforeIdx >= 0) beforeFrame = frames[beforeIdx];
                if (afterIdx >= 0) afterFrame = frames[afterIdx];
            }
        }

        internal static string FormatTraceBracketFrame(TrajectoryPoint? frame)
        {
            return frame.HasValue
                ? ("ut=" + frame.Value.ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + " lla=(" + frame.Value.latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + "," + frame.Value.longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + "," + frame.Value.altitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")")
                : "<none>";
        }

        /// <summary>
        /// Magnitude of the anchor-local offset of the last relative frame at or before the
        /// playback UT, scanning in order and stopping at the first later frame; NaN when the
        /// first frame is already later.
        /// </summary>
        internal static double ResolveRecordedAnchorLocalDistanceBefore(
            List<TrajectoryPoint> frames, double playbackUT)
        {
            int beforeIdx = -1;
            for (int j = 0; j < frames.Count; j++)
            {
                if (frames[j].ut <= playbackUT)
                    beforeIdx = j;
                else break;
            }
            if (beforeIdx >= 0)
            {
                TrajectoryPoint relFrame = frames[beforeIdx];
                Vector3d localOffset = new Vector3d(
                    relFrame.latitude, relFrame.longitude, relFrame.altitude);
                return localOffset.magnitude;
            }
            return double.NaN;
        }

        /// <summary>
        /// Walks every section's body-fixed frames in order and returns the last sample at or
        /// before the playback UT; within a section the scan stops at the first later sample,
        /// and a later section's qualifying sample replaces an earlier one.
        /// </summary>
        internal static TrajectoryPoint? FindLastBodyFixedFrameAtOrBefore(
            List<TrackSection> trackSections, double playbackUT)
        {
            TrajectoryPoint? parentBefore = null;
            for (int s = 0; s < trackSections.Count; s++)
            {
                var ps = trackSections[s];
                if (ps.bodyFixedFrames == null || ps.bodyFixedFrames.Count == 0)
                    continue;
                for (int j = 0; j < ps.bodyFixedFrames.Count; j++)
                {
                    if (ps.bodyFixedFrames[j].ut <= playbackUT)
                        parentBefore = ps.bodyFixedFrames[j];
                    else break;
                }
            }
            return parentBefore;
        }

        internal static string FormatTraceDistance(double value)
        {
            return double.IsNaN(value)
                ? "NaN"
                : value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
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
                if (ParsekFlight.ShouldApplyImmediatePointSurfaceClearance(
                        traj.TerminalStateValue,
                        traj.SurfacePos.HasValue))
                {
                    positioned = ParsekFlight.ApplyLandedGhostClearance(
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
            string bodyName = ParsekFlight.ResolveExplosionAnchorBodyName(
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

            double minClearance = ParsekFlight.ImmediateLandedGhostClearanceFallbackMeters;
            if (FlightGlobals.ActiveVessel != null)
            {
                double distanceToVessel = Vector3d.Distance(
                    rawWorldPos, FlightGlobals.ActiveVessel.GetWorldPos3D());
                minClearance = ParsekFlight.ComputeTerrainClearance(distanceToVessel);
            }
            else
            {
                ParsekLog.Verbose("TerrainCorrect",
                    $"Explosion anchor clearance fallback #{index} (\"{safeVesselName}\"): " +
                    $"active vessel unavailable — using legacy floor " +
                    $"{ParsekFlight.ImmediateLandedGhostClearanceFallbackMeters.ToString("F1", CultureInfo.InvariantCulture)}m");
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

        private static double ResolveImmediateLandedGhostClearanceMeters(
            int index, string vesselName,
            string bodyName, double latitude, double longitude, double altitude)
        {
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            bool hasBody = body != null;
            bool hasActiveVessel = FlightGlobals.ActiveVessel != null;
            string fallbackReason = ParsekFlight.ResolveImmediateLandedGhostClearanceFallbackReason(
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
                    $"{ParsekFlight.ImmediateLandedGhostClearanceFallbackMeters.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}m " +
                    "(distance-aware #262 path skipped)");
                return ParsekFlight.ImmediateLandedGhostClearanceFallbackMeters;
            }

            Vector3d pointWorld = body.GetWorldSurfacePosition(latitude, longitude, altitude);
            double distToVessel = Vector3d.Distance(pointWorld, FlightGlobals.ActiveVessel.GetWorldPos3D());
            return ParsekFlight.ComputeTerrainClearance(distToVessel);
        }

        void IGhostPositioner.PositionAtSurface(int index, IPlaybackTrajectory traj,
            GhostPlaybackState state)
        {
            if (state?.ghost == null || traj?.SurfacePos == null) return;

            SurfacePosition positioned = traj.SurfacePos.Value;
            if (ParsekFlight.ShouldApplyImmediateSurfacePositionClearance(
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
                syntheticPoint = ParsekFlight.ApplyLandedGhostClearance(
                    syntheticPoint, index, traj.VesselName, traj.TerrainHeightAtEnd,
                    ResolveImmediateLandedGhostClearanceMeters(
                        index, traj.VesselName,
                        positioned.body, positioned.latitude, positioned.longitude, positioned.altitude));
                positioned.altitude = syntheticPoint.altitude;
            }

            PositionGhostAtSurface(state.ghost, positioned, ParsekFlight.ShouldAutoActivateGhost(state),
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
            if (state?.ghost == null || host.orbitCache == null
                || !host.orbitCache.TryGetValue(cacheKey, out Orbit orbit) || orbit == null)
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
                if (host.cachedLoopUnits != null && host.cachedLoopUnits.TryGetUnitForMember(memberIndex, out GhostPlaybackLogic.LoopUnit unit)
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
                    ParsekFlight.ShouldAutoActivateGhost(state), out interpResult);
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
            return host.ApplyZoneRenderingImpl(index, state, traj, distance, playbackUT, protectedIndex);
        }

        void IGhostPositioner.ClearOrbitCache()
        {
            ClearOrbitPlaybackCaches();
        }

        // CreateGhostSphere moved to GhostVisualBuilder.CreateGhostSphere (T25 Phase 4 prep)

        internal void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points, ref int cachedIndex,
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
                host.Log($"Could not find body: {(bodyBefore == null ? before.bodyName : after.bodyName)}");
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
            double effectiveAltBefore = ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
                bodyBefore, before.latitude, before.longitude, before.altitude,
                before.recordedGroundClearance, ReferenceFrame.Absolute,
                before.ut, recordingId);
            double effectiveAltAfter = ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
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
                host.Log("Warning: NaN in interpolated position, using 'before' position");
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
                && ParsekFlight.allowSplinePositioning(recordingId, sectionIndex, out Parsek.Rendering.SmoothingSpline spline))
            {
                ParsekFlight.RecordSplineEvalForLogging();
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
                        host.unknownFrameTagWarned);
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
            bool allowHermite = ParsekFlight.allowPointHermiteInterpolation(
                false,
                splineApplied,
                allowNormalPlaybackHermite: ParsekFlight.NormalPlaybackPointHermiteEnabled,
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
            bool hasAnchorEps = ParsekFlight.allowRenderAnchorCorrectionInterval(
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
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.PointInterp,
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

        internal void InterpolateAndPosition(GameObject ghost, List<TrajectoryPoint> points,
            List<OrbitSegment> segments, ref int cachedIndex, double targetUT, int orbitCacheBase,
            out InterpolationResult interpResult, bool allowActivation = true, bool skipOrbitSegments = false,
            string recordingId = null, int sectionIndex = -1,
            string pointFrameSource = "input",
            List<TrackSection> flatPointSections = null)
        {
            // Check orbit segments first (unless suppressed for surface vehicles)
            if (!skipOrbitSegments && segments != null && segments.Count > 0)
            {
                OrbitSegment? seg = ParsekFlight.FindOrbitSegment(segments, targetUT);
                if (seg.HasValue)
                {
                    if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg.Value))
                    {
                        int smaKey = ~orbitCacheBase; // bitwise complement — guaranteed no collision with positive cache keys
                        if (host.loggedOrbitSegments.Add(smaKey))
                            host.Log($"Orbit segment rejected (unusable elements): sma={seg.Value.semiMajorAxis:F0}, " +
                                "falling through to point interpolation");
                    }
                    else
                    {
                        CelestialBody segBody = FlightGlobals.Bodies?.Find(b => b.name == seg.Value.bodyName);
                        // Use segment index as cache key offset
                        int segIdx = segments.IndexOf(seg.Value);
                        int cacheKey = orbitCacheBase + segIdx;
                        if (host.loggedOrbitSegments.Add(cacheKey))
                            host.Log($"Orbit segment activated: cache={cacheKey}, body={seg.Value.bodyName}, " +
                                $"sma={seg.Value.semiMajorAxis:F0}, UT {seg.Value.startUT:F0}-{seg.Value.endUT:F0}");
                        PositionGhostFromOrbit(ghost, seg.Value, targetUT, cacheKey, recordingId);
                        Vector3 vel = Vector3.zero;
                        Orbit orbit;
                        bool hasOrbit = host.orbitCache.TryGetValue(cacheKey, out orbit);
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

        /// <summary>
        /// Surface velocity from a RELATIVE section's body-fixed shadow frames at
        /// <paramref name="ut"/>. RELATIVE <c>frames</c> hold anchor-local metre offsets, never
        /// body-fixed coordinates, so they are not read here; a section without two
        /// <c>bodyFixedFrames</c> covering <paramref name="ut"/> leaves the result unchanged
        /// (surface velocity unknown).
        /// </summary>
        private static InterpolationResult WithRelativeSectionSurfaceVelocity(
            InterpolationResult result, TrackSection section, double ut)
        {
            List<TrajectoryPoint> bodyFixed =
                GhostSurfaceVelocity.SelectRelativeSectionBodyFixedFrames(section);
            int i = GhostSurfaceVelocity.FindBracketIndex(bodyFixed, ut);
            if (i < 0)
                return result;
            TrajectoryPoint before = bodyFixed[i];
            TrajectoryPoint after = bodyFixed[i + 1];
            if (string.IsNullOrEmpty(before.bodyName)
                || !string.Equals(before.bodyName, after.bodyName, StringComparison.Ordinal))
                return result;
            CelestialBody body = FindBodyByNameNoAlloc(before.bodyName);
            if (body == null)
                return result;
            Vector3d posBefore = body.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = body.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);
            if (!GhostSurfaceVelocity.TryFromBracketPositions(
                    posBefore, posAfter, after.ut - before.ut, out Vector3 surfaceVelocity))
                return result;
            return result.WithSurfaceVelocity(surfaceVelocity, GhostSurfaceVelocitySource.Positions);
        }

        // Runs per frame for every RELATIVE-section ghost: a plain loop, no predicate closure.
        private static CelestialBody FindBodyByNameNoAlloc(string bodyName)
        {
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies == null || string.IsNullOrEmpty(bodyName))
                return null;
            for (int i = 0; i < bodies.Count; i++)
            {
                CelestialBody candidate = bodies[i];
                if (candidate != null && candidate.name == bodyName)
                    return candidate;
            }
            return null;
        }

        internal static double ComputePointHermiteMaxDeviationMeters(Vector3d posBefore, Vector3d posAfter)
        {
            double chordMeters = Vector3d.Distance(posBefore, posAfter);
            if (double.IsNaN(chordMeters) || double.IsInfinity(chordMeters))
                return PointHermiteMinDeviationMeters;

            double scaled = chordMeters * PointHermiteDeviationFraction;
            if (scaled < PointHermiteMinDeviationMeters)
                return PointHermiteMinDeviationMeters;
            if (scaled > PointHermiteMaxDeviationCapMeters)
                return PointHermiteMaxDeviationCapMeters;
            return scaled;
        }

        /// <summary>
        /// Phase 3 multi-anchor-lerp gate (design doc §6.4 / §8 / §18 Phase 3
        /// / §19.2 Stage 4 log table). Returns <c>true</c> with a populated
        /// <paramref name="epsilon"/> when (a) the caller supplied both a
        /// recording id and a non-negative section index and (b)
        /// <see cref="Parsek.Rendering.RenderSessionState"/> holds at least
        /// one anchor (start or end side) for the (recordingId, sectionIndex)
        /// pair. The returned ε is evaluated at <paramref name="targetUT"/>
        /// per §6.4 — constant for one-sided intervals, linear lerp for
        /// both-end intervals.
        ///
        /// <para>
        /// Side effects on hit: emits one of two <c>[Pipeline-Lerp]</c> log
        /// lines (single-anchor Verbose, both-end divergence Warn) ONCE per
        /// session per (recordingId, sectionIndex) — the dedup is owned by
        /// <see cref="Parsek.Rendering.RenderSessionState"/> so per-frame
        /// queries here cost only a HashSet lookup. The
        /// <c>degenerate-span</c> Warn fires from the math evaluator
        /// (<see cref="Parsek.Rendering.AnchorCorrectionInterval.EvaluateAt"/>)
        /// when the interval is malformed — keeping per-key dedup in one
        /// place. HR-7 is naturally enforced because the consumer always
        /// queries with the section that owns <paramref name="targetUT"/>; a
        /// lerp cannot cross a hard discontinuity by construction.
        /// </para>
        /// </summary>
        internal static bool allowAnchorCorrectionInterval(string recordingId, int sectionIndex,
            double targetUT, out Vector3d epsilon)
        {
            epsilon = default(Vector3d);
            if (string.IsNullOrEmpty(recordingId) || sectionIndex < 0)
                return false;

            Parsek.Rendering.AnchorCorrectionInterval? maybeInterval =
                Parsek.Rendering.RenderSessionState.LookupForSegmentInterval(
                    recordingId, sectionIndex);
            if (!maybeInterval.HasValue)
                return false;

            Parsek.Rendering.AnchorCorrectionInterval interval = maybeInterval.Value;
            epsilon = interval.EvaluateAt(targetUT);

            // Per-session-per-key dedup'd diagnostics (§19.2 Stage 4):
            //   Both-end + divergence > threshold  → Warn  "epsilon-divergence"
            //   StartOnly / EndOnly                 → Verb  "Single-anchor case"
            // The Warn for degenerate spans (End.UT <= Start.UT) is emitted
            // from the evaluator itself so any caller of EvaluateAt picks it
            // up uniformly.
            if (interval.Kind == Parsek.Rendering.AnchorIntervalKind.Both)
            {
                Parsek.Rendering.RenderSessionState.NotifyLerpDivergenceCheck(in interval);
            }
            else
            {
                Parsek.Rendering.RenderSessionState.NotifySingleAnchorLerpCase(in interval);
            }

            return true;
        }

        internal static bool allowSplinePositioningForPlayback(
            bool suppressSpline,
            bool allowNormalPlaybackSplinePositioning,
            out string reason)
        {
            if (suppressSpline)
            {
                reason = "suppressed";
                return false;
            }

            if (!allowNormalPlaybackSplinePositioning)
            {
                reason = "normal-playback-linearized";
                return false;
            }

            reason = "eligible";
            return true;
        }

        /// <summary>
        internal static string FormatStandaloneCoverageRange(double startUT, double endUT)
        {
            if (double.IsNaN(startUT) || double.IsNaN(endUT))
                return "(none)";
            return "[" + startUT.ToString("R", CultureInfo.InvariantCulture)
                + "," + endUT.ToString("R", CultureInfo.InvariantCulture) + "]";
        }

        private static void LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed(
            IPlaybackTrajectory traj,
            int recordingIndex,
            double playbackUT,
            string callsite,
            string routeReason,
            DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic)
        {
            string recordingId = traj?.RecordingId ?? "(none)";
            string key = "playback-world-parent-debris-body-fixed-failed-closed|"
                + recordingId + "|" + recordingIndex.ToString(CultureInfo.InvariantCulture)
                + "|" + (callsite ?? "(unknown)");
            ParsekLog.WarnRateLimited(
                "Playback",
                key,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: body-fixed primary failed closed for parent-anchored debris; recorded-relative fallback suppressed recording=#{1} recordingId={2} vessel=\"{3}\" playbackUT={4} routeReason={5} coverageReason={6} sectionIndex={7} sectionUT={8} relativeFrames={9} bodyFixedFrames={10} anchorRec={11}",
                    callsite ?? "(unknown)",
                    recordingIndex.ToString(CultureInfo.InvariantCulture),
                    recordingId,
                    traj?.VesselName ?? "(unknown)",
                    playbackUT.ToString("R", CultureInfo.InvariantCulture),
                    routeReason ?? "(unknown)",
                    diagnostic.Reason ?? "(unknown)",
                    diagnostic.SectionIndex.ToString(CultureInfo.InvariantCulture),
                    FormatStandaloneCoverageRange(diagnostic.SectionStartUT, diagnostic.SectionEndUT),
                    FormatStandaloneCoverageRange(diagnostic.FirstRelativeFrameUT, diagnostic.LastRelativeFrameUT),
                    FormatStandaloneCoverageRange(diagnostic.FirstBodyFixedFrameUT, diagnostic.LastBodyFixedFrameUT),
                    diagnostic.AnchorRecordingId ?? "(none)"),
                5.0);
        }

        /// <summary>
        /// Resolves a recording by id without driving the active scene's
        /// playback context. Walks <see cref="RecordingStore.CommittedRecordings"/>
        /// directly so the preview-source tail-lift resolver can locate a
        /// recording outside the active playback scope.
        /// </summary>
        private static Recording ResolveRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            try
            {
                IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
                if (committed != null)
                {
                    for (int i = 0; i < committed.Count; i++)
                    {
                        Recording r = committed[i];
                        if (r == null) continue;
                        if (string.Equals(r.RecordingId, recordingId, StringComparison.Ordinal))
                            return r;
                    }
                }
            }
            catch
            {
                // Mid-load mutation; treat as missing.
            }
            return null;
        }

        /// <summary>
        /// Phase 1 + Phase 4 LateUpdate spline re-evaluation helper (design
        /// doc §6.1 / §6.2 / §18 Phase 1+4 / HR-9). Instance shim that
        /// supplies the per-session <see cref="ParsekFlight.unknownFrameTagWarned"/> dedup
        /// set; the gate-then-dispatch logic itself lives in
        /// <see cref="ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure"/> so xUnit
        /// can exercise every branch without driving Unity's LateUpdate.
        ///
        /// <para>
        /// Without re-evaluating the spline here, the LateUpdate PointInterp
        /// branch would overwrite the spline-positioned <c>transform.position</c>
        /// with the raw <c>(latBefore..latAfter)</c> bracket lerp on every
        /// late frame, discarding Phase 1's smoothing and Phase 4's frame
        /// transformation in the steady state.
        /// </para>
        /// </summary>
        internal bool TryComputeLateUpdateSplineWorldPosition(in ParsekFlight.GhostPosEntry e, out Vector3d worldPos)
        {
            if (!allowSplinePositioningForPlayback(
                    false,
                    allowNormalPlaybackSplinePositioning: NormalPlaybackSplinePositioningEnabled,
                    out _))
            {
                worldPos = default(Vector3d);
                return false;
            }

            return ParsekFlight.TryComputeLateUpdateSplineWorldPositionPure(
                e.anchorRecordingId, e.anchorSectionIndex, e.pointUT, e.bodyBefore,
                host.unknownFrameTagWarned, out worldPos);
        }

        // Keep the old signature for backward compat with tests
        internal int FindWaypointIndex(double targetUT)
        {
            return TrajectoryMath.FindWaypointIndex(host.recording, ref host.lastPlaybackIndex, targetUT);
        }

        internal void PositionGhostAt(GameObject ghost, TrajectoryPoint point, string recordingId = null,
            bool altitudeAuthoritative = false)
        {
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == point.bodyName);
            if (body == null)
            {
                ParsekLog.VerboseRateLimited("Flight", "position-ghost-no-body",
                    $"PositionGhostAt: body '{point.bodyName}' not found — ghost position not updated");
                return;
            }

            // Phase 7 (design doc §13.1, §18 Phase 7): apply continuous terrain
            // correction when the recorded surface section captured a
            // ground clearance. Effective altitude = current_terrain + clearance.
            // NaN sentinel ⇒ legacy / non-surface point ⇒ fall through to
            // the recorded altitude (HR-9 silent fall-through). See
            // ResolvePhase7EffectiveAltitude for the per-frame cache lookup
            // and Pipeline-Terrain logging.
            //
            // PositionGhostAt only services Absolute-frame TrajectoryPoints
            // (RELATIVE-frame playback uses InterpolateAndPositionRelative,
            // OrbitalCheckpoint uses orbit-driven positioning) — so the
            // helper's referenceFrame argument is hard-coded Absolute. The
            // helper still NaN-fall-throughs on legacy/non-surface
            // points; the explicit Absolute argument is the P2-1 safety
            // gate against any future caller routing a Relative point here.
            //
            // altitudeAuthoritative=true short-circuits both Phase 7 and
            // tail-lift: the caller (e.g. PositionAtPoint after
            // ApplyLandedGhostClearance) has already produced the final
            // rendered altitude, and re-correcting it would double-apply Δ.
            double effectiveAltitude = altitudeAuthoritative
                ? point.altitude
                : ParsekFlight.ResolveEffectiveAltitudeWithTailLift(
                    body, point.latitude, point.longitude, point.altitude,
                    point.recordedGroundClearance, ReferenceFrame.Absolute,
                    point.ut, recordingId);

            // M4b body-fixed derotation (identity at shift 0): see InterpolateAndPosition.
            Vector3d worldPos = body.GetWorldSurfacePosition(
                point.latitude,
                TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(
                    point.longitude, activeBodyFixedShiftSeconds, body),
                effectiveAltitude);

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(point.rotation);

            ghost.transform.position = worldPos;
            ghost.transform.rotation = body.bodyTransform.rotation
                * TrajectoryMath.FrameTransform.ShiftSurfaceRelativeRotation(
                    sanitized, activeBodyFixedShiftSeconds, body);
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut;
            if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    recordingId,
                    -1,
                    traceCurrentUT,
                    point.ut,
                    "UpdatePath",
                    "mode=SinglePoint body=" + (point.bodyName ?? "<none>")
                    + " lat=" + point.latitude.ToString("F6", CultureInfo.InvariantCulture)
                    + " lon=" + point.longitude.ToString("F6", CultureInfo.InvariantCulture)
                    + " alt=" + effectiveAltitude.ToString("F2", CultureInfo.InvariantCulture)
                    + " altitudeAuthoritative=" + (altitudeAuthoritative ? "true" : "false")
                    + " rawWorld=" + GhostRenderTrace.FormatVector3d(worldPos)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghost.transform.position)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }

            // Register for LateUpdate re-positioning after FloatingOrigin shift
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.SinglePoint,
                recordingId = recordingId,
                bodyBefore = body,
                latBefore = point.latitude, lonBefore = point.longitude, altBefore = effectiveAltitude,
                pointUT = point.ut,
                interpolatedRot = sanitized,
            });
        }

        internal void ClearOrbitPlaybackCaches()
        {
            host.orbitCache?.Clear();
            predictedOrbitTailContinuityCache?.Clear();
        }

        // The two loop-seam trace dictionaries are keyed by committed-list index, so they move with
        // the list through IndexShift like every other index-keyed slot. Without the shift a member's
        // first seam check after a mid-list change compares against the frame of the recording that
        // used to hold its index.
        internal void ShiftLoopSeamTracesAfterDelete(int removedIndex)
        {
            int reaimShifted = CountKeysAbove(reaimLastGhostFrame, removedIndex);
            int seamShifted = CountKeysAbove(loopSeamLastGhostFrame, removedIndex);
            bool reaimDropped = reaimLastGhostFrame.ContainsKey(removedIndex);
            bool seamDropped = loopSeamLastGhostFrame.ContainsKey(removedIndex);
            IndexShift.DictAfterDelete(reaimLastGhostFrame, removedIndex);
            IndexShift.DictAfterDelete(loopSeamLastGhostFrame, removedIndex);
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose("Flight",
                "Loop-seam traces reindexed after delete at #" + removedIndex.ToString(ic)
                + ": reaim shifted=" + reaimShifted.ToString(ic)
                + " dropped=" + (reaimDropped ? "1" : "0")
                + " count=" + reaimLastGhostFrame.Count.ToString(ic)
                + ", loopSeam shifted=" + seamShifted.ToString(ic)
                + " dropped=" + (seamDropped ? "1" : "0")
                + " count=" + loopSeamLastGhostFrame.Count.ToString(ic));
        }

        internal void ShiftLoopSeamTracesAfterInsert(int insertedIndex)
        {
            int reaimShifted = CountKeysAbove(reaimLastGhostFrame, insertedIndex - 1);
            int seamShifted = CountKeysAbove(loopSeamLastGhostFrame, insertedIndex - 1);
            IndexShift.DictAfterInsert(reaimLastGhostFrame, insertedIndex);
            IndexShift.DictAfterInsert(loopSeamLastGhostFrame, insertedIndex);
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose("Flight",
                "Loop-seam traces reindexed after insert at #" + insertedIndex.ToString(ic)
                + ": reaim shifted=" + reaimShifted.ToString(ic)
                + " count=" + reaimLastGhostFrame.Count.ToString(ic)
                + ", loopSeam shifted=" + seamShifted.ToString(ic)
                + " count=" + loopSeamLastGhostFrame.Count.ToString(ic));
        }

        internal static int CountKeysAbove<T>(Dictionary<int, T> dict, int index)
        {
            int count = 0;
            foreach (int key in dict.Keys)
                if (key > index) count++;
            return count;
        }

        internal Dictionary<int, (int segIdx, Vector3 pos, double loopUT)> ReaimLastGhostFrameForTesting
            => reaimLastGhostFrame;

        internal Dictionary<int, (int segIdx, Vector3 pos, double loopUT, double currentUT, int frame)>
            LoopSeamLastGhostFrameForTesting => loopSeamLastGhostFrame;

        void PositionGhostFromOrbit(GameObject ghost, OrbitSegment segment, double ut, long cacheKey,
            string recordingId = null, IPlaybackTrajectory traj = null)
        {
            if (!TrajectoryMath.HasUsableOrbitSegmentElements(segment))
            {
                host.Log($"Orbit segment rejected: unusable elements, sma={segment.semiMajorAxis:F0}, " +
                    $"UT {segment.startUT:F0}-{segment.endUT:F0}");
                return;
            }

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == segment.bodyName);
            if (body == null)
            {
                host.Log($"Could not find body '{segment.bodyName}' for orbit playback");
                return;
            }

            Orbit orbit;
            if (!host.orbitCache.TryGetValue(cacheKey, out orbit))
            {
                try
                {
                    orbit = new Orbit(
                        segment.inclination,
                        segment.eccentricity,
                        segment.semiMajorAxis,
                        segment.longitudeOfAscendingNode,
                        segment.argumentOfPeriapsis,
                        segment.meanAnomalyAtEpoch,
                        segment.epoch,
                        body);
                }
                catch (Exception ex)
                {
                    host.Log($"Orbit segment construction failed: body={segment.bodyName}, " +
                        $"sma={segment.semiMajorAxis:F0}, error={ex.GetType().Name}");
                    return;
                }
                host.orbitCache[cacheKey] = orbit;
            }

            Vector3d rawOrbitWorldPos;
            try
            {
                rawOrbitWorldPos = orbit.getPositionAtUT(ut);
            }
            catch (Exception ex)
            {
                host.Log($"Orbit segment propagation failed: body={segment.bodyName}, " +
                    $"sma={segment.semiMajorAxis:F0}, error={ex.GetType().Name}");
                return;
            }
            if (!ParsekFlight.IsFiniteVector3d(rawOrbitWorldPos))
            {
                host.Log($"Orbit segment propagation produced non-finite position: body={segment.bodyName}, " +
                    $"sma={segment.semiMajorAxis:F0}");
                return;
            }

            Vector3d worldPos = rawOrbitWorldPos;
            Vector3d orbitContinuityOffset = Vector3d.zero;
            Vector3d rawOrbitContinuityOffset = Vector3d.zero;
            bool hasOrbitContinuityOffset = TryResolvePredictedOrbitTailContinuityOffset(
                traj,
                segment,
                orbit,
                ut,
                out orbitContinuityOffset,
                out rawOrbitContinuityOffset,
                out double orbitContinuityWeight,
                out double orbitContinuityAnchorUT,
                out double orbitContinuityBlendSeconds,
                out string orbitContinuityAnchorSource);
            if (hasOrbitContinuityOffset)
                worldPos += orbitContinuityOffset;
            if (!ParsekFlight.IsFiniteVector3d(worldPos))
            {
                host.Log($"Orbit segment continuity produced non-finite position: body={segment.bodyName}, " +
                    $"sma={segment.semiMajorAxis:F0}");
                return;
            }

            // Surface clamp: if the Keplerian orbit goes underground (e.g., deorbit
            // orbit with periapsis below surface, or impact trajectory on airless body),
            // clamp to surface altitude. Prevents the ghost mesh from tunneling through
            // the planet during orbit-only recording sections where no trajectory points exist.
            double orbitAlt;
            bool orbitTerrainClamped = false;
            try
            {
                orbitAlt = body.GetAltitude(worldPos);
                if (!ParsekFlight.IsFinite(orbitAlt))
                {
                    host.Log($"Orbit segment altitude was non-finite: body={segment.bodyName}, " +
                        $"sma={segment.semiMajorAxis:F0}");
                    return;
                }

                if (orbitAlt < 0)
                {
                    double lat = body.GetLatitude(worldPos);
                    double lon = body.GetLongitude(worldPos);
                    worldPos = body.GetWorldSurfacePosition(lat, lon, 0);
                    if (!ParsekFlight.IsFiniteVector3d(worldPos))
                    {
                        host.Log($"Orbit segment surface clamp produced non-finite position: body={segment.bodyName}, " +
                            $"sma={segment.semiMajorAxis:F0}");
                        return;
                    }
                    orbitTerrainClamped = true;
                }
            }
            catch (Exception ex)
            {
                host.Log($"Orbit segment body-position API failed: body={segment.bodyName}, " +
                    $"sma={segment.semiMajorAxis:F0}, error={ex.GetType().Name}");
                return;
            }

            ghost.transform.position = worldPos;

            Vector3d velocity;
            try
            {
                velocity = orbit.getOrbitalVelocityAtUT(ut);
            }
            catch
            {
                velocity = Vector3d.zero;
            }
            if (!ParsekFlight.IsFiniteVector3d(velocity))
                velocity = Vector3d.zero;

            // Orbital rotation: 3-way branch
            bool hasOfr = TrajectoryMath.HasOrbitalFrameRotation(segment);
            bool spinning = TrajectoryMath.IsSpinning(segment);

            var (ghostRot, boundaryWorldRot) = ParsekFlight.ComputeOrbitalRotation(
                segment, orbit, ut, velocity, worldPos, body.position,
                ghost.transform.rotation, cacheKey, hasOfr, spinning);

            ghost.transform.rotation = ghostRot;
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : ut;
            if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    recordingId,
                    -1,
                    traceCurrentUT,
                    ut,
                    "UpdatePath",
                    "mode=Orbit cacheKey=" + cacheKey.ToString(CultureInfo.InvariantCulture)
                    + " body=" + (segment.bodyName ?? "<none>")
                    + " segmentUT=[" + segment.startUT.ToString("F3", CultureInfo.InvariantCulture)
                    + "," + segment.endUT.ToString("F3", CultureInfo.InvariantCulture) + "]"
                    + " rawWorld=" + GhostRenderTrace.FormatVector3d(rawOrbitWorldPos)
                    + " continuityHit=" + (hasOrbitContinuityOffset ? "true" : "false")
                    + " continuityAnchorUT=" + FormatFiniteOrNa(orbitContinuityAnchorUT, "F3")
                    + " continuityBlendSeconds=" + FormatFiniteOrNa(orbitContinuityBlendSeconds, "F3")
                    + " continuityWeight=" + FormatFiniteOrNa(orbitContinuityWeight, "F3")
                    + " continuityAnchorSource=" + (orbitContinuityAnchorSource ?? "<none>")
                    + " continuityRawOffset=" + GhostRenderTrace.FormatVector3d(rawOrbitContinuityOffset)
                    + " continuityOffset=" + GhostRenderTrace.FormatVector3d(orbitContinuityOffset)
                    + " terrainClamp=" + (orbitTerrainClamped ? "true" : "false")
                    + " orbitAlt=" + orbitAlt.ToString("F2", CultureInfo.InvariantCulture)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghost.transform.position)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation)
                    + " rotationMode=" + (spinning ? "spin-forward" : hasOfr ? "orbital-frame" : "prograde"));
            }

            // First-activation logging (once per segment)
            if (!host.loggedOrbitRotationSegments.Contains(cacheKey))
            {
                host.loggedOrbitRotationSegments.Add(cacheKey);
                if (spinning)
                    ParsekLog.Verbose("Playback", $"Orbit segment {cacheKey}: spin-forward (|angVel|={segment.angularVelocity.magnitude:F4})");
                else if (hasOfr)
                    ParsekLog.Verbose("Playback", $"Orbit segment {cacheKey}: orbital-frame rotation");
                else
                    ParsekLog.Verbose("Playback", $"Orbit segment {cacheKey}: velocity-derived prograde (no ofr data)");
            }

            // Register for LateUpdate re-positioning after FloatingOrigin shift
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.Orbit,
                recordingId = recordingId,
                orbitCacheKey = cacheKey,
                orbitUT = ut,
                orbitFrameRot = segment.orbitalFrameRotation,
                hasOrbitFrameRot = hasOfr,
                orbitBody = body,
                orbitAngularVelocity = segment.angularVelocity,
                isSpinning = spinning,
                orbitSegmentStartUT = segment.startUT,
                boundaryWorldRot = boundaryWorldRot,
                orbitContinuityOffset = rawOrbitContinuityOffset,
                hasOrbitContinuityOffset = hasOrbitContinuityOffset,
                orbitContinuityAnchorUT = orbitContinuityAnchorUT,
                orbitContinuityBlendSeconds = orbitContinuityBlendSeconds,
            });
        }

        internal static Vector3d ResolveOrbitContinuityOffset(ParsekFlight.GhostPosEntry entry, double playbackUT)
        {
            if (!entry.hasOrbitContinuityOffset)
                return Vector3d.zero;

            double weight = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityWeight(
                entry.orbitContinuityAnchorUT,
                playbackUT,
                entry.orbitContinuityBlendSeconds);
            if (weight <= 1e-6)
                return Vector3d.zero;

            Vector3d offset = entry.orbitContinuityOffset * weight;
            return ParsekFlight.IsFiniteVector3d(offset) ? offset : Vector3d.zero;
        }

        private bool TryResolvePredictedOrbitTailContinuityOffset(
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            Orbit orbit,
            double playbackUT,
            out Vector3d weightedOffset,
            out Vector3d rawOffset,
            out double weight,
            out double anchorUT,
            out double blendSeconds,
            out string anchorSource)
        {
            weightedOffset = Vector3d.zero;
            rawOffset = Vector3d.zero;
            weight = 0.0;
            anchorUT = double.NaN;
            blendSeconds = double.NaN;
            anchorSource = null;

            if (!ShouldApplyPredictedOrbitTailContinuity(traj, segment, playbackUT))
                return false;

            anchorUT = traj.Points[traj.Points.Count - 1].ut;
            blendSeconds = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityBlendSeconds(
                anchorUT, segment.startUT);
            weight = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityWeight(
                anchorUT, playbackUT, blendSeconds);
            if (weight <= 1e-6)
                return false;

            string cacheKey = BuildPredictedOrbitTailContinuityCacheKey(traj, segment, anchorUT);
            bool cacheHit = false;
            if (predictedOrbitTailContinuityCache.TryGetValue(
                    cacheKey, out ParsekFlight.PredictedOrbitTailContinuityCacheEntry cacheEntry)
                && ParsekFlight.IsFiniteVector3d(cacheEntry.rawOffset))
            {
                rawOffset = cacheEntry.rawOffset;
                anchorSource = cacheEntry.anchorSource;
                cacheHit = true;
            }
            else
            {
                if (!TryResolvePredictedOrbitTailContinuityAnchorWorldPosition(
                        traj, out Vector3d anchorWorld, out double resolvedAnchorUT, out anchorSource))
                {
                    return false;
                }

                if (Math.Abs(resolvedAnchorUT - anchorUT) > 1e-6)
                {
                    anchorUT = resolvedAnchorUT;
                    blendSeconds = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityBlendSeconds(
                        anchorUT, segment.startUT);
                    weight = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityWeight(
                        anchorUT, playbackUT, blendSeconds);
                    if (weight <= 1e-6)
                        return false;
                    cacheKey = BuildPredictedOrbitTailContinuityCacheKey(traj, segment, anchorUT);
                }

                Vector3d orbitAtAnchor = orbit.getPositionAtUT(anchorUT);
                rawOffset = anchorWorld - orbitAtAnchor;
                if (!ParsekFlight.IsFiniteVector3d(rawOffset))
                    return false;

                double candidateRawMeters = rawOffset.magnitude;
                if (candidateRawMeters <= 0.01)
                    return false;

                predictedOrbitTailContinuityCache[cacheKey] =
                    new ParsekFlight.PredictedOrbitTailContinuityCacheEntry
                    {
                        rawOffset = rawOffset,
                        anchorSource = anchorSource
                    };
            }

            weightedOffset = rawOffset * weight;
            if (!ParsekFlight.IsFiniteVector3d(weightedOffset))
                return false;

            double rawMeters = rawOffset.magnitude;
            if (rawMeters <= 0.01)
                return false;

            ParsekLog.VerboseRateLimited(
                "Playback",
                "orbit-tail-continuity|" + (traj.RecordingId ?? "<no-rec>"),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Predicted orbit-tail continuity: rec={0} vessel=\"{1}\" playbackUT={2:F3} anchorUT={3:F3} segmentUT=[{4:F3},{5:F3}] source={6} rawOffset={7} rawMeters={8:F2} weight={9:F3} blendSeconds={10:F3} appliedOffset={11}",
                    ShortRecordingId(traj.RecordingId),
                    traj.VesselName ?? "?",
                    playbackUT,
                    anchorUT,
                    segment.startUT,
                    segment.endUT,
                    anchorSource ?? "<none>",
                    GhostRenderTrace.FormatVector3d(rawOffset),
                    rawMeters,
                    weight,
                    blendSeconds,
                    GhostRenderTrace.FormatVector3d(weightedOffset)
                    + " cache=" + (cacheHit ? "hit" : "miss")),
                5.0);
            return true;
        }

        internal static string BuildPredictedOrbitTailContinuityCacheKey(
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            double anchorUT)
        {
            string recordingId = traj?.RecordingId;
            if (string.IsNullOrEmpty(recordingId))
                recordingId = traj?.VesselName ?? "<no-recording-id>";

            return string.Join("|",
                recordingId,
                segment.bodyName ?? "<no-body>",
                anchorUT.ToString("R", CultureInfo.InvariantCulture),
                segment.startUT.ToString("R", CultureInfo.InvariantCulture),
                segment.endUT.ToString("R", CultureInfo.InvariantCulture),
                segment.epoch.ToString("R", CultureInfo.InvariantCulture),
                segment.semiMajorAxis.ToString("R", CultureInfo.InvariantCulture),
                segment.eccentricity.ToString("R", CultureInfo.InvariantCulture),
                segment.inclination.ToString("R", CultureInfo.InvariantCulture));
        }

        internal static bool ShouldApplyPredictedOrbitTailContinuity(
            IPlaybackTrajectory traj, OrbitSegment segment, double playbackUT)
        {
            if (traj?.Points == null || traj.Points.Count == 0)
                return false;
            if (!segment.isPredicted)
                return false;

            double lastPointUT = traj.Points[traj.Points.Count - 1].ut;
            if (playbackUT <= lastPointUT + 1e-6)
                return false;

            double blendSeconds = GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityBlendSeconds(
                lastPointUT, segment.startUT);
            return playbackUT <= lastPointUT + blendSeconds + 1e-6;
        }

        private bool TryResolvePredictedOrbitTailContinuityAnchorWorldPosition(
            IPlaybackTrajectory traj,
            out Vector3d anchorWorld,
            out double anchorUT,
            out string source)
        {
            anchorWorld = Vector3d.zero;
            anchorUT = double.NaN;
            source = null;
            if (traj?.Points == null || traj.Points.Count == 0)
                return false;

            TrajectoryPoint lastPoint = traj.Points[traj.Points.Count - 1];
            anchorUT = lastPoint.ut;

            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, anchorUT);
                bool exactSection = sectionIdx >= 0;
                if (sectionIdx < 0)
                    sectionIdx = FindNearestTrackSectionIndexForContinuityAnchor(
                        traj.TrackSections, anchorUT);

                if (sectionIdx >= 0 && sectionIdx < traj.TrackSections.Count)
                {
                    TrackSection section = traj.TrackSections[sectionIdx];
                    if (section.referenceFrame == ReferenceFrame.Relative)
                    {
                        if (ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                                section, anchorUT, out _, out _))
                        {
                            int absoluteCachedIndex = 0;
                            if (TryResolvePointWorldPosition(
                                    section.bodyFixedFrames,
                                    ref absoluteCachedIndex,
                                    anchorUT,
                                    out anchorWorld))
                            {
                                source = "relative-body-fixed-primary";
                                return true;
                            }
                        }

                        if (exactSection)
                            return false;
                    }

                    if (section.referenceFrame == ReferenceFrame.Absolute
                        && ParsekFlight.TryGetAbsoluteSectionPlaybackFramesForPlayback(
                            traj.TrackSections,
                            sectionIdx,
                            out List<TrajectoryPoint> bodyFixedFrames,
                            out _))
                    {
                        int absoluteCachedIndex = 0;
                        if (TryResolvePointWorldPosition(
                                bodyFixedFrames,
                                ref absoluteCachedIndex,
                                anchorUT,
                                out anchorWorld))
                        {
                            source = "absolute-section";
                            return true;
                        }
                    }

                    if (exactSection && section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                        return false;
                }
            }

            if (TryResolveTrajectoryPointWorldPosition(lastPoint, out anchorWorld))
            {
                source = "flat-point";
                return true;
            }

            return false;
        }

        internal static int FindNearestTrackSectionIndexForContinuityAnchor(
            List<TrackSection> trackSections, double anchorUT)
        {
            if (trackSections == null || trackSections.Count == 0)
                return -1;

            if (anchorUT < trackSections[0].startUT)
                return 0;
            for (int i = trackSections.Count - 1; i >= 0; i--)
            {
                TrackSection section = trackSections[i];
                if (anchorUT >= section.startUT - 1e-6)
                    return i;
            }
            return trackSections.Count - 1;
        }

        internal static string FormatFiniteOrNa(double value, string format)
        {
            return ParsekFlight.IsFinite(value)
                ? value.ToString(format, CultureInfo.InvariantCulture)
                : "n/a";
        }

        /// <summary>
        /// Computes Quaternion.LookRotation(velocity, radialOut) with a fallback to Vector3.up
        /// when velocity and radialOut are nearly parallel (dot > 0.99). Logs rate-limited
        /// diagnostic when the fallback is used. Pass suffix "start" for startUT context or
        /// null for current-time context.
        /// <para>
        /// BOTH arguments are Y-up WORLD (see <see cref="ParsekFlight.ComputeOrbitalRotation"/>'s frame
        /// contract). That also makes the near-parallel test mean what it reads as: the dot of a
        /// world velocity with a world radial is the sine of the flight-path angle, so the
        /// fallback now fires on genuinely radial motion rather than on a cross-frame accident.
        /// </para>
        /// </summary>
        internal static Quaternion SafeOrbitalLookRotation(
            Vector3 velocity, Vector3 radialOut, long cacheKey, string suffix)
        {
            if (Mathf.Abs(Vector3.Dot(velocity.normalized, radialOut.normalized)) > 0.99f)
            {
                string tagSuffix = suffix != null ? $"-{suffix}" : "";
                string msgSuffix = suffix != null ? $" at {suffix}UT" : "";
                ParsekLog.VerboseRateLimited("Playback", $"orbit-near-parallel{tagSuffix}-{cacheKey}",
                    $"Orbit segment {cacheKey}: velocity/radialOut near-parallel{msgSuffix}, LookRotation fallback");
                return Quaternion.LookRotation(velocity, Vector3.up);
            }
            return Quaternion.LookRotation(velocity, radialOut);
        }

        /// <summary>
        /// Positions a ghost using only orbit segments (no trajectory points).
        /// Used for background-only recordings (vessels that stayed on rails).
        /// </summary>
        internal void PositionGhostFromOrbitOnly(GameObject ghost, Recording rec, double ut, int orbitCacheBase,
            bool allowActivation)
        {
            for (int s = 0; s < rec.OrbitSegments.Count; s++)
            {
                var seg = rec.OrbitSegments[s];
                if (ut >= seg.startUT && ut <= seg.endUT)
                {
                    if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg))
                        continue;

                    int cacheKey = orbitCacheBase + s;
                    if (host.loggedOrbitSegments.Add(cacheKey))
                        host.Log($"Orbit-only segment activated: cache={cacheKey}, body={seg.bodyName}, " +
                            $"sma={seg.semiMajorAxis:F0}, UT {seg.startUT:F0}-{seg.endUT:F0}");
                    PositionGhostFromOrbit(ghost, seg, ut, cacheKey, rec?.RecordingId, rec);
                    if (allowActivation)
                        ghost.SetActive(true);
                    return;
                }
            }
            // UT falls in a gap between orbit segments — hide ghost
            ghost.SetActive(false);
        }

        /// <summary>
        /// Positions a ghost at a fixed surface position (body, lat, lon, alt, rotation).
        /// Used for background vessels that are landed/splashed.
        /// </summary>
        internal void PositionGhostAtSurface(GameObject ghost, SurfacePosition surfPos, bool allowActivation,
            string recordingId = null, double pointUT = double.NaN)
        {
            CelestialBody body = FlightGlobals.Bodies.Find(b => b.name == surfPos.body);
            if (body == null)
            {
                ParsekLog.Warn("Flight", $"PositionGhostAtSurface: body '{surfPos.body}' not found");
                ghost.SetActive(false);
                return;
            }
            ghost.transform.position = body.GetWorldSurfacePosition(surfPos.latitude, surfPos.longitude, surfPos.altitude);
            ghost.transform.rotation = body.bodyTransform.rotation * surfPos.rotation;
            double tracePlaybackUT = double.IsNaN(pointUT) && Planetarium.fetch != null
                ? Planetarium.GetUniversalTime()
                : pointUT;
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : tracePlaybackUT;
            if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    recordingId,
                    -1,
                    traceCurrentUT,
                    tracePlaybackUT,
                    "UpdatePath",
                    "mode=Surface body=" + (surfPos.body ?? "<none>")
                    + " lat=" + surfPos.latitude.ToString("F6", CultureInfo.InvariantCulture)
                    + " lon=" + surfPos.longitude.ToString("F6", CultureInfo.InvariantCulture)
                    + " alt=" + surfPos.altitude.ToString("F2", CultureInfo.InvariantCulture)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghost.transform.position)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }
            if (allowActivation)
                ghost.SetActive(true);

            // Register for LateUpdate re-positioning after FloatingOrigin shift
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.Surface,
                recordingId = recordingId,
                bodyBefore = body,
                latBefore = surfPos.latitude, lonBefore = surfPos.longitude, altBefore = surfPos.altitude,
                surfaceRot = surfPos.rotation,
            });

            ParsekLog.VerboseRateLimited("Flight", "surface-ghost-positioned",
                $"PositionGhostAtSurface: body={surfPos.body} lat={surfPos.latitude:F4} lon={surfPos.longitude:F4} alt={surfPos.altitude:F1}");
        }

        internal double ResolvePlaybackDistanceForEngine(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            Camera sceneCamera = FlightCamera.fetch?.mainCamera;
            Vector3d? cameraWorldPosition = sceneCamera != null
                ? (Vector3d?)sceneCamera.transform.position
                : null;
            Vector3d referencePosition;
            if (!ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                    ResolveCameraAnchorWorldPosition(),
                    cameraWorldPosition,
                    out referencePosition))
            {
                return double.NaN;
            }

            return ResolvePlaybackDistanceFromReferencePosition(
                index, traj, state, playbackUT, referencePosition);
        }

        /// <summary>
        /// World position the flight camera is anchored on: the watched ghost while in watch
        /// mode, otherwise the active (real) vessel. The ghost render-distance LOD radius is
        /// centered here so zooming the camera in/out does not toggle ghost FX (see
        /// <see cref="ParsekFlight.TryResolvePlaybackDistanceReferencePosition"/>). A non-finite watched-ghost
        /// anchor falls through to the active vessel; an unavailable active vessel returns null.
        /// The caller treats null or a non-finite result as "no anchor" and falls back to the
        /// camera, so the active-vessel branch is intentionally not finiteness-checked here.
        /// </summary>
        Vector3d? ResolveCameraAnchorWorldPosition()
        {
            if (host.watchMode != null
                && host.watchMode.IsWatchingGhost
                && host.watchMode.TryGetWatchedGhostAnchorWorldPosition(out Vector3d watchedAnchor)
                && ParsekFlight.IsFiniteVector3d(watchedAnchor))
            {
                return watchedAnchor;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            Transform activeTransform = activeVessel != null ? activeVessel.transform : null;
            if (activeTransform != null)
                return (Vector3d)activeTransform.position;

            return null;
        }

        internal double ResolvePlaybackActiveVesselDistanceForEngine(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT)
        {
            Vector3d? activeVesselWorldPosition = FlightGlobals.ActiveVessel != null
                ? (Vector3d?)FlightGlobals.ActiveVessel.transform.position
                : null;
            if (!activeVesselWorldPosition.HasValue
                || !ParsekFlight.IsFiniteVector3d(activeVesselWorldPosition.Value))
            {
                return ResolvePlaybackDistanceForEngine(index, traj, state, playbackUT);
            }

            return ResolvePlaybackDistanceFromReferencePosition(
                index, traj, state, playbackUT, activeVesselWorldPosition.Value);
        }

        double ResolvePlaybackDistanceFromReferencePosition(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, Vector3d referencePosition)
        {
            Vector3d worldPos;
            if (TryResolvePlaybackWorldPosition(index, traj, state, playbackUT, out worldPos))
            {
                return Vector3d.Distance(worldPos, referencePosition);
            }

            return ResolveUnresolvedPlaybackDistanceFallback(
                traj, state, playbackUT, referencePosition);
        }

        internal static double ResolveUnresolvedPlaybackDistanceFallback(
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            Vector3d referencePosition)
        {
            double? relativeDistance = ParsekFlight.ResolveUnresolvedRelativeSectionDistanceFallback(
                traj, playbackUT);
            if (relativeDistance.HasValue)
                return relativeDistance.Value;

            if (state != null && state.ghost != null)
            {
                return Vector3d.Distance(
                    (Vector3d)state.ghost.transform.position,
                    referencePosition);
            }

            return double.NaN;
        }

        internal static bool IsRelativeSectionAtUT(IPlaybackTrajectory traj, double playbackUT)
        {
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return false;
            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
            return sectionIdx >= 0
                && traj.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative;
        }

        bool TryInterpolateAndPositionCheckpointSection(
            int recordingIndex,
            IPlaybackTrajectory traj,
            GameObject ghost,
            ref int playbackIdx,
            double playbackUT,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            interpResult = InterpolationResult.Zero;
            if (!TryGetCheckpointTrackSection(traj, playbackUT, out int sectionIdx, out TrackSection section))
                return false;

            if (!TryInterpolateAndPositionCheckpointSectionWithOrbitRotation(
                    recordingIndex,
                    sectionIdx,
                    section,
                    ghost,
                    ref playbackIdx,
                    playbackUT,
                    allowActivation,
                    recordingId: traj?.RecordingId,
                    out interpResult))
            {
                // Phase 1: pass section context. Checkpoint sections are
                // OrbitalCheckpoint reference frame, which ShouldFitSection
                // filters out — the spline gate will naturally fall through
                // to legacy lerp here. Threaded so a future spline-on-
                // checkpoint extension wouldn't need to revisit the call site.
                InterpolateAndPosition(
                    ghost,
                    section.frames,
                    ref playbackIdx,
                    playbackUT,
                    allowActivation,
                    out interpResult,
                    recordingId: traj?.RecordingId,
                    sectionIndex: sectionIdx);
            }

            int logIndex = playbackIdx;
            if (TryResolvePointWorldPosition(section.frames, ref logIndex, playbackUT, out Vector3d worldPos))
                LogCheckpointPointPlayback(traj, sectionIdx, section, playbackUT, worldPos);

            return true;
        }

        bool TryInterpolateAndPositionCheckpointSectionWithOrbitRotation(
            int recordingIndex,
            int sectionIdx,
            TrackSection section,
            GameObject ghost,
            ref int playbackIdx,
            double playbackUT,
            bool allowActivation,
            string recordingId,
            out InterpolationResult interpResult)
        {
            interpResult = InterpolationResult.Zero;
            if (!TryFindCheckpointOrbitSegment(section, playbackUT, out OrbitSegment segment, out int checkpointIdx))
                return false;

            long cacheKey = BuildCheckpointOrbitCacheKey(recordingIndex, sectionIdx, checkpointIdx);
            if (!TryGetOrbitForSegment(segment, cacheKey, out Orbit orbit, out CelestialBody orbitBody))
                return false;

            bool hasCheckpointFrames = section.frames != null && section.frames.Count > 0;
            if (!hasCheckpointFrames)
            {
                if (allowActivation && !ghost.activeSelf)
                    ghost.SetActive(true);

                PositionGhostFromOrbit(ghost, segment, playbackUT, cacheKey, recordingId);

                Vector3d orbitWorldPos = orbit.getPositionAtUT(playbackUT);
                Vector3d orbitVelocity = orbit.getOrbitalVelocityAtUT(playbackUT);
                double orbitAltitude = orbitBody.GetAltitude(orbitWorldPos);
                interpResult = new InterpolationResult(
                    (Vector3)orbitVelocity,
                    segment.bodyName,
                    orbitAltitude).WithSurfaceVelocity(
                        GhostSurfaceVelocity.FromOrbitVelocity(
                            orbitVelocity, orbitBody.getRFrmVel(orbitWorldPos)),
                        GhostSurfaceVelocitySource.Orbit);

                string recId = string.IsNullOrEmpty(recordingId) ? "(null)" : recordingId;
                ParsekLog.VerboseRateLimited(
                    "Playback",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "orbital-checkpoint-orbit-only-{0}-{1}-{2}",
                        recId,
                        sectionIdx,
                        checkpointIdx),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "OrbitalCheckpoint orbit-only playback: rec={0} currentUT={1:F1} section[{2}] sectionUT={3:F1}-{4:F1} checkpointIdx={5} segmentUT={6:F1}-{7:F1} world={8} alt={9:F1}",
                        recId,
                        playbackUT,
                        sectionIdx,
                        section.startUT,
                        section.endUT,
                        checkpointIdx,
                        segment.startUT,
                        segment.endUT,
                        GhostRenderTrace.FormatVector3d(orbitWorldPos),
                        orbitAltitude),
                    5.0);
                return true;
            }

            bool hasPointInterpolation = TryResolveCheckpointPointInterpolation(
                section.frames,
                ref playbackIdx,
                playbackUT,
                out TrajectoryPoint before,
                out TrajectoryPoint after,
                out float t);
            if (!hasPointInterpolation)
            {
                if (section.frames == null || section.frames.Count == 0)
                {
                    ghost.SetActive(false);
                    return true;
                }

                before = section.frames[0];
                after = before;
                t = 0f;
            }

            CelestialBody bodyBefore = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies?.Find(b => b.name == after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
                return false;

            if (allowActivation && !ghost.activeSelf)
                ghost.SetActive(true);

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);
            Vector3d interpolatedPos = Vector3d.Lerp(posBefore, posAfter, t);
            if (double.IsNaN(interpolatedPos.x) || double.IsNaN(interpolatedPos.y) || double.IsNaN(interpolatedPos.z))
                interpolatedPos = posBefore;

            // §7.7 BubbleEntry/Exit (and any other §7.x candidate landing on
            // a Checkpoint section): apply the rigid-translation ε on top of
            // the Kepler-bracketed lerp before assigning the transform.
            // Without this, the ε computed by AnchorPropagator and stored in
            // RenderSessionState would be invisible — the spline / lerp paths
            // already query allowAnchorCorrectionInterval, but the checkpoint
            // path used to skip it because the original §7.5 OrbitalCheckpoint
            // anchor lands on the ABSOLUTE neighbour rather than the
            // Checkpoint section itself; §7.7 changed that, and so did the
            // Phase 6 anchor-bound resolver work that produces ε at the
            // Checkpoint side. Same lookup as the PointInterp path uses
            // (Phase 2/3 ε, additive in world-space, HR-15 frozen-once).
            bool hasAnchorEps = ParsekFlight.allowRenderAnchorCorrectionInterval(
                recordingId, sectionIdx, playbackUT, false,
                out Vector3d anchorEps, out string anchorCorrectionReason);
            if (hasAnchorEps)
                interpolatedPos += anchorEps;

            Vector3d velocity = orbit.getOrbitalVelocityAtUT(playbackUT);
            bool hasOfr = TrajectoryMath.HasOrbitalFrameRotation(segment);
            bool spinning = TrajectoryMath.IsSpinning(segment);
            var (ghostRot, _) = ParsekFlight.ComputeOrbitalRotation(
                segment,
                orbit,
                playbackUT,
                velocity,
                interpolatedPos,
                orbitBody.position,
                ghost.transform.rotation,
                cacheKey,
                hasOfr,
                spinning);

            ghost.transform.position = interpolatedPos;
            ghost.transform.rotation = ghostRot;
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : playbackUT;
            if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    recordingId,
                    recordingIndex,
                    traceCurrentUT,
                    playbackUT,
                    "UpdatePath",
                    "mode=CheckpointPoint sectionIndex=" + sectionIdx.ToString(CultureInfo.InvariantCulture)
                    + " cacheKey=" + cacheKey.ToString(CultureInfo.InvariantCulture)
                    + " beforeUT=" + before.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " afterUT=" + after.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " t=" + t.ToString("F4", CultureInfo.InvariantCulture)
                    + " rawBefore=" + GhostRenderTrace.FormatVector3d(posBefore)
                    + " rawAfter=" + GhostRenderTrace.FormatVector3d(posAfter)
                    + " anchorCorrectionHit=" + (hasAnchorEps ? "true" : "false")
                    + " anchorCorrectionReason=" + anchorCorrectionReason
                    + " anchorEps=" + GhostRenderTrace.FormatVector3d(hasAnchorEps ? anchorEps : Vector3d.zero)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghost.transform.position)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }

            Quaternion pointRot = Quaternion.Slerp(before.rotation, after.rotation, t);
            pointRot = TrajectoryMath.SanitizeQuaternion(pointRot);
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.CheckpointPoint,
                recordingId = recordingId,
                recordingIndex = recordingIndex,
                hasRecordingIndex = true,
                bodyBefore = bodyBefore,
                bodyAfter = bodyAfter,
                latBefore = before.latitude,
                lonBefore = before.longitude,
                altBefore = before.altitude,
                latAfter = after.latitude,
                lonAfter = after.longitude,
                altAfter = after.altitude,
                t = t,
                pointUT = playbackUT,
                interpolatedRot = pointRot,
                orbitCacheKey = cacheKey,
                orbitUT = playbackUT,
                orbitFrameRot = segment.orbitalFrameRotation,
                hasOrbitFrameRot = hasOfr,
                orbitBody = orbitBody,
                orbitAngularVelocity = segment.angularVelocity,
                isSpinning = spinning,
                orbitSegmentStartUT = segment.startUT,
                // §7.7 anchor lookup keys for LateUpdate re-application.
                // Without these, the LateUpdate FloatingOrigin shift
                // would replay the bare lerp position and the rendered
                // ghost would drop the anchor correction every late frame.
                anchorRecordingId = recordingId,
                anchorSectionIndex = sectionIdx,
            });

            interpResult = new InterpolationResult(
                (Vector3)velocity,
                segment.bodyName,
                TrajectoryMath.InterpolateAltitude(before.altitude, after.altitude, t));
            if (hasPointInterpolation
                && ReferenceEquals(bodyBefore, bodyAfter)
                && GhostSurfaceVelocity.TryFromBracketPositions(
                    posBefore, posAfter, after.ut - before.ut, out Vector3 checkpointSurfaceVelocity))
            {
                interpResult = interpResult.WithSurfaceVelocity(
                    checkpointSurfaceVelocity, GhostSurfaceVelocitySource.Positions);
            }
            return true;
        }

        private bool TryGetOrbitForSegment(
            OrbitSegment segment,
            long cacheKey,
            out Orbit orbit,
            out CelestialBody body)
        {
            orbit = null;
            body = null;
            if (!TrajectoryMath.HasUsableOrbitSegmentElements(segment))
                return false;

            body = FlightGlobals.Bodies?.Find(b => b.name == segment.bodyName);
            if (body == null)
                return false;

            if (!host.orbitCache.TryGetValue(cacheKey, out orbit))
            {
                try
                {
                    orbit = new Orbit(
                        segment.inclination,
                        segment.eccentricity,
                        segment.semiMajorAxis,
                        segment.longitudeOfAscendingNode,
                        segment.argumentOfPeriapsis,
                        segment.meanAnomalyAtEpoch,
                        segment.epoch,
                        body);
                }
                catch
                {
                    orbit = null;
                    return false;
                }
                host.orbitCache[cacheKey] = orbit;
            }

            return true;
        }

        internal static bool TryFindCheckpointOrbitSegment(
            TrackSection section,
            double playbackUT,
            out OrbitSegment segment,
            out int checkpointIdx)
        {
            segment = default(OrbitSegment);
            checkpointIdx = -1;
            if (section.checkpoints == null || section.checkpoints.Count == 0)
                return false;

            for (int i = 0; i < section.checkpoints.Count; i++)
            {
                OrbitSegment candidate = section.checkpoints[i];
                if (playbackUT >= candidate.startUT && playbackUT <= candidate.endUT)
                {
                    segment = candidate;
                    checkpointIdx = i;
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveCheckpointPointInterpolation(
            List<TrajectoryPoint> frames,
            ref int playbackIdx,
            double playbackUT,
            out TrajectoryPoint before,
            out TrajectoryPoint after,
            out float t)
        {
            return TrajectoryMath.InterpolatePoints(
                frames,
                ref playbackIdx,
                playbackUT,
                out before,
                out after,
                out t);
        }

        internal static long BuildCheckpointOrbitCacheKey(
            int recordingIndex,
            int sectionIdx,
            int checkpointIdx)
        {
            // Regular orbit playback cache keys are non-negative int-derived values.
            // Checkpoint keys live in a negative long namespace with explicit strides
            // so section/checkpoint growth cannot alias the ordinary segment cache.
            long rec = Math.Min(CheckpointOrbitCacheMaxRecordingIndex, Math.Max(0, (long)recordingIndex));
            long section = Math.Min(CheckpointOrbitCacheSectionStride - 1, Math.Max(0, (long)sectionIdx));
            long checkpoint = Math.Min(CheckpointOrbitCacheSectionStride - 1, Math.Max(0, (long)checkpointIdx));
            return unchecked(
                CheckpointOrbitCacheBase
                + rec * CheckpointOrbitCacheRecordingStride
                + section * CheckpointOrbitCacheSectionStride
                + checkpoint);
        }

        bool TryGetCheckpointTrackSection(
            IPlaybackTrajectory traj,
            double playbackUT,
            out int sectionIdx,
            out TrackSection section)
        {
            sectionIdx = -1;
            section = default(TrackSection);
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return false;

            sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
            if (sectionIdx < 0)
                return false;

            section = traj.TrackSections[sectionIdx];
            return ParsekFlight.HasRenderableCheckpointTrackSection(section);
        }

        void LogCheckpointPointPlayback(
            IPlaybackTrajectory traj,
            int sectionIdx,
            TrackSection section,
            double playbackUT,
            Vector3d worldPos)
        {
            int pointIdx = 0;
            TrajectoryPoint? point = TrajectoryMath.BracketPointAtUT(
                section.frames,
                playbackUT,
                ref pointIdx);
            if (!point.HasValue && section.frames != null && section.frames.Count > 0)
                point = section.frames[0];

            string recId = string.IsNullOrEmpty(traj?.RecordingId) ? "(null)" : traj.RecordingId;
            string pointDetail = point.HasValue
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "pointUT={0:F1} body={1} alt={2:F0} speed={3:F1}",
                    point.Value.ut,
                    point.Value.bodyName ?? "(null)",
                    point.Value.altitude,
                    point.Value.velocity.magnitude)
                : "pointUT=(none) body=(none) alt=0 speed=0";

            // Bug #595: the previous 1.0s rate-limit window was tight enough that
            // a long-playing OrbitalCheckpoint section still emitted ~14
            // lines/min per (rec, section) pair (413 lines in a single 30-min
            // session). Use the default 5s window to keep one line per section
            // per (typical) rate-limit window without losing the section-change
            // signal — the key is per-(recId, sectionIdx) so a new section
            // still logs immediately on first frame.
            ParsekLog.VerboseRateLimited(
                "Playback",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "orbital-checkpoint-point-{0}-{1}",
                    recId,
                    sectionIdx),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "OrbitalCheckpoint point playback: rec={0} currentUT={1:F1} section[{2}] sectionUT={3:F1}-{4:F1} {5} world={6} frames={7}",
                    recId,
                    playbackUT,
                    sectionIdx,
                    section.startUT,
                    section.endUT,
                    pointDetail,
                    ParsekFlight.FormatVector3d(worldPos),
                    section.frames?.Count ?? 0));
        }

        internal bool TryResolvePlaybackWorldPosition(
            int index, IPlaybackTrajectory traj, GhostPlaybackState state,
            double playbackUT, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (traj == null)
                return false;

            int cachedIndex = state != null ? state.playbackIndex : 0;
            if (GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                    traj,
                    playbackUT,
                    out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic retireDiagnostic))
            {
                LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed(
                    traj,
                    index,
                    playbackUT,
                    "TryResolvePlaybackWorldPosition",
                    "body-fixed-primary-unavailable",
                    retireDiagnostic);
                return false;
            }

            bool skipRecordedRelativeResolverForAuthoredFrameGap =
                GhostPlaybackEngine.ShouldSkipRecordedRelativeResolverForAuthoredFrameGap(
                    traj,
                    playbackUT,
                    out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic);
            bool authoredGapHasShadow =
                skipRecordedRelativeResolverForAuthoredFrameGap
                && diagnostic.BodyFixedFramesCoverUT;

            if (!authoredGapHasShadow
                && TryResolveOrbitTailWorldPosition(
                    traj, playbackUT, index * 10000, out worldPos))
            {
                return true;
            }

            if (traj.TrackSections != null && traj.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, playbackUT);
                if (sectionIdx >= 0)
                {
                    var section = traj.TrackSections[sectionIdx];
                    if (section.referenceFrame == ReferenceFrame.Relative)
                    {
                        var target = new RelativeSectionPlaybackTarget(
                            traj.RecordingId,
                            sectionIdx,
                            section);
                        if (!skipRecordedRelativeResolverForAuthoredFrameGap
                            && TryResolveRelativeWorldPosition(
                                section.frames ?? traj.Points,
                                playbackUT,
                                target,
                                traj.RecordingFormatVersion,
                                out worldPos))
                        {
                            return true;
                        }
                        if ((!skipRecordedRelativeResolverForAuthoredFrameGap
                                || diagnostic.BodyFixedFramesCoverUT)
                            && ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                                section, playbackUT, out _, out _))
                        {
                            int absoluteCachedIndex = 0;
                            bool bodyFixedResolved = TryResolvePointWorldPosition(
                                section.bodyFixedFrames,
                                ref absoluteCachedIndex,
                                playbackUT,
                                out worldPos);
                            if (!bodyFixedResolved && skipRecordedRelativeResolverForAuthoredFrameGap)
                            {
                                LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed(
                                    traj,
                                    index,
                                    playbackUT,
                                    "TryResolvePlaybackWorldPosition",
                                    "body-fixed-primary-position-failed",
                                    diagnostic);
                            }
                            return bodyFixedResolved;
                        }
                        if (skipRecordedRelativeResolverForAuthoredFrameGap)
                        {
                            LogPlaybackWorldPositionParentAnchoredDebrisBodyFixedFailClosed(
                                traj,
                                index,
                                playbackUT,
                                "TryResolvePlaybackWorldPosition",
                                diagnostic.BodyFixedFramesCoverUT
                                    ? "body-fixed-primary-position-failed"
                                    : "body-fixed-primary-unavailable",
                                diagnostic);
                        }
                        // Relative sections store metre offsets in the
                        // lat/lon/alt fields. If their anchor cannot resolve,
                        // do not fall through to absolute/interpolated
                        // surface playback or checkpoint recovery.
                        return false;
                    }
                    else if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint
                        && section.frames != null
                        && section.frames.Count > 0
                        && TryResolvePointWorldPosition(
                            section.frames,
                            ref cachedIndex,
                            playbackUT,
                            out worldPos))
                    {
                        LogCheckpointPointPlayback(traj, sectionIdx, section, playbackUT, worldPos);
                        return true;
                    }
                    else if (ParsekFlight.TryGetAbsoluteSectionPlaybackFramesForPlayback(
                        traj.TrackSections,
                        sectionIdx,
                        out List<TrajectoryPoint> bodyFixedFrames,
                        out _))
                    {
                        // Section-local absolute frames are authoritative at
                        // frame boundaries. The flat Points list can contain
                        // adjacent Relative metre-offset samples; interpreting
                        // those as lat/lon/alt causes planet-scale spikes.
                        int sectionCachedIndex = 0;
                        return TryResolvePointWorldPosition(
                            bodyFixedFrames,
                            ref sectionCachedIndex,
                            playbackUT,
                            out worldPos);
                    }
                }
            }

            bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(traj.TrackSections, playbackUT);
            if (TryResolveInterpolatedWorldPosition(
                    traj.Points, traj.OrbitSegments, ref cachedIndex,
                    playbackUT, index * 10000, surfaceSkip, out worldPos,
                    traj.TrackSections))
            {
                return true;
            }

            if (traj.SurfacePos.HasValue)
                return TryResolveSurfaceWorldPosition(traj.SurfacePos.Value, out worldPos);

            return false;
        }

        bool TryResolveInterpolatedWorldPosition(
            List<TrajectoryPoint> points,
            List<OrbitSegment> segments,
            ref int cachedIndex,
            double targetUT,
            int orbitCacheBase,
            bool skipOrbitSegments,
            out Vector3d worldPos,
            List<TrackSection> flatPointSections = null)
        {
            if (!skipOrbitSegments
                && TryResolveOrbitWorldPosition(segments, targetUT, orbitCacheBase, out worldPos))
            {
                return true;
            }

            return TryResolvePointWorldPosition(
                points, ref cachedIndex, targetUT, out worldPos, flatPointSections);
        }

        bool TryResolveOrbitTailWorldPosition(
            IPlaybackTrajectory traj,
            double targetUT,
            int orbitCacheBase,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (!GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment(
                    traj, targetUT, out OrbitSegment segment, out int segmentIndex))
            {
                return false;
            }

            long cacheKey = orbitCacheBase + Math.Max(0, segmentIndex);
            if (!TryGetOrbitForSegment(segment, cacheKey, out Orbit orbit, out CelestialBody body))
                return false;

            worldPos = orbit.getPositionAtUT(targetUT);
            if (TryResolvePredictedOrbitTailContinuityOffset(
                    traj,
                    segment,
                    orbit,
                    targetUT,
                    out Vector3d continuityOffset,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _))
            {
                worldPos += continuityOffset;
            }

            if (!ParsekFlight.IsFiniteVector3d(worldPos))
                return false;

            double orbitAlt = body.GetAltitude(worldPos);
            if (orbitAlt < 0)
            {
                double lat = body.GetLatitude(worldPos);
                double lon = body.GetLongitude(worldPos);
                worldPos = body.GetWorldSurfacePosition(lat, lon, 0);
            }

            return ParsekFlight.IsFiniteVector3d(worldPos);
        }

        internal static bool TryFindAdjacentAbsolutePlaybackFrame(
            List<TrackSection> trackSections,
            int adjacentIdx,
            bool searchForward,
            double requireBeforeUT,
            double requireAfterUT,
            string bodyName,
            out TrajectoryPoint frame)
        {
            frame = default(TrajectoryPoint);
            if (trackSections == null
                || adjacentIdx < 0
                || adjacentIdx >= trackSections.Count)
            {
                return false;
            }

            List<TrajectoryPoint> adjacentFrames =
                GetAbsolutePlaybackFrameList(trackSections[adjacentIdx]);
            if (adjacentFrames == null || adjacentFrames.Count == 0)
                return false;

            if (searchForward)
            {
                for (int i = 0; i < adjacentFrames.Count; i++)
                {
                    TrajectoryPoint candidate = adjacentFrames[i];
                    if (IsEligibleBoundaryBridgeFrame(
                            candidate,
                            requireBeforeUT,
                            requireAfterUT,
                            bodyName))
                    {
                        frame = candidate;
                        return true;
                    }
                }
            }
            else
            {
                for (int i = adjacentFrames.Count - 1; i >= 0; i--)
                {
                    TrajectoryPoint candidate = adjacentFrames[i];
                    if (IsEligibleBoundaryBridgeFrame(
                            candidate,
                            requireBeforeUT,
                            requireAfterUT,
                            bodyName))
                    {
                        frame = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        internal static List<TrajectoryPoint> GetAbsolutePlaybackFrameList(TrackSection section)
        {
            if (section.referenceFrame == ReferenceFrame.Absolute)
                return section.frames;
            if (section.referenceFrame == ReferenceFrame.Relative)
                return section.bodyFixedFrames;
            return null;
        }

        internal static bool IsEligibleBoundaryBridgeFrame(
            TrajectoryPoint candidate,
            double requireBeforeUT,
            double requireAfterUT,
            string bodyName)
        {
            const double epsilonSeconds = 1e-6;
            if (!double.IsNaN(requireBeforeUT)
                && !(candidate.ut + epsilonSeconds < requireBeforeUT))
            {
                return false;
            }

            if (!double.IsNaN(requireAfterUT)
                && !(candidate.ut > requireAfterUT + epsilonSeconds))
            {
                return false;
            }

            return string.IsNullOrEmpty(bodyName)
                || string.IsNullOrEmpty(candidate.bodyName)
                || string.Equals(candidate.bodyName, bodyName, StringComparison.Ordinal);
        }

        private bool TryGetDenseReFlyAbsolutePlaybackFrames(
            IPlaybackTrajectory traj,
            int sectionIdx,
            TrackSection section,
            List<TrajectoryPoint> sectionFrames,
            double playbackUT,
            out List<TrajectoryPoint> denseFrames,
            out string reason)
        {
            denseFrames = null;
            if (section.referenceFrame != ReferenceFrame.Absolute)
            {
                reason = "not-absolute";
                return false;
            }

            if (!PointBelongsToSection(traj?.TrackSections, sectionIdx, section, playbackUT))
            {
                reason = "ut-outside-section";
                return false;
            }

            if (sectionFrames == null || sectionFrames.Count == 0)
            {
                reason = "no-section-frames";
                return false;
            }

            if (traj?.Points == null || traj.Points.Count == 0)
            {
                reason = "no-flat-points";
                return false;
            }

            string recId = string.IsNullOrEmpty(traj?.RecordingId) ? "(null)" : traj.RecordingId;
            string cacheKey = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2:R}|{3:R}|{4}|{5}|{6}",
                recId,
                sectionIdx,
                section.startUT,
                section.endUT,
                traj.Points.Count,
                traj.TrackSections?.Count ?? 0,
                RecordingStore.StateVersion);

            if (!host.reFlyDenseAbsolutePlaybackFrameCache.TryGetValue(
                    cacheKey,
                    out ParsekFlight.ReFlyDenseAbsolutePlaybackFrameCacheEntry entry))
            {
                denseFrames = BuildDensebodyFixedFramesForSection(
                    traj.TrackSections,
                    sectionIdx,
                    section,
                    traj.Points);

                string buildReason = "dense-built";
                if (denseFrames.Count < 2)
                    buildReason = "dense-too-small";
                else if (denseFrames.Count <= sectionFrames.Count)
                    buildReason = "dense-not-more-populated";
                else
                    buildReason = "dense-built-eligible";

                entry = new ParsekFlight.ReFlyDenseAbsolutePlaybackFrameCacheEntry(
                    buildReason == "dense-built-eligible" ? denseFrames : null,
                    denseFrames.Count,
                    buildReason);
                host.reFlyDenseAbsolutePlaybackFrameCache[cacheKey] = entry;
            }

            if (!entry.Eligible)
            {
                reason = entry.BuildReason;
                return false;
            }

            denseFrames = entry.Frames;
            if (!TryGetBracketWidthSeconds(
                    sectionFrames,
                    playbackUT,
                    out double sectionBracketSeconds))
            {
                reason = "no-section-bracket";
                return false;
            }

            if (!TryGetBracketWidthSeconds(
                    denseFrames,
                    playbackUT,
                    out double denseBracketSeconds))
            {
                reason = "no-dense-bracket";
                return false;
            }

            if (!(denseBracketSeconds + 1e-6 < sectionBracketSeconds))
            {
                reason = "dense-bracket-not-tighter";
                return false;
            }

            reason = "dense-bracket-tighter";
            if (host.loggedReFlyDenseAbsolutePlaybackFrameSelections.Add(cacheKey))
            {
                ParsekLog.Info("Playback",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Re-Fly PointInterp using dense flat samples: rec={0} section={1} sectionUT=[{2:F3},{3:F3}] sectionFrames={4} denseFrames={5} sectionBracketSeconds={6:F3} denseBracketSeconds={7:F3} reason={8} buildReason={9}",
                        recId,
                        sectionIdx,
                        section.startUT,
                        section.endUT,
                        sectionFrames?.Count ?? 0,
                        entry.DenseFrameCount,
                        sectionBracketSeconds,
                        denseBracketSeconds,
                        reason,
                        entry.BuildReason));
            }

            return true;
        }

        private static List<TrajectoryPoint> BuildDensebodyFixedFramesForSection(
            List<TrackSection> trackSections,
            int sectionIdx,
            TrackSection section,
            List<TrajectoryPoint> flatPoints)
        {
            var frames = new List<TrajectoryPoint>();
            if (flatPoints == null)
                return frames;

            for (int i = 0; i < flatPoints.Count; i++)
            {
                TrajectoryPoint point = flatPoints[i];
                if (PointBelongsToSection(trackSections, sectionIdx, section, point.ut))
                    frames.Add(point);
            }

            return frames;
        }

        internal static int CountDensebodyFixedFramesForSection(
            List<TrackSection> trackSections,
            int sectionIdx,
            TrackSection section,
            List<TrajectoryPoint> flatPoints)
        {
            if (flatPoints == null)
                return 0;

            int count = 0;
            for (int i = 0; i < flatPoints.Count; i++)
            {
                if (PointBelongsToSection(trackSections, sectionIdx, section, flatPoints[i].ut))
                    count++;
            }

            return count;
        }

        internal static bool TryGetDenseSectionBracketWidthSeconds(
            List<TrackSection> trackSections,
            int sectionIdx,
            TrackSection section,
            List<TrajectoryPoint> points,
            double playbackUT,
            out double bracketSeconds)
        {
            bracketSeconds = double.NaN;
            if (points == null || points.Count == 0)
                return false;

            bool hasBefore = false;
            bool hasAfter = false;
            double beforeUT = 0.0;
            double afterUT = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                TrajectoryPoint point = points[i];
                if (!PointBelongsToSection(trackSections, sectionIdx, section, point.ut))
                    continue;

                if (point.ut <= playbackUT && (!hasBefore || point.ut > beforeUT))
                {
                    beforeUT = point.ut;
                    hasBefore = true;
                }

                if (point.ut >= playbackUT && (!hasAfter || point.ut < afterUT))
                {
                    afterUT = point.ut;
                    hasAfter = true;
                }
            }

            if (!hasBefore || !hasAfter)
                return false;

            bracketSeconds = System.Math.Max(0.0, afterUT - beforeUT);
            return true;
        }

        internal static bool TryGetBracketWidthSeconds(
            List<TrajectoryPoint> points,
            double playbackUT,
            out double bracketSeconds)
        {
            bracketSeconds = double.NaN;
            if (points == null || points.Count < 2)
                return false;

            int cachedIndex = 0;
            if (!TrajectoryMath.InterpolatePoints(
                    points,
                    ref cachedIndex,
                    playbackUT,
                    out TrajectoryPoint before,
                    out TrajectoryPoint after,
                    out float ignoredT))
            {
                return false;
            }

            bracketSeconds = System.Math.Max(0.0, after.ut - before.ut);
            return true;
        }

        internal static bool PointBelongsToSection(
            List<TrackSection> trackSections,
            int sectionIdx,
            TrackSection section,
            double ut)
        {
            if (trackSections != null
                && sectionIdx >= 0
                && sectionIdx < trackSections.Count)
            {
                return TrajectoryMath.FindTrackSectionForUT(trackSections, ut) == sectionIdx;
            }

            return ut >= section.startUT && ut <= section.endUT;
        }

        bool TryResolvePointWorldPosition(
            List<TrajectoryPoint> points,
            ref int cachedIndex,
            double targetUT,
            out Vector3d worldPos,
            List<TrackSection> flatPointSections = null)
        {
            worldPos = Vector3d.zero;
            if (points == null || points.Count == 0)
                return false;

            TrajectoryPoint before;
            TrajectoryPoint after;
            float t;
            if (points.Count == 1)
            {
                before = points[0];
                after = points[0];
                if (flatPointSections != null
                    && !TryDispatchFlatPointEndpoints(
                        flatPointSections, ref before, ref after, targetUT, null, "world-resolve-flat-points"))
                {
                    return false;
                }
                return TryResolveTrajectoryPointWorldPosition(before, out worldPos);
            }

            bool hasSegment = TrajectoryMath.InterpolatePoints(
                points, ref cachedIndex, targetUT, out before, out after, out t);
            if (!hasSegment)
                after = before;
            if (flatPointSections != null
                && !TryDispatchFlatPointEndpoints(
                    flatPointSections, ref before, ref after, targetUT, null, "world-resolve-flat-points"))
            {
                return false;
            }
            if (!hasSegment)
            {
                return points != null
                    && points.Count > 0
                    && TryResolveTrajectoryPointWorldPosition(before, out worldPos);
            }

            if (t == 0f && before.ut == after.ut)
                return TryResolveTrajectoryPointWorldPosition(before, out worldPos);

            CelestialBody bodyBefore = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies?.Find(b => b.name == after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
                return false;

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);

            worldPos = Vector3d.Lerp(posBefore, posAfter, t);
            if (double.IsNaN(worldPos.x) || double.IsNaN(worldPos.y) || double.IsNaN(worldPos.z))
                worldPos = posBefore;
            return true;
        }

        bool TryResolveTrajectoryPointWorldPosition(TrajectoryPoint point, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == point.bodyName);
            if (body == null)
                return false;

            worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);
            return true;
        }

        bool TryResolveOrbitWorldPosition(
            List<OrbitSegment> segments, double targetUT, int orbitCacheBase, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (segments == null || segments.Count == 0)
                return false;

            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment seg = segments[i];
                if (targetUT < seg.startUT || targetUT > seg.endUT)
                    continue;

                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == seg.bodyName);
                if (body == null)
                    return false;

                if (!TrajectoryMath.HasUsableOrbitSegmentElements(seg))
                    return false;

                Orbit orbit;
                int cacheKey = orbitCacheBase + i;
                if (!host.orbitCache.TryGetValue(cacheKey, out orbit))
                {
                    try
                    {
                        orbit = new Orbit(
                            seg.inclination,
                            seg.eccentricity,
                            seg.semiMajorAxis,
                            seg.longitudeOfAscendingNode,
                            seg.argumentOfPeriapsis,
                            seg.meanAnomalyAtEpoch,
                            seg.epoch,
                            body);
                    }
                    catch
                    {
                        return false;
                    }
                    host.orbitCache[cacheKey] = orbit;
                }

                try
                {
                    worldPos = orbit.getPositionAtUT(targetUT);
                    if (!ParsekFlight.IsFiniteVector3d(worldPos))
                        return false;

                    if (body.GetAltitude(worldPos) < 0)
                    {
                        double lat = body.GetLatitude(worldPos);
                        double lon = body.GetLongitude(worldPos);
                        worldPos = body.GetWorldSurfacePosition(lat, lon, 0);
                        if (!ParsekFlight.IsFiniteVector3d(worldPos))
                            return false;
                    }
                }
                catch
                {
                    return false;
                }

                return true;
            }

            return false;
        }

        bool TryResolveRelativeWorldPosition(
            List<TrajectoryPoint> frames,
            double targetUT,
            RelativeSectionPlaybackTarget target,
            int recordingFormatVersion,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (frames == null || frames.Count == 0)
                return false;
            if (frames.Count == 1)
            {
                return TryResolveRelativeOffsetWorldPosition(
                    frames[0].latitude, frames[0].longitude, frames[0].altitude,
                    target, targetUT,
                    recordingFormatVersion, out worldPos);
            }

            int cachedIndex = 0;
            int indexBefore = TrajectoryMath.FindWaypointIndex(frames, ref cachedIndex, targetUT);
            if (indexBefore < 0)
                return TryResolveRelativeOffsetWorldPosition(
                    frames[0].latitude, frames[0].longitude, frames[0].altitude,
                    target, frames[0].ut,
                    recordingFormatVersion, out worldPos);
            if (indexBefore >= frames.Count - 1)
            {
                return TryResolveRelativeOffsetWorldPosition(
                    frames[frames.Count - 1].latitude,
                    frames[frames.Count - 1].longitude,
                    frames[frames.Count - 1].altitude,
                    target, frames[frames.Count - 1].ut,
                    recordingFormatVersion, out worldPos);
            }

            TrajectoryPoint before = frames[indexBefore];
            TrajectoryPoint after = frames[indexBefore + 1];
            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                return TryResolveRelativeOffsetWorldPosition(
                    before.latitude, before.longitude, before.altitude,
                    target, before.ut,
                    recordingFormatVersion, out worldPos);
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            double dx = before.latitude + (after.latitude - before.latitude) * t;
            double dy = before.longitude + (after.longitude - before.longitude) * t;
            double dz = before.altitude + (after.altitude - before.altitude) * t;
            return TryResolveRelativeOffsetWorldPosition(
                dx,
                dy,
                dz,
                target,
                targetUT,
                recordingFormatVersion,
                out worldPos);
        }

        bool TryResolveRelativeOffsetWorldPosition(
            double dx,
            double dy,
            double dz,
            RelativeSectionPlaybackTarget target,
            double targetUT,
            int recordingFormatVersion,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            ParsekFlight.RelativeAnchorPose anchorPose;
            if (!TryResolveRecordedRelativeAnchorPose(
                    target,
                    targetUT,
                    out anchorPose,
                    out RelativeAnchorResolveFailure failure))
            {
                string reason = RelativeAnchorResolveFailure.ReasonOrFallback(
                    failure,
                    "anchor-pose-unresolved");
                GhostRenderTrace.EmitRelativeResolver(
                    target.RecordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "recorded-relative-offset",
                    reason,
                    0u,
                    target.AnchorRecordingId,
                    success: false,
                    fromRecordedTrajectory: true,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: Vector3d.zero);
                return false;
            }

            worldPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                anchorPose.worldPos,
                anchorPose.worldRotation,
                dx,
                dy,
                dz);
            if (double.IsNaN(worldPos.x) || double.IsNaN(worldPos.y) || double.IsNaN(worldPos.z))
                worldPos = anchorPose.worldPos;
            GhostRenderTrace.EmitRelativeResolver(
                target.RecordingId,
                -1,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                targetUT,
                "recorded-relative-offset",
                "resolved",
                0u,
                target.AnchorRecordingId,
                success: true,
                fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                anchorPosition: anchorPose.worldPos,
                anchorRotation: anchorPose.worldRotation,
                localOffset: new Vector3d(dx, dy, dz),
                outputPosition: worldPos);
            return true;
        }

        internal bool TryResolveLoopLiveAnchorPose(
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT,
            out ParsekFlight.RelativeAnchorPose pose)
        {
            pose = default(ParsekFlight.RelativeAnchorPose);
            if (anchorVesselId == 0u)
            {
                GhostRenderTrace.EmitRelativeResolver(
                    victimRecordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-live-anchor",
                    "missing-anchor-pid",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                return false;
            }

            Vessel anchor = FlightRecorder.FindVesselByPid(anchorVesselId);
            if (anchor == null)
            {
                GhostRenderTrace.EmitRelativeResolver(
                    victimRecordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-live-anchor",
                    "anchor-not-loaded",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                return false;
            }

            // Match the recorder's live-anchor frame (FlightRecorder /
            // BackgroundRecorder TryResolveAnchorPoseForCandidate). The recorder
            // writes loop-relative offsets against vesselTransform.position; the
            // matching rotation is vesselTransform.rotation. GetWorldPos3D (CoMD)
            // here would re-introduce the same CoM-vs-vesselTransform mismatch
            // the recorder fix removed, but inverted at playback time. Use
            // anchor.transform explicitly (not GetTransform(), which follows
            // ReferenceTransform / "Control From Here").
            Transform anchorTransform = anchor.transform;
            Quaternion anchorRotation = anchorTransform != null
                ? anchorTransform.rotation
                : Quaternion.identity;
            Vector3d anchorWorld = anchorTransform != null
                ? (Vector3d)anchorTransform.position
                : anchor.GetWorldPos3D();
            if (!ParsekFlight.IsFiniteVector3d(anchorWorld))
            {
                GhostRenderTrace.EmitRelativeResolver(
                    victimRecordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-live-anchor",
                    "non-finite-live-anchor",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: anchorWorld,
                    anchorRotation: anchorRotation,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                return false;
            }

            pose.worldPos = anchorWorld;
            pose.worldRotation = anchorRotation;
            pose.fromRecordedTrajectory = false;
            GhostRenderTrace.EmitRelativeResolver(
                victimRecordingId,
                -1,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                targetUT,
                "loop-live-anchor",
                "live-selected",
                anchorVesselId,
                null,
                success: true,
                fromRecordedTrajectory: false,
                anchorPosition: pose.worldPos,
                anchorRotation: pose.worldRotation,
                localOffset: Vector3d.zero,
                outputPosition: pose.worldPos);
            return true;
        }

        internal static string ShortRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return "-";
            return recordingId.Length <= 8 ? recordingId : recordingId.Substring(0, 8);
        }

        bool TryResolveActiveReFlyPid(
            ReFlySessionMarker marker,
            IReadOnlyList<RecordingTree> searchTrees,
            out uint activeReFlyPid)
        {
            activeReFlyPid = 0u;
            if (marker == null || string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                return false;

            if (searchTrees != null)
            {
                for (int t = 0; t < searchTrees.Count; t++)
                {
                    RecordingTree tree = searchTrees[t];
                    if (tree?.Recordings == null)
                        continue;
                    Recording rec;
                    if (tree.Recordings.TryGetValue(marker.ActiveReFlyRecordingId, out rec)
                        && rec != null
                        && rec.VesselPersistentId != 0u)
                    {
                        activeReFlyPid = rec.VesselPersistentId;
                        return true;
                    }
                }
            }

            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    Recording rec = committed[i];
                    if (rec == null
                        || !string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId, StringComparison.Ordinal)
                        || rec.VesselPersistentId == 0u)
                    {
                        continue;
                    }

                    activeReFlyPid = rec.VesselPersistentId;
                    return true;
                }
            }

            return false;
        }

        bool TryResolveRecordedAnchorPoseWithCoverage(
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT,
            IReadOnlyList<RecordingTree> searchTrees,
            ReFlySessionMarker marker,
            bool bypassLiveAnchorForActiveReFly,
            bool requireCoverage,
            out ParsekFlight.RelativeAnchorPose pose)
        {
            pose = default(ParsekFlight.RelativeAnchorPose);
            if (anchorVesselId == 0u)
                return false;

            HashSet<string> seenRecordingIds = recordedAnchorSeenRecordingIds;
            seenRecordingIds.Clear();
            if (TryResolvePreReFlyFrozenAnchorPose(
                    anchorVesselId,
                    victimRecordingId,
                    targetUT,
                    searchTrees,
                    marker,
                    bypassLiveAnchorForActiveReFly,
                    seenRecordingIds,
                    requireCoverage,
                    out pose))
            {
                seenRecordingIds.Clear();
                return true;
            }

            if (searchTrees != null)
            {
                for (int t = 0; t < searchTrees.Count; t++)
                {
                    RecordingTree tree = searchTrees[t];
                    if (tree?.Recordings == null)
                        continue;
                    foreach (Recording rec in tree.Recordings.Values)
                    {
                        if (ParsekFlight.ShouldSkipMutableActiveReFlyAnchorCandidate(
                                rec, marker, bypassLiveAnchorForActiveReFly))
                        {
                            continue;
                        }
                        if (TryResolveRecordedAnchorPoseFromCandidate(
                                rec, anchorVesselId, victimRecordingId,
                                targetUT, seenRecordingIds, requireCoverage, out pose))
                        {
                            seenRecordingIds.Clear();
                            return true;
                        }
                    }
                }
            }

            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    if (ParsekFlight.ShouldSkipMutableActiveReFlyAnchorCandidate(
                            committed[i], marker, bypassLiveAnchorForActiveReFly))
                    {
                        continue;
                    }
                    if (TryResolveRecordedAnchorPoseFromCandidate(
                            committed[i], anchorVesselId, victimRecordingId,
                            targetUT, seenRecordingIds, requireCoverage, out pose))
                    {
                        seenRecordingIds.Clear();
                        return true;
                    }
                }
            }

            seenRecordingIds.Clear();
            return false;
        }

        bool TryResolvePreReFlyFrozenAnchorPose(
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT,
            IReadOnlyList<RecordingTree> searchTrees,
            ReFlySessionMarker marker,
            bool bypassLiveAnchorForActiveReFly,
            HashSet<string> seenRecordingIds,
            bool requireCoverage,
            out ParsekFlight.RelativeAnchorPose pose)
        {
            pose = default(ParsekFlight.RelativeAnchorPose);
            if (!bypassLiveAnchorForActiveReFly)
                return false;
            if (marker == null || string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                return false;

            Recording liveActive = FindActiveReFlyRecording(marker, searchTrees);
            if (!ParsekFlight.ShouldUsePreReFlyAnchorTrajectory(
                    liveActive,
                    anchorVesselId,
                    victimRecordingId,
                    marker))
            {
                return false;
            }

            Recording frozen = liveActive.BuildPreReFlyAnchorTrajectoryRecording(marker.SessionId);
            if (frozen == null)
                return false;

            return TryResolveRecordedAnchorPoseFromCandidate(
                frozen,
                anchorVesselId,
                victimRecordingId,
                targetUT,
                seenRecordingIds,
                requireCoverage,
                out pose);
        }

        Recording FindActiveReFlyRecording(
            ReFlySessionMarker marker,
            IReadOnlyList<RecordingTree> searchTrees)
        {
            if (marker == null || string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                return null;

            if (searchTrees != null)
            {
                for (int t = 0; t < searchTrees.Count; t++)
                {
                    RecordingTree tree = searchTrees[t];
                    if (tree?.Recordings == null)
                        continue;
                    Recording rec;
                    if (tree.Recordings.TryGetValue(marker.ActiveReFlyRecordingId, out rec)
                        && rec != null)
                    {
                        return rec;
                    }
                }
            }

            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    Recording rec = committed[i];
                    if (rec != null
                        && string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId, StringComparison.Ordinal))
                    {
                        return rec;
                    }
                }
            }

            return null;
        }

        bool TryResolveRecordedAnchorPoseFromCandidate(
            Recording rec,
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT,
            HashSet<string> seenRecordingIds,
            bool requireCoverage,
            out ParsekFlight.RelativeAnchorPose pose)
        {
            pose = default(ParsekFlight.RelativeAnchorPose);
            if (rec == null || rec.VesselPersistentId != anchorVesselId)
                return false;
            if (!string.IsNullOrEmpty(rec.RecordingId))
            {
                if (!string.IsNullOrEmpty(victimRecordingId)
                    && string.Equals(rec.RecordingId, victimRecordingId, StringComparison.Ordinal))
                    return false;
                if (seenRecordingIds != null && !seenRecordingIds.Add(rec.RecordingId))
                    return false;
            }

            if (rec.TrackSections != null && rec.TrackSections.Count > 0)
            {
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, targetUT);
                if (sectionIdx < 0)
                {
                    if (requireCoverage)
                        return false;

                    if (!TryResolveNearestRecordedAnchorAbsoluteSectionPose(
                            rec, targetUT, out pose, out double fallbackGapSeconds))
                    {
                        return false;
                    }

                    MaybeWarnLargeRecordedAnchorFallbackGap(
                        rec, anchorVesselId, victimRecordingId, targetUT, fallbackGapSeconds);
                    return true;
                }

                TrackSection section = rec.TrackSections[sectionIdx];
                if (section.referenceFrame != ReferenceFrame.Absolute)
                {
                    if (requireCoverage)
                        return false;

                    if (!TryResolveNearestRecordedAnchorAbsoluteSectionPose(
                            rec, targetUT, out pose, out double fallbackGapSeconds))
                    {
                        return false;
                    }

                    MaybeWarnLargeRecordedAnchorFallbackGap(
                        rec, anchorVesselId, victimRecordingId, targetUT, fallbackGapSeconds);
                    return true;
                }

                List<TrajectoryPoint> anchorPoints =
                    section.frames != null && section.frames.Count > 0
                        ? section.frames
                        : rec.Points;
                bool resolved = TryResolveRecordedAnchorPointPose(
                    anchorPoints,
                    targetUT,
                    requireCoverage: requireCoverage,
                    out pose);
                if (resolved && !requireCoverage)
                {
                    MaybeWarnLargeRecordedAnchorFallbackGap(
                        rec,
                        anchorVesselId,
                        victimRecordingId,
                        targetUT,
                        ParsekFlight.DistanceOutsideRecordedAnchorCoverage(anchorPoints, targetUT));
                }

                return resolved;
            }

            bool resolvedFromPoints = TryResolveRecordedAnchorPointPose(
                rec.Points,
                targetUT,
                requireCoverage: requireCoverage,
                out pose);
            if (resolvedFromPoints && !requireCoverage)
            {
                MaybeWarnLargeRecordedAnchorFallbackGap(
                    rec,
                    anchorVesselId,
                    victimRecordingId,
                    targetUT,
                    ParsekFlight.DistanceOutsideRecordedAnchorCoverage(rec.Points, targetUT));
            }

            return resolvedFromPoints;
        }

        bool TryResolveNearestRecordedAnchorAbsoluteSectionPose(
            Recording rec,
            double targetUT,
            out ParsekFlight.RelativeAnchorPose pose,
            out double fallbackGapSeconds)
        {
            pose = default(ParsekFlight.RelativeAnchorPose);
            fallbackGapSeconds = double.MaxValue;
            if (rec?.TrackSections == null || rec.TrackSections.Count == 0)
                return false;

            List<TrajectoryPoint> bestPoints = null;
            double bestGap = double.MaxValue;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection section = rec.TrackSections[i];
                if (section.referenceFrame != ReferenceFrame.Absolute)
                    continue;

                List<TrajectoryPoint> points =
                    section.frames != null && section.frames.Count > 0
                        ? section.frames
                        : rec.Points;
                if (points == null || points.Count == 0)
                    continue;

                double gap = ParsekFlight.DistanceOutsideRecordedAnchorCoverage(points, targetUT);
                if (gap < bestGap)
                {
                    bestGap = gap;
                    bestPoints = points;
                }
            }

            bool resolved = TryResolveRecordedAnchorPointPose(
                bestPoints,
                targetUT,
                requireCoverage: false,
                out pose);
            if (resolved)
                fallbackGapSeconds = bestGap;
            return resolved;
        }

        void MaybeWarnLargeRecordedAnchorFallbackGap(
            Recording rec,
            uint anchorVesselId,
            string victimRecordingId,
            double targetUT,
            double fallbackGapSeconds)
        {
            if (!ParsekFlight.ShouldWarnRecordedAnchorFallbackGap(fallbackGapSeconds))
                return;

            string anchorRecordingId = rec?.RecordingId;
            string key = string.Format(CultureInfo.InvariantCulture,
                "recorded-anchor-fallback-gap|anchorRec={0}|victimRec={1}|anchorPid={2}",
                string.IsNullOrEmpty(anchorRecordingId) ? "(none)" : anchorRecordingId,
                string.IsNullOrEmpty(victimRecordingId) ? "(none)" : victimRecordingId,
                anchorVesselId);
            ParsekLog.WarnRateLimited(
                "Anchor",
                key,
                ParsekFlight.BuildRecordedAnchorFallbackGapLog(
                    anchorRecordingId,
                    victimRecordingId,
                    anchorVesselId,
                    targetUT,
                    fallbackGapSeconds),
                minIntervalSeconds: 30.0);
        }

        internal static string BuildRecordedRelativeOffsetAppliedLog(
            int recordingFormatVersion,
            double dx,
            double dy,
            double dz,
            string anchorRecordingId)
        {
            return $"RELATIVE playback: contract=anchor-local " +
                $"version={recordingFormatVersion.ToString(CultureInfo.InvariantCulture)} " +
                $"dx={dx.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"dy={dy.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"dz={dz.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"|offset|={System.Math.Sqrt(dx * dx + dy * dy + dz * dz).ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"anchorRec={(string.IsNullOrEmpty(anchorRecordingId) ? "(missing)" : anchorRecordingId)} source=recorded";
        }

        bool TryResolveRecordedAnchorPointPose(
            List<TrajectoryPoint> points,
            double targetUT,
            bool requireCoverage,
            out ParsekFlight.RelativeAnchorPose pose)
        {
            pose = default(ParsekFlight.RelativeAnchorPose);
            if (points == null || points.Count == 0)
                return false;
            if (requireCoverage && !ParsekFlight.RecordedAnchorPointListCoversUT(points, targetUT))
                return false;
            if (points.Count == 1)
                return TryBuildRecordedAnchorPoseFromPoint(points[0], out pose);

            int cachedIndex = 0;
            TrajectoryPoint before;
            TrajectoryPoint after;
            float t;
            bool hasSegment = TrajectoryMath.InterpolatePoints(
                points, ref cachedIndex, targetUT, out before, out after, out t);
            if (!hasSegment)
                return requireCoverage
                    ? false
                    : TryBuildRecordedAnchorPoseFromPoint(before, out pose);

            CelestialBody bodyBefore = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
            CelestialBody bodyAfter = FlightGlobals.Bodies?.Find(b => b.name == after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
                return false;

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);
            Quaternion surfaceRot = TrajectoryMath.SanitizeQuaternion(
                Quaternion.Slerp(before.rotation, after.rotation, t));

            pose.worldPos = Vector3d.Lerp(posBefore, posAfter, t);
            pose.worldRotation = bodyBefore.bodyTransform.rotation * surfaceRot;
            pose.fromRecordedTrajectory = true;
            pose.hasSurfacePose = true;
            pose.bodyBefore = bodyBefore;
            pose.bodyAfter = bodyAfter;
            pose.latBefore = before.latitude;
            pose.lonBefore = before.longitude;
            pose.altBefore = before.altitude;
            pose.latAfter = after.latitude;
            pose.lonAfter = after.longitude;
            pose.altAfter = after.altitude;
            pose.t = t;
            pose.surfaceRot = surfaceRot;
            return ParsekFlight.IsFiniteVector3d(pose.worldPos);
        }

        bool TryBuildRecordedAnchorPoseFromPoint(
            TrajectoryPoint point, out ParsekFlight.RelativeAnchorPose pose)
        {
            pose = default(ParsekFlight.RelativeAnchorPose);
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == point.bodyName);
            if (body == null || body.bodyTransform == null)
                return false;

            Quaternion surfaceRot = TrajectoryMath.SanitizeQuaternion(point.rotation);
            pose.worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);
            pose.worldRotation = body.bodyTransform.rotation * surfaceRot;
            pose.fromRecordedTrajectory = true;
            pose.hasSurfacePose = true;
            pose.bodyBefore = body;
            pose.bodyAfter = body;
            pose.latBefore = point.latitude;
            pose.lonBefore = point.longitude;
            pose.altBefore = point.altitude;
            pose.latAfter = point.latitude;
            pose.lonAfter = point.longitude;
            pose.altAfter = point.altitude;
            pose.t = 0f;
            pose.surfaceRot = surfaceRot;
            return ParsekFlight.IsFiniteVector3d(pose.worldPos);
        }

        bool TryResolveSurfaceWorldPosition(SurfacePosition surfacePos, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == surfacePos.body);
            if (body == null)
                return false;

            worldPos = body.GetWorldSurfacePosition(
                surfacePos.latitude, surfacePos.longitude, surfacePos.altitude);
            return true;
        }

        /// <summary>
        /// Unified positioning method for loop ghosts. Selects between anchor-relative
        /// (InterpolateAndPositionRelative) and absolute (InterpolateAndPosition) based
        /// on the useAnchor flag and the TrackSection at the given loopUT.
        /// </summary>
        void PositionLoopGhost(
            GameObject ghost,
            IPlaybackTrajectory rec,
            ref int playbackIdx,
            double loopUT,
            int recIdx,
            int ghostIdSalt,
            bool useAnchor,
            GhostPlaybackState retireSignalState,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            if (TryInterpolateAndPositionCheckpointSection(
                recIdx,
                rec,
                ghost,
                ref playbackIdx,
                loopUT,
                allowActivation,
                out interpResult))
            {
                return;
            }

            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, loopUT);
            if (sectionIdx >= 0 && rec.TrackSections[sectionIdx].referenceFrame == ReferenceFrame.Relative)
            {
                var section = rec.TrackSections[sectionIdx];
                var sectionFrames = section.frames ?? rec.Points;
                uint anchorVesselId = section.anchorVesselId != 0
                    ? section.anchorVesselId
                    : rec.LoopAnchorVesselId;

                if (useAnchor)
                {
                    long relKey = ((long)recIdx << 32) | anchorVesselId;
                    if (host.loggedRelativeStart.Add(relKey))
                        ParsekLog.Info("Loop",
                            $"Anchor-relative loop playback started: recording #{recIdx} " +
                            $"\"{rec.VesselName}\" anchorPid={anchorVesselId} " +
                            $"contract=anchor-local " +
                            $"version={rec.RecordingFormatVersion} " +
                            $"sectionUT=[{section.startUT:F1},{section.endUT:F1}]");

                    InterpolateAndPositionLoopRelative(
                        ghost, sectionFrames, ref playbackIdx, loopUT,
                        anchorVesselId, rec.RecordingFormatVersion,
                        recIdx, rec.RecordingId, rec.VesselName, retireSignalState,
                        allowActivation, out interpResult);
                    if (GhostPlaybackEngine.IsInterpolationResultValid(interpResult))
                        interpResult = WithRelativeSectionSurfaceVelocity(interpResult, section, loopUT);
                    return;
                }

                if (ghost.activeSelf) ghost.SetActive(false);
                if (retireSignalState != null)
                    retireSignalState.anchorRetiredThisFrame = true;
                long missingAnchorKey = RelativeAnchorResolution.DedupeKey(recIdx, anchorVesselId);
                if (host.loggedAnchorNotFound.Add(missingAnchorKey))
                    ParsekLog.Warn("Anchor",
                        RelativeAnchorResolution.FormatRetiredMessage(
                            recIdx,
                            rec.VesselName,
                            anchorVesselId,
                            "PositionLoopGhost"));
                interpResult = InterpolationResult.Zero;
                return;
            }

            if (sectionIdx >= 0
                && ParsekFlight.TryGetAbsoluteSectionPlaybackFramesForPlayback(
                    rec.TrackSections,
                    sectionIdx,
                    out List<TrajectoryPoint> absoluteSectionFrames,
                    out _))
            {
                InterpolateAndPosition(
                    ghost,
                    absoluteSectionFrames,
                    null,
                    ref playbackIdx,
                    loopUT,
                    ghostIdSalt,
                    out interpResult,
                    allowActivation: allowActivation,
                    skipOrbitSegments: true,
                    recordingId: rec.RecordingId,
                    sectionIndex: sectionIdx);
                return;
            }

            // Absolute positioning fallback
            // Phase 1: pass section context for spline lookup. The fallback is
            // the body-fixed loop path, which is exactly where ABSOLUTE
            // ExoPropulsive / ExoBallistic splines are meant to apply.
            int splineSectionIdx = sectionIdx;
            InterpolateAndPosition(ghost, rec.Points, rec.OrbitSegments,
                ref playbackIdx, loopUT, ghostIdSalt, out interpResult,
                allowActivation: allowActivation,
                recordingId: rec.RecordingId, sectionIndex: splineSectionIdx,
                pointFrameSource: "loop-flat-points",
                flatPointSections: rec.TrackSections);
        }

        /// <summary>
        /// Positions a ghost using RELATIVE reference frame data.
        /// Ghost position/rotation resolve through the recording's versioned RELATIVE
        /// contract (legacy world-offset v5-and-older, anchor-local v6+).
        /// </summary>
        void InterpolateAndPositionRecordedRelative(
            GameObject ghost,
            IPlaybackTrajectory traj,
            List<TrajectoryPoint> frames,
            ref int cachedIndex,
            double targetUT,
            RelativeSectionPlaybackTarget target,
            int recordingFormatVersion,
            int recordingIndex,
            string recordingVesselName,
            GhostPlaybackState retireSignalState,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            if (GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                    traj,
                    targetUT,
                    out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic))
            {
                RetireUnresolvedRecordedRelative(
                    ghost,
                    recordingIndex,
                    recordingVesselName,
                    target,
                    retireSignalState,
                    "InterpolateAndPositionRecordedRelative",
                    resolverReason:
                        "parent-anchored-debris-outside-relative-coverage coverageReason="
                        + (diagnostic.Reason ?? "relative-and-body-fixed-frames-out-of-range"));
                interpResult = InterpolationResult.Zero;
                return;
            }

            if (GhostPlaybackEngine.ShouldSkipRecordedRelativeResolverForAuthoredFrameGap(
                    traj,
                    targetUT,
                    out diagnostic))
            {
                RetireUnresolvedRecordedRelative(
                    ghost,
                    recordingIndex,
                    recordingVesselName,
                    target,
                    retireSignalState,
                    "InterpolateAndPositionRecordedRelative",
                    resolverReason:
                        "body-fixed-primary-required; recorded-relative fallback suppressed coverageReason="
                        + (diagnostic.Reason ?? "covered-by-body-fixed-primary"));
                interpResult = InterpolationResult.Zero;
                return;
            }

            if (frames == null || frames.Count == 0)
            {
                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
                return;
            }

            int indexBefore = TrajectoryMath.FindWaypointIndex(frames, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                if (TryPositionGhostRecordedRelativeAt(
                        ghost,
                        frames[0],
                        target,
                        recordingFormatVersion,
                        allowActivation,
                        out double firstAltitude,
                        out RelativeAnchorResolveFailure firstFailure))
                {
                    interpResult = new InterpolationResult(
                        frames[0].velocity,
                        frames[0].bodyName,
                        firstAltitude);
                    return;
                }

                if (TryUseRelativeBodyFixedPrimaryFallback(
                        recordingIndex,
                        traj,
                        retireSignalState,
                        target,
                        targetUT,
                        ref cachedIndex,
                        out interpResult,
                        resolverFailure: firstFailure))
                    return;

                RetireUnresolvedRecordedRelative(
                    ghost,
                    recordingIndex,
                    recordingVesselName,
                    target,
                    retireSignalState,
                    "InterpolateAndPositionRecordedRelative",
                    firstFailure.Outcome.ToString(),
                    RelativeAnchorResolveFailure.ReasonOrFallback(firstFailure, null));
                interpResult = InterpolationResult.Zero;
                return;
            }

            TrajectoryPoint before = frames[indexBefore];
            TrajectoryPoint after = frames[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                if (TryPositionGhostRecordedRelativeAt(
                        ghost,
                        before,
                        target,
                        recordingFormatVersion,
                        allowActivation,
                        out double pointAltitude,
                        out RelativeAnchorResolveFailure pointFailure))
                {
                    interpResult = new InterpolationResult(
                        before.velocity,
                        before.bodyName,
                        pointAltitude);
                    return;
                }

                if (TryUseRelativeBodyFixedPrimaryFallback(
                        recordingIndex,
                        traj,
                        retireSignalState,
                        target,
                        targetUT,
                        ref cachedIndex,
                        out interpResult,
                        resolverFailure: pointFailure))
                    return;

                RetireUnresolvedRecordedRelative(
                    ghost,
                    recordingIndex,
                    recordingVesselName,
                    target,
                    retireSignalState,
                    "InterpolateAndPositionRecordedRelative",
                    pointFailure.Outcome.ToString(),
                    RelativeAnchorResolveFailure.ReasonOrFallback(pointFailure, null));
                interpResult = InterpolationResult.Zero;
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            Vector3d beforeOffset = new Vector3d(before.latitude, before.longitude, before.altitude);
            Vector3d afterOffset = new Vector3d(after.latitude, after.longitude, after.altitude);
            bool correctedBefore = ParsekFlight.TryCorrectIsolatedRecordedRelativeOffsetSpike(
                frames,
                indexBefore,
                out Vector3d correctedBeforeOffset,
                out double beforeSpikeDeviationMeters);
            bool correctedAfter = ParsekFlight.TryCorrectIsolatedRecordedRelativeOffsetSpike(
                frames,
                indexBefore + 1,
                out Vector3d correctedAfterOffset,
                out double afterSpikeDeviationMeters);
            if (correctedBefore)
                beforeOffset = correctedBeforeOffset;
            if (correctedAfter)
                afterOffset = correctedAfterOffset;
            if (correctedBefore || correctedAfter)
                LogRecordedRelativeOffsetSpikeCorrection(
                    target,
                    recordingIndex,
                    indexBefore,
                    correctedBefore,
                    beforeSpikeDeviationMeters,
                    correctedAfter,
                    afterSpikeDeviationMeters);

            double dx = beforeOffset.x + (afterOffset.x - beforeOffset.x) * t;
            double dy = beforeOffset.y + (afterOffset.y - beforeOffset.y) * t;
            double dz = beforeOffset.z + (afterOffset.z - beforeOffset.z) * t;
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);
            interpolatedRot = TrajectoryMath.SanitizeQuaternion(interpolatedRot);

            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            if (!TryResolveRecordedRelativeAnchorPose(
                    target,
                    targetUT,
                    out ParsekFlight.RelativeAnchorPose anchorPose,
                    out RelativeAnchorResolveFailure anchorFailure))
            {
                if (TryUseRelativeBodyFixedPrimaryFallback(
                        recordingIndex,
                        traj,
                        retireSignalState,
                        target,
                        targetUT,
                        ref cachedIndex,
                        out interpResult,
                        resolverFailure: anchorFailure))
                    return;

                RetireUnresolvedRecordedRelative(
                    ghost,
                    recordingIndex,
                    recordingVesselName,
                    target,
                    retireSignalState,
                    "InterpolateAndPositionRecordedRelative",
                    anchorFailure.Outcome.ToString(),
                    RelativeAnchorResolveFailure.ReasonOrFallback(anchorFailure, null));
                interpResult = InterpolationResult.Zero;
                return;
            }

            Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                anchorPose.worldPos,
                anchorPose.worldRotation,
                dx,
                dy,
                dz);

            if (double.IsNaN(ghostPos.x) || double.IsNaN(ghostPos.y) || double.IsNaN(ghostPos.z))
            {
                ParsekLog.Warn("Flight", "InterpolateAndPositionRecordedRelative: NaN in ghost position, using anchor position");
                ghostPos = anchorPose.worldPos;
            }

            ghost.transform.position = ghostPos;
            ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                anchorPose.worldRotation,
                interpolatedRot);
            GhostRenderTrace.EmitRelativeResolver(
                target.RecordingId,
                recordingIndex,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                targetUT,
                "recorded-relative-position",
                "resolved",
                0u,
                target.AnchorRecordingId,
                success: true,
                fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                anchorPosition: anchorPose.worldPos,
                anchorRotation: anchorPose.worldRotation,
                localOffset: new Vector3d(dx, dy, dz),
                outputPosition: ghostPos);
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT;
            if (GhostRenderTrace.ShouldEmitPhase(target.RecordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    target.RecordingId,
                    recordingIndex,
                    traceCurrentUT,
                    targetUT,
                    "UpdatePath",
                    "mode=RecordedRelative sectionIndex=" + target.SectionIndex.ToString(CultureInfo.InvariantCulture)
                    + " beforeUT=" + before.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " afterUT=" + after.ut.ToString("F3", CultureInfo.InvariantCulture)
                    + " t=" + t.ToString("F4", CultureInfo.InvariantCulture)
                    + " anchorRec=" + (target.AnchorRecordingId ?? "<none>")
                    + " localOffset=" + GhostRenderTrace.FormatVector3d(new Vector3d(dx, dy, dz))
                    + " anchorPos=" + GhostRenderTrace.FormatVector3d(anchorPose.worldPos)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghostPos)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }

            ParsekLog.VerboseRateLimited("Flight", "recorded-relative-offset-applied",
                BuildRecordedRelativeOffsetAppliedLog(
                    recordingFormatVersion,
                    dx,
                    dy,
                    dz,
                    target.AnchorRecordingId),
                2.0);

            string bodyName = before.bodyName ?? "Kerbin";
            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            double alt = body != null ? body.GetAltitude(ghostPos) : 0;

            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.Relative,
                recordingId = target.RecordingId,
                recordingIndex = recordingIndex,
                hasRecordingIndex = true,
                anchorVesselId = 0u,
                relDx = dx, relDy = dy, relDz = dz,
                relativeRot = interpolatedRot,
                relativeBodyName = bodyName,
                bodyBefore = body,
                latBefore = before.latitude, lonBefore = before.longitude, altBefore = before.altitude,
                relativeRecordedAnchorPose = ParsekFlight.RelativeAnchorPoseSnapshot.FromRecordedPose(anchorPose),
                anchorRecordingId = target.RecordingId,
                anchorSectionIndex = target.SectionIndex,
                relativeAnchorRecordingId = target.AnchorRecordingId,
                relativeLoopLiveAnchor = false,
                pointUT = targetUT,
            });

            interpResult = WithRelativeSectionSurfaceVelocity(
                new InterpolationResult(
                    Vector3.Lerp(before.velocity, after.velocity, t),
                    bodyName,
                    alt),
                target.Section,
                targetUT);
        }

        internal static Vector3d RecordedRelativeOffset(TrajectoryPoint point)
        {
            return new Vector3d(point.latitude, point.longitude, point.altitude);
        }

        private static void LogRecordedRelativeOffsetSpikeCorrection(
            RelativeSectionPlaybackTarget target,
            int recordingIndex,
            int indexBefore,
            bool correctedBefore,
            double beforeSpikeDeviationMeters,
            bool correctedAfter,
            double afterSpikeDeviationMeters)
        {
            string key = string.Concat(
                "recorded-relative-offset-spike-corrected|",
                target.RecordingId ?? "(none)",
                "|",
                target.SectionIndex.ToString(CultureInfo.InvariantCulture),
                "|",
                indexBefore.ToString(CultureInfo.InvariantCulture),
                "|",
                correctedBefore ? "B" : "-",
                correctedAfter ? "A" : "-");
            ParsekLog.WarnRateLimited(
                "Playback",
                key,
                $"Recorded Relative local-offset spike corrected: recording=#{recordingIndex} " +
                $"recordingId={ShortRecordingId(target.RecordingId)} " +
                $"sectionIndex={target.SectionIndex} bracketIndex={indexBefore} " +
                $"anchorRec={target.AnchorRecordingId ?? "(missing)"} " +
                $"correctedBefore={correctedBefore} beforeDeviation={beforeSpikeDeviationMeters.ToString("F2", CultureInfo.InvariantCulture)}m " +
                $"correctedAfter={correctedAfter} afterDeviation={afterSpikeDeviationMeters.ToString("F2", CultureInfo.InvariantCulture)}m",
                5.0);
        }

        private bool TryPositionGhostRecordedRelativeAt(
            GameObject ghost,
            TrajectoryPoint point,
            RelativeSectionPlaybackTarget target,
            int recordingFormatVersion,
            bool allowActivation,
            out double altitude,
            out RelativeAnchorResolveFailure failure)
        {
            altitude = 0.0;
            failure = default;
            if (!TryResolveRecordedRelativeAnchorPose(
                    target,
                    point.ut,
                    out ParsekFlight.RelativeAnchorPose anchorPose,
                    out failure))
            {
                return false;
            }

            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(point.rotation);
            double dx = point.latitude;
            double dy = point.longitude;
            double dz = point.altitude;
            string bodyName = point.bodyName ?? "Kerbin";

            Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                anchorPose.worldPos,
                anchorPose.worldRotation,
                dx,
                dy,
                dz);

            ghost.transform.position = ghostPos;
            ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                anchorPose.worldRotation,
                sanitized);
            GhostRenderTrace.EmitRelativeResolver(
                target.RecordingId,
                -1,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut,
                point.ut,
                "recorded-relative-single",
                "resolved",
                0u,
                target.AnchorRecordingId,
                success: true,
                fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                anchorPosition: anchorPose.worldPos,
                anchorRotation: anchorPose.worldRotation,
                localOffset: new Vector3d(dx, dy, dz),
                outputPosition: ghostPos);
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut;
            if (GhostRenderTrace.ShouldEmitPhase(target.RecordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    target.RecordingId,
                    -1,
                    traceCurrentUT,
                    point.ut,
                    "UpdatePath",
                    "mode=RecordedRelativeSingle sectionIndex=" + target.SectionIndex.ToString(CultureInfo.InvariantCulture)
                    + " anchorRec=" + (target.AnchorRecordingId ?? "<none>")
                    + " localOffset=" + GhostRenderTrace.FormatVector3d(new Vector3d(dx, dy, dz))
                    + " anchorPos=" + GhostRenderTrace.FormatVector3d(anchorPose.worldPos)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghostPos)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
            altitude = body != null ? body.GetAltitude(ghostPos) : 0.0;
            host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
            {
                ghost = ghost,
                mode = ParsekFlight.GhostPosMode.Relative,
                recordingId = target.RecordingId,
                anchorVesselId = 0u,
                relDx = dx, relDy = dy, relDz = dz,
                relativeRot = sanitized,
                relativeBodyName = bodyName,
                bodyBefore = body,
                latBefore = dx, lonBefore = dy, altBefore = dz,
                relativeRecordedAnchorPose = ParsekFlight.RelativeAnchorPoseSnapshot.FromRecordedPose(anchorPose),
                anchorRecordingId = target.RecordingId,
                anchorSectionIndex = target.SectionIndex,
                relativeAnchorRecordingId = target.AnchorRecordingId,
                relativeLoopLiveAnchor = false,
                pointUT = point.ut,
            });
            return true;
        }

        internal bool TryResolveRecordedRelativeAnchorPose(
            RelativeSectionPlaybackTarget target,
            double targetUT,
            out ParsekFlight.RelativeAnchorPose pose)
        {
            return TryResolveRecordedRelativeAnchorPose(
                target,
                targetUT,
                out pose,
                out _);
        }

        internal bool TryResolveRecordedRelativeAnchorPose(
            RelativeSectionPlaybackTarget target,
            double targetUT,
            out ParsekFlight.RelativeAnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            return TryResolveRecordedRelativeAnchorPose(
                target.RecordingId,
                target.AnchorRecordingId,
                target.SectionIndex,
                targetUT,
                out pose,
                out failure);
        }

        internal bool TryResolveRecordedRelativeAnchorPose(
            string recordingId,
            string anchorRecordingId,
            int sectionIndex,
            double targetUT,
            out ParsekFlight.RelativeAnchorPose pose)
        {
            return TryResolveRecordedRelativeAnchorPose(
                recordingId,
                anchorRecordingId,
                sectionIndex,
                targetUT,
                out pose,
                out _);
        }

        internal bool TryResolveRecordedRelativeAnchorPose(
            string recordingId,
            string anchorRecordingId,
            int sectionIndex,
            double targetUT,
            out ParsekFlight.RelativeAnchorPose pose,
            out RelativeAnchorResolveFailure failure)
        {
            pose = default(ParsekFlight.RelativeAnchorPose);
            failure = default;
            if (string.IsNullOrWhiteSpace(anchorRecordingId))
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.PreconditionFailed,
                    "anchor-recording-id-missing",
                    recordingId,
                    anchorRecordingId,
                    targetUT,
                    sectionIndex);
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "recorded-anchor-pose",
                    "anchor-recording-id-missing",
                    0u,
                    anchorRecordingId,
                    success: false,
                    fromRecordedTrajectory: true,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                ParsekLog.WarnRateLimited(
                    "Anchor",
                    "recorded-relative-missing-anchor-recording-id|" +
                    (recordingId ?? "(none)") + "|" +
                    sectionIndex.ToString(CultureInfo.InvariantCulture),
                    $"recorded-relative-unresolved: reason=anchor-recording-id-missing " +
                    $"recordingId={recordingId ?? "(none)"} " +
                    $"sectionIndex={sectionIndex} ut={targetUT.ToString("R", CultureInfo.InvariantCulture)}",
                    5.0);
                return false;
            }

            anchorRecordingId = anchorRecordingId.Trim();
            if (!TryFindRelativeAnchorFocusTree(recordingId, out RecordingTree focusTree))
            {
                failure = RelativeAnchorResolveFailure.Create(
                    RelativeAnchorResolveOutcome.Other,
                    "focus-tree-missing",
                    recordingId,
                    anchorRecordingId,
                    targetUT,
                    sectionIndex);
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "recorded-anchor-pose",
                    "focus-tree-missing",
                    0u,
                    anchorRecordingId,
                    success: false,
                    fromRecordedTrajectory: true,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                ParsekLog.WarnRateLimited(
                    "Anchor",
                    "recorded-relative-focus-tree-missing|" + (recordingId ?? "(none)"),
                    $"recorded-relative-unresolved: reason=focus-tree-missing " +
                    $"recordingId={recordingId ?? "(none)"} " +
                    $"anchorRecordingId={anchorRecordingId} " +
                    $"ut={targetUT.ToString("R", CultureInfo.InvariantCulture)}",
                    5.0);
                return false;
            }

            var context = ParsekFlight.BuildFlightRelativeAnchorResolverContext(
                focusTree,
                recordingId,
                ParsekScenario.Instance?.ActiveReFlySessionMarker);
            HashSet<string> visited = recordedRelativeAnchorVisited;
            visited.Clear();
            bool resolved;
            AnchorPose anchorPose;
            try
            {
                resolved = RelativeAnchorResolver.TryResolveAnchorPose(
                    context,
                    anchorRecordingId,
                    targetUT,
                    visited,
                    out anchorPose,
                    out failure);
            }
            finally
            {
                visited.Clear();
            }

            if (!resolved)
            {
                if (!failure.HasFailure)
                {
                    failure = RelativeAnchorResolveFailure.Create(
                        RelativeAnchorResolveOutcome.Other,
                        "resolver-unresolved",
                        recordingId,
                        anchorRecordingId,
                        targetUT,
                        sectionIndex);
                }
                string reason = RelativeAnchorResolveFailure.ReasonOrFallback(
                    failure,
                    "resolver-unresolved");
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    -1,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "recorded-anchor-pose",
                    reason,
                    0u,
                    anchorRecordingId,
                    success: false,
                    fromRecordedTrajectory: true,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: Vector3d.zero,
                    outputPosition: Vector3d.zero);
                return false;
            }

            pose.worldPos = anchorPose.WorldPos;
            pose.worldRotation = anchorPose.WorldRotation;
            pose.fromRecordedTrajectory = true;
            GhostRenderTrace.EmitRelativeResolver(
                recordingId,
                -1,
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                targetUT,
                "recorded-anchor-pose",
                "resolved",
                0u,
                anchorRecordingId,
                success: true,
                fromRecordedTrajectory: true,
                anchorPosition: pose.worldPos,
                anchorRotation: pose.worldRotation,
                localOffset: Vector3d.zero,
                outputPosition: pose.worldPos);
            return true;
        }

        internal static bool TryFindRelativeAnchorFocusTree(
            string recordingId,
            out RecordingTree focusTree)
        {
            focusTree = null;
            if (string.IsNullOrEmpty(recordingId))
                return false;

            List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    RecordingTree tree = committedTrees[i];
                    if (tree?.Recordings != null
                        && tree.Recordings.ContainsKey(recordingId))
                    {
                        focusTree = tree;
                        return true;
                    }
                }
            }

            RecordingTree pending = PendingTreeSlot.HasPendingTree
                ? PendingTreeSlot.PendingTree
                : null;
            if (pending?.Recordings != null && pending.Recordings.ContainsKey(recordingId))
            {
                focusTree = pending;
                return true;
            }

            return false;
        }

        private bool TryUseRelativeBodyFixedPrimaryFallback(
            int recordingIndex,
            IPlaybackTrajectory trajectory,
            GhostPlaybackState state,
            RelativeSectionPlaybackTarget target,
            double targetUT,
            ref int playbackIdx,
            out InterpolationResult interpResult,
            bool suppressFallbackWarn = false,
            RelativeAnchorResolveFailure resolverFailure = default(RelativeAnchorResolveFailure))
        {
            interpResult = InterpolationResult.Zero;
            double firstBodyFixedUT;
            double lastBodyFixedUT;
            if (state?.ghost == null
                || !ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                    target.Section,
                    targetUT,
                    out firstBodyFixedUT,
                    out lastBodyFixedUT))
            {
                return false;
            }

            int absolutePlaybackIdx = playbackIdx;
            bool ghostWasActive = state.ghost.activeSelf;
            InterpolateAndPosition(
                state.ghost,
                target.Section.bodyFixedFrames,
                null,
                ref absolutePlaybackIdx,
                targetUT,
                recordingIndex * 10000,
                out interpResult,
                allowActivation: ParsekFlight.ShouldAutoActivateGhost(state),
                skipOrbitSegments: true,
                recordingId: target.RecordingId,
                sectionIndex: target.SectionIndex);

            if (!GhostPlaybackEngine.IsInterpolationResultValid(interpResult))
            {
                if (state.ghost != null && state.ghost.activeSelf != ghostWasActive)
                    state.ghost.SetActive(ghostWasActive);
                return false;
            }

            playbackIdx = absolutePlaybackIdx;

            // PR 3c review follow-up: skip the [WARN] entirely (don't even
            // populate the dedupe set) when called by the legacy-debris gate —
            // the wrapper emits its own distinct verbose log so legacy-path
            // dispatches stay filterable. This keeps the post-failure-recovery
            // and intended-legacy-path log surfaces cleanly separate, and stops
            // the gate from inflating the WARN count the plan's success
            // criteria require to reach zero on a clean replay.
            if (!suppressFallbackWarn)
            {
                string key = string.Concat(
                    "recorded-relative-shadow-fallback|",
                    target.RecordingId ?? "(none)",
                    "|",
                    target.AnchorRecordingId ?? "(missing)",
                    "|",
                    target.SectionIndex.ToString(CultureInfo.InvariantCulture));
                if (host.loggedRelativeBodyFixedPrimaryStart.Add(key))
                {
                    ParsekLog.Warn("Playback",
                        $"RELATIVE recorded-anchor fallback to body-fixed primary: " +
                        $"recording #{recordingIndex} \"{trajectory?.VesselName}\" " +
                        $"recordingId={ShortRecordingId(target.RecordingId)} " +
                        $"anchorRec={target.AnchorRecordingId ?? "(missing)"} " +
                        $"frames={target.Section.bodyFixedFrames.Count} " +
                        $"sectionUT=[{target.Section.startUT:F1},{target.Section.endUT:F1}]");
                }
            }
            return true;
        }

        private void RetireUnresolvedRecordedRelative(
            GameObject ghost,
            int recordingIndex,
            string recordingVesselName,
            RelativeSectionPlaybackTarget target,
            GhostPlaybackState retireSignalState,
            string callsite,
            string resolverOutcome = null,
            string resolverReason = null)
        {
            if (!ReferenceEquals(ghost, null))
                GhostPlaybackLogic.HideGhostForRetire(ghost);
            if (retireSignalState != null)
                retireSignalState.anchorRetiredThisFrame = true;

            ParsekLog.WarnRateLimited(
                "Anchor",
                "recorded-relative-retired|" +
                recordingIndex.ToString(CultureInfo.InvariantCulture) + "|" +
                (target.AnchorRecordingId ?? "(missing)") + "|" +
                target.SectionIndex.ToString(CultureInfo.InvariantCulture),
                $"recorded-relative-retired: recording=#{recordingIndex} " +
                $"vessel=\"{recordingVesselName ?? "(unknown)"}\" " +
                $"anchorRec={target.AnchorRecordingId ?? "(missing)"} " +
                $"sectionIndex={target.SectionIndex} callsite={callsite}" +
                (!string.IsNullOrEmpty(resolverOutcome) ? $" outcome={resolverOutcome}" : string.Empty) +
                (!string.IsNullOrEmpty(resolverReason) ? $" reason={resolverReason}" : string.Empty),
                5.0);
        }

        void InterpolateAndPositionLoopRelative(
            GameObject ghost,
            List<TrajectoryPoint> frames,
            ref int cachedIndex,
            double targetUT,
            uint anchorVesselId,
            int recordingFormatVersion,
            int recordingIndex,
            string recordingId,
            string recordingVesselName,
            GhostPlaybackState retireSignalState,
            bool allowActivation,
            out InterpolationResult interpResult)
        {
            if (frames == null || frames.Count == 0)
            {
                interpResult = InterpolationResult.Zero;
                ghost.SetActive(false);
                return;
            }

            int indexBefore = TrajectoryMath.FindWaypointIndex(frames, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                // Before first frame — position at first frame's offset
                PositionLoopGhostRelativeAt(
                    ghost,
                    frames[0],
                    anchorVesselId,
                    recordingFormatVersion,
                    recordingIndex,
                    recordingId,
                    recordingVesselName,
                    retireSignalState,
                    allowActivation);
                interpResult = new InterpolationResult(frames[0].velocity, frames[0].bodyName, 0);
                return;
            }

            TrajectoryPoint before = frames[indexBefore];
            TrajectoryPoint after = frames[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionLoopGhostRelativeAt(
                    ghost,
                    before,
                    anchorVesselId,
                    recordingFormatVersion,
                    recordingIndex,
                    recordingId,
                    recordingVesselName,
                    retireSignalState,
                    allowActivation);
                interpResult = new InterpolationResult(before.velocity, before.bodyName, 0);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

            // Interpolate offset (dx, dy, dz stored in lat/lon/alt)
            double dx = before.latitude + (after.latitude - before.latitude) * t;
            double dy = before.longitude + (after.longitude - before.longitude) * t;
            double dz = before.altitude + (after.altitude - before.altitude) * t;

            // Interpolate rotation in the recording's stored RELATIVE frame.
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);
            interpolatedRot = TrajectoryMath.SanitizeQuaternion(interpolatedRot);

            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            string bodyName = before.bodyName ?? "Kerbin";
            ParsekFlight.RelativeAnchorPose anchorPose;

            if (TryResolveLoopLiveAnchorPose(anchorVesselId, recordingId, targetUT, out anchorPose))
            {
                Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorPose.worldPos,
                    anchorPose.worldRotation,
                    dx,
                    dy,
                    dz);

                if (double.IsNaN(ghostPos.x) || double.IsNaN(ghostPos.y) || double.IsNaN(ghostPos.z))
                {
                    ParsekLog.Warn("Flight", "InterpolateAndPositionLoopRelative: NaN in ghost position, using anchor position");
                    ghostPos = anchorPose.worldPos;
                }

                ghost.transform.position = ghostPos;
                ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                    anchorPose.worldRotation,
                    interpolatedRot);
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    recordingIndex,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-relative-position",
                    "resolved",
                    anchorVesselId,
                    null,
                    success: true,
                    fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                    anchorPosition: anchorPose.worldPos,
                    anchorRotation: anchorPose.worldRotation,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: ghostPos);
                double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT;
                if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
                {
                    GhostRenderTrace.EmitPhase(
                        recordingId,
                        recordingIndex,
                        traceCurrentUT,
                        targetUT,
                        "UpdatePath",
                        "mode=LoopRelative anchorPid=" + anchorVesselId.ToString(CultureInfo.InvariantCulture)
                        + " beforeUT=" + before.ut.ToString("F3", CultureInfo.InvariantCulture)
                        + " afterUT=" + after.ut.ToString("F3", CultureInfo.InvariantCulture)
                        + " t=" + t.ToString("F4", CultureInfo.InvariantCulture)
                        + " anchorSource=" + (anchorPose.fromRecordedTrajectory ? "recorded" : "live")
                        + " localOffset=" + GhostRenderTrace.FormatVector3d(new Vector3d(dx, dy, dz))
                        + " anchorPos=" + GhostRenderTrace.FormatVector3d(anchorPose.worldPos)
                        + " final=" + GhostRenderTrace.FormatVector3d(ghostPos)
                        + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
                }

                ParsekLog.VerboseRateLimited("Flight", "relative-offset-applied",
                    ParsekFlight.BuildRelativeOffsetAppliedLog(
                        recordingFormatVersion,
                        dx,
                        dy,
                        dz,
                        anchorVesselId,
                        anchorPose.fromRecordedTrajectory),
                    2.0);

                // Compute altitude for InterpolationResult
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                double alt = body != null ? body.GetAltitude(ghostPos) : 0;

                host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
                {
                    ghost = ghost,
                    mode = ParsekFlight.GhostPosMode.Relative,
                    recordingId = recordingId,
                    recordingIndex = recordingIndex,
                    hasRecordingIndex = true,
                    anchorVesselId = anchorVesselId,
                    relDx = dx, relDy = dy, relDz = dz,
                    relativeRot = interpolatedRot,
                    relativeBodyName = bodyName,
                    bodyBefore = body, // for fallback in LateUpdate
                    latBefore = before.latitude, lonBefore = before.longitude, altBefore = before.altitude,
                    relativeRecordedAnchorPose = ParsekFlight.RelativeAnchorPoseSnapshot.FromRecordedPose(anchorPose),
                    relativeLoopLiveAnchor = true,
                });

                interpResult = new InterpolationResult(
                    Vector3.Lerp(before.velocity, after.velocity, t),
                    bodyName, alt);
            }
            else
            {
                // No live or recorded anchor pose is available. Retire for this
                // frame so v6 metre offsets are never interpreted as surface
                // lat/lon/alt or left frozen at a stale transform.
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    recordingIndex,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : targetUT,
                    targetUT,
                    "loop-relative-position",
                    "anchor-unresolved-retired",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: Vector3d.zero);
                if (ghost.activeSelf) ghost.SetActive(false);
                if (retireSignalState != null)
                    retireSignalState.anchorRetiredThisFrame = true;
                long key = RelativeAnchorResolution.DedupeKey(recordingIndex, anchorVesselId);
                if (host.loggedAnchorNotFound.Add(key))
                    ParsekLog.Warn("Anchor",
                        RelativeAnchorResolution.FormatRetiredMessage(
                            recordingIndex,
                            recordingVesselName,
                            anchorVesselId,
                            "InterpolateAndPositionLoopRelative"));

                // Return zero result -- ghost stays hidden until the next
                // absolute section or until the anchor resolves.
                interpResult = InterpolationResult.Zero;
            }
        }

        /// <summary>
        /// Positions a ghost at a single RELATIVE frame point (no interpolation).
        /// Used for edge cases (before first frame, zero-duration segments).
        /// </summary>
        void PositionLoopGhostRelativeAt(
            GameObject ghost,
            TrajectoryPoint point,
            uint anchorVesselId,
            int recordingFormatVersion,
            int recordingIndex,
            string recordingId,
            string recordingVesselName,
            GhostPlaybackState retireSignalState,
            bool allowActivation)
        {
            if (allowActivation && !ghost.activeSelf) ghost.SetActive(true);

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(point.rotation);
            double dx = point.latitude;
            double dy = point.longitude;
            double dz = point.altitude;
            string bodyName = point.bodyName ?? "Kerbin";
            ParsekFlight.RelativeAnchorPose anchorPose;

            if (TryResolveLoopLiveAnchorPose(anchorVesselId, recordingId, point.ut, out anchorPose))
            {
                Vector3d ghostPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                    anchorPose.worldPos,
                    anchorPose.worldRotation,
                    dx,
                    dy,
                    dz);

                ghost.transform.position = ghostPos;
                ghost.transform.rotation = TrajectoryMath.ResolveRelativePlaybackRotation(
                    anchorPose.worldRotation,
                    sanitized);
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    recordingIndex,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut,
                    point.ut,
                    "loop-relative-single",
                    "resolved",
                    anchorVesselId,
                    null,
                    success: true,
                    fromRecordedTrajectory: anchorPose.fromRecordedTrajectory,
                    anchorPosition: anchorPose.worldPos,
                    anchorRotation: anchorPose.worldRotation,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: ghostPos);
                double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut;
                if (GhostRenderTrace.ShouldEmitPhase(recordingId, traceCurrentUT))
                {
                    GhostRenderTrace.EmitPhase(
                        recordingId,
                        recordingIndex,
                        traceCurrentUT,
                        point.ut,
                        "UpdatePath",
                        "mode=LoopRelativeSingle anchorPid=" + anchorVesselId.ToString(CultureInfo.InvariantCulture)
                        + " anchorSource=" + (anchorPose.fromRecordedTrajectory ? "recorded" : "live")
                        + " localOffset=" + GhostRenderTrace.FormatVector3d(new Vector3d(dx, dy, dz))
                        + " anchorPos=" + GhostRenderTrace.FormatVector3d(anchorPose.worldPos)
                        + " final=" + GhostRenderTrace.FormatVector3d(ghostPos)
                        + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
                }

                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == bodyName);
                host.AddOrReplaceGhostPosEntry(new ParsekFlight.GhostPosEntry
                {
                    ghost = ghost,
                    mode = ParsekFlight.GhostPosMode.Relative,
                    recordingId = recordingId,
                    recordingIndex = recordingIndex,
                    hasRecordingIndex = true,
                    anchorVesselId = anchorVesselId,
                    relDx = dx, relDy = dy, relDz = dz,
                    relativeRot = sanitized,
                    relativeBodyName = bodyName,
                    bodyBefore = body,
                    latBefore = dx, lonBefore = dy, altBefore = dz,
                    relativeRecordedAnchorPose = ParsekFlight.RelativeAnchorPoseSnapshot.FromRecordedPose(anchorPose),
                    relativeLoopLiveAnchor = true,
                });
            }
            else
            {
                // No live or recorded anchor pose is available. Retire for this
                // frame; see InterpolateAndPositionLoopRelative for the same gate.
                GhostRenderTrace.EmitRelativeResolver(
                    recordingId,
                    recordingIndex,
                    Planetarium.fetch != null ? Planetarium.GetUniversalTime() : point.ut,
                    point.ut,
                    "loop-relative-single",
                    "anchor-unresolved-retired",
                    anchorVesselId,
                    null,
                    success: false,
                    fromRecordedTrajectory: false,
                    anchorPosition: Vector3d.zero,
                    anchorRotation: Quaternion.identity,
                    localOffset: new Vector3d(dx, dy, dz),
                    outputPosition: Vector3d.zero);
                if (ghost.activeSelf) ghost.SetActive(false);
                if (retireSignalState != null)
                    retireSignalState.anchorRetiredThisFrame = true;
                long key = RelativeAnchorResolution.DedupeKey(recordingIndex, anchorVesselId);
                if (host.loggedAnchorNotFound.Add(key))
                    ParsekLog.Warn("Anchor",
                        RelativeAnchorResolution.FormatRetiredMessage(
                            recordingIndex,
                            recordingVesselName,
                            anchorVesselId,
                            "PositionGhostRelativeAt"));
            }
        }

        /// <summary>
        /// Flat-points dispatch for <see cref="FlatPointFrameDispatch"/>: substitutes the
        /// body-fixed shadow for a Relative-offset endpoint, or returns false when an
        /// endpoint has no body-fixed reading. Both outcomes are logged rate-limited.
        /// </summary>
        private static bool TryDispatchFlatPointEndpoints(
            List<TrackSection> sections,
            ref TrajectoryPoint before,
            ref TrajectoryPoint after,
            double targetUT,
            string recordingId,
            string pointFrameSource)
        {
            if (FlatPointFrameDispatch.TryDispatchEndpoints(
                    sections, ref before, ref after, out int substituted))
            {
                if (substituted > 0)
                {
                    ParsekLog.VerboseRateLimited("Playback",
                        "flat-point-relative-shadow|" + (recordingId ?? "(none)"),
                        $"Flat-point fallback read a Relative offset; used the body-fixed shadow: " +
                        $"rec={recordingId ?? "(none)"} source={pointFrameSource ?? "(none)"} " +
                        $"endpoints={substituted.ToString(CultureInfo.InvariantCulture)} " +
                        $"ut={targetUT.ToString("F2", CultureInfo.InvariantCulture)}",
                        5.0);
                }
                return true;
            }

            ParsekLog.InfoRateLimited("Playback",
                "flat-point-relative-refused|" + (recordingId ?? "(none)"),
                $"Flat-point fallback refused: a Relative-offset endpoint has no body-fixed shadow, ghost hidden " +
                $"rec={recordingId ?? "(none)"} source={pointFrameSource ?? "(none)"} " +
                $"ut={targetUT.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"beforeUT={before.ut.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"afterUT={after.ut.ToString("F2", CultureInfo.InvariantCulture)}",
                10.0);
            return false;
        }

        /// <summary>
        /// Tracing-only expected-attitude source for <see cref="GhostRenderTrace"/>'s
        /// <c>AfterUpdate</c> line (<c>dRotDeg=</c> / <c>rotRef=</c>). Resolves the world
        /// rotation the RECORDING implies at <paramref name="playbackUT"/>, independently of
        /// the positioner's per-ghost state (cached bracket index, orbit cache, the ghost's
        /// current transform), and through the SAME production conventions the positioner
        /// uses for the section's frame:
        /// <list type="bullet">
        /// <item>OrbitalCheckpoint / orbit segment: <see cref="ParsekFlight.ComputeOrbitalRotation"/> over a
        /// freshly built <see cref="Orbit"/>, with the radial taken from the orbit's own
        /// position at the UT (not the rendered, anchor-corrected position).</item>
        /// <item>Absolute points (flat or section frames, incl. the bridged playback frames):
        /// <c>body.bodyTransform.rotation * ShiftSurfaceRelativeRotation(slerp(srfRel))</c>.</item>
        /// <item>Body-fixed primary (parent-anchored debris): the same surface decode over
        /// <c>bodyFixedFrames</c>.</item>
        /// <item>Recorded-anchor RELATIVE: <see cref="TrajectoryMath.ResolveRelativePlaybackRotation"/>
        /// over the recorded anchor pose. Live-anchor (loop) RELATIVE is not resolved.</item>
        /// </list>
        /// Conventions are shared with the positioner BY DESIGN (the H9 frame probes guard
        /// them against live vessel attitude); what this residual measures is the data
        /// selection and everything that happens to the transform after the decode. Only
        /// called while ghost render tracing is on and a line is actually being written.
        /// </summary>
        internal bool TryResolveRecordedAttitudeForTrace(
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            GhostRenderTrace.RenderSurface surface,
            out Quaternion expected,
            out string rotRef)
        {
            expected = Quaternion.identity;
            rotRef = "unresolved";
            if (traj == null || double.IsNaN(playbackUT) || double.IsInfinity(playbackUT))
                return false;

            try
            {
                return TryResolveRecordedAttitudeForTraceCore(
                    traj, state, playbackUT, surface, out expected, out rotRef);
            }
            catch (Exception ex)
            {
                // Diagnostic-only path: never let the tracer disturb playback.
                expected = Quaternion.identity;
                rotRef = "resolver-error";
                ParsekLog.VerboseRateLimited(
                    "GhostRenderTrace",
                    "attitude-resolver-error",
                    "Attitude residual resolver threw " + ex.GetType().Name + ": " + ex.Message,
                    5.0);
                return false;
            }
        }

        private bool TryResolveRecordedAttitudeForTraceCore(
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            GhostRenderTrace.RenderSurface surface,
            out Quaternion expected,
            out string rotRef)
        {
            expected = Quaternion.identity;
            rotRef = "unresolved";
            double shiftSeconds = state != null ? state.bodyFixedShiftSeconds : 0.0;
            List<TrackSection> sections = traj.TrackSections;
            int sectionIdx = sections != null && sections.Count > 0
                ? TrajectoryMath.FindTrackSectionForUT(sections, playbackUT)
                : -1;
            bool hasSection = sectionIdx >= 0;
            TrackSection section = hasSection ? sections[sectionIdx] : default(TrackSection);

            // 1. OrbitalCheckpoint sections (the positioner's first dispatch).
            if (hasSection && ParsekFlight.HasRenderableCheckpointTrackSection(section))
            {
                if (TryFindCheckpointOrbitSegment(section, playbackUT, out OrbitSegment checkpoint, out _))
                    return TryResolveOrbitAttitudeForTrace(
                        checkpoint, playbackUT, "checkpoint-", out expected, out rotRef);
                return TryResolveSurfaceAttitudeForTrace(
                    section.frames, playbackUT, shiftSeconds, "checkpoint-points", out expected, out rotRef);
            }

            // 2. Body-fixed primary surface (parent-anchored debris).
            if (surface == GhostRenderTrace.RenderSurface.BodyFixedPrimary)
            {
                if (!hasSection || section.bodyFixedFrames == null)
                {
                    rotRef = "body-fixed-missing";
                    return false;
                }
                return TryResolveSurfaceAttitudeForTrace(
                    section.bodyFixedFrames, playbackUT, shiftSeconds, "body-fixed", out expected, out rotRef);
            }

            // 3. RELATIVE sections: recorded anchor only.
            if (hasSection && section.referenceFrame == ReferenceFrame.Relative)
                return TryResolveRelativeAttitudeForTrace(
                    traj, sectionIdx, section, playbackUT, out expected, out rotRef);

            // 4. Absolute section frames (with the same boundary bridging the positioner uses).
            if (hasSection && ParsekFlight.TryGetAbsoluteSectionPlaybackFrames(section, out List<TrajectoryPoint> sectionFrames))
            {
                if (ParsekFlight.TryGetAbsoluteSectionPlaybackFramesForPlayback(
                        sections, sectionIdx, out List<TrajectoryPoint> bridged, out _))
                    sectionFrames = bridged;
                return TryResolveSurfaceAttitudeForTrace(
                    sectionFrames, playbackUT, shiftSeconds, "surface", out expected, out rotRef);
            }

            // 5. Flat recording: orbit segments first unless the UT is a surface span,
            // then flat points (the orbit-aware InterpolateAndPosition overload's order).
            bool surfaceSkip = TrajectoryMath.IsSurfaceAtUT(sections, playbackUT);
            if (!surfaceSkip && traj.OrbitSegments != null && traj.OrbitSegments.Count > 0)
            {
                OrbitSegment? seg = ParsekFlight.FindOrbitSegment(traj.OrbitSegments, playbackUT);
                if (seg.HasValue && TrajectoryMath.HasUsableOrbitSegmentElements(seg.Value))
                    return TryResolveOrbitAttitudeForTrace(
                        seg.Value, playbackUT, "", out expected, out rotRef);
            }

            return TryResolveSurfaceAttitudeForTrace(
                traj.Points, playbackUT, shiftSeconds, "points", out expected, out rotRef);
        }

        private static bool TryResolveOrbitAttitudeForTrace(
            OrbitSegment segment,
            double ut,
            string refPrefix,
            out Quaternion expected,
            out string rotRef)
        {
            expected = Quaternion.identity;
            bool hasOfr = TrajectoryMath.HasOrbitalFrameRotation(segment);
            bool spinning = TrajectoryMath.IsSpinning(segment);
            rotRef = refPrefix + (spinning ? "orbit-spin" : hasOfr ? "orbit-ofr" : "orbit-prograde");

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == segment.bodyName);
            if (body == null)
            {
                rotRef = refPrefix + "orbit-no-body";
                return false;
            }
            Orbit orbit = MapRenderProbe.BuildOrbitFromSegment(segment, body);
            if (orbit == null)
            {
                rotRef = refPrefix + "orbit-unbuildable";
                return false;
            }

            Vector3d velocity = orbit.getOrbitalVelocityAtUT(ut);
            Vector3d worldPos = orbit.getPositionAtUT(ut);
            if (!spinning && velocity.sqrMagnitude <= 0.001)
            {
                // ComputeOrbitalRotation keeps the ghost's current rotation here: the
                // recording implies nothing to compare against.
                rotRef = refPrefix + "orbit-degenerate";
                return false;
            }

            // currentRotation is only returned on the degenerate branch excluded above,
            // and cacheKey only tags a rate-limited near-parallel log line.
            var (ghostRot, _) = ParsekFlight.ComputeOrbitalRotation(
                segment, orbit, ut, velocity, worldPos, body.position,
                Quaternion.identity, -1L, hasOfr, spinning);
            expected = ghostRot;
            return true;
        }

        private static bool TryResolveSurfaceAttitudeForTrace(
            List<TrajectoryPoint> points,
            double ut,
            double shiftSeconds,
            string refToken,
            out Quaternion expected,
            out string rotRef)
        {
            expected = Quaternion.identity;
            rotRef = refToken;
            if (points == null || points.Count == 0)
            {
                rotRef = refToken + "-empty";
                return false;
            }

            int cachedIndex = 0;
            bool hasSegment = TrajectoryMath.InterpolatePoints(
                points, ref cachedIndex, ut, out TrajectoryPoint before, out TrajectoryPoint after, out float t);
            Quaternion srfRel = hasSegment && !(t == 0f && before.ut == after.ut)
                ? TrajectoryMath.SanitizeQuaternion(Quaternion.Slerp(before.rotation, after.rotation, t))
                : TrajectoryMath.SanitizeQuaternion(before.rotation);

            CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == before.bodyName);
            if (body == null)
            {
                rotRef = refToken + "-no-body";
                return false;
            }

            expected = body.bodyTransform.rotation
                * TrajectoryMath.FrameTransform.ShiftSurfaceRelativeRotation(srfRel, shiftSeconds, body);
            return true;
        }

        private bool TryResolveRelativeAttitudeForTrace(
            IPlaybackTrajectory traj,
            int sectionIdx,
            TrackSection section,
            double ut,
            out Quaternion expected,
            out string rotRef)
        {
            expected = Quaternion.identity;
            rotRef = "relative";
            if (string.IsNullOrEmpty(section.anchorRecordingId)
                || GhostPlaybackLogic.ShouldUseLoopAnchor(traj))
            {
                rotRef = "relative-live-anchor";
                return false;
            }
            List<TrajectoryPoint> frames = section.frames != null ? section.frames : traj.Points;
            if (frames == null || frames.Count == 0)
            {
                rotRef = "relative-empty";
                return false;
            }

            int cachedIndex = 0;
            bool hasSegment = TrajectoryMath.InterpolatePoints(
                frames, ref cachedIndex, ut, out TrajectoryPoint before, out TrajectoryPoint after, out float t);
            Quaternion localRot = hasSegment
                ? TrajectoryMath.SanitizeQuaternion(Quaternion.Slerp(before.rotation, after.rotation, t))
                : TrajectoryMath.SanitizeQuaternion(before.rotation);

            if (!TryResolveRecordedRelativeAnchorPose(
                    traj.RecordingId,
                    section.anchorRecordingId,
                    sectionIdx,
                    ut,
                    out ParsekFlight.RelativeAnchorPose anchorPose,
                    out _))
            {
                rotRef = "relative-anchor-unresolved";
                return false;
            }

            expected = TrajectoryMath.ResolveRelativePlaybackRotation(anchorPose.worldRotation, localRot);
            return true;
        }

        private static Recording ResolveRecordingForTailLift(string recordingId)
        {
            Recording committed = ResolveRecordingById(recordingId);
            if (committed != null) return committed;

            Recording preview = ParsekFlight.s_previewRecordingForTailLift;
            if (preview != null
                && !string.IsNullOrEmpty(preview.RecordingId)
                && string.Equals(preview.RecordingId, recordingId, StringComparison.Ordinal))
            {
                return preview;
            }
            return null;
        }

        internal static TerrainCorrector.TailLiftPlan ResolveTailLiftPlan(
            string recordingId, CelestialBody body)
        {
            if (string.IsNullOrEmpty(recordingId))
                return TerrainCorrector.TailLiftPlan.Inactive;

            TerrainCorrector.TailLiftPlan cached;
            string cachedTerminalBodyName;
            if (ParsekFlight.s_tailLiftPlanCache.TryGetValue(recordingId, out cached))
            {
                if (!ParsekFlight.s_tailLiftPlanTerminalBodyCache.TryGetValue(recordingId, out cachedTerminalBodyName)
                    || TailLiftBodyMatches(body, cachedTerminalBodyName))
                {
                    return cached;
                }

                return TerrainCorrector.TailLiftPlan.Inactive;
            }

            Recording rec = ResolveRecordingForTailLift(recordingId);
            if (rec == null || rec.Points == null || rec.Points.Count == 0)
            {
                string reason = ResolveTailLiftInactiveReason(rec, body, double.NaN);
                CacheInactiveTailLiftPlanIfStable(recordingId, null, reason);
                LogTailLiftInactive(recordingId, rec, reason,
                    double.NaN, double.NaN);
                return TerrainCorrector.TailLiftPlan.Inactive;
            }

            TrajectoryPoint lastPt = rec.Points[rec.Points.Count - 1];
            string terminalBodyName = ResolveTailLiftTerminalBodyName(rec, lastPt);
            if (object.ReferenceEquals(body, null) || string.IsNullOrEmpty(terminalBodyName))
            {
                string reason = object.ReferenceEquals(body, null) ? "body-missing" : "terminal-body-missing";
                CacheInactiveTailLiftPlanIfStable(recordingId, terminalBodyName, reason);
                LogTailLiftInactive(recordingId, rec, reason, double.NaN, double.NaN);
                return TerrainCorrector.TailLiftPlan.Inactive;
            }

            if (!TailLiftBodyMatches(body, terminalBodyName))
            {
                LogTailLiftInactive(recordingId, rec, "current-body-not-terminal",
                    double.NaN, double.NaN);
                return TerrainCorrector.TailLiftPlan.Inactive;
            }

            if (!TailLiftTerminalPointIsAbsolute(rec, lastPt))
            {
                const string reason = "terminal-non-absolute-frame";
                CacheInactiveTailLiftPlanIfStable(recordingId, terminalBodyName, reason);
                LogTailLiftInactive(recordingId, rec, reason, double.NaN, double.NaN);
                return TerrainCorrector.TailLiftPlan.Inactive;
            }

            CelestialBody terminalBody = ResolveTailLiftTerminalBody(terminalBodyName, body);
            if (object.ReferenceEquals(terminalBody, null))
            {
                const string reason = "terminal-body-missing";
                CacheInactiveTailLiftPlanIfStable(recordingId, terminalBodyName, reason);
                LogTailLiftInactive(recordingId, rec, reason, double.NaN, double.NaN);
                return TerrainCorrector.TailLiftPlan.Inactive;
            }

            double currentTerrain = Parsek.Rendering.TerrainCacheBuckets.GetCachedSurfaceHeight(
                terminalBody, lastPt.latitude, lastPt.longitude);
            TerrainCorrector.TailLiftPlan plan = TerrainCorrector.BuildTailLiftPlan(
                rec.TerminalStateValue,
                rec.TerrainHeightAtEnd,
                currentTerrain,
                lastPt.ut,
                TerrainCorrector.DefaultTailLiftRampSeconds,
                TerrainCorrector.TailLiftMinAbsDeltaMeters);

            if (plan.Active)
            {
                ParsekFlight.s_tailLiftPlanCache[recordingId] = plan;
                ParsekFlight.s_tailLiftPlanTerminalBodyCache[recordingId] = terminalBodyName;
                LogTailLiftActive(recordingId, rec, plan, currentTerrain);
            }
            else
            {
                string reason = ResolveTailLiftInactiveReason(rec, terminalBody, currentTerrain);
                CacheInactiveTailLiftPlanIfStable(recordingId, terminalBodyName, reason);
                LogTailLiftInactive(recordingId, rec, reason,
                    currentTerrain,
                    currentTerrain - rec.TerrainHeightAtEnd);
            }
            return plan;
        }

        private static void CacheInactiveTailLiftPlanIfStable(
            string recordingId, string terminalBodyName, string reason)
        {
            if (string.IsNullOrEmpty(recordingId) || IsTransientTailLiftInactiveReason(reason))
                return;

            ParsekFlight.s_tailLiftPlanCache[recordingId] = TerrainCorrector.TailLiftPlan.Inactive;
            if (!string.IsNullOrEmpty(terminalBodyName))
                ParsekFlight.s_tailLiftPlanTerminalBodyCache[recordingId] = terminalBodyName;
        }

        internal static bool IsTransientTailLiftInactiveReason(string reason)
        {
            return string.Equals(reason, "recording-missing", StringComparison.Ordinal)
                || string.Equals(reason, "body-missing", StringComparison.Ordinal)
                || string.Equals(reason, "terminal-body-missing", StringComparison.Ordinal)
                || string.Equals(reason, "current-body-not-terminal", StringComparison.Ordinal)
                || string.Equals(reason, "current-terrain-nan", StringComparison.Ordinal);
        }

        internal static string ResolveTailLiftTerminalBodyName(Recording rec, TrajectoryPoint lastPt)
        {
            string terminalBodyName;
            if (RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out terminalBodyName)
                && !string.IsNullOrEmpty(terminalBodyName))
            {
                return terminalBodyName;
            }

            return !string.IsNullOrEmpty(lastPt.bodyName) ? lastPt.bodyName : null;
        }

        private static CelestialBody ResolveTailLiftTerminalBody(
            string terminalBodyName, CelestialBody fallbackBody)
        {
            if (!string.IsNullOrEmpty(terminalBodyName)
                && TailLiftBodyMatches(fallbackBody, terminalBodyName))
            {
                return fallbackBody;
            }

            if (!string.IsNullOrEmpty(terminalBodyName) && FlightGlobals.Bodies != null)
            {
                CelestialBody resolved = FlightGlobals.Bodies.Find(b =>
                    !object.ReferenceEquals(b, null)
                    && (string.Equals(b.bodyName, terminalBodyName, StringComparison.Ordinal)
                        || string.Equals(b.name, terminalBodyName, StringComparison.Ordinal)));
                if (!object.ReferenceEquals(resolved, null))
                    return resolved;
            }

            return null;
        }

        private static bool TailLiftBodyMatches(CelestialBody body, string bodyName)
        {
            return !object.ReferenceEquals(body, null)
                && !string.IsNullOrEmpty(bodyName)
                && string.Equals(body.bodyName, bodyName, StringComparison.Ordinal);
        }

        internal static bool TailLiftTerminalPointIsAbsolute(Recording rec, TrajectoryPoint lastPt)
        {
            if (rec == null || rec.TrackSections == null || rec.TrackSections.Count == 0)
                return true;

            int sectionIndex = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, lastPt.ut);
            if (sectionIndex < 0)
                return true;

            return rec.TrackSections[sectionIndex].referenceFrame == ReferenceFrame.Absolute;
        }

        private static string ResolveTailLiftInactiveReason(
            Recording rec, CelestialBody body, double currentTerrain)
        {
            return ResolveTailLiftInactiveReasonCore(
                rec, !object.ReferenceEquals(body, null), currentTerrain);
        }

        /// <summary>
        /// Names why a recording's tail lift is inactive, checked in order: the recording, its
        /// points, the body, the terminal state (only a surface terminal lifts), the recorded and
        /// current terrain heights, then the minimum delta.
        /// </summary>
        internal static string ResolveTailLiftInactiveReasonCore(
            Recording rec, bool hasBody, double currentTerrain)
        {
            if (rec == null)
                return "recording-missing";
            if (rec.Points == null || rec.Points.Count == 0)
                return "no-points";
            if (!hasBody)
                return "body-missing";
            if (!rec.TerminalStateValue.HasValue)
                return "terminal-missing";

            TerminalState terminal = rec.TerminalStateValue.Value;
            if (terminal != TerminalState.Landed
                && terminal != TerminalState.Splashed
                && terminal != TerminalState.Recovered)
                return "non-surface-terminal";
            if (double.IsNaN(rec.TerrainHeightAtEnd))
                return "recorded-terrain-nan";
            if (double.IsNaN(currentTerrain))
                return "current-terrain-nan";

            double delta = currentTerrain - rec.TerrainHeightAtEnd;
            if (System.Math.Abs(delta) < TerrainCorrector.TailLiftMinAbsDeltaMeters)
                return "delta-below-threshold";
            return "inactive";
        }

        private static void LogTailLiftActive(
            string recordingId, Recording rec,
            TerrainCorrector.TailLiftPlan plan, double currentTerrain)
        {
            string vesselName = rec != null && !string.IsNullOrEmpty(rec.VesselName)
                ? rec.VesselName
                : "?";
            double rampSeconds = plan.TerminalUT - plan.RampStartUT;
            ParsekLog.Verbose("Pipeline-Terrain",
                string.Format(CultureInfo.InvariantCulture,
                    "TailLift active: rec={0} vessel='{1}' delta={2}m terminalUT={3:R} " +
                    "rampSec={4:F1} (recTerrain={5:F1} curTerrain={6:F1})",
                    recordingId,
                    vesselName,
                    plan.TerrainDelta.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture),
                    plan.TerminalUT,
                    rampSeconds,
                    rec != null ? rec.TerrainHeightAtEnd : double.NaN,
                    currentTerrain));
        }

        private static void LogTailLiftInactive(
            string recordingId, Recording rec, string reason,
            double currentTerrain, double delta)
        {
            string vesselName = rec != null && !string.IsNullOrEmpty(rec.VesselName)
                ? rec.VesselName
                : "?";
            string key = "tail-lift-inactive-" + recordingId + "-" + reason;
            ParsekLog.VerboseRateLimited("Pipeline-Terrain",
                key,
                string.Format(CultureInfo.InvariantCulture,
                    "TailLift inactive: rec={0} vessel='{1}' reason={2} delta={3}m " +
                    "(recTerrain={4:F1} curTerrain={5:F1})",
                    recordingId,
                    vesselName,
                    reason,
                    delta.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture),
                    rec != null ? rec.TerrainHeightAtEnd : double.NaN,
                    currentTerrain),
                30.0);
        }
    }
}
