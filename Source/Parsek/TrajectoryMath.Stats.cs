using System;
using System.Collections.Generic;

namespace Parsek
{
    public static partial class TrajectoryMath
    {
        /// <summary>
        /// Computes aggregate statistics for a recording.
        /// Pure function — uses bodyLookup callback for orbit segment calculations.
        /// bodyLookup("Kerbin") should return [radius, gravParameter] or null.
        /// </summary>
        internal static RecordingStats ComputeStats(
            Recording rec,
            System.Func<string, double[]> bodyLookup = null)
        {
            var stats = new RecordingStats();
            stats.pointCount = rec.Points.Count;
            stats.orbitSegmentCount = rec.OrbitSegments.Count;
            stats.partEventCount = rec.PartEvents.Count;

            if (rec.Points.Count == 0)
            {
                ParsekLog.VerboseRateLimited("TrajectoryMath", "compute-stats-empty",
                    "ComputeStats called with empty trajectory", 5.0);
                return stats;
            }

            var bodyCounts = new Dictionary<string, int>();
            double lat0 = rec.Points[0].latitude;
            double lon0 = rec.Points[0].longitude;
            string body0 = rec.Points[0].bodyName ?? "Kerbin";
            ReferenceFrame firstPointFrame =
                ResolvePointFrameForStats(rec.TrackSections, rec.Points[0].ut);

            ApplyTrackSectionAltitudeMetadata(rec.TrackSections, ref stats);

            int skippedFrameChangePairs = 0;
            for (int i = 0; i < rec.Points.Count; i++)
            {
                var pt = rec.Points[i];

                if (pt.altitude > stats.maxAltitude)
                    stats.maxAltitude = pt.altitude;

                float speed = pt.velocity.magnitude;
                if (speed > stats.maxSpeed)
                    stats.maxSpeed = speed;

                string body = pt.bodyName ?? "Kerbin";
                int count;
                bodyCounts.TryGetValue(body, out count);
                bodyCounts[body] = count + 1;

                if (bodyLookup != null)
                {
                    double[] bodyData = bodyLookup(body);
                    if (bodyData != null)
                    {
                        double bodyRadius = bodyData[0];

                        // Distance from previous point (same body only).
                        // Skip when both points fall inside an orbit segment
                        // to avoid double-counting distance already covered
                        // by the segment's mean-speed calculation.
                        if (i > 0 && (rec.Points[i - 1].bodyName ?? "Kerbin") == body)
                        {
                            var prev = rec.Points[i - 1];
                            double midUT = (prev.ut + pt.ut) * 0.5;
                            bool inOrbitSegment = FindOrbitSegment(rec.OrbitSegments, midUT) != null;
                            if (!inOrbitSegment)
                            {
                                ReferenceFrame frame;
                                if (TryResolvePairTravelFrame(
                                        rec.TrackSections, prev.ut, pt.ut, out frame))
                                {
                                    stats.distanceTravelled += ComputePairwiseTravelDistance(
                                        prev, pt, frame, bodyRadius);
                                }
                                else
                                {
                                    skippedFrameChangePairs++;
                                }
                            }
                        }

                        // Max range from first point (same body only)
                        if (body == body0)
                        {
                            ReferenceFrame pointFrame =
                                ResolvePointFrameForStats(rec.TrackSections, pt.ut);
                            double range = ComputePointRangeFromStart(
                                rec.Points[0], pt, firstPointFrame, pointFrame, bodyRadius);
                            if (range > stats.maxRange)
                                stats.maxRange = range;
                        }
                    }
                }
            }

            AccumulateOrbitSegmentStats(rec.OrbitSegments, bodyLookup, ref stats);
            stats.primaryBody = DeterminePrimaryBody(bodyCounts);

            ParsekLog.Verbose("TrajectoryMath",
                $"ComputeStats complete: points={stats.pointCount} segments={stats.orbitSegmentCount} " +
                $"events={stats.partEventCount} maxAlt={stats.maxAltitude:F0} maxSpeed={stats.maxSpeed:F1} " +
                $"dist={stats.distanceTravelled:F0} range={stats.maxRange:F0} body={stats.primaryBody} " +
                $"skippedFrameChangePairs={skippedFrameChangePairs}");

            return stats;
        }

        /// <summary>
        /// PURE: resolves the reference frame a consecutive point pair is measured in, or
        /// returns false when the pair is not a travel step and contributes no distance.
        ///
        /// <para>Each endpoint resolves its own section through
        /// <see cref="ResolvePointFrameForStats"/>. A Relative point stores
        /// anchor-local metres in latitude/longitude/altitude while any other frame stores
        /// body-fixed degrees, so a pair whose endpoints disagree has no common unit:
        /// measuring the metre offsets as degrees turned a parent-anchored debris
        /// Relative-to-Absolute hand-off (a 0.06 s section gap, where the old mid-UT lookup
        /// found no section and defaulted to Absolute) into a ~1000 km step. A pair with no
        /// elapsed time is a seam duplicate, not travel, and is skipped for the same reason
        /// (a duplicated boundary sample can carry either frame's units). A point outside
        /// every section reads as Absolute, as before.</para>
        /// </summary>
        internal static bool TryResolvePairTravelFrame(
            List<TrackSection> sections, double prevUT, double curUT, out ReferenceFrame frame)
        {
            frame = ReferenceFrame.Absolute;
            if (!(curUT > prevUT))
                return false;
            ReferenceFrame prevFrame = ResolvePointFrameForStats(sections, prevUT);
            ReferenceFrame curFrame = ResolvePointFrameForStats(sections, curUT);
            if ((prevFrame == ReferenceFrame.Relative) != (curFrame == ReferenceFrame.Relative))
                return false;
            frame = curFrame;
            return true;
        }

