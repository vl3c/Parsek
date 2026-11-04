using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// What one flat <see cref="Recording.Points"/> entry holds, read from the track
    /// section that authored it.
    /// </summary>
    internal enum FlatPointFrame
    {
        /// <summary>Body-fixed lat/lon/alt: safe for <c>GetWorldSurfacePosition</c>.</summary>
        BodyFixed = 0,
        /// <summary>
        /// A Relative-section anchor-local metre offset whose section also recorded the
        /// body-fixed shadow at the same UT; the shadow is the usable position.
        /// </summary>
        RelativeWithShadow,
        /// <summary>
        /// A Relative-section anchor-local metre offset with no body-fixed shadow at its UT.
        /// It has no body-fixed reading at all: a caller must not place anything from it.
        /// </summary>
        RelativeNoShadow,
    }

    /// <summary>
    /// The flat <see cref="Recording.Points"/> list is the concatenation of every section's
    /// <c>frames</c> (<see cref="TrajectoryTextSidecarCodec.RebuildPointsFromTrackSections"/>,
    /// and the recorder's dual write), so inside a RELATIVE section its entries are
    /// anchor-local metre offsets stored in the latitude / longitude / altitude fields. A
    /// flat-points playback fallback that reaches one of them (a section gap, a span no
    /// section covers) would read metres as degrees and put the ghost deep inside the
    /// planet. This classifier tells a flat-points reader which entries those are and hands
    /// back the section's body-fixed shadow (<c>bodyFixedFrames</c>) at the same UT when one
    /// exists. Pure: no Unity or KSP calls.
    /// </summary>
    internal static class FlatPointFrameDispatch
    {
        internal const double UtToleranceSeconds = 1e-9;

        /// <summary>
        /// Classifies <paramref name="point"/> (an entry of the flat Points list) against
        /// <paramref name="sections"/>. A point is a Relative offset only when a RELATIVE
        /// section whose span holds its UT carries a byte-equal frame: the flat list is built
        /// from the section frames, so equality identifies the authoring section even at a
        /// boundary UT shared with an Absolute neighbour. <paramref name="bodyFixed"/> is the
        /// point itself for <see cref="FlatPointFrame.BodyFixed"/>, the shadow for
        /// <see cref="FlatPointFrame.RelativeWithShadow"/>, and the input for
        /// <see cref="FlatPointFrame.RelativeNoShadow"/> (not to be used).
        /// </summary>
        internal static FlatPointFrame Classify(
            IList<TrackSection> sections,
            TrajectoryPoint point,
            out TrajectoryPoint bodyFixed)
        {
            bodyFixed = point;
            if (sections == null || sections.Count == 0)
                return FlatPointFrame.BodyFixed;

            for (int i = 0; i < sections.Count; i++)
            {
                TrackSection section = sections[i];
                if (section.referenceFrame != ReferenceFrame.Relative)
                    continue;
                if (point.ut < section.startUT - UtToleranceSeconds)
                    continue;
                bool closed = section.endUT >= section.startUT;
                if (closed && point.ut > section.endUT + UtToleranceSeconds)
                    continue;
                if (!ContainsFrame(section.frames, point))
                    continue;

                if (TryFindAtUT(section.bodyFixedFrames, point.ut, out TrajectoryPoint shadow))
                {
                    bodyFixed = shadow;
                    return FlatPointFrame.RelativeWithShadow;
                }
                return FlatPointFrame.RelativeNoShadow;
            }

            return FlatPointFrame.BodyFixed;
        }

        /// <summary>
        /// Resolves both interpolation endpoints of a flat-points read. Returns false when
        /// either endpoint is a Relative offset with no shadow (the read must be refused);
        /// otherwise replaces Relative endpoints with their shadows in place and reports how
        /// many it replaced.
        /// </summary>
        internal static bool TryDispatchEndpoints(
            IList<TrackSection> sections,
            ref TrajectoryPoint before,
            ref TrajectoryPoint after,
            out int substituted)
        {
            substituted = 0;
            if (sections == null || sections.Count == 0)
                return true;

            FlatPointFrame beforeFrame = Classify(sections, before, out TrajectoryPoint beforeFixed);
            FlatPointFrame afterFrame = Classify(sections, after, out TrajectoryPoint afterFixed);
            if (beforeFrame == FlatPointFrame.RelativeNoShadow
                || afterFrame == FlatPointFrame.RelativeNoShadow)
            {
                return false;
            }

            if (beforeFrame == FlatPointFrame.RelativeWithShadow)
            {
                before = beforeFixed;
                substituted++;
            }
            if (afterFrame == FlatPointFrame.RelativeWithShadow)
            {
                after = afterFixed;
                substituted++;
            }
            return true;
        }

        private static bool ContainsFrame(List<TrajectoryPoint> frames, TrajectoryPoint point)
        {
            if (frames == null || frames.Count == 0)
                return false;

            for (int i = LowerBound(frames, point.ut); i < frames.Count; i++)
            {
                TrajectoryPoint frame = frames[i];
                if (frame.ut > point.ut + UtToleranceSeconds)
                    return false;
                if (frame.latitude == point.latitude
                    && frame.longitude == point.longitude
                    && frame.altitude == point.altitude
                    && string.Equals(frame.bodyName, point.bodyName, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryFindAtUT(List<TrajectoryPoint> frames, double ut, out TrajectoryPoint found)
        {
            found = default(TrajectoryPoint);
            if (frames == null || frames.Count == 0)
                return false;

            for (int i = LowerBound(frames, ut); i < frames.Count; i++)
            {
                TrajectoryPoint frame = frames[i];
                if (frame.ut > ut + UtToleranceSeconds)
                    return false;
                found = frame;
                return true;
            }
            return false;
        }

        // First index whose UT is >= ut - tolerance. Frames are UT-ascending.
        private static int LowerBound(List<TrajectoryPoint> frames, double ut)
        {
            double key = ut - UtToleranceSeconds;
            int lo = 0;
            int hi = frames.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (frames[mid].ut < key)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }
    }
}
