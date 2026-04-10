using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #156: pad-failure threshold tests, commit approval, and CacheEngineModules null-safety.
    /// </summary>
    [Collection("Sequential")]
    public class Bug156_PadFailureThresholdTests : IDisposable
    {
        public Bug156_PadFailureThresholdTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ────────────────────────────────────────────────────────────
        //  IsPadFailure — pad-failure discard threshold
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void IsPadFailure_ShortDurationAndCloseDistance_ReturnsTrue()
        {
            // Bug #156: 5s duration, 20m from launch — both under thresholds (10s, 30m)
            Assert.True(ParsekFlight.IsPadFailure(5.0, 20.0));
        }

        [Fact]
        public void IsPadFailure_LongDuration_ReturnsFalse()
        {
            // Bug #156: 15s duration exceeds 10s threshold — not a pad failure
            Assert.False(ParsekFlight.IsPadFailure(15.0, 20.0));
        }

        [Fact]
        public void IsPadFailure_FarDistance_ReturnsFalse()
        {
            // Bug #156: 50m distance exceeds 30m threshold — not a pad failure
            Assert.False(ParsekFlight.IsPadFailure(5.0, 50.0));
        }

        [Fact]
        public void IsPadFailure_ExactThresholds_ReturnsFalse()
        {
            // Boundary: exactly 10s and 30m — strict less-than means not a pad failure
            Assert.False(ParsekFlight.IsPadFailure(10.0, 30.0));
        }

        [Fact]
        public void IsPadFailure_BothExceedThresholds_ReturnsFalse()
        {
            Assert.False(ParsekFlight.IsPadFailure(15.0, 50.0));
        }

        [Fact]
        public void IsPadFailure_ZeroDurationAndZeroDistance_ReturnsTrue()
        {
            // Edge case: immediate destruction at spawn point
            Assert.True(ParsekFlight.IsPadFailure(0.0, 0.0));
        }

        [Fact]
        public void IsPadFailure_DistanceJustUnderThreshold_ReturnsTrue()
        {
            Assert.True(ParsekFlight.IsPadFailure(5.0, 29.9));
        }

        // ────────────────────────────────────────────────────────────
        //  IsIdleOnPad — idle-on-pad discard (distance-only, no duration)
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void IsIdleOnPad_ZeroDistance_ReturnsTrue()
        {
            Assert.True(ParsekFlight.IsIdleOnPad(0.0));
        }

        [Fact]
        public void IsIdleOnPad_JustUnderThreshold_ReturnsTrue()
        {
            Assert.True(ParsekFlight.IsIdleOnPad(29.9));
        }

        [Fact]
        public void IsIdleOnPad_ExactThreshold_ReturnsFalse()
        {
            // Strict less-than: exactly 30m is NOT idle
            Assert.False(ParsekFlight.IsIdleOnPad(30.0));
        }

        [Fact]
        public void IsIdleOnPad_AboveThreshold_ReturnsFalse()
        {
            Assert.False(ParsekFlight.IsIdleOnPad(50.0));
        }

        [Fact]
        public void IsTreeIdleOnPad_AllIdleRecordings_ReturnsTrue()
        {
            var tree = new RecordingTree { Recordings = new Dictionary<string, Recording>() };
            tree.Recordings["a"] = new Recording { MaxDistanceFromLaunch = 5.0 };
            tree.Recordings["b"] = new Recording { MaxDistanceFromLaunch = 10.0 };
            Assert.True(ParsekFlight.IsTreeIdleOnPad(tree));
        }

        [Fact]
        public void IsTreeIdleOnPad_MixedRecordings_ReturnsFalse()
        {
            var tree = new RecordingTree { Recordings = new Dictionary<string, Recording>() };
            tree.Recordings["a"] = new Recording { MaxDistanceFromLaunch = 5.0 };
            tree.Recordings["b"] = new Recording { MaxDistanceFromLaunch = 100.0 };
            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
        }

        [Fact]
        public void IsTreeIdleOnPad_NullTree_ReturnsFalse()
        {
            Assert.False(ParsekFlight.IsTreeIdleOnPad(null));
        }

        [Fact]
        public void IsTreeIdleOnPad_EmptyTree_ReturnsFalse()
        {
            var tree = new RecordingTree { Recordings = new Dictionary<string, Recording>() };
            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
        }

        [Fact]
        public void IsTreeIdleOnPad_AfterConfigNodeRoundTrip_RetainsMaxDistance()
        {
            // #302: MaxDistanceFromLaunch must survive ConfigNode serialization.
            // Without this, deserialized trees default to 0.0 and get falsely
            // auto-discarded as "idle on pad".
            var rec = new Recording
            {
                RecordingId = "root",
                VesselName = "Kerbal X",
                MaxDistanceFromLaunch = 5000.0
            };

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);

            var tree = new RecordingTree { Recordings = new Dictionary<string, Recording>() };
            tree.Recordings["root"] = restored;

            // Should NOT be idle on pad — the rocket flew 5km
            Assert.False(ParsekFlight.IsTreeIdleOnPad(tree));
        }

        // ────────────────────────────────────────────────────────────
        //  ShouldShowCommitApproval — commit dialog trigger (#88)
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldShowCommitApproval_LandedAtKSC_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldShowCommitApproval(
                GameScenes.SPACECENTER, TerminalState.Landed));
        }

        [Fact]
        public void ShouldShowCommitApproval_SplashedAtKSC_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldShowCommitApproval(
                GameScenes.SPACECENTER, TerminalState.Splashed));
        }

        [Fact]
        public void ShouldShowCommitApproval_LandedAtTrackStation_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldShowCommitApproval(
                GameScenes.TRACKSTATION, TerminalState.Landed));
        }

        [Fact]
        public void ShouldShowCommitApproval_SplashedAtTrackStation_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldShowCommitApproval(
                GameScenes.TRACKSTATION, TerminalState.Splashed));
        }

        [Fact]
        public void ShouldShowCommitApproval_OrbitingAtKSC_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldShowCommitApproval(
                GameScenes.SPACECENTER, TerminalState.Orbiting));
        }

        [Fact]
        public void ShouldShowCommitApproval_LandedAtMainMenu_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldShowCommitApproval(
                GameScenes.MAINMENU, TerminalState.Landed));
        }

        [Fact]
        public void ShouldShowCommitApproval_LandedAtFlight_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldShowCommitApproval(
                GameScenes.FLIGHT, TerminalState.Landed));
        }

        [Fact]
        public void ShouldShowCommitApproval_NullTerminalState_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldShowCommitApproval(
                GameScenes.SPACECENTER, null));
        }

        // ────────────────────────────────────────────────────────────
        //  CacheEngineModules — null-vessel guard
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void CacheEngineModules_NullVessel_ReturnsEmptyList()
        {
            // Bug #156: null vessel must not crash, must return empty list
            var result = FlightRecorder.CacheEngineModules(null);
            Assert.Empty(result);
        }
    }
}
