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
            public string VesselName;
            public bool FromTrackingStation;
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
            matched = default;
            dedupKey = null;
            if (events == null)
                return false;

            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                if (e.eventType != GameStateEventType.FundsChanged) continue;
                if (e.key != LedgerOrchestrator.VesselRecoveryReasonKey) continue;
                if (Math.Abs(e.ut - ut) > LedgerOrchestrator.VesselRecoveryEventEpsilonSeconds) continue;

                string candidateKey = BuildRecoveryEventDedupKey(e);
                if (skipConsumed && consumedRecoveryEventKeys.Contains(candidateKey)) continue;

                matched = e;
                dedupKey = candidateKey;
                return true;
            }

            return false;
        }

        internal static void OnRecoveryFundsEventRecorded(
            GameStateEvent evt,
            Func<double, string, bool, bool> tryAddRecoveryFundsAction)
        {
            if (pendingRecoveryFunds.Count == 0)
                return;

            string eventVesselName = evt.detail ?? "";

            int bestIndex = FindBestPairingIndex(evt.ut, eventVesselName);
            if (bestIndex < 0)
                return;

            var request = pendingRecoveryFunds[bestIndex];
            if (tryAddRecoveryFundsAction(
                    request.Ut,
                    request.VesselName,
                    request.FromTrackingStation))
            {
                pendingRecoveryFunds.RemoveAt(bestIndex);
            }
        }

        internal static int FindBestPairingIndex(double eventUt, string eventVesselName)
        {
            if (pendingRecoveryFunds.Count == 0)
                return -1;

            bool haveName = !string.IsNullOrEmpty(eventVesselName);

            int nameMatchBestIndex = -1;
            double nameMatchBestDistance = double.MaxValue;
            int nameMatchTies = 0;
            if (haveName)
            {
                for (int i = 0; i < pendingRecoveryFunds.Count; i++)
                {
                    if (!string.Equals(pendingRecoveryFunds[i].VesselName,
                            eventVesselName, StringComparison.Ordinal))
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
                    WarnPairingCandidateTie(eventUt, eventVesselName, byNameMatch: true,
                        bestDistance: nameMatchBestDistance);
                }
                return nameMatchBestIndex;
            }

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
                WarnPairingCandidateTie(eventUt, eventVesselName, byNameMatch: false,
                    bestDistance: fallbackBestDistance);
            }

            return fallbackBestIndex;
        }

        private static void WarnPairingCandidateTie(
            double eventUt, string eventVesselName, bool byNameMatch, double bestDistance)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < pendingRecoveryFunds.Count; i++)
            {
                double distance = Math.Abs(pendingRecoveryFunds[i].Ut - eventUt);
                if (distance > LedgerOrchestrator.VesselRecoveryEventEpsilonSeconds) continue;
                if (byNameMatch &&
                    !string.Equals(pendingRecoveryFunds[i].VesselName,
                        eventVesselName, StringComparison.Ordinal))
                    continue;
                if (distance != bestDistance) continue;

                if (sb.Length > 0) sb.Append(", ");
                sb.Append("vessel='").Append(pendingRecoveryFunds[i].VesselName ?? "")
                  .Append("' ut=").Append(pendingRecoveryFunds[i].Ut.ToString("F1", CultureInfo.InvariantCulture));
            }

            string tier = byNameMatch ? "name-match" : "nearest-UT";
            ParsekLog.Warn(Tag,
                $"OnRecoveryFundsEventRecorded: multiple pending requests tied at " +
                $"{tier} distance={bestDistance.ToString("F3", CultureInfo.InvariantCulture)} " +
                $"for event ut={eventUt.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"vesselName='{eventVesselName ?? ""}'. Candidates: [{sb}]. " +
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
                sb.Append("vessel='").Append(pendingRecoveryFunds[i].VesselName ?? "")
                  .Append("' ut=").Append(pendingRecoveryFunds[i].Ut.ToString("F1", CultureInfo.InvariantCulture));
            }

            ParsekLog.Warn(Tag,
                $"FlushStalePendingRecoveryFunds ({reason ?? ""}): " +
                $"evicting {pendingRecoveryFunds.Count} unclaimed recovery request(s) " +
                $"that never received a paired FundsChanged(VesselRecovery) event. " +
                $"Entries: [{sb}]");

            pendingRecoveryFunds.Clear();
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
            string vesselName,
            bool fromTrackingStation,
            VesselType vesselType,
            Func<double, string, bool, bool> tryAddRecoveryFundsAction)
        {
            if (string.IsNullOrEmpty(vesselName))
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: empty vesselName at ut={ut.ToString("F1", CultureInfo.InvariantCulture)} — skipping");
                return;
            }

            if (tryAddRecoveryFundsAction(ut, vesselName, fromTrackingStation))
                return;

            if (vesselType == VesselType.Debris)
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: vessel '{vesselName}' (VesselType.Debris) at ut={ut.ToString("F1", CultureInfo.InvariantCulture)} — skipping deferred recovery-funds pairing");
                return;
            }

            AddPendingRecoveryFundsRequest(ut, vesselName, fromTrackingStation);
            ParsekLog.Verbose(Tag,
                $"OnVesselRecoveryFunds: deferred pairing for vessel '{vesselName}' " +
                $"at ut={ut.ToString("F1", CultureInfo.InvariantCulture)} until FundsChanged(VesselRecovery) is recorded");
        }

        private static void AddPendingRecoveryFundsRequest(
            double ut, string vesselName, bool fromTrackingStation)
        {
            pendingRecoveryFunds.Add(new PendingRecoveryFundsRequest
            {
                Ut = ut,
                VesselName = vesselName,
                FromTrackingStation = fromTrackingStation
            });

            if (pendingRecoveryFunds.Count > LedgerOrchestrator.PendingRecoveryFundsStaleThreshold)
            {
                ParsekLog.Warn(Tag,
                    $"OnVesselRecoveryFunds: pending queue exceeded threshold " +
                    $"(count={pendingRecoveryFunds.Count} > {LedgerOrchestrator.PendingRecoveryFundsStaleThreshold}) " +
                    $"— paired FundsChanged(VesselRecovery) events may be missing. " +
                    $"Latest deferred request vessel='{vesselName}' ut={ut.ToString("F1", CultureInfo.InvariantCulture)}");
            }
        }

        internal static bool TryAddVesselRecoveryFundsAction(
            double ut,
            string vesselName,
            bool fromTrackingStation,
            Func<string, double, string> pickRecoveryRecordingId,
            Func<int> allocateKscSequence,
            IReadOnlyList<GameAction> actions,
            Action recalculateAndPatch)
        {
            if (!TryFindRecoveryFundsEvent(
                    GameStateStore.Events,
                    ut,
                    skipConsumed: true,
                    out GameStateEvent matched,
                    out string dedupKey))
            {
                return false;
            }

            double delta = matched.valueAfter - matched.valueBefore;
            if (delta <= 0)
            {
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: paired event for '{vesselName}' at ut={matched.ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"has delta={delta.ToString("F1", CultureInfo.InvariantCulture)} — skipping (zero or negative recovery value)");
                consumedRecoveryEventKeys.Add(dedupKey);
                return true;
            }

            if (HasRecoveryActionForDedupKey(actions, dedupKey))
            {
                consumedRecoveryEventKeys.Add(dedupKey);
                ParsekLog.Verbose(Tag,
                    $"OnVesselRecoveryFunds: paired event for '{vesselName}' at ut={matched.ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"already exists in ledger (dedupKey='{dedupKey}') — skipping duplicate add");
                return true;
            }

            string recordingId = pickRecoveryRecordingId(vesselName, matched.ut);

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
                $"VesselRecovery funds patched: vessel='{vesselName}' " +
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
