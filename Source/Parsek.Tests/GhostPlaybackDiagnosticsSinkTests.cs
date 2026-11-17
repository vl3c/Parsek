using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The playback engine counts its ghost lifecycle health events and publishes its frame
    /// budget through an <see cref="IGhostPlaybackDiagnosticsSink"/>. The global sink is the
    /// player's own engine's: each count member moves exactly one
    /// <see cref="DiagnosticsState.health"/> counter by one, the budget members write
    /// <see cref="DiagnosticsState.playbackBudget"/> and the frame history, and the threshold
    /// member is the existing budget check. An engine given another sink leaves the global
    /// state alone.
    /// </summary>
    [Collection("Sequential")]
    public class GhostPlaybackDiagnosticsSinkTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostPlaybackDiagnosticsSinkTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            DiagnosticsState.ResetForTesting();
            DiagnosticsComputation.ResetForTesting();
        }

        public void Dispose()
        {
            DiagnosticsState.ResetForTesting();
            DiagnosticsComputation.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private sealed class CountingSink : IGhostPlaybackDiagnosticsSink
        {
            internal readonly List<string> Calls = new List<string>();
            public void CountGhostBuild() => Calls.Add("build");
            public void CountReentryFxDeferred() => Calls.Add("reentryDeferred");
            public void CountReentryFxSkipped() => Calls.Add("reentrySkipped");
            public void CountReentryFxBuild() => Calls.Add("reentryBuild");
            public void CountGhostReusedAcrossCycle() => Calls.Add("reused");
            public void CountGhostDestroy() => Calls.Add("destroy");
            public void ClearPlaybackBudget() => Calls.Add("clearBudget");
            public void PublishPlaybackBudget(
                long totalMicroseconds, long spawnMicroseconds, long destroyMicroseconds,
                int ghostsProcessed, float warpRate, GhostObservability ghostObservability)
                => Calls.Add("publishBudget");
            public void AppendPlaybackFrame(float realtime, long totalMicroseconds)
                => Calls.Add("appendFrame");
            public void CheckPlaybackBudgetThreshold(
                long totalMicroseconds, int ghostsProcessed, float warpRate, PlaybackBudgetPhases phases)
                => Calls.Add("checkThreshold");
        }

        private static void AssertOnlyFieldMoved(string field)
        {
            HealthCounters h = DiagnosticsState.health;
            var values = new Dictionary<string, int>
            {
                { "ghostBuildsThisSession", h.ghostBuildsThisSession },
                { "reentryFxDeferredThisSession", h.reentryFxDeferredThisSession },
                { "reentryFxSkippedThisSession", h.reentryFxSkippedThisSession },
                { "reentryFxBuildsThisSession", h.reentryFxBuildsThisSession },
                { "ghostReusedAcrossCycleThisSession", h.ghostReusedAcrossCycleThisSession },
                { "ghostDestroysThisSession", h.ghostDestroysThisSession },
                { "waypointCacheHits", h.waypointCacheHits },
                { "waypointCacheMisses", h.waypointCacheMisses },
                { "snapshotRefreshSpikes", h.snapshotRefreshSpikes },
                { "spawnFailures", h.spawnFailures },
                { "spawnRetries", h.spawnRetries },
                { "gcGen0Baseline", h.gcGen0Baseline },
            };
            foreach (var kv in values)
                Assert.True(kv.Value == (kv.Key == field ? 1 : 0),
                    $"{kv.Key}={kv.Value} after counting {field}");
        }

        [Theory]
        [InlineData("ghostBuildsThisSession")]
        [InlineData("reentryFxDeferredThisSession")]
        [InlineData("reentryFxSkippedThisSession")]
        [InlineData("reentryFxBuildsThisSession")]
        [InlineData("ghostReusedAcrossCycleThisSession")]
        [InlineData("ghostDestroysThisSession")]
        public void GlobalSink_EachMemberMovesExactlyItsOwnCounterByOne(string field)
        {
            var sink = new GlobalGhostPlaybackDiagnosticsSink();
            switch (field)
            {
                case "ghostBuildsThisSession": sink.CountGhostBuild(); break;
                case "reentryFxDeferredThisSession": sink.CountReentryFxDeferred(); break;
                case "reentryFxSkippedThisSession": sink.CountReentryFxSkipped(); break;
                case "reentryFxBuildsThisSession": sink.CountReentryFxBuild(); break;
                case "ghostReusedAcrossCycleThisSession": sink.CountGhostReusedAcrossCycle(); break;
                case "ghostDestroysThisSession": sink.CountGhostDestroy(); break;
                default: throw new ArgumentException(field);
            }

            AssertOnlyFieldMoved(field);
        }

        [Fact]
        public void GlobalSink_AddsToTheExistingCount()
        {
            DiagnosticsState.health.ghostDestroysThisSession = 41;
            new GlobalGhostPlaybackDiagnosticsSink().CountGhostDestroy();
            Assert.Equal(42, DiagnosticsState.health.ghostDestroysThisSession);
        }

        [Fact]
        public void DefaultEngine_DestroyGhost_CountsIntoTheGlobalHealthCounters()
        {
            // The resource teardown is Unity-only; stub it so the count is all this test reaches.
            var engine = new GhostPlaybackEngine(positioner: null) { DestroyGhostResourcesOverrideForTesting = s => { } };
            engine.ghostStates[0] = new GhostPlaybackState { vesselName = "Sink Probe" };

            engine.DestroyGhost(0, reason: "sink test");

            AssertOnlyFieldMoved("ghostDestroysThisSession");
            Assert.Contains(logLines, l => l.Contains("[Engine]") && l.Contains("GhostPlaybackEngine created"));
        }

        [Fact]
        public void InjectedSink_DestroyGhost_CountsIntoTheSinkNotTheGlobalCounters()
        {
            var sink = new CountingSink();
            var engine = new GhostPlaybackEngine(null, sink) { DestroyGhostResourcesOverrideForTesting = s => { } };
            engine.ghostStates[0] = new GhostPlaybackState { vesselName = "Sink Probe" };

            engine.DestroyGhost(0, reason: "sink test");

            Assert.Equal(new[] { "destroy" }, sink.Calls);
            Assert.Equal(0, DiagnosticsState.health.ghostDestroysThisSession);
            Assert.Contains(logLines, l => l.Contains("[Engine]") && l.Contains("GhostPlaybackEngine created"));
        }

        [Fact]
        public void InjectedSink_DestroyOfAnEmptySlot_CountsNothing()
        {
            var sink = new CountingSink();
            var engine = new GhostPlaybackEngine(null, sink);

            engine.DestroyGhost(7, reason: "nothing there");

            Assert.Empty(sink.Calls);
        }

        [Fact]
        public void InjectedSink_ReuseOfANullGhost_CountsNothing()
        {
            var sink = new CountingSink();
            var engine = new GhostPlaybackEngine(null, sink);
            var state = new GhostPlaybackState { ghost = null };

            engine.ReusePrimaryGhostAcrossCycle(
                index: 3, traj: null, flags: default, state, playbackUT: 0, newCycleIndex: 1);

            Assert.Empty(sink.Calls);
            Assert.Equal(0, DiagnosticsState.health.ghostReusedAcrossCycleThisSession);
        }

        [Fact]
        public void GlobalSink_PublishPlaybackBudget_WritesTheSixBudgetFields()
        {
            var obs = new GhostObservability
            {
                activePrimaryGhostCount = 3,
                activeOverlapGhostCount = 1,
                zone2GhostCount = 2,
                rcsParticleSystemCount = 7,
            };

            new GlobalGhostPlaybackDiagnosticsSink().PublishPlaybackBudget(
                totalMicroseconds: 12_345,
                spawnMicroseconds: 2_100,
                destroyMicroseconds: 450,
                ghostsProcessed: 4,
                warpRate: 50f,
                ghostObservability: obs);

            FrameBudget b = DiagnosticsState.playbackBudget;
            Assert.Equal(12_345L, b.totalMicroseconds);
            Assert.Equal(2_100L, b.spawnMicroseconds);
            Assert.Equal(450L, b.destroyMicroseconds);
            Assert.Equal(4, b.ghostsProcessed);
            Assert.Equal(50f, b.warpRate);
            Assert.Equal(obs, b.ghostObservability);
            Assert.Equal(default(FrameBudget), DiagnosticsState.recordingBudget);
            Assert.True(DiagnosticsState.playbackFrameHistory.IsEmpty);
        }

        [Fact]
        public void GlobalSink_ClearPlaybackBudget_ReturnsTheBudgetToEmpty()
        {
            var sink = new GlobalGhostPlaybackDiagnosticsSink();
            sink.PublishPlaybackBudget(9_000, 100, 200, 5, 4f,
                new GhostObservability { activePrimaryGhostCount = 5 });
            DiagnosticsState.playbackFrameHistory.Append(1.0, 9_000);

            sink.ClearPlaybackBudget();

            Assert.Equal(default(FrameBudget), DiagnosticsState.playbackBudget);
            // The clear touches the budget only; the history keeps its sample.
            Assert.Equal(1, DiagnosticsState.playbackFrameHistory.Count);
        }

        [Fact]
        public void GlobalSink_AppendPlaybackFrame_AddsOneHistorySample()
        {
            new GlobalGhostPlaybackDiagnosticsSink().AppendPlaybackFrame(10.5f, 6_000);

            Assert.Equal(1, DiagnosticsState.playbackFrameHistory.Count);
            DiagnosticsState.playbackFrameHistory.ComputeStats(
                10.5, 4.0, out double avgMs, out double peakMs, out _, out int entries);
            Assert.Equal(1, entries);
            Assert.Equal(6.0, avgMs, 6);
            Assert.Equal(6.0, peakMs, 6);
            Assert.Equal(default(FrameBudget), DiagnosticsState.playbackBudget);
        }

        [Fact]
        public void GlobalSink_CheckPlaybackBudgetThreshold_AboveThreshold_LogsTheBudgetLine()
        {
            new GlobalGhostPlaybackDiagnosticsSink().CheckPlaybackBudgetThreshold(
                12_300, 2, 4f, default(PlaybackBudgetPhases));

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Diagnostics]")
                && l.Contains("Playback frame budget exceeded: 12.3ms (2 ghosts, warp: 4x)"));
            Assert.Contains(logLines, l =>
                l.Contains("[Diagnostics]") && l.Contains("Playback budget breakdown (one-shot"));
        }

        [Fact]
        public void GlobalSink_CheckPlaybackBudgetThreshold_WithinBudget_LogsNothing()
        {
            new GlobalGhostPlaybackDiagnosticsSink().CheckPlaybackBudgetThreshold(
                (long)(DiagnosticsComputation.PlaybackBudgetThresholdMs * 1000.0), 2, 1f,
                default(PlaybackBudgetPhases));

            Assert.DoesNotContain(logLines, l => l.Contains("[Diagnostics]"));
        }

        [Fact]
        public void DefaultEngine_UpdatePlaybackWithNoTrajectories_ClearsTheGlobalBudget()
        {
            DiagnosticsState.playbackBudget = new FrameBudget { totalMicroseconds = 7_000, ghostsProcessed = 3 };
            var engine = new GhostPlaybackEngine(positioner: null);

            engine.UpdatePlayback(new List<IPlaybackTrajectory>(), new TrajectoryPlaybackFlags[0], default(FrameContext));

            Assert.Equal(default(FrameBudget), DiagnosticsState.playbackBudget);
        }

        [Fact]
        public void InjectedSink_UpdatePlaybackEarlyReturns_ClearThroughTheSinkNotTheGlobalBudget()
        {
            var stale = new FrameBudget { totalMicroseconds = 7_000, ghostsProcessed = 3 };
            DiagnosticsState.playbackBudget = stale;
            var sink = new CountingSink();
            var engine = new GhostPlaybackEngine(null, sink);

            // No trajectories, then trajectories with no positioner wired: both early returns.
            engine.UpdatePlayback(null, null, default(FrameContext));
            engine.UpdatePlayback(
                new List<IPlaybackTrajectory> { new MockTrajectory() },
                new TrajectoryPlaybackFlags[1], default(FrameContext));

            Assert.Equal(new[] { "clearBudget", "clearBudget" }, sink.Calls);
            Assert.Equal(stale, DiagnosticsState.playbackBudget);
            Assert.True(DiagnosticsState.playbackFrameHistory.IsEmpty);
        }

        [Fact]
        public void NullSink_IsRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new GhostPlaybackEngine(null, null));
        }
    }
}
