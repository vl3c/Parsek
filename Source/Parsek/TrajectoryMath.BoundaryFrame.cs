using System;
using System.Collections.Generic;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek
{
    public static partial class TrajectoryMath
    {
        /// <summary>
        /// The body-fixed frame of a given INSTANT, reached from recorded data alone. A seed anchor
        /// epsilon compares two recorded positions of one boundary (a Kepler conic and a
        /// body-fixed sample); comparing them here, both at the boundary's own UT, gives an
        /// answer that does not depend on when it is computed. Reading either side through live
        /// state (<c>Orbit.getPositionAtUT</c> uses the current <c>Planetarium.Zup</c>,
        /// <c>GetWorldSurfacePosition</c> the body's current rotation) mixes two instants and
        /// carries the body's rotation since the boundary into the difference (todo
        /// REFLY-ANCHOR-EPSILON-1300KM).
        ///
        /// <para>
        /// The frame relation, stock 1.12.5 (decompiled <c>CelestialBody.CBUpdate</c>): a
        /// segment's stored elements put its LAN from <c>Planetarium.right</c>, and in that frame
        /// (z = the body's polar axis) a point's longitude is
        /// <c>body-fixed longitude + rotationAngle(ut)</c> with
        /// <c>rotationAngle = initialRotation + 360 * ut / rotationPeriod</c> for a rotating body
        /// (a tidally locked body's <c>rotationPeriod</c> is already its orbital period). The
        /// <c>cs-career-mun4</c> fixture's Kerbin and Mun bubble boundaries agree with it to
        /// metres (<c>AnchorEpsilonRebuildClockTests</c>). Pure: no Unity or KSP call.
        /// </para>
        /// </summary>
        internal static class BoundaryFrame
        {
            /// <summary>The body constants the frame relation needs.</summary>
            internal struct BodyRotation
            {
                public string BodyName;
                public double Radius;
                public double GravParameter;
                public double RotationPeriod;
                public double InitialRotationDeg;
                public bool Rotates;
                /// <summary>The fixed <c>rotationAngle</c> of a body that does not rotate.</summary>
                public double FixedRotationAngleDeg;
            }

            /// <summary>
            /// True when the constants describe a body the relation can use: finite positive
            /// radius and gravitational parameter, and a finite non-zero period when it rotates.
            /// </summary>
            internal static bool IsUsable(in BodyRotation body)
            {
                if (!IsFinite(body.Radius) || body.Radius <= 0.0) return false;
                if (!IsFinite(body.GravParameter) || body.GravParameter <= 0.0) return false;
                if (body.Rotates)
                {
                    if (!IsFinite(body.RotationPeriod) || Math.Abs(body.RotationPeriod) <= double.Epsilon)
                        return false;
                    if (!IsFinite(body.InitialRotationDeg)) return false;
                }
                else if (!IsFinite(body.FixedRotationAngleDeg))
                {
                    return false;
                }
                return true;
            }

            /// <summary>Stock's <c>rotationAngle</c> at <paramref name="ut"/>, in degrees (unwrapped).</summary>
            internal static double RotationAngleDegAt(in BodyRotation body, double ut)
            {
                return body.Rotates
                    ? body.InitialRotationDeg + (360.0 * ut) / body.RotationPeriod
                    : body.FixedRotationAngleDeg;
            }

            /// <summary>
            /// A body-relative position in the stored element frame at <paramref name="ut"/>
            /// expressed as body-fixed latitude / longitude (degrees, longitude wrapped to
            /// (-180, 180]) and altitude above <see cref="BodyRotation.Radius"/>.
            /// </summary>
            internal static bool TryStoredFrameToBodyFixed(
                Vector3d relativePosition, in BodyRotation body, double ut,
                out double latDeg, out double lonDeg, out double altM)
            {
                latDeg = lonDeg = altM = double.NaN;
                if (!IsUsable(body) || !IsFinite(ut)) return false;
                double r = Math.Sqrt(relativePosition.x * relativePosition.x
                    + relativePosition.y * relativePosition.y
                    + relativePosition.z * relativePosition.z);
                if (!IsFinite(r) || r <= 0.0) return false;

                double sinLat = relativePosition.z / r;
                if (sinLat > 1.0) sinLat = 1.0;
                else if (sinLat < -1.0) sinLat = -1.0;
                latDeg = Math.Asin(sinLat) * (180.0 / Math.PI);
                double inertialLonDeg = Math.Atan2(relativePosition.y, relativePosition.x) * (180.0 / Math.PI);
                lonDeg = WrapLongitudeDeg(inertialLonDeg - RotationAngleDegAt(body, ut));
                altM = r - body.Radius;
                return IsFinite(latDeg) && IsFinite(lonDeg) && IsFinite(altM);
            }

            /// <summary>
            /// The Kepler position of <paramref name="checkpoints"/> at <paramref name="ut"/>
            /// (segment pick: <see cref="TryPickOrbitSegmentForBoundaryUT"/>) in the body-fixed
            /// frame of <paramref name="ut"/>. <paramref name="bodies"/> resolves the segment's
            /// body; false on a miss, unusable constants, or a non-finite state.
            /// </summary>
            internal static bool TryOrbitSegmentBodyFixedAtUT(
                List<OrbitSegment> checkpoints, double ut,
                Func<string, BodyRotation?> bodies,
                out string bodyName, out double latDeg, out double lonDeg, out double altM)
            {
                bodyName = null;
                latDeg = lonDeg = altM = double.NaN;
                if (bodies == null) return false;
                if (!TryPickOrbitSegmentForBoundaryUT(checkpoints, ut, out OrbitSegment seg))
                    return false;
                BodyRotation? maybeBody = bodies(seg.bodyName);
                if (!maybeBody.HasValue || !IsUsable(maybeBody.Value)) return false;
                BodyRotation body = maybeBody.Value;
                if (!TwoBodyOrbit.TryCreateFromSegmentStoredFrame(seg, body.GravParameter, out TwoBodyOrbit orbit))
                    return false;
                Vector3d rel = orbit.GetPositionAtUT(ut);
                if (!TryStoredFrameToBodyFixed(rel, body, ut, out latDeg, out lonDeg, out altM))
                    return false;
                bodyName = seg.bodyName;
                return true;
            }

            /// <summary>
            /// A conic's state as two body-fixed points: the position, and the point
            /// <see cref="Step"/> metres further along the velocity, both lat / lon / alt;
            /// <see cref="Speed"/> is the velocity's magnitude. A rigid placement maps the pair to
            /// a world position and, as the placed difference over <see cref="Step"/> times
            /// <see cref="Speed"/>, the world velocity.
            /// </summary>
            internal struct BodyFixedState
            {
                public double Lat, Lon, Alt;
                public double AheadLat, AheadLon, AheadAlt;
                public double Speed, Step;
            }

            /// <summary>
            /// <paramref name="seg"/>'s conic at <paramref name="stateUT"/> (stored element frame,
            /// <paramref name="body"/>'s gravitational parameter), lowered to the body-fixed frame
            /// of <paramref name="lowerUT"/> (<see cref="TryStoredFrameToBodyFixed"/>). The two UTs
            /// differ only where a state at one instant has to be seen in the frame of another
            /// (a spin propagated from the segment's start). The ahead point is one orbital radius
            /// along the velocity; with zero speed it is the position. False on a non-propagatable
            /// segment, unusable constants or a non-finite result.
            /// </summary>
            internal static bool TryOrbitSegmentStateBodyFixed(
                OrbitSegment seg, in BodyRotation body, double stateUT, double lowerUT,
                out BodyFixedState state)
            {
                state = default;
                if (!IsUsable(body) || !IsFinite(stateUT)) return false;
                if (!TwoBodyOrbit.TryCreateFromSegmentStoredFrame(seg, body.GravParameter, out TwoBodyOrbit orbit))
                    return false;
                orbit.GetStateAtUT(stateUT, out Vector3d position, out Vector3d velocity);
                if (!TryStoredFrameToBodyFixed(position, body, lowerUT, out state.Lat, out state.Lon, out state.Alt))
                    return false;
                double speed = Math.Sqrt(velocity.x * velocity.x + velocity.y * velocity.y + velocity.z * velocity.z);
                if (!IsFinite(speed)) return false;
                double radius = Math.Sqrt(position.x * position.x + position.y * position.y + position.z * position.z);
                state.Speed = speed;
                state.Step = Math.Max(1.0, radius);
                if (speed <= 0.0)
                {
                    state.AheadLat = state.Lat;
                    state.AheadLon = state.Lon;
                    state.AheadAlt = state.Alt;
                    return true;
                }
                Vector3d ahead = position + velocity * (state.Step / speed);
                return TryStoredFrameToBodyFixed(ahead, body, lowerUT,
                    out state.AheadLat, out state.AheadLon, out state.AheadAlt);
            }

            /// <summary>
            /// A body's own conic around its parent, for carrying a position across an SOI
            /// boundary: stock's <c>Orbit</c> elements of the body (same conventions as a
            /// recorded <see cref="OrbitSegment"/>: degrees, LAN from <c>Planetarium.right</c>,
            /// mean anomaly in radians) and the parent's name and gravitational parameter. A
            /// root body (the Sun) has a null <see cref="ParentName"/>.
            /// </summary>
            internal struct BodyOrbitLink
            {
                public string ParentName;
                public double ParentGravParameter;
                public OrbitSegment Elements;
            }

            // A deeper chain than any stock or modded system; stops a malformed parent loop.
            private const int MaxBodyChainDepth = 16;

            /// <summary>
            /// The stored-frame position of <paramref name="bodyName"/> relative to its root body
            /// at <paramref name="ut"/>: the sum of each body's own conic around its parent up
            /// the parent chain. Every recorded and stock conic shares the stored element frame's
            /// axes, so these positions add. False when a body in the chain does not resolve, its
            /// conic does not propagate, or the chain is deeper than any real system.
            /// </summary>
            internal static bool TryStoredFramePositionFromRoot(
                string bodyName, double ut, Func<string, BodyOrbitLink?> links,
                out Vector3d position, out string rootName)
            {
                position = Vector3d.zero;
                rootName = null;
                if (links == null || string.IsNullOrEmpty(bodyName) || !IsFinite(ut)) return false;
                string current = bodyName;
                for (int depth = 0; depth < MaxBodyChainDepth; depth++)
                {
                    BodyOrbitLink? maybe = links(current);
                    if (!maybe.HasValue) return false;
                    BodyOrbitLink link = maybe.Value;
                    if (string.IsNullOrEmpty(link.ParentName))
                    {
                        rootName = current;
                        return true;
                    }
                    if (!TwoBodyOrbit.TryCreateFromSegmentStoredFrame(link.Elements, link.ParentGravParameter, out TwoBodyOrbit orbit))
                        return false;
                    Vector3d rel = orbit.GetPositionAtUT(ut);
                    if (!IsFinite(rel.x) || !IsFinite(rel.y) || !IsFinite(rel.z)) return false;
                    position += rel;
                    current = link.ParentName;
                }
                return false;
            }

            /// <summary>
            /// The Kepler position of <paramref name="checkpoints"/> at <paramref name="ut"/> in
            /// the body-fixed frame of <paramref name="frameBodyName"/> at <paramref name="ut"/>,
            /// which may be another body than the segment's (an SOI boundary: the conic is around
            /// the body on one side, the physics samples around the body on the other). The conic's
            /// body-relative position is moved to <paramref name="frameBodyName"/> by the two
            /// bodies' own conics at the same <paramref name="ut"/>
            /// (<see cref="TryStoredFramePositionFromRoot"/>), then lowered with that body's
            /// rotation at <paramref name="ut"/>. Same body: identical to
            /// <see cref="TryOrbitSegmentBodyFixedAtUT"/>. False on any miss, or when the two
            /// bodies have no common root.
            /// </summary>
            internal static bool TryOrbitSegmentInBodyFixedOfAtUT(
                List<OrbitSegment> checkpoints, double ut, string frameBodyName,
                Func<string, BodyRotation?> bodies, Func<string, BodyOrbitLink?> links,
                out string segmentBodyName, out double latDeg, out double lonDeg, out double altM)
            {
                segmentBodyName = null;
                latDeg = lonDeg = altM = double.NaN;
                if (bodies == null || string.IsNullOrEmpty(frameBodyName)) return false;
                if (!TryPickOrbitSegmentForBoundaryUT(checkpoints, ut, out OrbitSegment seg))
                    return false;
                BodyRotation? maybeSegBody = bodies(seg.bodyName);
                BodyRotation? maybeFrameBody = bodies(frameBodyName);
                if (!maybeSegBody.HasValue || !IsUsable(maybeSegBody.Value)) return false;
                if (!maybeFrameBody.HasValue || !IsUsable(maybeFrameBody.Value)) return false;
                if (!TwoBodyOrbit.TryCreateFromSegmentStoredFrame(seg, maybeSegBody.Value.GravParameter, out TwoBodyOrbit orbit))
                    return false;
                Vector3d rel = orbit.GetPositionAtUT(ut);
                if (!string.Equals(seg.bodyName, frameBodyName, StringComparison.Ordinal))
                {
                    if (!TryStoredFramePositionFromRoot(seg.bodyName, ut, links, out Vector3d segBodyPos, out string segRoot)
                        || !TryStoredFramePositionFromRoot(frameBodyName, ut, links, out Vector3d frameBodyPos, out string frameRoot)
                        || !string.Equals(segRoot, frameRoot, StringComparison.Ordinal))
                        return false;
                    rel = rel + segBodyPos - frameBodyPos;
                }
                if (!TryStoredFrameToBodyFixed(rel, maybeFrameBody.Value, ut, out latDeg, out lonDeg, out altM))
                    return false;
                segmentBodyName = seg.bodyName;
                return true;
            }

            /// <summary>
            /// The body-fixed longitude of a smoothing-spline sample evaluated at
            /// <paramref name="ut"/>: tag 0 is already body-fixed; tag 1 stores
            /// <c>lon + 360 * ut / rotationPeriod</c> (<see cref="FrameTransform.LiftToInertial"/>,
            /// which leaves the longitude unchanged for a degenerate period, as this does).
            /// False for any other tag.
            /// </summary>
            internal static bool TrySplineLongitudeToBodyFixed(
                byte frameTag, double splineLonDeg, double rotationPeriod, double ut, out double lonDeg)
            {
                lonDeg = double.NaN;
                if (!IsFinite(splineLonDeg)) return false;
                switch (frameTag)
                {
                    case 0:
                        lonDeg = WrapLongitudeDeg(splineLonDeg);
                        return true;
                    case 1:
                        if (!IsFinite(rotationPeriod) || Math.Abs(rotationPeriod) <= double.Epsilon
                            || !IsFinite(ut))
                        {
                            lonDeg = WrapLongitudeDeg(splineLonDeg);
                            return true;
                        }
                        lonDeg = WrapLongitudeDeg(splineLonDeg - (ut * 360.0) / rotationPeriod);
                        return true;
                    default:
                        return false;
                }
            }

            /// <summary>
            /// Body-fixed Cartesian position (metres, z = polar axis) of a latitude / longitude /
            /// altitude. The distance between two such points is the epsilon magnitude a
            /// boundary carries, independent of how the body is oriented in the world.
            /// </summary>
            internal static Vector3d BodyFixedCartesian(double latDeg, double lonDeg, double altM, double radius)
            {
                double r = radius + altM;
                double lat = latDeg * (Math.PI / 180.0);
                double lon = lonDeg * (Math.PI / 180.0);
                return new Vector3d(
                    r * Math.Cos(lat) * Math.Cos(lon),
                    r * Math.Cos(lat) * Math.Sin(lon),
                    r * Math.Sin(lat));
            }

            internal static double WrapLongitudeDeg(double lonDeg)
            {
                if (!IsFinite(lonDeg)) return lonDeg;
                double wrapped = lonDeg % 360.0;
                if (wrapped > 180.0) wrapped -= 360.0;
                else if (wrapped <= -180.0) wrapped += 360.0;
                return wrapped;
            }

            private static bool IsFinite(double v)
            {
                return !double.IsNaN(v) && !double.IsInfinity(v);
            }
        }
    }
}
