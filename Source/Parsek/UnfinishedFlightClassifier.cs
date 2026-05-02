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
        internal const string RecordingActionReasonPrefix = "recordingAction:";

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
            bool allowNotCommitted = false,
            int? focusSlotOverride = null)
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
            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                GetScenarioSupersedes(ParsekScenario.Instance);
            bool matchesByOrigin = SlotMatchesRecordingOrigin(rec, slot, rp, supersedes);
            if (string.IsNullOrEmpty(parentBpId)
                && string.IsNullOrEmpty(childBpId)
                && !matchesByOrigin)
            {
                reason = "noParentBp";
                LogVerdict(false, recId, reason, null);
                return false;
            }

            bool matchesParent = !string.IsNullOrEmpty(parentBpId)
                && string.Equals(rp.BranchPointId, parentBpId, StringComparison.Ordinal);
            bool matchesChild = !string.IsNullOrEmpty(childBpId)
                && string.Equals(rp.BranchPointId, childBpId, StringComparison.Ordinal);
            if (!matchesParent && !matchesChild && !matchesByOrigin)
            {
                reason = "branchMismatch";
                LogVerdict(false, recId, reason,
                    $"parentBp={parentBpId ?? "<none>"} childBp={childBpId ?? "<none>"} rpBp={rp.BranchPointId ?? "<none>"}");
                return false;
            }

            string branchSide = matchesByOrigin && !matchesParent && !matchesChild
                ? "origin-only"
                : matchesChild && !matchesParent
                ? "active-parent-child"
                : "child";

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
                bool destroyedTerminal = chainTip.TerminalStateValue.HasValue
                    && chainTip.TerminalStateValue.Value == TerminalState.Destroyed;
                bool hasResolvedDownstreamRp = HasResolvedRewindPointForBranch(
                    rec,
                    chainTip.ChildBranchPointId);

                if (!destroyedTerminal || hasResolvedDownstreamRp)
                {
                    reason = "downstreamBp";
                    LogVerdict(false, recId, reason,
                        $"chainTipChildBp={chainTip.ChildBranchPointId} matchedRpBp={rp.BranchPointId ?? "<none>"} " +
                        $"downstreamRp={hasResolvedDownstreamRp}");
                    return false;
                }
            }

            if (chainTip.TerminalStateValue.HasValue
                && chainTip.TerminalStateValue.Value == TerminalState.Destroyed)
            {
                // Destruction is conclusive unless the child BranchPoint has a
                // real rewind route of its own. Crash/debris bookkeeping BPs do
                // not suppress the older playable split.
                return TerminalOutcomeQualifiesInternal(
                    rec, recId, chainTip, slot, rp, out reason, branchSide,
                    focusSlotOverride);
            }

            return TerminalOutcomeQualifiesInternal(
                rec, recId, chainTip, slot, rp, out reason, branchSide,
                focusSlotOverride);
        }

        internal static bool TerminalOutcomeQualifies(
            Recording chainTip,
            ChildSlot slot,
            RewindPoint rp)
        {
            string reason;
            return TerminalOutcomeQualifiesInternal(
                null, chainTip?.RecordingId ?? "<no-id>", chainTip, slot, rp, out reason, null,
                focusSlotOverride: null);
        }

        private static bool TerminalOutcomeQualifiesInternal(
            Recording rec,
            string recId,
            Recording chainTip,
            ChildSlot slot,
            RewindPoint rp,
            out string reason,
            string branchSide,
            int? focusSlotOverride)
        {
            reason = null;
            TerminalState? terminal = chainTip?.TerminalStateValue;
            if (!terminal.HasValue)
            {
                reason = "noTerminal";
                LogVerdict(false, recId, reason, WithBranchSide("terminal=<none>", branchSide));
                return false;
            }

            if (terminal.Value == TerminalState.Destroyed)
            {
                if (TryRejectRecordingScopedWorldAction(
                    rec, recId, out reason, WithBranchSide("terminal=Destroyed", branchSide)))
                    return false;

                reason = "crashed";
                LogVerdict(true, recId, reason, WithBranchSide("terminal=Destroyed", branchSide));
                return true;
            }

            if (!string.IsNullOrEmpty(chainTip.EvaCrewName))
            {
                if (terminal.Value != TerminalState.Boarded)
                {
                    string detail = WithBranchSide(
                        $"terminal={terminal.Value} crew={chainTip.EvaCrewName}", branchSide);
                    if (TryRejectRecordingScopedWorldAction(
                        rec, recId, out reason, detail))
                        return false;

                    reason = "strandedEva";
                    LogVerdict(true, recId, reason, detail);
                    return true;
                }

                reason = "stableTerminal";
                LogVerdict(false, recId, reason,
                    WithBranchSide($"terminal={terminal.Value} crew={chainTip.EvaCrewName}", branchSide));
                return false;
            }

            // Re-Fly merge focus override (v0.9.1, design §4.6).
            // The Re-Fly merge call site passes the merge-time slot index in
            // focusSlotOverride. When the slot the player chose to fly
            // matches the override and the chain tip is a stable terminal,
            // the merge concludes the engagement: return
            // stableTerminalFocusSlot so SupersedeCommit closes the slot
            // (MergeState.Immutable + slot.Sealed=true). The override path
            // intentionally precedes the stashed-keep-open branch (a
            // stashed slot Re-Flown to a stable conclusion also seals) and
            // the noFocusSignalOrbiting / static focus checks below (the
            // override IS the focus signal for THIS merge regardless of
            // rp.FocusSlotIndex). World-action seals still fire here so
            // recordingAction:* wins ahead of stableTerminalFocusSlot when
            // applicable. Recovered/Docked are excluded from the override
            // path because they fall through to the existing stableTerminal
            // close + IsHardSafetyTerminal auto-seal; Boarded / Destroyed
            // returned earlier above. Non-Re-Fly callers pass null and
            // follow the existing stashed / focus / orbit flow unchanged.
            if (focusSlotOverride.HasValue && rp != null
                && IsReFlyOverrideStableTerminal(terminal.Value))
            {
                int overrideSlotListIndex = ResolveSlotListIndexByReference(rp, slot);
                if (overrideSlotListIndex == focusSlotOverride.Value)
                {
                    string overrideDetail = WithBranchSide(
                        $"slot={overrideSlotListIndex} focusSlot={rp.FocusSlotIndex} focusSlotOverride={focusSlotOverride.Value} terminal={terminal.Value}",
                        branchSide);
                    if (TryRejectRecordingScopedWorldAction(
                        rec, recId, out reason, overrideDetail))
                        return false;

                    reason = "stableTerminalFocusSlot";
                    LogVerdict(false, recId, reason, overrideDetail);
                    return false;
                }
            }

            if (slot?.Stashed == true && StashedTerminalQualifies(terminal.Value))
            {
                int stashedSlotListIndex = ResolveSlotListIndexByReference(rp, slot);
                int focusSlotIndex = rp != null ? rp.FocusSlotIndex : -1;
                string detail = WithBranchSide(
                    $"slot={stashedSlotListIndex} focusSlot={focusSlotIndex} terminal={terminal.Value} stashedRealTime={slot.StashedRealTime ?? "<none>"}",
                    branchSide);
                if (TryRejectRecordingScopedWorldAction(
                    rec, recId, out reason, detail))
                    return false;

                reason = "stashedStableLeaf";
                LogVerdict(true, recId, reason, detail);
                return true;
            }

            if (terminal.Value != TerminalState.Orbiting
                && terminal.Value != TerminalState.SubOrbital)
            {
                reason = "stableTerminal";
                LogVerdict(false, recId, reason,
                    WithBranchSide($"terminal={terminal.Value}", branchSide));
                return false;
            }

            if (rp == null)
            {
                int fallbackSlotListIndex = ResolveSlotListIndexByReference(null, slot);
                reason = "noFocusSignalOrbiting";
                LogVerdict(false, recId, reason,
                    WithBranchSide(
                        $"terminal={terminal.Value} slot={fallbackSlotListIndex} focusSlot=-1",
                        branchSide));
                return false;
            }

            int slotListIndex = ResolveSlotListIndexByReference(rp, slot);
            string focusSlotLogValue = focusSlotOverride.HasValue
                ? $"{rp.FocusSlotIndex} focusSlotOverride={focusSlotOverride.Value}"
                : rp.FocusSlotIndex.ToString();
            if (rp.FocusSlotIndex < 0)
            {
                reason = "noFocusSignalOrbiting";
                LogVerdict(false, recId, reason,
                    WithBranchSide(
                        $"terminal={terminal.Value} slot={slotListIndex} focusSlot={focusSlotLogValue}",
                        branchSide));
                return false;
            }

            if (slotListIndex == rp.FocusSlotIndex)
            {
                reason = "stableTerminalFocusSlot";
                LogVerdict(false, recId, reason,
                    WithBranchSide(
                        $"slot={slotListIndex} focusSlot={focusSlotLogValue} terminal={terminal.Value}",
                        branchSide));
                return false;
            }

            if (terminal.Value == TerminalState.Orbiting
                || terminal.Value == TerminalState.SubOrbital)
            {
                string detail = WithBranchSide(
                    $"slot={slotListIndex} focusSlot={focusSlotLogValue} terminal={terminal.Value}",
                    branchSide);
                if (TryRejectRecordingScopedWorldAction(
                    rec, recId, out reason, detail))
                    return false;

                reason = "stableLeafUnconcluded";
                LogVerdict(true, recId, reason, detail);
                return true;
            }

            reason = "stableTerminal";
            LogVerdict(false, recId, reason,
                WithBranchSide(
                    $"slot={slotListIndex} focusSlot={focusSlotLogValue} terminal={terminal.Value}",
                    branchSide));
            return false;
        }

        /// <summary>
        /// Stable terminals that the Re-Fly merge focus override seals on the
        /// player-chosen slot. Recovered / Docked / Boarded are excluded —
        /// they reach the slot-close path through their own existing branches
        /// (Boarded EVA returns at the EVA branch above, Recovered / Docked
        /// fall through to <c>stableTerminal</c> + <c>IsHardSafetyTerminal</c>
        /// auto-seal in <see cref="SupersedeCommit"/>). Destroyed returned
        /// earlier as <c>crashed</c>.
        /// </summary>
        private static bool IsReFlyOverrideStableTerminal(TerminalState terminal)
        {
            switch (terminal)
            {
                case TerminalState.Orbiting:
                case TerminalState.SubOrbital:
                case TerminalState.Landed:
                case TerminalState.Splashed:
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryRejectRecordingScopedWorldAction(
            Recording rec,
            string recId,
            out string reason,
            string detail)
        {
            reason = null;
            if (rec == null)
                return false;

            string actionSummary;
            if (!SupersedeCommit.TryFindRetryBlockingWorldAction(
                    rec, out actionSummary))
                return false;

            reason = RecordingActionReasonPrefix + actionSummary;
            LogVerdict(false, recId, reason, detail);
            return true;
        }

        internal static bool TryResolveStashableRewindPointForRecording(
            Recording rec,
            out RewindPoint rp,
            out int slotListIndex,
            out string reason)
        {
            rp = null;
            slotListIndex = -1;
            reason = null;

            if (rec == null)
            {
                reason = "recording-null";
                return false;
            }

            var scenario = ParsekScenario.Instance;
            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                !object.ReferenceEquals(null, scenario)
                    ? scenario.RecordingSupersedes
                    : null;
            if (!EffectiveState.IsVisible(rec, supersedes))
            {
                reason = "recording is superseded";
                return false;
            }

            string resolveReason;
            if (!TryResolveRewindPointForRecording(rec, out rp, out slotListIndex, out resolveReason)
                || rp?.ChildSlots == null
                || slotListIndex < 0
                || slotListIndex >= rp.ChildSlots.Count)
            {
                reason = resolveReason ?? "noMatchingRpSlot";
                rp = null;
                slotListIndex = -1;
                return false;
            }

            var slot = rp.ChildSlots[slotListIndex];
            if (slot == null)
            {
                reason = "slot-null";
                rp = null;
                slotListIndex = -1;
                return false;
            }

            if (slot.Stashed)
            {
                reason = "alreadyStashed";
                rp = null;
                slotListIndex = -1;
                return false;
            }

            if (slot.Sealed)
            {
                reason = "slotSealed";
                rp = null;
                slotListIndex = -1;
                return false;
            }

            string defaultReason;
            if (TryQualify(rec, slot, rp, true, out defaultReason))
            {
                reason = "alreadyUnfinishedFlight";
                rp = null;
                slotListIndex = -1;
                return false;
            }

            if (!IsManualStashOverrideReason(defaultReason))
            {
                reason = defaultReason ?? "notStashable";
                rp = null;
                slotListIndex = -1;
                return false;
            }

            Recording chainTip = EffectiveState.ResolveChainTerminalRecording(rec);
            TerminalState? terminal = chainTip?.TerminalStateValue;
            if (!terminal.HasValue)
            {
                reason = "noTerminal";
                rp = null;
                slotListIndex = -1;
                return false;
            }

            if (!StashedTerminalQualifies(terminal.Value))
            {
                reason = "unsafeTerminal:" + terminal.Value;
                rp = null;
                slotListIndex = -1;
                return false;
            }

            string actionSummary;
            if (SupersedeCommit.TryFindRetryBlockingWorldAction(rec, out actionSummary))
            {
                reason = RecordingActionReasonPrefix + actionSummary;
                rp = null;
                slotListIndex = -1;
                return false;
            }

            reason = null;
            return true;
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
            bool hasBranchLink =
                !string.IsNullOrEmpty(parentBp)
                || !string.IsNullOrEmpty(childBp);

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
            {
                rejectReason = "noScenario";
                return false;
            }

            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                scenario.RecordingSupersedes
                ?? (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();
            bool matchedRp = false;
            List<string> matchedRps = null;

            if (TryResolveRewindPointForBranch(
                rec,
                childBp,
                supersedes,
                ref matchedRp,
                ref matchedRps,
                out rp,
                out slotListIndex))
            {
                return true;
            }

            if (!string.Equals(parentBp, childBp, StringComparison.Ordinal)
                && TryResolveRewindPointForBranch(
                    rec,
                    parentBp,
                    supersedes,
                    ref matchedRp,
                    ref matchedRps,
                    out rp,
                    out slotListIndex))
            {
                return true;
            }

            if (rec.MergeState != MergeState.NotCommitted
                && TryResolveRewindPointByOriginSlot(
                    rec,
                    scenario.RewindPoints,
                    supersedes,
                    out rp,
                    out slotListIndex))
            {
                return true;
            }

            if (!hasBranchLink)
            {
                rejectReason = "noParentBp";
                return false;
            }

            rejectReason = matchedRp ? "noMatchingRpSlot" : "noMatchingRP";
            if (matchedRp)
            {
                string recId = rec.RecordingId ?? "<no-id>";
                ParsekLog.VerboseRateLimited(
                    Tag,
                    $"uf-resolve-{recId}-{rejectReason}",
                    $"IsUnfinishedFlight=false rec={recId} reason={rejectReason} " +
                    $"matches={(matchedRps?.Count ?? 0)} [{string.Join(",", matchedRps ?? (IEnumerable<string>)Array.Empty<string>())}]");
            }

            return false;
        }

        internal static bool TryResolveRewindPointByOriginSlot(
            Recording rec,
            IReadOnlyList<RewindPoint> rewindPoints,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            out RewindPoint rp,
            out int slotListIndex)
        {
            rp = null;
            slotListIndex = -1;
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return false;
            if (rewindPoints == null)
                return false;

            double bestUt = double.NegativeInfinity;
            for (int i = 0; i < rewindPoints.Count; i++)
            {
                var candidate = rewindPoints[i];
                if (candidate == null) continue;
                int resolved = EffectiveState.ResolveRewindPointSlotIndexForRecording(
                    candidate, rec, supersedes);
                if (resolved < 0)
                    continue;

                double candidateUt = candidate.UT;
                if (rp != null && candidateUt < bestUt)
                    continue;

                rp = candidate;
                slotListIndex = resolved;
                bestUt = candidateUt;
            }

            return rp != null;
        }

        private static bool TryResolveRewindPointForBranch(
            Recording rec,
            string branchPointId,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            ref bool matchedRp,
            ref List<string> matchedRps,
            out RewindPoint rp,
            out int slotListIndex)
        {
            rp = null;
            slotListIndex = -1;
            if (rec == null || string.IsNullOrEmpty(branchPointId))
                return false;

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
                return false;

            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var candidate = scenario.RewindPoints[i];
                if (candidate == null) continue;
                if (!string.Equals(candidate.BranchPointId, branchPointId, StringComparison.Ordinal))
                    continue;

                matchedRp = true;
                if (matchedRps == null)
                    matchedRps = new List<string>();
                matchedRps.Add($"{candidate.RewindPointId ?? "<no-rp>"}@{candidate.BranchPointId ?? "<no-bp>"}");
                int resolved = EffectiveState.ResolveRewindPointSlotIndexForRecording(
                    candidate, rec, supersedes);
                if (resolved < 0)
                    continue;

                rp = candidate;
                slotListIndex = resolved;
                return true;
            }

            return false;
        }

        private static bool HasResolvedRewindPointForBranch(
            Recording rec,
            string branchPointId)
        {
            bool matchedRp = false;
            List<string> matchedRps = null;
            RewindPoint rp;
            int slotListIndex;
            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                ParsekScenario.Instance?.RecordingSupersedes
                ?? (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();

            return TryResolveRewindPointForBranch(
                rec,
                branchPointId,
                supersedes,
                ref matchedRp,
                ref matchedRps,
                out rp,
                out slotListIndex);
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
            bool hasBranchLink =
                !string.IsNullOrEmpty(rec.ParentBranchPointId)
                || !string.IsNullOrEmpty(rec.ChildBranchPointId);
            if (!hasBranchLink && !HasOriginSlotMatchForRecording(rec))
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
            bool defaultCandidate = IsUnfinishedFlightCandidateShape(rec);
            bool stashedCandidate = !defaultCandidate
                && IsPotentialManualStashShape(rec)
                && HasStashedResolvedSlot(rec);
            if (!defaultCandidate && !stashedCandidate)
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

        private static bool IsPotentialManualStashShape(Recording rec)
        {
            if (rec == null) return false;
            if (rec.MergeState != MergeState.Immutable
                && rec.MergeState != MergeState.CommittedProvisional)
                return false;
            if (string.IsNullOrEmpty(rec.ParentBranchPointId)
                && string.IsNullOrEmpty(rec.ChildBranchPointId))
                return false;

            Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(rec);
            return terminalRec != null
                && terminalRec.TerminalStateValue.HasValue
                && StashedTerminalQualifies(terminalRec.TerminalStateValue.Value);
        }

        /// <summary>
        /// Returns true only for open manual-stash slots. A slot that is both
        /// stashed and sealed is closed; sealed wins, matching
        /// <see cref="RewindPointReaper.IsReapEligible"/>'s closed-slot rule.
        /// </summary>
        internal static bool HasStashedResolvedSlot(Recording rec)
        {
            RewindPoint rp;
            int slotListIndex;
            string reason;
            if (!TryResolveRewindPointForRecording(rec, out rp, out slotListIndex, out reason))
                return false;

            return rp?.ChildSlots != null
                && slotListIndex >= 0
                && slotListIndex < rp.ChildSlots.Count
                && rp.ChildSlots[slotListIndex]?.Stashed == true
                && rp.ChildSlots[slotListIndex]?.Sealed == false;
        }

        private static bool StashedTerminalQualifies(TerminalState terminal)
        {
            // Manual Stash is intentionally narrower than "stable terminal":
            // recovery, dock/merge, and board/absorb outcomes have already
            // changed career state or another vessel and are not safe to re-fly.
            return terminal == TerminalState.Landed
                || terminal == TerminalState.Splashed
                || terminal == TerminalState.Orbiting
                || terminal == TerminalState.SubOrbital;
        }

        private static bool IsManualStashOverrideReason(string reason)
        {
            // Stash only covers default-excluded stable leaves. Crashed and
            // stranded EVA rows are already Unfinished Flights, so they reject
            // earlier as alreadyUnfinishedFlight instead of becoming stashable.
            return string.Equals(reason, "stableTerminal", StringComparison.Ordinal)
                || string.Equals(reason, "stableTerminalFocusSlot", StringComparison.Ordinal)
                || string.Equals(reason, "noFocusSignalOrbiting", StringComparison.Ordinal);
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

        private static string WithBranchSide(string details, string branchSide)
        {
            if (string.IsNullOrEmpty(branchSide)) return details;
            if (string.IsNullOrEmpty(details)) return $"side={branchSide}";
            return details + $" side={branchSide}";
        }

        private static bool HasOriginSlotMatchForRecording(Recording rec)
        {
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
                return false;

            RewindPoint rp;
            int slotListIndex;
            return TryResolveRewindPointByOriginSlot(
                rec,
                scenario.RewindPoints,
                GetScenarioSupersedes(scenario),
                out rp,
                out slotListIndex);
        }

        private static bool SlotMatchesRecordingOrigin(
            Recording rec,
            ChildSlot slot,
            RewindPoint rp,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            int slotListIndex = ResolveSlotListIndexByReference(rp, slot);
            if (slotListIndex < 0)
                return false;

            if (EffectiveState.ResolveRewindPointSlotIndexForRecording(
                    rp, rec, supersedes) == slotListIndex)
                return true;

            string supersedeTarget = rec?.SupersedeTargetId;
            if (string.IsNullOrEmpty(supersedeTarget))
                return false;

            var targetRec = new Recording { RecordingId = supersedeTarget };
            return EffectiveState.ResolveRewindPointSlotIndexForRecording(
                rp, targetRec, supersedes) == slotListIndex;
        }

        private static IReadOnlyList<RecordingSupersedeRelation> GetScenarioSupersedes(
            ParsekScenario scenario)
        {
            return !object.ReferenceEquals(null, scenario)
                && scenario.RecordingSupersedes != null
                ? scenario.RecordingSupersedes
                : (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();
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
