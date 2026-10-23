using System;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Prefix + Postfix on the protected <c>Contracts.Contract.SetState(State)</c>: captures
    /// stock ending an Active contract as <c>OfferExpired</c>. Stock's <c>Update()</c> does that
    /// when an Active contract's requirements no longer hold (KSP 1.12.5, decompiled); the
    /// end charges no penalty and fires only <c>Contract.onFinished</c>, which also fires for
    /// an Offered contract expiring and for <c>Kill()</c>, so the event alone cannot tell the
    /// transition apart. The prefix records whether the contract was Active; the postfix raises
    /// <see cref="ActiveContractOfferExpired"/> only when it was and the call left it
    /// OfferExpired. A call <see cref="ContractEarlyFailPatch"/> refused leaves the contract
    /// Active, so nothing is raised. The subscribed <see cref="GameStateRecorder"/> files the
    /// terminal row (todo STOCK-OFFER-EXPIRED-ACTIVE-CONTRACT-NO-TERMINAL-ROW).
    /// </summary>
    [HarmonyPatch]
    internal static class ContractOfferExpiredCapturePatch
    {
        private const string Tag = "ContractOfferExpiredCapture";

        /// <summary>Raised with the contract after stock ended it Active -> OfferExpired.</summary>
        internal static event Action<Contracts.Contract> ActiveContractOfferExpired;

        static MethodBase TargetMethod()
        {
            var method = ContractEarlyFailPatch.ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "Contracts.Contract.SetState(State) not found - an Active contract stock ends as OfferExpired will keep no terminal ledger row. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");
            return method;
        }

        static void Prefix(Contracts.Contract __instance, Contracts.Contract.State newState, out bool __state)
        {
            __state = false;
            if (newState != Contracts.Contract.State.OfferExpired) return;
            if (ParsekGameModeGate.CheckInert("ContractOfferExpiredCapturePatch.Prefix")) return; // S9 game-mode gate
            if (__instance == null) return;
            __state = __instance.ContractState == Contracts.Contract.State.Active;
        }

        static void Postfix(Contracts.Contract __instance, bool __state)
        {
            if (!__state || __instance == null) return;
            if (!IsActiveOfferExpiredTransition(__state, __instance.ContractState)) return;
            Report(__instance);
        }

        /// <summary>
        /// The capture decision: the contract was Active before <c>SetState</c> and is
        /// OfferExpired after it.
        /// </summary>
        internal static bool IsActiveOfferExpiredTransition(bool wasActive, Contracts.Contract.State stateAfter)
        {
            return wasActive && stateAfter == Contracts.Contract.State.OfferExpired;
        }

        /// <summary>Raises <see cref="ActiveContractOfferExpired"/> (called by the postfix).</summary>
        internal static void Report(Contracts.Contract contract)
        {
            var handler = ActiveContractOfferExpired;
            if (handler == null)
            {
                ParsekLog.Verbose(Tag,
                    "Active contract ended as OfferExpired with no subscribed recorder - not captured (guid=" +
                    (contract != null ? contract.ContractGuid.ToString() : "(null)") + ")");
                return;
            }
            try
            {
                handler(contract);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"ActiveContractOfferExpired handler threw ({ex.GetType().Name}: {ex.Message})");
            }
        }

        internal static void ResetForTesting()
        {
            ActiveContractOfferExpired = null;
        }
    }
}