        /// <summary>
        /// PURE: the reference frame a single point's latitude/longitude/altitude are stored
        /// in, for the stats distance and range. Uses the same boundary-tolerant lookup as the
        /// relative-anchor resolver (<see cref="FindTrackSectionForUTWithBoundaryEpsilon"/>),
        /// so a section's last sample at or a hair past its exclusive endUT (a gap follows)
        /// still reads its own section's frame. A UT shared by two contiguous sections resolves
        /// through the strict lookup to the later section, as playback's section dispatch does;
        /// within the tolerance of both sides of a gap the start match wins, as in the anchor
        /// resolver. A point outside every section reads as Absolute.
        /// </summary>
        internal static ReferenceFrame ResolvePointFrameForStats(List<TrackSection> sections, double ut)
        {
            int idx = FindTrackSectionForUTWithBoundaryEpsilon(sections, ut);
            return idx >= 0 ? sections[idx].referenceFrame : ReferenceFrame.Absolute;
        }

        /// <summary>
        /// Computes the distance contributed by a single consecutive point pair, dispatching
        /// on reference frame: Relative sections store anchor-local metre offsets in
        /// latitude/longitude/altitude (Euclidean dx/dy/dz delta), while non-Relative sections
        /// store body-fixed lat/lon/alt (haversine surface distance plus altitude delta).
        /// </summary>
        internal static double ComputePairwiseTravelDistance(
            in TrajectoryPoint prev,
            in TrajectoryPoint cur,
            ReferenceFrame frame,
            double bodyRadius)
        {
            if (frame == ReferenceFrame.Relative)
            {
                double dx = cur.latitude - prev.latitude;
                double dy = cur.longitude - prev.longitude;
                double dz = cur.altitude - prev.altitude;
                return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            double avgAlt = (prev.altitude + cur.altitude) * 0.5;
            double surfaceDist = HaversineDistance(
                prev.latitude, prev.longitude,
                cur.latitude, cur.longitude,
                bodyRadius + avgAlt);
            double altDiff = System.Math.Abs(cur.altitude - prev.altitude);
            return System.Math.Sqrt(surfaceDist * surfaceDist + altDiff * altDiff);
        }

        /// <summary>
        /// Computes the range of a point from the first recorded point, dispatching on the
        /// start-point and current-point reference frames: both Relative uses an anchor-local
        /// Euclidean dx/dy/dz delta; current-Relative-only returns 0.0 (cannot mix frames);
        /// otherwise uses a haversine surface range from the start point.
        /// </summary>
        internal static double ComputePointRangeFromStart(
            in TrajectoryPoint start,
            in TrajectoryPoint cur,
            ReferenceFrame startFrame,
            ReferenceFrame curFrame,
            double bodyRadius)
        {
            if (startFrame == ReferenceFrame.Relative
                && curFrame == ReferenceFrame.Relative)
            {
                double dx = cur.latitude - start.latitude;
                double dy = cur.longitude - start.longitude;
                double dz = cur.altitude - start.altitude;
                return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            if (curFrame == ReferenceFrame.Relative)
            {
                return 0.0;
            }

            double avgAlt = (start.altitude + cur.altitude) * 0.5;
            return HaversineDistance(
                start.latitude, start.longitude,
                cur.latitude, cur.longitude,
                bodyRadius + avgAlt);
        }

        private static void ApplyTrackSectionAltitudeMetadata(
            List<TrackSection> sections,
            ref RecordingStats stats)
        {
            if (sections == null)
                return;

            for (int i = 0; i < sections.Count; i++)
            {
                if (!float.IsNaN(sections[i].maxAltitude)
                    && sections[i].maxAltitude > stats.maxAltitude)
                {
                    stats.maxAltitude = sections[i].maxAltitude;
                }
            }
        }

        /// <summary>
        /// Accumulates orbit segment contributions into recording stats: apoapsis altitude,
        /// max speed (vis-viva, see <see cref="TryComputeSegmentMaxSpeed"/>), and mean-speed
        /// distance for each segment.
        /// </summary>
        internal static void AccumulateOrbitSegmentStats(
            List<OrbitSegment> segments,
            System.Func<string, double[]> bodyLookup,
            ref RecordingStats stats)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (bodyLookup == null) continue;
                double[] bodyData = bodyLookup(seg.bodyName ?? "Kerbin");
                if (bodyData == null) continue;

                double bodyRadius = bodyData[0];
                double gm = bodyData[1];

                // Apoapsis altitude
                double apoRadius = seg.semiMajorAxis * (1.0 + seg.eccentricity);
                double apoAlt = apoRadius - bodyRadius;
                if (apoAlt > stats.maxAltitude)
                    stats.maxAltitude = apoAlt;

                // Max orbital speed via vis-viva at the lowest radius the arc can reach.
                double segMaxSpeed;
                bool haveSegMaxSpeed = TryComputeSegmentMaxSpeed(
                    seg.semiMajorAxis, seg.eccentricity, bodyRadius, gm, out segMaxSpeed);
                if (haveSegMaxSpeed && segMaxSpeed > stats.maxSpeed)
                    stats.maxSpeed = segMaxSpeed;

                // Mean orbital speed * duration. The mean speed over an arc cannot exceed the
                // arc's own max speed. On a real orbit sqrt(gm / sma) is always below the
                // periapsis speed, so the cap binds only on a sub-orbital arc, where the
                // circular speed at sma is far above anything the arc flew.
                if (seg.semiMajorAxis > 0)
                {
                    double meanSpeed = System.Math.Sqrt(gm / seg.semiMajorAxis);
                    if (haveSegMaxSpeed && meanSpeed > segMaxSpeed)
                        meanSpeed = segMaxSpeed;
                    stats.distanceTravelled += meanSpeed * (seg.endUT - seg.startUT);
                }
            }
        }

