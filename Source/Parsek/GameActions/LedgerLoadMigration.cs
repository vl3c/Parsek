using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal static class LedgerLoadMigration
    {
        /// <summary>
        /// The supply-route ids of <see cref="GameActionType"/> (23-30 and 36). Route events
        /// left the ledger (todo LOGISTICS-WITHOUT-MONEY): nothing writes these types, and a
        /// row of one is kept verbatim as unreadable at load
        /// (<see cref="GameAction.ClassifyUnreadableActionNode"/>). The ids stay defined and
        /// reserved, never reused. An explicit switch, not a numeric range, so a later type can
        /// never widen the set.
        /// </summary>
        internal static bool IsReservedRouteActionType(GameActionType t)
        {
            switch (t)
            {
                case GameActionType.RouteDispatched:        // 23
                case GameActionType.RouteCargoDebited:      // 24
                case GameActionType.RouteCargoDelivered:    // 25
                case GameActionType.RoutePaused:            // 26
                case GameActionType.RouteEndpointLost:      // 27
                case GameActionType.RouteRecoveryCredited:  // 28
                case GameActionType.RouteCargoPickedUp:     // 29
                case GameActionType.RouteResumed:           // 30
                case GameActionType.RouteHeld:              // 36
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Pure classifier: returns true when the given action type actually moves the
        /// funds/science/reputation pools. Non-resource action types — roster changes
        /// (<see cref="GameActionType.KerbalAssignment"/>, <see cref="GameActionType.KerbalRescue"/>,
        /// <see cref="GameActionType.KerbalStandIn"/>), <see cref="GameActionType.FacilityDestruction"/>
        /// (stateful only, no cost), <see cref="GameActionType.StrategyDeactivate"/> (stateful only,
        /// no cost), and the three <c>*Initial</c> seed types — return false.
        ///
        /// <para>When adding a new <see cref="GameActionType"/>, this switch must be updated
        /// and the <c>IsResourceImpactingAction_Theory</c> test in
        /// <c>LegacyTreeMigrationTests.cs</c> will fail until an entry is added — which is
        /// the intended forcing function for a code review.</para>
        /// </summary>
        internal static bool IsResourceImpactingAction(GameActionType t)
        {
            switch (t)
            {
                // Earnings/spendings that directly move a pool.
                case GameActionType.FundsEarning:
                case GameActionType.FundsSpending:
                case GameActionType.ScienceEarning:
                case GameActionType.ScienceSpending:
                // Strategy currency-exchange science leg: ScienceModule debits the pool
                // by Cost, so it moves a resource exactly like a ScienceSpending.
                case GameActionType.StrategyScienceDebit:
                // Strategy currency-converter science yield: credits the pool, so it
                // moves a resource exactly like a ScienceEarning.
                case GameActionType.StrategyScienceCredit:
                // Science lab credit: credits the pool like a ScienceEarning.
                case GameActionType.ScienceLabEarning:
                // The drawdown guard's adjustment: moves one pool by its amount.
                case GameActionType.StockPoolAdjustment:
                case GameActionType.ReputationEarning:
                case GameActionType.ReputationPenalty:
                    return true;

                // Composite rewards/penalties consumed by first-tier + second-tier modules.
                case GameActionType.MilestoneAchievement:  // MilestoneFunds/Sci/RepAwarded
                case GameActionType.ContractAccept:        // AdvanceFunds
                case GameActionType.ContractComplete:      // FundsReward/ScienceReward/RepReward
                case GameActionType.ContractParameterComplete: // same three, one parameter's share
                case GameActionType.ContractFail:          // FundsPenalty/RepPenalty
                case GameActionType.ContractCancel:        // FundsPenalty/RepPenalty
                    return true;

                // Infrastructure/strategy costs consumed by FundsModule / StrategiesModule.
                case GameActionType.FacilityUpgrade:       // FacilityCost
                case GameActionType.FacilityRepair:        // FacilityCost
                case GameActionType.KerbalHire:            // HireCost
                case GameActionType.StrategyActivate:      // SetupCost
                    return true;

                // Non-resource action types: roster changes, stateful facility/strategy flips,
                // and the immutable seed rows. These are intentionally ignored by the
                // coverage probe — see class summary for the false-positive this guards.
                case GameActionType.KerbalAssignment:
                case GameActionType.KerbalRescue:
                case GameActionType.KerbalStandIn:
                // KerbalDismissal is a roster change; no pool moves.
                case GameActionType.KerbalDismissal:
                // KerbalRecovered bounds a crew reservation; no pool moves.
                case GameActionType.KerbalRecovered:
                // VesselRecovered is spawn evidence only; no pool moves.
                case GameActionType.VesselRecovered:
                // ResourceScanUnlocked flips stock scan state; no pool moves.
                case GameActionType.ResourceScanUnlocked:
                case GameActionType.FacilityDestruction:
                case GameActionType.StrategyDeactivate:
                case GameActionType.FundsInitial:
                case GameActionType.ScienceInitial:
                case GameActionType.ReputationInitial:
                    return false;

                default:
                    // Conservative: an unrecognized new action type is treated as non-
                    // resource so it cannot poison the zero-coverage probe. The
                    // IsResourceImpactingAction_Theory test will flag any new enum value
                    // that reaches this branch.
                    return false;
            }
        }
    }
}
