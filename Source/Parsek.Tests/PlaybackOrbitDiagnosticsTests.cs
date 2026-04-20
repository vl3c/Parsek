using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class PlaybackOrbitDiagnosticsTests
    {
        [Fact]
        public void TryGetRecordedPayloadEndUT_UsesTrackSectionPayloadInsteadOfFlatOrbitTail()
        {
            var traj = new MockTrajectory
            {
                RecordingId = "rec-track-tail"
            };

            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 100.0,
                endUT = 200.0,
                checkpoints = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 100.0,
                        endUT = 200.0,
                        bodyName = "Kerbin",
                        semiMajorAxis = 700000.0
                    }
                }
            });

            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 100.0,
                endUT = 200.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0
            });
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 200.0,
                endUT = 500.0,
                bodyName = "Kerbin",
                semiMajorAxis = 710000.0
            });

            Assert.True(PlaybackOrbitDiagnostics.TryGetRecordedPayloadEndUT(traj, out double endUT));
            Assert.Equal(200.0, endUT);
        }

        [Fact]
        public void TryBuildPlaybackPredictedTailLog_AfterPayloadEnd_ReturnsDetails()
        {
            var traj = BuildPredictedTailTrajectory("rec-playback-tail");
            OrbitSegment predictedSegment = traj.OrbitSegments[1];

            bool result = PlaybackOrbitDiagnostics.TryBuildPlaybackPredictedTailLog(
                7, traj, predictedSegment, 350.0, out string key, out string message);

            Assert.True(result);
            Assert.Equal("predicted-tail-rec-playback-tail", key);
            Assert.Contains("rec=rec-playback-tail", message);
            Assert.Contains("index=7", message);
            Assert.Contains("body=Kerbin", message);
            Assert.Contains("segmentIndex=1", message);
            Assert.Contains("ut=350.00", message);
            Assert.Contains("payloadEndUT=200.00", message);
            Assert.Contains("segmentUT=200.00-500.00", message);
        }

        [Fact]
        public void TryBuildPlaybackPredictedTailLog_BeforePayloadEnd_ReturnsFalse()
        {
            var traj = BuildPredictedTailTrajectory("rec-no-tail");
            OrbitSegment actualSegment = traj.OrbitSegments[0];

            bool result = PlaybackOrbitDiagnostics.TryBuildPlaybackPredictedTailLog(
                3, traj, actualSegment, 150.0, out string key, out string message);

            Assert.False(result);
            Assert.Null(key);
            Assert.Null(message);
        }

        [Fact]
        public void TryBuildMapPredictedTailLog_AfterPayloadEnd_ReturnsDetails()
        {
            var traj = BuildPredictedTailTrajectory("rec-map-tail");
            OrbitSegment predictedSegment = traj.OrbitSegments[1];

            bool result = PlaybackOrbitDiagnostics.TryBuildMapPredictedTailLog(
                4,
                4242u,
                traj,
                predictedSegment,
                350.0,
                200.0,
                500.0,
                true,
                out string key,
                out string message);

            Assert.True(result);
            Assert.Equal("predicted-tail-rec-map-tail", key);
            Assert.Contains("rec=rec-map-tail", message);
            Assert.Contains("index=4", message);
            Assert.Contains("pid=4242", message);
            Assert.Contains("body=Kerbin", message);
            Assert.Contains("segmentIndex=1", message);
            Assert.Contains("windowUT=200.00-500.00", message);
            Assert.Contains("gapCarry=True", message);
        }

        private static MockTrajectory BuildPredictedTailTrajectory(string recordingId)
        {
            var traj = new MockTrajectory
            {
                RecordingId = recordingId
            };

            traj.Points.Add(new TrajectoryPoint
            {
                ut = 100.0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            });
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 200.0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            });

            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 100.0,
                endUT = 200.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0
            });
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 200.0,
                endUT = 500.0,
                bodyName = "Kerbin",
                semiMajorAxis = 710000.0
            });

            return traj;
        }
    }
}