        /// <summary>
        /// PURE: the highest speed an elliptic orbit segment's arc can reach, by vis-viva at
        /// <c>max(periapsis radius, body radius)</c>. Returns false for a non-elliptic or
        /// degenerate element set, or an ellipse lying wholly inside the body.
        ///
        /// <para>Vis-viva speed falls monotonically with radius, so an arc's top speed is at
        /// its lowest radius. A real orbit's lowest radius is its periapsis and the result is
        /// the plain periapsis speed. A sub-orbital arc (a ballistic fall, the predicted
        /// impact tail of a destroyed vessel) ends where it meets the surface and never
        /// reaches its periapsis, which can sit metres from the body centre: an e=0.99977
        /// debris impact segment had a periapsis radius of 69.7 m, and vis-viva there read
        /// 318 km/s. Clamping the radius at the body's sea-level radius gives the speed at
        /// the surface, an upper bound on what the arc flew. No Kepler solve is involved,
        /// which matters because the near-radial impact orbits this guards are exactly where
        /// an eccentric-anomaly solve stops converging.</para>
        /// </summary>
        internal static bool TryComputeSegmentMaxSpeed(
            double semiMajorAxis, double eccentricity, double bodyRadius, double gm,
            out double maxSpeed)
        {
            maxSpeed = 0.0;
            if (!(semiMajorAxis > 0.0) || double.IsInfinity(semiMajorAxis)) return false;
            if (!(eccentricity >= 0.0) || eccentricity >= 1.0) return false;
            if (!(gm > 0.0) || double.IsInfinity(gm)) return false;

            double periRadius = semiMajorAxis * (1.0 - eccentricity);
            double apoRadius = semiMajorAxis * (1.0 + eccentricity);
            if (!(periRadius > 0.0)) return false;

            double lowestRadius = periRadius;
            if (bodyRadius > periRadius && !double.IsInfinity(bodyRadius))
            {
                // The whole ellipse lies inside the body: no reachable point exists.
                if (bodyRadius > apoRadius) return false;
                lowestRadius = bodyRadius;
            }

            double v2 = gm * (2.0 / lowestRadius - 1.0 / semiMajorAxis);
            if (!(v2 >= 0.0) || double.IsInfinity(v2)) return false;
            maxSpeed = System.Math.Sqrt(v2);
            return true;
        }

        /// <summary>
        /// Returns the body name with the highest point count, or null if the dictionary is empty.
        /// </summary>
        internal static string DeterminePrimaryBody(Dictionary<string, int> bodyCounts)
        {
            string primaryBody = null;
            int maxCount = 0;
            foreach (var kvp in bodyCounts)
            {
                if (kvp.Value > maxCount)
                {
                    maxCount = kvp.Value;
                    primaryBody = kvp.Key;
                }
            }
            return primaryBody;
        }

        private static double HaversineDistance(
            double lat1Deg, double lon1Deg,
            double lat2Deg, double lon2Deg, double radius)
        {
            const double toRad = System.Math.PI / 180.0;
            double lat1 = lat1Deg * toRad;
            double lat2 = lat2Deg * toRad;
            double dlat = (lat2Deg - lat1Deg) * toRad;
            double dlon = (lon2Deg - lon1Deg) * toRad;
            double a = System.Math.Sin(dlat * 0.5) * System.Math.Sin(dlat * 0.5) +
                       System.Math.Cos(lat1) * System.Math.Cos(lat2) *
                       System.Math.Sin(dlon * 0.5) * System.Math.Sin(dlon * 0.5);
            double c = 2.0 * System.Math.Atan2(
                System.Math.Sqrt(a), System.Math.Sqrt(1.0 - a));
            return radius * c;
        }
    }
}
