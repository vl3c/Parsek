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
            long cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 50,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.False(result);
        }

        [Fact]
        public void TryComputeLoopUT_AtExactStart_ReturnsTrue()
        {
            // #381: period=110 > duration=100 (single-ghost loop with 10s pause tail).
            var rec = MakeKerbinRecording(startUT: 100, endUT: 200, loopPlayback: true,
                loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
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
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
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
            // #381: duration=100, period=110 → cycleDuration=110.
            // At UT 205, elapsed=105, cycle=0, cycleTime=105 > duration=100 → pause window.
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 205,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.True(inPauseWindow);
            Assert.Equal(0, cycleIndex);
            Assert.Equal(200, loopUT, 3);
        }

        [Fact]
        public void TryComputeLoopUT_JustPastBoundaryWithinEpsilon_StaysInPlayback()
        {
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 200.0000005,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(0, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(200, loopUT, 3);
        }

        [Fact]
        public void TryComputeLoopUT_SecondCycle_ReturnsCorrectCycleIndex()
        {
            // #381: duration=100, period=110 → cycleDuration=110.
            // At UT 215, elapsed=115, cycle=1, cycleTime=5.
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
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
            long cycleIndex;
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
            long cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 150,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.False(result);
        }

        [Fact]
        public void TryComputeLoopUT_ZeroInterval_ClampsAndCycles()
        {
            // #443: period=0 clamps to MinCycleDuration=5. duration=100.
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 0.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            // At UT 250: elapsed=150, cycleDuration=5, cycle=floor(150/5)=30, cycleTime=0.
            bool result = ParsekKSC.TryComputeLoopUT(rec, 250,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(30, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(100, loopUT, 3);
        }

        [Fact]
        public void TryComputeLoopUT_LargeCycleJump_NoOverflow()
        {
            // Simulate time warp: currentUT far ahead produces large cycleIndex.
            // #381: period=110 > duration → single-ghost path.
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            // At UT 11100: elapsed=11000, cycleDuration=110, cycle=100, phase=0.
            bool result = ParsekKSC.TryComputeLoopUT(rec, 11100,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(100, cycleIndex);
            Assert.False(inPauseWindow);
        }

        [Fact]
        public void TryComputeLoopUT_AtEndOfCycle_NotInPauseWindow()
        {
            // #381: At exactly EndUT within a cycle (cycleTime == duration), should NOT be in pause.
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            // At UT 200: elapsed=100, cycle=0, cycleTime=100 == duration.
            // cycleTime > duration is false → not in pause.
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
            // #381: negative intervals are no longer allowed — clamp defensively to MinCycleDuration.
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = -50.0;
            Assert.Equal(GhostPlaybackLogic.MinCycleDuration, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_VeryNegativeValue_ClampedToMinCycleDuration()
        {
            // #381: clamp applies regardless of magnitude.
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = -200.0;
            Assert.Equal(GhostPlaybackLogic.MinCycleDuration, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_PositiveValue_ReturnsAsIs()
        {
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = 15.0;
            Assert.Equal(15.0, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_Zero_ClampsToMinCycleDuration()
        {
            // #381: zero is also below MinCycleDuration.
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = 0.0;
            Assert.Equal(GhostPlaybackLogic.MinCycleDuration, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        #endregion

        #region TryComputeLoopUT with #381 period semantics

        [Fact]
        public void TryComputeLoopUT_PeriodShorterThanDuration_Overlaps()
        {
            // #381: period=30 < duration=100 → cycleDuration=30 (overlap via single-ghost math).
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 30.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            // At UT 160: elapsed=60, cycleDuration=30, cycle=2, cycleTime=0.
            bool result = ParsekKSC.TryComputeLoopUT(rec, 160,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(2, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(100, loopUT, 3);
        }

        [Fact]
        public void EffectiveLoopDuration_SubrangeAndFullRange_OverlapDecisionDiffers()
        {
            // #411 regression guard: the KSC dispatcher must branch on the effective loop
            // subrange, not the recording's full [StartUT, EndUT] span.
            var rec = MakeKerbinRecording(
                startUT: 0, endUT: 300, loopPlayback: true, loopInterval: 150.0);
            rec.LoopStartUT = 100;
            rec.LoopEndUT = 200;

            double effectiveDuration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
            double rawDuration = rec.EndUT - rec.StartUT;

            Assert.True(GhostPlaybackLogic.IsOverlapLoop(rec.LoopIntervalSeconds, rawDuration));
            Assert.False(GhostPlaybackLogic.IsOverlapLoop(rec.LoopIntervalSeconds, effectiveDuration));
        }

        [Fact]
        public void ResolveLoopPlaybackEndpointUT_Subrange_UsesEffectiveLoopEnd()
        {
            var rec = MakeKerbinRecording(
                startUT: 0, endUT: 300, loopPlayback: true, loopInterval: 80.0);
            rec.LoopStartUT = 100;
            rec.LoopEndUT = 200;

            Assert.Equal(200.0, GhostPlaybackEngine.ResolveLoopPlaybackEndpointUT(rec));
        }

        [Fact]
        public void EffectiveLoopDuration_SubrangeChangesOverlapCycleBoundsComparedToFullRange()
        {
            // #411 regression guard: UpdateOverlapKsc must anchor both active-cycle bounds
            // and phase math to the effective loop range, otherwise cycles start from the
            // recording's raw StartUT and stale overlap ghosts linger too long.
            var rec = MakeKerbinRecording(
                startUT: 0, endUT: 300, loopPlayback: true, loopInterval: 80.0);
            rec.LoopStartUT = 100;
            rec.LoopEndUT = 200;

            long effectiveFirstCycle;
            long effectiveLastCycle;
            long rawFirstCycle;
            long rawLastCycle;

            GhostPlaybackLogic.GetActiveCycles(260,
                GhostPlaybackEngine.EffectiveLoopStartUT(rec),
                GhostPlaybackEngine.EffectiveLoopEndUT(rec),
                ParsekKSC.GetLoopIntervalSeconds(rec),
                10, out effectiveFirstCycle, out effectiveLastCycle);
            GhostPlaybackLogic.GetActiveCycles(260,
                rec.StartUT, rec.EndUT,
                ParsekKSC.GetLoopIntervalSeconds(rec),
                10, out rawFirstCycle, out rawLastCycle);

            Assert.Equal(1, effectiveFirstCycle);
            Assert.Equal(2, effectiveLastCycle);
            Assert.Equal(0, rawFirstCycle);
            Assert.Equal(3, rawLastCycle);
        }

        [Fact]
        public void TryComputeLoopUT_NegativeInterval_ClampsDefensively_NoThrow()
        {
            // #443: negative intervals are rejected at UI; engine clamps defensively to
            // MinCycleDuration=5. Must not throw.
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: -30.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            // Clamped period=5, duration=100. At UT 250, elapsed=150, cycle=floor(150/5)=30, phase=0.
            bool result = ParsekKSC.TryComputeLoopUT(rec, 250,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.False(inPauseWindow);
            Assert.Equal(30, cycleIndex);
            Assert.Equal(100, loopUT, 3);
        }

        #endregion
    }
}
