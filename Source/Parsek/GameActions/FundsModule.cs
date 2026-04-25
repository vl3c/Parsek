using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Second-tier funds resource module. Tracks career fund balance from a seeded initial
    /// value through all earning and spending actions.
    ///
    /// Implements the recalculation walk from design doc section 5.8:
    ///   - Seeded balance from career start (FundsInitial action)
    ///   - Earnings: milestone funds (check Effective flag), contract rewards/advances,
    ///     vessel recovery, other
    ///   - Spendings: vessel build, facility upgrade/repair, kerbal hire, contract penalties
    ///
    /// Reservation system (design doc section 5.6):
    ///   availableFunds(ut) is the minimum projected fund balance from ut through
    ///   the future committed ledger, clamped to zero.
    ///   During the walk, <see cref="totalEarnings"/> accumulates effective earnings.
    ///   <see cref="GetAvailableFunds"/> returns the amount the player can spend
    ///   without making a future committed funds spending unaffordable.
    ///
    /// Second-tier: reads Effective flags set by first-tier modules (Milestones, Contracts).
    /// Pure computation — no KSP state access.
    /// </summary>
    internal class FundsModule : IResourceModule, ICashflowProjectionModule
    {
        private const string Tag = "Funds";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Career starting funds, extracted from save file via FundsInitial action.</summary>
        private double initialFunds;

        /// <summary>Running balance: initialFunds + earnings - spendings seen so far in the walk.</summary>
        private double runningBalance;

        /// <summary>
        /// Sum of committed fund spendings in the current pre-pass action scope.
        /// Full-timeline cutoff availability is installed separately by the projection pass.
        /// </summary>
        private double totalCommittedSpendings;

        /// <summary>
        /// Running sum of fund earnings accumulated during the walk.
        /// Updated by earning processing methods.
        /// Used by <see cref="GetAvailableFunds"/> for the full-ledger reservation check.
        /// </summary>
        private double totalEarnings;

        /// <summary>
        /// Optional cashflow-aware availability computed by the recalculation engine after a
        /// cutoff walk. It projects future ledger deltas from the current running balance and
        /// returns the minimum future balance as the amount spendable right now.
        /// </summary>
        private bool hasProjectedAvailableFunds;
        private double projectedAvailableFunds;

        /// <summary>
        /// True when a FundsInitial action was processed during the current walk.
        /// When false, the module has no seed balance and patching should be skipped
        /// (avoids zeroing out KSP funds for saves without a ledger).
        /// </summary>
        private bool hasInitialSeed;

        // ================================================================
        // IResourceModule
        // ================================================================

        /// <summary>
        /// Resets all derived state before a recalculation walk.
        /// Clears balance, totals, and initial funds.
        /// </summary>
        public void Reset()
        {
            double prevBalance = runningBalance;
            double prevSpendings = totalCommittedSpendings;
            double prevEarnings = totalEarnings;
            double prevSeed = initialFunds;

            initialFunds = 0.0;
            runningBalance = 0.0;
            totalCommittedSpendings = 0.0;
            totalEarnings = 0.0;
            hasProjectedAvailableFunds = false;
            projectedAvailableFunds = 0.0;
            hasInitialSeed = false;

            ParsekLog.Verbose(Tag,
                $"Reset: prevSeed={prevSeed.ToString("R", IC)}, " +
                $"prevBalance={prevBalance.ToString("R", IC)}, " +
                $"prevSpendings={prevSpendings.ToString("R", IC)}, " +
                $"prevEarnings={prevEarnings.ToString("R", IC)}");
        }

        /// <summary>
        /// Pre-pass: sums fund spending costs from the sorted action list before the walk starts.
        /// For cutoff walks, the engine supplies only visible/current actions here and later
        /// installs a full-timeline projected availability value.
        /// </summary>
        /// <remarks>
        /// <paramref name="walkNowUT"/> is unused — the engine has already applied any UT
        /// cutoff to <paramref name="actions"/> before calling PrePass, so every action we
        /// see is in scope for the visible/current aggregate.
        /// </remarks>
        public void PrePass(List<GameAction> actions, double? walkNowUT = null)
        {
            ComputeTotalSpendings(actions);
        }

        /// <summary>
        /// Processes a single game action during the recalculation walk.
        /// Handles fund-affecting action types; ignores all others.
        ///
        /// Fund-affecting types:
        ///   FundsInitial — seed the balance
        ///   FundsEarning — direct fund earnings (recovery, other)
        ///   FundsSpending — direct fund spendings (vessel build, facility, hire, etc.)
        ///   MilestoneAchievement — milestone funds (if Effective, set by first-tier)
        ///   ContractAccept — advance funds
        ///   ContractComplete — completion reward (if Effective, set by first-tier)
        ///   ContractFail — fund penalty
        ///   ContractCancel — fund penalty
        /// </summary>
        public void ProcessAction(GameAction action)
        {
            if (action == null)
                return;

            switch (action.Type)
            {
                case GameActionType.FundsInitial:
                    ProcessFundsInitial(action);
                    break;
                case GameActionType.FundsEarning:
                    ProcessFundsEarning(action);
                    break;
                case GameActionType.FundsSpending:
                    ProcessFundsSpending(action);
                    break;
                case GameActionType.MilestoneAchievement:
                    ProcessMilestoneEarning(action);
                    break;
                case GameActionType.ContractAccept:
                    ProcessContractAccept(action);
                    break;
                case GameActionType.ContractComplete:
                    ProcessContractComplete(action);
                    break;
                case GameActionType.ContractFail:
                    ProcessContractFail(action);
                    break;
                case GameActionType.ContractCancel:
                    ProcessContractCancel(action);
                    break;
                case GameActionType.FacilityUpgrade:
                    ProcessFacilityCost(action, "FacilityUpgrade");
                    break;
                case GameActionType.FacilityRepair:
                    ProcessFacilityCost(action, "FacilityRepair");
                    break;
                case GameActionType.KerbalHire:
                    ProcessKerbalHire(action);
                    break;
                case GameActionType.StrategyActivate:
                    ProcessStrategySetupCost(action);
                    break;
                // All other action types: ignore silently
            }
        }

        // ================================================================
        // Pre-pass: compute total committed spendings
        // ================================================================

        /// <summary>
        /// Pre-pass: sums all fund spending costs from the action list before the walk starts.
        /// Called by the engine (or test harness) before <see cref="ProcessAction"/> is invoked.
        ///
        /// Spending types counted:
        ///   FundsSpending — direct spendings
        ///   ContractFail — fund penalty
        ///   ContractCancel — fund penalty
        ///
        /// Note: FundsInitial is not a spending. MilestoneAchievement and ContractComplete are
        /// earnings (not spendings). ContractAccept advance is an earning (funds into player account).
        /// </summary>
        internal void ComputeTotalSpendings(List<GameAction> actions)
        {
            totalCommittedSpendings = 0.0;

            if (actions == null)
            {
                ParsekLog.Verbose(Tag,
                    "ComputeTotalSpendings: null actions list, totalCommittedSpendings=0");
                return;
            }

            int spendingCount = 0;
            int penaltyCount = 0;
            int facilityCostCount = 0;
            int hireCostCount = 0;
            int setupCostCount = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null) continue;

                switch (a.Type)
                {
                    case GameActionType.FundsSpending:
                        totalCommittedSpendings += (double)a.FundsSpent;
                        spendingCount++;
                        break;
                    case GameActionType.ContractFail:
                    case GameActionType.ContractCancel:
                        totalCommittedSpendings += (double)a.FundsPenalty;
                        penaltyCount++;
                        break;
                    case GameActionType.FacilityUpgrade:
                    case GameActionType.FacilityRepair:
                        totalCommittedSpendings += (double)a.FacilityCost;
                        facilityCostCount++;
                        break;
                    case GameActionType.KerbalHire:
                        totalCommittedSpendings += (double)a.HireCost;
                        hireCostCount++;
                        break;
                    case GameActionType.StrategyActivate:
                        totalCommittedSpendings += (double)a.SetupCost;
                        setupCostCount++;
                        break;
                }
            }

            ParsekLog.Verbose(Tag,
                $"ComputeTotalSpendings: spendings={spendingCount}, penalties={penaltyCount}, " +
                $"facilityCosts={facilityCostCount}, hireCosts={hireCostCount}, setupCosts={setupCostCount}, " +
                $"totalCommittedSpendings={totalCommittedSpendings.ToString("R", IC)}");
        }

        // ================================================================
        // Action processing
        // ================================================================

        /// <summary>
        /// Processes FundsInitial: sets the seed balance. Should appear exactly once per career.
        /// </summary>
        private void ProcessFundsInitial(GameAction action)
        {
            initialFunds = (double)action.InitialFunds;
            runningBalance += initialFunds;
            hasInitialSeed = true;

            ParsekLog.Info(Tag,
                $"FundsInitial: seed={initialFunds.ToString("R", IC)}, " +
                $"runningBalance={runningBalance.ToString("R", IC)}");
        }

        /// <summary>
        /// Processes FundsEarning: adds fundsAwarded to running balance.
        /// Checks Effective flag — milestone/contract-derived earnings may have been
        /// zeroed by first-tier modules.
        /// </summary>
        private void ProcessFundsEarning(GameAction action)
        {
            if (!action.Effective)
            {
                ParsekLog.Verbose(Tag,
                    $"FundsEarning skipped (not effective): " +
                    $"fundsAwarded={action.FundsAwarded.ToString("R", IC)}, " +
                    $"source={action.FundsSource}, " +
                    $"recordingId={action.RecordingId ?? "(none)"}");
                return;
            }

            double amount = (double)action.FundsAwarded;
            runningBalance += amount;
            totalEarnings += amount;

            ParsekLog.Verbose(Tag,
                $"FundsEarning: +{amount.ToString("R", IC)}, " +
                $"source={action.FundsSource}, " +
                $"runningBalance={runningBalance.ToString("R", IC)}, " +
                $"totalEarnings={totalEarnings.ToString("R", IC)}, " +
                $"recordingId={action.RecordingId ?? "(none)"}");
        }

        /// <summary>
        /// Processes FundsSpending: checks affordability, deducts from running balance.
        /// Sets <see cref="GameAction.Affordable"/>.
        /// </summary>
        private void ProcessFundsSpending(GameAction action)
        {
            double cost = (double)action.FundsSpent;
            bool affordable = runningBalance >= cost;
            action.Affordable = affordable;

            runningBalance -= cost;

            if (affordable)
            {
                if (cost == 0.0)
                    return;

                ParsekLog.Verbose(Tag,
                    $"FundsSpending: -{cost.ToString("R", IC)}, " +
                    $"source={action.FundsSpendingSource}, " +
                    $"affordable=true, " +
                    $"runningBalance={runningBalance.ToString("R", IC)}, " +
                    $"recordingId={action.RecordingId ?? "(none)"}");
            }
            else
            {
                ParsekLog.Warn(Tag,
                    $"FundsSpending NOT affordable: -{cost.ToString("R", IC)}, " +
                    $"source={action.FundsSpendingSource}, " +
                    $"runningBalance={runningBalance.ToString("R", IC)}, " +
                    $"recordingId={action.RecordingId ?? "(none)"} " +
                    "— possible bug or data corruption");
            }
        }

        /// <summary>
        /// Processes MilestoneAchievement: if effective (first-tier approved), adds
        /// MilestoneFundsAwarded to running balance.
        /// </summary>
        private void ProcessMilestoneEarning(GameAction action)
        {
            if (!action.Effective)
            {
                ParsekLog.Verbose(Tag,
                    $"Milestone funds skipped (not effective): " +
                    $"milestoneId={action.MilestoneId ?? "(none)"}, " +
                    $"fundsAwarded={action.MilestoneFundsAwarded.ToString("R", IC)}");
                return;
            }

            double amount = (double)action.MilestoneFundsAwarded;
            if (amount <= 0.0)
                return;

            runningBalance += amount;
            totalEarnings += amount;

            // Bug #593: this fires every recalc walk for every effective
            // record-milestone (RecordsSpeed/Altitude/Distance), producing 170+
            // identical lines per session. The state being logged is steady
            // across recalculations of the SAME action — the amount,
            // milestoneId, recordingId, and UT are all immutable — so
            // rate-limit per stable GameAction.ActionId. Two distinct
            // record-milestone hits with the same milestoneId+recordingId but
            // different UT/reward have different ActionIds and still log
            // separately on their first walk; only repeated recalculations
            // of the SAME action collapse.
            string actionIdKey = !string.IsNullOrEmpty(action.ActionId)
                ? action.ActionId
                : string.Format(IC, "{0}|{1}|{2}|{3}",
                    action.MilestoneId ?? "(none)",
                    action.RecordingId ?? "(none)",
                    action.UT.ToString("R", IC),
                    amount.ToString("R", IC));
            string key = "milestone-funds-action-" + actionIdKey;
            ParsekLog.VerboseRateLimited(Tag, key,
                $"Milestone funds: +{amount.ToString("R", IC)}, " +
                $"actionId={action.ActionId ?? "(none)"}, " +
                $"milestoneId={action.MilestoneId ?? "(none)"}, " +
                $"recordingId={action.RecordingId ?? "(none)"}, " +
                $"ut={action.UT.ToString("R", IC)}, " +
                $"runningBalance={runningBalance.ToString("R", IC)}, " +
                $"totalEarnings={totalEarnings.ToString("R", IC)}");
        }

        /// <summary>
        /// Processes ContractAccept: advance payment adds to running balance.
        /// Advance payments are unconditional — they are always credited on accept.
        /// </summary>
        private void ProcessContractAccept(GameAction action)
        {
            double advance = (double)action.AdvanceFunds;
            if (advance <= 0.0)
                return;

            runningBalance += advance;
            totalEarnings += advance;

            ParsekLog.Verbose(Tag,
                $"ContractAccept advance: +{advance.ToString("R", IC)}, " +
                $"contractId={action.ContractId ?? "(none)"}, " +
                $"runningBalance={runningBalance.ToString("R", IC)}, " +
                $"totalEarnings={totalEarnings.ToString("R", IC)}");
        }

        /// <summary>
        /// Processes ContractComplete: if effective (first-tier approved), adds FundsReward
        /// to running balance.
        /// </summary>
        private void ProcessContractComplete(GameAction action)
        {
            if (!action.Effective)
            {
                ParsekLog.Verbose(Tag,
                    $"ContractComplete funds skipped (not effective): " +
                    $"contractId={action.ContractId ?? "(none)"}, " +
                    $"fundsReward={action.TransformedFundsReward.ToString("R", IC)}");
                return;
            }

            double reward = (double)action.TransformedFundsReward;
            if (reward <= 0.0)
                return;

            runningBalance += reward;
            totalEarnings += reward;

            ParsekLog.Verbose(Tag,
                $"ContractComplete funds: +{reward.ToString("R", IC)}, " +
                $"contractId={action.ContractId ?? "(none)"}, " +
                $"runningBalance={runningBalance.ToString("R", IC)}, " +
                $"totalEarnings={totalEarnings.ToString("R", IC)}");
        }

        private void ProcessContractFail(GameAction action) => ProcessContractPenalty(action, "ContractFail");
        private void ProcessContractCancel(GameAction action) => ProcessContractPenalty(action, "ContractCancel");

        /// <summary>
        /// Shared processing for ContractFail/ContractCancel: deducts FundsPenalty from running balance.
        /// Penalties apply unconditionally (not gated by Effective flag).
        /// </summary>
        private void ProcessContractPenalty(GameAction action, string label)
        {
            double penalty = (double)action.FundsPenalty;
            if (penalty <= 0.0)
                return;

            runningBalance -= penalty;

            ParsekLog.Verbose(Tag,
                $"{label} penalty: -{penalty.ToString("R", IC)}, " +
                $"contractId={action.ContractId ?? "(none)"}, " +
                $"runningBalance={runningBalance.ToString("R", IC)}");
        }

        /// <summary>
        /// Processes FacilityUpgrade/FacilityRepair: deducts FacilityCost from running balance.
        /// </summary>
        private void ProcessFacilityCost(GameAction action, string label)
        {
            double cost = (double)action.FacilityCost;
            bool affordable = runningBalance >= cost;
            action.Affordable = affordable;

            runningBalance -= cost;

            ParsekLog.Verbose(Tag,
                $"{label}: -{cost.ToString("R", IC)}, " +
                $"facilityId={action.FacilityId ?? "(none)"}, " +
                $"affordable={affordable}, " +
                $"runningBalance={runningBalance.ToString("R", IC)}");
        }

        /// <summary>
        /// Processes KerbalHire: deducts HireCost from running balance.
        /// </summary>
        private void ProcessKerbalHire(GameAction action)
        {
            double cost = (double)action.HireCost;
            bool affordable = runningBalance >= cost;
            action.Affordable = affordable;

            runningBalance -= cost;

            ParsekLog.Verbose(Tag,
                $"KerbalHire: -{cost.ToString("R", IC)}, " +
                $"kerbalName={action.KerbalName ?? "(none)"}, " +
                $"affordable={affordable}, " +
                $"runningBalance={runningBalance.ToString("R", IC)}");
        }

        /// <summary>
        /// Processes StrategyActivate: deducts SetupCost from running balance.
        /// </summary>
        private void ProcessStrategySetupCost(GameAction action)
        {
            double cost = (double)action.SetupCost;
            if (cost <= 0.0)
                return;

            bool affordable = runningBalance >= cost;
            action.Affordable = affordable;

            runningBalance -= cost;

            ParsekLog.Verbose(Tag,
                $"StrategyActivate: -{cost.ToString("R", IC)}, " +
                $"strategyId={action.StrategyId ?? "(none)"}, " +
                $"affordable={affordable}, " +
                $"runningBalance={runningBalance.ToString("R", IC)}");
        }

        // ================================================================
        // Query methods
        // ================================================================

        /// <summary>Returns the current running balance (seed + earnings - spendings seen so far).</summary>
        internal double GetRunningBalance()
        {
            return runningBalance;
        }

        /// <summary>
        /// Returns the available funds after the reservation system is applied.
        /// This is the amount the player can actually spend at the current point in the walk.
        ///
        /// Formula (design doc 5.6):
        ///   availableFunds = min(projected fund balance from current UT through future ledger)
        ///
        /// For non-cutoff/full-ledger walks, this collapses to the legacy
        /// <c>initialFunds + totalEarnings - totalCommittedSpendings</c> calculation.
        /// Returns 0 if the result would be negative (player cannot spend negative funds).
        /// </summary>
        internal double GetAvailableFunds()
        {
            if (hasProjectedAvailableFunds)
                return projectedAvailableFunds;

            double available = initialFunds + totalEarnings - totalCommittedSpendings;
            return available > 0.0 ? available : 0.0;
        }

        /// <summary>
        /// Installs a cashflow-aware availability value after a cutoff recalculation.
        /// </summary>
        public double GetProjectionCurrentBalance()
        {
            return runningBalance;
        }

        public bool TryGetProjectionDelta(GameAction action, out double delta)
        {
            delta = 0.0;
            if (action == null)
                return false;

            switch (action.Type)
            {
                case GameActionType.FundsEarning:
                    if (!action.Effective) return false;
                    delta = (double)action.FundsAwarded;
                    return true;
                case GameActionType.MilestoneAchievement:
                    if (!action.Effective) return false;
                    delta = (double)action.MilestoneFundsAwarded;
                    return true;
                case GameActionType.ContractAccept:
                    delta = (double)action.AdvanceFunds;
                    return true;
                case GameActionType.ContractComplete:
                    if (!action.Effective) return false;
                    delta = (double)action.TransformedFundsReward;
                    return true;
                case GameActionType.FundsSpending:
                    delta = -(double)action.FundsSpent;
                    return true;
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    delta = -(double)action.FundsPenalty;
                    return true;
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityRepair:
                    delta = -(double)action.FacilityCost;
                    return true;
                case GameActionType.KerbalHire:
                    delta = -(double)action.HireCost;
                    return true;
                case GameActionType.StrategyActivate:
                    delta = -(double)action.SetupCost;
                    return true;
                default:
                    return false;
            }
        }

        public void SetProjectedAvailable(
            double available,
            double currentBalance,
            double minProjectedBalance,
            double finalProjectedBalance,
            int futureActions,
            int deltaActions)
        {
            projectedAvailableFunds = available > 0.0 ? available : 0.0;
            hasProjectedAvailableFunds = true;

            ParsekLog.Verbose(Tag,
                $"Projected availability: current={currentBalance.ToString("R", IC)}, " +
                $"minProjected={minProjectedBalance.ToString("R", IC)}, " +
                $"finalProjected={finalProjectedBalance.ToString("R", IC)}, " +
                $"available={projectedAvailableFunds.ToString("R", IC)}, " +
                $"futureActions={futureActions}, deltaActions={deltaActions}");
        }

        /// <summary>
        /// True when the module processed a FundsInitial action during the walk.
        /// When false, the module has no seed balance and KSP funds should not be patched.
        /// </summary>
        internal bool HasSeed => hasInitialSeed;

        /// <summary>Returns the career initial funds (seed value).</summary>
        internal double GetInitialFunds()
        {
            return initialFunds;
        }

        /// <summary>Returns the total effective earnings accumulated during the walk.</summary>
        internal double GetTotalEarnings()
        {
            return totalEarnings;
        }

        /// <summary>Returns the total committed spendings computed by the pre-pass.</summary>
        internal double GetTotalCommittedSpendings()
        {
            return totalCommittedSpendings;
        }

        public void PostWalk() { }
    }
}
