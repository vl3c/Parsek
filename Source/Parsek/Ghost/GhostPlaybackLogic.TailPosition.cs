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
            return TryFindTailSegment(traj, ut, out _)
                || TryFindTailSlide(traj, ut, out _, out _);
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

        // ---- Owner ruling L5: the slide across a gap the bridge cap does not cover ----
        //
        // When the predicted tail starts more than PredictedOrbitTailBridgeMaxGapSeconds after
        // the last recorded point, the ghost slides from that point to the start of the
        // predicted path over the gap. The predicted conic is never evaluated inside the gap
        // (it may be a later, post-burn orbit), only at its own start. The slide moves along
        // the great circle between the two body-relative directions with a monotone cubic on
        // the angle and a separate monotone cubic on the radius, both seeded from the
        // endpoint velocities: it matches both endpoints, keeps the speed where the inputs
        // agree, never overshoots either end, and never dips below the lower endpoint's
        // radius. The slide carries no velocity for the FX readers (no flames from a stale
        // velocity).

        /// <summary>
        /// The last recorded point (and the one before it, for the starting velocity) a slide
        /// starts from. False when the last point cannot anchor one: on another body than the
        /// segment, or inside a Relative section (its lat/lon/alt are anchor-local metres).
        /// The previous point is used only on the same body, outside a Relative section and
        /// within <see cref="GhostSurfaceVelocity.MaxBracketSeconds"/>. Pure.
        /// </summary>
        internal static bool TryGetTailSlideAnchorPoints(
            IPlaybackTrajectory traj, OrbitSegment segment,
            out TrajectoryPoint last, out TrajectoryPoint previous, out bool hasPrevious)
        {
            last = default(TrajectoryPoint);
            previous = default(TrajectoryPoint);
            hasPrevious = false;
            if (traj?.Points == null || traj.Points.Count == 0)
                return false;
            last = traj.Points[traj.Points.Count - 1];
            if (string.IsNullOrEmpty(last.bodyName)
                || !string.Equals(last.bodyName, segment.bodyName, System.StringComparison.Ordinal))
                return false;
            if (IsInRelativeSection(traj, last.ut))
                return false;
            if (!IsFiniteTailVector(new Vector3d(last.latitude, last.longitude, last.altitude)))
                return false;

            if (traj.Points.Count >= 2)
            {
                TrajectoryPoint prev = traj.Points[traj.Points.Count - 2];
                double dt = last.ut - prev.ut;
                if (dt >= GhostSurfaceVelocity.MinBracketSeconds
                    && dt <= GhostSurfaceVelocity.MaxBracketSeconds
                    && string.Equals(prev.bodyName, last.bodyName, System.StringComparison.Ordinal)
                    && !IsInRelativeSection(traj, prev.ut)
                    && IsFiniteTailVector(new Vector3d(prev.latitude, prev.longitude, prev.altitude)))
                {
                    previous = prev;
                    hasPrevious = true;
                }
            }
            return true;
        }

        private static bool IsInRelativeSection(IPlaybackTrajectory traj, double ut)
        {
            if (traj.TrackSections == null || traj.TrackSections.Count == 0)
                return false;
            int idx = TrajectoryMath.FindTrackSectionForUT(traj.TrackSections, ut);
            return idx >= 0 && traj.TrackSections[idx].referenceFrame == ReferenceFrame.Relative;
        }

        /// <summary>
        /// The predicted-tail slide at <paramref name="ut"/>, when one applies (see
        /// <see cref="GhostPlaybackEngine.TryFindPredictedOrbitTailSlide"/>), gated to the
        /// recording's span. Pure.
        /// </summary>
        internal static bool TryFindTailSlide(
            IPlaybackTrajectory traj, double ut, out OrbitSegment segment, out double lastPointUT)
        {
            segment = default(OrbitSegment);
            lastPointUT = double.NaN;
            if (traj == null || double.IsNaN(ut) || double.IsInfinity(ut))
                return false;
            if (ut > traj.EndUT + 1e-6)
                return false;
            return GhostPlaybackEngine.TryFindPredictedOrbitTailSlide(
                traj, ut, out segment, out _, out lastPointUT);
        }

        /// <summary>Slide progress in [0, 1] across the gap. Pure.</summary>
        internal static double ResolveTailSlideFraction(double lastPointUT, double startUT, double ut)
        {
            double duration = startUT - lastPointUT;
            if (!(duration > 1e-9))
                return 1.0;
            double s = (ut - lastPointUT) / duration;
            if (double.IsNaN(s) || s <= 0.0)
                return 0.0;
            return s >= 1.0 ? 1.0 : s;
        }

        /// <summary>Smoothstep weight for blending attitudes across the slide. Pure.</summary>
        internal static double SmoothStep01(double s)
        {
            if (!(s > 0.0)) return 0.0;
            if (s >= 1.0) return 1.0;
            return s * s * (3.0 - 2.0 * s);
        }

        /// <summary>
        /// Body-relative offset of the slide at fraction <paramref name="s"/>: from
        /// <paramref name="r0"/> (moving at <paramref name="v0"/> when known) to
        /// <paramref name="r1"/> (moving at <paramref name="v1"/> when known) over
        /// <paramref name="durationSeconds"/>. False on degenerate input (non-finite, a radius
        /// under 1 m, a non-positive duration, or opposite directions with no velocity to pick
        /// the plane). Pure, allocation-free.
        /// </summary>
        internal static bool TryComputeTailSlideOffset(
            Vector3d r0, Vector3d v0, bool hasV0,
            Vector3d r1, Vector3d v1, bool hasV1,
            double durationSeconds, double s, out Vector3d offset)
        {
            offset = Vector3d.zero;
            if (!IsFiniteTailVector(r0) || !IsFiniteTailVector(r1))
                return false;
            if (!(durationSeconds > 1e-9) || double.IsInfinity(durationSeconds) || double.IsNaN(s))
                return false;
            hasV0 = hasV0 && IsFiniteTailVector(v0);
            hasV1 = hasV1 && IsFiniteTailVector(v1);
            double m0 = r0.magnitude;
            double m1 = r1.magnitude;
            if (!(m0 > 1.0) || !(m1 > 1.0))
                return false;
            if (s <= 0.0) { offset = r0; return true; }
            if (s >= 1.0) { offset = r1; return true; }

            Vector3d u0 = r0 / m0;
            Vector3d u1 = r1 / m1;
            double cos = Vector3d.Dot(u0, u1);
            if (cos > 1.0) cos = 1.0;
            else if (cos < -1.0) cos = -1.0;
            double theta = System.Math.Acos(cos);

            Vector3d w = u1 - u0 * cos;
            double wMag = w.magnitude;
            Vector3d t;
            if (wMag > 1e-9)
            {
                t = w / wMag;
            }
            else
            {
                Vector3d p = hasV0 ? v0 - u0 * Vector3d.Dot(v0, u0) : Vector3d.zero;
                double pMag = p.magnitude;
                if (pMag > 1e-9)
                    t = p / pMag;
                else if (theta < 1e-6)
                    t = Vector3d.zero;
                else
                    return false;
            }

            double w0 = hasV0 ? Vector3d.Dot(v0, t) / m0 : double.NaN;
            Vector3d t1 = u0 * (-System.Math.Sin(theta)) + t * cos;
            double w1 = hasV1 ? Vector3d.Dot(v1, t1) / m1 : double.NaN;
            if (theta > 1e-6 && hasV0 && hasV1 && w0 < 0.0 && w1 < 0.0)
            {
                // Both ends move away from the short arc: the ghost went the long way round.
                t = -t;
                theta = 2.0 * System.Math.PI - theta;
                w0 = -w0;
                w1 = -w1;
            }

            double phi = MonotoneHermite(0.0, theta, w0 * durationSeconds, w1 * durationSeconds, s);
            double rd0 = hasV0 ? Vector3d.Dot(v0, u0) * durationSeconds : double.NaN;
            double rd1 = hasV1 ? Vector3d.Dot(v1, u1) * durationSeconds : double.NaN;
            double radius = MonotoneHermite(m0, m1, rd0, rd1, s);

            Vector3d dir = u0 * System.Math.Cos(phi) + t * System.Math.Sin(phi);
            offset = dir * radius;
            return IsFiniteTailVector(offset);
        }

        /// <summary>
        /// Cubic Hermite from <paramref name="a"/> to <paramref name="b"/> with endpoint slopes
        /// (per unit <paramref name="s"/>) limited so the curve stays monotone between the
        /// ends (Fritsch-Carlson): a slope against the secant becomes 0, one over three times
        /// the secant is capped there, and a missing (NaN) slope takes the secant. Pure.
        /// </summary>
        internal static double MonotoneHermite(double a, double b, double slopeA, double slopeB, double s)
        {
            double delta = b - a;
            slopeA = LimitMonotoneSlope(slopeA, delta);
            slopeB = LimitMonotoneSlope(slopeB, delta);
            double s2 = s * s;
            double s3 = s2 * s;
            double h00 = 2.0 * s3 - 3.0 * s2 + 1.0;
            double h10 = s3 - 2.0 * s2 + s;
            double h01 = -2.0 * s3 + 3.0 * s2;
            double h11 = s3 - s2;
            return h00 * a + h10 * slopeA + h01 * b + h11 * slopeB;
        }

        private static double LimitMonotoneSlope(double slope, double delta)
        {
            if (System.Math.Abs(delta) <= 1e-12)
                return 0.0;
            if (double.IsNaN(slope) || double.IsInfinity(slope))
                return delta;
            if (slope * delta <= 0.0)
                return 0.0;
            double cap = 3.0 * delta;
            return System.Math.Abs(slope) > System.Math.Abs(cap) ? cap : slope;
        }

        /// <summary>
        /// World position of the slide: <paramref name="lastWorld"/> (and
        /// <paramref name="previousWorld"/> <paramref name="previousDt"/> seconds earlier, for
        /// the starting velocity, when <paramref name="hasPrevious"/>) around a body at
        /// <paramref name="bodyPos"/>, sliding to the predicted path's start
        /// <paramref name="startOffset"/> / <paramref name="startVelocity"/> (body-relative,
        /// world axes). Pure.
        /// </summary>
        internal static bool TryComputeTailSlideWorldPosition(
            Vector3d bodyPos,
            Vector3d lastWorld, Vector3d previousWorld, bool hasPrevious, double previousDt,
            Vector3d startOffset, Vector3d startVelocity,
            double lastPointUT, double startUT, double ut,
            out Vector3d worldPos, out double fraction)
        {
            worldPos = Vector3d.zero;
            fraction = ResolveTailSlideFraction(lastPointUT, startUT, ut);
            Vector3d v0 = Vector3d.zero;
            bool hasV0 = hasPrevious && previousDt > 1e-9
                && IsFiniteTailVector(previousWorld);
            if (hasV0)
                v0 = (lastWorld - previousWorld) / previousDt;
            if (!TryComputeTailSlideOffset(
                    lastWorld - bodyPos, v0, hasV0,
                    startOffset, startVelocity, true,
                    startUT - lastPointUT, fraction, out Vector3d offset))
                return false;
            worldPos = bodyPos + offset;
            return IsFiniteTailVector(worldPos);
        }

        /// <summary>
        /// Live: world position of the slide around <paramref name="body"/>, for the markers
        /// (flight map, Tracking Station). <paramref name="bodyFixedShiftSeconds"/> is the
        /// marker's phasing derotation of the recorded points (identity at 0).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryGetTailSlideWorldPosition(
            IPlaybackTrajectory traj, OrbitSegment segment, CelestialBody body,
            double lastPointUT, double ut, double bodyFixedShiftSeconds, out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            if (body == null)
                return false;
            if (!TryGetTailSlideAnchorPoints(traj, segment,
                    out TrajectoryPoint last, out TrajectoryPoint prev, out bool hasPrev))
                return false;
            if (!TryComputeTailWorldOffset(segment, body.gravParameter, segment.startUT,
                    out Vector3d startOffset, out Vector3d startVelocity))
                return false;
            Vector3d lastWorld = body.GetWorldSurfacePosition(
                last.latitude,
                TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(last.longitude, bodyFixedShiftSeconds, body),
                last.altitude);
            Vector3d prevWorld = hasPrev
                ? body.GetWorldSurfacePosition(
                    prev.latitude,
                    TrajectoryMath.FrameTransform.ShiftLongitudeDegrees(prev.longitude, bodyFixedShiftSeconds, body),
                    prev.altitude)
                : Vector3d.zero;
            return TryComputeTailSlideWorldPosition(
                body.position, lastWorld, prevWorld, hasPrev, last.ut - prev.ut,
                startOffset, startVelocity, lastPointUT, segment.startUT, ut,
                out worldPos, out _);
        }

        private static bool IsFiniteTailVector(Vector3d v)
        {
            return !double.IsNaN(v.x) && !double.IsInfinity(v.x)
                && !double.IsNaN(v.y) && !double.IsInfinity(v.y)
                && !double.IsNaN(v.z) && !double.IsInfinity(v.z);
        }
    }
}
