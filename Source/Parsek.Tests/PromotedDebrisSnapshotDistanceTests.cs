using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// D5-PROMOTED-DEBRIS-MAXDIST-RELATIVE-FRAME (GS-11): a staging booster promoted to the
    /// foreground recorder entered RELATIVE mode near its sibling, so its flat
    /// <c>Points</c> list carried anchor-local Cartesian METRES in latitude/longitude/
    /// altitude. <c>FlightRecorder.SnapshotVessel</c> resolved that flat list as body-fixed
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

        /// <summary>
        /// The vessel-switch stop shape (EVA board, ChainToVessel): the recorder builds the
        /// capture without closing its section, so the capture's deep-copied TrackSections
        /// hold only the CLOSED Absolute section while the Relative one is still the
        /// recorder's open <c>currentTrackSection</c>. The flat list mirrors both.
        /// </summary>
        private static Recording SwitchStopCapture(out TrackSection openRelative)
        {
            Recording full = PromotedBoosterCapture();
            openRelative = full.TrackSections[1];
            full.TrackSections.RemoveAt(1);
            return full;
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
                LiveVesselSnapshot.SnapshotDistanceRoute.BodyFixedSections,
                LiveVesselSnapshot.ClassifySnapshotDistanceRoute(PromotedBoosterCapture()));
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

            Assert.Equal(LiveVesselSnapshot.SnapshotDistanceRoute.FlatPoints,
                LiveVesselSnapshot.ClassifySnapshotDistanceRoute(absoluteOnly));
            Assert.Equal(LiveVesselSnapshot.SnapshotDistanceRoute.FlatPoints,
                LiveVesselSnapshot.ClassifySnapshotDistanceRoute(sectionless));
            Assert.Equal(LiveVesselSnapshot.SnapshotDistanceRoute.FlatPoints,
                LiveVesselSnapshot.ClassifySnapshotDistanceRoute(null));
        }

        // D5 residual: the vessel-switch stop (EVA board, ChainToVessel) builds the capture
        // with the Relative section still open. Without the open section the capture routes
        // through the flat list (the defect); with it, through the body-fixed surfaces.
        [Fact]
        public void SwitchStop_OpenRelativeSection_TakesBodyFixedSections_NotTheFlatAnchorMetres()
        {
            Recording rec = SwitchStopCapture(out TrackSection open);
            Assert.Equal(ReferenceFrame.Relative, open.referenceFrame);
            // The defect: the closed sections alone route through the flat list, which
            // reads the open section's anchor-local metres as lat/lon.
            Assert.Equal(LiveVesselSnapshot.SnapshotDistanceRoute.FlatPoints,
                LiveVesselSnapshot.ClassifySnapshotDistanceRoute(rec));
            Assert.True(FlatChord(rec) > 1000000.0);

            Assert.Equal(LiveVesselSnapshot.SnapshotDistanceRoute.BodyFixedSections,
                LiveVesselSnapshot.ClassifySnapshotDistanceRoute(rec, open));
            Assert.True(LiveVesselSnapshot.TryComputeSnapshotDistancesFromBodyFixedSurfaces(
                rec, FlatSurfaceResolver, null,
                out double distanceFromLaunch,
                out double maxDist,
                out LiveVesselSnapshot.MaxDistanceReferenceSurface referenceSurface,
                out int sampleCount,
                out bool endResolved,
                open));
            Assert.Equal(5000.0, maxDist, 6);
            Assert.Equal(5000.0, distanceFromLaunch, 6);
            Assert.True(endResolved);
            Assert.Equal(LiveVesselSnapshot.MaxDistanceReferenceSurface.AbsoluteFrames, referenceSurface);
            Assert.Equal(5, sampleCount);

            Assert.True(LiveVesselSnapshot.TryGetEndBiomeSamplePoint(rec, true, out TrajectoryPoint end, open));
            Assert.Equal(120.0, end.ut);
            Assert.Equal(5.0, end.latitude);

            // The capture's own sections are not changed by the distance walk.
            Assert.Single(rec.TrackSections);
        }

        // The open section has no body-fixed sample (Relative frames only) and nothing else
        // authors one: the distances stay 0, so the finalize backfill still runs later.
        [Fact]
        public void SwitchStop_OpenRelativeWithoutBodyFixedFrames_LeavesDistancesZero()
        {
            var rec = new Recording { RecordingId = "eva-board" };
            var open = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 80.0,
                frames = new List<TrajectoryPoint> { P(80.0, 1200.0, -850.0, 300.0), P(90.0, 1100.0, -800.0, 200.0) },
            };
            rec.Points.AddRange(open.frames);

            Assert.Equal(LiveVesselSnapshot.SnapshotDistanceRoute.BodyFixedSections,
                LiveVesselSnapshot.ClassifySnapshotDistanceRoute(rec, open));
            Assert.False(LiveVesselSnapshot.TryComputeSnapshotDistancesFromBodyFixedSurfaces(
                rec, FlatSurfaceResolver, new Vector3d(1.0, 0.0, 0.0),
                out double distanceFromLaunch, out double maxDist,
                out LiveVesselSnapshot.MaxDistanceReferenceSurface _, out int _, out bool endResolved, open));
            Assert.Equal(0.0, distanceFromLaunch);
            Assert.Equal(0.0, maxDist);
            Assert.False(endResolved);
        }

        [Fact]
        public void BuildDistanceRouteSections_AddsTheSuppliedOpenSection_NeverMutatesClosed()
        {
            var closed = new List<TrackSection>
            {
                new TrackSection { referenceFrame = ReferenceFrame.Absolute, frames = new List<TrajectoryPoint> { P(1.0, 0.0, 0.0, 0.0) } },
            };
            var withPayload = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                bodyFixedFrames = new List<TrajectoryPoint> { P(2.0, 1.0, 0.0, 0.0) },
            };

            Assert.Same(closed, LiveVesselSnapshot.BuildDistanceRouteSections(closed, null));
            var combined = LiveVesselSnapshot.BuildDistanceRouteSections(closed, withPayload);
            Assert.Equal(2, combined.Count);
            Assert.Equal(ReferenceFrame.Relative, combined[1].referenceFrame);
            Assert.Single(closed);
            Assert.Single(LiveVesselSnapshot.BuildDistanceRouteSections(null, withPayload));
            Assert.Null(LiveVesselSnapshot.BuildDistanceRouteSections(null, null));
            Assert.Equal(LiveVesselSnapshot.SnapshotDistanceRoute.FlatPoints,
                LiveVesselSnapshot.ClassifySnapshotDistanceRouteForSections(closed));
            Assert.Equal(LiveVesselSnapshot.SnapshotDistanceRoute.BodyFixedSections,
                LiveVesselSnapshot.ClassifySnapshotDistanceRouteForSections(combined));
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

            Assert.True(LiveVesselSnapshot.TryComputeSnapshotDistancesFromBodyFixedSurfaces(
                rec, FlatSurfaceResolver, null,
                out double distanceFromLaunch,
                out double maxDist,
                out LiveVesselSnapshot.MaxDistanceReferenceSurface referenceSurface,
                out int sampleCount,
                out bool endResolved));

            Assert.Equal(5000.0, maxDist, 6);
            Assert.Equal(5000.0, distanceFromLaunch, 6);
            Assert.True(endResolved);
            Assert.Equal(LiveVesselSnapshot.MaxDistanceReferenceSurface.AbsoluteFrames, referenceSurface);
            Assert.Equal(5, sampleCount);
            Assert.NotEqual(flatChord, maxDist);
        }

        // The live-vessel path: the end position is the vessel's own world position.
        [Fact]
        public void Live_DistanceRunsToTheSuppliedEndPosition()
        {
            Recording rec = PromotedBoosterCapture();

            Assert.True(LiveVesselSnapshot.TryComputeSnapshotDistancesFromBodyFixedSurfaces(
                rec, FlatSurfaceResolver, new Vector3d(4000.0, 0.0, 0.0),
                out double distanceFromLaunch,
                out double maxDist,
                out LiveVesselSnapshot.MaxDistanceReferenceSurface _,
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

            Assert.Equal(LiveVesselSnapshot.SnapshotDistanceRoute.BodyFixedSections,
                LiveVesselSnapshot.ClassifySnapshotDistanceRoute(rec));
            Assert.False(LiveVesselSnapshot.TryComputeSnapshotDistancesFromBodyFixedSurfaces(
                rec, FlatSurfaceResolver, null,
                out double distanceFromLaunch,
                out double maxDist,
                out LiveVesselSnapshot.MaxDistanceReferenceSurface referenceSurface,
                out int sampleCount,
                out bool endResolved));

            Assert.Equal(0.0, distanceFromLaunch);
            Assert.Equal(0.0, maxDist);
            Assert.Equal(LiveVesselSnapshot.MaxDistanceReferenceSurface.None, referenceSurface);
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

            Assert.True(LiveVesselSnapshot.TryGetEndBiomeSamplePoint(rec, true, out TrajectoryPoint bodyFixed));
            Assert.Equal(120.0, bodyFixed.ut);
            Assert.Equal(5.0, bodyFixed.latitude);
            Assert.Equal(0.0, bodyFixed.longitude);

            Assert.True(LiveVesselSnapshot.TryGetEndBiomeSamplePoint(rec, false, out TrajectoryPoint flat));
            Assert.Equal(1100.0, flat.latitude);    // the flat route still reads the last point

            Assert.False(LiveVesselSnapshot.TryGetEndBiomeSamplePoint(null, true, out TrajectoryPoint _));
        }

        [Fact]
        public void LatestSampleIndex_TieResolvesToTheLaterCollectedSample()
        {
            var samples = new List<LiveVesselSnapshot.BodyFixedSectionSample>
            {
                new LiveVesselSnapshot.BodyFixedSectionSample { point = P(80.0, 1.0, 0.0, 0.0), sectionIndex = 0 },
                new LiveVesselSnapshot.BodyFixedSectionSample { point = P(80.0, 2.0, 0.0, 0.0), sectionIndex = 1 },
                new LiveVesselSnapshot.BodyFixedSectionSample { point = P(70.0, 0.0, 0.0, 0.0), sectionIndex = 1 },
            };

            Assert.Equal(1, LiveVesselSnapshot.ResolveLatestSampleIndex(samples));
            Assert.Equal(-1, LiveVesselSnapshot.ResolveLatestSampleIndex(new List<LiveVesselSnapshot.BodyFixedSectionSample>()));
            Assert.Equal(-1, LiveVesselSnapshot.ResolveLatestSampleIndex(null));
        }
    }
}
