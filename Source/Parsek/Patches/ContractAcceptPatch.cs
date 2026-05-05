using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;

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

    /// <summary>
    /// Stock Mission Control's button handler ignores Contract.Accept()'s return value,
    /// so block before the stock UI clears and rebuilds the selected mission panel.
    /// </summary>
    [HarmonyPatch]
    internal static class MissionControlAcceptPatch
    {
        private static readonly FieldInfo SelectedMissionField =
            typeof(MissionControl).GetField(
                "selectedMission",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo MissionSelectionContractField =
            typeof(MissionControl.MissionSelection).GetField(
                "contract",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static bool contractLookupWarned;

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("ContractAcceptPatch",
                    "MissionControl.OnClickAccept() not found - stock Mission Control accept pre-block will not apply. " +
                    "Contracts.Contract.Accept() backup patch remains active.");

            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return typeof(MissionControl).GetMethod(
                "OnClickAccept",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
        }

        static bool Prefix(MissionControl __instance)
        {
            Contracts.Contract contract;
            if (!TryGetSelectedContract(__instance, out contract))
                return true;

            string keyString = contract.ContractGuid.ToString();
            string title = contract.Title ?? keyString;
            return ContractAcceptPatch.ShouldAllowAccept(keyString, title);
        }

        private static bool TryGetSelectedContract(
            MissionControl missionControl,
            out Contracts.Contract contract)
        {
            contract = null;
            if (missionControl == null)
            {
                ParsekLog.Verbose("ContractAcceptPatch",
                    "MissionControl.OnClickAccept pre-block bypass - MissionControl instance was null");
                return false;
            }

            if (SelectedMissionField == null || MissionSelectionContractField == null)
            {
                LogContractLookupWarning(
                    "MissionControl.OnClickAccept pre-block cannot inspect selectedMission.contract; " +
                    "stock UI pre-block disabled for this session");
                return false;
            }

            try
            {
                var selectedMission = SelectedMissionField.GetValue(missionControl);
                if (selectedMission == null)
                {
                    ParsekLog.Verbose("ContractAcceptPatch",
                        "MissionControl.OnClickAccept pre-block bypass - no selected mission");
                    return false;
                }

                contract = MissionSelectionContractField.GetValue(selectedMission) as Contracts.Contract;
                if (contract == null)
                {
                    LogContractLookupWarning(
                        "MissionControl.OnClickAccept pre-block found selected mission without a contract; " +
                        "stock UI pre-block skipped");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogContractLookupWarning(
                    "MissionControl.OnClickAccept pre-block contract lookup failed; stock UI pre-block skipped (" +
                    ex.Message + ")");
                return false;
            }

            return true;
        }

        private static void LogContractLookupWarning(string message)
        {
            if (contractLookupWarned)
                return;

            contractLookupWarned = true;
            ParsekLog.Warn("ContractAcceptPatch", message);
        }
    }
}
