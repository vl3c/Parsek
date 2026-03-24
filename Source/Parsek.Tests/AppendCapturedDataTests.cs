using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="ParsekFlight.AppendCapturedDataToRecording"/>.
    /// Verifies that captured trajectory data (Points, OrbitSegments, PartEvents,
    /// TrackSections) is correctly appended from a source recording into a target,
    /// and that ExplicitEndUT is always set.
    /// </summary>
    public class AppendCapturedDataTests
    {
        private static TrajectoryPoint MakePoint(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 100.0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
        }

        private static PartEvent MakePartEvent(double ut, PartEventType type)
        {
            return new PartEvent
            {
                ut = ut,
                partPersistentId = 100000,
                eventType = type,
                partName = "fuelTank",
            };
        }

        private static OrbitSegment MakeOrbitSegment(double startUT, double endUT)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = "Kerbin",
                inclination = 0.0,
                eccentricity = 0.0,
                semiMajorAxis = 700000.0,
            };
        }

        private static TrackSection MakeTrackSection(double startUT, double endUT)
        {
            return new TrackSection
            {
                startUT = startUT,
                endUT = endUT,
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                source = TrackSectionSource.Active,
                frames = new List<TrajectoryPoint>(),
            };
        }

        [Fact]
        public void Normal_AllDataAppended()
        {
            // Bug caught: all four list types (Points, OrbitSegments, PartEvents,
            // TrackSections) must be appended — missing any one silently drops data
            // from continuation recordings.
            var target = new Recording();
            target.Points.Add(MakePoint(100.0));

            var source = new Recording();
            source.Points.Add(MakePoint(200.0));
            source.Points.Add(MakePoint(300.0));
            source.OrbitSegments.Add(MakeOrbitSegment(200.0, 300.0));
            source.PartEvents.Add(MakePartEvent(250.0, PartEventType.EngineIgnited));
            source.TrackSections.Add(MakeTrackSection(200.0, 300.0));

            ParsekFlight.AppendCapturedDataToRecording(target, source, 300.0);

            Assert.Equal(3, target.Points.Count);
            Assert.Single(target.OrbitSegments);
            Assert.Single(target.PartEvents);
            Assert.Single(target.TrackSections);
        }

        [Fact]
        public void NullSource_TargetUnchanged()
        {
            // Bug caught: null source must not throw NullReferenceException —
            // occurs when a background recorder has no captured data at split time.
            var target = new Recording();
            target.Points.Add(MakePoint(100.0));

            ParsekFlight.AppendCapturedDataToRecording(target, null, 200.0);

            Assert.Single(target.Points);
            Assert.Equal(200.0, target.ExplicitEndUT);
        }

        [Fact]
        public void EmptySourceLists_TargetUnchanged()
        {
            // Bug caught: source with empty lists must not corrupt target data —
            // AddRange on empty list is a no-op, but the Sort on PartEvents must
            // still succeed without throwing on empty list.
            var target = new Recording();
            target.Points.Add(MakePoint(100.0));
            target.PartEvents.Add(MakePartEvent(50.0, PartEventType.EngineShutdown));

            var source = new Recording(); // all lists empty

            ParsekFlight.AppendCapturedDataToRecording(target, source, 200.0);

            Assert.Single(target.Points);
            Assert.Single(target.PartEvents);
        }

        [Fact]
        public void ExplicitEndUT_SetCorrectly()
        {
            // Bug caught: ExplicitEndUT must always be set to the provided endUT —
            // background-only recordings with no Points rely on ExplicitEndUT for
            // their time range. Failing to set it breaks timeline display.
            var target = new Recording();
            Assert.True(double.IsNaN(target.ExplicitEndUT)); // default is NaN

            ParsekFlight.AppendCapturedDataToRecording(target, null, 42.5);

            Assert.Equal(42.5, target.ExplicitEndUT);
        }

        [Fact]
        public void PartEvents_SortedAfterAppend()
        {
            // Bug caught: PartEvents from different sources (active recorder,
            // background recorder, split captures) arrive in non-chronological order.
            // Without post-append sort, playback applies events at wrong times —
            // e.g., engine shutdown before ignition.
            var target = new Recording();
            target.PartEvents.Add(MakePartEvent(100.0, PartEventType.EngineIgnited));
            target.PartEvents.Add(MakePartEvent(300.0, PartEventType.EngineShutdown));

            var source = new Recording();
            source.PartEvents.Add(MakePartEvent(150.0, PartEventType.ParachuteDeployed));
            source.PartEvents.Add(MakePartEvent(50.0, PartEventType.LightOn));

            ParsekFlight.AppendCapturedDataToRecording(target, source, 400.0);

            Assert.Equal(4, target.PartEvents.Count);
            Assert.Equal(50.0, target.PartEvents[0].ut);
            Assert.Equal(100.0, target.PartEvents[1].ut);
            Assert.Equal(150.0, target.PartEvents[2].ut);
            Assert.Equal(300.0, target.PartEvents[3].ut);
        }

        [Fact]
        public void ExplicitEndUT_SetEvenWithNullSource()
        {
            // Bug caught: ExplicitEndUT must be set regardless of whether source
            // is null — the endUT parameter marks the recording boundary for the
            // merge/split operation. A conditional that only sets it inside the
            // non-null branch would leave background recordings with NaN end time.
            var target = new Recording();

            ParsekFlight.AppendCapturedDataToRecording(target, null, 999.0);

            Assert.Equal(999.0, target.ExplicitEndUT);
        }

        [Fact]
        public void PreExistingTargetData_Preserved()
        {
            // Bug caught: target data from the original recording must not be
            // overwritten — append uses AddRange, not assignment. If the
            // implementation cleared lists before adding, existing trajectory
            // data would be lost.
            var target = new Recording();
            target.Points.Add(MakePoint(10.0));
            target.Points.Add(MakePoint(20.0));
            target.OrbitSegments.Add(MakeOrbitSegment(10.0, 20.0));
            target.PartEvents.Add(MakePartEvent(15.0, PartEventType.Decoupled));
            target.TrackSections.Add(MakeTrackSection(10.0, 20.0));

            var source = new Recording();
            source.Points.Add(MakePoint(30.0));

            ParsekFlight.AppendCapturedDataToRecording(target, source, 30.0);

            Assert.Equal(3, target.Points.Count);
            Assert.Equal(10.0, target.Points[0].ut);
            Assert.Equal(20.0, target.Points[1].ut);
            Assert.Equal(30.0, target.Points[2].ut);
            Assert.Single(target.OrbitSegments);
            Assert.Single(target.PartEvents);
            Assert.Single(target.TrackSections);
        }
    }
}
