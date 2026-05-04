using System;
using HarmonyLib;

namespace Parsek
{
    /// <summary>
    /// Pre-transition merge confirmation: shows the
    /// <see cref="MergeDialog.ShowTreeDialog"/> while the player is still in
    /// flight, before <c>HighLogic.LoadScene</c> tears down the flight scene
    /// for Space Center / Tracking Station / Main Menu / Editor.
    ///
    /// <para>The Harmony Prefix on <see cref="HighLogic.LoadScene(GameScenes)"/>
    /// is a single chokepoint that catches every flight-exit path: PauseMenu's
    /// <c>saveAndExit</c> branch, PauseMenu's <c>CanRestart</c> no-save branch
    /// (direct LoadScene), and FlightResultsDialog's KSC / Menu / TS buttons
    /// (also direct LoadScene). By the time <c>HighLogic.LoadScene</c> runs,
    /// stock confirmation popups (atmosphere / throttle warning, quit-to-main
    /// confirmation) have already been confirmed by the user.</para>
    ///
    /// <para>The prefix does NOT pre-finalize the active tree before showing
    /// the dialog. Pre-finalize would call <c>recorder.ForceStop()</c>
    /// (<c>FlightRecorder.cs:11218</c>) and clear <c>activeTree</c>; if the
    /// popup is dismissed without a button click the player would be stranded
    /// in flight with a dead recorder. Instead, the dialog operates on the
    /// live <c>activeTree</c> and the button-handler wrapper inside
    /// <see cref="MergeDialog.ShowTreeDialog"/> runs preCommitFinalize ->
    /// MergeCommit/Discard -> postChoice in order, so finalize only happens
    /// when the player commits to a choice.</para>
    /// </summary>
    internal static class SceneExitInterceptor
    {
        /// <summary>
        /// One-shot bypass token: set to true by the dialog's postChoice
        /// just before re-invoking <c>HighLogic.LoadScene</c>. Consumed
        /// (with destination match check) by the next prefix entry.
        /// </summary>
        internal static bool s_AllowNextLoadScene;

        /// <summary>
        /// Paired expected-destination for the <see cref="s_AllowNextLoadScene"/>
        /// token. Mismatch on consume means a foreign LoadScene call slipped
        /// in between our set and our prefix entry; we Warn and fall through
        /// to normal handling rather than silently letting the foreign call
        /// bypass the dialog.
        /// </summary>
        internal static GameScenes s_AllowNextLoadSceneDestination = GameScenes.LOADING;

        /// <summary>
        /// Test seam: when non-null, the prefix invokes this instead of
        /// spawning a real <see cref="MergeDialog.ShowTreeDialog"/> popup.
        /// Receives the destination scene and the dialog variant.
        /// </summary>
        internal static Action<GameScenes, DialogVariant> ShowDialogForTesting;

        /// <summary>
        /// Test seam: when non-null, the prefix invokes this instead of
        /// running <see cref="ParsekFlight.AutoDiscardIdleActiveTree"/>
        /// for the idle-on-pad fast path. Receives the reason string.
        /// </summary>
        internal static Action<string> AutoDiscardIdleForTesting;

        /// <summary>
        /// Test seam: when non-null, the prefix calls this instead of
        /// <see cref="GamePersistence.SaveGame"/>. Receives the destination
        /// scene; returns true on success, false to simulate save failure.
        /// </summary>
        internal static Func<GameScenes, bool> SafeWritePersistentForTesting;

        internal enum DialogVariant
        {
            /// <summary>No dialog needed - prefix returns true.</summary>
            None,
            /// <summary>Regular merge dialog with default labels.</summary>
            RegularMerge,
            /// <summary>Re-Fly attempt-scoped dialog with Re-Fly labels.</summary>
            ReFlyAttempt,
        }

        /// <summary>Test reset: clears all static state and seams.</summary>
        internal static void ResetTestOverrides()
        {
            s_AllowNextLoadScene = false;
            s_AllowNextLoadSceneDestination = GameScenes.LOADING;
            ShowDialogForTesting = null;
            AutoDiscardIdleForTesting = null;
            SafeWritePersistentForTesting = null;
        }

