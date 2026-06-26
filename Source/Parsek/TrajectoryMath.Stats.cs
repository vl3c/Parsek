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
            int firstPointSectionIdx = FindTrackSectionForUT(rec.TrackSections, rec.Points[0].ut);
            ReferenceFrame firstPointFrame = firstPointSectionIdx >= 0
                ? rec.TrackSections[firstPointSectionIdx].referenceFrame
                : ReferenceFrame.Absolute;

            ApplyTrackSectionAltitudeMetadata(rec.TrackSections, ref stats);

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
                                int sectionIdx = FindTrackSectionForUT(rec.TrackSections, midUT);
                                ReferenceFrame frame = sectionIdx >= 0
                                    ? rec.TrackSections[sectionIdx].referenceFrame
                                    : ReferenceFrame.Absolute;

                                stats.distanceTravelled += ComputePairwiseTravelDistance(
                                    prev, pt, frame, bodyRadius);
                            }
                        }

                        // Max range from first point (same body only)
                        if (body == body0)
                        {
                            int pointSectionIdx = FindTrackSectionForUT(rec.TrackSections, pt.ut);
                            ReferenceFrame pointFrame = pointSectionIdx >= 0
                                ? rec.TrackSections[pointSectionIdx].referenceFrame
                                : ReferenceFrame.Absolute;
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
                $"dist={stats.distanceTravelled:F0} range={stats.maxRange:F0} body={stats.primaryBody}");

            return stats;
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
        /// periapsis speed (vis-viva), and mean-speed distance for each segment.
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

                // Periapsis speed (max orbital speed via vis-viva)
                double periRadius = seg.semiMajorAxis * (1.0 - seg.eccentricity);
                if (periRadius > 0 && seg.semiMajorAxis > 0)
                {
                    double periSpeed = System.Math.Sqrt(
                        gm * (2.0 / periRadius - 1.0 / seg.semiMajorAxis));
                    if (periSpeed > stats.maxSpeed)
                        stats.maxSpeed = periSpeed;
                }

                // Mean orbital speed * duration
                if (seg.semiMajorAxis > 0)
                {
                    double meanSpeed = System.Math.Sqrt(gm / seg.semiMajorAxis);
                    stats.distanceTravelled += meanSpeed * (seg.endUT - seg.startUT);
                }
            }
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
