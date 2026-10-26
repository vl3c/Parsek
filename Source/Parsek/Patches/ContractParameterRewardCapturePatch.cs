using System;
using System.Collections.Generic;
using System.Reflection;
using Contracts;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// What one contract parameter's <c>AwardCompletion</c> paid, read before stock zeroes it.
    /// </summary>
    internal struct ContractParameterRewardPaid
    {
        internal string ContractGuid;
        internal string ContractTitle;
        internal string ParameterId;
        internal string ParameterStockId;
        internal float FundsReward;
        internal float RepReward;
        internal float SciReward;
        internal bool Captured;
    }

    /// <summary>
    /// Prefix + Postfix on the protected virtual <c>Contracts.ContractParameter.AwardCompletion()</c>
    /// (todo CONTRACT-PARAMETER-REWARDS-NOT-LEDGERED). Stock pays each parameter's own
    /// completion reward there (KSP 1.12.5, decompiled): <c>Funding.AddFunds(FundsCompletion)</c>
    /// when it is nonzero, <c>AddScience(ScienceCompletion)</c> and
    /// <c>AddReputation(ReputationCompletion)</c>, all with reason <c>ContractReward</c>, and it
    /// ZEROES each of the three fields after paying it. Its only caller is
    /// <c>ContractParameter.SetComplete</c>, which runs it only on a not-yet-Complete parameter
    /// and fires <c>GameEvents.Contract.onParameterChange</c> AFTER it, so a handler on that
    /// event always reads zero rewards; the prefix is the one point the paid amounts are still
    /// visible. No stock subclass overrides <c>AwardCompletion</c>. The prefix records the three
    /// fields and the parameter's identity; the postfix (after stock paid) keeps only the legs
    /// stock actually paid (<see cref="PaidLeg"/>) and raises <see cref="RewardPaid"/>, which the
    /// subscribed <see cref="GameStateRecorder"/> files as a ContractParameterCompleted event.
    /// </summary>
    [HarmonyPatch]
    internal static class ContractParameterRewardCapturePatch
    {
        private const string Tag = "ContractParameterRewardCapture";

        /// <summary>Raised after stock paid one parameter's completion reward.</summary>
        internal static event Action<ContractParameterRewardPaid> RewardPaid;

        static MethodBase TargetMethod()
        {
            var method = AccessTools.Method(typeof(ContractParameter), "AwardCompletion", Type.EmptyTypes);
            if (method == null)
                ParsekLog.Warn(Tag,
                    "Contracts.ContractParameter.AwardCompletion() not found - a contract parameter's own reward will not be ledgered. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");
            return method;
        }

        static void Prefix(ContractParameter __instance, out ContractParameterRewardPaid __state)
        {
            __state = default(ContractParameterRewardPaid);
            if (ParsekGameModeGate.CheckInert("ContractParameterRewardCapturePatch.Prefix")) return; // S9 game-mode gate
            if (__instance == null) return;
            try
            {
                Contract root = __instance.Root;
                __state = new ContractParameterRewardPaid
                {
                    ContractGuid = root != null ? root.ContractGuid.ToString() : null,
                    ContractTitle = root != null ? (root.Title ?? "") : "",
                    ParameterId = ResolveParameterId(__instance),
                    ParameterStockId = __instance.ID,
                    FundsReward = (float)__instance.FundsCompletion,
                    RepReward = __instance.ReputationCompletion,
                    SciReward = __instance.ScienceCompletion,
                    Captured = true
                };
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Prefix could not read the parameter's reward ({ex.GetType().Name}: {ex.Message}) - not captured");
                __state = default(ContractParameterRewardPaid);
            }
        }

        static void Postfix(ContractParameterRewardPaid __state, ContractParameter __instance)
        {
            if (!__state.Captured || __instance == null) return;
            float funds = PaidLeg(__state.FundsReward, (float)__instance.FundsCompletion);
            float sci = PaidLeg(__state.SciReward, __instance.ScienceCompletion);
            float rep = PaidLeg(__state.RepReward, __instance.ReputationCompletion);
            if (funds != __state.FundsReward || sci != __state.SciReward || rep != __state.RepReward)
            {
                ParsekLog.Info(Tag,
                    $"Stock skipped a reward leg of contract {__state.ContractGuid ?? "(null)"} param={__state.ParameterId ?? "(null)"} " +
                    $"(a strategy cancelled it): funds {__state.FundsReward.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}->{funds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, " +
                    $"sci {__state.SciReward.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}->{sci.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, " +
                    $"rep {__state.RepReward.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}->{rep.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
            }
            __state.FundsReward = funds;
            __state.SciReward = sci;
            __state.RepReward = rep;
            Report(__state);
        }

        /// <summary>
        /// The amount one reward leg actually paid. Stock's <c>AwardCompletion</c> pays a leg and
        /// then zeroes its field; it skips the science or reputation leg when the value plus the
        /// strategy query's effect delta is zero, and then leaves the field as it was (decompiled
        /// KSP 1.12.5). So a field still nonzero after the call is a leg stock did not pay.
        /// </summary>
        internal static float PaidLeg(float before, float after)
        {
            return after == 0f ? before : 0f;
        }

        /// <summary>
        /// The parameter's index path inside its contract (see
        /// <see cref="GameStateRecorder.BuildContractParameterIndexPath"/>), or, when the walk to
        /// the contract fails, <c>"id:" + ID</c>; null when neither resolves.
        /// </summary>
        private static string ResolveParameterId(ContractParameter parameter)
        {
            var indicesLeafFirst = new List<int>();
            ContractParameter current = parameter;
            int depth = 0;
            bool resolved = false;
            while (current != null && depth++ < 32)
            {
                IContractParameterHost host = current.Parent;
                if (host == null)
                    break;
                int index = -1;
                int count = host.ParameterCount;
                for (int i = 0; i < count; i++)
                {
                    if (ReferenceEquals(host.GetParameter(i), current))
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0)
                    break;
                indicesLeafFirst.Add(index);
                current = host as ContractParameter;
                if (current == null)
                {
                    resolved = host is Contract;
                    break;
                }
            }

            if (resolved)
            {
                indicesLeafFirst.Reverse();
                return GameStateRecorder.BuildContractParameterIndexPath(indicesLeafFirst);
            }

            string stockId = parameter.ID;
            ParsekLog.Warn(Tag,
                $"Could not resolve the index path of parameter '{stockId ?? "(null)"}' in its contract - " +
                "falling back to its stock ID");
            return string.IsNullOrEmpty(stockId) ? null : "id:" + stockId;
        }

        /// <summary>Raises <see cref="RewardPaid"/> (called by the postfix).</summary>
        internal static void Report(ContractParameterRewardPaid paid)
        {
            var handler = RewardPaid;
            if (handler == null)
            {
                ParsekLog.Verbose(Tag,
                    "Contract parameter reward paid with no subscribed recorder - not captured (contract=" +
                    (paid.ContractGuid ?? "(null)") + " param=" + (paid.ParameterId ?? "(null)") + ")");
                return;
            }
            try
            {
                handler(paid);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"RewardPaid handler threw ({ex.GetType().Name}: {ex.Message})");
            }
        }

        internal static void ResetForTesting()
        {
            RewardPaid = null;
        }
    }
}