        /// <summary>
        /// Pure decision helper. Mirrors the OnLoad logic at
        /// <c>ParsekScenario.cs:1660-1723</c> so the pre-transition gate
        /// matches the existing post-load gate. Pure for unit-testability;
        /// the Prefix collects live state and passes it in.
        /// </summary>
        internal static DialogVariant ShouldShowDialogBeforeSceneChange(
            GameScenes destination,
            bool hasActiveTree,
            bool reFlyActive,
            bool isAutoMerge,
            bool activeVesselLandedOrSplashed)
        {
            if (!hasActiveTree)
                return DialogVariant.None;

            if (reFlyActive)
                return DialogVariant.ReFlyAttempt;

            if (!isAutoMerge)
                return DialogVariant.RegularMerge;

            if (destination == GameScenes.MAINMENU)
                return DialogVariant.RegularMerge;

            if ((destination == GameScenes.SPACECENTER
                 || destination == GameScenes.TRACKSTATION)
                && activeVesselLandedOrSplashed)
            {
                return DialogVariant.RegularMerge;
            }

            return DialogVariant.None;
        }

        /// <summary>
        /// Live-state wrapper that gathers values from
        /// <see cref="FlightGlobals"/> / <see cref="ParsekScenario"/> and
        /// dispatches to the pure helper. Used by the prefix.
        /// </summary>
        internal static DialogVariant ShouldShowDialogBeforeSceneChangeLive(
            GameScenes destination, ParsekFlight flight)
        {
            bool hasActiveTree = flight != null && flight.HasActiveTree;
            var scenario = ParsekScenario.Instance;
            bool reFlyActive =
                !object.ReferenceEquals(null, scenario)
                && scenario.ActiveReFlySessionMarker != null;
            bool isAutoMerge = ParsekScenario.IsAutoMerge;

            // Mirrors ShouldShowCommitApproval but reads live vessel
            // situation since post-finalize TerminalStateValue isn't
            // populated yet (finalize deferred to the dialog callback).
            // The two values agree on outcome:
            // RecordingTree.DetermineTerminalState override paths only fire
            // for SUB_ORBITAL/ORBITING base states; LANDED/SPLASHED pass
            // through unchanged.
            var v = FlightGlobals.ActiveVessel;
            bool landedOrSplashed =
                v != null
                && (v.situation == Vessel.Situations.LANDED
                    || v.situation == Vessel.Situations.SPLASHED);

            return ShouldShowDialogBeforeSceneChange(
                destination,
                hasActiveTree: hasActiveTree,
                reFlyActive: reFlyActive,
                isAutoMerge: isAutoMerge,
                activeVesselLandedOrSplashed: landedOrSplashed);
        }

        /// <summary>
        /// Idle-on-pad fast path. Detects via
        /// <see cref="ParsekFlight.IsActiveTreeIdleOnPad"/> and, if idle,
        /// tears down the live recorder / activeTree via
        /// <see cref="ParsekFlight.AutoDiscardIdleActiveTree"/>. After
        /// return-true, stock LoadScene proceeds with no active tree;
        /// OnSceneChangeRequested no-ops; destination scene loads with no
        /// pending tree, so the OnLoad idle-on-pad branch
        /// (<c>ParsekScenario.cs:1682-1689</c>) does not fire.
        /// </summary>
        internal static bool TryAutoDiscardIdleActiveTree(
            GameScenes destination, ParsekFlight flight)
        {
            if (flight == null || !flight.HasActiveTree) return false;
            if (!flight.IsActiveTreeIdleOnPad()) return false;

            string reason = $"scene-exit idle-on-pad auto-discard dest={destination}";
            ParsekLog.Info("SceneExit",
                $"TryAutoDiscardIdleActiveTree: idle detected dest={destination} - " +
                "tearing down live tree without finalize/stash");

            if (AutoDiscardIdleForTesting != null)
            {
                AutoDiscardIdleForTesting(reason);
            }
            else
            {
                flight.AutoDiscardIdleActiveTree(reason);
            }
            return true;
        }

