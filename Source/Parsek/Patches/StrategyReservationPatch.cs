using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSP.UI.Screens;

namespace Parsek.Patches
{
    /// <summary>
    /// Live glue between the stock Administration building and
    /// <see cref="StrategyReservationPredicates"/>: reads the stock active set and slot
    /// limit, evaluates the predicate over the one <see cref="CommittedFutureIndex"/>, and
    /// logs. The Harmony patches below call only this, so the reason stock prints and the
    /// click the backstop refuses come from the same decision.
    /// </summary>
    internal static class StrategyReservationGate
    {
        private const string Tag = "StrategyReservation";

        /// <summary>Test seams for the live stock reads. Null in production.</summary>
        internal static Func<IEnumerable<string>> ActiveStrategyIdsProviderForTesting;
        internal static Func<int> SlotLimitProviderForTesting;
        internal static Func<string, string> StrategyTitleProviderForTesting;
        internal static Func<IReadOnlyList<StrategyTagInfo>> StockStrategiesProviderForTesting;

        /// <summary>
        /// The activation refusal for one strategy, or false when activating it is allowed
        /// (or a replay / state patch is running).
        /// </summary>
        internal static bool TryRefuseActivation(string strategyId, out ReservationText text)
        {
            text = new ReservationText();
            if (string.IsNullOrEmpty(strategyId)) return false;
            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.VerboseRateLimited(Tag, "activation-replay-bypass",
                    "activation check bypassed - replay in progress");
                return false;
            }

            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            var decision = StrategyReservationPredicates.EvaluateActivation(
                index, strategyId, nowUT, ActiveStrategyIds(), SlotLimit(),
                stockStrategies: StockStrategies());
            if (!decision.Blocked) return false;

