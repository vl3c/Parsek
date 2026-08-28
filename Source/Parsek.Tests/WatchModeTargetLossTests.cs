using System;
using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the two watch-mode findings this file was written for.
    ///
    /// <para>WATCH-LOOPED-PARK-TARGET-LOSS-NRE-STORM - watch mode survived a loop re-arm with
    /// nothing bound to the camera and stayed armed to scene end. The cells below pin the two
    /// decisions that now bound it: every target-loss cause is COUNTED by one safety net, and a
    /// cycle fallback is only committed when the camera rebind actually took.</para>
    ///
    /// <para>WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE - watch auto-select refused far inside the
    /// 300 km range term. The cells below pin the established mechanism at the seam that
    /// produces it, using the values `V7M-minmus-player-loop` measured.</para>
    /// </summary>
    [Collection("Sequential")]
    public class WatchModeTargetLossTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public WatchModeTargetLossTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ---------------------------------------------------------------
        // WATCH-LOOPED-PARK-TARGET-LOSS-NRE-STORM
        // ---------------------------------------------------------------

        [Fact]
        public void ClassifyWatchTargetLoss_ContinuesOnlyWhenCameraAndGhostBothResolve()
        {
            Assert.Equal(
                WatchModeController.WatchTargetLossAction.Continue,
                WatchModeController.ClassifyWatchTargetLoss(
                    cameraInfrastructureReady: true,
                    hasGhostState: true,
                    hasCameraPivot: true,
                    consecutiveLostFrames: 1,
                    exitAfterFrames: WatchModeController.WatchNoTargetExitFrames));
        }

        /// <summary>
        /// The measured V15M state is a VALID ghost target (state present, camera pivot
        /// present) with the KSP camera gone: the old code took an uncounted early return from
        /// ABOVE the ghost-side net for exactly this shape, so the session never converged.
        /// Counting it is the fix, and this asserts the count reaches an exit.
        ///
        /// <para>HONEST SCOPE: this pins the CLASSIFIER, which did not exist before the fix -
        /// on origin/main it would fail to COMPILE rather than fail an assertion, so it is not
        /// a regression cell in the "revert and watch it go red" sense. The production site
        /// that consumes it is per-frame Unity code no headless cell can run; what stops that
        /// site being silently reverted is <see cref="WatchModeTargetLossWiringGateTests"/>,
        /// not this file.</para>
        /// </summary>
        [Fact]
        public void ClassifyWatchTargetLoss_CountsMissingCameraInfrastructureToAnExit()
        {
            const int budget = WatchModeController.WatchNoTargetExitFrames;

            for (int frame = 1; frame < budget; frame++)
            {
                Assert.Equal(
                    WatchModeController.WatchTargetLossAction.Wait,
                    WatchModeController.ClassifyWatchTargetLoss(
                        cameraInfrastructureReady: false,
                        hasGhostState: true,
                        hasCameraPivot: true,
                        consecutiveLostFrames: frame,
                        exitAfterFrames: budget));
            }

            Assert.Equal(
                WatchModeController.WatchTargetLossAction.ExitWatch,
                WatchModeController.ClassifyWatchTargetLoss(
                    cameraInfrastructureReady: false,
                    hasGhostState: true,
                    hasCameraPivot: true,
                    consecutiveLostFrames: budget,
                    exitAfterFrames: budget));
        }

        [Fact]
        public void ClassifyWatchTargetLoss_StillCountsTheLegacyGhostSideCauses()
        {
            const int budget = WatchModeController.WatchNoTargetExitFrames;

            Assert.Equal(
                WatchModeController.WatchTargetLossAction.ExitWatch,
                WatchModeController.ClassifyWatchTargetLoss(
                    cameraInfrastructureReady: true,
                    hasGhostState: false,
                    hasCameraPivot: false,
                    consecutiveLostFrames: budget,
                    exitAfterFrames: budget));

            Assert.Equal(
                WatchModeController.WatchTargetLossAction.Wait,
                WatchModeController.ClassifyWatchTargetLoss(
                    cameraInfrastructureReady: true,
                    hasGhostState: true,
                    hasCameraPivot: false,
                    consecutiveLostFrames: 1,
                    exitAfterFrames: budget));
        }

        [Fact]
        public void ShouldBridgeWatchCameraOffDestroyedGhost_OnlyForTheWatchedIndexAndABoundTarget()
        {
            // The measured trigger: the watched index's ghost is destroyed for the loop-unit
            // cycle rebuild while FlightCamera.Target is its horizonProxy.
            Assert.True(WatchModeController.ShouldBridgeWatchCameraOffDestroyedGhost(
                watchedRecordingIndex: 0,
                destroyedRecordingIndex: 0,
                hasFlightCamera: true,
                hasCameraTarget: true,
                targetBelongsToDestroyedGhost: true));

            // A different recording's ghost dying must never move the watch camera.
            Assert.False(WatchModeController.ShouldBridgeWatchCameraOffDestroyedGhost(
                watchedRecordingIndex: 0,
                destroyedRecordingIndex: 3,
                hasFlightCamera: true,
                hasCameraTarget: true,
                targetBelongsToDestroyedGhost: true));

            // Not watching at all.
            Assert.False(WatchModeController.ShouldBridgeWatchCameraOffDestroyedGhost(
                watchedRecordingIndex: -1,
                destroyedRecordingIndex: -1,
                hasFlightCamera: true,
                hasCameraTarget: true,
                targetBelongsToDestroyedGhost: true));

            // Camera already points somewhere safe (e.g. the overlap anchor) - nothing to do.
            Assert.False(WatchModeController.ShouldBridgeWatchCameraOffDestroyedGhost(
                watchedRecordingIndex: 0,
                destroyedRecordingIndex: 0,
                hasFlightCamera: true,
                hasCameraTarget: true,
                targetBelongsToDestroyedGhost: false));

            // No camera to rebind.
            Assert.False(WatchModeController.ShouldBridgeWatchCameraOffDestroyedGhost(
                watchedRecordingIndex: 0,
                destroyedRecordingIndex: 0,
                hasFlightCamera: false,
                hasCameraTarget: false,
                targetBelongsToDestroyedGhost: true));
        }

        /// <summary>
        /// The V15M line `Watched cycle lost - falling back to primary cycle=1` was followed
        /// immediately by `actualTarget=null targetMatches=False`: the primary WAS usable, the
        /// rebind was skipped because FlightCamera was gone, and the fallback committed anyway.
        /// </summary>
        [Fact]
        public void ClassifyWatchCycleFallback_RefusesToCommitAFallbackTheCameraDidNotTake()
        {
            Assert.Equal(
                WatchModeController.WatchCycleFallbackDecision.Commit,
                WatchModeController.ClassifyWatchCycleFallback(
                    primaryUsable: true, retargetSucceeded: true));

            Assert.Equal(
                WatchModeController.WatchCycleFallbackDecision.ReleaseTarget,
                WatchModeController.ClassifyWatchCycleFallback(
                    primaryUsable: true, retargetSucceeded: false));

            Assert.Equal(
                WatchModeController.WatchCycleFallbackDecision.NoPrimary,
                WatchModeController.ClassifyWatchCycleFallback(
                    primaryUsable: false, retargetSucceeded: false));
        }

        /// <summary>
        /// The bridge anchor must clear itself rather than wait for some other path to rebind.
        /// A destroy + respawn at an UNCHANGED cycle index never enters the cycle fallback, so
        /// "release once something rebinds" alone would leave the camera on a static GameObject
        /// for the rest of the session (HorizonLocked frozen, mismatch Warn repeating forever).
        /// </summary>
        [Fact]
        public void ClassifyWatchCycleBridgeDisposition_RebindsWhenTheCameraIsStillOnTheAnchor()
        {
            Assert.Equal(
                WatchModeController.WatchCycleBridgeDisposition.None,
                WatchModeController.ClassifyWatchCycleBridgeDisposition(
                    hasBridgeAnchor: false, cameraBoundToBridge: false, replacementTargetUsable: true));

            // Something else already rebound (the cycle fallback, a transfer, a fresh entry).
            Assert.Equal(
                WatchModeController.WatchCycleBridgeDisposition.Release,
                WatchModeController.ClassifyWatchCycleBridgeDisposition(
                    hasBridgeAnchor: true, cameraBoundToBridge: false, replacementTargetUsable: true));

            // THE CELL THIS EXISTS FOR: nothing else will, so this frame must.
            Assert.Equal(
                WatchModeController.WatchCycleBridgeDisposition.RebindThenRelease,
                WatchModeController.ClassifyWatchCycleBridgeDisposition(
                    hasBridgeAnchor: true, cameraBoundToBridge: true, replacementTargetUsable: true));

            // Nothing to rebind to yet - the anchor is a LIVE object, so holding it is safe and
            // is strictly better than releasing the camera onto a destroyed transform.
            Assert.Equal(
                WatchModeController.WatchCycleBridgeDisposition.Hold,
                WatchModeController.ClassifyWatchCycleBridgeDisposition(
                    hasBridgeAnchor: true, cameraBoundToBridge: true, replacementTargetUsable: false));
        }

        /// <summary>
        /// The exit Warn's grep tokens, asserted through the log sink the way CLAUDE.md's
        /// log-capture pattern prescribes. `cause=` is what a future reader slices the two loss
        /// causes apart by, and it did not exist before this fix (the old line named no cause at
        /// all). The message builder is pure precisely so this token is pinned somewhere; that
        /// <c>UpdateWatchCamera</c> emits it through <c>ParsekLog.Warn("CameraFollow", ...)</c>
        /// is pinned by <see cref="WatchModeTargetLossWiringGateTests"/>.
        /// </summary>
        [Fact]
        public void WatchTargetLossExitWarnNamesTheCauseAndTheSession()
        {
            ParsekLog.Warn("CameraFollow",
                WatchModeController.BuildWatchTargetLossExitMessage(
                    lostFrames: 3,
                    cameraInfrastructureReady: false,
                    cameraInfrastructureReason: "flight-camera-missing",
                    recordingIndex: 0,
                    recordingId: "77f724bb1d4844c3b132a1ccc00a7df3",
                    cycleIndex: 1));

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[CameraFollow]")
                && l.Contains("cause=flight-camera-missing")
                && l.Contains("rec=#0")
                && l.Contains("id=77f724bb1d4844c3b132a1ccc00a7df3")
                && l.Contains("cycle=1")
                && l.Contains("exiting watch mode"));

            // The ghost-side cause must remain distinguishable in the same field.
            logLines.Clear();
            ParsekLog.Warn("CameraFollow",
                WatchModeController.BuildWatchTargetLossExitMessage(
                    lostFrames: 3,
                    cameraInfrastructureReady: true,
                    cameraInfrastructureReason: "ready",
                    recordingIndex: 2,
                    recordingId: null,
                    cycleIndex: -1));

            Assert.Contains(logLines, l =>
                l.Contains("[CameraFollow]")
                && l.Contains("cause=ghost-target-missing")
                && l.Contains("id=null"));
        }

        // ---------------------------------------------------------------
        // WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE
        // ---------------------------------------------------------------

        /// <summary>
        /// V7M's measured separations, against the constant the production range predicate
        /// actually uses. All four archived readings are comfortably INSIDE 300 km, so the range
        /// term returned true on every refusing frame - which is what excludes it as the cause.
        /// </summary>
        [Theory]
        [InlineData(144349.0)]
        [InlineData(144356.0)]
        [InlineData(144365.0)]
        [InlineData(191499.0)]
        [InlineData(198711.0)]
        public void IsWithinWatchEntryRange_AcceptsEveryV7mRefusalDistance(double measuredMeters)
        {
            Assert.True(measuredMeters < WatchModeController.WatchEnterCutoffMeters);
            Assert.True(WatchModeController.IsWithinWatchEntryRange(measuredMeters));
        }

        /// <summary>
        /// THE ESTABLISHED MECHANISM, at the seam that produces it, with the values V7M
        /// measured. <c>lastInterpolatedBodyName</c> is SEEDED at spawn
        /// (<c>CreatePendingSpawnState</c> -&gt; <c>TryResolvePendingPlaybackInterpolation</c>)
        /// and thereafter written only on the POSITIONING path, which the render-zone hide
        /// early-returns above. A ghost the zone keeps hidden therefore answers with its
        /// spawn-time seed forever.
        ///
        /// <para>Both archived V7Mc attempts logged that seed as <c>body='Kerbin'</c> at
        /// UT=267563.7, held it across BOTH refusals while the observer sat at Minmus, and only
        /// re-seeded to <c>Minmus</c> a quarter-second before the entry that succeeded. So the
        /// refusing conjunct is <c>IsGhostOnSameBody</c> answering from a STALE reading - not a
        /// null one (an earlier draft of this entry said null; the seed line in the same log
        /// contradicts it), not <c>HasActiveGhost</c> (the state IS present, as the same
        /// frame's `engine-frame-iter` line proves by printing its zone and distance), and not
        /// the range term (previous cell).</para>
        ///
        /// <para>SCOPE, since the coordinated decision was later TAKEN: this cell pins the
        /// ENGINE-LEVEL cache read, which is unchanged and still exactly this stale. What changed
        /// is that <c>WatchModeController.IsGhostOnSameBody</c> no longer DECIDES on it - see
        /// <see cref="WatchEntryAcceptanceTests"/> for the term that now resolves the ghost's body
        /// from its own trajectory and accepts this shape.</para>
        /// </summary>
        [Fact]
        public void IsGhostOnBody_RefusesAHiddenGhostHoldingItsStaleSpawnSeed()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            var hiddenGhost = new GhostPlaybackState
            {
                // 144,356 m: V7Mc run `_1607`'s cycle-1 park reading. lastDistance IS written
                // (CachePlaybackDistances runs ABOVE the hide early return), which is why the
                // range term could evaluate a real sub-300 km number and pass.
                lastDistance = 144356.0,
                currentZone = RenderingZone.Beyond,
            };
            // The spawn seed, verbatim from both archived attempts.
            hiddenGhost.SetInterpolated(new InterpolationResult { bodyName = "Kerbin", altitude = 0.0 });
            engine.ghostStates[1] = hiddenGhost;

            Assert.False(engine.IsGhostOnBody(1, "Minmus"));
            // ...and the affordance must NOT describe that as an observed different body.
            Assert.False(WatchModeController.IsWatchBodyReadingCurrent(
                hiddenGhost.lastInterpolatedBodyName, hiddenGhost.currentZone));

            // The never-seeded population is real too (TryResolvePendingPlaybackInterpolation
            // can fail and logs "seed unavailable"), it just is not what V7M measured.
            var neverSeeded = new GhostPlaybackState
            {
                lastDistance = 144356.0,
                currentZone = RenderingZone.Beyond,
                lastInterpolatedBodyName = null,
            };
            engine.ghostStates[2] = neverSeeded;
            Assert.False(engine.IsGhostOnBody(2, "Minmus"));
            Assert.False(WatchModeController.IsWatchBodyReadingCurrent(
                neverSeeded.lastInterpolatedBodyName, neverSeeded.currentZone));

            // One positioning pass inside the zone is all it takes; the ghost was on Minmus the
            // whole time, and now both the comparison and the affordance agree.
            hiddenGhost.SetInterpolated(new InterpolationResult
            {
                bodyName = "Minmus",
                altitude = 40585.23,
            });
            hiddenGhost.currentZone = RenderingZone.Visual;
            Assert.True(engine.IsGhostOnBody(1, "Minmus"));
            Assert.True(WatchModeController.IsWatchBodyReadingCurrent(
                hiddenGhost.lastInterpolatedBodyName, hiddenGhost.currentZone));
        }

        /// <summary>
        /// The staleness predicate on its own. A present body name is NOT evidence the reading
        /// is current - that conflation is what made the first version of this fix describe the
        /// measured V7M refusal as a genuine different-body case.
        /// </summary>
        [Fact]
        public void IsWatchBodyReadingCurrent_RequiresBothANameAndALiveRenderZone()
        {
            Assert.True(WatchModeController.IsWatchBodyReadingCurrent("Minmus", RenderingZone.Physics));
            Assert.True(WatchModeController.IsWatchBodyReadingCurrent("Minmus", RenderingZone.Visual));
            Assert.False(WatchModeController.IsWatchBodyReadingCurrent("Minmus", RenderingZone.Beyond));
            Assert.False(WatchModeController.IsWatchBodyReadingCurrent(null, RenderingZone.Physics));
            Assert.False(WatchModeController.IsWatchBodyReadingCurrent("", RenderingZone.Visual));
        }

        /// <summary>
        /// The refusal as the auto-selector sees it, with the V7M triple: an active ghost, in
        /// range, and a body term that says no. The conjunction refuses, and the reject-branch
        /// report now NAMES the term instead of leaving `no-watchable-ghost` to be attributed by
        /// guesswork (the finding's first mechanism claim was wrong for exactly that reason).
        /// </summary>
        [Fact]
        public void AutoSelectRefusesOnTheBodyTermAndTheReportNamesIt()
        {
            var candidates = new List<TestCommandEnterWatchMode.WatchCandidate>
            {
                new TestCommandEnterWatchMode.WatchCandidate
                {
                    Index = 0, InScope = true,
                    HasActiveGhost = true, OnSameBody = false, WithinVisualRange = false,
                },
                new TestCommandEnterWatchMode.WatchCandidate
                {
                    Index = 1, InScope = true,
                    HasActiveGhost = true, OnSameBody = false, WithinVisualRange = true,
                },
                new TestCommandEnterWatchMode.WatchCandidate
                {
                    Index = 2, InScope = false,
                },
            };

            Assert.Equal(-1, TestCommandEnterWatchMode.ResolveAutoWatchIndex(candidates));

            string report = TestCommandEnterWatchMode.DescribeWatchCandidates(candidates);
            Assert.Equal("[0 ghost=T body=F range=F],[1 ghost=T body=F range=T],[2 scope=F]", report);
        }

        [Fact]
        public void DescribeWatchCandidates_HandlesEmptyAndNull()
        {
            Assert.Equal("(none)", TestCommandEnterWatchMode.DescribeWatchCandidates(null));
            Assert.Equal("(none)", TestCommandEnterWatchMode.DescribeWatchCandidates(
                new List<TestCommandEnterWatchMode.WatchCandidate>()));
        }

        /// <summary>
        /// The affordance used to tell a player 144 km from their own ghost, on the SAME body,
        /// that the ghost "is on a different body". The refusal is unchanged; the explanation is
        /// no longer false.
        /// </summary>
        [Fact]
        public void WatchButtonExplainsAnUnresolvedBodyAsNotRenderedRatherThanDifferentBody()
        {
            Assert.Equal("disabled (not rendered)",
                RecordingsTableUI.GetWatchButtonReason(
                    canWatch: false, hasGhost: true, sameBody: false, inRange: true,
                    isDebris: false, bodyReadingCurrent: false));

            Assert.Equal("disabled (different body)",
                RecordingsTableUI.GetWatchButtonReason(
                    canWatch: false, hasGhost: true, sameBody: false, inRange: true,
                    isDebris: false, bodyReadingCurrent: true));

            // The default keeps the historic meaning for callers that do not measure it.
            Assert.Equal("disabled (different body)",
                RecordingsTableUI.GetWatchButtonReason(
                    canWatch: false, hasGhost: true, sameBody: false, inRange: true,
                    isDebris: false));

            string unresolvedTooltip = RecordingsTableUI.GetWatchButtonTooltip(
                isWatching: false, hasGhost: true, sameBody: false, inRange: true,
                isDebris: false, bodyReadingCurrent: false);
            Assert.DoesNotContain("different body", unresolvedTooltip);
            Assert.Contains("too far away to be drawn", unresolvedTooltip);

            Assert.Equal("Ghost is on a different body",
                RecordingsTableUI.GetWatchButtonTooltip(
                    isWatching: false, hasGhost: true, sameBody: false, inRange: true,
                    isDebris: false, bodyReadingCurrent: true));
        }

        /// <summary>
        /// The Timeline W button reuses the table's strings, so it must carry the same split.
        /// </summary>
        [Fact]
        public void TimelineWatchDescriptorCarriesTheUnresolvedBodyExplanation()
        {
            var descriptor = TimelineWindowUI.BuildWatchButtonDescriptor(
                isWatching: false, hasGhost: true, sameBody: false, inRange: true,
                isDebris: false, bodyReadingCurrent: false);

            Assert.False(descriptor.CanWatch);
            Assert.Contains("too far away to be drawn", descriptor.Tooltip);
        }
    }
}
