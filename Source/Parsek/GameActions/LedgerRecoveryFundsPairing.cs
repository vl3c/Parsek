using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Helper for pairing vessel-recovery callbacks with FundsChanged(VesselRecovery).
    /// Kept behind LedgerOrchestrator wrappers so external call sites stay stable.
    /// </summary>
    internal static class LedgerRecoveryFundsPairing
    {
        private const string Tag = "LedgerOrchestrator";
        private const double LegacyRecoveryActionAmountTolerance = 0.01;

        private struct PendingRecoveryFundsRequest
        {
            public double Ut;
            public RecoveredVesselIdentity Identity;
            public bool FromTrackingStation;
            public VesselType VesselType;
            public RecoveryPayoutContext PayoutContext;

            public string VesselName => Identity.DisplayName;
        }

        private static readonly HashSet<string> consumedRecoveryEventKeys = new HashSet<string>();
        private static readonly List<PendingRecoveryFundsRequest> pendingRecoveryFunds =
            new List<PendingRecoveryFundsRequest>();

        internal static int PendingRecoveryFundsCountForTesting => pendingRecoveryFunds.Count;

        internal static void ResetForTesting()
        {
            consumedRecoveryEventKeys.Clear();
            pendingRecoveryFunds.Clear();
        }

        internal static void ClearConsumedRecoveryEventKeys()
        {
            consumedRecoveryEventKeys.Clear();
        }

        internal static string BuildRecoveryEventDedupKey(GameStateEvent e)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2:R}|{3:R}|{4:R}|{5}",
                e.eventType,
                e.key ?? "",
                e.ut,
                e.valueBefore,
                e.valueAfter,
                e.recordingId ?? "");
        }

        internal static bool TryFindRecoveryFundsEvent(
            IReadOnlyList<GameStateEvent> events,
            double ut,
            bool skipConsumed,
            out GameStateEvent matched,
            out string dedupKey)
        {
            return TryFindRecoveryFundsEvent(
                events,
                ut,
                skipConsumed,
                default(RecoveredVesselIdentity),
                out matched,
                out dedupKey);
        }

        internal static bool TryFindRecoveryFundsEvent(
            IReadOnlyList<GameStateEvent> events,
            double ut,
            bool skipConsumed,
            RecoveredVesselIdentity preferredIdentity,
            out GameStateEvent matched,
            out string dedupKey)
        {
            matched = default;
            dedupKey = null;
            if (events == null)
                return false;

            if (preferredIdentity.HasName &&
                TryFindRecoveryFundsEventCore(
                    events,
                    ut,
                    skipConsumed,
                    preferredIdentity,
                    matchMode: RecoveryEventIdentityMatchMode.RequireMatchingIdentity,
                    out matched,
                    out dedupKey))
            {
                return true;
            }

            if (preferredIdentity.HasName)
            {
                return TryFindRecoveryFundsEventCore(
                    events,
                    ut,
                    skipConsumed,
                    preferredIdentity,
                    matchMode: RecoveryEventIdentityMatchMode.RequireNoIdentity,
                    out matched,
                    out dedupKey);
            }

            return TryFindRecoveryFundsEventCore(
                events,
                ut,
                skipConsumed,
                preferredIdentity,
                matchMode: RecoveryEventIdentityMatchMode.Any,
                out matched,
                out dedupKey);
        }

        private enum RecoveryEventIdentityMatchMode
        {
            Any,
            RequireMatchingIdentity,
            RequireNoIdentity
        }

        private static bool TryFindRecoveryFundsEventCore(
            IReadOnlyList<GameStateEvent> events,
            double ut,
            bool skipConsumed,
            RecoveredVesselIdentity preferredIdentity,
            RecoveryEventIdentityMatchMode matchMode,
            out GameStateEvent matched,
            out string dedupKey)
        {
            matched = default;
            dedupKey = null;

            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                if (e.eventType != GameStateEventType.FundsChanged) continue;
                if (e.key != LedgerOrchestrator.VesselRecoveryReasonKey) continue;
                if (Math.Abs(e.ut - ut) > LedgerOrchestrator.VesselRecoveryEventEpsilonSeconds) continue;

                string candidateKey = BuildRecoveryEventDedupKey(e);
                if (skipConsumed && consumedRecoveryEventKeys.Contains(candidateKey)) continue;
                if (!RecoveryEventSatisfiesIdentityMode(e, preferredIdentity, matchMode)) continue;

                matched = e;
                dedupKey = candidateKey;
                return true;
            }

            return false;
        }

        internal static void OnRecoveryFundsEventRecorded(
            GameStateEvent evt,
            Func<double, RecoveredVesselIdentity, bool, bool> tryAddRecoveryFundsAction)
        {
            if (pendingRecoveryFunds.Count == 0)
                return;

            RecoveredVesselIdentity eventIdentity =
                RecoveryPayoutContextStore.ExtractIdentityFromFundsEventDetail(evt.detail);

            int bestIndex = FindBestPairingIndex(evt.ut, eventIdentity);
            if (bestIndex < 0)
                return;

            var request = pendingRecoveryFunds[bestIndex];
            if (tryAddRecoveryFundsAction(
                    request.Ut,
                    request.Identity,
                    request.FromTrackingStation))
            {
                pendingRecoveryFunds.RemoveAt(bestIndex);
            }
        }

        internal static int FindBestPairingIndex(double eventUt, string eventVesselName)
        {
            return FindBestPairingIndex(
                eventUt,
                RecoveryPayoutContextStore.ExtractIdentityFromFundsEventDetail(eventVesselName));
        }

        internal static int FindBestPairingIndex(
            double eventUt,
            RecoveredVesselIdentity eventIdentity)
        {
            if (pendingRecoveryFunds.Count == 0)
                return -1;

            bool haveName = eventIdentity.HasName;

            int nameMatchBestIndex = -1;
            double nameMatchBestDistance = double.MaxValue;
            int nameMatchTies = 0;
            if (haveName)
            {
                for (int i = 0; i < pendingRecoveryFunds.Count; i++)
                {
                    if (!pendingRecoveryFunds[i].Identity.Matches(eventIdentity))
                        continue;

                    double distance = Math.Abs(pendingRecoveryFunds[i].Ut - eventUt);
                    if (distance > LedgerOrchestrator.VesselRecoveryEventEpsilonSeconds) continue;

                    if (distance < nameMatchBestDistance)
                    {
                        nameMatchBestIndex = i;
                        nameMatchBestDistance = distance;
                        nameMatchTies = 1;
                    }
                    else if (distance == nameMatchBestDistance)
                    {
                        nameMatchTies++;
                    }
                }
            }

            if (nameMatchBestIndex >= 0)
            {
                if (nameMatchTies > 1)
                {
                    WarnPairingCandidateTie(eventUt, eventIdentity, byNameMatch: true,
                        bestDistance: nameMatchBestDistance);
                }
                return nameMatchBestIndex;
            }

            if (haveName)
                return -1;

            int fallbackBestIndex = -1;
            double fallbackBestDistance = double.MaxValue;
            int fallbackTies = 0;
            for (int i = 0; i < pendingRecoveryFunds.Count; i++)
            {
                double distance = Math.Abs(pendingRecoveryFunds[i].Ut - eventUt);
                if (distance > LedgerOrchestrator.VesselRecoveryEventEpsilonSeconds) continue;

                if (distance < fallbackBestDistance)
                {
                    fallbackBestIndex = i;
                    fallbackBestDistance = distance;
                    fallbackTies = 1;
                }
                else if (distance == fallbackBestDistance)
                {
                    fallbackTies++;
                }
            }

            if (fallbackBestIndex >= 0 && fallbackTies > 1)
            {
                WarnPairingCandidateTie(eventUt, eventIdentity, byNameMatch: false,
                    bestDistance: fallbackBestDistance);
            }

            return fallbackBestIndex;
        }

        private static void WarnPairingCandidateTie(
            double eventUt, RecoveredVesselIdentity eventIdentity, bool byNameMatch, double bestDistance)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < pendingRecoveryFunds.Count; i++)
            {
                double distance = Math.Abs(pendingRecoveryFunds[i].Ut - eventUt);
                if (distance > LedgerOrchestrator.VesselRecoveryEventEpsilonSeconds) continue;
                if (byNameMatch && !pendingRecoveryFunds[i].Identity.Matches(eventIdentity))
                    continue;
                if (distance != bestDistance) continue;

                if (sb.Length > 0) sb.Append(", ");
                AppendPendingRequestSummary(sb, pendingRecoveryFunds[i]);
            }

            string tier = byNameMatch ? "name-match" : "nearest-UT";
            ParsekLog.Warn(Tag,
                $"OnRecoveryFundsEventRecorded: multiple pending requests tied at " +
                $"{tier} distance={bestDistance.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"for event ut={eventUt.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"vesselName='{eventIdentity.DisplayName}'. Candidates: [{sb}]. " +
                $"Picking first match in list order.");
        }

        internal static void FlushStalePendingRecoveryFunds(string reason)
        {
            if (pendingRecoveryFunds.Count == 0)
                return;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < pendingRecoveryFunds.Count; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                AppendPendingRequestSummary(sb, pendingRecoveryFunds[i]);
            }

            ParsekLog.Warn(Tag,
                $"FlushStalePendingRecoveryFunds ({reason ?? ""}): " +
                $"evicting {pendingRecoveryFunds.Count} unclaimed recovery request(s) " +
                $"that never received a paired FundsChanged(VesselRecovery) event. " +
                $"Entries: [{sb}]");

            pendingRecoveryFunds.Clear();
        }

        private static bool RecoveryEventMatchesIdentity(
            GameStateEvent e,
            RecoveredVesselIdentity identity)
        {
            if (!identity.HasName)
                return false;

            RecoveredVesselIdentity eventIdentity =
                RecoveryPayoutContextStore.ExtractIdentityFromFundsEventDetail(e.detail);
            return eventIdentity.HasName && identity.Matches(eventIdentity);
        }

        private static bool RecoveryEventSatisfiesIdentityMode(
            GameStateEvent e,
            RecoveredVesselIdentity preferredIdentity,
            RecoveryEventIdentityMatchMode matchMode)
        {
            if (matchMode == RecoveryEventIdentityMatchMode.Any)
                return true;

            RecoveredVesselIdentity eventIdentity =
                RecoveryPayoutContextStore.ExtractIdentityFromFundsEventDetail(e.detail);
            if (matchMode == RecoveryEventIdentityMatchMode.RequireNoIdentity)
                return !eventIdentity.HasName;

            return preferredIdentity.HasName &&
                   eventIdentity.HasName &&
                   preferredIdentity.Matches(eventIdentity);
        }

        private static void AppendPendingRequestSummary(
            System.Text.StringBuilder sb,
            PendingRecoveryFundsRequest request)
        {
            sb.Append(request.Identity.FormatForLog())
              .Append(" ut=").Append(request.Ut.ToString("F1", CultureInfo.InvariantCulture))
              .Append(" vesselType=").Append(request.VesselType)
              .Append(" ")
              .Append(RecoveryPayoutContextStore.DescribeExpectedFunds(request.PayoutContext));
        }

        private static bool ShouldSkipRecoveryFundsBeforePairing(
            RecoveryPayoutContext payoutContext,
            out string reason)
        {
            if (GameStateRecorder.SuppressResourceEvents)
            {
                reason = "resource events suppressed";
                return true;
            }

            if (GameStateRecorder.IsReplayingActions)
            {
                reason = "ledger replay in progress";
                return true;
            }

            if (payoutContext != null && payoutContext.HasFundsEarned)
            {
                if (payoutContext.FundsEarned <= 0.0)
                {
                    reason = "stock expected zero recovery funds";
                    return true;
                }

                if (payoutContext.FundsEarned < GameStateRecorder.FundsThreshold)
                {
                    reason = "stock expected recovery funds " +
                             payoutContext.FundsEarned.ToString("F1", CultureInfo.InvariantCulture) +
                             " below recorder threshold " +
                             GameStateRecorder.FundsThreshold.ToString("F1", CultureInfo.InvariantCulture);
                    return true;
                }

                reason = null;
                return false;
            }

            reason = null;
            return false;
        }

        private static bool ShouldSkipDeferredRecoveryFunds(
            VesselType vesselType,
            RecoveryPayoutContext payoutContext,
            out string reason)
        {
            if (vesselType == VesselType.Debris && payoutContext == null)
            {
                reason = "debris recovery without recovery-processing payout context";
                return true;
            }

            reason = null;
            return false;
        }

        internal static void RepairMissingRecoveryDedupKeys(
            IReadOnlyList<GameAction> actions,
            IReadOnlyList<GameStateEvent> events)
        {
            if (actions == null || actions.Count == 0 || events == null || events.Count == 0)
                return;

            var reservedKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action.Type != GameActionType.FundsEarning) continue;
                if (action.FundsSource != FundsEarningSource.Recovery) continue;
                if (string.IsNullOrEmpty(action.DedupKey)) continue;
                reservedKeys.Add(action.DedupKey);
            }

            int scanned = 0;
            int repaired = 0;
            int unmatched = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action.Type != GameActionType.FundsEarning) continue;
                if (action.FundsSource != FundsEarningSource.Recovery) continue;
                if (!string.IsNullOrEmpty(action.DedupKey)) continue;

                scanned++;
                if (TryFindLegacyRecoveryDedupKey(action, events, reservedKeys, out string repairedKey))
                {
                    action.DedupKey = repairedKey;
                    reservedKeys.Add(repairedKey);
                    repaired++;
                }
                else
                {
                    unmatched++;
                }
            }

            if (scanned > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"RepairMissingRecoveryDedupKeys: scanned={scanned}, repaired={repaired}, unmatched={unmatched}");
            }
        }

        private static bool TryFindLegacyRecoveryDedupKey(
            GameAction action,
            IReadOnlyList<GameStateEvent> events,
            HashSet<string> reservedKeys,
            out string dedupKey)
        {
            dedupKey = null;
            if (action == null || events == null)
                return false;

            double bestUtDistance = double.MaxValue;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                if (e.eventType != GameStateEventType.FundsChanged) continue;
                if (e.key != LedgerOrchestrator.VesselRecoveryReasonKey) continue;

                double utDistance = Math.Abs(e.ut - action.UT);
                if (utDistance > LedgerOrchestrator.VesselRecoveryEventEpsilonSeconds) continue;

                double delta = e.valueAfter - e.valueBefore;
                double amountTolerance = Math.Max(
                    LegacyRecoveryActionAmountTolerance,
                    Math.Abs(delta - (double)(float)delta));
                if (Math.Abs(delta - action.FundsAwarded) > amountTolerance) continue;

                string candidateKey = BuildRecoveryEventDedupKey(e);
                if (reservedKeys.Contains(candidateKey)) continue;
                if (utDistance >= bestUtDistance) continue;

                dedupKey = candidateKey;
                bestUtDistance = utDistance;
                if (utDistance == 0)
                    break;
            }

            return dedupKey != null;
        }

        internal static void OnVesselRecoveryFunds(
            double ut,
            RecoveredVesselIdentity identity,
            bool fromTrackingStation,
            VesselType vesselType,
            RecoveryPayoutContext payoutContext,
            Func<double, RecoveredVesselIdentity, bool, bool> tryAddRecoveryFundsAction)
        {
            if (!identity.HasName)
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: empty vesselName at ut={ut.ToString("F1", CultureInfo.InvariantCulture)} — skipping");
                return;
            }

            if (ShouldSkipRecoveryFundsBeforePairing(payoutContext, out string skipReason))
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: {identity.FormatForLog()} (VesselType.{vesselType}) " +
                    $"at ut={ut.ToString("F1", CultureInfo.InvariantCulture)} — " +
                    $"skipping deferred recovery-funds pairing ({skipReason})");
                return;
            }

            if (tryAddRecoveryFundsAction(ut, identity, fromTrackingStation))
                return;

            if (ShouldSkipDeferredRecoveryFunds(vesselType, payoutContext, out skipReason))
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: {identity.FormatForLog()} (VesselType.{vesselType}) " +
                    $"at ut={ut.ToString("F1", CultureInfo.InvariantCulture)} — " +
                    $"skipping deferred recovery-funds pairing ({skipReason})");
                return;
            }

            AddPendingRecoveryFundsRequest(ut, identity, fromTrackingStation, vesselType, payoutContext);
            ParsekLog.Verbose(Tag,
                $"OnVesselRecoveryFunds: deferred pairing for {identity.FormatForLog()} " +
                $"at ut={ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"{RecoveryPayoutContextStore.DescribeExpectedFunds(payoutContext)} " +
                $"suppressResourceEvents={GameStateRecorder.SuppressResourceEvents} " +
                $"isReplayingActions={GameStateRecorder.IsReplayingActions} " +
                $"until FundsChanged(VesselRecovery) is recorded");
        }

        private static void AddPendingRecoveryFundsRequest(
            double ut,
            RecoveredVesselIdentity identity,
            bool fromTrackingStation,
            VesselType vesselType,
            RecoveryPayoutContext payoutContext)
        {
            pendingRecoveryFunds.Add(new PendingRecoveryFundsRequest
            {
                Ut = ut,
                Identity = identity,
                FromTrackingStation = fromTrackingStation,
                VesselType = vesselType,
                PayoutContext = payoutContext
            });

            if (pendingRecoveryFunds.Count > LedgerOrchestrator.PendingRecoveryFundsStaleThreshold)
            {
                ParsekLog.Warn(Tag,
                    $"OnVesselRecoveryFunds: pending queue exceeded threshold " +
                    $"(count={pendingRecoveryFunds.Count} > {LedgerOrchestrator.PendingRecoveryFundsStaleThreshold}) " +
                    $"— paired FundsChanged(VesselRecovery) events may be missing. " +
                    $"Latest deferred request {identity.FormatForLog()} " +
                    $"ut={ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"vesselType={vesselType} {RecoveryPayoutContextStore.DescribeExpectedFunds(payoutContext)}");
            }
        }

        internal static bool TryAddVesselRecoveryFundsAction(
            double ut,
            RecoveredVesselIdentity identity,
            bool fromTrackingStation,
            Func<RecoveredVesselIdentity, double, string> pickRecoveryRecordingId,
            Func<int> allocateKscSequence,
            IReadOnlyList<GameAction> actions,
            Action recalculateAndPatch)
        {
            if (!TryFindRecoveryFundsEvent(
                    GameStateStore.Events,
                    ut,
                    skipConsumed: true,
                    preferredIdentity: identity,
                    out GameStateEvent matched,
                    out string dedupKey))
            {
                return false;
            }

            double delta = matched.valueAfter - matched.valueBefore;
            if (delta <= 0)
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: paired event for '{identity.DisplayName}' at ut={matched.ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"has delta={delta.ToString("F1", CultureInfo.InvariantCulture)} — skipping (zero or negative recovery value)");
                consumedRecoveryEventKeys.Add(dedupKey);
                return true;
            }

            if (HasRecoveryActionForDedupKey(actions, dedupKey))
            {
                consumedRecoveryEventKeys.Add(dedupKey);
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: paired event for '{identity.DisplayName}' at ut={matched.ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"already exists in ledger (dedupKey='{dedupKey}') — skipping duplicate add");
                return true;
            }

            string recordingId = pickRecoveryRecordingId(identity, matched.ut);

            consumedRecoveryEventKeys.Add(dedupKey);

            int sequence = allocateKscSequence();

            var action = new GameAction
            {
                UT = matched.ut,
                Type = GameActionType.FundsEarning,
                RecordingId = recordingId,
                FundsAwarded = (float)delta,
                FundsSource = FundsEarningSource.Recovery,
                DedupKey = dedupKey,
                Sequence = sequence
            };

            Ledger.AddAction(action);

            ParsekLog.Info(Tag,
                $"VesselRecovery funds patched: vessel='{identity.DisplayName}' " +
                $"amount={delta.ToString("F0", CultureInfo.InvariantCulture)} " +
                $"ut={matched.ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"recordingId={recordingId ?? "(none)"} fromTrackingStation={fromTrackingStation}");

            recalculateAndPatch();
            return true;
        }

        private static bool HasRecoveryActionForDedupKey(IReadOnlyList<GameAction> actions, string dedupKey)
        {
            if (string.IsNullOrEmpty(dedupKey))
                return false;
            if (actions == null)
                return false;

            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action.Type != GameActionType.FundsEarning) continue;
                if (action.FundsSource != FundsEarningSource.Recovery) continue;
                if (!string.Equals(action.DedupKey, dedupKey, StringComparison.Ordinal)) continue;
                return true;
            }

            return false;
        }
    }
}