            text = StrategyReservationPredicates.ExplainActivation(
                decision, StrategyTitle, ReservationExplanation.DefaultDateFormatter);
            ParsekLog.VerboseRateLimited(Tag, "activation-refused|" + strategyId,
                "activation refused strategy=" + strategyId + " "
                + StrategyReservationPredicates.DescribeActivation(decision)
                + " nowUT=" + nowUT.ToString("F0", CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>The player-path deactivation refusal, or false when allowed.</summary>
        internal static bool TryRefuseDeactivation(string strategyId, out ReservationText text)
        {
            text = new ReservationText();
            if (string.IsNullOrEmpty(strategyId)) return false;
            if (GameStateRecorder.IsReplayingActions)
            {
                ParsekLog.VerboseRateLimited(Tag, "deactivation-replay-bypass",
                    "deactivation check bypassed - replay in progress");
                return false;
            }

            var index = CommittedFutureIndexCache.Current;
            double nowUT = CommittedFutureIndexCache.CurrentUT();
            var next = StrategyReservationPredicates.FirstFutureRow(index, strategyId, nowUT);
            if (next == null) return false;

            text = ReservationExplanation.StrategyDeactivation(next, ReservationExplanation.DefaultDateFormatter);
            ParsekLog.VerboseRateLimited(Tag, "deactivation-refused|" + strategyId,
                "deactivation refused strategy=" + strategyId
                + " committedRow=" + next.Kind
                + " committedUT=" + next.UT.ToString("F0", CultureInfo.InvariantCulture)
                + " nowUT=" + nowUT.ToString("F0", CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>The Administration Accept backstop: refuses with the committed-action
        /// dialog. True = let stock proceed.</summary>
        internal static bool ShouldAllowActivationClick(string strategyId, string title)
        {
            ReservationText text;
            if (!TryRefuseActivation(strategyId, out text)) return true;
            ParsekLog.Info(Tag, "blocking activation click for strategy=" + strategyId);
            CommittedActionDialog.ShowBlocked(
                "Cannot activate \"" + (string.IsNullOrEmpty(title) ? strategyId : title) + "\"",
                text.Body, "");
            return false;
        }

        /// <summary>The Administration Cancel backstop. True = let stock proceed.</summary>
        internal static bool ShouldAllowDeactivationClick(string strategyId, string title)
        {
            ReservationText text;
            if (!TryRefuseDeactivation(strategyId, out text)) return true;
            ParsekLog.Info(Tag, "blocking deactivation click for strategy=" + strategyId);
            CommittedActionDialog.ShowBlocked(
                "Cannot deactivate \"" + (string.IsNullOrEmpty(title) ? strategyId : title) + "\"",
                text.Body, "");
            return false;
        }

        private static IEnumerable<string> ActiveStrategyIds()
        {
            var seam = ActiveStrategyIdsProviderForTesting;
            if (seam != null) return seam();
            try
            {
                return ReadLiveActiveStrategyIds();
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "active-ids-unavailable",
                    "stock active strategies unreadable, slot check sees none (" + ex.GetType().Name + ")");
                return new List<string>();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<string> ReadLiveActiveStrategyIds()
        {
            var result = new List<string>();
            var system = Strategies.StrategySystem.Instance;
            if (system == null || system.Strategies == null) return result;
            for (int i = 0; i < system.Strategies.Count; i++)
            {
                var s = system.Strategies[i];
                if (s != null && s.IsActive && s.Config != null && !string.IsNullOrEmpty(s.Config.Name))
                    result.Add(s.Config.Name);
            }
            return result;
        }

        /// <summary>Stock's strategies in <c>StrategySystem.Strategies</c> order with their
        /// group tags, for the conflict rule; null when unreadable (the branch is skipped).</summary>
        internal static IReadOnlyList<StrategyTagInfo> StockStrategies()
        {
            var seam = StockStrategiesProviderForTesting;
            if (seam != null) return seam();
            try
            {
                return ReadLiveStockStrategies();
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "stock-strategies-unavailable",
                    "stock strategy list unreadable, conflict check skipped (" + ex.GetType().Name + ")");
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<StrategyTagInfo> ReadLiveStockStrategies()
        {
            var system = Strategies.StrategySystem.Instance;
            if (system == null || system.Strategies == null) return null;
            var result = new List<StrategyTagInfo>(system.Strategies.Count);
            for (int i = 0; i < system.Strategies.Count; i++)
            {
                var s = system.Strategies[i];
                result.Add(new StrategyTagInfo(s?.Config?.Name, s?.Config != null ? s.GroupTags : null));
            }
            return result;
        }

        private static int SlotLimit()
        {
            var seam = SlotLimitProviderForTesting;
            if (seam != null) return seam();
            try
            {
                return ReadLiveSlotLimit();
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "slot-limit-unavailable",
                    "stock strategy slot limit unreadable, using 1 (" + ex.GetType().Name + ")");
                return 1;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadLiveSlotLimit()
        {
            var admin = Administration.Instance;
            if (admin != null) return admin.MaxActiveStrategies;
            return GameVariables.Instance.GetActiveStrategyLimit(
                ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.Administration));
        }

        private static string StrategyTitle(string strategyId)
        {
            var seam = StrategyTitleProviderForTesting;
            if (seam != null) return seam(strategyId);
            try
            {
                return ReadLiveStrategyTitle(strategyId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadLiveStrategyTitle(string strategyId)
        {
            var system = Strategies.StrategySystem.Instance;
            if (system == null || system.Strategies == null) return null;
            for (int i = 0; i < system.Strategies.Count; i++)
            {
                var s = system.Strategies[i];
                if (s?.Config != null && s.Config.Name == strategyId) return s.Title;
            }
            return null;
        }

        internal static void ResetForTesting()
        {
            ActiveStrategyIdsProviderForTesting = null;
            SlotLimitProviderForTesting = null;
            StrategyTitleProviderForTesting = null;
            StockStrategiesProviderForTesting = null;
        }
    }

    /// <summary>
    /// Live glue for <see cref="StrategyUpdateExpiryPatch"/>: applies a committed strategy
    /// deactivation before stock's per-frame <c>Strategy.Update()</c> can replay the expiry
    /// (todo STRATEGY-EXPIRY-REPLAY-DUPLICATE-DEACTIVATE-ROW, option c; decision in
    /// <see cref="StrategyReservationPredicates.DecideStockUpdate"/>).
    ///
    /// <para><b>Cost.</b> Runs once per frame per ACTIVE strategy (stock's
    /// <c>StrategySystem.Update</c> only calls <c>Update()</c> on active ones). Fast exits
    /// first (inactive, replay / suppression, not Career, no id); then one read of the
    /// cached <see cref="CommittedFutureIndexCache.Current"/> (version checks, no rebuild
    /// unless the ledger changed) and its per-strategy rows
    /// (<see cref="CommittedFutureIndex.StrategyRows"/>, built once per strategy per index
    /// instance, read by binary search). A strategy the committed timeline never activated leaves before the
    /// duration getters are read. Nothing on the per-frame path walks the ledger, and only
    /// the apply (once per strategy) and the start of a hold log at Info.</para>
    /// </summary>
    internal static class StrategyExpiryGate
    {
        private const string Tag = "StrategyReservation";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private static readonly MethodInfo OnUpdateMethod = typeof(Strategies.Strategy).GetMethod(
            "OnUpdate", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        private static readonly Dictionary<string, double> holdLogged =
            new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Test seams. Null in production.</summary>
        internal static Func<bool> IsCareerProviderForTesting;
        internal static Action AdministrationRefreshForTesting;

        /// <summary>
        /// The prefix body. True lets stock's <c>Update()</c> run; false skips it (the
        /// committed deactivation was applied, or the expiry is held until its UT).
        /// </summary>
        internal static bool ShouldRunStockUpdate(Strategies.Strategy strategy)
        {
            if (strategy == null || !strategy.IsActive) return true;
            if (GameStateRecorder.IsReplayingActions || GameStateRecorder.SuppressResourceEvents)
            {
                ParsekLog.VerboseRateLimited(Tag, "expiry-bypass",
                    "Strategy.Update prefix bypassed - replay or suppression in progress");
                return true;
            }
            if (!IsCareer()) return true;
            string id = strategy.Config?.Name;
            if (string.IsNullOrEmpty(id)) return true;

            var rows = CommittedFutureIndexCache.Current.StrategyRows(id);
            if (rows == null || !rows.HasActivation) return true;

            double now = CommittedFutureIndexCache.CurrentUT();
            double dateActivated = strategy.DateActivated;
            double longest = strategy.LongestDuration;
            bool expiryDue = longest != 0.0 && dateActivated + longest <= now;
            var decision = StrategyReservationPredicates.DecideStockUpdate(rows, true, dateActivated, expiryDue, now);

            switch (decision.Action)
            {
                case StrategyStockUpdateAction.ApplyCommittedDeactivation:
                    return !ApplyCommittedDeactivation(strategy, id, decision, dateActivated, expiryDue, now);
                case StrategyStockUpdateAction.HoldExpiryUntilCommittedDeactivation:
                    HoldExpiry(strategy, id, decision, dateActivated, longest, now);
                    return false;
                default:
                    if (expiryDue)
                    {
                        ParsekLog.VerboseRateLimited(Tag, "expiry-stock|" + id,
                            "Strategy.Update: stock expiry of strategy=" + id + " proceeds (" + decision.Reason
                            + ") dateActivated=" + dateActivated.ToString("F0", IC)
                            + " longestDuration=" + longest.ToString("F0", IC)
                            + " nowUT=" + now.ToString("F0", IC));
                    }
                    return true;
            }
        }

        private static bool ApplyCommittedDeactivation(
            Strategies.Strategy strategy, string id, StrategyStockUpdateDecision decision,
            double dateActivated, bool expiryDue, double now)
        {
            bool applied;
            using (SuppressionGuard.ResourcesAndReplay())
                applied = StrategyStatePatcher.SwitchOffWithoutCapture(strategy, "Strategy.Update prefix");
            holdLogged.Remove(id);
            if (!applied)
            {
                ParsekLog.WarnRateLimited(Tag, "expiry-apply-failed|" + id,
                    "Strategy.Update: committed deactivation of strategy=" + id
                    + " could not be applied, stock's Update runs");
                return false;
            }
            ParsekLog.Info(Tag,
                "Strategy.Update: applied the committed deactivation of strategy=" + id
                + " before stock's Update (no ledger row): committedUT=" + decision.CommittedUT.ToString("F0", IC)
                + " nowUT=" + now.ToString("F0", IC)
                + " dateActivated=" + dateActivated.ToString("F0", IC)
                + " stockExpiryDue=" + (expiryDue ? "true" : "false"));
            RefreshAdministration();
            return true;
        }

        private static void HoldExpiry(
            Strategies.Strategy strategy, string id, StrategyStockUpdateDecision decision,
            double dateActivated, double longest, double now)
        {
            double loggedUT;
            if (!holdLogged.TryGetValue(id, out loggedUT) || loggedUT != decision.CommittedUT)
            {
                holdLogged[id] = decision.CommittedUT;
                ParsekLog.Info(Tag,
                    "Strategy.Update: holding stock's expiry of strategy=" + id
                    + " until its committed deactivation: committedUT=" + decision.CommittedUT.ToString("F0", IC)
                    + " nowUT=" + now.ToString("F0", IC)
                    + " stockThresholdUT=" + (dateActivated + longest).ToString("F0", IC));
            }
            else
            {
                ParsekLog.VerboseRateLimited(Tag, "expiry-hold|" + id,
                    "Strategy.Update: expiry of strategy=" + id + " still held, committedUT="
                    + decision.CommittedUT.ToString("F0", IC) + " nowUT=" + now.ToString("F0", IC));
            }
            RunUpdateBodyWithoutExpiry(strategy);
        }

        /// <summary>
        /// The rest of stock's <c>Update()</c> after the expiry branch: <c>OnUpdate()</c>
        /// then every effect's <c>Update()</c>. Every stock strategy and effect leaves these
        /// empty; a mod's override still runs while an expiry is held.
        /// </summary>
        private static void RunUpdateBodyWithoutExpiry(Strategies.Strategy strategy)
        {
            try
            {
                if (OnUpdateMethod != null) OnUpdateMethod.Invoke(strategy, null);
                var effects = strategy.Effects;
                if (effects == null) return;
                for (int i = 0; i < effects.Count; i++)
                    if (effects[i] != null) effects[i].Update();
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "expiry-hold-update-threw",
                    "Strategy.Update: held strategy's OnUpdate / effect Update threw: " + ex.Message);
            }
        }

        private static bool IsCareer()
        {
            var seam = IsCareerProviderForTesting;
            if (seam != null) return seam();
            try
            {
                return ReadLiveIsCareer();
            }
            catch (Exception)
            {
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadLiveIsCareer()
        {
            return HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;
        }

        private static void RefreshAdministration()
        {
            var seam = AdministrationRefreshForTesting;
            if (seam != null)
            {
                seam();
                return;
            }
            StrategyStatePatcher.RefreshAdministration();
        }

        internal static void ResetForTesting()
        {
            IsCareerProviderForTesting = null;
            AdministrationRefreshForTesting = null;
            holdLogged.Clear();
        }
    }

    /// <summary>
    /// Prefix on <c>Strategies.Strategy.Update()</c>, the per-frame method (called by
    /// <c>StrategySystem.Update</c> for each active strategy) that holds stock's expiry:
    /// with KSPCommunityFixes' StrategyDuration fix, it calls <c>Deactivate()</c> once
    /// <c>DateActivated + LongestDuration &lt;= now</c>. KSPCF patches the duration getters,
    /// <c>CanBeDeactivated</c> and <c>SendStateMessage</c>, not <c>Update()</c>, so this
    /// prefix runs before the expiry decision and its <c>Deactivate()</c> call. When the
    /// committed timeline already switches the strategy off (a replay after a rewind),
    /// <see cref="StrategyExpiryGate"/> applies that deactivation with no capture and skips
    /// the original, so stock never records a second one. Bypassed during replay.
    /// </summary>
    [HarmonyPatch]
    internal static class StrategyUpdateExpiryPatch
    {
        private const string Tag = "StrategyReservation";

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "Strategies.Strategy.Update() not found - a replayed strategy expiry records a second deactivation");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return typeof(Strategies.Strategy).GetMethod(
                nameof(Strategies.Strategy.Update),
                BindingFlags.Instance | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);
        }

        static bool Prefix(Strategies.Strategy __instance)
        {
            try
            {
                return StrategyExpiryGate.ShouldRunStockUpdate(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "strategy-update-prefix-threw",
                    "Strategy.Update prefix threw, stock's Update runs: " + ex.Message);
                return true;
            }
        }
    }

    /// <summary>
    /// Postfix on the private <c>Strategies.StrategySystem.LoadStrategies(List&lt;ConfigNode&gt;)</c>,
    /// the method <c>StrategySystem.OnLoadRoutine</c> calls one frame after <c>OnLoad</c> to
    /// fill the strategy list. Runs a strategy state patch that found the list not yet
    /// loaded (<see cref="StrategyStatePatcher.OnStockStrategiesLoaded"/>); does nothing
    /// when none is waiting.
    /// </summary>
    [HarmonyPatch]
    internal static class StrategySystemLoadStrategiesPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StrategyReservation",
                    "StrategySystem.LoadStrategies(List<ConfigNode>) not found - a strategy patch requested at load waits for the next recalculation");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return typeof(Strategies.StrategySystem).GetMethod(
                "LoadStrategies",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(List<ConfigNode>) },
                null);
        }

        static void Postfix(Strategies.StrategySystem __instance)
        {
            try
            {
                StrategyStatePatcher.OnStockStrategiesLoaded(__instance);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("StrategyReservation", "LoadStrategies postfix threw: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Postfix on <c>Strategies.Strategy.CanBeActivated(out string reason)</c>: when the
    /// committed timeline refuses the activation, returns false with the explanation as
    /// the reason. Stock Administration then greys the list row (state "na") and prints
    /// the reason in orange above the description, and <c>Strategy.Activate()</c>, which
    /// checks <c>CanBeActivated</c> first, refuses too. A postfix, so stock's own checks
    /// (slots now, conflicts, commitment, costs) still run; ours overrides their reason
    /// only when ours applies. Bypassed during replay (<c>IsReplayingActions</c>), which
    /// also covers the state patch (it activates through <c>Strategy.Load</c> anyway).
    /// </summary>
    [HarmonyPatch]
    internal static class StrategyCanBeActivatedPatch
    {
        private const string Tag = "StrategyReservation";

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "Strategies.Strategy.CanBeActivated(out string) not found - strategy activation block will not apply");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return typeof(Strategies.Strategy).GetMethod(
                nameof(Strategies.Strategy.CanBeActivated),
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(string).MakeByRefType() },
                null);
        }

        static void Postfix(Strategies.Strategy __instance, ref bool __result, ref string reason)
        {
            try
            {
                string id = __instance?.Config?.Name;
                ReservationText text;
                if (!StrategyReservationGate.TryRefuseActivation(id, out text)) return;
                __result = false;
                reason = text.Body;
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "can-be-activated-threw",
                    "CanBeActivated postfix threw: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Postfix on <c>Administration.SetSelectedStrategy(StrategyWrapper)</c>: for an ACTIVE
    /// strategy the committed timeline changes later, disables stock's Cancel button and
    /// puts the explanation in stock's reason slot of the description. The player path
    /// only: <c>Strategy.CanBeDeactivated</c> is NOT patched, because stock
    /// <c>Strategy.Update()</c> (with KSPCommunityFixes' StrategyDuration fix) expires a
    /// strategy through <c>Deactivate()</c> gated on it, and refusing there would freeze
    /// the expiry and re-post its message every frame.
    /// </summary>
    [HarmonyPatch]
    internal static class AdministrationSetSelectedStrategyPatch
    {
        private const string Tag = "StrategyReservation";
        private static readonly MethodInfo UpdateStrategyDescriptionMethod = typeof(Administration).GetMethod(
            "UpdateStrategyDescription",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(string), typeof(string), typeof(string), typeof(string) },
            null);

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(Tag,
                    "Administration.SetSelectedStrategy(StrategyWrapper) not found - deactivation reason will not show");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return typeof(Administration).GetMethod(
                "SetSelectedStrategy",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Administration.StrategyWrapper) },
                null);
        }

        internal static MethodInfo ResolveDescriptionMethodForTesting()
        {
            return UpdateStrategyDescriptionMethod;
        }

        static void Postfix(Administration __instance, Administration.StrategyWrapper wrapper)
        {
            try
            {
                var strategy = wrapper?.strategy;
                if (__instance == null || strategy == null || !strategy.IsActive) return;
                ReservationText text;
                if (!StrategyReservationGate.TryRefuseDeactivation(strategy.Config?.Name, out text)) return;

                if (__instance.btnAcceptCancel != null)
                    __instance.btnAcceptCancel.Enable(false);
                if (UpdateStrategyDescriptionMethod != null)
                {
                    UpdateStrategyDescriptionMethod.Invoke(__instance, new object[]
                    {
                        strategy.Title, strategy.Description, strategy.Effect, text.Body
                    });
                }
                ParsekLog.VerboseRateLimited(Tag, "deactivation-marked|" + strategy.Config?.Name,
                    "Administration: Cancel disabled with reason for strategy=" + strategy.Config?.Name);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "set-selected-threw",
                    "SetSelectedStrategy postfix threw: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Prefix on the private <c>Administration.BtnInputAccept(string state)</c>, the
    /// Accept / Cancel button handler: the click backstop behind the disabled button.
    /// <c>"cancel"</c> on an active strategy refuses with the committed-action dialog when
    /// the deactivation is blocked; <c>"accept"</c> on an inactive strategy does the same
    /// for a blocked activation (stock's confirmation dialog would otherwise open and its
    /// <c>Activate()</c> fail silently).
    /// </summary>
    [HarmonyPatch]
    internal static class AdministrationButtonBackstopPatch
    {
        internal const string AcceptState = "accept";
        internal const string CancelState = "cancel";

        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn("StrategyReservation",
                    "Administration.BtnInputAccept(string) not found - strategy click backstop will not apply");
            return method;
        }

        internal static MethodBase ResolveTargetMethodForTesting()
        {
            return typeof(Administration).GetMethod(
                "BtnInputAccept",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(string) },
                null);
        }

        static bool Prefix(Administration __instance, string state)
        {
            try
            {
                var strategy = __instance?.SelectedWrapper?.strategy;
                if (strategy == null || strategy.Config == null) return true;
                return ShouldAllow(state, strategy.IsActive, strategy.Config.Name, strategy.Title);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("StrategyReservation", "button-backstop-threw",
                    "BtnInputAccept prefix threw: " + ex.Message);
                return true;
            }
        }

        /// <summary>The backstop decision for one click. True = let stock proceed.</summary>
        internal static bool ShouldAllow(string state, bool isActive, string strategyId, string title)
        {
            if (state == CancelState && isActive)
                return StrategyReservationGate.ShouldAllowDeactivationClick(strategyId, title);
            if (state == AcceptState && !isActive)
                return StrategyReservationGate.ShouldAllowActivationClick(strategyId, title);
            return true;
        }
    }
}