        /// <summary>
        /// Pre-LoadScene save: persist Parsek's mutations. For paths where
        /// stock <c>saveAndExit</c> already saved (PauseMenu paths), our
        /// re-save runs after our mutations on top of stock's earlier save.
        /// For paths where stock did NOT save (PauseMenu CanRestart no-save,
        /// FlightResultsDialog direct LoadScene), our save is the only
        /// source. Mirrors the full stock saveAndExit prep
        /// (<c>onSceneConfirmExit</c> fire +
        /// <c>ClearpersistentIdDictionaries</c> + <c>SaveGame</c> +
        /// MAINMENU analytics) so paths that bypassed stock prep get the
        /// same prep here.
        ///
        /// <para>Returns false ONLY for MAINMENU on save throw (hard-block
        /// the transition; no later save will run before unload). Other
        /// destinations log Warn and continue (the destination scene's own
        /// save cycle eventually persists).</para>
        /// </summary>
        internal static bool SafeWritePersistent(GameScenes destination)
        {
            if (SafeWritePersistentForTesting != null)
                return SafeWritePersistentForTesting(destination);

            try
            {
                if (HighLogic.CurrentGame != null)
                    GameEvents.onSceneConfirmExit.Fire(HighLogic.CurrentGame.startScene);

                // Stock saveAndExit also calls FlightGlobals.ClearpersistentIdDictionaries
                // (internal) and AnalyticsUtil.LogSaveGameClosed (internal),
                // but both are inaccessible from outside Assembly-CSharp.
                // Skipping is safe: ClearpersistentIdDictionaries clears a
                // runtime cache that is rebuilt on the next scene load (the
                // cache is not serialized, so the saved persistent.sfs is
                // unaffected). The analytics call is non-functional. If
                // these become observably needed in playtest, switch to
                // reflection.

                if (HighLogic.CurrentGame == null)
                {
                    ParsekLog.Warn("SceneExit",
                        "SafeWritePersistent: HighLogic.CurrentGame is null - skipping save");
                    return true;
                }
                GamePersistence.SaveGame(
                    HighLogic.CurrentGame.Updated(),
                    "persistent",
                    HighLogic.SaveFolder,
                    SaveMode.OVERWRITE);
                ParsekLog.Info("SceneExit",
                    $"SafeWritePersistent: persistent.sfs written dest={destination}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("SceneExit",
                    $"SafeWritePersistent threw {ex.GetType().Name}: {ex.Message} dest={destination}");
                if (destination == GameScenes.MAINMENU)
                {
                    ShowSaveFailedPopup();
                    return false;
                }
                return true;
            }
        }

        private static void ShowSaveFailedPopup()
        {
            try
            {
                PopupDialog.SpawnPopupDialog(
                    new UnityEngine.Vector2(0.5f, 0.5f),
                    new UnityEngine.Vector2(0.5f, 0.5f),
                    new MultiOptionDialog(
                        "ParsekSceneExitSaveFailed",
                        "Could not save before quitting to main menu. " +
                        "Try again, or quit to Space Center first.",
                        "Save failed",
                        HighLogic.UISkin,
                        new DialogGUIButton("OK", () => { }, dismissOnSelect: true)),
                    persistAcrossScenes: false,
                    HighLogic.UISkin);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("SceneExit",
                    $"ShowSaveFailedPopup: SpawnPopupDialog threw " +
                    $"{ex.GetType().Name}: {ex.Message}; player gets no UI feedback");
            }
        }

        /// <summary>
        /// Builds the postChoice closure invoked after the dialog's
        /// preCommitFinalize + MergeCommit/Discard. Persists state and
        /// re-invokes <c>HighLogic.LoadScene</c> with the bypass token set.
        /// </summary>
        internal static Action BuildPostChoice(GameScenes destination)
        {
            return () =>
            {
                if (!SafeWritePersistent(destination))
                    return;
                s_AllowNextLoadScene = true;
                s_AllowNextLoadSceneDestination = destination;
                HighLogic.LoadScene(destination);
            };
        }
    }

