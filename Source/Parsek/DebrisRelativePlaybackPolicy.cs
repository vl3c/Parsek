using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Playback policy for v12+ debris that carries the explicit
    /// parent-recording anchor contract. The recorded parent is authoritative:
    /// playback retires outside recorded Relative coverage and may transiently
    /// hide frames whose parent rotation interpolation is unreliable for a
    /// large child offset.
    /// </summary>
    internal static class DebrisRelativePlaybackPolicy
    {
        internal struct ParentAnchoredDebrisCoverageDiagnostic
        {
            internal int SectionIndex;
            internal double SectionStartUT;
            internal double SectionEndUT;
            internal double FirstRelativeFrameUT;
            internal double LastRelativeFrameUT;
            internal double FirstAbsoluteFrameUT;
            internal double LastAbsoluteFrameUT;
            internal string AnchorRecordingId;
            internal string Reason;
            internal bool RelativeFramesCoverUT;
            internal bool AbsoluteFramesCoverUT;

            internal static ParentAnchoredDebrisCoverageDiagnostic Create(string reason)
            {
                return new ParentAnchoredDebrisCoverageDiagnostic
                {
                    SectionIndex = -1,
                    SectionStartUT = double.NaN,
                    SectionEndUT = double.NaN,
                    FirstRelativeFrameUT = double.NaN,
                    LastRelativeFrameUT = double.NaN,
                    FirstAbsoluteFrameUT = double.NaN,
                    LastAbsoluteFrameUT = double.NaN,
                    AnchorRecordingId = null,
                    Reason = reason,
                    RelativeFramesCoverUT = false,
                    AbsoluteFramesCoverUT = false,
                };
            }
        }

        /// <summary>
        /// Parent-anchored debris should disappear when its recorded parent
        /// anchor cannot be resolved. The v7 absolute shadow is not an
        /// independent fallback for this case because it can continue stale
        /// motion after the debris has left the parent's resolvable range.
        /// A non-null parent id is enough to select this contract; an empty
        /// serialized id is malformed v12+ data and should fail closed rather
        /// than be treated as legacy v11 debris.
        /// </summary>
        internal static bool ShouldRetireOnRecordedParentAnchorMiss(
            IPlaybackTrajectory traj)
        {
            return traj != null
                && traj.IsDebris
                && traj.DebrisParentRecordingId != null;
        }

        internal static bool ShouldRetireOutsideAuthoredRelativeCoverage(
            IPlaybackTrajectory traj,
            double playbackUT,
            out ParentAnchoredDebrisCoverageDiagnostic diagnostic)
        {
            diagnostic = BuildAuthoredCoverageDiagnostic(traj, playbackUT);
            if (!ShouldRetireOnRecordedParentAnchorMiss(traj))
                return false;
            if (traj.LoopAnchorVesselId != 0u)
                return false;

            return ShouldRetireFromDiagnostic(diagnostic);
        }

        internal static bool ShouldSkipRecordedRelativeResolverForAuthoredFrameGap(
            IPlaybackTrajectory traj,
            double playbackUT,
            out ParentAnchoredDebrisCoverageDiagnostic diagnostic)
        {
            diagnostic = BuildAuthoredCoverageDiagnostic(traj, playbackUT);
            if (!ShouldRetireOnRecordedParentAnchorMiss(traj)
                || traj.LoopAnchorVesselId != 0u)
            {
                return false;
            }

            if (ShouldRetireFromDiagnostic(diagnostic))
                return true;

            return diagnostic.SectionIndex >= 0
                && diagnostic.Reason != "non-relative-section"
                && !diagnostic.RelativeFramesCoverUT;
        }

        internal static bool RelativeFramesCoverUT(
            IPlaybackTrajectory traj,
            TrackSection section,
            double playbackUT)
        {
            List<TrajectoryPoint> frames = ResolveRelativeFrames(traj, section);
            return DebrisRelativeCoveragePrimitives.RelativeFramesCoverUT(
                frames,
                section.startUT,
                section.endUT,
                playbackUT,
                DebrisRelativeCoverageMode.PlaybackCompatible);
        }

        internal static bool AbsoluteShadowFramesCoverUT(
            TrackSection section,
            double playbackUT)
        {
            return DebrisRelativeCoveragePrimitives.AbsoluteShadowFramesCoverUT(
                section.absoluteFrames,
                playbackUT);
        }

        private static ParentAnchoredDebrisCoverageDiagnostic BuildAuthoredCoverageDiagnostic(
            IPlaybackTrajectory traj,
            double playbackUT)
        {
            if (!ShouldRetireOnRecordedParentAnchorMiss(traj))
                return ParentAnchoredDebrisCoverageDiagnostic.Create(
                    "not-parent-anchored-debris");

            if (traj.LoopAnchorVesselId != 0u)
                return ParentAnchoredDebrisCoverageDiagnostic.Create(
                    "live-loop-anchor");

            if (string.IsNullOrWhiteSpace(traj.DebrisParentRecordingId))
                return ParentAnchoredDebrisCoverageDiagnostic.Create(
                    "parent-recording-id-empty");

            if (traj.TrackSections == null || traj.TrackSections.Count == 0)
                return ParentAnchoredDebrisCoverageDiagnostic.Create(
                    "no-track-sections");

            int sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                traj.TrackSections, playbackUT);
            if (sectionIndex < 0 || sectionIndex >= traj.TrackSections.Count)
                return ParentAnchoredDebrisCoverageDiagnostic.Create(
                    "no-covering-section");

            TrackSection section = traj.TrackSections[sectionIndex];
            var diagnostic = ParentAnchoredDebrisCoverageDiagnostic.Create(null);
            diagnostic.SectionIndex = sectionIndex;
            diagnostic.SectionStartUT = section.startUT;
            diagnostic.SectionEndUT = section.endUT;
            diagnostic.AnchorRecordingId = section.anchorRecordingId;

            List<TrajectoryPoint> relativeFrames = ResolveRelativeFrames(traj, section);
            DebrisRelativeCoveragePrimitives.SetFrameRange(
                relativeFrames,
                out diagnostic.FirstRelativeFrameUT,
                out diagnostic.LastRelativeFrameUT);
            DebrisRelativeCoveragePrimitives.SetFrameRange(
                section.absoluteFrames,
                out diagnostic.FirstAbsoluteFrameUT,
                out diagnostic.LastAbsoluteFrameUT);

            if (section.referenceFrame != ReferenceFrame.Relative)
            {
                diagnostic.Reason = "non-relative-section";
                return diagnostic;
            }

            diagnostic.RelativeFramesCoverUT = DebrisRelativeCoveragePrimitives.RelativeFramesCoverUT(
                relativeFrames,
                section.startUT,
                section.endUT,
                playbackUT,
                DebrisRelativeCoverageMode.PlaybackCompatible);
            diagnostic.AbsoluteFramesCoverUT = DebrisRelativeCoveragePrimitives.AbsoluteShadowFramesCoverUT(
                section.absoluteFrames,
                playbackUT);

            if (diagnostic.RelativeFramesCoverUT)
                diagnostic.Reason = "covered-by-relative-frames";
            else if (diagnostic.AbsoluteFramesCoverUT)
                diagnostic.Reason = "covered-by-absolute-shadow";
            else
                diagnostic.Reason = "relative-and-shadow-frames-out-of-range";

            return diagnostic;
        }

        private static bool ShouldRetireFromDiagnostic(
            ParentAnchoredDebrisCoverageDiagnostic diagnostic)
        {
            return diagnostic.Reason == "parent-recording-id-empty"
                || diagnostic.Reason == "no-track-sections"
                || diagnostic.Reason == "no-covering-section"
                || diagnostic.Reason == "non-relative-section"
                || diagnostic.Reason == "relative-and-shadow-frames-out-of-range";
        }

        private static List<TrajectoryPoint> ResolveRelativeFrames(
            IPlaybackTrajectory traj,
            TrackSection section)
        {
            if (section.frames != null && section.frames.Count > 0)
                return section.frames;

            if (traj?.Points == null || traj.Points.Count == 0)
                return null;

            double start = Math.Min(section.startUT, section.endUT)
                - DebrisRelativeCoveragePrimitives.UtEpsilon;
            double end = Math.Max(section.startUT, section.endUT)
                + DebrisRelativeCoveragePrimitives.UtEpsilon;
            var projected = new List<TrajectoryPoint>();
            for (int i = 0; i < traj.Points.Count; i++)
            {
                TrajectoryPoint point = traj.Points[i];
                if (point.ut >= start && point.ut <= end)
                    projected.Add(point);
            }

            return projected;
        }

        /// <summary>
        /// v12+ parent-anchored debris can start with a structural seed point
        /// at the separation origin, followed by the first ordinary sampled
        /// relative offset several frames later. Hide that initial seed bridge
        /// instead of showing the debris sliding from seed to sample.
        /// </summary>
        internal static bool TryResolveInitialStructuralSeedBridgeEndUT(
            IPlaybackTrajectory traj,
            double activationStartUT,
            double maxBridgeSeconds,
            out double bridgeEndUT)
        {
            bridgeEndUT = double.NaN;
            if (!ShouldRetireOnRecordedParentAnchorMiss(traj)
                || maxBridgeSeconds <= DebrisRelativeCoveragePrimitives.UtEpsilon
                || traj.TrackSections == null
                || traj.TrackSections.Count == 0)
            {
                return false;
            }

            int sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                traj.TrackSections, activationStartUT + DebrisRelativeCoveragePrimitives.UtEpsilon);
            if (sectionIndex < 0)
                sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                    traj.TrackSections, activationStartUT);
            if (sectionIndex < 0 || sectionIndex >= traj.TrackSections.Count)
                return false;

            TrackSection section = traj.TrackSections[sectionIndex];
            if (section.referenceFrame != ReferenceFrame.Relative
                || section.frames == null
                || section.frames.Count < 2)
            {
                return false;
            }

            TrajectoryPoint seed = section.frames[0];
            if (!HasStructuralEventSnapshotFlag(seed)
                || Math.Abs(seed.ut - activationStartUT) > DebrisRelativeCoveragePrimitives.UtEpsilon
                || Math.Abs(seed.ut - section.startUT) > DebrisRelativeCoveragePrimitives.UtEpsilon)
            {
                return false;
            }

            if (!TryFindFirstOrdinaryPointAfterSeed(
                    section.frames,
                    seed.ut,
                    out TrajectoryPoint firstOrdinaryPoint))
            {
                return false;
            }

            double bridgeDuration = firstOrdinaryPoint.ut - seed.ut;
            if (bridgeDuration <= DebrisRelativeCoveragePrimitives.UtEpsilon || bridgeDuration > maxBridgeSeconds)
                return false;

            if (!IsSyntheticSeedBridgeDistance(
                    seed,
                    firstOrdinaryPoint,
                    GhostPlayback.InitialDebrisSeedBridgeActivationHiddenMinDistanceMeters))
            {
                return false;
            }

            bridgeEndUT = firstOrdinaryPoint.ut;
            return true;
        }

        internal static bool IsSyntheticSeedBridgeDistance(
            TrajectoryPoint seed,
            TrajectoryPoint firstOrdinaryPoint,
            double minDistanceMeters)
        {
            if (minDistanceMeters <= 0.0)
                return true;

            double dx = firstOrdinaryPoint.latitude - seed.latitude;
            double dy = firstOrdinaryPoint.longitude - seed.longitude;
            double dz = firstOrdinaryPoint.altitude - seed.altitude;
            double distanceSquared = dx * dx + dy * dy + dz * dz;
            return distanceSquared >= minDistanceMeters * minDistanceMeters;
        }

        private static bool TryFindFirstOrdinaryPointAfterSeed(
            List<TrajectoryPoint> frames,
            double seedUT,
            out TrajectoryPoint firstOrdinaryPoint)
        {
            firstOrdinaryPoint = default(TrajectoryPoint);
            if (frames == null)
                return false;

            for (int i = 1; i < frames.Count; i++)
            {
                TrajectoryPoint point = frames[i];
                if (point.ut <= seedUT + DebrisRelativeCoveragePrimitives.UtEpsilon)
                    continue;

                if (HasStructuralEventSnapshotFlag(point))
                    continue;

                firstOrdinaryPoint = point;
                return true;
            }

            return false;
        }

        private static bool HasStructuralEventSnapshotFlag(TrajectoryPoint point)
        {
            return ((TrajectoryPointFlags)point.flags
                    & TrajectoryPointFlags.StructuralEventSnapshot)
                == TrajectoryPointFlags.StructuralEventSnapshot;
        }
    }
}
