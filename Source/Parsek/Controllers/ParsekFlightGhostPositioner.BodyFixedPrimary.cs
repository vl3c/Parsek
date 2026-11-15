using System.Collections.Generic;

namespace Parsek
{
    // Body-fixed primary placement for parent-anchored recordings.
    internal sealed partial class ParsekFlightGhostPositioner
    {
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
    }
}
