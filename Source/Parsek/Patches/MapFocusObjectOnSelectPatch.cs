using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens.Mapview.MapContextMenuOptions;

namespace Parsek.Patches
{
    /// <summary>
    /// Arms a <see cref="StockActionIntentMarker"/> when the player clicks the
    /// "Switch To" item in the map-view context menu on an owned vessel. The marker
    /// authorizes immediate switch-segment start when the same-frame in-FLIGHT
    /// <c>OnVesselSwitchComplete</c> consumes it in Phase C.
    ///
    /// Decompiled MapContextMenuOptions.FocusObject.OnSelect()
    /// (KSP 1.12.5 Assembly-CSharp.dll, load-bearing excerpts):
    /// <code>
    /// protected override void OnSelect() {
    ///   switch (GetMode()) {
    ///     case FocusMode.OwnedVessel:
    ///       if (HighLogic.CurrentGame.Parameters.Flight.CanSwitchVesselsFar) {
    ///         FlightGlobals.SetActiveVessel(vessel);
    ///         MapView.ExitMapView();
    ///       }
    ///       break;
    ///     case FocusMode.UnownedVessel:
    ///       SpaceTracking.GoToAndFocusVessel(vessel);  // out-of-scope: routes to TS
    ///       break;
    ///     case FocusMode.CelestialBody:
    ///       PlanetariumCamera.fetch.SetTarget(...);     // out-of-scope: camera only
    ///       break;
    ///   }
    /// }
    /// </code>
    ///
    /// FlightGlobals.SetActiveVessel(Vessel) fires <c>onVesselSwitching</c> →
    /// <c>v.MakeActive()</c> → <c>onVesselChange</c> synchronously inside the method
    /// body before returning. Parsek's <c>OnVesselSwitchComplete</c> listener
    /// therefore runs *inside* SetActiveVessel — BEFORE this Postfix would run. Arming
    /// in the Postfix is too late; the consume site has already missed it. The
    /// correct shape is Prefix-arms / Postfix-cleans-up-on-refusal, with the Prefix's
    /// IntentId passed through <c>__state</c> so the Postfix only clears a marker it
    /// armed (not a subsequent click's marker).
    ///
    /// Refused early-return paths (decompile of FlightGlobals.setActiveVessel — all
    /// return <c>false</c> without firing onVesselSwitching / onVesselChange): vessel
    /// is null, vessel is already active, <c>ClearToSave()</c> fails for any of six
    /// reasons (not in atmosphere, under acceleration, moving over surface, about to
    /// crash, on a ladder, throttled up), or target's DiscoveryInfo.Level != Owned.
    /// The Postfix clears the marker with reason <c>refused-no-switch</c> in all of
    /// these.
    ///
    /// Unloaded-vessel branch outcome: when the target is unloaded, setActiveVessel
    /// fires <c>onVesselSwitchingToUnloaded</c>, saves, and calls
    /// <c>FlightDriver.StartAndFocusVessel</c> — a scene transition into a fresh
    /// FLIGHT scene load. The Postfix sees no <c>onVesselChange</c> consume happened
    /// and clears the marker with <c>refused-no-switch</c>; the new FLIGHT scene has
    /// no in-scene marker to consume. The deliberate outcome is that Map Switch-To
    /// to an unloaded vessel does NOT immediate-start a segment; the first-
    /// modification watcher catches the first meaningful change in the new scene.
    /// </summary>
    [HarmonyPatch]
    internal static class MapFocusObjectOnSelectPatch
    {
        // FocusObject lives in NAMESPACE KSP.UI.Screens.Mapview.MapContextMenuOptions
        // (a namespace, not a class) and FocusMode is a nested enum inside it.
        // Resolved at type-load via the FocusObject type's nested types. If a future
        // KSP drop renames anything, TargetMethod() returns null and Harmony skips
        // the patch instead of throwing.
        private static readonly Type FocusObjectType = typeof(FocusObject);
        private static readonly Type FocusModeType =
            FocusObjectType.GetNestedType("FocusMode", BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly object FocusModeOwnedVessel =
            FocusModeType != null && Enum.IsDefined(FocusModeType, "OwnedVessel")
                ? Enum.Parse(FocusModeType, "OwnedVessel")
                : null;

        // LOW 12 (PR #876 review): once-protection for TargetMethod miss Warns.
        // Harmony's patch-resolution machinery may call TargetMethod multiple
        // times across patcher passes; without this flag, a real KSP-version
        // mismatch (FocusObject renamed, OnSelect override moved) would spam
        // identical Warns at every retry. The flag is per-AppDomain one-shot
        // and intentionally never resets — same lifetime as
        // ParsekProcess.ProcessSessionId.
        private static bool _missLogged;

        // Harmony invokes TargetMethod() to resolve the target. The explicit
        // MethodInfo lookup (non-public override) sidesteps any attribute-time
        // resolution surprise that could throw if the override is materialized on
        // an unexpected derived class.
        static MethodBase TargetMethod()
        {
            if (FocusObjectType == null)
            {
                if (!_missLogged)
                {
                    ParsekLog.Warn("SwitchIntentPatch",
                        "MapFocusObjectOnSelectPatch: FocusObject type not found; patch will not be applied");
                    _missLogged = true;
                }
                return null;
            }
            MethodInfo method = FocusObjectType.GetMethod(
                "OnSelect",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                if (!_missLogged)
                {
                    ParsekLog.Warn("SwitchIntentPatch",
                        $"MapFocusObjectOnSelectPatch: OnSelect method not found on {FocusObjectType.FullName}; patch will not be applied");
                    _missLogged = true;
                }
                return null;
            }
            return method;
        }

        static bool Prefix(object __instance, out Guid __state)
        {
            __state = default(Guid);

            if (__instance == null)
                return true;

            // GetMode() — defensive Traverse so we don't crash if the method is
            // renamed in a future KSP drop.
            object modeValue;
            try
            {
                modeValue = Traverse.Create(__instance).Method("GetMode").GetValue();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: GetMode() failed: {ex.GetType().Name}: {ex.Message}");
                return true;
            }

            if (FocusModeOwnedVessel == null || modeValue == null || !modeValue.Equals(FocusModeOwnedVessel))
            {
                // UnownedVessel routes to TRACKSTATION (handled by the TS Fly
                // patch); CelestialBody is camera-only. Neither is in scope.
                ParsekLog.Verbose("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: focusMode={(modeValue != null ? modeValue.ToString() : "<null>")} (not OwnedVessel)");
                return true;
            }

            // Defensive Traverse on the private 'vessel' field. If KSP renames
            // the field we log a Warn and bail without arming (instead of arming
            // with PID 0).
            Vessel vessel;
            try
            {
                vessel = Traverse.Create(__instance).Field("vessel").GetValue<Vessel>();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: vessel field Traverse failed: {ex.GetType().Name}: {ex.Message}");
                return true;
            }
            if (vessel == null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    "Map Switch-To intent not armed: FocusObject.vessel is null (Traverse may have failed)");
                return true;
            }

            // Stock will refuse the switch if CanSwitchVesselsFar is off (the
            // OwnedVessel branch is gated on it). Do not arm.
            bool canSwitchVesselsFar = true;
            try
            {
                if (HighLogic.CurrentGame != null
                    && HighLogic.CurrentGame.Parameters != null
                    && HighLogic.CurrentGame.Parameters.Flight != null)
                {
                    canSwitchVesselsFar = HighLogic.CurrentGame.Parameters.Flight.CanSwitchVesselsFar;
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: CanSwitchVesselsFar read failed: {ex.GetType().Name}: {ex.Message}");
                return true;
            }
            if (!canSwitchVesselsFar)
            {
                ParsekLog.Verbose("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: CanSwitchVesselsFar=false targetPid={vessel.persistentId}");
                return true;
            }

            var scenario = ParsekScenario.Instance;
            if (scenario == null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: ParsekScenario.Instance is null targetPid={vessel.persistentId}");
                return true;
            }

            // Rapid-switch interception (post-#876 playtest 2026-05-17):
            // when a SwitchSegmentSession is already armed and the new target
            // is a different vessel, open a pre-switch Merge/Discard dialog
            // BEFORE stock OnSelect runs. This prevents the silent supersede
            // path from orphaning the prior session's tree (Bug A/B) and
            // turning the post-transition deferred dialog into a wrong-tree
            // prompt (Bug C). The Postfix is skipped (Prefix returns false)
            // because the dialog button handlers will arm the new intent and
            // call SetActiveVessel themselves.
            //
            // Case B (no-session unloaded-target, 2026-05-17 follow-up):
            // when no SwitchSegmentSession is armed but an in-flight
            // recording exists AND the target is unloaded, the stock path
            // would silently scene-reload (FLIGHT→FLIGHT transitions skip
            // the SceneExit Esc-to-Space-Center dialog filter). Open the
            // same Merge/Discard dialog so the player makes a deliberate
            // call before the reload, matching the Esc-to-SC UX.
            //
            // Case C (no-session loaded separate-committed target, the
            // in-bubble committed-clone Bounded fix): when no session is
            // armed but an in-flight recording exists AND the loaded target
            // is a SEPARATE previously-committed vessel (a committed tree
            // whose id differs from the live active tree id), the in-bubble
            // consume would run with activeTree still set and skip the
            // committed-clone restore (which requires activeTree == null),
            // wrongly routing the target to standalone / BG-member instead
            // of continuing its committed history. Open the same dialog so
            // the live tree is committed/discarded first; the target then
            // restores through the existing committed-clone Path B.
            //
            // Same-target Switch-To (player double-clicks the active session's
            // own vessel) bypasses the dialog: the consume helper's existing
            // `duplicate-intent-same-target` branch handles it. Re-selecting
            // the vessel you are already flying (its own committed clone) is
            // filtered by targetIsSeparateCommittedVessel below (matched tree
            // id == active tree id -> not separate -> no dialog).
            //
            // Re-entry guard: if a merge/dialog popup is already open (this
            // dialog or any other), defer to the existing one so the player
            // resolves it first.
            var existingSession = scenario.ActiveSwitchSegmentSession;
            var flightForGate = ParsekFlight.Instance;
            bool hasActiveRecording = flightForGate != null && flightForGate.HasActiveTree;
            // Vessel.loaded == false signals the target is out-of-bubble.
            // FlightGlobals.setActiveVessel detects this and routes through
            // onVesselSwitchingToUnloaded + FlightDriver.StartAndFocusVessel,
            // which is a scene reload — the load-bearing condition for
            // Case B. A null vessel.loaded read is treated as "loaded" so
            // we don't spuriously trigger the dialog on the in-bubble path;
            // an in-bubble switch with no session is the existing
            // continuation-recording flow and stays unchanged.
            bool targetIsUnloaded = !vessel.loaded;

            // Case C (in-bubble committed-clone Bounded fix): the loaded
            // target is a SEPARATE previously-committed vessel when there is
            // an active recording, the target is in the physics bubble
            // (loaded), and it matches a committed tree whose id differs from
            // the live active tree id. The live active tree id guard makes
            // re-selecting the vessel you are already flying (its own
            // committed clone) NOT open a dialog. Any lookup failure (null
            // flight, Traverse/store hiccup) falls back to false so we keep
            // today's in-bubble behavior; logged Verbose.
            bool targetIsSeparateCommittedVessel = false;
            if (hasActiveRecording && !targetIsUnloaded)
            {
                try
                {
                    // Guid "N" form is culture-invariant by definition (hex
                    // digits only); matches the ParsekFlight consume call site.
                    string liveGuid = vessel.id != Guid.Empty
                        ? vessel.id.ToString("N")
                        : null;
                    RecordingTree matchedTree;
                    bool matched = ParsekFlight.TryFindCommittedTreeMatchingVessel(
                        vessel.persistentId, liveGuid, out matchedTree);
                    string activeTreeId = ParsekFlight.Instance?.ActiveTreeForDisplay?.Id;
                    if (matched
                        && matchedTree != null
                        && !string.Equals(matchedTree.Id, activeTreeId, StringComparison.Ordinal))
                    {
                        targetIsSeparateCommittedVessel = true;
                        ParsekLog.Verbose("SwitchIntentPatch",
                            $"pre-switch Case C: loaded target is separate committed vessel " +
                            $"targetPid={vessel.persistentId} matchedTreeId={matchedTree.Id ?? "<null>"} " +
                            $"activeTreeId={activeTreeId ?? "<null>"}");
                    }
                }
                catch (Exception ex)
                {
                    targetIsSeparateCommittedVessel = false;
                    ParsekLog.Verbose("SwitchIntentPatch",
                        $"pre-switch Case C lookup failed: {ex.GetType().Name}: {ex.Message} " +
                        $"targetPid={vessel.persistentId} - treating as not-separate (today's behavior)");
                }
            }

            var decision = DecidePreSwitchDialogAction(
                hasActiveSession: existingSession != null,
                priorFocusedPid: existingSession?.FocusedVesselPersistentId ?? 0u,
                newTargetPid: vessel.persistentId,
                anotherDialogOpen: ParsekScenario.MergeDialogPending,
                hasActiveRecording: hasActiveRecording,
                targetIsUnloaded: targetIsUnloaded,
                targetIsSeparateCommittedVessel: targetIsSeparateCommittedVessel);
            switch (decision)
            {
                case PreSwitchDialogDecision.OpenDialog:
                    {
                        // Distinguish the three OpenDialog routes in KSP.log so
                        // a reader can tell Case A (session) from the two
                        // no-session sub-cases: Case B (unloaded target, scene
                        // reload) vs Case C (loaded separate-committed target,
                        // in-bubble committed-clone). The no-session handlers
                        // are the same for B and C; only the trigger differs.
                        string openCase = existingSession != null
                            ? "A-session"
                            : (targetIsSeparateCommittedVessel ? "C-loaded-separate-committed"
                                                               : "B-unloaded");
                        ParsekLog.Info("SwitchIntentPatch",
                            $"pre-switch-dialog opening case={openCase} " +
                            $"targetPid={vessel.persistentId} " +
                            $"hasActiveRecording={hasActiveRecording} " +
                            $"targetIsUnloaded={targetIsUnloaded} " +
                            $"targetIsSeparateCommittedVessel={targetIsSeparateCommittedVessel} " +
                            $"priorSessionId={existingSession?.SessionId.ToString("D", CultureInfo.InvariantCulture) ?? "<no-session>"}");

                        // Branch on whether a session is armed. Case A keeps
                        // the existing session-aware handlers; Case B / Case C
                        // route through the new no-session handlers that commit
                        // / discard the live active tree directly.
                        bool dialogOpened = existingSession != null
                            ? TryOpenPreSwitchDecisionDialog(vessel, existingSession)
                            : TryOpenPreSwitchDecisionDialogNoSession(vessel);
                        if (dialogOpened)
                        {
                            // Skip stock OnSelect; dialog button handlers will arm
                            // the new intent and invoke FlightGlobals.SetActiveVessel.
                            return false;
                        }
                        // Dialog spawn failed — fall through to the original
                        // arm-and-skip flow. For Case A the existing
                        // `superseded-by-new-switch` defensive path in the
                        // consume helper still logs the orphan as a documented
                        // degradation. For Case B the prior recording silently
                        // survives the scene reload as it did before this
                        // change; for Case C the in-bubble consume runs with
                        // activeTree still set and routes the target to
                        // standalone — both are the pre-extension behavior, no
                        // regression. Already Warn-logged by
                        // ShowPreSwitchDecisionDialog.
                        break;
                    }
                case PreSwitchDialogDecision.SkipDialogSameTarget:
                    ParsekLog.Verbose("SwitchIntentPatch",
                        $"pre-switch-dialog skipped: same-target Switch-To " +
                        $"priorSessionId={existingSession.SessionId:D} " +
                        $"targetPid={vessel.persistentId} - duplicate-intent-same-target consume path will handle");
                    break;
                case PreSwitchDialogDecision.SkipDialogReEntry:
                    ParsekLog.Verbose("SwitchIntentPatch",
                        $"pre-switch-dialog skipped: another merge dialog is open " +
                        $"priorSessionId={existingSession?.SessionId.ToString("D", CultureInfo.InvariantCulture) ?? "<no-session>"} " +
                        $"hasActiveRecording={hasActiveRecording} targetIsUnloaded={targetIsUnloaded} " +
                        $"targetIsSeparateCommittedVessel={targetIsSeparateCommittedVessel} " +
                        $"targetPid={vessel.persistentId} - re-entry guard");
                    break;
                case PreSwitchDialogDecision.NoPriorSession:
                default:
                    break;
            }

            Guid intentId = Guid.NewGuid();
            var marker = new StockActionIntentMarker
            {
                IntentId = intentId,
                Action = StockActionType.MapSwitchTo,
                TargetVesselPersistentId = vessel.persistentId,
                SourceScene = StockActionSourceScene.Flight,
                CapturedRealtime = UnityEngine.Time.realtimeSinceStartup,
                CapturedUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0,
                ProcessSessionId = ParsekProcess.ProcessSessionId,
            };

            // Prefix-on-Prefix race log: ParsekScenario.ArmStockActionIntent
            // already emits a `stale-intent-superseded` Info log when it overwrites
            // a still-armed marker, but we also tag the SwitchIntentPatch subsystem
            // so a grep of `[SwitchIntentPatch]` catches it.
            var existing = scenario.CurrentStockActionIntent;
            if (existing != null && existing.IntentId != intentId)
            {
                ParsekLog.Info("SwitchIntentPatch",
                    $"Map Switch-To intent stale-intent-superseded: prior intentId={existing.IntentId:D} " +
                    $"action={existing.Action} new intentId={intentId:D}");
            }

            scenario.ArmStockActionIntent(marker);
            __state = intentId;
            ParsekLog.Info("SwitchIntentPatch",
                $"Map Switch-To intent armed: intentId={marker.IntentId:D} action={marker.Action} " +
                $"targetPid={marker.TargetVesselPersistentId} sourceScene={marker.SourceScene} " +
                $"capturedUT={marker.CapturedUT.ToString("R", CultureInfo.InvariantCulture)}");
            return true;
        }

        /// <summary>
        /// Opens the pre-switch Merge / Discard decision dialog for the
        /// given <paramref name="target"/> vessel. The Merge handler commits
        /// the prior active tree in-flight and then arms a fresh intent +
        /// invokes <see cref="FlightGlobals.SetActiveVessel"/>; the Discard
        /// handler scoped-discards the prior session and does the same
        /// arm-then-switch. Returns true when the dialog was spawned (caller
        /// returns false from the Prefix); false on spawn failure (caller
        /// falls back to the original arm-and-skip flow).
        ///
        /// <para>Defensive note: this method does NOT arm an intent before
        /// the dialog opens. The button handlers own arming + SetActiveVessel
        /// so a Discard cleanly disposes of the prior session before the new
        /// switch routes through <c>OnVesselSwitchComplete</c>. If the
        /// player dismisses the dialog without clicking either button (e.g.
        /// Esc), no intent is left armed.</para>
        /// </summary>
        private static bool TryOpenPreSwitchDecisionDialog(
            Vessel target,
            SwitchSegmentSession priorSession)
        {
            return MergeDialog.ShowPreSwitchDecisionDialog(
                target,
                mergeAction: () => MergePriorAndSwitchTo(target, priorSession),
                discardAction: () => DiscardPriorAndSwitchTo(target, priorSession));
        }

        /// <summary>
        /// No-session Case B / Case C: open the pre-switch Merge / Discard
        /// dialog when an in-flight recording exists but no
        /// <see cref="SwitchSegmentSession"/> is armed and either the new
        /// target vessel is unloaded (Case B, out-of-bubble) or it is a
        /// loaded separate previously-committed vessel (Case C, in-bubble
        /// committed-clone). For Case B the Map Switch-To stock path would
        /// otherwise scene-reload (FLIGHT→FLIGHT) and skip the SceneExit
        /// Esc-to-Space-Center dialog filter; for Case C the in-bubble
        /// consume would skip the committed-clone restore (which requires
        /// <c>activeTree == null</c>) and wrongly route the target to a
        /// standalone / BG-member segment. Either way the player would
        /// never get a deliberate Merge/Discard prompt for the prior
        /// recording today.
        ///
        /// <para>The handlers commit-in-flight (Merge) or
        /// active-tree-discard (Discard) the live tree, then arm a fresh
        /// intent + call <see cref="FlightGlobals.SetActiveVessel"/>
        /// like the session-armed path. There is no scoped-discard
        /// subtree helper to call here because no segment marker exists.
        /// </para>
        ///
        /// <para>Returns true when the dialog was spawned; false when
        /// the live active tree is missing (degenerate state — the
        /// outer Prefix already gated on
        /// <c>HasActiveTree</c>) or the dialog spawn fails.</para>
        /// </summary>
        private static bool TryOpenPreSwitchDecisionDialogNoSession(Vessel target)
        {
            var flight = ParsekFlight.Instance;
            var activeTree = flight?.ActiveTreeForDisplay;
            if (activeTree == null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    "pre-switch-dialog-no-session: no active tree at dialog spawn time " +
                    "(race with recorder teardown) - falling through to stock OnSelect");
                return false;
            }
            return MergeDialog.ShowPreSwitchDecisionDialog(
                target,
                mergeAction: () => MergeActiveTreeAndSwitchTo(target),
                discardAction: () => DiscardActiveRecordingAndSwitchTo(target),
                priorTreeOverride: activeTree);
        }

