using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FlightPlaybackExplainabilityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FlightPlaybackExplainabilityTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording MakeRecording(
            string recordingId,
            string vesselName = "Ghost",
            bool playbackEnabled = true)
        {
            return new Recording
            {
                RecordingId = recordingId,
                VesselName = vesselName,
                PlaybackEnabled = playbackEnabled,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 101.0 }
                }
            };
        }

        [Fact]
        public void GhostPlaybackSkipReasonTokens_AreStable()
        {
            Assert.Equal("none", GhostPlaybackSkipReason.None.ToLogToken());
            Assert.Equal("no-renderable-data", GhostPlaybackSkipReason.NoRenderableData.ToLogToken());
            Assert.Equal("playback-disabled", GhostPlaybackSkipReason.PlaybackDisabled.ToLogToken());
            Assert.Equal("external-vessel-suppressed", GhostPlaybackSkipReason.ExternalVesselSuppressed.ToLogToken());
            Assert.Equal("before-activation", GhostPlaybackSkipReason.BeforeActivation.ToLogToken());
            Assert.Equal("anchor-missing", GhostPlaybackSkipReason.AnchorMissing.ToLogToken());
            Assert.Equal("loop-sync-failed", GhostPlaybackSkipReason.LoopSyncFailed.ToLogToken());
            Assert.Equal("parent-loop-paused", GhostPlaybackSkipReason.ParentLoopPaused.ToLogToken());
            Assert.Equal("warp-hidden", GhostPlaybackSkipReason.WarpHidden.ToLogToken());
            Assert.Equal("visual-load-failed", GhostPlaybackSkipReason.VisualLoadFailed.ToLogToken());
            Assert.Equal("session-suppressed", GhostPlaybackSkipReason.SessionSuppressed.ToLogToken());
        }

        [Fact]
        public void ResolveGhostPlaybackSkipReason_PrioritizesExplicitReasons()
        {
            Assert.Equal(
                GhostPlaybackSkipReason.NoRenderableData,
                ParsekFlight.ResolveGhostPlaybackSkipReason(
                    hasRenderableData: false,
                    playbackEnabled: false,
                    externalVesselSuppressed: true));
            Assert.Equal(
                GhostPlaybackSkipReason.PlaybackDisabled,
                ParsekFlight.ResolveGhostPlaybackSkipReason(
                    hasRenderableData: true,
                    playbackEnabled: false,
                    externalVesselSuppressed: true));
            Assert.Equal(
                GhostPlaybackSkipReason.ExternalVesselSuppressed,
                ParsekFlight.ResolveGhostPlaybackSkipReason(
                    hasRenderableData: true,
                    playbackEnabled: true,
                    externalVesselSuppressed: true));
            Assert.Equal(
                GhostPlaybackSkipReason.None,
                ParsekFlight.ResolveGhostPlaybackSkipReason(
                    hasRenderableData: true,
                    playbackEnabled: true,
                    externalVesselSuppressed: false));
        }

        [Fact]
        public void LogGhostSkipReasonChangeForTesting_LogsOnlyOnReasonChange()
        {
            ParsekFlight.LogGhostSkipReasonChangeForTesting(
                4,
                "rec-skip",
                "Skip Vessel",
                GhostPlaybackSkipReason.NoRenderableData,
                hasRenderableData: false,
                playbackEnabled: true,
                externalVesselSuppressed: false);
            ParsekFlight.LogGhostSkipReasonChangeForTesting(
                4,
                "rec-skip",
                "Skip Vessel",
                GhostPlaybackSkipReason.NoRenderableData,
                hasRenderableData: false,
                playbackEnabled: true,
                externalVesselSuppressed: false);
            ParsekFlight.LogGhostSkipReasonChangeForTesting(
                4,
                "rec-skip",
                "Skip Vessel",
                GhostPlaybackSkipReason.PlaybackDisabled,
                hasRenderableData: true,
                playbackEnabled: false,
                externalVesselSuppressed: false);

            Assert.Equal(
                1,
                logLines.Count(line => line.Contains("reason=no-renderable-data")));
            Assert.Contains(
                logLines,
                line => line.Contains("reason=playback-disabled")
                    && line.Contains("suppressed=1"));
        }

        [Fact]
        public void FrameSummary_IncludesAggregateSkipCounters()
        {
            var counters = new GhostPlaybackFrameCounters
            {
                spawned = 1,
                destroyed = 2,
                deferred = 3,
                beforeActivation = 4,
                anchorMissing = 5,
                loopSyncFailed = 6,
                parentLoopPaused = 7,
                warpHidden = 8,
                visualLoadFailed = 9,
                noRenderableData = 10,
                sessionSuppressed = 11,
                active = 12
            };

            string message = GhostPlaybackEngine.BuildFrameSummaryMessage(counters);

            Assert.True(GhostPlaybackEngine.ShouldEmitFrameSummary(counters));
            Assert.Contains("spawned=1", message);
            Assert.Contains("destroyed=2", message);
            Assert.Contains("deferred=3", message);
            Assert.Contains("beforeActivation=4", message);
            Assert.Contains("anchorMissing=5", message);
            Assert.Contains("loopSyncFailed=6", message);
            Assert.Contains("parentLoopPaused=7", message);
            Assert.Contains("warpHidden=8", message);
            Assert.Contains("visualLoadFailed=9", message);
            Assert.Contains("noRenderableData=10", message);
            Assert.Contains("sessionSuppressed=11", message);
            Assert.Contains("active=12", message);
        }

        [Fact]
        public void FrameSummary_DoesNotEmitForStableActiveOnlyFrame()
        {
            var counters = new GhostPlaybackFrameCounters { active = 3 };

            Assert.False(GhostPlaybackEngine.ShouldEmitFrameSummary(counters));
        }

        [Fact]
        public void FastForwardWatchHandoff_ClassifiesActiveGhostAsReady()
        {
            var recordings = new List<Recording> { MakeRecording("rec-a", "Vessel A") };
            var flags = new[]
            {
                new TrajectoryPlaybackFlags { skipReason = GhostPlaybackSkipReason.None }
            };

            ParsekFlight.FastForwardWatchHandoffDecision decision =
                ParsekFlight.ClassifyFastForwardWatchHandoff(
                    "rec-a",
                    recordings,
                    flags,
                    index => index == 0);

            Assert.True(decision.canEnterWatch);
            Assert.False(decision.warn);
            Assert.Equal(0, decision.index);
            Assert.Equal("active-ghost", decision.reason);
            Assert.Contains("Deferred FF watch handoff ready",
                ParsekFlight.BuildFastForwardWatchHandoffMessage(decision, 123.4));
        }

        [Fact]
        public void FastForwardWatchHandoff_ReportsSkipReasonWhenGhostMissing()
        {
            var recordings = new List<Recording> { MakeRecording("rec-b", "Vessel B") };
            var flags = new[]
            {
                new TrajectoryPlaybackFlags { skipReason = GhostPlaybackSkipReason.NoRenderableData }
            };

            ParsekFlight.FastForwardWatchHandoffDecision decision =
                ParsekFlight.ClassifyFastForwardWatchHandoff(
                    "rec-b",
                    recordings,
                    flags,
                    index => false);

            Assert.False(decision.canEnterWatch);
            Assert.True(decision.warn);
            Assert.Equal("no-renderable-data", decision.reason);
            Assert.Contains("skipReason=no-renderable-data",
                ParsekFlight.BuildFastForwardWatchHandoffMessage(decision, 123.4));
        }

        [Fact]
        public void FastForwardWatchHandoff_StaleRecordingIsQuietFailure()
        {
            ParsekFlight.FastForwardWatchHandoffDecision decision =
                ParsekFlight.ClassifyFastForwardWatchHandoff(
                    "missing",
                    new List<Recording> { MakeRecording("rec-c", "Vessel C") },
                    flags: null,
                    hasActiveGhost: index => true);

            Assert.False(decision.canEnterWatch);
            Assert.False(decision.warn);
            Assert.False(decision.recordingExists);
            Assert.Equal("stale-recording-id", decision.reason);
        }
    }
}
