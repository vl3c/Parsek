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
    internal class FundsModule : IResourceModule, ICashflowProjectionModule, IStockPoolModule, IReservedFloorModule
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
        // The unclamped minimum of the projection: negative when the committed future
        // overdraws the balance. Available is this floored at zero, which hides the
        // over-commit magnitude the currency tooltip needs to explain a deficit.
        private double projectedMinBalance;

        /// <summary>
        /// True when a FundsInitial action was processed during the current walk.
        /// When false, the module has no seed balance and patching should be skipped
        /// (avoids zeroing out KSP funds for saves without a ledger).
        /// </summary>
        private bool hasInitialSeed;

        /// <summary>
        /// Per destructible building id, whether the walk's own FacilityDestruction /
        /// FacilityRepair rows seen so far leave it destroyed (true) or intact (false).
        /// A building with no row yet is absent: its state is unknown to the ledger.
        /// Read by <see cref="ProcessFacilityRepair"/>, which runs before the facilities
        /// tier sees the same row.
        /// </summary>
        private readonly Dictionary<string, bool> buildingDestroyedInWalk =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>
        /// Stock's <c>AdvancedParams.AllowNegativeCurrency</c>, read once per walk in
        /// <see cref="Reset"/>. Off: a loss that is not a cost row stops at 0, as
        /// <c>Funding.AddFunds</c> stops it.
        /// </summary>
        private bool allowNegativeCurrency;

        /// <summary>
        /// The part of the walk's non-cost losses below 0 that stock forgives (its 0 clamp),
        /// summed over the walk. Added back in the no-projection availability so it stays the
        /// running balance.
        /// </summary>
        private double forgivenBelowZero;

        /// <summary>Per row id, what the walk's dispatch moved the running balance by
        /// (<see cref="IStockPoolModule.GetRowEffects"/>).</summary>
        private readonly Dictionary<string, double> rowEffects =
            new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>The reserved floor and the debt (committed-value rule plan section 2.3).</summary>
        private readonly ReservedFloorWalkState floorState = new ReservedFloorWalkState();

        /// <summary>Per row id, what stock paid beyond the walk effect (design 3.15 step 5,
        /// <see cref="IStockPoolModule.GetTakeBackDeltas"/>).</summary>
        private readonly Dictionary<string, double> takeBackDeltas =
            new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>The stock shadow's lead over the walk's running value: the take-back
        /// amounts so far in this walk (the shadow counts each duplicate at its captured
        /// amount).</summary>
        private double shadowDivergence;

        /// <summary>Stock's own pool at a repeat (<see cref="StockShadowStart"/>).</summary>
        private readonly StockShadowStart shadowStart = new StockShadowStart(StockPool.Funds);

        /// <summary>The parts, part upgrades, facility levels and hires the walk owns so far
        /// (design 3.15 step 6, the purchase state rule).</summary>
        private readonly PurchaseOwnershipBook purchaseOwnership = new PurchaseOwnershipBook();

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
            projectedMinBalance = 0.0;
            hasInitialSeed = false;
            buildingDestroyedInWalk.Clear();
            forgivenBelowZero = 0.0;
            rowEffects.Clear();
            takeBackDeltas.Clear();
            shadowDivergence = 0.0;
            shadowStart.Reset(StockPoolReference.Funds);
            floorState.Reset();
            purchaseOwnership.Reset();
            allowNegativeCurrency = StockCurrencyClamp.ReadAllowNegativeCurrency();

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
        public bool PrePass(List<GameAction> actions, double? walkNowUT = null)
        {
            ComputeTotalSpendings(actions);
            return false;
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

            double before = runningBalance;
            DispatchAction(action);

            // Rule 4 (committed-value rule plan section 2.3): a loss with a committed cost
            // captured ahead of it stops at the reserved floor and the rest is a debt; with
            // none, stock's 0 clamp (phase 2). A cost row and an adjustment row are applied
            // in full either way.
            bool exempt = IsFundsCostRow(action.Type) || action.Type == GameActionType.StockPoolAdjustment;
            double floor = 0.0;
            bool floored = !exempt && floorState.TryGetLossFloor(action, out floor);
            if (!floored)
                ApplyStockZeroClamp(action, before);

            // The stock pool reference steps a row by what stock applied: its effect before
            // the settle and before any debt charge (both are Parsek-side target adjustments).
            double effect = runningBalance - before;
            if (!string.IsNullOrEmpty(action.ActionId))
            {
                double prior;
                rowEffects.TryGetValue(action.ActionId, out prior);
                rowEffects[action.ActionId] = prior + effect;
            }

            double stockDelta = action.TakeBackFunds != 0f
                ? RecordTakeBack(action, before, effect, (double)action.TakeBackFunds)
                : 0.0;
            shadowStart.Note(action, effect + stockDelta);

            if (floored)
            {
                double heldBack = floorState.Settle(action, runningBalance, effect, floor);
                if (heldBack > 0.0)
                {
                    runningBalance += heldBack;
                    ParsekLog.VerboseRateLimited(Tag,
                        "floor-settle-" + (action.ActionId ?? action.Type.ToString()),
                        $"Floor settle: {action.Type} id={action.ActionId ?? "(none)"} " +
                        $"ut={action.UT.ToString("R", IC)} effect={effect.ToString("R", IC)} " +
                        $"floor={floor.ToString("R", IC)} heldBack={heldBack.ToString("R", IC)} " +
                        $"debt={floorState.Debt.ToString("R", IC)} " +
                        $"runningBalance={runningBalance.ToString("R", IC)} " +
                        "(a committed cost captured before this loss needs the rest)");
                }
            }

            double charged = floorState.Charge(action, runningBalance);
            if (charged > 0.0)
            {
                runningBalance -= charged;
                ParsekLog.VerboseRateLimited(Tag,
                    "floor-charge-" + (action.ActionId ?? action.Type.ToString()),
                    $"Debt charge: {action.Type} id={action.ActionId ?? "(none)"} " +
                    $"ut={action.UT.ToString("R", IC)} charged={charged.ToString("R", IC)} " +
                    $"debt={floorState.Debt.ToString("R", IC)} " +
                    $"runningBalance={runningBalance.ToString("R", IC)}");
            }
        }

        // ================================================================
        // IReservedFloorModule (committed-value rule plan section 2.3)
        // ================================================================

        /// <inheritdoc />
        public StockPool FloorPool => StockPool.Funds;

        /// <inheritdoc />
        public void ClassifyFloorRow(GameAction action, out double delta, out bool isCost, out bool floorLimited)
        {
            if (!TryGetRowDelta(action, out delta))
                delta = 0.0;
            isCost = action != null && IsFundsCostRow(action.Type) && delta < 0.0;
            floorLimited = false;
        }

        /// <inheritdoc />
        public void SetReservedFloorPlan(ReservedFloorPlan plan)
        {
            floorState.Plan = plan;
        }

        /// <inheritdoc />
        public double GetOutstandingDebt()
        {
            return floorState.Debt;
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, double> GetRowEffects()
        {
            return rowEffects;
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, double> GetTakeBackDeltas()
        {
            return takeBackDeltas;
        }

        /// <summary>
        /// The stock shadow for one row (design 3.15 step 5): stock paid the row's captured
        /// amount, so beyond the walk's effect it moved the pool by the take-back part, with
        /// stock's 0 clamp at stock's own pool when it paid the row (<see cref="StockShadowStart"/>).
        /// Returns that take-back part.
        /// </summary>
        private double RecordTakeBack(GameAction action, double before, double effect, double takeBack)
        {
            if (string.IsNullOrEmpty(action.ActionId))
                return 0.0;
            double shadowBefore = shadowStart.Before(action.CaptureSequence, before + shadowDivergence);
            double shadowEffect = StockShadowTakeBack.ShadowCurrencyEffect(
                shadowBefore, effect, takeBack, allowNegativeCurrency);
            double delta = shadowEffect - effect;
            if (delta == 0.0)
                return 0.0;
            shadowDivergence += delta;
            double prior;
            takeBackDeltas.TryGetValue(action.ActionId, out prior);
            takeBackDeltas[action.ActionId] = prior + delta;
            ParsekLog.Verbose(Tag,
                $"Stock shadow: {action.Type} id={action.ActionId} ut={action.UT.ToString("R", IC)} " +
                $"walkEffect={effect.ToString("R", IC)} stockPaid={shadowEffect.ToString("R", IC)} " +
                $"takeBack={delta.ToString("R", IC)} (funds; {action.TakeBackWitness ?? "no witness"})");
            return delta;
        }

        private void DispatchAction(GameAction action)
        {
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
                // A contract parameter's own reward credits the pool like the contract's.
                case GameActionType.ContractParameterComplete:
                    ProcessContractComplete(action);
                    break;
                case GameActionType.ContractFail:
                    ProcessContractFail(action);
                    break;
                case GameActionType.ContractCancel:
                    ProcessContractCancel(action);
                    break;
                case GameActionType.FacilityUpgrade:
                    ProcessFacilityUpgrade(action);
                    break;
                case GameActionType.FacilityDestruction:
                    ProcessFacilityDestruction(action);
                    break;
                case GameActionType.FacilityRepair:
                    ProcessFacilityRepair(action);
                    break;
                case GameActionType.KerbalHire:
                    ProcessKerbalHire(action);
                    break;
                case GameActionType.StrategyActivate:
                    ProcessStrategySetupCost(action);
                    break;
                case GameActionType.StockPoolAdjustment:
                    ProcessStockPoolAdjustment(action);
                    break;
                // Supply-route rows are NOT cased here: supply routes carry no money
                // (todo LOGISTICS-WITHOUT-MONEY), so every route row falls through and
                // moves no funds. Do NOT add a case: a funds mutation here would be a
                // phantom charge.
                // All other action types: ignore silently
            }
        }

        /// <summary>
        /// A cost row: a purchase or strategy setup the player chose (committed-value rule
        /// plan section 2.3). Never forgiven at 0: a purchase is paid in full or refused.
        /// </summary>
        internal static bool IsFundsCostRow(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.FundsSpending:
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityRepair:
                case GameActionType.KerbalHire:
                case GameActionType.StrategyActivate:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Stock's 0 clamp, applied in the walk (committed-value rule plan section 2.3, the
        /// no-cost-ahead case, built ahead of the floor and the debt): a row that is not a
        /// cost row and takes the running balance below 0 stops at 0 and the rest is
        /// forgiven, as <c>Funding.AddFunds</c> does with <c>AllowNegativeCurrency</c> off.
        /// A row that starts below 0 (a cost row already overdrew the walk) is forgiven in
        /// full, never raised.
        /// </summary>
        private void ApplyStockZeroClamp(GameAction action, double before)
        {
            double forgiven = StockCurrencyClamp.ForgivenBelowZero(
                before, runningBalance,
                IsFundsCostRow(action.Type) || action.Type == GameActionType.StockPoolAdjustment,
                allowNegativeCurrency);
            if (forgiven <= 0.0)
                return;

            runningBalance += forgiven;
            forgivenBelowZero += forgiven;
            ParsekLog.VerboseRateLimited(Tag,
                "zero-clamp-" + (action.ActionId ?? action.Type.ToString()),
                $"Zero clamp: {action.Type} id={action.ActionId ?? "(none)"} " +
                $"ut={action.UT.ToString("R", IC)} before={before.ToString("R", IC)} " +
                $"forgiven={forgiven.ToString("R", IC)} " +
                $"runningBalance={runningBalance.ToString("R", IC)} " +
                $"forgivenTotal={forgivenBelowZero.ToString("R", IC)} (stock stops this loss at 0)");
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
                        // A conversion leg counts its share (design 3.15 step 7; 1 elsewhere).
                        totalCommittedSpendings += (double)a.FundsSpent * a.ConversionShare;
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

            // A strategy conversion leg earns its cause's share (design 3.15 step 7); every
            // other row carries 1.
            double amount = (double)action.FundsAwarded * action.ConversionShare;
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
        /// Sets <see cref="GameAction.Affordable"/>. A part or part upgrade purchase the walk
        /// already owns is not charged (design 3.15 step 6).
        /// </summary>
        private void ProcessFundsSpending(GameAction action)
        {
            // A strategy conversion leg is charged its cause's share (design 3.15 step 7).
            double cost = (double)action.FundsSpent * action.ConversionShare;
            if (SkipOwnedPurchase(action, cost))
                return;
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

            // A partly credited world-record row earns only its new thresholds (3.15 step 4).
            double amount = (double)action.CreditedMilestoneFunds;
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
                    $"{action.Type} funds skipped (not effective): " +
                    $"contractId={action.ContractId ?? "(none)"}{FormatParameterSuffix(action)}, " +
                    $"fundsReward={action.TransformedFundsReward.ToString("R", IC)}");
                return;
            }

            double reward = (double)action.TransformedFundsReward;
            if (reward <= 0.0)
                return;

            runningBalance += reward;
            totalEarnings += reward;

            ParsekLog.Verbose(Tag,
                $"{action.Type} funds: +{reward.ToString("R", IC)}, " +
                $"contractId={action.ContractId ?? "(none)"}{FormatParameterSuffix(action)}, " +
                $"runningBalance={runningBalance.ToString("R", IC)}, " +
                $"totalEarnings={totalEarnings.ToString("R", IC)}");
        }

        /// <summary>
        /// <c>", param=&lt;id&gt;"</c> for a ContractParameterComplete row, empty otherwise.
        /// </summary>
        internal static string FormatParameterSuffix(GameAction action)
        {
            return action != null && action.Type == GameActionType.ContractParameterComplete
                ? ", param=" + (action.ContractParameterId ?? "(none)")
                : "";
        }

        private void ProcessContractFail(GameAction action) => ProcessContractPenalty(action, "ContractFail");
        private void ProcessContractCancel(GameAction action) => ProcessContractPenalty(action, "ContractCancel");

        /// <summary>
        /// Shared processing for ContractFail/ContractCancel: deducts FundsPenalty from running balance.
        /// Gated on Effective: ContractsModule marks a fail / cancel of a contract whose
        /// lifecycle already ended earlier in the walk ineffective, so the penalty is charged
        /// once, on the transition, as stock charges it.
        /// </summary>
        private void ProcessContractPenalty(GameAction action, string label)
        {
            double penalty = (double)action.FundsPenalty;
            if (penalty <= 0.0)
                return;

            if (!action.Effective)
            {
                // ComputeTotalSpendings (PrePass, before Effective is known) counted this
                // penalty; take it back out so the no-projection GetAvailableFunds, which
                // PatchFunds writes on every cutoff-less recalc, does not charge it.
                UncountPrePassSpending(penalty);
                ParsekLog.Verbose(Tag,
                    $"{label} penalty skipped (not effective: contract already resolved), " +
                    $"penalty={penalty.ToString("R", IC)}, " +
                    $"contractId={action.ContractId ?? "(none)"}, " +
                    $"runningBalance={runningBalance.ToString("R", IC)}");
                return;
            }

            runningBalance -= penalty;

            ParsekLog.Verbose(Tag,
                $"{label} penalty: -{penalty.ToString("R", IC)}, " +
                $"contractId={action.ContractId ?? "(none)"}, " +
                $"runningBalance={runningBalance.ToString("R", IC)}");
        }

        /// <summary>
        /// Removes a spending that <see cref="ComputeTotalSpendings"/> counted in PrePass but
        /// the walk found ineffective (a fail / cancel of a resolved contract, a repair of an
        /// intact building). PrePass runs before any module sets Effective, so it cannot
        /// decide these rows itself.
        /// </summary>
        private void UncountPrePassSpending(double amount)
        {
            if (amount > 0.0)
                totalCommittedSpendings -= amount;
        }

        /// <summary>
        /// Processes FacilityDestruction: no funds move (stock charges nothing for a
        /// collapse); records the building as destroyed for <see cref="ProcessFacilityRepair"/>.
        /// </summary>
        private void ProcessFacilityDestruction(GameAction action)
        {
            if (string.IsNullOrEmpty(action.FacilityId))
                return;
            buildingDestroyedInWalk[action.FacilityId] = true;
        }

        /// <summary>
        /// Pure: whether a FacilityRepair row charges its cost, given the building's state
        /// from the walk's own destruction / repair rows before it (null = no row yet).
        /// Stock can only repair a destroyed building, so a repair the walk finds INTACT
        /// (its last row was a repair, e.g. a second repair of one destruction after a
        /// rewind) is a no-op and is free. A building the walk has no row for charges:
        /// the repair row itself proves stock found it destroyed, and a destruction from
        /// before the ledger began leaves no row (the ledger never infers "intact" from the
        /// absence of a row, as <c>FacilityStatePatcher.PatchLiveDestructionState</c>).
        /// </summary>
        internal static bool ShouldChargeFacilityRepair(bool? buildingDestroyedBefore)
        {
            return !buildingDestroyedBefore.HasValue || buildingDestroyedBefore.Value;
        }

        /// <summary>
        /// Processes FacilityRepair: deducts FacilityCost when the walk finds the building
        /// destroyed (or has no row for it); a repair of a building the walk already has
        /// intact is marked ineffective and charges nothing.
        /// </summary>
        private void ProcessFacilityRepair(GameAction action)
        {
            string buildingId = action.FacilityId;
            bool? destroyedBefore = null;
            bool known;
            if (!string.IsNullOrEmpty(buildingId)
                && buildingDestroyedInWalk.TryGetValue(buildingId, out known))
            {
                destroyedBefore = known;
            }

            if (!ShouldChargeFacilityRepair(destroyedBefore))
            {
                action.Effective = false;
                action.NotCountedReason = GameActionNotCountedReason.FacilityAlreadyIntact;
                action.Affordable = true;
                UncountPrePassSpending((double)action.FacilityCost);
                ParsekLog.Info(Tag,
                    $"FacilityRepair not charged: building '{buildingId}' is already intact " +
                    $"at ut={action.UT.ToString("R", IC)} (its last destruction was already repaired " +
                    $"earlier in the walk), cost={action.FacilityCost.ToString("R", IC)}, " +
                    $"recording={action.RecordingId ?? "(none)"}, " +
                    $"runningBalance={runningBalance.ToString("R", IC)}");
                return;
            }

            if (!string.IsNullOrEmpty(buildingId))
                buildingDestroyedInWalk[buildingId] = false;
            ProcessFacilityCost(action, "FacilityRepair");
        }

        /// <summary>
        /// Processes FacilityUpgrade (an upgrade or a Rebuild): a level the walk already has the
        /// facility at is not charged (design 3.15 step 6), else the cost is deducted.
        /// </summary>
        private void ProcessFacilityUpgrade(GameAction action)
        {
            if (SkipOwnedPurchase(action, (double)action.FacilityCost))
                return;
            ProcessFacilityCost(action, "FacilityUpgrade");
        }

        /// <summary>
        /// The purchase state rule (design 3.15 step 6, <see cref="PurchaseStateRule"/>): true
        /// when the walk already owns the row's item here, bought by a purchase captured after
        /// it; the row is then marked not counted and charges nothing. Otherwise the row's item
        /// is recorded as owned (the caller charges it) and false is returned.
        /// </summary>
        private bool SkipOwnedPurchase(GameAction action, double cost)
        {
            PurchaseKind kind;
            string key;
            if (!PurchaseStateRule.TryGetItem(action, out kind, out key))
                return false;
            string holder;
            if (!purchaseOwnership.IsAlreadyOwned(action, out holder))
            {
                purchaseOwnership.RecordCharged(action);
                return false;
            }
            action.Effective = false;
            action.NotCountedReason = GameActionNotCountedReason.PurchaseAlreadyOwned;
            action.Affordable = true;
            UncountPrePassSpending(cost);
            ParsekLog.InfoRateLimited(Tag, "purchase-owned-" + (action.ActionId ?? kind + "|" + key),
                $"{action.Type} not charged: {PurchaseStateRule.DescribeItem(kind, key, action.ToLevel)} " +
                $"is already owned at ut={action.UT.ToString("R", IC)} " +
                $"cseq={action.CaptureSequence.ToString(IC)} (held by {holder}, captured after this row), " +
                $"cost={cost.ToString("R", IC)}, recording={action.RecordingId ?? "(none)"}, " +
                $"runningBalance={runningBalance.ToString("R", IC)}");
            return true;
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
        /// Processes KerbalHire: deducts the price captured with the hire (owner ruling
        /// 2026-10-26: an earlier new hire changing the roster size never re-prices it). A
        /// kerbal the walk already has on the roster is not charged (design 3.15 step 6).
        /// </summary>
        private void ProcessKerbalHire(GameAction action)
        {
            double cost = (double)action.HireCost;
            if (SkipOwnedPurchase(action, cost))
                return;
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

        /// <summary>
        /// Processes a funds <see cref="GameActionType.StockPoolAdjustment"/>: the amount stock
        /// already applied to the pool, as stored and never clamped (the live pool shows it).
        /// Counted with the earnings (signed), so the no-projection availability carries it.
        /// </summary>
        private void ProcessStockPoolAdjustment(GameAction action)
        {
            if (action.AdjustmentPool != StockPool.Funds)
                return;
            double amount = action.AdjustmentAmount;
            runningBalance += amount;
            totalEarnings += amount;
            ParsekLog.Verbose(Tag,
                $"StockPoolAdjustment: {amount.ToString("R", IC)} id={action.ActionId ?? "(none)"} " +
                $"ut={action.UT.ToString("R", IC)} runningBalance={runningBalance.ToString("R", IC)}");
        }

        // ================================================================
        // Query methods
        // ================================================================

        /// <summary>Returns the current running balance (seed + earnings - spendings seen so far).</summary>
        internal double GetRunningBalance()
        {
            return runningBalance;
        }

        /// <summary>What the walk forgave below 0 so far (stock's 0 clamp on non-cost losses).</summary>
        internal double GetForgivenBelowZero()
        {
            return forgivenBelowZero;
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

            double available = initialFunds + totalEarnings - totalCommittedSpendings
                + forgivenBelowZero + floorState.Debt;
            return available > 0.0 ? available : 0.0;
        }

        /// <summary>
        /// Installs a cashflow-aware availability value after a cutoff recalculation.
        /// </summary>
        public double GetProjectionCurrentBalance()
        {
            return runningBalance;
        }

        /// <summary>
        /// The unclamped minimum projected balance: the value <see cref="GetAvailableFunds"/>
        /// floors at zero. Negative means the committed future overdraws the balance by
        /// that much (a deficit the bar cannot show). Falls back to the legacy unclamped
        /// availability when no projection has been installed.
        /// </summary>
        internal double GetProjectionMinBalance()
        {
            if (hasProjectedAvailableFunds)
                return projectedMinBalance;
            return initialFunds + totalEarnings - totalCommittedSpendings + forgivenBelowZero
                + floorState.Debt;
        }

        public bool TryGetProjectionDelta(GameAction action, out double delta)
        {
            return TryGetRowDelta(action, out delta);
        }

        /// <summary>
        /// True for every action type this module moves the funds pool for, whatever the
        /// row's derived fields say. <see cref="TryGetRowDelta"/> answers false for an
        /// ineffective row of such a type; the stock pool reference
        /// (<see cref="StockPoolReference"/>) still counts that row, at 0.
        /// </summary>
        internal static bool AffectsFunds(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.FundsEarning:
                case GameActionType.MilestoneAchievement:
                case GameActionType.ContractAccept:
                case GameActionType.ContractComplete:
                case GameActionType.ContractParameterComplete:
                case GameActionType.FundsSpending:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityRepair:
                case GameActionType.KerbalHire:
                case GameActionType.StrategyActivate:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The funds a row moves the pool by after a walk populated its derived fields
        /// (<see cref="GameAction.Effective"/>, <see cref="GameAction.TransformedFundsReward"/>).
        /// Pure: reads only the row. Shared by the projection and the stock pool reference.
        /// </summary>
        internal static bool TryGetRowDelta(GameAction action, out double delta)
        {
            delta = 0.0;
            if (action == null)
                return false;

            switch (action.Type)
            {
                case GameActionType.FundsEarning:
                    if (!action.Effective) return false;
                    delta = (double)action.FundsAwarded * action.ConversionShare;
                    return true;
                case GameActionType.MilestoneAchievement:
                    if (!action.Effective) return false;
                    delta = (double)action.CreditedMilestoneFunds;
                    return true;
                case GameActionType.ContractAccept:
                    delta = (double)action.AdvanceFunds;
                    return true;
                case GameActionType.ContractComplete:
                case GameActionType.ContractParameterComplete:
                    if (!action.Effective) return false;
                    delta = (double)action.TransformedFundsReward;
                    return true;
                case GameActionType.FundsSpending:
                    // A part or upgrade the walk already owns is not charged (3.15 step 6).
                    if (!action.Effective) return false;
                    delta = -(double)action.FundsSpent * action.ConversionShare;
                    return true;
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    if (!action.Effective) return false;
                    delta = -(double)action.FundsPenalty;
                    return true;
                case GameActionType.FacilityUpgrade:
                    if (!action.Effective) return false;
                    delta = -(double)action.FacilityCost;
                    return true;
                case GameActionType.FacilityRepair:
                    if (!action.Effective) return false;
                    delta = -(double)action.FacilityCost;
                    return true;
                case GameActionType.KerbalHire:
                    if (!action.Effective) return false;
                    delta = -(double)action.HireCost;
                    return true;
                case GameActionType.StrategyActivate:
                    delta = -(double)action.SetupCost;
                    return true;
                case GameActionType.StockPoolAdjustment:
                    if (action.AdjustmentPool != StockPool.Funds) return false;
                    delta = action.AdjustmentAmount;
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
            projectedMinBalance = minProjectedBalance;
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

        /// <summary>One summary per walk of the floor and the debt (only when a plan is installed).</summary>
        public void PostWalk()
        {
            if (floorState.Plan == null)
                return;
            string summary = floorState.Summary();
            ParsekLog.VerboseOnChange(Tag, "floor-walk|funds", summary,
                $"Floor walk: pool=Funds {summary} runningBalance={runningBalance.ToString("R", IC)}");
        }
    }
}
