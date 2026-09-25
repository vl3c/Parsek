using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on Contracts.Contract.Accept to block accepting a contract a
    /// committed future row accepts (<see cref="CommittedFutureIndex"/>), or any other
    /// Offered contract when accepting it now would leave no Mission Control slot for a
    /// committed accept (<see cref="ContractSlotReservation"/>, C2). Both read
    /// <see cref="MissionControlStockAnnotation.Decide"/>, the decision the greyed Accept
    /// button and Contract Configurator's CanAccept postfix read (the pairing rule).
    /// Stock's other caller, <c>ContractSystem</c> generating an auto-accept contract,
    /// is never slot-checked: stock neither counts nor limits those.
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
                    "Contracts.Contract.Accept() not found - contract accept click-block will not apply. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");

            return method;
        }

        static bool Prefix(Contracts.Contract __instance)
        {
            if (__instance == null) return true;

            string keyString = __instance.ContractGuid.ToString();
            string title = __instance.Title ?? keyString;
            return ShouldAllowAccept(keyString, title, __instance.ContractState, __instance.AutoAccept, true,
                ContractSlotReservation.NewAcceptReleaseUT(__instance, CommittedFutureIndexCache.CurrentUT()));
        }

        /// <summary>The committed-accept block only (no slot model): an Offered contract.</summary>
        internal static bool ShouldAllowAccept(string keyString, string title)
        {
            return ShouldAllowAccept(keyString, title, Contracts.Contract.State.Offered, false, false);
        }

        /// <summary>
        /// The Accept backstop decision. <paramref name="useSlotModel"/> reads the live
        /// contract-slot forecast (<see cref="ContractSlotReservation.ForecastNow(CommittedFutureIndex, double)"/>,
        /// which tests replace through its provider seam). Refused exactly when
        /// <see cref="MissionControlStockAnnotation.BlocksAccept"/> holds for the decision.
        /// <paramref name="newContractReleaseUT"/> is when the contract, accepted now, gives its
        /// slot back by deadline (+inf for none).
        /// </summary>
        internal static bool ShouldAllowAccept(
            string keyString, string title, Contracts.Contract.State state, bool autoAccept, bool useSlotModel,
            double newContractReleaseUT = double.PositiveInfinity)
        {
            if (string.IsNullOrEmpty(keyString)) return true;

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose("ContractAcceptPatch",
                    "bypass - replay in progress");
                return true;
            }

            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            if (!StockUiReservationPredicates.IsContractAcceptBlocked(index, keyString, nowUT))
            {
                ContractSlotForecast slots = useSlotModel && !autoAccept
                    ? ContractSlotReservation.ForecastNow(index, nowUT)
                    : null;
                StockUiDecoration slotDecision = MissionControlStockAnnotation.Decide(
                    index, nowUT, keyString, state, ReservationExplanation.DefaultDateFormatter, slots, autoAccept,
                    newContractReleaseUT);
                if (MissionControlStockAnnotation.BlocksAcceptForSlot(slotDecision))
                {
                    ParsekLog.Info("ContractAcceptPatch",
                        $"blocking accept for guid={keyString} - the committed timeline needs every free contract slot " +
                        $"nowUT={nowUT.ToString("F0", CultureInfo.InvariantCulture)} " +
                        $"releaseUT={(double.IsPositiveInfinity(newContractReleaseUT) ? "none" : newContractReleaseUT.ToString("F0", CultureInfo.InvariantCulture))} " +
                        $"{slots.Describe()}");
                    CommittedActionDialog.ShowBlocked(
                        "Cannot accept \"" + (string.IsNullOrEmpty(title) ? keyString : title) + "\"",
                        slotDecision.Why,
                        "");
                    return false;
                }

                ParsekLog.Verbose("ContractAcceptPatch",
                    $"allowing accept for guid={keyString} - no committed future accept " +
                    $"(nowUT={nowUT.ToString("F0", CultureInfo.InvariantCulture)}" +
                    (slots != null ? " " + slots.Describe() : " slots=not-modeled") + ")");
                return true;
            }

            var entry = index.FirstFuture(CommittedFutureKind.ContractAccept, keyString, nowUT);
            ParsekLog.Info("ContractAcceptPatch",
                $"blocking accept for guid={keyString} - committed future accept " +
                $"ut={entry.UT.ToString("F0", CultureInfo.InvariantCulture)} " +
                $"nowUT={nowUT.ToString("F0", CultureInfo.InvariantCulture)} " +
                $"recording={entry.RecordingId ?? "(ksc)"}");

            var text = StockUiReservationPredicates.ExplainContractAccept(
                index, keyString, nowUT, ReservationExplanation.DefaultDateFormatter);
            CommittedActionDialog.ShowBlocked(
                "Cannot accept \"" + (string.IsNullOrEmpty(title) ? keyString : title) + "\"",
                text.Body,
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
            return ContractAcceptPatch.ShouldAllowAccept(keyString, title, contract.ContractState, contract.AutoAccept, true,
                ContractSlotReservation.NewAcceptReleaseUT(contract, CommittedFutureIndexCache.CurrentUT()));
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
