using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on Contracts.Contract.Accept to block accepting contracts
    /// that are already committed in unreplayed milestones.
    /// </summary>
    [HarmonyPatch]
    internal static class ContractAcceptPatch
    {
        static MethodBase TargetMethod()
        {
            var method = AccessTools.Method(
                typeof(Contracts.Contract),
                nameof(Contracts.Contract.Accept),
                Type.EmptyTypes);

            if (method == null)
                ParsekLog.Warn("ContractAcceptPatch",
                    "Contracts.Contract.Accept() not found — contract accept click-block will not apply. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");

            return method;
        }

        static bool Prefix(Contracts.Contract __instance)
        {
            if (__instance == null) return true;

            string keyString = __instance.ContractGuid.ToString();
            string title = __instance.Title ?? keyString;
            return ShouldAllowAccept(keyString, title);
        }

        internal static bool ShouldAllowAccept(string keyString, string title)
        {
            if (string.IsNullOrEmpty(keyString)) return true;

            if (!(ParsekSettings.Current?.blockCommittedActions ?? true))
            {
                ParsekLog.Verbose("ContractAcceptPatch",
                    "feature disabled by ParsekSettings");
                return true;
            }

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose("ContractAcceptPatch",
                    "bypass — replay in progress");
                return true;
            }

            var committedContracts = MilestoneStore.GetCommittedContractAcceptIds();
            if (!committedContracts.Contains(keyString))
                return true;

            var ev = MilestoneStore.FindCommittedEvent(
                GameStateEventType.ContractAccepted, keyString);

            string utValue = ev.HasValue
                ? ev.Value.ut.ToString("F0", CultureInfo.InvariantCulture)
                : "unknown";
            string utStr = ev.HasValue ? " at UT " + utValue : "";

            ParsekLog.Info("ContractAcceptPatch",
                $"blocking accept for guid={keyString} — committed at UT {utValue}");

            CommittedActionDialog.ShowBlocked(
                "Cannot accept \"" + (string.IsNullOrEmpty(title) ? keyString : title) + "\"",
                "This contract is already committed on your timeline" + utStr + ".",
                "");

            return false;
        }
    }
}
