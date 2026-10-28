namespace Parsek
{
    /// <summary>
    /// Which stock call a <c>FundsChanged(VesselRecovery)</c> comes from
    /// (SMALL-RECOVERY-FUNDS-ERASED-IN-REFLY). KSP 1.12.5, decompiled: stock pays a vessel's
    /// recovery value in exactly one place, <c>Funding.onVesselRecoveryProcessing</c>, through
    /// one <c>AddFunds(value, VesselRecovery)</c>. A strategy's effect on a query of ANOTHER
    /// currency under the same reason (Patents Licensing turning a recovered subject's or a
    /// lab's science into funds) moves funds in <c>Funding.OnCurrenciesModified</c>, which
    /// fires its own <c>FundsChanged</c> under the query's reason. That yield is already a
    /// <c>FundsEarning(Strategy)</c> leg, so it must never be taken for the payout.
    ///
    /// <para>Two depth counters, set by <c>Patches/RecoveryPayoutScopePatches.cs</c>:
    /// inside the payout method, the event is the payout (the payout's own query effect, if
    /// any strategy masks <c>VesselRecovery</c> for funds, folds into one net event and a
    /// zero-delta one); inside <c>OnCurrenciesModified</c> but not the payout, it is a
    /// strategy effect; inside neither (a mod's own <c>AddFunds</c>, or the patches did not
    /// apply), it is unscoped and handled exactly as before this split.</para>
    /// </summary>
    internal static class RecoveryPayoutScope
    {
        private static int payoutDepth;
        private static int fundsEffectDepth;

        internal enum FundsEventSource
        {
            /// <summary>Neither scope is open: the pre-split handling (the 100 floor, pairs).</summary>
            Unscoped,
            /// <summary>Inside stock's payout method: the recovery value itself.</summary>
            Payout,
            /// <summary>A strategy effect on another currency's query: never a payout.</summary>
            StrategyEffect,
        }

        internal static int PayoutDepth => payoutDepth;
        internal static int FundsEffectDepth => fundsEffectDepth;

        internal static void EnterPayout() { payoutDepth++; }
        internal static void ExitPayout() { if (payoutDepth > 0) payoutDepth--; }
        internal static void EnterFundsEffect() { fundsEffectDepth++; }
        internal static void ExitFundsEffect() { if (fundsEffectDepth > 0) fundsEffectDepth--; }

        /// <summary>
        /// Pure: whether a currency query opens the funds-effect scope. Only a query with no
        /// funds input of its own can turn another currency into funds; a query WITH a funds
        /// input is an <c>AddFunds</c> (a mod paying its own recovery under
        /// <c>VesselRecovery</c>), whose net event is that payout and must stay pairable.
        /// </summary>
        internal static bool OpensFundsEffectScope(double queryFundsInput)
        {
            return !(System.Math.Abs(queryFundsInput) > 0.0);
        }

        /// <summary>Pure: the source of a funds change seen with these depths.</summary>
        internal static FundsEventSource Classify(int payoutDepthNow, int fundsEffectDepthNow)
        {
            if (payoutDepthNow > 0)
                return FundsEventSource.Payout;
            if (fundsEffectDepthNow > 0)
                return FundsEventSource.StrategyEffect;
            return FundsEventSource.Unscoped;
        }

        internal static FundsEventSource Current => Classify(payoutDepth, fundsEffectDepth);

        internal static void ResetForTesting()
        {
            payoutDepth = 0;
            fundsEffectDepth = 0;
        }
    }
}
