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
        // FundsEarning and ScienceEarning), plus ScienceLabEarning, whose
        // ScienceChanged shares the subject science's reason key and coalesces with
        // it in the store, so its share must sit on the same comparison's expected
        // side. Other action types are reconciled per-action by ReconcileKscAction
        // and return Reconcile=false here.
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

        // The per-leg compare tolerances. Named constants rather than three locals
        // because the timeline-hidden coverage probe has to answer "would this leg have
        // compared equal?" with the SAME numbers the compare stage uses; a private copy
        // there could drift and start swallowing divergences the walk would have WARNed
        // about.
        internal const double PostWalkFundsTolerance = 1.0;
        internal const double PostWalkRepTolerance = 0.1;
        internal const double PostWalkScienceTolerance = 0.1;

        /// <summary>
        /// The tolerance <see cref="CompareLeg"/> is called with for a leg of this
        /// resource. Pure.
        /// </summary>
        internal static double GetPostWalkLegTolerance(GameStateEventType eventType)
        {
            switch (eventType)
            {
                case GameStateEventType.FundsChanged:
                    return PostWalkFundsTolerance;
                case GameStateEventType.ScienceChanged:
                    return PostWalkScienceTolerance;
                case GameStateEventType.ReputationChanged:
                    return PostWalkRepTolerance;
                default:
                    // No other event type reaches a post-walk leg. The tightest
                    // tolerance is the conservative answer for a future one: it can
                    // only refuse a pairing, never invent one.
                    return PostWalkRepTolerance;
            }
        }

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
        /// <item><c>ContractComplete</c> and <c>ContractParameterComplete</c> ->
        /// <c>ContractReward</c> (all three legs; stock's parameter and contract payouts land
        /// in one coalesced store event, so both rows sit on its expected side)</item>
        /// <item><c>ContractFail</c> / <c>ContractCancel</c> -> <c>ContractPenalty</c></item>
        /// <item><c>MilestoneAchievement</c> -> <c>Progression</c> (all three legs, via
        /// the generic resource-event path; <c>MilestoneAchieved.detail</c> is NOT parsed)</item>
        /// <item><c>ReputationEarning</c> maps by <see cref="ReputationSource"/>;
        /// <c>Other</c> and <c>Strategy</c> return Reconcile=false (no paired
        /// reason-keyed event).</item>
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
                case GameActionType.ContractParameterComplete:
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
                    // A fail / cancel of a contract already resolved earlier in the walk is
                    // Effective=false (ContractsModule) and the modules charge nothing, so
                    // post-walk skips it like a duplicate completion.
                    // Funds leg: FundsModule deducts FundsPenalty directly (no transform
                    // today). Rep leg: EffectiveRep from the curve (negative).
                    if (!action.Effective) return exp;
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
                        // Strategy: the query-family converter mutates a
                        // CurrencyModifierQuery IN PLACE and the ReputationChanged that
                        // follows (when it fires at all - GameStateRecorder's
                        // ReputationThreshold is 1.0f and the common yield is a fraction
                        // of a point) carries the ORIGINAL transaction reason, not a
                        // strategy-keyed one. There is no reason-keyed event to pair
                        // against, so an Untransformed leg would WARN on every row.
                        // Same standing as FundsEarningSource.Strategy below.
                        case ReputationSource.Strategy:
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
                            // Stock keys the crew-death reputation penalty under
                            // TransactionReasons.VesselLoss. There is no CrewKilled
                            // member on that enum at all (the only "CrewKilled"
                            // symbol in Assembly-CSharp is GameEvents.onCrewKilled),
                            // so the previous "CrewKilled" key could never pair with
                            // any observed ReputationChanged event and guaranteed a
                            // false "missing earning channel" WARN on every death.
                            // Measured on both CL-1-pod-impact flights:
                            //   "Added -9.999828 (-10) reputation: 'VesselLoss'."
                            // (docs/dev/autotest-status.md, CL-1-pod-impact row.)
                            key = "VesselLoss"; break;
                        case ReputationPenaltySource.Strategy:
                            // Bail-Out Grant CurrencyExchanger input: captured directly
                            // from the ReputationChanged(StrategyInput) event as an
                            // already-effective literal (no curve). Intentionally not
                            // reconciled (no paired "Other"-reason event), so skip.
                        case ReputationPenaltySource.StrategyConverter:
                            // The QUERY family's reputation debit (Fundraising Campaign
                            // and siblings). The converter mutates a CurrencyModifierQuery
                            // IN PLACE and the ReputationChanged that follows carries the
                            // ORIGINAL transaction reason, not a strategy-keyed one - and
                            // GameStateRecorder's ReputationThreshold is 1.0f, so a
                            // sub-point diversion emits nothing at all. There is no
                            // reason-keyed event to pair against, exactly as for
                            // ReputationSource.Strategy on the earning side above.
                        case ReputationPenaltySource.Other:
                        default:
                            return exp; // not reconciled (synthetic, or directly captured)
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
                    // Strategy currency-exchange output (Bail-Out Grant, reason
                    // StrategyOutput) is captured directly from the FundsChanged event,
                    // not paired with a "Other"-reason event, so skip reconciliation to
                    // avoid a false WARN. See fix-bailout-grant-currency-exchange-capture.md.
                    if (action.FundsSource == FundsEarningSource.Strategy) return exp;
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

                case GameActionType.ScienceLabEarning:
                {
                    // The science lab's subject-less credit. Stock pays it through a bare
                    // AddScience under the SAME reason key as the subject science beside it
                    // (VesselRecovery from ModuleScienceLab.RecoverScienceLabs,
                    // ScienceTransmission from a lab transmission), so its ScienceChanged
                    // event shares - and in the store often COALESCES with - the event a
                    // ScienceEarning leg pairs against. The leg exists so the lab's share
                    // is on the expected side of whichever comparison sums that event; see
                    // AbsorbLabCreditsIntoWindow and the lab ordering in ReconcilePostWalk.
                    // Raw ScienceAwarded (uncapped, ScienceModule.ProcessLabEarning credits
                    // it whole), the same field the commit-time reconcile sums.
                    if (!(action.ScienceAwarded > 0f)) return exp;
                    exp.Reconcile = true;
                    exp.Sci = new PostWalkLeg
                    {
                        Applies = true,
                        Expected = action.ScienceAwarded,
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
        /// order, <see cref="GameActionType.ScienceLabEarning"/> rows in a second pass
        /// (skipped when a science comparison already summed them); for each
        /// Transformed-bucket action whose UT survives the
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

            const double fundsTol = PostWalkFundsTolerance;
            const double repTol = PostWalkRepTolerance;
            const double sciTol = PostWalkScienceTolerance;
            double livePruneThreshold = MilestoneStore.GetLatestCommittedEndUT();

            int walked = 0;
            int compared = 0;
            int matched = 0;
            int mismatchFunds = 0;
            int mismatchRep = 0;
            int mismatchSci = 0;
            int labCovered = 0;

            // Lab credits reconciled INSIDE another action's comparison (their share sits on
            // that comparison's expected side because their ScienceChanged event sits on its
            // observed side). Lab rows walk in a second pass so this set is complete before
            // any of them would anchor a comparison of its own.
            var reconciledLabCredits = new HashSet<GameAction>();

            for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null) continue;
                if ((action.Type == GameActionType.ScienceLabEarning) != (pass == 1)) continue;

                // Respect the same filter as RecalculationEngine.Recalculate: seed
                // types always pass; non-seed actions pass when UT <= cutoff.
                if (utCutoff.HasValue &&
                    !RecalculationEngine.IsSeedType(action.Type) &&
                    action.UT > utCutoff.Value)
                {
                    continue;
                }

                // A lab credit a science comparison already summed is reconciled there:
                // comparing it again would count its ScienceChanged event a second time.
                // Counted as labCovered, not as a walked action, so actions= keeps
                // meaning "rows that owned or joined a comparison of their own".
                if (pass == 1 && reconciledLabCredits.Contains(action))
                {
                    labCovered++;
                    ParsekLog.VerboseRateLimited(Tag,
                        $"post-walk-lab-covered:{action.RecordingId ?? ""}:{action.UT.ToString("R", CultureInfo.InvariantCulture)}",
                        $"Post-walk lab credit ut={LedgerOrchestrator.FormatFixed1(action.UT)} " +
                        $"method={action.Method} reconciled inside a covering science comparison - skipped");
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
                        action, "funds", exp.Funds, fundsTol, events, actions, utCutoff, livePruneThreshold,
                        reconciledLabCredits);
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
                        action, "rep", exp.Rep, repTol, events, actions, utCutoff, livePruneThreshold,
                        reconciledLabCredits);
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
                        action, "sci", exp.Sci, sciTol, events, actions, utCutoff, livePruneThreshold,
                        reconciledLabCredits);
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
                $"cutoffUT={cutoffLabel}, labCovered={labCovered}");
        }

        /// <summary>
        /// Returns true when the action's UT falls outside the live <see cref="GameStateStore"/>
        /// coverage available to post-walk reconciliation. The live store prunes resource
        /// events at or below the latest committed milestone EndUT, and after a rewind/load the
        /// current epoch may have no coverage for older-epoch action history even though the
        /// ledger still retains those actions.
        /// <para>
        /// SECOND WAY LIVE COVERAGE DISAPPEARS, and the one a source anchor cannot express:
        /// every observed-side read filters through
        /// <see cref="GameStateStore.IsEventVisibleToCurrentTimeline"/>, which hides an
        /// event tagged to a recording that is no longer part of the effective recording
        /// set. The RECALC still walks that recording's rows (a re-fly session suppresses
        /// the origin subtree for visibility, it does not delete its ledger rows), so a
        /// recording-scoped row outlives its own captured events for the length of the
        /// session and can never pair. Anchorless types - the reputation / funds / science
        /// earning and penalty rows - have no source event to notice that with, so the
        /// state read as a "missing earning channel" WARN on every recalc. Measured on
        /// CL-4-refly-crew-standin (2026-09-09_2158): the crew-death
        /// ReputationPenalty(KerbalDeath) row for cl-pod-a paired cleanly at first commit
        /// and then WARNed once the re-fly session suppressed cl-pod-a.
        /// <see cref="HasOnlyTimelineHiddenPostWalkCoverage"/> now catches exactly that
        /// state - the matching capture IS in the store and IS hidden - and nothing wider:
        /// an action with no capture at all still WARNs.
        /// </para>
        /// <para>
        /// THIRD WAY: the capture was PRUNED while its recording was still visible, and the
        /// recording left the timeline afterwards. <see cref="GameStateStore.PruneProcessedEvents"/>
        /// removes visible events at or below the visible milestone watermark; once the
        /// recording is session-suppressed, the milestone holding its events stops counting
        /// toward <see cref="MilestoneStore.GetLatestCommittedEndUT"/>, the live threshold
        /// drops below the row's UT, and the row looks in-coverage with nothing left to pair.
        /// Measured on LAB-10-lab-refly-supersede (2026-10-17_2337): the superseded lab
        /// recording's ScienceLabEarning WARNed (plus the ERROR dump) in the Re-Fly merge's
        /// tree-commit walk, between the active tree being committed and the tombstone pass
        /// retiring the row. <see cref="HasPrunedTimelineHiddenPostWalkCoverage"/> catches it:
        /// recording outside the timeline, row UT at or below the highest committed milestone
        /// EndUT counting hidden milestones (an upper bound on every prune threshold
        /// applied), and no capture left at all, visible or hidden.
        /// </para>
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

            if (HasOnlyTimelineHiddenPostWalkCoverage(
                    action, expectation, events, livePruneThreshold, actions, utCutoff))
            {
                LogPostWalkLiveCoverageSkip(
                    action,
                    "the observed reward leg is present but tagged to a recording outside " +
                    "the current timeline");
                return true;
            }

            if (!IsActionRecordingVisibleToCurrentTimeline(action))
            {
                double committedWatermarkIncludingHidden =
                    MilestoneStore.GetLatestCommittedEndUTIncludingHidden();
                if (HasPrunedTimelineHiddenPostWalkCoverage(
                        action, expectation, events, livePruneThreshold,
                        committedWatermarkIncludingHidden))
                {
                    LogPostWalkLiveCoverageSkip(
                        action,
                        "its recording is outside the current timeline and its capture was " +
                        "pruned at the committed milestone watermark=" +
                        committedWatermarkIncludingHidden.ToString("F1", CultureInfo.InvariantCulture));
                    return true;
                }
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

        /// <summary>
        /// True when this action's observed leg EXISTS in the store but is hidden from
        /// the pass by <see cref="GameStateStore.IsEventVisibleToCurrentTimeline"/>,
        /// because the action's own recording left the current timeline. The comparison
        /// is then unreachable no matter what the ledger holds, so it is skipped rather
        /// than reported as a missing earning channel.
        /// <para>
        /// Deliberately NARROW - all three conditions must hold. An action whose recording
        /// is visible, one whose visible observed leg still exists, and one with no
        /// matching capture at ALL (the genuine "missing earning channel", which must keep
        /// WARNing) each return false and take the unchanged path. So does one whose
        /// hidden capture exists but carries a DIFFERENT amount: see
        /// <see cref="HasTimelineHiddenPostWalkObservedEventForLeg"/>.
        /// </para>
        /// </summary>
        private static bool HasOnlyTimelineHiddenPostWalkCoverage(
            GameAction action,
            PostWalkExpectation expectation,
            IReadOnlyList<GameStateEvent> events,
            double livePruneThreshold,
            IReadOnlyList<GameAction> actions,
            double? utCutoff)
        {
            if (IsActionRecordingVisibleToCurrentTimeline(action))
                return false;
            if (HasLivePostWalkObservedEvent(action, expectation, events, livePruneThreshold))
                return false;

            return HasTimelineHiddenPostWalkObservedEventForLeg(
                       action, expectation.Funds, events, livePruneThreshold, actions, utCutoff) ||
                   HasTimelineHiddenPostWalkObservedEventForLeg(
                       action, expectation.Rep, events, livePruneThreshold, actions, utCutoff) ||
                   HasTimelineHiddenPostWalkObservedEventForLeg(
                       action, expectation.Sci, events, livePruneThreshold, actions, utCutoff);
        }

        /// <summary>
        /// True when the action's recording is outside the current timeline, its UT is at or
        /// below <paramref name="committedWatermarkIncludingHidden"/> (so the store has had
        /// the chance to prune its capture while the recording was visible), and NO capture
        /// of it remains, neither a live one nor a hidden one of any amount. A hidden capture
        /// still in the store belongs to <see cref="HasOnlyTimelineHiddenPostWalkCoverage"/>,
        /// which also checks its amount; a row above the watermark, or on a visible recording,
        /// takes the unchanged path and keeps its "missing earning channel" WARN.
        /// </summary>
        internal static bool HasPrunedTimelineHiddenPostWalkCoverage(
            GameAction action,
            PostWalkExpectation expectation,
            IReadOnlyList<GameStateEvent> events,
            double livePruneThreshold,
            double committedWatermarkIncludingHidden)
        {
            if (action == null)
                return false;
            if (IsActionRecordingVisibleToCurrentTimeline(action))
                return false;
            if (action.UT > committedWatermarkIncludingHidden)
                return false;
            if (HasLivePostWalkObservedEvent(action, expectation, events, livePruneThreshold))
                return false;

            return !HasTimelineHiddenPostWalkCaptureForLeg(
                       action, expectation.Funds, events, livePruneThreshold) &&
                   !HasTimelineHiddenPostWalkCaptureForLeg(
                       action, expectation.Rep, events, livePruneThreshold) &&
                   !HasTimelineHiddenPostWalkCaptureForLeg(
                       action, expectation.Sci, events, livePruneThreshold);
        }

        /// <summary>
        /// True when any HIDDEN capture shaped like this leg's observed event is still in the
        /// store, whatever its amount (the filters of
        /// <see cref="HasTimelineHiddenPostWalkObservedEventForLeg"/> without its amount check).
        /// </summary>
        private static bool HasTimelineHiddenPostWalkCaptureForLeg(
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
                if (e.ut <= livePruneThreshold) continue;
                if (GameStateStore.IsEventVisibleToCurrentTimeline(e)) continue;
                if (e.eventType != leg.EventType) continue;
                if (Math.Abs(e.ut - action.UT) > PostWalkReconcileEpsilonSeconds) continue;
                if (!PostWalkEventMatchesAction(e, action)) continue;
                if (!string.Equals(e.key ?? "", expectedKey, StringComparison.Ordinal))
                    continue;
                return true;
            }

            return false;
        }

        /// <summary>
        /// The mirror of <see cref="HasLivePostWalkObservedEventForLeg"/> with exactly one
        /// condition inverted: the event must be HIDDEN from the current timeline. Every
        /// other filter (prune threshold, event type, epsilon window, recording scope,
        /// reason key) is identical, so a hit is an event this pass would have paired with
        /// had the recording still been visible.
        /// <para>
        /// AND THE AMOUNT HAS TO AGREE, which the shape filters alone do not say. This
        /// probe answers "would the comparison have MATCHED but for the visibility
        /// filter?", and its answer suppresses the WARN outright. A hidden capture whose
        /// delta diverges from <see cref="PostWalkLeg.Expected"/> would have been a
        /// mismatch, not a match, so it must not qualify the skip: the deltas are summed
        /// over the same window <see cref="CompareLeg"/> sums over and compared with the
        /// same per-resource tolerance (<see cref="GetPostWalkLegTolerance"/>), and a
        /// divergent row falls through to the mismatch WARN it earns.
        /// </para>
        /// </summary>
        private static bool HasTimelineHiddenPostWalkObservedEventForLeg(
            GameAction action,
            PostWalkLeg leg,
            IReadOnlyList<GameStateEvent> events,
            double livePruneThreshold,
            IReadOnlyList<GameAction> actions,
            double? utCutoff)
        {
            if (action == null || !leg.Applies || events == null || events.Count == 0)
                return false;

            double windowStartUt = action.UT - PostWalkReconcileEpsilonSeconds;
            double windowEndUt = action.UT + PostWalkReconcileEpsilonSeconds;
            double observed = 0.0;
            int observedCount = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (!IsComparisonObservedEvent(
                        e, action, leg, windowStartUt, windowEndUt,
                        livePruneThreshold, hiddenTimeline: true))
                    continue;
                observed += (e.valueAfter - e.valueBefore);
                observedCount++;
            }

            if (observedCount == 0)
                return false;

            // Same NET-vs-GROSS basis correction as CompareLeg, over the hidden events: the
            // "would it have matched?" answer must use the numbers the compare would use.
            if (leg.EventType == GameStateEventType.ScienceChanged)
            {
                var fold = ComputeConverterScienceFold(
                    action, leg, windowStartUt, windowEndUt,
                    actions, events, utCutoff, livePruneThreshold, hiddenTimeline: true);
                observed += fold.Take - fold.Yield;
            }

            return Math.Abs(leg.Expected - observed) <= GetPostWalkLegTolerance(leg.EventType);
        }

        /// <summary>
        /// True when the action is not recording-scoped, or when its recording still
        /// belongs to the current timeline. The mirror of the observed-side filter
        /// <see cref="GameStateStore.IsEventVisibleToCurrentTimeline"/>: an event tagged to
        /// a recording outside the effective set is invisible to this pass, so a row
        /// scoped to that same recording has no observable counterpart to compare against.
        /// An untagged row keeps the historical answer (visible), because its expected
        /// event is untagged too and stays visible.
        /// </summary>
        internal static bool IsActionRecordingVisibleToCurrentTimeline(GameAction action)
        {
            string recordingId = action == null ? null : action.RecordingId;
            if (string.IsNullOrEmpty(recordingId))
                return true;
            return RecordingStore.IsCurrentTimelineRecordingId(recordingId);
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

                case GameActionType.ContractParameterComplete:
                    anchorType = GameStateEventType.ContractParameterCompleted;
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
            // Lab credits whose share is in Expected (null when none). Filled only on the
            // primary aggregate, the one that owns the comparison.
            public List<GameAction> LabCredits;
        }

        private static PostWalkCompareResult CompareLeg(
            GameAction action,
            string legTag,
            PostWalkLeg leg,
            double tolerance,
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> actions,
            double? utCutoff,
            double livePruneThreshold,
            HashSet<GameAction> reconciledLabCredits)
        {
            var aggregate = AggregatePostWalkWindow(
                action, leg, tolerance, actions, utCutoff, events, livePruneThreshold);
            if (!aggregate.IsPrimary)
                return PostWalkCompareResult.Skipped;

            if (aggregate.LabCredits != null && reconciledLabCredits != null)
            {
                for (int i = 0; i < aggregate.LabCredits.Count; i++)
                    reconciledLabCredits.Add(aggregate.LabCredits[i]);
            }

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
                    if (!IsComparisonObservedEvent(
                            e, action, leg, observedWindowStartUt, observedWindowEndUt,
                            livePruneThreshold, hiddenTimeline: false))
                        continue;
                    observed += (e.valueAfter - e.valueBefore);
                    observedCount++;
                }
            }

            // The observed science delta is NET of any query-family strategy take
            // (Patents Licensing and siblings mutate the CurrencyModifierQuery in place), while
            // every expected science figure is GROSS and the take is its own
            // StrategyScienceDebit row. Gross the observed side back up, exactly as the KSC
            // door does (LedgerOrchestrator.ComputePendingRecentKscScienceCredit).
            if (observedCount > 0 && leg.EventType == GameStateEventType.ScienceChanged)
            {
                var fold = ComputeConverterScienceFold(
                    action, leg, observedWindowStartUt, observedWindowEndUt,
                    actions, events, utCutoff, livePruneThreshold, hiddenTimeline: false);
                if (fold.Take > 0.0 || fold.Yield > 0.0)
                {
                    double observedNet = observed;
                    observed = observedNet + fold.Take - fold.Yield;
                    ParsekLog.VerboseRateLimited(Tag,
                        $"post-walk-converter-fold:{action.Type}:{action.RecordingId ?? ""}:{leg.ReasonKey ?? ""}:{action.UT.ToString("R", CultureInfo.InvariantCulture)}",
                        "Post-walk science window: grossed the observed NET credit " +
                        observedNet.ToString("R", CultureInfo.InvariantCulture) +
                        " keyed '" + (leg.ReasonKey ?? "") + "' up through " +
                        fold.Rows.ToString(CultureInfo.InvariantCulture) +
                        " in-window strategy converter leg(s) (take=" +
                        fold.Take.ToString("R", CultureInfo.InvariantCulture) +
                        ", yield=" + fold.Yield.ToString("R", CultureInfo.InvariantCulture) +
                        ") -> " + observed.ToString("R", CultureInfo.InvariantCulture) +
                        " for " + action.Type + " id=" + ActionIdForPostWalk(action));
                }
                if (fold.Ambiguous > 0)
                {
                    ParsekLog.VerboseRateLimited(Tag,
                        $"post-walk-converter-fold-ambiguous:{action.Type}:{action.RecordingId ?? ""}:{leg.ReasonKey ?? ""}:{action.UT.ToString("R", CultureInfo.InvariantCulture)}",
                        "Post-walk science window: left " +
                        fold.Ambiguous.ToString(CultureInfo.InvariantCulture) +
                        " in-window strategy converter leg(s) out of the '" + (leg.ReasonKey ?? "") +
                        "' comparison for " + action.Type + " id=" + ActionIdForPostWalk(action) +
                        " - a science credit outside this comparison shares their UT, so the take " +
                        "cannot be attributed to it");
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
            List<GameAction> labCredits = null;

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

                    if (other.Type == GameActionType.ScienceLabEarning)
                    {
                        if (labCredits == null) labCredits = new List<GameAction>();
                        labCredits.Add(other);
                    }

                    if (primaryAction == null ||
                        GetPostWalkPrimaryRank(other) > GetPostWalkPrimaryRank(primaryAction))
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
                double anchorExpected = anchorLeg.Expected;
                int anchorContributorCount = 1;
                var anchorContributorIds = new List<string> { ActionIdForPostWalk(anchorAction) };
                AbsorbLabCreditsIntoWindow(
                    anchorAction, anchorLeg, observedWindowStartUt, observedWindowEndUt,
                    actions, utCutoff, events, livePruneThreshold,
                    ref anchorExpected, ref anchorContributorCount, anchorContributorIds, ref labCredits);
                return new PostWalkWindowAggregate
                {
                    Expected = anchorExpected,
                    ContributorCount = anchorContributorCount,
                    IsPrimary = true,
                    ContributorLabel = FormatPostWalkContributorLabel(anchorContributorIds, anchorContributorCount),
                    ObservedWindowStartUt = observedWindowStartUt,
                    ObservedWindowEndUt = observedWindowEndUt,
                    DisplayWindowStartUt = displayWindowStartUt,
                    DisplayWindowEndUt = displayWindowEndUt,
                    LabCredits = labCredits
                };
            }

            bool isPrimary = object.ReferenceEquals(primaryAction, anchorAction);
            if (isPrimary)
            {
                AbsorbLabCreditsIntoWindow(
                    primaryAction, anchorLeg, observedWindowStartUt, observedWindowEndUt,
                    actions, utCutoff, events, livePruneThreshold,
                    ref summedExpected, ref expectedCount, contributorIds, ref labCredits);
            }

            return new PostWalkWindowAggregate
            {
                Expected = summedExpected,
                ContributorCount = expectedCount,
                IsPrimary = isPrimary,
                ContributorLabel = FormatPostWalkContributorLabel(contributorIds, expectedCount),
                ObservedWindowStartUt = observedWindowStartUt,
                ObservedWindowEndUt = observedWindowEndUt,
                DisplayWindowStartUt = displayWindowStartUt,
                DisplayWindowEndUt = displayWindowEndUt,
                LabCredits = isPrimary ? labCredits : null
            };
        }

        /// <summary>
        /// Which coalesced-window member owns the comparison. Any subject or reward row
        /// outranks every <see cref="GameActionType.ScienceLabEarning"/>; within each class a
        /// recording-scoped row outranks an untagged one (the historical rule). A lab credit
        /// therefore never owns a comparison while a non-lab row of the same key is in its
        /// window, even an untagged one beside a tagged lab row: the untagged owner's group
        /// already holds the lab row (an untagged anchor shares scope with every row) and its
        /// observed side counts the lab's event (it matches every tag), so both sides agree,
        /// whereas a tagged lab owner would skip the earning's comparison and then compare
        /// alone, its own one-way scope leaving the earning's share out. Only a non-lab owner
        /// absorbs further lab credits (<see cref="AbsorbLabCreditsIntoWindow"/>). Pure.
        /// </summary>
        internal static int GetPostWalkPrimaryRank(GameAction action)
        {
            if (action == null) return -1;
            int rank = ActionHasRecordingScope(action) ? 1 : 0;
            if (action.Type != GameActionType.ScienceLabEarning) rank += 2;
            return rank;
        }

        /// <summary>
        /// Adds to a science comparison every <see cref="GameActionType.ScienceLabEarning"/>
        /// whose own ScienceChanged event that comparison's observed side already sums:
        /// same reason key, credit UT inside the observed window, and an event at that UT
        /// tagged to the lab row's recording passes the owner's scope filter
        /// (<see cref="PostWalkEventMatchesAction"/>, the filter the observed loop uses).
        /// Stock pays the lab through a bare AddScience under the subject science's own
        /// reason (VesselRecovery / ScienceTransmission) and the store coalesces same-UT
        /// deltas, so without this the lab's share was observed but never expected
        /// (LAB-RECOVERY-RECONCILE-COALESCED-FALSE-MISMATCH). It catches what the same-UT
        /// pass cannot: an untagged lab row beside a recording-scoped ScienceEarning (the
        /// action scope test is asymmetric there), and a mid-flight lab transmission
        /// inside an end-anchored ScienceEarning span. No-op when the owner is itself a lab
        /// row, for a non-science leg, and for credits already counted.
        /// </summary>
        private static void AbsorbLabCreditsIntoWindow(
            GameAction owner,
            PostWalkLeg ownerLeg,
            double windowStartUt,
            double windowEndUt,
            IReadOnlyList<GameAction> actions,
            double? utCutoff,
            IReadOnlyList<GameStateEvent> events,
            double livePruneThreshold,
            ref double summedExpected,
            ref int expectedCount,
            List<string> contributorIds,
            ref List<GameAction> labCredits)
        {
            if (owner == null || actions == null) return;
            if (owner.Type == GameActionType.ScienceLabEarning) return;
            if (ownerLeg.EventType != GameStateEventType.ScienceChanged) return;

            int absorbed = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var lab = actions[i];
                if (lab == null || lab.Type != GameActionType.ScienceLabEarning) continue;
                if (labCredits != null && labCredits.Contains(lab)) continue;
                if (lab.UT < windowStartUt || lab.UT > windowEndUt) continue;
                if (utCutoff.HasValue && lab.UT > utCutoff.Value) continue;

                var labExp = ClassifyPostWalk(lab);
                if (!labExp.Reconcile || !labExp.Sci.Applies) continue;
                if (!string.Equals(labExp.Sci.ReasonKey ?? "", ownerLeg.ReasonKey ?? "",
                        StringComparison.Ordinal))
                    continue;

                var labEventShape = new GameStateEvent
                {
                    ut = lab.UT,
                    eventType = GameStateEventType.ScienceChanged,
                    key = labExp.Sci.ReasonKey,
                    recordingId = lab.RecordingId ?? ""
                };
                if (!PostWalkEventMatchesAction(labEventShape, owner)) continue;
                if (IsOutsidePostWalkLiveCoverage(
                        lab, labExp, livePruneThreshold, events, actions, utCutoff))
                    continue;

                if (labCredits == null) labCredits = new List<GameAction>();
                labCredits.Add(lab);
                summedExpected += labExp.Sci.Expected;
                expectedCount++;
                contributorIds.Add("lab@" + LedgerOrchestrator.FormatFixed1(lab.UT));
                absorbed++;
            }

            if (absorbed > 0)
            {
                ParsekLog.VerboseRateLimited(Tag,
                    $"post-walk-lab-absorb:{owner.RecordingId ?? ""}:{ownerLeg.ReasonKey ?? ""}:{owner.UT.ToString("R", CultureInfo.InvariantCulture)}",
                    $"Post-walk science window: absorbed {absorbed} lab credit(s) keyed '{ownerLeg.ReasonKey}' " +
                    $"into {owner.Type} id={ActionIdForPostWalk(owner)} " +
                    $"window=[{LedgerOrchestrator.FormatFixed1(windowStartUt)},{LedgerOrchestrator.FormatFixed1(windowEndUt)}]");
            }
        }

        /// <summary>
        /// The one membership test for a comparison's observed side: a live (or, for the
        /// timeline-hidden coverage probe, a hidden) event of the leg's type and reason key,
        /// inside the window, that passes the owner's scope filter. CompareLeg's observed sum
        /// and the converter fold's attribution both read it, so the fold can only gross up
        /// an event the comparison actually counts.
        /// </summary>
        private static bool IsComparisonObservedEvent(
            GameStateEvent e,
            GameAction owner,
            PostWalkLeg leg,
            double windowStartUt,
            double windowEndUt,
            double livePruneThreshold,
            bool hiddenTimeline)
        {
            if (!IsEventInTimelineMode(e, livePruneThreshold, hiddenTimeline)) return false;
            if (e.eventType != leg.EventType) return false;
            if (e.ut < windowStartUt || e.ut > windowEndUt) return false;
            if (!PostWalkEventMatchesAction(e, owner)) return false;
            return string.Equals(e.key ?? "", leg.ReasonKey ?? "", StringComparison.Ordinal);
        }

        private static bool IsEventInTimelineMode(
            GameStateEvent e, double livePruneThreshold, bool hiddenTimeline)
        {
            if (e.ut <= livePruneThreshold) return false;
            bool visible = GameStateStore.IsEventVisibleToCurrentTimeline(e);
            return hiddenTimeline ? !visible : visible;
        }

        /// <summary>Result of <see cref="ComputeConverterScienceFold"/>.</summary>
        internal struct ConverterScienceFold
        {
            public double Take;
            public double Yield;
            public int Rows;
            public int Ambiguous;
        }

        /// <summary>
        /// The query-family strategy legs a science comparison's observed side must be
        /// grossed back up through. A <see cref="GameActionType.StrategyScienceDebit"/>
        /// with <see cref="StrategyConversionSource.Converter"/> source is the take a
        /// CurrencyConverter (Patents Licensing) diverted out of a science credit, so the
        /// ScienceChanged KSP fired is that much SHORT of the gross figure every expected
        /// science leg carries; a <see cref="GameActionType.StrategyScienceCredit"/> is a
        /// converter YIELD that rode in on a credit under the transaction's own reason, so it
        /// sits on the observed side with no expected counterpart. Exchanger-sourced debits
        /// move science under their own StrategyInput key, never a reward key, and are left
        /// out (the KSC door's rule).
        /// <para>
        /// The rows carry no reason key, so each is ATTRIBUTED through the event it rode on:
        /// a positive ScienceChanged within the coalesce epsilon of the row, with a
        /// compatible recording tag (equal, or either side untagged). The row folds in only
        /// when at least one such event is counted by THIS comparison
        /// (<see cref="IsComparisonObservedEvent"/>) and every such event is: a same-UT
        /// credit under another reason key or outside this comparison's scope makes the row
        /// ambiguous, and it is left out (<see cref="ConverterScienceFold.Ambiguous"/>)
        /// rather than gross two comparisons up with one take. Reads nothing but its
        /// inputs and the current-timeline visibility filter the observed side uses.
        /// </para>
        /// </summary>
        internal static ConverterScienceFold ComputeConverterScienceFold(
            GameAction owner,
            PostWalkLeg leg,
            double windowStartUt,
            double windowEndUt,
            IReadOnlyList<GameAction> actions,
            IReadOnlyList<GameStateEvent> events,
            double? utCutoff,
            double livePruneThreshold,
            bool hiddenTimeline)
        {
            var fold = new ConverterScienceFold();
            if (owner == null || actions == null || events == null || events.Count == 0)
                return fold;
            if (leg.EventType != GameStateEventType.ScienceChanged)
                return fold;

            for (int i = 0; i < actions.Count; i++)
            {
                var row = actions[i];
                if (row == null) continue;
                bool isTake = row.Type == GameActionType.StrategyScienceDebit
                    && row.ConversionSource == StrategyConversionSource.Converter;
                bool isYield = row.Type == GameActionType.StrategyScienceCredit;
                if (!isTake && !isYield) continue;
                if (row.UT < windowStartUt || row.UT > windowEndUt) continue;
                if (utCutoff.HasValue && row.UT > utCutoff.Value) continue;

                double amount = isTake ? row.Cost : row.ScienceAwarded;
                if (!(amount > 0.0)) continue;

                string rowTag = row.RecordingId ?? "";
                int candidates = 0;
                int counted = 0;
                for (int j = 0; j < events.Count; j++)
                {
                    var e = events[j];
                    if (e.eventType != GameStateEventType.ScienceChanged) continue;
                    if (!(e.valueAfter > e.valueBefore)) continue;
                    if (Math.Abs(e.ut - row.UT) > PostWalkReconcileEpsilonSeconds) continue;
                    if (!IsEventInTimelineMode(e, livePruneThreshold, hiddenTimeline)) continue;
                    string eventTag = e.recordingId ?? "";
                    if (rowTag.Length > 0 && eventTag.Length > 0
                        && !string.Equals(rowTag, eventTag, StringComparison.Ordinal))
                        continue;
                    candidates++;
                    if (IsComparisonObservedEvent(
                            e, owner, leg, windowStartUt, windowEndUt,
                            livePruneThreshold, hiddenTimeline))
                        counted++;
                }

                if (counted == 0)
                    continue;
                if (counted < candidates)
                {
                    fold.Ambiguous++;
                    continue;
                }

                if (isTake) fold.Take += amount;
                else fold.Yield += amount;
                fold.Rows++;
            }

            return fold;
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