    /// <summary>
    /// Harmony Prefix on <see cref="HighLogic.LoadScene(GameScenes)"/>.
    /// Filtered to flight-exit transitions; runs after any other mod
    /// prefix (HarmonyPriority.Last) so we only block when everyone else
    /// allowed it.
    /// </summary>
    [HarmonyPatch(typeof(HighLogic), nameof(HighLogic.LoadScene), new[] { typeof(GameScenes) })]
    [HarmonyPriority(Priority.Last)]
    internal static class HighLogic_LoadScene_Patch
    {
        [HarmonyPrefix]
        internal static bool Prefix(GameScenes scene)
        {
            // (1) one-shot self-bypass token: our own dialog callback
            //     re-invoked LoadScene. Includes a destination check so
            //     a stray foreign LoadScene between our callback's set
            //     and prefix's consume cannot silently steal the token.
            if (SceneExitInterceptor.s_AllowNextLoadScene)
            {
                var expected = SceneExitInterceptor.s_AllowNextLoadSceneDestination;
                SceneExitInterceptor.s_AllowNextLoadScene = false;
                SceneExitInterceptor.s_AllowNextLoadSceneDestination = GameScenes.LOADING;
                if (expected == scene)
                {
                    ParsekLog.Verbose("SceneExit",
                        $"LoadScene prefix: bypassing via s_AllowNextLoadScene dest={scene}");
                    return true;
                }
                ParsekLog.Warn("SceneExit",
                    $"LoadScene prefix: token consumed for unexpected dest={scene} " +
                    $"(expected {expected}); falling through to normal handling");
                // Fall through intentionally: a foreign caller stole our
                // token. The player's Merge/Discard choice still applies
                // to the CURRENT scene-exit attempt - re-evaluate the
                // dialog gate against `scene`. Do NOT return-true here -
                // that would let the foreign transition skip the merge
                // confirmation.
            }

            // (2) cheap filter - only intercept exits from FLIGHT to a
            //     flight-exit destination. dest == FLIGHT (quickload,
            //     RewindInvoker, vessel switch) passes through.
            if (HighLogic.LoadedScene != GameScenes.FLIGHT) return true;
            if (scene != GameScenes.SPACECENTER
                && scene != GameScenes.TRACKSTATION
                && scene != GameScenes.MAINMENU
                && scene != GameScenes.EDITOR)
            {
                return true;
            }

            // (3) PEEK existing Discard-Re-Fly suppression. If
            //     RevertInterceptor armed ArmNextTreeSceneExitCommitSuppression,
            //     this transition is already owned by Discard Re-Fly - get
            //     out of the way without consuming the flag
            //     (FinalizeTreeOnSceneChange consumes it at
            //     ParsekFlight.cs:2734).
            if (RecordingStore.IsNextTreeSceneExitCommitSuppressionArmed)
            {
                ParsekLog.Info("SceneExit",
                    $"LoadScene prefix: bypassing - existing tree-scene-exit-commit " +
                    $"suppression armed (Discard Re-Fly path) dest={scene}");
                return true;
            }

            var flight = ParsekFlight.Instance;
            if (flight == null || !flight.HasActiveTree)
                return true;

            // (4) decision matrix on LIVE state.
            var variant = SceneExitInterceptor.ShouldShowDialogBeforeSceneChangeLive(
                scene, flight);
            if (variant == SceneExitInterceptor.DialogVariant.None)
                return true;

            // (5) idle-on-pad auto-discard fast path. Both detects AND
            //     mutates: tears down activeTree / recorder /
            //     backgroundRecorder so OnSceneChangeRequested sees
            //     nothing to finalize.
            if (SceneExitInterceptor.TryAutoDiscardIdleActiveTree(scene, flight))
                return true;

            // (6) show dialog on the live activeTree. ShowTreeDialog's
            //     button-handler wrapper runs preCommitFinalize ->
            //     MergeCommit/MergeDiscard -> postChoice in order.
            if (SceneExitInterceptor.ShowDialogForTesting != null)
            {
                SceneExitInterceptor.ShowDialogForTesting(scene, variant);
                return false;
            }

            var labels = variant == SceneExitInterceptor.DialogVariant.ReFlyAttempt
                ? MergeDialog.MergeDialogButtonLabels.ReFlyAttempt
                : MergeDialog.MergeDialogButtonLabels.Default;

            MergeDialog.ShowTreeDialog(
                flight.ActiveTreeForDisplay,
                labels: labels,
                preCommitFinalize: () => flight.FinalizeTreeOnSceneChangeForCallback(scene),
                postChoice: SceneExitInterceptor.BuildPostChoice(scene));

            return false;   // block stock LoadScene; dialog drives it
        }
    }
}
