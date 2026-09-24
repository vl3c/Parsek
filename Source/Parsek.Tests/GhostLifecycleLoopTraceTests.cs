using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Ghostlife v2 producer side: the tracing-gated MeshDestroyed line an overlap copy writes when
    /// it vanishes, and the LoopCycle line a cycle advance writes. Both are read by
    /// harness/lib/ghostlife.py; with tracing off neither may appear.
    /// </summary>
    [Collection("Sequential")]
    public class GhostLifecycleLoopTraceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostLifecycleLoopTraceTests()
        {
            GhostRenderTrace.Reset();
            GhostRenderTrace.ForceEnabledForTesting = false;
            GhostRenderTrace.FrameCounterOverrideForTesting = () => 42;
            GhostPlaybackEngine.MeshTraceUTOverrideForTesting = () => 1234.5;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            DiagnosticsState.ResetForTesting();
        }

        public void Dispose()
        {
            GhostRenderTrace.Reset();
            GhostRenderTrace.ForceEnabledForTesting = false;
            GhostRenderTrace.FrameCounterOverrideForTesting = null;
            GhostPlaybackEngine.MeshTraceUTOverrideForTesting = null;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            DiagnosticsState.ResetForTesting();
        }

        private static GhostPlaybackState OverlapCopy(bool spawnTraced)
        {
            return new GhostPlaybackState
            {
                recordingId = "rec-overlap-0001",
                vesselName = "Kerbal X Debris",
                ghost = null,
                loopCycleIndex = 3,
                meshSpawnTraced = spawnTraced,
            };
        }

        private static GhostPlaybackEngine EngineWithResourceSpy(List<GhostPlaybackState> destroyed)
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            engine.DestroyGhostResourcesOverrideForTesting = s => destroyed.Add(s);
            return engine;
        }

        private List<string> TraceLines(string phase)
        {
            return logLines.Where(l => l.Contains("[GhostRenderTrace]")
                && l.Contains("phase=" + phase)).ToList();
        }

        [Fact]
        public void OverlapCopyExpiry_TracingOn_WritesMeshDestroyedWithReason()
        {
            GhostRenderTrace.ForceEnabledForTesting = true;
            var destroyed = new List<GhostPlaybackState>();
            var engine = EngineWithResourceSpy(destroyed);
            var state = OverlapCopy(spawnTraced: true);

            engine.DestroyOverlapGhostState(state, 7, null, "overlap expired");

            var lines = TraceLines("MeshDestroyed");
            Assert.Single(lines);
            Assert.Contains("[INFO]", lines[0]);
            Assert.Contains("recId=rec-overlap-0001", lines[0]);
            Assert.Contains("ghostIndex=7", lines[0]);
            Assert.EndsWith("vessel=Kerbal X Debris reason=overlap expired", lines[0]);
            Assert.False(state.meshSpawnTraced);
            Assert.Single(destroyed);
        }

        [Fact]
        public void OverlapCopyExpiry_TracingOff_WritesNoTraceLineButStillDestroys()
        {
            var destroyed = new List<GhostPlaybackState>();
            var engine = EngineWithResourceSpy(destroyed);
            var state = OverlapCopy(spawnTraced: true);

            engine.DestroyOverlapGhostState(state, 7, null, "overlap expired");

            Assert.DoesNotContain(logLines, l => l.Contains("[GhostRenderTrace]"));
            Assert.Single(destroyed);
        }

        [Fact]
        public void OverlapCopyThatNeverWroteMeshSpawned_StaysSilent()
        {
            // A boundary-overlap secondary or a primary demoted mid-build never wrote MeshSpawned;
            // a destroy line for it would unbalance the ghostlife line counts.
            GhostRenderTrace.ForceEnabledForTesting = true;
            var destroyed = new List<GhostPlaybackState>();
            var engine = EngineWithResourceSpy(destroyed);

            engine.DestroyOverlapGhostState(OverlapCopy(spawnTraced: false), 7, null, "overlap expired");

            Assert.Empty(TraceLines("MeshDestroyed"));
            Assert.Single(destroyed);
        }

        [Fact]
        public void OverlapCopy_DestroyedTwice_WritesOneLine()
        {
            GhostRenderTrace.ForceEnabledForTesting = true;
            var engine = EngineWithResourceSpy(new List<GhostPlaybackState>());
            var state = OverlapCopy(spawnTraced: true);

            engine.DestroyOverlapGhostState(state, 7, null, "overlap expired");
            engine.DestroyOverlapGhostState(state, 7, null, "engine teardown");

            Assert.Single(TraceLines("MeshDestroyed"));
        }

        [Fact]
        public void LoopCycle_TracingOn_WritesCycleLineWithVesselLast()
        {
            GhostRenderTrace.ForceEnabledForTesting = true;
            var state = OverlapCopy(spawnTraced: true);

            GhostPlaybackEngine.EmitLoopCycleTrace(4, null, state, 3, 4, "reuse");

            var lines = TraceLines("LoopCycle");
            Assert.Single(lines);
            Assert.Contains("[INFO]", lines[0]);
            Assert.Contains("recId=rec-overlap-0001", lines[0]);
            Assert.Contains("currentUT=1234.5", lines[0]);
            Assert.EndsWith("cycle=4 prev=3 mode=reuse vessel=Kerbal X Debris", lines[0]);
        }

        [Fact]
        public void LoopCycle_TracingOff_WritesNothing()
        {
            GhostPlaybackEngine.EmitLoopCycleTrace(4, null, OverlapCopy(true), 3, 4, "overlap-demote");

            Assert.DoesNotContain(logLines, l => l.Contains("[GhostRenderTrace]"));
        }

        [Fact]
        public void LoopCycle_NoRecordingId_WritesNothing()
        {
            GhostRenderTrace.ForceEnabledForTesting = true;
            var state = OverlapCopy(true);
            state.recordingId = null;

            GhostPlaybackEngine.EmitLoopCycleTrace(4, null, state, 3, 4, "unit");

            Assert.Empty(TraceLines("LoopCycle"));
        }

        [Fact]
        public void ReuseAcrossCycle_NullGhost_WritesNoLoopCycleLine()
        {
            // The reuse census counts only a live object carried across the boundary.
            GhostRenderTrace.ForceEnabledForTesting = true;
            var engine = new GhostPlaybackEngine(positioner: null);
            var state = OverlapCopy(true);

            engine.ReusePrimaryGhostAcrossCycle(
                index: 7, traj: null, flags: default, state, playbackUT: 0, newCycleIndex: 4);

            Assert.Empty(TraceLines("LoopCycle"));
            Assert.Equal(4L, state.loopCycleIndex);
        }
    }
}
