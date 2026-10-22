using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Prefix on the protected <c>Contracts.Contract.SetState(State)</c>: refuses to fail an
    /// Active contract whose outcome the committed timeline already holds later (owner ruling
    /// L1.1, rule 1 of the committed-value rule, game-actions design section 3.13). A new
    /// flight after a rewind would otherwise charge the fail penalty before a committed
    /// completion, which then pays nothing, and leave the committed purchases that completion
    /// paid for unpaid. A committed fail or cancel moves earlier the same way. A completion is
    /// not intercepted: it only moves the reward earlier. A refused fail freezes the contract
    /// where stock left it (stock's <c>Update()</c> returns after its <c>SetState</c> call, so
    /// per-frame parameters stop updating while its requirements are unmet, and a refused failed
    /// parameter stays Failed), which the committed outcome resolves.
    /// <para>Stock funnel (decompiled, KSP 1.12.5): <c>Fail()</c> and a failed non-optional
    /// parameter (<c>ParameterStateUpdate</c>) both reach <c>SetState(State.Failed)</c>, which
    /// runs <c>PenalizeFailure()</c>; refusing it leaves the contract Active, and the state
    /// patch applies the committed outcome at the next recalculation past its UT. The refusal
    /// is strict-future: the flight scene has no clock cursor, so between that UT and the next
    /// recalculation a fail goes through (known gap, todo LEDGER-RETIREMENT-PATHS-OUTSIDE-REFLY).
    /// Stock's <c>Fail()</c> ignores <c>SetState</c>'s outcome and returns true when refused. <c>DeadlineExpired</c>
    /// is not intercepted: the committed timeline holds the same expiry at the same UT
    /// (design 3.13, cases that need no rule). <c>OfferExpired</c> on an Active contract
    /// (stock's <c>Update()</c>, requirements no longer met) is refused too. <c>Fail()</c> and
    /// <c>Update()</c> set <c>dateFinished</c> before <c>SetState</c>, so a refused contract
    /// keeps that date until its committed outcome; nothing reads it while it is Active.
    /// A refusal from <c>Update()</c> repeats every frame, so its log line is rate-limited. Parsek itself never calls <c>SetState</c>
    /// (<c>KspStatePatcher.PatchContracts</c> writes contract state directly). It reads
    /// <see cref="StockUiReservationPredicates.CommittedContractResolutionAfter"/>, the
    /// predicate the Cancel block (ruling D7) reads, so a contract whose Cancel is greyed
    /// cannot fail in flight either.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class ContractEarlyFailPatch
    {
        private const string Tag = "ContractEarlyFailPatch";

        /// <summary>How many fails the patch has refused this process (test and seam read).</summary>
        internal static int RefusedFailCount { get; private set; }

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "Contracts.Contract.SetState(State) not found - a flight failing a contract the committed timeline resolves later will not be refused. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(Contracts.Contract), "SetState",
                new[] { typeof(Contracts.Contract.State) });
        }

        static bool Prefix(Contracts.Contract __instance, Contracts.Contract.State newState)
        {
            if (!IsEarlyEndState(newState)) return true;
            if (ParsekGameModeGate.CheckInert("ContractEarlyFailPatch.Prefix")) return true; // S9 game-mode gate
            if (__instance == null) return true;
            if (__instance.ContractState != Contracts.Contract.State.Active) return true;

            string key = __instance.ContractGuid.ToString();
            return ShouldAllowFail(key, __instance.Title ?? key);
        }

        /// <summary>
        /// The end states of an Active contract that would move a committed outcome earlier:
        /// <c>Failed</c> (charges the fail penalty) and <c>OfferExpired</c>, which stock's
        /// <c>Update()</c> sets on an Active contract whose requirements no longer hold (no
        /// penalty, but the contract leaves the active list before its committed outcome).
        /// </summary>
        internal static bool IsEarlyEndState(Contracts.Contract.State newState)
        {
            return newState == Contracts.Contract.State.Failed
                || newState == Contracts.Contract.State.OfferExpired;
        }

        /// <summary>
        /// The decision for a fail of an Active contract: false (refuse) while the committed
        /// timeline completes, fails or cancels it after now.
        /// </summary>
        internal static bool ShouldAllowFail(string keyString, string title)
        {
            if (string.IsNullOrEmpty(keyString)) return true;

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose(Tag, "bypass - replay in progress (guid=" + keyString + ")");
                return true;
            }

            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            var resolution = StockUiReservationPredicates.CommittedContractResolutionAfter(index, keyString, nowUT);
            if (resolution == null)
            {
                ParsekLog.Verbose(Tag,
                    "allowing fail for guid=" + keyString + " - no committed outcome after now " +
                    "(nowUT=" + nowUT.ToString("F0", CultureInfo.InvariantCulture) + ")");
                return true;
            }

            RefusedFailCount++;
            ParsekLog.InfoRateLimited(Tag, "refuse|" + keyString,
                "refusing fail of \"" + (string.IsNullOrEmpty(title) ? keyString : title) + "\" guid=" + keyString +
                " - committed " + resolution.Kind + " ut=" + resolution.UT.ToString("F0", CultureInfo.InvariantCulture) +
                " nowUT=" + nowUT.ToString("F0", CultureInfo.InvariantCulture) +
                " recording=" + (resolution.RecordingId ?? "(ksc)") + "; the contract stays Active until then");
            return false;
        }

        internal static void ResetForTesting()
        {
            RefusedFailCount = 0;
        }
    }
}
