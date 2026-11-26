using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    // Non-loop Relative section placement.
    internal sealed partial class ParsekFlightGhostPositioner
    {
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

        internal static string ShortRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return "-";
            return recordingId.Length <= 8 ? recordingId : recordingId.Substring(0, 8);
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
                        targetUT,
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
                        targetUT,
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
            CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, bodyName);
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

        /// <summary>
        /// Positions a ghost at one RELATIVE sample, holding its anchor-local offset beside the
        /// pose the anchor has at <paramref name="playbackUT"/>. The sample covers playback UTs
        /// other than its own (a one-sample section, two samples at one UT, a UT before the first
        /// frame), and the anchor keeps moving there: resolving it at the UT of the sample leaves
        /// the ghost behind at the speed of the anchor. An anchor with no pose at
        /// <paramref name="playbackUT"/> fails, and the caller hides the ghost.
        /// </summary>
        private bool TryPositionGhostRecordedRelativeAt(
            GameObject ghost,
            TrajectoryPoint point,
            double playbackUT,
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
                    playbackUT,
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
                Planetarium.fetch != null ? Planetarium.GetUniversalTime() : playbackUT,
                playbackUT,
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
            double traceCurrentUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : playbackUT;
            if (GhostRenderTrace.ShouldEmitPhase(target.RecordingId, traceCurrentUT))
            {
                GhostRenderTrace.EmitPhase(
                    target.RecordingId,
                    -1,
                    traceCurrentUT,
                    playbackUT,
                    "UpdatePath",
                    "mode=RecordedRelativeSingle sectionIndex=" + target.SectionIndex.ToString(CultureInfo.InvariantCulture)
                    + " anchorRec=" + (target.AnchorRecordingId ?? "<none>")
                    + " localOffset=" + GhostRenderTrace.FormatVector3d(new Vector3d(dx, dy, dz))
                    + " anchorPos=" + GhostRenderTrace.FormatVector3d(anchorPose.worldPos)
                    + " final=" + GhostRenderTrace.FormatVector3d(ghostPos)
                    + " rot=" + GhostRenderTrace.FormatQuaternion(ghost.transform.rotation));
            }

            double heldSeconds = playbackUT - point.ut;
            if (ParsekLog.IsVerboseEnabled && IsHeldRelativeSample(heldSeconds))
                ParsekLog.VerboseRateLimited("Playback",
                    "recorded-relative-held|" + (target.RecordingId ?? "(none)"),
                    FormatHeldRelativeSample(target, playbackUT, heldSeconds, dx, dy, dz),
                    5.0);

            CelestialBody body = FindBodyByNameOrNull(FlightGlobals.Bodies, bodyName);
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
                pointUT = playbackUT,
            });
            return true;
        }

        // More than one physics step, so an ordinary single-sample frame is not reported.
        private const double HeldRelativeSampleLogSeconds = 0.05;

        /// <summary>True when a RELATIVE sample is drawn at a playback UT other than its own.</summary>
        internal static bool IsHeldRelativeSample(double heldSeconds)
        {
            return !double.IsNaN(heldSeconds) && Math.Abs(heldSeconds) > HeldRelativeSampleLogSeconds;
        }

        internal static string FormatHeldRelativeSample(
            RelativeSectionPlaybackTarget target,
            double playbackUT,
            double heldSeconds,
            double dx,
            double dy,
            double dz)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "RELATIVE sample held beside its anchor: recordingId={0} sectionIndex={1} " +
                "anchorRec={2} playbackUT={3:F2} heldSeconds={4:F2} offset=({5:F2},{6:F2},{7:F2})",
                ShortRecordingId(target.RecordingId),
                target.SectionIndex,
                ShortRecordingId(target.AnchorRecordingId),
                playbackUT,
                heldSeconds,
                dx, dy, dz);
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
    }
}
