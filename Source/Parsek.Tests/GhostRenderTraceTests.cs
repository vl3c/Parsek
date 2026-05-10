using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostRenderTraceTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostRenderTraceTests()
        {
            GhostRenderTrace.Reset();
            GhostRenderTrace.ForceEnabledForTesting = false;
            GhostRenderTrace.FrameCounterOverrideForTesting = () => 42;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GhostRenderTrace.Reset();
            GhostRenderTrace.ForceEnabledForTesting = false;
            GhostRenderTrace.FrameCounterOverrideForTesting = null;
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void EvaluateGate_FirstSeenAndInitialWindow_EmitsThenCloses()
        {
            var first = GhostRenderTrace.EvaluateGateForTesting(
                currentUT: 100.0,
                firstSeenUT: 100.0,
                firstSeen: true,
                structuralWindow: false,
                sectionChanged: false,
                force: false,
                resolverMissOrRetired: false,
                reFlyWindow: false,
                deltaMeters: 0.0,
                expectedDeltaMeters: 0.0);

            Assert.True(first.Emit);
            Assert.False(first.Important);
            Assert.Equal("first-seen", first.Reason);

            var within = GhostRenderTrace.EvaluateGateForTesting(
                currentUT: 103.0,
                firstSeenUT: 100.0,
                firstSeen: false,
                structuralWindow: false,
                sectionChanged: false,
                force: false,
                resolverMissOrRetired: false,
                reFlyWindow: false,
                deltaMeters: 0.0,
                expectedDeltaMeters: 0.0);

            Assert.True(within.Emit);
            Assert.Equal("initial-window", within.Reason);

            var closed = GhostRenderTrace.EvaluateGateForTesting(
                currentUT: 106.0,
                firstSeenUT: 100.0,
                firstSeen: false,
                structuralWindow: false,
                sectionChanged: false,
                force: false,
                resolverMissOrRetired: false,
                reFlyWindow: false,
                deltaMeters: 0.0,
                expectedDeltaMeters: 0.0);

            Assert.False(closed.Emit);
            Assert.Equal("closed", closed.Reason);
        }

        [Fact]
        public void EvaluateGate_LargeDelta_IsImportant()
        {
            var decision = GhostRenderTrace.EvaluateGateForTesting(
                currentUT: 50.0,
                firstSeenUT: 10.0,
                firstSeen: false,
                structuralWindow: false,
                sectionChanged: false,
                force: false,
                resolverMissOrRetired: false,
                reFlyWindow: false,
                deltaMeters: 500.0,
                expectedDeltaMeters: 10.0);

            Assert.True(decision.Emit);
            Assert.True(decision.Important);
            Assert.Equal("large-delta", decision.Reason);
        }

        [Fact]
        public void FormatTracePrefix_UsesInvariantKeyValueFields()
        {
            string prefix = GhostRenderTrace.FormatTracePrefixForTesting(
                "abcdef0123456789",
                7,
                123.4567,
                120.25,
                "AfterUpdate");

            Assert.Contains("phase=AfterUpdate", prefix);
            Assert.Contains("rec=abcdef01", prefix);
            Assert.Contains("recId=abcdef0123456789", prefix);
            Assert.Contains("ghostIndex=7", prefix);
            Assert.Contains("currentUT=123.457", prefix);
            Assert.Contains("playbackUT=120.250", prefix);
        }

        [Fact]
        public void ShouldEmitPhase_DisabledByDefault_ReturnsFalseEvenForForce()
        {
            GhostRenderTrace.OpenDetailedWindow("rec-disabled", 100.0, 10.0, "test");

            Assert.False(GhostRenderTrace.ShouldEmitPhase(
                "rec-disabled",
                101.0,
                important: true,
                force: true));
        }

        [Fact]
        public void ShouldEmitPhase_EnabledBySettings_UsesDetailedWindow()
        {
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings
            {
                ghostRenderTracing = true
            };

            GhostRenderTrace.OpenDetailedWindow("rec-enabled", 100.0, 2.0, "test");

            Assert.True(GhostRenderTrace.ShouldEmitPhase("rec-enabled", 101.0));
            Assert.False(GhostRenderTrace.ShouldEmitPhase("rec-enabled", 103.0));
        }

        [Fact]
        public void ShouldEmitPhase_ForceEnabledForTesting_UsesDetailedWindow()
        {
            GhostRenderTrace.ForceEnabledForTesting = true;
            GhostRenderTrace.OpenDetailedWindow("rec-force", 50.0, 1.0, "test");

            Assert.True(GhostRenderTrace.ShouldEmitPhase("rec-force", 50.5));
            Assert.False(GhostRenderTrace.ShouldEmitPhase("rec-force", 52.0));
        }

        // ----------------------------------------------------------------
        // Phase 1 (controlled-ghost-init-slide observability) coverage:
        // EmitActivationDecision field shape, transition flag, hidden-pose
        // delta, retired carve-out, default-value EmitPostUpdate contract,
        // and loop-path clampFired=false invariant.
        // ----------------------------------------------------------------

        [Fact]
        public void EmitActivationDecision_HiddenFrame_EmitsExpectedFields()
        {
            GhostRenderTrace.ForceEnabledForTesting = true;
            var traj = new MockTrajectory { RecordingId = "rec-act-hidden" };

            GhostRenderTrace.EmitActivationDecision(
                trajectory: traj,
                ghostIndex: 5,
                currentUT: 100.001,
                rawPlaybackUT: 100.001,
                visiblePlaybackUT: 100.000,
                activationStartUT: 100.000,
                framesRemaining: 1,
                hidden: true,
                hideReason: "activation-settle",
                callSite: "RenderInRangeGhost",
                currentPosition: new Vector3(10, 20, 30),
                hasCurrentPosition: true);

            string line = Assert.Single(FindLines("phase=ActivationDecision"));
            Assert.Contains("rec=rec-act-", line);
            Assert.Contains("ghostIndex=5", line);
            Assert.Contains("callSite=RenderInRangeGhost", line);
            Assert.Contains("rawPlaybackUT=100.001", line);
            Assert.Contains("visiblePlaybackUT=100.000", line);
            Assert.Contains("activationStart=100.000", line);
            Assert.Contains("activationLead=0.001", line);
            Assert.Contains("visibleLead=0.000", line);
            Assert.Contains("clampFired=true", line);
            Assert.Contains("hidden=true", line);
            Assert.Contains("hideReason=activation-settle", line);
            Assert.Contains("framesRemaining=1", line);
            Assert.Contains("transition=hidden", line);
            Assert.Contains("hiddenPoseDelta=NaN", line);
        }

        [Fact]
        public void EmitActivationDecision_FirstVisibleTransition_EmitsHiddenPoseDelta()
        {
            GhostRenderTrace.ForceEnabledForTesting = true;
            var traj = new MockTrajectory { RecordingId = "rec-first-visible" };

            // Two hidden frames at distinct positions establish the
            // last-hidden cursor.
            GhostRenderTrace.EmitActivationDecision(
                trajectory: traj, ghostIndex: 0,
                currentUT: 50.000, rawPlaybackUT: 50.000, visiblePlaybackUT: 50.000,
                activationStartUT: 50.000, framesRemaining: 2,
                hidden: true, hideReason: "activation-settle",
                callSite: "RenderInRangeGhost",
                currentPosition: new Vector3(0, 0, 0), hasCurrentPosition: true);
            GhostRenderTrace.EmitActivationDecision(
                trajectory: traj, ghostIndex: 0,
                currentUT: 50.020, rawPlaybackUT: 50.020, visiblePlaybackUT: 50.000,
                activationStartUT: 50.000, framesRemaining: 1,
                hidden: true, hideReason: "minimum-frames",
                callSite: "RenderInRangeGhost",
                currentPosition: new Vector3(1, 2, 3), hasCurrentPosition: true);

            // Transition to visible at a new position (catch-up jump).
            GhostRenderTrace.EmitActivationDecision(
                trajectory: traj, ghostIndex: 0,
                currentUT: 50.040, rawPlaybackUT: 50.040, visiblePlaybackUT: 50.040,
                activationStartUT: 50.000, framesRemaining: 0,
                hidden: false, hideReason: null,
                callSite: "RenderInRangeGhost",
                currentPosition: new Vector3(7, 2, 3), hasCurrentPosition: true);

            var transitionLines = FindLines("transition=first-visible");
            string line = Assert.Single(transitionLines);
            Assert.Contains("hidden=false", line);
            Assert.Contains("hideReason=<none>", line);
            // Delta from (1,2,3) -> (7,2,3) = 6.
            Assert.Contains("hiddenPoseDelta=6.000", line);
            Assert.Contains("prevHiddenPos=(1.00,2.00,3.00)", line);
            Assert.Contains("clampFired=false", line);
            Assert.Contains("visibleLead=0.040", line);
        }

        [Fact]
        public void EmitActivationDecision_OpensDetailedWindowOnTransition()
        {
            GhostRenderTrace.ForceEnabledForTesting = true;
            const string recId = "rec-window";
            var traj = new MockTrajectory { RecordingId = recId };

            // Pre-prime the per-state firstSeenUT well in the past via a
            // primer EmitPostUpdate at currentUT=100. After this, the
            // per-state state.firstSeenUT == 100 and state.initialized=true,
            // so the initial-window gate (currentUT - firstSeenUT <= 4)
            // CLOSES at currentUT > 104. This isolates the activation-
            // transition window's effect at later UTs without coupling to
            // first-seen / initial-window gating.
            GhostRenderTrace.EmitPostUpdate(
                trajectory: traj, ghostIndex: 0,
                currentUT: 100.0, playbackUT: 100.0,
                playbackState: null,
                path: "non-loop", retired: false,
                surface: GhostRenderTrace.RenderSurface.Legacy);
            // Window must be closed before the activation transition fires.
            Assert.False(GhostRenderTrace.IsDetailedWindowOpenForTesting(recId, 200.0));

            // Hidden ActivationDecision: NOT the trigger for the window.
            GhostRenderTrace.EmitActivationDecision(
                trajectory: traj, ghostIndex: 0,
                currentUT: 200.0, rawPlaybackUT: 200.0, visiblePlaybackUT: 200.0,
                activationStartUT: 200.0, framesRemaining: 0,
                hidden: true, hideReason: "activation-settle",
                callSite: "RenderInRangeGhost",
                currentPosition: new Vector3(0, 0, 0), hasCurrentPosition: true);
            // Still no window — only first-visible opens it.
            Assert.False(GhostRenderTrace.IsDetailedWindowOpenForTesting(recId, 200.0));

            // First-visible transition opens the activation-transition window.
            GhostRenderTrace.EmitActivationDecision(
                trajectory: traj, ghostIndex: 0,
                currentUT: 200.05, rawPlaybackUT: 200.05, visiblePlaybackUT: 200.05,
                activationStartUT: 200.0, framesRemaining: 0,
                hidden: false, hideReason: null,
                callSite: "RenderInRangeGhost",
                currentPosition: new Vector3(0, 0, 0), hasCurrentPosition: true);

            // Window opens at the first-visible transition (currentUT=200.05),
            // covers ActivationTransitionWindowSeconds (1.0) past that.
            Assert.True(GhostRenderTrace.IsDetailedWindowOpenForTesting(recId, 200.05));
            Assert.True(GhostRenderTrace.IsDetailedWindowOpenForTesting(recId, 200.50));
            Assert.True(GhostRenderTrace.IsDetailedWindowOpenForTesting(recId, 201.05));
            Assert.False(GhostRenderTrace.IsDetailedWindowOpenForTesting(recId, 201.06));

            // End-to-end contract: at a UT firmly past initial-window's 4 s
            // span (currentUT=201.0; firstSeenUT=100.0; lead=101 >> 4) but
            // INSIDE the activation-transition window (200.05 + 1.0 = 201.05),
            // EmitPostUpdate must emit because ONLY the activation-transition
            // window keeps the gate open. This is the load-bearing assertion
            // the prior re-review's redundant ShouldEmitPhase round-trip did
            // not pin: it proves the new emit phase actually ungates the
            // surrounding render trace, not just that the predicate flips.
            int linesBeforeInWindow = FindLines("phase=AfterUpdate").Count;
            GhostRenderTrace.EmitPostUpdate(
                trajectory: traj, ghostIndex: 0,
                currentUT: 201.0, playbackUT: 201.0,
                playbackState: null,
                path: "non-loop", retired: false,
                surface: GhostRenderTrace.RenderSurface.Legacy);
            int linesAfterInWindow = FindLines("phase=AfterUpdate").Count;
            Assert.True(linesAfterInWindow > linesBeforeInWindow,
                "AfterUpdate at UT inside activation-transition window (and past initial-window) must emit");

            // At a UT past BOTH initial-window and activation-transition, the
            // gate closes and no AfterUpdate emit lands.
            int linesBeforeOutOfWindow = FindLines("phase=AfterUpdate").Count;
            GhostRenderTrace.EmitPostUpdate(
                trajectory: traj, ghostIndex: 0,
                currentUT: 500.0, playbackUT: 500.0,
                playbackState: null,
                path: "non-loop", retired: false,
                surface: GhostRenderTrace.RenderSurface.Legacy);
            int linesAfterOutOfWindow = FindLines("phase=AfterUpdate").Count;
            Assert.Equal(linesBeforeOutOfWindow, linesAfterOutOfWindow);
        }

        // Plan §1a's retired-carve-out (`EmitActivationDecision` does NOT
        // fire on retired frames) is structurally enforced by the engine
        // wrapping the call inside the `!retired` else branch at
        // `GhostPlaybackEngine.cs:1233-1276`. An xUnit test for that
        // carve-out would need to drive `RenderInRangeGhost` directly with
        // a state where `state.ghost` is a real Unity `GameObject` — which
        // xUnit cannot construct (no Unity runtime). Coverage falls to the
        // in-game test runner, same pattern as the watch-sync engine
        // coverage gap documented in the PR description. A tautology test
        // (asserting that a method we didn't call didn't emit) was tried
        // and removed after a review pass flagged it as proving nothing
        // about the engine contract.

        [Fact]
        public void EmitPostUpdate_AppendsRawPlaybackUTAndLeadAndClamped()
        {
            GhostRenderTrace.ForceEnabledForTesting = true;
            var traj = new MockTrajectory { RecordingId = "rec-clamp" };

            GhostRenderTrace.EmitPostUpdate(
                trajectory: traj, ghostIndex: 0,
                currentUT: 100.030, playbackUT: 100.000,
                playbackState: null,
                path: "non-loop", retired: false,
                surface: GhostRenderTrace.RenderSurface.Legacy,
                rawPlaybackUT: 100.030,
                activationStartUT: 100.000);

            string line = Assert.Single(FindLines("phase=AfterUpdate"));
            Assert.Contains("rawPlaybackUT=100.030", line);
            Assert.Contains("visibleLead=0.000", line);
            Assert.Contains("clampFired=true", line);
        }

        [Fact]
        public void EmitPostUpdate_DefaultRawPlaybackUT_ClampFiredFalse()
        {
            GhostRenderTrace.ForceEnabledForTesting = true;
            var traj = new MockTrajectory { RecordingId = "rec-default-raw" };

            // Caller does not pass rawPlaybackUT — default NaN. AfterUpdate
            // must treat raw == visible and emit clampFired=false instead of
            // synthesizing a fake clamp from the missing field.
            GhostRenderTrace.EmitPostUpdate(
                trajectory: traj, ghostIndex: 0,
                currentUT: 50.0, playbackUT: 50.0,
                playbackState: null,
                path: "non-loop", retired: false);

            string line = Assert.Single(FindLines("phase=AfterUpdate"));
            Assert.Contains("rawPlaybackUT=50.000", line);
            Assert.Contains("clampFired=false", line);
            Assert.Contains("visibleLead=NaN", line);
        }

        [Fact]
        public void EmitPostUpdate_LoopPathInvariant_ClampFiredFalse()
        {
            // Loop sites pass loopUT for both raw and visible — verified by
            // source walk that PositionLoopAtPlaybackUT and
            // UpdateExpireAndPositionOverlaps do NOT call
            // ResolveVisiblePlaybackUT. Pin the invariant at the trace API
            // level so a future refactor that adds clamp resolution to a loop
            // path fails this test rather than silently emitting spurious
            // clampFired=true rows.
            GhostRenderTrace.ForceEnabledForTesting = true;
            var traj = new MockTrajectory { RecordingId = "rec-loop" };

            // loop-primary
            GhostRenderTrace.EmitPostUpdate(
                trajectory: traj, ghostIndex: 0,
                currentUT: 100.0, playbackUT: 33.5,
                playbackState: null,
                path: "loop-primary", retired: false,
                surface: GhostRenderTrace.RenderSurface.Legacy,
                rawPlaybackUT: 33.5,
                activationStartUT: 30.0);
            // overlap-primary
            GhostRenderTrace.EmitPostUpdate(
                trajectory: traj, ghostIndex: 0,
                currentUT: 100.020, playbackUT: 18.0,
                playbackState: null,
                path: "overlap-primary", retired: false,
                surface: GhostRenderTrace.RenderSurface.Legacy,
                rawPlaybackUT: 18.0,
                activationStartUT: 15.0);
            // loop-overlap
            GhostRenderTrace.EmitPostUpdate(
                trajectory: traj, ghostIndex: 0,
                currentUT: 100.040, playbackUT: 5.5,
                playbackState: null,
                path: "loop-overlap cycle=2", retired: false,
                surface: GhostRenderTrace.RenderSurface.Legacy,
                rawPlaybackUT: 5.5,
                activationStartUT: 0.0);

            var afterLines = FindLines("phase=AfterUpdate");
            Assert.Equal(3, afterLines.Count);
            foreach (var line in afterLines)
            {
                Assert.Contains("clampFired=false", line);
            }
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private List<string> FindLines(string token)
        {
            var hits = new List<string>();
            foreach (string line in logLines)
            {
                if (line != null && line.Contains(token))
                    hits.Add(line);
            }
            return hits;
        }
    }
}
