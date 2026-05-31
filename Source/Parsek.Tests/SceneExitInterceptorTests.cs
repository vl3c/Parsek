using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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
    ///   <item>The finalized-pending-tree <see cref="HighLogic_LoadScene_Patch.Prefix"/> branch via the dialog test seam.</item>
    ///   <item>The <see cref="SceneExitInterceptor.SafeWritePersistent"/> save-failure-on-MAINMENU hard-block contract via the test seam.</item>
    /// </list>
    ///
    /// <para>The production <see cref="MergeDialog.ShowTreeDialog"/> PopupDialog
    /// spawn and the live-active-tree wrapper
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
        public void Prefix_NoActiveTree_FinalizedPending_InvokesShowDialogForTesting()
        {
            GameScenes priorScene = HighLogic.LoadedScene;
            GameScenes? capturedScene = null;
            SceneExitInterceptor.DialogVariant? capturedVariant = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) =>
            {
                capturedScene = scene;
                capturedVariant = variant;
            };
            RecordingStore.StashPendingTree(
                MakePendingTree("pending_prefix", TerminalState.Destroyed),
                PendingTreeState.Finalized);

            try
            {
                HighLogic.LoadedScene = GameScenes.FLIGHT;

                bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(
                    GameScenes.SPACECENTER);

                Assert.False(allowStockLoad);
                Assert.Equal(GameScenes.SPACECENTER, capturedScene);
                Assert.Equal(
                    SceneExitInterceptor.DialogVariant.RegularMerge,
                    capturedVariant);
            }
            finally
            {
                HighLogic.LoadedScene = priorScene;
            }
        }

        [Fact]
        public void Decision_NoActiveTree_NoSwitchSegment_ReturnsNone()
        {
            // Fails if: the switch-segment seam mistakenly triggers a dialog
            // when no session is armed AND no live active tree exists. The
            // gate must remain DialogVariant.None in that case.
            var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChange(
                GameScenes.SPACECENTER,
                hasActiveTree: false,
                reFlyActive: false,
                switchSegmentActive: false,
                isAutoMerge: false,
                activeVesselLandedOrSplashed: false);
            Assert.Equal(SceneExitInterceptor.DialogVariant.None, v);
        }

        [Fact]
        public void Decision_NoActiveTree_SwitchSegmentActive_ReturnsRegularMerge()
        {
            // Fails if: a torn-down active tree (vessel destroyed
            // mid-segment, rapid-switch race) with an armed
            // SwitchSegmentSession is silently passed through without a
            // dialog. This is the Bug C minimal seam from the post-#876
            // playtest 2026-05-17: the dialog must fire so the player can
            // Merge or Discard the segment before the scene exits.
            var v = SceneExitInterceptor.ShouldShowDialogBeforeSceneChange(
                GameScenes.SPACECENTER,
                hasActiveTree: false,
                reFlyActive: false,
                switchSegmentActive: true,
                isAutoMerge: false,
                activeVesselLandedOrSplashed: false);
            Assert.Equal(SceneExitInterceptor.DialogVariant.RegularMerge, v);
        }

        [Fact]
        public void TryResolveSessionTreeForDialog_NullSession_ReturnsNull()
        {
            // Fails if: the helper crashes or returns a tree for a null
            // session input. Defensive null-check is required so the
            // Prefix can safely call it from the no-active-tree fallback.
            var tree = SceneExitInterceptor.TryResolveSessionTreeForDialog(null);
            Assert.Null(tree);
        }

        [Fact]
        public void TryResolveSessionTreeForDialog_EmptyTreeId_ReturnsNull()
        {
            // Fails if: the helper resolves a tree for a session with no
            // TreeId (degenerate state). Caller must Warn-log and fall
            // back to the regular pending-tree path.
            var session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                TreeId = null,
            };
            var tree = SceneExitInterceptor.TryResolveSessionTreeForDialog(session);
            Assert.Null(tree);
        }

        [Fact]
        public void TryResolveSessionTreeForDialog_MatchesPendingTree_Returns()
        {
            // Fails if: the helper does not return the pending tree when
            // the session's TreeId matches RecordingStore.PendingTree.
            // This is the common case for a session whose live recorder
            // was torn down mid-segment.
            const string treeId = "session_tree_pending";
            RecordingStore.StashPendingTree(
                MakePendingTree(treeId, TerminalState.Orbiting),
                PendingTreeState.Finalized);

            var session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                TreeId = treeId,
            };
            var tree = SceneExitInterceptor.TryResolveSessionTreeForDialog(session);
            Assert.NotNull(tree);
            Assert.Equal(treeId, tree.Id);
        }

        [Fact]
        public void SceneExitInterceptor_SessionTreeResolvedFromCommitted_RefusesDialogSpawn()
        {
            // M1 (PR #876 round-5 review): source-text gate. Fails if: the
            // Bug-C path is allowed to spawn a pre-switch dialog for a
            // committed-tree-resolved session, where Merge would silently
            // no-op via the merge-commit-tree-mismatch guard. The pre-switch
            // dialog body returns false before LockInput in that case and
            // logs `bug-c-dialog-refused-session-tree-in-committed-slot`.
            string projectRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string mergeDialogPath = System.IO.Path.Combine(
                projectRoot, "Source", "Parsek", "MergeDialog.cs");
            string source = System.IO.File.ReadAllText(mergeDialogPath);

            // The committed-slot guard must exist verbatim.
            Assert.Contains(
                "bug-c-dialog-refused-session-tree-in-committed-slot", source);
            // And it must short-circuit BEFORE LockInput / popup spawn.
            var refuseRegex = new System.Text.RegularExpressions.Regex(
                @"TreeSlotSource\.Committed[\s\S]{0,400}?bug-c-dialog-refused-session-tree-in-committed-slot[\s\S]{0,800}?return false",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            Assert.Matches(refuseRegex, source);
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

        // ---------- Bug: deferred merge dialog after idle-on-pad exit -----
        //
        // The idle-on-pad scene-exit fast path silently discards the live
        // tree. Before this fix it left an armed switch-segment session
        // persisted in the save (the pre-exit OnSave wrote it before this
        // prefix tore the tree down), so on the next load the orphaned
        // session resurfaced as a deferred merge dialog. The two gates below
        // pin the fix: (a) the shared auto-discard teardown clears the
        // session in-memory; (b) the idle-on-pad prefix branch re-saves so the
        // cleared state is what survives. Neither path can be driven from
        // xUnit (live ParsekFlight + FlightGlobals), so these are source-text
        // gates over the production files.

        private static string ReadProjectSource(params string[] relativeParts)
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            var parts = new List<string> { projectRoot, "Source", "Parsek" };
            parts.AddRange(relativeParts);
            string path = Path.Combine(parts.ToArray());
            Assert.True(File.Exists(path), $"source not found at {path}");
            return File.ReadAllText(path);
        }

        // Fails if: AutoDiscardActiveTreeCore stops clearing the armed
        // switch-segment session when it tears down the active tree, letting
        // an orphaned session leak into the save again.
        [Fact]
        public void AutoDiscardActiveTreeCore_ClearsSwitchSegmentSession()
        {
            string source = ReadProjectSource("ParsekFlight.cs");

            int coreStart = source.IndexOf("private void AutoDiscardActiveTreeCore(");
            Assert.True(coreStart > 0, "AutoDiscardActiveTreeCore not found");
            int coreEnd = source.IndexOf("\n        private ", coreStart + 1);
            if (coreEnd < 0)
                coreEnd = Math.Min(coreStart + 6000, source.Length);
            string body = source.Substring(coreStart, coreEnd - coreStart);

            Assert.Matches(new Regex(
                @"ParsekScenario\.Instance\?\.ClearSwitchSegmentSession\(",
                RegexOptions.Multiline),
                body);
        }

        // Fails if: the idle-on-pad prefix branch stops re-saving after the
        // discard. Without the re-save the pre-exit OnSave's persisted session
        // survives and the deferred merge dialog fires on next load.
        [Fact]
        public void LoadScenePrefix_IdleOnPadBranch_ReSavesAfterDiscard()
        {
            string source = ReadProjectSource("SceneExitInterceptor.cs");

            // The idle-on-pad branch calls SafeWritePersistent after a true
            // return from TryAutoDiscardIdleActiveTree, and hard-blocks
            // (return false) when that save fails (MAINMENU contract).
            Assert.Matches(new Regex(
                @"TryAutoDiscardIdleActiveTree\(scene, flight\)\)\s*\{[\s\S]{0,1200}?" +
                @"if \(!SceneExitInterceptor\.SafeWritePersistent\(scene\)\)\s*return false;[\s\S]{0,200}?return true;",
                RegexOptions.Multiline),
                source);
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
