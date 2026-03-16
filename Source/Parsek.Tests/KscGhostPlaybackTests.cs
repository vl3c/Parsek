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

        #region Helper: create recordings

        static Recording MakeKerbinRecording(
            double startUT = 100, double endUT = 200,
            bool loopPlayback = false, double loopInterval = 10.0,
            bool playbackEnabled = true,
            string chainId = null, int chainIndex = -1,
            string bodyName = "Kerbin",
            TerminalState? terminalState = null)
        {
            var rec = new Recording
            {
                VesselName = "TestVessel",
                PlaybackEnabled = playbackEnabled,
                LoopPlayback = loopPlayback,
                LoopIntervalSeconds = loopInterval,
                ChainId = chainId,
                ChainIndex = chainIndex,
                TerminalStateValue = terminalState,
            };

            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT,
                latitude = -0.0972,
                longitude = -74.5575,
                altitude = 70,
                bodyName = bodyName,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 0, 0)
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT,
                latitude = -0.0972,
                longitude = -74.5575,
                altitude = 5000,
                bodyName = bodyName,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 50, 0)
            });

            return rec;
        }

        /// <summary>
        /// Create a recording that starts on Kerbin then transitions to Mun.
        /// </summary>
        static Recording MakeCrossBodyRecording()
        {
            var rec = new Recording
            {
                VesselName = "MunTransfer",
                PlaybackEnabled = true,
                LoopPlayback = false,
            };

            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = -0.0972, longitude = -74.5575, altitude = 70,
                bodyName = "Kerbin", rotation = new Quaternion(0, 0, 0, 1)
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200, latitude = -0.0972, longitude = -74.5575, altitude = 70000,
                bodyName = "Kerbin", rotation = new Quaternion(0, 0, 0, 1)
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 300, latitude = 5.0, longitude = 30.0, altitude = 10000,
                bodyName = "Mun", rotation = new Quaternion(0, 0, 0, 1)
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
            var rec = new Recording
            {
                PlaybackEnabled = true,
                VesselName = "Test"
            };
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_SinglePoint_ReturnsFalse()
        {
            var rec = new Recording
            {
                PlaybackEnabled = true,
                VesselName = "Test"
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_NonKerbinBody_ReturnsFalse()
        {
            var rec = MakeKerbinRecording(bodyName: "Mun");
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_ChainMidSegment_ReturnsTrue()
        {
            // Chain segments play independently — same behavior as flight scene
            var rec = MakeKerbinRecording(chainId: "chain-abc", chainIndex: 1);
            Assert.True(ParsekKSC.ShouldShowInKSC(rec));
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
            var rec = new Recording
            {
                PlaybackEnabled = true,
                VesselName = "Test",
                Points = null
            };
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_DestroyedRecording_StillShows()
        {
            // Destroyed recordings are valid for KSC display — they play then explode
            var rec = MakeKerbinRecording(terminalState: TerminalState.Destroyed);
            Assert.True(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_CrossBodyRecording_PassesFilter()
        {
            // First point is Kerbin so it passes filter. Ghost will hide
            // when trajectory reaches Mun points (handled by InterpolateAndPositionKsc).
            var rec = MakeCrossBodyRecording();
            Assert.True(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_IsPureFunction_NoLogging()
        {
            var rec = MakeKerbinRecording();
            ParsekKSC.ShouldShowInKSC(rec);
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
        public void TryComputeLoopUT_AtExactStart_ReturnsTrue()
        {
            var rec = MakeKerbinRecording(startUT: 100, endUT: 200, loopPlayback: true,
                loopInterval: 10.0);
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 100,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(0, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(100, loopUT, 3);
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
            Assert.Equal(200, loopUT, 3);
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
            Assert.Equal(105, loopUT, 3);
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
            var rec = MakeKerbinRecording(startUT: 100, endUT: 100, loopPlayback: true);
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
            Assert.Equal(150, loopUT, 3);
        }

        [Fact]
        public void TryComputeLoopUT_LargeCycleJump_NoOverflow()
        {
            // Simulate time warp: currentUT far ahead produces large cycleIndex
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 10.0);
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;

            // At UT 11100: elapsed=11000, cycleDuration=110, cycle=100
            bool result = ParsekKSC.TryComputeLoopUT(rec, 11100,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(100, cycleIndex);
            Assert.False(inPauseWindow);
        }

        [Fact]
        public void TryComputeLoopUT_AtEndOfCycle_NotInPauseWindow()
        {
            // At exactly EndUT within a cycle (cycleTime == duration), should NOT be in pause
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 10.0);
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;

            // At UT 200: elapsed=100, cycle=0, cycleTime=100 == duration
            // intervalSeconds > 0 && cycleTime > duration? 100 > 100 is false → not in pause
            bool result = ParsekKSC.TryComputeLoopUT(rec, 200,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(0, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(200, loopUT, 3);
        }

        #endregion

        #region GetLoopIntervalSeconds

        [Fact]
        public void GetLoopIntervalSeconds_NullRecording_ReturnsDefault()
        {
            Assert.Equal(10.0, ParsekKSC.GetLoopIntervalSeconds(null));
        }

        [Fact]
        public void GetLoopIntervalSeconds_NaN_ReturnsDefault()
        {
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = double.NaN;
            Assert.Equal(10.0, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_Infinity_ReturnsDefault()
        {
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = double.PositiveInfinity;
            Assert.Equal(10.0, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_NegativeValue_ClampedToMinCycleDuration()
        {
            // Negative intervals shorten cycle duration. Clamped so cycleDuration >= MinCycleDuration.
            // duration = 200-100 = 100, interval = -50 → returned as -50 (cycleDuration = 50)
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = -50.0;
            Assert.Equal(-50.0, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_VeryNegativeValue_ClampedToPreventZeroCycle()
        {
            // duration = 100, interval = -200 → clamped to -100 + epsilon = -99.999
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = -200.0;
            double result = ParsekKSC.GetLoopIntervalSeconds(rec);
            // cycleDuration = 100 + result should be >= MinCycleDuration (0.001)
            Assert.True(100.0 + result >= 0.001);
        }

        [Fact]
        public void GetLoopIntervalSeconds_PositiveValue_ReturnsAsIs()
        {
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = 15.0;
            Assert.Equal(15.0, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_Zero_ReturnsZero()
        {
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = 0.0;
            Assert.Equal(0.0, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        #endregion

        #region TryComputeLoopUT with negative interval (clamped to 0)

        [Fact]
        public void TryComputeLoopUT_NegativeInterval_ShorterCycles()
        {
            // Negative interval = -30 → cycleDuration = 100 + (-30) = 70
            // Shorter cycles = more frequent relaunches (overlap-style)
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: -30.0);
            double loopUT;
            int cycleIndex;
            bool inPauseWindow;

            // At UT 250: elapsed=150, cycleDuration=70, cycle=2, cycleTime=150-140=10
            bool result = ParsekKSC.TryComputeLoopUT(rec, 250,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(2, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(110, loopUT, 3); // 100 + 10
        }

        #endregion
    }
}