        /// <summary>
        /// Merge handler: commits the prior session's active tree using
        /// <see cref="ParsekFlight.CommitTreeFlight"/> (in-flight commit
        /// path that finalizes recordings, spawns committed leaves where
        /// needed, and tears down the live recorder / activeTree). After
        /// <c>CommitTreeFlight</c> succeeds and <c>OnTreeCommitted</c>
        /// fires, this handler explicitly clears the prior
        /// <see cref="ParsekScenario.ActiveSwitchSegmentSession"/> marker
        /// before arming the new intent. The defensive
        /// <c>superseded-by-new-switch</c> branch in
        /// <see cref="ParsekFlight.TryConsumeStockActionIntent"/> is now
        /// a backstop only — the marker should already be cleared by the
        /// time consume fires for the new target. Without the explicit
        /// clear here the marker survived <c>OnSave</c> / <c>OnLoad</c>
        /// for any save-on-switch routine (Bug B, post-#876 playtest
        /// 2026-05-17) and the next switch only collected it through the
        /// defensive fallback, which logged as an orphan.
        ///
        /// <para>Then arms a fresh Map Switch-To intent and calls
        /// <c>FlightGlobals.SetActiveVessel(target)</c> so the consume
        /// helper picks up the marker on the inline
        /// <c>onVesselChange</c> firing.</para>
        /// </summary>
        private static void MergePriorAndSwitchTo(
            Vessel target, SwitchSegmentSession priorSession)
        {
            var scenario = ParsekScenario.Instance;
            string priorSessionIdStr = priorSession != null
                ? priorSession.SessionId.ToString("D", CultureInfo.InvariantCulture)
                : "<none>";

            // L8 (PR #876 round-5 review): refuse if a Re-Fly merge journal
            // is concurrently active. CommitTreeFlight could race the
            // journal finisher (mirrors MergeDialog.MergeDiscardRanToCompletion
            // and TryDiscardActiveReFlyAttempt's existing guards). Player
            // retries after the journal completes.
            if (scenario != null && scenario.ActiveMergeJournal != null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"merge-refused-active-merge-journal " +
                    $"priorSessionId={priorSessionIdStr} " +
                    $"journal={scenario.ActiveMergeJournal.JournalId ?? "<no-id>"}");
                ParsekLog.ScreenMessage(
                    "Switch-to merge: re-fly merge in progress - retry in a moment", 3f);
                return;
            }

