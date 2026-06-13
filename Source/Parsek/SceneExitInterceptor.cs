using System;
using System.Reflection;
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
        ///
        /// <para><see cref="GameScenes.LOADING"/> is the "not armed"
        /// sentinel: it is never a flight-exit destination so it cannot be
        /// confused with a real armed value. If KSP adds a new flight-exit
        /// destination in a future version, audit this sentinel.</para>
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
        /// Test seam: when non-null, the no-op switch-segment fast path invokes
        /// this instead of
        /// <see cref="ParsekFlight.AutoDiscardNoOpStandaloneSwitchSegment"/>.
        /// Receives the destination scene and the live flight controller.
        /// </summary>
        internal static Action<GameScenes, ParsekFlight> AutoDiscardNoOpSwitchSegmentForTesting;

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

        // Stock saveAndExit calls FlightGlobals.ClearpersistentIdDictionaries
        // (internal) before SaveGame and AnalyticsUtil.LogSaveGameClosed
        // (internal) after SaveGame on MAINMENU. Both are inaccessible from
        // outside Assembly-CSharp; reflect them like the in-game test runner
        // does at RuntimeTests.cs:6566-6568.
        private static readonly MethodInfo s_FlightGlobalsClearPersistentIdDictionariesMethod =
            typeof(FlightGlobals).GetMethod("ClearpersistentIdDictionaries",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>Test reset: clears all static state and seams.</summary>
        internal static void ResetTestOverrides()
        {
            s_AllowNextLoadScene = false;
            s_AllowNextLoadSceneDestination = GameScenes.LOADING;
            ShowDialogForTesting = null;
            AutoDiscardIdleForTesting = null;
            AutoDiscardNoOpSwitchSegmentForTesting = null;
            SafeWritePersistentForTesting = null;
        }

        /// <summary>
        /// Pure decision helper. Mirrors the OnLoad logic at
        /// <c>ParsekScenario.cs:1660-1723</c> so the pre-transition gate
        /// matches the existing post-load gate. Pure for unit-testability;
        /// the Prefix collects live state and passes it in.
        ///
        /// <para>Bug C overload: kept for back-compat with existing tests.
        /// New call sites should pass <c>switchSegmentActive</c> through
        /// the four-argument overload below so a pre-transition dialog
        /// fires when a switch-segment session is armed even if the live
        /// active tree has been torn down (rapid-switch races,
        /// vessel-destroyed-during-segment, etc.).</para>
        /// </summary>
        internal static DialogVariant ShouldShowDialogBeforeSceneChange(
            GameScenes destination,
            bool hasActiveTree,
            bool reFlyActive,
            bool isAutoMerge,
            bool activeVesselLandedOrSplashed)
        {
            return ShouldShowDialogBeforeSceneChange(
                destination,
                hasActiveTree: hasActiveTree,
                reFlyActive: reFlyActive,
                switchSegmentActive: false,
                isAutoMerge: isAutoMerge,
                activeVesselLandedOrSplashed: activeVesselLandedOrSplashed);
        }

        /// <summary>
        /// Pure decision helper with the Bug C switch-segment seam (post-#876
        /// playtest 2026-05-17). When an <see cref="SwitchSegmentSession"/>
        /// is armed at scene-exit time, the dialog must fire even if the
        /// live <see cref="ParsekFlight.HasActiveTree"/> check has gone false
        /// (e.g. the focused vessel was destroyed mid-segment so
        /// <see cref="ParsekFlight"/> tore down its active tree on
        /// <see cref="GameEvents.onVesselWillDestroy"/>). Without this the
        /// `Deferred merge dialog fired - pre-transition intercept missed`
        /// post-load fallback runs against the wrong pending tree.
        /// </summary>
        internal static DialogVariant ShouldShowDialogBeforeSceneChange(
            GameScenes destination,
            bool hasActiveTree,
            bool reFlyActive,
            bool switchSegmentActive,
            bool isAutoMerge,
            bool activeVesselLandedOrSplashed)
        {
            if (!hasActiveTree && !switchSegmentActive)
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
        /// Decision helper for trees that were finalized and stashed before
        /// the player picked Space Center / Tracking Station from stock's
        /// flight-results UI. Semantics intentionally mirror the live
        /// active-tree gate above; only the source of terminal-state evidence
        /// differs.
        /// </summary>
        internal static DialogVariant ShouldShowDialogBeforeSceneChangeForPendingTree(
            GameScenes destination,
            bool hasFinalizedPendingTree,
            bool reFlyActive,
            bool isAutoMerge,
            bool pendingRootLandedOrSplashed)
        {
            return ShouldShowDialogBeforeSceneChange(
                destination,
                hasActiveTree: hasFinalizedPendingTree,
                reFlyActive: reFlyActive,
                isAutoMerge: isAutoMerge,
                activeVesselLandedOrSplashed: pendingRootLandedOrSplashed);
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
            // Bug C (post-#876 playtest 2026-05-17): include switch-segment
            // sessions in the dialog gate so a torn-down active tree (vessel
            // destroyed mid-segment, rapid-switch fallthrough, etc.) still
            // surfaces the merge decision pre-transition. Without this, the
            // deferred post-load dialog runs against whichever pending tree
            // happens to be in the slot — which can be an orphan from a
            // prior session, the exact symptom Bug A/B were rooted in.
            bool switchSegmentActive =
                !object.ReferenceEquals(null, scenario)
                && scenario.ActiveSwitchSegmentSession != null;
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
                switchSegmentActive: switchSegmentActive,
                isAutoMerge: isAutoMerge,
                activeVesselLandedOrSplashed: landedOrSplashed);
        }

        /// <summary>
        /// Live-state wrapper for an already-finalized pending tree. This is
        /// the post-destruction / flight-results path: the active recorder was
        /// stopped before stock asks where to go next, so the prefix must look
        /// at <see cref="RecordingStore.PendingTree"/> instead of
        /// <see cref="ParsekFlight.HasActiveTree"/>.
        /// </summary>
        internal static DialogVariant ShouldShowPendingTreeDialogBeforeSceneChangeLive(
            GameScenes destination)
        {
            bool hasFinalizedPendingTree =
                RecordingStore.HasPendingTree
                && RecordingStore.PendingTreeStateValue == PendingTreeState.Finalized;
            var scenario = ParsekScenario.Instance;
            bool reFlyActive =
                !object.ReferenceEquals(null, scenario)
                && scenario.ActiveReFlySessionMarker != null;
            bool isAutoMerge = ParsekScenario.IsAutoMerge;
            bool pendingRootLandedOrSplashed = PendingTreeRootLandedOrSplashed();

            return ShouldShowDialogBeforeSceneChangeForPendingTree(
                destination,
                hasFinalizedPendingTree: hasFinalizedPendingTree,
                reFlyActive: reFlyActive,
                isAutoMerge: isAutoMerge,
                pendingRootLandedOrSplashed: pendingRootLandedOrSplashed);
        }

        /// <summary>
        /// Bug C helper: resolve the in-memory <see cref="RecordingTree"/>
        /// that owns the given <paramref name="session"/>, scanning the
        /// pending slot, the active tree, and the committed-trees list in
        /// that priority order. Returns null when the session's TreeId
        /// resolves to no live tree (degenerate state — the caller falls
        /// back to the normal pending-tree dialog path with a Warn log).
        ///
        /// <para>Priority order: Pending (sealed-but-not-yet-committed) →
        /// Active (live activeTree on <see cref="ParsekFlight"/>, including
        /// clone-restore wrappers) → Committed (terminal storage). The active
        /// slot wins over committed when an in-FLIGHT clone-restore is
        /// mid-flight, ensuring the segment-bearing clone is dialog-ed
        /// instead of the original committed tree. Invariant: no live
        /// <see cref="SwitchSegmentSession"/> should ever share its TreeId
        /// with a non-clone committed tree.</para>
        ///
        /// <para>M3 (PR #876 round-5 review): now delegates to
        /// <see cref="RecordingStore.TryResolveTreeById"/> — the canonical
        /// resolver shared with
        /// <see cref="MergeDialog.ShowPreSwitchDecisionDialog"/>. The two
        /// callers used to walk the same slots with diverging logic; the
        /// helper keeps them in lockstep so a future refactor cannot
        /// recreate the inconsistency.</para>
        /// </summary>
        internal static RecordingTree TryResolveSessionTreeForDialog(SwitchSegmentSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.TreeId))
                return null;

            RecordingStore.TryResolveTreeById(
                session.TreeId,
                out RecordingTree tree,
                out _);
            return tree;
        }

        private static bool PendingTreeRootLandedOrSplashed()
        {
            var tree = RecordingStore.PendingTree;
            if (tree == null || string.IsNullOrEmpty(tree.RootRecordingId))
                return false;

            Recording root;
            if (!tree.Recordings.TryGetValue(tree.RootRecordingId, out root))
                return false;

            // Finalized pending trees no longer have live vessel state here.
            // Use the root terminal state as the conservative #88 autoMerge
            // approval proxy; autoMerge-off and Re-Fly dialog paths ignore it.
            return root != null
                && (root.TerminalStateValue == TerminalState.Landed
                    || root.TerminalStateValue == TerminalState.Splashed);
        }

        /// <summary>
        /// Idle-on-pad fast path. Detects via
        /// <see cref="ParsekFlight.IsActiveTreeIdleOnPad"/> and, if idle,
        /// tears down the live recorder / activeTree via
        /// <see cref="ParsekFlight.AutoDiscardIdleActiveTree"/> (which also
        /// clears any armed <see cref="SwitchSegmentSession"/>). On return-true
        /// the caller re-saves persistent.sfs so the torn-down state survives;
        /// stock LoadScene then proceeds with no active tree;
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
        /// No-op resumed-segment fast path. When the armed
        /// <see cref="SwitchSegmentSession"/>'s segment (Tracking-Station Fly /
        /// KSC marker Fly / map Switch-To) changed nothing meaningful, drops it
        /// here so a boring segment that just prolongs the ghost state is not
        /// committed. Mirrors <see cref="TryAutoDiscardIdleActiveTree"/>: on
        /// return-true the caller re-saves persistent.sfs and lets the transition
        /// proceed with no active tree.
        ///
        /// <para>Disposition handling: <see cref="SwitchSegmentDisposition.Standalone"/>
        /// tears down the whole throwaway tree
        /// (<see cref="ParsekFlight.AutoDiscardNoOpStandaloneSwitchSegment"/>).
        /// <see cref="SwitchSegmentDisposition.CommittedRestoreClone"/> tears down
        /// the live clone and reverts to the committed original
        /// (<see cref="ParsekFlight.DiscardActiveSwitchSegmentAttemptRevertingLiveClone"/>)
        /// — the committed mission is preserved, the boring resume dropped, no
        /// dialog. <see cref="SwitchSegmentDisposition.BgMemberOrMixed"/> is
        /// DEFERRED (return false → normal commit path) because the rest of the
        /// live tree must still commit.</para>
        /// </summary>
        internal static bool TryAutoDiscardNoOpSwitchSegment(
            GameScenes destination, ParsekFlight flight)
        {
            if (flight == null) return false;
            if (!flight.TryEvaluateActiveSwitchSegmentNoOp(
                    out string reason, out SwitchSegmentDisposition disposition))
            {
                return false;
            }

            if (disposition != SwitchSegmentDisposition.Standalone
                && disposition != SwitchSegmentDisposition.CommittedRestoreClone)
            {
                ParsekLog.Info("SceneExit",
                    $"TryAutoDiscardNoOpSwitchSegment: no-op detected but disposition=" +
                    $"{disposition} is deferred at scene exit dest={destination} - " +
                    "falling through to normal commit");
                return false;
            }

            string discardReason =
                $"scene-exit no-op switch-segment auto-discard dest={destination}";
            ParsekLog.Info("SceneExit",
                $"TryAutoDiscardNoOpSwitchSegment: no-op switch segment ({disposition}) " +
                $"detected dest={destination} - discarding without commit");

            if (AutoDiscardNoOpSwitchSegmentForTesting != null)
            {
                AutoDiscardNoOpSwitchSegmentForTesting(destination, flight);
            }
            else if (disposition == SwitchSegmentDisposition.CommittedRestoreClone)
            {
                // Revert to the committed original (which stays in committedTrees,
                // copy-on-write); the boring resume segment + the clone are dropped.
                flight.DiscardActiveSwitchSegmentAttemptRevertingLiveClone(
                    reason: discardReason,
                    screenMessage: "Recording discarded - vessel unchanged after switch",
                    ledgerRecalcReason: "noop-switch-segment-discard-revert-clone",
                    out _);
            }
            else
            {
                flight.AutoDiscardNoOpStandaloneSwitchSegment(discardReason);
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

                // Stock saveAndExit calls FlightGlobals.ClearpersistentIdDictionaries
                // before SaveGame to null stale pid pointers. ProtoVessel.Save
                // and friends resolve live Part references through these
                // dictionaries; entries pointing at soon-to-be-destroyed
                // GameObjects could leak into the saved scenario module
                // state. Method is internal to Assembly-CSharp so we
                // reflect it (RuntimeTests.cs:6566 already does the same
                // for stock-fidelity test runs).
                if (s_FlightGlobalsClearPersistentIdDictionariesMethod != null)
                {
                    try
                    {
                        s_FlightGlobalsClearPersistentIdDictionariesMethod.Invoke(null, null);
                    }
                    catch (TargetInvocationException tex)
                    {
                        ParsekLog.Warn("SceneExit",
                            $"FlightGlobals.ClearpersistentIdDictionaries (reflected) threw " +
                            $"{tex.InnerException?.GetType().Name ?? tex.GetType().Name}: " +
                            $"{tex.InnerException?.Message ?? tex.Message}; continuing");
                    }
                    catch (Exception rex)
                    {
                        ParsekLog.Warn("SceneExit",
                            $"FlightGlobals.ClearpersistentIdDictionaries reflection invoke threw " +
                            $"{rex.GetType().Name}: {rex.Message}; continuing");
                    }
                }
                else
                {
                    ParsekLog.Warn("SceneExit",
                        "FlightGlobals.ClearpersistentIdDictionaries reflection unavailable - " +
                        "stale pid mappings may persist into saved scenario state");
                }

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
                if (destination == GameScenes.MAINMENU)
                {
                    ParsekLog.Error("SceneExit",
                        $"SafeWritePersistent threw {ex.GetType().Name}: {ex.Message} " +
                        $"dest={destination} - hard-blocking transition");
                    ShowSaveFailedPopup();
                    return false;
                }
                ParsekLog.Warn("SceneExit",
                    $"SafeWritePersistent threw {ex.GetType().Name}: {ex.Message} " +
                    $"dest={destination} - continuing transition (destination scene's " +
                    "save cycle will eventually persist)");
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
        ///
        /// <para>Token-set ordering: <see cref="SafeWritePersistent"/>
        /// fires <c>onSceneConfirmExit</c> BEFORE we set the bypass token.
        /// A foreign listener that synchronously calls
        /// <c>HighLogic.LoadScene</c> from its handler would hit our
        /// prefix without the token armed. By the time postChoice runs,
        /// <c>MergeCommit</c> / <c>MergeDiscard</c> already cleared
        /// <c>activeTree</c> + <c>pendingTree</c>, so the prefix's gate
        /// sees <c>HasActiveTree == false</c> and lets the foreign call
        /// through (no dialog re-prompt). Acceptable fallback.</para>
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

            // (3.5) no-op resumed-segment fast path. If the armed switch-segment
            //     session's segment (Fly / Switch-To) changed nothing meaningful
            //     and it is a standalone tree, tear it down here (mirrors the
            //     idle-on-pad fast path) so a boring segment that only prolongs
            //     the ghost state is not committed. Runs BEFORE the HasActiveTree
            //     routing check so that check is taken on post-discard state (the
            //     teardown nulls activeTree). Non-standalone dispositions defer.
            if (flight != null
                && SceneExitInterceptor.TryAutoDiscardNoOpSwitchSegment(scene, flight))
            {
                if (!SceneExitInterceptor.SafeWritePersistent(scene))
                    return false;   // MAINMENU save failed: hard-block
                return true;
            }

            if (flight == null || !flight.HasActiveTree)
            {
                // Bug C (post-#876 playtest 2026-05-17): an armed
                // SwitchSegmentSession with no live active tree means the
                // session's tree is in a non-active slot — pending tree,
                // saved-pending-during-active-restore, or already committed
                // (the recorder having been torn down by an
                // OnVesselWillDestroy seam mid-segment). Route the dialog
                // to the SESSION'S tree rather than blindly grabbing
                // RecordingStore.PendingTree, which can be an unrelated
                // orphan from a prior switch in the same FLIGHT scene
                // (this was the wrong-tree symptom that surfaced as Bug A).
                var scenarioForSession = ParsekScenario.Instance;
                var session = scenarioForSession?.ActiveSwitchSegmentSession;
                if (session != null)
                {
                    RecordingTree sessionTree =
                        SceneExitInterceptor.TryResolveSessionTreeForDialog(session);
                    if (sessionTree != null)
                    {
                        if (SceneExitInterceptor.ShowDialogForTesting != null)
                        {
                            SceneExitInterceptor.ShowDialogForTesting(
                                scene, SceneExitInterceptor.DialogVariant.RegularMerge);
                            return false;
                        }

                        ParsekLog.Info("SceneExit",
                            $"LoadScene prefix: showing pre-transition dialog for " +
                            $"switch-segment session tree '{sessionTree.TreeName ?? "<unnamed>"}' " +
                            $"sessionId={session.SessionId:D} dest={scene}");

                        MergeDialog.ShowTreeDialog(
                            sessionTree,
                            labels: MergeDialog.MergeDialogButtonLabels.Default,
                            preCommitFinalize: () => { },
                            postChoice: SceneExitInterceptor.BuildPostChoice(scene));

                        return false;
                    }
                    ParsekLog.Warn("SceneExit",
                        $"LoadScene prefix: switch-segment session armed but tree " +
                        $"id={session.TreeId ?? "<null>"} resolves to no in-memory tree " +
                        $"sessionId={session.SessionId:D} - falling back to pending-tree branch");
                }

                var pendingVariant =
                    SceneExitInterceptor.ShouldShowPendingTreeDialogBeforeSceneChangeLive(scene);
                if (pendingVariant == SceneExitInterceptor.DialogVariant.None)
                    return true;

                if (SceneExitInterceptor.ShowDialogForTesting != null)
                {
                    SceneExitInterceptor.ShowDialogForTesting(scene, pendingVariant);
                    return false;
                }

                var pendingLabels = pendingVariant == SceneExitInterceptor.DialogVariant.ReFlyAttempt
                    ? MergeDialog.MergeDialogButtonLabels.ReFlyAttempt
                    : MergeDialog.MergeDialogButtonLabels.Default;

                ParsekLog.Info("SceneExit",
                    $"LoadScene prefix: showing pre-transition dialog for finalized pending tree " +
                    $"'{RecordingStore.PendingTree?.TreeName ?? "<null>"}' dest={scene}");

                MergeDialog.ShowTreeDialog(
                    RecordingStore.PendingTree,
                    labels: pendingLabels,
                    preCommitFinalize: () => { },
                    postChoice: SceneExitInterceptor.BuildPostChoice(scene));

                return false;
            }

            // (4) decision matrix on LIVE state.
            var variant = SceneExitInterceptor.ShouldShowDialogBeforeSceneChangeLive(
                scene, flight);
            if (variant == SceneExitInterceptor.DialogVariant.None)
                return true;

            // (5) idle-on-pad auto-discard fast path. Both detects AND
            //     mutates: tears down activeTree / recorder /
            //     backgroundRecorder (and clears any armed switch-segment
            //     session) so OnSceneChangeRequested sees nothing to finalize.
            if (SceneExitInterceptor.TryAutoDiscardIdleActiveTree(scene, flight))
            {
                // The pre-exit OnSave (stock saveAndExit, which runs before
                // this prefix) already persisted the now-discarded active tree
                // plus any armed switch-segment session into persistent.sfs.
                // Re-save so the torn-down state is what survives; otherwise a
                // dangling session resurfaces as a deferred merge dialog on the
                // next load. Mirrors BuildPostChoice's post-Discard
                // SafeWritePersistent (the dialog path already re-saves here;
                // the idle fast path historically skipped it).
                if (!SceneExitInterceptor.SafeWritePersistent(scene))
                    return false;   // MAINMENU save failed: hard-block
                return true;
            }

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
