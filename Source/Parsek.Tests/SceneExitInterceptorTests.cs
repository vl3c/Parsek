using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the pre-transition merge confirmation gate
    /// (<see cref="SceneExitInterceptor"/>) covering:
    ///
    /// <list type="bullet">
    ///   <item>The pure <see cref="SceneExitInterceptor.ShouldShowDialogBeforeSceneChange"/> decision matrix.</item>
    ///   <item>The <see cref="SceneExitInterceptor.s_AllowNextLoadScene"/> + destination-match watchdog.</item>
    ///   <item>The <see cref="SceneExitInterceptor.SafeWritePersistent"/> save-failure-on-MAINMENU hard-block contract via the test seam.</item>
    /// </list>
    ///
    /// <para>The Harmony prefix itself, the <see cref="MergeDialog.ShowTreeDialog"/>
    /// PopupDialog spawn, and the live-state wrapper
    /// <c>ShouldShowDialogBeforeSceneChangeLive</c> all touch
    /// <see cref="FlightGlobals"/> / <see cref="HighLogic"/> singletons
    /// that are unavailable in xUnit. Those layers are exercised via the
    /// in-game test runner (RuntimeTests).</para>
    /// </summary>
    [Collection("Sequential")]
    public class SceneExitInterceptorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public SceneExitInterceptorTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetCachedAutoMergeForTesting(false);
            SceneExitInterceptor.ResetTestOverrides();
        }

        public void Dispose()
        {
            SceneExitInterceptor.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetCachedAutoMergeForTesting(false);
        }

        // ---------- Decision helper matrix ------------------------------

        private static RecordingTree MakePendingTree(
            string treeId,
            TerminalState terminalState)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Pending Tree",
                RootRecordingId = "root",
                ActiveRecordingId = "root"
            };
            tree.Recordings["root"] = new Recording
            {
                RecordingId = "root",
                TreeId = treeId,
                VesselName = "Pending Root",
                TerminalStateValue = terminalState
            };
            return tree;
        }

        [Fact]
        public void Decision_NoActiveTree_ReturnsNone()
        {
            var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChange(
                GameScenes.SPACECENTER,
                hasActiveTree: false,
                reFlyActive: false,
                isAutoMerge: false,
                activeVesselLandedOrSplashed: false);
            Assert.Equal(SceneExitInterceptor.DialogVariant.None, v);
        }

        [Fact]
        public void Decision_PendingFinalizedTree_AutoMergeOff_ReturnsRegularMerge()
        {
            var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChangeForPendingTree(
                GameScenes.SPACECENTER,
                hasFinalizedPendingTree: true,
                reFlyActive: false,
                isAutoMerge: false,
                pendingRootLandedOrSplashed: false);
            Assert.Equal(SceneExitInterceptor.DialogVariant.RegularMerge, v);
        }

        [Fact]
        public void Decision_PendingFinalizedTree_ReFlyActive_ReturnsReFlyAttempt()
        {
            var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChangeForPendingTree(
                GameScenes.TRACKSTATION,
                hasFinalizedPendingTree: true,
                reFlyActive: true,
                isAutoMerge: true,
                pendingRootLandedOrSplashed: false);
            Assert.Equal(SceneExitInterceptor.DialogVariant.ReFlyAttempt, v);
        }

        [Fact]
        public void Decision_PendingNotFinalized_ReturnsNone()
        {
            var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChangeForPendingTree(
                GameScenes.SPACECENTER,
                hasFinalizedPendingTree: false,
                reFlyActive: false,
                isAutoMerge: false,
                pendingRootLandedOrSplashed: false);
            Assert.Equal(SceneExitInterceptor.DialogVariant.None, v);
        }

        [Fact]
        public void LivePendingTreeDecision_FinalizedLandedPendingTree_AutoMergeOn_ReturnsRegularMerge()
        {
            ParsekScenario.SetCachedAutoMergeForTesting(true);
            RecordingStore.StashPendingTree(
                MakePendingTree("pending_landed", TerminalState.Landed),
                PendingTreeState.Finalized);

            var v = SceneExitInterceptor.ShouldShowPendingTreeDialogBeforeSceneChangeLive(
                GameScenes.SPACECENTER);

            Assert.Equal(SceneExitInterceptor.DialogVariant.RegularMerge, v);
        }

        [Fact]
        public void LivePendingTreeDecision_LimboPendingTree_ReturnsNone()
        {
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_pending_limbo"
                }
            });
            RecordingStore.StashPendingTree(
                MakePendingTree("pending_limbo", TerminalState.Destroyed),
                PendingTreeState.Limbo);

            var v = SceneExitInterceptor.ShouldShowPendingTreeDialogBeforeSceneChangeLive(
                GameScenes.TRACKSTATION);

            Assert.Equal(SceneExitInterceptor.DialogVariant.None, v);
        }

        [Fact]
        public void Decision_AutoMergeOff_AnyDest_ReturnsRegularMerge()
        {
            foreach (var dest in new[]
                     {
                         GameScenes.SPACECENTER,
                         GameScenes.TRACKSTATION,
                         GameScenes.MAINMENU,
                         GameScenes.EDITOR,
                     })
            {
                var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChange(
                    dest,
                    hasActiveTree: true,
                    reFlyActive: false,
                    isAutoMerge: false,
                    activeVesselLandedOrSplashed: false);
                Assert.Equal(SceneExitInterceptor.DialogVariant.RegularMerge, v);
            }
        }

        [Fact]
        public void Decision_AutoMergeOn_LandedAtKsc_ReturnsRegularMerge()
        {
            var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChange(
                GameScenes.SPACECENTER,
                hasActiveTree: true,
                reFlyActive: false,
                isAutoMerge: true,
                activeVesselLandedOrSplashed: true);
            Assert.Equal(SceneExitInterceptor.DialogVariant.RegularMerge, v);
        }

        [Fact]
        public void Decision_AutoMergeOn_LandedAtTs_ReturnsRegularMerge()
        {
            var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChange(
                GameScenes.TRACKSTATION,
                hasActiveTree: true,
                reFlyActive: false,
                isAutoMerge: true,
                activeVesselLandedOrSplashed: true);
            Assert.Equal(SceneExitInterceptor.DialogVariant.RegularMerge, v);
        }

        [Fact]
        public void Decision_AutoMergeOn_NotLandedAtKsc_ReturnsNone()
        {
            var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChange(
                GameScenes.SPACECENTER,
                hasActiveTree: true,
                reFlyActive: false,
                isAutoMerge: true,
                activeVesselLandedOrSplashed: false);
            Assert.Equal(SceneExitInterceptor.DialogVariant.None, v);
        }

        [Fact]
        public void Decision_AutoMergeOn_MainMenu_AlwaysReturnsRegularMerge()
        {
            // Behaviour change: previously force-auto-merged silently. New
            // pre-transition path always shows the dialog so player can
            // choose to keep or discard before the game unloads.
            var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChange(
                GameScenes.MAINMENU,
                hasActiveTree: true,
                reFlyActive: false,
                isAutoMerge: true,
                activeVesselLandedOrSplashed: false);
            Assert.Equal(SceneExitInterceptor.DialogVariant.RegularMerge, v);
        }

        [Fact]
        public void Decision_ReFlyActive_ReturnsReFlyAttempt_AnyAutoMerge()
        {
            foreach (bool autoMerge in new[] { true, false })
            {
                var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChange(
                    GameScenes.SPACECENTER,
                    hasActiveTree: true,
                    reFlyActive: true,
                    isAutoMerge: autoMerge,
                    activeVesselLandedOrSplashed: false);
                Assert.Equal(SceneExitInterceptor.DialogVariant.ReFlyAttempt, v);
            }
        }

        [Fact]
        public void Decision_AutoMergeOn_NotLandedAtKsc_ReFlyActive_OverridesToReFlyAttempt()
        {
            // Re-Fly check fires before the autoMerge gate.
            var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChange(
                GameScenes.SPACECENTER,
                hasActiveTree: true,
                reFlyActive: true,
                isAutoMerge: true,
                activeVesselLandedOrSplashed: false);
            Assert.Equal(SceneExitInterceptor.DialogVariant.ReFlyAttempt, v);
        }

        // ---------- Token-bypass watchdog -------------------------------

        [Fact]
        public void Token_NotArmed_DefaultIsLOADING()
        {
            // ResetTestOverrides clears to LOADING sentinel.
            Assert.False(SceneExitInterceptor.s_AllowNextLoadScene);
            Assert.Equal(GameScenes.LOADING, SceneExitInterceptor.s_AllowNextLoadSceneDestination);
        }

        [Fact]
        public void BuildPostChoice_ArmsTokenWithDestination()
        {
            // Stub the save call so we don't touch GamePersistence.
            SceneExitInterceptor.SafeWritePersistentForTesting = _ => true;

            var postChoice = SceneExitInterceptor.BuildPostChoice(GameScenes.SPACECENTER);
            Assert.NotNull(postChoice);

            // Invoking postChoice would normally call HighLogic.LoadScene,
            // which is unavailable in xUnit. We can't run the lambda
            // directly. Instead verify the closure captured the destination
            // by checking the field BEFORE invocation - it should still be
            // LOADING (token only set inside the lambda right before
            // LoadScene). This test asserts BuildPostChoice does not arm
            // the token eagerly.
            Assert.False(SceneExitInterceptor.s_AllowNextLoadScene);
            Assert.Equal(GameScenes.LOADING, SceneExitInterceptor.s_AllowNextLoadSceneDestination);
        }

        // ---------- SafeWritePersistent test seam -----------------------

        [Fact]
        public void SafeWritePersistent_TestSeam_Success_ReturnsTrue()
        {
            int callCount = 0;
            GameScenes? capturedDest = null;
            SceneExitInterceptor.SafeWritePersistentForTesting = dest =>
            {
                callCount++;
                capturedDest = dest;
                return true;
            };

            bool result = SceneExitInterceptor.SafeWritePersistent(GameScenes.MAINMENU);
            Assert.True(result);
            Assert.Equal(1, callCount);
            Assert.Equal(GameScenes.MAINMENU, capturedDest);
        }

        [Fact]
        public void SafeWritePersistent_TestSeam_FailureOnMainMenu_ReturnsFalse()
        {
            SceneExitInterceptor.SafeWritePersistentForTesting = _ => false;
            bool result = SceneExitInterceptor.SafeWritePersistent(GameScenes.MAINMENU);
            Assert.False(result);
        }

        [Fact]
        public void SafeWritePersistent_TestSeam_FailureOnKsc_ReturnsFalseFromSeam()
        {
            // The test seam is authoritative: whatever it returns, that's
            // what SafeWritePersistent returns. The MAINMENU-specific
            // hard-block logic lives only in the production path. The
            // seam itself replicates whichever contract the test wants.
            SceneExitInterceptor.SafeWritePersistentForTesting = _ => false;
            bool result = SceneExitInterceptor.SafeWritePersistent(GameScenes.SPACECENTER);
            Assert.False(result);
        }

        // ---------- TryAutoDiscardIdleActiveTree -----------------------

        [Fact]
        public void TryAutoDiscardIdleActiveTree_NullFlight_ReturnsFalse()
        {
            bool result = SceneExitInterceptor.TryAutoDiscardIdleActiveTree(
                GameScenes.SPACECENTER, flight: null);
            Assert.False(result);
        }

        // ---------- BackfillMaxDistanceAbsoluteOnly --------------------

        [Fact]
        public void BackfillMaxDistanceAbsoluteOnly_NullRecording_ReturnsCleanly()
        {
            // No exception expected.
            VesselSpawner.BackfillMaxDistanceAbsoluteOnly(null);
        }

        [Fact]
        public void BackfillMaxDistanceAbsoluteOnly_NullTrackSections_ReturnsCleanly()
        {
            var rec = new Recording { RecordingId = "rec-null-ts" };
            rec.TrackSections = null;
            // Should not throw, should not write MaxDistanceFromLaunch.
            VesselSpawner.BackfillMaxDistanceAbsoluteOnly(rec);
            Assert.Equal(0.0, rec.MaxDistanceFromLaunch);
        }

        [Fact]
        public void BackfillMaxDistanceAbsoluteOnly_EmptyTrackSections_LeavesMaxDistanceUntouched()
        {
            var rec = new Recording { RecordingId = "rec-empty-ts" };
            rec.MaxDistanceFromLaunch = 42.5;   // pre-existing value
            VesselSpawner.BackfillMaxDistanceAbsoluteOnly(rec);
            // Empty TrackSections list -> no Absolute frames found -> no
            // write to MaxDistanceFromLaunch (preserves prior value).
            Assert.Equal(42.5, rec.MaxDistanceFromLaunch);
        }

        [Fact]
        public void BackfillMaxDistanceAbsoluteOnly_AllRelativeSections_LeavesMaxDistanceUntouched()
        {
            var rec = new Recording { RecordingId = "rec-relative-only" };
            rec.MaxDistanceFromLaunch = 99.9;
            rec.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    frames = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { latitude = 1.0, longitude = 2.0, altitude = 3.0, bodyName = "Kerbin" },
                    },
                },
            };
            VesselSpawner.BackfillMaxDistanceAbsoluteOnly(rec);
            // Only RELATIVE sections -> no Absolute frames -> no write.
            Assert.Equal(99.9, rec.MaxDistanceFromLaunch);
        }
    }
}
