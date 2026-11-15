using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    // The post-load phase: ConsumePostLoad, the abandoned restore attempt discard and the flight-ready probe, deferral and wait coroutine.
    internal static partial class RewindInvoker
    {
        internal static void ConsumePostLoad(ConfigNode loadedScenarioNode = null)
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
            // Rec-1 (logistics<->time-rewind determinism): pass the POST-LOAD live UT
            // (= the loaded quicksave UT, computed just below) as the route-row retire
            // cutoff so abandoned-future free-standing route ledger rows are dropped here
            // (SUCCESS path only) — NOT rp.UT, which is a frame too early (see the inner
            // comment / edge-case finding #4). The failed-load rollback (TryRestoreBundle)
            // uses the parameterless overload, so it retires nothing and the pre-rewind
            // ledger is restored intact.
            // Rec-1's route-retire cutoff, hoisted so the #15 resurrected-recovery
            // retirement downstream reuses the identical value rather than recomputing a
            // slightly different one after the onFlightReady deferral.
            double liveUTForCutoff = SafeNow();
            double retireCutoffUT = liveUTForCutoff > 0.0 ? liveUTForCutoff : rp.UT;

            // The RP quicksave's own route copy and clock, for the loop-position restore that
            // follows the bundle's route reconcile. A failed read leaves the cursor reset.
            List<Logistics.Route> loadedSaveRoutes = null;
            double loadedSaveUT = double.NaN;
            double loadedClockUT = double.NaN;
            double loadedFloorUT = double.NaN;
            List<Logistics.RouteEvent> loadedSaveEvents = null;
            if (hasBundle && loadedScenarioNode != null)
            {
                try
                {
                    loadedSaveRoutes = Logistics.RouteStore.ReadSavedCommittedRoutes(loadedScenarioNode);
                    loadedClockUT = KerbalsModule.ReadLoadedSaveUT();
                    // An RP quicksave written inside a go-back rewind's lead-time window carries
                    // that rewind's route state floor: its route copy is as of the floor.
                    loadedFloorUT = Logistics.RouteStore.ReadSavedRouteStateFloorUT(loadedScenarioNode);
                    loadedSaveUT = Logistics.RouteLoadReconcile.ApplyRouteStateFloor(loadedClockUT, loadedFloorUT);
                    loadedSaveEvents = Logistics.RouteEventLog.ReadSavedEvents(loadedScenarioNode);
                }
                catch (Exception ex)
                {
                    loadedSaveRoutes = null;
                    loadedSaveEvents = null;
                    ParsekLog.Warn(InvokeTag,
                        $"ConsumePostLoad: reading the RP quicksave's route copy threw {ex.GetType().Name}: " +
                        $"{ex.Message}; route loop cursors stay reset");
                }
            }

            if (hasBundle)
            {
                try
                {
                    // Rec-1 route-row retire cutoff = the UT the WORLD actually reverted
                    // to (the loaded quicksave's UT), which at this post-load point IS the
                    // live UT — the universe just loaded at it. This is preferred over
                    // rp.UT: rp.UT is captured at RewindPointAuthor.Begin one frame BEFORE
                    // the deferred quicksave save, so it is ~one frame EARLIER than the UT
                    // the .sfs embeds; using it could drop a route row in the
                    // (rp.UT, quicksaveUT] window whose physical effect IS in the reverted
                    // world, which the re-fly would then double-apply (edge-case review
                    // finding #4). Fall back to rp.UT if the live UT is unavailable
                    // (SafeNow returns 0.0 on failure).
                    double liveUT = liveUTForCutoff;
                    ParsekLog.Info(InvokeTag,
                        $"ConsumePostLoad: restoring bundle with route-retire cutoffUT={retireCutoffUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} (liveUT={liveUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, rp.UT={rp.UT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)})");
                    ReconciliationBundle.Restore(
                        bundle, retireCutoffUT, loadedSaveRoutes, loadedSaveUT,
                        loadedClockUT, loadedSaveEvents);
                    // Live-only income (design 3.15 step 9): the bundle was captured before the
                    // invoke, so it brings back the income OnLoad's drop just removed.
                    LiveOnlyIncome.DropAfterReFlyBundleRestore(loadedClockUT, retireCutoffUT);
                    if (loadedSaveRoutes != null)
                        Logistics.RouteLoadReconcile.InstallFloorAfterLoad(
                            loadedClockUT, loadedFloorUT, InvokeTag, "ConsumePostLoad");
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

            // The pre-rewind flight is gone with the scene: drop a copy-on-write restore
            // attempt it armed (CI-3), after the bundle restore so its purge sticks.
            DiscardRestoreAttemptOfAbandonedFlight(sessionId);

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
            //
            // Readiness is asserted against the SELECTED SLOT's pids, not against a
            // non-empty vessel list: the preserved fleet makes Vessels.Count > 0 true
            // before the slot's own vessel exists (#16 item 6).
            ReFlySlotPidSets readinessPids = BuildSlotPidSets(rp, slotIdx);
            if (IsFlightReady(readinessPids))
            {
                RunStripActivateMarker(rp, selected, sessionId, slotIdx, tempPath, retireCutoffUT);
            }
            else
            {
                ParsekLog.Info(InvokeTag,
                    $"ConsumePostLoad deferred to onFlightReady: sess={sessionId} " +
                    $"rp={rp.RewindPointId} slot={slotIdx} " +
                    $"selectedSlotUnmapped={readinessPids.SelectedSlotIsUnmapped} " +
                    "(selected slot's vessel not yet present after async scene load)");
                // Pass tempPath separately so the timeout branch in
                // `WaitForFlightReadyAndInvoke` can delete the root-level
                // Parsek_Rewind_*.sfs copy without reaching the action's
                // finally block. Otherwise a catastrophic flight-scene load
                // that never fires onFlightReady would leak the temp file
                // and leave RewindInvokeContext half-cleared.
                DeferUntilFlightReady(
                    () => RunStripActivateMarker(rp, selected, sessionId, slotIdx, tempPath, retireCutoffUT),
                    tempPath,
                    readinessPids);
            }
        }

        /// <summary>
        /// Discards a committed-tree restore attempt still armed when a Re-Fly's rewind point
        /// has loaded. The attempt guards the copy-on-write clone of the flight that was live
        /// before the rewind (<c>ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore</c>);
        /// the rewind replaced that flight, but the attempt is static and outlives the scene.
        /// Left armed it would treat the session tree's committed recordings as the stale
        /// attempt's members, holding back the session's events and writing the attempt's
        /// tree id into every save, so a save-file load re-arms an attempt nobody flies
        /// (CI-3). The clone's post-cutoff
        /// event tails are purged with it, as on every other discard of the clone. No-op when
        /// nothing is armed. Returns true when an attempt was discarded.
        /// </summary>
        internal static bool DiscardRestoreAttemptOfAbandonedFlight(string sessionId)
        {
            string armedTreeId = RecordingStore.CommittedTreeRestoreAttemptTreeId;
            if (string.IsNullOrEmpty(armedTreeId))
                return false;
            ParsekLog.Info(InvokeTag,
                $"ConsumePostLoad: discarding the committed-tree restore attempt of the pre-rewind " +
                $"flight tree={armedTreeId} sess={sessionId ?? "<none>"}");
            // The Re-Fly load reverted KSP to the rewind point: nothing of the attempt stays.
            RecordingStore.DiscardCommittedTreeRestoreAttempt(
                "refly-start: pre-rewind flight abandoned",
                preserveIrreversibleTails: false);
            return true;
        }

        /// <summary>
        /// Readiness gate for the Strip / Activate / Marker critical section.
        ///
        /// <para>
        /// <c>Vessels.Count &gt; 0</c> was a sufficient proxy for "the save's vessels are
        /// loaded" only while a Re-Fly load produced a ONE-vessel world. With the fleet
        /// preserved, the preserved population can be live several frames before the selected
        /// slot's own vessel is, so a non-empty list no longer implies the thing the caller
        /// actually needs; Strip then finds no candidate and the invocation bails with
        /// "Activate failed: selected vessel not present on reload". This now asserts the
        /// SELECTED SLOT specifically, by vessel pid or root-part pid (the same two keys
        /// <see cref="ClassifySlotAffinity"/> matches on).
        /// </para>
        /// <para>
        /// An RP whose selected slot maps no pid at all
        /// (<see cref="ReFlySlotPidSets.SelectedSlotIsUnmapped"/>) keeps the old count-only
        /// answer: there is nothing to assert, and refusing would turn a strip that declines
        /// cleanly into a wait that runs out the deferral budget first.
        /// </para>
        /// </summary>
        private static bool IsFlightReady(ReFlySlotPidSets selectedSlotPids)
        {
            if (FlightReadyProbeOverrideForTesting != null)
                return FlightReadyProbeOverrideForTesting();

            try
            {
                return IsFlightReadyCore(selectedSlotPids);
            }
            catch
            {
                return false;
            }
        }

        // NoInlining keeps the FlightGlobals references out of IsFlightReady's own
        // JIT unit: mono initializes FlightGlobals when compiling a method that
        // calls its statics, and a failed initializer (headless xUnit) would then
        // surface at IsFlightReady's CALLER, outside the try above. Same pattern
        // as RecordingStore.ReadUnityApplicationIsPlayingCore.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool IsFlightReadyCore(ReFlySlotPidSets selectedSlotPids)
        {
            if (!FlightGlobals.ready) return false;
            var vessels = FlightGlobals.Vessels;
            if (vessels == null || vessels.Count == 0) return false;
            if (selectedSlotPids.SelectedSlotIsUnmapped) return true;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null) continue;
                uint rootPartPid = 0u;
                Part root = v.rootPart;
                if (root != null) rootPartPid = root.persistentId;
                if (SelectedSlotVesselIsPresent(v.persistentId, rootPartPid, selectedSlotPids))
                    return true;
            }
            return false;
        }

        private static void DeferUntilFlightReady(
            Action action, string tempPath, ReFlySlotPidSets selectedSlotPids)
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
            // `FlightGlobals.ready` plus the SELECTED SLOT's own vessel flips true
            // (a non-empty vessel list is no longer proof of that - see IsFlightReady).
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

            scenario.StartCoroutine(
                WaitForFlightReadyAndInvoke(action, tempPath, selectedSlotPids));
        }

        private static System.Collections.IEnumerator WaitForFlightReadyAndInvoke(
            Action action, string tempPath, ReFlySlotPidSets selectedSlotPids)
        {
            // Bound the wait so a scene-load that never finishes (catastrophic
            // failure) doesn't leak the coroutine. 300 frames at 60 fps is 5 s,
            // well past the ~1.4 s observed async-load completion.
            const int MaxFrames = 300;
            int frame = 0;
            while (frame < MaxFrames && !IsFlightReady(selectedSlotPids))
            {
                frame++;
                yield return null;
            }

            if (!IsFlightReady(selectedSlotPids))
            {
                ParsekLog.Error(InvokeTag,
                    $"Deferred flight-ready wait timed out after {MaxFrames} frames — rewind aborted " +
                    $"(selected slot's vessel never appeared; selectedSlotUnmapped=" +
                    $"{selectedSlotPids.SelectedSlotIsUnmapped})");
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
    }
}
