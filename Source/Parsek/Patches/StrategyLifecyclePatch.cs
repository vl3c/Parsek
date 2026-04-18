using System;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony postfixes on <see cref="Strategies.Strategy.Activate"/> and
    /// <see cref="Strategies.Strategy.Deactivate"/>. Both KSP methods return <c>true</c>
    /// on success; we filter on <c>__result</c> to skip the false/CanBeActivated-failed
    /// branch and only capture real lifecycle transitions.
    ///
    /// Fixes #439 Phase A: stock strategies flow through KSP's
    /// <c>GameEvents.Modifiers.OnCurrencyModifierQuery</c> interception (contract rewards
    /// are already transformed before <c>onContractCompleted</c> fires), so there is no
    /// discrete payout API to patch. Capturing activate/deactivate is enough to feed
    /// <see cref="StrategiesModule"/>'s slot-accounting and to reconcile the
    /// <c>FundsChanged(StrategySetup)</c> debit KSP fires inside <c>Activate()</c> when
    /// <c>InitialCostFunds</c> is non-zero. See
    /// <c>docs/dev/plans/fix-439-strategy-lifecycle-capture.md</c> section 3 for the full
    /// decompile trace.
    ///
    /// Both postfixes respect <see cref="GameStateRecorder.IsReplayingActions"/> so
    /// <see cref="KspStatePatcher"/>-driven recalculation replays do not re-emit events.
    /// Defensive try/catch mirrors <see cref="ProgressRewardPatch"/> — never rethrow into
    /// KSP's strategy pipeline.
    /// </summary>
    [HarmonyPatch(typeof(Strategies.Strategy), nameof(Strategies.Strategy.Activate))]
    internal static class StrategyActivatePatch
    {
        private const string Tag = "StrategyLifecyclePatch";

        internal static void Postfix(Strategies.Strategy __instance, bool __result)
        {
            try
            {
                if (!__result)
                {
                    ParsekLog.Verbose(Tag,
                        "Activate postfix: __result=false (CanBeActivated failed) - skipped");
                    return;
                }
                if (GameStateRecorder.IsReplayingActions)
                {
                    ParsekLog.Verbose(Tag,
                        "Activate postfix: suppressed during action replay");
                    return;
                }
                if (__instance == null)
                {
                    ParsekLog.Verbose(Tag,
                        "Activate postfix: __instance null - skipped");
                    return;
                }

                GameStateRecorder.OnStrategyActivated(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Activate postfix threw while capturing strategy lifecycle: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Strategies.Strategy), nameof(Strategies.Strategy.Deactivate))]
    internal static class StrategyDeactivatePatch
    {
        private const string Tag = "StrategyLifecyclePatch";

        internal static void Postfix(Strategies.Strategy __instance, bool __result)
        {
            try
            {
                if (!__result)
                {
                    ParsekLog.Verbose(Tag,
                        "Deactivate postfix: __result=false - skipped");
                    return;
                }
                if (GameStateRecorder.IsReplayingActions)
                {
                    ParsekLog.Verbose(Tag,
                        "Deactivate postfix: suppressed during action replay");
                    return;
                }
                if (__instance == null)
                {
                    ParsekLog.Verbose(Tag,
                        "Deactivate postfix: __instance null - skipped");
                    return;
                }

                GameStateRecorder.OnStrategyDeactivated(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Deactivate postfix threw while capturing strategy lifecycle: {ex.Message}");
            }
        }
    }
}
