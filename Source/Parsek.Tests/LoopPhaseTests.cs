using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    #region ComputeLoopPhaseFromUT Tests

    public class ComputeLoopPhaseFromUT_Tests
    {
        [Fact]
        public void AtRecordingStart_ReturnsPhase0Cycle0NotPaused()
        {
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 100.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 10.0);

            Assert.Equal(100.0, loopUT);
            Assert.Equal(0, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void MidwayThroughFirstCycle_ReturnsCorrectLoopUT()
        {
            // duration=100, interval=10, cycle=110
            // At UT 150, elapsed=50, phase=50 within playback portion
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 150.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 10.0);

            Assert.Equal(150.0, loopUT);
            Assert.Equal(0, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void AtRecordingEnd_ReturnsEndUTCycle0NotPaused()
        {
            // At UT 200, elapsed=100, phase=100 == duration (edge of playback)
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 200.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 10.0);

            // phase=100, duration=100, so phase < duration is false (equal)
            // This falls into pause territory at exactly the boundary
            Assert.Equal(200.0, loopUT);
            Assert.Equal(0, cycleIndex);
            // At exactly duration, phaseInCycle == duration, so it enters pause branch
            Assert.True(isInPause);
        }

        [Fact]
        public void InPauseInterval_ReturnsPausedAtEndUT()
        {
            // duration=100, interval=10, cycle=110
            // At UT 205, elapsed=105, phase=105 > duration=100 → in pause
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 205.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 10.0);

            Assert.Equal(200.0, loopUT);
            Assert.Equal(0, cycleIndex);
            Assert.True(isInPause);
        }

        [Fact]
        public void StartOfSecondCycle_ReturnsCycle1NearStartUT()
        {
            // duration=100, interval=10, cycle=110
            // At UT 210, elapsed=110 → cycle 1, phaseInCycle=0
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 210.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 10.0);

            Assert.Equal(100.0, loopUT);
            Assert.Equal(1, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void MidwayThroughSecondCycle_CorrectPhase()
        {
            // duration=100, interval=10, cycle=110
            // At UT 260, elapsed=160, cycle=1 (160/110=1.45), phaseInCycle=160-110=50
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 260.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 10.0);

            Assert.Equal(150.0, loopUT);
            Assert.Equal(1, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void ManyCyclesElapsed_CorrectCycleIndexAndPhase()
        {
            // duration=100, interval=10, cycle=110
            // 1000 cycles elapsed: UT = 100 + 1000*110 = 110100
            // At UT 110150, elapsed=110050, cycle=1000 (110050/110=1000.45), phase=50
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 110150.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 10.0);

            Assert.Equal(150.0, loopUT);
            Assert.Equal(1000, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void ZeroDurationRecording_ReturnsStartUT()
        {
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 150.0, recordingStartUT: 100.0, recordingEndUT: 100.0, intervalSeconds: 10.0);

            Assert.Equal(100.0, loopUT);
            Assert.Equal(0, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void NegativeInterval_TreatedAsZeroPause()
        {
            // duration=100, interval=-5 → Math.Max(0, -5) = 0, cycleDuration=100
            // At UT 250, elapsed=150, cycle=1 (150/100=1.5), phase=50
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 250.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: -5.0);

            Assert.Equal(150.0, loopUT);
            Assert.Equal(1, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void BeforeRecordingStarted_ReturnsStartUTCycle0()
        {
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 50.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 10.0);

            Assert.Equal(100.0, loopUT);
            Assert.Equal(0, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void ZeroInterval_ImmediateRestart()
        {
            // duration=100, interval=0, cycleDuration=100
            // At UT 250, elapsed=150, cycle=1 (150/100=1.5), phase=50
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 250.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 0.0);

            Assert.Equal(150.0, loopUT);
            Assert.Equal(1, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void ZeroInterval_NeverInPause()
        {
            // With zero interval, cycleDuration == duration, so phaseInCycle is always < duration
            // (except at exact boundary). At exact cycle boundary, it wraps to next cycle with phase=0.
            // At UT 200, elapsed=100, cycle=1 (100/100=1), phase=0
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 200.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 0.0);

            Assert.Equal(100.0, loopUT);
            Assert.Equal(1, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void LargeInterval_LongPauseWindow()
        {
            // duration=100, interval=1000, cycleDuration=1100
            // At UT 300 (in pause), elapsed=200, cycle=0 (200/1100=0.18), phase=200 > duration=100
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 300.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 1000.0);

            Assert.Equal(200.0, loopUT);
            Assert.Equal(0, cycleIndex);
            Assert.True(isInPause);
        }
    }

    #endregion

    #region ShouldSpawnLoopedGhost Tests

    [Collection("Sequential")]
    public class ShouldSpawnLoopedGhost_Tests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ShouldSpawnLoopedGhost_Tests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        static Recording MakeLoopRec(
            bool loopPlayback = true,
            uint anchorPid = 12345,
            string anchorBodyName = "Kerbin")
        {
            return new Recording
            {
                VesselName = "TestLoop",
                LoopPlayback = loopPlayback,
                LoopAnchorVesselId = anchorPid,
                LoopAnchorBodyName = anchorBodyName,
            };
        }

        [Fact]
        public void Valid_LoopingAnchorSetExistsSameBody_ReturnsTrue()
        {
            var rec = MakeLoopRec();
            bool result = GhostPlaybackLogic.ShouldSpawnLoopedGhost(
                rec, anchorVesselExists: true, anchorBodyName: "Kerbin", recordingBodyName: "Kerbin");

            Assert.True(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("valid, returning true"));
        }

        [Fact]
        public void NotLooping_ReturnsFalse()
        {
            var rec = MakeLoopRec(loopPlayback: false);
            bool result = GhostPlaybackLogic.ShouldSpawnLoopedGhost(
                rec, anchorVesselExists: true, anchorBodyName: "Kerbin", recordingBodyName: "Kerbin");

            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("not looping"));
        }

        [Fact]
        public void NoAnchor_ReturnsFalse()
        {
            var rec = MakeLoopRec(anchorPid: 0);
            bool result = GhostPlaybackLogic.ShouldSpawnLoopedGhost(
                rec, anchorVesselExists: true, anchorBodyName: "Kerbin", recordingBodyName: "Kerbin");

            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("no anchor vessel"));
        }

        [Fact]
        public void AnchorMissing_ReturnsFalse()
        {
            var rec = MakeLoopRec();
            bool result = GhostPlaybackLogic.ShouldSpawnLoopedGhost(
                rec, anchorVesselExists: false, anchorBodyName: "Kerbin", recordingBodyName: "Kerbin");

            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("not found"));
        }

        [Fact]
        public void WrongBody_ReturnsFalseWithWarning()
        {
            var rec = MakeLoopRec();
            bool result = GhostPlaybackLogic.ShouldSpawnLoopedGhost(
                rec, anchorVesselExists: true, anchorBodyName: "Mun", recordingBodyName: "Kerbin");

            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("body mismatch") &&
                l.Contains("expected=Kerbin") && l.Contains("actual=Mun"));
        }

        [Fact]
        public void NullBodyNames_ReturnsTrue_NoValidationPossible()
        {
            var rec = MakeLoopRec(anchorBodyName: null);
            rec.LoopAnchorBodyName = null;
            bool result = GhostPlaybackLogic.ShouldSpawnLoopedGhost(
                rec, anchorVesselExists: true, anchorBodyName: null, recordingBodyName: null);

            Assert.True(result);
        }

        [Fact]
        public void EmptyRecordingBodyName_ReturnsTrue_Permissive()
        {
            var rec = MakeLoopRec();
            bool result = GhostPlaybackLogic.ShouldSpawnLoopedGhost(
                rec, anchorVesselExists: true, anchorBodyName: "Kerbin", recordingBodyName: "");

            Assert.True(result);
        }

        [Fact]
        public void EmptyAnchorBodyName_ReturnsTrue_Permissive()
        {
            var rec = MakeLoopRec();
            bool result = GhostPlaybackLogic.ShouldSpawnLoopedGhost(
                rec, anchorVesselExists: true, anchorBodyName: "", recordingBodyName: "Kerbin");

            Assert.True(result);
        }

        [Fact]
        public void NullRecording_ReturnsFalse()
        {
            bool result = GhostPlaybackLogic.ShouldSpawnLoopedGhost(
                null, anchorVesselExists: true, anchorBodyName: "Kerbin", recordingBodyName: "Kerbin");

            Assert.False(result);
        }
    }

    #endregion

    #region LoopAnchorBodyName Serialization Tests

    [Collection("Sequential")]
    public class LoopAnchorBodyName_Serialization_Tests : System.IDisposable
    {
        public LoopAnchorBodyName_Serialization_Tests()
        {
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void RoundTrip_ViaRecordingTree()
        {
            var source = new Recording
            {
                RecordingId = "body-test-tree",
                LoopPlayback = true,
                LoopAnchorVesselId = 42,
                LoopAnchorBodyName = "Kerbin",
            };
            source.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            source.Points.Add(new TrajectoryPoint
            {
                ut = 200, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });

            // Save via tree
            var tree = new RecordingTree { Id = "test-tree", TreeName = "Test" };
            tree.Recordings[source.RecordingId] = source;
            var treeNode = new ConfigNode("RECORDING_TREE");
            tree.Save(treeNode);

            // Load back
            var loadedTree = RecordingTree.Load(treeNode);
            Assert.Single(loadedTree.Recordings);
            Assert.Equal("Kerbin", loadedTree.Recordings["body-test-tree"].LoopAnchorBodyName);
        }

        [Fact]
        public void RoundTrip_ViaParseScenario()
        {
            var source = new Recording
            {
                RecordingId = "body-test-scenario",
                LoopPlayback = true,
                LoopAnchorVesselId = 42,
                LoopAnchorBodyName = "Mun",
            };

            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal("Mun", loaded.LoopAnchorBodyName);
        }

        [Fact]
        public void Sparse_NullNotWritten_RecordingTree()
        {
            var source = new Recording
            {
                RecordingId = "sparse-tree",
                LoopAnchorBodyName = null,
            };
            source.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            source.Points.Add(new TrajectoryPoint
            {
                ut = 200, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });

            var tree = new RecordingTree { Id = "sparse-test", TreeName = "Sparse" };
            tree.Recordings[source.RecordingId] = source;
            var treeNode = new ConfigNode("RECORDING_TREE");
            tree.Save(treeNode);

            // Check the saved node does not contain the key
            var recNodes = treeNode.GetNodes("RECORDING");
            Assert.True(recNodes.Length > 0);
            Assert.Null(recNodes[0].GetValue("loopAnchorBodyName"));
        }

        [Fact]
        public void Sparse_NullNotWritten_ParsekScenario()
        {
            var source = new Recording
            {
                RecordingId = "sparse-scenario",
                LoopAnchorBodyName = null,
            };

            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            Assert.Null(node.GetValue("loopAnchorBodyName"));
        }

        [Fact]
        public void BackwardCompat_MissingKey_ReturnsNull_RecordingTree()
        {
            // Simulate old save with no loopAnchorBodyName key
            var recNode = new ConfigNode("RECORDING");
            recNode.AddValue("recordingId", "old-rec");
            recNode.AddValue("loopPlayback", "True");
            recNode.AddValue("loopAnchorPid", "42");
            // No loopAnchorBodyName key

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(recNode, loaded);

            Assert.Null(loaded.LoopAnchorBodyName);
        }

        [Fact]
        public void BackwardCompat_MissingKey_ReturnsNull_ParsekScenario()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "old-rec-scenario");
            // No loopAnchorBodyName

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Null(loaded.LoopAnchorBodyName);
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_CopiesBodyName()
        {
            var source = new Recording
            {
                LoopAnchorBodyName = "Duna",
            };
            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal("Duna", target.LoopAnchorBodyName);
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_NullBodyName_CopiesNull()
        {
            var source = new Recording
            {
                LoopAnchorBodyName = null,
            };
            var target = new Recording { LoopAnchorBodyName = "Kerbin" };
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Null(target.LoopAnchorBodyName);
        }
    }

    #endregion
}
