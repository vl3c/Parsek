using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    public partial class ParsekKSC
    {
        internal struct KscAnchorFrame
        {
            internal KscAnchorFrame(Vector3d worldPos, Quaternion worldRot)
            {
                WorldPos = worldPos;
                WorldRot = worldRot;
            }

            internal Vector3d WorldPos;
            internal Quaternion WorldRot;
        }

        internal struct KscPoseResolution
        {
            internal bool Resolved;
            internal Vector3d WorldPos;
            internal Quaternion WorldRot;
            internal string Branch;
            internal string FailureReason;
            internal string AnchorRecordingId;

            internal static KscPoseResolution Success(
                Vector3d worldPos,
                Quaternion worldRot,
                string branch,
                string anchorRecordingId)
            {
                return new KscPoseResolution
                {
                    Resolved = true,
                    WorldPos = worldPos,
                    WorldRot = worldRot,
                    Branch = branch,
                    FailureReason = null,
                    AnchorRecordingId = anchorRecordingId
                };
            }

            internal static KscPoseResolution Failure(
                string branch,
                string failureReason,
                string anchorRecordingId)
            {
                return new KscPoseResolution
                {
                    Resolved = false,
                    WorldPos = Vector3d.zero,
                    WorldRot = Quaternion.identity,
                    Branch = branch,
                    FailureReason = failureReason,
                    AnchorRecordingId = anchorRecordingId
                };
            }
        }

        internal delegate bool KscSurfaceLookup(
            string bodyName,
            double latitude,
            double longitude,
            double altitude,
            out Vector3d worldPos,
            out Quaternion bodyWorldRot);

        internal delegate bool KscRecordedAnchorLookup(
            Recording rec,
            TrackSection section,
            int sectionIndex,
            double targetUT,
            out KscAnchorFrame anchorFrame);

        internal const int KscFlatPointFrameSourceKey = 0;

        /// <summary>
        /// Interpolates the KSC playback pose and dispatches the point payload
        /// through the originating TrackSection reference frame.
        /// </summary>
        internal static bool TryInterpolateKscPlaybackPose(
            Recording rec,
            ref int cachedIndex,
            ref int cachedFrameSourceKey,
            double targetUT,
            KscSurfaceLookup surfaceLookup,
            KscRecordedAnchorLookup anchorLookup,
            out KscPoseResolution pose)
        {
            pose = KscPoseResolution.Failure("none", "recording-null", null);
            if (rec == null)
            {
                ParsekLog.Verbose("KSCGhost",
                    $"KSC pose interpolation skipped: recording=null targetUT={targetUT:F2}");
                return false;
            }

            int targetSectionIndex = FindKscTrackSectionIndex(rec, targetUT);
            TrackSection? targetSection = GetKscTrackSection(rec, targetSectionIndex);
            bool usingSectionFrames;
            string frameSelectionFailureReason;
            List<TrajectoryPoint> frames = SelectKscInterpolationFrames(
                rec,
                targetSection,
                targetUT,
                out usingSectionFrames,
                out frameSelectionFailureReason);
            if (frames == null || frames.Count == 0)
            {
                string branch = targetSection.HasValue
                    ? ShouldUseKscBodyFixedPrimary(rec, targetSection.Value, targetUT)
                        ? "body-fixed-primary"
                        : targetSection.Value.referenceFrame.ToString()
                    : "no-section";
                pose = KscPoseResolution.Failure(
                    branch,
                    frameSelectionFailureReason ?? "no-points",
                    targetSection.HasValue ? NormalizeKscAnchorRecordingId(targetSection.Value) : null);
                // Rate-limit per recording: synthetic recordings with no
                // sampled points trigger this branch every KSC ghost frame
                // and the unrate-limited Verbose used to emit ~120 lines/sec
                // per offending recording.
                ParsekLog.VerboseRateLimited("KSCGhost",
                    $"ksc-no-points-{rec.RecordingId}",
                    $"KSC pose interpolation skipped: no points recording={rec.DebugName} " +
                    $"targetUT={targetUT:F2} sections={rec.TrackSections?.Count ?? 0}");
                return false;
            }

            int frameSourceKey = usingSectionFrames
                ? targetSectionIndex + 1
                : KscFlatPointFrameSourceKey;
            if (cachedFrameSourceKey != frameSourceKey)
            {
                cachedIndex = 0;
                cachedFrameSourceKey = frameSourceKey;
            }

            int interpolationIndex = cachedIndex;
            TrajectoryPoint before, after;
            float t;
            bool hasSegment = TrajectoryMath.InterpolatePoints(
                frames, ref interpolationIndex, targetUT, out before, out after, out t);
            cachedIndex = interpolationIndex;

            if (!hasSegment)
            {
                if (frames.Count == 0)
                {
                    pose = KscPoseResolution.Failure("none", "no-points", null);
                    return false;
                }

                TrackSection? pointSection = targetSection ?? FindKscTrackSection(rec, before.ut);
                return TryResolveKscPointPose(
                    rec,
                    before,
                    pointSection,
                    surfaceLookup,
                    anchorLookup,
                    out pose);
            }

            if (t == 0f && before.ut == after.ut)
            {
                TrackSection? pointSection = targetSection ?? FindKscTrackSection(rec, before.ut);
                return TryResolveKscPointPose(
                    rec,
                    before,
                    pointSection,
                    surfaceLookup,
                    anchorLookup,
                    out pose);
            }

            TrackSection? section = targetSection ?? FindKscTrackSection(rec, before.ut);
            return TryResolveKscSegmentPose(
                rec,
                before,
                after,
                t,
                section,
                surfaceLookup,
                anchorLookup,
                out pose);
        }

        private static List<TrajectoryPoint> SelectKscInterpolationFrames(
            Recording rec,
            TrackSection? targetSection,
            double targetUT,
            out bool usingSectionFrames,
            out string failureReason)
        {
            usingSectionFrames = false;
            failureReason = null;
            if (targetSection.HasValue
                && ShouldUseKscBodyFixedPrimary(rec, targetSection.Value, targetUT))
            {
                if (!ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                        targetSection.Value,
                        targetUT,
                        out _,
                        out _))
                {
                    if (DebrisRelativePlaybackPolicy.ShouldRetireOutsideAuthoredRelativeCoverage(
                            rec,
                            targetUT,
                            out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic)
                        && !string.IsNullOrWhiteSpace(diagnostic.Reason))
                    {
                        failureReason = diagnostic.Reason;
                    }
                    else
                    {
                        failureReason = "body-fixed-primary-unavailable";
                    }
                    return null;
                }

                usingSectionFrames = true;
                return targetSection.Value.bodyFixedFrames;
            }

            if (targetSection.HasValue
                && targetSection.Value.frames != null
                && targetSection.Value.frames.Count > 0)
            {
                usingSectionFrames = true;
                return targetSection.Value.frames;
            }

            return rec?.Points;
        }

        private static bool ShouldUseKscBodyFixedPrimary(
            Recording rec,
            TrackSection section,
            double targetUT)
        {
            return section.referenceFrame == ReferenceFrame.Relative
                && DebrisRelativePlaybackPolicy.ShouldRetireOnRecordedParentAnchorMiss(rec)
                && !GhostPlaybackEngine.ShouldUseLoopAnchoredDebrisChain(rec, targetUT);
        }

        private static TrackSection? FindKscTrackSection(Recording rec, double targetUT)
        {
            return GetKscTrackSection(rec, FindKscTrackSectionIndex(rec, targetUT));
        }

        private static TrackSection? GetKscTrackSection(Recording rec, int sectionIdx)
        {
            if (rec?.TrackSections == null || sectionIdx < 0 || sectionIdx >= rec.TrackSections.Count)
                return null;

            return rec.TrackSections[sectionIdx];
        }

        private static int FindKscTrackSectionIndex(Recording rec, double targetUT)
        {
            if (rec?.TrackSections == null || rec.TrackSections.Count == 0)
                return -1;

            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, targetUT);
            if (sectionIdx < 0 || sectionIdx >= rec.TrackSections.Count)
                return -1;

            return sectionIdx;
        }

        private static bool TryResolveKscPointPose(
            Recording rec,
            TrajectoryPoint point,
            TrackSection? section,
            KscSurfaceLookup surfaceLookup,
            KscRecordedAnchorLookup anchorLookup,
            out KscPoseResolution pose)
        {
            ReferenceFrame frame = section.HasValue
                ? section.Value.referenceFrame
                : ReferenceFrame.Absolute;
            bool useBodyFixedPrimary = section.HasValue
                && ShouldUseKscBodyFixedPrimary(rec, section.Value, point.ut);
            if (point.bodyName != "Kerbin")
            {
                pose = KscPoseResolution.Failure(
                    DescribeKscBranch(section, useBodyFixedPrimary),
                    "non-kerbin",
                    section.HasValue ? NormalizeKscAnchorRecordingId(section.Value) : null);
                ParsekLog.VerboseRateLimited("KSCGhost", $"ksc-point-non-kerbin-{rec.RecordingId}",
                    $"KSC point skipped: recording={rec.DebugName} body={point.bodyName ?? "null"} ut={point.ut:F2}");
                return false;
            }

            if (frame == ReferenceFrame.Relative && !useBodyFixedPrimary)
            {
                Quaternion storedRot = TrajectoryMath.SanitizeQuaternion(point.rotation);
                return TryResolveKscRelativePose(
                    rec,
                    point.latitude,
                    point.longitude,
                    point.altitude,
                    storedRot,
                    section.Value,
                    FindKscTrackSectionIndex(rec, point.ut),
                    point.ut,
                    anchorLookup,
                    out pose);
            }

            Vector3d worldPos;
            Quaternion bodyWorldRot;
            if (!surfaceLookup(
                    point.bodyName,
                    point.latitude,
                    point.longitude,
                    point.altitude,
                    out worldPos,
                    out bodyWorldRot))
            {
                pose = KscPoseResolution.Failure("absolute", "body-not-found", null);
                ParsekLog.VerboseRateLimited("KSCGhost", $"interp-no-body-{rec.RecordingId}",
                    $"Body not found: recording={rec.DebugName} {point.bodyName ?? "null"}");
                return false;
            }

            Quaternion worldRot = TrajectoryMath.PureMultiply(
                bodyWorldRot,
                TrajectoryMath.SanitizeQuaternion(point.rotation));
            pose = KscPoseResolution.Success(
                worldPos,
                worldRot,
                DescribeKscBranch(section, useBodyFixedPrimary),
                null);
            ParsekLog.VerboseRateLimited("KSCGhost", $"ksc-surface-position-{rec.RecordingId}",
                $"KSC SURFACE playback resolved: recording={rec.DebugName} " +
                $"ut={point.ut:F2} body={point.bodyName} branch={pose.Branch}",
                2.0);
            return true;
        }

        private static bool TryResolveKscSegmentPose(
            Recording rec,
            TrajectoryPoint before,
            TrajectoryPoint after,
            float t,
            TrackSection? section,
            KscSurfaceLookup surfaceLookup,
            KscRecordedAnchorLookup anchorLookup,
            out KscPoseResolution pose)
        {
            double targetUT = before.ut + (after.ut - before.ut) * t;
            ReferenceFrame frame = section.HasValue
                ? section.Value.referenceFrame
                : ReferenceFrame.Absolute;
            bool useBodyFixedPrimary = section.HasValue
                && ShouldUseKscBodyFixedPrimary(rec, section.Value, targetUT);
            if (before.bodyName != "Kerbin" || after.bodyName != "Kerbin")
            {
                pose = KscPoseResolution.Failure(
                    DescribeKscBranch(section, useBodyFixedPrimary),
                    "non-kerbin",
                    section.HasValue ? NormalizeKscAnchorRecordingId(section.Value) : null);
                ParsekLog.VerboseRateLimited("KSCGhost", "ksc-segment-non-kerbin",
                    $"KSC segment skipped: beforeBody={before.bodyName ?? "null"} " +
                    $"afterBody={after.bodyName ?? "null"} targetUT={targetUT:F2}");
                return false;
            }

            if (frame == ReferenceFrame.Relative && !useBodyFixedPrimary)
            {
                double dx = before.latitude + (after.latitude - before.latitude) * t;
                double dy = before.longitude + (after.longitude - before.longitude) * t;
                double dz = before.altitude + (after.altitude - before.altitude) * t;
                Quaternion storedRot = TrajectoryMath.PureSlerp(before.rotation, after.rotation, t);
                return TryResolveKscRelativePose(
                    rec,
                    dx,
                    dy,
                    dz,
                    storedRot,
                    section.Value,
                    FindKscTrackSectionIndex(rec, targetUT),
                    targetUT,
                    anchorLookup,
                    out pose);
            }

            Vector3d posBefore;
            Vector3d posAfter;
            Quaternion bodyRotBefore;
            Quaternion bodyRotAfter;
            if (!surfaceLookup(
                    before.bodyName,
                    before.latitude,
                    before.longitude,
                    before.altitude,
                    out posBefore,
                    out bodyRotBefore)
                || !surfaceLookup(
                    after.bodyName,
                    after.latitude,
                    after.longitude,
                    after.altitude,
                    out posAfter,
                    out bodyRotAfter))
            {
                pose = KscPoseResolution.Failure("absolute", "body-not-found", null);
                ParsekLog.VerboseRateLimited("KSCGhost", $"interp-no-body-{rec.RecordingId}",
                    $"Body not found: recording={rec.DebugName} before={before.bodyName ?? "null"} after={after.bodyName ?? "null"}");
                return false;
            }

            Vector3d interpolatedPos = new Vector3d(
                posBefore.x + (posAfter.x - posBefore.x) * t,
                posBefore.y + (posAfter.y - posBefore.y) * t,
                posBefore.z + (posAfter.z - posBefore.z) * t);
            if (double.IsNaN(interpolatedPos.x) || double.IsNaN(interpolatedPos.y) ||
                double.IsNaN(interpolatedPos.z))
            {
                ParsekLog.VerboseRateLimited("KSCGhost", "ksc-pos-nan-fallback",
                    $"KSC interpolation produced NaN; using before point at ut={before.ut:F2}");
                interpolatedPos = posBefore;
            }

            Quaternion interpolatedRot = TrajectoryMath.PureSlerp(before.rotation, after.rotation, t);
            Quaternion worldRot = TrajectoryMath.PureMultiply(bodyRotBefore, interpolatedRot);
            pose = KscPoseResolution.Success(
                interpolatedPos,
                worldRot,
                DescribeKscBranch(section, useBodyFixedPrimary),
                null);
            ParsekLog.VerboseRateLimited("KSCGhost", $"ksc-surface-position-{rec.RecordingId}",
                $"KSC SURFACE playback resolved: recording={rec.DebugName} " +
                $"targetUT={targetUT:F2} branch={pose.Branch}",
                2.0);
            return true;
        }

        private static bool TryResolveKscRelativePose(
            Recording rec,
            double dx,
            double dy,
            double dz,
            Quaternion storedRot,
            TrackSection section,
            int sectionIndex,
            double targetUT,
            KscRecordedAnchorLookup anchorLookup,
            out KscPoseResolution pose)
        {
            string anchorRecordingId = NormalizeKscAnchorRecordingId(section);
            if (string.IsNullOrEmpty(anchorRecordingId) || anchorLookup == null)
            {
                pose = KscPoseResolution.Failure(
                    "relative",
                    "relative-anchor-unresolved",
                    anchorRecordingId);
                ParsekLog.VerboseRateLimited("KSCGhost", $"ksc-relative-anchor-unresolved-{rec.RecordingId}",
                    $"RELATIVE KSC playback skipped: recording={rec.DebugName} " +
                    $"anchorRec={anchorRecordingId ?? "(missing)"} reason=no-recorded-anchor-lookup");
                return false;
            }

            KscAnchorFrame anchor;
            if (!anchorLookup(rec, section, sectionIndex, targetUT, out anchor))
            {
                pose = KscPoseResolution.Failure(
                    "relative",
                    "relative-anchor-unresolved",
                    anchorRecordingId);
                ParsekLog.VerboseRateLimited("KSCGhost", $"ksc-relative-anchor-unresolved-{rec.RecordingId}",
                    $"RELATIVE KSC playback skipped: recording={rec.DebugName} " +
                    $"anchorRec={anchorRecordingId} reason=anchor-recording-unresolved");
                return false;
            }

            Vector3d worldPos = TrajectoryMath.ResolveRelativePlaybackPosition(
                anchor.WorldPos,
                anchor.WorldRot,
                dx,
                dy,
                dz);
            if (double.IsNaN(worldPos.x) || double.IsNaN(worldPos.y) || double.IsNaN(worldPos.z))
            {
                ParsekLog.Warn("KSCGhost",
                    $"RELATIVE KSC playback produced NaN position; using anchor position " +
                    $"recording={rec.DebugName} anchorRec={anchorRecordingId}");
                worldPos = anchor.WorldPos;
            }

            Quaternion worldRot = TrajectoryMath.ResolveRelativePlaybackRotation(
                anchor.WorldRot,
                storedRot);
            pose = KscPoseResolution.Success(worldPos, worldRot, "relative", anchorRecordingId);
            ParsekLog.VerboseRateLimited("KSCGhost", $"ksc-relative-position-{rec.RecordingId}",
                $"RELATIVE KSC playback resolved: recording={rec.DebugName} " +
                $"contract=anchor-local " +
                $"version={rec.RecordingFormatVersion} dx={dx:F2} dy={dy:F2} dz={dz:F2} " +
                $"anchorRec={anchorRecordingId} |offset|={Math.Sqrt(dx * dx + dy * dy + dz * dz):F2}m",
                2.0);
            return true;
        }

        private static string NormalizeKscAnchorRecordingId(TrackSection section)
        {
            return string.IsNullOrWhiteSpace(section.anchorRecordingId)
                ? null
                : section.anchorRecordingId.Trim();
        }

        private static string DescribeKscBranch(
            TrackSection? section,
            bool useBodyFixedPrimary = false)
        {
            if (useBodyFixedPrimary)
                return "body-fixed-primary";
            if (!section.HasValue)
                return "no-section";
            return section.Value.referenceFrame == ReferenceFrame.Absolute
                ? "absolute"
                : section.Value.referenceFrame == ReferenceFrame.Relative
                    ? "relative"
                    : "orbital-checkpoint";
        }

        private static bool TryResolveRecordedKscAnchorFrame(
            Recording rec,
            TrackSection section,
            int sectionIndex,
            double targetUT,
            out KscAnchorFrame anchorFrame)
        {
            anchorFrame = default(KscAnchorFrame);
            if (!RecordedRelativeAnchorPoseResolver.TryResolveSectionAnchorPose(
                    rec,
                    section,
                    targetUT,
                    out AnchorPose pose))
            {
                return false;
            }

            anchorFrame = new KscAnchorFrame(
                pose.WorldPos,
                pose.WorldRotation);
            return true;
        }

        /// <summary>
        /// Compute loop playback UT for a recording (positive/zero interval path only).
        /// Negative intervals use UpdateOverlapKsc with GetActiveCycles instead.
        /// Reimplemented from ParsekFlight.TryComputeLoopPlaybackUT (instance version)
        /// because the static 6-param overload doesn't return pause-window state.
        /// </summary>
        private static bool TryGetLoopSchedule(
            Recording rec,
            int recIdx,
            IReadOnlyDictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule> autoLoopScheduleCache,
            out double playbackStartUT,
            out double scheduleStartUT,
            out double duration,
            out double intervalSeconds)
        {
            playbackStartUT = 0.0;
            scheduleStartUT = 0.0;
            duration = 0.0;
            intervalSeconds = 0.0;
            if (rec == null || !GhostPlaybackEngine.ShouldLoopPlayback(rec))
                return false;

            playbackStartUT = GhostPlaybackEngine.EffectiveLoopStartUT(rec);
            duration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? LoopTiming.DefaultLoopIntervalSeconds;
            double baseIntervalSeconds = GhostPlaybackLogic.ResolveLoopInterval(
                rec, globalInterval, LoopTiming.DefaultLoopIntervalSeconds, LoopTiming.MinCycleDuration);
            scheduleStartUT = playbackStartUT;
            intervalSeconds = baseIntervalSeconds;

            if (recIdx >= 0
                && autoLoopScheduleCache != null
                && autoLoopScheduleCache.TryGetValue(recIdx, out var cachedSchedule))
            {
                scheduleStartUT = cachedSchedule.LaunchStartUT;
                intervalSeconds = cachedSchedule.LaunchCadenceSeconds;
                return true;
            }

            if (recIdx >= 0 && GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue(rec))
            {
                var committed = RecordingStore.CommittedRecordings;
                var trajectories = new List<IPlaybackTrajectory>(committed.Count);
                for (int i = 0; i < committed.Count; i++)
                    trajectories.Add(committed[i]);

                if (GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule(
                        trajectories,
                        recIdx,
                        baseIntervalSeconds,
                        out var autoSchedule))
                {
                    scheduleStartUT = autoSchedule.LaunchStartUT;
                    intervalSeconds = autoSchedule.LaunchCadenceSeconds;
                }
            }

            return true;
        }

        private static bool TryGetLoopSchedule(
            Recording rec,
            int recIdx,
            out double playbackStartUT,
            out double scheduleStartUT,
            out double duration,
            out double intervalSeconds)
        {
            return TryGetLoopSchedule(
                rec,
                recIdx,
                null,
                out playbackStartUT,
                out scheduleStartUT,
                out duration,
                out intervalSeconds);
        }

        internal static bool TryComputeLoopUT(
            Recording rec,
            double currentUT,
            out double loopUT,
            out long cycleIndex,
            out bool inPauseWindow,
            int recIdx = -1)
        {
            return TryComputeLoopUT(
                rec,
                currentUT,
                out loopUT,
                out cycleIndex,
                out inPauseWindow,
                recIdx,
                null);
        }

        internal static bool TryComputeLoopUT(
            Recording rec,
            double currentUT,
            out double loopUT,
            out long cycleIndex,
            out bool inPauseWindow,
            int recIdx,
            IReadOnlyDictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule> autoLoopScheduleCache)
        {
            cycleIndex = 0;
            inPauseWindow = false;
            loopUT = 0.0;
            if (!TryGetLoopSchedule(
                    rec,
                    recIdx,
                    autoLoopScheduleCache,
                    out double playbackStartUT,
                    out double scheduleStartUT,
                    out double duration,
                    out double intervalSeconds))
                return false;

            if (!GhostPlaybackLogic.TryComputeLoopPlaybackPhase(
                    currentUT, scheduleStartUT, duration, intervalSeconds,
                    out double playbackPhase, out cycleIndex, out inPauseWindow))
            {
                return false;
            }

            loopUT = playbackStartUT + playbackPhase;
            return true;
        }

        /// <summary>
        /// Get the loop interval for a recording. Returns the launch-to-launch period
        /// in seconds (#381) — always &gt;= LoopTiming.MinCycleDuration.
        /// Overlap emerges when period &lt; recording duration (see IsOverlapLoop).
        /// </summary>
        internal static double GetLoopIntervalSeconds(Recording rec, int recIdx = -1)
        {
            return GetLoopIntervalSeconds(rec, recIdx, null);
        }

        internal static double GetLoopIntervalSeconds(
            Recording rec,
            int recIdx,
            IReadOnlyDictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule> autoLoopScheduleCache)
        {
            if (TryGetLoopSchedule(
                    rec,
                    recIdx,
                    autoLoopScheduleCache,
                    out _,
                    out _,
                    out _,
                    out double intervalSeconds))
                return intervalSeconds;

            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? LoopTiming.DefaultLoopIntervalSeconds;
            return GhostPlaybackLogic.ResolveLoopInterval(
                rec, globalInterval, LoopTiming.DefaultLoopIntervalSeconds, LoopTiming.MinCycleDuration);
        }
    }
}
