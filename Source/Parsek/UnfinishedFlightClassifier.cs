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

        /// <summary>
        /// Non-qualifying verdict for an EVA kerbal recording. Re-Fly is for
        /// vessel separations only, so an EVA kerbal never becomes an
        /// Unfinished Flight (owner ruling 2026-09-27).
        /// </summary>
        internal const string EvaNotSeparationReason = "evaNotSeparation";

        /// <summary>
        /// Non-qualifying verdict for a separation slot whose vessel put a crew
        /// member out on EVA after the separation, and that kerbal then joined a
        /// DIFFERENT vessel (boarded it, or his EVA history otherwise merged into a
        /// foreign vessel). A downstream structural / world interaction, like
        /// <c>downstreamBp</c> (owner ruling 2026-09-27, rule 2).
        /// </summary>
        internal const string EvaCrewJoinedForeignVesselReason = "evaCrewJoinedForeignVessel";

        /// <summary>
        /// Resolves the type of the branch point <paramref name="rp"/> was
        /// authored at, through the tree that owns <paramref name="rec"/>
        /// (the pending <paramref name="treeContext"/> first, then the committed
        /// trees). Null when the branch point cannot be found.
        /// </summary>
        internal static BranchPointType? ResolveRewindPointBranchType(
            Recording rec, RewindPoint rp, RecordingTree treeContext)
        {
            if (rp == null || string.IsNullOrEmpty(rp.BranchPointId)) return null;
            RecordingTree tree = EffectiveState.ResolveOwningTree(rec, treeContext);
            var bps = tree?.BranchPoints;
            if (bps == null) return null;
            for (int i = 0; i < bps.Count; i++)
            {
                var bp = bps[i];
                if (bp != null && string.Equals(bp.Id, rp.BranchPointId, StringComparison.Ordinal))
                    return bp.Type;
            }
            return null;
        }

        /// <summary>True when the recording is an EVA kerbal's own recording.</summary>
        internal static bool IsEvaKerbalRecording(Recording rec)
        {
            return rec != null && !string.IsNullOrEmpty(rec.EvaCrewName);
        }

        internal static bool Qualifies(
            Recording rec,
            ChildSlot slot,
            RewindPoint rp)
        {
            string reason;
            return TryQualify(rec, slot, rp, out reason);
        }

        internal static bool TryQualify(
            Recording rec,
            ChildSlot slot,
            RewindPoint rp,
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

            // A legacy Rewind Point authored at an EVA split (before the
            // 2026-09-27 ruling) offers no Re-Fly on EITHER side: rewinding to
            // the EVA moment to fly the vessel back is still an EVA Re-Fly. The
            // load-time sweep reaps such RPs; this closes the window before it
            // runs (for example a pending tree committed mid-session).
            BranchPointType? rpBranchType = ResolveRewindPointBranchType(rec, rp, treeContext);
            if (rpBranchType.HasValue && !RewindPointAuthor.IsReFlySplitType(rpBranchType.Value))
            {
                reason = EvaNotSeparationReason;
                LogVerdict(false, recId, reason,
                    WithBranchSide(
                        $"rp={rp.RewindPointId ?? "<no-rp>"} rpBp={rp.BranchPointId ?? "<none>"} rpBpType={rpBranchType.Value}",
                        branchSide));
                return false;
            }

            if (rec.IsDebris || !slot.Controllable)
            {
                reason = "notControllable";
                LogVerdict(false, recId, reason,
                    $"headIsDebris={rec.IsDebris} slotControllable={slot.Controllable}");
                return false;
            }

            // Walks chain hops, VesselSwitchContinuation branch points, and (owner
            // ruling 2026-09-27) the vessel's own EVA branch points plus a Board
            // that re-merges the same vessel with its own EVA crew. A stock Fly /
            // Switch-To glance, or a crew member stepping out for a report and
            // climbing back in, does not end the vessel's flight: the terminal
            // lives on the segment after it. Real splits (Undock / Dock /
            // JointBreak / Breakup / a Board by a foreign kerbal) still stop the
            // walk, so the downstreamBp reject below keeps its original meaning.
            // The (rec, slot, rp) subject and branchSide stay the ORIGIN's - only
            // the terminal is read through the walked segments.
            //
            // The walk starts at the slot stretch's HEAD, not at rec: a later vessel
            // segment of the same walked stretch (the continuation after an EVA) is a
            // slot member too, and it must reach the same verdict as the origin -
            // including the own-EVA crew registered at an EVA it never crossed itself
            // (rule 2) and the retry-blocking science they earned.
            Recording stretchHead = ResolveSlotStretchHead(rec, slot, treeContext);
            SlotVesselWalk walk = EffectiveState.WalkSlotVessel(
                stretchHead, treeContext, followOwnEvaBoard: true, collectDetail: true);
            Recording chainTip = walk?.Tip;
            if (chainTip == null)
            {
                reason = "noTerminal";
                LogVerdict(false, recId, reason, "tip=<null>");
                return false;
            }
            branchSide = WithWalkCounts(branchSide, walk);

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
                        WithBranchSide(
                            $"chainTipChildBp={chainTip.ChildBranchPointId} matchedRpBp={rp.BranchPointId ?? "<none>"} " +
                            $"downstreamRp={hasResolvedDownstreamRp} walkStop={walk.StopReason ?? "<none>"}",
                            branchSide));
                    return false;
                }
            }

            // Owner ruling 2026-09-27 rule 2: a crew member who stepped out of this
            // vessel after the separation and joined a DIFFERENT vessel (boarded it,
            // or his EVA history otherwise merged into a foreign vessel) is a
            // downstream structural / world interaction - the re-fly could not
            // rewrite the other vessel's history. A kerbal left standing on EVA, or
            // who died on it, does not block.
            if (walk.HasForeignJoin)
            {
                reason = EvaCrewJoinedForeignVesselReason;
                LogVerdict(false, recId, reason,
                    WithBranchSide(
                        $"kerbalRec={walk.ForeignJoinKerbalRecordingId ?? "<none>"} " +
                        $"joinBp={walk.ForeignJoinBranchPointId} joinType={walk.ForeignJoinBranchPointType}",
                        branchSide));
                return false;
            }

            if (chainTip.TerminalStateValue.HasValue
                && chainTip.TerminalStateValue.Value == TerminalState.Destroyed)
            {
                // Destruction is conclusive unless the child BranchPoint has a
                // real rewind route of its own. Crash/debris bookkeeping BPs do
                // not suppress the older playable split.
                return TerminalOutcomeQualifiesInternal(
                    rec, recId, chainTip, slot, rp, out reason, branchSide,
                    focusSlotOverride, safetySubject: stretchHead);
            }

            return TerminalOutcomeQualifiesInternal(
                rec, recId, chainTip, slot, rp, out reason, branchSide,
                focusSlotOverride, safetySubject: stretchHead);
        }

        /// <summary>
        /// The head of the own-vessel stretch <paramref name="rec"/> sits on: walked
        /// BACKWARD across switch-continuation, own-EVA and same-vessel-Board branch
        /// points (<see cref="EffectiveState.CollectOwnVesselStretchBackward"/>), stopping
        /// at the slot's origin and at a re-fly fork. <paramref name="rec"/> itself when
        /// it heads its stretch (the common case: a slot origin or a fork).
        /// </summary>
        internal static Recording ResolveSlotStretchHead(
            Recording rec, ChildSlot slot, RecordingTree treeContext)
        {
            if (rec == null) return null;
            if (slot != null
                && string.Equals(rec.RecordingId, slot.OriginChildRecordingId, StringComparison.Ordinal))
                return rec;
            if (string.IsNullOrEmpty(rec.ParentBranchPointId) && string.IsNullOrEmpty(rec.ChainId))
                return rec;
            List<Recording> back = EffectiveState.CollectOwnVesselStretchBackward(
                rec, treeContext, slot?.OriginChildRecordingId, double.NaN,
                stopBeforeSpanningRewind: false,
                alsoStopAtRecordingIds: EffectiveState.CollectSupersedeDestinationIds(
                    GetScenarioSupersedes(ParsekScenario.Instance)));
            return back.Count > 0 ? back[back.Count - 1] : rec;
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
            int? focusSlotOverride,
            Recording safetySubject = null)
        {
            reason = null;
            TerminalState? terminal = chainTip?.TerminalStateValue;
            // The retry-blocking action scan reads the slot stretch's head so every
            // stretch member sees the same walked history (see TryQualify).
            Recording safetyRec = safetySubject ?? rec;

            // Re-Fly is for vessel separations only (owner ruling 2026-09-27): an
            // EVA kerbal is never an Unfinished Flight, whatever its terminal
            // (Destroyed included). Checked before the terminal so a legacy EVA
            // Rewind Point in an existing career cannot surface a Re-Fly.
            if (IsEvaKerbalRecording(chainTip) || IsEvaKerbalRecording(rec))
            {
                string evaCrew = !string.IsNullOrEmpty(chainTip?.EvaCrewName)
                    ? chainTip.EvaCrewName
                    : rec?.EvaCrewName;
                reason = EvaNotSeparationReason;
                LogVerdict(false, recId, reason,
                    WithBranchSide(
                        $"terminal={(terminal.HasValue ? terminal.Value.ToString() : "<none>")} crew={evaCrew}",
                        branchSide));
                return false;
            }

            if (!terminal.HasValue)
            {
                reason = "noTerminal";
                LogVerdict(false, recId, reason, WithBranchSide("terminal=<none>", branchSide));
                return false;
            }

            if (terminal.Value == TerminalState.Destroyed)
            {
                if (TryRejectRecordingScopedWorldAction(
                    safetyRec, recId, out reason, WithBranchSide("terminal=Destroyed", branchSide)))
                    return false;

                reason = "crashed";
                LogVerdict(true, recId, reason, WithBranchSide("terminal=Destroyed", branchSide));
                return true;
            }

            // Re-Fly merge focus override (v0.9.1, design §4.6).
            // The Re-Fly merge call site passes the merge-time slot index in
            // focusSlotOverride. When the slot the player chose to fly
            // matches the override and the chain tip is a stable terminal,
            // the merge concludes the engagement: return
            // stableTerminalFocusSlot so SupersedeCommit closes the slot
            // (the slot's effective tip MergeState becomes Immutable). The override path
            // intentionally precedes the stashed-keep-open branch (a
            // stashed slot Re-Flown to a stable conclusion also seals) and
            // the noFocusSignalOrbiting / static focus checks below (the
            // override IS the focus signal for THIS merge regardless of
            // rp.FocusSlotIndex). World-action seals still fire here so
            // recordingAction:* wins ahead of stableTerminalFocusSlot when
            // applicable. Recovered/Docked are excluded from the override
            // path because they fall through to the existing stableTerminal
            // close + IsHardSafetyTerminal auto-seal; Boarded / Destroyed
            // returned earlier above. SubOrbital is also excluded: a
            // suborbital arc is "still in flight" (the vessel will crash,
            // land, splash, or with a burn reach orbit) and is not a
            // conclusive outcome, so it falls through to the
            // stableLeafUnconcluded branch below and keeps the slot open.
            // Non-Re-Fly callers pass null and follow the existing stashed
            // / focus / orbit flow unchanged.
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
                        safetyRec, recId, out reason, overrideDetail))
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
                    safetyRec, recId, out reason, detail))
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

            // Static-focus seal: only Orbiting concludes the engagement on the
            // focus slot. SubOrbital falls through to the stableLeafUnconcluded
            // branch below because a suborbital arc is still in flight: the
            // vessel will crash, land, splash, or with a burn reach orbit.
            // Sealing the slot here would close the loop before the outcome is
            // known. Keeping the slot open for SubOrbital lines up with the
            // Re-Fly override path above (which also excludes SubOrbital) and
            // with TerminalKindClassifier.Classify (which routes SubOrbital to
            // InFlight, not Landed).
            if (slotListIndex == rp.FocusSlotIndex
                && terminal.Value == TerminalState.Orbiting)
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
                    safetyRec, recId, out reason, detail))
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
        /// player-chosen slot. Recovered / Docked / Boarded are excluded
        /// because they reach the slot-close path through their own existing
        /// branches (Boarded EVA returns at the EVA branch above, Recovered /
        /// Docked fall through to <c>stableTerminal</c> +
        /// <c>IsHardSafetyTerminal</c> auto-seal in
        /// <see cref="SupersedeCommit"/>). Destroyed returned earlier as
        /// <c>crashed</c>. SubOrbital is also excluded: a suborbital arc is
        /// still in flight (the vessel will crash, land, splash, or with a
        /// burn reach orbit), so it falls through to
        /// <c>stableLeafUnconcluded</c> and keeps the slot open. This aligns
        /// the slot-close contract with <see cref="TerminalKindClassifier.Classify"/>,
        /// which already routes SubOrbital to <c>InFlight</c>.
        /// </summary>
        private static bool IsReFlyOverrideStableTerminal(TerminalState terminal)
        {
            switch (terminal)
            {
                case TerminalState.Orbiting:
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

            string defaultReason;
            if (TryQualify(rec, slot, rp, out defaultReason))
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

            // REFLY-QUALIFY-AND-TIP-WALKS-DISAGREE: the stash terminal read pairs
            // with the TryQualify reject above, which already hops switch
            // continuations, so it must read the terminal over the same recording.
            Recording chainTip = EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(rec, null);
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

            // Same switch-continuation walk as TryQualify: a recording whose own
            // terminal was left unstamped because a stock Fly / Switch-To glance
            // closed it mid-flight still has a candidate SHAPE — the terminal
            // lives on the continuation segment downstream of the
            // VesselSwitchContinuation branch point.
            Recording terminalRec = EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(
                rec, treeContext);
            if (terminalRec == null || !terminalRec.TerminalStateValue.HasValue)
                return false;

            // Same EVA gate as TerminalOutcomeQualifiesInternal: an EVA kerbal
            // never has an Unfinished Flight shape.
            if (IsEvaKerbalRecording(rec) || IsEvaKerbalRecording(terminalRec))
                return false;

            var terminal = terminalRec.TerminalStateValue.Value;
            if (terminal == TerminalState.Destroyed)
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

        internal static bool IsPotentialManualStashShape(Recording rec)
        {
            if (rec == null) return false;
            if (rec.MergeState != MergeState.Immutable
                && rec.MergeState != MergeState.CommittedProvisional)
                return false;
            if (string.IsNullOrEmpty(rec.ParentBranchPointId)
                && string.IsNullOrEmpty(rec.ChildBranchPointId))
                return false;

            // REFLY-QUALIFY-AND-TIP-WALKS-DISAGREE: mirrors the candidate-shape gate
            // IsUnfinishedFlightCandidateShape, which hops switch continuations; a
            // stash shape read on the bare chain walk would see no terminal at all.
            Recording terminalRec = EffectiveState.ResolveTerminalRecordingAcrossSwitchContinuations(rec, null);
            // An EVA kerbal is never re-flyable, so it has no stash shape either.
            if (IsEvaKerbalRecording(rec) || IsEvaKerbalRecording(terminalRec))
                return false;
            return terminalRec != null
                && terminalRec.TerminalStateValue.HasValue
                && StashedTerminalQualifies(terminalRec.TerminalStateValue.Value);
        }

        /// <summary>
        /// Returns true only for open manual-stash slots. A stashed slot
        /// whose effective tip has been sealed to
        /// <see cref="MergeState.Immutable"/> is closed; the tip MergeState
        /// is the single open/closed source of truth, matching
        /// <see cref="RewindPointReaper.IsReapEligible"/>'s closed-slot rule.
        /// </summary>
        internal static bool HasStashedResolvedSlot(Recording rec)
        {
            RewindPoint rp;
            int slotListIndex;
            string reason;
            if (!TryResolveRewindPointForRecording(rec, out rp, out slotListIndex, out reason))
                return false;

            if (rp?.ChildSlots == null
                || slotListIndex < 0
                || slotListIndex >= rp.ChildSlots.Count)
                return false;

            var slot = rp.ChildSlots[slotListIndex];
            // Pass rec as the candidate: when rec is itself the slot's effective
            // tip (the common stashed-leaf case) its MergeState is read directly,
            // so this works without rec being registered in CommittedRecordings
            // (unit-test fixtures pass an unregistered recording).
            return slot?.Stashed == true && IsSlotEffectiveTipOpen(slot, rec);
        }

        /// <summary>
        /// Returns true iff the slot's effective chain+supersede tip is a
        /// re-flyable OPEN Unfinished Flight, i.e. the tip MergeState is
        /// <see cref="MergeState.CommittedProvisional"/>. A tip that is
        /// <see cref="MergeState.Immutable"/> (sealed / concluded / canon),
        /// <see cref="MergeState.NotCommitted"/> (recorder still running, not a
        /// re-flyable row yet), or unresolved (null / orphan) returns false.
        /// This is the UI / UF-row "show as re-flyable" predicate, derived from
        /// the same tip MergeState that is the single open/closed source of
        /// truth after the slot.Sealed bit was collapsed into MergeState.
        /// NOTE the reaper's reap-eligibility rule is RELATED but not identical:
        /// it keeps an RP alive when a tip is CommittedProvisional OR
        /// NotCommitted (never reap a live recorder), and reaps only when every
        /// tip is Immutable. So this predicate and the reaper agree on CP (open)
        /// and Immutable (closed) but differ on NotCommitted by design.
        /// </summary>
        internal static bool IsSlotEffectiveTipOpen(ChildSlot slot)
            => IsSlotEffectiveTipOpen(slot, null);

        /// <param name="candidate">The recording being classified; when it is
        /// itself the slot's effective tip its MergeState is read directly,
        /// avoiding a committed-store lookup for the common case.</param>
        internal static bool IsSlotEffectiveTipOpen(ChildSlot slot, Recording candidate)
        {
            if (slot == null) return false;
            var supersedes = GetScenarioSupersedes(ParsekScenario.Instance);
            string tipId = slot.EffectiveRecordingId(supersedes);
            if (string.IsNullOrEmpty(tipId)) return false;

            if (candidate != null
                && string.Equals(candidate.RecordingId, tipId, StringComparison.Ordinal))
                return candidate.MergeState == MergeState.CommittedProvisional;

            // EffectiveState owns the raw committed read (allowlisted for the
            // ERS/ELS grep gate); open/closed must see the tip's
            // CommittedProvisional / Immutable / NotCommitted state directly.
            Recording tip = EffectiveState.FindCommittedRecordingByIdRaw(tipId);
            return tip != null && tip.MergeState == MergeState.CommittedProvisional;
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
            // Stash only covers default-excluded stable leaves. Crashed rows are
            // already Unfinished Flights, so they reject earlier as
            // alreadyUnfinishedFlight instead of becoming stashable. An EVA kerbal
            // (evaNotSeparation) is never stashable either.
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

        /// <summary>
        /// Appends the slot-vessel walk's EVA / Board hop counts to the verdict's side
        /// token (<c>side=child walkedEva=1 walkedBoard=1</c>) when the walk crossed any,
        /// so a verdict read through a crew member's EVA is greppable. A walk with no
        /// EVA / Board hop leaves the token unchanged.
        /// </summary>
        internal static string WithWalkCounts(string branchSide, SlotVesselWalk walk)
        {
            if (walk == null || (walk.EvaHops == 0 && walk.BoardHops == 0))
                return branchSide;
            string counts = $"walkedEva={walk.EvaHops} walkedBoard={walk.BoardHops}";
            return string.IsNullOrEmpty(branchSide) ? counts : branchSide + " " + counts;
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
