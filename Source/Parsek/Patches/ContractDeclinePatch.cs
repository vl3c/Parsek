using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Backstop prefix on the non-virtual <c>Contracts.Contract.Decline()</c>: refuses to
    /// decline an Offered contract the committed future accepts (owner ruling D7,
    /// docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md section 7.2). It reads
    /// the SAME predicate as the Accept block and the Mission Control mark, so Decline is
    /// refused exactly where the row is marked and the button greyed out.
    /// <para>Callers of <c>Decline()</c> in KSP 1.12.5 (whole-assembly IL scan): stock
    /// <c>MissionControl.OnClickDecline</c> and two debug-toolbar buttons
    /// (<c>DebugToolbar</c>'s contract list, <c>ScreenContractExistingItem.OnRightButtonClicked</c>),
    /// all player actions. No automatic stock path declines (offer expiry and withdrawal
    /// go through <c>SetState</c>), so nothing that must succeed is refused. Contract
    /// Configurator's <c>OnClickDecline</c> and kRPC's <c>Contract.Decline</c> call it
    /// too and are refused the same way.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class ContractDeclinePatch
    {
        private const string Tag = "ContractDeclinePatch";

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "Contracts.Contract.Decline() not found - declining a committed-accept contract will not be refused. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(Contracts.Contract), nameof(Contracts.Contract.Decline), Type.EmptyTypes);
        }

        static bool Prefix(Contracts.Contract __instance)
        {
            if (__instance == null) return true;
            if (__instance.ContractState != Contracts.Contract.State.Offered)
            {
                // Stock Decline is a no-op for anything but an Offered contract.
                ParsekLog.Verbose(Tag, "allowing decline for guid=" + __instance.ContractGuid
                    + " - state=" + __instance.ContractState + " is not Offered (stock no-op)");
                return true;
            }

            string key = __instance.ContractGuid.ToString();
            return ShouldAllowDecline(key, __instance.Title ?? key);
        }

        internal static bool ShouldAllowDecline(string keyString, string title)
        {
            if (string.IsNullOrEmpty(keyString)) return true;

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose(Tag, "bypass - replay in progress (guid=" + keyString + ")");
                return true;
            }

            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            if (!StockUiReservationPredicates.IsContractAcceptBlocked(index, keyString, nowUT))
            {
                ParsekLog.Verbose(Tag,
                    "allowing decline for guid=" + keyString + " - no committed future accept " +
                    "(nowUT=" + nowUT.ToString("F0", CultureInfo.InvariantCulture) + ")");
                return true;
            }

            var entry = index.FirstFuture(CommittedFutureKind.ContractAccept, keyString, nowUT);
            ParsekLog.Info(Tag,
                "blocking decline for guid=" + keyString + " - committed future accept " +
                "ut=" + entry.UT.ToString("F0", CultureInfo.InvariantCulture) + " " +
                "nowUT=" + nowUT.ToString("F0", CultureInfo.InvariantCulture) + " " +
                "recording=" + (entry.RecordingId ?? "(ksc)"));

            var text = StockUiReservationPredicates.ExplainContractAccept(
                index, keyString, nowUT, ReservationExplanation.DefaultDateFormatter);
            CommittedActionDialog.ShowBlocked(
                "Cannot decline \"" + (string.IsNullOrEmpty(title) ? keyString : title) + "\"",
                text.Body,
                "");
            return false;
        }
    }
}
