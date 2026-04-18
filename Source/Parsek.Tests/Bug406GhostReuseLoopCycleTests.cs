using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// #406 follow-up — ghost GameObject reuse across loop-cycle boundaries.
    ///
    /// Pure-helper tests that do not require a live Unity GameObject:
    ///  - `GhostPlaybackLogic.ResetForLoopCycle` field-level post-conditions.
    ///  - `GhostPlaybackEngine.ReusePrimaryGhostAcrossCycle` null-ghost
    ///    defensive path and frame-counter non-increment invariants via
    ///    the test-exposed counters.
    ///
    /// End-to-end reuse with a real ghost hierarchy (part reactivation,
    /// cameraPivot identity across the reuse, `reentryFxInfo` identity)
    /// lives in `InGameTests/RuntimeTests.cs` because that requires a live
    /// Unity scene for `ParsekFlight` / positioner integration.
    /// </summary>
    [Collection("Sequential")]
    public class Bug406GhostReuseLoopCycleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug406GhostReuseLoopCycleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            DiagnosticsState.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            DiagnosticsState.ResetForTesting();
        }

        // --- ResetForLoopCycle ---

        [Fact]
        public void ResetForLoopCycle_ResetsPlaybackIterators_PreservesSnapshotDictionaries()
        {
            // The cycle-rewind must zero every index walker that
            // `ApplyPartEvents` / `ApplyFlagEvents` / `TrackGhostAppearance`
            // consume, or the new cycle replays from halfway through and
            // decoupled parts never re-fire their decouple events.
            // Snapshot-derived dictionaries (`engineInfos`, `rcsInfos`,
            // `audioInfos`, `heatInfos`, `partTree`, `logicalPartIds`)
            // must NOT be nulled — they are the whole reuse optimisation.
            var engineInfos = new Dictionary<ulong, EngineGhostInfo>();
            var rcsInfos = new Dictionary<ulong, RcsGhostInfo>();
            var audioInfos = new Dictionary<ulong, AudioGhostInfo>();
            var heatInfos = new Dictionary<uint, HeatGhostInfo>();
            var partTree = new Dictionary<uint, List<uint>>();
            var logicalIds = new HashSet<uint> { 100000, 101111 };
            var deployableInfos = new Dictionary<uint, DeployableGhostInfo>();
            var parachuteInfos = new Dictionary<uint, ParachuteGhostInfo>();
            var jettisonInfos = new Dictionary<uint, JettisonGhostInfo>();
            var lightInfos = new Dictionary<uint, LightGhostInfo>();
            var fairingInfos = new Dictionary<uint, FairingGhostInfo>();
            var colorChangerInfos = new Dictionary<uint, List<ColorChangerGhostInfo>>();
            var roboticInfos = new Dictionary<ulong, RoboticGhostInfo>();
            var compoundPartInfos = new List<CompoundPartGhostInfo>();

            var state = new GhostPlaybackState
            {
                vesselName = "TestGhost",
                playbackIndex = 100,
                partEventIndex = 50,
                flagEventIndex = 3,
                appearanceCount = 7,
                hadVisibleRenderersLastFrame = true,
                loopCycleIndex = 12,
                explosionFired = true,
                pauseHidden = true,
                audioMuted = true,
                atmosphereFactor = 0.3f,
                deferVisibilityUntilPlaybackSync = false,
                engineInfos = engineInfos,
                rcsInfos = rcsInfos,
                audioInfos = audioInfos,
                heatInfos = heatInfos,
                partTree = partTree,
                logicalPartIds = logicalIds,
                deployableInfos = deployableInfos,
                parachuteInfos = parachuteInfos,
                jettisonInfos = jettisonInfos,
                lightInfos = lightInfos,
                fairingInfos = fairingInfos,
                colorChangerInfos = colorChangerInfos,
                roboticInfos = roboticInfos,
                compoundPartInfos = compoundPartInfos,
            };

            GhostPlaybackLogic.ResetForLoopCycle(state, newCycleIndex: 13);

            // Iterators rewind.
            Assert.Equal(0, state.playbackIndex);
            Assert.Equal(0, state.partEventIndex);
            Assert.Equal(0, state.flagEventIndex);
            Assert.Equal(0, state.appearanceCount);
            Assert.False(state.hadVisibleRenderersLastFrame);
            Assert.Equal(13L, state.loopCycleIndex);

            // Per-cycle flags reset.
            Assert.False(state.explosionFired);
            Assert.False(state.pauseHidden);
            Assert.False(state.audioMuted);
            Assert.Equal(1f, state.atmosphereFactor);
            Assert.True(state.deferVisibilityUntilPlaybackSync);

            // Snapshot-derived dictionaries preserved by reference — this is
            // the entire point of the reuse optimisation. If any of these
            // get nulled, the next frame's apply-events path NREs or silently
            // drops engine/RCS/audio/heat state.
            Assert.Same(engineInfos, state.engineInfos);
            Assert.Same(rcsInfos, state.rcsInfos);
            Assert.Same(audioInfos, state.audioInfos);
            Assert.Same(heatInfos, state.heatInfos);
            Assert.Same(partTree, state.partTree);
            Assert.Same(logicalIds, state.logicalPartIds);
            Assert.Same(deployableInfos, state.deployableInfos);
            Assert.Same(parachuteInfos, state.parachuteInfos);
            Assert.Same(jettisonInfos, state.jettisonInfos);
            Assert.Same(lightInfos, state.lightInfos);
            Assert.Same(fairingInfos, state.fairingInfos);
            Assert.Same(colorChangerInfos, state.colorChangerInfos);
            Assert.Same(roboticInfos, state.roboticInfos);
            Assert.Same(compoundPartInfos, state.compoundPartInfos);
        }

        [Fact]
        public void ResetForLoopCycle_PreservesReentryFxPendingBuildFlag_BothWays()
        {
            // #450 B3 invariant: the ~7 ms TryBuildReentryFx is paid at most
            // once per ghost lifetime. Destroy+respawn today re-defers + re-pays
            // it every cycle — that is half the cost this #406 follow-up
            // eliminates. If ResetForLoopCycle ever clears this flag, the
            // reuse path regresses to pre-B3 reentry cost. Cover both the
            // "pending" case and the "already built" case.

            // Case A: still pending (ghost never reached atmosphere). Flag stays true.
            var pending = new GhostPlaybackState
            {
                reentryFxPendingBuild = true,
                reentryFxInfo = null,
            };
            GhostPlaybackLogic.ResetForLoopCycle(pending, newCycleIndex: 1);
            Assert.True(pending.reentryFxPendingBuild);
            Assert.Null(pending.reentryFxInfo);

            // Case B: lazy build already fired. Flag stays false; info reference stays.
            var info = new ReentryFxInfo();
            var built = new GhostPlaybackState
            {
                reentryFxPendingBuild = false,
                reentryFxInfo = info,
            };
            GhostPlaybackLogic.ResetForLoopCycle(built, newCycleIndex: 1);
            Assert.False(built.reentryFxPendingBuild);
            Assert.Same(info, built.reentryFxInfo);

            // Case C: trajectory had no reentry potential (showcase/EVA). Both null/false.
            var none = new GhostPlaybackState
            {
                reentryFxPendingBuild = false,
                reentryFxInfo = null,
            };
            GhostPlaybackLogic.ResetForLoopCycle(none, newCycleIndex: 1);
            Assert.False(none.reentryFxPendingBuild);
            Assert.Null(none.reentryFxInfo);
        }

        [Fact]
        public void ResetForLoopCycle_ClearsLightPlaybackStatesInPlace()
        {
            // Per-part blink/on/off state accumulated from the prior cycle's
            // light events must drop so the new cycle starts from snapshot
            // baseline. The dictionary REFERENCE is preserved (downstream
            // code lazily creates it otherwise; preserving it avoids an
            // allocation on every cycle and keeps test-observable identity
            // stable for any code that cached the reference).
            var lights = new Dictionary<uint, LightPlaybackState>
            {
                { 100000, new LightPlaybackState { isOn = true, blinkEnabled = true, blinkRateHz = 2f } },
                { 101111, new LightPlaybackState { isOn = false } },
            };
            var state = new GhostPlaybackState { lightPlaybackStates = lights };

            GhostPlaybackLogic.ResetForLoopCycle(state, newCycleIndex: 1);

            Assert.Same(lights, state.lightPlaybackStates);
            Assert.Empty(state.lightPlaybackStates);
        }

        [Fact]
        public void ResetForLoopCycle_NullState_DoesNotThrow()
        {
            // Defensive: upstream call sites always guard on state != null,
            // but the helper should not NRE on a null input. Guards against
            // a future refactor that calls the helper from a not-yet-guarded
            // code path.
            var ex = Record.Exception(() => GhostPlaybackLogic.ResetForLoopCycle(null, newCycleIndex: 1));
            Assert.Null(ex);
        }

        [Fact]
        public void ResetForLoopCycle_HandlesNullDictionariesGracefully()
        {
            // A minimally-constructed state (first-frame-after-spawn) may
            // have `lightPlaybackStates == null` because it is lazily
            // created on the first light event. The helper must not NRE.
            // DestroyAllFakeCanopies is a Unity-touching helper called by
            // the engine orchestrator separately; the pure-logic reset
            // leaves fakeCanopies untouched (the null here just confirms
            // no accidental re-initialization).
            var state = new GhostPlaybackState
            {
                lightPlaybackStates = null,
                fakeCanopies = null,
            };

            var ex = Record.Exception(() => GhostPlaybackLogic.ResetForLoopCycle(state, newCycleIndex: 1));
            Assert.Null(ex);
            Assert.Null(state.lightPlaybackStates);
            Assert.Null(state.fakeCanopies);
        }

        // --- Engine-level reuse ---

        [Fact]
        public void ReusePrimaryGhostAcrossCycle_NullGhost_WarnsAndSkips()
        {
            // Defensive guard: if the ghost GameObject was somehow destroyed
            // between the caller's `state != null` check and this call, we
            // must not NRE. Warn so the condition is visible in playtest
            // logs and return without mutating state.
            var engine = new GhostPlaybackEngine(positioner: null);
            var state = new GhostPlaybackState
            {
                vesselName = "NoGhost",
                ghost = null,
                loopCycleIndex = 4,
                playbackIndex = 99,
            };

            engine.ReusePrimaryGhostAcrossCycle(
                index: 7, traj: null, flags: default, state, playbackUT: 0, newCycleIndex: 5);

            Assert.Equal(0, engine.FrameSpawnCountForTesting);
            Assert.Equal(0, engine.FrameLazyReentryBuildCountForTesting);
            // State unchanged: the helper bailed before any reset.
            Assert.Equal(99, state.playbackIndex);
            Assert.Equal(4L, state.loopCycleIndex);
            Assert.Contains(logLines, l =>
                l.Contains("[Engine]") && l.Contains("ReusePrimaryGhostAcrossCycle")
                && l.Contains("#7"));
        }

        [Fact]
        public void ReusePrimaryGhostAcrossCycle_NullGhost_DoesNotIncrementReuseCounter()
        {
            // The diagnostics counter is for SUCCESSFUL reuses. A defensive
            // bail on null-ghost is not a reuse. Guards against a refactor
            // that increments the counter unconditionally at method entry.
            var engine = new GhostPlaybackEngine(positioner: null);
            int reusesBefore = DiagnosticsState.health.ghostReusedAcrossCycleThisSession;
            var state = new GhostPlaybackState { ghost = null };

            engine.ReusePrimaryGhostAcrossCycle(
                index: 3, traj: null, flags: default, state, playbackUT: 0, newCycleIndex: 1);

            Assert.Equal(reusesBefore, DiagnosticsState.health.ghostReusedAcrossCycleThisSession);
        }

        [Fact]
        public void DiagnosticsHealth_ResetClearsReuseCounter()
        {
            // Test-infrastructure sanity: the session counter reset must
            // cover the new field. A forgotten reset leaks across tests
            // and masks regressions where the counter increments on the
            // wrong code path.
            DiagnosticsState.health.ghostReusedAcrossCycleThisSession = 42;
            DiagnosticsState.ResetForTesting();
            Assert.Equal(0, DiagnosticsState.health.ghostReusedAcrossCycleThisSession);
        }

        [Fact]
        public void ResetForLoopCycle_DoesNotBumpReuseCounter()
        {
            // Pure-logic contract: the reuse counter is bumped only by the
            // engine's ReusePrimaryGhostAcrossCycle orchestrator, never by
            // the pure-static reset helper. Fails if someone moves the
            // DiagnosticsState.health.ghostReusedAcrossCycleThisSession++
            // call into ResetForLoopCycle (e.g. to "simplify" the orchestrator),
            // which would double-count every reuse and produce misleading
            // `reusesThisSession` ratios in the diagnostics health line.
            // ReactivateGhostPartHierarchyForLoopRewind is Unity-touching so
            // is not invoked here; its counter-independence is covered by
            // the in-game test Bug406_ReusePrimaryGhostAcrossCycle_PreservesGhostIdentity.
            DiagnosticsState.health.ghostReusedAcrossCycleThisSession = 0;
            var state = new GhostPlaybackState
            {
                loopCycleIndex = 4,
                playbackIndex = 50,
                partEventIndex = 10,
            };

            GhostPlaybackLogic.ResetForLoopCycle(state, newCycleIndex: 5);

            Assert.Equal(0, DiagnosticsState.health.ghostReusedAcrossCycleThisSession);
            // Also verify the helper DID do its stated work — pins the
            // counter assertion above to a scenario where the real pure-logic
            // body ran.
            Assert.Equal(5L, state.loopCycleIndex);
            Assert.Equal(0, state.playbackIndex);
            Assert.Equal(0, state.partEventIndex);
        }
    }
}
