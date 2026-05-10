using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class RecordingVisualClassifierTests
    {
        [Fact]
        public void Classify_SurfacePlaceholderWithoutTrail_ReturnsStaticPlaceholder()
        {
            Recording rec = StaticSurfaceRecording();
            rec.ExplicitStartUT = 100;
            rec.ExplicitEndUT = 200;

            Assert.Equal(
                RecordingVisualKind.StaticPlaceholder,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_SurfacePlaceholderWithOneSeedPoint_ReturnsStaticPlaceholder()
        {
            Recording rec = StaticSurfaceRecording();
            rec.Points.Add(new TrajectoryPoint { ut = 100 });

            Assert.Equal(
                RecordingVisualKind.StaticPlaceholder,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_SurfaceRecordingWithTwoPoints_ReturnsNormal()
        {
            Recording rec = StaticSurfaceRecording();
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            rec.Points.Add(new TrajectoryPoint { ut = 200 });

            Assert.Equal(
                RecordingVisualKind.Normal,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_SurfacePlaceholderWithOrbitPayload_ReturnsNormal()
        {
            Recording rec = StaticSurfaceRecording();
            rec.OrbitSegments.Add(new OrbitSegment { startUT = 100, endUT = 200 });

            Assert.Equal(
                RecordingVisualKind.Normal,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_SurfacePlaceholderWithAnimatedSectionPayload_ReturnsNormal()
        {
            Recording rec = StaticSurfaceRecording();
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100,
                endUT = 200,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100 },
                    new TrajectoryPoint { ut = 200 }
                }
            });

            Assert.Equal(
                RecordingVisualKind.Normal,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_SurfacePlaceholderWithSingleSectionFrame_ReturnsStaticPlaceholder()
        {
            Recording rec = StaticSurfaceRecording();
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100,
                endUT = 200,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100 }
                }
            });

            Assert.Equal(
                RecordingVisualKind.StaticPlaceholder,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_AllSurfaceStationaryLeafWithoutEvents_ReturnsStationaryTail()
        {
            Recording rec = StationaryTailRecording();

            Assert.Equal(
                RecordingVisualKind.StationaryTail,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_AllSurfaceStationaryLeafWithInertPartEvent_ReturnsStationaryTail()
        {
            Recording rec = StationaryTailRecording();
            rec.PartEvents.Add(new PartEvent
            {
                ut = 150,
                eventType = PartEventType.EngineThrottle,
                value = 0f
            });

            Assert.Equal(
                RecordingVisualKind.StationaryTail,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_AllSurfaceStationaryLeafWithNonInertPartEvent_ReturnsNormal()
        {
            Recording rec = StationaryTailRecording();
            rec.PartEvents.Add(new PartEvent
            {
                ut = 150,
                eventType = PartEventType.ParachuteDeployed
            });

            Assert.Equal(
                RecordingVisualKind.Normal,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_AllSurfaceStationaryLeafWithSegmentEvent_ReturnsNormal()
        {
            Recording rec = StationaryTailRecording();
            rec.SegmentEvents.Add(new SegmentEvent
            {
                ut = 150,
                type = SegmentEventType.ControllerChange
            });

            Assert.Equal(
                RecordingVisualKind.Normal,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_SurfaceTailWithNonBoringSection_ReturnsNormal()
        {
            Recording rec = StationaryTailRecording();
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Approach,
                startUT = 200,
                endUT = 250
            });

            Assert.Equal(
                RecordingVisualKind.Normal,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_SurfaceTailWithOrbitPayload_ReturnsNormal()
        {
            Recording rec = StationaryTailRecording();
            rec.OrbitSegments.Add(new OrbitSegment { startUT = 100, endUT = 200 });

            Assert.Equal(
                RecordingVisualKind.Normal,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_AllExoBallisticLeaf_ReturnsNormal()
        {
            Recording rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            rec.Points.Add(new TrajectoryPoint { ut = 200 });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 100,
                endUT = 200
            });

            Assert.Equal(
                RecordingVisualKind.Normal,
                RecordingVisualClassifier.Classify(rec, new List<Recording> { rec }));
        }

        [Fact]
        public void Classify_MidChainSurfaceStationaryRecording_ReturnsNormal()
        {
            Recording first = StationaryTailRecording();
            first.ChainId = "chain";
            first.ChainIndex = 0;
            first.ChainBranch = 0;

            Recording successor = new Recording
            {
                ChainId = "chain",
                ChainIndex = 1,
                ChainBranch = 0
            };

            Assert.Equal(
                RecordingVisualKind.Normal,
                RecordingVisualClassifier.Classify(first, new List<Recording> { first, successor }));
        }

        private static Recording StaticSurfaceRecording()
        {
            return new Recording
            {
                SurfacePos = new SurfacePosition
                {
                    body = "Mun",
                    latitude = 1.0,
                    longitude = 2.0,
                    altitude = 3.0,
                    rotation = Quaternion.identity,
                    rotationRecorded = true,
                    situation = SurfaceSituation.Landed
                }
            };
        }

        private static Recording StationaryTailRecording()
        {
            Recording rec = new Recording
            {
                RecordingId = "stationary-tail",
                VesselName = "Mun Rover"
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            rec.Points.Add(new TrajectoryPoint { ut = 200 });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                startUT = 100,
                endUT = 200
            });
            return rec;
        }
    }
}
