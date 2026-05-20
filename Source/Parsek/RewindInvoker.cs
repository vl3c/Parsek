using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Phase 6 of Rewind-to-Staging (design §6.3 + §6.4): orchestrates the
    /// user-initiated rewind-to-rewind-point flow. The flow straddles a KSP
    /// scene reload:
    ///
    /// <list type="number">
    ///   <item><description><c>CanInvoke</c> precondition gates (§7.5 / §7.22 / §7.29 / §7.34)</description></item>
    ///   <item><description><c>ShowDialog</c> displays the confirmation popup</description></item>
    ///   <item><description>On confirm, <c>StartInvoke</c> runs the PRE-LOAD phase synchronously: validate preconditions, generate <c>sessionId</c>, capture the reconciliation bundle, stash it in <see cref="RewindInvokeContext"/>, copy the RP's quicksave to the save-root, and trigger <c>GamePersistence.LoadGame</c> + <c>HighLogic.LoadScene(FLIGHT)</c>.</description></item>
    ///   <item><description>KSP reloads the scene. The old <see cref="ParsekScenario"/> MonoBehaviour dies and Unity stops its coroutines; only the static <see cref="RewindInvokeContext"/> survives.</description></item>
    ///   <item><description>The new <see cref="ParsekScenario.OnLoad"/> calls <see cref="ConsumePostLoad"/>, which runs the POST-LOAD phase synchronously: <see cref="ReconciliationBundle.Restore"/>, <see cref="PostLoadStripper.Strip"/>, <c>FlightGlobals.SetActiveVessel</c>, <see cref="AtomicMarkerWrite"/>, delete the temp quicksave, clear the context, recalculate the ledger.</description></item>
    /// </list>
    ///
    /// <para>
    /// This matches the legacy rewind-to-launch pattern in
    /// <see cref="RecordingStore.InitiateRewind"/> — the only known-good way to
    /// resume Parsek work after a KSP quickload in a fresh scene.
    /// </para>
    ///
    /// <para>
    /// [ERS-exempt — Phase 6] The invoker correlates live vessels to the RP's
    /// PidSlotMap/RootPartPidMap via raw <c>Vessel.persistentId</c> reads; this
    /// is a physical identity correlation at load time, not a supersede-aware
    /// recording lookup, so routing through <see cref="EffectiveState.ComputeERS"/>
    /// would not apply. The file is allowlisted in
    /// <c>scripts/ers-els-audit-allowlist.txt</c> with a call-site rationale.
    /// </para>
    /// </summary>
    internal static class RewindInvoker
    {
        private const string InvokeTag = "Rewind";
        private const string UITag = "RewindUI";
        private const string SessionTag = "ReFlySession";

        // Test seam: allows unit tests to capture the synchronous atomic block
        // phase boundaries without running the full coroutine. Set to non-null
        // in tests to record each checkpoint; the invoker calls it at key
        // points during the §6.3 step 4 phase 1+2 critical section.
        internal static Action<string> CheckpointHookForTesting;
        internal static Func<RewindPoint, string> ResolveAbsoluteQuicksavePathOverrideForTesting;

        /// <summary>
        /// Returns <c>true</c> if the Rewind button for <paramref name="rp"/>
        /// should be enabled. Checks five preconditions per §6.3 / §7.22:
        /// <list type="bullet">
        ///   <item><description>Scene is FLIGHT / SPACECENTER / TRACKSTATION — not a scene transition (§7.22)</description></item>
        ///   <item><description>RP is not Corrupted</description></item>
        ///   <item><description>Quicksave file exists on disk (§7.34)</description></item>
        ///   <item><description>No other re-fly session is active (§7.5)</description></item>
        ///   <item><description>Deep-parse precondition passes — every PART node in the quicksave resolves via <see cref="PartLoader.getPartInfoByName"/> (§7.29)</description></item>
        /// </list>
        /// The result + reason are cached per RP for 60s via
        /// <see cref="PreconditionCache"/> so repeated UI draws do not re-parse
        /// the .sfs every frame.
        /// </summary>
        internal static bool CanInvoke(RewindPoint rp, out string reason)
        {
            bool canInvoke = EvaluateCanInvoke(rp, out reason);
            LogCanInvokeDecision(rp, canInvoke, reason);
            return canInvoke;
        }

        private static bool EvaluateCanInvoke(RewindPoint rp, out string reason)
        {
            if (rp == null)
            {
                reason = "rewind point is null";
                return false;
            }

            // §7.22: reject during scene transitions. A scene reload is already
            // in flight, so firing another LoadGame on top of it would either
            // stomp state or deadlock Unity's loader.
            if (!IsInvokableScene(HighLogic.LoadedScene))
            {
                reason = "Scene transition in progress — please wait";
                return false;
            }

            // Context-level guard: another invocation is already mid-flight
            // (pre-load phase captured, post-load hasn't consumed yet).
            if (RewindInvokeContext.Pending)
            {
                reason = "Another rewind invocation is already in flight";
                return false;
            }

            if (rp.Corrupted)
            {
                reason = "Rewind point is marked corrupted";
                return false;
            }

            if (string.IsNullOrEmpty(rp.QuicksaveFilename))
            {
                reason = "Rewind point has no quicksave file";
                return false;
            }

            string abs = ResolveAbsoluteQuicksavePath(rp);
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs))
            {
                reason = $"Quicksave file missing on disk: {rp.QuicksaveFilename}";
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (!object.ReferenceEquals(null, scenario) && scenario.ActiveReFlySessionMarker != null)
            {
                reason = "Another re-fly session is already active";
                return false;
            }

            // Deep-parse PartLoader precondition (cached per RP).
            if (!PreconditionCache.IsValid(rp))
            {
                var result = PartLoaderPrecondition.Check(rp, abs);
                PreconditionCache.Store(rp, result);
            }

            var cached = PreconditionCache.Get(rp);
            if (cached.HasValue && !cached.Value.Passed)
            {
                reason = cached.Value.Reason ?? "Deep-parse precondition failed";
                return false;
            }

            reason = null;
            return true;
        }

        private static void LogCanInvokeDecision(RewindPoint rp, bool canInvoke, string reason)
        {
            string rpId = GetRewindPointIdForLog(rp);
            string normalizedReason = string.IsNullOrEmpty(reason) ? "<none>" : reason;
            string identity = "CanInvoke|" + rpId;
            string stateKey = canInvoke ? "enabled" : "disabled|" + normalizedReason;
            string quicksave = rp == null || string.IsNullOrEmpty(rp.QuicksaveFilename)
                ? "<none>"
                : rp.QuicksaveFilename;
            string absoluteQuicksave = null;
            if (rp != null && !string.IsNullOrEmpty(rp.QuicksaveFilename))
            {
                try { absoluteQuicksave = ResolveAbsoluteQuicksavePath(rp); }
                catch (Exception ex) { absoluteQuicksave = "<resolve-error:" + ex.GetType().Name + ">"; }
            }

            ParsekLog.VerboseOnChange(
                InvokeTag,
                identity,
                stateKey,
                canInvoke
                    ? $"CanInvoke: enabled rp={rpId} scene={SafeLoadedSceneForCanInvokeLog()} " +
                      $"quicksave='{quicksave}' path='{FormatCanInvokePath(absoluteQuicksave)}'"
                    : $"CanInvoke: disabled rp={rpId} reason='{normalizedReason}' " +
                      $"scene={SafeLoadedSceneForCanInvokeLog()} corrupted={FormatNullableBool(rp?.Corrupted)} " +
                      $"quicksave='{quicksave}' path='{FormatCanInvokePath(absoluteQuicksave)}' " +
                      $"pendingInvoke={RewindInvokeContext.Pending} activeSession={FormatActiveSessionForCanInvokeLog()}");
        }

        private static string GetRewindPointIdForLog(RewindPoint rp)
        {
            if (rp == null)
                return "<null>";
            return string.IsNullOrEmpty(rp.RewindPointId) ? "<no-id>" : rp.RewindPointId;
        }

        private static string FormatCanInvokePath(string path)
        {
            return string.IsNullOrEmpty(path) ? "<none>" : path;
        }

        private static string FormatNullableBool(bool? value)
        {
            return value.HasValue ? value.Value.ToString() : "<null>";
        }

        private static string SafeLoadedSceneForCanInvokeLog()
        {
            try { return HighLogic.LoadedScene.ToString(); }
            catch (Exception ex) { return "<scene-error:" + ex.GetType().Name + ">"; }
        }

        private static string FormatActiveSessionForCanInvokeLog()
        {
            try
            {
                var scenario = ParsekScenario.Instance;
                if (object.ReferenceEquals(null, scenario) || scenario.ActiveReFlySessionMarker == null)
                    return "none";

                string sessionId = scenario.ActiveReFlySessionMarker.SessionId;
                return string.IsNullOrEmpty(sessionId) ? "<no-session>" : sessionId;
            }
            catch (Exception ex)
            {
                return "<session-error:" + ex.GetType().Name + ">";
            }
        }

        /// <summary>
        /// Copies identity-relevant fields from <paramref name="inheritFrom"/>
        /// onto <paramref name="provisional"/> for the in-place Re-Fly fork
        /// (issue #734). Pure field copies — no Unity types, no instance
        /// state — so xUnit can validate the contract directly.
        ///
        /// PR 3b review follow-up §3: extracting this block lets a
        /// regression test fail fast if a future refactor touches the
        /// inheritance set without preserving every field. The
        /// <c>ParentAnchorRecordingId</c> copy is the load-bearing current-schema
        /// debris-anchor inheritance the plan flags as a critical
        /// Re-Fly safety hook (plan §"Risk analysis": "Re-Fly inheritance
        /// loses the contract — High if RewindInvoker propagation is
        /// omitted").
        ///
        /// Side-effecting follow-up steps (`CapturePreReFlyAnchorTrajectoryFrom`,
        /// `FindTreeForReFlyFork`, the diagnostic log line) stay inline at
        /// the call site because they depend on the surrounding
        /// <c>sessionId</c>, instance state, or call-site context.
        ///
        /// Chain identity (<c>ChainId</c>/<c>ChainIndex</c>/<c>ChainBranch</c>)
        /// is intentionally NOT copied so the supersede table remains the
        /// only authority on chain-tip resolution — see the call-site
        /// comment in <c>AtomicMarkerWrite</c>.
        /// </summary>
        internal static void CopyInheritedIdentityForFork(
            Recording provisional,
            Recording inheritFrom)
        {
            if (provisional == null || inheritFrom == null) return;
            provisional.VesselPersistentId = inheritFrom.VesselPersistentId;
            provisional.VesselName = inheritFrom.VesselName;
            provisional.IsDebris = inheritFrom.IsDebris;
            // PR 3b: critical Re-Fly safety hook — propagate the v13 debris
            // parent-anchor contract so a re-fly of a flight with debris children
            // doesn't silently lose the contract on the provisional. Without this,
            // the resolver's chain-walk would not have a parent recording id to
            // chase through supersede successors.
            provisional.ParentAnchorRecordingId = inheritFrom.ParentAnchorRecordingId;
            provisional.Generation = inheritFrom.Generation;
            // SegmentPhase / SegmentBodyName are intentionally NOT copied. Those fields
            // describe the most-recent segment of THIS recording — for the parent that
            // means its terminating phase, which is unrelated to where the fork actually
            // flies. The runtime tagger called from AtomicMarkerWrite immediately after
            // this helper populates them from the live post-Strip vessel instead. See
            // docs/dev/plans/fix-refly-fork-segment-phase-inheritance.md.
            provisional.StartBodyName = inheritFrom.StartBodyName;
            provisional.StartBiome = inheritFrom.StartBiome;
            provisional.StartSituation = inheritFrom.StartSituation;
            provisional.LaunchSiteName = inheritFrom.LaunchSiteName;
            provisional.VesselSnapshot = inheritFrom.VesselSnapshot != null
                ? inheritFrom.VesselSnapshot.CreateCopy()
                : null;
            provisional.GhostVisualSnapshot = inheritFrom.GhostVisualSnapshot != null
                ? inheritFrom.GhostVisualSnapshot.CreateCopy()
                : null;
        }

        /// <summary>
        /// Tags <paramref name="provisional"/>.<see cref="Recording.SegmentPhase"/>
        /// and <see cref="Recording.SegmentBodyName"/> from the live post-Strip
        /// vessel state at fork-creation time. Replaces the prior stale-inheritance
        /// path in <see cref="CopyInheritedIdentityForFork"/> — the fork is a new
        /// flight whose phase classification must come from its own starting state,
        /// not from the parent's most-recent-segment classification (which would be
        /// "atmo" for an inherited Suborbital flight even when the fork goes on to
        /// reach orbit).
        ///
        /// Delegates to <see cref="ParsekFlight.TagSegmentPhaseIfMissing"/> for the
        /// shared body/altitude/situation classification used by the rest of the
        /// runtime taggers, so the fork's tag uses the same vocabulary as every
        /// other recording's first segment.
        ///
        /// Logs Verbose at every branch (tagged / null-vessel / null-mainBody)
        /// for diagnostic visibility. Aligns with the sibling
        /// <see cref="TryRefreshForkSnapshotsFromLiveVessel"/> helper, which
        /// also runs immediately after this one in <see cref="AtomicMarkerWrite"/>
        /// and uses Verbose for the same null-vessel-in-test-fixture case.
        /// (Promoting either to Warn would fire on every existing
        /// <c>AtomicMarkerWriteTests</c> in-place test path that uses
        /// <c>MakeStripResult</c>'s null <c>SelectedVessel</c> stub, polluting
        /// the test log sink without an assertion.)
        /// </summary>
        internal static void TagForkInitialSegmentPhase(
            Recording provisional, Vessel liveVessel, string sessionId)
        {
            if (provisional == null) return;
            if (liveVessel == null)
            {
                ParsekLog.Verbose(InvokeTag,
                    "TagForkInitialSegmentPhase: live vessel null — leaving fork SegmentPhase " +
                    $"unset rec={provisional.RecordingId} sess={sessionId ?? "<none>"}");
                return;
            }

            ParsekFlight.TagSegmentPhaseIfMissing(provisional, liveVessel);

            if (string.IsNullOrEmpty(provisional.SegmentPhase))
            {
                ParsekLog.Verbose(InvokeTag,
                    $"TagForkInitialSegmentPhase: classification skipped (mainBody null) " +
                    $"rec={provisional.RecordingId} pid={liveVessel.persistentId} " +
                    $"sess={sessionId ?? "<none>"}");
                return;
            }

            ParsekLog.Verbose(InvokeTag,
                $"TagForkInitialSegmentPhase: tagged from live vessel " +
                $"rec={provisional.RecordingId} pid={liveVessel.persistentId} " +
                $"body={provisional.SegmentBodyName ?? "<none>"} " +
                $"phase={provisional.SegmentPhase} situation={liveVessel.situation} " +
                $"alt={liveVessel.altitude:F0}m sess={sessionId ?? "<none>"}");
        }

        /// <summary>
        /// Refreshes <see cref="Recording.VesselSnapshot"/> and
        /// <see cref="Recording.GhostVisualSnapshot"/> on an in-place Re-Fly
        /// fork from the live post-Strip vessel, if available, replacing the
        /// stale copies inherited via <see cref="CopyInheritedIdentityForFork"/>.
        ///
        /// <para>
        /// Why this is needed: the inheritance source's
        /// <c>GhostVisualSnapshot</c> was captured exactly once when the
        /// recording first started (see
        /// <c>FlightRecorder.captureInitialGhostSnapshot</c>) and is never
        /// refreshed by mid-flight events. <c>ProcessBreakupEvent</c> only
        /// refreshes <c>VesselSnapshot</c> on each breakup. So a fork created
        /// at a UT after staging or breakups inherits the pre-staging
        /// full-vessel ghost snapshot, and any subsequent ghost render of the
        /// fork shows the whole rocket instead of the current sub-assembly
        /// (issue surfaced in 2026-05-07 playtest: lower-stage Re-Fly spawned
        /// an 84-part Kerbal X ghost where the actual upper-stage capsule had
        /// shed all but a handful of parts).
        /// </para>
        ///
        /// <para>
        /// The strip selects exactly the vessel rooted at the slot's selected
        /// root part; <c>inPlaceContinuation</c> already gates on
        /// <c>inheritFrom.VesselPersistentId == stripResult.SelectedPid</c> so
        /// the live vessel is the right thing to snapshot. If
        /// <see cref="VesselSpawner.TryBackupSnapshot"/> fails (or the live
        /// vessel handle is null in tests), we leave the inherited copies in
        /// place — strictly no worse than before.
        /// </para>
        ///
        /// <para>
        /// Returns <c>true</c> when both snapshots were replaced from the
        /// live vessel, <c>false</c> when the inherited copies remain. The
        /// caller uses that flag to log <c>vesselSnapshot=live-refresh</c>
        /// vs <c>vesselSnapshot=copied</c> for diagnostic clarity.
        /// </para>
        /// </summary>
        internal static bool TryRefreshForkSnapshotsFromLiveVessel(
            Recording provisional, Vessel liveVessel, string sessionId)
        {
            if (provisional == null) return false;
            if (liveVessel == null)
            {
                ParsekLog.Verbose(InvokeTag,
                    $"TryRefreshForkSnapshotsFromLiveVessel: live vessel null — keeping inherited snapshots " +
                    $"rec={provisional.RecordingId} sess={sessionId ?? "<none>"}");
                return false;
            }

            ConfigNode freshSnap = VesselSpawner.TryBackupSnapshot(liveVessel);
            if (freshSnap == null)
            {
                ParsekLog.Warn(InvokeTag,
                    $"TryRefreshForkSnapshotsFromLiveVessel: TryBackupSnapshot returned null — keeping inherited snapshots " +
                    $"rec={provisional.RecordingId} pid={liveVessel.persistentId} sess={sessionId ?? "<none>"}");
                return false;
            }

            int liveParts = liveVessel.parts != null ? liveVessel.parts.Count : 0;
            int inheritedVesselParts = CountSnapshotParts(provisional.VesselSnapshot);
            int inheritedGhostParts = CountSnapshotParts(provisional.GhostVisualSnapshot);

            provisional.VesselSnapshot = freshSnap;
            provisional.GhostVisualSnapshot = freshSnap.CreateCopy();

            ParsekLog.Info(InvokeTag,
                $"TryRefreshForkSnapshotsFromLiveVessel: refreshed fork snapshots from live vessel " +
                $"rec={provisional.RecordingId} pid={liveVessel.persistentId} liveParts={liveParts} " +
                $"replaced(vesselSnapshotParts={inheritedVesselParts} ghostSnapshotParts={inheritedGhostParts}) " +
                $"sess={sessionId ?? "<none>"}");
            return true;
        }

        /// <summary>
        /// Counts <c>PART</c> child nodes of a backed-up vessel ConfigNode for
        /// diagnostic logging. Returns 0 for null/malformed snapshots —
        /// purely informational, no behavioural impact.
        /// </summary>
        internal static int CountSnapshotParts(ConfigNode snap)
        {
            if (snap == null) return 0;
            try
            {
                var parts = snap.GetNodes("PART");
                return parts != null ? parts.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Spawns the "Rewind?" confirmation PopupDialog. On accept, starts
        /// the <see cref="StartInvoke"/> pre-load phase; the post-load phase
        /// is driven by <see cref="ParsekScenario.OnLoad"/> calling
        /// <see cref="ConsumePostLoad"/> once the scene reload lands.
        /// </summary>
        internal static void ShowDialog(RewindPoint rp, int selectedSlotListIndex)
        {
            if (rp == null)
            {
                ParsekLog.Warn(UITag, "ShowDialog called with null RP");
                return;
            }
            if (rp.ChildSlots == null || selectedSlotListIndex < 0 || selectedSlotListIndex >= rp.ChildSlots.Count)
            {
                ParsekLog.Warn(UITag,
                    $"ShowDialog: invalid slot list index {selectedSlotListIndex} (slots={rp.ChildSlots?.Count ?? 0}) for rp={rp.RewindPointId}");
                return;
            }

            var selected = rp.ChildSlots[selectedSlotListIndex];
            int selectedSlotId = selected != null ? selected.SlotIndex : selectedSlotListIndex;
            string title = "Confirm Re-Fly";
            string message =
                "Do you want to fly this again? This will take you to the moment after " +
                "separation and you will be in control of the craft / Kerbal.";

            var capturedRp = rp;
            var capturedSlotListIdx = selectedSlotListIndex;
            var capturedSlotId = selectedSlotId;
            var capturedSelected = selected;

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "ParsekRewindInvoke",
                    message,
                    title,
                    HighLogic.UISkin,
                    new DialogGUIButton("Fly", () =>
                    {
                        ParsekLog.Info(UITag,
                            $"Invoked rec={capturedSelected?.OriginChildRecordingId ?? "<none>"} " +
                            $"rp={capturedRp.RewindPointId} slot={capturedSlotId} listIndex={capturedSlotListIdx}");
                        StartInvoke(capturedRp, capturedSelected);
                    }),
                    new DialogGUIButton("Cancel", () =>
                    {
                        ParsekLog.Info(UITag,
                            $"Cancelled rp={capturedRp.RewindPointId} slot={capturedSlotId} listIndex={capturedSlotListIdx}");
                    })
                ),
                false, HighLogic.UISkin);
        }

        /// <summary>
        /// Runs the PRE-LOAD phase synchronously (design §6.3 steps 1-3):
        /// generate session id, capture reconciliation bundle, park state in
        /// <see cref="RewindInvokeContext"/>, copy the RP's quicksave to the
        /// save-root (KSP's <c>LoadGame</c> does not support subdirectory
        /// paths), then trigger <c>GamePersistence.LoadGame</c> +
        /// <c>HighLogic.LoadScene(FLIGHT)</c>.
        /// <para>
        /// The new scenario's <see cref="ParsekScenario.OnLoad"/> drains the
        /// context via <see cref="ConsumePostLoad"/>. Post-load execution does
        /// NOT run here — Unity tears down the coroutine on scene reload.
        /// </para>
        /// </summary>
        internal static void StartInvoke(RewindPoint rp, ChildSlot selected)
        {
            if (rp == null)
            {
                ParsekLog.Error(InvokeTag, "StartInvoke called with null rp");
                ShowUserError("Rewind failed: invalid rewind point");
                return;
            }
            if (selected == null)
            {
                ParsekLog.Error(InvokeTag,
                    $"StartInvoke called with null slot (rp={rp.RewindPointId})");
                ShowUserError("Rewind failed: invalid slot");
                return;
            }
            if (RewindInvokeContext.Pending)
            {
                ParsekLog.Warn(InvokeTag,
                    $"StartInvoke: another invocation already pending (sess={RewindInvokeContext.SessionId}) " +
                    $"— ignoring new request for rp={rp.RewindPointId}");
                ShowUserError("Rewind failed: another invocation is already pending");
                return;
            }

            // Re-run the full precondition gate. The confirmation dialog
            // called `CanInvoke` when it was opened, but preconditions can
            // change between dialog-open and confirm-click (scene transition
            // starts, another session activates, RP gets marked corrupted
            // by load-time sweep, part loader fails on a modded craft, etc.).
            // Without this second check, a stale confirmation can bypass
            // every safety gate and leave the RewindInvokeContext half-set
            // on top of invalid state. Tests cover each individual gate via
            // `CanInvoke`; this call is the integration point.
            if (!CanInvoke(rp, out string canInvokeReason))
            {
                ParsekLog.Warn(InvokeTag,
                    $"StartInvoke: precondition failed after dialog confirm — {canInvokeReason} " +
                    $"(rp={rp.RewindPointId}, slot={selected.SlotIndex})");
                ShowUserError(
                    string.IsNullOrEmpty(canInvokeReason)
                        ? "Rewind failed: precondition check failed"
                        : $"Rewind failed: {canInvokeReason}");
                return;
            }

            string sessionId = "sess_" + Guid.NewGuid().ToString("N");
            ParsekLog.Info(InvokeTag,
                $"StartInvoke: sess={sessionId} rp={rp.RewindPointId} " +
                $"slot={selected.SlotIndex}");

            // Step 1: capture reconciliation bundle (synchronous, no yield).
            ReconciliationBundle bundle;
            try
            {
                bundle = ReconciliationBundle.Capture();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(InvokeTag,
                    $"Invocation failed: bundle capture threw: {ex.Message}");
                ShowUserError($"Rewind failed: bundle capture error ({ex.Message})");
                return;
            }

            // Step 2: copy a slot-scrubbed view of the RP quicksave from
            // saves/<save>/Parsek/RewindPoints/<rpId>.sfs to
            // saves/<save>/Parsek_Rewind_<sessionId>.sfs (root; KSP's LoadGame
            // does not accept subdirectory paths).
            string tempPath = null;
            string tempLoadName = null;
            try
            {
                CopyQuicksaveToSaveRoot(rp, selected, sessionId, out tempPath, out tempLoadName);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(InvokeTag,
                    $"Invocation failed: copy-to-root threw: {ex.Message} rp={rp.RewindPointId}");
                TryDeleteTemp(tempPath);
                ShowUserError($"Rewind failed: could not stage quicksave ({ex.Message})");
                HandleQuicksaveMissing(rp);
                return;
            }
            if (string.IsNullOrEmpty(tempPath) || string.IsNullOrEmpty(tempLoadName))
            {
                // Treated as "cannot stage"; clean up the partial copy and bail.
                TryDeleteTemp(tempPath);
                ShowUserError("Rewind failed: could not stage quicksave");
                return;
            }

            // Step 3: park state for the post-load consumer.
            RewindInvokeContext.Pending = true;
            RewindInvokeContext.SessionId = sessionId;
            RewindInvokeContext.RewindPoint = rp;
            RewindInvokeContext.Selected = selected;
            RewindInvokeContext.RewindPointId = rp.RewindPointId;
            RewindInvokeContext.SelectedSlotIndex = selected.SlotIndex;
            RewindInvokeContext.SelectedOriginChildRecordingId = selected.OriginChildRecordingId;
            RewindInvokeContext.CapturedBundle = bundle;
            RewindInvokeContext.HasCapturedBundle = true;
            RewindInvokeContext.TempQuicksavePath = tempPath;

            // Step 4: trigger the load. Past this point, Unity will destroy the
            // current scenario; only the static context survives to the new
            // scenario's OnLoad, which calls ConsumePostLoad.
            try
            {
                ParsekLog.Info(InvokeTag,
                    $"Loading quicksave: tempPath='{tempPath}' loadName='{tempLoadName}' " +
                    $"saveFolder='{HighLogic.SaveFolder}'");
                Game game = GamePersistence.LoadGame(
                    tempLoadName, HighLogic.SaveFolder, true, false);
                if (game == null)
                {
                    ParsekLog.Error(InvokeTag,
                        $"Invocation failed: load error (GamePersistence.LoadGame returned null) " +
                        $"rp={rp.RewindPointId}");
                    TryDeleteTemp(tempPath);
                    RewindInvokeContext.Clear();
                    HandleQuicksaveMissing(rp);
                    TryRestoreBundle(bundle);
                    ShowUserError("Rewind failed: KSP rejected the quicksave");
                    return;
                }

                // KSP's flight-scene loader needs an explicit
                // `FlightDriver.StartAndFocusVessel(game, idx)` call to
                // populate FlightGlobals.Vessels from the loaded save's
                // flightState. Using `HighLogic.LoadScene(FLIGHT)` directly
                // made KSP fall back to the "launch from VAB auto-saved
                // ship" path — the flight scene loaded a fresh Kerbal X
                // craft from `Ships/VAB/Auto-Saved Ship.craft` instead of
                // the RP quicksave's pre-split state. Evidence from
                // logs/2026-04-25_0123_rewind-still-failing-after-fixes:
                //   Loading ship from file: ...\Ships\VAB\Auto-Saved Ship.craft
                //   [FLIGHT GLOBALS] Switching To Vessel Kerbal X
                //   Strip stripped=[2708531065] selected=none ... leftAlone=12
                // The booster (pid 1097581269 per the RP's PID_SLOT_MAP)
                // was never loaded into FlightGlobals, so Strip couldn't
                // match slot 1.
                //
                // StartAndFocusVessel is the canonical quickload-to-flight
                // call (stock F9 uses it). It assigns HighLogic.CurrentGame,
                // preloads FlightDriver.startupFlightState from the game's
                // flightState, and transitions to FLIGHT with the saved
                // active vessel focused. The deferred post-load Strip then
                // switches focus to the slot's target vessel.
                int activeIdx = game.flightState != null ? game.flightState.activeVesselIdx : 0;
                if (activeIdx < 0)
                    activeIdx = 0;
                ParsekLog.Info(InvokeTag,
                    $"StartAndFocusVessel: activeVesselIdx={activeIdx} " +
                    $"vesselCount={game.flightState?.protoVessels?.Count ?? 0}");
                FlightDriver.StartAndFocusVessel(game, activeIdx);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(InvokeTag,
                    $"Invocation failed: load error: {ex.Message} rp={rp.RewindPointId}");
                TryDeleteTemp(tempPath);
                RewindInvokeContext.Clear();
                HandleQuicksaveMissing(rp);
                TryRestoreBundle(bundle);
                ShowUserError($"Rewind failed: load error ({ex.Message})");
            }
        }

        /// <summary>
        /// Runs the POST-LOAD phase synchronously (design §6.3 step 4, §6.4):
        /// Restore → Strip → Activate → AtomicMarkerWrite. Called by
        /// <see cref="ParsekScenario.OnLoad"/> exactly once per invocation,
        /// after the scene has reloaded and the new scenario module is live.
        /// <para>
        /// Consumes <see cref="RewindInvokeContext"/>; clears it before
        /// returning. Cleans up the root-level temp quicksave copy regardless
        /// of success/failure.
        /// </para>
        /// </summary>
        // Test seams: override FlightGlobals-vessel readiness and the
        // onFlightReady subscription so headless tests can exercise both the
        // direct and the deferred Strip-Activate-Marker paths.
        internal static Func<bool> FlightReadyProbeOverrideForTesting;
        internal static Action<Action, string> DeferUntilFlightReadyOverrideForTesting;

        internal static void ConsumePostLoad()
        {
            if (!RewindInvokeContext.Pending)
                return;

            string sessionId = RewindInvokeContext.SessionId;
            RewindPoint rp = RewindInvokeContext.RewindPoint;
            ChildSlot selected = RewindInvokeContext.Selected;
            ReconciliationBundle bundle = RewindInvokeContext.CapturedBundle;
            bool hasBundle = RewindInvokeContext.HasCapturedBundle;
            string tempPath = RewindInvokeContext.TempQuicksavePath;
            int slotIdx = selected?.SlotIndex ?? -1;

            ParsekLog.Info(InvokeTag,
                $"ConsumePostLoad begin: sess={sessionId} rp={rp?.RewindPointId ?? "<null>"} " +
                $"slot={slotIdx}");

            if (rp == null || selected == null)
            {
                ParsekLog.Error(InvokeTag,
                    "ConsumePostLoad: context missing rp or slot — aborting");
                ShowUserError("Rewind failed: invocation context corrupted");
                TryDeleteTemp(tempPath);
                RewindInvokeContext.Clear();
                return;
            }

            // Step 1: reconcile. Runs now — it only touches scenario state and
            // does not depend on FlightGlobals.Vessels being populated.
            if (hasBundle)
            {
                try
                {
                    ReconciliationBundle.Restore(bundle);
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(InvokeTag,
                        $"Invocation failed: reconciliation restore threw: {ex.Message}");
                    ShowUserError($"Rewind failed: reconcile error ({ex.Message})");
                    TryDeleteTemp(tempPath);
                    RewindInvokeContext.Clear();
                    return;
                }
            }
            else
            {
                ParsekLog.Warn(InvokeTag,
                    "ConsumePostLoad: no captured bundle — skipping reconciliation");
            }

            // Steps 2-5: Strip + Activate + AtomicMarkerWrite + LedgerRecalc.
            // These need FlightGlobals.Vessels populated. During an async
            // SPACECENTER→FLIGHT scene change, ParsekScenario.OnLoad (and
            // thus ConsumePostLoad) fires before KSP has loaded the save's
            // vessels into FlightGlobals — Strip would find zero candidates
            // and the invocation would bail with
            // "Activate failed: selected vessel not present on reload".
            // Defer to GameEvents.onFlightReady (or run now if the scene
            // never unloaded — e.g. synchronous reload tests). The atomic
            // invariant (§2.5: no yield / no await between CheckpointA and
            // CheckpointB) still holds because Strip + Activate +
            // AtomicMarkerWrite still run as one synchronous block when the
            // callback fires.
            if (IsFlightReady())
            {
                RunStripActivateMarker(rp, selected, sessionId, slotIdx, tempPath);
            }
            else
            {
                ParsekLog.Info(InvokeTag,
                    $"ConsumePostLoad deferred to onFlightReady: sess={sessionId} " +
                    $"rp={rp.RewindPointId} slot={slotIdx} " +
                    "(FlightGlobals.Vessels not yet populated after async scene load)");
                // Pass tempPath separately so the timeout branch in
                // `WaitForFlightReadyAndInvoke` can delete the root-level
                // Parsek_Rewind_*.sfs copy without reaching the action's
                // finally block. Otherwise a catastrophic flight-scene load
                // that never fires onFlightReady would leak the temp file
                // and leave RewindInvokeContext half-cleared.
                DeferUntilFlightReady(
                    () => RunStripActivateMarker(rp, selected, sessionId, slotIdx, tempPath),
                    tempPath);
            }
        }

        private static void RunStripActivateMarker(
            RewindPoint rp,
            ChildSlot selected,
            string sessionId,
            int slotIdx,
            string tempPath)
        {
            try
            {
                // Step 2: post-load strip (§6.4 step 4).
                PostLoadStripResult stripResult;
                try
                {
                    stripResult = PostLoadStripper.Strip(
                        rp,
                        slotIdx,
                        stripUnmatchedVessels: true);
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(InvokeTag,
                        $"Invocation failed: strip threw: {ex.Message}");
                    ShowUserError($"Rewind failed: post-load strip error ({ex.Message})");
                    return;
                }

                if (stripResult.SelectedPid == 0u || stripResult.SelectedVessel == null)
                {
                    ParsekLog.Error(InvokeTag,
                        $"Activate failed: selected vessel not present on reload " +
                        $"rp={rp.RewindPointId} slot={slotIdx}");
                    ShowUserError("Rewind failed: selected vessel not present in quicksave");
                    return;
                }

                // Step 3: activate selected child's vessel (§6.4 step 5).
                try
                {
                    FlightGlobals.SetActiveVessel(stripResult.SelectedVessel);
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(InvokeTag,
                        $"Activate failed: SetActiveVessel threw: {ex.Message}");
                    ShowUserError($"Rewind failed: could not activate vessel ({ex.Message})");
                    return;
                }

                // Reconcile committed recordings whose SpawnedVesselPersistentId
                // points at a vessel the strip just removed (#168 analogue for
                // the Re-Fly load path). A prior merge can leave a sibling
                // recording stamped with a persisted-vessel PID that survived
                // into the save; Re-Fly's PostLoadStripper.Strip then deletes
                // that vessel along with every other non-selected sibling, but
                // without this reconcile pass the recording stays
                // VesselSpawned=true and ShouldSpawnAtRecordingEnd's PID dedup
                // gate blocks the engine from re-spawning the ghost at its
                // terminal endpoint.
                //
                // Survivor-set contract: PostLoadStripper.Strip removes vessels
                // via Vessel.Die() but does NOT remove the matching ProtoVessel
                // from HighLogic.CurrentGame.flightState.protoVessels — that
                // list is the save-shape mirror and does not auto-sync with
                // Vessel.Die(). So a survivor set built from protoVessels alone
                // still contains every stripped capsule's PID, which masks the
                // bug ShouldResetSpawnState is supposed to detect. The fix is
                // to subtract stripResult.StrippedPids explicitly here before
                // calling the reconcile helper. Keeping the helper itself
                // unchanged — its other callers (revert path at
                // ParsekScenario.cs:1701, defense-in-depth at :2405) may have
                // different invariants and are out of scope for this PR.
                try
                {
                    var fsReconcile = HighLogic.CurrentGame?.flightState;
                    if (fsReconcile != null)
                    {
                        var committed = RecordingStore.CommittedRecordings;
                        if (committed != null && committed.Count > 0)
                        {
                            var protoVesselPids = new List<uint>();
                            int protoCount = 0;
                            if (fsReconcile.protoVessels != null)
                            {
                                protoCount = fsReconcile.protoVessels.Count;
                                for (int i = 0; i < protoCount; i++)
                                    protoVesselPids.Add(fsReconcile.protoVessels[i].persistentId);
                            }

                            int strippedCount = stripResult.StrippedPids != null
                                ? stripResult.StrippedPids.Count
                                : 0;

                            var survivors = ParsekScenario.ComputeSurvivorsFromProtoVesselPids(
                                protoVesselPids, stripResult.StrippedPids);

                            ParsekLog.Info(InvokeTag,
                                $"Post-strip reconcile: strippedPids={strippedCount} " +
                                $"protoVesselsRemaining={protoCount} survivorPidCount={survivors.Count}");

                            ParsekScenario.ReconcileSpawnStateAfterStrip(survivors, committed);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InvokeTag,
                        $"Post-strip spawn-state reconcile threw (non-fatal): {ex.Message}");
                }

                // Step 4: §6.3 step 4 phases 1 + 2 — atomic provisional + marker write.
                // NO yield, NO await between checkpoints A and B.
                try
                {
                    AtomicMarkerWrite(rp, selected, stripResult, sessionId);
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(InvokeTag,
                        $"Invocation failed: atomic marker write threw: {ex.Message}");
                    ShowUserError($"Rewind failed: marker write error ({ex.Message})");
                    return;
                }

                // Phase 2 (ghost rendering pipeline, design doc §6.3 / §17.2 /
                // §18 Phase 2): rebuild the in-memory anchor ε map from the
                // freshly-written marker. The catch is HR-9 visible-failure —
                // a thrown exception inside the rebuild must not abort the
                // re-fly itself; we already own a valid marker on disk so the
                // session is live regardless of rendering-side anchor state.
                try
                {
                    Parsek.Rendering.RenderSessionState.RebuildFromMarker(
                        ParsekScenario.Instance?.ActiveReFlySessionMarker);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("Pipeline-Session",
                        $"RebuildFromMarker threw (non-fatal): {ex.Message}");
                }

                // Step 5: post-atomic ledger recalc.
                // Pass `double.MaxValue` as the cutoff so every action in
                // the reconciled ledger applies, and — critically — so
                // `bypassPatchDeferral` flips true inside `RecalculateAndPatch`.
                // Without that bypass, `LedgerOrchestrator` skips tech-tree
                // patching when no cutoff is supplied, and the live KSP R&D
                // state stays at the OLD RP quicksave's pre-rewind tech set.
                // Tech unlocks made after the RP would silently disappear,
                // violating the design's "career state sticks" rule (§2.3
                // of the rewind-staging design doc). `double.MaxValue`
                // means "no time filter" — the full ledger walks and every
                // tech unlock re-applies to KSP's R&D after the quicksave's
                // old state overwrote it.
                try
                {
                    LedgerOrchestrator.RecalculateAndPatch(double.MaxValue);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InvokeTag,
                        $"Post-invoke ledger recalculate threw (non-fatal): {ex.Message}");
                }

                ParsekLog.Info(InvokeTag,
                    $"Invocation complete: sess={sessionId} rp={rp.RewindPointId} " +
                    $"slot={slotIdx} activePid={stripResult.SelectedPid}");

                // Bug #587: pre-existing debris-vessel-supplement to PostLoadStripper.Strip.
                // Runs AFTER AtomicMarkerWrite so the marker-aware short-circuit in
                // ParsekPlaybackPolicy.RunSpawnDeathChecks is engaged and our Die()
                // calls cannot leak back into the policy as "spawned vessel died,
                // please re-spawn" (#573 contract). Pure no-op when the marker is
                // not in-place continuation (placeholder pattern keeps the live
                // pre-rewind active vessel in scene; killing matching debris there
                // would risk taking the player's actively-re-flown vessel).
                StripPreExistingDebrisForInPlaceContinuation(stripResult);

                // Bug: KSP's StageManager stack ends up unresponsive after a
                // ProtoVessel.Load when the underlying quicksave was captured
                // mid-decoupling-tick (the case for any Parsek breakup-RP).
                // The saved `vessel.currentStage` ends up referencing a stage
                // slot whose decoupler is gone from the post-decouple part
                // list, so the next [Space] press fires a no-op stage and the
                // auto-advance logic only runs on launchpad initial load.
                // KSPCommunityFixes has no fix for this and stock has none
                // either. Calling Vessel.ResumeStaging() — the same KSP API
                // that ProtoVessel.Load itself invokes during initial load —
                // forces the StageManager to re-discover the stack from the
                // current part hierarchy. Idempotent + harmless when the
                // stack was already correct, so we run it on every Re-Fly
                // load (in-place AND placeholder paths). See open-bug entry
                // "capsule upper-stage staging unresponsive after Re-Fly load".
                ForceStageManagerRebuildAfterReFlyLoad(stripResult, sessionId);

                // Diagnostic hint: a pre-existing quicksave vessel whose name
                // matches a recording in the re-fly tree produces two
                // identical-looking objects in the scene (real orbital relic +
                // playback ghost). The 01:53 playtest hit exactly this with a
                // prior-career "Kerbal X" at 162 km. Warn so the player knows
                // not to blame the re-fly pipeline — the real vessel predates
                // the rewind and is outside the tree.
                WarnOnLeftAloneNameCollisions(stripResult);
            }
            finally
            {
                // Always drop the temp quicksave copy and clear the context,
                // whether we succeeded or failed. A leftover Parsek_Rewind_*.sfs
                // in the save root is user-visible clutter.
                TryDeleteTemp(tempPath);
                RewindInvokeContext.Clear();
            }
        }

        private static bool IsFlightReady()
        {
            if (FlightReadyProbeOverrideForTesting != null)
                return FlightReadyProbeOverrideForTesting();

            try
            {
                if (!FlightGlobals.ready) return false;
                var vessels = FlightGlobals.Vessels;
                return vessels != null && vessels.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void DeferUntilFlightReady(Action action, string tempPath)
        {
            if (DeferUntilFlightReadyOverrideForTesting != null)
            {
                DeferUntilFlightReadyOverrideForTesting(action, tempPath);
                return;
            }

            // Poll `FlightGlobals.ready` via a coroutine on the living
            // `ParsekScenario` MonoBehaviour. Subscribing to
            // `GameEvents.onFlightReady` directly from this static context
            // crashed inside `EventVoid.EvtDelegate..ctor` with a
            // `NullReferenceException` because KSP derefs `delegate.Target`
            // while building the subscription's internal name and a static
            // method has a null Target. A per-frame poll on the scenario's
            // MonoBehaviour sidesteps the EvtDelegate issue entirely and is
            // still deterministic: it fires on the first Unity frame after
            // `FlightGlobals.ready && Vessels.Count > 0` flips true.
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
            {
                ParsekLog.Error(InvokeTag,
                    "DeferUntilFlightReady: no ParsekScenario.Instance — running action inline (best effort)");
                try { action?.Invoke(); }
                catch (Exception ex)
                {
                    ParsekLog.Error(InvokeTag,
                        $"Inline fallback handler threw: {ex.Message}");
                    // The action owns its own finally-block cleanup; if it
                    // threw before reaching that block, we still need to
                    // drop the temp file to meet the ConsumePostLoad cleanup
                    // contract.
                    TryDeleteTemp(tempPath);
                    RewindInvokeContext.Clear();
                }
                return;
            }

            scenario.StartCoroutine(WaitForFlightReadyAndInvoke(action, tempPath));
        }

        private static System.Collections.IEnumerator WaitForFlightReadyAndInvoke(Action action, string tempPath)
        {
            // Bound the wait so a scene-load that never finishes (catastrophic
            // failure) doesn't leak the coroutine. 300 frames at 60 fps is 5 s,
            // well past the ~1.4 s observed async-load completion.
            const int MaxFrames = 300;
            int frame = 0;
            while (frame < MaxFrames && !IsFlightReady())
            {
                frame++;
                yield return null;
            }

            if (!IsFlightReady())
            {
                ParsekLog.Error(InvokeTag,
                    $"Deferred flight-ready wait timed out after {MaxFrames} frames — rewind aborted");
                // The action's own finally block never runs on timeout, so
                // the temp quicksave (Parsek_Rewind_*.sfs at save root) would
                // be orphaned — user-visible clutter and a violation of the
                // ConsumePostLoad cleanup contract. Delete it explicitly
                // before clearing the context.
                TryDeleteTemp(tempPath);
                RewindInvokeContext.Clear();
                yield break;
            }

            ParsekLog.Verbose(InvokeTag,
                $"Deferred flight-ready wait completed after {frame} frame(s) — resuming rewind");
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(InvokeTag,
                    $"Deferred flight-ready handler threw: {ex.Message}");
                // If the action threw before reaching its own finally block
                // (e.g., crashed in Strip before the try/finally opened),
                // the temp quicksave is still ours to clean up.
                TryDeleteTemp(tempPath);
                RewindInvokeContext.Clear();
            }
        }

        /// <summary>
        /// §6.3 step 4 critical section. Runs synchronously; throws MUST
        /// leave the global state untouched (we roll back the provisional
        /// add before rethrowing). NO yield, NO await, NO deferred save.
        ///
        /// <para>
        /// Always creates a fresh provisional Recording (the "fork"). The
        /// recorder's first flush appends data to this fork; the origin
        /// recording is never mutated. Two flavors of fork:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>
        ///     <b>In-place continuation</b> (issue #734) — the Limbo-restore
        ///     kept the origin recording alive in the restored tree and the
        ///     strip-selected vessel pid matches the origin's
        ///     <see cref="Recording.VesselPersistentId"/>. The fork inherits
        ///     the origin's vessel identity and freezes the origin's
        ///     trajectory under the fork's own pre-Re-Fly anchor snapshot
        ///     so resolver paths keyed by
        ///     <see cref="ReFlySessionMarker.ActiveReFlyRecordingId"/> still
        ///     see the original trajectory data without reading the live
        ///     origin recording. <see cref="ReFlySessionMarker.InPlaceContinuation"/>
        ///     is set so <see cref="ReFlySessionMarker.ResolveInPlaceContinuationTarget"/>
        ///     can swap the active recording id during restore.
        ///   </description></item>
        ///   <item><description>
        ///     <b>New-recording path</b> — origin tree is gone or the active
        ///     pid does not match the origin's pid. The fork is a blank
        ///     placeholder that the recorder populates as the player flies.
        ///   </description></item>
        /// </list>
        /// </summary>
        internal static void AtomicMarkerWrite(
            RewindPoint rp, ChildSlot selected,
            PostLoadStripResult stripResult, string sessionId)
        {
            if (rp == null) throw new ArgumentNullException(nameof(rp));
            if (selected == null) throw new ArgumentNullException(nameof(selected));
            if (stripResult.SelectedPid == 0u)
                throw new InvalidOperationException("AtomicMarkerWrite: no selected vessel");

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
                throw new InvalidOperationException("AtomicMarkerWrite: no ParsekScenario instance");

            // Resolve the origin recording. If it survived the Limbo-restore,
            // is still the slot's effective tip, and its VesselPersistentId
            // matches the strip-selected pid, the recorder will continue
            // writing into THIS recording; we point the marker at it directly
            // with no placeholder. If the slot already supersedes origin, a
            // fresh placeholder is required so commit appends priorTip -> new
            // instead of creating origin -> priorTip -> origin cycles.
            //
            // Composite walker (Task A2 rewired ChildSlot.EffectiveRecordingId
            // to use EffectiveState.EffectiveTipRecordingId): traces
            // slot.OriginChildRecordingId → (chain) → (supersede) → final tip.
            // For nested Re-Fly chains the previous fork is reached via
            // chain-hop + supersede-hop in a single traversal — load-bearing
            // for fix-supersede-identity-scope plan §"Edge cases" #4.
            string priorTip = selected.EffectiveRecordingId(scenario.RecordingSupersedes);
            Recording originChild = FindRecordingById(selected.OriginChildRecordingId);
            bool originIsPriorTip = string.Equals(
                priorTip,
                selected.OriginChildRecordingId,
                StringComparison.Ordinal);
            // For a slot that has already been Re-flown but did not auto-seal
            // (e.g. crashed terminal => MergeState.CommittedProvisional), the
            // supersede chain has extended past origin to a prior Re-Fly's
            // recording. Inheriting from origin would lose the latest vessel
            // snapshot and identity that the previous Re-Fly's commit stamped
            // (and would also leave RestoreActiveTreeFromPending's marker-swap
            // path inactive — observed in 2026-05-06_2156 where the recorder
            // never armed and the live vessel stayed as the original full
            // rocket instead of the slot's probe). When priorTip resolves to a
            // valid committed recording with a matching vessel pid, fork from
            // it instead so the in-place path inherits the up-to-date state.
            Recording chainTipRec = originIsPriorTip
                ? originChild
                : FindRecordingById(priorTip);
            Recording inheritFrom = chainTipRec ?? originChild;
            // originChild was looked up via FindRecordingById which scans
            // CommittedRecordings, so a non-null originChild is by construction
            // a committed recording. The previous redundant
            // `IsCommittedRecording(originChild)` clause has been dropped.
            //
            // The third clause guards a degenerate case: priorTip references a
            // recording that is no longer in the committed list (orphan
            // supersede). With chainTipRec=null, inheritFrom falls back to
            // origin, but origin is no longer authoritative for the slot's
            // current state. In that case we keep the placeholder branch
            // (inPlaceContinuation=false) so the marker still routes through
            // the priorTip-aware supersede commit path.
            bool inPlaceContinuation =
                inheritFrom != null
                && inheritFrom.VesselPersistentId == stripResult.SelectedPid
                && (originIsPriorTip || chainTipRec != null);

            Recording provisional = null;
            string activeReFlyRecordingId = null;
            string treeIdForMarker = null;
            bool addedToPendingTree = false;
            RecordingTree pendingTreeForFork = null;
            TryResolveSelectedSlotRootPartPersistentId(
                rp,
                selected.SlotIndex,
                out uint selectedRootPartPersistentId);

            // Bug fix-refly-abandon-and-fork-persist §Bug1: before this
            // session adds its provisional, reap any prior NotCommitted
            // provisional that targets the same RewindPoint from a
            // different (now-abandoned) session. The user just clicked
            // Re-Fly on the same RP slot, so the prior attempt's
            // provisional is orphaned by construction. Without this
            // reap, the orphan survives in tree.Recordings dicts and
            // gets re-added to RecordingStore.CommittedRecordings by
            // FinalizeTreeCommit (RecordingStore.cs:1363-1386), and the
            // next merge's AppendRelations closure walk writes an
            // invalid `old=<NotCommitted-orphan> → new=<new-fork>`
            // supersede row.
            //
            // Hoisted OUTSIDE the try block below so the reap is an
            // independent cleanup step: if BuildProvisionalRecording
            // throws after a successful reap, the orphans stay gone
            // (which is correct — they were already invariant violations)
            // and the catch block does not need to undo the reap. A reap
            // failure propagates unhandled out of AtomicMarkerWrite,
            // which is the desired behavior — a corrupted store should
            // not silently fall through to writing a new marker.
            int reaped = ReapPriorProvisionalsForRp(rp.RewindPointId, sessionId);
            if (reaped > 0)
            {
                ParsekLog.Info(SessionTag,
                    $"AtomicMarkerWrite: reaped {reaped} prior NotCommitted provisional(s) " +
                    $"for rp={rp.RewindPointId} before new sess={sessionId}");
            }

            ReFlySessionMarker marker;
            try
            {
                // BuildProvisionalRecording previously also wrote
                // `SupersedeTargetId = selected.OriginChildRecordingId` as
                // a placeholder; that initial assignment was removed
                // because the marker path needs `priorTip` (which differs
                // from origin when the slot already supersedes origin),
                // and writing both would shadow the meaningful value.
                provisional = BuildProvisionalRecording(rp, selected, originChild, sessionId, stripResult);
                // The merge path consumes marker.SupersedeTargetId. This
                // recording-level copy is a transient diagnostic on the
                // NotCommitted placeholder and is cleared with the rest of
                // the provisional fields at merge time.
                provisional.SupersedeTargetId = priorTip;

                if (inPlaceContinuation)
                {
                    // Issue #734: the in-place attempt is forked into a
                    // separate provisional Recording instead of mutating the
                    // origin object. The fork inherits the inheritance source's
                    // vessel identity (so the recorder's per-vessel tracking
                    // continues unchanged), generation depth, and a defensive
                    // copy of its vessel/ghost snapshots so any code path that
                    // queries the active Re-Fly recording's snapshot before
                    // the recorder has refreshed it (ghost build, antenna
                    // registration) still sees a valid
                    // payload. The fork freezes the inheritance source's
                    // trajectory under the fork's own pre-Re-Fly anchor
                    // snapshot so resolver paths keyed by
                    // ActiveReFlyRecordingId still see the prior trajectory
                    // data. The inheritance source is left
                    // untouched: no live mutation, no session tagging, no
                    // rollback snapshot needed for Discard. Chain identity
                    // (ChainId/Index/Branch) is intentionally NOT copied so
                    // the supersede table is the only authority on chain-tip
                    // resolution, matching the non-in-place placeholder
                    // pattern.
                    //
                    // When the slot has prior CommittedProvisional Re-Fly
                    // recordings (priorTip != origin), the inheritance source
                    // is the chain tip's recording rather than origin so the
                    // new fork picks up the latest committed snapshot/identity
                    // — without that, the second-Re-Fly-of-an-unsealed-slot
                    // path falls through to the placeholder branch, the
                    // recorder never arms (RestoreActiveTreeFromPending's
                    // marker-swap is gated on inPlaceContinuation), and the
                    // live vessel stays as the original full assembly.
                    CopyInheritedIdentityForFork(provisional, inheritFrom);
                    TagForkInitialSegmentPhase(provisional, stripResult.SelectedVessel, sessionId);
                    bool snapshotRefreshedFromLive = TryRefreshForkSnapshotsFromLiveVessel(
                        provisional, stripResult.SelectedVessel, sessionId);
                    provisional.CapturePreReFlyAnchorTrajectoryFrom(inheritFrom, sessionId);
                    pendingTreeForFork = FindTreeForReFlyFork(inheritFrom.TreeId);
                    bool inheritedFromChainTip = !object.ReferenceEquals(inheritFrom, originChild)
                        && inheritFrom != null;
                    string snapshotProvenance = snapshotRefreshedFromLive ? "live-refresh" : "copied";
                    ParsekLog.Info(InvokeTag,
                        $"AtomicMarkerWrite: in-place continuation forked — fork " +
                        $"{provisional.RecordingId} supersedes priorTip {priorTip ?? "<none>"} " +
                        $"(origin={selected.OriginChildRecordingId ?? "<none>"} " +
                        $"inheritedFrom={(inheritedFromChainTip ? "chain-tip" : "origin")} " +
                        $"sourceRec={inheritFrom.RecordingId ?? "<no-id>"}; " +
                        $"source not mutated; pre-Re-Fly anchor snapshot captured on the fork; " +
                        $"vesselSnapshot={(provisional.VesselSnapshot != null ? snapshotProvenance : "<none>")}; " +
                        $"ghostSnapshot={(provisional.GhostVisualSnapshot != null ? snapshotProvenance : "<none>")}; " +
                        $"generation={provisional.Generation}; " +
                        $"treeAttach={(pendingTreeForFork != null ? "eager" : "deferred-to-restore")})");
                }

                activeReFlyRecordingId = provisional.RecordingId;
                treeIdForMarker = provisional.TreeId;

                CheckpointHookForTesting?.Invoke("CheckpointA:BeforeProvisional");
                RecordingStore.AddProvisional(provisional);
                if (pendingTreeForFork != null
                    && EnsureForkAttachedToTree(pendingTreeForFork, provisional, "AtomicMarkerWrite"))
                {
                    addedToPendingTree = true;
                }
                CheckpointHookForTesting?.Invoke("CheckpointA:AfterProvisional");

                marker = new ReFlySessionMarker
                {
                    SessionId = sessionId,
                    TreeId = treeIdForMarker,
                    ActiveReFlyRecordingId = activeReFlyRecordingId,
                    OriginChildRecordingId = selected.OriginChildRecordingId,
                    SupersedeTargetId = priorTip,
                    RewindPointId = rp.RewindPointId,
                    SelectedRootPartPersistentId = selectedRootPartPersistentId,
                    InvokedUT = SafeNow(),
                    // Stable RP cutoff captured directly from the RewindPoint.
                    // Decoupled from SafeNow() / onFlightReady deferrals so
                    // gates that need "before the rewind moment" semantics
                    // (see ReFlySessionMarker.RewindPointUT XML doc) have an
                    // exact reference UT rather than the drifted InvokedUT.
                    RewindPointUT = rp.UT,
                    InvokedRealTime = DateTime.UtcNow.ToString("o"),
                    PreSessionBranchPointIds = SnapshotTreeBranchPointIds(treeIdForMarker),
                    InPlaceContinuation = inPlaceContinuation,
                };

                CheckpointHookForTesting?.Invoke("CheckpointB:BeforeMarker");
                scenario.ActiveReFlySessionMarker = marker;
                scenario.BumpSupersedeStateVersion();
                // Re-enable the Esc-menu Revert button (KSP grays it out
                // because the loaded vessel is mid-flight, not PRELAUNCH;
                // see ReFlyRevertButtonGate doc-comment).
                ReFlyRevertButtonGate.Apply("AtomicMarkerWrite");
                CheckpointHookForTesting?.Invoke("CheckpointB:AfterMarker");
            }
            catch
            {
                // Roll back the provisional and the marker so no half-written
                // pair leaks out of the critical section. Both clears are
                // idempotent (RemoveCommittedInternal returns false if absent;
                // marker clear is a null-assignment). The in-place fork also
                // has to be removed from the pending tree if we attached it
                // before the throw.
                if (provisional != null)
                {
                    RecordingStore.RemoveCommittedInternal(provisional);
                    if (addedToPendingTree && pendingTreeForFork != null)
                        DetachForkFromTreeForRollback(pendingTreeForFork, provisional);
                }
                try
                {
                    if (ParsekScenario.Instance != null)
                        ParsekScenario.Instance.ActiveReFlySessionMarker = null;
                    Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
                    ReFlyRevertButtonGate.Apply("AtomicMarkerWrite:rollback");
                }
                catch { /* idempotent rollback; swallow secondary failure */ }
                throw;
            }

            // Nest the live fork under its tree's existing mission folder so the
            // recordings table renders it inside the group during the active
            // Re-Fly session instead of floating at the table root. Resolve the
            // tree from the eager in-place handle when present, else look it up
            // by the fork's TreeId (covers the placeholder branch that defers
            // tree attach to RestoreActiveTreeFromPending). No-op when the tree
            // has no auto-generated folder. Deliberately OUTSIDE the atomic
            // try/catch above: grouping is cosmetic, not part of the
            // provisional+marker critical section, so a thrown marker write
            // rolls back the provisional without leaking a created debris/crew
            // subgroup mapping.
            RecordingGroupStore.AssignTreeMemberToExistingAutoGroup(
                pendingTreeForFork ?? FindTreeForReFlyFork(provisional.TreeId),
                provisional);

            // The quickload-resume context can be armed before this method runs
            // on async FLIGHT loads. Refresh after the marker exists so recorder
            // resume consumes the Re-Fly active-only trim scope instead of the
            // pre-marker TreeWide fallback.
            try
            {
                ParsekScenario.RefreshPendingQuickloadTrimScope();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(InvokeTag,
                    $"AtomicMarkerWrite: quickload trim-scope refresh threw (non-fatal): {ex.Message}");
            }

            ParsekLog.Info(SessionTag,
                $"Started sess={sessionId} rp={rp.RewindPointId} slot={selected.SlotIndex} " +
                $"provisional={activeReFlyRecordingId} " +
                $"origin={selected.OriginChildRecordingId ?? "<none>"} " +
                $"supersedeTarget={priorTip ?? "<none>"} " +
                $"selectedRootPartPid={selectedRootPartPersistentId} " +
                $"tree={treeIdForMarker ?? "<none>"} " +
                $"inPlaceContinuation={inPlaceContinuation}");
        }

        /// <summary>
        /// Locates the in-memory tree that owns <paramref name="treeId"/> so
        /// the in-place fork (issue #734) can attach itself to the tree the
        /// recorder will eventually flush into. AtomicMarkerWrite usually
        /// runs after <c>TryRestoreActiveTreeNode</c> stashes the saved tree
        /// as pending-Limbo, so <see cref="RecordingStore.PendingTree"/> is
        /// the common target. The lookup additionally falls back to
        /// <see cref="RecordingStore.CommittedTrees"/> for sessions whose
        /// committed copy was never detached, and finally to
        /// <c>ParsekFlight.Instance.ActiveTreeForSerialization</c> for the
        /// async-load race where <c>RestoreActiveTreeFromPending</c> fired
        /// FIRST (popping the pending tree and starting the recorder)
        /// before AtomicMarkerWrite ran - in that window the tree no
        /// longer lives in PendingTree, and TryRestoreActiveTreeNode's
        /// <c>RemoveCommittedTreeById</c> already pulled the prior copy
        /// out of CommittedTrees. Without this fallback the fork would land
        /// only in <see cref="RecordingStore.CommittedRecordings"/> with no
        /// tree home, the recorder's flush would warn-and-stop, and the
        /// reconciliation pass at the top of RestoreActiveTreeFromPending
        /// has already completed and would not re-fire.
        /// </summary>
        internal static RecordingTree FindTreeForReFlyFork(string treeId)
        {
            if (string.IsNullOrEmpty(treeId))
                return null;

            RecordingTree pending = RecordingStore.PendingTree;
            if (pending != null
                && string.Equals(pending.Id, treeId, StringComparison.Ordinal))
            {
                return pending;
            }

            var committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    var committed = committedTrees[i];
                    if (committed == null) continue;
                    if (string.Equals(committed.Id, treeId, StringComparison.Ordinal))
                        return committed;
                }
            }

            // Live activeTree fallback for the AtomicMarkerWrite-late race.
            // ParsekFlight.Instance is null outside flight scenes (and in
            // tests that do not set it up), so guard defensively.
            var live = ParsekFlight.Instance?.ActiveTreeForSerialization;
            if (live != null
                && string.Equals(live.Id, treeId, StringComparison.Ordinal))
            {
                return live;
            }

            return null;
        }

        /// <summary>
        /// Bug fix-refly-abandon-and-fork-persist §Bug1: before a new Re-Fly
        /// session attaches its provisional to the same RewindPoint, reap any
        /// prior NotCommitted provisional from an earlier (abandoned) session
        /// targeting that RP. The retry path's marker clear (in
        /// <c>End reason=retry</c>) does not touch the prior provisional
        /// Recording, and <see cref="LoadTimeSweep.RemoveDiscardRecordings"/>
        /// historically only removed the recording from the flat
        /// <c>committedRecordings</c> list — its node in the owning tree's
        /// <c>Recordings</c> dict survived. <see cref="RecordingStore.FinalizeTreeCommit"/>
        /// (<c>RecordingStore.cs:1363-1386</c>) then re-added it on the next
        /// commit pass, and the closure walk in
        /// <see cref="EffectiveState.EnqueuePidPeerSiblings"/> enqueued the
        /// orphan as a same-PID peer of the new TIP, ultimately writing an
        /// invalid <c>RECORDING_SUPERSEDE_RELATION</c> with
        /// <c>oldRecordingId</c> pointing at a NotCommitted recording — a
        /// data-model invariant violation.
        ///
        /// <para>
        /// This helper walks every collection that can hold a Recording:
        /// the flat <see cref="RecordingStore.CommittedRecordings"/> list,
        /// every committed tree's <c>Recordings</c> dict, the pending tree,
        /// and the live active tree (the s11 evidence case — the zombie was
        /// reachable only from <see cref="ParsekFlight.ActiveTreeForSerialization"/>).
        /// Sidecar files on disk are also removed so a future
        /// <c>CleanOrphanFiles</c> pass does not warn about them.
        /// </para>
        ///
        /// <para>
        /// Idempotent: a second invocation finds no victims and returns 0.
        /// Crash-safe: the reap runs BEFORE the new session's marker is
        /// written, so a mid-reap crash leaves the next OnLoad's
        /// <see cref="LoadTimeSweep"/> to mop up any half-reaped orphans
        /// (their <c>CreatingSessionId</c> still names a session with no
        /// live marker).
        /// </para>
        /// </summary>
        internal static int ReapPriorProvisionalsForRp(string rpId, string newSessionId)
        {
            if (string.IsNullOrEmpty(rpId))
                return 0;

            // 1. Identify victims: NotCommitted, tagged to this RP, from a
            //    different session. Snapshot the ids first (don't mutate
            //    CommittedRecordings during iteration).
            var victimIds = new List<string>();
            var victimRecs = new List<Recording>();
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (rec.MergeState != MergeState.NotCommitted) continue;
                if (!string.Equals(rec.ProvisionalForRpId, rpId, StringComparison.Ordinal)) continue;
                if (string.Equals(rec.CreatingSessionId, newSessionId, StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(rec.RecordingId)) continue;

                victimIds.Add(rec.RecordingId);
                victimRecs.Add(rec);
            }

            if (victimIds.Count == 0)
                return 0;

            // 2. Remove victims from every collection that can hold them.
            //    Track removal counts per collection for the per-victim log so
            //    a future regression can identify which collection retained
            //    the leak.
            int totalReaped = 0;
            for (int v = 0; v < victimIds.Count; v++)
            {
                string id = victimIds[v];
                var rec = victimRecs[v];
                string priorSess = rec.CreatingSessionId ?? "<no-sess>";

                // Flat list.
                bool fromFlatList = RecordingStore.RemoveCommittedById(id);

                // Every committed tree's Recordings dict + BackgroundMap.
                int fromCommittedTrees = 0;
                var committedTrees = RecordingStore.CommittedTrees;
                if (committedTrees != null)
                {
                    for (int t = 0; t < committedTrees.Count; t++)
                    {
                        var tree = committedTrees[t];
                        if (tree?.Recordings != null && tree.Recordings.Remove(id))
                        {
                            tree.RebuildBackgroundMap();
                            fromCommittedTrees++;
                        }
                    }
                }

                // Pending tree.
                bool fromPendingTree = false;
                var pending = RecordingStore.PendingTree;
                if (pending?.Recordings != null && pending.Recordings.Remove(id))
                {
                    pending.RebuildBackgroundMap();
                    fromPendingTree = true;
                }

                // Live active tree (the s11 evidence case).
                bool fromActiveTree = false;
                var active = ParsekFlight.Instance?.ActiveTreeForSerialization;
                if (active?.Recordings != null && active.Recordings.Remove(id))
                {
                    active.RebuildBackgroundMap();
                    fromActiveTree = true;
                }

                // Sidecar files on disk.
                try { RecordingStore.DeleteRecordingFiles(rec); }
                catch (Exception ex)
                {
                    ParsekLog.Warn(SessionTag,
                        $"ReapPriorProvisional: DeleteRecordingFiles threw for rec={id} " +
                        $"priorSess={priorSess} newSess={newSessionId} rp={rpId}: " +
                        $"{ex.GetType().Name}: {ex.Message} " +
                        "(in-memory removal succeeded; sidecar files may leak on disk)");
                }

                ParsekLog.Info(SessionTag,
                    $"ReapPriorProvisional: removed orphan rec={id} priorSess={priorSess} " +
                    $"newSess={newSessionId} rp={rpId} " +
                    $"removedFromFlatList={fromFlatList} " +
                    $"removedFromCommittedTrees={fromCommittedTrees.ToString(CultureInfo.InvariantCulture)} " +
                    $"removedFromPendingTree={fromPendingTree} " +
                    $"removedFromActiveTree={fromActiveTree}");
                totalReaped++;
            }

            // 3. Bump the supersede-state version so ERS / ELS caches
            //    invalidate. RecordingStore.RemoveCommittedById has already
            //    bumped RecordingStore.StateVersion per victim; this bump
            //    is the scenario-level supersede version which is a
            //    separate signal that the closure walk depends on.
            //    Use ReferenceEquals to skip Unity's null-check override (so a
            //    test fixture scenario without a Unity lifecycle still works).
            if (totalReaped > 0)
            {
                var scenario = ParsekScenario.Instance;
                if (!object.ReferenceEquals(null, scenario))
                    scenario.BumpSupersedeStateVersion();
            }

            return totalReaped;
        }

        /// <summary>
        /// Idempotently inserts <paramref name="fork"/> into
        /// <paramref name="tree"/>'s <c>Recordings</c> dictionary and rebuilds
        /// the background map so the fork's pid does not double-list as both
        /// active recorder target and background entry. Returns true when the
        /// dictionary changed (fork was missing); false on no-op (already
        /// attached, or null inputs). The rebuild is skipped on a no-op so
        /// the restore-coroutine reconciliation path stays cheap on re-entry.
        /// Callers must only attach forks once <paramref name="tree"/>'s
        /// background-map shape is final for the marker write phase.
        /// </summary>
        internal static bool EnsureForkAttachedToTree(
            RecordingTree tree, Recording fork, string callSite,
            bool skipBackgroundMapRebuild = false)
        {
            if (tree == null || fork == null
                || string.IsNullOrEmpty(fork.RecordingId)
                || tree.Recordings == null)
            {
                return false;
            }
            bool overwroteStaleInstance = false;
            if (tree.Recordings.TryGetValue(fork.RecordingId, out var existing))
            {
                if (ReferenceEquals(existing, fork))
                    return false;
                // Expected for F5/F9 mid-Re-Fly: TryRestoreActiveTreeNode
                // deserialised a fresh tree-side Recording for the fork id,
                // but the committed list still holds the pre-load fork
                // object that the recorder is appending into. Convergence
                // is necessary so commit/discard see the populated
                // committed-list instance, not the empty deserialised
                // shadow. Log Info so the convergence is auditable but
                // doesn't signal corruption.
                overwroteStaleInstance = true;
                ParsekLog.Info(InvokeTag,
                    $"{callSite ?? "EnsureForkAttachedToTree"}: tree.Recordings[{fork.RecordingId}] " +
                    $"held a different Recording instance than the committed-list fork; " +
                    $"converging the tree slot to the committed instance so recorder flush, " +
                    $"merge, and discard agree on the same object");
            }
            tree.Recordings[fork.RecordingId] = fork;
            if (!skipBackgroundMapRebuild)
                tree.RebuildBackgroundMap();
            ParsekLog.Verbose(InvokeTag,
                $"{callSite ?? "EnsureForkAttachedToTree"}: attached in-place fork rec={fork.RecordingId} " +
                $"to tree '{tree.TreeName ?? "<unnamed>"}' (id={tree.Id ?? "<no-id>"})" +
                (overwroteStaleInstance ? " (replaced stale instance)" : "") +
                (skipBackgroundMapRebuild ? " (caller will rebuild background map)" : ""));
            return true;
        }

        private static void DetachForkFromTreeForRollback(RecordingTree tree, Recording fork)
        {
            if (tree == null || fork == null) return;
            if (tree.Recordings == null)
                return;
            if (tree.Recordings.Remove(fork.RecordingId))
            {
                tree.RebuildBackgroundMap();
                ParsekLog.Verbose(InvokeTag,
                    $"AtomicMarkerWrite rollback: detached in-place fork rec={fork.RecordingId} " +
                    $"from tree '{tree.TreeName ?? "<unnamed>"}' (id={tree.Id ?? "<no-id>"})");
            }
        }

        internal static Recording BuildProvisionalRecording(
            RewindPoint rp, ChildSlot selected, Recording originChild,
            string sessionId, PostLoadStripResult stripResult)
        {
            // SupersedeTargetId is intentionally NOT initialised here;
            // AtomicMarkerWrite assigns it to `priorTip` (the slot's
            // current effective tip) immediately after this call. Writing
            // it here as `selected.OriginChildRecordingId` was dead code
            // because the next statement at the call site overwrites it.
            var rec = new Recording
            {
                RecordingId = "rec_" + Guid.NewGuid().ToString("N"),
                MergeState = MergeState.NotCommitted,
                CreatingSessionId = sessionId,
                ProvisionalForRpId = rp.RewindPointId,
                ParentBranchPointId = originChild?.ParentBranchPointId ?? rp.BranchPointId,
                TreeId = originChild?.TreeId,
                VesselPersistentId = stripResult.SelectedPid,
                VesselName = stripResult.SelectedVessel != null
                    ? stripResult.SelectedVessel.vesselName
                    : (originChild?.VesselName ?? "Re-Fly"),
                PlaybackEnabled = false,
            };
            return rec;
        }

        private static Recording FindRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            // NOTE: raw CommittedRecordings read — this is the only code path
            // that can locate the origin recording by id at invoke time, since
            // the Phase 1-5 types carry the recording id but not a pre-resolved
            // reference. Allowlisted per [ERS-exempt — Phase 6] file-level note.
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var r = committed[i];
                if (r == null) continue;
                if (string.Equals(r.RecordingId, recordingId, StringComparison.Ordinal))
                    return r;
            }
            return null;
        }

        private static double SafeNow()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch { return 0.0; }
        }

        /// <summary>
        /// Snapshots the existing <see cref="BranchPoint.Id"/>s in the
        /// in-memory tree referenced by <paramref name="treeId"/>. The
        /// returned list is consumed by
        /// <see cref="SupersedeCommit.HasReFlySessionStructuralMutation"/>
        /// (auto-seal gate),
        /// <see cref="MergeDialog.PruneAttemptRecordingsFromCommittedTrees"/>
        /// (drops session-authored branch points on Discard), and
        /// <see cref="MergeDialog.AddSessionBranchPointDescendantAttemptIds"/>
        /// (collects attempt-authored debris children) to distinguish
        /// branch points authored DURING this Re-Fly from pre-existing
        /// ones. Returns an empty list (not null) when the tree is
        /// found with no branch points; returns null only when the tree
        /// id is empty or no in-memory tree matches.
        ///
        /// Lookup order matches <see cref="FindTreeForReFlyFork"/>:
        /// PendingTree first (the normal Re-Fly load shape after
        /// <c>TryRestoreActiveTreeNode</c> stashes the loaded tree as
        /// pending and `RemoveCommittedTreeById` drops the prior
        /// committed copy), then CommittedTrees, then the live
        /// <c>ParsekFlight.Instance.ActiveTreeForSerialization</c> (for
        /// the AtomicMarkerWrite-after-RestoreActiveTreeFromPending race).
        /// Restricting the lookup to CommittedTrees - as a prior
        /// implementation did - returned null on the normal pending-tree
        /// path and silently disabled structural-mutation auto-seal
        /// plus all session-BP-baseline-aware Discard cleanup.
        /// </summary>
        internal static List<string> SnapshotTreeBranchPointIds(string treeId)
        {
            RecordingTree tree = FindTreeForReFlyFork(treeId);
            if (tree == null || tree.BranchPoints == null)
                return null;

            var snapshot = new List<string>(tree.BranchPoints.Count);
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null) continue;
                if (string.IsNullOrEmpty(bp.Id)) continue;
                snapshot.Add(bp.Id);
            }
            return snapshot;
        }

        internal static string ResolveAbsoluteQuicksavePath(RewindPoint rp)
        {
            if (rp == null || string.IsNullOrEmpty(rp.QuicksaveFilename))
                return null;
            if (ResolveAbsoluteQuicksavePathOverrideForTesting != null)
                return ResolveAbsoluteQuicksavePathOverrideForTesting(rp);
            return RecordingPaths.ResolveSaveScopedPath(rp.QuicksaveFilename);
        }

        /// <summary>
        /// Copies the RP's quicksave from
        /// <c>saves/&lt;save&gt;/Parsek/RewindPoints/&lt;rpId&gt;.sfs</c> to
        /// <c>saves/&lt;save&gt;/Parsek_Rewind_&lt;sessionId&gt;.sfs</c> (save
        /// root), because KSP's <c>GamePersistence.LoadGame(fileName, folder, ...)</c>
        /// does not accept subdirectory paths.
        /// <para>
        /// Returns the absolute temp path (for post-load deletion) and the
        /// base filename without the <c>.sfs</c> extension (to pass to
        /// <c>LoadGame</c>). Throws on I/O failure.
        /// </para>
        /// </summary>
        internal static void CopyQuicksaveToSaveRoot(
            RewindPoint rp, string sessionId,
            out string tempAbsolutePath, out string tempLoadName)
        {
            tempAbsolutePath = null;
            tempLoadName = null;

            string sourceAbs = ResolveAbsoluteQuicksavePath(rp);
            if (string.IsNullOrEmpty(sourceAbs) || !File.Exists(sourceAbs))
            {
                ParsekLog.Error(InvokeTag,
                    $"CopyQuicksaveToSaveRoot: source missing " +
                    $"rp={rp?.RewindPointId} source='{sourceAbs ?? "<null>"}'");
                return;
            }

            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
            {
                ParsekLog.Error(InvokeTag,
                    $"CopyQuicksaveToSaveRoot: missing KSP paths " +
                    $"rootSet={!string.IsNullOrEmpty(root)} saveSet={!string.IsNullOrEmpty(saveFolder)}");
                return;
            }

            string saveRoot = Path.Combine(root, "saves", saveFolder);
            string tempName = "Parsek_Rewind_" + sessionId;
            string destAbs = Path.Combine(saveRoot, tempName + ".sfs");

            File.Copy(sourceAbs, destAbs, overwrite: true);
            tempAbsolutePath = destAbs;
            tempLoadName = tempName;
            ParsekLog.Verbose(InvokeTag,
                $"CopyQuicksaveToSaveRoot: '{sourceAbs}' -> '{destAbs}' loadName='{tempName}'");
        }

        /// <summary>
        /// Production Re-Fly staging path: copy the RP quicksave to the save root,
        /// then scrub that temp copy down to the selected slot before KSP loads it.
        /// The RP source save and persistent save are not modified.
        /// </summary>
        internal static void CopyQuicksaveToSaveRoot(
            RewindPoint rp, ChildSlot selected, string sessionId,
            out string tempAbsolutePath, out string tempLoadName)
        {
            CopyQuicksaveToSaveRoot(rp, sessionId, out tempAbsolutePath, out tempLoadName);
            if (!string.IsNullOrEmpty(tempAbsolutePath) && selected != null)
            {
                RequireSelectedSlotScrubApplied(tempAbsolutePath, rp, selected.SlotIndex);
            }
        }

        internal static ReFlySaveScrubResult RequireSelectedSlotScrubApplied(
            string tempAbsolutePath, RewindPoint rp, int selectedSlotIndex)
        {
            ReFlySaveScrubResult scrubResult = ScrubQuicksaveToSelectedSlotForReFly(
                tempAbsolutePath, rp, selectedSlotIndex);
            if (!scrubResult.Applied)
            {
                throw new InvalidOperationException(
                    "Re-Fly temp save scrub failed; refusing to load unscrubbed quicksave");
            }
            return scrubResult;
        }

        internal struct ReFlySaveScrubResult
        {
            public bool Applied;
            public int VesselCountBefore;
            public int VesselsKept;
            public int VesselsRemoved;
            public int SelectedActiveIndex;
            public int ThrottleResets;
            public int SidecarEpochsRefreshed;
            public int SidecarEpochRefreshSkipped;
        }

        /// <summary>
        /// Rewrites the temp re-fly save so KSP only instantiates the selected
        /// slot's real vessel. This is intentionally scoped to the root-level
        /// temp copy: persistent.sfs and the RP source save are not modified.
        /// </summary>
        internal static ReFlySaveScrubResult ScrubQuicksaveToSelectedSlotForReFly(
            string sfsPath, RewindPoint rp, int selectedSlotIndex)
        {
            var result = new ReFlySaveScrubResult { SelectedActiveIndex = -1 };
            if (string.IsNullOrEmpty(sfsPath) || rp == null || selectedSlotIndex < 0)
                return result;

            ConfigNode root = ConfigNode.Load(sfsPath);
            if (root == null)
            {
                ParsekLog.Warn(InvokeTag,
                    $"Re-Fly save scrub skipped: ConfigNode.Load returned null path='{sfsPath}'");
                return result;
            }

            ConfigNode gameNode = root.HasNode("GAME") ? root.GetNode("GAME") : root;
            ConfigNode flightState = gameNode?.GetNode("FLIGHTSTATE");
            if (flightState == null)
            {
                ParsekLog.Warn(InvokeTag,
                    $"Re-Fly save scrub skipped: no FLIGHTSTATE path='{sfsPath}'");
                return result;
            }

            var selectedVesselPids = BuildSelectedSlotPidSet(rp.PidSlotMap, selectedSlotIndex);
            var selectedRootPartPids = BuildSelectedSlotPidSet(rp.RootPartPidMap, selectedSlotIndex);
            if (selectedVesselPids.Count == 0 && selectedRootPartPids.Count == 0)
            {
                ParsekLog.Warn(InvokeTag,
                    $"Re-Fly save scrub skipped: selected slot has no pid mapping " +
                    $"rp={rp.RewindPointId} slot={selectedSlotIndex}");
                return result;
            }

            ConfigNode[] vesselNodes = flightState.GetNodes("VESSEL");
            result.VesselCountBefore = vesselNodes.Length;
            var remove = new List<ConfigNode>();
            int keptIndex = 0;
            for (int i = 0; i < vesselNodes.Length; i++)
            {
                ConfigNode vessel = vesselNodes[i];
                uint vesselPid = ParseUInt(vessel.GetValue("persistentId"));
                uint rootPartPid = GetRootPartPersistentId(vessel);
                bool keep = (vesselPid != 0u && selectedVesselPids.Contains(vesselPid))
                    || (rootPartPid != 0u && selectedRootPartPids.Contains(rootPartPid));

                if (keep)
                {
                    result.ThrottleResets += ForceReFlyVesselThrottleClosed(vessel);
                    if (result.SelectedActiveIndex < 0)
                        result.SelectedActiveIndex = keptIndex;
                    keptIndex++;
                    result.VesselsKept++;
                }
                else
                {
                    remove.Add(vessel);
                }
            }

            if (result.VesselsKept == 0)
            {
                ParsekLog.Warn(InvokeTag,
                    $"Re-Fly save scrub skipped: selected slot vessel not found " +
                    $"rp={rp.RewindPointId} slot={selectedSlotIndex} vessels={vesselNodes.Length}");
                return result;
            }

            for (int i = 0; i < remove.Count; i++)
                flightState.RemoveNode(remove[i]);

            result.VesselsRemoved = remove.Count;
            SetOrAddValue(flightState, "activeVessel",
                Math.Max(0, result.SelectedActiveIndex).ToString(CultureInfo.InvariantCulture));
            int sidecarSkipped;
            result.SidecarEpochsRefreshed =
                RefreshRecordingSidecarEpochsForTempSave(root, sfsPath, out sidecarSkipped);
            result.SidecarEpochRefreshSkipped = sidecarSkipped;
            root.Save(sfsPath);
            result.Applied = true;

            ParsekLog.Info(InvokeTag,
                $"Re-Fly save scrub applied: rp={rp.RewindPointId} slot={selectedSlotIndex} " +
                $"vesselsBefore={result.VesselCountBefore} kept={result.VesselsKept} " +
                $"removed={result.VesselsRemoved} activeVessel={result.SelectedActiveIndex} " +
                $"throttleResets={result.ThrottleResets} " +
                $"sidecarEpochsRefreshed={result.SidecarEpochsRefreshed} " +
                $"sidecarEpochRefreshSkipped={result.SidecarEpochRefreshSkipped} " +
                $"path='{sfsPath}'");
            return result;
        }

        internal static int RefreshRecordingSidecarEpochsForTempSave(
            ConfigNode root, string sfsPath, out int skipped)
        {
            skipped = 0;
            if (root == null || string.IsNullOrEmpty(sfsPath))
                return 0;

            string saveRoot = Path.GetDirectoryName(sfsPath);
            if (string.IsNullOrEmpty(saveRoot))
                return 0;

            int refreshed = 0;
            RefreshRecordingSidecarEpochsInNode(root, saveRoot, ref refreshed, ref skipped);
            return refreshed;
        }

        private static void RefreshRecordingSidecarEpochsInNode(
            ConfigNode node, string saveRoot, ref int refreshed, ref int skipped)
        {
            if (node == null) return;

            if (string.Equals(node.name, "RECORDING", StringComparison.Ordinal))
            {
                RefreshSingleRecordingSidecarEpoch(node, saveRoot, ref refreshed, ref skipped);
            }

            foreach (ConfigNode child in node.GetNodes())
            {
                RefreshRecordingSidecarEpochsInNode(child, saveRoot, ref refreshed, ref skipped);
            }
        }

        private static void RefreshSingleRecordingSidecarEpoch(
            ConfigNode recordingNode, string saveRoot, ref int refreshed, ref int skipped)
        {
            string recordingId = recordingNode.GetValue("recordingId");
            if (!RecordingPaths.ValidateRecordingId(recordingId))
            {
                skipped++;
                return;
            }

            string precPath = Path.Combine(saveRoot, RecordingPaths.BuildTrajectoryRelativePath(recordingId));
            TrajectorySidecarProbe probe;
            if (!RecordingStore.TryProbeTrajectorySidecar(precPath, out probe) || !probe.Supported)
            {
                skipped++;
                return;
            }

            if (!string.IsNullOrEmpty(probe.RecordingId)
                && !string.Equals(probe.RecordingId, recordingId, StringComparison.Ordinal))
            {
                skipped++;
                ParsekLog.Warn(InvokeTag,
                    $"Re-Fly sidecar epoch refresh skipped: recording id mismatch node={recordingId} " +
                    $"sidecar={probe.RecordingId} path='{precPath}'");
                return;
            }

            string oldValue = recordingNode.GetValue("sidecarEpoch");
            int currentEpoch;
            bool hasCurrentEpoch = int.TryParse(oldValue, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out currentEpoch);
            if (hasCurrentEpoch && probe.SidecarEpoch < currentEpoch)
            {
                skipped++;
                ParsekLog.Warn(InvokeTag,
                    $"Re-Fly sidecar epoch refresh skipped: sidecar older than temp save " +
                    $"rec={recordingId} sfs={currentEpoch} sidecar={probe.SidecarEpoch} path='{precPath}'");
                return;
            }

            int targetEpoch = hasCurrentEpoch
                ? Math.Max(currentEpoch, probe.SidecarEpoch)
                : probe.SidecarEpoch;
            string newValue = targetEpoch.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                SetOrAddValue(recordingNode, "sidecarEpoch", newValue);
                refreshed++;
                ParsekLog.Verbose(InvokeTag,
                    $"Re-Fly sidecar epoch refreshed: rec={recordingId} old={oldValue ?? "<missing>"} " +
                    $"new={newValue} path='{precPath}'");
            }
        }

        private static HashSet<uint> BuildSelectedSlotPidSet(
            Dictionary<uint, int> map, int selectedSlotIndex)
        {
            var result = new HashSet<uint>();
            if (map == null) return result;
            foreach (var kv in map)
            {
                if (kv.Key != 0u && kv.Value == selectedSlotIndex)
                    result.Add(kv.Key);
            }
            return result;
        }

        internal static bool TryResolveSelectedSlotRootPartPersistentId(
            RewindPoint rp, int selectedSlotIndex, out uint rootPartPersistentId)
        {
            rootPartPersistentId = 0u;
            Dictionary<uint, int> map = rp?.RootPartPidMap;
            if (map == null) return false;
            foreach (var kv in map)
            {
                if (kv.Key == 0u || kv.Value != selectedSlotIndex)
                    continue;

                rootPartPersistentId = kv.Key;
                return true;
            }
            return false;
        }

        private static uint GetRootPartPersistentId(ConfigNode vessel)
        {
            if (vessel == null) return 0u;
            int rootIndex;
            if (!int.TryParse(vessel.GetValue("root"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out rootIndex))
            {
                rootIndex = 0;
            }

            ConfigNode[] parts = vessel.GetNodes("PART");
            if (parts == null || parts.Length == 0) return 0u;
            if (rootIndex < 0 || rootIndex >= parts.Length) rootIndex = 0;
            return ParseUInt(parts[rootIndex].GetValue("persistentId"));
        }

        private static uint ParseUInt(string value)
        {
            uint parsed;
            return uint.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : 0u;
        }

        private static int ForceReFlyVesselThrottleClosed(ConfigNode vessel)
        {
            if (vessel == null) return 0;

            int resetCount = 0;
            // The temp save may contain uncontrolled debris with no CTRLSTATE;
            // adding only throttle fields is harmless and prevents inherited player input.
            ConfigNode ctrlState = vessel.GetNode("CTRLSTATE") ?? vessel.AddNode("CTRLSTATE");
            if (SetOrAddValueAndReportChange(ctrlState, "mainThrottle", "0"))
                resetCount++;
            if (SetOrAddValueAndReportChange(ctrlState, "wheelThrottle", "0"))
                resetCount++;

            // Engine module fields are normalized only when present. Module-less
            // debris has no engine throttle state to close.
            ConfigNode[] parts = vessel.GetNodes("PART");
            for (int partIndex = 0; partIndex < parts.Length; partIndex++)
            {
                ConfigNode[] modules = parts[partIndex].GetNodes("MODULE");
                for (int moduleIndex = 0; moduleIndex < modules.Length; moduleIndex++)
                {
                    ConfigNode module = modules[moduleIndex];
                    string moduleName = module.GetValue("name");
                    if (!IsEngineModuleName(moduleName))
                        continue;

                    if (SetExistingValueAndReportChange(module, "currentThrottle", "0"))
                        resetCount++;
                    if (SetExistingValueAndReportChange(module, "independentThrottlePercentage", "0"))
                        resetCount++;
                }
            }

            return resetCount;
        }

        private static bool IsEngineModuleName(string moduleName)
        {
            return string.Equals(moduleName, "ModuleEngines", StringComparison.Ordinal)
                || string.Equals(moduleName, "ModuleEnginesFX", StringComparison.Ordinal);
        }

        private static bool SetOrAddValueAndReportChange(ConfigNode node, string name, string value)
        {
            if (node == null || string.IsNullOrEmpty(name)) return false;
            string oldValue = node.GetValue(name);
            SetOrAddValue(node, name, value);
            return !string.Equals(oldValue, value, StringComparison.Ordinal);
        }

        private static bool SetExistingValueAndReportChange(ConfigNode node, string name, string value)
        {
            if (node == null || string.IsNullOrEmpty(name) || !node.HasValue(name)) return false;
            string oldValue = node.GetValue(name);
            node.SetValue(name, value);
            return !string.Equals(oldValue, value, StringComparison.Ordinal);
        }

        private static void SetOrAddValue(ConfigNode node, string name, string value)
        {
            if (node == null || string.IsNullOrEmpty(name)) return;
            if (node.HasValue(name))
                node.SetValue(name, value);
            else
                node.AddValue(name, value);
        }

        private static void TryDeleteTemp(string tempPath)
        {
            if (string.IsNullOrEmpty(tempPath)) return;
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                    ParsekLog.Verbose(InvokeTag, $"Deleted temp quicksave '{tempPath}'");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(InvokeTag,
                    $"Failed to delete temp quicksave '{tempPath}': {ex.Message}");
            }
        }

        /// <summary>§7.22: scenes where Rewind invocation is allowed.</summary>
        private static bool IsInvokableScene(GameScenes scene)
        {
            return scene == GameScenes.FLIGHT
                || scene == GameScenes.SPACECENTER
                || scene == GameScenes.TRACKSTATION;
        }

        /// <summary>
        /// After a successful Strip, cross-reference <see cref="PostLoadStripResult.LeftAlonePidNames"/>
        /// against the committed-recording vessel names in the scenario's
        /// trees. A match means the player has a pre-existing quicksave
        /// vessel sharing a name with a recording in the active tree — they
        /// will see both in-scene and almost always mistake the real vessel
        /// for a second ghost. WARN-log + ScreenMessage so the situation is
        /// diagnosable without reading KSP.log.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Three playtest-driven bug fixes are layered into this helper:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <see cref="PostLoadStripper.FindTreeNameCollisions"/> dedupes by
        /// name, so 3 actual <c>Kerbal X Debris</c> instances were summarized
        /// as <c>Strip left 1 pre-existing vessel(s)</c> — wrong instance
        /// count. Fixed by reporting vessel-instance and unique-name counts
        /// separately.
        /// </description></item>
        /// <item><description>
        /// <see cref="StripPreExistingDebrisForInPlaceContinuation"/> runs
        /// before this WARN and may have killed every colliding vessel via
        /// <see cref="Vessel.Die"/>; the WARN still emitted because it read
        /// the original pre-kill <c>LeftAloneNames</c> list, falsely
        /// reporting collisions that no longer exist. Fixed by re-checking
        /// pid liveness against <see cref="FlightGlobals.Vessels"/> before
        /// counting.
        /// </description></item>
        /// <item><description>
        /// PR #577 P2 review: a name-only re-survey of <c>FlightGlobals.Vessels</c>
        /// counts EVERY live vessel whose name lands in the colliding set —
        /// including the actively re-flown vessel (<c>SelectedPid</c>),
        /// any vessel the strip just killed but whose death event hasn't
        /// drained from the list, and ghost ProtoVessels created by
        /// <see cref="GhostMapPresence"/>. Any of those producing a name
        /// collision would re-introduce the misleading WARN this very fix
        /// is trying to suppress. Fixed by scoping the resurvey to the
        /// (pid, name) pairs the stripper actually left alone, and
        /// belt-and-suspenders excluding <c>SelectedPid</c>,
        /// <c>StrippedPids</c>, and any pid that
        /// <see cref="GhostMapPresence.IsGhostMapVessel(uint)"/> reports.
        /// </description></item>
        /// </list>
        /// </remarks>
        /// <summary>
        /// Workaround for a stock-KSP bug where loading a vessel from a
        /// quicksave captured DURING a decoupling event leaves the
        /// <c>StageManager</c> stack in a state where the next <c>[Space]</c>
        /// press fires a no-op. <c>ProtoVessel.Load</c> already calls
        /// <see cref="Vessel.ResumeStaging"/> on initial load, but the
        /// `currentStage` value saved to disk references a slot whose
        /// decoupler is already gone from the post-decouple part list, so
        /// the rebuilt stack ends up empty for the player's "next" stage.
        /// Calling <c>ResumeStaging</c> again here re-runs the discovery
        /// against the now-stable post-load part hierarchy and is
        /// idempotent when the stack was already correct.
        ///
        /// <para>Observed in <c>logs/2026-05-06_2308_staging-broken-after-first-flight</c>:
        /// the upper-stage Re-Fly recorded 18.4 s of flight with zero engine
        /// or decoupler events because every <c>[Space]</c> press hit a no-op
        /// stage. KSPCommunityFixes has no fix for this; web search returned
        /// no upstream report. Tracked as the open todo entry "capsule
        /// upper-stage staging unresponsive after Re-Fly load".</para>
        /// </summary>
        private static void ForceStageManagerRebuildAfterReFlyLoad(
            PostLoadStripResult stripResult, string sessionId)
        {
            Vessel vessel = null;
            try
            {
                vessel = stripResult.SelectedVessel ?? FlightGlobals.ActiveVessel;
            }
            catch
            {
                vessel = null;
            }
            if (vessel == null)
            {
                ParsekLog.Verbose(InvokeTag,
                    $"ForceStageManagerRebuildAfterReFlyLoad: no live vessel — skipping " +
                    $"sess={sessionId ?? "<no-id>"}");
                return;
            }

            try
            {
                int priorStage = vessel.currentStage;
                vessel.ResumeStaging();
                ParsekLog.Info(InvokeTag,
                    $"ForceStageManagerRebuildAfterReFlyLoad: vessel.ResumeStaging() invoked " +
                    $"vesselPid={vessel.persistentId} priorCurrentStage={priorStage} " +
                    $"postCurrentStage={vessel.currentStage} sess={sessionId ?? "<no-id>"} " +
                    "(workaround for stock KSP staging-after-mid-decouple-quicksave bug)");
            }
            catch (Exception ex)
            {
                // Non-fatal: ResumeStaging is the workaround, not the
                // primary mechanism. If KSP throws here we still want the
                // Re-Fly to proceed; the player can re-stage manually.
                ParsekLog.Warn(InvokeTag,
                    $"ForceStageManagerRebuildAfterReFlyLoad: vessel.ResumeStaging() threw " +
                    $"{ex.GetType().Name}: {ex.Message} — continuing without rebuild " +
                    $"sess={sessionId ?? "<no-id>"}");
            }
        }

        internal static void WarnOnLeftAloneNameCollisions(PostLoadStripResult stripResult)
        {
            if (stripResult.LeftAlonePidNames == null || stripResult.LeftAlonePidNames.Count == 0)
                return;

            // Project to names for the tree-collision intersection. This is
            // still a name-keyed match against committed recordings — the
            // pid scope only constrains the live-survey step downstream.
            IEnumerable<string> treeNames = EnumerateCommittedVesselNames();
            var collisions = PostLoadStripper.FindTreeNameCollisions(
                ProjectLeftAloneNames(stripResult.LeftAlonePidNames), treeNames);
            if (collisions == null || collisions.Count == 0)
                return;

            // PR #577 P2 review: do NOT walk the full live FlightGlobals list
            // when counting survivors. Instead, take the (pid, name) pairs
            // the stripper deliberately left alone (the only set that can
            // legitimately produce a "Strip left N" survivor) and
            // belt-and-suspenders strip out the active re-fly vessel,
            // freshly-stripped pids, and any GhostMap ProtoVessel pid.
            var collisionSet = new HashSet<string>(collisions, StringComparer.Ordinal);
            HashSet<uint> liveVesselPids = SnapshotLiveVesselPids();
            var survey = SurveyLiveLeftAloneCollisions(
                stripResult.LeftAlonePidNames,
                collisionSet,
                liveVesselPids,
                stripResult.SelectedPid,
                stripResult.StrippedPids,
                static pid => GhostMapPresence.IsGhostMapVessel(pid));
            EmitStripLeftAloneWarn(collisions, survey);
        }

        /// <summary>
        /// Pure helper: emit the strip-left-alone diagnostic and the matching
        /// player toast based on a live-vessel survey result. Split out so
        /// unit tests can exercise the message format without
        /// <see cref="FlightGlobals"/> wiring.
        /// </summary>
        internal static void EmitStripLeftAloneWarn(
            List<string> collidingNames,
            LeftAloneSurveyResult survey)
        {
            if (collidingNames == null || collidingNames.Count == 0)
                return;
            int liveVesselCount = survey.LiveCollidingVesselCount;
            if (liveVesselCount <= 0)
            {
                // Post-supplement kill drained every match (or every
                // surviving leftAlone pid was excluded as the active re-fly
                // vessel / freshly stripped / a ghost ProtoVessel). Verbose
                // is the right tier for a "nothing to warn about"
                // diagnostic — keeps a trail without pretending there is
                // an issue.
                ParsekLog.Verbose(InvokeTag,
                    $"Strip left no live pre-existing vessel(s) whose name matches a tree recording " +
                    $"(post-supplement kill drained the colliding set): collidingNames={collidingNames.Count} " +
                    $"[{string.Join(", ", collidingNames.ToArray())}] " +
                    $"(leftAlonePidsAlive={survey.LeftAlonePidsAliveCount} " +
                    $"excludedSelected={survey.ExcludedSelectedCount} " +
                    $"excludedStripped={survey.ExcludedStrippedCount} " +
                    $"excludedGhostMap={survey.ExcludedGhostMapCount})");
                return;
            }

            var stillPresentNames = survey.StillPresentNames ?? new List<string>();
            string joinedLiveNames = string.Join(", ", stillPresentNames.ToArray());
            ParsekLog.Warn(InvokeTag,
                $"Strip left vessels={liveVesselCount} collidingNames={stillPresentNames.Count} " +
                $"(leftAlonePidsAlive={survey.LeftAlonePidsAliveCount} " +
                $"excludedSelected={survey.ExcludedSelectedCount} " +
                $"excludedStripped={survey.ExcludedStrippedCount} " +
                $"excludedGhostMap={survey.ExcludedGhostMapCount}) " +
                $"pre-existing vessel(s) whose name matches a tree recording: [{joinedLiveNames}] — not " +
                $"related to the re-fly, will appear as second Kerbal X-shaped object in scene");
            ShowUserError(
                $"Heads up: pre-existing vessel(s) [{joinedLiveNames}] share a name with your re-fly " +
                $"tree. Any second Kerbal X in scene is NOT a Parsek ghost — it predates the rewind.");
        }

        /// <summary>
        /// Result of <see cref="SurveyLiveLeftAloneCollisions"/>: the
        /// post-supplement, defense-filtered survivor count plus exclusion
        /// counters that ride along into the structured log.
        /// </summary>
        internal struct LeftAloneSurveyResult
        {
            /// <summary>Total leftAlone vessel instances still alive AND in the colliding-name set.</summary>
            public int LiveCollidingVesselCount;

            /// <summary>Distinct names that still have at least one live colliding instance.</summary>
            public List<string> StillPresentNames;

            /// <summary>Count of leftAlone pids still in <see cref="FlightGlobals.Vessels"/> after defensive exclusions.</summary>
            public int LeftAlonePidsAliveCount;

            /// <summary>Count of leftAlone entries excluded because they matched <see cref="PostLoadStripResult.SelectedPid"/>.</summary>
            public int ExcludedSelectedCount;

            /// <summary>Count of leftAlone entries excluded because they matched <see cref="PostLoadStripResult.StrippedPids"/>.</summary>
            public int ExcludedStrippedCount;

            /// <summary>Count of leftAlone entries excluded because <see cref="GhostMapPresence.IsGhostMapVessel(uint)"/> returned true.</summary>
            public int ExcludedGhostMapCount;
        }

        /// <summary>
        /// Pure helper: walk the leftAlone (pid, name) pairs from
        /// <see cref="PostLoadStripResult.LeftAlonePidNames"/>, drop entries
        /// matching the selected/stripped/ghost-map exclusion sets, then
        /// intersect the survivors with the colliding-name set against the
        /// live-pid snapshot. Yields the per-name instance count and the
        /// exclusion counters used in the WARN's structured payload.
        /// </summary>
        /// <param name="leftAlonePidNames">The (pid, name) pairs the stripper left alone.</param>
        /// <param name="collisionNames">Names that match a committed-recording in the re-fly tree.</param>
        /// <param name="liveVesselPids">Pids currently in <c>FlightGlobals.Vessels</c>.</param>
        /// <param name="selectedPid">PR #577 P2 defense: the actively re-flown vessel pid is never a "leftover".</param>
        /// <param name="strippedPids">PR #577 P2 defense: pids the stripper just killed (death may lag the live list by a frame).</param>
        /// <param name="isGhostMapVessel">PR #577 P2 defense: GhostMap ProtoVessel pid predicate (delegate-injected for unit tests).</param>
        internal static LeftAloneSurveyResult SurveyLiveLeftAloneCollisions(
            IReadOnlyList<(uint pid, string name)> leftAlonePidNames,
            HashSet<string> collisionNames,
            HashSet<uint> liveVesselPids,
            uint selectedPid,
            IList<uint> strippedPids,
            Func<uint, bool> isGhostMapVessel)
        {
            var result = new LeftAloneSurveyResult
            {
                LiveCollidingVesselCount = 0,
                StillPresentNames = new List<string>(),
                LeftAlonePidsAliveCount = 0,
                ExcludedSelectedCount = 0,
                ExcludedStrippedCount = 0,
                ExcludedGhostMapCount = 0,
            };
            if (leftAlonePidNames == null || leftAlonePidNames.Count == 0)
                return result;

            HashSet<uint> strippedSet = null;
            if (strippedPids != null && strippedPids.Count > 0)
            {
                strippedSet = new HashSet<uint>();
                for (int i = 0; i < strippedPids.Count; i++) strippedSet.Add(strippedPids[i]);
            }

            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < leftAlonePidNames.Count; i++)
            {
                var (pid, name) = leftAlonePidNames[i];
                if (pid == 0u) continue;
                if (string.IsNullOrEmpty(name)) continue;

                // Defensive exclusions (PR #577 P2 review). All three are
                // belt-and-suspenders: the stripper does not put any of these
                // pids into LeftAlonePidNames in the first place. But if a
                // future code path leaks one in — e.g. selected slot match
                // ambiguity, or strip ordering with a delayed Die() — these
                // explicit filters keep the WARN honest.
                if (selectedPid != 0u && pid == selectedPid)
                {
                    result.ExcludedSelectedCount++;
                    continue;
                }
                if (strippedSet != null && strippedSet.Contains(pid))
                {
                    result.ExcludedStrippedCount++;
                    continue;
                }
                if (isGhostMapVessel != null && isGhostMapVessel(pid))
                {
                    result.ExcludedGhostMapCount++;
                    continue;
                }

                // Liveness check: is this leftAlone pid still in the live
                // vessel list? StripPreExistingDebrisForInPlaceContinuation
                // may have killed it between Strip and this WARN.
                if (liveVesselPids == null || !liveVesselPids.Contains(pid))
                    continue;

                result.LeftAlonePidsAliveCount++;

                if (collisionNames == null || !collisionNames.Contains(name))
                    continue;

                result.LiveCollidingVesselCount++;
                if (seenNames.Add(name)) result.StillPresentNames.Add(name);
            }
            return result;
        }

        private static IEnumerable<string> ProjectLeftAloneNames(
            IList<(uint pid, string name)> leftAlonePidNames)
        {
            if (leftAlonePidNames == null) yield break;
            for (int i = 0; i < leftAlonePidNames.Count; i++)
            {
                string n = leftAlonePidNames[i].name;
                if (!string.IsNullOrEmpty(n)) yield return n;
            }
        }

        private static HashSet<uint> SnapshotLiveVesselPids()
        {
            var pids = new HashSet<uint>();
            IList<Vessel> liveVessels;
            try { liveVessels = FlightGlobals.Vessels; }
            catch { liveVessels = null; }
            if (liveVessels == null) return pids;
            for (int i = 0; i < liveVessels.Count; i++)
            {
                var v = liveVessels[i];
                if (v == null) continue;
                uint pid = 0u;
                try { pid = v.persistentId; } catch { /* defensive */ }
                if (pid != 0u) pids.Add(pid);
            }
            return pids;
        }

        private static IEnumerable<string> EnumerateCommittedVesselNames()
        {
            var scenario = ParsekScenario.Instance;
            if (scenario == null) yield break;

            // Pull names straight from the committed-recording list. Any recording
            // in an active tree is in scope here; the goal is a cheap diagnostic,
            // not a strict ERS-filtered view.
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) yield break;

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                string name = rec.VesselName;
                if (!string.IsNullOrEmpty(name)) yield return name;
            }
        }

        /// <summary>
        /// Bug #587: pre-existing debris-vessel-supplement to <see cref="PostLoadStripper.Strip"/>
        /// for the in-place continuation Re-Fly path. <see cref="PostLoadStripper.Strip"/>
        /// keys on <see cref="RewindPoint.PidSlotMap"/> and only kills siblings registered in
        /// that map; pre-existing debris vessels carried in the rewind quicksave's
        /// <c>protoVessels</c> from prior career flights are left in scene by design.
        /// For an in-place continuation Re-Fly, however, those leftover vessels can share a
        /// name with a Destroyed-terminal recording in the actively re-flown tree, and
        /// KSP-stock patched-conics treats them as encounter candidates -- producing the
        /// phantom "Kerbin Encounter T+" + 50x warp cap that the playtest captured.
        /// <para>
        /// Returns the list of left-alone PIDs that should be killed: vessels whose
        /// <c>persistentId</c> is in <paramref name="leftAlonePids"/> AND whose name
        /// matches a Destroyed-terminal recording in the marker's tree. The
        /// <paramref name="protectedPids"/> parameter excludes the actively re-flown
        /// vessel + the marker's recording vessel pid so #573's strip-kill protection
        /// is preserved.
        /// </para>
        /// <para>
        /// Pure static; the caller is responsible for the actual <c>Vessel.Die()</c>
        /// invocations. The decision is keyed on (a) marker is in-place continuation,
        /// (b) recording is in the marker's tree, (c) recording's terminal state is
        /// Destroyed -- so a future career run with an alive "Kerbal X Debris" in
        /// orbit at rewind-point time cannot trip the kill (the recording would not
        /// be Destroyed-terminal there).
        /// </para>
        /// </summary>
        /// <param name="marker">Live re-fly marker. Returns empty list when null or not in-place.</param>
        /// <param name="trees">Committed trees from <c>RecordingStore.CommittedTrees</c>.</param>
        /// <param name="leftAlonePids">Pids of left-alone vessels with their names.</param>
        /// <param name="protectedPids">Pids that must NOT be killed (selected vessel + marker active).</param>
        /// <param name="sessionSuppressedRecordingIds">Recording ids in the
        /// session-suppressed subtree (typically
        /// <see cref="EffectiveState.ComputeSessionSuppressedSubtree"/>'s
        /// result for the live marker). Bug #587 follow-up: the original
        /// fix only killed leftovers matching <c>Destroyed</c>-terminal
        /// recordings, but the 2026-04-25 playtest captured a non-Destroyed
        /// pre-existing vessel in the supersede subtree (a phantom that
        /// would be replaced by the re-fly's new tail) being kept alive in
        /// scene. Names of recordings in the suppressed subtree are now
        /// also kill-eligible. Pass <c>null</c> in tests when only the
        /// Destroyed-terminal predicate is exercised — backwards-compatible
        /// with the original signature.</param>
        internal static List<uint> ResolveInPlaceContinuationDebrisToKill(
            ReFlySessionMarker marker,
            IReadOnlyList<RecordingTree> trees,
            IReadOnlyList<(uint pid, string vesselName)> leftAlonePids,
            HashSet<uint> protectedPids,
            IReadOnlyCollection<string> sessionSuppressedRecordingIds = null)
        {
            var kill = new List<uint>();
            if (marker == null) return kill;
            // Accept both legacy in-place (active == origin) and the
            // post-#734 fork shape (InPlaceContinuation flag, active != origin).
            // Both keep the player on the SAME physical vessel as origin, so
            // the parent-chain doubled-vessel cleanup applies identically.
            // Pure placeholder Re-Fly (the player flies a fresh vessel) is
            // skipped -- that active vessel is legitimately alive in scene.
            if (!ReFlySessionMarker.IsInPlaceContinuation(marker))
                return kill;
            if (string.IsNullOrEmpty(marker.TreeId)) return kill;
            if (trees == null || trees.Count == 0) return kill;
            if (leftAlonePids == null || leftAlonePids.Count == 0) return kill;

            // Locate the marker's tree.
            RecordingTree markerTree = null;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i] == null) continue;
                if (string.Equals(trees[i].Id, marker.TreeId, StringComparison.Ordinal))
                {
                    markerTree = trees[i];
                    break;
                }
            }
            if (markerTree == null) return kill;

            // Hoist the suppressed-subtree input into a local HashSet for O(1)
            // membership checks. EffectiveState.ComputeSessionSuppressedSubtree
            // returns a defensive HashSet copy; tests pass a HashSet or array.
            // The local rebuild also handles the IReadOnlyCollection-without-
            // Contains-method case at no measurable cost (subtree size is
            // typically <10 ids).
            HashSet<string> suppressedSet = null;
            if (sessionSuppressedRecordingIds != null && sessionSuppressedRecordingIds.Count > 0)
            {
                suppressedSet = new HashSet<string>(sessionSuppressedRecordingIds, StringComparer.Ordinal);
            }

            // Build the kill-eligible name set: Destroyed-terminal recordings
            // (the original #587 predicate) PLUS any recording in the
            // session-suppressed subtree (#587 follow-up). Suppressed-subtree
            // recordings are being superseded by the in-place continuation —
            // any pre-existing vessel matching one of their names is a phantom
            // from the old timeline that the re-fly is overwriting, and would
            // produce the "second Kerbal X-shaped object" the user sees.
            //
            // CRITICAL: never add the active Re-Fly target's vessel name to
            // the kill set. The active recording is itself the head of the
            // suppressed subtree, so it would otherwise be picked up by the
            // suppressed-subtree predicate. The protected-pids check below
            // already shields the LIVE vessel, but a duplicate-name vessel
            // (rare but possible) would still slip through if the name made
            // it into the eligible set. Drop the active rec early.
            var killEligibleNames = new HashSet<string>(StringComparer.Ordinal);
            int destroyedTerminalNames = 0;
            int suppressedSubtreeNames = 0;
            int parentChainNames = 0;
            foreach (var rec in markerTree.Recordings.Values)
            {
                if (rec == null) continue;
                if (string.IsNullOrEmpty(rec.VesselName)) continue;
                if (string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId,
                        StringComparison.Ordinal))
                    continue; // never kill by the active Re-Fly target's name

                bool destroyedTerminal = rec.TerminalStateValue == TerminalState.Destroyed;
                bool inSuppressedSubtree = suppressedSet != null
                    && !string.IsNullOrEmpty(rec.RecordingId)
                    && suppressedSet.Contains(rec.RecordingId);
                bool inActiveParentChain = false;
                if (!string.IsNullOrEmpty(rec.RecordingId))
                {
                    string walkTrace;
                    inActiveParentChain = GhostMapPresence.IsRecordingInParentChainOfActiveReFly(
                        rec.RecordingId,
                        marker.ActiveReFlyRecordingId,
                        trees,
                        out walkTrace);
                }

                if (!destroyedTerminal && !inSuppressedSubtree && !inActiveParentChain) continue;

                if (killEligibleNames.Add(rec.VesselName))
                {
                    if (destroyedTerminal) destroyedTerminalNames++;
                    if (inSuppressedSubtree) suppressedSubtreeNames++;
                    if (inActiveParentChain) parentChainNames++;
                }
            }
            if (killEligibleNames.Count == 0) return kill;

            for (int i = 0; i < leftAlonePids.Count; i++)
            {
                var (pid, name) = leftAlonePids[i];
                if (pid == 0u) continue;
                if (string.IsNullOrEmpty(name)) continue;
                if (protectedPids != null && protectedPids.Contains(pid)) continue;
                if (!killEligibleNames.Contains(name)) continue;
                kill.Add(pid);
            }

            if (kill.Count > 0 && !ParsekLog.SuppressLogging)
            {
                ParsekLog.Verbose(InvokeTag,
                    $"ResolveInPlaceContinuationDebrisToKill: matched {kill.Count} pid(s) " +
                    $"against {killEligibleNames.Count} kill-eligible name(s) " +
                    $"(destroyedTerminal={destroyedTerminalNames} suppressedSubtree={suppressedSubtreeNames} " +
                    $"parentChain={parentChainNames}) " +
                    $"in tree '{markerTree.Id}'");
            }

            return kill;
        }

        /// <summary>
        /// Bug #587 follow-up (PR #558 P2 review): build a stable snapshot of
        /// kill targets from a live <c>IList</c> before any
        /// <c>Vessel.Die()</c> calls run. Walking the live
        /// <c>FlightGlobals.Vessels</c> while calling <c>Die()</c> shifts
        /// subsequent indices and can skip consecutive matching debris -- the
        /// exact multi-debris pattern this PR claims to fix. The snapshot
        /// captures every entry whose <paramref name="pidGetter"/> returns a
        /// pid in <paramref name="killPids"/>, so subsequent iteration of the
        /// returned list is unaffected by source-list mutations.
        ///
        /// <para>Generic over <typeparamref name="T"/> so unit tests can pin
        /// the contract against a fake type without depending on KSP's
        /// <c>Vessel</c>.</para>
        /// </summary>
        /// <param name="liveSource">Live source list (typically
        /// <c>FlightGlobals.Vessels</c>).</param>
        /// <param name="killPids">Pids to kill.</param>
        /// <param name="pidGetter">Extracts the pid for an item; null items
        /// pass-through and are skipped.</param>
        internal static List<T> SnapshotKillTargets<T>(
            IList<T> liveSource,
            HashSet<uint> killPids,
            Func<T, uint> pidGetter) where T : class
        {
            var result = new List<T>();
            if (liveSource == null || killPids == null || killPids.Count == 0
                || pidGetter == null)
            {
                return result;
            }
            for (int i = 0; i < liveSource.Count; i++)
            {
                T item = liveSource[i];
                if (item == null) continue;
                uint pid = pidGetter(item);
                if (pid == 0u) continue;
                if (!killPids.Contains(pid)) continue;
                result.Add(item);
            }
            return result;
        }

        /// <summary>
        /// Builds the protected-pid set passed to
        /// <see cref="ResolveInPlaceContinuationDebrisToKill"/> at the
        /// in-place continuation strip-supplement seam. Three sources:
        /// <list type="bullet">
        ///   <item><description><see cref="PostLoadStripResult.SelectedPid"/>
        ///     — #573 contract, the actively re-flown vessel.</description></item>
        ///   <item><description>The marker's active recording's
        ///     <see cref="Recording.VesselPersistentId"/> from
        ///     <paramref name="committedRecs"/>. Same pid as Selected for
        ///     in-place continuation today, but kept as belt-and-suspenders
        ///     against a future code path that diverges them.</description></item>
        ///   <item><description><see cref="PostLoadStripResult.PreservedFlagPids"/>
        ///     — #fix-refly-preserve-flag-vessels follow-up: planted-flag
        ///     vessels the stripper deliberately left alone must also be
        ///     immune to the secondary kill walk, otherwise a flag whose
        ///     <c>vesselName</c> collides with a recording's
        ///     <c>VesselName</c> (player-typed labels, or KSP's default
        ///     kerbal-name-derived label clashing with an EVA recording)
        ///     would still be despawned by <c>Vessel.Die()</c>, silently
        ///     erasing the FlagPlant career milestone.</description></item>
        /// </list>
        /// Pure static so tests can pin the contract without a live KSP scene.
        /// </summary>
        internal static HashSet<uint> BuildProtectedPidsForInPlaceContinuation(
            PostLoadStripResult stripResult,
            ReFlySessionMarker marker,
            IReadOnlyList<Recording> committedRecs)
        {
            var protectedPids = new HashSet<uint>();
            if (stripResult.SelectedPid != 0u)
                protectedPids.Add(stripResult.SelectedPid);
            if (marker != null && committedRecs != null
                && !string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
            {
                for (int i = 0; i < committedRecs.Count; i++)
                {
                    var rec = committedRecs[i];
                    if (rec == null) continue;
                    if (string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId,
                            StringComparison.Ordinal))
                    {
                        if (rec.VesselPersistentId != 0u)
                            protectedPids.Add(rec.VesselPersistentId);
                        break;
                    }
                }
            }

            int flagsAddedCount = 0;
            if (stripResult.PreservedFlagPids != null)
            {
                for (int i = 0; i < stripResult.PreservedFlagPids.Count; i++)
                {
                    uint flagPid = stripResult.PreservedFlagPids[i];
                    if (flagPid == 0u) continue;
                    if (protectedPids.Add(flagPid)) flagsAddedCount++;
                }
            }

            if (flagsAddedCount > 0 && !ParsekLog.SuppressLogging)
            {
                ParsekLog.Info(InvokeTag,
                    $"BuildProtectedPidsForInPlaceContinuation: shielded " +
                    $"{flagsAddedCount} preserved flag pid(s) from in-place " +
                    $"continuation kill walk (treeId='{marker?.TreeId ?? "<null>"}', " +
                    $"activeRec='{marker?.ActiveReFlyRecordingId ?? "<null>"}') " +
                    $"-- planted-flag career milestones immune to name-collision " +
                    $"Die() even when their vesselName matches an in-scope recording");
            }

            return protectedPids;
        }

        /// <summary>
        /// #fix-refly-preserve-flag-vessels survey-level skip predicate. Returns
        /// true when a live vessel must be excluded from the
        /// <c>leftAlonePidNames</c> list built by
        /// <see cref="StripPreExistingDebrisForInPlaceContinuation"/>. Currently
        /// only <see cref="VesselType.Flag"/>: planted flags are durable
        /// FlagPlant career milestones that
        /// <see cref="PostLoadStripper.ShouldPreserveVesselType"/> already
        /// bypasses at the primary strip path, and that the secondary kill walk
        /// must NOT silently Die() on a name-collision with an in-scope
        /// recording. Keyed on actual <c>VesselType</c> (not on
        /// <see cref="PostLoadStripResult.PreservedFlagPids"/> membership) so
        /// the skip is robust against any future divergence between strip
        /// bookkeeping and live vessel state. Pure / static so unit tests can
        /// pin it against the <see cref="IStrippableVessel"/> contract.
        /// </summary>
        internal static bool ShouldSkipFromLeftAloneSurvey(IStrippableVessel v)
        {
            if (v == null) return false;
            VesselType type;
            // Mirror LiveVesselAdapter.VesselType's defensive try/catch: KSP's
            // Vessel getter walks managed Unity state and can throw on a
            // half-destroyed GameObject mid-strip. On throw we fall through to
            // the conservative "don't skip" branch so a defective vessel still
            // gets the kill-set protection layer.
            try { type = v.VesselType; }
            catch { return false; }
            return type == VesselType.Flag;
        }

        /// <summary>
        /// #fix-refly-preserve-flag-vessels survey-level filter: builds the
        /// <c>(pid, name)</c> pairs of live vessels that
        /// <see cref="PostLoadStripper"/> left in scene, EXCLUDING entries that
        /// must be shielded from the secondary in-place continuation kill walk.
        ///
        /// <para>Exclusion order (every skip increments a counter for the one-shot
        /// summary log):</para>
        /// <list type="bullet">
        ///   <item><description>Zero or null pid / name.</description></item>
        ///   <item><description>Ghost-map ProtoVessels (Parsek-owned).</description></item>
        ///   <item><description>Pids in <see cref="PostLoadStripResult.StrippedPids"/>
        ///     (already dead, only present if the Die() event hasn't drained
        ///     from <c>FlightGlobals</c> yet).</description></item>
        ///   <item><description><see cref="PostLoadStripResult.SelectedPid"/>
        ///     (#573 contract: the actively re-flown vessel).</description></item>
        ///   <item><description><see cref="VesselType.Flag"/> vessels
        ///     (<see cref="ShouldSkipFromLeftAloneSurvey"/>): the user-requested
        ///     belt-and-suspenders that resolves the original review note at
        ///     the source — a preserved flag must never enter the leftAlone
        ///     survey, even if its <c>vesselName</c> happens to collide with a
        ///     Destroyed-terminal / session-suppressed / parent-chain
        ///     recording.</description></item>
        /// </list>
        /// Returns an empty list (never null) on null/empty input. Pure /
        /// static so tests can pin the contract against
        /// <c>IStrippableVessel</c> stubs.
        /// </summary>
        internal static List<(uint pid, string name)> BuildLeftAlonePidNamesForInPlaceContinuation(
            IList<IStrippableVessel> liveVessels,
            PostLoadStripResult stripResult,
            Func<uint, bool> isGhostMapVessel)
        {
            var result = new List<(uint pid, string name)>();
            if (liveVessels == null || liveVessels.Count == 0) return result;
            Func<uint, bool> ghostCheck = isGhostMapVessel ?? (_ => false);

            int skippedFlags = 0;
            int skippedGhostMap = 0;
            int skippedStripped = 0;
            int skippedSelected = 0;
            int includedCount = 0;
            // First flag's identifiers captured for the one-shot Verbose log,
            // so playtest log readers can confirm the skip ran without spamming
            // a line per vessel.
            uint firstFlagPid = 0u;
            string firstFlagName = null;

            for (int i = 0; i < liveVessels.Count; i++)
            {
                var v = liveVessels[i];
                if (v == null) continue;
                uint pid;
                try { pid = v.PersistentId; }
                catch { continue; }
                if (pid == 0u) continue;
                if (ghostCheck(pid)) { skippedGhostMap++; continue; }
                if (stripResult.StrippedPids != null
                    && stripResult.StrippedPids.Contains(pid))
                {
                    skippedStripped++;
                    continue;
                }
                if (stripResult.SelectedPid == pid)
                {
                    skippedSelected++;
                    continue;
                }
                // Survey-level flag skip -- the user-requested upstream defense
                // alongside BuildProtectedPidsForInPlaceContinuation's
                // PreservedFlagPids branch.
                if (ShouldSkipFromLeftAloneSurvey(v))
                {
                    if (skippedFlags == 0)
                    {
                        firstFlagPid = pid;
                        try { firstFlagName = v.VesselName; }
                        catch { firstFlagName = null; }
                    }
                    skippedFlags++;
                    continue;
                }
                string name;
                try { name = v.VesselName; }
                catch { name = null; }
                if (string.IsNullOrEmpty(name)) continue;
                result.Add((pid, name));
                includedCount++;
            }

            if (skippedFlags > 0 && !ParsekLog.SuppressLogging)
            {
                string safeName = string.IsNullOrEmpty(firstFlagName) ? "<unnamed>" : firstFlagName;
                ParsekLog.Verbose(InvokeTag,
                    $"Strip post-supplement: skipping flag v={firstFlagPid} name='{safeName}' " +
                    $"from leftAlone survey -- preserved by PostLoadStripper " +
                    $"(totalFlagsSkipped={skippedFlags} included={includedCount} " +
                    $"skippedGhostMap={skippedGhostMap} skippedStripped={skippedStripped} " +
                    $"skippedSelected={skippedSelected})");
            }

            return result;
        }

        /// <summary>
        /// Bug #587: production caller for <see cref="ResolveInPlaceContinuationDebrisToKill"/>.
        /// Runs after <see cref="AtomicMarkerWrite"/> (so the marker is set + the
        /// re-fly-active short-circuit in <c>RunSpawnDeathChecks</c> is engaged) and
        /// kills pre-existing debris that would confuse KSP-stock patched conics.
        /// Each <c>Vessel.Die()</c> runs inside a <see cref="SuppressionGuard.Crew"/>
        /// to mirror <c>PostLoadStripper.StripVessel</c>'s silent-removal contract --
        /// no CrewKilled / CrewRemoved fanout into the ledger from this cleanup.
        ///
        /// <para>#fix-refly-preserve-flag-vessels protects preserved
        /// <see cref="VesselType.Flag"/> vessels through THREE coexisting
        /// layers — belt-and-suspenders for the planted-flag career
        /// milestone:</para>
        /// <list type="number">
        ///   <item><description><b>Primary strip bypass:</b>
        ///     <see cref="PostLoadStripper.ShouldPreserveVesselType"/>
        ///     short-circuits the strip BEFORE the slot-map / strict-unmatched
        ///     paths, so the flag is never recorded in
        ///     <see cref="PostLoadStripResult.StrippedPids"/> and never
        ///     receives a <c>Vessel.Die()</c> from <c>PostLoadStripper</c>.</description></item>
        ///   <item><description><b>Survey-level skip:</b>
        ///     <see cref="BuildLeftAlonePidNamesForInPlaceContinuation"/> filters
        ///     <see cref="VesselType.Flag"/> entries out of the
        ///     <c>leftAlonePidNames</c> list at the survey step, so a
        ///     name-colliding flag never even enters the kill-set construction.
        ///     This is the user-requested layer that resolves the original
        ///     review note's concern at the source: a preserved flag must not
        ///     reach the resolver in the first place.</description></item>
        ///   <item><description><b>Kill-set protection:</b>
        ///     <see cref="BuildProtectedPidsForInPlaceContinuation"/> still adds
        ///     <see cref="PostLoadStripResult.PreservedFlagPids"/> to the
        ///     <c>protectedPids</c> set passed to
        ///     <see cref="ResolveInPlaceContinuationDebrisToKill"/>. Redundant
        ///     given (2) today, kept as a safety net so a future refactor that
        ///     accidentally drops the survey-level skip (e.g., a change to the
        ///     adapter's vessel-type fallback, or a new survey path that
        ///     bypasses the helper) cannot silently regress flag preservation.</description></item>
        /// </list>
        /// Both (2) and (3) are exercised independently by
        /// <c>Bug587StripPreExistingDebrisTests</c> regression guards so a
        /// future refactor cannot drop one layer unnoticed.
        /// </summary>
        internal static void StripPreExistingDebrisForInPlaceContinuation(
            PostLoadStripResult stripResult)
        {
            var scenario = ParsekScenario.Instance;
            if (scenario == null) return;
            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null) return;

            // Build the leftAlone (pid, name) pairs from live FlightGlobals because
            // the strip result only retains names; we need pids to issue Die() calls.
            // The strip already enumerated FlightGlobals, but did not pair pid+name
            // in the public surface. Re-enumerate via the same IStrippableVessel
            // abstraction PostLoadStripper uses so the VesselType-aware skip
            // (#fix-refly-preserve-flag-vessels survey-level layer) runs against
            // the production-adapter's defensive vessel-type access. We also retain
            // the raw IList<Vessel> for SnapshotKillTargets downstream (which
            // operates on the live Vessel objects to issue Die() calls).
            IList<Vessel> liveVessels;
            try { liveVessels = FlightGlobals.Vessels; }
            catch { liveVessels = null; }
            if (liveVessels == null || liveVessels.Count == 0) return;

            var liveStrippable = new List<IStrippableVessel>();
            foreach (var sv in DefaultVesselEnumeration.Instance.EnumerateVessels())
            {
                if (sv == null) continue;
                liveStrippable.Add(sv);
            }
            if (liveStrippable.Count == 0) return;

            var leftAlonePidNames = BuildLeftAlonePidNamesForInPlaceContinuation(
                liveStrippable, stripResult, GhostMapPresence.IsGhostMapVessel);

            // Protect the selected slot vessel + the marker's active recording's pid
            // (#573 contract: never kill the actively re-flown vessel) + every
            // VesselType.Flag pid the stripper preserved (#fix-refly-preserve-flag-vessels
            // follow-up: belt-and-suspenders alongside the survey-level skip above.
            // Even though BuildLeftAlonePidNamesForInPlaceContinuation now filters
            // VesselType.Flag entries at the source, this kill-set protection stays
            // as a redundant safety net so a future refactor that accidentally
            // bypasses the survey helper cannot silently revive name-collision
            // Die() on a planted-flag career milestone).
            var protectedPids = BuildProtectedPidsForInPlaceContinuation(
                stripResult, marker, RecordingStore.CommittedRecordings);

            // Bug #587 follow-up: also broaden the kill set to recording names in
            // the session-suppressed subtree (recordings being superseded by this
            // in-place continuation). The original predicate only killed
            // Destroyed-terminal name matches; the 2026-04-25 playtest caught a
            // non-Destroyed pre-existing vessel surviving the strip and showing up
            // as a clickable "doubled" copy of the booster's upper stage. The
            // suppressed-subtree closure is the authoritative scope of "this
            // re-fly is overwriting these recordings", so any matching real
            // vessel from the old timeline is phantom and should be cleaned up.
            IReadOnlyCollection<string> suppressedSubtree;
            try { suppressedSubtree = EffectiveState.ComputeSessionSuppressedSubtree(marker); }
            catch (Exception ex)
            {
                ParsekLog.Warn(InvokeTag,
                    $"StripPreExistingDebrisForInPlaceContinuation: ComputeSessionSuppressedSubtree threw " +
                    $"({ex.GetType().Name}: {ex.Message}) — falling back to Destroyed-terminal-only kill set");
                suppressedSubtree = null;
            }

            var kill = ResolveInPlaceContinuationDebrisToKill(
                marker,
                RecordingStore.CommittedTrees,
                leftAlonePidNames,
                protectedPids,
                suppressedSubtree);
            if (kill.Count == 0) return;

            // Bug #587 follow-up (PR #558 P2 review): snapshot the targets
            // BEFORE iterating Die(). Vessel.Die() removes the vessel from
            // FlightGlobals.Vessels, so walking the live IList while calling
            // Die() shifts subsequent indices and can skip consecutive
            // matching debris -- exactly the multi-debris case this PR is
            // supposed to handle. SnapshotKillTargets builds a stable list
            // before any Die() runs.
            var killSet = new HashSet<uint>(kill);
            var killTargets = SnapshotKillTargets(
                liveVessels, killSet, v => v == null ? 0u : v.persistentId);

            int killed = 0;
            var killedNames = new List<string>();
            for (int i = 0; i < killTargets.Count; i++)
            {
                var v = killTargets[i];
                if (v == null) continue;
                string name = v.vesselName ?? "<unnamed>";
                try
                {
                    using (SuppressionGuard.Crew())
                    {
                        v.Die();
                    }
                    killed++;
                    killedNames.Add(name);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(InvokeTag,
                        $"Strip post-supplement: Die() threw for v={v.persistentId} " +
                        $"name='{name}': {ex.Message}");
                }
            }

            if (killed > 0)
            {
                string joined = string.Join(", ", killedNames.ToArray());
                ParsekLog.Warn(InvokeTag,
                    $"Strip post-supplement: killed {killed} pre-existing tree-collision vessel(s) " +
                    $"for in-place continuation re-fly: [{joined}] " +
                    $"(name matches a Destroyed-terminal, session-suppressed, or parent-chain recording in tree '{marker.TreeId}'; " +
                    $"left in scene by PostLoadStripper because no PidSlotMap entry; " +
                    $"would otherwise trip KSP patched conics into a phantom encounter -- bug #587)");
            }
        }

        /// <summary>
        /// Posts a user-visible error toast via <c>ScreenMessages</c>. Safe
        /// when called from a non-flight context (ScreenMessages tolerates it).
        /// </summary>
        internal static void ShowUserError(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                ScreenMessages.PostScreenMessage(
                    message, 5f, ScreenMessageStyle.UPPER_CENTER);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(InvokeTag,
                    $"ShowUserError: ScreenMessages.PostScreenMessage threw: {ex.Message}");
            }
        }

        private static void HandleQuicksaveMissing(RewindPoint rp)
        {
            if (rp == null) return;
            string abs = ResolveAbsoluteQuicksavePath(rp);
            if (!string.IsNullOrEmpty(abs) && !File.Exists(abs))
            {
                string previousFilename = rp.QuicksaveFilename;
                rp.QuicksaveFilename = null;
                rp.Corrupted = true;
                ParsekLog.Warn(InvokeTag,
                    $"Quicksave cleared: rp={rp.RewindPointId} missingFile='{previousFilename}' " +
                    $"(marking Corrupted)");
            }
        }

        private static void TryRestoreBundle(ReconciliationBundle bundle)
        {
            try
            {
                ReconciliationBundle.Restore(bundle);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(InvokeTag,
                    $"ReconciliationBundle.Restore threw on failure rollback: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        // Per-RP precondition cache (60s TTL). Populated by CanInvoke.
        // ----------------------------------------------------------------

        internal struct PreconditionResult
        {
            public bool Passed;
            public string Reason;
            public DateTime CheckedAtUtc;
        }

        internal static class PreconditionCache
        {
            private static readonly Dictionary<string, PreconditionResult> cache
                = new Dictionary<string, PreconditionResult>();
            private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

            internal static void InvalidateForTesting()
            {
                cache.Clear();
            }

            internal static void Invalidate(RewindPoint rp)
            {
                if (rp == null || string.IsNullOrEmpty(rp.RewindPointId)) return;
                cache.Remove(rp.RewindPointId);
            }

            internal static bool IsValid(RewindPoint rp)
            {
                if (rp == null || string.IsNullOrEmpty(rp.RewindPointId)) return false;
                PreconditionResult result;
                if (!cache.TryGetValue(rp.RewindPointId, out result)) return false;
                return DateTime.UtcNow - result.CheckedAtUtc < Ttl;
            }

            internal static PreconditionResult? Get(RewindPoint rp)
            {
                if (rp == null || string.IsNullOrEmpty(rp.RewindPointId)) return null;
                PreconditionResult result;
                if (cache.TryGetValue(rp.RewindPointId, out result)) return result;
                return null;
            }

            internal static void Store(RewindPoint rp, PreconditionResult result)
            {
                if (rp == null || string.IsNullOrEmpty(rp.RewindPointId)) return;
                cache[rp.RewindPointId] = result;
            }

            /// <summary>
            /// Clears every cached precondition result. Called on scene unload
            /// so the dict does not grow unbounded across long sessions.
            /// </summary>
            internal static void ClearAll()
            {
                int n = cache.Count;
                cache.Clear();
                if (n > 0)
                    ParsekLog.Verbose(InvokeTag,
                        $"PreconditionCache.ClearAll: dropped {n} cached result(s)");
            }
        }

        // ----------------------------------------------------------------
        // Deep-parse PartLoader precondition (§7.29)
        // ----------------------------------------------------------------

        internal static class PartLoaderPrecondition
        {
            // Test seam: when non-null, used instead of PartLoader.getPartInfoByName.
            internal static Func<string, bool> PartExistsOverrideForTesting;

            internal static PreconditionResult Check(RewindPoint rp, string absolutePath)
            {
                var result = new PreconditionResult
                {
                    Passed = true,
                    Reason = null,
                    CheckedAtUtc = DateTime.UtcNow,
                };

                if (rp == null) return result;
                if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                {
                    result.Passed = false;
                    result.Reason = "Quicksave file missing on disk";
                    return result;
                }

                try
                {
                    ConfigNode root = ConfigNode.Load(absolutePath);
                    if (root == null)
                    {
                        result.Passed = false;
                        result.Reason = "Quicksave file failed to parse";
                        MarkCorrupted(rp, result.Reason);
                        return result;
                    }

                    var missing = new List<string>();
                    CollectMissingParts(root, missing);

                    if (missing.Count > 0)
                    {
                        result.Passed = false;
                        result.Reason = $"Missing parts: {string.Join(", ", missing.ToArray())}";
                        MarkCorrupted(rp, result.Reason);
                    }
                }
                catch (Exception ex)
                {
                    result.Passed = false;
                    result.Reason = $"Quicksave parse threw: {ex.Message}";
                    MarkCorrupted(rp, result.Reason);
                }

                return result;
            }

            private static void CollectMissingParts(ConfigNode node, List<string> missing)
            {
                if (node == null) return;

                // Walk the whole tree; PART nodes may appear under GAME/FLIGHTSTATE/VESSEL/PART.
                foreach (ConfigNode child in node.GetNodes())
                {
                    if (child == null) continue;
                    if (string.Equals(child.name, "PART", StringComparison.Ordinal))
                    {
                        string partName = child.GetValue("name");
                        if (string.IsNullOrEmpty(partName)) continue;
                        if (!PartExists(partName) && !missing.Contains(partName))
                            missing.Add(partName);
                    }
                    else
                    {
                        CollectMissingParts(child, missing);
                    }
                }
            }

            private static bool PartExists(string partName)
            {
                if (PartExistsOverrideForTesting != null)
                    return PartExistsOverrideForTesting(partName);
                try
                {
                    return PartLoader.getPartInfoByName(partName) != null;
                }
                catch
                {
                    return false;
                }
            }

            private static void MarkCorrupted(RewindPoint rp, string reason)
            {
                if (rp == null) return;
                rp.Corrupted = true;
                ParsekLog.Warn(InvokeTag,
                    $"Precondition failed: rp={rp.RewindPointId} reason='{reason}' (marked Corrupted)");
            }
        }

    }
}
