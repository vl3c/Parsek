using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Playback policy for debris that carries the explicit parent-recording
    /// anchor contract. The v13 render contract uses body-fixed primary data
    /// for ordinary debris and only relies on anchor-local replay for
    /// loop-anchored chains.
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
            internal double FirstBodyFixedFrameUT;
            internal double LastBodyFixedFrameUT;
            internal string AnchorRecordingId;
            internal string Reason;
            internal bool RelativeFramesCoverUT;
            internal bool BodyFixedFramesCoverUT;

            internal static ParentAnchoredDebrisCoverageDiagnostic Create(string reason)
            {
                return new ParentAnchoredDebrisCoverageDiagnostic
                {
                    SectionIndex = -1,
                    SectionStartUT = double.NaN,
                    SectionEndUT = double.NaN,
                    FirstRelativeFrameUT = double.NaN,
                    LastRelativeFrameUT = double.NaN,
                    FirstBodyFixedFrameUT = double.NaN,
                    LastBodyFixedFrameUT = double.NaN,
                    AnchorRecordingId = null,
                    Reason = reason,
                    RelativeFramesCoverUT = false,
                    BodyFixedFramesCoverUT = false,
                };
            }
        }

        /// <summary>
        /// Parent-anchored debris should disappear when its recorded parent
        /// anchor cannot be resolved. The v7 body-fixed primary is not an
        /// independent fallback for this case because it can continue stale
        /// motion after the debris has left the parent's resolvable range.
        /// A non-null parent id is enough to select this contract; an empty
        /// serialized id is malformed current-schema data and should fail
        /// closed rather than be treated as non-parent-anchored debris.
        /// </summary>
        internal static bool ShouldRetireOnRecordedParentAnchorMiss(
            IPlaybackTrajectory traj)
        {
            // Parent-anchored gate is now `ParentAnchorRecordingId != null` alone:
            // both genuine debris and controlled-decoupled children participate in
            // the parent-anchored coverage / retirement / authored-frame-gap policy.
            // The retirement decision is per-frame (not sticky) and only fires
            // within a Relative section; controlled children's post-window Absolute
            // tail dispatches through the standard Absolute path without reaching
            // this predicate (see plan section 7).
            return traj != null
                && traj.ParentAnchorRecordingId != null;
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

        /// <summary>
        /// Ordinary parent-anchored debris must not route through recorded
        /// Relative playback once v13 body-fixed primary data is the authored
        /// render surface. A true result means callers should skip the
        /// recorded-relative resolver and either use body-fixed primary or
        /// fail closed.
        /// </summary>
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
                && diagnostic.Reason != "non-relative-section";
        }

        internal static bool RelativeFramesCoverUT(
            IPlaybackTrajectory traj,
            TrackSection section,
            double playbackUT,
            DebrisRelativeCoverageMode mode = DebrisRelativeCoverageMode.PlaybackCompatible)
        {
            List<TrajectoryPoint> frames = ResolveRelativeFrames(traj, section);
            return DebrisRelativeCoveragePrimitives.RelativeFramesCoverUT(
                frames,
                section.startUT,
                section.endUT,
                playbackUT,
                mode);
        }

        internal static bool BodyFixedPrimaryFramesCoverUT(
            TrackSection section,
            double playbackUT)
        {
            return DebrisRelativeCoveragePrimitives.BodyFixedPrimaryFramesCoverUT(
                section.bodyFixedFrames,
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

            if (string.IsNullOrWhiteSpace(traj.ParentAnchorRecordingId))
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
                section.bodyFixedFrames,
                out diagnostic.FirstBodyFixedFrameUT,
                out diagnostic.LastBodyFixedFrameUT);

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
            diagnostic.BodyFixedFramesCoverUT = DebrisRelativeCoveragePrimitives.BodyFixedPrimaryFramesCoverUT(
                section.bodyFixedFrames,
                playbackUT);

            if (diagnostic.BodyFixedFramesCoverUT)
                diagnostic.Reason = "covered-by-body-fixed-primary";
            else if (diagnostic.RelativeFramesCoverUT)
                diagnostic.Reason = "relative-only-without-body-fixed-primary";
            else
                diagnostic.Reason = "relative-and-body-fixed-frames-out-of-range";

            return diagnostic;
        }

        private static bool ShouldRetireFromDiagnostic(
            ParentAnchoredDebrisCoverageDiagnostic diagnostic)
        {
            return diagnostic.Reason == "parent-recording-id-empty"
                || diagnostic.Reason == "no-track-sections"
                || diagnostic.Reason == "no-covering-section"
                || diagnostic.Reason == "relative-only-without-body-fixed-primary"
                || diagnostic.Reason == "relative-and-body-fixed-frames-out-of-range";
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
        /// Parent-anchored debris can start with a structural seed point
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
