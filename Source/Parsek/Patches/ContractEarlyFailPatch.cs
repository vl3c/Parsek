using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Prefix on the protected <c>Contracts.Contract.SetState(State)</c>: refuses to end an
    /// Active contract whose outcome the committed timeline already holds (owner ruling
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
    /// runs <c>PenalizeFailure()</c>; <c>Update()</c> reaches <c>SetState(State.DeadlineExpired)</c>
    /// (also <c>PenalizeFailure()</c>) once the deadline passes, and <c>SetState(State.OfferExpired)</c>
    /// (no penalty) when an Active contract's requirements no longer hold. Refusing leaves the
    /// contract Active, and the state patch applies the committed outcome at the next
    /// recalculation past its UT.</para>
    /// <para>The refusal covers a committed outcome at ANY UT, not only one ahead of the clock
    /// (todo CONTRACT-FAIL-AFTER-REACHED-COMMITTED-OUTCOME). The flight scene has no clock
    /// cursor, so once a flight passes the committed UT stock still shows the contract Active
    /// until the next recalculation; a fail there would charge a penalty the walk then skips
    /// (the contract already ended), and the stock pool reference files that gap as a kept
    /// leak, a permanent loss. An Active contract with a committed outcome at or before now is
    /// exactly that stale state, counted from the live contract's own accept date on so an
    /// earlier lifecycle's resolution of a re-accepted contract does not hold the new one. A deadline expiry is refused on the same terms: a contract
    /// with no committed outcome expires as stock decides (the committed timeline holds the
    /// same expiry, design 3.13), and one whose outcome is committed cannot expire past it.</para>
    /// <para><c>Fail()</c> and <c>Update()</c> set <c>dateFinished</c> before <c>SetState</c>, so
    /// a refused contract keeps that date until its committed outcome; nothing reads it while it
    /// is Active. Stock's <c>Fail()</c> ignores <c>SetState</c>'s outcome and returns true when
    /// refused. A refusal from <c>Update()</c> repeats every frame, so its log line is
    /// rate-limited. Parsek itself never calls <c>SetState</c>
    /// (<c>KspStatePatcher.PatchContracts</c> writes contract state directly). It reads
    /// <see cref="StockUiReservationPredicates.CommittedContractResolution"/>, the any-UT form of
    /// the predicate the Cancel block (ruling D7) reads, so a contract whose Cancel is greyed
    /// cannot fail in flight either.</para>
    /// <para>The same prefix opens the recorder's clock-cause scope around a transition the
    /// clock causes: a deadline or offer expiry, and an auto-accept contract's accept
    /// (<see cref="IsClockCausedAccept"/>); everything stock does inside it is filed under no
    /// flight.</para>
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

        static bool Prefix(Contracts.Contract __instance, Contracts.Contract.State newState, out bool __state)
        {
            __state = false;
            if (__instance != null && IsClockCausedAccept(newState, __instance.ContractState, __instance.AutoAccept))
            {
                if (ParsekGameModeGate.CheckInert("ContractEarlyFailPatch.Prefix")) return true; // S9 game-mode gate
                GameStateRecorder.EnterClockCauseScope("auto-accept of contract '"
                    + (__instance.Title ?? __instance.ContractGuid.ToString()) + "'");
                __state = true;
                return true;
            }
            if (!IsEarlyEndState(newState)) return true;
            if (ParsekGameModeGate.CheckInert("ContractEarlyFailPatch.Prefix")) return true; // S9 game-mode gate
            if (__instance == null) return true;
            bool allow = __instance.ContractState != Contracts.Contract.State.Active
                || ShouldAllowFail(__instance.ContractGuid.ToString(),
                    __instance.Title ?? __instance.ContractGuid.ToString(), newState.ToString(),
                    __instance.DateAccepted);
            if (allow && IsClockCausedEndState(newState))
            {
                GameStateRecorder.EnterClockCauseScope("Contract.SetState(" + newState + ")");
                __state = true;
            }
            return allow;
        }

        static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state) GameStateRecorder.ExitClockCauseScope("Contract.SetState");
            return __exception;
        }

        /// <summary>
        /// The end states the clock causes, whatever a flight does: a deadline running out,
        /// and an offer (or an Active contract's requirements) expiring. Everything stock
        /// does inside such a <c>SetState</c> (the penalty, its strategy conversions, the
        /// contract's own expiry code) is filed under no flight (owner ruling 2026-10-24).
        /// </summary>
        internal static bool IsClockCausedEndState(Contracts.Contract.State newState)
        {
            return newState == Contracts.Contract.State.DeadlineExpired
                || newState == Contracts.Contract.State.OfferExpired;
        }

        /// <summary>
        /// True for an auto-accept contract's accept (todo
        /// LEDGER-EVENTS-OF-MOD-BACKGROUND-SOURCES-TAGGED-TO-THE-FLIGHT): stock's
        /// <c>ContractSystem</c> accepts an <c>AutoAccept</c> contract itself right after it
        /// offers it, on its own refresh (Contract Configurator contracts can carry the flag),
        /// so no flight causes the accept or its advance. A player's accept of an ordinary
        /// offer stays as it is.
        /// </summary>
        internal static bool IsClockCausedAccept(Contracts.Contract.State newState,
            Contracts.Contract.State currentState, bool autoAccept)
        {
            return autoAccept
                && newState == Contracts.Contract.State.Active
                && currentState == Contracts.Contract.State.Offered;
        }

        /// <summary>
        /// The end states of an Active contract that would contradict a committed outcome:
        /// <c>Failed</c> and <c>DeadlineExpired</c> (both charge the fail penalty) and
        /// <c>OfferExpired</c>, which stock's <c>Update()</c> sets on an Active contract whose
        /// requirements no longer hold (no penalty, but the contract leaves the active list
        /// before or instead of its committed outcome).
        /// </summary>
        internal static bool IsEarlyEndState(Contracts.Contract.State newState)
        {
            return newState == Contracts.Contract.State.Failed
                || newState == Contracts.Contract.State.DeadlineExpired
                || newState == Contracts.Contract.State.OfferExpired;
        }

        /// <summary>Slack below the live accept date for a reached outcome's UT (the accept
        /// date and the row UT are separate reads of the clock).</summary>
        internal const double AcceptDateToleranceSeconds = 1.0;

        /// <summary>
        /// The decision for a fail of an Active contract: false (refuse) while the committed
        /// timeline completes, fails or cancels it, ahead of now or already reached. A reached
        /// outcome is one the next recalculation has yet to apply to stock's live state; it
        /// counts only from the live contract's own accept date on (<paramref name="acceptedUT"/>,
        /// stock's <c>DateAccepted</c>; NaN or not positive: no bound), so a resolution that ended
        /// an EARLIER lifecycle of a re-accepted contract never freezes the new one. A contract
        /// a rewind restored keeps its original accept date, so its reached outcome still holds.
        /// </summary>
        internal static bool ShouldAllowFail(string keyString, string title, string endState = "Failed",
            double acceptedUT = double.NaN)
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
            bool reached = false;
            if (resolution == null)
            {
                resolution = HasAcceptBound(acceptedUT)
                    ? StockUiReservationPredicates.CommittedContractResolutionAfter(
                        index, keyString, acceptedUT - AcceptDateToleranceSeconds)
                    : StockUiReservationPredicates.CommittedContractResolution(index, keyString);
                reached = resolution != null;
            }
            if (resolution == null)
            {
                ParsekLog.Verbose(Tag,
                    "allowing fail for guid=" + keyString + " end=" + endState + " - no committed outcome " +
                    "(nowUT=" + nowUT.ToString("F0", CultureInfo.InvariantCulture) +
                    (HasAcceptBound(acceptedUT)
                        ? " acceptedUT=" + acceptedUT.ToString("F0", CultureInfo.InvariantCulture)
                        : "") + ")");
                return true;
            }

            RefusedFailCount++;
            string titleText = string.IsNullOrEmpty(title) ? keyString : title;
            string recording = resolution.RecordingId ?? "(ksc)";
            if (reached)
            {
                ParsekLog.InfoRateLimited(Tag, "refuse-reached|" + endState + "|" + keyString,
                    "refusing fail of \"" + titleText + "\" guid=" + keyString +
                    " end=" + endState + " - committed " + resolution.Kind + " ut=" + resolution.UT.ToString("F0", CultureInfo.InvariantCulture) +
                    " already reached nowUT=" + nowUT.ToString("F0", CultureInfo.InvariantCulture) +
                    " recording=" + recording + "; the contract stays Active until the next career update applies it");
            }
            else
            {
                ParsekLog.InfoRateLimited(Tag, "refuse|" + endState + "|" + keyString,
                    "refusing fail of \"" + titleText + "\" guid=" + keyString +
                    " end=" + endState + " - committed " + resolution.Kind + " ut=" + resolution.UT.ToString("F0", CultureInfo.InvariantCulture) +
                    " nowUT=" + nowUT.ToString("F0", CultureInfo.InvariantCulture) +
                    " recording=" + recording + "; the contract stays Active until then");
            }
            return false;
        }

        private static bool HasAcceptBound(double acceptedUT)
        {
            return !double.IsNaN(acceptedUT) && !double.IsInfinity(acceptedUT) && acceptedUT > 0.0;
        }

        internal static void ResetForTesting()
        {
            RefusedFailCount = 0;
        }
    }
}
