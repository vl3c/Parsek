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
    /// <see cref="RewindLauncher.InitiateRewind"/> — the only known-good way to
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
    internal static partial class RewindInvoker
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
        /// Refusal reason for an RP whose UT is later than the current UT. It reaches the
        /// player only as the disabled Fly button's existing hover tooltip (Recordings
        /// table and Timeline), so it stays constant-length and inside the Timeline
        /// window's single-line strip budget (<c>TooltipEchoBudgetTests</c>); the UTs go
        /// to the log line instead.
        /// </summary>
        internal const string FutureRewindPointReason =
            "This separation is in your future - Re-Fly opens once the clock reaches it";

        /// <summary>
        /// Slack for "the clock has reached the RP". The RP stamps its UT before its
        /// quicksave is written, so loading that quicksave puts the clock at or after the
        /// RP UT; the slack only absorbs the round-trip of a UT through a save file.
        /// </summary>
        internal const double RewindPointFutureToleranceSeconds = 0.001;

        /// <summary>Test seam for the current UT the future-RP gate compares against.</summary>
        internal static Func<double> NowUtProviderForTesting;

        // ---------------------------------------------------------------------
        // Pre-invoke advisory (part-action audit item C2). Purely informational:
        // it names what the revert takes back before the player commits to it,
        // and it changes NO control flow — no extra button, no new refusal, no
        // mutation. Composition is pure/static so the exact player-facing strings
        // are pinned by unit tests (ReFlyPreInvokeAdvisoryTests).
        // ---------------------------------------------------------------------

        /// <summary>
        /// Maximum sibling-slot craft names enumerated in the advisory (dialog
        /// body and log line alike). Mirrors <see cref="MaxPreservedNamesLogged"/>
        /// and the project's batch-logging convention: cap the enumeration and
        /// report the remainder as a count.
        /// </summary>
        internal const int MaxAdvisorySlotNamesShown = 8;

        /// <summary>
        /// Width (px) passed to the Re-Fly confirmation <c>MultiOptionDialog</c>.
        /// The no-width constructor defaults to a 300 px <c>dialogRect</c>, which
        /// renders the advisory body as a tall narrow column; 420 px keeps it to a
        /// readable paragraph shape without dominating the screen.
        /// </summary>
        internal const float AdvisoryDialogWidth = 420f;

        /// <summary>
        /// Plain-English roll-up of the seven career facets
        /// <c>KspStatePatcher.PatchAll</c> re-applies after the rewind load
        /// (<c>KspStatePatcher.cs:87-93</c>: science, tech tree, funds,
        /// reputation, facilities, milestones, contracts). Everything else in the
        /// save is whatever the RP quicksave holds, i.e. it reverts to
        /// <c>rp.UT</c>. Single source for the advisory's "outside these" claim
        /// so the dialog and the log line cannot drift apart.
        /// </summary>
        internal const string PatchedCareerFacetsSummary =
            "science, tech tree, funds, reputation, facility upgrades, milestones and contracts";

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
            if (!CanInvoke(rp, selected, out string canInvokeReason))
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
                $"Re-Fly (Rewind-to-Separation) StartInvoke: sess={sessionId} rp={rp.RewindPointId} " +
                $"slot={selected.SlotIndex}. Re-fly from a RewindPoint, NOT a plain Rewind-to-Launch");

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

            // Step 1b: capture the pre-rewind live career economy (E_before) for the
            // rewind read-back divergence guard, BEFORE LoadGame overwrites the live
            // singletons with the loaded quicksave state. This is the "career state
            // sticks" witness; it survives LoadGame on the static RewindInvokeContext and
            // is auto-cleared when that context clears.
            EconomySnapshot readbackPreRewind = EconomySnapshot.Capture();

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
            RewindInvokeContext.ReadbackPreRewind = readbackPreRewind;

            // Step 4: trigger the load. Past this point, Unity will destroy the
            // current scenario; only the static context survives to the new
            // scenario's OnLoad, which calls ConsumePostLoad.
            try
            {
                ParsekLog.Info(InvokeTag,
                    $"Loading quicksave: tempPath='{tempPath}' loadName='{tempLoadName}' " +
                    $"saveFolder='{HighLogic.SaveFolder}'");
                // Parsek's time travel keeps the current difficulty settings (owner ruling Q1).
                ConfigNode liveParameters = DifficultySettingsCarry.CaptureLive("Re-Fly");
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
                DifficultySettingsCarry.ApplyTo(game, liveParameters, "Re-Fly");
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
        /// <para>
        /// <paramref name="loadedScenarioNode"/> is the OnLoad node, the RP quicksave's own
        /// ParsekScenario: its ROUTES give each kept route its loop position back after the
        /// bundle's route reconcile. Null leaves the reconcile's cursor reset standing.
        /// </para>
        /// </summary>
        // Test seams: override FlightGlobals-vessel readiness and the
        // onFlightReady subscription so headless tests can exercise both the
        // direct and the deferred Strip-Activate-Marker paths.
        internal static Func<bool> FlightReadyProbeOverrideForTesting;
        internal static Action<Action, string> DeferUntilFlightReadyOverrideForTesting;

        private static double SafeNow()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch { return 0.0; }
        }

        /// <summary>
        /// Cap on how many preserved vessel names the scrub summary names, per the
        /// batch-counting logging convention (one summary line, not one line per
        /// item - a large career can preserve dozens of vessels).
        /// </summary>
        private const int MaxPreservedNamesLogged = 8;

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

    }
}
