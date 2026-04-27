using KscActionExpectation = Parsek.LedgerOrchestrator.KscActionExpectation;
using KscExpectationLeg = Parsek.LedgerOrchestrator.KscExpectationLeg;
using KscExpectationLegMode = Parsek.LedgerOrchestrator.KscExpectationLegMode;
using KscReconcileClass = Parsek.LedgerOrchestrator.KscReconcileClass;

namespace Parsek
{
    internal static class KscActionExpectationClassifier
    {
        internal static KscActionExpectation ClassifyAction(GameAction action)
        {
            if (action == null)
                return new KscActionExpectation { Class = KscReconcileClass.NoResourceImpact };

            switch (action.Type)
            {
                // ---- Untransformed: raw field equals post-walk contribution. ----

                case GameActionType.FundsSpending:
                    // The KSC path uses FundsSpending for part purchases (via
                    // ConvertPartPurchased -> source=Other) and for rollout deductions
                    // (#445, OnVesselRolloutSpending -> source=VesselBuild). Each pairs
                    // with a different KSP TransactionReasons key on FundsChanged.
                    // Strategy input (source=Strategy) is not yet captured on KSC (Phase
                    // E1.5) - skip if we ever see it. Stock bypass=true part unlocks
                    // now record a zero charged cost in GameStateRecorder, so they stay
                    // on the untransformed path here and short-circuit later via the
                    // zero-expected-delta early return in ReconcileKscAction.
                    if (action.FundsSpendingSource == FundsSpendingSource.Strategy)
                    {
                        return new KscActionExpectation
                        {
                            Class = KscReconcileClass.Transformed,
                            SkipReason = "strategy spending not yet KSC-captured (Phase E1.5)"
                        };
                    }
                    if (action.FundsSpendingSource == FundsSpendingSource.VesselBuild)
                    {
                        return new KscActionExpectation
                        {
                            Class = KscReconcileClass.Untransformed,
                            FundsLeg = CreateExpectationLeg(
                                GameStateEventType.FundsChanged,
                                "VesselRollout",
                                -action.FundsSpent)
                        };
                    }
                    return new KscActionExpectation
                    {
                        Class = KscReconcileClass.Untransformed,
                        FundsLeg = CreateExpectationLeg(
                            GameStateEventType.FundsChanged,
                            "RnDPartPurchase",
                            -action.FundsSpent)
                    };

                case GameActionType.ScienceSpending:
                    return new KscActionExpectation
                    {
                        Class = KscReconcileClass.Untransformed,
                        ScienceLeg = CreateExpectationLeg(
                            GameStateEventType.ScienceChanged,
                            LedgerOrchestrator.TechResearchScienceReasonKey,
                            -action.Cost)
                    };

                case GameActionType.FacilityUpgrade:
                    return new KscActionExpectation
                    {
                        Class = KscReconcileClass.Untransformed,
                        FundsLeg = CreateExpectationLeg(
                            GameStateEventType.FundsChanged,
                            "StructureConstruction",
                            -action.FacilityCost)
                    };

                case GameActionType.FacilityRepair:
                    return new KscActionExpectation
                    {
                        Class = KscReconcileClass.Untransformed,
                        FundsLeg = CreateExpectationLeg(
                            GameStateEventType.FundsChanged,
                            "StructureRepair",
                            -action.FacilityCost)
                    };

                case GameActionType.KerbalHire:
                    return new KscActionExpectation
                    {
                        Class = KscReconcileClass.Untransformed,
                        FundsLeg = CreateExpectationLeg(
                            GameStateEventType.FundsChanged,
                            "CrewRecruited",
                            -action.HireCost)
                    };

                case GameActionType.ContractAccept:
                    // Advance funds are unconditional (FundsModule.ProcessContractAccept
                    // does not strategy-transform the advance). Zero-advance contracts
                    // produce no FundsChanged event and are silent.
                    return new KscActionExpectation
                    {
                        Class = KscReconcileClass.Untransformed,
                        FundsLeg = CreateExpectationLeg(
                            GameStateEventType.FundsChanged,
                            "ContractAdvance",
                            action.AdvanceFunds)
                    };

                case GameActionType.StrategyActivate:
                    // Strategy.Activate() charges all three setup-cost resources with the
                    // same TransactionReasons.StrategySetup key. Funds and science are
                    // direct deltas. Reputation goes through KSP's granular curve, so the
                    // reconciliation leg marks that and ReconcileKscAction derives the
                    // expected actual delta from the observed starting rep.
                    var strategyExpectation = new KscActionExpectation
                    {
                        Class = KscReconcileClass.Untransformed
                    };
                    if (action.SetupCost != 0f)
                    {
                        strategyExpectation.FundsLeg = CreateExpectationLeg(
                            GameStateEventType.FundsChanged,
                            "StrategySetup",
                            -action.SetupCost);
                    }
                    if (action.SetupScienceCost != 0f)
                    {
                        strategyExpectation.ScienceLeg = CreateExpectationLeg(
                            GameStateEventType.ScienceChanged,
                            "StrategySetup",
                            -action.SetupScienceCost);
                    }
                    if (action.SetupReputationCost != 0f)
                    {
                        strategyExpectation.ReputationLeg = CreateExpectationLeg(
                            GameStateEventType.ReputationChanged,
                            "StrategySetup",
                            -action.SetupReputationCost,
                            KscExpectationLegMode.ReputationCurve);
                    }
                    return strategyExpectation;

                // ---- Transformed: raw fields do not equal post-walk contribution. ----

                case GameActionType.ContractComplete:
                    // FundsReward/RepReward/ScienceReward subject to StrategiesModule's
                    // TransformedFundsReward/Rep/Science during the walk; RepReward also
                    // passes through ApplyReputationCurve. Per-action KSC path stays
                    // silent; post-walk hook reconciles (#440).
                    return new KscActionExpectation
                    {
                        Class = KscReconcileClass.Transformed,
                        SkipReason = "contract rewards -- post-walk hook reconciles (#440)"
                    };

                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    // FundsPenalty is raw, but RepPenalty passes through the reputation
                    // curve. Post-walk hook reconciles both legs (#440).
                    return new KscActionExpectation
                    {
                        Class = KscReconcileClass.Transformed,
                        SkipReason = "contract penalty -- post-walk hook reconciles (#440)"
                    };

                case GameActionType.MilestoneAchievement:
                    // Rep leg goes through the curve; funds/sci are identity today but
                    // post-walk hook reconciles all three legs (#440) for regression
                    // safety if a mod introduces a transform.
                    return new KscActionExpectation
                    {
                        Class = KscReconcileClass.Transformed,
                        SkipReason = "milestone rewards -- post-walk hook reconciles (#440)"
                    };

                case GameActionType.ReputationEarning:
                case GameActionType.ReputationPenalty:
                    return new KscActionExpectation
                    {
                        Class = KscReconcileClass.Transformed,
                        SkipReason = "reputation curve -- post-walk hook reconciles (#440)"
                    };

                case GameActionType.FundsEarning:
                case GameActionType.ScienceEarning:
                    // Earning actions on the KSC path would be direct deposits (not seen
                    // today -- recovery/contract/milestone flow through their own action
                    // types). Post-walk hook reconciles the safety path (#440); strategy
                    // payout income will land here once #439 Phase C captures it.
                    return new KscActionExpectation
                    {
                        Class = KscReconcileClass.Transformed,
                        SkipReason = "direct earning -- post-walk hook reconciles (#440)"
                    };

                // ---- No resource impact: short-circuit silently. ----

                case GameActionType.KerbalAssignment:
                case GameActionType.KerbalRescue:
                case GameActionType.KerbalStandIn:
                case GameActionType.FacilityDestruction:
                case GameActionType.StrategyDeactivate:
                case GameActionType.FundsInitial:
                case GameActionType.ScienceInitial:
                case GameActionType.ReputationInitial:
                default:
                    return new KscActionExpectation { Class = KscReconcileClass.NoResourceImpact };
            }
        }

        private static KscExpectationLeg CreateExpectationLeg(
            GameStateEventType eventType,
            string expectedReasonKey,
            double expectedDelta,
            KscExpectationLegMode mode = KscExpectationLegMode.Direct)
        {
            return new KscExpectationLeg
            {
                IsPresent = true,
                EventType = eventType,
                ExpectedReasonKey = expectedReasonKey,
                ExpectedDelta = expectedDelta,
                Mode = mode
            };
        }
    }
}
