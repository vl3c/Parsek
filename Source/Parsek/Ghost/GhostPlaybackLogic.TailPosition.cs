using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    // Where a ghost is once its recorded points run out and its recording continues on an
    // orbit tail (the predicted scene-exit tail, or a recorded coast past the last sample).
    // Every surface that would otherwise clamp to the last recorded point (the flight-map
    // and Tracking Station markers, the KSC ghost) resolves the tail through here, so the
    // ghost follows its path to its recorded end instead of freezing where the samples stop.
    // The tail segment is the one the flight-scene mesh already rides
    // (GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment: a segment covering
    // the UT, or a predicted segment bridged backward across the gap after the last point),
    // so the map icon, the KSC ghost and the flight mesh agree on one position. Positions are
    // propagated by BallisticExtrapolator.TryPropagate (allocation-free, stock
    // frame) and lifted to world axes once here.
    internal static partial class GhostPlaybackLogic
    {
        /// <summary>
        /// True when <paramref name="ut"/> is past the recording's last flat point and an orbit
        /// tail segment carries the ghost there. Pure.
        /// </summary>
        internal static bool TryFindTailSegment(
            IPlaybackTrajectory traj, double ut, out OrbitSegment segment)
        {
            segment = default(OrbitSegment);
            if (traj == null || double.IsNaN(ut) || double.IsInfinity(ut))
                return false;
            if (ut > traj.EndUT + 1e-6)
                return false;
            return GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment(
                traj, ut, out segment, out _);
        }

        /// <summary>
        /// Time gate for a marker that draws from the recorded points: inside the flat points'
        /// span, or past it on an orbit tail. A recording with no flat points is outside. Pure.
        /// </summary>
        internal static bool IsInsidePointsOrTail(IPlaybackTrajectory traj, double ut)
        {
            if (traj?.Points == null || traj.Points.Count == 0)
                return false;
            if (ut < traj.Points[0].ut)
                return false;
            if (ut <= traj.Points[traj.Points.Count - 1].ut)
                return true;
            return TryFindTailSegment(traj, ut, out _);
        }

        /// <summary>
        /// Body-relative WORLD-axes position and velocity of a tail segment at
        /// <paramref name="ut"/>: <c>getRelativePositionAtUT(ut).xzy</c> in stock terms, so the
        /// world position is the body's world position plus <paramref name="worldOffset"/>.
        /// Pure apart from the Zup read <see cref="BallisticExtrapolator.TryPropagate"/> does
        /// (which declines to 0 headless).
        /// </summary>
        internal static bool TryComputeTailWorldOffset(
            OrbitSegment segment, double gravParameter, double ut,
            out Vector3d worldOffset, out Vector3d worldVelocity)
        {
            worldOffset = Vector3d.zero;
            worldVelocity = Vector3d.zero;
            if (!(gravParameter > 0.0) || double.IsNaN(ut) || double.IsInfinity(ut))
                return false;
            if (!BallisticExtrapolator.TryPropagate(
                    segment, gravParameter, ut, out Vector3d zupPos, out Vector3d zupVel))
                return false;
            worldOffset = TrajectoryMath.SwizzleZupBodyRelativeToWorld(zupPos);
            worldVelocity = TrajectoryMath.SwizzleZupBodyRelativeToWorld(zupVel);
            return IsFiniteTailVector(worldOffset) && IsFiniteTailVector(worldVelocity);
        }

        /// <summary>
        /// World attitude a tail segment's stored orbital-frame rotation decodes to at a state,
        /// the same decode the flight positioner's orbital-frame branch uses. False when the
        /// segment carries no rotation (the caller keeps the last recorded attitude). Pure.
        /// </summary>
        internal static bool TryResolveTailWorldRotation(
            OrbitSegment segment, Vector3d worldOffset, Vector3d worldVelocity,
            out Quaternion worldRotation)
        {
            worldRotation = Quaternion.identity;
            if (!BallisticExtrapolator.HasOrbitalFrameRotation(segment.orbitalFrameRotation))
                return false;
            if (worldOffset.sqrMagnitude <= 1e-6 || worldVelocity.sqrMagnitude <= 1e-12)
                return false;
            Quaternion rot = BallisticExtrapolator.ResolveWorldRotation(
                segment.orbitalFrameRotation, worldOffset, worldVelocity);
            if (rot.x == 0f && rot.y == 0f && rot.z == 0f && rot.w == 0f)
                return false;
            worldRotation = rot;
            return true;
        }

        /// <summary>
        /// Live: world position of the ghost on its tail segment around <paramref name="body"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryGetTailWorldPosition(
            OrbitSegment segment, CelestialBody body, double ut, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (body == null)
                return false;
            if (!TryComputeTailWorldOffset(
                    segment, body.gravParameter, ut, out Vector3d offset, out _))
                return false;
            worldPos = body.position + offset;
            return IsFiniteTailVector(worldPos);
        }

        private static bool IsFiniteTailVector(Vector3d v)
        {
            return !double.IsNaN(v.x) && !double.IsInfinity(v.x)
                && !double.IsNaN(v.y) && !double.IsInfinity(v.y)
                && !double.IsNaN(v.z) && !double.IsInfinity(v.z);
        }
    }
}
