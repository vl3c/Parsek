using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    #region ComputeLoopPhaseFromUT Tests

    public class ComputeLoopPhaseFromUT_Tests
    {
        // #381 semantics: cycleDuration = max(intervalSeconds, MinCycleDuration=1).
        // The helper does not own overlap dispatch — callers use IsOverlapLoop for that.
        // These tests use a duration=100s recording with period > duration (classic single-ghost
        // loop with a pause tail) unless otherwise noted.

        [Fact]
        public void BeforeRecordingStart_ReturnsStartUTCycle0NotPaused()
        {
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 50.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 110.0);

            Assert.Equal(100.0, loopUT);
            Assert.Equal(0, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void AtRecordingStart_ReturnsPhase0Cycle0NotPaused()
        {
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 100.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 110.0);

            Assert.Equal(100.0, loopUT);
            Assert.Equal(0, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void MidwayThroughFirstCycle_ReturnsCorrectLoopUT()
        {
            // duration=100, period=110, cycleDuration=110
            // At UT 150, elapsed=50, phase=50 within playback portion
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 150.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 110.0);

            Assert.Equal(150.0, loopUT);
            Assert.Equal(0, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void AtRecordingEnd_BoundaryEntersPause()
        {
            // duration=100, period=110. elapsed=100, phase=100 == duration → pause branch.
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 200.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 110.0);

            Assert.Equal(200.0, loopUT);
            Assert.Equal(0, cycleIndex);
            Assert.True(isInPause);
        }

        [Fact]
        public void InPauseInterval_ReturnsPausedAtEndUT()
        {
            // duration=100, period=110, cycleDuration=110
            // At UT 205, elapsed=105, phase=105 > duration=100 → in pause
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 205.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 110.0);

            Assert.Equal(200.0, loopUT);
            Assert.Equal(0, cycleIndex);
            Assert.True(isInPause);
        }

        [Fact]
        public void StartOfSecondCycle_ReturnsCycle1NearStartUT()
        {
            // duration=100, period=110, cycleDuration=110
            // At UT 210, elapsed=110 → cycle 1, phaseInCycle=0
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 210.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 110.0);

            Assert.Equal(100.0, loopUT);
            Assert.Equal(1, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void MidwayThroughSecondCycle_CorrectPhase()
        {
            // duration=100, period=110, cycleDuration=110
            // At UT 260, elapsed=160, cycle=1, phaseInCycle=50
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 260.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 110.0);

            Assert.Equal(150.0, loopUT);
            Assert.Equal(1, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void ManyCyclesElapsed_CorrectCycleIndexAndPhase()
        {
            // duration=100, period=110, cycleDuration=110
            // 1000 cycles: UT = 100 + 1000*110 = 110100; +50 phase into cycle 1000 → UT=110150
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 110150.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 110.0);

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
        public void NegativeInterval_ClampsToMinCycleDuration()
        {
            // #381: negative intervals no longer mean "zero-pause" (old behavior). They
            // clamp to MinCycleDuration = 1s (extreme overlap semantics). The phase is
            // computed as if period=1; overlap dispatch is someone else's problem.
            // duration=100, period=-5 → cycleDuration=1. elapsed=150 → cycle=150, phase=0.
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 250.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: -5.0);

            Assert.Equal(100.0, loopUT);
            Assert.Equal(150, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void ZeroInterval_ClampsToMinCycleDuration()
        {
            // #381: period=0 is below MinCycleDuration=1, so cycleDuration=1.
            // duration=100, elapsed=150 → cycle=150, phase=0.
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 250.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 0.0);

            Assert.Equal(100.0, loopUT);
            Assert.Equal(150, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void ZeroInterval_NeverInPause_AtRecordingEnd()
        {
            // #381: period clamps to 1, period <= duration so no pause window.
            // At UT 200, elapsed=100, cycleDuration=1 → cycle=100, phase=0.
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 200.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 0.0);

            Assert.Equal(100.0, loopUT);
            Assert.Equal(100, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void LargeInterval_LongPauseWindow()
        {
            // duration=100, period=1000 → cycleDuration=1000. Pause tail = 900s.
            // At UT 300 (in pause), elapsed=200, cycle=0, phase=200 > duration=100 → pause.
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 300.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 1000.0);

            Assert.Equal(200.0, loopUT);
            Assert.Equal(0, cycleIndex);
            Assert.True(isInPause);
        }

        [Fact]
        public void PeriodShorterThanDuration_OverlapsButNoPause()
        {
            // #381: period=30, duration=100. currentUT=150, start=100. elapsed=50.
            // cycleDuration=30. cycleIndex=floor(50/30)=1. phase=50-30=20.
            // phase (20) < duration (100) → not in pause. loopUT=100+20=120.
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 150.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 30.0);

            Assert.Equal(120.0, loopUT);
            Assert.Equal(1, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void PeriodEqualToDuration_BackToBack()
        {
            // #381: period=100, duration=100. currentUT=250, start=100. elapsed=150.
            // cycleDuration=100. cycle=1. phase=50. loopUT=150. No pause.
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 250.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 100.0);

            Assert.Equal(150.0, loopUT);
            Assert.Equal(1, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void PeriodGreaterThanDuration_HasPause()
        {
            // #381: period=150, duration=100. currentUT=220, start=100. elapsed=120.
            // cycleDuration=150. cycle=0. phase=120. phase > duration → pause. loopUT=200.
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT: 220.0, recordingStartUT: 100.0, recordingEndUT: 200.0, intervalSeconds: 150.0);

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
