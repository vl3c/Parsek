using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Science resource module — tracks per-subject credited totals and running science balance.
    /// Implements the hard-cap walk from design doc section 4.6:
    ///   - For each ScienceEarning: headroom = maxValue - creditedTotal, effectiveScience = min(awarded, headroom)
    ///   - For each ScienceSpending: the purchase state rule (a tech the walk already owns is
    ///     not charged), else deduct the cost in full; a cost the running balance cannot cover
    ///     is a science debt later earnings repay (design 3.15 step 6)
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
    internal class ScienceModule : IResourceModule, ICashflowProjectionModule, IStockPoolModule, IReservedFloorModule
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

        /// <summary>Per subject, the rows that credited it so far in the walk and how much
        /// (subject units): the witnesses a capped row repeats (design 3.15 step 5).</summary>
        private readonly Dictionary<string, List<KeyValuePair<IdentityWitness, double>>> subjectCredits =
            new Dictionary<string, List<KeyValuePair<IdentityWitness, double>>>();

        /// <summary>Per body, the orbital survey row that holds its bonus in this walk
        /// (design 3.15 step 7: once per body, earliest first).</summary>
        private readonly Dictionary<string, GameAction> surveyHolders =
            new Dictionary<string, GameAction>(StringComparer.Ordinal);

        /// <summary>The continuous producers' credited time slices in this walk (design 3.15
        /// step 7: a slice the committed timeline already holds earns nothing).</summary>
        private readonly ProducerSliceBook producerSlices = new ProducerSliceBook();

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

        /// <summary>Stock's <c>AllowNegativeCurrency</c>, read once per walk in <see cref="Reset"/>.</summary>
        private bool allowNegativeCurrency;

        /// <summary>The part of the walk's non-cost losses below 0 stock forgives (see FundsModule).</summary>
        private double forgivenBelowZero;

        /// <summary>Per row id, what the walk's dispatch moved the running science by
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
        private readonly StockShadowStart shadowStart = new StockShadowStart(StockPool.Science);

        /// <summary>The techs the walk owns so far (design 3.15 step 6, the purchase state rule).</summary>
        private readonly PurchaseOwnershipBook techOwnership = new PurchaseOwnershipBook();

        /// <summary>How much of the walk's tech costs the running science did not cover so
        /// far: the science debt the techs were charged into (design 3.15 step 6).</summary>
        private double techDebtCharged;

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
            subjectCredits.Clear();
            surveyHolders.Clear();
            producerSlices.Reset();
            runningScience = 0.0;
            totalCommittedSpendings = 0.0;
            totalEffectiveEarnings = 0.0;
            hasProjectedAvailableScience = false;
            projectedAvailableScience = 0.0;
            projectedMinBalance = 0.0;
            hasInitialSeed = false;
            forgivenBelowZero = 0.0;
            rowEffects.Clear();
            takeBackDeltas.Clear();
            shadowDivergence = 0.0;
            shadowStart.Reset(StockPoolReference.Science);
            floorState.Reset();
            techOwnership.Reset();
            techDebtCharged = 0.0;
            allowNegativeCurrency = StockCurrencyClamp.ReadAllowNegativeCurrency();

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

            double before = runningScience;
            DispatchAction(action);

            // Rule 4 (see FundsModule.ProcessAction): the floor and the debt where a committed
            // cost was captured ahead of the loss, else stock's 0 clamp.
            bool exempt = IsScienceCostRow(action.Type) || action.Type == GameActionType.StockPoolAdjustment;
            double floor = 0.0;
            bool floored = !exempt && floorState.TryGetLossFloor(action, out floor);
            if (!floored)
                ApplyStockZeroClamp(action, before);

            // The reference's effect: before the settle and any debt charge.
            double effect = runningScience - before;
            if (!string.IsNullOrEmpty(action.ActionId))
            {
                double prior;
                rowEffects.TryGetValue(action.ActionId, out prior);
                rowEffects[action.ActionId] = prior + effect;
            }

            double stockDelta = action.TakeBackScience != 0f
                ? RecordTakeBack(action, before, effect, (double)action.TakeBackScience)
                : 0.0;
            shadowStart.Note(action, effect + stockDelta);

            if (floored)
            {
                double heldBack = floorState.Settle(action, runningScience, effect, floor);
                if (heldBack > 0.0)
                {
                    runningScience += heldBack;
                    ParsekLog.VerboseRateLimited("ScienceModule",
                        "floor-settle-" + (action.ActionId ?? action.Type.ToString()),
                        $"Floor settle: {action.Type} id={action.ActionId ?? "(none)"} " +
                        $"ut={action.UT.ToString("R", IC)} effect={effect.ToString("R", IC)} " +
                        $"floor={floor.ToString("R", IC)} heldBack={heldBack.ToString("R", IC)} " +
                        $"debt={floorState.Debt.ToString("R", IC)} " +
                        $"runningScience={runningScience.ToString("R", IC)}");
                }
            }

            double charged = floorState.Charge(action, runningScience);
            if (charged > 0.0)
            {
                runningScience -= charged;
                ParsekLog.VerboseRateLimited("ScienceModule",
                    "floor-charge-" + (action.ActionId ?? action.Type.ToString()),
                    $"Debt charge: {action.Type} id={action.ActionId ?? "(none)"} " +
                    $"ut={action.UT.ToString("R", IC)} charged={charged.ToString("R", IC)} " +
                    $"debt={floorState.Debt.ToString("R", IC)} " +
                    $"runningScience={runningScience.ToString("R", IC)}");
            }
        }

        // ================================================================
        // IReservedFloorModule (committed-value rule plan section 2.3)
        // ================================================================

        /// <inheritdoc />
        public StockPool FloorPool => StockPool.Science;

        /// <inheritdoc />
        public void ClassifyFloorRow(GameAction action, out double delta, out bool isCost, out bool floorLimited)
        {
            if (!TryGetRowDelta(action, out delta))
                delta = 0.0;
            isCost = action != null && IsScienceCostRow(action.Type) && delta < 0.0;
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
            ParsekLog.Verbose("ScienceModule",
                $"Stock shadow: {action.Type} id={action.ActionId} ut={action.UT.ToString("R", IC)} " +
                $"walkEffect={effect.ToString("R", IC)} stockPaid={shadowEffect.ToString("R", IC)} " +
                $"takeBack={delta.ToString("R", IC)} (science; {action.TakeBackWitness ?? "no witness"})");
            return delta;
        }

        /// <summary>
        /// A cost row (committed-value rule plan section 2.3): a tech purchase or a strategy
        /// setup, plus the exchange debit, which is the science half of a currency conversion
        /// as the funds half (a <c>FundsSpending</c> row) is a cost row in funds.
        /// </summary>
        internal static bool IsScienceCostRow(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.ScienceSpending:
                case GameActionType.StrategyActivate:
                case GameActionType.StrategyScienceDebit:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Stock's 0 clamp on a non-cost science loss (see FundsModule.ApplyStockZeroClamp).</summary>
        private void ApplyStockZeroClamp(GameAction action, double before)
        {
            double forgiven = StockCurrencyClamp.ForgivenBelowZero(
                before, runningScience,
                IsScienceCostRow(action.Type) || action.Type == GameActionType.StockPoolAdjustment,
                allowNegativeCurrency);
            if (forgiven <= 0.0)
                return;

            runningScience += forgiven;
            forgivenBelowZero += forgiven;
            ParsekLog.VerboseRateLimited("ScienceModule",
                "zero-clamp-" + (action.ActionId ?? action.Type.ToString()),
                $"Zero clamp: {action.Type} id={action.ActionId ?? "(none)"} " +
                $"ut={action.UT.ToString("R", IC)} before={before.ToString("R", IC)} " +
                $"forgiven={forgiven.ToString("R", IC)} " +
                $"runningScience={runningScience.ToString("R", IC)} " +
                $"forgivenTotal={forgivenBelowZero.ToString("R", IC)} (stock stops this loss at 0)");
        }

        private void DispatchAction(GameAction action)
        {
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
                case GameActionType.ScienceLabEarning:
                    ProcessLabEarning(action);
                    break;
                case GameActionType.ContractComplete:
                // A contract parameter's own science is a flat pool credit like the contract's.
                case GameActionType.ContractParameterComplete:
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
                case GameActionType.StockPoolAdjustment:
                    ProcessStockPoolAdjustment(action);
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
                    totalCommittedSpendings += (double)action.Cost * action.ConversionShare;
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
        /// Processes a ScienceEarning action: applies the subject hard cap in subject units,
        /// scales the capped value by the row's captured <see cref="GameAction.ScienceGainMultiplier"/>
        /// for the pool, updates the credited total (subject units) and running balance (pool
        /// units). Sets <see cref="GameAction.EffectiveScience"/> (pool units).
        /// </summary>
        internal void ProcessEarning(GameAction action)
        {
            string subjectId = action.SubjectId ?? "";
            float scienceAwarded = action.ScienceAwarded;
            float subjectMaxValue = action.SubjectMaxValue;

            // A continuous producer's slice earns only the time no earlier credited slice of
            // the same producer and subject holds (design 3.15 step 7).
            ProducerSliceDecision slice = default(ProducerSliceDecision);
            if (ProducerSliceBook.IsProducerSlice(action))
            {
                slice = producerSlices.Decide(action, StockShadowTakeBack.BreakList);
                producerSlices.Hold(action, slice);
                if (slice.Covered > 0.0 && !CreditProducerSlicePart(action, slice, ref scienceAwarded))
                    return;
            }

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

            // The cap walk runs in SUBJECT units (pre-multiplier): stock adds the
            // un-multiplied value to subject.science, and that is what the cap and the
            // committed-science cache (ScienceSubjectPatch) compare against.
            double effectiveSubjectScience = Math.Min((double)scienceAwarded, headroom);
            if (effectiveSubjectScience < 0.0) effectiveSubjectScience = 0.0;

            // The POOL received the subject value times the ScienceGainMultiplier frozen at
            // capture (stock SubmitScienceData multiplies before AddScience).
            double gainMultiplier = GameAction.NormalizeScienceGainMultiplier(action.ScienceGainMultiplier);
            double effectiveScience = effectiveSubjectScience * gainMultiplier;

            // Take-back (design 3.15 step 5): stock paid the captured value; the part the cap
            // left out is taken back only as far as it repeats credit a rewind made stock forget.
            ApplyCapTakeBack(action, subjectId, (double)scienceAwarded - effectiveSubjectScience, gainMultiplier);
            // ...and a producer slice's part stock forgot after a rewind (3.15 step 7).
            ApplyProducerSliceTakeBack(action, slice, gainMultiplier);
            // Witnesses matter only once the save has a timeline break: a career never
            // rewound records none and walks as before.
            if (effectiveSubjectScience > 0.0 && StockShadowTakeBack.Breaks.Count > 0)
            {
                List<KeyValuePair<IdentityWitness, double>> credits;
                if (!subjectCredits.TryGetValue(subjectId, out credits))
                {
                    credits = new List<KeyValuePair<IdentityWitness, double>>();
                    subjectCredits[subjectId] = credits;
                }
                credits.Add(new KeyValuePair<IdentityWitness, double>(
                    IdentityWitness.Of(action, "science subject '" + subjectId + "'"), effectiveSubjectScience));
            }

            // Update state
            state.CreditedTotal += effectiveSubjectScience;
            subjects[subjectId] = state;
            runningScience += effectiveScience;
            totalEffectiveEarnings += effectiveScience;

            // Set derived field on the action, in POOL units: every reconcile reads it
            // against a ScienceChanged delta, which stock fires post-multiplier.
            action.EffectiveScience = (float)effectiveScience;

            string gainText = gainMultiplier != 1.0
                ? $", gainMultiplier={gainMultiplier.ToString("R", IC)}, " +
                  $"subjectEffective={effectiveSubjectScience.ToString("R", IC)}"
                : "";

            // Log — always log earnings (bounded by number of science actions, not per-frame)
            bool capHit = effectiveSubjectScience < (double)scienceAwarded;
            if (capHit)
            {
                ParsekLog.Verbose("ScienceModule",
                    $"Earning (cap hit): subject={subjectId}, awarded={scienceAwarded.ToString("R", IC)}, " +
                    $"effective={effectiveScience.ToString("R", IC)}{gainText}, headroom={headroom.ToString("R", IC)}, " +
                    $"creditedTotal={state.CreditedTotal.ToString("R", IC)}, " +
                    $"maxValue={state.MaxValue.ToString("R", IC)}, " +
                    $"runningScience={runningScience.ToString("R", IC)}, " +
                    $"recordingId={action.RecordingId ?? "(none)"}");
            }
            else
            {
                ParsekLog.Verbose("ScienceModule",
                    $"Earning: subject={subjectId}, awarded={scienceAwarded.ToString("R", IC)}, " +
                    $"effective={effectiveScience.ToString("R", IC)}{gainText}, " +
                    $"creditedTotal={state.CreditedTotal.ToString("R", IC)}, " +
                    $"maxValue={state.MaxValue.ToString("R", IC)}, " +
                    $"runningScience={runningScience.ToString("R", IC)}, " +
                    $"recordingId={action.RecordingId ?? "(none)"}");
            }
        }

        /// <summary>
        /// A producer slice an earlier credited slice partly or wholly holds: reduces
        /// <paramref name="scienceAwarded"/> to the uncovered share and returns true, or, when
        /// nothing is left, marks the row not counted
        /// (<see cref="GameActionNotCountedReason.ProducerSliceAlreadyCredited"/>), takes back
        /// the part stock forgot, and returns false.
        /// </summary>
        private bool CreditProducerSlicePart(GameAction action, ProducerSliceDecision slice, ref float scienceAwarded)
        {
            double keep = slice.CreditedFraction;
            GameAction holder = slice.Holder;
            string held = holder == null ? "(none)"
                : $"id={holder.ActionId ?? "(none)"} slice={ProducerSliceBook.FormatSlice(holder)} " +
                  $"cseq={holder.CaptureSequence.ToString(IC)}";
            if (keep > 0.0)
            {
                scienceAwarded = (float)(action.ScienceAwarded * keep);
                ParsekLog.Info("ScienceModule",
                    $"Producer slice {ProducerSliceBook.Label(action)} id={action.ActionId ?? "(none)"} " +
                    $"slice={ProducerSliceBook.FormatSlice(action)} counted in part: " +
                    $"{slice.Covered.ToString("R", IC)}s of {slice.Length.ToString("R", IC)}s already held by {held}; " +
                    $"awarded {action.ScienceAwarded.ToString("R", IC)} -> {scienceAwarded.ToString("R", IC)}");
                return true;
            }

            action.Effective = false;
            action.NotCountedReason = GameActionNotCountedReason.ProducerSliceAlreadyCredited;
            action.EffectiveScience = 0f;
            ApplyProducerSliceTakeBack(action, slice,
                GameAction.NormalizeScienceGainMultiplier(action.ScienceGainMultiplier));
            ParsekLog.Info("ScienceModule",
                $"Producer slice {ProducerSliceBook.Label(action)} id={action.ActionId ?? "(none)"} " +
                $"slice={ProducerSliceBook.FormatSlice(action)} not counted: the whole slice is already held by {held}" +
                (action.TakeBackScience != 0f
                    ? $", taking back {action.TakeBackScience.ToString("R", IC)}"
                    : ""));
            return false;
        }

        /// <summary>
        /// The take-back of a producer slice's forgotten part (pool units, the row's own
        /// multiplier): what stock paid for time a rewind made it forget an earlier slice had
        /// already paid. Added to any cap take-back. Eligible rows only; nothing for a part
        /// held by a slice captured after the row (stock never paid that one twice).
        /// </summary>
        private static void ApplyProducerSliceTakeBack(GameAction action, ProducerSliceDecision slice, double gainMultiplier)
        {
            double fraction = slice.ForgottenFraction;
            if (!(fraction > 0.0) || slice.Holder == null || !StockShadowTakeBack.IsEligibleRow(action))
                return;
            double amount = (double)action.ScienceAwarded * gainMultiplier * fraction;
            if (!(amount > 0.0))
                return;
            action.TakeBackScience += (float)amount;
            string witness = IdentityWitness.Of(slice.Holder, ProducerSliceBook.Label(action)).Describe();
            action.TakeBackWitness = string.IsNullOrEmpty(action.TakeBackWitness)
                ? witness
                : action.TakeBackWitness + "; " + witness;
            ParsekLog.Verbose("ScienceModule",
                $"Producer slice repeats forgotten credit: id={action.ActionId ?? "(none)"} " +
                $"forgotten={slice.Forgotten.ToString("R", IC)}s of {slice.Length.ToString("R", IC)}s " +
                $"takeBack={amount.ToString("R", IC)} (pool units)");
        }

        /// <summary>
        /// Pure over the walk's subject credits: the cap left <paramref name="cappedSubject"/>
        /// (subject units) of an eligible row out; of that, the part no greater than what the
        /// forgotten crediting rows stand for is taken back (pool units, the row's own
        /// multiplier). A capped row whose earlier credits stock's state still held (or an
        /// older row) takes nothing back, so a career never rewound walks as before.
        /// </summary>
        private void ApplyCapTakeBack(GameAction action, string subjectId, double cappedSubject, double gainMultiplier)
        {
            if (!(cappedSubject > 0.0) || !StockShadowTakeBack.IsEligibleRow(action))
                return;
            List<KeyValuePair<IdentityWitness, double>> credits;
            if (!subjectCredits.TryGetValue(subjectId, out credits) || credits.Count == 0)
                return;
            double forgotten = 0.0;
            var witnesses = new List<string>();
            for (int i = 0; i < credits.Count; i++)
            {
                if (!StockShadowTakeBack.IsWitnessForgotten(credits[i].Key, action.CaptureSequence))
                    continue;
                forgotten += credits[i].Value;
                witnesses.Add(credits[i].Key.Describe());
            }
            double subjectTakeBack = Math.Min(cappedSubject, forgotten);
            if (!(subjectTakeBack > 0.0))
                return;
            action.TakeBackScience = (float)(subjectTakeBack * gainMultiplier);
            action.TakeBackWitness = string.Join("; ", witnesses.ToArray());
            ParsekLog.Verbose("ScienceModule",
                $"Earning over the cap repeats forgotten credit: subject={subjectId} " +
                $"actionId={action.ActionId ?? "(none)"} capped={cappedSubject.ToString("R", IC)} " +
                $"forgottenCredit={forgotten.ToString("R", IC)} " +
                $"takeBack={action.TakeBackScience.ToString("R", IC)} (pool units)");
        }

        // ================================================================
        // Spending
        // ================================================================

        /// <summary>
        /// Processes a ScienceSpending action (design 3.15 step 6).
        ///
        /// <para>The purchase state rule first: a tech the walk already owns at this row,
        /// bought by a purchase captured after it (<see cref="PurchaseStateRule"/>), is not
        /// charged (<see cref="GameActionNotCountedReason.PurchaseAlreadyOwned"/>). It stays
        /// researched through the row that owns it.</para>
        ///
        /// <para>Otherwise the tech is charged in full, like a funds purchase: when the running
        /// science cannot cover it (credit a rewind moved away from it, a strategy's science
        /// cut moved out of its window) the running value goes below 0, a science debt that
        /// later earnings repay before the pool rises, and the tech stays researched. It is
        /// never left researched for free. <see cref="GameAction.Affordable"/> is the tech
        /// patch's "researched by the walk" contract and is true on every charged row.</para>
        /// </summary>
        internal void ProcessSpending(GameAction action)
        {
            float cost = action.Cost;
            action.UnaffordableRunningScience = null;

            string holder;
            if (techOwnership.IsAlreadyOwned(action, out holder))
            {
                action.Effective = false;
                action.NotCountedReason = GameActionNotCountedReason.PurchaseAlreadyOwned;
                action.Affordable = true;
                UncountPrePassSpending((double)cost);
                ParsekLog.InfoRateLimited("ScienceModule", "purchase-owned-" + (action.ActionId ?? action.NodeId),
                    $"Spending not charged: tech '{action.NodeId ?? "(none)"}' is already researched at " +
                    $"ut={action.UT.ToString("R", IC)} cseq={action.CaptureSequence.ToString(IC)} " +
                    $"(held by {holder}, captured after this row), cost={cost.ToString("R", IC)}, " +
                    $"runningScience={runningScience.ToString("R", IC)}");
                return;
            }

            double before = runningScience;
            runningScience -= (double)cost;
            action.Affordable = true;
            techOwnership.RecordCharged(action);

            double shortfall = ResolveDebtShortfall(before, (double)cost);
            if (shortfall <= 0.0)
            {
                ParsekLog.Verbose("ScienceModule",
                    $"Spending: nodeId={action.NodeId ?? "(none)"}, cost={cost.ToString("R", IC)}, " +
                    $"affordable=true, runningScience={runningScience.ToString("R", IC)}");
                return;
            }

            techDebtCharged += shortfall;
            ParsekLog.InfoRateLimited("ScienceModule", "tech-debt-" + (action.ActionId ?? action.NodeId),
                $"Spending charged into science debt: nodeId={action.NodeId ?? "(none)"} " +
                $"ut={action.UT.ToString("R", IC)} cost={cost.ToString("R", IC)} " +
                $"runningBefore={before.ToString("R", IC)} debt={shortfall.ToString("R", IC)} " +
                $"runningScience={runningScience.ToString("R", IC)} (the tech stays researched; " +
                "later earnings repay the debt before the pool rises)");
        }

        /// <summary>
        /// Pure: the part of a cost of <paramref name="cost"/> the running value
        /// <paramref name="before"/> does not cover (the whole cost when it is already below 0),
        /// 0 when it covers it.
        /// </summary>
        internal static double ResolveDebtShortfall(double before, double cost)
        {
            if (!(cost > 0.0))
                return 0.0;
            double after = before - cost;
            if (!(after < 0.0))
                return 0.0;
            return before > 0.0 ? -after : cost;
        }

        /// <summary>
        /// Removes a spending that <see cref="ComputeTotalSpendings"/> counted in PrePass but
        /// the walk does not charge (a tech it already owns). PrePass runs before the walk
        /// decides the purchase state rule.
        /// </summary>
        private void UncountPrePassSpending(double amount)
        {
            if (amount > 0.0)
                totalCommittedSpendings -= amount;
        }

        /// <summary>
        /// Processes a <see cref="GameActionType.StrategyScienceDebit"/> row: the science
        /// INPUT leg of a stock currency-exchange strategy (Patents Licensing).
        /// <see cref="GameAction.Cost"/> is the positive magnitude KSP already removed
        /// from the pool.
        ///
        /// <para>Modelled on <see cref="ProcessStrategySetupScienceCost"/>: KSP has ALREADY
        /// moved this science out of the pool, so the subtraction is unconditional, and an
        /// unaffordable case only logs a rate-limited Verbose (it is an ordering artefact of
        /// the walk, not a defect - see the log line's comment). Unlike
        /// <see cref="ProcessSpending"/> it is not a purchase: the purchase state rule does
        /// not apply to it.</para>
        ///
        /// <para>Deliberately does NOT set <see cref="GameAction.Affordable"/>: that
        /// field is the tech-node contract <c>KspStatePatcher</c> reads to decide which
        /// nodes to unlock, and this row is not a tech node.</para>
        ///
        /// <para>Deliberately does NOT gate on <see cref="GameAction.Effective"/> either:
        /// <c>Effective</c> is the duplicate-suppression flag the EARNING channels carry
        /// (ScienceEarning / ContractComplete / MilestoneAchievement), and nothing clears it
        /// on this row. The only spending row the walk leaves uncharged is a tech it already
        /// owns, which <see cref="ProcessSpending"/> decides for itself.</para>
        /// </summary>
        internal void ProcessStrategyScienceDebit(GameAction action)
        {
            // A converter leg is charged its cause's share (design 3.15 step 7; 1 elsewhere).
            double cost = (double)action.Cost * action.ConversionShare;
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
                // to ignore it: a science earning with no capture UT (an older save's row,
                // or a subject the converter end-anchors) lands at its recording's END UT
                // while the exchange debit carries the exchange's TRUE UT, so the walk can
                // reach the debit before the earnings that funded it. The final
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
            // A converter leg yields its cause's share (design 3.15 step 7; 1 elsewhere).
            double credit = (double)action.ScienceAwarded * action.ConversionShare;
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
        /// Processes a <see cref="GameActionType.ScienceLabEarning"/> row: the science lab's
        /// subject-less credit, applied uncapped (stock caps only the lab's stored science,
        /// and that cap already bounded the credit when it was paid). Feeds
        /// <see cref="totalEffectiveEarnings"/> like every other spendable earning and sets
        /// <see cref="GameAction.EffectiveScience"/> so the earnings reconcile reads it.
        /// </summary>
        internal void ProcessLabEarning(GameAction action)
        {
            double credit = (double)action.ScienceAwarded;
            if (!(credit > 0.0))
            {
                action.EffectiveScience = 0f;
                ParsekLog.Verbose("ScienceModule",
                    $"LabEarning: non-positive credit={credit.ToString("R", IC)} " +
                    $"at UT={action.UT.ToString("R", IC)} - no-op");
                return;
            }
            if (IsOrbitalSurveyRow(action) && !CreditOrbitalSurvey(action))
                return;

            runningScience += credit;
            totalEffectiveEarnings += credit;
            action.EffectiveScience = (float)credit;

            ParsekLog.Verbose("ScienceModule",
                $"LabEarning: credit={credit.ToString("R", IC)}, method={action.Method}, " +
                $"runningScience={runningScience.ToString("R", IC)}, " +
                $"recordingId={action.RecordingId ?? "(none)"}");
        }

        /// <summary>Pure: a lab-earning row that is stock's orbital survey bonus
        /// (<c>ModuleOrbitalSurveyor.CompleteSurvey</c>, the surveyed body in
        /// <see cref="GameAction.ScanBodyName"/>), not a lab payout or live-only income.</summary>
        internal static bool IsOrbitalSurveyRow(GameAction action)
        {
            return action != null && action.Type == GameActionType.ScienceLabEarning
                && !action.IsLiveOnlyIncome && !string.IsNullOrEmpty(action.ScanBodyName);
        }

        /// <summary>What the walk does with an orbital survey row whose body already has a
        /// holder (design 3.15 step 7).</summary>
        internal enum SurveyRepeat
        {
            /// <summary>Credited as captured: no holder, or a repeat stock paid by its own rule
            /// on one timeline (a second survey transmitted before the first completed).</summary>
            Credit,
            /// <summary>Not counted: the holder was captured after this row, so a rewind
            /// separated them and the earlier survey on the timeline keeps the bonus.</summary>
            HolderCapturedAfter,
            /// <summary>Not counted and taken back: stock paid it again because a rewind made
            /// it forget the holder's scan.</summary>
            HolderForgotten
        }

        /// <summary>
        /// Pure: the decision for a survey row of a body whose bonus <paramref name="holder"/>
        /// already holds (null: none). Stock pays the bonus once per body (decompiled
        /// <c>ModuleOrbitalSurveyor</c>: <c>PerformSurvey</c> is active only while the body is
        /// unscanned), so a repeat is a rewind's doing: the holder captured after the row
        /// (both capture numbers known), or the holder's scan forgotten across a timeline
        /// break. Anything else is credited, so a career never rewound walks as before.
        /// </summary>
        internal static SurveyRepeat DecideSurveyRepeat(GameAction holder, GameAction row, IList<TimelineBreak> breaks)
        {
            if (holder == null || row == null)
                return SurveyRepeat.Credit;
            if (PurchaseStateRule.IsCapturedAfter(holder.CaptureSequence, row.CaptureSequence))
                return SurveyRepeat.HolderCapturedAfter;
            if (StockShadowTakeBack.IsWitnessForgotten(
                    IdentityWitness.Of(holder, SurveyLabel(row.ScanBodyName)), row.CaptureSequence, breaks))
                return SurveyRepeat.HolderForgotten;
            return SurveyRepeat.Credit;
        }

        private static string SurveyLabel(string body)
        {
            return "orbital survey of '" + (body ?? "") + "'";
        }

        /// <summary>
        /// The once-per-body rule for one survey row: true to credit it (and hold the body
        /// when it is the first), false when the row is not counted
        /// (<see cref="GameActionNotCountedReason.SurveyBodyAlreadyCredited"/>), with the
        /// whole row taken back when stock paid it after forgetting the holder.
        /// </summary>
        private bool CreditOrbitalSurvey(GameAction action)
        {
            string body = action.ScanBodyName;
            GameAction holder;
            surveyHolders.TryGetValue(body, out holder);
            SurveyRepeat decision = DecideSurveyRepeat(holder, action, StockShadowTakeBack.BreakList);
            if (decision == SurveyRepeat.Credit)
            {
                if (holder == null)
                    surveyHolders[body] = action;
                else
                    ParsekLog.Verbose("ScienceModule",
                        $"Orbital survey of '{body}' id={action.ActionId ?? "(none)"} credited again: " +
                        $"stock paid it on the same timeline as id={holder.ActionId ?? "(none)"} " +
                        $"(cseq={holder.CaptureSequence.ToString(IC)}), kept as captured");
                return true;
            }

            action.Effective = false;
            action.NotCountedReason = GameActionNotCountedReason.SurveyBodyAlreadyCredited;
            action.EffectiveScience = 0f;
            bool takeBack = decision == SurveyRepeat.HolderForgotten
                && StockShadowTakeBack.ShouldTakeBackWholeRow(action, IdentityWitness.Of(holder, SurveyLabel(body)));
            if (takeBack)
                action.TakeBackScience = action.ScienceAwarded;
            ParsekLog.Info("ScienceModule",
                $"Orbital survey of '{body}' id={action.ActionId ?? "(none)"} ut={action.UT.ToString("R", IC)} " +
                $"cseq={action.CaptureSequence.ToString(IC)} not counted: the body's bonus is held by " +
                $"id={holder.ActionId ?? "(none)"} ut={holder.UT.ToString("R", IC)} " +
                $"cseq={holder.CaptureSequence.ToString(IC)} " +
                (decision == SurveyRepeat.HolderCapturedAfter
                    ? "(captured after this row: a rewind separated them, the earlier survey keeps it)"
                    : "(stock forgot that scan after a rewind and paid again)") +
                (takeBack ? $", taking back {action.ScienceAwarded.ToString("R", IC)}" : ""));
            return false;
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
                    $"{action.Type} science skipped (not effective): contractId={action.ContractId ?? "(none)"}" +
                    $"{FundsModule.FormatParameterSuffix(action)}, " +
                    $"scienceReward={action.ScienceReward.ToString("R", IC)}");
                return;
            }

            float reward = action.TransformedScienceReward;
            if (reward <= 0f)
                return;

            runningScience += (double)reward;
            totalEffectiveEarnings += (double)reward;

            ParsekLog.Verbose("ScienceModule",
                $"{action.Type} science: contractId={action.ContractId ?? "(none)"}" +
                $"{FundsModule.FormatParameterSuffix(action)}, " +
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

            // A partly credited world-record row earns only its new thresholds (3.15 step 4).
            float reward = action.CreditedMilestoneScience;
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

        /// <summary>
        /// Processes a science <see cref="GameActionType.StockPoolAdjustment"/>: the amount stock
        /// already applied to the pool, as stored and never clamped. Counted with the
        /// earnings (signed), so the no-projection availability carries it.
        /// </summary>
        private void ProcessStockPoolAdjustment(GameAction action)
        {
            if (action.AdjustmentPool != StockPool.Science)
                return;
            double amount = action.AdjustmentAmount;
            runningScience += amount;
            totalEffectiveEarnings += amount;
            ParsekLog.Verbose("ScienceModule",
                $"StockPoolAdjustment: {amount.ToString("R", IC)} id={action.ActionId ?? "(none)"} " +
                $"ut={action.UT.ToString("R", IC)} runningScience={runningScience.ToString("R", IC)}");
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

            double available = totalEffectiveEarnings - totalCommittedSpendings + forgivenBelowZero
                + floorState.Debt;
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
            return totalEffectiveEarnings - totalCommittedSpendings + forgivenBelowZero + floorState.Debt;
        }

        public bool TryGetProjectionDelta(GameAction action, out double delta)
        {
            return TryGetRowDelta(action, out delta);
        }

        /// <summary>
        /// True for every action type this module moves the science pool for, whatever the
        /// row's derived fields say. <see cref="TryGetRowDelta"/> answers false for an
        /// ineffective row of such a type; the stock pool reference
        /// (<see cref="StockPoolReference"/>) still counts that row, at 0.
        /// </summary>
        internal static bool AffectsScience(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.ScienceEarning:
                case GameActionType.MilestoneAchievement:
                case GameActionType.ContractComplete:
                case GameActionType.ContractParameterComplete:
                case GameActionType.ScienceSpending:
                case GameActionType.StrategyScienceDebit:
                case GameActionType.StrategyScienceCredit:
                case GameActionType.ScienceLabEarning:
                case GameActionType.StrategyActivate:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The science a row moves the pool by after a walk populated its derived fields
        /// (<see cref="GameAction.EffectiveScience"/>, <see cref="GameAction.Effective"/>,
        /// <see cref="GameAction.TransformedScienceReward"/>). Pure: reads only the row.
        /// Shared by the projection and the stock pool reference.
        /// </summary>
        internal static bool TryGetRowDelta(GameAction action, out double delta)
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
                    delta = (double)action.CreditedMilestoneScience;
                    return true;
                case GameActionType.ContractComplete:
                case GameActionType.ContractParameterComplete:
                    if (!action.Effective) return false;
                    delta = (double)action.TransformedScienceReward;
                    return true;
                case GameActionType.ScienceSpending:
                    // A tech the walk already owns is not charged (3.15 step 6).
                    if (!action.Effective) return false;
                    delta = -(double)action.Cost;
                    return true;
                case GameActionType.StrategyScienceDebit:
                    delta = -(double)action.Cost * action.ConversionShare;
                    return true;
                case GameActionType.StrategyScienceCredit:
                    delta = (double)action.ScienceAwarded * action.ConversionShare;
                    return true;
                case GameActionType.ScienceLabEarning:
                    // A repeated orbital survey is not counted (3.15 step 7).
                    if (!action.Effective) return false;
                    delta = (double)action.ScienceAwarded;
                    return true;
                case GameActionType.StrategyActivate:
                    delta = -(double)action.SetupScienceCost;
                    return true;
                case GameActionType.StockPoolAdjustment:
                    if (action.AdjustmentPool != StockPool.Science) return false;
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

        /// <summary>The part of the walk's tech costs the running science did not cover: the
        /// science debt the techs were charged into (design 3.15 step 6). Later earnings repay it
        /// through the running value.</summary>
        internal double GetTechDebtCharged()
        {
            return techDebtCharged;
        }

        /// <summary>One summary per walk of the floor and the debt (only when a plan is installed).</summary>
        public void PostWalk()
        {
            if (techDebtCharged > 0.0)
            {
                ParsekLog.VerboseOnChange("ScienceModule", "tech-debt|science",
                    techDebtCharged.ToString("R", IC) + "|" + runningScience.ToString("R", IC),
                    $"Tech debt walk: charged={techDebtCharged.ToString("R", IC)} " +
                    $"runningScience={runningScience.ToString("R", IC)}");
            }
            if (floorState.Plan == null)
                return;
            string summary = floorState.Summary();
            ParsekLog.VerboseOnChange("ScienceModule", "floor-walk|science", summary,
                $"Floor walk: pool=Science {summary} runningScience={runningScience.ToString("R", IC)}");
        }
    }
}
