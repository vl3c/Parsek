using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Per-row Stash action for stable Rewind Point leaves the default
    /// Unfinished Flights predicate excluded. Stash marks the slot only; it
    /// does not mutate the recording or its MergeState.
    /// </summary>
    internal static class UnfinishedFlightStashHandler
    {
        internal static Func<DateTime> UtcNowForTesting;

        internal static void ResetForTesting()
        {
            UtcNowForTesting = null;
        }

        internal static bool TryStash(Recording rec, out string reason)
        {
            reason = null;
            if (rec == null)
            {
                reason = "recording-null";
                ParsekLog.Error("UnfinishedFlights",
                    "Stash could not resolve slot for rec=<null> reason=recording-null");
                return false;
            }

            RewindPoint rp;
            int slotListIndex;
            string rejectReason;
            if (!UnfinishedFlightClassifier.TryResolveStashableRewindPointForRecording(
                    rec, out rp, out slotListIndex, out rejectReason)
                || rp?.ChildSlots == null
                || slotListIndex < 0
                || slotListIndex >= rp.ChildSlots.Count)
            {
                reason = rejectReason ?? "slot-index-invalid";
                LogStashRejected(rec, reason);
                return false;
            }

            var slot = rp.ChildSlots[slotListIndex];
            if (slot == null)
            {
                reason = "slot-null";
                ParsekLog.Error("UnfinishedFlights",
                    $"Stash could not resolve slot for rec={rec.RecordingId ?? "<no-id>"} reason=slot-null");
                return false;
            }

            bool stashedNow = false;
            if (!slot.Stashed)
            {
                DateTime now = UtcNowForTesting != null ? UtcNowForTesting() : DateTime.UtcNow;
                slot.Stashed = true;
                slot.StashedRealTime = now.ToString("o", CultureInfo.InvariantCulture);
                stashedNow = true;
            }

            var scenario = ParsekScenario.Instance;
            if (stashedNow && !object.ReferenceEquals(null, scenario))
                scenario.BumpSupersedeStateVersion();

            Recording tip = EffectiveState.ResolveChainTerminalRecording(rec);
            string terminal = tip?.TerminalStateValue.HasValue == true
                ? tip.TerminalStateValue.Value.ToString()
                : "<none>";
            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                !object.ReferenceEquals(null, scenario)
                    ? scenario.RecordingSupersedes
                    : null;
            bool reapEligible = RewindPointReaper.IsReapEligible(rp, supersedes);

            ParsekLog.Info("UnfinishedFlights",
                $"Stashed slot={slotListIndex} rec={rec.RecordingId ?? "<no-id>"} " +
                $"bp={rp.BranchPointId ?? "<no-bp>"} rp={rp.RewindPointId ?? "<no-rp>"} " +
                $"terminal={terminal} reaperBlocked={!reapEligible}");
            return true;
        }

        private static void LogStashRejected(Recording rec, string reason)
        {
            string recId = rec?.RecordingId ?? "<no-id>";
            if (IsResolutionFailure(reason))
            {
                ParsekLog.Error("UnfinishedFlights",
                    $"Stash could not resolve slot for rec={recId} reason={reason ?? "<none>"}");
                return;
            }

            ParsekLog.Warn("UnfinishedFlights",
                $"Stash unavailable rec={recId} reason={reason ?? "<none>"}");
        }

        private static bool IsResolutionFailure(string reason)
        {
            return string.Equals(reason, "recording-null", StringComparison.Ordinal)
                || string.Equals(reason, "noScenario", StringComparison.Ordinal)
                || string.Equals(reason, "noMatchingRP", StringComparison.Ordinal)
                || string.Equals(reason, "noMatchingRpSlot", StringComparison.Ordinal)
                || string.Equals(reason, "slot-index-invalid", StringComparison.Ordinal)
                || string.Equals(reason, "slot-null", StringComparison.Ordinal);
        }
    }
}
