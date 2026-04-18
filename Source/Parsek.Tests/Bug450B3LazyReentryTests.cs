using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #450 Phase B3 tests — lazy reentry FX pre-warm.
    ///
    /// Phase A's one-shot breakdown showed reentry FX costs ~6.94 ms at spawn time
    /// (29 % of a 24.2 ms bimodal single-spawn frame). B3 defers that build to the
    /// first frame the ghost is actually inside an atmosphere. Trajectories that
    /// never enter atmosphere save the full build cost.
    ///
    /// Tests cover:
    ///  - pure `GhostPlaybackLogic.ShouldBuildLazyReentryFx` decision logic (no Unity).
    ///  - engine-side throttle + log behaviour via `TryPerformLazyReentryBuildForTesting`
    ///    (the actual `TryBuildReentryFx` call needs a live Unity GameObject, so the
    ///    tests observe the non-build code paths — throttle, idempotency, defensive
    ///    heatInfos-null guard. End-to-end build assertion is in the in-game runtime
    ///    test suite).
    /// </summary>
    [Collection("Sequential")]
    public class Bug450B3LazyReentryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug450B3LazyReentryTests()
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

        // ----- Pure decision logic -----

        [Fact]
        public void ShouldBuildLazyReentryFx_NotPending_ReturnsFalse()
        {
            // Most common path: flag never set in the first place (no-reentry trajectory).
            Assert.False(GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                pendingFlag: false, bodyName: "Kerbin",
                bodyHasAtmosphere: true, altitudeMeters: 10_000, atmosphereDepthMeters: 70_000));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ShouldBuildLazyReentryFx_NoBodyName_ReturnsFalse(string bodyName)
        {
            // Positioner hasn't written the body yet — don't speculate. Fails if a future
            // refactor forgets to guard against positioning that hasn't run yet.
            Assert.False(GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                pendingFlag: true, bodyName: bodyName,
                bodyHasAtmosphere: true, altitudeMeters: 10_000, atmosphereDepthMeters: 70_000));
        }

        [Fact]
        public void ShouldBuildLazyReentryFx_BodyHasNoAtmosphere_ReturnsFalse()
        {
            // Mun / Minmus / asteroids: never build. Most orbital showcases land here
            // — fails if a future refactor drops the atmosphere check (would build FX
            // that then permanently idle with zero intensity).
            Assert.False(GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                pendingFlag: true, bodyName: "Mun",
                bodyHasAtmosphere: false, altitudeMeters: 0, atmosphereDepthMeters: 0));
        }

        [Fact]
        public void ShouldBuildLazyReentryFx_AboveAtmosphereDepth_ReturnsFalse()
        {
            // Orbital ghost still in vacuum: don't build yet. Fails if the altitude
            // comparison is inverted.
            Assert.False(GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                pendingFlag: true, bodyName: "Kerbin",
                bodyHasAtmosphere: true, altitudeMeters: 100_000, atmosphereDepthMeters: 70_000));
        }

        [Fact]
        public void ShouldBuildLazyReentryFx_BelowAtmosphereDepth_ReturnsTrue()
        {
            // Happy path: ghost deorbited, now in atmosphere. Positive case.
            Assert.True(GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                pendingFlag: true, bodyName: "Kerbin",
                bodyHasAtmosphere: true, altitudeMeters: 50_000, atmosphereDepthMeters: 70_000));
        }

        [Fact]
        public void ShouldBuildLazyReentryFx_JustBelowDepth_ReturnsTrue()
        {
            // Boundary: one meter below the cap. Fails if the comparison uses `>` by mistake.
            Assert.True(GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                pendingFlag: true, bodyName: "Kerbin",
                bodyHasAtmosphere: true, altitudeMeters: 69_999, atmosphereDepthMeters: 70_000));
        }

        [Fact]
        public void ShouldBuildLazyReentryFx_ExactlyAtDepth_ReturnsFalse()
        {
            // Pins the `>=` on the altitude check: at EXACTLY the atmosphere depth,
            // stock KSP's existing UpdateReentryFx calls DriveReentryToZero. Firing
            // a lazy build here would pay ~7 ms for a frame that produces zero-intensity
            // output, then drive-to-zero on the next frame. Wait one more frame.
            Assert.False(GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                pendingFlag: true, bodyName: "Kerbin",
                bodyHasAtmosphere: true, altitudeMeters: 70_000, atmosphereDepthMeters: 70_000));
        }

        [Fact]
        public void ShouldBuildLazyReentryFx_NegativeAltitude_ReturnsTrue()
        {
            // Defensive: reload-artifact landed ghosts can read below-surface altitudes.
            // Still inside atmosphere, so still a valid build trigger. Fails if a future
            // refactor adds an `altitude > 0` guard (would strand landed ghosts pending
            // forever).
            Assert.True(GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                pendingFlag: true, bodyName: "Kerbin",
                bodyHasAtmosphere: true, altitudeMeters: -100, atmosphereDepthMeters: 70_000));
        }

        // ----- Engine seam: throttle + idempotency + defensive paths -----
        //
        // These tests exercise `TryPerformLazyReentryBuild` via the test seam. The
        // actual `TryBuildReentryFx` call inside requires a live Unity GameObject for
        // GetComponentsInChildren, so we drive the NO-BUILD branches here (heatInfos
        // null, cap exhausted, flag already cleared). Actual build assertion lives in
        // the in-game runtime test suite.

        private static GhostPlaybackState BuildPendingState()
        {
            // Minimal state for the defensive branches — ghost stays null, heatInfos
            // stays null, so the real TryBuildReentryFx call is never reached.
            return new GhostPlaybackState
            {
                vesselName = "TestGhost",
                reentryFxPendingBuild = true,
                reentryFxInfo = null,
            };
        }

        [Fact]
        public void TryPerformLazyReentryBuild_FlagAlreadyCleared_NoOp()
        {
            // Defensive idempotency: calling the helper with the flag already cleared
            // must NOT fire a build, must NOT emit a log line, must NOT increment any
            // counter. Guards against a refactor that loses the production-side
            // `if (state.reentryFxPendingBuild)` guard in UpdateReentryFx.
            var engine = new GhostPlaybackEngine(positioner: null);
            var state = BuildPendingState();
            state.reentryFxPendingBuild = false;
            int buildsBefore = DiagnosticsState.health.reentryFxBuildsThisSession;

            engine.TryPerformLazyReentryBuildForTesting(
                recIdx: 7, state, vesselName: "X", bodyName: "Kerbin", altitude: 50_000);

            Assert.Equal(buildsBefore, DiagnosticsState.health.reentryFxBuildsThisSession);
            Assert.Equal(0, engine.FrameLazyReentryBuildCountForTesting);
            Assert.Equal(0, engine.FrameLazyReentryBuildDeferredForTesting);
            Assert.DoesNotContain(logLines, l => l.Contains("Lazy reentry build"));
            Assert.False(state.reentryFxPendingBuild);
        }

        [Fact]
        public void TryPerformLazyReentryBuild_HeatInfosNull_ClearsFlagAndLogs()
        {
            // heatInfos is only nulled by ClearLoadedVisualReferences (which also
            // clears the pending flag) or by a rebuild in flight. If we somehow land
            // here with a null it, clearing the flag prevents an infinite retry loop.
            var engine = new GhostPlaybackEngine(positioner: null);
            var state = BuildPendingState();
            state.heatInfos = null;

            engine.TryPerformLazyReentryBuildForTesting(
                recIdx: 3, state, vesselName: "NoHeat", bodyName: "Kerbin", altitude: 50_000);

            Assert.False(state.reentryFxPendingBuild);  // flag cleared
            Assert.Equal(0, engine.FrameLazyReentryBuildCountForTesting);  // no build slot consumed
            Assert.Contains(logLines, l =>
                l.Contains("[ReentryFx]") && l.Contains("state.heatInfos is null"));
        }

        [Fact]
        public void TryPerformLazyReentryBuild_CapExhausted_DefersAndLogs()
        {
            // 3 ghosts want to lazy-build in the same frame, cap = 2. The third must
            // be throttled: pending flag STAYS true so the next frame retries, deferred
            // counter increments, throttle log line emits. This is the key guard against
            // relocating the bimodal spawn hitch to atmosphere-entry-time (review #3).
            var engine = new GhostPlaybackEngine(positioner: null);
            var state = BuildPendingState();

            // Force the cap exhausted. Using a test-seam setter avoids the need to
            // actually fire TryBuildReentryFx (which requires a live Unity GameObject).
            engine.SetFrameLazyReentryBuildCountForTesting(GhostPlaybackEngine.MaxLazyReentryBuildsPerFrame);

            engine.TryPerformLazyReentryBuildForTesting(
                recIdx: 7, state, vesselName: "Excess", bodyName: "Kerbin", altitude: 50_000);

            Assert.True(state.reentryFxPendingBuild);  // flag stays — next frame retries
            Assert.Equal(1, engine.FrameLazyReentryBuildDeferredForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[ReentryFx]") && l.Contains("Lazy reentry build throttled") &&
                l.Contains("#7"));
        }

        [Fact]
        public void TryPerformLazyReentryBuild_CapResetsOnFrameReset_RetryFires()
        {
            // After exhausting the cap, the next frame's counter reset should re-arm
            // throttling. Fails if the reset ever loses the new field.
            var engine = new GhostPlaybackEngine(positioner: null);
            var state = BuildPendingState();
            state.heatInfos = null;  // Force the heatInfos-null defensive path for the actual attempt.

            // Frame 1: cap exhausted.
            engine.SetFrameLazyReentryBuildCountForTesting(GhostPlaybackEngine.MaxLazyReentryBuildsPerFrame);
            engine.TryPerformLazyReentryBuildForTesting(
                recIdx: 1, state, vesselName: "T", bodyName: "Kerbin", altitude: 50_000);
            Assert.True(state.reentryFxPendingBuild);  // throttled, flag stays

            // Frame 2: reset simulates UpdatePlayback head running again.
            engine.ResetPerFrameCountersForTesting();
            Assert.Equal(0, engine.FrameLazyReentryBuildCountForTesting);

            // Retry: now walks past the throttle into the heatInfos-null defensive
            // path (still no real build — just verifies the throttle gate is released).
            engine.TryPerformLazyReentryBuildForTesting(
                recIdx: 1, state, vesselName: "T", bodyName: "Kerbin", altitude: 50_000);
            Assert.False(state.reentryFxPendingBuild);  // cleared by heatInfos-null path
        }

        [Fact]
        public void ClearLoadedVisualReferences_ResetsPendingFlag()
        {
            // The pending flag must be cleared by ClearLoadedVisualReferences (same
            // path that nulls reentryFxInfo and heatInfos). Otherwise a rebuild that
            // reruns PopulateGhostInfoDictionaries would see a dangling true flag from
            // the pre-rebuild instance and skip the new deferred-set.
            var state = new GhostPlaybackState
            {
                reentryFxPendingBuild = true,
            };

            state.ClearLoadedVisualReferences();

            Assert.False(state.reentryFxPendingBuild);
        }

        // ----- Session-counter semantics -----

        [Fact]
        public void HealthCounters_ReentryFxDeferredThisSession_DefaultsToZero()
        {
            // A fresh session starts with zero deferrals. Fails if the new counter
            // field is ever accidentally initialized from persisted state.
            var counters = new HealthCounters();
            counters.Reset();

            Assert.Equal(0, counters.reentryFxDeferredThisSession);
        }

        [Fact]
        public void HealthCounters_Reset_ZerosReentryFxDeferred()
        {
            // Reset must zero the new counter alongside the other session metrics.
            // Fails if Reset() is ever expanded without including the new field.
            var counters = new HealthCounters { reentryFxDeferredThisSession = 42 };
            counters.Reset();

            Assert.Equal(0, counters.reentryFxDeferredThisSession);
        }
    }
}
