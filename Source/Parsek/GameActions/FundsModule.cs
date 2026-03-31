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
    ///   availableFunds = initialFunds + totalEarnings - totalCommittedSpendings
    ///   The pre-pass <see cref="ComputeTotalSpendings"/> sums all spending costs before the walk.
    ///   During the walk, <see cref="totalEarnings"/> accumulates effective earnings.
    ///   <see cref="GetAvailableFunds"/> returns the clamped-to-zero amount the player can spend.
    ///
    /// Second-tier: reads Effective flags set by first-tier modules (Milestones, Contracts).
    /// Pure computation — no KSP state access.
    /// </summary>
    internal class FundsModule : IResourceModule
    {
        private const string Tag = "Funds";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Career starting funds, extracted from save file via FundsInitial action.</summary>
        private double initialFunds;

        /// <summary>Running balance: initialFunds + earnings - spendings seen so far in the walk.</summary>
        private double runningBalance;

        /// <summary>
        /// Sum of ALL committed fund spendings on the entire timeline.
        /// Computed by <see cref="ComputeTotalSpendings"/> before the walk starts.
        /// Used by <see cref="GetAvailableFunds"/> for the reservation check.
        /// </summary>
        private double totalCommittedSpendings;

        /// <summary>
        /// Running sum of fund earnings accumulated during the walk.
        /// Updated by earning processing methods.
        /// Used by <see cref="GetAvailableFunds"/> for the reservation check.
        /// </summary>
        private double totalEarnings;

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

            ParsekLog.Verbose(Tag,
                $"Reset: prevSeed={prevSeed.ToString("R", IC)}, " +
                $"prevBalance={prevBalance.ToString("R", IC)}, " +
                $"prevSpendings={prevSpendings.ToString("R", IC)}, " +
                $"prevEarnings={prevEarnings.ToString("R", IC)}");
        }

        /// <summary>
        /// Pre-pass: sums all fund spending costs from the sorted action list before the walk starts.
        /// Required for the reservation system: availableFunds = initialFunds + totalEarnings - totalCommittedSpendings.
        /// </summary>
        public void PrePass(List<GameAction> actions)
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

            ParsekLog.Verbose(Tag,
                $"Milestone funds: +{amount.ToString("R", IC)}, " +
                $"milestoneId={action.MilestoneId ?? "(none)"}, " +
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
        ///   availableFunds = initialFunds + totalEarnings - totalCommittedSpendings
        ///
        /// Requires <see cref="ComputeTotalSpendings"/> to have been called before the walk.
        /// Returns 0 if the result would be negative (player cannot spend negative funds).
        /// </summary>
        internal double GetAvailableFunds()
        {
            double available = initialFunds + totalEarnings - totalCommittedSpendings;
            return available > 0.0 ? available : 0.0;
        }

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
    }
}
