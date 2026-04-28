using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal static class PostWalkActionReconciler
    {
        // Preserve the original LedgerOrchestrator subsystem tag so log lines stay byte-stable.
        private const string Tag = "LedgerOrchestrator";

        // ================================================================
        // #440 Phase E2 -- Post-walk reconciliation (transformed reward types)
        //
        // After RecalculationEngine.Recalculate populates TransformedFundsReward,
        // TransformedScienceReward, EffectiveRep, and EffectiveScience on each
        // action, ReconcilePostWalk iterates the same (cutoff-filtered) action
        // list and compares those derived values against live KSP deltas in
        // GameStateStore. WARN on divergence, VERBOSE on match. Log-only: does
        // not mutate the ledger, the KSP state, or any module.
        //
        // Scope: the eight action types ClassifyAction routes to the Transformed
        // bucket (ContractComplete, ContractFail, ContractCancel,
        // MilestoneAchievement, ReputationEarning, ReputationPenalty, KSC-path
        // FundsEarning and ScienceEarning). Other action types are reconciled
        // per-action by ReconcileKscAction and return Reconcile=false here.
        //
        // See docs/dev/done/plans/fix-440-post-walk-reconciliation.md.
        // ================================================================

        /// <summary>
        /// UT window (seconds) used by <see cref="PostWalkActionReconciler.ReconcilePostWalk"/> to pair a
        /// transformed-type action with its resource-changed event. Matches the same
        /// coalesce invariant documented on
        /// <see cref="KscActionReconciler.KscReconcileEpsilonSeconds"/>, kept
        /// independent so a future tune cannot inadvertently couple the two paths.
        /// </summary>
        internal const double PostWalkReconcileEpsilonSeconds = 0.1;

        // Membership in a coalesced post-walk window is stricter than "exactly zero",
        // but intentionally independent from the compare tolerance so multiple tiny
        // contributors can still aggregate into a visible mismatch.
        private const double PostWalkAggregateContributionEpsilon = 1e-6;

        /// <summary>
        /// One resource leg of a <see cref="PostWalkExpectation"/>. Populated
        /// only when the leg applies for the action type; otherwise the leg is
        /// default-zeroed (Applies=false).
        /// </summary>
        internal struct PostWalkLeg
        {
            public bool Applies;
            public double Expected;
            public string ReasonKey;
            public GameStateEventType EventType;
        }

        /// <summary>
        /// Classification output for one action in the post-walk reconcile pass.
        /// Up to three legs (funds/rep/sci) may apply independently. Returns
        /// Reconcile=false for action types outside #440 scope.
        /// </summary>
        internal struct PostWalkExpectation
        {
            public bool Reconcile;
            public PostWalkLeg Funds;
            public PostWalkLeg Rep;
            public PostWalkLeg Sci;
        }

        /// <summary>
        /// Pure classifier: maps a Transformed-bucket <see cref="GameAction"/>
        /// to its post-walk expected delta(s) and the TransactionReasons key
        /// the emitter stamps on the paired GameStateEvent. Action types
        /// outside #440 scope (everything ClassifyAction returns as
        /// Untransformed or NoResourceImpact) return Reconcile=false.
        /// <para>
        /// Reviewer-corrected event keys (see plan sections 3.1-3.6):
        /// <list type="bullet">
        /// <item><c>ContractComplete</c> -> <c>ContractReward</c> (all three legs)</item>
        /// <item><c>ContractFail</c> / <c>ContractCancel</c> -> <c>ContractPenalty</c></item>
        /// <item><c>MilestoneAchievement</c> -> <c>Progression</c> (all three legs, via
        /// the generic resource-event path; <c>MilestoneAchieved.detail</c> is NOT parsed)</item>
        /// <item><c>ReputationEarning</c> maps by <see cref="ReputationSource"/>;
        /// <c>Other</c> returns Reconcile=false (synthetic, no paired event).</item>
        /// <item><c>ReputationPenalty</c> maps by <see cref="ReputationPenaltySource"/>;
        /// <c>Other</c> returns Reconcile=false.</item>
        /// </list>
        /// </para>
        /// </summary>
        internal static PostWalkExpectation ClassifyPostWalk(GameAction action)
        {
            var exp = new PostWalkExpectation();
            if (action == null) return exp;

            switch (action.Type)
            {
                case GameActionType.ContractComplete:
                    // All three legs when Effective; duplicate completions (Effective=false)
                    // are filtered by MilestonesModule/ContractsModule and the modules
                    // skip crediting, so the observed delta is 0 for those. Post-walk
                    // mirrors the module gate by skipping entirely.
                    if (!action.Effective) return exp;
                    exp.Reconcile = true;
                    exp.Funds = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.TransformedFundsReward,
                        ReasonKey = "ContractReward",
                        EventType = GameStateEventType.FundsChanged
                    };
                    exp.Rep = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.EffectiveRep,
                        ReasonKey = "ContractReward",
                        EventType = GameStateEventType.ReputationChanged
                    };
                    exp.Sci = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.TransformedScienceReward,
                        ReasonKey = "ContractReward",
                        EventType = GameStateEventType.ScienceChanged
                    };
                    return exp;

                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    // Penalties fire unconditionally (no Effective gate in the modules).
                    // Funds leg: FundsModule deducts FundsPenalty directly (no transform
                    // today). Rep leg: EffectiveRep from the curve (negative).
                    exp.Reconcile = true;
                    exp.Funds = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = -(double)action.FundsPenalty,
                        ReasonKey = "ContractPenalty",
                        EventType = GameStateEventType.FundsChanged
                    };
                    exp.Rep = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.EffectiveRep,
                        ReasonKey = "ContractPenalty",
                        EventType = GameStateEventType.ReputationChanged
                    };
                    return exp;

                case GameActionType.MilestoneAchievement:
                    // All three legs gated on Effective (duplicates skip).
                    if (!action.Effective) return exp;
                    exp.Reconcile = true;
                    exp.Funds = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.MilestoneFundsAwarded,
                        ReasonKey = "Progression",
                        EventType = GameStateEventType.FundsChanged
                    };
                    exp.Rep = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.EffectiveRep,
                        ReasonKey = "Progression",
                        EventType = GameStateEventType.ReputationChanged
                    };
                    exp.Sci = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.MilestoneScienceAwarded,
                        ReasonKey = "Progression",
                        EventType = GameStateEventType.ScienceChanged
                    };
                    return exp;

                case GameActionType.ReputationEarning:
                {
                    // Map source -> key. Synthetic sources (Other, and LegacyMigration if
                    // re-introduced on the rep enum later) return Reconcile=false.
                    string key;
                    switch (action.RepSource)
                    {
                        case ReputationSource.ContractComplete:
                            key = "ContractReward"; break;
                        case ReputationSource.Milestone:
                            key = "Progression"; break;
                        case ReputationSource.Other:
                        default:
                            return exp; // synthetic, no paired event
                    }
                    exp.Reconcile = true;
                    exp.Rep = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.EffectiveRep,
                        ReasonKey = key,
                        EventType = GameStateEventType.ReputationChanged
                    };
                    return exp;
                }

                case GameActionType.ReputationPenalty:
                {
                    string key;
                    switch (action.RepPenaltySource)
                    {
                        case ReputationPenaltySource.ContractFail:
                            key = "ContractPenalty"; break;
                        case ReputationPenaltySource.ContractDecline:
                            key = "ContractDecline"; break;
                        case ReputationPenaltySource.KerbalDeath:
                            key = "CrewKilled"; break;
                        case ReputationPenaltySource.Strategy:
                        case ReputationPenaltySource.Other:
                        default:
                            return exp; // synthetic / no stock emitter today
                    }
                    exp.Reconcile = true;
                    exp.Rep = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.EffectiveRep,
                        ReasonKey = key,
                        EventType = GameStateEventType.ReputationChanged
                    };
                    return exp;
                }

                case GameActionType.FundsEarning:
                {
                    // KSC-path direct earnings (source != Recovery / ContractComplete /
                    // Milestone). Recovery/ContractComplete/Milestone arrive as their own
                    // action types and are handled above. This branch is a safety net
                    // for direct "Other"-source KSC payouts (today: none from stock; mod
                    // strategy payouts could land here once #439 Phase C captures them).
                    if (!action.Effective) return exp;
                    if (action.FundsSource == FundsEarningSource.LegacyMigration) return exp;
                    if (action.FundsSource == FundsEarningSource.Recovery) return exp;
                    if (action.FundsSource == FundsEarningSource.ContractComplete) return exp;
                    if (action.FundsSource == FundsEarningSource.ContractAdvance) return exp;
                    if (action.FundsSource == FundsEarningSource.Milestone) return exp;
                    exp.Reconcile = true;
                    exp.Funds = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.FundsAwarded,
                        ReasonKey = "Other",
                        EventType = GameStateEventType.FundsChanged
                    };
                    return exp;
                }

                case GameActionType.ScienceEarning:
                {
                    // Post-cap EffectiveScience (ScienceModule sets this on walk).
                    if (!action.Effective) return exp;
                    exp.Reconcile = true;
                    exp.Sci = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.EffectiveScience,
                        ReasonKey = LedgerOrchestrator.GetScienceChangedReasonKey(action),
                        EventType = GameStateEventType.ScienceChanged
                    };
                    return exp;
                }

                default:
                    return exp; // Reconcile stays false
            }
        }

        /// <summary>
        /// Post-walk reconciliation. Runs once per <see cref="RecalculateAndPatch"/>
        /// after <see cref="RecalculationEngine.Recalculate"/> returns and before
        /// <see cref="KspStatePatcher.PatchAll"/>. Iterates actions in stored
        /// order; for each Transformed-bucket action whose UT survives the
        /// cutoff, compares the post-walk derived delta against the live KSP
        /// delta (<c>valueAfter - valueBefore</c>) summed across matching
        /// <see cref="GameStateStore"/> events inside the observed-side match window:
        /// normally <see cref="PostWalkReconcileEpsilonSeconds"/> around <c>action.UT</c>,
        /// but for end-anchored <see cref="GameActionType.ScienceEarning"/> actions the
        /// owning recording span is used so earlier in-flight science transmissions still pair.
        /// WARN on divergence per leg; VERBOSE rate-limited on match. Emits a
        /// single INFO summary after the iteration. Log-only.
        /// <para>
        /// Parameterized for testability — production calls pass
        /// the live game-state event list and the recalculation action list.
        /// </para>
        /// </summary>
        internal static void ReconcilePostWalk(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> actions,
            double? utCutoff)
        {
            if (actions == null || actions.Count == 0) return;

            LedgerOrchestrator.GetResourceTrackingAvailability(
                out bool fundsTracked,
                out bool scienceTracked,
                out bool repTracked);
            if (!fundsTracked && !scienceTracked && !repTracked)
            {
                LedgerOrchestrator.LogReconcileSkippedOnce("post-walk", "Post-walk reconcile",
                    fundsTracked, scienceTracked, repTracked);
                return;
            }

            const double fundsTol = 1.0;
            const double repTol = 0.1;
            const double sciTol = 0.1;
            double livePruneThreshold = MilestoneStore.GetLatestCommittedEndUT();

            int walked = 0;
            int compared = 0;
            int matched = 0;
            int mismatchFunds = 0;
            int mismatchRep = 0;
            int mismatchSci = 0;

            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null) continue;

                // Respect the same filter as RecalculationEngine.Recalculate: seed
                // types always pass; non-seed actions pass when UT <= cutoff.
                if (utCutoff.HasValue &&
                    !RecalculationEngine.IsSeedType(action.Type) &&
                    action.UT > utCutoff.Value)
                {
                    continue;
                }

                var exp = ClassifyPostWalk(action);
                if (!exp.Reconcile) continue;
                if (!HasTrackedPostWalkLeg(exp, fundsTracked, scienceTracked, repTracked))
                    continue;
                if (IsOutsidePostWalkLiveCoverage(
                        action,
                        exp,
                        livePruneThreshold,
                        events,
                        actions,
                        utCutoff))
                {
                    continue;
                }
                walked++;

                bool anyCompared = false;
                bool anyMismatch = false;
                if (fundsTracked && exp.Funds.Applies)
                {
                    var result = CompareLeg(
                        action, "funds", exp.Funds, fundsTol, events, actions, utCutoff, livePruneThreshold);
                    if (result != PostWalkCompareResult.Skipped)
                    {
                        anyCompared = true;
                        if (result == PostWalkCompareResult.Mismatch)
                        {
                            mismatchFunds++;
                            anyMismatch = true;
                        }
                    }
                }
                if (repTracked && exp.Rep.Applies)
                {
                    var result = CompareLeg(
                        action, "rep", exp.Rep, repTol, events, actions, utCutoff, livePruneThreshold);
                    if (result != PostWalkCompareResult.Skipped)
                    {
                        anyCompared = true;
                        if (result == PostWalkCompareResult.Mismatch)
                        {
                            mismatchRep++;
                            anyMismatch = true;
                        }
                    }
                }
                if (scienceTracked && exp.Sci.Applies)
                {
                    var result = CompareLeg(
                        action, "sci", exp.Sci, sciTol, events, actions, utCutoff, livePruneThreshold);
                    if (result != PostWalkCompareResult.Skipped)
                    {
                        anyCompared = true;
                        if (result == PostWalkCompareResult.Mismatch)
                        {
                            mismatchSci++;
                            anyMismatch = true;
                        }
                    }
                }

                if (anyCompared)
                {
                    compared++;
                    if (!anyMismatch) matched++;
                }
            }

            string cutoffLabel = utCutoff.HasValue
                ? utCutoff.Value.ToString("R", CultureInfo.InvariantCulture)
                : "null";
            ParsekLog.Info(Tag,
                $"Post-walk reconcile: actions={walked}, compared={compared}, matches={matched}, " +
                $"mismatches(funds/rep/sci)={mismatchFunds}/{mismatchRep}/{mismatchSci}, " +
                $"cutoffUT={cutoffLabel}");
        }

        /// <summary>
        /// Returns true when the action's UT falls outside the live <see cref="GameStateStore"/>
        /// coverage available to post-walk reconciliation. The live store prunes resource
        /// events at or below the latest committed milestone EndUT, and after a rewind/load the
        /// current epoch may have no coverage for older-epoch action history even though the
        /// ledger still retains those actions.
        /// </summary>
        private static bool IsOutsidePostWalkLiveCoverage(
            GameAction action,
            PostWalkExpectation expectation,
            double livePruneThreshold,
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> actions,
            double? utCutoff)
        {
            if (action == null)
                return false;

            if (action.UT <= livePruneThreshold)
            {
                LogPostWalkLiveCoverageSkip(
                    action,
                    "ut is at/below live prune threshold=" +
                    livePruneThreshold.ToString("F1", CultureInfo.InvariantCulture));
                return true;
            }

            GameStateEventType anchorType;
            string anchorKey;
            if (!TryGetPostWalkSourceAnchor(action, out anchorType, out anchorKey))
                return false;

            if (HasLivePostWalkSourceAnchor(action, anchorType, anchorKey, events))
                return false;

            if (!HasLivePostWalkObservedEvent(action, expectation, events, livePruneThreshold))
            {
                LogPostWalkLiveCoverageSkip(
                    action,
                    "no live source anchor or observed reward leg remains in the current timeline");
                return true;
            }

            if (HasAmbiguousLiveCoverageOverlap(
                    action,
                    expectation,
                    actions,
                    utCutoff,
                    events,
                    livePruneThreshold))
            {
                LogPostWalkLiveCoverageSkip(
                    action,
                    "same-UT live overlap is ambiguous without a live source anchor");
                return true;
            }

            return false;
        }

        private static void LogPostWalkLiveCoverageSkip(
            GameAction action,
            string reason)
        {
            string actionType = action != null ? action.Type.ToString() : "(null)";
            string actionId = action != null ? ActionIdForPostWalk(action) : "(null)";
            string utLabel = action != null
                ? action.UT.ToString("F1", CultureInfo.InvariantCulture)
                : "null";
            string rateKey = string.Format(
                CultureInfo.InvariantCulture,
                "post-walk-live-coverage-skip:{0}:{1}:{2}",
                actionType,
                actionId,
                action != null ? action.UT.ToString("R", CultureInfo.InvariantCulture) : "null");

            ParsekLog.VerboseRateLimited(
                Tag,
                rateKey,
                $"Post-walk live-coverage skip: {actionType} id={actionId} ut={utLabel} -- {reason}");
        }

        private static bool TryGetPostWalkSourceAnchor(
            GameAction action,
            out GameStateEventType anchorType,
            out string anchorKey)
        {
            anchorType = default(GameStateEventType);
            anchorKey = null;

            if (action == null)
                return false;

            switch (action.Type)
            {
                case GameActionType.ContractComplete:
                    anchorType = GameStateEventType.ContractCompleted;
                    anchorKey = action.ContractId;
                    return !string.IsNullOrEmpty(anchorKey);

                case GameActionType.ContractFail:
                    anchorType = GameStateEventType.ContractFailed;
                    anchorKey = action.ContractId;
                    return !string.IsNullOrEmpty(anchorKey);

                case GameActionType.ContractCancel:
                    anchorType = GameStateEventType.ContractCancelled;
                    anchorKey = action.ContractId;
                    return !string.IsNullOrEmpty(anchorKey);

                case GameActionType.MilestoneAchievement:
                    anchorType = GameStateEventType.MilestoneAchieved;
                    anchorKey = action.MilestoneId;
                    return !string.IsNullOrEmpty(anchorKey);

                default:
                    return false;
            }
        }

        private static bool HasLivePostWalkSourceAnchor(
            GameAction action,
            GameStateEventType anchorType,
            string anchorKey,
            IReadOnlyList<GameStateEvent> events)
        {
            if (action == null || events == null || events.Count == 0)
                return false;

            string expectedKey = anchorKey ?? "";

            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (!GameStateStore.IsEventVisibleToCurrentTimeline(e)) continue;
                if (e.eventType != anchorType) continue;
                if (Math.Abs(e.ut - action.UT) > PostWalkReconcileEpsilonSeconds) continue;
                if (!PostWalkEventMatchesAction(e, action)) continue;
                if (!string.Equals(e.key ?? "", expectedKey, StringComparison.Ordinal))
                    continue;
                return true;
            }

            return false;
        }

        private static bool HasLivePostWalkObservedEvent(
            GameAction action,
            PostWalkExpectation expectation,
            IReadOnlyList<GameStateEvent> events,
            double livePruneThreshold)
        {
            if (action == null || events == null || events.Count == 0)
                return false;

            return HasLivePostWalkObservedEventForLeg(
                       action, expectation.Funds, events, livePruneThreshold) ||
                   HasLivePostWalkObservedEventForLeg(
                       action, expectation.Rep, events, livePruneThreshold) ||
                   HasLivePostWalkObservedEventForLeg(
                       action, expectation.Sci, events, livePruneThreshold);
        }

        private static bool HasLivePostWalkObservedEventForLeg(
            GameAction action,
            PostWalkLeg leg,
            IReadOnlyList<GameStateEvent> events,
            double livePruneThreshold)
        {
            if (action == null || !leg.Applies || events == null || events.Count == 0)
                return false;

            string expectedKey = leg.ReasonKey ?? "";
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (!IsLivePostWalkObservedEvent(e, livePruneThreshold)) continue;
                if (e.eventType != leg.EventType) continue;
                if (Math.Abs(e.ut - action.UT) > PostWalkReconcileEpsilonSeconds) continue;
                if (!PostWalkEventMatchesAction(e, action)) continue;
                if (!string.Equals(e.key ?? "", expectedKey, StringComparison.Ordinal))
                    continue;
                return true;
            }

            return false;
        }

        private static bool IsLivePostWalkObservedEvent(
            GameStateEvent e,
            double livePruneThreshold)
        {
            if (e.ut <= livePruneThreshold)
                return false;

            return GameStateStore.IsEventVisibleToCurrentTimeline(e);
        }

        private static bool HasAmbiguousLiveCoverageOverlap(
            GameAction anchorAction,
            PostWalkExpectation anchorExpectation,
            IReadOnlyList<GameAction> actions,
            double? utCutoff,
            IReadOnlyList<GameStateEvent> events,
            double livePruneThreshold)
        {
            if (anchorAction == null || actions == null || actions.Count == 0)
                return false;

            var anchorLegs = new[] { anchorExpectation.Funds, anchorExpectation.Rep, anchorExpectation.Sci };

            for (int i = 0; i < actions.Count; i++)
            {
                var other = actions[i];
                if (other == null || object.ReferenceEquals(other, anchorAction))
                    continue;
                if (!PostWalkActionsShareScope(anchorAction, other))
                    continue;
                if (Math.Abs(other.UT - anchorAction.UT) > PostWalkReconcileEpsilonSeconds)
                    continue;
                if (utCutoff.HasValue &&
                    !RecalculationEngine.IsSeedType(other.Type) &&
                    other.UT > utCutoff.Value)
                {
                    continue;
                }

                var otherExp = ClassifyPostWalk(other);
                if (!otherExp.Reconcile)
                    continue;
                if (other.UT <= livePruneThreshold)
                    continue;

                GameStateEventType otherAnchorType;
                string otherAnchorKey;
                bool hasLiveSourceAnchor =
                    TryGetPostWalkSourceAnchor(other, out otherAnchorType, out otherAnchorKey) &&
                    HasLivePostWalkSourceAnchor(other, otherAnchorType, otherAnchorKey, events);

                var otherLegs = new[] { otherExp.Funds, otherExp.Rep, otherExp.Sci };
                for (int anchorIndex = 0; anchorIndex < anchorLegs.Length; anchorIndex++)
                {
                    var anchorLeg = anchorLegs[anchorIndex];
                    if (!anchorLeg.Applies)
                        continue;

                    for (int otherIndex = 0; otherIndex < otherLegs.Length; otherIndex++)
                    {
                        var otherLeg = otherLegs[otherIndex];
                        if (!otherLeg.Applies)
                            continue;
                        if (anchorLeg.EventType != otherLeg.EventType)
                            continue;
                        if (!string.Equals(
                                anchorLeg.ReasonKey ?? "",
                                otherLeg.ReasonKey ?? "",
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (hasLiveSourceAnchor ||
                            HasLivePostWalkObservedEventForLeg(
                                other, otherLeg, events, livePruneThreshold))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Compares one resource leg for a transformed action. Returns
        /// <see cref="PostWalkCompareResult.Match"/> on match,
        /// <see cref="PostWalkCompareResult.Mismatch"/> on mismatch or missing event,
        /// and <see cref="PostWalkCompareResult.Skipped"/> when another action in the
        /// same coalesced window already owns the comparison/logging for this leg.
        /// Observed delta =
        /// <c>sum(valueAfter - valueBefore)</c> across events with matching
        /// type + key within <see cref="PostWalkReconcileEpsilonSeconds"/> of
        /// <c>action.UT</c>.
        /// </summary>
        private enum PostWalkCompareResult
        {
            Match,
            Mismatch,
            Skipped
        }

        private struct PostWalkWindowAggregate
        {
            public double Expected;
            public int ContributorCount;
            public bool IsPrimary;
            public string ContributorLabel;
            public double ObservedWindowStartUt;
            public double ObservedWindowEndUt;
            public double DisplayWindowStartUt;
            public double DisplayWindowEndUt;
        }

        private static PostWalkCompareResult CompareLeg(
            GameAction action,
            string legTag,
            PostWalkLeg leg,
            double tolerance,
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> actions,
            double? utCutoff,
            double livePruneThreshold)
        {
            var aggregate = AggregatePostWalkWindow(
                action, leg, tolerance, actions, utCutoff, events, livePruneThreshold);
            if (!aggregate.IsPrimary)
                return PostWalkCompareResult.Skipped;

            double observedWindowStartUt = aggregate.ObservedWindowStartUt;
            double observedWindowEndUt = aggregate.ObservedWindowEndUt;
            string observedWindowLabel = FormatPostWalkObservedWindowLabel(
                action,
                leg,
                aggregate.DisplayWindowStartUt,
                aggregate.DisplayWindowEndUt);

            double observed = 0.0;
            int observedCount = 0;
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    if (!IsLivePostWalkObservedEvent(e, livePruneThreshold)) continue;
                    if (e.eventType != leg.EventType) continue;
                    if (e.ut < observedWindowStartUt || e.ut > observedWindowEndUt) continue;
                    if (!PostWalkEventMatchesAction(e, action)) continue;
                    string eventKey = e.key ?? "";
                    if (!string.Equals(eventKey, leg.ReasonKey, StringComparison.Ordinal))
                        continue;
                    observed += (e.valueAfter - e.valueBefore);
                    observedCount++;
                }
            }

            // Zero-expected + zero-observed is silent match (no event fired, none
            // expected). Zero-expected + non-zero observed is a mismatch: the walk
            // produced nothing but KSP credited a delta.
            if (Math.Abs(aggregate.Expected) <= tolerance && observedCount == 0)
                return PostWalkCompareResult.Match;

            string expectedLabel = aggregate.Expected.ToString("F1", CultureInfo.InvariantCulture);
            string observedLabel = observed.ToString("F1", CultureInfo.InvariantCulture);
            string warnKeyPrefix = string.Format(
                CultureInfo.InvariantCulture,
                "postwalk:{0}:{1}:{2}:{3:F3}:{4:F3}:{5}",
                legTag,
                action.Type,
                action.RecordingId ?? "",
                observedWindowStartUt,
                observedWindowEndUt,
                leg.ReasonKey ?? "");

            if (observedCount == 0)
            {
                string message =
                    $"Earnings reconciliation (post-walk, {legTag}): {action.Type} " +
                    $"{aggregate.ContributorLabel} expected={expectedLabel} but no matching {leg.EventType} event " +
                    $"keyed '{leg.ReasonKey}' {observedWindowLabel} -- missing earning channel or stale event?";
                string warnKey = warnKeyPrefix + ":missing:" + expectedLabel;
                if (LedgerOrchestrator.LogReconcileWarnOnce(warnKey, message))
                {
                    LogSciencePostWalkReconcileDumpOnce(
                        warnKey,
                        action,
                        leg,
                        events,
                        observedWindowStartUt,
                        observedWindowEndUt,
                        livePruneThreshold);
                }
                return PostWalkCompareResult.Mismatch;
            }

            if (Math.Abs(aggregate.Expected - observed) > tolerance)
            {
                string message =
                    $"Earnings reconciliation (post-walk, {legTag}): {action.Type} " +
                    $"{aggregate.ContributorLabel} expected={expectedLabel}, observed={observedLabel} across " +
                    $"{observedCount} event(s) keyed '{leg.ReasonKey}' {observedWindowLabel} " +
                    $"-- post-walk delta mismatch";
                string warnKey = warnKeyPrefix + ":mismatch:" + expectedLabel + ":" + observedLabel;
                if (LedgerOrchestrator.LogReconcileWarnOnce(warnKey, message))
                {
                    LogSciencePostWalkReconcileDumpOnce(
                        warnKey,
                        action,
                        leg,
                        events,
                        observedWindowStartUt,
                        observedWindowEndUt,
                        livePruneThreshold);
                }
                return PostWalkCompareResult.Mismatch;
            }

            ParsekLog.VerboseRateLimited(Tag,
                $"post-walk-match:{action.Type}:{legTag}:{action.RecordingId ?? ""}:{leg.ReasonKey ?? ""}:{action.UT.ToString("R", CultureInfo.InvariantCulture)}",
                $"Post-walk match: {action.Type} {legTag} {aggregate.ContributorLabel} " +
                $"expected={expectedLabel}, observed={observedLabel}, keyed '{leg.ReasonKey}' " +
                $"{observedWindowLabel}");
            return PostWalkCompareResult.Match;
        }

        private static void GetPostWalkObservedWindow(
            GameAction action,
            PostWalkLeg leg,
            out double startUt,
            out double endUt,
            out string label)
        {
            double epsilon = PostWalkReconcileEpsilonSeconds;
            startUt = action.UT - epsilon;
            endUt = action.UT + epsilon;
            GetPostWalkObservedDisplayWindow(action, leg, out double displayStartUt, out double displayEndUt, out label);

            if (action.Type != GameActionType.ScienceEarning ||
                leg.EventType != GameStateEventType.ScienceChanged ||
                string.IsNullOrEmpty(action.SubjectId) ||
                action.SubjectId.StartsWith("LegacyMigration:", StringComparison.Ordinal))
            {
                return;
            }

            if (!TryGetScienceReconcileWindow(
                    action,
                    out double scienceStartUt,
                    out double scienceEndUt,
                    out bool collapsedPersistedSpan))
                return;

            double startPad = collapsedPersistedSpan ? 0.0 : GetScienceReconcileBoundaryPadding(scienceStartUt);
            double endPad = collapsedPersistedSpan ? 0.0 : GetScienceReconcileBoundaryPadding(scienceEndUt);
            startUt = scienceStartUt - epsilon - startPad;
            endUt = scienceEndUt + epsilon + endPad;
            label = FormatPostWalkObservedWindowLabel(action, leg, displayStartUt, displayEndUt);
        }

        private static void GetPostWalkObservedDisplayWindow(
            GameAction action,
            PostWalkLeg leg,
            out double startUt,
            out double endUt,
            out string label)
        {
            double epsilon = PostWalkReconcileEpsilonSeconds;
            startUt = action.UT - epsilon;
            endUt = action.UT + epsilon;
            label = FormatPostWalkObservedWindowLabel(action, leg, startUt, endUt);

            if (action.Type != GameActionType.ScienceEarning ||
                leg.EventType != GameStateEventType.ScienceChanged ||
                string.IsNullOrEmpty(action.SubjectId) ||
                action.SubjectId.StartsWith("LegacyMigration:", StringComparison.Ordinal))
            {
                return;
            }

            if (!TryGetScienceReconcileWindow(
                    action,
                    out double scienceStartUt,
                    out double scienceEndUt,
                    out bool ignoredCollapsedPersistedSpan))
                return;

            startUt = scienceStartUt;
            endUt = scienceEndUt;
            label = FormatPostWalkObservedWindowLabel(action, leg, startUt, endUt);
        }

        private static bool TryGetScienceReconcileWindow(
            GameAction action,
            out double startUt,
            out double endUt,
            out bool collapsedPersistedSpan)
        {
            startUt = 0.0;
            endUt = 0.0;
            collapsedPersistedSpan = false;

            if (action == null)
                return false;

            if (!LedgerOrchestrator.TryGetPersistedScienceActionWindow(
                    action,
                    out startUt,
                    out endUt,
                    out collapsedPersistedSpan))
            {
                var rec = LedgerOrchestrator.FindRecordingById(action.RecordingId);
                if (rec == null)
                    return false;

                startUt = rec.StartUT;
                endUt = rec.EndUT;
                if (endUt <= startUt)
                    return false;

                ParsekLog.VerboseRateLimited(Tag,
                    $"post-walk-science-window-fallback:{action.RecordingId}:{ActionIdForPostWalk(action)}",
                    $"Post-walk science window: {ActionIdForPostWalk(action)} missing persisted span; " +
                    $"falling back to recording {action.RecordingId ?? "(null)"} " +
                    $"[{LedgerOrchestrator.FormatFixed1(startUt)},{LedgerOrchestrator.FormatFixed1(endUt)}]");
            }
            else if (collapsedPersistedSpan)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    $"post-walk-science-window-collapsed:{action.RecordingId}:{ActionIdForPostWalk(action)}",
                    $"Post-walk science window: {ActionIdForPostWalk(action)} collapsed persisted span " +
                    $"recording={action.RecordingId ?? "(null)"} " +
                    $"start={action.StartUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"end={action.EndUT.ToString("R", CultureInfo.InvariantCulture)} -> " +
                    $"reconstructed [{LedgerOrchestrator.FormatFixed3(startUt)},{LedgerOrchestrator.FormatFixed3(endUt)}]");
            }

            // Only widen the observed-side window for the current end-anchored shape.
            // ScienceEarning spans are persisted through float fields, so at large UTs
            // the stored EndUT may drift from the double-backed action.UT by more than
            // the nominal 0.1 s epsilon. Allow the float quantization loss here while
            // still keeping the gate tight enough to reject truly non-end-anchored rows.
            return Math.Abs(action.UT - endUt) <= GetScienceReconcileAnchorTolerance(action.UT);
        }

        private static double GetScienceReconcileAnchorTolerance(double actionUt)
        {
            double floatRoundTripLoss = Math.Abs((double)(float)actionUt - actionUt);
            return PostWalkReconcileEpsilonSeconds + floatRoundTripLoss;
        }

        private static double GetScienceReconcileBoundaryPadding(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;

            float single = (float)value;
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(single), 0);
            float nextUp = BitConverter.ToSingle(BitConverter.GetBytes(bits + 1), 0);
            if (float.IsNaN(nextUp) || float.IsInfinity(nextUp))
                return 0.0;

            // One full ULP safely covers any double->float rounding loss at this magnitude.
            return Math.Abs((double)nextUp - single);
        }

        /// <summary>
        /// Aggregates the expected post-walk delta across all actions that classify to
        /// the same (EventType, ReasonKey) pair within the coalesce window. Mirrors the
        /// observed-side event coalescing so same-UT reward bursts do not falsely warn,
        /// and designates one "primary" action to own the comparison/logging for that
        /// coalesced window.
        /// </summary>
        private static PostWalkWindowAggregate AggregatePostWalkWindow(
            GameAction anchorAction,
            PostWalkLeg anchorLeg,
            double tolerance,
            IReadOnlyList<GameAction> actions,
            double? utCutoff,
            IReadOnlyList<GameStateEvent> events,
            double livePruneThreshold)
        {
            double summedExpected = 0.0;
            int expectedCount = 0;
            GameAction primaryAction = null;
            var contributorIds = new List<string>();
            double observedWindowStartUt = double.PositiveInfinity;
            double observedWindowEndUt = double.NegativeInfinity;
            bool hasObservedWindow = false;
            double displayWindowStartUt = double.PositiveInfinity;
            double displayWindowEndUt = double.NegativeInfinity;
            bool hasDisplayWindow = false;

            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    var other = actions[i];
                    if (other == null) continue;
                    if (!PostWalkActionsShareScope(anchorAction, other)) continue;
                    if (Math.Abs(other.UT - anchorAction.UT) > PostWalkReconcileEpsilonSeconds)
                        continue;
                    if (utCutoff.HasValue &&
                        !RecalculationEngine.IsSeedType(other.Type) &&
                        other.UT > utCutoff.Value)
                    {
                        continue;
                    }

                    var otherExp = ClassifyPostWalk(other);
                    if (!otherExp.Reconcile) continue;
                    if (IsOutsidePostWalkLiveCoverage(
                            other,
                            otherExp,
                            livePruneThreshold,
                            events,
                            actions,
                            utCutoff))
                    {
                        continue;
                    }

                    bool matched = false;
                    matched |= AccumulateMatchingPostWalkLeg(
                        otherExp.Funds, anchorLeg,
                        ref summedExpected, ref expectedCount);
                    matched |= AccumulateMatchingPostWalkLeg(
                        otherExp.Rep, anchorLeg,
                        ref summedExpected, ref expectedCount);
                    matched |= AccumulateMatchingPostWalkLeg(
                        otherExp.Sci, anchorLeg,
                        ref summedExpected, ref expectedCount);

                    if (!matched) continue;

                    if (primaryAction == null ||
                        (!ActionHasRecordingScope(primaryAction) && ActionHasRecordingScope(other)))
                        primaryAction = other;

                    GetPostWalkObservedWindow(
                        other,
                        anchorLeg,
                        out double contributorWindowStartUt,
                        out double contributorWindowEndUt,
                        out string ignoredContributorWindowLabel);
                    GetPostWalkObservedDisplayWindow(
                        other,
                        anchorLeg,
                        out double contributorDisplayWindowStartUt,
                        out double contributorDisplayWindowEndUt,
                        out string ignoredContributorDisplayWindowLabel);
                    if (!hasObservedWindow || contributorWindowStartUt < observedWindowStartUt)
                        observedWindowStartUt = contributorWindowStartUt;
                    if (!hasObservedWindow || contributorWindowEndUt > observedWindowEndUt)
                        observedWindowEndUt = contributorWindowEndUt;
                    hasObservedWindow = true;
                    if (!hasDisplayWindow || contributorDisplayWindowStartUt < displayWindowStartUt)
                        displayWindowStartUt = contributorDisplayWindowStartUt;
                    if (!hasDisplayWindow || contributorDisplayWindowEndUt > displayWindowEndUt)
                        displayWindowEndUt = contributorDisplayWindowEndUt;
                    hasDisplayWindow = true;
                    contributorIds.Add(ActionIdForPostWalk(other));
                }
            }

            if (expectedCount == 0)
            {
                GetPostWalkObservedWindow(
                    anchorAction,
                    anchorLeg,
                    out observedWindowStartUt,
                    out observedWindowEndUt,
                    out string ignoredAnchorWindowLabel);
                GetPostWalkObservedDisplayWindow(
                    anchorAction,
                    anchorLeg,
                    out displayWindowStartUt,
                    out displayWindowEndUt,
                    out string ignoredAnchorDisplayWindowLabel);
                return new PostWalkWindowAggregate
                {
                    Expected = anchorLeg.Expected,
                    ContributorCount = 1,
                    IsPrimary = true,
                    ContributorLabel = $"id={ActionIdForPostWalk(anchorAction)}",
                    ObservedWindowStartUt = observedWindowStartUt,
                    ObservedWindowEndUt = observedWindowEndUt,
                    DisplayWindowStartUt = displayWindowStartUt,
                    DisplayWindowEndUt = displayWindowEndUt
                };
            }

            return new PostWalkWindowAggregate
            {
                Expected = summedExpected,
                ContributorCount = expectedCount,
                IsPrimary = object.ReferenceEquals(primaryAction, anchorAction),
                ContributorLabel = FormatPostWalkContributorLabel(contributorIds, expectedCount),
                ObservedWindowStartUt = observedWindowStartUt,
                ObservedWindowEndUt = observedWindowEndUt,
                DisplayWindowStartUt = displayWindowStartUt,
                DisplayWindowEndUt = displayWindowEndUt
            };
        }
        private static bool PostWalkEventMatchesAction(GameStateEvent evt, GameAction action)
        {
            if (evt.eventType == GameStateEventType.ScienceChanged &&
                action != null &&
                action.Type == GameActionType.ScienceEarning)
            {
                double actionStartUt = action.StartUT;
                double actionEndUt = action.EndUT;
                if (TryGetScienceReconcileWindow(
                        action,
                        out double reconstructedStartUt,
                        out double reconstructedEndUt,
                        out bool ignoredCollapsedPersistedSpan))
                {
                    actionStartUt = reconstructedStartUt;
                    actionEndUt = reconstructedEndUt;
                }

                return LedgerOrchestrator.DoesScienceEventMatchActionScope(
                    evt,
                    action,
                    actionStartUt,
                    actionEndUt,
                    out bool _);
            }

            string eventRecordingId = evt.recordingId ?? "";
            string actionRecordingId = action?.RecordingId ?? "";
            if (string.IsNullOrEmpty(actionRecordingId))
                return true;
            return string.Equals(eventRecordingId, actionRecordingId, StringComparison.Ordinal);
        }
        private static string FormatPostWalkObservedWindowLabel(
            GameAction action,
            PostWalkLeg leg,
            double startUt,
            double endUt)
        {
            if (action != null &&
                action.Type == GameActionType.ScienceEarning &&
                leg.EventType == GameStateEventType.ScienceChanged &&
                !string.IsNullOrEmpty(action.SubjectId) &&
                !action.SubjectId.StartsWith("LegacyMigration:", StringComparison.Ordinal))
            {
                return $"within science window [{LedgerOrchestrator.FormatFixed1(startUt)},{LedgerOrchestrator.FormatFixed1(endUt)}] for action ut={LedgerOrchestrator.FormatFixed1(action.UT)}";
            }

            return $"within {LedgerOrchestrator.FormatFixed1(PostWalkReconcileEpsilonSeconds)}s of ut={LedgerOrchestrator.FormatFixed1(action.UT)}";
        }

        private static void LogSciencePostWalkReconcileDumpOnce(
            string dumpKey,
            GameAction action,
            PostWalkLeg leg,
            IReadOnlyList<GameStateEvent> events,
            double observedWindowStartUt,
            double observedWindowEndUt,
            double livePruneThreshold)
        {
            if (events == null || events.Count == 0)
                return;
            if (leg.EventType != GameStateEventType.ScienceChanged)
                return;
            if (!LedgerOrchestrator.TryRegisterPostWalkDumpKey(dumpKey))
                return;

            double dumpStartUt = observedWindowStartUt - 5.0;
            double dumpEndUt = observedWindowEndUt + 5.0;
            var lines = new List<string>();
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.eventType != GameStateEventType.ScienceChanged)
                    continue;
                if (evt.ut < dumpStartUt || evt.ut > dumpEndUt)
                    continue;

                bool scopeMatch = PostWalkEventMatchesAction(evt, action);
                bool liveMatch = IsLivePostWalkObservedEvent(evt, livePruneThreshold);
                lines.Add(LedgerOrchestrator.FormatScienceEventForReconcileDump(evt, scopeMatch && liveMatch, scopeMatch && string.IsNullOrEmpty(evt.recordingId ?? "")));
            }

            string detail = lines.Count == 0
                ? "(no ScienceChanged events in dump window)"
                : string.Join(" | ", lines.ToArray());
            ParsekLog.Error(Tag,
                $"Science reconcile dump (post-walk): action={ActionIdForPostWalk(action)} " +
                $"reason='{leg.ReasonKey}' window=[{LedgerOrchestrator.FormatFixed1(observedWindowStartUt)},{LedgerOrchestrator.FormatFixed1(observedWindowEndUt)}] " +
                $"events={detail}");
        }
        private static bool PostWalkActionsShareScope(GameAction anchorAction, GameAction other)
        {
            string anchorRecordingId = anchorAction?.RecordingId ?? "";
            string otherRecordingId = other?.RecordingId ?? "";
            if (string.IsNullOrEmpty(anchorRecordingId))
                return true;
            return string.Equals(anchorRecordingId, otherRecordingId, StringComparison.Ordinal);
        }

        private static bool ActionHasRecordingScope(GameAction action)
        {
            return !string.IsNullOrEmpty(action?.RecordingId);
        }

        private static bool AccumulateMatchingPostWalkLeg(
            PostWalkLeg candidate,
            PostWalkLeg anchorLeg,
            ref double summedExpected,
            ref int expectedCount)
        {
            if (!candidate.Applies) return false;
            if (candidate.EventType != anchorLeg.EventType) return false;

            string candidateKey = candidate.ReasonKey ?? "";
            string anchorKey = anchorLeg.ReasonKey ?? "";
            if (!string.Equals(candidateKey, anchorKey, StringComparison.Ordinal))
                return false;

            if (Math.Abs(candidate.Expected) <= PostWalkAggregateContributionEpsilon)
                return false;

            summedExpected += candidate.Expected;
            expectedCount++;
            return true;
        }

        private static string FormatPostWalkContributorLabel(
            List<string> contributorIds,
            int contributorCount)
        {
            if (contributorIds == null || contributorIds.Count == 0)
                return "id=(none)";

            if (contributorCount <= 1 || contributorIds.Count == 1)
                return $"id={contributorIds[0]}";

            return $"ids=[{string.Join(", ", contributorIds.ToArray())}] across {contributorCount} action(s)";
        }

        private static bool HasTrackedPostWalkLeg(
            PostWalkExpectation exp,
            bool fundsTracked,
            bool scienceTracked,
            bool repTracked)
        {
            return (fundsTracked && exp.Funds.Applies)
                || (scienceTracked && exp.Sci.Applies)
                || (repTracked && exp.Rep.Applies);
        }
        /// <summary>Pure: best-effort identifier for post-walk log lines.</summary>
        private static string ActionIdForPostWalk(GameAction action)
        {
            if (action == null) return "null";
            if (!string.IsNullOrEmpty(action.ContractId)) return action.ContractId;
            if (!string.IsNullOrEmpty(action.MilestoneId)) return action.MilestoneId;
            if (!string.IsNullOrEmpty(action.SubjectId)) return action.SubjectId;
            if (!string.IsNullOrEmpty(action.RecordingId)) return action.RecordingId;
            return "(none)";
        }
    }
}
