using System;
using System.Reflection;
using Contracts;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Contract twins stay out of the offer pool (design 3.15 step 8): a Harmony postfix on
    /// <c>ContractSystem.GenerateContract(int, ContractPrestige, Type)</c>, the one generator
    /// stock's offer loop (<c>GenerateContract(ref int, ContractPrestige)</c>) calls per seed.
    /// A contract that twins a committed future accept (<see cref="ContractTwinRule"/>: the
    /// same stock id, or the same once-per-career goal key) is failed and returned as null,
    /// exactly as stock fails a contract whose <c>ContractID</c> an offered or active
    /// contract already holds, so the loop tries the
    /// next seed. That covers an <c>AutoAccept</c> contract too: stock accepts only what the
    /// loop returns. <see cref="ContractAcceptPatch"/> backstops an auto-accept twin from any
    /// other generator, and the contract patch withdraws an Offered twin left from before the
    /// committed accept was known.
    /// </summary>
    [HarmonyPatch]
    internal static class ContractTwinGenerationPatch
    {
        static MethodBase TargetMethod()
        {
            MethodBase method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(ContractTwinRule.Tag,
                    "ContractSystem.GenerateContract(int, ContractPrestige, Type) not found - a twin of a committed " +
                    "contract can be offered after a rewind (the contract patch still withdraws it). " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(ContractSystem), nameof(ContractSystem.GenerateContract),
                new[] { typeof(int), typeof(Contract.ContractPrestige), typeof(Type) });
        }

        static void Postfix(ref Contract __result)
        {
            if (__result == null) return;
            if (ParsekGameModeGate.CheckInert("ContractTwinGenerationPatch.Postfix")) return; // S9 game-mode gate
            if (GameStateRecorder.IsReplayingActions) return;

            long stockId = 0;
            try { stockId = __result.ContractID; }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(ContractTwinRule.Tag, "generate-id",
                    $"Generation: reading ContractID threw ({ex.Message}) - judged on the goal key only");
            }
            // Owner ruling 2026-10-27: a guid-seeded type whose goal stock offers once per
            // career is also matched on its goal key, read only when a committed future accept
            // of the same type carries one.
            string goalKey = ContractGoalKeyReader.ReadIfJudged(__result, CommittedFutureIndexCache.Current,
                CommittedFutureIndexCache.CurrentUT(), "Generation");
            if (!ContractTwinRule.IsTwinNow(stockId, __result.ContractGuid.ToString(), __result.Title, "Generation",
                    rateLimitPerHolder: true, goalKey: goalKey))
                return;
            try
            {
                __result.GenerateFailed();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(ContractTwinRule.Tag,
                    $"Generation: GenerateFailed on twin '{__result.Title}' threw: {ex.Message} (refused anyway)");
            }
            __result = null;
        }
    }
}
