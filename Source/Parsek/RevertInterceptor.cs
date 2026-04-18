using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Parsek
{
    /// <summary>
    /// Phase 12 of Rewind-to-Staging (design §6.7): intercepts
    /// <see cref="FlightDriver.RevertToLaunch"/> when a re-fly session is
    /// active and routes the player into <see cref="ReFlyRevertDialog"/>
    /// instead of running the stock revert.
    ///
    /// <para>
    /// The interceptor is a Harmony prefix on <c>FlightDriver.RevertToLaunch</c>.
    /// When <see cref="ParsekScenario.ActiveReFlySessionMarker"/> is null the
    /// prefix returns <c>true</c> and the stock revert runs unchanged. When the
    /// marker is non-null, the prefix returns <c>false</c> (blocking stock
    /// revert) and spawns the 3-option dialog. Each dialog branch wires to a
    /// static handler method on this class:
    ///
    /// <list type="bullet">
    ///   <item><description><see cref="RetryHandler"/> — clears the marker, generates a fresh <see cref="Guid"/> session id, and re-invokes <see cref="RewindInvoker.StartInvoke"/> with the same RP + slot captured from the marker. The old provisional becomes a zombie that the load-time sweep (Phase 13) cleans up.</description></item>
    ///   <item><description><see cref="FullRevertHandler"/> — invokes <see cref="TreeDiscardPurge.PurgeTree"/> which already clears the marker + journal, then re-runs <see cref="FlightDriver.RevertToLaunch"/> (now with no active session so the prefix lets it through).</description></item>
    ///   <item><description><see cref="CancelHandler"/> — pure logging; no state changes.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    [HarmonyPatch(typeof(FlightDriver), nameof(FlightDriver.RevertToLaunch))]
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
        /// handler re-triggering the stock revert (Harmony will otherwise
        /// route back into the prefix and allow it, but the test environment
        /// has no FlightDriver statics).
        /// </summary>
        internal static Action StockRevertInvokerForTesting;

        /// <summary>Clears all Phase 12 test seams.</summary>
        internal static void ResetTestOverrides()
        {
            DialogShowForTesting = null;
            RewindInvokeStartForTesting = null;
            StockRevertInvokerForTesting = null;
        }

        /// <summary>
        /// Harmony prefix on <c>FlightDriver.RevertToLaunch</c>. Returning
        /// <c>false</c> blocks the stock method body; returning <c>true</c>
        /// lets it run. Gate is simple:
        /// <c>ParsekScenario.Instance?.ActiveReFlySessionMarker != null</c>.
        /// </summary>
        [HarmonyPrefix]
        internal static bool Prefix()
        {
            if (!ShouldBlock(out var marker))
            {
                ParsekLog.Verbose(PatchTag,
                    "Prefix: no active re-fly session — allowing stock RevertToLaunch");
                return true;
            }

            string sessionId = marker.SessionId ?? "<no-id>";
            ParsekLog.Info(PatchTag,
                $"Prefix: blocking stock RevertToLaunch sess={sessionId} — showing re-fly dialog");

            var dialogHook = DialogShowForTesting;
            if (dialogHook != null)
            {
                dialogHook(marker);
            }
            else
            {
                // Capture the marker fields the Retry path needs at dialog
                // spawn time; the marker may be cleared by the time a callback
                // fires (Retry mutates it mid-invocation).
                var capturedMarker = marker;
                ReFlyRevertDialog.Show(
                    marker,
                    onRetry: () => RetryHandler(capturedMarker),
                    onFullRevert: () => FullRevertHandler(capturedMarker),
                    onCancel: () => CancelHandler(capturedMarker));
            }

            return false;
        }

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
        /// </summary>
        internal static void RetryHandler(ReFlySessionMarker marker)
        {
            if (marker == null)
            {
                ParsekLog.Warn(SessionTag, "RetryHandler: null marker — cannot retry");
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
                    $"RetryHandler: cannot resolve rp={rpId} for sess={oldSessionId} — aborting retry");
                ReFlyRevertDialog.ClearLock();
                return;
            }

            ChildSlot slot = FindSlotForMarker(rp, marker);
            if (slot == null)
            {
                ParsekLog.Error(SessionTag,
                    $"RetryHandler: cannot resolve slot for origin={marker.OriginChildRecordingId ?? "<none>"} " +
                    $"in rp={rpId} sess={oldSessionId} — aborting retry");
                ReFlyRevertDialog.ClearLock();
                return;
            }

            ParsekLog.Info(SessionTag,
                $"End reason=retry sess={oldSessionId} rp={rpId} slot={slot.SlotIndex}");

            // Clear the active marker so the new StartInvoke precondition
            // (§7.5) sees no active session. The provisional recording stays
            // in the committed list and will be swept as a zombie.
            var scenario = ParsekScenario.Instance;
            if (!ReferenceEquals(null, scenario))
            {
                scenario.ActiveReFlySessionMarker = null;
                scenario.BumpSupersedeStateVersion();
                ParsekLog.Verbose(SessionTag,
                    $"RetryHandler: marker cleared for sess={oldSessionId}; re-invoking rewind");
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
        /// and aborts any in-flight merge journal), then re-trigger
        /// <see cref="FlightDriver.RevertToLaunch"/>. With the marker cleared,
        /// <see cref="Prefix"/> returns true and the stock revert executes.
        /// </summary>
        internal static void FullRevertHandler(ReFlySessionMarker marker)
        {
            if (marker == null)
            {
                ParsekLog.Warn(SessionTag, "FullRevertHandler: null marker — cannot full-revert");
                return;
            }

            string sessionId = marker.SessionId ?? "<no-id>";
            string treeId = marker.TreeId;

            if (string.IsNullOrEmpty(treeId))
            {
                ParsekLog.Warn(SessionTag,
                    $"FullRevertHandler: marker sess={sessionId} has empty TreeId — " +
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
                $"End reason=fullRevert sess={sessionId} tree={treeId ?? "<none>"}");

            // Now drive the stock revert: marker is cleared, so the
            // interceptor's prefix will allow it through.
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
        internal static void CancelHandler(ReFlySessionMarker marker)
        {
            string sessionId = marker?.SessionId ?? "<no-id>";
            ParsekLog.Info(SessionTag, $"Revert dialog cancelled sess={sessionId}");
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
}
