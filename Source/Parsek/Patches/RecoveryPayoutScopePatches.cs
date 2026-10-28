using System;
using HarmonyLib;
using KSP.UI.Screens;

namespace Parsek.Patches
{
    /// <summary>
    /// Opens <see cref="RecoveryPayoutScope"/>'s payout scope around stock's recovery payout,
    /// <c>Funding.onVesselRecoveryProcessing</c> (private; KSP 1.12.5 decompiled: sums the
    /// parts' recovery value and calls <c>AddFunds(value, VesselRecovery)</c> once). A
    /// finalizer closes it even when stock throws.
    /// </summary>
    [HarmonyPatch(typeof(Funding), "onVesselRecoveryProcessing",
        typeof(ProtoVessel), typeof(MissionRecoveryDialog), typeof(float))]
    internal static class RecoveryPayoutScopePatch
    {
        static void Prefix(out bool __state)
        {
            RecoveryPayoutScope.EnterPayout();
            __state = true;
        }

        static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state)
                RecoveryPayoutScope.ExitPayout();
            return __exception;
        }
    }

    /// <summary>
    /// Opens <see cref="RecoveryPayoutScope"/>'s funds-effect scope around
    /// <c>Funding.OnCurrenciesModified</c> (private; KSP 1.12.5 decompiled: adds the query's
    /// funds effect delta and fires <c>OnFundsChanged</c> under the query's reason when it is
    /// nonzero). Outside a payout scope that event is a strategy effect, never a payout. Only a
    /// query with no funds input opens it (<see cref="RecoveryPayoutScope.OpensFundsEffectScope"/>):
    /// a funds-input query is an <c>AddFunds</c>, such as a mod paying its own recovery.
    /// </summary>
    [HarmonyPatch(typeof(Funding), "OnCurrenciesModified", typeof(CurrencyModifierQuery))]
    internal static class RecoveryFundsEffectScopePatch
    {
        static void Prefix(CurrencyModifierQuery query, out bool __state)
        {
            __state = query == null
                || RecoveryPayoutScope.OpensFundsEffectScope(query.GetInput(Currency.Funds));
            if (__state)
                RecoveryPayoutScope.EnterFundsEffect();
        }

        static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state)
                RecoveryPayoutScope.ExitFundsEffect();
            return __exception;
        }
    }
}
