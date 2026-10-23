using System;

namespace Parsek
{
    /// <summary>
    /// The penalty stock charges when the player cancels an Active contract (KSP 1.12.5,
    /// decompiled <c>Contracts.Contract.PenalizeCancellation</c>):
    /// <c>t = Mathf.InverseLerp((float)dateAccepted, (float)dateDeadline, (float)dateFinished)</c>,
    /// funds <c>|Mathfx.Lerp(FundsAdvance, FundsFailure, t)|</c> and reputation
    /// <c>|Mathfx.Lerp(RepLossDeclined, ReputationFailure, t)|</c>. A cancel right after the
    /// accept costs about the advance and the decline loss; one at the deadline costs the full
    /// fail penalty. Only <c>PenalizeFailure</c> (a fail or a deadline expiry) charges
    /// <c>FundsFailure</c> / <c>ReputationFailure</c> whole.
    /// <para>The float casts are stock's: the dates are narrowed to float before the inverse
    /// lerp, so this reproduces stock's rounding rather than an exact double fraction. The
    /// reputation amount is the raw (pre-curve) value stock passes to <c>AddReputation</c>,
    /// the same raw shape a ContractFail row carries.</para>
    /// </summary>
    internal static class ContractCancellationPenalty
    {
        /// <summary>Stock's time fraction of the cancel, in [0, 1].</summary>
        internal static float TimeFraction(double dateAccepted, double dateDeadline, double dateFinished)
        {
            return InverseLerp((float)dateAccepted, (float)dateDeadline, (float)dateFinished);
        }

        /// <summary>The funds stock deducts for the cancel (non-negative).</summary>
        internal static double Funds(double fundsAdvance, double fundsFailure, float t)
        {
            double lerp = (1.0 - t) * fundsAdvance + t * fundsFailure;
            return Math.Abs(lerp);
        }

        /// <summary>The raw (pre-curve) reputation stock deducts for the cancel (non-negative).</summary>
        internal static float Reputation(float repLossDeclined, float reputationFailure, float t)
        {
            float lerp = (1f - t) * repLossDeclined + t * reputationFailure;
            return Math.Abs(lerp);
        }

        /// <summary>
        /// UnityEngine <c>Mathf.InverseLerp</c>, transcribed so the computation runs headless:
        /// 0 when the endpoints are equal, else <c>Clamp01((value - a) / (b - a))</c>.
        /// </summary>
        internal static float InverseLerp(float a, float b, float value)
        {
            if (a != b)
                return Clamp01((value - a) / (b - a));
            return 0f;
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }
    }
}
