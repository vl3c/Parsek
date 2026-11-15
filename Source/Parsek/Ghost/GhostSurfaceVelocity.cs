using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Pure derivation of a ghost's velocity relative to the body surface.
    ///
    /// <c>TrajectoryPoint.velocity</c> is the Unity world vector as recorded: surface-relative
    /// below the body's <c>inverseRotThresholdAltitude</c> (the world co-rotates there, which
    /// covers every landed or low vessel), inertial above it, and in world axes whose yaw about
    /// the spin axis depends on the recording session. No single conversion turns it into a
    /// ground speed: subtracting stock <c>getRFrmVel</c> invents ~175 m/s on a parked Kerbin
    /// ghost, and the direction still carries the session yaw.
    ///
    /// Recorded POSITIONS (latitude / longitude / altitude) are body-fixed, so two of them
    /// resolved through <c>GetWorldSurfacePosition</c> at the same moment differ only by the
    /// vessel's motion over the surface, already in today's world axes. The bracket the
    /// interpolation uses supplies both, so the derivation costs one subtraction and one
    /// divide per ghost per frame and allocates nothing.
    ///
    /// RELATIVE-frame sections store anchor-local metre offsets in those same fields; they
    /// are never body-fixed and must not be fed here. Use the section's
    /// <c>bodyFixedFrames</c> when present (<see cref="SelectRelativeSectionBodyFixedFrames"/>),
    /// otherwise the surface velocity is unknown.
    /// </summary>
    internal static class GhostSurfaceVelocity
    {
        /// <summary>A bracket shorter than this is a duplicate sample, not a time step.</summary>
        internal const double MinBracketSeconds = 1e-4;

        /// <summary>
        /// A bracket longer than this spans a warp, pause or sampling hole; the chord across it
        /// is an average over minutes, not the ghost's current ground speed.
        /// </summary>
        internal const double MaxBracketSeconds = 60.0;

        /// <summary>
        /// Surface velocity from two body-fixed positions resolved to world space in the same
        /// frame, and the UT gap between their samples. False (and zero) when the gap is not a
        /// usable time step or either input is non-finite.
        /// </summary>
        internal static bool TryFromBracketPositions(
            Vector3d worldPosBefore, Vector3d worldPosAfter, double dtSeconds,
            out Vector3 surfaceVelocity)
        {
            surfaceVelocity = Vector3.zero;
            if (double.IsNaN(dtSeconds) || double.IsInfinity(dtSeconds)
                || dtSeconds < MinBracketSeconds || dtSeconds > MaxBracketSeconds)
                return false;

            Vector3d delta = worldPosAfter - worldPosBefore;
            if (!IsFinite(delta.x) || !IsFinite(delta.y) || !IsFinite(delta.z))
                return false;

            surfaceVelocity = (Vector3)(delta / dtSeconds);
            return true;
        }

        /// <summary>
        /// Index of the sample that starts the bracket containing <paramref name="ut"/>, or -1
        /// when the list has fewer than two samples or <paramref name="ut"/> lies outside
        /// [first.ut, last.ut]. Binary search; no allocation and no diagnostics counters.
        /// </summary>
        internal static int FindBracketIndex(List<TrajectoryPoint> frames, double ut)
        {
            if (frames == null || frames.Count < 2 || double.IsNaN(ut))
                return -1;
            int last = frames.Count - 1;
            if (ut < frames[0].ut || ut > frames[last].ut)
                return -1;
            if (ut >= frames[last - 1].ut)
                return last - 1;

            int low = 0;
            int high = last - 1;
            while (low < high)
            {
                int mid = low + ((high - low + 1) >> 1);
                if (frames[mid].ut <= ut)
                    low = mid;
                else
                    high = mid - 1;
            }
            return low;
        }

        /// <summary>
        /// The body-fixed samples a RELATIVE section offers for a surface velocity: its
        /// <c>bodyFixedFrames</c> when there are at least two, otherwise null. Any other
        /// reference frame returns null too; its flat frames are the caller's own bracket.
        /// </summary>
        internal static List<TrajectoryPoint> SelectRelativeSectionBodyFixedFrames(TrackSection section)
        {
            if (section.referenceFrame != ReferenceFrame.Relative)
                return null;
            List<TrajectoryPoint> frames = section.bodyFixedFrames;
            return frames != null && frames.Count >= 2 ? frames : null;
        }

        /// <summary>
        /// Surface velocity of a ghost moving on an orbit: the orbit's inertial velocity
        /// (stock Zup, body-relative) lifted to world axes, minus the body's rotating-frame
        /// velocity at the ghost (stock <c>getRFrmVel</c>, world). This is the one place the
        /// rotating-frame term is legitimately subtracted: an <c>Orbit</c> velocity is truly
        /// inertial, unlike a recorded <c>TrajectoryPoint.velocity</c>.
        /// </summary>
        internal static Vector3 FromOrbitVelocity(Vector3d zupOrbitalVelocity, Vector3d rotatingFrameVelocityWorld)
        {
            return (Vector3)(TrajectoryMath.SwizzleZupBodyRelativeToWorld(zupOrbitalVelocity)
                - rotatingFrameVelocityWorld);
        }

        /// <summary>Grep-stable log token for a source.</summary>
        internal static string SourceToken(GhostSurfaceVelocitySource source)
        {
            switch (source)
            {
                case GhostSurfaceVelocitySource.Positions: return "positions";
                case GhostSurfaceVelocitySource.SurfaceHold: return "surfaceHold";
                case GhostSurfaceVelocitySource.Orbit: return "orbit";
                default: return "none";
            }
        }

        /// <summary>
        /// The surface velocity a reader may use: the stored vector when its source is known,
        /// otherwise zero (no ground speed is ever invented).
        /// </summary>
        internal static Vector3 Usable(Vector3 surfaceVelocity, GhostSurfaceVelocitySource source)
        {
            return source == GhostSurfaceVelocitySource.None ? Vector3.zero : surfaceVelocity;
        }

        private static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }
}
