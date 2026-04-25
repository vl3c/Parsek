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
            string title = "Parsek - Finish Flight";
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
                    new DialogGUIButton("Re-Fly", () =>
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

            // Step 2: copy RP quicksave from saves/<save>/Parsek/RewindPoints/<rpId>.sfs
            // to saves/<save>/Parsek_Rewind_<sessionId>.sfs (root; KSP's LoadGame
            // does not accept subdirectory paths).
            string tempPath;
            string tempLoadName;
            try
            {
                CopyQuicksaveToSaveRoot(rp, sessionId, out tempPath, out tempLoadName);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(InvokeTag,
                    $"Invocation failed: copy-to-root threw: {ex.Message} rp={rp.RewindPointId}");
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
                    stripResult = PostLoadStripper.Strip(rp, slotIdx);
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
        /// Two paths, decided up front:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>
        ///     <b>In-place continuation</b> — when the Limbo-restore path
        ///     kept the origin recording alive in the restored tree and the
        ///     active vessel pid (just focused by Strip+Activate) matches the
        ///     origin's <see cref="Recording.VesselPersistentId"/>, the
        ///     recorder will append new samples directly to the origin
        ///     recording. Point <see cref="ReFlySessionMarker.ActiveReFlyRecordingId"/>
        ///     at the origin id and create no placeholder. This eliminates
        ///     the legacy placeholder-then-redirect dance: the marker now
        ///     points directly at the recording that will receive samples.
        ///   </description></item>
        ///   <item><description>
        ///     <b>New-recording path</b> — origin tree is gone or the active
        ///     pid does not match the origin's pid. Create a fresh
        ///     placeholder recording (no trajectory yet), add it via
        ///     <see cref="RecordingStore.AddProvisional"/>, and point the
        ///     marker at its id. The recorder will populate this placeholder
        ///     as the player flies.
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

            // Resolve the origin recording. If it survived the Limbo-restore
            // and its VesselPersistentId matches the strip-selected pid, the
            // recorder will continue writing into THIS recording; we point
            // the marker at it directly with no placeholder. Otherwise we
            // build a fresh placeholder.
            Recording originChild = FindRecordingById(selected.OriginChildRecordingId);
            bool inPlaceContinuation =
                originChild != null
                && IsCommittedRecording(originChild)
                && originChild.VesselPersistentId == stripResult.SelectedPid;

            Recording provisional = null;
            string activeReFlyRecordingId;
            string treeIdForMarker;

            if (inPlaceContinuation)
            {
                activeReFlyRecordingId = originChild.RecordingId;
                treeIdForMarker = originChild.TreeId;
                ParsekLog.Info(InvokeTag,
                    $"AtomicMarkerWrite: in-place continuation detected — marker → origin " +
                    $"{originChild.RecordingId} (no placeholder created)");

                CheckpointHookForTesting?.Invoke("CheckpointA:BeforeProvisional");
                CheckpointHookForTesting?.Invoke("CheckpointA:AfterProvisional");
            }
            else
            {
                provisional = BuildProvisionalRecording(rp, selected, originChild, sessionId, stripResult);
                activeReFlyRecordingId = provisional.RecordingId;
                treeIdForMarker = provisional.TreeId;

                CheckpointHookForTesting?.Invoke("CheckpointA:BeforeProvisional");
                RecordingStore.AddProvisional(provisional);
                CheckpointHookForTesting?.Invoke("CheckpointA:AfterProvisional");
            }

            ReFlySessionMarker marker;
            try
            {
                marker = new ReFlySessionMarker
                {
                    SessionId = sessionId,
                    TreeId = treeIdForMarker,
                    ActiveReFlyRecordingId = activeReFlyRecordingId,
                    OriginChildRecordingId = selected.OriginChildRecordingId,
                    RewindPointId = rp.RewindPointId,
                    InvokedUT = SafeNow(),
                    InvokedRealTime = DateTime.UtcNow.ToString("o"),
                };

                CheckpointHookForTesting?.Invoke("CheckpointB:BeforeMarker");
                scenario.ActiveReFlySessionMarker = marker;
                scenario.BumpSupersedeStateVersion();
                CheckpointHookForTesting?.Invoke("CheckpointB:AfterMarker");
            }
            catch
            {
                // Roll back the provisional (when we added one) AND the
                // marker so no half-written pair leaks out of the critical
                // section. Both clears are idempotent
                // (RemoveCommittedInternal returns false if absent; marker
                // clear is a null-assignment). In-place continuation paths
                // did not add anything to the committed list, so there's
                // nothing to roll back on the recording side.
                if (provisional != null)
                    RecordingStore.RemoveCommittedInternal(provisional);
                try
                {
                    if (ParsekScenario.Instance != null)
                        ParsekScenario.Instance.ActiveReFlySessionMarker = null;
                }
                catch { /* idempotent rollback; swallow secondary failure */ }
                throw;
            }

            ParsekLog.Info(SessionTag,
                $"Started sess={sessionId} rp={rp.RewindPointId} slot={selected.SlotIndex} " +
                $"provisional={activeReFlyRecordingId} " +
                $"origin={selected.OriginChildRecordingId ?? "<none>"} " +
                $"tree={treeIdForMarker ?? "<none>"} " +
                $"inPlaceContinuation={inPlaceContinuation}");
        }

        /// <summary>
        /// True iff <paramref name="rec"/> currently appears in
        /// <see cref="RecordingStore.CommittedRecordings"/> by reference.
        /// Used by <see cref="AtomicMarkerWrite"/> to decide between the
        /// in-place continuation path (origin survived Limbo restore) and
        /// the placeholder path (origin tree was gone / pid mismatched).
        /// </summary>
        private static bool IsCommittedRecording(Recording rec)
        {
            if (rec == null) return false;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return false;
            for (int i = 0; i < committed.Count; i++)
            {
                if (ReferenceEquals(committed[i], rec))
                    return true;
            }
            return false;
        }

        internal static Recording BuildProvisionalRecording(
            RewindPoint rp, ChildSlot selected, Recording originChild,
            string sessionId, PostLoadStripResult stripResult)
        {
            var rec = new Recording
            {
                RecordingId = "rec_" + Guid.NewGuid().ToString("N"),
                MergeState = MergeState.NotCommitted,
                CreatingSessionId = sessionId,
                SupersedeTargetId = selected.OriginChildRecordingId,
                ProvisionalForRpId = rp.RewindPointId,
                ParentBranchPointId = originChild?.ParentBranchPointId ?? rp.BranchPointId,
                TreeId = originChild?.TreeId,
                VesselPersistentId = stripResult.SelectedPid,
                VesselName = stripResult.SelectedVessel != null
                    ? stripResult.SelectedVessel.vesselName
                    : (originChild?.VesselName ?? "Re-fly"),
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

        internal static string ResolveAbsoluteQuicksavePath(RewindPoint rp)
        {
            if (rp == null || string.IsNullOrEmpty(rp.QuicksaveFilename))
                return null;
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
        /// After a successful Strip, cross-reference <see cref="PostLoadStripResult.LeftAloneNames"/>
        /// against the committed-recording vessel names in the scenario's
        /// trees. A match means the player has a pre-existing quicksave
        /// vessel sharing a name with a recording in the active tree — they
        /// will see both in-scene and almost always mistake the real vessel
        /// for a second ghost. WARN-log + ScreenMessage so the situation is
        /// diagnosable without reading KSP.log.
        /// </summary>
        internal static void WarnOnLeftAloneNameCollisions(PostLoadStripResult stripResult)
        {
            if (stripResult.LeftAloneNames == null || stripResult.LeftAloneNames.Count == 0)
                return;

            IEnumerable<string> treeNames = EnumerateCommittedVesselNames();
            var collisions = PostLoadStripper.FindTreeNameCollisions(
                stripResult.LeftAloneNames, treeNames);
            if (collisions == null || collisions.Count == 0)
                return;

            string joined = string.Join(", ", collisions.ToArray());
            ParsekLog.Warn(InvokeTag,
                $"Strip left {collisions.Count} pre-existing vessel(s) whose name matches a " +
                $"tree recording: [{joined}] — not related to the re-fly, will appear as " +
                $"second Kerbal X-shaped object in scene");
            ShowUserError(
                $"Heads up: pre-existing vessel(s) [{joined}] share a name with your re-fly " +
                $"tree. Any second Kerbal X in scene is NOT a Parsek ghost — it predates the rewind.");
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
        internal static List<uint> ResolveInPlaceContinuationDebrisToKill(
            ReFlySessionMarker marker,
            IReadOnlyList<RecordingTree> trees,
            IReadOnlyList<(uint pid, string vesselName)> leftAlonePids,
            HashSet<uint> protectedPids)
        {
            var kill = new List<uint>();
            if (marker == null) return kill;
            if (string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                || string.IsNullOrEmpty(marker.OriginChildRecordingId))
                return kill;
            if (!string.Equals(
                    marker.ActiveReFlyRecordingId,
                    marker.OriginChildRecordingId,
                    StringComparison.Ordinal))
                return kill; // placeholder pattern -- skip; the active vessel is alive in scene
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

            // Build the Destroyed-terminal name set.
            var destroyedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rec in markerTree.Recordings.Values)
            {
                if (rec == null) continue;
                if (rec.TerminalStateValue != TerminalState.Destroyed) continue;
                if (string.IsNullOrEmpty(rec.VesselName)) continue;
                destroyedNames.Add(rec.VesselName);
            }
            if (destroyedNames.Count == 0) return kill;

            for (int i = 0; i < leftAlonePids.Count; i++)
            {
                var (pid, name) = leftAlonePids[i];
                if (pid == 0u) continue;
                if (string.IsNullOrEmpty(name)) continue;
                if (protectedPids != null && protectedPids.Contains(pid)) continue;
                if (!destroyedNames.Contains(name)) continue;
                kill.Add(pid);
            }
            return kill;
        }

        /// <summary>
        /// Bug #587: production caller for <see cref="ResolveInPlaceContinuationDebrisToKill"/>.
        /// Runs after <see cref="AtomicMarkerWrite"/> (so the marker is set + the
        /// re-fly-active short-circuit in <c>RunSpawnDeathChecks</c> is engaged) and
        /// kills pre-existing debris that would confuse KSP-stock patched conics.
        /// Each <c>Vessel.Die()</c> runs inside a <see cref="SuppressionGuard.Crew"/>
        /// to mirror <c>PostLoadStripper.StripVessel</c>'s silent-removal contract --
        /// no CrewKilled / CrewRemoved fanout into the ledger from this cleanup.
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
            // in the public surface. Re-enumerate.
            IList<Vessel> liveVessels;
            try { liveVessels = FlightGlobals.Vessels; }
            catch { liveVessels = null; }
            if (liveVessels == null || liveVessels.Count == 0) return;

            var leftAlonePidNames = new List<(uint, string)>();
            for (int i = 0; i < liveVessels.Count; i++)
            {
                var v = liveVessels[i];
                if (v == null) continue;
                uint pid = v.persistentId;
                if (pid == 0u) continue;
                if (GhostMapPresence.IsGhostMapVessel(pid)) continue;
                // Skip vessels we just stripped (already dead) or selected.
                if (stripResult.StrippedPids != null
                    && stripResult.StrippedPids.Contains(pid)) continue;
                if (stripResult.SelectedPid == pid) continue;
                string name = v.vesselName;
                if (string.IsNullOrEmpty(name)) continue;
                leftAlonePidNames.Add((pid, name));
            }

            // Protect the selected slot vessel + the marker's active recording's pid
            // (#573 contract: never kill the actively re-flown vessel).
            var protectedPids = new HashSet<uint>();
            if (stripResult.SelectedPid != 0u)
                protectedPids.Add(stripResult.SelectedPid);
            // Also resolve the active recording's vessel pid from the committed list
            // -- same pid as Selected for in-place continuation, but defensive against
            // a future code-path that diverges them.
            var committedRecs = RecordingStore.CommittedRecordings;
            if (committedRecs != null && !string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
            {
                for (int i = 0; i < committedRecs.Count; i++)
                {
                    var rec = committedRecs[i];
                    if (rec == null) continue;
                    if (string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId, StringComparison.Ordinal))
                    {
                        if (rec.VesselPersistentId != 0u)
                            protectedPids.Add(rec.VesselPersistentId);
                        break;
                    }
                }
            }

            var kill = ResolveInPlaceContinuationDebrisToKill(
                marker,
                RecordingStore.CommittedTrees,
                leftAlonePidNames,
                protectedPids);
            if (kill.Count == 0) return;

            int killed = 0;
            var killedNames = new List<string>();
            for (int i = 0; i < liveVessels.Count; i++)
            {
                var v = liveVessels[i];
                if (v == null) continue;
                if (!kill.Contains(v.persistentId)) continue;
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
                    $"Strip post-supplement: killed {killed} pre-existing debris vessel(s) " +
                    $"for in-place continuation re-fly: [{joined}] " +
                    $"(name matches a Destroyed-terminal recording in tree '{marker.TreeId}'; " +
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
