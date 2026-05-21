using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Per-row Stash action for stable Rewind Point leaves the default
    /// Unfinished Flights predicate excluded. Stash sets the monotonic
    /// <see cref="ChildSlot.Stashed"/> bit (the re-stash guard) AND opens the
    /// slot by demoting its effective tip recording from Immutable to
    /// CommittedProvisional (open/closed is read from the tip MergeState).
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
            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                !object.ReferenceEquals(null, scenario)
                    ? scenario.RecordingSupersedes
                    : null;

            // Open the slot: demote its effective chain+supersede tip from
            // Immutable to CommittedProvisional. Open/closed is read from the
            // tip MergeState (the single source of truth), so a stashed stable
            // leaf becomes re-flyable by flipping its tip to CP. Guarded to
            // only demote from Immutable (a stable leaf is born Immutable);
            // never disturb a NotCommitted or already-CP tip. slot.Stashed
            // stays set monotonically and blocks any later re-stash, so the
            // stash -> seal -> re-stash un-seal cannot happen (plan §5).
            string tipId = slot.EffectiveRecordingId(supersedes);
            Recording tip = FindCommittedRecordingById(tipId);
            bool demotedTip = false;
            if (tip != null && tip.MergeState == MergeState.Immutable)
            {
                tip.MergeState = MergeState.CommittedProvisional;
                tip.FilesDirty = true;
                demotedTip = true;
            }

            if ((stashedNow || demotedTip) && !object.ReferenceEquals(null, scenario))
                scenario.BumpSupersedeStateVersion();

            Recording terminalTip = EffectiveState.ResolveChainTerminalRecording(rec);
            string terminal = terminalTip?.TerminalStateValue.HasValue == true
                ? terminalTip.TerminalStateValue.Value.ToString()
                : "<none>";
            MergeState tipState = tip != null ? tip.MergeState : MergeState.NotCommitted;
            bool reapEligible = RewindPointReaper.IsReapEligible(rp, supersedes);

            ParsekLog.Info("UnfinishedFlights",
                $"Stashed slot={slotListIndex} rec={rec.RecordingId ?? "<no-id>"} " +
                $"bp={rp.BranchPointId ?? "<no-bp>"} rp={rp.RewindPointId ?? "<no-rp>"} " +
                $"tip={tipId ?? "<no-tip>"} tipMergeState={tipState} tipDemoted={demotedTip} " +
                $"terminal={terminal} reaperBlocked={!reapEligible}");
            return true;
        }

        private static Recording FindCommittedRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            // Open/closed is read from the slot's effective tip MergeState;
            // EffectiveState owns the raw committed-list read (allowlisted for
            // the ERS/ELS grep gate). The tip may be NotCommitted, which ERS
            // would filter out, so route through the raw-by-id helper.
            return EffectiveState.FindCommittedRecordingByIdRaw(recordingId);
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