            // Step 1: in-flight commit of the prior tree. CommitTreeFlight
            // commits the tree to storage but does not itself touch the
            // session marker (unlike MergeCommit, which fires
            // OnTreeCommitted and clears via scoped-merge-success). Bug B
            // fix (post-#876 playtest 2026-05-17): clear the prior session
            // marker synchronously below, AFTER OnTreeCommitted, so a
            // save/load round-trip cannot resurrect it and the next switch
            // does not have to lean on the `superseded-by-new-switch`
            // defensive branch in TryConsumeStockActionIntent (which is
            // now a backstop only).
            //
            // Defensive: if the prior tree is gone, log, clear the marker,
            // and continue with the switch anyway.
            var flight = ParsekFlight.Instance;
            if (flight != null && flight.HasActiveTree)
            {
                // Capture the active tree reference BEFORE CommitTreeFlight so
                // we can pass it to OnTreeCommitted (signature changed to
                // Action<RecordingTree> on the logistics branch — the route-
                // creation post-commit hook needs the committed tree's id).
                RecordingTree committedTreeRef = flight.ActiveTreeForSerialization;
                try
                {
                    flight.CommitTreeFlight();
                    ParsekLog.Info("SwitchIntentPatch",
                        $"pre-switch-dialog committed-prior-segment " +
                        $"priorSessionId={priorSessionIdStr}");
                }
                catch (Exception ex)
                {
                    // L2 (PR #876 round-5 review): on CommitTreeFlight
                    // exception, activeTree may be left half-finalized.
                    // Aborting the switch is safer than letting
                    // ArmIntentAndSwitchTo proceed and consume against a
                    // partial tree. The player sees a screen message,
                    // stays on the prior vessel, and can retry; normal
                    // recorder lifecycle or the next merge dialog will
                    // sweep the half-finalized state.
                    ParsekLog.Error("SwitchIntentPatch",
                        $"pre-switch-dialog merge: CommitTreeFlight threw " +
                        $"{ex.GetType().Name}: {ex.Message} - aborting switch");
                    ParsekLog.Warn("SwitchIntentPatch",
                        $"pre-switch-dialog merge abort: priorSessionId={priorSessionIdStr} " +
                        $"newTargetPid={(target != null ? target.persistentId : 0u)} - " +
                        "player remains on prior vessel; retry the Switch-To click");
                    ParsekLog.ScreenMessage(
                        "Switch-to canceled: failed to commit prior recording", 4f);
                    return;
                }

                // L3 (PR #876 round-5 review): MergeDialog.OnTreeCommitted is
                // only fired by MergeCommit (not CommitTreeFlight), so the
                // ParsekFlight subscriber that runs EvaluateAndApplyGhostChains
                // doesn't fire on this path. Invoke it directly so newly
                // committed recordings get picked up by ghost-chain
                // evaluation immediately instead of waiting for the next
                // scene-load.
                try
                {
                    MergeDialog.OnTreeCommitted?.Invoke(committedTreeRef);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("SwitchIntentPatch",
                        $"pre-switch-dialog merge: OnTreeCommitted invoker threw " +
                        $"{ex.GetType().Name}: {ex.Message} - continuing with switch");
                }

                // Bug B fix (post-#876 playtest 2026-05-17): explicitly
                // clear the prior session marker now that CommitTreeFlight
                // and OnTreeCommitted have completed. Without this clear,
                // the marker survived OnSave / OnLoad and the next switch
                // only collected it through the defensive
                // `superseded-by-new-switch` branch in
                // TryConsumeStockActionIntent — logging an orphan every
                // time. Must run BEFORE ArmIntentAndSwitchTo so the
                // synchronous onVesselChange consume for the new target
                // sees a clean slate.
                if (scenario != null && scenario.ActiveSwitchSegmentSession != null)
                {
                    var clearedSessionId = scenario.ActiveSwitchSegmentSession.SessionId;
                    scenario.ClearSwitchSegmentSession("merge-committed");
                    ParsekLog.Info("SwitchIntentPatch",
                        $"pre-switch-dialog-session-cleared " +
                        $"sessionId={clearedSessionId:D} reason=merge-committed");
                }
            }
            else
            {
                ParsekLog.Info("SwitchIntentPatch",
                    $"pre-switch-dialog merge: no active tree to commit " +
                    $"priorSessionId={priorSessionIdStr} - clearing session and switching");
                // Defensive clear in case the active tree disappeared but the
                // session marker is still armed (degenerate state).
                if (scenario != null && scenario.ActiveSwitchSegmentSession != null)
                    scenario.ClearSwitchSegmentSession("pre-switch-dialog-merge-no-active-tree");
            }

