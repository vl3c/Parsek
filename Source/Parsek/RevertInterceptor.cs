using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;

namespace Parsek
{
    /// <summary>
    /// Which stock revert entry point the player clicked. Both go through
    /// <see cref="RevertInterceptor"/>'s shared dispatcher; the callback
    /// handlers branch on this value so Discard Re-fly drives the correct
    /// scene transition (Space Center for Launch, VAB / SPH for Prelaunch)
    /// after reloading the origin RP's quicksave.
    /// </summary>
    internal enum RevertTarget
    {
        /// <summary>Esc &gt; Revert to Launch, or the flight-results Revert-to-Launch button. Discard Re-fly reloads the RP quicksave then transitions to <see cref="GameScenes.SPACECENTER"/>.</summary>
        Launch,
        /// <summary>Esc &gt; Revert to VAB / Revert to SPH, or the flight-results equivalent. Discard Re-fly reloads the RP quicksave then transitions to <see cref="GameScenes.EDITOR"/> at the clicked <see cref="EditorFacility"/>.</summary>
        Prelaunch,
    }

    /// <summary>
    /// Phase 12 of Rewind-to-Staging (design §6.7): intercepts
    /// <see cref="FlightDriver.RevertToLaunch"/> AND
    /// <see cref="FlightDriver.RevertToPrelaunch"/> when a re-fly session is
    /// active and routes the player into <see cref="ReFlyRevertDialog"/>
    /// instead of running the stock revert.
    ///
    /// <para>
    /// [ERS-exempt — Phase 12/§6.14] <see cref="DiscardReFlyHandler"/> reads
    /// <see cref="RecordingStore.CommittedRecordings"/> directly to resolve
    /// the session's NotCommitted provisional by
    /// <see cref="ReFlySessionMarker.ActiveReFlyRecordingId"/>. ERS filters
    /// out NotCommitted entries by definition, so the raw read is the only
    /// way to find the provisional we need to remove. See the file-level
    /// entry in <c>scripts/ers-els-audit-allowlist.txt</c>.
    /// </para>
    ///
    /// <para>
    /// The two patch classes <see cref="RevertToLaunchInterceptor"/> and
    /// <see cref="RevertToPrelaunchInterceptor"/> each carry a
    /// <c>[HarmonyPatch]</c> attribute for their respective stock method and
    /// both delegate to <see cref="Prefix"/> here, parameterised by
    /// <see cref="RevertTarget"/>.
    ///
    /// When <see cref="ParsekScenario.ActiveReFlySessionMarker"/> is null the
    /// prefix returns <c>true</c> and the stock revert runs unchanged. When the
    /// marker is non-null, the prefix returns <c>false</c> (blocking stock
    /// revert) and spawns the 3-option dialog. Each dialog branch wires to a
    /// static handler method on this class:
    ///
    /// <list type="bullet">
    ///   <item><description><see cref="RetryHandler"/> — clears the marker, generates a fresh <see cref="Guid"/> session id, and re-invokes <see cref="RewindInvoker.StartInvoke"/> with the same RP + slot captured from the marker. The old provisional becomes a zombie that the load-time sweep (Phase 13) cleans up. Retry is RP-anchored and returns the player to FLIGHT regardless of which revert button was clicked.</description></item>
    ///   <item><description><see cref="DiscardReFlyHandler"/> — removes the current session's provisional recording from <see cref="RecordingStore.CommittedRecordings"/>, promotes the origin RP to persistent so the post-load <see cref="LoadTimeSweep"/> does not reap it, clears marker + journal, bumps the supersede state version, then stages the RP quicksave through the save-root copy path and calls <see cref="GamePersistence.LoadGame"/> + <see cref="HighLogic.LoadScene"/> to transition to <see cref="GameScenes.SPACECENTER"/> (Launch) or <see cref="GameScenes.EDITOR"/> with the clicked facility (Prelaunch). Does NOT call <see cref="TreeDiscardPurge.PurgeTree"/>; the tree's other RPs, supersede relations, and tombstones are preserved.</description></item>
    ///   <item><description><see cref="CancelHandler"/> — pure logging; no state changes.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class RevertInterceptor
    {
        private const string SessionTag = "ReFlySession";
        private const string PatchTag = "RevertInterceptor";
        private const string RewindSaveTag = "RewindSave";

        /// <summary>
        /// Test seam: set to non-null in unit tests to observe that the prefix
        /// asked the dialog to spawn. The real prefix path calls
        /// <see cref="ReFlyRevertDialog.Show"/>; tests can short-circuit.
        /// </summary>
        internal static Action<ReFlySessionMarker> DialogShowForTesting;

        /// <summary>
        /// Test seam: set to non-null in unit tests to observe the Retry
        /// handler firing <see cref="RewindInvoker.StartInvoke"/> without
        /// pulling in the full KSP load pipeline. Receives the <c>rp</c> and
        /// <c>slot</c> captured from the marker.
        /// </summary>
        internal static Action<RewindPoint, ChildSlot> RewindInvokeStartForTesting;

        /// <summary>
        /// Test seam: when non-null, suppresses the real
        /// <see cref="GamePersistence.LoadGame"/> call in
        /// <see cref="DiscardReFlyHandler"/>. Receives the resolved
        /// <see cref="RewindPoint"/> + the temp file load name the handler
        /// computed; lets unit tests observe the handler's intent without
        /// pulling in Unity statics.
        /// </summary>
        internal static Action<RewindPoint, string> DiscardReFlyLoadGameForTesting;

        /// <summary>
        /// Test seam: when non-null, suppresses <see cref="HighLogic.LoadScene"/>
        /// plus the <see cref="EditorDriver"/> pre-sets in
        /// <see cref="DiscardReFlyHandler"/>. Receives the target
        /// <see cref="GameScenes"/> value + the <see cref="EditorFacility"/>
        /// (meaningful only for <see cref="RevertTarget.Prelaunch"/>).
        /// </summary>
        internal static Action<GameScenes, EditorFacility> DiscardReFlyLoadSceneForTesting;

        /// <summary>
        /// Test seam: when non-null, suppresses the
        /// <see cref="ScreenMessages.PostScreenMessage"/> call the discard path
        /// uses for user-visible error toasts so unit tests can capture the
        /// message text without hitting the live UI.
        /// </summary>
        internal static Action<string> ScreenMessagePostForTesting;

        /// <summary>
        /// Test seam: when non-null, overrides the on-disk
        /// <see cref="File.Exists"/> check the handler runs against the origin
        /// RP's quicksave path before staging the copy. Returning <c>false</c>
        /// simulates a missing source file; returning <c>true</c> forces the
        /// "source present" branch regardless of the actual filesystem state.
        /// </summary>
        internal static Func<RewindPoint, bool> DiscardReFlyQuicksaveExistsForTesting;

        /// <summary>Clears all Phase 12 test seams.</summary>
        internal static void ResetTestOverrides()
        {
            DialogShowForTesting = null;
            RewindInvokeStartForTesting = null;
            DiscardReFlyLoadGameForTesting = null;
            DiscardReFlyLoadSceneForTesting = null;
            ScreenMessagePostForTesting = null;
            DiscardReFlyQuicksaveExistsForTesting = null;
        }

        /// <summary>
        /// Shared prefix dispatcher. Invoked by the two thin patch classes
        /// (<see cref="RevertToLaunchInterceptor"/> and
        /// <see cref="RevertToPrelaunchInterceptor"/>) with the appropriate
        /// <see cref="RevertTarget"/>. Returning <c>false</c> blocks the stock
        /// method body; returning <c>true</c> lets it run. Gate is simple:
        /// <c>ParsekScenario.Instance?.ActiveReFlySessionMarker != null</c>.
        /// </summary>
        /// <param name="target">Which revert button the player clicked.</param>
        /// <param name="facility">For <see cref="RevertTarget.Prelaunch"/>, the
        /// <see cref="EditorFacility"/> value the stock call passed (VAB or SPH).
        /// Captured into the Discard Re-fly closure so the handler's scene
        /// transition lands the player in the correct editor. Ignored when
        /// <paramref name="target"/> is <see cref="RevertTarget.Launch"/>.</param>
        internal static bool Prefix(RevertTarget target, EditorFacility facility = EditorFacility.VAB)
        {
            if (!ShouldBlock(out var marker))
            {
                ParsekLog.Verbose(PatchTag,
                    $"Prefix: no active re-fly session — allowing stock RevertTo{target}");
                return true;
            }

            string sessionId = marker.SessionId ?? "<no-id>";
            ParsekLog.Info(PatchTag,
                $"Prefix: blocking stock RevertTo{target} sess={sessionId} target={target} facility={facility} — showing re-fly dialog");

            var dialogHook = DialogShowForTesting;
            if (dialogHook != null)
            {
                dialogHook(marker);
            }
            else
            {
                // Capture the marker + target + facility the handlers need at
                // dialog spawn time; the marker may be cleared by the time a
                // callback fires (Retry mutates it mid-invocation).
                var capturedMarker = marker;
                var capturedTarget = target;
                var capturedFacility = facility;
                ReFlyRevertDialog.Show(
                    marker,
                    capturedTarget,
                    onRetry: () => RetryHandler(capturedMarker, capturedTarget),
                    onDiscardReFly: () => DiscardReFlyHandler(capturedMarker, capturedTarget, capturedFacility),
                    onCancel: () => CancelHandler(capturedMarker, capturedTarget));
            }

            return false;
        }

        /// <summary>
        /// Back-compat overload for existing callers / tests that don't care
        /// about the revert-target context. Defaults to
        /// <see cref="RevertTarget.Launch"/>.
        /// </summary>
        internal static bool Prefix() => Prefix(RevertTarget.Launch);

        /// <summary>
        /// True when the active scenario has a non-null re-fly marker. Pulled
        /// out for direct unit-test invocation without Harmony scaffolding.
        /// </summary>
        internal static bool ShouldBlock(out ReFlySessionMarker marker)
        {
            marker = null;
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario)) return false;
            marker = scenario.ActiveReFlySessionMarker;
            return marker != null;
        }

        // ------------------------------------------------------------------
        // Callback handlers
        // ------------------------------------------------------------------

        /// <summary>
        /// Retry path: generate a fresh session id and re-invoke rewind with
        /// the same RP + slot. The old provisional + marker are cleared so
        /// <see cref="RewindInvoker.StartInvoke"/> sees a clean slate (its
        /// own precondition rejects a nested session). The old provisional
        /// recording is left in the committed list as a zombie — Phase 13
        /// load-time sweep purges zombies at next load.
        ///
        /// <para>
        /// Retry's semantics are RP-anchored and do not depend on
        /// <paramref name="target"/> — the RP quicksave is a flight-scene
        /// save, so Retry always lands the player back in FLIGHT regardless
        /// of whether they clicked Revert-to-Launch or Revert-to-VAB/SPH.
        /// The target is kept on the log line for symmetry with the other
        /// handlers.
        /// </para>
        /// </summary>
        internal static void RetryHandler(ReFlySessionMarker marker, RevertTarget target = RevertTarget.Launch)
        {
            if (marker == null)
            {
                ParsekLog.Warn(SessionTag, $"RetryHandler: null marker target={target} — cannot retry");
                return;
            }

            string oldSessionId = marker.SessionId ?? "<no-id>";
            string rpId = marker.RewindPointId ?? "<no-rp>";

            // Look up the RP + slot from scenario by id before clearing the
            // marker, so the later StartInvoke call has valid inputs even
            // though the marker is gone.
            RewindPoint rp = FindRewindPointById(rpId);
            if (rp == null)
            {
                ParsekLog.Error(SessionTag,
                    $"RetryHandler: cannot resolve rp={rpId} for sess={oldSessionId} target={target} — aborting retry");
                ReFlyRevertDialog.ClearLock();
                return;
            }

            ChildSlot slot = FindSlotForMarker(rp, marker);
            if (slot == null)
            {
                ParsekLog.Error(SessionTag,
                    $"RetryHandler: cannot resolve slot for origin={marker.OriginChildRecordingId ?? "<none>"} " +
                    $"in rp={rpId} sess={oldSessionId} target={target} — aborting retry");
                ReFlyRevertDialog.ClearLock();
                return;
            }

            ParsekLog.Info(SessionTag,
                $"End reason=retry sess={oldSessionId} rp={rpId} slot={slot.SlotIndex} target={target}");

            // Clear the active marker so the new StartInvoke precondition
            // (§7.5) sees no active session. The provisional recording stays
            // in the committed list and will be swept as a zombie.
            var scenario = ParsekScenario.Instance;
            if (!ReferenceEquals(null, scenario))
            {
                scenario.ActiveReFlySessionMarker = null;
                Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
                scenario.BumpSupersedeStateVersion();
                ReFlyRevertButtonGate.Apply("RetryHandler:marker-cleared");
                ParsekLog.Verbose(SessionTag,
                    $"RetryHandler: marker cleared for sess={oldSessionId} target={target}; re-invoking rewind");
            }

            var invokeHook = RewindInvokeStartForTesting;
            if (invokeHook != null)
            {
                invokeHook(rp, slot);
                return;
            }

            RewindInvoker.StartInvoke(rp, slot);
        }

        /// <summary>
        /// Discard Re-fly path (design §6.14): session-scoped cleanup that
        /// throws away only the current re-fly attempt's artifacts, reloads
        /// the origin RP's quicksave, and transitions to the scene the player
        /// clicked. The tree's other Rewind Points, supersede relations, and
        /// tombstones stay intact; the Unfinished Flights entry for the
        /// origin split stays visible because the origin RP is promoted from
        /// <c>SessionProvisional=true</c> to persistent BEFORE the marker is
        /// cleared (so <see cref="LoadTimeSweep"/>'s post-load RP-discard
        /// pass leaves it alone per <c>LoadTimeSweep.cs:139</c>'s
        /// <c>if (!rp.SessionProvisional) continue;</c> invariant).
        ///
        /// <para>
        /// Steps, each recoverable — a failure in step N must not strand
        /// half-cleared state for step N+1:
        /// <list type="number">
        ///   <item><description>Validate marker + resolve the origin RP.</description></item>
        ///   <item><description>Defensive check: refuse if <see cref="ParsekScenario.ActiveMergeJournal"/> is non-null (dialog gate also hides the button in that case).</description></item>
        ///   <item><description>Remove the provisional re-fly recording from <see cref="RecordingStore"/> (by-id inline lookup; <see cref="RecordingStore.RemoveCommittedInternal"/> bumps <c>StateVersion</c>).</description></item>
        ///   <item><description>Promote the origin RP: <c>SessionProvisional=false</c>, <c>CreatingSessionId=null</c>.</description></item>
        ///   <item><description>Clear <c>ActiveReFlySessionMarker</c> + <c>ActiveMergeJournal</c>.</description></item>
        ///   <item><description>Bump <c>SupersedeStateVersion</c> (belt-and-braces; <c>RemoveCommittedInternal</c> also bumped <c>StateVersion</c>).</description></item>
        ///   <item><description>Defensively delete the prior session's temp quicksave <c>saves/&lt;save&gt;/Parsek_Rewind_&lt;sessionId&gt;.sfs</c> (99% path: already gone).</description></item>
        ///   <item><description>Check the origin RP's quicksave exists on disk; if missing, show an error toast and leave the player in flight (marker is already cleared so Revert again lands on Continue Flying or Retry).</description></item>
        ///   <item><description>Copy the RP quicksave to the save root via <see cref="RewindInvoker.CopyQuicksaveToSaveRoot"/>, <see cref="GamePersistence.LoadGame"/>, delete temp, assign <see cref="HighLogic.CurrentGame"/>.</description></item>
        ///   <item><description>Transition: <see cref="HighLogic.LoadScene(GameScenes)"/> with <see cref="GameScenes.SPACECENTER"/> (Launch) or (for Prelaunch) pre-set <c>EditorDriver.StartupBehaviour=START_CLEAN</c> + <c>EditorDriver.editorFacility=facility</c> then <see cref="GameScenes.EDITOR"/>.</description></item>
        ///   <item><description>Log <c>[ReFlySession] End reason=discardReFly sess=&lt;id&gt; target=&lt;Launch|Prelaunch&gt; facility=&lt;VAB|SPH|--&gt;</c>.</description></item>
        /// </list>
        /// </para>
        /// </summary>
        internal static void DiscardReFlyHandler(
            ReFlySessionMarker marker,
            RevertTarget target = RevertTarget.Launch,
            EditorFacility facility = EditorFacility.VAB)
        {
            if (marker == null)
            {
                ParsekLog.Warn(SessionTag,
                    $"DiscardReFlyHandler: null marker target={target} — cannot discard");
                return;
            }

            string sessionId = marker.SessionId ?? "<no-id>";
            string rpId = marker.RewindPointId;
            string activeReFlyRecId = marker.ActiveReFlyRecordingId;

            // Defensive: refuse when a merge journal is active. The dialog
            // also hides the Discard button in that case (see
            // ReFlyRevertDialog.Show). Belt-and-braces: any call site that
            // bypasses the dialog (e.g. a test) gets a clear Warn log + user
            // toast instead of a half-run handler.
            var scenario = ParsekScenario.Instance;
            if (!ReferenceEquals(null, scenario) && scenario.ActiveMergeJournal != null)
            {
                ParsekLog.Warn(SessionTag,
                    $"DiscardReFlyHandler: refusing — merge journal active " +
                    $"sess={sessionId} target={target} journal={scenario.ActiveMergeJournal.JournalId ?? "<no-id>"}");
                PostScreenMessage("Discard Re-fly: merge in progress — retry in a moment");
                return;
            }

            RewindPoint originRp = FindRewindPointById(rpId);
            if (string.IsNullOrEmpty(rpId) || originRp == null)
            {
                ParsekLog.Error(SessionTag,
                    $"DiscardReFlyHandler: unresolvable rp={(rpId ?? "<none>")} sess={sessionId} " +
                    $"target={target} — clearing session artifacts but skipping scene transition");

                // Step 3-6 still run so the session does not strand the
                // player behind the marker; skip 7-10 (no RP to reload).
                RemoveProvisionalById(activeReFlyRecId, sessionId);
                if (!ReferenceEquals(null, scenario))
                {
                    scenario.ActiveReFlySessionMarker = null;
                    Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
                    scenario.ActiveMergeJournal = null;
                    scenario.BumpSupersedeStateVersion();
                    ReFlyRevertButtonGate.Apply("DiscardReFlyHandler:rp-unresolvable");
                }

                PostScreenMessage("Discard Re-fly failed: rewind point missing");
                ParsekLog.Info(SessionTag,
                    $"End reason=discardReFly sess={sessionId} target={target}" +
                    (target == RevertTarget.Prelaunch ? $" facility={facility}" : " facility=--") +
                    " dispatched=false");
                return;
            }

            // Step 3: drop the provisional. Log + continue on absence — session
            // cleanup is desired even on a partially-corrupted marker.
            RemoveProvisionalById(activeReFlyRecId, sessionId);

            // Step 4: promote the origin RP to persistent BEFORE clearing
            // the marker — LoadTimeSweep's RP-discard pass in the fresh
            // scenario skips any RP with SessionProvisional=false, which is
            // how we keep the Unfinished Flights row visible (the §5.4
            // BranchPoint->RP check requires an RP to resolve).
            if (originRp.SessionProvisional)
            {
                originRp.SessionProvisional = false;
                ParsekLog.Info(SessionTag,
                    $"Origin RP promoted to persistent rp={rpId} sess={sessionId} reason=discardReFly");
            }
            else
            {
                ParsekLog.Verbose(SessionTag,
                    $"Origin RP already persistent rp={rpId} sess={sessionId} — no promotion needed");
            }
            // Always clear CreatingSessionId — defensive against invariant
            // violations where a persistent RP can carry a stale
            // CreatingSessionId from a crashed session.
            originRp.CreatingSessionId = null;

            // Step 5-6: clear scenario session state + bump caches.
            if (!ReferenceEquals(null, scenario))
            {
                scenario.ActiveReFlySessionMarker = null;
                Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
                scenario.ActiveMergeJournal = null;
                scenario.BumpSupersedeStateVersion();
                // Apply now so the failure paths in steps 8-9 (which may bail
                // and leave the player in flight) do not strand a forced
                // CanRevertToPostInit. The success path transitions out of
                // flight where the next FlightDriver.Start will recompute,
                // making the call a logged no-op there — cheap and consistent.
                ReFlyRevertButtonGate.Apply("DiscardReFlyHandler:marker-cleared");
            }

            // Step 7: defensively clean up the prior session's temp quicksave
            // (the normal path deletes this in RewindInvoker.ConsumePostLoad's
            // finally block; this is belt-and-braces for a crash-resumed
            // session where that finally never ran).
            TryDeletePriorSessionTemp(sessionId);

            // Step 8: verify the origin RP's quicksave is present on disk.
            if (!QuicksaveExists(originRp))
            {
                ParsekLog.Error(RewindSaveTag,
                    $"DiscardReFly: rewind point quicksave missing rp={rpId} sess={sessionId} " +
                    $"file='{originRp.QuicksaveFilename ?? "<none>"}'");
                PostScreenMessage("Discard Re-fly failed: rewind point quicksave missing");
                ParsekLog.Info(SessionTag,
                    $"End reason=discardReFly sess={sessionId} target={target}" +
                    (target == RevertTarget.Prelaunch ? $" facility={facility}" : " facility=--") +
                    " dispatched=false");
                return;
            }

            // Step 9: stage + LoadGame + LoadScene. In production we use
            // RewindInvoker.CopyQuicksaveToSaveRoot (same file naming + path
            // handling RewindInvoker uses) then GamePersistence.LoadGame.
            // In unit tests DiscardReFlyLoadGameForTesting short-circuits
            // both calls — we skip the real copy (no KSP filesystem to copy
            // from) and synthesize a fake temp name so the seam still sees
            // the handler's intent.
            string discardSessionId = "discard_" + Guid.NewGuid().ToString("N");
            var loadGameHook = DiscardReFlyLoadGameForTesting;
            bool loadSucceeded;
            if (loadGameHook != null)
            {
                string fakeTempName = "Parsek_Rewind_" + discardSessionId;
                loadGameHook(originRp, fakeTempName);
                loadSucceeded = true;
            }
            else
            {
                string tempPath;
                string tempLoadName;
                try
                {
                    RewindInvoker.CopyQuicksaveToSaveRoot(
                        originRp, discardSessionId, out tempPath, out tempLoadName);
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(RewindSaveTag,
                        $"DiscardReFly: copy-to-root threw rp={rpId} sess={sessionId}: {ex.GetType().Name}: {ex.Message}");
                    PostScreenMessage("Discard Re-fly failed: could not stage rewind point");
                    ParsekLog.Info(SessionTag,
                        $"End reason=discardReFly sess={sessionId} target={target}" +
                        (target == RevertTarget.Prelaunch ? $" facility={facility}" : " facility=--") +
                        " dispatched=false");
                    return;
                }

                if (string.IsNullOrEmpty(tempPath) || string.IsNullOrEmpty(tempLoadName))
                {
                    ParsekLog.Error(RewindSaveTag,
                        $"DiscardReFly: staging returned empty path rp={rpId} sess={sessionId}");
                    PostScreenMessage("Discard Re-fly failed: could not stage rewind point");
                    ParsekLog.Info(SessionTag,
                        $"End reason=discardReFly sess={sessionId} target={target}" +
                        (target == RevertTarget.Prelaunch ? $" facility={facility}" : " facility=--") +
                        " dispatched=false");
                    return;
                }

                loadSucceeded = TryLoadGameAndAssign(tempPath, tempLoadName, rpId, sessionId);
            }

            if (!loadSucceeded)
            {
                PostScreenMessage("Discard Re-fly failed: could not load rewind point");
                ParsekLog.Info(SessionTag,
                    $"End reason=discardReFly sess={sessionId} target={target}" +
                    (target == RevertTarget.Prelaunch ? $" facility={facility}" : " facility=--") +
                    " dispatched=false");
                return;
            }

            // Step 10: scene transition.
            DispatchScene(target, facility);

            // Step 11: log end line. Append dispatched=true so greppers can
            // filter on dispatched=<true|false> symmetrically across the
            // success and failure paths.
            string facilityText = target == RevertTarget.Prelaunch
                ? $" facility={facility}"
                : " facility=--";
            ParsekLog.Info(SessionTag,
                $"End reason=discardReFly sess={sessionId} target={target}{facilityText} dispatched=true");
        }

        /// <summary>
        /// Cancel path: purely informational. Marker and all session state
        /// are left untouched; the player resumes flight.
        /// </summary>
        internal static void CancelHandler(ReFlySessionMarker marker, RevertTarget target = RevertTarget.Launch)
        {
            string sessionId = marker?.SessionId ?? "<no-id>";
            ParsekLog.Info(SessionTag, $"Revert dialog cancelled sess={sessionId} target={target}");
        }

        // ------------------------------------------------------------------
        // Discard Re-fly helpers
        // ------------------------------------------------------------------

        private static void RemoveProvisionalById(string recordingId, string sessionId)
        {
            if (string.IsNullOrEmpty(recordingId))
            {
                ParsekLog.Warn(SessionTag,
                    $"DiscardReFly: marker had empty ActiveReFlyRecordingId sess={sessionId} — skipping provisional removal");
                return;
            }

            // Inline by-id lookup on CommittedRecordings — mirrors the pattern
            // RewindInvoker uses at the atomic rollback path. No new public
            // API surface.
            var committed = RecordingStore.CommittedRecordings;
            Recording target = null;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec == null) continue;
                    if (string.Equals(rec.RecordingId, recordingId, StringComparison.Ordinal))
                    {
                        target = rec;
                        break;
                    }
                }
            }

            if (target == null)
            {
                ParsekLog.Warn("RecordingStore",
                    $"DiscardReFly: provisional rec={recordingId} sess={sessionId} not in committed list — already gone");
                return;
            }

            bool removed = RecordingStore.RemoveCommittedInternal(target);
            if (removed)
            {
                ParsekLog.Info("RecordingStore",
                    $"Removed provisional rec={recordingId} sess={sessionId}");
            }
            else
            {
                ParsekLog.Warn("RecordingStore",
                    $"DiscardReFly: RemoveCommittedInternal returned false for rec={recordingId} sess={sessionId}");
            }
        }

        private static bool QuicksaveExists(RewindPoint rp)
        {
            var override_ = DiscardReFlyQuicksaveExistsForTesting;
            if (override_ != null)
            {
                try { return override_(rp); }
                catch (Exception ex)
                {
                    ParsekLog.Warn(SessionTag,
                        $"DiscardReFlyQuicksaveExistsForTesting threw: {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }

            if (rp == null || string.IsNullOrEmpty(rp.QuicksaveFilename))
                return false;
            try
            {
                string abs = RewindInvoker.ResolveAbsoluteQuicksavePath(rp);
                return !string.IsNullOrEmpty(abs) && File.Exists(abs);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(SessionTag,
                    $"DiscardReFly: QuicksaveExists check threw rp={rp.RewindPointId}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void TryDeletePriorSessionTemp(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            try
            {
                string root = KSPUtil.ApplicationRootPath ?? "";
                string saveFolder = HighLogic.SaveFolder ?? "";
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder)) return;

                string path = Path.Combine(root, "saves", saveFolder,
                    "Parsek_Rewind_" + sessionId + ".sfs");
                if (File.Exists(path))
                {
                    File.Delete(path);
                    ParsekLog.Verbose(SessionTag,
                        $"DiscardReFly: deleted prior session temp '{path}'");
                }
                else
                {
                    ParsekLog.Verbose(SessionTag,
                        $"DiscardReFly: no prior session temp to delete sess={sessionId}");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(SessionTag,
                    $"DiscardReFly: prior session temp delete threw sess={sessionId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void TryDeleteTemp(string tempPath)
        {
            if (string.IsNullOrEmpty(tempPath)) return;
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                    ParsekLog.Verbose(SessionTag,
                        $"DiscardReFly: deleted staging temp '{tempPath}'");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(SessionTag,
                    $"DiscardReFly: staging temp delete threw '{tempPath}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool TryLoadGameAndAssign(
            string tempPath, string tempLoadName, string rpId, string sessionId)
        {
            try
            {
                ParsekLog.Info(RewindSaveTag,
                    $"DiscardReFly: loading temp tempPath='{tempPath}' loadName='{tempLoadName}' " +
                    $"saveFolder='{HighLogic.SaveFolder ?? "<none>"}'");
                Game game = GamePersistence.LoadGame(
                    tempLoadName, HighLogic.SaveFolder, true, false);
                if (game == null)
                {
                    ParsekLog.Error(RewindSaveTag,
                        $"DiscardReFly: LoadGame returned null rp={rpId} sess={sessionId}");
                    TryDeleteTemp(tempPath);
                    return false;
                }

                HighLogic.CurrentGame = game;
                TryDeleteTemp(tempPath);
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Error(RewindSaveTag,
                    $"DiscardReFly: LoadGame threw rp={rpId} sess={sessionId}: {ex.GetType().Name}: {ex.Message}");
                TryDeleteTemp(tempPath);
                return false;
            }
        }

        private static void DispatchScene(RevertTarget target, EditorFacility facility)
        {
            var hook = DiscardReFlyLoadSceneForTesting;
            if (hook != null)
            {
                hook(target == RevertTarget.Prelaunch ? GameScenes.EDITOR : GameScenes.SPACECENTER,
                    facility);
                return;
            }

            try
            {
                if (target == RevertTarget.Prelaunch)
                {
                    // Pre-set before LoadScene so EditorDriver.Awake sees the
                    // correct facility when the scene comes up. START_CLEAN
                    // (NOT LOAD_FROM_CACHE) is the right StartupBehaviour
                    // after GamePersistence.LoadGame swapped HighLogic.CurrentGame
                    // — the cached ShipConstruct is stale / from a different
                    // game. See design §6.14 / plan §Approach step 8.
                    EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.START_CLEAN;
                    EditorDriver.editorFacility = facility;
                    HighLogic.LoadScene(GameScenes.EDITOR);
                }
                else
                {
                    HighLogic.LoadScene(GameScenes.SPACECENTER);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Error(PatchTag,
                    $"DiscardReFly: scene dispatch threw target={target} facility={facility}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void PostScreenMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            var hook = ScreenMessagePostForTesting;
            if (hook != null)
            {
                try { hook(message); }
                catch (Exception ex)
                {
                    ParsekLog.Warn(PatchTag,
                        $"ScreenMessagePostForTesting threw: {ex.GetType().Name}: {ex.Message}");
                }
                return;
            }

            try
            {
                ScreenMessages.PostScreenMessage(
                    message, 5f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(PatchTag,
                    $"PostScreenMessage threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static RewindPoint FindRewindPointById(string rewindPointId)
        {
            if (string.IsNullOrEmpty(rewindPointId)) return null;
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
                return null;
            List<RewindPoint> rps = scenario.RewindPoints;
            for (int i = 0; i < rps.Count; i++)
            {
                var rp = rps[i];
                if (rp == null) continue;
                if (string.Equals(rp.RewindPointId, rewindPointId, StringComparison.Ordinal))
                    return rp;
            }
            return null;
        }

        private static ChildSlot FindSlotForMarker(RewindPoint rp, ReFlySessionMarker marker)
        {
            if (rp == null || rp.ChildSlots == null || marker == null) return null;
            string originId = marker.OriginChildRecordingId;
            if (string.IsNullOrEmpty(originId)) return null;
            for (int i = 0; i < rp.ChildSlots.Count; i++)
            {
                var slot = rp.ChildSlots[i];
                if (slot == null) continue;
                if (string.Equals(slot.OriginChildRecordingId, originId, StringComparison.Ordinal))
                    return slot;
            }
            return null;
        }
    }

    /// <summary>
    /// Thin Harmony patch class targeting
    /// <see cref="FlightDriver.RevertToLaunch"/> (Esc &gt; Revert to Launch,
    /// flight-results Revert-to-Launch). Delegates to
    /// <see cref="RevertInterceptor.Prefix(RevertTarget)"/> with
    /// <see cref="RevertTarget.Launch"/>.
    /// </summary>
    [HarmonyPatch(typeof(FlightDriver), nameof(FlightDriver.RevertToLaunch))]
    internal static class RevertToLaunchInterceptor
    {
        [HarmonyPrefix]
        internal static bool Prefix() => RevertInterceptor.Prefix(RevertTarget.Launch);
    }

    /// <summary>
    /// Thin Harmony patch class targeting
    /// <see cref="FlightDriver.RevertToPrelaunch"/> (Esc &gt; Revert to VAB /
    /// Revert to SPH, flight-results equivalent). Delegates to
    /// <see cref="RevertInterceptor.Prefix(RevertTarget, EditorFacility)"/>
    /// with <see cref="RevertTarget.Prelaunch"/>, passing through the
    /// <c>facility</c> argument so the Discard Re-fly closure can drive the
    /// scene transition to the same editor the player originally clicked.
    /// </summary>
    [HarmonyPatch(typeof(FlightDriver), nameof(FlightDriver.RevertToPrelaunch))]
    internal static class RevertToPrelaunchInterceptor
    {
        [HarmonyPrefix]
        internal static bool Prefix(EditorFacility facility) =>
            RevertInterceptor.Prefix(RevertTarget.Prelaunch, facility);
    }
}
