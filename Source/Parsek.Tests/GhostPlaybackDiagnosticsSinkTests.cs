using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The playback engine counts its ghost lifecycle health events through an
    /// <see cref="IGhostPlaybackDiagnosticsSink"/>. The global sink is the player's own engine's:
    /// each member moves exactly one <see cref="DiagnosticsState.health"/> counter by one. An
    /// engine given another sink leaves the global counters alone.
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
        }

        public void Dispose()
        {
            DiagnosticsState.ResetForTesting();
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
            var engine = new GhostPlaybackEngine(positioner: null);
            engine.ghostStates[0] = new GhostPlaybackState { vesselName = "Sink Probe" };

            engine.DestroyGhost(0, reason: "sink test");

            AssertOnlyFieldMoved("ghostDestroysThisSession");
            Assert.Contains(logLines, l => l.Contains("[Engine]") && l.Contains("GhostPlaybackEngine created"));
        }

        [Fact]
        public void InjectedSink_DestroyGhost_CountsIntoTheSinkNotTheGlobalCounters()
        {
            var sink = new CountingSink();
            var engine = new GhostPlaybackEngine(null, sink);
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
        public void NullSink_IsRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new GhostPlaybackEngine(null, null));
        }
    }
}
