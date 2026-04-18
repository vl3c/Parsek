using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Parsek
{
    /// <summary>
    /// Which stock revert entry point the player clicked. Both go through
    /// <see cref="RevertInterceptor"/>'s shared dispatcher; the callback
    /// handlers branch on this value so Full Revert drives the same stock
    /// method the player originally requested.
    /// </summary>
    internal enum RevertTarget
    {
        /// <summary>Esc &gt; Revert to Launch, or the flight-results Revert-to-Launch button. Routes through <see cref="FlightDriver.RevertToLaunch"/>.</summary>
        Launch,
        /// <summary>Esc &gt; Revert to VAB / Revert to SPH, or the flight-results equivalent. Routes through <see cref="FlightDriver.RevertToPrelaunch"/>.</summary>
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
    ///   <item><description><see cref="FullRevertHandler"/> — invokes <see cref="TreeDiscardPurge.PurgeTree"/> which already clears the marker + journal, then re-runs the stock revert method the player originally clicked (<see cref="FlightDriver.RevertToLaunch"/> for <see cref="RevertTarget.Launch"/>, <see cref="FlightDriver.RevertToPrelaunch"/> for <see cref="RevertTarget.Prelaunch"/>). Now with no active session the prefix lets it through.</description></item>
    ///   <item><description><see cref="CancelHandler"/> — pure logging; no state changes.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class RevertInterceptor
    {
        private const string SessionTag = "ReFlySession";
        private const string PatchTag = "RevertInterceptor";

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
        /// Test seam: set to non-null in unit tests to observe the Full Revert
        /// handler re-triggering <see cref="FlightDriver.RevertToLaunch"/>
        /// (Harmony will otherwise route back into the prefix and allow it,
        /// but the test environment has no FlightDriver statics).
        /// </summary>
        internal static Action StockRevertInvokerForTesting;

        /// <summary>
        /// Test seam: set to non-null in unit tests to observe the Full Revert
        /// handler re-triggering <see cref="FlightDriver.RevertToPrelaunch"/>
        /// (same reasoning as <see cref="StockRevertInvokerForTesting"/>).
        /// </summary>
        internal static Action StockRevertToPrelaunchInvokerForTesting;

        /// <summary>Clears all Phase 12 test seams.</summary>
        internal static void ResetTestOverrides()
        {
            DialogShowForTesting = null;
            RewindInvokeStartForTesting = null;
            StockRevertInvokerForTesting = null;
            StockRevertToPrelaunchInvokerForTesting = null;
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
        /// Captured into the Full Revert closure so the re-dispatched stock
        /// <see cref="FlightDriver.RevertToPrelaunch"/> lands the player in the
        /// correct editor. Ignored when <paramref name="target"/> is
        /// <see cref="RevertTarget.Launch"/>.</param>
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
                    onFullRevert: () => FullRevertHandler(capturedMarker, capturedTarget, capturedFacility),
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
                scenario.BumpSupersedeStateVersion();
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
        /// Full Revert path: purge the tree (which already clears the marker
        /// and aborts any in-flight merge journal), then re-trigger whichever
        /// stock revert method the player originally clicked —
        /// <see cref="FlightDriver.RevertToLaunch"/> for
        /// <see cref="RevertTarget.Launch"/> or
        /// <see cref="FlightDriver.RevertToPrelaunch"/> for
        /// <see cref="RevertTarget.Prelaunch"/>. With the marker cleared,
        /// <see cref="Prefix"/> returns true and the stock revert executes.
        /// </summary>
        internal static void FullRevertHandler(
            ReFlySessionMarker marker,
            RevertTarget target = RevertTarget.Launch,
            EditorFacility facility = EditorFacility.VAB)
        {
            if (marker == null)
            {
                ParsekLog.Warn(SessionTag, $"FullRevertHandler: null marker target={target} — cannot full-revert");
                return;
            }

            string sessionId = marker.SessionId ?? "<no-id>";
            string treeId = marker.TreeId;

            if (string.IsNullOrEmpty(treeId))
            {
                ParsekLog.Warn(SessionTag,
                    $"FullRevertHandler: marker sess={sessionId} target={target} has empty TreeId — " +
                    "cannot purge tree; clearing marker only");
                var scenario = ParsekScenario.Instance;
                if (!ReferenceEquals(null, scenario))
                {
                    scenario.ActiveReFlySessionMarker = null;
                    scenario.BumpSupersedeStateVersion();
                }
            }
            else
            {
                TreeDiscardPurge.PurgeTree(treeId);
            }

            ParsekLog.Info(SessionTag,
                $"End reason=fullRevert sess={sessionId} tree={treeId ?? "<none>"} target={target}" +
                (target == RevertTarget.Prelaunch ? $" facility={facility}" : string.Empty));

            // Now drive the stock revert: marker is cleared, so the
            // interceptor's prefix will allow it through. Route to the
            // method matching the revert button the player originally
            // clicked so they land in the scene they asked for.
            if (target == RevertTarget.Prelaunch)
            {
                var prelaunchHook = StockRevertToPrelaunchInvokerForTesting;
                if (prelaunchHook != null)
                {
                    prelaunchHook();
                    return;
                }

                try
                {
                    FlightDriver.RevertToPrelaunch(facility);
                }
                catch (Exception ex)
                {
                    ParsekLog.Error(PatchTag,
                        $"FullRevertHandler: FlightDriver.RevertToPrelaunch threw: {ex.GetType().Name}: {ex.Message}");
                }
                return;
            }

            var stockHook = StockRevertInvokerForTesting;
            if (stockHook != null)
            {
                stockHook();
                return;
            }

            try
            {
                FlightDriver.RevertToLaunch();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(PatchTag,
                    $"FullRevertHandler: FlightDriver.RevertToLaunch threw: {ex.GetType().Name}: {ex.Message}");
            }
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
    /// <c>facility</c> argument so the Full Revert closure can re-dispatch
    /// stock <see cref="FlightDriver.RevertToPrelaunch"/> with the same
    /// editor target the player originally clicked.
    /// </summary>
    [HarmonyPatch(typeof(FlightDriver), nameof(FlightDriver.RevertToPrelaunch))]
    internal static class RevertToPrelaunchInterceptor
    {
        [HarmonyPrefix]
        internal static bool Prefix(EditorFacility facility) =>
            RevertInterceptor.Prefix(RevertTarget.Prelaunch, facility);
    }
}