            ArmIntentAndSwitchTo(target);
        }

        /// <summary>
        /// Discard handler: scoped-discards the prior session via
        /// <see cref="RecordingStore.TryDiscardActiveSwitchSegmentAttempt"/>,
        /// then arms a fresh Map Switch-To intent and calls
        /// <c>FlightGlobals.SetActiveVessel(target)</c>.
        /// </summary>
        private static void DiscardPriorAndSwitchTo(
            Vessel target, SwitchSegmentSession priorSession)
        {
            string priorSessionIdStr = priorSession != null
                ? priorSession.SessionId.ToString("D", CultureInfo.InvariantCulture)
                : "<none>";

            try
            {
                string reason;
                var disposition = RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out reason);
                ParsekLog.Info("SwitchIntentPatch",
                    $"pre-switch-dialog scoped-discard-success " +
                    $"priorSessionId={priorSessionIdStr} " +
                    $"disposition={disposition} reason={reason ?? "<none>"}");
            }
            catch (Exception ex)
            {
                ParsekLog.Error("SwitchIntentPatch",
                    $"pre-switch-dialog discard: TryDiscardActiveSwitchSegmentAttempt threw " +
                    $"{ex.GetType().Name}: {ex.Message} - continuing with switch anyway");
            }

            ArmIntentAndSwitchTo(target);
        }

        /// <summary>
        /// No-session Case B Merge handler: commits the live active tree
        /// via <see cref="ParsekFlight.CommitTreeFlight"/> (which is
        /// session-agnostic — it operates on <c>activeTree</c>
        /// directly), invokes <see cref="MergeDialog.OnTreeCommitted"/>
        /// so ghost-chain evaluation picks up the newly committed
        /// recordings, then arms a fresh intent and calls
        /// <see cref="FlightGlobals.SetActiveVessel"/>.
        ///
        /// <para>No <c>ClearSwitchSegmentSession</c> call (no session
        /// armed). Mirrors the active-merge-journal guard from the
        /// session-armed Merge handler so a Re-Fly merge journal
        /// finisher cannot race the commit.</para>
        /// </summary>
        private static void MergeActiveTreeAndSwitchTo(Vessel target)
        {
            var scenario = ParsekScenario.Instance;
            var flight = ParsekFlight.Instance;
            string priorTreeIdStr = flight?.ActiveTreeForDisplay?.Id ?? "<no-tree>";

            // Mirror the session-armed guard (L8 PR #876 round-5
            // review): refuse if a Re-Fly merge journal is concurrently
            // active. CommitTreeFlight could race the journal finisher.
            if (scenario != null && scenario.ActiveMergeJournal != null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"merge-refused-active-merge-journal-no-session " +
                    $"priorTreeId={priorTreeIdStr} " +
                    $"journal={scenario.ActiveMergeJournal.JournalId ?? "<no-id>"}");
                ParsekLog.ScreenMessage(
                    "Switch-to merge: re-fly merge in progress - retry in a moment", 3f);
                return;
            }

            if (flight != null && flight.HasActiveTree)
            {
                // Capture the active tree reference BEFORE CommitTreeFlight so
                // we can pass it to OnTreeCommitted (signature changed to
                // Action<RecordingTree> on the logistics branch).
                RecordingTree committedTreeRef = flight.ActiveTreeForSerialization;
                try
                {
                    flight.CommitTreeFlight();
                    ParsekLog.Info("SwitchIntentPatch",
                        $"pre-switch-dialog-merge-chosen-no-session " +
                        $"priorTreeId={priorTreeIdStr} " +
                        $"newTargetPid={(target != null ? target.persistentId : 0u)}");
                }
                catch (Exception ex)
                {
                    // Defensive on CommitTreeFlight exception: abort the
                    // switch rather than letting ArmIntentAndSwitchTo
                    // proceed against a half-finalized tree (mirrors L2
                    // in MergePriorAndSwitchTo).
                    ParsekLog.Error("SwitchIntentPatch",
                        $"pre-switch-dialog merge (no-session): CommitTreeFlight threw " +
                        $"{ex.GetType().Name}: {ex.Message} - aborting switch");
                    ParsekLog.Warn("SwitchIntentPatch",
                        $"pre-switch-dialog merge abort (no-session): priorTreeId={priorTreeIdStr} " +
                        $"newTargetPid={(target != null ? target.persistentId : 0u)} - " +
                        "player remains on prior vessel; retry the Switch-To click");
                    ParsekLog.ScreenMessage(
                        "Switch-to canceled: failed to commit prior recording", 4f);
                    return;
                }

                // L3 PR #876 round-5 review: CommitTreeFlight does not
                // fire MergeDialog.OnTreeCommitted, so the
                // EvaluateAndApplyGhostChains subscriber on
                // ParsekFlight wouldn't run on this path. Invoke
                // directly so newly committed recordings get picked
                // up immediately.
                try
                {
                    MergeDialog.OnTreeCommitted?.Invoke(committedTreeRef);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("SwitchIntentPatch",
                        $"pre-switch-dialog merge (no-session): OnTreeCommitted invoker threw " +
                        $"{ex.GetType().Name}: {ex.Message} - continuing with switch");
                }
            }
            else
            {
                ParsekLog.Info("SwitchIntentPatch",
                    $"pre-switch-dialog merge (no-session): no active tree to commit " +
                    $"priorTreeId={priorTreeIdStr} - proceeding with switch");
            }

            ArmIntentAndSwitchTo(target);
        }

        /// <summary>
        /// No-session Case B Discard handler: drops the live active
        /// tree without committing it via
        /// <see cref="ParsekFlight.AutoDiscardActiveTreeWithMessage"/>
        /// (the reason-aware overload of
        /// <see cref="ParsekFlight.AutoDiscardIdleActiveTree"/> — same
        /// teardown body: stops continuations and gloops, ForceStops
        /// the recorder, discards the background recorder, nulls
        /// <c>activeTree</c>, and rolls back any future-time ledger
        /// entries via
        /// <c>LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions</c>).
        /// Then arms a fresh intent and calls
        /// <see cref="FlightGlobals.SetActiveVessel"/>.
        ///
        /// <para>Round-6 review: passes a context-aware screen message
        /// ("Recording discarded - switching vessels") and ledger
        /// reason ("pre-switch-dialog-discard-no-session"). The default
        /// "idle on pad" toast is wrong-context here because this handler
        /// now covers both Case B (target out-of-bubble / unloaded, scene
        /// reload) and Case C (loaded separate-committed target,
        /// in-bubble), with <c>HasActiveTree</c> required in either, i.e.
        /// the player has been actively flying; the neutral "switching
        /// vessels" wording fits both. The default
        /// "suppressed-scene-exit-discard" ledger reason is
        /// similarly wrong-context.</para>
        ///
        /// <para>Mirrors the active-merge-journal guard so the journal
        /// finisher cannot race the discard.</para>
        /// </summary>
        private static void DiscardActiveRecordingAndSwitchTo(Vessel target)
        {
            var scenario = ParsekScenario.Instance;
            var flight = ParsekFlight.Instance;
            string priorTreeIdStr = flight?.ActiveTreeForDisplay?.Id ?? "<no-tree>";

            // Mirror the session-armed merge guard for symmetry: refuse
            // if a Re-Fly merge journal is concurrently active.
            // AutoDiscardActiveTreeWithMessage calls
            // LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions,
            // which would race the journal's rollback.
            if (scenario != null && scenario.ActiveMergeJournal != null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"discard-refused-active-merge-journal-no-session " +
                    $"priorTreeId={priorTreeIdStr} " +
                    $"journal={scenario.ActiveMergeJournal.JournalId ?? "<no-id>"}");
                ParsekLog.ScreenMessage(
                    "Switch-to discard: re-fly merge in progress - retry in a moment", 3f);
                return;
            }

            if (flight != null && flight.HasActiveTree)
            {
                try
                {
                    flight.AutoDiscardActiveTreeWithMessage(
                        reason: "pre-switch-dialog discard (no-session)",
                        screenMessage: "Recording discarded - switching vessels",
                        ledgerRecalcReason: "pre-switch-dialog-discard-no-session");
                    ParsekLog.Info("SwitchIntentPatch",
                        $"pre-switch-dialog-discard-chosen-no-session " +
                        $"priorTreeId={priorTreeIdStr} " +
                        $"newTargetPid={(target != null ? target.persistentId : 0u)}");
                }
                catch (Exception ex)
                {
                    // Defensive on discard exception: log + proceed with
                    // the switch. The live tree may be in an
                    // intermediate state, but the next scene-load /
                    // OnSave will sweep it.
                    ParsekLog.Error("SwitchIntentPatch",
                        $"pre-switch-dialog discard (no-session): AutoDiscardActiveTreeWithMessage threw " +
                        $"{ex.GetType().Name}: {ex.Message} - continuing with switch anyway");
                }
            }
            else
            {
                ParsekLog.Info("SwitchIntentPatch",
                    $"pre-switch-dialog discard (no-session): no active tree to discard " +
                    $"priorTreeId={priorTreeIdStr} - proceeding with switch");
            }

            ArmIntentAndSwitchTo(target);
        }

        /// <summary>
        /// Shared arm-then-switch finisher for the dialog button handlers.
        /// Arms a fresh Map Switch-To intent and invokes
        /// <c>FlightGlobals.SetActiveVessel(target)</c>. The consume helper
        /// will pick up the marker on the synchronous <c>onVesselChange</c>
        /// firing inside <c>SetActiveVessel</c>.
        /// </summary>
        private static void ArmIntentAndSwitchTo(Vessel target)
        {
            if (target == null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    "pre-switch-dialog: target vessel is null - cannot arm intent or switch");
                return;
            }
            var scenario = ParsekScenario.Instance;
            if (scenario == null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"pre-switch-dialog: scenario is null - cannot arm intent (targetPid={target.persistentId})");
                return;
            }

            Guid intentId = Guid.NewGuid();
            var marker = new StockActionIntentMarker
            {
                IntentId = intentId,
                Action = StockActionType.MapSwitchTo,
                TargetVesselPersistentId = target.persistentId,
                SourceScene = StockActionSourceScene.Flight,
                CapturedRealtime = UnityEngine.Time.realtimeSinceStartup,
                CapturedUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0,
                ProcessSessionId = ParsekProcess.ProcessSessionId,
            };
            scenario.ArmStockActionIntent(marker);
            ParsekLog.Info("SwitchIntentPatch",
                $"pre-switch-dialog re-armed intent: intentId={marker.IntentId:D} action={marker.Action} " +
                $"targetPid={marker.TargetVesselPersistentId} " +
                $"capturedUT={marker.CapturedUT.ToString("R", CultureInfo.InvariantCulture)}");

            try
            {
                FlightGlobals.SetActiveVessel(target);
                if (MapView.MapIsEnabled)
                    MapView.ExitMapView();
            }
            catch (Exception ex)
            {
                ParsekLog.Error("SwitchIntentPatch",
                    $"pre-switch-dialog: FlightGlobals.SetActiveVessel threw " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        static void Postfix(Guid __state)
        {
            // Contract: this Postfix uses "marker still armed under my IntentId"
            // as a proxy for "Phase C's TryConsumeStockActionIntent didn't fire
            // for this click, so SetActiveVessel took an early-return path."
            // For that proxy to be reliable, the contract is:
            //
            //   Every refusal branch inside ParsekFlight.TryConsumeStockActionIntent
            //   MUST call scenario.ClearStockActionIntent before returning,
            //   regardless of route (NoIntent excepted - no marker was armed).
            //
            // Verified for all current refusal paths (ParsekFlight.cs around
            // lines 7843, 7881-7882 - the `consume-null-vessel` early-return
            // and the FormatRefusalDiagnostic + ClearStockActionIntent pair).
            // If a future refusal path is added without clearing, this Postfix
            // would mis-attribute it as "no consume fired" and clear with
            // refused-no-switch - masking the real refusal reason in logs.
            // Search for "scenario.ClearStockActionIntent" inside that method
            // before adding any new refusal branch.

            // Prefix didn't arm (gate failed) — nothing to clean up.
            if (__state == Guid.Empty)
                return;

            var scenario = ParsekScenario.Instance;
            if (scenario == null)
                return;

            var current = scenario.CurrentStockActionIntent;
            if (current == null)
            {
                // Either Phase C's OnVesselSwitchComplete consumed it (success
                // path) or a refusal cleared it (per Contract above). Either
                // way, nothing to do.
                return;
            }
            if (current.IntentId != __state)
            {
                // Subsequent click's marker (stale-intent-superseded path); leave
                // it armed for the new Prefix's lifecycle.
                ParsekLog.Verbose("SwitchIntentPatch",
                    $"Map Switch-To Postfix: marker IntentId mismatch (mine={__state:D} current={current.IntentId:D}) — leaving armed");
                return;
            }

            // Marker is still armed under our IntentId — consume site (Phase C
            // OnVesselSwitchComplete) didn't fire, meaning SetActiveVessel took an
            // early-return path (vessel null, already active, ClearToSave failed,
            // DiscoveryLevel != Owned) or the unloaded-vessel scene-transition
            // branch. Clear with refused-no-switch. (Per Contract above, a
            // refusal inside TryConsumeStockActionIntent would already have
            // cleared the marker and we would have taken the `current == null`
            // branch earlier.)
            scenario.ClearStockActionIntent("refused-no-switch");
            ParsekLog.Info("SwitchIntentPatch",
                $"Map Switch-To Postfix: cleared own marker intentId={__state:D} reason=refused-no-switch");
        }

        /// <summary>
        /// Pure gate predicate exposed for unit tests. Mirrors the three Prefix
        /// gates: FocusMode == OwnedVessel, CanSwitchVesselsFar, and vessel
        /// non-null. Returns true only when all three gates pass.
        /// </summary>
        internal static bool ShouldArmMapSwitchTo(
            bool isOwnedVesselMode,
            bool canSwitchVesselsFar,
            bool vesselNotNull)
        {
            if (!vesselNotNull) return false;
            if (!isOwnedVesselMode) return false;
            if (!canSwitchVesselsFar) return false;
            return true;
        }

        /// <summary>
        /// Pre-switch decision branch selector exposed for unit tests. The
        /// Prefix opens the pre-switch Merge / Discard dialog in two
        /// independent cases (predicate priority: session always beats
        /// no-session):
        ///
        /// <para><b>Case A — switch-segment session armed.</b> A prior
        /// session exists with a different focused PID; the dialog opens
        /// and stock OnSelect is skipped. Same-target re-click defers to
        /// the consume helper's <c>duplicate-intent-same-target</c> path
        /// without opening a dialog.</para>
        ///
        /// <para><b>Case B — no session, active in-flight recording,
        /// target out-of-bubble (unloaded), OR a loaded separate
        /// previously-committed vessel.</b> Two sub-cases share the same
        /// no-session Merge/Discard handlers:
        /// <list type="bullet">
        /// <item><b>Unloaded target</b> — Map Switch-To to an unloaded
        ///     target triggers stock's <c>FlightDriver.StartAndFocusVessel</c>
        ///     scene reload. The existing SceneExit dialog filter explicitly
        ///     skips FLIGHT→FLIGHT transitions (see
        ///     <see cref="SceneExitInterceptor"/>), so the prior recording
        ///     silently survives the reload without a player decision today.</item>
        /// <item><b>Loaded separate-committed target</b>
        ///     (<paramref name="targetIsSeparateCommittedVessel"/>) — the
        ///     target is in the physics bubble but is a SEPARATE vessel with
        ///     its own committed history (a different tree than the live
        ///     active one). Without the dialog the in-bubble consume runs with
        ///     the live <c>activeTree</c> still set, so the committed-clone
        ///     restore (which requires <c>activeTree == null</c>) is skipped
        ///     and the target wrongly routes to a standalone / BG-member
        ///     segment instead of continuing its committed history. Committing
        ///     or discarding the live tree first clears <c>activeTree</c> so
        ///     the existing committed-clone restore path fires.</item>
        /// </list>
        /// Both surface the same Merge/Discard dialog the Esc-to-Space-Center
        /// flow uses, before the switch runs.</para>
        ///
        /// <para>Returns:
        /// <list type="bullet">
        /// <item><c>OpenDialog</c>: dialog should be opened, stock OnSelect
        ///     skipped (either Case A or Case B).</item>
        /// <item><c>SkipDialogSameTarget</c>: Case A only — the prior
        ///     session targets the same vessel; consume helper's
        ///     <c>duplicate-intent-same-target</c> branch handles.</item>
        /// <item><c>SkipDialogReEntry</c>: another merge/dialog popup is
        ///     already open; defer to that one regardless of case.</item>
        /// <item><c>NoPriorSession</c>: neither case applies; the regular
        ///     arm-and-skip flow runs unchanged.</item>
        /// </list>
        /// </para>
        /// </summary>
        internal enum PreSwitchDialogDecision
        {
            NoPriorSession = 0,
            OpenDialog = 1,
            SkipDialogSameTarget = 2,
            SkipDialogReEntry = 3,
        }

        /// <summary>
        /// Pure helper for unit-testing the pre-switch dialog gate. See
        /// <see cref="PreSwitchDialogDecision"/> for the full truth table.
        /// Case A (active session) ALWAYS takes priority over Case B
        /// (no session, active recording, unloaded OR loaded-separate-committed
        /// target): a live session is a stronger signal than a passive
        /// recording, and the existing session-aware Merge path keeps its
        /// session-specific bookkeeping (clear marker, scoped discard).
        /// </summary>
        /// <param name="targetIsSeparateCommittedVessel">
        /// True when the switch target is a LOADED (in-bubble) vessel that is a
        /// SEPARATE previously-committed vessel — i.e. it matches a committed
        /// tree whose id differs from the live active tree id. Computed by the
        /// Prefix via <see cref="ParsekFlight.TryFindCommittedTreeMatchingVessel(uint,string,out RecordingTree)"/>.
        /// When true (and no session is armed), the no-session Merge/Discard
        /// dialog opens so the live tree is committed/discarded first and the
        /// target's committed-clone restore can fire (it requires
        /// <c>activeTree == null</c>). False for the active vessel's own
        /// committed clone (same tree), non-committed loaded targets, and on
        /// any lookup failure (falls back to today's in-bubble behavior).
        /// </param>
        internal static PreSwitchDialogDecision DecidePreSwitchDialogAction(
            bool hasActiveSession,
            uint priorFocusedPid,
            uint newTargetPid,
            bool anotherDialogOpen,
            bool hasActiveRecording,
            bool targetIsUnloaded,
            bool targetIsSeparateCommittedVessel)
        {
            if (hasActiveSession)
            {
                if (priorFocusedPid == newTargetPid)
                    return PreSwitchDialogDecision.SkipDialogSameTarget;
                if (anotherDialogOpen)
                    return PreSwitchDialogDecision.SkipDialogReEntry;
                return PreSwitchDialogDecision.OpenDialog;
            }
            // Case B: no session armed, but a recording is in flight AND
            // either (1) the target is unloaded — stock would scene-reload
            // silently and skip the Esc-to-SC dialog filter; or (2) the
            // target is a loaded separate previously-committed vessel —
            // the in-bubble consume would otherwise skip the committed-clone
            // restore (which needs activeTree == null) and wrongly route the
            // target to standalone / BG-member. Either way, surface the
            // choice so the live tree is resolved before the switch.
            if (hasActiveRecording && (targetIsUnloaded || targetIsSeparateCommittedVessel))
            {
                if (anotherDialogOpen)
                    return PreSwitchDialogDecision.SkipDialogReEntry;
                return PreSwitchDialogDecision.OpenDialog;
            }
            return PreSwitchDialogDecision.NoPriorSession;
        }
    }
}
