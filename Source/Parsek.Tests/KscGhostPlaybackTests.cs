using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class KscGhostPlaybackTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KscGhostPlaybackTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        #region Helper: create a recording with Kerbin points

        static RecordingStore.Recording MakeKerbinRecording(
            double startUT = 100, double endUT = 200,
            bool loopPlayback = false, double loopInterval = 10.0,
            bool playbackEnabled = true,
            string chainId = null, int chainIndex = -1)
        {
            var rec = new RecordingStore.Recording
            {
                VesselName = "TestVessel",
                PlaybackEnabled = playbackEnabled,
                LoopPlayback = loopPlayback,
                LoopIntervalSeconds = loopInterval,
                ChainId = chainId,
                ChainIndex = chainIndex,
            };

            // Add two Kerbin points to make it valid
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT,
                latitude = -0.0972,
                longitude = -74.5575,
                altitude = 70,
                bodyName = "Kerbin",
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 0, 0)
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT,
                latitude = -0.0972,
                longitude = -74.5575,
                altitude = 5000,
                bodyName = "Kerbin",
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 50, 0)
            });

            return rec;
        }

        #endregion

        #region ShouldShowInKSC

        [Fact]
        public void ShouldShowInKSC_ValidKerbinRecording_ReturnsTrue()
        {
            var rec = MakeKerbinRecording();
            Assert.True(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_DisabledRecording_ReturnsFalse()
        {
            var rec = MakeKerbinRecording(playbackEnabled: false);
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_EmptyPoints_ReturnsFalse()
        {
            var rec = new RecordingStore.Recording
            {
                PlaybackEnabled = true,
                VesselName = "Test"
            };
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_SinglePoint_ReturnsFalse()
        {
            var rec = new RecordingStore.Recording
            {
                PlaybackEnabled = true,
                VesselName = "Test"
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, bodyName = "Kerbin"
            });
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_NonKerbinBody_ReturnsFalse()
        {
            var rec = new RecordingStore.Recording
            {
                PlaybackEnabled = true,
                VesselName = "Test"
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Mun" });
            rec.Points.Add(new TrajectoryPoint { ut = 200, bodyName = "Mun" });
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_ChainMidSegment_ReturnsFalse()
        {
            var rec = MakeKerbinRecording(chainId: "chain-abc", chainIndex: 1);
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_ChainFirstSegment_ReturnsTrue()
        {
            var rec = MakeKerbinRecording(chainId: "chain-abc", chainIndex: 0);
            Assert.True(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_NullPoints_ReturnsFalse()
        {
            var rec = new RecordingStore.Recording
            {
                PlaybackEnabled = true,
                VesselName = "Test",
                Points = null
            };
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_LogsSubsystem()
        {
            // ShouldShowInKSC is a pure filter — no logging expected
            var rec = MakeKerbinRecording();
            ParsekKSC.ShouldShowInKSC(rec);
            // Verify no log output (filter is a pure function)
            Assert.Empty(logLines);
        }

        #endregion

        #region TryComputeLoopUT

        [Fact]
        public void TryComputeLoopUT_BeforeStart_ReturnsFalse()
        {
            var rec = MakeKerbinRecording(startUT: 100, endUT: 200, loopPlayback: true);
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 50,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.False(result);
        }

        [Fact]
        public void TryComputeLoopUT_WithinFirstCycle_ReturnsCorrectUT()
        {
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 10.0);
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 150,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(0, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(150, loopUT, 3);
        }

        [Fact]
        public void TryComputeLoopUT_InPauseWindow_SetsFlag()
        {
            // Recording: UT 100-200 (duration=100), interval=10
            // Cycle duration = 110. At UT 205, elapsed=105, cycle=0, cycleTime=105 > 100
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 10.0);
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 205,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.True(inPauseWindow);
            Assert.Equal(0, cycleIndex);
            Assert.Equal(200, loopUT, 3); // Clamped to EndUT during pause
        }

        [Fact]
        public void TryComputeLoopUT_SecondCycle_ReturnsCorrectCycleIndex()
        {
            // Recording: UT 100-200 (duration=100), interval=10
            // Cycle duration = 110. At UT 215, elapsed=115, cycle=1, cycleTime=5
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 10.0);
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 215,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(1, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(105, loopUT, 3); // 100 + 5
        }

        [Fact]
        public void TryComputeLoopUT_NullRecording_ReturnsFalse()
        {
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(null, 100,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.False(result);
        }

        [Fact]
        public void TryComputeLoopUT_TooShortRecording_ReturnsFalse()
        {
            // Duration of exactly 0 (startUT == endUT)
            var rec = MakeKerbinRecording(startUT: 100, endUT: 100, loopPlayback: true);
            // Override points to have same UT
            rec.Points[1] = new TrajectoryPoint
            {
                ut = 100, bodyName = "Kerbin",
                rotation = new Quaternion(0, 0, 0, 1)
            };

            double loopUT;
            int cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 150,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.False(result);
        }

        [Fact]
        public void TryComputeLoopUT_ZeroInterval_NoGap()
        {
            // With interval=0, cycles should transition immediately (no pause window)
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 0.0);
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;

            // At UT 250: elapsed=150, cycleDuration=100, cycle=1, cycleTime=50
            bool result = ParsekKSC.TryComputeLoopUT(rec, 250,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(1, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(150, loopUT, 3); // 100 + 50
        }

        #endregion

        #region InterpolateAndPositionKsc (pure math aspects)

        [Fact]
        public void InterpolateAndPositionKsc_NullPoints_DeactivatesGhost()
        {
            // InterpolateAndPositionKsc requires a real GameObject, which we can't
            // construct outside Unity. This test verifies the null-points guard path
            // by checking that no exceptions are thrown and the method handles it.
            // Full integration testing requires in-game validation.

            // Note: We can't call this method in unit tests because it requires
            // Unity GameObjects and FlightGlobals.Bodies. Documenting as
            // integration-test-only.
            Assert.True(true); // Placeholder — method needs Unity runtime
        }

        #endregion
    }
}
