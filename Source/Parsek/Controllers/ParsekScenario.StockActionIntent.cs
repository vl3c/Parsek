using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Stock-action intent and switch-segment session markers the scenario owns: arm, clear and the
    // load-time freshness check.
    public partial class ParsekScenario
    {
        /// <summary>
        /// Pending stock-action intent armed by a Tracking Station Fly / KSC
        /// marker Fly / Map Switch-To click and not yet consumed by the
        /// FLIGHT-side scene-load tail. Null when no UI click is in flight.
        /// </summary>
        internal StockActionIntentMarker CurrentStockActionIntent => activeStockActionIntent;

        /// <summary>
        /// Active switch-segment session (live attempt or pending-after-reload).
        /// Null outside an active switch-segment attempt.
        /// </summary>
        internal SwitchSegmentSession ActiveSwitchSegmentSession => activeSwitchSegmentSession;

        /// <summary>
        /// Arms a new stock-action intent marker. Replaces any previously
        /// armed marker (which is then logged as superseded). Idempotent
        /// on a structurally identical marker; callers must rebuild the
        /// marker rather than mutate an armed one in place.
        /// </summary>
        internal void ArmStockActionIntent(StockActionIntentMarker marker)
        {
            if (marker == null) throw new ArgumentNullException(nameof(marker));
            if (activeStockActionIntent != null)
            {
                ParsekLog.Info("SwitchIntent",
                    $"intent superseded: prior intentId={activeStockActionIntent.IntentId:D} " +
                    $"action={activeStockActionIntent.Action} reason=stale-intent-superseded " +
                    $"new intentId={marker.IntentId:D} action={marker.Action}");
            }
            activeStockActionIntent = marker;
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info("SwitchIntent",
                $"armed: intentId={marker.IntentId:D} action={marker.Action} " +
                $"targetPid={marker.TargetVesselPersistentId.ToString(ic)} " +
                $"sourceScene={marker.SourceScene} " +
                $"capturedUT={marker.CapturedUT.ToString("R", ic)} " +
                $"capturedRealtime={marker.CapturedRealtime.ToString("R", ic)}");
        }

        /// <summary>
        /// Clears the current stock-action intent marker with a reason.
        /// Idempotent: clearing with no marker armed logs a Verbose line
        /// and returns.
        /// </summary>
        internal void ClearStockActionIntent(string reason)
        {
            if (activeStockActionIntent == null)
            {
                ParsekLog.Verbose("SwitchIntent",
                    $"clear no-op: no marker armed (reason={reason ?? "<none>"})");
                return;
            }
            var marker = activeStockActionIntent;
            activeStockActionIntent = null;
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info("SwitchIntent",
                $"cleared: intentId={marker.IntentId:D} action={marker.Action} " +
                $"targetPid={marker.TargetVesselPersistentId.ToString(ic)} " +
                $"reason={reason ?? "<none>"}");
        }

        /// <summary>
        /// Arms an active switch-segment session. Replaces any previously
        /// armed session (which is logged as superseded).
        /// </summary>
        internal void ArmSwitchSegmentSession(SwitchSegmentSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (activeSwitchSegmentSession != null)
            {
                ParsekLog.Info("SwitchSegment",
                    $"session superseded: prior sessionId={activeSwitchSegmentSession.SessionId:D} " +
                    $"reason=session-superseded new sessionId={session.SessionId:D}");
            }
            activeSwitchSegmentSession = session;
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info("SwitchSegment",
                $"armed: sessionId={session.SessionId:D} intentId={session.IntentId:D} " +
                $"entryReason={session.EntryReason} " +
                $"treeId={session.TreeId ?? "<null>"} " +
                $"parentRecordingId={session.ParentRecordingId ?? "<null>"} " +
                $"activeSegmentRecordingId={session.ActiveSegmentRecordingId ?? "<null>"} " +
                $"sourcePid={session.SourceVesselPersistentId.ToString(ic)} " +
                $"focusedPid={session.FocusedVesselPersistentId.ToString(ic)} " +
                $"switchUT={session.SwitchUT.ToString("R", ic)}");
        }

        /// <summary>
        /// Clears the active switch-segment session with a reason.
        /// Idempotent: clearing with no session armed logs Verbose only.
        /// </summary>
        internal void ClearSwitchSegmentSession(string reason)
        {
            if (activeSwitchSegmentSession == null)
            {
                ParsekLog.Verbose("SwitchSegment",
                    $"clear no-op: no session armed (reason={reason ?? "<none>"})");
                return;
            }
            var session = activeSwitchSegmentSession;
            activeSwitchSegmentSession = null;
            ParsekLog.Info("SwitchSegment",
                $"cleared: sessionId={session.SessionId:D} intentId={session.IntentId:D} " +
                $"entryReason={session.EntryReason} reason={reason ?? "<none>"}");
        }

        /// <summary>
        /// Called by <see cref="LoadRewindStagingState"/> right after the intent
        /// marker is loaded. Applies the documented cross-run / TTL / UT
        /// regression staleness checks and clears the marker with the
        /// appropriate reason on a miss. Fresh markers stay armed for the
        /// future Phase C consume site to act on.
        /// </summary>
        private void ValidateLoadedStockActionIntentFreshness()
        {
            if (activeStockActionIntent == null)
                return;
            float currentRealtime = Time.realtimeSinceStartup;
            double currentUT = Planetarium.fetch != null
                ? Planetarium.GetUniversalTime()
                : activeStockActionIntent.CapturedUT;
            var classification = StockActionIntentMarker.EvaluateStaleness(
                activeStockActionIntent,
                ParsekProcess.ProcessSessionId,
                currentRealtime,
                currentUT);
            switch (classification)
            {
                case StockActionIntentStaleness.StaleCrossRun:
                    ClearStockActionIntent("stale-cross-run");
                    return;
                case StockActionIntentStaleness.StaleIntentTtlExpired:
                case StockActionIntentStaleness.StaleIntentUtRegressed:
                    ClearStockActionIntent("stale-intent");
                    return;
                case StockActionIntentStaleness.Fresh:
                default:
                    ParsekLog.Info("SwitchIntent",
                        $"intent kept-armed on OnLoad: intentId={activeStockActionIntent.IntentId:D} " +
                        $"action={activeStockActionIntent.Action} " +
                        $"targetPid={activeStockActionIntent.TargetVesselPersistentId}");
                    return;
            }
        }
    }
}
