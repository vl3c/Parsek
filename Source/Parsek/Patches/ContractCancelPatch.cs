using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Backstop prefix on the non-virtual <c>Contracts.Contract.Cancel()</c>: refuses to
    /// cancel an Active contract the committed timeline completes, fails or cancels later
    /// (owner ruling D7, cases X1-X3 of
    /// docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md section 7.3). It reads
    /// <see cref="StockUiReservationPredicates.CommittedContractResolutionAfter"/>, the SAME
    /// helper the Mission Control Active-row label and the greyed Cancel button read, so
    /// Cancel is refused exactly where the row is marked. Derived deadline expiry is not a
    /// committed row, so a contract the committed future leaves open stays cancellable (X4).
    /// <para>Callers of <c>Cancel()</c> in KSP 1.12.5 (whole-assembly IL scan for
    /// <c>call(virt) Contracts.Contract::Cancel()</c>): stock <c>MissionControl.OnClickCancel</c>
    /// (the player's Cancel button), <c>DebugToolbar.ContractsActive</c> and
    /// <c>ScreenContractExistingItem.OnRightButtonClicked</c> (the debug toolbar's per-contract
    /// cancel buttons), and <c>ContractSystem.RebuildContracts()</c>, which cancels every
    /// Active contract and then clears and regenerates the whole list (reached only from the
    /// debug toolbar's "regenerate" buttons, <c>DebugToolbar.ContractsTools</c> and
    /// <c>ScreenContractTools.OnRegenerateCurrentClicked</c>). The three direct callers are
    /// player actions and are refused. <c>RebuildContracts</c> is let through
    /// (<see cref="ContractSystemRebuildContractsScopePatch"/>): it drops every contract from
    /// the list right after the loop, so a refused Cancel there would not keep the contract,
    /// it would only lose it without its stock end state. Contract Configurator's
    /// <c>OnClickCancel</c> and kRPC's <c>Contract.Cancel</c> call it too and are refused
    /// the same way. Parsek itself never calls <c>Cancel()</c>: <c>KspStatePatcher.PatchContracts</c>
    /// writes contract state directly.</para>
    /// </summary>
    [HarmonyPatch]
    internal static class ContractCancelPatch
    {
        private const string Tag = "ContractCancelPatch";

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "Contracts.Contract.Cancel() not found - cancelling a contract the committed timeline resolves later will not be refused. " +
                    "Harmony will skip this patch (caught by ParsekHarmony try/catch).");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(Contracts.Contract), nameof(Contracts.Contract.Cancel), Type.EmptyTypes);
        }

        static bool Prefix(Contracts.Contract __instance)
        {
            if (ParsekGameModeGate.CheckInert("ContractCancelPatch.Prefix")) return true; // S9 game-mode gate
            if (__instance == null) return true;
            if (__instance.ContractState != Contracts.Contract.State.Active)
            {
                // Stock Cancel is a no-op for anything but an Active contract.
                ParsekLog.Verbose(Tag, "allowing cancel for guid=" + __instance.ContractGuid
                    + " - state=" + __instance.ContractState + " is not Active (stock no-op)");
                return true;
            }

            string key = __instance.ContractGuid.ToString();
            return ShouldAllowCancel(key, __instance.Title ?? key);
        }

        internal static bool ShouldAllowCancel(string keyString, string title)
        {
            if (string.IsNullOrEmpty(keyString)) return true;

            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.Verbose(Tag, "bypass - replay in progress (guid=" + keyString + ")");
                return true;
            }

            if (ContractSystemRebuildContractsScopePatch.InRebuildContracts)
            {
                ParsekLog.Info(Tag, "bypass - ContractSystem.RebuildContracts is regenerating every contract (guid="
                    + keyString + ")");
                return true;
            }

            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            var resolution = StockUiReservationPredicates.CommittedContractResolutionAfter(index, keyString, nowUT);
            if (resolution == null)
            {
                ParsekLog.Verbose(Tag,
                    "allowing cancel for guid=" + keyString + " - no committed resolution after now " +
                    "(nowUT=" + nowUT.ToString("F0", CultureInfo.InvariantCulture) + ")");
                return true;
            }

            ParsekLog.Info(Tag,
                "blocking cancel for guid=" + keyString + " - committed " + resolution.Kind + " " +
                "ut=" + resolution.UT.ToString("F0", CultureInfo.InvariantCulture) + " " +
                "nowUT=" + nowUT.ToString("F0", CultureInfo.InvariantCulture) + " " +
                "recording=" + (resolution.RecordingId ?? "(ksc)"));

            var text = StockUiReservationPredicates.ExplainContractCancel(
                index, keyString, nowUT, ReservationExplanation.DefaultDateFormatter);
            CommittedActionDialog.ShowBlocked(
                "Cannot cancel \"" + (string.IsNullOrEmpty(title) ? keyString : title) + "\"",
                text.Body,
                "");
            return false;
        }
    }

    /// <summary>
    /// Marks the scope of stock <c>ContractSystem.RebuildContracts()</c> so
    /// <see cref="ContractCancelPatch"/> lets its internal <c>Cancel()</c> calls through
    /// (see that class for why). The finalizer clears the mark even when the method throws.
    /// </summary>
    [HarmonyPatch]
    internal static class ContractSystemRebuildContractsScopePatch
    {
        private const string Tag = "ContractCancelPatch";

        /// <summary>Nesting depth of <c>RebuildContracts</c> calls in progress.</summary>
        private static int depth;

        internal static bool InRebuildContracts => depth > 0;

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "Contracts.ContractSystem.RebuildContracts() not found - a debug-toolbar contract regeneration may have its " +
                    "Cancel() calls refused for committed contracts. Harmony will skip this patch.");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return AccessTools.Method(typeof(Contracts.ContractSystem), "RebuildContracts", Type.EmptyTypes);
        }

        static void Prefix()
        {
            EnterScope();
            ParsekLog.Verbose(Tag, "ContractSystem.RebuildContracts began - Cancel() refusals bypassed for its scope");
        }

        static Exception Finalizer(Exception __exception)
        {
            ExitScope();
            ParsekLog.Verbose(Tag, "ContractSystem.RebuildContracts ended ("
                + (__exception == null ? "returned" : "threw") + ") - Cancel() refusals active again");
            return __exception;
        }

        internal static void EnterScope()
        {
            depth++;
        }

        internal static void ExitScope()
        {
            if (depth > 0) depth--;
        }

        internal static void ResetForTesting()
        {
            depth = 0;
        }
    }
}
