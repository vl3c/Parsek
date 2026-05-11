using System;
using System.Collections.Generic;

namespace Parsek
{
    internal enum DebrisRelativeCoverageMode
    {
        PlaybackCompatible,
        RecorderPersistable
    }

    internal static class DebrisRelativeCoveragePrimitives
    {
        internal const double UtEpsilon = 1e-6;

        internal static bool IsFiniteUT(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        internal static bool RelativeFramesCoverUT(
            IList<TrajectoryPoint> frames,
            double sectionStartUT,
            double sectionEndUT,
            double targetUT,
            DebrisRelativeCoverageMode mode)
        {
            if (frames == null || frames.Count == 0)
                return false;
            if (!IsFiniteUT(targetUT))
                return false;

            if (frames.Count == 1)
            {
                if (Math.Abs(frames[0].ut - targetUT) <= UtEpsilon)
                    return true;

                if (mode == DebrisRelativeCoverageMode.RecorderPersistable)
                    return false;

                if (!IsFiniteUT(sectionStartUT) || !IsFiniteUT(sectionEndUT))
                    return false;

                double start = Math.Min(sectionStartUT, sectionEndUT);
                double end = Math.Max(sectionStartUT, sectionEndUT);
                return targetUT >= start - UtEpsilon
                    && targetUT <= end + UtEpsilon;
            }

            return targetUT >= frames[0].ut - UtEpsilon
                && targetUT <= frames[frames.Count - 1].ut + UtEpsilon;
        }

        internal static bool AbsoluteShadowFramesCoverUT(
            IList<TrajectoryPoint> frames,
            double targetUT)
        {
            // The shadow renderer interpolates between two samples; unlike a
            // single Relative frame, one body-fixed primary point cannot cover a
            // full section span. See TryPositionFromBodyFixedPrimary.
            if (frames == null || frames.Count < 2)
                return false;
            if (!IsFiniteUT(targetUT))
                return false;

            return targetUT >= frames[0].ut - UtEpsilon
                && targetUT <= frames[frames.Count - 1].ut + UtEpsilon;
        }

        internal static bool TryGetRelativeFrameCoverageEndUT(
            IList<TrajectoryPoint> frames,
            double sectionStartUT,
            double sectionEndUT,
            DebrisRelativeCoverageMode mode,
            out double endUT)
        {
            endUT = double.NaN;
            if (frames == null || frames.Count == 0)
                return false;

            if (frames.Count == 1)
            {
                if (mode == DebrisRelativeCoverageMode.PlaybackCompatible
                    && IsFiniteUT(sectionStartUT)
                    && IsFiniteUT(sectionEndUT))
                {
                    endUT = Math.Max(sectionStartUT, sectionEndUT);
                    return true;
                }

                endUT = frames[0].ut;
                return IsFiniteUT(endUT);
            }

            endUT = frames[frames.Count - 1].ut;
            return IsFiniteUT(endUT);
        }

        internal static bool TryGetAbsoluteShadowCoverageEndUT(
            IList<TrajectoryPoint> frames,
            out double endUT)
        {
            endUT = double.NaN;
            if (frames == null || frames.Count < 2)
                return false;

            endUT = frames[frames.Count - 1].ut;
            return IsFiniteUT(endUT);
        }

        internal static bool TryGetCheckpointCoverageEndUT(
            IList<OrbitSegment> checkpoints,
            out double endUT)
        {
            endUT = double.NaN;
            bool found = false;
            if (checkpoints == null)
                return false;

            for (int i = 0; i < checkpoints.Count; i++)
            {
                OrbitSegment checkpoint = checkpoints[i];
                if (checkpoint.isPredicted || !IsFiniteUT(checkpoint.endUT))
                    continue;

                if (!found || checkpoint.endUT > endUT)
                    endUT = checkpoint.endUT;
                found = true;
            }

            return found;
        }

        internal static bool TryGetRenderableCoverageEndUT(
            IList<TrajectoryPoint> relativeFrames,
            IList<TrajectoryPoint> bodyFixedFrames,
            IList<OrbitSegment> checkpoints,
            double sectionStartUT,
            double sectionEndUT,
            DebrisRelativeCoverageMode mode,
            out double coverageEndUT,
            out string coverageReason)
        {
            coverageEndUT = double.NaN;
            coverageReason = null;
            bool found = false;

            if (TryGetRelativeFrameCoverageEndUT(
                    relativeFrames,
                    sectionStartUT,
                    sectionEndUT,
                    mode,
                    out double relativeEndUT))
            {
                ConsiderCoverage(relativeEndUT, "relative-frames", ref coverageEndUT, ref coverageReason, ref found);
            }

            if (TryGetAbsoluteShadowCoverageEndUT(bodyFixedFrames, out double shadowEndUT))
            {
                ConsiderCoverage(shadowEndUT, "body-fixed-primary", ref coverageEndUT, ref coverageReason, ref found);
            }

            if (TryGetCheckpointCoverageEndUT(checkpoints, out double checkpointEndUT))
            {
                ConsiderCoverage(checkpointEndUT, "checkpoint", ref coverageEndUT, ref coverageReason, ref found);
            }

            if (!found)
                coverageReason = "no-authored-coverage";

            return found;
        }

        internal static void SetFrameRange(
            IList<TrajectoryPoint> frames,
            out double firstUT,
            out double lastUT)
        {
            if (frames == null || frames.Count == 0)
            {
                firstUT = double.NaN;
                lastUT = double.NaN;
                return;
            }

            firstUT = frames[0].ut;
            lastUT = frames[frames.Count - 1].ut;
        }

        private static void ConsiderCoverage(
            double candidateUT,
            string reason,
            ref double coverageEndUT,
            ref string coverageReason,
            ref bool found)
        {
            if (!IsFiniteUT(candidateUT))
                return;

            if (!found || candidateUT > coverageEndUT)
            {
                coverageEndUT = candidateUT;
                coverageReason = reason;
            }

            found = true;
        }
    }
}
