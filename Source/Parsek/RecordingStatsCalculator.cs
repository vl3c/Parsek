using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Aggregate statistics for a whole recording (the recordings table's stats columns).
    /// The one Recording-aware stats entry point; the per-sample and per-segment helpers it
    /// calls (frame resolution, pairwise distance, range, orbit-segment accumulation) stay on
    /// <see cref="TrajectoryMath"/>, which knows trajectories, not recordings.
    /// </summary>
    internal static class RecordingStatsCalculator
    {
        /// <summary>
        /// Computes aggregate statistics for a recording.
        /// Pure function - uses bodyLookup callback for orbit segment calculations.
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
                TrajectoryMath.ResolvePointFrameForStats(rec.TrackSections, rec.Points[0].ut);

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
                            bool inOrbitSegment = TrajectoryMath.FindOrbitSegment(rec.OrbitSegments, midUT) != null;
                            if (!inOrbitSegment)
                            {
                                ReferenceFrame frame;
                                if (TrajectoryMath.TryResolvePairTravelFrame(
                                        rec.TrackSections, prev.ut, pt.ut, out frame))
                                {
                                    stats.distanceTravelled += TrajectoryMath.ComputePairwiseTravelDistance(
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
                                TrajectoryMath.ResolvePointFrameForStats(rec.TrackSections, pt.ut);
                            double range = TrajectoryMath.ComputePointRangeFromStart(
                                rec.Points[0], pt, firstPointFrame, pointFrame, bodyRadius);
                            if (range > stats.maxRange)
                                stats.maxRange = range;
                        }
                    }
                }
            }

            TrajectoryMath.AccumulateOrbitSegmentStats(rec.OrbitSegments, bodyLookup, ref stats);
            stats.primaryBody = TrajectoryMath.DeterminePrimaryBody(bodyCounts);

            ParsekLog.Verbose("TrajectoryMath",
                $"ComputeStats complete: points={stats.pointCount} segments={stats.orbitSegmentCount} " +
                $"events={stats.partEventCount} maxAlt={stats.maxAltitude:F0} maxSpeed={stats.maxSpeed:F1} " +
                $"dist={stats.distanceTravelled:F0} range={stats.maxRange:F0} body={stats.primaryBody} " +
                $"skippedFrameChangePairs={skippedFrameChangePairs}");

            return stats;
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
    }
}
