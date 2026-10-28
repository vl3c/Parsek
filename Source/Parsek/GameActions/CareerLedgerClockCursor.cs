using System;

namespace Parsek
{
    /// <summary>
    /// Keeps the career ledger in step with a clock that moves with no scene change (the
    /// Space Center and Tracking Station warp). Both scenes load with the current-UT cutoff,
    /// so committed actions still ahead of the clock are filtered out of the patched career;
    /// when the clock reaches the next such action (or moves backward) this runs one
    /// live-timeline recalculation at the new UT. Per frame it costs a cached lookup and two
    /// comparisons; the recalculation runs once per crossed action.
    /// </summary>
    internal sealed class CareerLedgerClockCursor
    {
        internal const double AdvanceEpsilonSeconds = 0.05;

        private readonly string reasonPrefix;
        private readonly Func<double, double> nextActionUTAfter;
        private readonly Action<double, string> recalculate;

        private double lastCutoffUT = double.NaN;
        private int cachedNextActionVersion = int.MinValue;
        private double cachedNextActionAfterUT = double.NaN;
        private double cachedNextActionUT = double.PositiveInfinity;

        /// <param name="reasonPrefix">Scene tag for the recalculation reason, e.g. "ksc"
        /// gives "ksc-clock" / "ksc-clock-backward".</param>
        /// <param name="nextActionUTAfter">Test seam; defaults to the live ledger, cached on
        /// <see cref="Ledger.StateVersion"/>.</param>
        /// <param name="recalculate">Test seam; defaults to
        /// <see cref="LedgerOrchestrator.RecalculateAndPatchForClockAdvance"/>, which lets
        /// the reached committed-future rows past the drawdown guard.</param>
        internal CareerLedgerClockCursor(
            string reasonPrefix,
            Func<double, double> nextActionUTAfter = null,
            Action<double, string> recalculate = null)
        {
            this.reasonPrefix = string.IsNullOrEmpty(reasonPrefix) ? "scene" : reasonPrefix;
            this.nextActionUTAfter = nextActionUTAfter;
            this.recalculate = recalculate ?? LedgerOrchestrator.RecalculateAndPatchForClockAdvance;
        }

        internal double LastCutoffUT => lastCutoffUT;

        /// <summary>
        /// Advances the cursor to <paramref name="currentUT"/>. The first valid frame only
        /// seeds it (the scene load already recalculated at that clock). Returns true when
        /// it recalculated.
        /// </summary>
        internal bool Advance(double currentUT)
        {
            if (double.IsNaN(currentUT) || double.IsInfinity(currentUT))
                return false;

            if (ParsekKSC.ShouldSeedCareerLedgerForKscUT(currentUT, lastCutoffUT))
            {
                lastCutoffUT = currentUT;
                return false;
            }

            // Rows up to the ledger held-through UT are in the loaded world (a Rewind-to-Launch
            // lead window): reaching one is no crossing, so the next row that is one lies after it.
            double nextActionUT = GetNextActionUTAfter(LedgerHeldWindow.ResolveLedgerNowUT(lastCutoffUT));
            if (!ParsekKSC.ShouldAdvanceCareerLedgerForKscUT(
                    currentUT, lastCutoffUT, nextActionUT, AdvanceEpsilonSeconds))
                return false;

            string reason = FormatReason(reasonPrefix, currentUT, lastCutoffUT, AdvanceEpsilonSeconds);
            recalculate(currentUT, reason);

            // Advance even if a pending/live tree makes the internal patch defer; tree
            // resolution runs its own recalc, while retrying here every frame would spam
            // full ledger walks.
            lastCutoffUT = currentUT;
            return true;
        }

        internal static string FormatReason(
            string reasonPrefix, double currentUT, double lastAppliedUT, double epsilon)
        {
            return currentUT < lastAppliedUT - epsilon
                ? reasonPrefix + "-clock-backward"
                : reasonPrefix + "-clock";
        }

        private double GetNextActionUTAfter(double afterUT)
        {
            if (nextActionUTAfter != null)
                return nextActionUTAfter(afterUT);

            int ledgerVersion = Ledger.StateVersion;
            if (!ParsekKSC.IsKscLedgerNextActionCacheValid(
                    cachedNextActionVersion,
                    cachedNextActionAfterUT,
                    ledgerVersion,
                    afterUT))
            {
                cachedNextActionVersion = ledgerVersion;
                cachedNextActionAfterUT = afterUT;
                if (!LedgerOrchestrator.TryGetNextActionUTAfter(afterUT, out cachedNextActionUT))
                    cachedNextActionUT = double.PositiveInfinity;
            }

            return cachedNextActionUT;
        }
    }
}
