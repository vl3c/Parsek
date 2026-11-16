using System;
using System.Collections.Generic;
using System.Security;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The per-frame spawn cap as an object (<see cref="GhostSpawnFrameBudget"/>): the single
    /// engine owns its budget and resets it every frame exactly as the old counter was reset;
    /// two engines given one budget share one cap and never reset it themselves (the owner of a
    /// shared budget resets it once per frame before the first engine runs).
    /// </summary>
    [Collection("Sequential")]
    public class GhostSpawnFrameBudgetTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostSpawnFrameBudgetTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();
            DiagnosticsState.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            DiagnosticsState.ResetForTesting();
        }

        private sealed class NullSink : IGhostPlaybackDiagnosticsSink
        {
            public void CountGhostBuild() { }
            public void CountReentryFxDeferred() { }
            public void CountReentryFxSkipped() { }
            public void CountReentryFxBuild() { }
            public void CountGhostReusedAcrossCycle() { }
            public void CountGhostDestroy() { }
            public void ClearPlaybackBudget() { }
            public void PublishPlaybackBudget(
                long totalMicroseconds, long spawnMicroseconds, long destroyMicroseconds,
                int ghostsProcessed, float warpRate, GhostObservability ghostObservability) { }
            public void AppendPlaybackFrame(float realtime, long totalMicroseconds) { }
            public void CheckPlaybackBudgetThreshold(
                long totalMicroseconds, int ghostsProcessed, float warpRate, PlaybackBudgetPhases phases) { }
        }

        // A frame of skipped entries reaches no positioner member; any call is a test failure.
        private sealed class UnreachedPositioner : IGhostPositioner
        {
            public void InterpolateAndPosition(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx)
                => throw new InvalidOperationException("positioner reached");
            public void InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx, RelativeSectionPlaybackTarget target)
                => throw new InvalidOperationException("positioner reached");
            public bool TryPositionFromBodyFixedPrimary(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT, RelativeSectionPlaybackTarget target, out double bracketBeforeUT, out double bracketAfterUT)
                => throw new InvalidOperationException("positioner reached");
            public void PositionAtPoint(int index, IPlaybackTrajectory traj, GhostPlaybackState state, TrajectoryPoint point)
                => throw new InvalidOperationException("positioner reached");
            public void PositionAtSurface(int index, IPlaybackTrajectory traj, GhostPlaybackState state)
                => throw new InvalidOperationException("positioner reached");
            public void PositionFromOrbit(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut)
                => throw new InvalidOperationException("positioner reached");
            public void PositionLoop(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx)
                => throw new InvalidOperationException("positioner reached");
            public bool TryResolveExplosionAnchorPosition(int index, IPlaybackTrajectory traj, GhostPlaybackState state, out Vector3 worldPosition)
                => throw new InvalidOperationException("positioner reached");
            public ZoneRenderingResult ApplyZoneRendering(int index, GhostPlaybackState state, IPlaybackTrajectory traj, double distance, double playbackUT, int protectedIndex)
                => throw new InvalidOperationException("positioner reached");
        }

        /// <summary>
        /// One production frame over a single skipped entry: UpdatePlayback resets the per-frame
        /// counters before its loop. The frame tail reads Unity's realtime clock, which the
        /// headless runtime refuses; everything before it, the reset included, has run by then.
        /// </summary>
        private static void RunSkippedFrame(GhostPlaybackEngine engine)
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.RecordingId = "skip";
            traj.VesselName = "V-skip";
            var flags = new[]
            {
                new TrajectoryPlaybackFlags
                {
                    recordingId = "skip",
                    chainEndUT = 200,
                    skipGhost = true,
                    skipReason = GhostPlaybackSkipReason.ExternalVesselSuppressed,
                },
            };
            try
            {
                engine.UpdatePlayback(
                    new List<IPlaybackTrajectory> { traj }, flags,
                    new FrameContext { currentUT = 150, warpRate = 1f });
            }
            catch (Exception ex) when (ex is MissingMethodException || ex is SecurityException || ex is MethodAccessException)
            {
            }
        }

        [Fact]
        public void NewBudget_IsEmpty_AtTheConfiguredCap()
        {
            var budget = new GhostSpawnFrameBudget();

            Assert.Equal(0, budget.Used);
            Assert.Equal(GhostPlayback.MaxSpawnsPerFrame, budget.MaxPerFrame);
        }

        [Fact]
        public void Consume_Counts_AndResetForFrame_Clears()
        {
            var budget = new GhostSpawnFrameBudget();
            budget.Consume();
            budget.Consume();
            budget.Consume();
            Assert.Equal(3, budget.Used);

            budget.ResetForFrame();
            Assert.Equal(0, budget.Used);
        }

        [Fact]
        public void OwnedBudget_IsResetByTheEngineEachFrame()
        {
            var engine = new GhostPlaybackEngine(new UnreachedPositioner(), new NullSink());
            for (int i = 0; i < GhostPlayback.MaxSpawnsPerFrame; i++)
                engine.IncrementFrameSpawnCountForTesting();
            Assert.False(engine.TryReserveSpawnSlotForTesting(0, "test"));

            RunSkippedFrame(engine);

            Assert.Equal(0, engine.FrameSpawnCountForTesting);
            Assert.True(engine.TryReserveSpawnSlotForTesting(0, "test"));
        }

        [Fact]
        public void OwnedBudget_IsResetByTheTestHookToo()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            for (int i = 0; i < GhostPlayback.MaxSpawnsPerFrame; i++)
                engine.IncrementFrameSpawnCountForTesting();
            Assert.False(engine.TryReserveSpawnSlotForTesting(0, "test"));

            engine.ResetPerFrameCountersForTesting();

            Assert.True(engine.TryReserveSpawnSlotForTesting(0, "test"));
        }

        [Fact]
        public void TwoEngines_OnOneSharedBudget_RefuseTheThirdSpawnAcrossBoth()
        {
            var shared = new GhostSpawnFrameBudget();
            var a = new GhostPlaybackEngine(null, new NullSink(), shared);
            var b = new GhostPlaybackEngine(null, new NullSink(), shared);
            Assert.Equal(2, shared.MaxPerFrame);

            Assert.True(a.TryReserveSpawnSlotForTesting(0, "a"));
            a.IncrementFrameSpawnCountForTesting();
            Assert.True(b.TryReserveSpawnSlotForTesting(0, "b"));
            b.IncrementFrameSpawnCountForTesting();

            // Each engine has started one spawn; the shared cap of two is spent.
            Assert.Equal(1, a.FrameSpawnCountForTesting);
            Assert.Equal(1, b.FrameSpawnCountForTesting);
            Assert.Equal(2, shared.Used);
            Assert.False(a.TryReserveSpawnSlotForTesting(1, "a"));
            Assert.False(b.TryReserveSpawnSlotForTesting(1, "b"));
            Assert.Equal(1, a.FrameSpawnDeferredForTesting);
            Assert.Equal(1, b.FrameSpawnDeferredForTesting);

            // The owner of the shared budget opens the next frame.
            shared.ResetForFrame();
            Assert.True(a.TryReserveSpawnSlotForTesting(1, "a"));
            Assert.True(b.TryReserveSpawnSlotForTesting(1, "b"));
        }

        [Fact]
        public void SharedBudget_IsNotResetByEitherEngine()
        {
            var shared = new GhostSpawnFrameBudget();
            var a = new GhostPlaybackEngine(new UnreachedPositioner(), new NullSink(), shared);
            var b = new GhostPlaybackEngine(new UnreachedPositioner(), new NullSink(), shared);
            a.IncrementFrameSpawnCountForTesting();
            b.IncrementFrameSpawnCountForTesting();

            RunSkippedFrame(a);
            RunSkippedFrame(b);
            a.ResetPerFrameCountersForTesting();
            b.ResetPerFrameCountersForTesting();

            // Each engine's own count resets with its frame; the shared budget does not.
            Assert.Equal(0, a.FrameSpawnCountForTesting);
            Assert.Equal(0, b.FrameSpawnCountForTesting);
            Assert.Equal(2, shared.Used);
            Assert.False(a.TryReserveSpawnSlotForTesting(0, "a"));
        }

        [Fact]
        public void ThrottleLine_TextIsUnchanged()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            engine.IncrementFrameSpawnCountForTesting();
            engine.IncrementFrameSpawnCountForTesting();

            Assert.False(engine.TryReserveSpawnSlotForTesting(5, "first-spawn"));

            Assert.Contains(logLines, l => l.Contains("[Engine]")
                && l.EndsWith("Spawn throttled (first-spawn): #5 deferred to next frame (used 2/2)"));
            Assert.Equal(2, engine.FrameSpawnCountForTesting);
            Assert.Equal(1, engine.FrameSpawnDeferredForTesting);
        }

        [Fact]
        public void OwnedEngine_LogsOnlyTheCreatedLine_SharedEngineNamesItsBudget()
        {
            new GhostPlaybackEngine(positioner: null);
            Assert.Contains(logLines, l => l.Contains("[Engine]") && l.Contains("GhostPlaybackEngine created"));
            Assert.DoesNotContain(logLines, l => l.Contains("shared spawn budget"));

            logLines.Clear();
            var shared = new GhostSpawnFrameBudget();
            shared.Consume();
            new GhostPlaybackEngine(null, new NullSink(), shared);
            Assert.Contains(logLines, l => l.Contains("[Engine]") && l.Contains("GhostPlaybackEngine created"));
            Assert.Contains(logLines, l => l.Contains("[Engine]") && l.Contains(
                "GhostPlaybackEngine draws on a shared spawn budget (used 1/2); the caller resets it each frame"));
        }
    }
}
