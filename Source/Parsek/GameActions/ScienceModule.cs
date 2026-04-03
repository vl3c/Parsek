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
    ///   availableScience(ut) = sum(effective earnings up to ut) - sum(ALL committed spendings on entire timeline)
    ///   The pre-pass <see cref="ComputeTotalSpendings"/> sums all spending costs before the walk starts.
    ///   During the walk, <see cref="totalEffectiveEarnings"/> accumulates effective earnings.
    ///   <see cref="GetAvailableScience"/> returns the amount the player can actually spend at the current point.
    ///
    /// Pure computation — no KSP state access.
    /// </summary>
    internal class ScienceModule : IResourceModule
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
        /// Sum of ALL committed science spending costs on the entire timeline.
        /// Computed by <see cref="ComputeTotalSpendings"/> before the walk starts.
        /// Used by <see cref="GetAvailableScience"/> for the reservation check.
        /// </summary>
        private double totalCommittedSpendings;

        /// <summary>
        /// Running sum of effective science earnings accumulated during the walk.
        /// Updated in <see cref="ProcessEarning"/> alongside runningScience.
        /// Used by <see cref="GetAvailableScience"/> for the reservation check.
        /// </summary>
        private double totalEffectiveEarnings;

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

            ParsekLog.Verbose("ScienceModule",
                $"Reset: cleared {subjectCount} subjects, runningScience=0, " +
                $"totalCommittedSpendings=0, totalEffectiveEarnings=0");
        }

        /// <summary>
        /// Pre-pass: sums all ScienceSpending costs from the sorted action list before the walk starts.
        /// This is required for the reservation system: available = effective earnings - ALL spendings.
        /// </summary>
        public void PrePass(List<GameAction> actions)
        {
            ComputeTotalSpendings(actions);
        }

        /// <summary>
        /// Processes a single game action during the recalculation walk.
        /// Handles ScienceEarning, ScienceSpending, ContractComplete (science reward),
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
                case GameActionType.ContractComplete:
                    ProcessContractScienceReward(action);
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
        /// Pre-pass: sums all ScienceSpending costs from the action list before the walk starts.
        /// Called by the engine (or test harness) before <see cref="ProcessAction"/> is invoked.
        /// This is required for the reservation system: available = effective earnings - ALL spendings.
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
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i] != null && actions[i].Type == GameActionType.ScienceSpending)
                {
                    totalCommittedSpendings += (double)actions[i].Cost;
                    spendingCount++;
                }
            }

            ParsekLog.Verbose("ScienceModule",
                $"ComputeTotalSpendings: spendingCount={spendingCount}, " +
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
            // (in practice this should be consistent, but be defensive)
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
        /// Sets <see cref="GameAction.Affordable"/>.
        /// </summary>
        internal void ProcessSpending(GameAction action)
        {
            float cost = action.Cost;
            bool affordable = runningScience >= (double)cost;

            action.Affordable = affordable;

            if (affordable)
            {
                runningScience -= (double)cost;

                ParsekLog.Verbose("ScienceModule",
                    $"Spending: nodeId={action.NodeId ?? "(none)"}, cost={cost.ToString("R", IC)}, " +
                    $"affordable=true, runningScience={runningScience.ToString("R", IC)}");
            }
            else
            {
                ParsekLog.Warn("ScienceModule",
                    $"Spending NOT affordable: nodeId={action.NodeId ?? "(none)"}, cost={cost.ToString("R", IC)}, " +
                    $"runningScience={runningScience.ToString("R", IC)} — possible bug or data corruption");
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
        ///   availableScience = totalEffectiveEarnings - totalCommittedSpendings
        ///
        /// Requires <see cref="ComputeTotalSpendings"/> to have been called before the walk.
        /// Returns 0 if the result would be negative (player cannot spend negative science).
        /// </summary>
        internal double GetAvailableScience()
        {
            double available = totalEffectiveEarnings - totalCommittedSpendings;
            return available > 0.0 ? available : 0.0;
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
    }
}
