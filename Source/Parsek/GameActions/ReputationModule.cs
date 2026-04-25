using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Second-tier reputation module. Simulates KSP's non-linear reputation curve
    /// to compute effective rep changes from nominal values.
    ///
    /// Reads Effective flags set by first-tier modules (Milestones, Contracts).
    /// Running rep starts at 0 and is updated by applying the gain or loss curve
    /// for each rep-affecting action in UT order.
    ///
    /// The curve replicates KSP's addReputation_granular algorithm:
    ///   - Split nominal delta into integer-sized steps
    ///   - Each step is multiplied by a curve-derived factor based on currentRep / repRange
    ///   - Gain curve: multiplier ~2.0x at rep=-1000, ~1.0x at rep=0, ~0.0x at rep=+1000
    ///   - Loss curve: multiplier ~0.0x at rep=-1000, ~1.0x at rep=0, ~2.0x at rep=+1000
    ///
    /// Pure computation — no KSP state access.
    /// Design doc: section 6 (Reputation Module).
    /// Curve keyframes: docs/dev/plans/game-actions-spike-findings.md (Spike A).
    /// </summary>
    internal class ReputationModule : IResourceModule
    {
        private const string Tag = "Reputation";
        private const float RepRange = 1000f;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private float runningRep;

        /// <summary>
        /// True when a ReputationInitial action was processed during the current walk.
        /// When false, the module has no seed balance and patching should be skipped.
        /// </summary>
        private bool hasInitialSeed;

        // ================================================================
        // IResourceModule
        // ================================================================

        /// <inheritdoc/>
        public void Reset()
        {
            float previousRep = runningRep;
            runningRep = 0f;
            hasInitialSeed = false;
            ParsekLog.Verbose(Tag, $"Reset: runningRep {previousRep.ToString("F2", IC)} -> 0");
        }

        /// <inheritdoc/>
        public void PrePass(System.Collections.Generic.List<GameAction> actions, double? walkNowUT = null)
        {
            // No pre-pass needed for reputation; walkNowUT is ignored.
        }

        /// <inheritdoc/>
        public void ProcessAction(GameAction action)
        {
            switch (action.Type)
            {
                case GameActionType.ReputationEarning:
                    ProcessRepEarning(action);
                    break;

                case GameActionType.ReputationPenalty:
                    ProcessRepPenalty(action);
                    break;

                case GameActionType.MilestoneAchievement:
                    ProcessMilestoneRep(action);
                    break;

                case GameActionType.ContractComplete:
                    ProcessContractCompleteRep(action);
                    break;

                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    ProcessContractPenaltyRep(action);
                    break;

                case GameActionType.ReputationInitial:
                    ProcessReputationInitial(action);
                    break;

                case GameActionType.StrategyActivate:
                    ProcessStrategySetupReputation(action);
                    break;

                default:
                    // Not a rep-affecting action — skip silently
                    return;
            }
        }

        // ================================================================
        // Per-type processing
        // ================================================================

        private void ProcessRepEarning(GameAction action)
        {
            float nominal = action.NominalRep;
            var result = ApplyReputationCurve(nominal, runningRep);

            action.EffectiveRep = result.actualDelta;
            runningRep = result.newRep;

            ParsekLog.Verbose(Tag,
                $"RepEarning at UT={action.UT.ToString("F1", IC)}: nominal={nominal.ToString("F2", IC)}, " +
                $"effective={result.actualDelta.ToString("F2", IC)}, runningRep={runningRep.ToString("F2", IC)}" +
                $" (recording={action.RecordingId ?? "null"})");
        }

        private void ProcessRepPenalty(GameAction action)
        {
            // NominalPenalty is stored as a positive magnitude; curve expects negative input for losses
            float nominal = -action.NominalPenalty;
            var result = ApplyReputationCurve(nominal, runningRep);

            action.EffectiveRep = result.actualDelta; // negative
            runningRep = result.newRep;

            ParsekLog.Verbose(Tag,
                $"RepPenalty at UT={action.UT.ToString("F1", IC)}: nominalPenalty={action.NominalPenalty.ToString("F2", IC)}, " +
                $"effective={result.actualDelta.ToString("F2", IC)}, runningRep={runningRep.ToString("F2", IC)}" +
                $" (recording={action.RecordingId ?? "null"})");
        }

        private void ProcessMilestoneRep(GameAction action)
        {
            if (!action.Effective)
            {
                action.EffectiveRep = 0f;
                ParsekLog.Verbose(Tag,
                    $"Milestone rep skipped (not effective) at UT={action.UT.ToString("F1", IC)}" +
                    $": milestoneId={action.MilestoneId ?? "null"}");
                return;
            }

            float nominal = action.MilestoneRepAwarded;
            if (nominal == 0f)
                return;

            var result = ApplyReputationCurve(nominal, runningRep);

            action.EffectiveRep = result.actualDelta;
            runningRep = result.newRep;

            // Bug #593: every effective milestone re-emits this line on every
            // recalc walk, even though milestoneId/recordingId/nominal don't
            // change between walks. Rate-limit per (milestoneId, recordingId).
            string key = string.Format(IC,
                "milestone-rep-{0}-{1}",
                action.MilestoneId ?? "null",
                action.RecordingId ?? "(none)");
            ParsekLog.VerboseRateLimited(Tag, key,
                $"Milestone rep at UT={action.UT.ToString("F1", IC)}: milestoneId={action.MilestoneId ?? "null"}, " +
                $"recordingId={action.RecordingId ?? "(none)"}, " +
                $"nominal={nominal.ToString("F2", IC)}, effective={result.actualDelta.ToString("F2", IC)}, " +
                $"runningRep={runningRep.ToString("F2", IC)}");
        }

        private void ProcessContractCompleteRep(GameAction action)
        {
            if (!action.Effective)
            {
                action.EffectiveRep = 0f;
                ParsekLog.Verbose(Tag,
                    $"Contract complete rep skipped (not effective) at UT={action.UT.ToString("F1", IC)}" +
                    $": contractId={action.ContractId ?? "null"}");
                return;
            }

            float nominal = action.TransformedRepReward;
            if (nominal == 0f)
                return;

            var result = ApplyReputationCurve(nominal, runningRep);

            action.EffectiveRep = result.actualDelta;
            runningRep = result.newRep;

            ParsekLog.Verbose(Tag,
                $"Contract complete rep at UT={action.UT.ToString("F1", IC)}: contractId={action.ContractId ?? "null"}, " +
                $"nominal={nominal.ToString("F2", IC)}, effective={result.actualDelta.ToString("F2", IC)}, " +
                $"runningRep={runningRep.ToString("F2", IC)}");
        }

        private void ProcessContractPenaltyRep(GameAction action)
        {
            float nominal = -action.RepPenalty;
            if (nominal == 0f)
                return;

            var result = ApplyReputationCurve(nominal, runningRep);

            action.EffectiveRep = result.actualDelta; // negative
            runningRep = result.newRep;

            ParsekLog.Verbose(Tag,
                $"Contract {action.Type} rep at UT={action.UT.ToString("F1", IC)}: " +
                $"contractId={action.ContractId ?? "null"}, " +
                $"nominalPenalty={action.RepPenalty.ToString("F2", IC)}, effective={result.actualDelta.ToString("F2", IC)}, " +
                $"runningRep={runningRep.ToString("F2", IC)}");
        }

        private void ProcessStrategySetupReputation(GameAction action)
        {
            float nominal = -action.SetupReputationCost;
            if (nominal == 0f)
                return;

            var result = ApplyReputationCurve(nominal, runningRep);

            action.EffectiveRep = result.actualDelta;
            runningRep = result.newRep;

            ParsekLog.Verbose(Tag,
                $"StrategyActivate rep at UT={action.UT.ToString("F1", IC)}: " +
                $"strategyId={action.StrategyId ?? "null"}, " +
                $"nominalPenalty={action.SetupReputationCost.ToString("F2", IC)}, " +
                $"effective={result.actualDelta.ToString("F2", IC)}, " +
                $"runningRep={runningRep.ToString("F2", IC)}");
        }

        // ================================================================
        // Reputation initial seed
        // ================================================================

        /// <summary>
        /// Processes a ReputationInitial action: sets baseline reputation for mid-career install.
        /// Directly sets the running reputation without applying the curve — the initial value
        /// is the actual reputation the player has, not a nominal delta.
        /// </summary>
        internal void ProcessReputationInitial(GameAction action)
        {
            float initial = action.InitialReputation;
            runningRep += initial;
            hasInitialSeed = true;

            ParsekLog.Info(Tag,
                $"ReputationInitial: seed={initial.ToString("R", IC)}, " +
                $"runningRep={runningRep.ToString("R", IC)}");
        }

        // ================================================================
        // Public query
        // ================================================================

        /// <summary>
        /// True when the module processed a ReputationInitial action during the walk.
        /// When false, the module has no seed balance and KSP reputation should not be patched.
        /// </summary>
        internal bool HasSeed => hasInitialSeed;

        /// <summary>
        /// Returns the current running reputation after the most recent recalculation walk.
        /// </summary>
        internal float GetRunningRep()
        {
            return runningRep;
        }

        // ================================================================
        // Curve implementation (Spike A findings)
        // ================================================================

        /// <summary>
        /// Replicates KSP's addReputation_granular algorithm.
        /// Splits the nominal delta into integer-sized steps, applies the curve
        /// multiplier at each step based on current rep.
        ///
        /// For positive nominal: uses addition curve (gain diminishes at high rep).
        /// For negative nominal: uses subtraction curve (loss amplified at high rep).
        /// </summary>
        internal static (float actualDelta, float newRep) ApplyReputationCurve(
            float nominal, float currentRep, float repRange = RepRange)
        {
            if (nominal == 0f)
                return (0f, currentRep);

            int num = (int)Math.Abs(nominal);
            float delta = Math.Sign(nominal);
            float accumulated = 0f;
            float rep = currentRep;

            for (int i = 0; i <= num; i++)
            {
                float input = (i != num) ? delta : (nominal - (delta * num));
                if (input == 0f)
                    continue;

                float time = rep / repRange;
                float mult = (input < 0f)
                    ? EvaluateSubtractionCurve(time)
                    : EvaluateAdditionCurve(time);
                float step = input * mult;
                rep += step;
                accumulated += step;
            }

            return (accumulated, rep);
        }

        // ================================================================
        // Hermite spline curve evaluators
        // ================================================================

        // reputationAddition keyframes (5 keys) from Spike A
        private static readonly float[] addTimes  = { -1.000108f, -0.505605f, 0.001540f, 0.501354f, 1.000023f };
        private static readonly float[] addValues = {  2.001723f,  1.500368f, 0.999268f, 0.503444f, -0.000005f };
        private static readonly float[] addInSlopes  = {  0.873274f, -2.772799f, 0.009784f, -2.572293f, -0.006748f };
        private static readonly float[] addOutSlopes = { -0.025381f, -2.772799f, 0.009784f, -2.572293f,  1.003260f };

        // reputationSubtraction keyframes (4 keys) from Spike A
        private static readonly float[] subTimes  = { -1.000136f, -1.000038f, -0.000005f, 1.000356f };
        private static readonly float[] subValues = { -0.000129f,  0.049983f,  1.000065f, 1.998481f };
        private static readonly float[] subInSlopes  = { -1216.706f, 2.479460f, 0.950051f, 0.998054f };
        private static readonly float[] subOutSlopes = {   510.160f, 0.950051f, 0.998054f, 0.949444f };

        /// <summary>
        /// Evaluates the reputation addition (gain) curve at the given normalized time.
        /// Time is currentRep / repRange, typically in [-1, +1].
        /// Returns the multiplier to apply to a positive reputation delta.
        /// </summary>
        internal static float EvaluateAdditionCurve(float time)
        {
            return EvaluateHermiteSpline(time,
                addTimes, addValues, addInSlopes, addOutSlopes);
        }

        /// <summary>
        /// Evaluates the reputation subtraction (loss) curve at the given normalized time.
        /// Time is currentRep / repRange, typically in [-1, +1].
        /// Returns the multiplier to apply to a negative reputation delta.
        /// </summary>
        internal static float EvaluateSubtractionCurve(float time)
        {
            return EvaluateHermiteSpline(time,
                subTimes, subValues, subInSlopes, subOutSlopes);
        }

        /// <summary>
        /// Evaluates a cubic Hermite spline defined by keyframes.
        /// Clamps to boundary values outside the key range.
        /// Uses outSlope of left key and inSlope of right key for each segment,
        /// matching Unity's AnimationCurve behavior.
        /// </summary>
        private static float EvaluateHermiteSpline(
            float time, float[] times, float[] values,
            float[] inSlopes, float[] outSlopes)
        {
            int count = times.Length;
            if (count == 0)
                return 0f;

            // Clamp to first/last key
            if (time <= times[0])
                return values[0];
            if (time >= times[count - 1])
                return values[count - 1];

            // Find the segment
            int left = 0;
            for (int i = 0; i < count - 1; i++)
            {
                if (time < times[i + 1])
                {
                    left = i;
                    break;
                }
            }

            int right = left + 1;
            float dt = times[right] - times[left];
            if (dt <= 0f)
                return values[left];

            float t = (time - times[left]) / dt;

            // Unity AnimationCurve uses outSlope of left key, inSlope of right key
            // Tangents are scaled by the segment duration
            float m0 = outSlopes[left] * dt;
            float m1 = inSlopes[right] * dt;

            return CubicHermite(t, values[left], m0, values[right], m1);
        }

        /// <summary>
        /// Standard cubic Hermite interpolation.
        /// t in [0,1], p0/p1 are endpoint values, m0/m1 are endpoint tangents.
        /// </summary>
        internal static float CubicHermite(float t, float p0, float m0, float p1, float m1)
        {
            float t2 = t * t;
            float t3 = t2 * t;

            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            return h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
        }

        public void PostWalk() { }
    }
}
