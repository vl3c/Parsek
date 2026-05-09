using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Playback policy for v12+ debris that carries the explicit
    /// parent-recording anchor contract.
    /// </summary>
    internal static class DebrisRelativePlaybackPolicy
    {
        private const double UtEpsilon = 1e-6;

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
                || maxBridgeSeconds <= UtEpsilon
                || traj.TrackSections == null
                || traj.TrackSections.Count == 0)
            {
                return false;
            }

            int sectionIndex = TrajectoryMath.FindTrackSectionForUT(
                traj.TrackSections, activationStartUT + UtEpsilon);
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
                || Math.Abs(seed.ut - activationStartUT) > UtEpsilon
                || Math.Abs(seed.ut - section.startUT) > UtEpsilon)
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
            if (bridgeDuration <= UtEpsilon || bridgeDuration > maxBridgeSeconds)
                return false;

            bridgeEndUT = firstOrdinaryPoint.ut;
            return true;
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
                if (point.ut <= seedUT + UtEpsilon)
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
