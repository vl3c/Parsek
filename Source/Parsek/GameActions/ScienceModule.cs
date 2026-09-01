using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Science resource module — tracks per-subject credited totals and running science balance.
    /// Implements the hard-cap walk from design doc section 4.6:
    ///   - For each ScienceEarning: headroom = maxValue - creditedTotal, effectiveScience = min(awarded, headroom)
    ///   - For each ScienceSpending: check affordability, deduct from running balance
    ///
    /// Reservation system (design doc section 4.5):
    ///   availableScience(ut) is the minimum projected science balance from ut through
    ///   the future committed ledger, clamped to zero.
    ///   During the walk, <see cref="totalEffectiveEarnings"/> accumulates effective earnings.
    ///   <see cref="GetAvailableScience"/> returns the amount the player can actually spend
    ///   without making a future committed science spending unaffordable.
    ///
    /// Pure computation — no KSP state access.
    /// </summary>
    internal class ScienceModule : IResourceModule, ICashflowProjectionModule
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Per-subject tracking state for the hard-cap walk.</summary>
        internal struct SubjectState
        {
            /// <summary>Total science credited to this subject across all recordings so far in the walk.</summary>
            public double CreditedTotal;

            /// <summary>Maximum science this subject can yield (scienceCap).</summary>
            public double MaxValue;
        }

        /// <summary>Per-subject state keyed by subjectId.</summary>
        private readonly Dictionary<string, SubjectState> subjects = new Dictionary<string, SubjectState>();

        /// <summary>Accumulated effective science balance (earnings minus spendings).</summary>
        private double runningScience;

        /// <summary>
        /// Sum of committed science spending costs in the current pre-pass action scope.
        /// Full-timeline cutoff availability is installed separately by the projection pass.
        /// </summary>
        private double totalCommittedSpendings;

        /// <summary>
        /// Running sum of effective science earnings accumulated during the walk.
        /// Updated in <see cref="ProcessEarning"/> alongside runningScience.
        /// Used by <see cref="GetAvailableScience"/> for the full-ledger reservation check.
        /// </summary>
        private double totalEffectiveEarnings;

        /// <summary>
        /// Optional cashflow-aware availability computed by the recalculation engine after a
        /// cutoff walk. It projects future ledger deltas from the current running balance and
        /// returns the minimum future balance as the amount spendable right now.
        /// </summary>
        private bool hasProjectedAvailableScience;
        private double projectedAvailableScience;
        // The unclamped minimum of the projection (see FundsModule.projectedMinBalance).
        private double projectedMinBalance;

        /// <summary>
        /// True when a ScienceInitial action was processed during the current walk.
        /// When false, the module has no seed balance and patching should be skipped.
        /// </summary>
        private bool hasInitialSeed;

        // ================================================================
        // IResourceModule
        // ================================================================

        /// <summary>
        /// Resets all derived state before a recalculation walk.
        /// Clears per-subject credited totals and resets running science to 0.
        /// </summary>
        public void Reset()
        {
            int subjectCount = subjects.Count;
            subjects.Clear();
            runningScience = 0.0;
            totalCommittedSpendings = 0.0;
            totalEffectiveEarnings = 0.0;
            hasProjectedAvailableScience = false;
            projectedAvailableScience = 0.0;
            projectedMinBalance = 0.0;
            hasInitialSeed = false;

            ParsekLog.Verbose("ScienceModule",
                $"Reset: cleared {subjectCount} subjects, runningScience=0, " +
                $"totalCommittedSpendings=0, totalEffectiveEarnings=0");
        }

        /// <summary>
        /// Pre-pass: sums ScienceSpending costs from the sorted action list before the walk starts.
        /// For cutoff walks, the engine supplies only visible/current actions here and later
        /// installs a full-timeline projected availability value.
        /// </summary>
        /// <remarks>
        /// <paramref name="walkNowUT"/> is unused — the engine has already applied any UT
        /// cutoff to <paramref name="actions"/> before PrePass runs.
        /// </remarks>
        public bool PrePass(List<GameAction> actions, double? walkNowUT = null)
        {
            ComputeTotalSpendings(actions);
            return false;
        }

        /// <summary>
        /// Processes a single game action during the recalculation walk.
        /// Handles ScienceEarning, ScienceSpending, ContractComplete (science reward),
        /// MilestoneAchievement (science reward), StrategyActivate (science setup cost),
        /// and ScienceInitial (mid-career seed); ignores all other action types.
        /// </summary>
        public void ProcessAction(GameAction action)
        {
            if (action == null)
                return;

            switch (action.Type)
            {
                case GameActionType.ScienceEarning:
                    ProcessEarning(action);
                    break;
                case GameActionType.ScienceSpending:
                    ProcessSpending(action);
                    break;
                case GameActionType.StrategyScienceDebit:
                    ProcessStrategyScienceDebit(action);
                    break;
                case GameActionType.StrategyScienceCredit:
                    ProcessStrategyScienceCredit(action);
                    break;
                case GameActionType.ContractComplete:
                    ProcessContractScienceReward(action);
                    break;
                case GameActionType.MilestoneAchievement:
                    ProcessMilestoneScienceReward(action);
                    break;
                case GameActionType.StrategyActivate:
                    ProcessStrategySetupScienceCost(action);
                    break;
                case GameActionType.ScienceInitial:
                    ProcessScienceInitial(action);
                    break;
                // All other action types: ignore silently
            }
        }

        // ================================================================
        // Pre-pass: compute total committed spendings
        // ================================================================

        /// <summary>
        /// Pre-pass: sums ScienceSpending costs from the action list before the walk starts.
        /// Called by the engine (or test harness) before <see cref="ProcessAction"/> is invoked.
        /// This supports the full-ledger reservation calculation; cutoff walks install a
        /// cashflow-projected value after the visible/current walk.
        /// </summary>
        internal void ComputeTotalSpendings(List<GameAction> actions)
        {
            totalCommittedSpendings = 0.0;

            if (actions == null)
            {
                ParsekLog.Verbose("ScienceModule",
                    "ComputeTotalSpendings: null actions list, totalCommittedSpendings=0");
                return;
            }

            int spendingCount = 0;
            int strategySetupCount = 0;
            int strategyExchangeCount = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null)
                    continue;

                if (action.Type == GameActionType.ScienceSpending)
                {
                    totalCommittedSpendings += (double)action.Cost;
                    spendingCount++;
                }
                else if (action.Type == GameActionType.StrategyActivate)
                {
                    totalCommittedSpendings += (double)action.SetupScienceCost;
                    strategySetupCount++;
                }
                else if (action.Type == GameActionType.StrategyScienceDebit)
                {
                    // A committed currency exchange must RESERVE science exactly like a
                    // tech cost: without this arm GetAvailableScience over-reports what
                    // the player can still spend by the traded-away amount.
                    totalCommittedSpendings += (double)action.Cost;
                    strategyExchangeCount++;
                }
            }

            ParsekLog.Verbose("ScienceModule",
                $"ComputeTotalSpendings: spendingCount={spendingCount}, " +
                $"strategySetupCount={strategySetupCount}, " +
                $"strategyExchangeCount={strategyExchangeCount}, " +
                $"totalCommittedSpendings={totalCommittedSpendings.ToString("R", IC)}");
        }

        // ================================================================
        // Earning
        // ================================================================

        /// <summary>
        /// Processes a ScienceEarning action: applies the subject hard cap, computes effective science,
        /// updates the credited total and running balance. Sets <see cref="GameAction.EffectiveScience"/>.
        /// </summary>
        internal void ProcessEarning(GameAction action)
        {
            string subjectId = action.SubjectId ?? "";
            float scienceAwarded = action.ScienceAwarded;
            float subjectMaxValue = action.SubjectMaxValue;

            // Get or initialize subject state
            SubjectState state;
            if (!subjects.TryGetValue(subjectId, out state))
            {
                state = new SubjectState
                {
                    CreditedTotal = 0.0,
                    MaxValue = subjectMaxValue
                };
            }

            // Update maxValue if the incoming action reports a higher cap
            // (in practice this should be consistent, but be defensive). Mirror of
            // the same monotonic-cap defense in
            // LedgerOrchestrator.BuildCommittedScienceSubjectCredits — keep both
            // sites in sync.
            if (subjectMaxValue > state.MaxValue)
                state.MaxValue = subjectMaxValue;

            // Hard cap walk: headroom = maxValue - creditedTotal
            double headroom = state.MaxValue - state.CreditedTotal;
            if (headroom < 0.0) headroom = 0.0;

            double effectiveScience = Math.Min((double)scienceAwarded, headroom);
            if (effectiveScience < 0.0) effectiveScience = 0.0;

            // Update state
            state.CreditedTotal += effectiveScience;
            subjects[subjectId] = state;
            runningScience += effectiveScience;
            totalEffectiveEarnings += effectiveScience;

            // Set derived field on the action
            action.EffectiveScience = (float)effectiveScience;

            // Log — always log earnings (bounded by number of science actions, not per-frame)
            bool capHit = effectiveScience < (double)scienceAwarded;
            if (capHit)
            {
                ParsekLog.Verbose("ScienceModule",
                    $"Earning (cap hit): subject={subjectId}, awarded={scienceAwarded.ToString("R", IC)}, " +
                    $"effective={effectiveScience.ToString("R", IC)}, headroom={headroom.ToString("R", IC)}, " +
                    $"creditedTotal={state.CreditedTotal.ToString("R", IC)}, " +
                    $"maxValue={state.MaxValue.ToString("R", IC)}, " +
                    $"runningScience={runningScience.ToString("R", IC)}, " +
                    $"recordingId={action.RecordingId ?? "(none)"}");
            }
            else
            {
                ParsekLog.Verbose("ScienceModule",
                    $"Earning: subject={subjectId}, awarded={scienceAwarded.ToString("R", IC)}, " +
                    $"effective={effectiveScience.ToString("R", IC)}, " +
                    $"creditedTotal={state.CreditedTotal.ToString("R", IC)}, " +
                    $"maxValue={state.MaxValue.ToString("R", IC)}, " +
                    $"runningScience={runningScience.ToString("R", IC)}, " +
                    $"recordingId={action.RecordingId ?? "(none)"}");
            }
        }

        // ================================================================
        // Spending
        // ================================================================

        /// <summary>
        /// Processes a ScienceSpending action: checks affordability, deducts from running balance.
        /// Sets <see cref="GameAction.Affordable"/> and, on the refused branch,
        /// <see cref="GameAction.UnaffordableRunningScience"/> — the positive "this walk
        /// processed and refused this row" marker the tech-tree re-lock guard keys on
        /// (<c>KspStatePatcher.ShouldRefuseUnaffordableRelock</c>). Refusing to deduct while
        /// the surviving ledger still carries the unlock row is exactly the state that used to
        /// let a researched node be silently re-locked downstream.
        /// </summary>
        internal void ProcessSpending(GameAction action)
        {
            float cost = action.Cost;
            bool affordable = runningScience >= (double)cost;

            action.Affordable = affordable;

            if (affordable)
            {
                runningScience -= (double)cost;
                action.UnaffordableRunningScience = null;

                ParsekLog.Verbose("ScienceModule",
                    $"Spending: nodeId={action.NodeId ?? "(none)"}, cost={cost.ToString("R", IC)}, " +
                    $"affordable=true, runningScience={runningScience.ToString("R", IC)}");
            }
            else
            {
                action.UnaffordableRunningScience = runningScience;

                ParsekLog.Warn("ScienceModule",
                    $"Spending NOT affordable: nodeId={action.NodeId ?? "(none)"}, cost={cost.ToString("R", IC)}, " +
                    $"runningScience={runningScience.ToString("R", IC)} — possible bug or data corruption");
            }
        }

        /// <summary>
        /// Processes a <see cref="GameActionType.StrategyScienceDebit"/> row: the science
        /// INPUT leg of a stock currency-exchange strategy (Patents Licensing).
        /// <see cref="GameAction.Cost"/> is the positive magnitude KSP already removed
        /// from the pool.
        ///
        /// <para>Modelled on <see cref="ProcessStrategySetupScienceCost"/>, NOT on
        /// <see cref="ProcessSpending"/>: ProcessSpending REFUSES to deduct when the
        /// running balance cannot cover the cost (it only warns). That refusal is right
        /// for a tech-node purchase the reconstruction is re-deciding, but wrong here -
        /// KSP has ALREADY moved this science out of the pool, so refusing would make the
        /// reconstruction diverge in the opposite direction. The subtraction is therefore
        /// unconditional, and an unaffordable case only logs a rate-limited Verbose (it is
        /// an ordering artefact of the walk, not a defect - see the log line's comment).</para>
        ///
        /// <para>Deliberately does NOT set <see cref="GameAction.Affordable"/>: that
        /// field is the tech-node contract <c>KspStatePatcher</c> reads to decide which
        /// nodes to unlock, and this row is not a tech node.</para>
        ///
        /// <para>Deliberately does NOT gate on <see cref="GameAction.Effective"/> either.
        /// That is CONSISTENT with <see cref="ProcessSpending"/>, which is also
        /// unconditional on that flag: <c>Effective</c> is the duplicate-suppression flag
        /// the EARNING channels carry (ScienceEarning / ContractComplete /
        /// MilestoneAchievement), and no producer clears it on a spending row. No
        /// behavior change is intended here.</para>
        /// </summary>
        internal void ProcessStrategyScienceDebit(GameAction action)
        {
            double cost = (double)action.Cost;
            if (cost <= 0.0)
            {
                ParsekLog.Verbose("ScienceModule",
                    $"StrategyScienceDebit: non-positive cost={cost.ToString("R", IC)} " +
                    $"at UT={action.UT.ToString("R", IC)} - no-op");
                return;
            }

            bool affordable = runningScience >= cost;
            runningScience -= cost;

            if (affordable)
            {
                ParsekLog.Verbose("ScienceModule",
                    $"StrategyScienceDebit: cost={cost.ToString("R", IC)}, " +
                    $"runningScience={runningScience.ToString("R", IC)}");
            }
            else
            {
                // NOT a Warn. A mid-walk negative balance here is EXPECTED on a
                // legitimate ledger, so a Warn would fire routinely and train the reader
                // to ignore it: commit-stamped science earnings land at their recording's
                // END UT while the exchange debit carries the exchange's TRUE UT, so the
                // walk can reach the debit before the earnings that funded it. The final
                // totals are unaffected - the walk is a sum, and every row is applied
                // exactly once regardless of the order the balance dips through. There is
                // deliberately no affordability REFUSAL to warn about either (see the
                // summary): KSP already removed this science.
                ParsekLog.VerboseRateLimited("ScienceModule", "strategy-science-debit-unaffordable",
                    $"StrategyScienceDebit ahead of its earnings: cost={cost.ToString("R", IC)}, " +
                    $"runningScience={runningScience.ToString("R", IC)} - deducted anyway " +
                    "(KSP already removed this science from the pool; a mid-walk negative " +
                    "balance is expected when commit-stamped earnings sort after the debit)");
            }
        }

        /// <summary>
        /// Processes a <see cref="GameActionType.StrategyScienceCredit"/> row: the
        /// science OUTPUT leg of a stock <c>CurrencyConverter</c> strategy (Open-Source
        /// Tech Program). <see cref="GameAction.ScienceAwarded"/> is the positive
        /// magnitude KSP already added to the pool.
        ///
        /// <para>Credits <see cref="runningScience"/> UNCONDITIONALLY and does NOT
        /// touch the per-subject cap bookkeeping - the mirror of
        /// <see cref="ProcessStrategyScienceDebit"/>'s unconditional subtraction, and
        /// for the same reason: KSP has already moved this science, so re-deciding it
        /// would make the reconstruction diverge. There is no subject to cap against
        /// (that is exactly why this is not a <see cref="ProcessEarning"/> row).</para>
        ///
        /// <para>It DOES feed <see cref="totalEffectiveEarnings"/>, because that is the
        /// figure <see cref="GetAvailableScience"/> reserves against: science the
        /// player genuinely holds and can spend on a tech node. Leaving it out would
        /// under-report spendable science by the yielded amount.</para>
        /// </summary>
        internal void ProcessStrategyScienceCredit(GameAction action)
        {
            double credit = (double)action.ScienceAwarded;
            if (credit <= 0.0)
            {
                ParsekLog.Verbose("ScienceModule",
                    $"StrategyScienceCredit: non-positive credit={credit.ToString("R", IC)} " +
                    $"at UT={action.UT.ToString("R", IC)} - no-op");
                return;
            }

            runningScience += credit;
            totalEffectiveEarnings += credit;

            ParsekLog.Verbose("ScienceModule",
                $"StrategyScienceCredit: credit={credit.ToString("R", IC)}, " +
                $"runningScience={runningScience.ToString("R", IC)}, " +
                $"totalEffectiveEarnings={totalEffectiveEarnings.ToString("R", IC)}");
        }

        /// <summary>
        /// Processes a StrategyActivate action's science setup cost. The action already
        /// carries the exact configured science charge from the strategy detail.
        /// </summary>
        internal void ProcessStrategySetupScienceCost(GameAction action)
        {
            double cost = (double)action.SetupScienceCost;
            if (cost <= 0.0)
                return;

            bool affordable = runningScience >= cost;
            runningScience -= cost;

            if (affordable)
            {
                ParsekLog.Verbose("ScienceModule",
                    $"StrategyActivate science: strategyId={action.StrategyId ?? "(none)"}, " +
                    $"cost={cost.ToString("R", IC)}, runningScience={runningScience.ToString("R", IC)}");
            }
            else
            {
                ParsekLog.Warn("ScienceModule",
                    $"StrategyActivate science NOT affordable: strategyId={action.StrategyId ?? "(none)"}, " +
                    $"cost={cost.ToString("R", IC)}, runningScience={runningScience.ToString("R", IC)}");
            }
        }

        // ================================================================
        // Science initial seed
        // ================================================================

        /// <summary>
        /// Processes a ScienceInitial action: sets baseline science balance for mid-career install.
        /// Adds the initial science to both the running balance and effective earnings total.
        /// </summary>
        internal void ProcessScienceInitial(GameAction action)
        {
            double initial = (double)action.InitialScience;
            runningScience += initial;
            totalEffectiveEarnings += initial;
            hasInitialSeed = true;

            ParsekLog.Info("ScienceModule",
                $"ScienceInitial: seed={initial.ToString("R", IC)}, " +
                $"runningScience={runningScience.ToString("R", IC)}, " +
                $"totalEffectiveEarnings={totalEffectiveEarnings.ToString("R", IC)}");
        }

        // ================================================================
        // Contract science reward
        // ================================================================

        /// <summary>
        /// Processes a ContractComplete action's science reward. Contract science is a flat
        /// reward added directly to the running balance — it is NOT subject-capped (unlike
        /// experiment science which goes through the per-subject hard cap).
        /// Only processes when Effective == true (chronologically first completion gets credit).
        /// Uses TransformedScienceReward (post-strategy-transform value).
        /// </summary>
        internal void ProcessContractScienceReward(GameAction action)
        {
            if (!action.Effective)
            {
                ParsekLog.Verbose("ScienceModule",
                    $"ContractComplete science skipped (not effective): contractId={action.ContractId ?? "(none)"}, " +
                    $"scienceReward={action.ScienceReward.ToString("R", IC)}");
                return;
            }

            float reward = action.TransformedScienceReward;
            if (reward <= 0f)
                return;

            runningScience += (double)reward;
            totalEffectiveEarnings += (double)reward;

            ParsekLog.Verbose("ScienceModule",
                $"ContractComplete science: contractId={action.ContractId ?? "(none)"}, " +
                $"reward={reward.ToString("R", IC)}, " +
                $"runningScience={runningScience.ToString("R", IC)}, " +
                $"totalEffectiveEarnings={totalEffectiveEarnings.ToString("R", IC)}");
        }

        // ================================================================
        // Milestone science reward
        // ================================================================

        /// <summary>
        /// Processes a MilestoneAchievement action's science reward. Milestone science is a flat
        /// reward added directly to the running balance — not subject-capped. Only processes when
        /// <see cref="GameAction.Effective"/> is true (chronologically first completion gets credit),
        /// mirroring how <see cref="MilestonesModule"/> and <see cref="ReputationModule"/> gate
        /// funds/rep awards.
        /// Previously the sci= value was dropped at convert time; see codex review [P2] on PR #307.
        /// </summary>
        internal void ProcessMilestoneScienceReward(GameAction action)
        {
            if (!action.Effective)
            {
                ParsekLog.Verbose("ScienceModule",
                    $"MilestoneAchievement science skipped (not effective): id={action.MilestoneId ?? "(none)"}, " +
                    $"sciAwarded={action.MilestoneScienceAwarded.ToString("R", IC)}");
                return;
            }

            float reward = action.MilestoneScienceAwarded;
            if (reward <= 0f)
                return;

            runningScience += (double)reward;
            totalEffectiveEarnings += (double)reward;

            ParsekLog.Verbose("ScienceModule",
                $"MilestoneAchievement science: id={action.MilestoneId ?? "(none)"}, " +
                $"reward={reward.ToString("R", IC)}, " +
                $"runningScience={runningScience.ToString("R", IC)}, " +
                $"totalEffectiveEarnings={totalEffectiveEarnings.ToString("R", IC)}");
        }

        // ================================================================
        // Query methods
        // ================================================================

        /// <summary>Returns the current running science balance (earnings minus spendings seen so far).</summary>
        internal double GetRunningScience()
        {
            return runningScience;
        }

        /// <summary>
        /// Returns the available science after the reservation system is applied.
        /// This is the amount the player can actually spend at the current point in the walk.
        ///
        /// Formula (design doc 4.5):
        ///   availableScience = min(projected science balance from current UT through future ledger)
        ///
        /// For non-cutoff/full-ledger walks, this collapses to the legacy
        /// <c>totalEffectiveEarnings - totalCommittedSpendings</c> calculation.
        /// Returns 0 if the result would be negative (player cannot spend negative science).
        /// </summary>
        internal double GetAvailableScience()
        {
            if (hasProjectedAvailableScience)
                return projectedAvailableScience;

            double available = totalEffectiveEarnings - totalCommittedSpendings;
            return available > 0.0 ? available : 0.0;
        }

        /// <summary>
        /// Installs a cashflow-aware availability value after a cutoff recalculation.
        /// </summary>
        public double GetProjectionCurrentBalance()
        {
            return runningScience;
        }

        /// <summary>
        /// The unclamped minimum projected balance: the value <see cref="GetAvailableScience"/>
        /// floors at zero. Negative means the committed future overdraws the balance by
        /// that much. Falls back to the legacy unclamped availability when no projection
        /// has been installed.
        /// </summary>
        internal double GetProjectionMinBalance()
        {
            if (hasProjectedAvailableScience)
                return projectedMinBalance;
            return totalEffectiveEarnings - totalCommittedSpendings;
        }

        public bool TryGetProjectionDelta(GameAction action, out double delta)
        {
            delta = 0.0;
            if (action == null)
                return false;

            switch (action.Type)
            {
                case GameActionType.ScienceEarning:
                    delta = (double)action.EffectiveScience;
                    return true;
                case GameActionType.MilestoneAchievement:
                    if (!action.Effective) return false;
                    delta = (double)action.MilestoneScienceAwarded;
                    return true;
                case GameActionType.ContractComplete:
                    if (!action.Effective) return false;
                    delta = (double)action.TransformedScienceReward;
                    return true;
                case GameActionType.ScienceSpending:
                    delta = -(double)action.Cost;
                    return true;
                case GameActionType.StrategyScienceDebit:
                    delta = -(double)action.Cost;
                    return true;
                case GameActionType.StrategyScienceCredit:
                    delta = (double)action.ScienceAwarded;
                    return true;
                case GameActionType.StrategyActivate:
                    delta = -(double)action.SetupScienceCost;
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
            projectedAvailableScience = available > 0.0 ? available : 0.0;
            projectedMinBalance = minProjectedBalance;
            hasProjectedAvailableScience = true;

            ParsekLog.Verbose("ScienceModule",
                $"Projected availability: current={currentBalance.ToString("R", IC)}, " +
                $"minProjected={minProjectedBalance.ToString("R", IC)}, " +
                $"finalProjected={finalProjectedBalance.ToString("R", IC)}, " +
                $"available={projectedAvailableScience.ToString("R", IC)}, " +
                $"futureActions={futureActions}, deltaActions={deltaActions}");
        }

        /// <summary>
        /// Returns the credited total for a specific subject.
        /// Returns 0 if the subject has not been seen.
        /// </summary>
        internal double GetSubjectCredited(string subjectId)
        {
            if (subjectId == null)
                return 0.0;

            SubjectState state;
            if (subjects.TryGetValue(subjectId, out state))
                return state.CreditedTotal;

            return 0.0;
        }

        /// <summary>
        /// True when the module processed a ScienceInitial action during the walk.
        /// When false, the module has no seed balance and KSP science should not be patched.
        /// </summary>
        internal bool HasSeed => hasInitialSeed;

        /// <summary>Returns the total effective earnings accumulated during the walk.</summary>
        internal double GetTotalEffectiveEarnings()
        {
            return totalEffectiveEarnings;
        }

        /// <summary>Returns the total committed spendings computed by the pre-pass.</summary>
        internal double GetTotalCommittedSpendings()
        {
            return totalCommittedSpendings;
        }

        /// <summary>Returns the number of tracked subjects (for diagnostics).</summary>
        internal int SubjectCount => subjects.Count;

        /// <summary>
        /// Returns a read-only view of all tracked subjects for per-subject patching.
        /// The dictionary maps subjectId to the current SubjectState (creditedTotal, maxValue).
        /// </summary>
        internal IReadOnlyDictionary<string, SubjectState> GetAllSubjects()
        {
            return subjects;
        }

        public void PostWalk() { }
    }
}
