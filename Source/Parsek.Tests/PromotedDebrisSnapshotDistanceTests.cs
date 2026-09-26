using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// D5-PROMOTED-DEBRIS-MAXDIST-RELATIVE-FRAME (GS-11): a staging booster promoted to the
    /// foreground recorder entered RELATIVE mode near its sibling, so its flat
    /// <c>Points</c> list carried anchor-local Cartesian METRES in latitude/longitude/
    /// altitude. <c>VesselSpawner.SnapshotVessel</c> resolved that flat list as body-fixed
    /// coordinates and stamped <c>Max distance: 1205340m</c> on a booster that fell 5 km
    /// from the pad; the frame-correct finalize backfill then skipped the recording
    /// because its maxDist was already non-zero. The stop-time distances now take the
    /// body-fixed section walk whenever a Relative section is present.
    /// </summary>
    public class PromotedDebrisSnapshotDistanceTests
    {
        // A deterministic stand-in for CelestialBody.GetWorldSurfacePosition: a flat plane
        // at 1000 m per degree. These cells assert WHICH samples the computation read.
        private const double MetresPerDegree = 1000.0;

        private static Vector3d? FlatSurfaceResolver(TrajectoryPoint pt)
        {
            if (string.IsNullOrEmpty(pt.bodyName)) return null;
            return new Vector3d(
                pt.latitude * MetresPerDegree, pt.longitude * MetresPerDegree, pt.altitude);
        }

        private static TrajectoryPoint P(double ut, double lat, double lon, double alt)
        {
            return new TrajectoryPoint
            {
                ut = ut, latitude = lat, longitude = lon, altitude = alt, bodyName = "Kerbin",
            };
        }

        /// <summary>
        /// The promoted-booster capture shape: an Absolute section from the promotion,
        /// then a Relative section (anchored near the sibling booster) running to impact.
        /// The flat list mirrors both sections' frames verbatim, so its tail is anchor-local
        /// metres - numbers that resolve ~1470 km from the first point on the flat plane.
        /// The true body-fixed track covers 5 km.
        /// </summary>
        private static Recording PromotedBoosterCapture()
        {
            var absolute = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 71.0,
                endUT = 80.0,
                frames = new List<TrajectoryPoint> { P(71.0, 0.0, 0.0, 0.0), P(80.0, 1.0, 0.0, 0.0) },
            };
            var relative = new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Relative,
                startUT = 80.0,
                endUT = 120.0,
                anchorRecordingId = "sibling-booster",
                frames = new List<TrajectoryPoint>
                {
                    P(80.0, 1200.0, -850.0, 300.0),
                    P(100.0, 1150.0, -820.0, 250.0),
                    P(120.0, 1100.0, -800.0, 200.0),
                },
                bodyFixedFrames = new List<TrajectoryPoint>
                {
                    P(80.0, 1.0, 0.0, 0.0),
                    P(100.0, 3.0, 0.0, 0.0),
                    P(120.0, 5.0, 0.0, 0.0),
                },
            };

            var rec = new Recording
            {
                RecordingId = "baa46b97",
                VesselName = "Kerbal X Debris",
                ParentAnchorRecordingId = "kerbal-x-root",
                IsDebris = true,
            };
            rec.TrackSections.Add(absolute);
            rec.TrackSections.Add(relative);
            rec.Points.AddRange(absolute.frames);
            rec.Points.AddRange(relative.frames);
            return rec;
        }

        private static double FlatChord(Recording rec)
        {
            Vector3d first = FlatSurfaceResolver(rec.Points[0]).Value;
            double max = 0.0;
            for (int i = 1; i < rec.Points.Count; i++)
                max = Math.Max(max, Vector3d.Distance(first, FlatSurfaceResolver(rec.Points[i]).Value));
            return max;
        }

        [Fact]
        public void Route_RelativeSectionPresent_TakesBodyFixedSections()
        {
            Assert.Equal(
                VesselSpawner.SnapshotDistanceRoute.BodyFixedSections,
                VesselSpawner.ClassifySnapshotDistanceRoute(PromotedBoosterCapture()));
        }

        [Fact]
        public void Route_NoRelativeSection_KeepsFlatPoints()
        {
            var absoluteOnly = new Recording { RecordingId = "abs-only" };
            absoluteOnly.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                frames = new List<TrajectoryPoint> { P(1.0, 0.0, 0.0, 0.0), P(2.0, 1.0, 0.0, 0.0) },
            });
            absoluteOnly.Points.AddRange(absoluteOnly.TrackSections[0].frames);

            var sectionless = new Recording { RecordingId = "flat-only" };
            sectionless.Points.Add(P(1.0, 0.0, 0.0, 0.0));
            sectionless.Points.Add(P(2.0, 1.0, 0.0, 0.0));

            Assert.Equal(VesselSpawner.SnapshotDistanceRoute.FlatPoints,
                VesselSpawner.ClassifySnapshotDistanceRoute(absoluteOnly));
            Assert.Equal(VesselSpawner.SnapshotDistanceRoute.FlatPoints,
                VesselSpawner.ClassifySnapshotDistanceRoute(sectionless));
            Assert.Equal(VesselSpawner.SnapshotDistanceRoute.FlatPoints,
                VesselSpawner.ClassifySnapshotDistanceRoute(null));
        }

        // The destroyed-vessel path: no live position, so the distance runs from the
        // earliest to the latest body-fixed sample. Both numbers come from body-fixed data,
        // never from the flat list's anchor-local tail.
        [Fact]
        public void Destroyed_DistancesComeFromBodyFixedSamples_NotTheFlatAnchorMetres()
        {
            Recording rec = PromotedBoosterCapture();
            double flatChord = FlatChord(rec);
            Assert.True(flatChord > 1000000.0,
                "fixture must reproduce the defect: the flat list reads as >1000 km");

            Assert.True(VesselSpawner.TryComputeSnapshotDistancesFromBodyFixedSurfaces(
                rec, FlatSurfaceResolver, null,
                out double distanceFromLaunch,
                out double maxDist,
                out VesselSpawner.MaxDistanceReferenceSurface referenceSurface,
                out int sampleCount,
                out bool endResolved));

            Assert.Equal(5000.0, maxDist, 6);
            Assert.Equal(5000.0, distanceFromLaunch, 6);
            Assert.True(endResolved);
            Assert.Equal(VesselSpawner.MaxDistanceReferenceSurface.AbsoluteFrames, referenceSurface);
            Assert.Equal(5, sampleCount);
            Assert.NotEqual(flatChord, maxDist);
        }

        // The live-vessel path: the end position is the vessel's own world position.
        [Fact]
        public void Live_DistanceRunsToTheSuppliedEndPosition()
        {
            Recording rec = PromotedBoosterCapture();

            Assert.True(VesselSpawner.TryComputeSnapshotDistancesFromBodyFixedSurfaces(
                rec, FlatSurfaceResolver, new Vector3d(4000.0, 0.0, 0.0),
                out double distanceFromLaunch,
                out double maxDist,
                out VesselSpawner.MaxDistanceReferenceSurface _,
                out int _,
                out bool endResolved));

            Assert.Equal(4000.0, distanceFromLaunch, 6);
            Assert.Equal(5000.0, maxDist, 6);
            Assert.True(endResolved);
        }

        // A Relative section with no bodyFixedFrames and no Absolute frames: nothing
        // body-fixed to measure, so the computation refuses (fail closed) instead of
        // resolving the anchor-local metres.
        [Fact]
        public void RelativeFramesOnly_RefusesAndWritesZeros()
        {
            var rec = new Recording { RecordingId = "relative-frames-only" };
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 80.0,
                endUT = 120.0,
                frames = new List<TrajectoryPoint> { P(80.0, 1200.0, -850.0, 300.0), P(120.0, 1100.0, -800.0, 200.0) },
            });
            rec.Points.AddRange(rec.TrackSections[0].frames);

            Assert.Equal(VesselSpawner.SnapshotDistanceRoute.BodyFixedSections,
                VesselSpawner.ClassifySnapshotDistanceRoute(rec));
            Assert.False(VesselSpawner.TryComputeSnapshotDistancesFromBodyFixedSurfaces(
                rec, FlatSurfaceResolver, null,
                out double distanceFromLaunch,
                out double maxDist,
                out VesselSpawner.MaxDistanceReferenceSurface referenceSurface,
                out int sampleCount,
                out bool endResolved));

            Assert.Equal(0.0, distanceFromLaunch);
            Assert.Equal(0.0, maxDist);
            Assert.Equal(VesselSpawner.MaxDistanceReferenceSurface.None, referenceSurface);
            Assert.Equal(0, sampleCount);
            Assert.False(endResolved);
        }

        // Mirror direction: the destroyed path also resolves the END BIOME from a sample.
        // On the body-fixed route that is the latest body-fixed sample, not the flat tail
        // (an anchor-local Relative frame).
        [Fact]
        public void EndBiomeSample_BodyFixedRoute_IsTheLatestBodyFixedSample()
        {
            Recording rec = PromotedBoosterCapture();

            Assert.True(VesselSpawner.TryGetEndBiomeSamplePoint(rec, true, out TrajectoryPoint bodyFixed));
            Assert.Equal(120.0, bodyFixed.ut);
            Assert.Equal(5.0, bodyFixed.latitude);
            Assert.Equal(0.0, bodyFixed.longitude);

            Assert.True(VesselSpawner.TryGetEndBiomeSamplePoint(rec, false, out TrajectoryPoint flat));
            Assert.Equal(1100.0, flat.latitude);    // the flat route still reads the last point

            Assert.False(VesselSpawner.TryGetEndBiomeSamplePoint(null, true, out TrajectoryPoint _));
        }

        [Fact]
        public void LatestSampleIndex_TieResolvesToTheLaterCollectedSample()
        {
            var samples = new List<VesselSpawner.BodyFixedSectionSample>
            {
                new VesselSpawner.BodyFixedSectionSample { point = P(80.0, 1.0, 0.0, 0.0), sectionIndex = 0 },
                new VesselSpawner.BodyFixedSectionSample { point = P(80.0, 2.0, 0.0, 0.0), sectionIndex = 1 },
                new VesselSpawner.BodyFixedSectionSample { point = P(70.0, 0.0, 0.0, 0.0), sectionIndex = 1 },
            };

            Assert.Equal(1, VesselSpawner.ResolveLatestSampleIndex(samples));
            Assert.Equal(-1, VesselSpawner.ResolveLatestSampleIndex(new List<VesselSpawner.BodyFixedSectionSample>()));
            Assert.Equal(-1, VesselSpawner.ResolveLatestSampleIndex(null));
        }
    }
}
