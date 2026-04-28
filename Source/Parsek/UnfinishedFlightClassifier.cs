using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Shared Unfinished Flights predicate and lookup helpers. UI, original
    /// tree commit, and re-fly merge classification all route through this
    /// class so stable-leaf membership cannot drift between call sites.
    /// </summary>
    internal static class UnfinishedFlightClassifier
    {
        private const string Tag = "UnfinishedFlights";

        internal static bool Qualifies(
            Recording rec,
            ChildSlot slot,
            RewindPoint rp,
            bool considerSealed)
        {
            string reason;
            return TryQualify(rec, slot, rp, considerSealed, out reason);
        }

        internal static bool TryQualify(
            Recording rec,
            ChildSlot slot,
            RewindPoint rp,
            bool considerSealed,
            out string reason,
            RecordingTree treeContext = null,
            bool allowNotCommitted = false)
        {
            reason = null;
            string recId = rec?.RecordingId ?? "<no-id>";

            if (rec == null)
            {
                reason = "nullRecording";
                LogVerdict(false, recId, reason, null);
                return false;
            }

            if (rec.MergeState != MergeState.Immutable
                && rec.MergeState != MergeState.CommittedProvisional
                && !(allowNotCommitted && rec.MergeState == MergeState.NotCommitted))
            {
                reason = "mergeState:" + rec.MergeState;
                LogVerdict(false, recId, reason, null);
                return false;
            }

            if (rp == null)
            {
                reason = "noMatchingRP";
                LogVerdict(false, recId, reason, null);
                return false;
            }

            if (slot == null)
            {
                reason = "noMatchingRpSlot";
                LogVerdict(false, recId, reason, $"rp={rp.RewindPointId ?? "<no-rp>"}");
                return false;
            }

            string parentBpId = rec.ParentBranchPointId;
            string childBpId = rec.ChildBranchPointId;
            if (string.IsNullOrEmpty(parentBpId) && string.IsNullOrEmpty(childBpId))
            {
                reason = "noParentBp";
                LogVerdict(false, recId, reason, null);
                return false;
            }

            bool matchesParent = !string.IsNullOrEmpty(parentBpId)
                && string.Equals(rp.BranchPointId, parentBpId, StringComparison.Ordinal);
            bool matchesChild = !string.IsNullOrEmpty(childBpId)
                && string.Equals(rp.BranchPointId, childBpId, StringComparison.Ordinal);
            if (!matchesParent && !matchesChild)
            {
                reason = "branchMismatch";
                LogVerdict(false, recId, reason,
                    $"parentBp={parentBpId ?? "<none>"} childBp={childBpId ?? "<none>"} rpBp={rp.BranchPointId ?? "<none>"}");
                return false;
            }

            if (rec.IsDebris || !slot.Controllable)
            {
                reason = "notControllable";
                LogVerdict(false, recId, reason,
                    $"headIsDebris={rec.IsDebris} slotControllable={slot.Controllable}");
                return false;
            }

            if (considerSealed && slot.Sealed)
            {
                reason = "slotSealed";
                LogVerdict(false, recId, reason,
                    $"slot={ResolveSlotListIndexByReference(rp, slot)} sealedRealTime={slot.SealedRealTime ?? "<none>"}");
                return false;
            }

            Recording chainTip = EffectiveState.ResolveChainTerminalRecording(rec, treeContext);
            if (chainTip == null)
            {
                reason = "noTerminal";
                LogVerdict(false, recId, reason, "tip=<null>");
                return false;
            }

            if (!string.IsNullOrEmpty(chainTip.ChildBranchPointId)
                && !string.Equals(chainTip.ChildBranchPointId, rp.BranchPointId, StringComparison.Ordinal))
            {
                reason = "downstreamBp";
                LogVerdict(false, recId, reason,
                    $"chainTipChildBp={chainTip.ChildBranchPointId} matchedRpBp={rp.BranchPointId ?? "<none>"}");
                return false;
            }

            return TerminalOutcomeQualifiesInternal(
                recId, chainTip, slot, rp, out reason);
        }

        internal static bool TerminalOutcomeQualifies(
            Recording chainTip,
            ChildSlot slot,
            RewindPoint rp)
        {
            string reason;
            return TerminalOutcomeQualifiesInternal(
                chainTip?.RecordingId ?? "<no-id>", chainTip, slot, rp, out reason);
        }

        private static bool TerminalOutcomeQualifiesInternal(
            string recId,
            Recording chainTip,
            ChildSlot slot,
            RewindPoint rp,
            out string reason)
        {
            reason = null;
            TerminalState? terminal = chainTip?.TerminalStateValue;
            if (!terminal.HasValue)
            {
                reason = "noTerminal";
                LogVerdict(false, recId, reason, "terminal=<none>");
                return false;
            }

            if (terminal.Value == TerminalState.Destroyed)
            {
                reason = "crashed";
                LogVerdict(true, recId, reason, "terminal=Destroyed");
                return true;
            }

            if (!string.IsNullOrEmpty(chainTip.EvaCrewName))
            {
                if (terminal.Value != TerminalState.Boarded)
                {
                    reason = "strandedEva";
                    LogVerdict(true, recId, reason,
                        $"terminal={terminal.Value} crew={chainTip.EvaCrewName}");
                    return true;
                }

                reason = "stableTerminal";
                LogVerdict(false, recId, reason,
                    $"terminal={terminal.Value} crew={chainTip.EvaCrewName}");
                return false;
            }

            int slotListIndex = ResolveSlotListIndexByReference(rp, slot);
            if (rp == null || rp.FocusSlotIndex < 0)
            {
                reason = "noFocusSignalOrbiting";
                LogVerdict(false, recId, reason,
                    $"terminal={terminal.Value} slot={slotListIndex} focusSlot={(rp != null ? rp.FocusSlotIndex : -1)}");
                return false;
            }

            if (slotListIndex == rp.FocusSlotIndex)
            {
                reason = "stableTerminalFocusSlot";
                LogVerdict(false, recId, reason,
                    $"slot={slotListIndex} focusSlot={rp.FocusSlotIndex} terminal={terminal.Value}");
                return false;
            }

            if (terminal.Value == TerminalState.Orbiting
                || terminal.Value == TerminalState.SubOrbital)
            {
                reason = "stableLeafUnconcluded";
                LogVerdict(true, recId, reason,
                    $"slot={slotListIndex} focusSlot={rp.FocusSlotIndex} terminal={terminal.Value}");
                return true;
            }

            reason = "stableTerminal";
            LogVerdict(false, recId, reason,
                $"slot={slotListIndex} focusSlot={rp.FocusSlotIndex} terminal={terminal.Value}");
            return false;
        }

        internal static bool TryResolveRewindPointForRecording(
            Recording rec,
            out RewindPoint rp,
            out int slotListIndex)
        {
            string reason;
            return TryResolveRewindPointForRecording(rec, out rp, out slotListIndex, out reason);
        }

        internal static bool TryResolveRewindPointForRecording(
            Recording rec,
            out RewindPoint rp,
            out int slotListIndex,
            out string rejectReason)
        {
            rp = null;
            slotListIndex = -1;
            rejectReason = null;
            if (rec == null)
            {
                rejectReason = "recording-null";
                return false;
            }

            string parentBp = rec.ParentBranchPointId;
            string childBp = rec.ChildBranchPointId;
            if (string.IsNullOrEmpty(parentBp) && string.IsNullOrEmpty(childBp))
            {
                rejectReason = "noParentBp";
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
            {
                rejectReason = "noScenario";
                return false;
            }

            bool matchedRp = false;
            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                scenario.RecordingSupersedes
                ?? (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();

            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var candidate = scenario.RewindPoints[i];
                if (candidate == null) continue;
                bool matchesParent = !string.IsNullOrEmpty(parentBp)
                    && string.Equals(candidate.BranchPointId, parentBp, StringComparison.Ordinal);
                bool matchesChild = !string.IsNullOrEmpty(childBp)
                    && string.Equals(candidate.BranchPointId, childBp, StringComparison.Ordinal);
                if (!matchesParent && !matchesChild)
                    continue;

                matchedRp = true;
                int resolved = EffectiveState.ResolveRewindPointSlotIndexForRecording(
                    candidate, rec, supersedes);
                if (resolved < 0)
                    continue;

                rp = candidate;
                slotListIndex = resolved;
                return true;
            }

            rejectReason = matchedRp ? "noMatchingRpSlot" : "noMatchingRP";
            return false;
        }

        internal static int ResolveSlotListIndexForRecording(RewindPoint rp, Recording rec)
        {
            var supersedes = ParsekScenario.Instance?.RecordingSupersedes
                ?? (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();
            return EffectiveState.ResolveRewindPointSlotIndexForRecording(rp, rec, supersedes);
        }

        internal static bool IsUnfinishedFlightCandidateShape(Recording rec)
            => IsUnfinishedFlightCandidateShape(rec, null);

        internal static bool IsUnfinishedFlightCandidateShape(
            Recording rec,
            RecordingTree treeContext)
        {
            if (rec == null) return false;
            if (rec.MergeState != MergeState.Immutable
                && rec.MergeState != MergeState.CommittedProvisional)
                return false;
            if (string.IsNullOrEmpty(rec.ParentBranchPointId)
                && string.IsNullOrEmpty(rec.ChildBranchPointId))
                return false;

            Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(rec, treeContext);
            if (terminalRec == null || !terminalRec.TerminalStateValue.HasValue)
                return false;

            var terminal = terminalRec.TerminalStateValue.Value;
            if (terminal == TerminalState.Destroyed)
                return true;
            if (!string.IsNullOrEmpty(terminalRec.EvaCrewName)
                && terminal != TerminalState.Boarded)
                return true;
            return terminal == TerminalState.Orbiting
                || terminal == TerminalState.SubOrbital;
        }

        internal static bool IsVisibleUnfinishedFlight(Recording rec, out string reason)
        {
            reason = null;
            if (!IsUnfinishedFlightCandidateShape(rec))
            {
                reason = "not an unfinished flight";
                return false;
            }

            var scenario = ParsekScenario.Instance;
            var supersedes = !object.ReferenceEquals(null, scenario)
                ? scenario.RecordingSupersedes
                : null;
            if (!EffectiveState.IsVisible(rec, supersedes))
            {
                reason = "recording is superseded";
                return false;
            }

            if (!EffectiveState.IsUnfinishedFlight(rec))
            {
                reason = "no matching rewind point or slot";
                return false;
            }

            return true;
        }

        private static int ResolveSlotListIndexByReference(RewindPoint rp, ChildSlot slot)
        {
            if (rp?.ChildSlots != null && slot != null)
            {
                for (int i = 0; i < rp.ChildSlots.Count; i++)
                    if (object.ReferenceEquals(rp.ChildSlots[i], slot))
                        return i;
            }

            return slot != null ? slot.SlotIndex : -1;
        }

        private static void LogVerdict(bool qualifies, string recId, string reason, string details)
        {
            string key = $"uf-{recId}-{reason}";
            string suffix = string.IsNullOrEmpty(details) ? "" : " " + details;
            ParsekLog.VerboseRateLimited(
                Tag,
                key,
                $"IsUnfinishedFlight={(qualifies ? "true" : "false")} rec={recId} reason={reason}{suffix}");
        }
    }
}
